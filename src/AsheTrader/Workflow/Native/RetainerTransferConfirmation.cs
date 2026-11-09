using AsheTrader.Core;

namespace AsheTrader.Workflow.Native;

/// <summary>
/// Confirms one issued retainer movement from both inventories without scheduling another command.
/// Pacing is measured from dispatch, so verification does not add a second fixed delay.
/// </summary>
internal sealed class RetainerTransferConfirmation
{
    public const int MinimumIntervalMilliseconds = 350;
    internal const int TimeoutMs = 15_000;
    private readonly Dictionary<ItemKey, ulong> beforeBags;
    private readonly Dictionary<ItemKey, ulong> beforeRetainer;
    private readonly ItemKey item;
    private readonly uint quantity;
    private readonly bool deposit;
    private long startedAt;
    private long? lastMatchAt;
    private int matchingObservations;

    public RetainerTransferConfirmation(long startedAt, IReadOnlyDictionary<ItemKey, ulong> beforeBags,
        IReadOnlyDictionary<ItemKey, ulong> beforeRetainer, ItemKey item, uint quantity, bool deposit)
    {
        ArgumentNullException.ThrowIfNull(beforeBags);
        ArgumentNullException.ThrowIfNull(beforeRetainer);
        if (quantity == 0) throw new ArgumentOutOfRangeException(nameof(quantity), "A positive transfer quantity is required.");
        this.startedAt = startedAt;
        this.beforeBags = new(beforeBags);
        this.beforeRetainer = new(beforeRetainer);
        this.item = item;
        this.quantity = quantity;
        this.deposit = deposit;
    }

    public bool Observe(long now, IReadOnlyDictionary<ItemKey, ulong> bags, IReadOnlyDictionary<ItemKey, ulong> retainer)
    {
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(retainer);
        if (ElapsedMilliseconds(now) > TimeoutMs)
            throw new InvalidOperationException("Retainer movement could not be reconciled exactly. It was not retried; inspect the inventories before resuming.");

        if (!MatchesDelta(beforeBags, bags, increase: !deposit) || !MatchesDelta(beforeRetainer, retainer, increase: deposit))
        {
            lastMatchAt = null;
            matchingObservations = 0;
            return false;
        }

        // Re-reading in the same update cannot prove the state survived another update.
        if (!lastMatchAt.HasValue || now > lastMatchAt.Value)
        {
            matchingObservations = Math.Min(2, matchingObservations + 1);
            lastMatchAt = now;
        }
        return matchingObservations >= 2 && ElapsedMilliseconds(now) >= MinimumIntervalMilliseconds;
    }

    public long ElapsedMilliseconds(long now) => Math.Max(0, now - startedAt);

    public void Suspend(long elapsed)
    {
        if (elapsed < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        startedAt = checked(startedAt + elapsed);
        // Observe fresh state after a cutscene rather than reusing a pre-scene match.
        lastMatchAt = null;
        matchingObservations = 0;
    }

    private bool MatchesDelta(IReadOnlyDictionary<ItemKey, ulong> before, IReadOnlyDictionary<ItemKey, ulong> after, bool increase)
    {
        foreach (var key in before.Keys.Union(after.Keys).Append(item).Distinct())
        {
            var oldValue = before.GetValueOrDefault(key);
            var newValue = after.GetValueOrDefault(key);
            if (key != item)
            {
                if (oldValue != newValue) return false;
            }
            else if (increase
                ? newValue < oldValue || newValue - oldValue != quantity
                : oldValue < newValue || oldValue - newValue != quantity)
                return false;
        }
        return true;
    }
}
