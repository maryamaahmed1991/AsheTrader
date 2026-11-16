using AsheTrader.Core;
using AsheTrader.Core.Store;
using AsheTrader.Integrations;

namespace AsheTrader.Store;

/// <summary>One IPC per frame. Reads only explicitly selected characters and their retainers.</summary>
internal sealed class StoreCacheReader
{
    private readonly Queue<CharacterProfile> characters;
    private readonly Queue<(ulong Id, bool Retainer, string Label)> owners = new();
    private readonly HashSet<ulong> ownerIds = [];
    private readonly IReadOnlyDictionary<uint,string> items;
    private readonly string salt;
    private bool ready;
    public List<StoreOwnerStock> Results { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool Complete { get; private set; }
    public string Progress { get; private set; } = "Reading selected inventories…";

    public StoreCacheReader(IEnumerable<CharacterProfile> selected, IReadOnlyDictionary<uint,string> items, string salt)
    { characters = new(selected); this.items=items; this.salt=salt; }

    public void Step()
    {
        if(Complete) return;
        if(!Plugin.Framework.IsInFrameworkUpdateThread) throw new InvalidOperationException("Inventory cache must be read on the framework thread.");
        try
        {
            if(!ready)
            {
                if(!Plugin.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized").InvokeFunc()) throw new InvalidOperationException();
                ready=true; return;
            }
            if(characters.TryDequeue(out var character))
            {
                Progress=$"Reading retainer roster for {character.Name}…";
                var data=Plugin.PluginInterface.GetIpcSubscriber<ulong,CharacterData?>("AutoRetainer.GetOfflineCharacterData").InvokeFunc(character.ContentId);
                if(data?.CID!=character.ContentId || data.RetainerData==null) {Warnings.Add($"{character.Name}: retainer roster unavailable.");return;}
                Add(character.ContentId,false,character.Name);
                foreach(var retainer in data.RetainerData) Add(retainer.RetainerID,true,retainer.Name ?? "Retainer");
                return;
            }
            if(!owners.TryDequeue(out var owner)) {Complete=true;return;}
            Progress=$"Reading cached inventory: {owner.Label}…";
            try
            {
                var records=Plugin.PluginInterface.GetIpcSubscriber<ulong,HashSet<ulong[]>>("AllaganTools.GetCharacterItems").InvokeFunc(owner.Id) ?? throw new InvalidDataException();
                // Require complete personal bag pages. Missing cache must not erase published stock.
                var inventory=AllaganInventoryCache.Read(owner.Id,records,owner.Retainer,items.Keys.First(),false,uint.MaxValue);
                var ordinarySlots=records.Where(x=>x.Length>=25 && x[6]<=1).Select(x=>(Container:(uint)x[20],Slot:(uint)x[22])).ToHashSet();
                var entries=inventory.Items.Where(x=>x.ItemId!=0 && items.ContainsKey(x.ItemId) && x.Quantity>0 && ordinarySlots.Contains((x.Container,x.Slot)))
                    .GroupBy(x=>(x.ItemId,x.HighQuality))
                    .Select(g=>new StoreStockItem(g.Key.ItemId,items[g.Key.ItemId],g.Key.HighQuality,g.Aggregate(0UL,(sum,x)=>checked(sum+x.Quantity))))
                    .ToArray();
                Results.Add(new(StorePrivacy.OwnerKey(salt,owner.Id),entries));
            }
            catch { Warnings.Add($"{owner.Label}: complete cached inventory unavailable. Visit this inventory in-game, then refresh."); }
        }
        catch { Warnings.Add("Allagan Tools or AutoRetainer is unavailable. Previous published stock is retained."); Complete=true; }
    }
    private void Add(ulong id,bool retainer,string label) {if(id!=0 && ownerIds.Add(id))owners.Enqueue((id,retainer,label));}
    private sealed class CharacterData {public ulong CID {get;set;} public List<RetainerData>? RetainerData {get;set;}}
    private sealed class RetainerData {public ulong RetainerID {get;set;} public string? Name {get;set;}}
}
