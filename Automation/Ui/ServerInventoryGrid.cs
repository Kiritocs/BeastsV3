using System;
using BeastsV3.Shared;
using ExileCore.PoEMemory.MemoryObjects;

namespace BeastsV3.Automation.Ui;

// Occupancy reads over a ServerInventory grid, shared by the inventory, stash, map device
// and merchant adapters.
public static class ServerInventoryGrid
{
    // Occupancy grid for a server inventory, or null when it cannot be read.
    public static bool[,] Occupied(ServerInventory server)
    {
        var columns = server?.Columns ?? 0;
        var rows = server?.Rows ?? 0;
        var slots = server?.InventorySlotItems;
        if (columns <= 0 || rows <= 0 || slots == null) return null;

        var occupied = new bool[columns, rows];
        foreach (var item in slots)
        {
            if (item == null) continue;
            var endX = Math.Min(columns, item.PosX + Math.Max(1, item.SizeX));
            var endY = Math.Min(rows, item.PosY + Math.Max(1, item.SizeY));
            for (var x = Math.Max(0, item.PosX); x < endX; x++)
                for (var y = Math.Max(0, item.PosY); y < endY; y++)
                    occupied[x, y] = true;
        }
        return occupied;
    }

    // Free 1x1 cells in an occupancy grid; 0 for a null grid.
    public static int CountFree(bool[,] occupied)
    {
        if (occupied == null) return 0;
        var free = 0;
        foreach (var cell in occupied)
            if (!cell) free++;
        return free;
    }

    // Free 1x1 cells in a server inventory, or -1 when unreadable. `what` names the grid in
    // the log line a failed read writes.
    public static int FreeCellCount(ServerInventory server, string what)
    {
        try
        {
            var occupied = Occupied(server);
            return occupied == null ? -1 : CountFree(occupied);
        }
        catch (Exception ex)
        {
            Log.Debug($"{what} capacity read failed: {ex.GetType().Name}");
            return -1;
        }
    }
}
