using AsheTrader.Core;
using Newtonsoft.Json;

namespace AsheTrader.Core.Tests;

public sealed class FuelResumeRegressionTests
{
    private static FuelPurchaseCheckpoint Pending() => JsonConvert.DeserializeObject<FuelPurchaseCheckpoint>("""
        {"CharacterId":123,"FreeCompanyId":456,"Quantity":99,"BagBefore":19305,
         "CreditsBefore":16029397,"UnitPrice":100}
        """)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPendingReceiptIsTalliedBeforeAnySupplierTravel(bool ending)
    {
        Assert.Equal(FuelQueueResumeAction.RecoverPurchase, FuelQueueResumeRouting.Decide(ending, true, false, false, false));
        // The player is in Limsa with no shop, credit display, price, or housing access.
        Assert.Equal(FuelPurchaseOutcome.Completed, FuelPurchaseRecovery.ClassifyInventoryReceipt(Pending(), 123, 456, 19404));
        Assert.Equal(ending ? FuelQueueResumeAction.Finish : FuelQueueResumeAction.SelectCharacter,
            FuelQueueResumeRouting.Decide(ending, false, false, false, false));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PendingPurchaseWinsOverOtherPersistedStages(bool deposit, bool refresh)
        => Assert.Equal(FuelQueueResumeAction.RecoverPurchase, FuelQueueResumeRouting.Decide(true, true, deposit, true, refresh));

    [Fact]
    public void EndingWithInterruptedDepositStillTalliesTheDeposit()
        => Assert.Equal(FuelQueueResumeAction.RecoverDeposit, FuelQueueResumeRouting.Decide(true, false, true, true, false));

    [Fact]
    public void EndingWithoutPendingMovementDoesNotTravelOrDeposit()
        => Assert.Equal(FuelQueueResumeAction.Finish, FuelQueueResumeRouting.Decide(true, false, false, true, true));

    [Theory]
    [InlineData(123UL, 456UL, 19305U, FuelPurchaseOutcome.Unchanged)]
    [InlineData(123UL, 456UL, 19350U, FuelPurchaseOutcome.Inconsistent)]
    [InlineData(123UL, 456UL, 19405U, FuelPurchaseOutcome.Inconsistent)]
    [InlineData(123UL, 456UL, 19000U, FuelPurchaseOutcome.Inconsistent)]
    [InlineData(124UL, 456UL, 19404U, FuelPurchaseOutcome.Inconsistent)]
    [InlineData(123UL, 457UL, 19404U, FuelPurchaseOutcome.Inconsistent)]
    [InlineData(123UL, 0UL, 19404U, FuelPurchaseOutcome.Inconsistent)]
    public void BagOnlyRecoveryNeverApprovesAnAmbiguousReceipt(ulong cid, ulong fc, uint bags, FuelPurchaseOutcome expected)
        => Assert.Equal(expected, FuelPurchaseRecovery.ClassifyInventoryReceipt(Pending(), cid, fc, bags));

    [Theory]
    [InlineData(0U, 19305U, 100U, 16029397U)]
    [InlineData(100U, 19305U, 100U, 16029397U)]
    [InlineData(99U, uint.MaxValue, 100U, 16029397U)]
    [InlineData(99U, 19305U, 0U, 16029397U)]
    [InlineData(99U, 19305U, 100U, 0U)]
    public void CorruptSavedPurchaseIsNeverTallied(uint quantity, uint before, uint price, uint credits)
    {
        var pending = Pending(); pending.Quantity = quantity; pending.BagBefore = before;
        pending.UnitPrice = price; pending.CreditsBefore = credits;
        Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.ClassifyInventoryReceipt(pending, 123, 456, 19404));
    }
}
