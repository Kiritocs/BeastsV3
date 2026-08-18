using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Navigation;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;

namespace BeastsV3.Automation.Ui;

// Accessors for the Faustus offline-merchant panel: tabs, items and shop capacity.
public sealed class MerchantUi
{
    // Metadata of the hideout Faustus NPC.
    private const string FaustusMetadata = "Metadata/NPC/League/Kalguur/VillageFaustusHideout";

    // Child-index paths into the merchant panel.
    private static readonly int[] ShopTabsPath = { 2, 0, 0, 1, 1, 0, 0, 1, 0 };
    private static readonly int[] TabTextPath = { 0, 1 };
    private static readonly int[] PopupPriceTextPath = { 2, 0, 0 };

    private readonly GameController _game;
    private readonly WorldEntity _worldEntity;

    // Constructs without walk-to-open support.
    public MerchantUi(GameController game) : this(game, null) { }

    public MerchantUi(GameController game, WorldEntity worldEntity)
    {
        _game = game;
        _worldEntity = worldEntity;
    }

    // Walks to Faustus and ctrl-alt-clicks him; no-op if the panel is already open.
    public async Task<bool> EnsureFaustusOpenAsync()
    {
        if (IsPanelVisible) return true;
        if (_worldEntity == null) return false;

        return await _worldEntity.EnsureOpenAsync(
            isOpen: () => IsPanelVisible,
            findEntity: FindFaustusEntity,
            button: MouseButtons.Left,
            modifiers: new[] { Keys.LControlKey, Keys.LMenu });
    }

    private Entity FindFaustusEntity()
    {
        return _game?.EntityListWrapper?.Entities?
            .FirstOrDefault(e => e?.IsValid == true &&
                string.Equals(e.Metadata, FaustusMetadata, StringComparison.OrdinalIgnoreCase));
    }

    public StashElement Panel => _game?.IngameState?.IngameUi?.OfflineMerchantPanel;

    public bool IsPanelVisible => Panel?.IsVisible == true;

    public Element PopupWindow => _game?.IngameState?.IngameUi?.PopUpWindow;

    public bool IsPopupVisible => PopupWindow?.IsVisible == true;

    // Same popup slot as PopupWindow, typed so its price controls can be read directly.
    public AsyncItemRightClickPriceMenu PriceMenu => _game?.IngameState?.IngameUi?.AsyncItemRightClickPriceMenu;

    public DropdownElement PriceCurrencyDropdown => PriceMenu?.PriceCurrencyDropdown;

    public Element PriceAmountInput => PriceMenu?.PriceAmountInput;

    // Name of the currency currently selected in the Faustus price popup, or null when unreadable.
    public string PopupCurrencyName()
    {
        var dropdown = PriceCurrencyDropdown;
        var options = dropdown?.Options;
        var index = dropdown?.RememberedSelection ?? -1;
        return options != null && index >= 0 && index < options.Count ? options[index]?.Name : null;
    }

    // Items visible in the current merchant panel view.
    public IList<NormalInventoryItem> VisibleItems => Panel?.VisibleStash?.VisibleInventoryItems;

    // ---- shop-inventory switch (Shop / Purchase) ----------------------

