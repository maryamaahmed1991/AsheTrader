namespace AsheTrader.Workflow.Native;

internal enum InnArrivalDecision { Wait, Arrived, TimedOut }

/// <summary>
/// Lifestream becoming idle may precede the inn's territory and object loading.
/// Only a continuously ready, idle observation in one territory consumes arrival grace.
/// The executor separately bounds the whole journey and owns any gameplay actions.
/// </summary>
internal sealed class InnArrivalGate
{
    internal const int GraceMilliseconds = 15_000;
    private long? readyIdleSince;
    public uint? ObservedTerritory { get; private set; }
    public bool IsWaitingForArrival => readyIdleSince.HasValue;

    public void Reset()
    {
        readyIdleSince = null;
        ObservedTerritory = null;
    }

    public InnArrivalDecision Observe(long now, uint territory, bool playerReadyAtHome, bool travelBusy, bool bellAvailable)
    {
        if (!playerReadyAtHome || travelBusy || territory == 0)
        {
            Reset();
            return InnArrivalDecision.Wait;
        }
        if (ObservedTerritory != territory)
        {
            readyIdleSince = null;
            ObservedTerritory = territory;
        }
        readyIdleSince ??= now;
        if (bellAvailable) return InnArrivalDecision.Arrived;
        return GraceElapsedMilliseconds(now) >= GraceMilliseconds
            ? InnArrivalDecision.TimedOut : InnArrivalDecision.Wait;
    }

    public long GraceElapsedMilliseconds(long now) => readyIdleSince is { } since ? Math.Max(0, now - since) : 0;
}
