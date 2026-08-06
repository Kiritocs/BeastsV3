using System;
using System.Numerics;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Prices;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ImGuiNET;
using Color = SharpDX.Color;
using Vector3 = System.Numerics.Vector3;

namespace BeastsV3.Rendering;

// Draws in-world beast labels and ground circles, the Tracked Beasts window and the
// settings style preview.
public sealed class WorldLabels
{
    private const string PreviewBeastName = "Craicic Chimeral";

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly BeastTracker _tracker;
    private readonly PriceService _prices;
    private readonly Func<bool> _isInFinalizedMap;
    private readonly Vector2[] _circleBuffer = new Vector2[RenderPrimitives.UnitCirclePoints.Length];

    // isInFinalizedMap reads the banked-map flag from SessionState.
    public WorldLabels(GameController game, BeastsSettings settings, BeastTracker tracker, PriceService prices,
        Func<bool> isInFinalizedMap)
    {
        _game = game;
        _settings = settings;
        _tracker = tracker;
        _prices = prices;
        _isInFinalizedMap = isInFinalizedMap ?? (() => false);
    }

    public void RenderInWorld()
    {
        if (!_settings.MapRender.ShowBeastLabelsInWorld.Value) return;
        if (_settings.Visibility.HideInHideout.Value && GameHelpers.IsTownOrHideout(_game.Area?.CurrentArea)) return;
        // Nothing is tracked inside a banked map.
        if (_isInFinalizedMap()) return;

        var camera = _game?.IngameState?.Camera;
        var terrainData = _game?.IngameState?.Data;
        if (camera == null || terrainData == null) return;

        var drawList = ImGui.GetBackgroundDrawList();
        var mapRender = _settings.MapRender;
        var showEnabledOnly = mapRender.ShowEnabledOnly.Value;

        foreach (var (id, entity) in _tracker.LiveTracked)
        {
            if (entity?.IsValid != true) continue;

            // Name and capture state come from the tracker's per-frame pass.
            if (!_tracker.TryGetLiveInfo(id, out var info)) continue;
            var beastName = info.BeastName;

            // Includes beasts selected only for their talisman.
            if (showEnabledOnly && !_prices.IsShownWhileEnabledOnly(beastName)) continue;

            var positioned = entity.GetComponent<Positioned>();
            if (positioned == null) continue;

            var captureState = info.CaptureState;
            var talismanOnly = _prices.IsTalismanOnly(beastName);
            var worldPos = terrainData.ToWorldWithTerrainHeight(positioned.GridPosition);
            var screenPos = camera.WorldToScreen(worldPos);

            DrawWorldLabelText(drawList, screenPos, beastName, captureState, talismanOnly);
            RenderPrimitives.DrawFilledCircleInWorld(
                drawList, camera, worldPos,
                mapRender.Layout.WorldBeastCircleRadius.Value,
                GetCircleColor(captureState, talismanOnly),
                mapRender.Layout.WorldBeastCircleOutlineThickness.Value,
                mapRender.Layout.WorldBeastCircleFillOpacityPercent.Value,
                _circleBuffer);
        }
    }

