using System;
using System.Collections.Generic;
using BeastsV3.Shared;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace BeastsV3.Beasts;

// Capture progress of a beast.
public enum BeastCaptureState
{
    None,
    Capturing,
    Captured,
}

// Identifies capturable rare monsters.
//
// ExileCore's GameStat enum is a snapshot of the game's stat table, so a client patch that
// inserts rows shifts every index after the insertion point. IsCapturableMonster sits near the
// end of that table and has drifted, which made the stat lookup miss on every beast: no counter,
// no markers, no overlay. The enum value is therefore only a starting guess -- the real index is
// re-learned from beasts the catalog recognises on metadata alone, which needs no stat at all.
public static class BeastCaptureStates
{
    private const string CapturedBuff = "capture_monster_captured";
    private const string TrappedBuff  = "capture_monster_trapped";

    // Index the current ExileCore build claims for the stat, or -1 when the name is gone.
    private static readonly int DeclaredCapturableStat =
        Enum.TryParse<GameStat>("IsCapturableMonster", out var stat) ? (int)stat : -1;

    // How far either side of the declared index a drifted stat is still recognised. Patches
    // shift the tail of the table by a handful of rows, not by hundreds.
    private const int SearchRadius = 32;

    // Index learned from catalog beasts; -1 until calibration narrows the window to one hit.
    private static int _resolvedCapturableStat = -1;

    // Indices still consistent with every catalog beast seen so far. Narrowed by intersection.
    private static HashSet<int> _candidates;

    // True when the entity is a rare monster that can be captured.
    public static bool IsRareCapturable(Entity entity)
    {
        if (entity == null || entity.Rarity != MonsterRarity.Rare) return false;

        // Metadata is the one signal no stat-table shift can break, so it is checked first.
        if (MatchesCatalogMetadata(entity.Metadata))
        {
            CalibrateFrom(entity);
            return true;
        }

        if (HasCapturableStat(entity)) return true;

        // A netted beast is capturable whatever the stat table says.
        var buffs = entity.Buffs;
        return buffs != null && buffs.Find(b => b.Name == CapturedBuff || b.Name == TrappedBuff) != null;
    }

    // Reads the capturable stat at the learned index, falling back to the declared one until
    // calibration succeeds.
    private static bool HasCapturableStat(Entity entity)
    {
        var index = _resolvedCapturableStat >= 0 ? _resolvedCapturableStat : DeclaredCapturableStat;
        if (index < 0) return false;

        return entity.Stats?.ContainsKey((GameStat)index) == true;
    }

    // Learns the live stat index from an entity already known to be a beast. Every index the
    // entity carries within the search window is a candidate; intersecting across beasts drops
    // the unrelated ones until a single index remains.
    public static void CalibrateFrom(Entity entity)
    {
        if (_resolvedCapturableStat >= 0 || DeclaredCapturableStat < 0) return;

        var stats = entity?.Stats;
        if (stats == null || stats.Count == 0) return;

        var present = new HashSet<int>();
        foreach (var key in stats.Keys)
        {
            var index = (int)key;
            if (Math.Abs(index - DeclaredCapturableStat) <= SearchRadius) present.Add(index);
        }

        // Stats not populated yet in this window: no information, so the candidate set is left
        // untouched rather than being emptied.
        if (present.Count == 0) return;

        if (_candidates == null)
        {
            _candidates = present;
        }
        else
        {
            _candidates.IntersectWith(present);

            // Disjoint readings mean the earlier sample was noise; restart from this one.
            if (_candidates.Count == 0) _candidates = present;
        }

        if (_candidates.Count != 1) return;

        foreach (var index in _candidates) _resolvedCapturableStat = index;
        _candidates = null;

        if (_resolvedCapturableStat != DeclaredCapturableStat)
        {
            Log.Info($"IsCapturableMonster resolved to stat index {_resolvedCapturableStat}; ExileCore " +
                     $"declares {DeclaredCapturableStat}. The client's stat table has shifted by " +
                     $"{_resolvedCapturableStat - DeclaredCapturableStat:+#;-#;0}.");
        }
    }

    // True when the entity's metadata matches a catalog beast prefix.
    private static bool MatchesCatalogMetadata(string metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return false;

        foreach (var beast in BeastCatalog.All)
            foreach (var pattern in beast.MetadataPatterns)
                if (metadata.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }
}
