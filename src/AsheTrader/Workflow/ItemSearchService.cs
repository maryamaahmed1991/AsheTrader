using System.Diagnostics;
using AsheTrader.Core.Networking;
using AsheTrader.Core.Search;
using AsheTrader.Integrations;
using Lumina.Excel.Sheets;

namespace AsheTrader.Workflow;

public sealed record ItemSearchView(string Query, bool Busy, ItemSearchSource? Local, ItemSearchSource? Remote,
    IReadOnlyList<ItemSearchRow> Rows, string Error, bool Paired);

internal sealed record ItemSearchRequest(Guid RequestId, List<uint> ItemIds);
internal sealed record ItemSearchPacket(Guid RequestId, int Sequence, bool Complete, string Error,
    List<string> Warnings, DateTimeOffset? RetrievedAt, int CheckedItems, int TotalItems, List<ItemSearchLocation> Locations);

/// <summary>Read-only searches have their own lifetime, errors and progress; they never own a worker or pause a job.</summary>
public sealed partial class WorkflowService
{
    private const int SearchPageSize = 64;
    private const int MaxSearchItems = 50_000;
    private const int MaxSearchLocations = 250_000;
    private IReadOnlyList<ItemSearchCatalogItem>? searchCatalog;
    private IReadOnlyList<ItemSearchCatalogItem> searchMatches = [];
    private AllaganItemSearch? localSearch;
    private AllaganItemSearch? peerSearch;
    private Guid searchId;
    private Guid peerSearchId;
    private Guid? searchConnection;
    private Guid? observedSearchConnection;
    private int peerSearchOffset;
    private int peerSearchSequence;
    private long lastSearchProgress;
    private long nextPeerSearchProgress;
    private long nextLocalSearchProgress;
    private ItemSearchPageBuffer? remoteSearchPages;
    private bool waitingForSearchPeer;
    private string itemSearchQuery = "";
    private ItemSearchSource? localSearchSource;
    private ItemSearchSource? remoteSearchSource;
    public ItemSearchView ItemSearch { get; private set; } = new("", false, null, null, [], "", false);

    public void SearchItems(string query)
    {
        CancelItemSearch();
        itemSearchQuery = query.Trim();
        localSearchSource = remoteSearchSource = null;
        searchMatches = [];
        remoteSearchPages = null;
        try
        {
            var terms = ItemSearchQuery.Parse(query);
            if (terms.Count == 0) { PublishItemSearch(); return; }
            searchCatalog ??= Plugin.DataManager.GetExcelSheet<Item>()
                .Where(x => x.RowId > 1 && !x.Name.IsEmpty)
                .Select(x => new ItemSearchCatalogItem(x.RowId, x.Name.ToString())).ToArray();
            searchMatches = ItemSearchQuery.Match(searchCatalog, query);
            if (searchMatches.Count > MaxSearchItems)
                throw new ArgumentException("That search matches too many item names. Add a longer item-name fragment.");
            searchId = Guid.NewGuid();
            var ids = searchMatches.Select(x => x.ItemId).ToList();
            localSearch = new AllaganItemSearch(ids, plugin.Config.Characters);
            localSearchSource = Source(localSearch, Settings.PeerId, Settings.PeerName);
            nextLocalSearchProgress = 0;
            if (Connected && HasAcceptedConnection)
            {
                searchConnection = connectionId;
                waitingForSearchPeer = true;
                lastSearchProgress = Environment.TickCount64;
                remoteSearchPages = new(searchId, connectionId!.Value, ids);
                remoteSearchSource = new(acceptedPeerId!.Value, RemoteRoster?.Name ?? "Paired PC", false, "", [], null, [], 0, ids.Count);
                Send("item-search", new ItemSearchRequest(searchId, ids));
            }
            PublishItemSearch();
        }
        catch (Exception ex) { SearchFailed(ex); }
    }

