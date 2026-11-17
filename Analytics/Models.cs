using System;
using System.Collections.Generic;

namespace BeastsV3.Analytics;

// Analytics DTOs, serialised as camelCase JSON.

// A single cost line of a map's device loadout.
public sealed class MapCostItem
{
    public string ItemName { get; set; } = string.Empty;
    public double UnitPriceChaos { get; set; }

}

// Per-beast totals for one map.
public sealed class MapBeastStat
{
    public string BeastName { get; set; } = string.Empty;
    public int Count { get; set; }

    // Number kept, already doubled when the map duplicates every capture.
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
    // 3 (PoE 3.30): adds MapStats and QuestRedBeastsTotal.
    public const int CurrentSchemaVersion = 3;

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

    // Base names of the map device items (Threads; Scarabs before 3.30). The Scarab names here
    // and below are kept because saved sessions and the telemetry collector use them.
    public string[] ScarabNames { get; set; } = [];

    public double? DeviceReadAgeMs { get; set; }

    // Read from the map's own stat table, so it holds even when the device was never read.
    // Null means the stat was unreadable, which is not zero; see MapModifierStats.

    // Total additional red beasts the map's modifiers added.
    public int? MapAdditionalRedBeasts { get; set; }

    // Chance for a captured beast to be duplicated, percent.
    public int? MapDuplicateCapturedBeastsChancePct { get; set; }

    // Every beast and thread stat the map carried, by the game's stat name.
    public Dictionary<string, int> MapStats { get; set; } = new();

    // The red beast total from Einhar's quest line. Null when the line never appeared.
    public int? QuestRedBeastsTotal { get; set; }

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

    // Time in completed maps. Map History is capped, so its durations cannot rebuild this.
    public double CompletedMapsDurationSeconds { get; set; }

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

    // Autosaves only: the map being run when the save was written, so a reload or a crash
    // carries on with it. Null in named saves and before the first map.
    public MapInProgress CurrentMap { get; set; }
}

// The current map's live state, as written into an autosave and restored on start-up.
public sealed class MapInProgress
{
    public string AreaHash { get; set; } = string.Empty;
    public string AreaName { get; set; } = string.Empty;
    public int InstanceId { get; set; } = -1;

    // The map's side zones (SessionState.ActiveMapSideZones).
    public string[] SideZones { get; set; } = [];

    // Already banked to Map History. Only the identity above matters then, so walking back
    // into the instance shows it as banked instead of starting it again.
    public bool IsFinalized { get; set; }

    public bool WasComplete { get; set; }
    public double ElapsedSeconds { get; set; }
    public int BeastsFound { get; set; }
    public int RedBeastsFound { get; set; }
    public double? FirstRedSeenSeconds { get; set; }
    public int QuestRedBeastsTotal { get; set; }
    public AtlasSnapshot Atlas { get; set; } = new();
    public Dictionary<string, int> BeastCounts { get; set; } = new();
    public Dictionary<string, int> CapturedCounts { get; set; } = new();
    public EncounterInProgress[] Encounters { get; set; } = [];
    public MapReplayEvent[] ReplayEvents { get; set; } = [];

    // The beast tracker's ids for this instance, so beasts already counted are not counted again.
    public long[] CountedEntityIds { get; set; } = [];
    public long[] CapturedEntityIds { get; set; } = [];
    public int TrackerRedBeastsFound { get; set; }

    public MapCostInProgress Cost { get; set; } = new();
}

public sealed class EncounterInProgress
{
    public long EntityId { get; set; }
    public string BeastName { get; set; } = string.Empty;
    public double FirstSeenSeconds { get; set; }
    public double? CapturedSeconds { get; set; }
}

// CostTracker's current-map state.
public sealed class MapCostInProgress
{
    public MapCostItem[] Lines { get; set; } = [];
    public int? AdditionalRedBeasts { get; set; }
    public int? DuplicateCapturedChancePct { get; set; }
    public Dictionary<string, int> MapStats { get; set; } = new();
    public int RegionFreeBestiary { get; set; }
    public double? DeviceReadAgeMs { get; set; }
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
