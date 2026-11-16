using AsheTrader.Core.Store;
using Item = Lumina.Excel.Sheets.Item;

namespace AsheTrader.Store;

internal sealed class StoreService : IDisposable
{
    private readonly Plugin plugin;
    private readonly StoreApi api = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly StoreCredentialFile secrets;
    private StoreCredentials credentials = new();
    private bool credentialsHealthy = true;
    private Task<Action>? pending;
    private StoreCacheReader? reader;
    private StoreSourceState? source;
    private StoreAccess? browsing;
    private bool publishAfterRead;
    private long nextAuto;
    private string? previewSelection;
    public string Status {get;private set;} = "Connect a store to begin. Nothing is shared until you publish.";
    public string Error {get;private set;} = "";
    public bool Busy => pending!=null || reader!=null;
    public StoreAccess? Owner => credentials.Owner;
    public StoreAccess? Publisher => credentials.Publisher;
    public IReadOnlyList<StoreAccess> Readers => credentials.Readers;
    public StoreManagement? Management {get;private set;}
    public StoreCatalogue? Catalogue {get;private set;}
    public StoreSnapshot? Preview {get;private set;}
    public DateTimeOffset? PreviewAt {get;private set;}
    public string GeneratedCode {get;private set;} = "";
    public string GeneratedRole {get;private set;} = "";
    public string[] Warnings {get;private set;} = [];
    public bool PreviewCurrent => Preview!=null && previewSelection==Selection();

