using System;
using BeastsV3.Shared;
using ExileCore;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Color = SharpDX.Color;

namespace BeastsV3.Rendering;

// Draws a borderless, non-interactive ImGui text box positioned as a fraction of the
// game window.
public static class OverlayWindow
{
    public readonly record struct Style(
        Color Text,
        Color Border,
        Color Background,
        float Padding,
        int BorderThickness,
        float BorderRounding,
        float TextScale);

    // Returns the drawn window's size in pixels, so callers can stack another window
    // directly beneath it.
    public static Vector2 Draw(
        GameController game,
        string windowId,
        string text,
        float xPercent,
        float yPercent,
        Style style,
        bool centerHorizontally = true,
        float maxWidthPercent = 0f)
    {
        var rect = game.Window.GetWindowRectangle();
        var anchor = new Vector2(rect.Width * (xPercent / 100f), rect.Height * (yPercent / 100f));

        // Wrap width is measured before the font scale is applied, since SetWindowFontScale
        // multiplies whatever ImGui lays out. Without this a long error is one line wider than
        // the screen, so both ends of it are off-screen.
        var wrapWidth = 0f;
        if (maxWidthPercent > 0f)
        {
            var maxWindowWidth = rect.Width * (maxWidthPercent / 100f);
            wrapWidth = MathF.Max(80f, (maxWindowWidth - (style.Padding * 2f)) / MathF.Max(0.1f, style.TextScale));
        }

        var textSize = wrapWidth > 0f
            ? ImGui.CalcTextSize(text, false, wrapWidth)
            : ImGui.CalcTextSize(text);

        var windowSize = new Vector2(
            textSize.X * style.TextScale + style.Padding * 2,
            textSize.Y * style.TextScale + style.Padding * 2);

        var position = centerHorizontally
            ? new Vector2(anchor.X - windowSize.X / 2f, anchor.Y)
            : anchor;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, ImGuiEx.ToVec4(style.Background));
        ImGui.PushStyleColor(ImGuiCol.Border, ImGuiEx.ToVec4(style.Border));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, style.BorderRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, style.BorderThickness);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(style.Padding, style.Padding));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoMove;

        ImGui.Begin(windowId, flags);
        ImGui.SetWindowFontScale(style.TextScale);
        if (wrapWidth > 0f) ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
        ImGui.TextColored(ImGuiEx.ToVec4(style.Text), text);
        if (wrapWidth > 0f) ImGui.PopTextWrapPos();
        ImGui.SetWindowFontScale(1f);
        ImGui.End();

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);

        return windowSize;
    }
}
