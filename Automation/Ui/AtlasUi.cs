using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BeastsV3.Automation.Input;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;
using SharpVec2 = SharpDX.Vector2;

namespace BeastsV3.Automation.Ui;

// Normalises the Atlas view's scale and Y position by scrolling and dragging, and
// resolves a configured map name to the UI child that selects it.
public sealed class AtlasUi
{
    public const string OpenMapSelectionValue = "open Map";
    public const float TargetScale = 0.45f;
    public const float TargetScaleTolerance = 0.015f;
    public const float CenteredMinY = -1200f;
    public const float CenteredMaxY = -1100f;

    private const int MaxScrollAttempts = 18;
    private const int MaxCenterAttempts = 14;
    private const int AtlasNodeUiOffset = 2;
    private const int AtlasNodeScanLimit = 110;

    // Normalised screen areas covered by the HUD; drag anchors avoid these.
    private static readonly (float MinX, float MinY, float MaxX, float MaxY)[] HudBlockedZones =
    {
        (0.00f, 0.00f, 0.21f, 0.080f),   // top-left
        (0.33f, 0.00f, 0.67f, 0.115f),   // top-center
        (0.00f, 0.80f, 0.115f, 1.00f),   // bottom-left globe
        (0.885f, 0.80f, 1.00f, 1.00f),   // bottom-right globe
        (0.00f, 0.87f, 0.29f, 1.00f),    // bottom-left bar
        (0.71f, 0.87f, 1.00f, 1.00f),    // bottom-right bar
    };

