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
    private const string CapturedBuff = "capture_monster_captured";
    private const string TrappedBuff  = "capture_monster_trapped";

    private static readonly GameStat? CapturableStat =
        Enum.TryParse<GameStat>("IsCapturableMonster", out var stat) ? stat : null;

    // True when the entity is a rare monster that can be captured.
    public static bool IsRareCapturable(Entity entity)
    {
        if (entity == null || entity.Rarity != MonsterRarity.Rare) return false;

        // Metadata is the one signal a stat lookup can't provide, so it is checked first.
        if (MatchesCatalogMetadata(entity.Metadata)) return true;

        if (CapturableStat is { } stat && entity.Stats?.ContainsKey(stat) == true) return true;

        // A netted beast is capturable whatever the stat table says.
        var buffs = entity.Buffs;
        return buffs != null && buffs.Find(b => b.Name == CapturedBuff || b.Name == TrappedBuff) != null;
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
