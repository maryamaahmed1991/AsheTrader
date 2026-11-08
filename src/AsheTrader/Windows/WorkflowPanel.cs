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
    private string itemSearch = string.Empty;
    private bool revealKey;
    private bool confirmedOutcome;
    private Guid? reviewedTradeId;
    private string dependencies = "Dependency state has not been checked on this PC.";
    private string notice = string.Empty;
    private List<(uint Id, string Name)>? itemCatalog;
    private readonly Dictionary<string, string> amountInputs = [];
    private WorkflowService Service => plugin.Workflow;
    private WorkflowSettings Settings => plugin.Config.Workflow;
    private bool HasUnfinishedJob => Service.Run is { Step: not CoordinatorStep.Finished };
    private bool LocalActionRunning => Service.Busy || plugin.Trader.IsRunning || plugin.Fuel.Active;
    private bool EditingLocked => LocalActionRunning || HasUnfinishedJob;

    public void Draw()
    {
        if (Service.Run is not { Paused: true, TradeOperationId: not null })
        { confirmedOutcome = false; reviewedTradeId = null; }
        ImGui.PushID("AsheWorkflowPanel");
        ImGui.Spacing();
        ImGui.TextWrapped(Service.ConnectionStatus);
        ImGui.TextWrapped(Service.Status);
        if (notice.Length > 0) ImGui.TextWrapped(notice);
        DrawRunControls();
        ImGui.Separator();
        if (ImGui.BeginTabBar("WorkflowTabs"))
        {
            if (ImGui.BeginTabItem("Pair PCs")) { DrawPairing(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Job setup")) { DrawJobs(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Retainer storage")) { DrawStorage(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Progress")) { DrawProgress(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
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
        ImGui.TextWrapped("Choose one PC as coordinator. Generate its pairing key, copy it to the other PC, then connect the worker to the coordinator's LAN IPv4 address. Pairing starts only when you press Host or Connect.");
        var changed = false;
        ImGui.BeginDisabled(Service.PairingEnabled || LocalActionRunning);
        var name = Settings.PeerName;
        if (ImGui.InputText("This PC's label", ref name, 80)) { Settings.PeerName = name; changed = true; }
        var bind = Settings.BindAddress;
        if (ImGui.InputText("Coordinator LAN address", ref bind, 64)) { Settings.BindAddress = bind.Trim(); changed = true; }
        var connect = Settings.ConnectAddress;
        if (ImGui.InputText("Connect to coordinator", ref connect, 64)) { Settings.ConnectAddress = connect.Trim(); changed = true; }
        var port = Settings.Port;
        if (ImGui.InputInt("Port", ref port)) { Settings.Port = Math.Clamp(port, 1, ushort.MaxValue); changed = true; }
        ImGui.BeginDisabled(HasUnfinishedJob);
        var key = Settings.PairingKey;
        if (ImGui.InputText("Pairing key", ref key, 128, revealKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password))
        { Settings.PairingKey = key.Trim(); changed = true; }
        if (ImGui.Button("Generate pairing key")) { Settings.PairingKey = WorkflowService.NewPairingKey(); changed = true; }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(Settings.PairingKey.Length == 0);
        if (ImGui.Button("Copy key")) ImGui.SetClipboardText(Settings.PairingKey);
        ImGui.EndDisabled();
        ImGui.Checkbox("Show pairing key", ref revealKey);
        if (changed) plugin.Save();

        ImGui.BeginDisabled(Service.PairingEnabled || LocalActionRunning || Settings.PairingKey.Length == 0);
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(Settings.BindAddress));
        if (ImGui.Button("Host as coordinator", new Vector2(185, 30))) plugin.QueueAction(Service.Host);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(Settings.ConnectAddress) || HasUnfinishedJob);
        if (ImGui.Button("Connect as worker", new Vector2(185, 30))) plugin.QueueAction(Service.Connect);
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!Service.PairingEnabled);
        if (ImGui.Button("Disconnect")) plugin.QueueAction(Service.Disconnect);
        ImGui.EndDisabled();

        var allow = Settings.AllowRemoteControl;
        if (ImGui.Checkbox("Allow the paired coordinator to run jobs on this PC", ref allow))
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
        ImGui.TextWrapped("Enable this on the worker. The coordinator controls only characters in this PC's imported roster.");
        ImGui.TextWrapped("Both PCs need AutoRetainer for relogging, Lifestream for travel and inn access, and vnavmesh for walking. Pause AutoRetainer's own automation before starting. These checks report this PC only; check the other PC separately.");
        if (ImGui.Button("Check local dependencies")) plugin.QueueAction(CheckDependencies);
        ImGui.TextWrapped(dependencies);
        if (Settings.ExpectedRemotePeerId.HasValue)
        {
            ImGui.TextDisabled("The paired PC's identity is remembered.");
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
        ImGui.BeginDisabled(Service.Busy);
        if (ImGui.Button("Import / refresh both rosters")) plugin.QueueAction(Service.RefreshRosters);
        ImGui.EndDisabled();
        ImGui.TextUnformatted($"This PC: {plugin.Config.Characters.Count} known characters");
        var remote = Service.RemoteRoster;
        ImGui.TextUnformatted(remote == null ? "Other PC: connect to load its roster" : $"{remote.Name}: {remote.Characters.Count} characters · control {(remote.ControlEnabled ? "enabled" : "disabled")}");
        if (ImGui.CollapsingHeader("Known characters"))
            foreach (var entry in Service.AllCharacters())
                ImGui.TextUnformatted($"{CharacterName(entry.Character)} — {PeerName(entry.PeerId)}");
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
            job = new SavedTransferJob { Definition = new TransferJobDefinition { Name = $"Transfer job {Settings.Jobs.Count + 1}", ItemId = 10155 } };
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
        if (job == null) return;
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
        if (ImGui.CollapsingHeader("1. Source characters and retainers", ImGuiTreeNodeFlags.DefaultOpen))
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
            ImGui.TextWrapped("All stored retainer inventories are included. Discovery verifies each selected character before allocation.");
        }

        if (ImGui.CollapsingHeader("2. Recipients in priority order", ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (var index = 0; index < definition.Recipients.Count; index++)
            {
                var recipient = definition.Recipients[index];
                var priorityChanged = false;
                ImGui.PushID($"recipient{recipient.CharacterId}");
                ImGui.TextUnformatted($"{index + 1}. {CharacterName(recipient.CharacterId)}");
                ImGui.SameLine();
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
                    if (ImGui.InputText(definition.QuantityMode == JobQuantityMode.RestockToTotal ? "Stock target" : "Amount / cap", ref amount, 24, ImGuiInputTextFlags.CharsDecimal))
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
                        var recipient = new RecipientRule { CharacterId = entry.Character.ContentId, Quantity = definition.QuantityMode == JobQuantityMode.MoveAll && remainderIndex < 0 ? null : 1 };
                        if (remainderIndex >= 0) definition.Recipients.Insert(remainderIndex, recipient);
                        else definition.Recipients.Add(recipient);
                        changed = true;
                    }
                ImGui.EndCombo();
            }
            ImGui.TextWrapped("The remainder recipient stays last. To move it earlier, turn off 'Take the remainder' and set its cap first. New recipients are inserted before an existing remainder recipient.");
        }

        if (ImGui.CollapsingHeader("3. Relay characters"))
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
        }

        if (ImGui.CollapsingHeader("4. Meeting point", ImGuiTreeNodeFlags.DefaultOpen))
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
        ImGui.BeginDisabled(Service.Busy);
        if (ImGui.Button("Read Allagan Tools inventory hints"))
        {
            var item = definition.ItemId; var highQuality = definition.HighQuality;
            plugin.QueueAction(() => Service.RefreshInventoryHints(item, highQuality));
        }
        ImGui.EndDisabled();
        ImGui.TextWrapped("Cached inventory hints appear in Progress. Missing hints mean unknown stock; every retainer is still visited during discovery.");
        ImGui.BeginDisabled(EditingLocked || !Service.IsHost || !Service.Connected || Service.RemoteRoster is not { ControlEnabled: true } || HasInvalidAmounts(definition));
        if (ImGui.Button("Scan inventories only", new Vector2(220, 34)))
        {
            var chosen = job;
            plugin.QueueAction(() => Service.ScanJob(chosen));
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(job.Meeting.WorldId == 0 || job.Meeting.AetheryteId == 0);
        if (ImGui.Button("Start this transfer job", new Vector2(230, 34)))
        {
            var chosen = job;
            plugin.QueueAction(() => Service.StartJob(chosen));
        }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.TextWrapped("Start with an inventory scan on both PCs. It visits the selected characters and retainers without trading. Full transfers also require a recorded meeting point.");
        if (!Service.IsHost) ImGui.TextWrapped("Start jobs on the coordinator PC after pairing.");
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
        var hq = definition.HighQuality;
        if (ImGui.Checkbox("High quality only", ref hq)) { definition.HighQuality = hq; changed = true; }
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
        DrawHints();
        var run = Service.Run;
        if (run == null) { ImGui.TextWrapped("Start a job to discover inventories and build its delivery plan. The worker PC reports its current operation above."); return; }
        ImGui.TextUnformatted(run.Job.Definition.Name);
        ImGui.TextUnformatted($"{ItemName(run.Job.Definition.ItemId)}{(run.Job.Definition.HighQuality ? " HQ" : "")} · {ModeName(run.Job.Definition.QuantityMode)}");
        ImGui.TextWrapped(run.Status);
        if (run.Journal == null)
            ImGui.TextUnformatted($"Discovery: {run.Snapshots.Count}/{run.DiscoveryOrder.Count} characters verified");
        else
        {
            try
            {
                var ledger = new TransferJobLedger(run.Journal);
                ImGui.TextUnformatted($"Verified trade batches: {run.Journal.Operations.Count(x => x.State == TransferOperationState.Committed)}");
                foreach (var recipient in run.Journal.Plan.Definition.Recipients)
                {
                    var assigned = run.Journal.Plan.Allocations.Where(x => x.RecipientCharacterId == recipient.CharacterId).Aggregate(0UL, (sum, x) => sum + x.Quantity);
                    ImGui.TextUnformatted($"{CharacterName(recipient.CharacterId)}: {ledger.DeliveredTo(recipient.CharacterId):N0} / {assigned:N0} allocated items delivered");
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
        if (ImGui.CollapsingHeader("Current operations", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (run.Pending.Count == 0) ImGui.TextUnformatted("No pending worker command.");
            foreach (var pending in run.Pending)
            {
                var command = pending.Command;
                ImGui.TextUnformatted($"{PeerName(pending.PeerId)} · {CharacterName(command.Operation.CharacterId)} · {(command.Kind == WorkerCommandKind.Game ? command.Operation.Kind.ToString() : command.Kind.ToString())}");
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
        string mesh;
        try { mesh = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc() ? "navigation mesh ready" : "navigation mesh not ready in this territory"; }
        catch (Exception) { mesh = "vnavmesh unavailable or its IPC is not ready"; }
        dependencies = $"AutoRetainer: {(ar.Ready ? $"available; busy={ar.Busy?.ToString() ?? "unknown"}, multi mode={ar.MultiModeEnabled?.ToString() ?? "unknown"}" : ar.Error)}. Lifestream: {(ls.Ready ? $"available; busy={ls.Busy?.ToString() ?? "unknown"}" : ls.Error)}. {mesh}. Checked {DateTime.Now:T}.";
    }
}
