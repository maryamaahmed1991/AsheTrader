using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class HousingAddressResolverTests
{
    private static int District(uint territory) => territory switch { 339 or 423 => 8, 340 => 2, 341 => 9, 641 => 111, 979 => 70, _ => 0 };
    private static ulong Id(int world, uint territory, int ward, int plot, int room = 0)
        => ((ulong)world << 48) | ((ulong)territory << 32) | ((ulong)(ward | room << 6) << 16) | (uint)plot;

    [Theory]
    [InlineData(339U, 8)]
    [InlineData(340U, 2)]
    [InlineData(341U, 9)]
    [InlineData(641U, 111)]
    [InlineData(979U, 70)]
    public void WorkshopUsesItsDedicatedHouseIdWhenGenericGettersAreUnavailable(uint territory, int city)
    {
        var observed = new HousingLocationObservation(408, 423, 0, HousingLocationKind.Workshop,
            Id(408, territory, 29, 59, 0x3FF), ulong.MaxValue, -1, -1);
        Assert.True(HousingAddressResolver.TryResolve(observed, District, out var address, out _));
        Assert.Equal(new FuelHouseAddress(408, city, 30, 60), address);
    }

    [Fact]
    public void PackedEstateDistrictWinsOverRenovatedInteriorOrStaleGenericOriginal()
    {
        var observed = new HousingLocationObservation(408, 641, 641, HousingLocationKind.Indoor,
            Id(408, 339, 0, 0), 0, 0, 0);
        Assert.True(HousingAddressResolver.TryResolve(observed, District, out var address, out _));
        Assert.Equal(new FuelHouseAddress(408, 8, 1, 1), address);
    }

    [Fact]
    public void WorkshopRoomBitsNeverBecomeWardBits()
    {
        var observed = new HousingLocationObservation(408, 423, 0, HousingLocationKind.Workshop,
            Id(408, 340, 5, 30, 0x3FF), 0, -1, -1);
        Assert.True(HousingAddressResolver.TryResolve(observed, District, out var address, out _));
        Assert.Equal(new FuelHouseAddress(408, 2, 6, 31), address);
    }

    [Fact]
    public void EmptyHouseIdentityCannotInventFirstPlotFromWorkshopTerritory()
    {
        var observed = new HousingLocationObservation(408, 423, 0, HousingLocationKind.Workshop, 0, ulong.MaxValue, 0, 0);
        Assert.False(HousingAddressResolver.TryResolve(observed, District, out _, out _));
    }

    [Fact]
    public void WorkshopCanUseItsCurrentDistrictWhenNonemptyHouseIdentityHasNoTerritory()
    {
        var observed = new HousingLocationObservation(408, 423, 0, HousingLocationKind.Workshop,
            Id(408, 0, 5, 6, 0x3FF), 0, -1, -1);
        Assert.True(HousingAddressResolver.TryResolve(observed, District, out var address, out _));
        Assert.Equal(new FuelHouseAddress(408, 8, 6, 7), address);
    }

    [Fact]
    public void OriginalTerritoryRemainsFallbackWhenThePackedTerritoryIsUnavailable()
    {
        var observed = new HousingLocationObservation(408, 641, 339, HousingLocationKind.Indoor,
            Id(408, 0, 5, 6), 0, -1, -1);
        Assert.True(HousingAddressResolver.TryResolve(observed, District, out var address, out _));
        Assert.Equal(new FuelHouseAddress(408, 8, 6, 7), address);
    }

    [Fact]
    public void KnownWorldMismatchCannotFallBackToNativeCoordinates()
    {
        var observed = new HousingLocationObservation(408, 423, 339, HousingLocationKind.Workshop,
            Id(409, 339, 0, 0, 0x3FF), Id(408, 339, 0, 0), 0, 0);
        Assert.False(HousingAddressResolver.TryResolve(observed, District, out _, out _));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(255)]
    public void ApartmentUnitIsNeverAcceptedAsAWorkshopPlot(int unit)
    {
        var observed = new HousingLocationObservation(408, 423, 339, HousingLocationKind.Workshop,
            Id(408, 339, 0, unit), 0, 0, 0);
        Assert.False(HousingAddressResolver.TryResolve(observed, District, out _, out _));
    }

    [Fact]
    public void OutsideUsesLiveTerritoryAndCoordinatesInsteadOfRetainedHouseId()
    {
        var observed = new HousingLocationObservation(408, 340, 339, HousingLocationKind.Outdoor,
            Id(408, 339, 5, 6), Id(408, 339, 5, 6), 3, 45);
        Assert.True(HousingAddressResolver.TryResolve(observed, District, out var address, out _));
        Assert.Equal(new FuelHouseAddress(408, 2, 4, 46), address);
    }

    [Fact]
    public void OutsideNotStandingOnPlotCannotReuseOldPlot()
    {
        var observed = new HousingLocationObservation(408, 340, 339, HousingLocationKind.Outdoor,
            Id(408, 339, 5, 6), Id(408, 339, 5, 6), 3, -1);
        Assert.False(HousingAddressResolver.TryResolve(observed, District, out _, out _));
    }
}
