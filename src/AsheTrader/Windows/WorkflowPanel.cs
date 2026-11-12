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
public sealed class WorkflowPanel(Plugin plugin)
{
    private Guid selectedJob;
    private int selectedItemIndex;
    private int page;
    private bool? coordinatorRole;
    private string itemSearch = string.Empty;
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

    public void Draw()
    {
        if (Interlocked.Exchange(ref diagnosticsToCopy, null) is { } report)
        {
            ImGui.SetClipboardText(report);
            notice = "Diagnostics copied for this PC. Include the report when reporting the issue; also copy the other PC's report if it was running the stuck operation.";
        }
        ImGui.PushID("AsheWorkflowPanel");
        ImGui.Spacing();
        UiTone.Wrapped(ReadyConnection ? UiTone.Good : UiTone.Warning, ReadyConnection
            ? $"Connected to {Service.RemoteRoster?.Name ?? "paired PC"} · {(Service.IsHost ? "This PC controls transfers" : Settings.AllowRemoteControl ? "Ready for coordinator" : "Enable control below to receive jobs")}"
            : Service.Connected ? "Authenticating the paired PC and loading its characters..." : Service.PairingEnabled ? "Waiting for the other PC to connect..." : "Connect your two PCs to get started.");
        if (LocalActionRunning || HasUnfinishedJob) UiTone.Status(Service.Status);
        DrawRunControls();
        ImGui.Separator();
        string[] steps = ["1. Connect PCs", "2. Set up", "3. Review & run", "4. Activity"];
        var width = Math.Max(90, (ImGui.GetContentRegionAvail().X - 24) / 4);
        for (var index = 0; index < steps.Length; index++)
        {
            if (index > 0) ImGui.SameLine();
            ImGui.BeginDisabled(!CoordinatorRole && index is 1 or 2);
            if (ImGui.Button((page == index ? "> " : "") + steps[index], new Vector2(width, 30))) page = index;
            ImGui.EndDisabled();
        }
        if (!CoordinatorRole && page is 1 or 2) page = 0;
        if (ImGui.BeginChild("WorkflowPage", new Vector2(0, 0)))
        {
            if (notice.Length > 0)
            {
                UiTone.Status(notice);
                if (ImGui.SmallButton("Dismiss message")) notice = string.Empty;
                ImGui.Separator();
            }
            switch (page)
            {
                case 0: DrawPairing(); break;
                case 1: DrawJobs(); break;
                case 2: DrawReview(); break;
                default: DrawTradeRecovery(); DrawStorageRecovery(); DrawDiagnosticsControls(); DrawProgress(); DrawActivityLog(); break;
            }
        }
        ImGui.EndChild();
        ImGui.PopID();
    }

    private void DrawRunControls()
    {
        if (Service.Busy)
        {
            if (ImGui.Button("Pause workflow", new Vector2(165, 30)))
                plugin.QueueAction(() => Service.Pause("Paused by user."));
        }
        if (Service.Run is { Paused: true, Step: not CoordinatorStep.Finished })
        {
            var needsRecovery = NeedsStorageRecovery || NeedsTradeRecovery;
            ImGui.BeginDisabled(!Service.IsHost || !Service.Connected || LocalActionRunning);
            if (ImGui.Button("Resume / tally inventories", new Vector2(230, 30)))
                plugin.QueueAction(Service.Resume);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(LocalActionRunning || needsRecovery);
            if (ImGui.Button("End paused job")) plugin.QueueAction(Service.EndPausedJob);
            ImGui.EndDisabled();
            UiTone.Wrapped(UiTone.Info, "Resume tallies the current inventories, updates saved progress and continues the remaining work automatically. Activity shows any counts that cannot be reconciled.");
        }
    }

