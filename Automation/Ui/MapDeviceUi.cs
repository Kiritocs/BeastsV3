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
    public int StorageFreeCellCount()
    {
        try
        {
            var serverInventory = Storage?.ServerInventory;
            var columns = serverInventory?.Columns ?? 0;
            var rows = serverInventory?.Rows ?? 0;
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
            Log.Debug($"Map Device storage capacity read failed: {ex.GetType().Name}");
            return -1;
        }
    }

    public List<SlotItem> GetSlotItems()
    {
        var result = new List<SlotItem>();
        if (!TryGetScarabSlots(out var slots)) return result;

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

    // ---- private -------------------------------------------------------

    // Reads the window's slot collection via reflection.
    private bool TryGetScarabSlots(out List<object> slots)
    {
        slots = new List<object>();
        var w = Window;
        if (w == null || w.IsVisible != true) return false;

        var raw = w.GetType().GetProperty("ScarabSlots")?.GetValue(w) as IEnumerable;
        if (raw == null) return false;

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

    // Address of the dedicated map slot.
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
