using System.Net;
using System.Net.Sockets;

namespace AsheTrader.Core.Networking;

/// <summary>
/// Optional LAN rendezvous hints. Discovery never carries a pairing key or gameplay command,
/// and a discovered endpoint is not trusted until LanPeer completes its authenticated handshake.
/// Construction performs no networking; hosting or scanning must be explicitly started.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    public const int DiscoveryPort = 45875;
    private readonly object gate = new();
    private Run? current;
    private bool disposed;
    private string status = "Nearby PC discovery is stopped.";

    public bool IsEnabled { get { lock (gate) return current != null; } }
    public string Status { get { lock (gate) return status; } }
    internal int BoundPort { get { lock (gate) return current?.BoundPort ?? 0; } }

    public IReadOnlyList<DiscoveredPeer> Snapshot()
    {
        lock (gate) return current?.Catalog?.Snapshot(Environment.TickCount64) ?? [];
    }

    public void StartAdvertising(IPAddress localAddress, int tcpPort, Guid peerId, string name)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        var adapter = LanNetwork.GetLocalAddresses().FirstOrDefault(x => x.Address.Equals(localAddress))
            ?? throw new ArgumentException("Choose an active private IPv4 network adapter for nearby PC discovery.", nameof(localAddress));
        StartAdvertisingCore(localAddress, adapter.PrefixLength, DiscoveryPort, tcpPort, peerId, name, false);
    }

    public void StartScanning(Guid localPeerId)
    {
        var interfaces = LanNetwork.GetLocalAddresses();
        if (interfaces.Count == 0) throw new InvalidOperationException("No active private IPv4 network was found. Use a manual address or connect to your home network.");
        StartScanningCore(localPeerId, interfaces.Select(x => (x.Address, new IPEndPoint(x.BroadcastAddress, DiscoveryPort))).ToArray(), false);
    }

    internal void StartAdvertisingForTest(int tcpPort, Guid peerId, string name) =>
        StartAdvertisingCore(IPAddress.Loopback, 8, 0, tcpPort, peerId, name, true);

    internal void StartScanningForTest(Guid localPeerId, IPEndPoint target) =>
        StartScanningCore(localPeerId, [(IPAddress.Loopback, target)], true);

    private void StartAdvertisingCore(IPAddress address, int prefixLength, int discoveryPort, int tcpPort, Guid peerId, string name, bool allowLoopback)
    {
        ValidateIdentity(peerId);
        if (tcpPort is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(tcpPort));
        var displayName = DiscoveryProtocol.CleanName(name);
        lock (gate)
        {
            RequireStopped();
            var sockets = new List<UdpClient>();
            try
            {
                // Directed broadcasts do not reliably reach a socket bound to a unicast IP.
                // Listen on Any, filter to the chosen interface's subnet, and reply from a
                // second socket bound to that interface so the observed source is the TCP address.
                var listener = Bind(allowLoopback ? IPAddress.Loopback : IPAddress.Any, discoveryPort);
                sockets.Add(listener);
                var reply = Bind(address, 0);
                sockets.Add(reply);
                var run = new Run(sockets, peerId, null, ((IPEndPoint)listener.Client.LocalEndPoint!).Port);
                current = run;
                status = "This coordinator is discoverable on the selected home network.";
                run.Lifetime = Task.Run(() => RunAdvertisingAsync(run, listener, reply, address, prefixLength, tcpPort, displayName, allowLoopback));
            }
            catch { foreach (var socket in sockets) socket.Dispose(); throw; }
        }
    }

    private void StartScanningCore(Guid localPeerId, IReadOnlyList<(IPAddress Local, IPEndPoint Target)> endpoints, bool allowLoopback)
    {
        ValidateIdentity(localPeerId);
        lock (gate)
        {
            RequireStopped();
            var sockets = new List<UdpClient>();
            try
            {
                foreach (var endpoint in endpoints)
                {
                    if (!(LanNetwork.IsPrivateIPv4(endpoint.Local) || allowLoopback && IPAddress.IsLoopback(endpoint.Local)))
                        throw new ArgumentException("Discovery must use a private IPv4 network.");
                    var socket = Bind(endpoint.Local, 0);
                    socket.EnableBroadcast = true;
                    sockets.Add(socket);
                }
                var run = new Run(sockets, localPeerId, new(localPeerId, allowLoopback), 0);
                current = run;
                status = "Looking for nearby coordinators…";
                run.Lifetime = Task.Run(() => RunScanningAsync(run, endpoints));
            }
            catch { foreach (var socket in sockets) socket.Dispose(); throw; }
        }
    }

    private static UdpClient Bind(IPAddress address, int port)
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Client.ReceiveBufferSize = 16 * 1024;
            socket.Client.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private async Task RunAdvertisingAsync(Run run, UdpClient listener, UdpClient reply, IPAddress localAddress,
        int prefixLength, int tcpPort, string name, bool allowLoopback)
    {
        long nextReplyAt = 0;
        try
        {
            while (!run.Cancellation.IsCancellationRequested)
            {
                var datagram = await listener.ReceiveAsync(run.Cancellation.Token).ConfigureAwait(false);
                var now = Environment.TickCount64;
                if (now < nextReplyAt || datagram.RemoteEndPoint.Port == 0 || datagram.RemoteEndPoint.Address.AddressFamily != AddressFamily.InterNetwork
                    || !(allowLoopback && IPAddress.IsLoopback(datagram.RemoteEndPoint.Address)
                        || LanNetwork.SameSubnet(datagram.RemoteEndPoint.Address, localAddress, prefixLength))
                    || !DiscoveryProtocol.TryRead(datagram.Buffer, out var query) || query.IsReply || query.PeerId == run.PeerId) continue;
                // At most ten equally sized replies per second, globally, with no reflection amplification.
                nextReplyAt = now + 100;
                await reply.SendAsync(DiscoveryProtocol.Reply(query.Nonce, run.PeerId, tcpPort, name),
                    datagram.RemoteEndPoint, run.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (run.Cancellation.IsCancellationRequested) { }
        catch (SocketException) when (run.Cancellation.IsCancellationRequested) { }
        catch (Exception exception) { Fail(run, exception); }
        finally { Finish(run); }
    }

    private async Task RunScanningAsync(Run run, IReadOnlyList<(IPAddress Local, IPEndPoint Target)> endpoints)
    {
        var tasks = run.Sockets.Select(socket => ReceiveRepliesAsync(run, socket)).Append(SendQueriesAsync(run, endpoints)).ToArray();
        try
        {
            await await Task.WhenAny(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (run.Cancellation.IsCancellationRequested) { }
        catch (SocketException) when (run.Cancellation.IsCancellationRequested) { }
        catch (Exception exception) { Fail(run, exception); }
        finally
        {
            run.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { }
            Finish(run);
        }
    }

    private static async Task SendQueriesAsync(Run run, IReadOnlyList<(IPAddress Local, IPEndPoint Target)> endpoints)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            var nonce = Guid.NewGuid();
            run.Catalog!.BeginQuery(nonce, Environment.TickCount64);
            var query = DiscoveryProtocol.Query(nonce, run.PeerId);
            var sent = false;
            SocketException? lastFailure = null;
            for (var index = 0; index < endpoints.Count; index++)
            {
                try
                {
                    await run.Sockets[index].SendAsync(query, endpoints[index].Target, run.Cancellation.Token).ConfigureAwait(false);
                    sent = true;
                }
                catch (SocketException exception) { lastFailure = exception; }
            }
            if (!sent && lastFailure != null) throw lastFailure;
        } while (await timer.WaitForNextTickAsync(run.Cancellation.Token).ConfigureAwait(false));
    }

    private static async Task ReceiveRepliesAsync(Run run, UdpClient socket)
    {
        while (!run.Cancellation.IsCancellationRequested)
        {
            var datagram = await socket.ReceiveAsync(run.Cancellation.Token).ConfigureAwait(false);
            run.Catalog!.Observe(datagram.Buffer, datagram.RemoteEndPoint, Environment.TickCount64, DateTimeOffset.UtcNow);
        }
    }

    private void Fail(Run run, Exception exception)
    {
        lock (gate)
            if (current == run) status = "Nearby PC discovery stopped: " + exception.Message + " Manual connection is still available.";
    }

    private void Finish(Run run)
    {
        run.Cancel();
        lock (gate) { if (current == run) current = null; }
        run.Cancellation.Dispose();
    }

    public void Stop()
    {
        Run? old;
        lock (gate) { old = current; current = null; status = "Nearby PC discovery is stopped."; }
        old?.Cancel();
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Stop();
    }

    private void RequireStopped()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (current != null) throw new InvalidOperationException("Stop nearby PC discovery before starting it again.");
    }

    private static void ValidateIdentity(Guid peerId)
    {
        if (peerId == Guid.Empty) throw new ArgumentException("Configure a stable, nonempty peer ID.", nameof(peerId));
    }

    private sealed class Run(List<UdpClient> sockets, Guid peerId, DiscoveryCatalog? catalog, int boundPort)
    {
        internal List<UdpClient> Sockets { get; } = sockets;
        internal Guid PeerId { get; } = peerId;
        internal DiscoveryCatalog? Catalog { get; } = catalog;
        internal int BoundPort { get; } = boundPort;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task? Lifetime;
        private int cancelled;
        internal void Cancel()
        {
            if (Interlocked.Exchange(ref cancelled, 1) != 0) return;
            Cancellation.Cancel();
            foreach (var socket in Sockets) socket.Dispose();
        }
    }
}
