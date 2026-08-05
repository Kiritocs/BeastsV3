using System;

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

// One completed map's full analytics record.
public sealed class MapAnalyticsRecord
{
    public string MapId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CompletedAtUtc { get; set; }
    public string AreaHash { get; set; } = string.Empty;
    public string AreaName { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public int BeastsFound { get; set; }
    public int RedBeastsFound { get; set; }
    public double CapturedChaos { get; set; }
    public double CostChaos { get; set; }
    public double NetChaos { get; set; }
    public bool UsedBestiaryScarabOfDuplicating { get; set; }
    public double? FirstRedSeenSeconds { get; set; }
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
