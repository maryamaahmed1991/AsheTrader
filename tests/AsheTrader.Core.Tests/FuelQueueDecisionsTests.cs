using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelQueueDecisionsTests
{
    [Fact]
    public void TargetBudgetSurvivesStorageTripsWithoutRestockingTheSameAmountTwice()
    {
        var entry = new FuelQueueEntry { BuyBelow = 100, TargetQuantity = 500 };
        var remaining = FuelQueueDecisions.InitialTarget(entry, 50);
        Assert.Equal(450U, remaining);
        var first = FuelQueueDecisions.PlanPurchase(entry, remaining, 250, 1_000_000, 100);
        Assert.Equal(250U, first.Quantity);
        remaining = FuelQueueDecisions.RemainingAfterPurchase(remaining, first.Quantity);
        Assert.True(FuelQueueDecisions.PlanPurchase(entry, remaining, 0, 975_000, 100).NeedsDeposit);
        Assert.Equal(FuelQueueDepositDecision.ReturnToSupplier, FuelQueueDecisions.AfterDeposit(300, 300, false));
        var afterDeposit = FuelQueueDecisions.PlanPurchase(entry, remaining, 300, 975_000, 100);
        Assert.Equal(200U, afterDeposit.Quantity);
        remaining = FuelQueueDecisions.RemainingAfterPurchase(remaining, afterDeposit.Quantity);
        var done = FuelQueueDecisions.PlanPurchase(entry, remaining, 100, 955_000, 100);
        Assert.True(done.FinishAfterDeposit); Assert.False(done.CreditExhausted);
    }

    [Fact]
    public void ExhaustCreditsContinuesAfterEachFullBagDepositUntilRemainingCreditsCannotBuyOneTank()
    {
        var entry = new FuelQueueEntry { ExhaustCredits = true, CreditReserve = 1000 };
        uint? remaining = FuelQueueDecisions.InitialTarget(entry, 9999);
        Assert.Null(remaining);
        var first = FuelQueueDecisions.PlanPurchase(entry, remaining, 100, 26_050, 100);
        Assert.Equal(100U, first.Quantity);
        remaining = FuelQueueDecisions.RemainingAfterPurchase(remaining, first.Quantity);
        Assert.Null(remaining);
        var second = FuelQueueDecisions.PlanPurchase(entry, remaining, 999, 16_050, 100);
        Assert.Equal(150U, second.Quantity);
        var done = FuelQueueDecisions.PlanPurchase(entry, remaining, 999, 1_050, 100);
        Assert.True(done.CreditExhausted); Assert.True(done.FinishAfterDeposit);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(50U)]
    [InlineData(99U)]
    public void PartialOrZeroDepositContinuesToAnotherSelectedCharacter(uint moved)
        => Assert.Equal(FuelQueueDepositDecision.TryNextCharacter, FuelQueueDecisions.AfterDeposit(100, moved, false));

    [Fact]
    public void FullyStoredFinalLoadCompletesCharacter()
        => Assert.Equal(FuelQueueDepositDecision.Complete, FuelQueueDecisions.AfterDeposit(100, 100, true));

    [Fact]
    public void InvalidMovementCannotSilentlyConsumeAnExtraBudget()
    {
        Assert.Throws<InvalidOperationException>(() => FuelQueueDecisions.RemainingAfterPurchase(50, 51));
        Assert.Throws<InvalidOperationException>(() => FuelQueueDecisions.AfterDeposit(100, 101, false));
    }

    [Fact]
    public void ThresholdAlreadyMetCompletesWithoutBuying()
    {
        var entry = new FuelQueueEntry { BuyBelow = 100, TargetQuantity = 500 };
        var remaining = FuelQueueDecisions.InitialTarget(entry, 100);
        var result = FuelQueueDecisions.PlanPurchase(entry, remaining, 999, 100_000, 100);
        Assert.Equal(0U, result.Quantity); Assert.True(result.FinishAfterDeposit);
    }

    [Fact]
    public void ExhaustionKeysShareOnlyTheSameWorldCompanyAndReserve()
    {
        var a = new FuelQueueEntry { CharacterId = 1, World = "Cuchulainn", FreeCompanyId = 55, CreditReserve = 500 };
        var b = new FuelQueueEntry { CharacterId = 2, World = " cuchulainn ", FreeCompanyId = 55, CreditReserve = 500 };
        Assert.Equal(FuelQueueDecisions.CompanyBudgetKey(a), FuelQueueDecisions.CompanyBudgetKey(b));
        b.FreeCompanyId = 56;
        Assert.NotEqual(FuelQueueDecisions.CompanyBudgetKey(a), FuelQueueDecisions.CompanyBudgetKey(b));
        b.FreeCompanyId = 55; b.CreditReserve = 0;
        Assert.NotEqual(FuelQueueDecisions.CompanyBudgetKey(a), FuelQueueDecisions.CompanyBudgetKey(b));
        b.CreditReserve = 500; b.World = "Seraph";
        Assert.NotEqual(FuelQueueDecisions.CompanyBudgetKey(a), FuelQueueDecisions.CompanyBudgetKey(b));
    }

    [Fact]
    public void UnknownCompaniesNeverShareAnExhaustionKey()
    {
        var a = new FuelQueueEntry { CharacterId = 1, World = "Cuchulainn" };
        var b = new FuelQueueEntry { CharacterId = 2, World = "Cuchulainn" };
        Assert.NotEqual(FuelQueueDecisions.CompanyBudgetKey(a), FuelQueueDecisions.CompanyBudgetKey(b));
    }
}
