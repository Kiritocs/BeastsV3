using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Automation.Navigation;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;

namespace BeastsV3.Automation.Ui;

// Adapter over MapDeviceWindow: visibility, slot contents, storage grid and the
// activate button. Slots are reached via reflection.
public sealed class MapDeviceUi
{
    // Metadata marker of the hideout map device.
    private const string MapDeviceMetadataMarker = "MappingDevice";
    private const string CanonicalMapDeviceMetadata = "Metadata/Terrain/Missions/Hideouts/Objects/StrDexIntMappingDevice";

    private readonly GameController _game;
    private readonly WorldEntity _worldEntity;

    // Constructs without walk-to-open support.
    public MapDeviceUi(GameController game) : this(game, null) { }

    public MapDeviceUi(GameController game, WorldEntity worldEntity)
    {
        _game = game;
        _worldEntity = worldEntity;
    }

    // Walks to the map device and clicks it; no-op if the window or Atlas is already up.
    public async Task<bool> EnsureOpenAsync()
    {
        var ui = _game?.IngameState?.IngameUi;
        if (ui?.MapDeviceWindow?.IsVisible == true) return true;
        if (ui?.Atlas?.IsVisible == true) return true;

        if (_worldEntity == null) return false;

        return await _worldEntity.EnsureOpenAsync(
            isOpen: () => _game?.IngameState?.IngameUi?.MapDeviceWindow?.IsVisible == true
                       || _game?.IngameState?.IngameUi?.Atlas?.IsVisible == true,
            findEntity: FindMapDeviceEntity,
            button: MouseButtons.Left);
    }

    // Window title text, used to verify the selected map.
    public string GetWindowTitleText()
    {
        var w = Window;
        if (w?.IsVisible != true) return null;
        var title = w.GetType().GetProperty("Title")?.GetValue(w);
        if (title is Element el) return el.Text?.Trim();
        return null;
    }

    public static bool TitleMatches(string titleText, string selectedMap) =>
        !string.IsNullOrWhiteSpace(titleText) &&
        !string.IsNullOrWhiteSpace(selectedMap) &&
        titleText.IndexOf(selectedMap, StringComparison.OrdinalIgnoreCase) >= 0;

    private Entity FindMapDeviceEntity()
    {
        return _game?.EntityListWrapper?.Entities?
            .Where(e => e?.IsValid == true && IsMapDevice(e))
            .FirstOrDefault();
    }

