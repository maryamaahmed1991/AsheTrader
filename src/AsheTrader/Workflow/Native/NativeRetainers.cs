using System.Numerics;
using System.Text;
using AsheTrader.Core;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AsheTrader.Workflow.Native;

internal sealed record RetainerIdentity(ulong Id, string Name, bool Available);
internal sealed record NativeBagSlot(InventoryType Container, int Slot, uint ItemId, bool HighQuality, uint Quantity, uint StackSize, bool Movable);
internal sealed record ItemInventory(uint Quantity, uint Capacity, uint StackSize, Dictionary<ItemKey, ulong> AllItems, IReadOnlyList<NativeBagSlot> Slots);

/// <summary>Version-sensitive native actions. Each caller must run on the framework thread.</summary>
internal static unsafe class NativeRetainers
{
    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    private static readonly InventoryType[] RetainerBags = [InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7];

    public static AtkUnitBase* Ready(string name)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsReady && addon->IsVisible ? addon : null;
    }

    public static bool HasUnrelatedModal => Ready("SelectYesno") != null || Ready("Trade") != null;
    public static bool HasNumeric => Ready("InputNumeric") != null;
    public static bool HasRetainerList => Ready("RetainerList") != null;
    public static bool HasRetainerMenu => Ready("SelectString") != null;
    public static bool HasRetainerInventory => AgentRetainer.Instance() != null && AgentRetainer.Instance()->IsAgentActive();
    public static bool HasVisibleRetainerInventory => HasRetainerInventory && (Ready("InventoryRetainer") != null || Ready("InventoryRetainerLarge") != null);

    public static bool HasExpectedRetainerTalk(RetainerIdentity expected)
    {
        var talk = (AddonTalk*)Ready("Talk");
        if (talk == null || !Plugin.Condition[ConditionFlag.OccupiedSummoningBell]) return false;
        // AddonTalk's first text node is the speaker (also used by TextToTalk/TalkUtils).
        var speaker = talk->AtkTextNode220;
        return speaker != null && speaker->NodeText.StringPtr.Value != null
            && string.Equals(SeString.Parse(speaker->NodeText.StringPtr.Value).TextValue.Trim(), expected.Name.Trim(), StringComparison.Ordinal);
    }

    public static bool AdvanceRetainerTalk(RetainerIdentity expected, Action<string>? trace = null)
    {
        if (!HasExpectedRetainerTalk(expected) || HasNumeric || HasUnrelatedModal) return false;
        VerifyRetainer(expected.Id);
        var addon = Ready("Talk");
        var stage = AtkStage.Instance();
        if (addon == null || stage == null) return false;
        // The Talk event sequence used by ECommons' AddonMaster.Talk. Only the
        // verified retainer's own greeting/farewell is eligible for this action.
        var evt = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = &stage->AtkEventTarget,
            State = new() { StateFlags = (AtkEventStateFlags)132 },
        };
        var data = new AtkEventData();
        Trace(trace, $"action Talk advance begin; retainer={expected.Id}; speaker={DiagnosticText(expected.Name)}");
        addon->ReceiveEvent(AtkEventType.MouseDown, 0, &evt, &data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
        addon->ReceiveEvent(AtkEventType.MouseUp, 0, &evt, &data);
        Trace(trace, "action Talk advance dispatched; awaiting observed UI change");
        return true;
    }

    public static string DescribeStorageUi()
    {
        string[] names = ["RetainerList", "Talk", "SelectString", "InventoryRetainer", "InventoryRetainerLarge", "RetainerItemTransferProgress", "InputNumeric", "SelectYesno", "Trade"];
        var visible = names.Where(name => Ready(name) != null).ToArray();
        return $"Ready windows: {(visible.Length == 0 ? "none" : string.Join(", ", visible))}; summoning-bell occupied: {Plugin.Condition[ConditionFlag.OccupiedSummoningBell]}";
    }

    /// <summary>Bounded read-only state for a user-requested troubleshooting report. No dialogue body or chat.</summary>
    public static IReadOnlyList<string> CaptureDiagnosticState(RetainerIdentity? expected, uint? menuRow, bool includeInventoryPages)
    {
        var lines = new List<string>();
        try
        {
            string[] names = ["RetainerList", "Talk", "SelectString", "InventoryRetainer", "InventoryRetainerLarge", "RetainerItemTransferProgress", "InputNumeric", "SelectYesno", "Trade"];
            var addonStates = new List<string>();
            foreach (var name in names)
            {
                var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
                addonStates.Add(addon == null ? $"{name}=absent" : $"{name}=visible:{addon->IsVisible},ready:{addon->IsReady},values:{addon->AtkValuesCount}");
            }
            lines.Add("windows " + string.Join("; ", addonStates));
            var manager = RetainerManager.Instance();
            var active = manager == null ? null : manager->GetActiveRetainer();
            lines.Add($"retainer expected={expected?.Id.ToString() ?? "none"} name={DiagnosticText(expected?.Name)}; active={(active == null ? "none" : active->RetainerId.ToString())}; lastSelected={(manager == null ? "none" : manager->LastSelectedRetainerId.ToString())}; rosterReady={manager != null && manager->IsReady}; bellOccupied={Plugin.Condition[ConditionFlag.OccupiedSummoningBell]}; inventoryAgentActive={HasRetainerInventory}");
            var talk = (AddonTalk*)Ready("Talk");
            if (talk != null && Plugin.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                var speaker = talk->AtkTextNode220;
                var name = speaker == null || speaker->NodeText.StringPtr.Value == null ? null : SeString.Parse(speaker->NodeText.StringPtr.Value).TextValue;
                lines.Add($"talk speaker={DiagnosticText(name)}; expectedSpeakerMatch={expected != null && HasExpectedRetainerTalk(expected)}");
            }
            if (menuRow.HasValue)
            {
                var expectedText = MenuMatchText(menuRow.Value);
                var allowSuffix = menuRow.Value == 2378;
                lines.Add($"menu expectedRow={menuRow.Value}; text={DiagnosticText(expectedText)}; length={expectedText.Length}; rule={(allowSuffix ? "unique localized prefix" : "exact")}");
                var menu = (AddonSelectString*)Ready("SelectString");
                if (menu == null) lines.Add("menu entries unavailable: SelectString is absent, hidden or not ready");
                else if (!Plugin.Condition[ConditionFlag.OccupiedSummoningBell]) lines.Add("menu entries omitted: no owned summoning-bell interaction");
                else
                {
                    var count = menu->PopupMenu.PopupMenu.EntryCount;
                    lines.Add($"menu entryCount={count}");
                    if (count is >= 0 and <= 40)
                    {
                        for (var index = 0; index < count; index++)
                        {
                            var pointer = menu->PopupMenu.PopupMenu.EntryNames[index].Value;
                            var value = pointer == null ? null : SeString.Parse(pointer).TextValue.Trim();
                            var matches = value != null && (allowSuffix ? value.StartsWith(expectedText, StringComparison.Ordinal) : value.Equals(expectedText, StringComparison.Ordinal));
                            lines.Add($"menu[{index}] text={DiagnosticText(value)}; length={value?.Length ?? 0}; matches={matches}");
                        }
                    }
                    else lines.Add("menu entries omitted: layout count outside supported range 0..40");
                }
            }
            if (includeInventoryPages)
            {
                var inventory = InventoryManager.Instance();
                if (inventory == null) lines.Add("inventory manager unavailable");
                else foreach (var type in Bags.Concat(RetainerBags))
                {
                    var page = inventory->GetInventoryContainer(type);
                    lines.Add(page == null ? $"inventory {type}=absent" : $"inventory {type}: loaded={page->IsLoaded}; size={page->Size}");
                }
            }
        }
        catch (Exception exception) { lines.Add($"diagnostic state unavailable: {exception.GetType().Name}: {DiagnosticText(exception.Message)}"); }
        return lines;
    }

    internal static string DiagnosticText(string? value)
    {
        if (value == null) return "<null>";
        var text = new StringBuilder("\"");
        foreach (var character in value.Take(160))
        {
            if (char.IsControl(character)) text.Append($"\\u{(int)character:X4}");
            else if (character is '\\' or '"') text.Append('\\').Append(character);
            else text.Append(character);
        }
        if (value.Length > 160) text.Append("…");
        return text.Append('"').ToString();
    }

    private static void Trace(Action<string>? trace, string message)
    {
        try { trace?.Invoke(message); } catch { /* Diagnostics must not change gameplay outcomes. */ }
    }

    public static ItemInventory ReadBags(uint itemId, bool hq) => Read(Bags, itemId, hq);
    public static ItemInventory ReadRetainer(ulong expectedRetainer, uint itemId, bool hq)
    {
        VerifyRetainer(expectedRetainer);
        if (!HasRetainerInventory) throw new InvalidOperationException("Retainer inventory is not open.");
        return Read(RetainerBags, itemId, hq);
    }

    private static ItemInventory Read(InventoryType[] containers, uint itemId, bool hq)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) throw new InvalidOperationException("Inventory manager is unavailable.");
        var items = Plugin.DataManager.GetExcelSheet<Item>();
        var wanted = items.GetRowOrDefault(itemId);
        if (!wanted.HasValue || wanted.Value.StackSize == 0) throw new InvalidOperationException("The selected item is invalid.");
        var stackSize = wanted.Value.StackSize;
        var counts = new Dictionary<ItemKey, ulong>();
        var slots = new List<NativeBagSlot>();
        ulong capacity = 0, quantity = 0;
        foreach (var type in containers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Size is < 1 or > 1000)
                throw new InvalidOperationException("Inventory pages are not fully loaded.");
            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot == null) throw new InvalidOperationException("Inventory slot is unavailable.");
                if (slot->ItemId == 0) { capacity += stackSize; continue; }
                if (slot->Quantity < 0) throw new InvalidDataException("Inventory quantity is invalid.");
                var key = new ItemKey(slot->ItemId, slot->IsHighQuality());
                counts[key] = checked(counts.GetValueOrDefault(key) + (uint)slot->Quantity);
                var row = items.GetRowOrDefault(slot->ItemId);
                var movable = row.HasValue && !row.Value.IsUntradable && slot->SpiritbondOrCollectability == 0 && slot->GlamourId == 0 && (slot->Flags & ~InventoryItem.ItemFlags.HighQuality) == 0;
                slots.Add(new(type, index, slot->ItemId, key.HighQuality, (uint)slot->Quantity, row?.StackSize ?? 1, movable));
                if (slot->ItemId != itemId || key.HighQuality != hq) continue;
                quantity += (uint)slot->Quantity;
                if (movable && slot->Quantity <= stackSize) capacity += stackSize - (uint)slot->Quantity;
            }
        }
        return new(checked((uint)quantity), (uint)Math.Min(uint.MaxValue, capacity), stackSize, counts, slots);
    }

    public static IReadOnlyList<RetainerIdentity> ReadRoster()
    {
        if (!HasRetainerList) throw new InvalidOperationException("Open the summoning bell's retainer list before reading the roster.");
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady) throw new InvalidOperationException("Retainer roster is not loaded.");
        var count = manager->GetRetainerCount();
        if (count > 10) throw new InvalidDataException("Unsupported retainer roster size.");
        var result = new List<RetainerIdentity>();
        for (uint index = 0; index < count; index++)
        {
            var retainer = manager->GetRetainerBySortedIndex(index);
            if (retainer == null || retainer->RetainerId == 0 || string.IsNullOrWhiteSpace(retainer->NameString))
                throw new InvalidOperationException("Retainer roster is incomplete.");
            result.Add(new(retainer->RetainerId, retainer->NameString, retainer->Available));
        }
        if (result.Select(x => x.Id).Distinct().Count() != result.Count) throw new InvalidDataException("Duplicate retainer identity.");
        return result;
    }

    public static bool SelectRetainer(RetainerIdentity identity, Action<string>? trace = null)
    {
        var addon = Ready("RetainerList");
        if (addon == null) return false;
        // ReaderRetainerList upstream exposes ten entries at offset 3, stride 10.
        if (addon->AtkValues == null || addon->AtkValuesCount < 103) throw new InvalidOperationException("Retainer list layout is incompatible.");
        for (var index = 0; index < 10; index++)
        {
            var offset = 3 + index * 10;
            var nameValue = addon->AtkValues[offset];
            if (nameValue.Type != AtkValueType.String && nameValue.Type != AtkValueType.ManagedString) continue;
            if (nameValue.String.Value == null || !string.Equals(SeString.Parse(nameValue.String.Value).TextValue, identity.Name, StringComparison.Ordinal)) continue;
            var active = addon->AtkValues[offset + 8];
            if (active.Type != AtkValueType.Bool || !active.Bool) throw new InvalidOperationException($"Retainer {identity.Name} is inaccessible.");
            var values = stackalloc AtkValue[4];
            values[0] = new() { Type = AtkValueType.Int, Int = 2 };
            values[1] = new() { Type = AtkValueType.UInt, UInt = (uint)index };
            values[2] = default; values[3] = default;
            Trace(trace, $"action RetainerList select begin; retainer={identity.Id}; name={DiagnosticText(identity.Name)}; index={index}; callback=[2,{index},0,0]");
            var callbackResult = addon->FireCallback(4, values, true);
            Trace(trace, $"action RetainerList select dispatched; callbackReturn={callbackResult}; awaiting active retainer and menu");
            return true;
        }
        throw new InvalidOperationException("Expected retainer is absent from the current list.");
    }

    public static void VerifyRetainer(ulong expected)
    {
        var manager = RetainerManager.Instance();
        var active = manager == null ? null : manager->GetActiveRetainer();
        if (active == null || active->RetainerId != expected || manager->LastSelectedRetainerId != expected)
            throw new InvalidOperationException("The active retainer changed unexpectedly.");
    }

    public static bool SelectMenu(uint localizedAddonRow, ulong expected, Action<string>? trace = null)
    {
        VerifyRetainer(expected);
        if (!Plugin.Condition[ConditionFlag.OccupiedSummoningBell] || HasNumeric || HasUnrelatedModal)
            throw new InvalidOperationException("The summoning-bell interaction ended or another window prevents retainer menu selection.");
        var addon = (AddonSelectString*)Ready("SelectString");
        if (addon == null) return false;
        var text = MenuMatchText(localizedAddonRow);
        var count = addon->PopupMenu.PopupMenu.EntryCount;
        if (count is < 0 or > 40 || text.Length == 0) throw new InvalidOperationException("Retainer menu layout is incompatible.");
        var entries = new string?[count];
        for (var index = 0; index < count; index++)
        {
            var pointer = addon->PopupMenu.PopupMenu.EntryNames[index].Value;
            entries[index] = pointer == null ? null : SeString.Parse(pointer).TextValue;
        }
        var selected = RetainerMenuMatcher.FindUnique(entries, text, localizedAddonRow == 2378);
        if (selected < 0) return false;
        var value = new AtkValue { Type = AtkValueType.Int, Int = selected };
        Trace(trace, $"action SelectString begin; row={localizedAddonRow}; index={selected}; text={DiagnosticText(text)}; retainer={expected}; callback=[{selected}]");
        var callbackResult = addon->FireCallback(1, &value, true);
        Trace(trace, $"action SelectString dispatched; callbackReturn={callbackResult}; awaiting observed UI change");
        return true;
    }

    private static string MenuMatchText(uint rowId)
    {
        var localized = Plugin.DataManager.GetExcelSheet<Addon>().GetRow(rowId).Text;
        if (rowId != 2378) return localized.ExtractText().Trim();
        // Row 2378 contains an evaluated slot-count payload. ExtractText omits the number
        // but keeps its surrounding text, so it can never equal the rendered menu entry.
        // AutoRetainer uses the first localized text payload as the stable prefix.
        var prefix = SeString.Parse(localized.Data.Span).Payloads.OfType<TextPayload>().FirstOrDefault()?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prefix)) throw new InvalidOperationException("The localized retainer storage label is unavailable.");
        return prefix;
    }

    public static bool BeginMove(ulong retainerId, NativeBagSlot slot, uint quantity, bool deposit, Action<string>? trace = null)
    {
        VerifyRetainer(retainerId);
        if (!HasRetainerInventory || HasNumeric || HasUnrelatedModal) throw new InvalidOperationException("Another window prevents the retainer item movement.");
        var manager = InventoryManager.Instance();
        var source = manager->GetInventorySlot(slot.Container, slot.Slot);
        if (source == null || source->ItemId != slot.ItemId || source->IsHighQuality() != slot.HighQuality || source->Quantity != slot.Quantity
            || source->SpiritbondOrCollectability != 0 || source->GlamourId != 0 || (source->Flags & ~InventoryItem.ItemFlags.HighQuality) != 0
            || !slot.Movable || quantity == 0 || quantity > slot.Quantity || quantity > int.MaxValue)
            throw new InvalidOperationException("The source slot changed before the retainer movement.");
        var partial = quantity < slot.Quantity;
        var callback = deposit ? (partial ? 4UL : 1UL) : (partial ? 3UL : 0UL);
        Trace(trace, $"action retainer move begin; deposit={deposit}; retainer={retainerId}; item={slot.ItemId}; hq={slot.HighQuality}; source={slot.Container}/{slot.Slot}; sourceQuantity={slot.Quantity}; requested={quantity}; callback={callback}");
        AgentRetainer.Instance()->HandleCallback((uint)slot.Slot, slot.Container, default, callback);
        Trace(trace, $"action retainer move dispatched; quantityDialogExpected={partial}; completion requires inventory deltas");
        return partial;
    }

    public static bool SubmitQuantity(ulong expected, uint quantity, uint sourceQuantity, Action<string>? trace = null)
    {
        VerifyRetainer(expected);
        var numeric = (AddonInputNumeric*)Ready("InputNumeric");
        if (numeric == null) return false;
        var owner = AgentRetainer.Instance()->GetAddonId();
        if ((numeric->ParentId != owner && numeric->BlockedParentId != owner)
            || (numeric->ParentId != 0 && numeric->ParentId != owner) || (numeric->BlockedParentId != 0 && numeric->BlockedParentId != owner))
            throw new InvalidOperationException("The quantity dialog belongs to another operation.");
        if (quantity == 0 || quantity > sourceQuantity || quantity > int.MaxValue || numeric->AtkValues == null || numeric->AtkValuesCount < 7
            || numeric->AtkValues[2].Type != AtkValueType.UInt || numeric->AtkValues[3].Type != AtkValueType.UInt
            || numeric->AtkValues[2].UInt > quantity || numeric->AtkValues[3].UInt < quantity || numeric->AtkValues[3].UInt > sourceQuantity)
            throw new InvalidOperationException("The retainer quantity dialog limits do not match the pending source stack.");
        var value = new AtkValue { Type = AtkValueType.Int, Int = checked((int)quantity) };
        Trace(trace, $"action quantity begin; retainer={expected}; amount={quantity}; sourceQuantity={sourceQuantity}");
        var callbackResult = numeric->FireCallback(1, &value, true);
        Trace(trace, $"action quantity dispatched; callbackReturn={callbackResult}; awaiting inventory deltas");
        return true;
    }

    public static void HideInventory(ulong expected, Action<string>? trace = null)
    {
        VerifyRetainer(expected);
        var agent = AgentRetainer.Instance();
        if (agent != null && agent->IsAgentActive())
        {
            Trace(trace, $"action retainer inventory hide begin; retainer={expected}");
            agent->Hide();
            Trace(trace, "action retainer inventory hide dispatched");
        }
    }

    public static bool TryCloseTransferProgress(ulong expected, Action<string>? trace = null)
    {
        VerifyRetainer(expected);
        var addon = Ready("RetainerItemTransferProgress");
        if (addon == null) return true;
        var button = addon->GetComponentButtonById(9);
        var expectedText = Plugin.DataManager.GetExcelSheet<Addon>().GetRow(13530).Text.ExtractText().Trim();
        if (button == null || button->ButtonTextNode == null || button->OwnerNode == null || !button->IsEnabled
            || !button->OwnerNode->AtkResNode.IsVisible() || !string.Equals(SeString.Parse(button->ButtonTextNode->NodeText.StringPtr.Value).TextValue.Trim(), expectedText, StringComparison.Ordinal))
            return false;
        var registered = button->OwnerNode->AtkResNode.AtkEventManager.Event;
        for (var index = 0; registered != null && index < 64; registered = registered->NextEvent, index++)
        {
            if (registered->State.EventType != AtkEventType.ButtonClick || registered->Listener != (AtkEventListener*)addon) continue;
            var copy = *registered; var data = new AtkEventData();
            Trace(trace, $"action RetainerItemTransferProgress close begin; retainer={expected}; event={registered->Param}");
            registered->Listener->ReceiveEvent(AtkEventType.ButtonClick, checked((int)registered->Param), &copy, &data);
            Trace(trace, "action RetainerItemTransferProgress close dispatched");
            break;
        }
        return false;
    }

    public static void CloseList(Action<string>? trace = null)
    {
        var addon = Ready("RetainerList");
        if (addon == null) return;
        var value = new AtkValue { Type = AtkValueType.Int, Int = -1 };
        Trace(trace, "action RetainerList close begin; callback=[-1]");
        var callbackResult = addon->FireCallback(1, &value, true);
        Trace(trace, $"action RetainerList close dispatched; callbackReturn={callbackResult}");
    }

    public static IGameObject? FindBell()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;
        var name = Plugin.DataManager.GetExcelSheet<EObjName>().GetRow(2000401).Singular.ExtractText();
        return Plugin.ObjectTable.Where(x => x.IsTargetable && x.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject &&
                (x.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase) || x.Name.TextValue == "リテイナーベル"))
            .OrderBy(x => Vector3.Distance(x.Position, player.Position)).FirstOrDefault();
    }

    public static void InteractBell(IGameObject bell, Action<string>? trace = null)
    {
        if (!bell.IsTargetable || Plugin.ObjectTable.LocalPlayer == null || Vector3.Distance(bell.Position, Plugin.ObjectTable.LocalPlayer.Position) > 4)
            throw new InvalidOperationException("Summoning bell is not within interaction range.");
        Plugin.TargetManager.Target = bell;
        Trace(trace, $"action summoning-bell interaction begin; object={bell.GameObjectId}; distance={Vector3.Distance(bell.Position, Plugin.ObjectTable.LocalPlayer.Position):F2}");
        TargetSystem.Instance()->InteractWithObject((NativeObject*)bell.Address, false);
        Trace(trace, "action summoning-bell interaction dispatched; awaiting RetainerList");
    }
}
