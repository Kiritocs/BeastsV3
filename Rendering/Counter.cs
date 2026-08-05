using System;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;

namespace BeastsV3.Rendering;

// Draws the beast counter and completion messages, applying the panel and area
// visibility rules and latching the map-completed state.
public sealed class Counter
{
    private const string CounterLabel = "Beasts Found";

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly BeastTracker _tracker;

    private bool _currentMapCompleted;

    // Quest-tracker reads are polled at this interval; the beast count stays frame-accurate.
    private static readonly TimeSpan QuestPollInterval = TimeSpan.FromMilliseconds(250);
    private DateTime _questPolledUtc = DateTime.MinValue;
    private int _questTotal;
    private bool _questMissionComplete;

    private readonly Func<bool> _isInFinalizedMap;

    public Counter(GameController game, BeastsSettings settings, BeastTracker tracker,
        Func<bool> isInFinalizedMap)
    {
        _game = game;
        _settings = settings;
        _tracker = tracker;
        _isInFinalizedMap = isInFinalizedMap ?? (() => false);
    }

    public void OnAreaChanged()
    {
        _currentMapCompleted = false;
        _questPolledUtc = DateTime.MinValue;
        _questTotal = 0;
        _questMissionComplete = false;
    }

    public void Render()
    {
        if (!Visible()) return;

        PollQuestTracker();

        var counter = _settings.Counter;
        BuildCounterText(out var counterText, out var allFoundNow);

        var missionComplete = _questMissionComplete;
        var allTrackedCaptured = _tracker.AllTrackedValuableBeastsCaptured();
        var allFound = allFoundNow || _currentMapCompleted || missionComplete;
        var allCaptured = allFound && allTrackedCaptured;

        // A banked map counts as complete without re-deriving it.
        if (_isInFinalizedMap())
        {
            allFound = true;
            allCaptured = true;
        }

        // Same completion rule the recorder uses.
        if (MapCompletion.IsComplete(missionComplete, _questTotal, _tracker.RareBeastsFound, allTrackedCaptured))
            _currentMapCompleted = true;

        if (counter.Show.Value)
        {
            DrawCounter(counterText, allFound);
        }

        DrawCompletion(counter.CompletedMessage, "##BeastsV3CompletedMsg", allFound);
        DrawCompletion(counter.TrackedCompletionMessage, "##BeastsV3TrackedMsg", allCaptured);
    }

    private void PollQuestTracker()
    {
        var now = DateTime.UtcNow;
        if (now - _questPolledUtc < QuestPollInterval) return;
        _questPolledUtc = now;

        _questTotal = BeastQuest.TryGetProgress(_game, out _, out var total) && total > 0 ? total : 0;
        _questMissionComplete = BeastQuest.IsMissionComplete(_game);
    }

    private void BuildCounterText(out string text, out bool allFound)
    {
        var found = _tracker.RareBeastsFound;
        if (_questTotal > 0)
        {
            text = $"{CounterLabel}: {found}/{_questTotal}";
            allFound = found >= _questTotal;
            return;
        }

        text = $"{CounterLabel}: {found}";
        allFound = false;
    }

    private void DrawCounter(string text, bool allFound)
    {
        var counter = _settings.Counter;
        var completed = counter.CompletedStyle;
        var showCompletedStyle = allFound || completed.ShowWhileNotComplete.Value;

        var style = new OverlayWindow.Style(
            Text: showCompletedStyle ? completed.TextColor.Value : counter.TextColor.Value,
            Border: showCompletedStyle ? completed.BorderColor.Value : counter.BorderColor.Value,
            Background: counter.BackgroundColor.Value,
            Padding: counter.Padding.Value,
            BorderThickness: counter.BorderThickness.Value,
            BorderRounding: counter.BorderRounding.Value,
            TextScale: showCompletedStyle ? completed.TextScale.Value : counter.TextScale.Value);

        OverlayWindow.Draw(_game, "##BeastsV3Counter", text, counter.XPos.Value, counter.YPos.Value, style);
    }

    private void DrawCompletion(CompletionMessageSettings message, string windowId, bool conditionMet)
    {
        var shouldShow = message.Show.Value &&
                         !string.IsNullOrWhiteSpace(message.Text.Value) &&
                         (conditionMet || message.ShowWhileNotComplete.Value);
        if (!shouldShow) return;

        var style = new OverlayWindow.Style(
            Text: message.TextColor.Value,
            Border: message.BorderColor.Value,
            Background: message.BackgroundColor.Value,
            Padding: message.Padding.Value,
            BorderThickness: message.BorderThickness.Value,
            BorderRounding: message.BorderRounding.Value,
            TextScale: message.TextScale.Value);

        OverlayWindow.Draw(_game, windowId, message.Text.Value, message.XPos.Value, message.YPos.Value, style);
    }

    private bool Visible()
    {
        var ingameUi = _game?.IngameState?.IngameUi;
        if (ingameUi == null) return false;

        var counter = _settings.Counter;
        var previewMode =
            counter.CompletedStyle.ShowWhileNotComplete.Value ||
            counter.CompletedMessage.ShowWhileNotComplete.Value ||
            counter.TrackedCompletionMessage.ShowWhileNotComplete.Value;
        if (previewMode) return true;

        var visibility = _settings.Visibility;
        if (visibility.HideOnFullscreenPanels.Value)
        {
            // Explicit loop to avoid per-frame allocations.
            var panels = ingameUi.FullscreenPanels;
            if (panels != null)
            {
                foreach (var panel in panels)
                {
                    if (panel?.IsVisible == true) return false;
                }
            }
        }
        if (visibility.HideInHideout.Value && GameHelpers.IsTownOrHideout(_game.Area?.CurrentArea))
            return false;
        if (visibility.HideOnLeftPanelOpen.Value && ingameUi.OpenLeftPanel?.IsVisible == true)
            return false;
        if (visibility.HideOnRightPanelOpen.Value && ingameUi.OpenRightPanel?.IsVisible == true)
            return false;

        return true;
    }
}
