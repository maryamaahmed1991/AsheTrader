using System.Net;
using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Core.Networking;
using AsheTrader.Store;
using Newtonsoft.Json;

namespace AsheTrader.Workflow;

internal sealed record TrustedDeviceSecret(DeviceCredential? Access, string PairingKey);
internal sealed record DeviceReadiness(bool Ready, bool HasUnfinishedJob = false);
internal sealed record WaitingTransfer(SavedTransferJob Job, bool ScanOnly);

public sealed partial class WorkflowService
{
    private readonly DeviceApi deviceApi = new();
    private readonly CancellationTokenSource deviceLifetime = new();
    private StoreCredentialFile deviceFile = null!;
    private SettingsFileStore waitingFile = null!;
    private TrustedDeviceSecret? deviceSecret;
    private Task<Action>? deviceTask;
    private bool autoConnectionSuppressed;
    private bool remoteReady;
    private bool remoteHasUnfinishedJob;
    private readonly TrustedRunGate trustedRun = new();
    private WaitingTransfer? waitingTransfer;
    private long nextConnectAt;
    private long nextDevicePoll;
    private long nextAdapterCheck;
    private long remoteReadyAt;
    private string deviceStatus = "Looking on your local network.";
    public string DeviceInvitationCode { get; private set; } = "";
    public string DeviceSetupStatus { get; private set; } = "";
    public bool DeviceSetupBusy => deviceTask != null;
    public bool HasTrustedPeer => Settings.ExpectedRemotePeerId.HasValue;
    public bool HasHostedDeviceLink => deviceSecret?.Access != null;
    public bool AutomaticConnectionActive => Settings.AutoConnect && !autoConnectionSuppressed;
    public bool WaitingForPeer => trustedRun.Armed;
    public bool HasQueuedTransfer => waitingTransfer != null;
    public bool CanQueueTransfer => HasTrustedPeer && Settings.RememberedHost == true;
    private string EffectivePairingKey => deviceSecret?.PairingKey is { Length: > 0 } secret ? secret : Settings.PairingKey;
    private bool LocalReadyForJob => !Busy && !plugin.Trader.IsRunning && !plugin.Fuel.Active && !(plugin.FuelQueue.HasUnfinishedRun || plugin.Utilities.HasUnfinishedRun) && journalHealthy;

    private void InitializeTrustedPairing()
    {
        var root = Plugin.PluginInterface.ConfigDirectory.FullName;
        deviceFile = new(Path.Combine(root, "trusted-device-credentials.json"));
        waitingFile = new(Path.Combine(root, "waiting-transfer.json"));
        try
        {
            deviceSecret = deviceFile.LoadValue<TrustedDeviceSecret>();
            waitingTransfer = JsonConvert.DeserializeObject<WaitingTransfer>(waitingFile.Load() ?? "null");
            if (waitingTransfer != null && Run is { Step: not CoordinatorStep.Finished } && Run.Job.Definition.Id == waitingTransfer.Job.Definition.Id)
            { waitingTransfer = null; SaveWaitingTransfer(); }
            InitializeInventorySync(root);
            if (HasTrustedPeer && !Settings.RememberedHost.HasValue)
            {
                Settings.RememberedHost = Run is { Step: not CoordinatorStep.Finished } || !Settings.AllowRemoteControl;
                plugin.Save();
            }
            host = Settings.RememberedHost == true;
        }
        catch (Exception ex) { DeviceSetupStatus = "Saved device setup could not be loaded. " + ex.Message; autoConnectionSuppressed = true; }
        // Never arm a saved job at startup. Connections and inventory reads are independent of game work.
    }

    public void ReconnectTrustedPc()
    {
        autoConnectionSuppressed = false;
        Settings.AutoConnect = true;
        nextConnectAt = nextDevicePoll = 0;
        plugin.Save();
    }

