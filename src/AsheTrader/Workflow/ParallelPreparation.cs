using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private long preparationRecoveryDeadline;

    private void BeginPreparation()
    {
        var run = Run!;
        if (run.Pending.Count != 0 || run.TradeOperationId.HasValue)
            throw new InvalidOperationException("Resolve pending work before preparing another delivery.");
        var allocation = run.Journal!.Plan.Allocations.Single(x => x.Id == run.AllocationId);
        var leg = allocation.Legs[run.LegIndex];
        if (leg.SenderPeerId == leg.ReceiverPeerId)
            throw new InvalidDataException("A trade requires two different PCs.");
        PinLegMeeting(allocation, run.LegIndex);
        run.Preparation = new()
        {
            Sender = new() { CharacterId = leg.SenderId, PeerId = leg.SenderPeerId },
            Receiver = new() { CharacterId = leg.ReceiverId, PeerId = leg.ReceiverPeerId },
        };
        run.PreviousItemDepositIndex = null;
        run.PreviousItemDepositCharacterId = 0;
        SetStep(CoordinatorStep.PrepareParticipants);
    }

    private void ResumePreparation()
    {
        var run = Run!;
        var preparation = run.Preparation ?? throw new InvalidDataException("The saved preparation state is missing.");
        // Stop/reconnect may leave two independent operations to reconcile. Keep their IDs,
        // quantities and applied storage counters until both workers have durable outcomes.
        preparation.Recovering = true;
        foreach (var pending in run.Pending)
        {
            RememberAppliedStorageQuantity(pending);
            if (pending.Response is { Success: true } or { AutoReconciled: true } or { NeverStarted: true }
                || StorageReconciliation.IsReviewed(pending.Response)) continue;
            if (StorageReconciliation.IsStorage(pending.Command)) pending.StorageRecoveryRequested = true;
            pending.Response = null;
        }
        preparationRecoveryDeadline = Environment.TickCount64 + 30_000;
        resendAt = 0;
        run.Paused = false;
        run.Status = "Recovering both PCs' saved preparation and tallying any interrupted retainer movements…";
        SaveRun();
    }

    private void AdvancePreparation(TransferJobLedger ledger, TransferAllocation allocation, TransferLeg leg)
    {
        var run = Run!;
        var preparation = run.Preparation ?? throw new InvalidDataException("The saved preparation state is missing.");
        if (preparation.Sender.CharacterId != leg.SenderId || preparation.Sender.PeerId != leg.SenderPeerId
            || preparation.Receiver.CharacterId != leg.ReceiverId || preparation.Receiver.PeerId != leg.ReceiverPeerId
            || leg.SenderPeerId == leg.ReceiverPeerId || !run.MeetingPinned || run.EffectiveMeeting == null)
            throw new InvalidDataException("The saved preparation does not match its pinned trade route.");
        var lanes = new[] { preparation.Sender, preparation.Receiver };
        if (run.Pending.Count > 2 || run.Pending.Select(x => x.PeerId).Distinct().Count() != run.Pending.Count
            || run.Pending.Any(pending => pending.Command.Kind != WorkerCommandKind.Game
                || !lanes.Any(lane => lane.PeerId == pending.PeerId && lane.CharacterId == pending.Command.Operation.CharacterId)))
            throw new InvalidDataException("Each PC must have at most one preparation command for its assigned character.");
        if (preparation.Recovering)
        {
            RecoverPreparation();
            return;
        }

        // Consume every available result before issuing more work, so a failure stops both
        // sides without starting another operation on the side that just finished.
        foreach (var lane in lanes)
        {
            var pending = run.Pending.SingleOrDefault(x => x.PeerId == lane.PeerId);
            if (pending?.Response == null) continue;
            if (pending.Command.Operation.Kind != PreparationOperationKind(lane.Stage)
                || pending.InventoryItemIndex != lane.InventoryItemIndex)
                throw new InvalidDataException("The pending command does not match its participant's preparation stage.");
            if (!ResolvePreparationStorage(pending)) continue;
            var response = pending.Response!;
            if (!response.Success && !response.AutoReconciled)
                throw new InvalidOperationException($"{Character(lane.CharacterId).Character.Name}: {response.Error}");
            var result = response.Observation ?? throw new InvalidDataException("Worker omitted its preparation observation.");
            if (result.CharacterId != lane.CharacterId || result.Id != pending.Command.Operation.Id)
                throw new InvalidDataException("Worker observed a different preparation operation or character.");
            if (result.CurrentWorldId != 0) run.ObservedWorlds[lane.CharacterId] = result.CurrentWorldId;
            if (response.AutoReconciled && !response.Success)
            {
                // A partially completed storage command is already accounted. Inspect its
                // resulting bags before calculating the next pickup/deposit, never replay it.
                lane.Stage = ParticipantPreparationStage.Inspect;
                lane.InventoryItemIndex = null;
            }
            else ApplyPreparationResult(lane, result, ledger, allocation, leg);
            run.Pending.Remove(pending);
            SaveRun();
        }

        // A full relay may need to switch to delivery. Drain already-issued work first;
        // changing character or destination while the other PC has a pickup is forbidden.
        if (preparation.ReplanRequested)
        {
            if (run.Pending.Count == 0)
            {
                run.Preparation = null;
                SetStep(CoordinatorStep.SelectLeg);
            }
            else SetPreparationStatus();
            return;
        }
        if (lanes.All(lane => lane.Stage == ParticipantPreparationStage.Ready))
        {
            if (run.Pending.Count != 0) throw new InvalidDataException("A prepared participant still has pending work.");
            run.DesiredQuantity = (uint)Math.Min(ledger.RemainingForLeg(run.AllocationId, run.LegIndex),
                Math.Min(run.DesiredQuantity, Math.Min(run.SenderBagQuantity,
                    Math.Min(run.ReceiverCapacity, checked(run.SenderStackSize * 5)))));
            if (run.DesiredQuantity == 0) throw new InvalidOperationException("No items fit the prepared trade. Resume to refresh the participants.");
            RecordDiagnostic($"Both participants ready; sender={leg.SenderId}; receiver={leg.ReceiverId}; world={run.EffectiveMeeting.WorldId}; quantity={run.DesiredQuantity}");
            run.Preparation = null;
            SetStep(CoordinatorStep.PreviewBatch);
            return;
        }
        foreach (var lane in lanes)
        {
            if (run.Paused) return;
            if (lane.Stage == ParticipantPreparationStage.Ready || run.Pending.Any(x => x.PeerId == lane.PeerId)) continue;
            var kind = PreparationOperationKind(lane.Stage);
            var definition = lane.InventoryItemIndex is { } itemIndex
                ? run.CompletedItems.Single(item => item.ItemIndex == itemIndex).Definition : null;
            var quantity = kind is GameOperationKind.Withdraw or GameOperationKind.Deposit ? lane.StorageQuantity : 0;
            var command = Command(lane.CharacterId, kind, quantity,
                kind == GameOperationKind.TravelMeeting ? run.EffectiveMeeting : null, item: definition);
            Issue(lane.PeerId, command, inventoryItemIndex: lane.InventoryItemIndex);
            if (run.Pending.Any(pending => pending.Response is { Success: false })) break;
        }
        SetPreparationStatus();
    }

    private bool ResolvePreparationStorage(PendingCommand pending)
    {
        if (!StorageReconciliation.IsStorage(pending.Command)) return true;
        AccountStorageMovement(pending, pending.Command.Operation.CharacterId);
        if (pending.Response?.Ambiguous != true) return true;
        if (pending.StorageRecoveryRequested) throw new InvalidOperationException(StorageReviewMessage(pending));
        pending.StorageRecoveryRequested = true;
        pending.Response = null;
        resendAt = 0;
        SaveRun();
        return false;
    }

    private void RecoverPreparation()
    {
        var run = Run!;
        foreach (var pending in run.Pending.ToArray())
        {
            if (pending.Response == null || !ResolvePreparationStorage(pending)) continue;
            if (StorageReconciliation.IsStorage(pending.Command) && !pending.Response!.NeverStarted
                && pending.Response.Observation == null)
                throw new InvalidOperationException("The interrupted retainer operation has no inventory observation. Keep its original character logged in and resume tallying.");
            // Login/inspect/travel cannot move items. A stopped result is enough to retire
            // their old command; each participant will be logged in and prepared afresh.
            run.Pending.Remove(pending);
            SaveRun();
        }
        if (run.Pending.Count != 0)
        {
            if (Environment.TickCount64 >= preparationRecoveryDeadline)
                throw new TimeoutException("Still waiting for a PC to finish stopping or tally its retainer movement. Keep both PCs connected with the original characters, then resume.");
            return;
        }
        run.Preparation = null;
        SetStep(CoordinatorStep.SelectLeg);
    }

    private void ApplyPreparationResult(ParticipantPreparation lane, GameOperationResult result,
        TransferJobLedger ledger, TransferAllocation allocation, TransferLeg leg)
    {
        var run = Run!;
        var sender = lane.CharacterId == leg.SenderId;
        switch (lane.Stage)
        {
            case ParticipantPreparationStage.Login:
                lane.Stage = ParticipantPreparationStage.Inspect;
                break;
            case ParticipantPreparationStage.Inspect:
                lane.InventoryItemIndex = null;
                ObserveRelayCapacity(lane.CharacterId, result.BagCapacity);
                if (sender)
                {
                    ApplySenderPreparationInventory(result);
                    if (result.BagQuantity != 0) { lane.Stage = ParticipantPreparationStage.Travel; break; }
                    if (run.LegIndex != 0)
                        throw new InvalidOperationException("The relay is missing its carried job items. Restore that cargo to its bags before resuming; unrelated retainer stock will not be collected.");
                    if (result.BagCapacity == 0)
                    {
                        if (PrepareEarlierItemStorage(lane)) break;
                        throw new InvalidOperationException("The provider has no room to collect its next load. Free bag space, then resume; confirmed deliveries are saved.");
                    }
                    lane.StorageQuantity = (uint)Math.Min(ledger.RemainingForLeg(run.AllocationId, run.LegIndex),
                        Math.Min(run.DesiredQuantity, result.BagCapacity));
                    if (lane.StorageQuantity == 0) throw new InvalidOperationException("No funded provider pickup remains.");
                    lane.Stage = ParticipantPreparationStage.Withdraw;
                    break;
                }
                run.ReceiverCapacity = result.BagCapacity;
                if (result.BagCapacity != 0) { lane.Stage = ParticipantPreparationStage.Travel; break; }
                if (allocation.Legs.Count == 2 && run.LegIndex == 0)
                {
                    if (ledger.RelayCargo(lane.CharacterId) == 0)
                        throw new InvalidOperationException("The relay is full but has no carried job cargo to deliver. Free space on that relay, then resume.");
                    run.RelayFlushCharacterId = lane.CharacterId;
                    run.Preparation!.ReplanRequested = true;
                    break;
                }
                var allowance = DepositAllowance(run.Journal!, run.Deposited, lane.CharacterId);
                if (result.BagQuantity == 0 || allowance == 0)
                {
                    if (PrepareEarlierItemStorage(lane)) break;
                    throw new InvalidOperationException("Receiver is full and has no received job items available to deposit. Free some space before resuming.");
                }
                lane.StorageQuantity = (uint)Math.Min(result.BagQuantity, allowance);
                lane.Stage = ParticipantPreparationStage.Deposit;
                break;
            case ParticipantPreparationStage.Withdraw:
                if (result.BagQuantity == 0 || result.MovedQuantity == 0)
                    throw new InvalidOperationException("No provider items could be collected. The planned stock may be stale or unavailable. Check its retainer stock and bag space before resuming; confirmed deliveries are preserved.");
                ApplySenderPreparationInventory(result);
                ObserveRelayCapacity(lane.CharacterId, result.BagCapacity);
                lane.Stage = ParticipantPreparationStage.Travel;
                break;
            case ParticipantPreparationStage.Deposit:
                if (result.MovedQuantity == 0)
                    throw new InvalidOperationException("No received job items could be deposited. Check that permitted retainers have room, then resume.");
                if (!lane.InventoryItemIndex.HasValue && result.BagCapacity == 0)
                    throw new InvalidOperationException("All permitted retainers are full or inaccessible. The job remains paused.");
                lane.InventoryItemIndex = null;
                lane.Stage = ParticipantPreparationStage.Inspect;
                break;
            case ParticipantPreparationStage.Travel:
                if (result.CurrentWorldId != run.EffectiveMeeting!.WorldId)
                    throw new InvalidDataException("A participant arrived on a different world than the pinned meeting point.");
                ObserveRelayCapacity(lane.CharacterId, result.BagCapacity);
                if (sender) ApplySenderPreparationInventory(result);
                else run.ReceiverCapacity = result.BagCapacity;
                lane.Stage = (sender ? result.BagQuantity : result.BagCapacity) == 0
                    ? ParticipantPreparationStage.Inspect : ParticipantPreparationStage.Ready;
                break;
            default: throw new InvalidDataException("Unexpected preparation result.");
        }
        RecordDiagnostic($"Preparation advanced; character={lane.CharacterId}; peer={lane.PeerId}; stage={lane.Stage}; storageQuantity={lane.StorageQuantity}; storageItem={lane.InventoryItemIndex?.ToString() ?? "active"}");
    }

    private void ApplySenderPreparationInventory(GameOperationResult result)
    {
        if (result.StackSize == 0) throw new InvalidDataException("Invalid item stack size.");
        Run!.SenderBagQuantity = result.BagQuantity;
        Run.SenderBagCapacity = result.BagCapacity;
        Run.SenderStackSize = result.StackSize;
    }

    private bool PrepareEarlierItemStorage(ParticipantPreparation lane)
    {
        foreach (var item in Run!.CompletedItems)
        {
            if (item.Journal == null) continue;
            var allowance = DepositAllowance(item.Journal, item.Deposited, lane.CharacterId);
            if (allowance == 0) continue;
            lane.InventoryItemIndex = item.ItemIndex;
            lane.StorageQuantity = (uint)Math.Min(uint.MaxValue, allowance);
            lane.Stage = ParticipantPreparationStage.Deposit;
            return true;
        }
        return false;
    }

    private void SetPreparationStatus()
    {
        var run = Run!;
        if (run.Paused) return;
        var preparation = run.Preparation!;
        var status = preparation.ReplanRequested ? "Finishing pending preparation before the relay delivers its cargo…"
            : $"{Character(preparation.Sender.CharacterId).Character.Name}: {PreparationStageLabel(preparation.Sender.Stage)} · "
                + $"{Character(preparation.Receiver.CharacterId).Character.Name}: {PreparationStageLabel(preparation.Receiver.Stage)}";
        if (run.TotalItemCount > 1) status = $"Item {run.ActiveItemIndex + 1}/{run.TotalItemCount}: {status}";
        if (status == run.Status) return;
        run.Status = status;
        SaveRun();
    }

    internal static string PreparationStageLabel(ParticipantPreparationStage stage) => stage switch
    {
        ParticipantPreparationStage.Login => "logging in",
        ParticipantPreparationStage.Inspect => "checking carried items",
        ParticipantPreparationStage.Withdraw => "collecting from retainers",
        ParticipantPreparationStage.Deposit => "depositing into retainers",
        ParticipantPreparationStage.Travel => "travelling to the meeting point",
        ParticipantPreparationStage.Ready => "ready at the meeting point",
        _ => "waiting",
    };

    private static GameOperationKind PreparationOperationKind(ParticipantPreparationStage stage) => stage switch
    {
        ParticipantPreparationStage.Login => GameOperationKind.Login,
        ParticipantPreparationStage.Inspect => GameOperationKind.Inspect,
        ParticipantPreparationStage.Withdraw => GameOperationKind.Withdraw,
        ParticipantPreparationStage.Deposit => GameOperationKind.Deposit,
        ParticipantPreparationStage.Travel => GameOperationKind.TravelMeeting,
        _ => throw new InvalidDataException("Invalid participant preparation stage."),
    };
}
