using System;
using System.Collections.Generic;
using System.Globalization;
using BeastsV3.Automation.Ui;
using BeastsV3.Plugin.Settings;
using BeastsV3.Prices;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;
using ImGuiNET;
using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;

namespace BeastsV3.Rendering;

// Draws price overlays on captured-monster items in the inventory, stash, merchant panel
// and Bestiary tab, via the ImGui foreground draw list.
public sealed class PricePanels
{
    private const string ItemizedCapturedMonsterMetadata = "Metadata/Items/Currency/CurrencyItemisedCapturedMonster";

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly PriceService _prices;
    private readonly BestiaryUi _bestiary;

    // Bestiary row names are deliberately NOT cached. The Bestiary list recycles its row
    // elements, so when automation itemizes a beast the remaining rows keep their addresses
    // and take on different beasts. A previous address-keyed cache kept answering with the
    // old occupant, painting e.g. "Farric Chieftain 1c" onto a Goatman. Read them fresh.

    public PricePanels(GameController game, BeastsSettings settings, PriceService prices, BestiaryUi bestiary)
    {
        _game = game;
        _settings = settings;
        _prices = prices;
        _bestiary = bestiary;
    }

    public void Render()
    {
        if (_settings.MapRender.ShowPricesInInventory.Value) DrawInventoryPrices();
        if (_settings.MapRender.ShowPricesInStash.Value) DrawStashPrices();
        if (_settings.MapRender.ShowPricesInMerchant.Value) DrawMerchantPrices();
        if (_settings.MapRender.ShowPricesInBestiary.Value) DrawBestiaryPanelPrices();
    }

    // ---- private ----------------------------------------------------------

    // Exception types already logged, so a repeating failure writes one line, not one per
    // frame.
    private static readonly HashSet<string> LoggedReadFailures = new(StringComparer.Ordinal);

    // Reads a panel's visible items, returning null instead of throwing while a panel is
    // being torn down.
    private static IList<NormalInventoryItem> SafeVisibleItems(Func<IList<NormalInventoryItem>> read)
    {
        try { return read(); }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            if (LoggedReadFailures.Add(kind))
                Log.Debug($"Visible item read failed mid-frame ({kind}). Further occurrences of this type are not logged.");
            return null;
        }
    }

    private void DrawInventoryPrices()
    {
        var inventory = _game?.IngameState?.IngameUi?.InventoryPanel?[InventoryIndex.PlayerInventory];
        if (inventory?.IsVisible != true) return;
        DrawCapturedItems(SafeVisibleItems(() => inventory.VisibleInventoryItems));
    }

    private void DrawStashPrices() =>
        DrawStashLike(_game?.IngameState?.IngameUi?.StashElement);

    private void DrawMerchantPrices() =>
        DrawStashLike(_game?.IngameState?.IngameUi?.OfflineMerchantPanel);

    private void DrawStashLike(StashElement stash)
    {
        if (stash?.IsVisible != true) return;
        DrawCapturedItems(SafeVisibleItems(() => stash.VisibleStash?.VisibleInventoryItems));
    }

    private void DrawCapturedItems(IList<NormalInventoryItem> items)
    {
        if (items == null) return;
        var drawList = ImGui.GetForegroundDrawList();

        foreach (var item in items)
        {
            if (item?.Item == null || item.Item.Metadata != ItemizedCapturedMonsterMetadata) continue;

            var monsterName = item.Item.GetComponent<CapturedMonster>()?.MonsterVariety?.MonsterName;
            var rect = item.GetClientRect();
            var topLeft = new Vector2(rect.Left, rect.Top);
            var bottomRight = new Vector2(rect.Right, rect.Bottom);

            if (string.IsNullOrEmpty(monsterName) ||
                !_prices.BeastPrices.TryGetValue(monsterName, out var price) || price < 0)
            {
                drawList.AddRectFilled(topLeft, bottomRight, ImGuiEx.ToU32(new Color(255, 255, 0, 25)));
                drawList.AddRect(topLeft, bottomRight, ImGuiEx.ToU32(new Color(255, 255, 0, 50)));
                continue;
            }

            drawList.AddRectFilled(topLeft, bottomRight, ImGuiEx.ToU32(new Color(0, 0, 0, 100)));
            var priceText = $"{price.ToString("0", CultureInfo.InvariantCulture)}c";
            RenderPrimitives.DrawCenteredOutlinedText(
                drawList,
                new Vector2(rect.Center.X, rect.Center.Y),
                priceText,
                Color.White,
                Color.Black);
        }
    }

    private void DrawBestiaryPanelPrices()
    {
        // Rows near the viewport, with rects read this frame. Empty unless the Captured
        // Beasts sub-tab is showing, so no separate readiness check is needed.
        var onScreen = _bestiary.OverlayRows();
        if (onScreen.Count == 0) return;

        var drawList = ImGui.GetForegroundDrawList();

        foreach (var (beast, rect) in onScreen)
        {
            // Read fresh every frame so the label always describes the beast currently in
            // this row. An empty read means the tooltip hasn't populated yet; skipping draws
            // nothing this frame rather than drawing a name that may no longer belong here.
            var name = BestiaryUi.BeastDisplayName(beast);
            if (string.IsNullOrEmpty(name)) continue;
            if (!_prices.BeastPrices.TryGetValue(name, out var price) || price < 0) continue;

            // Uses the rect read alongside the row rather than re-reading it.
            var topLeft = new Vector2(rect.Left, rect.Top);
            var bottomRight = new Vector2(rect.Right, rect.Bottom);
            var center = new Vector2(rect.Center.X, rect.Center.Y);

            drawList.AddRectFilled(topLeft, bottomRight, ImGuiEx.ToU32(new Color(0, 0, 0, 128)));
            drawList.AddRect(topLeft, bottomRight, ImGuiEx.ToU32(Color.White));

            RenderPrimitives.DrawCenteredOutlinedText(drawList, center + new Vector2(0, -10), name, Color.White, Color.Black);
            RenderPrimitives.DrawCenteredOutlinedText(
                drawList, center + new Vector2(0, 10),
                $"{price.ToString("0", CultureInfo.InvariantCulture)}c",
                Color.White, Color.Black);
        }
    }
}
