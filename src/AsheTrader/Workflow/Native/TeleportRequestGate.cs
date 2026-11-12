namespace AsheTrader.Workflow.Native;

internal enum TeleportRequestDecision { Wait, Request, Arrived, ReadinessTimedOut, ArrivalTimedOut }

/// <summary>Waits for action readiness after login and only retries calls explicitly rejected before dispatch.</summary>
internal sealed class TeleportRequestGate
{
    public const int StableMilliseconds = 500;
    public const int RetryMilliseconds = 1000;
    public const int ReadinessTimeoutMilliseconds = 30_000;
    public const int ArrivalIdleTimeoutMilliseconds = 15_000;
    public const int MaximumRejectedRequests = 5;
    private long? startedAt;
    private long? readySince;
    private long? lastRequestAt;
    private long? idleAfterRequestSince;
    private (uint World, uint Territory)? location;
    public bool Accepted { get; private set; }
    public int Attempts { get; private set; }

    public TeleportRequestDecision Observe(long now, uint world, uint territory, bool requestReady,
        bool arrived, bool travelInProgress)
    {
        startedAt ??= now;
        if (arrived) return TeleportRequestDecision.Arrived;
        if (location != (world, territory))
        {
            location = (world, territory);
            readySince = null;
            idleAfterRequestSince = null;
        }
        if (Accepted)
        {
            if (travelInProgress) idleAfterRequestSince = null;
            else idleAfterRequestSince ??= now;
            return idleAfterRequestSince.HasValue && now - idleAfterRequestSince.Value >= ArrivalIdleTimeoutMilliseconds
                ? TeleportRequestDecision.ArrivalTimedOut : TeleportRequestDecision.Wait;
        }
        if (now - startedAt.Value >= ReadinessTimeoutMilliseconds || Attempts >= MaximumRejectedRequests)
            return TeleportRequestDecision.ReadinessTimedOut;
        if (!requestReady || travelInProgress)
        {
            readySince = null;
            return TeleportRequestDecision.Wait;
        }
        readySince ??= now;
        return now - readySince.Value >= StableMilliseconds
            && (!lastRequestAt.HasValue || now - lastRequestAt.Value >= RetryMilliseconds)
                ? TeleportRequestDecision.Request : TeleportRequestDecision.Wait;
    }

    public void RecordRequest(long now, bool accepted)
    {
        if (Accepted) throw new InvalidOperationException("An accepted teleport must be observed, not dispatched again.");
        Attempts++;
        lastRequestAt = now;
        readySince = null;
        Accepted = accepted;
        idleAfterRequestSince = null;
    }

    public void Suspend(long elapsed)
    {
        if (elapsed < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (startedAt.HasValue) startedAt += elapsed;
        if (lastRequestAt.HasValue) lastRequestAt += elapsed;
        readySince = null;
        idleAfterRequestSince = null;
    }

    public void Reset()
    {
        startedAt = readySince = lastRequestAt = idleAfterRequestSince = null;
        location = null;
        Accepted = false;
        Attempts = 0;
    }
}
