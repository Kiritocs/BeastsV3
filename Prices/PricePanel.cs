using System;
using System.Collections.Generic;
using System.Numerics;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using ImGuiNET;

namespace BeastsV3.Prices;

// Draws the Tracked Beasts picker table in the settings menu.
public sealed class PricePanel
{
    private static readonly Vector4 TrackedColor = new(0.4f, 1f, 0.4f, 1f);

    private static readonly Vector4 TalismanOnlyColor = new(0.84f, 0.67f, 0.24f, 1f);

    // Size of the search input buffer, in bytes.
    private const uint SearchBufferSize = 64;

    private readonly BeastsSettings _settings;
    private readonly PriceService _prices;

    // Search term; view state only, not persisted.
    private string _search = string.Empty;

    // Reused buffer for the per-frame filter and sort.
    private readonly List<TrackedBeast> _rows = new();

    public PricePanel(BeastsSettings settings, PriceService prices)
    {
        _settings = settings;
        _prices = prices;
    }

    // Draws the table. Each row has two independent toggles: Trk marks the beast as worth
    // capturing, Tal marks its talisman as worth seeing on overlays.
    public void Draw()
    {
        DrawPriceAgeLine();

        var showTalismans = _settings.BeastPrices.TrackTalismanPrices.Value;
        if (!showTalismans)
        {
            ImGui.TextDisabled("Turn on Track Talisman Prices above to price talismans and use the Tal column.");
        }

        DrawSearchBar();
        ImGui.Separator();

        var columns = showTalismans ? 6 : 3;
        if (!BeginPickerTable(columns, showTalismans)) return;

        var filter = _search.Trim();
        BuildRows(filter);
        SortRows(showTalismans);

        foreach (var beast in _rows) DrawBeastRow(beast, showTalismans);

        // Explains an empty table caused by the search filter.
        if (_rows.Count == 0)
        {
            ImGui.TableNextRow();
            for (var i = 0; i < columns; i++) ImGui.TableNextColumn();
            ImGui.TextDisabled($"No beasts match \"{filter}\".");
        }

        ImGui.EndTable();
    }

    // Stated outright rather than in a tooltip: LastUpdated is only when this plugin fetched,
    // and poe.ninja's copy already had its own age, which adds to it.
    private void DrawPriceAgeLine()
    {
        ImGui.Text($"Prices as of: {_settings.BeastPrices.LastUpdated}");
        ImGui.SameLine();
        ImGui.TextDisabled(_prices.UpstreamAgeSeconds is { } upstreamAge
            ? $"(poe.ninja's copy was {upstreamAge / 60} min old then)"
            : "(poe.ninja's own age unknown)");
    }

    // Opens the picker table and lays out its header row; false when ImGui skipped the table.
    private static bool BeginPickerTable(int columns, bool showTalismans)
    {
        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.BordersOuter |
            ImGuiTableFlags.BordersV |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Sortable |
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##BeastsV3Picker", columns, tableFlags, new Vector2(0, 400))) return false;

        // The checkbox columns are unsortable so ticking one does not move the row.
        ImGui.TableSetupColumn("Trk", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 28);
        if (showTalismans)
            ImGui.TableSetupColumn("Tal", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 28);

        // Both prices sit together, beast then talisman, so they can be read as one pair.
        ImGui.TableSetupColumn("Price",
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending,
            SortableHeaderWidth("Price", 60));
        if (showTalismans)
            ImGui.TableSetupColumn("Tal Price",
                ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending,
                SortableHeaderWidth("Tal Price", 70));

        ImGui.TableSetupColumn("Beast", ImGuiTableColumnFlags.WidthStretch);
        if (showTalismans)
            ImGui.TableSetupColumn("Talisman", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        return true;
    }

    // Width a sortable header needs: its text, the sort arrow ImGui draws beside it and the cell
    // padding. Sized rather than hardcoded, or the header clips to "Pri..." at larger font scales.
    private static float SortableHeaderWidth(string header, float minimumWidth)
    {
        var style = ImGui.GetStyle();
        var sortArrow = ImGui.GetFontSize() + style.FramePadding.X * 2f;
        return Math.Max(minimumWidth, ImGui.CalcTextSize(header).X + sortArrow + style.CellPadding.X * 2f);
    }

    // One table row: the two toggles, both prices, then the beast and its talisman.
    private void DrawBeastRow(TrackedBeast beast, bool showTalismans)
    {
        var hasTalisman = TalismanCatalog.TryGetByBeast(beast.Name, out var talisman);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var isTracked = _prices.IsTracked(beast.Name);
        if (ImGui.Checkbox($"##trk_{beast.Name}", ref isTracked))
            _prices.ToggleEnabled(beast.Name, isTracked);
        SetTooltip("Track this beast: overlays, Bestiary regex, completion and analytics.");

        var isTalisman = false;
        if (showTalismans)
        {
            ImGui.TableNextColumn();
            if (hasTalisman)
            {
                isTalisman = _settings.BeastPrices.EnabledTalismans.Contains(beast.Name);
                if (ImGui.Checkbox($"##tal_{beast.Name}", ref isTalisman))
                    _prices.ToggleTalismanEnabled(beast.Name, isTalisman);
                SetTooltip("Show this beast for its talisman. Stays out of the regex, completion and analytics unless Trk is also ticked.");
            }
            else
            {
                ImGui.TextDisabled("-");
                SetTooltip("This beast drops no talisman.");
            }
        }

        ImGui.TableNextColumn();
        ImGui.Text(_prices.TryGetBeastPriceText(beast.Name, out var priceText) ? priceText : "?");

        if (showTalismans)
        {
            ImGui.TableNextColumn();
            ImGui.Text(hasTalisman && _prices.TryGetTalismanPriceText(beast.Name, out var talismanPrice)
                ? talismanPrice
                : "?");
        }

        ImGui.TableNextColumn();
        // Row colors match the map overlay colors.
        if (isTracked) ImGui.TextColored(TrackedColor, beast.Name);
        else if (isTalisman) ImGui.TextColored(TalismanOnlyColor, beast.Name);
        else ImGui.TextDisabled(beast.Name);

        if (!showTalismans) return;

        ImGui.TableNextColumn();
        if (hasTalisman)
        {
            ImGui.TextDisabled(talisman.TalismanName);
            SetTooltip(talisman.Implicit);
        }
        else
        {
            ImGui.TextDisabled("-");
        }
    }

    // Fills _rows with catalog beasts matching the filter.
    private void BuildRows(string filter)
    {
        _rows.Clear();
        foreach (var beast in _prices.SortedByPrice)
        {
            var talismanName = TalismanCatalog.TryGetByBeast(beast.Name, out var talisman)
                ? talisman.TalismanName
                : null;
            if (Matches(beast.Name, talismanName, filter)) _rows.Add(beast);
        }
    }

    // Sorts the rows by the clicked header. Column indices shift when the Tal columns show,
    // so they are derived rather than hardcoded.
    private void SortRows(bool showTalismans)
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsCount <= 0) return;

