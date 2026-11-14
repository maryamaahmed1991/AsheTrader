using System.Net;
using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Core.Networking;
using AsheTrader.Workflow.Native;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Newtonsoft.Json;

namespace AsheTrader.Workflow;

/// <summary>All game work, message dispatch and journals are handled on the framework thread.</summary>
public sealed partial class WorkflowService : IDisposable
{
    private readonly Plugin plugin;
    private readonly LanPeer peer = new();
    private readonly GameWorkflowExecutor executor = new();
    private readonly SettingsFileStore workerFile;
    private readonly SettingsFileStore runFile;
    private Dictionary<Guid, WorkerRecord> workerRecords = [];
    private WorkerRecord? activeWorker;
    private Guid activeReplyPeer;
    private bool receiverReadySent;
    private bool host;
    private bool wasConnected;
    private Guid? connectionId;
    private Guid? acceptedPeerId;
    private long heartbeatAt;
    private long peerSeenAt;
    private bool disposed;
    private bool journalHealthy = true;
    private readonly Dictionary<Guid, (Guid Round, uint Quantity, long Since)> tradeTallySamples = [];
    private string error = "";
    public CoordinatorRun? Run { get; private set; }
    public PeerRoster? RemoteRoster { get; private set; }
    public string RosterStatus { get; private set; } = "Import characters from AutoRetainer to get started.";
    public bool Connected => peer.IsConnected;
    public bool PairingEnabled => peer.IsEnabled;
    public bool IsHost => host;
    public bool Busy => activeWorker != null || executor.IsBusy || Run is { Paused: false, Step: not CoordinatorStep.Finished };
    public bool RunningJob => Run is { Paused: false, Step: not CoordinatorStep.Finished };
    public bool ExpectsLogout => activeWorker?.Command.Kind == WorkerCommandKind.Game
        && activeWorker.Command.Operation.Kind is not (GameOperationKind.Inspect or GameOperationKind.ReadCachedInventory);
    public string ConnectionStatus => peer.Status;
    public string Status => error.Length > 0 ? error
        : Run != null && (host || Run.Step != CoordinatorStep.Finished) ? Run.Status
        : activeWorker != null && activeWorker.Command.Kind != WorkerCommandKind.Game ? plugin.Trader.Status
        : executor.Status;
    private WorkflowSettings Settings => plugin.Config.Workflow;
    private bool HasAcceptedConnection => peer.SessionIdentity is { } session && session.ConnectionId == connectionId
        && session.RemotePeerId == acceptedPeerId
        && (!selectedCoordinatorId.HasValue || selectedCoordinatorId == session.RemotePeerId)
        && (!Settings.ExpectedRemotePeerId.HasValue || Settings.ExpectedRemotePeerId == session.RemotePeerId);

