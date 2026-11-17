using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Prices;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;

namespace BeastsV3.Analytics;

// Polls the map device for its loadout and keeps a prepared and a current cost breakdown.
// Prepared is copied to current when a trackable map starts. Manual extra cost is added as
// an "Extra (Manual)" line.
public sealed class CostTracker
{
    private const string ExtraManualLineName = "Extra (Manual)";
    private const string BestiaryThreadName = "Bestiary Thread";

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly PriceService _prices;

    private readonly List<MapCostItem> _prepared = new();
    private readonly List<MapCostItem> _current = new();
    private DateTime _lastPollUtc = DateTime.MinValue;

    // Free threads a Shaped Region puts into each thread slot, by slot position. Learned while
    // the slot sits at its minimum and kept until another map is selected, since the count
    // itself looks the same once the player adds threads of their own.
    private readonly int[] _regionFreeBySlot = new int[3];
    private string _deviceMapTitle;
    private int _preparedRegionFreeBestiary;
    private int _currentRegionFreeBestiary;
    private DateTime _lastReadUtc = DateTime.MinValue;

    // Map stats can lag the area load, so one read at map start is unreliable. Bounded retry:
    // once the table is populated, an absent stat is a real zero.
    private const double ModifierPollIntervalMs = 1000;
    private const int MaxModifierPolls = 10;

    private DateTime _lastModifierPollUtc = DateTime.MinValue;
    private int _modifierPolls;
    private bool _modifierLogged;

    public CostTracker(GameController game, BeastsSettings settings, PriceService prices)
    {
        _game = game;
        _settings = settings;
        _prices = prices;
    }

    public IReadOnlyList<MapCostItem> Prepared => _prepared;
    public IReadOnlyList<MapCostItem> Current => _current;

    public double? CurrentDeviceReadAgeMs { get; private set; }

    // Latched from the map's stat table while inside the map, and kept - the stats are gone
    // once you leave, but the map record is built after that.
    public int? CurrentAdditionalRedBeasts { get; private set; }
    public int? CurrentDuplicateCapturedChancePct { get; private set; }

    // Every beast and thread stat of the current map, latched with the two above.
    public Dictionary<string, int> CurrentMapStats { get; private set; } = new();

    // True when every capture in the current map is duplicated. A partial chance (3.30's
    // Bonded for Life is 20%) is not counted: which captures it hit cannot be observed.
    public bool CurrentMapDuplicatesCaptures => CurrentDuplicateCapturedChancePct >= 100;

    // Total chaos cost of the current breakdown.
    public double ComputeCurrentCostChaos() => _current.Sum(x => x.UnitPriceChaos);

    // Returns a copy of the current breakdown.
    public MapCostItem[] SnapshotCurrent() => CloneList(_current).ToArray();

    // Grants a fresh polling window without discarding what is latched, for re-entering a
    // still-active map whose stats were unreadable in its opening seconds.
    public void ResumeMapModifierPolling()
    {
        _modifierPolls = 0;
        _lastModifierPollUtc = DateTime.MinValue;
        _modifierLogged = false;
    }

    public void LatchMapModifiers(DateTime nowUtc)
    {
        if (_modifierPolls >= MaxModifierPolls) return;

        // Both values come from a single reading, so one landing means both have.
        if (CurrentAdditionalRedBeasts is not null) return;

        if ((nowUtc - _lastModifierPollUtc).TotalMilliseconds < ModifierPollIntervalMs) return;
        _lastModifierPollUtc = nowUtc;
        _modifierPolls++;

        // Null means the table is not populated yet - keep retrying. A populated table with
        // a stat missing is a real zero, not an unknown, so there is nothing left to wait
        // for once this succeeds.
        if (MapModifierStats.Read(_game) is not { } reading)
        {
            if (!_modifierLogged && _modifierPolls >= MaxModifierPolls)
            {
                _modifierLogged = true;
                LogMapModifiers();
            }
            return;
        }

        CurrentAdditionalRedBeasts = reading.AdditionalRedBeasts;
        CurrentDuplicateCapturedChancePct = reading.DuplicateCapturedChancePct;
        CurrentMapStats = MapModifierStats.ReadRelevant(_game);
        AddBestiaryThreadsTheDeviceMissed(reading);

        if (!_modifierLogged)
        {
            _modifierLogged = true;
            LogMapModifiers();
        }
    }

