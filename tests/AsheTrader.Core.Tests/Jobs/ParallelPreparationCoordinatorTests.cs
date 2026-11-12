using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class ParallelPreparationCoordinatorTests
{
    private static SimulatedCharacter Sim(ulong id, Guid peer, uint bags = 0, uint stored = 0, uint capacity = 999) => new()
    {
        Id = id, PeerId = peer, Bags = bags, BagLimit = capacity, Retainers = new() { [id + 1000] = stored },
    };

    private static SavedTransferJob Saved(uint quantity = 100) => new()
    {
        Definition = Job([1], (2, quantity)),
        Meeting = new() { WorldId = 999, TerritoryId = 20, AetheryteId = 30, X = 1, Y = 2, Z = 3 },
    };

    private static void AssertOneCommandPerPeer(WorkflowService service)
    {
        Assert.All(service.Run!.Pending.GroupBy(pending => pending.PeerId), group => Assert.Single(group));
        Assert.InRange(service.ActiveCommandsByPeer.Count, 0, 2);
    }

    private static void TickWhileBlocked(WorkflowService service)
    {
        for (var i = 0; i < 8; i++)
        {
            service.Tick();
            Assert.False(service.Run!.Paused, service.LastError);
            AssertOneCommandPerPeer(service);
        }
    }

    [Fact]
    public void BothLoginsStartBeforeEitherFinishesAndEachPeerKeepsOneNativeOperation()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 100), Sim(2, PeerB))
        { DeferGameCommand = command => command.Operation.Kind == GameOperationKind.Login };
        service.StartJob(Saved());
        service.RunUntil(_ => service.DeferredGameCommands.Count == 2);

        Assert.All(service.DeferredGameCommands, command => Assert.Equal(GameOperationKind.Login, command.Operation.Kind));
        Assert.Equal(2, service.ActiveCommandsByPeer.Count);
        Assert.Equal(2, service.PeakActivePeerCount);
        AssertOneCommandPerPeer(service);
        TickWhileBlocked(service);
        Assert.Equal(2, service.Trace.Count(trace => trace.Operation == GameOperationKind.Login));
        service.ReleaseDeferredGameCommandsForPeer(PeerA);
        TickWhileBlocked(service);
        Assert.Single(service.DeferredGameCommands);
        Assert.DoesNotContain(service.Trace, trace => trace.CharacterId == 2 && trace.Operation == GameOperationKind.Inspect);
        Assert.Empty(service.Trades);

        service.DeferGameCommand = null;
        service.ReleaseDeferredGameCommandsForPeer(PeerB);
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Single(service.Trades);
    }

    [Fact]
    public void ReceiverTravelsWhileProviderWithdrawalIsStillRunning()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, stored: 100), Sim(2, PeerB))
        {
            DeferGameCommand = command => command.Operation.Kind == GameOperationKind.Withdraw
                || command.Operation.Kind == GameOperationKind.TravelMeeting && command.Operation.CharacterId == 2,
        };
        service.StartJob(Saved());
        service.RunUntil(_ => service.DeferredGameCommands.Count == 2);
        var withdrawal = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.Withdraw);
        var travel = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.TravelMeeting);
        Assert.Equal(100U, service.Characters[1].RetainerTotal);
        Assert.Equal(0U, service.Characters[1].Bags);
        Assert.Equal(2UL, travel.Operation.CharacterId);
        AssertOneCommandPerPeer(service);

        service.ReleaseDeferredGameCommand(travel.Id);
        TickWhileBlocked(service);
        Assert.Equal("meeting", service.Characters[2].Location);
        Assert.Single(service.DeferredGameCommands);
        Assert.Empty(service.Trades);
        Assert.DoesNotContain(service.Trace, trace => trace.Kind is WorkerCommandKind.PreviewBatch or WorkerCommandKind.ReceiveBatch or WorkerCommandKind.SendBatch);

        service.DeferGameCommand = null;
        service.ReleaseDeferredGameCommand(withdrawal.Id);
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(100UL, service.Run.Withdrawn[1]);
        Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
        Assert.Single(service.Trades);
    }

    [Fact]
    public void SenderTravelsWhileRecipientDepositsAndNeitherStartsTheNextTradeEarly()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 300), Sim(2, PeerB, capacity: 100));
        service.DeferGameCommand = command => command.Operation.Kind == GameOperationKind.Deposit
            || service.Trades.Count > 0 && command.Operation.Kind == GameOperationKind.TravelMeeting && command.Operation.CharacterId == 1;
        service.StartJob(Saved(300));
        service.RunUntil(_ => service.DeferredGameCommands.Count == 2);
        var deposit = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.Deposit);
        var travel = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.TravelMeeting);
        Assert.Equal(1UL, travel.Operation.CharacterId);
        Assert.Single(service.Trades);
        Assert.Equal(100U, service.Characters[2].Bags);
        Assert.Equal(0U, service.Characters[2].RetainerTotal);

        service.ReleaseDeferredGameCommand(travel.Id);
        TickWhileBlocked(service);
        Assert.Single(service.Trades);
        Assert.Single(service.Trace, trace => trace.Kind == WorkerCommandKind.PreviewBatch);
        AssertOneCommandPerPeer(service);
        service.DeferGameCommand = null;
        service.ReleaseDeferredGameCommand(deposit.Id);
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(300UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Equal(200UL, service.Run.Deposited[2]);
        Assert.Equal(3, service.Trades.Count);
    }

    [Fact]
    public void BothTravelCommandsUseOnePinnedWorldAndBothMustFinishBeforePreview()
    {
        var service = new WorkflowService(PeerA, PeerB,
            new SimulatedCharacter { Id = 1, PeerId = PeerA, HomeWorldId = 11, WorldName = "Provider", Bags = 100 },
            new SimulatedCharacter { Id = 2, PeerId = PeerB, HomeWorldId = 21, WorldName = "Recipient" })
        {
            TestWorlds = [new(11, "Provider", 1, "First DC", 1), new(21, "Recipient", 2, "Second DC", 1), new(999, "Captured", 9, "Old DC", 1)],
            DeferGameCommand = command => command.Operation.Kind == GameOperationKind.TravelMeeting,
        };
        service.StartJob(Saved());
        service.RunUntil(_ => service.DeferredGameCommands.Count == 2);
        Assert.All(service.DeferredGameCommands, command =>
        {
            Assert.Equal(11U, command.Operation.Destination!.WorldId);
            Assert.Equal(20U, command.Operation.Destination.TerritoryId);
            Assert.Equal(30U, command.Operation.Destination.AetheryteId);
            Assert.Equal(1, command.Operation.Destination.X);
        });
        service.ReleaseDeferredGameCommandsForPeer(PeerA);
        TickWhileBlocked(service);
        Assert.DoesNotContain(service.Trace, trace => trace.Kind == WorkerCommandKind.PreviewBatch);
        Assert.Equal(21U, service.Characters[2].CurrentWorldId);
        service.DeferGameCommand = null;
        service.ReleaseDeferredGameCommandsForPeer(PeerB);
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(11U, Assert.Single(service.Trades).WorldId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartRecoversBothOutstandingCommandsWithoutRepeatingProviderStorage(bool withdrawalCompletedBeforeRestart)
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, stored: 100), Sim(2, PeerB))
        {
            DeferGameCommand = command => command.Operation.Kind == GameOperationKind.Withdraw
                || command.Operation.Kind == GameOperationKind.TravelMeeting && command.Operation.CharacterId == 2,
        };
        service.StartJob(Saved());
        service.RunUntil(_ => service.DeferredGameCommands.Count == 2);
        var withdrawal = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.Withdraw);
        var travel = Assert.Single(service.DeferredGameCommands, command => command.Operation.Kind == GameOperationKind.TravelMeeting);
        var originalIds = service.Run!.Pending.Select(pending => pending.Command.Id).Order().ToArray();
        if (withdrawalCompletedBeforeRestart)
        {
            service.SuppressInitialResponse = command => command.Id == withdrawal.Id;
            service.ReleaseDeferredGameCommand(withdrawal.Id);
            service.PauseForTest();
        }
        service.SimulateCoordinatorRestart();
        service.Resume();
        Assert.True(service.Run!.Preparation!.Recovering);
        Assert.Equal(originalIds, service.Run.Pending.Select(pending => pending.Command.Id).Order());
        TickWhileBlocked(service);
        Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
        Assert.Empty(service.Trades);
        service.SuppressInitialResponse = null;
        service.DeferGameCommand = null;
        if (!withdrawalCompletedBeforeRestart) service.ReleaseDeferredGameCommand(withdrawal.Id);
        service.ReleaseDeferredGameCommand(travel.Id);
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
        Assert.Equal(100UL, service.Run.Withdrawn[1]);
        Assert.Equal(0U, service.Characters[1].RetainerTotal);
        Assert.Equal(100UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Single(service.Trades);
    }

    [Fact]
    public void EndingAfterCompletedStorageBeforeItsResultWasConsumedAccountsThatMovementOnce()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, stored: 100), Sim(2, PeerB));
        service.StartJob(Saved());
        service.RunUntil(run => run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.Withdraw
            && pending.Response?.Observation is { MovedQuantity: 100 }) && run.Pending.All(pending => pending.Response != null));
        Assert.Equal(100U, service.Characters[1].Bags);
        Assert.Equal(0UL, service.Run!.Withdrawn.GetValueOrDefault(1UL));
        service.PauseForTest();

        service.EndPausedJob();
        service.EndPausedJob();

        Assert.Equal(CoordinatorStep.Finished, service.Run.Step);
        Assert.Equal(100UL, service.Run.Withdrawn[1]);
        Assert.Equal(0UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
        Assert.Empty(service.Trades);
    }
}
