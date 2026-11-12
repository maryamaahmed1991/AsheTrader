using AsheTrader.Core;
using AsheTrader.Core.Jobs;
using AsheTrader.Workflow.Native;

namespace AsheTrader.Workflow;

public sealed class VisitBatchLine
{
    public int ItemIndex { get; set; }
    public Guid AllocationId { get; set; }
    public int LegIndex { get; set; }
    public uint Quantity { get; set; }
    public Guid OperationId { get; set; }
}

public sealed class VisitTradeBatch
{
    public List<TradeLine> Requested { get; set; } = [];
    public List<VisitBatchLine> Lines { get; set; } = [];
}

public sealed partial class WorkflowService
{
    private void AdvanceVisitWorkflow()
    {
        if (Run!.VisitBatch == null)
        {
            if (!AdvanceCharacterVisit()) FinishVisitWorkflow();
            return;
        }
        AdvanceVisitTrade();
    }

    private void BeginPreparedVisitTrade(PreparedVisit visit)
    {
        var run = Run!;
        if (run.Pending.Count != 0 || run.VisitBatch != null) throw new InvalidDataException("The visit has unresolved work.");
        // Every capacity observation includes the same physical empty slots. Reserve each
        // slot once across the whole offer, allowing independent matching-stack headroom.
        var freeSlots = visit.Lines.Where(line => line.ReceiverFreeBagSlots.HasValue)
            .Select(line => line.ReceiverFreeBagSlots!.Value).DefaultIfEmpty(0U).Min();
        var knownSlots = visit.Lines.All(line => line.ReceiverFreeBagSlots.HasValue);
        var requests = new List<TradeLine>();
        foreach (var line in visit.Lines)
        {
            if (line.StackSize == 0) throw new InvalidDataException("A prepared item has no stack size.");
            var item = run.VisitItems[line.ItemIndex];
            var ledger = new TransferJobLedger(item.Journal!);
            var cap = line.ReceiverBagCapacity;
            uint merge = 0;
            if (knownSlots)
            {
                var emptyCapacity = (ulong)line.ReceiverFreeBagSlots!.Value * line.StackSize;
                merge = emptyCapacity >= cap ? 0 : cap - (uint)emptyCapacity;
                cap = (uint)Math.Min(uint.MaxValue, merge + (ulong)freeSlots * line.StackSize);
            }
            else if (requests.Count != 0) break;
            var quantity = (uint)Math.Min(Math.Min(line.MaximumQuantity, ledger.RemainingForLeg(line.AllocationId, line.LegIndex)),
                Math.Min((ulong)line.SenderBagQuantity, cap));
            if (quantity == 0) continue;
            if (knownSlots)
            {
                var needSlots = quantity > merge ? ((ulong)quantity - merge + line.StackSize - 1) / line.StackSize : 0;
                freeSlots -= checked((uint)needSlots);
            }
            requests.Add(new() { ItemId = item.Definition.ItemId, HighQuality = item.Definition.HighQuality, Quantity = quantity });
        }
        if (requests.Count == 0) throw new InvalidOperationException("No prepared items fit this visit. Free receiving space, then resume.");
        run.VisitBatch = new() { Requested = requests };
        run.Step = CoordinatorStep.PreviewBatch;
        run.Status = $"Preparing a mixed trade for {requests.Count} item type(s)…";
        SaveRun();
    }

    private WorkerCommand VisitTradeCommand(WorkerCommandKind kind, IReadOnlyList<TradeLine> items)
    {
        var visit = Run!.Visit!;
        var definition = Run.VisitItems.Values.Single(item => item.Definition.ItemId == items[0].ItemId
            && item.Definition.HighQuality == items[0].HighQuality).Definition;
        var receive = kind == WorkerCommandKind.ReceiveBatch;
        var command = Command(receive ? visit.ReceiverId : visit.SenderId, GameOperationKind.Inspect,
            checked((uint)items.Aggregate(0UL, (sum, item) => sum + item.Quantity)), workerKind: kind,
            partner: kind == WorkerCommandKind.PreviewBatch ? null : receive ? visit.SenderId : visit.ReceiverId, item: definition);
        command.TradeItems = items.Select(item => new TradeLine { ItemId = item.ItemId, HighQuality = item.HighQuality, Quantity = item.Quantity }).ToList();
        return command;
    }

    private List<TradeLine> VisitBatchItems() => Run!.VisitBatch!.Lines.Select(line =>
    {
        var definition = Run.VisitItems[line.ItemIndex].Definition;
        return new TradeLine { ItemId = definition.ItemId, HighQuality = definition.HighQuality, Quantity = line.Quantity };
    }).ToList();

