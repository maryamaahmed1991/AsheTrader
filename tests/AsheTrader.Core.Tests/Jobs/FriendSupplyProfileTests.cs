using AsheTrader.Workflow;

namespace AsheTrader.Core.Tests.Jobs;

public class FriendSupplyProfileTests
{
    private static FriendSupplyProfile Profile() => new()
    {
        Providers = [11, 22], Items = [new() { ItemId = 10155 }, new() { ItemId = 5110, Providers = [22] }],
        Meeting = new() { TerritoryId = 129, AetheryteId = 8, WorldId = 54, X = 1, Y = 2, Z = 3 }
    };

    [Fact]
    public void IndependentSelectionCreatesSnapshotWithoutJobOrRecipient()
    {
        var profile = Profile();
        var template = profile.ToTemplate([11, 22]);
        Assert.Equal(new ulong[] { 11, 22 }, template.Definition.Sources);
        Assert.Equal(new ulong[] { 22 }, template.AdditionalItems[0].Sources);
        Assert.All(template.ItemDefinitions, d => Assert.Empty(d.Recipients));
        profile.Providers.Clear(); profile.Items[1].Providers!.Clear(); profile.Meeting.X = 99;
        Assert.Equal(2, template.Definition.Sources.Count);
        Assert.Single(template.AdditionalItems[0].Sources);
        Assert.Equal(1, template.Meeting.X);
        Assert.Equal(2, template.ItemDefinitions.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void LegacyMigrationPreservesDifferentItemProvidersQualityAndMeeting()
    {
        var legacy = new SavedTransferJob
        {
            Definition = new() { Name = "Supplies", ItemId = 10155, Sources = [11, 22], Recipients = [new() { CharacterId = 99 }] },
            AdditionalItems = [new() { ItemId = 5110, HighQuality = true, Sources = [22] }],
            Meeting = new() { TerritoryId = 129, AetheryteId = 8, WorldId = 54, X = 4 }
        };
        var draft = FriendSupplyProfile.FromLegacy(legacy);
        var template = draft.ToTemplate([11, 22]);
        Assert.Null(draft.Items[0].Providers);
        Assert.Equal(new ulong[] { 22 }, draft.Items[1].Providers);
        Assert.Equal("Supplies", template.Definition.Name);
        Assert.True(template.AdditionalItems[0].HighQuality);
        draft.Meeting.X = 8; draft.Providers.Clear();
        Assert.Equal(4, legacy.Meeting.X);
        Assert.Equal(2, legacy.Definition.Sources.Count);
    }

    [Fact]
    public void MissingProviderCannotSilentlyBroadenOrShrinkPublishedSelection()
    {
        var profile = Profile();
        Assert.Throws<InvalidOperationException>(() => profile.ToTemplate([11]));
        profile.Providers.Remove(22);
        Assert.Throws<InvalidOperationException>(() => profile.ToTemplate([11, 22]));
        profile.Items[1].Providers = [];
        Assert.Throws<InvalidOperationException>(() => profile.ToTemplate([11, 22]));
        profile.Items[1].Providers = null;
        Assert.Equal(new ulong[] { 11 }, profile.ToTemplate([11, 22]).AdditionalItems[0].Sources);
    }

    [Fact]
    public void DuplicateQualityRejectedButNormalAndHqCanBeOfferedSeparately()
    {
        var profile = Profile();
        profile.Items.Add(new() { ItemId = 10155 });
        Assert.Throws<InvalidOperationException>(() => profile.ToTemplate([11, 22]));
        profile.Items[^1].HighQuality = true;
        Assert.Equal(3, profile.ToTemplate([11, 22]).ItemDefinitions.Count());
    }

    [Fact]
    public void EmptySelectionOrMissingMeetingCannotBePublished()
    {
        Assert.Throws<InvalidOperationException>(() => new FriendSupplyProfile().ToTemplate([11, 22]));
        var profile = Profile();profile.Meeting.AetheryteId = 0;
        Assert.Throws<InvalidOperationException>(() => profile.ToTemplate([11, 22]));
        profile = Profile();profile.Items.Clear();
        Assert.Throws<InvalidOperationException>(() => profile.ToTemplate([11, 22]));
        profile = Profile();profile.Items = Enumerable.Range(2, 51).Select(i => new FriendSupplyItem { ItemId = (uint)i }).ToList();
        Assert.Throws<InvalidOperationException>(() => profile.ToTemplate([11, 22]));
    }
}
