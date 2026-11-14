using System.Numerics;
using Lumina.Excel.Sheets;

namespace AsheTrader.Fuel;

/// <summary>Reads plot anchors from game sheets; these elevated map markers are not movement destinations.</summary>
internal static class NativeFuelEstateMarkers
{
    private const int PlotCount = 60;
    private sealed record Lookup(IReadOnlyList<Vector3> Markers, string Diagnostic, bool Success);
    private static readonly Dictionary<uint, Lookup> Cache = [];

    public static bool TryRead(uint territory, out IReadOnlyList<Vector3> markers, out string diagnostic)
    {
        markers = Array.Empty<Vector3>();
        diagnostic = "The residential plot markers are not available yet.";
        if (!Plugin.Framework.IsInFrameworkUpdateThread || Plugin.Framework.IsFrameworkUnloading)
        {
            diagnostic = "Plot markers must be read on the game framework thread.";
            return false;
        }
        if (territory == 0)
        {
            diagnostic = "The residential territory is not available yet.";
            return false;
        }

        if (!Cache.TryGetValue(territory, out var lookup))
        {
            try
            {
                lookup = ReadSheet(territory);
                // Sheet contents are immutable for the plugin session. Cache validation
                // failures too, but let a transient sheet-access exception be retried.
                Cache.Add(territory, lookup);
            }
            catch (Exception exception)
            {
                diagnostic = $"Plot marker sheet read failed; territory={territory}; type={exception.GetType().Name}.";
                return false;
            }
        }

        markers = lookup.Markers;
        diagnostic = lookup.Diagnostic;
        return lookup.Success;
    }

    private static Lookup ReadSheet(uint territory)
    {
        var positions = new Vector3[PlotCount];
        var present = new bool[PlotCount];
        var found = 0;
        var sheet = Plugin.DataManager.GetSubrowExcelSheet<HousingMapMarkerInfo>();
        foreach (var row in sheet)
        {
            foreach (var marker in row)
            {
                // Subrows 0..59 are plot indices, including the subdivision.
                // Subrows 60/61 describe apartment buildings. Resolve the district
                // through the referenced map rather than assuming an outer-row ID.
                var index = marker.SubrowId;
                if (index >= PlotCount || marker.Map.ValueNullable?.TerritoryType.RowId != territory) continue;
                if (present[index])
                    return Failure($"Duplicate plot marker; territory={territory}; plotIndex={index}.");

                var point = new Vector3(marker.X, marker.Y, marker.Z);
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) || point == Vector3.Zero)
                    return Failure($"Invalid plot marker position; territory={territory}; plotIndex={index}; position={point}.");
                positions[index] = point;
                present[index] = true;
                found++;
            }
        }

        if (found != PlotCount)
        {
            var missing = string.Join(",", Enumerable.Range(0, PlotCount).Where(index => !present[index]));
            return Failure($"Incomplete plot markers; territory={territory}; found={found}/{PlotCount}; missingIndices={missing}.");
        }
        return new(Array.AsReadOnly(positions), $"plotMarkers source=HousingMapMarkerInfo; territory={territory}; count={found}", true);
    }

    private static Lookup Failure(string diagnostic) => new(Array.Empty<Vector3>(), diagnostic, false);
}
