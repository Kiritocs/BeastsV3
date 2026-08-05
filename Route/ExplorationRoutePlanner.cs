using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = System.Numerics.Vector2;

namespace BeastsV3.Route;

// Builds an ordered list of exploration waypoints from terrain data and the player's
// position, plus the debug fields used to re-order unvisited points later.
public static class ExplorationRoutePlanner
{
    // Eight compass directions, counter-clockwise from north, for the boundary tracer.
    private static readonly (int dx, int dy)[] Dirs8CcwFromN =
    {
        (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1),
    };

    public sealed record PlanRequest(
        int[][] PathData,
        int MaxX,
        int MaxY,
        Vector2 PlayerPosition,
        int DetectionRadius,
        bool PreferPerimeterFirstRoute,
        bool VisitOuterShellLast,
        bool FollowMapOutlineFirst,
        int EntityExclusionRadius,
        IReadOnlyList<Vector2> ExclusionZonePositions);

    public sealed record ReorderRequest(
        List<Vector2> Unvisited,
        Vector2 PlayerPosition,
        bool FollowMapOutlineFirst,
        bool PreferPerimeterFirstRoute,
        bool[][] ReachableMask,
        int MaxX,
        int MaxY,
        int RouteStep,
        int MinWallDist,
        int[][] DistanceField,
        bool VisitOuterShellLast);

    public sealed record Plan(
        List<Vector2> Route,
        int RouteStep,
        int MinWallDist,
        bool[][] ReachableMask,
        int[][] DistanceField,
        int MaxX,
        int MaxY);

    private readonly record struct Bounds(int MinX, int MinY, int MaxX, int MaxY)
    {
        public bool IsEmpty => MaxX < MinX || MaxY < MinY;
    }

    // Builds the initial route from scratch.
    public static Plan GeneratePlan(PlanRequest request)
    {
        if (request.PathData == null) return null;

        var detectionRadius = Math.Min(request.DetectionRadius, Math.Min(request.MaxX, request.MaxY) / 4);
        if (detectionRadius < 4) return null;

        var routeStep = Math.Max(4, (int)(detectionRadius * 1.1f));
        if (!TryFindNearestWalkableCell(request.PathData, request.MaxX, request.MaxY,
                (int)request.PlayerPosition.X, (int)request.PlayerPosition.Y, routeStep, out var startCell))
            return null;

        var reachableMask = BuildReachableMask(request.PathData, request.MaxX, request.MaxY, startCell.x, startCell.y, out var bounds);
        if (bounds.IsEmpty) return null;

        ApplyEntityExclusions(reachableMask, request.MaxX, request.MaxY, request.ExclusionZonePositions, request.EntityExclusionRadius);

        var distanceField = BuildWallDistanceField(request.PathData, request.MaxX, request.MaxY);
        var minWallDist = Math.Max(6, routeStep / 2);
        var candidates = CollectGridWaypoints(reachableMask, distanceField, bounds, routeStep, minWallDist);
        if (candidates.Count == 0) return null;

        TryInsertPlayerAnchor(candidates, distanceField, reachableMask, request.PlayerPosition, routeStep, minWallDist);

        List<Vector2> route;
        if (request.FollowMapOutlineFirst)
        {
            route = OrderByBoundaryContourFirst(candidates, reachableMask, request.MaxX, request.MaxY,
                routeStep, distanceField, minWallDist, request.PlayerPosition);
        }
        else if (request.PreferPerimeterFirstRoute)
        {
            route = OrderByOuterInnerSweep(candidates, distanceField, routeStep, minWallDist,
                request.PlayerPosition, request.VisitOuterShellLast);
        }
        else
        {
            route = OrderByNearestNeighbor(candidates, request.PlayerPosition);
        }

        return new Plan(route, routeStep, minWallDist, reachableMask, distanceField, request.MaxX, request.MaxY);
    }

