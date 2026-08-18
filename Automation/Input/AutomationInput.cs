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

// Synthetic keyboard and mouse input for workflows: applies the configured delays, checks
// cancellation, and tells InputLock to let each injected event through. With humanization
// on, delays are jittered, the cursor travels an arc and clicks land off-center; poll
// intervals and timeouts stay exact - see Humanizer.
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

    // The delay around a synthetic input event, which is where humanization applies.
    // DelayAsync stays exact so poll loops keep their meaning.
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

    // The occasional longer pause a person takes. Rolled before a click, so it reads as deciding.
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

    // Where the cursor actually is, so a caller can skip a move it does not need.
    public SharpVec2 CursorPosition => CurrentCursorPosition();

    // Screen-space, straight from the OS: a cursor path has to start where the cursor
    // actually is, not at ExileCore's cached position.
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

        // Humanization replaces the hold rather than scaling it: a real press sits in the 40-90ms
        // band regardless of the configured value.
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
    // a 1x1 rect, so TrackCursor must move the clip before SetCursorPos can leave the old spot.
    public void MoveCursorTo(SharpVec2 position)
    {
        var clamped = ClampToGameWindow(position);
        _inputLock.TrackCursor(clamped.X, clamped.Y);
        _inputLock.AllowMouse();
        ExileCore.Input.SetCursorPos(new System.Numerics.Vector2(clamped.X, clamped.Y));
        ExileCore.Input.MouseMove();
    }

    // Aims at an element; the bounds let click jitter use its real size, not a fixed radius.
    public Task MoveCursorToAsync(RectangleF bounds) =>
        MoveCursorToAsync(new SharpVec2(bounds.Center.X, bounds.Center.Y), bounds);

    // Humanized move: an off-center aim point inside `bounds`, reached along a WindMouse arc.
    // Falls back to an instant move when humanization or the curve is off, or the hop is tiny.
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

        // ExileCore's Input.Click can block on the game's loop, so it is timed apart from the delay.
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

    // A click that took far longer than its configured delays, reported once so a stall names
    // its phase. Set generously: the delays are tens of ms, so anything past this is the game.
    private const int SlowClickThresholdMs = 100;

    public Task ClickAtAsync(SharpVec2 position, MouseButtons button, int preDelayMs, int postDelayMs, params Keys[] modifiers) =>
        ClickAtAsync(position, (RectangleF?)null, button, preDelayMs, postDelayMs, modifiers);

    // Ctrl-clicks a rect using the configured ctrl-click delays - the transfer chord every
    // workflow uses to move an item between grids.
    public Task CtrlClickAtAsync(RectangleF bounds)
    {
        var clicks = _settings.Timing.Clicks;
        return ClickAtAsync(bounds, MouseButtons.Left,
            preDelayMs: clicks.CtrlClickPreDelayMs.Value,
            postDelayMs: clicks.CtrlClickPostDelayMs.Value,
            modifiers: new[] { Keys.LControlKey });
    }


    // Clicks an element's center; the bounds let click jitter use its real size.
    public Task ClickAtAsync(RectangleF bounds, MouseButtons button, int preDelayMs, int postDelayMs, params Keys[] modifiers) =>
        ClickAtAsync(new SharpVec2(bounds.Center.X, bounds.Center.Y), bounds, button, preDelayMs, postDelayMs, modifiers);

    public async Task ClickAtAsync(SharpVec2 position, RectangleF? bounds, MouseButtons button, int preDelayMs, int postDelayMs, params Keys[] modifiers)
    {
        // Phase-timed: a slow click is otherwise indistinguishable from a long delay.
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

    // Most detents one mouse_event can carry. WM_MOUSEWHEEL's delta is read back as a signed
    // 16-bit value (+/-273 detents), and one tick past that silently wraps and scrolls the
    // other way, so this stays well under the limit.
    private const int MaxWheelTicksPerCall = 200;

    // Scrolls the wheel at the current cursor position. Positive ticks scroll up, negative
    // down. Aim the cursor first - this does not aim itself.
    public void ScrollWheel(int ticks)
    {
        if (ticks == 0) return;
        _inputLock.AllowMouse();

        var remaining = ticks;
        while (remaining != 0)
        {
            var chunk = Math.Clamp(remaining, -MaxWheelTicksPerCall, MaxWheelTicksPerCall);
            mouse_event(MouseEventWheel, 0, 0, chunk * MouseWheelDelta, UIntPtr.Zero);
            remaining -= chunk;
        }
    }

    private SharpVec2 ClampToGameWindow(SharpVec2 position)
    {
        var rect = _game.Window.GetWindowRectangle();
        return new SharpVec2(
            Math.Clamp(position.X, rect.Left, rect.Right - 1),
            Math.Clamp(position.Y, rect.Top, rect.Bottom - 1));
    }
}
