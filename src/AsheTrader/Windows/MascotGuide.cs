using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AsheTrader.Windows;

/// <summary>Compact, non-interactive illustrations; help remains readable when art is hidden or unavailable.</summary>
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

    internal static void Card(string id, ImTextureID? texture, string title, string body, bool showArt)
    {
        if (TraderStyle.BeginCard(id))
        {
            if (ImGui.BeginTable("GuideLayout", showArt ? 2 : 1, ImGuiTableFlags.SizingStretchProp))
            {
                if (showArt) ImGui.TableSetupColumn("Ashe", ImGuiTableColumnFlags.WidthFixed, TraderStyle.Px(82));
                ImGui.TableSetupColumn("Help", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (showArt) { Portrait(texture, 78); ImGui.TableNextColumn(); }
                ImGui.TextColored(TraderStyle.Rose, "A LITTLE HELP FROM ASHE");
                TraderStyle.Section(title);
                ImGui.TextWrapped(body);
                ImGui.EndTable();
            }
        }
        TraderStyle.EndCard();
    }
}
