using AsheTrader.Core;
using AsheTrader.Integrations;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AsheTrader.Fuel;

internal readonly record struct FuelShopSnapshot(nint Address, ushort AddonId, uint Credits, uint UnitPrice, uint InventoryQuantity, uint Capacity);

internal static unsafe class NativeFuelShop
{
    public static ulong CurrentCompanyId
    {
        get
        {
            var info = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoModule.Instance();
            if (info == null) return 0;
            var company = info->GetInfoProxyFreeCompany();
            return company == null ? 0 : company->Id;
        }
    }
    public const uint FuelItemId = 10155;
    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    public static bool TryCaptureHouse(out HouseProfile house, out string error)
    {
        house = new();
        var manager = HousingManager.Instance();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (manager == null || player == null || !manager->IsInWorkshop())
        {
            error = "Enter the company workshop before capturing this house.";
            return false;
        }
        var id = manager->GetCurrentHouseId();
        var territory = (uint)id.TerritoryTypeId;
        if (territory == 0) territory = HousingManager.GetOriginalHouseTerritoryTypeId();
        var region = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceNameRegion.RowId;
        var city = region switch { 24 => 9, 23 => 2, 22 => 8, 25 => 70, 2402 => 111, _ => 0 };
        var world = (int)player.CurrentWorld.RowId;
        var ward = manager->GetCurrentWard() + 1;
        var plot = manager->GetCurrentPlot() + 1;
        var address = LifestreamClient.FormatAddress(world, city, ward, plot);
        if (!LifestreamClient.TryParseAddress(address, out _) || (id.WorldId != 0 && id.WorldId != world))
        {
            error = "The current workshop's world, district, ward, and plot could not be verified.";
            return false;
        }
        house.Name = $"{player.CurrentWorld.Value.Name.ExtractText()}, {LifestreamClient.DistrictName(city)} W{ward}, P{plot}";
        house.Address = address;
        error = string.Empty;
        return true;
    }

    public static bool TryRead(out FuelShopSnapshot snapshot, out string error)
    {
        snapshot = default;
        var addon = ReadyAddon("FreeCompanyCreditShop");
        if (addon == null)
        {
            error = "Open the configured supplier's Ceruleum Tank credit shop before starting.";
            return false;
        }

        // ReaderFreeCompanyCreditShop in upstream AutoRetainer defines the arrays below.
        // Only operate row zero after checking its actual item ID and price, never an arbitrary credit shop.
        if (!TryUInt(addon, 3, out var credits) || !TryUInt(addon, 9, out var count) || count is 0 or > 20
            || !TryUInt(addon, 30, out var itemId) || itemId != FuelItemId
            || !TryUInt(addon, 130, out var price) || price == 0)
        {
            error = "The open credit shop does not match the supported Ceruleum Tank layout.";
            return false;
        }
        if (!TryInventory(out var quantity, out var capacity))
        {
            error = "Carried inventory is not available yet.";
            return false;
        }
        snapshot = new((nint)addon, addon->Id, credits, price, quantity, capacity);
        error = string.Empty;
        return true;
    }

    public static bool HasConfirmation => ReadyAddon("SelectYesno") != null;

    public static bool Buy(FuelShopSnapshot expected, uint quantity, out string error)
    {
        var addon = ReadyAddon("FreeCompanyCreditShop");
        if (addon == null || quantity is 0 or > 99 || HasConfirmation)
        {
            error = "The fuel shop is not ready for a new purchase.";
            return false;
        }
        if (!TryRead(out var current, out error) || current != expected)
        {
            error = "The fuel shop changed before the purchase was issued.";
            return false;
        }
        // Current AR's TaskRecursivelyBuyFuel sets this numeric control before the row-zero list click.
        var number = QuantityControl(addon);
        var stage = AtkStage.Instance();
        if (number == null || stage == null)
        {
            error = "The fuel quantity control does not match this game version.";
            return false;
        }
        number->SetValue(checked((int)quantity));
        if (number->Value != quantity)
        {
            error = "The shop did not accept the requested fuel quantity.";
            return false;
        }
        var evt = new AtkEvent { Listener = (AtkEventListener*)addon, Target = &stage->AtkEventTarget };
        var data = new AtkEventData();
        addon->ReceiveEvent(AtkEventType.ListItemClick, 0, &evt, &data);
        error = string.Empty;
        return true;
    }

