namespace AsheTrader.Core;

public enum HousingLocationKind { None, Outdoor, Indoor, Workshop }

/// <summary>Detached native housing observations; all ward and plot indices are zero based.</summary>
public readonly record struct HousingLocationObservation(
    int CurrentWorld, uint CurrentTerritory, uint OriginalTerritory, HousingLocationKind Kind,
    ulong ActiveTerritoryHouseId, ulong CurrentHouseId, int WardIndex, int PlotIndex);

/// <summary>Resolves estate identity without confusing a workshop room or renovated interior with its plot.</summary>
public static class HousingAddressResolver
{
    public static bool TryResolve(HousingLocationObservation observation, Func<uint, int> districtForTerritory,
        out FuelHouseAddress address, out string reason)
    {
        address = default;
        reason = "The current housing address is not available yet.";
        if (observation.CurrentWorld is <= 0 or > ushort.MaxValue || observation.Kind == HousingLocationKind.None) return false;
        if (observation.Kind == HousingLocationKind.Outdoor)
        {
            if (!TryCreate(observation.CurrentWorld, districtForTerritory(observation.CurrentTerritory),
                    observation.WardIndex, observation.PlotIndex, out address)) return false;
            reason = string.Empty;
            return true;
        }

        // Workshops have their own HouseId. Never borrow the retained IndoorTerritory pointer:
        // it may still describe the previous estate. The generic getter is only a fallback.
        foreach (var id in new[] { observation.ActiveTerritoryHouseId, observation.CurrentHouseId }.Distinct())
        {
            if (id is 0 or ulong.MaxValue) continue;
            var unit = (byte)id;
            var ward = (int)((id >> 16) & 0x3F);
            var territory = (uint)((id >> 32) & 0xFFFF);
            var world = (int)((id >> 48) & 0xFFFF);
            if ((unit & 0x80) != 0)
            { reason = "The current housing identity is an apartment, not a workshop house."; return false; }
            if (world != 0 && world != observation.CurrentWorld)
            { reason = "The housing identity is still from another world. Wait for loading to finish."; return false; }
            if (ward >= 30 || unit >= 60) continue;
            // Packed estate identity wins over generic helpers or a renovated interior's
            // appearance. The workshop itself is district-specific, so its current territory
            // is a last fallback only when a nonempty house identity supplied real coordinates.
            var city = districtForTerritory(territory);
            if (city == 0) city = districtForTerritory(observation.OriginalTerritory);
            if (city == 0 && observation.Kind == HousingLocationKind.Workshop)
                city = districtForTerritory(observation.CurrentTerritory);
            if (city == 0) continue;
            if (TryCreate(observation.CurrentWorld, city, ward, unit, out address))
            { reason = string.Empty; return true; }
        }

        // Native coordinates can be used only with an independently known original estate
        // territory. A zero/default HouseId must never invent Ward 1 / Plot 1.
        if (TryCreate(observation.CurrentWorld, districtForTerritory(observation.OriginalTerritory),
                observation.WardIndex, observation.PlotIndex, out address))
        { reason = string.Empty; return true; }
        return false;
    }

    private static bool TryCreate(int world, int city, int wardIndex, int plotIndex, out FuelHouseAddress address)
    {
        address = default;
        if (world is <= 0 or > ushort.MaxValue || city is not (2 or 8 or 9 or 70 or 111)
            || wardIndex is < 0 or >= 30 || plotIndex is < 0 or >= 60) return false;
        address = new(world, city, wardIndex + 1, plotIndex + 1);
        return true;
    }
}
