using AsheTrader.Integrations;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class AllaganItemSearchParserTests
{
    private const ulong Owner = 33777097243848890;
    private const uint Fuel = 10155;
    private static readonly ItemSearchOwnerLabel Label = new("Retainer", "Character", "World");

    [Fact]
    public void MultipleStacksAggregateWithinStorageAndKeepQualitySeparate()
    {
        var rows = new[] { Slot(Fuel, 99, slot: 0), Slot(Fuel, 20, slot: 1), Slot(Fuel, 3, flags: 1, slot: 2), Slot(Fuel, 1, flags: 2, slot: 3) };
        var result = AllaganItemSearchParser.Read(Owner, rows, new HashSet<uint> { Fuel }, Label, out var incomplete);
        Assert.False(incomplete);
        Assert.Equal(3, result.Count);
        Assert.Equal(119UL, result.Single(row => row.ItemFlags == 0).Quantity);
        Assert.Equal(3UL, result.Single(row => row.ItemFlags == 1).Quantity);
        Assert.Equal(1UL, result.Single(row => row.ItemFlags == 2).Quantity);
        Assert.All(result, row => Assert.Equal(Label.CharacterName, row.CharacterName));
    }

    [Fact]
    public void AllPersonalStorageIsSearchableIncludingMarketSaddlebagsAndGlamour()
    {
        uint[] containers = [0, 1000, 2000, 2001, 2500, 2501, 3200, 4000, 4101, 10000, 11000, 12000, 12001, 12002];
        var result = AllaganItemSearchParser.Read(Owner, containers.Select(container => Slot(Fuel, 1, container: container)),
            new HashSet<uint> { Fuel }, Label, out var incomplete);
        Assert.False(incomplete);
        Assert.Equal(containers.Length, result.Count);
        Assert.Contains(result, row => row.Storage == "Retainer market listings");
        Assert.Contains(result, row => row.Storage == "Glamour dresser");
        Assert.DoesNotContain(result, row => row.Storage.StartsWith("Storage ", StringComparison.Ordinal));
    }

    [Fact]
    public void UnrequestedItemsAndEmptyPlaceholdersDoNotBecomeMatchesOrWarnings()
    {
        var empty = Slot(0, 0); empty[23] = 0;
        var result = AllaganItemSearchParser.Read(Owner, [empty, Slot(Fuel + 1, 2, slot: 1)],
            new HashSet<uint> { Fuel }, Label, out var incomplete);
        Assert.Empty(result);
        Assert.False(incomplete);
    }

    [Fact]
    public void DuplicatePhysicalSlotDoesNotDoubleCount()
    {
        var first = Slot(Fuel, 90);
        var result = AllaganItemSearchParser.Read(Owner, [first, first.ToArray()], new HashSet<uint> { Fuel }, Label, out var incomplete);
        Assert.False(incomplete);
        Assert.Equal(90UL, Assert.Single(result).Quantity);
    }

    [Fact]
    public void ConflictingPhysicalSlotsAreOmittedWhileOtherResultsRemain()
    {
        var result = AllaganItemSearchParser.Read(Owner,
            [Slot(Fuel, 90), Slot(Fuel, 91), Slot(Fuel, 92), Slot(Fuel, 20, slot: 1)],
            new HashSet<uint> { Fuel }, Label, out var incomplete);
        Assert.True(incomplete);
        Assert.Equal(20UL, Assert.Single(result).Quantity);
    }

    [Fact]
    public void MalformedForeignAndInconsistentSlotsProducePartialResults()
    {
        var foreign = Slot(Fuel, 9, slot: 1); foreign[23] = Owner + 1;
        var invalidFlags = Slot(Fuel, 10, slot: 2); invalidFlags[6] = ulong.MaxValue;
        var result = AllaganItemSearchParser.Read(Owner,
            [Slot(Fuel, 15), foreign, invalidFlags, Slot(0, 1, slot: 3), new ulong[2]],
            new HashSet<uint> { Fuel }, Label, out var incomplete);
        Assert.True(incomplete);
        Assert.Equal(15UL, Assert.Single(result).Quantity);
    }

    [Fact]
    public void AggregationUsesWideQuantityAndSortedLocations()
    {
        var result = AllaganItemSearchParser.Read(Owner,
            [Slot(Fuel, uint.MaxValue), Slot(Fuel, 1, slot: 1)],
            new HashSet<uint> { Fuel }, Label, out var incomplete);
        Assert.False(incomplete);
        Assert.Equal((ulong)uint.MaxValue + 1, Assert.Single(result).Quantity);
    }

    [Fact]
    public void UnknownContainerIsVisibleWithNumericLabel()
    {
        var result = AllaganItemSearchParser.Read(Owner, [Slot(Fuel, 4, container: 9876)],
            new HashSet<uint> { Fuel }, Label, out var incomplete);
        Assert.False(incomplete);
        Assert.Equal("Storage 9876", Assert.Single(result).Storage);
    }

    private static ulong[] Slot(uint itemId, uint quantity, uint flags = 0, uint container = 10000, uint slot = 0)
    {
        var row = new ulong[25];
        row[2] = itemId; row[3] = quantity; row[6] = flags;
        row[20] = container; row[22] = slot; row[23] = Owner;
        return row;
    }
}
