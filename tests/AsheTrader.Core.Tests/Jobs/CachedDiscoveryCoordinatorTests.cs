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
        Assert.Contains(1UL, service.Run.Snapshots.Keys);
        Assert.DoesNotContain(1UL, service.Run.SkippedCharacters.Keys);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal).DeliveredTo(3));
        Assert.Equal(7UL, service.Run.Withdrawn[2]);
    }

    [Fact]
    public void MissingCacheSkipsTheSourceWithoutLoggingInOrFabricatingEmptyInventory()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10, cached: false), Sim(2, PeerB));
        service.ScanJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(CoordinatorStep.Finished, service.Run.Step);
        Assert.All(service.Trace, trace => Assert.Equal(GameOperationKind.ReadCachedInventory, trace.Operation));
        Assert.DoesNotContain(1UL, service.Run.Snapshots.Keys);
        Assert.Contains(2UL, service.Run.Snapshots.Keys);
        Assert.Null(service.Run.Journal);
        Assert.Contains("Character 1@Home World on this PC", service.Run.SkippedCharacters[1]);
        Assert.True(service.Run.HasSkippedCharacters);
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
    public void IncompleteRetainerCacheSkipsTheItemInsteadOfScanningOrTreatingMissingItemsAsZero()
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

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(CoordinatorStep.Finished, service.Run.Step);
        Assert.All(service.Trace, trace => Assert.Equal(GameOperationKind.ReadCachedInventory, trace.Operation));
        Assert.Null(service.Run.Journal);
        Assert.DoesNotContain(1UL, service.Run.Snapshots.Keys);
        Assert.Contains(1UL, service.Run.SkippedCharacters.Keys);
        Assert.False(string.IsNullOrWhiteSpace(service.Run.ItemSkipReason));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPausedCacheDiscoveryResumesAndEitherSkipsOrUsesRefreshedCacheWithoutRelogging(bool refreshed)
    {
        var source = Sim(1, PeerA, 10, cached: false);
        var service = new WorkflowService(PeerA, PeerB, source, Sim(2, PeerB));
        service.ScanJob(Saved(Job([1], (2, 10))));
        service.RunUntil(run => run.Pending.Any(pending => pending.Command.Operation.Kind == GameOperationKind.ReadCachedInventory
            && pending.Command.Operation.CharacterId == 1 && pending.Response != null));
        service.PauseForTest("Allagan Tools cache unavailable. Saved pause from an older plugin.");
        var previousLogins = service.Trace.Count(trace => trace.Operation == GameOperationKind.Login);

        source.CacheAvailable = refreshed;
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(previousLogins, service.Trace.Count(trace => trace.Operation == GameOperationKind.Login));
        Assert.Equal(2, service.Trace.Count(trace => trace.Operation == GameOperationKind.ReadCachedInventory && trace.CharacterId == 1));
        if (refreshed)
        {
            Assert.Equal(InventoryVerification.Cached, service.Run.Snapshots[1].Verification);
            Assert.DoesNotContain(1UL, service.Run.SkippedCharacters.Keys);
        }
        else
        {
            Assert.Contains(1UL, service.Run.SkippedCharacters.Keys);
            Assert.DoesNotContain(1UL, service.Run.Snapshots.Keys);
        }
    }

    [Fact]
    public void MissingRemoteCacheNamesThePairedPcAndDoesNotStartGameplay()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerB, cached: false));
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Contains("Character 2@Home World on the paired PC", service.Run.SkippedCharacters[2]);
        Assert.All(service.Trace, trace => Assert.Equal(GameOperationKind.ReadCachedInventory, trace.Operation));
        Assert.Null(service.Run.Journal);
        Assert.DoesNotContain(2UL, service.Run.Snapshots.Keys);
        Assert.False(string.IsNullOrWhiteSpace(service.Run.ItemSkipReason));
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

    [Theory]
    [InlineData("missing")]
    [InlineData("incomplete retainers")]
    public void UnavailableSourceIsExcludedWhileKnownSourceWithdrawsAndCompletesTheTarget(string failure)
    {
        var missing = Sim(1, PeerA, 100, cached: failure != "missing");
        var service = new WorkflowService(PeerA, PeerB, missing, Sim(2, PeerA, 3, 7), Sim(3, PeerB));
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ReadCachedInventory && command.Operation.CharacterId == 1)
            {
                if (failure == "incomplete retainers") result.RetainerRosterKnown = false;
            }
            return result;
        };
        var template = Saved(Job([1, 2], (3, 10)));
        service.StartJob(template);
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.True(service.Run.HasSkippedCharacters);
        Assert.Contains(1UL, service.Run.SkippedCharacters.Keys);
        Assert.DoesNotContain(1UL, service.Run.Snapshots.Keys);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 1 && command.Operation != GameOperationKind.ReadCachedInventory);
        Assert.Equal(100UL, missing.Total);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(3));
        Assert.Equal(7UL, service.Run.Withdrawn[2]);
        Assert.Equal(new ulong[] { 2 }, service.Run.Journal!.Plan.Definition.Sources);
        Assert.Equal(new ulong[] { 2 }, service.Run.Journal.Plan.SourceBudgets.Keys);
        Assert.Equal(new ulong[] { 1, 2 }, template.Definition.Sources);
        Assert.Equal(new ulong[] { 1, 2 }, service.Run.Job.Definition.Sources);
    }

    [Fact]
    public void MissingRestockRecipientIsExcludedRatherThanTreatedAsZeroAndLaterPriorityIsPreserved()
    {
        var definition = Job([1, 2], (2, 100), (3, 20), (4, 10));
        definition.QuantityMode = JobQuantityMode.RestockToTotal;
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 20), Sim(2, PeerB, 99, cached: false),
            Sim(3, PeerB, 5, 5), Sim(4, PeerB));
        service.StartJob(Saved(definition));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.False(service.Run.HasShortages);
        Assert.True(service.Run.HasSkippedCharacters);
        Assert.Contains(2UL, service.Run.SkippedCharacters.Keys);
        Assert.DoesNotContain(2UL, service.Run.Snapshots.Keys);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 2 && command.Operation != GameOperationKind.ReadCachedInventory);
        Assert.Equal(99UL, service.Characters[2].Total);
        Assert.Equal(20UL, service.Characters[3].Total);
        Assert.Equal(10UL, service.Characters[4].Total);
        Assert.Equal(new ulong[] { 3, 4 }, service.Trades.Select(trade => trade.ReceiverId).Distinct());
        Assert.Equal(new ulong[] { 3, 4 }, service.Run.Journal!.Plan.Definition.Recipients.Select(rule => rule.CharacterId));
        Assert.Equal(new ulong[] { 1 }, service.Run.Journal.Plan.Definition.Sources);
        Assert.DoesNotContain(service.Run.Journal.Plan.UnmetTargets, target => target.CharacterId == 2);
        Assert.Equal(new ulong[] { 2, 3, 4 }, service.Run.Job.Definition.Recipients.Select(rule => rule.CharacterId));
    }

    [Fact]
    public void AllMissingInventoriesFinishWithDurableSkipNotAnEmptySuccessfulPlan()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10, cached: false), Sim(2, PeerB, cached: false));
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(CoordinatorStep.Finished, service.Run.Step);
        Assert.True(service.Run.HasSkippedCharacters);
        Assert.Equal(2, service.Run.SkippedCharacters.Count);
        Assert.Empty(service.Run.Snapshots);
        Assert.Null(service.Run.Journal);
        Assert.False(string.IsNullOrWhiteSpace(service.Run.ItemSkipReason));
        var completed = Assert.Single(service.Run.CompletedItems);
        Assert.Equal(service.Run.ItemSkipReason, completed.ItemSkipReason);
        Assert.Equal(2, completed.SkippedCharacters.Count);
        Assert.Null(completed.Journal);
        Assert.All(service.Trace, command => Assert.Equal(GameOperationKind.ReadCachedInventory, command.Operation));
        Assert.Empty(service.Trades);
        Assert.Equal(10UL, service.Characters[1].Total);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    public void SkippedMiddleItemPersistsAcrossRestartWhileLaterItemStillCompletes(ulong unavailableCharacter)
    {
        var source = Sim(1, PeerA, 10);
        source.Inventory(200).Bags = 6;
        source.Inventory(300).Bags = 8;
        var second = Job([1], (2, 6)); second.ItemId = 200;
        var third = Job([1], (2, 8)); third.ItemId = 300;
        var template = Saved(Job([1], (2, 10)));
        template.AdditionalItems = [second, third];
        var service = new WorkflowService(PeerA, PeerB, source, Sim(2, PeerB));
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ReadCachedInventory && command.Operation.ItemId == 200
                && command.Operation.CharacterId == unavailableCharacter)
            { result.CachedInventoryAvailable = false; result.Error = "No cached stock for the second item."; }
            return result;
        };
        service.StartJob(template);
        service.RunUntil(run => run.ActiveItemIndex == 2);
        Assert.Equal(2, service.Run!.CompletedItems.Count);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.CompletedItems[0].Journal!).DeliveredTo(2));
        Assert.Contains(unavailableCharacter, service.Run.CompletedItems[1].SkippedCharacters.Keys);
        Assert.False(string.IsNullOrWhiteSpace(service.Run.CompletedItems[1].ItemSkipReason));
        Assert.Null(service.Run.CompletedItems[1].Journal);
        var skipReason = service.Run.CompletedItems[1].SkippedCharacters[unavailableCharacter];
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Equal(3, service.Run.CompletedItems.Count);
        Assert.True(service.Run.HasSkippedCharacters);
        Assert.Empty(service.Run.SkippedCharacters);
        Assert.Equal(skipReason, service.Run.CompletedItems[1].SkippedCharacters[unavailableCharacter]);
        Assert.Equal(8UL, new TransferJobLedger(service.Run.CompletedItems[2].Journal!).DeliveredTo(2));
        Assert.Equal(6U, source.Inventory(200).Bags);
        Assert.DoesNotContain(service.Trace, command => command.ItemId == 200 && command.Operation != GameOperationKind.ReadCachedInventory);
        Assert.Equal(new uint[] { 10155, 300 }, service.Trades.Select(trade => trade.ItemId));
    }

    [Fact]
    public void UnknownRosterSourcesAndRecipientsAreSkippedWithoutDispatchingCommandsToThem()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerB));
        service.StartJob(Saved(Job([999, 1], (888, 5), (2, 10))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Contains(999UL, service.Run.SkippedCharacters.Keys);
        Assert.Contains(888UL, service.Run.SkippedCharacters.Keys);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId is 999 or 888);
        Assert.DoesNotContain(999UL, service.Run.Snapshots.Keys);
        Assert.DoesNotContain(888UL, service.Run.Snapshots.Keys);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal!).DeliveredTo(2));
    }

    [Fact]
    public void FailedDiscoveryCharacterCannotBecomeTheAutomaticRelayEvenWithMostFreeSlots()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerB, cached: false), Sim(2, PeerA, 10), Sim(3, PeerA), Sim(9, PeerB));
        service.SetCachedFreeSlots(1, 140);
        service.SetCachedFreeSlots(9, 10);
        service.StartJob(Saved(Job([1, 2], (3, 10))));
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Contains(1UL, service.Run.SkippedCharacters.Keys);
        Assert.Equal(9UL, service.Run.AutomaticRelays[PeerB]);
        Assert.DoesNotContain(service.Trace, command => command.CharacterId == 1 && command.Operation != GameOperationKind.ReadCachedInventory);
        Assert.DoesNotContain(service.Run.Journal!.Plan.Allocations.SelectMany(allocation => allocation.Legs), leg => leg.SenderId == 1 || leg.ReceiverId == 1);
        Assert.Equal(10UL, new TransferJobLedger(service.Run.Journal).DeliveredTo(3));
    }

    [Fact]
    public void ExistingIssuedPlanIsNotRefilteredWhenCacheAvailabilityChangesAfterDelivery()
    {
        var source = Sim(1, PeerA, 800);
        var service = new WorkflowService(PeerA, PeerB, source, Sim(2, PeerB));
        service.StartJob(Saved(Job([1], (2, 800))));
        service.RunUntil(run => service.Trades.Count == 1 && run.Pending.Count == 0);
        var allocationId = Assert.Single(service.Run!.Journal!.Plan.Allocations).Id;
        var originalCacheReads = service.Trace.Count(command => command.Operation == GameOperationKind.ReadCachedInventory);
        source.CacheAvailable = false;
        service.PauseForTest();
        service.SimulateCoordinatorRestart();
        service.Resume();
        service.RunToTerminal();

        Assert.False(service.Run.Paused, service.LastError);
        Assert.Empty(service.Run.SkippedCharacters);
        Assert.Equal(allocationId, Assert.Single(service.Run.Journal!.Plan.Allocations).Id);
        Assert.Equal(800UL, service.Run.Journal.Plan.SourceBudgets[1]);
        Assert.Equal(800UL, new TransferJobLedger(service.Run.Journal).DeliveredTo(2));
        Assert.Equal(originalCacheReads, service.Trace.Count(command => command.Operation == GameOperationKind.ReadCachedInventory));
    }

    [Fact]
    public void FailedWorkerCommandStillPausesRatherThanHidingAnExecutionOrConnectionProblemAsMissingCache()
    {
        var service = new WorkflowService(PeerA, PeerB, Sim(1, PeerA, 10), Sim(2, PeerB));
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ReadCachedInventory)
            { result.Success = false; result.Error = "This client is busy with another operation."; }
            return result;
        };
        service.StartJob(Saved(Job([1], (2, 10))));
        service.RunToTerminal();

        Assert.True(service.Run!.Paused);
        Assert.Contains("busy with another operation", service.LastError);
        Assert.Empty(service.Run.SkippedCharacters);
        Assert.Empty(service.Run.Snapshots);
        Assert.Empty(service.Trades);
        Assert.All(service.Trace, command => Assert.Equal(GameOperationKind.ReadCachedInventory, command.Operation));
    }

    [Fact]
    public void AllKnownRelayCandidatesSkippedSkipsOnlyThatItemAndNextDirectTransferStillRuns()
    {
        var sourceOnOtherAccount = Sim(1, PeerB);
        sourceOnOtherAccount.Inventory(999).Bags = 7;
        var service = new WorkflowService(PeerA, PeerB, sourceOnOtherAccount, Sim(2, PeerA, 10), Sim(3, PeerA));
        service.TransformResult = (command, result) =>
        {
            if (command.Operation.Kind == GameOperationKind.ReadCachedInventory && command.Operation.CharacterId == 1
                && command.Operation.ItemId == 10155)
            { result.CachedInventoryAvailable = false; result.Error = "The only relay character has no complete cache for this item."; }
            return result;
        };
        var second = Job([1], (3, 7)); second.ItemId = 999;
        var template = Saved(Job([1, 2], (3, 10)));
        template.AdditionalItems.Add(second);
        service.StartJob(template);
        service.RunToTerminal();

        Assert.False(service.Run!.Paused, service.LastError);
        Assert.Equal(2, service.Run.CompletedItems.Count);
        Assert.Contains("relay", service.Run.CompletedItems[0].ItemSkipReason);
        Assert.Contains(1UL, service.Run.CompletedItems[0].SkippedCharacters.Keys);
        Assert.Null(service.Run.CompletedItems[0].Journal);
        Assert.DoesNotContain(service.Trace, command => command.ItemId == 10155 && command.Operation != GameOperationKind.ReadCachedInventory);
        Assert.Equal(10UL, service.Characters[2].Total);
        Assert.Equal(7UL, new TransferJobLedger(service.Run.CompletedItems[1].Journal!).DeliveredTo(3));
        Assert.Empty(service.Run.SkippedCharacters);
        Assert.True(service.Run.HasSkippedCharacters);
        Assert.Equal(999U, Assert.Single(service.Trades).ItemId);
    }
}
