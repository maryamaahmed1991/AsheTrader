using System.Numerics;
using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelHouseArrivalPolicyTests
{
    private static readonly FuelHouseAddress Destination = new(54, 9, 12, 17);
    private static readonly Vector3 Gate = new(100, 8, -40);

    [Fact]
    public void LifestreamDropOffOutsideTheYardCanStartTheDoorApproach()
    {
        // The reported run reached the Goblet on Faerie, then never left WaitTravel.
        // At a plot entrance the native plot can remain -1 until the player walks into the yard.
        Assert.True(CanApproach(player: new(103, 8, -36), plot: -1));
    }

    [Theory]
    [InlineData(9.9f, true)]
    [InlineData(10f, true)]
    [InlineData(10.1f, false)]
    public void HandoffRequiresProximityToTheSpecificPlotAnchor(float distance, bool accepted)
        => Assert.Equal(accepted, CanApproach(player: Gate + new Vector3(distance, 0, 0)));

    [Fact]
    public void BeingOnTheIntendedPlotAlsoAllowsTheGateHandoff()
        => Assert.True(CanApproach(plot: 16));

    [Fact]
    public void KnownNeighborPlotCannotBeAcceptedEvenAtTheSameAnchor()
        => Assert.False(CanApproach(plot: 17));

    [Theory]
    [InlineData(55, 54, 9, 11)]
    [InlineData(54, 55, 9, 11)]
    [InlineData(54, 54, 8, 11)]
    [InlineData(54, 54, 9, 12)]
    public void MatchingCoordinatesCannotOverrideWorldDistrictOrWard(int currentWorld, int homeWorld, int district, int ward)
        => Assert.False(FuelHouseArrivalPolicy.CanApproachFromGate(Destination,
            currentWorld, homeWorld, district, ward, -1, true, Gate, Gate));

    [Fact]
    public void InteriorCannotUseTheWeakerOutdoorArrivalCheck()
        => Assert.False(FuelHouseArrivalPolicy.CanApproachFromGate(Destination,
            54, 54, 9, 11, 16, false, Gate, Gate));

    [Fact]
    public void UnavailableAnchorCannotAuthorizeAnUnknownPlot()
        => Assert.False(FuelHouseArrivalPolicy.CanApproachFromGate(Destination,
            54, 54, 9, 11, -1, true, Gate, null));

    [Fact]
    public void AnAnchorFromAnotherDropOffCannotAuthorizeTheCurrentPosition()
        => Assert.False(FuelHouseArrivalPolicy.CanApproachFromGate(Destination,
            54, 54, 9, 11, -1, true, Gate, new Vector3(400, 8, 250)));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonfinitePlayerOrAnchorCoordinatesNeverCountAsArrival(float invalid)
    {
        Vector3[] invalidPositions = [new(invalid, 8, -40), new(100, invalid, -40), new(100, 8, invalid)];
        foreach (var position in invalidPositions)
        {
            Assert.False(CanApproach(player: position));
            Assert.False(FuelHouseArrivalPolicy.CanApproachFromGate(Destination,
                54, 54, 9, 11, -1, true, Gate, position));
        }
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-128)]
    [InlineData(60)]
    public void OnlyTheKnownNoPlotSentinelCanUseTheGateFallback(int nativePlot)
        => Assert.False(CanApproach(plot: nativePlot));

    [Theory]
    [InlineData(0, 9, 12, 17)]
    [InlineData(65536, 9, 12, 17)]
    [InlineData(54, 0, 12, 17)]
    [InlineData(54, 9, 0, 17)]
    [InlineData(54, 9, 31, 17)]
    [InlineData(54, 9, 12, 0)]
    [InlineData(54, 9, 12, 61)]
    public void InvalidDestinationCannotMatchEvenWithCorrespondingObservedValues(int world, int district, int ward, int plot)
        => Assert.False(FuelHouseArrivalPolicy.CanApproachFromGate(new(world, district, ward, plot),
            world, world, district, ward - 1, -1, true, Gate, Gate));

    [Theory]
    [InlineData(31, 30)]
    [InlineData(60, 59)]
    public void SubdivisionPlotsKeepTheirFullZeroBasedNativeIndex(int displayedPlot, int nativePlot)
        => Assert.True(FuelHouseArrivalPolicy.CanApproachFromGate(new(54, 9, 30, displayedPlot),
            54, 54, 9, 29, nativePlot, true, Gate, Gate));

    private static bool CanApproach(Vector3? player = null, int plot = -1)
        => FuelHouseArrivalPolicy.CanApproachFromGate(Destination,
            54, 54, 9, 11, plot, true, player ?? Gate, Gate);
}
