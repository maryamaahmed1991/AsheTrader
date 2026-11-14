namespace AsheTrader.Core.Search;

/// <summary>
/// An ordered search result belongs to one request on one authenticated connection. Invalid,
/// stale or repeated pages never change the accepted quantities or advance the sequence.
/// </summary>
public sealed class ItemSearchPageBuffer
{
    public const int MaximumPageSize = 64;
    public const int MaximumLocations = 250_000;
    public const int MaximumLabelLength = 256;

    private readonly Guid requestId;
    private readonly Guid connectionId;
    private readonly HashSet<uint> requestedItems;
    private readonly int maximumLocations;
    private readonly List<ItemSearchLocation> locations = [];
    private readonly HashSet<(uint Item, ulong Owner, uint Container, uint Flags)> acceptedKeys = [];
    private readonly Dictionary<uint, ulong> itemTotals = [];
    private int checkedItems;

    public int ExpectedSequence { get; private set; }
    public IReadOnlyList<ItemSearchLocation> Locations { get; }

    public ItemSearchPageBuffer(Guid requestId, Guid connectionId, IReadOnlyList<uint> itemIds,
        int maximumLocations = MaximumLocations)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (requestId == Guid.Empty || connectionId == Guid.Empty)
            throw new ArgumentException("A search request and authenticated connection are required.");
        if (itemIds.Any(id => id <= 1) || itemIds.Distinct().Count() != itemIds.Count)
            throw new ArgumentException("Search item IDs must be valid and distinct.", nameof(itemIds));
        if (maximumLocations is < 1 or > MaximumLocations)
            throw new ArgumentOutOfRangeException(nameof(maximumLocations));
        this.requestId = requestId;
        this.connectionId = connectionId;
        requestedItems = itemIds.ToHashSet();
        this.maximumLocations = maximumLocations;
        Locations = locations.AsReadOnly();
    }

    public bool TryAppend(Guid request, Guid connection, int sequence, int checkedItemCount, int totalItems,
        IReadOnlyList<ItemSearchLocation>? page, out string error)
    {
        error = string.Empty;
        if (request != requestId || connection != connectionId)
        { error = "This search page belongs to a previous request or connection."; return false; }
        if (sequence != ExpectedSequence || ExpectedSequence == int.MaxValue)
        { error = "The paired PC returned an out-of-order or repeated item-search page."; return false; }
        if (totalItems != requestedItems.Count || checkedItemCount < checkedItems || checkedItemCount > totalItems)
        { error = "The paired PC returned invalid item-search progress."; return false; }
        if (page == null || page.Count > MaximumPageSize || page.Count > maximumLocations - locations.Count)
        { error = "The paired PC exceeded the item-search result limit."; return false; }

        var pageKeys = new HashSet<(uint Item, ulong Owner, uint Container, uint Flags)>();
        var newTotals = new Dictionary<uint, ulong>();
        foreach (var location in page)
        {
            if (location == null || location.OwnerId == 0 || location.Quantity == 0 || !requestedItems.Contains(location.ItemId)
                || !ValidLabel(location.OwnerName) || !ValidLabel(location.CharacterName)
                || !ValidLabel(location.World) || !ValidLabel(location.Storage))
            { error = "The paired PC returned an invalid cached inventory location."; return false; }
            var key = (location.ItemId, location.OwnerId, location.Container, location.ItemFlags);
            if (acceptedKeys.Contains(key) || !pageKeys.Add(key))
            { error = "The paired PC returned the same cached inventory location more than once."; return false; }
            var currentTotal = newTotals.GetValueOrDefault(location.ItemId, itemTotals.GetValueOrDefault(location.ItemId));
            if (ulong.MaxValue - currentTotal < location.Quantity)
            { error = "The paired PC returned an item quantity outside the supported range."; return false; }
            newTotals[location.ItemId] = currentTotal + location.Quantity;
        }

        locations.AddRange(page);
        acceptedKeys.UnionWith(pageKeys);
        foreach (var (itemId, quantity) in newTotals) itemTotals[itemId] = quantity;
        checkedItems = checkedItemCount;
        ExpectedSequence++;
        return true;
    }

    private static bool ValidLabel(string? label) => label != null && label.Length <= MaximumLabelLength
        && !label.Any(char.IsControl);
}