        var spec = specs.Specs;
        var ascending = spec.SortDirection != ImGuiSortDirection.Descending;
        var column = spec.ColumnIndex;

        // Trk [Tal] Price [Tal Price] Beast [Talisman]
        var priceCol = showTalismans ? 2 : 1;
        var talPriceCol = priceCol + 1;
        var beastCol = showTalismans ? priceCol + 2 : priceCol + 1;
        var talismanCol = beastCol + 1;

        // Direction applies to the primary key only; ties stay in name order.
        var sign = ascending ? 1 : -1;

        Comparison<TrackedBeast> comparison;
        if (column == beastCol)
            comparison = (a, b) => sign * NameTiebreak(a.Name, b.Name);
        else if (showTalismans && column == talismanCol)
            comparison = (a, b) => Compare(TalismanNameOf(a), TalismanNameOf(b), a, b, sign);
        else if (showTalismans && column == talPriceCol)
            comparison = (a, b) => Compare(TalismanPriceOf(a), TalismanPriceOf(b), a, b, sign);
        else if (column == priceCol)
            comparison = (a, b) => Compare(BeastPriceOf(a), BeastPriceOf(b), a, b, sign);
        else
            return;

        _rows.Sort(comparison);
        specs.SpecsDirty = false;
    }

    // Compares prices, sorting unpriced rows to the bottom in both directions.
    private static int Compare(float a, float b, TrackedBeast rowA, TrackedBeast rowB, int sign)
    {
        var hasA = a >= 0;
        var hasB = b >= 0;
        if (hasA != hasB) return hasA ? -1 : 1;
        if (!hasA) return NameTiebreak(rowA.Name, rowB.Name);

        var byPrice = a.CompareTo(b);
        return byPrice != 0 ? sign * byPrice : NameTiebreak(rowA.Name, rowB.Name);
    }

    // Compares talisman names, sorting beasts without one to the bottom either way.
    private static int Compare(string a, string b, TrackedBeast rowA, TrackedBeast rowB, int sign)
    {
        var hasA = !string.IsNullOrEmpty(a);
        var hasB = !string.IsNullOrEmpty(b);
        if (hasA != hasB) return hasA ? -1 : 1;
        if (!hasA) return NameTiebreak(rowA.Name, rowB.Name);

        var byText = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return byText != 0 ? sign * byText : NameTiebreak(rowA.Name, rowB.Name);
    }

    private static int NameTiebreak(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

    private float BeastPriceOf(TrackedBeast beast) =>
        _prices.BeastPrices.TryGetValue(beast.Name, out var price) ? price : -1f;

    private float TalismanPriceOf(TrackedBeast beast) =>
        _prices.TalismanPrices.TryGetValue(beast.Name, out var price) ? price : -1f;

    private static string TalismanNameOf(TrackedBeast beast) =>
        TalismanCatalog.TryGetByBeast(beast.Name, out var talisman) ? talisman.TalismanName : null;

    private static void SetTooltip(string text)
    {
        if (!ImGui.IsItemHovered() || string.IsNullOrEmpty(text)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void DrawSearchBar()
    {
        // Leaves room for the Clear button on the same line.
        ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - 60f));

        var search = _search;
        if (ImGui.InputTextWithHint("##BeastsV3PickerSearch", "Search by name or family...", ref search, SearchBufferSize))
            _search = search;

        ImGui.SameLine();
        if (ImGui.Button("Clear##BeastsV3PickerSearch"))
            _search = string.Empty;
    }

    // Matches the filter against the beast name, its family and its talisman name.
    private static bool Matches(string beastName, string talismanName, string filter) =>
        filter.Length == 0 ||
        beastName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        BeastCatalog.GetFamily(beastName).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (talismanName != null && talismanName.Contains(filter, StringComparison.OrdinalIgnoreCase));
}
