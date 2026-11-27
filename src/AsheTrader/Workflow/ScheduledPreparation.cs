using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private void AdvanceScheduledStart()
    {
        var run = Run!;
        foreach (var group in AllCharacters().GroupBy(c => c.PeerId))
            if (!run.Pending.Any(p => p.PeerId == group.Key))
                Issue(group.Key, Command(group.First().Character.ContentId, GameOperationKind.Inspect,
                    workerKind: WorkerCommandKind.PauseAutoRetainer));
        if (run.Pending.Count != 2) throw new InvalidOperationException("A scheduled transfer requires both paired accounts.");
        var failed = run.Pending.FirstOrDefault(p => p.Response is { Success: false });
        if (failed != null) throw new InvalidOperationException("Scheduled AR preparation: " + failed.Response!.Error);
        if (run.Pending.Any(p => p.Response == null)) return;
        run.HistoryNotes.Add(new(DateTimeOffset.UtcNow, "Both PCs confirmed AutoRetainer idle with Multi Mode off."));
        run.Pending.Clear();
        SetStep(CoordinatorStep.DiscoverCache);
    }
}
