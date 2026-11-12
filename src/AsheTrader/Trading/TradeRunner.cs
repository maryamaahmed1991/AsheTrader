using AsheTrader.Core;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AsheTrader.Trading;

/// <summary>
/// One-way transfers with a fixed nearby partner. UI calls queue commands; native work
/// happens exclusively in Update. No run is restored after loading the plugin.
/// Receiver mode permits incoming items from the chosen character, never outgoing items
/// or gil. It does not assert that the incoming offer belongs to a particular saved list.
/// </summary>
public sealed class TradeRunner : IDisposable
{
    private enum Stage { Idle, Prepare, AwaitOpen, Fill, AwaitQuantity, AwaitSlot, SenderReady, ReceiverIdle, ReceiverOffer, Confirm, Completion, Settlement }
    private sealed record RunRequest(bool Receive, TradePartner Partner, IReadOnlyList<TradeLine> Lines, bool SingleBatch);
    private readonly NativeTradeAdapter native = new();
    private readonly TradeOpeningGate openingGate = new();
    private readonly TradePacing pacing = new();
    private readonly object commandGate = new();
    private RunRequest? requested;
    private bool stopRequested;
    private volatile bool startPending;
    private volatile bool disposed;
    private volatile Stage stage;
    private TradePartner? partner;
    private TradeProgressTracker? progress;
    private TradeBatch? batch;
    private IReadOnlyList<InventoryStack> inventory = [];
    private Dictionary<ItemKey, ulong> before = [];
    private IReadOnlyList<OfferedItem> incoming = [];
    private ulong localContentId;
    private ushort addonId;
    private int slotIndex;
    private long enteredAt;
    private long refreshAt;
    private int receivedBatches;
    private bool finalConfirmationSent;
    private bool singleBatch;
    private IReadOnlyList<TradeLine> expectedIncoming = [];
    private long nextDiagnosticAt;
    private string lastStop = "No trade has stopped in this session.";
    public Action<string>? Diagnostic { get; set; }
    public bool Succeeded { get; private set; }
    public uint VerifiedQuantity { get; private set; }
    public bool ReceiverReady => stage == Stage.ReceiverIdle && IsReceiver;
    /// <summary>Read on the framework thread before tallying an interrupted trade.</summary>
    public bool IsInventoryTallyReady => !IsRunning && native.IsInventoryTallyReady;
    public string InventoryTallyBlockReason => IsRunning ? "trade automation is still stopping" : native.ReadActivity().BlockReason;

    public string Status { get; private set; } = "Idle";
    public bool IsRunning => stage != Stage.Idle || startPending;
    public bool IsReceiver { get; private set; }
    public int CompletedBatches => IsReceiver ? receivedBatches : progress?.CompletedBatches ?? 0;
    public IReadOnlyList<TradeLine> Remaining => progress?.Remaining ?? [];

    /// <summary>Latest immutable carried-inventory snapshot, refreshed on the framework thread.</summary>
    public IReadOnlyList<InventoryStack> ReadInventory() => inventory;
    public IReadOnlyList<InventoryStack> SnapshotInventoryNow() => inventory = native.ReadInventory();

    public void StartSending(IEnumerable<TradeLine> lines, IPlayerCharacter target) => Queue(false, lines, target);
    public void ArmReceiver(IPlayerCharacter partner) => Queue(true, [], partner);
    public void SendBatch(TradeLine line, IPlayerCharacter target) => Queue(false, [line], target, true);
    public void ReceiveBatch(TradeLine line, IPlayerCharacter target) => Queue(true, [line], target, true);
    public TradeBatch PreviewBatch(TradeLine line) => TradePlanner.Plan([line], native.ReadInventory());

