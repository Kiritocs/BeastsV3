using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Prices;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;

namespace BeastsV3.Analytics;

// Routes lifecycle and tracker events into SessionState, finalizes maps, autosaves and
// applies loaded sessions.
public sealed class SessionRecorder
{
    private const int MaxMapHistoryEntries = 200;

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly BeastTracker _tracker;
    private readonly PriceService _prices;
    private readonly CostTracker _cost;
    private readonly SessionStore _store;

    public SessionState State { get; } = new();

    // Imported sessions, kept so they can be unloaded again.
    private readonly HashSet<string> _loadedSaveIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SavedSessionData> _loadedSaves = new(StringComparer.OrdinalIgnoreCase);

    // Save ids currently merged into this session.
    public IReadOnlyCollection<string> LoadedSaveIds => _loadedSaveIds;

    // Raised once per map actually completed and banked. Not raised for records merged in
    // from a loaded session, or importing one would resubmit someone else's maps.
    public event Action<MapAnalyticsRecord> MapFinalized;

    public SessionRecorder(
        GameController game,
        BeastsSettings settings,
        BeastTracker tracker,
        PriceService prices,
        CostTracker cost,
        SessionStore store)
    {
        _game = game;
        _settings = settings;
        _tracker = tracker;
        _prices = prices;
        _cost = cost;
        _store = store;

        _tracker.RareBeastSeen += OnRareBeastSeen;
        _tracker.BeastCaptured += OnBeastCaptured;
    }

    // Unsubscribes from tracker events.
    public void Detach()
    {
        _tracker.RareBeastSeen -= OnRareBeastSeen;
        _tracker.BeastCaptured -= OnBeastCaptured;
    }

    // Per-frame update of pause state, the map timer and quest completion.
    public void Tick(DateTime nowUtc)
    {
        if (!IsAnalyticsEnabled()) return;

        ApplyPauseMenuState(nowUtc);
        UpdateCurrentMapTimer(nowUtc);
        DetectQuestMissionComplete();
    }

    // Applies an area change: finalizes the previous map if needed and restarts the timer.
    public void OnAreaTransition(AreaTransitionDecision decision, DateTime nowUtc)
    {
        if (!IsAnalyticsEnabled()) return;

        PauseCurrentMapTimer(nowUtc);

        if (decision.ShouldFinalizePreviousMap)
        {
            FinalizeCurrentMap(decision.PreviousAreaHash, decision.PreviousAreaName, nowUtc);
            AutoSave();
        }

        if (decision.Kind == AreaTransitionKind.EnteredNewTrackableMap)
        {
            ResetCurrentMapState();
            _cost.BeginCurrentFromPrepared();
            CaptureAtlasSnapshot();
        }
        else if (decision.Kind == AreaTransitionKind.ReenteredActiveMap)
        {
            // Another chance to read the map's stats, covering a map unreadable in its first seconds.
            _cost.ResumeMapModifierPolling();
        }

        RestartCurrentMapTimer(nowUtc);
    }

    // Manually banks the map currently being run, without waiting for an area transition.
    // Exists for runs that never trigger DetectQuestMissionComplete.
    public bool CompleteCurrentMapManually(DateTime nowUtc)
    {
        if (!IsAnalyticsEnabled()) return false;

        // Already finalized (a previous press, or normal completion): re-entering the same
        // instance later must not double-count it.
        if (State.MapWasFinalized) return false;

        var hasProgress = State.CurrentMapBeastsFound > 0 || State.CurrentMapRedBeastsFound > 0 ||
                           State.CurrentMapElapsed > TimeSpan.Zero;
        if (!hasProgress) return false;

        // Captured before it gets cleared below - decides whether IsInFinalizedMap applies.
        var wasStandingInMap = State.IsCurrentAreaTrackable;

        var areaHash = State.ActiveMapAreaHash;
        var areaName = State.ActiveMapAreaName;

        // No-op if the timer is already paused; banks whatever elapsed was recorded on the way out.
        PauseCurrentMapTimer(nowUtc);
        FinalizeCurrentMap(areaHash, areaName, nowUtc);
        AutoSave();

        // Done either way: stop the clock and flag the instance as finalized.
        State.IsCurrentAreaTrackable = false;
        State.MapWasFinalized = true;
        State.CurrentMapWasComplete = false;
        State.CurrentMapStartUtc = null;

        // IsInFinalizedMap means "standing inside a map that has already been banked".
        State.IsInFinalizedMap = wasStandingInMap;

        return true;
    }

