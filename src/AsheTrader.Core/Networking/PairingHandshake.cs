using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AsheTrader.Core.Networking;

internal sealed record SessionSecrets(Guid RemotePeerId, SecureFrameCodec Sender, SecureFrameCodec Receiver) : IDisposable
{
    public void Dispose() { Sender.Dispose(); Receiver.Dispose(); }
}

internal static class PairingHandshake
{
    private const int HelloLength = 61;
    private static ReadOnlySpan<byte> Magic => "ASHETRD1"u8;
    // Version 5 adds paired live inventory tallies and durable automatic reconciliation.
    internal const int ProtocolVersion = 5;

    internal static async Task<SessionSecrets> AuthenticateAsync(NetworkStream stream, byte[] pairingKey, Guid localPeerId, bool host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        var localHello = CreateHello(localPeerId, host);
        byte[] clientHello;
        byte[] hostHello;
        Guid remoteId;
        if (host)
        {
            clientHello = await ReadAsync(stream, HelloLength, token).ConfigureAwait(false);
            remoteId = ValidateHello(clientHello, false, localPeerId);
            hostHello = localHello;
            var hostProof = Proof(pairingKey, "host", clientHello, hostHello);
            await stream.WriteAsync(hostHello, token).ConfigureAwait(false);
            await stream.WriteAsync(hostProof, token).ConfigureAwait(false);
            var received = await ReadAsync(stream, 32, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(received, Proof(pairingKey, "client", clientHello, hostHello)))
                throw new CryptographicException("Pairing authentication failed.");
            await stream.WriteAsync(Proof(pairingKey, "ready", clientHello, hostHello), token).ConfigureAwait(false);
        }
        else
        {
            clientHello = localHello;
            await stream.WriteAsync(clientHello, token).ConfigureAwait(false);
            hostHello = await ReadAsync(stream, HelloLength, token).ConfigureAwait(false);
            remoteId = ValidateHello(hostHello, true, localPeerId);
            var received = await ReadAsync(stream, 32, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(received, Proof(pairingKey, "host", clientHello, hostHello)))
                throw new CryptographicException("Pairing authentication failed.");
            await stream.WriteAsync(Proof(pairingKey, "client", clientHello, hostHello), token).ConfigureAwait(false);
            received = await ReadAsync(stream, 32, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(received, Proof(pairingKey, "ready", clientHello, hostHello)))
                throw new CryptographicException("Pairing authentication failed.");
        }

        var salt = SHA256.HashData(Join(clientHello, hostHello));
        var hostToClient = HKDF.DeriveKey(HashAlgorithmName.SHA256, pairingKey, 36, salt, "AsheTrader/v1/host-to-client"u8.ToArray());
        var clientToHost = HKDF.DeriveKey(HashAlgorithmName.SHA256, pairingKey, 36, salt, "AsheTrader/v1/client-to-host"u8.ToArray());
        try
        {
            var send = host ? hostToClient : clientToHost;
            var receive = host ? clientToHost : hostToClient;
            return new SessionSecrets(remoteId, new SecureFrameCodec(send.AsSpan(0, 32), send.AsSpan(32)), new SecureFrameCodec(receive.AsSpan(0, 32), receive.AsSpan(32)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hostToClient);
            CryptographicOperations.ZeroMemory(clientToHost);
        }
    }

    private static byte[] CreateHello(Guid peerId, bool host)
    {
        var hello = new byte[HelloLength];
        Magic.CopyTo(hello);
        BinaryPrimitives.WriteInt32BigEndian(hello.AsSpan(8), ProtocolVersion);
        hello[12] = host ? (byte)1 : (byte)0;
        peerId.TryWriteBytes(hello.AsSpan(13, 16));
        RandomNumberGenerator.Fill(hello.AsSpan(29, 32));
        return hello;
    }

    private static Guid ValidateHello(byte[] hello, bool expectHost, Guid localPeerId)
    {
        if (!hello.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("Unsupported Ashe's Trader pairing protocol or role.");
        if (BinaryPrimitives.ReadInt32BigEndian(hello.AsSpan(8)) != ProtocolVersion)
            throw new InvalidDataException("Update Ashe's Trader to the same version on both PCs before pairing.");
        if (hello[12] != (expectHost ? 1 : 0))
            throw new InvalidDataException("Unsupported Ashe's Trader pairing protocol or role.");
        var id = new Guid(hello.AsSpan(13, 16));
        if (id == Guid.Empty || id == localPeerId) throw new InvalidDataException("Peer identities must be distinct and nonempty.");
        return id;
    }

    private static byte[] Proof(byte[] key, string role, byte[] clientHello, byte[] hostHello) =>
        HMACSHA256.HashData(key, Join(Encoding.UTF8.GetBytes("AsheTrader/v1/auth/" + role + "\0"), clientHello, hostHello));

    private static byte[] Join(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts) { part.CopyTo(result, offset); offset += part.Length; }
        return result;
    }

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int length, CancellationToken token)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
}
