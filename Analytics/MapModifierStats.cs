using System;
using ExileCore;
using ExileCore.Shared.Enums;

namespace BeastsV3.Analytics;

// What the game actually applied to the current map, read from its stat table.
public static class MapModifierStats
{
    // Bestiary Scarab of the Herd: "5 additional Red Beasts". The stat totals every scarab
    // loaded, so 10 means two. Must match HERD_REDS_PER_SCARAB in the calculator and worker.
    public const int RedBeastsPerHerdScarab = 5;

    private static readonly GameStat? AdditionalRedBeastsStat =
        Enum.TryParse<GameStat>("MapAdditionalRedBeasts", out var s) ? s : null;

    private static readonly GameStat? DuplicateCapturedChanceStat =
        Enum.TryParse<GameStat>("MapDuplicateCapturedBeastsChancePct", out var s) ? s : null;

    // One reading of the map's modifier stats.
    public readonly record struct Reading(int AdditionalRedBeasts, int DuplicateCapturedChancePct)
    {
        // Herd scarab count implied by the additional red beasts, at 5 each. Integer division on
        // purpose: a value that is not a clean multiple of 5 means something else feeds this stat.
        public int HerdScarabCount => AdditionalRedBeasts / RedBeastsPerHerdScarab;

        public bool UsedDuplicatingScarab => DuplicateCapturedChancePct > 0;
    }

    // Reads both stats at once, or null while the table is unavailable.
    // The table is checked for content first, and that is the point: a stat is absent rather
    // than zero when its modifier is not applied, so "key missing" only means something once
    // the table is known to be populated. Null keeps the caller retrying, which is correct -
    // an empty table means the area has not finished loading.
    public static Reading? Read(GameController game)
    {
        var stats = game?.IngameState?.Data?.MapStats;
        if (stats == null || stats.Count == 0) return null;

        var reds = AdditionalRedBeastsStat is { } r && stats.TryGetValue(r, out var rv) ? rv : 0;
        var dup = DuplicateCapturedChanceStat is { } d && stats.TryGetValue(d, out var dv) ? dv : 0;

        return new Reading(Math.Max(0, reds), Math.Max(0, dup));
    }
}
