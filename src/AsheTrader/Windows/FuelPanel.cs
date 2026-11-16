using System.Numerics;
using AsheTrader.Core;
using AsheTrader.Integrations;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AsheTrader.Windows;

/// <summary>Local fuel queues are configured around worlds and shared FC credit pools.</summary>
internal sealed class FuelPanel(Plugin plugin)
{
    private string filter = "";
    private bool selectedOnly;
    private int page;
    private string selectedCompany = "";
    private bool wasRunning;
    private bool requestedRefresh;
    private long refreshAfter;
    private ulong selectedCharacter;
    private string houseName = "";
    private string houseWorldSearch = "";
    private int houseWorld;
    private int houseCity = 8;
    private int houseWard = 1;
    private int housePlot = 1;
    private bool addingHouse;
    private bool showLastRun;
    private string? diagnosticsToCopy;
    private int diagnosticCopyPending;
    private bool diagnosticsCopied;
    private List<(uint Id, string Name)>? worlds;

    private bool TransferLocked => plugin.Workflow.Busy || plugin.Workflow.Run is { Step: not Workflow.CoordinatorStep.Finished };
    private bool SettingsLocked => TransferLocked || plugin.FuelQueue.HasUnfinishedRun || plugin.Trader.IsRunning || plugin.Fuel.Active;