    // Index of the named merchant inventory tab, or -1 when not found.
    public int ResolveInventoryIndex(string inventoryName)
    {
        var panel = Panel;
        if (panel?.Inventories == null || string.IsNullOrWhiteSpace(inventoryName)) return -1;
        for (var i = 0; i < panel.Inventories.Count; i++)
        {
            if (string.Equals(panel.Inventories[i]?.TabName, inventoryName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public bool IsShopInventoryReady()
    {
        var panel = Panel;
        if (panel?.IsVisible != true || panel.VisibleStash == null) return false;

        var shopIndex = ResolveInventoryIndex("Shop");
        if (shopIndex < 0) return true;

        var expected = panel.Inventories != null && shopIndex < panel.Inventories.Count
            ? panel.Inventories[shopIndex]?.Inventory
            : null;
        if (expected?.Address > 0 && panel.VisibleStash.Address > 0)
            return expected.Address == panel.VisibleStash.Address;
        return panel.IndexVisibleStash == shopIndex;
    }

    // ---- ordered shop tabs --------------------------------------------

    // Shop tabs in on-screen order, with their names and click targets.
    public IReadOnlyList<(string Name, Element Tab, Element ClickTarget)> OrderedShopTabs()
    {
        var panel = Panel;
        var tabsRoot = ImGuiEx.GetChildAt(panel, ShopTabsPath);
        if (tabsRoot?.Children == null) return Array.Empty<(string, Element, Element)>();

        return tabsRoot.Children
            .Where(child => child?.IsVisible == true)
            .Select(child =>
            {
                var name = TryGetTabName(child);
                var target = TryGetClickTarget(child);
                return (name, child, target);
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.name))
            .OrderBy(t => t.target?.GetClientRect().Left ?? 0)
            .ToList();
    }

    // Deliberately not de-duplicated: collapsing same-named tabs hid every one but the first,
    // so a second "Beasts" tab could not be picked or selected. Position is what tells them apart.
    public List<string> AvailableShopTabNames()
    {
        return OrderedShopTabs()
            .Select(t => t.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
    }

    // Position of the shop tab currently on screen, or -1.
    public int CurrentShopTabIndex()
    {
        var shopInventory = ResolveShopInventory();
        if (shopInventory?.IsVisible != true) return -1;

        var idx = shopInventory.NestedVisibleInventoryIndex;
        if (!idx.HasValue || idx.Value < 0 || idx.Value >= OrderedShopTabs().Count) return -1;
        return idx.Value;
    }

    public string CurrentShopTabName()
    {
        var index = CurrentShopTabIndex();
        return index < 0 ? null : OrderedShopTabs()[index].Name;
    }

    public string ShopTabNameAt(int tabIndex)
    {
        var ordered = OrderedShopTabs();
        return tabIndex >= 0 && tabIndex < ordered.Count ? ordered[tabIndex].Name : $"Tab {tabIndex}";
    }

    // Understands a "Name#index" pin, so tabs sharing a name stay distinguishable.
    public int ResolveShopTabIndex(string value) => TabPin.Resolve(AvailableShopTabNames(), value);

    // Compared by position, not name: two tabs sharing a name would both look "ready".
    public bool IsShopTabReady(int tabIndex) => tabIndex >= 0 && CurrentShopTabIndex() == tabIndex;

    // ---- popup price text ---------------------------------------------

    // Text currently in the price popup's input field.
    public string GetPopupEnteredPriceText()
    {
        var textElement = ImGuiEx.GetChildAt(PopupWindow, PopupPriceTextPath);
        return textElement?.Text?.Trim();
    }

    public static bool PopupPriceMatches(string observed, string expected)
    {
        return string.Equals(NormalizePriceDigits(observed), NormalizePriceDigits(expected), StringComparison.Ordinal);
    }

    // ---- shop-tab capacity ---------------------------------------------

    // Server-side inventory backing the shop tab currently on screen.
    public ServerInventory VisibleShopServerInventory => Panel?.VisibleStash?.ServerInventory;

    // Occupancy grid of the current shop tab, or null when unreadable.
    public bool[,] OccupiedShopCells()
    {
        var server = VisibleShopServerInventory;

        // A server read that has not caught up reports an empty tab, which looks like unlimited
        // room. Only trust the grid when the rendered items agree.
        if (server?.InventorySlotItems?.Count == 0 && VisibleItems?.Count > 0) return null;

        return ServerInventoryGrid.Occupied(server);
    }

    // The game appends this to the name of a tab that can only have items taken out of it.
    private const string RemoveOnlyMarker = "Remove-only";

    // A remove-only tab takes no new listings however many cells are free, so it is not capacity.
    public static bool IsRemoveOnlyTab(string tabName) =>
        tabName?.IndexOf(RemoveOnlyMarker, StringComparison.OrdinalIgnoreCase) >= 0;

    // Free cells in the shop tab on screen, or null when unreadable. Captured monsters are
    // 1x1, so this doubles as "how many beasts this tab still takes".
    public int? CurrentShopTabFreeCells()
    {
        var grid = OccupiedShopCells();
        return grid == null ? null : CountFreeCells(grid);
    }

    // True when all footprints fit into the free cells, placed largest-first.
    public static bool CanFit(bool[,] occupied, IReadOnlyList<(int W, int H)> requiredFootprints)
    {
        if (occupied == null || requiredFootprints == null || requiredFootprints.Count == 0) return true;

        var cols = occupied.GetLength(0);
        var rows = occupied.GetLength(1);
        var working = (bool[,])occupied.Clone();

        // Largest footprints are placed first.
        var sorted = requiredFootprints
            .OrderByDescending(f => f.W * f.H)
            .ThenByDescending(f => Math.Max(f.W, f.H))
            .ToList();

        foreach (var (w, h) in sorted)
        {
            if (!TryPlaceFootprint(working, cols, rows, w, h))
                return false;
        }
        return true;
    }

    public static int CountFreeCells(bool[,] grid) => ServerInventoryGrid.CountFree(grid);

    // Grid footprint of an item. Captured monsters are 1x1, but the read keeps the fit honest.
    public static (int W, int H) GetItemFootprint(NormalInventoryItem item) =>
        (Math.Max(1, item?.ItemWidth ?? 1), Math.Max(1, item?.ItemHeight ?? 1));

    // ---- private -------------------------------------------------------

    // Returns the merchant's Shop inventory.
    private Inventory ResolveShopInventory()
    {
        var idx = ResolveInventoryIndex("Shop");
        var panel = Panel;
        if (idx < 0 || panel?.Inventories == null || idx >= panel.Inventories.Count) return null;
        return panel.Inventories[idx]?.Inventory;
    }

    private static string TryGetTabName(Element tabElement)
    {
        var textElement = ImGuiEx.GetChildAt(tabElement, TabTextPath);
        return textElement?.Text?.Trim();
    }

    private static Element TryGetClickTarget(Element tabElement) =>
        tabElement?.Children?.FirstOrDefault() ?? tabElement;

    private static bool TryPlaceFootprint(bool[,] grid, int cols, int rows, int w, int h)
    {
        for (var y = 0; y <= rows - h; y++)
        {
            for (var x = 0; x <= cols - w; x++)
            {
                var fits = true;
                for (var dy = 0; dy < h && fits; dy++)
                    for (var dx = 0; dx < w && fits; dx++)
                        if (grid[x + dx, y + dy]) fits = false;
                if (!fits) continue;

                for (var dy = 0; dy < h; dy++)
                    for (var dx = 0; dx < w; dx++)
                        grid[x + dx, y + dy] = true;
                return true;
            }
        }
        return false;
    }

    private static string NormalizePriceDigits(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return new string(text.Where(char.IsDigit).ToArray());
    }
}