    public void CreateDeviceInvitation()
    {
        RequireIdle();
        if (DeviceSetupBusy || HasHostedDeviceLink || HasTrustedPeer) throw new InvalidOperationException("Forget the current pairing before adding a different PC.");
        var owner = plugin.Store.Owner ?? throw new InvalidOperationException("Connect the store owner's access on this PC first, or use local pairing below.");
        if (PairingEnabled) Disconnect();
        var key = NewPairingKey();
        StartDeviceTask(async () =>
        {
            var result = await deviceApi.Create(owner, Settings.PeerId, Settings.PeerName, deviceLifetime.Token);
            var access = new DeviceCredential(owner.Url, result.Token, result.Id, true);
            var secret = new TrustedDeviceSecret(access, key);
            // Persist credentials before advertising or presenting an invitation.
            try { deviceFile.SaveValue(secret); }
            catch { await deviceApi.Revoke(access, deviceLifetime.Token); throw; }
            return () =>
            {
                deviceSecret = secret;
                Settings.RememberedHost = true; host = true; Settings.AutoConnect = true; Settings.PairingKey = "";
                plugin.Save(); autoConnectionSuppressed = false; nextConnectAt = nextDevicePoll = 0;
                DeviceInvitationCode = new DeviceInvitation(1, owner.Url, result.Invite, Settings.PeerId, Settings.PeerName, key, result.ExpiresAt).Encode();
                DeviceSetupStatus = "Paste this one-use code on your second PC within 10 minutes. It grants inventory sharing and optional coordinator control, not store access.";
            };
        });
    }

    public void JoinDevice(string code)
    {
        RequireIdle();
        if (Run is { Step: not CoordinatorStep.Finished } || HasTrustedPeer || HasHostedDeviceLink)
            throw new InvalidOperationException("End the saved job and forget the previous pairing before adding a different coordinator.");
        var invitation = DeviceInvitation.Parse(code);
        if (invitation.HostPeerId == Settings.PeerId) throw new InvalidOperationException("Both PCs have the same identity. Do not copy the entire plugin configuration between PCs.");
        if (PairingEnabled) Disconnect();
        // Verify OS encryption works before consuming a one-use invitation.
        deviceFile.SaveValue<TrustedDeviceSecret?>(null);
        StartDeviceTask(async () =>
        {
            var result = await deviceApi.Join(invitation, Settings.PeerId, Settings.PeerName, deviceLifetime.Token);
            var access = new DeviceCredential(invitation.Url, result.Token, result.Id, false);
            if (result.HostPeerId != invitation.HostPeerId) { await deviceApi.Revoke(access, deviceLifetime.Token); throw new InvalidDataException("The invitation's coordinator identity did not match."); }
            var secret = new TrustedDeviceSecret(access, invitation.PairingKey);
            try { deviceFile.SaveValue(secret); }
            catch { await deviceApi.Revoke(access, deviceLifetime.Token); throw; }
            return () =>
            {
                deviceSecret = secret; Settings.ExpectedRemotePeerId = invitation.HostPeerId; Settings.RememberedHost = false;
                Settings.AutoConnect = true; Settings.PairingKey = ""; host = false; plugin.Save();
                autoConnectionSuppressed = false; nextConnectAt = nextDevicePoll = 0;
                DeviceSetupStatus = $"Trusted {result.HostName}. Reconnection is automatic. Enable coordinator control below if this PC should run transfer tasks.";
            };
        });
    }

    public void DismissDeviceInvitation() => DeviceInvitationCode = "";

    public void ForgetTrustedPc()
    {
        RequireIdle();
        if (Run is { Step: not CoordinatorStep.Finished } || waitingTransfer != null)
            throw new InvalidOperationException("End or cancel the saved transfer before forgetting its paired PC.");
        if (DeviceSetupBusy) throw new InvalidOperationException("Wait for device setup to finish.");
        Disconnect();
        if (deviceSecret?.Access is { } access)
            StartDeviceTask(async () => { await deviceApi.Revoke(access, deviceLifetime.Token); return ClearTrustedPc; });
        else ClearTrustedPc();
    }

