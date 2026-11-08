using System.Net;
using AsheTrader.Core.Networking;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private readonly LanDiscovery advertiser = new();
    private readonly LanDiscovery scanner = new();
    private readonly object addressGate = new();
    private IReadOnlyList<LanAddress> localAddresses = [];
    private long addressRefreshAt;
    private Guid? selectedCoordinatorId;
    private string advertisementError = "";

    public bool Discovering => scanner.IsEnabled;
    public IReadOnlyList<DiscoveredPeer> NearbyPeers => scanner.Snapshot();
    public string DiscoveryStatus => Discovering ? scanner.Status
        : advertisementError.Length > 0 ? advertisementError
        : IsHost && PairingEnabled ? advertiser.Status : scanner.Status;

    public IReadOnlyList<LanAddress> LocalAddresses
    {
        get
        {
            lock (addressGate)
            {
                if (Environment.TickCount64 >= addressRefreshAt)
                {
                    localAddresses = LanNetwork.GetLocalAddresses();
                    addressRefreshAt = Environment.TickCount64 + 5000;
                }
                return localAddresses;
            }
        }
    }

    private IPAddress SelectHostAddress()
    {
        var addresses = LanNetwork.GetLocalAddresses();
        IPAddress.TryParse(Settings.BindAddress, out var configured);
        // Retain an explicit loopback address for local diagnostics; it is never advertised.
        if (configured != null && IPAddress.IsLoopback(configured)) return configured;
        // Permit a manual fallback if adapter enumeration is unavailable. LanPeer validates the address before binding.
        if (addresses.Count == 0 && configured != null) return configured;
        var selected = addresses.FirstOrDefault(x => x.Address.Equals(configured))
            ?? addresses.OrderByDescending(x => x.Preferred).FirstOrDefault()
            ?? throw new InvalidOperationException("No active home-network IPv4 address was found. Connect to your home network, or use an explicit address under Advanced.");
        Settings.BindAddress = selected.Address.ToString();
        plugin.Save();
        if (plugin.SettingsError.Length > 0) throw new IOException(plugin.SettingsError);
        return selected.Address;
    }

    public void StartDiscovery()
    {
        RequireIdle();
        if (PairingEnabled) throw new InvalidOperationException("Disconnect the current LAN session before searching for another coordinator.");
        if (Run is { Step: not CoordinatorStep.Finished })
            throw new InvalidOperationException("Resume or end this PC's coordinator job before using it as a worker.");
        scanner.StartScanning(Settings.PeerId);
    }

    public void StopDiscovery() => scanner.Stop();

    public void ConnectDiscovered(DiscoveredPeer selected)
    {
        RequireIdle();
        if (PairingEnabled || Run is { Step: not CoordinatorStep.Finished })
            throw new InvalidOperationException("Disconnect the current LAN session and finish any coordinator job before connecting as a worker.");
        var current = NearbyPeers.FirstOrDefault(x => x.PeerId == selected.PeerId
            && x.Address.Equals(selected.Address) && x.Port == selected.Port)
            ?? throw new InvalidOperationException("That coordinator is no longer nearby. Search again before connecting.");
        if (Settings.ExpectedRemotePeerId.HasValue && Settings.ExpectedRemotePeerId != current.PeerId)
            throw new InvalidOperationException("This is a different PC from the remembered pairing. Choose the paired PC, or forget the old pairing before replacing it.");
        Settings.ConnectAddress = current.Address.ToString();
        Settings.Port = current.Port;
        plugin.Save();
        if (plugin.SettingsError.Length > 0) throw new IOException(plugin.SettingsError);
        BeginConnect(current.PeerId);
    }
}
