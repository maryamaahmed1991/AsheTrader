using System.Net;
using System.Text;
using AsheTrader.Core.Networking;

namespace AsheTrader.Core.Tests.Networking;

public sealed class LanDiscoveryTests
{
    private static readonly Guid LocalId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HostId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPEndPoint Origin = new(IPAddress.Parse("192.168.1.200"), 50000);

    [Fact]
    public void ConstructingDiscoveryDoesNotOpenSocketsOrExposeHosts()
    {
        using var discovery = new LanDiscovery();
        Assert.False(discovery.IsEnabled);
        Assert.Equal(0, discovery.BoundPort);
        Assert.Empty(discovery.Snapshot());
        discovery.Stop();
        Assert.False(discovery.IsEnabled);
    }

    [Fact]
    public void ProtocolCorrelatesIdentityAndNonceWithoutSecretsOrAmplification()
    {
        var nonce = Guid.NewGuid();
        var query = DiscoveryProtocol.Query(nonce, LocalId);
        var reply = DiscoveryProtocol.Reply(nonce, HostId, 45874, "Gaming PC");
        Assert.Equal(256, query.Length);
        Assert.Equal(query.Length, reply.Length);
        Assert.True(DiscoveryProtocol.TryRead(query, out var request));
        Assert.False(request.IsReply);
        Assert.Equal(LocalId, request.PeerId);
        Assert.Equal(nonce, request.Nonce);
        Assert.True(DiscoveryProtocol.TryRead(reply, out var response));
        Assert.True(response.IsReply);
        Assert.Equal(HostId, response.PeerId);
        Assert.Equal(nonce, response.Nonce);
        Assert.Equal(45874, response.Port);
        Assert.Equal("Gaming PC", response.Name);
    }

    [Fact]
    public void MalformedOrDifferentProtocolPacketsCannotBecomeHosts()
    {
        var valid = DiscoveryProtocol.Reply(Guid.NewGuid(), HostId, 45874, "Host");
        Assert.False(DiscoveryProtocol.TryRead(valid[..^1], out _));
        Assert.False(DiscoveryProtocol.TryRead([.. valid, 0], out _));
        foreach (var index in new[] { 0, 8, 9, 10, 11, 255 })
        {
            var changed = valid.ToArray(); changed[index] = 99;
            Assert.False(DiscoveryProtocol.TryRead(changed, out _));
        }
        var emptyIdentity = valid.ToArray(); Array.Clear(emptyIdentity, 28, 16);
        Assert.False(DiscoveryProtocol.TryRead(emptyIdentity, out _));
        var emptyNonce = valid.ToArray(); Array.Clear(emptyNonce, 12, 16);
        Assert.False(DiscoveryProtocol.TryRead(emptyNonce, out _));
        var emptyPort = valid.ToArray(); Array.Clear(emptyPort, 44, 2);
        Assert.False(DiscoveryProtocol.TryRead(emptyPort, out _));
        var invalidUtf8 = valid.ToArray(); invalidUtf8[48] = 0xff;
        Assert.False(DiscoveryProtocol.TryRead(invalidUtf8, out _));
        var queryWithPayload = DiscoveryProtocol.Query(Guid.NewGuid(), LocalId); queryWithPayload[45] = 12;
        Assert.False(DiscoveryProtocol.TryRead(queryWithPayload, out _));
    }

    [Fact]
    public void DisplayNamesAreBoundedAndDoNotInjectControlsOrImGuiIdentifiers()
    {
        var reply = DiscoveryProtocol.Reply(Guid.NewGuid(), HostId, 45874, "  Home\nPC##hidden\u202e" + new string('界', 100));
        Assert.True(DiscoveryProtocol.TryRead(reply, out var packet));
        Assert.StartsWith("HomePChidden", packet.Name);
        Assert.True(packet.Name.Length <= 64);
        Assert.True(Encoding.UTF8.GetByteCount(packet.Name) <= 192);
        Assert.DoesNotContain('#', packet.Name);
        Assert.DoesNotContain('\n', packet.Name);
        Assert.DoesNotContain('\u202e', packet.Name);
        Assert.Throws<ArgumentException>(() => DiscoveryProtocol.Query(Guid.Empty, LocalId));
        Assert.Throws<ArgumentException>(() => DiscoveryProtocol.Reply(Guid.NewGuid(), HostId, 0, "Host"));
    }

    [Fact]
    public void CatalogRequiresCurrentQueryAndUsesTheObservedSourceAddress()
    {
        var catalog = new DiscoveryCatalog(LocalId, false);
        var nonce = Guid.NewGuid();
        var reply = DiscoveryProtocol.Reply(nonce, HostId, 45874, "Host");
        Assert.False(catalog.Observe(reply, Origin, 1000, Now));
        catalog.BeginQuery(nonce, 1000);
        Assert.True(catalog.Observe(reply, Origin, 1001, Now));
        var discovered = Assert.Single(catalog.Snapshot(1001));
        Assert.Equal(Origin.Address, discovered.Address);
        Assert.Equal(45874, discovered.Port);
        Assert.Equal(HostId, discovered.PeerId);
        Assert.Equal(Now, discovered.LastSeen);
        Assert.False(catalog.Observe(DiscoveryProtocol.Reply(nonce, LocalId, 45874, "Self"), Origin, 1001, Now));
        Assert.False(catalog.Observe(DiscoveryProtocol.Query(nonce, HostId), Origin, 1001, Now));
        Assert.False(catalog.Observe(reply, new(IPAddress.Parse("8.8.8.8"), 50000), 1001, Now));
        Assert.False(catalog.Observe(reply, new(IPAddress.IPv6Loopback, 50000), 1001, Now));
        Assert.False(catalog.Observe(reply, new(IPAddress.Loopback, 50000), 1001, Now));
        Assert.False(catalog.Observe(reply, new(Origin.Address, 0), 1001, Now));
    }

