using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

namespace AsheTrader.Fuel;

/// <summary>Reads the selected plot's door position from the loaded residential layout.</summary>
internal static unsafe class NativeFuelEstateEntrance
{
    public static bool TryRead(int plot, out Vector3 position, out string diagnostic)
    {
        position = default;
        diagnostic = "The configured plot's estate layout is not loaded yet.";
        if (plot is < 1 or > 60) { diagnostic = "The configured estate plot is invalid."; return false; }
        var world = LayoutWorld.Instance();
        var layout = world == null ? null : world->ActiveLayout;
        if (layout == null) return false;
        diagnostic = $"layoutState={layout->InitState}; layoutTerritory={layout->TerritoryTypeId}; currentTerritory={Plugin.ClientState.TerritoryType}; plotIndex={plot - 1}; outdoorData={layout->OutdoorAreaData != null}";
        if (layout->InitState != 7 || layout->TerritoryTypeId != Plugin.ClientState.TerritoryType || layout->OutdoorAreaData == null) return false;

        // FFXIVClientStructs OutdoorPlotLayoutData.EntrancePosition is the entrance
        // VFX position for this exact plot. The array contains all 60 plots, including
        // subdivision plots; no nearest-house or private/FC ownership guess is needed.
        var point = layout->OutdoorAreaData->Plots[plot - 1].EntrancePosition;
        diagnostic += $"; entrance={point}";
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) || point == Vector3.Zero) return false;
        position = point;
        return true;
    }
}
