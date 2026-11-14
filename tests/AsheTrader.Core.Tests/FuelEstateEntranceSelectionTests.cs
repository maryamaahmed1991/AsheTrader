using System.Numerics;
using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelEstateEntranceSelectionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(60)]
    public void UsesTheDisplayedPlotAcrossBothDivisions(int plot)
    {
        var markers = Markers();
        var door = Door(17, markers[plot - 1] + new Vector3(3, -12, 4));

        AssertSelected(plot, markers, [door], 17);
    }

    [Fact]
    public void ElevatedMapAnchorDoesNotBecomeTheNavigationDestination()
    {
        var markers = Markers();
        // Map marker elevation is not the door's ground elevation. Selection returns
        // the live object identity; the runner navigates to that object's position.
        var door = Door(42, new(103, 8, 104));

        AssertSelected(1, markers, [door], 42);
    }

    [Fact]
    public void NeighborDoorDoesNotDisplaceTheDoorOfTheSelectedPlot()
    {
        var markers = Markers();
        markers[1] = new(125, 20, 100);
        var intendedDoor = Door(11, new(102, 8, 100));
        var neighborDoor = Door(22, new(122, 8, 100));

        AssertSelected(1, markers, [neighborDoor, intendedDoor], 11);
        AssertSelected(1, markers, [intendedDoor, neighborDoor], 11);
    }

    [Fact]
    public void UnloadedNeighborDoorDoesNotRemoveItsPlotFromOwnershipChecks()
    {
        var markers = Markers();
        markers[1] = new(106, 20, 100);
        // This is the only loaded door, but its position is closer to plot 2.
        AssertRejected(1, markers, [Door(7, new(105, 8, 100))]);
    }

    [Theory]
    [InlineData(-0.5f, false)]
    [InlineData(0f, false)]
    [InlineData(0.9f, false)]
    [InlineData(1f, false)]
    [InlineData(1.1f, true)]
    public void PlotOwnershipRequiresMoreThanOneMetreOfSeparation(float advantage, bool accepted)
    {
        var markers = Markers();
        markers[1] = new(120 + advantage, 20, 100);
        var candidates = new[] { Door(7, new(110, 8, 100)) };

        if (accepted) AssertSelected(1, markers, candidates, 7);
        else AssertRejected(1, markers, candidates);
    }

    [Theory]
    [InlineData(23.9f, true)]
    [InlineData(24f, true)]
    [InlineData(24.1f, false)]
    public void BoundsTheDoorDistanceFromTheSelectedMarker(float distance, bool accepted)
    {
        var markers = Markers();
        var candidates = new[] { Door(7, markers[0] + new Vector3(0, -12, distance)) };

        if (accepted) AssertSelected(1, markers, candidates, 7);
        else AssertRejected(1, markers, candidates);
    }

    [Theory]
    [InlineData(-32f, true)]
    [InlineData(32f, true)]
    [InlineData(-32.1f, false)]
    [InlineData(32.1f, false)]
    public void RejectsImplausibleVerticalSeparation(float heightDifference, bool accepted)
    {
        var markers = Markers();
        var candidates = new[] { Door(7, markers[0] + new Vector3(3, heightDifference, 4)) };

        if (accepted) AssertSelected(1, markers, candidates, 7);
        else AssertRejected(1, markers, candidates);
    }

    [Fact]
    public void MultiplePlausibleDoorsAreAmbiguousEvenWhenOneIsCloser()
    {
        var markers = Markers();
        AssertRejected(1, markers,
            [Door(1, markers[0] + new Vector3(2, -12, 0)), Door(2, markers[0] + new Vector3(6, -12, 0))]);
    }

    [Fact]
    public void DuplicateObjectIdentityCannotProduceASelection()
    {
        var markers = Markers();
        var door = Door(7, markers[0] + new Vector3(2, -12, 0));
        AssertRejected(1, markers, [door, door]);
    }

    [Fact]
    public void NonTargetableDoorIsNotSelected()
    {
        var markers = Markers();
        var hidden = new FuelEstateEntranceCandidate(7, markers[0], false);
        AssertRejected(1, markers, [hidden]);
        AssertSelected(1, markers, [hidden, Door(8, markers[0] + new Vector3(2, -12, 0))], 8);
    }

    [Fact]
    public void ZeroObjectIdentityCannotBeSelected()
        => AssertRejected(1, Markers(), [Door(0, new(103, 8, 104))]);

    [Fact]
    public void NoVisibleDoorsProduceAnExplainedFailure()
        => AssertRejected(1, Markers(), []);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(61)]
    public void RejectsInvalidPlotNumbers(int plot)
        => AssertRejected(plot, Markers(), [Door(7, new(103, 8, 104))]);

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(61)]
    public void RequiresTheCompleteSixtyPlotMarkerSet(int count)
    {
        var markers = Enumerable.Range(0, count).Select(i => new Vector3(100 + i * 100, 20, 100)).ToArray();
        AssertRejected(1, markers, [Door(7, new(103, 8, 104))]);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidMarkersCannotSilentlyRemoveACompetingPlot(float invalid)
    {
        foreach (var position in new[] { new Vector3(invalid, 20, 100), new(200, invalid, 100), new(200, 20, invalid) })
        {
            var markers = Markers();
            markers[59] = position;
            AssertRejected(1, markers, [Door(7, new(103, 8, 104))]);
        }
    }

    [Fact]
    public void MissingMarkerPositionCannotSilentlyRemoveACompetingPlot()
    {
        var markers = Markers();
        markers[59] = Vector3.Zero;
        AssertRejected(1, markers, [Door(7, new(103, 8, 104))]);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidDoorCoordinatesAreNeverSelected(float invalid)
    {
        foreach (var position in new[] { new Vector3(invalid, 8, 104), new(103, invalid, 104), new(103, 8, invalid) })
            AssertRejected(1, Markers(), [Door(7, position)]);
    }

    private static Vector3[] Markers() => Enumerable.Range(0, 60)
        .Select(i => new Vector3(100 + i * 100, 20, 100)).ToArray();

    private static FuelEstateEntranceCandidate Door(ulong id, Vector3 position) => new(id, position, true);

    private static void AssertSelected(int plot, IReadOnlyList<Vector3> markers,
        IReadOnlyList<FuelEstateEntranceCandidate> candidates, ulong expected)
    {
        Assert.True(FuelEstateEntranceSelection.TrySelect(plot, markers, candidates, out var selected, out var reason), reason);
        Assert.Equal(expected, selected);
    }

    private static void AssertRejected(int plot, IReadOnlyList<Vector3> markers,
        IReadOnlyList<FuelEstateEntranceCandidate> candidates)
    {
        Assert.False(FuelEstateEntranceSelection.TrySelect(plot, markers, candidates, out var selected, out var reason));
        Assert.Equal(0UL, selected);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