    // Starts or resumes the map clock; leaves it stopped in untracked areas.
    private void RestartCurrentMapTimer(DateTime nowUtc)
    {
        if (!State.IsCurrentAreaTrackable)
        {
            State.CurrentMapStartUtc = null;
            return;
        }

        // Origin is back-dated by already-banked time so elapsed resumes rather than resets.
        State.CurrentMapStartUtc = nowUtc - State.CurrentMapElapsed;
    }

    // Clears all session and map state and starts a new session id.
    public void ResetSession(DateTime nowUtc, bool startNewCurrentMapTimer)
    {
        State.SessionId = Guid.NewGuid().ToString("N");
        State.SessionStartUtc = nowUtc;
        State.LoadedSessionsDuration = TimeSpan.Zero;
        State.PauseMenuStartUtc = null;
        State.SessionBeastsFound = 0;
        State.SessionRedBeastsFound = 0;
        State.CompletedMapCount = 0;
        State.CompletedMapsDuration = TimeSpan.Zero;
        State.MapHistory.Clear();

        _loadedSaveIds.Clear();
        _loadedSaves.Clear();

        ResetCurrentMapState();
        _cost.ResetCurrent();
        if (startNewCurrentMapTimer && State.IsCurrentAreaTrackable)
        {
            State.CurrentMapStartUtc = nowUtc;
        }
    }

    // Clears completed-map counts and history, leaving session totals intact.
    public void ResetMapAverage()
    {
        State.CompletedMapCount = 0;
        State.CompletedMapsDuration = TimeSpan.Zero;
        State.MapHistory.Clear();
    }

    // Builds a save payload from the current live state.
    public SavedSessionData BuildSavedSessionData(DateTime nowUtc, SaveSessionRequest request)
    {
        var mapHistoryOrdered = State.MapHistory
            .OrderByDescending(x => x.CompletedAtUtc)
            .Select(Clone)
            .ToArray();

        var completedCaptured = mapHistoryOrdered.Sum(x => x.CapturedChaos);
        var completedCost = mapHistoryOrdered.Sum(x => x.CostChaos);
        var currentCaptured = ComputeCurrentMapCapturedChaos();
        var currentCost = State.IsCurrentAreaTrackable ? _cost.ComputeCurrentCostChaos() : 0d;

        var (beastTotals, familyTotals) = BuildTotals(includeCurrentMap: true);

        return new SavedSessionData
        {
            SaveId = Guid.NewGuid().ToString("N"),
            SessionId = string.IsNullOrWhiteSpace(State.SessionId) ? Guid.NewGuid().ToString("N") : State.SessionId,
            SavedAtUtc = nowUtc,
            IsAutoSave = request?.IsAutoSave == true,
            Name = BuildDisplayName(request?.Name, request?.IsAutoSave == true),
            Tags = new SessionTags
            {
                Strategy = NormalizeTag(request?.StrategyTag),
                Scarab = NormalizeTag(request?.ScarabTag),
                Atlas = NormalizeTag(request?.AtlasTag),
                MapPool = NormalizeTag(request?.MapPoolTag),
            },
            Summary = new SessionSummary
            {
                DurationSeconds = State.GetTotalTime(nowUtc).TotalSeconds,
                MapsCompleted = State.CompletedMapCount,
                BeastsFound = State.SessionBeastsFound,
                RedBeastsFound = State.SessionRedBeastsFound,
                CapturedChaos = completedCaptured + currentCaptured,
                CostChaos = completedCost + currentCost,
                NetChaos = (completedCaptured + currentCaptured) - (completedCost + currentCost),
            },
            BeastTotals = beastTotals,
            FamilyTotals = familyTotals,
            MapHistory = mapHistoryOrdered,
            CostDefaults = _cost.Prepared.Select(Clone).ToArray(),
        };
    }

