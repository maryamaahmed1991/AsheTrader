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
    public WorkflowSettings Workflow { get; set; } = new();

    public void Normalize()
    {
        TradeLists ??= [];
        Characters ??= [];
        Houses ??= [];
        Workflow ??= new();
        foreach (var list in TradeLists) list.Items ??= [];
        foreach (var character in Characters) character.Fuel ??= new FuelPolicy();
    }
}
