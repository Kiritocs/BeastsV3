using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore.Shared.Nodes;

namespace BeastsV3.Automation;

// Runs one automation workflow at a time: polls hotkeys, wraps the body in UI prep,
// input lock and cleanup, and publishes status messages.
public sealed class Runner
{
    private readonly RuntimeState _state;
    private readonly BeastsSettings _settings;
    private readonly HotkeyTracker _hotkeys;
    private readonly InputLock _inputLock;
    private readonly UiCleanup _uiCleanup;
    private readonly AutomationInput _input;

    public Runner(RuntimeState state, BeastsSettings settings, HotkeyTracker hotkeys,
        InputLock inputLock, UiCleanup uiCleanup, AutomationInput input)
    {
        _state = state;
        _settings = settings;
        _hotkeys = hotkeys;
        _inputLock = inputLock;
        _uiCleanup = uiCleanup;
        _input = input;
    }

    public bool IsRunning => _state.IsRunning;

    // Runs `action` when the hotkey fires, or requests a stop if a run is already active.
    public bool CheckHotkey(HotkeyNodeV2 hotkey, string label, Func<Task> action)
    {
        if (!_hotkeys.TryGet(hotkey, IsRunning, out var key, out var usedHeldFallback)) return false;

        Log.Debug($"{label} hotkey pressed. key={key}, source={(usedHeldFallback ? "held-fallback" : "pressed-once")}");
        if (IsRunning)
        {
            RequestStop();
            return true;
        }

        // Detached, so a workflow that throws before QueueAsync still reaches the log.
        Log.FireAndForget(action, $"{label} hotkey action");
        return true;
    }

    // Starts a run, or requests a stop if one is already active.
    public async Task QueueAsync(
        Func<CancellationToken, Task> body,
        string failureLabel,
        IEnumerable<Keys> passthroughKeys,
        UiCleanupOptions uiCleanupOptions = null,
        string cancelledStatus = null,
        bool isBestiaryClearRunning = false,
        bool clearBestiaryDeleteModeOverride = false)
    {
        if (!TryClaimRunSlot())
        {
            return;
        }

        BeginRun(isBestiaryClearRunning);

        try
        {
            await _uiCleanup.PrepareAsync(failureLabel, uiCleanupOptions);
            _input.ThrowIfStopRequested();

            _inputLock.EnableForRun(WithPanicStopKey(passthroughKeys));
            await body(_state.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(cancelledStatus)) UpdateStatus(cancelledStatus);
        }
        catch (Exception ex)
        {
            Log.Error($"{failureLabel} failed", ex);
            ShowError($"{failureLabel} failed: {ex.Message}");
        }
        finally
        {
            EndRun(clearBestiaryDeleteModeOverride);
        }
    }

    public void RequestStop()
    {
        if (!_state.IsRunning || _state.StopRequested) return;
        _state.StopRequested = true;
        _state.Cts?.Cancel();
        if (!_state.IsBestiaryClearRunning) UpdateStatus("Stopping...");
    }

    // Checks for panic stop hotkey and stops if pressed. Works anytime without requiring workflow matching.
    // Polled every frame including while idle: the tracker discards the first edge it ever sees for a
    // key, so skipping the idle frames would make it swallow the first real press of every session.
    public void CheckPanicStop(HotkeyNodeV2 panicStopHotkey)
    {
        if (panicStopHotkey == null) return;
        if (!_hotkeys.TryGet(panicStopHotkey, IsRunning, out var key, out var usedHeldFallback)) return;
        if (!IsRunning) return;

        Log.Info($"Panic stop activated! key={key}, source={(usedHeldFallback ? "held-fallback" : "pressed-once")}");
        RequestStop();
    }

    // The panic stop key must always survive the input lock, whatever the workflow passes through.
    // Without this the low-level keyboard hook eats the press before ExileCore can poll it.
    private IEnumerable<Keys> WithPanicStopKey(IEnumerable<Keys> passthroughKeys)
    {
        var keys = passthroughKeys?.ToList() ?? new List<Keys>();
        var panicKey = _settings.PanicStopHotkey?.Value.Key ?? Keys.None;
        if (panicKey != Keys.None && !keys.Contains(panicKey)) keys.Add(panicKey);
        return keys;
    }

    // ---- status helpers ------------------------------------------------

    // Sets the overlay status text and logs it. Identical consecutive lines are collapsed
    // by the host logger, so callers should vary repeated messages.
    public void UpdateStatus(string message)
    {
        SetOverlay(message, isError: false);
        _state.LastStatusMessage = message;
        Log.Debug($"STATUS: {message}");
    }

    public void ShowError(string message)
    {
        _state.LastStatusMessage = message;
        SetOverlay(message, isError: true);
    }

    public void ClearStatus()
    {
        _state.OverlayMessage = string.Empty;
        _state.OverlayIsError = false;
        _state.OverlayHideAtUtc = DateTime.MinValue;
    }

    public bool TryGetLiveOverlay(out string message, out bool isError)
    {
        message = null;
        isError = false;
        if (string.IsNullOrWhiteSpace(_state.OverlayMessage)) return false;

        var now = DateTime.UtcNow;
        if (!IsRunning && _state.OverlayHideAtUtc != DateTime.MaxValue && now >= _state.OverlayHideAtUtc)
        {
            ClearStatus();
            return false;
        }

        message = _state.OverlayMessage;
        isError = _state.OverlayIsError;
        return true;
    }

    // ---- private -------------------------------------------------------

    // Claims the single run slot, returning false when a run is already active.
    private bool TryClaimRunSlot()
    {
        if (_state.IsRunning) { RequestStop(); return false; }
        return true;
    }

    private void BeginRun(bool isBestiaryClearRunning)
    {
        _state.IsRunning = true;
        _state.IsBestiaryClearRunning = isBestiaryClearRunning;
        _state.StopRequested = false;
        _state.Cts = new CancellationTokenSource();
        ClearStatus();
    }

    private void EndRun(bool clearBestiaryDeleteModeOverride)
    {
        _state.IsRunning = false;
        _state.IsInputLockActive = false;
        _state.IsBestiaryClearRunning = false;
        _state.StopRequested = false;
        if (clearBestiaryDeleteModeOverride) _state.BestiaryDeleteModeOverride = null;

        _state.Cts?.Dispose();
        _state.Cts = null;

        _inputLock.DisableForRun();
        // Releases modifier keys left held by the workflow.
        _input.ReleaseKeys(Keys.LControlKey, Keys.RControlKey, Keys.LShiftKey, Keys.RShiftKey, Keys.LMenu, Keys.RMenu);

        // Schedules the overlay to hide; errors stay up longer than info.
        if (string.IsNullOrWhiteSpace(_state.OverlayMessage)) return;
        var seconds = _state.OverlayIsError
            ? _settings.AutomationStatus.ErrorDurationSeconds.Value
            : _settings.AutomationStatus.StatusDurationSeconds.Value;
        _state.OverlayHideAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
    }

    private void SetOverlay(string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message)) { ClearStatus(); return; }
        _state.OverlayMessage = message.Trim();
        _state.OverlayIsError = isError;
        // Overlay stays visible for the rest of the run.
        _state.OverlayHideAtUtc = DateTime.MaxValue;
    }
}
