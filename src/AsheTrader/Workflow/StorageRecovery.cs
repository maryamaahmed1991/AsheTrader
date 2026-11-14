using AsheTrader.Workflow.Native;
using Dalamud.Game.ClientState.Conditions;
using Newtonsoft.Json;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private sealed class StorageRecoveryProbe
    {
        public StorageRecoveryStability Stability { get; } = new();
        public long StartedAt { get; } = Environment.TickCount64;
        public uint? RecoveredQuantity { get; set; }
        public uint VerifiedBagQuantity { get; set; }
        public uint? VerifiedRetainerQuantity { get; set; }
        public bool UsedRetainerObservation { get; set; }
    }

    private readonly Dictionary<Guid, StorageRecoveryProbe> storageRecoveryProbes = [];

    /// <summary>Queries only; false means another fresh observation/owned-menu close is still pending.</summary>
    private bool TryRecoverStorage(WorkerRecord record, out WorkerResponse response)
    {
        response = null!;
        var command = record.Command;
        if (!StorageReconciliation.IsStorage(command)) return false;
        if (record.Response is { AutoReconciled: true }) { response = record.Response; return true; }
        try
        {
            if (activeWorker != null || executor.IsBusy || plugin.Trader.IsRunning || (plugin.Fuel.Active || plugin.FuelQueue.HasUnfinishedRun)) return false;
            if (!Plugin.ClientState.IsLoggedIn || Plugin.PlayerState.ContentId != command.Operation.CharacterId)
                throw new InvalidOperationException("Log in the original storage character, then resume to tally its current inventory automatically.");
            if (Plugin.ObjectTable.LocalPlayer == null || NativeRetainers.HasActiveCutscene
                || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            { storageRecoveryProbes.Remove(command.Id); return false; }
            var ar = plugin.AutoRetainer.GetStatus();
            if (ar.Ready && (ar.Busy != false || ar.MultiModeEnabled != false))
                throw new InvalidOperationException("Pause AutoRetainer multi-mode and wait for its current task to finish, then resume the automatic storage tally.");
            var ls = plugin.Lifestream.GetStatus();
            if (ls.Ready && ls.Busy != false)
                throw new InvalidOperationException("Wait for Lifestream to finish, then resume the automatic storage tally.");
            var checkpoint = record.StorageCheckpoint ?? record.Response?.Observation?.StorageCheckpoint
                ?? throw new InvalidOperationException("This older storage operation has no saved inventory baseline from before the interrupted movement. Its remaining quantity cannot be determined automatically; preserve diagnostics rather than guessing or repeating the movement.");
            // Validate ownership and quantity bounds before even closing an owned progress window.
            StorageRecoveryTally.Reconcile(command.Operation, checkpoint, Plugin.PlayerState.ContentId,
                checkpoint!.BagBeforeQuantity, checkpoint.RetainerBeforeQuantity);
            if (!storageRecoveryProbes.TryGetValue(command.Id, out var probe))
            {
                storageRecoveryProbes[command.Id] = probe = new();
                RecordDiagnostic($"Storage automatic tally started; command={command.Id}; character={command.Operation.CharacterId}; checkpointRetainer={checkpoint!.RetainerId}; attempted={checkpoint.AttemptedQuantity}; confirmedBefore={checkpoint.ConfirmedMovedBefore}");
            }
            if (Environment.TickCount64 - probe.StartedAt > 20_000)
                throw new InvalidOperationException("The storage inventory or its owned retainer window did not settle within 20 seconds. Close the remaining retainer windows, then resume the automatic tally.");
            // Includes visible-but-not-ready quantity/confirmation windows and raw trade state.
            if (!plugin.Trader.IsInventoryTallyReady || GameWorkflowExecutor.HasVisibleStorageProgress())
            {
                probe.Stability.Reset(); probe.RecoveredQuantity = null;
                if (plugin.Trader.IsInventoryTallyReady && GameWorkflowExecutor.HasVisibleStorageProgress())
                    NativeRetainers.TryCloseTransferProgress(checkpoint.RetainerId, RecordDiagnostic);
                return false;
            }
            var bags = NativeRetainers.ReadBags(command.Operation.ItemId, command.Operation.HighQuality);
            uint? retainerQuantity = NativeRetainers.HasVisibleRetainerInventory
                ? NativeRetainers.ReadRetainer(checkpoint.RetainerId, command.Operation.ItemId, command.Operation.HighQuality).Quantity : null;
            if (!probe.RecoveredQuantity.HasValue)
            {
                if (!probe.Stability.Observe(Environment.TickCount64, bags.Quantity, retainerQuantity)) return false;
                var recovered = StorageRecoveryTally.Reconcile(command.Operation, checkpoint, Plugin.PlayerState.ContentId, bags.Quantity, retainerQuantity);
                if (record.Response?.Observation is { } earlier && recovered < earlier.MovedQuantity)
                    throw new InvalidOperationException("The current storage tally is smaller than movement already recorded as confirmed. No progress was removed or repeated; preserve diagnostics.");
                probe.RecoveredQuantity = recovered;
                probe.VerifiedBagQuantity = bags.Quantity;
                probe.VerifiedRetainerQuantity = retainerQuantity;
                probe.UsedRetainerObservation = retainerQuantity.HasValue;
            }
            else if (bags.Quantity != probe.VerifiedBagQuantity)
                throw new InvalidOperationException("The bag count changed while completing the automatic storage tally. No movement was repeated; stop other item actions, then resume.");
            else if (retainerQuantity.HasValue)
            {
                if (probe.VerifiedRetainerQuantity.HasValue && retainerQuantity != probe.VerifiedRetainerQuantity
                    || StorageRecoveryTally.Reconcile(command.Operation, checkpoint, Plugin.PlayerState.ContentId, bags.Quantity, retainerQuantity) != probe.RecoveredQuantity)
                    throw new InvalidOperationException("The retainer count changed while completing the automatic storage tally. No movement was repeated; wait for inventory to settle, then resume.");
                probe.VerifiedRetainerQuantity = retainerQuantity;
                probe.UsedRetainerObservation = true;
            }
            if (!GameWorkflowExecutor.TryCloseRecoveredStorage(checkpoint!, RecordDiagnostic)) return false;
            var moved = probe.RecoveredQuantity.Value;
            var message = $"Storage tallied automatically: {moved:N0} of {command.Operation.Quantity:N0} requested items moved. Remaining work will be calculated from this count.";
            var observation = new GameOperationResult
            {
                Id = command.Id, CharacterId = command.Operation.CharacterId, Success = moved == command.Operation.Quantity,
                Error = message, MovedQuantity = moved, MovementUncertain = false, BagQuantity = bags.Quantity,
                BagCapacity = bags.Capacity, StackSize = bags.StackSize, ObservedAt = DateTimeOffset.UtcNow,
                CurrentWorldId = Plugin.ObjectTable.LocalPlayer?.CurrentWorld.RowId ?? 0, StorageCheckpoint = checkpoint,
            };
            response = new()
            {
                CommandId = command.Id, Success = observation.Success, AutoReconciled = true, Ambiguous = false,
                Quantity = moved, BeforeQuantity = checkpoint!.BagBeforeQuantity, CurrentQuantity = bags.Quantity,
                Unchanged = moved == 0, Observation = observation, Error = message,
            };
            record.Response = response;
            SaveWorkers(); // Persist the tally before replying; future queries replay it exactly.
            storageRecoveryProbes.Remove(command.Id);
            RecordDiagnostic($"Storage automatic tally saved; command={command.Id}; moved={moved}; requested={command.Operation.Quantity}; bags={bags.Quantity}; pairedRetainerObservation={probe.UsedRetainerObservation}");
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            storageRecoveryProbes.Remove(command.Id);
            response = record.Response == null ? new() { CommandId = command.Id }
                : JsonConvert.DeserializeObject<WorkerResponse>(JsonConvert.SerializeObject(record.Response))!;
            response.Success = false; response.Ambiguous = true; response.AutoReconciled = false;
            response.Error = exception.Message;
            RecordDiagnostic($"Storage automatic tally blocked; command={command.Id}; reason={exception.Message}");
            return true;
        }
    }
}
