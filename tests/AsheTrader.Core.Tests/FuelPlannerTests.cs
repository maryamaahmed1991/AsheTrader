using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelPlannerTests
{
    [Theory]
    [InlineData(100U)]
    [InlineData(101U)]
    [InlineData(600U)]
    public void DoesNotBuyAtOrAboveThreshold(uint current)
    {
        var result = FuelPlanner.Plan(Policy(), current, 100_000, 100, 1000);
        Assert.Equal(FuelPlanStatus.ThresholdNotReached, result.Status);
        Assert.False(result.HasPurchases);
    }

    [Fact]
    public void RestocksToTargetInNinetyNineUnitTransactions()
    {
        var result = FuelPlanner.Plan(Policy(), 50, 100_000, 100, 1000);
        Assert.Equal(450U, result.Quantity);
        Assert.Equal(45_000UL, result.CreditCost);
        Assert.Equal(new uint[] { 99, 99, 99, 99, 54 }, result.Transactions);
        Assert.Equal(FuelPlanStatus.Ready, result.Status);
    }

    [Fact]
    public void CreditsCannotBeOverspentAndRemainderIsNotAnExtraUnit()
    {
        var result = FuelPlanner.Plan(Policy(), 0, 10_099, 100, 1000);
        Assert.Equal(100U, result.Quantity);
        Assert.Equal(10_000UL, result.CreditCost);
        Assert.Equal(new uint[] { 99, 1 }, result.Transactions);
        Assert.Equal(FuelPlanStatus.LimitedByCredits, result.Status);
    }

    [Fact]
    public void AdditionalCapacityCapsPurchases()
    {
        var result = FuelPlanner.Plan(Policy(), 50, 100_000, 100, 20);
        Assert.Equal(20U, result.Quantity);
        Assert.Equal(FuelPlanStatus.LimitedByCapacity, result.Status);
    }

    [Fact]
    public void ReportsBothResourceLimits()
    {
        var result = FuelPlanner.Plan(Policy(), 50, 5_000, 100, 20);
        Assert.Equal(20U, result.Quantity);
        Assert.Equal(FuelPlanStatus.LimitedByCreditsAndCapacity, result.Status);
    }

    [Theory]
    [InlineData(0UL, 100U)]
    [InlineData(99UL, 100U)]
    [InlineData(100_000UL, 0U)]
    public void ZeroAffordableUnitsOrCapacityProduceNoTransactions(ulong credits, uint capacity)
    {
        var result = FuelPlanner.Plan(Policy(), 0, credits, 100, capacity);
        Assert.False(result.HasPurchases);
        Assert.Empty(result.Transactions);
    }

    [Fact]
    public void DisabledPolicyDoesNotRequirePurchaseSettings()
    {
        var result = FuelPlanner.Plan(new FuelPolicy(), 0, 0, 0, 0);
        Assert.Equal(FuelPlanStatus.Disabled, result.Status);
    }

    [Fact]
    public void RejectsThresholdAboveTargetAndZeroCost()
    {
        Assert.Throws<ArgumentException>(() => FuelPlanner.Plan(new FuelPolicy { Enabled = true, BuyBelow = 100, TargetQuantity = 99 }, 0, 1000, 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => FuelPlanner.Plan(Policy(), 0, 1000, 0, 100));
    }

    [Fact]
    public void ExhaustCreditsIgnoresThresholdAndTarget()
    {
        var policy = new FuelPolicy { Enabled = true, ExhaustCredits = true };
        var result = FuelPlanner.Plan(policy, 10_000, 20_050, 100, 1000);

        Assert.Equal(200U, result.Quantity);
        Assert.Equal(20_000UL, result.CreditCost);
        Assert.Equal(new uint[] { 99, 99, 2 }, result.Transactions);
        Assert.Equal(FuelPlanStatus.Ready, result.Status);
    }

    [Fact]
    public void ExhaustCreditsStillHonorsInventoryCapacity()
    {
        var policy = new FuelPolicy { Enabled = true, ExhaustCredits = true };
        var result = FuelPlanner.Plan(policy, 0, 1_000_000, 100, 124);

        Assert.Equal(124U, result.Quantity);
        Assert.Equal(new uint[] { 99, 25 }, result.Transactions);
        Assert.Equal(FuelPlanStatus.LimitedByCapacity, result.Status);
    }

    [Fact]
    public void ExhaustCreditsWithInsufficientCreditsIsExplicit()
    {
        var result = FuelPlanner.Plan(new FuelPolicy { Enabled = true, ExhaustCredits = true }, 0, 99, 100, 1000);
        Assert.Equal(FuelPlanStatus.LimitedByCredits, result.Status);
        Assert.Empty(result.Transactions);
    }

    [Fact]
    public void HugeInputPlansWithoutAllocatingEveryTransactionOrOverflowing()
    {
        var result = FuelPlanner.Plan(new FuelPolicy { Enabled = true, ExhaustCredits = true }, 0, ulong.MaxValue, uint.MaxValue, uint.MaxValue);

        Assert.Equal(uint.MaxValue, result.Quantity);
        Assert.Equal((ulong)uint.MaxValue * uint.MaxValue, result.CreditCost);
        Assert.Equal(43_383_509, result.Transactions.Count);
        Assert.InRange(result.Transactions[^1], 1U, 99U);
    }

    private static FuelPolicy Policy() => new() { Enabled = true, BuyBelow = 100, TargetQuantity = 500 };

    [Fact]
    public void ExhaustCreditsPreservesSharedCompanyReserveAndFractionalRemainder()
    {
        var policy = new FuelPolicy { Enabled = true, ExhaustCredits = true, CreditReserve = 10_000 };
        var result = FuelPlanner.Plan(policy, 0, 20_050, 100, 1000);
        Assert.Equal(100U, result.Quantity);
        Assert.Equal(10_000UL, result.CreditCost);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(9_999UL)]
    [InlineData(10_000UL)]
    [InlineData(10_099UL)]
    public void ReserveCannotUnderflowOrBeSpent(ulong credits)
    {
        var result = FuelPlanner.Plan(new FuelPolicy { Enabled = true, ExhaustCredits = true, CreditReserve = 10_000 }, 0, credits, 100, 1000);
        Assert.Equal(0U, result.Quantity);
        Assert.Equal(FuelPlanStatus.LimitedByCredits, result.Status);
    }

    [Fact]
    public void RestockModeAlsoHonorsCreditReserve()
    {
        var policy = Policy(); policy.CreditReserve = 10_000;
        var result = FuelPlanner.Plan(policy, 0, 15_000, 100, 1000);
        Assert.Equal(50U, result.Quantity);
        Assert.Equal(FuelPlanStatus.LimitedByCredits, result.Status);
    }
}
