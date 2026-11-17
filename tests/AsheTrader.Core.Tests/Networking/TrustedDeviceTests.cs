using System.Net;
using System.Net.Http.Json;
using AsheTrader.Core.Networking;

namespace AsheTrader.Core.Tests.Networking;

public sealed class TrustedDeviceTests
{
    private static DeviceInvitation Invitation() => new(1,"https://catalogue.example/",new string('a',64),Guid.NewGuid(),"Main PC",LanPeer.GeneratePairingKey(),DateTimeOffset.UtcNow.AddMinutes(10));
    [Fact]
    public void InvitationRoundTripsWithoutRenderingSecrets()
    {
        var invite = Invitation();
        Assert.Equal(invite,DeviceInvitation.Parse(invite.Encode()));
        Assert.DoesNotContain(invite.Token,invite.ToString());
        Assert.DoesNotContain(invite.PairingKey,invite.ToString());
        Assert.DoesNotContain(invite.Token,new DeviceCredential(invite.Url,invite.Token,Guid.NewGuid(),true).ToString());
    }
    [Fact]
    public void ExpiredWrongKindOrInsecureInvitationsCannotBeImported()
    {
        var invite = Invitation();
        Assert.Throws<ArgumentException>(()=>DeviceInvitation.Parse("ashe-store:v1:abcd"));
        Assert.Throws<ArgumentException>(()=>DeviceInvitation.Parse((invite with {ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1)}).Encode()));
        Assert.Throws<ArgumentException>(()=>DeviceInvitation.Parse((invite with {Url="http://catalogue.example/"}).Encode()));
        Assert.Throws<ArgumentException>(()=>DeviceInvitation.Parse((invite with {HostPeerId=Guid.Empty}).Encode()));
        Assert.Throws<ArgumentException>(()=>DeviceInvitation.Parse((invite with {PairingKey=Convert.ToBase64String(new byte[16])}).Encode()));
    }
    [Theory]
    [InlineData("10.1.2.3",true)]
    [InlineData("172.16.2.3",true)]
    [InlineData("172.31.2.3",true)]
    [InlineData("192.168.1.2",true)]
    [InlineData("127.0.0.1",false)]
    [InlineData("169.254.169.254",false)]
    [InlineData("172.32.2.3",false)]
    [InlineData("8.8.8.8",false)]
    [InlineData("::1",false)]
    public void HostedEndpointsAreOnlyPrivateIpv4(string address,bool expected) => Assert.Equal(expected,TrustedDevicePolicy.IsPrivateAddress(address));
    [Fact]
    public void StaleOrMismatchedHostDiscoveryCannotRedirectTheWorker()
    {
        var host=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        var state=new DeviceLinkState(Guid.NewGuid(),host,"Main",Guid.NewGuid(),"Guest","192.168.1.2",45874,now,null);
        Assert.True(TrustedDevicePolicy.UsableEndpoint(state,host,now));
        Assert.False(TrustedDevicePolicy.UsableEndpoint(state,Guid.NewGuid(),now));
        Assert.False(TrustedDevicePolicy.UsableEndpoint(state with {HostSeen=now.AddMinutes(-3)},host,now));
        Assert.False(TrustedDevicePolicy.UsableEndpoint(state with {HostSeen=now.AddMinutes(2)},host,now));
        Assert.False(TrustedDevicePolicy.UsableEndpoint(state with {Port=0},host,now));
        Assert.False(TrustedDevicePolicy.UsableEndpoint(state with {Address="8.8.8.8"},host,now));
    }
    [Fact]
    public async Task RevokedLinkIsDistinguishedFromNetworkFailureWithoutLeakingResponse()
    {
        using var api=new DeviceApi(new Handler(_=>new HttpResponseMessage(HttpStatusCode.Unauthorized){Content=new StringContent("secret SQL and tokens")}));
        var invite=Invitation();
        var error=await Assert.ThrowsAsync<HttpRequestException>(()=>api.Poll(new(invite.Url,invite.Token,Guid.NewGuid(),false),"",0,CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized,error.StatusCode);Assert.DoesNotContain("SQL",error.Message);Assert.DoesNotContain(invite.Token,error.Message);
    }
    [Fact]
    public async Task DeviceSecretOnlyTravelsInAuthorizationHeader()
    {
        var invite=Invitation();
        using var api=new DeviceApi(new Handler(request=>
        {
            Assert.Equal("/v1/device-link",request.RequestUri!.AbsolutePath);
            Assert.Equal("",request.RequestUri.Query);Assert.Equal(invite.Token,request.Headers.Authorization!.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new DeviceLinkState(Guid.NewGuid(),invite.HostPeerId,"Main",Guid.NewGuid(),"Guest","",0,null,null))};
        }));
        await api.Poll(new(invite.Url,invite.Token,Guid.NewGuid(),false),"",0,CancellationToken.None);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(respond(request));}
}
