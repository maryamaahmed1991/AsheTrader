namespace AsheTrader.Trading;

/// <summary>
/// Times callbacks from their dispatch, while observations decide when an action is complete.
/// Beginning another batch preserves the request cooldown used by the previous batch.
/// </summary>
internal sealed class TradePacing
{
    internal const int OfferIntervalMilliseconds = 250;
    internal const int RequestIntervalMilliseconds = 3000;
    private long? lastRequestAt;
    private long? lastOfferAt;
    private string? incomingFingerprint;
    private long? incomingObservedAt;
    private int incomingObservations;
    private long? settlementObservedAt;
    private int settlementObservations;

    public bool OfferOutstanding { get; private set; }

    public void BeginBatch()
    {
        lastOfferAt = null;
        OfferOutstanding = false;
        ResetIncoming();
        settlementObservedAt = null;
        settlementObservations = 0;
    }

    public bool CanRequest(long now) => !lastRequestAt.HasValue
        || now - lastRequestAt.Value >= RequestIntervalMilliseconds;

    public void RecordRequest(long now)
    {
        if (!CanRequest(now)) throw new InvalidOperationException("The previous trade request is still cooling down.");
        lastRequestAt = now;
    }

    public bool CanOffer(long now) => !OfferOutstanding
        && (!lastOfferAt.HasValue || now - lastOfferAt.Value >= OfferIntervalMilliseconds);

    public void RecordOffer(long now)
    {
        if (!CanOffer(now)) throw new InvalidOperationException("The previous item offer is still pending or cooling down.");
        lastOfferAt = now;
        OfferOutstanding = true;
    }

    public void ConfirmOfferedItem()
    {
        if (!OfferOutstanding) throw new InvalidOperationException("No item offer is awaiting observation.");
        OfferOutstanding = false;
    }

    public bool ObserveIncoming(long now, string fingerprint, bool lockedAndSynchronized)
    {
        if (!lockedAndSynchronized || string.IsNullOrEmpty(fingerprint))
        {
            ResetIncoming();
            return false;
        }
        if (!string.Equals(incomingFingerprint, fingerprint, StringComparison.Ordinal))
        {
            incomingFingerprint = fingerprint;
            incomingObservedAt = now;
            incomingObservations = 1;
            return false;
        }
        CountDistinctObservation(now, ref incomingObservedAt, ref incomingObservations);
        return incomingObservations >= 2;
    }

    public bool ObserveSettlement(long now, bool exactInventoryDeltas)
    {
        if (!exactInventoryDeltas)
        {
            settlementObservedAt = null;
            settlementObservations = 0;
            return false;
        }
        CountDistinctObservation(now, ref settlementObservedAt, ref settlementObservations);
        return settlementObservations >= 2;
    }

    private void ResetIncoming()
    {
        incomingFingerprint = null;
        incomingObservedAt = null;
        incomingObservations = 0;
    }

    private static void CountDistinctObservation(long now, ref long? previous, ref int count)
    {
        // Repeated reads inside one update cannot replace an observation on another update.
        if (previous.HasValue && now <= previous.Value) return;
        previous = now;
        count = Math.Min(2, count + 1);
    }
}
