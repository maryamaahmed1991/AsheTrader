using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AsheTrader.Core.Networking;

public sealed record DiscoveredPeer(Guid PeerId, string Name, IPAddress Address, int Port, DateTimeOffset LastSeen);

internal readonly record struct DiscoveryPacket(bool IsReply, Guid Nonce, Guid PeerId, int Port, string Name);

/// <summary>Discovery contains public connection hints only. Both datagram kinds have equal length.</summary>
internal static class DiscoveryProtocol
{
    internal const int PacketLength = 256;
    private const int NameOffset = 48;
    private const int MaxNameBytes = 192;
    private static readonly byte[] Magic = "ASHTDISC"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Query(Guid nonce, Guid peerId) => Encode(new(false, nonce, peerId, 0, ""));
    internal static byte[] Reply(Guid nonce, Guid peerId, int port, string name) => Encode(new(true, nonce, peerId, port, CleanName(name)));

    private static byte[] Encode(DiscoveryPacket packet)
    {
        if (packet.Nonce == Guid.Empty || packet.PeerId == Guid.Empty || packet.IsReply && (packet.Port is < 1 or > ushort.MaxValue || packet.Name.Length == 0))
            throw new ArgumentException("Discovery requires valid identity and endpoint fields.");
        var bytes = new byte[PacketLength];
        Magic.CopyTo(bytes, 0);
        bytes[8] = 1;
        bytes[9] = packet.IsReply ? (byte)2 : (byte)1;
        packet.Nonce.TryWriteBytes(bytes.AsSpan(12, 16));
        packet.PeerId.TryWriteBytes(bytes.AsSpan(28, 16));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(44), (ushort)packet.Port);
        var length = Utf8.GetBytes(packet.Name, bytes.AsSpan(NameOffset, MaxNameBytes));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(46), (ushort)length);
        return bytes;
    }

    internal static bool TryRead(ReadOnlySpan<byte> bytes, out DiscoveryPacket packet)
    {
        packet = default;
        if (bytes.Length != PacketLength || !bytes[..8].SequenceEqual(Magic) || bytes[8] != 1
            || bytes[9] is not (1 or 2) || bytes[10] != 0 || bytes[11] != 0) return false;
        var nonce = new Guid(bytes.Slice(12, 16));
        var peerId = new Guid(bytes.Slice(28, 16));
        var port = BinaryPrimitives.ReadUInt16BigEndian(bytes[44..]);
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[46..]);
        if (nonce == Guid.Empty || peerId == Guid.Empty || length > MaxNameBytes
            || bytes[(NameOffset + length)..].ContainsAnyExcept((byte)0)) return false;
        if (bytes[9] == 1)
        {
            if (port != 0 || length != 0) return false;
            packet = new(false, nonce, peerId, 0, "");
            return true;
        }
        if (port == 0 || length == 0) return false;
        string name;
        try { name = Utf8.GetString(bytes.Slice(NameOffset, length)); }
        catch (DecoderFallbackException) { return false; }
        if (CleanName(name) != name) return false;
        packet = new(true, nonce, peerId, port, name);
        return true;
    }

    internal static string CleanName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator || rune.Value == '#') continue;
            if (result.Length + rune.Utf16SequenceLength > 64 || bytes + rune.Utf8SequenceLength > MaxNameBytes) break;
            result.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        var clean = result.ToString().Trim();
        return clean.Length == 0 ? "Ashe's Trader PC" : clean;
    }
}

internal sealed class DiscoveryCatalog(Guid localPeerId, bool allowLoopback)
{
    internal const long LifetimeMilliseconds = 8000;
    internal const int MaxPeers = 64;
    private readonly object gate = new();
    private readonly Dictionary<Guid, long> queries = [];
    private readonly Dictionary<(Guid, IPAddress, int), (DiscoveredPeer Peer, long Seen)> peers = [];

    internal void BeginQuery(Guid nonce, long now)
    {
        if (nonce == Guid.Empty) throw new ArgumentException("A query nonce is required.", nameof(nonce));
        lock (gate)
        {
            Expire(now);
            // Queries run every two seconds; retain only a bounded response window.
            if (queries.Count >= 8) queries.Remove(queries.MinBy(x => x.Value).Key);
            queries[nonce] = now;
        }
    }

    internal bool Observe(ReadOnlySpan<byte> bytes, IPEndPoint origin, long now, DateTimeOffset observedAt)
    {
        if (origin.Port == 0 || origin.Address.AddressFamily != AddressFamily.InterNetwork
            || !(LanNetwork.IsPrivateIPv4(origin.Address) || allowLoopback && IPAddress.IsLoopback(origin.Address))
            || !DiscoveryProtocol.TryRead(bytes, out var packet) || !packet.IsReply || packet.PeerId == localPeerId) return false;
        lock (gate)
        {
            Expire(now);
            if (!queries.ContainsKey(packet.Nonce)) return false;
            var key = (packet.PeerId, origin.Address, packet.Port);
            if (!peers.ContainsKey(key) && peers.Count >= MaxPeers) return false;
            peers[key] = (new(packet.PeerId, packet.Name, origin.Address, packet.Port, observedAt), now);
            return true;
        }
    }

    internal IReadOnlyList<DiscoveredPeer> Snapshot(long now)
    {
        lock (gate)
        {
            Expire(now);
            return peers.Values.Select(x => x.Peer).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Address.ToString(), StringComparer.Ordinal).ToArray();
        }
    }

    private void Expire(long now)
    {
        foreach (var entry in queries.Where(x => now - x.Value >= LifetimeMilliseconds).ToArray()) queries.Remove(entry.Key);
        foreach (var entry in peers.Where(x => now - x.Value.Seen >= LifetimeMilliseconds).ToArray()) peers.Remove(entry.Key);
    }
}
