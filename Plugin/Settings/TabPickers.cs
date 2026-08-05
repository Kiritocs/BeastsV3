using System;
using System.Collections.Generic;
using BeastsV3.Automation.Ui;
using ExileCore.Shared.Nodes;
using ImGuiNET;

namespace BeastsV3.Plugin.Settings;

// Dropdown pickers for settings that name a stash tab or map, drawn in the settings menu.
// Names are only readable while the owning panel is open, so a closed panel shows the
// current value and a hint instead of a list.
public sealed class TabPickers
{
    private readonly BeastsSettings _settings;
    private readonly StashUi _stash;
    private readonly MerchantUi _merchant;
    private readonly AtlasUi _atlas;

    public TabPickers(BeastsSettings settings, StashUi stash, MerchantUi merchant, AtlasUi atlas)
    {
        _settings = settings;
        _stash = stash;
        _merchant = merchant;
        _atlas = atlas;
    }

    public void DrawItemizedBeastTab() =>
        DrawStashTabList("ItemizedBeasts", _settings.BestiaryAutomation.ItemizedBeastTabs,
            emptyHint: "No tabs set - auto-stash will not run.");

    public void DrawRedBeastTab() =>
        DrawStashTabList("RedBeasts", _settings.BestiaryAutomation.RedBeastTabs,
            emptyHint: "No tabs set - red beasts go to the Itemized Beasts tabs.");

    public void DrawRestockTargetTab(RestockTargetSettings target, int slot) =>
        DrawStashTabPicker($"RestockTarget{slot}", target.StashTabName, "Select tab");

    public void DrawFaustusShopTab()
    {
        var tabs = _settings.MerchantAutomation.FaustusShopTabs;

        if (_merchant.Panel?.IsVisible != true)
        {
            DrawClosedListHint(tabs, "Talk to Faustus to list his shop tabs.");
            return;
        }

        var names = _merchant.AvailableShopTabNames();
        if (names.Count == 0)
        {
            ImGui.TextDisabled("No shop tabs found in the open merchant panel.");
            return;
        }

        // Numbered and pinned like stash tabs. Faustus happily hands out several tabs with
        // the same name, and the position is the only thing separating them.
        DrawTabList("FaustusShopTab", tabs, names, numbered: true,
            emptyHint: "No tabs set - listing will not run.", resolveIndex: _merchant.ResolveShopTabIndex);
    }

    public void DrawAtlasMap()
    {
        var selected = _settings.Restock.SelectedMapToRun;

        // Normalised on draw so older stored values still match a combo entry.
        var normalized = AtlasUi.NormalizeMapSelectionValue(selected.Value);
        if (!string.Equals(selected.Value, normalized, StringComparison.Ordinal))
            selected.Value = normalized;

        var isKeepCurrent = string.Equals(normalized, AtlasUi.OpenMapSelectionValue, StringComparison.OrdinalIgnoreCase);
        var preview = isKeepCurrent ? KeepCurrentMapLabel : normalized;
        var names = _atlas.AvailableMapNames();

        ImGui.SetNextItemWidth(ComboWidth);
        if (ImGui.BeginCombo("##BeastsV3PickerAtlasMap", preview))
        {
            var filter = BeginFilter("AtlasMap", "Filter maps...");
            var shown = 0;

            if (Matches(KeepCurrentMapLabel, filter))
            {
                if (ImGui.Selectable(KeepCurrentMapLabel, isKeepCurrent))
                    selected.Value = AtlasUi.OpenMapSelectionValue;
                if (isKeepCurrent) ImGui.SetItemDefaultFocus();
                shown++;
            }

            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                if (!Matches(name, filter)) continue;
                shown++;

                var isSelected = string.Equals(selected.Value, name, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{name}##AtlasMap{i}", isSelected)) selected.Value = name;
                if (isSelected) ImGui.SetItemDefaultFocus();
            }

            if (shown == 0) ImGui.TextDisabled("No map matches that filter.");

            ImGui.EndCombo();
        }
        else
        {
            EndFilter("AtlasMap");
        }

        // Map names come from the game files, so no panel needs to be open.
        ImGui.TextDisabled(names.Count > 0
            ? $"{names.Count} maps loaded from AtlasNodes."
            : "Map list unavailable. Enter a game instance once to load Atlas data.");
    }

