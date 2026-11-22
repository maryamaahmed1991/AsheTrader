using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class SaddlebagOpeningGateTests
{
    [Fact]
    public void LostRequestBeforeInnCutsceneRetriesAfterFreshReadiness()
    {
        var gate = new SaddlebagOpeningGate();
        Assert.Equal(SaddlebagOpeningDecision.Wait, gate.Observe(0, true, false, false));
        Assert.Equal(SaddlebagOpeningDecision.Open, gate.Observe(500, true, false, false));
        gate.RecordAttempt(500);
        gate.Interrupt(); // Executor does not run gameplay updates during the cutscene.
        Assert.Equal(SaddlebagOpeningDecision.Wait, gate.Observe(11000, true, false, false));
        Assert.Equal(SaddlebagOpeningDecision.Open, gate.Observe(11500, true, false, false));
        gate.RecordAttempt(11500);
        Assert.Equal(SaddlebagOpeningDecision.Ready, gate.Observe(11800, true, true, true));
        Assert.Equal(2, gate.Attempts);
    }

    [Fact]
    public void IgnoredOpenIsRetriedWithoutManualResumeButNeverSpamToggled()
    {
        var gate = new SaddlebagOpeningGate();
        gate.Observe(0, true, false, false);
        gate.RecordAttempt(500);
        for (var now = 800; now < 2500; now += 100)
            Assert.Equal(SaddlebagOpeningDecision.Wait, gate.Observe(now, true, false, false));
        Assert.Equal(SaddlebagOpeningDecision.Open, gate.Observe(2500, true, false, false));
    }

    [Fact]
    public void VisibleLoadingWindowMustNotBeToggled()
    {
        var gate = new SaddlebagOpeningGate();
        gate.RecordAttempt(500);
        for (var now = 1000; now < 10000; now += 500)
            Assert.Equal(SaddlebagOpeningDecision.Wait, gate.Observe(now, true, true, false));
        Assert.Equal(SaddlebagOpeningDecision.Ready, gate.Observe(10000, true, true, true));
        Assert.Equal(1, gate.Attempts);
    }

    [Fact]
    public void ExistingReadyWindowNeedsNoNewOpenOrDelay()
    {
        var gate = new SaddlebagOpeningGate();
        Assert.Equal(SaddlebagOpeningDecision.Ready, gate.Observe(0, true, true, true));
        Assert.Equal(0, gate.Attempts);
    }

    [Fact]
    public void LoadingOrAnimationLockRestartsStableReadiness()
    {
        var gate = new SaddlebagOpeningGate();
        gate.Observe(0, true, false, false);
        Assert.Equal(SaddlebagOpeningDecision.Wait, gate.Observe(400, false, false, false));
        Assert.Equal(SaddlebagOpeningDecision.Wait, gate.Observe(500, true, false, false));
        Assert.Equal(SaddlebagOpeningDecision.Wait, gate.Observe(999, true, false, false));
        Assert.Equal(SaddlebagOpeningDecision.Open, gate.Observe(1000, true, false, false));
    }

    [Fact]
    public void PersistentFailureIsBoundedAndLateAcknowledgementStillAccepted()
    {
        var gate = new SaddlebagOpeningGate();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var now = attempt * 3000;
            gate.Observe(now, true, false, false);
            Assert.Equal(SaddlebagOpeningDecision.Open, gate.Observe(now + 500, true, false, false));
            gate.RecordAttempt(now + 500);
        }
        Assert.Equal(SaddlebagOpeningDecision.Wait, gate.Observe(15000, true, false, false));
        Assert.Equal(SaddlebagOpeningDecision.Exhausted, gate.Observe(15500, true, false, false));
        Assert.Equal(SaddlebagOpeningDecision.Ready, gate.Observe(15500, true, true, true));
        gate.Reset();
        Assert.Equal(0, gate.Attempts);
        Assert.Equal(SaddlebagOpeningDecision.Wait, gate.Observe(16000, true, false, false));
        Assert.Equal(SaddlebagOpeningDecision.Open, gate.Observe(16500, true, false, false));
    }
}
