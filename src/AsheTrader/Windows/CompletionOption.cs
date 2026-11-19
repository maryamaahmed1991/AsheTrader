using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

internal static class CompletionOption
{
    public static bool Draw(ref bool enabled, bool locked, bool paired)
    {
        ImGui.BeginDisabled(locked);
        var changed = ImGui.Checkbox("Enable AR Multi Mode after completed job", ref enabled);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip((paired ? "Applies to both paired PCs. " : "Applies to this PC. ")
                + "Uses your existing AutoRetainer setup after the entire queue finishes. Pausing, ending early, or a failure never enables it. Finished queues with shortages or skipped characters still enable it. Update both PCs for paired jobs.");
        return changed;
    }
    public static void Status(Plugin plugin)
    {
        if (plugin.CompletionHandoff.Status.Length > 0)
            UiTone.Wrapped(TraderStyle.Muted, plugin.CompletionHandoff.Status);
    }
}