    // Writes an autosave unless the session is still empty.
    public bool AutoSave()
    {
        if (!IsAnalyticsEnabled()) return false;
        if (State.MapHistory.Count == 0 && State.SessionBeastsFound == 0) return false;

        var data = BuildSavedSessionData(DateTime.UtcNow, new SaveSessionRequest { IsAutoSave = true });
        return _store.SaveAutoSave(data);
    }

    // Writes a named save of the current session.
    public bool SaveNamed(string name)
    {
        if (!IsAnalyticsEnabled()) return false;

        var data = BuildSavedSessionData(DateTime.UtcNow, new SaveSessionRequest { Name = name });
        return _store.SaveNamed(data);
    }

    // Merges a saved session's totals and map history into the live session.
    public bool ApplyLoadedSession(SavedSessionData data)
    {
        if (data == null || string.IsNullOrWhiteSpace(data.SaveId)) return false;
        if (!_loadedSaveIds.Add(data.SaveId)) return false;
        _loadedSaves[data.SaveId] = data;

        var summary = data.Summary ?? new SessionSummary();
        State.LoadedSessionsDuration += TimeSpan.FromSeconds(Math.Max(0, summary.DurationSeconds));
        State.SessionBeastsFound += Math.Max(0, summary.BeastsFound);
        State.SessionRedBeastsFound += Math.Max(0, summary.RedBeastsFound);
        State.CompletedMapCount += Math.Max(0, summary.MapsCompleted);
        State.CompletedMapsDuration += TimeSpan.FromSeconds(Math.Max(0,
            (data.MapHistory ?? []).Sum(x => x.DurationSeconds)));

        foreach (var record in data.MapHistory ?? [])
        {
            InsertMapRecord(Clone(record));
        }
        return true;
    }

    // Subtracts a previously merged session back out of the live session.
    public bool RemoveLoadedSession(string saveId)
    {
        if (string.IsNullOrWhiteSpace(saveId)) return false;
        if (!_loadedSaves.TryGetValue(saveId, out var data)) return false;

        _loadedSaves.Remove(saveId);
        _loadedSaveIds.Remove(saveId);

        var summary = data.Summary ?? new SessionSummary();
        State.LoadedSessionsDuration -= TimeSpan.FromSeconds(Math.Max(0, summary.DurationSeconds));
        if (State.LoadedSessionsDuration < TimeSpan.Zero) State.LoadedSessionsDuration = TimeSpan.Zero;
        State.SessionBeastsFound = Math.Max(0, State.SessionBeastsFound - Math.Max(0, summary.BeastsFound));
        State.SessionRedBeastsFound = Math.Max(0, State.SessionRedBeastsFound - Math.Max(0, summary.RedBeastsFound));
        State.CompletedMapCount = Math.Max(0, State.CompletedMapCount - Math.Max(0, summary.MapsCompleted));
        State.CompletedMapsDuration -= TimeSpan.FromSeconds(Math.Max(0,
            (data.MapHistory ?? []).Sum(x => x.DurationSeconds)));
        if (State.CompletedMapsDuration < TimeSpan.Zero) State.CompletedMapsDuration = TimeSpan.Zero;

        var idsToRemove = new HashSet<string>(
            (data.MapHistory ?? []).Select(x => x.MapId).Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);
        if (idsToRemove.Count > 0)
        {
            State.MapHistory.RemoveAll(x => idsToRemove.Contains(x.MapId));
        }
        return true;
    }

    // ---- private ----------------------------------------------------------

    private bool IsAnalyticsEnabled() => _settings.Analytics.Enable.Value;

