namespace AsheTrader.Core.Search;

/// <summary>A cached quantity grouped by physical owner, container, item and item flags.</summary>
public sealed record ItemSearchLocation(
    uint ItemId, ulong OwnerId, string OwnerName, string CharacterName, string World,
    string Storage, uint Container, uint ItemFlags, ulong Quantity);

public sealed record ItemSearchCatalogItem(uint ItemId, string Name);

/// <summary>Retrieval time is the time of this query, not when Allagan Tools last observed inventory.</summary>
public sealed record ItemSearchSource(
    Guid PeerId, string Name, bool Complete, string Error, IReadOnlyList<string> Warnings,
    DateTimeOffset? RetrievedAt, IReadOnlyList<ItemSearchLocation> Locations,
    int CheckedItems, int TotalItems);

public sealed record ItemSearchHolding(Guid PeerId, string PeerName, ItemSearchLocation Location);

public sealed record ItemSearchRow(
    uint ItemId, string Name, ulong Total, ulong NormalQuality, ulong HighQuality, ulong OtherQuality,
    ulong LocalQuantity, ulong RemoteQuantity, IReadOnlyList<ItemSearchHolding> Holdings);

public static class ItemSearchQuery
{
    public const int MaximumQueryLength = 2048;
    public const int MaximumTerms = 64;

    /// <summary>Commas separate alternatives. Repeated alternatives never multiply inventory counts.</summary>
    public static IReadOnlyList<string> Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length > MaximumQueryLength)
            throw new ArgumentException($"Keep the search to {MaximumQueryLength:N0} characters or fewer.", nameof(query));
        var terms = query.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > MaximumTerms)
            throw new ArgumentException($"Search for up to {MaximumTerms} comma-separated terms at a time.", nameof(query));
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool Matches(string name, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(terms);
        return terms.Any(term => !string.IsNullOrWhiteSpace(term)
            && name.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<ItemSearchCatalogItem> Match(IEnumerable<ItemSearchCatalogItem> catalog, string query)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var terms = Parse(query);
        if (terms.Count == 0) return [];
        return catalog.Where(item => item.ItemId != 0 && !string.IsNullOrWhiteSpace(item.Name)
                && Matches(item.Name, terms))
            .DistinctBy(item => item.ItemId)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .ToArray();
    }
}

public static class ItemSearchAggregation
{
    /// <summary>
    /// Returns one row per matched item, including all qualities. Cached copies of the same
    /// physical inventory on both PCs count once, preferring the local query. Unavailable
    /// inventories remain represented by their source status; they do not create zero rows.
    /// </summary>
    public static IReadOnlyList<ItemSearchRow> Build(IEnumerable<ItemSearchCatalogItem> catalog,
        ItemSearchSource local, ItemSearchSource? remote = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(local);
        var names = catalog.Where(item => item.ItemId != 0 && !string.IsNullOrWhiteSpace(item.Name))
            .DistinctBy(item => item.ItemId).ToDictionary(item => item.ItemId, item => item.Name);
        var holdings = new Dictionary<(uint Item, ulong Owner, uint Container, uint Flags), ItemSearchHolding>();
        AddSource(local);
        if (remote != null) AddSource(remote);
        return holdings.Values.GroupBy(holding => holding.Location.ItemId)
            .Select(group =>
            {
                var rows = group.OrderBy(holding => holding.PeerId != local.PeerId)
                    .ThenBy(holding => holding.Location.CharacterName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(holding => holding.Location.OwnerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(holding => holding.Location.Storage, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(holding => holding.Location.OwnerId)
                    .ThenBy(holding => holding.Location.Container)
                    .ThenBy(holding => holding.Location.ItemFlags)
                    .ToArray();
                return new ItemSearchRow(group.Key, names[group.Key], Sum(rows),
                    Sum(rows.Where(row => row.Location.ItemFlags == 0)),
                    Sum(rows.Where(row => row.Location.ItemFlags == 1)),
                    Sum(rows.Where(row => row.Location.ItemFlags is not (0 or 1))),
                    Sum(rows.Where(row => row.PeerId == local.PeerId)),
                    Sum(rows.Where(row => row.PeerId != local.PeerId)), rows);
            })
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ToArray();

        void AddSource(ItemSearchSource source)
        {
            foreach (var group in source.Locations.Where(location => location.OwnerId != 0
                         && location.Quantity > 0 && names.ContainsKey(location.ItemId))
                         .GroupBy(location => (location.ItemId, location.OwnerId, location.Container, location.ItemFlags)))
            {
                var first = group.First();
                var merged = first with { Quantity = group.Aggregate(0UL, (sum, location) => checked(sum + location.Quantity)) };
                holdings.TryAdd(group.Key, new ItemSearchHolding(source.PeerId, source.Name, merged));
            }
        }
    }

    private static ulong Sum(IEnumerable<ItemSearchHolding> rows) =>
        rows.Aggregate(0UL, (sum, row) => checked(sum + row.Location.Quantity));
}
