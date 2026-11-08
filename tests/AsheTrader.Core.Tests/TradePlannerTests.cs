using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class TradePlannerTests
{
    [Fact]
    public void FiveSlotLimitDoesNotImplyInventoryShortage()
    {
        var list = new[] { Line(100, 650) };
        var inventory = Enumerable.Range(0, 7).Select(slot => Stack(slot, 100, 99)).ToArray();

        var result = TradePlanner.Plan(list, inventory);

        Assert.Equal(5, result.Entries.Count);
        Assert.Equal(495UL, result.TotalQuantity);
        Assert.False(result.HasShortage);
        Assert.Equal(650U, list[0].Quantity);
        Assert.All(inventory, stack => Assert.Equal(99U, stack.Quantity));
    }

    [Fact]
    public void PartialStacksAreAllocatedOnlyToRequestedQuantity()
    {
        var result = TradePlanner.Plan([Line(100, 125)], [Stack(0, 100, 30), Stack(1, 100, 80), Stack(2, 100, 99)]);

        Assert.Equal(new uint[] { 30, 80, 15 }, result.Entries.Select(entry => entry.Quantity));
        Assert.Equal(new[] { 0, 1, 2 }, result.Entries.Select(entry => entry.Slot));
        Assert.False(result.HasShortage);
    }

    [Fact]
    public void HqIsExactAndNontradableInventoryIsExcluded()
    {
        var result = TradePlanner.Plan([Line(100, 50, true)],
        [
            Stack(0, 100, 99),
            Stack(1, 100, 90, true, false),
            Stack(2, 100, 20, true),
        ]);

        Assert.Equal(20U, Assert.Single(result.Entries).Quantity);
        var shortage = Assert.Single(result.Shortages);
        Assert.Equal(20UL, shortage.Available);
        Assert.Equal(30UL, shortage.Missing);
        Assert.Contains("missing 30", shortage.ToString());
    }

    [Fact]
    public void ExhaustedInventoryHasHelpfulShortageWithoutEmptyAllocations()
    {
        var result = TradePlanner.Plan([Line(100, 40)], [Stack(0, 200, 99)]);

        Assert.False(result.HasItems);
        Assert.Equal(40UL, Assert.Single(result.Shortages).Missing);
    }

    [Fact]
    public void ShortagesIncludeItemsBeyondCurrentBatch()
    {
        var result = TradePlanner.Plan([Line(100, 495), Line(200, 5)],
            Enumerable.Range(0, 5).Select(slot => Stack(slot, 100, 99)));

        Assert.Equal(5, result.Entries.Count);
        Assert.Equal(new ItemKey(200, false), Assert.Single(result.Shortages).Item);
    }

    [Fact]
    public void RejectsDuplicateRequestsInsteadOfOverdrawingOneStack()
    {
        Assert.Throws<ArgumentException>(() => TradePlanner.Plan([Line(100, 2), Line(100, 3)], [Stack(0, 100, 4)]));
    }

    [Fact]
    public void NqAndHqAreSeparateRequests()
    {
        var result = TradePlanner.Plan([Line(100, 2), Line(100, 3, true)], [Stack(0, 100, 4), Stack(1, 100, 6, true)]);
        Assert.Equal(new uint[] { 2, 3 }, result.Entries.Select(entry => entry.Quantity));
    }

    [Theory]
    [InlineData(0U, 5U)]
    [InlineData(100U, 0U)]
    public void RejectsZeroItemOrQuantity(uint itemId, uint quantity)
    {
        Assert.Throws<ArgumentException>(() => TradePlanner.Plan([Line(itemId, quantity)], []));
    }

    [Fact]
    public void RejectsDuplicatePhysicalSlots()
    {
        Assert.Throws<ArgumentException>(() => TradePlanner.Plan([Line(100, 100)], [Stack(0, 100, 60), Stack(0, 100, 60)]));
    }

    [Fact]
    public void AggregateInventoryCountsDoNotOverflowUint()
    {
        InventoryStack[] inventory =
        [
            new(0, 0, 100, false, uint.MaxValue, uint.MaxValue, true),
            new(0, 1, 100, false, uint.MaxValue, uint.MaxValue, true),
        ];

        var result = TradePlanner.Plan([Line(100, uint.MaxValue)], inventory);

        Assert.False(result.HasShortage);
        Assert.Equal(2UL * uint.MaxValue, TradePlanner.Count(inventory)[new ItemKey(100, false)]);
    }

    internal static TradeLine Line(uint itemId, uint quantity, bool hq = false) => new() { ItemId = itemId, Quantity = quantity, HighQuality = hq };
    internal static InventoryStack Stack(int slot, uint itemId, uint quantity, bool hq = false, bool tradable = true) =>
        new(0, slot, itemId, hq, quantity, 99, tradable);
}