    // Counts a newly seen rare beast and opens its encounter record.
    private void OnRareBeastSeen(long entityId, string beastName, DateTime nowUtc)
    {
        if (!IsAnalyticsEnabled() || !State.IsCurrentAreaTrackable) return;

        State.CurrentMapBeastsFound++;
        State.SessionBeastsFound++;

        if (string.IsNullOrWhiteSpace(beastName)) return;

        State.CurrentMapRedBeastsFound++;
        State.SessionRedBeastsFound++;

        // Replay events are recorded only for catalog beasts, once each.
        if (State.CurrentMapEncounters.ContainsKey(entityId)) return;

        var offsetSeconds = State.CurrentMapReplayOffsetSeconds(nowUtc);
        State.CurrentMapEncounters[entityId] = new BeastEncounter
        {
            BeastName = beastName,
            FirstSeenSeconds = offsetSeconds,
        };

        if (!State.CurrentMapFirstRedSeenSeconds.HasValue)
        {
            State.CurrentMapFirstRedSeenSeconds = offsetSeconds;
        }

        State.CurrentMapValuableBeastCounts[beastName] =
            State.CurrentMapValuableBeastCounts.TryGetValue(beastName, out var prev) ? prev + 1 : 1;

        State.CurrentMapReplayEvents.Add(new MapReplayEvent
        {
            BeastName = beastName,
            EventType = "seen",
            OffsetSeconds = offsetSeconds,
            UnitPriceChaos = GetUnitPriceChaos(beastName),
        });
    }

    // Records a capture against its encounter and the map's captured counts.
    private void OnBeastCaptured(long entityId, string beastName, DateTime nowUtc)
    {
        if (!IsAnalyticsEnabled() || !State.IsCurrentAreaTrackable) return;
        if (string.IsNullOrWhiteSpace(beastName)) return;

        if (!State.CurrentMapEncounters.TryGetValue(entityId, out var encounter))
        {
            OnRareBeastSeen(entityId, beastName, nowUtc);
            if (!State.CurrentMapEncounters.TryGetValue(entityId, out encounter)) return;
        }
        if (encounter.CapturedSeconds.HasValue) return;

        var offsetSeconds = State.CurrentMapReplayOffsetSeconds(nowUtc);
        encounter.CapturedSeconds = offsetSeconds;

        // A Bestiary Scarab of Duplicating yields two beasts per capture.
        var captureMultiplier = _cost.CurrentMapUsesDuplicatingScarab ? 2 : 1;
        State.CurrentMapValuableBeastCapturedCounts[beastName] =
            State.CurrentMapValuableBeastCapturedCounts.TryGetValue(beastName, out var prev)
                ? prev + captureMultiplier
                : captureMultiplier;

        // One replay event per capture, regardless of duplication.
        State.CurrentMapReplayEvents.Add(new MapReplayEvent
        {
            BeastName = beastName,
            EventType = "captured",
            OffsetSeconds = offsetSeconds,
            UnitPriceChaos = GetUnitPriceChaos(beastName),
        });
    }

    // Sets or clears the pause stamp from the escape-menu state.
    private void ApplyPauseMenuState(DateTime nowUtc)
    {
        var isPaused = _game?.Game?.IsEscapeState == true;
        if (isPaused)
        {
            State.PauseMenuStartUtc ??= nowUtc;
        }
        else
        {
            State.PauseMenuStartUtc = null;
        }
    }

    // Recomputes map elapsed time, holding it steady while paused.
    private void UpdateCurrentMapTimer(DateTime nowUtc)
    {
        if (!State.IsCurrentAreaTrackable || !State.CurrentMapStartUtc.HasValue) return;

        if (State.PauseMenuStartUtc.HasValue)
        {
            // While paused the origin is re-anchored each frame so elapsed does not advance.
            State.CurrentMapStartUtc = nowUtc - State.CurrentMapElapsed;
            return;
        }

        var elapsed = nowUtc - State.CurrentMapStartUtc.Value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        State.CurrentMapElapsed = elapsed;
    }

    // Quest text is polled at this interval; the completion test runs every frame.
    private static readonly TimeSpan QuestPollInterval = TimeSpan.FromMilliseconds(250);
    private DateTime _questPolledUtc = DateTime.MinValue;
    private int _questTotal;
    private bool _questMissionComplete;

    // Latches CurrentMapWasComplete once the map counts as done.
    private void DetectQuestMissionComplete()
    {
        if (State.CurrentMapWasComplete) return;
        if (!State.IsCurrentAreaTrackable) return;

        var now = DateTime.UtcNow;
        if (now - _questPolledUtc >= QuestPollInterval)
        {
            _questPolledUtc = now;
            _questTotal = BeastQuest.TryGetProgress(_game, out _, out var total) && total > 0 ? total : 0;
            _questMissionComplete = BeastQuest.IsMissionComplete(_game);
        }

        // Same rule the counter overlay uses.
        if (MapCompletion.IsComplete(_questMissionComplete, _questTotal, _tracker.RareBeastsFound,
                _tracker.AllTrackedValuableBeastsCaptured()))
        {
            State.CurrentMapWasComplete = true;
        }
    }

