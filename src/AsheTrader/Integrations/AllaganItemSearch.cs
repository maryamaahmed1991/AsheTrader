using AsheTrader.Core;
using AsheTrader.Core.Search;

namespace AsheTrader.Integrations;

/// <summary>
/// Incremental, read-only search of every personal owner known to Allagan Tools on this PC.
/// The public IPC cannot enumerate all cached owners, so item-count queries discover owners
/// first; each discovered owner's inventory is then read once. No character visits are made.
/// </summary>
internal sealed class AllaganItemSearch
{
    private readonly uint[] itemIds;
    private readonly HashSet<uint> requestedItems;
    private readonly Dictionary<ulong, ItemSearchOwnerLabel> labels = [];
    private readonly Dictionary<ulong, Dictionary<uint, uint>> expectedOwners = [];
    private readonly List<ItemSearchLocation> results = [];
    private readonly List<string> warnings = [];
    private readonly Queue<ulong> labelCharacters = [];
    private Queue<ulong>? inventoryOwners;
    private bool initialized;
    private bool ownerDiscoveryStopped;
    private bool labelDiscoveryAttempted;
    private bool labelsUnavailable;

    public bool Complete { get; private set; }
    public int CheckedItems { get; private set; }
    public int TotalItems => itemIds.Length;
    public string Error { get; private set; } = string.Empty;
    public IReadOnlyList<string> Warnings => warnings;
    public IReadOnlyList<ItemSearchLocation> Results { get; private set; } = [];
    // This is cache retrieval time, never a claim about the game's last observation time.
    public DateTimeOffset RetrievedAt { get; } = DateTimeOffset.UtcNow;

    public AllaganItemSearch(IReadOnlyList<uint> itemIds, IReadOnlyList<CharacterProfile> knownCharacters)
    {
        this.itemIds = itemIds.Where(id => id != 0).Distinct().ToArray();
        requestedItems = this.itemIds.ToHashSet();
        foreach (var character in knownCharacters.Where(character => character.ContentId != 0))
        {
            var name = CleanLabel(character.Name, $"Character {character.ContentId}");
            labels[character.ContentId] = new(name, name, CleanLabel(character.World));
            labelCharacters.Enqueue(character.ContentId);
        }
        if (this.itemIds.Length == 0) Complete = true;
    }

    /// <summary>Call on the framework thread. Each call performs at most one IPC invocation.</summary>
    public void Step()
    {
        if (Complete) return;
        if (!Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading)
        { Fail("Item Search could not read the game framework. Please try again."); return; }
        if (!initialized)
        {
            try
            {
                if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized").InvokeFunc())
                { Fail("Allagan Tools is not ready on this PC. Enable it, then refresh the search."); return; }
                initialized = true;
            }
            catch (Exception ex)
            { Plugin.Log.Debug(ex, "Item Search: Allagan Tools availability check failed"); Fail("Allagan Tools is unavailable on this PC. Enable it, then refresh the search."); }
            return;
        }
        if (!ownerDiscoveryStopped && CheckedItems < itemIds.Length)
        {
            ReadItemOwners(itemIds[CheckedItems++]);
            return;
        }
        if (expectedOwners.Count == 0) { Complete = true; return; }
        if (!labelDiscoveryAttempted)
        {
            labelDiscoveryAttempted = true;
            // Labels are optional. AT still discovers every owner when AR is absent or stale.
            try
            {
                var registered = Plugin.PluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs").InvokeFunc();
                foreach (var id in (registered ?? []).Where(id => id != 0).Distinct())
                    if (!labelCharacters.Contains(id)) labelCharacters.Enqueue(id);
            }
            catch (Exception ex) { Plugin.Log.Debug(ex, "Item Search: optional AutoRetainer name lookup unavailable"); labelsUnavailable = true; }
            return;
        }
        if (!labelsUnavailable && labelCharacters.Count > 0 && expectedOwners.Keys.Any(owner => !labels.ContainsKey(owner)))
        {
            ReadCharacterLabels(labelCharacters.Dequeue());
            return;
        }
        inventoryOwners ??= new Queue<ulong>(expectedOwners.Keys.Order());
        if (inventoryOwners.Count == 0) { Complete = true; return; }
        ReadOwner(inventoryOwners.Dequeue());
        if (inventoryOwners.Count == 0) Complete = true;
    }

