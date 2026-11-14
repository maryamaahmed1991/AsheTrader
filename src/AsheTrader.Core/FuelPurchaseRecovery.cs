namespace AsheTrader.Core;

/// <summary>Durable intent captured before a purchase callback. Never infer success from credits alone.</summary>
public sealed class FuelPurchaseCheckpoint
{
    public ulong CharacterId { get; set; }
    public ulong FreeCompanyId { get; set; }
    public uint Quantity { get; set; }
    public uint BagBefore { get; set; }
    public uint CreditsBefore { get; set; }
    public uint UnitPrice { get; set; }
}

public enum FuelPurchaseOutcome { Unchanged, Completed, Inconsistent }

public static class FuelPurchaseRecovery
{
    /// <summary>
    /// Unchanged is only a candidate for retry: the caller must first observe stable counts,
    /// no purchase confirmation and no active transaction. Partial updates remain inconsistent.
    /// </summary>
    public static FuelPurchaseOutcome Classify(FuelPurchaseCheckpoint checkpoint, ulong characterId,
        ulong freeCompanyId, uint bagQuantity, uint credits)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var cost = (ulong)checkpoint.Quantity * checkpoint.UnitPrice;
        if (checkpoint.CharacterId == 0 || checkpoint.FreeCompanyId == 0 || checkpoint.Quantity is 0 or > FuelPlanner.MaxPurchasePerTransaction
            || checkpoint.UnitPrice == 0 || checkpoint.CharacterId != characterId || checkpoint.FreeCompanyId != freeCompanyId
            || cost > checkpoint.CreditsBefore || (ulong)checkpoint.BagBefore + checkpoint.Quantity > uint.MaxValue)
            return FuelPurchaseOutcome.Inconsistent;
        if (bagQuantity == checkpoint.BagBefore && credits == checkpoint.CreditsBefore)
            return FuelPurchaseOutcome.Unchanged;
        return (ulong)bagQuantity == (ulong)checkpoint.BagBefore + checkpoint.Quantity
            && (ulong)credits + cost == checkpoint.CreditsBefore
            ? FuelPurchaseOutcome.Completed : FuelPurchaseOutcome.Inconsistent;
    }
}
