using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace AsheTrader.Windows;

/// <summary>Shared native UI surfaces and controls. Every pushed style has an explicit matching scope.</summary>
internal static class TraderStyle
{
    internal static readonly Vector4 Background = new(.945f, .918f, .957f, 1);
    internal static readonly Vector4 Sidebar = new(.875f, .827f, .910f, 1);
    internal static readonly Vector4 Surface = new(1f, .980f, .961f, 1);
    internal static readonly Vector4 Elevated = new(.902f, .859f, .925f, 1);
    internal static readonly Vector4 Border = new(.620f, .525f, .663f, 1);
    internal static readonly Vector4 Text = new(.204f, .149f, .255f, 1);
    internal static readonly Vector4 Muted = new(.365f, .294f, .412f, 1);
    internal static readonly Vector4 Accent = new(.055f, .384f, .314f, 1);
    internal static readonly Vector4 Rose = new(.596f, .176f, .365f, 1);
    internal static readonly Vector4 Gold = new(.490f, .310f, .055f, 1);
    internal static readonly Vector4 Danger = new(.663f, .137f, .204f, 1);
    private static readonly Vector4 Hover = new(.880f, .820f, .910f, 1);
    private static readonly Vector4 Active = new(.860f, .800f, .900f, 1);
    internal static float Scale => ImGuiHelpers.GlobalScale;
    internal static float Px(float value) => value * Scale;
    private static readonly Dictionary<uint, float> CardHeights = [];
    private static readonly Stack<(uint Id, bool AutoHeight, bool Visible)> Cards = [];

    internal static IDisposable Theme() => new ThemeScope();

