using System.Globalization;
using System.Numerics;
using System.Text;
using AsheTrader.Core.Search;
using AsheTrader.Workflow;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

/// <summary>Read-only inventory lookup; remains available while transfers are running.</summary>
internal sealed class ItemSearchPanel(Plugin plugin)
{
    private const int PageSize = 100;
    private string query = "";
    private long searchAfter;
    private bool searchPending;
    private uint selectedItem;
    private int resultPage;
    private bool sortByQuantity;
    private string notice = "";
    private string displayedQuery = "";
    private IReadOnlyList<ItemSearchRow>? displayedRows;
    private IReadOnlyList<ItemSearchRow> sortedRows = [];

    internal void Draw()
    {
        var view = plugin.Workflow.ItemSearch;
        if (ImGui.BeginChild("ItemSearchPage", new Vector2(0, 0)))
        {
            DrawSearch(view);
            if (notice.Length > 0)
            {
                UiTone.Wrapped(UiTone.Good, notice);
                if (ImGui.SmallButton("Dismiss##searchNotice")) notice = "";
            }
            if (!string.IsNullOrWhiteSpace(view.Error)) UiTone.Wrapped(UiTone.Warning, view.Error);

            if (!ReferenceEquals(displayedRows, view.Rows))
            {
                displayedRows = view.Rows;
                SortRows();
                if (displayedQuery != view.Query) resultPage = 0;
                displayedQuery = view.Query;
                if (!view.Busy && sortedRows.All(row => row.ItemId != selectedItem)) selectedItem = 0;
            }
            if (view.Query.Length == 0)
                DrawIntroduction();
            else if (view.Rows.Count == 0)
                DrawEmpty(view);
            else
            {
                DrawResults(view);
                if (view.Rows.FirstOrDefault(row => row.ItemId == selectedItem) is { } selected)
                    DrawLocations(selected, view.Paired);
            }
        }
        ImGui.EndChild();

        if (searchPending && Environment.TickCount64 >= searchAfter) SubmitSearch();
    }

