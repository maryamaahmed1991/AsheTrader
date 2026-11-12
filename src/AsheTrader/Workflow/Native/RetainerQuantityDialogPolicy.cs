namespace AsheTrader.Workflow.Native;

internal sealed record QuantityDialogAddon(uint Id, uint ParentId, uint HostId, bool ReadyAndVisible);

/// <summary>Checks a quantity dialog's UI owners against the active inventory windows.</summary>
internal static class RetainerQuantityDialogPolicy
{
    public static bool IsOwned(uint parentId, uint blockedParentId, IReadOnlySet<uint> inventoryRoots,
        IReadOnlyDictionary<uint, QuantityDialogAddon> addons)
    {
        // Zero is an absent relationship, never proof of ownership. Every relationship
        // the game supplies must lead to an approved, visible inventory root.
        if (parentId == 0 && blockedParentId == 0) return false;
        return (parentId == 0 || ReachesInventory(parentId, inventoryRoots, addons))
            && (blockedParentId == 0 || ReachesInventory(blockedParentId, inventoryRoots, addons));
    }

    private static bool ReachesInventory(uint start, IReadOnlySet<uint> roots, IReadOnlyDictionary<uint, QuantityDialogAddon> addons)
    {
        var seen = new HashSet<uint>();
        var pending = new Queue<(uint Id, int Depth)>();
        pending.Enqueue((start, 0));
        while (pending.TryDequeue(out var current))
        {
            if (current.Id == 0 || !seen.Add(current.Id) || current.Depth > 8) continue;
            if (roots.Contains(current.Id)) return true;
            if (!addons.TryGetValue(current.Id, out var addon) || !addon.ReadyAndVisible) continue;
            pending.Enqueue((addon.ParentId, current.Depth + 1));
            pending.Enqueue((addon.HostId, current.Depth + 1));
        }
        return false;
    }

    public static bool LimitsMatch(uint quantity, uint sourceQuantity, uint minimum, uint maximum)
        => quantity > 0 && quantity < sourceQuantity && quantity <= int.MaxValue
            && minimum <= quantity && quantity <= maximum && maximum <= sourceQuantity;
}