    private static bool IsMapDevice(Entity e)
    {
        if (string.Equals(e?.Metadata, CanonicalMapDeviceMetadata, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(e?.Metadata) &&
            e.Metadata.IndexOf(MapDeviceMetadataMarker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (!string.IsNullOrWhiteSpace(e?.Path) &&
            e.Path.IndexOf(MapDeviceMetadataMarker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    public Element Window => _game?.IngameState?.IngameUi?.MapDeviceWindow;

    public bool IsWindowVisible => Window?.IsVisible == true;

    public Element ActivateButton
    {
        get
        {
            var w = Window;
            if (w == null) return null;
            var prop = w.GetType().GetProperty("ActivateButton");
            return prop?.GetValue(w) as Element;
        }
    }

    // ---- device storage --------------------------------------------------

    // The 4x5 storage grid beside the device; its items are not consumed on Activate.
    public VendorInventory Storage => _game?.IngameState?.IngameUi?.Atlas?.MapDeviceStorage;

    public IList<NormalInventoryItem> StorageItems => Storage?.VisibleInventoryItems;

    // Number of free storage cells, or -1 when unreadable.
    public int StorageFreeCellCount() =>
        ServerInventoryGrid.FreeCellCount(Storage?.ServerInventory, "Map Device storage");

    public List<SlotItem> GetSlotItems()
    {
        var result = new List<SlotItem>();
        if (!TryGetSlots(out var slots)) return result;

        // The map slot is identified by address rather than by index.
        var mapSlotAddress = MapSlotAddress;

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var element = slot as Element;

            // Slot rect, present even when empty, used as a click target.
            var rect = element?.GetClientRect() ?? default;
            var isMapSlot = mapSlotAddress != 0 && element?.Address == mapSlotAddress;

            var item = ExtractSlotItem(slot);
            var entity = item?.Item;
            if (entity == null) { result.Add(new SlotItem(i, null, null, null, 0, 0, rect, isMapSlot)); continue; }

            var baseName = entity.GetComponent<Base>()?.Name?.Trim();
            var mapTier = entity.GetComponent<MapKey>()?.Tier;
            var stack = entity.GetComponent<ExileCore.PoEMemory.Components.Stack>();
            var stackSize = Math.Max(1, stack?.Size ?? 1);

            // Read from the item rather than assumed, so a second slot can be filled once the
            // first is at its real cap.
            var maxStackSize = Math.Max(0, stack?.Info?.MaxStackSize ?? 0);

            result.Add(new SlotItem(i, item, baseName, mapTier, stackSize, maxStackSize, rect, isMapSlot));
        }
        return result;
    }

    // Total stack size loaded for a base name across all slots.
    public int CountLoadedByName(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return 0;
        return GetSlotItems()
            .Where(s => string.Equals(s.BaseName, itemName, StringComparison.OrdinalIgnoreCase))
            .Sum(s => s.StackSize);
    }

    public int CountLoadedByMapTier(int tier)
    {
        if (tier <= 0) return 0;
        return GetSlotItems()
            .Where(s => s.MapTier == tier)
            .Sum(s => s.StackSize);
    }

    // ---- threads ---------------------------------------------------------

    // Levels walked below the device window; a thread's count text sits seven down.
    private const int MaxThreadDepth = 8;

    public enum ThreadSlotState { Locked, Empty, Picked }

    // One of the device's thread slots. Slot is the element to click or hover. For a picked
    // slot, Count is the per-map count you pay for (free threads from the atlas are not in it) and Minus and Plus are the buttons that change it; Name
    // is the thread, or null until the slot has been hovered once - the name exists only in a
    // tooltip the game builds on first hover.
    public sealed record ThreadSlot(
        ThreadSlotState State, string Name, int Count, Element Slot, Element Minus, Element Plus)
    {
        // True when the count cannot go lower: the game greys the minus button out. That only
        // happens on a map in a Shaped Region, where the count then is the region's free
        // threads. On any other map minus stays live at 1 and clears the slot.
        public bool AtMinimum => State == ThreadSlotState.Picked && Minus is { IsSaturated: false };
    }

    // The device's thread slots, left to right. Since 3.30 a thread is not an item in a slot,
    // so the slots are found by what each state shows: a count above two buttons when picked,
    // a "Select a [Thread] type" tooltip when empty, an "... to unlock" tooltip when locked.
    public static List<ThreadSlot> ReadThreadSlots(Element window)
    {
        var slots = new List<ThreadSlot>();
        CollectThreadSlots(window, 0, slots);
        return slots.OrderBy(s => s.Slot.GetClientRect().X).ToList();
    }

    // The slots with a thread picked.
    public static List<ThreadSlot> ReadThreads(Element window) =>
        ReadThreadSlots(window).Where(s => s.State == ThreadSlotState.Picked).ToList();

    // The thread named by a slot's tooltip, or null while the tooltip is not built.
    public static string ThreadName(Element slot) => FirstText(slot?.Tooltip, 2);

    // A slot's count reads "4", or "4+2" when the atlas adds free threads on top. Only the
    // part before the plus is taken from the stash and changed by the buttons.
    private static bool TryReadPaidCount(string text, out int count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var plus = text.IndexOf('+');
        var paid = plus >= 0 ? text[..plus] : text;
        return int.TryParse(paid.Trim(), out count) && count > 0;
    }

    private static void CollectThreadSlots(Element element, int depth, List<ThreadSlot> into)
    {
        if (element == null || !element.IsVisibleLocal || depth > MaxThreadDepth) return;

        if (element.ChildCount == 0 && TryReadPaidCount(element.Text, out var count))
        {
            // The count sits two levels inside the slot; the slot's small leaf siblings are its
            // buttons. A number without that pair around it is not a thread count.
            var slot = element.Parent?.Parent;
            if (slot == null) return;

            var slotRect = slot.GetClientRect();
            var buttons = ImGuiEx.FreshChildren(slot.Parent)
                .Where(e => e.Address != slot.Address && e.IsVisibleLocal && e.ChildCount == 0 &&
                            e.GetClientRect().Width < slotRect.Width / 2)
                .OrderBy(e => e.GetClientRect().X)
                .ToList();

            if (buttons.Count == 2)
                into.Add(new ThreadSlot(ThreadSlotState.Picked, ThreadName(slot), count, slot, buttons[0], buttons[1]));
            return;
        }

        var tooltip = FirstText(element.Tooltip, 2);
        if (tooltip != null && tooltip.StartsWith("Select a", StringComparison.OrdinalIgnoreCase))
        {
            into.Add(new ThreadSlot(ThreadSlotState.Empty, null, 0, element, null, null));
            return;
        }
        // Locked by atlas progress, or closed by the Unwavering Vision keystone ("Can only use
        // one type of thread ..."). Either way the slot cannot be used.
        if (tooltip != null && (tooltip.Contains("to unlock", StringComparison.OrdinalIgnoreCase) ||
                                tooltip.Contains("one type of thread", StringComparison.OrdinalIgnoreCase)))
        {
            into.Add(new ThreadSlot(ThreadSlotState.Locked, null, 0, element, null, null));
            return;
        }

        foreach (var child in ImGuiEx.FreshChildren(element)) CollectThreadSlots(child, depth + 1, into);
    }

    // ---- thread picker ---------------------------------------------------

    // The open picker's cells - one per thread type you own, plus a clear cell when the slot
    // holds a thread - or an empty list while it is closed. The picker is a top-level panel of its own, not part of the device window. It is
    // told from other panels by its grid of equal squares, at least one showing an owned count.
    public List<Element> ThreadPickerCells()
    {
        foreach (var panel in VisiblePanels())
        {
            var cells = SquareGrid(panel);
            if (cells.Count >= 2 && cells.Any(c => HasNumberText(c, 2))) return cells;
        }
        return new List<Element>();
    }

    // The thread a picker cell stands for. Null until the cell has been hovered once: the game
    // only builds a cell's tooltip then.
    public static string PickerCellThreadName(Element cell)
    {
        if (cell == null) return null;

        foreach (var element in new[] { cell }.Concat(ImGuiEx.FreshChildren(cell)))
        {
            var name = FirstText(element?.Tooltip, 4);
            if (name != null) return name;
        }
        return null;
    }

    private IEnumerable<Element> VisiblePanels() =>
        ImGuiEx.FreshChildren(_game?.IngameState?.IngameUi).Where(p => p.IsVisibleLocal);

    // The picker's grid inside `root`: the largest group of visible siblings that are all
    // squares of one size.
    private static List<Element> SquareGrid(Element root)
    {
        var best = new List<Element>();
        var level = new List<Element> { root };

        for (var depth = 0; depth <= 3 && level.Count > 0; depth++)
        {
            foreach (var element in level)
            {
                var children = ImGuiEx.FreshChildren(element).Where(c => c.IsVisibleLocal).ToList();
                if (children.Count > best.Count && AreEqualSquares(children)) best = children;
            }
            level = level.SelectMany(ImGuiEx.FreshChildren).Where(c => c.IsVisibleLocal).ToList();
        }
        return best;
    }

    private static bool AreEqualSquares(List<Element> elements)
    {
        var first = elements[0].GetClientRect();
        if (first.Width < 20f || Math.Abs(first.Width - first.Height) > 2f) return false;

        return elements.All(e =>
        {
            var rect = e.GetClientRect();
            return Math.Abs(rect.Width - first.Width) <= 2f && Math.Abs(rect.Height - first.Height) <= 2f;
        });
    }

    // True when the element or a descendant shows a whole number. A picker cell's own text is
    // unreadable noise, so the first text found is not enough; its count sits two levels down.
    private static bool HasNumberText(Element element, int maxDepth)
    {
        if (element == null) return false;
        if (int.TryParse(element.Text?.Trim(), out _)) return true;
        return maxDepth > 0 && ImGuiEx.FreshChildren(element).Any(c => HasNumberText(c, maxDepth - 1));
    }

    // First non-empty text of an element or its descendants, nearest level first.
    private static string FirstText(Element element, int maxDepth)
    {
        if (element == null || element.Address == 0) return null;

        var level = new List<Element> { element };
        for (var depth = 0; depth <= maxDepth && level.Count > 0; depth++)
        {
            foreach (var e in level)
            {
                if (!string.IsNullOrWhiteSpace(e?.Text)) return e.Text.Trim();
            }
            level = level.Where(e => e != null).SelectMany(ImGuiEx.FreshChildren).ToList();
        }
        return null;
    }

    // ---- private -------------------------------------------------------

    private static bool _loggedMissingSlots;

    // Reads the window's slot collection via reflection. "ScarabSlots" is ExileCore's name for it.
    private bool TryGetSlots(out List<object> slots)
    {
        slots = new List<object>();
        var w = Window;
        if (w == null || w.IsVisible != true) return false;

        var raw = w.GetType().GetProperty("ScarabSlots")?.GetValue(w) as IEnumerable;
        if (raw == null)
        {
            // Names the window's real properties once, so a rename in ExileCore is a one-line fix.
            if (!_loggedMissingSlots)
            {
                _loggedMissingSlots = true;
                Log.Info("Map Device has no 'ScarabSlots' property. Its properties: " +
                         string.Join(", ", w.GetType().GetProperties().Select(p => p.Name)));
            }
            return false;
        }

        foreach (var slot in raw) slots.Add(slot);
        return true;
    }

    // Returns the item loaded in a slot, or null when empty.
    private static NormalInventoryItem ExtractSlotItem(object slot)
    {
        if (slot == null) return null;
        if (slot.GetType().GetProperty("VisibleInventoryItems")?.GetValue(slot) is not IEnumerable items)
            return null;

        foreach (var item in items)
        {
            if (item is NormalInventoryItem inventoryItem &&
                !string.IsNullOrWhiteSpace(inventoryItem.Item?.Metadata))
                return inventoryItem;
        }
        return null;
    }

    private long MapSlotAddress
    {
        get
        {
            var window = Window;
            if (window == null) return 0;
            var slot = window.GetType().GetProperty("MapSlot")?.GetValue(window) as Element;
            return slot?.Address ?? 0;
        }
    }

    public sealed record SlotItem(int SlotIndex, NormalInventoryItem Item, string BaseName, int? MapTier,
        int StackSize, int MaxStackSize, SharpDX.RectangleF Rect, bool IsMapSlot)
    {
        public bool IsEmpty => Item == null;

        public bool IsClickable => Rect.Width > 8 && Rect.Height > 8;

        // Room left in this slot's stack. Items that do not stack, and slots whose stack info
        // is unreadable, report none - callers fall back to IsEmpty for those.
        public int Headroom => MaxStackSize > 0 ? Math.Max(0, MaxStackSize - StackSize) : 0;
    }
}
