using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeastsV3.Prices;

// Fetches beast and market prices from poe.ninja, caches them, and persists the enabled
// beast sets and last-updated stamp to the plugin's settings JSON.
public sealed class PriceService
{
    private const string SettingsFileName = "BeastsV3_settings.json";
    private const string ItemOverviewEndpoint = "economy/stash/current/item/overview";
    private const string ExchangeOverviewEndpoint = "economy/exchange/current/overview";

    private static readonly HttpClient Http = new();
    private static readonly string[] MarketTypes = ["Scarab", "Map", "Fragment", "Currency", "Invitation"];
    private static readonly Dictionary<string, string> EndpointByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Beast"] = ItemOverviewEndpoint,
        ["Scarab"] = ExchangeOverviewEndpoint,
        ["Map"] = ItemOverviewEndpoint,
        ["Fragment"] = ExchangeOverviewEndpoint,
        ["Currency"] = ExchangeOverviewEndpoint,
        ["Invitation"] = ItemOverviewEndpoint,
    };
    private static readonly Regex MapTierInName = new(@"\(\s*Tier\s*(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConfiguredMapTier = new(@"^Map \(Tier\s*(\d+)\)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly BeastsSettings _settings;
    private readonly Func<string> _serverLeague;
    private Dictionary<string, float> _beastPrices = BeastCatalog.All.ToDictionary(x => x.Name, _ => -1f, StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, float> _marketPrices = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, float> _mapTierAverages = new();
    private Dictionary<string, string> _beastPriceTexts = new(StringComparer.OrdinalIgnoreCase);

    // Talisman prices keyed by beast name, matching how callers look them up.
    private Dictionary<string, float> _talismanPrices = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _talismanPriceTexts = new(StringComparer.OrdinalIgnoreCase);

    private TrackedBeast[] _sortedByPrice = BeastCatalog.All;
    private bool _fetching;
    private DateTime _lastFetchUtc = DateTime.MinValue;

    // serverLeague returns the league the game reports for the logged-in character.
    public PriceService(BeastsSettings settings, Func<string> serverLeague = null)
    {
        _settings = settings;
        _serverLeague = serverLeague;
    }

    // How stale poe.ninja's own copy was when last read, from their Age header. Null means
    // they sent none, which is not the same as zero. Worth having because LastUpdated only
    // records when this plugin fetched, and the response body carries no timestamp.
    public int? UpstreamAgeSeconds { get; private set; }

    // Age for the current refresh cycle, so a partial refresh cannot publish a mixed figure.
    private int? _pendingUpstreamAge;

    public IReadOnlyDictionary<string, float> BeastPrices => _beastPrices;
    public IReadOnlyDictionary<string, float> MarketPrices => _marketPrices;
    public IReadOnlyDictionary<int, float> MapTierAverages => _mapTierAverages;
    public TrackedBeast[] SortedByPrice => _sortedByPrice;

    public bool TryGetBeastPriceText(string beastName, out string text) =>
        _beastPriceTexts.TryGetValue(beastName ?? string.Empty, out text);

    public IReadOnlyDictionary<string, float> TalismanPrices => _talismanPrices;

    public bool TryGetTalismanPriceText(string beastName, out string text) =>
        _talismanPriceTexts.TryGetValue(beastName ?? string.Empty, out text);

    // ---- selection semantics -------------------------------------------

    // Selected as worth capturing.
    public bool IsTracked(string beastName) =>
        !string.IsNullOrEmpty(beastName) && _settings.BeastPrices.EnabledBeasts.Contains(beastName);

    // Selected for its talisman, and talisman tracking is on.
    public bool IsTalismanSelected(string beastName) =>
        _settings.BeastPrices.TrackTalismanPrices.Value &&
        !string.IsNullOrEmpty(beastName) &&
        _settings.BeastPrices.EnabledTalismans.Contains(beastName);

    // Selected for its talisman only, which gets the amber overlay colors.
    public bool IsTalismanOnly(string beastName) =>
        !IsTracked(beastName) && IsTalismanSelected(beastName);

    // Whether a beast passes the "Only Show Enabled Beasts" filter.
    public bool IsShownWhileEnabledOnly(string beastName) =>
        IsTracked(beastName) || IsTalismanSelected(beastName);

    // Overlay price text, with the talisman price appended when combining or talisman-only.
    public string GetDisplayPriceText(string beastName)
    {
        _beastPriceTexts.TryGetValue(beastName ?? string.Empty, out var beastText);

        if (!_settings.BeastPrices.TrackTalismanPrices.Value) return beastText;
        if (!_settings.BeastPrices.CombineTalismanPrice.Value && !IsTalismanOnly(beastName)) return beastText;
        if (!IsTalismanSelected(beastName)) return beastText;
        if (!TryGetTalismanPriceText(beastName, out var talismanText)) return beastText;

        return string.IsNullOrEmpty(beastText) ? $"+{talismanText}" : $"{beastText} +{talismanText}";
    }

    public void ToggleTalismanEnabled(string beastName, bool enabled)
    {
        var set = _settings.BeastPrices.EnabledTalismans;
        if (enabled) set.Add(beastName);
        else set.Remove(beastName);
        SavePersisted();
    }

    public void SetAllTalismansEnabled(bool enabled) =>
        ApplyTalismanFilter(_ => enabled);

    public void EnableTalismansPricedAtLeast(float minChaos) =>
        ApplyTalismanFilter(t => _talismanPrices.TryGetValue(t.BeastName, out var price) && price >= minChaos);

    private void ApplyTalismanFilter(Func<TalismanInfo, bool> predicate)
    {
        var set = _settings.BeastPrices.EnabledTalismans;
        set.Clear();
        set.UnionWith(TalismanCatalog.All.Where(predicate).Select(x => x.BeastName));
        SavePersisted();
    }

    public bool TryGetItemPriceChaos(string configuredName, out double chaos)
    {
        chaos = 0;
        if (string.IsNullOrWhiteSpace(configuredName)) return false;

        var normalized = configuredName.Trim();
        if (_marketPrices.TryGetValue(normalized, out var direct) && direct > 0)
        {
            chaos = direct;
            return true;
        }

        var tierMatch = ConfiguredMapTier.Match(normalized);
        if (tierMatch.Success && int.TryParse(tierMatch.Groups[1].Value, out var tier) &&
            _mapTierAverages.TryGetValue(tier, out var avg) && avg > 0)
        {
            chaos = avg;
            return true;
        }

        return false;
    }

    public void LoadPersisted()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return;

            var root = JObject.Parse(File.ReadAllText(path));
            if (root["BeastPrices"] is not JObject section) return;

            _settings.BeastPrices.LastUpdated = section["LastUpdated"]?.Value<string>() ?? _settings.BeastPrices.LastUpdated;

            if (section["EnabledBeasts"] is JArray arr)
            {
                _settings.BeastPrices.EnabledBeasts = new HashSet<string>(
                    arr.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)),
                    StringComparer.OrdinalIgnoreCase);
            }

            if (section["EnabledTalismans"] is JArray talismanArr)
            {
                _settings.BeastPrices.EnabledTalismans = new HashSet<string>(
                    talismanArr.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load persisted beast price settings", ex);
        }
    }

    public void SavePersisted()
    {
        try
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var content = File.Exists(path) ? File.ReadAllText(path) : null;
            var root = string.IsNullOrWhiteSpace(content) ? new JObject() : JObject.Parse(content);
            var section = root["BeastPrices"] as JObject ?? new JObject();
            section["LastUpdated"] = _settings.BeastPrices.LastUpdated;
            section["EnabledBeasts"] = new JArray(_settings.BeastPrices.EnabledBeasts
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            section["EnabledTalismans"] = new JArray(_settings.BeastPrices.EnabledTalismans
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            root["BeastPrices"] = section;
            File.WriteAllText(path, root.ToString());
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save persisted beast price settings", ex);
        }
    }

    public void QueueFetch() => Log.FireAndForget(StartOrJoinFetch, "Price fetch");

    // The fetch in flight, so concurrent callers join it instead of hitting poe.ninja twice.
    private Task _inFlightFetch;
    private readonly object _fetchSync = new();

    // Every fetch goes through here so there is exactly one task to await. FetchAsync early-
    // returns while a fetch is running, handing back a completed task - which would let
    // EnsureFreshAsync report success against prices it never waited for.
    private Task StartOrJoinFetch()
    {
        lock (_fetchSync)
        {
            if (_inFlightFetch is { IsCompleted: false }) return _inFlightFetch;
            return _inFlightFetch = FetchAsync();
        }
    }

    // Refreshes prices and waits for them unless they are recent enough. For paths that price
    // something for real (listing at Faustus), where a stale number is money. True when the
    // prices in hand are fresh. Never throws: poe.ninja being down must not abort a sell run.
    public async Task<bool> EnsureFreshAsync(TimeSpan maxAge, int timeoutMs, CancellationToken ct = default)
    {
        var age = DateTime.UtcNow - _lastFetchUtc;
        if (age < maxAge)
        {
            Log.Debug($"Prices are {age.TotalSeconds:0}s old, within the {maxAge.TotalSeconds:0}s window. No refresh needed.");
            return true;
        }

        var fetch = StartOrJoinFetch();

        try
        {
            var completed = await Task.WhenAny(fetch, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);
            if (completed != fetch)
            {
                Log.Warn($"Price refresh did not finish within {timeoutMs}ms. Continuing with prices from {_settings.BeastPrices.LastUpdated}.");
                return false;
            }

            // Surfaces a fetch that faulted rather than timed out.
            await fetch.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Price refresh failed ({ex.GetType().Name}: {ex.Message}). Continuing with prices from {_settings.BeastPrices.LastUpdated}.");
            return false;
        }
    }

    // Points the League setting at the character's actual league; true when it changed.
    public bool SyncLeagueFromServerData()
    {
        if (_settings.BeastPrices.AutoSyncLeague?.Value != true) return false;

        var league = _serverLeague?.Invoke();
        if (string.IsNullOrWhiteSpace(league)) return false;

        var current = _settings.BeastPrices.League?.Value ?? string.Empty;
        if (string.Equals(current, league, StringComparison.OrdinalIgnoreCase)) return false;

        _settings.BeastPrices.League.Value = league;
        Log.Info($"League auto-synced from the game: '{current}' -> '{league}'.");
        return true;
    }

    // Kicks off an auto-refresh if enough time has passed since the last attempt.
    public void MaybeAutoRefresh(DateTime nowUtc)
    {
        var intervalMinutes = _settings.BeastPrices.AutoRefreshMinutes.Value;
        if (intervalMinutes <= 0 || _fetching) return;
        if ((nowUtc - _lastFetchUtc).TotalMinutes < intervalMinutes) return;
        QueueFetch();
    }

    public void SetAllEnabled(bool enabled) =>
        ApplyEnabledFilter(_ => enabled);

    public void EnableOnlyPricedAtLeast(float minChaos)
    {
        ApplyEnabledFilter(beast =>
            _beastPrices.TryGetValue(beast.Name, out var price) && price >= minChaos);
    }

    public void ToggleEnabled(string beastName, bool enabled)
    {
        var enabledSet = _settings.BeastPrices.EnabledBeasts;
        if (enabled) enabledSet.Add(beastName);
        else enabledSet.Remove(beastName);
        SavePersisted();
    }

    private void ApplyEnabledFilter(Func<TrackedBeast, bool> predicate)
    {
        var enabled = _settings.BeastPrices.EnabledBeasts;
        enabled.Clear();
        enabled.UnionWith(BeastCatalog.All.Where(predicate).Select(x => x.Name));
        SavePersisted();
    }

    private async Task FetchAsync()
    {
        if (_fetching) return;
        _fetching = true;
        _lastFetchUtc = DateTime.UtcNow;

        // Cleared per cycle, or the oldest age ever seen would stick around forever.
        _pendingUpstreamAge = null;

        try
        {
            // Re-checked per fetch to catch a mid-session character swap.
            SyncLeagueFromServerData();

            var league = Uri.EscapeDataString(_settings.BeastPrices.League.Value?.Trim() ?? "Allflame");
            Log.Info("Fetching prices from poe.ninja...");

            var beastLookup = await FetchTypeAsync(league, "Beast");
            if (beastLookup == null) return;

            var updatedBeasts = BeastCatalog.All.ToDictionary(
                b => b.Name,
                b => beastLookup.TryGetValue(b.Name, out var p) ? p : -1f,
                StringComparer.OrdinalIgnoreCase);
            _beastPrices = updatedBeasts;
            RebuildBeastCaches(updatedBeasts);

            var market = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var tierBuckets = new Dictionary<int, List<float>>();

            foreach (var type in MarketTypes)
            {
                var lookup = await FetchTypeAsync(league, type, tierBuckets);
                if (lookup == null) continue;

                foreach (var (name, price) in lookup) market[name] = price;
            }

            _marketPrices = market;
            _mapTierAverages = tierBuckets.ToDictionary(
                x => x.Key,
                x => x.Value.Count > 0 ? x.Value.Average() : 0f);

            await FetchTalismanPricesAsync(league);

            // Published only now, so a refresh that threw cannot leave an age for uninstalled data.
            UpstreamAgeSeconds = _pendingUpstreamAge;

            _settings.BeastPrices.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            SavePersisted();

            var upstream = UpstreamAgeSeconds is { } age
                ? $", poe.ninja's copy was {age / 60} min old"
                : ", poe.ninja sent no age";
            Log.Info($"Prices updated ({_settings.BeastPrices.LastUpdated}{upstream}).");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to fetch prices", ex);
        }
        finally
        {
            _fetching = false;
        }
    }

    // GETs a poe.ninja overview and records how stale their copy was, from the Age header.
    // GetAsync rather than GetStringAsync, which discards headers. Several overviews are
    // fetched per refresh; the oldest age is kept, so the figure describes the worst case.
    private async Task<string> GetJsonRecordingAgeAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        if (response.Headers.TryGetValues("Age", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var age) &&
            age >= 0 &&
            (_pendingUpstreamAge is not { } current || age > current))
        {
            _pendingUpstreamAge = age;
        }

        return await response.Content.ReadAsStringAsync();
    }

    // Fetches talisman prices from the BaseType feed; skipped when talismans are off.
    private async Task FetchTalismanPricesAsync(string escapedLeague)
    {
        if (!_settings.BeastPrices.TrackTalismanPrices.Value) return;

        try
        {
            var url = BuildOverviewUrl(escapedLeague, "BaseType");
            var json = await GetJsonRecordingAgeAsync(url);
            var response = JsonConvert.DeserializeObject<OverviewResponse>(json);
            if (response?.Lines == null) return;

            var namesById = BuildNameById(response);
            var best = new Dictionary<string, OverviewLine>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in response.Lines)
            {
                // Influenced bases are a different market and are excluded.
                if (!string.IsNullOrEmpty(line?.Variant)) continue;

                var name = GetLineName(line, namesById);
                if (!TalismanCatalog.TryGetByTalismanName(name, out var talisman)) continue;
                if (GetLineChaosValue(line) <= 0) continue;

                // The feed lists each base once per item-level bracket; keep the best-supported.
                if (!best.TryGetValue(talisman.TalismanName, out var existing) || IsDeeperListing(line, existing))
                    best[talisman.TalismanName] = line;
            }

            var pricesByBeast = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var talisman in TalismanCatalog.All)
            {
                pricesByBeast[talisman.BeastName] = best.TryGetValue(talisman.TalismanName, out var line)
                    ? GetLineChaosValue(line)
                    : -1f;
            }

            _talismanPrices = pricesByBeast;
            _talismanPriceTexts = pricesByBeast
                .Where(x => x.Value >= 0)
                .ToDictionary(x => x.Key, x => $"{x.Value:0}c", StringComparer.OrdinalIgnoreCase);

            Log.Debug($"Talisman prices updated for {best.Count} of {TalismanCatalog.All.Length} talismans.");
        }
        catch (Exception ex)
        {
            Log.Debug($"Skipping poe.ninja talisman prices. {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Ranks listings by listing count, then item level.
    private static bool IsDeeperListing(OverviewLine candidate, OverviewLine current)
    {
        var candidateListings = candidate?.ListingCount ?? 0;
        var currentListings = current?.ListingCount ?? 0;
        if (candidateListings != currentListings) return candidateListings > currentListings;

        return (candidate?.LevelRequired ?? 0) > (current?.LevelRequired ?? 0);
    }

    // Instance rather than static: it records the upstream age into _pendingUpstreamAge.
    private async Task<Dictionary<string, float>> FetchTypeAsync(
        string escapedLeague,
        string type,
        Dictionary<int, List<float>> tierBucketsOut = null)
    {
        try
        {
            var url = BuildOverviewUrl(escapedLeague, type);
            var json = await GetJsonRecordingAgeAsync(url);
            var response = JsonConvert.DeserializeObject<OverviewResponse>(json);
            if (response?.Lines == null) return null;

            var namesById = BuildNameById(response);
            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in response.Lines)
            {
                var name = GetLineName(line, namesById);
                var value = GetLineChaosValue(line);
                if (string.IsNullOrWhiteSpace(name) || value <= 0) continue;

                if (!result.TryGetValue(name, out var existing) || value > existing)
                    result[name] = value;

                if (tierBucketsOut != null)
                {
                    var tier = GetLineMapTier(line, name);
                    if (tier.HasValue)
                    {
                        if (!tierBucketsOut.TryGetValue(tier.Value, out var bucket))
                            tierBucketsOut[tier.Value] = bucket = new List<float>();
                        bucket.Add(value);
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Debug($"Skipping poe.ninja {type} prices. {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string BuildOverviewUrl(string escapedLeague, string type)
    {
        var endpoint = EndpointByType.TryGetValue(type ?? string.Empty, out var e) ? e : ItemOverviewEndpoint;
        return $"https://poe.ninja/poe1/api/{endpoint}?league={escapedLeague}&type={Uri.EscapeDataString(type ?? string.Empty)}";
    }

    private static Dictionary<string, string> BuildNameById(OverviewResponse response)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response?.Items == null) return map;
        foreach (var item in response.Items)
        {
            if (!string.IsNullOrWhiteSpace(item?.Id) && !string.IsNullOrWhiteSpace(item.Name))
                map[item.Id] = item.Name;
        }
        return map;
    }

    private static string GetLineName(OverviewLine line, IReadOnlyDictionary<string, string> namesById)
    {
        if (!string.IsNullOrWhiteSpace(line?.Id) && namesById.TryGetValue(line.Id, out var byId)) return byId;
        if (!string.IsNullOrWhiteSpace(line?.Name)) return line.Name;
        return line?.CurrencyTypeName ?? string.Empty;
    }

    private static float GetLineChaosValue(OverviewLine line)
    {
        if (line == null) return -1f;
        if (line.PrimaryValue > 0) return line.PrimaryValue.Value;
        if (line.ChaosValue > 0) return line.ChaosValue.Value;
        if (line.ChaosEquivalent > 0) return line.ChaosEquivalent.Value;
        return -1f;
    }

    private static int? GetLineMapTier(OverviewLine line, string name)
    {
        if (line?.MapTier > 0) return line.MapTier;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var match = MapTierInName.Match(name);
        return match.Success && int.TryParse(match.Groups[1].Value, out var tier) && tier > 0 ? tier : null;
    }

    private void RebuildBeastCaches(Dictionary<string, float> prices)
    {
        _beastPriceTexts = BeastCatalog.All
            .Where(b => prices.TryGetValue(b.Name, out var p) && p >= 0)
            .ToDictionary(b => b.Name, b => $"{prices[b.Name]:0}c", StringComparer.OrdinalIgnoreCase);

        _sortedByPrice = BeastCatalog.All
            .OrderByDescending(b => prices.TryGetValue(b.Name, out var p) ? p : -1f)
            .ToArray();
    }

    private static string GetSettingsPath() =>
        Path.Combine(Directory.GetCurrentDirectory(), "config", "global", SettingsFileName);

    private class OverviewResponse
    {
        [JsonProperty("items")] public List<OverviewItem> Items { get; set; }
        [JsonProperty("lines")] public List<OverviewLine> Lines { get; set; }
    }

    private class OverviewItem
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
    }

    private class OverviewLine
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("currencyTypeName")] public string CurrencyTypeName { get; set; }
        [JsonProperty("primaryValue")] public float? PrimaryValue { get; set; }
        [JsonProperty("chaosValue")] public float? ChaosValue { get; set; }
        [JsonProperty("chaosEquivalent")] public float? ChaosEquivalent { get; set; }
        [JsonProperty("mapTier")] public int? MapTier { get; set; }

        // BaseType feed only.
        [JsonProperty("variant")] public string Variant { get; set; }
        [JsonProperty("levelRequired")] public int? LevelRequired { get; set; }
        [JsonProperty("listingCount")] public int? ListingCount { get; set; }
    }
}
