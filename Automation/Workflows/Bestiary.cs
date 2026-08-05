using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ImGuiNET;
using RectangleF = SharpDX.RectangleF;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Workflows;

// Bestiary itemize and delete workflow.
//
// Delete ctrl-clicks each row's release button; itemize holds ctrl and clicks each row
// body, turning the beast into an inventory item. An optional regex filters the list first.
// When inventory fills mid-itemize and auto-stash is configured, the run stashes, reopens
// the panel and continues.
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

    private readonly Runner _runner;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly BestiaryUi _bestiaryUi;
    private readonly InventoryUi _inventoryUi;
    private readonly ClipboardAutoPaste _clipboard;
    private readonly CapturedMonsterStash _capturedMonsterStash;

    // Regex to reapply when the panel is reopened after an auto-stash interrupt.
    private string _activeRegexForResume = string.Empty;

    public Bestiary(Runner runner, AutomationInput input, Waits waits, BeastsSettings settings,
        BestiaryUi bestiaryUi, InventoryUi inventoryUi, ClipboardAutoPaste clipboard,
        CapturedMonsterStash capturedMonsterStash)
    {
        _runner = runner;
        _input = input;
        _waits = waits;
        _settings = settings;
        _bestiaryUi = bestiaryUi;
        _inventoryUi = inventoryUi;
        _clipboard = clipboard;
        _capturedMonsterStash = capturedMonsterStash;
    }

    // ---- entry points --------------------------------------------------

    public Task DeleteAllAsync() =>
        _runner.QueueAsync(
            ct => RunClearAsync(ct, deleteMode: true, applyRegex: false),
            failureLabel: "Bestiary delete",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepBestiary = true },
            cancelledStatus: "Bestiary delete cancelled.",
            isBestiaryClearRunning: true,
            clearBestiaryDeleteModeOverride: true);

    public Task ItemizeAllAsync() =>
        _runner.QueueAsync(
            ct => RunClearAsync(ct, deleteMode: false, applyRegex: false),
            failureLabel: "Bestiary itemize",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepBestiary = true },
            cancelledStatus: "Bestiary itemize cancelled.",
            isBestiaryClearRunning: true);

    public Task RegexItemizeAsync() =>
        _runner.QueueAsync(
            ct => RunClearAsync(ct, deleteMode: false, applyRegex: true),
            failureLabel: "Bestiary regex itemize",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepBestiary = true },
            cancelledStatus: "Bestiary regex itemize cancelled.",
            isBestiaryClearRunning: true);

    // Rows processed by a pass, and how many still match the filter.
    public readonly record struct ClearResult(int Processed, int Remaining);

    // Runs one clear pass. With autoStashOnFull false, a full inventory ends the pass
    // instead of stashing. maxToProcess stops the pass early once that many rows have gone,
    // for callers that have somewhere smaller than inventory to put them.
    public Task<ClearResult> RunClearBodyAsync(CancellationToken ct, bool deleteMode, bool applyRegex,
        bool autoStashOnFull = true, int? maxToProcess = null) =>
        RunClearAsync(ct, deleteMode, applyRegex, autoStashOnFull, maxToProcess);

    // ---- core loop -----------------------------------------------------

    // Opens the tab, applies the filter and clicks rows in batches until none remain.
    private async Task<ClearResult> RunClearAsync(CancellationToken ct, bool deleteMode, bool applyRegex,
        bool autoStashOnFull = true, int? maxToProcess = null)
    {
        await EnsureCapturedBeastsPanelOpenAsync(ct);
        ct.ThrowIfCancellationRequested();

        _activeRegexForResume = string.Empty;
        if (applyRegex)
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

        // Itemize holds Ctrl for the whole pass; delete applies it per click.
        _input.ReleaseKeys(Keys.LControlKey, Keys.RControlKey);
        var holdCtrl = !deleteMode;
        if (holdCtrl) _input.PressKeyDown(Keys.LControlKey);

        // Counted from how far the match count has fallen, not accumulated per batch.
        var released = 0;
        int? baselineRemaining = null;
        var stallCount = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (!_bestiaryUi.IsCapturedBeastsTabReady)
                    throw new InvalidOperationException("Captured Beasts tab is not visible while clearing.");

                // Phase timers. Each batch reports where its time actually went, because
                // reading it back from the gaps between status lines can only ever show the
                // total — which is how an earlier guess at the cause of these pauses went
                // wrong.
                var batchSw = Stopwatch.StartNew();

                // A confirmation dialog blocks later clicks, so clear it first.
                var dialogSw = Stopwatch.StartNew();
                var dismissed = await TryDismissDestroyConfirmationAsync(ct);
                dialogSw.Stop();
                if (dismissed) continue;

                // One pass over the row list yields both the clickable rows and how many
                // matches are left; asking separately walked 850+ rows twice per batch.
                // The viewport rect comes back from the same pass: re-reading it per click
                // re-walked the whole row list each time.
                var scanSw = Stopwatch.StartNew();
                var clickable = _bestiaryUi.ClickableBeasts(out var remaining, out var viewport);
                scanSw.Stop();
                baselineRemaining ??= remaining;
                released = Math.Max(released, baselineRemaining.Value - remaining);

                if (remaining == 0)
                {
                    await FinishAsync(deleteMode, released, holdCtrl, autoStashOnFull, ct);
                    return new ClearResult(released, 0);
                }

                // Caller-imposed cap reached. Left unfinished on purpose: the rows still match,
                // and the caller comes back for them once it has made room.
                if (maxToProcess.HasValue && released >= maxToProcess.Value)
                {
                    Log.Debug($"Bestiary clear stopping at the {maxToProcess.Value}-beast cap ({remaining} still matching).");
                    return new ClearResult(released, remaining);
                }

                // Itemize only: inventory is full.
                if (!deleteMode && _inventoryUi.FreeCellCount() <= 0)
                {
                    if (!autoStashOnFull)
                        return new ClearResult(released, remaining);

                    if (!CanAutoStash())
                    {
                        _runner.UpdateStatus(
                            $"Inventory full - itemized {released} beast{ImGuiEx.PluralSuffix(released)}. Set Automation: Bestiary -> Itemized Beasts Stash Tab + enable Auto-Stash After Itemize to continue past this.");
                        return new ClearResult(released, remaining);
                    }

                    _input.PressKeyUp(Keys.LControlKey);
                    try
                    {
                        await _capturedMonsterStash.StashAllAsync(ct);
                        await ReopenPanelAndReapplyRegexAsync(ct);
                    }
                    finally
                    {
                        if (holdCtrl) _input.PressKeyDown(Keys.LControlKey);
                    }

                    if (_inventoryUi.FreeCellCount() <= 0)
                        throw new InvalidOperationException("Inventory is still full after auto-stashing itemized beasts.");
                    continue;
                }

                if (clickable.Count == 0)
                {
                    // Matches exist but none are in the viewport: mid-reflow or a stall.
                    if (++stallCount >= MaxConsecutiveStalls)
                        throw new InvalidOperationException(
                            $"{remaining} matching beast{ImGuiEx.PluralSuffix(remaining)} remain but none are in the viewport.");

                    await _input.DelayAsync(_settings.Timing.Polling.FastPollDelayMs.Value);
                    continue;
                }

                // Batch is capped to free inventory cells when itemizing, and to whatever the
                // caller's cap has left - overshooting it would itemize beasts with nowhere to go.
                var batchSize = deleteMode
                    ? clickable.Count
                    : Math.Min(clickable.Count, Math.Max(0, _inventoryUi.FreeCellCount()));
                if (maxToProcess.HasValue)
                    batchSize = Math.Min(batchSize, Math.Max(0, maxToProcess.Value - released));
                if (batchSize <= 0) continue;

                var startingRemaining = remaining;
                var startingFree = deleteMode ? 0 : _inventoryUi.FreeCellCount();

                _runner.UpdateStatus(
                    $"Bestiary {(deleteMode ? "deleting" : "itemizing")} batch of {batchSize} ({remaining} left)... total processed: {released}");

                // Clicked bottom-up so removals do not reflow the rows still queued.
                var clickSw = Stopwatch.StartNew();
                var clicked = 0;
                for (var i = batchSize - 1; i >= 0; i--)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await ClickBeastAsync(clickable[i], viewport, deleteMode, ct)) clicked++;
                }
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
                    startingRemaining, startingFree, clicked, deleteMode, ct, waitStats);

                var recheckMs = 0L;
                if (releasedThisBatch <= 0)
                {
                    // Re-reads after a short settle before treating the batch as a stall.
                    var recheckSw = Stopwatch.StartNew();
                    await _input.DelayForUiCheckAsync(_settings.Timing.Polling.UiCheckInitialSettleDelayMs.Value);
                    releasedThisBatch = Math.Max(0, startingRemaining - _bestiaryUi.MatchingCount());
                    recheckMs = recheckSw.ElapsedMilliseconds;
                }

                batchSw.Stop();

                // One line per batch naming every phase. This is the measurement that says
                // whether a slow batch was the row scan, the clicks, or waiting on the game.
                Log.Debug(
                    $"Bestiary batch: total={batchSw.ElapsedMilliseconds}ms " +
                    $"[dialog={dialogSw.ElapsedMilliseconds} scan={scanSw.ElapsedMilliseconds}({remaining} match) " +
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
            }
        }
        finally
        {
            if (holdCtrl) _input.PressKeyUp(Keys.LControlKey);
        }
    }

    private bool CanAutoStash() =>
        _settings.BestiaryAutomation.AutoStashAfterItemize.Value &&
        _settings.BestiaryAutomation.ItemizedBeastTabs
            .Exists(tab => !string.IsNullOrWhiteSpace(tab));

    // Releases held keys and auto-stashes itemized beasts when configured.
    private async Task FinishAsync(bool deleteMode, int released, bool holdCtrl, bool autoStashOnFull, CancellationToken ct)
    {
        if (!deleteMode && autoStashOnFull && released > 0 && CanAutoStash() &&
            _inventoryUi.VisibleCapturedMonsters().Count > 0)
        {
            _input.PressKeyUp(Keys.LControlKey);
            try { await _capturedMonsterStash.StashAllAsync(ct); }
            catch (InvalidOperationException ex) { Log.Debug($"Post-itemize auto-stash skipped: {ex.Message}"); }
            finally { if (holdCtrl) _input.PressKeyDown(Keys.LControlKey); }
        }

        _runner.UpdateStatus(
            released > 0
                ? $"Bestiary {(deleteMode ? "delete" : "itemize")} complete. Processed {released} beast{ImGuiEx.PluralSuffix(released)}."
                : "Bestiary clear complete. No matching captured beasts were found.");
    }

    // ---- clicking ------------------------------------------------------

    // Clicks one row to itemize or release it; false when the row was skipped.
    // `viewport` is passed in rather than read here. BestiaryUi.ViewportRect resolves the
    // viewport by walking every row in the panel, so reading it per click cost a full
    // 700-row scan per beast — the whole of the multi-second pause this used to produce.
    // The viewport is fixed for the duration of a batch.
    private async Task<bool> ClickBeastAsync(CapturedBeast beast, RectangleF viewport, bool deleteMode,
        CancellationToken ct)
    {
        if (beast?.IsVisible != true) return false;

        var rect = beast.GetClientRect();
        if (rect.Width < 16 || rect.Height < 16) return false;

        // Re-checked against the viewport, since earlier clicks reflow the grid.
        if (!ImGuiEx.IsRectMostlyInside(rect, viewport, 0.9f)) return false;

        var timing = _settings.Timing;
        var floor = timing.Clicks.BestiaryClickDelayMs.Value;

        if (!deleteMode)
        {
            // Ctrl is already held; clicking the row body itemizes it.
            await _input.ClickAtAsync(
                new SharpVec2(rect.Center.X, rect.Center.Y),
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

        var center = new SharpVec2(buttonRect.Center.X, buttonRect.Center.Y);
        _input.MoveCursorTo(center);

        var hovered = await _waits.WaitForAsync(
            () => _bestiaryUi.IsHoveringReleaseButton(releaseButton),
            timeoutMs: Math.Max(120, timing.Clicks.UiClickPreDelayMs.Value + timing.Polling.FastPollDelayMs.Value * 4),
            pollDelayMs: Math.Max(5, timing.Polling.FastPollDelayMs.Value));

        if (!hovered)
        {
            Log.Debug($"Skipped beast '{beast.Name}' - release button hover was not registered.");
            return false;
        }

        // Ctrl on the click confirms the release and suppresses the destroy dialog.
        await _input.ClickAsync(
            MouseButtons.Left,
            preDelayMs: timing.Clicks.CtrlClickPreDelayMs.Value,
            postDelayMs: Math.Max(timing.Clicks.CtrlClickPostDelayMs.Value, floor),
            Keys.LControlKey);

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
            new SharpVec2(rect.Center.X, rect.Center.Y),
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

    // Waits for a batch's clicks to register and returns how many rows left the list.
    // Why the release wait returned, which is the difference between "the game was slow"
    // and "we asked for something that could never happen".
    private enum WaitOutcome
    {
        // Every click produced a release. The normal, fast path.
        Satisfied,

        // Progress stopped for SettleAfterChangeMs with fewer releases than clicks — some
        // clicks did not take.
        Settled,

        // Nothing ever moved, or movement never stopped, until the timeout expired.
        TimedOut,
    }

    private async Task<int> WaitForReleaseAsync(int startingRemaining, int startingFree, int clickedCount,
        bool deleteMode, CancellationToken ct, WaitStats stats = null)
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

            // Itemize polls inventory only; delete has to read the list.
            //
            // MatchingCount walks every row in the Bestiary panel — 850+ of them on a full
            // account — reading each one out of process memory, and this loop used to do
            // that on every tick. It was the single most expensive thing in the itemize
            // path. An itemized beast always consumes exactly one inventory cell, so the
            // free-cell count is an equivalent signal over a grid of ~60 slots instead.
            //
            // Delete mode has no inventory side effect, so it still reads the list.
            var effective = deleteMode
                ? Math.Max(0, startingRemaining - _bestiaryUi.MatchingCount())
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

    // Reopens the Bestiary panel after an auto-stash and reapplies the saved regex.
    private async Task ReopenPanelAndReapplyRegexAsync(CancellationToken ct)
    {
        await EnsureCapturedBeastsPanelOpenAsync(ct);
        if (!string.IsNullOrWhiteSpace(_activeRegexForResume))
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

    // Clicks the Bestiary category then the Captured Beasts tab, verifying each by the
    // state it produces.
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
                new SharpVec2(rect.Center.X, rect.Center.Y),
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

    // Pastes the regex into the filter and confirms both that the text landed and that
    // the list is actually being culled.
    private async Task ApplyRegexAsync(string regex, CancellationToken ct)
    {
        _runner.UpdateStatus("Applying Bestiary regex...");

        var timing = _settings.Timing;

        // The field is only stable once rows have finished streaming.
        await WaitForRowsToSettleAsync();

        // Clipboard auto-paste may already have applied this regex.
        if (FilterMatches(regex) && IsListFiltered())
        {
            Log.Debug($"Bestiary regex already applied: {_bestiaryUi.MatchingCount()} of {_bestiaryUi.TotalBeastCount()} rows match.");
            return;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try { ImGui.SetClipboardText(regex); }
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

            // Enter is sent only after the field is confirmed to hold the regex.
            var landed = await _waits.WaitForAsync(
                () => FilterMatches(regex),
                timeoutMs: PanelWaitTimeoutMs,
                pollDelayMs: timing.Polling.FastPollDelayMs.Value);

            if (!landed)
            {
                Log.Debug($"Bestiary regex paste attempt {attempt} did not reach the filter field. filter='{_bestiaryUi.FilterText}'");
                continue;
            }

            await _input.TapKeyAsync(Keys.Enter,
                downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                postDelayMs: timing.Polling.UiCheckInitialSettleDelayMs.Value);

            // Committing re-runs the match pass; wait for it before reading.
            await WaitForRowsToSettleAsync();

            Log.Debug($"Bestiary regex attempt {attempt}: filter='{_bestiaryUi.FilterText}', matching {_bestiaryUi.MatchingCount()} of {_bestiaryUi.TotalBeastCount()} rows.");

            if (!FilterMatches(regex))
            {
                Log.Debug($"Bestiary regex was cleared while the list reloaded (attempt {attempt}).");
                continue;
            }

            if (!IsListFiltered())
            {
                Log.Debug($"Bestiary filter holds the regex but every row still matches (attempt {attempt}) - re-committing.");
                continue;
            }

            return;
        }

        throw new InvalidOperationException(
            $"Bestiary regex did not take effect after 3 attempts (field reads '{_bestiaryUi.FilterText}', " +
            $"all {_bestiaryUi.TotalBeastCount()} beasts still match). Check that Ctrl+F focuses the beast " +
            "filter in-game, or that the regex is not matching every captured beast.");
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
    }.Where(k => k != Keys.None).ToArray();
}
