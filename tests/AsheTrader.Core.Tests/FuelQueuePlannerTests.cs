using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelQueuePlannerTests
{
    private static readonly HouseProfile House = new() { Name = "Workshop", Address = "ls-house:408:8:2:5" };
    private static uint? World(string name) => name.Equals("Cuchulainn", StringComparison.OrdinalIgnoreCase) ? 408U
        : name.Equals("Seraph", StringComparison.OrdinalIgnoreCase) ? 407U : null;

    private static CharacterProfile Character(ulong id, string name, ulong? fc = 55) => new()
    {
        ContentId = id, Name = name, World = "Cuchulainn", FreeCompanyId = fc,
        Fuel = new() { Enabled = true, ExhaustCredits = true, HouseId = House.Id },
    };

    [Fact]
    public void OnlySelectedCharactersAreQueuedWithoutUsingCachedCreditsAsAuthority()
    {
        var selected = Character(1, "Selected"); selected.FreeCompanyCredits = 0;
        var disabled = Character(2, "Disabled"); disabled.Fuel.Enabled = false;
        var result = FuelQueuePlanner.Build([disabled, selected], [House], new(), World);
        Assert.Equal(1UL, Assert.Single(result).CharacterId);
        Assert.Equal(FuelQueueEntryStatus.Queued, result[0].Status);
    }

    [Fact]
    public void GroupsWorldThenCompanyThenExplicitCharacterOrder()
    {
        var second = Character(1, "A", 55); second.Fuel.QueueOrder = 2;
        var first = Character(2, "Z", 55); first.Fuel.QueueOrder = 1;
        var otherCompany = Character(3, "B", 66);
        var otherWorld = Character(4, "A", 12); otherWorld.World = "Seraph";
        var result = FuelQueuePlanner.Build([otherWorld, second, otherCompany, first], [House], new(), World);
        Assert.Equal(new ulong[] { 2, 1, 3, 4 }, result.Select(x => x.CharacterId));
    }

    [Fact]
    public void UnknownMembershipsStaySeparateAndAreCheckedAtRuntime()
    {
        var a = Character(1, "A", null); var b = Character(2, "B", null);
        Assert.NotEqual(FuelQueuePlanner.CompanyKey(a), FuelQueuePlanner.CompanyKey(b));
        var rows = FuelQueuePlanner.Build([a, b], [House], new(), World);
        Assert.All(rows, row => { Assert.False(row.FreeCompanyKnown); Assert.Equal(FuelQueueEntryStatus.Queued, row.Status); Assert.Contains("after login", row.Note); });
    }

    [Fact]
    public void KnownNoCompanySkipsWithoutBlockingUsableCompanies()
    {
        var result = FuelQueuePlanner.Build([Character(1, "None", 0), Character(2, "Member")], [House], new(), World);
        Assert.Equal(FuelQueueEntryStatus.Skipped, result.Single(x => x.CharacterId == 1).Status);
        Assert.Equal(FuelQueueEntryStatus.Queued, result.Single(x => x.CharacterId == 2).Status);
    }

    [Fact]
    public void UsesCompanyDestinationAndReserveUnlessCharacterHasDestinationOverride()
    {
        var a = Character(1, "A"); a.Fuel.HouseId = null;
        var own = new HouseProfile { Name = "Another", Address = "ls-house:408:2:1:1" };
        var b = Character(2, "B"); b.Fuel.HouseId = own.Id;
        var settings = new FuelQueueSettings();
        settings.Companies[FuelQueuePlanner.CompanyKey(a)] = new() { NameOverride = "Friendly FC", HouseId = House.Id, CreditReserve = 12345 };
        var rows = FuelQueuePlanner.Build([a, b], [House, own], settings, World);
        Assert.Equal(House.Id, rows[0].HouseId); Assert.Equal(own.Id, rows[1].HouseId);
        Assert.All(rows, row => { Assert.Equal("Friendly FC", row.FreeCompanyName); Assert.Equal(12345U, row.CreditReserve); });
    }

    [Fact]
    public void RejectsDifferentWorldHouseEvenWhenItExists()
    {
        var character = Character(1, "A"); character.World = "Seraph";
        var row = Assert.Single(FuelQueuePlanner.Build([character], [House], new(), World));
        Assert.Equal(FuelQueueEntryStatus.Skipped, row.Status);
        Assert.Contains("home world", row.Note);
    }

    [Fact]
    public void RemovedHouseDoesNotFallbackToAnUnselectedDestination()
    {
        var row = Assert.Single(FuelQueuePlanner.Build([Character(1, "A")], [], new(), World));
        Assert.Equal(FuelQueueEntryStatus.Skipped, row.Status);
        Assert.Contains("missing", row.Note);
    }

    [Fact]
    public void QueueSnapshotsPolicyHouseAndCompanySettings()
    {
        var character = Character(1, "A"); character.Fuel.KeepCarried = 999;
        var house = new HouseProfile { Id = House.Id, Name = House.Name, Address = House.Address };
        var settings = new FuelQueueSettings();
        settings.Companies[FuelQueuePlanner.CompanyKey(character)] = new() { CreditReserve = 500 };
        var row = Assert.Single(FuelQueuePlanner.Build([character], [house], settings, World));
        character.Name = "Changed"; character.Fuel.KeepCarried = 0; house.Address = "invalid";
        settings.Companies[row.CompanyKey].CreditReserve = 999;
        Assert.Equal("A", row.CharacterName); Assert.Equal(999U, row.KeepCarried);
        Assert.Equal(House.Address, row.HouseAddress); Assert.Equal(500U, row.CreditReserve);
    }

    [Fact]
    public void LegacyFuelPoliciesKeepThresholdModeAndGainDepositDefault()
    {
        var policy = System.Text.Json.JsonSerializer.Deserialize<FuelPolicy>("{\"Enabled\":true,\"BuyBelow\":100,\"TargetQuantity\":500}")!;
        Assert.False(policy.ExhaustCredits); Assert.True(policy.DepositToRetainers);
        Assert.Equal(0U, policy.CreditReserve); Assert.Equal(100U, policy.BuyBelow);
    }

    [Theory]
    [InlineData("ls-house:408:8:1:1", true)]
    [InlineData("ls-house:65535:111:30:60", true)]
    [InlineData("ls-house:0:8:1:1", false)]
    [InlineData("ls-house:408:8:31:1", false)]
    [InlineData("ls-house:408:8:1:61", false)]
    [InlineData("ls-house:408:123:1:1", false)]
    [InlineData("ls-house:408:8:-1:1", false)]
    [InlineData("ls-apartment:408:8:1:1", false)]
    public void HouseAddressesValidateWorldDistrictAndOneBasedLimits(string address, bool valid)
        => Assert.Equal(valid, FuelHouseAddress.TryParse(address, out _));

    [Fact]
    public void DuplicateCharactersAreNeverScheduledTwice()
    {
        var rows = FuelQueuePlanner.Build([Character(1, "A"), Character(1, "A duplicate")], [House], new(), World);
        Assert.Single(rows, row => row.Status == FuelQueueEntryStatus.Queued);
        Assert.Single(rows, row => row.Status == FuelQueueEntryStatus.Skipped);
    }

    [Fact]
    public void CaretakerAllowsNoRegisteredHouseOnTheHomeWorld()
    {
        var character = Character(1, "A"); character.Fuel.HouseId = null;
        var settings = new FuelQueueSettings();
        settings.Companies[FuelQueuePlanner.CompanyKey(character)] = new() { UseCaretaker = true, CaretakerCity = 111 };
        var row = Assert.Single(FuelQueuePlanner.Build([character], [], settings, World));
        Assert.True(row.UseCaretaker); Assert.Null(row.HouseId);
        Assert.Equal(111, row.CaretakerCity); Assert.Equal(FuelQueueEntryStatus.Queued, row.Status);
    }

    [Fact]
    public void ExplicitCharacterHouseOverridesCompanyCaretaker()
    {
        var character = Character(1, "A");
        var settings = new FuelQueueSettings();
        settings.Companies[FuelQueuePlanner.CompanyKey(character)] = new() { UseCaretaker = true };
        var row = Assert.Single(FuelQueuePlanner.Build([character], [House], settings, World));
        Assert.False(row.UseCaretaker); Assert.Equal(House.Id, row.HouseId);
        Assert.Equal(FuelQueueEntryStatus.Queued, row.Status);
    }

    [Fact]
    public void CaretakerStillRequiresARealWorldAndSupportedDistrict()
    {
        var character = Character(1, "A"); character.Fuel.HouseId = null;
        var settings = new FuelQueueSettings();
        settings.Companies[FuelQueuePlanner.CompanyKey(character)] = new() { UseCaretaker = true, CaretakerCity = 999 };
        var invalidDistrict = Assert.Single(FuelQueuePlanner.Build([character], [], settings, World));
        Assert.Equal(FuelQueueEntryStatus.Skipped, invalidDistrict.Status);
        settings.Companies[FuelQueuePlanner.CompanyKey(character)].CaretakerCity = 8;
        var invalidWorld = Assert.Single(FuelQueuePlanner.Build([character], [], settings, _ => null));
        Assert.Equal(FuelQueueEntryStatus.Skipped, invalidWorld.Status);
    }
}
