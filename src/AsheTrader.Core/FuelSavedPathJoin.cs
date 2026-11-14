using System.Numerics;

namespace AsheTrader.Core;

/// <summary>Joins a recorded route locally without cutting across distant corners or another floor.</summary>
public static class FuelSavedPathJoin
{
    public static bool TryCreate(IReadOnlyList<Vector3> recorded, Vector3 player, Vector3? entryAnchor,
        bool freshEntry, out IReadOnlyList<Vector3> remaining, out string reason)
    {
        remaining = Array.Empty<Vector3>();
        if (!LifestreamHouseRoutes.TrySnapshot(recorded, out var path, out reason)) return false;
        if (path.Count == 0)
        { reason = "There is no recorded path to follow."; return false; }
        if (!LifestreamHouseRoutes.TrySnapshot(entryAnchor is { } anchorInput ? [player, anchorInput] : [player], out _, out reason))
        { reason = "The current route-join position is invalid. " + reason; return false; }

        // A verified gate is the start of the route, not permission to skip to a
        // nearby later point across a fence. Keep every recorded point after it.
        if (entryAnchor is { } anchor && NearEndpoint(player, anchor))
            return Snapshot([anchor, .. path], "Following the complete recorded path from the verified gate.", out remaining, out reason);

        // Only the caller's observed estate-entry transition authorizes starting
        // an interior route from its known spawn position without a local join.
        if (freshEntry && entryAnchor == null)
            return Snapshot(path, "Following the complete recorded path after entering the estate.", out remaining, out reason);

        var endpoint = path[^1];
        if (NearEndpoint(player, endpoint))
            return Snapshot([endpoint], "The character is already at the recorded endpoint.", out remaining, out reason);

        IReadOnlyList<Vector3> joined = entryAnchor is { } start ? new[] { start }.Concat(path).ToArray() : path;
        // First matching segment wins. Selecting the globally nearest later
        // segment could skip an intervening doorway where recorded paths overlap.
        for (var index = 0; index + 1 < joined.Count; index++)
        {
            var from = joined[index];
            var delta = joined[index + 1] - from;
            var lengthSquared = delta.LengthSquared();
            var fraction = lengthSquared == 0 ? 0 : Math.Clamp(Vector3.Dot(player - from, delta) / lengthSquared, 0, 1);
            var projection = from + delta * fraction;
            if (Vector3.DistanceSquared(player, projection) > 2.25f) continue;
            return Snapshot(new[] { projection }.Concat(joined.Skip(index + 1)).ToArray(),
                $"Resuming from recorded path segment {index + 1}.", out remaining, out reason);
        }

        reason = "The character is too far from the recorded route to join it without cutting across obstacles.";
        return false;
    }

    private static bool Snapshot(IReadOnlyList<Vector3> points, string detail, out IReadOnlyList<Vector3> path, out string reason)
    {
        if (!LifestreamHouseRoutes.TrySnapshot(points, out path, out reason)) return false;
        reason = detail;
        return true;
    }

    private static bool NearEndpoint(Vector3 player, Vector3 endpoint)
        => Vector2.DistanceSquared(new(player.X, player.Z), new(endpoint.X, endpoint.Z)) <= 2.25f
            && MathF.Abs(player.Y - endpoint.Y) <= 3f;
}
