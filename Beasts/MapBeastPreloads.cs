using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;

namespace BeastsV3.Beasts;

// Which beasts the current map can contain, from the monster files the game loaded for it.
// Measured over six maps: every red beast that spawned was among them, and each map also
// loaded two to four species that never spawned. So a beast missing here is not in the map,
// and a beast listed here may be.
public sealed class MapBeastPreloads
{
    private readonly GameController _game;

    // Replaced as a whole by the background read, never changed in place.
    private volatile HashSet<string> _possible;
    private int _generation;

    public MapBeastPreloads(GameController game)
    {
        _game = game;
    }

    // Beast names the map can contain, or null until the first read has finished.
    public IReadOnlySet<string> Possible => _possible;

    // Forgets the last map and reads the one just entered.
    public void StartNewMap()
    {
        _possible = null;
        Read();
    }

    // Reads again when the first try never finished, e.g. the map was left within seconds.
    public void EnsureRead()
    {
        if (_possible == null) Read();
    }

    public void Clear()
    {
        _generation++;
        _possible = null;
    }

    // Scanning the game's file table takes about 0.2 s, so it runs off the render thread
    // (FireAndForget starts a task).
    private void Read()
    {
        var generation = ++_generation;
        var areaChange = _game.Game.AreaChangeCount;

        // One read on entry is enough: over eight maps, reads 2, 6 and 12 seconds later never
        // added a species.
        Log.FireAndForget(() =>
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddLoadedBeasts(found, areaChange);

            // Left the map, or another read took over: these files would be another area's.
            if (generation == _generation && _game.Game.AreaChangeCount == areaChange) _possible = found;
            return Task.CompletedTask;
        }, "Map beast preloads");
    }

    private void AddLoadedBeasts(HashSet<string> into, int areaChange)
    {
        var loaded = new FilesFromMemory(_game.Memory).GetAllFiles()
            .Where(f => f.Value.ChangeCount == areaChange)
            .Select(f => f.Key.Split('@')[0])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var beast in BeastCatalog.All)
        {
            if (beast.MetadataPatterns.Any(loaded.Contains)) into.Add(beast.Name);
        }
    }
}
