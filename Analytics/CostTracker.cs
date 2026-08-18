using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    private const string DuplicatingScarabName = "Bestiary Scarab of Duplicating";
    private const string HerdScarabName = "Bestiary Scarab of the Herd";
    private const string ExtraManualLineName = "Extra (Manual)";

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly PriceService _prices;

    private readonly List<MapCostItem> _prepared = new();
    private readonly List<MapCostItem> _current = new();
    private DateTime _lastPollUtc = DateTime.MinValue;
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

    public bool PreparedUsedDuplicatingScarab { get; private set; }
    public bool CurrentUsedDuplicatingScarab { get; private set; }

    public IReadOnlyList<MapCostItem> Prepared => _prepared;
    public IReadOnlyList<MapCostItem> Current => _current;

    public double? CurrentDeviceReadAgeMs { get; private set; }

    // Latched from the map's stat table while inside the map, and kept - the stats are gone
    // once you leave, but the map record is built after that.
    public int? CurrentAdditionalRedBeasts { get; private set; }
    public int? CurrentDuplicateCapturedChancePct { get; private set; }

    // Herd scarab count for the current map. Null when the stat could not be read.
    public int? CurrentHerdScarabCount =>
        CurrentAdditionalRedBeasts is { } n && n >= 0
            ? n / MapModifierStats.RedBeastsPerHerdScarab
            : null;

    public bool CurrentMapUsesDuplicatingScarab =>
        CurrentDuplicateCapturedChancePct is { } pct
            ? pct > 0
            : CurrentUsedDuplicatingScarab || _current.Any(x => IsDuplicatingScarabItemName(x?.ItemName));

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

        InferCostFromMapModifiers(reading);

        if (!_modifierLogged)
        {
            _modifierLogged = true;
            LogMapModifiers();
        }
    }

    // Rebuilds the scarab cost lines from the map's stats when the device was never read,
    // so a failed reading is not recorded as a free map. Only fills a genuinely empty
    // breakdown - a device reading that produced anything is the more detailed record.
    private void InferCostFromMapModifiers(MapModifierStats.Reading reading)
    {
        // The manual extra-cost line is added by the plugin, so it does not count as
        // evidence that the device itself was read.
        if (_current.Any(x => !string.Equals(x?.ItemName, ExtraManualLineName,
                                             StringComparison.OrdinalIgnoreCase))) return;

        var inferred = new List<MapCostItem>();
        AddInferred(inferred, HerdScarabName, reading.HerdScarabCount);
        if (reading.UsedDuplicatingScarab) AddInferred(inferred, DuplicatingScarabName, 1);

        if (inferred.Count == 0) return;

        _current.InsertRange(0, inferred);
        CurrentUsedDuplicatingScarab = CurrentUsedDuplicatingScarab || reading.UsedDuplicatingScarab;

        Log.Info($"Map cost inferred from map stats: {inferred.Count} line(s), " +
                 $"{inferred.Sum(x => x.UnitPriceChaos):0.#}c total - the map device was " +
                 "not read, so these are reconstructed rather than observed.");
    }

    private void AddInferred(List<MapCostItem> into, string itemName, int count)
    {
        if (count <= 0) return;

        _prices.TryGetItemPriceChaos(itemName, out var price);
        for (var i = 0; i < count; i++)
        {
            into.Add(new MapCostItem { ItemName = itemName, UnitPriceChaos = price, Inferred = true });
        }
    }

    // Logged separately from "Map cost armed", which is written at map start before these
    // stats are readable. The device reading is printed alongside because the gap is the
    // point: herd=2 from the map against 0 items from the device is a handled case.
    private void LogMapModifiers()
    {
        var herd = CurrentHerdScarabCount is { } h ? h.ToString() : "unknown";
        var reds = CurrentAdditionalRedBeasts is { } r ? r.ToString() : "unknown";
        var dup = CurrentDuplicateCapturedChancePct is { } d ? $"{d}%" : "unknown";

        var deviceHerd = _current.Count(x =>
            x?.ItemName?.IndexOf("Herd", StringComparison.OrdinalIgnoreCase) >= 0);

        // attempt=1 means the stats were ready on the first frame in the map, which is the
        // question that decides whether the retry below is earning its keep. If every map
        // reports 1, this could collapse to a single read in the area-change callback.
        Log.Info($"Map modifiers (from map stats): herd={herd} ({reds} additional reds), " +
                 $"duplicating={dup}, attempt={_modifierPolls} | map device had recorded " +
                 $"herd={deviceHerd}, {_current.Count} item(s)" +
                 (CurrentHerdScarabCount is { } mapHerd && mapHerd != deviceHerd
                     ? " <- DISAGREE, map stats win"
                     : string.Empty));
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
        CurrentUsedDuplicatingScarab = PreparedUsedDuplicatingScarab ||
                                       _current.Any(x => IsDuplicatingScarabItemName(x?.ItemName));
        _prepared.Clear();
        PreparedUsedDuplicatingScarab = false;

        // Cleared for the new map, then relatched by LatchMapModifiers once inside it.
        // Carrying the previous map's stats over would be worse than having none.
        CurrentAdditionalRedBeasts = null;
        CurrentDuplicateCapturedChancePct = null;
        _modifierPolls = 0;
        _lastModifierPollUtc = DateTime.MinValue;
        _modifierLogged = false;

        CurrentDeviceReadAgeMs = _lastReadUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (DateTime.UtcNow - _lastReadUtc).TotalMilliseconds);

        // What the DEVICE said, which is only the cost breakdown - not the last word on which
        // scarabs were loaded. The map's own stats follow in "Map modifiers" and win where they
        // disagree; kept separate because this runs before those stats exist.
        var age = CurrentDeviceReadAgeMs is { } ms ? $"{ms:0}ms" : "never read";
        Log.Info($"Map cost armed: dupScarab={CurrentUsedDuplicatingScarab}, " +
                 $"deviceReadAge={age}, {_current.Count} item(s)" +
                 (_current.Count > 0
                     ? $": {string.Join(", ", _current.Select(x => $"{x.ItemName} {x.UnitPriceChaos:0.#}c"))}"
                     : " - Map Device was not read; scarab counts will come from map stats."));
    }

    // Clears the current breakdown.
    public void ResetCurrent()
    {
        _current.Clear();
        CurrentUsedDuplicatingScarab = false;
        CurrentAdditionalRedBeasts = null;
        CurrentDuplicateCapturedChancePct = null;
        _modifierPolls = 0;
        _lastModifierPollUtc = DateTime.MinValue;
        _modifierLogged = false;
    }

    // Replaces the prepared breakdown and appends the manual extra-cost line.
    public void SetPrepared(IEnumerable<MapCostItem> items, bool? usedDuplicatingScarabOverride = null)
    {
        _prepared.Clear();
        _prepared.AddRange(CloneList(items));

        PreparedUsedDuplicatingScarab = usedDuplicatingScarabOverride ??
                                        _prepared.Any(x => IsDuplicatingScarabItemName(x?.ItemName));

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
        return true;
    }

    public static bool IsDuplicatingScarabItemName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.IndexOf(DuplicatingScarabName, StringComparison.OrdinalIgnoreCase) >= 0;

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
                Inferred = item.Inferred,
            });
        }
        return result;
    }
}
