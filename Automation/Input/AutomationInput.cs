using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using RectangleF = SharpDX.RectangleF;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Input;

// Synthetic keyboard and mouse input for workflows. Applies the configured delays,
// checks cancellation, and tells InputLock to let each injected event through.
//
// When humanization is enabled, the delays around synthetic input are spread over a
// Gaussian, the cursor travels along an arc instead of teleporting, and clicks land
// off-centre. Poll intervals and timeouts stay exact -- see Humanizer for why.
public sealed class AutomationInput
{
    private const int DelaySliceMs = 50;

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly RuntimeState _state;
    private readonly InputLock _inputLock;
    private readonly Humanizer _human;

    public AutomationInput(GameController game, BeastsSettings settings, RuntimeState state, InputLock inputLock)
    {
        _game = game;
        _settings = settings;
        _state = state;
        _inputLock = inputLock;
        _human = new Humanizer(settings);
    }

    public Humanizer Human => _human;

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

    // The delay around an actual synthetic input event, which is where humanization applies.
    // DelayAsync stays exact so poll loops and timeouts keep their meaning.
    public Task ActionDelayAsync(int baseDelayMs) =>
        DelayScaledAsync(_human.Delay(ScaleDelay(baseDelayMs)));

    // Waits an already-scaled value in slices, aborting if the run is stopped.
    private async Task DelayScaledAsync(int adjustedMs)
    {
        ThrowIfStopRequested();
        _inputLock.EnforceCursor();

        var remaining = Math.Max(0, adjustedMs);
        while (remaining > 0)
        {
            var slice = Math.Min(remaining, DelaySliceMs);
            await Task.Delay(slice);
            remaining -= slice;
            _inputLock.EnforceCursor();
            ThrowIfStopRequested();
        }
    }

    // The occasional longer pause a person takes mid-task. Rolled before a click, not after,
    // so it reads as deciding rather than reacting.
    private async Task HesitateAsync()
    {
        var pause = _human.Hesitation();
        if (pause <= 0) return;

        if (!_human.DriftDuringPauses)
        {
            await DelayScaledAsync(pause);
            return;
        }

        // Drift and re-park, so whatever the cursor was hovering is still hovered afterwards.
        var anchor = CurrentCursorPosition();
        await DelayScaledAsync(pause / 2);
        MoveCursorTo(_human.Drift(anchor));
        await DelayScaledAsync(pause - pause / 2);
        MoveCursorTo(anchor);
    }

    // Screen-space, straight from the OS. ExileCore's cached mouse position is not used here
    // because a cursor path has to start where the cursor actually is.
    private static SharpVec2 CurrentCursorPosition()
    {
        ExileCore.Shared.WinApi.GetCursorPos(out SharpDX.Point p);
        return new SharpVec2(p.X, p.Y);
    }

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

        // Humanization replaces the hold rather than scaling it: a real key press sits in the
        // 40-90ms band no matter what the caller configured. Without it the caller's value
        // is used untouched.
        var hold = _human.KeyHold(downHoldMs);

        PressKeyDown(key);
        if (hold > 0) await DelayScaledAsync(hold);
        PressKeyUp(key);
        if (postDelayMs > 0) await ActionDelayAsync(postDelayMs);
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

    // Moves the cursor and updates the clip position. Instant: InputLock clips the cursor to
    // a 1x1 rect, so TrackCursor has to move the clip before SetCursorPos can leave the old
    // spot. Every step of a humanized path goes through here for the same reason.
    public void MoveCursorTo(SharpVec2 position)
    {
        var clamped = ClampToGameWindow(position);
        _inputLock.TrackCursor(clamped.X, clamped.Y);
        _inputLock.AllowMouse();
        ExileCore.Input.SetCursorPos(new System.Numerics.Vector2(clamped.X, clamped.Y));
        ExileCore.Input.MouseMove();
    }