    // Banks elapsed time and stops the map clock.
    private void PauseCurrentMapTimer(DateTime nowUtc)
    {
        if (!State.CurrentMapStartUtc.HasValue) return;
        var elapsed = nowUtc - State.CurrentMapStartUtc.Value;
        if (elapsed > TimeSpan.Zero) State.CurrentMapElapsed = elapsed;
        State.CurrentMapStartUtc = null;
    }

    // Writes the current map to history and resets map state; empty maps are discarded.
    private void FinalizeCurrentMap(string areaHash, string areaName, DateTime nowUtc)
    {
        if (State.CurrentMapBeastsFound <= 0 &&
            State.CurrentMapRedBeastsFound <= 0 &&
            State.CurrentMapElapsed <= TimeSpan.Zero)
        {
            ResetCurrentMapState();
            return;
        }

        var record = BuildMapRecord(areaHash, areaName, nowUtc);

        // The numbers that landed in Map History, as they landed. Says whether a wrongly reported
        // row was built wrong or is being displayed wrong.
        Log.Info($"Map finalized '{record.AreaName}': {record.DurationSeconds:0}s, " +
                 $"beasts={record.BeastsFound} red={record.RedBeastsFound}, " +
                 $"captured={record.CapturedChaos:0.#}c cost={record.CostChaos:0.#}c net={record.NetChaos:0.#}c, " +
                 $"dupScarab={record.UsedBestiaryScarabOfDuplicating}, " +
                 $"costLines={record.CostBreakdown.Length} beastRows={record.BeastBreakdown.Length} " +
                 $"replayEvents={record.ReplayEvents.Length}");

        InsertMapRecord(record);
        State.CompletedMapCount++;
        State.CompletedMapsDuration += TimeSpan.FromSeconds(Math.Max(0, record.DurationSeconds));

        // A throwing subscriber must never break map recording, so it is swallowed here.
        try { MapFinalized?.Invoke(record); }
        catch (Exception ex) { Log.Debug($"MapFinalized handler threw. {ex.GetType().Name}: {ex.Message}"); }

        ResetCurrentMapState();
    }

