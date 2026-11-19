namespace AsheTrader.Core;

/// <summary>A session-scoped, one-shot handoff. Never restarts automation on plugin load.</summary>
public sealed class CompletionHandoff
{
    private long? requestedAt;
    public bool Pending => requestedAt.HasValue;
    public string Status { get; private set; } = "";
    public void Request(long now, string task)
    {
        requestedAt = now;
        Status = $"{task} finished. Waiting to enable AutoRetainer Multi Mode…";
    }
    public void Cancel(string reason)
    {
        if (!Pending) return;
        requestedAt = null;
        Status = "AutoRetainer handoff cancelled: " + reason;
    }
    public void Tick(long now, bool otherJob, bool ready, Func<string?> enable)
    {
        if (requestedAt is not { } start) return;
        if (otherJob) { Cancel("another job is active or paused."); return; }
        if (now - start >= 60_000) { Cancel("the game or a required plugin did not become idle within 60 seconds."); return; }
        if (!ready) return;
        // Clear before invoking IPC: even a throwing callback must never repeatedly toggle AR.
        requestedAt = null;
        try
        {
            var error = enable();
            Status = error == null ? "AutoRetainer Multi Mode enabled after job completion."
                : "AutoRetainer handoff failed: " + error;
        }
        catch (Exception ex) { Status = "AutoRetainer handoff failed: " + ex.Message; }
    }
}
