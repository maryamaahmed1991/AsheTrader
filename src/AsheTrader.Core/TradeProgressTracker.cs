namespace AsheTrader.Core;

public enum TradeOutcome
{
    Unknown,
    Cancelled,
    Completed,
}

/// <summary>
/// Tracks one run. The adapter must both observe successful trade completion and provide
/// full carried-inventory snapshots taken immediately before and after that trade.
/// Cancelled, uncertain, replayed, or mismatching trades never consume the requested quantities.
/// This object is intended to be used on one framework thread.
/// </summary>
public sealed class TradeProgressTracker
{
    private readonly Dictionary<ItemKey, uint> remaining;
    private readonly HashSet<Guid> committedBatches = [];

    public TradeProgressTracker(IEnumerable<TradeLine> lines) => remaining = TradePlanner.ValidateLines(lines);

    public int CompletedBatches => committedBatches.Count;
    public bool IsComplete => remaining.Count == 0;
    public IReadOnlyList<TradeLine> Remaining => remaining.Select(pair => new TradeLine
    {
        ItemId = pair.Key.ItemId,
        HighQuality = pair.Key.HighQuality,
        Quantity = pair.Value,
    }).ToArray();

    public bool TryCommit(
        TradeBatch batch,
        TradeOutcome outcome,
        IReadOnlyDictionary<ItemKey, ulong> before,
        IReadOnlyDictionary<ItemKey, ulong> after,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (outcome != TradeOutcome.Completed)
        {
            error = outcome == TradeOutcome.Cancelled ? "Trade was cancelled; progress was not changed." : "Trade completion is unconfirmed; progress was not changed.";
            return false;
        }

        if (!batch.HasItems || committedBatches.Contains(batch.Id))
        {
            error = !batch.HasItems ? "Cannot commit an empty trade." : "This trade batch has already been committed.";
            return false;
        }

        var expected = batch.Entries.GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.Aggregate(0UL, (total, entry) => total + entry.Quantity));
        foreach (var (key, quantity) in expected)
        {
            if (!remaining.TryGetValue(key, out var needed) || quantity > needed)
            {
                error = $"Batch quantity for item {key} exceeds the remaining requested quantity.";
                return false;
            }
        }

        // Checking the entire snapshot also catches unexpected incoming items or unrelated
        // inventory activity. A matching decrease alone cannot establish causation.
        foreach (var key in before.Keys.Union(after.Keys).Union(expected.Keys))
        {
            var oldQuantity = before.GetValueOrDefault(key);
            var newQuantity = after.GetValueOrDefault(key);
            var expectedDecrease = expected.GetValueOrDefault(key);
            if (newQuantity > oldQuantity || oldQuantity - newQuantity != expectedDecrease)
            {
                error = $"Inventory change for item {key} does not exactly match the planned trade; stop and reconcile before resuming.";
                return false;
            }
        }

        foreach (var (key, quantity) in expected)
        {
            remaining[key] -= checked((uint)quantity);
            if (remaining[key] == 0)
                remaining.Remove(key);
        }
        committedBatches.Add(batch.Id);
        error = string.Empty;
        return true;
    }
}
