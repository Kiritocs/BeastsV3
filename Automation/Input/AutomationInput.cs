using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Input;

// Synthetic keyboard and mouse input for workflows. Applies the configured delays,
// checks cancellation, and tells InputLock to let each injected event through.
public sealed class AutomationInput
{
    private const int DelaySliceMs = 50;

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly RuntimeState _state;
    private readonly InputLock _inputLock;

    public AutomationInput(GameController game, BeastsSettings settings, RuntimeState state, InputLock inputLock)
    {
        _game = game;
        _settings = settings;
        _state = state;
        _inputLock = inputLock;
    }

    // ---- delay helpers -------------------------------------------------

    // Waits the scaled delay in slices, aborting if the run is stopped.
    public async Task DelayAsync(int baseDelayMs)
    {
        ThrowIfStopRequested();
        _inputLock.EnforceCursor();

        var adjusted = ScaleDelay(baseDelayMs);
        var remaining = adjusted;
        while (remaining > 0)
        {
            var slice = Math.Min(remaining, DelaySliceMs);
            await Task.Delay(slice);
            remaining -= slice;
            _inputLock.EnforceCursor();
            ThrowIfStopRequested();
        }
    }

    // Delay used to let the UI settle before reading it.
    public Task DelayForUiCheckAsync(int baseDelayMs) => DelayAsync(baseDelayMs);

    // Applies the speed multiplier and flat extra delay to a base delay.
    public int ScaleDelay(int baseDelayMs)
    {
        var normalized = Math.Max(0, baseDelayMs);
        var timing = _settings?.Timing;
        if (timing == null) return normalized;

        var extraLatency = timing.General.IncludeServerLatencyInDelays.Value ? GetServerLatencyMs() : 0;
        return Math.Max(0, normalized + timing.General.FlatExtraDelayMs.Value + extraLatency);
    }

    // Scaled delay plus server latency.
    public int ScaleTimeout(int baseTimeoutMs) => Math.Max(0, ScaleDelay(baseTimeoutMs) + GetServerLatencyMs());

    public int GetServerLatencyMs() =>
        Math.Max(0, _game?.Game?.IngameState?.ServerData?.Latency ?? 0);

    public int ClickPostDelayFloor() =>
        Math.Max(0, _settings.Timing.Clicks.ClickDelayMs.Value);

    // ---- cancellation --------------------------------------------------

    // Throws when the current run has been cancelled.
    public void ThrowIfStopRequested()
    {
        if (_state.StopRequested) throw new OperationCanceledException("Automation stop requested.");
    }

    // ---- keys ----------------------------------------------------------

    // Sends a key-down event.
    public void PressKeyDown(Keys key)
    {
        if (key == Keys.None) return;
        _inputLock.AllowKeys(key);
        ExileCore.Input.KeyDown(key);
    }

    public void PressKeyUp(Keys key)
    {
        if (key == Keys.None) return;
        _inputLock.AllowKeys(key);
        ExileCore.Input.KeyUp(key);
    }

    public async Task TapKeyAsync(Keys key, int downHoldMs, int postDelayMs)
    {
        if (key == Keys.None) return;
        PressKeyDown(key);
        if (downHoldMs > 0) await DelayAsync(downHoldMs);
        PressKeyUp(key);
        if (postDelayMs > 0) await DelayAsync(postDelayMs);
    }

    public async Task CtrlTapKeyAsync(Keys key, int downHoldMs, int postDelayMs)
    {
        PressKeyDown(Keys.LControlKey);
        try { await TapKeyAsync(key, downHoldMs, postDelayMs); }
        finally { PressKeyUp(Keys.LControlKey); }
    }

