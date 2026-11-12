using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

/// <summary>Colors supplement explicit status labels; ordinary instructions keep the theme's text color.</summary>
internal static class UiTone
{
    internal static readonly Vector4 Good = TraderStyle.Accent;
    internal static readonly Vector4 Warning = TraderStyle.Gold;
    internal static readonly Vector4 Error = TraderStyle.Danger;
    internal static readonly Vector4 Info = new(.59f, .76f, .94f, 1);
    private static readonly string[] ErrorWords = ["failed", "error", "invalid", "unavailable", "could not", "cannot", "mismatch", "blocked", "missing"];
    private static readonly string[] WarningWords = ["paused", "unknown", "uncertain", "waiting", "needs attention", "not ready", "not been checked", "unmet", "short", "incomplete", "unverified", "not verified", "not complete", "disabled"];
    private static readonly string[] GoodWords = ["complete", "verified", "ready", "available and idle", "refreshed"];

    internal static void Wrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    internal static void Status(string text)
    {
        var color = ErrorWords
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)) ? Error
            : WarningWords
                .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)) ? Warning
            : GoodWords
                .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)) ? Good : Info;
        Wrapped(color, text);
    }

    internal static void Progress(float fraction, Vector2 size, bool warning = false)
    {
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, warning ? Warning : fraction >= 1 ? Good : Info);
        ImGui.ProgressBar(Math.Clamp(fraction, 0, 1), size);
        ImGui.PopStyleColor();
    }
}
