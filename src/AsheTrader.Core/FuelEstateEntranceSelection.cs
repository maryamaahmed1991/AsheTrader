using System.Numerics;

namespace AsheTrader.Core;

public readonly record struct FuelEstateEntranceCandidate(ulong ObjectId, Vector3 Position, bool Targetable);

/// <summary>Finds a bounded approach target from the configured plot's map marker.
/// This is approach evidence only: the native house address must still match before interaction.</summary>
public static class FuelEstateEntranceSelection
{
    public static bool TrySelect(int plot, IReadOnlyList<Vector3> markers,
        IReadOnlyList<FuelEstateEntranceCandidate> candidates, out ulong objectId, out string reason)
    {
        objectId = 0;
        if (plot is < 1 or > 60 || markers.Count != 60 || markers.Any(x => !Finite(x) || x == Vector3.Zero))
        { reason = "The district's complete plot-marker data is unavailable."; return false; }

        var anchor = markers[plot - 1];
        foreach (var candidate in candidates)
        {
            if (!candidate.Targetable || candidate.ObjectId == 0 || !Finite(candidate.Position)) continue;
            var distance = HorizontalDistance(anchor, candidate.Position);
            // Markers can be above the building. Navigate to the live object, never
            // the elevated marker; a vertical bound excludes unrelated levels.
            if (distance > 24f || MathF.Abs(anchor.Y - candidate.Position.Y) > 32f) continue;
            var belongsToPlot = true;
            for (var index = 0; index < markers.Count; index++)
            {
                if (index == plot - 1) continue;
                if (HorizontalDistance(markers[index], candidate.Position) <= distance + 1f)
                { belongsToPlot = false; break; }
            }
            if (!belongsToPlot) continue;
            if (objectId != 0)
            { objectId = 0; reason = "More than one entrance matches the configured plot marker."; return false; }
            objectId = candidate.ObjectId;
        }
        reason = objectId == 0 ? "No targetable entrance could be associated with the configured plot marker."
            : "A unique entrance is nearest to the configured plot marker.";
        return objectId != 0;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
