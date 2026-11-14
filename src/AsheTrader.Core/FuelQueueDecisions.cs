using System.Globalization;

namespace AsheTrader.Core;

public sealed record FuelQueuePurchaseDecision(uint Quantity, bool NeedsDeposit, bool FinishAfterDeposit,
    bool CreditExhausted, string Reason);

public enum FuelQueueDepositDecision { ReturnToSupplier, Complete, TryNextCharacter }

/// <summary>Budget and storage decisions shared by the durable queue and its isolated tests.</summary>
public static class FuelQueueDecisions
{
    public static uint? InitialTarget(FuelQueueEntry entry, uint currentBags)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ExhaustCredits) return null;
        if (entry.BuyBelow == 0 || entry.TargetQuantity == 0 || entry.BuyBelow > entry.TargetQuantity)
            throw new ArgumentException("The fuel threshold must be positive and no greater than its target.", nameof(entry));
        return currentBags >= entry.BuyBelow ? 0 : entry.TargetQuantity - currentBags;
    }

    public static uint? RemainingAfterPurchase(uint? remaining, uint quantity)
    {
        if (!remaining.HasValue) return null;
        if (quantity > remaining.Value) throw new InvalidOperationException("The purchase exceeds the saved fuel target.");
        return remaining.Value - quantity;
    }

    public static FuelQueuePurchaseDecision PlanPurchase(FuelQueueEntry entry, uint? remainingTarget,
        uint bagCapacity, uint liveCredits, uint unitPrice)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (unitPrice == 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
        var spendable = liveCredits > entry.CreditReserve ? liveCredits - entry.CreditReserve : 0;
        var affordable = spendable / unitPrice;
        var creditsDone = affordable == 0;
        var targetDone = remainingTarget == 0;
        if (creditsDone || targetDone)
            return new(0, true, true, creditsDone,
                creditsDone ? "FC credit budget exhausted." : "Configured fuel target reached.");
        if (bagCapacity == 0)
            return new(0, true, false, false, "Bags full. Depositing fuel before purchasing more…");
        var quantity = Math.Min(remainingTarget ?? uint.MaxValue, Math.Min(bagCapacity, affordable));
        return new(quantity, false, false, false, "Fuel budget and carried space available.");
    }

    public static FuelQueueDepositDecision AfterDeposit(uint requested, uint moved, bool finishAfterDeposit)
    {
        if (requested == 0 || moved > requested) throw new InvalidOperationException("The completed deposit does not match its requested quantity.");
        if (moved < requested) return FuelQueueDepositDecision.TryNextCharacter;
        return finishAfterDeposit ? FuelQueueDepositDecision.Complete : FuelQueueDepositDecision.ReturnToSupplier;
    }

    /// <summary>
    /// Exhaustion applies to a particular FC budget. Different reserves cannot share a completion
    /// marker, including when two initially unknown memberships later resolve to the same FC.
    /// </summary>
    public static string CompanyBudgetKey(FuelQueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var identity = entry.FreeCompanyId == 0
            ? $"character:{entry.CharacterId.ToString(CultureInfo.InvariantCulture)}"
            : $"fc:{entry.FreeCompanyId.ToString(CultureInfo.InvariantCulture)}";
        return $"{entry.World.Trim().ToUpperInvariant()}:{identity}:reserve:{entry.CreditReserve.ToString(CultureInfo.InvariantCulture)}";
    }
}
