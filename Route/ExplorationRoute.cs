using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.Components;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;

namespace BeastsV3.Route;

// Holds exploration-route state and draws it on the large map: regeneration, waypoint
// visits, exclusion zones, debug overlays and the Radar pathfinding bridge.
public sealed class ExplorationRoute
{
    // 48 unit-circle vertices used for coverage, detection and exclusion rings.
    private static readonly Vector2[] RingUnitPoints = BuildUnitCircle(48);

    private readonly GameController _game;
    private readonly BeastsSettings _settings;

    // Route state -----------------------------------------------------------
    private List<Vector2> _route = new();
    private readonly HashSet<int> _visited = new();
    private readonly List<Vector2> _exclusionZones = new();
    private bool[][] _reachableMask;
    private int[][] _distanceField;
    private int _maxX;
    private int _maxY;
    private int _cachedRouteStep = 4;
    private int _cachedMinWallDist = 6;

    // Snapshots compared each frame to decide when to regenerate.
    private bool _needsRegen = true;
    private bool? _lastEnabled;
    private int _lastDetectionRadius = -1;
    private bool? _lastPreferPerimeter;
    private bool? _lastVisitOuterShellLast;
    private bool? _lastFollowMapOutlineFirst;
    private string _lastExcludedPathsSnapshot;
    private int _lastEntityExclusionRadius = -1;

    // Bridge to Radar.LookForRoute for the path to the next waypoint.
    private CancellationTokenSource _pathFindingCts = new();
    private List<Vector2i> _explorationPath;
    private int _explorationPathForIdx = -1;

    public ExplorationRoute(GameController game, BeastsSettings settings)
    {
        _game = game;
        _settings = settings;
    }

    public bool IsEnabled => _settings.ExplorationRoute.Enabled.Value;

    // True when the route needs the transparent map overlay opened.
    public bool WantsMapOverlay => IsEnabled && (
        _settings.ExplorationRoute.ShowExplorationRoute.Value ||
        _settings.ExplorationRoute.ShowPathsToBeasts.Value ||
        _settings.ExplorationRoute.ShowCoverageOnMiniMap.Value ||
        _settings.ExplorationRoute.ShowEntityExclusionZones.Value ||
        _settings.ExplorationRoute.Debug.ShowWalkableCells.Value ||
        _settings.ExplorationRoute.Debug.ShowObstacleCells.Value ||
        _settings.ExplorationRoute.Debug.ShowDistanceField.Value);

    public void OnAreaChanged()
    {
        _needsRegen = true;
        CancelPathFinding();
    }

    public void RequestRegen()
    {
        if (!IsEnabled) { ClearRouteState(); _needsRegen = false; return; }
        _needsRegen = true;
        CancelPathFinding();
    }

    // Draws the route, coverage circles and debug overlays onto the large map.
    public void Render(ImDrawListPtr drawList, Vector2 mapCenter, Func<Vector2, float, Vector2> gridToScreen)
    {
        if (!IsEnabled) { EnsureCurrent(); return; }
        EnsureCurrent();

        if (!TryGetPlayerGridPos(out var playerGridPos)) return;

        if (_route.Count > 0)
        {
            UpdateVisitedWaypoints(playerGridPos);
            var nextIdx = GetNextWaypointIndex();

            var er = _settings.ExplorationRoute;
            var style = er.Style;

            if (er.ShowCoverageOnMiniMap.Value)
            {
                DrawCoverageCircles(drawList, mapCenter, playerGridPos, gridToScreen,
                    er.DetectionRadius.Value, style);
            }

            if (er.ShowExplorationRoute.Value)
            {
                DrawRouteLines(drawList, mapCenter, playerGridPos, gridToScreen, nextIdx, style);
                DrawWaypointDots(drawList, mapCenter, playerGridPos, gridToScreen, nextIdx, style);
                DrawDetectionRadius(drawList, mapCenter, gridToScreen, er.DetectionRadius.Value, style);
            }

            if (er.ShowPathsToBeasts.Value)
            {
                DrawPathToNextWaypoint(drawList, mapCenter, playerGridPos, gridToScreen, nextIdx);
            }
        }

        if (_settings.ExplorationRoute.ShowEntityExclusionZones.Value && _exclusionZones.Count > 0)
        {
            DrawEntityExclusionZones(drawList, mapCenter, playerGridPos, gridToScreen);
        }

        DrawDebugOverlays(drawList, mapCenter, playerGridPos, gridToScreen);
    }

