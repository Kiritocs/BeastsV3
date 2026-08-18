using System;
using System.Numerics;
using BeastsV3.Shared;
using ExileCore.PoEMemory.MemoryObjects;
using ImGuiNET;
using Color = SharpDX.Color;
using Vector3 = System.Numerics.Vector3;

namespace BeastsV3.Rendering;

// Pure-function drawing helpers shared by every renderer.
public static class RenderPrimitives
{
    // 15 unit-circle vertices used by the world-ground circles.
    public static readonly Vector2[] UnitCirclePoints = BuildUnitCircle(15);

    // Draws outlined text at a top-left screen position.
    public static void DrawOutlinedText(ImDrawListPtr drawList, Vector2 position, string text, Color textColor, Color outlineColor)
    {
        var outline = ImGuiEx.ToU32(outlineColor);
        var main = ImGuiEx.ToU32(textColor);

        drawList.AddText(new Vector2(position.X - 1, position.Y - 1), outline, text);
        drawList.AddText(new Vector2(position.X + 1, position.Y - 1), outline, text);
        drawList.AddText(new Vector2(position.X - 1, position.Y + 1), outline, text);
        drawList.AddText(new Vector2(position.X + 1, position.Y + 1), outline, text);
        drawList.AddText(position, main, text);
    }

    // Draws text centered on the given screen point, with an outline.
    public static void DrawCenteredOutlinedText(ImDrawListPtr drawList, Vector2 center, string text, Color textColor, Color outlineColor)
    {
        var size = ImGui.CalcTextSize(text);
        var topLeft = new Vector2(center.X - size.X / 2f, center.Y - size.Y / 2f);
        DrawOutlinedText(drawList, topLeft, text, textColor, outlineColor);
    }

    // Draws a filled, outlined circle in world space, projected through the camera into
    // the caller's screenPoints buffer.
    public static void DrawFilledCircleInWorld(
        ImDrawListPtr drawList,
        Camera camera,
        Vector3 worldCenter,
        float radius,
        Color color,
        float outlineThickness,
        int fillOpacityPercent,
        Vector2[] screenPointsBuffer)
    {
        for (var i = 0; i < UnitCirclePoints.Length; i++)
        {
            var p = UnitCirclePoints[i];
            screenPointsBuffer[i] = camera.WorldToScreen(worldCenter + new Vector3(p.X * radius, p.Y * radius, 0));
        }

        var fillColor = color with { A = SharpDX.Color.ToByte((int)(Math.Clamp(fillOpacityPercent, 0, 100) / 100f * 255)) };
        drawList.AddConvexPolyFilled(ref screenPointsBuffer[0], screenPointsBuffer.Length, ImGuiEx.ToU32(fillColor));
        drawList.AddPolyline(ref screenPointsBuffer[0], screenPointsBuffer.Length, ImGuiEx.ToU32(color), ImDrawFlags.Closed, outlineThickness);
    }

    // Draws a centerd label with optional secondary text and a padded background.
    public static void DrawCenteredLabel(
        ImDrawListPtr drawList,
        string primaryText,
        string secondaryText,
        Vector2 center,
        Color backgroundColor,
        Color primaryColor,
        Color secondaryColor,
        float paddingX,
        float paddingY,
        float lineSpacing)
    {
        var hasSecondary = !string.IsNullOrEmpty(secondaryText);
        var primarySize = ImGui.CalcTextSize(primaryText);
        var secondarySize = hasSecondary ? ImGui.CalcTextSize(secondaryText) : Vector2.Zero;

        var width = MathF.Max(primarySize.X, secondarySize.X);
        var height = primarySize.Y + (hasSecondary ? secondarySize.Y + lineSpacing * 0.25f : 0f);
        var half = new Vector2(width / 2f, height / 2f);
        var pad = new Vector2(paddingX, paddingY);

        drawList.AddRectFilled(center - half - pad, center + half + pad, ImGuiEx.ToU32(backgroundColor));

        var primaryPos = new Vector2(
            center.X - primarySize.X / 2f,
            hasSecondary ? center.Y - height / 2f : center.Y - primarySize.Y / 2f);
        drawList.AddText(primaryPos, ImGuiEx.ToU32(primaryColor), primaryText);

        if (!hasSecondary) return;

        var secondaryPos = new Vector2(center.X - secondarySize.X / 2f, primaryPos.Y + primarySize.Y + lineSpacing * 0.25f);
        drawList.AddText(secondaryPos, ImGuiEx.ToU32(secondaryColor), secondaryText);
    }


    private static Vector2[] BuildUnitCircle(int segments)
    {
        var points = new Vector2[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = i * 2f * MathF.PI / segments;
            points[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
        return points;
    }
}
