using AsheTrader.Core;
using Newtonsoft.Json;

namespace AsheTrader.Core.Tests;

public sealed class FuelPurchaseRecoveryTests
{
    private static FuelPurchaseCheckpoint Intent() => new()
    { CharacterId = 123, FreeCompanyId = 456, Quantity = 99, BagBefore = 200, CreditsBefore = 50_000, UnitPrice = 100 };

    [Fact]
    public void ExactItemReceiptCompletesWhileUntouchedEvidenceRemainsUnchanged()
    {
        Assert.Equal(FuelPurchaseOutcome.Completed, FuelPurchaseRecovery.Classify(Intent(), 123, 456, 299, 40_100, 100));
        Assert.Equal(FuelPurchaseOutcome.Unchanged, FuelPurchaseRecovery.Classify(Intent(), 123, 456, 200, 50_000, 100));
    }

    [Theory]
    [InlineData(40_127U)] // Other FC activity earned 27 credits while 99 tanks arrived.
    [InlineData(50_000U)] // A credit update can lag the inventory notification.
    [InlineData(60_000U)] // FC income can exceed this purchase's entire cost.
    [InlineData(30_000U)] // Another FC member can also spend credits concurrently.
    [InlineData(0U)]
    [InlineData(uint.MaxValue)]
    public void SharedCreditChangesCannotUndoAnExactItemReceipt(uint credits)
        => Assert.Equal(FuelPurchaseOutcome.Completed, FuelPurchaseRecovery.Classify(Intent(), 123, 456, 299, credits, 100));

    [Theory]
    [InlineData(16_029_397U)]
    [InlineData(16_019_524U)]
    [InlineData(16_079_059U)]
    public void ReportedLegacyCheckpointRecoversTheNinetyNineTanksWithoutRepeatingThem(uint credits)
    {
        // Existing files have no new version or acknowledgement fields to migrate.
        var pending = JsonConvert.DeserializeObject<FuelPurchaseCheckpoint>("""
            {"CharacterId":123,"FreeCompanyId":456,"Quantity":99,"BagBefore":19305,
             "CreditsBefore":16029397,"UnitPrice":100}
            """)!;
        Assert.Equal(FuelPurchaseOutcome.Completed, FuelPurchaseRecovery.Classify(pending, 123, 456, 19_404, credits, 100));
        Assert.True(FuelPurchaseRecovery.CanAcknowledge(pending, 123, 456, 19_404, 19_404, credits, 100, false));
        Assert.False(FuelPurchaseRecovery.CanAcknowledge(pending, 123, 456, 19_404, 19_305, credits, 100, false));
    }

    [Theory]
    [InlineData(200U, 40_100U)]
    [InlineData(200U, 50_027U)]
    [InlineData(200U, 0U)]
    [InlineData(298U, 40_100U)]
    [InlineData(298U, 60_000U)]
    [InlineData(300U, 40_100U)]
    [InlineData(300U, 60_000U)]
    [InlineData(199U, 50_000U)]
    public void PartialOrExternalChangesCannotBeReplayedAsNoPurchase(uint bags, uint credits)
        => Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(Intent(), 123, 456, bags, credits, 100));

    [Theory]
    [InlineData(124UL, 456UL)]
    [InlineData(123UL, 457UL)]
    [InlineData(0UL, 456UL)]
    [InlineData(123UL, 0UL)]
    public void WrongCharacterOrCompanyCannotResolveIntent(ulong character, ulong fc)
        => Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(Intent(), character, fc, 299, 40_100, 100));

    [Theory]
    [InlineData(0U)]
    [InlineData(99U)]
    [InlineData(101U)]
    public void ChangedOrUnavailableUnitPriceDoesNotResolveThePendingPurchase(uint price)
        => Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(Intent(), 123, 456, 299, 40_127, price));

    [Fact]
    public void RejectsInvalidIntentAndOverflowEvenWhenCountsAppearUnchanged()
    {
        var pending = Intent(); pending.Quantity = 100;
        Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(pending, 123, 456, 200, 50_000, 100));
        pending = Intent(); pending.BagBefore = uint.MaxValue;
        Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(pending, 123, 456, uint.MaxValue, 50_000, 100));
        pending = Intent(); pending.CreditsBefore = 99;
        Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(pending, 123, 456, 200, 99, 100));
        pending = Intent(); pending.Quantity = 0;
        Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(pending, 123, 456, 200, 50_000, 100));
    }

    [Theory]
    [InlineData(299U, 200U, false, false)]
    [InlineData(299U, 299U, true, false)]
    [InlineData(298U, 298U, false, false)]
    [InlineData(299U, 299U, false, true)]
    public void LiveAcknowledgementWaitsForExactInventoryAndTheShopToSettle(uint bags, uint displayed, bool confirmation, bool acknowledged)
        => Assert.Equal(acknowledged, FuelPurchaseRecovery.CanAcknowledge(Intent(), 123, 456, bags, displayed, 40_127, 100, confirmation));

    [Fact]
    public void ANewPurchaseBaselineCannotCreditThePreviousReceiptAgain()
    {
        var first = Intent();
        Assert.Equal(FuelPurchaseOutcome.Completed, FuelPurchaseRecovery.Classify(first, 123, 456, 299, 40_127, 100));
        var next = new FuelPurchaseCheckpoint
        { CharacterId = 123, FreeCompanyId = 456, Quantity = 99, BagBefore = 299, CreditsBefore = 40_127, UnitPrice = 100 };
        Assert.Equal(FuelPurchaseOutcome.Unchanged, FuelPurchaseRecovery.Classify(next, 123, 456, 299, 40_127, 100));
        Assert.False(FuelPurchaseRecovery.CanAcknowledge(next, 123, 456, 299, 299, 40_127, 100, false));
        Assert.Equal(FuelPurchaseOutcome.Completed, FuelPurchaseRecovery.Classify(next, 123, 456, 398, 30_254, 100));
    }
}
