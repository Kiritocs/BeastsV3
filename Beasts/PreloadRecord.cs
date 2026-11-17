using System;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Analytics;
using BeastsV3.Shared;

namespace BeastsV3.Beasts;

// How often each beast the game loaded for a map really was in that map. The game loads a
// few species it never spawns, and nothing in what it loads tells them apart, so the plugin
// counts for itself. Only maps where every red beast was found are counted, which keeps the
// numbers exact. Kept across sessions in the plugin's data file.
public sealed class PreloadRecord
{
    private const string DataKey = "possibleBeasts";

    // Per beast: [maps it was loaded for, maps it was found in].
    private Dictionary<string, int[]> _counts = new(StringComparer.OrdinalIgnoreCase);

    public PreloadRecord()
    {
        foreach (var (name, pair) in PluginData.Get<Dictionary<string, int[]>>(DataKey) ?? new())
        {
            if (pair is { Length: 2 }) _counts[name] = pair;
        }
    }

    // Maps the beast was loaded for and maps it was found in.
    public (int Listed, int Present) Get(string beastName) =>
        _counts.TryGetValue(beastName ?? string.Empty, out var pair) ? (pair[0], pair[1]) : (0, 0);

    // Counts a finished map, given what the game had loaded for it.
    public void Add(MapAnalyticsRecord map, IReadOnlySet<string> loaded)
    {
        if (map == null || loaded == null || loaded.Count == 0) return;

        // A beast not seen only proves it was absent when every red beast was found.
        if (map.QuestRedBeastsTotal is not { } total || total <= 0 || map.RedBeastsFound < total) return;

        var found = (map.BeastBreakdown ?? [])
            .Where(b => b.Count > 0)
            .Select(b => b.BeastName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in loaded)
        {
            if (!_counts.TryGetValue(name, out var pair)) _counts[name] = pair = new int[2];
            pair[0]++;
            if (found.Contains(name)) pair[1]++;
        }

        PluginData.Set(DataKey, _counts);
    }
}
