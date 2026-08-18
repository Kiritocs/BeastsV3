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
