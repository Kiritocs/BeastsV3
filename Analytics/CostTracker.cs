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
    private const string ExtraManualLineName = "Extra (Manual)";

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly PriceService _prices;

    private readonly List<MapCostItem> _prepared = new();
    private readonly List<MapCostItem> _current = new();
    private DateTime _lastPollUtc = DateTime.MinValue;

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

    // True when the current map duplicates captures, from either the latched flag or the
    // current breakdown.
    public bool CurrentMapUsesDuplicatingScarab =>
        CurrentUsedDuplicatingScarab || _current.Any(x => IsDuplicatingScarabItemName(x?.ItemName));

    // Total chaos cost of the current breakdown.
    public double ComputeCurrentCostChaos() => _current.Sum(x => x.UnitPriceChaos);

    // Returns a copy of the current breakdown.
    public MapCostItem[] SnapshotCurrent() => CloneList(_current).ToArray();

    // Re-reads the map device, throttled to Analytics.MapDeviceCapturePollIntervalMs.
    public void MaybePoll(DateTime nowUtc)
    {
        var intervalMs = Math.Max(50, _settings.Analytics.MapDeviceCapturePollIntervalMs.Value);
        if ((nowUtc - _lastPollUtc).TotalMilliseconds < intervalMs) return;
        _lastPollUtc = nowUtc;

        if (!TryReadMapDeviceItemNames(out var names)) return;
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

        // Records what the map started with; an empty breakdown means the device was never read.
        Log.Info($"Map cost armed: dupScarab={CurrentUsedDuplicatingScarab}, " +
                 $"{_current.Count} item(s)" +
                 (_current.Count > 0
                     ? $": {string.Join(", ", _current.Select(x => $"{x.ItemName} {x.UnitPriceChaos:0.#}c"))}"
                     : " - Map Device was not read before this map started."));
    }

    // Clears the current breakdown.
    public void ResetCurrent()
    {
        _current.Clear();
        CurrentUsedDuplicatingScarab = false;
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

    // True when the name is a Bestiary Scarab of Duplicating.
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
            result.Add(new MapCostItem { ItemName = item.ItemName, UnitPriceChaos = item.UnitPriceChaos });
        }
        return result;
    }
}
