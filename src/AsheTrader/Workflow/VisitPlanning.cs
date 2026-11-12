using AsheTrader.Core.Jobs;

namespace AsheTrader.Workflow;

public sealed partial class WorkflowService
{
    private void UpgradeLegacyItemRun()
    {
        var run = Run!;
        // Existing allocations and cargo remain authoritative. Only unplanned item
        // lines need discovery; no completed trade or retainer movement is replayed.
        foreach (var completed in run.CompletedItems)
            run.VisitItems[completed.ItemIndex] = new()
            {
                ItemIndex = completed.ItemIndex, Definition = completed.Definition, Journal = completed.Journal,
                Snapshots = completed.Snapshots, Withdrawn = completed.Withdrawn, Deposited = completed.Deposited,
                AutomaticRelays = completed.AutomaticRelays, SkippedCharacters = completed.SkippedCharacters,
                ItemSkipReason = completed.ItemSkipReason,
            };
        run.VisitMode = true;
        run.Preparation = null;
        AdvanceVisitPlanning();
        RecordDiagnostic("Upgraded the saved multi-item run at an idle scheduling boundary; all original item ledgers and cargo remain unchanged.");
    }

    private void AdvanceVisitPlanning()
    {
        var run = Run!;
        if (run.Pending.Count != 0 || run.TradeOperationId.HasValue)
            throw new InvalidOperationException("Finish pending inventory discovery before planning another item.");
        run.VisitItems[run.ActiveItemIndex] = new()
        {
            ItemIndex = run.ActiveItemIndex, Definition = run.ActiveDefinition, Journal = run.Journal,
            Snapshots = run.Snapshots, Withdrawn = run.Withdrawn, Deposited = run.Deposited,
            AutomaticRelays = run.AutomaticRelays, RelayCapacity = run.RelayCapacity,
            CollectionRoute = run.CollectionRoute, SkippedCharacters = run.SkippedCharacters,
            ItemSkipReason = run.ItemSkipReason, RelayFlushCharacterId = run.RelayFlushCharacterId,
        };
        var next = Enumerable.Range(0, run.TotalItemCount).FirstOrDefault(index => !run.VisitItems.ContainsKey(index), -1);
        if (next >= 0)
        {
            run.ActiveItemIndex = next;
            run.Journal = null; run.Snapshots = []; run.Withdrawn = []; run.Deposited = [];
            run.AutomaticRelays = []; run.RelayCapacity = []; run.CollectionRoute = null;
            run.SkippedCharacters = []; run.DiscoveryNotes = []; run.ItemSkipReason = "";
            run.RelayFlushCharacterId = null; run.RelayCandidateId = null; run.RelayCandidatePeerId = null;
            run.DiscoveryOrder = DiscoveryCharacters(run.ActiveDefinition); run.DiscoveryIndex = 0;
            SetStep(CoordinatorStep.DiscoverCache);
            return;
        }
        run.VisitPlanningComplete = true;
        FocusVisitItem(0);
        run.Status = "All item budgets ready. Planning character visits and mixed-item deliveries…";
        run.Step = CoordinatorStep.SelectLeg;
        SaveRun();
    }

    /// <summary>Expose one canonical item to existing progress views; visit state remains authoritative.</summary>
    private void FocusVisitItem(int index)
    {
        var run = Run!;
        var item = run.VisitItems[index];
        run.ActiveItemIndex = index;
        run.Journal = item.Journal; run.Snapshots = item.Snapshots;
        run.Withdrawn = item.Withdrawn; run.Deposited = item.Deposited;
        run.AutomaticRelays = item.AutomaticRelays; run.RelayCapacity = item.RelayCapacity;
        run.CollectionRoute = item.CollectionRoute; run.SkippedCharacters = item.SkippedCharacters;
        run.ItemSkipReason = item.ItemSkipReason; run.RelayFlushCharacterId = item.RelayFlushCharacterId;
    }