    private void ClearTrustedPc()
    {
        deviceFile.SaveValue<TrustedDeviceSecret?>(null);
        deviceSecret = null; Settings.ExpectedRemotePeerId = null; Settings.RememberedHost = null; Settings.PairingKey = "";
        DeviceInvitationCode = ""; RemoteRoster = savedRemoteRoster = null;
        ClearInventorySync(); plugin.Save(); DeviceSetupStatus = "Device pairing removed. Its saved private inventory was cleared from this PC.";
    }

    private void StartDeviceTask(Func<Task<Action>> action)
    {
        if (deviceTask != null) throw new InvalidOperationException("A device request is already running.");
        deviceTask = action();
    }

    private void UpdateTrustedPairing(long now)
    {
        if (deviceTask is { IsCompleted: true } completed)
        {
            deviceTask = null;
            try { completed.GetAwaiter().GetResult()(); }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                DeviceSetupStatus = ex.Message;
                if (deviceSecret?.Access != null) { Disconnect(); deviceStatus = "Hosted device access was revoked. Forget this link before pairing again."; }
            }
            catch (InvalidDataException ex) { Disconnect(); DeviceSetupStatus = ex.Message; deviceStatus = "Device identity needs to be checked before reconnecting."; }
            catch (Exception ex) { DeviceSetupStatus = ex.Message; deviceStatus = "Hosted lookup unavailable; trying local discovery."; }
        }
        if (autoConnectionSuppressed || !Settings.AutoConnect && !Connected) return;
        if (host && PairingEnabled && !Connected && now >= nextAdapterCheck)
        {
            nextAdapterCheck = now + 10_000;
            if (TrustedDevicePolicy.IsPrivateAddress(Settings.BindAddress)
                && !LocalAddresses.Any(x => x.Address.ToString() == Settings.BindAddress))
            { advertiser.Stop(); peer.Stop(); nextConnectAt = 0; nextDevicePoll = 0; }
        }
        if (deviceSecret?.Access is { } access && deviceTask == null && now >= nextDevicePoll)
        {
            nextDevicePoll = now + 30_000;
            var address = host && PairingEnabled && TrustedDevicePolicy.IsPrivateAddress(Settings.BindAddress) ? Settings.BindAddress : "";
            var port = address.Length > 0 ? peer.BoundPort : 0;
            StartDeviceTask(async () =>
            {
                var state = await deviceApi.Poll(access, address, port, deviceLifetime.Token);
                return () =>
                {
                    if (state.Id != access.LinkId || (access.Host ? state.HostPeerId : state.GuestPeerId) != Settings.PeerId)
                        throw new InvalidDataException("Hosted device identity mismatch.");
                    var remoteId = access.Host ? state.GuestPeerId : state.HostPeerId;
                    if (remoteId.HasValue && remoteId != Guid.Empty)
                    {
                        if (Settings.ExpectedRemotePeerId.HasValue && Settings.ExpectedRemotePeerId != remoteId) throw new InvalidDataException("The hosted link identifies a different trusted PC.");
                        if (!Settings.ExpectedRemotePeerId.HasValue) { Settings.ExpectedRemotePeerId = remoteId; plugin.Save(); DeviceInvitationCode = ""; }
                    }
                    if (!host && TrustedDevicePolicy.UsableEndpoint(state, Settings.ExpectedRemotePeerId ?? Guid.Empty, DateTimeOffset.UtcNow))
                    { Settings.ConnectAddress = state.Address; Settings.Port = state.Port; deviceStatus = "Found the coordinator's current local-network address."; }
                    else deviceStatus = "Waiting for the other PC on your local network.";
                };
            });
        }
        if (!Settings.AutoConnect || Connected || PairingEnabled || now < nextConnectAt || EffectivePairingKey.Length == 0 || (!HasTrustedPeer && deviceSecret?.Access == null)) return;
        nextConnectAt = now + 10_000;
        try
        {
            if (Settings.RememberedHost == true) StartHosting();
            else
            {
                if (!Discovering)
                {
                    scanner.StartScanning(Settings.PeerId);
                    // Give broadcast replies time to arrive before falling back to a remembered address.
                    nextConnectAt = now + 2000;
                    return;
                }
                var found = NearbyPeers.FirstOrDefault(x => x.PeerId == Settings.ExpectedRemotePeerId);
                if (found != null) { Settings.ConnectAddress = found.Address.ToString(); Settings.Port = found.Port; }
                if (IPAddress.TryParse(Settings.ConnectAddress, out _)) StartConnecting(Settings.ExpectedRemotePeerId);
            }
        }
        catch (Exception ex) { deviceStatus = "Retrying local connection: " + ex.Message; }
    }

    public void StartWhenReady(SavedTransferJob job, bool scanOnly = false)
    {
        if (Connected && RemoteRoster is { ControlEnabled: true } && RemoteReadyForJob) { if (scanOnly) ScanJob(job); else StartJob(job); return; }
        RequireIdle();
        if (!CanQueueTransfer) throw new InvalidOperationException("Pair this coordinator with your other PC first.");
        if (Run is { Step: not CoordinatorStep.Finished }) throw new InvalidOperationException("Resume or end the existing job first.");
        foreach (var item in job.ItemDefinitions) JobPlanner.ValidateDefinition(item);
        if (!scanOnly && (job.Meeting.TerritoryId == 0 || job.Meeting.AetheryteId == 0)) throw new InvalidOperationException("Record a meeting point before starting.");
        waitingTransfer = new(Clone(job), scanOnly); SaveWaitingTransfer(); trustedRun.Queue(); error = "";
        ReconnectTrustedPc();
    }
    public void ResumeWhenReady()
    {
        if (waitingTransfer != null) { trustedRun.Queue(); ReconnectTrustedPc(); return; }
        if (Connected && RemoteRoster is { ControlEnabled: true } && RemoteReadyForJob) { Resume(); return; }
        if (!CanQueueTransfer || Run is not { Paused: true, Step: not CoordinatorStep.Finished }) return;
        trustedRun.Resume(); ReconnectTrustedPc();
    }
    public void CancelWaitingTransfer()
    {
        trustedRun.Cancel(); waitingTransfer = null; SaveWaitingTransfer();
    }
    private void SaveWaitingTransfer()
    {
        if (!waitingFile.TrySave(JsonConvert.SerializeObject(waitingTransfer), out var failure)) throw new IOException(failure);
    }
    private void PauseForReconnect(string reason)
    {
        var resume = RunningJob && host && Settings.ResumeAfterReconnect;
        Pause(reason, false); trustedRun.RecoverConnection(resume, Settings.ResumeAfterReconnect);
        remoteReady = false; remoteReadyAt = 0;
    }
    private void UpdateWaitingJob()
    {
        if (!trustedRun.CanProceed(Connected && HasAcceptedConnection, RemoteRoster is { ControlEnabled: true }, LocalReadyForJob, RemoteReadyForJob)) return;
        var queued = waitingTransfer;
        trustedRun.Cancel();
        if (queued != null)
        {
            // Start persists the real run before the waiting entry is removed. A restart never arms either automatically.
            if (queued.ScanOnly) ScanJob(queued.Job); else StartJob(queued.Job);
            waitingTransfer = null; SaveWaitingTransfer();
        }
        else Resume();
    }
    private bool RemoteReadyForJob => RemoteRoster is { ProtocolVersion: < 2 }
        || remoteReady && Environment.TickCount64 - remoteReadyAt <= 5000;
    private bool ReceiveTrustedDevice(PeerMessage message)
    {
        if (message.Kind == "device-ready")
        { var state = Decode<DeviceReadiness>(message.Payload); remoteReady = state.Ready; remoteHasUnfinishedJob = state.HasUnfinishedJob; remoteReadyAt = Environment.TickCount64; return true; }
        return ReceiveInventorySync(message);
    }
}
