using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class TeleportRequestGateTests
{
    private static TeleportRequestDecision Observe(TeleportRequestGate gate, long now, bool ready = true,
        bool arrived = false, bool travelling = false, uint territory = 177)
        => gate.Observe(now, 409, territory, ready, arrived, travelling);

    [Fact]
    public void LoginObjectAvailabilityDoesNotDispatchUntilTeleportReadinessStabilizes()
    {
        var gate = new TeleportRequestGate();
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 0, ready: false));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 177, ready: false));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 2000));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 2499));
        Assert.Equal(TeleportRequestDecision.Request, Observe(gate, 2500));
        Assert.Equal(0, gate.Attempts);
    }

    [Fact]
    public void TransientAnimationLockOrMovementRestartsTheReadinessWindow()
    {
        var gate = new TeleportRequestGate();
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 0));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 499, ready: false));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 600));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 1099));
        Assert.Equal(TeleportRequestDecision.Request, Observe(gate, 1100));
    }

    [Fact]
    public void RejectedRequestCanRetryOnlyAfterFreshReadinessAndRetryInterval()
    {
        var gate = new TeleportRequestGate();
        Observe(gate, 0);
        Assert.Equal(TeleportRequestDecision.Request, Observe(gate, 500));
        gate.RecordRequest(500, false);
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 600));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 1100));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 1499));
        Assert.Equal(TeleportRequestDecision.Request, Observe(gate, 1500));
        Assert.Equal(1, gate.Attempts);
    }

    [Fact]
    public void AcceptedRequestNeverDispatchesAgainEvenIfNoCastStarts()
    {
        var gate = new TeleportRequestGate();
        Observe(gate, 0);
        gate.RecordRequest(500, true);
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 600));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 15_599));
        Assert.Equal(TeleportRequestDecision.ArrivalTimedOut, Observe(gate, 15_600));
        Assert.Throws<InvalidOperationException>(() => gate.RecordRequest(16_000, true));
        Assert.Equal(1, gate.Attempts);
    }

    [Fact]
    public void CastingAndLoadingSuspendIdleArrivalFailureUntilTheDestinationIsReady()
    {
        var gate = new TeleportRequestGate();
        Observe(gate, 0);
        gate.RecordRequest(500, true);
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 1000));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 5000, ready: false, travelling: true));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 30_000, ready: false, travelling: true));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 40_000, territory: 129));
        Assert.Equal(TeleportRequestDecision.Arrived, Observe(gate, 41_000, arrived: true, territory: 129));
    }

    [Fact]
    public void MissingUnlockedDestinationOrUnavailableActionTimesOutWithoutDispatch()
    {
        var gate = new TeleportRequestGate();
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 0, ready: false));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 29_999, ready: false));
        Assert.Equal(TeleportRequestDecision.ReadinessTimedOut, Observe(gate, 30_000, ready: false));
        Assert.Equal(0, gate.Attempts);
    }

    [Fact]
    public void AcceptedTeleportWithAnUnusableActionCannotWaitForeverWhileIdle()
    {
        var gate = new TeleportRequestGate();
        Observe(gate, 0);
        gate.RecordRequest(500, true);
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 1000, ready: false));
        Assert.Equal(TeleportRequestDecision.ArrivalTimedOut, Observe(gate, 16_000, ready: false));
    }

    [Fact]
    public void RepeatedPreDispatchRefusalsAreBounded()
    {
        var gate = new TeleportRequestGate();
        Observe(gate, 0);
        for (var attempt = 0; attempt < TeleportRequestGate.MaximumRejectedRequests; attempt++)
            gate.RecordRequest(500 + attempt * 1000, false);
        Assert.Equal(TeleportRequestDecision.ReadinessTimedOut, Observe(gate, 6000));
        Assert.False(gate.Accepted);
    }

    [Fact]
    public void CutsceneWaitExcludesItsDurationAndRequiresFreshReadiness()
    {
        var gate = new TeleportRequestGate();
        Observe(gate, 0);
        gate.Suspend(60_000);
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 60_500));
        Assert.Equal(TeleportRequestDecision.Request, Observe(gate, 61_000));
        gate.RecordRequest(61_000, true);
        Observe(gate, 61_500);
        gate.Suspend(60_000);
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 122_000));
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 136_999));
        Assert.Equal(TeleportRequestDecision.ArrivalTimedOut, Observe(gate, 137_000));
    }

    [Fact]
    public void NewTerritoryAndNewOperationCannotReusePreviousReadiness()
    {
        var gate = new TeleportRequestGate();
        Observe(gate, 0);
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 500, territory: 129));
        Assert.Equal(TeleportRequestDecision.Request, Observe(gate, 1000, territory: 129));
        gate.RecordRequest(1000, true);
        gate.Reset();
        Assert.False(gate.Accepted);
        Assert.Equal(0, gate.Attempts);
        Assert.Equal(TeleportRequestDecision.Wait, Observe(gate, 50_000));
        Assert.Equal(TeleportRequestDecision.Request, Observe(gate, 50_500));
    }
}