    private void AdvanceVisitTrade()
    {
        var run = Run!;
        var visit = run.Visit ?? throw new InvalidDataException("Mixed trade has no pinned visit.");
        var batch = run.VisitBatch!;
        switch (run.Step)
        {
            case CoordinatorStep.PreviewBatch:
                if (run.Pending.Count == 0)
                    Issue(visit.SenderPeerId, VisitTradeCommand(WorkerCommandKind.PreviewBatch, batch.Requested));
                else if (run.Pending.Single().Response is { } preview)
                {
                    if (!preview.Success) throw new InvalidOperationException(preview.Error);
                    if (preview.TradeItems.Count == 0 || preview.TradeItems.Any(line => line.Quantity == 0)
                        || preview.TradeItems.Select(line => new ItemKey(line.ItemId, line.HighQuality)).Distinct().Count() != preview.TradeItems.Count)
                        throw new InvalidDataException("Mixed trade preview has missing, zero or duplicate item quantities.");
                    var prepared = new List<(int Index, TransferJobJournal Journal, VisitBatchLine Line)>();
                    foreach (var offered in preview.TradeItems)
                    {
                        var requested = batch.Requested.SingleOrDefault(line => line.ItemId == offered.ItemId && line.HighQuality == offered.HighQuality);
                        if (requested == null || offered.Quantity > requested.Quantity) throw new InvalidDataException("Mixed preview exceeds the agreed item budget.");
                        var work = visit.Lines.Single(line => run.VisitItems[line.ItemIndex].Definition.ItemId == offered.ItemId
                            && run.VisitItems[line.ItemIndex].Definition.HighQuality == offered.HighQuality);
                        var journal = Clone(run.VisitItems[work.ItemIndex].Journal!);
                        var operation = new TransferJobLedger(journal).PrepareBatch(work.AllocationId, work.LegIndex, offered.Quantity);
                        prepared.Add((work.ItemIndex, journal, new() { ItemIndex = work.ItemIndex, AllocationId = work.AllocationId,
                            LegIndex = work.LegIndex, Quantity = offered.Quantity, OperationId = operation.Id }));
                    }
                    if (preview.Quantity != prepared.Aggregate(0UL, (sum, item) => sum + item.Line.Quantity))
                        throw new InvalidDataException("Mixed preview total differs from its exact item quantities.");
                    foreach (var item in prepared) run.VisitItems[item.Index].Journal = item.Journal;
                    batch.Lines = prepared.Select(item => item.Line).ToList();
                    run.Pending.Clear(); run.TradeOperationId = batch.Lines[0].OperationId;
                    FocusVisitItem(batch.Lines[0].ItemIndex);
                    run.AllocationId = batch.Lines[0].AllocationId; run.LegIndex = batch.Lines[0].LegIndex;
                    run.DesiredQuantity = preview.Quantity;
                    run.Step = CoordinatorStep.ArmReceiver;
                    SaveRun();
                }
                break;
            case CoordinatorStep.ArmReceiver:
                if (run.Pending.Count == 0) Issue(visit.ReceiverPeerId, VisitTradeCommand(WorkerCommandKind.ReceiveBatch, VisitBatchItems()));
                else if (run.Pending.Single().Response is { Success: false } rejected) throw new InvalidOperationException(rejected.Error);
                else if (run.Pending.Single().Ready) { run.Step = CoordinatorStep.SendBatch; SaveRun(); }
                break;
            case CoordinatorStep.SendBatch:
                if (run.Pending.Any(pending => pending.Response is { Success: false })) throw new InvalidOperationException(TradeFailureMessage());
                if (run.Pending.Count == 1)
                {
                    foreach (var line in batch.Lines) new TransferJobLedger(run.VisitItems[line.ItemIndex].Journal!).MarkIssued(line.OperationId);
                    Issue(visit.SenderPeerId, VisitTradeCommand(WorkerCommandKind.SendBatch, VisitBatchItems()));
                }
                if (!run.Paused && run.TradeRecovery == null) { run.Step = CoordinatorStep.AwaitTrade; SaveRun(); }
                break;
            case CoordinatorStep.AwaitTrade: ReconcileVisitBatch(); break;
            default: throw new InvalidDataException("Mixed trade is in an invalid execution stage.");
        }
    }

