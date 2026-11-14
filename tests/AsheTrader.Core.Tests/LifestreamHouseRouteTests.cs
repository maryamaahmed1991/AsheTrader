using System.Numerics;
using AsheTrader.Core;
using Newtonsoft.Json;

namespace AsheTrader.Core.Tests;

public sealed class LifestreamHouseRouteTests
{
    private static readonly FuelHouseAddress Destination = new(54, 9, 9, 33);

    [Fact]
    public void PublicIpcTuplePreservesRecordedVectorPaths()
    {
        // Mirrors Dalamud's conversion of the public HousePathData tuple through JSON.
        const string payload = """
            {"Item1":null,"Item2":{"ResidentialDistrict":9,"Ward":8,"Plot":32,
            "IsPrivate":false,"CID":42,
            "PathToEntrance":[{"X":-629.25,"Y":6,"Z":-776.75},{"X":-628.5,"Y":7.5,"Z":-790}],
            "PathToWorkshop":[{"X":1,"Y":0,"Z":2},{"X":3,"Y":-6,"Z":4}]}}
            """;
        var tuple = JsonConvert.DeserializeObject<(LifestreamRegisteredHouse? Private, LifestreamRegisteredHouse? FC)>(payload);

        Assert.Null(tuple.Private);
        Assert.True(LifestreamHouseRoutes.TryCreate(Destination, 42, 54, tuple.FC, out var route, out var reason), reason);
        Assert.Equal(42UL, route.OwnerCharacterId);
        Assert.Equal(new[] { new Vector3(-629.25f, 6, -776.75f), new(-628.5f, 7.5f, -790) }, route.Entrance);
        Assert.Equal(new[] { new Vector3(1, 0, 2), new(3, -6, 4) }, route.Workshop);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(60)]
    public void ConvertsRegisteredPlotNumbersWithoutChangingWaypointOrder(int plot)
    {
        var registration = Registration();
        registration.Plot = plot - 1;
        registration.PathToEntrance = [new(1, 2, 3), new(4, 5, 6), new(7, 8, 9)];

        Assert.True(LifestreamHouseRoutes.TryCreate(new(54, 9, 9, plot), 42, 54, registration, out var route, out var reason), reason);
        Assert.Equal(registration.PathToEntrance, route.Entrance);
    }

    [Theory]
    [InlineData(55, 9, 9, 33)]
    [InlineData(54, 8, 9, 33)]
    [InlineData(54, 9, 8, 33)]
    [InlineData(54, 9, 9, 32)]
    public void RejectsAnIdenticallyShapedRouteForADifferentAddress(int world, int district, int ward, int plot)
        => AssertRejected(new(world, district, ward, plot), 42, 54, Registration());

    [Fact]
    public void DoesNotAttachARegistrationToADifferentOwnersWorld()
    {
        AssertRejected(Destination, 42, 55, Registration());
        AssertRejected(Destination, 42, 0, Registration());
        AssertRejected(Destination, 43, 54, Registration());
        AssertRejected(Destination, 0, 54, Registration());
    }

    [Fact]
    public void RejectsMissingPrivateAndUnboundRegistrations()
    {
        AssertRejected(Destination, 42, 54, null);
        var registration = Registration();
        registration.IsPrivate = true;
        AssertRejected(Destination, 42, 54, registration);
        registration.IsPrivate = null;
        AssertRejected(Destination, 42, 54, registration);
        registration.IsPrivate = false;
        registration.CID = null;
        AssertRejected(Destination, 42, 54, registration);
    }

