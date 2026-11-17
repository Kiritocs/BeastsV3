using System;
using System.Linq;
using Hexa.NET.ImGui;

namespace BeastsV3.Plugin;

// Version history shown under "What's New" in the settings menu. Add releases as entries
// in Entries; order does not matter.
public static class Changelog
{
    private static readonly ChangelogEntry[] Entries =
    [
        new(2026, 11, 14, 1,
            "Initial fixes for 3.30. Some things are probably still not working."),
        new(2026, 11, 16, 1,
            "New overlay: Thread Effects. Shows the Bestiary Thread count and every beast effect on the current map.",
            "Thread Effects: set each effect to Good, Bad, Show or Hide under Effects. Good and Bad get their own color.",
            "Itemizing works again. The game now puts an itemized beast on the cursor, so the plugin drops the beasts and picks them all up afterwards.",
            "Option 'Drop Beasts While Itemizing' (on by default). Turn it off to place each beast into the inventory instead, which is slower.",
            "Fixed the Bestiary panel's Captured Beasts tab not being found (prices, buttons, itemizing).",
            "Fixed the Bestiary regex not being entered: the plugin clicks the filter field, since Ctrl+F no longer focuses it.",
            "Fixed Faustus listing stopping with 'Could not switch the Faustus price currency'.",
            "Fixed Load Map Device selecting the wrong map on the Atlas.",
            "Map cost counts only the Threads you pay for: free Threads from Unwavering Vision or a Shaped Region are left out, and Threads picked by hand are counted.",
            "Analytics and the web dashboard are back and switched on again. Turn them off under Analytics if you do not want them.",
            "Restock has a single Map Target now. Your old restock targets were reset: set the map and its stash tab again.",
            "New: Left, Middle and Right Thread Slot settings. Load Map Device sets each slot's thread and count (1-8), clears it, or leaves it alone."),
        new(2026, 11, 17, 1,
            "New overlay: Possible Beasts. As soon as you enter a map it lists which of your tracked beasts the map can contain, and says so when none can. A listed beast is possible, not certain, so each one shows how often it really was there in your earlier maps that listed it; a beast that is not listed is not in the map."),
        new(2026, 8, 23, 1,
            "Map Device load: a quantity of two stacks now keeps two slots filled.",
            "Map Device load: slots beyond what your quantity asks for go back to your inventory.",
            "Map Device load: stops when the device cannot be filled, instead of running the map short."),
        new(2026, 8, 18, 1,
            "First public release (1.0.0)."),
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
