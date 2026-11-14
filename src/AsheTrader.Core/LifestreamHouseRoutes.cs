using System.Numerics;

namespace AsheTrader.Core;

/// <summary>A detached pair of recorded paths from one verified house registration.</summary>
public sealed record LifestreamHouseRoute(ulong OwnerCharacterId, IReadOnlyList<Vector3> Entrance, IReadOnlyList<Vector3> Workshop)
{
    public static LifestreamHouseRoute Empty { get; } = new(0, Array.Empty<Vector3>(), Array.Empty<Vector3>());
}

public static class LifestreamHouseRoutes
{
    public const int MaximumPathPoints = 512;
    public const float MaximumCoordinate = 10_000;

    public static bool TryCreate(FuelHouseAddress expected, ulong ownerCharacterId, int ownerHomeWorld,
        LifestreamRegisteredHouse? registration, out LifestreamHouseRoute route, out string reason)
    {
        route = LifestreamHouseRoute.Empty;
        if (!LifestreamHouseImports.TryRegisteredFcHouse(ownerCharacterId, ownerHomeWorld, null, registration, out var house)
            || !FuelHouseAddress.TryParse(house.Address, out var actual) || actual != expected)
        { reason = "The recorded house path does not match this owner and configured address."; return false; }

        if (!TrySnapshot(registration!.PathToEntrance, out var entrance, out var entranceError))
        { reason = "Invalid recorded entrance path: " + entranceError; return false; }
        if (!TrySnapshot(registration.PathToWorkshop, out var workshop, out var workshopError))
        { reason = "Invalid recorded workshop path: " + workshopError; return false; }
        if (entrance.Count == 0 && workshop.Count == 0)
        { reason = "The matching house registration has no recorded entrance or workshop path."; return false; }

        route = new(ownerCharacterId, entrance, workshop);
        reason = $"Recorded house route: owner={ownerCharacterId}; entrancePoints={entrance.Count}; workshopPoints={workshop.Count}.";
        return true;
    }

    /// <summary>Prefer a complete route, then the current character, then a stable owner order.
    /// Paths from different registrations are never spliced together.</summary>
    public static LifestreamHouseRoute SelectBest(IEnumerable<LifestreamHouseRoute> routes, ulong currentCharacterId)
        => routes.Where(route => route.Entrance.Count > 0 || route.Workshop.Count > 0)
            .OrderByDescending(route => route.Entrance.Count > 0 && route.Workshop.Count > 0)
            .ThenByDescending(route => route.OwnerCharacterId == currentCharacterId)
            .ThenBy(route => route.OwnerCharacterId)
            .FirstOrDefault() ?? LifestreamHouseRoute.Empty;

    /// <summary>Copies IPC-owned lists before use; null and empty mean no recorded route.</summary>
    public static bool TrySnapshot(IReadOnlyList<Vector3>? source, out IReadOnlyList<Vector3> path, out string reason)
    {
        path = Array.Empty<Vector3>();
        reason = string.Empty;
        if (source == null || source.Count == 0) return true;
        if (source.Count > MaximumPathPoints)
        { reason = $"The path exceeds {MaximumPathPoints} points."; return false; }
        var points = new Vector3[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            var point = source[index];
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)
                || MathF.Abs(point.X) > MaximumCoordinate || MathF.Abs(point.Y) > MaximumCoordinate || MathF.Abs(point.Z) > MaximumCoordinate)
            { reason = $"The path has an invalid coordinate at point {index + 1}."; return false; }
            points[index] = point;
        }
        path = Array.AsReadOnly(points);
        return true;
    }
}
