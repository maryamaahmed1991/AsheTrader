using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class TradeFailureCoordinatorTests
{
    private const string Failure = "No verified progress within the timeout. The operation was not retried.";

    [Theory]
    [InlineData(WorkerCommandKind.SendBatch, false)]
    [InlineData(WorkerCommandKind.SendBatch, true)]
    [InlineData(WorkerCommandKind.ReceiveBatch, false)]
    [InlineData(WorkerCommandKind.ReceiveBatch, true)]
    public void FailedSideStopsBothThenAutomaticallyTalliesAndRetriesOnlyProvenEmptyBatch(WorkerCommandKind failedSide, bool sourceRemote)
    {
        var service = Create(sourceRemote);
        service.DeferTradeCompletion = true;
        service.StartJob(Job());
        service.RunUntil(run => run.Step == CoordinatorStep.AwaitTrade && run.Pending.Count == 2);
        var operationId = service.Run!.TradeOperationId!.Value;
        var commandIds = service.Run.Pending.Select(pending => pending.Command.Id).ToArray();
        service.FailTradeForTest(failedSide, Failure);
        Assert.False(service.Run.Paused);
        Assert.NotNull(service.Run.TradeRecovery);
        Assert.All(commandIds, id => Assert.Contains(id, service.StoppedTradeCommands));
        Assert.Empty(service.Trades);
        Assert.Equal(100U, service.Characters[1].Bags);
        Assert.Equal(0U, service.Characters[2].Bags);
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.DeferTradeCompletion = false;
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(TransferOperationState.Cancelled, service.Run.Journal!.Operations.Single(operation => operation.Id == operationId).State);
        Assert.Single(service.Trades);
        Assert.Equal(100UL, new TransferJobLedger(service.Run.Journal).DeliveredTo(2));
        Assert.Equal(0U, service.Characters[1].Bags);
        Assert.Equal(100U, service.Characters[2].Bags);
        Assert.Contains(service.Diagnostics, line => line.Contains(Failure, StringComparison.Ordinal));
    }

    [Fact]
    public void ReceiverFailureAfterReadyNeverDispatchesOldSender()
    {
        var service = Create();
        service.StartJob(Job());
        service.RunUntil(run => run.Step == CoordinatorStep.SendBatch && run.Pending.Count == 1 && run.Pending[0].Ready);
        var interruptedOperation = service.Run!.TradeOperationId;
        service.FailTradeForTest(WorkerCommandKind.ReceiveBatch, "Receiver stopped before the sender was dispatched.");
        Assert.NotNull(service.Run.TradeRecovery);
        Assert.Single(service.Run.Pending);
        Assert.DoesNotContain(service.Trace, trace => trace.Kind == WorkerCommandKind.SendBatch);
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(TransferOperationState.Cancelled, service.Run.Journal!.Operations.Single(operation => operation.Id == interruptedOperation).State);
        Assert.Single(service.Trades);
        Assert.Single(service.Trace, trace => trace.Kind == WorkerCommandKind.SendBatch);
    }

    internal static WorkflowService Create(bool sourceRemote = false) => new(PeerA, PeerB,
        new SimulatedCharacter { Id = 1, PeerId = sourceRemote ? PeerB : PeerA, Bags = 100, Retainers = new() { [1001] = 0 } },
        new SimulatedCharacter { Id = 2, PeerId = sourceRemote ? PeerA : PeerB, Retainers = new() { [1002] = 0 } });

    internal static SavedTransferJob Job() => new()
    {
        Definition = JobPlannerTests.Job([1], (2, 100)),
        Meeting = new() { TerritoryId = 20, AetheryteId = 30, X = 1, Y = 2, Z = 3 },
    };
}
