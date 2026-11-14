using AsheTrader.Core.Search;

namespace AsheTrader.Core.Tests.Search;

public sealed class ItemSearchTests
{
    private static readonly Guid LocalPeer = Guid.Parse("c65437a0-d797-4395-9d60-530ad089ba11");
    private static readonly Guid RemotePeer = Guid.Parse("8a7c519e-bac0-4b47-ab9c-d3ea607485ad");
    private static readonly ItemSearchCatalogItem[] Catalog =
    [new(10155, "Ceruleum Tank"), new(5530, "Coke"), new(5501, "Hardened Sap"), new(9999, "Ceruleum Sample")];

    private static ItemSearchLocation Location(uint itemId = 10155, ulong quantity = 10,
        ulong ownerId = 7, uint container = 0, uint flags = 0) =>
        new(itemId, ownerId, $"Owner {ownerId}", "Alt A", "Cuchulainn", "Inventory", container, flags, quantity);

    private static ItemSearchSource Source(bool remote = false, params ItemSearchLocation[] locations) =>
        new(remote ? RemotePeer : LocalPeer, remote ? "Other PC" : "This PC", true, "", [],
            DateTimeOffset.Parse("2026-11-13T13:00:00Z"), locations, 4, 4);

    [Fact]
    public void CommaTermsAreTrimmedAndCaseInsensitiveDuplicatesAreIgnored()
    {
        Assert.Equal(["ceru", "Coke", "Sap"], ItemSearchQuery.Parse("  ceru, Coke ,,CERU,  Sap  , "));
    }

    [Fact]
    public void PartialTermsMatchAlternativesAcrossTheCatalog()
    {
        var result = ItemSearchQuery.Match(Catalog, "  COKE , hardened, tank ");
        Assert.Equal([10155U, 5530U, 5501U], result.Select(item => item.ItemId));
    }

    [Fact]
    public void OverlappingTermsAndDuplicateCatalogRowsProduceOneMatchPerItem()
    {
        var result = ItemSearchQuery.Match(Catalog.Concat([Catalog[0]]), "ceru, tank, CERU");
        Assert.Equal([9999U, 10155U], result.Select(item => item.ItemId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ,, ")]
    [InlineData("   ")]
    public void EmptySearchDoesNotMatchTheWholeCatalog(string query) => Assert.Empty(ItemSearchQuery.Match(Catalog, query));

    [Fact]
    public void SearchLimitsRejectRatherThanSilentlyTruncate()
    {
        Assert.Throws<ArgumentException>(() => ItemSearchQuery.Parse(new string('a', ItemSearchQuery.MaximumQueryLength + 1)));
        Assert.Throws<ArgumentException>(() => ItemSearchQuery.Parse(string.Join(',', Enumerable.Repeat("a", ItemSearchQuery.MaximumTerms + 1))));
        Assert.Single(ItemSearchQuery.Parse(new string('a', ItemSearchQuery.MaximumQueryLength)));
        Assert.Equal(ItemSearchQuery.MaximumTerms,
            ItemSearchQuery.Parse(string.Join(',', Enumerable.Range(0, ItemSearchQuery.MaximumTerms))).Count);
    }

    [Fact]
    public void MatchesIgnoresEmptyTermsWhenCalledDirectly()
    {
        Assert.False(ItemSearchQuery.Matches("Coke", ["", " ", "Fuel"]));
        Assert.True(ItemSearchQuery.Matches("Coke", ["", " COK "]));
    }

    [Fact]
    public void BothPcsAndQualitiesShareOneItemRowWithExactBreakdowns()
    {
        var local = Source(false, Location(quantity: 40), Location(quantity: 15, flags: 1), Location(quantity: 3, flags: 8));
        var remote = Source(true, Location(quantity: 200, ownerId: 8), Location(quantity: 7, ownerId: 8, flags: 1));
        var row = Assert.Single(ItemSearchAggregation.Build(Catalog, local, remote));
        Assert.Equal("Ceruleum Tank", row.Name);
        Assert.Equal(265UL, row.Total);
        Assert.Equal(240UL, row.NormalQuality);
        Assert.Equal(22UL, row.HighQuality);
        Assert.Equal(3UL, row.OtherQuality);
        Assert.Equal(58UL, row.LocalQuantity);
        Assert.Equal(207UL, row.RemoteQuantity);
        Assert.Equal(5, row.Holdings.Count);
    }

    [Fact]
    public void LocalOnlySearchIncludesOtherStorageOnTheSameOwner()
    {
        var rows = ItemSearchAggregation.Build(Catalog,
            Source(false, Location(quantity: 30), Location(quantity: 90, container: 10000), Location(5530, 5)));
        Assert.Equal(2, rows.Count);
        Assert.Equal(120UL, rows[0].Total);
        Assert.Equal(120UL, rows[0].LocalQuantity);
        Assert.Equal(0UL, rows[0].RemoteQuantity);
        Assert.Equal(2, rows[0].Holdings.Count);
    }

    [Fact]
    public void CopiedCacheDoesNotCountTheSamePhysicalInventoryTwice()
    {
        var local = Source(false, Location(quantity: 20), Location(quantity: 5));
        var remote = Source(true, Location(quantity: 99), Location(quantity: 13, container: 1));
        var row = Assert.Single(ItemSearchAggregation.Build(Catalog, local, remote));
        Assert.Equal(38UL, row.Total);
        Assert.Equal(25UL, row.LocalQuantity);
        Assert.Equal(13UL, row.RemoteQuantity);
        Assert.Equal(2, row.Holdings.Count);
        Assert.Equal(25UL, row.Holdings.Single(holding => holding.PeerId == LocalPeer).Location.Quantity);
    }

    [Fact]
    public void TotalsUseUlongAcrossManyOwners()
    {
        var row = Assert.Single(ItemSearchAggregation.Build(Catalog,
            Source(false, Location(quantity: uint.MaxValue)), Source(true, Location(quantity: uint.MaxValue, ownerId: 9))));
        Assert.Equal(2UL * uint.MaxValue, row.Total);
    }

    [Fact]
    public void UnavailableSourceDoesNotFabricateZeroStockRowsOrHideKnownResults()
    {
        var failed = Source(true) with { Error = "Allagan Tools unavailable.", RetrievedAt = null, CheckedItems = 0 };
        var partial = Source(false, Location(5530, 10)) with { Complete = false, Warnings = ["One owner could not be read."] };
        var row = Assert.Single(ItemSearchAggregation.Build(Catalog, partial, failed));
        Assert.Equal(5530U, row.ItemId);
        Assert.Equal(10UL, row.Total);
    }

    [Fact]
    public void UnknownUnmatchedZeroAndInvalidOwnerEntriesAreExcluded()
    {
        var rows = ItemSearchAggregation.Build([Catalog[0]], Source(false,
            Location(), Location(999999), Location(5530), Location(quantity: 0, ownerId: 8), Location(ownerId: 0)));
        Assert.Equal(10155U, Assert.Single(rows).ItemId);
        Assert.Equal(10UL, rows[0].Total);
    }

    [Fact]
    public void DuplicateNamesKeepDifferentItemIdsAndUseStableOrdering()
    {
        var rows = ItemSearchAggregation.Build([new(20, "Same"), new(10, "Same"), new(10, "duplicate")],
            Source(false, Location(20), Location(10)));
        Assert.Equal([10U, 20U], rows.Select(row => row.ItemId));
        Assert.All(rows, row => Assert.Equal("Same", row.Name));
    }
}