    // Aims at an element. Preferred over the point overload: the bounds let click-point
    // jitter use the element's real size instead of a blind fixed radius.
    public Task MoveCursorToAsync(RectangleF bounds) =>
        MoveCursorToAsync(new SharpVec2(bounds.Center.X, bounds.Center.Y), bounds);

    // Moves the cursor the humanized way: an off-centre aim point inside `bounds` when they
    // are known, reached along a WindMouse arc. Falls back to the instant move when
    // humanization is off, the curve is disabled, or the hop is too short to be worth tracing.
    public async Task MoveCursorToAsync(SharpVec2 position, RectangleF? bounds = null)
    {
        var target = ClampToGameWindow(_human.AimPoint(position, bounds));

        if (!_human.UseCursorPath)
        {
            MoveCursorTo(target);
            return;
        }

        var from = CurrentCursorPosition();
        if (SharpVec2.Distance(from, target) < _human.MinPathDistance)
        {
            MoveCursorTo(target);
            return;
        }

        var path = _human.BuildPath(from, target);
        foreach (var point in path)
        {
            ThrowIfStopRequested();
            MoveCursorTo(point);

            var step = _human.PathStepDelay();
            if (step > 0) await Task.Delay(step);
        }
    }

    public async Task ClickAsync(MouseButtons button, int preDelayMs, int postDelayMs, params Keys[] modifiers)
    {
        await HesitateAsync();
        await ActionDelayAsync(preDelayMs);

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
        await ActionDelayAsync(Math.Max(postDelayMs, floor));
    }

    // A single click that took far longer than the delays it was configured with, reported
    // once so a stall names its own phase.
    //
    // Set generously: the configured delays are tens of milliseconds, so anything past this
    // is the game or ExileCore blocking, not the delays being long.
    private const int SlowClickThresholdMs = 100;

    public Task ClickAtAsync(SharpVec2 position, MouseButtons button, int preDelayMs, int postDelayMs, params Keys[] modifiers) =>
        ClickAtAsync(position, (RectangleF?)null, button, preDelayMs, postDelayMs, modifiers);

    // Clicks the centre of an element. Preferred over the point overload: knowing the bounds
    // lets click-point jitter use the element's real size instead of a blind fixed radius.
    public Task ClickAtAsync(RectangleF bounds, MouseButtons button, int preDelayMs, int postDelayMs, params Keys[] modifiers) =>
        ClickAtAsync(new SharpVec2(bounds.Center.X, bounds.Center.Y), bounds, button, preDelayMs, postDelayMs, modifiers);

    public async Task ClickAtAsync(SharpVec2 position, RectangleF? bounds, MouseButtons button, int preDelayMs, int postDelayMs, params Keys[] modifiers)
    {
        // Phase-timed because a slow click is otherwise indistinguishable from a long delay,
        // and the two have completely different fixes.
        var moveSw = Stopwatch.StartNew();
        await MoveCursorToAsync(position, bounds);
        moveSw.Stop();

        var preSw = Stopwatch.StartNew();
        await ActionDelayAsync(preDelayMs);
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

    // ---- scroll wheel ----------------------------------------------------

    private const uint MouseEventWheel = 0x0800;
    private const int MouseWheelDelta = 120;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);

    // Scrolls the wheel at the current cursor position. Positive ticks scroll up, negative
    // scroll down.  Move the cursor over the target element first, this does not aim itself.
    public void ScrollWheel(int ticks)
    {
        if (ticks == 0) return;
        _inputLock.AllowMouse();
        mouse_event(MouseEventWheel, 0, 0, ticks * MouseWheelDelta, UIntPtr.Zero);
    }

    private SharpVec2 ClampToGameWindow(SharpVec2 position)
    {
        var rect = _game.Window.GetWindowRectangle();
        return new SharpVec2(
            Math.Clamp(position.X, rect.Left, rect.Right - 1),
            Math.Clamp(position.Y, rect.Top, rect.Bottom - 1));
    }
}