    private const uint MouseEventWheel = 0x0800;
    private const int MouseWheelDelta = 120;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);

    private readonly GameController _game;
    private readonly AutomationInput _input;
    private readonly Waits _waits;
    private readonly BeastsSettings _settings;

    public AtlasUi(GameController game, AutomationInput input, Waits waits, BeastsSettings settings)
    {
        _game = game;
        _input = input;
        _waits = waits;
        _settings = settings;
    }

    public Element Atlas => _game?.IngameState?.IngameUi?.Atlas;

    public bool IsVisible => Atlas?.IsVisible == true;

    // The InnerAtlas element, read via reflection for Scale and Y.
    public object InnerAtlas => Atlas?.GetChildAtIndex(0);

    // ---- scale + Y normalization --------------------------------------

    public float? CurrentScale() => ReadInnerAtlasScale(InnerAtlas);
    public float? CurrentY() => ReadInnerAtlasY(InnerAtlas);

    public bool IsScaleAtTarget()
    {
        var s = CurrentScale();
        return s.HasValue && Math.Abs(s.Value - TargetScale) <= TargetScaleTolerance;
    }

    public bool IsYCentered()
    {
        var y = CurrentY();
        return y.HasValue && IsInRange(NormalizeYForCentering(y.Value));
    }

    public async Task NormalizeScaleAsync()
    {
        var inner = InnerAtlas;
        if (inner == null) return;

        if (IsScaleAtTarget()) return;

        for (var attempt = 1; attempt <= MaxScrollAttempts; attempt++)
        {
            var anchor = await FindTooltipFreeProbeAsync(inner, passIndex: 0);
            if (!anchor.HasValue) return;

            _input.MoveCursorTo(anchor.Value);
            await _input.DelayAsync(Math.Max(35, _settings.Timing.Polling.FastPollDelayMs.Value));

            ScrollWheelDown();
            await _input.DelayForUiCheckAsync(Math.Max(90, _settings.Timing.Polling.FastPollDelayMs.Value + 20));

            if (IsScaleAtTarget())
            {
                Log.Debug($"Atlas scale normalized after {attempt} scroll(s).");
                return;
            }
        }

        Log.Debug($"Atlas scale did not reach target after {MaxScrollAttempts} attempts. current={CurrentScale():0.###}");
    }

    public async Task CenterYAsync()
    {
        var inner = InnerAtlas;
        if (inner == null) return;

        if (IsYCentered()) return;

        int? lastDirection = null;
        var flipCount = 0;

        for (var attempt = 1; attempt <= MaxCenterAttempts; attempt++)
        {
            var y = CurrentY();
            if (!y.HasValue) return;

            var normalized = NormalizeYForCentering(y.Value);
            if (IsInRange(normalized))
            {
                Log.Debug($"Atlas Y centered. y={y.Value:0.###} normalized={normalized:0.##}");
                return;
            }

            var distance = normalized > CenteredMaxY
                ? normalized - CenteredMaxY
                : CenteredMinY - normalized;
            var direction = normalized > CenteredMaxY ? -1 : 1;

            var dragPixels = DragPixelsForDistance(Math.Abs(distance));
            if (Math.Abs(distance) < 120f) dragPixels = Math.Min(dragPixels, 52f);
            if (Math.Abs(distance) < 55f) dragPixels = Math.Min(dragPixels, 38f);

            if (lastDirection.HasValue && lastDirection.Value != direction)
            {
                flipCount++;
                var dampening = flipCount >= 2 ? 0.45f : 0.6f;
                dragPixels = Math.Max(26f, dragPixels * dampening);
            }

            var vertical = direction < 0 ? -dragPixels : dragPixels;
            if (!await PanAsync(inner, vertical, attempt)) return;

            lastDirection = direction;
        }

        var finalY = CurrentY();
        if (finalY.HasValue)
            Log.Debug($"Atlas Y did not center after {MaxCenterAttempts} attempts. y={finalY.Value:0.###} normalized={NormalizeYForCentering(finalY.Value):0.##}");
    }

    // ---- map selection ------------------------------------------------

    // Returns the InnerAtlas child index for a map name (AtlasNodes index + 2).
    public int? TryResolveMapUiIndex(string mapName)
    {
        var lookup = NormalizeMapName(mapName);
        if (string.IsNullOrWhiteSpace(lookup)) return null;

        var nodes = _game?.Files?.AtlasNodes?.EntriesList;
        if (nodes == null || nodes.Count == 0) return null;

        var limit = Math.Min(nodes.Count, AtlasNodeScanLimit);
        for (var i = 0; i < limit; i++)
        {
            var nodeName = NormalizeMapName(nodes[i].Area?.Name);
            if (!string.IsNullOrWhiteSpace(nodeName) &&
                string.Equals(nodeName, lookup, StringComparison.OrdinalIgnoreCase))
            {
                return i + AtlasNodeUiOffset;
            }
        }
        return null;
    }

    // Every map name TryResolveMapUiIndex can resolve, for the settings picker.
    public List<string> AvailableMapNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var nodes = _game?.Files?.AtlasNodes?.EntriesList;
        if (nodes == null) return names;

        var limit = Math.Min(nodes.Count, AtlasNodeScanLimit);
        for (var i = 0; i < limit; i++)
        {
            var name = NormalizeMapName(nodes[i].Area?.Name);
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) names.Add(name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public Element TryGetMapElement(int uiIndex)
    {
        var inner = InnerAtlas as Element;
        return inner?.GetChildAtIndex(uiIndex);
    }

    public static string NormalizeMapName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Trim().TrimStart('★', ' ', ' ').Trim();
    }

    public static string NormalizeMapSelectionValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return OpenMapSelectionValue;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, OpenMapSelectionValue, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "open Map (Default)", StringComparison.OrdinalIgnoreCase))
            return OpenMapSelectionValue;
        var normalized = NormalizeMapName(trimmed);
        return string.IsNullOrWhiteSpace(normalized) ? OpenMapSelectionValue : normalized;
    }

    // ---- private: pan + probe -----------------------------------------

    // Pixels to drag for a given Y distance from the target.
    private static float DragPixelsForDistance(float distance)
    {
        if (distance > 1600f) return 170f;
        if (distance > 900f) return 135f;
        if (distance > 450f) return 105f;
        if (distance > 220f) return 80f;
        return 60f;
    }

    private static bool IsInRange(float y) => y >= CenteredMinY && y <= CenteredMaxY;

    // Converts a normalised Y value back to grid space.
    private static float NormalizeYForCentering(float y) =>
        Math.Abs(y) < 1f ? y * 10000f : y;

    private async Task<bool> PanAsync(object inner, float verticalPixels, int attempt)
    {
        if (Math.Abs(verticalPixels) < 20f) return false;

        var passHint = verticalPixels < 0 ? 0 : 1;
        var start = await FindTooltipFreeProbeAsync(inner, passHint);
        if (!start.HasValue) return false;

        var clampedStart = ClampToWindow(start.Value);
        var clampedEnd = ClampToWindow(new SharpVec2(clampedStart.X, clampedStart.Y + verticalPixels));
        if (SharpVec2.DistanceSquared(clampedStart, clampedEnd) < 225f) return false;

        var timing = _settings.Timing;
        var preDelay = Math.Max(25, timing.Polling.FastPollDelayMs.Value);
        var holdSettle = Math.Max(20, timing.Clicks.KeyTapDelayMs.Value / 2);
        var endSettle = Math.Max(120, timing.Polling.UiCheckInitialSettleDelayMs.Value);
        var releaseSettle = Math.Max(90, timing.Polling.FastPollDelayMs.Value);

        _input.MoveCursorTo(clampedStart);
        await _input.DelayAsync(preDelay);
        _input.LeftMouseDown();
        await _input.DelayAsync(holdSettle);
        _input.MoveCursorTo(clampedEnd);
        await _input.DelayAsync(endSettle);
        _input.LeftMouseUp();
        await _input.DelayAsync(releaseSettle);

        Log.Debug($"Atlas pan attempt {attempt}: ({clampedStart.X:0},{clampedStart.Y:0}) -> ({clampedEnd.X:0},{clampedEnd.Y:0})");
        return true;
    }

    // Finds a nearby drag anchor that is not covered by a hover tooltip.
    private async Task<SharpVec2?> FindTooltipFreeProbeAsync(object inner, int passIndex)
    {
        var rect = _game?.Window?.GetWindowRectangle();
        if (rect is not { Width: > 0, Height: > 0 }) return null;

        var wr = rect.Value;
        var screenCenter = new SharpVec2(wr.Left + wr.Width * 0.5f, wr.Top + wr.Height * 0.5f);

        var centered = await ProbeAroundAsync(screenCenter, wr);
        if (centered.HasValue) return centered;

        var fallback = ResolveFallbackAnchor(inner, passIndex, wr);
        return fallback.HasValue ? await ProbeAroundAsync(fallback.Value, wr) : null;
    }

    private async Task<SharpVec2?> ProbeAroundAsync(SharpVec2 origin, SharpDX.RectangleF windowRect)
    {
        var normalized = ClampToWindow(AdjustToSafeZone(origin, windowRect));
        var step = Math.Clamp(Math.Min(windowRect.Width, windowRect.Height) * 0.035f, 26f, 64f);
        var offsets = new[]
        {
            new SharpVec2(0f, 0f),
            new SharpVec2(step, 0f), new SharpVec2(-step, 0f),
            new SharpVec2(0f, step), new SharpVec2(0f, -step),
            new SharpVec2(step, step), new SharpVec2(-step, step),
            new SharpVec2(step, -step), new SharpVec2(-step, -step),
            new SharpVec2(step * 2f, 0f), new SharpVec2(-step * 2f, 0f),
            new SharpVec2(0f, step * 2f), new SharpVec2(0f, -step * 2f),
        };

        foreach (var offset in offsets)
        {
            var probe = ClampToWindow(AdjustToSafeZone(normalized + offset, windowRect));
            if (!IsHoverPositionUsable(probe, windowRect)) continue;

            _input.MoveCursorTo(probe);
            await _input.DelayAsync(Math.Max(40, _settings.Timing.Polling.FastPollDelayMs.Value));

            if (!IsTooltipShowing()) return probe;
        }
        return null;
    }

    private SharpVec2? ResolveFallbackAnchor(object inner, int passIndex, SharpDX.RectangleF windowRect)
    {
        if (inner is Element innerElement)
        {
            var r = innerElement.GetClientRect();
            if (r.Width > 0 && r.Height > 0)
            {
                var anchors = new[]
                {
                    new SharpVec2(r.Left + r.Width * 0.36f, r.Top + r.Height * 0.40f),
                    new SharpVec2(r.Left + r.Width * 0.64f, r.Top + r.Height * 0.40f),
                    new SharpVec2(r.Left + r.Width * 0.36f, r.Top + r.Height * 0.62f),
                    new SharpVec2(r.Left + r.Width * 0.64f, r.Top + r.Height * 0.62f),
                    new SharpVec2(r.Left + r.Width * 0.50f, r.Top + r.Height * 0.48f),
                };
                return AdjustToSafeZone(anchors[Math.Abs(passIndex) % anchors.Length], windowRect);
            }
        }
        return new SharpVec2(windowRect.Left + windowRect.Width * 0.5f, windowRect.Top + windowRect.Height * 0.5f);
    }

    private static SharpVec2 AdjustToSafeZone(SharpVec2 anchor, SharpDX.RectangleF rect)
    {
        var nx = Math.Clamp((anchor.X - rect.Left) / rect.Width, 0.16f, 0.84f);
        var ny = Math.Clamp((anchor.Y - rect.Top) / rect.Height, 0.18f, 0.76f);
        return new SharpVec2(rect.Left + nx * rect.Width, rect.Top + ny * rect.Height);
    }

    private static bool IsHoverPositionUsable(SharpVec2 pos, SharpDX.RectangleF windowRect)
    {
        if (pos.X < windowRect.Left || pos.X > windowRect.Right ||
            pos.Y < windowRect.Top || pos.Y > windowRect.Bottom) return false;

        var nx = (pos.X - windowRect.Left) / windowRect.Width;
        var ny = (pos.Y - windowRect.Top) / windowRect.Height;
        foreach (var (minX, minY, maxX, maxY) in HudBlockedZones)
        {
            if (nx >= minX && nx <= maxX && ny >= minY && ny <= maxY) return false;
        }
        return true;
    }

    private bool IsTooltipShowing()
    {
        var ingameState = _game?.IngameState;
        var uiHover = ingameState?.GetType().GetProperty("UIHover")?.GetValue(ingameState);
        return uiHover?.GetType().GetProperty("Tooltip")?.GetValue(uiHover) != null;
    }

    private SharpVec2 ClampToWindow(SharpVec2 position)
    {
        var rect = _game?.Window?.GetWindowRectangle();
        if (rect is not { Width: > 0, Height: > 0 }) return position;
        var r = rect.Value;
        return new SharpVec2(
            Math.Clamp(position.X, r.Left, r.Right - 1),
            Math.Clamp(position.Y, r.Top, r.Bottom - 1));
    }

    private static void ScrollWheelDown(int clicks = 1) =>
        mouse_event(MouseEventWheel, 0, 0, -Math.Max(1, clicks) * MouseWheelDelta, UIntPtr.Zero);

    // ---- private: reflection over InnerAtlas --------------------------

    // Reads InnerAtlas.Y via reflection.
    private static float? ReadInnerAtlasY(object innerAtlas)
    {
        if (innerAtlas == null) return null;
        return ReadNested(innerAtlas, "Position", "Y")
            ?? ReadNested(innerAtlas, "PositionNum", "Y")
            ?? ReadNumeric(innerAtlas, "Y")
            ?? ReadNumeric(innerAtlas, "PosY")
            ?? ReadNumeric(innerAtlas, "OffsetY")
            ?? ReadNumeric(innerAtlas, "TranslateY")
            ?? ReadNumeric(innerAtlas, "TranslationY");
    }

    private static float? ReadInnerAtlasScale(object innerAtlas)
    {
        if (innerAtlas == null) return null;
        var direct = ReadNumeric(innerAtlas, "Scale");
        if (direct.HasValue) return direct.Value;

        try
        {
            var type = innerAtlas.GetType();
            var scaleObject = type.GetProperty("Scale")?.GetValue(innerAtlas)
                              ?? type.GetField("Scale")?.GetValue(innerAtlas);
            if (scaleObject == null) return null;
            return ReadNumeric(scaleObject, "Value")
                ?? ReadNumeric(scaleObject, "Current")
                ?? ReadNumeric(scaleObject, "X");
        }
        catch { return null; }
    }

    private static float? ReadNumeric(object instance, string memberName)
    {
        if (instance == null || string.IsNullOrWhiteSpace(memberName)) return null;
        try
        {
            var type = instance.GetType();
            var val = type.GetProperty(memberName)?.GetValue(instance)
                   ?? type.GetField(memberName)?.GetValue(instance);
            return val == null ? null : Convert.ToSingle(val);
        }
        catch { return null; }
    }

    private static float? ReadNested(object instance, string parent, string child)
    {
        if (instance == null) return null;
        try
        {
            var type = instance.GetType();
            var parentObj = type.GetProperty(parent)?.GetValue(instance) ?? type.GetField(parent)?.GetValue(instance);
            return parentObj == null ? null : ReadNumeric(parentObj, child);
        }
        catch { return null; }
    }
}