    // Draws the Tracked Beasts window from the tracker's markers, including remembered ones.
    public void RenderTrackedBeastsWindow()
    {
        if (!_settings.MapRender.ShowTrackedBeastsWindow.Value) return;
        if (_settings.Visibility.HideInHideout.Value && GameHelpers.IsTownOrHideout(_game.Area?.CurrentArea)) return;
        if (_isInFinalizedMap()) return;
        if (_tracker.Markers.Count == 0) return;

        var showEnabledOnly = _settings.MapRender.ShowEnabledOnly.Value;
        var showCached = _settings.MapRender.ShowCachedTrackedBeasts.Value;
        var beastNameColor = ImGuiEx.ToVec4(_settings.MapRender.Colors.TrackedWindowText.Value);
        var talismanOnlyColor = ImGuiEx.ToVec4(_settings.MapRender.Colors.TrackedWindowTalismanOnlyText.Value);
        var cachedTag = _settings.MapRender.CachedTagText.Value?.Trim();
        var cachedTagColor = ImGuiEx.ToVec4(_settings.MapRender.Colors.TrackedWindowCachedTag.Value);

        // Per-state colours and texts, resolved once for the whole list.
        var capturingColor = ImGuiEx.ToVec4(GetStatusColor(BeastCaptureState.Capturing));
        var capturedColor = ImGuiEx.ToVec4(GetStatusColor(BeastCaptureState.Captured));
        var capturingText = " " + GetStatusText(BeastCaptureState.Capturing);
        var capturedText = " " + GetStatusText(BeastCaptureState.Captured);
        var cachedTagText = string.IsNullOrWhiteSpace(cachedTag) ? null : " " + cachedTag;

        ImGui.SetNextWindowBgAlpha(0.6f);

        // Skips the contents when the window is collapsed or clipped.
        if (!ImGui.Begin("##BeastsV3TrackedBeasts", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV;
        if (ImGui.BeginTable("##BeastsV3TrackedTable", 2, tableFlags))
        {
            ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 52);

            // Fixed width, measured from the widest row, so auto-resize converges in one
            // frame instead of oscillating.
            ImGui.TableSetupColumn("Beast", ImGuiTableColumnFlags.WidthFixed,
                MeasureWidestBeastColumn(showEnabledOnly, showCached, capturingText, capturedText, cachedTagText));

            foreach (var marker in _tracker.Markers)
            {
                if (!marker.IsLive && !showCached) continue;
                if (showEnabledOnly && !_prices.IsShownWhileEnabledOnly(marker.BeastName)) continue;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var priceText = _prices.GetDisplayPriceText(marker.BeastName);
                ImGui.Text(string.IsNullOrEmpty(priceText) ? "?" : priceText);

                ImGui.TableNextColumn();
                ImGui.TextColored(
                    _prices.IsTalismanOnly(marker.BeastName) ? talismanOnlyColor : beastNameColor,
                    marker.BeastName);

                if (marker.CaptureState != BeastCaptureState.None)
                {
                    ImGui.SameLine(0, 0);
                    var captured = marker.CaptureState == BeastCaptureState.Captured;
                    ImGui.TextColored(captured ? capturedColor : capturingColor,
                        captured ? capturedText : capturingText);
                }

                // Marks rows whose position and status are frozen from when the beast unloaded.
                if (!marker.IsLive && cachedTagText != null)
                {
                    ImGui.SameLine(0, 0);
                    ImGui.TextColored(cachedTagColor, cachedTagText);
                }
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    // Width of the widest row about to be drawn, used to fix the column width.
    private float MeasureWidestBeastColumn(
        bool showEnabledOnly, bool showCached, string capturingText, string capturedText, string cachedTagText)
    {
        var widest = 0f;

        foreach (var marker in _tracker.Markers)
        {
            if (!marker.IsLive && !showCached) continue;
            if (showEnabledOnly && !_prices.IsShownWhileEnabledOnly(marker.BeastName)) continue;

            var width = ImGui.CalcTextSize(marker.BeastName).X;

            if (marker.CaptureState == BeastCaptureState.Captured)
                width += ImGui.CalcTextSize(capturedText).X;
            else if (marker.CaptureState == BeastCaptureState.Capturing)
                width += ImGui.CalcTextSize(capturingText).X;

            if (!marker.IsLive && cachedTagText != null)
                width += ImGui.CalcTextSize(cachedTagText).X;

            if (width > widest) widest = width;
        }

        return widest;
    }

    public void RenderStylePreview()
    {
        if (!_settings.MapRender.ShowStylePreviewWindow.Value) return;

        ImGui.SetNextWindowBgAlpha(0.9f);
        if (!ImGui.Begin("Beast Style Preview##BeastsV3StylePreview",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            return;
        }

        ImGui.Text("World Label Preview");
        DrawPreviewWorldLabel(BeastCaptureState.None);
        DrawPreviewWorldLabel(BeastCaptureState.Capturing);
        DrawPreviewWorldLabel(BeastCaptureState.Captured);

        ImGui.Separator();
        ImGui.Text("Map Label Preview");
        DrawPreviewMapLabel(BeastCaptureState.None);
        DrawPreviewMapLabel(BeastCaptureState.Capturing);
        DrawPreviewMapLabel(BeastCaptureState.Captured);

        ImGui.End();
    }

    // Draws one beast marker on the large map.
    internal void DrawMapMarker(ImDrawListPtr drawList, string beastName, BeastCaptureState captureState, Vector2 pos)
    {
        BuildMapMarkerTexts(beastName, captureState, out var primary, out var secondary);
        var mapRender = _settings.MapRender;
        var colors = mapRender.Colors;
        var primaryColor = ShouldReplaceWithStatus(captureState)
            ? GetStatusColor(captureState)
            : _prices.IsTalismanOnly(beastName)
                ? colors.MapLabelTalismanOnlyText.Value
                : colors.MapLabelText.Value;
        RenderPrimitives.DrawCenteredLabel(
            drawList, primary, secondary, pos,
            colors.MapLabelBackground.Value, primaryColor, GetStatusColor(captureState),
            mapRender.Layout.MapLabelPaddingX.Value, mapRender.Layout.MapLabelPaddingY.Value,
            mapRender.Layout.WorldTextLineSpacing.Value);
    }

    // ---- private ---------------------------------------------------------

    // Draws a beast's name, price and status lines at a screen position.
    private void DrawWorldLabelText(ImDrawListPtr drawList, Vector2 screenPos, string beastName,
        BeastCaptureState captureState, bool talismanOnly = false)
    {
        var mapRender = _settings.MapRender;
        var colors = mapRender.Colors;
        var lineSpacing = mapRender.Layout.WorldTextLineSpacing.Value;
        var beastColor = talismanOnly
            ? colors.WorldTalismanOnlyText.Value
            : captureState == BeastCaptureState.None
                ? colors.WorldBeastText.Value : colors.WorldCapturedBeastText.Value;
        var statusText = GetStatusText(captureState);
        var statusColor = GetStatusColor(captureState);
        var outline = colors.WorldTextOutline.Value;

        if (ShouldReplaceWithStatus(captureState))
        {
            RenderPrimitives.DrawOutlinedText(drawList, screenPos, statusText, statusColor, outline);
            return;
        }

        RenderPrimitives.DrawOutlinedText(drawList, screenPos, beastName, beastColor, outline);

        var nextY = lineSpacing;
        var priceText = _prices.GetDisplayPriceText(beastName);
        if (!string.IsNullOrEmpty(priceText))
        {
            RenderPrimitives.DrawOutlinedText(drawList, screenPos + new Vector2(0, nextY), priceText, colors.WorldPriceText.Value, outline);
            nextY += lineSpacing;
        }
        if (captureState != BeastCaptureState.None)
        {
            RenderPrimitives.DrawOutlinedText(drawList, screenPos + new Vector2(0, nextY), statusText, statusColor, outline);
        }
    }

    private void BuildMapMarkerTexts(string beastName, BeastCaptureState captureState, out string primary, out string secondary)
    {
        var priceText = _prices.GetDisplayPriceText(beastName);
        var label = _settings.MapRender.ShowNameInsteadOfPrice.Value || string.IsNullOrEmpty(priceText)
            ? beastName
            : $"{beastName} {priceText}";

        if (captureState == BeastCaptureState.None)
        {
            primary = label;
            secondary = null;
            return;
        }

        if (ShouldReplaceWithStatus(captureState))
        {
            primary = GetStatusText(captureState);
            secondary = null;
            return;
        }

        primary = label;
        secondary = GetStatusText(captureState);
    }

    private bool ShouldReplaceWithStatus(BeastCaptureState captureState) =>
        captureState != BeastCaptureState.None &&
        _settings.MapRender.CapturedText.ReplaceNameAndPriceWithStatusText.Value;

    private string GetStatusText(BeastCaptureState captureState)
    {
        var text = _settings.MapRender.CapturedText;
        var (setting, fallback) = captureState == BeastCaptureState.Captured
            ? (text.CapturedText.Value, "Captured")
            : (text.CapturingText.Value, "Capturing");
        return string.IsNullOrWhiteSpace(setting) ? fallback : setting;
    }

    private Color GetStatusColor(BeastCaptureState captureState) =>
        captureState == BeastCaptureState.Captured
            ? _settings.MapRender.CapturedText.CapturedColor.Value
            : _settings.MapRender.CapturedText.CapturingColor.Value;

    // Circle colour by capture state, falling back to the talisman colour.
    private Color GetCircleColor(BeastCaptureState captureState, bool talismanOnly = false) => captureState switch
    {
        BeastCaptureState.Captured => _settings.MapRender.Colors.WorldCapturedCircle.Value,
        BeastCaptureState.Capturing => _settings.MapRender.Colors.WorldCaptureRing.Value,
        _ when talismanOnly => _settings.MapRender.Colors.WorldTalismanOnlyCircle.Value,
        _ => _settings.MapRender.Colors.WorldBeastCircle.Value,
    };

    private void DrawPreviewWorldLabel(BeastCaptureState captureState)
    {
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(280, 88);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##BeastsV3WorldPreview{captureState}", size);

        var centerX = origin.X + size.X / 2f;
        var lineSpacing = _settings.MapRender.Layout.WorldTextLineSpacing.Value;
        var beastColor = captureState == BeastCaptureState.None
            ? _settings.MapRender.Colors.WorldBeastText.Value
            : _settings.MapRender.Colors.WorldCapturedBeastText.Value;
        var statusText = GetStatusText(captureState);
        var statusColor = GetStatusColor(captureState);
        var outline = _settings.MapRender.Colors.WorldTextOutline.Value;

        if (ShouldReplaceWithStatus(captureState))
        {
            RenderPrimitives.DrawCenteredOutlinedText(drawList, new Vector2(centerX, origin.Y + 14), statusText, statusColor, outline);
            return;
        }

        RenderPrimitives.DrawCenteredOutlinedText(drawList, new Vector2(centerX, origin.Y + 8), PreviewBeastName, beastColor, outline);
        RenderPrimitives.DrawCenteredOutlinedText(drawList, new Vector2(centerX, origin.Y + 8 + lineSpacing), "1c", _settings.MapRender.Colors.WorldPriceText.Value, outline);
        if (captureState != BeastCaptureState.None)
        {
            RenderPrimitives.DrawCenteredOutlinedText(drawList, new Vector2(centerX, origin.Y + 8 + lineSpacing * 2), statusText, statusColor, outline);
        }
    }

    private void DrawPreviewMapLabel(BeastCaptureState captureState)
    {
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(280, 72);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##BeastsV3MapPreview{captureState}", size);

        var label = _settings.MapRender.ShowNameInsteadOfPrice.Value ? PreviewBeastName : $"{PreviewBeastName} 1c";
        string primary, secondary;
        if (captureState == BeastCaptureState.None) { primary = label; secondary = null; }
        else if (ShouldReplaceWithStatus(captureState)) { primary = GetStatusText(captureState); secondary = null; }
        else { primary = label; secondary = GetStatusText(captureState); }

        var mapRender = _settings.MapRender;
        var colors = mapRender.Colors;
        var primaryColor = ShouldReplaceWithStatus(captureState) ? GetStatusColor(captureState) : colors.MapLabelText.Value;
        RenderPrimitives.DrawCenteredLabel(
            drawList, primary, secondary, origin + size / 2f,
            colors.MapLabelBackground.Value, primaryColor, GetStatusColor(captureState),
            mapRender.Layout.MapLabelPaddingX.Value, mapRender.Layout.MapLabelPaddingY.Value,
            mapRender.Layout.WorldTextLineSpacing.Value);
    }
}
