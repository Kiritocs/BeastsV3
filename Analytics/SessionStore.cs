using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BeastsV3.Shared;

namespace BeastsV3.Analytics;

// JSON persistence for session snapshots under config/BeastsV3Sessions (autosaves in
// AutoSaves/). Each session keeps a single autosave file, overwritten as maps complete.
// Trims autosaves to the file and byte limits below.
public sealed class SessionStore
{
    public const int MaxAutoSaveFilesPerStore = 60;
    public const int MinAutoSaveFilesToKeep = 25;
    public const long MaxAutoSaveBytesPerStore = 64L * 1024L * 1024L;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly string _namedDir;
    private readonly string _autoSaveDir;

    // The file each session's autosave was last written to. Saves run every few seconds, so
    // reading every autosave back to find the previous one would cost a full parse each time.
    private readonly Dictionary<string, string> _lastAutoSavePath = new(StringComparer.OrdinalIgnoreCase);

    // When each session's newest written autosave was taken, so a slower, older one cannot
    // replace it.
    private readonly Dictionary<string, DateTime> _lastAutoSaveAtUtc = new(StringComparer.OrdinalIgnoreCase);

    // Writes and trims run under this lock, so a background autosave and a direct one never
    // touch the same files at once.
    private readonly object _writeLock = new();

    // Background autosaves: only the newest queued one is written; an older one still waiting
    // is dropped, since the newer one holds everything it had.
    private readonly object _queueLock = new();
    private SavedSessionData _queuedAutoSave;
    private bool _draining;
    private Task _drainTask = Task.CompletedTask;

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
        lock (_writeLock)
        {
            TrimAutoSaves(_namedDir);
            TrimAutoSaves(_autoSaveDir);
        }
    }

    // Writes a named save.
    public bool SaveNamed(SavedSessionData data)
    {
        lock (_writeLock) return TrySave(_namedDir, data, isAutoSave: false, out _);
    }

    // Writes an autosave on a background thread. Serialising and writing a long session takes
    // 10-20 ms, which on the render thread is a visible hitch every save. The caller must hand
    // over data it no longer changes (BuildSavedSessionData copies everything).
    public void QueueAutoSave(SavedSessionData data)
    {
        if (data == null) return;
        lock (_queueLock)
        {
            _queuedAutoSave = data;
            if (_draining) return;
            _draining = true;
            _drainTask = Task.Run(DrainAutoSaves);
        }
    }

    private void DrainAutoSaves()
    {
        while (true)
        {
            SavedSessionData next;
            lock (_queueLock)
            {
                next = _queuedAutoSave;
                _queuedAutoSave = null;
                if (next == null)
                {
                    _draining = false;
                    return;
                }
            }

            try { SaveAutoSave(next); }
            catch (Exception ex) { Log.Error("Background autosave failed", ex); }
        }
    }

    // Waits for a background autosave still being written, e.g. before the plugin unloads.
    public void WaitForQueuedAutoSaves(TimeSpan timeout)
    {
        Task task;
        lock (_queueLock) task = _drainTask;
        try { task.Wait(timeout); }
        catch (Exception ex) { Log.Debug($"Waiting for the background autosave failed: {ex.Message}"); }
    }

    // Writes an autosave now, replacing any prior one for the same session (one file per session).
    // A queued background autosave is older than this one and is dropped.
    public bool SaveAutoSave(SavedSessionData data)
    {
        if (data == null) return false;
        lock (_queueLock)
        {
            if (_queuedAutoSave != null && _queuedAutoSave.SavedAtUtc <= data.SavedAtUtc) _queuedAutoSave = null;
        }

        lock (_writeLock) return WriteAutoSave(data);
    }

    private bool WriteAutoSave(SavedSessionData data)
    {
        // A background save that was already under way when a newer direct save was written.
        if (_lastAutoSaveAtUtc.TryGetValue(data.SessionId, out var newest) && data.SavedAtUtc < newest) return true;

        var previousForSession = _lastAutoSavePath.TryGetValue(data.SessionId, out var known)
            ? [known]
            : ReadAll(_autoSaveDir)
                .Where(x => x.Data.IsAutoSave)
                .Where(x => IdEquals(x.Data.SessionId, data.SessionId))
                .Select(x => x.FullPath)
                .ToArray();

        if (!TrySave(_autoSaveDir, data, isAutoSave: true, out var path)) return false;

        _lastAutoSavePath[data.SessionId] = path;
        _lastAutoSaveAtUtc[data.SessionId] = data.SavedAtUtc;
        foreach (var previous in previousForSession)
        {
            if (!string.Equals(previous, path, StringComparison.OrdinalIgnoreCase)) TryDeleteFile(previous);
        }
        EnsureAutoSaveMaintenance();
        return true;
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

    // Serialises a save to a timestamped file, appending a suffix on name collisions. Written
    // to a temporary name first, so a crash mid-write never leaves a cut-off save behind.
    private static bool TrySave(string directory, SavedSessionData data, bool isAutoSave, out string path)
    {
        path = null;
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
            path = Path.Combine(directory, candidate);
            var suffix = 1;
            while (File.Exists(path))
            {
                candidate = BuildFileName(data.SavedAtUtc, $"{slug}-{suffix++}");
                path = Path.Combine(directory, candidate);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temp, path);
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