    private bool PrepareVisitRelays()
    {
        var run = Run!;
        var available = AvailablePlanningDefinition();
        var required = JobPlanner.RequiredRelaySourcePeers(available, run.Snapshots.Values);
        var funding = JobPlanner.PreviewFunding(available, run.Snapshots.Values);
        foreach (var sourcePeer in required)
        {
            var peerId = sourcePeer == Settings.PeerId ? RemoteRoster!.PeerId : Settings.PeerId;
            var roster = AllCharacters().Where(entry => !run.SkippedCharacters.ContainsKey(entry.Character.ContentId)).ToArray();
            var quantity = funding.Allocations.Where(allocation => run.Snapshots[allocation.SourceCharacterId].PeerId == sourcePeer
                    && run.Snapshots[allocation.RecipientCharacterId].PeerId == sourcePeer)
                .Aggregate(0UL, (sum, allocation) => checked(sum + allocation.Quantity));
            var candidates = AutomaticRelaySelector.Rank(peerId, roster, preferredWorld: PreferredMeetingWorld(funding),
                requiredQuantity: quantity, stackSize: PlanningItemStackSize());
            var previous = run.VisitItems.Values.SelectMany(item => item.AutomaticRelays)
                .Where(pair => pair.Key == peerId).Select(pair => pair.Value).FirstOrDefault();
            var chosen = candidates.FirstOrDefault(character => character.ContentId == previous) ?? candidates.FirstOrDefault();
            if (chosen == null)
            {
                if (AutomaticRelaySelector.Rank(peerId, AllCharacters()).Any(character => run.SkippedCharacters.ContainsKey(character.ContentId)))
                {
                    run.ItemSkipReason = "Item skipped: every known relay on the other PC has unavailable inventory data.";
                    AdvanceVisitPlanning(); return false;
                }
                throw new InvalidOperationException("No automatic relay has an AutoRetainer inventory-slot count on the other PC. Refresh its roster after AutoRetainer records an eligible alt.");
            }
            run.AutomaticRelays[peerId] = chosen.ContentId;
            if (!run.Snapshots.ContainsKey(chosen.ContentId))
                run.Snapshots[chosen.ContentId] = new()
                {
                    CharacterId = chosen.ContentId, PeerId = peerId, Name = chosen.Name, World = chosen.World,
                    BagCapacity = (uint)Math.Min(uint.MaxValue, (ulong)chosen.InventoryFreeSlots!.Value * PlanningItemStackSize()),
                    Verification = InventoryVerification.Cached,
                };
        }
        return true;
    }

    private void RefreshVisitResults()
    {
        var run = Run!;
        run.CompletedItems = run.VisitItems.Values.Where(item => item.Journal == null || new TransferJobLedger(item.Journal).IsComplete)
            .OrderBy(item => item.ItemIndex).Select(item => new CoordinatorItemResult
            {
                ItemIndex = item.ItemIndex, Definition = item.Definition, Journal = item.Journal, Snapshots = item.Snapshots,
                Withdrawn = item.Withdrawn, Deposited = item.Deposited, AutomaticRelays = item.AutomaticRelays,
                SkippedCharacters = item.SkippedCharacters, ItemSkipReason = item.ItemSkipReason,
            }).ToList();
    }

    private void FinishVisitWorkflow()
    {
        var run = Run!;
        if (run.Pending.Count != 0 || run.VisitBatch != null) throw new InvalidOperationException("Pending visit work must finish first.");
        RefreshVisitResults();
        if (run.CompletedItems.Count != run.TotalItemCount) throw new InvalidDataException("A planned item still has unfinished deliveries.");
        run.Step = CoordinatorStep.Finished;
        run.Paused = run.HasShortages;
        run.Status = run.HasShortages ? "Available items delivered. Some requested quantities remain short; see item results."
            : run.HasSkippedCharacters ? "Visits finished with skipped characters or items; see the recorded reasons."
            : "Transfer job complete: all items' planned final deliveries are verified.";
        SaveRun();
    }
}
