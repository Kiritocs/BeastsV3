using System;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Ui;

// Read-only accessors for the player inventory panel.
public sealed class InventoryUi
{
    private const string CapturedMonsterPathFragment = "CapturedMonster";

    private readonly GameController _game;

    public InventoryUi(GameController game)
    {
        _game = game;
    }

    public bool IsVisible =>
        _game?.IngameState?.IngameUi?.InventoryPanel?[InventoryIndex.PlayerInventory]?.IsVisible == true;

    // True while an item is attached to the mouse cursor.
    public bool IsHoldingItem =>
        _game?.IngameState?.IngameUi?.Cursor?.Action == MouseActionType.HoldItem;

    // ---- itemized beasts on the ground -----------------------------------

    private static bool IsGroundCapturedMonster(Entity worldItem)
    {
        var path = worldItem?.GetComponent<WorldItem>()?.ItemEntity?.Path;
        return !string.IsNullOrEmpty(path) &&
               path.IndexOf(CapturedMonsterPathFragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // The itemized beasts lying on the ground, nearest first.
    public List<Entity> GroundCapturedMonsters()
    {
        var entities = _game?.EntityListWrapper?.Entities;
        if (entities == null) return new List<Entity>();

        return entities
            .Where(e => e is { IsValid: true, Type: EntityType.WorldItem } && IsGroundCapturedMonster(e))
            .OrderBy(e => e.DistancePlayer)
            .ToList();
    }

    // Whether ground labels are switched on, or null when it cannot be told. Read from the
    // labels of things that are not items (Einhar, the stash, a waypoint): those are shown only
    // while labels are on. Item labels do not work for this - beasts on the ground kept
    // reporting visible with labels off. With no such label around, the game's own "always
    // highlight" option is used, which followed the toggle key both ways.
    public bool? AreGroundLabelsOn
    {
        get
        {
            var labels = _game?.IngameState?.IngameUi?.ItemsOnGroundLabels;

            // The thing under the cursor shows its label even with labels off, so it says nothing.
            var hovered = _game?.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.ItemOnHover?.Address ?? 0;
            var sawOne = false;
            if (labels != null)
            {
                foreach (var label in labels)
                {
                    var entity = label?.ItemOnGround;
                    if (entity == null || entity.Type == EntityType.WorldItem) continue;
                    if (hovered != 0 && entity.Address == hovered) continue;

                    sawOne = true;
                    if (label.IsVisible) return true;
                }
            }

            if (sawOne) return false;
            return AlwaysHighlightOption;
        }
    }

    // The game's own "always highlight" option, or null when it cannot be read.
    public bool? AlwaysHighlightOption =>
        bool.TryParse(GameOption("UI", "always_highlight"), out var on) ? on : null;

    private string GameOption(string section, string key)
    {
        try
        {
            var map = _game?.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.Config?.ConfigMap;
            if (map != null && map.TryGetValue(section, out var values) && values?.SectionConfig != null &&
                values.SectionConfig.TryGetValue(key, out var value))
                return value?.ToString();
        }
        catch
        {
            // Falls through to "not known".
        }
        return null;
    }

    // Where a ground item is drawn on screen, or null when that is outside the game window.
    public SharpVec2? ScreenPositionOf(Entity entity)
    {
        var camera = _game?.IngameState?.Camera;
        if (camera == null || entity == null) return null;

        var screen = camera.WorldToScreen(entity.PosNum);
        var window = _game.Window.GetWindowRectangle();
        if (screen.X <= 0 || screen.Y <= 0 || screen.X >= window.Width || screen.Y >= window.Height) return null;
        return new SharpVec2(screen.X, screen.Y);
    }

    // True while the cursor is over an itemized beast on the ground, so a click picks it up
    // and does not walk the character or talk to an NPC standing there.
    public bool IsHoveringGroundCapturedMonster =>
        IsGroundCapturedMonster(_game?.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.ItemOnHover);

    // The player's "toggle item highlighting" key, from the game's own key bindings.
    public System.Windows.Forms.Keys HighlightToggleKey =>
        int.TryParse(GameOption("ACTION_KEYS", "highlight_toggle"), out var code) && code > 0
            ? (System.Windows.Forms.Keys)code
            : System.Windows.Forms.Keys.None;

    public IList<NormalInventoryItem> VisibleItems =>
        _game?.IngameState?.IngameUi?.InventoryPanel?[InventoryIndex.PlayerInventory]?.VisibleInventoryItems;

    // Whether the game reports the cursor over this exact inventory item.
    public bool IsHoveringItem(NormalInventoryItem item) =>
        UiHover.IsSameItem(
            _game?.IngameState?.IngameUi?.InventoryPanel?[InventoryIndex.PlayerInventory]?.HoverItem, item);

    // Screen rect of the inventory panel, or an empty rect when it isn't open.
    public SharpDX.RectangleF PanelRect =>
        _game?.IngameState?.IngameUi?.InventoryPanel?[InventoryIndex.PlayerInventory]?.GetClientRect() ?? default;

    // Whether a point is inside the open inventory panel.
    public bool IsInsidePanel(SharpVec2 point)
    {
        var panel = PanelRect;
        if (panel.Width <= 0 || panel.Height <= 0) return false;

        return point.X >= panel.Left && point.X <= panel.Right &&
               point.Y >= panel.Top && point.Y <= panel.Bottom;
    }

    // Captured-monster items in reading order, top to bottom then left to right.
    public List<NormalInventoryItem> VisibleCapturedMonsters()
    {
        var items = VisibleItems;
        if (items == null) return new List<NormalInventoryItem>();

        return items
            .Where(IsCapturedMonster)
            .OrderByScreenPosition(item => item.GetClientRect())
            .ToList();
    }

    public int FreeCellCount() => ServerInventoryGrid.CountFree(GetOccupiedCells(out _, out _));

    // The shift-click quantity prompt, not the destroy-confirmation PopUpWindow.
    public Element StackSplitDialog => _game?.IngameState?.IngameUi?.CurrencyShiftClickMenu;

    public bool IsStackSplitDialogVisible => StackSplitDialog?.IsVisible == true;

    // Text currently in the split prompt's quantity field.
    public string StackSplitQuantityText =>
        ImGuiEx.GetChildAt(StackSplitDialog, SplitQuantityTextPath)?.Text?.Trim();

    private static readonly int[] SplitQuantityTextPath = { 0, 0, 1 };

    // Screen center of the first free inventory cell, in reading order.
    public bool TryGetFreeCellCenter(out SharpVec2 center)
    {
        center = default;

        var rect = _game?.IngameState?.IngameUi?.InventoryPanel?[InventoryIndex.PlayerInventory]?.GetClientRect() ?? default;
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        var occupied = GetOccupiedCells(out var columns, out var rows);
        if (occupied == null || columns <= 0 || rows <= 0) return false;

        var cellWidth = rect.Width / columns;
        var cellHeight = rect.Height / rows;

        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
        {
            if (occupied[x, y]) continue;
            center = new SharpVec2(rect.Left + (x + 0.5f) * cellWidth, rect.Top + (y + 0.5f) * cellHeight);
            return true;
        }
        return false;
    }

    public static bool IsCapturedMonster(NormalInventoryItem item)
    {
        var path = item?.Item?.Path;
        if (!string.IsNullOrWhiteSpace(path) &&
            path.IndexOf(CapturedMonsterPathFragment, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        var metadata = item?.Item?.Metadata;
        return !string.IsNullOrWhiteSpace(metadata) &&
               metadata.IndexOf(CapturedMonsterPathFragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ---- private ----------------------------------------------------------

    // Builds an occupancy grid of the inventory from ServerData slot positions.
    private bool[,] GetOccupiedCells(out int columns, out int rows)
    {
        var serverInventory = _game?.Game?.IngameState?.ServerData?
            .PlayerInventories[(int)InventorySlotE.MainInventory1]?.Inventory;

        var occupied = ServerInventoryGrid.Occupied(serverInventory);
        columns = occupied?.GetLength(0) ?? 0;
        rows = occupied?.GetLength(1) ?? 0;
        return occupied;
    }
}
