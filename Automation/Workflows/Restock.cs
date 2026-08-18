using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Workflows;

// Pulls configured stash items into inventory until each target quantity is met.
//
// Per enabled target, deduped by item name:
//   1. Skip when inventory already holds the cumulative requested quantity.
//   2. Select the target's stash tab.
//   3. For "Map (Tier N)" targets in a map stash, apply the regex filter and select the tier.
//   4. Ctrl-click matching items until the quantity is met or the stash runs out.
public sealed class Restock
{
    // Ceiling on how long the split prompt may take to appear.
    private const int SplitDialogTimeoutMs = 700;

    // Ceiling on how long a placed split may take to show up in the inventory.
    private const int SplitPlacementTimeoutMs = 2000;

    // A placement click can miss when its cell was filled meanwhile, so the cursor is re-aimed
    // at a freshly read free cell before giving up.
    private const int PlacementAttempts = 2;

    private readonly Runner _runner;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly StashUi _stash;
    private readonly MapStashUi _mapStash;
    private readonly InventoryUi _inventory;
    private readonly BatchClicker _batch;

    public Restock(Runner runner, AutomationInput input, Waits waits, BeastsSettings settings,
        StashUi stash, MapStashUi mapStash, InventoryUi inventory)
    {
        _runner = runner;
        _input = input;
        _waits = waits;
        _settings = settings;
        _stash = stash;
        _mapStash = mapStash;
        _inventory = inventory;
        _batch = new BatchClicker(input, settings);
    }

