using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using ImGuiNET;

namespace BeastsV3.Automation.Workflows;

// Loops Menagerie itemize, travel to hideout and Faustus listing until the Bestiary has
// no matches left. Travel uses chat commands; itemize runs with auto-stash disabled.
// Requires a configured Faustus shop tab with room, and an idle chat box.
public sealed class FullSequence
{
    private const string MenagerieAreaName = "The Menagerie";

    // Timeout for a zone change, sized for a cold load.
    private const int TravelTimeoutMs = 15000;

    // Delay after the loading screen clears before driving the UI.
    private const int ZoneSettleMs = 750;

    // Upper bound on loop passes.
    private const int MaxPasses = 25;

    private readonly Runner _runner;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly GameController _game;
    private readonly Bestiary _bestiary;
    private readonly FaustusList _faustusList;
    private readonly MerchantUi _merchant;
    private readonly InventoryUi _inventory;

    public FullSequence(Runner runner, AutomationInput input, Waits waits, BeastsSettings settings,
        GameController game, Bestiary bestiary, FaustusList faustusList, MerchantUi merchant,
        InventoryUi inventory)
    {
        _runner = runner;
        _input = input;
        _waits = waits;
        _settings = settings;
        _game = game;
        _bestiary = bestiary;
        _faustusList = faustusList;
        _merchant = merchant;
        _inventory = inventory;
    }

    public Task RunAsync() =>
        _runner.QueueAsync(
            RunBodyAsync,
            failureLabel: "Full sequence",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions
            {
                KeepBestiary = true,
                KeepInventory = true,
                KeepMerchant = true,
            },
            cancelledStatus: "Full sequence cancelled.",
            isBestiaryClearRunning: true);