    [Fact]
    public void SnapshotIsDetachedFromIpcOwnedListsAndReadOnly()
    {
        var registration = Registration();
        var entrance = registration.PathToEntrance!.ToArray();
        var workshop = registration.PathToWorkshop!.ToArray();
        Assert.True(LifestreamHouseRoutes.TryCreate(Destination, 42, 54, registration, out var route, out _));

        registration.PathToEntrance[0] = new(999, 999, 999);
        registration.PathToEntrance.Add(new(888, 888, 888));
        registration.PathToWorkshop.Clear();

        Assert.Equal(entrance, route.Entrance);
        Assert.Equal(workshop, route.Workshop);
        var exposed = Assert.IsAssignableFrom<IList<Vector3>>(route.Entrance);
        Assert.True(exposed.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposed[0] = Vector3.Zero);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllowsOneRecordedLegWithoutInventingTheOther(bool entranceOnly)
    {
        var registration = Registration();
        if (entranceOnly) registration.PathToWorkshop = null;
        else registration.PathToEntrance = [];

        Assert.True(LifestreamHouseRoutes.TryCreate(Destination, 42, 54, registration, out var route, out var reason), reason);
        Assert.Equal(entranceOnly ? 2 : 0, route.Entrance.Count);
        Assert.Equal(entranceOnly ? 0 : 2, route.Workshop.Count);
    }

    [Fact]
    public void MatchingHouseWithoutRecordedPathsIsNotARoute()
    {
        var registration = Registration();
        registration.PathToEntrance = null;
        registration.PathToWorkshop = [];
        AssertRejected(Destination, 42, 54, registration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidRecordedLegDoesNotSilentlyBecomeAPartialRoute(bool invalidEntrance)
    {
        var registration = Registration();
        var invalid = new List<Vector3> { new(float.NaN, 0, 0) };
        if (invalidEntrance) registration.PathToEntrance = invalid;
        else registration.PathToWorkshop = invalid;
        AssertRejected(Destination, 42, 54, registration);
    }

    [Fact]
    public void CompleteRouteOutranksCurrentCharactersPartialRouteWithoutSplicing()
    {
        var partial = Route(42, entrance: [new(1, 2, 3)], workshop: []);
        var complete = Route(84, entrance: [new(4, 5, 6)], workshop: [new(7, 8, 9)]);

        Assert.Same(complete, LifestreamHouseRoutes.SelectBest([partial, complete], 42));
        Assert.Same(complete, LifestreamHouseRoutes.SelectBest([complete, partial], 42));
    }

    [Fact]
    public void ComplementaryPartialRegistrationsAreNeverJoined()
    {
        var entrance = Route(42, entrance: [new(1, 2, 3)], workshop: []);
        var workshop = Route(84, entrance: [], workshop: [new(4, 5, 6)]);

        var selected = LifestreamHouseRoutes.SelectBest([workshop, entrance], 42);
        Assert.Same(entrance, selected);
        Assert.Empty(selected.Workshop);
        Assert.Same(workshop, LifestreamHouseRoutes.SelectBest([entrance, workshop], 84));
    }

    [Fact]
    public void EqualQualityRoutesPreferCurrentOwnerThenStableOwnerOrder()
    {
        var first = Route(42, [new(1, 2, 3)], [new(4, 5, 6)]);
        var second = Route(84, [new(7, 8, 9)], [new(10, 11, 12)]);

        Assert.Same(second, LifestreamHouseRoutes.SelectBest([first, second], 84));
        Assert.Same(first, LifestreamHouseRoutes.SelectBest([second, first], 100));
        Assert.Same(first, LifestreamHouseRoutes.SelectBest([first, second], 100));
    }

    [Fact]
    public void EmptyCandidatesDoNotCreateAnExecutableRoute()
    {
        Assert.Same(LifestreamHouseRoute.Empty, LifestreamHouseRoutes.SelectBest([], 42));
        Assert.Same(LifestreamHouseRoute.Empty, LifestreamHouseRoutes.SelectBest([Route(42, [], [])], 42));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void RejectsNonFiniteCoordinatesInEveryAxis(float invalid)
    {
        foreach (var point in new[] { new Vector3(invalid, 1, 2), new(1, invalid, 2), new(1, 2, invalid) })
        {
            Assert.False(LifestreamHouseRoutes.TrySnapshot([new(1, 2, 3), point], out var path, out var reason));
            Assert.Empty(path);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    [Theory]
    [InlineData(10_000f, true)]
    [InlineData(-10_000f, true)]
    [InlineData(10_001f, false)]
    [InlineData(-10_001f, false)]
    public void BoundsRecordedCoordinatesWithoutRejectingTheOrigin(float coordinate, bool accepted)
    {
        Assert.Equal(accepted, LifestreamHouseRoutes.TrySnapshot([Vector3.Zero, new(coordinate, coordinate, coordinate)], out var path, out _));
        Assert.Equal(accepted ? 2 : 0, path.Count);
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void BoundsTheRecordedPathLength(int count, bool accepted)
    {
        var points = Enumerable.Range(0, count).Select(index => new Vector3(index, 0, index)).ToArray();
        Assert.Equal(accepted, LifestreamHouseRoutes.TrySnapshot(points, out var path, out _));
        Assert.Equal(accepted ? count : 0, path.Count);
    }

    private static LifestreamRegisteredHouse Registration() => new()
    {
        CID = 42, ResidentialDistrict = 9, Ward = 8, Plot = 32, IsPrivate = false,
        PathToEntrance = [new(-629.25f, 6, -776.75f), new(-628.5f, 7.5f, -790)],
        PathToWorkshop = [new(1, 0, 2), new(3, -6, 4)],
    };

    private static LifestreamHouseRoute Route(ulong owner, IReadOnlyList<Vector3> entrance, IReadOnlyList<Vector3> workshop)
        => new(owner, entrance, workshop);

    private static void AssertRejected(FuelHouseAddress expected, ulong owner, int world, LifestreamRegisteredHouse? registration)
    {
        Assert.False(LifestreamHouseRoutes.TryCreate(expected, owner, world, registration, out var route, out var reason));
        Assert.Same(LifestreamHouseRoute.Empty, route);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
