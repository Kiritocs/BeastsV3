using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BeastsV3.Analytics;
using BeastsV3.Automation;
using BeastsV3.Shared;
using System.Windows.Forms;
using ExileCore.Shared.Nodes;
using ImGuiNET;

namespace BeastsV3.Plugin.Settings.Menu;

// Live values shown on the menu's home page, read through delegates so they survive an
// unload.
public sealed class MenuContext
{
    public Func<SessionState> Session { get; init; }
    public Func<RuntimeState> Automation { get; init; }
    public Func<string> DashboardUrl { get; init; }

    // How stale poe.ninja's copy was when prices were last fetched, or null when they sent no
    // Age header. A getter, since the menu outlives any single refresh.
    public Func<int?> UpstreamPriceAgeSeconds { get; init; }
}

// The plugin's settings menu: a category rail, a section pane, a search box and a home
// page. Categories come from the "Category: Section" prefixes in the [Menu] labels.
public sealed class SettingsMenu
{
    // Inner padding of a landing card.
    private static readonly Vector2 CardPadding = new(11f, 8f);

    // The rail sizes itself to its longest label: the host's font scale is not known here, and a
    // fixed width either clipped section names or wasted space on the settings pane.
    private const float RailMinWidth = 176f;
    private const float RailMaxWidth = 264f;
    private const float RailLabelIndent = 22f;
    private const float RailSectionIndent = 30f;
    private const float MinBodyHeight = 200f;
    private const float FallbackBodyHeight = 540f;

    // Most buttons laid side by side in one row.
    private const int MaxButtonsPerRow = 3;

    // How often the reflected tree is rebuilt, so replaced settings groups are picked up.
    private static readonly TimeSpan RebuildInterval = TimeSpan.FromMilliseconds(500);

    private readonly BeastsSettings _settings;
    private readonly MenuContext _context;

    private List<MenuCategory> _categories = new();
    private List<MenuItem> _allItems = new();
    private DateTime _builtAtUtc = DateTime.MinValue;

    private string _selectedCategory = MenuTree.HomeCategory;

    // Null means the category's landing view; otherwise the one section being shown.
    private string _selectedSection;

    // Whether the selected category is showing its sections. Clicking the open category folds
    // it away without changing what the content pane shows.
    private bool _categoryExpanded = true;

    private float _railWidth = RailMinWidth;
    private string _search = string.Empty;

    public SettingsMenu(BeastsSettings settings, MenuContext context = null)
    {
        _settings = settings;
        _context = context;
    }

    public void Draw()
    {
        if (_settings == null) return;

        EnsureTree();

        MenuTheme.Push();
        try
        {
            DrawHeader();
            DrawSearchBar();
            DrawBody();
        }
        finally
        {
            // In a finally so the style stack stays balanced if drawing throws.
            MenuTheme.Pop();
        }
    }

    // ---- chrome --------------------------------------------------------

    // Draws the title bar, master toggle and search box.
    private void DrawHeader()
    {
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextColored(MenuTheme.Gold, "Beasts V3");
        ImGui.SetWindowFontScale(1f);

        ImGui.SameLine();
        var toggleWidth = ImGui.GetFrameHeight() * 1.45f;
        var labelWidth = ImGui.CalcTextSize("Enabled").X + ImGui.GetStyle().ItemSpacing.X;
        MenuWidgets.AlignRight(toggleWidth + labelWidth);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(_settings.Enable.Value ? MenuTheme.Good : MenuTheme.Muted, "Enabled");
        MenuWidgets.Tip("Master switch. Off means the plugin renders and automates nothing.");
        ImGui.SameLine();

        var enabled = _settings.Enable.Value;
        if (MenuWidgets.ToggleSwitch("##bv3master", ref enabled)) _settings.Enable.Value = enabled;

        ImGui.Spacing();
    }

    private void DrawSearchBar()
    {
        const float clearWidth = 58f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var searching = !string.IsNullOrWhiteSpace(_search);

        // Labelled so the field reads as an input rather than leftover text.
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(searching ? MenuTheme.Gold : MenuTheme.Muted, "Search");
        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Border, searching ? MenuTheme.Gold : MenuTheme.BorderStrong);