    public WorkflowService(Plugin plugin)
    {
        this.plugin = plugin;
        executor.Diagnostic = RecordDiagnostic;
        workerFile = new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "worker-journal.json"));
        runFile = new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "transfer-job.json"));
        executor.PersistStorageCheckpoint = checkpoint =>
        {
            if (activeWorker?.Command.Id != checkpoint.OperationId)
                throw new InvalidOperationException("The storage movement has no active durable worker command.");
            activeWorker.StorageCheckpoint = Clone(checkpoint);
            SaveWorkers();
        };
        try
        {
            workerRecords = JsonConvert.DeserializeObject<Dictionary<Guid, WorkerRecord>>(workerFile.Load() ?? "{}") ?? [];
            Run = JsonConvert.DeserializeObject<CoordinatorRun>(runFile.Load() ?? "null");
            if (Run is { Step: not CoordinatorStep.Finished })
            {
                Run.Paused = true;
                Run.Status = "Saved job paused. Pair both clients and resume to reconcile pending work.";
                SaveRun();
            }
        }
        catch (Exception ex) { journalHealthy = false; error = $"Journal could not be loaded: {ex.Message}"; }
    }

    public void Host()
    {
        RequireIdle();
        if (PairingEnabled) throw new InvalidOperationException("Disconnect the current LAN session before starting another host.");
        var address = SelectHostAddress();
        if (string.IsNullOrWhiteSpace(Settings.PairingKey))
        {
            Settings.PairingKey = NewPairingKey();
            plugin.Save();
        }
        scanner.Stop();
        peer.StartHost(address, Settings.Port, Settings.PairingKey, Settings.PeerId);
        host = true;
        error = "";
        selectedCoordinatorId = null;
        advertisementError = "";
        if (!IPAddress.IsLoopback(address))
        {
            try { advertiser.StartAdvertising(address, peer.BoundPort, Settings.PeerId, Settings.PeerName); }
            catch (Exception ex)
            {
                advertisementError = $"Hosting is available, but nearby discovery could not start: {ex.Message} Use the manual connection fields if needed.";
                Plugin.Log.Warning(ex, "LAN discovery could not start");
            }
        }
    }

    public void Connect() => BeginConnect(null);

    private void BeginConnect(Guid? discoveredPeerId)
    {
        RequireIdle();
        if (Run is { Step: not CoordinatorStep.Finished })
            throw new InvalidOperationException("Resume or end this PC's coordinator job before connecting as a worker.");
        if (PairingEnabled) throw new InvalidOperationException("Disconnect the current LAN session before connecting again.");
        var address = IPAddress.Parse(Settings.ConnectAddress);
        scanner.Stop();
        advertiser.Stop();
        selectedCoordinatorId = discoveredPeerId;
        host = false;
        error = "";
        // Transport updates its own thread-safe status. All subsequent messages are drained in Update.
        _ = ConnectCore(address, Settings.Port, Settings.PairingKey, Settings.PeerId);
    }

    private async Task ConnectCore(IPAddress address, int port, string key, Guid peerId)
    {
        try { await peer.ConnectAsync(address, port, key, peerId); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "LAN connection failed"); }
    }

    public static string NewPairingKey() => LanPeer.GeneratePairingKey();
    public void Disconnect()
    {
        Pause("LAN connection stopped.");
        scanner.Stop(); advertiser.Stop(); peer.Stop();
        selectedCoordinatorId = null; advertisementError = "";
        connectionId = null; acceptedPeerId = null;
        RemoteRoster = null; wasConnected = false;
    }

    private void RequireIdle()
    {
        if (Busy || plugin.Trader.IsRunning || plugin.Fuel.Active) throw new InvalidOperationException("Stop the current operation first.");
        if (!journalHealthy) throw new InvalidOperationException(error);
    }

    public void RefreshRosters()
    {
        ImportLocalCharacters();
        if (HasAcceptedConnection)
        {
            Send("roster", LocalRoster());
            Send("roster-request", new { });
        }
    }

    private void ImportLocalCharacters()
    {
        // Only current AutoRetainer registrations may be chosen automatically as relays.
        // Keep manually configured profiles, but do not retain stale eligibility after a failed refresh.
        foreach (var character in plugin.Config.Characters)
        {
            character.InventoryFreeSlots = null;
            character.InventorySlotsRetrievedAt = null;
        }
        if (!plugin.AutoRetainer.TryGetCharacters(out var imported, out var importError))
        {
            RosterStatus = $"Could not refresh this PC's characters: {importError} Enable AutoRetainer and try again. Previously configured characters are retained.";
            return;
        }
        foreach (var row in imported)
        {
            var character = plugin.Config.Characters.FirstOrDefault(x => x.ContentId == row.ContentId);
            if (character == null) { character = new() { ContentId = row.ContentId }; plugin.Config.Characters.Add(character); }
            character.Name = row.Name; character.World = row.World; character.ServiceAccount = row.ServiceAccount;
            character.InventoryFreeSlots = row.InventorySpace;
            character.InventorySlotsRetrievedAt = DateTimeOffset.UtcNow;
        }
        plugin.Save();
        RosterStatus = imported.Count == 0
            ? "AutoRetainer returned no characters. Configure this account's characters in AutoRetainer, then refresh."
            : $"Refreshed {imported.Count} characters from this PC's AutoRetainer at {DateTime.Now:T}.";
    }

    public PeerRoster LocalRoster() => new()
    {
        PeerId = Settings.PeerId, Name = Settings.PeerName, ControlEnabled = host || Settings.AllowRemoteControl,
        Characters = plugin.Config.Characters.Select(x => new CharacterProfile
        {
            ContentId = x.ContentId, Name = x.Name, World = x.World, AccountLabel = x.AccountLabel, ServiceAccount = x.ServiceAccount,
            InventoryFreeSlots = x.InventoryFreeSlots, InventorySlotsRetrievedAt = x.InventorySlotsRetrievedAt
        }).ToList()
    };

    public IEnumerable<(Guid PeerId, CharacterProfile Character)> AllCharacters() =>
        LocalRoster().Characters.Select(x => (Settings.PeerId, x)).Concat(
            RemoteRoster?.Characters.Select(x => (RemoteRoster.PeerId, x)) ?? []);

    public void Update()
    {
        if (disposed) return;
        try
        {
            var now = Environment.TickCount64;
            if (peer.SessionIdentity is { } session && connectionId != session.ConnectionId)
            {
                if (wasConnected || Busy) Pause("Connection changed. Resume after reviewing the paired clients.", false);
                RemoteRoster = null;
                if (selectedCoordinatorId.HasValue && selectedCoordinatorId != session.RemotePeerId)
                { peer.Stop(); advertiser.Stop(); throw new InvalidOperationException("The authenticated PC does not match the selected nearby coordinator. Search again and verify the pairing key."); }
                if (Settings.ExpectedRemotePeerId.HasValue && Settings.ExpectedRemotePeerId != session.RemotePeerId)
                { peer.Stop(); advertiser.Stop(); throw new InvalidOperationException("A different peer connected. Forget the previous pairing before pairing another PC."); }
                Settings.ExpectedRemotePeerId ??= session.RemotePeerId;
                plugin.Save();
                connectionId = session.ConnectionId;
                acceptedPeerId = session.RemotePeerId;
                peerSeenAt = now;
                RefreshRosters();
            }
            if (activeWorker != null && activeReplyPeer != Settings.PeerId && !Settings.AllowRemoteControl)
                Pause("Remote workflow control was disabled on this PC.");
            if (!Connected && wasConnected && (Busy || activeWorker != null)) Pause("Peer disconnected. Pending operations must be reconciled before resuming.", false);
            wasConnected = Connected;
            if (Connected && now >= heartbeatAt)
            {
                heartbeatAt = now + 1000;
                Send("heartbeat", new { });
            }
            SyncItemSearchConnection();
            for (var i = 0; i < 64 && connectionId.HasValue && peer.TryReceive(out var message, connectionId); i++)
            {
                peerSeenAt = now;
                Receive(message);
            }
            if (Connected && now - peerSeenAt > 15_000 && Busy) Pause("Peer heartbeat timed out.");
            UpdateItemSearch();
            executor.Update();
            while (executor.TryTakeResult(out var result))
            {
                if (activeWorker?.Command.Operation.Id == result.Id)
                    CompleteWorker(new() { CommandId = activeWorker.Command.Id, Success = result.Success, Error = result.Error,
                        Ambiguous = result.MovementUncertain, Observation = result, Quantity = result.MovedQuantity });
            }
            UpdateWorkerTrade();
            if (RunningJob) AdvanceCoordinator();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Transfer workflow stopped");
            Pause(ex.Message);
        }
    }

    private void Send<T>(string kind, T payload, Guid? correlation = null)
    {
        if (!HasAcceptedConnection) throw new InvalidOperationException("The connected PC's identity has not yet been accepted.");
        peer.Send(new PeerMessage(kind, Guid.NewGuid(), correlation, JsonConvert.SerializeObject(payload)), connectionId);
    }

    private void Receive(PeerMessage message)
    {
        if (ReceiveItemSearch(message)) return;
        switch (message.Kind)
        {
            case "heartbeat": break;
            case "cache-request": Send("cache-response", ReadHints(Decode<InventoryHintRequest>(message.Payload))); break;
            case "cache-response": RemoteInventoryHints = Decode<InventoryHintResponse>(message.Payload); break;
            case "roster-request":
                ImportLocalCharacters();
                Send("roster", LocalRoster());
                break;
            case "roster":
                var roster = Decode<PeerRoster>(message.Payload);
                if (roster.PeerId != acceptedPeerId || roster.PeerId == Settings.PeerId) throw new InvalidDataException("Peer identity mismatch.");
                RemoteRoster = roster;
                break;
            case "command":
                if (host) throw new InvalidOperationException("A worker cannot issue commands to the coordinator.");
                HandleWorker(Decode<WorkerCommand>(message.Payload), acceptedPeerId!.Value);
                break;
            case "response":
                if (host) AcceptResponse(acceptedPeerId!.Value, Decode<WorkerResponse>(message.Payload));
                break;
            case "query":
                if (!host) HandleQuery(Decode<Guid>(message.Payload), acceptedPeerId!.Value);
                break;
            case "trade-tally":
                if (!host && Settings.AllowRemoteControl) HandleTradeTally(Decode<TradeTallyRequest>(message.Payload), acceptedPeerId!.Value);
                break;
            case "trade-tally-result":
                if (host) AcceptTradeTally(acceptedPeerId!.Value, Decode<TradeTallyReply>(message.Payload));
                break;
            case "trade-settle":
                if (!host && Settings.AllowRemoteControl) HandleAutomaticTradeResolution(Decode<AutomaticTradeResolution>(message.Payload), acceptedPeerId!.Value);
                break;
            case "trade-settle-result":
                if (host) AcceptAutomaticTradeResolution(acceptedPeerId!.Value, Decode<TradeResolutionReply>(message.Payload));
                break;
            case "resolve":
                if (!host && Settings.AllowRemoteControl) ResolveWorker(Decode<ManualResolution>(message.Payload), acceptedPeerId!.Value);
                break;
            case "pause": Pause("Peer requested pause.", false); break;
            default: throw new InvalidDataException("Unsupported peer message.");
        }
    }

    private static T Decode<T>(string json) => JsonConvert.DeserializeObject<T>(json,
        new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None, MaxDepth = 32 })
        ?? throw new InvalidDataException("Empty protocol message.");

    private void Reply(Guid destination, WorkerResponse response)
    {
        if (destination == Settings.PeerId) AcceptResponse(destination, response);
        else if (HasAcceptedConnection && destination == acceptedPeerId) Send("response", response, response.CommandId);
    }

    private void HandleWorker(WorkerCommand command, Guid caller)
    {
        if (!Enum.IsDefined(command.Kind) || command.Id == Guid.Empty || command.JobId == Guid.Empty
            || command.Operation == null || command.Operation.Id != command.Id || !Enum.IsDefined(command.Operation.Kind)
            || command.Operation.CharacterId == 0
            || command.Operation.Kind == GameOperationKind.ReadCachedInventory && command.Kind != WorkerCommandKind.Game)
            throw new InvalidDataException("Invalid worker command.");
        if (command.Kind == WorkerCommandKind.Game && MultiItemTradeEvidence.IsVector(command))
            throw new InvalidDataException("Native storage/travel commands cannot contain a trade item batch.");
        if (command.Kind != WorkerCommandKind.Game) _ = MultiItemTradeEvidence.Lines(command);
        var fingerprint = JsonConvert.SerializeObject(command);
        if (workerRecords.TryGetValue(command.Id, out var previous))
        {
            if (previous.Fingerprint != fingerprint)
                throw new InvalidDataException("Operation ID reused with different contents.");
            HandleQuery(command.Id, caller);
            return;
        }
        if (!journalHealthy) throw new InvalidOperationException("Worker journal is unavailable.");
        if (workerRecords.Values.Any(x => x.Command.Kind is WorkerCommandKind.ReceiveBatch or WorkerCommandKind.SendBatch
            && (x.Response == null || x.Response.Ambiguous))
            && !(command.Kind == WorkerCommandKind.Game && command.Operation.Kind is GameOperationKind.Login or GameOperationKind.Inspect or GameOperationKind.ReadCachedInventory))
        { Reply(caller, new() { CommandId = command.Id, Error = "This worker has an unresolved trade. Reconcile it before issuing more work." }); return; }
        if (caller != Settings.PeerId && !Settings.AllowRemoteControl)
        { Reply(caller, new() { CommandId = command.Id, Error = "Enable paired workflow control on this PC." }); return; }
        if (activeWorker != null || executor.IsBusy || plugin.Trader.IsRunning || plugin.Fuel.Active)
        { Reply(caller, new() { CommandId = command.Id, Error = "This client is busy with another operation." }); return; }
        var character = plugin.Config.Characters.FirstOrDefault(x => x.ContentId == command.Operation.CharacterId)
            ?? throw new InvalidOperationException("Requested character is not in this client's configured roster.");
        command = Clone(command);
        command.Operation.CharacterName = character.Name;
        command.Operation.World = character.World;
        if (!MultiItemTradeEvidence.IsVector(command) && command.Operation.ItemId <= 1) throw new InvalidOperationException("Select a tradable inventory item.");
        var ar = plugin.AutoRetainer.GetStatus();
        if (command.Operation.Kind != GameOperationKind.ReadCachedInventory && ar.Ready && (ar.Busy != false || ar.MultiModeEnabled != false))
        { Reply(caller, new() { CommandId = command.Id, Error = "AutoRetainer must be idle with multi-mode paused." }); return; }
        activeReplyPeer = caller;
        receiverReadySent = false;
        activeWorker = new WorkerRecord { Command = command, Fingerprint = fingerprint };
        RecordDiagnostic($"Worker accepted command={command.Id}; job={command.JobId}; kind={command.Kind}/{command.Operation.Kind}; character={character.ContentId}; item={command.Operation.ItemId}; HQ={command.Operation.HighQuality}; quantity={command.Operation.Quantity}");
        workerRecords[command.Id] = activeWorker;
        SaveWorkers(); // Persist intent before any native action.
        error = "";
        try
        {
            if (command.Kind == WorkerCommandKind.Game)
            {
                if (command.Operation.Kind == GameOperationKind.ReadCachedInventory)
                {
                    var cached = ReadCachedInventory(command.Operation);
                    CompleteWorker(new() { CommandId = command.Id, Success = true, Observation = cached });
                    return;
                }
                if (command.Operation.Kind is GameOperationKind.ScanRetainers or GameOperationKind.Withdraw or GameOperationKind.Deposit)
                {
                    command.Operation.Destination = Settings.StorageRoutes.GetValueOrDefault(character.ContentId);
                    if (command.Operation.Kind == GameOperationKind.Deposit)
                        command.Operation.AllowedRetainerIds = Settings.AllowedRetainers.GetValueOrDefault(character.ContentId) ?? [];
                    SaveWorkers();
                }
                if (!executor.Execute(command.Operation)) throw new InvalidOperationException("The local executor did not accept the operation.");
                return;
            }
            if (Plugin.PlayerState.ContentId != character.ContentId) throw new InvalidOperationException("The wrong local character is logged in.");
            var lines = MultiItemTradeEvidence.Lines(command);
            if (command.Kind == WorkerCommandKind.PreviewBatch)
            {
                var preview = plugin.Trader.PreviewBatch(lines);
                if (preview.HasShortage || !preview.HasItems) throw new InvalidOperationException("Source stock changed; this batch cannot be prepared.");
                var response = new WorkerResponse { CommandId = command.Id, Success = true, Quantity = checked((uint)preview.TotalQuantity) };
                if (MultiItemTradeEvidence.IsVector(command))
                    response.TradeItems = preview.Entries.GroupBy(entry => entry.Key).Select(group => new TradeItemObservation
                    {
                        ItemId = group.Key.ItemId, HighQuality = group.Key.HighQuality,
                        Quantity = checked((uint)group.Aggregate(0UL, (sum, entry) => sum + entry.Quantity)),
                    }).ToList();
                CompleteWorker(response);
                return;
            }
            if (lines.Count > TradePlanner.MaxItemSlots) throw new InvalidOperationException("An agreed trade can contain at most five distinct item/quality lines.");
            var target = FindPartner(command.Partner ?? throw new InvalidDataException("Trade partner missing."));
            var startingCounts = ReadBatchItemCounts(command);
            activeWorker.TradeBaselines = lines.Select(line => new TradeItemObservation
            {
                ItemId = line.ItemId, HighQuality = line.HighQuality,
                BeforeQuantity = startingCounts[new(line.ItemId, line.HighQuality)], Quantity = line.Quantity,
            }).ToList();
            activeWorker.BeforeQuantity = lines.Count == 1 ? activeWorker.TradeBaselines[0].BeforeQuantity : null;
            SaveWorkers();
            if (command.Kind == WorkerCommandKind.ReceiveBatch) plugin.Trader.ReceiveBatch(lines, target);
            else plugin.Trader.SendBatch(lines, target);
            activeWorker.TradeStarted = true;
            SaveWorkers();
        }
        catch (Exception ex) { CompleteWorker(new() { CommandId = command.Id, Error = ex.Message }); }
    }

    private IPlayerCharacter FindPartner(CharacterProfile partner) => Plugin.ObjectTable.OfType<IPlayerCharacter>()
        .FirstOrDefault(x => x.Name.TextValue == partner.Name && x.HomeWorld.Value.Name.ToString() == partner.World)
        ?? throw new InvalidOperationException($"{partner.Name}@{partner.World} is not nearby at the meeting point.");

    private uint ReadQuantity(uint itemId, bool highQuality) => checked((uint)plugin.Trader.SnapshotInventoryNow()
        .Where(x => x.ItemId == itemId && x.HighQuality == highQuality).Aggregate(0UL, (sum, x) => sum + x.Quantity));

    private void UpdateWorkerTrade()
    {
        if (activeWorker?.Command.Kind is not (WorkerCommandKind.ReceiveBatch or WorkerCommandKind.SendBatch)) return;
        if (!activeWorker.TradeStarted) return;
        if (plugin.Trader.ReceiverReady && !receiverReadySent)
        {
            receiverReadySent = true;
            Reply(activeReplyPeer, new() { CommandId = activeWorker.Command.Id, Ready = true });
        }
        if (plugin.Trader.IsRunning) return;
        CompleteWorker(CompletedBatchResponse(activeWorker));
    }

    private void CompleteWorker(WorkerResponse response)
    {
        if (activeWorker == null) return;
        RecordDiagnostic($"Worker result command={response.CommandId}; success={response.Success}; ambiguous={response.Ambiguous}; quantity={response.Quantity}; error={response.Error}");
        if (!activeWorker.TradeStarted && activeWorker.Command.Kind is WorkerCommandKind.ReceiveBatch or WorkerCommandKind.SendBatch)
            response.NeverStarted = true;
        activeWorker.Response = response;
        var destination = activeReplyPeer;
        SaveWorkers(); // An acknowledgment never precedes durable completion.
        activeWorker = null;
        Reply(destination, response);
    }

    private void HandleQuery(Guid id, Guid caller)
    {
        if (!journalHealthy) throw new InvalidOperationException("The worker journal cannot be read; its outcomes cannot be inferred.");
        if (!workerRecords.TryGetValue(id, out var record))
        { Reply(caller, new() { CommandId = id, NeverStarted = true, Unchanged = true, Error = "This operation has no persisted intent on this worker and was never started." }); return; }
        if (activeWorker?.Command.Id == id)
        { if (plugin.Trader.ReceiverReady) Reply(caller, new() { CommandId = id, Ready = true }); return; }
        if (record.Response is { Success: true } or { OperatorReviewed: true } or { AutoReconciled: true } or { NeverStarted: true }) { Reply(caller, record.Response); return; }
        if (StorageReconciliation.IsStorage(record.Command) && (record.Response == null || record.Response.Ambiguous))
        {
            if (TryRecoverStorage(record, out var storageRecovery)) Reply(caller, storageRecovery);
            return;
        }
        if (record.Command.Kind is WorkerCommandKind.ReceiveBatch or WorkerCommandKind.SendBatch && !record.TradeStarted)
        {
            var notStarted = new WorkerResponse { CommandId = id, NeverStarted = true, Unchanged = true, Error = "No trade action was started for this operation." };
            record.Response = notStarted; SaveWorkers(); Reply(caller, notStarted); return;
        }
        if (record.Command.Kind is WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch
            && MultiItemTradeEvidence.IsVector(record.Command) && Plugin.PlayerState.ContentId == record.Command.Operation.CharacterId)
        {
            var outcome = MultiItemTradeEvidence.Capture(record.Command, record.Response, record.TradeBaselines, ReadBatchItemCounts(record.Command));
            record.Response = outcome; SaveWorkers(); Reply(caller, outcome); return;
        }
        // Inventory deltas after interruption are evidence for the operator, not proof that a trade succeeded.
        if (record.Command.Kind is WorkerCommandKind.ReceiveBatch or WorkerCommandKind.SendBatch
            && record.BeforeQuantity.HasValue && Plugin.PlayerState.ContentId == record.Command.Operation.CharacterId)
        {
            var after = ReadQuantity(record.Command.Operation.ItemId, record.Command.Operation.HighQuality);
            var before = record.BeforeQuantity.Value;
            var outcome = TradeReconciliationEvidence.Capture(record.Command, record.Response, before, after);
            RecordDiagnostic($"Trade reconciliation evidence command={id}; before={before}; current={after}; unchanged={outcome.Unchanged}; originalError={outcome.Error}");
            record.Response = outcome; SaveWorkers(); Reply(caller, outcome); return;
        }
        Reply(caller, record.Response ?? new() { CommandId = id, Ambiguous = true,
            Error = "Interrupted operation has no verified outcome. Log in the original character, inspect inventory, and resume reconciliation." });
    }

    public void Pause(string reason, bool notify = true)
    {
        error = reason;
        if (Run != null) { Run.Paused = true; Run.Status = reason; TrySaveRun(); }
        if (Plugin.Framework.IsInFrameworkUpdateThread && !Plugin.Framework.IsFrameworkUnloading) executor.Stop(reason);
        plugin.Trader.Stop();
        if (notify && Connected) { try { Send("pause", new { }); } catch { } }
    }

    private void StopTradeForTally()
    {
        plugin.Trader.Stop();
        if (Connected) Send("pause", new { });
    }

    private string? TradeTallyReadiness(WorkerRecord record)
    {
        if (activeWorker != null || executor.IsBusy || plugin.Trader.IsRunning || plugin.Fuel.Active)
            return "Waiting for both characters' active operations to stop…";
        if (!Plugin.ClientState.IsLoggedIn || Plugin.PlayerState.ContentId != record.Command.Operation.CharacterId)
            throw new InvalidOperationException("Log in the original trade character on this PC, then use Resume / tally inventories.");
        if (plugin.Trader.InventoryTallyBlockReason is { Length: > 0 } blocker)
            return $"Waiting to tally inventory: {blocker}.";
        if (NativeRetainers.HasNumeric || NativeRetainers.HasRetainerList || NativeRetainers.HasRetainerMenu
            || NativeRetainers.HasRetainerInventory || NativeRetainers.HasUnrelatedModal || NativeRetainers.HasActiveCutscene
            || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell])
            return "Waiting for trade, retainer and quantity windows to close before tallying inventories…";
        var ar = plugin.AutoRetainer.GetStatus();
        if (ar.Ready && (ar.Busy != false || ar.MultiModeEnabled != false))
            throw new InvalidOperationException("Pause AutoRetainer multi-mode on this PC before tallying the interrupted trade.");
        return null;
    }

    private void HandleTradeTally(TradeTallyRequest request, Guid caller)
    {
        TradeTallyReply reply;
        try
        {
            if (!journalHealthy || request.RoundId == Guid.Empty) throw new InvalidOperationException("The saved worker journal or tally request is unavailable.");
            if (!workerRecords.TryGetValue(request.CommandId, out var record))
                reply = new(request.CommandId, request.RoundId, true, false,
                    new() { CommandId = request.CommandId, NeverStarted = true, Unchanged = true, Error = "No worker intent exists; this side never started." });
            else if (record.Command.Kind is not (WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch))
                throw new InvalidOperationException("The requested command is not a trade.");
            else if (record.Response is { Success: true } or { AutoReconciled: true } or { OperatorReviewed: true } or { NeverStarted: true })
                reply = new(request.CommandId, request.RoundId, true, false, Clone(record.Response));
            else if (TradeTallyReadiness(record) is { } waiting)
            {
                tradeTallySamples.Remove(request.CommandId);
                batchTallySamples.Remove(request.CommandId);
                reply = new(request.CommandId, request.RoundId, false, true, null, waiting);
            }
            else if (!record.TradeStarted)
            {
                record.Response = new() { CommandId = request.CommandId, NeverStarted = true, Unchanged = true, Error = "No native trade action was started." };
                SaveWorkers();
                reply = new(request.CommandId, request.RoundId, true, false, Clone(record.Response));
            }
            else if (MultiItemTradeEvidence.IsVector(record.Command))
                reply = CaptureBatchTally(record, request);
            else if (record.BeforeQuantity is not { } before)
                throw new InvalidOperationException("This older trade has no saved starting inventory count. Its outcome cannot be tallied automatically; preserve diagnostics and the current bags.");
            else
            {
                var current = ReadQuantity(record.Command.Operation.ItemId, record.Command.Operation.HighQuality);
                var now = Environment.TickCount64;
                if (!tradeTallySamples.TryGetValue(request.CommandId, out var sample)
                    || sample.Round != request.RoundId || sample.Quantity != current)
                {
                    tradeTallySamples[request.CommandId] = (request.RoundId, current, now);
                    reply = new(request.CommandId, request.RoundId, false, true, null, "Checking that the stopped trade's inventory is stable…");
                }
                else if (now - sample.Since < 500)
                    reply = new(request.CommandId, request.RoundId, false, true, null, "Checking that the stopped trade's inventory is stable…");
                else
                {
                    record.TradeTallyRound = request.RoundId;
                    record.TradeTallyCurrent = current;
                    record.Response = TradeReconciliationEvidence.Capture(record.Command, record.Response, before, current);
                    SaveWorkers();
                    RecordDiagnostic($"Automatic trade tally; command={request.CommandId}; round={request.RoundId}; before={before}; current={current}");
                    reply = new(request.CommandId, request.RoundId, true, false, Clone(record.Response));
                }
            }
        }
        catch (Exception exception) { reply = new(request.CommandId, request.RoundId, false, false, null, exception.Message); }
        if (caller == Settings.PeerId) AcceptTradeTally(caller, reply);
        else Send("trade-tally-result", reply);
    }

    private void HandleAutomaticTradeResolution(AutomaticTradeResolution resolution, Guid caller)
    {
        TradeResolutionReply reply;
        try
        {
            if (!journalHealthy || !workerRecords.TryGetValue(resolution.CommandId, out var record))
                throw new InvalidOperationException("The trade worker record is unavailable during automatic reconciliation.");
            var settled = record.Response is { Success: true } or { AutoReconciled: true } or { OperatorReviewed: true } or { NeverStarted: true };
            if (!settled && TradeTallyReadiness(record) is { } waiting) throw new InvalidOperationException(waiting);
            if (MultiItemTradeEvidence.IsVector(record.Command))
                record.Response = MultiItemTradeEvidence.Settle(record, resolution, settled ? new Dictionary<ItemKey, uint>() : ReadBatchItemCounts(record.Command));
            else
            {
                var current = settled ? record.Response!.CurrentQuantity.GetValueOrDefault()
                    : ReadQuantity(record.Command.Operation.ItemId, record.Command.Operation.HighQuality);
                record.Response = TradeReconciliationEvidence.Settle(record, resolution, current);
            }
            SaveWorkers();
            error = "";
            RecordDiagnostic($"Automatic trade outcome saved; command={resolution.CommandId}; round={resolution.RoundId}; transferred={resolution.Transferred}; quantity={record.Response.Quantity}");
            reply = new(resolution.CommandId, resolution.RoundId, Clone(record.Response));
        }
        catch (Exception exception) { reply = new(resolution.CommandId, resolution.RoundId, null, exception.Message); }
        if (caller == Settings.PeerId) AcceptAutomaticTradeResolution(caller, reply);
        else Send("trade-settle-result", reply);
    }

    private void SaveWorkers()
    {
        if (!workerFile.TrySave(JsonConvert.SerializeObject(workerRecords), out var failure))
        { journalHealthy = false; throw new IOException(failure); }
    }
    private void SaveRun()
    {
        if (!runFile.TrySave(JsonConvert.SerializeObject(Run), out var failure))
        { journalHealthy = false; throw new IOException(failure); }
    }
    private void TrySaveRun() { try { SaveRun(); } catch (Exception ex) { Plugin.Log.Error(ex, "Could not persist paused job"); journalHealthy = false; } }
    public void Dispose()
    {
        disposed = true; Pause("Plugin unloaded.", false); executor.Shutdown();
        scanner.Dispose(); advertiser.Dispose(); peer.Dispose();
    }

    private void ResolveWorker(ManualResolution resolution, Guid caller)
    {
        if (resolution.StorageVerifiedOnly)
        {
            if (resolution.Transferred || !journalHealthy
                || !workerRecords.TryGetValue(resolution.CommandId, out var storageRecord)
                || !StorageReconciliation.IsStorage(storageRecord.Command))
                throw new InvalidOperationException("This command cannot be reconciled as a recorded storage-only movement.");
            if (StorageReconciliation.IsReviewed(storageRecord.Response))
            {
                RecordDiagnostic($"Storage review replay; command={resolution.CommandId}; confirmedMoved={storageRecord.Response!.Observation!.MovedQuantity}");
                Reply(caller, storageRecord.Response!);
                return;
            }
            if (activeWorker != null || executor.IsBusy || plugin.Trader.IsRunning || plugin.Fuel.Active)
                throw new InvalidOperationException("Stop active work before storage reconciliation.");
            if (!Plugin.ClientState.IsLoggedIn || Plugin.PlayerState.ContentId != storageRecord.Command.Operation.CharacterId)
                throw new InvalidOperationException("Log in the original storage character before reviewing its bags and retainers.");
            if (NativeRetainers.HasNumeric || NativeRetainers.HasRetainerList || NativeRetainers.HasRetainerMenu
                || NativeRetainers.HasRetainerInventory || NativeRetainers.HasUnrelatedModal || NativeRetainers.HasActiveCutscene
                || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell])
                throw new InvalidOperationException("Close all retainer, quantity and trade windows before accepting the storage review.");
            var storageCurrent = ReadQuantity(storageRecord.Command.Operation.ItemId, storageRecord.Command.Operation.HighQuality);
            var storageResponse = StorageReconciliation.Review(storageRecord.Command, storageRecord.Response, storageCurrent);
            storageRecord.Response = storageResponse;
            SaveWorkers(); // The review is durable before its acknowledgement is sent.
            RecordDiagnostic($"Storage review saved; command={resolution.CommandId}; confirmedMoved={storageResponse.Observation!.MovedQuantity}; bagAtStop={storageResponse.Observation.BagQuantity}; remainsPaused=True");
            Reply(caller, storageResponse);
            return;
        }
        if (activeWorker != null || executor.IsBusy || plugin.Trader.IsRunning) throw new InvalidOperationException("Stop active work before reconciliation.");
        if (!workerRecords.TryGetValue(resolution.CommandId, out var record)
            || record.Command.Kind is not (WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch)
            || !record.BeforeQuantity.HasValue || Plugin.PlayerState.ContentId != record.Command.Operation.CharacterId)
            throw new InvalidOperationException("Log in the original trade character before reconciliation.");
        if (record.Response is { Success: true }) { Reply(caller, record.Response); return; }
        if (MultiItemTradeEvidence.IsVector(record.Command))
            throw new InvalidOperationException("Use the automatic paired item tally to reconcile this multi-item trade.");
        var current = ReadQuantity(record.Command.Operation.ItemId, record.Command.Operation.HighQuality);
        var before = record.BeforeQuantity.Value;
        var wanted = record.Command.Operation.Quantity;
        var expected = record.Command.Kind == WorkerCommandKind.SendBatch
            ? before >= current && before - current == wanted : current >= before && current - before == wanted;
        if (resolution.Transferred ? !expected : current != before)
            throw new InvalidOperationException("Current inventory does not support the selected reconciliation outcome.");
        var response = new WorkerResponse { CommandId = resolution.CommandId, Success = resolution.Transferred,
            Unchanged = !resolution.Transferred, OperatorReviewed = true, Quantity = resolution.Transferred ? wanted : 0,
            BeforeQuantity = before, CurrentQuantity = current,
            Error = resolution.Transferred ? "" : "Operator confirmed no transfer." };
        record.Response = response; SaveWorkers(); Reply(caller, response);
    }

    public IReadOnlyList<RetainerInventorySnapshot> GetKnownRetainers(ulong characterId) => workerRecords.Values
        .Where(x => x.Command.Operation.CharacterId == characterId
            && x.Command.Operation.Kind is GameOperationKind.ScanRetainers or GameOperationKind.ReadCachedInventory
            && x.Response?.Observation is { Success: true } observation
            && (observation.RetainerRosterVerified || observation.CachedInventoryAvailable && observation.RetainerRosterKnown))
        .OrderByDescending(x => x.Response!.Observation!.CacheRetrievedAt ?? x.Response.Observation.ObservedAt)
        .FirstOrDefault()?.Response?.Observation?.Retainers ?? [];
}
