using System;
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
public static class BeastCaptureStates
{
    private static readonly GameStat? CapturableMonsterStat =
        Enum.TryParse<GameStat>("IsCapturableMonster", out var stat) ? stat : null;

    private const string CapturedBuff = "capture_monster_captured";
    private const string TrappedBuff  = "capture_monster_trapped";

    // True when the entity is a rare monster that can be captured.
    public static bool IsRareCapturable(Entity entity)
    {
        if (entity == null || entity.Rarity != MonsterRarity.Rare) return false;

        // Primary check: the capturable-monster game stat.
        if (CapturableMonsterStat is { } capStat)
            return entity.Stats?.ContainsKey(capStat) == true;

        // Fallback when the stat is missing: capture buffs, then catalog metadata prefixes.
        var buffs = entity.Buffs;
        if (buffs != null && buffs.Find(b => b.Name == CapturedBuff || b.Name == TrappedBuff) != null)
            return true;

        var metadata = entity.Metadata;
        if (string.IsNullOrEmpty(metadata)) return false;
        foreach (var beast in BeastCatalog.All)
            foreach (var pattern in beast.MetadataPatterns)
                if (metadata.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }
}
