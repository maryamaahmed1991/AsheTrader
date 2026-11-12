using AsheTrader.Core;
using System.Numerics;
using AsheTrader.Workflow.Native;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AsheTrader.Trading;

internal sealed record TradePartner(uint EntityId, string Name, uint HomeWorldId);
internal sealed record OfferedItem(uint ItemId, bool HighQuality, uint Quantity)
{
    public ItemKey Key => new(ItemId, HighQuality);
}

internal sealed record NativeTradeSnapshot(
    bool IsOpen, ushort AddonId, uint PartnerEntityId,
    TradeState LocalState, TradeState RemoteState, bool SyncPending,
    IReadOnlyList<OfferedItem> LocalItems, IReadOnlyList<OfferedItem> RemoteItems,
    uint LocalGil, uint RemoteGil);

internal sealed record TradeOpeningSnapshot(bool Ready, bool Acknowledged, string BlockReason, float Distance,
    uint SelectedTarget, uint RawPartner, TradeState LocalState, TradeState RemoteState, bool SyncPending,
    bool AddonVisible, bool AddonReady, bool TradeOpenCondition, bool AgentActive, bool PartnerTargetable)
{
    public string Describe() => $"ready={Ready}; blocked={BlockReason}; distance={Distance:F2}; selectedTarget={SelectedTarget}; rawPartner={RawPartner}; localState={LocalState}; remoteState={RemoteState}; sync={SyncPending}; addonVisible={AddonVisible}; addonReady={AddonReady}; tradeOpen={TradeOpenCondition}; agentActive={AgentActive}; partnerTargetable={PartnerTargetable}";
}

