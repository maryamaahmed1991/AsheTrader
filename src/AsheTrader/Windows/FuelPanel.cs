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
    private bool housesPage;
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
        {
            ImGui.SetClipboardText(report);
            diagnosticsCopied = true;
        }
        if (!SettingsLocked && (!requestedRefresh || Environment.TickCount64 >= refreshAfter)) Refresh();
        DrawRunControls();
        if (TraderStyle.SecondaryButton("Fuel queue", new(135, 32))) housesPage = false;
        ImGui.SameLine();
        if (TraderStyle.SecondaryButton("Workshop houses", new(170, 32))) housesPage = true;
        ImGui.SameLine();
        ImGui.BeginDisabled(SettingsLocked);
        if (ImGui.SmallButton("Refresh setup")) Refresh();
        ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(plugin.FuelQueue.SetupStatus))
            UiTone.Wrapped(TraderStyle.Muted, plugin.FuelQueue.SetupStatus);
        ImGui.Spacing();

        if (!housesPage && plugin.FuelQueue.HasUnfinishedRun)
        {
            DrawQueueProgress();
            if (ImGui.CollapsingHeader("View saved queue setup"))
            {
                ImGui.BeginDisabled();
                DrawCharacters();
                ImGui.EndDisabled();
            }
            return;
        }

        if (SettingsLocked)
            UiTone.Wrapped(TraderStyle.Muted, "Setup is locked while a saved run is unfinished. Queue controls remain available above.");
        ImGui.BeginDisabled(SettingsLocked);
        if (housesPage) DrawHouses();
        else DrawCharacters();
        ImGui.EndDisabled();
        if (!housesPage) DrawQueueProgress();
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
        var selected = plugin.Config.Characters.Count(character => character.Fuel.Enabled);
        if (TraderStyle.BeginCard("FuelRunControls"))
        {
            TraderStyle.Badge(queue.Active ? "RUNNING" : queue.HasUnfinishedRun ? "PAUSED" : "THIS PC", queue.Active ? TraderStyle.Accent : TraderStyle.Gold);
            ImGui.SameLine();
            ImGui.TextDisabled($"{selected:N0} selected character{(selected == 1 ? "" : "s")}");
            UiTone.Status(queue.Status);
            if (run != null)
            {
                var completed = run.Entries.Count(entry => entry.Status is FuelQueueEntryStatus.Completed or FuelQueueEntryStatus.Skipped or FuelQueueEntryStatus.StorageFull);
                UiTone.Wrapped(TraderStyle.Muted, $"{completed:N0}/{run.Entries.Count:N0} characters  ·  {run.TotalPurchased:N0} tanks bought  ·  {run.TotalDeposited:N0} stored");
                if (run.Entries.Count > 0) UiTone.Progress((float)completed / run.Entries.Count, new(-1, TraderStyle.Px(5)), run.Paused);
            }
            ImGui.BeginDisabled(TransferLocked || plugin.Trader.IsRunning || plugin.Fuel.Active && !queue.Active || selected == 0 || queue.HasUnfinishedRun);
            if (TraderStyle.PrimaryButton("Start selected", new(150, 34))) plugin.QueueAction(queue.Start);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!queue.HasUnfinishedRun || TransferLocked || plugin.Trader.IsRunning);
            if (queue.Active)
            {
                if (TraderStyle.SecondaryButton("Pause", new(90, 34))) plugin.QueueAction(() => queue.Pause());
            }
            else if (TraderStyle.SecondaryButton("Resume", new(90, 34))) plugin.QueueAction(queue.Resume);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!queue.HasUnfinishedRun);
            if (TraderStyle.SecondaryButton("End queue", new(110, 34))) plugin.QueueAction(queue.End);
            ImGui.EndDisabled();
            if (TransferLocked)
                UiTone.Wrapped(UiTone.Warning, "Finish or end the transfer job before starting a fuel queue on this PC.");
            else if (selected == 0 && !queue.HasUnfinishedRun)
                UiTone.Wrapped(TraderStyle.Muted, "Select fuel buyers below, choose their destinations, then start the queue.");
            ImGui.BeginDisabled(Volatile.Read(ref diagnosticCopyPending) != 0);
            if (ImGui.SmallButton("Copy diagnostics"))
            {
                diagnosticsCopied = false;
                Interlocked.Exchange(ref diagnosticCopyPending, 1);
                plugin.QueueAction(() =>
                {
                    try { Volatile.Write(ref diagnosticsToCopy, plugin.Workflow.CaptureDiagnosticReport()); }
                    catch (Exception ex) { Volatile.Write(ref diagnosticsToCopy, $"Ashe's Trader diagnostic capture failed: {ex.Message}"); }
                    finally { Interlocked.Exchange(ref diagnosticCopyPending, 0); }
                });
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy this PC's fuel queue state and recent steps when reporting an issue. Includes character and retainer identifiers; excludes pairing keys and chat.");
            if (diagnosticsCopied) { ImGui.SameLine(); ImGui.TextColored(UiTone.Good, "Copied for this PC."); }
        }
        TraderStyle.EndCard();
    }

    private void DrawCharacters()
    {
        if (TraderStyle.BeginCard("FuelRosterFilter"))
        {
            ImGui.TextColored(TraderStyle.Gold, "CHOOSE YOUR FUEL BUYERS");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##FuelRosterSearch", "Find a world, FC or character...", ref filter, 200);
            ImGui.Checkbox("Selected characters only", ref selectedOnly);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Characters run in world and FC order. If a buyer's storage fills, the next selected character in that FC can continue.");
        }
        TraderStyle.EndCard();

        var all = plugin.Config.Characters.Where(character => (!selectedOnly || character.Fuel.Enabled)
            && (string.IsNullOrWhiteSpace(filter) || character.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || character.World.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || CompanyName(character).Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (all.Length == 0)
        {
            UiTone.Wrapped(TraderStyle.Muted, plugin.Config.Characters.Count == 0
                ? "No characters yet. Refresh setup to import this PC's AutoRetainer roster."
                : "No characters match these filters.");
            return;
        }

        foreach (var world in all.GroupBy(character => character.World).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            ImGui.PushID(world.Key);
            var houseCount = HousesForWorld(world.Key).Count;
            if (ImGui.CollapsingHeader($"{world.Key}  ·  {world.Count(character => character.Fuel.Enabled)}/{world.Count()} selected  ·  {houseCount} house{(houseCount == 1 ? "" : "s")}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (houseCount == 0)
                    UiTone.Wrapped(TraderStyle.Muted, "No house registered here. Choose a Resident Caretaker, or add a workshop under Workshop houses.");
                foreach (var company in world.GroupBy(FuelQueuePlanner.CompanyKey).OrderBy(group => group.Key, StringComparer.Ordinal))
                    DrawCompany(company.Key, company.OrderBy(character => character.Fuel.QueueOrder).ThenBy(character => character.Name, StringComparer.OrdinalIgnoreCase).ToArray());
            }
            ImGui.PopID();
        }
    }

    private void DrawCompany(string companyKey, IReadOnlyList<CharacterProfile> characters)
    {
        var first = characters[0];
        var rule = plugin.Config.FuelSettings.Companies.GetValueOrDefault(companyKey) ?? new FuelCompanyRule();
        var title = CompanyName(first);
        var selected = characters.Count(character => character.Fuel.Enabled);
        ImGui.PushID(companyKey);
        if (TraderStyle.BeginCard("FuelCompany"))
        {
            if (ImGui.TreeNodeEx($"{title}  ·  {selected}/{characters.Count} selected##Company", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var creditSnapshot = characters.Where(character => character.FreeCompanyCredits.HasValue)
                    .OrderByDescending(character => character.FreeCompanyCreditsObservedAt).FirstOrDefault();
                if (creditSnapshot != null)
                {
                    var age = creditSnapshot.FreeCompanyCreditsObservedAt.HasValue
                        ? $" · observed {creditSnapshot.FreeCompanyCreditsObservedAt.Value.ToLocalTime():g}" : "";
                    UiTone.Wrapped(TraderStyle.Gold, $"{creditSnapshot.FreeCompanyCredits:N0} FC credits cached{age}");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("All buyers in this FC share this balance. The current balance is checked at purchase time.");
                }
                else UiTone.Wrapped(TraderStyle.Muted, "FC credit balance will be checked when a buyer logs in.");
                if (!first.FreeCompanyId.HasValue)
                    UiTone.Wrapped(UiTone.Warning, "FC membership will be checked after login. Unknown characters stay separate until their FC is known.");
                else if (first.FreeCompanyId == 0)
                    UiTone.Wrapped(UiTone.Warning, "This character has no FC and will be skipped.");

                var destination = plugin.Config.Houses.FirstOrDefault(house => house.Id == rule.HouseId);
                UiTone.Wrapped(rule.UseCaretaker || destination != null ? TraderStyle.Muted : UiTone.Warning,
                    rule.UseCaretaker ? $"Resident Caretaker · {LifestreamClient.DistrictName(rule.CaretakerCity)} · leave {rule.CreditReserve:N0} credits"
                        : destination != null ? $"{destination.Name} · leave {rule.CreditReserve:N0} credits"
                        : "Choose the FC destination below, or assign each buyer a house.");
                if (ImGui.SmallButton("Select these characters"))
                { foreach (var character in characters) Enable(character, true); plugin.Save(); }
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear selection"))
                { foreach (var character in characters) character.Fuel.Enabled = false; plugin.Save(); }
                ImGui.Spacing();

                foreach (var character in characters) DrawCharacter(character, characters, rule);
                if (ImGui.TreeNode("FC destination & credit reserve"))
                {
                    var companyLabel = rule.NameOverride;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputTextWithHint("##CompanyLabel", "Optional FC display name", ref companyLabel, 128))
                    { rule.NameOverride = companyLabel; SaveRule(companyKey, rule); }
                    DrawCompanyDestination(companyKey, first.World, rule);
                    var reserve = rule.CreditReserve;
                    ImGui.SetNextItemWidth(TraderStyle.Px(160));
                    if (EditUInt("Credits to leave", ref reserve)) { rule.CreditReserve = reserve; SaveRule(companyKey, rule); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Zero converts every affordable FC credit into Ceruleum Tanks. This reserve applies to all selected buyers in the FC.");
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
        }
        TraderStyle.EndCard();
        ImGui.PopID();
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
            if (ImGui.Checkbox("##Enabled", ref enabled)) { Enable(character, enabled); plugin.Save(); }
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
            ImGui.TableNextColumn();
            var index = siblings.ToList().FindIndex(profile => profile.ContentId == character.ContentId);
            ImGui.BeginDisabled(index <= 0);
            if (ImGui.SmallButton("Up")) MoveCharacter(siblings, index, -1);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(index < 0 || index + 1 >= siblings.Count);
            if (ImGui.SmallButton("Down")) MoveCharacter(siblings, index, 1);
            ImGui.EndDisabled();
            ImGui.EndTable();
        }
        if (selectedCharacter == character.ContentId) DrawCharacterOptions(character, rule);
        ImGui.Separator();
        ImGui.PopID();
    }

    private void DrawCharacterOptions(CharacterProfile character, FuelCompanyRule rule)
    {
        var fuel = character.Fuel;
        ImGui.Indent(TraderStyle.Px(12));
        ImGui.TextDisabled("Character settings");
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
        var exhaust = fuel.ExhaustCredits;
        if (ImGui.Checkbox("Convert all credits above the FC reserve", ref exhaust))
        {
            fuel.ExhaustCredits = exhaust;
            if (!exhaust && fuel.TargetQuantity == 0) { fuel.BuyBelow = 1; fuel.TargetQuantity = 999; }
            plugin.Save();
        }
        if (!exhaust)
        {
            var below = fuel.BuyBelow;
            ImGui.SetNextItemWidth(TraderStyle.Px(160));
            if (EditUInt("Buy when carrying fewer than", ref below)) { fuel.BuyBelow = below; plugin.Save(); }
            var target = fuel.TargetQuantity;
            ImGui.SetNextItemWidth(TraderStyle.Px(160));
            if (EditUInt("Restock to", ref target)) { fuel.TargetQuantity = target; plugin.Save(); }
        }
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

    private void DrawQueueProgress()
    {
        var run = plugin.FuelQueue.Run;
        if (run is { Finished: true }) ImGui.Checkbox("Show last run instead of preview", ref showLastRun);
        var showingRun = run is { Finished: false } || run != null && showLastRun;
        var entries = showingRun ? run!.Entries : plugin.FuelQueue.Preview().ToList();
        if (entries.Count == 0) return;
        if (TraderStyle.BeginCard("FuelQueuePreview"))
        {
            TraderStyle.Section(run is { Finished: false } ? "Current queue" : showingRun ? "Last run" : "Queue preview", "Each PC runs its own selected characters.");
            if (ImGui.BeginTable("FuelQueueRows", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(105));
                ImGui.TableSetupColumn("Tanks", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(105));
                ImGui.TableHeadersRow();
                foreach (var entry in entries)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.CharacterName);
                    UiTone.Wrapped(TraderStyle.Muted, entry.UseCaretaker
                        ? $"{entry.World} · Resident Caretaker, {LifestreamClient.DistrictName(entry.CaretakerCity)}"
                        : $"{entry.World} · {entry.HouseName}");
                    if (!string.IsNullOrWhiteSpace(entry.Note)) UiTone.Wrapped(TraderStyle.Muted, entry.Note);
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
                    ImGui.TextUnformatted($"{entry.Purchased:N0} bought");
                    ImGui.TextDisabled($"{entry.Deposited:N0} stored");
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
            TraderStyle.Section("Workshop houses", "Saved Lifestream houses are imported automatically. Add any other workshop you can access here.");
            if (TraderStyle.PrimaryButton("Add a house", new(140, 32))) addingHouse = !addingHouse;
            ImGui.SameLine();
            if (TraderStyle.SecondaryButton("Record this workshop", new(190, 32))) plugin.QueueAction(plugin.FuelQueue.CaptureHouse);
            ImGui.TextWrapped("Characters can only use houses on their home world. A saved address must have an accessible company workshop and fuel supplier.");
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
                    var name = house.Name;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText("##HouseName", ref name, 128)) { house.Name = name; plugin.Save(); }
                    UiTone.Wrapped(TraderStyle.Muted, AddressLabel(house));
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
