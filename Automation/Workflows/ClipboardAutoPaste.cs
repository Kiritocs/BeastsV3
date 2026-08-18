using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ImGuiNET;

namespace BeastsV3.Automation.Workflows;

// On the Captured Beasts tab opening, copies the configured regex to the clipboard and
// optionally pastes it into the Bestiary search field. Runs from Render, outside the Runner.
public sealed class ClipboardAutoPaste
{
    private readonly BeastsSettings _settings;
    private readonly Runner _runner;
    private readonly AutomationInput _input;
    private readonly BestiaryUi _bestiary;

    // Sampling interval for the tab-ready check, which is costly in the negative case.
    private const int ReadyPollIntervalMs = 200;

    private readonly Stopwatch _sinceReadyPoll = Stopwatch.StartNew();
    private bool _polledOnce;
    private bool _lastReady;

    // The first reading only establishes a baseline. Without it, loading (or reloading) the
    // plugin while the tab is already open reads as "the tab just opened", which fires a paste
    // - Ctrl+F/A/V at the game - purely because the plugin started.
    private bool _primed;

    private bool _wasVisibleLastFrame;
    private bool _pasteRunning;

    public ClipboardAutoPaste(BeastsSettings settings, Runner runner, AutomationInput input, BestiaryUi bestiary)
    {
        _settings = settings;
        _runner = runner;
        _input = input;
        _bestiary = bestiary;
    }

    public void Tick()
    {
        if (!_settings.BestiaryClipboard.EnableAutoCopy.Value)
        {
            _wasVisibleLastFrame = false;
            _lastReady = false;
            return;
        }

        if (!_polledOnce || _sinceReadyPoll.ElapsedMilliseconds >= ReadyPollIntervalMs)
        {
            _polledOnce = true;
            _sinceReadyPoll.Restart();
            _lastReady = _bestiary.IsCapturedBeastsTabReady;
        }

        var isVisible = _lastReady;
        if (!_primed)
        {
            _primed = true;
            _wasVisibleLastFrame = isVisible;
            return;
        }

        if (isVisible && !_wasVisibleLastFrame)
        {
            var regex = BuildRegex();
            try { ImGui.SetClipboardText(regex ?? string.Empty); }
            catch (Exception ex) { Log.Debug($"Clipboard copy failed: {ex.Message}"); }

            if (_settings.BestiaryClipboard.AutoPasteAfterCopy.Value &&
                !_runner.IsRunning && !_pasteRunning &&
                !string.IsNullOrWhiteSpace(regex))
            {
                _pasteRunning = true;
                Log.FireAndForget(() => PasteRegexAsync(regex), "Bestiary regex auto-paste");
            }
        }

        _wasVisibleLastFrame = isVisible;
    }

    public string BuildRegex()
    {
        if (_settings.BestiaryClipboard.UseAutoRegex.Value)
        {
            var enabled = _settings.BeastPrices.EnabledBeasts;
            if (enabled.Count == 0) return string.Empty;

            var fragments = BeastCatalog.All
                .Where(b => enabled.Contains(b.Name) && !string.IsNullOrEmpty(b.RegexFragment))
                .Select(b => b.RegexFragment);
            return string.Join('|', fragments);
        }

        return _settings.BestiaryClipboard.ManualRegex.Value ?? string.Empty;
    }

    private async Task PasteRegexAsync(string regex)
    {
        try
        {
            var timing = _settings.Timing;
            await Task.Delay(Math.Max(timing.Polling.FastPollDelayMs.Value, 25));

            if (_runner.IsRunning || !_bestiary.IsCapturedBeastsTabReady) return;

            // Ctrl+F focuses, Ctrl+A selects, Ctrl+V pastes, Enter commits.
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
                _input.PressKeyUp(Keys.LControlKey);
            }

            await _input.DelayForUiCheckAsync(timing.Polling.UiCheckInitialSettleDelayMs.Value);
            await _input.TapKeyAsync(Keys.Enter,
                downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
                postDelayMs: timing.Polling.FastPollDelayMs.Value);

            // Verifies the paste landed; a mismatch is logged rather than thrown.
            var landed = _bestiary.FilterText;
            if (!string.Equals(landed?.Trim(), regex.Trim(), StringComparison.Ordinal))
                Log.Debug($"Bestiary auto-paste did not stick. expected='{regex}' actual='{landed}'");
        }
        catch (Exception ex)
        {
            Log.Debug($"Bestiary clipboard auto-paste skipped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _pasteRunning = false;
        }
    }
}
