using System;

namespace BeastsV3.Analytics.Web;

// Web-facing DTOs; property names are the JSON keys the dashboard reads.

// Live session snapshot polled by the dashboard.
public sealed class SessionCurrentResponse
{
    public DateTime GeneratedAtUtc { get; set; }
    public bool IsCurrentAreaTrackable { get; set; }
    public bool IsPaused { get; set; }
    public string ActiveAreaHash { get; set; } = string.Empty;
    public string ActiveAreaName { get; set; } = string.Empty;

    public double CurrentMapDurationSeconds { get; set; }
    public double AverageMapDurationSeconds { get; set; }
    public double SessionDurationSeconds { get; set; }

    public int CompletedMapCount { get; set; }
    public int SessionBeastsFound { get; set; }
    public int SessionRedBeastsFound { get; set; }
    public int CurrentMapBeastsFound { get; set; }
    public int CurrentMapRedBeastsFound { get; set; }

    public double CurrentMapCapturedChaos { get; set; }
    public double CurrentMapCostChaos { get; set; }
    public double CurrentMapNetChaos { get; set; }
    public bool CurrentMapUsesDuplicatingScarab { get; set; }
    public double? CurrentMapFirstRedSeenSeconds { get; set; }
    public MapCostItem[] CurrentMapCostBreakdown { get; set; } = [];
    public MapReplayEvent[] CurrentMapReplayEvents { get; set; } = [];

    public double SessionCapturedChaos { get; set; }
    public double SessionCostChaos { get; set; }
    public double SessionNetChaos { get; set; }
    public double SessionCapturedPerHourChaos { get; set; }
    public double SessionNetPerHourChaos { get; set; }
    public double AverageCapturedPerMapChaos { get; set; }
    public double AverageNetPerMapChaos { get; set; }

    public RollingStats Rolling { get; set; } = new();
    public FamilyTotal[] FamilyTotals { get; set; } = [];
    public BeastTotal[] BeastTotals { get; set; } = [];
    public string[] TrackedBeastNames { get; set; } = [];
}

// Aggregates over the last N completed maps.
public sealed class RollingStats
{
    public int WindowMapCount { get; set; }
    public double AvgCapturedChaos { get; set; }
    public double AvgNetChaos { get; set; }
    public double AvgRedsPerMap { get; set; }
    public double AvgDurationSeconds { get; set; }
    public double MedianCapturedChaos { get; set; }
    public double P90CapturedChaos { get; set; }
    public double P95CapturedChaos { get; set; }
    public double VarianceCapturedChaos { get; set; }
    public double StdDevCapturedChaos { get; set; }
    public double BestCapturedChaos { get; set; }
    public double WorstCapturedChaos { get; set; }
    public string BestAreaName { get; set; } = string.Empty;
    public string WorstAreaName { get; set; } = string.Empty;
}

// A page of map history.
public sealed class MapListResponse
{
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    public MapListItem[] Items { get; set; } = [];
}

// One map history row.
public sealed class MapListItem
{
    public string MapId { get; set; } = string.Empty;
    public DateTime CompletedAtUtc { get; set; }
    public string CompletedAtDisplay { get; set; } = string.Empty;
    public string AreaHash { get; set; } = string.Empty;
    public string AreaName { get; set; } = string.Empty;
    public int MapTier { get; set; }
    public double DurationSeconds { get; set; }
    public int BeastsFound { get; set; }
    public int RedBeastsFound { get; set; }
    public int YellowBeastsFound { get; set; }
    public double CapturedChaos { get; set; }
    public double CostChaos { get; set; }
    public double NetChaos { get; set; }
    public bool UsedBestiaryScarabOfDuplicating { get; set; }
    public double? FirstRedSeenSeconds { get; set; }
    public AtlasSnapshot Atlas { get; set; } = new();
    public MapBeastStat[] BeastBreakdown { get; set; } = [];
    public MapCostItem[] CostBreakdown { get; set; } = [];
    public MapReplayEvent[] ReplayEvents { get; set; } = [];
}

// A saved session as listed in the dashboard.
public sealed class SessionSaveListItem
{
    public string SaveId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime SavedAtUtc { get; set; }
    public string SavedAtDisplay { get; set; } = string.Empty;
    public bool IsAutoSave { get; set; }
    public SessionTags Tags { get; set; } = new();
    public SessionSummary Summary { get; set; } = new();
    public bool AlreadyLoaded { get; set; }
}

// A single saved session's full contents.
public sealed class SessionSaveDetail
{
    public SavedSessionData Session { get; set; }
}

// Parameters for an A/B session comparison.
public sealed class CompareSessionsRequest
{
    public string SaveAId { get; set; } = string.Empty;
    public string SaveBId { get; set; } = string.Empty;
    public bool MatchAreas { get; set; }
    public int TrimPercent { get; set; }
    public int MinMaps { get; set; } = 30;
}

// Result of an A/B session comparison.
public sealed class CompareSessionsResponse
{
    public bool Success { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool SampleOk { get; set; }
    public string Recommendation { get; set; } = string.Empty;
    public CompareSessionMetrics SessionA { get; set; } = new();
    public CompareSessionMetrics SessionB { get; set; } = new();
    public CompareSessionMetrics Delta { get; set; } = new();
}

// Per-side metrics of a comparison.
public sealed class CompareSessionMetrics
{
    public int Count { get; set; }
    public double DurationSeconds { get; set; }
    public double CapturedChaos { get; set; }
    public double CostChaos { get; set; }
    public double NetChaos { get; set; }
    public double Reds { get; set; }
    public double NetPerMinuteChaos { get; set; }
    public double CapturedPerMinuteChaos { get; set; }
    public double NetPerMapChaos { get; set; }
    public double CapturedPerMapChaos { get; set; }
    public double CostPerMapChaos { get; set; }
    public double RedsPerMap { get; set; }
}

// Generic success/failure result for API actions.
public sealed class ApiActionResponse
{
    public bool Success { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object Details { get; set; }

    public static ApiActionResponse Ok(string code, string message, object details = null)
        => new() { Success = true, Code = code, Message = message, Details = details };

    public static ApiActionResponse Fail(string code, string message, object details = null)
        => new() { Success = false, Code = code, Message = message, Details = details };
}

// Error body returned for failed requests.
public sealed class ApiErrorResponse
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object Details { get; set; }
}

// Save-session request from the dashboard.
public sealed class CreateSessionSaveRequest
{
    public string Name { get; set; } = string.Empty;
    public string StrategyTag { get; set; } = string.Empty;
    public string ScarabTag { get; set; } = string.Empty;
    public string AtlasTag { get; set; } = string.Empty;
    public string MapPoolTag { get; set; } = string.Empty;
    public bool IsAutoSave { get; set; }
}
