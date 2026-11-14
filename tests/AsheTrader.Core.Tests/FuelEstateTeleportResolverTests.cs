using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelEstateTeleportResolverTests
{
    private static ulong House(uint world, uint territory, int ward, int plot)
        => (ulong)world << 48 | (ulong)territory << 32 | (ulong)ward << 16 | (uint)plot;
    private static int District(uint territory) => territory switch { 339 => 8, 340 => 2, 341 => 9, 641 => 111, 979 => 70, _ => 0 };

    [Fact]
    public void LoggedShiroganeEstateResolvesWithoutSharedWardAndPlotFields()
    {
        Assert.True(FuelEstateTeleportResolver.TryResolve(96, 0, 408, House(408, 641, 6, 19), District, out var estate));
        Assert.Equal(new FuelHouseAddress(408, 111, 7, 20), estate.Address);
        var entry = new FuelQueueEntry { CharacterId = 123, FreeCompanyKnown = true, FreeCompanyId = 456,
            HouseAddress = "ls-house:408:111:7:20" };
        var snapshot = new FuelAccessSnapshot { CharacterId = 123, HomeWorldId = 408, FreeCompanyId = 456,
            TeleportsComplete = true, FcTeleports = [estate],
            Cities = [new() { City = 111, AetheryteAttuned = false, HousingQuestComplete = false }] };
        Assert.Equal(FuelAccessRouteKind.DirectFcEstate, FuelAccessPlanner.Assess(entry, 408, snapshot).Kind);
        entry.HouseAddress = "ls-house:408:111:7:21";
        Assert.Equal(FuelAccessStatus.Ineligible, FuelAccessPlanner.Assess(entry, 408, snapshot).Status);
    }

    [Theory]
    [InlineData(56U, 339U)]
    [InlineData(57U, 340U)]
    [InlineData(58U, 341U)]
    [InlineData(96U, 641U)]
    [InlineData(164U, 979U)]
    public void AllFcDistrictsUseOneBasedAddresses(uint aetheryte, uint territory)
    {
        Assert.True(FuelEstateTeleportResolver.TryResolve(aetheryte, 0, 408, House(408, territory, 29, 59), District, out var estate));
        Assert.Equal(30, estate.Ward); Assert.Equal(60, estate.Plot);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    public void MissingHouseIdentityDoesNotInventFirstPlot(ulong id)
        => Assert.False(FuelEstateTeleportResolver.TryResolve(96, 0, 408, id, District, out _));

    [Theory]
    [InlineData(409U, 641U, 6, 19)]
    [InlineData(408U, 341U, 6, 19)]
    [InlineData(408U, 0U, 6, 19)]
    [InlineData(408U, 641U, 30, 19)]
    [InlineData(408U, 641U, 6, 60)]
    [InlineData(408U, 641U, 6, 128)]
    public void MismatchedOrInvalidEstateCannotAuthorizeTeleport(uint world, uint territory, int ward, int plot)
        => Assert.False(FuelEstateTeleportResolver.TryResolve(96, 0, 408, House(world, territory, ward, plot), District, out _));
}
