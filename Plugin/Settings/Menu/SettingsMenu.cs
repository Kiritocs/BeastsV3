using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BeastsV3.Analytics;
using BeastsV3.Automation;
using BeastsV3.Shared;
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
}

// The plugin's settings menu: a category rail, a section pane, a search box and a home
// page. Categories come from the "Category: Section" prefixes in the [Menu] labels.
public sealed class SettingsMenu
{
    private const float RailWidth = 172f;
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
        ImGui.InputTextWithHint("##bv3search", "settings by name - try \"hotkey\", \"colour\", \"delay\"",
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
        ImGui.BeginChild("##bv3rail", new Vector2(RailWidth, height), MenuTheme.BorderedChild);
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
        RailItem(MenuTree.HomeCategory, sectionCount: 0);

        if (_categories.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        foreach (var category in _categories)
            RailItem(category.Name, category.Sections.Count);
    }

    private void RailItem(string name, int sectionCount)
    {
        var selected = string.Equals(_selectedCategory, name, StringComparison.OrdinalIgnoreCase);
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight() + 4f;
        var origin = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton("##bv3rail" + name, new Vector2(width, height));
        if (ImGui.IsItemClicked()) _selectedCategory = name;
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
        draw.AddText(new Vector2(origin.X + 11f, textY), MenuTheme.U32(textColor), name);

        if (sectionCount <= 0) return;

        var count = sectionCount.ToString();
        var countWidth = ImGui.CalcTextSize(count).X;
        var countColor = selected ? MenuTheme.WithAlpha(MenuTheme.Panel, 0.6f) : MenuTheme.Border;
        draw.AddText(new Vector2(max.X - countWidth - 10f, textY), MenuTheme.U32(countColor), count);
    }

    private void DrawContent()
    {
        if (string.Equals(_selectedCategory, MenuTree.HomeCategory, StringComparison.OrdinalIgnoreCase))
        {
            DrawHome();
            return;
        }

        var category = _categories.FirstOrDefault(c =>
            string.Equals(c.Name, _selectedCategory, StringComparison.OrdinalIgnoreCase));

        if (category == null)
        {
            ImGui.TextColored(MenuTheme.Muted, "Nothing here.");
            return;
        }

        var labelWidth = MenuWidgets.LabelWidth();
        for (var i = 0; i < category.Sections.Count; i++)
        {
            if (i > 0)
            {
                ImGui.Dummy(new Vector2(0f, 4f));
                ImGui.Separator();
                ImGui.Dummy(new Vector2(0f, 4f));
            }

            DrawSection(category.Sections[i], labelWidth);
        }
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

        MenuWidgets.SectionHeading("Where things are");
        MenuWidgets.Caption("Pick a category on the left, or search above to jump straight to one setting. "
            + "Hover any label for the full explanation.");
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
        MenuWidgets.Pill($"prices: {lastUpdated}", pricesStale ? MenuTheme.Warn : MenuTheme.Good,
            "When poe.ninja prices were last fetched.");

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

        if (string.Equals(_selectedCategory, MenuTree.HomeCategory, StringComparison.OrdinalIgnoreCase)) return;
        if (_categories.Any(c => string.Equals(c.Name, _selectedCategory, StringComparison.OrdinalIgnoreCase))) return;

        _selectedCategory = MenuTree.HomeCategory;
    }

    private static T Invoke<T>(Func<T> accessor)
    {
        if (accessor == null) return default;
        try { return accessor(); }
        catch { return default; }
    }

}