    // The Map Device only names a thread once its slot was hovered, so a thread picked by hand
    // can be missing from the cost. The map itself says how many Bestiary Threads it got and
    // how many of those were free, which fills the gap. Only ever adds: a device read that
    // already lists them all is left alone.
    private void AddBestiaryThreadsTheDeviceMissed(MapModifierStats.Reading reading)
    {
        var paid = Math.Max(0, reading.PaidBestiaryThreads - _currentRegionFreeBestiary);
        var listed = _current.Count(x => string.Equals(x.ItemName, BestiaryThreadName, StringComparison.OrdinalIgnoreCase));

        if (paid == listed) return;

        if (paid < listed)
        {
            Log.Info($"Map cost: the Map Device listed {listed} Bestiary Thread(s) but the map reports {paid} paid " +
                     $"({reading.BestiaryThreads} on the map, {reading.FreeThreads} free from the atlas, " +
                     $"{_currentRegionFreeBestiary} free from a Shaped Region). Cost left as read from the device.");
            return;
        }

        _prices.TryGetItemPriceChaos(BestiaryThreadName, out var price);
        for (var i = listed; i < paid; i++)
            _current.Add(new MapCostItem { ItemName = BestiaryThreadName, UnitPriceChaos = price });

        Log.Info($"Map cost: added {paid - listed} Bestiary Thread(s) at {price:0.#}c from the map's own count " +
                 $"({reading.BestiaryThreads} on the map, {reading.FreeThreads} free from the atlas, " +
                 $"{_currentRegionFreeBestiary} free from a Shaped Region, Map Device listed {listed}).");
    }

    // Logged separately from "Map cost armed", which is written at map start before these
    // stats are readable.
    private void LogMapModifiers()
    {
        var reds = CurrentAdditionalRedBeasts is { } r ? r.ToString() : "unknown";
        var dup = CurrentDuplicateCapturedChancePct is { } d ? $"{d}%" : "unknown";

        Log.Info($"Map modifiers (from map stats): {reds} additional reds, duplicating={dup}, " +
                 $"attempt={_modifierPolls} | map device had recorded {_current.Count} item(s) | " +
                 $"beast/thread stats: {MapModifierStats.DescribeRelevant(_game)}");
    }

    // Re-reads the map device, throttled to Analytics.MapDeviceCapturePollIntervalMs.
    public void MaybePoll(DateTime nowUtc)
    {
        var intervalMs = Math.Max(50, _settings.Analytics.MapDeviceCapturePollIntervalMs.Value);
        if ((nowUtc - _lastPollUtc).TotalMilliseconds < intervalMs) return;
        _lastPollUtc = nowUtc;

        if (!TryReadMapDeviceItemNames(out var names)) return;

        _lastReadUtc = nowUtc;
        SetPrepared(BuildBreakdownFromNames(names));
    }

    // Moves the prepared breakdown into current and clears prepared.
    public void BeginCurrentFromPrepared()
    {
        _current.Clear();
        _current.AddRange(CloneList(_prepared));
        _prepared.Clear();
        _currentRegionFreeBestiary = _preparedRegionFreeBestiary;

        // Cleared for the new map, then relatched by LatchMapModifiers once inside it.
        // Carrying the previous map's stats over would be worse than having none.
        CurrentAdditionalRedBeasts = null;
        CurrentDuplicateCapturedChancePct = null;
        CurrentMapStats = new();
        _modifierPolls = 0;
        _lastModifierPollUtc = DateTime.MinValue;
        _modifierLogged = false;

        CurrentDeviceReadAgeMs = _lastReadUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (DateTime.UtcNow - _lastReadUtc).TotalMilliseconds);

        var age = CurrentDeviceReadAgeMs is { } ms ? $"{ms:0}ms" : "never read";
        Log.Info($"Map cost armed: deviceReadAge={age}, {_current.Count} item(s)" +
                 (_current.Count > 0
                     ? $": {string.Join(", ", _current.Select(x => $"{x.ItemName} {x.UnitPriceChaos:0.#}c"))}"
                     : " - Map Device was not read."));
    }

    // The current map's cost state, for an autosave.
    public MapCostInProgress SaveCurrent() => new()
    {
        Lines = SnapshotCurrent(),
        AdditionalRedBeasts = CurrentAdditionalRedBeasts,
        DuplicateCapturedChancePct = CurrentDuplicateCapturedChancePct,
        MapStats = new Dictionary<string, int>(CurrentMapStats),
        RegionFreeBestiary = _currentRegionFreeBestiary,
        DeviceReadAgeMs = CurrentDeviceReadAgeMs,
    };

    // Puts back a current map's cost state from an autosave.
    public void RestoreCurrent(MapCostInProgress saved)
    {
        ResetCurrent();
        if (saved == null) return;
        _current.AddRange(CloneList(saved.Lines));
        CurrentAdditionalRedBeasts = saved.AdditionalRedBeasts;
        CurrentDuplicateCapturedChancePct = saved.DuplicateCapturedChancePct;
        CurrentMapStats = new Dictionary<string, int>(saved.MapStats ?? new());
        _currentRegionFreeBestiary = saved.RegionFreeBestiary;
        CurrentDeviceReadAgeMs = saved.DeviceReadAgeMs;
    }

