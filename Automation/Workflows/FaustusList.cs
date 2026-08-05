using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Prices;
using BeastsV3.Shared;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Workflows;

// Lists itemized captured monsters into a Faustus shop tab, opening the panel if needed.
//
// Per sellable item in inventory:
//   1. Ctrl-click the item and wait for the price popup.
//   2. Type the price and wait for the popup to read it back.
//   3. Press Enter and wait for the item to leave inventory.
public sealed class FaustusList
{
    private const int MaxConsecutiveFailures = 3;

    // A full tab makes the game swallow the ctrl-click without opening the price popup, and
    // the cell read does not always catch that first. Hand over before the stall limit hits.
    private const int PopupFailuresBeforeTabHandover = 2;

    // How long to give the client to report the cursor over the beast just aimed at, and how
    // many times to re-aim before giving up on it for this iteration.
    private const int HoverConfirmTimeoutMs = 250;
    private const int AimAttempts = 3;

    // Cleared for the rest of a run if the hover read turns out to be unavailable.
    private bool _hoverGateEnabled = true;
    private bool _anyHoverConfirmed;

    private readonly Runner _runner;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly PriceService _prices;
    private readonly MerchantUi _merchant;
    private readonly InventoryUi _inventory;

    public FaustusList(Runner runner, AutomationInput input, Waits waits, BeastsSettings settings,
        PriceService prices, MerchantUi merchant, InventoryUi inventory)
    {
        _runner = runner;
        _input = input;
        _waits = waits;
        _settings = settings;
        _prices = prices;
        _merchant = merchant;
        _inventory = inventory;
    }

    public Task RunAsync()
    {
        return _runner.QueueAsync(
            RunBodyAsync,
            failureLabel: "Faustus beast listing",
            passthroughKeys: PassthroughKeys(),
            uiCleanupOptions: new UiCleanupOptions { KeepInventory = true, KeepMerchant = true },
            cancelledStatus: "Faustus beast listing cancelled.");
    }

    // ---- capacity probe -------------------------------------------------

    // Free slots across every configured shop tab, or null when the chain cannot be read.
    //
    // Each tab has to be brought on screen to be counted: ServerInventory only ever describes
    // the tab currently displayed, and the panel exposes no per-tab totals for the rest. That
    // costs one click per tab, so this is meant to be called once per sequence pass, not per
    // item. Leaves the first tab with room selected, which is where listing resumes.
    public async Task<int?> MeasureChainFreeSlotsAsync(CancellationToken ct)
    {
        if (!_merchant.IsPanelVisible && !await _merchant.EnsureFaustusOpenAsync()) return null;
        if (!_merchant.IsShopInventoryReady()) await SwitchToShopInventoryAsync();

        var tabChain = ResolveShopTabChain();
        if (tabChain.Count == 0) return null;

        var total = 0;
        var firstWithRoom = -1;

        foreach (var tabIndex in tabChain)
        {
            ct.ThrowIfCancellationRequested();

            // Counting a remove-only tab's free cells would promise room that cannot be used.
            var name = _merchant.ShopTabNameAt(tabIndex);
            if (MerchantUi.IsRemoveOnlyTab(name))
            {
                Log.Warn($"Faustus shop tab '{name}' is remove-only and cannot be listed into. Counting it as full.");
                continue;
            }

            if (!_merchant.IsShopTabReady(tabIndex)) await SelectShopTabAsync(tabIndex);

            var free = _merchant.CurrentShopTabFreeCells();

            // One unreadable tab makes the total a floor rather than a count, and a floor is
            // not safe to cap itemizing with - it would leave beasts in the Menagerie that
            // there was room for. Report "unknown" and let the caller fill inventory instead.
            if (free == null)
            {
                Log.Debug($"Faustus shop tab '{_merchant.ShopTabNameAt(tabIndex)}' grid unreadable - capacity unknown.");
                return null;
            }

            total += free.Value;
            if (firstWithRoom < 0 && free.Value > 0) firstWithRoom = tabIndex;
        }

        if (firstWithRoom >= 0 && !_merchant.IsShopTabReady(firstWithRoom))
            await SelectShopTabAsync(firstWithRoom);

        Log.Debug($"Faustus chain has {total} free slot{ImGuiEx.PluralSuffix(total)} across " +
                  $"{tabChain.Count} tab{ImGuiEx.PluralSuffix(tabChain.Count)}.");
        return total;
    }

