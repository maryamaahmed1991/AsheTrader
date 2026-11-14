using System.Numerics;

namespace AsheTrader.Core;

/// <summary>Accepts Lifestream's plot-specific landing point before the player steps into its yard.</summary>
public static class FuelHouseArrivalPolicy
{
    public const float GateArrivalDistance = 10f;

    public static bool CanApproachFromGate(FuelHouseAddress expected, int currentWorld, int homeWorld,
        int district, int wardIndex, int plotIndex, bool outside, Vector3 playerPosition, Vector3? plotEntrance)
    {
        if (expected.World is <= 0 or > ushort.MaxValue || expected.City is not (2 or 8 or 9 or 70 or 111)
            || expected.Ward is < 1 or > 30 || expected.Plot is < 1 or > 60
            || !outside || currentWorld != expected.World || homeWorld != expected.World
            || district != expected.City || wardIndex != expected.Ward - 1
            || (plotIndex != -1 && plotIndex != expected.Plot - 1)
            || plotEntrance is not { } gate || !IsFinite(gate) || !IsFinite(playerPosition)) return false;
        return Vector3.DistanceSquared(playerPosition, gate) <= GateArrivalDistance * GateArrivalDistance;
    }

    private static bool IsFinite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
