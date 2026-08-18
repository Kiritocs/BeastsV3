using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using AStar;
using AStar.Options;
using ExileCore;
using GameOffsets;

namespace BeastsV3.Automation.Navigation;

// A* pathfinder over a walkability grid decoded from Terrain.LayerMelee.
public sealed class Navigator
{
    private readonly WorldGrid _worldGrid;
    private readonly PathFinder _pathFinder;

    public Navigator(GameController gameController)
    {
        var terrain = gameController.IngameState.Data.Terrain;

        var gridWidth = ((int)terrain.NumCols - 1) * 23;
        var gridHeight = ((int)terrain.NumRows - 1) * 23;
        if (gridWidth % 2 != 0) gridWidth++;

        _worldGrid = new WorldGrid(gridHeight, gridWidth + 1);
        _pathFinder = new PathFinder(_worldGrid, new PathFinderOptions
        {
            PunishChangeDirection = false,
            UseDiagonals = true,
            SearchLimit = gridWidth * gridHeight,
        });

        PopulateWorldGrid(terrain, _worldGrid, gameController.Memory);
    }

    // Returns a walkable path from start to end, thinning nodes closer than nodeSize.
    public List<Vector2> FindPath(Vector2 start, Vector2 end, int nodeSize)
    {
        var s = FindNearestWalkable(start);
        var e = FindNearestWalkable(end);

        var pathPoints = _pathFinder.FindPath(
            new Point((int)s.X, (int)s.Y),
            new Point((int)e.X, (int)e.Y));
        if (pathPoints == null || pathPoints.Length == 0) return null;

        var path = new List<Vector2>(pathPoints.Length);
        foreach (var p in pathPoints) path.Add(new Vector2(p.X, p.Y));

        if (path.Count <= 2 || nodeSize <= 0) return path;

        var simplified = new List<Vector2> { path[0] };
        var lastKept = path[0];
        for (var i = 1; i < path.Count - 1; i++)
        {
            var node = path[i];
            if (Vector2.Distance(node, lastKept) < nodeSize) continue;
            simplified.Add(node);
            lastKept = node;
        }
        simplified.Add(path[^1]);
        return simplified;
    }

    private Vector2 FindNearestWalkable(Vector2 point)
    {
        var cx = Math.Clamp((int)point.X, 0, _worldGrid.Width - 1);
        var cy = Math.Clamp((int)point.Y, 0, _worldGrid.Height - 1);
        if (_worldGrid[cy, cx] > 0) return new Vector2(cx, cy);

        const int maxSearchRadius = 12;
        for (var r = 1; r <= maxSearchRadius; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                var x = cx + dx;
                var y = cy + dy;
                if (x < 0 || x >= _worldGrid.Width || y < 0 || y >= _worldGrid.Height) continue;
                if (_worldGrid[y, x] > 0) return new Vector2(x, y);
            }
        }

        return new Vector2(cx, cy);
    }

    private static void PopulateWorldGrid(TerrainData terrain, WorldGrid grid, ExileCore.Shared.Interfaces.IMemory memory)
    {
        // LayerMelee is a byte range given as First/Last addresses.
        var byteCount = (int)(terrain.LayerMelee.Last - terrain.LayerMelee.First);
        var bytes = memory.ReadBytes(terrain.LayerMelee.First, byteCount);
        var offset = 0;

        for (var row = 0; row < grid.Height; row++)
        {
            for (var col = 0; col < grid.Width; col += 2)
            {
                if (offset + (col >> 1) >= bytes.Length) break;

                var tile = bytes[offset + (col >> 1)];
                var lo = tile & 0xF;
                grid[row, col] = (short)(lo > 0 ? 1 : 0);

                if (col + 1 < grid.Width)
                {
                    var hi = tile >> 4;
                    grid[row, col + 1] = (short)(hi > 0 ? 1 : 0);
                }
            }
            offset += terrain.BytesPerRow;
        }
    }
}