    private void ReadItemOwners(uint itemId)
    {
        try
        {
            // Empty categories means every personal storage category. Shared FC/house owners
            // are excluded, preventing shared chests being counted once by each paired PC.
            var counts = Plugin.PluginInterface.GetIpcSubscriber<uint, bool, uint[], bool, Dictionary<ulong, uint>>(
                "AllaganTools.GetItemCountsByCharacter").InvokeFunc(itemId, false, [], false)
                ?? throw new InvalidDataException("No owner-count response.");
            foreach (var (ownerId, quantity) in counts)
            {
                if (ownerId == 0) { Warn("Allagan Tools returned an unidentified inventory owner; that entry was omitted."); continue; }
                if (quantity == 0) continue;
                if (!expectedOwners.TryGetValue(ownerId, out var items)) expectedOwners[ownerId] = items = [];
                items[itemId] = quantity;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Item Search: Allagan Tools owner discovery failed for item {ItemId}", itemId);
            // An unavailable/incompatible IPC is unlikely to recover by issuing thousands of
            // identical calls. Preserve owners already found and finish their readable caches.
            Error = "Allagan Tools could not finish finding item owners. Results may be incomplete; refresh when it is ready.";
            Warn(Error);
            ownerDiscoveryStopped = true;
        }
    }

    private void ReadCharacterLabels(ulong characterId)
    {
        try
        {
            var character = Plugin.PluginInterface.GetIpcSubscriber<ulong, CharacterWireData?>(
                "AutoRetainer.GetOfflineCharacterData").InvokeFunc(characterId);
            if (character?.CID != characterId) return;
            var characterName = CleanLabel(character.Name, $"Character {characterId}");
            var world = CleanLabel(character.World);
            labels[characterId] = new(characterName, characterName, world);
            foreach (var retainer in character.RetainerData ?? [])
            {
                if (retainer.RetainerID == 0 || retainer.RetainerID == characterId) continue;
                var name = CleanLabel(retainer.Name, $"Retainer {retainer.RetainerID}");
                labels.TryAdd(retainer.RetainerID, new(name, characterName, world));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Item Search: optional character name lookup failed for {CharacterId}", characterId);
            labelsUnavailable = true;
        }
    }

    private void ReadOwner(ulong ownerId)
    {
        try
        {
            var records = Plugin.PluginInterface.GetIpcSubscriber<ulong, HashSet<ulong[]>>(
                "AllaganTools.GetCharacterItems").InvokeFunc(ownerId)
                ?? throw new InvalidDataException("No cached inventory response.");
            var label = labels.GetValueOrDefault(ownerId) ?? new ItemSearchOwnerLabel($"Owner {ownerId}", string.Empty, string.Empty);
            var rows = AllaganItemSearchParser.Read(ownerId, records, requestedItems, label, out var incomplete);
            results.AddRange(rows);
            Results = results.ToArray();
            var currentCounts = rows.GroupBy(row => row.ItemId).ToDictionary(group => group.Key,
                group => group.Aggregate(0UL, (sum, row) => checked(sum + row.Quantity)));
            if (incomplete) Warn($"Some cached slots for {label.OwnerName} were invalid or conflicted and were omitted.");
            if (expectedOwners[ownerId].Any(item => currentCounts.GetValueOrDefault(item.Key) != item.Value))
                Warn($"The cache for {label.OwnerName} changed or was incomplete during this search. Showing the readable inventory; refresh for a new tally.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Item Search: cached owner inventory failed for {OwnerId}", ownerId);
            var name = labels.GetValueOrDefault(ownerId)?.OwnerName ?? $"Owner {ownerId}";
            Warn($"Could not read {name}'s cached inventory. Other available results are shown.");
        }
    }

    private void Fail(string message) { Error = message; Warn(message); Complete = true; }
    private static string CleanLabel(string? value, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return new string(value.Where(character => !char.IsControl(character)).Take(128).ToArray()).Trim();
    }

    private void Warn(string message)
    {
        if (warnings.Contains(message)) return;
        if (warnings.Count < 100) warnings.Add(message);
        else if (warnings.Count == 100) warnings.Add("Additional inventories could not be read. Refresh the search after checking Allagan Tools.");
    }

    private sealed class CharacterWireData
    {
        public ulong CID { get; set; }
        public string? Name { get; set; }
        public string? World { get; set; }
        public List<RetainerWireData>? RetainerData { get; set; }
    }

    private sealed class RetainerWireData
    {
        public ulong RetainerID { get; set; }
        public string? Name { get; set; }
    }
}