    // Re-orders the unvisited waypoints from the player's current position.
    public static List<Vector2> OrderUnvisited(ReorderRequest request)
    {
        if (request.Unvisited.Count <= 1)
            return OrderByNearestNeighbor(request.Unvisited, request.PlayerPosition);

        if (request.FollowMapOutlineFirst && request.ReachableMask != null && request.DistanceField != null)
        {
            return OrderByBoundaryContourFirst(request.Unvisited, request.ReachableMask, request.MaxX, request.MaxY,
                request.RouteStep, request.DistanceField, request.MinWallDist, request.PlayerPosition);
        }

        if (!request.PreferPerimeterFirstRoute || request.DistanceField == null)
            return OrderByNearestNeighbor(request.Unvisited, request.PlayerPosition);

        return OrderByOuterInnerSweep(request.Unvisited, request.DistanceField, request.RouteStep,
            request.MinWallDist, request.PlayerPosition, request.VisitOuterShellLast);
    }

    public static bool IsWalkableCell(int[][] pathData, int y, int x)
    {
        if (pathData == null || y < 0 || y >= pathData.Length || pathData[y] == null) return false;
        if (x < 0 || x >= pathData[y].Length) return false;
        var v = pathData[y][x];
        return v is >= 1 and <= 5;
    }

    // ---- private ----------------------------------------------------------

    // Clears mask cells covered by excluded entities.
    private static void ApplyEntityExclusions(bool[][] mask, int maxX, int maxY,
        IReadOnlyList<Vector2> exclusions, int radius)
    {
        if (exclusions == null || exclusions.Count == 0) return;
        foreach (var loc in exclusions)
        {
            PunchExclusionCircle(mask, maxX, maxY, (int)loc.X, (int)loc.Y, radius);
        }
    }

    private static void PunchExclusionCircle(bool[][] mask, int maxX, int maxY, int cx, int cy, int radius)
    {
        var rSq = radius * radius;
        var yStart = Math.Max(0, cy - radius);
        var yEnd = Math.Min(maxY - 1, cy + radius);
        for (var y = yStart; y <= yEnd; y++)
        {
            if (y >= mask.Length || mask[y] == null) continue;
            var dy = y - cy;
            var xSpan = (int)Math.Sqrt(rSq - dy * dy);
            var xStart = Math.Max(0, cx - xSpan);
            var xEnd = Math.Min(Math.Min(maxX, mask[y].Length) - 1, cx + xSpan);
            for (var x = xStart; x <= xEnd; x++) mask[y][x] = false;
        }
    }

    private static int[][] BuildWallDistanceField(int[][] pathData, int maxX, int maxY)
    {
        var dist = new int[maxY][];
        var queue = new Queue<(int x, int y)>();

        for (var y = 0; y < maxY; y++)
        {
            dist[y] = new int[maxX];
            for (var x = 0; x < maxX; x++)
            {
                if (IsWalkableCell(pathData, y, x)) dist[y][x] = int.MaxValue;
                else queue.Enqueue((x, y));
            }
        }

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            var next = dist[y][x] + 1;
            TryRelax(x + 1, y, next);
            TryRelax(x - 1, y, next);
            TryRelax(x, y + 1, next);
            TryRelax(x, y - 1, next);
        }

        return dist;

