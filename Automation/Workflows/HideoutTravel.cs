using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using ImGuiNET;

namespace BeastsV3.Automation.Workflows;

// Chat-command travel, shared by every workflow that needs to be standing in a specific area before it can do its job.
public sealed class HideoutTravel
{
    // Timeout for a zone change, sized for a cold load.
    private const int TravelTimeoutMs = 15000;

    // Delay after the loading screen clears before driving the UI.
    private const int ZoneSettleMs = 750;

    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly GameController _game;

    public HideoutTravel(AutomationInput input, Waits waits, BeastsSettings settings, GameController game)
    {
        _input = input;
        _waits = waits;
        _settings = settings;
        _game = game;
    }

    public string CurrentAreaName => _game?.Area?.CurrentArea?.Name ?? "<unknown>";

    public bool IsInHideout => _game?.Area?.CurrentArea?.IsHideout == true;

    // Travels to the hideout via chat command if not already there. Throws when travel fails,
    // since callers need the hideout to proceed and cannot make progress without it.
    public async Task EnsureInHideoutAsync(CancellationToken ct, string reason)
    {
        if (IsInHideout) return;

        if (!await TravelViaChatAsync("/hideout", () => IsInHideout, "hideout", ct))
            throw new InvalidOperationException(
                $"Could not reach the hideout (still in '{CurrentAreaName}'). {reason}");
    }

    // Sends a travel chat command and waits for the destination, retrying once.
    public async Task<bool> TravelViaChatAsync(
        string command,
        Func<bool> hasArrived,
        string destinationLabel,
        CancellationToken ct,
        int maxAttempts = 2)
    {
        if (hasArrived()) return true;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Log.Debug($"Traveling to {destinationLabel} via '{command}'. attempt={attempt}, currentArea='{CurrentAreaName}'");

            await SendChatCommandAsync(command);

            if (await _waits.WaitForAsync(hasArrived, timeoutMs: TravelTimeoutMs, pollDelayMs: 250))
            {
                await WaitForZoneReadyAsync();
                return true;
            }

            Log.Debug($"Did not reach {destinationLabel} on attempt {attempt}. currentArea='{CurrentAreaName}'");
        }

        return false;
    }

    // Waits for the loading screen to clear, then for the client to settle.
    private async Task WaitForZoneReadyAsync()
    {
        await _waits.WaitForAsync(
            () => _game?.IsLoading != true && _game?.InGame == true,
            timeoutMs: TravelTimeoutMs,
            pollDelayMs: 100);
        await _input.DelayAsync(ZoneSettleMs);
    }

    // Sends a chat command by pasting it from the clipboard.
    private async Task SendChatCommandAsync(string command)
    {
        var timing = _settings.Timing;

        try { ImGui.SetClipboardText(command); }
        catch (Exception ex) { Log.Debug($"Clipboard set failed: {ex.Message}"); }

        // Enter opens chat, Ctrl+V pastes, Enter sends.
        await _input.TapKeyAsync(Keys.Enter,
            downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
            postDelayMs: timing.Polling.UiCheckInitialSettleDelayMs.Value);

        await _input.CtrlTapKeyAsync(Keys.V,
            downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
            postDelayMs: timing.Polling.UiCheckInitialSettleDelayMs.Value);

        await _input.TapKeyAsync(Keys.Enter,
            downHoldMs: timing.Clicks.KeyTapDelayMs.Value,
            postDelayMs: timing.Polling.FastPollDelayMs.Value);
    }
}
