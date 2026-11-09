using AsheTrader.Core.Jobs;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class AutomaticRelaySelectorTests
{
    private static CharacterProfile Profile(ulong id, uint? freeSlots) => new()
    { ContentId = id, Name = $"Character {id}", World = "Test world", InventoryFreeSlots = freeSlots };

    [Fact]
    public void RanksOnlyOtherAccountByMostAutoRetainerSlotsWithStableTies()
    {
        var roster = new[]
        {
            (PeerA, Profile(1, 140)), (PeerB, Profile(5, 80)),
            (PeerB, Profile(3, 80)), (PeerB, Profile(4, 20)),
        };
        var ranked = AutomaticRelaySelector.Rank(PeerB, roster);
        Assert.Equal(new ulong[] { 3, 5, 4 }, ranked.Select(character => character.ContentId));
        Assert.Equal(ranked.Select(character => character.ContentId),
            AutomaticRelaySelector.Rank(PeerB, roster.Reverse()).Select(character => character.ContentId));
    }

    [Fact]
    public void UnknownIsNotVerifiedEmptyAndKnownZeroRemainsLastForMergeCapacity()
    {
        var ranked = AutomaticRelaySelector.Rank(PeerB,
            [(PeerB, Profile(1, null)), (PeerB, Profile(2, 0)), (PeerB, Profile(3, 8))]);
        Assert.Equal(new ulong[] { 3, 2 }, ranked.Select(character => character.ContentId));
        Assert.Equal(0U, ranked.Last().InventoryFreeSlots);
    }

    [Fact]
    public void FullCandidateIsExcludedWithoutChangingTheRemainingRanking()
    {
        var ranked = AutomaticRelaySelector.Rank(PeerB,
            [(PeerB, Profile(1, 100)), (PeerB, Profile(2, 90)), (PeerB, Profile(3, 3))], [1UL]);
        Assert.Equal(new ulong[] { 2, 3 }, ranked.Select(character => character.ContentId));
    }

    [Fact]
    public void DoesNotSelectUnknownOrEmptyCharacterIdentities()
    {
        Assert.Empty(AutomaticRelaySelector.Rank(PeerB,
            [(PeerB, Profile(0, 140)), (PeerB, Profile(1, null)), (PeerA, Profile(2, 10))]));
    }

    [Fact]
    public void RequiresRelayOnlyForFundedSameAccountAllocations()
    {
        var definition = Job([1], (2, 5));
        Assert.Equal(new[] { PeerA }, JobPlanner.RequiredRelaySourcePeers(definition,
            [Character(1, PeerA, 5), Character(2, PeerA)]));
        Assert.Empty(JobPlanner.RequiredRelaySourcePeers(definition,
            [Character(1, PeerA), Character(2, PeerA)]));
    }

    [Fact]
    public void PriorityCanConsumeStockWithoutEverNeedingTheSameAccountRoute()
    {
        var definition = Job([1], (2, 10), (3, 10));
        Assert.Empty(JobPlanner.RequiredRelaySourcePeers(definition,
            [Character(1, PeerA, 10), Character(2, PeerB), Character(3, PeerA)]));
    }

    [Fact]
    public void RestockTargetsAndProtectedStockApplyBeforeRelaySelection()
    {
        var definition = Job([1, 2], (2, 10));
        definition.QuantityMode = JobQuantityMode.RestockToTotal;
        Assert.Empty(JobPlanner.RequiredRelaySourcePeers(definition,
            [Character(1, PeerA, 100), Character(2, PeerA, 0, 10)]));
        Assert.Equal(new[] { PeerA }, JobPlanner.RequiredRelaySourcePeers(definition,
            [Character(1, PeerA, 100), Character(2, PeerA, 0, 9)]));
    }

    [Fact]
    public void PreviewIgnoresLegacyRelaySelectionWithoutMutatingTheTemplate()
    {
        var definition = Job([1], (2, 5));
        definition.RelayCharacters[PeerB] = 99;
        var needed = JobPlanner.RequiredRelaySourcePeers(definition,
            [Character(1, PeerA, 5), Character(2, PeerA)]);
        Assert.Equal(new[] { PeerA }, needed);
        Assert.Equal(99UL, definition.RelayCharacters[PeerB]);
    }

    [Fact]
    public void RelayOnlyNeedsVerifiedCarriedCapacityAndCannotDonateRetainerStock()
    {
        var definition = Job([1], (2, 5));
        definition.RelayCharacters[PeerB] = 3;
        var relay = Character(3, PeerB);
        relay.RetainerRosterVerified = false;
        relay.Retainers.Clear();
        var plan = JobPlanner.Plan(definition, [Character(1, PeerA, 5), Character(2, PeerA), relay]);
        Assert.Equal(3UL, Assert.Single(plan.Allocations).Legs[0].ReceiverId);
        Assert.False(plan.SourceBudgets.ContainsKey(3));
        Assert.False(relay.IsFullyVerified);

        relay.Verification = InventoryVerification.Cached;
        Assert.Throws<InvalidOperationException>(() => JobPlanner.Plan(definition,
            [Character(1, PeerA, 5), Character(2, PeerA), relay]));
    }

    [Fact]
    public void RelayAlsoSelectedAsProviderStillRequiresAllRetainersVerified()
    {
        var definition = Job([1, 3], (2, 5));
        definition.RelayCharacters[PeerB] = 3;
        var relay = Character(3, PeerB);
        relay.RetainerRosterVerified = false;
        Assert.Throws<InvalidOperationException>(() => JobPlanner.Plan(definition,
            [Character(1, PeerA, 5), Character(2, PeerA), relay]));
    }
}
