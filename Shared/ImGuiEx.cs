using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExileCore.PoEMemory;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace BeastsV3.Shared;

// ImGui/geometry helpers: color conversion, duration formatting, element and rect math.
public static class ImGuiEx
{
    public static Vector4 ToVec4(Color color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    public static uint ToU32(Color color) => ImGui.ColorConvertFloat4ToU32(ToVec4(color));

    public static string PluralSuffix(int count) => count == 1 ? string.Empty : "s";

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    public static IOrderedEnumerable<T> OrderByScreenPosition<T>(
        this IEnumerable<T> items,
        Func<T, RectangleF> rectSelector) =>
        items.OrderBy(item => rectSelector(item).Top)
             .ThenBy(item => rectSelector(item).Left);

    public static Element GetChildAt(Element root, params int[] indices)
    {
        var current = root;
        if (current == null || indices == null) return null;

        foreach (var index in indices)
        {
            current = current.GetChildAtIndex(index);
            if (current == null) return null;
        }

        return current;
    }

    public static bool IsRectMostlyInside(RectangleF inner, RectangleF outer, float minOverlapFraction = 0.5f)
    {
        var overlapLeft = MathF.Max(inner.Left, outer.Left);
        var overlapTop = MathF.Max(inner.Top, outer.Top);
        var overlapRight = MathF.Min(inner.Right, outer.Right);
        var overlapBottom = MathF.Min(inner.Bottom, outer.Bottom);
        if (overlapRight <= overlapLeft || overlapBottom <= overlapTop) return false;

        var innerArea = inner.Width * inner.Height;
        if (innerArea <= 0) return false;

        var overlapArea = (overlapRight - overlapLeft) * (overlapBottom - overlapTop);
        return overlapArea / innerArea >= minOverlapFraction;
    }
}
