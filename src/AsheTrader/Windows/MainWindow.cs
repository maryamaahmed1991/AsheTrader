using System.Numerics;
using AsheTrader.Core;
using AsheTrader.Fuel;
using AsheTrader.Integrations;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace AsheTrader.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly WorkflowPanel workflowPanel;
    private readonly ItemSearchPanel itemSearchPanel;
    private readonly FuelPanel fuelPanel;
    private Guid selectedList;
    private string search = string.Empty;
    private string importStatus = "Import AutoRetainer's known characters to start assigning accounts.";
    private uint selectedItem;
    private int quantity = 999;
    private bool highQuality;
    private string error = string.Empty;
    private List<(uint Id, string Name)>? catalog;
    private int selectedPage;
    private IDisposable? theme;
    private static readonly string Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "";
    private static readonly string[] PageTitles = ["Connection", "Transfer jobs", "Review & run", "Activity", "Fuel & houses", "Advanced tools", "Item Search"];
    private static readonly string[] PageDescriptions =
    [
        "Connect your PCs and get your characters ready.",
        "Choose your items, providers and destination characters.",
        "Check the route and start when both PCs are ready.",
        "Follow the current run and inspect saved progress.",
        "Convert FC credits with world-by-world character queues.",
        "Manual trades, character profiles and local preferences.",
        "Find your items across characters and connected PCs.",
    ];

    public MainWindow(Plugin plugin) : base("Ashe's Trader###AsheTraderMain")
    {
        this.plugin = plugin;
        workflowPanel = new WorkflowPanel(plugin);
        itemSearchPanel = new ItemSearchPanel(plugin);
        fuelPanel = new FuelPanel(plugin);
        Size = new Vector2(1080, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(880, 620), MaximumSize = new Vector2(float.MaxValue) };
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public override void PreDraw() => theme = TraderStyle.Theme();

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    public override void Draw()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, TraderStyle.Sidebar);
        if (ImGui.BeginChild("Navigation", new Vector2(TraderStyle.Px(202), 0), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysUseWindowPadding)) DrawNavigation();
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.SameLine(0, TraderStyle.Px(20));
        if (ImGui.BeginChild("Workspace", new Vector2(0, 0), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawPageHeader();
            if (plugin.Trader.IsRunning) UiTone.Status(plugin.Trader.Status);
            if (plugin.Fuel.Active) UiTone.Status(plugin.Fuel.Status);
            if (!string.IsNullOrEmpty(plugin.SettingsError)) UiTone.Wrapped(UiTone.Error, plugin.SettingsError);
            if (!string.IsNullOrEmpty(error))
            {
                UiTone.Wrapped(UiTone.Error, error);
                if (ImGui.SmallButton("Dismiss message")) error = string.Empty;
            }
            if (selectedPage <= 3) selectedPage = workflowPanel.Draw(selectedPage);
            else if (selectedPage == 6) itemSearchPanel.Draw();
            else if (selectedPage == 4)
            {
                if (ImGui.BeginChild("FuelPage", new Vector2(0, 0))) fuelPanel.Draw();
                ImGui.EndChild();
            }
            else
            {
                if (ImGui.BeginChild("ToolPage", new Vector2(0, 0)))
                {
                    var workflowLocked = plugin.Workflow.Busy || plugin.Workflow.Run is { Step: not Workflow.CoordinatorStep.Finished } || plugin.FuelQueue.HasUnfinishedRun;
                    if (workflowLocked)
                        UiTone.Wrapped(UiTone.Warning, "These settings stay locked while a transfer or fuel queue is unfinished. Finish or end the run before making changes.");
                    DrawAdvanced(workflowLocked);
                }
                ImGui.EndChild();
            }
        }
        ImGui.EndChild();
    }

    private void DrawNavigation()
    {
        ImGui.Spacing();
        UiTone.Wrapped(TraderStyle.Gold, "A S H E ' S");
        var titlePosition = ImGui.GetCursorScreenPos();
        var titleSize = ImGui.GetFontSize() * 1.7f;
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), titleSize, titlePosition, ImGui.GetColorU32(TraderStyle.Text), "Trader");
        ImGui.Dummy(new Vector2(0, titleSize + TraderStyle.Px(10)));
        ImGui.TextDisabled("YOUR WORKSPACE");
        ImGui.Spacing();
        string[] labels = ["Connection", "Jobs", "Review & run", "Activity", "Fuel & houses", "Advanced", "Item Search"];
        string[] details = ["PCs & integrations", "Items & characters", "Ready to transfer", "Progress & diagnostics", "FC fuel queues", "Manual controls", "Across your inventories"];
        int[] order = [0, 1, 2, 3, 6, 4, 5];
        // The page list can scroll at small window sizes; run status stays reachable below it.
        if (ImGui.BeginChild("NavigationPages", new Vector2(0, Math.Max(TraderStyle.Px(130), ImGui.GetContentRegionAvail().Y - TraderStyle.Px(82)))))
        {
            foreach (var index in order)
            {
                if (TraderStyle.NavButton($"page{index}", labels[index], details[index], selectedPage == index, new Vector2(0, 52)))
                    selectedPage = index;
            }
        }
        ImGui.EndChild();
        var footerY = ImGui.GetWindowHeight() - TraderStyle.Px(82);
        if (ImGui.GetCursorPosY() < footerY) ImGui.SetCursorPosY(footerY);
        ImGui.Separator();
        var active = plugin.Workflow.Busy || plugin.Trader.IsRunning || plugin.Fuel.Active || plugin.FuelQueue.Active;
        var paused = plugin.Workflow.Run is { Paused: true, Step: not Workflow.CoordinatorStep.Finished }
            || plugin.FuelQueue.HasUnfinishedRun && !plugin.FuelQueue.Active;
        TraderStyle.Badge(active ? "RUNNING" : paused ? "PAUSED" : "IDLE", active ? TraderStyle.Accent : paused ? TraderStyle.Gold : TraderStyle.Muted);
        ImGui.TextDisabled($"Version {Version}");
    }

    private void DrawPageHeader()
    {
        if (ImGui.BeginTable("PageHeader", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(125));
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            TraderStyle.Section(PageTitles[selectedPage], PageDescriptions[selectedPage]);
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!plugin.Trader.IsRunning && !plugin.Fuel.Active && !plugin.Workflow.Busy && !plugin.FuelQueue.Active);
            if (TraderStyle.DangerButton("STOP ALL", new Vector2(-1, 34))) plugin.QueueAction(plugin.StopAll);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stop all actions on this PC and pause the paired workflow.");
            TraderStyle.Badge(plugin.Workflow.Connected ? "PCs connected" : "Not connected", plugin.Workflow.Connected ? TraderStyle.Accent : TraderStyle.Muted);
            ImGui.EndTable();
        }
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawAdvanced(bool workflowLocked)
    {
        if (TraderStyle.BeginCard("ManualTools"))
        {
            TraderStyle.Section("Manual trading", "Direct a single pair of characters using a saved item list.");
            ImGui.BeginDisabled(workflowLocked);
            if (ImGui.CollapsingHeader("Open manual trade lists")) DrawTradeLists();
            ImGui.EndDisabled();
        }
        TraderStyle.EndCard();
        if (TraderStyle.BeginCard("CharacterProfiles"))
        {
            TraderStyle.Section("Character profiles", "Import your roster and manage account labels.");
            ImGui.BeginDisabled(workflowLocked);
            if (ImGui.CollapsingHeader("Manage character profiles")) DrawCharacters();
            ImGui.EndDisabled();
        }
        TraderStyle.EndCard();
        if (TraderStyle.BeginCard("LocalPreferences"))
        {
            TraderStyle.Section("Preferences & local log");
            if (ImGui.CollapsingHeader("Open preferences and recent messages")) DrawActivity();
        }
        TraderStyle.EndCard();
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
