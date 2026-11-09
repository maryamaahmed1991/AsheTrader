namespace AsheTrader.Workflow.Native;

internal enum BellInteractionDecision { Wait, Interact, Exhausted }

/// <summary>Only retries opening the bell before an interaction has been acknowledged.</summary>
internal sealed class BellInteractionGate
{
    internal const int MaximumAttempts = 3;
    internal const int ReadyDelayMs = 750;
    internal const int RetryDelayMs = 5000;
    private long? readySince;
    private long lastAttemptAt;

    public int Attempts { get; private set; }
    public bool Acknowledged { get; private set; }

    public void Reset()
    {
        Attempts = 0;
        Acknowledged = false;
        readySince = null;
        lastAttemptAt = 0;
    }

    public BellInteractionDecision Observe(long now, bool playerReady, bool interactionBlocked, bool bellOccupied)
    {
        if (bellOccupied) Acknowledged = true;
        if (Acknowledged || !playerReady || interactionBlocked)
        {
            readySince = null;
            return BellInteractionDecision.Wait;
        }
        readySince ??= now;
        if (now - readySince.Value < ReadyDelayMs || (Attempts != 0 && now - lastAttemptAt < RetryDelayMs))
            return BellInteractionDecision.Wait;
        return Attempts >= MaximumAttempts ? BellInteractionDecision.Exhausted : BellInteractionDecision.Interact;
    }

    public void RecordAttempt(long now)
    {
        if (Acknowledged || Attempts >= MaximumAttempts)
            throw new InvalidOperationException("The summoning-bell interaction cannot be repeated.");
        Attempts++;
        lastAttemptAt = now;
    }
}
