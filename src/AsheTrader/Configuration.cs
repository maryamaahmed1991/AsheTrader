using AsheTrader.Core;
using Dalamud.Configuration;
using AsheTrader.Workflow;

namespace AsheTrader;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public List<TradeList> TradeLists { get; set; } = [];
    public List<CharacterProfile> Characters { get; set; } = [];
    public List<HouseProfile> Houses { get; set; } = [];
    public bool OpenOnLoad { get; set; }
    public bool ShowMascotIllustrations { get; set; } = true;
    public Core.Search.ItemSearchPreferences ItemSearch { get; set; } = new();
    public WorkflowSettings Workflow { get; set; } = new();
    public FuelQueueSettings FuelSettings { get; set; } = new();
    public Dictionary<ulong, FuelAccessSnapshot> FuelAccessSnapshots { get; set; } = [];
    public Core.Store.StorePreferences Store { get; set; } = new();
    public HashSet<ulong> UtilityCharacters { get; set; } = [];

    public void Normalize()
    {
        TradeLists ??= [];
        Characters ??= [];
        Houses ??= [];
        ItemSearch ??= new();
        ItemSearch.Normalize();
        Workflow ??= new();
        FuelSettings ??= new();
        FuelAccessSnapshots ??= [];
        Store ??= new();
        UtilityCharacters ??= [];
        Store.Characters ??= [];
        Store.Items ??= [];
        FuelSettings.Companies ??= [];
        foreach (var list in TradeLists) list.Items ??= [];
        foreach (var character in Characters) character.Fuel ??= new FuelPolicy();
    }
}
