using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class AutomaticRelayCoordinatorTests
{
    private static SimulatedCharacter Sim(ulong id, Guid peer, uint bags = 0, uint capacity = 999) => new()
    { Id = id, PeerId = peer, Bags = bags, BagLimit = capacity, Retainers = new() { [id + 1000] = 0 } };
    private static SavedTransferJob Saved(TransferJobDefinition definition) => new()
    { Definition = definition, Meeting = new() { WorldId = 10, TerritoryId = 20, AetheryteId = 30 } };

    [Fact]
    public void AutomaticallyUsesHighestAutoRetainerSlotCountInsteadOfLegacyOptIn()
    {
        var service = new WorkflowService(PeerA, PeerB,
            Sim(1, PeerA, 10), Sim(2, PeerA), Sim(3, PeerB), Sim(4, PeerB));
        service.SetCachedFreeSlots(3, 2); service.SetCachedFreeSlots(4, 90);
        var definition = Job([1], (2, 10));
        definition.RelayCharacters[PeerB] = 3; // Legacy preference is no longer required or obeyed for a new job.
        service.StartJob(Saved(definition));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(4UL, service.Run.AutomaticRelays[PeerB]);
        Assert.Equal(4UL, service.Run.CompletedItems[0].AutomaticRelays[PeerB]);
        Assert.Equal(new ulong[] { 4, 2 }, service.Trades.Select(trade => trade.ReceiverId));
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 3);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 4 && command.Operation == GameOperationKind.ScanRetainers);
        Assert.False(service.Run.Snapshots[4].RetainerRosterVerified);
        Assert.Empty(service.Run.Snapshots[4].Retainers);
        Assert.Equal(InventoryVerification.Verified, service.Run.Snapshots[4].Verification);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
    }

    [Fact]
    public void FullCachedTopCandidateFallsBackToNextAltAfterLiveInspection()
    {
        var full = Sim(3, PeerB, 10, 10);
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerA), full, Sim(4, PeerB));
        service.SetCachedFreeSlots(3, 90); service.SetCachedFreeSlots(4, 20);
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(4UL, service.Run.AutomaticRelays[PeerB]);
        Assert.Contains(3UL, service.Run.RejectedRelayCharacters);
        Assert.Contains(service.Trace, command => command.CharacterId == 3 && command.Operation == GameOperationKind.Inspect);
        Assert.Equal(10U, full.Bags);
        Assert.All(service.Trades, trade => Assert.NotEqual(3UL, trade.SenderId));
    }

    [Fact]
    public void AllFullRelaysCanBeRetriedAfterSpaceIsFreedAndUserResumes()
    {
        var relay = Sim(3, PeerB, 10, 10);
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerA), relay);
        service.SetCachedFreeSlots(3, 90);
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();
        Assert.True(service.Run!.Paused);
        Assert.Contains("No automatic relay", service.LastError);
        Assert.Empty(service.Trades);

        relay.Bags = 5;
        service.Resume();
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Equal(5U, relay.Bags);
        Assert.Equal(4, service.Trades.Count); // Two bounded relay loads, each forwarded before the next pickup.
    }

    [Fact]
    public void NoRelayIsLoggedInWhenExistingRecipientStockAlreadyMeetsTarget()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerA, 10), Sim(3, PeerB));
        service.SetCachedFreeSlots(3, null);
        var definition = Job([1], (2, 10)); definition.QuantityMode = JobQuantityMode.RestockToTotal;
        service.StartJob(Saved(definition));
        service.RunToTerminal();
        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Empty(service.Run.AutomaticRelays);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 3);
        Assert.Empty(service.Trades);
    }

    [Fact]
    public void InterruptedRelayInspectionRechecksCandidateWithoutStartingTrade()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerA), Sim(3, PeerB));
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunUntil(run => run.Step == CoordinatorStep.RelayInspect && run.Pending.Count == 1);
        Assert.Empty(service.Run!.AutomaticRelays);
        Assert.Empty(service.Trades);
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && run.Journal != null);
        Assert.Equal(2, service.Trace.Count(command => command.CharacterId == 3 && command.Operation == GameOperationKind.Inspect));
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(3UL, service.Run.AutomaticRelays[PeerB]);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
    }

    [Fact]
    public void IssuedRelayStaysPinnedAcrossRestartEvenWhenAnotherAltHasMoreCachedSpace()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerA), Sim(3, PeerB), Sim(4, PeerB));
        service.SetCachedFreeSlots(3, 80); service.SetCachedFreeSlots(4, 20);
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunUntil(run => run.Step == CoordinatorStep.SelectLeg && service.Trades.Count == 1);
        Assert.Equal(10UL, new TransferJobLedger(service.Run!.Journal!).RelayCargo(3));
        service.SetCachedFreeSlots(3, 0); service.SetCachedFreeSlots(4, 140);
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(3UL, service.Run.AutomaticRelays[PeerB]);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 4);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Equal(0UL, new TransferJobLedger(service.Run.Journal!).RelayCargo(3));
    }

    [Fact]
    public void PrePlanRelaySelectionIsLiveCheckedAgainAfterRestart()
    {
        var firstCandidate = Sim(3, PeerB, capacity: 10);
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerA), firstCandidate, Sim(4, PeerB));
        service.SetCachedFreeSlots(3, 90); service.SetCachedFreeSlots(4, 20);
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunUntil(run => run.Step == CoordinatorStep.Plan && run.AutomaticRelays.Count == 1 && run.Journal == null);
        Assert.Equal(3UL, service.Run!.AutomaticRelays[PeerB]);
        firstCandidate.Bags = 10;
        service.SimulateCoordinatorRestart(); service.Resume();
        Assert.Empty(service.Run.AutomaticRelays);
        service.RunToTerminal();
        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(4UL, service.Run.AutomaticRelays[PeerB]);
        Assert.Equal(10U, firstCandidate.Bags);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
    }
}
