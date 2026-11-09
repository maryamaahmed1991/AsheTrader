using AsheTrader.Core;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class RetainerTransferConfirmationTests
{
    private static readonly ItemKey Item = new(10155, false);
    private static Dictionary<ItemKey, ulong> Inventory(ulong quantity) => quantity == 0 ? [] : new() { [Item] = quantity };
    private static RetainerTransferConfirmation Withdrawal(long startedAt = 0) => new(startedAt, Inventory(10), Inventory(100), Item, 30, false);

    [Fact]
    public void FastAcknowledgementUsesOneDispatchIntervalInsteadOfStackedSleeps()
    {
        var pending = Withdrawal();
        Assert.False(pending.Observe(50, Inventory(40), Inventory(70)));
        Assert.False(pending.Observe(100, Inventory(40), Inventory(70)));
        Assert.False(pending.Observe(349, Inventory(40), Inventory(70)));
        Assert.True(pending.Observe(350, Inventory(40), Inventory(70)));
    }

    [Fact]
    public void DelayedDestinationAcknowledgementCannotReleaseAnotherCommand()
    {
        var pending = Withdrawal();
        Assert.False(pending.Observe(350, Inventory(10), Inventory(70)));
        Assert.False(pending.Observe(1500, Inventory(10), Inventory(70)));
        Assert.False(pending.Observe(1600, Inventory(40), Inventory(70)));
        Assert.False(pending.Observe(1600, Inventory(40), Inventory(70)));
        Assert.True(pending.Observe(1616, Inventory(40), Inventory(70)));
    }

    [Fact]
    public void ATransientMatchingReadMustBeConfirmedAgainAfterAnyMismatch()
    {
        var pending = Withdrawal();
        Assert.False(pending.Observe(350, Inventory(40), Inventory(70)));
        Assert.False(pending.Observe(366, Inventory(10), Inventory(70)));
        Assert.False(pending.Observe(382, Inventory(40), Inventory(70)));
        Assert.True(pending.Observe(398, Inventory(40), Inventory(70)));
    }

    [Fact]
    public void WrongQuantityOrDirectionNeverConfirms()
    {
        var pending = Withdrawal();
        foreach (var now in new long[] { 350, 700 })
        {
            Assert.False(pending.Observe(now, Inventory(39), Inventory(71)));
            Assert.False(pending.Observe(now, Inventory(41), Inventory(69)));
            Assert.False(pending.Observe(now, Inventory(0), Inventory(110)));
        }
    }

    [Fact]
    public void QualityAndUnrelatedItemsMustRemainUnchanged()
    {
        var pending = Withdrawal();
        var hqBags = new Dictionary<ItemKey, ulong> { [new(10155, true)] = 40 };
        var changedBags = Inventory(40);
        changedBags[new(42, false)] = 1;
        var changedRetainer = Inventory(70);
        changedRetainer[new(42, false)] = 1;
        foreach (var now in new long[] { 350, 700 })
        {
            Assert.False(pending.Observe(now, hqBags, Inventory(70)));
            Assert.False(pending.Observe(now, changedBags, Inventory(70)));
            Assert.False(pending.Observe(now, Inventory(40), changedRetainer));
        }
    }

    [Fact]
    public void TimeoutNeverTreatsAnUnacknowledgedCommandAsCompleted()
    {
        var pending = Withdrawal();
        Assert.False(pending.Observe(15_000, Inventory(10), Inventory(100)));
        var error = Assert.Throws<InvalidOperationException>(() => pending.Observe(15_001, Inventory(10), Inventory(100)));
        Assert.Contains("not retried", error.Message);
    }

    [Fact]
    public void CutsceneTimeIsExcludedAndRequiresFreshConfirmation()
    {
        var pending = Withdrawal(1000);
        Assert.False(pending.Observe(1400, Inventory(40), Inventory(70)));
        pending.Suspend(60_000);
        Assert.Equal(400, pending.ElapsedMilliseconds(61_400));
        Assert.False(pending.Observe(61_400, Inventory(40), Inventory(70)));
        Assert.True(pending.Observe(61_416, Inventory(40), Inventory(70)));
    }

    [Fact]
    public void SnapshotsAreCopiedBeforeExternalDictionariesCanChange()
    {
        var bags = Inventory(10);
        var retainer = Inventory(100);
        var pending = new RetainerTransferConfirmation(0, bags, retainer, Item, 30, false);
        bags[Item] = 40;
        retainer[Item] = 70;
        Assert.False(pending.Observe(350, bags, retainer));
        Assert.True(pending.Observe(366, bags, retainer));
    }

    [Fact]
    public void DepositConfirmsTheOppositeInventoryDeltas()
    {
        var pending = new RetainerTransferConfirmation(0, Inventory(40), Inventory(70), Item, 30, true);
        Assert.False(pending.Observe(350, Inventory(10), Inventory(100)));
        Assert.True(pending.Observe(366, Inventory(10), Inventory(100)));
    }

    [Fact]
    public void LargeTotalsDoNotOverflowOrAcceptWrappedDeltas()
    {
        var pending = new RetainerTransferConfirmation(0, Inventory(ulong.MaxValue - 30), Inventory(30), Item, 30, false);
        Assert.False(pending.Observe(350, Inventory(ulong.MaxValue), Inventory(0)));
        Assert.True(pending.Observe(366, Inventory(ulong.MaxValue), Inventory(0)));
        var impossible = new RetainerTransferConfirmation(0, Inventory(ulong.MaxValue), Inventory(30), Item, 30, false);
        Assert.False(impossible.Observe(350, Inventory(29), Inventory(0)));
        Assert.False(impossible.Observe(366, Inventory(29), Inventory(0)));
    }

    [Fact]
    public void InvalidMovementAndSuspensionInputsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetainerTransferConfirmation(0, Inventory(10), Inventory(100), Item, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => Withdrawal().Suspend(-1));
    }
}
