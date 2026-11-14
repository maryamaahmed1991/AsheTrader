namespace AsheTrader.Core;

/// <summary>
/// Allows a bounded repeat of a rejected shop click before any purchase confirmation.
/// The caller retains the original purchase checkpoint and verifies its identities and counts.
/// </summary>
public sealed class FuelPurchaseRetryGate(long dispatchedAt)
{
    private bool rejected;
    private long? stableSince;

    public int Attempts { get; private set; } = 1;
    public long DispatchedAt { get; private set; } = dispatchedAt;
    public bool ConfirmationObserved { get; private set; }
    public bool EvidenceChanged { get; private set; }

    public void ObserveConfirmation() => ConfirmationObserved = true;

    public bool ReportInventoryRejection(long occurredAt)
    {
        if (ConfirmationObserved || EvidenceChanged || occurredAt < DispatchedAt
            || occurredAt - DispatchedAt > 5_000) return false;
        // Duplicate notifications do not extend the deadline or reset the stable observation.
        rejected = true;
        return true;
    }

    /// <param name="baselineUnchanged">False means an observed delta, not an unavailable inventory read.</param>
    /// <param name="ready">All required observations and the original shop context are currently available.</param>
    public bool ShouldRetry(long now, bool confirmationVisible, bool baselineUnchanged, bool ready)
    {
        if (confirmationVisible) ObserveConfirmation();
        if (!baselineUnchanged) EvidenceChanged = true;
        if (ConfirmationObserved || EvidenceChanged || !ready || !rejected || now < DispatchedAt)
        {
            stableSince = null;
            return false;
        }

        stableSince ??= now;
        return Attempts < 3 && now - stableSince.Value >= 2_000
            && now - DispatchedAt >= Attempts * 2_000L;
    }

    /// <summary>Call once for an actual repeat dispatch; the first dispatch is counted by construction.</summary>
    public void MarkDispatched(long now)
    {
        Attempts = checked(Attempts + 1);
        DispatchedAt = now;
        rejected = false;
        stableSince = null;
    }
}
