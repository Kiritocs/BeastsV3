using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.Shared.Enums;

namespace BeastsV3.Analytics;

// What the game actually applied to the current map, read from its stat table.
public static class MapModifierStats
{
    private static readonly GameStat? AdditionalRedBeastsStat =
        Enum.TryParse<GameStat>("MapAdditionalRedBeasts", out var s) ? s : null;

    private static readonly GameStat? DuplicateCapturedChanceStat =
        Enum.TryParse<GameStat>("MapDuplicateCapturedBeastsChancePct", out var s) ? s : null;

    // The Bestiary Thread count on the map, still named Scarab by the game. It includes the
    // free threads below.
    private static readonly GameStat? BestiaryThreadsStat =
        Enum.TryParse<GameStat>("MapBestiaryScarabCount", out var s) ? s : null;

    // Threads the atlas added on top of the ones taken from the stash (Unwavering Vision).
    private static readonly GameStat? FreeThreadsStat =
        Enum.TryParse<GameStat>("MapXAdditionalFreeThreadsBypassingMaximum", out var s) ? s : null;

    // One reading of the map's modifier stats.
    public readonly record struct Reading(
        int AdditionalRedBeasts, int DuplicateCapturedChancePct, int BestiaryThreads, int FreeThreads)
    {
        // Bestiary Threads that came out of the stash.
        public int PaidBestiaryThreads => Math.Max(0, BestiaryThreads - FreeThreads);
    }

    // Every beast or thread stat on the map with its value. Since 3.30 these carry the combined
    // effect of the atlas and the Threads, so they describe the map without the atlas tree.
    // Empty while the stat table is unavailable.
    public static Dictionary<string, int> ReadRelevant(GameController game)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var stats = game?.IngameState?.Data?.MapStats;
        if (stats == null) return result;

        foreach (var s in stats)
        {
            var name = s.Key.ToString();
            if (name.Contains("Beast") || name.Contains("Bestiary") || name.Contains("Thread"))
                result[name] = s.Value;
        }

        return result;
    }

    // The same stats as one line, for the log.
    public static string DescribeRelevant(GameController game) =>
        string.Join(", ", ReadRelevant(game).Select(s => $"{s.Key}={s.Value}"));

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

        var threads = BestiaryThreadsStat is { } t && stats.TryGetValue(t, out var tv) ? tv : 0;
        var free = FreeThreadsStat is { } f && stats.TryGetValue(f, out var fv) ? fv : 0;

        return new Reading(Math.Max(0, reds), Math.Max(0, dup), Math.Max(0, threads), Math.Max(0, free));
    }
}
