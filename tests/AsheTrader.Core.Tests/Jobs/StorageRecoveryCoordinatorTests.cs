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
    public void RepeatedResumeRetainsKnownStorageEvidenceAndGuidesReviewWithoutResending()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        var originalResponse = pending.Response;
        var originalEvidence = JsonConvert.SerializeObject(originalResponse);
        var originalActions = service.Trace.Count;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            service.Resume();
            Assert.True(service.Run.Paused);
            Assert.Same(originalResponse, pending.Response);
            Assert.Equal(originalEvidence, JsonConvert.SerializeObject(pending.Response));
            Assert.Equal(originalActions, service.Trace.Count);
            Assert.Contains("Review interrupted storage", service.Run.Status);
            Assert.Contains("Accept recorded storage count", service.LastError);
            Assert.Equal(100UL, service.Run.Deposited[2]);
        }
        Assert.Contains(service.Diagnostics, line => line.Contains("Storage review required before resume"));
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
        Assert.Contains("Review interrupted storage", service.LastError);
    }

    [Fact]
    public void EndJobExplainsStorageReviewBeforeTheRemainingRelayDelivery()
    {
        var service = StoppedPartialDeposit();
        var pending = service.Run!.Pending.Single();
        var originalEvidence = JsonConvert.SerializeObject(pending.Response);
        var first = Assert.Throws<InvalidOperationException>(service.EndPausedJob);
        Assert.Contains("Review interrupted storage first", first.Message);
        Assert.Contains("finish any relay deliveries", first.Message);
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
}