    // ---- body ----------------------------------------------------------

    // Lists every sellable captured monster in inventory.
    // How long to let a pre-listing refresh run before giving up and using what we have.
    // Generous enough for a slow poe.ninja, short enough not to feel like a hang.
    private const int PriceRefreshTimeoutMs = 8000;

    private async Task RefreshPricesAsync(CancellationToken ct)
    {
        var merchant = _settings.MerchantAutomation;
        if (!merchant.RefreshPricesBeforeListing.Value) return;

        var maxAge = TimeSpan.FromSeconds(Math.Max(1, merchant.MaxPriceAgeBeforeListingSeconds.Value));

        _runner.UpdateStatus("Refreshing poe.ninja prices...");
        var fresh = await _prices.EnsureFreshAsync(maxAge, PriceRefreshTimeoutMs, ct);

        // Not fatal: listing on slightly old prices beats refusing to sell because a website
        // is down. EnsureFreshAsync has already logged why.
        if (!fresh)
            _runner.UpdateStatus("Could not refresh prices - listing with the prices already loaded.");
    }

    public async Task RunBodyAsync(CancellationToken ct)
    {
        // Re-armed per run: a client that could not answer the hover read last time is not a
        // reason to stop asking this time.
        _hoverGateEnabled = true;
        _anyHoverConfirmed = false;

        // Waits for the user to release the left mouse button.
        while ((Control.MouseButtons & MouseButtons.Left) != 0)
        {
            await Task.Delay(10);
        }

        _input.ReleaseKeys(Keys.LControlKey, Keys.RControlKey, Keys.LShiftKey, Keys.RShiftKey, Keys.LMenu, Keys.RMenu);

        // Refreshed before anything is priced, and while Faustus is still being opened —
        // the panel work overlaps the fetch, so in the common case this costs nothing.
        // Every listing price below derives from these numbers, so a stale set here is real
        // money rather than a stale label.
        await RefreshPricesAsync(ct);

        if (!_merchant.IsPanelVisible)
        {
            _runner.UpdateStatus("Opening Faustus merchant panel...");
            if (!await _merchant.EnsureFaustusOpenAsync())
                throw new InvalidOperationException(
                    "Could not open Faustus. Make sure you're in your hideout with Faustus reachable.");
        }

        if (!_merchant.IsShopInventoryReady())
        {
            _runner.UpdateStatus("Switching Faustus to Shop inventory...");
            await SwitchToShopInventoryAsync();
        }

        var availableTabs = _merchant.AvailableShopTabNames();
        var tabChain = ResolveShopTabChain();
        if (tabChain.Count == 0)
        {
            throw new InvalidOperationException(
                "Set Automation: Merchant (Faustus) -> Faustus Shop Tabs to the tab(s) you want beasts listed in. " +
                $"Available: {string.Join(", ", availableTabs)}.");
        }

        // Position in the chain. Advanced when a tab turns out to be full.
        var tabCursor = 0;
        var configuredTab = tabChain[tabCursor];

        if (!_merchant.IsShopTabReady(configuredTab))
        {
            await SelectShopTabAsync(configuredTab);
        }

        var beastsAtStart = _inventory.VisibleCapturedMonsters().Count;
        var consecutiveFailures = 0;
        var consecutiveAimFailures = 0;

        // Two tallies: observed sums per-attempt deltas, derived is start minus what's left.
        var observed = 0;
        var iterations = 0;

        // Moves the chain on to the next configured tab. False once the chain is exhausted.
        async Task<bool> TryHandOverToNextTabAsync(string reason)
        {
            if (tabCursor + 1 >= tabChain.Count) return false;

            configuredTab = tabChain[++tabCursor];
            var nextTabName = _merchant.ShopTabNameAt(configuredTab);
            Log.Info($"{reason} Moving to the next configured tab '{nextTabName}'.");
            _runner.UpdateStatus($"Shop tab full. Switching to '{nextTabName}'...");
            await SelectShopTabAsync(configuredTab);
            return true;
        }

        string ChainExhaustedMessage(string prefix) =>
            $"{prefix} Tried {string.Join(", ", tabChain.Select(_merchant.ShopTabNameAt))}. " +
            $"Listed {observed} beast{ImGuiEx.PluralSuffix(observed)}. " +
            "Free up space, or add another tab under Automation: Merchant (Faustus) -> Faustus Shop Tabs.";

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!_merchant.IsPanelVisible)
                throw new InvalidOperationException("Faustus merchant panel closed mid-listing.");
            if (!_merchant.IsShopTabReady(configuredTab))
                await SelectShopTabAsync(configuredTab);

