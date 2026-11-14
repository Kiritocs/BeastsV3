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
    private readonly Func<TelemetryUploader> _telemetry;
    private readonly List<string> _lineBuffer = new(7);

    public AnalyticsOverlay(GameController game, BeastsSettings settings,
        SessionRecorder recorder, Func<TelemetryUploader> telemetry)
    {
        _game = game;
        _settings = settings;
        _recorder = recorder;
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
            _lineBuffer.Add(CohortLine(state.CurrentMapAtlas));
        }

        return string.Join('\n', _lineBuffer);
    }

    // One short line describing what this run contributes.
    private static string CohortLine(AtlasSnapshot atlas) => atlas.Cohort switch
    {
        "A" => "Tree: reference (BASELINE DATA)",
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
