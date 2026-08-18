using System;
using System.Collections.Generic;
using BeastsV3.Analytics;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;

namespace BeastsV3.Rendering;

// Draws the compact analytics overlay: session and map timers, beast and map counts,
// and average clear time.
public sealed class AnalyticsOverlay
{
    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly SessionRecorder _recorder;
    private readonly CostTracker _cost;
    private readonly Func<TelemetryUploader> _telemetry;
    private readonly List<string> _lineBuffer = new(7);

    public AnalyticsOverlay(GameController game, BeastsSettings settings,
        SessionRecorder recorder, CostTracker cost, Func<TelemetryUploader> telemetry)
    {
        _game = game;
        _settings = settings;
        _recorder = recorder;
        _cost = cost;
        _telemetry = telemetry;
    }

    public void Render()
    {
        var window = _settings.Analytics.Overlay;
        if (!_settings.Analytics.Enable.Value || !window.Show.Value) return;
        if (!Visible()) return;

        var text = BuildText();
        if (string.IsNullOrEmpty(text)) return;

        var style = new OverlayWindow.Style(
            Text: window.TextColor.Value,
            Border: window.BorderColor.Value,
            Background: window.BackgroundColor.Value,
            Padding: window.Padding.Value,
            BorderThickness: window.BorderThickness.Value,
            BorderRounding: window.BorderRounding.Value,
            TextScale: window.TextScale.Value);

        var size = OverlayWindow.Draw(_game, "##BeastsV3AnalyticsOverlay", text,
            window.XPos.Value, window.YPos.Value, style, centerHorizontally: false);

        DrawTelemetryWarning(window, size);
    }

    // Orange banner under the analytics overlay while anonymous map data is being uploaded.
    private void DrawTelemetryWarning(AnalyticsOverlaySettings window, System.Numerics.Vector2 overlaySize)
    {
        var telemetrySettings = _settings.Analytics.Telemetry;
        if (!telemetrySettings.ShareAnonymousData.Value || !telemetrySettings.ShowActiveBanner.Value) return;

        var rectHeight = _game.Window.GetWindowRectangle().Height;
        if (rectHeight <= 0) return;

        const float gapPixels = 4f;
        var yPercent = window.YPos.Value + ((overlaySize.Y + gapPixels) / rectHeight * 100f);

        var style = new OverlayWindow.Style(
            Text: WarnColor,
            Border: WarnColor,
            Background: window.BackgroundColor.Value,
            Padding: window.Padding.Value,
            BorderThickness: window.BorderThickness.Value,
            BorderRounding: window.BorderRounding.Value,
            TextScale: window.TextScale.Value);

        var eta = _telemetry?.Invoke()?.TimeUntilNextFlush(DateTime.UtcNow);
        var text = eta.HasValue
            ? $"Community data sharing on - next upload in {ImGuiEx.FormatDuration(eta.Value)}"
            : "Community data sharing on";

        OverlayWindow.Draw(_game, "##BeastsV3TelemetryWarning", text,
            window.XPos.Value, yPercent, style, centerHorizontally: false);
    }

    private static readonly SharpDX.Color WarnColor = new(224, 155, 60, 255);

    private string BuildText()
    {
        var now = DateTime.UtcNow;
        var state = _recorder.State;

        _lineBuffer.Clear();
        _lineBuffer.Add($"Beasts (session): {state.SessionBeastsFound}");
        _lineBuffer.Add($"Session time: {ImGuiEx.FormatDuration(state.GetTotalTime(now))}");
        _lineBuffer.Add($"Map time: {ImGuiEx.FormatDuration(state.CurrentMapElapsed)}");
        _lineBuffer.Add($"Maps completed: {state.CompletedMapCount}");

        if (state.CompletedMapCount > 0)
        {
            var avgSeconds = state.CompletedMapsDuration.TotalSeconds / state.CompletedMapCount;
            _lineBuffer.Add($"Avg map time: {ImGuiEx.FormatDuration(TimeSpan.FromSeconds(avgSeconds))}");
        }

        // Which data cohort the current tree contributes to. Shown regardless of sharing: it
        // doubles as a check that the tree is what you think it is.
        if (_settings.Analytics.Telemetry.ShowCohortBanner.Value &&
            state.IsCurrentAreaTrackable &&
            state.CurrentMapAtlas?.AllocatedNodes.Length > 0)
        {
            var (herdCount, otherScarabs, deviceKnown) = ScarabsLoaded();

            // The map's own stats beat the device reading: a map can arm with an empty or partial
            // device breakdown, and the banner used to report that as fact.
            if (_cost?.CurrentHerdScarabCount is { } fromMapStats)
            {
                herdCount = fromMapStats;
                deviceKnown = true;
            }

            _lineBuffer.Add(CohortLine(state.CurrentMapAtlas, herdCount, otherScarabs, deviceKnown));
        }

        return string.Join('\n', _lineBuffer);
    }

