namespace AsheTrader.Core;

public enum FuelPlanStatus
{
    Disabled,
    ThresholdNotReached,
    Ready,
    LimitedByCredits,
    LimitedByCapacity,
    LimitedByCreditsAndCapacity,
}

public sealed record FuelPurchasePlan(
    uint Quantity,
    ulong CreditCost,
    IReadOnlyList<uint> Transactions,
    FuelPlanStatus Status)
{
    public bool HasPurchases => Quantity > 0;
}

public static class FuelPlanner
{
    public const uint MaxPurchasePerTransaction = 99;

    /// <param name="availableCapacity">Number of additional fuel units that carried inventory can hold.</param>
    public static FuelPurchasePlan Plan(
        FuelPolicy policy,
        uint currentQuantity,
        ulong availableCredits,
        uint creditsPerUnit,
        uint availableCapacity)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
            return Empty(FuelPlanStatus.Disabled);
        if (!policy.ExhaustCredits && (policy.BuyBelow == 0 || policy.TargetQuantity == 0 || policy.BuyBelow > policy.TargetQuantity))
            throw new ArgumentException("Fuel policies require a positive purchase threshold no greater than the target quantity.", nameof(policy));
        if (creditsPerUnit == 0)
            throw new ArgumentOutOfRangeException(nameof(creditsPerUnit), "Fuel unit cost must be positive.");
        if (!policy.ExhaustCredits && currentQuantity >= policy.BuyBelow)
            return Empty(FuelPlanStatus.ThresholdNotReached);

        var spendable = availableCredits > policy.CreditReserve ? availableCredits - policy.CreditReserve : 0UL;
        var affordable = spendable / creditsPerUnit;
        var wanted = policy.ExhaustCredits ? affordable : policy.TargetQuantity - currentQuantity;
        var quantity = (uint)Math.Min(wanted, Math.Min(affordable, availableCapacity));
        var creditLimited = affordable < wanted;
        var capacityLimited = availableCapacity < wanted;
        if (policy.ExhaustCredits && affordable == 0)
            return Empty(FuelPlanStatus.LimitedByCredits);
        var status = (creditLimited, capacityLimited) switch
        {
            (true, true) => FuelPlanStatus.LimitedByCreditsAndCapacity,
            (true, false) => FuelPlanStatus.LimitedByCredits,
            (false, true) => FuelPlanStatus.LimitedByCapacity,
            _ => FuelPlanStatus.Ready,
        };
        return new FuelPurchasePlan(quantity, (ulong)quantity * creditsPerUnit, new PurchaseTransactions(quantity), status);
    }

    private static FuelPurchasePlan Empty(FuelPlanStatus status) => new(0, 0, Array.Empty<uint>(), status);

    // A large requested quantity must not allocate millions of entries merely to plan a run.
    private sealed class PurchaseTransactions(uint quantity) : IReadOnlyList<uint>
    {
        public int Count { get; } = (int)(((ulong)quantity + MaxPurchasePerTransaction - 1) / MaxPurchasePerTransaction);
        public uint this[int index] => index < 0 || index >= Count
            ? throw new ArgumentOutOfRangeException(nameof(index))
            : Math.Min(MaxPurchasePerTransaction, quantity - (uint)index * MaxPurchasePerTransaction);
        public IEnumerator<uint> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
