using System.Numerics;
using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelEstateReportedArrivalTests
{
    [Fact]
    public void ReportedGobletPlot33ArrivalSelectsItsVisibleDoor()
    {
        Assert.True(FuelEstateEntranceSelection.TrySelect(33, Markers, Candidates, out var selected, out var reason), reason);
        Assert.Equal(1UL, selected);
        Assert.True(FuelEstateEntranceSelection.TrySelect(33, Markers, Candidates.Reverse().ToArray(), out selected, out reason), reason);
        Assert.Equal(1UL, selected);
    }

    [Fact]
    public void ReportedNeighborDoorsCannotSubstituteForTheConfiguredHouse()
    {
        foreach (var candidate in Candidates.Skip(1))
        {
            Assert.False(FuelEstateEntranceSelection.TrySelect(33, Markers, [candidate], out var selected, out _));
            Assert.Equal(0UL, selected);
        }
        Assert.False(FuelEstateEntranceSelection.TrySelect(33, Markers, Candidates.Skip(1).ToArray(), out _, out _));
    }

    // Only static map coordinates and observed entrance geometry are retained.
    // Synthetic object IDs replace the report's IDs; no character or FC data is stored.
    private static readonly FuelEstateEntranceCandidate[] Candidates =
    [
        new(1, new(-628.72437f, 7.6202555f, -790.16455f), true),
        new(2, new(-610.4874f, 6.0935946f, -769.7409f), true),
        new(3, new(-658.9691f, 9.725464f, -776.9489f), true),
        new(4, new(-641.4532f, 2.0935946f, -745.62317f), true),
        new(5, new(-614.47327f, 13.640658f, -824.64185f), false),
        new(6, new(-580.6708f, -2.189605f, -787.7461f), true),
    ];

    // HousingMapMarkerInfo for territory 341, actual SubrowId 0..59, read from
    // installed game data using Lumina. Includes both divisions so every competing
    // plot participates in the production selection policy.
    private static readonly Vector3[] Markers =
    [
        new(-38f, -4f, -110f),
        new(-64f, 4f, -98f),
        new(-85.64f, 6f, -80.02f),
        new(-82.203f, -4f, -121.07f),
        new(-118.665f, 12f, -92.44f),
        new(-39.73f, 0f, -64.44f),
        new(-72.6f, 8f, -40.315f),
        new(-117.81f, 10f, -23.37f),
        new(-140.246f, 12f, -51.004f),
        new(-147.905f, 15f, -24.543f),
        new(-158.78f, 15f, 18.875f),
        new(-116f, 15f, 24f),
        new(-72f, 11.618f, 12f),
        new(-32f, -20f, 18f),
        new(-14.018f, -16.02f, -36.035f),
        new(68.25f, -16f, -39.567f),
        new(67f, -20f, 0.25f),
        new(73.188f, -24f, 28.851f),
        new(44f, -24f, 53.81f),
        new(0f, -24f, 80f),
        new(94f, -26f, -70f),
        new(106f, -14f, -36f),
        new(130.465f, -18f, -56.628f),
        new(144.5f, -26f, -32f),
        new(123.75f, -28f, 4f),
        new(120f, -28f, 40f),
        new(130f, -36f, 72f),
        new(95.545f, -33.68f, 59.08f),
        new(184f, -44f, 23.5f),
        new(188f, -40f, 60f),
        new(-594f, -4f, -742f),
        new(-606f, 4f, -768f),
        new(-623.98f, 6f, -789.64f),
        new(-582.93f, -4f, -786.203f),
        new(-611.56f, 12f, -822.665f),
        new(-639.56f, 0f, -743.73f),
        new(-663.685f, 8f, -776.6f),
        new(-680.63f, 10f, -821.81f),
        new(-652.996f, 12f, -844.246f),
        new(-679.457f, 15f, -851.905f),
        new(-722.875f, 15f, -862.78f),
        new(-728f, 15f, -820f),
        new(-716f, 11.618f, -776f),
        new(-722f, -20f, -736f),
        new(-667.965f, -16.02f, -718.018f),
        new(-664.433f, -16f, -635.75f),
        new(-704.25f, -20f, -637f),
        new(-732.851f, -24f, -630.812f),
        new(-757.81f, -24f, -660f),
        new(-784f, -24f, -704f),
        new(-634f, -26f, -610f),
        new(-668f, -14f, -598f),
        new(-647.372f, -18f, -573.535f),
        new(-672f, -26f, -559.5f),
        new(-708f, -28f, -580.25f),
        new(-744f, -28f, -584f),
        new(-776f, -36f, -574f),
        new(-763.08f, -33.68f, -608.455f),
        new(-727.5f, -44f, -520f),
        new(-764f, -40f, -516f),
    ];
}

