using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

/// <summary>Help is opened explicitly. Artwork is requested only while the popup is visible.</summary>
internal static class MascotGuide
{
    internal static void Portrait(ImTextureID? texture, float logicalSize)
    {
        var size = TraderStyle.Px(logicalSize);
        var position = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(position + new Vector2(size * .5f), size * .45f,
            ImGui.GetColorU32(new Vector4(TraderStyle.Rose.X, TraderStyle.Rose.Y, TraderStyle.Rose.Z, .12f)));
        if (texture is { } image) draw.AddImage(image, position, position + new Vector2(size));
        else
            draw.AddText(position + new Vector2(size * .35f, size * .35f), ImGui.GetColorU32(TraderStyle.Gold), "AT");
        ImGui.Dummy(new Vector2(size));
    }

    internal static void HelpButton(string id, Func<ImTextureID?> getTexture, string title, string body, bool showArt)
    {
        ImGui.PushID(id);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, TraderStyle.Px(16));
        ImGui.PushStyleColor(ImGuiCol.Button, TraderStyle.Surface);
        ImGui.PushStyleColor(ImGuiCol.Text, TraderStyle.Muted);
        if (ImGui.Button("i##help", new Vector2(TraderStyle.Px(28)))) ImGui.OpenPopup("Help");
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Help: {title}");
        var anchor = ImGui.GetItemRectMin();
        anchor.Y = ImGui.GetItemRectMax().Y + TraderStyle.Px(6);
        var viewport = ImGui.GetMainViewport();
        var width = Math.Min(TraderStyle.Px(440), Math.Max(1, viewport.WorkSize.X - TraderStyle.Px(32)));
        ImGui.SetNextWindowPos(anchor, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width, Math.Max(1, viewport.WorkSize.Y - TraderStyle.Px(32))));
        if (ImGui.BeginPopup("Help", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            if (ImGui.BeginTable("GuideLayout", showArt ? 2 : 1, ImGuiTableFlags.SizingStretchProp))
            {
                if (showArt) ImGui.TableSetupColumn("Ashe", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(82));
                ImGui.TableSetupColumn("Help", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (showArt) { Portrait(getTexture(), 78); ImGui.TableNextColumn(); }
                ImGui.TextColored(TraderStyle.Rose, "A LITTLE HELP FROM ASHE");
                TraderStyle.Section(title);
                ImGui.TextWrapped(body);
                ImGui.EndTable();
            }
            ImGui.Spacing();
            if (TraderStyle.SecondaryButton("Got it")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopID();
    }
}
