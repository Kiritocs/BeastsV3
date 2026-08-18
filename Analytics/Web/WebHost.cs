using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeastsV3.Plugin.Settings;
using BeastsV3.Prices;
using BeastsV3.Shared;

namespace BeastsV3.Analytics.Web;

// Runs the dashboard's HTTP listener and builds the session snapshot it serves.
public sealed class WebHost
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly BeastsSettings _settings;
    private readonly SessionRecorder _recorder;
    private readonly CostTracker _cost;
    private readonly SessionStore _store;
    private readonly PriceService _prices;

    private HttpListener _listener;
    private CancellationTokenSource _cts;
    private Task _listenTask;
    private int _boundPort;
    private bool _boundAllowNetwork;

    private DateTime _lastSnapshotUtc = DateTime.MinValue;
    private SessionCurrentResponse _latestSnapshot = new();

    public WebHost(BeastsSettings settings, SessionRecorder recorder, CostTracker cost,
        SessionStore store, PriceService prices)
    {
        _settings = settings;
        _recorder = recorder;
        _cost = cost;
        _store = store;
        _prices = prices;
    }

    public bool IsRunning => _listener?.IsListening == true;
    public string Url => $"http://localhost:{_boundPort}/";

    // The most recent snapshot; never null.
    public SessionCurrentResponse LatestSnapshot => _latestSnapshot;

    // ---- lifecycle -------------------------------------------------------

    // Starts, stops or restarts the listener to match the current settings.
    public void EnsureServerState()
    {
        var analyticsOn = _settings.Analytics.Enable.Value;
        var web = _settings.Analytics.Web;
        var wantsRunning = analyticsOn && web.Enabled.Value;

        if (!wantsRunning)
        {
            if (IsRunning) Stop();
            return;
        }

        var port = web.Port.Value;
        var allowNetwork = web.AllowNetworkAccess.Value;
        if (IsRunning && _boundPort == port && _boundAllowNetwork == allowNetwork) return;

        Start(port, allowNetwork);
    }

    // Stops the listener.
    public void DisposeServer() => Stop();

    // ---- snapshot -------------------------------------------------------

    // Rebuilds the snapshot at most once per SnapshotRefreshMs.
    public void MaybeRefreshSnapshot(DateTime nowUtc)
    {
        var web = _settings.Analytics.Web;
        var intervalMs = Math.Max(100, web.SnapshotRefreshMs.Value);
        if (_lastSnapshotUtc != DateTime.MinValue &&
            (nowUtc - _lastSnapshotUtc).TotalMilliseconds < intervalMs) return;

        _latestSnapshot = BuildSnapshot(nowUtc);
        _lastSnapshotUtc = nowUtc;
    }

    // Returns a page of map history, newest first.
    public MapListResponse BuildMapList(int offset, int limit)
    {
        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = Math.Clamp(limit, 1, 1000);

        var ordered = _recorder.State.MapHistory
            .OrderByDescending(x => x.CompletedAtUtc)
            .ToArray();

        return new MapListResponse
        {
            Total = ordered.Length,
            Offset = normalizedOffset,
            Limit = normalizedLimit,
            Items = ordered
                .Skip(normalizedOffset)
                .Take(normalizedLimit)
                .Select(MapToItem)
                .Where(x => x != null)
                .ToArray(),
        };
    }

    // Lists every save on disk, flagging those already loaded.
    public IReadOnlyList<SessionSaveListItem> ListSavedSessions()
    {
        var loadedSaveIds = _recorder.LoadedSaveIds;
        var items = new List<SessionSaveListItem>();
        foreach (var entry in _store.ListAll())
        {
            var data = entry.Data;
            items.Add(new SessionSaveListItem
            {
                SaveId = data.SaveId,
                SessionId = data.SessionId,
                Name = data.Name ?? string.Empty,
                SavedAtUtc = data.SavedAtUtc,
                SavedAtDisplay = FormatLocalDateTime(data.SavedAtUtc),
                IsAutoSave = data.IsAutoSave,
                Tags = data.Tags ?? new SessionTags(),
                Summary = data.Summary ?? new SessionSummary(),
                AlreadyLoaded = loadedSaveIds.Contains(data.SaveId),
            });
        }
        return items;
    }

    // Loads one save's full contents.
    public SessionSaveDetail GetSavedSessionDetail(string saveId)
    {
        var entry = _store.ReadBySaveId(saveId);
        return entry?.Data == null ? null : new SessionSaveDetail { Session = entry.Data };
    }

    // Saves the current session under a name.
    public ApiActionResponse CreateSave(CreateSessionSaveRequest request)
    {
        if (!_settings.Analytics.Enable.Value)
            return ApiActionResponse.Fail("analytics_disabled", "Analytics is disabled.");

        var ok = _recorder.SaveNamed(request?.Name);
        return ok
            ? ApiActionResponse.Ok("saved", "Session saved.")
            : ApiActionResponse.Fail("save_failed", "Failed to save session.");
    }

    // Merges a stored save into the live session.
    public ApiActionResponse LoadSave(string saveId)
    {
        if (!_settings.Analytics.Enable.Value)
            return ApiActionResponse.Fail("analytics_disabled", "Analytics is disabled.");

        if (string.IsNullOrWhiteSpace(saveId))
            return ApiActionResponse.Fail("invalid_id", "saveId is required.");

        var entry = _store.ReadBySaveId(saveId);
        if (entry?.Data == null)
            return ApiActionResponse.Fail("not_found", "Session not found.");

        if (_recorder.LoadedSaveIds.Contains(entry.Data.SaveId))
            return ApiActionResponse.Fail("duplicate", "Session is already loaded.");

        return _recorder.ApplyLoadedSession(entry.Data)
            ? ApiActionResponse.Ok("loaded", "Session loaded.")
            : ApiActionResponse.Fail("load_failed", "Failed to load session.");
    }

    // Removes a previously loaded save from the live session.
    public ApiActionResponse UnloadSave(string saveId)
    {
        if (string.IsNullOrWhiteSpace(saveId))
            return ApiActionResponse.Fail("invalid_id", "saveId is required.");
        if (!_recorder.LoadedSaveIds.Contains(saveId))
            return ApiActionResponse.Fail("not_loaded", "Session is not loaded.");

        return _recorder.RemoveLoadedSession(saveId)
            ? ApiActionResponse.Ok("unloaded", "Session unloaded.")
            : ApiActionResponse.Fail("unload_failed", "Failed to unload session.");
    }

    // Unloads a save if needed, then deletes its file.
    public ApiActionResponse DeleteSave(string saveId)
    {
        if (string.IsNullOrWhiteSpace(saveId))
            return ApiActionResponse.Fail("invalid_id", "saveId is required.");

        if (_recorder.LoadedSaveIds.Contains(saveId))
        {
            var unload = UnloadSave(saveId);
            if (!unload.Success) return unload;
        }

        return _store.DeleteBySaveId(saveId)
            ? ApiActionResponse.Ok("deleted", "Session deleted.")
            : ApiActionResponse.Fail("delete_failed", "Failed to delete session.");
    }

    // Compares two saves by id.
    public CompareSessionsResponse CompareSaves(CompareSessionsRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.SaveAId) || string.IsNullOrWhiteSpace(request.SaveBId))
        {
            return new CompareSessionsResponse
            {
                Success = false,
                Code = "invalid_request",
                Message = "saveAId and saveBId are required.",
            };
        }

        var a = _store.ReadBySaveId(request.SaveAId)?.Data;
        var b = _store.ReadBySaveId(request.SaveBId)?.Data;
        return CompareStats.Compare(a, b, request);
    }

    // ---- private --------------------------------------------------------

    // Binds the listener and starts the accept loop.
    private void Start(int port, bool allowNetworkAccess)
    {
        Stop();

        _boundPort = port;
        _boundAllowNetwork = allowNetworkAccess;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        if (allowNetworkAccess)
        {
            try { _listener.Prefixes.Add($"http://+:{port}/"); }
            catch (Exception ex)
            {
                Log.Error($"Web dashboard network prefix failed on port {port}", ex);
            }
        }

        try
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            Log.Info($"Web dashboard started at {Url}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start web dashboard on port {port}", ex);
            _listener = null;
        }
    }

    // Cancels the accept loop and closes the listener.
    private void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch { /* shutdown errors ignored */ }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _listener = null;
            _listenTask = null;
        }
    }

    // Accepts requests and dispatches each to WebRoutes.
    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                // Handled detached so a slow request cannot block the accept loop.
                Log.FireAndForget(() => WebRoutes.HandleAsync(context, this),
                    $"Dashboard request {context.Request?.Url?.AbsolutePath}");
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Debug($"Web dashboard listener error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Builds the live session snapshot served to the dashboard.
    private SessionCurrentResponse BuildSnapshot(DateTime nowUtc)
    {
        var state = _recorder.State;
        var completedMapCount = state.CompletedMapCount;

        var averageMapTime = completedMapCount > 0
            ? TimeSpan.FromTicks(state.CompletedMapsDuration.Ticks / completedMapCount)
            : TimeSpan.Zero;

        var completedCaptured = state.MapHistory.Sum(x => x.CapturedChaos);
        var completedCost = state.MapHistory.Sum(x => x.CostChaos);
        var currentCaptured = ComputeCurrentMapCapturedChaos(state);
        var currentCost = state.IsCurrentAreaTrackable ? _cost.ComputeCurrentCostChaos() : 0d;

        var sessionCaptured = completedCaptured + currentCaptured;
        var sessionCost = completedCost + currentCost;
        var sessionNet = sessionCaptured - sessionCost;
        var sessionHours = Math.Max(state.GetTotalTime(nowUtc).TotalHours, 1d / 3600d);

        var (beastTotals, familyTotals) = _recorder.BuildSessionTotals(includeCurrentMap: true);
        var currentCostBreakdown = _cost.SnapshotCurrent();
        // Same predicate the recorder counts captures with.
        var currentUsesDupScarab = state.IsCurrentAreaTrackable && _cost.CurrentMapUsesDuplicatingScarab;

        return new SessionCurrentResponse
        {
            GeneratedAtUtc = nowUtc,
            IsCurrentAreaTrackable = state.IsCurrentAreaTrackable,
            IsPaused = state.PauseMenuStartUtc.HasValue,
            ActiveAreaHash = state.ActiveMapAreaHash ?? string.Empty,
            ActiveAreaName = state.ActiveMapAreaName ?? string.Empty,

            CurrentMapDurationSeconds = state.CurrentMapElapsed.TotalSeconds,
            AverageMapDurationSeconds = averageMapTime.TotalSeconds,
            SessionDurationSeconds = state.GetTotalTime(nowUtc).TotalSeconds,

            CompletedMapCount = completedMapCount,
            SessionBeastsFound = state.SessionBeastsFound,
            SessionRedBeastsFound = state.SessionRedBeastsFound,
            CurrentMapBeastsFound = state.CurrentMapBeastsFound,
            CurrentMapRedBeastsFound = state.CurrentMapRedBeastsFound,

            CurrentMapCapturedChaos = currentCaptured,
            CurrentMapCostChaos = currentCost,
            CurrentMapNetChaos = currentCaptured - currentCost,
            CurrentMapUsesDuplicatingScarab = currentUsesDupScarab,
            CurrentMapFirstRedSeenSeconds = state.IsCurrentAreaTrackable ? state.CurrentMapFirstRedSeenSeconds : null,
            CurrentMapCostBreakdown = state.IsCurrentAreaTrackable ? currentCostBreakdown : [],
            CurrentMapReplayEvents = state.IsCurrentAreaTrackable ? _recorder.BuildCurrentMapReplayEventsForSnapshot() : [],

            SessionCapturedChaos = sessionCaptured,
            SessionCostChaos = sessionCost,
            SessionNetChaos = sessionNet,
            SessionCapturedPerHourChaos = sessionCaptured / sessionHours,
            SessionNetPerHourChaos = sessionNet / sessionHours,
            AverageCapturedPerMapChaos = completedMapCount > 0 ? completedCaptured / completedMapCount : 0d,
            AverageNetPerMapChaos = completedMapCount > 0 ? state.MapHistory.Average(x => x.NetChaos) : 0d,

            Rolling = CompareStats.BuildRollingStats(state.MapHistory, _settings.Analytics.Web.RollingStatsWindowMaps.Value),
            FamilyTotals = familyTotals,
            BeastTotals = beastTotals,
            TrackedBeastNames = _settings.BeastPrices.EnabledBeasts.ToArray(),
        };
    }

    // Chaos value of everything captured in the current map.
    private double ComputeCurrentMapCapturedChaos(SessionState state)
    {
        var total = 0d;
        foreach (var (name, count) in state.CurrentMapValuableBeastCapturedCounts)
        {
            if (count <= 0) continue;
            if (_prices.BeastPrices.TryGetValue(name, out var unit) && unit > 0)
                total += count * unit;
        }
        return total;
    }

    // Projects a map record into its list-item form.
    private static MapListItem MapToItem(MapAnalyticsRecord source)
    {
        if (source == null) return null;
        return new MapListItem
        {
            MapId = source.MapId,
            CompletedAtUtc = source.CompletedAtUtc,
            CompletedAtDisplay = FormatLocalDateTime(source.CompletedAtUtc),
            AreaHash = source.AreaHash,
            AreaName = source.AreaName,
            MapTier = source.MapTier,
            DurationSeconds = source.DurationSeconds,
            BeastsFound = source.BeastsFound,
            RedBeastsFound = source.RedBeastsFound,
            YellowBeastsFound = source.YellowBeastsFound > 0
                ? source.YellowBeastsFound
                : Math.Max(0, source.BeastsFound - source.RedBeastsFound),
            Atlas = source.Atlas ?? new AtlasSnapshot(),
            CapturedChaos = source.CapturedChaos,
            CostChaos = source.CostChaos,
            NetChaos = source.NetChaos,
            UsedBestiaryScarabOfDuplicating = source.UsedBestiaryScarabOfDuplicating,
            FirstRedSeenSeconds = source.FirstRedSeenSeconds,
            BeastBreakdown = source.BeastBreakdown ?? [],
            CostBreakdown = source.CostBreakdown ?? [],
            ReplayEvents = source.ReplayEvents ?? [],
        };
    }

    // Formats a UTC timestamp in local time, or empty for MinValue.
    private static string FormatLocalDateTime(DateTime value)
    {
        if (value == DateTime.MinValue) return string.Empty;
        var local = value.Kind == DateTimeKind.Local ? value : value.ToLocalTime();
        return local.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }
}
