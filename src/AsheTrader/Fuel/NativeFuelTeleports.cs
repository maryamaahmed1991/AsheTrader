using AsheTrader.Core;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AsheTrader.Fuel;

internal static unsafe class NativeFuelTeleports
{
    public static bool TryResolve(uint aetheryteId, byte subIndex, uint world, out FuelEstateTeleport estate, out string detail)
    {
        estate = new();
        detail = "Native FC teleport identity is unavailable.";
        if (!Plugin.Framework.IsInFrameworkUpdateThread || FuelAccessPlanner.FcCity(aetheryteId) == 0) return false;
        var telepo = Telepo.Instance();
        if (telepo == null) return false;
        foreach (var entry in telepo->TeleportList)
        {
            if (entry.AetheryteId != aetheryteId || entry.SubIndex != subIndex || entry.IsSharedHouse || entry.IsApartment) continue;
            var id = entry.HouseId.Id;
            var source = "teleport HouseId";
            if (id is 0 or ulong.MaxValue)
            {
                id = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate).Id;
                source = "owned FC HouseId";
            }
            var resolved = FuelEstateTeleportResolver.TryResolve(aetheryteId, subIndex, world, id, NativeHousingLocation.District, out estate);
            detail = $"id={aetheryteId}; sub={subIndex}; sharedWard={entry.Ward}; sharedPlot={entry.Plot}; houseId={id:X16}; source={source}; resolved={resolved}";
            return resolved;
        }
        return false;
    }
}
