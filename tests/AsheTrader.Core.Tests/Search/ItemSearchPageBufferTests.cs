using AsheTrader.Core.Search;

namespace AsheTrader.Core.Tests.Search;

public sealed class ItemSearchPageBufferTests
{
    private static readonly Guid Request = Guid.Parse("d2d38445-965f-4acf-a3ba-e064cdfb634c");
    private static readonly Guid Connection = Guid.Parse("cf7f1a7b-36cd-423f-9f27-81f83e401ed9");
    private static ItemSearchLocation Location(ulong owner = 1, ulong quantity = 10) =>
        new(10155, owner, "Retainer", "Character", "Cuchulainn", "Inventory", 10000, 0, quantity);
    private static ItemSearchPageBuffer Buffer(int maximumLocations = ItemSearchPageBuffer.MaximumLocations) =>
        new(Request, Connection, [10155, 5530], maximumLocations);

    [Fact]
    public void OrderedPagesAndProgressOnlyPagesAccumulateOnce()
    {
        var buffer = Buffer();
        Assert.True(buffer.TryAppend(Request, Connection, 0, 1, 2, [], out _));
        Assert.True(buffer.TryAppend(Request, Connection, 1, 2, 2, [Location()], out _));
        Assert.True(buffer.TryAppend(Request, Connection, 2, 2, 2, [Location(2)], out _));
        Assert.Equal(3, buffer.ExpectedSequence);
        Assert.Equal([1UL, 2UL], buffer.Locations.Select(location => location.OwnerId));
    }

    [Fact]
    public void StaleRequestsAndReconnectedSessionsCannotContributeQuantities()
    {
        var buffer = Buffer();
        Assert.False(buffer.TryAppend(Guid.NewGuid(), Connection, 0, 2, 2, [Location()], out var staleRequest));
        Assert.False(buffer.TryAppend(Request, Guid.NewGuid(), 0, 2, 2, [Location()], out var staleConnection));
        Assert.NotEmpty(staleRequest);
        Assert.NotEmpty(staleConnection);
        Assert.Empty(buffer.Locations);
        Assert.Equal(0, buffer.ExpectedSequence);
        Assert.True(buffer.TryAppend(Request, Connection, 0, 2, 2, [Location()], out _));
    }

    [Fact]
    public void RepeatedOrOutOfOrderPagesDoNotChangeAcceptedState()
    {
        var buffer = Buffer();
        Assert.False(buffer.TryAppend(Request, Connection, 1, 2, 2, [Location(2)], out _));
        Assert.True(buffer.TryAppend(Request, Connection, 0, 2, 2, [Location()], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 0, 2, 2, [Location()], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 2, 2, 2, [Location(3)], out _));
        Assert.Single(buffer.Locations);
        Assert.Equal(1, buffer.ExpectedSequence);
    }

    [Fact]
    public void DuplicatePhysicalInventoryIsRejectedWithinAndAcrossPages()
    {
        var buffer = Buffer();
        Assert.False(buffer.TryAppend(Request, Connection, 0, 2, 2, [Location(), Location()], out _));
        Assert.Empty(buffer.Locations);
        Assert.True(buffer.TryAppend(Request, Connection, 0, 2, 2, [Location()], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 1, 2, 2, [Location(2), Location()], out _));
        Assert.Single(buffer.Locations);
        Assert.Equal(1, buffer.ExpectedSequence);
        Assert.True(buffer.TryAppend(Request, Connection, 1, 2, 2, [Location(2)], out _));
    }

    [Fact]
    public void ProgressCannotRegressOrChangeRequestedItemCount()
    {
        var buffer = Buffer();
        Assert.True(buffer.TryAppend(Request, Connection, 0, 1, 2, [], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 1, 0, 2, [], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 1, -1, 2, [], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 1, 3, 2, [], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 1, 1, 3, [], out _));
        Assert.Equal(1, buffer.ExpectedSequence);
    }

    [Fact]
    public void ResultAndPageLimitsRejectWholePageWithoutTruncation()
    {
        var buffer = Buffer(2);
        Assert.True(buffer.TryAppend(Request, Connection, 0, 2, 2, [Location()], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 1, 2, 2, [Location(2), Location(3)], out _));
        Assert.Single(buffer.Locations);
        Assert.True(buffer.TryAppend(Request, Connection, 1, 2, 2, [Location(2)], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 2, 2, 2, [Location(3)], out _));

        var largePage = Enumerable.Range(1, ItemSearchPageBuffer.MaximumPageSize + 1).Select(owner => Location((ulong)owner)).ToArray();
        Assert.False(Buffer().TryAppend(Request, Connection, 0, 2, 2, largePage, out _));
    }

    [Fact]
    public void ItemTotalOverflowDoesNotPoisonTheFollowingValidPage()
    {
        var buffer = Buffer();
        Assert.True(buffer.TryAppend(Request, Connection, 0, 2, 2, [Location(quantity: ulong.MaxValue - 2)], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 1, 2, 2, [Location(2, 2), Location(3, 1)], out _));
        Assert.Single(buffer.Locations);
        Assert.True(buffer.TryAppend(Request, Connection, 1, 2, 2, [Location(2, 2)], out _));
        Assert.Equal(2, buffer.Locations.Count);
    }

    [Fact]
    public void InvalidLocationsAndTextAreRejectedWithoutChangingState()
    {
        var invalid = new ItemSearchLocation[]
        {
            Location(0), Location(quantity: 0), Location() with { ItemId = 999 },
            Location() with { OwnerName = null! }, Location() with { World = "bad\nworld" },
            Location() with { Storage = new string('a', ItemSearchPageBuffer.MaximumLabelLength + 1) }, null!,
        };
        var buffer = Buffer();
        foreach (var location in invalid)
            Assert.False(buffer.TryAppend(Request, Connection, 0, 2, 2, [location], out _));
        Assert.False(buffer.TryAppend(Request, Connection, 0, 2, 2, null, out _));
        Assert.Empty(buffer.Locations);
        Assert.Equal(0, buffer.ExpectedSequence);
    }

    [Fact]
    public void DifferentQualitiesAndContainersRemainDifferentLocations()
    {
        var buffer = Buffer();
        Assert.True(buffer.TryAppend(Request, Connection, 0, 2, 2,
            [Location(), Location() with { ItemFlags = 1 }, Location() with { Container = 10001 }], out _));
        Assert.Equal(3, buffer.Locations.Count);
    }
}
