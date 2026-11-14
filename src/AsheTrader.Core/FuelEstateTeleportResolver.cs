namespace AsheTrader.Core;

public static class FuelEstateTeleportResolver
{
    // FC teleport entries leave their shared-estate Ward/Plot fields zero. Their packed
    // HouseId contains the actual world, territory, zero-based ward and zero-based plot.
    public static bool TryResolve(uint aetheryteId, byte subIndex, uint homeWorld, ulong houseId,
        Func<uint, int> districtForTerritory, out FuelEstateTeleport estate)
    {
        estate = new();
        if (houseId is 0 or ulong.MaxValue || homeWorld is 0 or > ushort.MaxValue) return false;
        var world = (uint)(houseId >> 48);
        var city = districtForTerritory((uint)((houseId >> 32) & 0xFFFF));
        var ward = (int)((houseId >> 16) & 0x3F);
        var plot = (int)(houseId & 0xFF);
        if (world != homeWorld || city == 0 || city != FuelAccessPlanner.FcCity(aetheryteId)
            || ward >= 30 || plot >= 60 || subIndex == 128) return false;
        estate = new() { AetheryteId = aetheryteId, SubIndex = subIndex, World = world,
            City = city, Ward = ward + 1, Plot = plot + 1 };
        return true;
    }
}
