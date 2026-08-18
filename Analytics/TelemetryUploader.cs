using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;

namespace BeastsV3.Analytics;

// Opt-in, anonymous map submissions. Rules, in priority order:
//   1. Off by default; nothing leaves the machine until the user turns it on.
//   2. Strict allowlist: the payload is built field by field, never by serialising a
//      record wholesale, so a new field cannot start being uploaded by accident.
//   3. Legible: BuildPreviewJson produces exactly what would be sent.
//   4. Never disruptive: a failed upload is logged at debug level and forgotten.
public sealed class TelemetryUploader
{
    // Bumped with MapAnalyticsRecord.CurrentSchemaVersion; the server rejects unknown versions.
    private const int PayloadSchemaVersion = 2;

    private const int MaxBatchSize = 50;
    private const string InstallIdFileName = "BeastsV3_installId.txt";

    // The collector URL, XOR-obscured to keep it out of trivial repo scrapes rather than to
    // hide where data goes: it decodes at runtime, and Preview Submission shows the exact
    // bytes and destination before anything is sent.
    private const string ObscuredEndpoint =
        "KhEVAwdJeRxPJgAfFzETUl4AFjpWQCYVHhpsBwQSBwclHlkmDQkOJxETCloEOUFGJhMfTSYAF1wCQnleTDMS";

    private static readonly byte[] EndpointObscureKey = Encoding.UTF8.GetBytes("BeastsV3-Calc");

