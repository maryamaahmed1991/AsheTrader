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
    /// <summary>A positive saved receipt can be tallied anywhere. Unchanged bags alone
    /// do not authorize clearing or repeating the purchase; that still requires shop evidence.</summary>
    public static FuelPurchaseOutcome ClassifyInventoryReceipt(FuelPurchaseCheckpoint checkpoint,
        ulong characterId, ulong freeCompanyId, uint bagQuantity)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var cost = (ulong)checkpoint.Quantity * checkpoint.UnitPrice;
        if (checkpoint.CharacterId == 0 || checkpoint.FreeCompanyId == 0
            || checkpoint.CharacterId != characterId || checkpoint.FreeCompanyId != freeCompanyId
            || checkpoint.Quantity is 0 or > FuelPlanner.MaxPurchasePerTransaction || checkpoint.UnitPrice == 0
            || cost > checkpoint.CreditsBefore || (ulong)checkpoint.BagBefore + checkpoint.Quantity > uint.MaxValue)
            return FuelPurchaseOutcome.Inconsistent;
        if ((ulong)bagQuantity == (ulong)checkpoint.BagBefore + checkpoint.Quantity) return FuelPurchaseOutcome.Completed;
        return bagQuantity == checkpoint.BagBefore ? FuelPurchaseOutcome.Unchanged : FuelPurchaseOutcome.Inconsistent;
    }

    /// <summary>
    /// Unchanged is only a candidate for retry: the caller must first observe stable counts,
    /// no purchase confirmation and no active transaction. FC credits are shared, so exact item
    /// receipt establishes completion even when concurrent FC activity changes the credit balance.
    /// Partial inventory updates remain inconsistent; credits alone never establish completion.
    /// </summary>
    public static FuelPurchaseOutcome Classify(FuelPurchaseCheckpoint checkpoint, ulong characterId,
        ulong freeCompanyId, uint bagQuantity, uint credits, uint currentUnitPrice)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var cost = (ulong)checkpoint.Quantity * checkpoint.UnitPrice;
        if (checkpoint.CharacterId == 0 || checkpoint.FreeCompanyId == 0 || checkpoint.Quantity is 0 or > FuelPlanner.MaxPurchasePerTransaction
            || checkpoint.UnitPrice == 0 || currentUnitPrice != checkpoint.UnitPrice
            || checkpoint.CharacterId != characterId || checkpoint.FreeCompanyId != freeCompanyId
            || cost > checkpoint.CreditsBefore || (ulong)checkpoint.BagBefore + checkpoint.Quantity > uint.MaxValue)
            return FuelPurchaseOutcome.Inconsistent;
        if (bagQuantity == checkpoint.BagBefore && credits == checkpoint.CreditsBefore)
            return FuelPurchaseOutcome.Unchanged;
        return (ulong)bagQuantity == (ulong)checkpoint.BagBefore + checkpoint.Quantity
            ? FuelPurchaseOutcome.Completed : FuelPurchaseOutcome.Inconsistent;
    }

    /// <summary>Live purchases also wait for the shop's inventory display and confirmation to settle.</summary>
    public static bool CanAcknowledge(FuelPurchaseCheckpoint checkpoint, ulong characterId,
        ulong freeCompanyId, uint bagQuantity, uint displayedBagQuantity, uint credits,
        uint currentUnitPrice, bool confirmationVisible)
        => !confirmationVisible && bagQuantity == displayedBagQuantity
            && Classify(checkpoint, characterId, freeCompanyId, bagQuantity, credits, currentUnitPrice) == FuelPurchaseOutcome.Completed;
}
