using AsheTrader.Core.Jobs;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class CollectionRoutePlannerTests
{
    private static readonly RouteWorld[] Worlds =
    [
        new(11, "Alpha", 1, "DC One", 2), new(12, "Amber", 1, "DC One", 2),
        new(21, "Beta", 2, "DC Two", 2), new(22, "Blue", 2, "DC Two", 2),
        new(31, "Copper", 3, "DC Three", 2), new(41, "Foreign", 4, "Foreign DC", 3),
        new(51, "Ocean", 5, "Ocean DC", 4), new(61, "Delta", 6, "DC Six", 2),
    ];
    private static JobCharacterSnapshot Character(ulong id, string world) => new() { CharacterId = id, World = world };
    private static TransferAllocation Allocation(ulong source, ulong recipient = 9, ulong relay = 0, ulong quantity = 100) => new()
    {
        SourceCharacterId = source, RecipientCharacterId = recipient, Quantity = quantity,
        Legs = relay == 0 ? [new() { SenderId = source, ReceiverId = recipient }]
            : [new() { SenderId = source, ReceiverId = relay }, new() { SenderId = relay, ReceiverId = recipient }],
    };
    private static TransferPlan Plan(params TransferAllocation[] allocations) => new()
    {
        Allocations = [.. allocations],
        Definition = new() { Recipients = allocations.Select(allocation => allocation.RecipientCharacterId).Distinct()
            .Select(id => new RecipientRule { CharacterId = id }).ToList() },
    };
    private static List<CollectionRouteStop> Build(TransferPlan plan, params JobCharacterSnapshot[] characters)
        => CollectionRoutePlanner.Build(plan, characters, Worlds, new Dictionary<ulong, uint>());

    [Fact]
    public void InterleavedSourcesStayTogetherByDataCenterAndWorld()
    {
        var plan = Plan(Allocation(1), Allocation(2), Allocation(3), Allocation(4), Allocation(5));
        var stops = Build(plan, Character(1, "Beta"), Character(2, "Alpha"), Character(3, "Blue"),
            Character(4, "Beta"), Character(5, "Copper"), Character(9, "Alpha"));
        Assert.Equal(new uint[] { 11, 21, 21, 22, 31 }, stops.Select(stop => stop.WorldId));
        Assert.Equal(new[] { plan.Allocations[1].Id, plan.Allocations[0].Id, plan.Allocations[3].Id, plan.Allocations[2].Id, plan.Allocations[4].Id },
            stops.Select(stop => stop.AllocationId));
    }

    [Fact]
    public void KnownCollectorWorldWinsOverHomeWorldAndBusierGroups()
    {
        var plan = Plan(Allocation(1), Allocation(2), Allocation(3), Allocation(4));
        var stops = CollectionRoutePlanner.Build(plan,
            [Character(1, "Alpha"), Character(2, "Beta"), Character(3, "Beta"), Character(4, "Blue"), Character(9, "Alpha")],
            Worlds, new Dictionary<ulong, uint> { [9] = 22 });
        Assert.Equal(new uint[] { 22, 21, 21, 11 }, stops.Select(stop => stop.WorldId));
    }

    [Fact]
    public void UnknownCurrentWorldFallsBackToCollectorHome()
    {
        var plan = Plan(Allocation(1), Allocation(2));
        var stops = CollectionRoutePlanner.Build(plan, [Character(1, "Beta"), Character(2, "Amber"), Character(9, "Amber")],
            Worlds, new Dictionary<ulong, uint> { [9] = 999 });
        Assert.Equal(new uint[] { 12, 21 }, stops.Select(stop => stop.WorldId));
    }

    [Fact]
    public void RemainingDataCentersAndWorldsUseCountsThenStableIds()
    {
        var plan = Plan(Allocation(1), Allocation(2), Allocation(3), Allocation(4), Allocation(5), Allocation(6));
        var stops = Build(plan, Character(1, "Copper"), Character(2, "Beta"), Character(3, "Blue"), Character(4, "Blue"),
            Character(5, "Amber"), Character(6, "Alpha"), Character(9, "Delta"));
        Assert.Equal(new uint[] { 22, 22, 21, 11, 12, 31 }, stops.Select(stop => stop.WorldId));
        Assert.Equal(new ulong[] { 1, 2, 3, 4, 5, 6 }, plan.Allocations.Select(allocation => allocation.SourceCharacterId));
    }

    [Fact]
    public void RecipientPriorityPrecedesCollectorTravelPreferences()
    {
        var plan = Plan(Allocation(1, 9), Allocation(2, 8), Allocation(3, 9));
        plan.Definition.Recipients = [new() { CharacterId = 8 }, new() { CharacterId = 9 }];
        var stops = Build(plan, Character(1, "Beta"), Character(2, "Copper"), Character(3, "Alpha"), Character(8, "Alpha"), Character(9, "Alpha"));
        Assert.Equal(new[] { plan.Allocations[1].Id, plan.Allocations[2].Id, plan.Allocations[0].Id }, stops.Select(stop => stop.AllocationId));
    }

    [Fact]
    public void RelayGroupsCollectBeforeDirectWorkAndUseTheirOwnStartingWorld()
    {
        var direct = Allocation(1);
        var relayA = Allocation(2, relay: 7);
        var relayB = Allocation(3, relay: 6);
        var relayAOtherDc = Allocation(4, relay: 7);
        var plan = Plan(direct, relayA, relayB, relayAOtherDc);
        var stops = Build(plan, Character(1, "Alpha"), Character(2, "Alpha"), Character(3, "Copper"), Character(4, "Beta"),
            Character(6, "Alpha"), Character(7, "Beta"), Character(9, "Alpha"));
        Assert.Equal(new[] { relayAOtherDc.Id, relayA.Id, relayB.Id, direct.Id }, stops.Select(stop => stop.AllocationId));
    }

    [Fact]
    public void UnfundedCharactersDoNotNeedWorldDataOrCreateStops()
    {
        var empty = Allocation(1, quantity: 0);
        Assert.Empty(Build(Plan(empty)));
        var funded = Allocation(2);
        var stops = Build(Plan(empty, funded), Character(2, "Alpha"), Character(9, "Alpha"));
        Assert.Equal(funded.Id, Assert.Single(stops).AllocationId);
    }

    [Fact]
    public void UnsupportedRegionsRejectDirectAndRelayCollectors()
    {
        Assert.Contains("unsupported regions", Assert.Throws<InvalidOperationException>(() =>
            Build(Plan(Allocation(1)), Character(1, "Foreign"), Character(9, "Alpha"))).Message);
        Assert.Contains("unsupported regions", Assert.Throws<InvalidOperationException>(() =>
            Build(Plan(Allocation(1, relay: 7)), Character(1, "Alpha"), Character(7, "Foreign"), Character(9, "Alpha"))).Message);
    }

    [Fact]
    public void RelayMustBeAbleToVisitFinalRecipientHome()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(Plan(Allocation(1, relay: 7)),
            Character(1, "Foreign"), Character(7, "Foreign"), Character(9, "Alpha")));
        Assert.Contains("Character 7", error.Message);
        Assert.Contains("unsupported regions", error.Message);
    }

    [Fact]
    public void OceanDestinationIsAllowedAcrossHomeRegions()
    {
        var stop = Assert.Single(Build(Plan(Allocation(1, relay: 7)),
            Character(1, "Ocean"), Character(7, "Foreign"), Character(9, "Foreign")));
        Assert.Equal(51U, stop.WorldId);
    }

    [Fact]
    public void RelayCanDeliverToOceanRecipientWithoutMakingRecipientVisitProviderRegion()
    {
        var stop = Assert.Single(Build(Plan(Allocation(1, relay: 7)),
            Character(1, "Alpha"), Character(7, "Alpha"), Character(9, "Ocean")));
        Assert.Equal(11U, stop.WorldId);
    }

    [Fact]
    public void MissingOrAmbiguousHomeWorldFailsClearly()
    {
        Assert.Contains("missing or ambiguous", Assert.Throws<InvalidOperationException>(() => Build(Plan(Allocation(1)), Character(9, "Alpha"))).Message);
        Assert.Contains("Unknown", Assert.Throws<InvalidOperationException>(() => Build(Plan(Allocation(1)), Character(1, "Unknown"), Character(9, "Alpha"))).Message);
        var worlds = Worlds.Append(new RouteWorld(99, "ALPHA", 1, "DC One", 2));
        Assert.Throws<InvalidOperationException>(() => CollectionRoutePlanner.Build(Plan(Allocation(1)),
            [Character(1, "Alpha"), Character(9, "Alpha")], worlds, new Dictionary<ulong, uint>()));
    }

    [Fact]
    public void IncompleteWorldMetadataAndMalformedLegsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => CollectionRoutePlanner.Build(Plan(Allocation(1)),
            [Character(1, "Alpha"), Character(9, "Alpha")], [new(11, "Alpha", 0, "", 2)], new Dictionary<ulong, uint>()));
        var broken = Allocation(1, relay: 7);
        broken.Legs[1].SenderId = 8;
        Assert.Contains("inconsistent", Assert.Throws<InvalidOperationException>(() =>
            Build(Plan(broken), Character(1, "Alpha"), Character(7, "Beta"), Character(9, "Alpha"))).Message);
    }
}
