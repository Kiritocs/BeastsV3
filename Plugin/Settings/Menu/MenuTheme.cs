using System.Numerics;
using ImGuiNET;

namespace BeastsV3.Plugin.Settings.Menu;

// Palette and ImGui style stack for the settings menu. Everything pushed is popped again,
// leaving the host's style untouched.
public static class MenuTheme
{
    public static readonly Vector4 Gold = Rgb(0xD4, 0xA2, 0x4A);
    public static readonly Vector4 GoldBright = Rgb(0xF0, 0xC0, 0x70);
    public static readonly Vector4 GoldDim = Rgb(0x8A, 0x6C, 0x33);

    public static readonly Vector4 Text = Rgb(0xCF, 0xC6, 0xB8);
    public static readonly Vector4 TextBright = Rgb(0xE8, 0xDF, 0xD0);
    public static readonly Vector4 Muted = Rgb(0x7D, 0x73, 0x67);

    public static readonly Vector4 Panel = Rgb(0x12, 0x0F, 0x0B);
    public static readonly Vector4 Surface = Rgb(0x17, 0x14, 0x0F);
    public static readonly Vector4 Card = Rgb(0x1C, 0x18, 0x13);
    // Input field background.
    public static readonly Vector4 Field = Rgb(0x1E, 0x19, 0x12);
    public static readonly Vector4 Border = Rgb(0x3E, 0x35, 0x2B);
    public static readonly Vector4 BorderStrong = Rgb(0x5C, 0x50, 0x42);

    public static readonly Vector4 Good = Rgb(0x8F, 0xC4, 0x7A);
    public static readonly Vector4 GoodFill = Rgb(0x5A, 0x8F, 0x4A);
    public static readonly Vector4 Warn = Rgb(0xE0, 0x9B, 0x3C);
    public static readonly Vector4 Bad = Rgb(0xD4, 0x6A, 0x5A);
    public static readonly Vector4 Info = Rgb(0x7A, 0xAA, 0xD4);

    public static readonly Vector4 Transparent = new(0f, 0f, 0f, 0f);

    public static uint U32(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

    // Cast from the raw value so it compiles against any ImGui.NET version the host uses.
    public const ImGuiChildFlags BorderedChild = (ImGuiChildFlags)1;
    public const ImGuiChildFlags PlainChild = default;

    // Returns the color with a different alpha.
    public static Vector4 WithAlpha(Vector4 color, float alpha) => new(color.X, color.Y, color.Z, alpha);

    private static int _pushedColors;
    private static int _pushedVars;

    public static void Push()
    {
        // Counted so Pop stays balanced when entries are added.
        _pushedColors = 0;
        _pushedVars = 0;

        Color(ImGuiCol.Text, Text);
        Color(ImGuiCol.TextDisabled, Muted);
        Color(ImGuiCol.ChildBg, Surface);
        Color(ImGuiCol.PopupBg, Panel);
        Color(ImGuiCol.Border, Border);
        Color(ImGuiCol.BorderShadow, Transparent);
        Color(ImGuiCol.FrameBg, Field);
        Color(ImGuiCol.FrameBgHovered, Card);
        Color(ImGuiCol.FrameBgActive, Card);
        Color(ImGuiCol.Button, Card);
        Color(ImGuiCol.ButtonHovered, Rgb(0x2A, 0x23, 0x1C));
        Color(ImGuiCol.ButtonActive, GoldDim);
        Color(ImGuiCol.Header, Card);
        Color(ImGuiCol.HeaderHovered, Rgb(0x2A, 0x23, 0x1C));
        Color(ImGuiCol.HeaderActive, Rgb(0x2A, 0x23, 0x1C));
        Color(ImGuiCol.SliderGrab, Gold);
        Color(ImGuiCol.SliderGrabActive, GoldBright);
        Color(ImGuiCol.CheckMark, Gold);
        Color(ImGuiCol.Separator, Border);
        Color(ImGuiCol.SeparatorHovered, BorderStrong);
        Color(ImGuiCol.SeparatorActive, Gold);
        Color(ImGuiCol.ScrollbarBg, Transparent);
        Color(ImGuiCol.ScrollbarGrab, Rgb(0x2A, 0x23, 0x1C));
        Color(ImGuiCol.ScrollbarGrabHovered, BorderStrong);
        Color(ImGuiCol.ScrollbarGrabActive, GoldDim);
        Color(ImGuiCol.TextSelectedBg, WithAlpha(Gold, 0.35f));

        Var(ImGuiStyleVar.FrameRounding, 4f);
        Var(ImGuiStyleVar.ChildRounding, 6f);
        Var(ImGuiStyleVar.PopupRounding, 6f);
        Var(ImGuiStyleVar.GrabRounding, 3f);
        Var(ImGuiStyleVar.ScrollbarRounding, 4f);
        // Hairline borders keep controls legible over live gameplay.
        Var(ImGuiStyleVar.FrameBorderSize, 1f);
        Var(ImGuiStyleVar.ChildBorderSize, 1f);
        Var(ImGuiStyleVar.ScrollbarSize, 10f);
        Var(ImGuiStyleVar.GrabMinSize, 9f);
        Var(ImGuiStyleVar.FramePadding, new Vector2(7f, 4f));
        Var(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));
        Var(ImGuiStyleVar.ItemInnerSpacing, new Vector2(6f, 4f));
        Var(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f));
    }

    public static void Pop()
    {
        if (_pushedVars > 0) ImGui.PopStyleVar(_pushedVars);
        if (_pushedColors > 0) ImGui.PopStyleColor(_pushedColors);
        _pushedVars = 0;
        _pushedColors = 0;
    }

    private static void Color(ImGuiCol target, Vector4 color)
    {
        ImGui.PushStyleColor(target, color);
        _pushedColors++;
    }

    private static void Var(ImGuiStyleVar target, float value)
    {
        ImGui.PushStyleVar(target, value);
        _pushedVars++;
    }

    private static void Var(ImGuiStyleVar target, Vector2 value)
    {
        ImGui.PushStyleVar(target, value);
        _pushedVars++;
    }

    private static Vector4 Rgb(int r, int g, int b, int a = 255) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);
}
