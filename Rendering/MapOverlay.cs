using System;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Route;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ImGuiNET;
using SharpDX;
using SharpVec2 = SharpDX.Vector2;
using Vector2 = System.Numerics.Vector2;

namespace BeastsV3.Rendering;

// Sets up the transparent overlay drawn over the large map, and renders beast markers
// and the exploration route into it.
public sealed class MapOverlay
{
    private const int TileToGridConversion = 23;
    private const int TileToWorldConversion = 250;
    private const float GridToWorldMultiplier = TileToWorldConversion / (float)TileToGridConversion;
    private const double CameraAngle = 38.7 * Math.PI / 180;
    private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
    private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly BeastTracker _tracker;
    private readonly BeastsV3.Prices.PriceService _prices;
    private readonly WorldLabels _worldLabels;
    private readonly ExplorationRoute _explorationRoute;
    private readonly Func<bool> _isInFinalizedMap;

    // Map rect and scale, cached each frame for other renderers.
    public RectangleF MapRect { get; private set; }
    public float MapScale { get; private set; }
    public ImDrawListPtr MapDrawList { get; private set; }

    public MapOverlay(GameController game, BeastsSettings settings, BeastTracker tracker,
        BeastsV3.Prices.PriceService prices, WorldLabels worldLabels, ExplorationRoute explorationRoute,
        Func<bool> isInFinalizedMap)
    {
        _game = game;
        _settings = settings;
        _tracker = tracker;
        _prices = prices;
        _worldLabels = worldLabels;
        _explorationRoute = explorationRoute;
        _isInFinalizedMap = isInFinalizedMap ?? (() => false);
    }

    public bool IsLargeMapVisible =>
        _game?.IngameState?.IngameUi?.Map?.LargeMap?.IsVisible == true;

    public void Render()
    {
        if (!IsLargeMapVisible) return;
        if (!ShouldDrawOverlay()) return;

        var ui = _game.IngameState.IngameUi;
        var mapRect = _game.Window.GetWindowRectangle();
        mapRect.Location = SharpVec2.Zero;

        if (ui.OpenRightPanel?.IsVisible == true)
            mapRect.Right = ui.OpenRightPanel.GetClientRectCache.Left;
        if (ui.OpenLeftPanel?.IsVisible == true)
            mapRect.Left = ui.OpenLeftPanel.GetClientRectCache.Right;

        MapRect = mapRect;

        ImGui.SetNextWindowSize(new Vector2(mapRect.Width, mapRect.Height));
        ImGui.SetNextWindowPos(new Vector2(mapRect.Left, mapRect.Top));
        ImGui.Begin("##BeastsV3MapOverlay",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoBackground);

        MapDrawList = ImGui.GetWindowDrawList();
        var largeMap = _game.IngameState.IngameUi.Map.LargeMap;
        MapScale = largeMap.MapScale;

        // Markers are hidden in a banked map; the route below still draws.
        if (_settings.MapRender.ShowBeastsOnMap.Value && !_isInFinalizedMap())
        {
            DrawBeastMarkers(largeMap.MapCenter);
        }

        _explorationRoute?.Render(MapDrawList, largeMap.MapCenter, GridDeltaToScreenDelta);

        ImGui.End();
    }

    // Converts a player-relative grid delta to a screen delta on the large map.
    public Vector2 GridDeltaToScreenDelta(Vector2 gridDelta, float deltaZ)
    {
        deltaZ /= GridToWorldMultiplier;
        return MapScale * new Vector2(
            (gridDelta.X - gridDelta.Y) * CameraAngleCos,
            (deltaZ - (gridDelta.X + gridDelta.Y)) * CameraAngleSin);
    }

    // True when Render has anything to draw.
    private bool ShouldDrawOverlay() =>
        (_settings.MapRender.ShowBeastsOnMap.Value && !_isInFinalizedMap()) ||
        (_explorationRoute?.WantsMapOverlay ?? false);

    // Draws markers from the tracker's per-frame snapshot, including remembered positions.
    private void DrawBeastMarkers(Vector2 mapCenter)
    {
        var showEnabledOnly = _settings.MapRender.ShowEnabledOnly.Value;
        var showCached = _settings.MapRender.ShowCachedTrackedBeasts.Value;

        if (!TryGetPlayerContext(out var playerGridPos, out var playerHeight, out var heightData))
            return;

        foreach (var marker in _tracker.Markers)
        {
            if (!marker.IsLive && !showCached) continue;
            // Talisman-only beasts are included and coloured differently.
            if (showEnabledOnly && !_prices.IsShownWhileEnabledOnly(marker.BeastName)) continue;

            var grid = marker.GridPos;
            var beastHeight = TryGetHeight(heightData, (int)grid.X, (int)grid.Y);
            var mapDelta = GridDeltaToScreenDelta(new Vector2(grid.X, grid.Y) - playerGridPos, playerHeight + beastHeight);
            _worldLabels.DrawMapMarker(MapDrawList, marker.BeastName, marker.CaptureState, mapCenter + mapDelta);
        }
    }

    private bool TryGetPlayerContext(out Vector2 playerGridPos, out float playerHeight, out float[][] heightData)
    {
        playerGridPos = default;
        playerHeight = 0;
        heightData = null;

        var player = _game?.Player;
        var positioned = player?.GetComponent<Positioned>();
        var render = player?.GetComponent<Render>();
        if (positioned == null || render == null) return false;

        playerGridPos = new Vector2(positioned.GridPosNum.X, positioned.GridPosNum.Y);
        playerHeight = -render.RenderStruct.Height;
        heightData = _game.IngameState.Data.RawTerrainHeightData;
        return heightData != null;
    }

    private static float TryGetHeight(float[][] heightData, int x, int y)
    {
        if (heightData == null || y < 0 || y >= heightData.Length) return 0;
        var row = heightData[y];
        return row != null && x >= 0 && x < row.Length ? row[x] : 0;
    }
}
