using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using AsheTrader.Workflow.Native;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class CachedDiscoveryCoordinatorTests
{
    private static SimulatedCharacter Sim(ulong id, Guid peer, uint bags = 0, uint stored = 0, bool cached = true) => new()
    {
        Id = id, PeerId = peer, Bags = bags, CacheAvailable = cached,
        Retainers = new() { [id + 1000] = stored },
    };

    private static SavedTransferJob Saved(TransferJobDefinition definition) => new()
    {
        Definition = definition,
        Meeting = new() { WorldId = 10, TerritoryId = 20, AetheryteId = 30 },
    };

    [Fact]
    public void CompleteCacheDiscoveryDoesNotLogInOrOpenRetainers()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 2, 18), Sim(2, PeerB));
        service.ScanJob(Saved(Job([1], (2, null))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.All(service.Trace, trace => Assert.Equal(GameOperationKind.ReadCachedInventory, trace.Operation));
        Assert.Equal(20UL, service.Run.Snapshots[1].TotalQuantity);
        Assert.All(service.Run.Snapshots.Values, snapshot =>
        {
            Assert.Equal(InventoryVerification.Cached, snapshot.Verification);
            Assert.True(snapshot.IsPlanningReady);
            Assert.False(snapshot.IsFullyVerified);
            Assert.False(snapshot.RetainerRosterVerified);
            Assert.Null(snapshot.ObservedAt);
            Assert.NotNull(snapshot.RetrievedAt);
        });
    }

    [Fact]
    public void CachedEmptyProviderIsNotLoggedInWhileUsefulProviderStillWithdrawsAndTrades()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA), Sim(2, PeerA, 3, 7), Sim(3, PeerB));
        service.StartJob(Saved(Job([1, 2], (3, 10))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.True(service.Run.Journal!.Plan.PlannedFromCache);
        Assert.DoesNotContain(service.Trace, trace => trace.Operation == GameOperationKind.ScanRetainers);
        Assert.DoesNotContain(service.Trace, trace => trace.CharacterId == 1 && trace.Operation != GameOperationKind.ReadCachedInventory);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal).DeliveredTo(3));
        Assert.Equal(7UL, service.Run.Withdrawn[2]);
    }

    [Fact]
    public void MissingCachePausesWithoutLoggingInOrOpeningRetainers()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10, cached: false), Sim(2, PeerB));
        service.ScanJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.All(service.Trace, trace => Assert.Equal(GameOperationKind.ReadCachedInventory, trace.Operation));
        Assert.Empty(service.Run.Snapshots);
        Assert.Null(service.Run.Journal);
        Assert.Contains("Character 1@Home World on this PC", service.LastError);
        Assert.Contains("then Resume", service.LastError);
        Assert.Contains("No complete cached", service.Run.DiscoveryNotes[1]);
    }

    [Fact]
    public void CachedSameAccountRouteStillChecksOnlySelectedRelaysLiveBags()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerA), Sim(3, PeerB));
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.DoesNotContain(service.Trace, trace => trace.Operation == GameOperationKind.ScanRetainers);
        Assert.Contains(service.Trace, trace => trace.CharacterId == 3 && trace.Operation == GameOperationKind.Inspect);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Equal(0UL, new TransferJobLedger(service.Run.Journal!).RelayCargo(3));
    }

    [Fact]
    public void EachItemQueriesItsOwnCacheAndPreservesEarlierDelivery()
    {
        var source = Sim(1, PeerA, 10);
        source.Inventory(999).Bags = 7;
        var recipient = Sim(2, PeerB);
        var saved = Saved(Job([1], (2, 10)));
        var second = Job([1], (2, 7));
        second.ItemId = 999;
        saved.AdditionalItems.Add(second);
        var service = new WorkflowService(PeerA, PeerB, source, recipient);
        service.StartJob(saved);
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.DoesNotContain(service.Trace, trace => trace.Operation == GameOperationKind.ScanRetainers);
        Assert.Equal(new uint[] { 10155, 999 }, service.Trace.Where(trace => trace.Operation == GameOperationKind.ReadCachedInventory)
            .Select(trace => trace.ItemId).Distinct());
        Assert.Equal(10UL, recipient.Total);
        Assert.Equal(7UL, recipient.Inventory(999).Total);
        Assert.Equal(2, service.Run.CompletedItems.Count);
    }

    [Fact]
    public void IncompleteRetainerCachePausesInsteadOfScanningOrTreatingMissingItemsAsZero()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, stored: 15), Sim(2, PeerB));
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ReadCachedInventory && command.Operation.CharacterId == 1)
            {
                result.RetainerRosterKnown = false;
                result.Retainers.Clear();
            }
            return result;
        };
        service.StartJob(Saved(Job([1], (2, 15))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.All(service.Trace, trace => Assert.Equal(GameOperationKind.ReadCachedInventory, trace.Operation));
        Assert.Null(service.Run.Journal);
        Assert.Empty(service.Trades);
        Assert.Equal(15UL, service.Characters[1].Total);
    }

    [Fact]
    public void StaleSourceOverestimateNeverReportsUnmovedItemsAsDelivered()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 2, 3), Sim(2, PeerB));
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ReadCachedInventory && command.Operation.CharacterId == 1)
                result.Retainers = [new(1001, "Retainer", 30, false)];
            return result;
        };
        service.StartJob(Saved(Job([1], (2, 20))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Equal(5UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Equal(5UL, service.Characters[2].Total);
        Assert.Contains("planned stock may be stale", service.LastError);
    }

    [Fact]
    public void SourceRecipientOverlapKeepsAllExistingStockWithoutExtraLiveDiscovery()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 20), Sim(2, PeerB, 30, 40), Sim(3, PeerB));
        service.StartJob(Saved(Job([1, 2], (2, 5), (3, null))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(0UL, service.Run.Journal!.Plan.SourceBudgets[2]);
        Assert.DoesNotContain(service.Trades, trade => trade.SenderId == 2);
        Assert.DoesNotContain(service.Trace, trace => trace.Operation == GameOperationKind.ScanRetainers);
        Assert.Equal(75UL, service.Characters[2].Total);
    }

    [Fact]
    public void RestockUsesCachedRecipientTotalWithoutOpeningRetainers()
    {
        var definition = Job([1], (2, 20));
        definition.QuantityMode = JobQuantityMode.RestockToTotal;
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 20), Sim(2, PeerB, 5, 5));
        service.StartJob(Saved(definition));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.DoesNotContain(service.Trace, trace => trace.Operation == GameOperationKind.ScanRetainers);
        Assert.True(service.Run.Journal!.Plan.PlannedFromCache);
        Assert.Equal(20UL, service.Characters[2].Total);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
        Assert.Contains("no extra login scan", service.Run.DiscoveryNotes[2]);
    }

    [Fact]
    public void ExactRestockPreviewCanUseCacheWithoutLoggingIn()
    {
        var definition = Job([1], (2, 20));
        definition.QuantityMode = JobQuantityMode.RestockToTotal;
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 20), Sim(2, PeerB, 5, 5));
        service.ScanJob(Saved(definition));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.All(service.Trace, trace => Assert.Equal(GameOperationKind.ReadCachedInventory, trace.Operation));
    }

    [Fact]
    public void ResumeAfterCacheRefreshReadsCacheAgainWithoutRelogging()
    {
        var source = Sim(1, PeerA, 10, cached: false);
        var service = new WorkflowService(PeerA, PeerB, source, Sim(2, PeerB));
        service.ScanJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();
        Assert.True(service.Run!.Paused);
        var previousLogins = service.Trace.Count(trace => trace.Operation == GameOperationKind.Login);

        source.CacheAvailable = true;
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(previousLogins, service.Trace.Count(trace => trace.Operation == GameOperationKind.Login));
        Assert.Equal(InventoryVerification.Cached, service.Run.Snapshots[1].Verification);
    }

    [Fact]
    public void MissingRemoteCacheNamesThePairedPcAndDoesNotStartGameplay()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerB, cached: false));
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Contains("Character 2@Home World on the paired PC", service.LastError);
        Assert.All(service.Trace, trace => Assert.Equal(GameOperationKind.ReadCachedInventory, trace.Operation));
        Assert.Null(service.Run.Journal);
    }

    [Theory]
    [InlineData(CoordinatorStep.DiscoverLogin, GameOperationKind.Login)]
    [InlineData(CoordinatorStep.DiscoverScan, GameOperationKind.ScanRetainers)]
    public void SavedLegacyDiscoveryResumesFromCacheWithoutReissuingItsPendingCommand(CoordinatorStep legacyStep, GameOperationKind legacyOperation)
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 3, 7), Sim(2, PeerB));
        service.ScanJob(Saved(Job([1], (2, 10))));
        service.Run!.Step = legacyStep;
        service.Run.Pending.Add(new()
        {
            PeerId = PeerA,
            Command = new() { Id = Guid.NewGuid(), Kind = WorkerCommandKind.Game,
                Operation = new() { Kind = legacyOperation, CharacterId = 1, ItemId = 10155 } }
        });
        service.PauseForTest("Saved discovery from an older build");
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.All(service.Trace, trace => Assert.Equal(GameOperationKind.ReadCachedInventory, trace.Operation));
        Assert.Equal(10UL, service.Run.Snapshots[1].TotalQuantity);
    }

    [Fact]
    public void StaleRestockBaselineRemainsAnEstimateWithoutForcingAnotherDiscoveryScan()
    {
        var definition = Job([1], (2, 20));
        definition.QuantityMode = JobQuantityMode.RestockToTotal;
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 20), Sim(2, PeerB, 5, 5));
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ReadCachedInventory && command.Operation.CharacterId == 2)
                result.Retainers = [new(1002, "Retainer", 0, false)];
            return result;
        };
        service.StartJob(Saved(definition));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.True(service.Run.Journal!.Plan.PlannedFromCache);
        Assert.Equal(15UL, new TransferJobLedger(service.Run.Journal).DeliveredTo(2));
        Assert.Equal(25UL, service.Characters[2].Total);
        Assert.DoesNotContain(service.Trace, trace => trace.Operation == GameOperationKind.ScanRetainers);
        Assert.Contains(service.Trace, trace => trace.Operation == GameOperationKind.Inspect && trace.CharacterId == 2);
    }
}