    // Types a digit string as individual key taps.
    public async Task TypeDigitsAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var timing = _settings.Timing;
        foreach (var ch in text)
        {
            var key = ch switch
            {
                '0' => Keys.D0, '1' => Keys.D1, '2' => Keys.D2, '3' => Keys.D3, '4' => Keys.D4,
                '5' => Keys.D5, '6' => Keys.D6, '7' => Keys.D7, '8' => Keys.D8, '9' => Keys.D9,
                _ => Keys.None,
            };
            if (key == Keys.None) continue;
            await TapKeyAsync(key, timing.Clicks.KeyTapDelayMs.Value, timing.Clicks.KeyTapDelayMs.Value);
        }
    }

    public void ReleaseKeys(params Keys[] keys)
    {
        if (keys == null) return;
        _inputLock.AllowKeys(keys);
        for (var i = keys.Length - 1; i >= 0; i--)
        {
            if (keys[i] == Keys.None) continue;
            ExileCore.Input.KeyUp(keys[i]);
        }
    }

    // ---- mouse ---------------------------------------------------------

    // Moves the cursor and updates the clip position.
    public void MoveCursorTo(SharpVec2 position)
    {
        var clamped = ClampToGameWindow(position);
        _inputLock.TrackCursor(clamped.X, clamped.Y);
        _inputLock.AllowMouse();
        ExileCore.Input.SetCursorPos(new System.Numerics.Vector2(clamped.X, clamped.Y));
        ExileCore.Input.MouseMove();
    }

    public async Task ClickAsync(MouseButtons button, int preDelayMs, int postDelayMs, params Keys[] modifiers)
    {
        await DelayAsync(preDelayMs);

        if (modifiers is { Length: > 0 })
        {
            _inputLock.AllowKeys(modifiers);
            foreach (var key in modifiers)
            {
                if (key == Keys.None) continue;
                ExileCore.Input.KeyDown(key);
            }
        }

        _inputLock.AllowMouse();

        // ExileCore's Input.Click can block on the game's own loop, so it is timed apart
        // from the delay that follows it.
        var rawSw = Stopwatch.StartNew();
        ExileCore.Input.Click(button);
        rawSw.Stop();
        if (rawSw.ElapsedMilliseconds >= SlowClickThresholdMs)
            Log.Debug($"ExileCore Input.Click({button}) alone took {rawSw.ElapsedMilliseconds}ms.");

        if (modifiers is { Length: > 0 })
        {
            _inputLock.AllowKeys(modifiers);
            for (var i = modifiers.Length - 1; i >= 0; i--)
            {
                if (modifiers[i] == Keys.None) continue;
                ExileCore.Input.KeyUp(modifiers[i]);
            }
        }

        var floor = ClickPostDelayFloor();
        await DelayAsync(Math.Max(postDelayMs, floor));
    }

    // A single click that took far longer than the delays it was configured with, reported
    // once so a stall names its own phase.
    //
    // Set generously: the configured delays are tens of milliseconds, so anything past this
    // is the game or ExileCore blocking, not the delays being long.
    private const int SlowClickThresholdMs = 100;

    public async Task ClickAtAsync(SharpVec2 position, MouseButtons button, int preDelayMs, int postDelayMs, params Keys[] modifiers)
    {
        // Phase-timed because a slow click is otherwise indistinguishable from a long delay,
        // and the two have completely different fixes.
        var moveSw = Stopwatch.StartNew();
        MoveCursorTo(position);
        moveSw.Stop();

        var preSw = Stopwatch.StartNew();
        await DelayAsync(preDelayMs);
        preSw.Stop();

        var clickSw = Stopwatch.StartNew();
        await ClickAsync(button, 0, postDelayMs, modifiers);
        clickSw.Stop();

        var total = moveSw.ElapsedMilliseconds + preSw.ElapsedMilliseconds + clickSw.ElapsedMilliseconds;
        if (total >= SlowClickThresholdMs)
        {
            Log.Debug(
                $"Slow click ({total}ms): move={moveSw.ElapsedMilliseconds}ms " +
                $"preDelay={preSw.ElapsedMilliseconds}ms (asked {ScaleDelay(preDelayMs)}ms) " +
                $"clickAndPost={clickSw.ElapsedMilliseconds}ms (asked {ScaleDelay(Math.Max(postDelayMs, ClickPostDelayFloor()))}ms)");
        }
    }

    public void LeftMouseDown()
    {
        _inputLock.AllowMouse();
        ExileCore.Input.LeftDown();
    }

    public void LeftMouseUp()
    {
        _inputLock.AllowMouse();
        ExileCore.Input.LeftUp();
    }

    private SharpVec2 ClampToGameWindow(SharpVec2 position)
    {
        var rect = _game.Window.GetWindowRectangle();
        return new SharpVec2(
            Math.Clamp(position.X, rect.Left, rect.Right - 1),
            Math.Clamp(position.Y, rect.Top, rect.Bottom - 1));
    }
}
