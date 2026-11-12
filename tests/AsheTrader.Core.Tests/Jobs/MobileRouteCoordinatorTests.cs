using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class MobileRouteCoordinatorTests
{
    private static readonly RouteWorld[] Worlds =
    [
        new(11, "Alpha", 1, "First DC", 1), new(12, "Beta", 1, "First DC", 1),
        new(21, "Gamma", 2, "Second DC", 1), new(31, "Recipient", 3, "Third DC", 1),
        new(999, "Captured", 9, "Captured DC", 1),
    ];

    private static SimulatedCharacter Sim(ulong id, Guid peer, uint world, uint bags = 0, uint capacity = 1000) => new()
    {
        Id = id, PeerId = peer, HomeWorldId = world, WorldName = Worlds.Single(entry => entry.WorldId == world).Name,
        Bags = bags, BagLimit = capacity, Retainers = new() { [id + 1000] = 0 },
    };

    private static WorkflowService Service(params SimulatedCharacter[] characters) => new(PeerA, PeerB, characters)
    { TestWorlds = [.. Worlds] };

    private static SavedTransferJob Saved(TransferJobDefinition definition, uint capturedWorld = 999) => new()
    {
        Definition = definition,
        Meeting = new() { WorldId = capturedWorld, TerritoryId = 20, AetheryteId = 30, X = 1, Y = 2, Z = 3 },
    };

    private static TransferJobLedger Ledger(WorkflowService service) => new(service.Run!.Journal!);

    [Theory]
    [InlineData(999U)]
    [InlineData(0U)]
    public void CapturedWorldDoesNotAnchorTradeAndInterleavedProviderDcsAreGrouped(uint capturedWorld)
    {
        var service = Service(Sim(1, PeerA, 11, 100), Sim(2, PeerA, 21, 100), Sim(3, PeerA, 12, 100), Sim(4, PeerB, 31));
        service.StartJob(Saved(Job([1, 2, 3], (4, 300)), capturedWorld));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(new ulong[] { 1, 3, 2 }, service.Trades.Select(trade => trade.SenderId));
        Assert.Equal(new uint[] { 11, 12, 21 }, service.Trades.Select(trade => trade.WorldId));
        Assert.All(service.Trace.Where(command => command.Operation == GameOperationKind.TravelMeeting && command.CharacterId != 4),
            command => Assert.Equal(service.Characters[command.CharacterId].HomeWorldId, command.DestinationWorldId));
        Assert.DoesNotContain(service.Trace, command => command.Operation == GameOperationKind.TravelMeeting && command.DestinationWorldId == 999);
        Assert.Equal(300UL, Ledger(service).DeliveredTo(4));
    }

    [Fact]
    public void FullRelayDeliversAtRecipientHomeThenResumesUnfinishedCollectionDc()
    {
        var service = Service(Sim(1, PeerA, 11, 100), Sim(2, PeerA, 21, 100), Sim(3, PeerA, 12, 100),
            Sim(4, PeerA, 31), Sim(9, PeerB, 11, capacity: 150));
        service.StartJob(Saved(Job([1, 2, 3], (4, 300))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        var incoming = service.Trades.Where(trade => trade.ReceiverId == 9).ToArray();
        Assert.Equal(new ulong[] { 1, 3, 3, 2 }, incoming.Select(trade => trade.SenderId));
        Assert.Equal(new uint[] { 11, 12, 12, 21 }, incoming.Select(trade => trade.WorldId));
        var firstFlush = service.Trades.SkipWhile(trade => trade.ReceiverId == 9).TakeWhile(trade => trade.ReceiverId == 4).ToArray();
        Assert.Equal(150U, firstFlush.Aggregate(0U, (sum, trade) => sum + trade.Quantity));
        Assert.All(service.Trades.Where(trade => trade.ReceiverId == 4), trade => Assert.Equal(31U, trade.WorldId));
        Assert.Equal(300UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(0UL, Ledger(service).RelayCargo(9));
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 9 && command.Operation == GameOperationKind.Deposit);
    }

    [Fact]
    public void RelayWaitsAtRecipientHomeThroughSeveralLocalDepositCycles()
    {
        var service = Service(Sim(1, PeerA, 11, 300), Sim(4, PeerA, 31, capacity: 100), Sim(9, PeerB, 11));
        service.StartJob(Saved(Job([1], (4, 300))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        var deposits = service.Trace.Where(command => command.CharacterId == 4 && command.Operation == GameOperationKind.Deposit).ToArray();
        Assert.Equal(2, deposits.Length);
        Assert.All(deposits, command => Assert.Equal(31U, command.StartWorldId));
        Assert.All(service.Trace.Where(command => command.CharacterId == 4 && command.Operation == GameOperationKind.TravelMeeting),
            command => { Assert.Equal(31U, command.StartWorldId); Assert.Equal(31U, command.DestinationWorldId); });
        var relayTravels = service.Trace.Where(command => command.CharacterId == 9 && command.Operation == GameOperationKind.TravelMeeting).ToArray();
        Assert.Single(relayTravels, command => command.StartWorldId != command.DestinationWorldId);
        Assert.All(service.Trades.Where(trade => trade.ReceiverId == 4), trade => Assert.Equal(31U, trade.WorldId));
        Assert.Equal(300UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(200UL, service.Run.Deposited[4]);
    }

    [Fact]
    public void RestartAfterLostTravelAcknowledgementKeepsTheProviderDestination()
    {
        var service = Service(Sim(1, PeerA, 11, 100), Sim(4, PeerB, 31));
        service.SuppressInitialResponse = command => command.Operation.Kind == GameOperationKind.TravelMeeting && command.Operation.CharacterId == 1;
        service.StartJob(Saved(Job([1], (4, 100))));
        service.RunUntil(run => run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.TravelMeeting
            && pending.Command.Operation.CharacterId == 1 && pending.Response == null));
        Assert.Equal(11U, service.Run!.EffectiveMeeting!.WorldId);
        var route = JsonConvert.SerializeObject(service.Run.CollectionRoute);
        service.SimulateCoordinatorRestart(); service.Resume();
        service.SuppressInitialResponse = null;
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(route, JsonConvert.SerializeObject(service.Run.CollectionRoute));
        Assert.Single(service.Trades);
        Assert.Equal(11U, service.Trades[0].WorldId);
        Assert.All(service.Trace.Where(command => command.Operation == GameOperationKind.TravelMeeting), command => Assert.Equal(11U, command.DestinationWorldId));
    }

    [Fact]
    public void LostRelayDeliveryResponseKeepsRecipientHomePinnedAndDoesNotRepeatDelivery()
    {
        var service = Service(Sim(1, PeerA, 11, 100), Sim(3, PeerA, 12, 100), Sim(4, PeerA, 31), Sim(9, PeerB, 11));
        service.SuppressInitialResponse = command => command.Kind == WorkerCommandKind.SendBatch && command.Operation.CharacterId == 9;
        service.StartJob(Saved(Job([1, 3], (4, 200))));
        service.RunUntil(run => run.Step == CoordinatorStep.AwaitTrade && service.Trades.Any(trade => trade.SenderId == 9));
        var pendingTrade = service.Run!.TradeOperationId;
        Assert.Equal(31U, service.Run.EffectiveMeeting!.WorldId);
        service.SimulateCoordinatorRestart(); service.Resume();
        Assert.Equal(pendingTrade, service.Run.TradeOperationId);
        Assert.Equal(31U, service.Run.EffectiveMeeting.WorldId);
        service.SuppressInitialResponse = null;
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(4, service.Trades.Count);
        Assert.All(service.Trades.Where(trade => trade.SenderId == 9), trade => Assert.Equal(31U, trade.WorldId));
        Assert.Equal(200UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(0UL, Ledger(service).RelayCargo(9));
    }

    [Fact]
    public void LegacyJournalBuildsNewItineraryWithoutChangingAllocatedCargo()
    {
        var service = Service(Sim(1, PeerA, 11, 100), Sim(2, PeerA, 21, 100), Sim(3, PeerA, 12, 100),
            Sim(4, PeerA, 31), Sim(9, PeerB, 11));
        service.StartJob(Saved(Job([1, 2, 3], (4, 300))));
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Count == 1);
        var allocations = service.Run!.Journal!.Plan.Allocations.Select(allocation => (allocation.Id, allocation.Quantity)).ToArray();
        var legacy = JObject.Parse(service.SavedJson);
        foreach (var field in new[] { "CollectionRoute", "ObservedWorlds", "MeetingPinned", "MeetingDataCenterName", "MeetingReason" }) legacy.Remove(field);
        typeof(WorkflowService).GetProperty(nameof(WorkflowService.SavedJson))!.SetValue(service, legacy.ToString(Formatting.None));
        service.SimulateCoordinatorRestart(); service.Resume(); service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.NotNull(service.Run.CollectionRoute);
        Assert.Equal(allocations, service.Run.Journal!.Plan.Allocations.Select(allocation => (allocation.Id, allocation.Quantity)).ToArray());
        Assert.Equal(new ulong[] { 1, 3, 2 }, service.Trades.Where(trade => trade.ReceiverId == 9).Select(trade => trade.SenderId));
        Assert.All(service.Trades.Where(trade => trade.ReceiverId == 4), trade => Assert.Equal(31U, trade.WorldId));
        Assert.Equal(300UL, Ledger(service).DeliveredTo(4));
        Assert.Equal(0UL, Ledger(service).RelayCargo(9));
    }
}
