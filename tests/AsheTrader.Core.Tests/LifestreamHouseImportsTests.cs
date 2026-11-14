using AsheTrader.Core;
using Newtonsoft.Json;

namespace AsheTrader.Core.Tests;

public sealed class LifestreamHouseImportsTests
{
    private static LifestreamRegisteredHouse Registration(int ward = 0, int plot = 0) => new()
    { CID = 42, ResidentialDistrict = 8, Ward = ward, Plot = plot, IsPrivate = false };

    [Theory]
    [InlineData(0, 0, "ls-house:408:8:1:1")]
    [InlineData(29, 59, "ls-house:408:8:30:60")]
    [InlineData(11, 30, "ls-house:408:8:12:31")]
    public void RegisteredHousingUsesOwnersWorldAndConvertsZeroBasedNumbers(int ward, int plot, string expected)
    {
        Assert.True(LifestreamHouseImports.TryRegisteredFcHouse(42, 408, "Buyer", Registration(ward, plot), out var house));
        Assert.Equal(expected, house.Address);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(30, 0)]
    [InlineData(0, 60)]
    public void RegisteredNumbersCannotWrapOrBeMistakenForOneBasedValues(int ward, int plot)
        => Assert.False(LifestreamHouseImports.TryRegisteredFcHouse(42, 408, "Buyer", Registration(ward, plot), out _));

    [Fact]
    public void RejectsMissingConflictingAndPrivateRegistrations()
    {
        Assert.False(LifestreamHouseImports.TryRegisteredFcHouse(42, 408, "Buyer", null, out _));
        Assert.False(LifestreamHouseImports.TryRegisteredFcHouse(43, 408, "Buyer", Registration(), out _));
        Assert.False(LifestreamHouseImports.TryRegisteredFcHouse(42, 0, "Buyer", Registration(), out _));
        Assert.False(LifestreamHouseImports.TryRegisteredFcHouse(42, 408, "Buyer", new() { CID = 42 }, out _));
        var entry = Registration(); entry.IsPrivate = true;
        Assert.False(LifestreamHouseImports.TryRegisteredFcHouse(42, 408, "Buyer", entry, out _));
        entry.IsPrivate = false; entry.ResidentialDistrict = 999;
        Assert.False(LifestreamHouseImports.TryRegisteredFcHouse(42, 408, "Buyer", entry, out _));
    }

    [Fact]
    public void AddressBookValuesStayOneBasedAndApartmentsAreExcluded()
    {
        Assert.True(LifestreamHouseImports.TryAddressBookHouse("Workshop", 408, 8, 1, 0, 31, out var house));
        Assert.Equal("ls-house:408:8:1:31", house.Address);
        Assert.False(LifestreamHouseImports.TryAddressBookHouse("Apartment", 408, 8, 1, 1, 31, out _));
        Assert.False(LifestreamHouseImports.TryAddressBookHouse("Broken", 408, 8, 0, 0, 31, out _));
    }

    [Fact]
    public void MergeKeepsAddressBookLabelsAndDeduplicatesFcMembersWithoutMergingWorlds()
    {
        Assert.True(LifestreamHouseImports.TryAddressBookHouse("My workshop", 408, 8, 1, 0, 1, out var named));
        Assert.True(LifestreamHouseImports.TryRegisteredFcHouse(42, 408, "Buyer", Registration(), out var same));
        Assert.True(LifestreamHouseImports.TryRegisteredFcHouse(42, 409, "Other", Registration(), out var elsewhere));
        var houses = LifestreamHouseImports.Merge([named], [same, same, elsewhere]);
        Assert.Equal(2, houses.Count);
        Assert.Same(named, houses[0]);
        Assert.Same(elsewhere, houses[1]);
    }

    [Fact]
    public void DetachedPublicIpcTuplePreservesExplicitZeroAndFalseFields()
    {
        // Dalamud converts a provider's different return type through Newtonsoft JSON.
        const string payload = "{\"Item1\":null,\"Item2\":{\"ResidentialDistrict\":8,\"Ward\":0,\"Plot\":0,\"IsPrivate\":false,\"CID\":42,\"PathToWorkshop\":[]}}";
        var result = JsonConvert.DeserializeObject<(LifestreamRegisteredHouse? Private, LifestreamRegisteredHouse? FC)>(payload);
        Assert.Null(result.Private);
        Assert.True(LifestreamHouseImports.TryRegisteredFcHouse(42, 408, "Buyer", result.FC, out var house));
        Assert.Equal("ls-house:408:8:1:1", house.Address);
    }
}