        ImGui.SetNextItemWidth(MathF.Max(120f,
            ImGui.GetContentRegionAvail().X - clearWidth - spacing));
        ImGui.InputTextWithHint("##bv3search", "settings by name - try \"hotkey\", \"color\", \"delay\"",
            ref _search, 64);

        ImGui.PopStyleColor();

        ImGui.SameLine();
        if (ImGui.Button("Clear##bv3search", new Vector2(clearWidth, 0f))) _search = string.Empty;

        ImGui.Spacing();
    }

    private void DrawBody()
    {
        // Fills the host's available height.
        var available = ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().WindowPadding.Y;
        var height = available > MinBodyHeight ? available : FallbackBodyHeight;

        if (!string.IsNullOrWhiteSpace(_search))
        {
            DrawSearchResults(height);
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.ChildBg, MenuTheme.Panel);
        ImGui.BeginChild("##bv3rail", new Vector2(_railWidth, height), MenuTheme.BorderedChild);
        DrawRail();
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.SameLine();

        ImGui.BeginChild("##bv3content", new Vector2(0f, height), MenuTheme.BorderedChild);
        DrawContent();
        ImGui.EndChild();
    }

    private void DrawRail()
    {
        RailCategory(MenuTree.HomeCategory, null);

        if (_categories.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        foreach (var category in _categories)
        {
            RailCategory(category.Name, category);

            // A single-section category is its own page, so it never expands. Otherwise only the
            // open category lists its sections, which keeps the rail one screen tall.
            if (!IsSelected(category.Name) || !Expandable(category) || !_categoryExpanded) continue;

            foreach (var section in category.Sections)
                RailSection(category.Name, section);
        }
    }

    // Only categories with something to choose between expand.
    private static bool Expandable(MenuCategory category) => category != null && category.Sections.Count > 1;

    // The section a category opens on: its only one, or the landing view when there is a choice.
    private static MenuSection OnlySection(MenuCategory category) =>
        category != null && category.Sections.Count == 1 ? category.Sections[0] : null;

    private bool IsSelected(string category) =>
        string.Equals(_selectedCategory, category, StringComparison.OrdinalIgnoreCase);

    // A top-level row. A category with several sections opens on its landing view; one with a
    // single section opens that section directly, since a list of one is not a choice.
    private void RailCategory(string name, MenuCategory category)
    {
        var sectionCount = category?.Sections.Count ?? 0;
        var selected = IsSelected(name);
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight() + 4f;
        var origin = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton("##bv3rail" + name, new Vector2(width, height));
        if (ImGui.IsItemClicked())
        {
            if (selected && Expandable(category))
            {
                // Clicking the open category again folds its sections away. The content pane keeps
                // showing whatever you were reading.
                _categoryExpanded = !_categoryExpanded;
            }
            else
            {
                _selectedCategory = name;
                _selectedSection = OnlySection(category)?.Title;
                _categoryExpanded = true;
            }
        }
        var hovered = ImGui.IsItemHovered();

        var draw = ImGui.GetWindowDrawList();
        var max = new Vector2(origin.X + width, origin.Y + height);

        if (selected)
        {
            draw.AddRectFilled(origin, max, MenuTheme.U32(MenuTheme.Gold), 4f);
            draw.AddRectFilled(origin, new Vector2(origin.X + 3f, max.Y), MenuTheme.U32(MenuTheme.GoldBright));
        }
        else if (hovered)
        {
            draw.AddRectFilled(origin, max, MenuTheme.U32(MenuTheme.Card), 4f);
        }

        var textColor = selected ? MenuTheme.Panel : hovered ? MenuTheme.TextBright : MenuTheme.Muted;
        var textY = origin.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f);
        var textX = origin.X + 10f;

        if (Expandable(category))
        {
            DrawCaret(draw, new Vector2(origin.X + 12f, origin.Y + (height * 0.5f)),
                selected && _categoryExpanded, textColor);
            textX = origin.X + RailLabelIndent;
        }

        // The count is dropped while the category is open: the sections are listed directly
        // below, and on a narrow rail the number ran into the longer category names.
        var showCount = Expandable(category) && (!selected || !_categoryExpanded);
        var countWidth = showCount ? ImGui.CalcTextSize(sectionCount.ToString()).X : 0f;
        var labelWidth = max.X - textX - countWidth - (showCount ? 14f : 8f);

        draw.AddText(new Vector2(textX, textY), MenuTheme.U32(textColor), Fit(name, labelWidth, out var clipped));
        if (clipped) MenuWidgets.Tip(name);

        if (!showCount) return;

        draw.AddText(new Vector2(max.X - countWidth - 10f, textY),
            MenuTheme.U32(MenuTheme.Border), sectionCount.ToString());
    }

    // An indented section row under its open category.
    private void RailSection(string categoryName, MenuSection section)
    {
        var selected = string.Equals(_selectedSection, section.Title, StringComparison.OrdinalIgnoreCase);
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetTextLineHeight() + 7f;
        var origin = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton("##bv3sec" + categoryName + section.Title, new Vector2(width, height));
        if (ImGui.IsItemClicked()) _selectedSection = section.Title;
        var hovered = ImGui.IsItemHovered();

        var draw = ImGui.GetWindowDrawList();
        var max = new Vector2(origin.X + width, origin.Y + height);

        if (selected)
        {
            draw.AddRectFilled(origin, max, MenuTheme.U32(MenuTheme.WithAlpha(MenuTheme.Gold, 0.16f)), 4f);
            draw.AddRectFilled(new Vector2(origin.X + 18f, origin.Y), new Vector2(origin.X + 20f, max.Y),
                MenuTheme.U32(MenuTheme.Gold));
        }
        else if (hovered)
        {
            draw.AddRectFilled(origin, max, MenuTheme.U32(MenuTheme.Card), 4f);
        }

        var textColor = selected ? MenuTheme.GoldBright : hovered ? MenuTheme.TextBright : MenuTheme.Muted;
        var textY = origin.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f);

        // Indented past the category label so the nesting is visible without a connector line.
        var textX = origin.X + RailSectionIndent;

        var label = Fit(section.Title, max.X - textX - 8f, out var clipped);
        draw.AddText(new Vector2(textX, textY), MenuTheme.U32(textColor), label);

        // The tooltip carries the description; a clipped name needs its full text too.
        MenuWidgets.Tip(clipped && !string.IsNullOrWhiteSpace(section.Tooltip)
            ? section.Title + " - " + section.Tooltip
            : clipped ? section.Title : section.Tooltip);
    }

    // Trims a rail label to the width available, so a long section name reads as
    // "Merchant (Faus..." instead of running off the edge of the rail.
    private static string Fit(string text, float maxWidth, out bool clipped)
    {
        clipped = false;
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth) return text;

        clipped = true;
        const string ellipsis = "...";
        var ellipsisWidth = ImGui.CalcTextSize(ellipsis).X;

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd();
            if (ImGui.CalcTextSize(candidate).X + ellipsisWidth <= maxWidth) return candidate + ellipsis;
        }

        return ellipsis;
    }

    // Drawn rather than typed: the default font has no arrow glyphs.
    private static void DrawCaret(ImDrawListPtr draw, Vector2 center, bool open, Vector4 color)
    {
        const float r = 3.5f;
        var packed = MenuTheme.U32(color);

        if (open)
        {
            draw.AddTriangleFilled(
                new Vector2(center.X - r, center.Y - (r * 0.6f)),
                new Vector2(center.X + r, center.Y - (r * 0.6f)),
                new Vector2(center.X, center.Y + (r * 0.9f)), packed);
            return;
        }

        draw.AddTriangleFilled(
            new Vector2(center.X - (r * 0.6f), center.Y - r),
            new Vector2(center.X - (r * 0.6f), center.Y + r),
            new Vector2(center.X + (r * 0.9f), center.Y), packed);
    }

    private void DrawContent()
    {
        if (IsSelected(MenuTree.HomeCategory))
        {
            DrawHome();
            return;
        }

        var category = _categories.FirstOrDefault(c => IsSelected(c.Name));
        if (category == null)
        {
            ImGui.TextColored(MenuTheme.Muted, "Nothing here.");
            return;
        }

        // A single-section category resolves to that section even with nothing selected, so it
        // never shows a landing page listing one card.
        var section = category.Sections.FirstOrDefault(s =>
            string.Equals(s.Title, _selectedSection, StringComparison.OrdinalIgnoreCase)) ?? OnlySection(category);

        if (section == null)
        {
            DrawCategoryLanding(category);
            return;
        }

        // Nothing to go back to when the category is one page.
        if (Expandable(category)) DrawBreadcrumb(category.Name, section.Title);

        DrawSection(section, MenuWidgets.LabelWidth());
    }

    // Category name, then the section. The category is clickable, so there is a way back to the
    // landing view without going via the rail.
    private void DrawBreadcrumb(string categoryName, string sectionTitle)
    {
        ImGui.TextColored(MenuTheme.Info, categoryName);
        if (ImGui.IsItemClicked()) _selectedSection = null;

        ImGui.SameLine(0f, 6f);
        ImGui.TextColored(MenuTheme.Border, ">");
        ImGui.SameLine(0f, 6f);
        ImGui.TextColored(MenuTheme.Muted, sectionTitle);
        ImGui.Spacing();
    }

    // What a category shows before you pick a section: its sections as cards, each with the
    // description from its [Menu] attribute. Those descriptions exist already and are otherwise
    // only visible as a hover tooltip.
    private void DrawCategoryLanding(MenuCategory category)
    {
        MenuWidgets.SectionHeading(category.Name);
        MenuWidgets.Caption(category.Sections.Count == 1
            ? "1 section."
            : $"{category.Sections.Count} sections. Pick one here or in the list on the left.");
        ImGui.Spacing();

        foreach (var section in category.Sections)
            DrawLandingCard(section);
    }

    private void DrawLandingCard(MenuSection section)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var textWidth = width - (CardPadding.X * 2f);
        var hasTooltip = !string.IsNullOrWhiteSpace(section.Tooltip);

        var titleHeight = ImGui.GetTextLineHeight();
        var tooltipHeight = hasTooltip
            ? ImGui.CalcTextSize(section.Tooltip, false, textWidth).Y + 3f
            : 0f;
        var height = (CardPadding.Y * 2f) + titleHeight + tooltipHeight;

        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##bv3card" + section.Title, new Vector2(width, height));
        if (ImGui.IsItemClicked()) _selectedSection = section.Title;
        var hovered = ImGui.IsItemHovered();
        var after = ImGui.GetCursorPos();

        var draw = ImGui.GetWindowDrawList();
        var max = new Vector2(origin.X + width, origin.Y + height);
        draw.AddRectFilled(origin, max, MenuTheme.U32(hovered ? MenuTheme.Surface : MenuTheme.Card), 4f);
        draw.AddRect(origin, max, MenuTheme.U32(hovered ? MenuTheme.GoldDim : MenuTheme.Border), 4f);

        // Text goes through ImGui rather than the draw list so it wraps the same way every other
        // caption in the menu does.
        ImGui.SetCursorScreenPos(new Vector2(origin.X + CardPadding.X, origin.Y + CardPadding.Y));
        ImGui.TextColored(hovered ? MenuTheme.GoldBright : MenuTheme.TextBright, section.Title);

        if (hasTooltip)
        {
            ImGui.SetCursorScreenPos(
                new Vector2(origin.X + CardPadding.X, origin.Y + CardPadding.Y + titleHeight + 3f));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + textWidth);
            ImGui.TextColored(MenuTheme.Muted, section.Tooltip);
            ImGui.PopTextWrapPos();
        }

        ImGui.SetCursorPos(after);
        ImGui.Dummy(new Vector2(0f, 6f));
    }

    private static void DrawSection(MenuSection section, float labelWidth)
    {
        MenuWidgets.SectionHeading(section.Title);
        if (!string.IsNullOrWhiteSpace(section.Tooltip)) MenuWidgets.Caption(section.Tooltip);
        ImGui.Spacing();
        DrawGroupBody(section.Root, labelWidth);
    }

    private static void DrawGroupBody(MenuGroup group, float labelWidth)
    {
        if (group == null) return;

        for (var index = 0; index < group.Entries.Count; index++)
        {
            var entry = group.Entries[index];

            // Consecutive buttons are collected and laid out as one strip.
            if (entry.Item?.Node is ButtonNode)
            {
                var run = new List<MenuItem>();
                while (index < group.Entries.Count && group.Entries[index].Item?.Node is ButtonNode)
                {
                    run.Add(group.Entries[index].Item);
                    index++;
                }

                index--;
                DrawButtonRun(run);
                continue;
            }

            if (entry.Item != null)
            {
                MenuWidgets.DrawItem(entry.Item, labelWidth);
                continue;
            }

            var nested = entry.Group;
            if (nested == null) continue;

            var flags = nested.CollapsedByDefault
                ? ImGuiTreeNodeFlags.None
                : ImGuiTreeNodeFlags.DefaultOpen;

            var open = ImGui.CollapsingHeader($"{nested.Label}##bv3{nested.Id}", flags);
            // Drawn before the body, since the tooltip attaches to the last item drawn.
            MenuWidgets.Tip(nested.Tooltip);

            if (!open) continue;

            ImGui.Indent(10f);
            DrawGroupBody(nested, MathF.Max(MenuWidgets.RowLabelMin, labelWidth - 10f));
            ImGui.Unindent(10f);
            ImGui.Spacing();
        }
    }

    // Lays a run of buttons into rows, sized from the widest label so none are clipped.
    private static void DrawButtonRun(IReadOnlyList<MenuItem> buttons)
    {
        if (buttons.Count == 0) return;

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var padding = ImGui.GetStyle().FramePadding.X * 2f;
        var widest = buttons.Max(item => ImGui.CalcTextSize(item.Label).X) + padding + 14f;

        for (var start = 0; start < buttons.Count; start += MaxButtonsPerRow)
        {
            var rowAvailable = ImGui.GetContentRegionAvail().X;
            var fit = Math.Clamp((int)(rowAvailable / MathF.Max(1f, widest)), 1, MaxButtonsPerRow);
            var count = Math.Min(fit, buttons.Count - start);
            var width = (rowAvailable - (spacing * (count - 1))) / count;

            for (var column = 0; column < count; column++)
            {
                if (column > 0) ImGui.SameLine();

                var item = buttons[start + column];
                if (ImGui.Button(item.Label + "##bv3" + item.Id, new Vector2(width, 0f)))
                    (item.Node as ButtonNode)?.OnPressed?.Invoke();

                MenuWidgets.Tip(item.Tooltip);
            }

            // Advances by what was actually drawn, not the maximum.
            start += count - MaxButtonsPerRow;
        }
    }

    private void DrawSearchResults(float height)
    {
        ImGui.BeginChild("##bv3results", new Vector2(0f, height), MenuTheme.BorderedChild);

        var query = _search.Trim().ToLowerInvariant();
        var matches = _allItems.Where(item => item.SearchText.Contains(query)).Take(150).ToList();

        if (matches.Count == 0)
        {
            ImGui.TextColored(MenuTheme.Muted, $"No settings match \"{_search.Trim()}\".");
            ImGui.EndChild();
            return;
        }

        MenuWidgets.Caption($"{matches.Count} setting{ImGuiEx.PluralSuffix(matches.Count)} matching \"{_search.Trim()}\"");
        ImGui.Spacing();

        var labelWidth = MenuWidgets.LabelWidth();
        string lastBreadcrumb = null;

        foreach (var item in matches)
        {
            if (!string.Equals(item.Breadcrumb, lastBreadcrumb, StringComparison.Ordinal))
            {
                if (lastBreadcrumb != null) ImGui.Dummy(new Vector2(0f, 4f));
                MenuWidgets.SectionHeading(item.Breadcrumb);
                lastBreadcrumb = item.Breadcrumb;
            }

            MenuWidgets.DrawItem(item, labelWidth);
        }

        ImGui.EndChild();
    }

    // ---- home ----------------------------------------------------------

    // Draws the home page: live session numbers and the mid-run action buttons.
    private void DrawHome()
    {
        var session = Invoke(_context?.Session);
        var automation = Invoke(_context?.Automation);
        var now = DateTime.UtcNow;

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var cardWidth = MathF.Max(70f, (ImGui.GetContentRegionAvail().X - (spacing * 3f)) / 4f);

        MenuWidgets.MetricCard("time", "Session",
            session != null ? ImGuiEx.FormatDuration(session.GetTotalTime(now)) : "-",
            MenuTheme.TextBright, cardWidth);
        ImGui.SameLine();
        MenuWidgets.MetricCard("maps", "Maps",
            session?.CompletedMapCount.ToString() ?? "-", MenuTheme.TextBright, cardWidth);
        ImGui.SameLine();
        MenuWidgets.MetricCard("beasts", "Beasts",
            session?.SessionBeastsFound.ToString() ?? "-", MenuTheme.TextBright, cardWidth);
        ImGui.SameLine();
        MenuWidgets.MetricCard("tracked", "Tracked",
            (_settings.BeastPrices.EnabledBeasts?.Count ?? 0).ToString(), MenuTheme.Gold, cardWidth);

        ImGui.Dummy(new Vector2(0f, 6f));

        DrawStatusPills(automation);

        ImGui.Dummy(new Vector2(0f, 8f));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 4f));

        MenuWidgets.SectionHeading("Quick actions");
        DrawQuickActions();

        ImGui.Dummy(new Vector2(0f, 8f));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 4f));

        MenuWidgets.SectionHeading("Hotkeys");
        DrawHotkeyOverview();

        ImGui.Dummy(new Vector2(0f, 8f));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 4f));

        MenuWidgets.Caption("Pick a category on the left, or search above to jump straight to one setting. "
            + "Hover any label for the full explanation.");
    }

    // Every hotkey in the settings tree and what it is bound to, so the answer to "what did I
    // bind that to" is on the front page instead of spread over six sections.
    private void DrawHotkeyOverview()
    {
        var hotkeys = _allItems
            .Where(item => item.Node is HotkeyNodeV2)
            .ToList();

        if (hotkeys.Count == 0)
        {
            MenuWidgets.Caption("No hotkeys in this build.");
            return;
        }

        var bound = hotkeys.Count(item => ((HotkeyNodeV2)item.Node).Value.Key != Keys.None);
        MenuWidgets.Caption(bound == 0
            ? "Nothing is bound yet - nothing can run."
            : $"{bound} of {hotkeys.Count} bound. Click a row to open the section it lives in.");
        ImGui.Spacing();

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                                      ImGuiTableFlags.BordersV | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##bv3hotkeys", 3, flags)) return;

        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 118f);
        ImGui.TableHeadersRow();

        foreach (var item in hotkeys)
        {
            var key = ((HotkeyNodeV2)item.Node).Value.Key;
            var isBound = key != Keys.None;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextColored(isBound ? MenuTheme.TextBright : MenuTheme.Muted, item.Label);
            MenuWidgets.Tip(item.Tooltip);

            ImGui.TableNextColumn();
            if (ImGui.Selectable(item.Breadcrumb + "##bv3hk" + item.Id)) GoTo(item.Breadcrumb);
            MenuWidgets.Tip("Open this section.");

            ImGui.TableNextColumn();
            ImGui.TextColored(isBound ? MenuTheme.Gold : MenuTheme.Muted, isBound ? key.ToString() : "unbound");
        }

        ImGui.EndTable();
    }

    // Jumps the rail to the "Category: Section" a search or hotkey row points at.
    private void GoTo(string breadcrumb)
    {
        if (string.IsNullOrWhiteSpace(breadcrumb)) return;

        // MenuTree builds these as "Category  >  Section", or just the category when the two match.
        var parts = breadcrumb.Split('>', 2);
        var categoryName = parts[0].Trim();
        var sectionTitle = parts.Length > 1 ? parts[1].Trim() : null;

        var category = _categories.FirstOrDefault(c =>
            string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));
        if (category == null) return;

        _selectedCategory = category.Name;
        _categoryExpanded = true;
        _selectedSection = category.Sections
            .FirstOrDefault(s => string.Equals(s.Title, sectionTitle, StringComparison.OrdinalIgnoreCase))?.Title
            ?? OnlySection(category)?.Title;
    }

    private void DrawStatusPills(RuntimeState automation)
    {
        var league = string.IsNullOrWhiteSpace(_settings.BeastPrices.League.Value)
            ? "no league set"
            : _settings.BeastPrices.League.Value;

        MenuWidgets.Pill(league, MenuTheme.Info,
            _settings.BeastPrices.AutoSyncLeague.Value
                ? "League is being synced from the logged-in character."
                : "Auto sync is off - this league name is whatever you typed.");

        ImGui.SameLine();
        var lastUpdated = string.IsNullOrWhiteSpace(_settings.BeastPrices.LastUpdated)
            ? "never"
            : _settings.BeastPrices.LastUpdated;
        var pricesStale = string.Equals(lastUpdated, "never", StringComparison.OrdinalIgnoreCase);

        // Two ages, and they add up: LastUpdated is when this plugin fetched, upstream age is how
        // stale poe.ninja's copy already was then.
        var upstreamAge = _context?.UpstreamPriceAgeSeconds?.Invoke();
        var upstreamNote = upstreamAge is { } seconds
            ? $" poe.ninja's copy was {seconds / 60} min old at that point, so the true age is both combined."
            : " poe.ninja sent no age header, so how stale their copy was is unknown.";

        // On the pill, not just the tooltip: this changes how much to trust every price on screen.
        var upstreamSuffix = upstreamAge is { } age ? $" +{age / 60}m upstream" : string.Empty;

        MenuWidgets.Pill($"prices: {lastUpdated}{upstreamSuffix}",
            pricesStale ? MenuTheme.Warn : MenuTheme.Good,
            "When poe.ninja prices were last fetched." + upstreamNote);

        ImGui.SameLine();
        if (automation?.IsRunning == true)
        {
            MenuWidgets.Pill("automation running", MenuTheme.Warn,
                string.IsNullOrWhiteSpace(automation.LastStatusMessage)
                    ? "A workflow is in progress."
                    : automation.LastStatusMessage);
        }
        else
        {
            MenuWidgets.Pill("automation idle", MenuTheme.Muted,
                automation == null ? "Plugin is not loaded." : "No workflow is running.");
        }

        ImGui.SameLine();
        if (_settings.Analytics.Enable.Value)
        {
            MenuWidgets.Pill("analytics on", MenuTheme.Good, "Sessions and maps are being recorded.");
        }
        else
        {
            MenuWidgets.Pill("analytics off", MenuTheme.Muted, "Nothing is being recorded this session.");
        }

        if (_settings.Analytics.Telemetry.ShareAnonymousData.Value)
        {
            ImGui.SameLine();
            MenuWidgets.Pill("community sharing on", MenuTheme.Warn,
                "Completed maps are being uploaded. Turn off under Analytics -> Community Data Sharing.");
        }

        var url = Invoke(_context?.DashboardUrl);
        if (string.IsNullOrWhiteSpace(url)) return;

        ImGui.SameLine();
        MenuWidgets.Pill("dashboard", MenuTheme.Info, url);
    }

    private void DrawQuickActions()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(90f, (ImGui.GetContentRegionAvail().X - (spacing * 2f)) / 3f);
        var size = new Vector2(width, 0f);

        QuickAction("Refresh prices##bv3home", size, _settings.BeastPrices.FetchPrices,
            "Fetch the latest beast and market prices from poe.ninja now.");
        ImGui.SameLine();
        QuickAction("Open dashboard##bv3home", size, _settings.Analytics.Web.OpenInBrowser,
            "Open the analytics web dashboard in your browser.");
        ImGui.SameLine();
        QuickAction("Save snapshot##bv3home", size, _settings.Analytics.SaveSessionSnapshot,
            "Save the current session so it can be compared later.");

        QuickAction("Reset session##bv3home", size, _settings.Analytics.ResetSession,
            "Clear session totals. Hold Shift and click to confirm.");
        ImGui.SameLine();
        QuickAction("Reset map average##bv3home", size, _settings.Analytics.ResetMapAverage,
            "Clear the running per-map average. Hold Shift and click to confirm.");
        ImGui.SameLine();
        QuickAction("Open log folder##bv3home", size, _settings.LogFile.OpenFolder,
            "Open config/BeastsV3Logs in Explorer.");

        var fullWidth = new Vector2(ImGui.GetContentRegionAvail().X, 0f);
        QuickAction("Complete current map##bv3home", fullWidth, _settings.Analytics.CompleteCurrentMap,
            "Bank the last map's progress and mark it done, whether you're still standing in it or " +
            "already back in hideout/town. Beast labels, the tracked window and map markers hide " +
            "right away. For runs that never trigger normal completion, like gathering spawn-rate " +
            "data without killing anything.");
    }

    private static void QuickAction(string label, Vector2 size, ButtonNode node, string tooltip)
    {
        var wired = node?.OnPressed != null;
        if (!wired) ImGui.PushStyleColor(ImGuiCol.Text, MenuTheme.Muted);

        if (ImGui.Button(label, size) && wired) node.OnPressed.Invoke();

        if (!wired) ImGui.PopStyleColor();
        MenuWidgets.Tip(wired ? tooltip : "Unavailable until the plugin finishes loading.");
    }

    // ---- model ---------------------------------------------------------

    // Rebuilds the reflected settings tree when RebuildInterval has elapsed.
    private void EnsureTree()
    {
        var now = DateTime.UtcNow;
        if (_categories.Count > 0 && now - _builtAtUtc < RebuildInterval) return;

        _categories = MenuTree.Build(_settings);
        _allItems = MenuTree.Flatten(_categories);
        _builtAtUtc = now;
        MeasureRail();

        if (string.Equals(_selectedCategory, MenuTree.HomeCategory, StringComparison.OrdinalIgnoreCase)) return;
        if (_categories.Any(c => string.Equals(c.Name, _selectedCategory, StringComparison.OrdinalIgnoreCase))) return;

        _selectedCategory = MenuTree.HomeCategory;
        _selectedSection = null;
        _categoryExpanded = true;
    }

    // Widest label the rail has to show, plus its indents and padding. Sections only count when
    // their category can expand; a single-section category never lists them.
    private void MeasureRail()
    {
        var widest = ImGui.CalcTextSize(MenuTree.HomeCategory).X + RailLabelIndent;

        foreach (var category in _categories)
        {
            var expandable = Expandable(category);
            var countWidth = expandable ? ImGui.CalcTextSize(category.Sections.Count.ToString()).X + 14f : 0f;
            widest = MathF.Max(widest, ImGui.CalcTextSize(category.Name).X + RailLabelIndent + countWidth);

            if (!expandable) continue;

            foreach (var section in category.Sections)
                widest = MathF.Max(widest, ImGui.CalcTextSize(section.Title).X + RailSectionIndent);
        }

        var padding = (ImGui.GetStyle().WindowPadding.X * 2f) + 10f;
        _railWidth = Math.Clamp(widest + padding, RailMinWidth, RailMaxWidth);
    }

    private static T Invoke<T>(Func<T> accessor)
    {
        if (accessor == null) return default;
        try { return accessor(); }
        catch { return default; }
    }

}
