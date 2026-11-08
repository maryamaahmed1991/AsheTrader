using AsheTrader.Integrations;

namespace AsheTrader.Workflow;

public sealed record InventoryHint(ulong OwnerId, uint ItemId, bool HighQuality, ulong Quantity, DateTimeOffset RetrievedAt);
public sealed record InventoryHintRequest(uint ItemId, bool HighQuality);
public sealed record InventoryHintResponse(uint ItemId, bool HighQuality, string Status, List<InventoryHint> Items);

public sealed partial class WorkflowService
{
    public InventoryHintResponse? LocalInventoryHints { get; private set; }
    public InventoryHintResponse? RemoteInventoryHints { get; private set; }

    public void RefreshInventoryHints(uint itemId, bool highQuality)
    {
        var query = new InventoryHintRequest(itemId, highQuality);
        LocalInventoryHints = ReadHints(query);
        if (Connected) Send("cache-request", query);
    }

    private static InventoryHintResponse ReadHints(InventoryHintRequest query)
    {
        if (query.ItemId == 0) throw new InvalidDataException("An item ID is required.");
        var api = new AllaganToolsClient();
        if (!api.TryGetKnownItemHolders(query.ItemId, out var owners, out var error))
            return new(query.ItemId, query.HighQuality, error, []);
        var hints = new List<InventoryHint>();
        foreach (var owner in owners.Take(512))
        {
            if (!api.TryReadOwner(owner, out var snapshot, out _) || snapshot == null) continue;
            var quantity = snapshot.Items.Where(x => x.ItemId == query.ItemId && x.HighQuality == query.HighQuality)
                .Aggregate(0UL, (sum, x) => checked(sum + x.Quantity));
            hints.Add(new(owner, query.ItemId, query.HighQuality, quantity, snapshot.RetrievedAt));
        }
        return new(query.ItemId, query.HighQuality, "Cached hints only. Missing owners and zero values do not prove storage has been scanned.", hints);
    }
}