    // Builds the analytics record for the map that just ended.
    private MapAnalyticsRecord BuildMapRecord(string areaHash, string areaName, DateTime nowUtc)
    {
        var costBreakdown = _cost.SnapshotCurrent();
        var costChaos = costBreakdown.Sum(x => x.UnitPriceChaos);

        // Resolved once and stamped onto every breakdown row.
        var usedDupScarab = _cost.CurrentMapUsesDuplicatingScarab;

        var breakdown = State.CurrentMapValuableBeastCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                var captured = State.CurrentMapValuableBeastCapturedCounts.TryGetValue(x.Key, out var c) ? c : 0;
                var unit = GetUnitPriceChaos(x.Key);
                return new MapBeastStat
                {
                    BeastName = x.Key,
                    Count = x.Value,
                    CapturedCount = captured,
                    IsDuplicated = usedDupScarab && captured > 0,
                    UnitPriceChaos = unit,
                };
            })
            .ToArray();

        var capturedChaos = breakdown.Sum(x => x.CapturedChaos);

        var beastsFound = Math.Max(0, State.CurrentMapBeastsFound);
        var redBeastsFound = Math.Max(0, State.CurrentMapRedBeastsFound);

        return new MapAnalyticsRecord
        {
            SchemaVersion = MapAnalyticsRecord.CurrentSchemaVersion,
            MapId = Guid.NewGuid().ToString("N"),
            CompletedAtUtc = nowUtc,
            AreaHash = areaHash ?? string.Empty,
            AreaName = areaName ?? string.Empty,
            MapTier = ParseMapTier(costBreakdown),
            League = _settings.BeastPrices.League.Value?.Trim() ?? string.Empty,
            DurationSeconds = Math.Max(0, State.CurrentMapElapsed.TotalSeconds),
            BeastsFound = beastsFound,
            RedBeastsFound = redBeastsFound,

            // Uncatalogd capturables are yellow in the normal case; a new or renamed red also lands
            // here until BeastCatalog is updated, which LogUnknownMetadataOnce surfaces.
            YellowBeastsFound = Math.Max(0, beastsFound - redBeastsFound),

            CapturedChaos = capturedChaos,
            CostChaos = costChaos,
            NetChaos = capturedChaos - costChaos,
            UsedBestiaryScarabOfDuplicating = usedDupScarab,
            ScarabNames = costBreakdown
                .Select(x => x.ItemName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray(),

            // How stale the device reading was when the map was armed. See MapAnalyticsRecord.
            DeviceReadAgeMs = _cost?.CurrentDeviceReadAgeMs,

            // What the game actually applied, latched from the map's stats. Beats the device reading.
            MapAdditionalRedBeasts = _cost?.CurrentAdditionalRedBeasts,
            MapDuplicateCapturedBeastsChancePct = _cost?.CurrentDuplicateCapturedChancePct,
            FirstRedSeenSeconds = State.CurrentMapFirstRedSeenSeconds,
            Atlas = State.CurrentMapAtlas ?? new AtlasSnapshot(),
            BeastBreakdown = breakdown,
            CostBreakdown = costBreakdown,
            ReplayEvents = BuildReplayEvents(),
        };
    }

    // Builds the map's replay timeline, adding "missed" events for uncaptured encounters.
    private MapReplayEvent[] BuildReplayEvents()
    {
        var events = State.CurrentMapReplayEvents
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.BeastName) && !string.IsNullOrWhiteSpace(e.EventType))
            .Select(e => new MapReplayEvent
            {
                BeastName = e.BeastName,
                EventType = e.EventType,
                OffsetSeconds = e.OffsetSeconds,
                UnitPriceChaos = e.UnitPriceChaos,
            })
            .ToList();

        // Encounters without a capture become "missed" events at the map's end.
        var finalOffset = Math.Max(0, State.CurrentMapElapsed.TotalSeconds);
        foreach (var encounter in State.CurrentMapEncounters.Values)
        {
            if (encounter.CapturedSeconds.HasValue) continue;
            events.Add(new MapReplayEvent
            {
                BeastName = encounter.BeastName,
                EventType = "missed",
                OffsetSeconds = Math.Max(finalOffset, encounter.FirstSeenSeconds),
                UnitPriceChaos = GetUnitPriceChaos(encounter.BeastName),
            });
        }

        return events
            .OrderBy(x => x.OffsetSeconds)
            .ThenBy(x => EventTypeSortOrder(x.EventType))
            .ThenBy(x => x.BeastName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Prepends a record to map history and trims to MaxMapHistoryEntries.
    private void InsertMapRecord(MapAnalyticsRecord record)
    {
        State.MapHistory.Insert(0, record);
        if (State.MapHistory.Count > MaxMapHistoryEntries)
            State.MapHistory.RemoveRange(MaxMapHistoryEntries, State.MapHistory.Count - MaxMapHistoryEntries);
    }

    // Records the atlas tree for the map being entered.
    private void CaptureAtlasSnapshot()
    {
        State.CurrentMapAtlas = AtlasTree.Capture(_game);

        var snapshot = State.CurrentMapAtlas;
        if (snapshot.AllocatedNodes.Length == 0)
        {
            Log.Debug("Atlas tree unreadable on map entry; map recorded without a tree.");
            return;
        }

        Log.Debug($"Atlas tree captured: {snapshot.AllocatedNodes.Length} nodes, " +
                  $"cohort={snapshot.Cohort} strict={snapshot.IsStrictReferenceMatch}");
    }

    // Map tier from the cost tracker, which reads MapKey.Tier and writes a "Map (Tier N)"
    // cost line. 0 when absent.
    private static int ParseMapTier(IEnumerable<MapCostItem> costBreakdown)
    {
        foreach (var item in costBreakdown ?? [])
        {
            var name = item?.ItemName;
            if (string.IsNullOrEmpty(name)) continue;

            var match = MapTierPattern.Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var tier)) return tier;
        }
        return 0;
    }

    private static readonly Regex MapTierPattern =
        new(@"^Map \(Tier (\d+)\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Clears all current-map counters, encounters and cached quest data.
    private void ResetCurrentMapState()
    {
        _questPolledUtc = DateTime.MinValue;
        _questTotal = 0;
        _questMissionComplete = false;
        BeastQuest.ResetLogState();

        State.CurrentMapElapsed = TimeSpan.Zero;
        State.CurrentMapBeastsFound = 0;
        State.CurrentMapRedBeastsFound = 0;
        State.CurrentMapFirstRedSeenSeconds = null;
        State.CurrentMapValuableBeastCounts.Clear();
        State.CurrentMapValuableBeastCapturedCounts.Clear();
        State.CurrentMapEncounters.Clear();
        State.CurrentMapReplayEvents.Clear();
        State.CurrentMapAtlas = new AtlasSnapshot();
    }

    // Chaos value of everything captured in the current map.
    private double ComputeCurrentMapCapturedChaos()
    {
        var total = 0d;
        foreach (var (name, count) in State.CurrentMapValuableBeastCapturedCounts)
        {
            if (count <= 0) continue;
            total += count * GetUnitPriceChaos(name);
        }
        return total;
    }

    // Per-beast and per-family session totals for the dashboard.
    public (BeastTotal[] beastTotals, FamilyTotal[] familyTotals) BuildSessionTotals(bool includeCurrentMap) =>
        BuildTotals(includeCurrentMap);

    // Current-map replay events without the inferred "missed" entries.
    public MapReplayEvent[] BuildCurrentMapReplayEventsForSnapshot()
    {
        var events = State.CurrentMapReplayEvents
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.BeastName) && !string.IsNullOrWhiteSpace(e.EventType))
            .Select(e => new MapReplayEvent
            {
                BeastName = e.BeastName,
                EventType = e.EventType,
                OffsetSeconds = e.OffsetSeconds,
                UnitPriceChaos = e.UnitPriceChaos,
            })
            .OrderBy(x => x.OffsetSeconds)
            .ThenBy(x => EventTypeSortOrder(x.EventType))
            .ThenBy(x => x.BeastName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return events;
    }

    // Sums captured counts and chaos across map history, optionally including the live map.
    private (BeastTotal[] beastTotals, FamilyTotal[] familyTotals) BuildTotals(bool includeCurrentMap)
    {
        var enabled = _settings.BeastPrices.EnabledBeasts;
        var beastNames = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);

        var capturedCounts = beastNames.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        var capturedChaos = beastNames.ToDictionary(x => x, _ => 0d, StringComparer.OrdinalIgnoreCase);
        var unitPrices = beastNames.ToDictionary(x => x, x => (double)GetUnitPriceChaos(x), StringComparer.OrdinalIgnoreCase);

        foreach (var record in State.MapHistory)
        {
            foreach (var stat in record.BeastBreakdown ?? [])
            {
                if (stat == null || !beastNames.Contains(stat.BeastName)) continue;
                capturedCounts[stat.BeastName] += Math.Max(0, stat.CapturedCount);
                capturedChaos[stat.BeastName] += Math.Max(0, stat.CapturedCount) * Math.Max(0, stat.UnitPriceChaos);
                if (stat.UnitPriceChaos > 0) unitPrices[stat.BeastName] = stat.UnitPriceChaos;
            }
        }

        if (includeCurrentMap)
        {
            foreach (var (name, count) in State.CurrentMapValuableBeastCapturedCounts)
            {
                if (count <= 0 || !beastNames.Contains(name)) continue;
                capturedCounts[name] += count;
                var unit = GetUnitPriceChaos(name);
                if (unit > 0)
                {
                    capturedChaos[name] += count * unit;
                    unitPrices[name] = unit;
                }
            }
        }

        var beastTotals = beastNames
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(name => new BeastTotal
            {
                BeastName = name,
                CapturedCount = capturedCounts[name],
                UnitPriceChaos = unitPrices[name],
                CapturedChaos = capturedChaos[name],
            })
            .ToArray();

        var familyTotals = beastTotals
            .GroupBy(x => BeastCatalog.GetFamily(x.BeastName), StringComparer.OrdinalIgnoreCase)
            .Select(g => new FamilyTotal
            {
                FamilyName = g.Key,
                CapturedCount = g.Sum(x => x.CapturedCount),
                CapturedChaos = g.Sum(x => x.CapturedChaos),
            })
            .OrderByDescending(x => x.CapturedChaos)
            .ThenBy(x => x.FamilyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return (beastTotals, familyTotals);
    }

    // Current chaos price of a beast, or 0 when unpriced.
    private float GetUnitPriceChaos(string beastName) =>
        _prices.BeastPrices.TryGetValue(beastName ?? string.Empty, out var price) && price > 0 ? price : 0f;

    private static int EventTypeSortOrder(string eventType) =>
        eventType switch { "seen" => 0, "captured" => 1, "missed" => 2, _ => 99 };

    private static string NormalizeTag(string tag) => (tag ?? string.Empty).Trim();

    private static string BuildDisplayName(string name, bool isAutoSave) =>
        !string.IsNullOrWhiteSpace(name) ? name.Trim() : (isAutoSave ? "AutoSave" : "Session");

    private static MapAnalyticsRecord Clone(MapAnalyticsRecord source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        MapId = source.MapId,
        CompletedAtUtc = source.CompletedAtUtc,
        AreaHash = source.AreaHash,
        AreaName = source.AreaName,
        MapTier = source.MapTier,
        League = source.League,
        GameVersion = source.GameVersion,
        DurationSeconds = source.DurationSeconds,
        BeastsFound = source.BeastsFound,
        RedBeastsFound = source.RedBeastsFound,

        // Older records predate the field, so recompute rather than trusting a stored 0.
        YellowBeastsFound = source.YellowBeastsFound > 0
            ? source.YellowBeastsFound
            : Math.Max(0, source.BeastsFound - source.RedBeastsFound),

        CapturedChaos = source.CapturedChaos,
        CostChaos = source.CostChaos,
        NetChaos = source.NetChaos,
        UsedBestiaryScarabOfDuplicating = source.UsedBestiaryScarabOfDuplicating,
        ScarabNames = source.ScarabNames?.ToArray() ?? [],
        DeviceReadAgeMs = source.DeviceReadAgeMs,
        MapAdditionalRedBeasts = source.MapAdditionalRedBeasts,
        MapDuplicateCapturedBeastsChancePct = source.MapDuplicateCapturedBeastsChancePct,
        FirstRedSeenSeconds = source.FirstRedSeenSeconds,
        Atlas = source.Atlas == null
            ? new AtlasSnapshot()
            : new AtlasSnapshot
            {
                AllocatedNodes = source.Atlas.AllocatedNodes?.ToArray() ?? [],
                TreeUrl = source.Atlas.TreeUrl,
                Cohort = source.Atlas.Cohort,
                IsStrictReferenceMatch = source.Atlas.IsStrictReferenceMatch,
            },
        BeastBreakdown = source.BeastBreakdown?.Select(b => new MapBeastStat
        {
            BeastName = b.BeastName, Count = b.Count,
            CapturedCount = b.CapturedCount, IsDuplicated = b.IsDuplicated,
            UnitPriceChaos = b.UnitPriceChaos,
        }).ToArray() ?? [],
        CostBreakdown = source.CostBreakdown?.Select(c => new MapCostItem
        {
            ItemName = c.ItemName, UnitPriceChaos = c.UnitPriceChaos, Inferred = c.Inferred,
        }).ToArray() ?? [],
        ReplayEvents = source.ReplayEvents?.Select(e => new MapReplayEvent
        {
            BeastName = e.BeastName, EventType = e.EventType,
            OffsetSeconds = e.OffsetSeconds, UnitPriceChaos = e.UnitPriceChaos,
        }).ToArray() ?? [],
    };

    private static MapCostItem Clone(MapCostItem source) =>
        new() { ItemName = source.ItemName, UnitPriceChaos = source.UnitPriceChaos };
}
