using System;
using BeastsV3.Plugin.Settings;
using ExileCore;
using ExileCore.Shared.Enums;

namespace BeastsV3.Beasts;

// Whether the current map holds no beasts at all, so the beast overlays have nothing to say.
// The map loaded no beast species (see MapBeastPreloads), and nothing else speaks against it:
// no Bestiary Threads, no Black Mórrigan (which the game does not preload) and no beast seen.
// Untracked beasts count as beasts.
public sealed class EmptyMap
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private static readonly GameStat? ThreadCountStat = Stat("MapBestiaryScarabCount");
    private static readonly GameStat? BlackMorriganStat = Stat("MapBestiaryAreaContainsBlackMorrigan");

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly Func<MapBeastPreloads> _preloads;
    private readonly Func<BeastTracker> _tracker;

    private DateTime _checkedUtc = DateTime.MinValue;
    private bool _empty;

    public EmptyMap(GameController game, BeastsSettings settings, Func<MapBeastPreloads> preloads, Func<BeastTracker> tracker)
    {
        _game = game;
        _settings = settings;
        _preloads = preloads;
        _tracker = tracker;
    }

    // True when the beast overlays should stay hidden in this map.
    public bool HideOverlays
    {
        get
        {
            if (!_settings.Visibility.HideInMapsWithoutBeasts.Value) return false;

            var now = DateTime.UtcNow;
            if (now - _checkedUtc >= RefreshInterval)
            {
                _checkedUtc = now;
                _empty = Check();
            }
            return _empty;
        }
    }

    private bool Check()
    {
        // Not read yet: unknown, so nothing is hidden.
        if (_preloads()?.Possible is not { Count: 0 }) return false;
        if (_tracker()?.RedBeastsFound > 0) return false;

        var stats = _game?.IngameState?.Data?.MapStats;
        if (stats == null) return false;
        return !Positive(stats, ThreadCountStat) && !Positive(stats, BlackMorriganStat);
    }

    private static bool Positive(System.Collections.Generic.Dictionary<GameStat, int> stats, GameStat? stat) =>
        stat is { } key && stats.TryGetValue(key, out var value) && value > 0;

    private static GameStat? Stat(string name) => Enum.TryParse<GameStat>(name, out var stat) ? stat : null;
}
