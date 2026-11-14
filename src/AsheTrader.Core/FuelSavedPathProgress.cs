using System.Numerics;

namespace AsheTrader.Core;

public enum FuelSavedPathState { Waiting, Arrived, Stalled, StoppedShort }

/// <summary>Observes one dispatched Lifestream route. An idle service alone is not arrival evidence.</summary>
public sealed class FuelSavedPathProgress
{
    private readonly Vector3 endpoint;
    private Vector3 progressAnchor;
    private long startedAt;
    private long progressedAt;

    public FuelSavedPathProgress(Vector3 endpoint, Vector3 start, long now)
    {
        this.endpoint = endpoint;
        progressAnchor = start;
        startedAt = progressedAt = now;
    }

    public FuelSavedPathState Observe(bool busy, Vector3 position, long now)
    {
        if (!Finite(endpoint) || !Finite(progressAnchor) || !Finite(position))
            return FuelSavedPathState.StoppedShort;

        if (Vector3.DistanceSquared(progressAnchor, position) >= 0.25f)
        {
            progressAnchor = position;
            progressedAt = now;
        }

        // Move ignores vertical differences while following the waypoints, so allow
        // stair/ground height variation, but do not accept an unrelated floor.
        var atEndpoint = Vector2.DistanceSquared(new(position.X, position.Z), new(endpoint.X, endpoint.Z)) <= 2.25f
            && MathF.Abs(position.Y - endpoint.Y) <= 3f;
        if (!busy)
            return atEndpoint ? FuelSavedPathState.Arrived
                : now - startedAt >= 1_000 ? FuelSavedPathState.StoppedShort : FuelSavedPathState.Waiting;

        return now - progressedAt >= 15_000 ? FuelSavedPathState.Stalled : FuelSavedPathState.Waiting;
    }

    /// <summary>Preserves the full timeout budgets while a cutscene or loading state prevents observation.</summary>
    public void Suspend(long elapsed)
    {
        if (elapsed <= 0) return;
        startedAt += elapsed;
        progressedAt += elapsed;
    }

    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
