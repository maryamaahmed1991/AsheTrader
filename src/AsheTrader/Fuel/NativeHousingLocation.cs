using AsheTrader.Core;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AsheTrader.Fuel;

internal static unsafe class NativeHousingLocation
{
    public static bool TryRead(out FuelHouseAddress address, out string error, out string diagnostic, bool logFailure = false)
    {
        address = default;
        error = "The current housing address is not available yet.";
        diagnostic = "HousingManager or the local player is not ready.";
        var housing = HousingManager.Instance();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (housing == null || player == null || housing->CurrentTerritory == null) return false;
        var kind = housing->IsInWorkshop() ? HousingLocationKind.Workshop
            : housing->IsOutside() ? HousingLocationKind.Outdoor
            : housing->IsInside() ? HousingLocationKind.Indoor : HousingLocationKind.None;
        ulong activeId = kind switch
        {
            HousingLocationKind.Workshop when housing->WorkshopTerritory != null => housing->WorkshopTerritory->HouseId.Id,
            HousingLocationKind.Indoor when housing->IndoorTerritory != null => housing->IndoorTerritory->HouseId.Id,
            _ => 0,
        };
        var original = kind is HousingLocationKind.Indoor or HousingLocationKind.Workshop
            ? HousingManager.GetOriginalHouseTerritoryTypeId() : 0;
        var observed = new HousingLocationObservation((int)player.CurrentWorld.RowId, Plugin.ClientState.TerritoryType,
            original, kind, activeId, housing->GetCurrentHouseId().Id, housing->GetCurrentWard(), housing->GetCurrentPlot());
        diagnostic = $"kind={kind}; world={observed.CurrentWorld}; territory={observed.CurrentTerritory}; originalTerritory={original}; activeHouseId={activeId:X16}; currentHouseId={observed.CurrentHouseId:X16}; nativeWardIndex={observed.WardIndex}; nativePlotIndex={observed.PlotIndex}";
        if (HousingAddressResolver.TryResolve(observed, District, out address, out error)) return true;
        if (logFailure)
            Plugin.Log.Warning("Fuel house capture could not resolve address: {Observation}; reason={Reason}", diagnostic, error);
        return false;
    }

    public static int District(uint territory)
    {
        if (territory == 0) return 0;
        return Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceNameRegion.RowId switch
        { 24 => 9, 23 => 2, 22 => 8, 25 => 70, 2402 => 111, _ => 0 };
    }
}
