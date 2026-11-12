using AsheTrader.Trading;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class TradeOpeningGateTests
{
    [Fact]
    public void TargetAndGameMustRemainReadyAcrossUpdatesBeforeRequest()
    {
        var gate = new TradeOpeningGate();
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(0, false, false));
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(100, true, false));
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(599, true, false));
        Assert.Equal(TradeOpeningDecision.Request, gate.Observe(600, true, false));
    }

    [Fact]
    public void LostReadinessRestartsStableTargetDelay()
    {
        var gate = new TradeOpeningGate();
        gate.Observe(0, true, false);
        gate.Observe(450, false, false);
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(500, true, false));
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(999, true, false));
        Assert.Equal(TradeOpeningDecision.Request, gate.Observe(1000, true, false));
    }

    [Fact]
    public void OnlyThreeUnacknowledgedRequestsAreAllowedWithSpacing()
    {
        var gate = new TradeOpeningGate();
        gate.Observe(0, true, false);
        gate.RecordRequest(500);
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(501, true, false));
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(3499, true, false));
        gate.RecordRequest(3500);
        gate.Observe(3501, true, false);
        gate.RecordRequest(6500);
        gate.Observe(6501, true, false);
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(9499, true, false));
        Assert.Equal(TradeOpeningDecision.Exhausted, gate.Observe(9500, true, false));
        Assert.Equal(3, gate.Requests);
        Assert.Throws<InvalidOperationException>(() => gate.RecordRequest(10000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyAcknowledgementPermanentlyStopsRetriesEvenIfWindowDisappears(bool ready)
    {
        var gate = new TradeOpeningGate();
        gate.Observe(0, true, false);
        gate.RecordRequest(500);
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(501, ready, true));
        Assert.True(gate.Acknowledged);
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(30000, true, false));
        Assert.Throws<InvalidOperationException>(() => gate.RecordRequest(30000));
        Assert.Equal(1, gate.Requests);
    }

    [Fact]
    public void ExistingTradePreventsEvenFirstRequest()
    {
        var gate = new TradeOpeningGate();
        gate.Observe(0, true, true);
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(10000, true, false));
        Assert.Equal(0, gate.Requests);
    }

    [Fact]
    public void ResetAllowsANewExplicitBatchWithoutReusingReadiness()
    {
        var gate = new TradeOpeningGate();
        gate.Observe(0, true, true);
        gate.Reset();
        Assert.False(gate.Acknowledged);
        Assert.Equal(TradeOpeningDecision.Wait, gate.Observe(5000, true, false));
        Assert.Equal(TradeOpeningDecision.Request, gate.Observe(5500, true, false));
    }

    [Fact]
    public void TwoArrivalsWithinMeetingRadiusStayInsideRequestDistance()
    {
        var first = new System.Numerics.Vector3(TradeOpeningGate.MeetingArrivalRadius, 0, 0);
        var second = -first;
        Assert.True(System.Numerics.Vector3.Distance(first, second) <= TradeOpeningGate.RequestDistance);
        // The old 3-unit tolerance could leave the two clients six units apart.
        Assert.True(System.Numerics.Vector3.Distance(first * 3, second * 3) > TradeOpeningGate.RequestDistance);
    }
}
