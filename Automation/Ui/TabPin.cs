using System;
using System.Collections.Generic;

namespace BeastsV3.Automation.Ui;

// Tells apart tabs that share a name.
//
// A stash or shop tab is identified in settings by its name, which is fine until two tabs
// are called the same thing - then every one of them resolves to whichever came first, and
// only that one can ever be selected. Appending the position gives the saved value enough
// to pick a specific tab: "Beasts#3".
//
// The suffix is only written when a name is genuinely ambiguous, so the settings file keeps
// plain readable names in the overwhelmingly common case, and values saved before pins
// existed keep resolving exactly as they always did.
public static class TabPin
{
    public const char Separator = '#';

    public static string Pin(string tabName, int tabIndex) => $"{tabName}{Separator}{tabIndex}";

    // The name without any pin, for anything shown to the user.
    public static string DisplayName(string value) => TrySplit(value, out var name, out _) ? name : value;

    public static bool TrySplit(string value, out string name, out int tabIndex)
    {
        name = value;
        tabIndex = -1;
        if (string.IsNullOrEmpty(value)) return false;

        var separator = value.LastIndexOf(Separator);
        if (separator <= 0 || separator == value.Length - 1) return false;
        if (!int.TryParse(value[(separator + 1)..], out var parsed) || parsed < 0) return false;

        name = value[..separator];
        tabIndex = parsed;
        return true;
    }

    // Three passes, in descending order of confidence.
    //
    // The exact match comes first so a tab genuinely called "Loot#2" beats the pin reading
    // of the same string. The pin is next, and is the only thing that can tell apart tabs
    // sharing a name. Falling back to the bare name means a pin whose tab has since been
    // moved or renamed still lands on a sensible tab rather than failing the run.
    public static int Resolve(IReadOnlyList<string> names, string value)
    {
        if (names == null || string.IsNullOrWhiteSpace(value)) return -1;

        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        }

        if (!TrySplit(value, out var bareName, out var pinnedIndex)) return -1;

        if (pinnedIndex < names.Count &&
            string.Equals(names[pinnedIndex], bareName, StringComparison.OrdinalIgnoreCase))
        {
            return pinnedIndex;
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], bareName, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    // Names appearing more than once, so only those get a position pinned onto them.
    public static HashSet<string> Duplicates(IReadOnlyList<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names ?? Array.Empty<string>())
        {
            if (!seen.Add(name)) duplicates.Add(name);
        }

        return duplicates;
    }
}