    public static bool TryConfirmFuel(FuelShopSnapshot expected, uint quantity, out string error)
    {
        var confirmation = (AddonSelectYesno*)ReadyAddon("SelectYesno");
        if (confirmation == null || confirmation->PromptText == null)
        {
            error = string.Empty; // The pending transaction's deadline bounds UI readiness waits.
            return false;
        }
        var shop = ReadyAddon("FreeCompanyCreditShop");
        if (shop == null || (nint)shop != expected.Address || shop->Id != expected.AddonId
            || !TryRead(out var current, out error) || current != expected)
        {
            error = "The pending fuel shop identity, inventory, price, or credits changed. The confirmation was left untouched.";
            return false;
        }
        // A word match alone cannot identify a purchase dialog. Require an actual parent
        // relationship to the exact shop instance that issued the pending transaction.
        var parent = confirmation->ParentId;
        var blockedParent = confirmation->BlockedParentId;
        if ((parent != expected.AddonId && blockedParent != expected.AddonId)
            || (parent != 0 && parent != expected.AddonId)
            || (blockedParent != 0 && blockedParent != expected.AddonId))
        {
            error = $"Could not bind the fuel confirmation to its shop (shop {expected.AddonId}, parent {parent}, blocked parent {blockedParent}). It was left untouched.";
            return false;
        }
        var numeric = QuantityControl(shop);
        if (quantity is 0 or > 99 || numeric == null || numeric->Value != quantity
            || (ulong)quantity * expected.UnitPrice > expected.Credits || quantity > expected.Capacity)
        {
            error = "The pending fuel quantity or cost could not be verified. The confirmation was left untouched.";
            return false;
        }
        var text = SeString.Parse(confirmation->PromptText->NodeText.StringPtr.Value).TextValue;
        var name = Plugin.DataManager.GetExcelSheet<Item>().GetRow(FuelItemId).Name.ExtractText();
        // Inflected names may differ from the sheet spelling; these markers are used only
        // after binding the dialog to the pending shop, quantity and untouched baseline.
        if (!(name.Length > 0 && text.Contains(name, StringComparison.OrdinalIgnoreCase))
            && !new[] { "ceruleum", "青燐水バレル", "Erdseim", "céruleum" }.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            error = "An unrelated confirmation appeared. It was left untouched.";
            return false;
        }
        var numbers = ReadNumbers(text);
        var cost = (ulong)quantity * expected.UnitPrice;
        if (!numbers.Contains(cost) || (quantity > 1 && !numbers.Contains(quantity)))
        {
            error = "The fuel prompt does not display the expected quantity and total credit cost. It was left untouched.";
            return false;
        }
        var button = confirmation->YesButton;
        if (button == null || button->OwnerNode == null || !button->IsEnabled || !button->OwnerNode->AtkResNode.IsVisible())
        {
            error = string.Empty;
            return false;
        }
        var registered = button->OwnerNode->AtkResNode.AtkEventManager.Event;
        for (var count = 0; registered != null && count < 64; count++, registered = registered->NextEvent)
        {
            if (registered->State.EventType != AtkEventType.ButtonClick || registered->Listener != (AtkEventListener*)confirmation)
                continue;
            var copy = *registered;
            var data = new AtkEventData();
            registered->Listener->ReceiveEvent(AtkEventType.ButtonClick, checked((int)registered->Param), &copy, &data);
            error = string.Empty;
            return true;
        }
        error = "The fuel confirmation has no supported button event.";
        return false;
    }

    private static bool TryInventory(out uint quantity, out uint capacity)
    {
        quantity = 0;
        capacity = 0;
        var inventory = InventoryManager.Instance();
        var stackSize = Plugin.DataManager.GetExcelSheet<Item>().GetRow(FuelItemId).StackSize;
        if (inventory == null || stackSize == 0) return false;
        foreach (var type in Bags)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Size is <= 0 or > 1000) return false;
            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot == null || slot->Quantity < 0) return false;
                if (slot->ItemId == 0) capacity = checked(capacity + stackSize);
                else if (slot->ItemId == FuelItemId)
                {
                    if (slot->IsHighQuality() || slot->Quantity > stackSize) return false;
                    quantity = checked(quantity + (uint)slot->Quantity);
                    if (slot->Quantity < stackSize) capacity = checked(capacity + stackSize - (uint)slot->Quantity);
                }
            }
        }
        return true;
    }

    private static AtkComponentNumericInput* QuantityControl(AtkUnitBase* shop)
    {
        var listNode = shop->GetComponentNodeById(27);
        var rowNode = listNode == null || listNode->Component == null ? null : listNode->Component->GetNodeById(5);
        var row = rowNode == null ? null : rowNode->GetAsAtkComponentListItemRenderer();
        var numberNode = row == null ? null : row->GetNodeById(5);
        return numberNode == null ? null : numberNode->GetAsAtkComponentNumericInput();
    }

    private static HashSet<ulong> ReadNumbers(string text)
    {
        var values = new HashSet<ulong>();
        foreach (Match match in Regex.Matches(text, @"\p{Nd}+(?:[,.\u00A0\u202F ]\p{Nd}{3})*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
        {
            var digits = new string(match.Value.Where(char.IsDigit).Select(c => (char)('0' + (int)char.GetNumericValue(c))).ToArray());
            if (ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) values.Add(value);
        }
        return values;
    }

    private static bool TryUInt(AtkUnitBase* addon, int index, out uint value)
    {
        value = 0;
        if (addon->AtkValues == null || addon->AtkValuesCount <= index) return false;
        var entry = addon->AtkValues[index];
        if (entry.Type != AtkValueType.UInt) return false;
        value = entry.UInt;
        return true;
    }

    private static AtkUnitBase* ReadyAddon(string name)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsReady && addon->IsVisible ? addon : null;
    }
}
