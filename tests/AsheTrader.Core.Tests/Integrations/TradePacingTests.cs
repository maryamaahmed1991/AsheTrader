using AsheTrader.Trading;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class TradePacingTests
{
    [Fact]
    public void ItemDispatchWaitsForObservationEvenAfterIntervalExpires()
    {
        var pacing = new TradePacing();
        Assert.True(pacing.CanOffer(0));
        pacing.RecordOffer(0);
        Assert.False(pacing.CanOffer(10_000));
        Assert.Throws<InvalidOperationException>(() => pacing.RecordOffer(10_000));
        pacing.ConfirmOfferedItem();
        Assert.True(pacing.CanOffer(10_000));
    }

    [Fact]
    public void FastObservationWaitsOnlyForTheOriginalDispatchInterval()
    {
        var pacing = new TradePacing();
        pacing.RecordOffer(1000);
        pacing.ConfirmOfferedItem();
        Assert.False(pacing.CanOffer(1249));
        Assert.True(pacing.CanOffer(1250));
        pacing.RecordOffer(1250);
        Assert.True(pacing.OfferOutstanding);
    }

    [Fact]
    public void SlowObservationDoesNotAddAnotherFixedDelay()
    {
        var pacing = new TradePacing();
        pacing.RecordOffer(1000);
        pacing.ConfirmOfferedItem();
        Assert.True(pacing.CanOffer(1700));
    }

    [Fact]
    public void FiveObservedOffersCanBeDispatchedOneSecondApartFromFirstToLast()
    {
        var pacing = new TradePacing();
        for (var slot = 0; slot < 5; slot++)
        {
            var now = slot * 250;
            Assert.True(pacing.CanOffer(now));
            pacing.RecordOffer(now);
            pacing.ConfirmOfferedItem();
        }
    }

    [Fact]
    public void NewBatchDoesNotEraseThePreviousOpeningRequestCooldown()
    {
        var pacing = new TradePacing();
        pacing.RecordRequest(1000);
        pacing.RecordOffer(1200);
        pacing.BeginBatch();
        Assert.False(pacing.OfferOutstanding);
        Assert.True(pacing.CanOffer(1500));
        Assert.False(pacing.CanRequest(3999));
        Assert.Throws<InvalidOperationException>(() => pacing.RecordRequest(3999));
        Assert.True(pacing.CanRequest(4000));
        pacing.RecordRequest(4000);
        Assert.False(pacing.CanRequest(6999));
    }

    [Fact]
    public void IncomingOfferRequiresTwoDistinctObservationsWithoutAFixedSleep()
    {
        var pacing = new TradePacing();
        Assert.False(pacing.ObserveIncoming(100, "10155:False:999", true));
        Assert.False(pacing.ObserveIncoming(100, "10155:False:999", true));
        Assert.True(pacing.ObserveIncoming(101, "10155:False:999", true));
    }

    [Fact]
    public void ChangedIncomingQuantityRequiresFreshObservations()
    {
        var pacing = new TradePacing();
        pacing.ObserveIncoming(100, "10155:False:999", true);
        Assert.False(pacing.ObserveIncoming(101, "10155:False:1", true));
        Assert.True(pacing.ObserveIncoming(102, "10155:False:1", true));
    }

    [Fact]
    public void UnlockOrPendingSynchronizationInvalidatesIncomingEvidence()
    {
        var pacing = new TradePacing();
        pacing.ObserveIncoming(100, "10155:False:999", true);
        Assert.True(pacing.ObserveIncoming(101, "10155:False:999", true));
        Assert.False(pacing.ObserveIncoming(102, "10155:False:999", false));
        Assert.False(pacing.ObserveIncoming(1000, "10155:False:999", true));
        Assert.True(pacing.ObserveIncoming(1001, "10155:False:999", true));
    }

    [Fact]
    public void EmptyIncomingOfferNeverBecomesStable()
    {
        var pacing = new TradePacing();
        Assert.False(pacing.ObserveIncoming(0, "", true));
        Assert.False(pacing.ObserveIncoming(1000, "", true));
    }

    [Fact]
    public void SettlementRequiresTwoExactDistinctObservationsAndResetsOnMismatch()
    {
        var pacing = new TradePacing();
        Assert.False(pacing.ObserveSettlement(100, true));
        Assert.False(pacing.ObserveSettlement(100, true));
        Assert.False(pacing.ObserveSettlement(101, false));
        Assert.False(pacing.ObserveSettlement(102, true));
        Assert.True(pacing.ObserveSettlement(103, true));
    }

    [Fact]
    public void NewBatchDiscardsPreviousIncomingAndSettlementObservations()
    {
        var pacing = new TradePacing();
        pacing.ObserveIncoming(100, "10155:False:999", true);
        pacing.ObserveSettlement(100, true);
        pacing.BeginBatch();
        Assert.False(pacing.ObserveIncoming(101, "10155:False:999", true));
        Assert.False(pacing.ObserveSettlement(101, true));
        Assert.True(pacing.ObserveIncoming(102, "10155:False:999", true));
        Assert.True(pacing.ObserveSettlement(102, true));
    }
}
