using AsheTrader.Core.Search;

namespace AsheTrader.Integrations;

internal sealed record ItemSearchOwnerLabel(string OwnerName, string CharacterName, string World);

/// <summary>Reads AT's detached numeric cache records without requiring complete inventory pages.</summary>
internal static class AllaganItemSearchParser
{
    internal static IReadOnlyList<ItemSearchLocation> Read(ulong ownerId, IEnumerable<ulong[]> records,
        IReadOnlySet<uint> requestedItems, ItemSearchOwnerLabel label, out bool incomplete)
    {
        ArgumentNullException.ThrowIfNull(records);
        incomplete = false;
        var slots = new Dictionary<(uint Container, uint Slot), (uint Item, uint Quantity, uint Flags)>();
        var conflicts = new HashSet<(uint Container, uint Slot)>();
        foreach (var record in records)
        {
            // CriticalCommonLib InventoryItem.ToNumeric uses sorted container/slot 20/22 and owner 23.
            if (record == null || record.Length < 25 || record[2] > uint.MaxValue || record[3] > uint.MaxValue
                || record[6] > uint.MaxValue || record[20] > uint.MaxValue || record[22] > int.MaxValue)
            { incomplete = true; continue; }
            if (record[2] == 0 && record[3] == 0) continue; // FillSlots can use owner=0 for empty placeholders.
            if (record[2] == 0 || record[3] == 0 || record[23] != ownerId)
            { incomplete = true; continue; }
            var key = ((uint)record[20], (uint)record[22]);
            var value = ((uint)record[2], (uint)record[3], (uint)record[6]);
            if (conflicts.Contains(key)) continue;
            if (slots.TryGetValue(key, out var previous))
            {
                if (previous != value)
                {
                    slots.Remove(key);
                    conflicts.Add(key);
                    incomplete = true;
                }
                continue; // Identical duplicate records describe one physical slot.
            }
            slots.Add(key, value);
        }
        return slots.Where(slot => requestedItems.Contains(slot.Value.Item))
            .GroupBy(slot => (slot.Value.Item, slot.Value.Flags, slot.Key.Container))
            .Select(group => new ItemSearchLocation(group.Key.Item, ownerId, label.OwnerName,
                label.CharacterName, label.World, StorageName(group.Key.Container), group.Key.Container,
                group.Key.Flags, group.Aggregate(0UL, (sum, slot) => checked(sum + slot.Value.Quantity))))
            .OrderBy(row => row.ItemId).ThenBy(row => row.Container).ThenBy(row => row.ItemFlags).ToArray();
    }

    internal static string StorageName(uint container) => container switch
    {
        <= 3 => $"Inventory · bag {container + 1}",
        1000 => "Equipped gear",
        1001 => "Gear storage",
        2000 => "Currency",
        2001 => "Crystals",
        2003 => "Mail",
        2004 => "Key items",
        2005 => "Hand-in items",
        2007 => "Damaged gear",
        2009 => "Examine",
        2500 => "Armoire",
        2501 => "Glamour dresser",
        2502 => "Free company currency",
        >= 3200 and <= 3209 or 3300 or 3400 or 3500 => "Armoury chest",
        4000 or 4001 => $"Saddlebag · bag {container - 3999}",
        4100 or 4101 => $"Premium saddlebag · bag {container - 4099}",
        >= 10000 and <= 10006 => $"Retainer inventory · bag {container - 9999}",
        11000 => "Retainer equipped gear",
        12000 => "Retainer gil",
        12001 => "Retainer crystals",
        12002 => "Retainer market listings",
        _ => $"Storage {container}",
    };
}