    private void DrawPairing()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Choose what this PC does");
        ImGui.BeginDisabled(Service.PairingEnabled || EditingLocked);
        if (ImGui.RadioButton("This PC controls transfers", CoordinatorRole)) coordinatorRole = true;
        if (ImGui.RadioButton("This PC receives instructions", !CoordinatorRole)) coordinatorRole = false;
        ImGui.EndDisabled();
        if (HasUnfinishedJob) UiTone.Wrapped(UiTone.Warning, "This PC has an unfinished coordinator job. Reconnect it as the coordinator to resume or resolve that job.");
        var coordinator = CoordinatorRole;
        ImGui.TextWrapped(coordinator
            ? "Create and start your jobs here. On the other PC, choose 'receives instructions' and connect to this PC."
            : "Connect once, then create and start jobs on the coordinator. You do not need to copy or recreate its transfer setup here.");
        ImGui.Separator();
        var changed = false;
        ImGui.BeginDisabled(Service.PairingEnabled || LocalActionRunning);
        var name = Settings.PeerName;
        ImGui.SetNextItemWidth(Math.Min(310, ImGui.GetContentRegionAvail().X * .6f));
        if (ImGui.InputText("This PC's name", ref name, 80)) { Settings.PeerName = name; changed = true; }
        ImGui.BeginDisabled(HasUnfinishedJob);
        var key = Settings.PairingKey;
        ImGui.SetNextItemWidth(Math.Min(310, ImGui.GetContentRegionAvail().X * .6f));
        if (ImGui.InputText(coordinator ? "Shared pairing key" : "Key from coordinator", ref key, 128, revealKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password))
        { Settings.PairingKey = key.Trim(); changed = true; }
        if (coordinator && ImGui.Button(Settings.PairingKey.Length == 0 ? "Create pairing key" : "Replace pairing key"))
        { Settings.PairingKey = WorkflowService.NewPairingKey(); changed = true; }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        if (coordinator)
        {
            ImGui.BeginDisabled(Settings.PairingKey.Length == 0);
            if (ImGui.Button("Copy key for the other PC")) { ImGui.SetClipboardText(Settings.PairingKey); notice = "Pairing key copied. Enter it on the other PC once; both PCs remember it."; }
            ImGui.EndDisabled();
        }
        ImGui.Checkbox("Show key", ref revealKey);
        if (changed) plugin.Save();
        if (Settings.PairingKey.Length == 0)
            ImGui.TextWrapped(coordinator ? "Host below to create a key automatically, then copy it to your other PC once." : "Enter the coordinator's key once. Nearby discovery finds its address automatically.");

