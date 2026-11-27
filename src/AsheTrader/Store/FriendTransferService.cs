using AsheTrader.Core;
using AsheTrader.Core.Store;
using AsheTrader.Workflow;
using Newtonsoft.Json;
using Item= Lumina.Excel.Sheets.Item;

namespace AsheTrader.Store;

internal sealed class FriendSettings
{
    public FriendSupplyProfile? Supply {get;set;}
    public SavedTransferJob? Template {get;set;}
    public bool Enabled {get;set;}
    public FriendOrder? Active {get;set;}
    public string AccessUrl {get;set;}="";
    public bool Supplying {get;set;}
    public FriendRequestDraft? Draft {get;set;}
    public SavedTransferJob? PendingTemplate {get;set;}
    public StoreStockItem[] PendingItems {get;set;}=[];
    public long PendingRevision {get;set;}
}
internal sealed record FriendRequestDraft(Guid Id,string Source,Guid Peer,FriendRecipient Recipient,StoreStockItem[] Items);

internal sealed class FriendTransferService : IDisposable
{
    private readonly Plugin plugin;
    private readonly FriendTransferApi api=new();
    private readonly CancellationTokenSource lifetime=new();
    private readonly SettingsFileStore file;
    private FriendSettings settings=new();
    private bool healthy=true,armed;
    private int stopGeneration;
    private Task<Action>? pending;
    private long nextPoll;
    private StoreAccess? browsing;
    private StoreCacheReader? cache;
    private SavedTransferJob? publishing;
    private int publishIndex;
    private long publishRevision;
    private readonly List<StoreStockItem> publishItems=[];
    public FriendOffer[] Offers {get;private set;}=[];
    public FriendOrder[] Orders {get;private set;}=[];
    public FriendGrant[] Grants {get;private set;}=[];
    public string Status {get;private set;}="Publish a delivery profile and grant a friend request access to get started.";
    public string Error {get;private set;}="";
    public bool Busy=>pending!=null||publishing!=null;
    public bool Enabled=>settings.Enabled;
    public FriendOrder? Active=>settings.Active;
    public bool Supplying=>settings.Supplying;
    public bool AcceptingCommands=>armed&&settings.Active is {State:"active"};
    public string ProfileName=>settings.Template?.Definition.Name??"No delivery profile";
    public FriendSupplyProfile Supply => settings.Supply ??= FriendSupplyProfile.FromLegacy(settings.Template);
    public bool NeedsReconnect=>(settings.Active!=null||settings.Draft!=null)&&!armed;
    public bool HasPendingRequest=>settings.Draft!=null;
    public StoreAccess? SelectedAccess=>browsing;
    public FriendTransferService(Plugin plugin)
    {
        this.plugin=plugin;file=new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName,"friend-transfers.json"));
        try {settings=JsonConvert.DeserializeObject<FriendSettings>(file.Load()??"null")??new();}
        catch {healthy=false;Error="Friend delivery settings could not be read; the file was preserved.";}
        if(settings.Active!=null||settings.Draft!=null)Status="Saved friend request retained. Reconnect it to continue; no game work starts from reload.";
    }
    private void Save()
    {if(!healthy||!file.TrySave(JsonConvert.SerializeObject(settings),out _)){healthy=false;settings.Enabled=false;throw new IOException("Could not save friend request progress.");}}
    private void Start(Func<Task<Action>> operation)
    {if(pending!=null)throw new InvalidOperationException("Wait for the current friend request operation.");Error="";pending=operation();}
    private StoreAccess Access()=>settings.Supplying?plugin.Store.Publisher is {} p&&p.Url==settings.AccessUrl?p:throw new InvalidOperationException("Reconnect this delivery's publisher credential.")
        :plugin.Store.Readers.SingleOrDefault(r=>r.Url==settings.AccessUrl)??throw new InvalidOperationException("Reconnect this delivery's reader invitation.");
    public void Refresh(StoreAccess? access=null)
    {
        if(Busy)return;
        browsing=access??browsing??plugin.Store.Readers.FirstOrDefault();var selected=browsing;
        Start(async()=>
        {
            var offers=selected==null?Array.Empty<FriendOffer>():await api.Offers(selected,lifetime.Token);
            var orders=selected!=null?await api.Orders(selected,lifetime.Token):plugin.Store.Publisher is {} p?await api.Orders(p,lifetime.Token):[];
            var grants=plugin.Store.Owner is {} owner?await api.Grants(owner,lifetime.Token):[];
            return ()=>{Offers=offers;Orders=orders;Grants=grants;Status="Requestable stock refreshed. Quantities exclude existing reservations.";};
        });
    }
    public void RefreshIncoming()
    {
        var publisher=plugin.Store.Publisher??throw new InvalidOperationException("This PC has no publisher access.");
        Start(async()=>{var orders=await api.Orders(publisher,lifetime.Token);return ()=>Orders=orders;});
    }
    public void SetGrant(string reader,string source,bool enabled)
    {var owner=plugin.Store.Owner??throw new InvalidOperationException("Owner access is required.");Start(async()=>{await api.Grant(owner,reader,source,enabled,lifetime.Token);return ()=>Refresh();});}
    public void SetEnabled(bool enabled)
    {
        if(enabled&&settings.PendingTemplate!=null)
        {
            var access=plugin.Store.Publisher??throw new InvalidOperationException("Publisher unavailable.");
            var generation=stopGeneration;
            Start(async()=>{var offer=await api.Offer(access,lifetime.Token);return ()=>
            {
                var items=System.Text.Json.JsonSerializer.Deserialize<StoreStockItem[]>(offer!.Items,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if(offer.PeerId!=plugin.Config.Workflow.PeerId||offer.Revision!=settings.PendingRevision+1||!settings.PendingItems.SequenceEqual(items??[]))
                    throw new InvalidOperationException("Republish the delivery profile before enabling it.");
                settings.Template=settings.PendingTemplate;settings.PendingTemplate=null;settings.PendingItems=[];settings.Enabled=generation==stopGeneration;Save();nextPoll=0;
            };});return;
        }
        if(enabled&&settings.Template==null)throw new InvalidOperationException("Publish a delivery profile first.");settings.Enabled=enabled;Save();nextPoll=0;Status=enabled?"This PC will accept permitted requests when idle.":"Automatic friend deliveries paused. Queued stock remains reserved.";
    }
    public void SaveSupplyDraft()
    {
        Save();
        Status="Supply selection saved locally. Publish it when ready to update requestable stock.";
    }
    public void CaptureSupplyMeeting()
    {
        if(Busy||settings.Active!=null)throw new InvalidOperationException("Finish the active request before changing its meeting point.");
        if(!Workflow.Native.GameWorkflowExecutor.TryCaptureCurrentLocation(out var location,out var reason)||location==null)
            throw new InvalidOperationException(reason);
        if(location.AetheryteId==0)throw new InvalidOperationException("Record an outdoor meeting territory with a main aetheryte.");
        Supply.Meeting=location;SaveSupplyDraft();
    }
    public void PublishSupply()
    {
        Save();
        PublishProfile(Supply.ToTemplate(plugin.Config.Characters.Select(c=>c.ContentId)));
    }
    private void PublishProfile(SavedTransferJob template)
    {
        if(Busy||settings.Active!=null)throw new InvalidOperationException("Finish the active request first.");
        var publisher=plugin.Store.Publisher??throw new InvalidOperationException("Authorize this PC as a store publisher first.");
        var copy=JsonConvert.DeserializeObject<SavedTransferJob>(JsonConvert.SerializeObject(template))!;
        var local=plugin.Config.Characters.Select(c=>c.ContentId).ToHashSet();
        foreach(var d in copy.ItemDefinitions){d.Sources=d.Sources.Where(local.Contains).Distinct().ToList();if(d.Sources.Count==0)throw new InvalidOperationException("Every profile item must have a provider on this PC.");}
        if(copy.ItemDefinitions.Count()>50||copy.Meeting.AetheryteId==0||copy.Meeting.TerritoryId==0)throw new InvalidOperationException("Choose a meeting point and at most 50 items.");
        settings.Enabled=false;Save();
        Start(async()=>{var existing=await api.Offer(publisher,lifetime.Token);return ()=>{publishing=copy;publishRevision=existing?.Revision??0;publishIndex=0;publishItems.Clear();BeginCache();};});
    }
    private void BeginCache()
    {
        var d=publishing!.ItemDefinitions.ElementAt(publishIndex);
        var item=Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(d.ItemId);
        if(item==null||item.Value.IsUntradable)throw new InvalidOperationException("Delivery profiles require tradable items.");
        cache=new(plugin.Config.Characters.Where(c=>d.Sources.Contains(c.ContentId)),new Dictionary<uint,string>{{d.ItemId,item.Value.Name.ToString()}},new string('0',64));
    }
    public void Request(FriendOffer offer,ulong recipientId,StoreStockItem[] items)
    {
        if(settings.Active!=null||settings.Draft!=null)throw new InvalidOperationException("Finish or cancel your existing request first.");
        if(!plugin.Workflow.CanStartFriendDelivery)throw new InvalidOperationException("Finish the current job on this PC before requesting delivery.");
        var access=browsing??throw new InvalidOperationException("Select a friend's store first.");
        var recipient=plugin.Config.Characters.Single(c=>c.ContentId==recipientId);
        if(items.Length==0||items.Any(i=>i.Quantity==0||i.Quantity>(offer.Items.SingleOrDefault(s=>s.ItemId==i.ItemId&&s.HighQuality==i.HighQuality)?.Quantity??0)))throw new InvalidOperationException("Choose quantities within available stock.");
        settings.AccessUrl=access.Url;settings.Supplying=false;
        settings.Draft=new(Guid.NewGuid(),offer.SourceId,plugin.Config.Workflow.PeerId,new(recipientId.ToString(System.Globalization.CultureInfo.InvariantCulture),recipient.Name,recipient.World),items);
        Save();armed=true;SubmitDraft();
    }
    private void SubmitDraft()
    {
        var draft=settings.Draft!;var access=Access();
        Start(async()=>{var order=await api.Request(access,draft.Id,draft.Source,draft.Peer,draft.Recipient,draft.Items,lifetime.Token);return ()=>{settings.Active=order;settings.Draft=null;Save();Status="Stock reserved. Waiting for the supplier and this PC to be ready.";nextPoll=0;};});
    }
    public void Reconnect()
    {armed=true;Error="";nextPoll=0;if(settings.Active is {} active)plugin.Workflow.ReconnectFriendDelivery(Access(),active);if(settings.Draft!=null&&pending==null)SubmitDraft();}
    public void Cancel(FriendOrder order)
    {
        var access=settings.Active?.Id==order.Id?Access():order.GuestPeer==plugin.Config.Workflow.PeerId?browsing??Access():plugin.Store.Publisher??throw new InvalidOperationException("Publisher unavailable.");
        Start(async()=>{await api.Cancel(access,order.Id,lifetime.Token);return ()=>{if(settings.Active?.Id==order.Id){settings.Active=null;Save();}Refresh();};});
    }
    public void ClearUnsubmittedRequest()
    {
        if(settings.Draft is not {} draft)return;
        var access=Access();
        Start(async()=>{var orders=await api.Orders(access,lifetime.Token);return ()=>
        {
            settings.Active=orders.FirstOrDefault(o=>o.Id==draft.Id);settings.Draft=null;armed=false;Save();
            Status=settings.Active==null?"Unsubmitted request cleared. Refresh stock before trying again.":"The request was already accepted. Reconnect it or cancel it while queued.";
        };});
    }
    public void Pause()
    {
        stopGeneration++;armed=false;settings.Enabled=false;
        try {Save();}catch {Error="Friend requests stopped, but their paused state could not be saved. Check diagnostics before reloading.";}
        Status="Friend requests paused. Active stock remains reserved until delivery is reconciled.";
    }
    public void Update()
    {
        if(pending is {IsCompleted:true} task)
        {
            pending=null;
            try {task.GetAwaiter().GetResult()();}
            catch(Exception ex) {Error=ex is InvalidOperationException or IOException?ex.Message:"Friend request service unavailable. Refresh or reconnect to retry; existing stock reservations are retained.";nextPoll=Environment.TickCount64+10000;publishing=null;cache=null;if(settings.Draft!=null)armed=false;}
        }
        if(cache is {} reader)
        {
            reader.Step();Status=reader.Progress;
            if(reader.Complete)
            {
                cache=null;
                if(reader.Warnings.Count>0){publishing=null;Error="Could not publish: "+string.Join(" ",reader.Warnings.Take(3));return;}
                var d=publishing!.ItemDefinitions.ElementAt(publishIndex);
                var stock=reader.Results.SelectMany(r=>r.Items).Where(i=>i.HighQuality==d.HighQuality).ToArray();
                var total=stock.Aggregate(0UL,(sum,i)=>checked(sum+i.Quantity));
                if(total>0)publishItems.Add(new(d.ItemId,stock[0].Name,d.HighQuality,Math.Min(total,1_000_000_000UL)));
                if(++publishIndex<publishing.ItemDefinitions.Count()){BeginCache();return;}
                var snapshot=publishing;publishing=null;var items=publishItems.ToArray();var access=plugin.Store.Publisher!;
                // Persist the profile before publication; do not accept requests until its publication is acknowledged.
                settings.PendingTemplate=snapshot;settings.PendingItems=items;settings.PendingRevision=publishRevision;settings.Enabled=false;Save();
                Start(async()=>{await api.Publish(access,publishRevision,plugin.Config.Workflow.PeerId,snapshot.Definition.Name,items,lifetime.Token);return ()=>{settings.Template=snapshot;settings.PendingTemplate=null;settings.PendingItems=[];Save();Status="Requestable stock published. Enable automatic deliveries on this PC when ready.";Refresh();};});
            }
        }
        if(pending!=null||publishing!=null||!healthy)return;
        if(settings.Active is {} active)
        {
            if(!armed)return;
            if(active.State=="active"&&!plugin.Workflow.FriendDeliveryConnected)
            {
                if(!plugin.Workflow.CanStartFriendDelivery&&plugin.Workflow.Run?.ScheduleOccurrenceId!=active.Id)return;
                plugin.Workflow.ConnectFriendDelivery(Access(),active,settings.Supplying?settings.Template:null);
            }
            if(settings.Supplying&&active.State=="active")
            {
                plugin.Workflow.StartFriendDelivery();
                if(plugin.Workflow.FriendDeliveryFinished)
                {
                    var receipt=TradeHistoryProjection.Create(plugin.Workflow.Run!);var access=Access();
                    // The complete movement log stays on the supplier. Hosted receipts retain item totals and outcome.
                    receipt.Movements=[];receipt.Notes=receipt.Notes.TakeLast(10).ToList();
                    Start(async()=>{await api.Finish(access,active.Id,JsonConvert.SerializeObject(receipt),lifetime.Token);return ()=>{plugin.Workflow.ReleaseFriendDelivery();settings.Active=null;Save();Status="Friend delivery recorded in Past Trades. Refresh the profile to offer remaining stock.";};});
                }
                return;
            }
            if(Environment.TickCount64<nextPoll)return;nextPoll=Environment.TickCount64+5000;
            if(active.State=="queued"&&!plugin.Workflow.CanStartFriendDelivery)return;
            var guestAccess=Access();
            Start(async()=>{var latest=await api.Presence(guestAccess,active.Id,lifetime.Token);return ()=>
            {
                settings.Active=latest;Save();
                if(latest.State=="completed"&&plugin.Workflow.FriendWorkerIdle)
                {
                    if(latest.Receipt is {} json)
                    {
                        var receipt=JsonConvert.DeserializeObject<TradeReceipt>(json,new JsonSerializerSettings{TypeNameHandling=TypeNameHandling.None,MaxDepth=32})
                            ??throw new InvalidDataException("Missing delivery receipt.");
                        if(receipt.Id==Guid.Empty||receipt.Items==null||receipt.Items.Count>50||receipt.Items.Any(i=>!active.Items.Any(a=>a.ItemId==i.ItemId&&a.HighQuality==i.HighQuality)))throw new InvalidDataException("Invalid delivery receipt.");
                        plugin.Workflow.TradeHistory.Record(receipt);
                        if(!plugin.Workflow.TradeHistory.Records.Any(r=>r.Id==receipt.Id))throw new IOException("Could not save the delivery receipt.");
                    }
                    plugin.Workflow.ReleaseFriendDelivery();settings.Active=null;Save();Status="Delivery finished. See Past Trades for delivered quantities and any shortfall.";
                }
                else if(latest.State=="cancelled"){settings.Active=null;Save();Status="Request cancelled; no delivery was started.";}
            };});
            return;
        }
        if(settings.Draft!=null)return;
        if(settings.Enabled&&settings.Template!=null&&plugin.Store.Publisher is {} publisher&&Environment.TickCount64>=nextPoll)
        {
            nextPoll=Environment.TickCount64+10000;
            var canClaim=plugin.Workflow.CanStartFriendDelivery;
            var generation=stopGeneration;var peerId=plugin.Config.Workflow.PeerId;
            Start(async()=>
            {
                var orders=await api.Orders(publisher,lifetime.Token);
                var interrupted=orders.FirstOrDefault(o=>o.HostPeer==peerId&&o.State=="active");
                if(interrupted!=null)return ()=>{settings.Active=interrupted;settings.AccessUrl=publisher.Url;settings.Supplying=true;Save();armed=false;Status="An interrupted delivery was recovered. Reconnect it before continuing.";};
                if(!canClaim)return ()=>Orders=orders;
                var candidate=orders.Where(o=>o.HostPeer==peerId&&o.State=="queued"&&o.GuestSeen>DateTimeOffset.UtcNow.AddSeconds(-40)).OrderBy(o=>o.CreatedAt).FirstOrDefault();
                if(candidate==null)return ()=>Orders=orders;
                var claimed=await api.Claim(publisher,candidate.Id,lifetime.Token);
                return ()=>{settings.Active=claimed;settings.AccessUrl=publisher.Url;settings.Supplying=true;Save();armed=settings.Enabled&&generation==stopGeneration;Orders=orders;Status=armed?"Friend request claimed; preparing both accounts.":"Request claimed while stopping. Reconnect it explicitly to begin.";};
            });
        }
    }
    public void Dispose(){lifetime.Cancel();api.Dispose();lifetime.Dispose();}
}
