using System;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;
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

    public int FreeCellCount()
    {
        var occupied = GetOccupiedCells(out var columns, out var rows);
        if (occupied == null || columns <= 0 || rows <= 0) return 0;

        var free = 0;
        for (var x = 0; x < columns; x++)
        for (var y = 0; y < rows; y++)
            if (!occupied[x, y]) free++;
        return free;
    }

    // The shift-click quantity prompt, not the destroy-confirmation PopUpWindow.
    public Element StackSplitDialog => _game?.IngameState?.IngameUi?.CurrencyShiftClickMenu;

    public bool IsStackSplitDialogVisible => StackSplitDialog?.IsVisible == true;

    // Text currently in the split prompt's quantity field.
    public string StackSplitQuantityText =>
        ImGuiEx.GetChildAt(StackSplitDialog, SplitQuantityTextPath)?.Text?.Trim();

    private static readonly int[] SplitQuantityTextPath = { 0, 0, 1 };

    // Screen centre of the first free inventory cell, in reading order.
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
        columns = 0;
        rows = 0;

        var serverInventory = _game?.Game?.IngameState?.ServerData?
            .PlayerInventories[(int)InventorySlotE.MainInventory1]?.Inventory;
        if (serverInventory == null || serverInventory.Columns <= 0 || serverInventory.Rows <= 0)
            return null;

        columns = serverInventory.Columns;
        rows = serverInventory.Rows;
        var occupied = new bool[columns, rows];

        foreach (var item in serverInventory.InventorySlotItems)
        {
            var startX = Math.Max(0, item.PosX);
            var startY = Math.Max(0, item.PosY);
            var endX = Math.Min(columns, item.PosX + item.SizeX);
            var endY = Math.Min(rows, item.PosY + item.SizeY);
            for (var x = startX; x < endX; x++)
                for (var y = startY; y < endY; y++)
                    occupied[x, y] = true;
        }

        return occupied;
    }
}
