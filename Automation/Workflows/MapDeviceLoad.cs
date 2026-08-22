using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Analytics;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Prices;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Workflows;

// Loads the configured Restock targets into the Map Device slots.
//
// Flow:
//   1. Open the Map Device, walking to it if needed.
//   2. Select the configured Atlas map, if any.
//   3. Clear non-target items out of the device.
//   4. Ctrl-click each target in from storage or inventory, splitting stacks that overshoot.
//   5. Verify quantities and warn on shortfalls.
//   6. Capture the prepared cost breakdown and move the cursor to Activate.
public sealed class MapDeviceLoad
{
    // Ceiling on how long the split prompt may take to appear.
    private const int SplitPromptTimeoutMs = 700;

    private readonly Runner _runner;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly GameController _game;
    private readonly MapDeviceUi _mapDevice;
    private readonly AtlasUi _atlas;
    private readonly InventoryUi _inventory;
    private readonly CostTracker _cost;
    private readonly PriceService _prices;
    private readonly Restock _restock;
    private readonly BatchClicker _batch;
    private readonly HideoutTravel _hideoutTravel;

    public MapDeviceLoad(Runner runner, AutomationInput input, Waits waits, BeastsSettings settings,
        GameController game, MapDeviceUi mapDevice, AtlasUi atlas, InventoryUi inventory,
        CostTracker cost, PriceService prices, Restock restock, HideoutTravel hideoutTravel)
    {
        _restock = restock;
        _batch = new BatchClicker(input, settings);
        _runner = runner;
        _input = input;
        _waits = waits;
        _settings = settings;
        _game = game;
        _mapDevice = mapDevice;
        _atlas = atlas;
        _inventory = inventory;
        _cost = cost;
        _prices = prices;
        _hideoutTravel = hideoutTravel;
    }

