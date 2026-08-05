using System;
using ExileCore;

namespace BeastsV3.Shared;

// Area classification and server-data reads.
public static class GameHelpers
{
    public const string MenagerieAreaName = "The Menagerie";

    public static bool IsHideoutLike(AreaInstance area) =>
        area?.IsHideout == true ||
        string.Equals(area?.Name, MenagerieAreaName, StringComparison.OrdinalIgnoreCase);

    public static bool IsTownOrHideout(AreaInstance area) =>
        area?.IsTown == true || area?.IsPeaceful == true || IsHideoutLike(area);

    public static bool IsRunnableMap(AreaInstance area) =>
        area is { IsTown: false } && !IsHideoutLike(area);

    public static string TryGetAreaHashText(AreaInstance area) =>
        area == null ? null : TryReadPropertyString(area, "AreaHash") ?? TryReadPropertyString(area, "Hash");

    public static int TryGetAreaInstanceId(AreaInstance area)
    {
        if (area == null) return -1;
        var val = area.GetType().GetProperty("InstanceId")?.GetValue(area);
        if (val is int id) return id;
        return val != null && int.TryParse(val.ToString(), out var parsed) ? parsed : -1;
    }

    public static string TryGetAreaName(AreaInstance area)
    {
        if (area == null) return string.Empty;
        return TryReadPropertyString(area, "Name")
            ?? TryReadPropertyString(area, "DisplayName")
            ?? TryReadPropertyString(area, "RawName")
            ?? string.Empty;
    }

    // Returns the character's league name, or empty when unreadable.
    public static string TryGetServerLeague(GameController game)
    {
        var ingameState = game?.Game?.IngameState;
        if (ingameState == null) return string.Empty;

        try
        {
            var serverData = ingameState.GetType().GetProperty("ServerData")?.GetValue(ingameState);
            if (serverData == null) return string.Empty;

            return TryReadPropertyString(serverData, "League")?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryReadPropertyString(object value, string propertyName) =>
        value.GetType().GetProperty(propertyName)?.GetValue(value)?.ToString();
}
