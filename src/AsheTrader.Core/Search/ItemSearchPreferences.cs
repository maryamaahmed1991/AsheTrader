namespace AsheTrader.Core.Search;

/// <summary>Local search shortcuts, never inventory snapshots or paired-PC configuration.</summary>
public sealed class ItemSearchPreferences
{
    public string LastQuery { get; set; } = "";
    public bool LargestFirst { get; set; }
    public List<SavedItemSearch> Saved { get; set; } = [];

    public void Normalize()
    {
        LastQuery = Limit(LastQuery, ItemSearchQuery.MaximumQueryLength);
        Saved ??= [];
        Saved = Saved.Where(x => x != null).ToList();
        var ids = new HashSet<Guid>();
        foreach (var search in Saved)
        {
            if (search.Id == Guid.Empty || !ids.Add(search.Id))
            {
                search.Id = Guid.NewGuid();
                ids.Add(search.Id);
            }
            search.Name = Limit(search.Name, 80).Trim();
            if (search.Name.Length == 0) search.Name = "Saved search";
            search.Query = Limit(search.Query, ItemSearchQuery.MaximumQueryLength);
        }
    }

    private static string Limit(string? value, int max) => (value ?? "")[..Math.Min(value?.Length ?? 0, max)];
}

public sealed class SavedItemSearch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
}
