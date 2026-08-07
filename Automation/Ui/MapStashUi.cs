using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using ImGuiNET;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Ui;

// Map-stash tier, page and search-bar navigation.
//
// Layout under the visible map stash root:
//   [0]     tier row 1: 9 cells, tiers I..IX
//   [1]     tier row 2: tiers X..XVI plus 2 non-tier cells
//   [3][0]  page-tab strip for the selected tier
//   [4]     map grid for the selected tier and page
// Each tier cell's [0] text is the number of maps held at that tier.
// The search bar belongs to the stash panel, not this subtree.
public sealed class MapStashUi
{
    private const int MinTier = 1;
    private const int MaxTier = 16;

    // Tier N sits at row (N-1)/9, cell (N-1)%9.
    private const int TierCellsPerRow = 9;

    private readonly GameController _game;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly StashUi _stash;

    public MapStashUi(GameController game, AutomationInput input, Waits waits, BeastsSettings settings,
        StashUi stash)
    {
        _game = game;
        _input = input;
        _waits = waits;
        _settings = settings;
        _stash = stash;
    }

    public bool IsMapStashVisible()
    {
        var visible = _game?.IngameState?.IngameUi?.StashElement?.VisibleStash;
        return visible != null && visible.InvType == InventoryType.MapStash;
    }

    // Root element of the visible map stash.
    public Element MapStashRoot => _stash?.VisibleTabRoot;

    public Element TryFindTierTab(int tier)
    {
        if (tier < MinTier || tier > MaxTier) return null;

        var root = MapStashRoot;
        if (root == null) return null;

        var cell = root
            .GetChildAtIndex((tier - 1) / TierCellsPerRow)?
            .GetChildAtIndex((tier - 1) % TierCellsPerRow);

        var rect = cell?.GetClientRect() ?? default;
        return rect.Width >= 12 && rect.Height >= 12 ? cell : null;
    }

    // Base name carried by every plain map of a tier.
    public static string TierItemName(int tier) => $"Map (Tier {tier})";

    // Map count reported by a tier cell, or -1 when unreadable.
    public int TierCount(int tier)
    {
        var text = TryFindTierTab(tier)?.GetChildAtIndex(0)?.Text?.Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : -1;
    }

    // True when the grid holds maps of that tier, matched by base name and ignoring the
    // search filter.
    public bool IsTierShowing(int tier) => HoldsItem(TierItemName(tier));

    private bool HoldsItem(string itemName, bool highlightedOnly = false)
    {
        var items = _game?.IngameState?.IngameUi?.StashElement?.VisibleStash?.VisibleInventoryItems;
        if (items == null || items.Count == 0 || string.IsNullOrWhiteSpace(itemName)) return false;

        return items.Any(i =>
            string.Equals(i?.Item?.GetComponent<Base>()?.Name, itemName, StringComparison.OrdinalIgnoreCase) &&
            (!highlightedOnly || StashUi.IsHighlighted(i)));
    }

    // ---- pages ---------------------------------------------------------

    // Paths to the page-tab strip and a page button's label.
    private static readonly int[] PageStripPath = { 3, 0 };
    private static readonly int[] PageLabelPath = { 0, 1 };

    // Page tabs of the selected tier, keyed by label since selection reorders the strip.
    public List<(string Label, Element Button)> Pages()
    {
        var result = new List<(string, Element)>();

        var strip = ImGuiEx.GetChildAt(MapStashRoot, PageStripPath);
        if (strip == null) return result;

        for (var i = 0; i < strip.ChildCount; i++)
        {
            var button = strip.GetChildAtIndex(i);
            var label = ImGuiEx.GetChildAt(button, PageLabelPath)?.Text?.Trim();
            if (string.IsNullOrEmpty(label)) continue;

            var rect = button.GetClientRect();
            if (rect.Width < 20 || rect.Height < 12) continue;

            result.Add((label, button));
        }

        result.Sort((a, b) => PageNumber(a.Item1).CompareTo(PageNumber(b.Item1)));
        return result;
    }

    private static int PageNumber(string label) =>
        int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

