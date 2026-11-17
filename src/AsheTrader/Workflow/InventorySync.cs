using AsheTrader.Core;
using AsheTrader.Core.Networking;
using AsheTrader.Core.Search;
using AsheTrader.Integrations;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace AsheTrader.Workflow;

internal sealed record PrivateInventoryCache(PeerRoster? Roster, ItemSearchSource? Inventory);
internal sealed record InventorySyncRequest(Guid Id, List<uint> ItemIds);

public sealed partial class WorkflowService
{
    private SettingsFileStore inventorySyncFile = null!;
    private PeerRoster? savedRemoteRoster;
    private ItemSearchSource? savedRemoteInventory;
    private AllaganItemSearch? inventorySender;
    private ItemSearchPageBuffer? inventoryReceiver;
    private Guid inventoryRequestId;
    private Guid inventorySendId;
    private Guid? inventoryConnection;
    private Guid? inventoryObservedConnection;
    private int inventorySendOffset;
    private int inventorySendSequence;
    private long nextInventorySync;
    private long nextInventorySend;
    private long inventoryProgressAt;
    private uint[]? inventoryCatalog;
    public DateTimeOffset? RemoteInventorySyncedAt => savedRemoteInventory?.RetrievedAt;
    public string InventorySyncStatus { get; private set; } = "Inventory sync starts when both trusted PCs are connected.";

    private void InitializeInventorySync(string root)
    {
        inventorySyncFile = new(Path.Combine(root, "trusted-pc-inventory.json"));
        var text = inventorySyncFile.Load();
        if (text == null) return;
        var cache = JsonConvert.DeserializeObject<PrivateInventoryCache>(text);
        if (cache == null) return;
        if (cache.Roster?.PeerId != Settings.ExpectedRemotePeerId) return;
        savedRemoteRoster = cache.Roster;
        if (cache.Inventory?.PeerId == Settings.ExpectedRemotePeerId) savedRemoteInventory = cache.Inventory;
        RemoteRoster = savedRemoteRoster;
    }
    private void SaveRemoteRoster(PeerRoster roster)
    {
        savedRemoteRoster = Clone(roster);
        SaveInventorySync();
    }
    private void SaveInventorySync()
    {
        if (!inventorySyncFile.TrySave(JsonConvert.SerializeObject(new PrivateInventoryCache(savedRemoteRoster, savedRemoteInventory)), out var failure))
            InventorySyncStatus = "Inventory is available for this session, but could not be saved: " + failure;
    }
    private void ClearInventorySync()
    {
        savedRemoteInventory = null; inventoryReceiver = null; inventorySender = null;
        inventorySyncFile.TrySave("null", out _);
        CancelItemSearch(); remoteSearchSource = null; PublishItemSearch();
    }
    private uint[] InventoryCatalog() => inventoryCatalog ??= Plugin.DataManager.GetExcelSheet<Item>()
        .Where(x => x.RowId > 1 && !x.Name.IsEmpty).Select(x => x.RowId).ToArray();

    private ItemSearchSource? OfflineInventory(IReadOnlyList<uint> requested)
    {
        if (savedRemoteInventory is not { } saved || saved.PeerId != Settings.ExpectedRemotePeerId) return null;
        var ids = requested.ToHashSet();
        return saved with
        {
            Locations = saved.Locations.Where(x => ids.Contains(x.ItemId)).ToArray(),
            Warnings = [.. saved.Warnings, "Saved snapshot from the other PC. Its inventories may have changed since this sync. Refresh after reconnecting for a current cache query."],
            CheckedItems = ids.Count, TotalItems = ids.Count
        };
    }
    public void RefreshPrivateInventory() => nextInventorySync = 0;

