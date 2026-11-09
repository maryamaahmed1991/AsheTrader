using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class RelayBatchCoordinatorTests
{
    private static SimulatedCharacter Sim(ulong id, Guid peer, uint bags = 0, uint stored = 0, uint capacity = 2000) => new()
    {
        Id = id, PeerId = peer, Bags = bags, BagLimit = capacity,
        Retainers = new() { [id + 1000] = stored },
    };

    private static SavedTransferJob Saved(TransferJobDefinition definition) => new()
    { Definition = definition, Meeting = new() { WorldId = 10, TerritoryId = 20, AetheryteId = 30 } };

    private static TransferJobLedger Ledger(WorkflowService service) => new(service.Run!.Journal!);

    [Fact]
    public void ThreeProvidersFillOneRelayLoadBeforeDestinationLoginAndPreserveRelayBaseline()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 300), Sim(2, PeerA, 300),
            Sim(3, PeerA, 300), Sim(4, PeerA), Sim(9, PeerB, 50, capacity: 1000));
        service.StartJob(Saved(Job([1, 2, 3], (4, 900))));
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Count == 3);

        Assert.All(service.Trades, trade => Assert.Equal(9UL, trade.ReceiverId));
        Assert.DoesNotContain(service.Trace, command => command.Operation == GameOperationKind.Login && command.CharacterId == 4);
        Assert.Equal(900UL, Ledger(service).RelayCargo(9));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(new ulong[] { 9, 9, 9, 4, 4, 4 }, service.Trades.Select(trade => trade.ReceiverId));
        Assert.Equal(900UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(50U, service.Characters[9].Bags);
        Assert.Equal(0UL, Ledger(service).RelayCargo(9));
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 9 && command.Operation is GameOperationKind.Withdraw or GameOperationKind.Deposit);
    }

    [Fact]
    public void RelayFlushesAtCapacityThenCollectsTheRemainingProviders()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 300), Sim(2, PeerA, 300),
            Sim(3, PeerA, 300), Sim(4, PeerA), Sim(9, PeerB, capacity: 500));
        service.StartJob(Saved(Job([1, 2, 3], (4, 900))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        var firstDelivery = service.Trades.FindIndex(trade => trade.ReceiverId == 4);
        Assert.Equal(500U, service.Trades.Take(firstDelivery).Aggregate(0U, (sum, trade) => sum + trade.Quantity));
        var collected = 0U;
        var maximumLoad = 0U;
        foreach (var trade in service.Trades)
        {
            collected = trade.ReceiverId == 9 ? checked(collected + trade.Quantity) : collected - trade.Quantity;
            maximumLoad = Math.Max(maximumLoad, collected);
            Assert.InRange(collected, 0U, 500U);
        }
        Assert.Equal(500U, maximumLoad);
        Assert.Equal(0U, collected);
        Assert.Equal(900UL, Ledger(service).DeliveredTo(4));
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 9 && command.Operation == GameOperationKind.Deposit);
    }

    [Fact]
    public void ReloadDuringCollectionKeepsGatheringBeforeDelivery()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 300), Sim(2, PeerA, 300),
            Sim(3, PeerA, 300), Sim(4, PeerA), Sim(9, PeerB, capacity: 1000));
        service.StartJob(Saved(Job([1, 2, 3], (4, 900))));
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Count == 1);
        service.SimulateCoordinatorRestart(); service.Resume();
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Count == 3);

        Assert.All(service.Trades, trade => Assert.Equal(9UL, trade.ReceiverId));
        Assert.Equal(900UL, Ledger(service).RelayCargo(9));
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(900UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(6, service.Trades.Count);
    }

    [Fact]
    public void ReloadDuringFlushDrainsTheLoadBeforeLoggingAnotherProvider()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 300), Sim(2, PeerA, 300),
            Sim(3, PeerA, 300), Sim(4, PeerA), Sim(9, PeerB, capacity: 500));
        service.StartJob(Saved(Job([1, 2, 3], (4, 900))));
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Any(trade => trade.ReceiverId == 4));
        Assert.Equal(200UL, Ledger(service).RelayCargo(9));
        Assert.Equal(9UL, service.Run!.RelayFlushCharacterId);
        var priorTrades = service.Trades.Count;
        service.SimulateCoordinatorRestart(); service.Resume();
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Count > priorTrades);

        Assert.Equal(4UL, service.Trades[^1].ReceiverId);
        Assert.Equal(0UL, Ledger(service).RelayCargo(9));
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(900UL, Ledger(service).DeliveredTo(4));
    }

    [Fact]
    public void UnexpectedFullRelayFlushesItsCargoInsteadOfDepositingIt()
    {
        var relay = Sim(9, PeerB, capacity: 500);
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 150), Sim(2, PeerA, 150), Sim(4, PeerA), relay);
        service.StartJob(Saved(Job([1, 2], (4, 300))));
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Count == 1);
        relay.OtherItems[new ItemKey(20000, false)] = new() { Bags = 350 };
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(new ulong[] { 9, 4, 9, 4 }, service.Trades.Select(trade => trade.ReceiverId));
        Assert.Equal(300UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(350U, relay.OtherItems[new ItemKey(20000, false)].Bags);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 9 && command.Operation == GameOperationKind.Deposit);
    }

    [Fact]
    public void CollectionAndFlushFinishHigherPriorityRecipientBeforeCollectingForTheNext()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 300), Sim(2, PeerA, 200),
            Sim(4, PeerA), Sim(5, PeerA), Sim(9, PeerB, capacity: 1000));
        service.StartJob(Saved(Job([1, 2], (4, 400), (5, 100))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        var delivery = 0U;
        var received = 0U;
        foreach (var trade in service.Trades)
        {
            if (trade.ReceiverId == 9)
            {
                if (received >= 400) Assert.Equal(400U, delivery);
                received += trade.Quantity;
            }
            if (trade.ReceiverId == 4) delivery += trade.Quantity;
            if (trade.ReceiverId == 5) Assert.Equal(400U, delivery);
        }
        Assert.Equal(400UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(100UL, Ledger(service).DeliveredTo(5));
    }

    [Fact]
    public void EmptyOrUnallocatedProvidersAndSatisfiedRecipientsAreNeverLoggedIn()
    {
        var definition = Job([1, 2, 3], (4, 50), (5, 100));
        definition.QuantityMode = JobQuantityMode.RestockToTotal;
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 100), Sim(2, PeerA, 1), Sim(3, PeerA),
            Sim(4, PeerA, 50), Sim(5, PeerA), Sim(9, PeerB));
        service.StartJob(Saved(definition));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        var funded = service.Run.Journal!.Plan.Allocations.SelectMany(allocation => allocation.Legs
            .SelectMany(leg => new[] { leg.SenderId, leg.ReceiverId })).ToHashSet();
        Assert.All(service.Trace.Where(command => command.Operation == GameOperationKind.Login),
            command => Assert.Contains(command.CharacterId, funded));
        Assert.DoesNotContain(service.Trace, command => command.Operation == GameOperationKind.Login && command.CharacterId is 3 or 4);
        Assert.Equal(100UL, Ledger(service).DeliveredTo(5));
    }

    [Fact]
    public void ProviderRetainerLoadsAreLimitedByRemainingRelaySpace()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 300), Sim(2, PeerA, stored: 300),
            Sim(4, PeerA), Sim(9, PeerB, capacity: 500));
        service.StartJob(Saved(Job([1, 2], (4, 600))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(new uint[] { 200, 100 }, service.Trace.Where(command => command.Operation == GameOperationKind.Withdraw).Select(command => command.Quantity));
        Assert.Equal(600UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(300UL, service.Run.Withdrawn[2]);
    }

    [Fact]
    public void LostIncomingResponseReconcilesOriginalTradeBeforeContinuingTheLoad()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 300), Sim(2, PeerA, 300),
            Sim(3, PeerA, 300), Sim(4, PeerA), Sim(9, PeerB, capacity: 1000))
        {
            SuppressInitialResponse = command => command.Kind == WorkerCommandKind.SendBatch && command.Operation.CharacterId == 2,
        };
        service.StartJob(Saved(Job([1, 2, 3], (4, 900))));
        service.RunUntil(run => run.Step == CoordinatorStep.AwaitTrade && service.Trades.Count == 2);
        var interruptedId = service.Run!.TradeOperationId;
        Assert.Equal(300UL, Ledger(service).RelayCargo(9));
        service.SimulateCoordinatorRestart(); service.Resume();
        Assert.Equal(interruptedId, service.Run.TradeOperationId);
        service.SuppressInitialResponse = null;
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.All(service.Trades.Take(3), trade => Assert.Equal(9UL, trade.ReceiverId));
        Assert.Equal(6, service.Trades.Count);
        Assert.Equal(900UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(0UL, Ledger(service).RelayCargo(9));
    }

    [Fact]
    public void LegacyJournalWithoutSchedulerFieldsPreservesItsCarriedCargo()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 300), Sim(2, PeerA, 300),
            Sim(4, PeerA), Sim(9, PeerB, capacity: 500));
        service.StartJob(Saved(Job([1, 2], (4, 600))));
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Count == 1);
        var legacy = Newtonsoft.Json.Linq.JObject.Parse(service.SavedJson);
        legacy.Remove("RelayCapacity"); legacy.Remove("RelayFlushCharacterId");
        legacy.Remove("EffectiveMeeting");
        typeof(WorkflowService).GetProperty(nameof(WorkflowService.SavedJson))!
            .SetValue(service, legacy.ToString(Newtonsoft.Json.Formatting.None));
        service.SimulateCoordinatorRestart(); service.Resume(); service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(600UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(0UL, Ledger(service).RelayCargo(9));
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 9 && command.Operation == GameOperationKind.Deposit);
    }
}
