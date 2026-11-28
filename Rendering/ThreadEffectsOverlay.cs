using System;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using Hexa.NET.ImGui;
using Color = SharpDX.Color;

namespace BeastsV3.Rendering;

// Lists what the Bestiary Threads did to the current map: the thread count and the modifiers
// the Threads rolled, read from the map's own stats, so the list is there from the moment the
// map loads. Only what a Thread can roll is listed; beast stats that come from the atlas tree
// (pair chance, Onslaught, copy chance and so on) are the same on every map and are left out.
// Each effect is drawn as good, bad or plain, or left out, as the user set it.
public sealed class ThreadEffectsOverlay
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private const string ThreadCountStat = "MapBestiaryScarabCount";

    // One known effect: its ExileCore stat name, the name shown in the settings list, how it is
    // shown until the user says otherwise, and the overlay line for a given value.
    private sealed record Effect(string Stat, string Label, string DefaultMode, Func<int, string> Describe);

    private const string Good = ThreadEffectsSettings.Good;
    private const string Bad = ThreadEffectsSettings.Bad;
    private const string Plain = ThreadEffectsSettings.Plain;

    // The eight modifiers the game lists under "Random Bestiary Thread Modifiers", in its own
    // words. The invasion, Sacred Fauna and additional modifier stats are matched by name: none
    // has been seen on a map yet.
    private static readonly Effect[] Effects =
    [
        new("MapAdditionalRedBeasts", "Additional Red Beasts", Good, v => $"{v} additional Red Beast{ImGuiEx.PluralSuffix(v)}"),
        new("MapEinharMissionBeastInvasionChancePct", "Invaded by a large number of Red Beasts", Good, _ => "Invaded by a large number of Red Beasts"),
        new("MapBestiaryAreaContainsBlackMorrigan", "Contains the Black Mórrigan", Good, _ => "Contains the Black Mórrigan"),
        new("MapXBestiaryPacksAreHarvestBeasts", "Additional Sacred Fauna Red Beasts", Good, _ => "Additional Sacred Fauna Red Beasts"),
        new("MapBestiaryBeastsHaveXWispsOfAllTypes", "Beasts empowered by Wildwood Wisps", Plain, _ => "Beasts are empowered by Wildwood Wisps of all types"),
        new("MapBestiaryEinharBloodExplodeAura", "Enemies near Einhar explode", Plain, _ => "Enemies near Einhar explode on death"),
        new("MapBestiaryYellowBeastsAreHulks", "Yellow Beasts are Nameless Beasts", Plain, _ => "Yellow Beasts are Nameless Beasts"),
        new("MapBestiaryBeastsXAdditionalModifiers", "Red Beasts have additional Bestiary Modifiers", Plain,
            v => v == 1 ? "Red Beasts have an additional Bestiary Modifier" : $"Red Beasts have {v} additional Bestiary Modifiers"),
    ];

    private readonly GameController _game;
    private readonly BeastsSettings _settings;
    private readonly OverlayLayout _layout;
    private readonly EmptyMap _emptyMap;

    private string _text = string.Empty;
    private Color[] _lineColors = [];
    private DateTime _builtUtc = DateTime.MinValue;

    public ThreadEffectsOverlay(GameController game, BeastsSettings settings, OverlayLayout layout, EmptyMap emptyMap)
    {
        _emptyMap = emptyMap;
        _game = game;
        _settings = settings;
        _layout = layout;
    }

    public void Render()
    {
        var window = _settings.ThreadEffects;
        if (!window.Show.Value || !Visible() || _emptyMap.HideOverlays) return;

        // The stats only change with the map, so the text is rebuilt once a second, not per frame.
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

        _layout.Draw(OverlayLayout.ThreadEffects, "##BeastsV3ThreadEffects", _text, style, _lineColors);
    }

    // The settings list: one row per known effect with how the overlay shows it.
    public void DrawEffectPicker()
    {
        var modes = _settings.ThreadEffects.EffectModes;
        foreach (var effect in Effects)
        {
            var current = ModeOf(effect);
            ImGui.SetNextItemWidth(90f);
            if (ImGui.BeginCombo($"##BeastsV3Effect{effect.Stat}", current))
            {
                foreach (var mode in ThreadEffectsSettings.Modes)
                {
                    var isSelected = mode == current;
                    if (ImGui.Selectable(mode, isSelected))
                    {
                        modes[effect.Stat] = mode;
                        _builtUtc = DateTime.MinValue;
                    }
                    if (isSelected) ImGui.SetItemDefaultFocus();
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiEx.ToVec4(ColorOf(current)));
            ImGui.TextUnformatted(effect.Label);
            ImGui.PopStyleColor();
        }
    }

    private string ModeOf(Effect effect) =>
        _settings.ThreadEffects.EffectModes.TryGetValue(effect.Stat, out var mode) &&
        Array.IndexOf(ThreadEffectsSettings.Modes, mode) >= 0
            ? mode
            : effect.DefaultMode;

    private Color ColorOf(string mode)
    {
        var window = _settings.ThreadEffects;
        return mode switch
        {
            Good => window.GoodColor.Value,
            Bad => window.BadColor.Value,
            _ => window.TextColor.Value,
        };
    }

    // Fills the text and the colour of each of its lines. The text is empty when the map has
    // no Bestiary Threads and none of their effects.
    private void Build()
    {
        _text = string.Empty;
        _lineColors = [];

        var stats = _game?.IngameState?.Data?.MapStats;
        if (stats == null || stats.Count == 0) return;

        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stat in stats) byName[stat.Key.ToString()] = stat.Value;

        var lines = new List<string>();
        var colors = new List<Color>();

        if (byName.TryGetValue(ThreadCountStat, out var threads) && threads > 0)
        {
            lines.Add($"Bestiary Threads: {threads}");
            colors.Add(_settings.ThreadEffects.TextColor.Value);
        }

        foreach (var effect in Effects)
        {
            if (!byName.TryGetValue(effect.Stat, out var value) || value == 0) continue;
            var mode = ModeOf(effect);
            if (mode == ThreadEffectsSettings.Hide) continue;
            lines.Add("- " + effect.Describe(value));
            colors.Add(ColorOf(mode));
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
