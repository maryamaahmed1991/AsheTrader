namespace AsheTrader.Workflow.Native;

public sealed partial class GameWorkflowExecutor
{
    private readonly SaddlebagReadSession saddlebagReadSession = new();
    private readonly SaddlebagOpeningGate saddlebagOpening = new();
    private bool ownsSaddlebagWindow;
    private bool saddlebagRequested;
    private ItemInventory? saddlebagObservation;
    private RetainerTransferConfirmation? saddlebagConfirmation;
    private long saddlebagStableSince;
    private (uint Bags, uint Saddle)? saddlebagStableCounts;
    private byte? previousSaddlebagTab;
    private bool premiumRequested;

    private void ObserveSaddlebagSession() => saddlebagReadSession.Observe(
        Plugin.ClientState.IsLoggedIn ? Plugin.PlayerState.ContentId : 0,
        Plugin.ClientState.TerritoryType, NativeSaddlebags.Premium, NativeSaddlebags.Loaded);

    private void ResetSaddlebags()
    {
        ownsSaddlebagWindow = saddlebagRequested = false;
        saddlebagObservation = null; saddlebagConfirmation = null;
        saddlebagStableCounts = null; saddlebagStableSince = 0;
        previousSaddlebagTab = null; premiumRequested = false;
        saddlebagOpening.Reset();
    }

    private void UpdateSaddlebags()
    {
        var op = operation!;
        if (!PlayerReady || NativeRetainers.HasActiveCutscene) { saddlebagOpening.Interrupt(); return; }
        if (!NativeSaddlebags.Unlocked)
        {
            if (op.Kind == GameOperationKind.ArrangeSaddlebags) throw new InvalidOperationException("This character's saddlebags are unavailable; saved cargo was retained.");
            Finish(true); return;
        }
        var silentRead = op.Kind == GameOperationKind.Inspect && saddlebagReadSession.CanReadSilently;
        if (!silentRead)
        {
            var decision = saddlebagOpening.Observe(Environment.TickCount64,
                Plugin.ObjectTable.LocalPlayer?.IsTargetable == true
                    && !NativeRetainers.BellInteractionBlocked && !NativeSaddlebags.Pending,
                NativeSaddlebags.Visible, NativeSaddlebags.Open);
            if (decision == SaddlebagOpeningDecision.Exhausted)
                throw new InvalidOperationException("The saddlebag window did not open after five automatic attempts. Check whether this character can open saddlebags here; no item movement was repeated by these attempts.");
            if (decision == SaddlebagOpeningDecision.Wait) return;
            if (decision == SaddlebagOpeningDecision.Open)
            {
                saddlebagRequested = ownsSaddlebagWindow = true;
                saddlebagOpening.RecordAttempt(Environment.TickCount64);
                saddlebagStableCounts = null;
                premiumRequested = false;
                Trace($"saddlebag window open requested; attempt={saddlebagOpening.Attempts}/{SaddlebagOpeningGate.MaximumAttempts}; awaiting visible ready addon");
                NativeSaddlebags.Show();
                actionAfter = Environment.TickCount64 + 300;
                return;
            }
            if (!saddlebagRequested) Trace("saddlebag window already open; using existing window");
            saddlebagRequested = true;
        }
        if (!NativeSaddlebags.Loaded && NativeSaddlebags.Premium && !premiumRequested)
        {
            previousSaddlebagTab = NativeSaddlebags.Tab; premiumRequested = true;
            NativeSaddlebags.Tab = 1; actionAfter = Environment.TickCount64 + 500; return;
        }
        if (!NativeSaddlebags.Loaded || NativeSaddlebags.Pending) return;
        if (!silentRead) saddlebagReadSession.ConfirmLoaded();
        var bags = NativeRetainers.ReadBags(op.ItemId, op.HighQuality);
        var saddle = NativeSaddlebags.Read(op.ItemId, op.HighQuality);
        if (silentRead && !saddlebagRequested)
        {
            // Like ordinary bag inspection, read current loaded slots immediately.
            // Pending native operations were checked above. This operation opened no
            // window and moved no items, so there is no new load/move to stabilize.
            saddlebagObservation = saddle;
            Trace("saddlebag inspection read live loaded containers; no additional settling delay");
            Finish(true);
            return;
        }
        var now = Environment.TickCount64;
        if (saddlebagConfirmation != null)
        {
            if (!saddlebagConfirmation.Observe(now, bags.AllItems, saddle.AllItems)) return;
            saddlebagConfirmation = null;
            // Verified progress resets the inactivity deadline. Large premium-bag
            // loads must not time out just because many stacks need moving.
            stageStarted = now;
            saddlebagStableCounts = (bags.Quantity, saddle.Quantity);
            saddlebagStableSince = now - 300;
            Trace($"saddlebag movement verified; bags={bags.Quantity}; saddlebag={saddle.Quantity}");
        }
        var counts = (bags.Quantity, saddle.Quantity);
        if (saddlebagStableCounts != counts)
        { saddlebagStableCounts = counts; saddlebagStableSince = now; return; }
        if (now - saddlebagStableSince < 300) return;
        saddlebagObservation = saddle;
        if (op.Kind == GameOperationKind.Inspect) { Finish(true); return; }
        if ((ulong)bags.Quantity + saddle.Quantity != op.CargoExpectedTotal)
            throw new InvalidOperationException("Cargo inventory changed outside the saved saddlebag move. No quantity was guessed; keep this character logged in and inspect its items.");
        if (saddle.Quantity == op.Quantity) { Finish(true); return; }
        var packing = saddle.Quantity < op.Quantity;
        var allowance = packing ? op.Quantity - saddle.Quantity : saddle.Quantity - op.Quantity;
        var move = SaddlebagCargoPolicy.NextMove(NativeSaddlebags.Slots(!packing), NativeSaddlebags.Slots(packing),
            op.ItemId, op.HighQuality, allowance);
        if (move == null) { Trace("No whole-stack or merge move fits the cargo allowance; continuing with available bag space."); Finish(true); return; }
        // The absolute goal and starting conserved total were durably saved by the worker before Execute.
        saddlebagConfirmation = new(now, bags.AllItems, saddle.AllItems, new(op.ItemId, op.HighQuality), move.Quantity, packing);
        Trace($"saddlebag move; packing={packing}; quantity={move.Quantity}; source={move.Source.Container}/{move.Source.Slot}; target={move.Destination.Container}/{move.Destination.Slot}");
        movementIssued = true;
        NativeSaddlebags.Move(move);
        actionAfter = now + 200;
    }

    private void ApplySaddlebagResult(GameOperationResult result)
    {
        if (saddlebagObservation != null)
        {
            result.SaddlebagsAvailable = true;
            result.PremiumSaddlebagsAvailable = NativeSaddlebags.Premium;
            result.SaddlebagQuantity = saddlebagObservation.Quantity;
            result.SaddlebagCapacity = saddlebagObservation.Capacity;
            result.FreeSaddlebagSlots = saddlebagObservation.FreeSlots;
        }
        if (ownsSaddlebagWindow) NativeSaddlebags.Close();
        else if (previousSaddlebagTab.HasValue) NativeSaddlebags.Tab = previousSaddlebagTab;
        ownsSaddlebagWindow = false;
    }
}
