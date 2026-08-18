using System;
using System.Collections.Generic;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using SharpDX;

namespace BeastsV3.Plugin.Settings;

// Root settings tree.
public class BeastsSettings : ISettings
{
    [Menu("Enabled", "Enable or disable the Beasts V3 plugin.")]
    public ToggleNode Enable { get; set; } = new(false);

    [Menu("Tracking: Prices", "poe.ninja price fetching and tracked-beast selection.")]
    public BeastPricesSettings BeastPrices { get; set; } = new();

    [Menu("Overlays: Counter", "On-screen beast counter and completion overlays.")]
    public CounterSettings Counter { get; set; } = new();

    [Menu("Overlays: Beast Labels", "In-world beast labels, large-map markers, tracked-beasts floating window, and inventory/stash/merchant price panels.")]
    public MapRenderSettings MapRender { get; set; } = new();

    [Menu("Overlays: Exploration Route", "Experimental route through the map that covers all beast spawns.")]
    public ExplorationRouteSettings ExplorationRoute { get; set; } = new();

    [Menu("Overlays: Automation Status", "Automation status message shown at the top of the screen while running.")]
    public AutomationStatusOverlaySettings AutomationStatus { get; set; } = new();

    [Menu("Overlays: Visibility", "When to auto-hide overlays.")]
    public VisibilitySettings Visibility { get; set; } = new();

    [Menu("Automation: Bestiary", "Bestiary itemize / delete / quick-button hotkeys and options.")]
    public BestiaryAutomationSettings BestiaryAutomation { get; set; } = new();

    [Menu("Automation: Faustus", "List itemized captured beasts to Faustus.")]
    public MerchantAutomationSettings MerchantAutomation { get; set; } = new();

    [Menu("Automation: Restock", "Pull configured maps/scarabs from stash into inventory to reach target quantities.")]
    public RestockSettings Restock { get; set; } = new();

    [Menu("Automation: Full Sequence", "One-key: Bestiary regex-itemize -> travel to hideout -> Faustus list.")]
    public HotkeyNodeV2 FullSequenceHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Automation: Panic Stop", "Emergency stop for any running automation. Works anytime, doesn't require UI panels to be visible.")]
    public HotkeyNodeV2 PanicStopHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Automation: Search Regex", "Auto-copy + auto-paste search regex when the Bestiary panel opens.")]
    public BestiaryClipboardSettings BestiaryClipboard { get; set; } = new();

    [Menu("Automation: Timing", "All automation delays, poll intervals, and timeouts in one place.")]
    public TimingSettings Timing { get; set; } = new();

    [Menu("Analytics", "Per-session and per-map analytics: overlay, autosave, session buttons.")]
    public AnalyticsSettings Analytics { get; set; } = new();

    [Menu("Diagnostics: Log File", "Write a full session log to disk, including the detail the console setting above hides.")]
    public LogFileSettings LogFile { get; set; } = new();

    [Menu("What's New", "Plugin update history, grouped by version.")]
    public ChangelogSettings Changelog { get; set; } = new();

}

