using System.Globalization;
using System.Numerics;
using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AsheTrader.Windows;

/// <summary>Setup and controls for the paired transfer workflow. Game actions are queued.</summary>
public sealed partial class WorkflowPanel(Plugin plugin)
{
    private Guid selectedJob;
    private int selectedItemIndex;
    private int page;
    private bool? coordinatorRole;
    private string itemSearch = string.Empty;
    private string providerSearch = string.Empty;
    private bool confirmDeleteJob;
    private bool revealKey;
    private string dependencies = "Dependency state has not been checked on this PC.";
    private string dependencySummary = "Required plugins have not been checked on this PC yet.";
    private string notice = string.Empty;
    private string? diagnosticsToCopy;
    private int diagnosticCopyPending;
    private List<(uint Id, string Name)>? itemCatalog;
    private readonly Dictionary<string, string> amountInputs = [];
    private WorkflowService Service => plugin.Workflow;
    private WorkflowSettings Settings => plugin.Config.Workflow;
    private bool HasUnfinishedJob => Service.Run is { Step: not CoordinatorStep.Finished };
    private bool LocalActionRunning => Service.Busy || plugin.Trader.IsRunning || plugin.Fuel.Active;
    private bool EditingLocked => LocalActionRunning || HasUnfinishedJob;
    private bool ReadyConnection => Service.Connected && Service.RemoteRoster != null;
    private bool CoordinatorRole => HasUnfinishedJob || (Service.PairingEnabled ? Service.IsHost : coordinatorRole ?? !Settings.AllowRemoteControl);
    private IEnumerable<PendingCommand> PendingStorage => Service.Run is { Paused: true, TradeOperationId: null } run
        ? run.Pending.Where(pending => StorageReconciliation.IsStorage(pending.Command)) : [];
    private bool NeedsStorageRecovery => PendingStorage.Any(pending => pending.Response is { Ambiguous: true });
    private bool NeedsTradeRecovery => Service.Run is { Paused: true, TradeOperationId: not null };
    private SavedTransferJob? SelectedJob => Settings.Jobs.FirstOrDefault(x => x.Definition.Id == selectedJob) ?? Settings.Jobs.FirstOrDefault();

    public int Draw(int pageIndex)
    {
        page = Math.Clamp(pageIndex, 0, 3);
        if (Interlocked.Exchange(ref diagnosticsToCopy, null) is { } report)
        {
            ImGui.SetClipboardText(report);
            notice = "Diagnostics copied for this PC. Include the report when reporting the issue; also copy the other PC's report if it was running the stuck operation.";
        }
        ImGui.PushID("AsheWorkflowPanel");
        if (LocalActionRunning || HasUnfinishedJob)
        {
            if (TraderStyle.BeginCard("RunControls"))
            {
                TraderStyle.Badge(Service.Run is { Paused: true } ? "PAUSED" : "IN PROGRESS",
                    Service.Run is { Paused: true } ? UiTone.Warning : TraderStyle.Accent);
                UiTone.Status(Service.Status);
                DrawRunControls();
            }
            TraderStyle.EndCard();
        }
        if (ImGui.BeginChild("WorkflowPage", new Vector2(0, 0)))
        {
            if (notice.Length > 0)
            {
                if (TraderStyle.BeginCard("Notice"))
                {
                    UiTone.Status(notice);
                    if (ImGui.SmallButton("Dismiss")) notice = string.Empty;
                }
                TraderStyle.EndCard();
            }
            if (!CoordinatorRole && page is 1 or 2)
            {
                TraderStyle.Section("Your other PC manages the jobs", "This PC follows the coordinator. Open Jobs on that PC to edit recipient assignments or start a transfer.");
                if (TraderStyle.PrimaryButton("View this PC's activity")) page = 3;
            }
            else
            switch (page)
            {
                case 0: DrawPairing(); break;
                case 1: DrawJobs(); break;
                case 2: DrawReview(); break;
                default:
                    TraderStyle.Section("Activity", "Live progress, completed deliveries and recovery details.");
                    DrawDiagnosticsControls();
                    DrawTradeRecovery(); DrawStorageRecovery();
                    if (TraderStyle.BeginCard("RunProgress")) DrawProgress();
                    TraderStyle.EndCard();
                    if (TraderStyle.BeginCard("RecentActivity")) DrawActivityLog();
                    TraderStyle.EndCard();
                    break;
            }
        }
        ImGui.EndChild();
        ImGui.PopID();
        return page;
    }

    private void DrawRunControls()
    {
        if (Service.Busy)
        {
            if (TraderStyle.SecondaryButton("Pause workflow"))
                plugin.QueueAction(() => Service.Pause("Paused by user."));
        }
        if (Service.Run is { Paused: true, Step: not CoordinatorStep.Finished })
        {
            var needsRecovery = NeedsStorageRecovery || NeedsTradeRecovery;
            ImGui.BeginDisabled(!Service.IsHost || !Service.Connected || LocalActionRunning);
            if (TraderStyle.PrimaryButton("Resume / tally inventories"))
                plugin.QueueAction(Service.Resume);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(LocalActionRunning || needsRecovery);
            if (TraderStyle.SecondaryButton("End paused job")) plugin.QueueAction(Service.EndPausedJob);
            ImGui.EndDisabled();
            ImGui.TextDisabled("Resume reconciles current inventory and continues the remaining work.");
        }
    }