    private async Task RunBodyAsync(CancellationToken ct)
    {
        var totalItemized = 0;

        // Free Faustus slots across the configured tab chain. Null means "not measured", and
        // the itemize step then fills inventory the way it always did.
        //
        // Measured up front when the run starts in the hideout so the very first pass already
        // knows how little room is left; refreshed after each listing step, where the panel is
        // open anyway.
        var faustusRoom = IsInHideout ? await MeasureFaustusRoomAsync(ct) : null;

        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();

            if (faustusRoom is 0)
            {
                _runner.UpdateStatus(
                    $"Full sequence stopped: every configured Faustus shop tab is full. " +
                    $"Listed {totalItemized} beast{ImGuiEx.PluralSuffix(totalItemized)}. " +
                    "Free up space, or add another tab under Automation: Merchant (Faustus) -> Faustus Shop Tabs.");
                return;
            }

            // Step 1: travel to the Menagerie.
            if (!IsInMenagerie)
            {
                _runner.UpdateStatus("Full sequence: traveling to Menagerie...");
                if (!await TravelViaChatAsync("/menagerie", () => IsInMenagerie, "Menagerie", ct))
                {
                    _runner.UpdateStatus(
                        $"Full sequence stopped: could not reach the Menagerie (still in '{CurrentAreaName}').");
                    return;
                }
            }

            ct.ThrowIfCancellationRequested();

            // Step 2: itemize as much as inventory holds, without auto-stash. Capped to the
            // room left at Faustus when that is known, so a nearly-full last tab does not leave
            // a bag of beasts with nowhere to be listed.
            _runner.UpdateStatus(faustusRoom.HasValue
                ? $"Full sequence: itemizing up to {faustusRoom.Value} beast{ImGuiEx.PluralSuffix(faustusRoom.Value)} (pass {pass})..."
                : $"Full sequence: itemizing beasts (pass {pass})...");
            var clear = await _bestiary.RunClearBodyAsync(ct, deleteMode: false, applyRegex: true,
                autoStashOnFull: false, maxToProcess: faustusRoom);
            totalItemized += clear.Processed;

            ct.ThrowIfCancellationRequested();

            var carrying = _inventory.VisibleCapturedMonsters().Count;
            if (carrying == 0)
            {
                _runner.UpdateStatus(
                    totalItemized > 0
                        ? $"Full sequence complete. Itemized and listed {totalItemized} beast{ImGuiEx.PluralSuffix(totalItemized)}."
                        : "Full sequence complete - no beasts itemized, skipping Faustus.");
                return;
            }

            // A pass that itemized nothing will not make progress; stop.
            if (clear.Processed == 0 && pass > 1)
            {
                _runner.UpdateStatus(
                    $"Full sequence stopped: {carrying} beast{ImGuiEx.PluralSuffix(carrying)} with no price data are filling inventory " +
                    $"and {clear.Remaining} match{(clear.Remaining == 1 ? "" : "es")} remain. Clear them out and re-run. " +
                    $"Listed {totalItemized} beast{ImGuiEx.PluralSuffix(totalItemized)} so far.");
                return;
            }

            // Step 3: travel to the hideout.
            if (!IsInHideout)
            {
                _runner.UpdateStatus("Full sequence: traveling to hideout...");
                if (!await TravelViaChatAsync("/hideout", () => IsInHideout, "hideout", ct))
                {
                    _runner.UpdateStatus(
                        $"Full sequence stopped after itemizing {totalItemized} beast{ImGuiEx.PluralSuffix(totalItemized)}: could not reach hideout for the Faustus step.");
                    return;
                }
            }

            // Step 4: open the Faustus panel.
            if (!_merchant.IsPanelVisible)
            {
                _runner.UpdateStatus("Full sequence: opening Faustus...");
                if (!await _merchant.EnsureFaustusOpenAsync())
                {
                    _runner.UpdateStatus(
                        $"Full sequence stopped after itemizing {totalItemized} beast{ImGuiEx.PluralSuffix(totalItemized)}. Could not reach Faustus in hideout.");
                    return;
                }
            }

            // Step 5: list the carried beasts, freeing inventory for the next pass.
            _runner.UpdateStatus("Full sequence: listing at Faustus...");
            await _faustusList.RunBodyAsync(ct);

            ct.ThrowIfCancellationRequested();

            // Panel is still open here, so re-counting the chain costs only the tab clicks.
            faustusRoom = await MeasureFaustusRoomAsync(ct);

            if (clear.Remaining <= 0)
            {
                _runner.UpdateStatus(
                    $"Full sequence complete. Itemized and listed {totalItemized} beast{ImGuiEx.PluralSuffix(totalItemized)}.");
                return;
            }

            Log.Debug($"Full sequence pass {pass}: itemized {clear.Processed}, {clear.Remaining} still matching - returning to the Menagerie.");
        }

        _runner.UpdateStatus(
            $"Full sequence stopped after {MaxPasses} passes ({totalItemized} beast{ImGuiEx.PluralSuffix(totalItemized)} listed). Re-run to continue.");
    }

    // ---- Faustus capacity ------------------------------------------------

    // Free slots across the configured Faustus tabs, or null when they cannot be counted.
    //
    // A failed measurement is not a reason to abandon the run: the pass just falls back to
    // itemizing an inventory's worth, which is what it did before any of this existed.
    private async Task<int?> MeasureFaustusRoomAsync(CancellationToken ct)
    {
        try
        {
            return await _faustusList.MeasureChainFreeSlotsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not measure Faustus capacity: {ex.Message}");
            return null;
        }
    }

    // ---- travel ---------------------------------------------------------

    private string CurrentAreaName => _game?.Area?.CurrentArea?.Name ?? "<unknown>";

    // Matched on Area.Name, which carries no instance suffix.
    private bool IsInMenagerie =>
        string.Equals(CurrentAreaName, MenagerieAreaName, StringComparison.OrdinalIgnoreCase);

    private bool IsInHideout => _game?.Area?.CurrentArea?.IsHideout == true;

    // Sends a travel chat command and waits for the destination, retrying once.
    private async Task<bool> TravelViaChatAsync(
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

    private Keys[] PassthroughKeys()
    {
        var key = _settings.FullSequenceHotkey?.Value.Key ?? Keys.None;
        return key == Keys.None ? Array.Empty<Keys>() : new[] { key };
    }
}
