using AsheTrader.Core.Jobs;
using AsheTrader.Workflow;
using static AsheTrader.Core.Tests.Jobs.JobPlannerTests;

namespace AsheTrader.Core.Tests.Jobs;

public sealed class CompletionCoordinatorTests
{
    private static (WorkflowService, SavedTransferJob) Setup(bool enabled, ulong quantity = 100)
    {
        var service = new WorkflowService(PeerA, PeerB,
            new() { Id = 1, PeerId = PeerA, Bags = 100 }, new() { Id = 2, PeerId = PeerB });
        var job = new SavedTransferJob
        {
            EnableAutoRetainerAfterCompletion = enabled,
            Definition = new() { ItemId = 10155, Sources = [1], QuantityMode = JobQuantityMode.DeliverAmount,
                Recipients = [new() { CharacterId = 2, Quantity = quantity }] },
            Meeting = new() { WorldId = 1, TerritoryId = 2, AetheryteId = 3 },
        };
        return (service, job);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyOptedInCompletedJobRequestsBothPeersOnce(bool enabled)
    {
        var (service, job) = Setup(enabled);
        service.StartJob(job); service.RunToTerminal(); service.Tick(); service.Tick();
        Assert.True(service.Run!.CompletedNormally);
        Assert.Equal(enabled ? 2 : 0, service.AutoRetainerRequests.Count);
        Assert.All(service.AutoRetainerRequests.Values, value => Assert.Equal(1, value));
    }
    [Fact]
    public void DiscoveryNeverEnablesAutoRetainer()
    {
        var (scan, job) = Setup(true); scan.ScanJob(job); scan.RunToTerminal(); scan.Tick();
        Assert.Empty(scan.AutoRetainerRequests); Assert.False(scan.Run!.CompletedNormally);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NaturallyFinishedShortageHandsBackBothPeersOnlyWhenEnabled(bool legacy, bool enabled)
    {
        var (service, job) = Setup(enabled, 200);
        if (legacy) service.StartLegacyJob(job); else service.StartJob(job);
        service.RunToTerminal(); service.Tick(); service.Tick();
        Assert.Equal(CoordinatorStep.Finished, service.Run!.Step);
        Assert.True(service.Run.HasShortages);
        Assert.True(service.Run.CompletedNormally);
        Assert.Empty(service.Run.Pending);
        Assert.Equal(100U, service.Characters[2].Bags);
        Assert.Equal(enabled ? 2 : 0, service.AutoRetainerRequests.Count);
        Assert.All(service.AutoRetainerRequests.Values, count => Assert.Equal(1, count));
    }
    [Fact]
    public void SkippedSourceDoesNotBlockNaturallyFinishedHandoff()
    {
        var (service, job) = Setup(true);
        job.Definition.Sources.Add(999); // A removed character has no cache/roster entry.
        service.StartJob(job); service.RunToTerminal(); service.Tick();
        Assert.True(service.Run!.HasSkippedCharacters);
        Assert.True(service.Run.CompletedNormally);
        Assert.Equal(2, service.AutoRetainerRequests.Count);
    }
    [Fact]
    public void LostCompletionAcknowledgmentsDoNotRepeatHandoffs()
    {
        var (service, job) = Setup(true);
        service.SuppressInitialResponse = c => c.Kind == WorkerCommandKind.EnableAutoRetainer;
        service.StartJob(job); service.RunToTerminal();
        Assert.All(service.Run!.CompletionCommands, p => Assert.Null(p.Response));
        service.SuppressInitialResponse = null;
        service.PumpCompletionForTest();
        Assert.All(service.Run.CompletionCommands, p => Assert.True(p.Response?.Success));
        Assert.Equal(2, service.AutoRetainerRequests.Count);
        Assert.All(service.AutoRetainerRequests.Values, value => Assert.Equal(1, value));
    }
    [Fact]
    public void ReloadDoesNotReplayAnOldCompletion()
    {
        var (service, job) = Setup(true);
        service.SuppressInitialResponse = c => c.Kind == WorkerCommandKind.EnableAutoRetainer;
        service.StartJob(job); service.RunToTerminal(); service.SimulateCoordinatorRestart();
        service.SuppressInitialResponse = null; service.PumpCompletionForTest();
        Assert.All(service.Run!.CompletionCommands, p => Assert.Null(p.Response));
    }
    [Fact]
    public void OldPeerCannotStartOptedInTransfer()
    {
        var (service, job) = Setup(true); service.RemoteRoster!.ProtocolVersion = 3;
        Assert.Throws<InvalidOperationException>(() => service.StartJob(job));
        Assert.Empty(service.AutoRetainerRequests);
    }
    [Fact]
    public void PauseAndEndDoNotRequestHandoff()
    {
        var (service, job) = Setup(true); service.StartJob(job); service.PauseForTest("User pause");
        service.Tick(); Assert.Empty(service.AutoRetainerRequests);
        service.EndPausedJob(); service.Tick();
        Assert.False(service.Run!.CompletedNormally); Assert.Empty(service.AutoRetainerRequests);
    }
}
