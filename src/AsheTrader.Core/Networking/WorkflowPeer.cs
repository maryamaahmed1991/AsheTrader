using System.Net;
using AsheTrader.Core.Store;

namespace AsheTrader.Core.Networking;

/// <summary>A temporary, order-scoped HTTPS mailbox can replace LAN transport without changing saved pairing.</summary>
public sealed class WorkflowPeer : IDisposable
{
    private readonly LanPeer lan = new();
    private HostedSession? hosted;
    public bool Hosted => hosted != null;
    public bool IsEnabled => Hosted || lan.IsEnabled;
    public bool IsConnected => hosted?.Connected ?? lan.IsConnected;
    public string Status => hosted?.Status ?? lan.Status;
    public int BoundPort => lan.BoundPort;
    public Guid? RemotePeerId => SessionIdentity?.RemotePeerId;
    public (Guid ConnectionId, Guid RemotePeerId)? SessionIdentity => hosted is {} h ? h.Connected ? (h.Connection,h.Remote) : null : lan.SessionIdentity;
    public void StartHost(IPAddress address,int port,string key,Guid local)=>lan.StartHost(address,port,key,local);
    public Task ConnectAsync(IPAddress address,int port,string key,Guid local)=>lan.ConnectAsync(address,port,key,local);
    public void StartHosted(StoreAccess access,FriendOrder order,bool host)
    { Stop(); hosted=new(access,order.Id,host?order.GuestPeer:order.HostPeer); }
    public void Send(PeerMessage message,Guid? expected=null)
    {if(hosted is {} h)h.Send(message,expected);else lan.Send(message,expected);}
    public bool TryReceive(out PeerMessage message,Guid? expected=null)
    {if(hosted is {} h)return h.Receive(out message,expected);return lan.TryReceive(out message,expected);}
    public void Stop(){hosted?.Dispose();hosted=null;lan.Stop();}
    public void Dispose(){Stop();lan.Dispose();}

    private sealed class HostedSession : IDisposable
    {
        private readonly object gate=new();
        private readonly Queue<PeerMessage> incoming=new(),outgoing=new();
        private readonly CancellationTokenSource lifetime=new();
        private readonly FriendTransferApi api=new();
        private long lastSuccess,after;
        private bool closed;
        public Guid Connection {get;}=Guid.NewGuid();
        public Guid Remote {get;}
        public bool Connected {get {lock(gate)return !closed && lastSuccess!=0 && Environment.TickCount64-lastSuccess<15000;}}
        public string Status => Connected?"Connected for the friend delivery over HTTPS.":"Waiting for the friend delivery connection…";
        public HostedSession(StoreAccess access,Guid order,Guid remote){Remote=remote;_ = Pump(access,order);}
        public void Send(PeerMessage message,Guid? expected)
        {
            lock(gate)
            {
                if(closed || expected.HasValue&&expected!=Connection)throw new InvalidOperationException("Friend connection changed.");
                if(outgoing.Count>=128)throw new InvalidOperationException("Friend delivery connection is not keeping up.");
                outgoing.Enqueue(message);
            }
        }
        public bool Receive(out PeerMessage message,Guid? expected)
        {lock(gate){message=null!;return (!expected.HasValue||expected==Connection)&&incoming.TryDequeue(out message!);}}
        private async Task Pump(StoreAccess access,Guid order)
        {
            try
            {
                while(!lifetime.IsCancellationRequested)
                {
                    PeerMessage[] sending;long cursor;
                    lock(gate){sending=outgoing.Take(24).ToArray();cursor=after;}
                    try
                    {
                        var result=await api.Exchange(access,order,cursor,sending,lifetime.Token).ConfigureAwait(false);
                        if(result.Messages==null||result.Messages.Length>32)throw new InvalidDataException("Invalid delivery mailbox.");
                        lock(gate)
                        {
                            if(incoming.Count+result.Messages.Length>128)throw new InvalidDataException("Delivery receive queue is full.");
                            foreach(var e in result.Messages)
                            {
                                if(e.Sequence<=after || e.Message==null || e.Message.Payload.Length>100000)throw new InvalidDataException("Invalid delivery message.");
                                incoming.Enqueue(e.Message);after=e.Sequence;
                            }
                            foreach(var _ in sending)outgoing.Dequeue();
                            lastSuccess=Environment.TickCount64;
                        }
                    }
                    catch(OperationCanceledException) when(lifetime.IsCancellationRequested){break;}
                    catch { /* No secrets or payloads in transport diagnostics. Durable worker commands reconcile retries. */ }
                    await Task.Delay(500,lifetime.Token).ConfigureAwait(false);
                }
            }
            catch(OperationCanceledException){ }
            finally {api.Dispose();lifetime.Dispose();}
        }
        public void Dispose(){lock(gate){closed=true;lifetime.Cancel();}}
    }
}
