using AsheTrader.Core;
using Dalamud.Game.ClientState.Conditions;

namespace AsheTrader.Fuel;

/// <summary>
/// Explicitly started fuel purchases in an already-open supplier shop. Confirmed requests are never
/// repeated; a rejected pre-confirmation click may be retried with unchanged inventory and credits.
/// </summary>
public sealed class FuelRunner
{
    public bool Active { get; private set; }
    public string Status { get; private set; } = "Fuel idle. Configure selected buyers and destinations in Fuel.";
    public uint Purchased { get; private set; }
    public bool CompletedNormally { get; private set; }
    public Action<FuelPurchaseCheckpoint?, uint>? PersistPurchase { get; set; }
    public Action<string>? Diagnostic { get; set; }
    private FuelQueueEntry? queueEntry;
    private ulong characterId;
    private ulong companyId;
    private string houseAddress = string.Empty;
    private nint shopAddress;
    private ushort shopAddonId;
    private FuelShopSnapshot pendingShop;
    private FuelPurchaseCheckpoint? pendingCheckpoint;
    private uint unitPrice;
    private uint creditReserve;
    private uint remaining;
    private uint pendingQuantity;
    private uint beforeQuantity;
    private uint beforeCredits;
    private bool confirmed;
    private long nextActionAt;
    private long transactionExpiresAt;
    private long runExpiresAt;
    private long inventoryRejectionAt, nextSnapshot;
    private FuelPurchaseRetryGate? retryGate;

    internal void SignalInventoryRejection() => Interlocked.Exchange(ref inventoryRejectionAt, Environment.TickCount64);

