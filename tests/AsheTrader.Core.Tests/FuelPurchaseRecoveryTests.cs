using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelPurchaseRecoveryTests
{
    private static FuelPurchaseCheckpoint Intent() => new()
    { CharacterId = 123, FreeCompanyId = 456, Quantity = 99, BagBefore = 200, CreditsBefore = 50_000, UnitPrice = 100 };

    [Fact]
    public void RequiresBothExactItemGainAndExactCreditSpend()
    {
        Assert.Equal(FuelPurchaseOutcome.Completed, FuelPurchaseRecovery.Classify(Intent(), 123, 456, 299, 40_100));
        Assert.Equal(FuelPurchaseOutcome.Unchanged, FuelPurchaseRecovery.Classify(Intent(), 123, 456, 200, 50_000));
    }

    [Theory]
    [InlineData(299U, 50_000U)]
    [InlineData(200U, 40_100U)]
    [InlineData(298U, 40_100U)]
    [InlineData(300U, 40_100U)]
    [InlineData(299U, 40_000U)]
    [InlineData(199U, 50_000U)]
    public void PartialOrExternalChangesCannotBeReplayedAsNoPurchase(uint bags, uint credits)
        => Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(Intent(), 123, 456, bags, credits));

    [Theory]
    [InlineData(124UL, 456UL)]
    [InlineData(123UL, 457UL)]
    [InlineData(0UL, 456UL)]
    [InlineData(123UL, 0UL)]
    public void WrongCharacterOrCompanyCannotResolveIntent(ulong character, ulong fc)
        => Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(Intent(), character, fc, 299, 40_100));

    [Fact]
    public void RejectsInvalidIntentAndOverflowEvenWhenCountsAppearUnchanged()
    {
        var pending = Intent(); pending.Quantity = 100;
        Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(pending, 123, 456, 200, 50_000));
        pending = Intent(); pending.BagBefore = uint.MaxValue;
        Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(pending, 123, 456, uint.MaxValue, 50_000));
        pending = Intent(); pending.CreditsBefore = 99;
        Assert.Equal(FuelPurchaseOutcome.Inconsistent, FuelPurchaseRecovery.Classify(pending, 123, 456, 200, 99));
    }
}