    // ---- private -------------------------------------------------------

    private const float ComboWidth = 240f;
    private const uint FilterBufferSize = 64;
    private const string KeepCurrentMapLabel = "Keep currently opened map";
    private const string ClearLabel = "(none)";
    private static readonly System.Numerics.Vector4 WarningColor = new(1f, 0.55f, 0.2f, 1f);

    // Filter text per combo, and which combos were open last frame so the box can take
    // focus the moment one opens. A full stash is dozens of tabs; scrolling to find one is
    // worse than typing three letters of it.
    private readonly Dictionary<string, string> _filters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openCombos = new(StringComparer.Ordinal);

    // Draws the filter box at the top of an open combo and returns its current text.
    private string BeginFilter(string id, string hint)
    {
        if (_openCombos.Add(id))
        {
            _filters[id] = string.Empty;
            // Only on the frame it opens. Every frame would reset the caret mid-word.
            ImGui.SetKeyboardFocusHere();
        }

        var filter = _filters.TryGetValue(id, out var stored) ? stored : string.Empty;

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint($"##BeastsV3Filter{id}", hint, ref filter, FilterBufferSize))
            _filters[id] = filter;

        ImGui.Separator();
        return filter;
    }

    private void EndFilter(string id) => _openCombos.Remove(id);

    private static bool Matches(string name, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return name?.IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void DrawStashTabPicker(string id, TextNode selected, string emptyPreview, bool allowClear = false)
    {
        if (!_stash.IsVisible)
        {
            DrawClosedPanelHint(selected, "Open your stash to list tab names.");
            return;
        }

        var names = _stash.TabNames();
        if (names.Count == 0)
        {
            ImGui.TextDisabled("No stash tabs found in the open stash.");
            return;
        }

        DrawCombo(id, selected, names, emptyPreview, numbered: true, allowClear);
    }

    private void DrawCombo(string id, TextNode selected, IReadOnlyList<string> names,
        string emptyPreview, bool numbered, bool allowClear)
    {
        var current = selected.Value ?? string.Empty;
        // Resolved rather than string-compared, so a pinned value ticks the tab it actually
        // points at instead of the first one sharing its name.
        var selectedIndex = _stash.ResolveTabIndex(current);
        var display = TabPin.DisplayName(current);
        var preview = string.IsNullOrWhiteSpace(current)
            ? emptyPreview
            : numbered && selectedIndex >= 0 ? $"{selectedIndex}: {display}" : display;

        ImGui.SetNextItemWidth(ComboWidth);
        if (!ImGui.BeginCombo($"##BeastsV3Picker{id}", preview))
        {
            EndFilter(id);
            return;
        }

        var filter = BeginFilter(id, "Filter tabs...");
        var duplicates = TabPin.Duplicates(names);
        var shown = 0;

        if (allowClear && Matches(ClearLabel, filter))
        {
            if (ImGui.Selectable(ClearLabel, string.IsNullOrWhiteSpace(current)))
                selected.Value = string.Empty;
            shown++;
        }

        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (!Matches(name, filter)) continue;
            shown++;

            var isSelected = selectedIndex >= 0
                ? i == selectedIndex
                : string.Equals(current, name, StringComparison.OrdinalIgnoreCase);

            // The index prefix is part of the label only; the value stays the tab name,
            // pinned with its position when the name alone cannot identify the tab.
            if (ImGui.Selectable(numbered ? $"{i}: {name}##{id}{i}" : $"{name}##{id}{i}", isSelected))
                selected.Value = duplicates.Contains(name) ? TabPin.Pin(name, i) : name;

            if (isSelected) ImGui.SetItemDefaultFocus();
        }

        if (shown == 0) ImGui.TextDisabled("Nothing matches that filter.");

        ImGui.EndCombo();
    }

    private static void DrawClosedPanelHint(TextNode selected, string hint)
    {
        var current = selected.Value;
        if (string.IsNullOrWhiteSpace(current)) ImGui.TextDisabled("Not set.");
        else if (TabPin.TrySplit(current, out var name, out var tabIndex)) ImGui.Text($"{tabIndex}: {name}");
        else ImGui.Text(current);

        ImGui.TextDisabled(hint);
    }

    // ---- ordered tab lists ---------------------------------------------

    private void DrawStashTabList(string id, List<string> tabs, string emptyHint)
    {
        if (!_stash.IsVisible)
        {
            DrawClosedListHint(tabs, "Open your stash to list tab names.");
            return;
        }

        var names = _stash.TabNames();
        if (names.Count == 0)
        {
            ImGui.TextDisabled("No stash tabs found in the open stash.");
            return;
        }

        DrawTabList(id, tabs, names, numbered: true, emptyHint, _stash.ResolveTabIndex);
    }

    // An add/remove list rather than a fixed row of slots: the number of overflow tabs
    // someone needs depends entirely on how long they run unattended, and fixed slots would
    // mean either a wall of empty dropdowns or an arbitrary ceiling. An empty list is the
    // natural way to say "none", so it needs no separate clear option.
    //
    // resolveIndex is null for lists whose entries are matched by name the whole way down,
    // and non-null for stash tabs, where a position can be pinned to tell apart tabs that
    // share a name.
    private void DrawTabList(string id, List<string> tabs, IReadOnlyList<string> names,
        bool numbered, string emptyHint, Func<string, int> resolveIndex)
    {
        if (tabs == null) return;

        // Deferred so the list isn't mutated while it's being enumerated for drawing.
        var removeAt = -1;
        var duplicateNames = resolveIndex != null ? TabPin.Duplicates(names) : null;

        for (var slot = 0; slot < tabs.Count; slot++)
        {
            ImGui.PushID($"{id}Slot{slot}");

            // Fill order is the whole semantic here, so the position is labelled.
            ImGui.TextDisabled($"{slot + 1}.");
            ImGui.SameLine();

            var current = tabs[slot] ?? string.Empty;
            var selectedIndex = resolveIndex?.Invoke(current) ?? -1;
            var display = TabPin.DisplayName(current);
            var preview = string.IsNullOrWhiteSpace(current)
                ? "Select tab"
                : numbered && selectedIndex >= 0 ? $"{selectedIndex}: {display}" : display;

            var comboId = $"{id}{slot}";

            ImGui.SetNextItemWidth(ComboWidth);
            if (ImGui.BeginCombo($"##BeastsV3TabList{comboId}", preview))
            {
                var filter = BeginFilter(comboId, "Filter tabs...");
                var shown = 0;

                for (var i = 0; i < names.Count; i++)
                {
                    var name = names[i];
                    if (!Matches(name, filter)) continue;
                    shown++;

                    var isSelected = selectedIndex >= 0
                        ? i == selectedIndex
                        : string.Equals(current, name, StringComparison.OrdinalIgnoreCase);
                    var label = numbered ? $"{i}: {name}" : name;

                    if (ImGui.Selectable($"{label}##{id}{slot}_{i}", isSelected))
                    {
                        // Pinned only when the name alone cannot identify the tab, so the
                        // settings file keeps plain readable names in the common case.
                        tabs[slot] = duplicateNames?.Contains(name) == true
                            ? TabPin.Pin(name, i)
                            : name;
                    }

                    if (isSelected) ImGui.SetItemDefaultFocus();
                }

                if (shown == 0) ImGui.TextDisabled("Nothing matches that filter.");

                ImGui.EndCombo();
            }
            else
            {
                EndFilter(comboId);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("X")) removeAt = slot;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this tab from the list.");

            // Duplicates would silently waste an overflow step, since a tab already filled
            // reports no free space the second time it comes round.
            if (!string.IsNullOrWhiteSpace(current) && FirstIndexOf(tabs, current) != slot)
            {
                ImGui.SameLine();
                ImGui.TextColored(WarningColor, "duplicate");
            }

            ImGui.PopID();
        }

        if (removeAt >= 0) tabs.RemoveAt(removeAt);

        if (ImGui.Button($"Add tab##BeastsV3TabListAdd{id}")) tabs.Add(string.Empty);

        if (tabs.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(emptyHint);
        }
    }

    private static int FirstIndexOf(List<string> tabs, string name)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            if (string.Equals(tabs[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    // Shown when the owning panel is closed and the names can't be read.
    private static void DrawClosedListHint(List<string> tabs, string hint)
    {
        if (tabs == null || tabs.Count == 0) ImGui.TextDisabled("No tabs set.");
        else ImGui.Text(string.Join("  ->  ", tabs.ConvertAll(TabPin.DisplayName)));

        ImGui.TextDisabled(hint);
    }
}
