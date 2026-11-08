using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace AsheTrader.Core.Networking;

/// <summary>
/// Explicitly enabled, authenticated LAN transport for exactly two peers. It queues data
/// only; consumers must dispatch messages to the game framework themselves. Disconnects
/// discard session queues. The application must reconcile its durable operation journal
/// before issuing work after reconnecting. No automatic reconnect or command replay occurs.
/// </summary>
public sealed class LanPeer : IDisposable
{
    public const int MaxMessageBytes = 1024 * 1024;
    public const int QueueCapacity = 32;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private readonly object gate = new();
    private RunContext? current;
    private string status = "LAN pairing is disabled.";
    private bool disposed;

    /// <summary>True while hosting, connecting, or connected; false until explicitly started.</summary>
    public bool IsEnabled { get { lock (gate) return current != null; } }
    public bool IsConnected { get { lock (gate) return current?.Session is { Connected: true }; } }
    /// <summary>
    /// A new identity for every installed socket session, including reconnects by the same
    /// remote peer. Consumers must observe this as well as IsConnected: disconnect and
    /// reconnect can both happen between framework ticks.
    /// </summary>
    public Guid? ConnectionId { get { lock (gate) return current?.Session is { Connected: true } s ? s.ConnectionId : null; } }
    public Guid? RemotePeerId { get { lock (gate) return current?.Session is { Connected: true } s ? s.Secrets.RemotePeerId : null; } }
    /// <summary>Read socket generation and authenticated peer together before accepting a connection.</summary>
    public (Guid ConnectionId, Guid RemotePeerId)? SessionIdentity
    {
        get { lock (gate) return current?.Session is { Connected: true } s ? (s.ConnectionId, s.Secrets.RemotePeerId) : null; }
    }
    public string Status { get { lock (gate) return status; } }
    public int BoundPort { get { lock (gate) return current?.BoundPort ?? 0; } }