    // Clears the current breakdown.
    public void ResetCurrent()
    {
        _current.Clear();
        CurrentAdditionalRedBeasts = null;
        CurrentDuplicateCapturedChancePct = null;
        CurrentMapStats = new();
        _modifierPolls = 0;
        _lastModifierPollUtc = DateTime.MinValue;
        _modifierLogged = false;
    }

    // Replaces the prepared breakdown and appends the manual extra-cost line.
    public void SetPrepared(IEnumerable<MapCostItem> items)
    {
        _prepared.Clear();
        _prepared.AddRange(CloneList(items));

        var extra = _settings.Analytics.ExtraCostPerMapChaos.Value;
        if (extra > 0)
        {
            _prepared.Add(new MapCostItem { ItemName = ExtraManualLineName, UnitPriceChaos = extra });
        }
    }

    // Turns item names into priced cost lines.
    private List<MapCostItem> BuildBreakdownFromNames(IReadOnlyList<string> names)
    {
        var items = new List<MapCostItem>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            _prices.TryGetItemPriceChaos(name, out var price);
            items.Add(new MapCostItem { ItemName = name, UnitPriceChaos = price });
        }
        return items;
    }

    // Reads the base name of the item in each map device slot via reflection.
    private bool TryReadMapDeviceItemNames(out List<string> names)
    {
        names = null;
        var ui = _game?.IngameState?.IngameUi;
        var window = ui?.MapDeviceWindow;
        if (window == null) return false;
        if (window.IsVisible != true && ui.Atlas?.IsVisible != true) return false;

        if (window.GetType().GetProperty("ScarabSlots")?.GetValue(window) is not IEnumerable rawSlots)
            return false;

        names = new List<string>();
        foreach (var slot in rawSlots)
        {
            if (slot == null) continue;
            if (slot.GetType().GetProperty("VisibleInventoryItems")?.GetValue(slot) is not IEnumerable visibleItems)
                continue;

            foreach (var visibleItem in visibleItems)
            {
                if (visibleItem is not NormalInventoryItem inventoryItem) continue;
                var entity = inventoryItem.Item;
                if (entity == null) continue;

                var baseName = entity.GetComponent<Base>()?.Name?.Trim();
                if (!string.IsNullOrWhiteSpace(baseName))
                {
                    names.Add(baseName);
                    break;
                }

                var mapTier = entity.GetComponent<MapKey>()?.Tier;
                if (mapTier > 0)
                {
                    names.Add($"Map (Tier {mapTier})");
                    break;
                }
            }
        }

        AddThreadNames(window, names);
        return true;
    }

    private static bool IsBestiaryThread(string name) =>
        string.Equals(name, BestiaryThreadName, StringComparison.OrdinalIgnoreCase);

    // One line per thread paid for, so the cost is count x price. A Shaped Region's free
    // threads sit inside the slot's count and are taken off again.
    private void AddThreadNames(ExileCore.PoEMemory.Element window, List<string> names)
    {
        // Another map selected: what was learned about the last one no longer holds.
        var title = (window.GetType().GetProperty("Title")?.GetValue(window) as ExileCore.PoEMemory.Element)?.Text?.Trim();
        if (!string.IsNullOrEmpty(title) && !string.Equals(title, _deviceMapTitle, StringComparison.Ordinal))
        {
            _deviceMapTitle = title;
            Array.Clear(_regionFreeBySlot);
        }

        var slots = MapDeviceUi.ReadThreadSlots(window);
        var namedBestiary = slots.Any(s => IsBestiaryThread(s.Name));
        _preparedRegionFreeBestiary = 0;

        for (var i = 0; i < slots.Count && i < _regionFreeBySlot.Length; i++)
        {
            var slot = slots[i];
            if (slot.State != MapDeviceUi.ThreadSlotState.Picked)
            {
                _regionFreeBySlot[i] = 0;
                continue;
            }

            if (slot.AtMinimum && _regionFreeBySlot[i] != slot.Count)
            {
                _regionFreeBySlot[i] = slot.Count;
                Log.Debug($"Map Device: thread slot {i + 1} cannot go below {slot.Count} on '{_deviceMapTitle}', " +
                          "so that many threads are free from a Shaped Region.");
            }

            var free = Math.Min(_regionFreeBySlot[i], slot.Count);

            // A slot without a name is taken for the Bestiary one only when no slot names it.
            if (IsBestiaryThread(slot.Name) || (slot.Name == null && !namedBestiary))
                _preparedRegionFreeBestiary += free;

            if (slot.Name == null) continue;
            for (var n = free; n < slot.Count; n++) names.Add(slot.Name);
        }
    }

    // Copies cost lines, dropping entries with no name.
    private static List<MapCostItem> CloneList(IEnumerable<MapCostItem> items)
    {
        var result = new List<MapCostItem>();
        if (items == null) return result;
        foreach (var item in items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ItemName)) continue;
            result.Add(new MapCostItem
            {
                ItemName = item.ItemName,
                UnitPriceChaos = item.UnitPriceChaos,
            });
        }
        return result;
    }
}
