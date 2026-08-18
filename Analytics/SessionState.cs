using System;
using System.Collections.Generic;

namespace BeastsV3.Analytics;

// One place for all live session/map counters.
public sealed class SessionState
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    // Session timers ---------------------------------------------------------
    public DateTime SessionStartUtc { get; set; } = DateTime.UtcNow;
    public TimeSpan LoadedSessionsDuration { get; set; }

    // Set while the escape menu is open; holds the map clock but not session time.
    public DateTime? PauseMenuStartUtc { get; set; }

    // Current-area / current-map state --------------------------------------
    public bool IsCurrentAreaTrackable { get; set; }
    public string ActiveMapAreaHash { get; set; } = string.Empty;
    public string ActiveMapAreaName { get; set; } = string.Empty;
    public int ActiveMapInstanceId { get; set; } = -1;
    public bool CurrentMapWasComplete { get; set; }
    public bool MapWasFinalized { get; set; }

    // True while inside a re-entered map that was already banked to Map History.
    public bool IsInFinalizedMap { get; set; }

    public DateTime? CurrentMapStartUtc { get; set; }
    public TimeSpan CurrentMapElapsed { get; set; }
    public int CurrentMapBeastsFound { get; set; }
    public int CurrentMapRedBeastsFound { get; set; }
    public double? CurrentMapFirstRedSeenSeconds { get; set; }

    // The atlas tree as it was when this map opened. Captured on entry rather than on
    // completion, because the tree can be respecced mid-session and the snapshot has to
    // describe the map that actually ran.
    public AtlasSnapshot CurrentMapAtlas { get; set; } = new();
    public Dictionary<string, int> CurrentMapValuableBeastCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> CurrentMapValuableBeastCapturedCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<long, BeastEncounter> CurrentMapEncounters { get; } = new();
    public List<MapReplayEvent> CurrentMapReplayEvents { get; } = new();

    // Session-wide aggregates -----------------------------------------------
    public int SessionBeastsFound { get; set; }
    public int SessionRedBeastsFound { get; set; }
    public int CompletedMapCount { get; set; }
    public TimeSpan CompletedMapsDuration { get; set; }
    public List<MapAnalyticsRecord> MapHistory { get; } = new();

    // Returns elapsed session time plus any loaded sessions' duration.
    public TimeSpan GetTotalTime(DateTime nowUtc)
    {
        var raw = nowUtc - SessionStartUtc;
        if (raw < TimeSpan.Zero) raw = TimeSpan.Zero;
        return raw + LoadedSessionsDuration;
    }

    // Returns seconds since the current map started.
    public double CurrentMapReplayOffsetSeconds(DateTime nowUtc)
    {
        if (!CurrentMapStartUtc.HasValue) return 0;
        var offset = (nowUtc - CurrentMapStartUtc.Value).TotalSeconds;
        return Math.Max(0, offset);
    }
}

// When a beast was first seen and captured within the current map.
public sealed class BeastEncounter
{
    public string BeastName { get; set; } = string.Empty;
    public double FirstSeenSeconds { get; set; }
    public double? CapturedSeconds { get; set; }
}
