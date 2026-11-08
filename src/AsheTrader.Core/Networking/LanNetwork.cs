using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AsheTrader.Core.Networking;

public sealed record LanAddress(IPAddress Address, IPAddress BroadcastAddress, string Label, bool Preferred)
{
    internal int PrefixLength { get; init; }
}

/// <summary>Candidate home-network interfaces. Public, loopback, and tunnel addresses are excluded.</summary>
public static class LanNetwork
{
    public static IReadOnlyList<LanAddress> GetLocalAddresses()
    {
        var addresses = new List<LanAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            var preferred = properties.GatewayAddresses.Any(x => IsPrivateIPv4(x.Address));
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (!IsPrivateIPv4(unicast.Address) || unicast.PrefixLength is < 1 or > 30) continue;
                var broadcast = BroadcastAddress(unicast.Address, unicast.PrefixLength);
                addresses.Add(new(unicast.Address, broadcast, adapter.Name, preferred) { PrefixLength = unicast.PrefixLength });
            }
        }
        return addresses.DistinctBy(x => x.Address).OrderByDescending(x => x.Preferred)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Address.ToString(), StringComparer.Ordinal).ToArray();
    }

    public static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }

    internal static IPAddress BroadcastAddress(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 1 or > 30)
            throw new ArgumentException("Choose a usable IPv4 subnet.");
        var bytes = address.GetAddressBytes();
        var mask = uint.MaxValue << (32 - prefixLength);
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value | ~mask);
        return new(bytes);
    }

    internal static bool SameSubnet(IPAddress address, IPAddress local, int prefixLength)
    {
        if (!IsPrivateIPv4(address) || !IsPrivateIPv4(local)) return false;
        var remoteValue = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        var localValue = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(local.GetAddressBytes());
        if (prefixLength is < 1 or > 30) return false;
        var mask = uint.MaxValue << (32 - prefixLength);
        var hostMask = ~mask;
        var broadcastValue = localValue | hostMask;
        return (remoteValue & mask) == (localValue & mask) && remoteValue != broadcastValue && (remoteValue & hostMask) != 0;
    }
}
