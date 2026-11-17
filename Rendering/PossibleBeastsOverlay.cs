using System;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Analytics;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Prices;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.Shared.Enums;
using Color = SharpDX.Color;

namespace BeastsV3.Rendering;

// Lists the tracked beasts the current map can contain, dearest first, from the moment the
// map has loaded. A listed beast is possible, not certain (see MapBeastPreloads), so each line
// carries the beast's own record: in how many of the maps that listed it it really was there.
// Once a beast is seen its line changes colour. When none of the tracked beasts can be in the
// map, it says so.
public sealed class PossibleBeastsOverlay
{
    private const string BlackMorrigan = "Black Mórrigan";

    // The game does not preload the Black Mórrigan; the map says outright when it holds one.
    private static readonly GameStat? BlackMorriganStat =
        Enum.TryParse<GameStat>("MapBestiaryAreaContainsBlackMorrigan", out var s) ? s : null;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly MapBeastPreloads _preloads;
    private readonly PreloadRecord _record;
    private readonly PriceService _prices;
    private readonly SessionState _state;

    private string _text = string.Empty;
    private Color[] _lineColors = [];
    private DateTime _builtUtc = DateTime.MinValue;

    public PossibleBeastsOverlay(
        GameController game, BeastsSettings settings, MapBeastPreloads preloads, PreloadRecord record,
        PriceService prices, SessionState state)
    {
        _game = game;
        _settings = settings;
        _preloads = preloads;
        _record = record;
        _prices = prices;
        _state = state;
    }

    public void Render()
    {
        var window = _settings.PossibleBeasts;
        if (!window.Show.Value || !Visible()) return;

        var now = DateTime.UtcNow;
        if (now - _builtUtc >= RefreshInterval)
        {
            _builtUtc = now;
            Build();
        }
        if (_text.Length == 0) return;

        var style = new OverlayWindow.Style(
            Text: window.TextColor.Value,
            Border: window.BorderColor.Value,
            Background: window.BackgroundColor.Value,
            Padding: window.Padding.Value,
            BorderThickness: window.BorderThickness.Value,
            BorderRounding: window.BorderRounding.Value,
            TextScale: window.TextScale.Value);

        OverlayWindow.Draw(_game, "##BeastsV3PossibleBeasts", _text,
            window.XPos.Value, window.YPos.Value, style, centerHorizontally: false, lineColors: _lineColors);
    }

    private void Build()
    {
        _text = string.Empty;
        _lineColors = [];

        // Nothing is shown until the map's files have been read: an empty list then would
        // read as "nothing here".
        var possible = _preloads.Possible;
        if (possible == null) return;

        var window = _settings.PossibleBeasts;
        var hasMorrigan = BlackMorriganStat is { } stat &&
                          _game?.IngameState?.Data?.MapStats is { } stats &&
                          stats.TryGetValue(stat, out var value) && value > 0;

        var names = possible
            .Where(n => !string.Equals(n, BlackMorrigan, StringComparison.OrdinalIgnoreCase))
            .Concat(hasMorrigan ? [BlackMorrigan] : Array.Empty<string>())
            .Where(_prices.IsTracked)
            .OrderByDescending(n => _prices.BeastPrices.TryGetValue(n, out var price) ? price : -1f)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            _text = "None of your tracked beasts can be in this map";
            _lineColors = [window.NoneColor.Value];
            return;
        }

        var lines = new List<string> { "Tracked beasts that can be in this map" };
        var colors = new List<Color> { window.TextColor.Value };

        foreach (var name in names)
        {
            var price = _prices.GetDisplayPriceText(name);
            var line = string.IsNullOrEmpty(price) ? name : $"{name}  {price}";

            if (_state.CurrentMapValuableBeastCounts.TryGetValue(name, out var seen) && seen > 0)
            {
                lines.Add($"{line}  - found{(seen > 1 ? $" x{seen}" : string.Empty)}");
                colors.Add(window.FoundColor.Value);
            }
            else if (string.Equals(name, BlackMorrigan, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{line}  - in this map");
                colors.Add(window.FoundColor.Value);
            }
            else
            {
                var (listed, present) = _record.Get(name);
                lines.Add(listed > 0 ? $"{line}  (there in {present} of {listed})" : line);
                colors.Add(window.PossibleColor.Value);
            }
        }

        _text = string.Join('\n', lines);
        _lineColors = colors.ToArray();
    }

    private bool Visible()
    {
        var ui = _game?.IngameState?.IngameUi;
        if (ui == null) return false;
        var visibility = _settings.Visibility;

        if (GameHelpers.IsTownOrHideout(_game.Area?.CurrentArea)) return false;

        if (visibility.HideOnFullscreenPanels.Value)
        {
            foreach (var panel in ui.FullscreenPanels)
                if (panel.IsVisible) return false;
        }
        if (visibility.HideOnLeftPanelOpen.Value && ui.OpenLeftPanel?.IsVisible == true) return false;
        if (visibility.HideOnRightPanelOpen.Value && ui.OpenRightPanel?.IsVisible == true) return false;
        return true;
    }
}
