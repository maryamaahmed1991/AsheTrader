using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class InnArrivalGateTests
{
    [Fact]
    public void LongJourneyDoesNotConsumeTheGraceBeforeInnLoadingBegins()
    {
        var gate = new InnArrivalGate();
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(0, 129, true, true, false));
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(20_000, 130, true, true, false));
        // The travel queue can empty on an outdoor frame just before the zone change.
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(24_000, 130, true, false, false));
        Assert.Equal(0, gate.GraceElapsedMilliseconds(24_000));
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(24_100, 130, false, false, false));
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(30_000, 178, true, false, false));
        Assert.Equal(InnArrivalDecision.Arrived, gate.Observe(31_000, 178, true, false, true));
    }

    [Fact]
    public void TerritoryChangeRestartsGraceEvenWithoutAnObservedLoadingFrame()
    {
        var gate = new InnArrivalGate();
        gate.Observe(0, 130, true, false, false);
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(14_000, 130, true, false, false));
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(15_000, 178, true, false, false));
        Assert.Equal(178U, gate.ObservedTerritory);
        Assert.Equal(0, gate.GraceElapsedMilliseconds(15_000));
        Assert.Equal(InnArrivalDecision.Arrived, gate.Observe(16_000, 178, true, false, true));
    }

    [Fact]
    public void TravelBecomingBusyAgainRestartsTheIdleGrace()
    {
        var gate = new InnArrivalGate();
        gate.Observe(0, 130, true, false, false);
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(14_000, 130, true, true, false));
        Assert.False(gate.IsWaitingForArrival);
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(30_000, 130, true, false, false));
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(44_999, 130, true, false, false));
        Assert.Equal(InnArrivalDecision.TimedOut, gate.Observe(45_000, 130, true, false, false));
    }

    [Fact]
    public void NeverLoadedBellHasABoundedIdleTimeout()
    {
        var gate = new InnArrivalGate();
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(1000, 178, true, false, false));
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(15_999, 178, true, false, false));
        Assert.Equal(InnArrivalDecision.TimedOut, gate.Observe(16_000, 178, true, false, false));
        Assert.Equal(InnArrivalGate.GraceMilliseconds, gate.GraceElapsedMilliseconds(16_000));
    }

    [Theory]
    [InlineData(false, false, 178)]
    [InlineData(true, true, 178)]
    [InlineData(true, false, 0)]
    public void BellObservationCannotBypassReadinessOrHomeWorldOrTravel(bool readyAtHome, bool travelBusy, uint territory)
    {
        var gate = new InnArrivalGate();
        gate.Observe(0, 178, true, false, false);
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(20_000, territory, readyAtHome, travelBusy, true));
        Assert.False(gate.IsWaitingForArrival);
        Assert.Null(gate.ObservedTerritory);
    }

    [Fact]
    public void CutsceneResetProvidesFreshGraceWithoutSkippingAnything()
    {
        var gate = new InnArrivalGate();
        gate.Observe(0, 178, true, false, false);
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(14_000, 178, true, false, false));
        // The executor resets this gate before returning to wait through a cutscene.
        gate.Reset();
        Assert.Equal(InnArrivalDecision.Wait, gate.Observe(80_000, 178, true, false, false));
        Assert.Equal(0, gate.GraceElapsedMilliseconds(80_000));
        Assert.Equal(InnArrivalDecision.Arrived, gate.Observe(81_000, 178, true, false, true));
    }

    [Fact]
    public void BellAppearingAtTheGraceBoundaryIsAnObservedArrival()
    {
        var gate = new InnArrivalGate();
        gate.Observe(0, 178, true, false, false);
        Assert.Equal(InnArrivalDecision.Arrived, gate.Observe(15_000, 178, true, false, true));
    }
}