    internal static void Section(string title, string? subtitle = null)
    {
        var position = ImGui.GetCursorScreenPos();
        var fontSize = ImGui.GetFontSize() * 1.18f;
        if (ImGui.CalcTextSize(title).X * 1.18f > ImGui.GetContentRegionAvail().X)
            ImGui.TextWrapped(title);
        else
        {
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), fontSize, position, ImGui.GetColorU32(Text), title);
            ImGui.Dummy(new Vector2(0, fontSize + Px(3)));
        }
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Muted);
            ImGui.TextWrapped(subtitle);
            ImGui.PopStyleColor();
        }
        ImGui.Spacing();
    }

    /// <remarks>Always call EndCard, including when this returns false.</remarks>
    internal static bool BeginCard(string id, float height = 0)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Surface);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(16), Px(14)));
        var key = ImGui.GetID(id);
        var autoHeight = height == 0;
        var measuredHeight = autoHeight ? CardHeights.GetValueOrDefault(key, 140) : height;
        var visible = ImGui.BeginChild(id, new Vector2(0, Px(measuredHeight)), true,
            ImGuiWindowFlags.AlwaysUseWindowPadding | (autoHeight ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None));
        Cards.Push((key, autoHeight, visible));
        return visible;
    }

    internal static void EndCard()
    {
        var card = Cards.Pop();
        if (card.AutoHeight && card.Visible)
        {
            // ImGui 1.88 has no auto-size child flag. Keep logical heights for DPI changes,
            // and retain clipped cards' last measurements instead of collapsing them.
            if (CardHeights.Count > 512 && !CardHeights.ContainsKey(card.Id)) CardHeights.Remove(CardHeights.Keys.First());
            CardHeights[card.Id] = Math.Max(48, (ImGui.GetCursorPosY() + ImGui.GetStyle().WindowPadding.Y) / Scale);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
        ImGui.Spacing();
    }

    internal static bool PrimaryButton(string label, Vector2 size = default)
        => Button(label, size, Rose, new(.520f, .137f, .310f, 1), new(.443f, .102f, .263f, 1), Surface);

    internal static bool SecondaryButton(string label, Vector2 size = default)
        => Button(label, size, Elevated, Hover, Active, Text);

    internal static bool DangerButton(string label, Vector2 size = default)
        => Button(label, size, new(1f, .875f, .875f, 1), new(.980f, .780f, .800f, 1), new(.945f, .690f, .733f, 1), Danger);

    private static bool Button(string label, Vector2 size, Vector4 normal, Vector4 hovered, Vector4 active, Vector4 text)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        var clicked = ImGui.Button(label, new Vector2(size.X > 0 ? Px(size.X) : size.X, size.Y > 0 ? Px(size.Y) : Px(36)));
        ImGui.PopStyleColor(4);
        return clicked;
    }

    internal static void Badge(string text, Vector4 color)
    {
        var position = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(text) + new Vector2(Px(18), Px(10));
        var draw = ImGui.GetWindowDrawList();
        var tint = new Vector4(color.X, color.Y, color.Z, .13f);
        draw.AddRectFilled(position, position + size, ImGui.GetColorU32(tint), size.Y * .5f);
        draw.AddText(position + new Vector2(Px(9), Px(5)), ImGui.GetColorU32(color), text);
        ImGui.Dummy(size);
    }

    internal static bool NavButton(string id, string label, string detail, bool selected, Vector2 size = default)
    {
        var position = ImGui.GetCursorScreenPos();
        var actual = new Vector2(size.X > 0 ? Px(size.X) : ImGui.GetContentRegionAvail().X, size.Y > 0 ? Px(size.Y) : Px(57));
        var clicked = ImGui.InvisibleButton(id, actual);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (selected || hovered)
            draw.AddRectFilled(position, position + actual, ImGui.GetColorU32(selected ? new Vector4(.980f, .843f, .890f, 1) : Elevated), Px(14));
        if (selected)
            draw.AddRectFilled(position + new Vector2(0, Px(12)), position + new Vector2(Px(3), actual.Y - Px(12)), ImGui.GetColorU32(Gold), Px(2));
        draw.AddText(position + new Vector2(Px(14), Px(9)), ImGui.GetColorU32(selected ? Rose : Text), label);
        draw.AddText(position + new Vector2(Px(14), Px(31)), ImGui.GetColorU32(Muted), detail);
        return clicked;
    }

    private sealed class ThemeScope : IDisposable
    {
        private int colors;
        private int variables;
        private bool disposed;

        public ThemeScope()
        {
            Color(ImGuiCol.WindowBg, Background); Color(ImGuiCol.ChildBg, Background); Color(ImGuiCol.PopupBg, Surface);
            Color(ImGuiCol.Text, Text); Color(ImGuiCol.TextDisabled, Muted); Color(ImGuiCol.Border, Border);
            Color(ImGuiCol.TitleBg, Sidebar); Color(ImGuiCol.TitleBgActive, Sidebar); Color(ImGuiCol.TitleBgCollapsed, Sidebar);
            Color(ImGuiCol.FrameBg, Surface); Color(ImGuiCol.FrameBgHovered, Elevated); Color(ImGuiCol.FrameBgActive, Hover);
            Color(ImGuiCol.Button, Elevated); Color(ImGuiCol.ButtonHovered, Hover); Color(ImGuiCol.ButtonActive, Active);
            Color(ImGuiCol.Header, Elevated); Color(ImGuiCol.HeaderHovered, Hover); Color(ImGuiCol.HeaderActive, Active);
            Color(ImGuiCol.CheckMark, Accent); Color(ImGuiCol.SliderGrab, Accent); Color(ImGuiCol.SliderGrabActive, Gold);
            Color(ImGuiCol.Separator, Border); Color(ImGuiCol.SeparatorHovered, Accent); Color(ImGuiCol.SeparatorActive, Accent);
            Color(ImGuiCol.ScrollbarBg, Background); Color(ImGuiCol.ScrollbarGrab, Border); Color(ImGuiCol.ScrollbarGrabHovered, Muted); Color(ImGuiCol.ScrollbarGrabActive, Accent);
            Color(ImGuiCol.TableHeaderBg, Elevated); Color(ImGuiCol.TableBorderStrong, Border); Color(ImGuiCol.TableBorderLight, new(.76f, .69f, .79f, 1));
            Color(ImGuiCol.TableRowBg, new(0, 0, 0, 0)); Color(ImGuiCol.TableRowBgAlt, new(1f, .98f, .96f, .60f));
            Color(ImGuiCol.TextSelectedBg, new(.75f, .49f, .64f, .30f)); Color(ImGuiCol.PlotHistogram, new(.60f, .82f, .73f, 1));
            Color(ImGuiCol.Tab, Surface); Color(ImGuiCol.TabHovered, Hover);
            Color(ImGuiCol.TabActive, Elevated); Color(ImGuiCol.TabUnfocused, Sidebar); Color(ImGuiCol.TabUnfocusedActive, Elevated);
            Variable(ImGuiStyleVar.WindowPadding, new Vector2(Px(18), Px(16)));
            Variable(ImGuiStyleVar.FramePadding, new Vector2(Px(12), Px(8)));
            Variable(ImGuiStyleVar.ItemSpacing, new Vector2(Px(10), Px(9)));
            Variable(ImGuiStyleVar.ItemInnerSpacing, new Vector2(Px(8), Px(6)));
            Variable(ImGuiStyleVar.CellPadding, new Vector2(Px(10), Px(8)));
            Variable(ImGuiStyleVar.WindowRounding, Px(18)); Variable(ImGuiStyleVar.ChildRounding, Px(16));
            Variable(ImGuiStyleVar.FrameRounding, Px(12)); Variable(ImGuiStyleVar.PopupRounding, Px(12));
            Variable(ImGuiStyleVar.ScrollbarRounding, Px(8)); Variable(ImGuiStyleVar.GrabRounding, Px(8));
            Variable(ImGuiStyleVar.WindowBorderSize, Px(1)); Variable(ImGuiStyleVar.ChildBorderSize, Px(1));
            Variable(ImGuiStyleVar.FrameBorderSize, Px(1)); Variable(ImGuiStyleVar.IndentSpacing, Px(20));
            Variable(ImGuiStyleVar.ScrollbarSize, Px(10));
        }

        private void Color(ImGuiCol target, Vector4 value) { ImGui.PushStyleColor(target, value); colors++; }
        private void Variable(ImGuiStyleVar target, float value) { ImGui.PushStyleVar(target, value); variables++; }
        private void Variable(ImGuiStyleVar target, Vector2 value) { ImGui.PushStyleVar(target, value); variables++; }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ImGui.PopStyleVar(variables);
            ImGui.PopStyleColor(colors);
        }
    }
}
