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
    private readonly List<string> _lineBuffer = new(6);

    public AnalyticsOverlay(GameController game, BeastsSettings settings, SessionRecorder recorder)
    {
        _game = game;
        _settings = settings;
        _recorder = recorder;
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

        OverlayWindow.Draw(_game, "##BeastsV3AnalyticsOverlay", text,
            window.XPos.Value, window.YPos.Value, style, centerHorizontally: false);
    }

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
        if (visibility.HideInHideout.Value && GameHelpers.IsTownOrHideout(_game.Area?.CurrentArea))
            return false;
        if (visibility.HideOnLeftPanelOpen.Value && ui.OpenLeftPanel?.IsVisible == true)
            return false;
        if (visibility.HideOnRightPanelOpen.Value && ui.OpenRightPanel?.IsVisible == true)
            return false;
        return true;
    }
}