    internal void Draw()
    {
        if (Interlocked.Exchange(ref diagnosticsToCopy, null) is { } report)
        { ImGui.SetClipboardText(report); diagnosticsCopied = true; }
        if (!SettingsLocked && (!requestedRefresh || Environment.TickCount64 >= refreshAfter)) Refresh();
        if (plugin.FuelQueue.Active && !wasRunning) page = 2;
        wasRunning = plugin.FuelQueue.Active;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(TraderStyle.Px(10), TraderStyle.Px(6)));
        DrawRunControls();
        if (ImGui.BeginTable("FuelViews", 3, ImGuiTableFlags.SizingStretchSame))
        {
            string[] titles = ["Buyers", "Destinations", "Run overview"];
            string[] subtitles = ["Worlds & companies", "Your workshop library", "Queue & results"];
            for (var i = 0; i < titles.Length; i++)
            {
                ImGui.TableNextColumn();
                if (TraderStyle.NavButton($"FuelView{i}", titles[i], subtitles[i], page == i)) page = i;
            }
            ImGui.EndTable();
        }
        ImGui.Spacing();
        if (ImGui.BeginChild("FuelViewContent", new Vector2(0, 0)))
        {
            if (page == 2) DrawQueueProgress();
            else
            {
                if (SettingsLocked)
                    UiTone.Wrapped(UiTone.Warning, "Viewing setup only. Finish the saved queue to edit buyers or destinations.");
                if (page == 1)
                { ImGui.BeginDisabled(SettingsLocked); DrawHouses(); ImGui.EndDisabled(); }
                else DrawCharacters();
            }
            if (ImGui.CollapsingHeader("Setup details & refresh"))
            {
                UiTone.Wrapped(TraderStyle.Muted, plugin.FuelQueue.SetupStatus);
                ImGui.BeginDisabled(SettingsLocked);
                if (TraderStyle.SecondaryButton("Refresh setup")) Refresh();
                ImGui.EndDisabled();
                UiTone.Wrapped(TraderStyle.Muted, "Imports this PC's AutoRetainer roster and Lifestream houses. Access is learned on normal logins; refreshing never switches characters.");
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void Refresh()
    {
        requestedRefresh = true;
        refreshAfter = Environment.TickCount64 + 30_000;
        plugin.QueueAction(plugin.FuelQueue.RefreshSetup);
    }

    private void DrawRunControls()
    {
        var queue = plugin.FuelQueue;
        var run = queue.Run;
        var preview = queue.Preview();
        var selected = preview.Count;
        var configured = preview.Count(entry => entry.Status != FuelQueueEntryStatus.Skipped);
        var compact = ImGui.GetContentRegionAvail().Y < TraderStyle.Px(650);
        if (TraderStyle.BeginCard("FuelRunControls"))
        {
            TraderStyle.Badge(queue.Active ? "FUEL RUN ACTIVE" : queue.HasUnfinishedRun ? "RUN PAUSED" : "FUEL OPERATIONS",
                queue.Active ? TraderStyle.Accent : queue.HasUnfinishedRun ? TraderStyle.Gold : TraderStyle.Accent);
            ImGui.SameLine(); ImGui.TextDisabled("Independent queue · this PC");
            if (ImGui.BeginTable("FuelMetrics", 3, ImGuiTableFlags.SizingStretchSame))
            {
                var complete = run?.Entries.Count(e => e.Status is FuelQueueEntryStatus.Completed or FuelQueueEntryStatus.Skipped or FuelQueueEntryStatus.StorageFull) ?? 0;
                Metric(queue.HasUnfinishedRun ? "Processed" : "Selected", queue.HasUnfinishedRun ? $"{complete} / {run!.Entries.Count}" : $"{selected}", TraderStyle.Text, compact);
                Metric(run == null ? "Companies" : "Purchased", run == null ? preview.Select(e => e.CompanyKey).Distinct().Count().ToString() : $"{run.TotalPurchased:N0}", TraderStyle.Accent, compact);
                Metric(run == null ? "Workshops" : "Stored", run == null ? plugin.Config.Houses.Count.ToString() : $"{run.TotalDeposited:N0}", TraderStyle.Gold, compact);
                ImGui.EndTable();
            }
            if (run != null && run.Entries.Count > 0)
            {
                var complete = run.Entries.Count(e => e.Status is FuelQueueEntryStatus.Completed or FuelQueueEntryStatus.Skipped or FuelQueueEntryStatus.StorageFull);
                UiTone.Progress((float)complete / run.Entries.Count, new(-1, TraderStyle.Px(4)), run.Paused);
            }
            if (queue.HasUnfinishedRun || run is { Finished: true }) UiTone.Status(queue.Status);
            else UiTone.Wrapped(TraderStyle.Muted, selected == 0 ? "Choose buyers, set their destination, then start your fuel run."
                : $"{configured} of {selected} buyers configured · routes are verified before travel.");

            if (queue.HasUnfinishedRun)
            {
                ImGui.BeginDisabled(TransferLocked || plugin.Trader.IsRunning);
                if (TraderStyle.PrimaryButton(queue.Active ? "Pause run" : "Resume run", new(140, 32)))
                    plugin.QueueAction(() => { if (queue.Active) queue.Pause(); else queue.Resume(); });
                ImGui.EndDisabled(); ImGui.SameLine();
                if (TraderStyle.DangerButton("End queue", new(110, 32))) plugin.QueueAction(queue.End);
            }
            else
            {
                ImGui.BeginDisabled(TransferLocked || plugin.Trader.IsRunning || plugin.Fuel.Active || configured == 0);
                if (TraderStyle.PrimaryButton("Start fuel run", new(150, 32))) plugin.QueueAction(queue.Start);
                ImGui.EndDisabled();
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(Volatile.Read(ref diagnosticCopyPending) != 0);
            if (TraderStyle.SecondaryButton(diagnosticsCopied ? "Diagnostics copied" : "Copy diagnostics", new(165, 32)))
            {
                diagnosticsCopied = false; Interlocked.Exchange(ref diagnosticCopyPending, 1);
                plugin.QueueAction(() =>
                {
                    try { Volatile.Write(ref diagnosticsToCopy, plugin.Workflow.CaptureDiagnosticReport()); }
                    catch (Exception ex) { Volatile.Write(ref diagnosticsToCopy, $"Ashe's Trader diagnostic capture failed: {ex.Message}"); }
                    finally { Interlocked.Exchange(ref diagnosticCopyPending, 0); }
                });
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy this PC's queue state for troubleshooting. Includes character and retainer identifiers; excludes pairing keys and chat.");
            if (TransferLocked) UiTone.Wrapped(UiTone.Warning, "Finish the transfer job before starting fuel purchases.");
        }
        TraderStyle.EndCard();
    }

    private static void Metric(string label, string value, Vector4 tone, bool compact)
    {
        ImGui.TableNextColumn();
        if (compact) { ImGui.TextColored(tone, value); ImGui.SameLine(); ImGui.TextDisabled(label.ToLowerInvariant()); }
        else
        {
            ImGui.TextColored(TraderStyle.Muted, label.ToUpperInvariant());
            var size = ImGui.GetFontSize() * 1.65f;
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), size, ImGui.GetCursorScreenPos(), ImGui.GetColorU32(tone), value);
            ImGui.Dummy(new Vector2(0, size));
        }
    }

    private void DrawCharacters()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##FuelRosterSearch", "Find a world, company or buyer...", ref filter, 200);
        ImGui.Checkbox("Companies with selected buyers", ref selectedOnly);
        var groups = plugin.Config.Characters.GroupBy(FuelQueuePlanner.CompanyKey)
            .Where(g => (!selectedOnly || g.Any(c => c.Fuel.Enabled)) && (string.IsNullOrWhiteSpace(filter)
                || g.Any(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || c.World.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || CompanyName(c).Contains(filter, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(g => g.First().World, StringComparer.OrdinalIgnoreCase).ThenBy(g => CompanyName(g.First()), StringComparer.OrdinalIgnoreCase).ToArray();
        if (groups.Length == 0)
        {
            TraderStyle.Section("No buyers to show", plugin.Config.Characters.Count == 0
                ? "Refresh setup below to import this PC's AutoRetainer characters." : "Try a different search or turn off the selected-company filter.");
            return;
        }
        if (!groups.Any(g => g.Key == selectedCompany)) selectedCompany = groups[0].Key;
        var wide = ImGui.GetContentRegionAvail().X >= TraderStyle.Px(800);
        if (wide && ImGui.BeginTable("FuelCompanyWorkspace", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Companies", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(245));
            ImGui.TableSetupColumn("Buyer setup", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextColumn(); DrawCompanyPicker(groups);
            ImGui.TableNextColumn(); DrawSelectedCompany(groups);
            ImGui.EndTable();
        }
        else
        {
            ImGui.SetNextItemWidth(-1);
            var current = groups.First(g => g.Key == selectedCompany).First();
            if (ImGui.BeginCombo("##ActiveFuelCompany", $"{current.World} · {CompanyName(current)}"))
            {
                foreach (var world in groups.GroupBy(g => g.First().World))
                {
                    ImGui.TextColored(TraderStyle.Gold, world.Key);
                    foreach (var group in world)
                        if (ImGui.Selectable($"{CompanyName(group.First())} · {group.Count(c => c.Fuel.Enabled)}/{group.Count()} selected##{group.Key}", selectedCompany == group.Key))
                            selectedCompany = group.Key;
                }
                ImGui.EndCombo();
            }
            DrawSelectedCompany(groups);
        }
    }

    private void DrawCompanyPicker(IEnumerable<IGrouping<string, CharacterProfile>> groups)
    {
        foreach (var world in groups.GroupBy(g => g.First().World))
        {
            ImGui.TextColored(TraderStyle.Gold, world.Key.ToUpperInvariant());
            foreach (var group in world)
            {
                var title = CompanyName(group.First());
                // Keep the navigation label inside its narrow column; full names remain in the detail pane.
                while (title.Length > 3 && ImGui.CalcTextSize(title).X > ImGui.GetContentRegionAvail().X - TraderStyle.Px(30)) title = title[..^2];
                if (title != CompanyName(group.First())) title += "…";
                if (TraderStyle.NavButton($"FC{group.Key}", title, $"{group.Count(c => c.Fuel.Enabled)} / {group.Count()} buyers selected", selectedCompany == group.Key))
                    selectedCompany = group.Key;
            }
            ImGui.Spacing();
        }
    }

    private void DrawSelectedCompany(IEnumerable<IGrouping<string, CharacterProfile>> groups)
    {
        var group = groups.First(g => g.Key == selectedCompany);
        DrawCompany(group.Key, group.OrderBy(c => c.Fuel.QueueOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private void DrawCompany(string companyKey, IReadOnlyList<CharacterProfile> characters)
    {
        var first = characters[0];
        var rule = plugin.Config.FuelSettings.Companies.GetValueOrDefault(companyKey) ?? new FuelCompanyRule();
        ImGui.PushID(companyKey);
        if (TraderStyle.BeginCard("FuelCompany"))
        {
            TraderStyle.Section(CompanyName(first), $"{first.World} · {characters.Count(c => c.Fuel.Enabled)} of {characters.Count} buyers selected");
            var snapshot = characters.Where(c => c.FreeCompanyCredits.HasValue).OrderByDescending(c => c.FreeCompanyCreditsObservedAt).FirstOrDefault();
            if (snapshot != null)
            {
                TraderStyle.Badge($"{snapshot.FreeCompanyCredits:N0} credits · cached", TraderStyle.Gold);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Cached balance · {snapshot.FreeCompanyCreditsObservedAt?.ToLocalTime():g}. Live credits and spending permission are checked at the supplier.");
            }
            if (first.FreeCompanyId == 0) UiTone.Wrapped(UiTone.Warning, "No FC membership. These buyers will be skipped.");
            else if (!first.FreeCompanyId.HasValue) UiTone.Wrapped(UiTone.Warning, "FC membership will be checked on login.");
            ImGui.Spacing(); ImGui.TextColored(TraderStyle.Accent, "01  DESTINATION & BUDGET");
            ImGui.BeginDisabled(SettingsLocked);
            DrawCompanyDestination(companyKey, first.World, rule);
            var reserve = rule.CreditReserve;
            ImGui.SetNextItemWidth(TraderStyle.Px(150));
            if (EditUInt("FC credits to keep", ref reserve)) { rule.CreditReserve = reserve; SaveRule(companyKey, rule); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shared by every buyer in this FC. Set zero to spend all affordable credits.");
            ImGui.EndDisabled();
            ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
            ImGui.TextColored(TraderStyle.Accent, "02  BUYERS & PRIORITY");
            UiTone.Wrapped(TraderStyle.Muted, "Buyers run from top to bottom. Select a name to edit its purchase and storage settings.");
            ImGui.BeginDisabled(SettingsLocked);
            if (ImGui.SmallButton("Select all in FC")) { foreach (var c in characters) Enable(c, true); plugin.Save(); }
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear FC selection")) { foreach (var c in characters) c.Fuel.Enabled = false; plugin.Save(); }
            ImGui.EndDisabled();
            foreach (var character in characters) DrawCharacter(character, characters, rule);
            if (ImGui.TreeNode("Company display name"))
            {
                var label = rule.NameOverride; ImGui.SetNextItemWidth(-1);
                ImGui.BeginDisabled(SettingsLocked);
                if (ImGui.InputTextWithHint("##CompanyLabel", "Optional display name", ref label, 128)) { rule.NameOverride = label; SaveRule(companyKey, rule); }
                ImGui.EndDisabled();
                ImGui.TreePop();
            }
        }
        TraderStyle.EndCard(); ImGui.PopID();
    }

    private void DrawCharacter(CharacterProfile character, IReadOnlyList<CharacterProfile> siblings, FuelCompanyRule rule)
    {
        ImGui.PushID(character.ContentId.ToString());
        if (ImGui.BeginTable("FuelCharacterRow", 3, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(26));
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(115));
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            var enabled = character.Fuel.Enabled;
            ImGui.BeginDisabled(SettingsLocked);
            if (ImGui.Checkbox("##Enabled", ref enabled)) { Enable(character, enabled); plugin.Save(); }
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{character.Name}##Details", selectedCharacter == character.ContentId))
                selectedCharacter = selectedCharacter == character.ContentId ? 0 : character.ContentId;
            var assignedId = character.Fuel.HouseId ?? rule.HouseId;
            var assigned = plugin.Config.Houses.FirstOrDefault(house => house.Id == assignedId);
            var caretaker = !character.Fuel.HouseId.HasValue && rule.UseCaretaker;
            var valid = caretaker || assigned != null && IsSameWorld(assigned, character.World);
            UiTone.Wrapped(valid ? TraderStyle.Muted : UiTone.Warning,
                caretaker ? $"Resident Caretaker · {LifestreamClient.DistrictName(rule.CaretakerCity)}"
                    : assigned == null ? "Choose a fuel destination" : valid ? assigned.Name : "Assigned house is on another world");
            DrawAccess(character, detailed: false);
            ImGui.TableNextColumn();
            var index = siblings.ToList().FindIndex(profile => profile.ContentId == character.ContentId);
            ImGui.BeginDisabled(SettingsLocked || index <= 0);
            if (ImGui.SmallButton("Up")) MoveCharacter(siblings, index, -1);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(SettingsLocked || index < 0 || index + 1 >= siblings.Count);
            if (ImGui.SmallButton("Down")) MoveCharacter(siblings, index, 1);
            ImGui.EndDisabled();
            ImGui.EndTable();
        }
        if (selectedCharacter == character.ContentId)
        { ImGui.BeginDisabled(SettingsLocked); DrawCharacterOptions(character, rule); ImGui.EndDisabled(); }
        ImGui.Separator();
        ImGui.PopID();
    }

    private void DrawCharacterOptions(CharacterProfile character, FuelCompanyRule rule)
    {
        var fuel = character.Fuel;
        ImGui.Indent(TraderStyle.Px(12));
        TraderStyle.Section("Buyer preferences", character.Name);
        var overrideHouse = fuel.HouseId.HasValue;
        if (ImGui.Checkbox("Override the FC destination with a house", ref overrideHouse))
        {
            fuel.HouseId = overrideHouse ? HousesForWorld(character.World).FirstOrDefault()?.Id : null;
            plugin.Save();
        }
        if (overrideHouse)
        {
            var house = fuel.HouseId;
            if (HouseSelector("##CharacterHouse", character.World, ref house, "Choose a workshop")) { fuel.HouseId = house; plugin.Save(); }
        }
        else
        {
            var house = plugin.Config.Houses.FirstOrDefault(candidate => candidate.Id == rule.HouseId);
            UiTone.Wrapped(TraderStyle.Muted, rule.UseCaretaker
                ? $"Uses FC destination: Resident Caretaker · {LifestreamClient.DistrictName(rule.CaretakerCity)}"
                : $"Uses FC destination: {house?.Name ?? "not selected"}");
        }
        if (ImGui.TreeNode("Route details & access requirements"))
        { DrawAccess(character, detailed: true); ImGui.TreePop(); }
        ImGui.Spacing();
        ImGui.TextColored(TraderStyle.Gold, "PURCHASE PLAN");
        var exhaust = fuel.ExhaustCredits;
        var changed = false;
        if (ImGui.RadioButton("Use FC budget", exhaust)) { exhaust = true; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Restock to an amount", !exhaust)) { exhaust = false; changed = true; }
        if (changed)
        {
            fuel.ExhaustCredits = exhaust;
            if (!exhaust && fuel.TargetQuantity == 0) { fuel.BuyBelow = 1; fuel.TargetQuantity = 999; }
            plugin.Save();
        }
        if (exhaust) UiTone.Wrapped(TraderStyle.Muted, "Keep buying until the FC credit reserve is reached or storage is full.");
        if (!exhaust)
        {
            var below = fuel.BuyBelow;
            ImGui.SetNextItemWidth(TraderStyle.Px(160));
            if (EditUInt("Buy when carrying fewer than", ref below)) { fuel.BuyBelow = below; plugin.Save(); }
            var target = fuel.TargetQuantity;
            ImGui.SetNextItemWidth(TraderStyle.Px(160));
            if (EditUInt("Restock to", ref target)) { fuel.TargetQuantity = target; plugin.Save(); }
        }
        ImGui.Spacing();
        ImGui.TextColored(TraderStyle.Gold, "STORAGE");
        var deposit = fuel.DepositToRetainers;
        if (ImGui.Checkbox("Store fuel in retainers and continue buying", ref deposit)) { fuel.DepositToRetainers = deposit; plugin.Save(); }
        if (deposit)
        {
            var keep = fuel.KeepCarried;
            ImGui.SetNextItemWidth(TraderStyle.Px(160));
            if (EditUInt("Tanks to keep carried", ref keep)) { fuel.KeepCarried = keep; plugin.Save(); }
            UiTone.Wrapped(TraderStyle.Muted, "Stores all carried tanks above this amount, including fuel already in your bags. Uses this character's home-world bell and configured retainer selection.");
        }
        ImGui.Unindent(TraderStyle.Px(12));
    }

    private void DrawAccess(CharacterProfile character, bool detailed, FuelQueueEntry? entry = null)
    {
        entry ??= plugin.FuelQueue.PreviewCharacter(character);
        if (entry.Status == FuelQueueEntryStatus.Skipped)
        {
            UiTone.Wrapped(UiTone.Error, "Needs attention · " + entry.Note);
            return;
        }
        var access = plugin.FuelAccess.Assess(character, entry);
        var manualEntry = access.Status == FuelAccessStatus.Unknown && access.Kind == FuelAccessRouteKind.Unavailable;
        var tone = manualEntry ? UiTone.Warning : access.Status switch
        {
            FuelAccessStatus.Eligible => UiTone.Good,
            FuelAccessStatus.Ineligible => UiTone.Warning,
            _ => TraderStyle.Muted,
        };
        var label = manualEntry ? "Manual entry needed" : access.Status switch
        {
            FuelAccessStatus.Eligible => "Route available",
            FuelAccessStatus.Ineligible => "Route unavailable",
            _ => "Check on login",
        };
        UiTone.Wrapped(tone, $"{label} · {access.RouteLabel}");
        if (!detailed)
        {
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(access.Reason);
            return;
        }
        UiTone.Wrapped(tone, access.Reason);
        if (!string.IsNullOrWhiteSpace(access.RecommendedRoute))
            UiTone.Wrapped(UiTone.Info, "Recommended: " + access.RecommendedRoute);
        if (access.SnapshotObservedAtUtc is { } observed)
            UiTone.Wrapped(TraderStyle.Muted, $"Access observed {observed.ToLocalTime():g}. Rechecked before travel when this character is logged in.");
        UiTone.Wrapped(TraderStyle.Muted, "Travel access does not guarantee workshop entry or permission to spend FC credits. Those are verified at the destination.");
        ImGui.Spacing();
    }

    private void DrawQueueProgress()
    {
        var run = plugin.FuelQueue.Run;
        if (run is { Finished: true }) ImGui.Checkbox("Show last run instead of preview", ref showLastRun);
        var showingRun = run is { Finished: false } || run != null && showLastRun;
        var entries = showingRun ? run!.Entries : plugin.FuelQueue.Preview().ToList();
        if (entries.Count == 0)
        {
            TraderStyle.Section("Your next fuel run", "Select buyers and destinations to build a queue. Each PC runs independently.");
            if (TraderStyle.PrimaryButton("Choose buyers")) page = 0;
            return;
        }
        if (TraderStyle.BeginCard("FuelQueuePreview"))
        {
            TraderStyle.Section(run is { Finished: false } ? "Live itinerary" : showingRun ? "Last run results" : "Your next run",
                "Grouped by world and company. When storage fills, the next buyer in that FC takes over.");
            if (ImGui.BeginTable("FuelQueueRows", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(105));
                ImGui.TableSetupColumn(showingRun ? "Tanks" : "Purchase plan", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(145));
                ImGui.TableHeadersRow();
                foreach (var entry in entries)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.CharacterName);
                    UiTone.Wrapped(TraderStyle.Muted, entry.UseCaretaker
                        ? $"{entry.World} · Resident Caretaker, {LifestreamClient.DistrictName(entry.CaretakerCity)}"
                        : $"{entry.World} · {entry.HouseName}");
                    if (!string.IsNullOrWhiteSpace(entry.Note)) UiTone.Wrapped(TraderStyle.Muted, entry.Note);
                    if (!showingRun && plugin.Config.Characters.FirstOrDefault(character => character.ContentId == entry.CharacterId) is { } buyer)
                        DrawAccess(buyer, detailed: false, entry);
                    ImGui.TableNextColumn();
                    var tone = entry.Status switch
                    {
                        FuelQueueEntryStatus.Running => UiTone.Info,
                        FuelQueueEntryStatus.Completed => UiTone.Good,
                        FuelQueueEntryStatus.Skipped or FuelQueueEntryStatus.StorageFull => UiTone.Warning,
                        _ => TraderStyle.Muted,
                    };
                    ImGui.TextColored(tone, entry.Status == FuelQueueEntryStatus.StorageFull ? "Storage full" : entry.Status.ToString());
                    ImGui.TableNextColumn();
                    if (showingRun)
                    { ImGui.TextColored(TraderStyle.Accent, $"{entry.Purchased:N0} bought"); ImGui.TextDisabled($"{entry.Deposited:N0} stored"); }
                    else
                    { ImGui.TextUnformatted(entry.ExhaustCredits ? "Use FC budget" : $"To {entry.TargetQuantity:N0}"); ImGui.TextDisabled(entry.DepositToRetainers ? "Retainer storage" : "Carry only"); }
                }
                ImGui.EndTable();
            }
        }
        TraderStyle.EndCard();
    }

    private void DrawHouses()
    {
        if (TraderStyle.BeginCard("FuelHouseLibrary"))
        {
            TraderStyle.Section("Your destination library", "Saved workshops are available to every buyer on the same world.");
            if (TraderStyle.PrimaryButton("Add a house", new(140, 32))) addingHouse = !addingHouse;
            ImGui.SameLine();
            if (TraderStyle.SecondaryButton("Record this workshop", new(190, 32))) plugin.QueueAction(plugin.FuelQueue.CaptureHouse);
            UiTone.Wrapped(TraderStyle.Muted, "Lifestream houses are imported automatically. No workshop? Choose Resident Caretaker in the Buyers view instead.");
            if (addingHouse) DrawHouseForm();
        }
        TraderStyle.EndCard();
        foreach (var world in plugin.Config.Houses.GroupBy(HouseWorldName).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            ImGui.PushID(world.Key);
            if (TraderStyle.BeginCard("FuelWorldHouses"))
            {
                TraderStyle.Section(world.Key, $"{world.Count()} registered house{(world.Count() == 1 ? "" : "s")}");
                foreach (var house in world)
                {
                    ImGui.PushID(house.Id.ToString());
                    ImGui.TextColored(TraderStyle.Text, house.Name);
                    UiTone.Wrapped(TraderStyle.Muted, AddressLabel(house));
                    if (ImGui.TreeNode("Rename house"))
                    {
                        var name = house.Name;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputText("##HouseName", ref name, 128)) { house.Name = name; plugin.Save(); }
                        ImGui.TreePop();
                    }
                    ImGui.Separator();
                    ImGui.PopID();
                }
            }
            TraderStyle.EndCard();
            ImGui.PopID();
        }
        if (plugin.Config.Houses.Count == 0)
            UiTone.Wrapped(TraderStyle.Muted, "No houses found. Add a house or save an address in Lifestream and refresh setup.");
    }

    private void DrawHouseForm()
    {
        ImGui.Separator();
        ImGui.TextDisabled("New workshop house");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##NewHouseName", "House label", ref houseName, 128);
        worlds ??= Plugin.DataManager.GetExcelSheet<World>().Where(world => world.IsPublic && !world.Name.IsEmpty)
            .Select(world => (world.RowId, world.Name.ToString())).OrderBy(world => world.Item2, StringComparer.OrdinalIgnoreCase).ToList();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##NewHouseWorld", worlds.FirstOrDefault(world => world.Id == houseWorld).Name ?? "Select world"))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##HouseWorldSearch", "Find world...", ref houseWorldSearch, 100);
            foreach (var world in worlds.Where(world => world.Name.Contains(houseWorldSearch, StringComparison.OrdinalIgnoreCase)))
                if (ImGui.Selectable(world.Name, world.Id == houseWorld)) { houseWorld = (int)world.Id; houseWorldSearch = ""; }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##NewHouseDistrict", LifestreamClient.DistrictName(houseCity)))
        {
            foreach (var city in new[] { 8, 2, 9, 111, 70 })
                if (ImGui.Selectable(LifestreamClient.DistrictName(city), city == houseCity)) houseCity = city;
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(TraderStyle.Px(140));
        if (ImGui.InputInt("Ward", ref houseWard)) houseWard = Math.Clamp(houseWard, 1, 30);
        ImGui.SetNextItemWidth(TraderStyle.Px(140));
        if (ImGui.InputInt("Plot", ref housePlot)) housePlot = Math.Clamp(housePlot, 1, 60);
        ImGui.BeginDisabled(houseWorld == 0 || string.IsNullOrWhiteSpace(houseName));
        if (TraderStyle.PrimaryButton("Register house", new(155, 32)))
        {
            var name = houseName.Trim(); var world = houseWorld; var city = houseCity; var ward = houseWard; var plot = housePlot;
            plugin.QueueAction(() => plugin.FuelQueue.RegisterHouse(name, (uint)world, city, ward, plot));
            addingHouse = false; houseName = "";
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (TraderStyle.SecondaryButton("Cancel", new(90, 32))) addingHouse = false;
    }

    private bool HouseSelector(string id, string world, ref Guid? value, string empty)
    {
        var chosenId = value;
        var selected = plugin.Config.Houses.FirstOrDefault(house => house.Id == chosenId);
        var changed = false;
        var current = selected != null && IsSameWorld(selected, world) ? selected.Name : selected != null ? "Choose a house on this world" : empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo(id, current))
        {
            if (ImGui.Selectable("Not assigned", value == null)) { value = null; changed = true; }
            var candidates = HousesForWorld(world);
            foreach (var house in candidates)
                if (ImGui.Selectable($"{house.Name} · {AddressLabel(house)}##{house.Id}", value == house.Id)) { value = house.Id; changed = true; }
            if (candidates.Count == 0) ImGui.TextDisabled("Register a workshop on this world first.");
            ImGui.EndCombo();
        }
        return changed;
    }

    private void DrawCompanyDestination(string companyKey, string world, FuelCompanyRule rule)
    {
        var chosen = plugin.Config.Houses.FirstOrDefault(house => house.Id == rule.HouseId);
        var label = rule.UseCaretaker ? "Resident Caretaker · no house needed"
            : chosen != null && IsSameWorld(chosen, world) ? chosen.Name : "Choose a fuel destination";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##CompanyDestination", label))
        {
            if (ImGui.Selectable("Resident Caretaker · no house needed", rule.UseCaretaker))
            { rule.UseCaretaker = true; rule.HouseId = null; SaveRule(companyKey, rule); }
            foreach (var house in HousesForWorld(world))
            {
                if (ImGui.Selectable($"{house.Name} · {AddressLabel(house)}##{house.Id}", !rule.UseCaretaker && rule.HouseId == house.Id))
                { rule.UseCaretaker = false; rule.HouseId = house.Id; SaveRule(companyKey, rule); }
            }
            ImGui.EndCombo();
        }
        if (!rule.UseCaretaker) return;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##CaretakerDistrict", LifestreamClient.DistrictName(rule.CaretakerCity)))
        {
            foreach (var city in new[] { 8, 2, 9, 111, 70 })
                if (ImGui.Selectable(LifestreamClient.DistrictName(city), city == rule.CaretakerCity))
                { rule.CaretakerCity = city; SaveRule(companyKey, rule); }
            ImGui.EndCombo();
        }
        UiTone.Wrapped(TraderStyle.Muted, "The Resident Caretaker is used in this world's residential district. No company house is required; FC rank and spending permissions still apply.");
    }

    private List<HouseProfile> HousesForWorld(string world) => plugin.Config.Houses.Where(house => IsSameWorld(house, world))
        .OrderBy(house => house.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsSameWorld(HouseProfile house, string world) => LifestreamClient.TryParseAddress(house.Address, out var destination)
        && string.Equals(Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault((uint)destination.World)?.Name.ToString(), world, StringComparison.OrdinalIgnoreCase);

    private static string HouseWorldName(HouseProfile house) => LifestreamClient.TryParseAddress(house.Address, out var destination)
        ? Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault((uint)destination.World)?.Name.ToString() ?? $"World {destination.World}"
        : "Unrecognized address";

    private static string AddressLabel(HouseProfile house) => LifestreamClient.TryParseAddress(house.Address, out var destination)
        ? $"{LifestreamClient.DistrictName(destination.City)} · Ward {destination.Ward}, Plot {destination.Plot}" : house.Address;

    private string CompanyName(CharacterProfile character)
    {
        var rule = plugin.Config.FuelSettings.Companies.GetValueOrDefault(FuelQueuePlanner.CompanyKey(character));
        return !string.IsNullOrWhiteSpace(rule?.NameOverride) ? rule.NameOverride
            : !string.IsNullOrWhiteSpace(character.FreeCompanyName) ? character.FreeCompanyName
            : character.FreeCompanyId is > 0 ? $"FC {character.FreeCompanyId}"
            : character.FreeCompanyId == 0 ? $"No FC · {character.Name}" : $"FC unknown · {character.Name}";
    }

    private void SaveRule(string key, FuelCompanyRule rule) { plugin.Config.FuelSettings.Companies[key] = rule; plugin.Save(); }
    private static void Enable(CharacterProfile character, bool enabled)
    {
        character.Fuel.Enabled = enabled;
        if (enabled && character.Fuel.BuyBelow == 0 && character.Fuel.TargetQuantity == 0) character.Fuel.ExhaustCredits = true;
    }

    private void MoveCharacter(IReadOnlyList<CharacterProfile> characters, int index, int direction)
    {
        var all = characters.ToList();
        var destination = index + direction;
        if (index < 0 || destination < 0 || destination >= all.Count) return;
        (all[index], all[destination]) = (all[destination], all[index]);
        for (var order = 0; order < all.Count; order++) all[order].Fuel.QueueOrder = order;
        plugin.Save();
    }

    private static bool EditUInt(string label, ref uint value)
    {
        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!ImGui.InputText(label, ref text, 10, ImGuiInputTextFlags.CharsDecimal)) return false;
        if (text.Length == 0) { value = 0; return true; }
        if (!uint.TryParse(text, out var parsed)) return false;
        value = parsed;
        return true;
    }
}
