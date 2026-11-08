using AsheTrader.Core;
using Dalamud.Game.ClientState.Conditions;

namespace AsheTrader.Fuel;

/// <summary>
/// Explicitly started fuel purchases in an already-open workshop shop. Each purchase is issued once,
/// then requires matching inventory and FC-credit changes before another purchase can begin.
/// </summary>
public sealed class FuelRunner
{
    public bool Active { get; private set; }
    public string Status { get; private set; } = "Fuel idle. Open the configured workshop's fuel shop to purchase.";
    public uint Purchased { get; private set; }
    private ulong characterId;
    private string houseAddress = string.Empty;
    private nint shopAddress;
    private ushort shopAddonId;
    private FuelShopSnapshot pendingShop;
    private uint unitPrice;
    private uint remaining;
    private uint pendingQuantity;
    private uint beforeQuantity;
    private uint beforeCredits;
    private bool confirmed;
    private long nextActionAt;
    private long transactionExpiresAt;
    private long runExpiresAt;

    public bool Start(CharacterProfile character, HouseProfile house, out string error)
    {
        if (!CanRun(out error)) return false;
        if (Active) { error = "A fuel purchase run is already active."; return false; }
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
        houseAddress = currentHouse.Address;
        shopAddress = shop.Address;
        shopAddonId = shop.AddonId;
        unitPrice = shop.UnitPrice;
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

    public void Update()
    {
        if (!Active) return;
        if (!CanRun(out var error)) { Halt(error); return; }
        if (Plugin.PlayerState.ContentId != characterId) { Halt("The active character changed."); return; }
        var now = Environment.TickCount64;
        if (now > runExpiresAt) { Halt("The 15-minute fuel run limit was reached."); return; }
        if (now < nextActionAt) return;
        if (!TryCaptureCurrentHouse(out var house, out error) || house.Address != houseAddress)
        {
            Halt("The configured workshop is no longer active.");
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
            if (inventoryDelta == pendingQuantity && creditDelta == expectedCost)
            {
                Purchased = checked(Purchased + pendingQuantity);
                remaining -= pendingQuantity;
                pendingQuantity = 0;
                nextActionAt = now + 1000;
                Status = $"Purchased {Purchased:N0} Ceruleum Tanks; {remaining:N0} remaining.";
                if (remaining == 0) Complete("Configured purchase quantity reached.");
                return;
            }
            if (inventoryDelta < 0 || inventoryDelta > pendingQuantity || creditDelta < 0 || creditDelta > expectedCost)
            {
                Halt("Inventory or FC credits changed unexpectedly; the pending purchase was not repeated.");
                return;
            }
            if (now > transactionExpiresAt)
            {
                Halt("Purchase verification timed out. Check inventory and credits before restarting; the purchase was not repeated.");
                return;
            }
            // A manually confirmed purchase may be arriving in multiple updates. Never click again
            // after either value has started changing, and never resend a previously issued purchase.
            if (!confirmed && inventoryDelta == 0 && creditDelta == 0 && NativeFuelShop.HasConfirmation)
            {
                if (!NativeFuelShop.TryConfirmFuel(pendingShop, pendingQuantity, out error)) { Halt(error); return; }
                confirmed = true;
                Status = $"Waiting for {pendingQuantity:N0} tanks and {expectedCost:N0} credits to reconcile.";
            }
            return;
        }

        if (NativeFuelShop.HasConfirmation) { Halt("An unexpected confirmation is open; it was left untouched."); return; }
        if (shop.Capacity == 0) { Complete("Inventory is full."); return; }
        if (shop.Credits < unitPrice) { Complete("Not enough FC credits remain for another tank."); return; }
        var amount = Math.Min(99u, Math.Min(remaining, Math.Min(shop.Capacity, shop.Credits / unitPrice)));
        if (amount == 0) { Complete("No further fuel can be purchased."); return; }
        beforeQuantity = shop.InventoryQuantity;
        beforeCredits = shop.Credits;
        pendingShop = shop;
        pendingQuantity = amount;
        confirmed = false;
        transactionExpiresAt = now + 30_000;
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
        Active = false;
        Status = $"Fuel stopped: {reason}";
    }

    private void Complete(string reason)
    {
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
