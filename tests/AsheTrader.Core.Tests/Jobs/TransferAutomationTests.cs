using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using Newtonsoft.Json;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class TransferAutomationTests
{
    private static DateTimeOffset Utc(int day, int hour = 12) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);
    private static TransferScheduleState Queue(TransferRecurrence recurrence = TransferRecurrence.Daily) => new()
    {
        Schedules = [new() { Name = "Supplies", Timing = new() { LocalStart = Utc(26).DateTime, TimeZoneId = "UTC", Recurrence = recurrence },
            NextDue = Utc(26), Job = JobTemplate() }]
    };
    private static SavedTransferJob JobTemplate() => new()
    {
        Definition = new() { ItemId = 10155, Sources = [1], QuantityMode = JobQuantityMode.DeliverAmount, Recipients = [new() { CharacterId = 2, Quantity = 100 }] },
        Meeting = new() { WorldId = 1, TerritoryId = 2, AetheryteId = 3 }
    };
    private static WorkflowService Service() => new(PeerA, PeerB,
        new() { Id = 1, PeerId = PeerA, Bags = 100 }, new() { Id = 2, PeerId = PeerB });

    [Fact]
    public void BusyQueueDoesNotConsumeStartAndClaimIsStableAcrossRepeatedTicks()
    {
        var state = Queue();
        Assert.Null(TransferScheduleQueue.Claim(state, Utc(26), false));
        Assert.Equal(Utc(26), state.Schedules[0].NextDue);
        var occurrence = TransferScheduleQueue.Claim(state, Utc(26), true)!;
        Assert.Same(occurrence, TransferScheduleQueue.Claim(state, Utc(26), true));
        Assert.Equal(Utc(27), state.Schedules[0].NextDue);
        state.Schedules[0].Job.Definition.Recipients[0].Quantity = 777;
        Assert.Equal(100UL, occurrence.Job.Definition.Recipients[0].Quantity);
        state.Claimed = null;
        Assert.Null(TransferScheduleQueue.Claim(state, Utc(26), true));
        Assert.Null(TransferScheduleQueue.Claim(state, Utc(25), true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReloadCannotDuplicateClaimOrSilentlyRunUncertainStart(bool runSaved)
    {
        var state = Queue();
        var occurrence = TransferScheduleQueue.Claim(state, Utc(26), true)!;
        var loaded = JsonConvert.DeserializeObject<TransferScheduleState>(JsonConvert.SerializeObject(state))!;
        Assert.True(TransferScheduleQueue.RecoverClaim(loaded, runSaved ? occurrence.Id : null));
        Assert.Equal(!runSaved, loaded.Suspended);
        Assert.Equal(runSaved, loaded.Claimed == null);
        Assert.Null(TransferScheduleQueue.Claim(loaded, Utc(26), true));
    }

    [Fact]
    public void MissedDailyStartsSkipBacklogAndAdvanceToFuture()
    {
        var state = Queue();
        Assert.True(TransferScheduleQueue.ExpireMissed(state, Utc(29, 18)));
        Assert.Equal(Utc(30), state.Schedules[0].NextDue);
        Assert.Null(TransferScheduleQueue.Claim(state, Utc(29, 18), true));
        Assert.Contains("Skipped", state.Schedules[0].LastResult);
    }

    [Fact]
    public void OneTimeStartExpiresAndSuspensionNeverConsumesOccurrences()
    {
        var state = Queue(TransferRecurrence.Once);
        state.Suspended = true;
        Assert.False(TransferScheduleQueue.ExpireMissed(state, Utc(27)));
        Assert.Null(TransferScheduleQueue.Claim(state, Utc(26), true));
        state.Suspended = false;
        Assert.True(TransferScheduleQueue.ExpireMissed(state, Utc(27)));
        Assert.False(state.Schedules[0].Enabled); Assert.Null(state.Schedules[0].NextDue);
    }

    [Fact]
    public void WeeklyTimePreservesWallClockAndHandlesDaylightSavingOnce()
    {
        var timing = new TransferScheduleTiming { TimeZoneId = "America/New_York", Recurrence = TransferRecurrence.Weekly, LocalStart = new(2026, 10, 25, 1, 30, 0) };
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 5, 30, 0, TimeSpan.Zero), timing.First());
        var fall = timing.NextAfter(timing.First())!.Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), fall);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 6, 30, 0, TimeSpan.Zero), timing.NextAfter(fall));
        timing.LocalStart = new(2026, 3, 8, 2, 30, 0); timing.Recurrence = TransferRecurrence.Daily;
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), timing.First());
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero), timing.NextAfter(timing.First()));
    }

    [Fact]
    public void ScheduledRunPausesBothBeforeAnyGameWorkAndHandsBackBoth()
    {
        var service = Service(); var occurrence = Guid.NewGuid();
        service.StartScheduledForTest(JobTemplate(), occurrence); service.RunToTerminal();
        Assert.All(service.Trace.Take(2), t => Assert.Equal(WorkerCommandKind.PauseAutoRetainer, t.Kind));
        Assert.Equal(2, service.AutoRetainerPauseRequests.Count); Assert.Equal(2, service.AutoRetainerRequests.Count);
        Assert.All(service.AutoRetainerPauseRequests.Values, value => Assert.Equal(1, value));
        Assert.Equal(occurrence, service.Run!.ScheduleOccurrenceId);
        Assert.True(service.Run.CompletedNormally);
    }

    [Fact]
    public void ArPreparationFailureCannotStartMovementOrCompletionHandoff()
    {
        var service = Service(); service.AutoRetainerPauseFailure = "AR unavailable";
        service.StartScheduledForTest(JobTemplate(), Guid.NewGuid()); service.RunToTerminal();
        Assert.True(service.Run!.Paused); Assert.Empty(service.Trades); Assert.Empty(service.AutoRetainerRequests);
        Assert.All(service.Trace, t => Assert.Equal(WorkerCommandKind.PauseAutoRetainer, t.Kind));
    }

    [Fact]
    public void OldWorkerIsRejectedBeforeScheduledRunIsCreated()
    {
        var service = Service(); service.RemoteRoster!.ProtocolVersion = 4;
        Assert.Throws<InvalidOperationException>(() => service.StartScheduledForTest(JobTemplate(), Guid.NewGuid()));
        Assert.Null(service.Run); Assert.Empty(service.Trace);
    }

    [Fact]
    public void LostArAcknowledgementsWaitAndRecoverWithoutDuplicateActions()
    {
        var service = Service();
        service.SuppressInitialResponse = c => c.Kind == WorkerCommandKind.PauseAutoRetainer;
        service.StartScheduledForTest(JobTemplate(), Guid.NewGuid()); service.Tick();
        Assert.Equal(CoordinatorStep.ScheduledStart, service.Run!.Step);
        Assert.All(service.Trace, t => Assert.Equal(WorkerCommandKind.PauseAutoRetainer, t.Kind));
        service.SuppressInitialResponse = null; service.RetryPendingForTest(); service.RunToTerminal();
        Assert.True(service.Run.CompletedNormally);
        Assert.All(service.AutoRetainerPauseRequests.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public void ManualPauseDuringScheduledPreparationNeverStartsInventoryWork()
    {
        var service = Service();
        service.StartScheduledForTest(JobTemplate(), Guid.NewGuid());
        service.PauseForTest(); service.Tick();
        Assert.Empty(service.Trace);
        service.Resume(); service.RunToTerminal();
        Assert.True(service.Run!.CompletedNormally);
    }

    [Fact]
    public void UncommittedMovementsAreNotReportedAsDelivered()
    {
        var service = Service(); service.StartJob(JobTemplate()); service.RunToTerminal();
        var run = service.Run!;
        var journal = run.CompletedItems[0].Journal!;
        journal.Operations.Add(new() { AllocationId = journal.Plan.Allocations[0].Id, LegIndex = 0, Quantity = 100, State = TransferOperationState.Issued });
        var receipt = TradeHistoryProjection.Create(run);
        Assert.Equal(100UL, Assert.Single(receipt.Items).Delivered);
        Assert.DoesNotContain(receipt.Movements, m => m.OperationId == journal.Operations.Last().Id);
    }

    [Fact]
    public void ReceiptsDeduplicateVisitAndCompletedLedgersAndKeepEachRunDistinct()
    {
        var service = Service(); var job = JobTemplate();
        service.StartScheduledForTest(job, Guid.NewGuid()); service.RunToTerminal();
        var receipt = TradeHistoryProjection.Create(service.Run!);
        Assert.Equal(100UL, Assert.Single(receipt.Items).Delivered);
        Assert.Equal(100UL, receipt.Movements.Aggregate(0UL, (sum, m) => sum + m.Quantity));
        Assert.Equal("Weekly supplies", receipt.Schedule);
        Assert.All(receipt.Movements, m => Assert.True(m.FinalDelivery));
        service.StartJob(job);
        Assert.NotEqual(receipt.Id, service.Run!.RunId);
    }

    [Fact]
    public void RelayHopDoesNotDoubleFinalDeliveryTotal()
    {
        var service = new WorkflowService(PeerA, PeerB,
            new() { Id = 1, PeerId = PeerA, Bags = 100 }, new() { Id = 2, PeerId = PeerA }, new() { Id = 3, PeerId = PeerB });
        service.StartJob(JobTemplate()); service.RunToTerminal();
        var receipt = TradeHistoryProjection.Create(service.Run!);
        Assert.Equal(100UL, Assert.Single(receipt.Items).Delivered);
        Assert.Equal(100m, receipt.Movements.Where(m => !m.FinalDelivery).Sum(m => (decimal)m.Quantity));
        Assert.Equal(200m, receipt.Movements.Sum(m => (decimal)m.Quantity));
    }

    [Fact]
    public void ReceiptPersistsOnceAndCorruptArchiveIsNotOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AsheTraderTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "history.json");
        var store = new TradeHistoryStore(path);
        var receipt = new TradeReceipt { Id = Guid.NewGuid(), Name = "Run", EndedAt = Utc(26) };
        store.Record(receipt); store.Record(receipt);
        Assert.Single(new TradeHistoryStore(path).Records);
        File.WriteAllText(path, "broken");
        store = new(path); store.Record(new() { Id = Guid.NewGuid() });
        Assert.NotEmpty(store.Error); Assert.Equal("broken", File.ReadAllText(path));
    }
}