        if (coordinator)
        {
            ImGui.BeginDisabled(Service.PairingEnabled || LocalActionRunning);
            if (ImGui.Button("Host for my other PC", new Vector2(220, 32))) plugin.QueueAction(Service.Host);
            ImGui.EndDisabled();
        }
        else
        {
            ImGui.BeginDisabled(Service.PairingEnabled || LocalActionRunning || HasUnfinishedJob || Service.Discovering);
            if (ImGui.Button("Find my coordinator", new Vector2(220, 32))) plugin.QueueAction(Service.StartDiscovery);
            ImGui.EndDisabled();
            if (Service.Discovering)
            {
                ImGui.SameLine();
                if (ImGui.Button("Stop search")) plugin.QueueAction(Service.StopDiscovery);
            }
            if (!Service.PairingEnabled)
            {
                UiTone.Status(Service.DiscoveryStatus);
                foreach (var candidate in Service.NearbyPeers)
                {
                    ImGui.PushID($"{candidate.PeerId}/{candidate.Address}:{candidate.Port}");
                    var remembered = Settings.ExpectedRemotePeerId == candidate.PeerId;
                    var otherIdentity = Settings.ExpectedRemotePeerId.HasValue && !remembered;
                    ImGui.TextWrapped(candidate.Name + (remembered ? " (remembered PC)" : otherIdentity ? " (different PC; see Advanced to replace pairing)" : " (key required to authenticate)"));
                    ImGui.BeginDisabled(LocalActionRunning || HasUnfinishedJob || Settings.PairingKey.Length == 0 || otherIdentity);
                    if (ImGui.Button("Connect to this PC"))
                    {
                        var selected = candidate;
                        plugin.QueueAction(() => Service.ConnectDiscovered(selected));
                    }
                    ImGui.EndDisabled();
                    ImGui.PopID();
                }
            }
            ImGui.Spacing();
            var allow = Settings.AllowRemoteControl;
            if (ImGui.Checkbox("Allow the coordinator to run jobs on this PC", ref allow))
            {
                Settings.AllowRemoteControl = allow;
                plugin.Save();
                var enabled = allow;
                plugin.QueueAction(() =>
                {
                    if (!enabled) Service.Pause("Paired workflow control was disabled on this PC.");
                    Service.RefreshRosters();
                });
            }
            ImGui.TextWrapped("This allows character changes, travel, retainer visits and trades for the coordinator's jobs. Turning it off pauses this PC.");
            if (ReadyConnection)
                UiTone.Wrapped(Settings.AllowRemoteControl ? UiTone.Good : UiTone.Warning, Settings.AllowRemoteControl ? "Connected — ready for coordinator. Leave this PC running; start the job on the other PC." : "Connected — waiting for you to allow control above.");
        }
        if (Service.PairingEnabled)
        {
            if (ImGui.Button("Disconnect PCs")) plugin.QueueAction(Service.Disconnect);
        }
        UiTone.Status(Service.ConnectionStatus);
        ImGui.TextWrapped("Both PCs must be on the same local network for automatic discovery. Keep the coordinator hosting while connecting.");
        ImGui.Separator();
        ImGui.TextUnformatted("Characters available for jobs");
        ImGui.TextWrapped($"{Settings.PeerName}: {plugin.Config.Characters.Count} characters");
        var remote = Service.RemoteRoster;
        ImGui.TextWrapped(remote == null ? "Other PC: waiting for its character list" : $"{remote.Name}: {remote.Characters.Count} characters · control {(remote.ControlEnabled ? "enabled" : "disabled")}");
        ImGui.BeginDisabled(LocalActionRunning);
        if (ImGui.Button("Refresh characters on both PCs")) plugin.QueueAction(Service.RefreshRosters);
        ImGui.EndDisabled();
        UiTone.Status(Service.RosterStatus);
        ImGui.TextWrapped("Characters come from AutoRetainer on each PC. Refresh after connecting or configuring more characters.");
        if (ImGui.CollapsingHeader("View known characters"))
            foreach (var entry in Service.AllCharacters())
                ImGui.TextWrapped($"{CharacterName(entry.Character)} — {PeerName(entry.PeerId)}");
        ImGui.Separator();
        if (ImGui.Button("Check required plugins on this PC")) plugin.QueueAction(CheckDependencies);
        UiTone.Status(dependencySummary);
        ImGui.TextWrapped("Check this on both PCs. Keep AutoRetainer enabled, pause its multi mode, and let pending tasks finish.");
        if (coordinator && ImGui.Button("Next: set up transfer", new Vector2(220, 32))) page = 1;
        if (!coordinator && ImGui.Button("View activity", new Vector2(220, 32))) page = 3;
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Advanced: connection, plugins and retainer settings"))
        {
            DrawConnectionAdvanced(coordinator);
            if (ImGui.CollapsingHeader("Retainer destinations on this PC")) DrawStorage();
        }
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
        ImGui.Spacing();
        var job = Settings.Jobs.FirstOrDefault(x => x.Definition.Id == selectedJob) ?? Settings.Jobs.FirstOrDefault();
        if (job != null) selectedJob = job.Definition.Id;
        if (ImGui.BeginCombo("Saved job", job?.Definition.Name ?? "Create a transfer job"))
        {
            foreach (var candidate in Settings.Jobs)
                if (ImGui.Selectable($"{candidate.Definition.Name}##{candidate.Definition.Id}", candidate.Definition.Id == selectedJob))
                { selectedJob = candidate.Definition.Id; selectedItemIndex = 0; itemSearch = ""; job = candidate; }
            ImGui.EndCombo();
        }
        ImGui.BeginDisabled(EditingLocked);
        if (ImGui.Button("New transfer job"))
        {
            job = new SavedTransferJob { Definition = new TransferJobDefinition { Name = $"Transfer job {Settings.Jobs.Count + 1}", ItemId = 10155, QuantityMode = JobQuantityMode.DeliverAmount } };
            Settings.Jobs.Add(job); selectedJob = job.Definition.Id; selectedItemIndex = 0; itemSearch = ""; plugin.Save();
        }
        if (job != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete selected setup"))
            {
                Settings.Jobs.Remove(job);
                selectedJob = Guid.Empty;
                selectedItemIndex = 0;
                job = Settings.Jobs.FirstOrDefault();
                plugin.Save();
            }
        }
        ImGui.EndDisabled();
        if (EditingLocked) UiTone.Wrapped(UiTone.Warning, "Job and storage settings are locked while a run is unfinished. Pause to inspect progress; end a resolved job to edit its setup.");
        if (job == null)
        {
            ImGui.TextWrapped("Create a transfer job, add the items you want to move, then choose providers and recipients for each item. Items run in order, sharing one meeting point.");
            return;
        }
        var changed = false;
        ImGui.BeginDisabled(EditingLocked);
        var name = job.Definition.Name;
        if (ImGui.InputText("Job name", ref name, 100)) { job.Definition.Name = name; changed = true; }
        var definition = DrawItemLines(job, ref changed);
        ImGui.PushID(definition.Id.ToString());
        DrawItemPicker(definition, ref changed);
        if (ImGui.BeginCombo("Quantity rule", ModeName(definition.QuantityMode)))
        {
            foreach (var mode in Enum.GetValues<JobQuantityMode>())
                if (ImGui.Selectable(ModeName(mode), definition.QuantityMode == mode))
                {
                    definition.QuantityMode = mode;
                    if (mode != JobQuantityMode.MoveAll)
                        foreach (var recipient in definition.Recipients)
                            if (recipient.Quantity == null)
                            { recipient.Quantity = 1; amountInputs.Remove($"{definition.Id}/{recipient.CharacterId}"); }
                    changed = true;
                }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped(definition.QuantityMode switch
        {
            JobQuantityMode.MoveAll => "Collect eligible source stock. Fill recipient caps in order; the final recipient can take everything remaining.",
            JobQuantityMode.DeliverAmount => "Amounts are additional deliveries for this run. Retainer deposits still count toward delivery.",
            _ => "Amounts are final stock targets, counting the recipient's bags and retainers. Existing target stock is protected."
        });

        var roster = Service.AllCharacters().ToArray();
        if (ImGui.CollapsingHeader("Providers: who gives the items?", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.SmallButton("Select all known characters")) { definition.Sources = roster.Select(x => x.Character.ContentId).Distinct().ToList(); changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear sources")) { definition.Sources.Clear(); changed = true; }
            foreach (var group in roster.GroupBy(x => x.PeerId))
            {
                ImGui.TextDisabled(PeerName(group.Key));
                foreach (var entry in group)
                {
                    var selected = definition.Sources.Contains(entry.Character.ContentId);
                    if (ImGui.Checkbox($"{CharacterName(entry.Character)}##source{entry.PeerId}{entry.Character.ContentId}", ref selected))
                    {
                        if (selected) definition.Sources.Add(entry.Character.ContentId); else definition.Sources.Remove(entry.Character.ContentId);
                        changed = true;
                    }
                }
            }
            foreach (var missing in definition.Sources.Where(id => roster.All(x => x.Character.ContentId != id)).ToArray())
            {
                ImGui.TextUnformatted($"Source {missing}: roster not currently available");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Remove##missing{missing}")) { definition.Sources.Remove(missing); changed = true; }
            }
            if (roster.Length == 0) UiTone.Wrapped(UiTone.Warning, "No characters yet. Connect both PCs and refresh characters in step 1.");
            ImGui.TextWrapped("Providers use carried items first, then collect more from retainers as needed. Planning uses Allagan Tools' saved bag and retainer counts without logging in to scan them again.");
        }

        if (ImGui.CollapsingHeader("Recipients: fill from top to bottom", ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (var index = 0; index < definition.Recipients.Count; index++)
            {
                var recipient = definition.Recipients[index];
                var priorityChanged = false;
                ImGui.PushID($"recipient{recipient.CharacterId}");
                ImGui.TextUnformatted($"{index + 1}. {CharacterName(recipient.CharacterId)}");
                ImGui.BeginDisabled(index == 0 || recipient.Quantity == null);
                if (ImGui.SmallButton("Up"))
                {
                    (definition.Recipients[index - 1], definition.Recipients[index]) = (definition.Recipients[index], definition.Recipients[index - 1]);
                    changed = true; priorityChanged = true;
                }
                ImGui.EndDisabled(); ImGui.SameLine();
                ImGui.BeginDisabled(index == definition.Recipients.Count - 1 || definition.Recipients[index + 1].Quantity == null);
                if (ImGui.SmallButton("Down"))
                {
                    (definition.Recipients[index + 1], definition.Recipients[index]) = (definition.Recipients[index], definition.Recipients[index + 1]);
                    changed = true; priorityChanged = true;
                }
                ImGui.EndDisabled(); ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) { definition.Recipients.RemoveAt(index--); changed = true; ImGui.PopID(); continue; }
                if (priorityChanged) { ImGui.PopID(); break; }
                var remainder = recipient.Quantity == null;
                if (definition.QuantityMode == JobQuantityMode.MoveAll && index == definition.Recipients.Count - 1)
                    if (ImGui.Checkbox("Take the remainder", ref remainder))
                    { recipient.Quantity = remainder ? null : 1; amountInputs.Remove($"{definition.Id}/{recipient.CharacterId}"); changed = true; }
                if (!remainder)
                {
                    var inputKey = $"{definition.Id}/{recipient.CharacterId}";
                    if (!amountInputs.TryGetValue(inputKey, out var amount)) amount = recipient.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "1";
                    ImGui.SetNextItemWidth(210);
                    if (ImGui.InputText(definition.QuantityMode switch { JobQuantityMode.RestockToTotal => "Total in bags + retainers", JobQuantityMode.DeliverAmount => "Additional items to send", _ => "Maximum items to send" }, ref amount, 24, ImGuiInputTextFlags.CharsDecimal))
                    {
                        amountInputs[inputKey] = amount;
                        if (ulong.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                        { recipient.Quantity = parsed; changed = true; }
                    }
                    if (!ulong.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity == 0)
                        UiTone.Wrapped(UiTone.Error, "Enter a positive whole number; the last valid amount remains saved.");
                }
                ImGui.PopID();
            }
            if (ImGui.BeginCombo("Add recipient", "Choose a character"))
            {
                foreach (var entry in roster.Where(x => definition.Recipients.All(recipient => recipient.CharacterId != x.Character.ContentId)))
                    if (ImGui.Selectable($"{CharacterName(entry.Character)} — {PeerName(entry.PeerId)}"))
                    {
                        var remainderIndex = definition.Recipients.FindIndex(x => x.Quantity == null);
                        var recipient = new RecipientRule { CharacterId = entry.Character.ContentId, Quantity = definition.QuantityMode == JobQuantityMode.MoveAll && remainderIndex < 0 ? null : 10 };
                        if (remainderIndex >= 0) definition.Recipients.Insert(remainderIndex, recipient);
                        else definition.Recipients.Add(recipient);
                        changed = true;
                    }
                ImGui.EndCombo();
            }
            ImGui.TextWrapped("The remainder recipient stays last. To move it earlier, turn off 'Take the remainder' and set its cap first. New recipients are inserted before an existing remainder recipient.");
        }

        ImGui.PopID();
        DrawAutomaticRelayInfo();

        if (ImGui.CollapsingHeader("Meeting point: where the PCs trade", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Record an outdoor location both accounts can reach. The territory, position and arrival aetheryte are reused on each provider's world; the world you record it on does not restrict the job.");
            if (ImGui.Button("Record my current meeting point"))
            {
                var jobId = job.Definition.Id;
                plugin.QueueAction(() => CaptureMeeting(jobId));
            }
            DrawLocation(job.Meeting, worldIndependent: true);
            if (job.Meeting.TerritoryId != 0)
            {
                var choices = Plugin.DataManager.GetExcelSheet<Aetheryte>().Where(x => x.IsAetheryte && x.Territory.RowId == job.Meeting.TerritoryId).ToArray();
                if (ImGui.BeginCombo("Arrival aetheryte", AetheryteName(job.Meeting.AetheryteId)))
                {
                    foreach (var aetheryte in choices)
                        if (ImGui.Selectable(AetheryteName(aetheryte.RowId), job.Meeting.AetheryteId == aetheryte.RowId))
                        { job.Meeting.AetheryteId = aetheryte.RowId; changed = true; }
                    ImGui.EndCombo();
                }
                if (choices.Length == 0) ImGui.TextWrapped("This territory has no main aetheryte. Record a supported outdoor meeting point.");
            }
        }
        ImGui.EndDisabled();
        if (changed) plugin.Save();
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Optional: retainer destinations on this PC")) DrawStorage();
        ImGui.TextWrapped("When a recipient's bags fill, it returns to its home world, deposits into its allowed retainers, then comes back to continue. The default destination is a home-world inn.");
        if (ImGui.Button("Next: review this transfer", new Vector2(250, 32))) page = 2;
    }

    private TransferJobDefinition DrawItemLines(SavedTransferJob job, ref bool changed)
    {
        var lines = job.ItemDefinitions.ToArray();
        selectedItemIndex = Math.Clamp(selectedItemIndex, 0, lines.Length - 1);
        UiTone.Wrapped(UiTone.Info, $"{lines.Length} item{(lines.Length == 1 ? "" : "s")} in this job · processed from top to bottom");
        if (ImGui.BeginCombo("Edit item", $"{selectedItemIndex + 1}. {ItemLabel(lines[selectedItemIndex])}"))
        {
            for (var index = 0; index < lines.Length; index++)
                if (ImGui.Selectable($"{index + 1}. {ItemLabel(lines[index])}##{lines[index].Id}", index == selectedItemIndex))
                { selectedItemIndex = index; itemSearch = ""; }
            ImGui.EndCombo();
        }
        if (ImGui.Button("Add another item"))
        {
            var previous = lines[selectedItemIndex];
            job.AdditionalItems.Add(new TransferJobDefinition
            {
                Name = job.Definition.Name, QuantityMode = previous.QuantityMode,
                Sources = [.. previous.Sources],
                Recipients = previous.Recipients.Select(x => new RecipientRule { CharacterId = x.CharacterId, Quantity = x.Quantity }).ToList()
            });
            selectedItemIndex = job.AdditionalItems.Count;
            itemSearch = "";
            notice = "New item added. Choose its item, then review the copied providers, recipients and amounts before running.";
            changed = true;
            lines = job.ItemDefinitions.ToArray();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(lines.Length == 1);
        if (ImGui.Button("Remove this item"))
        {
            if (selectedItemIndex == 0)
            {
                var jobId = job.Definition.Id;
                var jobName = job.Definition.Name;
                job.Definition = job.AdditionalItems[0];
                job.AdditionalItems.RemoveAt(0);
                job.Definition.Id = jobId;
                job.Definition.Name = jobName;
            }
            else job.AdditionalItems.RemoveAt(selectedItemIndex - 1);
            amountInputs.Clear();
            lines = job.ItemDefinitions.ToArray();
            selectedItemIndex = Math.Min(selectedItemIndex, lines.Length - 1);
            itemSearch = "";
            changed = true;
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped("Each item has its own providers, recipient priorities and quantities. The meeting point below applies to the whole job.");
        if (lines.GroupBy(x => new ItemKey(x.ItemId, x.HighQuality)).Any(group => group.Key.ItemId != 0 && group.Count() > 1))
            UiTone.Wrapped(UiTone.Error, "Duplicate item: keep each item/quality combination on one line. Edit or remove the duplicate before running.");
        ImGui.Separator();
        return lines[selectedItemIndex];
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
            ImGui.TextWrapped("Cached slot counts can be stale. Characters with unknown counts are not selected automatically; refresh characters in step 1 after AutoRetainer updates an alt. Actual capacity is checked before cargo moves.");
        }
    }

    private static string ItemLabel(TransferJobDefinition definition) => definition.ItemId == 0
        ? "Choose an item" : ItemName(definition.ItemId) + (definition.HighQuality ? " HQ" : "");

    private void DrawReview()
    {
        var job = SelectedJob;
        if (job == null)
        {
            ImGui.TextWrapped("Create a transfer job in step 2 first.");
            if (ImGui.Button("Set up transfer")) page = 1;
            return;
        }
        var definitions = job.ItemDefinitions.ToArray();
        ImGui.Spacing();
        UiTone.Wrapped(UiTone.Info, job.Definition.Name);
        ImGui.TextWrapped($"{definitions.Length} item{(definitions.Length == 1 ? "" : "s")} will run in order. Each item has its own inventory lookup and progress.");
        for (var itemIndex = 0; itemIndex < definitions.Length; itemIndex++)
        {
            var definition = definitions[itemIndex];
            ImGui.PushID(definition.Id.ToString());
            if (ImGui.CollapsingHeader($"{itemIndex + 1}. {ItemLabel(definition)} · {ModeName(definition.QuantityMode)}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.TextWrapped($"Collect from {definition.Sources.Count} provider{(definition.Sources.Count == 1 ? "" : "s")}. Fill these recipients in priority order:");
                foreach (var recipient in definition.Recipients.Select((value, index) => (value, index)))
                {
                    var amount = recipient.value.Quantity;
                    var action = definition.QuantityMode switch
                    {
                        JobQuantityMode.RestockToTotal => $"Top up bags + retainers to {amount:N0}",
                        JobQuantityMode.DeliverAmount => $"Send {amount:N0} additional items",
                        _ => amount.HasValue ? $"Send up to {amount:N0} items" : "Send all remaining eligible items"
                    };
                    UiTone.Wrapped(UiTone.Info, $"{recipient.index + 1}. {CharacterName(recipient.value.CharacterId)}: {action}.");
                }
                if (ImGui.CollapsingHeader("Provider names"))
                    foreach (var source in definition.Sources) ImGui.TextWrapped(CharacterName(source));
            }
            ImGui.PopID();
        }
        ImGui.TextWrapped("Providers use carried items first, then automatically refill from retainers. Recipients deposit when full and return to continue. A shortage for one item is reported separately while other items continue.");
        UiTone.Wrapped(UiTone.Warning, "Characters missing from the roster or without usable cached inventories are skipped for that item. Their requests remain unfulfilled; Activity lists the reasons while the available work continues.");
        DrawAutomaticRelayInfo();
        ImGui.TextUnformatted("Meeting location · world chosen automatically");
        DrawLocation(job.Meeting, worldIndependent: true);
        ImGui.TextWrapped("Recipients and relays collect on provider home worlds, grouped by DC and world for each recipient and item. Relay deliveries happen on the final recipient's home world so deposits stay local. Recipient priority, full relays and later items can require another DC visit. Enable DC travel in Lifestream on both PCs when needed. Only characters with assigned work log in.");
        ImGui.Separator();
        ImGui.TextUnformatted("Ready to run?");
        var scanBlockers = GetScanBlockers(job);
        var transferBlockers = GetTransferBlockers(job);
        ReadinessLine(Service.IsHost && Service.Connected && Service.RemoteRoster != null, "PCs connected; this PC is coordinator", "Connect both PCs in step 1; host on this PC.");
        ReadinessLine(Service.RemoteRoster is { ControlEnabled: true }, "Other PC allows control", "Enable 'Allow the coordinator to run jobs' on the other PC.");
        ReadinessLine(definitions.All(definition => definition.Sources.Count > 0 && definition.Recipients.Count > 0 && !HasInvalidAmounts(definition)), "Every item has providers, recipients and amounts", "Choose providers, recipients and positive amounts for every item in step 2.");
        ReadinessLine(job.Meeting.TerritoryId != 0 && job.Meeting.AetheryteId != 0, "Meeting point recorded", "Record an outdoor meeting point in step 2 before starting transfers.");
        if (scanBlockers.Count > 0 || transferBlockers.Count > 0)
        {
            ImGui.Spacing();
            foreach (var reason in scanBlockers.Concat(transferBlockers).Distinct()) UiTone.Wrapped(UiTone.Error, $"To do: {reason}");
        }
        ImGui.Spacing();
        if (ImGui.Button("Check required plugins on this PC")) plugin.QueueAction(CheckDependencies);
        UiTone.Status(dependencySummary);
        ImGui.TextWrapped("Before running: check required plugins on both PCs, pause AutoRetainer multi mode and let its tasks finish. Close existing trade, retainer and confirmation dialogs.");
        ImGui.Separator();
        UiTone.Wrapped(UiTone.Info, "Inventory lookup reads each PC's Allagan Tools cache. It never changes characters, travels or opens retainers.");
        ImGui.TextWrapped("Cached counts, including empty slots, are used as reported. A character with missing or incomplete data is skipped for that item, with the reason shown in Activity. The remaining characters continue.");
        ImGui.BeginDisabled(scanBlockers.Count > 0);
        if (ImGui.Button("Check inventories only", new Vector2(230, 34)))
        {
            plugin.QueueAction(() => Service.ScanJob(job));
            page = 3;
        }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(scanBlockers.Count > 0 || transferBlockers.Count > 0);
        if (ImGui.Button("Start transfer", new Vector2(230, 34)))
        {
            plugin.QueueAction(() => Service.StartJob(job));
            page = 3;
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped("Start transfer logs in only for the planned work. There is no second full inventory scan after login. Restock amounts use cached totals too; actual carried stock and receiving space are checked when moving items. You can pause at any time.");
        if (ImGui.Button("Back: edit setup")) page = 1;
        if (ImGui.CollapsingHeader("Optional: cached inventory preview"))
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
            if (ImGui.Button("Read Allagan Tools hints"))
            {
                var item = definition.ItemId;
                var highQuality = definition.HighQuality;
                plugin.QueueAction(() => Service.RefreshInventoryHints(item, highQuality));
            }
            ImGui.EndDisabled();
            ImGui.TextWrapped("This quick preview lists known item holders. Jobs use saved character and retainer counts; characters with unavailable data are skipped with a notice, without a live scan.");
            DrawHints();
        }
    }

    private List<string> GetScanBlockers(SavedTransferJob job)
    {
        var reasons = new List<string>();
        if (LocalActionRunning) reasons.Add("Pause the current action before starting another job.");
        if (HasUnfinishedJob) reasons.Add("Resume the saved job, or resolve and end it before starting a new one.");
        if (!Service.IsHost || !Service.Connected || Service.RemoteRoster == null) reasons.Add("Connect both PCs with this PC hosting in step 1.");
        if (Service.RemoteRoster is not { ControlEnabled: true }) reasons.Add("Allow coordinator control on the other PC in step 1.");
        var definitions = job.ItemDefinitions.ToArray();
        if (definitions.Any(x => x.Id == Guid.Empty) || definitions.Select(x => x.Id).Distinct().Count() != definitions.Length)
            reasons.Add("This saved setup has missing or repeated item identities. Recreate its item lines in a new job before running.");
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            var label = $"Item {index + 1} ({ItemLabel(definition)})";
            if (definition.Sources.Count == 0) reasons.Add($"{label}: select at least one provider in step 2.");
            if (definition.Recipients.Count == 0) reasons.Add($"{label}: add at least one recipient in step 2.");
            if (HasInvalidAmounts(definition)) reasons.Add($"{label}: fix the highlighted quantity fields; use positive whole numbers.");
            var item = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(definition.ItemId);
            if (item == null || definition.ItemId == 0 || item.Value.IsUntradable) reasons.Add($"{label}: choose a tradable item in step 2.");
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
            reasons.Add("A full transfer needs a recorded meeting point and arrival aetheryte in step 2. You can still scan inventories.");
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
            ImGui.TextWrapped(CoordinatorRole ? "No transfer run yet. Set up a job, then review and start it in step 3." : ReadyConnection && Settings.AllowRemoteControl ? "Connected — ready for coordinator. Start the job on your other PC; this PC reports its actions here." : "Connect and allow coordinator control in step 1 to receive jobs.");
            UiTone.Status(Service.Status);
            return;
        }
        ImGui.TextUnformatted(run.Job.Definition.Name);
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
            foreach (var participant in new[] { preparation.Sender, preparation.Receiver })
                UiTone.Wrapped(participant.Stage == ParticipantPreparationStage.Ready ? UiTone.Good : UiTone.Info,
                    $"{PeerName(participant.PeerId)} · {CharacterName(participant.CharacterId)}: {WorkflowService.PreparationStageLabel(participant.Stage)}");
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
        if (ImGui.CollapsingHeader("Current operations", ImGuiTreeNodeFlags.DefaultOpen))
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
        if (ImGui.Button("Copy diagnostics", new Vector2(230, 32)))
        {
            Interlocked.Exchange(ref diagnosticCopyPending, 1);
            plugin.QueueAction(() =>
            {
                try { Volatile.Write(ref diagnosticsToCopy, Service.CaptureDiagnosticReport()); }
                finally { Interlocked.Exchange(ref diagnosticCopyPending, 0); }
            });
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped("Detailed logging is automatic. If a step gets stuck, copy diagnostics on that PC before pausing or closing the game menu. The report includes recent steps, menu labels and plugin versions; it excludes pairing keys and chat.");
        ImGui.Separator();
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