    // No Herd-count constant here on purpose: Herd's effect is stated and flat, so downstream
    // inverts each map at whatever count it had (see beast-calculator/build-spawn-rates.py).

    // The manual cost line CostTracker appends when Analytics.ExtraCostPerMapChaos is set.
    // It is not a device item, so it must not make an unread device look read.
    private const string ManualCostLineName = "Extra (Manual)";

    // Counts Herd scarabs and flags any scarab besides Herd or Duplicating. Duplicating only
    // copies a beast after capture, so it changes what is kept, not what spawns; anything else
    // has an effect the model has no stated value for. deviceKnown separates "no scarabs" from
    // "device never read" - the map itself occupies a slot, so a read device is never empty,
    // and assuming zero Herd on a real 2-Herd map inflates the published base ~11x.
    private (int herdCount, bool otherScarabs, bool deviceKnown) ScarabsLoaded()
    {
        var items = _cost?.Current;
        int herd = 0;
        var other = false;
        var deviceKnown = false;
        if (items == null) return (herd, other, deviceKnown);

        foreach (var item in items)
        {
            var name = item?.ItemName;
            if (string.IsNullOrEmpty(name)) continue;

            // Anything out of the device proves it was read; the manual cost line proves nothing.
            if (!string.Equals(name, ManualCostLineName, StringComparison.OrdinalIgnoreCase))
                deviceKnown = true;

            if (name.IndexOf("Scarab", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (name.IndexOf("Herd", StringComparison.OrdinalIgnoreCase) >= 0)
                herd++;
            else if (name.IndexOf("Duplicating", StringComparison.OrdinalIgnoreCase) < 0)
                other = true;
        }
        return (herd, other, deviceKnown);
    }

    // One short line describing what this run contributes.
    private static string CohortLine(AtlasSnapshot atlas, int herdCount, bool otherScarabs,
                                     bool deviceKnown) => atlas.Cohort switch
    {
        // Worth calling out loudly: the tree is right but the scarabs disqualify the run.
        "A" when otherScarabs => "Tree: reference, but OTHER SCARABS LOADED - not baseline data",

        // Reopening the map device before entering fixes this for the next map, not this one.
        "A" when !deviceKnown =>
            "Tree: reference, but MAP DEVICE NOT READ - Herd count unknown, not baseline data",

        "A" when herdCount == 0 =>
            "Tree: reference, no Herd scarabs (BASELINE DATA)",
        "A" => $"Tree: reference, {herdCount} Herd scarab{(herdCount == 1 ? "" : "s")} (BASELINE DATA)",

        "B" or "C" => $"Tree: +{string.Join(", ", BoostedFamilies(atlas))}",
        _ => "Tree: off-reference (counts only)",
    };

    private static IEnumerable<string> BoostedFamilies(AtlasSnapshot atlas)
    {
        foreach (var node in AtlasTree.ClassificationNodeIds)
        {
            if (Array.IndexOf(atlas.AllocatedNodes, node) >= 0)
                yield return AtlasTree.ClassificationFamily(node);
        }
    }

    private bool Visible()
    {
        var ui = _game?.IngameState?.IngameUi;
        if (ui == null) return false;
        var visibility = _settings.Visibility;

        if (visibility.HideOnFullscreenPanels.Value)
        {
            foreach (var panel in ui.FullscreenPanels)
                if (panel.IsVisible) return false;
        }
        if (visibility.HideInHideout.Value && (GameHelpers.IsTownOrHideout(_game.Area?.CurrentArea) || !_recorder.State.IsCurrentAreaTrackable))
            return false;
        if (visibility.HideOnLeftPanelOpen.Value && ui.OpenLeftPanel?.IsVisible == true)
            return false;
        if (visibility.HideOnRightPanelOpen.Value && ui.OpenRightPanel?.IsVisible == true)
            return false;
        return true;
    }
}
