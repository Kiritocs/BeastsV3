using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BeastsV3.Shared;

namespace BeastsV3.Analytics;

// JSON persistence for session snapshots, one file per save under config/BeastsV3Sessions
// (autosaves in AutoSaves/). Trims autosaves to the file and byte limits below.
public sealed class SessionStore
{
    public const int MaxAutoSaveFilesPerStore = 60;
    public const int MinAutoSaveFilesToKeep = 25;
    public const long MaxAutoSaveBytesPerStore = 64L * 1024L * 1024L;
    private const int AutoSaveEquivalentScanLimit = 25;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly string _namedDir;
    private readonly string _autoSaveDir;

    public SessionStore()
    {
        var baseDir = Path.Combine(Directory.GetCurrentDirectory(), "config", "BeastsV3Sessions");
        _namedDir = baseDir;
        _autoSaveDir = Path.Combine(baseDir, "AutoSaves");
    }

    public string NamedDirectory => _namedDir;
    public string AutoSaveDirectory => _autoSaveDir;

    // Trims autosaves in both stores to the configured limits.
    public void EnsureAutoSaveMaintenance()
    {
        TrimAutoSaves(_namedDir);
        TrimAutoSaves(_autoSaveDir);
    }

    // Writes a named save.
    public bool SaveNamed(SavedSessionData data) => TrySave(_namedDir, data, isAutoSave: false);

    // Writes an autosave, then drops duplicates and trims the store.
    public bool SaveAutoSave(SavedSessionData data)
    {
        var ok = TrySave(_autoSaveDir, data, isAutoSave: true);
        if (ok)
        {
            DeleteEquivalentAutoSaves(_autoSaveDir, data);
            EnsureAutoSaveMaintenance();
        }
        return ok;
    }