    public void CancelItemSearch()
    {
        // Cancelling a search is unrelated to pause/stop commands and cannot interrupt gameplay.
        if (waitingForSearchPeer && Connected && HasAcceptedConnection && searchConnection == connectionId)
        {
            try { Send("item-search-cancel", searchId); }
            catch (Exception ex) { Plugin.Log.Debug(ex, "Could not cancel the paired inventory search"); }
        }
        if (localSearch != null) localSearchSource = Source(localSearch, Settings.PeerId, Settings.PeerName)
            with { Complete = true, Warnings = [.. localSearch.Warnings, "Search cancelled; results may be incomplete."] };
        if (waitingForSearchPeer && remoteSearchSource != null)
            remoteSearchSource = remoteSearchSource with { Complete = true, Warnings = [.. remoteSearchSource.Warnings, "Search cancelled; results may be incomplete."] };
        localSearch = null;
        waitingForSearchPeer = false;
        searchConnection = null;
        PublishItemSearch();
    }

    private static ItemSearchSource Source(AllaganItemSearch reader, Guid id, string name) =>
        new(id, name, reader.Complete, reader.Error, reader.Warnings.ToArray(), reader.RetrievedAt,
            reader.Results.ToArray(), reader.CheckedItems, reader.TotalItems);

    private void PublishItemSearch(string error = "")
    {
        try
        {
            ItemSearch = new(itemSearchQuery, localSearch != null || waitingForSearchPeer, localSearchSource, remoteSearchSource,
                ItemSearchAggregation.Build(searchMatches, localSearchSource ?? new(Settings.PeerId, Settings.PeerName, false, "", [], null, [], 0, 0),
                    Connected && HasAcceptedConnection ? remoteSearchSource : null), error, Connected && HasAcceptedConnection);
        }
        catch (Exception ex)
        {
            // Even an incompatible peer/cache must not escape into the gameplay update's pause handler.
            Plugin.Log.Debug(ex, "Could not aggregate item-search results");
            localSearch = null;
            waitingForSearchPeer = false;
            ItemSearch = new(itemSearchQuery, false, null, null, [],
                "The cached search results were incompatible. Refresh to try again.", Connected && HasAcceptedConnection);
        }
    }

    private void SearchFailed(Exception ex)
    {
        Plugin.Log.Debug(ex, "Item search failed (transfer workflow is unaffected)");
        localSearch = null;
        waitingForSearchPeer = false;
        PublishItemSearch(ex.Message);
    }

    private bool ReceiveItemSearch(PeerMessage message)
    {
        if (message.Kind is not ("item-search" or "item-search-result" or "item-search-cancel")) return false;
        try
        {
            if (!Connected || !HasAcceptedConnection) return true;
            switch (message.Kind)
            {
                case "item-search":
                    var request = Decode<ItemSearchRequest>(message.Payload);
                    if (request.RequestId == Guid.Empty || request.ItemIds == null || request.ItemIds.Count > MaxSearchItems
                        || request.ItemIds.Any(x => x <= 1) || request.ItemIds.Distinct().Count() != request.ItemIds.Count)
                        throw new InvalidDataException("The paired item search is invalid.");
                    peerSearch = new AllaganItemSearch(request.ItemIds, plugin.Config.Characters);
                    peerSearchId = request.RequestId;
                    peerSearchOffset = peerSearchSequence = 0;
                    nextPeerSearchProgress = 0;
                    break;
                case "item-search-cancel":
                    if (Decode<Guid>(message.Payload) == peerSearchId) peerSearch = null;
                    break;
                case "item-search-result":
                    var packet = Decode<ItemSearchPacket>(message.Payload);
                    if (!waitingForSearchPeer || searchConnection != connectionId || packet.RequestId != searchId) return true;
                    if (packet.Warnings == null || packet.Warnings.Count > 64 || packet.Error == null || packet.Error.Length > 1024
                        || packet.Warnings.Any(x => x == null || x.Length > 300) || remoteSearchPages == null)
                        throw new InvalidDataException("The paired PC returned an invalid item-search page.");
                    if (!remoteSearchPages.TryAppend(packet.RequestId, connectionId!.Value, packet.Sequence,
                            packet.CheckedItems, packet.TotalItems, packet.Locations, out var pageError))
                        throw new InvalidDataException(pageError);
                    lastSearchProgress = Environment.TickCount64;
                    remoteSearchSource = new(acceptedPeerId!.Value, RemoteRoster?.Name ?? "Paired PC", packet.Complete,
                        packet.Error, packet.Warnings, packet.RetrievedAt, remoteSearchPages.Locations.ToArray(), packet.CheckedItems, packet.TotalItems);
                    if (packet.Complete) waitingForSearchPeer = false;
                    PublishItemSearch();
                    break;
            }
        }
        catch (Exception ex)
        {
            // A missing IPC, malformed search or full send queue must not affect a transfer journal.
            if (message.Kind == "item-search-result")
            {
                waitingForSearchPeer = false;
                if (remoteSearchSource != null) remoteSearchSource = remoteSearchSource with { Complete = true, Error = ex.Message };
                PublishItemSearch();
            }
            else { peerSearch = null; Plugin.Log.Debug(ex, "Paired item search failed"); }
        }
        return true;
    }