    private void Queue(bool receive, IEnumerable<TradeLine> lines, IPlayerCharacter target, bool oneBatch = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        var copied = lines.Select(line => new TradeLine { ItemId = line.ItemId, HighQuality = line.HighQuality, Quantity = line.Quantity }).ToArray();
        lock (commandGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsRunning) throw new InvalidOperationException("Stop the current run first.");
            requested = new(receive, new(target.EntityId, target.Name.TextValue, target.HomeWorld.RowId), copied, oneBatch);
            Succeeded = false;
            VerifiedQuantity = 0;
            startPending = true;
            Status = receive ? "Arming receiver…" : "Preparing transfer…";
        }
    }

    public void Stop()
    {
        lock (commandGate) { requested = null; stopRequested = true; }
    }

    /// <summary>Call once per Dalamud framework update. Never call from an async continuation.</summary>
    public void Update()
    {
        if (disposed) return;
        try
        {
            RunRequest? request;
            bool stop;
            lock (commandGate)
            {
                request = requested;
                requested = null;
                stop = stopRequested;
                stopRequested = false;
            }
            if (stop) { Halt("Stopped. Paired jobs tally inventories before continuing."); return; }
            var now = Environment.TickCount64;
            if (Plugin.ObjectTable.LocalPlayer == null || !Plugin.ClientState.IsLoggedIn)
            {
                inventory = [];
                if (stage != Stage.Idle || request != null) Halt("Stopped because the character logged out.");
                return;
            }
            if (now >= refreshAt)
            {
                refreshAt = now + 1000;
                try { inventory = native.ReadInventory(); }
                catch when (stage == Stage.Idle && request == null)
                {
                    inventory = [];
                    return; // Inventory can be transiently unavailable during login/loading.
                }
            }
            if (request != null)
            {
                Begin(request);
                startPending = false;
            }
            if (stage == Stage.Idle) return;
            if (now >= nextDiagnosticAt) CaptureDiagnosticSnapshot("periodic wait");
            if (Plugin.PlayerState.ContentId != localContentId || partner == null || native.Resolve(partner) == null)
                throw new InvalidOperationException("Character or selected trade partner changed.");
            if (now - enteredAt > (stage == Stage.ReceiverIdle ? 300_000 : 30_000))
                throw new TimeoutException($"Timed out at trade stage {stage}. Opening requests={openingGate.Requests}; acknowledged={openingGate.Acknowledged}. No item offer or completed batch was retried. Open Activity and copy diagnostics.");

            var trade = native.ReadTrade();
            if (trade.IsOpen && trade.PartnerEntityId != partner.EntityId)
                throw new InvalidOperationException("A different character opened a trade. This run was stopped.");
            if (addonId != 0 && trade.IsOpen && addonId != trade.AddonId)
                throw new InvalidOperationException("The trade window was replaced unexpectedly.");
            // A visible addon that is rebuilding controls is not evidence that a trade closed.
            if (!trade.IsOpen && native.HasTradeUi && stage is Stage.Fill or Stage.AwaitQuantity
                or Stage.AwaitSlot or Stage.SenderReady or Stage.ReceiverOffer or Stage.Confirm)
            {
                if (stage == Stage.ReceiverOffer) pacing.ObserveIncoming(now, string.Empty, false);
                return;
            }

            switch (stage)
            {
                case Stage.Prepare: Prepare(); break;
                case Stage.AwaitOpen:
                    var opening = native.ReadOpening(partner);
                    var decision = openingGate.Observe(now, opening.Ready, opening.Acknowledged);
                    if (!trade.IsOpen)
                    {
                        if (opening.Acknowledged && opening.RawPartner != 0 && opening.RawPartner != partner.EntityId)
                            throw new InvalidOperationException("The game acknowledged a trade with a different partner.");
                        if (decision == TradeOpeningDecision.Exhausted)
                            throw new TimeoutException($"The game did not acknowledge {openingGate.Requests} trade opening requests. {opening.Describe()}. The batch was not repeated.");
                        if (decision == TradeOpeningDecision.Request)
                        {
                            if (!pacing.CanRequest(now))
                            {
                                Status = "Waiting for the previous trade request cooldown…";
                                break;
                            }
                            var current = NativeTradeAdapter.Totals(native.ReadInventory());
                            if (before.Keys.Union(current.Keys).Any(key => before.GetValueOrDefault(key) != current.GetValueOrDefault(key)))
                                throw new InvalidOperationException("Inventory changed before the trade opened. The opening request was not repeated.");
                            openingGate.RecordRequest(now);
                            pacing.RecordRequest(Environment.TickCount64);
                            Trace($"request dispatch attempt={openingGate.Requests}; partner={partner.EntityId}; {opening.Describe()}");
                            native.RequestTrade(partner);
                            LatchOpeningAcknowledgement(native.ReadOpening(partner));
                            Status = $"Requesting trade with {partner.Name}… (attempt {openingGate.Requests}/{TradeOpeningGate.MaximumRequests})";
                            CaptureDiagnosticSnapshot("after request dispatch");
                        }
                        else Status = opening.Acknowledged ? "Waiting for the acknowledged trade window…"
                            : opening.Ready ? "Waiting for stable trade readiness…" : $"Waiting to request trade: {opening.BlockReason} (distance {opening.Distance:F1}).";
                        break;
                    }
                    RequireEmpty(trade);
                    addonId = trade.AddonId;
                    Transition(Stage.Fill, "Filling the outgoing trade…");
                    break;
                case Stage.Fill:
                    RequireOpen(trade);
                    if (trade.SyncPending) break;
                    if (slotIndex == batch!.Entries.Count)
                    {
                        Transition(Stage.SenderReady, "Checking the offer before trading…");
                        break;
                    }
                    RequirePartialOffer(trade, slotIndex);
                    if (!pacing.CanOffer(now)) break;
                    var entry = batch.Entries[slotIndex];
                    Trace($"offer item slot={slotIndex}; item={entry.ItemId}; HQ={entry.HighQuality}; quantity={entry.Quantity}; source={entry.Container}/{entry.Slot}");
                    pacing.RecordOffer(Environment.TickCount64);
                    var quantityDialog = native.AddItem(entry);
                    Transition(quantityDialog ? Stage.AwaitQuantity : Stage.AwaitSlot, $"Adding item {slotIndex + 1}/{batch.Entries.Count}…");
                    break;
                case Stage.AwaitQuantity:
                    RequireOpen(trade);
                    if (native.SubmitQuantity(batch!.Entries[slotIndex].Quantity, addonId))
                    {
                        Trace($"quantity submitted slot={slotIndex}; quantity={batch.Entries[slotIndex].Quantity}");
                        Transition(Stage.AwaitSlot, "Waiting for the game to apply the quantity…");
                    }
                    break;
                case Stage.AwaitSlot:
                    RequireOpen(trade);
                    if (trade.SyncPending || native.HasNumeric) break;
                    if (!LocalMatches(trade, slotIndex + 1)) break;
                    pacing.ConfirmOfferedItem();
                    slotIndex++;
                    Transition(Stage.Fill, "Continuing the outgoing offer…");
                    break;
                case Stage.SenderReady:
                    RequireOpen(trade);
                    if (trade.SyncPending) break;
                    RequireSenderOffer(trade);
                    if (native.HasModal) throw new InvalidOperationException("An unexpected dialog appeared before the trade was ready.");
                    if (native.ClickTrade()) { Trace("outgoing offer locked"); Transition(Stage.Confirm, "Waiting for the selected partner to confirm…"); }
                    break;
                case Stage.ReceiverIdle:
                    if (!trade.IsOpen) break;
                    if (native.HasModal) throw new InvalidOperationException("Another dialog is open; receiving was stopped.");
                    RequireNoOutgoing(trade);
                    addonId = trade.AddonId;
                    before = NativeTradeAdapter.Totals(native.ReadInventory());
                    Transition(Stage.ReceiverOffer, "Waiting for the sender to lock the incoming offer…");
                    break;
                case Stage.ReceiverOffer:
                    RequireOpen(trade);
                    RequireNoOutgoing(trade);
                    if (!pacing.ObserveIncoming(now, Fingerprint(trade.RemoteItems),
                            !trade.SyncPending && trade.RemoteItems.Count > 0
                            && IsLocked(trade.RemoteState) && !native.HasModal)) break;
                    incoming = trade.RemoteItems.ToArray();
                    if (singleBatch && !IncomingMatchesExpected(incoming))
                        throw new InvalidOperationException("Incoming items do not match the coordinator's agreed batch.");
                    if (native.ClickTrade()) { Trace("incoming offer accepted"); Transition(Stage.Confirm, "Confirming the locked incoming offer…"); }
                    break;
                case Stage.Confirm:
                    RequireOpen(trade);
                    ValidateFinalOffer(trade);
                    if (trade.SyncPending || !IsLocked(trade.LocalState) || !IsLocked(trade.RemoteState)) break;
                    if (native.ConfirmTrade())
                    {
                        finalConfirmationSent = true;
                        Trace("final confirmation dispatched; completion still requires closed UI and exact inventory deltas");
                        Transition(Stage.Completion, "Waiting for trade completion…");
                    }
                    break;
                case Stage.Completion:
                    // The server may clear the offer just before the window closes.
                    // We make no more native actions after final confirmation.
                    // Let the previous trade's native state clear before another batch can
                    // mistake it for acknowledgment of a new opening request.
                    if (!native.IsInventoryTallyReady) break;
                    Transition(Stage.Settlement, "Verifying the inventory changes…");
                    break;
                case Stage.Settlement:
                    if (native.HasTradeUi) throw new InvalidOperationException("Another trade opened before inventory verification completed.");
                    if (!native.IsInventoryTallyReady) { pacing.ObserveSettlement(now, false); break; }
                    Settle();
                    break;
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.Warning(exception, "Ashe's Trader stopped the active transfer.");
            Halt(exception.Message);
        }
    }

    private void Begin(RunRequest request)
    {
        if (native.HasTradeUi || native.HasModal)
            throw new InvalidOperationException("Close the current trade and confirmation windows before starting.");
        if (request.Partner.EntityId == Plugin.ObjectTable.LocalPlayer!.EntityId)
            throw new InvalidOperationException("Select the other account's character.");
        partner = request.Partner;
        if (native.Resolve(partner) == null) throw new InvalidOperationException("Select a nearby player character.");
        localContentId = Plugin.PlayerState.ContentId;
        IsReceiver = request.Receive;
        singleBatch = request.SingleBatch;
        expectedIncoming = IsReceiver ? request.Lines : [];
        progress = IsReceiver ? null : new TradeProgressTracker(request.Lines);
        receivedBatches = 0;
        openingGate.Reset();
        pacing.BeginBatch();
        nextDiagnosticAt = 0;
        addonId = 0;
        finalConfirmationSent = false;
        Transition(IsReceiver ? Stage.ReceiverIdle : Stage.Prepare,
            IsReceiver ? $"Receiving items only from {partner.Name}. Armed for five minutes." : "Planning the first batch…");
    }

    private void Prepare()
    {
        if (progress!.IsComplete) { Finish($"Finished: {progress.CompletedBatches} verified batch(es)."); return; }
        inventory = native.ReadInventory();
        batch = TradePlanner.Plan(progress.Remaining, inventory);
        if (batch.HasShortage) throw new InvalidOperationException(batch.Shortages[0].ToString());
        if (!batch.HasItems) throw new InvalidOperationException("No eligible items remain to trade.");
        if (singleBatch && batch.TotalQuantity != progress.Remaining.Aggregate(0UL, (sum, line) => sum + line.Quantity))
            throw new InvalidOperationException("The agreed batch no longer fits into five trade slots; it must be replanned.");
        before = NativeTradeAdapter.Totals(inventory);
        addonId = 0;
        slotIndex = 0;
        finalConfirmationSent = false;
        if (native.HasModal || native.HasTradeUi)
            throw new InvalidOperationException("An unexpected window prevents the next trade.");
        openingGate.Reset();
        pacing.BeginBatch();
        native.SelectPartner(partner!);
        Trace($"partner selected for a later request; partner={partner!.EntityId}; plannedQuantity={batch.TotalQuantity}; slots={batch.Entries.Count}");
        Transition(Stage.AwaitOpen, "Waiting for the selected partner and game to be ready…");
    }

    private void Settle()
    {
        var current = native.ReadInventory();
        var after = NativeTradeAdapter.Totals(current);
        bool matches;
        if (IsReceiver)
        {
            var expected = incoming.GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.Aggregate(0UL, (sum, item) => sum + item.Quantity));
            matches = before.Keys.Union(after.Keys).Union(expected.Keys).All(key =>
                after.GetValueOrDefault(key) >= before.GetValueOrDefault(key) &&
                after.GetValueOrDefault(key) - before.GetValueOrDefault(key) == expected.GetValueOrDefault(key));
        }
        else
        {
            var expected = batch!.Entries.GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.Aggregate(0UL, (sum, item) => sum + item.Quantity));
            matches = before.Keys.Union(after.Keys).Union(expected.Keys).All(key =>
                before.GetValueOrDefault(key) >= after.GetValueOrDefault(key) &&
                before.GetValueOrDefault(key) - after.GetValueOrDefault(key) == expected.GetValueOrDefault(key));
        }
        if (!pacing.ObserveSettlement(Environment.TickCount64, matches))
        {
            if (matches || Environment.TickCount64 - enteredAt < 5000) return;
            throw new InvalidOperationException("Trade completion could not be reconciled with inventory. The paired workflow will tally both characters before continuing.");
        }
        if (!finalConfirmationSent) throw new InvalidOperationException("Trade completion was not initiated by this run.");
        // There is no stable public trade-success event here. Completion evidence is:
        // exact validated offers, our final confirmation, closed trade UI, and exact full
        // inventory deltas. Any cancelled/ambiguous run is stopped, never blindly retried.
        if (!IsReceiver && !progress!.TryCommit(batch!, TradeOutcome.Completed, before, after, out var error))
            throw new InvalidOperationException(error);
        inventory = current;
        VerifiedQuantity = checked(VerifiedQuantity + (uint)(IsReceiver
            ? incoming.Aggregate(0UL, (sum, item) => sum + item.Quantity) : batch!.TotalQuantity));
        Trace($"settlement verified; receiver={IsReceiver}; verifiedQuantity={VerifiedQuantity}; exactInventoryDeltas=True");
        addonId = 0;
        finalConfirmationSent = false;
        if (singleBatch)
        {
            if (IsReceiver) receivedBatches++;
            Succeeded = true;
            Finish($"Verified one agreed batch ({VerifiedQuantity:N0} items).");
            return;
        }
        if (IsReceiver)
        {
            receivedBatches++;
            pacing.BeginBatch();
            Transition(Stage.ReceiverIdle, $"Received {receivedBatches} verified batch(es). Waiting for {partner!.Name}…");
        }
        else Transition(Stage.Prepare, $"Verified batch {progress!.CompletedBatches}. Preparing the next…");
    }

    private static bool IsLocked(TradeState state) => state is TradeState.LockedIn or TradeState.WaitingForConfirmation or TradeState.Confirmed;
    private bool IncomingMatchesExpected(IReadOnlyList<OfferedItem> offered)
    {
        var actual = offered.GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Aggregate(0UL, (sum, y) => sum + y.Quantity));
        return expectedIncoming.Count > 0 && actual.Count == expectedIncoming.Count
            && expectedIncoming.All(x => actual.GetValueOrDefault(new ItemKey(x.ItemId, x.HighQuality)) == x.Quantity);
    }
    private static string Fingerprint(IEnumerable<OfferedItem> items) => string.Join("|", items.Select(item => $"{item.ItemId}:{item.HighQuality}:{item.Quantity}"));
    private void RequireOpen(NativeTradeSnapshot trade)
    {
        if (!trade.IsOpen || trade.PartnerEntityId != partner!.EntityId)
            throw new InvalidOperationException("Trade closed or was cancelled before completion.");
    }
    private static void RequireEmpty(NativeTradeSnapshot trade)
    {
        if (trade.LocalItems.Count != 0 || trade.RemoteItems.Count != 0 || trade.LocalGil != 0 || trade.RemoteGil != 0)
            throw new InvalidOperationException("A new trade already contains an unexpected offer.");
    }
    private static void RequireNoOutgoing(NativeTradeSnapshot trade)
    {
        if (trade.LocalItems.Count != 0 || trade.LocalGil != 0 || trade.RemoteGil != 0)
            throw new InvalidOperationException("Receiver mode accepts items only, with no outgoing items or gil.");
    }
    private bool LocalMatches(NativeTradeSnapshot trade, int count)
    {
        if (trade.LocalItems.Count != count) return false;
        var expected = batch!.Entries.Take(count).Select(entry => new OfferedItem(entry.ItemId, entry.HighQuality, entry.Quantity));
        return trade.LocalItems.SequenceEqual(expected);
    }
    private void RequirePartialOffer(NativeTradeSnapshot trade, int count)
    {
        if (!LocalMatches(trade, count) || trade.LocalGil != 0 || trade.RemoteGil != 0 || trade.RemoteItems.Count != 0)
            throw new InvalidOperationException("The trade offer differs from the planned one-way transfer.");
    }
    private void RequireSenderOffer(NativeTradeSnapshot trade) => RequirePartialOffer(trade, batch!.Entries.Count);
    private void ValidateFinalOffer(NativeTradeSnapshot trade)
    {
        if (!IsReceiver) RequireSenderOffer(trade);
        else
        {
            RequireNoOutgoing(trade);
            if (!trade.RemoteItems.SequenceEqual(incoming))
                throw new InvalidOperationException("The incoming offer changed after it was locked.");
        }
    }
    private void Transition(Stage next, string status)
    {
        Trace($"stage {stage} -> {next}; previousStageMs={Environment.TickCount64 - enteredAt}; status={status}");
        stage = next;
        enteredAt = Environment.TickCount64;
        Status = status;
    }
    private void Halt(string reason)
    {
        Succeeded = false;
        if (stage != Stage.Idle || startPending)
        {
            lastStop = $"stage={stage}; requests={openingGate.Requests}; acknowledged={openingGate.Acknowledged}; finalConfirmation={finalConfirmationSent}; verifiedQuantity={VerifiedQuantity}; reason={reason}";
            CaptureDiagnosticSnapshot("stopping");
            Trace($"stopped; {lastStop}");
        }
        try { native.Refuse(partner); }
        catch (Exception exception) { Plugin.Log.Warning(exception, "Could not cancel the active trade."); }
        Finish(reason);
    }
    private void Finish(string status)
    {
        stage = Stage.Idle;
        startPending = false;
        Status = status;
        addonId = 0;
        partner = null;
    }

    public void CaptureDiagnosticSnapshot(string reason)
    {
        nextDiagnosticAt = Environment.TickCount64 + 5000;
        try
        {
            Trace($"native activity reason={reason}; stage={stage}; {native.ReadActivity().Describe()}");
            if (partner == null) { Trace($"snapshot reason={reason}; stage={stage}; lastStop=({lastStop})"); return; }
            var opening = native.ReadOpening(partner);
            LatchOpeningAcknowledgement(opening);
            var trade = native.ReadTrade();
            Trace($"snapshot reason={reason}; stage={stage}; receiver={IsReceiver}; elapsedMs={Environment.TickCount64 - enteredAt}; requests={openingGate.Requests}; acknowledged={openingGate.Acknowledged}; slot={slotIndex}; finalConfirmation={finalConfirmationSent}; verifiedQuantity={VerifiedQuantity}; partner={partner.EntityId}; {opening.Describe()}");
            Trace($"offer snapshot open={trade.IsOpen}; uiPresent={native.HasTradeUi}; addon={trade.AddonId}; local=[{Fingerprint(trade.LocalItems)}]; remote=[{Fingerprint(trade.RemoteItems)}]; localGil={trade.LocalGil}; remoteGil={trade.RemoteGil}; numeric={native.HasNumeric}; offerOutstanding={pacing.OfferOutstanding}");
        }
        catch (Exception ex) { Trace($"snapshot unavailable: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void Trace(string message)
    {
        try { Diagnostic?.Invoke(message); } catch { /* Logging cannot change a trade outcome. */ }
    }

    private void LatchOpeningAcknowledgement(TradeOpeningSnapshot opening)
    {
        // Even a transient acknowledgment observed immediately after dispatch or in a
        // snapshot rules out another request. A closed/unready addon cannot erase it.
        if (stage == Stage.AwaitOpen && opening.Acknowledged)
            openingGate.Observe(Environment.TickCount64, false, true);
    }
    public void Dispose()
    {
        lock (commandGate) { disposed = true; requested = null; startPending = false; }
        stage = Stage.Idle;
        Status = "Stopped (plugin unloaded).";
        // Unloading stops automation immediately; the game retains control of any open trade.
    }
}
