using AsheTrader.Core.Jobs;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private long tradeRecoveryPollAt;
    private long tradeRecoveryStartedAt;
    private const int MaximumEmptyTrades = 3;

    private void BeginAutomaticTradeRecovery(string status)
    {
        if (Run?.TradeOperationId == null || Run.Pending.Count == 0
            || Run.Pending.Any(pending => pending.Command.Kind is not (WorkerCommandKind.SendBatch or WorkerCommandKind.ReceiveBatch)))
            throw new InvalidOperationException("The saved trade command checkpoint is incomplete. Preserve diagnostics before changing inventory.");
        Run.TradeRecovery ??= new();
        Run.Paused = false;
        Run.Step = CoordinatorStep.AwaitTrade;
        Run.Status = status;
        error = "";
        tradeRecoveryPollAt = 0;
        tradeRecoveryStartedAt = Environment.TickCount64;
        SaveRun();
        StopTradeForTally();
    }

    private void AcceptTradeTally(Guid peerId, TradeTallyReply reply)
    {
        var recovery = Run?.TradeRecovery;
        var pending = Run?.Pending.SingleOrDefault(pending => pending.PeerId == peerId && pending.Command.Id == reply.CommandId);
        if (Run?.Paused != false || recovery == null || recovery.RoundId != reply.RoundId || recovery.Transferred.HasValue || pending == null) return;
        if (!reply.Ready)
        {
            if (!reply.Retryable) { Pause($"Automatic trade tally stopped: {reply.Error}"); return; }
            Run.Status = reply.Error;
            return;
        }
        if (reply.Result == null || reply.Result.CommandId != pending.Command.Id)
            throw new InvalidDataException("The trade tally omitted its matching worker result.");
        recovery.Tallies[pending.Command.Id] = Clone(reply.Result);
        SaveRun();
    }

    private void AcceptAutomaticTradeResolution(Guid peerId, TradeResolutionReply reply)
    {
        var recovery = Run?.TradeRecovery;
        var pending = Run?.Pending.SingleOrDefault(pending => pending.PeerId == peerId && pending.Command.Id == reply.CommandId);
        if (Run?.Paused != false || recovery?.Transferred == null || recovery.RoundId != reply.RoundId || pending == null) return;
        if (reply.Result == null) { Pause($"Automatic trade tally could not be saved: {reply.Error}"); return; }
        var expected = recovery.Transferred.Value ? TradeTallyOutcome.Transferred : TradeTallyOutcome.Unchanged;
        if (reply.Result.CommandId != pending.Command.Id || reply.Result.Ambiguous
            || TradeReconciliationEvidence.Classify(pending.Command, reply.Result) != expected)
            throw new InvalidDataException("A worker acknowledged a different trade outcome than the paired tally.");
        recovery.Acknowledgements[pending.Command.Id] = Clone(reply.Result);
        SaveRun();
    }

    private void AdvanceAutomaticTradeRecovery()
    {
        var run = Run!;
        var recovery = run.TradeRecovery!;
        if (tradeRecoveryStartedAt == 0) tradeRecoveryStartedAt = Environment.TickCount64;
        if (Environment.TickCount64 - tradeRecoveryStartedAt > 30_000)
            throw new TimeoutException("Automatic trade tally timed out. Keep both original characters logged in, close trade and retainer windows, then use Resume / tally inventories. No unverified batch has been repeated.");
        if (recovery.Transferred == null)
        {
            if (Environment.TickCount64 >= tradeRecoveryPollAt)
            {
                tradeRecoveryPollAt = Environment.TickCount64 + 500;
                foreach (var pending in run.Pending.Where(pending => !recovery.Tallies.ContainsKey(pending.Command.Id)).ToArray())
                {
                    var request = new TradeTallyRequest(pending.Command.Id, recovery.RoundId);
                    if (pending.PeerId == Settings.PeerId) HandleTradeTally(request, pending.PeerId);
                    else Send("trade-tally", request);
                    if (run.Paused) return;
                }
            }
            if (recovery.Tallies.Count != run.Pending.Count) return;
            var outcomes = run.Pending.Select(pending => TradeReconciliationEvidence.Classify(pending.Command, recovery.Tallies[pending.Command.Id])).ToArray();
            if (outcomes.All(outcome => outcome == TradeTallyOutcome.Unchanged)) recovery.Transferred = false;
            else if (run.Pending.Count == 2 && outcomes.All(outcome => outcome == TradeTallyOutcome.Transferred)) recovery.Transferred = true;
            else
            {
                var counts = run.Pending.Select(pending =>
                {
                    var tally = recovery.Tallies[pending.Command.Id];
                    return $"{pending.Command.Kind}: {tally.BeforeQuantity?.ToString() ?? "unknown"} → {tally.CurrentQuantity?.ToString() ?? "unknown"}, expected {pending.Command.Operation.Quantity}";
                });
                throw new InvalidOperationException($"Both inventories do not prove the same zero-or-full trade outcome ({string.Join("; ", counts)}). Check for manual inventory changes and keep the original characters logged in. Resume / tally inventories will re-read them; no further items have been moved.");
            }
            run.Status = recovery.Transferred.Value ? "Both inventories confirm the transfer; saving both worker outcomes…"
                : "Both inventories confirm no transfer; saving the empty batch before retrying…";
            // The paired decision is durable before either worker can be released for another command.
            SaveRun();
            tradeRecoveryPollAt = 0;
        }
        if (Environment.TickCount64 >= tradeRecoveryPollAt)
        {
            tradeRecoveryPollAt = Environment.TickCount64 + 500;
            foreach (var pending in run.Pending.Where(pending => !recovery.Acknowledgements.ContainsKey(pending.Command.Id)).ToArray())
            {
                var tally = recovery.Tallies[pending.Command.Id];
                if (tally is { Success: true } or { AutoReconciled: true } or { OperatorReviewed: true } or { NeverStarted: true })
                {
                    recovery.Acknowledgements[pending.Command.Id] = Clone(tally);
                    continue;
                }
                var resolution = new AutomaticTradeResolution(pending.Command.Id, recovery.RoundId, recovery.Transferred!.Value,
                    tally.BeforeQuantity, tally.CurrentQuantity);
                if (pending.PeerId == Settings.PeerId) HandleAutomaticTradeResolution(resolution, pending.PeerId);
                else Send("trade-settle", resolution);
                if (run.Paused) return;
            }
        }
        if (recovery.Acknowledgements.Count != run.Pending.Count) return;
        foreach (var pending in run.Pending) pending.Response = Clone(recovery.Acknowledgements[pending.Command.Id]);
        SaveRun();
        var transferred = recovery.Transferred!.Value;
        var ledger = new TransferJobLedger(run.Journal!);
        var assignment = run.Journal!.Plan.Allocations.Single(allocation => allocation.Id == run.AllocationId);
        var alreadyCancelled = run.Journal.Operations.Single(operation => operation.Id == run.TradeOperationId).State == TransferOperationState.Cancelled;
        run.TradeRecovery = null;
        if (!transferred && !alreadyCancelled) run.ConsecutiveEmptyTrades++;
        ReconcileTrade(ledger, assignment.Legs[run.LegIndex]);
        if (!transferred)
        {
            RecordDiagnostic($"Automatic trade tally resolved no transfer; consecutiveEmpty={run.ConsecutiveEmptyTrades}");
            if (run.ConsecutiveEmptyTrades >= MaximumEmptyTrades)
                Pause("Three consecutive trade attempts moved no items. Check both characters can interact at the meeting point, then use Resume / tally inventories to try again. All empty batches were accounted for automatically.");
        }
        else RecordDiagnostic("Automatic trade tally confirmed the full batch; original operation committed once.");
        SaveRun();
    }
}