            var candidate = NextSellable();
            if (candidate == null) break;

            // Out of room: move to the next configured tab and carry on. Checked here rather
            // than once up front, because the tab fills as we list into it.
            if (!HasRoomFor(candidate))
            {
                if (!await TryHandOverToNextTabAsync("Faustus shop tab is full."))
                    throw new InvalidOperationException(
                        ChainExhaustedMessage("Every configured Faustus shop tab is full."));
                continue;
            }

            iterations++;

            // The counter keeps each status line unique so repeats are not collapsed.
            _runner.UpdateStatus($"Listing {iterations}/{beastsAtStart} {candidate.BeastName} for {candidate.ListingPriceChaos}c...");

            var attempt = await TryListOneAsync(candidate, ct);

            if (attempt.CursorNeverLanded)
            {
                // Not a full tab, so it must not push the run onto the next one.
                if (++consecutiveAimFailures >= MaxConsecutiveFailures)
                    throw new InvalidOperationException(
                        $"Could not get the cursor onto '{candidate.BeastName}' to list it. " +
                        $"Listed {observed} beast{ImGuiEx.PluralSuffix(observed)}.");

                await _input.DelayAsync(15);
                continue;
            }
            consecutiveAimFailures = 0;

            if (!attempt.PopupOpened)
            {
                consecutiveFailures++;

                // The popup refusing to open repeatedly means the tab is full far more often
                // than it means the click was dropped, so hand over rather than stall out on
                // a tab that cannot take another item.
                if (consecutiveFailures >= PopupFailuresBeforeTabHandover &&
                    await TryHandOverToNextTabAsync("Faustus price popup would not open - treating the tab as full."))
                {
                    consecutiveFailures = 0;
                    continue;
                }

                if (consecutiveFailures >= MaxConsecutiveFailures)
                    throw new InvalidOperationException(ChainExhaustedMessage(
                        "Listing stalled while opening the Faustus price popup - the shop tab is most likely full."));

                await _input.DelayAsync(15);
                continue;
            }

            if (attempt.CurrentCount >= attempt.PreviousCount)
            {
                consecutiveFailures = Increment(consecutiveFailures, "Listing stalled while moving beasts into the Faustus shop tab.");
                await _input.DelayAsync(15);
                continue;
            }

