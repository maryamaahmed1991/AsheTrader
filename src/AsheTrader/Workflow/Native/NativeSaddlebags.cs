using AsheTrader.Core;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using PlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace AsheTrader.Workflow.Native;

internal static unsafe class NativeSaddlebags
{
    public const uint MainCommand = 77;
    public static bool Unlocked => UIModule.Instance() != null && UIModule.Instance()->IsMainCommandUnlocked(MainCommand);
    public static bool Premium => PlayerState.Instance() != null && PlayerState.Instance()->HasPremiumSaddlebag;
    public static bool Open => NativeRetainers.Ready("InventoryBuddy") != null;
    public static void Show() => UIModule.Instance()->ExecuteMainCommand(MainCommand);
    public static void Close() { var addon = NativeRetainers.Ready("InventoryBuddy"); if (addon != null) addon->Close(true); }
    public static byte? Tab
    {
        get { var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonInventoryBuddy*)NativeRetainers.Ready("InventoryBuddy"); return addon == null ? null : addon->TabIndex; }
        set { var addon = (FFXIVClientStructs.FFXIV.Client.UI.AddonInventoryBuddy*)NativeRetainers.Ready("InventoryBuddy"); if (addon != null && value.HasValue) addon->SetTab(value.Value); }
    }
    public static InventoryType[] Containers => Premium
        ? [(InventoryType)4000, (InventoryType)4001, (InventoryType)4100, (InventoryType)4101]
        : [(InventoryType)4000, (InventoryType)4001];

    public static bool Loaded => InventoryManager.Instance() != null && Containers.All(type =>
    {
        var page = InventoryManager.Instance()->GetInventoryContainer(type);
        return page != null && page->IsLoaded && page->Size == 35;
    });

    public static ItemInventory Read(uint item, bool hq) => NativeRetainers.ReadContainers(Containers, item, hq);

    public static IReadOnlyList<CargoSlot> Slots(bool saddlebags)
    {
        var result = new List<CargoSlot>();
        var manager = InventoryManager.Instance();
        if (manager == null) throw new InvalidOperationException("Inventory is unavailable.");
        InventoryType[] types = saddlebags ? Containers : [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
        foreach (var type in types)
        {
            var page = manager->GetInventoryContainer(type);
            if (page == null || !page->IsLoaded || page->Size != 35) throw new InvalidOperationException("Cargo inventory is still loading.");
            for (ushort index = 0; index < page->Size; index++)
            {
                var slot = page->GetInventorySlot(index);
                if (slot == null) throw new InvalidDataException("Cargo slot is unavailable.");
                var item = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(slot->ItemId);
                var movable = item.HasValue && !item.Value.IsUntradable && slot->SpiritbondOrCollectability == 0
                    && slot->GlamourId == 0 && (slot->Flags & ~InventoryItem.ItemFlags.HighQuality) == 0;
                result.Add(new((uint)type, index, slot->ItemId, slot->IsHighQuality(), checked((uint)slot->Quantity), item?.StackSize ?? 1, movable));
            }
        }
        return result;
    }

    public static bool Pending
    {
        get
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return true;
            foreach (ref var entry in manager->PendingOperations) if (!entry.IsEmpty) return true;
            return false;
        }
    }

    public static void Move(CargoMove move)
    {
        if (!Open || !Loaded || Pending || NativeRetainers.HasNumeric || NativeRetainers.HasUnrelatedModal || NativeRetainers.HasActiveCutscene)
            throw new InvalidOperationException("Saddlebags are not ready to move cargo.");
        var result = InventoryManager.Instance()->MoveItemSlot((InventoryType)move.Source.Container, move.Source.Slot,
            (InventoryType)move.Destination.Container, move.Destination.Slot, true);
        if (result != 0) throw new InvalidOperationException($"The game rejected the saddlebag movement ({result}). Resume will inspect both inventories before continuing.");
    }
}
