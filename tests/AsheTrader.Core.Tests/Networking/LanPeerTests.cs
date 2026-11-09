using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AsheTrader.Core.Networking;

namespace AsheTrader.Core.Tests.Networking;

public sealed class LanPeerTests
{
    [Fact]
    public void ConstructionDoesNotStartAListenerOrAllowSending()
    {
        using var peer = new LanPeer();
        Assert.False(peer.IsEnabled);
        Assert.False(peer.IsConnected);
        Assert.Equal(0, peer.BoundPort);
        Assert.Null(peer.RemotePeerId);
        Assert.Null(peer.ConnectionId);
        Assert.False(peer.TryReceive(out _));
        Assert.Throws<InvalidOperationException>(() => peer.Send(Message("command", "{}")));
    }

    [Fact]
    public async Task PairedPeersExchangeMessagesInBothDirections()
    {
        using var host = new LanPeer();
        using var worker = new LanPeer();
        var secret = LanPeer.GeneratePairingKey();
        var hostId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        host.StartHost(IPAddress.Loopback, 0, secret, hostId);
        Assert.True(host.IsEnabled);
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, workerId);
        await Until(() => host.IsConnected);
        Assert.True(worker.IsEnabled);
        Assert.Equal(workerId, host.RemotePeerId);
        Assert.Equal(hostId, worker.RemotePeerId);
        var command = Message("begin", "{\"item\":10155,\"amount\":999}");
        var response = new PeerMessage("result", Guid.NewGuid(), command.Id, "{\"observed\":999}");
        host.Send(command);
        Assert.Equal(command, await Receive(worker));
        worker.Send(response);
        Assert.Equal(response, await Receive(host));
    }

    [Fact]
    public async Task IncorrectPairingKeyNeverMakesApplicationMessagesAvailable()
    {
        using var host = new LanPeer();
        using var worker = new LanPeer();
        var key = LanPeer.GeneratePairingKey();
        host.StartHost(IPAddress.Loopback, 0, key, Guid.NewGuid());
        await Assert.ThrowsAnyAsync<CryptographicException>(() => worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, LanPeer.GeneratePairingKey(), Guid.NewGuid()));
        Assert.False(worker.IsConnected);
        Assert.False(host.IsConnected);
        Assert.False(host.TryReceive(out _));
        // A failed attempt does not poison the listener for the legitimate peer.
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, key, Guid.NewGuid());
        await Until(() => host.IsConnected);
        Assert.True(worker.IsConnected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyProtocolIsRejectedBeforeSessionAndListenerAcceptsCurrentPeer(int legacyVersion)
    {
        using var host = new LanPeer();
        var key = LanPeer.GeneratePairingKey();
        host.StartHost(IPAddress.Loopback, 0, key, Guid.NewGuid());
        using var legacy = new System.Net.Sockets.TcpClient();
        await legacy.ConnectAsync(IPAddress.Loopback, host.BoundPort);
        // Older workers lack the current workflow/AutoRetainer metadata contract.
        var hello = new byte[61];
        "ASHETRD1"u8.CopyTo(hello);
        BinaryPrimitives.WriteInt32BigEndian(hello.AsSpan(8), legacyVersion);
        hello[12] = 0;
        Guid.NewGuid().TryWriteBytes(hello.AsSpan(13, 16));
        RandomNumberGenerator.Fill(hello.AsSpan(29, 32));
        await legacy.GetStream().WriteAsync(hello);

        await Until(() => host.Status.Contains("Update Ashe's Trader to the same version on both PCs", StringComparison.Ordinal));
        Assert.True(host.IsEnabled);
        Assert.False(host.IsConnected);
        Assert.Null(host.SessionIdentity);
        Assert.Null(host.RemotePeerId);
        Assert.False(host.TryReceive(out _));
        Assert.Throws<InvalidOperationException>(() => host.Send(Message("roster", "must not send before authentication")));

        using var worker = new LanPeer();
        var workerId = Guid.NewGuid();
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, key, workerId);
        await Until(() => host.IsConnected);
        Assert.Equal(workerId, host.RemotePeerId);
        var refresh = Message("roster-request", "{}");
        host.Send(refresh);
        Assert.Equal(refresh, await Receive(worker));
        var response = Message("roster", "current protocol peer");
        worker.Send(response);
        Assert.Equal(response, await Receive(host));
    }

    [Fact]
    public async Task DisconnectClearsUnreadMessagesAndNewConnectionDoesNotReplayThem()
    {
        using var host = new LanPeer();
        using var worker = new LanPeer();
        var secret = LanPeer.GeneratePairingKey();
        var workerId = Guid.NewGuid();
        host.StartHost(IPAddress.Loopback, 0, secret, Guid.NewGuid());
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, workerId);
        await Until(() => host.IsConnected);
        host.Send(Message("old-command", "must not survive reconnection"));
        // A roundtrip barrier proves the old stream was active; leave old-command unread.
        var barrier = Message("barrier", "{}");
        worker.Send(barrier);
        Assert.Equal(barrier, await Receive(host));
        worker.Stop();
        Assert.False(worker.TryReceive(out _));
        await Until(() => !host.IsConnected);
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, workerId);
        await Until(() => host.IsConnected);
        Assert.False(worker.TryReceive(out _));
        var fresh = Message("fresh-command", "new session only");
        host.Send(fresh);
        Assert.Equal(fresh, await Receive(worker));
    }

    [Fact]
    public async Task StopCancelsAnIncompleteHandshakePromptly()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var worker = new LanPeer();
        var connection = worker.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, LanPeer.GeneratePairingKey(), Guid.NewGuid());
        using var accepted = await listener.AcceptTcpClientAsync();
        worker.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(worker.IsConnected);
        Assert.False(worker.TryReceive(out _));
    }

    [Fact]
    public void EndpointsAndPairingKeysMustBeExplicitAndValid()
    {
        using var peer = new LanPeer();
        Assert.Throws<ArgumentException>(() => peer.StartHost(IPAddress.Any, 55001, LanPeer.GeneratePairingKey(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => peer.StartHost(IPAddress.Loopback, 55001, "easy password", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => peer.StartHost(IPAddress.Loopback, 55001, Convert.ToBase64String(new byte[16]), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => peer.StartHost(IPAddress.Loopback, 55001, LanPeer.GeneratePairingKey(), Guid.Empty));
        Assert.Equal(32, Convert.FromBase64String(LanPeer.GeneratePairingKey()).Length);
    }

    [Fact]
    public async Task OversizedOrReservedApplicationMessagesAreRejectedBeforeSending()
    {
        using var host = new LanPeer();
        using var worker = new LanPeer();
        var secret = LanPeer.GeneratePairingKey();
        host.StartHost(IPAddress.Loopback, 0, secret, Guid.NewGuid());
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, Guid.NewGuid());
        await Until(() => host.IsConnected);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Send(Message("large", new string('a', LanPeer.MaxMessageBytes))));
        Assert.Throws<InvalidDataException>(() => host.Send(Message("__ping", "")));
        Assert.Throws<InvalidDataException>(() => host.Send(new PeerMessage("command", Guid.Empty, null, "{}")));
        var valid = Message("valid", "still connected");
        host.Send(valid);
        Assert.Equal(valid, await Receive(worker));
    }

    [Fact]
    public async Task ListenerCanBeStoppedAndRestartedWithoutOldSessionState()
    {
        using var host = new LanPeer();
        using var worker = new LanPeer();
        var secret = LanPeer.GeneratePairingKey();
        var hostId = Guid.NewGuid();
        host.StartHost(IPAddress.Loopback, 0, secret, hostId);
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, Guid.NewGuid());
        await Until(() => host.IsConnected);
        host.Stop();
        Assert.False(host.IsEnabled);
        await Until(() => !worker.IsConnected);
        Assert.False(worker.IsEnabled);
        Assert.Null(host.RemotePeerId);
        host.StartHost(IPAddress.Loopback, 0, secret, hostId);
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, Guid.NewGuid());
        await Until(() => host.IsConnected);
        Assert.Equal(hostId, worker.RemotePeerId);
    }

    [Fact]
    public async Task ReconnectionBySamePeerHasANewConnectionIdentity()
    {
        using var host = new LanPeer();
        using var worker = new LanPeer();
        var secret = LanPeer.GeneratePairingKey();
        var hostId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        host.StartHost(IPAddress.Loopback, 0, secret, hostId);
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, workerId);
        await Until(() => host.IsConnected);
        var oldHostSession = host.ConnectionId;
        var oldWorkerSession = worker.ConnectionId;
        Assert.NotNull(oldHostSession);
        Assert.NotNull(oldWorkerSession);
        worker.Stop();
        Assert.Null(worker.ConnectionId);
        await Until(() => !host.IsConnected);
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, workerId);
        await Until(() => host.IsConnected);
        Assert.Equal(workerId, host.RemotePeerId);
        Assert.NotNull(host.ConnectionId);
        Assert.NotEqual(oldHostSession, host.ConnectionId);
        Assert.NotEqual(oldWorkerSession, worker.ConnectionId);
    }

    [Fact]
    public async Task AcceptedSessionGuardsRejectMessagesAfterReconnectWithoutConsumingTheNewSession()
    {
        using var host = new LanPeer();
        using var worker = new LanPeer();
        var secret = LanPeer.GeneratePairingKey();
        var workerId = Guid.NewGuid();
        host.StartHost(IPAddress.Loopback, 0, secret, Guid.NewGuid());
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, workerId);
        await Until(() => host.IsConnected);
        var accepted = host.SessionIdentity!.Value;
        Assert.Equal(workerId, accepted.RemotePeerId);
        worker.Stop();
        await Until(() => !host.IsConnected);
        await worker.ConnectAsync(IPAddress.Loopback, host.BoundPort, secret, workerId);
        await Until(() => host.IsConnected);
        var replacement = host.SessionIdentity!.Value;
        Assert.NotEqual(accepted.ConnectionId, replacement.ConnectionId);
        Assert.Equal(accepted.RemotePeerId, replacement.RemotePeerId);

        Assert.Throws<InvalidOperationException>(() => host.Send(Message("private-roster", "must not leak"), accepted.ConnectionId));
        var fresh = Message("accepted", "new session");
        host.Send(fresh, replacement.ConnectionId);
        Assert.Equal(fresh, await Receive(worker));

        var response = Message("response", "keep queued until accepted");
        worker.Send(response, worker.SessionIdentity!.Value.ConnectionId);
        PeerMessage? received = null;
        await Until(() =>
        {
            Assert.False(host.TryReceive(out _, accepted.ConnectionId));
            return host.TryReceive(out received!, replacement.ConnectionId);
        });
        Assert.Equal(response, received);
    }

    [Fact]
    public void TamperedFrameFailsAuthenticationWithoutConsumingItsSequence()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var prefix = RandomNumberGenerator.GetBytes(4);
        using var sender = new SecureFrameCodec(key, prefix);
        using var receiver = new SecureFrameCodec(key, prefix);
        var clear = Encoding.UTF8.GetBytes("secret character inventory");
        var frame = sender.Seal(clear);
        var corrupt = frame.ToArray();
        corrupt[SecureFrameCodec.HeaderLength] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => receiver.Open(corrupt));
        Assert.Equal(clear, receiver.Open(frame));
    }

    [Fact]
    public void ReplayedAndReorderedFramesAreRejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var prefix = RandomNumberGenerator.GetBytes(4);
        using var sender = new SecureFrameCodec(key, prefix);
        using var receiver = new SecureFrameCodec(key, prefix);
        var first = sender.Seal("first"u8);
        var second = sender.Seal("second"u8);
        Assert.ThrowsAny<CryptographicException>(() => receiver.Open(second));
        Assert.Equal("first", Encoding.UTF8.GetString(receiver.Open(first)));
        Assert.ThrowsAny<CryptographicException>(() => receiver.Open(first));
        Assert.Equal("second", Encoding.UTF8.GetString(receiver.Open(second)));
    }

    [Fact]
    public void OversizedFrameLengthIsRejectedBeforeAllocation()
    {
        var header = new byte[SecureFrameCodec.HeaderLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => SecureFrameCodec.ReadPayloadLength(header));
    }

    private static PeerMessage Message(string kind, string payload) => new(kind, Guid.NewGuid(), null, payload);

    private static async Task<PeerMessage> Receive(LanPeer peer)
    {
        PeerMessage? result = null;
        await Until(() => peer.TryReceive(out result!));
        return result!;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