    public Task RunAsync() =>
        _runner.QueueAsync(
            RunBodyAsync,
            failureLabel: "Map device load",
            // Let through the input lock so the run's own hotkey can stop it.
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepInventory = true, KeepMapDeviceWindow = true, KeepAtlas = true },
            cancelledStatus: "Map device load cancelled.");

    private Keys[] PassthroughKeys()
    {
        var key = _settings.Restock.LoadMapDeviceHotkey?.Value.Key ?? Keys.None;
        return key == Keys.None ? Array.Empty<Keys>() : new[] { key };
    }

    public async Task RunBodyAsync(CancellationToken ct)
    {
        // The Map Device only exists in the hideout, so travel there rather than erroring out.
        if (!_hideoutTravel.IsInHideout)
        {
            _runner.UpdateStatus("Traveling to hideout for Map Device loading...");
            await _hideoutTravel.EnsureInHideoutAsync(ct, "Map device loading only works in the hideout.");
        }

        // Walks to the map device if its window is not already up.
        if (!_mapDevice.IsWindowVisible && _atlas.IsVisible != true)
        {
            _runner.UpdateStatus("Opening Map Device...");
            if (!await _mapDevice.EnsureOpenAsync())
                throw new InvalidOperationException("Could not open the Map Device. Make sure you're standing next to it.");
        }

        // Selects the configured map on the Atlas, if any.
        await SelectConfiguredMapIfNeededAsync(ct);

        if (!_mapDevice.IsWindowVisible)
        {
            throw new InvalidOperationException(
                "Map Device window still isn't visible after opening + map selection. Aborting.");
        }

        var targets = EnabledTargets();
        if (targets.Count == 0)
        {
            _runner.UpdateStatus("Map device load: no enabled restock targets to load.");
            return;
        }

        LogSlotContents("before load");

        _runner.UpdateStatus("Loading Map Device...");
        var loadedTotal = 0;
        var dedupedTargets = DedupeByIdentity(targets);

        // Settles the slot count first, so free-slot reads are accurate and the consumed-run check
        // below sees the width the config asks for: strays out, then targets spread too wide.
        await ClearNonTargetItemsAsync(dedupedTargets, ct);
        await TrimSurplusTargetSlotsAsync(dedupedTargets, ct);

        dedupedTargets = ApplyConsumedRunDeficit(dedupedTargets);

        await AutoRestockIfShortAsync(dedupedTargets, ct);

        foreach (var target in dedupedTargets)
        {
            ct.ThrowIfCancellationRequested();
            loadedTotal += await LoadTargetAsync(target, ct);
        }

        await StoreSpareMapsAsync(dedupedTargets, ct);

        LogSlotContents("after load");

        // Re-scans and stops on shortfalls rather than warning. A device short of a slot runs the
        // map with fewer scarabs than configured for as long as the stock lasts, which is worth
        // failing the load over - there is nothing left to retry with anyway, restock already ran.
        var mismatches = VerifyLoadedTargets(dedupedTargets);
        if (mismatches.Count > 0)
        {
            var summary = string.Join(", ", mismatches.Select(m =>
                $"{m.Name} x{m.Loaded}/{m.Requested} in {m.SlotsHeld}/{m.SlotsWanted} " +
                $"slot{ImGuiEx.PluralSuffix(m.SlotsWanted)}"));
            Log.Info($"Map Device verification found {mismatches.Count} target(s) short: {summary}");

            throw new MapSupplyExhaustedException(
                $"Map Device could not be filled: {summary}. Restock those and load again.");
        }

        // Records the prepared cost breakdown for the upcoming map.
        CaptureCostBreakdown();

        await MoveCursorToActivateButtonAsync();
        _runner.UpdateStatus($"Map Device loaded. {loadedTotal} item{ImGuiEx.PluralSuffix(loadedTotal)} placed. Cursor on Activate.");
    }

    // ---- clearing non-targets ------------------------------------------

    // Ctrl-clicks non-target items out of the device. Slot kinds with no configured target
    // are left untouched.
    private async Task ClearNonTargetItemsAsync(List<TargetInfo> targets, CancellationToken ct)
    {
        if (!_settings.Restock.ClearNonTargetMapDeviceItems.Value) return;

        var hasMapTarget = targets.Any(t => t.IsMapTier);
        var hasFragmentTarget = targets.Any(t => !t.IsMapTier);

        var strays = _mapDevice.GetSlotItems()
            .Where(s => !s.IsEmpty && s.IsClickable)
            .Where(s => s.IsMapSlot ? hasMapTarget : hasFragmentTarget)
            .Where(s => !targets.Any(t => t.IsMapTier == s.IsMapSlot && MatchesSlot(s, t)))
            .ToList();

        if (strays.Count == 0) return;

        foreach (var stray in strays)
        {
            Log.Info($"Map Device slot [{stray.SlotIndex}] holds '{stray.BaseName ?? "<unreadable>"}' x{stray.StackSize}, " +
                     "which is not a configured target. Moving it to inventory.");
        }

        await EvictSlotsAsync(strays, "Clearing non-target items out of the Map Device", ct);

        LogSlotContents("after clearing non-targets");
    }

    // Ctrl-clicks a target's surplus slots back into inventory. Activation applies a fragment once
    // per slot it occupies, so a scarab configured for one stack but sitting in two both costs and
    // reads double - lowering a quantity leaves the device exactly like that. The shallowest slots
    // go first, keeping the deepest ones whole.
    private async Task TrimSurplusTargetSlotsAsync(List<TargetInfo> targets, CancellationToken ct)
    {
        var trimmed = false;

        foreach (var target in targets)
        {
            if (target.IsMapTier) continue;

            // Without a readable stack size the wanted count falls back to a single slot, which
            // would read a legitimately two-slot target as surplus. Nothing is trimmed on a guess.
            if (MaxStackSizeFor(target) <= 0) continue;

            var wanted = SlotsWantedFor(target);
            var held = _mapDevice.GetSlotItems()
                .Where(s => !s.IsEmpty && s.IsClickable && SlotHoldsTarget(s, target))
                .OrderBy(s => s.StackSize)
                .ToList();

            if (held.Count <= wanted) continue;

            var surplus = held.Take(held.Count - wanted).ToList();
            var described = string.Join(", ", surplus.Select(s => $"[{s.SlotIndex}] x{s.StackSize}"));
            var occupies = $"'{target.ItemName}' sits in {held.Count} Map Device slots but its quantity " +
                           $"({target.CumulativeQuantity}) asks for {wanted}";

            if (!_settings.Restock.ClearNonTargetMapDeviceItems.Value)
            {
                Log.Warn($"{occupies}, so every map applies it {held.Count} times over. " +
                         $"Turn on 'Clear Non-Target Map Device Items' to move {described} out, " +
                         "or take them out by hand.");
                continue;
            }

            Log.Info($"{occupies}. Moving {described} to inventory.");

            await EvictSlotsAsync(
                surplus,
                $"Trimming {target.ItemName} to {wanted} Map Device slot{ImGuiEx.PluralSuffix(wanted)}",
                ct);

            trimmed = true;
        }

        if (trimmed) LogSlotContents("after trimming surplus slots");
    }

    // Ctrl-clicks the given slots out to inventory and waits for them to empty.
    private async Task EvictSlotsAsync(List<MapDeviceUi.SlotItem> slots, string status, CancellationToken ct)
    {
        if (slots.Count == 0) return;

        var timing = _settings.Timing;

        // Slots are fixed positions that do not move as neighbours empty, so the whole set is
        // clicked before anything is confirmed.
        var passes = _batch.Enabled
            ? new[] { slots }
            : slots.Select(s => new List<MapDeviceUi.SlotItem> { s }).ToArray();

        foreach (var pass in passes)
        {
            ct.ThrowIfCancellationRequested();

            _runner.UpdateStatus($"{status} ({pass.Count} slot{ImGuiEx.PluralSuffix(pass.Count)})...");

            await _batch.CtrlClickAllAsync(
                pass.Select(s => new SharpVec2(s.Rect.Center.X, s.Rect.Center.Y)).ToList(),
                ct,
                guard: () => _mapDevice.IsWindowVisible);

            // Progress is read from the slots emptying.
            var slotIndexes = pass.Select(s => s.SlotIndex).ToList();
            await _batch.SettleAsync(
                () => slotIndexes.Count(i => SlotAt(i)?.IsEmpty != false),
                emptied => emptied >= slotIndexes.Count,
                _batch.BatchTimeoutMs(timing.Timeouts.MapDeviceTransferTimeoutMs.Value, pass.Count),
                ct);

            var stuck = pass.Where(s => SlotAt(s.SlotIndex)?.IsEmpty == false).ToList();
            if (stuck.Count > 0)
            {
                var described = string.Join(", ", stuck.Select(s => $"[{s.SlotIndex}] '{s.BaseName ?? "<unreadable>"}'"));
                throw new InvalidOperationException(
                    $"Could not clear {described} out of the Map Device - inventory is probably full.");
            }
        }
    }

    private MapDeviceUi.SlotItem SlotAt(int slotIndex) =>
        _mapDevice.GetSlotItems().FirstOrDefault(s => s.SlotIndex == slotIndex);

    // ---- auto-restock --------------------------------------------------

    // Total of a target across its slot, device storage and inventory.
    private int AvailableFor(TargetInfo target) =>
        CountLoadedFor(target) + StoredQuantityFor(target) + InventoryQuantityFor(target);

    // Runs Restock when a target cannot be satisfied from the device, storage and inventory.
    // Must run after ApplyConsumedRunDeficit so a partly consumed device does not read short.
    private async Task AutoRestockIfShortAsync(List<TargetInfo> targets, CancellationToken ct)
    {
        if (!_settings.Restock.AutoRestockMissingMapDeviceItems.Value) return;

        var shortfalls = targets
            .Where(t => t.CumulativeQuantity > 0 && AvailableFor(t) < t.CumulativeQuantity)
            .ToList();
        if (shortfalls.Count == 0) return;

        var summary = string.Join(", ", shortfalls.Select(t => $"{t.ItemName} {AvailableFor(t)}/{t.CumulativeQuantity}"));
        Log.Info($"Map Device auto-restock triggered. Short: {summary}");
        _runner.UpdateStatus($"Map Device short on {summary}. Restocking first...");

        // Device contents snapshotted while readable, so Restock pulls only the shortfall.
        var heldInDevice = targets.ToDictionary(
            t => t.ItemName,
            t => CountLoadedFor(t) + StoredQuantityFor(t),
            StringComparer.OrdinalIgnoreCase);

        Log.Debug($"Held in Map Device: {string.Join(", ", heldInDevice.Select(kv => $"{kv.Key} x{kv.Value}"))}");

        // Space closes the device window while keeping the map selection; Escape would not.
        await PressUntilClosedAsync(Keys.Space,
            () => !_mapDevice.IsWindowVisible,
            "the Map Device window");

        // Called as a body since this is already inside a run slot.
        await _restock.RunBodyAsync(ct, heldInDevice);

        // Closes the stash Restock left open, which would block the walk back.
        await PressUntilClosedAsync(Keys.Escape,
            () => _game?.IngameState?.IngameUi?.StashElement?.IsVisible != true,
            "the stash");

        if (!_mapDevice.IsWindowVisible)
        {
            _runner.UpdateStatus("Reopening Map Device after restock...");
            if (!await _mapDevice.EnsureOpenAsync())
                throw new InvalidOperationException("Restocked, but could not reopen the Map Device afterwards.");

            // Waits for the device window itself, since slots are unreadable from the Atlas.
            var timing = _settings.Timing;
            var windowUp = await _waits.WaitForAsync(
                () => _mapDevice.IsWindowVisible,
                timeoutMs: _input.ScaleTimeout(timing.Timeouts.MapDeviceOpenTimeoutMs.Value),
                pollDelayMs: timing.Polling.FastPollDelayMs.Value);

            if (!windowUp)
                throw new InvalidOperationException(
                    "Restocked and reopened, but the Map Device window never appeared - the Atlas is showing instead.");
        }

        LogSlotContents("after auto-restock");
    }

    // Taps `key` until `closed` holds.
    private async Task PressUntilClosedAsync(Keys key, Func<bool> closed, string what)
    {
        var timing = _settings.Timing;
        var attempts = Math.Max(1, timing.Timeouts.MapDeviceCloseUiMaxAttempts.Value);

        for (var attempt = 0; attempt < attempts && !closed(); attempt++)
        {
            await _input.TapKeyAsync(key, timing.Clicks.KeyTapDelayMs.Value,
                timing.Polling.FastPollDelayMs.Value);

            await _waits.WaitForAsync(closed,
                timeoutMs: Math.Max(timing.Polling.VisibleTabTimeoutMs.Value, 600),
                pollDelayMs: timing.Polling.FastPollDelayMs.Value);
        }

        if (!closed())
            throw new InvalidOperationException($"Could not close {what} after {attempts} attempts.");

        Log.Debug($"Closed {what}.");
    }

    // ---- per-target ----------------------------------------------------

    // Loads one target into the device and returns the quantity moved.
    private async Task<int> LoadTargetAsync(TargetInfo target, CancellationToken ct)
    {
        var requested = target.CumulativeQuantity;
        if (requested <= 0) return 0;
        if (!EnsureLoadable(target, requested, out var alreadyLoaded)) return 0;

        _runner.UpdateStatus($"Loading {target.ItemName} into Map Device ({alreadyLoaded}/{requested})...");

        var loaded = 0;
        var stallCount = 0;
        var timing = _settings.Timing;

        while (CountLoadedFor(target) < requested)
        {
            ct.ThrowIfCancellationRequested();

            var beforeLoaded = CountLoadedFor(target);
            var stillNeeded = requested - beforeLoaded;

            // Device storage is drained before inventory.
            var batch = CollectLoadBatch(target, stillNeeded, out var overshooting);
            if (batch.Count == 0 && overshooting == null)
            {
                _runner.UpdateStatus($"Loaded {target.ItemName}: {beforeLoaded}/{requested}. Nothing left to load.");
                break;
            }

            // What this pass should add. A pass can fall short when storage and inventory run out,
            // so the settle waits for what was actually clicked.
            var expectedGain = await SendLoadPassAsync(target, batch, overshooting, stillNeeded, ct);
            if (expectedGain == null)
            {
                if (++stallCount >= 3)
                    throw new InvalidOperationException(
                        $"Map device load of '{target.ItemName}' stalled - no source cell could be clicked after transferring {loaded}.");
                await _input.DelayAsync(timing.Polling.FastPollDelayMs.Value);
                continue;
            }

            // One confirmation per pass, read from the slot rather than the source: the slot lags
            // the inventory count and is what the run actually needs.
            var expected = Math.Min(requested, beforeLoaded + expectedGain.Value);
            var afterLoaded = await _batch.SettleAsync(
                () => CountLoadedFor(target),
                count => count >= expected,
                _batch.BatchTimeoutMs(timing.Timeouts.MapDeviceTransferTimeoutMs.Value, Math.Max(1, batch.Count)),
                ct);

            if (afterLoaded <= beforeLoaded)
            {
                if (++stallCount >= 3)
                    throw new InvalidOperationException(
                        $"Map device load of '{target.ItemName}' stalled after transferring {loaded}. Loaded {afterLoaded}/{requested}.");
                await _input.DelayAsync(timing.Polling.FastPollDelayMs.Value);
                continue;
            }

            stallCount = 0;
            loaded += afterLoaded - beforeLoaded;
            _runner.UpdateStatus($"Loading {target.ItemName} into Map Device ({afterLoaded}/{requested})...");
        }

        return loaded;
    }

    // Preflight for one target: false when the device already holds enough. Throws when the
    // target could never be loaded - no slot will take it, or there is none to load.
    private bool EnsureLoadable(TargetInfo target, int requested, out int alreadyLoaded)
    {
        alreadyLoaded = CountLoadedFor(target);
        if (alreadyLoaded >= requested)
        {
            Log.Debug($"MapDevice '{target.ItemName}': already loaded {alreadyLoaded}/{requested}. Skipping.");
            return false;
        }

        // Fails early with the blocking occupants named when too few slots can take the target.
        if (!HasSlotFor(target, out var occupants))
        {
            throw new InvalidOperationException(
                $"Map device: not enough {(target.IsMapTier ? "map" : "fragment")} slots for '{target.ItemName}' - " +
                $"{occupants}. Turn on 'Clear Non-Target Map Device Items' or take it out by hand.");
        }

        if (InventoryQuantityFor(target) + StoredQuantityFor(target) <= 0)
        {
            throw new MapSupplyExhaustedException(
                $"Map device: no '{target.ItemName}' in inventory or device storage to load. Restock first.");
        }

        return true;
    }

    // Sends one transfer pass and reports the quantity it should add, or null when no source
    // cell could be clicked. The split runs last, so its prompt opens against a settled slot.
    private async Task<int?> SendLoadPassAsync(
        TargetInfo target, List<NormalInventoryItem> batch, NormalInventoryItem overshooting,
        int stillNeeded, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            await TransferPartialStackAsync(overshooting, stillNeeded, target, ct);
            return stillNeeded;
        }

        var expectedGain = batch.Sum(item => Math.Max(1, MoveableIntoSlot(target, item)));
        var clicked = await _batch.CtrlClickAllAsync(
            BatchClicker.CellCenters(batch), ct, guard: () => _mapDevice.IsWindowVisible);

        return clicked == 0 ? null : expectedGain;
    }

    // Ctrl-clicks leftover maps from inventory into the device's storage grid.
    private async Task StoreSpareMapsAsync(List<TargetInfo> targets, CancellationToken ct)
    {
        var mapTarget = targets.FirstOrDefault(t => t.IsMapTier);
        if (mapTarget == null) return;

        var free = _mapDevice.StorageFreeCellCount();
        if (free <= 0)
        {
            if (free == 0) Log.Debug("Map Device storage is full; leaving spare maps in inventory.");
            return;
        }

        var timing = _settings.Timing;
        var stored = 0;
        var stallCount = 0;

        while (_mapDevice.StorageFreeCellCount() > 0)
        {
            ct.ThrowIfCancellationRequested();

            // Maps are one cell each, so a pass is capped by the free storage cells.
            var freeCells = _mapDevice.StorageFreeCellCount();
            var candidates = FindInventoryItems(mapTarget);
            var batch = _batch.Enabled
                ? candidates.Take(freeCells).ToList()
                : candidates.Take(1).ToList();

            if (batch.Count == 0) break;

            var beforeStored = StoredQuantityFor(mapTarget);
            var clicked = await _batch.CtrlClickAllAsync(
                BatchClicker.CellCenters(batch), ct, guard: () => _mapDevice.IsWindowVisible);

            var afterStored = clicked == 0
                ? beforeStored
                : await _batch.SettleAsync(
                    () => StoredQuantityFor(mapTarget),
                    count => count >= beforeStored + clicked,
                    _batch.BatchTimeoutMs(timing.Timeouts.MapDeviceTransferTimeoutMs.Value, clicked),
                    ct);

            if (afterStored <= beforeStored)
            {
                stallCount++;
                if (stallCount >= 3)
                {
                    Log.Debug($"Storing spare maps stalled after {stored}. Leaving the rest in inventory.");
                    break;
                }
                await _input.DelayAsync(timing.Polling.FastPollDelayMs.Value);
                continue;
            }

            stallCount = 0;
            stored += afterStored - beforeStored;
            _runner.UpdateStatus($"Storing spare maps in the Map Device... {stored}");
        }

        if (stored > 0)
            Log.Info($"Stored {stored} spare map{ImGuiEx.PluralSuffix(stored)} in the Map Device.");
    }

    // ---- atlas map selection ------------------------------------------

    // Selects the configured map on the Atlas unless it is already loaded.
    private async Task SelectConfiguredMapIfNeededAsync(CancellationToken ct)
    {
        var configured = AtlasUi.NormalizeMapSelectionValue(_settings.Restock.SelectedMapToRun.Value);
        if (string.Equals(configured, AtlasUi.OpenMapSelectionValue, StringComparison.OrdinalIgnoreCase))
            return;

        // Skips when the device already shows the configured map.
        if (MapDeviceUi.TitleMatches(_mapDevice.GetWindowTitleText(), configured))
        {
            Log.Debug($"Configured map '{configured}' already loaded in Map Device. Skipping selection.");
            return;
        }

        if (!_atlas.IsVisible)
        {
            Log.Debug("Configured map selection skipped - Atlas isn't visible.");
            return;
        }

        // Closes inventory so it does not block the Atlas clicks.
        await CloseInventoryIfOpenAsync();

        _runner.UpdateStatus("Preparing Atlas for map selection...");
        await _atlas.NormalizeScaleAsync();
        await _atlas.CenterYAsync();

        ct.ThrowIfCancellationRequested();

        var index = _atlas.TryResolveMapUiIndex(configured);
        if (!index.HasValue)
            throw new InvalidOperationException(
                $"Configured map '{configured}' was not found in AtlasNodes. Change 'Selected Atlas Map' to a map name that exists on your Atlas, or 'open Map' to skip selection.");

        var element = _atlas.TryGetMapElement(index.Value);
        if (element?.IsVisible != true)
            throw new InvalidOperationException(
                $"Configured map '{configured}' (Atlas UI index {index}) is not visible. Move the Atlas so this map is on-screen.");

        _runner.UpdateStatus($"Selecting map: {configured}");
        var rect = element.GetClientRect();
        var timing = _settings.Timing;
        await _input.ClickAtAsync(
            rect,
            MouseButtons.Left,
            preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
            postDelayMs: timing.Clicks.CtrlClickPostDelayMs.Value);

        var opened = await _waits.WaitForAsync(
            () => _mapDevice.IsWindowVisible && MapDeviceUi.TitleMatches(_mapDevice.GetWindowTitleText(), configured),
            timeoutMs: Math.Max(500, timing.Polling.OpenStashPostClickDelayMs.Value),
            pollDelayMs: Math.Max(10, timing.Polling.FastPollDelayMs.Value));

        if (!opened)
        {
            var observed = _mapDevice.GetWindowTitleText();
            throw new InvalidOperationException(
                $"Clicked map '{configured}' on the Atlas but Map Device title didn't match. Observed: '{observed ?? "<null>"}'.");
        }
    }

    private async Task CloseInventoryIfOpenAsync()
    {
        var inventoryPanel = _inventory.IsVisible;
        if (!inventoryPanel) return;

        var toggle = _settings.Restock.InventoryToggleHotkey?.Value.Key ?? Keys.None;
        if (toggle == Keys.None)
            throw new InvalidOperationException(
                "Inventory is open before Atlas map search; set Automation: Restock -> Inventory Toggle Hotkey to match your PoE keybind.");

        _runner.UpdateStatus("Closing inventory before map search...");
        var timing = _settings.Timing;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await _input.TapKeyAsync(toggle, timing.Clicks.KeyTapDelayMs.Value, timing.Polling.FastPollDelayMs.Value);
            await _input.DelayForUiCheckAsync(100);
            if (!_inventory.IsVisible) return;
        }
        throw new InvalidOperationException(
            $"Inventory still open after pressing Inventory Toggle Hotkey '{toggle}' twice.");
    }

    // ---- cost capture --------------------------------------------------

    // Prices the device's contents into CostTracker's prepared breakdown.
    private void CaptureCostBreakdown()
    {
        var breakdown = new List<MapCostItem>();
        var usesDupScarab = false;

        foreach (var slot in _mapDevice.GetSlotItems())
        {
            if (slot.IsEmpty) continue;
            var name = slot.BaseName ?? (slot.MapTier.HasValue ? $"Map (Tier {slot.MapTier.Value})" : null);
            if (string.IsNullOrWhiteSpace(name)) continue;

            _prices.TryGetItemPriceChaos(name, out var chaos);
            breakdown.Add(new MapCostItem { ItemName = name, UnitPriceChaos = chaos });
            if (CostTracker.IsDuplicatingScarabItemName(name)) usesDupScarab = true;
        }

        _cost.SetPrepared(breakdown, usedDuplicatingScarabOverride: usesDupScarab);
    }

    private async Task MoveCursorToActivateButtonAsync()
    {
        var button = _mapDevice.ActivateButton;
        if (button?.IsVisible != true)
        {
            Log.Debug("Activate button not visible after load - leaving cursor as-is.");
            return;
        }
        await _input.MoveCursorToAsync(button.GetClientRect());
    }

    // ---- inventory + slot queries -------------------------------------

    // Total quantity of a target held in inventory.
    private int InventoryQuantityFor(TargetInfo target)
    {
        var items = _inventory.VisibleItems;
        if (items == null) return 0;

        return items
            .Where(i => MatchesTarget(i, target))
            .Sum(i => Math.Max(1, i.Item.GetComponent<Stack>()?.Size ?? 1));
    }

    // Matching inventory cells in reading order.
    private List<NormalInventoryItem> FindInventoryItems(TargetInfo target) =>
        Ordered(_inventory.VisibleItems, target);

    private static List<NormalInventoryItem> Ordered(IList<NormalInventoryItem> items, TargetInfo target)
    {
        if (items == null) return new List<NormalInventoryItem>();
        return items
            .Where(i => MatchesTarget(i, target))
            .OrderBy(i => i.GetClientRect().Top)
            .ThenBy(i => i.GetClientRect().Left)
            .ToList();
    }

    private int StoredQuantityFor(TargetInfo target)
    {
        var items = _mapDevice.StorageItems;
        if (items == null) return 0;

        return items
            .Where(i => MatchesTarget(i, target))
            .Sum(i => Math.Max(1, i.Item.GetComponent<Stack>()?.Size ?? 1));
    }

    // Matching device-storage cells in reading order.
    private List<NormalInventoryItem> FindStorageItems(TargetInfo target) =>
        Ordered(_mapDevice.StorageItems, target);

    // Sources to ctrl-click into the device in one pass, storage before inventory.
    private List<NormalInventoryItem> CollectLoadBatch(
        TargetInfo target, int stillNeeded, out NormalInventoryItem overshooting)
    {
        overshooting = null;
        var batch = new List<NormalInventoryItem>();
        if (stillNeeded <= 0) return batch;

        var sources = FindStorageItems(target);
        if (sources.Count == 0) sources = FindInventoryItems(target);

        var running = 0;
        foreach (var candidate in sources)
        {
            if (running >= stillNeeded) break;

            var moveable = MoveableIntoSlot(target, candidate);
            if (!target.IsMapTier && running + moveable > stillNeeded)
            {
                overshooting = candidate;
                break;
            }

            batch.Add(candidate);
            running += Math.Max(1, moveable);

            if (!_batch.Enabled) break;
        }

        return batch;
    }

    private static bool MatchesTarget(NormalInventoryItem item, TargetInfo target)
    {
        if (item?.Item == null) return false;

        // Base name must match; tier is an additional check, never the only one.
        if (target.IsMapTier && item.Item.GetComponent<MapKey>()?.Tier != target.MapTier)
            return false;

        var baseName = item.Item.GetComponent<Base>()?.Name;
        return string.Equals(baseName, target.ItemName, StringComparison.OrdinalIgnoreCase);
    }

    // Logs what each device slot reports holding.
    private void LogSlotContents(string when)
    {
        var slots = _mapDevice.GetSlotItems();
        if (slots.Count == 0)
        {
            Log.Debug($"Map Device slots ({when}): none readable.");
            return;
        }

        var described = slots.Select(s => s.IsEmpty
            ? $"[{s.SlotIndex}] empty"
            : $"[{s.SlotIndex}] '{s.BaseName}' tier={s.MapTier?.ToString() ?? "-"} x{s.StackSize}");
        Log.Debug($"Map Device slots ({when}), {slots.Count} total: {string.Join(" | ", described)}");
    }

    // Quantity one ctrl-click moves: the source stack capped by the room left in the slot.
    private int MoveableIntoSlot(TargetInfo target, NormalInventoryItem item)
    {
        var stack = item?.Item?.GetComponent<Stack>();
        var size = Math.Max(1, stack?.Size ?? 1);

        var maxStackSize = stack?.Info?.MaxStackSize ?? 0;
        if (maxStackSize <= 0) return size;

        var headroom = SlotHeadroomFor(target, maxStackSize);

        // No readable headroom means the slots could not be measured, not that they are full;
        // one stack is the safe assumption and the transfer verifies itself either way.
        return Math.Min(size, headroom > 0 ? headroom : maxStackSize);
    }

    private int CountLoadedFor(TargetInfo target) =>
        target.IsMapTier
            ? _mapDevice.CountLoadedByMapTier(target.MapTier.Value)
            : _mapDevice.CountLoadedByName(target.ItemName);

    // Takes exactly `quantity` from a stack via the split prompt and clicks it into the
    // destination slot. Falls back to a whole-stack ctrl-click when that is not possible.
    private async Task TransferPartialStackAsync(NormalInventoryItem item, int quantity, TargetInfo target,
        CancellationToken ct)
    {
        var timing = _settings.Timing;
        var loadedBefore = CountLoadedFor(target);

        var slotRect = FindDestinationSlotRect(target);
        if (slotRect == null)
        {
            Log.Debug($"No Map Device slot found to place a partial stack of '{target.ItemName}'. Falling back to a whole-stack ctrl-click.");
            await _input.CtrlClickAtAsync(item.GetClientRect());
            return;
        }

        var rect = item.GetClientRect();
        await _input.ClickAtAsync(
            rect, MouseButtons.Left,
            preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
            postDelayMs: timing.Clicks.UiClickPostDelayMs.Value,
            modifiers: new[] { Keys.LShiftKey });

        // Waits for the split prompt, not the destroy-confirmation dialog.
        var opened = await _waits.WaitForAsync(
            () => _inventory.IsStackSplitDialogVisible,
            timeoutMs: SplitPromptTimeoutMs,
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        if (!opened)
        {
            Log.Debug($"Split prompt did not open for '{target.ItemName}'. Falling back to a whole-stack ctrl-click.");
            await _input.CtrlClickAtAsync(item.GetClientRect());
            return;
        }

        // Verifies the typed amount before committing it.
        var wanted = quantity.ToString(CultureInfo.InvariantCulture);
        var typed = false;
        for (var attempt = 1; attempt <= 2 && !typed; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await _input.TypeDigitsAsync(wanted);
            typed = await _waits.WaitForAsync(
                () => string.Equals(_inventory.StackSplitQuantityText, wanted, StringComparison.Ordinal),
                timeoutMs: SplitPromptTimeoutMs,
                pollDelayMs: timing.Polling.FastPollDelayMs.Value);

            if (!typed)
                Log.Debug($"Split quantity attempt {attempt}: field reads '{_inventory.StackSplitQuantityText}', wanted '{wanted}'.");
        }

        if (!typed)
            throw new InvalidOperationException(
                $"Could not enter {quantity} into the split prompt for '{target.ItemName}'. Field reads '{_inventory.StackSplitQuantityText}'.");

        await _input.TapKeyAsync(Keys.Enter, timing.Clicks.KeyTapDelayMs.Value, timing.Polling.FastPollDelayMs.Value);

        var closed = await _waits.WaitForAsync(
            () => !_inventory.IsStackSplitDialogVisible,
            timeoutMs: SplitPromptTimeoutMs,
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        if (!closed)
            throw new InvalidOperationException(
                $"Split prompt for '{target.ItemName}' never closed after entering {quantity}.");

        // Checks whether the split already landed, since clicking again would pick it up.
        var landedOnConfirm = await _waits.WaitForAsync(
            () => CountLoadedFor(target) >= loadedBefore + quantity,
            timeoutMs: Math.Max(150, timing.Polling.FastPollDelayMs.Value * 3),
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        if (landedOnConfirm)
        {
            Log.Debug($"Split {quantity} of '{target.ItemName}' went straight into the Map Device - no placement click needed.");
            return;
        }

        // Drops the cursor-held split into the slot.
        var destination = slotRect.Value;
        await _input.ClickAtAsync(
            destination, MouseButtons.Left,
            preDelayMs: Math.Max(timing.Clicks.UiClickPreDelayMs.Value, 100),
            postDelayMs: timing.Clicks.CtrlClickPostDelayMs.Value);

        // Confirms the slot count rose, which the caller's looser check would not catch.
        var landed = await _waits.WaitForAsync(
            () => CountLoadedFor(target) >= loadedBefore + quantity,
            timeoutMs: _input.ScaleTimeout(timing.Timeouts.MapDeviceTransferTimeoutMs.Value),
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        if (!landed)
            throw new InvalidOperationException(
                $"Split {quantity} of '{target.ItemName}' but the Map Device slot at " +
                $"({destination.Center.X:0}, {destination.Center.Y:0}) reads {CountLoadedFor(target)} " +
                $"(was {loadedBefore}). The stack is probably still on the cursor.");

        Log.Debug($"Split {quantity} of '{target.ItemName}' into Map Device slot at ({destination.Center.X:0}, {destination.Center.Y:0}).");
    }

    // True when enough slots can take the target: the ones it already occupies plus the empty
    // ones, measured against the slot count its quantity needs. Unreadable slots do not block.
    private bool HasSlotFor(TargetInfo target, out string occupants)
    {
        var candidates = _mapDevice.GetSlotItems()
            .Where(s => s.IsMapSlot == target.IsMapTier)
            .ToList();

        var wanted = SlotsWantedFor(target);
        var usable = candidates.Count(s => s.IsEmpty || SlotHoldsTarget(s, target));

        var held = candidates.Any(s => !s.IsEmpty)
            ? string.Join(", ", candidates.Where(s => !s.IsEmpty)
                .Select(s => $"slot [{s.SlotIndex}] holds '{s.BaseName}' x{s.StackSize}"))
            : "no slots readable";

        occupants = $"needs {wanted}, {usable} can take it; {held}";

        return candidates.Count == 0 || usable >= wanted;
    }

    // The slot a split should be dropped into. While the target occupies fewer slots than it
    // wants, an empty one comes first: width is what the map reads, so opening the missing slot
    // beats deepening a stack that already counts. Otherwise it is the matching slot with room -
    // past one full stack the request needs a second slot, and aiming at a full one leaves the
    // stack stuck on the cursor. When neither exists the caller falls back to a plain ctrl-click.
    private SharpDX.RectangleF? FindDestinationSlotRect(TargetInfo target)
    {
        var candidates = _mapDevice.GetSlotItems()
            .Where(s => s.IsClickable && s.IsMapSlot == target.IsMapTier)
            .ToList();
        if (candidates.Count == 0) return null;

        var empty = candidates.FirstOrDefault(s => s.IsEmpty);
        if (empty != null && SlotsHeldFor(target) < SlotsWantedFor(target)) return empty.Rect;

        var withRoom = candidates.FirstOrDefault(s => !s.IsEmpty && MatchesSlot(s, target) && s.Headroom > 0);
        if (withRoom != null) return withRoom.Rect;

        return empty?.Rect;
    }

    // Room in the slot this target's next transfer will land in. Per-slot, not per-target:
    // two slots holding the same scarab take two full stacks, and a combined total would
    // call the device full after the first.
    private int SlotHeadroomFor(TargetInfo target, int maxStackSize)
    {
        var candidates = _mapDevice.GetSlotItems()
            .Where(s => s.IsClickable && s.IsMapSlot == target.IsMapTier)
            .ToList();

        var withRoom = candidates.FirstOrDefault(s => !s.IsEmpty && MatchesSlot(s, target) && s.Headroom > 0);
        if (withRoom != null) return withRoom.Headroom;

        // A fresh slot takes a whole stack.
        return candidates.Any(s => s.IsEmpty) ? maxStackSize : 0;
    }

    // True when a slot's item matches the target by base name, and by tier for map targets.
    private static bool MatchesSlot(MapDeviceUi.SlotItem slot, TargetInfo target)
    {
        if (!string.Equals(slot.BaseName, target.ItemName, StringComparison.OrdinalIgnoreCase))
            return false;

        return !target.IsMapTier || slot.MapTier == target.MapTier;
    }

    // ---- slot model ----------------------------------------------------

    // Max stack size for a target, read off a live copy in a slot, device storage or inventory.
    // 0 means no copy was readable, and callers fall back to a single slot.
    private int MaxStackSizeFor(TargetInfo target)
    {
        if (target.IsMapTier) return 1;

        var fromSlot = _mapDevice.GetSlotItems()
            .Where(s => !s.IsEmpty && MatchesSlot(s, target))
            .Select(s => s.MaxStackSize)
            .FirstOrDefault(size => size > 0);
        if (fromSlot > 0) return fromSlot;

        foreach (var item in FindStorageItems(target).Concat(FindInventoryItems(target)))
        {
            var max = item?.Item?.GetComponent<Stack>()?.Info?.MaxStackSize ?? 0;
            if (max > 0) return max;
        }

        return 0;
    }

    // Slots a target's request needs: one stack per slot, rounded up. Activating consumes one
    // item from every occupied slot, so it is the slot count - not the item total - that decides
    // how many scarabs a map actually gets. Unreadable stack info falls back to a single slot.
    private int SlotsWantedFor(TargetInfo target)
    {
        if (target.IsMapTier) return 1;

        var maxStackSize = MaxStackSizeFor(target);
        if (maxStackSize <= 0) return 1;

        return Math.Max(1, (int)Math.Ceiling(target.CumulativeQuantity / (double)maxStackSize));
    }

    // Slots currently holding the target.
    private int SlotsHeldFor(TargetInfo target) =>
        _mapDevice.GetSlotItems().Count(s => !s.IsEmpty && SlotHoldsTarget(s, target));

    // Whether a slot counts toward the target, matched the way its loaded quantity is counted:
    // by tier for maps, by base name for fragments. MatchesSlot is stricter for maps - it wants
    // the base name too - so counting slots with it would read a loaded map as a missing one.
    private static bool SlotHoldsTarget(MapDeviceUi.SlotItem slot, TargetInfo target) =>
        target.IsMapTier ? slot.MapTier == target.MapTier : MatchesSlot(slot, target);

    // ---- verification --------------------------------------------------

    // Returns every target the device did not end up holding: short on items, or short on the
    // slots those items were meant to occupy. Slots are checked separately because they are what
    // the map reads - one slot holding a double stack still applies the scarab once.
    private List<(string Name, int Loaded, int Requested, int SlotsHeld, int SlotsWanted)>
        VerifyLoadedTargets(List<TargetInfo> targets)
    {
        var mismatches = new List<(string Name, int Loaded, int Requested, int SlotsHeld, int SlotsWanted)>();

        // Slots that cannot be read at all are a transient UI state, not a shortfall.
        if (_mapDevice.GetSlotItems().Count == 0) return mismatches;

        foreach (var target in targets)
        {
            var loaded = CountLoadedFor(target);
            var slotsHeld = SlotsHeldFor(target);
            var slotsWanted = SlotsWantedFor(target);

            if (loaded < target.CumulativeQuantity || slotsHeld < slotsWanted)
                mismatches.Add((target.ItemName, loaded, target.CumulativeQuantity, slotsHeld, slotsWanted));
        }
        return mismatches;
    }

    // ---- target enumeration -------------------------------------------

    // Enabled restock targets, with map targets reduced to one per device slot.
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

            var mapTier = TryParseMapTier(name);

            // Fragment targets load as one stack; the map slot takes exactly one map.
            result.Add(new TargetInfo(name, mapTier.HasValue ? 1 : qty, mapTier));
        }
        return result;
    }

    // Lowers a fragment target's request to what the device already holds when it is part-way
    // through its stock: every wanted slot still occupied, just shallower than a fresh load.
    //
    // Judged per target by slot count. A run consumes one item per occupied slot, so an item
    // deficit on its own says nothing about how many runs went by - and once a target spans two
    // slots it says the wrong thing outright, reading a slot that ran dry as a device deep into
    // its stock and leaving it to run one scarab short forever.
    private List<TargetInfo> ApplyConsumedRunDeficit(List<TargetInfo> targets)
    {
        var result = new List<TargetInfo>();

        foreach (var target in targets)
        {
            if (target.IsMapTier)
            {
                result.Add(target);
                continue;
            }

            var loaded = CountLoadedFor(target);
            var slotsWanted = SlotsWantedFor(target);
            var slotsHeld = SlotsHeldFor(target);

            // A missing slot is an under-loaded device, not a consumed one: the request stands so
            // restock and load rebuild it to its full width.
            if (slotsHeld < slotsWanted)
            {
                if (slotsHeld > 0)
                    Log.Info($"Map Device holds '{target.ItemName}' x{loaded} in {slotsHeld} of " +
                             $"{slotsWanted} wanted slot{ImGuiEx.PluralSuffix(slotsWanted)}. Reloading to full.");
                result.Add(target);
                continue;
            }

            // An empty or already-satisfied target is not a consumed-run state.
            if (loaded <= 0 || loaded >= target.CumulativeQuantity)
            {
                result.Add(target);
                continue;
            }

            Log.Info($"Map Device holds '{target.ItemName}' x{loaded} in {slotsHeld} " +
                     $"slot{ImGuiEx.PluralSuffix(slotsHeld)}, {target.CumulativeQuantity - loaded} into its " +
                     $"stock. Leaving those slots alone.");

            result.Add(target with { CumulativeQuantity = loaded });
        }

        return result;
    }

    private static List<TargetInfo> DedupeByIdentity(List<TargetInfo> targets)
    {
        var byKey = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            var key = t.IsMapTier ? $"map-tier:{t.MapTier}" : $"item:{t.ItemName}";
            if (byKey.TryGetValue(key, out var existing))
                byKey[key] = existing with { CumulativeQuantity = existing.CumulativeQuantity + t.Quantity };
            else
                byKey[key] = t with { CumulativeQuantity = t.Quantity };
        }
        return byKey.Values.ToList();
    }

    // Parses the tier out of a "Map (Tier N)" name; null for plain base-name targets.
    private static int? TryParseMapTier(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(name.Trim(),
            @"^Map \(Tier\s*(\d+)\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var tier) && tier > 0 ? tier : null;
    }

    private sealed record TargetInfo(string ItemName, int Quantity, int? MapTier)
    {
        public bool IsMapTier => MapTier.HasValue;
        public int CumulativeQuantity { get; init; } = Quantity;
    }
}
