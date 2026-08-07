using System;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Navigation;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Ui;

// Adapter over the stash panel: tab resolution, tab navigation and item reads.
public sealed class StashUi
{
    private const string WorldStashMetadataMarker = "MiscellaneousObjects/Stash";

    private readonly GameController _game;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;
    private readonly WorldEntity _worldEntity;

    public StashUi(GameController game, AutomationInput input, Waits waits, BeastsSettings settings,
        WorldEntity worldEntity)
    {
        _game = game;
        _input = input;
        _waits = waits;
        _settings = settings;
        _worldEntity = worldEntity;
    }

    // Walks to and clicks a stash if the panel is not already open.
    public Task<bool> EnsureOpenAsync() =>
        _worldEntity.EnsureOpenAsync(
            isOpen: () => IsVisible,
            findEntity: () => FindNearestStashEntity(),
            button: MouseButtons.Left);

    private Entity FindNearestStashEntity()
    {
        return _game?.EntityListWrapper?.Entities?
            .Where(e => e?.IsValid == true && IsStashEntity(e))
            .OrderBy(e =>
            {
                var d = _game.Game?.IngameState?.Data?.LocalPlayer?.GetComponent<Positioned>();
                var ep = e.GetComponent<Positioned>();
                if (d == null || ep == null) return float.MaxValue;
                var dx = d.GridPosNum.X - ep.GridPosNum.X;
                var dy = d.GridPosNum.Y - ep.GridPosNum.Y;
                return dx * dx + dy * dy;
            })
            .FirstOrDefault();
    }

