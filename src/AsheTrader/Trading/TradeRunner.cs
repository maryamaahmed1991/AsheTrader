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
    private long nextActionAt;
    private long refreshAt;
    private int receivedBatches;
    private bool finalConfirmationSent;
    private string offerFingerprint = string.Empty;
    private long offerStableSince;
    private bool singleBatch;
    private IReadOnlyList<TradeLine> expectedIncoming = [];
    public bool Succeeded { get; private set; }
    public uint VerifiedQuantity { get; private set; }
    public bool ReceiverReady => stage == Stage.ReceiverIdle && IsReceiver;

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
            if (stop) { Halt("Stopped. Review inventory before starting another transfer."); return; }
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
            if (Plugin.PlayerState.ContentId != localContentId || partner == null || native.Resolve(partner) == null)
                throw new InvalidOperationException("Character or selected trade partner changed.");
            if (now < nextActionAt) return;
            if (now - enteredAt > (stage == Stage.ReceiverIdle ? 300_000 : 30_000))
                throw new TimeoutException("No verified progress within the timeout. The operation was not retried.");

            var trade = native.ReadTrade();
            if (trade.IsOpen && trade.PartnerEntityId != partner.EntityId)
                throw new InvalidOperationException("A different character opened a trade. This run was stopped.");
            if (addonId != 0 && trade.IsOpen && addonId != trade.AddonId)
                throw new InvalidOperationException("The trade window was replaced unexpectedly.");

            switch (stage)
            {
                case Stage.Prepare: Prepare(); break;
                case Stage.AwaitOpen:
                    if (!trade.IsOpen) break;
                    RequireEmpty(trade);
                    addonId = trade.AddonId;
                    Transition(Stage.Fill, "Filling the outgoing trade…");
                    break;
                case Stage.Fill:
                    RequireOpen(trade);
                    if (trade.SyncPending) break;
                    if (slotIndex == batch!.Entries.Count)
                    {
                        Transition(Stage.SenderReady, "Checking the offer before trading…", 500);
                        break;
                    }
                    RequirePartialOffer(trade, slotIndex);
                    var entry = batch.Entries[slotIndex];
                    var quantityDialog = native.AddItem(entry);
                    Transition(quantityDialog ? Stage.AwaitQuantity : Stage.AwaitSlot, $"Adding item {slotIndex + 1}/{batch.Entries.Count}…", 250);
                    break;
                case Stage.AwaitQuantity:
                    RequireOpen(trade);
                    if (native.SubmitQuantity(batch!.Entries[slotIndex].Quantity, addonId))
                        Transition(Stage.AwaitSlot, "Waiting for the game to apply the quantity…", 350);
                    break;
                case Stage.AwaitSlot:
                    RequireOpen(trade);
                    if (trade.SyncPending || native.HasNumeric) break;
                    if (!LocalMatches(trade, slotIndex + 1)) break;
                    slotIndex++;
                    Transition(Stage.Fill, "Continuing the outgoing offer…", 250);
                    break;
                case Stage.SenderReady:
                    RequireOpen(trade);
                    if (trade.SyncPending) break;
                    RequireSenderOffer(trade);
                    if (native.HasModal) throw new InvalidOperationException("An unexpected dialog appeared before the trade was ready.");
                    if (native.ClickTrade()) Transition(Stage.Confirm, "Waiting for the selected partner to confirm…");
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
                    if (trade.SyncPending || trade.RemoteItems.Count == 0 || !IsLocked(trade.RemoteState)) break;
                    var fingerprint = Fingerprint(trade.RemoteItems);
                    if (fingerprint != offerFingerprint)
                    {
                        offerFingerprint = fingerprint;
                        offerStableSince = now;
                        break;
                    }
                    if (now - offerStableSince < 750) break;
                    incoming = trade.RemoteItems.ToArray();
                    if (singleBatch && !IncomingMatchesExpected(incoming))
                        throw new InvalidOperationException("Incoming items do not match the coordinator's agreed batch.");
                    if (native.ClickTrade()) Transition(Stage.Confirm, "Confirming the locked incoming offer…");
                    break;
                case Stage.Confirm:
                    RequireOpen(trade);
                    ValidateFinalOffer(trade);
                    if (trade.SyncPending || !IsLocked(trade.LocalState) || !IsLocked(trade.RemoteState)) break;
                    if (native.ConfirmTrade())
                    {
                        finalConfirmationSent = true;
                        Transition(Stage.Completion, "Waiting for trade completion…", 250);
                    }
                    break;
                case Stage.Completion:
                    // The server may clear the offer just before the window closes.
                    // We make no more native actions after final confirmation.
                    if (trade.IsOpen) break;
                    Transition(Stage.Settlement, "Verifying the inventory changes…", 750);
                    break;
                case Stage.Settlement:
                    if (trade.IsOpen) throw new InvalidOperationException("Another trade opened before inventory verification completed.");
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
        if (native.ReadTrade().IsOpen || native.HasModal)
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
        addonId = 0;
        finalConfirmationSent = false;
        offerFingerprint = string.Empty;
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
        if (native.HasModal || native.ReadTrade().IsOpen)
            throw new InvalidOperationException("An unexpected window prevents the next trade.");
        native.RequestTrade(partner!);
        Transition(Stage.AwaitOpen, $"Requesting trade with {partner!.Name}…");
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
        if (!matches)
        {
            if (Environment.TickCount64 - enteredAt < 5000) return;
            throw new InvalidOperationException("Trade completion could not be reconciled with inventory. Progress was not changed; review the items before restarting.");
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
            offerFingerprint = string.Empty;
            Transition(Stage.ReceiverIdle, $"Received {receivedBatches} verified batch(es). Waiting for {partner!.Name}…", 1000);
        }
        else Transition(Stage.Prepare, $"Verified batch {progress!.CompletedBatches}. Preparing the next…", 1200);
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
    private void Transition(Stage next, string status, int delayMs = 0)
    {
        stage = next;
        enteredAt = Environment.TickCount64;
        nextActionAt = enteredAt + delayMs;
        Status = status;
    }
    private void Halt(string reason)
    {
        Succeeded = false;
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
    public void Dispose()
    {
        lock (commandGate) { disposed = true; requested = null; startPending = false; }
        stage = Stage.Idle;
        Status = "Stopped (plugin unloaded).";
        // Unloading stops automation immediately; the game retains control of any open trade.
    }
}
