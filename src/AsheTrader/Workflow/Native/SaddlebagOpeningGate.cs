namespace AsheTrader.Workflow.Native;

internal enum SaddlebagOpeningDecision { Wait, Open, Ready, Exhausted }

// Main command 77 toggles the window. Dispatch is not acknowledgement: only a
// visible, ready addon proves it opened. Never toggle a visible/loading window.
internal sealed class SaddlebagOpeningGate
{
    internal const int StableMilliseconds = 500;
    internal const int RetryMilliseconds = 2000;
    internal const int MaximumAttempts = 5;
    private long? readySince;
    private long? lastAttemptAt;
    public int Attempts { get; private set; }

    public SaddlebagOpeningDecision Observe(long now, bool canInteract, bool visible, bool ready)
    {
        if (!canInteract) { Interrupt(); return SaddlebagOpeningDecision.Wait; }
        if (ready) return SaddlebagOpeningDecision.Ready;
        if (visible) { Interrupt(); return SaddlebagOpeningDecision.Wait; }
        readySince ??= now;
        if (now - readySince.Value < StableMilliseconds
            || (lastAttemptAt.HasValue && now - lastAttemptAt.Value < RetryMilliseconds))
            return SaddlebagOpeningDecision.Wait;
        return Attempts >= MaximumAttempts ? SaddlebagOpeningDecision.Exhausted : SaddlebagOpeningDecision.Open;
    }

    public void RecordAttempt(long now)
    {
        Attempts++;
        lastAttemptAt = now;
        readySince = null;
    }

    // A cutscene or loading transition invalidates readiness, not retry accounting.
    public void Interrupt() => readySince = null;
    public void Reset() { Attempts = 0; readySince = lastAttemptAt = null; }
}