    private void DrawSearch(ItemSearchView view)
    {
        if (TraderStyle.BeginCard("ItemSearchControls"))
        {
            ImGui.TextColored(TraderStyle.Gold, "FIND ITEMS");
            ImGui.SetNextItemWidth(-1);
            var previous = query;
            var entered = ImGui.InputTextWithHint("##itemSearchQuery", "coke, ceruleum, hardened sap", ref query, ItemSearchQuery.MaximumQueryLength,
                ImGuiInputTextFlags.EnterReturnsTrue);
            if (previous != query)
            {
                searchPending = true;
                searchAfter = Environment.TickCount64 + 600;
                notice = "";
            }
            if (entered) SubmitSearch();
            ImGui.TextDisabled("Separate names with commas. Partial names work too.");

            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(query.Trim().Trim(',')));
            if (TraderStyle.PrimaryButton(view.Query == query && view.Query.Length > 0 ? "Refresh results" : "Search items", new(145, 32)))
                SubmitSearch();
            ImGui.EndDisabled();
            if (view.Busy)
            {
                ImGui.SameLine();
                if (TraderStyle.SecondaryButton("Cancel", new(80, 32)))
                {
                    searchPending = false;
                    plugin.QueueAction(plugin.Workflow.CancelItemSearch);
                }
            }
            else if (query.Length > 0)
            {
                ImGui.SameLine();
                if (TraderStyle.SecondaryButton("Clear", new(80, 32))) { query = ""; SubmitSearch(); }
            }
            ImGui.SameLine();
            TraderStyle.Badge(view.Paired ? "BOTH PC CACHES" : "THIS PC'S CACHE", view.Paired ? TraderStyle.Accent : UiTone.Info);
            ImGui.Spacing();
            DrawSourceStatus(view.Local, "This PC", view.Busy);
            if (view.Paired) DrawSourceStatus(view.Remote, "Paired PC", view.Busy);
        }
        TraderStyle.EndCard();
    }

    private static void DrawSourceStatus(ItemSearchSource? source, string label, bool busy)
    {
        if (source == null)
        {
            UiTone.Wrapped(TraderStyle.Muted, $"{label}: {(busy ? "waiting for cached results..." : "not searched yet")}");
            return;
        }
        var name = string.IsNullOrWhiteSpace(source.Name) ? label : $"{label} / {source.Name}";
        var time = source.RetrievedAt.HasValue ? $" · read {source.RetrievedAt.Value.ToLocalTime():HH:mm:ss}" : "";
        var state = !string.IsNullOrWhiteSpace(source.Error)
            ? source.Locations.Count == 0 ? "unavailable" : "partial results"
            : !source.Complete && busy ? $"searching {source.CheckedItems:N0}/{source.TotalItems:N0}"
            : SourceIncomplete(source) ? "partial results" : "ready";
        UiTone.Wrapped(SourceIncomplete(source) ? UiTone.Warning : TraderStyle.Muted, $"{name}: {state}{time}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Counts come from Allagan Tools. Read time is when Ashe's Trader queried its cache, not when the game inventory was last observed.");
        if (!string.IsNullOrWhiteSpace(source.Error)) UiTone.Wrapped(UiTone.Warning, source.Error);
        if (source.Warnings.Count > 0)
        {
            ImGui.PushID(label);
            if (ImGui.TreeNode($"{source.Warnings.Count:N0} cache notice{(source.Warnings.Count == 1 ? "" : "s")}"))
            {
                foreach (var warning in source.Warnings) UiTone.Wrapped(UiTone.Warning, warning);
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
    }

    private static void DrawIntroduction()
    {
        if (TraderStyle.BeginCard("SearchIntroduction"))
        {
            TraderStyle.Section("One search, all your characters", "Find what you already own without logging into each character.");
            UiTone.Wrapped(TraderStyle.Accent, "Try: coke, ceruleum, sap");
            ImGui.TextWrapped("Each matching item appears once, with a combined count. Select an item to see which characters and retainers hold it.");
            ImGui.Spacing();
            ImGui.TextDisabled("Includes personal storage cached by Allagan Tools.");
            ImGui.TextWrapped("Pair your other PC to include its inventories automatically. Free company and housing storage are excluded.");
        }
        TraderStyle.EndCard();
    }

    private static void DrawEmpty(ItemSearchView view)
    {
        if (TraderStyle.BeginCard("NoSearchResults"))
        {
            TraderStyle.Section(view.Busy ? "Looking through your items..." : "No matching cached items", view.Query);
            ImGui.TextWrapped(view.Busy
                ? "Results will appear as each PC answers. You can keep using the plugin while this runs."
                : "Try a shorter name or check the cache notices above. Only items present in the available Allagan Tools cache appear here.");
        }
        TraderStyle.EndCard();
    }

    private void DrawResults(ItemSearchView view)
    {
        TraderStyle.Section($"{view.Rows.Count:N0} matching item{(view.Rows.Count == 1 ? "" : "s")}");
        if (query != view.Query) UiTone.Wrapped(TraderStyle.Muted, $"Showing results for: {view.Query}");
        var partial = SourceIncomplete(view.Local) || view.Paired && SourceIncomplete(view.Remote);
        if (partial) UiTone.Wrapped(UiTone.Warning, "Partial counts: + means more may exist; - means no count is available yet.");
        if (ImGui.Checkbox("Largest quantities first", ref sortByQuantity)) { SortRows(); resultPage = 0; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy results"))
        {
            CopyResults(view);
            notice = "Item names and cached quantities copied.";
        }
        var pageCount = Math.Max(1, (sortedRows.Count + PageSize - 1) / PageSize);
        resultPage = Math.Clamp(resultPage, 0, pageCount - 1);
        var start = resultPage * PageSize;
        var end = Math.Min(sortedRows.Count, start + PageSize);
        var height = Math.Clamp(ImGui.GetContentRegionAvail().Y - TraderStyle.Px(selectedItem == 0 ? 50 : 190), TraderStyle.Px(180), TraderStyle.Px(390));
        height = Math.Min(height, (end - start + 1) * (ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2) + TraderStyle.Px(4));
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("ItemSearchMatches", view.Paired ? 4 : 2, flags, new Vector2(0, height)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(95));
            if (view.Paired)
            {
                ImGui.TableSetupColumn("This PC", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(85));
                ImGui.TableSetupColumn("Paired PC", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(85));
            }
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            for (var index = start; index < end; index++)
            {
                var row = sortedRows[index];
                ImGui.PushID((int)row.ItemId);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Selectable($"{row.Name}##item", selectedItem == row.ItemId, ImGuiSelectableFlags.SpanAllColumns))
                    selectedItem = selectedItem == row.ItemId ? 0 : row.ItemId;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{row.Name}\n{QualitySummary(row)}\nSelect to see characters and storage.");
                ImGui.TableNextColumn(); ImGui.TextColored(TraderStyle.Accent, row.Total.ToString("N0") + (partial ? "+" : ""));
                if (view.Paired)
                {
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(QuantityLabel(row.LocalQuantity, view.Local));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(QuantityLabel(row.RemoteQuantity, view.Remote));
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (pageCount > 1)
        {
            ImGui.BeginDisabled(resultPage == 0);
            if (ImGui.SmallButton("Previous")) resultPage--;
            ImGui.EndDisabled(); ImGui.SameLine();
            ImGui.TextUnformatted($"{start + 1:N0}-{end:N0} of {sortedRows.Count:N0}"); ImGui.SameLine();
            ImGui.BeginDisabled(resultPage + 1 >= pageCount);
            if (ImGui.SmallButton("Next")) resultPage++;
            ImGui.EndDisabled();
        }
        else ImGui.TextDisabled("Select an item to see where it is stored. Counts combine NQ and HQ.");
    }

    private static void DrawLocations(ItemSearchRow row, bool paired)
    {
        if (TraderStyle.BeginCard("SelectedItemLocations"))
        {
            TraderStyle.Section(row.Name, QualitySummary(row));
            // AT's per-container rows become a single readable storage line per owner/quality/PC.
            var locations = row.Holdings.GroupBy(holding => new
                {
                    holding.PeerId, holding.PeerName, holding.Location.OwnerId, holding.Location.OwnerName,
                    holding.Location.CharacterName, holding.Location.World, holding.Location.Storage, holding.Location.ItemFlags,
                })
                .Select(group => new { group.Key, Quantity = group.Aggregate(0UL, (sum, holding) => sum + holding.Location.Quantity) })
                .OrderBy(location => location.Key.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(location => location.Key.OwnerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(location => location.Key.Storage, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var location in locations)
            {
                var key = location.Key;
                var character = string.IsNullOrWhiteSpace(key.CharacterName) ? key.OwnerName : key.CharacterName;
                if (!string.IsNullOrWhiteSpace(key.World)) character += " @ " + key.World;
                var storage = key.Storage;
                if (!string.IsNullOrWhiteSpace(key.OwnerName) && key.OwnerName != key.CharacterName)
                    storage = $"{key.OwnerName} · {storage}";
                var quality = key.ItemFlags == 0 ? "NQ" : key.ItemFlags == 1 ? "HQ" : "Special";
                if (ImGui.BeginTable($"Location{key.PeerId}/{key.OwnerId}/{key.Storage}/{key.ItemFlags}", 2))
                {
                    ImGui.TableSetupColumn("Owner", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(120));
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    UiTone.Wrapped(TraderStyle.Text, character);
                    UiTone.Wrapped(TraderStyle.Muted, paired ? $"{storage} · {key.PeerName}" : storage);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(TraderStyle.Accent, $"{location.Quantity:N0}");
                    ImGui.TextDisabled(quality);
                    ImGui.EndTable();
                }
            }
        }
        TraderStyle.EndCard();
    }

    private void SortRows() => sortedRows = sortByQuantity
        ? (displayedRows ?? []).OrderByDescending(row => row.Total).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray()
        : (displayedRows ?? []).OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    private void SubmitSearch()
    {
        searchPending = false;
        resultPage = 0;
        notice = "";
        var submitted = query;
        plugin.QueueAction(() => plugin.Workflow.SearchItems(submitted));
    }

    private static string QualitySummary(ItemSearchRow row) => $"NQ {row.NormalQuality:N0} · HQ {row.HighQuality:N0}"
        + (row.OtherQuality > 0 ? $" · Special {row.OtherQuality:N0}" : "");

    private static bool SourceIncomplete(ItemSearchSource? source) => source == null || !source.Complete
        || !string.IsNullOrWhiteSpace(source.Error) || source.Warnings.Count > 0;

    private static string QuantityLabel(ulong quantity, ItemSearchSource? source) => SourceIncomplete(source)
        ? quantity == 0 ? "-" : $"{quantity:N0}+"
        : quantity.ToString("N0");

    private void CopyResults(ItemSearchView view)
    {
        var text = new StringBuilder("Item\tTotal\tNQ\tHQ\tSpecial\tThis PC\tPaired PC\n");
        foreach (var row in sortedRows)
        {
            text.Append(row.Name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));
            foreach (var quantity in new[] { row.Total, row.NormalQuality, row.HighQuality, row.OtherQuality, row.LocalQuantity, row.RemoteQuantity })
                text.Append('\t').Append(quantity.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
        }
        ImGui.SetClipboardText(text.ToString());
    }
}
