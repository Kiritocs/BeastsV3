using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Workflows;

// Moves itemized captured-monster items into their configured stash tabs, with an
// optional separate tab for red beasts.
public sealed class CapturedMonsterStash
{
    // How long the inventory count must hold steady before a batch counts as settled.
    private const int SettleAfterChangeMs = 200;

    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly StashUi _stash;
    private readonly InventoryUi _inventory;
    private readonly UiCleanup _uiCleanup;
    private readonly Action<string> _updateStatus;

    // Catalog beast names and metadata patterns, for the red-beast check.
    private static readonly HashSet<string> KnownRedNames = new(
        BeastCatalog.All.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KnownRedMetadataPrefixes = new(
        BeastCatalog.All.SelectMany(b => b.MetadataPatterns), StringComparer.OrdinalIgnoreCase);

    public CapturedMonsterStash(AutomationInput input, Waits waits, BeastsSettings settings,
        StashUi stash, InventoryUi inventory, UiCleanup uiCleanup, Action<string> updateStatus)
    {
        _input = input;
        _waits = waits;
        _settings = settings;
        _stash = stash;
        _inventory = inventory;
        _uiCleanup = uiCleanup;
        _updateStatus = updateStatus;
    }

    // Moves every captured-monster item to its stash tab and returns the count moved.
    // Throws when the stash can't be opened, a tab isn't configured, or transfer stalls.
    public async Task<int> StashAllAsync(CancellationToken ct)
    {
        var pending = _inventory.VisibleCapturedMonsters();
        if (pending.Count == 0) return 0;

        await EnsureStashOpenAsync();

        // Ordered destination chains. Each is walked in turn as tabs fill up.
        var normal = new TabChain(ResolveTabChain(_settings.BestiaryAutomation.ItemizedBeastTabs, "Itemized Beasts"), "Itemized Beasts");
        if (normal.Tabs.Count == 0)
            throw new InvalidOperationException(
                "Set Automation: Bestiary -> Itemized Beasts Stash Tabs before enabling auto-stash.");

        // Empty means red beasts share the normal chain, matching the old optional tab.
        var red = new TabChain(ResolveTabChain(_settings.BestiaryAutomation.RedBeastTabs, "Red Beasts"), "Red Beasts");

        _updateStatus?.Invoke("Stashing itemized beasts...");

        var moved = 0;
        var currentTabIndex = int.MinValue;
        var stallCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!_stash.IsVisible)
                throw new InvalidOperationException("Stash closed while auto-stashing itemized beasts.");

            var items = _inventory.VisibleCapturedMonsters();
            if (items.Count == 0) return moved;

            // Items are batched per destination tab, one tab switch each; which tab depends on how
            // far each chain has advanced, so it is resolved per iteration.
            var useRed = red.Tabs.Count > 0 && IsRedBeast(items[0]);
            var chain = useRed ? red : normal;
            var targetTabIndex = chain.Current;

            var batch = items
                .Where(item => (red.Tabs.Count > 0 && IsRedBeast(item)) == useRed)
                .ToList();

            if (currentTabIndex != targetTabIndex)
            {
                await _stash.SelectTabAsync(targetTabIndex);
                await _input.DelayAsync(_settings.Timing.Polling.TabSwitchDelayMs.Value);
                currentTabIndex = targetTabIndex;
            }

            // Beast items are 1x1, so free cells equals how many more fit. -1 means the grid could
            // not be read; the stall detector handles that.
            var freeCells = _stash.VisibleTabFreeCellCount();
            if (freeCells == 0)
            {
                // Full tab: step to the next in this chain and retry rather than failing.
                if (chain.HasNext)
                {
                    Log.Info($"Stash tab '{_stash.TabNameAt(chain.Current)}' is full. " +
                             $"Moving to the next {chain.Label} tab '{_stash.TabNameAt(chain.Next)}'.");

                    chain.Advance();
                    currentTabIndex = int.MinValue;
                    continue;
                }

                throw new InvalidOperationException(
                    $"Every configured {chain.Label} stash tab is full ({string.Join(", ", chain.Tabs.Select(_stash.TabNameAt))}) - " +
                    $"{items.Count} itemized beast{ImGuiEx.PluralSuffix(items.Count)} still in inventory (moved {moved} so far). " +
                    $"Free up space, or add another tab under Automation: Bestiary -> {chain.Label} Stash Tabs.");
            }

            if (freeCells > 0 && batch.Count > freeCells)
            {
                Log.Debug($"Stash tab '{_stash.TabNameAt(targetTabIndex)}' has {freeCells} free cell(s) for {batch.Count} beast(s) - filling it, then stopping.");
                batch = batch.Take(freeCells).ToList();
            }

            var previousCount = items.Count;
            var clicked = await CtrlClickBatchAsync(batch, ct);

            if (clicked == 0)
            {
                if (++stallCount >= 3)
                    throw new InvalidOperationException("Auto-stash stalled - no inventory item could be clicked.");
                await _input.DelayAsync(_settings.Timing.Polling.FastPollDelayMs.Value);
                continue;
            }

            var afterCount = await WaitForStashedAsync(previousCount, clicked, ct);

            if (afterCount >= previousCount)
            {
                stallCount++;
                if (stallCount >= 3)
                    throw new InvalidOperationException("Auto-stash stalled while moving beasts into the stash.");
                await _input.DelayAsync(_settings.Timing.Polling.FastPollDelayMs.Value);
                continue;
            }

            stallCount = 0;
            moved += previousCount - afterCount;
        }
    }

    // Opens the stash if it is not already up, walking to it when needed.
    private async Task EnsureStashOpenAsync()
    {
        if (_stash.IsVisible) return;

        // Closes open panels so the walk and stash clicks reach the world.
        if (_uiCleanup != null)
            await _uiCleanup.PrepareAsync("walking to stash", new UiCleanupOptions());

        _updateStatus?.Invoke("Walking to stash...");
        if (!await _stash.EnsureOpenAsync())
            throw new InvalidOperationException(
                "Auto-stash could not reach a stash in this area. Move next to your stash and re-run, or disable Auto-Stash After Itemize.");
    }

    // A configured destination chain: the tabs to fill, in order, and how far along we are.
    // A tab found full is never revisited, so the stash loop cannot ping-pong between a full
    // tab and its successor.
    private sealed class TabChain
    {
        public TabChain(List<int> tabs, string label)
        {
            Tabs = tabs;
            Label = label;
        }

        public List<int> Tabs { get; }
        public string Label { get; }
        public int Cursor { get; private set; }

        public int Current => Tabs[Cursor];
        public bool HasNext => Cursor + 1 < Tabs.Count;
        public int Next => Tabs[Cursor + 1];
        public void Advance() => Cursor++;
    }

    // Turns configured tab names into stash indices, in order. A name that no longer matches
    // is skipped with a warning rather than failing the run - renaming one tab of three should
    // cost that destination, not the whole auto-stash.
    private List<int> ResolveTabChain(List<string> configured, string label)
    {
        var resolved = new List<int>();
        if (configured == null) return resolved;

        foreach (var name in configured)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var index = _stash.ResolveTabIndex(trimmed);
            if (index < 0)
            {
                Log.Warn($"{label} stash tab '{trimmed}' was not found and will be skipped.");
                continue;
            }

            // Listing a tab twice would waste an overflow step on an already-full tab.
            if (!resolved.Contains(index)) resolved.Add(index);
        }

        return resolved;
    }

    // Ctrl-clicks every item in the batch with Ctrl held throughout.
    private async Task<int> CtrlClickBatchAsync(List<NormalInventoryItem> batch, CancellationToken ct)
    {
        var timing = _settings.Timing;

        _input.ReleaseKeys(Keys.LControlKey, Keys.RControlKey);
        _input.PressKeyDown(Keys.LControlKey);

        var clicked = 0;
        try
        {
            foreach (var item in batch)
            {
                ct.ThrowIfCancellationRequested();

                var rect = item.GetClientRect();
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                await _input.ClickAtAsync(
                    rect,
                    MouseButtons.Left,
                    preDelayMs: timing.Clicks.CtrlClickPreDelayMs.Value,
                    postDelayMs: timing.Clicks.CtrlClickPostDelayMs.Value);
                clicked++;
            }
        }
        finally
        {
            _input.PressKeyUp(Keys.LControlKey);
        }

        return clicked;
    }

    // Waits for the batch to leave inventory and returns the resulting item count.
    private async Task<int> WaitForStashedAsync(int previousCount, int clicked, CancellationToken ct)
    {
        var timing = _settings.Timing;
        var baseTimeout = Math.Max(
            timing.Polling.QuantityChangeBaseDelayMs.Value,
            _input.ClickPostDelayFloor() + timing.Polling.QuantityChangeBaseDelayMs.Value);
        var timeout = _input.ScaleTimeout(baseTimeout + Math.Max(0, clicked - 1) * Math.Max(50, baseTimeout / 2));
        var pollDelay = Math.Max(1, timing.Polling.FastPollDelayMs.Value);

        var sw = Stopwatch.StartNew();
        var best = previousCount;
        long? lastChangeMs = null;

        while (sw.ElapsedMilliseconds < timeout)
        {
            ct.ThrowIfCancellationRequested();

            var current = _inventory.VisibleCapturedMonsters().Count;
            if (current < best)
            {
                best = current;
                lastChangeMs = sw.ElapsedMilliseconds;
            }

            if (best <= previousCount - clicked) break;
            if (lastChangeMs.HasValue && sw.ElapsedMilliseconds - lastChangeMs.Value >= SettleAfterChangeMs) break;

            await _input.DelayAsync(pollDelay);
        }

        return best;
    }

    // True when the item's beast is in BeastCatalog.
    private static bool IsRedBeast(NormalInventoryItem item)
    {
        var monster = item?.Item?.GetComponent<CapturedMonster>();
        var variety = monster?.MonsterVariety;

        var monsterName = variety?.GetType().GetProperty("MonsterName")?.GetValue(variety) as string;
        if (!string.IsNullOrWhiteSpace(monsterName) && KnownRedNames.Contains(monsterName.Trim()))
            return true;
        var name = variety?.GetType().GetProperty("Name")?.GetValue(variety) as string;
        if (!string.IsNullOrWhiteSpace(name) && KnownRedNames.Contains(name.Trim()))
            return true;

        var baseName = item?.Item?.GetComponent<Base>()?.Name;
        if (!string.IsNullOrWhiteSpace(baseName) && KnownRedNames.Contains(baseName.Trim()))
            return true;

        var metadata = item?.Item?.Metadata;
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            foreach (var prefix in KnownRedMetadataPrefixes)
            {
                if (metadata.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
