namespace AsheTrader.Trading;

internal enum TradeOpeningDecision { Wait, Request, Exhausted }

/// <summary>Retries only an unacknowledged opening request, never an offer or completed batch.</summary>
internal sealed class TradeOpeningGate
{
    internal const int StableReadyMilliseconds = 500;
    internal const int RetryMilliseconds = 3000;
    internal const int MaximumRequests = 3;
    internal const float MeetingArrivalRadius = 1f;
    internal const float RequestDistance = 2.5f;
    private long? readySince;
    private long? lastRequestAt;
    public int Requests { get; private set; }
    public bool Acknowledged { get; private set; }

    public void Reset()
    {
        readySince = lastRequestAt = null;
        Requests = 0;
        Acknowledged = false;
    }

    public TradeOpeningDecision Observe(long now, bool ready, bool acknowledged)
    {
        if (acknowledged) Acknowledged = true;
        if (Acknowledged || !ready) { readySince = null; return TradeOpeningDecision.Wait; }
        readySince ??= now;
        if (now - readySince < StableReadyMilliseconds
            || lastRequestAt.HasValue && now - lastRequestAt.Value < RetryMilliseconds)
            return TradeOpeningDecision.Wait;
        return Requests >= MaximumRequests ? TradeOpeningDecision.Exhausted : TradeOpeningDecision.Request;
    }

    public void RecordRequest(long now)
    {
        if (Observe(now, true, false) != TradeOpeningDecision.Request)
            throw new InvalidOperationException("A trade opening request is not ready.");
        Requests++;
        lastRequestAt = now;
        readySince = null;
    }
}
