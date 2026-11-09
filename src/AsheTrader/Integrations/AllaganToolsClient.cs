using Lumina.Excel.Sheets;

namespace AsheTrader.Integrations;

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
        if (!IsAvailable) { error = "Allagan Tools is unavailable. This character needs a live inventory scan."; return false; }
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

    /// <summary>Reads an offline character and every AR-known retainer; never initiates login or gameplay.</summary>
    public bool TryReadCharacter(ulong characterId, uint itemId, bool highQuality,
        out AllaganCharacterInventorySnapshot? snapshot, out string error)
    {
        snapshot = null;
        if (!IsAvailable) { error = "Allagan Tools is unavailable. This character needs a live inventory scan."; return false; }
        try
        {
            var item = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
            if (!item.HasValue || item.Value.StackSize == 0 || item.Value.IsUntradable)
                throw new InvalidDataException("The selected item cannot be traded.");
            // AT only maps retainers for the active character. AR's detached offline roster supplies
            // the owner IDs for any registered character without changing its login or configuration.
            var roster = Plugin.PluginInterface.GetIpcSubscriber<ulong, CharacterRetainerWireData?>(
                "AutoRetainer.GetOfflineCharacterData").InvokeFunc(characterId);
            if (roster == null || roster.CID != characterId || roster.RetainerData == null || roster.RetainerData.Count == 0)
                throw new InvalidDataException("AutoRetainer has no known retainer roster for this character.");
            if (roster.RetainerData.Count > 10 || roster.RetainerData.Any(x => x.RetainerID == 0 || x.RetainerID == characterId || string.IsNullOrWhiteSpace(x.Name))
                || roster.RetainerData.Select(x => x.RetainerID).Distinct().Count() != roster.RetainerData.Count)
                throw new InvalidDataException("AutoRetainer's cached retainer roster is incomplete or incompatible.");
            var bags = ReadInventory(characterId, false, itemId, highQuality, item.Value.StackSize);
            var retainers = new List<AllaganRetainerInventorySnapshot>();
            foreach (var retainer in roster.RetainerData)
            {
                var inventory = ReadInventory(retainer.RetainerID, true, itemId, highQuality, item.Value.StackSize);
                retainers.Add(new(retainer.RetainerID, retainer.Name!, inventory.Quantity, inventory.Capacity));
            }
            snapshot = new(characterId, bags.Quantity, bags.Capacity, item.Value.StackSize, DateTimeOffset.UtcNow, retainers.AsReadOnly());
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Allagan Tools cached character inventory query failed");
            error = ex is InvalidDataException ? ex.Message : "Could not read compatible Allagan Tools inventory and AutoRetainer roster caches.";
            return false;
        }
    }

    private static CachedContainerInventory ReadInventory(ulong ownerId, bool retainer, uint itemId, bool highQuality, uint stackSize) =>
        AllaganInventoryCache.Read(ownerId, Plugin.PluginInterface.GetIpcSubscriber<ulong, HashSet<ulong[]>>(
            "AllaganTools.GetCharacterItems").InvokeFunc(ownerId), retainer, itemId, highQuality, stackSize);

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
                if (record.Length < 25 || record[20] > uint.MaxValue || record[22] > uint.MaxValue || record[2] > uint.MaxValue || record[3] > uint.MaxValue)
                    throw new InvalidDataException("Allagan Tools returned an incompatible inventory record.");
                var container = (uint)record[20];
                if (!(container <= 3 || container is >= 10000 and <= 10004) || record[2] == 0 || record[3] == 0) continue;
                if (record[23] != ownerId) throw new InvalidDataException("Allagan Tools returned another owner's cached item.");
                // Flags other than HQ designate unsupported collectible or special inventory items.
                if ((record[6] & ~1UL) != 0) continue;
                result.Add(new(ownerId, container, (uint)record[22], (uint)record[2], (uint)record[3], (record[6] & 1) != 0));
            }
            if (result.GroupBy(x => (x.Container, x.Slot)).Any(x => x.Count() > 1))
                throw new InvalidDataException("Allagan Tools returned duplicate physical inventory slots.");
            snapshot = new(ownerId, DateTimeOffset.UtcNow, result.AsReadOnly());
            error = string.Empty;
            return true;
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "Allagan Tools inventory query failed"); error = "Allagan Tools did not supply a compatible cached inventory."; return false; }
    }

    // Public setters allow Dalamud's JSON conversion to produce detached data without an AR assembly dependency.
    private sealed class CharacterRetainerWireData
    {
        public ulong CID { get; set; }
        public List<RetainerWireData>? RetainerData { get; set; }
    }

    private sealed class RetainerWireData
    {
        public ulong RetainerID { get; set; }
        public string? Name { get; set; }
    }
}
