namespace AsheTrader.Core;

/// <summary>
/// A conservative spending ceiling for one observed FC balance. Item receipt can precede the
/// shop's credit update, so acknowledged purchases reduce this ceiling even when that UI is stale.
/// Only a new supplier session may start a fresh budget from the displayed balance.
/// </summary>
public sealed class FuelCreditBudget
{
    public ulong FreeCompanyId { get; set; }
    public uint BaselineCredits { get; set; }
    public ulong AcknowledgedCost { get; set; }

    public static FuelCreditBudget Begin(ulong companyId, uint credits)
    {
        if (companyId == 0) throw new ArgumentOutOfRangeException(nameof(companyId));
        return new() { FreeCompanyId = companyId, BaselineCredits = credits };
    }

    public uint AvailableCredits(uint observedCredits)
        => (uint)Math.Min(observedCredits, AcknowledgedCost >= BaselineCredits ? 0UL : BaselineCredits - AcknowledgedCost);

    // A completed target cannot spend again. Its completion must not mark the entire FC as
    // exhausted solely because this conservative ceiling has not yet been refreshed.
    public uint PlanningCredits(uint observedCredits, uint? remainingTarget)
        => remainingTarget == 0 ? observedCredits : AvailableCredits(observedCredits);

    public bool NeedsRefresh(uint observedCredits, uint reserve, uint unitPrice)
    {
        if (unitPrice == 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
        var available = AvailableCredits(observedCredits);
        return observedCredits > reserve && observedCredits - reserve >= unitPrice
            && (available <= reserve || available - reserve < unitPrice);
    }

    public void Acknowledge(FuelPurchaseCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var cost = (ulong)checkpoint.Quantity * checkpoint.UnitPrice;
        if (FreeCompanyId == 0 || checkpoint.FreeCompanyId != FreeCompanyId
            || checkpoint.Quantity is 0 or > FuelPlanner.MaxPurchasePerTransaction
            || checkpoint.UnitPrice == 0 || cost > checkpoint.CreditsBefore)
            throw new InvalidOperationException("The acknowledged purchase does not belong to this FC credit budget.");
        AcknowledgedCost = checked(AcknowledgedCost + cost);
    }
}