/// <summary>
/// All methods are called only by TradeRunner.Update on the Dalamud framework thread.
/// The Trade context callback, numeric prompt and button location were cross-checked
/// against Knightmore/Henchman TestyTraderTasks (AGPL-3.0) and FFXIVClientStructs.
/// These game-facing details require in-game validation after client updates.
/// </summary>
internal sealed unsafe class NativeTradeAdapter
{
    private static readonly InventoryType[] Bags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    public IReadOnlyList<InventoryStack> ReadInventory()
    {
        var result = new List<InventoryStack>();
        var manager = InventoryManager.Instance();
        if (manager == null || Plugin.ObjectTable.LocalPlayer == null) return result;
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var bag in Bags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded || container->Size is < 0 or > 1000)
                throw new InvalidOperationException("Carried inventory is not fully loaded.");
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0 || slot->Quantity <= 0) continue;
                var row = sheet.GetRowOrDefault(slot->ItemId);
                var tradable = row.HasValue && !row.Value.IsUntradable &&
                               slot->SpiritbondOrCollectability == 0 && slot->GlamourId == 0 &&
                               (slot->Flags & ~InventoryItem.ItemFlags.HighQuality) == 0;
                result.Add(new InventoryStack((int)bag, slotIndex, slot->ItemId,
                    slot->IsHighQuality(), checked((uint)slot->Quantity),
                    row?.StackSize ?? 1, tradable));
            }
        }
        return result.AsReadOnly();
    }

    public static Dictionary<ItemKey, ulong> Totals(IEnumerable<InventoryStack> stacks) => stacks
        .GroupBy(stack => stack.Key)
        .ToDictionary(group => group.Key, group => group.Aggregate(0UL, (total, item) => total + item.Quantity));

    public IPlayerCharacter? Resolve(TradePartner partner) => Plugin.ObjectTable.OfType<IPlayerCharacter>()
        .FirstOrDefault(player => player.EntityId == partner.EntityId &&
            player.HomeWorld.RowId == partner.HomeWorldId &&
            string.Equals(player.Name.TextValue, partner.Name, StringComparison.Ordinal));

    public NativeTradeSnapshot ReadTrade()
    {
        var manager = InventoryManager.Instance();
        var addon = ReadyAddon("Trade");
        if (manager == null || addon == null)
            return new(false, 0, 0, default, default, false, [], [], 0, 0);
        return new(true, addon->Id, manager->TradePartnerEntityId,
            manager->TradeLocalState, manager->TradeRemoteState, manager->TradeIsSyncPending,
            ReadOffer(manager->TradeItemsLocal[..5]), ReadOffer(manager->TradeItemsRemote[..5]),
            ReadGil(manager->TradeItemsLocal[5]), ReadGil(manager->TradeItemsRemote[5]));
    }

    private static uint ReadGil(InventoryItem item) => checked((uint)Math.Max(0, item.Quantity));

    private static IReadOnlyList<OfferedItem> ReadOffer(Span<InventoryItem> items)
    {
        var result = new List<OfferedItem>();
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (!item.IsSymbolic && (item.ItemId == 0 || item.Quantity <= 0)) continue;
            // Trade slots can be symbolic; getters resolve their source inventory entry.
            var id = item.GetBaseItemId();
            var quantity = item.GetQuantity();
            if (id != 0 && quantity != 0) result.Add(new(id, item.IsHighQuality(), quantity));
        }
        return result;
    }

    // A visible window remains a blocker while its controls are becoming ready or closing.
    public bool HasModal => HasNumeric || VisibleAddon("SelectYesno") != null;
    public bool HasNumeric => VisibleAddon("InputNumeric") != null;
    public bool HasTradeUi => VisibleAddon("Trade") != null || Plugin.Condition[ConditionFlag.TradeOpen];
    public bool IsInventoryTallyReady => ReadActivity().Ready;

    // Capture even without a selected partner: recovery after reload has no active runner.
    public TradeActivityState ReadActivity()
    {
        var manager = InventoryManager.Instance();
        var agent = AgentTrade.Instance();
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("Trade").Address;
        return new()
        {
            LoggedIn = Plugin.ClientState.IsLoggedIn, PlayerAvailable = Plugin.ObjectTable.LocalPlayer != null,
            InventoryAvailable = manager != null, AgentAvailable = agent != null,
            LocalState = manager == null ? -1 : (int)manager->TradeLocalState,
            RemoteState = manager == null ? -1 : (int)manager->TradeRemoteState,
            SyncPending = manager != null && manager->TradeIsSyncPending,
            AddonVisible = addon != null && addon->IsVisible, AddonReady = addon != null && addon->IsReady,
            TradeOpenCondition = Plugin.Condition[ConditionFlag.TradeOpen], AgentActive = agent != null && agent->IsAgentActive(),
            NumericVisible = HasNumeric, ConfirmationVisible = VisibleAddon("SelectYesno") != null,
        };
    }

    public void SelectPartner(TradePartner partner)
    {
        var target = Resolve(partner) ?? throw new InvalidOperationException("The selected partner is no longer nearby.");
        Plugin.TargetManager.Target = target;
    }

    public TradeOpeningSnapshot ReadOpening(TradePartner partner)
    {
        var manager = InventoryManager.Instance();
        var player = Plugin.ObjectTable.LocalPlayer;
        var target = Resolve(partner);
        var activity = ReadActivity();
        var local = (TradeState)activity.LocalState;
        var remote = (TradeState)activity.RemoteState;
        var acknowledged = activity.Acknowledged;
        var distance = player == null || target == null ? float.PositiveInfinity : Vector3.Distance(player.Position, target.Position);
        var selected = Plugin.TargetManager.Target?.EntityId ?? 0;
        var busy = Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || Plugin.Condition[ConditionFlag.Occupied] || Plugin.Condition[ConditionFlag.OccupiedInEvent]
            || Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] || Plugin.Condition[ConditionFlag.OccupiedSummoningBell]
            || Plugin.Condition[ConditionFlag.Casting] || Plugin.Condition[ConditionFlag.Jumping]
            || Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.LoggingOut]
            || NativeRetainers.HasActiveCutscene;
        var reason = !Plugin.ClientState.IsLoggedIn || player == null || manager == null ? "local player/inventory unavailable"
            : target == null ? "partner not found"
            : !player.IsTargetable || !target.IsTargetable ? "player or partner not targetable"
            : target.CurrentWorld.RowId != player.CurrentWorld.RowId ? "different current worlds"
            : distance > TradeOpeningGate.RequestDistance ? "partner outside request range"
            : selected != partner.EntityId ? "partner target not selected"
            : acknowledged ? "trade request already acknowledged"
            : !activity.Ready ? activity.BlockReason
            : busy ? "game busy/loading/cutscene"
            : HasModal || NativeRetainers.HasRetainerList || NativeRetainers.HasRetainerMenu || NativeRetainers.HasRetainerInventory
                || ReadyAddon("Talk") != null || ReadyAddon("ContextMenu") != null ? "another interaction window is open"
            : ActionManager.Instance() == null || ActionManager.Instance()->AnimationLock > 0 ? "animation lock"
            : "";
        return new(reason.Length == 0, acknowledged, reason, distance, selected,
            manager == null ? 0 : manager->TradePartnerEntityId, local, remote, activity.SyncPending,
            activity.AddonVisible, activity.AddonReady, activity.TradeOpenCondition, activity.AgentActive, target?.IsTargetable == true);
    }

    public void RequestTrade(TradePartner partner)
    {
        // Selection and stable-readiness observation happen on earlier updates.
        var opening = ReadOpening(partner);
        if (!opening.Ready) throw new InvalidOperationException($"Trade request is no longer ready: {opening.BlockReason}.");
        InventoryManager.Instance()->SendTradeRequest(partner.EntityId);
    }

    public bool AddItem(TradeAllocation entry)
    {
        if (InventoryManager.Instance()->TradeLocalState != TradeState.SelectingTradeGoods)
            throw new InvalidOperationException("The trade stopped accepting items before the offer was filled.");
        if (!Bags.Contains((InventoryType)entry.Container) || entry.Slot < 0)
            throw new InvalidOperationException("Only carried bag items are supported.");
        var source = InventoryManager.Instance()->GetInventorySlot((InventoryType)entry.Container, entry.Slot);
        if (source == null || source->ItemId != entry.ItemId || source->IsHighQuality() != entry.HighQuality ||
            source->Quantity < entry.Quantity || source->SpiritbondOrCollectability != 0)
            throw new InvalidOperationException("The planned inventory slot changed. Start again after reviewing the list.");
        if (HasModal) throw new InvalidOperationException("Another confirmation or quantity window is open.");
        var needsQuantity = source->Quantity > 1;
        var agent = AgentTrade.Instance();
        if (agent == null) throw new InvalidOperationException("The game's trade agent is unavailable.");
        agent->HandleCallback(checked((uint)entry.Slot), (InventoryType)entry.Container, default, 0);
        return needsQuantity;
    }

    public bool SubmitQuantity(uint quantity, ushort tradeAddonId)
    {
        var addon = ReadyAddon("InputNumeric");
        if (addon == null) return false;
        if (quantity == 0 || quantity > 9999)
            throw new InvalidOperationException("The quantity is outside the supported trade range.");
        if ((addon->ParentId != 0 && addon->ParentId != tradeAddonId) ||
            (addon->BlockedParentId != 0 && addon->BlockedParentId != tradeAddonId))
            throw new InvalidOperationException("The quantity dialog belongs to another window.");
        var value = new AtkValue { Type = AtkValueType.UInt, UInt = quantity };
        addon->FireCallback(1, &value, true);
        return true;
    }

    public bool ClickTrade()
    {
        var addon = ReadyAddon("Trade");
        if (addon == null || addon->UldManager.NodeList == null || addon->UldManager.NodeListCount <= 3)
            return false;
        var node = addon->UldManager.NodeList[3];
        if (node == null) return false;
        var button = node->GetAsAtkComponentButton();
        if (button == null || button->ButtonTextNode == null) return false;
        // A node index alone is insufficient: also require the localized Trade button text.
        var expected = Plugin.DataManager.GetExcelSheet<Addon>().GetRow(203).Text.ExtractText();
        if (!string.Equals(Text(button->ButtonTextNode), expected.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("The Trade button layout does not match this client version.");
        return ClickRegisteredButton(addon, button);
    }

    public bool ConfirmTrade()
    {
        var addon = (AddonSelectYesno*)ReadyAddon("SelectYesno");
        if (addon == null) return false;
        if (addon->PromptText == null) throw new InvalidOperationException("The confirmation text is unavailable.");
        var expected = Plugin.DataManager.GetExcelSheet<Addon>().GetRow(102223).Text.ExtractText();
        if (string.IsNullOrWhiteSpace(expected) ||
            !string.Equals(Text(addon->PromptText), expected.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("An unrelated confirmation appeared. It was left untouched.");
        return ClickRegisteredButton((AtkUnitBase*)addon, addon->YesButton);
    }

    public void Refuse(TradePartner? partner)
    {
        if (partner == null) return;
        var manager = InventoryManager.Instance();
        if (manager != null && manager->TradePartnerEntityId == partner.EntityId &&
            ReadActivity().Acknowledged)
            manager->RefuseTrade();
    }

    private static string Text(AtkTextNode* node) =>
        SeString.Parse(node->NodeText.StringPtr.Value).TextValue.Trim();

    private static AtkUnitBase* ReadyAddon(string name)
    {
        var addon = VisibleAddon(name);
        return addon != null && addon->IsReady ? addon : null;
    }

    private static AtkUnitBase* VisibleAddon(string name)
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible ? addon : null;
    }

    private static bool ClickRegisteredButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button == null || button->OwnerNode == null || !button->IsEnabled) return false;
        var node = &button->OwnerNode->AtkResNode;
        if (!node->IsVisible()) return false;
        // Dispatch the actual registered event, rather than guessing callback numbers.
        var registered = node->AtkEventManager.Event;
        for (var count = 0; registered != null && count < 64; count++, registered = registered->NextEvent)
        {
            if (registered->State.EventType != AtkEventType.ButtonClick ||
                registered->Listener != (AtkEventListener*)addon) continue;
            var eventCopy = *registered;
            var data = new AtkEventData();
            registered->Listener->ReceiveEvent(AtkEventType.ButtonClick, checked((int)registered->Param), &eventCopy, &data);
            return true;
        }
        return false;
    }
}