    internal void CaptureDiagnosticSnapshot(string reason)
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading) return;
        try
        {
            Trace($"snapshot reason={reason}; active={Active}; character={characterId}; FC={companyId}; pending={pendingQuantity}; beforeBags={beforeQuantity}; beforeCredits={beforeCredits}; confirmed={confirmed}; attempts={retryGate?.Attempts ?? 0}; confirmationSeen={retryGate?.ConfirmationObserved ?? false}; evidenceChanged={retryGate?.EvidenceChanged ?? false}; {NativeFuelShop.Describe()}");
        }
        catch (Exception exception) { Trace($"snapshot unavailable: {exception.GetType().Name}"); }
    }

    private void Trace(string message) => Diagnostic?.Invoke(message);

    private void ResetRetry()
    {
        retryGate = null;
        pendingCheckpoint = null;
        Interlocked.Exchange(ref inventoryRejectionAt, 0);
        nextSnapshot = 0;
    }

    public bool Start(CharacterProfile character, HouseProfile house, out string error)
    {
        if (!CanRun(out error)) return false;
        if (Active) { error = "A fuel purchase run is already active."; return false; }
        queueEntry = null;
        ResetRetry();
        CompletedNormally = false;
        if (!character.Fuel.Enabled || character.Fuel.HouseId != house.Id)
        {
            error = "Enable fuel purchasing and assign this house to the character first.";
            return false;
        }
        if (Plugin.PlayerState.ContentId != character.ContentId || character.ContentId == 0)
        {
            error = "Log in on the selected character before starting its fuel purchases.";
            return false;
        }
        if (!TryCaptureCurrentHouse(out var currentHouse, out error)) return false;
        if (currentHouse.Address != house.Address)
        {
            error = "The current workshop does not match this character's configured house.";
            return false;
        }
        if (NativeFuelShop.HasConfirmation)
        {
            error = "Close the existing confirmation dialog before starting fuel purchases.";
            return false;
        }
        if (!NativeFuelShop.TryRead(out var shop, out error)) return false;
        var currentCompany = NativeFuelShop.CurrentCompanyId;
        if (currentCompany == 0) { error = "The active FC identity is unavailable."; return false; }
        FuelPurchasePlan plan;
        try
        {
            plan = FuelPlanner.Plan(character.Fuel, shop.InventoryQuantity, shop.Credits, shop.UnitPrice, shop.Capacity);
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
        Purchased = 0;
        if (!plan.HasPurchases)
        {
            Status = $"No fuel purchases needed ({plan.Status}).";
            error = string.Empty;
            return true;
        }
        characterId = character.ContentId;
        companyId = currentCompany;
        houseAddress = currentHouse.Address;
        shopAddress = shop.Address;
        shopAddonId = shop.AddonId;
        unitPrice = shop.UnitPrice;
        creditReserve = character.Fuel.CreditReserve;
        remaining = plan.Quantity;
        pendingQuantity = 0;
        confirmed = false;
        nextActionAt = Environment.TickCount64;
        runExpiresAt = nextActionAt + (15 * 60 * 1000);
        Active = true;
        Status = $"Purchasing up to {remaining:N0} Ceruleum Tanks at {house.Name}.";
        error = string.Empty;
        return true;
    }

    internal bool StartQueue(FuelQueueEntry entry, uint maximumQuantity, out string error)
    {
        if (!CanRun(out error)) return false;
        if (Active) { error = "A fuel purchase is already active."; return false; }
        CompletedNormally = false;
        if (Plugin.PlayerState.ContentId != entry.CharacterId || entry.FreeCompanyId == 0
            || NativeFuelShop.CurrentCompanyId != entry.FreeCompanyId)
        { error = "The active character or FC does not match this fuel queue entry."; return false; }
        if (!FuelDestinationRunner.IsExpectedLocation(entry))
        { error = "The configured home-world fuel destination is not active."; return false; }
        if (NativeFuelShop.HasConfirmation) { error = "Close the existing confirmation before starting purchases."; return false; }
        if (!NativeFuelShop.TryRead(out var shop, out error)) return false;
        var affordable = shop.Credits > entry.CreditReserve ? (shop.Credits - entry.CreditReserve) / shop.UnitPrice : 0;
        remaining = Math.Min(maximumQuantity, Math.Min(shop.Capacity, affordable));
        ResetRetry();
        queueEntry = entry;
        characterId = entry.CharacterId;
        companyId = entry.FreeCompanyId;
        creditReserve = entry.CreditReserve;
        shopAddress = shop.Address; shopAddonId = shop.AddonId; unitPrice = shop.UnitPrice;
        Purchased = pendingQuantity = 0; confirmed = false;
        if (remaining == 0) { Complete("No further fuel fits the configured budget or inventory."); return true; }
        nextActionAt = Environment.TickCount64;
        runExpiresAt = nextActionAt + (15 * 60 * 1000);
        Active = true;
        Status = $"Purchasing up to {remaining:N0} tanks for {entry.CharacterName}.";
        return true;
    }

    public void Update()
    {
        if (!Active) return;
        if (!CanRun(out var error)) { Halt(error); return; }
        if (Plugin.PlayerState.ContentId != characterId) { Halt("The active character changed."); return; }
        if (NativeFuelShop.CurrentCompanyId != companyId) { Halt("The active FC changed or became unavailable."); return; }
        var now = Environment.TickCount64;
        if (pendingQuantity != 0 && NativeFuelShop.HasConfirmation) retryGate?.ObserveConfirmation();
        if (now >= nextSnapshot)
        {
            nextSnapshot = now + 5_000;
            CaptureDiagnosticSnapshot("periodic wait");
        }
        if (now > runExpiresAt) { Halt("No fuel purchase progress was observed for fifteen minutes."); return; }
        if (now < nextActionAt) return;
        if (queueEntry != null ? !FuelDestinationRunner.IsExpectedLocation(queueEntry)
                || NativeFuelShop.CurrentCompanyId != queueEntry.FreeCompanyId
            : !TryCaptureCurrentHouse(out var house, out error) || house.Address != houseAddress)
        {
            Halt("The configured fuel destination is no longer active.");
            return;
        }
        if (!NativeFuelShop.TryRead(out var shop, out error)) { Halt(error); return; }
        if (shop.Address != shopAddress || shop.AddonId != shopAddonId || shop.UnitPrice != unitPrice)
        {
            Halt("The fuel shop or unit price changed.");
            return;
        }
        if (pendingQuantity != 0)
        {
            var inventoryDelta = (long)shop.InventoryQuantity - beforeQuantity;
            var creditDelta = (long)beforeCredits - shop.Credits;
            var expectedCost = (long)pendingQuantity * unitPrice;
            if (pendingCheckpoint == null) { Halt("The pending fuel purchase checkpoint is unavailable."); return; }
            if (FuelPurchaseRecovery.CanAcknowledge(pendingCheckpoint, characterId, companyId,
                shop.InventoryQuantity, shop.DisplayedInventoryQuantity, shop.Credits, shop.UnitPrice, NativeFuelShop.HasConfirmation))
            {
                if (queueEntry != null) PersistPurchase?.Invoke(null, pendingQuantity);
                Trace($"reconciled quantity={pendingQuantity}; attempts={retryGate?.Attempts ?? 1}; bags={shop.InventoryQuantity}; credits={shop.Credits}; expectedCreditSpend={expectedCost}; observedCreditSpend={creditDelta}; sharedCreditDrift={expectedCost - creditDelta}; receipt=exact inventory gain");
                Purchased = checked(Purchased + pendingQuantity);
                remaining -= pendingQuantity;
                pendingQuantity = 0;
                pendingCheckpoint = null;
                retryGate = null;
                runExpiresAt = now + (15 * 60 * 1000);
                nextActionAt = now + 250;
                Status = $"Purchased {Purchased:N0} Ceruleum Tanks; {remaining:N0} remaining.";
                if (remaining == 0) Complete("Configured purchase quantity reached.");
                return;
            }
            if (inventoryDelta < 0 || inventoryDelta > pendingQuantity)
            {
                Halt("Carried fuel changed unexpectedly; the pending purchase was not repeated.");
                return;
            }
            if (now > transactionExpiresAt)
            {
                Halt("Purchase verification timed out. Open Copy diagnostics; no further purchase was issued.");
                return;
            }
            var rejection = Interlocked.Exchange(ref inventoryRejectionAt, 0);
            if (rejection != 0)
            {
                var accepted = retryGate?.ReportInventoryRejection(rejection) == true;
                Trace($"inventory rejection logMessage=4167; associated={accepted}; attempts={retryGate?.Attempts ?? 0}; confirmed={confirmed}");
                CaptureDiagnosticSnapshot("inventory rejection");
                if (accepted)
                    Status = "The shop rejected this request. Waiting for unchanged inventory and credits before retrying…";
            }
            if (retryGate?.ShouldRetry(now, NativeFuelShop.HasConfirmation,
                inventoryDelta == 0 && creditDelta == 0,
                !confirmed && shop == pendingShop && NativeFuelShop.RetryUiReady) == true)
            {
                // The original durable intent and baseline remain in place. This is only a
                // retry of an explicitly rejected request that never reached confirmation.
                retryGate.MarkDispatched(now);
                Trace($"retry dispatch attempt={retryGate.Attempts}; quantity={pendingQuantity}; bags={beforeQuantity}; credits={beforeCredits}");
                if (!NativeFuelShop.Buy(pendingShop, pendingQuantity, out error)) { Halt(error); return; }
                Status = $"Retrying rejected fuel request ({retryGate.Attempts}/3)…";
                return;
            }
            // A manually confirmed purchase may arrive in multiple inventory updates. Never
            // confirm after any fuel arrives, or repeat a confirmed purchase. FC income alone
            // is allowed; the native confirmation rechecks the live budget and reserve.
            if (!confirmed && inventoryDelta == 0 && NativeFuelShop.HasConfirmation)
            {
                if (!NativeFuelShop.TryConfirmFuel(pendingShop, pendingQuantity, out error, creditReserve))
                {
                    if (!string.IsNullOrEmpty(error)) Halt(error);
                    return;
                }
                confirmed = true;
                Trace($"confirmation dispatched; quantity={pendingQuantity}; attempt={retryGate?.Attempts ?? 1}");
                Status = $"Waiting for {pendingQuantity:N0} purchased tanks to arrive.";
            }
            return;
        }

        if (NativeFuelShop.HasConfirmation) { Halt("An unexpected confirmation is open; it was left untouched."); return; }
        if (shop.DisplayedInventoryQuantity != shop.InventoryQuantity)
        { Status = "Waiting for the fuel shop inventory to update…"; return; }
        if (shop.Capacity == 0) { Complete("Inventory is full."); return; }
        var spendable = shop.Credits > creditReserve ? shop.Credits - creditReserve : 0;
        if (spendable < unitPrice) { Complete("The FC credit budget is exhausted."); return; }
        var amount = Math.Min(99u, Math.Min(remaining, Math.Min(shop.Capacity, spendable / unitPrice)));
        if (amount == 0) { Complete("No further fuel can be purchased."); return; }
        beforeQuantity = shop.InventoryQuantity;
        beforeCredits = shop.Credits;
        pendingShop = shop;
        pendingQuantity = amount;
        pendingCheckpoint = new FuelPurchaseCheckpoint { CharacterId = characterId, FreeCompanyId = companyId,
            Quantity = amount, BagBefore = beforeQuantity, CreditsBefore = beforeCredits, UnitPrice = unitPrice };
        confirmed = false;
        retryGate = new(now);
        Interlocked.Exchange(ref inventoryRejectionAt, 0);
        transactionExpiresAt = now + 30_000;
        if (queueEntry != null)
        {
            var persist = PersistPurchase ?? throw new InvalidOperationException("Fuel purchases require a durable checkpoint.");
            persist(pendingCheckpoint, 0);
        }
        Trace($"dispatch attempt=1; quantity={amount}; bags={beforeQuantity}; credits={beforeCredits}; capacity={shop.Capacity}; price={shop.UnitPrice}");
        if (!NativeFuelShop.Buy(shop, amount, out error)) { Halt(error); return; }
        Status = $"Confirming purchase of {amount:N0} Ceruleum Tanks.";
    }

    public void Stop()
    {
        if (!Active) return;
        Halt(pendingQuantity == 0 ? "Stopped by user." : "Stopped by user. A pending purchase may still complete; check inventory before restarting.");
    }

    public static bool TryCaptureCurrentHouse(out HouseProfile house, out string error)
    {
        house = new();
        if (!CanRun(out error)) return false;
        return NativeFuelShop.TryCaptureHouse(out house, out error);
    }

    private void Halt(string reason)
    {
        CaptureDiagnosticSnapshot("stopping: " + reason);
        CompletedNormally = false;
        Active = false;
        Status = $"Fuel stopped: {reason}";
    }

    private void Complete(string reason)
    {
        CompletedNormally = true;
        Active = false;
        Status = $"Fuel complete: {Purchased:N0} tanks purchased. {reason}";
    }

    private static bool CanRun(out string error)
    {
        error = !Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading
            ? "Fuel operations require the active game framework thread."
            : !Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer is null
                ? "Log in before using the fuel runner."
                : Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
                    ? "Fuel purchases stop during combat or zone changes."
                    : string.Empty;
        return error.Length == 0;
    }
}
