using System.Numerics;
using AsheTrader.Core;
using AsheTrader.Fuel;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace AsheTrader.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly WorkflowPanel workflowPanel;
    private Guid selectedList;
    private string search = string.Empty;
    private string importStatus = "Import AutoRetainer's known characters to start assigning accounts.";
    private uint selectedItem;
    private int quantity = 999;
    private bool highQuality;
    private string error = string.Empty;
    private List<(uint Id, string Name)>? catalog;

    public MainWindow(Plugin plugin) : base("Ashe's Trader###AsheTraderMain")
    {
        this.plugin = plugin;
        workflowPanel = new WorkflowPanel(plugin);
        Size = new Vector2(920, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(650, 440), MaximumSize = new Vector2(float.MaxValue) };
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("ASHE'S TRADER");
        ImGui.SameLine();
        ImGui.TextDisabled("v0.3.8.1 · two-PC test build");
        ImGui.SameLine();
        ImGui.BeginDisabled(!plugin.Trader.IsRunning && !plugin.Fuel.Active && !plugin.Workflow.Busy);
        if (ImGui.Button("STOP ALL", new Vector2(110, 26))) plugin.QueueAction(plugin.StopAll);
        ImGui.EndDisabled();
        ImGui.Separator();
        if (plugin.Trader.IsRunning) UiTone.Status(plugin.Trader.Status);
        if (plugin.Fuel.Active) UiTone.Status(plugin.Fuel.Status);
        if (!string.IsNullOrEmpty(plugin.SettingsError)) UiTone.Wrapped(UiTone.Error, plugin.SettingsError);
        if (!string.IsNullOrEmpty(error)) UiTone.Wrapped(UiTone.Error, error);
        if (!ImGui.BeginTabBar("MainTabs")) return;
        if (ImGui.BeginTabItem("Transfers")) { workflowPanel.Draw(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Fuel & houses"))
        {
            if (ImGui.BeginChild("FuelPage", new Vector2(0, 0)))
            {
                ImGui.BeginDisabled(plugin.Workflow.Busy || plugin.Workflow.Run is { Step: not Workflow.CoordinatorStep.Finished });
                DrawFuel();
                ImGui.EndDisabled();
            }
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Advanced tools"))
        {
            if (ImGui.BeginChild("AdvancedPage", new Vector2(0, 0)))
            {
                ImGui.TextWrapped("Transfer jobs handle the two-PC workflow automatically. These separate tools are for manually directed trades and character configuration.");
                var workflowLocked = plugin.Workflow.Busy || plugin.Workflow.Run is { Step: not Workflow.CoordinatorStep.Finished };
                if (workflowLocked) ImGui.TextWrapped("Manual actions and character settings are locked until the transfer job is resolved and ended.");
                ImGui.BeginDisabled(workflowLocked);
                if (ImGui.CollapsingHeader("Manual trade lists")) DrawTradeLists();
                if (ImGui.CollapsingHeader("Character profiles")) DrawCharacters();
                ImGui.EndDisabled();
                if (ImGui.CollapsingHeader("Activity log")) DrawActivity();
            }
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawTradeLists()
    {
        ImGui.Spacing();
        var target = Plugin.TargetManager.Target as IPlayerCharacter;
        ImGui.TextUnformatted(target == null ? "Target one of your other characters in-game." : $"Selected partner: {target.Name}@{target.HomeWorld.Value.Name}");
        ImGui.TextWrapped("On the receiving client, target the sender and arm receive mode. On the sending client, choose a list and start. Receive mode accepts incoming items from that partner and offers nothing.");
        ImGui.BeginDisabled(plugin.Trader.IsRunning || plugin.Fuel.Active || target == null);
        if (ImGui.Button("Arm receive from selected partner") && target != null)
        {
            var partner = target;
            plugin.QueueAction(() => { EnsureArIdle(); plugin.Trader.ArmReceiver(partner); });
        }
        ImGui.EndDisabled();
        ImGui.Separator();
        if (plugin.Trader.CompletedBatches > 0)
        {
            ImGui.TextUnformatted($"Last/current run: {plugin.Trader.CompletedBatches} verified batches.");
            foreach (var line in plugin.Trader.Remaining) ImGui.TextUnformatted($"Remaining: {ItemName(line.ItemId)}{(line.HighQuality ? " HQ" : "")} × {line.Quantity:N0}");
            ImGui.TextWrapped("Starting again uses the full saved list. After a partial run, reconcile your inventory and update the list first.");
        }
        ImGui.BeginDisabled(plugin.Trader.IsRunning || plugin.Fuel.Active);
        var list = plugin.Config.TradeLists.FirstOrDefault(x => x.Id == selectedList);
        if (ImGui.BeginCombo("Saved list", list?.Name ?? "Select a list"))
        {
            foreach (var candidate in plugin.Config.TradeLists)
                if (ImGui.Selectable($"{candidate.Name}##{candidate.Id}", candidate.Id == selectedList)) { selectedList = candidate.Id; list = candidate; }
            ImGui.EndCombo();
        }
        if (ImGui.Button("New list"))
        {
            list = new TradeList { Name = $"Trade list {plugin.Config.TradeLists.Count + 1}" };
            plugin.Config.TradeLists.Add(list); selectedList = list.Id; plugin.Save();
        }
        if (list != null)
        {
            var name = list.Name;
            if (ImGui.InputText("List name", ref name, 100)) { list.Name = name; plugin.Save(); }
            for (var index = 0; index < list.Items.Count; index++)
            {
                var line = list.Items[index];
                ImGui.PushID(index);
                if (ImGui.SmallButton("Remove")) { list.Items.RemoveAt(index--); plugin.Save(); ImGui.PopID(); continue; }
                ImGui.SameLine();
                ImGui.TextUnformatted($"{ItemName(line.ItemId)}{(line.HighQuality ? " HQ" : "")}  × {line.Quantity:N0}");
                ImGui.PopID();
            }
            ImGui.Spacing();
            ImGui.InputText("Find item", ref search, 100);
            if (search.Length >= 2)
            {
                catalog ??= Plugin.DataManager.GetExcelSheet<Item>()
                    .Where(x => x.RowId > 19 && !x.IsUntradable && !x.Name.IsEmpty)
                    .Select(x => (x.RowId, x.Name.ToString())).ToList();
                if (ImGui.BeginCombo("Item", selectedItem == 0 ? "Select matching item" : ItemName(selectedItem)))
                {
                    foreach (var item in catalog.Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(100))
                        if (ImGui.Selectable($"{item.Name}##{item.Id}", selectedItem == item.Id)) selectedItem = item.Id;
                    ImGui.EndCombo();
                }
            }
            ImGui.Checkbox("High quality", ref highQuality);
            ImGui.InputInt("Total quantity", ref quantity, 1, 99);
            ImGui.BeginDisabled(selectedItem == 0 || quantity < 1);
            if (ImGui.Button("Add item"))
            {
                if (list.Items.Any(x => x.ItemId == selectedItem && x.HighQuality == highQuality)) error = "That item and quality are already in this list. Remove its old entry before replacing it.";
                else { list.Items.Add(new TradeLine { ItemId = selectedItem, HighQuality = highQuality, Quantity = (uint)quantity }); plugin.Save(); error = string.Empty; }
            }
            ImGui.EndDisabled();
            ImGui.Spacing();
            ImGui.BeginDisabled(target == null || list.Items.Count == 0);
            if (ImGui.Button("Start sending this list", new Vector2(220, 32)) && target != null)
            {
                var lines = list.Items.Select(x => new TradeLine { ItemId = x.ItemId, HighQuality = x.HighQuality, Quantity = x.Quantity }).ToArray();
                var partner = target;
                plugin.QueueAction(() => { EnsureArIdle(); plugin.Trader.StartSending(lines, partner); });
            }
            ImGui.EndDisabled();
            ImGui.TextWrapped("Transfers use your four main inventory bags. List quantities are per run; the saved list stays unchanged. Stop or logout ends the run.");
        }
        ImGui.EndDisabled();
    }

    private void EnsureArIdle()
    {
        var state = plugin.AutoRetainer.GetStatus();
        if (state.Ready && (state.Busy != false || state.MultiModeEnabled != false))
            throw new InvalidOperationException("Pause AutoRetainer's multi-mode and wait for its tasks to finish before starting a transfer.");
    }

    private void DrawCharacters()
    {
        ImGui.Spacing();
        if (ImGui.Button("Import / refresh AutoRetainer roster")) plugin.QueueAction(ImportCharacters);
        ImGui.SameLine();
        if (ImGui.Button("Add current character")) plugin.QueueAction(AddCurrentCharacter);
        UiTone.Status(importStatus);
        ImGui.TextWrapped("Account labels belong to Ashe's Trader. AutoRetainer's service-account number is retained as metadata, and is not treated as a unique login.");
        if (!ImGui.BeginTable("Characters", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable)) return;
        ImGui.TableSetupColumn("Character"); ImGui.TableSetupColumn("Home world"); ImGui.TableSetupColumn("Account label"); ImGui.TableSetupColumn("AR service account"); ImGui.TableSetupColumn("Switch"); ImGui.TableHeadersRow();
        foreach (var character in plugin.Config.Characters)
        {
            ImGui.PushID(character.ContentId.ToString());
            ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(character.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(character.World);
            ImGui.TableNextColumn(); var label = character.AccountLabel;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##account", ref label, 80)) { character.AccountLabel = label; plugin.Save(); }
            ImGui.TableNextColumn(); ImGui.TextUnformatted((character.ServiceAccount + 1).ToString());
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(plugin.Trader.IsRunning || plugin.Fuel.Active);
            if (ImGui.SmallButton("Log in via AR"))
            {
                var identity = $"{character.Name}@{character.World}";
                plugin.QueueAction(() => { if (!plugin.AutoRetainer.TryRelog(identity, out var reason)) plugin.Record(reason); });
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void AddCurrentCharacter()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || Plugin.PlayerState.ContentId == 0) { importStatus = "Log in before adding the current character."; return; }
        if (plugin.Config.Characters.All(x => x.ContentId != Plugin.PlayerState.ContentId))
            plugin.Config.Characters.Add(new CharacterProfile { ContentId = Plugin.PlayerState.ContentId, Name = player.Name.TextValue, World = player.HomeWorld.Value.Name.ToString() });
        plugin.Save();
        importStatus = "Current character is available in the roster and fuel settings.";
    }

    private void ImportCharacters()
    {
        plugin.Workflow.RefreshRosters();
        importStatus = plugin.Workflow.RosterStatus;
        plugin.Record(importStatus);
    }

    private void DrawFuel()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Import a Lifestream destination or record the workshop you are in. Travel goes to the saved house. Enter its workshop and open the Ceruleum Tank shop before starting purchases.");
        ImGui.BeginDisabled(plugin.Trader.IsRunning || plugin.Fuel.Active);
        if (ImGui.Button("Import Lifestream houses")) plugin.QueueAction(() =>
        {
            if (!plugin.Lifestream.TryGetHouses(out var houses, out var reason)) { plugin.Record(reason); return; }
            foreach (var house in houses)
                if (plugin.Config.Houses.All(x => x.Address != house.Address)) plugin.Config.Houses.Add(house);
            plugin.Save(); plugin.Record($"Imported {houses.Count} Lifestream house destinations.");
        });
        ImGui.SameLine();
        if (ImGui.Button("Record this workshop")) plugin.QueueAction(() =>
        {
            if (!FuelRunner.TryCaptureCurrentHouse(out var house, out var reason)) { plugin.Record(reason); return; }
            if (plugin.Config.Houses.All(x => x.Address != house.Address)) plugin.Config.Houses.Add(house);
            plugin.Save(); plugin.Record($"Recorded {house.Name}.");
        });
        foreach (var house in plugin.Config.Houses)
        {
            ImGui.PushID(house.Id.ToString());
            var name = house.Name;
            if (ImGui.InputText("House name", ref name, 100)) { house.Name = name; plugin.Save(); }
            ImGui.TextDisabled(house.Address);
            if (house.Address.StartsWith("ls-house:", StringComparison.Ordinal) && ImGui.SmallButton("Travel to this house"))
            {
                var destination = house;
                plugin.QueueAction(() => { EnsureArIdle(); if (!plugin.Lifestream.TryTravel(destination, out var reason)) plugin.Record(reason); else plugin.Record($"Lifestream travel requested: {destination.Name}."); });
            }
            ImGui.PopID();
        }
        ImGui.Separator();
        foreach (var character in plugin.Config.Characters)
        {
            ImGui.PushID(character.ContentId.ToString());
            if (ImGui.CollapsingHeader($"{character.Name}@{character.World}"))
            {
                var fuel = character.Fuel;
                var enabled = fuel.Enabled;
                if (ImGui.Checkbox("Include in fuel runs", ref enabled)) { fuel.Enabled = enabled; plugin.Save(); }
                var selected = plugin.Config.Houses.FirstOrDefault(x => x.Id == fuel.HouseId);
                if (ImGui.BeginCombo("Workshop house", selected?.Name ?? "Select a house"))
                {
                    foreach (var house in plugin.Config.Houses)
                        if (ImGui.Selectable($"{house.Name} — {house.Address}", house.Id == fuel.HouseId)) { fuel.HouseId = house.Id; plugin.Save(); }
                    ImGui.EndCombo();
                }
                var exhaust = fuel.ExhaustCredits;
                if (ImGui.Checkbox("Buy until FC credits are exhausted (or inventory is full)", ref exhaust)) { fuel.ExhaustCredits = exhaust; plugin.Save(); }
                ImGui.BeginDisabled(exhaust);
                var below = (int)Math.Min(fuel.BuyBelow, int.MaxValue); var target = (int)Math.Min(fuel.TargetQuantity, int.MaxValue);
                if (ImGui.InputInt("Buy when carrying fewer than", ref below)) { fuel.BuyBelow = (uint)Math.Max(0, below); plugin.Save(); }
                if (ImGui.InputInt("Restock to this total", ref target)) { fuel.TargetQuantity = (uint)Math.Max(0, target); plugin.Save(); }
                ImGui.EndDisabled();
                ImGui.BeginDisabled(selected == null || !fuel.Enabled || character.ContentId != Plugin.PlayerState.ContentId);
                if (ImGui.Button("Start purchases in this workshop") && selected != null)
                {
                    var profile = character; var destination = selected;
                    plugin.QueueAction(() => { EnsureArIdle(); if (!plugin.Fuel.Start(profile, destination, out var reason)) plugin.Record(reason); });
                }
                ImGui.EndDisabled();
            }
            ImGui.PopID();
        }
        ImGui.EndDisabled();
    }

    private void DrawActivity()
    {
        var open = plugin.Config.OpenOnLoad;
        if (ImGui.Checkbox("Open this window when the plugin loads", ref open)) { plugin.Config.OpenOnLoad = open; plugin.Save(); }
        ImGui.TextWrapped("No run resumes automatically after reload. /ashetrader stop stops the current operation.");
        if (ImGui.Button("Reload shared settings")) plugin.ReloadSettings();
        ImGui.Separator();
        foreach (var entry in plugin.Activity.Reverse()) UiTone.Status(entry);
    }

    private static string ItemName(uint id) => Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(id, out var item) ? item.Name.ToString() : $"Item {id}";
}
