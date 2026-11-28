using System;
using System.Collections.Generic;
using BeastsV3.Analytics;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using Vector2 = System.Numerics.Vector2;

namespace BeastsV3.Rendering;

// Draws the compact analytics overlay: session and map timers, beast and map counts,
// and average clear time.
public sealed class AnalyticsOverlay
{
    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly SessionRecorder _recorder;
    private readonly Func<TelemetryUploader> _telemetry;
    private readonly OverlayLayout _layout;
    private readonly List<string> _lineBuffer = new(7);

    public AnalyticsOverlay(GameController game, BeastsSettings settings,
        SessionRecorder recorder, Func<TelemetryUploader> telemetry, OverlayLayout layout)
    {
        _layout = layout;
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

        var (position, size) = _layout.Draw(OverlayLayout.AnalyticsOverlay, "##BeastsV3AnalyticsOverlay", text, style);

        DrawTelemetryWarning(window, position, size);
    }

    // Orange banner under the analytics overlay while anonymous map data is being uploaded.
    private void DrawTelemetryWarning(AnalyticsOverlaySettings window, Vector2 overlayPosition, Vector2 overlaySize)
    {
        var telemetrySettings = _settings.Analytics.Telemetry;
        if (!TelemetrySettings.Unlocked) return;
        if (!telemetrySettings.ShareAnonymousData.Value || !telemetrySettings.ShowActiveBanner.Value) return;

        const float gapPixels = 4f;

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

        var (size, wrapWidth) = OverlayWindow.Measure(_game, text, style);
        var position = new Vector2(overlayPosition.X, overlayPosition.Y + overlaySize.Y + gapPixels);
        OverlayWindow.DrawAt("##BeastsV3TelemetryWarning", text, position, size, wrapWidth, style);

        // The banner belongs to this overlay: whatever is attached goes around both.
        _layout.Extend(OverlayLayout.AnalyticsOverlay,
            new Vector2(MathF.Max(overlaySize.X, size.X), overlaySize.Y + gapPixels + size.Y));
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

        // Whether this map's tree is the reference tree. Shown regardless of sharing: it
        // doubles as a check that the tree is what you think it is.
        if (TelemetrySettings.Unlocked &&
            _settings.Analytics.Telemetry.ShowTreeBanner.Value &&
            state.IsCurrentAreaTrackable)
        {
            _lineBuffer.Add(AtlasTree.Describe(state.CurrentMapAtlas));
        }

        return string.Join('\n', _lineBuffer);
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
