using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

/// <summary>A standalone stock selection. No saved job or recipient is needed to publish it.</summary>
public sealed class FriendSupplyProfile
{
    public string Name { get; set; } = "My supplies";
    public List<ulong> Providers { get; set; } = [];
    public List<FriendSupplyItem> Items { get; set; } = [];
    public TravelLocation Meeting { get; set; } = new();

    public static FriendSupplyProfile FromLegacy(SavedTransferJob? template)
    {
        if (template == null) return new();
        var definitions = template.ItemDefinitions.ToArray();
        var providers = definitions.SelectMany(d => d.Sources).Distinct().ToList();
        return new()
        {
            Name = template.Definition.Name,
            Providers = providers,
            Meeting = CopyMeeting(template.Meeting),
            Items = definitions.Select(d => new FriendSupplyItem
            {
                ItemId = d.ItemId, HighQuality = d.HighQuality,
                Providers = d.Sources.ToHashSet().SetEquals(providers) ? null : d.Sources.Distinct().ToList()
            }).ToList()
        };
    }

    public SavedTransferJob ToTemplate(IEnumerable<ulong> localCharacters)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 80)
            throw new InvalidOperationException("Give the supply profile a name of up to 80 characters.");
        var local = localCharacters.ToHashSet();
        var providers = Providers.Distinct().ToArray();
        if (providers.Length == 0 || providers.Any(id => id == 0 || !local.Contains(id)))
            throw new InvalidOperationException("Select available provider characters on this PC. Refresh AutoRetainer if a selected character is missing.");
        if (Items.Count is < 1 or > 50 || Items.Any(i => i.ItemId <= 1)
            || Items.Select(i => (i.ItemId, i.HighQuality)).Distinct().Count() != Items.Count)
            throw new InvalidOperationException("Select 1–50 unique item and quality combinations.");
        if (Meeting.TerritoryId == 0 || Meeting.AetheryteId == 0)
            throw new InvalidOperationException("Record an outdoor meeting point with a main aetheryte.");
        var definitions = Items.Select(item =>
        {
            var selected = item.Providers?.Distinct().ToArray() ?? providers;
            if (selected.Length == 0 || selected.Any(id => !providers.Contains(id)))
                throw new InvalidOperationException("Each item's providers must include at least one of the selected characters.");
            return new TransferJobDefinition
            {
                Name = Name.Trim(), ItemId = item.ItemId, HighQuality = item.HighQuality,
                Sources = selected.ToList(), QuantityMode = JobQuantityMode.MoveAll,
                Recipients = []
            };
        }).ToArray();
        return new() { Definition = definitions[0], AdditionalItems = definitions.Skip(1).ToList(), Meeting = CopyMeeting(Meeting) };
    }

    private static TravelLocation CopyMeeting(TravelLocation location) => new()
    {
        WorldId = location.WorldId, TerritoryId = location.TerritoryId, AetheryteId = location.AetheryteId,
        X = location.X, Y = location.Y, Z = location.Z
    };
}

public sealed class FriendSupplyItem
{
    public uint ItemId { get; set; }
    public bool HighQuality { get; set; }
    // Null uses every provider in the profile; an explicit list narrows this item only.
    public List<ulong>? Providers { get; set; }
}
