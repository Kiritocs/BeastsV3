using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;

namespace BeastsV3.Beasts;

// Reads the in-game quest tracker to find the red beast "(x/y)" line and completion state.
// Since 3.30 Einhar's quest has a yellow line (kill) and a red line (capture); only red counts.
// Used by the counter overlay and by SessionRecorder to detect a finished map.
public static class BeastQuest
{
    private static readonly Regex ProgressRegex = new(@"\((\d+)/(\d+)\)", RegexOptions.Compiled);

    // Child-index path into IngameUi for the tracker, used when QuestTracker is missing or stale.
    private static readonly int[] FallbackTrackerPath = { 4, 0 };

    // Levels walked below the tracker; an objective line's text sits seven down.
    private const int MaxTextDepth = 8;

    // Last logged read, so the breadcrumbs below fire on change instead of on every poll.
    private static string _lastLoggedProgress;
    private static bool _lastLoggedMissionComplete;

    // Reads the captured/total red beast counts from the quest line.
    public static bool TryGetProgress(GameController game, out int current, out int total)
    {
        current = 0;
        total = 0;

        foreach (var (text, source) in QuestTextCandidates(game))
        {
            if (!TryParse(text, out current, out total)) continue;

            // Records which element the number came from and what it said.
            LogOnChange(ref _lastLoggedProgress, $"{source}|{current}/{total}",
                $"Quest progress {current}/{total} read from {source}: \"{Trim(text)}\"");
            return true;
        }

        LogOnChange(ref _lastLoggedProgress, "none",
            "Quest progress unreadable: no visible tracker line mentions red beasts with (x/y).");
        return false;
    }

    // True when the quest tracker shows the mission as complete.
    public static bool IsMissionComplete(GameController game)
    {
        foreach (var (text, source) in QuestTextCandidates(game))
        {
            if (!IsMissionCompleteText(text)) continue;

            if (!_lastLoggedMissionComplete)
            {
                _lastLoggedMissionComplete = true;
                Log.Info($"Quest reports mission complete, from {source}: \"{Trim(text)}\"");
            }
            return true;
        }

        _lastLoggedMissionComplete = false;
        return false;
    }

    // Yields every visible text in the quest tracker, tagged with where it came from. Walking
    // the texts instead of fixed child indices survives the tracker gaining or moving lines.
    private static IEnumerable<(string Text, string Source)> QuestTextCandidates(GameController game)
    {
        var ui = game?.IngameState?.IngameUi;

        foreach (var text in VisibleTexts(ui?.QuestTracker, 0)) yield return (text, "tracker");
        foreach (var text in VisibleTexts(ImGuiEx.GetChildAt(ui, FallbackTrackerPath), 0))
            yield return (text, "fallback-path");
    }

    private static IEnumerable<string> VisibleTexts(Element element, int depth)
    {
        if (element == null || !(depth == 0 ? element.IsVisible : element.IsVisibleLocal)) yield break;

        var text = element.Text;
        if (!string.IsNullOrWhiteSpace(text)) yield return text;

        if (depth >= MaxTextDepth) yield break;
        foreach (var child in element.Children)
        {
            foreach (var childText in VisibleTexts(child, depth + 1)) yield return childText;
        }
    }

    // Called from a polled path, so it only writes when the answer actually changed.
    private static void LogOnChange(ref string last, string key, string message)
    {
        if (string.Equals(last, key, StringComparison.Ordinal)) return;
        last = key;
        Log.Debug(message);
    }

    // Quest lines are short, but the fallback path could resolve to anything.
    private static string Trim(string text) =>
        text == null ? "(null)" : text.Length <= 120 ? text : text[..120] + "...";

    // Clears the change-detection latches so the next map logs its quest state afresh.
    public static void ResetLogState()
    {
        _lastLoggedProgress = null;
        _lastLoggedMissionComplete = false;
    }

    // Parses "(x/y)" from the line that mentions red beasts.
    private static bool TryParse(string text, out int current, out int total)
    {
        current = 0;
        total = 0;

        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!text.Contains("red beast", StringComparison.OrdinalIgnoreCase)) return false;

        var match = ProgressRegex.Match(text);
        if (!match.Success) return false;

        current = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        total = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    // Matches the "Mission Complete" line; no beast keyword is required.
    private static bool IsMissionCompleteText(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Contains("mission complete", StringComparison.OrdinalIgnoreCase);
}