    // Clicks through unvisited pages until one holds itemName; false when all are tried.
    // With highlightedOnly, pages holding only filtered-out maps count as exhausted.
    public async Task<bool> TryAdvancePageAsync(string itemName, ISet<string> visitedPages, bool highlightedOnly = false)
    {
        var pages = Pages();
        if (pages.Count == 0) return false;

        var timing = _settings.Timing;
        foreach (var (label, button) in pages)
        {
            if (!visitedPages.Add(label)) continue;
            _input.ThrowIfStopRequested();

            var rect = button.GetClientRect();
            await _input.ClickAtAsync(
                rect, MouseButtons.Left,
                preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
                postDelayMs: Math.Max(timing.Clicks.UiClickPostDelayMs.Value, timing.Polling.TabSwitchDelayMs.Value));

            var found = await _waits.WaitForAsync(
                () => HoldsItem(itemName, highlightedOnly),
                timeoutMs: Math.Max(timing.Polling.VisibleTabTimeoutMs.Value, 600),
                pollDelayMs: timing.Polling.FastPollDelayMs.Value);

            if (found)
            {
                Log.Debug($"Map-stash page {label} holds more '{itemName}'{(highlightedOnly ? " matching the filter" : string.Empty)}.");
                return true;
            }
        }
        return false;
    }

    // ---- search bar ----------------------------------------------------

    // Path to the stash panel's search field, relative to StashElement.
    private static readonly int[] SearchBarPath = { 3, 1, 0 };

    public string SearchText =>
        ImGuiEx.GetChildAt(_game?.IngameState?.IngameUi?.StashElement, SearchBarPath)?.Text?.Trim();

