using AsheTrader.Core;
using Newtonsoft.Json;

namespace AsheTrader.Core.Tests;

public sealed class FuelAccessPlanningTests
{
    private static FuelQueueEntry Buyer(bool caretaker = false) => new()
    {
        CharacterId = 42, World = "Faerie", FreeCompanyId = 123, FreeCompanyKnown = true,
        HouseAddress = "ls-house:54:8:9:33", UseCaretaker = caretaker, CaretakerCity = 8,
    };

    private static FuelEstateTeleport Estate(int city = 8, int ward = 9, int plot = 33) => new()
    {
        AetheryteId = city switch { 8 => 56, 2 => 57, 9 => 58, 111 => 96, 70 => 164, _ => 0 },
        City = city, World = 54, Ward = ward, Plot = plot,
    };

    private static FuelAccessSnapshot Snapshot(bool attuned = false, bool unlocked = false, params FuelEstateTeleport[] estates) => new()
    {
        CharacterId = 42, HomeWorldId = 54, FreeCompanyId = 123, TeleportsComplete = true,
        ObservedAtUtc = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero),
        Cities = FuelAccessPlanner.Cities.Select(c => new FuelCityAccess
        { City = c, AetheryteAttuned = attuned, HousingQuestComplete = unlocked }).ToArray(),
        FcTeleports = estates,
    };

    [Fact]
    public void ExactFcEstateBypassesMissingCityAttunementAndHousingQuest()
    {
        var plan = FuelAccessPlanner.Assess(Buyer(), 54, Snapshot(estates: [Estate()]));
        Assert.Equal(FuelAccessStatus.Eligible, plan.Status);
        Assert.Equal(FuelAccessRouteKind.DirectFcEstate, plan.Kind);
        Assert.Equal(Estate(), plan.Teleport);
    }

    [Fact]
    public void DirectEstatePreferredEvenWhenCityRouteIsUnlocked()
        => Assert.Equal(FuelAccessRouteKind.DirectFcEstate,
            FuelAccessPlanner.Assess(Buyer(), 54, Snapshot(true, true, Estate())).Kind);

    [Theory]
    [InlineData(8, 10, 33)]
    [InlineData(8, 9, 32)]
    [InlineData(2, 9, 33)]
    public void AnotherFcPlotDoesNotGrantTravelToConfiguredWorkshop(int city, int ward, int plot)
    {
        var plan = FuelAccessPlanner.Assess(Buyer(), 54, Snapshot(estates: [Estate(city, ward, plot)]));
        Assert.Equal(FuelAccessStatus.Ineligible, plan.Status);
        Assert.Null(plan.Teleport);
        Assert.Equal(city, plan.RecommendedCaretakerCity);
    }

    [Fact]
    public void CaretakerCanUseAnFcEstateInAnyWardOfTheConfiguredDistrict()
    {
        var plan = FuelAccessPlanner.Assess(Buyer(true), 54, Snapshot(estates: [Estate(8, 30, 60)]));
        Assert.Equal(FuelAccessRouteKind.DirectFcEstate, plan.Kind);
        Assert.Equal(30, plan.Teleport!.Ward);
        Assert.Contains("caretaker", plan.RouteLabel);
    }

    [Fact]
    public void CaretakerDoesNotSilentlyChangeDistrict()
    {
        var plan = FuelAccessPlanner.Assess(Buyer(true), 54, Snapshot(estates: [Estate(111)]));
        Assert.Equal(FuelAccessStatus.Ineligible, plan.Status);
        Assert.Equal(111, plan.RecommendedCaretakerCity);
        Assert.Null(plan.Teleport);
    }

    [Theory]
    [InlineData(true, true, FuelAccessStatus.Eligible)]
    [InlineData(false, true, FuelAccessStatus.Ineligible)]
    [InlineData(true, false, FuelAccessStatus.Ineligible)]
    [InlineData(false, false, FuelAccessStatus.Ineligible)]
    public void CityRouteRequiresBothActualAttunementAndHousingQuest(bool attuned, bool unlocked, FuelAccessStatus expected)
    {
        var plan = FuelAccessPlanner.Assess(Buyer(), 54, Snapshot(attuned, unlocked));
        Assert.Equal(expected, plan.Status);
        if (expected == FuelAccessStatus.Eligible) Assert.Equal(FuelAccessRouteKind.CityAethernet, plan.Kind);
    }

    [Fact]
    public void NeverUsesTheCurrentlyLoggedInCharactersAccessForAnOfflineAlt()
    {
        var snapshot = Snapshot(true, true, Estate()) with { CharacterId = 99 };
        Assert.Equal(FuelAccessStatus.Unknown, FuelAccessPlanner.Assess(Buyer(), 54, snapshot).Status);
    }

    [Fact]
    public void ChangedWorldOrFcAndOldFormatsInvalidateCachedEligibility()
    {
        var snapshot = Snapshot(true, true, Estate());
        Assert.Equal(FuelAccessStatus.Unknown, FuelAccessPlanner.Assess(Buyer(), 408, snapshot).Status);
        Assert.Equal(FuelAccessStatus.Unknown, FuelAccessPlanner.Assess(Buyer(), 54, snapshot with { FreeCompanyId = 456 }).Status);
        Assert.Equal(FuelAccessStatus.Unknown, FuelAccessPlanner.Assess(Buyer(), 54, snapshot with { Format = 99 }).Status);
        Assert.Equal(FuelAccessStatus.Unknown, FuelAccessPlanner.Assess(Buyer(), 54, null).Status);
    }

    [Fact]
    public void PartialOrLoadingStateIsUnknownRatherThanIneligible()
    {
        Assert.Equal(FuelAccessStatus.Unknown, FuelAccessPlanner.Assess(Buyer(), 54, Snapshot() with { TeleportsComplete = false }).Status);
        Assert.Equal(FuelAccessStatus.Unknown, FuelAccessPlanner.Assess(Buyer(), 54, Snapshot() with { Cities = [] }).Status);
        Assert.Equal(FuelAccessStatus.Unknown, FuelAccessPlanner.Assess(Buyer(), 54, Snapshot() with
        { Cities = [new() { City = 8, AetheryteAttuned = null, HousingQuestComplete = false }] }).Status);
    }

    [Fact]
    public void ACompleteCityRouteDoesNotNeedACompleteEstateList()
        => Assert.Equal(FuelAccessStatus.Eligible,
            FuelAccessPlanner.Assess(Buyer(), 54, Snapshot(true, true) with { TeleportsComplete = false }).Status);

    [Fact]
    public void UnsupportedPrivateOrSharedEstateRouteIsUnknownRatherThanIneligible()
    {
        var snapshot = Snapshot() with { OtherEstateCities = [8] };
        var plan = FuelAccessPlanner.Assess(Buyer(true), 54, snapshot);
        Assert.Equal(FuelAccessStatus.Unknown, plan.Status);
        Assert.Equal(FuelAccessRouteKind.Unavailable, plan.Kind);
        Assert.Contains("private, shared or apartment", plan.Reason);
        Assert.Equal(FuelAccessRouteKind.CityAethernet,
            FuelAccessPlanner.Assess(Buyer(true), 54, Snapshot(true, true) with { OtherEstateCities = [8] }).Kind);
        Assert.Equal(FuelAccessRouteKind.Unknown,
            FuelAccessPlanner.Assess(Buyer(true), 54, snapshot with { TeleportsComplete = false }).Kind);
    }

    [Fact]
    public void InvalidOrOtherWorldEstateEntryCannotAuthorizeTravel()
    {
        foreach (var teleport in new[] { Estate() with { World = 408 }, Estate() with { Ward = 0 },
            Estate() with { Plot = 61 }, Estate() with { AetheryteId = 59 } })
            Assert.NotEqual(FuelAccessRouteKind.DirectFcEstate,
                FuelAccessPlanner.Assess(Buyer(), 54, Snapshot(estates: [teleport])).Kind);
    }

    [Fact]
    public void SnapshotRoundTripsAndRetainsCharacterSpecificEvidence()
    {
        var before = Snapshot(true, true, Estate());
        var after = JsonConvert.DeserializeObject<FuelAccessSnapshot>(JsonConvert.SerializeObject(before))!;
        Assert.Equal(before.CharacterId, after.CharacterId);
        Assert.Equal(before.ObservedAtUtc, after.ObservedAtUtc);
        Assert.Equal(before.Cities, after.Cities);
        Assert.Equal(before.FcTeleports, after.FcTeleports);
        Assert.Equal(FuelAccessRouteKind.DirectFcEstate, FuelAccessPlanner.Assess(Buyer(), 54, after).Kind);
    }

    [Theory]
    [InlineData(2, 66748)]
    [InlineData(9, 66749)]
    [InlineData(8, 66750)]
    [InlineData(111, 68167)]
    [InlineData(70, 69708)]
    public void QuestMappingMatchesInstalledGameData(int city, uint quest)
        => Assert.Equal(quest, FuelAccessPlanner.HousingQuestId(city));
}
