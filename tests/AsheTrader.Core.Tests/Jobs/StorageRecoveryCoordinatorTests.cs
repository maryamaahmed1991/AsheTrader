using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using Newtonsoft.Json;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class StorageRecoveryCoordinatorTests
{
    private static SimulatedCharacter Sim(ulong id, Guid peer, uint bags = 0, uint capacity = 1000) => new()
    {
        Id = id, PeerId = peer, Bags = bags, BagLimit = capacity, StackSize = 999,
        Retainers = new() { [id + 1000] = 0 },
    };

    private static WorkflowService StoppedPartialDeposit()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 600), Sim(2, PeerA, capacity: 200), Sim(9, PeerB, capacity: 500))
        { DepositMovementLimit = 100 };
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.Deposit && service.DepositMovementLimit.HasValue)
            {
                service.DepositMovementLimit = null;
                result.Success = false;
                result.MovementUncertain = true;
                result.Error = "A quantity dialog was rejected after earlier confirmed deposits; no later item moved.";
            }
            return result;
        };
        service.StartJob(new() { Definition = Job([1], (2, 600)), Meeting = new() { TerritoryId = 20, AetheryteId = 30 } });
        service.RunToTerminal();
        Assert.True(service.Run!.Paused);
        Assert.Equal(100UL, service.Run.Deposited[2]);
        Assert.Equal(300UL, new TransferJobLedger(service.Run.Journal!).RelayCargo(9));
        var pending = Assert.Single(service.Run.Pending);
        Assert.True(pending.Response!.Ambiguous);
        Assert.Equal(200U, pending.Command.Operation.Quantity);
        Assert.Equal(100U, pending.Response.Observation!.MovedQuantity);
        return service;
    }

    [Fact]
    public void ReviewedConfirmedPartialDepositAllowsRemainingRelayCargoWithoutRepeatingRecordedMovement()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        var originalId = pending.Command.Id;
        var actionCount = service.Trace.Count;
        service.ResolvePendingStorage(originalId);

        Assert.True(service.Run.Paused);
        Assert.Equal("", service.LastError);
        Assert.Equal(actionCount, service.Trace.Count);
        Assert.True(pending.Response!.OperatorReviewed);
        Assert.False(pending.Response.Success);
        Assert.False(pending.Response.Ambiguous);
        Assert.False(pending.Response.Observation!.MovementUncertain);
        Assert.False(pending.Response.Observation.Success);
        Assert.Equal(100U, pending.Response.Quantity);
        Assert.Equal(100U, pending.Response.Observation.MovedQuantity);
        Assert.Equal(100UL, service.Run.Deposited[2]);

        service.Resume(); service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        var ledger = new TransferJobLedger(service.Run.Journal!);
        Assert.Equal(600UL, ledger.DeliveredTo(2));
        Assert.Equal(0UL, ledger.RelayCargo(9));
        Assert.Equal(600UL, service.Characters[2].Total);
        Assert.Equal(500UL, service.Run.Deposited[2]);
        Assert.Equal(new uint[] { 200, 200, 200 }, service.Trace.Where(command => command.Operation == GameOperationKind.Deposit).Select(command => command.Quantity));
    }

    [Fact]
    public void ChangedBagCountAndOpenMenusRefuseReviewWithoutClearingUncertainty()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        service.StorageMenusClosed = false;
        Assert.Throws<InvalidOperationException>(() => service.ResolvePendingStorage(pending.Command.Id));
        service.StorageMenusClosed = true;
        service.Characters[2].Bags++;
        var error = Assert.Throws<InvalidOperationException>(() => service.ResolvePendingStorage(pending.Command.Id));
        Assert.Contains("bag count differs", error.Message);
        Assert.True(service.Run.Paused);
        Assert.True(pending.Response!.Ambiguous);
        Assert.False(pending.Response.OperatorReviewed);
        Assert.Equal(100UL, service.Run.Deposited[2]);
    }

    [Fact]
    public void LostReviewReplyCanBeRepeatedAfterRestartWithoutDoubleAccounting()
    {
        var service = StoppedPartialDeposit();
        var commandId = service.Run!.Pending.Single().Command.Id;
        var actionCount = service.Trace.Count;
        service.SuppressInitialResponse = command => command.Id == commandId;
        service.ResolvePendingStorage(commandId);
        Assert.True(service.Run.Pending.Single().Response!.Ambiguous);
        Assert.Equal("", service.LastError);
        service.SimulateCoordinatorRestart();
        service.SuppressInitialResponse = null;
        service.ResolvePendingStorage(commandId);
        service.ResolvePendingStorage(commandId);

        Assert.True(service.Run.Paused);
        Assert.True(service.Run.Pending.Single().Response!.OperatorReviewed);
        Assert.Equal("", service.LastError);
        Assert.Equal(actionCount, service.Trace.Count);
        Assert.Equal(100UL, service.Run.Deposited[2]);
        service.Resume(); service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(500UL, service.Run.Deposited[2]);
        Assert.Equal(600UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
    }

    [Fact]
    public void LateUnreviewedResponseCannotUndoDurableOperatorReview()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        var late = JsonConvert.DeserializeObject<WorkerResponse>(JsonConvert.SerializeObject(pending.Response))!;
        service.ResolvePendingStorage(pending.Command.Id);
        typeof(WorkflowService).GetMethod("AcceptResponse", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(service, [PeerA, late]);
        Assert.True(service.Run.Pending.Single().Response!.OperatorReviewed);
        Assert.False(service.Run.Pending.Single().Response!.Ambiguous);
        Assert.True(service.Run.Paused);
        Assert.Equal(100UL, service.Run.Deposited[2]);
    }

    [Fact]
    public void MissingObservationWrongCommandAndPendingTradeRemainBlocked()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        Assert.Throws<InvalidOperationException>(() => service.ResolvePendingStorage(Guid.NewGuid()));
        service.Run.TradeOperationId = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => service.ResolvePendingStorage(pending.Command.Id));
        service.Run.TradeOperationId = null;
        pending.Response!.Observation = null;
        Assert.Throws<InvalidOperationException>(() => service.ResolvePendingStorage(pending.Command.Id));
        Assert.True(service.Run.Paused);
        Assert.True(pending.Response.Ambiguous);
    }

    [Fact]
    public void RepeatedResumeQueriesLegacyStorageWithoutRepeatingItsMovement()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        var originalResponse = pending.Response;
        var originalEvidence = JsonConvert.SerializeObject(originalResponse);
        var originalActions = service.Trace.Count;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            service.Resume();
            Assert.False(service.Run.Paused);
            Assert.Null(pending.Response);
            service.RunToTerminal();
            Assert.True(service.Run.Paused);
            Assert.Equal(originalEvidence, JsonConvert.SerializeObject(pending.Response));
            Assert.Equal(originalActions, service.Trace.Count);
            Assert.Contains("could not be tallied automatically", service.Run.Status);
            Assert.Contains("Resume / tally inventories", service.LastError);
            Assert.Equal(100UL, service.Run.Deposited[2]);
        }
        Assert.DoesNotContain(service.Diagnostics, line => line.Contains("Storage review accepted"));
    }

    [Fact]
    public void ResumeWithMissingStopObservationStillRecoversTheOriginalWorkerResult()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        var originalId = pending.Command.Id;
        var originalActions = service.Trace.Count;
        pending.Response!.Observation = null;
        service.Resume();
        Assert.False(service.Run.Paused);
        Assert.Equal(originalId, service.Run.Pending.Single().Command.Id);
        Assert.Null(service.Run.Pending.Single().Response);
        service.RunToTerminal();

        Assert.True(service.Run.Paused);
        Assert.Equal(originalId, service.Run.Pending.Single().Command.Id);
        Assert.NotNull(service.Run.Pending.Single().Response!.Observation);
        Assert.Equal(originalActions, service.Trace.Count);
        Assert.Equal(100UL, service.Run.Deposited[2]);
        Assert.Contains("could not be tallied automatically", service.LastError);
    }

    [Fact]
    public void EndJobExplainsAutomaticStorageTallyBeforeTheRemainingRelayDelivery()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        var originalEvidence = JsonConvert.SerializeObject(pending.Response);
        var first = Assert.Throws<InvalidOperationException>(service.EndPausedJob);
        Assert.Contains("Resume / tally inventories", first.Message);
        Assert.Contains("deliver any remaining relay cargo", first.Message);
        Assert.Equal(first.Message, service.Run.Status);
        Assert.Equal(originalEvidence, JsonConvert.SerializeObject(pending.Response));
        service.ResolvePendingStorage(pending.Command.Id);
        Assert.Equal("", service.LastError);
        Assert.Contains(service.Diagnostics, line => line.Contains("Storage review requested"));
        Assert.Contains(service.Diagnostics, line => line.Contains("Storage review accepted"));
        var second = Assert.Throws<InvalidOperationException>(service.EndPausedJob);
        Assert.Contains("relay still holds undelivered cargo", second.Message);
        Assert.True(service.Run.Paused);
        Assert.NotEqual(CoordinatorStep.Finished, service.Run.Step);
        Assert.Equal(100UL, service.Run.Deposited[2]);
        service.Resume(); service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(600UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
    }

    [Fact]
    public void AutomaticPartialTallyMigratesLegacyAppliedFlagAndAddsOnlyTheNewMovement()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        pending.AppliedStorageQuantity = null; // Saved before cumulative accounting was introduced.
        Assert.True(pending.InventoryAdjustmentApplied);
        var actionCount = service.Trace.Count;
        var recovered = CompleteInterruptedDeposit(service, pending, 50);
        DeliverSavedResponse(service, pending.PeerId, recovered);
        DeliverSavedResponse(service, pending.PeerId, recovered);

        Assert.Equal(100U, pending.AppliedStorageQuantity);
        Assert.Equal(100UL, service.Run.Deposited[2]);
        service.Resume();
        Assert.Equal(150UL, service.Run.Deposited[2]);
        Assert.Equal(150U, pending.AppliedStorageQuantity);
        Assert.Equal(actionCount, service.Trace.Count);
        Assert.Empty(service.Run.Pending);
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(600UL, service.Characters[2].Total);
        Assert.Equal((ulong)service.Characters[2].RetainerTotal, service.Run.Deposited[2]);
        Assert.Equal(0UL, new TransferJobLedger(service.Run.Journal!).RelayCargo(9));
    }

    [Fact]
    public void LostAutomaticTallyReplySurvivesCoordinatorRestartWithoutDoubleAccounting()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        pending.AppliedStorageQuantity = null;
        service.PauseForTest(); // Persist the legacy boolean and its original 100-item observation.
        var actionCount = service.Trace.Count;
        service.SuppressInitialResponse = command => command.Id == pending.Command.Id;
        var recovered = CompleteInterruptedDeposit(service, pending, 50);
        DeliverSavedResponse(service, pending.PeerId, recovered);
        Assert.True(pending.Response!.Ambiguous);

        service.SimulateCoordinatorRestart();
        service.SuppressInitialResponse = null;
        service.Resume();
        service.RunUntil(run => run.Deposited.GetValueOrDefault(2UL) == 150);
        Assert.Equal(actionCount, service.Trace.Count);
        Assert.Equal(150UL, service.Run!.Deposited[2]);
        service.PauseForTest();
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(600UL, service.Characters[2].Total);
        Assert.Equal((ulong)service.Characters[2].RetainerTotal, service.Run.Deposited[2]);
        Assert.Equal(600UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
    }

    private static WorkerResponse CompleteInterruptedDeposit(WorkflowService service, PendingCommand pending, uint additional)
    {
        var character = service.Characters[pending.Command.Operation.CharacterId];
        character.Bags -= additional;
        character.Retainers[character.Retainers.Keys.First()] += additional;
        var recovered = JsonConvert.DeserializeObject<WorkerResponse>(JsonConvert.SerializeObject(pending.Response))!;
        recovered.Success = false;
        recovered.Ambiguous = false;
        recovered.AutoReconciled = true;
        recovered.OperatorReviewed = false;
        recovered.Observation!.MovementUncertain = false;
        recovered.Observation.MovedQuantity += additional;
        recovered.Observation.BagQuantity = character.Bags;
        recovered.Observation.BagCapacity = character.BagSpace;
        recovered.Quantity = recovered.Observation.MovedQuantity;
        recovered.CurrentQuantity = character.Bags;
        recovered.Error = recovered.Observation.Error = "Storage tallied automatically from its saved checkpoint.";
        return recovered;
    }

    private static void DeliverSavedResponse(WorkflowService service, Guid peerId, WorkerResponse response)
        => typeof(WorkflowService).GetMethod("Deliver", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(service, [peerId, response]);
}