    private void DrawPairing()
    {
        TraderStyle.Section("Your two PCs, connected", "Pair once. Build jobs on the coordinator and let both PCs work together.");
        var coordinator = CoordinatorRole;
        if (TraderStyle.BeginCard("ConnectionOverview"))
        {
            TraderStyle.Badge(ReadyConnection ? "CONNECTED" : Service.PairingEnabled ? "WAITING FOR PEER" : "NOT CONNECTED", ReadyConnection ? UiTone.Good : UiTone.Warning);
            UiTone.Status(Service.ConnectionStatus);
            if (ImGui.BeginTable("PeerOverview", 2, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.TextColored(TraderStyle.Gold, "THIS PC");
                ImGui.TextWrapped(Settings.PeerName);
                ImGui.TextWrapped($"{plugin.Config.Characters.Count} characters · {(coordinator ? "Coordinator" : "Worker")}");
                ImGui.TableNextColumn();
                ImGui.TextColored(TraderStyle.Gold, "PAIRED PC");
                ImGui.TextWrapped(Service.RemoteRoster?.Name ?? "Waiting for connection");
                ImGui.TextWrapped(Service.RemoteRoster is { } remote ? $"{remote.Characters.Count} characters · Control {(remote.ControlEnabled ? "enabled" : "disabled")}" : "Connect with the same pairing key");
                ImGui.EndTable();
            }
            if (ReadyConnection)
            {
                if (coordinator && TraderStyle.PrimaryButton("Set up recipients & items")) page = 1;
                if (!coordinator && TraderStyle.PrimaryButton("View activity")) page = 3;
                ImGui.SameLine();
                ImGui.BeginDisabled(LocalActionRunning);
                if (TraderStyle.SecondaryButton("Refresh characters")) plugin.QueueAction(Service.RefreshRosters);
                ImGui.EndDisabled();
            }
            if (!coordinator)
            {
                var allow = Settings.AllowRemoteControl;
                if (ImGui.Checkbox("Allow the coordinator to run jobs on this PC", ref allow))
                {
                    Settings.AllowRemoteControl = allow; plugin.Save();
                    var enabled = allow;
                    plugin.QueueAction(() =>
                    {
                        if (!enabled) Service.Pause("Paired workflow control was disabled on this PC.");
                        Service.RefreshRosters();
                    });
                }
                UiTone.Wrapped(Settings.AllowRemoteControl ? UiTone.Good : UiTone.Warning, Settings.AllowRemoteControl
                    ? "Ready to receive character changes, travel and trades from the coordinator."
                    : "Enable control here before starting a job on your other PC.");
            }
        }
        TraderStyle.EndCard();
        if (TraderStyle.BeginCard("PairingSettings"))
        {
            if (ImGui.CollapsingHeader(ReadyConnection ? "Pairing & connection settings" : "Connect this PC", ReadyConnection ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.BeginDisabled(Service.PairingEnabled || EditingLocked);
                if (ImGui.RadioButton("Coordinator · I create jobs here", coordinator)) coordinatorRole = true;
                if (ImGui.RadioButton("Worker · follow my other PC", !coordinator)) coordinatorRole = false;
                ImGui.EndDisabled();
                coordinator = CoordinatorRole;
                if (HasUnfinishedJob) UiTone.Wrapped(UiTone.Warning, "A saved coordinator job is unfinished. Reconnect as coordinator to resume it.");
                ImGui.Separator();
                var changed = false;
                ImGui.BeginDisabled(Service.PairingEnabled || LocalActionRunning);
                var name = Settings.PeerName;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##PeerName", "Name this PC", ref name, 80)) { Settings.PeerName = name; changed = true; }
                ImGui.BeginDisabled(HasUnfinishedJob);
                var key = Settings.PairingKey;
                ImGui.TextUnformatted(coordinator ? "Shared pairing key" : "Pairing key from the coordinator");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##PairingKey", ref key, 128, revealKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password))
                { Settings.PairingKey = key.Trim(); changed = true; }
                if (coordinator && TraderStyle.SecondaryButton(Settings.PairingKey.Length == 0 ? "Create pairing key" : "Replace pairing key"))
                { Settings.PairingKey = WorkflowService.NewPairingKey(); changed = true; }
                ImGui.EndDisabled(); ImGui.EndDisabled();
                if (coordinator)
                {
                    ImGui.BeginDisabled(Settings.PairingKey.Length == 0);
                    if (TraderStyle.SecondaryButton("Copy key for the other PC"))
                    { ImGui.SetClipboardText(Settings.PairingKey); notice = "Pairing key copied. Enter it once on your other PC; both PCs remember it."; }
                    ImGui.EndDisabled();
                }
                ImGui.Checkbox("Show pairing key", ref revealKey);
                if (changed) plugin.Save();
                if (!Service.PairingEnabled && coordinator)
                {
                    ImGui.BeginDisabled(LocalActionRunning);
                    if (TraderStyle.PrimaryButton("Host for my other PC", new Vector2(-1, 0))) plugin.QueueAction(Service.Host);
                    ImGui.EndDisabled();
                    ImGui.TextWrapped("Hosting creates a key if needed. Copy it to your other PC, then use Find my coordinator there.");
                }
                if (!Service.PairingEnabled && !coordinator)
                {
                    ImGui.BeginDisabled(LocalActionRunning || HasUnfinishedJob || Service.Discovering);
                    if (TraderStyle.PrimaryButton("Find my coordinator", new Vector2(-1, 0))) plugin.QueueAction(Service.StartDiscovery);
                    ImGui.EndDisabled();
                    if (Service.Discovering && TraderStyle.SecondaryButton("Stop search")) plugin.QueueAction(Service.StopDiscovery);
                    UiTone.Status(Service.DiscoveryStatus);
                    foreach (var candidate in Service.NearbyPeers)
                    {
                        ImGui.PushID($"{candidate.PeerId}/{candidate.Address}:{candidate.Port}");
                        var remembered = Settings.ExpectedRemotePeerId == candidate.PeerId;
                        var otherIdentity = Settings.ExpectedRemotePeerId.HasValue && !remembered;
                        ImGui.TextWrapped(candidate.Name + (remembered ? " · remembered PC" : otherIdentity ? " · different PC" : " · pairing key required"));
                        ImGui.BeginDisabled(LocalActionRunning || HasUnfinishedJob || Settings.PairingKey.Length == 0 || otherIdentity);
                        if (TraderStyle.SecondaryButton("Connect to this PC"))
                        {
                            var selected = candidate;
                            plugin.QueueAction(() => Service.ConnectDiscovered(selected));
                        }
                        ImGui.EndDisabled(); ImGui.PopID();
                    }
                }
                if (Service.PairingEnabled && TraderStyle.SecondaryButton("Disconnect PCs")) plugin.QueueAction(Service.Disconnect);
                ImGui.TextWrapped("Automatic discovery works on the same local network. Keep the coordinator hosting while the other PC connects.");
            }
        }
        TraderStyle.EndCard();
        if (TraderStyle.BeginCard("Readiness"))
        {
            TraderStyle.Section("Characters & integrations", "Characters come from AutoRetainer on each PC. Refresh after adding an alt.");
            UiTone.Status(Service.RosterStatus);
            ImGui.BeginDisabled(LocalActionRunning);
            if (TraderStyle.SecondaryButton("Refresh both rosters")) plugin.QueueAction(Service.RefreshRosters);
            ImGui.EndDisabled();
            if (ImGui.CollapsingHeader("Known characters"))
                foreach (var entry in Service.AllCharacters()) ImGui.TextWrapped($"{CharacterName(entry.Character)} · {PeerName(entry.PeerId)}");
            if (TraderStyle.SecondaryButton("Check required plugins")) plugin.QueueAction(CheckDependencies);
            UiTone.Status(dependencySummary);
            ImGui.TextWrapped("Check each PC. Keep AutoRetainer enabled, pause its multi mode, and let pending tasks finish.");
        }
        TraderStyle.EndCard();
        if (ImGui.CollapsingHeader("Advanced connection settings")) DrawConnectionAdvanced(coordinator);
        if (ImGui.CollapsingHeader("Retainer destinations on this PC")) DrawStorage();
    }
    private void DrawConnectionAdvanced(bool coordinator)
    {
        var manualChanged = false;
        ImGui.BeginDisabled(Service.PairingEnabled || LocalActionRunning);
        if (coordinator)
        {
            var addresses = Service.LocalAddresses;
            var selected = addresses.FirstOrDefault(x => x.Address.ToString() == Settings.BindAddress);
            if (ImGui.BeginCombo("Network adapter", selected?.Label ?? "Automatic"))
            {
                if (ImGui.Selectable("Automatic", string.IsNullOrWhiteSpace(Settings.BindAddress))) { Settings.BindAddress = ""; manualChanged = true; }
                foreach (var address in addresses)
                    if (ImGui.Selectable($"{address.Label} — {address.Address}##{address.Address}", address.Address.ToString() == Settings.BindAddress))
                    { Settings.BindAddress = address.Address.ToString(); manualChanged = true; }
                ImGui.EndCombo();
            }
            var bind = Settings.BindAddress;
            if (ImGui.InputText("Coordinator LAN address", ref bind, 64)) { Settings.BindAddress = bind.Trim(); manualChanged = true; }
        }
        else
        {
            var connect = Settings.ConnectAddress;
            if (ImGui.InputText("Coordinator LAN address", ref connect, 64)) { Settings.ConnectAddress = connect.Trim(); manualChanged = true; }
        }
        var port = Settings.Port;
        if (ImGui.InputInt("Port", ref port)) { Settings.Port = Math.Clamp(port, 1, ushort.MaxValue); manualChanged = true; }
        ImGui.EndDisabled();
        if (manualChanged) plugin.Save();
        if (!coordinator)
        {
            ImGui.BeginDisabled(Service.PairingEnabled || LocalActionRunning || HasUnfinishedJob || Settings.PairingKey.Length == 0 || string.IsNullOrWhiteSpace(Settings.ConnectAddress));
            if (ImGui.Button("Connect using manual address")) plugin.QueueAction(Service.Connect);
            ImGui.EndDisabled();
        }
        if (Settings.ExpectedRemotePeerId.HasValue)
        {
            ImGui.TextWrapped("Your paired PC's identity is remembered. Disconnect both PCs before replacing it.");
            ImGui.BeginDisabled(Service.PairingEnabled || EditingLocked);
            if (ImGui.Button("Forget paired PC"))
            {
                Settings.ExpectedRemotePeerId = null;
                plugin.Save();
                notice = "Paired identity cleared. Generate a new key when replacing a PC.";
            }
            ImGui.EndDisabled();
        }
        ImGui.Separator();
        ImGui.TextWrapped("Both PCs need AutoRetainer for character changes, Lifestream for travel and inn access, and vnavmesh for walking. Pause AutoRetainer's multi mode before running a job.");
        if (ImGui.Button("Check plugins on this PC")) plugin.QueueAction(CheckDependencies);
        UiTone.Status(dependencies);
        ImGui.TextWrapped("This check covers this PC only. Check plugins on the other PC as well.");
    }
    private void DrawJobs()
    {
        TraderStyle.Section("Build your delivery plan", "Choose a recipient, then assign only the items that character should receive.");
        var job = SelectedJob;
        if (job != null) selectedJob = job.Definition.Id;
        ImGui.SetNextItemWidth(Math.Max(TraderStyle.Px(150), ImGui.GetContentRegionAvail().X - TraderStyle.Px(150)));
        if (ImGui.BeginCombo("##SavedJob", job?.Definition.Name ?? "Choose a saved job"))
        {
            foreach (var candidate in Settings.Jobs)
                if (ImGui.Selectable($"{candidate.Definition.Name}##{candidate.Definition.Id}", candidate.Definition.Id == selectedJob))
                {
                    selectedJob = candidate.Definition.Id; selectedItemIndex = 0; itemSearch = "";
                    confirmDeleteJob = false; job = candidate;
                }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(EditingLocked);
        if (TraderStyle.SecondaryButton("+ New job", new Vector2(130, 0)))
        {
            job = new SavedTransferJob { Definition = new TransferJobDefinition { Name = $"Transfer job {Settings.Jobs.Count + 1}", QuantityMode = JobQuantityMode.DeliverAmount } };
            Settings.Jobs.Add(job); selectedJob = job.Definition.Id; selectedItemIndex = 0;
            itemSearch = ""; confirmDeleteJob = false; plugin.Save();
        }
        ImGui.EndDisabled();
        ImGui.Spacing();
        if (EditingLocked) UiTone.Wrapped(UiTone.Warning, "This setup is read-only while a job is unfinished. Resume it from Activity, or end it before editing.");
        if (job == null)
        {
            if (TraderStyle.BeginCard("NoJobs"))
            {
                TraderStyle.Section("A home for every item", "Create your first job to send Coke and Fuel to one alt, Fuel to another, and Hardened Sap to a third.");
                ImGui.TextWrapped("Your PCs handle providers, relays and retainer storage automatically. You decide who gets what.");
            }
            TraderStyle.EndCard();
            return;
        }
        ImGui.BeginDisabled(EditingLocked);
        DrawRecipientSetup(job);
        var changed = false;
        if (TraderStyle.BeginCard("SourcesAndOrder"))
        {
            TraderStyle.Section("Providers & item priority", "Characters collect their assigned items together. Each item can use a different set of providers.");
            var lines = job.ItemDefinitions.ToArray();
            selectedItemIndex = Math.Clamp(selectedItemIndex, 0, lines.Length - 1);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##ProviderItem", $"{selectedItemIndex + 1}. {ItemLabel(lines[selectedItemIndex])}"))
            {
                for (var index = 0; index < lines.Length; index++)
                    if (ImGui.Selectable($"{index + 1}. {ItemLabel(lines[index])}##{lines[index].Id}", index == selectedItemIndex))
                        selectedItemIndex = index;
                ImGui.EndCombo();
            }
            var definition = lines[selectedItemIndex];
            ImGui.PushID(definition.Id.ToString());
            ImGui.TextWrapped($"{definition.Sources.Count} providers selected for {ItemLabel(definition)}");
            if (ImGui.CollapsingHeader("Choose providers", definition.Sources.Count == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                DrawProviderSelection(definition, ref changed);
            if (ImGui.CollapsingHeader("Item priority when space is limited"))
            {
                for (var index = 0; index < lines.Length; index++)
                {
                    ImGui.PushID($"order{lines[index].Id}");
                    ImGui.BeginDisabled(index == 0);
                    var moveUp = ImGui.SmallButton("Earlier");
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.BeginDisabled(index == lines.Length - 1);
                    var moveDown = ImGui.SmallButton("Later");
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.TextWrapped($"{index + 1}. {ItemLabel(lines[index])}");
                    ImGui.PopID();
                    if (!moveUp && !moveDown) continue;
                    RecipientAssignments.MoveItem(job, lines[index].Id, moveUp ? -1 : 1);
                    selectedJob = job.Definition.Id;
                    selectedItemIndex = Array.IndexOf(job.ItemDefinitions.ToArray(), definition);
                    changed = true;
                    break;
                }
            }
            ImGui.PopID();
        }
        TraderStyle.EndCard();
        if (TraderStyle.BeginCard("MeetingPoint"))
        {
            TraderStyle.Section("Meeting point", "One outdoor location. The best world is chosen automatically for each delivery.");
            if (job.Meeting.TerritoryId != 0) DrawLocation(job.Meeting, worldIndependent: true);
            else UiTone.Wrapped(UiTone.Warning, "Record a meeting point before starting your first transfer.");
            if (TraderStyle.SecondaryButton(job.Meeting.TerritoryId == 0 ? "Record my current location" : "Replace with my current location"))
            {
                var jobId = job.Definition.Id;
                plugin.QueueAction(() => CaptureMeeting(jobId));
            }
            if (job.Meeting.TerritoryId != 0 && ImGui.CollapsingHeader("Arrival aetheryte"))
            {
                var choices = Plugin.DataManager.GetExcelSheet<Aetheryte>().Where(x => x.IsAetheryte && x.Territory.RowId == job.Meeting.TerritoryId).ToArray();
                if (ImGui.BeginCombo("##ArrivalAetheryte", AetheryteName(job.Meeting.AetheryteId)))
                {
                    foreach (var aetheryte in choices)
                        if (ImGui.Selectable(AetheryteName(aetheryte.RowId), job.Meeting.AetheryteId == aetheryte.RowId))
                        { job.Meeting.AetheryteId = aetheryte.RowId; changed = true; }
                    ImGui.EndCombo();
                }
                if (choices.Length == 0) UiTone.Wrapped(UiTone.Warning, "Choose an outdoor territory with a main aetheryte.");
            }
        }
        TraderStyle.EndCard();
        if (ImGui.CollapsingHeader("Job settings"))
        {
            var name = job.Definition.Name;
            if (ImGui.InputText("Job name", ref name, 100)) { job.Definition.Name = name; changed = true; }
            if (!confirmDeleteJob)
            {
                if (TraderStyle.DangerButton("Delete this saved setup")) confirmDeleteJob = true;
            }
            else
            {
                UiTone.Wrapped(UiTone.Warning, "Delete this setup? Completed transfer history is kept separately.");
                if (TraderStyle.DangerButton("Delete setup"))
                {
                    Settings.Jobs.Remove(job); selectedJob = Guid.Empty; selectedItemIndex = 0;
                    confirmDeleteJob = false; plugin.Save();
                }
                ImGui.SameLine();
                if (TraderStyle.SecondaryButton("Keep setup")) confirmDeleteJob = false;
            }
        }
        ImGui.EndDisabled();
        if (changed) plugin.Save();
        if (ImGui.CollapsingHeader("Retainer destinations on this PC")) DrawStorage();
        if (ImGui.CollapsingHeader("How automatic relays work")) DrawAutomaticRelayInfo();
        ImGui.Spacing();
        if (TraderStyle.PrimaryButton("Review delivery plan", new Vector2(-1, 42))) page = 2;
    }

    private void DrawProviderSelection(TransferJobDefinition definition, ref bool changed)
    {
        var roster = Service.AllCharacters().ToArray();
        if (ImGui.SmallButton("All known characters"))
        { definition.Sources = roster.Select(x => x.Character.ContentId).Distinct().ToList(); changed = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear providers")) { definition.Sources.Clear(); changed = true; }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ProviderSearch", "Find a provider by character, world or PC...", ref providerSearch, 100);
        if (ImGui.BeginChild("ProviderChoices", new Vector2(0, TraderStyle.Px(230)), true))
        {
            foreach (var group in roster.GroupBy(x => x.PeerId))
            {
                ImGui.TextColored(TraderStyle.Gold, PeerName(group.Key));
                foreach (var entry in group)
                {
                    var label = CharacterName(entry.Character);
                    if (providerSearch.Length > 0 && !(label + " " + PeerName(entry.PeerId)).Contains(providerSearch, StringComparison.OrdinalIgnoreCase)) continue;
                    var selected = definition.Sources.Contains(entry.Character.ContentId);
                    if (ImGui.Checkbox($"{label}##source{entry.PeerId}{entry.Character.ContentId}", ref selected))
                    {
                        if (selected) definition.Sources.Add(entry.Character.ContentId); else definition.Sources.Remove(entry.Character.ContentId);
                        changed = true;
                    }
                }
            }
            foreach (var missing in definition.Sources.Where(id => roster.All(x => x.Character.ContentId != id)).ToArray())
            {
                UiTone.Wrapped(UiTone.Warning, $"{CharacterName(missing)} · not in the current roster");
                if (ImGui.SmallButton($"Remove##missing{missing}")) { definition.Sources.Remove(missing); changed = true; }
            }
            if (roster.Length == 0) UiTone.Wrapped(UiTone.Warning, "Connect both PCs and refresh characters on the Connection page.");
        }
        ImGui.EndChild();
        ImGui.TextWrapped("Carried stock is used first, then retainers. Planning reads Allagan Tools without logging into every character.");
    }
    private void DrawAutomaticRelayInfo()
    {
        UiTone.Wrapped(UiTone.Info, "Automatic relays: no selection needed.");
        ImGui.TextWrapped("Same-account transfers use an alt on the other PC. Ashe's Trader estimates how many relay loads are needed from AutoRetainer's free slots, favors a useful home world, then checks the selected relay's capacity. Providers fill the relay before it delivers to the recipient.");
        if (ImGui.CollapsingHeader("View AutoRetainer relay candidates"))
        {
            foreach (var group in Service.AllCharacters().GroupBy(x => x.PeerId))
            {
                ImGui.TextDisabled(PeerName(group.Key));
                foreach (var entry in group.OrderByDescending(x => x.Character.InventoryFreeSlots.HasValue)
                             .ThenByDescending(x => x.Character.InventoryFreeSlots).ThenBy(x => x.Character.ContentId))
                {
                    var free = entry.Character.InventoryFreeSlots;
                    UiTone.Wrapped(free is > 0 ? UiTone.Info : UiTone.Warning,
                        $"{CharacterName(entry.Character)}: {(free.HasValue ? $"{free:N0} cached free slots" : "AutoRetainer slot count unknown")}");
                }
            }
            ImGui.TextWrapped("Cached slot counts can be stale. Characters with unknown counts are not selected automatically; refresh characters on the Connection page after AutoRetainer updates an alt. Actual capacity is checked before cargo moves.");
        }
    }

    private static string ItemLabel(TransferJobDefinition definition) => definition.ItemId == 0
        ? "Choose an item" : ItemName(definition.ItemId) + (definition.HighQuality ? " HQ" : "");

    private void DrawReview()
    {
        TraderStyle.Section("Review & run", "See exactly who receives each item before the PCs start moving.");
        var job = SelectedJob;
        if (job == null)
        {
            UiTone.Wrapped(TraderStyle.Muted, "Your delivery plan will appear here after you create a job.");
            if (TraderStyle.PrimaryButton("Create a transfer job")) page = 1;
            return;
        }
        var definitions = job.ItemDefinitions.ToArray();
        if (TraderStyle.BeginCard("DeliverySummary"))
        {
            TraderStyle.Section(job.Definition.Name,
                $"{definitions.SelectMany(x => x.Recipients).Select(x => x.CharacterId).Distinct().Count()} recipients  /  {definitions.Length} items  /  {definitions.SelectMany(x => x.Sources).Distinct().Count()} providers");
            DrawAssignmentSummary(definitions);
            if (TraderStyle.SecondaryButton("Edit recipients & items")) page = 1;
        }
        TraderStyle.EndCard();

        var scanBlockers = GetScanBlockers(job);
        var transferBlockers = GetTransferBlockers(job);
        var ready = scanBlockers.Count == 0 && transferBlockers.Count == 0;
        if (TraderStyle.BeginCard("LaunchChecklist"))
        {
            TraderStyle.Badge(ready ? "READY TO START" : "SETUP INCOMPLETE", ready ? UiTone.Good : UiTone.Warning);
            if (!ready)
                foreach (var reason in scanBlockers.Concat(transferBlockers).Distinct()) UiTone.Wrapped(UiTone.Warning, reason);
            else
                ImGui.TextWrapped("Both PCs are connected and this plan is ready. You can pause at any time.");
            ImGui.BeginDisabled(!ready);
            if (TraderStyle.PrimaryButton("Start transfer", new Vector2(-1, 44)))
            {
                plugin.QueueAction(() => Service.StartJob(job));
                page = 3;
            }
            ImGui.EndDisabled();
            ImGui.BeginDisabled(scanBlockers.Count > 0);
            if (TraderStyle.SecondaryButton("Check cached inventories only", new Vector2(-1, 0)))
            {
                plugin.QueueAction(() => Service.ScanJob(job));
                page = 3;
            }
            ImGui.EndDisabled();
            ImGui.TextWrapped("Inventory checks use Allagan Tools and never change characters. Missing data is reported as skipped; the available work continues.");
            if (ImGui.CollapsingHeader("Plugin readiness"))
            {
                if (TraderStyle.SecondaryButton("Check plugins on this PC")) plugin.QueueAction(CheckDependencies);
                UiTone.Status(dependencySummary);
                ImGui.TextWrapped("On both PCs, keep required plugins enabled, pause AutoRetainer multi mode and let its tasks finish. Close existing trade and retainer menus.");
            }
        }
        TraderStyle.EndCard();
        if (TraderStyle.BeginCard("TravelSummary"))
        {
            TraderStyle.Section("Travel & storage", "Providers stay on their home worlds. Recipients and relays collect from them.");
            DrawLocation(job.Meeting, worldIndependent: true);
            ImGui.TextWrapped("Relay deliveries take place on the final recipient's home world, keeping repeated deposits local. Full relays deliver their load before collecting more.");
            if (ImGui.CollapsingHeader("Item & recipient priorities"))
            {
                ImGui.TextWrapped("Items share character visits and trade windows. This item order breaks ties when space is limited. Within each item, recipients fill in priority order; the remainder recipient takes stock left after earlier caps.");
                for (var itemIndex = 0; itemIndex < definitions.Length; itemIndex++)
                {
                    var definition = definitions[itemIndex];
                    ImGui.PushID(definition.Id.ToString());
                    ImGui.TextColored(TraderStyle.Gold, $"{itemIndex + 1}. {ItemLabel(definition)} · {ModeName(definition.QuantityMode)}");
                    foreach (var recipient in definition.Recipients.Select((value, index) => (value, index)))
                        ImGui.TextWrapped($"{recipient.index + 1}. {CharacterName(recipient.value.CharacterId)} · {AssignmentAmount(definition, recipient.value)}");
                    if (ImGui.CollapsingHeader($"{definition.Sources.Count} providers"))
                        foreach (var id in definition.Sources) ImGui.TextWrapped(CharacterName(id));
                    ImGui.PopID();
                }
            }
            if (ImGui.CollapsingHeader("How the route is chosen"))
            {
                DrawAutomaticRelayInfo();
                ImGui.TextWrapped("Compatible item assignments share a character visit. Routes favor the current pair, then group destinations by DC and world. Recipient priority and full inventories may still require another visit. Enable DC travel in Lifestream on both PCs when needed.");
            }
        }
        TraderStyle.EndCard();
        if (ImGui.CollapsingHeader("Cached inventory preview"))
        {
            selectedItemIndex = Math.Clamp(selectedItemIndex, 0, definitions.Length - 1);
            if (ImGui.BeginCombo("Preview item", ItemLabel(definitions[selectedItemIndex])))
            {
                for (var index = 0; index < definitions.Length; index++)
                    if (ImGui.Selectable($"{index + 1}. {ItemLabel(definitions[index])}", selectedItemIndex == index)) selectedItemIndex = index;
                ImGui.EndCombo();
            }
            var definition = definitions[selectedItemIndex];
            ImGui.BeginDisabled(LocalActionRunning);
            if (TraderStyle.SecondaryButton("Read Allagan Tools hints"))
            {
                var item = definition.ItemId;
                var highQuality = definition.HighQuality;
                plugin.QueueAction(() => Service.RefreshInventoryHints(item, highQuality));
            }
            ImGui.EndDisabled();
            DrawHints();
        }
    }

    private void DrawAssignmentSummary(IReadOnlyList<TransferJobDefinition> definitions)
    {
        var recipients = definitions.SelectMany(x => x.Recipients).Select(x => x.CharacterId).Distinct().ToArray();
        if (recipients.Length == 0)
        {
            UiTone.Wrapped(UiTone.Warning, "No recipient assignments yet. Add a character and their items on the Jobs page.");
            return;
        }
        if (!ImGui.BeginTable("DeliveryAssignments", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Recipient", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Receives", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(68));
        ImGui.TableHeadersRow();
        foreach (var characterId in recipients)
        {
            var first = true;
            foreach (var definition in definitions)
            {
                var rule = definition.Recipients.FirstOrDefault(x => x.CharacterId == characterId);
                if (rule == null) continue;
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (first) UiTone.Wrapped(TraderStyle.Text, CharacterName(characterId));
                first = false;
                ImGui.TableNextColumn(); UiTone.Wrapped(TraderStyle.Accent, ItemLabel(definition));
                ImGui.TableNextColumn(); ImGui.TextWrapped(AssignmentAmount(definition, rule));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{definition.Recipients.IndexOf(rule) + 1} / {definition.Recipients.Count}");
            }
        }
        ImGui.EndTable();
        ImGui.TextWrapped("Priority is per item. Only the assignments shown above receive that item.");
    }

    private static string AssignmentAmount(TransferJobDefinition definition, RecipientRule rule) => definition.QuantityMode switch
    {
        JobQuantityMode.RestockToTotal => $"Stock to {rule.Quantity:N0}",
        JobQuantityMode.DeliverAmount => $"Send {rule.Quantity:N0}",
        _ => rule.Quantity.HasValue ? $"Up to {rule.Quantity:N0}" : "All remaining"
    };
    private List<string> GetScanBlockers(SavedTransferJob job)
    {
        var reasons = new List<string>();
        if (LocalActionRunning) reasons.Add("Pause the current action before starting another job.");
        if (HasUnfinishedJob) reasons.Add("Resume the saved job, or resolve and end it before starting a new one.");
        if (!Service.IsHost || !Service.Connected || Service.RemoteRoster == null) reasons.Add("Connect both PCs with this PC hosting on the Connection page.");
        if (Service.RemoteRoster is not { ControlEnabled: true }) reasons.Add("Allow coordinator control on the other PC on the Connection page.");
        var definitions = job.ItemDefinitions.ToArray();
        if (definitions.Any(x => x.Id == Guid.Empty) || definitions.Select(x => x.Id).Distinct().Count() != definitions.Length)
            reasons.Add("This saved setup has missing or repeated item identities. Recreate its item lines in a new job before running.");
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            var label = $"Item {index + 1} ({ItemLabel(definition)})";
            if (definition.Sources.Count == 0) reasons.Add($"{label}: select at least one provider on the Jobs page.");
            if (definition.Recipients.Count == 0) reasons.Add($"{label}: add at least one recipient on the Jobs page.");
            if (HasInvalidAmounts(definition)) reasons.Add($"{label}: fix the highlighted quantity fields; use positive whole numbers.");
            var item = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(definition.ItemId);
            if (item == null || definition.ItemId == 0 || item.Value.IsUntradable) reasons.Add($"{label}: choose a tradable item on the Jobs page.");
            else if (definition.HighQuality && !item.Value.CanBeHq) reasons.Add($"{label}: this item has no HQ version. Turn off high quality.");
            if (definition.Sources.Count > 0 && definition.Recipients.Count > 0 && !HasInvalidAmounts(definition))
            {
                try
                {
                    // New runs select their relays automatically. Legacy template nominations are not requirements.
                    JobPlanner.ValidateDefinition(new TransferJobDefinition
                    {
                        Id = definition.Id, ItemId = definition.ItemId, HighQuality = definition.HighQuality,
                        QuantityMode = definition.QuantityMode, Sources = definition.Sources, Recipients = definition.Recipients
                    });
                }
                catch (ArgumentException exception) { reasons.Add($"{label}: {exception.Message.Split('\n')[0]}"); }
            }
        }
        if (definitions.GroupBy(x => new ItemKey(x.ItemId, x.HighQuality)).Any(group => group.Key.ItemId != 0 && group.Count() > 1))
            reasons.Add("The same item and quality appear more than once. Keep each combination on one item line.");
        return reasons;
    }

    private static List<string> GetTransferBlockers(SavedTransferJob job)
    {
        var reasons = new List<string>();
        if (job.Meeting.TerritoryId == 0 || job.Meeting.AetheryteId == 0)
            reasons.Add("A full transfer needs a recorded meeting point and arrival aetheryte on the Jobs page. You can still scan inventories.");
        return reasons;
    }

    private static void ReadinessLine(bool ready, string yes, string no) =>
        UiTone.Wrapped(ready ? UiTone.Good : UiTone.Warning, ready ? $"Ready: {yes}" : $"To do: {no}");
    private void DrawItemPicker(TransferJobDefinition definition, ref bool changed)
    {
        UiTone.Wrapped(definition.ItemId == 0 ? UiTone.Warning : UiTone.Info, $"Editing item {selectedItemIndex + 1}: {ItemLabel(definition)}");
        ImGui.InputText("Find item", ref itemSearch, 100);
        if (itemSearch.Length >= 2)
        {
            itemCatalog ??= Plugin.DataManager.GetExcelSheet<Item>().Where(x => x.RowId > 19 && !x.IsUntradable && !x.Name.IsEmpty)
                .Select(x => (x.RowId, x.Name.ToString())).ToList();
            if (ImGui.BeginCombo("Matching items", "Choose an item"))
            {
                foreach (var candidate in itemCatalog.Where(x => x.Name.Contains(itemSearch, StringComparison.OrdinalIgnoreCase)).Take(100))
                    if (ImGui.Selectable($"{candidate.Name}##{candidate.Id}", candidate.Id == definition.ItemId))
                    { definition.ItemId = candidate.Id; changed = true; }
                ImGui.EndCombo();
            }
        }
        var supportsHq = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(definition.ItemId)?.CanBeHq == true;
        if (supportsHq || definition.HighQuality)
        {
            var hq = definition.HighQuality;
            if (ImGui.Checkbox("High quality only", ref hq)) { definition.HighQuality = hq; changed = true; }
            if (!supportsHq) UiTone.Wrapped(UiTone.Error, "This item has no high-quality version. Turn off the saved HQ selection to continue.");
        }
    }

    private void DrawStorage()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Configure these settings separately on each PC. With no saved bell location, the worker returns home and uses Lifestream's inn shortcut. A saved location must be near a reachable outdoor summoning bell on that character's home world.");
        ImGui.BeginDisabled(EditingLocked);
        foreach (var character in plugin.Config.Characters)
        {
            ImGui.PushID($"storage{character.ContentId}");
            if (ImGui.CollapsingHeader(CharacterName(character)))
            {
                var route = Settings.StorageRoutes.GetValueOrDefault(character.ContentId);
                if (route == null) ImGui.TextUnformatted("Bell destination: home-world inn via Lifestream");
                else DrawLocation(route);
                ImGui.BeginDisabled(Plugin.PlayerState.ContentId != character.ContentId);
                if (ImGui.Button("Record bell location here"))
                {
                    var id = character.ContentId;
                    plugin.QueueAction(() => CaptureStorage(id));
                }
                ImGui.EndDisabled();
                if (route != null)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Use inn instead")) { Settings.StorageRoutes.Remove(character.ContentId); plugin.Save(); }
                }
                var retainers = Service.GetKnownRetainers(character.ContentId);
                var allowed = Settings.AllowedRetainers.GetValueOrDefault(character.ContentId) ?? [];
                var useAll = allowed.Length == 0;
                ImGui.BeginDisabled(retainers.Count == 0 && useAll);
                if (ImGui.Checkbox("Allow deposits into all retainers", ref useAll))
                {
                    Settings.AllowedRetainers[character.ContentId] = useAll ? [] : retainers.Select(x => x.RetainerId).ToArray();
                    plugin.Save();
                    allowed = Settings.AllowedRetainers[character.ContentId];
                }
                ImGui.EndDisabled();
                if (retainers.Count == 0) ImGui.TextWrapped("Retainer choices appear after an inventory lookup or live scan. All retainers are permitted by default.");
                if (!useAll)
                {
                    foreach (var retainer in retainers)
                    {
                        var enabled = allowed.Contains(retainer.RetainerId);
                        if (ImGui.Checkbox($"{retainer.Name}##{retainer.RetainerId}", ref enabled))
                        {
                            var next = allowed.ToHashSet();
                            if (enabled) next.Add(retainer.RetainerId); else next.Remove(retainer.RetainerId);
                            if (next.Count == 0) notice = "Keep at least one permitted retainer, or enable all retainers.";
                            else { allowed = next.ToArray(); Settings.AllowedRetainers[character.ContentId] = allowed; plugin.Save(); }
                        }
                    }
                    foreach (var unknown in allowed.Where(id => retainers.All(x => x.RetainerId != id)))
                        ImGui.TextWrapped($"Saved retainer {unknown} has not been rediscovered yet.");
                }
            }
            ImGui.PopID();
        }
        ImGui.EndDisabled();
    }

    private void DrawTradeRecovery()
    {
        if (Service.Run is not { Paused: true, TradeOperationId: not null } run) return;
        var pending = run.Pending.Where(x => x.Command.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch).ToArray();
        UiTone.Wrapped(UiTone.Info, "Interrupted trade — inventory tally");
        ImGui.TextWrapped("Resume reads both original characters' current item counts and reconciles this batch automatically. A matching transfer is credited once; unchanged counts allow another attempt. Conflicting counts stay paused with an explanation.");
        foreach (var kind in new[] { WorkerCommandKind.SendBatch, WorkerCommandKind.ReceiveBatch })
        {
            var side = kind == WorkerCommandKind.SendBatch ? "Sender" : "Receiver";
            var command = pending.FirstOrDefault(x => x.Command.Kind == kind);
            if (command == null)
            {
                UiTone.Wrapped(UiTone.Info, $"{side}: retrieving the saved checkpoint on resume.");
                continue;
            }
            var operation = command.Command.Operation;
            var response = command.Response;
            ImGui.Spacing();
            UiTone.Wrapped(UiTone.Info, $"{side}: {CharacterName(operation.CharacterId)} on {PeerName(command.PeerId)}");
            ImGui.TextWrapped($"{ItemName(operation.ItemId)}{(operation.HighQuality ? " HQ" : "")} · expected {(kind == WorkerCommandKind.SendBatch ? "sent" : "received")}: {operation.Quantity:N0}");
            ImGui.TextWrapped($"Bag count before: {response?.BeforeQuantity?.ToString("N0") ?? "unavailable"} · last observed: {response?.CurrentQuantity?.ToString("N0") ?? "unavailable"}");
            var state = response == null ? "Waiting for this PC's inventory tally."
                : response.Success ? "Transfer recorded."
                : response.NeverStarted ? "Command did not start."
                : response.BeforeQuantity.HasValue && response.CurrentQuantity.HasValue
                    ? response.BeforeQuantity == response.CurrentQuantity ? "Observed count is unchanged." : "Observed count has changed."
                    : "Current inventory evidence is needed.";
            UiTone.Wrapped(response?.Success == true ? UiTone.Good : UiTone.Info, state);
        }
        if (!Service.IsHost || !Service.Connected)
            UiTone.Wrapped(UiTone.Warning, "Reconnect both PCs with this PC as coordinator before resuming.");
        ImGui.Separator();
    }

    private void DrawStorageRecovery()
    {
        foreach (var pending in PendingStorage)
        {
            UiTone.Wrapped(UiTone.Info, "Interrupted retainer movement — inventory tally");
            ImGui.TextWrapped($"{CharacterName(pending.Command.Operation.CharacterId)} on {PeerName(pending.PeerId)} · {ItemName(pending.Command.Operation.ItemId)}{(pending.Command.Operation.HighQuality ? " HQ" : "")}");
            if (pending.Response?.Observation is { } stopped)
            {
                ImGui.TextWrapped($"Confirmed moved: {stopped.MovedQuantity:N0} of {pending.Command.Operation.Quantity:N0} · bag count at stop: {stopped.BagQuantity:N0}");
                ImGui.TextWrapped("Resume compares the current count with the saved movement checkpoint, credits what moved and continues the remaining work automatically.");
            }
            else
                ImGui.TextWrapped("Resume retrieves this PC's saved movement checkpoint before continuing.");
            ImGui.Separator();
        }
    }

    private void DrawProgress()
    {
        ImGui.Spacing();
        var run = CoordinatorRole ? Service.Run : null;
        if (run == null)
        {
            ImGui.TextWrapped(CoordinatorRole ? "No transfer run yet. Set up a job, then review and start it on the Review & run page." : ReadyConnection && Settings.AllowRemoteControl ? "Connected — ready for coordinator. Start the job on your other PC; this PC reports its actions here." : "Connect and allow coordinator control on the Connection page to receive jobs.");
            UiTone.Status(Service.Status);
            return;
        }
        if (run.VisitMode && run.VisitPlanningComplete)
        {
            DrawVisitProgress(run);
            return;
        }
        TraderStyle.Section(run.Job.Definition.Name);
        if (run.ActiveItemIndex < 0 || run.ActiveItemIndex >= run.TotalItemCount)
        {
            UiTone.Wrapped(UiTone.Error, "The saved job has an invalid active item. Preserve the journal and report this error before continuing.");
            return;
        }
        if (run.TotalItemCount > 1 || run.CompletedItems.Count > 0)
        {
            UiTone.Wrapped(UiTone.Info, $"Items completed: {run.CompletedItems.Count} / {run.TotalItemCount}");
            UiTone.Progress((float)run.CompletedItems.Count / run.TotalItemCount, new Vector2(-1, 18), run.HasShortages || run.HasSkippedCharacters);
        }
        UiTone.Status(run.Status);
        if (run.Preparation is { } preparation)
        {
            var columns = ImGui.GetContentRegionAvail().X >= TraderStyle.Px(530) ? 2 : 1;
            if (ImGui.BeginTable("ParticipantProgress", columns, ImGuiTableFlags.SizingStretchSame))
            {
                foreach (var participant in new[] { preparation.Sender, preparation.Receiver })
                {
                    ImGui.TableNextColumn();
                    ImGui.TextColored(TraderStyle.Gold, PeerName(participant.PeerId));
                    ImGui.TextWrapped(CharacterName(participant.CharacterId));
                    UiTone.Wrapped(participant.Stage == ParticipantPreparationStage.Ready ? UiTone.Good : UiTone.Info,
                        WorkflowService.PreparationStageLabel(participant.Stage));
                }
                ImGui.EndTable();
            }
            ImGui.TextWrapped(preparation.Recovering
                ? "Both PCs' interrupted operations are being recovered before new preparation starts."
                : "Both PCs prepare independently. Trading begins once both characters are ready at the meeting point.");
        }
        UiTone.Wrapped(run.Paused || run.HasShortages || run.HasSkippedCharacters ? UiTone.Warning : run.Step == CoordinatorStep.Finished ? UiTone.Good : UiTone.Info, run.Step == CoordinatorStep.Finished
            ? run.HasShortages
                ? "Run ended with unmet targets. Review the shortages below; create a new job for any remaining items."
                : run.HasSkippedCharacters ? "Run ended with skipped characters or items. Their requests were not fulfilled; see the notices below."
                : "Run ended — inspect the results below before starting another job."
            : run.Paused ? "Paused — use Resume / tally inventories above when ready." : StageName(run.Step));
        foreach (var completed in run.CompletedItems.Where(x => x.ItemIndex != run.ActiveItemIndex).OrderBy(x => x.ItemIndex))
        {
            var shortfall = completed.Journal?.Plan.HasShortage == true;
            var skipped = completed.SkippedCharacters.Count != 0 || completed.ItemSkipReason.Length != 0;
            ImGui.PushID($"completedItem{completed.ItemIndex}");
            UiTone.Wrapped(shortfall || skipped ? UiTone.Warning : UiTone.Good,
                $"Item {completed.ItemIndex + 1}: {ItemLabel(completed.Definition)} · {(completed.ItemSkipReason.Length != 0 ? "item skipped" : completed.DiscoveryOnly ? skipped ? "cache lookup complete with skips" : "scan complete" : shortfall ? "available stock delivered; target remains short" : skipped ? "available deliveries complete with skips" : "delivery complete")}");
            if (ImGui.CollapsingHeader("View item results", shortfall || skipped ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            {
                DrawSkipNotices(completed.SkippedCharacters, completed.ItemSkipReason);
                DrawItemResults(completed.Journal, completed.Snapshots, completed.Withdrawn, completed.Deposited);
            }
            ImGui.PopID();
        }
        ImGui.Separator();
        UiTone.Wrapped(UiTone.Info, $"Item {run.ActiveItemIndex + 1} of {run.TotalItemCount}: {ItemLabel(run.ActiveDefinition)} · {ModeName(run.ActiveDefinition.QuantityMode)}");
        DrawSkipNotices(run.SkippedCharacters, run.ItemSkipReason);
        foreach (var relay in run.AutomaticRelays)
            UiTone.Wrapped(UiTone.Info, $"Automatic relay on {PeerName(relay.Key)}: {CharacterName(relay.Value)} · live capacity checked");
        if (run.EffectiveMeeting != null)
            UiTone.Wrapped(UiTone.Info, $"Meeting: {run.MeetingWorldName} · {run.MeetingDataCenterName}. {run.MeetingReason}.");
        if (run.CollectionRoute is { Count: > 0 } route && ImGui.CollapsingHeader("Collection itinerary"))
        {
            ImGui.TextWrapped("Grouped within each recipient and collector. Relay delivery interrupts collection when needed; the remaining provider order is kept.");
            foreach (var stop in route)
            {
                var allocation = run.Journal!.Plan.Allocations.Single(allocation => allocation.Id == stop.AllocationId);
                var ledger = new AsheTrader.Core.Jobs.TransferJobLedger(run.Journal);
                var remaining = ledger.RemainingForLeg(allocation.Id, 0);
                var active = run.AllocationId == allocation.Id && run.LegIndex == 0;
                var tone = active ? UiTone.Info : remaining == 0 ? UiTone.Good : UiTone.Warning;
                UiTone.Wrapped(tone, $"{(active ? "Now: " : "")}{stop.DataCenterName} / {stop.WorldName} · {CharacterName(allocation.SourceCharacterId)} → {CharacterName(allocation.RecipientCharacterId)} · {remaining:N0} left to collect");
            }
        }
        if (run.Journal == null)
        {
            var scanned = run.DiscoveryOrder.Count(id => run.Snapshots.TryGetValue(id, out var snapshot) && snapshot.IsPlanningReady);
            var cached = run.Snapshots.Values.Count(snapshot => snapshot.IsPlanningReady && !snapshot.IsFullyVerified);
            ImGui.TextUnformatted($"Inventory lookup: {run.DiscoveryIndex}/{run.DiscoveryOrder.Count} processed · {scanned} ready · {run.SkippedCharacters.Count} skipped · {cached} from cache");
            UiTone.Progress(run.DiscoveryOrder.Count == 0 ? 0 : (float)run.DiscoveryIndex / run.DiscoveryOrder.Count, new Vector2(-1, 18), run.SkippedCharacters.Count != 0);
        }
        else
        {
            if (run.Journal.Plan.PlannedFromCache)
                UiTone.Wrapped(UiTone.Info, "Planned from cached inventory estimates. Delivery counts below are confirmed trades; cache retrieval does not prove when inventory was last updated.");
            try
            {
                var ledger = new TransferJobLedger(run.Journal);
                ImGui.TextUnformatted($"Verified trade batches: {run.Journal.Operations.Count(x => x.State == TransferOperationState.Committed)}");
                foreach (var recipient in run.Journal.Plan.Definition.Recipients)
                {
                    var assigned = run.Journal.Plan.Allocations.Where(x => x.RecipientCharacterId == recipient.CharacterId).Aggregate(0UL, (sum, x) => sum + x.Quantity);
                    var delivered = ledger.DeliveredTo(recipient.CharacterId);
                    UiTone.Wrapped(delivered >= assigned ? UiTone.Good : UiTone.Info, $"{CharacterName(recipient.CharacterId)}: {delivered:N0} / {assigned:N0} planned items delivered");
                    UiTone.Progress(assigned == 0 ? 1 : (float)((double)delivered / assigned), new Vector2(-1, 18));
                }
                foreach (var shortage in run.Journal.Plan.UnmetTargets)
                    UiTone.Wrapped(UiTone.Warning, $"Unmet target: {CharacterName(shortage.CharacterId)} needs {shortage.Missing:N0} more than the discovered available stock.");
                foreach (var relay in run.Journal.Plan.Definition.RelayCharacters.Values)
                {
                    var cargo = ledger.RelayCargo(relay);
                    var phase = run.RelayFlushCharacterId == relay ? "Delivering the collected load" : "Collecting from providers";
                    if (cargo > 0) UiTone.Wrapped(UiTone.Info, $"{phase}: {CharacterName(relay)} holds {cargo:N0} job items.");
                    if (run.RelayCapacity.TryGetValue(relay, out var remainingCapacity))
                        ImGui.TextWrapped($"Relay capacity remaining for this item: approximately {remainingCapacity:N0}. Deliver when full or when all providers for this recipient have contributed.");
                }
            }
            catch (Exception exception) { UiTone.Wrapped(UiTone.Error, $"Journal requires attention: {exception.Message}"); }
        }
        foreach (var pickup in run.Withdrawn.Where(x => x.Value > 0))
            UiTone.Wrapped(UiTone.Info, $"Collected from provider retainers: {CharacterName(pickup.Key)} · {pickup.Value:N0} items");
        foreach (var deposit in run.Deposited.Where(x => x.Value > 0))
            UiTone.Wrapped(UiTone.Info, $"Stored in recipient retainers: {CharacterName(deposit.Key)} · {deposit.Value:N0} items");
        if (ImGui.CollapsingHeader("Operation details"))
        {
            if (run.Pending.Count == 0) ImGui.TextUnformatted("No pending worker command.");
            foreach (var pending in run.Pending)
            {
                var command = pending.Command;
                ImGui.TextWrapped($"{PeerName(pending.PeerId)} · {CharacterName(command.Operation.CharacterId)} · {ItemName(command.Operation.ItemId)}{(command.Operation.HighQuality ? " HQ" : "")} · {OperationName(command)}");
                var response = pending.Response;
                var cacheQuery = command.Operation.Kind == GameOperationKind.ReadCachedInventory;
                UiTone.Wrapped(response == null || cacheQuery ? UiTone.Info : response.Success ? UiTone.Good : UiTone.Error, response == null ? (pending.Ready ? "Receiver armed; awaiting the agreed trade." : "Waiting for the worker's observation.")
                    : response.Success ? cacheQuery ? response.Observation?.CachedInventoryAvailable == true ? "Cached inventory received; used for planning." : "Cache unavailable; this character will be skipped and discovery will continue." : $"Verified response: {response.Quantity:N0} items." : response.Error);
                if (response?.BeforeQuantity is not null || response?.CurrentQuantity is not null)
                    ImGui.TextUnformatted($"Recorded bag count: {response?.BeforeQuantity?.ToString("N0") ?? "unknown"}; current: {response?.CurrentQuantity?.ToString("N0") ?? "unknown"}");
                if (command.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch)
                    ImGui.TextUnformatted($"Expected trade: {command.Operation.Quantity:N0} items {(command.Kind == WorkerCommandKind.SendBatch ? "sent" : "received")}");
            }
        }
        if (ImGui.CollapsingHeader("Inventory counts and sources"))
        {
            ImGui.TextWrapped("Cached estimates and live observations are labeled separately. Delivery progress is tracked independently and remains valid after retainer deposits.");
            foreach (var note in run.DiscoveryNotes)
                UiTone.Wrapped(run.SkippedCharacters.ContainsKey(note.Key) ? UiTone.Warning : UiTone.Info, $"{CharacterName(note.Key)}: {note.Value}");
            foreach (var snapshot in run.Snapshots.Values)
            {
                UiTone.Wrapped(snapshot.IsFullyVerified ? UiTone.Good : UiTone.Info,
                    $"{snapshot.Name}@{snapshot.World}: bags {snapshot.BagQuantity:N0}; " +
                    (snapshot.RetainerRosterVerified || snapshot.HasCompleteCachedInventory ? $"retainers {snapshot.Retainers.Aggregate(0UL, (sum, x) => sum + x.Quantity):N0} · {snapshot.Verification}"
                        : $"relay capacity {snapshot.BagCapacity:N0}; retainers not scanned (not needed for relay use)"));
                if (snapshot.Verification == InventoryVerification.Cached && snapshot.RetrievedAt is { } retrieved)
                    ImGui.TextDisabled($"  Cache read at {retrieved.ToLocalTime():g}; last inventory observation time is unknown.");
                foreach (var retainer in snapshot.Retainers)
                    ImGui.TextDisabled($"  {retainer.Name}: {retainer.Quantity:N0} · {retainer.Verification}");
            }
        }
    }

    private static void DrawSkipNotices(IReadOnlyDictionary<ulong, string> skipped, string itemSkipReason)
    {
        if (itemSkipReason.Length != 0) UiTone.Wrapped(UiTone.Warning, itemSkipReason);
        if (skipped.Count == 0) return;
        UiTone.Wrapped(UiTone.Warning, $"Skipped characters for this item: {skipped.Count}. Their inventory is unknown and their requests were not fulfilled.");
        if (ImGui.CollapsingHeader("Why characters were skipped", ImGuiTreeNodeFlags.DefaultOpen))
            foreach (var notice in skipped.Values) UiTone.Wrapped(UiTone.Warning, notice);
    }

    private void DrawItemResults(TransferJobJournal? journal, IReadOnlyDictionary<ulong, JobCharacterSnapshot> snapshots,
        IReadOnlyDictionary<ulong, ulong> withdrawn, IReadOnlyDictionary<ulong, ulong> deposited)
    {
        if (journal != null)
        {
            try
            {
                var ledger = new TransferJobLedger(journal);
                foreach (var recipient in journal.Plan.Definition.Recipients)
                {
                    var assigned = journal.Plan.Allocations.Where(x => x.RecipientCharacterId == recipient.CharacterId).Aggregate(0UL, (sum, x) => checked(sum + x.Quantity));
                    UiTone.Wrapped(UiTone.Good, $"{CharacterName(recipient.CharacterId)}: {ledger.DeliveredTo(recipient.CharacterId):N0} / {assigned:N0} planned items delivered");
                }
                foreach (var shortage in journal.Plan.UnmetTargets)
                    UiTone.Wrapped(UiTone.Warning, $"Unmet target: {CharacterName(shortage.CharacterId)} needs {shortage.Missing:N0} more items.");
                foreach (var relay in journal.Plan.Definition.RelayCharacters)
                    UiTone.Wrapped(UiTone.Info, $"Automatic relay on {PeerName(relay.Key)}: {CharacterName(relay.Value)}");
            }
            catch (Exception exception) { UiTone.Wrapped(UiTone.Error, $"Journal requires attention: {exception.Message}"); }
        }
        foreach (var pickup in withdrawn.Where(x => x.Value > 0))
            ImGui.TextWrapped($"Collected from provider retainers: {CharacterName(pickup.Key)} · {pickup.Value:N0} items");
        foreach (var deposit in deposited.Where(x => x.Value > 0))
            ImGui.TextWrapped($"Stored in recipient retainers: {CharacterName(deposit.Key)} · {deposit.Value:N0} items");
        foreach (var snapshot in snapshots.Values)
            ImGui.TextWrapped($"{snapshot.Verification} · {snapshot.Name}@{snapshot.World}: bags {snapshot.BagQuantity:N0}; " +
                (snapshot.RetainerRosterVerified || snapshot.HasCompleteCachedInventory ? $"retainers {snapshot.Retainers.Aggregate(0UL, (sum, x) => sum + x.Quantity):N0}"
                    : "relay capacity checked; retainers not scanned"));
    }

    private void DrawDiagnosticsControls()
    {
        ImGui.Spacing();
        ImGui.BeginDisabled(Volatile.Read(ref diagnosticCopyPending) != 0);
        if (TraderStyle.SecondaryButton("Copy diagnostics"))
        {
            Interlocked.Exchange(ref diagnosticCopyPending, 1);
            plugin.QueueAction(() =>
            {
                try { Volatile.Write(ref diagnosticsToCopy, Service.CaptureDiagnosticReport()); }
                finally { Interlocked.Exchange(ref diagnosticCopyPending, 0); }
            });
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy on the affected PC before closing a stuck game menu. Includes recent steps, menu labels and plugin versions; excludes pairing keys and chat.");
        ImGui.Spacing();
    }

    private void DrawActivityLog()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Recent activity", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (ImGui.Button("Copy activity for troubleshooting"))
            ImGui.SetClipboardText($"Ashe's Trader v{typeof(Plugin).Assembly.GetName().Version}\n{Service.ConnectionStatus}\n{Service.Status}\n{dependencySummary}\n{string.Join("\n", plugin.Activity)}");
        if (plugin.Activity.Count == 0) ImGui.TextWrapped("Actions and outcomes will appear here when a job runs.");
        foreach (var entry in plugin.Activity.Reverse()) UiTone.Status(entry);
        DrawHints();
    }

    private static string StageName(CoordinatorStep step) => step switch
    {
        CoordinatorStep.DiscoverCache => "Reading cached inventories from the owning PC",
        CoordinatorStep.DiscoverLogin or CoordinatorStep.DiscoverScan => "Resuming legacy discovery through cached inventories",
        CoordinatorStep.Plan or CoordinatorStep.SelectLeg => "Planning the next delivery",
        CoordinatorStep.RelayLogin or CoordinatorStep.RelayInspect => "Choosing an automatic relay and checking its live inventory space",
        CoordinatorStep.LoginSender or CoordinatorStep.LoginReceiver => "Logging in the characters for this delivery",
        CoordinatorStep.PrepareParticipants => "Preparing both PCs in parallel",
        CoordinatorStep.InspectSender or CoordinatorStep.InspectReceiver => "Checking carried items and available inventory space",
        CoordinatorStep.Withdraw => "Provider refill: collecting the next load from retainers",
        CoordinatorStep.Deposit => "Recipient storage: making room in bags before continuing",
        CoordinatorStep.DepositPreviousItem => "Storing an earlier job item to make room before continuing",
        CoordinatorStep.TravelSender or CoordinatorStep.TravelReceiver => "Returning to the meeting point",
        _ => "Trading and verifying the delivery on both PCs"
    };

    private static string OperationName(WorkerCommand command) => command.Kind switch
    {
        WorkerCommandKind.PreviewBatch => "Checking the next trade's contents",
        WorkerCommandKind.ReceiveBatch => "Receiving and verifying a trade",
        WorkerCommandKind.SendBatch => "Sending and verifying a trade",
        _ => command.Operation.Kind switch
        {
            GameOperationKind.Login => "Changing character",
            GameOperationKind.Inspect => "Checking inventory",
            GameOperationKind.TravelMeeting => "Travelling to the meeting point",
            GameOperationKind.ReadCachedInventory => "Reading Allagan Tools cached inventory",
            GameOperationKind.ScanRetainers => "Scanning bags and retainers",
            GameOperationKind.Withdraw => "Collecting items from provider retainers",
            GameOperationKind.Deposit => "Depositing items into recipient retainers",
            _ => "Working"
        }
    };

    private void CaptureMeeting(Guid jobId)
    {
        if (EditingLocked) throw new InvalidOperationException("Finish the active job before changing its meeting point.");
        if (!GameWorkflowExecutor.TryCaptureCurrentLocation(out var location, out var error) || location == null)
            throw new InvalidOperationException(error);
        if (location.AetheryteId == 0) throw new InvalidOperationException("Record an outdoor meeting territory with a main aetheryte.");
        var job = Settings.Jobs.FirstOrDefault(x => x.Definition.Id == jobId) ?? throw new InvalidOperationException("Saved job no longer exists.");
        job.Meeting = location; plugin.Save(); notice = "Meeting point recorded.";
    }

    private void CaptureStorage(ulong characterId)
    {
        if (EditingLocked) throw new InvalidOperationException("Finish the active job before changing storage settings.");
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || Plugin.PlayerState.ContentId != characterId || player.CurrentWorld.RowId != player.HomeWorld.RowId)
            throw new InvalidOperationException("Log in this character on its home world before recording its bell.");
        if (!GameWorkflowExecutor.TryCaptureCurrentLocation(out var location, out var error) || location == null)
            throw new InvalidOperationException(error);
        if (location.AetheryteId == 0) throw new InvalidOperationException("Record an outdoor bell with an arrival aetheryte, or use the inn option for interior storage.");
        Settings.StorageRoutes[characterId] = location; plugin.Save(); notice = "Home-world bell location recorded.";
    }

    private void DrawLocation(TravelLocation location, bool worldIndependent = false)
    {
        if (location.TerritoryId == 0 || !worldIndependent && location.WorldId == 0) { UiTone.Wrapped(UiTone.Warning, "No location recorded."); return; }
        var world = Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault(location.WorldId)?.Name.ToString() ?? location.WorldId.ToString();
        var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(location.TerritoryId)?.PlaceName.Value.Name.ToString() ?? location.TerritoryId.ToString();
        ImGui.TextWrapped($"{(worldIndependent ? "Automatic world" : world)} · {territory} · ({location.X:F1}, {location.Y:F1}, {location.Z:F1})");
    }

    private string PeerName(Guid peerId) => peerId == Settings.PeerId ? Settings.PeerName : Service.RemoteRoster?.PeerId == peerId ? Service.RemoteRoster.Name : "Other PC";
    private string CharacterName(ulong id) => Service.AllCharacters().FirstOrDefault(x => x.Character.ContentId == id).Character is { } character
        ? CharacterName(character) : Service.Run?.Snapshots.GetValueOrDefault(id) is { } snapshot ? $"{snapshot.Name}@{snapshot.World}" : $"Unavailable character {id}";
    private static string CharacterName(CharacterProfile character) => $"{character.Name}@{character.World}";
    private static string ItemName(uint id) => Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id)?.Name.ToString() ?? $"Item {id}";
    private static string AetheryteName(uint id) => id == 0 ? "Select an aetheryte" : Plugin.DataManager.GetExcelSheet<Aetheryte>().GetRowOrDefault(id)?.PlaceName.Value.Name.ToString() ?? $"Aetheryte {id}";
    private static string ModeName(JobQuantityMode mode) => mode switch { JobQuantityMode.MoveAll => "Move all", JobQuantityMode.DeliverAmount => "Deliver an amount", _ => "Restock to a total" };
    private bool HasInvalidAmounts(TransferJobDefinition definition) => definition.Recipients
        .Where(x => x.Quantity != null).Any(x => amountInputs.TryGetValue($"{definition.Id}/{x.CharacterId}", out var text)
            && (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount == 0));
    private void DrawHints()
    {
        if (Service.LocalInventoryHints == null && Service.RemoteInventoryHints == null) return;
        if (!ImGui.CollapsingHeader("Allagan Tools cached inventory hints", ImGuiTreeNodeFlags.DefaultOpen)) return;
        foreach (var entry in new[] { (Label: Settings.PeerName, Hints: Service.LocalInventoryHints), (Label: Service.RemoteRoster?.Name ?? "Other PC", Hints: Service.RemoteInventoryHints) })
        {
            if (entry.Hints is not { } hints) { ImGui.TextWrapped($"{entry.Label}: no hints received."); continue; }
            ImGui.TextWrapped($"{entry.Label}: {hints.Status}");
            ImGui.TextUnformatted($"{ItemName(hints.ItemId)}{(hints.HighQuality ? " HQ" : "")}: {hints.Items.Sum(x => (decimal)x.Quantity):N0} cached items across {hints.Items.Count} owners.");
        }
        ImGui.TextWrapped("Cached hints can be stale or incomplete. A missing owner or quantity is not verified empty stock.");
    }

    private void CheckDependencies()
    {
        var ar = plugin.AutoRetainer.GetStatus();
        var ls = plugin.Lifestream.GetStatus();
        var cache = new AsheTrader.Integrations.AllaganToolsClient().IsAvailable;
        var issues = new List<string>();
        if (!cache) issues.Add("enable Allagan Tools for cached inventory planning");
        if (!ar.Ready) issues.Add("enable AutoRetainer");
        else if (ar.Busy != false || ar.MultiModeEnabled != false) issues.Add("pause AutoRetainer multi mode and wait for its tasks");
        if (!ls.Ready) issues.Add("enable Lifestream");
        else if (ls.Busy != false) issues.Add("wait for Lifestream to become idle");
        string mesh;
        try
        {
            var ready = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc();
            mesh = ready ? "navigation mesh ready" : "navigation mesh not ready in this territory";
            if (!ready) issues.Add("wait for vnavmesh to be ready in the current territory");
        }
        catch (Exception) { mesh = "vnavmesh unavailable or its IPC is not ready"; issues.Add("enable vnavmesh and wait for navigation to load"); }
        dependencySummary = issues.Count == 0 ? $"Local plugins checked at {DateTime.Now:T}: available and idle. Check the other PC separately." : $"This PC needs attention: {string.Join("; ", issues)}. Checked {DateTime.Now:T}.";
        dependencySummary += cache ? " Allagan Tools cache is available." : " Inventory planning waits for Allagan Tools; it does not start live scans.";
        dependencies = $"AutoRetainer: {(ar.Ready ? $"available; busy={ar.Busy?.ToString() ?? "unknown"}, multi mode={ar.MultiModeEnabled?.ToString() ?? "unknown"}" : ar.Error)}. Lifestream: {(ls.Ready ? $"available; busy={ls.Busy?.ToString() ?? "unknown"}" : ls.Error)}. {mesh}. Checked {DateTime.Now:T}.";
    }
}
