using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Input;

// Ctrl-clicks a set of grid cells in one pass, leaving the caller to verify once at the end.
// The serial alternative spends a full round trip per item, which is where nearly all of a
// transfer loop's runtime goes. Inventory, stash and device grids are fixed - a cell does
// not reflow when its neighbour empties - so positions collected before a pass are still
// correct after earlier clicks land. Ctrl is held for the whole pass rather than tapped.
public sealed class BatchClicker
{
    private readonly AutomationInput _input;
    private readonly BeastsSettings _settings;

    public BatchClicker(AutomationInput input, BeastsSettings settings)
    {
        _input = input;
        _settings = settings;
    }

    // False puts the callers back on their old one-item-per-round-trip path.
    public bool Enabled => _settings?.Timing?.General?.BatchItemTransfers?.Value != false;

    // Ctrl-clicks every position with Ctrl held, returning how many clicks were sent. `guard`
    // is re-checked before each click, or a panel closing mid-pass sends clicks into the world.
    public async Task<int> CtrlClickAllAsync(
        IReadOnlyList<SharpVec2> positions,
        CancellationToken ct,
        Func<bool> guard = null)
    {
        if (positions == null || positions.Count == 0) return 0;

        var timing = _settings.Timing;
        var sw = Stopwatch.StartNew();

        // A modifier left down by an earlier run would turn the first click into a combination.
        _input.ReleaseKeys(Keys.LControlKey, Keys.RControlKey);
        _input.PressKeyDown(Keys.LControlKey);

        var clicked = 0;
        try
        {
            foreach (var position in positions)
            {
                ct.ThrowIfCancellationRequested();

                if (guard != null && !guard())
                {
                    Log.Debug($"Batch ctrl-click stopped after {clicked} of {positions.Count} click(s): guard no longer holds.");
                    break;
                }

                await _input.ClickAtAsync(
                    position,
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

        sw.Stop();
        if (clicked > 0)
            Log.Debug($"Batch ctrl-clicked {clicked} cell(s) in {sw.ElapsedMilliseconds}ms.");

        return clicked;
    }

    // Polls `read` until `isComplete` holds, the timeout expires, or the value moved and then
    // stayed put for the settle window. The settle window is what keeps a dropped click cheap:
    // a pass that moved 19 of 20 would otherwise sit on the full timeout.
    public async Task<int> SettleAsync(
        Func<int> read,
        Func<int, bool> isComplete,
        int timeoutMs,
        CancellationToken ct)
    {
        var timing = _settings.Timing;
        var pollDelayMs = Math.Max(1, timing.Polling.FastPollDelayMs.Value);
        var settleWindowMs = Math.Max(1, timing.Polling.QuantitySettleStableWindowMs.Value);

        var sw = Stopwatch.StartNew();
        var observed = read();
        var lastValue = observed;
        long? lastChangeMs = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (isComplete(observed)) return observed;
            if (sw.ElapsedMilliseconds >= timeoutMs) return observed;
            if (lastChangeMs.HasValue && sw.ElapsedMilliseconds - lastChangeMs.Value >= settleWindowMs)
                return observed;

            await _input.DelayAsync(pollDelayMs);

            observed = read();
            if (observed != lastValue)
            {
                lastValue = observed;
                lastChangeMs = sw.ElapsedMilliseconds;
            }
        }
    }

    // Timeout for a pass of `clicked` transfers: the single-transfer budget plus a smaller
    // allowance per extra item, since they pipeline rather than queue.
    public int BatchTimeoutMs(int baseTimeoutMs, int clicked)
    {
        var normalized = Math.Max(1, baseTimeoutMs);
        return _input.ScaleTimeout(normalized + Math.Max(0, clicked - 1) * Math.Max(50, normalized / 2));
    }

    // Center of an item's cell, or null when the rect is unreadable.
    public static SharpVec2? CellCenter(ExileCore.PoEMemory.Elements.InventoryElements.NormalInventoryItem item)
    {
        var rect = item?.GetClientRect() ?? default;
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        return new SharpVec2(rect.Center.X, rect.Center.Y);
    }

    // Cell centers for a run of items, skipping any whose rect cannot be read.
    public static List<SharpVec2> CellCenters(
        IEnumerable<ExileCore.PoEMemory.Elements.InventoryElements.NormalInventoryItem> items)
    {
        var centers = new List<SharpVec2>();
        if (items == null) return centers;

        foreach (var item in items)
        {
            var center = CellCenter(item);
            if (center.HasValue) centers.Add(center.Value);
        }
        return centers;
    }
}
