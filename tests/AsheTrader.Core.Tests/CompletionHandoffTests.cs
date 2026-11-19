namespace AsheTrader.Core.Tests;

public sealed class CompletionHandoffTests
{
    [Fact]
    public void WaitsForIdleAndEnablesExactlyOnce()
    {
        var handoff = new CompletionHandoff(); var calls = 0;
        handoff.Request(100, "Fuel");
        handoff.Tick(200, false, false, () => { calls++; return null; });
        Assert.True(handoff.Pending); Assert.Equal(0, calls);
        handoff.Tick(300, false, true, () => { calls++; return null; });
        handoff.Tick(400, false, true, () => { calls++; return null; });
        Assert.Equal(1, calls); Assert.False(handoff.Pending);
        Assert.Contains("enabled", handoff.Status);
    }
    [Theory]
    [InlineData(true, false, 200)]
    [InlineData(false, true, 200)]
    [InlineData(false, false, 60_100)]
    public void NewJobCancellationAndTimeoutPreventEnabling(bool otherJob, bool cancelled, long now)
    {
        var handoff = new CompletionHandoff();
        handoff.Request(100, "Utilities");
        if (cancelled) handoff.Cancel("user stopped");
        handoff.Tick(now, otherJob, true, () => throw new Xunit.Sdk.XunitException("Must not enable"));
        Assert.False(handoff.Pending); Assert.Contains("cancelled", handoff.Status);
    }
    [Fact]
    public void IpcFailureIsReportedAndNeverRetried()
    {
        var handoff = new CompletionHandoff(); var calls = 0;
        handoff.Request(0, "Transfer");
        handoff.Tick(1, false, true, () => { calls++; throw new IOException("Unavailable"); });
        handoff.Tick(2, false, true, () => { calls++; return null; });
        Assert.Equal(1, calls); Assert.Contains("failed", handoff.Status);
        Assert.False(new CompletionHandoff().Pending);
    }
}