    private void UpdateInventorySync(long now)
    {
        try
        {
            var current = Connected && HasAcceptedConnection ? connectionId : null;
            if (current != inventoryObservedConnection)
            {
                inventoryObservedConnection = current; inventoryReceiver = null; inventorySender = null;
                nextInventorySync = 0; inventoryConnection = null; remoteReady = false;
                if (!current.HasValue) RemoteRoster = savedRemoteRoster;
            }
            if (!current.HasValue || RemoteRoster is not { ProtocolVersion: >= 2 }) return;
            if (now >= nextInventorySync && inventoryReceiver == null)
            {
                var ids = InventoryCatalog();
                inventoryRequestId = Guid.NewGuid(); inventoryConnection = current;
                inventoryReceiver = new(inventoryRequestId, current.Value, ids);
                inventoryProgressAt = now; nextInventorySync = now + 300_000;
                Send("inventory-sync", new InventorySyncRequest(inventoryRequestId, ids.ToList()));
                InventorySyncStatus = "Syncing private inventory from the trusted PC…";
            }
            // Read at most one IPC per frame; sending is paced independently of native game work.
            if (inventorySender is { Complete: false }) inventorySender.Step();
            if (inventorySender is { } sender && now >= nextInventorySend)
            {
                var page = sender.Results.Skip(inventorySendOffset).Take(SearchPageSize).ToList();
                var end = inventorySendOffset + page.Count;
                var capped = end >= MaxSearchLocations;
                var done = capped || sender.Complete && end == sender.Results.Count;
                var warnings = sender.Warnings.Take(62).Select(x => x.Length > 300 ? x[..300] : x).ToList();
                if (capped) warnings.Add("Private inventory snapshot reached its result limit.");
                Send("inventory-sync-result", new ItemSearchPacket(inventorySendId, inventorySendSequence++, done,
                    sender.Error, warnings, sender.RetrievedAt, sender.CheckedItems, sender.TotalItems, page));
                inventorySendOffset = end; nextInventorySend = now + (page.Count > 0 ? 100 : 1000);
                if (done) inventorySender = null;
            }
            if (inventoryReceiver != null && now - inventoryProgressAt > 30_000)
            { inventoryReceiver = null; InventorySyncStatus = "Inventory sync timed out. The last saved snapshot is retained; retrying automatically."; nextInventorySync = now + 30_000; }
        }
        catch (Exception ex)
        {
            inventoryReceiver = null; inventorySender = null; nextInventorySync = now + 30_000;
            InventorySyncStatus = "Inventory sync will retry; previous snapshot retained. " + ex.Message;
            Plugin.Log.Debug(ex, "Private inventory sync failed; game workflow is unaffected");
        }
    }

    private bool ReceiveInventorySync(PeerMessage message)
    {
        if (message.Kind is not ("inventory-sync" or "inventory-sync-result")) return false;
        try
        {
            if (message.Kind == "inventory-sync")
            {
                var request = Decode<InventorySyncRequest>(message.Payload);
                if (request.Id == Guid.Empty || request.ItemIds == null || request.ItemIds.Count > 100_000
                    || request.ItemIds.Any(x => x <= 1) || request.ItemIds.Distinct().Count() != request.ItemIds.Count)
                    throw new InvalidDataException("Invalid inventory sync request.");
                inventorySender = new(request.ItemIds, plugin.Config.Characters, registeredOwnersOnly: true);
                inventorySendId = request.Id; inventorySendOffset = inventorySendSequence = 0; nextInventorySend = 0;
            }
            else
            {
                var packet = Decode<ItemSearchPacket>(message.Payload);
                if (inventoryReceiver == null || packet.RequestId != inventoryRequestId || inventoryConnection != connectionId) return true;
                if (packet.Warnings == null || packet.Warnings.Count > 64 || packet.Error == null || packet.Error.Length > 1024
                    || packet.Warnings.Any(x => x == null || x.Length > 300)
                    || !packet.RetrievedAt.HasValue || packet.RetrievedAt > DateTimeOffset.UtcNow.AddMinutes(5)) throw new InvalidDataException("Invalid inventory sync metadata.");
                if (!inventoryReceiver.TryAppend(packet.RequestId, connectionId!.Value, packet.Sequence, packet.CheckedItems, packet.TotalItems, packet.Locations, out var failure))
                    throw new InvalidDataException(failure);
                inventoryProgressAt = Environment.TickCount64;
                if (!packet.Complete) return true;
                if (packet.Error.Length == 0)
                {
                    savedRemoteInventory = new(acceptedPeerId!.Value, RemoteRoster?.Name ?? "Trusted PC", true, "", packet.Warnings,
                        packet.RetrievedAt, inventoryReceiver.Locations.ToArray(), packet.CheckedItems, packet.TotalItems);
                    SaveInventorySync();
                    InventorySyncStatus = $"Private inventory synced at {packet.RetrievedAt.Value.ToLocalTime():g}. Available offline on this PC.";
                }
                else InventorySyncStatus = "The other PC could not refresh its inventory. Previous snapshot retained. " + packet.Error;
                inventoryReceiver = null;
            }
        }
        catch (Exception ex) { inventoryReceiver = null; InventorySyncStatus = ex.Message; }
        return true;
    }
}
