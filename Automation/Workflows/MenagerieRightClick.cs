using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore.PoEMemory.Elements.InventoryElements;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Workflows;

// Right-clicks every captured-monster item in inventory to release the beasts.
// Only runs in the Menagerie or with the Bestiary panel open.
public sealed class MenagerieRightClick
{
    // Budget for the client to report the cursor over the item just aimed at.
    private const int HoverConfirmTimeoutMs = 250;

    // Cleared for the rest of a run if the hover read turns out to be unavailable.
    private bool _hoverGateEnabled = true;

    private readonly Runner _runner;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly InventoryUi _inventory;
    private readonly StashUi _stash;
    private readonly BestiaryUi _bestiary;
    private readonly Func<bool> _isInMenagerie;

    public MenagerieRightClick(Runner runner, AutomationInput input, Waits waits, BeastsSettings settings,
        InventoryUi inventory, StashUi stash, BestiaryUi bestiary, Func<bool> isInMenagerie)
    {
        _runner = runner;
        _input = input;
        _waits = waits;
        _settings = settings;
        _inventory = inventory;
        _stash = stash;
        _bestiary = bestiary;
        _isInMenagerie = isInMenagerie;
    }

    public bool CanUse() => _isInMenagerie() || _bestiary.IsChallengesPanelVisible;

    public Task RunAsync() => _runner.QueueAsync(
        RunBodyAsync,
        failureLabel: "Right-click inventory beasts",
        passthroughKeys: Array.Empty<Keys>(),
        uiCleanupOptions: new UiCleanupOptions
        {
            SkipUiCleanup = true, KeepInventory = true, KeepBestiary = true, KeepStash = true,
        },
        cancelledStatus: "Right-click inventory beasts cancelled.");

    private async Task RunBodyAsync(CancellationToken ct)
    {
        _hoverGateEnabled = true;

        if (!CanUse())
        {
            _runner.UpdateStatus("Right-click beasts only works in The Menagerie or with the Bestiary panel open.");
            return;
        }

        var sources = AvailableSources();
        if (sources.Count == 0)
            throw new InvalidOperationException(
                "Open your inventory or a stash tab before using Right Click All Beasts.");

        var beastsAtStart = sources.Sum(source => source.Items().Count);
        if (beastsAtStart == 0)
        {
            _runner.UpdateStatus($"No captured beasts found in {Describe(sources)}.");
            return;
        }

        var clicked = 0;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            clicked += await ReleaseFromAsync(source, beastsAtStart, clicked, ct);
        }

