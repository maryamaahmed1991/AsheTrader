using System.Text.Json;
using AsheTrader.Core.Search;

namespace AsheTrader.Core.Tests.Search;

public sealed class ItemSearchPreferencesTests
{
    [Fact]
    public void SearchShortcutsAndLastQuerySurviveReload()
    {
        var settings = new ItemSearchPreferences
        {
            LastQuery = "coke, ceruleum", LargestFirst = true,
            Saved = [new() { Name = "Workshop supplies", Query = "coke, ceruleum, sap" }],
        };
        var restored = JsonSerializer.Deserialize<ItemSearchPreferences>(JsonSerializer.Serialize(settings))!;
        restored.Normalize();
        Assert.Equal(settings.LastQuery, restored.LastQuery);
        Assert.True(restored.LargestFirst);
        Assert.Equal(settings.Saved[0].Id, restored.Saved[0].Id);
        Assert.Equal("Workshop supplies", restored.Saved[0].Name);
        Assert.Equal(["coke", "ceruleum", "sap"], ItemSearchQuery.Parse(restored.Saved[0].Query));
    }

    [Fact]
    public void ExistingConfigurationWithoutSearchPreferencesUsesEmptyDefaults()
    {
        var restored = JsonSerializer.Deserialize<ItemSearchPreferences>("{}")!;
        restored.Normalize();
        Assert.Empty(restored.LastQuery);
        Assert.Empty(restored.Saved);
        Assert.False(restored.LargestFirst);
    }

    [Fact]
    public void DamagedSavedIdsCannotCollideInPicker()
    {
        var sameId = Guid.NewGuid();
        var restored = new ItemSearchPreferences { LastQuery = null!, Saved =
            [new() { Id = sameId }, new() { Id = sameId }, new() { Id = Guid.Empty, Query = null! }] };
        restored.Normalize();
        Assert.Empty(restored.LastQuery);
        Assert.Equal(3, restored.Saved.Select(x => x.Id).Distinct().Count());
        Assert.All(restored.Saved, x => { Assert.NotEqual(Guid.Empty, x.Id); Assert.NotEmpty(x.Name); Assert.NotNull(x.Query); });
    }
}
