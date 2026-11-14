using AsheTrader.Core;
using Newtonsoft.Json;

namespace AsheTrader.Core.Tests;

public sealed class FuelCreditBudgetTests
{
    private static FuelPurchaseCheckpoint Receipt(uint beforeCredits, uint quantity = 1) => new()
    { CharacterId = 123, FreeCompanyId = 456, Quantity = quantity, BagBefore = 200, CreditsBefore = beforeCredits, UnitPrice = 100 };

    [Fact]
    public void StaleCreditsCannotFundASecondBatchThroughTheReserve()
    {
        var entry = new FuelQueueEntry { ExhaustCredits = true, CreditReserve = 1_000 };
        var budget = FuelCreditBudget.Begin(456, 1_100);
        var first = FuelQueueDecisions.PlanPurchase(entry, null, 10_000, budget.AvailableCredits(1_100), 100);
        Assert.Equal(1U, first.Quantity);
        budget.Acknowledge(Receipt(1_100));

        // Item receipt reached the client before the credit-shop balance changed.
        var second = FuelQueueDecisions.PlanPurchase(entry, null, 9_999, budget.AvailableCredits(1_100), 100);
        Assert.Equal(0U, second.Quantity);
        Assert.Equal(1_000U, budget.AvailableCredits(1_100));
        Assert.True(budget.NeedsRefresh(1_100, entry.CreditReserve, 100));
    }

    [Theory]
    [InlineData(1_100U, 1U)] // New FC income exactly offset the acknowledged purchase.
    [InlineData(1_300U, 3U)] // FC income exceeded the purchase cost.
    [InlineData(1_000U, 0U)] // No income; the original display was only stale.
    public void FreshlyReopenedSupplierDistinguishesIncomeFromCreditLag(uint freshCredits, uint affordable)
    {
        var budget = FuelCreditBudget.Begin(456, 1_100);
        budget.Acknowledge(Receipt(1_100));
        Assert.Equal(1_000U, budget.AvailableCredits(freshCredits));

        // The queue creates this new epoch only after fully closing and reopening the supplier.
        var refreshed = FuelCreditBudget.Begin(456, freshCredits);
        var entry = new FuelQueueEntry { ExhaustCredits = true, CreditReserve = 1_000 };
        var next = FuelQueueDecisions.PlanPurchase(entry, null, 10_000, refreshed.AvailableCredits(freshCredits), 100);
        Assert.Equal(affordable, next.Quantity);
    }

    [Fact]
    public void CreditIncomeDoesNotResetTheKnownLocalSpendingCeiling()
    {
        var budget = FuelCreditBudget.Begin(456, 10_000);
        budget.Acknowledge(Receipt(10_000, 10));
        Assert.Equal(9_000U, budget.AvailableCredits(10_027));
        Assert.Equal(9_000U, budget.AvailableCredits(50_000));
        budget.Acknowledge(Receipt(50_000, 10));
        Assert.Equal(8_000U, budget.AvailableCredits(49_000));
        Assert.Equal(8_000U, budget.AvailableCredits(50_000));
    }

    [Fact]
    public void CompletedTargetDoesNotFalselyExhaustTheFcForAnotherBuyer()
    {
        var entry = new FuelQueueEntry { CreditReserve = 1_000 };
        var budget = FuelCreditBudget.Begin(456, 1_100);
        budget.Acknowledge(Receipt(1_100));
        var completed = FuelQueueDecisions.PlanPurchase(entry, 0, 10_000, budget.PlanningCredits(1_100, 0), 100);
        Assert.Equal(0U, completed.Quantity);
        Assert.True(completed.FinishAfterDeposit);
        Assert.False(completed.CreditExhausted);
        // The next buyer still cannot spend the unrefreshed credit display.
        Assert.Equal(1_000U, budget.PlanningCredits(1_100, null));
        Assert.Equal(1_000U, budget.PlanningCredits(1_100, 1));
        Assert.True(budget.NeedsRefresh(1_100, 1_000, 100));
    }

    [Fact]
    public void AnotherMembersCreditSpendingStillLimitsTheNextPurchase()
    {
        var budget = FuelCreditBudget.Begin(456, 10_000);
        budget.Acknowledge(Receipt(10_000, 10));
        Assert.Equal(500U, budget.AvailableCredits(500));
        Assert.False(budget.NeedsRefresh(500, 1_000, 100));
    }

    [Fact]
    public void ResumeRetainsTheSameCeilingAndAcknowledgedSpend()
    {
        var budget = FuelCreditBudget.Begin(456, 1_100);
        budget.Acknowledge(Receipt(1_100));
        var resumed = JsonConvert.DeserializeObject<FuelCreditBudget>(JsonConvert.SerializeObject(budget))!;
        Assert.Equal(456UL, resumed.FreeCompanyId);
        Assert.Equal(100UL, resumed.AcknowledgedCost);
        Assert.Equal(1_000U, resumed.AvailableCredits(1_100));
        Assert.True(resumed.NeedsRefresh(1_100, 1_000, 100));
    }

    [Fact]
    public void LegacyInterruptedReceiptStartsFromItsOriginalCreditBaseline()
    {
        var pending = Receipt(16_029_397, 99);
        pending.BagBefore = 19_305;
        Assert.Equal(FuelPurchaseOutcome.Completed, FuelPurchaseRecovery.Classify(pending, 123, 456, 19_404, 16_079_059, 100));
        var budget = FuelCreditBudget.Begin(pending.FreeCompanyId, pending.CreditsBefore);
        budget.Acknowledge(pending);
        Assert.Equal(16_019_497U, budget.AvailableCredits(16_079_059));
        Assert.Equal(16_019_497U, budget.AvailableCredits(16_019_524));
    }

    [Theory]
    [InlineData(1_000U, 1_000U, false)]
    [InlineData(1_099U, 1_000U, false)]
    [InlineData(1_100U, 1_000U, true)]
    [InlineData(1_000U, 0U, false)]
    public void RefreshOnlyWhenUnaccountedShownCreditsCouldBuyAnotherTank(uint shown, uint reserve, bool refresh)
    {
        var budget = FuelCreditBudget.Begin(456, 1_100);
        budget.Acknowledge(Receipt(1_100));
        Assert.Equal(refresh, budget.NeedsRefresh(shown, reserve, 100));
    }

    [Fact]
    public void DifferentCompanyCannotChargeOrReuseTheBudget()
    {
        var budget = FuelCreditBudget.Begin(456, 1_100);
        var receipt = Receipt(1_100);
        receipt.FreeCompanyId = 789;
        Assert.Throws<InvalidOperationException>(() => budget.Acknowledge(receipt));
        Assert.Equal(0UL, budget.AcknowledgedCost);
        Assert.Throws<ArgumentOutOfRangeException>(() => FuelCreditBudget.Begin(0, 1_100));
    }

    [Fact]
    public void InvalidReceiptOrPriceCannotExpandTheCeiling()
    {
        var budget = FuelCreditBudget.Begin(456, 1_100);
        var receipt = Receipt(99);
        Assert.Throws<InvalidOperationException>(() => budget.Acknowledge(receipt));
        receipt = Receipt(1_100, 0);
        Assert.Throws<InvalidOperationException>(() => budget.Acknowledge(receipt));
        receipt = Receipt(1_100);
        receipt.UnitPrice = 0;
        Assert.Throws<InvalidOperationException>(() => budget.Acknowledge(receipt));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.NeedsRefresh(1_100, 0, 0));
        Assert.Equal(1_100U, budget.AvailableCredits(1_100));
    }
}
