using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using Newtonsoft.Json;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class RecipientAssignmentsTests
{
    private const uint Coke = 1_001;
    private const uint Fuel = 10_155;
    private const uint Sap = 1_003;

    private static SavedTransferJob Empty() => new()
    {
        Definition = new() { Name = "Workshop supplies", Sources = [90, 91] },
        Meeting = new() { WorldId = 1, TerritoryId = 2, AetheryteId = 3 }
    };

    [Fact]
    public void EachRecipientGetsOnlyTheirChosenItemsWithoutExpandingOtherLists()
    {
        var job = Empty();
        RecipientAssignments.Assign(job, 11, Coke, false, job.Definition.Sources, 50);
        RecipientAssignments.Assign(job, 11, Fuel, false, job.Definition.Sources, 100);
        RecipientAssignments.Assign(job, 12, Fuel, false, job.Definition.Sources, 200);
        RecipientAssignments.Assign(job, 13, Sap, false, job.Definition.Sources, 300);

        Assert.Equal(new uint[] { Coke, Fuel }, RecipientAssignments.ForCharacter(job, 11).Select(pair => pair.Item.ItemId));
        Assert.Equal(new uint[] { Fuel }, RecipientAssignments.ForCharacter(job, 12).Select(pair => pair.Item.ItemId));
        Assert.Equal(new uint[] { Sap }, RecipientAssignments.ForCharacter(job, 13).Select(pair => pair.Item.ItemId));
        Assert.Equal(4, job.ItemDefinitions.Sum(item => item.Recipients.Count));
        Assert.Equal(new ulong?[] { 100, 200 }, job.ItemDefinitions.Single(item => item.ItemId == Fuel).Recipients.Select(rule => rule.Quantity));
        Assert.Equal(new ulong[] { 11, 12, 13 }, RecipientAssignments.Characters(job));
        foreach (var item in job.ItemDefinitions) JobPlanner.ValidateDefinition(item);
    }

    [Fact]
    public void ReusingAnItemPreservesIdentityRuleProviderConfigurationAndExistingAmounts()
    {
        var job = Empty();
        var fuel = RecipientAssignments.Assign(job, 11, Fuel, false, [90, 91], 75);
        fuel.QuantityMode = JobQuantityMode.RestockToTotal;
        var id = fuel.Id;
        var originalRecipient = fuel.Recipients[0];
        var relayPeer = Guid.NewGuid();
        fuel.RelayCharacters[relayPeer] = 92;

        var reused = RecipientAssignments.Assign(job, 12, Fuel, false, [99], 120);
        var duplicate = RecipientAssignments.Assign(job, 11, Fuel, false, [98], 999);

        Assert.Same(fuel, reused);
        Assert.Same(fuel, duplicate);
        Assert.Equal(id, fuel.Id);
        Assert.Equal(JobQuantityMode.RestockToTotal, fuel.QuantityMode);
        Assert.Equal(new ulong[] { 90, 91 }, fuel.Sources);
        Assert.Equal(92UL, fuel.RelayCharacters[relayPeer]);
        Assert.Same(originalRecipient, fuel.Recipients[0]);
        Assert.Equal(new ulong?[] { 75, 120 }, fuel.Recipients.Select(rule => rule.Quantity));
        Assert.Single(job.ItemDefinitions);
    }

    [Fact]
    public void AddingBeforeRemainderKeepsItsPriorityAndNeverMakesAnExtraRemainder()
    {
        var job = Empty();
        var fuel = RecipientAssignments.Assign(job, 11, Fuel, false, [90]);
        fuel.QuantityMode = JobQuantityMode.MoveAll;
        fuel.Recipients[0].Quantity = null;

        RecipientAssignments.Assign(job, 12, Fuel, false, [91], 50);
        RecipientAssignments.Assign(job, 13, Fuel, false, [91], 60);

        Assert.Equal(new ulong[] { 12, 13, 11 }, fuel.Recipients.Select(rule => rule.CharacterId));
        Assert.Equal(new ulong?[] { 50, 60, null }, fuel.Recipients.Select(rule => rule.Quantity));
        JobPlanner.ValidateDefinition(fuel);
    }

    [Fact]
    public void QualityVariantsStayDistinctAndNewProviderListsDoNotAlias()
    {
        var job = Empty();
        var sources = new List<ulong> { 90, 91 };
        var normal = RecipientAssignments.Assign(job, 11, Coke, false, sources, 50);
        var highQuality = RecipientAssignments.Assign(job, 12, Coke, true, normal.Sources, 100);
        sources.Add(92);
        highQuality.Sources.Remove(90);

        Assert.NotEqual(normal.Id, highQuality.Id);
        Assert.Equal(new ulong[] { 90, 91 }, normal.Sources);
        Assert.Equal(new ulong[] { 91 }, highQuality.Sources);
        Assert.False(normal.HighQuality);
        Assert.True(highQuality.HighQuality);
        Assert.Equal(11UL, Assert.Single(normal.Recipients).CharacterId);
        Assert.Equal(12UL, Assert.Single(highQuality.Recipients).CharacterId);
    }

    [Fact]
    public void RemovingOneItemFromOneRecipientPreservesOtherAssignmentsAndConfiguration()
    {
        var job = Empty();
        var fuel = RecipientAssignments.Assign(job, 11, Fuel, false, [90], 50);
        var coke = RecipientAssignments.Assign(job, 11, Coke, false, [91], 80);
        RecipientAssignments.Assign(job, 12, Fuel, false, [92], 100);
        var originalId = job.Definition.Id;

        Assert.True(RecipientAssignments.Remove(job, fuel.Id, 11));
        Assert.False(RecipientAssignments.Remove(job, fuel.Id, 11));

        Assert.Equal(originalId, job.Definition.Id);
        Assert.Same(coke, Assert.Single(RecipientAssignments.ForCharacter(job, 11)).Item);
        Assert.Equal(12UL, Assert.Single(fuel.Recipients).CharacterId);
        Assert.Equal(100UL, fuel.Recipients[0].Quantity);
        Assert.Equal(new ulong[] { 90 }, fuel.Sources);
        Assert.Equal(new ulong[] { 91 }, coke.Sources);
        Assert.Equal(new uint[] { Fuel, Coke }, job.ItemDefinitions.Select(item => item.ItemId));
    }

    [Fact]
    public void RemovingEmptyFirstItemPromotesExistingDefinitionWithoutRelabellingItsIdentity()
    {
        var job = Empty();
        var oldFirst = RecipientAssignments.Assign(job, 11, Fuel, false, [90]);
        var promoted = RecipientAssignments.Assign(job, 12, Coke, true, [91], 80);
        promoted.QuantityMode = JobQuantityMode.RestockToTotal;
        var last = RecipientAssignments.Assign(job, 13, Sap, false, [92], 30);
        var promotedId = promoted.Id;
        var lastId = last.Id;
        var meeting = job.Meeting;

        Assert.True(RecipientAssignments.Remove(job, oldFirst.Id, 11));

        Assert.Same(promoted, job.Definition);
        Assert.Equal(promotedId, job.Definition.Id);
        Assert.NotEqual(oldFirst.Id, job.Definition.Id);
        Assert.Equal(lastId, Assert.Single(job.AdditionalItems).Id);
        Assert.Equal(JobQuantityMode.RestockToTotal, job.Definition.QuantityMode);
        Assert.True(job.Definition.HighQuality);
        Assert.Equal(80UL, Assert.Single(job.Definition.Recipients).Quantity);
        Assert.Equal(new ulong[] { 91 }, job.Definition.Sources);
        Assert.Equal("Workshop supplies", job.Definition.Name);
        Assert.Same(meeting, job.Meeting);
    }

    [Fact]
    public void RemovingLastAssignmentLeavesReusablePlaceholderWithOriginalIdentityAndProviders()
    {
        var job = Empty();
        var id = job.Definition.Id;
        var fuel = RecipientAssignments.Assign(job, 11, Fuel, false, [90, 91]);
        Assert.Equal(id, fuel.Id);

        Assert.True(RecipientAssignments.Remove(job, fuel.Id, 11));
        Assert.Equal(0U, job.Definition.ItemId);
        Assert.Empty(job.Definition.Recipients);
        Assert.Equal(id, job.Definition.Id);
        Assert.Equal(new ulong[] { 90, 91 }, job.Definition.Sources);

        var replacement = RecipientAssignments.Assign(job, 12, Sap, false, job.Definition.Sources);
        Assert.Equal(id, replacement.Id);
        Assert.Single(job.ItemDefinitions);
        Assert.Equal(10UL, Assert.Single(replacement.Recipients).Quantity);
        Assert.Equal(JobQuantityMode.DeliverAmount, replacement.QuantityMode);
    }

    [Fact]
    public void PriorityChangesAffectOnlyThatItemAndCannotMoveRemainderAheadOfAnotherRecipient()
    {
        var job = Empty();
        var fuel = RecipientAssignments.Assign(job, 11, Fuel, false, [90], 50);
        RecipientAssignments.Assign(job, 12, Fuel, false, [90], 60);
        RecipientAssignments.Assign(job, 13, Fuel, false, [90], 70);
        var coke = RecipientAssignments.Assign(job, 11, Coke, false, [90]);
        RecipientAssignments.Assign(job, 12, Coke, false, [90]);
        fuel.QuantityMode = JobQuantityMode.MoveAll;
        fuel.Recipients[2].Quantity = null;

        Assert.True(RecipientAssignments.MovePriority(fuel, 12, -1));
        Assert.False(RecipientAssignments.MovePriority(fuel, 11, 1));
        Assert.False(RecipientAssignments.MovePriority(fuel, 13, -1));
        Assert.False(RecipientAssignments.MovePriority(fuel, 12, -1));
        Assert.Equal(new ulong[] { 12, 11, 13 }, fuel.Recipients.Select(rule => rule.CharacterId));
        Assert.Equal(new ulong[] { 11, 12 }, coke.Recipients.Select(rule => rule.CharacterId));
        JobPlanner.ValidateDefinition(fuel);
    }

    [Fact]
    public void ItemPriorityReorderingPreservesCanonicalIdentitiesAndEachItemsRecipientOrder()
    {
        var job = Empty();
        var fuel = RecipientAssignments.Assign(job, 11, Fuel, false, [90], 50);
        RecipientAssignments.Assign(job, 12, Fuel, false, [90], 60);
        var coke = RecipientAssignments.Assign(job, 12, Coke, true, [91], 70);
        RecipientAssignments.Assign(job, 11, Coke, true, [91], 80);
        coke.QuantityMode = JobQuantityMode.RestockToTotal;
        var sap = RecipientAssignments.Assign(job, 13, Sap, false, [92], 90);
        var identities = new[] { fuel.Id, coke.Id, sap.Id };

        Assert.True(RecipientAssignments.MoveItem(job, coke.Id, -1));
        Assert.Same(coke, job.Definition);
        Assert.Equal(new[] { identities[1], identities[0], identities[2] }, job.ItemDefinitions.Select(item => item.Id));
        Assert.Equal("Workshop supplies", job.Definition.Name);
        Assert.Equal(new ulong[] { 12, 11 }, coke.Recipients.Select(rule => rule.CharacterId));
        Assert.Equal(new ulong?[] { 70, 80 }, coke.Recipients.Select(rule => rule.Quantity));
        Assert.Equal(JobQuantityMode.RestockToTotal, coke.QuantityMode);
        Assert.Equal(new ulong[] { 91 }, coke.Sources);
        Assert.True(coke.HighQuality);
        Assert.Equal(new ulong[] { 11, 12 }, fuel.Recipients.Select(rule => rule.CharacterId));
        Assert.False(RecipientAssignments.MoveItem(job, coke.Id, -1));
        Assert.False(RecipientAssignments.MoveItem(job, sap.Id, 1));
        Assert.False(RecipientAssignments.MoveItem(job, Guid.NewGuid(), 1));
        Assert.True(RecipientAssignments.MoveItem(job, coke.Id, 1));
        Assert.Same(fuel, job.Definition);
        Assert.Equal(identities, job.ItemDefinitions.Select(item => item.Id));
    }

    [Fact]
    public void SharedModeChangeRetainsPositiveAmountsAndConvertsRemainderToAnExplicitAmount()
    {
        var job = Empty();
        var item = RecipientAssignments.Assign(job, 11, Fuel, false, [90], 80);
        RecipientAssignments.Assign(job, 12, Fuel, false, [90]);
        item.QuantityMode = JobQuantityMode.MoveAll;
        item.Recipients[1].Quantity = null;

        Assert.True(RecipientAssignments.SetMode(item, JobQuantityMode.RestockToTotal));
        Assert.False(RecipientAssignments.SetMode(item, JobQuantityMode.RestockToTotal));
        Assert.Equal(new ulong?[] { 80, 10 }, item.Recipients.Select(rule => rule.Quantity));
        JobPlanner.ValidateDefinition(item);
    }

    [Fact]
    public void ExistingSavedShapeRoundTripsWithAllAssignmentsAndIdentitiesIntact()
    {
        var job = Empty();
        RecipientAssignments.Assign(job, 11, Fuel, false, [90], 50);
        RecipientAssignments.Assign(job, 12, Fuel, false, [90], 60);
        RecipientAssignments.Assign(job, 12, Coke, false, [91], 70);
        var json = JsonConvert.SerializeObject(job);
        var restored = JsonConvert.DeserializeObject<SavedTransferJob>(json)!;

        Assert.DoesNotContain("ItemDefinitions", json);
        Assert.Equal(job.ItemDefinitions.Select(item => item.Id), restored.ItemDefinitions.Select(item => item.Id));
        Assert.Equal(new uint[] { Fuel, Coke }, RecipientAssignments.ForCharacter(restored, 12).Select(pair => pair.Item.ItemId));
        Assert.Equal(new ulong?[] { 60, 70 }, RecipientAssignments.ForCharacter(restored, 12).Select(pair => pair.Rule.Quantity));
        Assert.Equal(new ulong[] { 91 }, restored.AdditionalItems[0].Sources);
    }

    [Fact]
    public void InvalidAssignmentAndDuplicateDefinitionsAreRejectedWithoutPartialEdits()
    {
        var job = Empty();
        var fuel = RecipientAssignments.Assign(job, 11, Fuel, false, [90], 50);
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipientAssignments.Assign(job, 0, Coke, false, [90]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipientAssignments.Assign(job, 12, 0, false, [90]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipientAssignments.Assign(job, 12, Coke, false, [90], 0));
        Assert.Single(job.ItemDefinitions);
        Assert.Single(fuel.Recipients);

        job.AdditionalItems.Add(new() { ItemId = Fuel, Sources = [91], Recipients = [new() { CharacterId = 13, Quantity = 100 }] });
        Assert.Throws<InvalidOperationException>(() => RecipientAssignments.Assign(job, 12, Fuel, false, [90]));
        Assert.DoesNotContain(job.ItemDefinitions.SelectMany(item => item.Recipients), rule => rule.CharacterId == 12);
    }
}