    private void ReconcileVisitBatch()
    {
        var run = Run!;
        var batch = run.VisitBatch ?? throw new InvalidDataException("The mixed batch checkpoint is missing.");
        var visit = run.Visit ?? throw new InvalidDataException("The mixed visit checkpoint is missing.");
        if (run.Pending.Any(pending => pending.Response == null)) return;
        var unchanged = run.Pending.Count is 1 or 2 && run.Pending.All(pending => IsVerifiedNoTransfer(pending.Response!));
        if (!unchanged && (run.Pending.Count != 2 || run.Pending.Any(pending => !pending.Response!.Success
            || TradeReconciliationEvidence.Classify(pending.Command, pending.Response) != TradeTallyOutcome.Transferred)))
            throw new InvalidOperationException("Both PCs must confirm every exact item quantity before a mixed trade can be credited.");
        // Validate and settle cloned journals first. A single save publishes the entire
        // mixed batch, so a restart cannot credit just the first item then resend others.
        var updated = new Dictionary<int, TransferJobJournal>();
        foreach (var line in batch.Lines)
        {
            var item = run.VisitItems[line.ItemIndex];
            var journal = Clone(item.Journal!);
            var ledger = new TransferJobLedger(journal);
            var operation = journal.Operations.Single(operation => operation.Id == line.OperationId);
            var leg = journal.Plan.Allocations.Single(allocation => allocation.Id == line.AllocationId).Legs[line.LegIndex];
            if (unchanged)
            {
                if (operation.State == TransferOperationState.Prepared) ledger.CancelBeforeIssue(operation.Id);
                else if (operation.State != TransferOperationState.Cancelled
                    && !ledger.ResolveNoTransfer(operation.Id, Observe(leg.SenderPeerId, leg.SenderId, 0), Observe(leg.ReceiverPeerId, leg.ReceiverId, 0), out var failure))
                    throw new InvalidOperationException(failure);
            }
            else
            {
                foreach (var pending in run.Pending)
                {
                    var evidence = pending.Response!.TradeItems.SingleOrDefault(value => value.ItemId == item.Definition.ItemId && value.HighQuality == item.Definition.HighQuality);
                    if (evidence?.Quantity != line.Quantity) throw new InvalidDataException("Mixed trade evidence differs from the item journal.");
                }
                ledger.RecordObservation(operation.Id, ObservationSide.Sender, Observe(leg.SenderPeerId, leg.SenderId, line.Quantity));
                ledger.RecordObservation(operation.Id, ObservationSide.Receiver, Observe(leg.ReceiverPeerId, leg.ReceiverId, line.Quantity));
                if (!ledger.TryCommit(operation.Id, out var failure)) throw new InvalidOperationException(failure);
            }
            updated.Add(line.ItemIndex, journal);
        }
        foreach (var item in updated) run.VisitItems[item.Key].Journal = item.Value;
        if (!unchanged)
        {
            OnVisitBatchCommitted(visit, batch.Lines);
            run.ConsecutiveEmptyTrades = 0;
        }
        run.LastVisitSenderId = visit.SenderId; run.LastVisitReceiverId = visit.ReceiverId;
        run.Pending.Clear(); run.TradeOperationId = null; run.VisitBatch = null; run.Visit = null;
        run.MeetingPinned = false;
        RefreshVisitResults(); FocusVisitItem(batch.Lines[0].ItemIndex);
        run.Step = CoordinatorStep.SelectLeg;
        run.Status = unchanged ? "No items moved. Preparing this character visit again…" : $"Verified mixed trade: {batch.Lines.Count} item type(s). Continuing this visit…";
        SaveRun();
    }

    private void ResumeVisitWorkflow()
    {
        var run = Run!;
        FocusVisitItem(run.ActiveItemIndex);
        if (run.VisitBatch is { Lines.Count: > 0 } batch)
        {
            if (run.Pending.Count == 0)
            {
                foreach (var line in batch.Lines) new TransferJobLedger(run.VisitItems[line.ItemIndex].Journal!).CancelBeforeIssue(line.OperationId);
                run.VisitBatch = null; run.TradeOperationId = null;
                run.Visit = null; run.Step = CoordinatorStep.SelectLeg; run.Paused = false; SaveRun();
            }
            else { run.TradeRecovery = null; BeginAutomaticTradeRecovery("Tallying every item in the interrupted trade on both PCs…"); }
            return;
        }
        // A preview is read-only. Every gameplay command still uses its original ID
        // through ResumeCharacterVisit, especially an interrupted retainer movement.
        if (run.VisitBatch != null) { run.Pending.Clear(); run.VisitBatch = null; run.Visit = null; }
        if (run.Visit != null) ResumeCharacterVisit();
        else { run.Paused = false; run.Step = CoordinatorStep.SelectLeg; resendAt = 0; SaveRun(); }
    }

    private void EndPausedVisitJob()
    {
        var run = Run!;
        if (run.VisitBatch != null || run.Pending.Any(pending => pending.Response == null || pending.Response.Ambiguous))
            RefuseToEndPausedJob("Resume to tally the pending visit before ending this job.");
        foreach (var pending in run.Pending.Where(pending => StorageReconciliation.IsStorage(pending.Command)))
            AccountStorageMovement(pending, pending.Command.Operation.CharacterId);
        foreach (var item in run.VisitItems.Values.Where(item => item.Journal != null))
        {
            var ledger = new TransferJobLedger(item.Journal!);
            if (ledger.HasPendingOperation || item.Journal!.Plan.Allocations.Any(allocation => allocation.Legs.Count == 2 && ledger.RelayCargo(allocation.Legs[0].ReceiverId) > 0))
                RefuseToEndPausedJob("Resume to reconcile the pending trade and deliver all remaining relay cargo before ending this job.");
        }
        run.Step = CoordinatorStep.Finished; run.Status = "Job ended by user. Confirmed item deliveries remain saved.";
        run.Pending.Clear(); run.Visit = null; SaveRun();
    }
}