    // Pastes regex into the stash search bar via Ctrl+F, Ctrl+A, Ctrl+V, Enter, then
    // verifies the committed text and throws on a mismatch.
    public async Task ApplySearchRegexAsync(string regex)
    {
        _input.ThrowIfStopRequested();
        if (string.IsNullOrWhiteSpace(regex))
            throw new InvalidOperationException("Map regex filter is on but the Map Regex Pattern is empty.");

        var wanted = regex.Trim();
        if (string.Equals(SearchText, wanted, StringComparison.Ordinal))
        {
            Log.Debug("Map-stash search bar already holds the configured regex. No paste needed.");
            return;
        }

        try { ImGui.SetClipboardText(wanted); }
        catch (Exception ex) { throw new InvalidOperationException($"Could not put the map regex on the clipboard: {ex.Message}", ex); }

        var timing = _settings.Timing;

        _input.PressKeyDown(Keys.LControlKey);
        try
        {
            await _input.TapKeyAsync(Keys.F, timing.Clicks.KeyTapDelayMs.Value, timing.Polling.FastPollDelayMs.Value);
            await _input.DelayForUiCheckAsync(timing.Polling.UiCheckInitialSettleDelayMs.Value);

            await _input.TapKeyAsync(Keys.A, timing.Clicks.KeyTapDelayMs.Value, timing.Polling.FastPollDelayMs.Value);
            await _input.TapKeyAsync(Keys.V, timing.Clicks.KeyTapDelayMs.Value, timing.Polling.FastPollDelayMs.Value);
        }
        finally
        {
            _input.PressKeyUp(Keys.LControlKey);
        }

        await _input.DelayForUiCheckAsync(timing.Polling.UiCheckInitialSettleDelayMs.Value);
        await _input.TapKeyAsync(Keys.Enter, timing.Clicks.KeyTapDelayMs.Value, timing.Polling.FastPollDelayMs.Value);

        // Waits for the committed text, which the highlight repaint trails.
        var landed = await _waits.WaitForAsync(
            () => string.Equals(SearchText, wanted, StringComparison.Ordinal),
            timeoutMs: Math.Max(timing.Polling.VisibleTabTimeoutMs.Value, 1000),
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        if (!landed)
            throw new InvalidOperationException(
                $"Map regex never landed in the stash search bar. It reads '{SearchText}', expected '{wanted}'. " +
                "Check that your PoE search keybind is Ctrl+F and that the clipboard is not locked by another app.");

        await _input.DelayForUiCheckAsync(Math.Max(timing.Polling.UiCheckInitialSettleDelayMs.Value, 100));

        Log.Debug($"Map-stash search filtered by '{wanted}'.");
    }

    // Clicks the tier cell and confirms its grid is showing, falling back to a page hunt.
    // visitedPages is shared with the caller's pull loop.
    public async Task EnsureTierSelectedAsync(int tier, ISet<string> visitedPages = null, bool highlightedOnly = false)
    {
        if (!IsMapStashVisible())
            throw new InvalidOperationException("Map stash isn't the visible stash. Switch to your map stash tab first.");

        var timing = _settings.Timing;

        // Waits for the tier grid to render; reads before that are unreliable.
        var readable = await _waits.WaitForAsync(
            () => TierCount(tier) >= 0,
            timeoutMs: Math.Max(timing.Polling.VisibleTabTimeoutMs.Value, 2000),
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        var held = TierCount(tier);
        if (!readable)
            throw new InvalidOperationException(
                $"Map-stash tier grid never became readable (tier {tier} count reads {held}). The stash may still have been loading.");

        if (held == 0)
            throw new InvalidOperationException($"Map stash holds no tier {tier} maps.");

        if (IsTierShowing(tier))
        {
            Log.Debug($"Map-stash tier {tier} already showing ({held} held). No click needed.");
            return;
        }

        var tab = TryFindTierTab(tier);
        if (tab == null)
        {
            LogTierGrid(tier);
            throw new InvalidOperationException(
                $"Could not find the map-stash tier {tier} cell. Turn on Diagnostics: Verbose Logging and re-run - the log dumps the tier grid it did find.");
        }

        var rect = tab.GetClientRect();

        await _input.ClickAtAsync(
            rect,
            MouseButtons.Left,
            preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
            postDelayMs: Math.Max(timing.Clicks.UiClickPostDelayMs.Value, timing.Polling.TabSwitchDelayMs.Value));

        var selected = await _waits.WaitForAsync(
            () => IsTierShowing(tier),
            timeoutMs: Math.Max(timing.Polling.VisibleTabTimeoutMs.Value, 1500),
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        if (selected) return;

        // The tier may be selected on a page holding none of its maps; try the others.
        if (await TryAdvancePageAsync(TierItemName(tier), visitedPages ?? new HashSet<string>(StringComparer.Ordinal), highlightedOnly))
            return;

        // No page held the tier's maps; with a filter on, none of them matched it.
        throw new InvalidOperationException(highlightedOnly
            ? $"Clicked the map-stash tier {tier} cell at ({rect.Center.X:0}, {rect.Center.Y:0}) and checked every page, " +
              $"but no tier {tier} map matched the map regex (the tier cell reports {held} held in total). Check the pattern."
            : $"Clicked the map-stash tier {tier} cell at ({rect.Center.X:0}, {rect.Center.Y:0}) and checked every page, " +
              $"but none showed tier {tier} maps (the tier cell reports {held}).");
    }

    // Logs the tier grid's structure when a tier cell can't be found.
    private void LogTierGrid(int tier)
    {
        var root = MapStashRoot;
        if (root == null)
        {
            Log.Debug($"Map-stash tier {tier}: could not resolve the visible stash tab's root.");
            return;
        }

        Log.Debug($"Map-stash tier {tier} lookup failed. Root has {root.ChildCount} children:");
        var rowsToDump = (int)Math.Min(2L, root.ChildCount);
        for (var row = 0; row < rowsToDump; row++)
        {
            var rowElement = root.GetChildAtIndex(row);
            if (rowElement == null) continue;

            var cells = Enumerable.Range(0, (int)rowElement.ChildCount).Select(i =>
            {
                var cell = rowElement.GetChildAtIndex(i);
                var r = cell?.GetClientRect() ?? default;
                return $"[{i}] count='{cell?.GetChildAtIndex(0)?.Text ?? "?"}' {r.Width:0}x{r.Height:0}";
            });
            Log.Debug($"  row {row} ({rowElement.ChildCount} cells): {string.Join(" ", cells)}");
        }
    }
}
