using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class ParallelPreparationRecoveryTests
{
    private static SimulatedCharacter Sim(ulong id, Guid peer, uint bags = 0, uint stored = 0, uint capacity = 100) => new()
    {
        Id = id, PeerId = peer, Bags = bags, BagLimit = capacity,
        Retainers = new() { [id + 1000] = stored },
    };

    private static SavedTransferJob Saved(TransferJobDefinition definition) => new()
    {
        Definition = definition,
        Meeting = new() { WorldId = 999, TerritoryId = 20, AetheryteId = 30 },
    };

    private static WorkflowService ConcurrentStorage()
    {
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, bags: 100, stored: 100), Sim(2, PeerB))
        {
            DeferGameCommand = command => command.Operation.Kind is GameOperationKind.Withdraw or GameOperationKind.Deposit,
        };
        service.StartJob(Saved(Job([1], (2, 200))));
        service.RunUntil(_ => service.DeferredGameCommands.Count == 2);
        Assert.Single(service.Trades);
        Assert.Equal(100U, service.Characters[1].RetainerTotal);
        Assert.Equal(0U, service.Characters[1].Bags);
        Assert.Equal(100U, service.Characters[2].Bags);
        Assert.Equal(0U, service.Characters[2].RetainerTotal);
        Assert.Equal(2, service.ActiveCommandsByPeer.Count);
        Assert.All(service.Run!.Pending.GroupBy(pending => pending.PeerId), group => Assert.Single(group));
        return service;
    }

    private static void AssertCompletedOnce(WorkflowService service)
    {
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(CoordinatorStep.Finished, service.Run.Step);
        Assert.Equal(200UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Equal(100UL, service.Run.Withdrawn[1]);
        Assert.Equal(100UL, service.Run.Deposited[2]);
        Assert.Equal(0UL, service.Characters[1].Total);
        Assert.Equal(200UL, service.Characters[2].Total);
        Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
        Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Deposit);
        Assert.Equal(2, service.Trades.Count);
        Assert.Empty(service.ActiveCommandsByPeer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderWithdrawsWhileRecipientDepositsAndTradeWaitsForBoth(bool depositFinishesFirst)
    {
        var service = ConcurrentStorage();
        var firstKind = depositFinishesFirst ? GameOperationKind.Deposit : GameOperationKind.Withdraw;
        var first = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == firstKind);
        service.ReleaseDeferredGameCommand(first.Id);
        for (var i = 0; i < 8; i++)
        {
            service.Tick();
            Assert.False(service.Run!.Paused, service.LastError);
            Assert.All(service.Run.Pending.GroupBy(pending => pending.PeerId), group => Assert.Single(group));
        }

        Assert.Single(service.Trades);
        Assert.Single(service.Trace, trace => trace.Kind == WorkerCommandKind.PreviewBatch);
        var remaining = Assert.Single(service.DeferredGameCommands);
        Assert.NotEqual(firstKind, remaining.Operation.Kind);
        Assert.Contains(service.Run!.Pending, pending => pending.Command.Id == remaining.Id);
        service.DeferGameCommand = null;
        service.ReleaseDeferredGameCommand(remaining.Id);
        service.RunToTerminal();
        AssertCompletedOnce(service);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RestartRecoversBothStorageIdsAndAccountsEachLostAcknowledgmentOnce(
        bool withdrawalCompletedBeforeRestart, bool depositCompletedBeforeRestart)
    {
        var service = ConcurrentStorage();
        var withdrawal = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.Withdraw);
        var deposit = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.Deposit);
        var originalIds = service.Run!.Pending.Select(pending => pending.Command.Id).Order().ToArray();
        service.SuppressInitialResponse = command => command.Id == withdrawal.Id || command.Id == deposit.Id;
        if (withdrawalCompletedBeforeRestart) service.ReleaseDeferredGameCommand(withdrawal.Id);
        if (depositCompletedBeforeRestart) service.ReleaseDeferredGameCommand(deposit.Id);
        service.PauseForTest();
        service.SimulateCoordinatorRestart();
        service.Resume();

        Assert.True(service.Run!.Preparation!.Recovering);
        Assert.Equal(originalIds, service.Run.Pending.Select(pending => pending.Command.Id).Order());
        service.Tick(); // Queries durable completions; still-running commands supply no outcome.
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(withdrawalCompletedBeforeRestart ? 100UL : 0UL, service.Run.Withdrawn.GetValueOrDefault(1UL));
        Assert.Equal(depositCompletedBeforeRestart ? 100UL : 0UL, service.Run.Deposited.GetValueOrDefault(2UL));
        Assert.Single(service.Trades);
        if (!withdrawalCompletedBeforeRestart || !depositCompletedBeforeRestart)
        {
            var traceCount = service.Trace.Count;
            for (var i = 0; i < 8; i++) service.Tick();
            Assert.False(service.Run.Paused, service.LastError);
            Assert.True(service.Run.Preparation!.Recovering);
            Assert.Equal(traceCount, service.Trace.Count);
            Assert.All(service.Run.Pending, pending => Assert.Contains(pending.Command.Id, originalIds));
        }

        service.SuppressInitialResponse = null;
        service.DeferGameCommand = null;
        if (!withdrawalCompletedBeforeRestart) service.ReleaseDeferredGameCommand(withdrawal.Id);
        if (!depositCompletedBeforeRestart) service.ReleaseDeferredGameCommand(deposit.Id);
        service.RunToTerminal();
        AssertCompletedOnce(service);
    }

    [Fact]
    public void UnexpectedFullRelayWaitsForInFlightProviderPickupBeforeSwitchingToDelivery()
    {
        var relay = Sim(9, PeerB, capacity: 300);
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, bags: 100), Sim(2, PeerA, stored: 100), Sim(4, PeerA, capacity: 500), relay);
        service.DeferGameCommand = command => service.Trades.Count == 1
            && (command.Operation.CharacterId == 2 && command.Operation.Kind == GameOperationKind.Withdraw
                || command.Operation.CharacterId == 9 && command.Operation.Kind == GameOperationKind.Inspect);
        service.StartJob(Saved(Job([1, 2], (4, 200))));
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Count == 1);
        relay.OtherItems[new ItemKey(20000, false)] = new() { Bags = 200 };
        service.RunUntil(_ => service.DeferredGameCommands.Count == 2);
        var withdrawal = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.Withdraw);
        var inspection = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.Inspect);
        var allocationId = service.Run!.AllocationId;
        var pinnedWorld = service.Run.EffectiveMeeting!.WorldId;
        service.ReleaseDeferredGameCommand(inspection.Id);
        service.RunUntil(run => run.Preparation?.ReplanRequested == true);
        var traceCount = service.Trace.Count;
        for (var i = 0; i < 8; i++) service.Tick();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(CoordinatorStep.PrepareParticipants, service.Run.Step);
        Assert.Equal(allocationId, service.Run.AllocationId);
        Assert.Equal(pinnedWorld, service.Run.EffectiveMeeting!.WorldId);
        Assert.Equal(traceCount, service.Trace.Count);
        Assert.Equal(withdrawal.Id, Assert.Single(service.Run.Pending).Command.Id);
        Assert.Equal(9UL, service.Run.RelayFlushCharacterId);
        Assert.Single(service.Trades);
        Assert.DoesNotContain(service.Trace, trace => trace.CharacterId == 4 && trace.Operation == GameOperationKind.Login);

        service.DeferGameCommand = null;
        service.ReleaseDeferredGameCommand(withdrawal.Id);
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg);
        Assert.Empty(service.Run.Pending);
        Assert.Null(service.Run.Preparation);
        Assert.Equal(100UL, service.Run.Withdrawn[2]);
        Assert.Equal(100U, service.Characters[2].Bags);
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(200UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(4));
        Assert.Equal(new ulong[] { 9, 4, 9, 4 }, service.Trades.Select(trade => trade.ReceiverId));
        Assert.Single(service.Trace, trace => trace.CharacterId == 2 && trace.Operation == GameOperationKind.Withdraw);
        Assert.Equal(200U, relay.OtherItems[new ItemKey(20000, false)].Bags);
        Assert.DoesNotContain(service.Trace, trace => trace.CharacterId == 9 && trace.Operation == GameOperationKind.Deposit);
    }
}
