using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using Newtonsoft.Json;
using static AsheTrader.Core.Tests.Jobs.TradeFailureCoordinatorTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class AutomaticTradeRecoveryTests
{
    private static WorkflowService Interrupted(uint senderAfter = 100, uint receiverAfter = 0)
    {
        var service = Create();
        service.DeferTradeCompletion = true;
        service.StartJob(Job());
        service.RunUntil(run => run.Step == CoordinatorStep.AwaitTrade && run.Pending.Count == 2);
        service.Characters[1].Bags = senderAfter;
        service.Characters[2].Bags = receiverAfter;
        service.FailTradeForTest(WorkerCommandKind.SendBatch, "Original native timeout.");
        return service;
    }

    [Fact]
    public void MatchingFullChangesCommitOriginalBatchWithoutAnotherGameTrade()
    {
        var service = Interrupted(0, 100);
        var original = service.Run!.TradeOperationId;
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(TransferOperationState.Committed, Assert.Single(service.Run.Journal!.Operations).State);
        Assert.Equal(original, Assert.Single(service.Run.Journal.Operations).Id);
        Assert.Equal(100UL, new TransferJobLedger(service.Run.Journal).DeliveredTo(2));
        Assert.Single(service.Trace, trace => trace.Kind == WorkerCommandKind.SendBatch);
        Assert.Empty(service.Trades);
        Assert.Null(service.Run.TradeRecovery);
    }

    [Theory]
    [InlineData(0U, 99U)]
    [InlineData(50U, 50U)]
    [InlineData(100U, 100U)]
    [InlineData(101U, 0U)]
    public void MismatchedPartialOrOppositeDeltasPauseWithoutAccountingOrRetry(uint sender, uint receiver)
    {
        var service = Interrupted(sender, receiver);
        service.RunToTerminal();
        Assert.True(service.Run!.Paused);
        Assert.Contains("do not prove the same zero-or-full", service.LastError);
        Assert.Equal(0UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Empty(service.Run.TradeRecovery!.Acknowledgements);
        Assert.Single(service.Trace, trace => trace.Kind == WorkerCommandKind.SendBatch);
        Assert.Equal(sender, service.Characters[1].Bags);
        Assert.Equal(receiver, service.Characters[2].Bags);
    }

    [Fact]
    public void ChangedBagsBetweenTallyAndDurableSettlementRefuseTheOldDecision()
    {
        var service = Interrupted();
        service.BeforeAutomaticResolution = command => service.Characters[command.Operation.CharacterId].Bags++;
        service.RunToTerminal();
        Assert.True(service.Run!.Paused);
        Assert.Contains("Inventory changed after the paired tally", service.LastError);
        Assert.Equal(0UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Empty(service.Trades);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostSettlementAcknowledgementAndRestartReplaySavedWorkerDecisionExactlyOnce(bool transferred)
    {
        var service = transferred ? Interrupted(0, 100) : Interrupted();
        service.SuppressAutomaticResolutionReply = command => command.Kind == WorkerCommandKind.SendBatch;
        service.RunUntil(run => run.TradeRecovery?.Acknowledgements.Count == 1);
        var original = service.Run!.TradeOperationId;
        service.SimulateCoordinatorRestart();
        service.SuppressAutomaticResolutionReply = null;
        service.DeferTradeCompletion = false;
        service.Resume();
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(100UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Equal(transferred ? TransferOperationState.Committed : TransferOperationState.Cancelled,
            service.Run.Journal!.Operations.Single(operation => operation.Id == original).State);
        Assert.Equal(transferred ? 0 : 1, service.Trades.Count);
        Assert.Null(service.Run.TradeRecovery);
    }

    [Fact]
    public void ExplicitPauseDuringAutomaticTallyStaysPausedUntilResume()
    {
        var service = Interrupted();
        var round = service.Run!.TradeRecovery!.RoundId;
        service.PauseForTest("User paused.");
        for (var i = 0; i < 10; i++) service.Tick();
        Assert.True(service.Run.Paused);
        Assert.Empty(service.Run.TradeRecovery.Tallies);
        Assert.Empty(service.Trades);
        service.DeferTradeCompletion = false;
        service.Resume();
        Assert.NotEqual(round, service.Run.TradeRecovery!.RoundId);
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Single(service.Trades);
    }

    [Fact]
    public void ThreeEmptyAttemptsAreCancelledAutomaticallyThenFurtherAttemptsPause()
    {
        var service = Interrupted();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            service.RunUntil(run => run.TradeRecovery == null);
            Assert.Equal(attempt, service.Run!.ConsecutiveEmptyTrades);
            Assert.Equal(attempt, service.Run.Journal!.Operations.Count(operation => operation.State == TransferOperationState.Cancelled));
            if (attempt == 3) break;
            service.RunUntil(run => run.Step == CoordinatorStep.AwaitTrade && run.Pending.Count == 2);
            service.FailTradeForTest(WorkerCommandKind.SendBatch, "Trade request failed again.");
        }
        Assert.True(service.Run!.Paused);
        Assert.Contains("Three consecutive", service.LastError);
        Assert.Empty(service.Trades);
        Assert.Null(service.Run.TradeOperationId);
        service.DeferTradeCompletion = false;
        service.Resume();
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Single(service.Trades);
    }

    [Fact]
    public void SavedTerminalCleanupNeverRetainsRecoveryWithEmptyCommands()
    {
        var service = Interrupted(0, 100);
        service.RunUntil(run => run.TradeOperationId == null);
        var saved = JsonConvert.DeserializeObject<CoordinatorRun>(service.SavedJson)!;
        Assert.Empty(saved.Pending);
        Assert.Null(saved.TradeRecovery);
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(100UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Single(service.Run.Journal!.Operations);
    }
}
