namespace AsheTrader.Core.Networking;

public enum TrustedRunIntent { None, StartQueued, ReconcileInterrupted }

/// <summary>Session-only intent. Loading a durable job must never arm automatic game work.</summary>
public sealed class TrustedRunGate
{
    public TrustedRunIntent Intent { get; private set; }
    public bool Armed => Intent != TrustedRunIntent.None;
    public void Queue() => Intent = TrustedRunIntent.StartQueued;
    public void Resume() => Intent = TrustedRunIntent.ReconcileInterrupted;
    public void Cancel() => Intent = TrustedRunIntent.None;
    public void RecoverConnection(bool wasRunning, bool enabled)
    {
        if (wasRunning && enabled) Resume();
    }
    public bool CanProceed(bool authenticated, bool remoteAllowsControl, bool localReady, bool remoteReady) =>
        Armed && authenticated && remoteAllowsControl && localReady && remoteReady;
}