    public Task RunAsync() =>
        _runner.QueueAsync(
            ct => RunBodyAsync(ct),
            failureLabel: "Restock",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepStash = true, KeepInventory = true },
            cancelledStatus: "Restock cancelled.");

    // Restocks every enabled target. heldElsewhere credits stock held outside inventory,
    // such as the map device slots.
    public async Task RunBodyAsync(CancellationToken ct, IReadOnlyDictionary<string, int> heldElsewhere = null)
    {
        if (!_stash.IsVisible)
        {
            _runner.UpdateStatus("Opening stash...");
            if (!await _stash.EnsureOpenAsync())
                throw new InvalidOperationException("Could not open the stash. Move next to it or open it manually.");
        }

        var targets = EnabledTargets();
        if (targets.Count == 0)
        {
            _runner.UpdateStatus("Restock: no enabled targets with a quantity and item name.");
            return;
        }

        PreflightInventoryCapacity(targets, heldElsewhere);

        _runner.UpdateStatus("Restocking inventory...");
        var totalTransferred = 0;
        foreach (var target in DedupeByIdentity(targets))
        {
            ct.ThrowIfCancellationRequested();
            totalTransferred += await RestockTargetAsync(target, ct, heldElsewhere);
        }

        _runner.UpdateStatus($"Restock complete. Transferred {totalTransferred} item{ImGuiEx.PluralSuffix(totalTransferred)}.");
    }

    // ---- per-target ----------------------------------------------------

    // Pulls one target from the stash and returns the quantity moved.
    private async Task<int> RestockTargetAsync(TargetInfo target, CancellationToken ct,
        IReadOnlyDictionary<string, int> heldElsewhere)
    {
        var itemName = target.ItemName;
        var requested = RequestedFromStash(target, heldElsewhere);
        if (requested <= 0)
        {
            Log.Debug($"Restock '{itemName}': already covered by stock held outside inventory. Skipping.");
            return 0;
        }

        var inventoryBefore = InventoryQuantity(itemName);
        if (inventoryBefore >= requested)
        {
            Log.Debug($"Restock '{itemName}': inventory already has {inventoryBefore}/{requested}. Skipping.");
            return 0;
        }

        var tabIndex = _stash.ResolveTabIndex(target.StashTabName);
        if (tabIndex < 0)
        {
            throw new InvalidOperationException(
                $"Restock target '{itemName}': stash tab '{TabPin.DisplayName(target.StashTabName)}' not found. Set the exact tab name.");
        }

        _runner.UpdateStatus($"Loading {itemName} ({inventoryBefore}/{requested})...");
        await _stash.SelectTabAsync(tabIndex);
        await _input.DelayAsync(_settings.Timing.Polling.TabSwitchDelayMs.Value);

        // Shared with the pull loop so tier selection and draining agree on visited pages.
        var visitedPages = new HashSet<string>(StringComparer.Ordinal);

        // Map-tier targets in a map stash route through tier and page selection.
        var isMapTier = TryParseMapTier(itemName, out var mapTier);

        // Waited for, not sampled: the stash reports its visible tab a beat after it opens or
        // switches, so reading it here once said "not a map stash", skipped tier selection
        // entirely, and then looked for the maps in whichever section the stash was left on.
        var inMapStash = isMapTier && await _waits.WaitForAsync(
            _mapStash.IsMapStashVisible,
            timeoutMs: Math.Max(_settings.Timing.Polling.VisibleTabTimeoutMs.Value, 1000),
            pollDelayMs: _settings.Timing.Polling.FastPollDelayMs.Value);

        if (isMapTier && !inMapStash)
        {
            Log.Debug($"Restock '{itemName}': tab '{TabPin.DisplayName(target.StashTabName)}' never reported as a " +
                      "map stash, so tier selection was skipped and the target is matched by name alone.");
        }

        // The regex filter applies to map stashes only.
        var filterByRegex = inMapStash && _settings.Restock.EnableMapRegexFilter.Value;
        if (filterByRegex)
        {
            var pattern = _settings.Restock.MapRegexPattern.Value?.Trim();
            if (string.IsNullOrWhiteSpace(pattern))
            {
                // Fatal: running unfiltered would pull the wrong maps.
                throw new InvalidOperationException(
                    "Map regex filter is enabled but Map Regex Pattern is empty. Set a pattern or turn the filter off.");
            }

            _runner.UpdateStatus($"Filtering map stash by regex for {itemName}...");
            await _mapStash.ApplySearchRegexAsync(pattern);
        }

        if (inMapStash)
        {
            _runner.UpdateStatus($"Selecting map-stash tier {mapTier}...");
            await _mapStash.EnsureTierSelectedAsync(mapTier, visitedPages, filterByRegex);
        }

        // Waits for the tab's contents or sub-tab strip to populate before reading it.
        var polling = _settings.Timing.Polling;
        await _waits.WaitForAsync(
            () => _stash.CountMatchingQuantity(itemName, filterByRegex) > 0 || _stash.SubTabs().Count > 0,
            timeoutMs: Math.Max(polling.VisibleTabTimeoutMs.Value, 1000),
            pollDelayMs: polling.FastPollDelayMs.Value);

        var available = _stash.CountMatchingQuantity(itemName, filterByRegex);
        if (available <= 0)
        {
            // Fragment stashes split contents across sub-tabs; try the others.
            if (await _stash.TrySelectSubTabWithAsync(itemName))
                available = _stash.CountMatchingQuantity(itemName, filterByRegex);
        }

        if (available <= 0)
        {
            var subTabs = _stash.SubTabs();
            var where = subTabs.Count > 0
                ? $"stash tab '{TabPin.DisplayName(target.StashTabName)}' (checked sub-tabs: {string.Join(", ", subTabs.Select(s => s.Name))})"
                : $"stash tab '{TabPin.DisplayName(target.StashTabName)}'";
            throw new InvalidOperationException(filterByRegex
                ? $"No '{itemName}' in {where} matched the map regex. Check the pattern, or turn the filter off."
                : $"No '{itemName}' found in {where}.");
        }

        var transferred = 0;
        var stallCount = 0;
        var timing = _settings.Timing;

        while (InventoryQuantity(itemName) < requested)
        {
            ct.ThrowIfCancellationRequested();

            if (_inventory.FreeCellCount() <= 0)
            {
                _runner.UpdateStatus($"Inventory full while loading {itemName}. Loaded {transferred}.");
                break;
            }

            var beforeQty = InventoryQuantity(itemName);
            var stillNeeded = requested - beforeQty;

            // Cells whose whole stack fits inside what is still needed, plus at most one
            // trailing cell that would overshoot.
            var batch = CollectPullBatch(itemName, filterByRegex, stillNeeded,
                _inventory.FreeCellCount(), out var overshooting);

            if (batch.Count == 0 && overshooting == null)
            {
                // A tier splits across pages; try the next one holding matches.
                if (isMapTier && _mapStash.IsMapStashVisible() &&
                    await _mapStash.TryAdvancePageAsync(itemName, visitedPages, filterByRegex))
                    continue;

                var loaded = InventoryQuantity(itemName);
                if (loaded < requested)
                {
                    _runner.UpdateStatus(filterByRegex
                        ? $"Loaded {itemName}: {loaded}/{requested}. No more maps match the regex."
                        : $"Loaded {itemName}: {loaded}/{requested}. Stash ran out.");
                }
                break;
            }

            // What this pass should add. A pass can fall short of stillNeeded (free cells or stash
            // run out), so the settle waits for what was actually clicked.
            var expectedGain = await SendPullPassAsync(itemName, batch, overshooting, stillNeeded, ct);
            if (expectedGain == null)
            {
                if (++stallCount >= 3)
                    throw new InvalidOperationException(
                        $"Restock of '{itemName}' stalled - no stash cell could be clicked after transferring {transferred}.");
                await _input.DelayAsync(timing.Polling.FastPollDelayMs.Value);
                continue;
            }

            // One confirmation for the whole pass, stopping as soon as it lands or the count goes
            // quiet - which is what makes batching cheaper than a serial pass.
            var expected = Math.Min(requested, beforeQty + expectedGain.Value);
            var afterQty = await _batch.SettleAsync(
                () => InventoryQuantity(itemName),
                qty => qty >= expected,
                _batch.BatchTimeoutMs(
                    Math.Max(timing.Polling.QuantityChangeBaseDelayMs.Value,
                             _input.ClickPostDelayFloor() + timing.Polling.QuantityChangeBaseDelayMs.Value),
                    Math.Max(1, batch.Count)),
                ct);

            if (afterQty <= beforeQty)
            {
                stallCount++;
                if (stallCount >= 3)
                    throw new InvalidOperationException($"Restock of '{itemName}' stalled after transferring {transferred}.");
                await _input.DelayAsync(timing.Polling.FastPollDelayMs.Value);
                continue;
            }

            stallCount = 0;
            transferred += afterQty - beforeQty;
            _runner.UpdateStatus($"Loading {itemName} ({InventoryQuantity(itemName)}/{requested})...");
        }

        return transferred;
    }

    // Cells to ctrl-click in one pass, in stash reading order. Each ctrl-click moves a whole
    // stack, so cells are taken while the running total still fits. The first cell that would
    // overshoot comes back as `overshooting` instead - only the split prompt can take part of
    // a stack, so at most one split runs per target, last. With batching off, at most one cell
    // comes back.
    private List<NormalInventoryItem> CollectPullBatch(
        string itemName, bool filterByRegex, int stillNeeded, int freeCells,
        out NormalInventoryItem overshooting)
    {
        overshooting = null;
        var batch = new List<NormalInventoryItem>();
        if (stillNeeded <= 0) return batch;

        var running = 0;
        foreach (var candidate in _stash.FindAllMatching(itemName, filterByRegex))
        {
            if (running >= stillNeeded) break;

            // Worst case each click lands in its own cell; merging stacks free it up again, so the
            // cap only ever under-fills a pass.
            if (batch.Count >= freeCells) break;

            var moveable = MoveableByCtrlClick(candidate);
            if (running + moveable > stillNeeded)
            {
                overshooting = candidate;
                break;
            }

            batch.Add(candidate);
            running += moveable;

            if (!_batch.Enabled) break;
        }

        return batch;
    }

    // Quantity one ctrl-click moves: the slot's contents capped at one full stack.
    private static int MoveableByCtrlClick(NormalInventoryItem item)
    {
        var stack = item?.Item?.GetComponent<ExileCore.PoEMemory.Components.Stack>();
        if (stack == null) return 1;

        var size = Math.Max(1, stack.Size);
        var maxStackSize = stack.Info?.MaxStackSize ?? 0;
        return maxStackSize > 0 ? Math.Min(size, maxStackSize) : size;
    }

    // Takes exactly `quantity` from a stack via the split prompt, then drops it in a free
    // cell. Falls back to a whole-stack ctrl-click if the prompt never opens.
    private async Task TransferPartialStackAsync(NormalInventoryItem item, int quantity, string itemName)
    {
        var beforeQty = InventoryQuantity(itemName);

        // Preflight only: the cell used is re-read after the prompt closes, as it can be taken.
        if (!_inventory.TryGetFreeCellCenter(out _))
            throw new InvalidOperationException($"No free inventory cell to place a partial stack of '{itemName}'.");

        if (!await TryOpenSplitPromptAsync(item, itemName))
        {
            // Usual cause is a loaded cursor - the game will not open a split prompt while one is held.
            Log.Debug($"Split prompt did not open for '{itemName}' - check nothing is stuck on the cursor. " +
                      "Falling back to a whole-stack ctrl-click.");
            await _input.CtrlClickAtAsync(item.GetClientRect());
            return;
        }

        await CommitSplitQuantityAsync(quantity, itemName);
        await PlaceSplitStackAsync(quantity, itemName, beforeQty);
    }

    // Shift-clicks the stack and waits for the quantity prompt.
    private async Task<bool> TryOpenSplitPromptAsync(NormalInventoryItem item, string itemName)
    {
        var timing = _settings.Timing;

        await _input.ClickAtAsync(
            item.GetClientRect(), MouseButtons.Left,
            preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
            postDelayMs: timing.Clicks.UiClickPostDelayMs.Value,
            modifiers: new[] { Keys.LShiftKey });

        return await _waits.WaitForAsync(
            () => _inventory.IsStackSplitDialogVisible,
            timeoutMs: SplitDialogTimeoutMs,
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);
    }

    // Types the amount, verifies the field reads it back, then commits with Enter.
    private async Task CommitSplitQuantityAsync(int quantity, string itemName)
    {
        var timing = _settings.Timing;
        var wanted = quantity.ToString(CultureInfo.InvariantCulture);

        // Verifies the typed amount before committing it.
        var typed = false;
        for (var attempt = 1; attempt <= 2 && !typed; attempt++)
        {
            await _input.TypeDigitsAsync(wanted);
            typed = await _waits.WaitForAsync(
                () => string.Equals(_inventory.StackSplitQuantityText, wanted, StringComparison.Ordinal),
                timeoutMs: SplitDialogTimeoutMs,
                pollDelayMs: timing.Polling.FastPollDelayMs.Value);

            if (!typed)
                Log.Debug($"Split quantity attempt {attempt}: field reads '{_inventory.StackSplitQuantityText}', wanted '{wanted}'.");
        }

        if (!typed)
            throw new InvalidOperationException(
                $"Could not enter {quantity} into the split prompt for '{itemName}'. Field reads '{_inventory.StackSplitQuantityText}'.");

        await _input.TapKeyAsync(Keys.Enter, timing.Clicks.KeyTapDelayMs.Value, timing.Polling.FastPollDelayMs.Value);

        var closed = await _waits.WaitForAsync(
            () => !_inventory.IsStackSplitDialogVisible,
            timeoutMs: SplitDialogTimeoutMs,
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        if (!closed)
            throw new InvalidOperationException($"Split prompt for '{itemName}' never closed after entering {quantity}.");
    }

    // Drops the cursor-held split into a free cell, re-read per attempt: clicking a cell that has
    // since been filled swaps the two stacks instead of placing this one.
    private async Task PlaceSplitStackAsync(int quantity, string itemName, int beforeQty)
    {
        var timing = _settings.Timing;

        // Checks whether the split already landed, since clicking again would pick it up.
        if (await _waits.WaitForAsync(
                () => InventoryQuantity(itemName) >= beforeQty + quantity,
                timeoutMs: Math.Max(150, timing.Polling.FastPollDelayMs.Value * 3),
                pollDelayMs: timing.Polling.FastPollDelayMs.Value))
        {
            Log.Debug($"Split {quantity} of '{itemName}' went straight into the inventory - no placement click needed.");
            return;
        }

        for (var attempt = 1; attempt <= PlacementAttempts; attempt++)
        {
            if (!_inventory.TryGetFreeCellCenter(out var landing))
                throw new InvalidOperationException(
                    $"Split {quantity} of '{itemName}' but no free inventory cell is left to place it in. " +
                    "The stack is on the cursor - put it back by hand before re-running.");

            await _input.ClickAtAsync(
                landing, MouseButtons.Left,
                preDelayMs: Math.Max(timing.Clicks.UiClickPreDelayMs.Value, 100),
                postDelayMs: timing.Clicks.CtrlClickPostDelayMs.Value);

            if (await _waits.WaitForAsync(
                    () => InventoryQuantity(itemName) >= beforeQty + quantity,
                    timeoutMs: SplitPlacementTimeoutMs,
                    pollDelayMs: timing.Polling.FastPollDelayMs.Value))
            {
                Log.Debug($"Split {quantity} of '{itemName}' into inventory at ({landing.X:0}, {landing.Y:0}).");
                return;
            }

            Log.Debug($"Split of '{itemName}' did not land at ({landing.X:0}, {landing.Y:0}) " +
                      $"(attempt {attempt}/{PlacementAttempts}); inventory reads {InventoryQuantity(itemName)}, was {beforeQty}.");
        }

        throw new InvalidOperationException(
            $"Split {quantity} of '{itemName}' but the inventory reads {InventoryQuantity(itemName)} (was {beforeQty}) " +
            $"after {PlacementAttempts} placement attempts. The stack is probably still on the cursor - " +
            "put it back by hand before re-running.");
    }

    // Sends one transfer pass and reports the quantity it should add, or null when no stash cell
    // could be clicked. The overshooting cell goes last, so its prompt sees a settled inventory.
    private async Task<int?> SendPullPassAsync(
        string itemName, List<NormalInventoryItem> batch, NormalInventoryItem overshooting,
        int stillNeeded, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            await TransferPartialStackAsync(overshooting, stillNeeded, itemName);
            return stillNeeded;
        }

        var expectedGain = batch.Sum(MoveableByCtrlClick);
        var clicked = await _batch.CtrlClickAllAsync(
            BatchClicker.CellCenters(batch), ct, guard: () => _stash.IsVisible);

        return clicked == 0 ? null : expectedGain;
    }


    // ---- preflight ----------------------------------------------------

    // Quantity of a target still to pull from the stash, after crediting heldElsewhere.
    private static int RequestedFromStash(TargetInfo target, IReadOnlyDictionary<string, int> heldElsewhere)
    {
        var held = heldElsewhere != null && heldElsewhere.TryGetValue(target.ItemName, out var amount) ? amount : 0;
        return Math.Max(0, target.CumulativeQuantity - held);
    }

    private void PreflightInventoryCapacity(List<TargetInfo> targets, IReadOnlyDictionary<string, int> heldElsewhere)
    {
        _runner.UpdateStatus("Checking inventory capacity...");
        var free = _inventory.FreeCellCount();
        if (free <= 0)
            throw new InvalidOperationException("Restock preflight failed. Inventory is full.");

        var requiredCells = 0;
        foreach (var target in DedupeByIdentity(targets))
        {
            var have = InventoryQuantity(target.ItemName);
            var need = Math.Max(0, RequestedFromStash(target, heldElsewhere) - have);
            if (need <= 0) continue;

            requiredCells += CellsNeededFor(target.ItemName, need);
        }

        if (requiredCells > free)
        {
            throw new InvalidOperationException(
                $"Restock preflight failed. Needs ~{requiredCells} free inventory slot{ImGuiEx.PluralSuffix(requiredCells)}, only {free} free.");
        }
    }

    // Cells a quantity of an item will occupy. Maps take a cell each; stackables fill to
    // their stack size before spilling, so a request above one stack costs more than one cell.
    private int CellsNeededFor(string itemName, int quantity)
    {
        if (quantity <= 0) return 0;
        if (TryParseMapTier(itemName, out _)) return quantity;

        var maxStackSize = MaxStackSizeFor(itemName);
        return maxStackSize > 0 ? (int)Math.Ceiling(quantity / (double)maxStackSize) : 1;
    }

    // Max stack size for an item, read off a visible copy. The preflight runs before any tab
    // is selected, so 0 means "unknown" and callers fall back to a single-cell estimate.
    private int MaxStackSizeFor(string itemName)
    {
        var fromStash = _stash.FindNextMatching(itemName)?.Item?.GetComponent<Stack>()?.Info?.MaxStackSize ?? 0;
        if (fromStash > 0) return fromStash;

        var fromInventory = _inventory.VisibleItems?
            .FirstOrDefault(item => StashUi.Matches(item, itemName));
        return Math.Max(0, fromInventory?.Item?.GetComponent<Stack>()?.Info?.MaxStackSize ?? 0);
    }

    // ---- target enumeration -------------------------------------------

    // Enabled restock targets from settings.
    private List<TargetInfo> EnabledTargets()
    {
        var r = _settings.Restock;
        var raw = new[] { r.Target1, r.Target2, r.Target3, r.Target4, r.Target5, r.Target6 };

        var result = new List<TargetInfo>();
        foreach (var t in raw)
        {
            if (t?.Enabled.Value != true) continue;
            var name = t.ItemName.Value?.Trim();
            var qty = Math.Clamp(t.Quantity.Value, 0, RestockTargetSettings.MaxQuantity);
            if (string.IsNullOrWhiteSpace(name) || qty <= 0) continue;

            result.Add(new TargetInfo(name, qty, t.StashTabName.Value?.Trim() ?? string.Empty));
        }
        return result;
    }

    // Merges targets naming the same item, summing their quantities.
    private static List<TargetInfo> DedupeByIdentity(List<TargetInfo> targets)
    {
        var byName = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (byName.TryGetValue(t.ItemName, out var existing))
            {
                byName[t.ItemName] = existing with { CumulativeQuantity = existing.CumulativeQuantity + t.Quantity };
            }
            else
            {
                byName[t.ItemName] = t with { CumulativeQuantity = t.Quantity };
            }
        }
        return byName.Values.ToList();
    }

    private int InventoryQuantity(string itemName)
    {
        var items = _inventory.VisibleItems;
        if (items == null || string.IsNullOrWhiteSpace(itemName)) return 0;
        return items
            .Where(item => StashUi.Matches(item, itemName))
            .Sum(item => Math.Max(1, item.Item.GetComponent<Stack>()?.Size ?? 1));
    }

    // Parses the tier number out of a "Map (Tier N)" name.
    private static bool TryParseMapTier(string itemName, out int tier)
    {
        tier = 0;
        if (string.IsNullOrWhiteSpace(itemName)) return false;
        var match = System.Text.RegularExpressions.Regex.Match(itemName.Trim(),
            @"^Map \(Tier\s*(\d+)\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out tier) && tier > 0;
    }

    private Keys[] PassthroughKeys()
    {
        var key = _settings.Restock.RestockHotkey?.Value.Key ?? Keys.None;
        return key == Keys.None ? Array.Empty<Keys>() : new[] { key };
    }

    private sealed record TargetInfo(string ItemName, int Quantity, string StashTabName)
    {
        // Summed quantity across merged targets; equals Quantity before dedup.
        public int CumulativeQuantity { get; init; } = Quantity;
    }
}
