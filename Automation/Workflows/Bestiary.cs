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
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ImGuiNET;
using RectangleF = SharpDX.RectangleF;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Workflows;

// Bestiary itemize and delete workflow. Delete ctrl-clicks each row's release button;
// itemize ctrl-clicks the row body. A regex can filter the list; the yellow pass clears
// the search instead and gates rows in code against BeastCatalog.
public sealed class Bestiary
{
    private const int MaxConsecutiveStalls = 6;
    private const int SettleAfterChangeMs = 200;
    private const int PanelWaitTimeoutMs = 2000;

    // Budget for rows to finish streaming in after the tab opens.
    private const int RowSettleTimeoutMs = 6000;

    // How long the row count must hold steady before the list counts as populated.
    private const int RowsStableMs = 250;

    // Budget for a tab to open after its button is clicked.
    private const int TabVerifyTimeoutMs = 1200;

    // Retries for a tab click, since synthetic clicks are occasionally dropped.
    private const int TabClickAttempts = 3;

    // Token no row's name, mods or recipes can contain, used to prove the filter is live.
    private const string FilterLivenessProbe = "zzqxvwj";

    // Scroll budget for getting past untracked rows blocking the viewport. Only refilled by
    // a batch that releases something, so scrolling can never replace progress.
    private const int MaxScrollsBetweenBatches = 40;

    // Same budget for a sweep, which runs unfiltered and can face far longer stuck patches.
    private const int MaxScrollsBetweenSweepBatches = 250;

    // Extra upward ticks when backing out, so the grid lands at the top.
    private const int ScrollBackOvershoot = 3;

    // Ceiling on the doubling downward scroll step, in wheel notches.
    private const int MaxScrollStepTicks = 4;

    // Consecutive "grid did not move" readings before a downward scroll counts as the end.
    private const int NoMoveSamplesForBottom = 2;

    // Clickable cells a sweep lines up before committing a batch (~one viewport: 3 x 4).
    private const int GatherTargetCells = 9;

    // Cap on notches spent gathering, and on empty ones before the batch goes as it is.
    private const int MaxGatherScrolls = 6;
    private const int MaxBarrenGatherScrolls = 2;

    // Movement below this counts as "the list did not move", i.e. already at the bottom.
    private const float ScrollMovedEpsilonPixels = 1f;

    private readonly Runner _runner;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly BestiaryUi _bestiaryUi;
    private readonly InventoryUi _inventoryUi;
    private readonly ClipboardAutoPaste _clipboard;
    private readonly CapturedMonsterStash _capturedMonsterStash;
    private readonly HideoutTravel _hideoutTravel;

    // Regex to reapply when the panel is reopened after an auto-stash interrupt.
    private string _activeRegexForResume = string.Empty;

    // Set for a yellow pass, so a reopened panel gets its search cleared again.
    private bool _clearFilterForResume;

    public Bestiary(Runner runner, AutomationInput input, Waits waits, BeastsSettings settings,
        BestiaryUi bestiaryUi, InventoryUi inventoryUi, ClipboardAutoPaste clipboard,
        CapturedMonsterStash capturedMonsterStash, HideoutTravel hideoutTravel)
    {
        _runner = runner;
        _input = input;
        _waits = waits;
        _settings = settings;
        _bestiaryUi = bestiaryUi;
        _inventoryUi = inventoryUi;
        _clipboard = clipboard;
        _capturedMonsterStash = capturedMonsterStash;
        _hideoutTravel = hideoutTravel;
    }

    // ---- entry points --------------------------------------------------

