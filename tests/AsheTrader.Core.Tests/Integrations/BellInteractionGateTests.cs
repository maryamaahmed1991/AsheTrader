using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class BellInteractionGateTests
{
    [Fact]
    public void LoginReadinessMustRemainStableBeforeOpeningBell()
    {
        var gate = new BellInteractionGate();
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(0, true, false, false));
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(38, true, false, false));
        Assert.Equal(BellInteractionDecision.Interact, gate.Observe(750, true, false, false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void BusyOrUnavailablePlayerResetsReadiness(bool ready, bool blocked)
    {
        var gate = new BellInteractionGate();
        gate.Observe(0, true, false, false);
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(750, ready, blocked, false));
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(1000, true, false, false));
        Assert.Equal(BellInteractionDecision.Interact, gate.Observe(1750, true, false, false));
    }

    [Fact]
    public void UnacknowledgedAttemptRetriesAfterFiveSeconds()
    {
        var gate = ReadyGate();
        gate.RecordAttempt(750);
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(5749, true, false, false));
        Assert.Equal(BellInteractionDecision.Interact, gate.Observe(5750, true, false, false));
    }

    [Fact]
    public void ThreeAttemptsAreTheHardLimit()
    {
        var gate = ReadyGate();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = 750 + attempt * 5000;
            Assert.Equal(BellInteractionDecision.Interact, gate.Observe(now, true, false, false));
            gate.RecordAttempt(now);
        }
        Assert.Equal(3, gate.Attempts);
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(15749, true, false, false));
        Assert.Equal(BellInteractionDecision.Exhausted, gate.Observe(15750, true, false, false));
        Assert.Throws<InvalidOperationException>(() => gate.RecordAttempt(15750));
    }

    [Fact]
    public void AcknowledgedBellNeverReopensEvenIfTheMenuLaterCloses()
    {
        var gate = ReadyGate();
        gate.RecordAttempt(750);
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(900, true, false, true));
        Assert.True(gate.Acknowledged);
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(10_000, true, false, false));
        Assert.Throws<InvalidOperationException>(() => gate.RecordAttempt(10_000));
    }

    [Fact]
    public void UnrelatedUiPreventsRetryAfterDeadline()
    {
        var gate = ReadyGate();
        gate.RecordAttempt(750);
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(10_000, true, true, false));
        Assert.Equal(1, gate.Attempts);
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(11_000, true, false, false));
        Assert.Equal(BellInteractionDecision.Interact, gate.Observe(11_750, true, false, false));
    }

    [Fact]
    public void NewOperationResetsAttemptsAndAcknowledgement()
    {
        var gate = ReadyGate();
        gate.RecordAttempt(750);
        gate.Observe(900, true, false, true);
        gate.Reset();
        Assert.Equal(0, gate.Attempts);
        Assert.False(gate.Acknowledged);
        Assert.Equal(BellInteractionDecision.Wait, gate.Observe(10_000, true, false, false));
        Assert.Equal(BellInteractionDecision.Interact, gate.Observe(10_750, true, false, false));
    }

    private static BellInteractionGate ReadyGate()
    {
        var gate = new BellInteractionGate();
        gate.Observe(0, true, false, false);
        return gate;
    }
}