[Submenu(CollapsedByDefault = true)]
public class ChangelogSettings
{
    [Menu("Update Timeline", "Changes grouped by version, newest first.")]
    [JsonIgnore]
    public CustomNode Panel { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class LogFileSettings
{
    // Read once at load; takes effect on reload.
    [Menu("Write Log File", "Record this session to config/BeastsV3Logs/BeastsV3.log, with the previous session kept alongside it as BeastsV3.prev.log. Takes effect on plugin reload.")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Max Size (MB)", "When the current log passes this, it rolls over to BeastsV3.prev.log and starts fresh, so a runaway session can't fill the disk. Takes effect on plugin reload.")]
    public RangeNode<int> MaxSizeMb { get; set; } = new(8, 1, 64);

    [Menu("Verbose Console Logging", "Also emit the detailed step-by-step lines to the ExileCore console. Noisy; the log file records them either way.")]
    public ToggleNode DebugLogging { get; set; } = new(false);

    [Menu("Open Log Folder", "Open config/BeastsV3Logs in Explorer.")]
    public ButtonNode OpenFolder { get; set; } = new();

    [Menu("Dump Diagnostics", "Write a full snapshot of the current state to the log: build, settings, area, tracker, markers, map cost, quest text and which UI panels are reachable. Press this right after something goes wrong, then send the log.")]
    public ButtonNode DumpDiagnostics { get; set; } = new();

    [Menu("Dump Diagnostics Hotkey", "Same as the button, but usable without opening settings - which matters when the problem only happens mid-run.")]
    public HotkeyNodeV2 DumpDiagnosticsHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Log File Status", "Where the log is being written, its current size, and whether any lines were lost.")]
    [JsonIgnore]
    public CustomNode StatusPanel { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class BeastPricesSettings
{
    [Menu("League", "poe.ninja league name; must match your current league exactly (e.g. Mirage).")]
    public TextNode League { get; set; } = new("Allflame");

    [Menu("Auto Sync League", "Overwrite the League field with the league the game reports for the logged-in character, whenever that's readable. Turn off to pin the field by hand.")]
    public ToggleNode AutoSyncLeague { get; set; } = new(true);

    [Menu("Auto Refresh (min)", "How often to auto-fetch prices in minutes. 0 = manual only.")]
    public RangeNode<int> AutoRefreshMinutes { get; set; } = new(10, 0, 60);

    [Menu("Refresh Prices", "Fetch the latest beast + market prices from poe.ninja now.")]
    public ButtonNode FetchPrices { get; set; } = new();

    [Menu("Select All Tracked Beasts", "Enable every beast in the tracked list.")]
    public ButtonNode SelectAll { get; set; } = new();

    [Menu("Clear Tracked Beasts", "Disable every beast in the tracked list.")]
    public ButtonNode DeselectAll { get; set; } = new();

    [Menu("Select Tracked Beasts >=15c", "Enable only beasts currently priced at 15 chaos or more.")]
    public ButtonNode Select15cPlus { get; set; } = new();

    [Menu("Track Talisman Prices", "Also fetch talisman base-type prices from poe.ninja when prices refresh, and enable the Talisman column in the picker below. Off by default because the base-type feed is large.")]
    public ToggleNode TrackTalismanPrices { get; set; } = new(false);

    [Menu("Add Talisman Price To Beast Price", "Show a beast's price with its talisman price added on, as \"95c +3c\", on world labels, map labels and the tracked-beasts window. Only applies to beasts whose Talisman box is ticked. A beast shown purely for its talisman always includes it, since that price is the reason it's on screen.")]
    public ToggleNode CombineTalismanPrice { get; set; } = new(false);

    [Menu("Select All Talismans", "Tick the Talisman box for every beast that drops one.")]
    public ButtonNode SelectAllTalismans { get; set; } = new();

    [Menu("Clear Talismans", "Untick every Talisman box.")]
    public ButtonNode DeselectAllTalismans { get; set; } = new();

    [Menu("Select Talismans >=15c", "Tick the Talisman box only for talismans currently priced at 15 chaos or more.")]
    public ButtonNode SelectTalismans15cPlus { get; set; } = new();

    [Menu("Tracked Beasts", "One row per beast with two independent choices. Track counts it as valuable everywhere: overlays, the Bestiary regex, completion and analytics. Talisman only puts it on overlays in the talisman colors so you can spot it - it stays out of the regex, completion and analytics. Tick both to do both.")]
    [JsonIgnore]
    public CustomNode BeastPicker { get; set; } = new();

    // Persisted by PriceService rather than by the host serializer.
    [JsonIgnore]
    public string LastUpdated { get; set; } = "never";

    [JsonIgnore]
    public HashSet<string> EnabledBeasts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Beasts selected for their talisman only; kept separate from EnabledBeasts so capture
    // and itemize logic never sees them.
    [JsonIgnore]
    public HashSet<string> EnabledTalismans { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[Submenu(CollapsedByDefault = true)]
public class CounterSettings
{
    [Menu("Show", "Show or hide the main counter window.")]
    public ToggleNode Show { get; set; } = new(true);

    [Menu("X Position (%)", "0 = left edge, 50 = center, 100 = right edge.")]
    public RangeNode<float> XPos { get; set; } = new(50, 0, 100);

    [Menu("Y Position (%)", "0 = top edge, 100 = bottom edge.")]
    public RangeNode<float> YPos { get; set; } = new(10, 0, 100);

    [Menu("Padding", "Inner spacing in pixels between text and window border.")]
    public RangeNode<float> Padding { get; set; } = new(6, 0, 50);

    [Menu("Border Thickness")]
    public RangeNode<int> BorderThickness { get; set; } = new(1, 1, 10);

    [Menu("Border Rounding")]
    public RangeNode<float> BorderRounding { get; set; } = new(0, 0, 25);

    [Menu("Text Scale", "Size multiplier before all beasts are found.")]
    public RangeNode<float> TextScale { get; set; } = new(1f, 0.5f, 4f);

    [Menu("Text Color")]
    public ColorNode TextColor { get; set; } = new(new Color(255, 180, 70, 255));

    [Menu("Border Color")]
    public ColorNode BorderColor { get; set; } = new(Color.Black);

    [Menu("Background Color")]
    public ColorNode BackgroundColor { get; set; } = new(new Color(0, 0, 0, 180));

    [Menu("Completed Style", "How the counter changes after all beasts in the area are found.")]
    public CompletedCounterSettings CompletedStyle { get; set; } = new();

    [Menu("Completed Message", "Floating message shown after all beasts are found.")]
    public CompletionMessageSettings CompletedMessage { get; set; } = new();

    [Menu("Tracked Completion Message", "Floating message shown after all beasts are found AND all tracked valuable beasts are captured.")]
    public CompletionMessageSettings TrackedCompletionMessage { get; set; } = new()
    {
        Text = new TextNode("All beasts found and tracked beasts captured!"),
        YPos = new RangeNode<float>(20, 0, 100),
    };
}

[Submenu(CollapsedByDefault = true)]
public class CompletedCounterSettings
{
    [Menu("Show While Not Complete", "Preview mode: apply the completed style even before all beasts are found.")]
    public ToggleNode ShowWhileNotComplete { get; set; } = new(false);

    [Menu("Text Scale")]
    public RangeNode<float> TextScale { get; set; } = new(1.8f, 0.5f, 6f);

    [Menu("Text Color")]
    public ColorNode TextColor { get; set; } = new(new Color(90, 255, 120, 255));

    [Menu("Border Color")]
    public ColorNode BorderColor { get; set; } = new(new Color(90, 255, 120, 255));
}

[Submenu(CollapsedByDefault = true)]
public class CompletionMessageSettings
{
    [Menu("Show")]
    public ToggleNode Show { get; set; } = new(true);

    [Menu("Show While Not Complete", "Preview mode.")]
    public ToggleNode ShowWhileNotComplete { get; set; } = new(false);

    [Menu("Message Text")]
    public TextNode Text { get; set; } = new("All beasts found!");

    [Menu("X Position (%)")]
    public RangeNode<float> XPos { get; set; } = new(50, 0, 100);

    [Menu("Y Position (%)")]
    public RangeNode<float> YPos { get; set; } = new(16, 0, 100);

    [Menu("Padding")]
    public RangeNode<float> Padding { get; set; } = new(8, 0, 50);

    [Menu("Border Thickness")]
    public RangeNode<int> BorderThickness { get; set; } = new(1, 1, 10);

    [Menu("Border Rounding")]
    public RangeNode<float> BorderRounding { get; set; } = new(4, 0, 25);

    [Menu("Text Scale")]
    public RangeNode<float> TextScale { get; set; } = new(1.4f, 0.5f, 6f);

    [Menu("Text Color")]
    public ColorNode TextColor { get; set; } = new(new Color(120, 255, 140, 255));

    [Menu("Border Color")]
    public ColorNode BorderColor { get; set; } = new(new Color(90, 255, 120, 255));

    [Menu("Background Color")]
    public ColorNode BackgroundColor { get; set; } = new(new Color(0, 0, 0, 200));
}

[Submenu(CollapsedByDefault = true)]
public class MapRenderSettings
{
    [Menu("Show World Labels", "Draw beast name/price labels + ground circles on tracked beasts in the 3D world.")]
    public ToggleNode ShowBeastLabelsInWorld { get; set; } = new(true);

    [Menu("Hide While Large Map Is Open", "Hide World Labels while the large map is open.")]
    public ToggleNode HideWhileLargeMapIsOpen { get; set; } = new(false);

    [Menu("Show Cached Tracked Beasts", "Keep a beast on the large map and in the tracked-beasts window after it leaves your vision bubble, at the last position it was seen. Off shows only beasts currently loaded. In-world labels are always live-only, since a remembered beast isn't there to draw a label over.")]
    public ToggleNode ShowCachedTrackedBeasts { get; set; } = new(true);

    [Menu("Cached Tag Text", "Tag appended in the tracked-beasts window to a beast that is only remembered, so a stale position isn't read as a live one. Leave empty to show no tag.")]
    public TextNode CachedTagText { get; set; } = new("(cached)");

    [Menu("Show Large Map Labels", "Draw beast markers on the large overlay map (Tab).")]
    public ToggleNode ShowBeastsOnMap { get; set; } = new(true);

    [Menu("Show Tracked Beasts Window", "Show the floating list of currently alive tracked beasts.")]
    public ToggleNode ShowTrackedBeastsWindow { get; set; } = new(true);

    [Menu("Only Show Enabled Beasts", "Only draw beasts you have checked in the tracked list. Off = draw every tracked-catalog beast.")]
    public ToggleNode ShowEnabledOnly { get; set; } = new(true);

    [Menu("Show Name Only On Map Labels", "On the large map, show only the name (no price).")]
    public ToggleNode ShowNameInsteadOfPrice { get; set; } = new(false);

    [Menu("Show Price Only On Map Labels", "On the large map, show only the price (no name). Ignored if Show Name Only is also on.")]
    public ToggleNode ShowPriceInsteadOfName { get; set; } = new(false);

    [Menu("Show Prices In Inventory", "Draw poe.ninja price on captured beast items in your inventory.")]
    public ToggleNode ShowPricesInInventory { get; set; } = new(true);

    [Menu("Show Prices In Stash", "Draw poe.ninja price on captured beast items in your stash.")]
    public ToggleNode ShowPricesInStash { get; set; } = new(true);

    [Menu("Show Prices In Merchant Panel", "Draw poe.ninja price on captured beast items in the Faustus offline merchant panel.")]
    public ToggleNode ShowPricesInMerchant { get; set; } = new(true);

    [Menu("Show Prices In Bestiary", "Draw poe.ninja price next to each beast in the captured-beasts Bestiary panel.")]
    public ToggleNode ShowPricesInBestiary { get; set; } = new(true);

    [Menu("Show Style Preview Window", "Floating window with sample labels so you can tune styling without needing live beasts.")]
    public ToggleNode ShowStylePreviewWindow { get; set; } = new(false);

    [Menu("Captured Status Text", "Text/colors for the two capture stages (Capturing, Captured).")]
    public CapturedStatusSettings CapturedText { get; set; } = new();

    [Menu("Colors", "Colors used by all beast overlays.")]
    public MapRenderColorSettings Colors { get; set; } = new();

    [Menu("Layout", "Spacing, radii, padding, thickness for overlays.")]
    public MapRenderLayoutSettings Layout { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class CapturedStatusSettings
{
    [Menu("Capture Text Only", "When on, show only the status text (no name/price) once capture starts.")]
    public ToggleNode ReplaceNameAndPriceWithStatusText { get; set; } = new(false);

    [Menu("Capturing Text", "Shown while the net is thrown.")]
    public TextNode CapturingText { get; set; } = new("Capturing");

    [Menu("Captured Text", "Shown after the beast is safely captured.")]
    public TextNode CapturedText { get; set; } = new("Captured");

    [Menu("Capturing Color")]
    public ColorNode CapturingColor { get; set; } = new(new Color(57, 255, 20, 255));

    [Menu("Captured Color")]
    public ColorNode CapturedColor { get; set; } = new(new Color(120, 220, 255, 255));
}

[Submenu(CollapsedByDefault = true)]
public class MapRenderColorSettings
{
    [Menu("World Beast Text")]
    public ColorNode WorldBeastText { get; set; } = new(new Color(180, 20, 20, 255));

    [Menu("World Captured Beast Text")]
    public ColorNode WorldCapturedBeastText { get; set; } = new(new Color(255, 40, 40, 255));

    [Menu("World Price Text")]
    public ColorNode WorldPriceText { get; set; } = new(new Color(255, 235, 120, 255));

    [Menu("World Text Outline")]
    public ColorNode WorldTextOutline { get; set; } = new(Color.Black);

    [Menu("World Beast Circle")]
    public ColorNode WorldBeastCircle { get; set; } = new(new Color(180, 20, 20, 255));

    [Menu("World Capture Ring")]
    public ColorNode WorldCaptureRing { get; set; } = new(Color.White);

    [Menu("World Captured Circle")]
    public ColorNode WorldCapturedCircle { get; set; } = new(new Color(120, 220, 255, 255));

    [Menu("Map Label Text")]
    public ColorNode MapLabelText { get; set; } = new(new Color(180, 20, 20, 255));

    [Menu("Map Label Background")]
    public ColorNode MapLabelBackground { get; set; } = new(new Color(0, 0, 0, 230));

    [Menu("Tracked Window Text")]
    public ColorNode TrackedWindowText { get; set; } = new(new Color(180, 20, 20, 255));

    [Menu("Talisman-Only World Text", "Color for a beast shown only because its talisman is selected.")]
    public ColorNode WorldTalismanOnlyText { get; set; } = new(new Color(215, 170, 60, 255));

    [Menu("Talisman-Only World Circle")]
    public ColorNode WorldTalismanOnlyCircle { get; set; } = new(new Color(215, 170, 60, 255));

    [Menu("Talisman-Only Map Label Text")]
    public ColorNode MapLabelTalismanOnlyText { get; set; } = new(new Color(215, 170, 60, 255));

    [Menu("Talisman-Only Tracked Window Text")]
    public ColorNode TrackedWindowTalismanOnlyText { get; set; } = new(new Color(215, 170, 60, 255));

    [Menu("Tracked Window Cached Tag", "Color of the tag marking a beast that is only remembered, not currently loaded. Muted by default so it reads as an annotation rather than a status.")]
    public ColorNode TrackedWindowCachedTag { get; set; } = new(new Color(150, 150, 150, 255));
}

[Submenu(CollapsedByDefault = true)]
public class MapRenderLayoutSettings
{
    [Menu("World Label Line Spacing", "Vertical spacing between world-label lines.")]
    public RangeNode<float> WorldTextLineSpacing { get; set; } = new(18f, 8f, 40f);

    [Menu("World Beast Circle Radius", "Ground circle radius in world units.")]
    public RangeNode<float> WorldBeastCircleRadius { get; set; } = new(80f, 20f, 200f);

    [Menu("World Circle Outline Thickness")]
    public RangeNode<float> WorldBeastCircleOutlineThickness { get; set; } = new(2f, 1f, 8f);

    [Menu("World Circle Fill Opacity (%)")]
    public RangeNode<int> WorldBeastCircleFillOpacityPercent { get; set; } = new(20, 0, 100);

    [Menu("Map Label Padding X")]
    public RangeNode<float> MapLabelPaddingX { get; set; } = new(4f, 0f, 20f);

    [Menu("Map Label Padding Y")]
    public RangeNode<float> MapLabelPaddingY { get; set; } = new(2f, 0f, 20f);
}

[Submenu(CollapsedByDefault = true)]
public class BestiaryAutomationSettings
{
    [Menu("Delete Hotkey", "Hotkey to delete every matching beast in the Bestiary panel.")]
    public HotkeyNodeV2 DeleteHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Regex Itemize Hotkey", "Hotkey to filter with regex + ctrl-click every matching beast.")]
    public HotkeyNodeV2 RegexItemizeHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Yellow Itemize Hotkey", "Hotkey to itemize every beast that is not in the plugin's beast catalog.")]
    public HotkeyNodeV2 YellowItemizeHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Challenges Window Hotkey", "Must match your Path of Exile Challenges keybind so automation can open the Bestiary panel when the Menagerie isn't reachable.")]
    public HotkeyNodeV2 ChallengesWindowHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Show Bestiary Quick Buttons", "Show 'Itemize All' / 'Delete All' buttons next to the captured-beasts panel.")]
    public ToggleNode ShowBestiaryButtons { get; set; } = new(false);

    [Menu("Show Inventory Quick Button", "Show a 'Right Click All Beasts' button next to inventory while in Menagerie or with the Bestiary panel open. It releases beasts from inventory and, when a stash tab is open, from that tab as well.")]
    public ToggleNode ShowInventoryButton { get; set; } = new(false);

    [Menu("Only Itemize Tracked Beasts", "During a regex itemize, skip any beast that isn't on your tracked list even if the in-game search matched it. The Bestiary search also looks at a beast's rare name, mods and recipes, so short regex fragments let untracked yellows through - and those have no price and can't be listed at Faustus. Turn off to itemize everything the search matches.")]
    public ToggleNode OnlyItemizeTrackedBeasts { get; set; } = new(true);

    [Menu("Auto-Stash After Itemize", "When enabled, itemized beasts are moved to the configured stash tab whenever inventory fills mid-itemize, and once itemizing finishes.")]
    public ToggleNode AutoStashAfterItemize { get; set; } = new(true);

    [Menu("Itemized Beasts Stash Tabs", "Stash tabs that captured-monster items go into, filled in order - when one is full the next is used. Add as many as you need; the run only fails once every one of them is full. Open your stash in-game to populate the dropdowns.")]
    [JsonIgnore]
    public CustomNode ItemizedBeastTabPicker { get; set; } = new();

    [Menu("Red Beasts Stash Tabs (optional)", "Optional separate tabs for red (tracked-valuable) beasts, also filled in order. Leave the list empty to send red beasts to the Itemized Beasts tabs above.")]
    [JsonIgnore]
    public CustomNode RedBeastTabPicker { get; set; } = new();

    // Ordered destinations. An empty list means "not configured" - for red beasts that
    // falls back to the itemized list, and for itemized beasts it disables auto-stash.
    [JsonProperty] public List<string> ItemizedBeastTabs { get; set; } = new();

    [JsonProperty] public List<string> RedBeastTabs { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class RestockSettings
{
    [Menu("Restock Hotkey", "Pull configured targets from stash into inventory. Stash must be open.")]
    public HotkeyNodeV2 RestockHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Load Map Device Hotkey", "Ctrl-click configured items from inventory into the open Map Device slots.")]
    public HotkeyNodeV2 LoadMapDeviceHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Selected Atlas Map", "Map to click on the Atlas before loading. 'Keep currently opened map' skips Atlas selection and uses whatever map is already loaded.")]
    [JsonIgnore]
    public CustomNode AtlasMapPicker { get; set; } = new();

    [JsonProperty] internal TextNode SelectedMapToRun { get; set; } = new("open Map");

    [Menu("Inventory Toggle Hotkey", "Must match your PoE inventory keybind (default I). Used to close inventory before scanning the Atlas.")]
    public HotkeyNodeV2 InventoryToggleHotkey { get; set; } = new(System.Windows.Forms.Keys.I);

    [Menu("Auto-Restock Missing Map Device Items", "When Map Device load finds a target short across the device slots, device storage and inventory, run Restock first instead of loading a partial device.")]
    public ToggleNode AutoRestockMissingMapDeviceItems { get; set; } = new(true);

    [Menu("Clear Non-Target Map Device Items", "Before loading, ctrl-click anything in the Map Device slots that isn't one of your targets back into inventory. Off leaves it in place, and a target with no free slot fails instead.")]
    public ToggleNode ClearNonTargetMapDeviceItems { get; set; } = new(true);

    [Menu("Enable Map Regex Filter", "Before pulling a 'Map (Tier N)' target, paste the pattern below into the map-stash search bar and take only highlighted matches. Applies to map targets only - fragment and scarab targets are untouched.")]
    public ToggleNode EnableMapRegexFilter { get; set; } = new(false);

    [Menu("Map Regex Pattern", "Pasted into the map-stash search bar before restocking maps. Build one at https://poe.re/#/maps. Restock fails rather than pulling unfiltered if this is empty while the filter is on.")]
    public TextNode MapRegexPattern { get; set; } = new(string.Empty);

    // Target 1 holds the map, since the device fills its map slot from the first match.
    [Menu("Target 1")] public RestockTargetSettings Target1 { get; set; } = new() { ItemName = new TextNode("Map (Tier 16)") };
    [Menu("Target 2")] public RestockTargetSettings Target2 { get; set; } = new() { ItemName = new TextNode("Bestiary Scarab of the Herd") };
    [Menu("Target 3")] public RestockTargetSettings Target3 { get; set; } = new() { ItemName = new TextNode("Bestiary Scarab of Duplicating") };
    [Menu("Target 4")] public RestockTargetSettings Target4 { get; set; } = new() { Enabled = new(false) };
    [Menu("Target 5")] public RestockTargetSettings Target5 { get; set; } = new() { Enabled = new(false) };
    [Menu("Target 6")] public RestockTargetSettings Target6 { get; set; } = new() { Enabled = new(false) };
}

[Submenu(CollapsedByDefault = true)]
public class RestockTargetSettings
{
    public const int MaxQuantity = 100;

    [Menu("Enabled")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Item Name", "Exact base item name (e.g. 'Bestiary Scarab of Duplicating').")]
    public TextNode ItemName { get; set; } = new(string.Empty);

    [Menu("Quantity", "Target total in inventory (0-100). Restock tops up to this amount. Amounts above one stack are fine - they spill into more inventory cells and, in the Map Device, into more slots.")]
    public RangeNode<int> Quantity { get; set; } = new(20, 0, MaxQuantity);

    [Menu("Stash Tab", "Pick the stash tab that holds this item. Open your stash in-game to populate the list.")]
    [JsonIgnore]
    public CustomNode StashTabPicker { get; set; } = new();

    [JsonProperty] internal TextNode StashTabName { get; set; } = new(string.Empty);
}

[Submenu(CollapsedByDefault = true)]
public class MerchantAutomationSettings
{
    [Menu("Faustus List Hotkey", "Hotkey to list all sellable captured beasts at Faustus.")]
    public HotkeyNodeV2 FaustusListHotkey { get; set; } = new(System.Windows.Forms.Keys.None);

    [Menu("Faustus Price Multiplier", "Scale poe.ninja prices before listing. 1.0 = poe.ninja price, 1.2 = list 20% higher.")]
    public RangeNode<float> FaustusPriceMultiplier { get; set; } = new(1f, 0.5f, 1.5f);

    [Menu("Refresh Prices Before Listing", "Fetch poe.ninja prices and wait for them before listing, so beasts are priced on current data rather than whatever the last background refresh left behind. Skipped when prices are already newer than the max age below. If the fetch fails or times out, listing continues on the prices already held.")]
    public ToggleNode RefreshPricesBeforeListing { get; set; } = new(true);

    [Menu("Max Price Age Before Listing (s)", "How old prices may be before a listing run refetches them. Low values cost a fetch on every run; high values risk listing on a stale market.")]
    public RangeNode<int> MaxPriceAgeBeforeListingSeconds { get; set; } = new(120, 10, 900);

    [Menu("Faustus Shop Tabs", "Faustus shop tabs beasts are listed into, filled in order - when one is full the next is used. Talk to Faustus to populate the dropdowns.")]
    [JsonIgnore]
    public CustomNode FaustusShopTabPicker { get; set; } = new();

    [JsonProperty] public List<string> FaustusShopTabs { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class BestiaryClipboardSettings
{
    [Menu("Enable Auto Copy", "Copy the search regex to the clipboard when the Bestiary panel opens.")]
    public ToggleNode EnableAutoCopy { get; set; } = new(true);

    [Menu("Auto Paste After Copy", "Also paste the regex into the Bestiary search field automatically.")]
    public ToggleNode AutoPasteAfterCopy { get; set; } = new(true);

    [Menu("Build Regex From Enabled Beasts", "Auto-build the regex from your Tracked Beasts list. When off, uses the manual regex below.")]
    public ToggleNode UseAutoRegex { get; set; } = new(true);

    [Menu("Manual Regex", "Search regex used when 'Build Regex From Enabled Beasts' is off.")]
    public TextNode ManualRegex { get; set; } = new(string.Empty);
}

// Every automation delay, poll interval and timeout, grouped by kind.
[Submenu(CollapsedByDefault = true)]
public class TimingSettings
{
    [Menu("General", "Master switches + flat extra delay.")]
    public TimingGeneralSettings General { get; set; } = new();

    [Menu("Clicks", "Delays applied around clicks (pre/post/key-tap).")]
    public TimingClicksSettings Clicks { get; set; } = new();

    [Menu("Polling", "How often wait loops re-check state.")]
    public TimingPollingSettings Polling { get; set; } = new();

    [Menu("Timeouts", "Upper bounds on how long a wait can take.")]
    public TimingTimeoutsSettings Timeouts { get; set; } = new();

    [Menu("Humanization", "Spread delays, curve the cursor, and click off-center so automation stops looking metronomic.")]
    public TimingHumanizationSettings Humanization { get; set; } = new();

    [Menu("Reset Timings To Defaults", "Restore every timing knob below to its shipping default.")]
    public ButtonNode ResetToDefaults { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class TimingHumanizationSettings
{
    [Menu("Enable Humanization", "Master switch. Off means every delay and cursor move behaves exactly as it did before.")]
    public ToggleNode Enable { get; set; } = new(false);

    // ---- presets ----

    [Menu("Preset: Light", "Barely-there jitter. Instant cursor, small delay spread. Fastest.")]
    public ButtonNode PresetLight { get; set; } = new();

    [Menu("Preset: Human", "Curved cursor travel, real key hold times, off-center clicks. The recommended balance.")]
    public ButtonNode PresetHuman { get; set; } = new();

    [Menu("Preset: Paranoid", "Slow curved travel, wide delay spread, frequent hesitation pauses. Noticeably slower runs.")]
    public ButtonNode PresetParanoid { get; set; } = new();

    // ---- delay spread ----

    [Menu("Delay Variance (%)", "Standard deviation of the delay spread, as a percentage of the configured delay. 30 means a 100ms delay lands mostly between 70ms and 130ms.")]
    public RangeNode<int> DelayVariancePercent { get; set; } = new(25, 0, 100);

    [Menu("Delay Floor (%)", "A humanized delay is never shorter than this share of the configured value.")]
    public RangeNode<int> MinDelayPercent { get; set; } = new(60, 0, 100);

    [Menu("Delay Ceiling (%)", "A humanized delay is never longer than this share of the configured value.")]
    public RangeNode<int> MaxDelayPercent { get; set; } = new(180, 100, 500);

    [Menu("Minimum Jitter (ms)", "Absolute spread applied even to very short delays, so a 1ms delay is not always exactly 1ms.")]
    public RangeNode<int> MinJitterMs { get; set; } = new(4, 0, 50);

    // ---- key presses ----

    [Menu("Key Hold Min (ms)", "Shortest time a key stays down between KeyDown and KeyUp.")]
    public RangeNode<int> KeyHoldMinMs { get; set; } = new(35, 0, 300);

    [Menu("Key Hold Max (ms)", "Longest time a key stays down between KeyDown and KeyUp.")]
    public RangeNode<int> KeyHoldMaxMs { get; set; } = new(85, 0, 300);

    // ---- click points ----

    [Menu("Jitter Click Points", "Click a random point inside the target instead of always dead-center.")]
    public ToggleNode ClickPointJitter { get; set; } = new(true);

    [Menu("Click Jitter Radius (px)", "Hard cap on how far from the center a click may land.")]
    public RangeNode<int> ClickJitterRadiusPx { get; set; } = new(5, 0, 40);

    [Menu("Click Jitter Element (%)", "Share of the target element the jitter may use, when the element's bounds are known. Capped by the radius above and always kept clear of the edge.")]
    public RangeNode<int> ClickJitterElementPercent { get; set; } = new(40, 0, 100);

    // ---- cursor travel (WindMouse) ----

    [Menu("Curved Cursor Travel", "Move the cursor along a WindMouse arc instead of teleporting it. Adds real travel time to every click.")]
    public ToggleNode UseWindMouse { get; set; } = new(true);

    [Menu("Minimum Travel Distance (px)", "Moves shorter than this teleport instead of tracing a path.")]
    public RangeNode<int> MinPathDistancePx { get; set; } = new(12, 0, 200);

    [Menu("Wind Strength", "How hard the random sideways force pushes. Higher is wobblier.")]
    public RangeNode<float> WindStrength { get; set; } = new(3.0f, 0.0f, 10.0f);

    [Menu("Gravity Strength", "How hard the cursor is pulled toward the target. Higher is straighter and faster.")]
    public RangeNode<float> GravityStrength { get; set; } = new(9.0f, 0.1f, 15.0f);

    [Menu("Step Size", "Maximum distance covered per path point. Higher means fewer, longer hops.")]
    public RangeNode<float> StepSize { get; set; } = new(12.0f, 1.0f, 40.0f);

    [Menu("Target Area", "Distance from the target at which the wind is damped and the cursor settles in.")]
    public RangeNode<float> TargetArea { get; set; } = new(12.0f, 0.0f, 40.0f);

    [Menu("Path Step Min Delay (ms)", "Shortest pause between two points of a cursor path.")]
    public RangeNode<int> PathStepMinDelayMs { get; set; } = new(1, 0, 50);

    [Menu("Path Step Max Delay (ms)", "Longest pause between two points of a cursor path. This is the main cost of curved travel.")]
    public RangeNode<int> PathStepMaxDelayMs { get; set; } = new(4, 0, 100);

    // ---- hesitation ----

    [Menu("Hesitation Chance (%)", "Chance that any one click is preceded by a longer 'looked away' pause.")]
    public RangeNode<int> HesitationChancePercent { get; set; } = new(4, 0, 100);

    [Menu("Hesitation Min (ms)", "Shortest hesitation pause.")]
    public RangeNode<int> HesitationMinMs { get; set; } = new(180, 0, 5000);

    [Menu("Hesitation Max (ms)", "Longest hesitation pause.")]
    public RangeNode<int> HesitationMaxMs { get; set; } = new(700, 0, 5000);

    [Menu("Cursor Drift During Pauses", "Wander the cursor a pixel or two during hesitation pauses. Off by default: it is the most likely knob to disturb a hover-sensitive UI.")]
    public ToggleNode CursorDriftDuringPauses { get; set; } = new(false);

    [Menu("Cursor Drift Radius (px)", "How far the cursor may wander during a hesitation pause.")]
    public RangeNode<int> CursorDriftRadiusPx { get; set; } = new(2, 0, 15);

    // ---- presets ----

    public enum Preset { Light, Human, Paranoid }

    // Stamps a whole coherent configuration in one go. Every knob stays individually
    // editable afterwards; a preset is a starting point, not a mode.
    public void Apply(Preset preset)
    {
        switch (preset)
        {
            case Preset.Light:
                DelayVariancePercent.Value = 15;
                MinDelayPercent.Value = 80;
                MaxDelayPercent.Value = 130;
                MinJitterMs.Value = 2;
                KeyHoldMinMs.Value = 20;
                KeyHoldMaxMs.Value = 45;
                ClickPointJitter.Value = true;
                ClickJitterRadiusPx.Value = 3;
                ClickJitterElementPercent.Value = 25;
                UseWindMouse.Value = false;
                MinPathDistancePx.Value = 12;
                PathStepMinDelayMs.Value = 0;
                PathStepMaxDelayMs.Value = 2;
                HesitationChancePercent.Value = 0;
                CursorDriftDuringPauses.Value = false;
                break;

            case Preset.Human:
                DelayVariancePercent.Value = 25;
                MinDelayPercent.Value = 60;
                MaxDelayPercent.Value = 180;
                MinJitterMs.Value = 4;
                KeyHoldMinMs.Value = 35;
                KeyHoldMaxMs.Value = 85;
                ClickPointJitter.Value = true;
                ClickJitterRadiusPx.Value = 5;
                ClickJitterElementPercent.Value = 40;
                UseWindMouse.Value = true;
                MinPathDistancePx.Value = 12;
                WindStrength.Value = 3.0f;
                GravityStrength.Value = 9.0f;
                StepSize.Value = 12.0f;
                TargetArea.Value = 12.0f;
                PathStepMinDelayMs.Value = 1;
                PathStepMaxDelayMs.Value = 4;
                HesitationChancePercent.Value = 4;
                HesitationMinMs.Value = 180;
                HesitationMaxMs.Value = 700;
                CursorDriftDuringPauses.Value = false;
                CursorDriftRadiusPx.Value = 2;
                break;

            case Preset.Paranoid:
                DelayVariancePercent.Value = 45;
                MinDelayPercent.Value = 50;
                MaxDelayPercent.Value = 260;
                MinJitterMs.Value = 8;
                KeyHoldMinMs.Value = 45;
                KeyHoldMaxMs.Value = 120;
                ClickPointJitter.Value = true;
                ClickJitterRadiusPx.Value = 8;
                ClickJitterElementPercent.Value = 55;
                UseWindMouse.Value = true;
                MinPathDistancePx.Value = 6;
                WindStrength.Value = 4.5f;
                GravityStrength.Value = 6.0f;
                StepSize.Value = 9.0f;
                TargetArea.Value = 18.0f;
                PathStepMinDelayMs.Value = 2;
                PathStepMaxDelayMs.Value = 8;
                HesitationChancePercent.Value = 12;
                HesitationMinMs.Value = 300;
                HesitationMaxMs.Value = 1400;
                CursorDriftDuringPauses.Value = true;
                CursorDriftRadiusPx.Value = 3;
                break;
        }

        Enable.Value = true;
    }
}

[Submenu(CollapsedByDefault = true)]
public class TimingGeneralSettings
{
    [Menu("Lock User Input During Automation", "Suppress user mouse+keyboard while automation runs. Trigger hotkeys still pass through.")]
    public ToggleNode LockUserInputDuringAutomation { get; set; } = new(true);

    [Menu("Include Server Latency In Delays", "Add ServerData.Latency to every automation wait. Enable if actions land too early on higher-ping connections.")]
    public ToggleNode IncludeServerLatencyInDelays { get; set; } = new(false);

    [Menu("Flat Extra Delay (ms)", "Added on top of every automation wait. Use as a global slowdown for stability.")]
    public RangeNode<int> FlatExtraDelayMs { get; set; } = new(0, 0, 500);

    [Menu("Batch Item Transfers", "Ctrl-click a whole pass of items and confirm once, instead of confirming each item before clicking the next. Much faster for Restock and Map Device loading. Turn off if transfers start getting dropped.")]
    public ToggleNode BatchItemTransfers { get; set; } = new(true);
}

[Submenu(CollapsedByDefault = true)]
public class TimingClicksSettings
{
    [Menu("Click Delay (ms)", "Minimum delay after any automation click.")]
    public RangeNode<int> ClickDelayMs { get; set; } = new(10, 0, 250);

    [Menu("UI Click Pre-Delay (ms)", "Delay before UI-element clicks.")]
    public RangeNode<int> UiClickPreDelayMs { get; set; } = new(15, 0, 250);

    [Menu("UI Click Post-Delay (ms)", "Delay after tab clicks / small UI selections.")]
    public RangeNode<int> UiClickPostDelayMs { get; set; } = new(15, 0, 250);

    [Menu("Ctrl-Click Pre-Delay (ms)", "Delay before ctrl-click starts.")]
    public RangeNode<int> CtrlClickPreDelayMs { get; set; } = new(10, 0, 250);

    [Menu("Ctrl-Click Post-Delay (ms)", "Delay after ctrl-click completes.")]
    public RangeNode<int> CtrlClickPostDelayMs { get; set; } = new(10, 0, 250);

    [Menu("Bestiary Click Delay (ms)", "Extra minimum delay after Bestiary-panel clicks.")]
    public RangeNode<int> BestiaryClickDelayMs { get; set; } = new(20, 0, 250);

    [Menu("Bestiary Itemize Pre-Delay (ms)", "Delay before each ctrl-click while itemizing beasts.")]
    public RangeNode<int> BestiaryItemizePreDelayMs { get; set; } = new(5, 0, 250);

    [Menu("Bestiary Itemize Post-Delay (ms)", "Delay after each ctrl-click while itemizing beasts.")]
    public RangeNode<int> BestiaryItemizePostDelayMs { get; set; } = new(0, 0, 250);

    [Menu("Key Tap Delay (ms)", "Delay between KeyDown/KeyUp for synthetic key taps.")]
    public RangeNode<int> KeyTapDelayMs { get; set; } = new(1, 0, 250);
}

[Submenu(CollapsedByDefault = true)]
public class TimingPollingSettings
{
    [Menu("Fast Poll Delay (ms)", "Poll interval for the ~30 wait loops in automation (inventory settle, UI appears, etc.).")]
    public RangeNode<int> FastPollDelayMs { get; set; } = new(15, 1, 500);

    [Menu("UI Check Initial Settle Delay (ms)", "Wait after a UI event before re-reading state.")]
    public RangeNode<int> UiCheckInitialSettleDelayMs { get; set; } = new(90, 0, 500);

    [Menu("Stash Open Poll Delay (ms)", "Poll interval while waiting for the stash to open.")]
    public RangeNode<int> StashOpenPollDelayMs { get; set; } = new(30, 0, 500);

    [Menu("Open Stash Post-Click Delay (ms)", "Delay after clicking to open the stash.")]
    public RangeNode<int> OpenStashPostClickDelayMs { get; set; } = new(250, 0, 2000);

    [Menu("Quantity Change Base Delay (ms)", "Base delay used when waiting for inventory / stash quantity changes.")]
    public RangeNode<int> QuantityChangeBaseDelayMs { get; set; } = new(100, 0, 500);

    [Menu("Quantity Settle Window (ms)", "Duration a quantity must stay stable to consider the change complete.")]
    public RangeNode<int> QuantitySettleStableWindowMs { get; set; } = new(100, 0, 500);

    [Menu("Bestiary Release Poll Delay (ms)", "Poll interval while waiting for a beast release/delete to complete.")]
    public RangeNode<int> BestiaryReleasePollDelayMs { get; set; } = new(10, 1, 500);

    [Menu("Tab Switch Delay (ms)", "Delay after switching stash tabs before scanning contents.")]
    public RangeNode<int> TabSwitchDelayMs { get; set; } = new(50, 0, 500);

    [Menu("Tab Retry Delay (ms)", "Delay between failed tab-switch retries.")]
    public RangeNode<int> TabRetryDelayMs { get; set; } = new(20, 0, 500);

    [Menu("Tab Change Timeout (ms)", "Max wait for a tab-index change to register.")]
    public RangeNode<int> TabChangeTimeoutMs { get; set; } = new(50, 0, 2000);

    [Menu("Fragment Tab Base Timeout (ms)", "Max wait for the fragment sub-tab to switch.")]
    public RangeNode<int> FragmentTabBaseTimeoutMs { get; set; } = new(50, 0, 2000);

    [Menu("Visible Tab Timeout (ms)", "Max wait for a stash tab to become visible after selection.")]
    public RangeNode<int> VisibleTabTimeoutMs { get; set; } = new(100, 0, 2000);
}

[Submenu(CollapsedByDefault = true)]
public class TimingTimeoutsSettings
{
    [Menu("Menagerie Travel Timeout (ms)", "Max wait for /hideout+Menagerie travel to complete.")]
    public RangeNode<int> MenagerieTravelTimeoutMs { get; set; } = new(15000, 500, 60000);

    [Menu("Map Device Open Timeout (ms)", "Max wait for the Map Device window to open.")]
    public RangeNode<int> MapDeviceOpenTimeoutMs { get; set; } = new(4000, 500, 30000);

    [Menu("Map Device Transfer Timeout (ms)", "Max wait for a single Map Device slot transfer.")]
    public RangeNode<int> MapDeviceTransferTimeoutMs { get; set; } = new(3000, 500, 30000);

    [Menu("Bestiary Release Timeout (ms)", "Max wait for a single beast release/delete.")]
    public RangeNode<int> BestiaryReleaseTimeoutMs { get; set; } = new(250, 50, 5000);

    [Menu("Map Transfer Extra Confirmation Delay (ms)", "Extra settle after a transfer before treating it as complete.")]
    public RangeNode<int> MapTransferExtraConfirmationDelayMs { get; set; } = new(10, 0, 500);

    [Menu("Map Device Inventory Lookup Retries")]
    public RangeNode<int> MapDeviceInventoryLookupRetries { get; set; } = new(4, 1, 20);

    [Menu("Map Device Inventory Lookup Retry Delay (ms)")]
    public RangeNode<int> MapDeviceInventoryLookupRetryDelayMs { get; set; } = new(60, 10, 1000);

    [Menu("Map Device Close UI Max Attempts", "How many times to spam Space while trying to close blocking UI.")]
    public RangeNode<int> MapDeviceCloseUiMaxAttempts { get; set; } = new(3, 1, 10);

    [Menu("Map Stash Discovery Retry Delay (ms)", "Delay between dynamic map-stash element discovery attempts.")]
    public RangeNode<int> MapStashDiscoveryRetryDelayMs { get; set; } = new(15, 1, 500);

    [Menu("Stash Interaction Distance (grid units)", "Max grid-space distance from the stash to consider it interactable.")]
    public RangeNode<int> StashInteractionDistance { get; set; } = new(100, 10, 500);

    [Menu("Atlas Max Scroll Attempts", "Zoom-normalization attempts before giving up.")]
    public RangeNode<int> AtlasMaxScrollAttempts { get; set; } = new(18, 1, 50);

    [Menu("Atlas Max Center Attempts", "Panning attempts before giving up on Atlas centering.")]
    public RangeNode<int> AtlasMaxCenterAttempts { get; set; } = new(14, 1, 50);
}

[Submenu(CollapsedByDefault = true)]
public class AutomationStatusOverlaySettings
{
    [Menu("Show", "Show/hide the automation status banner.")]
    public ToggleNode Show { get; set; } = new(true);

    [Menu("Show Preview While Idle", "Show a sample banner even with no active automation, to help position + style.")]
    public ToggleNode ShowPreviewWhileIdle { get; set; } = new(false);

    [Menu("X Position (%)")]
    public RangeNode<float> XPos { get; set; } = new(50, 0, 100);

    [Menu("Y Position (%)")]
    public RangeNode<float> YPos { get; set; } = new(4, 0, 100);

    [Menu("Max Width (%)", "Widest the status box may get, as a share of the game window. Long errors wrap onto more lines instead of running off both sides of the screen.")]
    public RangeNode<int> MaxWidthPercent { get; set; } = new(40, 15, 100);

    [Menu("Status Duration (seconds)", "How long a success/info message stays after the step finishes.")]
    public RangeNode<int> StatusDurationSeconds { get; set; } = new(2, 1, 30);

    [Menu("Error Duration (seconds)", "How long an error message stays visible.")]
    public RangeNode<int> ErrorDurationSeconds { get; set; } = new(10, 1, 60);

    [Menu("Padding")]
    public RangeNode<float> Padding { get; set; } = new(8, 0, 50);

    [Menu("Border Thickness")]
    public RangeNode<int> BorderThickness { get; set; } = new(1, 1, 10);

    [Menu("Border Rounding")]
    public RangeNode<float> BorderRounding { get; set; } = new(4, 0, 25);

    [Menu("Text Scale")]
    public RangeNode<float> TextScale { get; set; } = new(1.2f, 0.5f, 4f);

    [Menu("Text Color")]
    public ColorNode TextColor { get; set; } = new(new Color(220, 220, 220, 255));

    [Menu("Error Text Color")]
    public ColorNode ErrorTextColor { get; set; } = new(new Color(255, 100, 100, 255));

    [Menu("Border Color")]
    public ColorNode BorderColor { get; set; } = new(new Color(90, 90, 90, 255));

    [Menu("Error Border Color")]
    public ColorNode ErrorBorderColor { get; set; } = new(new Color(200, 80, 80, 255));

    [Menu("Background Color")]
    public ColorNode BackgroundColor { get; set; } = new(new Color(0, 0, 0, 200));
}

[Submenu(CollapsedByDefault = true)]
public class ExplorationRouteSettings
{
    [Menu("Enable Exploration Route", "Master toggle. When off, route generation + all overlays stay inactive.")]
    public ToggleNode Enabled { get; set; } = new(false);

    [Menu("Show Route On Large Map", "Draw the route as connected lines + waypoint dots.")]
    public ToggleNode ShowExplorationRoute { get; set; } = new(true);

    [Menu("Show Coverage Circles", "Draw a circle at each unvisited waypoint showing the covered detection area.")]
    public ToggleNode ShowCoverageOnMiniMap { get; set; } = new(true);

    [Menu("Detection Radius (grid units)", "Beast detection radius per waypoint. Larger = fewer waypoints with wider coverage.")]
    public RangeNode<int> DetectionRadius { get; set; } = new(189, 20, 500);

    [Menu("Waypoint Visit Radius (grid units)", "How close you must walk to a waypoint before it's marked visited.")]
    public RangeNode<int> WaypointVisitRadius { get; set; } = new(40, 5, 200);

    [Menu("Follow Map Outline First", "Walk the outer edge of the map first, then fill the interior. Click Recalculate after toggling.")]
    public ToggleNode FollowMapOutlineFirst { get; set; } = new(false);

    [Menu("Perimeter-First Route", "Only used when Follow Map Outline First is off. Walk waypoints in shells from outer to inner. When off, uses nearest-neighbor.")]
    public ToggleNode PreferPerimeterFirstRoute { get; set; } = new(true);

    [Menu("Visit Outer Shell Last", "Only used with Perimeter-First. Reverses shell order so interior is done first.")]
    public ToggleNode VisitOuterShellLast { get; set; } = new(false);

    [Menu("Recalculate Route", "Regenerate the route from your current position using the current settings.")]
    public ButtonNode Recalculate { get; set; } = new();

    [Menu("Show Path To Next Waypoint", "Draw a pathfinding line from the player to the next unvisited waypoint.")]
    public ToggleNode ShowPathsToBeasts { get; set; } = new(true);

    [Menu("Excluded Entity Paths (raw)", "Game entity metadata paths to avoid. Waypoints near matching entities are removed. One per line, or separated by ; or ,.")]
    public TextNode ExcludedEntityPaths { get; set; } = new("Metadata/Terrain/Mountain/DriedLake/Features/tent_SmallOld_v02_01.tdt");

    [Menu("Excluded Entity Paths (list editor)", "Friendlier list-editor for the raw text above. Changes sync automatically.")]
    [JsonIgnore]
    public CustomNode ExcludedEntityPathsList { get; set; } = new();

    [Menu("Entity Exclusion Radius (grid units)", "How far around each excluded entity to remove waypoints.")]
    public RangeNode<int> EntityExclusionRadius { get; set; } = new(300, 50, 1200);

    [Menu("Show Exclusion Zones On Map", "Draw circles on the large map for each matched excluded entity's no-go zone.")]
    public ToggleNode ShowEntityExclusionZones { get; set; } = new(true);

    [Menu("Exclusion Zone Color")]
    public ColorNode ExclusionZoneColor { get; set; } = new(new Color(220, 50, 50, 140));

    [Menu("Route Style", "Line colors, waypoint dot sizes, coverage-circle styling.")]
    public ExplorationRouteStyleSettings Style { get; set; } = new();

    [Menu("Debug Overlays", "Walkable-grid + wall-distance visualizations for tuning the algorithm.")]
    public ExplorationRouteDebugSettings Debug { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class ExplorationRouteStyleSettings
{
    [Menu("Route Line Color")]
    public ColorNode RouteLineColor { get; set; } = new(new Color(51, 204, 255, 178));

    [Menu("Visited Route Line Color")]
    public ColorNode VisitedLineColor { get; set; } = new(new Color(127, 127, 127, 64));

    [Menu("Waypoint Color")]
    public ColorNode WaypointColor { get; set; } = new(new Color(51, 204, 255, 178));

    [Menu("Next Waypoint Color")]
    public ColorNode NextWaypointColor { get; set; } = new(new Color(255, 255, 0, 255));

    [Menu("Coverage Circle Color")]
    public ColorNode CoverageColor { get; set; } = new(new Color(255, 255, 51, 46));

    [Menu("Detection Radius Color")]
    public ColorNode DetectionRadiusColor { get; set; } = new(new Color(255, 255, 51, 115));

    [Menu("Route Line Thickness")]
    public RangeNode<float> RouteLineThickness { get; set; } = new(1.5f, 0.5f, 5f);

    [Menu("Coverage Line Thickness")]
    public RangeNode<float> CoverageLineThickness { get; set; } = new(1f, 0.5f, 5f);

    [Menu("Detection Radius Thickness")]
    public RangeNode<float> DetectionRadiusThickness { get; set; } = new(1.5f, 0.5f, 5f);

    [Menu("Waypoint Dot Radius")]
    public RangeNode<float> WaypointDotRadius { get; set; } = new(2f, 1f, 10f);

    [Menu("Next Waypoint Dot Radius")]
    public RangeNode<float> NextWaypointDotRadius { get; set; } = new(5f, 2f, 15f);
}

[Submenu(CollapsedByDefault = true)]
public class ExplorationRouteDebugSettings
{
    [Menu("Show Walkable Cells")]
    public ToggleNode ShowWalkableCells { get; set; } = new(false);

    [Menu("Show Obstacle Cells")]
    public ToggleNode ShowObstacleCells { get; set; } = new(false);

    [Menu("Show Distance Field", "Color-code walkable cells by distance from the nearest wall.")]
    public ToggleNode ShowDistanceField { get; set; } = new(false);

    [Menu("Debug Render Radius (grid units)")]
    public RangeNode<int> DebugCellRadius { get; set; } = new(200, 50, 600);

    [Menu("Debug Cell Sample Step", "Draw every Nth cell. Higher = fewer dots, better perf.")]
    public RangeNode<int> DebugCellSampleStep { get; set; } = new(2, 1, 8);

    [Menu("Walkable Cell Color")]
    public ColorNode WalkableColor { get; set; } = new(new Color(0, 220, 0, 80));

    [Menu("Obstacle Cell Color")]
    public ColorNode ObstacleColor { get; set; } = new(new Color(220, 50, 50, 100));

    [Menu("Debug Cell Dot Radius")]
    public RangeNode<float> DebugDotRadius { get; set; } = new(1.5f, 0.5f, 5f);
}

[Submenu(CollapsedByDefault = true)]
public class AnalyticsSettings
{
    [Menu("Enable Analytics", "Master toggle for map-record persistence, autosave, and the analytics overlay. Disable to stop all analytics work.")]
    public ToggleNode Enable { get; set; } = new(true);

    [Menu("Reset Session", "Wipe all session counters, timers, and map history. Hold Shift and click to confirm.")]
    public ButtonNode ResetSession { get; set; } = new();

    [Menu("Reset Map Average", "Reset only the completed-map count and total duration. Hold Shift and click to confirm.")]
    public ButtonNode ResetMapAverage { get; set; } = new();

    [Menu("Save Session Snapshot", "Save the current live session state to a named JSON file in config/BeastsV3Sessions/.")]
    public ButtonNode SaveSessionSnapshot { get; set; } = new();

    [Menu("Complete Current Map",
        "Bank the last map's progress right now, for runs that never trigger normal completion " +
        "(e.g. gathering spawn-rate data without killing anything). Works whether you're still " +
        "standing in the map or already back in hideout/town. The map is marked done immediately - " +
        "world labels, the tracked-beasts window and map markers hide, same as walking back into an " +
        "already-banked map.")]
    public ButtonNode CompleteCurrentMap { get; set; } = new();

    [Menu("Extra Cost Per Map (chaos)", "Flat chaos value added to every map cost breakdown as an 'Extra (Manual)' line.")]
    public RangeNode<int> ExtraCostPerMapChaos { get; set; } = new(0, 0, 500);

    [Menu("Map Device Capture Poll (ms)", "How often to re-read the Map Device window while it's open. Lower = faster capture, higher = less CPU.")]
    public RangeNode<int> MapDeviceCapturePollIntervalMs { get; set; } = new(250, 50, 2000);

    [Menu("Overlay", "Style of the in-game analytics text overlay.")]
    public AnalyticsOverlaySettings Overlay { get; set; } = new();

    [Menu("Web Dashboard", "Local HTTP server serving the analytics dashboard at http://localhost:{port}/.")]
    public WebDashboardSettings Web { get; set; } = new();

    [Menu("Community Data Sharing", "Community spawn-rate data. Off by default; nothing is sent unless you turn this on.")]
    public TelemetrySettings Telemetry { get; set; } = new();
}

// Opt-in anonymous submissions feeding the community beast calculator.
[Submenu(CollapsedByDefault = true)]
public class TelemetrySettings
{
    [Menu("Enable Sharing",
        "OFF BY DEFAULT. When on, each completed map sends: league, map tier, area name, duration, " +
        "beast counts, which beasts were captured, which scarabs were used, and your allocated atlas " +
        "passives. It never sends your account name, character name, prices, session names or file paths. " +
        "Use Preview Submission to read the exact bytes before enabling.")]
    public ToggleNode ShareAnonymousData { get; set; } = new(false);

    [Menu("Show In-Game Banner",
        "Show a small on-screen banner under the analytics overlay while sharing is active. " +
        "Sharing itself is unaffected - this only hides the on-screen reminder.")]
    public ToggleNode ShowActiveBanner { get; set; } = new(true);

    [Menu("Upload Interval (minutes)",
        "How often queued maps are sent automatically. Higher means fewer network calls but " +
        "longer before a map shows up server-side. Use Upload Now to send immediately regardless.")]
    public RangeNode<int> UploadIntervalMinutes { get; set; } = new(15, 1, 60);

    [Menu("Sharing Status", "Warning shown while anonymous map data is being sent.")]
    [JsonIgnore]
    public CustomNode StatusBanner { get; set; } = new();

    [Menu("Preview Submission",
        "Write the exact JSON that would be uploaded to config/BeastsV3TelemetryPreview.json and open it. " +
        "Works whether or not sharing is enabled.")]
    public ButtonNode PreviewSubmission { get; set; } = new();

    [Menu("Upload Now", "Send everything currently queued immediately, without waiting for the interval above.")]
    public ButtonNode UploadNow { get; set; } = new();

    [Menu("Reset Install ID",
        "Generate a new random id, breaking any link to data already sent. The id is random, stored " +
        "locally, and never derived from your account, character or machine.")]
    public ButtonNode ResetInstallId { get; set; } = new();

    [Menu("Show Tree Cohort Banner",
        "Show which data cohort your current atlas tree contributes to. Reference tree = baseline spawn " +
        "rates; other trees still contribute beast counts and profit data.")]
    public ToggleNode ShowCohortBanner { get; set; } = new(true);
}

[Submenu(CollapsedByDefault = true)]
public class WebDashboardSettings
{
    [Menu("Enable Web Dashboard", "Start a local HTTP server hosting the analytics dashboard. Restart on toggle.")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Port", "TCP port the dashboard listens on. Localhost + 127.0.0.1 always accepted.")]
    public RangeNode<int> Port { get; set; } = new(18422, 1024, 65535);

    [Menu("Allow Network Access", "Also bind to 0.0.0.0 so other devices on your LAN can reach the dashboard. Requires elevated permissions on Windows.")]
    public ToggleNode AllowNetworkAccess { get; set; } = new(false);

    [Menu("Snapshot Refresh (ms)", "Minimum interval between live-snapshot rebuilds. Lower = fresher dashboard, higher = less CPU.")]
    public RangeNode<int> SnapshotRefreshMs { get; set; } = new(1000, 100, 10000);

    [Menu("Rolling Stats Window (maps)", "Number of recent completed maps used for rolling averages / percentiles shown on the dashboard.")]
    public RangeNode<int> RollingStatsWindowMaps { get; set; } = new(10, 1, 100);

    [Menu("Copy Dashboard URL", "Copy the local dashboard URL to your clipboard.")]
    public ButtonNode CopyUrl { get; set; } = new();

    [Menu("Open Dashboard In Browser", "Open the dashboard URL in your default browser.")]
    public ButtonNode OpenInBrowser { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class AnalyticsOverlaySettings
{
    [Menu("Show")]
    public ToggleNode Show { get; set; } = new(true);

    [Menu("X Position (%)")]
    public RangeNode<float> XPos { get; set; } = new(50, 0, 100);

    [Menu("Y Position (%)")]
    public RangeNode<float> YPos { get; set; } = new(25, 0, 100);

    [Menu("Padding")]
    public RangeNode<float> Padding { get; set; } = new(8, 0, 50);

    [Menu("Border Thickness")]
    public RangeNode<int> BorderThickness { get; set; } = new(1, 1, 10);

    [Menu("Border Rounding")]
    public RangeNode<float> BorderRounding { get; set; } = new(0, 0, 25);

    [Menu("Text Scale")]
    public RangeNode<float> TextScale { get; set; } = new(1f, 0.5f, 4f);

    [Menu("Text Color")]
    public ColorNode TextColor { get; set; } = new(new Color(220, 220, 220, 255));

    [Menu("Border Color")]
    public ColorNode BorderColor { get; set; } = new(new Color(90, 90, 90, 255));

    [Menu("Background Color")]
    public ColorNode BackgroundColor { get; set; } = new(new Color(0, 0, 0, 180));
}

[Submenu(CollapsedByDefault = true)]
public class VisibilitySettings
{
    [Menu("Hide In Hideout / Town", "Hide overlays in hideout, town, or Menagerie.")]
    public ToggleNode HideInHideout { get; set; } = new(true);

    [Menu("Hide On Fullscreen Panels", "Hide overlays when Atlas or Passive Tree is open.")]
    public ToggleNode HideOnFullscreenPanels { get; set; } = new(true);

    [Menu("Hide On Left Panel Open", "Hide overlays when a left-side panel (Bestiary, Challenges) is open.")]
    public ToggleNode HideOnLeftPanelOpen { get; set; } = new(true);

    [Menu("Hide On Right Panel Open", "Hide overlays when a right-side panel (Inventory, Stash) is open.")]
    public ToggleNode HideOnRightPanelOpen { get; set; } = new(true);
}
