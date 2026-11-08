namespace AsheTrader.Integrations;

public sealed record CachedInventorySlot(ulong OwnerId, uint Container, uint Slot, uint ItemId, uint Quantity, bool HighQuality);
public sealed record AllaganInventorySnapshot(ulong OwnerId, DateTimeOffset RetrievedAt, IReadOnlyList<CachedInventorySlot> Items)
{
    // Allagan's IPC supplies cached data without a trustworthy observation timestamp.
    public bool Verified => false;
}

/// <summary>Optional read-only cache integration. Never treats absence as a verified zero.</summary>
public sealed class AllaganToolsClient
{
    public bool IsAvailable
    {
        get
        {
            if (!Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading) return false;
            try { return Plugin.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized").InvokeFunc(); }
            catch { return false; }
        }
    }

    public bool TryGetKnownItemHolders(uint itemId, out IReadOnlyList<ulong> owners, out string error)
    {
        owners = [];
        if (!IsAvailable) { error = "Allagan Tools is unavailable. Live character and retainer scanning is still required."; return false; }
        try
        {
            // The returned values count both qualities and may include other inventory categories.
            // Use only owner IDs as hints; ReadOwner filters the physical bag containers precisely.
            var counts = Plugin.PluginInterface.GetIpcSubscriber<uint, bool, uint[], bool, Dictionary<ulong, uint>>(
                "AllaganTools.GetItemCountsByCharacter").InvokeFunc(itemId, false, [], false);
            owners = counts.Keys.Where(id => id != 0).ToArray();
            error = string.Empty;
            return true;
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "Allagan Tools holder query failed"); error = "Allagan Tools did not supply a compatible cached holder list."; return false; }
    }

    public bool TryReadOwner(ulong ownerId, out AllaganInventorySnapshot? snapshot, out string error)
    {
        snapshot = null;
        if (!IsAvailable) { error = "Allagan Tools is unavailable."; return false; }
        try
        {
            var records = Plugin.PluginInterface.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems").InvokeFunc(ownerId);
            var result = new List<CachedInventorySlot>();
            foreach (var record in records)
            {
                if (record.Length < 24 || record[23] != ownerId || record[0] > uint.MaxValue || record[1] > uint.MaxValue || record[2] > uint.MaxValue || record[3] > uint.MaxValue)
                    throw new InvalidDataException("Allagan Tools returned an incompatible inventory record.");
                var container = (uint)record[0];
                if (!(container <= 3 || container is >= 10000 and <= 10006) || record[2] == 0 || record[3] == 0) continue;
                // Flags other than HQ designate unsupported collectible or special inventory items.
                if ((record[6] & ~1UL) != 0) continue;
                result.Add(new(ownerId, container, (uint)record[1], (uint)record[2], (uint)record[3], (record[6] & 1) != 0));
            }
            if (result.GroupBy(x => (x.Container, x.Slot)).Any(x => x.Count() > 1))
                throw new InvalidDataException("Allagan Tools returned duplicate physical inventory slots.");
            snapshot = new(ownerId, DateTimeOffset.UtcNow, result.AsReadOnly());
            error = string.Empty;
            return true;
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "Allagan Tools inventory query failed"); error = "Allagan Tools did not supply a compatible cached inventory."; return false; }
    }
}