            observed += attempt.PreviousCount - attempt.CurrentCount;
            consecutiveFailures = 0;
        }

        // Anything left had no price data.
        var skipped = _inventory.VisibleCapturedMonsters().Count;
        var derived = Math.Max(0, beastsAtStart - skipped);

        // derived is only meaningful while the inventory is readable.
        var inventoryReadable = _inventory.IsVisible;
        var listed = inventoryReadable ? Math.Max(observed, derived) : observed;

        Log.Debug($"Faustus listing tallies: iterations={iterations}, observed={observed}, derived={derived} " +
                  $"(start={beastsAtStart}, left={skipped}, inventoryReadable={inventoryReadable}) -> reporting {listed}.");

        _runner.UpdateStatus(BuildFinalStatus(listed, skipped));
    }

    // ---- single-item listing ------------------------------------------

    // Lists one item and reports what happened.
    private async Task<ListingAttempt> TryListOneAsync(Candidate candidate, CancellationToken ct)
    {
        var previousCount = _inventory.VisibleCapturedMonsters().Count;
        var timing = _settings.Timing;

        // Opens the price popup, but only once the client agrees the cursor is on this beast.
        //
        // Moving and clicking a fixed delay later is a guess about how soon the client
        // processes the move, and a missed frame ctrl-clicks whatever the pointer was over
        // before. Here that means listing the wrong beast at this one's price, which no later
        // check would catch - the popup opens and the price reads back exactly as expected.
        if (!await AimAtAsync(candidate.Item))
            return ListingAttempt.AimFailed(previousCount);

        await _input.ClickAsync(MouseButtons.Left,
            preDelayMs: timing.Clicks.CtrlClickPreDelayMs.Value,
            postDelayMs: timing.Clicks.CtrlClickPostDelayMs.Value,
            modifiers: new[] { Keys.LControlKey });

        var popupOpened = await _waits.WaitForAsync(
            () => _merchant.IsPopupVisible, timeoutMs: 1500,
            pollDelayMs: Math.Max(10, timing.Polling.FastPollDelayMs.Value));

        if (!popupOpened)
        {
            return new ListingAttempt(false, previousCount, previousCount);
        }

        // Releases the ctrl-click modifiers, then enters the price.
        _input.ReleaseKeys(Keys.LControlKey, Keys.RControlKey, Keys.LMenu, Keys.RMenu);
        var priceText = candidate.ListingPriceChaos.ToString(CultureInfo.InvariantCulture);
        await EnterPriceAsync(priceText, ct);

        // Waits for the item count to drop or the popup to close.
        var currentCount = await _waits.PollAsync(
            () => _inventory.VisibleCapturedMonsters().Count,
            count => count < previousCount,
            timeoutMs: Math.Max(timing.Polling.QuantityChangeBaseDelayMs.Value,
                                _input.ClickPostDelayFloor() + timing.Polling.QuantityChangeBaseDelayMs.Value),
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        if (currentCount >= previousCount && _merchant.IsPopupVisible)
        {
            // Popup is still open; wait for it to close.
            if (!await _waits.WaitForAsync(() => !_merchant.IsPopupVisible, 1000, timing.Polling.FastPollDelayMs.Value))
                throw new InvalidOperationException("Timed out closing the Faustus price popup.");
            currentCount = _inventory.VisibleCapturedMonsters().Count;
        }

        if (currentCount >= previousCount)
        {
            await _input.DelayForUiCheckAsync(250);
            currentCount = _inventory.VisibleCapturedMonsters().Count;
        }

        return new ListingAttempt(true, previousCount, currentCount);
    }

    // Puts the cursor on an item and waits for the client to confirm it is hovering it.
    //
    // Re-aimed rather than waited longer: a move the client dropped is not one a longer wait
    // recovers, but a second move it will.
    private async Task<bool> AimAtAsync(NormalInventoryItem item)
    {
        if (!_hoverGateEnabled)
        {
            var blind = item.GetClientRect();
            _input.MoveCursorTo(new SharpVec2(blind.Center.X, blind.Center.Y));
            await _input.DelayAsync(_settings.Timing.Clicks.CtrlClickPreDelayMs.Value);
            return true;
        }

        for (var attempt = 1; attempt <= AimAttempts; attempt++)
        {
            var rect = item.GetClientRect();
            if (rect.Width <= 0 || rect.Height <= 0) return false;

            _input.MoveCursorTo(new SharpVec2(rect.Center.X, rect.Center.Y));

            if (await _waits.WaitForAsync(() => _inventory.IsHoveringItem(item),
                    HoverConfirmTimeoutMs, Math.Max(1, _settings.Timing.Polling.FastPollDelayMs.Value)))
            {
                _anyHoverConfirmed = true;
                return true;
            }

            Log.Debug($"Cursor not confirmed on '{GetBeastName(item)}' (aim attempt {attempt}/{AimAttempts}).");
        }

        // Never confirming at all means this client does not supply the hover read, not that
        // every item moved. Gating on a signal that is always false would stall the run, so it
        // is dropped and listing falls back to the timing it used before.
        if (!_anyHoverConfirmed)
        {
            _hoverGateEnabled = false;
            Log.Warn("The hover read never confirmed the cursor on a beast. " +
                     "Falling back to clicking on position alone for the rest of this run.");
        }

        return false;
    }

    private async Task EnterPriceAsync(string priceText, CancellationToken ct)
    {
        if (!_merchant.IsPopupVisible)
        {
            if (!await _waits.WaitForAsync(() => _merchant.IsPopupVisible, 1000, _settings.Timing.Polling.FastPollDelayMs.Value))
                throw new InvalidOperationException("Timed out waiting for the Faustus price popup.");
        }

        var timing = _settings.Timing;

        // The popup retains the previous price, so typing is skipped when it already matches.
        if (!MerchantUi.PopupPriceMatches(_merchant.GetPopupEnteredPriceText(), priceText))
        {
            // First pass types at full speed; the retry spaces the keys out.
            if (!await TypePriceAsync(priceText, spaceOutKeys: false, ct))
            {
                Log.Debug($"Faustus price '{priceText}' did not land on the fast pass - retyping.");
                if (!await TypePriceAsync(priceText, spaceOutKeys: true, ct))
                {
                    throw new InvalidOperationException(
                        $"Faustus popup price mismatch. Expected '{priceText}', observed '{_merchant.GetPopupEnteredPriceText() ?? "<null>"}'.");
                }
            }
        }

        await _input.TapKeyAsync(Keys.Enter, timing.Clicks.KeyTapDelayMs.Value, 0);
    }

    // Clears the popup field and types the price; false when the read-back mismatches.
    private async Task<bool> TypePriceAsync(string priceText, bool spaceOutKeys, CancellationToken ct)
    {
        var timing = _settings.Timing;
        var keyTap = timing.Clicks.KeyTapDelayMs.Value;
        var betweenKeys = spaceOutKeys ? keyTap : 0;

        await _input.CtrlTapKeyAsync(Keys.A, keyTap, betweenKeys);
        await _input.TapKeyAsync(Keys.Back, keyTap, spaceOutKeys ? timing.Polling.FastPollDelayMs.Value : 0);

        foreach (var ch in priceText)
        {
            ct.ThrowIfCancellationRequested();
            var key = ch switch
            {
                '0' => Keys.D0, '1' => Keys.D1, '2' => Keys.D2, '3' => Keys.D3, '4' => Keys.D4,
                '5' => Keys.D5, '6' => Keys.D6, '7' => Keys.D7, '8' => Keys.D8, '9' => Keys.D9,
                _ => Keys.None,
            };
            if (key == Keys.None) continue;
            await _input.TapKeyAsync(key, keyTap, betweenKeys);
        }

        // Waits for the field to read back the typed digits.
        var observed = await _waits.PollAsync(
            _merchant.GetPopupEnteredPriceText,
            text => MerchantUi.PopupPriceMatches(text, priceText),
            timeoutMs: 500,
            pollDelayMs: timing.Polling.FastPollDelayMs.Value);

        return MerchantUi.PopupPriceMatches(observed, priceText);
    }

    // ---- shop-tab helpers ---------------------------------------------

    // Switches the merchant panel to its Shop inventory.
    private async Task SwitchToShopInventoryAsync()
    {
        var panel = _merchant.Panel;
        if (panel == null) return;

        var shopIdx = _merchant.ResolveInventoryIndex("Shop");
        var timing = _settings.Timing;

        // Clicks the Inventories entry named "Shop", reached via reflection.
        var switchClicked = false;
        var inventoriesProp = panel.GetType().GetProperty("Inventories");
        var inventories = inventoriesProp?.GetValue(panel) as System.Collections.IEnumerable;
        if (inventories != null)
        {
            foreach (var entry in inventories)
            {
                var tabName = entry?.GetType().GetProperty("TabName")?.GetValue(entry) as string;
                if (!string.Equals(tabName, "Shop", StringComparison.OrdinalIgnoreCase)) continue;

                var tabButton = entry.GetType().GetProperty("TabButton")?.GetValue(entry) as ExileCore.PoEMemory.Element;
                if (tabButton == null) continue;

                var rect = tabButton.GetClientRect();
                await _input.ClickAtAsync(
                    new SharpVec2(rect.Center.X, rect.Center.Y),
                    MouseButtons.Left,
                    preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
                    postDelayMs: Math.Max(timing.Clicks.UiClickPostDelayMs.Value, timing.Polling.TabSwitchDelayMs.Value));
                switchClicked = true;
                break;
            }
        }

        if (!switchClicked && !_merchant.IsShopInventoryReady())
        {
            throw new InvalidOperationException("Could not locate Faustus 'Shop' inventory tab. Panel structure may differ.");
        }

        if (!await _waits.WaitForAsync(_merchant.IsShopInventoryReady, 1000, timing.Polling.FastPollDelayMs.Value))
            throw new InvalidOperationException("Faustus 'Shop' inventory did not become ready after switch.");
    }

    private async Task SelectShopTabAsync(int tabIndex)
    {
        var timing = _settings.Timing;
        var ordered = _merchant.OrderedShopTabs();
        var tabName = _merchant.ShopTabNameAt(tabIndex);

        // Indexed rather than searched by name, so a chain entry pointing at the second
        // "Beasts" tab clicks that one instead of the first.
        if (tabIndex < 0 || tabIndex >= ordered.Count)
            throw new InvalidOperationException($"Faustus shop tab '{tabName}' element could not be resolved.");

        var tab = ordered[tabIndex];
        if (tab.Tab == null)
            throw new InvalidOperationException($"Faustus shop tab '{tabName}' element could not be resolved.");

        var target = tab.ClickTarget ?? tab.Tab;
        var rect = target.GetClientRect();
        var previousServerInventory = _merchant.VisibleShopServerInventory?.Address ?? 0;

        await _input.ClickAtAsync(
            new SharpVec2(rect.Center.X, rect.Center.Y),
            MouseButtons.Left,
            preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
            postDelayMs: Math.Max(timing.Clicks.UiClickPostDelayMs.Value, timing.Polling.TabSwitchDelayMs.Value));

        if (!await _waits.WaitForAsync(() => _merchant.IsShopTabReady(tabIndex), 500, timing.Polling.FastPollDelayMs.Value))
            throw new InvalidOperationException($"Could not switch Faustus to the shop tab '{tabName}'.");

        // The occupancy check reads server inventory data, which repoints a moment after the
        // panel does. Reading before it catches up would grade the new tab on the old tab's
        // contents. Not fatal if it never lands - the fit check treats an unreadable grid as room.
        if (previousServerInventory != 0 &&
            !await _waits.WaitForAsync(
                () => (_merchant.VisibleShopServerInventory?.Address ?? 0) is var addr && addr != 0 && addr != previousServerInventory,
                500, timing.Polling.FastPollDelayMs.Value))
        {
            Log.Debug($"Faustus shop tab '{tabName}' server inventory did not repoint after the switch.");
        }
    }

    // Whether the current shop tab can still fit this one item.
    private bool HasRoomFor(Candidate candidate)
    {
        var occupied = _merchant.OccupiedShopCells();
        if (occupied == null)
        {
            // Unreadable grid: assume room and let the listing attempt itself fail. Better
            // than skipping a tab that was probably fine.
            Log.Debug("Could not read Faustus shop cells - assuming the tab has room.");
            return true;
        }

        var footprint = MerchantUi.GetItemFootprint(candidate.Item);
        var fits = MerchantUi.CanFit(occupied, [footprint]);
        if (!fits)
        {
            Log.Debug($"Faustus shop tab '{_merchant.CurrentShopTabName()}' has no {footprint.W}x{footprint.H} slot left " +
                      $"({MerchantUi.CountFreeCells(occupied)} free cells).");
        }
        return fits;
    }

    // Configured shop tabs as positions, in order, keeping only those Faustus actually has.
    //
    // Positions rather than names, because a name is not an identifier once two tabs share
    // one: selecting, verifying readiness and de-duplicating all have to agree on which
    // physical tab is meant, and only the index does that.
    private List<int> ResolveShopTabChain()
    {
        var resolved = new List<int>();
        var configured = _settings.MerchantAutomation.FaustusShopTabs;
        if (configured == null) return resolved;

        foreach (var value in configured)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var index = _merchant.ResolveShopTabIndex(trimmed);
            if (index < 0)
            {
                Log.Warn($"Faustus shop tab '{TabPin.DisplayName(trimmed)}' was not found and will be skipped.");
                continue;
            }

            // Listing the same tab twice would waste an overflow step, since the second
            // visit finds it already full.
            if (!resolved.Contains(index)) resolved.Add(index);
        }

        return resolved;
    }

    // ---- candidate selection ------------------------------------------

    // The next item to list, or null when none remain.
    private Candidate NextSellable() => SellableCandidates().FirstOrDefault();

    // Sellable items ordered by price, so equal prices list consecutively.
    private IEnumerable<Candidate> SellableCandidates() =>
        EnumerateSellable().OrderByDescending(c => c.ListingPriceChaos);

    private IEnumerable<Candidate> EnumerateSellable()
    {
        var multiplier = Math.Clamp(_settings.MerchantAutomation.FaustusPriceMultiplier.Value, 0.5f, 1.5f);

        foreach (var item in _inventory.VisibleCapturedMonsters())
        {
            var name = GetBeastName(item);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!_prices.BeastPrices.TryGetValue(name, out var chaos) || chaos <= 0) continue;

            var listingPrice = Math.Max(1, (int)Math.Ceiling(chaos * multiplier));
            yield return new Candidate(item, name, listingPrice);
        }
    }

    private static string GetBeastName(NormalInventoryItem item)
    {
        var monster = item?.Item?.GetComponent<CapturedMonster>();
        var variety = monster?.MonsterVariety;
        var name = variety?.GetType().GetProperty("MonsterName")?.GetValue(variety) as string;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        name = variety?.GetType().GetProperty("Name")?.GetValue(variety) as string;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        return item?.Item?.GetComponent<Base>()?.Name?.Trim();
    }

    // ---- utility ------------------------------------------------------

    // Increments a stall counter and throws once the limit is reached.
    private static int Increment(int consecutive, string stallMessage)
    {
        consecutive++;
        if (consecutive >= MaxConsecutiveFailures) throw new InvalidOperationException(stallMessage);
        return consecutive;
    }

    private static string BuildFinalStatus(int listed, int skipped)
    {
        if (listed > 0 && skipped > 0)
            return $"Listed {listed} beast{ImGuiEx.PluralSuffix(listed)}. Skipped {skipped} without price data.";
        if (listed > 0) return $"Listed {listed} beast{ImGuiEx.PluralSuffix(listed)}.";
        if (skipped > 0) return $"No sellable beasts found. {skipped} beast{ImGuiEx.PluralSuffix(skipped)} missing price data.";
        return "No itemized beasts were found in player inventory.";
    }

    private Keys[] PassthroughKeys()
    {
        var key = _settings.MerchantAutomation.FaustusListHotkey?.Value.Key ?? Keys.None;
        return key == Keys.None ? Array.Empty<Keys>() : new[] { key };
    }

    private sealed record Candidate(NormalInventoryItem Item, string BeastName, int ListingPriceChaos);
    private sealed record ListingAttempt(bool PopupOpened, int PreviousCount, int CurrentCount,
        bool CursorNeverLanded = false)
    {
        // Kept apart from a popup that would not open: that means the tab is full, this means
        // the click was never aimed. Counting one as the other hands the run to the next tab
        // over a cursor that just needed another go.
        public static ListingAttempt AimFailed(int previousCount) =>
            new(false, previousCount, previousCount, CursorNeverLanded: true);
    }
}
