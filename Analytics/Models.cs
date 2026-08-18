using System;

namespace BeastsV3.Analytics;

// Analytics DTOs, serialised as camelCase JSON.

// A single cost line of a map's device loadout.
public sealed class MapCostItem
{
    public string ItemName { get; set; } = string.Empty;
    public double UnitPriceChaos { get; set; }

    // True when this line was reconstructed from the map's own stats rather than read off the
    // map device, which is only readable while its window is visible and can miss a loadout
    // entirely (recording the map as free). Keeps a reconstruction distinguishable.
    public bool Inferred { get; set; }
}

// Per-beast totals for one map.
public sealed class MapBeastStat
{
    public string BeastName { get; set; } = string.Empty;
    public int Count { get; set; }

    // Number kept, already doubled when a Bestiary Scarab of Duplicating was used.
    public int CapturedCount { get; set; }

    // Whether CapturedCount includes duplication.
    public bool IsDuplicated { get; set; }

    public double UnitPriceChaos { get; set; }
    public double CapturedChaos => CapturedCount * UnitPriceChaos;
}

// A timestamped beast event within a map, used by the replay timeline.
public sealed class MapReplayEvent
{
    public string BeastName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty; // "seen" | "captured" | "missed"
    public double OffsetSeconds { get; set; }
    public double UnitPriceChaos { get; set; }
}

// The atlas tree a map was run with, captured when the map opens.
public sealed class AtlasSnapshot
{
    // Allocated atlas passive ids, from ServerData.AtlasPassiveSkillIds.
    public ushort[] AllocatedNodes { get; set; } = [];

    // pathofexile.com tree URL for AllocatedNodes. Redundant, but makes a record reviewable.
    public string TreeUrl { get; set; } = string.Empty;

    // Data-collection cohort: "A" reference tree, "B" +1 classification node, "C" +2 or more,
    // "D" off-reference, "unknown" when unreadable. See AtlasTree.ClassifyCohort.
    public string Cohort { get; set; } = "unknown";

    // True when the tree is exactly the reference set, so purity can be tightened later.
    public bool IsStrictReferenceMatch { get; set; }
}

// One completed map's full analytics record.
public sealed class MapAnalyticsRecord
{
    // Bumped when the shape changes. Telemetry rejects unknown versions; older files just
    // leave new fields at their defaults.
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string MapId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CompletedAtUtc { get; set; }
    public string AreaHash { get; set; } = string.Empty;
    public string AreaName { get; set; } = string.Empty;

    // Map tier, 0 when unknown. Spawn rates vary by tier, so this is a grouping key.
    public int MapTier { get; set; }

    // League and game version. Rates change between leagues, so aggregates must not mix them.
    public string League { get; set; } = string.Empty;
    public string GameVersion { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    public int BeastsFound { get; set; }

    // Beasts matched to a BeastCatalog entry - the red beasts.
    public int RedBeastsFound { get; set; }

    // BeastsFound - RedBeastsFound. Also catches red beasts missing from the catalog, which
    // a new league can introduce until it is updated.
    public int YellowBeastsFound { get; set; }

    public double CapturedChaos { get; set; }
    public double CostChaos { get; set; }
    public double NetChaos { get; set; }
    public bool UsedBestiaryScarabOfDuplicating { get; set; }

    // Base names of the map device items, so a submission records which scarabs were used.
    public string[] ScarabNames { get; set; } = [];

    public double? DeviceReadAgeMs { get; set; }

    // Read from the map's own stat table, and authoritative over ScarabNames where they
    // disagree - ScarabNames comes from polling the device window and can be stale or empty.
    // Null means the stat was unreadable, which is not zero; see MapModifierStats.
    // Additive, so SchemaVersion stays at 2: older readers ignore unknown fields.

    // Total additional red beasts from Herd scarabs, 5 per scarab (so 10 means two).
    public int? MapAdditionalRedBeasts { get; set; }

    // Chance for a captured beast to be duplicated, percent. 100 is the Duplicating scarab.
    public int? MapDuplicateCapturedBeastsChancePct { get; set; }
    public double? FirstRedSeenSeconds { get; set; }
    public AtlasSnapshot Atlas { get; set; } = new();
    public MapBeastStat[] BeastBreakdown { get; set; } = [];
    public MapCostItem[] CostBreakdown { get; set; } = [];
    public MapReplayEvent[] ReplayEvents { get; set; } = [];
}

// Free-text labels attached to a saved session.
public sealed class SessionTags
{
    public string Strategy { get; set; } = string.Empty;
    public string Scarab { get; set; } = string.Empty;
    public string Atlas { get; set; } = string.Empty;
    public string MapPool { get; set; } = string.Empty;
}

// Session-wide totals.
public sealed class SessionSummary
{
    public double DurationSeconds { get; set; }
    public int MapsCompleted { get; set; }
    public int BeastsFound { get; set; }
    public int RedBeastsFound { get; set; }
    public double CapturedChaos { get; set; }
    public double CostChaos { get; set; }
    public double NetChaos { get; set; }
}

// Session totals for one beast.
public sealed class BeastTotal
{
    public string BeastName { get; set; } = string.Empty;
    public int CapturedCount { get; set; }
    public double UnitPriceChaos { get; set; }
    public double CapturedChaos { get; set; }
}

// Session totals for one beast family.
public sealed class FamilyTotal
{
    public string FamilyName { get; set; } = string.Empty;
    public int CapturedCount { get; set; }
    public double CapturedChaos { get; set; }
}

// A session snapshot as written to disk.
public sealed class SavedSessionData
{
    public string SaveId { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime SavedAtUtc { get; set; }
    public bool IsAutoSave { get; set; }
    public string Name { get; set; } = string.Empty;
    public SessionTags Tags { get; set; } = new();
    public SessionSummary Summary { get; set; } = new();
    public BeastTotal[] BeastTotals { get; set; } = [];
    public FamilyTotal[] FamilyTotals { get; set; } = [];
    public MapAnalyticsRecord[] MapHistory { get; set; } = [];
    public MapCostItem[] CostDefaults { get; set; } = [];
}

// Save-session payload from the dashboard.
public sealed class SaveSessionRequest
{
    public string Name { get; set; } = string.Empty;
    public bool IsAutoSave { get; set; }
    public string StrategyTag { get; set; } = string.Empty;
    public string ScarabTag { get; set; } = string.Empty;
    public string AtlasTag { get; set; } = string.Empty;
    public string MapPoolTag { get; set; } = string.Empty;
}
