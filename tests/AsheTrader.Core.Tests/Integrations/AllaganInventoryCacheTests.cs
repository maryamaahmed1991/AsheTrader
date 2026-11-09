using AsheTrader.Integrations;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class AllaganInventoryCacheTests
{
    private const ulong Owner = 42;
    private const uint Item = 10155;

    [Fact]
    public void CompleteObservedEmptyBagsAreKnownCachedZero()
    {
        var snapshot = Read(Empty());
        Assert.Equal(0U, snapshot.Quantity);
        Assert.Equal(140U * 999, snapshot.Capacity);
    }

    [Fact]
    public void EntirelySyntheticEmptyGroupIsUnknown()
    {
        var rows = Empty();
        foreach (var row in rows) row[23] = 0;
        Assert.Throws<InvalidDataException>(() => Read(rows));
    }

    [Fact]
    public void SavedPopulatedGroupAcceptsSyntheticEmptySlots()
    {
        var rows = Empty();
        foreach (var row in rows) row[23] = 0;
        Put(rows[139], 10);
        Assert.Equal(10U, Read(rows).Quantity);
        Assert.Equal(140U * 999 - 10, Read(rows).Capacity);
    }

    [Fact]
    public void MissingOwnerIsNotAnEmptyInventory() => Assert.Throws<InvalidDataException>(() => Read([]));

    [Fact]
    public void MissingPageFallsBackRatherThanInventingFreeSpace() => Assert.Throws<InvalidDataException>(() => Read(Empty().Take(105)));

    [Fact]
    public void RetainersUseFiveSortedPagesOf35()
    {
        var rows = Empty(true);
        Put(rows[174], 70);
        var snapshot = AllaganInventoryCache.Read(Owner, rows, true, Item, false, 999);
        Assert.Equal(70U, snapshot.Quantity);
        Assert.Equal(175U * 999 - 70, snapshot.Capacity);
    }

    [Fact]
    public void HqAndNqRemainSeparateAndForeignItemsConsumeSpace()
    {
        var rows = Empty();
        Put(rows[0], 30);
        Put(rows[1], 40, 1);
        Put(rows[2], 50, 0, Item + 1);
        Assert.Equal(30U, Read(rows).Quantity);
        Assert.Equal(137U * 999 + 969, Read(rows).Capacity);
        Assert.Equal(40U, AllaganInventoryCache.Read(Owner, rows, false, Item, true, 999).Quantity);
    }

    [Fact]
    public void CollectiblesDoNotCountAsNormalQuality()
    {
        var rows = Empty(); Put(rows[0], 30, 2);
        Assert.Equal(0U, Read(rows).Quantity);
        Assert.Equal(139U * 999, Read(rows).Capacity);
    }

    [Fact]
    public void UsesSortedSlotsWhenRawSlotsAreDefault()
    {
        var rows = Empty(); Put(rows[40], 45);
        Assert.Equal(45U, Read(rows).Quantity);
    }

    [Fact]
    public void DuplicateSortedSlotsAreRejected()
    {
        var rows = Empty(); rows[1][22] = 0;
        Assert.Throws<InvalidDataException>(() => Read(rows));
    }

    [Fact]
    public void PopulatedForeignOwnerIsRejected()
    {
        var rows = Empty(); Put(rows[0], 3); rows[0][23] = Owner + 1;
        Assert.Throws<InvalidDataException>(() => Read(rows));
    }

    [Fact]
    public void PopulatedAnonymousOwnerIsRejected()
    {
        var rows = Empty(); Put(rows[0], 3); rows[0][23] = 0;
        Assert.Throws<InvalidDataException>(() => Read(rows));
    }

    [Theory]
    [InlineData(2, 0UL)]
    [InlineData(3, 0UL)]
    [InlineData(3, 1000UL)]
    [InlineData(22, 35UL)]
    public void InvalidPopulatedRecordsAreRejected(int field, ulong value)
    {
        var rows = Empty(); Put(rows[0], 3); rows[0][field] = value;
        Assert.Throws<InvalidDataException>(() => Read(rows));
    }

    [Fact]
    public void OtherContainersAreExcluded()
    {
        var rows = Empty(); var other = new ulong[25]; Put(other, 500); other[20] = 4000;
        rows.Add(other);
        Assert.Equal(0U, Read(rows).Quantity);
        Assert.Equal(140U * 999, Read(rows).Capacity);
    }

    [Fact]
    public void UnmovableStackDoesNotOfferMergeSpace()
    {
        var rows = Empty(); Put(rows[0], 20); rows[0][4] = 1;
        Assert.Equal(20U, Read(rows).Quantity);
        Assert.Equal(139U * 999, Read(rows).Capacity);
    }

    private static CachedContainerInventory Read(IEnumerable<ulong[]> rows) => AllaganInventoryCache.Read(Owner, rows, false, Item, false, 999);

    private static List<ulong[]> Empty(bool retainer = false) => Enumerable.Range(0, retainer ? 175 : 140).Select(index =>
    {
        var row = new ulong[25];
        row[20] = (ulong)((retainer ? 10000 : 0) + index / 35);
        row[22] = (ulong)(index % 35);
        row[23] = Owner;
        return row;
    }).ToList();

    private static void Put(ulong[] row, uint quantity, ulong flags = 0, uint itemId = Item)
    {
        row[2] = itemId; row[3] = quantity; row[6] = flags; row[23] = Owner;
    }
}
