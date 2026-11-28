using System;
using System.Collections.Generic;
using BeastsV3.Plugin.Settings;
using ExileCore;
using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;

namespace BeastsV3.Rendering;

// Places the text overlays that can be attached to one another. An overlay sits at its own
// X/Y position, or at an edge of the overlay it is attached to, so it moves as that one grows
// and shrinks. While the overlay it is attached to is not shown, it takes that one's place.
public sealed class OverlayLayout
{
    public const string AnalyticsOverlay = "Analytics Overlay";
    public const string ThreadEffects = "Thread Effects";
    public const string PossibleBeasts = "Possible Beasts";

    private static readonly string[] Names = [AnalyticsOverlay, ThreadEffects, PossibleBeasts];

    private readonly record struct Box(Vector2 Position, Vector2 Size, long Frame);

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly Dictionary<string, Box> _drawn = new(StringComparer.Ordinal);
    private long _frame;

    public OverlayLayout(GameController game, BeastsSettings settings)
    {
        _game = game;
        _settings = settings;

        // List choices are not saved, so they are supplied on every start. An overlay cannot
        // be attached to itself.
        foreach (var name in Names)
        {
            var choices = new List<string> { AnalyticsOverlaySettings.AttachNothing };
            foreach (var other in Names)
                if (other != name) choices.Add(other);

            var window = SettingsOf(name);
            window.AttachTo.SetListValues(choices);
            window.AttachSide.SetListValues([.. AnalyticsOverlaySettings.Sides]);
            if (!choices.Contains(window.AttachTo.Value)) window.AttachTo.Value = AnalyticsOverlaySettings.AttachNothing;
            if (Array.IndexOf(AnalyticsOverlaySettings.Sides, window.AttachSide.Value) < 0)
                window.AttachSide.Value = AnalyticsOverlaySettings.Below;
        }
    }

    // Once per rendered frame, before the overlays draw.
    public void BeginFrame() => _frame++;

    // Draws the overlay and returns where it was put and how large it is.
    public (Vector2 Position, Vector2 Size) Draw(
        string name, string windowId, string text, OverlayWindow.Style style, Color[] lineColors = null)
    {
        var (size, wrapWidth) = OverlayWindow.Measure(_game, text, style);
        var position = Place(name, size);
        OverlayWindow.DrawAt(windowId, text, position, size, wrapWidth, style, lineColors);
        _drawn[name] = new Box(position, size, _frame);
        return (position, size);
    }

    // Makes the overlay count as larger than its text, for something drawn along with it.
    public void Extend(string name, Vector2 size)
    {
        if (_drawn.TryGetValue(name, out var box)) _drawn[name] = box with { Size = size };
    }

    private Vector2 Place(string name, Vector2 size)
    {
        var window = SettingsOf(name);
        var parent = ParentOf(name);
        if (parent == null)
        {
            var rect = _game.Window.GetWindowRectangle();
            return new Vector2(rect.Width * (window.XPos.Value / 100f), rect.Height * (window.YPos.Value / 100f));
        }

        // Shown this frame, or last frame when it draws after this one.
        if (!_drawn.TryGetValue(parent, out var box) || box.Frame < _frame - 1) return Place(parent, size);

        float gap = window.AttachGap.Value;
        return window.AttachSide.Value switch
        {
            AnalyticsOverlaySettings.Above => new Vector2(box.Position.X, box.Position.Y - gap - size.Y),
            AnalyticsOverlaySettings.Right => new Vector2(box.Position.X + box.Size.X + gap, box.Position.Y),
            AnalyticsOverlaySettings.Left => new Vector2(box.Position.X - gap - size.X, box.Position.Y),
            _ => new Vector2(box.Position.X, box.Position.Y + box.Size.Y + gap),
        };
    }

    // The overlay this one is attached to, or null. Overlays attached in a circle count as
    // not attached: each would follow the other across the screen.
    private string ParentOf(string name)
    {
        var parent = AttachedTo(name);
        var next = parent;
        for (var i = 0; next != null && i < Names.Length; i++)
        {
            if (next == name) return null;
            next = AttachedTo(next);
        }
        return parent;
    }

    private string AttachedTo(string name)
    {
        var target = SettingsOf(name).AttachTo.Value;
        return target != name && Array.IndexOf(Names, target) >= 0 ? target : null;
    }

    private AnalyticsOverlaySettings SettingsOf(string name) => name switch
    {
        ThreadEffects => _settings.ThreadEffects,
        PossibleBeasts => _settings.PossibleBeasts,
        _ => _settings.Analytics.Overlay,
    };
}