    // Run before draining messages so a first-frame request is not discarded by a later reset.
    private void SyncItemSearchConnection()
    {
        try
        {
            var current = Connected && HasAcceptedConnection ? connectionId : null;
            if (current == observedSearchConnection) return;
            observedSearchConnection = current;
            peerSearch = null;
            remoteSearchSource = null;
            remoteSearchPages = null;
            waitingForSearchPeer = false;
            searchConnection = null;
            if (!string.IsNullOrWhiteSpace(itemSearchQuery)) SearchItems(itemSearchQuery);
            else PublishItemSearch();
        }
        catch (Exception ex) { SearchFailed(ex); }
    }

    private void UpdateItemSearch()
    {
        try
        {
            var current = Connected && HasAcceptedConnection ? connectionId : null;
            var now = Environment.TickCount64;
            // Limit IPC work per frame. Reads are always on Dalamud's framework thread.
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 4 && timer.ElapsedMilliseconds < 2; i++)
            {
                if (localSearch is { Complete: false }) localSearch.Step();
                if (timer.ElapsedMilliseconds < 2 && peerSearch is { Complete: false }) peerSearch.Step();
                if (localSearch is not { Complete: false } && peerSearch is not { Complete: false }) break;
            }
            if (localSearch != null && (localSearch.Complete || now >= nextLocalSearchProgress))
            {
                nextLocalSearchProgress = now + 250;
                localSearchSource = Source(localSearch, Settings.PeerId, Settings.PeerName);
                if (localSearch.Complete) localSearch = null;
                PublishItemSearch();
            }
            if (peerSearch != null && current.HasValue && now >= nextPeerSearchProgress)
            {
                var reader = peerSearch;
                var page = reader.Results.Skip(peerSearchOffset).Take(Math.Min(SearchPageSize, MaxSearchLocations - peerSearchOffset)).ToList();
                var end = peerSearchOffset + page.Count;
                var capped = end >= MaxSearchLocations;
                var done = capped || reader.Complete && end == reader.Results.Count;
                var warnings = reader.Warnings.Take(62).Select(x => x.Length > 300 ? x[..300] : x).ToList();
                if (reader.Warnings.Count > 62) warnings.Add($"{reader.Warnings.Count - 62} additional cache warnings.");
                if (capped) warnings.Add("Result limit reached. Narrow the search to see remaining locations.");
                Send("item-search-result", new ItemSearchPacket(peerSearchId, peerSearchSequence, done,
                    reader.Error, warnings.Take(64).ToList(), reader.RetrievedAt, reader.CheckedItems, reader.TotalItems, page));
                peerSearchSequence++;
                peerSearchOffset = end;
                nextPeerSearchProgress = now + (page.Count > 0 ? 50 : 500);
                if (done) peerSearch = null;
            }
            if (waitingForSearchPeer && now - lastSearchProgress > 30_000)
            {
                waitingForSearchPeer = false;
                if (remoteSearchSource != null) remoteSearchSource = remoteSearchSource with { Complete = true,
                    Error = "The paired PC stopped responding to this search. Refresh to try again; local results remain available." };
                PublishItemSearch();
            }
        }
        catch (Exception ex) { peerSearch = null; SearchFailed(ex); }
    }
}
