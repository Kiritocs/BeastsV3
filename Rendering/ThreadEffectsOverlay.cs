using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BeastsV3.Plugin.Settings;
using BeastsV3.Shared;
using ExileCore;
using Hexa.NET.ImGui;
using Color = SharpDX.Color;

namespace BeastsV3.Rendering;

// Lists what the Bestiary Threads did to the current map: the thread count and the beast
// effects the map carries, each with a short description. The effects are read from the map's
// own stats, so the list is there from the moment the map loads. Threads of other mechanics
// set no beast stats and never show up here. Each effect is drawn as good, bad or plain, or
// left out, as the user set it.
public sealed class ThreadEffectsOverlay
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private const string ThreadCountStat = "MapBestiaryScarabCount";

    // Beast stats that say nothing a player needs: the thread count has its own line, the rest
    // are switches the game uses internally.
    private static readonly HashSet<string> Internal = new(StringComparer.Ordinal)
    {
        ThreadCountStat, "MapBestiaryLeague", "MapSpawnBestiaryEncounters", "MapForceBestiaryEncounter",
        "MapUsesBeastCatcherRules",
    };

    // One known effect: its ExileCore stat name, the name shown in the settings list, how it is
    // shown until the user says otherwise, and the overlay line for a given value.
    private sealed record Effect(string Stat, string Label, string DefaultMode, Func<int, string> Describe);

    private const string Good = ThreadEffectsSettings.Good;
    private const string Bad = ThreadEffectsSettings.Bad;
    private const string Plain = ThreadEffectsSettings.Plain;

    // The wording is the plugin's, taken from each stat's name and value, not the game's own
    // text. A beast stat missing here is still listed, by its raw name, so a new effect cannot
    // go unnoticed.
    private static readonly Effect[] Effects =
    [
        new("MapAdditionalRedBeasts", "Additional Red Beasts", Good, v => $"{v} additional Red Beast{ImGuiEx.PluralSuffix(v)}"),
        new("MapAdditionalRedBeastChancePct", "Chance for one more Red Beast", Good, v => $"{v}% chance for one more Red Beast"),
        new("MapAdditionalRedBeastFamilyCaverns", "Additional Red Beasts from The Caverns", Good, v => $"{v} additional Red Beast{ImGuiEx.PluralSuffix(v)} from The Caverns"),
        new("MapAdditionalRedBeastFamilyDeep", "Additional Red Beasts from The Deep", Good, v => $"{v} additional Red Beast{ImGuiEx.PluralSuffix(v)} from The Deep"),
        new("MapAdditionalRedBeastFamilySands", "Additional Red Beasts from The Sands", Good, v => $"{v} additional Red Beast{ImGuiEx.PluralSuffix(v)} from The Sands"),
        new("MapAdditionalRedBeastFamilyWilds", "Additional Red Beasts from The Wilds", Good, v => $"{v} additional Red Beast{ImGuiEx.PluralSuffix(v)} from The Wilds"),
        new("MapRedBeastFromCavernsChancePct", "Red Beasts more likely from The Caverns", Plain, v => $"Red Beasts are {v}% more likely to be from The Caverns"),
        new("MapRedBeastFromDeepChancePct", "Red Beasts more likely from The Deep", Plain, v => $"Red Beasts are {v}% more likely to be from The Deep"),
        new("MapRedBeastFromSandsChancePct", "Red Beasts more likely from The Sands", Plain, v => $"Red Beasts are {v}% more likely to be from The Sands"),
        new("MapRedBeastFromWildsChancePct", "Red Beasts more likely from The Wilds", Plain, v => $"Red Beasts are {v}% more likely to be from The Wilds"),
        new("MapRedBeastsSpawnedInPairsChancePct", "Red Beasts appear in pairs", Good, v => $"Red Beasts have {v}% chance to appear in pairs"),
        new("MapYellowBeastsReplacedByRedBeastsChancePct", "Yellow Beasts become Red Beasts", Good, v => $"Yellow Beasts have {v}% chance to be Red Beasts instead"),
        new("MapRareBeastsMoreCommon", "Rarer beast varieties more common", Good, _ => "Rarer beast varieties are more common"),
        new("MapBestiaryAreaContainsBlackMorrigan", "Black Mórrigan in the map", Good, _ => "The map contains a Black Mórrigan"),
        new("MapXBestiaryPacksAreHarvestBeasts", "Beast packs are Harvest beasts", Plain, v => $"{v} beast pack{ImGuiEx.PluralSuffix(v)} {(v == 1 ? "is" : "are")} Harvest beasts"),
        new("MapBestiaryYellowBeastsAreHulks", "Yellow Beasts are Hulks", Plain, _ => "Yellow Beasts are Hulks"),
        new("MapDuplicateCapturedBeastsChancePct", "Copy of each captured beast", Good, v => $"{v}% chance to get a copy of each captured beast"),
        new("MapBeastPctChanceToNotBeConsumed", "Beasts not used up at the Blood Altar", Plain, v => $"Captured beasts have {v}% chance not to be used up at the Blood Altar"),
        new("MapBestiaryBeastsXAdditionalModifiers", "Additional Bestiary modifiers", Plain, v => $"Beasts have {v} additional Bestiary modifier{ImGuiEx.PluralSuffix(v)}"),
        new("MapBestiaryTalismanDropChancePct", "Talisman drop chance", Plain, v => $"Red Beasts are {v}% more likely to drop a Talisman"),
        new("MapBestiaryBeastsHaveXWispsOfAllTypes", "Beasts carry Wildwood Wisps", Plain, v => $"Beasts carry {v} Wildwood Wisps of each type"),
        new("MapBestiaryMonsterFromLeagueItemRarityPctPermyriad", "Beast item rarity", Plain, v => $"Beasts drop items with {v / 100f:0.#}% increased rarity"),
        new("MapRedBeastDoubleExperienceChancePct", "Red Beasts grant double experience", Plain, v => $"Red Beasts have {v}% chance to grant double experience"),
        new("MapRedBeastsAreLuckyChancePct", "Red Beasts are Lucky", Plain, v => $"Red Beasts have {v}% chance to be Lucky"),
        new("MapBestiaryMonsterDifficultyTankinessPct", "Beasts are tougher", Bad, v => $"Beasts are {v}% tougher"),
        new("MapBestiaryMonsterLifePctFinal", "Beasts have more life", Bad, v => $"Beasts have {v}% more life"),
        new("MapBestiaryMonsterDamagePct", "Beasts deal increased damage", Bad, v => $"Beasts deal {v}% increased damage"),
        new("MapBestiaryMonsterDamagePctFinal", "Beasts deal more damage", Bad, v => $"Beasts deal {v}% more damage"),
        new("MapRedBeastsHaveOnslaught", "Red Beasts have Onslaught", Bad, _ => "Red Beasts have Onslaught"),
        new("MapBestiaryBeastHasXBestialRage", "Beasts have Bestial Rage", Bad, v => $"Beasts have {v} Bestial Rage"),
        new("MapBestiaryBeastChanceToBreakFree", "Beasts break free of the net", Bad, v => $"Beasts have {v}% chance to break free of the net"),
        new("MapBestiaryEinharBloodExplodeAura", "Einhar's blood aura", Plain, _ => "Einhar has a blood aura that makes enemies explode"),
        new("MapEinharMissionBeastInvasionChancePct", "Beast invasion", Plain, v => $"{v}% chance for a beast invasion"),
        new("MapBestiaryScarabMoreLikelyPct", "Bestiary Threads more likely to drop", Plain, v => $"Bestiary Threads are {v}% more likely to drop"),
        new("MapDisableBestiaryScarabDrops", "Bestiary Threads cannot drop", Plain, _ => "Bestiary Threads cannot drop"),
    ];

    private static readonly HashSet<string> Known = Effects.Select(e => e.Stat).ToHashSet(StringComparer.Ordinal);
    private static readonly Regex WordStart = new("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    private readonly GameController _game;
    private readonly BeastsSettings _settings;

    private string _text = string.Empty;
    private Color[] _lineColors = [];
    private DateTime _builtUtc = DateTime.MinValue;

    public ThreadEffectsOverlay(GameController game, BeastsSettings settings)
    {
        _game = game;
        _settings = settings;
    }

    public void Render()
    {
        var window = _settings.ThreadEffects;
        if (!window.Show.Value || !Visible()) return;

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

        OverlayWindow.Draw(_game, "##BeastsV3ThreadEffects", _text,
            window.XPos.Value, window.YPos.Value, style, centerHorizontally: false, lineColors: _lineColors);
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

    // Fills the text and the colour of each of its lines. The text is empty when the map
    // carries no beast stat at all.
    private void Build()
    {
        _text = string.Empty;
        _lineColors = [];

        var stats = _game?.IngameState?.Data?.MapStats;
        if (stats == null || stats.Count == 0) return;

        var beastStats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stat in stats)
        {
            var name = stat.Key.ToString();
            if (name.Contains("Bestiary") || name.Contains("Beast")) beastStats[name] = stat.Value;
        }
        if (beastStats.Count == 0) return;

        var plain = _settings.ThreadEffects.TextColor.Value;
        var lines = new List<string>();
        var colors = new List<Color>();

        if (beastStats.TryGetValue(ThreadCountStat, out var threads) && threads > 0)
        {
            lines.Add($"Bestiary Threads: {threads}");
            colors.Add(plain);
        }

        foreach (var effect in Effects)
        {
            if (!beastStats.TryGetValue(effect.Stat, out var value) || value == 0) continue;
            var mode = ModeOf(effect);
            if (mode == ThreadEffectsSettings.Hide) continue;
            lines.Add("- " + effect.Describe(value));
            colors.Add(ColorOf(mode));
        }

        // Effects the plugin has no entry for always show, so a new one is noticed.
        foreach (var (name, value) in beastStats.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (value == 0 || Internal.Contains(name) || Known.Contains(name)) continue;
            lines.Add($"- {WordStart.Replace(name.StartsWith("Map") ? name[3..] : name, " ")}: {value}");
            colors.Add(plain);
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
