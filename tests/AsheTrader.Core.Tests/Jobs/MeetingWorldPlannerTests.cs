using AsheTrader.Core.Jobs;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class MeetingWorldPlannerTests
{
    private static readonly MeetingWorldOption[] Worlds = [new(1, "Alpha"), new(2, "Beta")];
    private static JobCharacterSnapshot Character(ulong id, string world) => new() { CharacterId = id, World = world };
    private static TransferAllocation Allocation(ulong source, ulong recipient, ulong relay = 0) => new()
    {
        SourceCharacterId = source, RecipientCharacterId = recipient, Quantity = 300,
        Legs = relay == 0 ? [new() { SenderId = source, ReceiverId = recipient }]
            : [new() { SenderId = source, ReceiverId = relay }, new() { SenderId = relay, ReceiverId = recipient }]
    };

    [Fact]
    public void MajorityFundedHomeWorldReducesDistinctWorldTravelers()
    {
        var plan = new TransferPlan { Allocations = [Allocation(1, 4, 5), Allocation(2, 4, 5), Allocation(3, 4, 5)] };
        var choice = MeetingWorldPlanner.Choose(2, Worlds, plan,
            [Character(1, "Alpha"), Character(2, "Alpha"), Character(3, "Alpha"), Character(4, "Alpha"), Character(5, "Beta")]);
        Assert.Equal(1U, choice.WorldId);
        Assert.Equal(1, choice.WorldTravelers);
        Assert.Equal(4, choice.ConfiguredWorldTravelers);
        Assert.Equal(5, choice.ActiveCharacters);
    }

    [Fact]
    public void UnfundedCharactersAndRepeatedRelayLegsDoNotDistortCost()
    {
        var plan = new TransferPlan { Allocations = [Allocation(1, 2, 3), Allocation(1, 2, 3)] };
        var characters = Enumerable.Range(4, 50).Select(id => Character((ulong)id, "Beta"))
            .Concat([Character(1, "Alpha"), Character(2, "Alpha"), Character(3, "Beta")]);
        var choice = MeetingWorldPlanner.Choose(2, Worlds, plan, characters);
        Assert.Equal(1U, choice.WorldId);
        Assert.Equal(3, choice.ActiveCharacters);
        Assert.Equal(1, choice.WorldTravelers);
    }

    [Fact]
    public void TieAndEmptyPlanKeepConfiguredWorld()
    {
        var plan = new TransferPlan { Allocations = [Allocation(1, 2)] };
        Assert.Equal(2U, MeetingWorldPlanner.Choose(2, Worlds, plan, [Character(1, "Alpha"), Character(2, "Beta")]).WorldId);
        Assert.Equal(2U, MeetingWorldPlanner.Choose(2, Worlds, new(), []).WorldId);
    }

    [Fact]
    public void CandidateBoundaryDoesNotInventAnotherWorld()
    {
        var plan = new TransferPlan { Allocations = [Allocation(1, 2)] };
        var choice = MeetingWorldPlanner.Choose(1, [Worlds[0]], plan, [Character(1, "Elsewhere"), Character(2, "Elsewhere")]);
        Assert.Equal(1U, choice.WorldId);
        Assert.Equal(2, choice.WorldTravelers);
    }

    [Fact]
    public void FundingPreviewWithoutRelayLegsCanChooseAnAnchorBeforeRelaySelection()
    {
        var plan = new TransferPlan { Allocations = [new() { SourceCharacterId = 1, RecipientCharacterId = 2, Quantity = 300 }] };
        var choice = MeetingWorldPlanner.Choose(2, Worlds, plan, [Character(1, "alpha"), Character(2, "Alpha")]);
        Assert.Equal(1U, choice.WorldId);
        Assert.Equal(0, choice.WorldTravelers);
    }
}