        void TryRelax(int x, int y, int d)
        {
            if (x < 0 || x >= maxX || y < 0 || y >= maxY) return;
            if (dist[y][x] <= d) return;
            dist[y][x] = d;
            queue.Enqueue((x, y));
        }
    }

    private static List<Vector2> CollectGridWaypoints(bool[][] mask, int[][] distanceField,
        Bounds bounds, int step, int minDist)
    {
        var result = new List<Vector2>();
        var startY = Math.Max(0, bounds.MinY - step / 2);
        var endY = bounds.MaxY + step / 2;
        var startX = Math.Max(0, bounds.MinX - step / 2);
        var endX = bounds.MaxX + step / 2;

        for (var blockY = startY; blockY <= endY; blockY += step)
        for (var blockX = startX; blockX <= endX; blockX += step)
        {
            if (TryBestCellInBlock(mask, distanceField, blockX, blockY, step, minDist, out var best))
                result.Add(best);
        }

        return result;
    }

    private static bool TryBestCellInBlock(bool[][] mask, int[][] distanceField,
        int startX, int startY, int step, int minDist, out Vector2 best)
    {
        best = default;
        var centerX = startX + step / 2f;
        var centerY = startY + step / 2f;
        var bestDistSq = float.MaxValue;
        var found = false;

        var endY = Math.Min(mask.Length, startY + step);
        for (var y = Math.Max(0, startY); y < endY; y++)
        {
            var row = mask[y];
            if (row == null) continue;

            var endX = Math.Min(row.Length, startX + step);
            for (var x = Math.Max(0, startX); x < endX; x++)
            {
                if (!row[x] || distanceField[y][x] < minDist) continue;

                var dx = x - centerX;
                var dy = y - centerY;
                var dSq = dx * dx + dy * dy;
                if (dSq >= bestDistSq) continue;

                bestDistSq = dSq;
                best = new Vector2(x, y);
                found = true;
            }
        }

        return found;
    }

    private static void TryInsertPlayerAnchor(List<Vector2> candidates, int[][] distanceField,
        bool[][] mask, Vector2 playerPos, int step, int minDist)
    {
        var minDistSq = float.MaxValue;
        foreach (var c in candidates)
        {
            var d = Vector2.DistanceSquared(c, playerPos);
            if (d < minDistSq) minDistSq = d;
        }

        var threshold = Math.Max(6, step / 2);
        if (minDistSq <= (float)(threshold * threshold)) return;

        var px = (int)playerPos.X;
        var py = (int)playerPos.Y;

        for (var radius = 0; radius <= step; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;

                var x = px + dx;
                var y = py + dy;
                if (!IsReachableCell(mask, y, x) || distanceField[y][x] < minDist) continue;

                var anchor = new Vector2(x, y);
                var duplicate = false;
                foreach (var c in candidates)
                {
                    if (Vector2.DistanceSquared(c, anchor) < 1f) { duplicate = true; break; }
                }

                if (!duplicate) { candidates.Add(anchor); return; }
            }
        }
    }

    private static List<Vector2> OrderByOuterInnerSweep(List<Vector2> waypoints, int[][] distanceField,
        int step, int minDist, Vector2 startNear, bool visitOuterShellLast)
    {
        if (waypoints.Count <= 1) return new List<Vector2>(waypoints);

        var tagged = new (Vector2 pos, int dist)[waypoints.Count];
        for (var i = 0; i < waypoints.Count; i++)
        {
            var p = waypoints[i];
            var iy = (int)p.Y;
            var ix = (int)p.X;
            var d = (iy >= 0 && iy < distanceField.Length && distanceField[iy] != null &&
                     ix >= 0 && ix < distanceField[iy].Length)
                ? distanceField[iy][ix] : 0;
            tagged[i] = (p, d);
        }

        var shellSize = Math.Max(1, step / 2);
        var groupsByKey = tagged
            .GroupBy(t => (t.dist - minDist) / shellSize)
            .OrderBy(g => g.Key)
            .ToList();

        if (groupsByKey.Count == 0) return new List<Vector2>(waypoints);

        var shellGroups = new List<(int Key, List<Vector2> Pts)>(groupsByKey.Count);
        foreach (var g in groupsByKey) shellGroups.Add((g.Key, g.Select(t => t.pos).ToList()));

        if (shellGroups.Count == 1 && waypoints.Count >= 2)
        {
            var sortedByWall = tagged.OrderBy(t => t.dist).ToList();
            var mid = sortedByWall.Count / 2;
            shellGroups = new List<(int Key, List<Vector2> Pts)>
            {
                (0, sortedByWall.Take(mid).Select(t => t.pos).ToList()),
                (1, sortedByWall.Skip(mid).Select(t => t.pos).ToList())
            };
        }

        var outermostKey = shellGroups.Min(s => s.Key);
        var traverseOrder = visitOuterShellLast
            ? shellGroups.OrderByDescending(s => s.Key).ToList()
            : shellGroups.OrderBy(s => s.Key).ToList();

        var sumX = 0f;
        var sumY = 0f;
        foreach (var p in waypoints) { sumX += p.X; sumY += p.Y; }
        var mapCentroid = new Vector2(sumX / waypoints.Count, sumY / waypoints.Count);

        var result = new List<Vector2>(waypoints.Count);
        var current = startNear;

        foreach (var (groupKey, pts) in traverseOrder)
        {
            if (pts.Count == 0) continue;

            var ordered = groupKey == outermostKey
                ? OrderByAngle(pts, mapCentroid)
                : BoustrophedonOrder(pts, step);

            AppendShellWaypointsFromNearest(result, ordered, current);
            if (result.Count > 0) current = result[result.Count - 1];
        }

        return result;
    }

    private static void AppendShellWaypointsFromNearest(List<Vector2> result, List<Vector2> ordered, Vector2 current)
    {
        if (ordered.Count == 0) return;
        if (ordered.Count == 1) { result.Add(ordered[0]); return; }

        var n = ordered.Count;
        var startIdx = 0;
        var bestDist = float.MaxValue;
        for (var i = 0; i < n; i++)
        {
            var d = Vector2.DistanceSquared(ordered[i], current);
            if (d < bestDist) { bestDist = d; startIdx = i; }
        }

        if (n == 2)
        {
            result.Add(ordered[startIdx]);
            result.Add(ordered[(startIdx + 1) % 2]);
            return;
        }

        var forwardNext = (startIdx + 1) % n;
        var backwardNext = (startIdx - 1 + n) % n;
        var dForward = Vector2.DistanceSquared(ordered[startIdx], ordered[forwardNext]);
        var dBackward = Vector2.DistanceSquared(ordered[startIdx], ordered[backwardNext]);

        if (dForward <= dBackward)
        {
            for (var i = startIdx; i < n; i++) result.Add(ordered[i]);
            for (var i = 0; i < startIdx; i++) result.Add(ordered[i]);
        }
        else
        {
            for (var i = startIdx; i >= 0; i--) result.Add(ordered[i]);
            for (var i = n - 1; i > startIdx; i--) result.Add(ordered[i]);
        }
    }

    // True when a walkable cell borders unwalkable terrain.
    private static bool IsReachableBoundaryCell(bool[][] mask, int maxX, int maxY, int x, int y)
    {
        if (x < 0 || y < 0 || x >= maxX || y >= maxY) return false;
        if (!mask[y][x]) return false;
        return !IsReachableCell(mask, y - 1, x) || !IsReachableCell(mask, y + 1, x) ||
               !IsReachableCell(mask, y, x - 1) || !IsReachableCell(mask, y, x + 1);
    }

    private static int IndexOfDir8(int dx, int dy)
    {
        for (var i = 0; i < 8; i++)
        {
            if (Dirs8CcwFromN[i].dx == dx && Dirs8CcwFromN[i].dy == dy) return i;
        }
        return -1;
    }

    private static List<(int x, int y)> MooreWalkBoundaryFrom(bool[][] mask, int maxX, int maxY, int startX, int startY)
    {
        var chain = new List<(int x, int y)> { (startX, startY) };
        var cx = startX;
        var cy = startY;
        int bx, by;
        if (startX > 0) { bx = startX - 1; by = startY; }
        else if (startY > 0) { bx = startX; by = startY - 1; }
        else { bx = -1; by = 0; }

        var maxIter = maxX * maxY * 4;
        for (var iter = 0; iter < maxIter; iter++)
        {
            var backIdx = IndexOfDir8(bx - cx, by - cy);
            if (backIdx < 0) backIdx = 6;

            var found = false;
            var nx = 0;
            var ny = 0;
            for (var k = 0; k < 8; k++)
            {
                var i = (backIdx + 1 + k) % 8;
                var (ddx, ddy) = Dirs8CcwFromN[i];
                var tx = cx + ddx;
                var ty = cy + ddy;
                if (tx < 0 || tx >= maxX || ty < 0 || ty >= maxY) continue;
                if (!IsReachableBoundaryCell(mask, maxX, maxY, tx, ty) || (tx == bx && ty == by)) continue;
                nx = tx; ny = ty; found = true; break;
            }

            if (!found) break;
            if (nx == startX && ny == startY && chain.Count > 2) break;

            chain.Add((nx, ny));
            (bx, by) = (cx, cy);
            (cx, cy) = (nx, ny);
        }

        return chain;
    }

    private static List<(int x, int y)> TraceReachableBoundaryMoore(bool[][] mask, int maxX, int maxY,
        int preferNearX, int preferNearY)
    {
        var startX = -1;
        var startY = -1;
        var bestStartD = int.MaxValue;
        for (var y = 0; y < maxY; y++)
        for (var x = 0; x < maxX; x++)
        {
            if (!IsReachableBoundaryCell(mask, maxX, maxY, x, y)) continue;
            var d = (x - preferNearX) * (x - preferNearX) + (y - preferNearY) * (y - preferNearY);
            if (d >= bestStartD) continue;
            bestStartD = d; startX = x; startY = y;
        }

        if (startX < 0) return new List<(int x, int y)>();

        var chainNear = MooreWalkBoundaryFrom(mask, maxX, maxY, startX, startY);

        // Also traces from the top-left boundary cell and keeps the longer chain.
        var rmX = -1;
        var rmY = -1;
        for (var y = 0; y < maxY && rmX < 0; y++)
        for (var x = 0; x < maxX; x++)
        {
            if (!IsReachableBoundaryCell(mask, maxX, maxY, x, y)) continue;
            rmX = x; rmY = y; break;
        }

        if (rmX < 0) return chainNear;

        var chainRm = MooreWalkBoundaryFrom(mask, maxX, maxY, rmX, rmY);
        return chainRm.Count > chainNear.Count ? chainRm : chainNear;
    }

    private static List<Vector2> OrderByBoundaryContourFirst(List<Vector2> candidates, bool[][] mask,
        int maxX, int maxY, int routeStep, int[][] distanceField, int minWallDist, Vector2 playerPos)
    {
        if (candidates.Count <= 1) return new List<Vector2>(candidates);

        var px = (int)Math.Round(playerPos.X);
        var py = (int)Math.Round(playerPos.Y);
        var chain = TraceReachableBoundaryMoore(mask, maxX, maxY, px, py);
        if (chain.Count < 4) return OrderByNearestNeighbor(candidates, playerPos);

        var contourBand = Math.Max(routeStep / 2, 12);
        var nearContourSq = contourBand * contourBand;

        var tagged = new List<(Vector2 p, int k, int d2)>(candidates.Count);
        foreach (var p in candidates)
        {
            var ix = (int)Math.Round(p.X);
            var iy = (int)Math.Round(p.Y);
            var bestK = 0;
            var bestD2 = int.MaxValue;
            for (var k = 0; k < chain.Count; k++)
            {
                var dx = ix - chain[k].x;
                var dy = iy - chain[k].y;
                var d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; bestK = k; }
            }
            tagged.Add((p, bestK, bestD2));
        }

        var boundaryTagged = tagged.Where(t => t.d2 <= nearContourSq).ToList();
        var interiorTagged = tagged.Where(t => t.d2 > nearContourSq).ToList();

        if (boundaryTagged.Count == 0)
        {
            boundaryTagged = tagged.ToList();
            interiorTagged = [];
        }

        var kStart = boundaryTagged
            .OrderBy(t => { var dx = t.p.X - playerPos.X; var dy = t.p.Y - playerPos.Y; return dx * dx + dy * dy; })
            .First().k;

        var nChain = chain.Count;
        var boundaryOrdered = boundaryTagged
            .OrderBy(t => (t.k - kStart + nChain) % nChain)
            .ThenBy(t => { var dx = t.p.X - playerPos.X; var dy = t.p.Y - playerPos.Y; return dx * dx + dy * dy; })
            .ThenBy(t => t.d2)
            .ToList();

        var result = new List<Vector2>(candidates.Count);
        foreach (var t in boundaryOrdered) result.Add(t.p);

        if (interiorTagged.Count == 0) return result;

        var interiorPts = interiorTagged.Select(t => t.p).ToList();
        var last = result.Count > 0 ? result[result.Count - 1] : playerPos;
        var innerOrdered = BoustrophedonOrder(interiorPts, routeStep);
        AppendShellWaypointsFromNearest(result, innerOrdered, last);
        return result;
    }

    private static List<Vector2> OrderByAngle(List<Vector2> pts, Vector2 centre) =>
        pts.OrderBy(p => Math.Atan2(p.Y - centre.Y, p.X - centre.X)).ToList();

    private static List<Vector2> BoustrophedonOrder(List<Vector2> pts, int step)
    {
        if (pts.Count <= 1) return new List<Vector2>(pts);

        var rowSize = Math.Max(1, step / 2);
        var rows = pts
            .GroupBy(p => (int)p.Y / rowSize)
            .OrderBy(g => g.Key)
            .ToList();

        var result = new List<Vector2>(pts.Count);
        var ascending = true;

        foreach (var row in rows)
        {
            var rowPts = ascending
                ? row.OrderBy(p => p.X).ToList()
                : row.OrderByDescending(p => p.X).ToList();
            result.AddRange(rowPts);
            ascending = !ascending;
        }

        return result;
    }

    private static bool TryFindNearestWalkableCell(int[][] pathData, int maxX, int maxY,
        int startX, int startY, int maxSearchRadius, out (int x, int y) cell)
    {
        if (IsWalkableCell(pathData, startY, startX)) { cell = (startX, startY); return true; }

        for (var radius = 1; radius <= maxSearchRadius; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
                var x = startX + dx;
                var y = startY + dy;
                if (x < 0 || x >= maxX || y < 0 || y >= maxY || !IsWalkableCell(pathData, y, x)) continue;
                cell = (x, y); return true;
            }
        }

        cell = default;
        return false;
    }

    private static bool[][] BuildReachableMask(int[][] pathData, int maxX, int maxY,
        int startX, int startY, out Bounds bounds)
    {
        var mask = new bool[maxY][];
        for (var y = 0; y < maxY; y++) mask[y] = new bool[maxX];

        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((startX, startY));
        mask[startY][startX] = true;

        var minX = startX;
        var minY = startY;
        var maxReachableX = startX;
        var maxReachableY = startY;

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxReachableX) maxReachableX = x;
            if (y > maxReachableY) maxReachableY = y;

            TryVisit(x + 1, y);
            TryVisit(x - 1, y);
            TryVisit(x, y + 1);
            TryVisit(x, y - 1);
        }

        bounds = new Bounds(minX, minY, maxReachableX, maxReachableY);
        return mask;

        void TryVisit(int x, int y)
        {
            if (x < 0 || x >= maxX || y < 0 || y >= maxY || mask[y][x] || !IsWalkableCell(pathData, y, x)) return;
            mask[y][x] = true;
            queue.Enqueue((x, y));
        }
    }

    private static bool IsReachableCell(bool[][] mask, int y, int x)
    {
        if (y < 0 || y >= mask.Length) return false;
        var row = mask[y];
        if (row == null || x < 0 || x >= row.Length) return false;
        return row[x];
    }

    private static List<Vector2> OrderByNearestNeighbor(List<Vector2> points, Vector2 startNear)
    {
        if (points.Count <= 1) return new List<Vector2>(points);

        var startIndex = 0;
        var bestStartDistance = float.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var distance = Vector2.DistanceSquared(points[i], startNear);
            if (distance < bestStartDistance) { bestStartDistance = distance; startIndex = i; }
        }

        var result = new List<Vector2>(points.Count);
        var used = new bool[points.Count];
        var currentIndex = startIndex;
        used[currentIndex] = true;
        result.Add(points[currentIndex]);

        for (var i = 1; i < points.Count; i++)
        {
            var nextIndex = -1;
            var bestNextDistance = float.MaxValue;

            for (var j = 0; j < points.Count; j++)
            {
                if (used[j]) continue;
                var distance = Vector2.DistanceSquared(points[currentIndex], points[j]);
                if (distance < bestNextDistance) { bestNextDistance = distance; nextIndex = j; }
            }

            if (nextIndex < 0) break;

            used[nextIndex] = true;
            currentIndex = nextIndex;
            result.Add(points[currentIndex]);
        }

        return result;
    }
}