    private static string DecodeEndpoint()
    {
        var raw = Convert.FromBase64String(ObscuredEndpoint);
        var bytes = new byte[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            bytes[i] = (byte)(raw[i] ^ EndpointObscureKey[i % EndpointObscureKey.Length]);
        return Encoding.UTF8.GetString(bytes);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions PreviewOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly BeastsSettings _settings;
    private readonly Queue<MapAnalyticsRecord> _pending = new();
    private string _installId;
    private bool _sending;

    // When the interval-gated flush last ran, whether or not it sent anything. Drives the
    // "next upload in..." countdown; unrelated to a direct FlushAsync.
    private DateTime _lastFlushUtc = DateTime.MinValue;

    public TelemetryUploader(BeastsSettings settings)
    {
        _settings = settings;
    }

    public int PendingCount => _pending.Count;

    // A random per-install id, stored with the settings. Never derived from an account,
    // character or machine, and resettable.
    public string InstallId
    {
        get
        {
            if (!string.IsNullOrEmpty(_installId)) return _installId;

            var path = GetInstallIdPath();
            try
            {
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path).Trim();
                    if (IsValidInstallId(existing)) return _installId = existing;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Could not read install id. {ex.GetType().Name}: {ex.Message}");
            }

            return _installId = ResetInstallId();
        }
    }

    // Generates a new anonymous id, discarding any link to previously sent data.
    public string ResetInstallId()
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var path = GetInstallIdPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not persist install id. {ex.GetType().Name}: {ex.Message}");
        }
        return _installId = id;
    }

    // Queues a completed map. Does nothing unless sharing is switched on.
    public void Enqueue(MapAnalyticsRecord record)
    {
        if (record == null || !IsEnabled()) return;

        _pending.Enqueue(record);

        // Drop the oldest rather than growing without bound if uploads keep failing.
        while (_pending.Count > MaxBatchSize * 4) _pending.Dequeue();
    }

    // Time remaining until the periodic flush next runs, for display. Null when there is
    // nothing queued or sharing is off, so the UI can show "nothing to send" instead of a
    // countdown to an upload that will not happen.
    public TimeSpan? TimeUntilNextFlush(DateTime nowUtc)
    {
        if (!IsEnabled() || _pending.Count == 0) return null;
        if (_lastFlushUtc == DateTime.MinValue) return TimeSpan.Zero;

        var remaining = _lastFlushUtc.AddMinutes(IntervalMinutes) - nowUtc;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    // Call every frame; only actually flushes once per IntervalMinutes. Fire-and-forget
    // safe. Overlapping calls are ignored by FlushAsync's own _sending guard.
    public Task MaybeFlushAsync(DateTime nowUtc)
    {
        if (_pending.Count == 0) return Task.CompletedTask;
        if (_lastFlushUtc != DateTime.MinValue && (nowUtc - _lastFlushUtc).TotalMinutes < IntervalMinutes)
            return Task.CompletedTask;

        _lastFlushUtc = nowUtc;
        return FlushAsync();
    }

    // Backs the "Upload Now" button: sends immediately regardless of the interval, then
    // resets the countdown so the periodic flush does not immediately repeat right after.
    public Task ForceFlushAsync(DateTime nowUtc)
    {
        if (_pending.Count == 0) return Task.CompletedTask;

        _lastFlushUtc = nowUtc;
        return FlushAsync();
    }

    // Configurable so a user who wants fewer network calls can widen it, and lean on
    // Upload Now for anything they want sent sooner.
    private double IntervalMinutes => _settings.Analytics.Telemetry.UploadIntervalMinutes.Value;

    // Sends everything queued. Safe to call from a timer; overlapping calls are ignored.
    public async Task FlushAsync()
    {
        if (_sending || !IsEnabled() || _pending.Count == 0) return;

        var endpoint = GetEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        _sending = true;
        try
        {
            var batch = new List<MapAnalyticsRecord>();
            while (batch.Count < MaxBatchSize && _pending.Count > 0) batch.Add(_pending.Dequeue());

            var payload = BuildPayload(batch);
            var body = JsonSerializer.Serialize(payload, JsonOptions);

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(endpoint, content);

            if (response.IsSuccessStatusCode)
            {
                Log.Debug($"Telemetry: sent {batch.Count} map(s).");
            }
            else
            {
                // Requeue so a transient server error does not lose the data. A 400 means
                // the payload is wrong and retrying will not help, so drop those.
                if ((int)response.StatusCode >= 500)
                {
                    foreach (var record in Enumerable.Reverse(batch)) _pending.Enqueue(record);
                }
                Log.Debug($"Telemetry: server returned {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Telemetry upload failed. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _sending = false;
        }
    }

    // Exactly what would be uploaded, pretty-printed. Backs the "Preview Submission"
    // button: nobody should have to trust a description when they can read the bytes.
    public string BuildPreviewJson(IEnumerable<MapAnalyticsRecord> records)
    {
        var sample = (records ?? []).Take(MaxBatchSize).ToList();
        return JsonSerializer.Serialize(BuildPayload(sample), PreviewOptions);
    }

    // Writes a preview file and returns its path.
    public string WritePreviewFile(IEnumerable<MapAnalyticsRecord> records)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(), "config", "BeastsV3TelemetryPreview.json");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BuildPreviewJson(records));
        return path;
    }

    // ---- payload ----------------------------------------------------------

    // Field-by-field allowlist. Adding a field to MapAnalyticsRecord does not add it here.
    private TelemetryBatch BuildPayload(IReadOnlyCollection<MapAnalyticsRecord> records) => new()
    {
        InstallId = InstallId,
        Maps = records.Select(r => new TelemetryMap
        {
            SchemaVersion = PayloadSchemaVersion,
            MapId = r.MapId,
            League = r.League,
            GameVersion = r.GameVersion,
            MapTier = r.MapTier,
            AreaName = r.AreaName,
            DurationSeconds = r.DurationSeconds,
            BeastsFound = r.BeastsFound,
            RedBeastsFound = r.RedBeastsFound,
            UsedBestiaryScarabOfDuplicating = r.UsedBestiaryScarabOfDuplicating,
            AtlasNodes = r.Atlas?.AllocatedNodes ?? [],
            Scarabs = r.ScarabNames ?? [],
            MapAdditionalRedBeasts = r.MapAdditionalRedBeasts,
            MapDuplicateCapturedBeastsChancePct = r.MapDuplicateCapturedBeastsChancePct,
            Beasts = (r.BeastBreakdown ?? [])
                .Where(b => b.Count > 0 && !string.IsNullOrWhiteSpace(b.BeastName))
                .Select(b => new TelemetryBeast { Name = b.BeastName, Count = b.Count })
                .ToArray(),
        }).ToArray(),
    };

    private bool IsEnabled() => _settings.Analytics.Telemetry.ShareAnonymousData.Value;

    private static string GetEndpoint() => DecodeEndpoint();

    // The collector every upload posts to, so the preview can name the destination as well as
    // the payload.
    public static string EndpointUrl => DecodeEndpoint();

    private static string GetInstallIdPath() =>
        Path.Combine(Directory.GetCurrentDirectory(), "config", "global", InstallIdFileName);

    private static bool IsValidInstallId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 32 &&
        value.All(Uri.IsHexDigit);

    // ---- wire shapes ------------------------------------------------------
    // Deliberately separate from the analytics models: these are the only fields that can ever
    // leave the machine. Notably absent: character and account name, session name, free-text
    // tags, prices paid, area hash and file paths.

    private sealed class TelemetryBatch
    {
        public string InstallId { get; set; } = string.Empty;
        public TelemetryMap[] Maps { get; set; } = [];
    }

    private sealed class TelemetryMap
    {
        public int SchemaVersion { get; set; }
        public string MapId { get; set; } = string.Empty;
        public string League { get; set; } = string.Empty;
        public string GameVersion { get; set; } = string.Empty;
        public int MapTier { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
        public int BeastsFound { get; set; }
        public int RedBeastsFound { get; set; }
        public bool UsedBestiaryScarabOfDuplicating { get; set; }
        public ushort[] AtlasNodes { get; set; } = [];
        public string[] Scarabs { get; set; } = [];
        public int? MapAdditionalRedBeasts { get; set; }
        public int? MapDuplicateCapturedBeastsChancePct { get; set; }
        public TelemetryBeast[] Beasts { get; set; } = [];
    }

    private sealed class TelemetryBeast
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
