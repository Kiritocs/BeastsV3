using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;

namespace BeastsV3.Beasts;

// Reads the in-game quest tracker to find the "Capture X/Y beasts" line and completion state.
// Used by the counter overlay and by SessionRecorder to detect a finished map.
public static class BeastQuest
{
    private static readonly Regex ProgressRegex = new(@"\((\d+)/(\d+)\)", RegexOptions.Compiled);

    // Child-index path into IngameUi for the quest text, used when QuestTracker is missing.
    private static readonly int[] FallbackQuestTextPath = { 4, 0, 0, 0, 0, 0, 1, 0, 1 };

    // Last logged read, so the breadcrumbs below fire on change instead of on every poll.
    private static string _lastLoggedProgress;
    private static bool _lastLoggedMissionComplete;

    // Reads the captured/total beast counts from the quest line.
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
            "Quest progress unreadable: no candidate element carried a beast/einhar line with (x/y).");
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

    // Yields every string that might hold the beast quest line, best source first, tagged
    // with where it came from.
    private static IEnumerable<(string Text, string Source)> QuestTextCandidates(GameController game)
    {
        var ui = game?.IngameState?.IngameUi;

        var tracker = ui?.QuestTracker;
        if (tracker != null)
        {
            yield return (GetPrimaryEntryText(tracker), "tracker:primary");

            var entries = GetEntriesContainer(tracker)?.Children;
            if (entries != null)
            {
                var index = 0;
                foreach (var entry in entries)
                {
                    var label = $"tracker:entry{index++}";
                    if (entry?.IsVisible == true) yield return (GetEntryText(entry), label);
                }
            }
        }

        // Yielded last so tracker entries take priority.
        var fallback = ImGuiEx.GetChildAt(ui, FallbackQuestTextPath);
        if (!string.IsNullOrWhiteSpace(fallback?.Text)) yield return (fallback.Text, "fallback-path");
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

    private static Element GetEntriesContainer(Element tracker) => ImGuiEx.GetChildAt(tracker, 0, 0);

    private static Element GetPrimaryEntry(Element tracker) => GetEntriesContainer(tracker)?.GetChildAtIndex(0);

    private static string GetEntryText(Element entry) => ImGuiEx.GetChildAt(entry, 0, 1, 0, 1)?.Text;

    private static string GetPrimaryEntryText(Element tracker)
    {
        var entry = GetPrimaryEntry(tracker);
        return entry?.IsVisible == true ? GetEntryText(entry) : null;
    }

    // Parses "(x/y)" from text that also mentions beast or Einhar.
    private static bool TryParse(string text, out int current, out int total)
    {
        current = 0;
        total = 0;

        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!text.Contains("beast", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("einhar", StringComparison.OrdinalIgnoreCase))
            return false;

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
