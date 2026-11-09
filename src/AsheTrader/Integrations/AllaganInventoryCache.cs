namespace AsheTrader.Integrations;

public sealed record CachedInventorySlot(ulong OwnerId, uint Container, uint Slot, uint ItemId, uint Quantity, bool HighQuality);
public sealed record AllaganInventorySnapshot(ulong OwnerId, DateTimeOffset RetrievedAt, IReadOnlyList<CachedInventorySlot> Items)
{
    // The IPC supplies retrieval time, not a trustworthy last-observed timestamp.
    public bool Verified => false;
}

public sealed record AllaganRetainerInventorySnapshot(ulong RetainerId, string Name, uint Quantity, uint AdditionalCapacity);
public sealed record AllaganCharacterInventorySnapshot(
    ulong CharacterId, uint BagQuantity, uint BagCapacity, uint StackSize, DateTimeOffset RetrievedAt,
    IReadOnlyList<AllaganRetainerInventorySnapshot> Retainers);

internal sealed record CachedContainerInventory(uint Quantity, uint Capacity, IReadOnlyList<CachedInventorySlot> Items);

/// <summary>Decodes Allagan's numeric IPC records without treating missing pages as empty pages.</summary>
internal static class AllaganInventoryCache
{
    // CriticalCommonLib InventoryItem.ToNumeric: sorted container/slot are 20/22 and owner is 23.
    // AT represents retainer inventory as five sorted pages of 35 (175 total), even though
    // the game's native container API exposes seven pages of 25.
    public static CachedContainerInventory Read(ulong ownerId, IEnumerable<ulong[]> records, bool retainer,
        uint itemId, bool highQuality, uint stackSize)
    {
        if (ownerId == 0 || itemId == 0 || stackSize == 0) throw new ArgumentException("Inventory identity and stack size are required.");
        ArgumentNullException.ThrowIfNull(records);
        var firstContainer = retainer ? 10000U : 0U;
        var pages = retainer ? 5U : 4U;
        var slots = new Dictionary<(uint Container, uint Slot), CachedInventorySlot>();
        var observedBagGroup = false;
        ulong quantity = 0, capacity = 0;
        foreach (var record in records)
        {
            if (record == null || record.Length < 25) throw new InvalidDataException("Allagan Tools returned an incompatible inventory record.");
            if (record[20] < firstContainer || record[20] >= firstContainer + pages) continue;
            if (record[22] >= 35 || record[2] > uint.MaxValue || record[3] > uint.MaxValue)
                throw new InvalidDataException("Allagan Tools returned an invalid cached bag slot.");
            var empty = record[2] == 0 && record[3] == 0;
            // FillSlots creates owner=0 placeholders on cache load. They are valid only when empty.
            if (record[23] != ownerId && !(record[23] == 0 && empty))
                throw new InvalidDataException("Allagan Tools returned inventory belonging to a different owner.");
            observedBagGroup |= record[23] == ownerId;
            if ((record[2] == 0) != (record[3] == 0)) throw new InvalidDataException("Allagan Tools returned an inconsistent cached item quantity.");
            var container = (uint)record[20]; var slot = (uint)record[22];
            var cached = new CachedInventorySlot(ownerId, container, slot, (uint)record[2], (uint)record[3], record[6] == 1);
            if (!slots.TryAdd((container, slot), cached)) throw new InvalidDataException("Allagan Tools returned duplicate cached inventory slots.");
            if (empty) { capacity += stackSize; continue; }
            if (record[2] != itemId || record[6] != (highQuality ? 1UL : 0UL)) continue;
            if (record[3] > stackSize) throw new InvalidDataException("The cached item exceeds its stack size.");
            quantity += record[3];
            if (record[4] == 0 && record[19] == 0) capacity += stackSize - record[3];
        }
        // FillSlots can synthesize an entire never-observed category. The monitor loads each bag
        // group atomically; require an owner-tagged witness in that group as well as every slot.
        // An entirely empty saved group has no witness and deliberately falls back to a live scan.
        if (!observedBagGroup || slots.Count != pages * 35)
            throw new InvalidDataException(retainer
                ? "Allagan Tools has no complete cached inventory for this retainer."
                : "Allagan Tools has no complete cached carried inventory for this character.");
        return new(checked((uint)quantity), (uint)Math.Min(uint.MaxValue, capacity), slots.Values.ToArray());
    }
}