        _runner.UpdateStatus($"Right-clicked {clicked} beast{ImGuiEx.PluralSuffix(clicked)} from {Describe(sources)}.");
    }

    // The open panels that can hold captured beasts. Inventory first: it is where beasts
    // arrive and where the quick button sits. A visible stash tab is included, so stored
    // beasts can be released without moving them back to inventory.
    private List<ReleaseSource> AvailableSources()
    {
        var sources = new List<ReleaseSource>();

        if (_inventory.IsVisible)
        {
            sources.Add(new ReleaseSource("inventory",
                () => _inventory.IsVisible,
                () => _inventory.VisibleCapturedMonsters(),
                point => _inventory.IsInsidePanel(point),
                item => _inventory.IsHoveringItem(item)));
        }

        if (_stash.IsVisible)
        {
            sources.Add(new ReleaseSource("the open stash tab",
                () => _stash.IsVisible,
                () => _stash.VisibleCapturedMonsters(),
                point => _stash.IsInsidePanel(point),
                item => _stash.IsHoveringItem(item)));
        }

        return sources;
    }

    private static string Describe(List<ReleaseSource> sources) =>
        string.Join(" and ", sources.Select(source => source.Name));

    // Clicks out every beast in one source, returning how many were released.
    private async Task<int> ReleaseFromAsync(ReleaseSource source, int beastsAtStart, int alreadyClicked,
        CancellationToken ct)
    {
        var clicked = 0;
        var stallCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // A closed panel turns "clicking beasts" into casting your right-click skill at the
            // world, so it is checked before every pass and again before every click.
            if (!source.IsVisible())
                throw new InvalidOperationException(
                    $"{Capitalize(source.Name)} closed while right-clicking beasts " +
                    $"(released {alreadyClicked + clicked} of {beastsAtStart}). Re-open it and run again.");

            var items = source.Items();
            if (items.Count == 0) return clicked;

            var previousCount = items.Count;
            // Progress is reported against the starting total, which does not shrink.
            _runner.UpdateStatus($"Right-clicking beasts in {source.Name}... {alreadyClicked + clicked}/{beastsAtStart}");

            var attempted = await ClickPassAsync(source, items, ct);

            // Checked before the settle, not after: a closed panel reports zero items, which
            // satisfies "the count went down" instantly and would credit every untouched beast.
            if (!source.IsVisible())
                throw new InvalidOperationException(
                    $"{Capitalize(source.Name)} closed while right-clicking beasts " +
                    $"(released about {alreadyClicked + clicked + attempted} of {beastsAtStart}). Re-open it and run again.");

            if (attempted == 0)
            {
                throw new InvalidOperationException(
                    $"None of the {items.Count} captured beast{ImGuiEx.PluralSuffix(items.Count)} in {source.Name} had a " +
                    "clickable on-screen position. The panel may have closed or moved mid-run.");
            }

            var timing = _settings.Timing;

            // One settle for the whole pass rather than per item. Cells do not reflow when an item
            // is consumed, so the whole visible set can be clicked before waiting.
            var afterCount = await _waits.PollAsync(
                () => source.Items().Count,
                count => count <= previousCount - attempted,
                timeoutMs: Math.Max(timing.Polling.QuantityChangeBaseDelayMs.Value,
                                    _input.ClickPostDelayFloor() + timing.Polling.QuantityChangeBaseDelayMs.Value),
                pollDelayMs: timing.Polling.FastPollDelayMs.Value);

            if (afterCount >= previousCount)
            {
                stallCount++;
                if (stallCount >= 3)
                {
                    throw new InvalidOperationException(
                        $"Right-click beasts stalled: {attempted} click(s) sent over 3 passes with " +
                        $"{previousCount} beast{ImGuiEx.PluralSuffix(previousCount)} still in {source.Name} " +
                        $"(released {alreadyClicked + clicked} of {beastsAtStart}). The clicks are landing but not " +
                        "consuming items - check that the panel is the active one.");
                }

                Log.Debug($"Right-click pass in {source.Name} made no progress. attempted={attempted}, before={previousCount}, after={afterCount}, stall={stallCount}/3");
                await _input.DelayAsync(timing.Polling.FastPollDelayMs.Value);
                continue;
            }

            stallCount = 0;
            clicked += previousCount - afterCount;
        }
    }

    private static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // A panel that can hold captured beasts, with the checks needed to click into it safely.
    private sealed record ReleaseSource(
        string Name,
        Func<bool> IsVisible,
        Func<List<NormalInventoryItem>> Items,
        Func<SharpVec2, bool> Contains,
        Func<NormalInventoryItem, bool> IsHovered);

    // Right-clicks every beast whose position can be verified, returning how many were sent.
    // Batching is safe because right-clicking an already-emptied cell does nothing.
    private async Task<int> ClickPassAsync(ReleaseSource source, List<NormalInventoryItem> items, CancellationToken ct)
    {
        var timing = _settings.Timing;
        var tally = new PassTally();
        var passSw = Stopwatch.StartNew();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            // Split so a slow pass names its own cause: reading the panel (guards) or issuing the
            // click (input delays).
            var guardSw = Stopwatch.StartNew();

            // Re-checked per click: the panel can close mid-pass, sending later clicks to the world.
            var stillOpen = source.IsVisible();
            SharpVec2 center = default;
            var usable = stillOpen && TryResolveClickPoint(source, item, out center);

            guardSw.Stop();
            tally.AddGuard(guardSw.ElapsedMilliseconds);

            if (!stillOpen)
            {
                Log.Debug($"{Capitalize(source.Name)} closed mid-pass after {tally.Attempted} click(s).");
                break;
            }

            if (!usable)
            {
                tally.Skipped++;
                continue;
            }

            var clickSw = Stopwatch.StartNew();

            // Aim, confirm, then fire - ClickAtAsync would click a fixed delay after the move.
            await _input.MoveCursorToAsync(center);

            var confirmed = !_hoverGateEnabled ||
                await _waits.WaitForAsync(() => source.IsHovered(item),
                    HoverConfirmTimeoutMs, Math.Max(1, timing.Polling.FastPollDelayMs.Value));

            if (!confirmed)
            {
                clickSw.Stop();
                tally.GuardMs += clickSw.ElapsedMilliseconds;
                tally.Unconfirmed++;
                tally.Skipped++;
                continue;
            }

            tally.HoverConfirmed++;
            await _input.ClickAsync(
                MouseButtons.Right,
                preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
                postDelayMs: timing.Clicks.UiClickPostDelayMs.Value);
            clickSw.Stop();

            tally.AddClick(clickSw.ElapsedMilliseconds);
        }

        passSw.Stop();

        if (_hoverGateEnabled && tally.HoverConfirmed == 0 && tally.Unconfirmed > 0)
        {
            _hoverGateEnabled = false;
            Log.Warn($"The hover read never confirmed any of {tally.Unconfirmed} beast(s) in {source.Name}. " +
                     "Falling back to clicking on position alone for the rest of this run.");
        }

        tally.LogSummary(source.Name, passSw.ElapsedMilliseconds);
        return tally.Attempted;
    }

    // The cell center to click, when the item's rect reads as a real cell of this source.
    private static bool TryResolveClickPoint(ReleaseSource source, NormalInventoryItem item, out SharpVec2 center)
    {
        center = default;

        var rect = item.GetClientRect();
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        center = new SharpVec2(rect.Center.X, rect.Center.Y);

        // Stops a stray click casting a skill or hitting a UI control: a stale rect reads as
        // zero, as a world position, or as panel chrome rather than a cell.
        if (source.Contains(center)) return true;

        Log.Debug($"Skipping a beast whose rect ({center.X:0}, {center.Y:0}) is outside the item cells of {source.Name}.");
        return false;
    }

    // Per-pass counters and phase timings, kept together so the summary line stays one call.
    private sealed class PassTally
    {
        public int Attempted;
        public int Skipped;
        public int Unconfirmed;
        public int HoverConfirmed;
        public long GuardMs;

        private long _clickMs;
        private long _slowestClickMs;
        private long _slowestGuardMs;

        public void AddGuard(long elapsedMs)
        {
            GuardMs += elapsedMs;
            _slowestGuardMs = Math.Max(_slowestGuardMs, elapsedMs);
        }

        public void AddClick(long elapsedMs)
        {
            _clickMs += elapsedMs;
            _slowestClickMs = Math.Max(_slowestClickMs, elapsedMs);
            Attempted++;
        }

        public void LogSummary(string sourceName, long totalMs)
        {
            var perClick = Attempted > 0 ? _clickMs / (double)Attempted : 0;
            Log.Debug(
                $"Right-click pass in {sourceName}: total={totalMs}ms " +
                $"[clicks={_clickMs}ms ({Attempted} sent, {perClick:0.#}ms avg, slowest {_slowestClickMs}ms) " +
                $"guards={GuardMs}ms (slowest {_slowestGuardMs}ms) skipped={Skipped} " +
                $"hoverConfirmed={HoverConfirmed} hoverMissed={Unconfirmed}]");
        }
    }
}
