using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class TransferCoordinatorTests
{
    private static SimulatedCharacter Sim(ulong id, Guid peer, uint bags = 0, uint stored = 0, uint bagLimit = 999, uint storageLimit = 1_000_000) => new()
    {
        Id = id, PeerId = peer, Bags = bags, BagLimit = bagLimit, StorageLimit = storageLimit,
        Retainers = new() { [id + 1000] = stored },
    };

    private static SavedTransferJob Saved(TransferJobDefinition definition) => new()
    {
        Definition = definition,
        Meeting = new() { WorldId = 10, TerritoryId = 20, AetheryteId = 30, X = 1, Y = 2, Z = 3 },
    };

    private static TransferJobLedger Ledger(WorkflowService service) => new(service.Run!.Journal!);

    private static PendingCommand PendingOperation(WorkflowService service, GameOperationKind kind) =>
        Assert.Single(service.Run!.Pending, pending => pending.Command.Operation.Kind == kind);

    [Fact]
    public void ActualCoordinatorDiscoversRetainersFillsPriorityAndHandlesRepeatedOverflowTrips()
    {
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, 100, 900, 250), Sim(2, PeerA, 0, 500, 150),
            Sim(3, PeerB, 20, 0, 200), Sim(4, PeerB, 0, 0, 300));
        service.StartJob(Saved(Job([1, 2], (3, 700), (4, null))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(TransferJobState.Completed, service.Run.Journal!.State);
        Assert.Equal(4, service.Run.Snapshots.Count);
        Assert.All(service.Run.Snapshots.Values, snapshot =>
        {
            Assert.True(snapshot.IsPlanningReady);
            Assert.Equal(InventoryVerification.Cached, snapshot.Verification);
        });
        Assert.Equal(700UL, Ledger(service).DeliveredTo(3));
        Assert.Equal(800UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(720UL, service.Characters[3].Total); // The recipient's baseline stock remains protected.
        Assert.Equal(800UL, service.Characters[4].Total);
        Assert.Equal(0UL, service.Characters[1].Total + service.Characters[2].Total);
        Assert.True(service.Trace.Count(trace => trace.Operation == GameOperationKind.Deposit) >= 4);
        Assert.True(service.Trace.Count(trace => trace.Operation == GameOperationKind.Withdraw && trace.CharacterId == 2) >= 3);
        var firstLowPriority = service.Trades.FindIndex(trade => trade.ReceiverId == 4);
        Assert.True(firstLowPriority > 0);
        Assert.Equal(700U, service.Trades.Take(firstLowPriority).Aggregate(0U, (sum, trade) => sum + trade.Quantity));
        Assert.DoesNotContain(service.Trades.Skip(firstLowPriority), trade => trade.ReceiverId == 3);
        Assert.True(service.Run.Deposited.GetValueOrDefault(3UL) <= Ledger(service).DeliveredTo(3));
        Assert.Equal(900UL, service.Run.Withdrawn[1]);
        Assert.Equal(500UL, service.Run.Withdrawn[2]);
        Assert.All(service.Trace.Where(trace => trace.Operation == GameOperationKind.Withdraw),
            pickup => Assert.InRange(pickup.Quantity, 1U, service.Characters[pickup.CharacterId].BagLimit));
    }

    [Fact]
    public void ActualCoordinatorTradesCarriedStockThenCollectsOnlyTheRemainingTarget()
    {
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, 3, 17), Sim(2, PeerB));
        service.DeferGameCommand = command => command.Operation.Kind == GameOperationKind.Withdraw;
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunUntil(_ => service.DeferredGameCommands.Any(command => command.Operation.Kind == GameOperationKind.Withdraw));

        Assert.Equal(3UL, Ledger(service).DeliveredTo(2));
        Assert.Equal(7U, Assert.Single(service.DeferredGameCommands).Operation.Quantity);
        Assert.Contains("collecting from retainers", service.Run!.Status);
        Assert.Equal(17U, service.Characters[1].RetainerTotal);
        service.DeferGameCommand = null;
        service.ReleaseDeferredGameCommand(Assert.Single(service.DeferredGameCommands).Id);
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(new uint[] { 3, 7 }, service.Trades.Select(trade => trade.Quantity));
        var pickup = Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
        Assert.Equal(7U, pickup.Quantity);
        Assert.Equal("meeting", pickup.StartLocation);
        Assert.Equal(10U, service.Characters[1].RetainerTotal);
        Assert.Equal(7UL, service.Run.Withdrawn[1]);
        Assert.Equal(10UL, Ledger(service).DeliveredTo(2));
    }

    [Fact]
    public void ActualCoordinatorLoadsBagCapacityThenTradesMultipleBatchesBeforeRefilling()
    {
        var source = Sim(1, PeerA, 100, 700, 600);
        source.Retainers.Add(2001, 300); // A load may span more than one retainer.
        var service = new WorkflowService(PeerA, PeerB, source, Sim(2, PeerB, bagLimit: 2000));
        service.StartJob(Saved(Job([1], (2, 1100))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(new uint[] { 600, 400 }, service.Trace.Where(trace => trace.Operation == GameOperationKind.Withdraw).Select(trace => trace.Quantity));
        Assert.Equal(new uint[] { 100, 495, 105, 400 }, service.Trades.Select(trade => trade.Quantity));
        Assert.Equal(1000UL, service.Run.Withdrawn[1]);
        Assert.Equal(1100UL, Ledger(service).DeliveredTo(2));
        Assert.Equal(0UL, source.Total);
    }

    [Fact]
    public void ActualCoordinatorProviderPickupsPreserveRestockProtectedStock()
    {
        var definition = Job([1], (1, 100), (2, 20));
        definition.QuantityMode = JobQuantityMode.RestockToTotal;
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 0, 120), Sim(2, PeerB));
        service.StartJob(Saved(definition));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(100UL, service.Characters[1].Total);
        Assert.Equal(20UL, service.Run.Withdrawn[1]);
        Assert.Equal(20U, Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw).Quantity);
        Assert.Equal(20UL, Ledger(service).DeliveredTo(2));
    }

    [Fact]
    public void ActualCoordinatorPausesBeforePickupWhenProviderBagsHaveNoRoom()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 0, 10, 0), Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Contains("provider has no room", service.LastError);
        Assert.DoesNotContain(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
        Assert.Empty(service.Trades);
        Assert.Equal(0UL, Ledger(service).DeliveredTo(2));
    }

    [Fact]
    public void ActualCoordinatorDoesNotReplaceMissingRelayCargoWithUnrelatedRetainerStock()
    {
        var definition = Job([1], (2, 10));
        definition.RelayCharacters[PeerB] = 3;
        var relay = Sim(3, PeerB, stored: 100);
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerA), relay);
        service.StartJob(Saved(definition));
        service.RunUntil(run => service.Trades.Count == 1 && run.Step == CoordinatorStep.SelectLeg);
        relay.Bags = 0; // Cargo was manually removed between the two legs.
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Contains("relay is missing", service.LastError);
        Assert.Equal(100U, relay.RetainerTotal);
        Assert.DoesNotContain(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw && trace.CharacterId == 3);
        Assert.Single(service.Trades);
        Assert.Equal(0UL, Ledger(service).DeliveredTo(2));
        Assert.Equal(10UL, Ledger(service).RelayCargo(3));
    }

    [Fact]
    public void ActualCoordinatorRelaysBetweenCharactersOnOneAccountAndKeepsRelayBaseline()
    {
        var definition = Job([1], (2, 900));
        definition.RelayCharacters[PeerB] = 3;
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, 0, 900, 400), Sim(2, PeerA, 0, 0, 150), Sim(3, PeerB, 100, 500, 250));
        service.StartJob(Saved(definition));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(900UL, Ledger(service).DeliveredTo(2));
        Assert.Equal(900UL, Ledger(service).SourceConsumed(1));
        Assert.Equal(0UL, Ledger(service).RelayCargo(3));
        Assert.Equal(600UL, service.Characters[3].Total);
        Assert.All(service.Trades, trade => Assert.True((trade.SenderId, trade.ReceiverId) is (1, 3) or (3, 2)));
        Assert.True(service.Trace.Count(trace => trace.Operation == GameOperationKind.Deposit && trace.CharacterId == 2) >= 5);
    }

    [Fact]
    public void ActualCoordinatorSkipsUnavailableRetainerCacheWithoutGameplay()
    {
        var source = Sim(1, PeerA, 10, 100);
        source.CacheAvailable = false;
        var service = new WorkflowService(PeerA, PeerB, source, Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, null))));
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Contains("No complete cached", service.Run.SkippedCharacters[1]);
        Assert.Equal(CoordinatorStep.Finished, service.Run.Step);
        Assert.False(string.IsNullOrWhiteSpace(service.Run.ItemSkipReason));
        Assert.DoesNotContain(1UL, service.Run.Snapshots.Keys);
        Assert.DoesNotContain(service.Trace, trace => trace.Operation is GameOperationKind.Login or GameOperationKind.ScanRetainers);
        Assert.Empty(service.Trades);
        Assert.Null(service.Run.Journal);
    }

    [Fact]
    public void ActualCoordinatorRejectsWorkerObservingAnotherCharacter()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerB));
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ReadCachedInventory) result.CharacterId = 999;
            return result;
        };
        service.StartJob(Saved(Job([1], (2, null))));
        service.RunToTerminal();
        Assert.True(service.Run!.Paused);
        Assert.Contains("different character", service.LastError);
        Assert.Empty(service.Trades);
    }

    [Fact]
    public void ActualCoordinatorCompletesVerifiedEmptyMoveAllWithoutTrading()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA), Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, null))));
        service.RunToTerminal();
        Assert.Equal(CoordinatorStep.Finished, service.Run!.Step);
        Assert.Equal(TransferJobState.Completed, service.Run.Journal!.State);
        Assert.False(service.Run.Paused);
        Assert.Empty(service.Trades);
    }

    [Fact]
    public void ActualCoordinatorKeepsShortageAsPartialResult()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 40), Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, 100))));
        service.RunToTerminal();
        Assert.Equal(CoordinatorStep.Finished, service.Run!.Step);
        Assert.True(service.Run.Paused);
        Assert.Equal(TransferJobState.Paused, service.Run.Journal!.State);
        Assert.Equal(60UL, Assert.Single(service.Run.Journal.Plan.UnmetTargets).Missing);
        Assert.Equal(40UL, Ledger(service).DeliveredTo(2));
    }

    [Fact]
    public void ActualCoordinatorResumesPersistedPartialDeliveryWithoutRepeatingIt()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 100, 900, 250), Sim(2, PeerB, 0, 0, 200));
        service.StartJob(Saved(Job([1], (2, 1000))));
        service.RunUntil(run => service.Trades.Count > 0 && run.Step == CoordinatorStep.SelectLeg);
        var alreadyDelivered = Ledger(service).DeliveredTo(2);
        Assert.True(alreadyDelivered > 0);
        service.PauseForTest();
        service.SimulateCoordinatorRestart();
        service.Resume();
        Assert.Equal(alreadyDelivered, Ledger(service).DeliveredTo(2));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(1000UL, Ledger(service).DeliveredTo(2));
        Assert.Equal(1000U, service.Trades.Aggregate(0U, (sum, trade) => sum + trade.Quantity));
        Assert.Equal(service.Trades.Count, service.Trades.Select(trade => trade.SenderCommandId).Distinct().Count());
    }

    [Fact]
    public void ActualCoordinatorRecoversLostPickupThenContinuesMultipleProvidersAndReceiverOverflow()
    {
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, 3, 17, 7), Sim(2, PeerA, 2, 18, 6), Sim(3, PeerB, bagLimit: 5));
        service.SuppressInitialResponse = command => command.Operation.Kind == GameOperationKind.Withdraw;
        service.StartJob(Saved(Job([1, 2], (3, 40))));
        service.RunUntil(run => run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.Withdraw));

        var originalCommand = PendingOperation(service, GameOperationKind.Withdraw).Command.Id;
        Assert.Null(PendingOperation(service, GameOperationKind.Withdraw).Response);
        Assert.Equal(7U, service.Characters[1].Bags);
        Assert.Equal(3UL, Ledger(service).DeliveredTo(3));
        service.SimulateCoordinatorRestart();
        service.Resume();

        Assert.Equal(CoordinatorStep.PrepareParticipants, service.Run!.Step);
        Assert.True(service.Run.Preparation!.Recovering);
        Assert.Equal(originalCommand, PendingOperation(service, GameOperationKind.Withdraw).Command.Id);
        Assert.Equal(0UL, service.Run!.Withdrawn.GetValueOrDefault(1UL));
        service.SuppressInitialResponse = null;
        service.Tick(); // Recover the original pickup outcome; do not collect a second load.
        Assert.Equal(7UL, service.Run.Withdrawn[1]);
        Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
        Assert.Equal(3UL, Ledger(service).DeliveredTo(3));
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(40UL, Ledger(service).DeliveredTo(3));
        Assert.Equal(17UL, service.Run.Withdrawn[1]);
        Assert.Equal(18UL, service.Run.Withdrawn[2]);
        Assert.Equal(0UL, service.Characters[1].Total + service.Characters[2].Total);
        Assert.Equal(40UL, service.Characters[3].Total);
        Assert.True(service.Trace.Count(trace => trace.Operation == GameOperationKind.Deposit) > 1);
        Assert.True(service.Trace.Count(trace => trace.Operation == GameOperationKind.Withdraw && trace.CharacterId == 1) > 1);
        Assert.True(service.Trace.Count(trace => trace.Operation == GameOperationKind.Withdraw && trace.CharacterId == 2) > 1);
        Assert.Equal(40U, service.Trades.Aggregate(0U, (total, trade) => total + trade.Quantity));
    }

    [Fact]
    public void ActualCoordinatorAccountsSavedPickupResultBeforeReinspectingProvider()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 0, 20), Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunUntil(run => run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.Withdraw
            && pending.Response?.Observation is { MovedQuantity: 10 }));

        Assert.False(PendingOperation(service, GameOperationKind.Withdraw).InventoryAdjustmentApplied);
        Assert.Equal(10U, service.Characters[1].Bags);
        Assert.Equal(0UL, service.Run!.Withdrawn.GetValueOrDefault(1UL));
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.Tick(); // Both saved preparation outcomes are consumed before restarting the leg.
        Assert.Equal(10UL, service.Run.Withdrawn[1]);
        Assert.Equal(CoordinatorStep.SelectLeg, service.Run.Step);
        Assert.Empty(service.Run.Pending);
        Assert.Equal(0UL, Ledger(service).DeliveredTo(2));
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(10UL, Ledger(service).DeliveredTo(2));
        Assert.Equal(10U, service.Characters[1].RetainerTotal);
        Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
    }

    [Fact]
    public void ActualCoordinatorCountsFailedPartialPickupOnceAndTradesItAfterResume()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 0, 20), Sim(2, PeerB));
        service.WithdrawMovementLimit = 4;
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.Withdraw && service.WithdrawMovementLimit.HasValue)
            {
                service.WithdrawMovementLimit = null;
                result.Success = false;
                result.Error = "Retainer interrupted after verified partial pickup.";
            }
            return result;
        };
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Contains("partial pickup", service.LastError);
        Assert.Equal(4U, service.Characters[1].Bags);
        Assert.Equal(4UL, service.Run.Withdrawn[1]);
        Assert.Equal(0UL, Ledger(service).DeliveredTo(2));
        service.SimulateCoordinatorRestart();
        service.Resume();
        Assert.Equal(4UL, service.Run.Withdrawn[1]);
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(new uint[] { 4, 6 }, service.Trades.Select(trade => trade.Quantity));
        Assert.Equal(new uint[] { 10, 6 }, service.Trace.Where(trace => trace.Operation == GameOperationKind.Withdraw).Select(trace => trace.Quantity));
        Assert.Equal(10UL, service.Run.Withdrawn[1]);
        Assert.Equal(10U, service.Characters[1].RetainerTotal);
        Assert.Equal(10UL, Ledger(service).DeliveredTo(2));
    }

    [Fact]
    public void ActualCoordinatorNeverReplaysUncertainPickupAfterRepeatedRestarts()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 0, 20), Sim(2, PeerB));
        service.WithdrawMovementLimit = 6;
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.Withdraw)
            {
                result.Success = false;
                result.MovementUncertain = true;
                result.MovedQuantity = 4; // Six physically moved; only four were verified.
                result.Error = "Pickup interrupted before its final movement could be verified.";
            }
            return result;
        };
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Contains("pickup could not be tallied automatically", service.LastError);
        var originalCommand = PendingOperation(service, GameOperationKind.Withdraw).Command.Id;
        Assert.Equal(4UL, service.Run.Withdrawn[1]);
        for (var restart = 0; restart < 2; restart++)
        {
            service.SimulateCoordinatorRestart();
            service.Resume();
            Assert.Equal(CoordinatorStep.PrepareParticipants, service.Run.Step);
            Assert.True(service.Run.Preparation!.Recovering);
            Assert.Equal(originalCommand, PendingOperation(service, GameOperationKind.Withdraw).Command.Id);
            service.RunToTerminal();

            Assert.True(service.Run.Paused);
            Assert.Contains("pickup could not be tallied automatically", service.LastError);
            Assert.Equal(4UL, service.Run.Withdrawn[1]);
            Assert.Equal(6U, service.Characters[1].Bags);
            Assert.Equal(14U, service.Characters[1].RetainerTotal);
            Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Withdraw);
            Assert.Empty(service.Trades);
            Assert.Equal(0UL, Ledger(service).DeliveredTo(2));
        }
    }

    [Fact]
    public void ActualCoordinatorRecoversPreparedBatchWithNoCommandsWithoutInventingTradeEvidence()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 100), Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, 100))));
        service.RunUntil(run => run.Step == CoordinatorStep.ArmReceiver && run.Pending.Count == 0);
        var interruptedId = service.Run!.TradeOperationId!.Value;
        Assert.Empty(service.Trades);
        service.SimulateCoordinatorRestart();
        service.Resume();
        var abandoned = service.Run!.Journal!.Operations.Single(operation => operation.Id == interruptedId);
        Assert.Equal(TransferOperationState.Cancelled, abandoned.State);
        Assert.Null(abandoned.SenderObservation);
        Assert.Null(abandoned.ReceiverObservation);
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(100UL, Ledger(service).DeliveredTo(2));
        Assert.Single(service.Trades);
    }

    [Fact]
    public void ActualCoordinatorAutomaticallyTalliesReceiverOnlyInterruptedBatch()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 100), Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, 100))));
        service.RunUntil(run => run.Step == CoordinatorStep.SendBatch && run.Pending.Count == 1 && run.Pending[0].Ready);
        var interruptedId = service.Run!.TradeOperationId!.Value;
        service.SimulateCoordinatorRestart(interruptArmedReceiver: true);
        service.Resume();
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Null(service.Run.TradeRecovery);
        Assert.Equal(TransferOperationState.Cancelled, service.Run.Journal!.Operations.Single(operation => operation.Id == interruptedId).State);
        Assert.Equal(100UL, Ledger(service).DeliveredTo(2));
        Assert.Single(service.Trades);
    }

    [Fact]
    public void ActualCoordinatorCountsFailedPartialDepositOnceAcrossSerializedResume()
    {
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, 600), Sim(2, PeerB, 25, 0, 225));
        service.DepositMovementLimit = 70;
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.Deposit && service.DepositMovementLimit.HasValue)
            {
                service.DepositMovementLimit = null;
                result.Success = false;
                result.Error = "Retainer interrupted after verified partial movement.";
            }
            return result;
        };
        service.StartJob(Saved(Job([1], (2, 600))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Contains("partial movement", service.LastError);
        Assert.Equal(70U, service.Characters[2].RetainerTotal);
        Assert.Equal(70UL, service.Run.Deposited[2]);
        Assert.True(PendingOperation(service, GameOperationKind.Deposit).InventoryAdjustmentApplied);
        Assert.Equal(200UL, Ledger(service).DeliveredTo(2));

        service.SimulateCoordinatorRestart();
        Assert.True(PendingOperation(service, GameOperationKind.Deposit).InventoryAdjustmentApplied);
        service.Resume();
        service.Tick(); // Recover both preparation responses before selecting a fresh leg.
        Assert.Equal(70UL, service.Run.Deposited[2]);
        Assert.Empty(service.Run.Pending);
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(600UL, Ledger(service).DeliveredTo(2));
        Assert.Equal(625UL, service.Characters[2].Total);
        Assert.Equal((ulong)service.Characters[2].RetainerTotal, service.Run.Deposited[2]);
        Assert.Equal(600U, service.Trades.Aggregate(0U, (sum, trade) => sum + trade.Quantity));
    }

    [Fact]
    public void ActualCoordinatorAccountsSavedDepositResultBeforeRestartingTheLeg()
    {
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, 600), Sim(2, PeerB, 25, 0, 225));
        service.StartJob(Saved(Job([1], (2, 600))));
        service.RunUntil(run => run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.Deposit
            && pending.Response?.Observation is { MovedQuantity: > 0 }));

        var savedDeposit = PendingOperation(service, GameOperationKind.Deposit);
        Assert.False(savedDeposit.InventoryAdjustmentApplied);
        Assert.Equal(200U, service.Characters[2].RetainerTotal);
        Assert.Equal(0UL, service.Run!.Deposited.GetValueOrDefault(2UL));
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.Tick(); // Drain both saved preparation outcomes before replanning.
        Assert.Equal(200UL, service.Run.Deposited[2]);
        Assert.Equal(CoordinatorStep.SelectLeg, service.Run.Step);
        Assert.Empty(service.Run.Pending);
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(600UL, Ledger(service).DeliveredTo(2));
        Assert.Equal(625UL, service.Characters[2].Total);
        Assert.Equal((ulong)service.Characters[2].RetainerTotal, service.Run.Deposited[2]);
        Assert.Equal(2, service.Trace.Count(trace => trace.Operation == GameOperationKind.Deposit));
    }

    [Fact]
    public void ActualCoordinatorRecoversLostDepositResponseWithOriginalCommandId()
    {
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, 600), Sim(2, PeerB, 25, 0, 225));
        service.SuppressInitialResponse = command => command.Operation.Kind == GameOperationKind.Deposit;
        service.StartJob(Saved(Job([1], (2, 600))));
        service.RunUntil(run => run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.Deposit));

        var interruptedCommandId = PendingOperation(service, GameOperationKind.Deposit).Command.Id;
        Assert.Null(PendingOperation(service, GameOperationKind.Deposit).Response);
        Assert.Equal(200U, service.Characters[2].RetainerTotal);
        service.SimulateCoordinatorRestart();
        service.Resume();

        Assert.Equal(CoordinatorStep.PrepareParticipants, service.Run!.Step);
        Assert.True(service.Run.Preparation!.Recovering);
        Assert.Equal(interruptedCommandId, PendingOperation(service, GameOperationKind.Deposit).Command.Id);
        Assert.Null(PendingOperation(service, GameOperationKind.Deposit).Response);
        Assert.Equal(0UL, service.Run.Deposited.GetValueOrDefault(2UL));
        service.SuppressInitialResponse = null;
        service.Tick(); // Re-dispatch is deduplicated by the worker, which returns its durable observation.

        Assert.Equal(200UL, service.Run.Deposited[2]);
        Assert.Equal(200U, service.Characters[2].RetainerTotal);
        Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Deposit);
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(600UL, Ledger(service).DeliveredTo(2));
        Assert.Equal(625UL, service.Characters[2].Total);
        Assert.Equal((ulong)service.Characters[2].RetainerTotal, service.Run.Deposited[2]);
        Assert.Equal(2, service.Trace.Count(trace => trace.Operation == GameOperationKind.Deposit));
    }

    [Fact]
    public void ActualCoordinatorCombinesNeverStartedSenderWithAutomaticallyTalliedReceiver()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 100), Sim(2, PeerB))
        { RejectNextSenderBeforeStart = true };
        service.StartJob(Saved(Job([1], (2, 100))));
        service.RunUntil(run => run.TradeRecovery != null);
        var interruptedId = service.Run!.TradeOperationId!.Value;
        Assert.Empty(service.Trades);
        Assert.Equal(0UL, Ledger(service).DeliveredTo(2));

        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        var cancelled = service.Run.Journal!.Operations.Single(operation => operation.Id == interruptedId);
        Assert.Equal(TransferOperationState.Cancelled, cancelled.State);
        Assert.Equal(0UL, cancelled.SenderObservation!.Quantity);
        Assert.Equal(0UL, cancelled.ReceiverObservation!.Quantity);
        Assert.Equal(100UL, Ledger(service).DeliveredTo(2));
        Assert.Single(service.Trades);
        Assert.Equal(100UL, service.Characters[2].Total);
        Assert.Null(service.Run.TradeRecovery);
    }

    [Fact]
    public void ActualCoordinatorNeverReplaysUncertainDepositAfterRepeatedRestarts()
    {
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, 600), Sim(2, PeerB, 25, 0, 225));
        service.DepositMovementLimit = 70;
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.Deposit)
            {
                result.Success = false;
                result.MovementUncertain = true;
                result.MovedQuantity = 50; // Only part of the physical movement was verified before interruption.
                result.Error = "Interrupted before the last retainer movement could be verified.";
            }
            return result;
        };
        service.StartJob(Saved(Job([1], (2, 600))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Contains("could not be tallied automatically", service.LastError);
        var originalCommandId = PendingOperation(service, GameOperationKind.Deposit).Command.Id;
        Assert.Equal(50UL, service.Run.Deposited[2]);
        Assert.Equal(70U, service.Characters[2].RetainerTotal);
        for (var restart = 0; restart < 2; restart++)
        {
            service.SimulateCoordinatorRestart();
            service.Resume();
            Assert.Equal(CoordinatorStep.PrepareParticipants, service.Run.Step);
            Assert.True(service.Run.Preparation!.Recovering);
            Assert.Equal(originalCommandId, PendingOperation(service, GameOperationKind.Deposit).Command.Id);
            service.RunToTerminal();

            Assert.True(service.Run.Paused);
            Assert.Contains("could not be tallied automatically", service.LastError);
            Assert.Equal(50UL, service.Run.Deposited[2]);
            Assert.Equal(70U, service.Characters[2].RetainerTotal);
            Assert.Single(service.Trace, trace => trace.Operation == GameOperationKind.Deposit);
            Assert.Single(service.Trades);
            Assert.Equal(200UL, Ledger(service).DeliveredTo(2));
        }
    }

    [Fact]
    public void ActualCoordinatorRejectsInvalidMeetingButSkipsMissingRosterSourcesWithoutSendingThemCommands()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerB));
        var badMeeting = Saved(Job([1], (2, null)));
        badMeeting.Meeting.AetheryteId = 0;
        Assert.Throws<InvalidOperationException>(() => service.StartJob(badMeeting));
        Assert.Empty(service.Trace);
        service.StartJob(Saved(Job([999], (2, null))));
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Contains(999UL, service.Run.SkippedCharacters.Keys);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 999);
        Assert.All(service.Trace, command => Assert.Equal(GameOperationKind.ReadCachedInventory, command.Operation));
        Assert.Empty(service.Trades);
    }
}