    // Returns all saves from both stores, newest first and deduped by save id.
    public IReadOnlyList<SessionFileEntry> ListAll()
    {
        var entries = ReadAll(_namedDir).Concat(ReadAll(_autoSaveDir));
        return entries
            .GroupBy(x => x.Data.SaveId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Data.SavedAtUtc).First())
            .OrderByDescending(x => x.Data.SavedAtUtc)
            .ToArray();
    }

    // Loads one save by id, searching named saves first.
    public SessionFileEntry ReadBySaveId(string saveId)
    {
        if (!IsValidId(saveId)) return null;
        return ReadAll(_namedDir).FirstOrDefault(x => IdEquals(x.Data.SaveId, saveId))
            ?? ReadAll(_autoSaveDir).FirstOrDefault(x => IdEquals(x.Data.SaveId, saveId));
    }

    // Deletes the file backing a save id.
    public bool DeleteBySaveId(string saveId)
    {
        var entry = ReadBySaveId(saveId);
        if (entry == null) return false;
        return TryDeleteFile(entry.FullPath);
    }

    // ---- private ----------------------------------------------------------

    // Serialises a save to a timestamped file, appending a suffix on name collisions.
    private static bool TrySave(string directory, SavedSessionData data, bool isAutoSave)
    {
        if (data == null || string.IsNullOrWhiteSpace(directory)) return false;
        if (!IsValidId(data.SaveId) || !IsValidId(data.SessionId)) return false;

        try
        {
            Directory.CreateDirectory(directory);

            var slug = BuildSlug(data.Name);
            if (isAutoSave && !slug.Contains("autosave", StringComparison.OrdinalIgnoreCase))
            {
                slug = "autosave-" + slug;
            }

            var candidate = BuildFileName(data.SavedAtUtc, slug);
            var path = Path.Combine(directory, candidate);
            var suffix = 1;
            while (File.Exists(path))
            {
                candidate = BuildFileName(data.SavedAtUtc, $"{slug}-{suffix++}");
                path = Path.Combine(directory, candidate);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Session save failed for {directory}", ex);
            return false;
        }
    }

    // Yields every valid save file in a directory.
    private static IEnumerable<SessionFileEntry> ReadAll(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            yield break;

        foreach (var path in Directory.EnumerateFiles(directory, "????-??-??_??-??-??-*.json"))
        {
            SessionFileEntry entry = null;
            try
            {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<SavedSessionData>(json, JsonOptions);
                if (data != null && IsValidId(data.SaveId) && IsValidId(data.SessionId))
                    entry = new SessionFileEntry(Path.GetFileName(path), path, data);
            }
            catch
            {
                // Malformed files are skipped.
            }
            if (entry != null) yield return entry;
        }
    }

    // Deletes the oldest autosaves once the file or byte limit is exceeded.
    private static void TrimAutoSaves(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        var isAutoSaveStore = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Equals("AutoSaves", StringComparison.OrdinalIgnoreCase);

        var candidates = Directory.EnumerateFiles(directory, "????-??-??_??-??-??-*.json")
            .Select(path => { try { return new FileInfo(path); } catch { return null; } })
            .Where(fi => fi is { Exists: true })
            .Where(fi => isAutoSaveStore || fi.Name.Contains("autosave", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ThenByDescending(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var bytesKept = 0L;
        var filesKept = 0;
        foreach (var fi in candidates)
        {
            var mustKeep = filesKept < MinAutoSaveFilesToKeep;
            var withinFileLimit = filesKept < MaxAutoSaveFilesPerStore;
            var withinSizeLimit = bytesKept + fi.Length <= MaxAutoSaveBytesPerStore;
            if (mustKeep || (withinFileLimit && withinSizeLimit))
            {
                filesKept++;
                bytesKept += fi.Length;
                continue;
            }
            TryDeleteFile(fi.FullName);
        }
    }

    // Removes recent autosaves whose contents match the one just written.
    private static void DeleteEquivalentAutoSaves(string directory, SavedSessionData saved)
    {
        if (saved == null) return;

        var recent = ReadAll(directory)
            .OrderByDescending(x => x.Data.SavedAtUtc)
            .Take(AutoSaveEquivalentScanLimit)
            .Where(x => !IdEquals(x.Data.SaveId, saved.SaveId))
            .Where(x => x.Data.IsAutoSave)
            .Where(x => AreEquivalentContents(x.Data, saved))
            .ToArray();

        foreach (var entry in recent)
        {
            TryDeleteFile(entry.FullPath);
        }
    }

    // Compares two saves by summary counts and map ids.
    private static bool AreEquivalentContents(SavedSessionData a, SavedSessionData b)
    {
        if (a == null || b == null) return false;
        var aSummary = a.Summary ?? new SessionSummary();
        var bSummary = b.Summary ?? new SessionSummary();
        if (aSummary.MapsCompleted != bSummary.MapsCompleted ||
            aSummary.BeastsFound != bSummary.BeastsFound ||
            aSummary.RedBeastsFound != bSummary.RedBeastsFound) return false;

        var aIds = (a.MapHistory ?? []).Select(x => x?.MapId ?? string.Empty)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var bIds = (b.MapHistory ?? []).Select(x => x?.MapId ?? string.Empty)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return aIds.SequenceEqual(bIds, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Session file delete skipped for {path}: {ex.Message}");
            return false;
        }
    }

    private static string BuildFileName(DateTime savedAtUtc, string slug) =>
        $"{savedAtUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss}-{slug}.json";

    // Converts a save name into a filename-safe slug.
    private static string BuildSlug(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "auto";
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-');
        var raw = new string(chars.ToArray());
        while (raw.Contains("--", StringComparison.Ordinal))
            raw = raw.Replace("--", "-", StringComparison.Ordinal);
        var trimmed = raw.Trim('-');
        return string.IsNullOrEmpty(trimmed) ? "auto" : trimmed;
    }

    private static bool IsValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(ch => char.IsLetterOrDigit(ch) || ch == '-');

    private static bool IdEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

// A save file on disk and its deserialised contents.
public sealed record SessionFileEntry(string FileName, string FullPath, SavedSessionData Data);
