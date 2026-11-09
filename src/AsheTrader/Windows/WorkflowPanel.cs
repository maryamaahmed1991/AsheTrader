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
    private int page;
    private bool? coordinatorRole;
    private string itemSearch = string.Empty;
    private bool revealKey;
    private bool confirmedOutcome;
    private Guid? reviewedTradeId;
    private string dependencies = "Dependency state has not been checked on this PC.";
    private string dependencySummary = "Required plugins have not been checked on this PC yet.";
    private string notice = string.Empty;
    private List<(uint Id, string Name)>? itemCatalog;
    private readonly Dictionary<string, string> amountInputs = [];
    private WorkflowService Service => plugin.Workflow;
    private WorkflowSettings Settings => plugin.Config.Workflow;
    private bool HasUnfinishedJob => Service.Run is { Step: not CoordinatorStep.Finished };
    private bool LocalActionRunning => Service.Busy || plugin.Trader.IsRunning || plugin.Fuel.Active;
    private bool EditingLocked => LocalActionRunning || HasUnfinishedJob;
    private bool ReadyConnection => Service.Connected && Service.RemoteRoster != null;
    private bool CoordinatorRole => HasUnfinishedJob || (Service.PairingEnabled ? Service.IsHost : coordinatorRole ?? !Settings.AllowRemoteControl);
    private SavedTransferJob? SelectedJob => Settings.Jobs.FirstOrDefault(x => x.Definition.Id == selectedJob) ?? Settings.Jobs.FirstOrDefault();

    public void Draw()
    {
        if (Service.Run is not { Paused: true, TradeOperationId: not null })
        { confirmedOutcome = false; reviewedTradeId = null; }
        ImGui.PushID("AsheWorkflowPanel");
        ImGui.Spacing();
        ImGui.TextWrapped(ReadyConnection
            ? $"Connected to {Service.RemoteRoster?.Name ?? "paired PC"} · {(Service.IsHost ? "This PC controls transfers" : Settings.AllowRemoteControl ? "Ready for coordinator" : "Enable control below to receive jobs")}"
            : Service.Connected ? "Authenticating the paired PC and loading its characters..." : Service.PairingEnabled ? "Waiting for the other PC to connect..." : "Connect your two PCs to get started.");
        if (LocalActionRunning || HasUnfinishedJob) ImGui.TextWrapped(Service.Status);
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
                ImGui.TextWrapped(notice);
                if (ImGui.SmallButton("Dismiss message")) notice = string.Empty;
                ImGui.Separator();
            }
            switch (page)
            {
                case 0: DrawPairing(); break;
                case 1: DrawJobs(); break;
                case 2: DrawReview(); break;
                default: DrawProgress(); DrawActivityLog(); break;
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
            ImGui.BeginDisabled(!Service.IsHost || !Service.Connected || Service.Busy);
            if (ImGui.Button("Resume / reconcile", new Vector2(180, 30))) { confirmedOutcome = false; plugin.QueueAction(Service.Resume); }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(LocalActionRunning);
            if (ImGui.Button("End paused job")) plugin.QueueAction(Service.EndPausedJob);
            ImGui.EndDisabled();
            ImGui.TextWrapped("Resume checks saved outcomes before continuing. Ending is blocked while a trade or relay cargo is unresolved.");
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
        if (HasUnfinishedJob) ImGui.TextWrapped("This PC has an unfinished coordinator job. Reconnect it as the coordinator to resume or resolve that job.");
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
                ImGui.TextWrapped(Service.DiscoveryStatus);
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
                ImGui.TextWrapped(Settings.AllowRemoteControl ? "Connected — ready for coordinator. Leave this PC running; start the job on the other PC." : "Connected — waiting for you to allow control above.");
        }
        if (Service.PairingEnabled)
        {
            if (ImGui.Button("Disconnect PCs")) plugin.QueueAction(Service.Disconnect);
        }
        ImGui.TextWrapped(Service.ConnectionStatus);
        ImGui.TextWrapped("Both PCs must be on the same local network for automatic discovery. Keep the coordinator hosting while connecting.");
        ImGui.Separator();
        ImGui.TextUnformatted("Characters available for jobs");
        ImGui.TextWrapped($"{Settings.PeerName}: {plugin.Config.Characters.Count} characters");
        var remote = Service.RemoteRoster;
        ImGui.TextWrapped(remote == null ? "Other PC: waiting for its character list" : $"{remote.Name}: {remote.Characters.Count} characters · control {(remote.ControlEnabled ? "enabled" : "disabled")}");
        ImGui.BeginDisabled(LocalActionRunning);
        if (ImGui.Button("Refresh characters on both PCs")) plugin.QueueAction(Service.RefreshRosters);
        ImGui.EndDisabled();
        ImGui.TextWrapped(Service.RosterStatus);
        ImGui.TextWrapped("Characters come from AutoRetainer on each PC. Refresh after connecting or configuring more characters.");
        if (ImGui.CollapsingHeader("View known characters"))
            foreach (var entry in Service.AllCharacters())
                ImGui.TextWrapped($"{CharacterName(entry.Character)} — {PeerName(entry.PeerId)}");
        ImGui.Separator();
        if (ImGui.Button("Check required plugins on this PC")) plugin.QueueAction(CheckDependencies);
        ImGui.TextWrapped(dependencySummary);
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
        ImGui.TextWrapped(dependencies);
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
                { selectedJob = candidate.Definition.Id; job = candidate; }
            ImGui.EndCombo();
        }
        ImGui.BeginDisabled(EditingLocked);
        if (ImGui.Button("New transfer job"))
        {
            job = new SavedTransferJob { Definition = new TransferJobDefinition { Name = $"Transfer job {Settings.Jobs.Count + 1}", ItemId = 10155, QuantityMode = JobQuantityMode.DeliverAmount } };
            Settings.Jobs.Add(job); selectedJob = job.Definition.Id; plugin.Save();
        }
        if (job != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete selected setup"))
            {
                Settings.Jobs.Remove(job);
                selectedJob = Guid.Empty;
                job = Settings.Jobs.FirstOrDefault();
                plugin.Save();
            }
        }
        ImGui.EndDisabled();
        if (EditingLocked) ImGui.TextWrapped("Job and storage settings are locked while a run is unfinished. Pause to inspect progress; end a resolved job to edit its setup.");
        if (job == null)
        {
            ImGui.TextWrapped("Create a transfer job, choose an item and its providers, then add recipients in the order you want them filled. New jobs start with a small, fixed delivery amount.");
            return;
        }
        var changed = false;
        ImGui.BeginDisabled(EditingLocked);
        var definition = job.Definition;
        var name = definition.Name;
        if (ImGui.InputText("Job name", ref name, 100)) { definition.Name = name; changed = true; }
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
            if (roster.Length == 0) ImGui.TextWrapped("No characters yet. Connect both PCs and refresh characters in step 1.");
            ImGui.TextWrapped("Providers use carried items first, then automatically return to their retainers for more when their bags run out. The job checks bags and every retainer before planning.");
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
                        ImGui.TextWrapped("Enter a positive whole number; the last valid amount remains saved.");
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

        if (ImGui.CollapsingHeader("Optional: relay for characters on the same account"))
        {
            ImGui.TextWrapped("A transfer between characters on the same account uses a relay on the other PC. Select one available relay for each account that may receive temporary cargo.");
            foreach (var group in roster.GroupBy(x => x.PeerId))
            {
                var relay = definition.RelayCharacters.GetValueOrDefault(group.Key);
                if (ImGui.BeginCombo($"Relay on {PeerName(group.Key)}##{group.Key}", relay == 0 ? "No relay selected" : CharacterName(relay)))
                {
                    if (ImGui.Selectable("No relay", relay == 0)) { definition.RelayCharacters.Remove(group.Key); changed = true; }
                    foreach (var entry in group)
                        if (ImGui.Selectable(CharacterName(entry.Character), relay == entry.Character.ContentId))
                        { definition.RelayCharacters[group.Key] = entry.Character.ContentId; changed = true; }
                    ImGui.EndCombo();
                }
            }
            foreach (var orphan in definition.RelayCharacters.Where(x => roster.All(character => character.PeerId != x.Key)).ToArray())
            {
                ImGui.TextWrapped($"Saved relay {CharacterName(orphan.Value)} belongs to a PC that is not connected.");
                if (ImGui.SmallButton($"Remove this saved relay##{orphan.Key}")) { definition.RelayCharacters.Remove(orphan.Key); changed = true; }
            }
        }

        if (ImGui.CollapsingHeader("Meeting point: where the PCs trade", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped("Stand at an outdoor location both accounts can reach, then record it. Choose a main aetheryte in that territory for the return trip.");
            if (ImGui.Button("Record my current meeting point"))
            {
                var jobId = definition.Id;
                plugin.QueueAction(() => CaptureMeeting(jobId));
            }
            DrawLocation(job.Meeting);
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

    private void DrawReview()
    {
        var job = SelectedJob;
        if (job == null)
        {
            ImGui.TextWrapped("Create a transfer job in step 2 first.");
            if (ImGui.Button("Set up transfer")) page = 1;
            return;
        }
        var definition = job.Definition;
        ImGui.Spacing();
        ImGui.TextWrapped(job.Definition.Name);
        ImGui.TextUnformatted($"{ItemName(definition.ItemId)}{(definition.HighQuality ? " HQ" : "")} · {ModeName(definition.QuantityMode)}");
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
            ImGui.TextWrapped($"{recipient.index + 1}. {CharacterName(recipient.value.CharacterId)}: {action}.");
        }
        ImGui.TextWrapped("Providers use carried items first, then automatically refill from retainers when they run out. Recipients deposit when full and return to keep trading.");
        if (ImGui.CollapsingHeader("Review provider names and meeting location"))
        {
            foreach (var source in definition.Sources) ImGui.TextWrapped(CharacterName(source));
            DrawLocation(job.Meeting);
            foreach (var relay in definition.RelayCharacters)
                ImGui.TextWrapped($"Relay on {PeerName(relay.Key)}: {CharacterName(relay.Value)}");
        }
        ImGui.Separator();
        ImGui.TextUnformatted("Ready to run?");
        var scanBlockers = GetScanBlockers(job);
        var transferBlockers = GetTransferBlockers(job);
        ReadinessLine(Service.IsHost && Service.Connected && Service.RemoteRoster != null, "PCs connected; this PC is coordinator", "Connect both PCs in step 1; host on this PC.");
        ReadinessLine(Service.RemoteRoster is { ControlEnabled: true }, "Other PC allows control", "Enable 'Allow the coordinator to run jobs' on the other PC.");
        ReadinessLine(definition.Sources.Count > 0 && definition.Recipients.Count > 0 && !HasInvalidAmounts(definition), "Providers, recipients and amounts selected", "Choose providers, recipients and positive amounts in step 2.");
        ReadinessLine(job.Meeting.WorldId != 0 && job.Meeting.TerritoryId != 0 && job.Meeting.AetheryteId != 0, "Meeting point recorded", "Record an outdoor meeting point in step 2 before starting transfers.");
        if (scanBlockers.Count > 0 || transferBlockers.Count > 0)
        {
            ImGui.Spacing();
            foreach (var reason in scanBlockers.Concat(transferBlockers).Distinct()) ImGui.TextWrapped($"• {reason}");
        }
        ImGui.Spacing();
        if (ImGui.Button("Check required plugins on this PC")) plugin.QueueAction(CheckDependencies);
        ImGui.TextWrapped(dependencySummary);
        ImGui.TextWrapped("Before running: check required plugins on both PCs, pause AutoRetainer multi mode and let its tasks finish. Close existing trade, retainer and confirmation dialogs.");
        ImGui.Separator();
        ImGui.TextWrapped("Inventory scan actively changes characters, travels to their home-world bells and opens each retainer. It does not move items. Record your meeting point before scanning, since the scan moves your character.");
        ImGui.BeginDisabled(scanBlockers.Count > 0);
        if (ImGui.Button("Scan inventories only", new Vector2(230, 34)))
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
        ImGui.TextWrapped("Start transfer scans current inventories again, builds the delivery plan, and then carries out trades. You can pause at any time.");
        if (ImGui.Button("Back: edit setup")) page = 1;
        if (ImGui.CollapsingHeader("Optional: cached inventory preview"))
        {
            ImGui.BeginDisabled(LocalActionRunning);
            if (ImGui.Button("Read Allagan Tools hints"))
            {
                var item = definition.ItemId;
                var highQuality = definition.HighQuality;
                plugin.QueueAction(() => Service.RefreshInventoryHints(item, highQuality));
            }
            ImGui.EndDisabled();
            ImGui.TextWrapped("Allagan Tools provides an optional cached preview. A missing entry means unknown stock. Every selected retainer is still visited during the live scan.");
            DrawHints();
        }
    }

    private List<string> GetScanBlockers(SavedTransferJob job)
    {
        var reasons = new List<string>();
        var definition = job.Definition;
        if (LocalActionRunning) reasons.Add("Pause the current action before starting another job.");
        if (HasUnfinishedJob) reasons.Add("Resume the saved job, or resolve and end it before starting a new one.");
        if (!Service.IsHost || !Service.Connected || Service.RemoteRoster == null) reasons.Add("Connect both PCs with this PC hosting in step 1.");
        if (Service.RemoteRoster is not { ControlEnabled: true }) reasons.Add("Allow coordinator control on the other PC in step 1.");
        if (definition.Sources.Count == 0) reasons.Add("Select at least one provider in step 2.");
        if (definition.Recipients.Count == 0) reasons.Add("Add at least one recipient in step 2.");
        if (HasInvalidAmounts(definition)) reasons.Add("Fix the highlighted quantity fields in step 2; use positive whole numbers.");
        var known = Service.AllCharacters().Select(x => x.Character.ContentId).ToHashSet();
        if (definition.Sources.Concat(definition.Recipients.Select(x => x.CharacterId)).Concat(definition.RelayCharacters.Values).Any(id => !known.Contains(id)))
            reasons.Add("Some selected characters are missing. Refresh characters in step 1 or update your selections.");
        if (definition.RelayCharacters.Any(relay => !Service.AllCharacters().Any(character => character.PeerId == relay.Key && character.Character.ContentId == relay.Value)))
            reasons.Add("A saved relay belongs to an unavailable or different PC. Update the relay section in step 2.");
        var item = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(definition.ItemId);
        if (item == null || item.Value.IsUntradable) reasons.Add("Choose a tradable item in step 2.");
        else if (definition.HighQuality && !item.Value.CanBeHq) reasons.Add("This item has no high-quality version. Turn off high quality in step 2.");
        if (definition.Sources.Count > 0 && definition.Recipients.Count > 0 && !HasInvalidAmounts(definition))
        {
            try { JobPlanner.ValidateDefinition(definition); }
            catch (ArgumentException exception) { reasons.Add($"Check the saved job setup: {exception.Message.Split('\n')[0]}"); }
        }
        return reasons;
    }

    private List<string> GetTransferBlockers(SavedTransferJob job)
    {
        var reasons = new List<string>();
        if (job.Meeting.WorldId == 0 || job.Meeting.TerritoryId == 0 || job.Meeting.AetheryteId == 0)
            reasons.Add("A full transfer needs a recorded meeting point and arrival aetheryte in step 2. You can still scan inventories.");
        var roster = Service.AllCharacters().ToArray();
        foreach (var group in roster.GroupBy(x => x.PeerId))
        {
            var ids = group.Select(x => x.Character.ContentId).ToHashSet();
            if (!job.Definition.Sources.Any(source => ids.Contains(source) && job.Definition.Recipients.Any(recipient => ids.Contains(recipient.CharacterId) && recipient.CharacterId != source))) continue;
            var otherPeers = roster.Select(x => x.PeerId).Distinct().Where(id => id != group.Key).ToArray();
            if (otherPeers.Length != 1 || !job.Definition.RelayCharacters.TryGetValue(otherPeers[0], out var relay) || !roster.Any(x => x.PeerId == otherPeers[0] && x.Character.ContentId == relay))
                reasons.Add($"Transfers between characters on {PeerName(group.Key)} need a relay on the other PC. Choose it under the relay section in step 2.");
        }
        return reasons;
    }

    private static void ReadinessLine(bool ready, string yes, string no)
    {
        ImGui.TextWrapped(ready ? $"Ready: {yes}" : $"To do: {no}");
    }
    private void DrawItemPicker(TransferJobDefinition definition, ref bool changed)
    {
        ImGui.TextUnformatted($"Item: {ItemName(definition.ItemId)}");
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
            if (!supportsHq) ImGui.TextWrapped("This item has no high-quality version. Turn off the saved HQ selection to continue.");
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
                if (retainers.Count == 0) ImGui.TextWrapped("Retainer choices appear after a verified discovery run. All retainers are permitted by default.");
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

    private void DrawProgress()
    {
        ImGui.Spacing();
        var run = CoordinatorRole ? Service.Run : null;
        if (run == null)
        {
            ImGui.TextWrapped(CoordinatorRole ? "No transfer run yet. Set up a job, then review and start it in step 3." : ReadyConnection && Settings.AllowRemoteControl ? "Connected — ready for coordinator. Start the job on your other PC; this PC reports its actions here." : "Connect and allow coordinator control in step 1 to receive jobs.");
            ImGui.TextWrapped(Service.Status);
            return;
        }
        ImGui.TextUnformatted(run.Job.Definition.Name);
        ImGui.TextUnformatted($"{ItemName(run.Job.Definition.ItemId)}{(run.Job.Definition.HighQuality ? " HQ" : "")} · {ModeName(run.Job.Definition.QuantityMode)}");
        ImGui.TextWrapped(run.Status);
        ImGui.TextWrapped(run.Step == CoordinatorStep.Finished
            ? run.Journal?.Plan.UnmetTargets.Count > 0
                ? "Run ended with unmet targets. Review the shortages below; create a new job for any remaining items."
                : "Run ended — inspect the results below before starting another job."
            : run.Paused ? "Paused — use Resume / reconcile above when ready." : StageName(run.Step));
        if (run.Journal == null)
        {
            ImGui.TextUnformatted($"Inventory scan: {run.Snapshots.Count}/{run.DiscoveryOrder.Count} characters verified");
            ImGui.ProgressBar(run.DiscoveryOrder.Count == 0 ? 0 : (float)run.Snapshots.Count / run.DiscoveryOrder.Count, new Vector2(-1, 18));
        }
        else
        {
            try
            {
                var ledger = new TransferJobLedger(run.Journal);
                ImGui.TextUnformatted($"Verified trade batches: {run.Journal.Operations.Count(x => x.State == TransferOperationState.Committed)}");
                foreach (var recipient in run.Journal.Plan.Definition.Recipients)
                {
                    var assigned = run.Journal.Plan.Allocations.Where(x => x.RecipientCharacterId == recipient.CharacterId).Aggregate(0UL, (sum, x) => sum + x.Quantity);
                    var delivered = ledger.DeliveredTo(recipient.CharacterId);
                    ImGui.TextWrapped($"{CharacterName(recipient.CharacterId)}: {delivered:N0} / {assigned:N0} planned items delivered");
                    ImGui.ProgressBar(assigned == 0 ? 1 : (float)((double)delivered / assigned), new Vector2(-1, 18));
                }
                foreach (var shortage in run.Journal.Plan.UnmetTargets)
                    ImGui.TextWrapped($"Unmet target: {CharacterName(shortage.CharacterId)} needs {shortage.Missing:N0} more than the discovered available stock.");
                foreach (var relay in run.Journal.Plan.Definition.RelayCharacters.Values)
                {
                    var cargo = ledger.RelayCargo(relay);
                    if (cargo > 0) ImGui.TextWrapped($"Relay cargo awaiting final delivery: {CharacterName(relay)} holds {cargo:N0} items.");
                }
            }
            catch (Exception exception) { ImGui.TextWrapped($"Journal requires attention: {exception.Message}"); }
        }
        foreach (var pickup in run.Withdrawn.Where(x => x.Value > 0))
            ImGui.TextWrapped($"Collected from provider retainers: {CharacterName(pickup.Key)} · {pickup.Value:N0} items");
        foreach (var deposit in run.Deposited.Where(x => x.Value > 0))
            ImGui.TextWrapped($"Stored in recipient retainers: {CharacterName(deposit.Key)} · {deposit.Value:N0} items");
        if (ImGui.CollapsingHeader("Current operations", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (run.Pending.Count == 0) ImGui.TextUnformatted("No pending worker command.");
            foreach (var pending in run.Pending)
            {
                var command = pending.Command;
                ImGui.TextWrapped($"{PeerName(pending.PeerId)} · {CharacterName(command.Operation.CharacterId)} · {OperationName(command)}");
                var response = pending.Response;
                ImGui.TextWrapped(response == null ? (pending.Ready ? "Receiver armed; awaiting the agreed trade." : "Waiting for the worker's observation.")
                    : response.Success ? $"Verified response: {response.Quantity:N0} items." : response.Error);
                if (response?.BeforeQuantity is not null || response?.CurrentQuantity is not null)
                    ImGui.TextUnformatted($"Recorded bag count: {response?.BeforeQuantity?.ToString("N0") ?? "unknown"}; current: {response?.CurrentQuantity?.ToString("N0") ?? "unknown"}");
                if (command.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch)
                    ImGui.TextUnformatted($"Expected trade: {command.Operation.Quantity:N0} items {(command.Kind == WorkerCommandKind.SendBatch ? "sent" : "received")}");
            }
            if (run.Paused && run.TradeOperationId != null)
            {
                ImGui.TextWrapped("This job has an unfinished trade. Compare the recorded observations on both PCs before any explicit reconciliation. Counts alone do not prove a completed trade.");
                if (reviewedTradeId != run.TradeOperationId) { reviewedTradeId = run.TradeOperationId; confirmedOutcome = false; }
                ImGui.Checkbox("I checked both characters and confirm this outcome", ref confirmedOutcome);
                ImGui.BeginDisabled(!confirmedOutcome || !Service.IsHost || !Service.Connected || Service.Busy);
                if (ImGui.Button("Confirmed transferred")) { confirmedOutcome = false; plugin.QueueAction(() => Service.ResolvePendingTrade(true)); }
                ImGui.SameLine();
                if (ImGui.Button("Confirmed no transfer")) { confirmedOutcome = false; plugin.QueueAction(() => Service.ResolvePendingTrade(false)); }
                ImGui.EndDisabled();
                ImGui.TextWrapped("These decisions reconcile the saved journal; they do not perform another trade. Both workers must report counts compatible with your decision.");
            }
            else { reviewedTradeId = null; confirmedOutcome = false; }
        }
        if (ImGui.CollapsingHeader("Verified discovery snapshots"))
        {
            ImGui.TextWrapped("These counts were observed during discovery. Delivery progress is tracked separately and remains valid after retainer deposits.");
            foreach (var snapshot in run.Snapshots.Values)
            {
                ImGui.TextUnformatted($"{snapshot.Name}@{snapshot.World}: bags {snapshot.BagQuantity:N0}; retainers {snapshot.Retainers.Aggregate(0UL, (sum, x) => sum + x.Quantity):N0} · {snapshot.Verification}");
                foreach (var retainer in snapshot.Retainers)
                    ImGui.TextDisabled($"  {retainer.Name}: {retainer.Quantity:N0} · {retainer.Verification}");
            }
        }
    }

    private void DrawActivityLog()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Recent activity", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (ImGui.Button("Copy activity for troubleshooting"))
            ImGui.SetClipboardText($"Ashe's Trader v0.2.2\n{Service.ConnectionStatus}\n{Service.Status}\n{dependencySummary}\n{string.Join("\n", plugin.Activity)}");
        if (plugin.Activity.Count == 0) ImGui.TextWrapped("Actions and outcomes will appear here when a job runs.");
        foreach (var entry in plugin.Activity.Reverse()) ImGui.TextWrapped(entry);
        DrawHints();
    }

    private static string StageName(CoordinatorStep step) => step switch
    {
        CoordinatorStep.DiscoverLogin or CoordinatorStep.DiscoverScan => "Scanning each selected character's bags and retainers",
        CoordinatorStep.Plan or CoordinatorStep.SelectLeg => "Planning the next delivery",
        CoordinatorStep.LoginSender or CoordinatorStep.LoginReceiver => "Logging in the characters for this delivery",
        CoordinatorStep.InspectSender or CoordinatorStep.InspectReceiver => "Checking carried items and available inventory space",
        CoordinatorStep.Withdraw => "Provider refill: collecting the next load from retainers",
        CoordinatorStep.Deposit => "Recipient storage: making room in bags before continuing",
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

    private void DrawLocation(TravelLocation location)
    {
        if (location.WorldId == 0) { ImGui.TextUnformatted("No location recorded."); return; }
        var world = Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault(location.WorldId)?.Name.ToString() ?? location.WorldId.ToString();
        var territory = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(location.TerritoryId)?.PlaceName.Value.Name.ToString() ?? location.TerritoryId.ToString();
        ImGui.TextWrapped($"{world} · {territory} · ({location.X:F1}, {location.Y:F1}, {location.Z:F1})");
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
        var issues = new List<string>();
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
        dependencies = $"AutoRetainer: {(ar.Ready ? $"available; busy={ar.Busy?.ToString() ?? "unknown"}, multi mode={ar.MultiModeEnabled?.ToString() ?? "unknown"}" : ar.Error)}. Lifestream: {(ls.Ready ? $"available; busy={ls.Busy?.ToString() ?? "unknown"}" : ls.Error)}. {mesh}. Checked {DateTime.Now:T}.";
    }
}
