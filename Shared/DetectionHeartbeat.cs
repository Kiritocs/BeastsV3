using System;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using ExileCore;

namespace BeastsV3.Shared;

// Writes a periodic one-line summary of beast detection and overlay gating to the log file.
public sealed class DetectionHeartbeat
{
    // Cadence while the reported state keeps changing.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    // Slower keepalive so an idle session still leaves a timeline without repeating itself.
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(5);

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly BeastTracker _tracker;

    private DateTime _lastWriteUtc = DateTime.MinValue;
    private string _lastLine;

    public DetectionHeartbeat(GameController game, BeastsSettings settings, BeastTracker tracker)
    {
        _game = game;
        _settings = settings;
        _tracker = tracker;
    }

    public void Tick(DateTime nowUtc)
    {
        var sinceLast = nowUtc - _lastWriteUtc;
        if (sinceLast < Interval) return;

        string line;
        try
        {
            line = BuildLine();
        }
        catch (Exception ex)
        {
            // A heartbeat is never worth breaking a frame over.
            _lastWriteUtc = nowUtc;
            Log.Debug($"Heartbeat skipped: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Identical state is repeated only at the idle cadence.
        if (string.Equals(line, _lastLine, StringComparison.Ordinal) && sinceLast < IdleInterval) return;

        _lastWriteUtc = nowUtc;
        _lastLine = line;
        Log.Info(line);
    }

    private string BuildLine()
    {
        var area = _game?.Area?.CurrentArea;
        var ingameUi = _game?.IngameState?.IngameUi;

        var markers = _tracker?.Markers;
        var markerCount = markers?.Count ?? 0;
        var liveMarkers = 0;
        if (markers != null)
        {
            // Explicit loop to avoid a per-call allocation.
            for (var i = 0; i < markers.Count; i++)
            {
                if (markers[i].IsLive) liveMarkers++;
            }
        }

        var render = _settings.MapRender;
        var visibility = _settings.Visibility;

        return "Heartbeat: " +
               $"area='{GameHelpers.TryGetAreaName(area)}' town={GameHelpers.IsTownOrHideout(area)} " +
               $"map={GameHelpers.IsRunnableMap(area)} " +
               $"| rares={_tracker?.RareBeastsFound ?? 0} live={_tracker?.LiveTracked.Count ?? 0} " +
               $"markers={markerCount}({liveMarkers} live,{markerCount - liveMarkers} cached) " +
               $"entities={_game?.EntityListWrapper?.Entities?.Count ?? -1} " +
               $"| show: labels={render.ShowBeastLabelsInWorld.Value} map={render.ShowBeastsOnMap.Value} " +
               $"window={render.ShowTrackedBeastsWindow.Value} cached={render.ShowCachedTrackedBeasts.Value} " +
               $"enabledOnly={render.ShowEnabledOnly.Value} enabled={_settings.BeastPrices.EnabledBeasts.Count} " +
               $"| hideOn: town={visibility.HideInHideout.Value} full={visibility.HideOnFullscreenPanels.Value} " +
               $"left={visibility.HideOnLeftPanelOpen.Value} right={visibility.HideOnRightPanelOpen.Value} " +
               $"| openNow: left={ingameUi?.OpenLeftPanel?.IsVisible == true} " +
               $"right={ingameUi?.OpenRightPanel?.IsVisible == true} " +
               $"full={IsAnyFullscreenPanelVisible()}";
    }

    // Mirrors the fullscreen check the overlays gate on, so the line explains a hidden overlay
    // rather than only reporting that the toggle is on.
    private bool IsAnyFullscreenPanelVisible()
    {
        var panels = _game?.IngameState?.IngameUi?.FullscreenPanels;
        if (panels == null) return false;

        foreach (var panel in panels)
        {
            if (panel?.IsVisible == true) return true;
        }

        return false;
    }
}