    // Draws the settings editor for excluded entity paths.
    public void DrawExcludedEntityPathsPanel()
    {
        var er = _settings.ExplorationRoute;
        var paths = ParseExcludedPaths(er.ExcludedEntityPaths.Value);

        ImGui.TextDisabled("One row per path. Edits sync back to the text field and refresh the route.");
        if (ImGui.Button("Add path##BeastsV3ExPathAdd"))
        {
            paths.Add(string.Empty);
            CommitExcludedPaths(paths);
        }

        ImGui.BeginChild("##BeastsV3ExPathScroll", new Vector2(0, 200), ImGuiChildFlags.Border);
        for (var i = 0; i < paths.Count; i++)
        {
            ImGui.PushID(i);
            var line = paths[i];
            var avail = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth(Math.Max(50f, avail - 76f));
            if (ImGui.InputText("##line", ref line, 2048u))
            {
                paths[i] = line;
                CommitExcludedPaths(paths);
            }

            ImGui.SameLine();
            if (ImGui.Button("Remove##BeastsV3ExPathRm"))
            {
                paths.RemoveAt(i);
                CommitExcludedPaths(paths);
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    // ---- private: dirty-tracking + regen ----------------------------------

    // Regenerates the route when settings, area or exclusions have changed.
    private void EnsureCurrent()
    {
        var enabled = IsEnabled;
        if (_lastEnabled != enabled)
        {
            _lastEnabled = enabled;
            if (!enabled) { ClearRouteState(); _needsRegen = false; return; }
            _needsRegen = true;
            CancelPathFinding();
        }

        if (!enabled) return;

        var er = _settings.ExplorationRoute;
        if (_lastDetectionRadius != er.DetectionRadius.Value)
        {
            _lastDetectionRadius = er.DetectionRadius.Value;
            _needsRegen = true;
            CancelPathFinding();
        }
        if (_lastPreferPerimeter != er.PreferPerimeterFirstRoute.Value)
        {
            _lastPreferPerimeter = er.PreferPerimeterFirstRoute.Value;
            _needsRegen = true;
            CancelPathFinding();
        }
        if (_lastVisitOuterShellLast != er.VisitOuterShellLast.Value)
        {
            _lastVisitOuterShellLast = er.VisitOuterShellLast.Value;
            _needsRegen = true;
            CancelPathFinding();
        }
        if (_lastFollowMapOutlineFirst != er.FollowMapOutlineFirst.Value)
        {
            _lastFollowMapOutlineFirst = er.FollowMapOutlineFirst.Value;
            _needsRegen = true;
            CancelPathFinding();
        }

        var excluded = er.ExcludedEntityPaths.Value ?? string.Empty;
        if (_lastExcludedPathsSnapshot == null) _lastExcludedPathsSnapshot = excluded;
        else if (!string.Equals(_lastExcludedPathsSnapshot, excluded, StringComparison.Ordinal))
        {
            _lastExcludedPathsSnapshot = excluded;
            _needsRegen = true;
            CancelPathFinding();
        }

        if (_lastEntityExclusionRadius < 0) _lastEntityExclusionRadius = er.EntityExclusionRadius.Value;
        else if (_lastEntityExclusionRadius != er.EntityExclusionRadius.Value)
        {
            _lastEntityExclusionRadius = er.EntityExclusionRadius.Value;
            _needsRegen = true;
            CancelPathFinding();
        }

        if (_needsRegen)
        {
            _needsRegen = false;
            GenerateRoute();
        }
    }

    private void GenerateRoute()
    {
        if (!IsEnabled) { ClearRouteState(); return; }

        _route.Clear();
        _visited.Clear();
        _exclusionZones.Clear();

        var pathData = _game?.IngameState?.Data?.RawPathfindingData;
        var areaDim = _game?.IngameState?.Data?.AreaDimensions;
        if (pathData == null || areaDim == null || !TryGetPlayerGridPos(out var playerPos)) return;

        var maxX = areaDim.Value.X;
        var maxY = Math.Min(pathData.Length, areaDim.Value.Y);
        var er = _settings.ExplorationRoute;

        var exclusions = ResolveEntityExclusionZones();
        _exclusionZones.AddRange(exclusions);

        var plan = ExplorationRoutePlanner.GeneratePlan(new ExplorationRoutePlanner.PlanRequest(
            pathData, maxX, maxY, playerPos,
            er.DetectionRadius.Value,
            er.PreferPerimeterFirstRoute.Value,
            er.VisitOuterShellLast.Value,
            er.FollowMapOutlineFirst.Value,
            er.EntityExclusionRadius.Value,
            exclusions));
        if (plan == null) return;

        _route = plan.Route;
        _reachableMask = plan.ReachableMask;
        _distanceField = plan.DistanceField;
        _maxX = plan.MaxX;
        _maxY = plan.MaxY;
        _cachedRouteStep = plan.RouteStep;
        _cachedMinWallDist = plan.MinWallDist;
    }

    private void ClearRouteState()
    {
        _route.Clear();
        _visited.Clear();
        _exclusionZones.Clear();
        _reachableMask = null;
        _distanceField = null;
        _maxX = 0;
        _maxY = 0;
        _cachedRouteStep = 4;
        _cachedMinWallDist = 6;
    }

    private List<Vector2> ResolveEntityExclusionZones()
    {
        var raw = _settings.ExplorationRoute.ExcludedEntityPaths.Value;
        var excludedPaths = SplitExcludedPaths(raw);
        if (excludedPaths.Count == 0) return [];

        var clusterTarget = _game?.PluginBridge
            .GetMethod<Func<string, int, Vector2[]>>("Radar.ClusterTarget");
        if (clusterTarget == null) return [];

        var positions = new List<Vector2>();
        foreach (var path in excludedPaths)
        {
            var locations = clusterTarget(path, 1);
            if (locations != null) positions.AddRange(locations);
        }
        return positions;
    }

    // ---- private: waypoint visit tracking + re-order ----------------------

    // Marks waypoints the player has reached and re-orders the remainder.
    private void UpdateVisitedWaypoints(Vector2 playerGridPos)
    {
        var visitRadius = _settings.ExplorationRoute.WaypointVisitRadius.Value;
        var visitSq = (float)(visitRadius * visitRadius);
        var anyNew = false;

        for (var i = 0; i < _route.Count; i++)
        {
            if (_visited.Contains(i)) continue;
            var d = _route[i] - playerGridPos;
            if (d.X * d.X + d.Y * d.Y <= visitSq)
            {
                _visited.Add(i);
                anyNew = true;
            }
        }

        if (anyNew) ReSortUnvisited(playerGridPos);
    }

    private void ReSortUnvisited(Vector2 playerGridPos)
    {
        var visited = new List<Vector2>();
        var unvisited = new List<Vector2>();
        for (var i = 0; i < _route.Count; i++)
        {
            if (_visited.Contains(i)) visited.Add(_route[i]);
            else unvisited.Add(_route[i]);
        }
        if (unvisited.Count == 0) return;

        var sorted = ExplorationRoutePlanner.OrderUnvisited(new ExplorationRoutePlanner.ReorderRequest(
            unvisited, playerGridPos,
            _settings.ExplorationRoute.FollowMapOutlineFirst.Value,
            _settings.ExplorationRoute.PreferPerimeterFirstRoute.Value,
            _reachableMask, _maxX, _maxY,
            _cachedRouteStep, _cachedMinWallDist, _distanceField,
            _settings.ExplorationRoute.VisitOuterShellLast.Value));

        _route = visited.Concat(sorted).ToList();
        _visited.Clear();
        for (var i = 0; i < visited.Count; i++) _visited.Add(i);
        CancelPathFinding();
    }

    private int GetNextWaypointIndex()
    {
        for (var i = 0; i < _route.Count; i++)
            if (!_visited.Contains(i)) return i;
        return -1;
    }

    // ---- private: drawing ------------------------------------------------

    // Draws the coverage and detection rings around each waypoint.
    private void DrawCoverageCircles(ImDrawListPtr drawList, Vector2 mapCenter, Vector2 playerGridPos,
        Func<Vector2, float, Vector2> gridToScreen, int coverageRadius, ExplorationRouteStyleSettings style)
    {
        var col = ImGuiEx.ToU32(style.CoverageColor.Value);
        for (var i = 0; i < _route.Count; i++)
        {
            if (_visited.Contains(i)) continue;
            var centerScreen = mapCenter + gridToScreen(_route[i] - playerGridPos, 0);
            DrawRingOnMap(drawList, centerScreen, coverageRadius, col, style.CoverageLineThickness.Value, gridToScreen);
        }
    }

    private void DrawRouteLines(ImDrawListPtr drawList, Vector2 mapCenter, Vector2 playerGridPos,
        Func<Vector2, float, Vector2> gridToScreen, int nextIdx, ExplorationRouteStyleSettings style)
    {
        var visitedCol = ImGuiEx.ToU32(style.VisitedLineColor.Value);
        var routeCol = ImGuiEx.ToU32(style.RouteLineColor.Value);
        var thickness = style.RouteLineThickness.Value;

        for (var i = 0; i < _route.Count - 1; i++)
        {
            var a = mapCenter + gridToScreen(_route[i] - playerGridPos, 0);
            var b = mapCenter + gridToScreen(_route[i + 1] - playerGridPos, 0);
            var col = _visited.Contains(i) && _visited.Contains(i + 1) ? visitedCol : routeCol;
            drawList.AddLine(a, b, col, thickness);
        }
    }

    private void DrawWaypointDots(ImDrawListPtr drawList, Vector2 mapCenter, Vector2 playerGridPos,
        Func<Vector2, float, Vector2> gridToScreen, int nextIdx, ExplorationRouteStyleSettings style)
    {
        var waypointCol = ImGuiEx.ToU32(style.WaypointColor.Value);
        var nextCol = ImGuiEx.ToU32(style.NextWaypointColor.Value);
        var waypointR = style.WaypointDotRadius.Value;
        var nextR = style.NextWaypointDotRadius.Value;

        for (var i = 0; i < _route.Count; i++)
        {
            var pos = mapCenter + gridToScreen(_route[i] - playerGridPos, 0);
            if (i == nextIdx) drawList.AddCircleFilled(pos, nextR, nextCol);
            else if (!_visited.Contains(i)) drawList.AddCircleFilled(pos, waypointR, waypointCol);
        }
    }

    private void DrawDetectionRadius(ImDrawListPtr drawList, Vector2 mapCenter,
        Func<Vector2, float, Vector2> gridToScreen, int radius, ExplorationRouteStyleSettings style)
    {
        var col = ImGuiEx.ToU32(style.DetectionRadiusColor.Value);
        DrawRingOnMap(drawList, mapCenter, radius, col, style.DetectionRadiusThickness.Value, gridToScreen);
    }

    private void DrawEntityExclusionZones(ImDrawListPtr drawList, Vector2 mapCenter, Vector2 playerGridPos,
        Func<Vector2, float, Vector2> gridToScreen)
    {
        var er = _settings.ExplorationRoute;
        var col = ImGuiEx.ToU32(er.ExclusionZoneColor.Value);
        foreach (var loc in _exclusionZones)
        {
            var centerScreen = mapCenter + gridToScreen(loc - playerGridPos, 0);
            DrawRingOnMap(drawList, centerScreen, er.EntityExclusionRadius.Value, col, 1.5f, gridToScreen);
        }
    }

    private static void DrawRingOnMap(ImDrawListPtr drawList, Vector2 center, int gridRadius,
        uint color, float thickness, Func<Vector2, float, Vector2> gridToScreen)
    {
        Vector2? prev = null;
        foreach (var pt in RingUnitPoints)
        {
            var screen = center + gridToScreen(pt * gridRadius, 0);
            if (prev.HasValue) drawList.AddLine(prev.Value, screen, color, thickness);
            prev = screen;
        }
    }

    // ---- private: debug + path-to-waypoint -------------------------------

    // Draws the walkability, exclusion and pathfinding debug layers.
    private void DrawDebugOverlays(ImDrawListPtr drawList, Vector2 mapCenter, Vector2 playerGridPos,
        Func<Vector2, float, Vector2> gridToScreen)
    {
        var dbg = _settings.ExplorationRoute.Debug;
        var showWalkable = dbg.ShowWalkableCells.Value;
        var showObstacles = dbg.ShowObstacleCells.Value;
        var showDistField = dbg.ShowDistanceField.Value;
        if (!showWalkable && !showObstacles && !showDistField) return;

        var pathData = _game?.IngameState?.Data?.RawPathfindingData;
        if (pathData == null) return;

        var px = (int)playerGridPos.X;
        var py = (int)playerGridPos.Y;
        var radius = dbg.DebugCellRadius.Value;
        var sampleStep = Math.Max(1, dbg.DebugCellSampleStep.Value);
        var dotRadius = dbg.DebugDotRadius.Value;

        var maxY = _maxY > 0 ? _maxY : pathData.Length;
        var maxX = _maxX > 0 ? _maxX : (pathData.Length > 0 ? pathData[0]?.Length ?? 0 : 0);

        var yStart = Math.Max(0, py - radius);
        var yEnd = Math.Min(maxY, py + radius);
        var xStart = Math.Max(0, px - radius);
        var xEnd = Math.Min(maxX, px + radius);

        var walkableCol = ImGuiEx.ToU32(dbg.WalkableColor.Value);
        var obstacleCol = ImGuiEx.ToU32(dbg.ObstacleColor.Value);

        var maxDist = 1;
        if (showDistField && _distanceField != null)
        {
            for (var cy = yStart; cy < yEnd; cy += sampleStep)
            {
                var distRow = _distanceField[cy];
                if (distRow == null) continue;
                for (var cx = xStart; cx < xEnd; cx += sampleStep)
                {
                    if (cx >= distRow.Length) break;
                    var d = distRow[cx];
                    if (d != int.MaxValue && d > maxDist) maxDist = d;
                }
            }
        }

        for (var cy = yStart; cy < yEnd; cy += sampleStep)
        for (var cx = xStart; cx < xEnd; cx += sampleStep)
        {
            var walkable = ExplorationRoutePlanner.IsWalkableCell(pathData, cy, cx);
            var mapPos = mapCenter + gridToScreen(new Vector2(cx, cy) - playerGridPos, 0);

            if (showDistField && walkable && _distanceField != null)
            {
                var distRow = _distanceField[cy];
                var d = distRow != null && cx < distRow.Length ? distRow[cx] : 0;
                if (d == int.MaxValue) d = maxDist;
                drawList.AddCircleFilled(mapPos, dotRadius, DistanceHeatmapColor(d, maxDist));
            }
            else if (showWalkable && walkable)
            {
                drawList.AddCircleFilled(mapPos, dotRadius, walkableCol);
            }
            else if (showObstacles && !walkable)
            {
                var adj = ExplorationRoutePlanner.IsWalkableCell(pathData, cy, cx + sampleStep) ||
                          ExplorationRoutePlanner.IsWalkableCell(pathData, cy, cx - sampleStep) ||
                          ExplorationRoutePlanner.IsWalkableCell(pathData, cy + sampleStep, cx) ||
                          ExplorationRoutePlanner.IsWalkableCell(pathData, cy - sampleStep, cx);
                if (adj) drawList.AddCircleFilled(mapPos, dotRadius, obstacleCol);
            }
        }
    }

    private void DrawPathToNextWaypoint(ImDrawListPtr drawList, Vector2 mapCenter, Vector2 playerGridPos,
        Func<Vector2, float, Vector2> gridToScreen, int nextIdx)
    {
        if (nextIdx < 0) return;
        if (nextIdx != _explorationPathForIdx)
        {
            _explorationPathForIdx = nextIdx;
            _explorationPath = null;
            RequestExplorationPath(nextIdx, _route[nextIdx]);
        }

        var path = _explorationPath;
        if (path == null || path.Count == 0) return;

        var pathCol = ImGuiEx.ToU32(new Color(255, 165, 0, 216));
        const float thickness = 2f;

        var heightData = _game?.IngameState?.Data?.RawTerrainHeightData;
        var playerHeight = _game?.Player?.GetComponent<Render>()?.RenderStruct.Height ?? 0;
        playerHeight = -playerHeight;

        Vector2? prev = null;
        var skip = 0;
        foreach (var node in path)
        {
            if (++skip % 2 != 0) continue;

            var nodeHeight = TryGetHeight(heightData, node.X, node.Y);
            var pos = mapCenter + gridToScreen(new Vector2(node.X, node.Y) - playerGridPos, playerHeight + nodeHeight);
            if (prev.HasValue) drawList.AddLine(prev.Value, pos, pathCol, thickness);
            prev = pos;
        }
    }

    private void RequestExplorationPath(int waypointIdx, Vector2 gridPos)
    {
        var lookForRoute = _game?.PluginBridge
            .GetMethod<Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>>("Radar.LookForRoute");
        if (lookForRoute == null) return;

        var token = _pathFindingCts.Token;
        _ = lookForRoute(gridPos, path =>
        {
            if (path != null && !token.IsCancellationRequested && _explorationPathForIdx == waypointIdx)
                _explorationPath = path;
        }, token);
    }

    private void CancelPathFinding()
    {
        _pathFindingCts.Cancel();
        _pathFindingCts = new CancellationTokenSource();
        _explorationPath = null;
        _explorationPathForIdx = -1;
    }

    // ---- private: excluded-path list helpers -----------------------------

    // Writes the excluded-path list back to settings and flags a regeneration.
    private void CommitExcludedPaths(List<string> paths)
    {
        var joined = string.Join("\n", paths.Select(p => p?.Trim() ?? string.Empty));
        var er = _settings.ExplorationRoute;
        if (string.Equals(er.ExcludedEntityPaths.Value, joined, StringComparison.Ordinal)) return;

        er.ExcludedEntityPaths.Value = joined;
        _needsRegen = true;
    }

    private static List<string> ParseExcludedPaths(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return new List<string>();
        return raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.None).ToList();
    }

    private static List<string> SplitExcludedPaths(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw
            .Split(new[] { '\n', '\r', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    // ---- private: game-state helpers -------------------------------------

    private bool TryGetPlayerGridPos(out Vector2 playerGridPos)
    {
        var positioned = _game?.Game?.IngameState?.Data?.LocalPlayer?.GetComponent<Positioned>();
        if (positioned == null) { playerGridPos = default; return false; }
        playerGridPos = new Vector2(positioned.GridPosNum.X, positioned.GridPosNum.Y);
        return true;
    }

    private static float TryGetHeight(float[][] heightData, int x, int y)
    {
        if (heightData == null || y < 0 || y >= heightData.Length) return 0;
        var row = heightData[y];
        return row != null && x >= 0 && x < row.Length ? row[x] : 0;
    }

    private static uint DistanceHeatmapColor(int dist, int maxDist)
    {
        var t = maxDist > 0 ? Math.Clamp((float)dist / maxDist, 0f, 1f) : 0f;
        byte r, g, b;
        if (t < 0.5f)
        {
            var u = t * 2f;
            r = (byte)(255 * (1f - u));
            g = (byte)(255 * u);
            b = 0;
        }
        else
        {
            var u = (t - 0.5f) * 2f;
            r = 0;
            g = (byte)(255 * (1f - u));
            b = (byte)(255 * u);
        }
        return ImGuiEx.ToU32(new Color(r, g, b, (byte)180));
    }

    private static Vector2[] BuildUnitCircle(int segments)
    {
        var points = new Vector2[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = i * 2f * MathF.PI / segments;
            points[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
        return points;
    }
}