    public static string GeneratePairingKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public void StartHost(IPAddress bindAddress, int port, string pairingKey, Guid localPeerId)
    {
        ValidateEndpoint(bindAddress, port, true);
        var key = ParseKey(pairingKey);
        ValidateIdentity(localPeerId);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current != null) throw new InvalidOperationException("Stop the current LAN session before starting another.");
            var listener = new TcpListener(bindAddress, port);
            try
            {
                listener.Start(2);
                var run = new RunContext(key, localPeerId, listener, ((IPEndPoint)listener.LocalEndpoint).Port);
                current = run;
                status = $"Waiting for the paired PC on {bindAddress}:{run.BoundPort}.";
                run.Lifetime = Task.Run(() => AcceptLoopAsync(run));
            }
            catch { listener.Stop(); CryptographicOperations.ZeroMemory(key); throw; }
        }
    }

    public async Task ConnectAsync(IPAddress hostAddress, int port, string pairingKey, Guid localPeerId, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(hostAddress, port, false);
        var key = ParseKey(pairingKey);
        ValidateIdentity(localPeerId);
        RunContext run;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current != null) throw new InvalidOperationException("Stop the current LAN session before starting another.");
            run = new RunContext(key, localPeerId, null, 0);
            current = run;
            status = "Connecting to the paired PC…";
        }
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(run.Cancellation.Token, cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(hostAddress, port, connectTimeout.Token).ConfigureAwait(false);
            var secrets = await PairingHandshake.AuthenticateAsync(client.GetStream(), run.Key, localPeerId, false, connectTimeout.Token).ConfigureAwait(false);
            var session = new Connection(client, secrets, run.Cancellation.Token);
            if (!Install(run, session)) { session.Dispose(); throw new OperationCanceledException("Pairing was stopped."); }
            run.Lifetime = Task.Run(() => RunConnectionAsync(run, session));
        }
        catch (Exception exception)
        {
            client.Dispose();
            lock (gate)
            {
                if (current == run) { current = null; status = "Connection failed: " + Describe(exception); }
            }
            run.Cancel();
            run.ReleaseKey();
            throw;
        }
    }

    public void Send(PeerMessage message, Guid? expectedConnectionId = null)
    {
        var bytes = Serialize(message, false);
        lock (gate)
        {
            if (current?.Session is not { Connected: true } session) throw new InvalidOperationException("The paired PC is disconnected.");
            if (expectedConnectionId.HasValue && session.ConnectionId != expectedConnectionId)
                throw new InvalidOperationException("The connection changed before this message could be sent. Verify the new peer first.");
            if (!session.Outgoing.Writer.TryWrite(bytes)) throw new InvalidOperationException("The LAN send queue is full. Pause the workflow until messages are processed.");
        }
    }

    public bool TryReceive(out PeerMessage message, Guid? expectedConnectionId = null)
    {
        lock (gate)
        {
            if (current?.Session is { Connected: true } session
                && (!expectedConnectionId.HasValue || session.ConnectionId == expectedConnectionId)
                && session.Incoming.Reader.TryRead(out var received))
            { message = received; return true; }
        }
        message = null!;
        return false;
    }

    public void Stop()
    {
        RunContext? old;
        lock (gate) { old = current; current = null; status = "LAN pairing is disabled."; }
        old?.Cancel();
        // The asynchronous lifetime owns and clears key material after all socket work stops.
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Stop();
    }

    private async Task AcceptLoopAsync(RunContext run)
    {
        try
        {
            while (!run.Cancellation.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await run.Listener!.AcceptTcpClientAsync(run.Cancellation.Token).ConfigureAwait(false);
                    client.NoDelay = true;
                    var secrets = await PairingHandshake.AuthenticateAsync(client.GetStream(), run.Key, run.PeerId, true, run.Cancellation.Token).ConfigureAwait(false);
                    var session = new Connection(client, secrets, run.Cancellation.Token);
                    client = null;
                    if (!Install(run, session)) { session.Dispose(); break; }
                    await RunConnectionAsync(run, session).ConfigureAwait(false);
                }
                catch (Exception exception) when (!run.Cancellation.IsCancellationRequested)
                {
                    SetStatus(run, "Pairing attempt failed: " + Describe(exception) + " Waiting for the paired PC.");
                }
                finally { client?.Dispose(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally { run.ReleaseKey(); }
    }

    private bool Install(RunContext run, Connection session)
    {
        lock (gate)
        {
            if (current != run || run.Cancellation.IsCancellationRequested || run.Session != null) return false;
            run.Session = session;
            status = "Connected to the paired PC.";
            return true;
        }
    }

    private async Task RunConnectionAsync(RunContext run, Connection session)
    {
        string reason = "Peer disconnected.";
        var read = ReadLoopAsync(session);
        var write = WriteLoopAsync(session);
        var heartbeat = HeartbeatAsync(session);
        try
        {
            var completed = await Task.WhenAny(read, write, heartbeat).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        catch (Exception exception) { reason = Describe(exception); }
        finally
        {
            lock (gate)
            {
                session.Connected = false;
                if (run.Session == session) run.Session = null;
                if (current == run)
                {
                    status = "Disconnected: " + reason + (run.Listener == null ? " Reconnect explicitly after reviewing the job." : " Waiting for the paired PC.");
                    if (run.Listener == null) current = null;
                }
            }
            session.Cancel();
            try { await Task.WhenAll(read, write, heartbeat).ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
            if (run.Listener == null) { run.Cancel(); run.ReleaseKey(); }
        }
    }

    private static async Task ReadLoopAsync(Connection session)
    {
        var stream = session.Client.GetStream();
        var token = session.Cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            var header = new byte[SecureFrameCodec.HeaderLength];
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var length = SecureFrameCodec.ReadPayloadLength(header);
            var frame = new byte[header.Length + length + SecureFrameCodec.TagLength];
            header.CopyTo(frame, 0);
            await stream.ReadExactlyAsync(frame.AsMemory(header.Length), token).ConfigureAwait(false);
            var message = JsonSerializer.Deserialize<PeerMessage>(session.Secrets.Receiver.Open(frame), JsonOptions)
                ?? throw new InvalidDataException("Invalid peer message.");
            ValidateMessage(message, true);
            Interlocked.Exchange(ref session.LastReceivedAt, Environment.TickCount64);
            if (message.Kind == "__ping")
            {
                if (!session.Outgoing.Writer.TryWrite(Serialize(new("__pong", Guid.NewGuid(), message.Id, string.Empty), true)))
                    throw new IOException("Peer send queue is full.");
            }
            else if (message.Kind != "__pong" && !session.Incoming.Writer.TryWrite(message))
                throw new IOException("Peer receive queue is full. Workflow must pause.");
        }
    }

    private static async Task WriteLoopAsync(Connection session)
    {
        var stream = session.Client.GetStream();
        await foreach (var bytes in session.Outgoing.Reader.ReadAllAsync(session.Cancellation.Token).ConfigureAwait(false))
            await stream.WriteAsync(session.Secrets.Sender.Seal(bytes), session.Cancellation.Token).ConfigureAwait(false);
    }

    private static async Task HeartbeatAsync(Connection session)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(session.Cancellation.Token).ConfigureAwait(false))
        {
            if (Environment.TickCount64 - Interlocked.Read(ref session.LastReceivedAt) > 12_000)
                throw new TimeoutException("The paired PC stopped responding.");
            if (!session.Outgoing.Writer.TryWrite(Serialize(new("__ping", Guid.NewGuid(), null, string.Empty), true)))
                throw new IOException("Peer send queue is full.");
        }
    }

    private static byte[] Serialize(PeerMessage message, bool internalMessage)
    {
        ValidateMessage(message, internalMessage);
        // Bound before encoding too: JSON can only make these strings larger.
        if (message.Payload.Length > MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(message), "Message exceeds the transport limit.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (bytes.Length > MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(message), "Message exceeds the transport limit.");
        return bytes;
    }

    private static void ValidateMessage(PeerMessage message, bool internalMessage)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrEmpty(message.Kind) || message.Kind.Length > 64 || message.Kind.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '.' and not '-' and not '_')
            || message.Id == Guid.Empty || message.Payload == null)
            throw new InvalidDataException("Malformed peer message.");
        if (message.Kind.StartsWith("__", StringComparison.Ordinal)
            && (!internalMessage || message.Kind is not "__ping" and not "__pong"))
            throw new InvalidDataException("Reserved peer message kind.");
    }

    private static void ValidateEndpoint(IPAddress address, int port, bool hosting)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
            throw new ArgumentException("Choose a specific IPv4 LAN address (or loopback for testing).", nameof(address));
        if (port < (hosting ? 0 : 1) || port > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
    }

    private static byte[] ParseKey(string pairingKey)
    {
        ArgumentNullException.ThrowIfNull(pairingKey);
        byte[] key;
        try { key = Convert.FromBase64String(pairingKey.Trim()); }
        catch (FormatException) { throw new ArgumentException("The pairing key must be the generated 32-byte base64 key.", nameof(pairingKey)); }
        if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new ArgumentException("The pairing key must be exactly 32 random bytes.", nameof(pairingKey)); }
        return key;
    }

    private static void ValidateIdentity(Guid peerId)
    {
        if (peerId == Guid.Empty) throw new ArgumentException("Configure a stable, nonempty peer ID.", nameof(peerId));
    }

    private void SetStatus(RunContext run, string value) { lock (gate) { if (current == run) status = value; } }
    private static string Describe(Exception exception) => exception switch
    {
        CryptographicException => "Pairing authentication or encrypted message validation failed.",
        OperationCanceledException => "Connection cancelled or timed out.",
        EndOfStreamException => "Peer closed the connection.",
        _ => exception.Message,
    };

    private sealed class RunContext(byte[] key, Guid peerId, TcpListener? listener, int boundPort)
    {
        internal byte[] Key { get; } = key;
        internal Guid PeerId { get; } = peerId;
        internal TcpListener? Listener { get; } = listener;
        internal int BoundPort { get; } = boundPort;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Connection? Session;
        internal Task? Lifetime;
        internal void Cancel() { Cancellation.Cancel(); Listener?.Stop(); Session?.Cancel(); }
        internal void ReleaseKey() => CryptographicOperations.ZeroMemory(Key);
    }

    private sealed class Connection(TcpClient client, SessionSecrets secrets, CancellationToken lifetimeToken) : IDisposable
    {
        internal Guid ConnectionId { get; } = Guid.NewGuid();
        internal TcpClient Client { get; } = client;
        internal SessionSecrets Secrets { get; } = secrets;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        internal Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        internal Channel<PeerMessage> Incoming { get; } = Channel.CreateBounded<PeerMessage>(new BoundedChannelOptions(QueueCapacity) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        internal bool Connected = true;
        internal long LastReceivedAt = Environment.TickCount64;
        private int cancelled;
        private int disposed;
        internal void Cancel()
        {
            if (Interlocked.Exchange(ref cancelled, 1) != 0) return;
            Connected = false;
            Cancellation.Cancel(); Client.Dispose(); Outgoing.Writer.TryComplete(); Incoming.Writer.TryComplete();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Cancel(); Secrets.Dispose(); Cancellation.Dispose();
        }
    }
}
