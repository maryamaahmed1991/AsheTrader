using System.Numerics;
using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelSavedPathJoinTests
{
    private static readonly Vector3 Gate = new(10, 2, 10);
    private static readonly Vector3[] Route = [new(10, 2, 15), new(20, 2, 15), new(20, 2, 25)];

    [Fact]
    public void AtVerifiedGatePreservesEveryCornerBeforeTheDoor()
    {
        var remaining = Join(Route, Gate + new Vector3(1, 0, 0), Gate);
        Assert.Equal(new[] { Gate }.Concat(Route), remaining);
    }

    [Fact]
    public void ObservedFreshInteriorEntryPreservesTheWholePath()
        => Assert.Equal(Route, Join(Route, new(0, 0, 0), null, fresh: true));

    [Fact]
    public void FreshEntryCannotBypassAnOutdoorAnchorJoin()
        => Reject(Route, new(-20, 2, -20), Gate, fresh: true);

    [Fact]
    public void MidRouteResumeProjectsLocallyAndPreservesRemainingCorners()
    {
        var remaining = Join(Route, new(15, 2, 16), Gate);
        Assert.Equal(new[] { new Vector3(15, 2, 15), Route[1], Route[2] }, remaining);
    }

    [Fact]
    public void ACharacterOnTheInitialGateSegmentCanResumeBeforeTheFirstRecordedPoint()
    {
        var remaining = Join(Route, new(10, 2, 13), Gate);
        Assert.Equal(new[] { new Vector3(10, 2, 13) }.Concat(Route), remaining);
    }

    [Fact]
    public void ResumeNearDoorDoesNotWalkBackThroughTheEntirePath()
        => Assert.Equal(new[] { Route[^1] }, Join(Route, Route[^1] + new Vector3(1, 1, 0), Gate));

    [Fact]
    public void NoDistantWaypointCanAuthorizeCuttingAcrossTheYard()
        => Reject(Route, new(16, 2, 8), Gate);

    [Fact]
    public void CrossingAnotherFloorDoesNotJoinARecordedSegment()
        => Reject(Route, new(15, 7, 15), Gate);

    [Fact]
    public void FirstMatchingSegmentWinsWhereRoutesOverlap()
    {
        Vector3[] route = [new(10, 2, 10), new(20, 2, 10), new(20, 2, 20), new(10, 2, 20), new(10, 2, 10), new(30, 2, 10)];
        var remaining = Join(route, new(15, 2, 10), null);
        Assert.Equal(new[] { new Vector3(15, 2, 10) }.Concat(route.Skip(1)), remaining);
    }

    [Fact]
    public void RepeatedWaypointsDoNotCauseInvalidProjection()
    {
        Vector3[] route = [Route[0], Route[0], Route[1], Route[2]];
        Assert.Equal(new[] { Route[0] }.Concat(route.Skip(1)), Join(route, Route[0], null));
    }

    [Theory]
    [InlineData(1.5f, true)]
    [InlineData(1.501f, false)]
    public void LocalJoinHasABoundedThreeDimensionalDistance(float offset, bool accepted)
    {
        var player = new Vector3(15, 2 + offset, 15);
        if (accepted) Assert.Equal(new Vector3(15, 2, 15), Join(Route, player, null)[0]);
        else Reject(Route, player, null);
    }

    [Theory]
    [InlineData(1.5f, 3f, true)]
    [InlineData(1.501f, 0f, false)]
    [InlineData(0f, 3.001f, false)]
    public void SinglePointEndpointHasTheSameArrivalBoundsAsTheProgressObserver(float horizontal, float vertical, bool accepted)
    {
        Vector3[] route = [Route[^1]];
        var player = route[0] + new Vector3(horizontal, vertical, 0);
        if (accepted) Assert.Equal(route, Join(route, player, null));
        else Reject(route, player, null);
    }

    [Fact]
    public void SnapshotDoesNotRetainTheCallersMutablePoints()
    {
        var original = Route.ToArray();
        var remaining = Join(original, Gate, Gate);
        original[0] = new(999, 999, 999);
        Assert.Equal(Route[0], remaining[1]);
        Assert.True(Assert.IsAssignableFrom<IList<Vector3>>(remaining).IsReadOnly);
    }

    [Fact]
    public void EmptyRouteCannotStartMovement()
        => Reject([], Gate, Gate);

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(10_001f)]
    public void InvalidRecordedOrCurrentCoordinatesAreRejected(float invalid)
    {
        var bad = new Vector3(invalid, 2, 15);
        Reject([bad, Route[1]], Gate, Gate);
        Reject(Route, bad, Gate);
        Reject(Route, Gate, bad);
    }

    [Fact]
    public void PrependingTheGateCannotExceedTheDispatchPointLimit()
    {
        var route = Enumerable.Repeat(Route[0], LifestreamHouseRoutes.MaximumPathPoints).ToArray();
        Reject(route, Gate, Gate);
    }

    private static IReadOnlyList<Vector3> Join(IReadOnlyList<Vector3> recorded, Vector3 player, Vector3? anchor, bool fresh = false)
    {
        Assert.True(FuelSavedPathJoin.TryCreate(recorded, player, anchor, fresh, out var remaining, out var reason), reason);
        return remaining;
    }

    private static void Reject(IReadOnlyList<Vector3> recorded, Vector3 player, Vector3? anchor, bool fresh = false)
    {
        Assert.False(FuelSavedPathJoin.TryCreate(recorded, player, anchor, fresh, out var remaining, out var reason));
        Assert.Empty(remaining);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
