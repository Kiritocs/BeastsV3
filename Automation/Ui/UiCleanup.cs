using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Plugin.Settings;
using ExileCore;

namespace BeastsV3.Automation.Ui;

// Presses Space to close blocking UI at the start of an automation run.
public sealed class UiCleanup
{
    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly AutomationInput _input;

    public UiCleanup(GameController game, BeastsSettings settings, AutomationInput input)
    {
        _game = game;
        _settings = settings;
        _input = input;
    }

    public bool IsBlockingUiOpen(UiCleanupOptions options = null)
    {
        var ui = _game?.IngameState?.IngameUi;
        if (ui == null) return false;

        options ??= new UiCleanupOptions();
        var keepBestiary = options.KeepBestiary && ui.ChallengesPanel?.IsVisible == true;
        var keepAtlas = options.KeepAtlas && ui.Atlas?.IsVisible == true;
        var keepStash = options.KeepStash && ui.StashElement?.IsVisible == true;
        var keepMerchant = options.KeepMerchant && ui.OfflineMerchantPanel?.IsVisible == true;
        var keepInventory = options.KeepInventory && ui.InventoryPanel?.IsVisible == true;
        var keepMapDevice = options.KeepMapDeviceWindow && ui.MapDeviceWindow?.IsVisible == true;
        var keepLeft = keepBestiary || keepAtlas || keepStash || keepMerchant || keepMapDevice;
        var keepRight = keepInventory;

        return (!keepStash && ui.StashElement?.IsVisible == true) ||
               ui.NpcDialog?.IsVisible == true ||
               ui.SellWindow?.IsVisible == true ||
               ui.PurchaseWindow?.IsVisible == true ||
               (!keepInventory && ui.InventoryPanel?.IsVisible == true) ||
               ui.TreePanel?.IsVisible == true ||
               (!keepAtlas && ui.Atlas?.IsVisible == true) ||
               ui.AtlasTreePanel?.IsVisible == true ||
               ui.RitualWindow?.IsVisible == true ||
               (!keepLeft && ui.OpenLeftPanel?.IsVisible == true) ||
               (!keepRight && ui.OpenRightPanel?.IsVisible == true) ||
               ui.TradeWindow?.IsVisible == true ||
               (!keepBestiary && ui.ChallengesPanel?.IsVisible == true) ||
               ui.CraftBench?.IsVisible == true ||
               ui.DelveWindow?.IsVisible == true ||
               ui.ExpeditionWindow?.IsVisible == true ||
               ui.BanditDialog?.IsVisible == true ||
               ui.MetamorphWindow?.IsVisible == true ||
               ui.SyndicatePanel?.IsVisible == true ||
               ui.SyndicateTree?.IsVisible == true ||
               ui.QuestRewardWindow?.IsVisible == true ||
               (!keepMapDevice && ui.MapDeviceWindow?.IsVisible == true) ||
               ui.SettingsPanel?.IsVisible == true ||
               ui.PopUpWindow?.IsVisible == true;
    }

    public async Task PrepareAsync(string debugContext, UiCleanupOptions options = null)
    {
        if (options?.SkipUiCleanup == true) return;

        var maxAttempts = Math.Max(1, _settings.Timing.Timeouts.MapDeviceCloseUiMaxAttempts.Value);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!IsBlockingUiOpen(options)) return;

            await _input.TapKeyAsync(Keys.Space,
                downHoldMs: _settings.Timing.Clicks.KeyTapDelayMs.Value,
                postDelayMs: _settings.Timing.Polling.UiCheckInitialSettleDelayMs.Value);
        }

        if (IsBlockingUiOpen(options))
        {
            throw new InvalidOperationException($"Blocking UI stayed open after {maxAttempts} attempts ({debugContext}).");
        }
    }
}
