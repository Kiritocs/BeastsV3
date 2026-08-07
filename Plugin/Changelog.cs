using System;
using System.Linq;
using ImGuiNET;

namespace BeastsV3.Plugin;

// Version history shown under "What's New" in the settings menu. Add releases as entries
// in Entries; order does not matter.
public static class Changelog
{
    private static readonly ChangelogEntry[] Entries =
    [
        new(2026, 8, 5, 1,
            "First testing release.",
            "Beast tracking: in-world labels, large-map markers, capture state and talisman support.",
            "Prices: poe.ninja fetching with auto-refresh, plus price overlays on captured-monster items in inventory, stash, merchant and Bestiary panels.",
            "Overlays: beast counter, map completion, and auto-hide rules for town, hideout and fullscreen panels.",
            "Analytics: per-session and per-map records with autosave, and an optional local web dashboard (off by default).",
            "Automation: Bestiary regex-itemize and delete, auto-stash, Faustus listing, map device load, stash restock, and a one-key full sequence.",
            "Exploration route overlay (experimental, off by default).",
            "Restock and Map Device loading now ctrl-click a whole pass of items and confirm once, instead of confirming every item before clicking the next. Turn off Automation: Timing -> Batch Item Transfers if transfers get dropped.",
            "Diagnostics: session log file with rollover, plus a Dump Diagnostics button and hotkey for bug reports.",
            "Ships disabled with every hotkey unbound - nothing runs until you configure it."),
        new(2026, 8, 6, 1,
            "Fixed beast detection breaking after the latest PoE patch.",
            "Diagnostics: added a periodic heartbeat log of beast detection status, to make future breakage easier to spot.",
            "Web Dashboard is now enabled by default.",
            "Fixed beast labels and the tracked-beasts window not respecting the hideout visibility setting."),
        new(2026, 8, 7, 1,
            "Automation: added a Panic Stop hotkey that halts any running automation immediately, independent of workflow state or visible UI panels.",
            "Faustus listing: the price popup's currency is now forced back to Chaos Orb if something else was selected, instead of silently mispricing every listed beast.",
            "Overlays: beast markers, world labels, the tracked-beasts window, the counter and the analytics overlay are now also hidden in non-trackable special areas (Menagerie, atlas encounters, Expedition, Betrayal safehouses), not just town and hideout."),
        new(2026, 8, 7, 2,
            "Automation: added an Input Humanization system (off by default, under Timing) - Gaussian-jittered delays, WindMouse curved cursor travel, off-centre click points, randomized key-hold times, and occasional hesitation pauses with optional cursor drift.",
            "Automation: three Humanization presets (Light, Human, Paranoid) to set a coherent starting point in one click; every knob stays individually editable afterwards.",
            "World Labels: added a 'Hide while Large Map is open' option to the overlays settings")
    ];

    // Entries newest first, sorted once at load.
    private static readonly ChangelogEntry[] Sorted = Entries
        .OrderByDescending(entry => entry.SortKey)
        .ToArray();

    public static void Draw()
    {
        if (Sorted.Length == 0)
        {
            ImGui.TextDisabled("No changelog entries yet.");
            return;
        }

        for (var i = 0; i < Sorted.Length; i++)
        {
            var entry = Sorted[i];

            // Newest entry is expanded, older ones collapsed.
            var flags = i == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{entry.Version}##BeastsV3Changelog{i}", flags)) continue;

            foreach (var change in entry.Changes ?? [])
            {
                if (string.IsNullOrWhiteSpace(change)) continue;

                ImGui.Bullet();
                ImGui.SameLine();
                // Wrapped to the panel width.
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextUnformatted(change);
                ImGui.PopTextWrapPos();
            }
        }
    }
}

// One release: a date, a same-day revision, and its list of changes. Revision 1 is left
// off the displayed version.
public sealed record ChangelogEntry(int Year, int Month, int Day, int Revision, params string[] Changes)
{
    public int SortKey => (Year * 1_000_000) + (Month * 10_000) + (Day * 100) + Revision;

    public string Version => Revision <= 1
        ? $"{Year:0000}.{Month:00}.{Day:00}"
        : $"{Year:0000}.{Month:00}.{Day:00}-r{Revision}";
}
