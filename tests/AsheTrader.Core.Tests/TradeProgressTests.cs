using AsheTrader.Core;
using static AsheTrader.Core.Tests.TradePlannerTests;

namespace AsheTrader.Core.Tests;

public sealed class TradeProgressTests
{
    [Fact]
    public void RepeatedBatchesCompleteOnlyAfterVerifiedDeltas()
    {
        var progress = new TradeProgressTracker([Line(100, 600)]);
        var firstInventory = Enumerable.Range(0, 7).Select(slot => Stack(slot, 100, 99)).ToArray();
        var firstBatch = TradePlanner.Plan(progress.Remaining, firstInventory);
        var secondInventory = new[] { Stack(5, 100, 99), Stack(6, 100, 99) };

        Assert.True(progress.TryCommit(firstBatch, TradeOutcome.Completed, TradePlanner.Count(firstInventory), TradePlanner.Count(secondInventory), out var error), error);
        Assert.Equal(105U, Assert.Single(progress.Remaining).Quantity);

        var secondBatch = TradePlanner.Plan(progress.Remaining, secondInventory);
        Assert.Equal(new uint[] { 99, 6 }, secondBatch.Entries.Select(entry => entry.Quantity));
        Assert.True(progress.TryCommit(secondBatch, TradeOutcome.Completed, TradePlanner.Count(secondInventory), TradePlanner.Count([Stack(6, 100, 93)]), out error), error);
        Assert.True(progress.IsComplete);
        Assert.Equal(2, progress.CompletedBatches);
    }

    [Theory]
    [InlineData(TradeOutcome.Cancelled)]
    [InlineData(TradeOutcome.Unknown)]
    public void CancellationOrUnconfirmedCompletionNeverConsumesProgress(TradeOutcome outcome)
    {
        var progress = new TradeProgressTracker([Line(100, 40)]);
        var before = new[] { Stack(0, 100, 50) };
        var batch = TradePlanner.Plan(progress.Remaining, before);

        Assert.False(progress.TryCommit(batch, outcome, TradePlanner.Count(before), TradePlanner.Count([Stack(0, 100, 10)]), out _));
        Assert.Equal(40U, Assert.Single(progress.Remaining).Quantity);
        Assert.Equal(0, progress.CompletedBatches);
    }

    [Theory]
    [InlineData(50U)]
    [InlineData(20U)]
    [InlineData(0U)]
    [InlineData(60U)]
    public void NoProgressOrWrongDecreaseIsRejected(uint afterQuantity)
    {
        var progress = new TradeProgressTracker([Line(100, 40)]);
        var before = new[] { Stack(0, 100, 50) };
        var batch = TradePlanner.Plan(progress.Remaining, before);

        Assert.False(progress.TryCommit(batch, TradeOutcome.Completed, TradePlanner.Count(before), TradePlanner.Count([Stack(0, 100, afterQuantity)]), out var error));
        Assert.Contains("does not exactly match", error);
        Assert.Equal(40U, Assert.Single(progress.Remaining).Quantity);
    }

    [Fact]
    public void UnexpectedOtherInventoryActivityIsAmbiguousAndRejectsWholeBatch()
    {
        var progress = new TradeProgressTracker([Line(100, 40)]);
        var before = new[] { Stack(0, 100, 50), Stack(1, 200, 10) };
        var batch = TradePlanner.Plan(progress.Remaining, before);
        var after = new[] { Stack(0, 100, 10), Stack(1, 200, 11) };

        Assert.False(progress.TryCommit(batch, TradeOutcome.Completed, TradePlanner.Count(before), TradePlanner.Count(after), out _));
        Assert.Equal(40U, Assert.Single(progress.Remaining).Quantity);
    }

    [Fact]
    public void CommitIsAtomicWhenOnlyOneOfSeveralItemsMatches()
    {
        var progress = new TradeProgressTracker([Line(100, 40), Line(200, 20)]);
        var before = new[] { Stack(0, 100, 50), Stack(1, 200, 30) };
        var batch = TradePlanner.Plan(progress.Remaining, before);
        var after = new[] { Stack(0, 100, 10), Stack(1, 200, 20) };

        Assert.False(progress.TryCommit(batch, TradeOutcome.Completed, TradePlanner.Count(before), TradePlanner.Count(after), out _));
        Assert.Equal(new uint[] { 40, 20 }, progress.Remaining.Select(line => line.Quantity));
    }

    [Fact]
    public void SameCompletionCannotBeReplayed()
    {
        var progress = new TradeProgressTracker([Line(100, 600)]);
        var before = Enumerable.Range(0, 7).Select(slot => Stack(slot, 100, 99)).ToArray();
        var batch = TradePlanner.Plan(progress.Remaining, before);
        var after = new[] { Stack(5, 100, 99), Stack(6, 100, 99) };

        Assert.True(progress.TryCommit(batch, TradeOutcome.Completed, TradePlanner.Count(before), TradePlanner.Count(after), out _));
        Assert.False(progress.TryCommit(batch, TradeOutcome.Completed, TradePlanner.Count(before), TradePlanner.Count(after), out var error));
        Assert.Contains("already been committed", error);
        Assert.Equal(105U, Assert.Single(progress.Remaining).Quantity);
    }

    [Fact]
    public void MutableInputAndReturnedLinesCannotAlterTracker()
    {
        var line = Line(100, 40);
        var progress = new TradeProgressTracker([line]);
        line.Quantity = 1;
        progress.Remaining[0].Quantity = 2;

        Assert.Equal(40U, Assert.Single(progress.Remaining).Quantity);
    }
}