    private static bool IsStashEntity(Entity e)
    {
        if (e?.Type == EntityType.Stash) return true;
        if (!string.IsNullOrWhiteSpace(e?.Metadata) &&
            e.Metadata.IndexOf(WorldStashMetadataMarker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (!string.IsNullOrWhiteSpace(e?.Path) &&
            e.Path.IndexOf(WorldStashMetadataMarker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    public StashElement Element => _game?.IngameState?.IngameUi?.StashElement;

    public bool IsVisible => Element?.IsVisible == true;

    public IList<NormalInventoryItem> VisibleItems => Element?.VisibleStash?.VisibleInventoryItems;

    // Captured-monster items in the visible tab, in reading order.
    public List<NormalInventoryItem> VisibleCapturedMonsters()
    {
        var items = VisibleItems;
        if (items == null) return new List<NormalInventoryItem>();

        return items
            .Where(InventoryUi.IsCapturedMonster)
            .OrderByScreenPosition(item => item.GetClientRect())
            .ToList();
    }

    // Whether the game reports the cursor over this exact item in the visible tab.
    //
    // Compared on the item entity rather than the element, because the element wrapper the
    // hover read hands back is not guaranteed to be the same instance as the one enumerated.
    public bool IsHoveringItem(NormalInventoryItem item) =>
        UiHover.IsSameItem(Element?.VisibleStash?.HoverItem, item);

    // Screen rect of the stash panel, or an empty rect when it isn't open.
    public SharpDX.RectangleF PanelRect => Element?.GetClientRect() ?? default;

    public bool IsInsidePanel(SharpVec2 point)
    {
        var panel = PanelRect;
        if (panel.Width <= 0 || panel.Height <= 0) return false;

        return point.X >= panel.Left && point.X <= panel.Right &&
               point.Y >= panel.Top && point.Y <= panel.Bottom;
    }

    public int IndexVisible => Element?.IndexVisibleStash ?? -1;

    public List<string> TabNames()
    {
        var stash = Element;
        if (stash == null) return new List<string>();

        var total = (int)stash.TotalStashes;
        var inventories = stash.Inventories;
        var names = new List<string>(total);
        for (var i = 0; i < total; i++)
        {
            var name = i >= 0 && i < inventories.Count ? inventories[i]?.TabName : null;
            names.Add(string.IsNullOrWhiteSpace(name) ? $"Tab {i}" : name);
        }
        return names;
    }

    // Understands a "Name#index" pin, so tabs sharing a name stay distinguishable.
    public int ResolveTabIndex(string tabName) => TabPin.Resolve(TabNames(), tabName);

    public string TabNameAt(int tabIndex)
    {
        var names = TabNames();
        return tabIndex >= 0 && tabIndex < names.Count ? names[tabIndex] : $"Tab {tabIndex}";
    }

    // ---- sub-tabs ------------------------------------------------------

    // Path to the container holding one child per stash tab.
    private static readonly int[] StashTabNodesPath = { 2, 0, 0, 1, 1 };

    // Index of the sub-tab strip within a tab's root.
    private const int SubTabStripChildIndex = 5;

    // Root element of the currently visible stash tab.
    public Element VisibleTabRoot
    {
        get
        {
            var stash = Element;
            if (stash == null) return null;

            var nodes = ImGuiEx.GetChildAt(_game?.IngameState?.IngameUi?.OpenLeftPanel, StashTabNodesPath);
            var index = stash.IndexVisibleStash;
            if (nodes == null || index < 0 || index >= nodes.ChildCount) return null;

            return nodes.GetChildAtIndex(index)?.GetChildAtIndex(0);
        }
    }

    // Sub-tab buttons of the visible tab; empty for stash types without them.
    public List<(string Name, Element Button)> SubTabs()
    {
        var result = new List<(string, Element)>();

        var strip = SubTabStrip;
        if (strip == null) return result;

        for (var i = 0; i < strip.ChildCount; i++)
        {
            var button = strip.GetChildAtIndex(i);
            if (button?.IsVisible != true) continue;

            var name = button.GetChildAtIndex(0)?.Text?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var rect = button.GetClientRect();
            if (rect.Width < 20 || rect.Height < 12) continue;

            result.Add((name, button));
        }
        return result;
    }

    private Element SubTabStrip
    {
        get
        {
            var root = VisibleTabRoot;
            if (root == null) return null;

            var direct = root.GetChildAtIndex(SubTabStripChildIndex)?.GetChildAtIndex(0);
            if (LooksLikeSubTabStrip(direct)) return direct;

            for (var i = 0; i < root.ChildCount; i++)
            {
                var candidate = root.GetChildAtIndex(i)?.GetChildAtIndex(0);
                if (LooksLikeSubTabStrip(candidate)) return candidate;
            }
            return null;
        }
    }

    private static bool LooksLikeSubTabStrip(Element candidate)
    {
        if (candidate == null || candidate.ChildCount < 2) return false;

        var rect = candidate.GetClientRect();
        // Sub-tab strips are wide, short and labelled.
        if (rect.Width < 200 || rect.Height < 12 || rect.Height > 90) return false;

        for (var i = 0; i < candidate.ChildCount; i++)
        {
            if (string.IsNullOrWhiteSpace(candidate.GetChildAtIndex(i)?.GetChildAtIndex(0)?.Text)) return false;
        }
        return true;
    }

    // Selects the sub-tab holding itemName; false when no sub-tab holds it.
    public async Task<bool> TrySelectSubTabWithAsync(string itemName)
    {
        if (CountMatchingQuantity(itemName) > 0) return true;

        var subTabs = SubTabs();
        if (subTabs.Count == 0) return false;

        var timing = _settings.Timing;
        foreach (var (name, button) in subTabs)
        {
            _input.ThrowIfStopRequested();

            var rect = button.GetClientRect();
            await _input.ClickAtAsync(
                rect, MouseButtons.Left,
                preDelayMs: timing.Clicks.UiClickPreDelayMs.Value,
                postDelayMs: Math.Max(timing.Clicks.UiClickPostDelayMs.Value, timing.Polling.TabSwitchDelayMs.Value));

            var found = await _waits.WaitForAsync(
                () => CountMatchingQuantity(itemName) > 0,
                timeoutMs: Math.Max(timing.Polling.VisibleTabTimeoutMs.Value, 600),
                pollDelayMs: timing.Polling.FastPollDelayMs.Value);

            if (found)
            {
                Log.Debug($"Stash sub-tab '{name}' holds '{itemName}'.");
                return true;
            }
        }

        Log.Debug($"No stash sub-tab holds '{itemName}'. Checked: {string.Join(", ", subTabs.Select(s => s.Name))}");
        return false;
    }

    // Free 1x1 cells in the visible tab, or -1 when the grid can't be read.
    public int VisibleTabFreeCellCount()
    {
        try
        {
            var serverInventory = Element?.VisibleStash?.ServerInventory;
            if (serverInventory == null) return -1;

            var columns = serverInventory.Columns;
            var rows = serverInventory.Rows;
            if (columns <= 0 || rows <= 0) return -1;

            var occupied = new bool[columns, rows];
            foreach (var item in serverInventory.InventorySlotItems)
            {
                var endX = Math.Min(columns, item.PosX + item.SizeX);
                var endY = Math.Min(rows, item.PosY + item.SizeY);
                for (var x = Math.Max(0, item.PosX); x < endX; x++)
                    for (var y = Math.Max(0, item.PosY); y < endY; y++)
                        occupied[x, y] = true;
            }

            var free = 0;
            for (var x = 0; x < columns; x++)
                for (var y = 0; y < rows; y++)
                    if (!occupied[x, y]) free++;
            return free;
        }
        catch (Exception ex)
        {
            Log.Debug($"Stash tab capacity read failed: {ex.GetType().Name}");
            return -1;
        }
    }

    public bool IsTabReady(int tabIndex)
    {
        var stash = Element;
        return stash?.IsVisible == true && stash.IndexVisibleStash == tabIndex && stash.VisibleStash != null;
    }

    // Steps to the target tab with the Left/Right stash-tab keys.
    public async Task SelectTabAsync(int tabIndex)
    {
        var stash = Element;
        if (stash?.IsVisible != true) throw new InvalidOperationException("Stash is not open.");
        if (tabIndex < 0 || tabIndex >= stash.TotalStashes)
            throw new InvalidOperationException("Select a valid stash tab name before running restock.");
        if (IsTabReady(tabIndex)) return;

        var timing = _settings.Timing;
        var tabSwitchDelay = timing.Polling.TabSwitchDelayMs.Value;
        var maxSteps = Math.Max(3, (int)stash.TotalStashes * 2);

        for (var step = 0; step < maxSteps; step++)
        {
            _input.ThrowIfStopRequested();
            stash = Element;
            if (stash?.IsVisible != true) throw new InvalidOperationException("Stash closed while switching tabs.");

            var current = stash.IndexVisibleStash;
            if (current == tabIndex)
            {
                await WaitForTabVisibleAsync(tabIndex);
                return;
            }

            var key = tabIndex < current ? Keys.Left : Keys.Right;
            await _input.TapKeyAsync(key, timing.Clicks.KeyTapDelayMs.Value, 0);

            var changed = await WaitForIndexChangeAsync(current, Math.Max(timing.Polling.TabChangeTimeoutMs.Value, tabSwitchDelay));
            if (changed == current)
            {
                await _input.DelayAsync(Math.Max(timing.Polling.TabRetryDelayMs.Value, tabSwitchDelay / 2));
            }
        }

        await WaitForTabVisibleAsync(tabIndex);
    }

    public async Task WaitForTabVisibleAsync(int tabIndex)
    {
        var timing = _settings.Timing;
        var timeout = Math.Max(timing.Polling.VisibleTabTimeoutMs.Value, Math.Max(1500, timing.Polling.TabSwitchDelayMs.Value));
        var ready = await _waits.WaitForAsync(() => IsTabReady(tabIndex), timeout, timing.Polling.FastPollDelayMs.Value);
        if (!ready) throw new InvalidOperationException($"Timed out loading stash tab {tabIndex}.");
    }

    // ---- item queries --------------------------------------------------

    // Total stack size of matching items in the visible tab; highlightedOnly restricts the
    // count to items the stash search bar is highlighting.
    public int CountMatchingQuantity(string itemName, bool highlightedOnly = false)
    {
        var items = VisibleItems;
        if (items == null || string.IsNullOrWhiteSpace(itemName)) return 0;
        return items
            .Where(item => Matches(item, itemName) && (!highlightedOnly || IsHighlighted(item)))
            .Sum(item => Math.Max(1, item.Item.GetComponent<ExileCore.PoEMemory.Components.Stack>()?.Size ?? 1));
    }

    // Every matching cell in the visible tab, ordered top-left to bottom-right so a batched
    // pass clicks in reading order and its log lines are followable.
    public List<NormalInventoryItem> FindAllMatching(string itemName, bool highlightedOnly = false)
    {
        var items = VisibleItems;
        if (items == null || string.IsNullOrWhiteSpace(itemName)) return new List<NormalInventoryItem>();
        return items
            .Where(item => Matches(item, itemName) && (!highlightedOnly || IsHighlighted(item)))
            .OrderBy(item => item.GetClientRect().Top)
            .ThenBy(item => item.GetClientRect().Left)
            .ToList();
    }

    public NormalInventoryItem FindNextMatching(string itemName, bool highlightedOnly = false) =>
        FindAllMatching(itemName, highlightedOnly).FirstOrDefault();

    // True when the search bar is highlighting the item; ignores the new-item glow.
    public static bool IsHighlighted(NormalInventoryItem item) => item?.isHighlighted == true;

    // Matches an item by base name, and by MapKey.Tier for "Map (Tier N)" targets.
    public static bool Matches(NormalInventoryItem item, string itemName)
    {
        if (item?.Item == null || string.IsNullOrWhiteSpace(itemName)) return false;

        var tier = TryParseMapTier(itemName);
        if (tier.HasValue)
        {
            var mapKey = item.Item.GetComponent<ExileCore.PoEMemory.Components.MapKey>();
            if (mapKey == null || mapKey.Tier != tier.Value) return false;
        }

        return string.Equals(item.Item.GetComponent<Base>()?.Name, itemName, StringComparison.OrdinalIgnoreCase);
    }

    private static int? TryParseMapTier(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name.Trim(),
            @"^Map \(Tier\s*(\d+)\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var tier) && tier > 0 ? tier : null;
    }

    private async Task<int> WaitForIndexChangeAsync(int previousIndex, int timeoutMs)
    {
        var pollDelay = _settings.Timing.Polling.FastPollDelayMs.Value;
        var result = await _waits.PollAsync(
            () => IndexVisible,
            idx => idx != previousIndex,
            timeoutMs,
            pollDelay);
        return result;
    }
}
