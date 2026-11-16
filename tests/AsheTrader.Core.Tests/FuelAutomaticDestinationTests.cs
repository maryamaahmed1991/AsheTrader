using AsheTrader.Core;
using Newtonsoft.Json;

namespace AsheTrader.Core.Tests;

public sealed class FuelAutomaticDestinationTests
{
    private static readonly HouseProfile House = new() { Name = "Workshop", Address = "ls-house:408:8:2:5" };
    private static FuelQueueEntry Buyer() => new() { CharacterId = 7, World = "Cuchulainn", FreeCompanyId = 55, FreeCompanyKnown = true };
    private static FuelAccessPlan Eligible(FuelAccessRouteKind kind = FuelAccessRouteKind.CityAethernet) => new() { Status = FuelAccessStatus.Eligible, Kind = kind };

    [Fact]
    public void CandidatePoolIsSameWorldValidAndDeduplicated()
    {
        var other = new HouseProfile { Address = "ls-house:407:8:2:5" };
        var broken = new HouseProfile { Address = "invalid" };
        var duplicate = new HouseProfile { Address = House.Address };
        var result = FuelAutomaticDestination.Candidates(Buyer(), 408, [House, other, broken, duplicate]);
        Assert.Equal(6, result.Count);
        Assert.Equal(House.Address, Assert.Single(result, c => !c.UseCaretaker).HouseAddress);
        Assert.All(result, c => { Assert.Equal(7UL, c.CharacterId); Assert.Equal(55UL, c.FreeCompanyId); });
        Assert.Empty(FuelAutomaticDestination.Candidates(Buyer(), 0, [House]));
    }

    [Theory]
    [InlineData(FuelAccessStatus.Unknown)]
    [InlineData(FuelAccessStatus.Ineligible)]
    public void UnknownOrUnavailableAccessDoesNotDispatchTravel(FuelAccessStatus status)
    {
        Assert.Null(FuelAutomaticDestination.Choose(FuelAutomaticDestination.Candidates(Buyer(), 408, [House]),
            _ => new() { Status = status }, House.Address));
    }

    [Fact]
    public void ExistingLocationWinsEvenOverPreviousSuccess()
    {
        var choice = FuelAutomaticDestination.Choose(FuelAutomaticDestination.Candidates(Buyer(), 408, [House]),
            e => Eligible(e.UseCaretaker && e.CaretakerCity == 9 ? FuelAccessRouteKind.AlreadyThere : FuelAccessRouteKind.DirectFcEstate), House.Address)!;
        Assert.Equal("caretaker:9", FuelAutomaticDestination.Key(choice.Destination));
    }

    [Fact]
    public void PreviousSuccessRequiresCurrentEligibility()
    {
        var choices = FuelAutomaticDestination.Candidates(Buyer(), 408, [House]);
        Assert.Equal("caretaker:9", FuelAutomaticDestination.Key(FuelAutomaticDestination.Choose(choices, _ => Eligible(), "caretaker:9")!.Destination));
        Assert.Equal(House.Address, FuelAutomaticDestination.Choose(choices,
            e => e.CaretakerCity == 9 ? new() { Status = FuelAccessStatus.Ineligible } : Eligible(), "caretaker:9")!.Destination.HouseAddress);
    }

    [Fact]
    public void DirectEstateHouseBeatsCityAndCaretakerRoutes()
    {
        var choice = FuelAutomaticDestination.Choose(FuelAutomaticDestination.Candidates(Buyer(), 408, [House]),
            _ => Eligible(FuelAccessRouteKind.DirectFcEstate))!;
        Assert.False(choice.Destination.UseCaretaker);
    }

    [Fact]
    public void AccessibleCaretakerIsChosenWhenEveryHouseIsBlocked()
    {
        var choice = FuelAutomaticDestination.Choose(FuelAutomaticDestination.Candidates(Buyer(), 408, [House]),
            e => e.UseCaretaker && e.CaretakerCity == 111 ? Eligible() : new() { Status = FuelAccessStatus.Ineligible })!;
        Assert.Equal(111, choice.Destination.CaretakerCity);
    }

    [Fact]
    public void ApplyOnlyChangesDestinationAndExplanation()
    {
        var entry = Buyer(); entry.Purchased = 99; entry.CreditReserve = 123; entry.AutomaticDestination = true;
        FuelAutomaticDestination.Apply(entry, FuelAutomaticDestination.Choose(FuelAutomaticDestination.Candidates(entry, 408, [House]), _ => Eligible())!);
        Assert.Equal(99UL, entry.Purchased); Assert.Equal(123U, entry.CreditReserve); Assert.True(entry.AutomaticDestination);
        Assert.Equal(House.Id, entry.HouseId); Assert.NotEmpty(entry.DestinationReason);
    }

    [Fact]
    public void AutoSetupQueuesWithoutAnOldHouseButHonorsCharacterOverride()
    {
        var character = new CharacterProfile { ContentId = 7, Name = "Buyer", World = "Cuchulainn", FreeCompanyId = 55, Fuel = new() { Enabled = true, ExhaustCredits = true } };
        var settings = new FuelQueueSettings(); settings.Companies[FuelQueuePlanner.CompanyKey(character)] = new() { AutomaticDestination = true };
        var auto = Assert.Single(FuelQueuePlanner.Build([character], [], settings, _ => 408));
        Assert.True(auto.AutomaticDestination); Assert.Equal(FuelQueueEntryStatus.Queued, auto.Status);
        character.Fuel.HouseId = House.Id;
        var manual = Assert.Single(FuelQueuePlanner.Build([character], [House], settings, _ => 408));
        Assert.False(manual.AutomaticDestination); Assert.Equal(House.Address, manual.HouseAddress);
    }

    [Fact]
    public void OldSettingsAndInterruptedEntriesDoNotBecomeAutomatic()
    {
        Assert.False(JsonConvert.DeserializeObject<FuelCompanyRule>("{\"UseCaretaker\":true}")!.AutomaticDestination);
        Assert.False(JsonConvert.DeserializeObject<FuelQueueEntry>("{\"Purchased\":99}")!.AutomaticDestination);
    }
}
