namespace AsheTrader.Core;

public sealed class CharacterProfile
{
    public ulong ContentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public string AccountLabel { get; set; } = string.Empty;
    public int ServiceAccount { get; set; }
    public FuelPolicy Fuel { get; set; } = new();
}

public sealed class FuelPolicy
{
    public bool Enabled { get; set; }
    public bool ExhaustCredits { get; set; }
    public Guid? HouseId { get; set; }
    public uint BuyBelow { get; set; }
    public uint TargetQuantity { get; set; }
}

public sealed class HouseProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
}

public sealed class TradeList
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New trade list";
    public List<TradeLine> Items { get; set; } = [];
}

public sealed class TradeLine
{
    public uint ItemId { get; set; }
    public bool HighQuality { get; set; }
    public uint Quantity { get; set; }
}

public readonly record struct ItemKey(uint ItemId, bool HighQuality)
{
    public override string ToString() => $"{ItemId}{(HighQuality ? " HQ" : "")}";
}

public sealed record InventoryStack(
    int Container,
    int Slot,
    uint ItemId,
    bool HighQuality,
    uint Quantity,
    uint StackSize,
    bool Tradable)
{
    public ItemKey Key => new(ItemId, HighQuality);
}

public sealed record TradeAllocation(int Container, int Slot, uint ItemId, bool HighQuality, uint Quantity)
{
    public ItemKey Key => new(ItemId, HighQuality);
}

public sealed record TradeShortage(ItemKey Item, uint Requested, ulong Available)
{
    public ulong Missing => Requested > Available ? Requested - Available : 0;
    public override string ToString() => $"Item {Item}: requested {Requested}, only {Available} tradable units available (missing {Missing}).";
}

public sealed class TradeBatch
{
    internal TradeBatch(IEnumerable<TradeAllocation> entries, IEnumerable<TradeShortage> shortages)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        Shortages = Array.AsReadOnly(shortages.ToArray());
    }

    public Guid Id { get; } = Guid.NewGuid();
    public IReadOnlyList<TradeAllocation> Entries { get; }
    public IReadOnlyList<TradeShortage> Shortages { get; }
    public bool HasShortage => Shortages.Count != 0;
    public bool HasItems => Entries.Count != 0;
    public ulong TotalQuantity => Entries.Aggregate(0UL, (total, entry) => total + entry.Quantity);
}