    public StoreService(Plugin plugin)
    {
        this.plugin=plugin;
        secrets=new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName,"store-credentials.json"));
        try { credentials=secrets.Load(); }
        catch { credentialsHealthy=false;Error="Stored store credentials could not be unlocked. They were not overwritten."; }
        // Explicit enablement is saved; wait before reading or sending after a reload.
        nextAuto=Environment.TickCount64+300_000;
    }
    public void Import(string code)
    {
        if(Busy) return;
        try
        {
            var access=StoreAccess.Parse(code);
            Start(async()=>
            {
                if(access.Role=="owner") {var view=await api.Manage(access,lifetime.Token);return ()=>{credentials.Owner=access;Management=view;Save();};}
                if(access.Role=="publisher") {var state=await api.Source(access,lifetime.Token);return ()=>{credentials.Publisher=access;source=state;Preview=null;plugin.Config.Store.AutoPublish=false;plugin.Save();Save();};}
                var catalogue=await api.Browse(access,lifetime.Token);
                return ()=>{credentials.Readers=credentials.Readers.Where(x=>x.Url!=access.Url).Append(access).ToList();browsing=access;Catalogue=catalogue;Save();};
            },"Connecting to the private store…");
        }
        catch {Error="Invalid store code. Copy the complete code and try again.";}
    }
    public void RefreshManagement()
    {
        if(Owner is not {} access) return;
        Start(async()=>{var view=await api.Manage(access,lifetime.Token);return ()=>Management=view;},"Refreshing store access…");
    }
    public void CreateKey(string role,string label,bool useThisPc)
    {
        if(Owner is not {} access) return;
        Start(async()=>
        {
            var result=await api.CreateKey(access,role,label,lifetime.Token);
            var created=new StoreAccess(1,access.Url,result.Token,result.Role);
            // Preserve newly issued code before any optional follow-up network call can fail.
            return ()=>{GeneratedCode=created.Encode();GeneratedRole=role;if(useThisPc){credentials.Publisher=created;source=null;Preview=null;Save();} RefreshManagement();};
        },"Creating private access…");
    }
    public void Revoke(string id)
    {
        if(Owner is not {} access) return;
        Start(async()=>{await api.Revoke(access,id,lifetime.Token);return ()=>RefreshManagement();},"Revoking access…");
    }
    public void DismissCode() {GeneratedCode="";GeneratedRole="";}
    public void Browse(StoreAccess? access=null)
    {
        access ??= browsing ?? Readers.FirstOrDefault() ?? Owner ?? Publisher;
        if(access==null) return;
        var selected=access;
        Start(async()=>{var result=await api.Browse(selected,lifetime.Token);return ()=>{browsing=selected;Catalogue=result;};},"Loading published stock…");
    }
    public void PreviewStock(bool automatic=false)
    {
        if(Busy || Publisher is not {} access) return;
        if(plugin.Config.Store.Items.Count==0 || plugin.Config.Store.Characters.Count==0) {Error="Select at least one item and character first.";return;}
        var selected=plugin.Config.Characters.Where(x=>plugin.Config.Store.Characters.Contains(x.ContentId)).ToArray();
        if(selected.Length!=plugin.Config.Store.Characters.Count) {Error="A selected character is missing from your roster. Update the selection.";return;}
        var items=Plugin.DataManager.GetExcelSheet<Item>().Where(x=>plugin.Config.Store.Items.Contains(x.RowId) && !x.Name.IsEmpty).ToDictionary(x=>x.RowId,x=>x.Name.ToString());
        if(items.Count!=plugin.Config.Store.Items.Count) {Error="A selected item is unavailable. Update your item selection.";return;}
        var selection=Selection();
        Start(async()=>
        {
            var state=await api.Source(access,lifetime.Token);
            return ()=>{source=state;Preview=null;Warnings=[];previewSelection=selection;publishAfterRead=automatic;reader=new(selected,items,state.Salt);};
        },"Preparing the local stock preview…");
    }
    public void Publish()
    {
        if(Publisher is not {} access || !PreviewCurrent || Preview is not {} snapshot || Warnings.Length!=0) return;
        Start(async()=>{var result=await api.Publish(access,snapshot,lifetime.Token);return ()=>{Preview=snapshot with {Revision=result.Revision};source=source! with {Revision=result.Revision,PublishedAt=result.PublishedAt};Status=$"Stock published at {result.PublishedAt.ToLocalTime():t}. Friends can view it while you are offline.";nextAuto=Environment.TickCount64+300_000;};},"Publishing selected stock…");
    }
    public void ClearPublished()
    {
        if(Publisher is not {} access) return;
        plugin.Config.Store.AutoPublish=false;plugin.Save();
        Start(async()=>{var current=await api.Source(access,lifetime.Token);await api.Clear(access,current.Revision,lifetime.Token);return ()=>{Preview=null;Status="This PC's published stock was removed. Other PCs are unchanged.";};},"Removing this PC's published stock…");
    }
    public void ChangedSelection()
    {
        Preview=null;previewSelection=null;plugin.Config.Store.AutoPublish=false;plugin.Save();
        Status="Selection changed. Preview and publish to replace this PC's previous stock.";
    }
    public void SetAuto(bool enabled)
    {plugin.Config.Store.AutoPublish=enabled;plugin.Save();nextAuto=Environment.TickCount64+300_000;}
    public void Update()
    {
        if(pending is {IsCompleted:true} task)
        {
            pending=null;
            try {var apply=task.GetAwaiter().GetResult();Status="Ready.";apply();}
            catch(InvalidOperationException ex){Error=ex.Message;}
            catch {Error="Could not reach the private catalogue. Previous stock is retained; try again shortly.";}
        }
        if(reader is {} cache)
        {
            cache.Step();Status=cache.Progress;
            if(cache.Complete)
            {
                reader=null;Warnings=cache.Warnings.ToArray();PreviewAt=DateTimeOffset.UtcNow;
                if(Warnings.Length==0 && previewSelection==Selection())
                {
                    Preview=new(source!.Revision,cache.Results.ToArray());Status="Preview ready. Only these item totals will be published.";
                    if(publishAfterRead) Publish();
                }
                else {Preview=null;Status="Preview incomplete. Previous published stock is retained; resolve the warnings before publishing.";}
            }
        }
        if(!Busy && plugin.Config.Store.AutoPublish && Environment.TickCount64>=nextAuto)
        {nextAuto=Environment.TickCount64+300_000;PreviewStock(true);}
    }
    private void Start(Func<Task<Action>> operation,string status)
    {if(Busy)return;Error="";Status=status;pending=operation();}
    private string Selection()=>string.Join(',',plugin.Config.Store.Characters.Order())+"/"+string.Join(',',plugin.Config.Store.Items.Order());
    private void Save()
    {
        if(!credentialsHealthy){Error="Credentials are usable for this session only; the saved credential file could not be unlocked.";return;}
        try {secrets.Save(credentials);Status="Store connected. Access is saved privately on this PC.";}
        catch {Error="Access works for this session but could not be saved securely. Keep your access code for the next session.";}
    }
    public void Dispose(){lifetime.Cancel();api.Dispose();lifetime.Dispose();GeneratedCode="";}
}
