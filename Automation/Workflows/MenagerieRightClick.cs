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
    // How long to give the client to report the cursor over the item just aimed at. Generous
    // next to a frame, and only ever paid in full by an item that is not going to confirm.
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

    // The panels currently open that can hold captured beasts.
    //
    // Inventory first: it is where beasts arrive, and it is the panel the quick button sits
    // beside. A stash tab is included whenever one is showing, so a tab full of stored
    // beasts can be released without moving them back to inventory first.
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

            // A panel closing is the difference between "clicking beasts" and "casting your
            // right-click skill at whatever is under the cursor", so it is checked before
            // every pass and again before every individual click.
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

            // Checked before the settle below, not after. A closed panel reports zero items,
            // which satisfies "the count went down" instantly and credits the pass with every
            // beast it never touched - that is how a run that sent 2 clicks reported releasing
            // 106 of 106. Clicks already sent are counted; the rest are still there.
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

            // One settle for the whole pass rather than one per item. Cells do not reflow
            // when an item is consumed, so the whole visible set can be clicked before
            // waiting — which is where nearly all of the previous runtime went.
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

    // Right-clicks every beast whose position can be verified, returning how many were
    // actually sent.
    //
    // A cell emptied earlier in the same pass is harmless to click again — right-clicking
    // empty inventory space does nothing — which is what makes batching safe here.
    private async Task<int> ClickPassAsync(ReleaseSource source, List<NormalInventoryItem> items, CancellationToken ct)
    {
        var timing = _settings.Timing;
        var attempted = 0;
        var skipped = 0;
        var unconfirmed = 0;
        var hoverConfirmed = 0;

        // Split so a slow pass names its own cause: reading the panel (guards) or issuing
        // the click (the input delays). Guessing between those from the outside is what made
        // this take several rounds to pin down.
        var passSw = Stopwatch.StartNew();
        long guardMs = 0;
        long clickMs = 0;
        long slowestClickMs = 0;
        long slowestGuardMs = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var guardSw = Stopwatch.StartNew();

            // Re-checked per click: the panel can close part-way through a pass, and every
            // click after that point would go to the world.
            var stillOpen = source.IsVisible();
            SharpVec2 center = default;
            var usable = false;

            if (stillOpen)
            {
                var rect = item.GetClientRect();
                if (rect.Width > 0 && rect.Height > 0)
                {
                    center = new SharpVec2(rect.Center.X, rect.Center.Y);

                    // The guard that stops a stray click from casting a skill or hitting a UI
                    // control: a rect that has gone stale reads as zero, as a position out in
                    // the world, or as a spot in the panel's chrome rather than its cells.
                    usable = source.Contains(center);
                    if (!usable)
                        Log.Debug($"Skipping a beast whose rect ({center.X:0}, {center.Y:0}) is outside the item cells of {source.Name}.");
                }
            }

            guardSw.Stop();
            guardMs += guardSw.ElapsedMilliseconds;
            slowestGuardMs = Math.Max(slowestGuardMs, guardSw.ElapsedMilliseconds);

            if (!stillOpen)
            {
                Log.Debug($"{Capitalize(source.Name)} closed mid-pass after {attempted} click(s).");
                break;
            }

            if (!usable)
            {
                skipped++;
                continue;
            }

            var clickSw = Stopwatch.StartNew();

            // Aim, confirm, then fire.
            //
            // ClickAtAsync moves the cursor and clicks a fixed delay later
            _input.MoveCursorTo(center);

            var confirmed = !_hoverGateEnabled ||
                await _waits.WaitForAsync(() => source.IsHovered(item),
                    HoverConfirmTimeoutMs, Math.Max(1, timing.Polling.FastPollDelayMs.Value));

            if (!confirmed)
            {
                clickSw.Stop();
                guardMs += clickSw.ElapsedMilliseconds;
                unconfirmed++;
                skipped++;
                continue;
            }

            hoverConfirmed++;
            await _input.ClickAsync(
                MouseButtons.Right,
                preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
                postDelayMs: timing.Clicks.UiClickPostDelayMs.Value);
            clickSw.Stop();

            clickMs += clickSw.ElapsedMilliseconds;
            slowestClickMs = Math.Max(slowestClickMs, clickSw.ElapsedMilliseconds);
            attempted++;
        }

        passSw.Stop();

        if (_hoverGateEnabled && hoverConfirmed == 0 && unconfirmed > 0)
        {
            _hoverGateEnabled = false;
            Log.Warn($"The hover read never confirmed any of {unconfirmed} beast(s) in {source.Name}. " +
                     "Falling back to clicking on position alone for the rest of this run.");
        }

        var perClick = attempted > 0 ? clickMs / (double)attempted : 0;
        Log.Debug(
            $"Right-click pass in {source.Name}: total={passSw.ElapsedMilliseconds}ms " +
            $"[clicks={clickMs}ms ({attempted} sent, {perClick:0.#}ms avg, slowest {slowestClickMs}ms) " +
            $"guards={guardMs}ms (slowest {slowestGuardMs}ms) skipped={skipped} " +
            $"hoverConfirmed={hoverConfirmed} hoverMissed={unconfirmed}]");

        return attempted;
    }
}