    [Fact]
    public void CatalogExpiresHostsAndOldQueryNoncesAndRefreshesLiveHosts()
    {
        var catalog = new DiscoveryCatalog(LocalId, false);
        var nonce = Guid.NewGuid();
        catalog.BeginQuery(nonce, 0);
        var reply = DiscoveryProtocol.Reply(nonce, HostId, 45874, "Host");
        Assert.True(catalog.Observe(reply, Origin, 0, Now));
        Assert.Single(catalog.Snapshot(7999));
        Assert.Empty(catalog.Snapshot(8000));
        Assert.False(catalog.Observe(reply, Origin, 8000, Now));
        var freshNonce = Guid.NewGuid(); catalog.BeginQuery(freshNonce, 8001);
        Assert.True(catalog.Observe(DiscoveryProtocol.Reply(freshNonce, HostId, 45875, "Renamed"), Origin, 8001, Now.AddSeconds(8)));
        Assert.Equal("Renamed", Assert.Single(catalog.Snapshot(8001)).Name);
    }

    [Fact]
    public void CatalogMemoryIsBoundedEvenWhenManyAdvertisementsArrive()
    {
        var catalog = new DiscoveryCatalog(LocalId, false);
        var nonce = Guid.NewGuid(); catalog.BeginQuery(nonce, 0);
        var firstId = Guid.NewGuid();
        Assert.True(catalog.Observe(DiscoveryProtocol.Reply(nonce, firstId, 45874, "First"), Origin, 0, Now));
        for (var index = 1; index < DiscoveryCatalog.MaxPeers; index++)
            Assert.True(catalog.Observe(DiscoveryProtocol.Reply(nonce, Guid.NewGuid(), 45874, "Host"), Origin, 0, Now));
        Assert.False(catalog.Observe(DiscoveryProtocol.Reply(nonce, Guid.NewGuid(), 45874, "Overflow"), Origin, 0, Now));
        Assert.True(catalog.Observe(DiscoveryProtocol.Reply(nonce, firstId, 45874, "Renamed"), Origin, 1, Now));
        Assert.Equal(DiscoveryCatalog.MaxPeers, catalog.Snapshot(1).Count);
    }

    [Theory]
    [InlineData("192.168.1.200", 24, "192.168.1.255", "192.168.1.12", "192.168.2.12")]
    [InlineData("10.10.10.3", 23, "10.10.11.255", "10.10.11.2", "10.10.12.2")]
    [InlineData("172.16.1.1", 30, "172.16.1.3", "172.16.1.2", "172.16.1.4")]
    public void DirectedBroadcastsRespectEachInterfacesActualPrefix(string local, int prefix, string broadcast, string neighbor, string otherSubnet)
    {
        var address = IPAddress.Parse(local);
        Assert.Equal(IPAddress.Parse(broadcast), LanNetwork.BroadcastAddress(address, prefix));
        Assert.True(LanNetwork.SameSubnet(IPAddress.Parse(neighbor), address, prefix));
        Assert.False(LanNetwork.SameSubnet(IPAddress.Parse(otherSubnet), address, prefix));
        Assert.False(LanNetwork.SameSubnet(IPAddress.Parse(broadcast), address, prefix));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.2", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.1.2", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::ffff:192.168.1.2", false)]
    public void OnlyRfc1918Ipv4AddressesAreAutomaticallyDiscovered(string address, bool expected) =>
        Assert.Equal(expected, LanNetwork.IsPrivateIPv4(IPAddress.Parse(address)));

    [Fact]
    public async Task LocalhostQueryFindsOnlyTheOtherHostAndStopClearsItsResult()
    {
        using var host = new LanDiscovery();
        using var worker = new LanDiscovery();
        host.StartAdvertisingForTest(45874, HostId, "Coordinator");
        Assert.True(host.IsEnabled);
        worker.StartScanningForTest(LocalId, new(IPAddress.Loopback, host.BoundPort));
        await Until(() => worker.Snapshot().Count == 1);
        var found = Assert.Single(worker.Snapshot());
        Assert.Equal(HostId, found.PeerId);
        Assert.Equal(IPAddress.Loopback, found.Address);
        Assert.Equal(45874, found.Port);
        Assert.Equal("Coordinator", found.Name);
        worker.Stop();
        Assert.Empty(worker.Snapshot());
        Assert.False(worker.IsEnabled);
        worker.StartScanningForTest(HostId, new(IPAddress.Loopback, host.BoundPort));
        await Task.Delay(150);
        Assert.Empty(worker.Snapshot());
        host.Stop();
        Assert.False(host.IsEnabled);
    }

    [Fact]
    public async Task DiscoveryRestartDoesNotRetainThePreviousHostsOrNonce()
    {
        using var host = new LanDiscovery();
        using var worker = new LanDiscovery();
        host.StartAdvertisingForTest(45874, HostId, "First host");
        worker.StartScanningForTest(LocalId, new(IPAddress.Loopback, host.BoundPort));
        await Until(() => worker.Snapshot().Count == 1);
        worker.Stop(); host.Stop();
        var nextHostId = Guid.NewGuid();
        host.StartAdvertisingForTest(50000, nextHostId, "Replacement host");
        worker.StartScanningForTest(LocalId, new(IPAddress.Loopback, host.BoundPort));
        await Until(() => worker.Snapshot().Count == 1);
        var found = Assert.Single(worker.Snapshot());
        Assert.Equal(nextHostId, found.PeerId);
        Assert.Equal(50000, found.Port);
    }

    private static async Task Until(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("Discovery did not respond on localhost.");
            await Task.Delay(10);
        }
    }
}
