using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private long? completionDeadline;
    private long nextCompletionSend;

    private void BeginCompletionHandoff()
    {
        var run = Run!;
        if (!run.Job.EnableAutoRetainerAfterCompletion || !run.CompletedNormally || run.DiscoveryOnly) return;
        if (run.CompletionCommands.Count != 0) return;
        RecordDiagnostic($"Starting AutoRetainer completion handoff; shortages={run.HasShortages}; skippedCharacters={run.HasSkippedCharacters}; all planned deliveries settled.");
        if (RemoteRoster?.ProtocolVersion < 4)
        { RecordDiagnostic("AutoRetainer handoff unavailable: update both PCs."); return; }
        foreach (var group in AllCharacters().GroupBy(entry => entry.PeerId))
            run.CompletionCommands.Add(new()
            {
                PeerId = group.Key,
                Command = Command(group.First().Character.ContentId, GameOperationKind.Inspect,
                    workerKind: WorkerCommandKind.EnableAutoRetainer),
            });
        SaveRun();
        completionDeadline = Environment.TickCount64 + 60_000;
        nextCompletionSend = 0;
    }

    private void CancelCompletionHandoff() => completionDeadline = null;

    private void UpdateCompletionHandoff(long now)
    {
        if (completionDeadline is not { } deadline) return;
        if (Run is not { CompletedNormally: true, Step: CoordinatorStep.Finished })
        { CancelCompletionHandoff(); return; }
        var pending = Run.CompletionCommands.Where(p => p.Response == null).ToArray();
        if (pending.Length == 0)
        {
            CancelCompletionHandoff();
            var failures = Run.CompletionCommands.Where(p => p.Response?.Success != true).ToArray();
            plugin.Record(failures.Length == 0 ? "AutoRetainer completion handoff accepted on both PCs."
                : "Transfer completed, but an AutoRetainer handoff failed: " + string.Join("; ", failures.Select(p => p.Response!.Error)));
            return;
        }
        if (now >= deadline)
        {
            CancelCompletionHandoff();
            plugin.Record("Transfer completed. An AutoRetainer handoff was not acknowledged within 60 seconds; check the other PC.");
            return;
        }
        if (now < nextCompletionSend) return;
        nextCompletionSend = now + 1000;
        foreach (var command in pending)
            if (command.PeerId == Settings.PeerId || HasAcceptedConnection)
                Dispatch(command.PeerId, command.Command);
    }
}
