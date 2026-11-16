using System.Net;
using System.Text.Json;
using AsheTrader.Core.Store;

namespace AsheTrader.Core.Tests;

public sealed class StoreTests
{
    private static StoreAccess Access => new(1,"https://catalogue.example/",new string('a',64),"reader");
    [Fact] public void InvitationRoundTripsWithoutLeakingInToString()
    {
        Assert.Equal(Access,StoreAccess.Parse(Access.Encode()));
        Assert.DoesNotContain(Access.Token,Access.ToString());
        Assert.DoesNotContain(Access.Token,Assert.Throws<FormatException>(()=>StoreAccess.Parse(Access.Token)).Message);
    }
    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://localhost/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://example.com/?secret=x")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com/#fragment")]
    public void RejectsUnsafeEndpoint(string url) => Assert.Throws<FormatException>(()=>(Access with {Url=url}).Validate());
    [Fact] public void OwnerKeysAreStableAndIsolatedBetweenStores()
    {
        var key=StorePrivacy.OwnerKey(new string('a',64),123);
        Assert.Equal(64,key.Length);
        Assert.Equal(key,StorePrivacy.OwnerKey(new string('a',64),123));
        Assert.NotEqual(key,StorePrivacy.OwnerKey(new string('a',64),124));
        Assert.NotEqual(key,StorePrivacy.OwnerKey(new string('b',64),123));
    }
    [Fact] public async Task CredentialOnlyInAuthorizationHeaderAndRemoteErrorsSanitized()
    {
        using var client=new StoreApi(new Handler(request=>
        {
            Assert.Equal("Bearer",request.Headers.Authorization?.Scheme);
            Assert.Equal(Access.Token,request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain(Access.Token,request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.Conflict){Content=new StringContent(Access.Token)};
        }));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>client.Browse(Access,CancellationToken.None));
        Assert.DoesNotContain(Access.Token,error.Message);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(response(request));
    }
    [Fact] public void CredentialFileEncryptsSecretsAndRejectsConflictingSaves()
    {
        if(!OperatingSystem.IsWindows())return;
        var directory=Path.Combine(Path.GetTempPath(),"ashe-store-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);var path=Path.Combine(directory,"credentials");
        try
        {
            var first=new AsheTrader.Store.StoreCredentialFile(path);first.Load();
            first.Save(new(){Owner=Access with {Role="owner"}});
            Assert.DoesNotContain(Access.Token,File.ReadAllText(path));
            var second=new AsheTrader.Store.StoreCredentialFile(path);
            Assert.Equal(Access with {Role="owner"},second.Load().Owner);
            first.Save(new(){Publisher=Access with {Role="publisher"}});
            Assert.Throws<IOException>(()=>second.Save(new()));
            Assert.Equal(Access with {Role="publisher"},new AsheTrader.Store.StoreCredentialFile(path).Load().Publisher);
        }
        finally {Directory.Delete(directory,true);}
    }
    [Fact] public async Task MalformedRemoteCatalogueIsRejectedBeforeUI()
    {
        using var client=new StoreApi(new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"name\":\"Store\",\"items\":null,\"sources\":[]}",System.Text.Encoding.UTF8,"application/json")}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>client.Browse(Access,CancellationToken.None));
    }
}
