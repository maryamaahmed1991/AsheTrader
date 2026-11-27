using System.Text.Json;
using AsheTrader.Core.Networking;

namespace AsheTrader.Core.Store;

public sealed record FriendRecipient(string ContentId, string Name, string World);
public sealed record FriendGrant(string ReaderId, string SourceId);
public sealed record FriendOffer(string SourceId, string Label, long Revision, DateTimeOffset UpdatedAt, StoreStockItem[] Items);
public sealed record FriendOfferState(long Revision, int Enabled, string Label, Guid PeerId, string Items);
public sealed record FriendOrder(Guid Id, string SourceId, string ReaderId, Guid HostPeer, Guid GuestPeer,
    FriendRecipient Recipient, StoreStockItem[] Items, string State, DateTimeOffset CreatedAt, string? Receipt,DateTimeOffset? GuestSeen=null);
public sealed record FriendEnvelope(long Sequence, PeerMessage Message);
public sealed record FriendExchange(FriendEnvelope[] Messages);

public sealed class FriendTransferApi : IDisposable
{
    private readonly StoreApi api = new();
    public Task<FriendGrant[]> Grants(StoreAccess a,CancellationToken ct) => api.Send<FriendGrant[]>(a,HttpMethod.Get,"v1/transfers/grants",null,ct);
    public Task<JsonElement> Grant(StoreAccess a,string reader,string source,bool enable,CancellationToken ct) => api.Send<JsonElement>(a,enable?HttpMethod.Put:HttpMethod.Delete,"v1/transfers/grants",new {readerId=reader,sourceId=source},ct);
    public Task<FriendOfferState?> Offer(StoreAccess a,CancellationToken ct) => api.Send<FriendOfferState?>(a,HttpMethod.Get,"v1/transfers/offer",null,ct);
    public Task<JsonElement> Publish(StoreAccess a,long revision,Guid peer,string label,StoreStockItem[] items,CancellationToken ct) => api.Send<JsonElement>(a,HttpMethod.Put,"v1/transfers/offer",new {revision,peerId=peer,label,items,enabled=true},ct);
    public Task<FriendOffer[]> Offers(StoreAccess a,CancellationToken ct) => api.Send<FriendOffer[]>(a,HttpMethod.Get,"v1/transfers/offers",null,ct);
    public Task<FriendOrder[]> Orders(StoreAccess a,CancellationToken ct) => api.Send<FriendOrder[]>(a,HttpMethod.Get,"v1/transfers/orders",null,ct);
    public Task<FriendOrder> Request(StoreAccess a,Guid id,string source,Guid peer,FriendRecipient recipient,StoreStockItem[] items,CancellationToken ct) => api.Send<FriendOrder>(a,HttpMethod.Post,"v1/transfers/orders",new {id,sourceId=source,guestPeer=peer,recipient,items},ct);
    public Task<FriendOrder> Claim(StoreAccess a,Guid id,CancellationToken ct) => api.Send<FriendOrder>(a,HttpMethod.Post,$"v1/transfers/orders/{id:D}/claim",new {},ct);
    public Task<FriendOrder> Presence(StoreAccess a,Guid id,CancellationToken ct) => api.Send<FriendOrder>(a,HttpMethod.Post,$"v1/transfers/orders/{id:D}/presence",new {},ct);
    public Task<JsonElement> Cancel(StoreAccess a,Guid id,CancellationToken ct) => api.Send<JsonElement>(a,HttpMethod.Post,$"v1/transfers/orders/{id:D}/cancel",new {},ct);
    public Task<JsonElement> Finish(StoreAccess a,Guid id,string receipt,CancellationToken ct) => api.Send<JsonElement>(a,HttpMethod.Post,$"v1/transfers/orders/{id:D}/finish",new {receipt},ct);
    public Task<FriendExchange> Exchange(StoreAccess a,Guid id,long after,PeerMessage[] messages,CancellationToken ct) => api.Send<FriendExchange>(a,HttpMethod.Post,$"v1/transfers/orders/{id:D}/exchange",new {after,messages},ct);
    public void Dispose()=>api.Dispose();
}
