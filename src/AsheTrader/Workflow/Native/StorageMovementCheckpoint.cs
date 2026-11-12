namespace AsheTrader.Workflow.Native;

/// <summary>A durable boundary saved before dispatch and again after an acknowledged storage movement.</summary>
public sealed class StorageMovementCheckpoint
{
    public int Version { get; set; } = 1;
    public Guid OperationId { get; set; }
    public ulong CharacterId { get; set; }
    public uint ItemId { get; set; }
    public bool HighQuality { get; set; }
    public GameOperationKind Kind { get; set; }
    public uint RequestedQuantity { get; set; }
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = "";
    public uint BagBeforeQuantity { get; set; }
    public uint RetainerBeforeQuantity { get; set; }
    // Zero denotes a settled boundary: no unacknowledged movement may follow this tally.
    public uint AttemptedQuantity { get; set; }
    public uint ConfirmedMovedBefore { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

internal static class StorageRecoveryTally
{
    public static uint Reconcile(GameOperation operation, StorageMovementCheckpoint? checkpoint,
        ulong currentCharacterId, uint currentBagQuantity, uint? currentRetainerQuantity)
    {
        if (checkpoint == null)
            throw new InvalidOperationException("This older storage operation has no saved inventory baseline from before the interrupted movement. Its remaining quantity cannot be determined automatically; preserve diagnostics rather than guessing or repeating the movement.");
        if (checkpoint.Version != 1 || operation.Kind is not (GameOperationKind.Deposit or GameOperationKind.Withdraw)
            || checkpoint.OperationId != operation.Id || checkpoint.CharacterId != operation.CharacterId
            || checkpoint.ItemId != operation.ItemId || checkpoint.HighQuality != operation.HighQuality
            || checkpoint.Kind != operation.Kind || checkpoint.RequestedQuantity != operation.Quantity
            || operation.Quantity == 0 || operation.ItemId == 0 || operation.CharacterId == 0)
            throw new InvalidOperationException("The saved storage movement does not match this command, character, item or quality. Preserve diagnostics; no movement was repeated.");
        if (currentCharacterId != operation.CharacterId)
            throw new InvalidOperationException("Log in the original storage character so its current inventory can be tallied automatically.");
        if (checkpoint.ConfirmedMovedBefore > operation.Quantity
            || checkpoint.AttemptedQuantity > operation.Quantity - checkpoint.ConfirmedMovedBefore
            || checkpoint.AttemptedQuantity > 0 && checkpoint.RetainerId == 0
            || operation.Kind == GameOperationKind.Deposit && checkpoint.RetainerId != 0
                && operation.AllowedRetainerIds is { Length: > 0 } allowed && !allowed.Contains(checkpoint.RetainerId))
            throw new InvalidOperationException("The saved storage movement exceeds the command's quantity or permitted retainers. Preserve diagnostics; no movement was repeated.");

        var deposit = operation.Kind == GameOperationKind.Deposit;
        var bagDelta = deposit ? (long)checkpoint.BagBeforeQuantity - currentBagQuantity
            : (long)currentBagQuantity - checkpoint.BagBeforeQuantity;
        if (bagDelta < 0 || bagDelta > checkpoint.AttemptedQuantity)
            throw new InvalidOperationException($"The current bag tally changed by {bagDelta:N0}, outside the interrupted movement's range of 0–{checkpoint.AttemptedQuantity:N0}. Other item movement may have occurred; no amount was guessed or repeated.");
        if (currentRetainerQuantity is { } retainerQuantity)
        {
            var retainerDelta = deposit ? (long)retainerQuantity - checkpoint.RetainerBeforeQuantity
                : (long)checkpoint.RetainerBeforeQuantity - retainerQuantity;
            if (retainerDelta != bagDelta)
                throw new InvalidOperationException("The current bag and retainer tallies disagree about the interrupted movement. Wait for the inventory to settle; no amount was guessed or repeated.");
        }
        return checked(checkpoint.ConfirmedMovedBefore + (uint)bagDelta);
    }
}

internal sealed class StorageRecoveryStability
{
    public const int MinimumIntervalMilliseconds = 500;
    private (uint Bags, uint? Retainer)? last;
    private long firstObservedAt;

    public bool Observe(long now, uint bags, uint? retainer)
    {
        var observation = (bags, retainer);
        if (last != observation)
        {
            last = observation;
            firstObservedAt = now;
            return false;
        }
        return now - firstObservedAt >= MinimumIntervalMilliseconds;
    }

    public void Reset() => last = null;
}
