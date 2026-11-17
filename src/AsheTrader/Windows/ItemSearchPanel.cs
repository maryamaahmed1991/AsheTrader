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
    private string query = plugin.Config.ItemSearch.LastQuery;
    private bool restoreSearch = true;
    private Guid savedId;
    private Guid editingId;
    private string savedName = "";
    private string savedQuery = "";
    private string saveError = "";
    private SavedItemSearch? deletedSearch;
    private long searchAfter;
    private bool searchPending;
    private uint selectedItem;
    private int resultPage;
    private bool sortByQuantity = plugin.Config.ItemSearch.LargestFirst;
    private string notice = "";
    private string displayedQuery = "";
    private IReadOnlyList<ItemSearchRow>? displayedRows;
    private IReadOnlyList<ItemSearchRow> sortedRows = [];

    internal void Draw()
    {
        if (restoreSearch)
        {
            restoreSearch = false;
            savedId = plugin.Config.ItemSearch.Saved.FirstOrDefault(x => x.Query == query)?.Id ?? Guid.Empty;
            if (query.Length > 0) SubmitSearch();
        }
        var view = plugin.Workflow.ItemSearch;
        if (ImGui.BeginChild("ItemSearchPage", new Vector2(0, 0)))
        {
            DrawSearch(view);
            if (notice.Length > 0)
            {
                UiTone.Wrapped(UiTone.Good, notice);
                if (deletedSearch is { } deleted)
                {
                    if (ImGui.SmallButton("Undo deletion"))
                    {
                        plugin.Config.ItemSearch.Saved.Add(deleted);
                        plugin.Save(); deletedSearch = null; notice = "Saved search restored.";
                    }
                    ImGui.SameLine();
                }
                if (ImGui.SmallButton("Dismiss##searchNotice")) { notice = ""; deletedSearch = null; }
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
                var selected = view.Rows.FirstOrDefault(row => row.ItemId == selectedItem);
                var split = selected != null && ImGui.GetContentRegionAvail().X >= TraderStyle.Px(950);
                if (split && ImGui.BeginTable("SearchSplit", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Results", ImGuiTableColumnFlags.WidthStretch, 1.3f);
                    ImGui.TableSetupColumn("Locations", ImGuiTableColumnFlags.WidthStretch, 1);
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    DrawResults(view, true);
                    ImGui.TableNextColumn();
                    if (view.Rows.FirstOrDefault(row => row.ItemId == selectedItem) is { } current) DrawLocations(current, view.Paired, true);
                    ImGui.EndTable();
                }
                else
                {
                    DrawResults(view, false);
                    if (view.Rows.FirstOrDefault(row => row.ItemId == selectedItem) is { } current) DrawLocations(current, view.Paired, false);
                }
            }
        }
        ImGui.EndChild();

        if (searchPending && Environment.TickCount64 >= searchAfter) SubmitSearch();
    }

    private void DrawSearch(ItemSearchView view)
    {
        if (TraderStyle.BeginCard("ItemSearchControls"))
        {
            DrawSavedSearches();
            ImGui.TextColored(TraderStyle.Text, "Item names");
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
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(query.Trim().Trim(',')));
            if (TraderStyle.PrimaryButton(view.Query == query && view.Query.Length > 0 ? "Refresh results" : "Search items", new(145, 32)))
                SubmitSearch();
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(query.Trim().Trim(',')));
            if (TraderStyle.SecondaryButton("Save list", new(100, 32))) OpenSave(false);
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
            if (ImGui.GetContentRegionAvail().X >= TraderStyle.Px(690)) ImGui.SameLine();
            TraderStyle.Badge(view.Paired ? plugin.Workflow.Connected ? "BOTH PC CACHES" : "INCLUDES OFFLINE PC" : "THIS PC'S CACHE", view.Paired ? TraderStyle.Accent : UiTone.Info);
            if (ImGui.TreeNode("Sources & cache details"))
            {
                DrawSourceStatus(view.Local, "This PC", view.Busy);
                if (view.Paired) DrawSourceStatus(view.Remote, "Paired PC", view.Busy);
                ImGui.TreePop();
            }
            DrawSavePopup();
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
        ImGui.TextDisabled("Search for an item above to see cached stock.");
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

    private void DrawResults(ItemSearchView view, bool split)
    {
        TraderStyle.Section($"{view.Rows.Count:N0} matching item{(view.Rows.Count == 1 ? "" : "s")}");
        if (view.Busy) TraderStyle.Badge("UPDATING COUNTS", TraderStyle.Gold);
        if (query != view.Query) UiTone.Wrapped(TraderStyle.Gold, $"Previous results: {view.Query}");
        var partial = SourceIncomplete(view.Local) || view.Paired && SourceIncomplete(view.Remote);
        if (partial) UiTone.Wrapped(UiTone.Warning, "Partial counts: + means more may exist; - means no count is available yet.");
        if (ImGui.Checkbox("Largest first", ref sortByQuantity))
        {
            SortRows(); resultPage = 0;
            plugin.Config.ItemSearch.LargestFirst = sortByQuantity; plugin.Save();
        }
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
        var height = Math.Max(TraderStyle.Px(140), ImGui.GetContentRegionAvail().Y - TraderStyle.Px(!split && selectedItem != 0 ? 300 : 45));
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
                ImGui.TableNextColumn(); Count(row.Total.ToString("N0") + (partial ? "+" : ""), TraderStyle.Accent);
                if (view.Paired)
                {
                    ImGui.TableNextColumn(); Count(QuantityLabel(row.LocalQuantity, view.Local), TraderStyle.Text);
                    ImGui.TableNextColumn(); Count(QuantityLabel(row.RemoteQuantity, view.Remote), TraderStyle.Text);
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

    private void DrawLocations(ItemSearchRow row, bool paired, bool split)
    {
        var height = split ? Math.Max(250, ImGui.GetContentRegionAvail().Y / TraderStyle.Scale - 10) : 260;
        if (TraderStyle.BeginCard("SelectedItemLocations", height))
        {
            if (ImGui.SmallButton("Close locations")) selectedItem = 0;
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
            if (ImGui.BeginTable("StorageLocations", 2, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH,
                    new Vector2(0, Math.Max(TraderStyle.Px(70), ImGui.GetContentRegionAvail().Y))))
            {
                ImGui.TableSetupColumn("Character / storage", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(85));
                ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
                foreach (var location in locations)
                {
                    var key = location.Key;
                    var character = string.IsNullOrWhiteSpace(key.CharacterName) ? key.OwnerName : key.CharacterName;
                    if (!string.IsNullOrWhiteSpace(key.World)) character += " @ " + key.World;
                    var storage = key.Storage;
                    if (!string.IsNullOrWhiteSpace(key.OwnerName) && key.OwnerName != key.CharacterName)
                        storage = $"{key.OwnerName} · {storage}";
                    var quality = key.ItemFlags == 0 ? "NQ" : key.ItemFlags == 1 ? "HQ" : "Special";
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    UiTone.Wrapped(TraderStyle.Text, character);
                    UiTone.Wrapped(TraderStyle.Muted, paired ? $"{storage} · {key.PeerName}" : storage);
                    ImGui.TableNextColumn();
                    Count($"{location.Quantity:N0}", TraderStyle.Accent);
                    ImGui.TextDisabled(quality);
                }
                ImGui.EndTable();
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
        deletedSearch = null;
        var submitted = query;
        if (plugin.Config.ItemSearch.LastQuery != submitted)
        {
            plugin.Config.ItemSearch.LastQuery = submitted;
            plugin.Save();
        }
        plugin.QueueAction(() => plugin.Workflow.SearchItems(submitted));
    }

    private static void Count(string text, Vector4 color)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X));
        ImGui.TextColored(color, text);
    }

    private void DrawSavedSearches()
    {
        var selected = plugin.Config.ItemSearch.Saved.FirstOrDefault(x => x.Id == savedId);
        var label = selected == null ? "Saved searches..." : selected.Name + (selected.Query != query ? " (edited)" : "");
        ImGui.SetNextItemWidth(Math.Max(TraderStyle.Px(150), ImGui.GetContentRegionAvail().X - TraderStyle.Px(100)));
        if (ImGui.BeginCombo("##savedSearches", label))
        {
            if (plugin.Config.ItemSearch.Saved.Count == 0) ImGui.TextDisabled("Use Save list to keep a search here.");
            foreach (var search in plugin.Config.ItemSearch.Saved.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.PushID(search.Id.ToString());
                if (ImGui.Selectable(search.Name, search.Id == savedId))
                {
                    savedId = search.Id; query = search.Query; SubmitSearch();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(search.Query);
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine(); ImGui.BeginDisabled(selected == null);
        if (TraderStyle.SecondaryButton("Edit list", new(90, 32))) OpenSave(true);
        ImGui.EndDisabled();
    }

    private void OpenSave(bool edit)
    {
        var selected = plugin.Config.ItemSearch.Saved.FirstOrDefault(x => x.Id == savedId);
        editingId = edit && selected != null ? selected.Id : Guid.Empty;
        savedName = edit && selected != null ? selected.Name : "";
        savedQuery = query;
        saveError = "";
        ImGui.OpenPopup("Save search list");
    }

    private void DrawSavePopup()
    {
        ImGui.SetNextWindowSize(new Vector2(TraderStyle.Px(420), 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("Save search list", ImGuiWindowFlags.AlwaysAutoResize)) return;
        var existing = plugin.Config.ItemSearch.Saved.FirstOrDefault(x => x.Id == editingId);
        TraderStyle.Section(existing == null ? "Save a search list" : "Edit saved search");
        ImGui.TextUnformatted("List name"); ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##savedName", "Workshop supplies", ref savedName, 80);
        ImGui.TextUnformatted("Items to search"); ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##savedQuery", "coke, ceruleum, hardened sap", ref savedQuery, ItemSearchQuery.MaximumQueryLength);
        ImGui.TextWrapped("Comma-separated names; partial names work too.");
        if (saveError.Length > 0) UiTone.Wrapped(UiTone.Warning, saveError);
        if (TraderStyle.PrimaryButton(existing == null ? "Save list" : "Update list"))
        {
            var name = savedName.Trim();
            try
            {
                if (name.Length == 0) throw new ArgumentException("Give this list a name.");
                if (ItemSearchQuery.Parse(savedQuery).Count == 0) throw new ArgumentException("Add at least one item name.");
                if (plugin.Config.ItemSearch.Saved.Any(x => x.Id != editingId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("That list name is already in use. Choose another name.");
                var saved = existing ?? new SavedItemSearch();
                saved.Name = name; saved.Query = savedQuery;
                if (existing == null) plugin.Config.ItemSearch.Saved.Add(saved);
                savedId = saved.Id; query = savedQuery; plugin.Save(); SubmitSearch(); notice = $"Saved {name}.";
                ImGui.CloseCurrentPopup();
            }
            catch (ArgumentException ex) { saveError = ex.Message; }
        }
        ImGui.SameLine();
        if (TraderStyle.SecondaryButton("Cancel")) ImGui.CloseCurrentPopup();
        if (existing != null)
        {
            ImGui.Separator();
            if (TraderStyle.DangerButton("Delete saved list"))
            {
                plugin.Config.ItemSearch.Saved.Remove(existing); deletedSearch = existing; savedId = Guid.Empty;
                plugin.Save(); notice = $"Deleted {existing.Name}."; ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndPopup();
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