    public Task DeleteAllAsync() =>
        _runner.QueueAsync(
            ct => RunClearAsync(ct, deleteMode: true, applyRegex: false),
            failureLabel: "Bestiary delete",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepBestiary = true, KeepInventory = true },
            cancelledStatus: "Bestiary delete cancelled.",
            isBestiaryClearRunning: true,
            clearBestiaryDeleteModeOverride: true);

    public Task ItemizeAllAsync() =>
        _runner.QueueAsync(
            ct => RunItemizeGuardedAsync(ct, applyRegex: false),
            failureLabel: "Bestiary itemize",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepBestiary = true, KeepInventory = true },
            cancelledStatus: "Bestiary itemize cancelled.",
            isBestiaryClearRunning: true);

    public Task RegexItemizeAsync() =>
        _runner.QueueAsync(
            ct => RunItemizeGuardedAsync(ct, applyRegex: true),
            failureLabel: "Bestiary regex itemize",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepBestiary = true, KeepInventory = true },
            cancelledStatus: "Bestiary regex itemize cancelled.",
            isBestiaryClearRunning: true);

    // Itemizes everything not in BeastCatalog - the inverse of a regex itemize.
    public Task ItemizeYellowsAsync() =>
        _runner.QueueAsync(
            ct => RunItemizeGuardedAsync(ct, applyRegex: false, sweepYellows: true),
            failureLabel: "Bestiary yellow itemize",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepBestiary = true, KeepInventory = true },
            cancelledStatus: "Bestiary yellow itemize cancelled.",
            isBestiaryClearRunning: true);

    // Itemizing only works while standing in the Menagerie.
    private async Task<ClearResult> RunItemizeGuardedAsync(CancellationToken ct, bool applyRegex,
        bool sweepYellows = false)
    {
        if (!IsInMenagerie)
        {
            _runner.UpdateStatus("Bestiary itemize: traveling to Menagerie...");
            if (!await _hideoutTravel.TravelViaChatAsync("/menagerie", () => IsInMenagerie, "Menagerie", ct))
            {
                _runner.UpdateStatus(
                    $"Bestiary itemize stopped: could not reach the Menagerie (still in '{_hideoutTravel.CurrentAreaName}').");
                return new ClearResult(0, 0);
            }
        }

        return await RunClearAsync(ct, deleteMode: false, applyRegex, sweepYellows: sweepYellows);
    }

    private bool IsInMenagerie =>
        string.Equals(_hideoutTravel.CurrentAreaName, GameHelpers.MenagerieAreaName, StringComparison.OrdinalIgnoreCase);

    // Rows processed by a pass, and how many still match the filter.
    public readonly record struct ClearResult(int Processed, int Remaining);

    // Runs one clear pass. autoStashOnFull false ends the pass on a full inventory instead
    // of stashing; maxToProcess stops early after that many rows.
    public Task<ClearResult> RunClearBodyAsync(CancellationToken ct, bool deleteMode, bool applyRegex,
        bool autoStashOnFull = true, int? maxToProcess = null, bool sweepYellows = false) =>
        RunClearAsync(ct, deleteMode, applyRegex, autoStashOnFull, maxToProcess, sweepYellows);

    // ---- core loop -----------------------------------------------------

    // Opens the tab, applies the filter and clicks rows in batches until none remain.
    private async Task<ClearResult> RunClearAsync(CancellationToken ct, bool deleteMode, bool applyRegex,
        bool autoStashOnFull = true, int? maxToProcess = null, bool sweepYellows = false)
    {
        await EnsureCapturedBeastsPanelOpenAsync(ct);
        ct.ThrowIfCancellationRequested();

        _activeRegexForResume = string.Empty;
        _clearFilterForResume = sweepYellows;

        // The yellow gate lives in code, so the game's search must not narrow the list first.
        if (sweepYellows)
        {
            await ClearFilterAsync(ct);
        }
        else if (applyRegex)
        {
            var regex = _clipboard.BuildRegex();
            if (string.IsNullOrWhiteSpace(regex))
            {
                _runner.UpdateStatus("Bestiary regex is empty. Enable at least one tracked beast or set a manual regex.");
                return new ClearResult(0, 0);
            }
            _activeRegexForResume = regex;
            await ApplyRegexAsync(regex, ct);
        }

        await WaitForRowsToSettleAsync();

        // Second gate, in code, over the game's search: the Bestiary filter also matches rare
        // names, mods and recipes, so short regex fragments let untracked beasts through - and
        // those have no price and Faustus will not take them. Only applied when a regex is used.
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eligible = sweepYellows
            ? BuildYellowFilter(skipped)
            : BuildEligibilityFilter(applyRegex, skipped);

        // Held for the whole pass in both modes.
        _input.ReleaseKeys(Keys.LControlKey, Keys.RControlKey);
        _input.PressKeyDown(Keys.LControlKey);

        // Counted from how far the match count has fallen, not accumulated per batch.
        var released = 0;
        int? baselineRemaining = null;
        var stallCount = 0;
        var scroll = new ScrollState(sweepYellows ? MaxScrollsBetweenSweepBatches : MaxScrollsBetweenBatches);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (!_bestiaryUi.IsCapturedBeastsTabReady)
                    throw new InvalidOperationException("Captured Beasts tab is not visible while clearing.");

                // Phase timers: each batch reports where its time actually went.
                var batchSw = Stopwatch.StartNew();

                // A confirmation dialog blocks later clicks, so clear it first.
                var dialogSw = Stopwatch.StartNew();
                var dismissed = await TryDismissDestroyConfirmationAsync(ct);
                dialogSw.Stop();
                if (dismissed) continue;

                var scanSw = Stopwatch.StartNew();
                var scan = ScanViewport(eligible);
                scanSw.Stop();
                baselineRemaining ??= scan.Remaining;
                released = Math.Max(released, baselineRemaining.Value - scan.Remaining);

                if (scan.Remaining == 0)
                {
                    await FinishAsync(deleteMode, released, autoStashOnFull, skipped, sweepYellows, ct);
                    return new ClearResult(released, 0);
                }

                // Caller's cap reached. Left unfinished on purpose - it comes back once it has room.
                if (maxToProcess.HasValue && released >= maxToProcess.Value)
                {
                    Log.Debug($"Bestiary clear stopping at the {maxToProcess.Value}-beast cap ({scan.Remaining} still matching).");
                    return new ClearResult(released, scan.Remaining);
                }

                // Itemize only: inventory is full.
                if (!deleteMode && _inventoryUi.FreeCellCount() <= 0)
                {
                    var stopped = await MakeRoomForMoreBeastsAsync(released, scan.Remaining, autoStashOnFull, ct);
                    if (stopped.HasValue) return stopped.Value;
                    continue;
                }

                if (scan.Rows.Count == 0)
                {
                    stallCount++;
                    if (stallCount == 1)
                    {
                        await _input.DelayAsync(_settings.Timing.Polling.FastPollDelayMs.Value);
                        continue;
                    }

                    // Untracked rows are walling off the viewport: they pass the game's filter so they are
                    // never clicked and never reflow away, and the grid shows only ~6 cells.
                    if (scroll.CanScroll && (scan.Blocked > 0 || scroll.ScrolledDown > 0))
                    {
                        await ScrollPastBlockedRowsAsync(scan, scroll, ct);

                        // Back to "confirmed empty once" so the next frame can scroll without re-polling.
                        stallCount = 1;
                        continue;
                    }

                    // Matches exist but none are in the viewport: mid-reflow or a stall.
                    if (stallCount >= MaxConsecutiveStalls)
                        throw new InvalidOperationException(StalledWithNoClickableRowsMessage(scan, skipped, sweepYellows));

                    await _input.DelayAsync(_settings.Timing.Polling.FastPollDelayMs.Value);
                    continue;
                }

                if (sweepYellows && scroll.CanScroll)
                {
                    scan = await GatherMoreRowsAsync(scan, scroll, eligible, ct);

                    // Everything eligible scrolled off the top; let the next pass re-read.
                    if (scan.Rows.Count == 0) continue;
                }

                // Capped to free inventory cells when itemizing, and to whatever the caller's cap has left.
                var batchSize = deleteMode
                    ? scan.Rows.Count
                    : Math.Min(scan.Rows.Count, Math.Max(0, _inventoryUi.FreeCellCount()));
                if (maxToProcess.HasValue)
                    batchSize = Math.Min(batchSize, Math.Max(0, maxToProcess.Value - released));
                if (batchSize <= 0) continue;

                var startingRemaining = scan.Remaining;
                var startingFree = deleteMode ? 0 : _inventoryUi.FreeCellCount();

                _runner.UpdateStatus(
                    $"Bestiary {(deleteMode ? "deleting" : "itemizing")} batch of {batchSize} ({scan.Remaining} left)... total processed: {released}");

                var clickSw = Stopwatch.StartNew();
                var clicked = await ClickBatchAsync(scan, batchSize, deleteMode, eligible, ct);
                clickSw.Stop();

                if (clicked == 0)
                {
                    if (++stallCount >= MaxConsecutiveStalls)
                        throw new InvalidOperationException("Bestiary clear stalled - no row could be clicked.");
                    await _input.DelayAsync(_settings.Timing.Polling.FastPollDelayMs.Value);
                    continue;
                }

                var waitStats = new WaitStats();
                var releasedThisBatch = await WaitForReleaseAsync(
                    startingRemaining, startingFree, clicked, deleteMode, eligible, ct, waitStats);

                var recheckMs = 0L;
                if (releasedThisBatch <= 0)
                {
                    // Re-reads after a short settle before treating the batch as a stall.
                    var recheckSw = Stopwatch.StartNew();
                    await _input.DelayForUiCheckAsync(_settings.Timing.Polling.UiCheckInitialSettleDelayMs.Value);
                    // Counted like startingRemaining, or the eligibility filter inflates every batch.
                    releasedThisBatch = Math.Max(0, startingRemaining - _bestiaryUi.MatchingCount(eligible));
                    recheckMs = recheckSw.ElapsedMilliseconds;
                }

                batchSw.Stop();

                // One line per batch naming every phase: row scan vs clicks vs waiting on the game.
                Log.Debug(
                    $"Bestiary batch: total={batchSw.ElapsedMilliseconds}ms " +
                    $"[dialog={dialogSw.ElapsedMilliseconds} scan={scanSw.ElapsedMilliseconds}({scan.Remaining} match) " +
                    $"clicks={clickSw.ElapsedMilliseconds}({clicked}/{batchSize} sent) " +
                    $"wait={waitStats.ElapsedMs}({waitStats.Outcome}, released {waitStats.Released}/{clicked}, " +
                    $"{waitStats.Ticks} ticks, cap {waitStats.TimeoutMs}ms)" +
                    (recheckMs > 0 ? $" recheck={recheckMs}" : string.Empty) + "]");

                if (releasedThisBatch <= 0)
                {
                    if (++stallCount >= MaxConsecutiveStalls)
                        throw new InvalidOperationException("Bestiary clear stalled while releasing captured beasts.");
                    continue;
                }

                stallCount = 0;
                scroll.ResetBudget();
            }
        }
        finally
        {
            _input.PressKeyUp(Keys.LControlKey);

            // In the finally so it survives the stall throws and cancellation.
            LogSkipped(skipped, sweepYellows);
        }
    }

    // ---- core loop: viewport state ---------------------------------------

    // One read of the grid: the rows this pass may click, how many rows still match, how many
    // of the matching ones are ineligible but occupying the viewport, and the viewport rect.
    private sealed class ViewportScan
    {
        public List<CapturedBeast> Rows;
        public int Remaining;
        public int Blocked;
        public RectangleF Viewport;
    }

    private ViewportScan ScanViewport(Func<CapturedBeast, bool> eligible)
    {
        // One pass yields the clickable rows, the remaining match count and the viewport rect;
        // asking separately re-walked 850+ rows twice per batch.
        var rows = _bestiaryUi.ClickableBeasts(eligible, out var remaining, out var blocked, out var viewport);
        return new ViewportScan { Rows = rows, Remaining = remaining, Blocked = blocked, Viewport = viewport };
    }

    // Scrolling budget for one clear pass. Budget bounds the scrolling one stuck patch may do;
    // ScrolledDown is the distance from the top, so the loop can rewind.
    private sealed class ScrollState
    {
        private readonly int _maxScrolls;

        public ScrollState(int maxScrolls)
        {
            _maxScrolls = maxScrolls;
            Budget = maxScrolls;
        }

        public int Budget;
        public int ScrolledDown;

        // Ticks per downward scroll. Doubles while blocked, resets when a batch gets through.
        public int Step = 1;

        // Set when a downward scroll stops moving the grid, so the pass rewinds.
        public bool AtBottom;
        public int NoMoveSamples;

        public bool CanScroll => Budget > 0;

        // Refilled only by a batch that released something, so scrolling can never replace progress.
        public void ResetBudget()
        {
            Budget = _maxScrolls;
            Step = 1;
            AtBottom = false;
        }
    }

    // ---- core loop: steps ------------------------------------------------

    // Handles a full inventory mid-itemize: auto-stashes and reopens the panel, or returns the
    // result the pass should stop on.
    private async Task<ClearResult?> MakeRoomForMoreBeastsAsync(
        int released, int remaining, bool autoStashOnFull, CancellationToken ct)
    {
        if (!autoStashOnFull)
            return new ClearResult(released, remaining);

        if (!CanAutoStash())
        {
            _runner.UpdateStatus(
                $"Inventory full - itemized {released} beast{ImGuiEx.PluralSuffix(released)}. Set Automation: Bestiary -> Itemized Beasts Stash Tab + enable Auto-Stash After Itemize to continue past this.");
            return new ClearResult(released, remaining);
        }

        // Nothing itemized yet to move out of the way.
        if (_inventoryUi.VisibleCapturedMonsters().Count == 0)
        {
            throw new InvalidOperationException(
                "Inventory is full of non-beast items, so there is nothing itemized yet to auto-stash out " +
                "of the way. Free up inventory space (sell, stash or drop other items) and re-run." +
                (released > 0
                    ? $" Itemized {released} beast{ImGuiEx.PluralSuffix(released)} so far."
                    : string.Empty));
        }

        // Dropped around the stash pass, where Ctrl-click means "transfer".
        _input.PressKeyUp(Keys.LControlKey);
        var movedToStash = 0;
        try
        {
            movedToStash = await _capturedMonsterStash.StashAllAsync(ct);
            await ReopenPanelAndReapplyRegexAsync(ct);
        }
        finally
        {
            _input.PressKeyDown(Keys.LControlKey);
        }

        if (_inventoryUi.FreeCellCount() <= 0)
            throw new InvalidOperationException(
                movedToStash == 0
                    ? "Auto-stash moved nothing and inventory is still full. Free up inventory space and re-run."
                    : "Inventory is still full after auto-stashing itemized beasts.");

        return null;
    }

    // Scrolls past a patch of ineligible rows, or rewinds to the top when scrolling is not what
    // the pass needs. Spends `scroll` budget either way.
    private async Task ScrollPastBlockedRowsAsync(ViewportScan scan, ScrollState scroll, CancellationToken ct)
    {
        // Blocked rows mean "keep going down", but only while that still moves the grid.
        // Rewinding to the top throws away every notch, so it is kept for the two cases that
        // need it: the view is clear with nothing to click, or the list will not scroll further.
        if (scan.Blocked > 0 && !scroll.AtBottom)
        {
            // Landing on more of the same is evidence of a long patch, so the step doubles rather
            // than crawling. Nothing is lost: clearing the patch rewinds to the top and rescans.
            var step = Math.Min(scroll.Step, scroll.Budget);
            scroll.Budget -= step;
            scroll.ScrolledDown += step;
            scroll.Step = Math.Min(scroll.Step * 2, MaxScrollStepTicks);

            var offsetBefore = _bestiaryUi.ScrollOffsetPixels;
            await ScrollViewportAsync(scan.Viewport, -step, ct);
            var offsetAfter = _bestiaryUi.ScrollOffsetPixels;

            // A wheel that no longer moves the grid means the end of the list.
            var moved = offsetBefore < 0f || offsetAfter < 0f ||
                        offsetAfter > offsetBefore + ScrollMovedEpsilonPixels;
            if (moved)
            {
                scroll.NoMoveSamples = 0;
                return;
            }

            if (++scroll.NoMoveSamples >= NoMoveSamplesForBottom)
            {
                scroll.AtBottom = true;
                Log.Debug(
                    $"Bestiary scroll is at the bottom ({offsetAfter:0}px after {scroll.ScrolledDown} ticks); " +
                    "rewinding to the top for anything left above.");
            }
            return;
        }

        Log.Debug(
            $"Bestiary rewinding {scroll.ScrolledDown} scroll tick{ImGuiEx.PluralSuffix(scroll.ScrolledDown)} to the top " +
            $"({(scroll.AtBottom ? "end of list" : "viewport clear")}, {scan.Remaining} still matching).");

        scroll.Budget--;
        await ScrollViewportAsync(scan.Viewport, scroll.ScrolledDown + ScrollBackOvershoot, ct);
        scroll.ScrolledDown = 0;
        scroll.Step = 1;
        scroll.AtBottom = false;
        scroll.NoMoveSamples = 0;
    }

    // A sparse viewport is worth widening first. In a sweep an eligible row often sits alone,
    // and clicking it alone costs a full row scan plus a release wait. One notch trades the
    // top line for a new one, so two notches that add nothing mean the list is simply sparse.
    private async Task<ViewportScan> GatherMoreRowsAsync(
        ViewportScan scan, ScrollState scroll, Func<CapturedBeast, bool> eligible, CancellationToken ct)
    {
        var gatherTarget = Math.Min(GatherTargetCells, Math.Max(1, _inventoryUi.FreeCellCount()));
        var barren = 0;

        for (var gather = 0;
             gather < MaxGatherScrolls && scan.Rows.Count < gatherTarget &&
             scroll.CanScroll && barren < MaxBarrenGatherScrolls;
             gather++)
        {
            ct.ThrowIfCancellationRequested();

            var before = scan.Rows.Count;
            var held = RowAddresses(scan.Rows);

            scroll.Budget--;
            scroll.ScrolledDown++;
            await ScrollViewportAsync(scan.Viewport, -1, ct);

            var next = ScanViewport(eligible);

            // A notch that pushes an already-found row off the top is a bad trade - the pass is
            // heading down and nothing comes back for it. Checked by address, since row counts stay
            // the same as three cells leave the top and three arrive at the bottom.
            if (!HoldsAll(next.Rows, held))
            {
                scroll.Budget--;
                scroll.ScrolledDown = Math.Max(0, scroll.ScrolledDown - 1);
                await ScrollViewportAsync(next.Viewport, 1, ct);
                return ScanViewport(eligible);
            }

            scan = next;
            barren = scan.Rows.Count > before ? 0 : barren + 1;
        }

        return scan;
    }

    // Clicks a batch bottom-up so removals do not reflow the rows still queued.
    private async Task<int> ClickBatchAsync(
        ViewportScan scan, int batchSize, bool deleteMode, Func<CapturedBeast, bool> eligible, CancellationToken ct)
    {
        var clicked = 0;
        for (var i = batchSize - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            if (await ClickBeastAsync(scan.Rows[i], scan.Viewport, deleteMode, eligible, ct)) clicked++;
        }
        return clicked;
    }

    private static string StalledWithNoClickableRowsMessage(ViewportScan scan, HashSet<string> skipped, bool sweepYellows) =>
        $"{scan.Remaining} matching beast{ImGuiEx.PluralSuffix(scan.Remaining)} remain but none are in the viewport" +
        (scan.Blocked > 0
            ? $" ({scan.Blocked} {(sweepYellows ? "catalogd" : "untracked")} beast{ImGuiEx.PluralSuffix(scan.Blocked)} covering it, and scrolling did not clear them" +
              (skipped.Count > 0 ? $": {string.Join(", ", skipped.Take(6))}" : string.Empty) + ")."
            : ".");

    // ---- eligibility -----------------------------------------------------

    // The per-row gate for a regex run, or null to accept every matching row.
    private Func<CapturedBeast, bool> BuildEligibilityFilter(bool applyRegex, HashSet<string> skipped)
    {
        if (!applyRegex) return null;
        if (!_settings.BestiaryAutomation.OnlyItemizeTrackedBeasts.Value) return null;

        var tracked = TrackedNames();

        // Never throws: Name reads game memory and a row can go stale under it. An unreadable
        // row counts as untracked - at worst one beast left behind, instead of a wasted slot.
        return beast =>
        {
            if (!TryReadName(beast, out var trimmed)) return false;

            if (IsTrackedName(tracked, trimmed)) return true;

            skipped.Add(trimmed);
            return false;
        };
    }


    // The beast names counted as tracked, or null to mean "the whole catalog".
    private ICollection<string> TrackedNames()
    {
        var enabled = _settings.BestiaryClipboard.UseAutoRegex.Value
            ? _settings.BeastPrices.EnabledBeasts
            : null;

        // An empty enabled list would reject everything, so fall back to the catalog.
        return enabled is { Count: > 0 } ? enabled : null;
    }

    private static bool IsTrackedName(ICollection<string> tracked, string name) =>
        tracked != null ? tracked.Contains(name) : BeastCatalog.IsTracked(name);

    // Reads a row's species name. False when missing or stale, which callers treat as skip.
    private static bool TryReadName(CapturedBeast beast, out string trimmed)
    {
        trimmed = null;
        try
        {
            var name = beast?.Name;
            if (string.IsNullOrWhiteSpace(name)) return false;
            trimmed = name.Trim();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not read a captured beast's name ({ex.GetType().Name}); leaving it in the Bestiary.");
            return false;
        }
    }

    // The per-row gate for a yellow run: every beast not in BeastCatalog. Uses the catalog
    // and not the enabled Beast Prices list, which changes with what is worth listing and
    // would start sweeping beasts the plugin can price. Unreadable rows are left alone.
    private static Func<CapturedBeast, bool> BuildYellowFilter(HashSet<string> skipped)
    {
        return beast =>
        {
            if (!TryReadName(beast, out var trimmed)) return false;

            if (!BeastCatalog.IsTracked(trimmed)) return true;

            skipped.Add(trimmed);
            return false;
        };
    }

    // The only place that says which beasts the search regex is letting through, which is
    // what tells you a fragment needs tightening.
    private static void LogSkipped(HashSet<string> skipped, bool sweepYellows)
    {
        if (skipped.Count == 0) return;

        if (sweepYellows)
        {
            Log.Debug(
                $"Bestiary yellow itemize left {skipped.Count} catalogd beast type{ImGuiEx.PluralSuffix(skipped.Count)} " +
                $"alone: {string.Join(", ", skipped.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}.");
            return;
        }

        Log.Debug(
            $"Bestiary itemize skipped {skipped.Count} untracked beast type{ImGuiEx.PluralSuffix(skipped.Count)} " +
            $"that the search regex matched: {string.Join(", ", skipped.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}.");
    }

    // The rows' element addresses, for checking a scroll did not shed any of them.
    private static HashSet<long> RowAddresses(List<CapturedBeast> rows)
    {
        var addresses = new HashSet<long>(rows.Count);
        foreach (var row in rows)
        {
            if (row != null) addresses.Add(row.Address);
        }
        return addresses;
    }

    // True when every address is still among `rows`.
    private static bool HoldsAll(List<CapturedBeast> rows, HashSet<long> addresses)
    {
        if (addresses.Count == 0) return true;

        var found = 0;
        foreach (var row in rows)
        {
            if (row != null && addresses.Contains(row.Address)) found++;
        }
        return found >= addresses.Count;
    }

    // Scrolls the beast grid. Negative ticks scroll down, positive up. Ctrl stays held over
    // the wheel: releasing it cost a modifier edge the game read as the itemize chord ending.
    private async Task ScrollViewportAsync(RectangleF viewport, int ticks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ticks == 0 || viewport.Width <= 0 || viewport.Height <= 0) return;

        // The wheel goes to whatever is under the cursor, which is already a row after a batch.
        // Re-aiming at the center would trace a humanized path on every scroll of a stuck patch.
        if (!IsCursorInside(viewport))
            await _input.MoveCursorToAsync(viewport);

        _input.ScrollWheel(ticks);
        await _input.DelayForUiCheckAsync(_settings.Timing.Polling.UiCheckInitialSettleDelayMs.Value);
    }

    // True when the cursor is well inside the rect, with a margin off the grid edge.
    private bool IsCursorInside(RectangleF rect)
    {
        var cursor = _input.CursorPosition;
        const float margin = 4f;
        return cursor.X >= rect.Left + margin && cursor.X <= rect.Right - margin &&
               cursor.Y >= rect.Top + margin && cursor.Y <= rect.Bottom - margin;
    }

    private bool CanAutoStash() =>
        _settings.BestiaryAutomation.AutoStashAfterItemize.Value &&
        _settings.BestiaryAutomation.ItemizedBeastTabs
            .Exists(tab => !string.IsNullOrWhiteSpace(tab));

    // Releases held keys and auto-stashes itemized beasts when configured.
    private async Task FinishAsync(bool deleteMode, int released, bool autoStashOnFull,
        HashSet<string> skipped, bool sweepYellows, CancellationToken ct)
    {
        if (!deleteMode && autoStashOnFull && released > 0 && CanAutoStash() &&
            _inventoryUi.VisibleCapturedMonsters().Count > 0)
        {
            // Dropped around the stash pass, where Ctrl-click means "transfer".
            _input.PressKeyUp(Keys.LControlKey);
            try { await _capturedMonsterStash.StashAllAsync(ct); }
            catch (InvalidOperationException ex) { Log.Debug($"Post-itemize auto-stash skipped: {ex.Message}"); }
            finally { _input.PressKeyDown(Keys.LControlKey); }
        }

        if (released > 0)
        {
            if (sweepYellows)
            {
                _runner.UpdateStatus(
                    $"Bestiary yellow itemize complete. Itemized {released} beast{ImGuiEx.PluralSuffix(released)}." +
                    (skipped.Count > 0
                        ? $" Left {skipped.Count} catalogd beast type{ImGuiEx.PluralSuffix(skipped.Count)} in the Bestiary."
                        : string.Empty));
                return;
            }

            _runner.UpdateStatus(
                $"Bestiary {(deleteMode ? "delete" : "itemize")} complete. Processed {released} beast{ImGuiEx.PluralSuffix(released)}." +
                (skipped.Count > 0
                    ? $" Skipped {skipped.Count} untracked beast type{ImGuiEx.PluralSuffix(skipped.Count)} the search also matched."
                    : string.Empty));
            return;
        }

        if (sweepYellows)
        {
            _runner.UpdateStatus(
                skipped.Count > 0
                    ? $"Bestiary yellow itemize complete. Nothing to itemize - all {skipped.Count} " +
                      $"captured beast type{ImGuiEx.PluralSuffix(skipped.Count)} are in the beast catalog."
                    : "Bestiary yellow itemize complete. No captured beasts were found.");
            return;
        }

        // "Nothing found" would be a lie when rows matched and the tracked-only gate emptied them.
        _runner.UpdateStatus(
            skipped.Count > 0
                ? $"Bestiary clear complete. Nothing itemized: every beast the search matched is untracked " +
                  $"({string.Join(", ", skipped.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(4))}" +
                  $"{(skipped.Count > 4 ? ", ..." : string.Empty)}). Add them under Beast Prices, or turn off " +
                  "Automation: Bestiary -> Only Itemize Tracked Beasts."
                : "Bestiary clear complete. No matching captured beasts were found.");
    }

    // ---- clicking ------------------------------------------------------

    // Clicks one row to itemize or release it; false when the row was skipped.
    private async Task<bool> ClickBeastAsync(CapturedBeast beast, RectangleF viewport, bool deleteMode,
        Func<CapturedBeast, bool> eligible, CancellationToken ct)
    {
        if (beast?.IsVisible != true) return false;

        var rect = beast.GetClientRect();
        if (rect.Width < 16 || rect.Height < 16) return false;

        // Re-checked against the viewport, since earlier clicks reflow the grid.
        if (!ImGuiEx.IsRectMostlyInside(rect, viewport, 0.9f)) return false;

        // Rows are windows onto game memory, so a removal earlier in the batch can leave this
        // one pointing at a different beast than the scan classified. Last gate before a slot goes.
        if (eligible != null && !eligible(beast))
        {
            Log.Debug("Skipped a beast at click time - not tracked (the row changed under us after the scan).");
            return false;
        }

        var timing = _settings.Timing;
        var floor = timing.Clicks.BestiaryClickDelayMs.Value;

        if (!deleteMode)
        {
            // Ctrl is already held; clicking the row body itemizes it.
            await _input.ClickAtAsync(
                rect,
                MouseButtons.Left,
                preDelayMs: timing.Clicks.BestiaryItemizePreDelayMs.Value,
                postDelayMs: Math.Max(timing.Clicks.BestiaryItemizePostDelayMs.Value, floor));
            return true;
        }

        // The release button only renders while hovered, so the hover is confirmed first.
        var releaseButton = _bestiaryUi.TryGetReleaseButton(beast);
        if (releaseButton == null) return false;

        var buttonRect = releaseButton.GetClientRect();
        if (buttonRect.Width <= 0 || buttonRect.Height <= 0) return false;

        await _input.MoveCursorToAsync(buttonRect);

        var hovered = await _waits.WaitForAsync(
            () => _bestiaryUi.IsHoveringReleaseButton(releaseButton),
            timeoutMs: Math.Max(120, timing.Clicks.UiClickPreDelayMs.Value + timing.Polling.FastPollDelayMs.Value * 4),
            pollDelayMs: Math.Max(5, timing.Polling.FastPollDelayMs.Value));

        if (!hovered)
        {
            Log.Debug($"Skipped beast '{beast.Name}' - release button hover was not registered.");
            return false;
        }

        // Ctrl is already held; it confirms the release and suppresses the destroy dialog.
        await _input.ClickAsync(
            MouseButtons.Left,
            preDelayMs: timing.Clicks.CtrlClickPreDelayMs.Value,
            postDelayMs: Math.Max(timing.Clicks.CtrlClickPostDelayMs.Value, floor));

        // Clears the dialog if it appeared anyway.
        await TryDismissDestroyConfirmationAsync(ct);
        return true;
    }

    // Dismisses the destroy-confirmation dialog; false when none was present.
    private async Task<bool> TryDismissDestroyConfirmationAsync(CancellationToken ct)
    {
        if (!_bestiaryUi.IsDestroyConfirmationVisible) return false;

        ct.ThrowIfCancellationRequested();
        var button = _bestiaryUi.TryGetDestroyConfirmationButton();

        // Presence is tested by rect; IsVisible is unreliable in this panel.
        var rect = button?.GetClientRect() ?? default;
        if (rect.Width <= 8 || rect.Height <= 8)
        {
            throw new InvalidOperationException(
                "A destroy-confirmation dialog is open and its confirm button could not be found. Close it manually and re-run.");
        }

        var timing = _settings.Timing;
        Log.Debug("Dismissing Bestiary destroy-confirmation dialog.");

        await _input.ClickAtAsync(
            rect,
            MouseButtons.Left,
            preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
            postDelayMs: timing.Clicks.UiClickPostDelayMs.Value);

        await _waits.WaitForAsync(
            () => !_bestiaryUi.IsDestroyConfirmationVisible,
            timeoutMs: PanelWaitTimeoutMs,
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);
        return true;
    }

    // ---- release wait --------------------------------------------------

    // Why the release wait returned.
    private enum WaitOutcome
    {
        // Every click produced a release. The normal, fast path.
        Satisfied,

        // Progress stopped for SettleAfterChangeMs with fewer releases than clicks.
        Settled,

        // Nothing ever moved, or movement never stopped, until the timeout expired.
        TimedOut,
    }

    private async Task<int> WaitForReleaseAsync(int startingRemaining, int startingFree, int clickedCount,
        bool deleteMode, Func<CapturedBeast, bool> eligible, CancellationToken ct, WaitStats stats = null)
    {
        var timing = _settings.Timing;
        var baseTimeout = _input.ScaleTimeout(timing.Timeouts.BestiaryReleaseTimeoutMs.Value);
        var perClickPad = Math.Max(75, baseTimeout / 2);
        var timeout = baseTimeout + Math.Max(0, clickedCount - 1) * perClickPad;
        var pollDelay = Math.Max(1, timing.Polling.BestiaryReleasePollDelayMs.Value);

        var sw = Stopwatch.StartNew();
        long? lastChangeMs = null;
        var maxReleased = 0;
        var outcome = WaitOutcome.TimedOut;
        var ticks = 0;

        while (sw.ElapsedMilliseconds < timeout)
        {
            ticks++;
            ct.ThrowIfCancellationRequested();

            // Itemize polls inventory instead of the row list: MatchingCount walks every row (850+
            // on a full account) out of process memory, and an itemized beast always frees exactly
            // one cell. Delete has no inventory side effect, so it still reads the list.
            var effective = deleteMode
                ? Math.Max(0, startingRemaining - _bestiaryUi.MatchingCount(eligible))
                : Math.Max(0, startingFree - _inventoryUi.FreeCellCount());
            if (effective > maxReleased)
            {
                maxReleased = effective;
                lastChangeMs = sw.ElapsedMilliseconds;
            }

            if (maxReleased >= clickedCount)
            {
                outcome = WaitOutcome.Satisfied;
                break;
            }

            if (lastChangeMs.HasValue && sw.ElapsedMilliseconds - lastChangeMs.Value >= SettleAfterChangeMs)
            {
                outcome = WaitOutcome.Settled;
                break;
            }

            await _input.DelayAsync(pollDelay);
        }

        if (stats != null)
        {
            stats.ElapsedMs = sw.ElapsedMilliseconds;
            stats.Outcome = outcome;
            stats.TimeoutMs = timeout;
            stats.Ticks = ticks;
            stats.Released = maxReleased;
        }

        return maxReleased;
    }

    // Filled in by WaitForReleaseAsync so the caller can report the whole batch in one line.
    private sealed class WaitStats
    {
        public long ElapsedMs;
        public WaitOutcome Outcome;
        public int TimeoutMs;
        public int Ticks;
        public int Released;
    }

    // Waits until loading finishes and the row count holds steady; false on timeout.
    private async Task<bool> WaitForRowsToSettleAsync()
    {
        var timing = _settings.Timing;
        var pollDelay = Math.Max(10, timing.Polling.FastPollDelayMs.Value);

        var sw = Stopwatch.StartNew();
        var lastCount = -1;
        var steadySince = 0L;

        while (sw.ElapsedMilliseconds < RowSettleTimeoutMs)
        {
            var count = _bestiaryUi.TotalBeastCount();
            if (count != lastCount)
            {
                lastCount = count;
                steadySince = sw.ElapsedMilliseconds;
            }
            else if (!_bestiaryUi.IsLoading && sw.ElapsedMilliseconds - steadySince >= RowsStableMs)
            {
                await _input.DelayForUiCheckAsync(timing.Polling.UiCheckInitialSettleDelayMs.Value);
                return true;
            }

            await _input.DelayAsync(pollDelay);
        }

        Log.Debug($"Captured Beasts list did not settle within {RowSettleTimeoutMs}ms (rows={lastCount}, loading={_bestiaryUi.IsLoading}).");
        await _input.DelayForUiCheckAsync(timing.Polling.UiCheckInitialSettleDelayMs.Value);
        return false;
    }

    // ---- panel open ----------------------------------------------------

    // Reopens the Bestiary panel after an auto-stash and restores the run's search state.
    private async Task ReopenPanelAndReapplyRegexAsync(CancellationToken ct)
    {
        await EnsureCapturedBeastsPanelOpenAsync(ct);

        if (_clearFilterForResume)
            await ClearFilterAsync(ct);
        else if (!string.IsNullOrWhiteSpace(_activeRegexForResume))
            await ApplyRegexAsync(_activeRegexForResume, ct);

        await WaitForRowsToSettleAsync();
    }

    private async Task EnsureCapturedBeastsPanelOpenAsync(CancellationToken ct)
    {
        if (_bestiaryUi.IsCapturedBeastsTabReady) return;

        var hotkey = _settings.BestiaryAutomation.ChallengesWindowHotkey?.Value.Key ?? Keys.None;
        if (hotkey == Keys.None)
        {
            throw new InvalidOperationException(
                "Bestiary panel isn't open. Set Automation: Bestiary -> Challenges Window Hotkey to match your PoE Challenges keybind, or open the panel manually first.");
        }

        _runner.UpdateStatus("Opening Challenges panel...");
        var timing = _settings.Timing;

        if (!_bestiaryUi.IsChallengesPanelVisible)
        {
            await _input.TapKeyAsync(hotkey,
                downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                postDelayMs: timing.Polling.UiCheckInitialSettleDelayMs.Value);

            var opened = await _waits.WaitForAsync(
                () => _bestiaryUi.IsChallengesPanelVisible,
                timeoutMs: PanelWaitTimeoutMs,
                pollDelayMs: timing.Polling.FastPollDelayMs.Value);

            if (!opened)
            {
                throw new InvalidOperationException(
                    "Challenges Window Hotkey did not open the Challenges panel. Verify it matches your in-game keybind.");
            }
        }

        if (!_bestiaryUi.IsCapturedBeastsTabReady &&
            !await EnsureCapturedBeastsTabSelectedAsync(ct))
        {
            throw new InvalidOperationException(
                "Challenges panel opened, but the Captured Beasts tab could not be selected.");
        }
    }

    // Clicks the Bestiary category then the Captured Beasts tab, verifying each.
    private async Task<bool> EnsureCapturedBeastsTabSelectedAsync(CancellationToken ct)
    {
        if (_bestiaryUi.IsCapturedBeastsTabReady) return true;

        Log.Debug($"Bestiary tab selection starting. ChallengesPanelVisible={_bestiaryUi.IsChallengesPanelVisible}, BestiaryTabOpen={_bestiaryUi.IsBestiaryTabOpen}");

        // The sub-tab strip only exists once the category is selected.
        if (!_bestiaryUi.IsBestiaryTabOpen &&
            !await ClickUntilAsync(_bestiaryUi.TryGetBestiaryCategoryButton,
                () => _bestiaryUi.IsBestiaryTabOpen, "Bestiary category", ct))
            return false;

        return _bestiaryUi.IsCapturedBeastsTabReady ||
               await ClickUntilAsync(_bestiaryUi.TryGetCapturedBeastsButtonToClick,
                   () => _bestiaryUi.IsCapturedBeastsTabReady, "Captured Beasts sub-tab", ct);
    }

    // Clicks a button and waits for `done`, re-resolving and retrying on failure.
    // Targets are found by rect; IsVisible is unreliable for these buttons.
    private async Task<bool> ClickUntilAsync(Func<Element> resolve, Func<bool> done, string label, CancellationToken ct)
    {
        var timing = _settings.Timing;

        for (var attempt = 1; attempt <= TabClickAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (done()) return true;

            await _waits.WaitForAsync(() => HasUsableRect(resolve()), PanelWaitTimeoutMs,
                timing.Polling.FastPollDelayMs.Value);

            var target = resolve();
            if (!HasUsableRect(target))
            {
                Log.Debug($"{label}: no usable rect on attempt {attempt}/{TabClickAttempts}.");
                continue;
            }

            var rect = target.GetClientRect();
            await _input.ClickAtAsync(
                rect,
                MouseButtons.Left,
                preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
                postDelayMs: Math.Max(timing.Clicks.UiClickPostDelayMs.Value, timing.Polling.TabSwitchDelayMs.Value));

            if (await _waits.WaitForAsync(done, TabVerifyTimeoutMs, timing.Polling.FastPollDelayMs.Value))
            {
                Log.Debug($"{label} selected on attempt {attempt}.");
                return true;
            }

            Log.Debug($"{label}: clicked ({rect.Center.X:0}, {rect.Center.Y:0}) on attempt {attempt}/{TabClickAttempts}, no state change.");
        }

        Log.Debug($"{label}: gave up after {TabClickAttempts} attempts.");
        return false;
    }

    private static bool HasUsableRect(Element element)
    {
        var rect = element?.GetClientRect() ?? default;
        return rect.Width > 8 && rect.Height > 8;
    }

    // ---- regex paste ---------------------------------------------------

    // Pastes the regex into the filter, confirming the text landed and the filter is live.
    private async Task ApplyRegexAsync(string regex, CancellationToken ct)
    {
        _runner.UpdateStatus("Applying Bestiary regex...");

        // The field is only stable once rows have finished streaming.
        await WaitForRowsToSettleAsync();

        // Clipboard auto-paste may already have applied this regex.
        if (FilterMatches(regex) && IsListFiltered())
        {
            Log.Debug($"Bestiary regex already applied: {_bestiaryUi.MatchingCount()} of {_bestiaryUi.TotalBeastCount()} rows match.");
            return;
        }

        // Set once the probe shows the field culls rows; after that "everything matches" is real.
        var filterProvenLive = false;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (!await PasteAndCommitFilterAsync(regex, ct))
            {
                Log.Debug($"Bestiary regex paste attempt {attempt} did not reach the filter field. filter='{_bestiaryUi.FilterText}'");
                continue;
            }

            Log.Debug($"Bestiary regex attempt {attempt}: filter='{_bestiaryUi.FilterText}', matching {_bestiaryUi.MatchingCount()} of {_bestiaryUi.TotalBeastCount()} rows.");

            if (!FilterMatches(regex))
            {
                Log.Debug($"Bestiary regex was cleared while the list reloaded (attempt {attempt}).");
                continue;
            }

            if (IsListFiltered()) return;

            if (filterProvenLive)
            {
                Log.Debug($"Bestiary regex matches all {_bestiaryUi.TotalBeastCount()} captured rows; filter already proven live.");
                return;
            }

            filterProvenLive = await ProbeFilterIsLiveAsync(ct);
            Log.Debug(filterProvenLive
                ? "Bestiary filter culls rows when probed - re-applying the regex over a tab it fully matches."
                : $"Bestiary filter holds the regex but every row still matches (attempt {attempt}) - re-committing.");

            // The probe clobbered the field either way, so the next attempt re-pastes.
        }

        throw new InvalidOperationException(
            $"Bestiary regex did not take effect after 3 attempts (field reads '{_bestiaryUi.FilterText}', " +
            $"all {_bestiaryUi.TotalBeastCount()} beasts still match and the filter did not cull rows when " +
            $"probed with '{FilterLivenessProbe}'). Check that Ctrl+F focuses the beast filter in-game.");
    }

    // Pastes text into the beast filter and commits with Enter. False when it never landed,
    // so the caller can retry without having sent a stray Enter.
    private async Task<bool> PasteAndCommitFilterAsync(string text, CancellationToken ct)
    {
        var timing = _settings.Timing;

        try { ImGui.SetClipboardText(text); }
        catch (Exception ex) { Log.Debug($"Clipboard set failed: {ex.Message}"); }

        // Ctrl+F focuses, Ctrl+A selects, Ctrl+V pastes.
        _input.PressKeyDown(Keys.LControlKey);
        try
        {
            await _input.TapKeyAsync(Keys.F,
                downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                postDelayMs: timing.Polling.FastPollDelayMs.Value);
            await _input.DelayForUiCheckAsync(timing.Polling.UiCheckInitialSettleDelayMs.Value);

            await _input.TapKeyAsync(Keys.A,
                downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                postDelayMs: timing.Polling.FastPollDelayMs.Value);

            await _input.TapKeyAsync(Keys.V,
                downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                postDelayMs: timing.Polling.FastPollDelayMs.Value);
        }
        finally
        {
            // Released before Enter, which is not a Ctrl chord.
            _input.PressKeyUp(Keys.LControlKey);
        }

        await _input.DelayForUiCheckAsync(timing.Polling.UiCheckInitialSettleDelayMs.Value);

        ct.ThrowIfCancellationRequested();

        // Enter is sent only after the field is confirmed to hold the text.
        var landed = await _waits.WaitForAsync(
            () => FilterMatches(text),
            timeoutMs: PanelWaitTimeoutMs,
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        if (!landed) return false;

        await _input.TapKeyAsync(Keys.Enter,
            downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
            postDelayMs: timing.Polling.UiCheckInitialSettleDelayMs.Value);

        // Committing re-runs the match pass; wait for it before reading.
        await WaitForRowsToSettleAsync();
        return true;
    }

    // Commits a token nothing can match and checks that the list empties.
    private async Task<bool> ProbeFilterIsLiveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!await PasteAndCommitFilterAsync(FilterLivenessProbe, ct))
        {
            Log.Debug($"Bestiary filter probe did not reach the filter field. filter='{_bestiaryUi.FilterText}'");
            return false;
        }

        var culled = await _waits.WaitForAsync(
            () => _bestiaryUi.MatchingCount() == 0,
            timeoutMs: PanelWaitTimeoutMs,
            pollDelayMs: _settings.Timing.Polling.FastPollDelayMs.Value);

        Log.Debug($"Bestiary filter probe: {_bestiaryUi.MatchingCount()} of {_bestiaryUi.TotalBeastCount()} rows match '{FilterLivenessProbe}'.");
        return culled;
    }

    // Empties the beast filter so an in-code gate sees every row. Deletes the selection
    // rather than pasting an empty string, which the field does not reliably accept.
    private async Task ClearFilterAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_bestiaryUi.FilterText) && !IsListFiltered()) return;

        _runner.UpdateStatus("Clearing the Bestiary search field...");
        var timing = _settings.Timing;

        await WaitForRowsToSettleAsync();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            _input.PressKeyDown(Keys.LControlKey);
            try
            {
                await _input.TapKeyAsync(Keys.F,
                    downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                    postDelayMs: timing.Polling.FastPollDelayMs.Value);
                await _input.DelayForUiCheckAsync(timing.Polling.UiCheckInitialSettleDelayMs.Value);

                await _input.TapKeyAsync(Keys.A,
                    downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                    postDelayMs: timing.Polling.FastPollDelayMs.Value);
            }
            finally
            {
                // Released before Backspace and Enter, which are not Ctrl chords.
                _input.PressKeyUp(Keys.LControlKey);
            }

            await _input.TapKeyAsync(Keys.Back,
                downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                postDelayMs: timing.Polling.FastPollDelayMs.Value);

            await _input.TapKeyAsync(Keys.Enter,
                downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                postDelayMs: timing.Polling.UiCheckInitialSettleDelayMs.Value);

            // Committing re-runs the match pass; wait for it before reading.
            await WaitForRowsToSettleAsync();

            if (string.IsNullOrWhiteSpace(_bestiaryUi.FilterText) && !IsListFiltered())
            {
                Log.Debug($"Bestiary search cleared on attempt {attempt}; {_bestiaryUi.TotalBeastCount()} rows visible.");
                return;
            }

            Log.Debug(
                $"Bestiary search clear attempt {attempt} left '{_bestiaryUi.FilterText}' " +
                $"({_bestiaryUi.MatchingCount()} of {_bestiaryUi.TotalBeastCount()} rows matching).");
        }

        throw new InvalidOperationException(
            $"Could not clear the Bestiary search field (it still reads '{_bestiaryUi.FilterText}', " +
            $"{_bestiaryUi.MatchingCount()} of {_bestiaryUi.TotalBeastCount()} rows matching). " +
            "Clear it manually and re-run, or check that Ctrl+F focuses the beast filter in-game.");
    }

    // True when fewer rows match than the tab holds, so the filter is culling.
    private bool IsListFiltered()
    {
        var total = _bestiaryUi.TotalBeastCount();
        if (total <= 0) return true; // Nothing to cull.
        return _bestiaryUi.MatchingCount() < total;
    }

    private bool FilterMatches(string regex)
    {
        var actual = _bestiaryUi.FilterText;
        return !string.IsNullOrEmpty(actual) &&
               string.Equals(actual.Trim(), regex.Trim(), StringComparison.Ordinal);
    }

    // Hotkeys the input lock lets through so a run can be stopped mid-run.
    private Keys[] PassthroughKeys() => new[]
    {
        _settings.BestiaryAutomation.DeleteHotkey?.Value.Key ?? Keys.None,
        _settings.BestiaryAutomation.RegexItemizeHotkey?.Value.Key ?? Keys.None,
        _settings.BestiaryAutomation.YellowItemizeHotkey?.Value.Key ?? Keys.None,
    }.Where(k => k != Keys.None).ToArray();
}
