using System;
using ExileCore;

namespace BeastsV3.Shared;

// Area classification and server-data reads.
public static class GameHelpers
{
    public const string MenagerieAreaName = "The Menagerie";

    public static bool IsMenagerie(AreaInstance area) =>
        string.Equals(TryGetAreaName(area), MenagerieAreaName, StringComparison.OrdinalIgnoreCase);

    public static bool IsHideoutLike(AreaInstance area) =>
        area?.IsHideout == true || IsMenagerie(area);

    public static bool IsTownOrHideout(AreaInstance area) =>
        area?.IsTown == true || area?.IsPeaceful == true || IsHideoutLike(area);

    public static bool IsRunnableMap(AreaInstance area) =>
        area is { IsTown: false } && !IsHideoutLike(area) && !IsSpecialNonBeastMap(area);

    // Area ID substrings that mark special maps with no monsters/beasts to track.
    private static readonly string[] SpecialNonBeastMapIdMarkers =
    {
        "MapAtlasEncounter_",
        "Expedition",
        "BetrayalSafeHouse",
        "MapWorldsTropicalIslandUnique",
        "HarvestLeagueMemoryLine",
    };

    // True for special maps (atlas encounters, Expedition, etc.) that contain no trackable beasts.
    public static bool IsSpecialNonBeastMap(AreaInstance area)
    {
        if (area == null) return false;
        var areaId = TryGetAreaId(area);
        if (string.IsNullOrWhiteSpace(areaId)) return false;

        foreach (var marker in SpecialNonBeastMapIdMarkers)
        {
            if (areaId.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static string TryGetAreaId(AreaInstance area) =>
        area?.Area == null ? null : TryReadPropertyString(area.Area, "Id") ?? TryReadPropertyString(area.Area, "RawName");

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
