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
    public void OneFailedSideImmediatelyStopsBothAndOnlyExplicitReviewAllowsAnotherTrade(WorkerCommandKind failedSide, bool sourceRemote)
    {
        var service = Create(sourceRemote);
        service.DeferTradeCompletion = true;
        service.StartJob(Job());
        service.RunUntil(run => run.Step == CoordinatorStep.AwaitTrade && run.Pending.Count == 2);
        var operationId = service.Run!.TradeOperationId!.Value;
        var commandIds = service.Run.Pending.Select(pending => pending.Command.Id).ToArray();

        service.FailTradeForTest(failedSide, Failure);

        Assert.True(service.Run.Paused); // No later receiver timeout or extra coordinator tick is needed.
        Assert.Contains(Failure, service.LastError);
        Assert.Contains("No trade will be repeated automatically", service.LastError);
        Assert.All(commandIds, id => Assert.Contains(id, service.StoppedTradeCommands));
        Assert.Null(service.Run.Pending.Single(pending => pending.Command.Kind != failedSide).Response);
        Assert.Empty(service.Trades);
        Assert.Equal(100U, service.Characters[1].Bags);
        Assert.Equal(0U, service.Characters[2].Bags);
        Assert.Equal(0UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            service.SimulateCoordinatorRestart();
            service.Resume();
            service.RunToTerminal();
            Assert.True(service.Run!.Paused);
            Assert.Equal(commandIds, service.Run.Pending.Select(pending => pending.Command.Id));
            Assert.Equal(Failure, service.Run.Pending.Single(pending => pending.Command.Kind == failedSide).Response!.Error);
            Assert.Contains(Failure, service.LastError);
            Assert.All(service.Run.Pending, pending =>
            {
                Assert.True(pending.Response!.Ambiguous);
                Assert.True(pending.Response.Unchanged);
                Assert.False(pending.Response.Success);
                Assert.False(pending.Response.NeverStarted);
                Assert.False(pending.Response.OperatorReviewed);
                Assert.Equal(pending.Response.BeforeQuantity, pending.Response.CurrentQuantity);
            });
            Assert.Equal(2, service.Trace.Count(trace => trace.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch));
            Assert.Empty(service.Trades);
        }

        service.DeferTradeCompletion = false;
        service.ResolvePendingTrade(false);
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(TransferOperationState.Cancelled, service.Run.Journal!.Operations.Single(operation => operation.Id == operationId).State);
        Assert.Single(service.Trades);
        Assert.Equal(100UL, new TransferJobLedger(service.Run.Journal).DeliveredTo(2));
        Assert.Equal(0U, service.Characters[1].Bags);
        Assert.Equal(100U, service.Characters[2].Bags);
    }

    [Fact]
    public void ReceiverFailureAfterReadyPreventsSenderDispatch()
    {
        var service = Create(false);
        service.StartJob(Job());
        service.RunUntil(run => run.Step == CoordinatorStep.SendBatch && run.Pending.Count == 1 && run.Pending[0].Ready);

        service.FailTradeForTest(WorkerCommandKind.ReceiveBatch, "Receiver stopped before the sender was dispatched.");
        service.Tick();

        Assert.True(service.Run!.Paused);
        Assert.Single(service.Run.Pending);
        Assert.DoesNotContain(service.Trace, trace => trace.Kind == WorkerCommandKind.SendBatch);
        Assert.Empty(service.Trades);
        Assert.Equal(TransferOperationState.Prepared, Assert.Single(service.Run.Journal!.Operations).State);

        service.Resume();
        service.RunToTerminal();
        Assert.True(service.Run.Paused);
        Assert.DoesNotContain(service.Trace, trace => trace.Kind == WorkerCommandKind.SendBatch);
        service.ResolvePendingTrade(false);
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Single(service.Trades);
    }

    private static WorkflowService Create(bool sourceRemote) => new(PeerA, PeerB,
        new SimulatedCharacter { Id = 1, PeerId = sourceRemote ? PeerB : PeerA, Bags = 100, Retainers = new() { [1001] = 0 } },
        new SimulatedCharacter { Id = 2, PeerId = sourceRemote ? PeerA : PeerB, Retainers = new() { [1002] = 0 } });

    private static SavedTransferJob Job() => new()
    {
        Definition = JobPlannerTests.Job([1], (2, 100)),
        Meeting = new() { TerritoryId = 20, AetheryteId = 30, X = 1, Y = 2, Z = 3 },
    };
}
