using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelPurchaseRetryGateTests
{
    [Fact]
    public void UnansweredClickAloneNeverAuthorizesARepeat()
    {
        var gate = new FuelPurchaseRetryGate(0);
        Assert.False(Ready(gate, 0));
        Assert.False(Ready(gate, 30_000));
        Assert.Equal(1, gate.Attempts);
    }

    [Fact]
    public void RejectedClickNeedsTwoSecondsOfObservedUnchangedReadiness()
    {
        var gate = new FuelPurchaseRetryGate(0);
        Assert.True(gate.ReportInventoryRejection(100));
        Assert.False(Ready(gate, 100));
        Assert.False(Ready(gate, 2_099));
        Assert.True(Ready(gate, 2_100));
        // Merely asking does not consume an attempt or clear the accepted failure.
        Assert.True(Ready(gate, 2_100));
        Assert.Equal(1, gate.Attempts);
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1_000, true)]
    [InlineData(6_000, true)]
    [InlineData(6_001, false)]
    public void ErrorMustBelongToTheCurrentDispatchWindow(long errorAt, bool accepted)
    {
        var gate = new FuelPurchaseRetryGate(1_000);
        Assert.Equal(accepted, gate.ReportInventoryRejection(errorAt));
        Assert.False(Ready(gate, 6_001));
        Assert.Equal(accepted, Ready(gate, 8_001));
    }

    [Fact]
    public void DuplicateFailureNotificationsDoNotRestartStabilityOrResetBudget()
    {
        var gate = new FuelPurchaseRetryGate(0);
        Assert.True(gate.ReportInventoryRejection(100));
        Assert.False(Ready(gate, 100));
        Assert.True(gate.ReportInventoryRejection(2_000));
        Assert.True(Ready(gate, 2_100));
        gate.MarkDispatched(2_100);
        Assert.Equal(2, gate.Attempts);
        Assert.Equal(2_100, gate.DispatchedAt);
        Assert.False(gate.ReportInventoryRejection(2_000));
        Assert.True(gate.ReportInventoryRejection(2_200));
        Assert.False(Ready(gate, 2_200));
        Assert.True(gate.ReportInventoryRejection(4_200));
        Assert.False(Ready(gate, 6_099));
        Assert.True(Ready(gate, 6_100));
        Assert.Equal(2, gate.Attempts);
    }

    [Fact]
    public void AtMostTwoRepeatsUseIncreasingDispatchBackoff()
    {
        var gate = new FuelPurchaseRetryGate(0);
        Assert.True(gate.ReportInventoryRejection(0));
        Assert.False(Ready(gate, 0));
        Assert.True(Ready(gate, 2_000));
        gate.MarkDispatched(2_000);
        Assert.True(gate.ReportInventoryRejection(2_050));
        Assert.False(Ready(gate, 2_050));
        Assert.False(Ready(gate, 4_050)); // Stable, but the second click's four-second backoff is not over.
        Assert.False(Ready(gate, 5_999));
        Assert.True(Ready(gate, 6_000));
        gate.MarkDispatched(6_000);
        Assert.True(gate.ReportInventoryRejection(6_050));
        Assert.False(Ready(gate, 6_050));
        Assert.False(Ready(gate, 60_000));
        Assert.Equal(3, gate.Attempts);
    }

    [Fact]
    public void EveryRepeatRequiresAFreshRejectionAndStableObservation()
    {
        var gate = new FuelPurchaseRetryGate(0);
        Assert.True(gate.ReportInventoryRejection(0));
        Assert.False(Ready(gate, 0));
        Assert.True(Ready(gate, 2_000));
        gate.MarkDispatched(2_000);
        Assert.False(Ready(gate, 6_000));
        Assert.True(gate.ReportInventoryRejection(6_000));
        Assert.False(Ready(gate, 6_000));
        Assert.False(Ready(gate, 7_999));
        Assert.True(Ready(gate, 8_000));
    }

    [Fact]
    public void ReadinessLossRequiresANewContinuousStableInterval()
    {
        var gate = new FuelPurchaseRetryGate(0);
        Assert.True(gate.ReportInventoryRejection(0));
        Assert.False(Ready(gate, 0));
        Assert.False(gate.ShouldRetry(1_000, false, true, false));
        Assert.False(Ready(gate, 2_000));
        Assert.False(Ready(gate, 3_999));
        Assert.True(Ready(gate, 4_000));
        Assert.False(gate.EvidenceChanged);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfirmationSeenBeforeOrAfterTheErrorPermanentlyPreventsRetry(bool beforeError)
    {
        var gate = new FuelPurchaseRetryGate(0);
        if (beforeError) gate.ObserveConfirmation();
        Assert.Equal(!beforeError, gate.ReportInventoryRejection(100));
        Assert.False(gate.ShouldRetry(100, true, true, true));
        Assert.False(Ready(gate, 30_000));
        Assert.False(gate.ReportInventoryRejection(200));
        Assert.True(gate.ConfirmationObserved);
    }

    [Fact]
    public void TransientInventoryOrCreditChangesCannotBecomeUnchangedAgain()
    {
        var gate = new FuelPurchaseRetryGate(0);
        Assert.True(gate.ReportInventoryRejection(0));
        Assert.False(Ready(gate, 0));
        Assert.False(gate.ShouldRetry(1_000, false, false, true));
        Assert.False(Ready(gate, 3_000));
        Assert.False(gate.ReportInventoryRejection(3_500));
        Assert.True(gate.EvidenceChanged);
    }

    [Fact]
    public void UnavailableUiDoesNotHideAConfirmationOrKnownEvidenceChange()
    {
        var gate = new FuelPurchaseRetryGate(0);
        Assert.False(gate.ShouldRetry(100, true, false, false));
        Assert.True(gate.ConfirmationObserved);
        Assert.True(gate.EvidenceChanged);
        gate.MarkDispatched(1_000);
        Assert.True(gate.ConfirmationObserved);
        Assert.True(gate.EvidenceChanged);
        Assert.False(gate.ReportInventoryRejection(1_000));
        Assert.False(Ready(gate, 30_000));
    }

    [Fact]
    public void ObservationBeforeDispatchCannotAccumulateStableTime()
    {
        var gate = new FuelPurchaseRetryGate(1_000);
        Assert.True(gate.ReportInventoryRejection(1_000));
        Assert.False(Ready(gate, 500));
        Assert.False(Ready(gate, 1_000));
        Assert.False(Ready(gate, 2_999));
        Assert.True(Ready(gate, 3_000));
    }

    private static bool Ready(FuelPurchaseRetryGate gate, long now)
        => gate.ShouldRetry(now, confirmationVisible: false, baselineUnchanged: true, ready: true);
}
