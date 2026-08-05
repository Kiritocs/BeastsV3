using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeastsV3.Automation.Input;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using GridVec2 = System.Numerics.Vector2;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Navigation;

// Walks toward an entity by A*-pathing to it, projecting a lookahead node to screen
// space and clicking it. The Navigator is rebuilt on area change.
public sealed class Navigate
{
    private const int NodeSize = 18;
    private const int LookAheadIndex = 4;
    private const float MinMoveDistance = 12f;
    private const float OffScreenClampRadius = 400f;

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly AutomationInput _input;

    private Navigator _navigator;
    private string _navigatorAreaHash;

    public Navigate(GameController game, BeastsSettings settings, AutomationInput input)
    {
        _game = game;
        _settings = settings;
        _input = input;
    }

    public void InvalidateNavigator()
    {
        _navigator = null;
        _navigatorAreaHash = null;
    }

    public async Task<bool> WalkTowardsAsync(Entity entity)
    {
        _input.ThrowIfStopRequested();

        var playerPositioned = _game?.Game?.IngameState?.Data?.LocalPlayer?.GetComponent<Positioned>();
        var targetPositioned = entity?.GetComponent<Positioned>();
        var navigator = GetOrBuildNavigator();
        if (playerPositioned == null || targetPositioned == null || navigator == null) return false;

        var path = navigator.FindPath(playerPositioned.GridPosNum, targetPositioned.GridPosNum, NodeSize);
        if (path == null || path.Count == 0) return false;

        var destination = SelectDestination(path, playerPositioned.GridPosNum);
        if (!TryProjectGridToScreen(destination, out var screenPos)) return false;

        await _input.ClickAtAsync(
            screenPos, System.Windows.Forms.MouseButtons.Left,
            preDelayMs: _settings.Timing.Clicks.UiClickPreDelayMs.Value,
            postDelayMs: Math.Max(_settings.Timing.Polling.FastPollDelayMs.Value, _input.ClickPostDelayFloor()));
        return true;
    }

    public float? DistanceToEntity(Entity entity)
    {
        var playerPositioned = _game?.Game?.IngameState?.Data?.LocalPlayer?.GetComponent<Positioned>();
        var targetPositioned = entity?.GetComponent<Positioned>();
        if (playerPositioned == null || targetPositioned == null) return null;
        return GridVec2.Distance(playerPositioned.GridPosNum, targetPositioned.GridPosNum);
    }

    // ---- private -------------------------------------------------------

    // Returns the current area's navigator, rebuilding it after an area change.
    private Navigator GetOrBuildNavigator()
    {
        if (_game?.IngameState?.Data?.Terrain == null) return null;

        var areaHash = GameHelpers.TryGetAreaHashText(_game.Area?.CurrentArea) ?? string.Empty;
        if (_navigator != null && string.Equals(_navigatorAreaHash, areaHash, StringComparison.Ordinal))
            return _navigator;

        _navigator = new Navigator(_game);
        _navigatorAreaHash = areaHash;
        return _navigator;
    }

    private static GridVec2 SelectDestination(IReadOnlyList<GridVec2> path, GridVec2 playerGridPos)
    {
        var maxIndex = Math.Min(path.Count - 1, LookAheadIndex);
        for (var i = maxIndex; i >= 0; i--)
        {
            if (GridVec2.Distance(path[i], playerGridPos) >= MinMoveDistance) return path[i];
        }
        return path[^1];
    }

    private bool TryProjectGridToScreen(GridVec2 gridPos, out SharpVec2 position)
    {
        position = default;
        var data = _game?.Game?.IngameState?.Data;
        var camera = _game?.Game?.IngameState?.Camera;
        var window = _game?.Window;
        if (data == null || camera == null || window == null) return false;

        var relative = camera.WorldToScreen(data.ToWorldWithTerrainHeight(gridPos));
        if (!IsFinite(relative)) return false;

        var windowRect = window.GetWindowRectangle();
        var clamped = ClampToVisibleArea(relative, windowRect.Width, windowRect.Height);
        position = new SharpVec2(windowRect.X + clamped.X, windowRect.Y + clamped.Y);
        return true;
    }

    private static bool IsFinite(GridVec2 v) =>
        !float.IsNaN(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.X) && !float.IsInfinity(v.Y);

    // Pulls an off-screen point back onto a circle around the screen centre.
    private static GridVec2 ClampToVisibleArea(GridVec2 position, float width, float height)
    {
        const float left = 10f;
        const float top = 10f;
        var right = Math.Max(20f, width - 20f);
        var bottom = Math.Max(20f, height - 130f);
        if (position.X >= left && position.X <= right && position.Y >= top && position.Y <= bottom)
            return position;

        var center = new GridVec2(width / 2f, bottom / 2f);
        var delta = position - center;
        if (delta.LengthSquared() < 0.001f) return center;

        var clamped = center + GridVec2.Normalize(delta) * OffScreenClampRadius;
        return new GridVec2(
            Math.Clamp(clamped.X, left, right),
            Math.Clamp(clamped.Y, top, bottom));
    }
}
