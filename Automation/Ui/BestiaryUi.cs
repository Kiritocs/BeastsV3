using System;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using SharpDX;

namespace BeastsV3.Automation.Ui;

// Adapter over the Bestiary panel's Captured Beasts tab: readiness, row and viewport
// reads, release buttons, the filter field and the tab-selection buttons.
// A row's IsVisible means "passes the active filter", not "on screen".
public sealed class BestiaryUi
{
    // Children of CapturedBeastsTab: footer, viewport, scrollbar, loading overlay.
    private const int FooterChildIndex = 0;
    private const int ViewportChildIndex = 1;
    private const int LoadingOverlayChildIndex = 3;

    // The viewport's single child: the tall element holding the whole grid, which slides
    // up as the list scrolls.
    private const int ContentChildIndex = 0;

    // Path to the beast filter's text element, relative to CapturedBeastsTab.
    private static readonly int[] FilterTextPath = { 0, 0, 1, 0 };

    // Path to the Challenges category bar container; its children reorder.
    private static readonly int[] ChallengesEntriesRootPath = { 2, 0, 1, 0 };

    // Path to a category button's label text, relative to the button. Stable across the game
    // moving, reordering or hiding category entries.
    private static readonly int[] CategoryLabelPath = { 0, 1 };

    // Fraction of a row that must lie inside the viewport to be clickable or drawable.
    private const float MinClickOverlap = 0.9f;
    private const float MinDrawOverlap = 0.6f;

    // Path to a row tooltip's name line.
    private static readonly int[] BeastNamePath = { 1, 0 };

    private readonly GameController _game;

    public BestiaryUi(GameController game)
    {
        _game = game;
    }

    // ---- panels --------------------------------------------------------

    public Element ChallengesPanel =>
        _game?.IngameState?.IngameUi?.ChallengesPanel;

    public bool IsChallengesPanelVisible => ChallengesPanel?.IsVisible == true;

    public BestiaryTab BestiaryTab
    {
        get
        {
            try { return _game?.IngameState?.IngameUi?.ChallengesPanel?.TabContainer?.BestiaryTab; }
            catch { return null; }
        }
    }

    public CapturedBeastsTab CapturedBeastsTab
    {
        get
        {
            try { return BestiaryTab?.CapturedBeastsTab; }
            catch { return null; }
        }
    }

    public bool IsBestiaryTabOpen => IsChallengesPanelVisible && BestiaryTab?.IsVisible == true;

    // Cheap check that the tab looks open; acting on it requires IsCapturedBeastsTabReady.
    public bool IsCapturedBeastsTabOpen
    {
        get
        {
            if (!IsBestiaryTabOpen) return false;
            var tab = CapturedBeastsTab;
            return tab != null && tab.ChildCount > 0;
        }
    }

    // True when the Captured Beasts sub-tab is showing, proven by reading its rows. Throws
    // internally in the negative case, so keep it off per-frame paths.
    public bool IsCapturedBeastsTabReady
    {
        get
        {
            if (!IsBestiaryTabOpen) return false;
            var tab = CapturedBeastsTab;
            if (tab == null || tab.ChildCount == 0) return false;
            try { return tab.CapturedBeasts != null; }
            catch { return false; }
        }
    }

    // Same answer as IsCapturedBeastsTabReady, throttled for per-frame use.
    public bool IsCapturedBeastsTabReadyCached
    {
        get
        {
            if (!IsBestiaryTabOpen)
            {
                _readyCached = false;
                _readyCheckedUtc = DateTime.MinValue;
                return false;
            }

            var now = DateTime.UtcNow;
            if (now - _readyCheckedUtc < ReadyCacheTtl) return _readyCached;

            _readyCheckedUtc = now;
            _readyCached = IsCapturedBeastsTabReady;
            return _readyCached;
        }
    }

    private static readonly TimeSpan ReadyCacheTtl = TimeSpan.FromMilliseconds(250);
    private DateTime _readyCheckedUtc = DateTime.MinValue;
    private bool _readyCached;

    // True while the panel is still populating rows.
    public bool IsLoading =>
        CapturedBeastsTab?.GetChildAtIndex(LoadingOverlayChildIndex)?.IsVisible == true;

    // ---- viewport / rows -----------------------------------------------

    // The scroll clip region holding the beast grid, found by walking up from a row and
    // falling back to a fixed child index when the grid is empty.
    public Element ViewportElement
    {
        get
        {
            if (!IsCapturedBeastsTabOpen) return null;

            var tab = CapturedBeastsTab;
            if (tab == null) return null;

            foreach (var beast in SafeCapturedBeasts(tab))
            {
                for (var cur = beast?.Parent; cur != null; cur = cur.Parent)
                {
                    if (cur.Parent?.Address == tab.Address) return cur;
                }
                break;
            }

            var candidate = tab.GetChildAtIndex(ViewportChildIndex);
            var rect = candidate?.GetClientRect() ?? default;
            return rect.Width > 100 && rect.Height > 100 ? candidate : null;
        }
    }

    // Reads the tab's row list, returning empty instead of throwing when unpopulated.
    private static List<CapturedBeast> SafeCapturedBeasts(CapturedBeastsTab tab)
    {
        if (tab == null || tab.ChildCount == 0) return new List<CapturedBeast>();
        try { return tab.CapturedBeasts ?? new List<CapturedBeast>(); }
        catch (Exception ex)
        {
            Log.Debug($"CapturedBeasts read failed: {ex.GetType().Name}");
            return new List<CapturedBeast>();
        }
    }

    public RectangleF ViewportRect => ViewportElement?.GetClientRect() ?? default;

    public float ScrollOffsetPixels
    {
        get
        {
            var viewport = CapturedBeastsTab?.GetChildAtIndex(ViewportChildIndex);
            var viewportRect = viewport?.GetClientRect() ?? default;
            if (viewportRect.Width <= 100 || viewportRect.Height <= 100) return -1f;

            var contentRect = viewport.GetChildAtIndex(ContentChildIndex)?.GetClientRect() ?? default;
            if (contentRect.Height <= viewportRect.Height) return -1f;

            return viewportRect.Top - contentRect.Top;
        }
    }

    // Every captured beast passing the current filter, on screen or not.
    public List<CapturedBeast> MatchingBeasts()
    {
        // Stale children survive navigating away from the tab.
        if (!IsCapturedBeastsTabOpen) return new List<CapturedBeast>();

        var beasts = SafeCapturedBeasts(CapturedBeastsTab);

        var result = new List<CapturedBeast>(beasts.Count);
        foreach (var beast in beasts)
        {
            // IsVisible means the row passes the active filter.
            if (beast?.IsVisible == true) result.Add(beast);
        }
        return result;
    }

    public int MatchingCount() => MatchingBeasts().Count;

    // Matching rows that also satisfy `eligible`; null counts every match.
    public int MatchingCount(Func<CapturedBeast, bool> eligible)
    {
        if (eligible == null) return MatchingCount();
        if (!IsCapturedBeastsTabOpen) return 0;

        var count = 0;
        foreach (var beast in SafeCapturedBeasts(CapturedBeastsTab))
        {
            if (beast?.IsVisible != true) continue;
            if (!SafeEligible(eligible, beast)) continue;
            count++;
        }
        return count;
    }

    // A row that throws while being classified is skipped rather than costing an inventory slot.
    private static bool SafeEligible(Func<CapturedBeast, bool> eligible, CapturedBeast beast)
    {
        try { return eligible(beast); }
        catch (Exception ex)
        {
            Log.Debug($"Beast eligibility check failed: {ex.GetType().Name}");
            return false;
        }
    }

    // Row count regardless of filter.
    public int TotalBeastCount() =>
        IsCapturedBeastsTabOpen ? SafeCapturedBeasts(CapturedBeastsTab).Count : 0;

    // A row and the rect it occupied when read.
    public readonly record struct PositionedBeast(CapturedBeast Beast, RectangleF Rect);

    // Filter matches almost fully inside the viewport, in reading order.
    public List<CapturedBeast> ClickableBeasts()
    {
        var positioned = BeastsInViewport(MinClickOverlap, sort: true);
        var result = new List<CapturedBeast>(positioned.Count);
        foreach (var entry in positioned) result.Add(entry.Beast);
        return result;
    }

    // Clickable rows and the total matching count from one pass. The itemize loop needs both
    // every batch, and asking separately walked 850+ rows out of process memory twice.
    public List<CapturedBeast> ClickableBeasts(out int matchingCount) =>
        ClickableBeasts(out matchingCount, out _);

    // Also hands back the viewport rect it resolved.
    public List<CapturedBeast> ClickableBeasts(out int matchingCount, out RectangleF viewportRect) =>
        ClickableBeasts(null, out matchingCount, out _, out viewportRect);

    // The eligibility-aware form, used by regex itemize. `eligible` narrows the pass, and
    // `matchingCount` then counts only eligible rows - the loop treats it as "how many are
    // left", so it has to be able to reach zero. `blockedInViewport` is the ineligible-but-
    // matching rows occupying the viewport: they never get clicked and wall off the rows
    // below, so the count tells the caller to scroll instead of declaring a stall.
    public List<CapturedBeast> ClickableBeasts(Func<CapturedBeast, bool> eligible,
        out int matchingCount, out int blockedInViewport, out RectangleF viewportRect)
    {
        matchingCount = 0;
        blockedInViewport = 0;
        viewportRect = default;

        var result = new List<CapturedBeast>();
        if (!IsCapturedBeastsTabOpen) return result;

        var beasts = SafeCapturedBeasts(CapturedBeastsTab);
        if (beasts.Count == 0) return result;

        var viewport = ResolveViewportRect(beasts);
        viewportRect = viewport;
        var viewportUsable = viewport.Width > 0 && viewport.Height > 0;

        var positioned = new List<PositionedBeast>();
        foreach (var beast in beasts)
        {
            // IsVisible means the row passes the active filter.
            if (beast?.IsVisible != true) continue;

            // The rect is still read for rejected rows below.
            var wanted = eligible == null || SafeEligible(eligible, beast);

            // Counted whether or not it is on screen: this is "how many are left".
            if (wanted) matchingCount++;

            if (!viewportUsable) continue;

            var rect = beast.GetClientRect();
            if (rect.Width < 16 || rect.Height < 16) continue;
            if (!ImGuiEx.IsRectMostlyInside(rect, viewport, MinClickOverlap)) continue;

            if (!wanted)
            {
                blockedInViewport++;
                continue;
            }

            positioned.Add(new PositionedBeast(beast, rect));
        }

        // Reading order, using the rects already read.
        positioned.Sort((a, b) =>
        {
            var rowCompare = ((int)(a.Rect.Top / 8)).CompareTo((int)(b.Rect.Top / 8));
            return rowCompare != 0 ? rowCompare : a.Rect.Left.CompareTo(b.Rect.Left);
        });

        foreach (var entry in positioned) result.Add(entry.Beast);
        return result;
    }

    // ---- price-overlay row tracking (render path only) -------------------
    // A full scan establishes a candidate band of rows near the viewport; later frames re-read
    // only those rects and rescan when the band stops covering the viewport.

    // Maximum time between full rescans.
    private static readonly TimeSpan OverlayRescanInterval = TimeSpan.FromSeconds(1);

    // How long a read row list is reused for.
    private static readonly TimeSpan OverlayRowListTtl = TimeSpan.FromMilliseconds(500);

    // Minimum gap between coverage-triggered rescans.
    private static readonly TimeSpan OverlayCoverageRescanFloor = TimeSpan.FromMilliseconds(150);

    // Viewport heights of candidate rows kept above and below the visible region.
    private const float OverlayBandViewports = 2f;

    private readonly List<CapturedBeast> _overlayCandidates = new();
    private readonly List<PositionedBeast> _overlayRows = new();
    private List<CapturedBeast> _overlayRowList;
    private Element _overlayViewport;
    private DateTime _overlayScannedUtc = DateTime.MinValue;
    private DateTime _overlayRowListUtc = DateTime.MinValue;
    private bool _overlayCandidatesCoverAll;

    // Rows the price overlay draws this frame. The list is reused until the next call.
    public IReadOnlyList<PositionedBeast> OverlayRows()
    {
        _overlayRows.Clear();

        // Cheap gate before anything that can throw.
        if (!IsCapturedBeastsTabOpen)
        {
            ResetOverlayState();
            return _overlayRows;
        }

        var now = DateTime.UtcNow;

        // Doubles as the readiness check.
        if (!TryGetOverlayRowList(now, out var rows))
        {
            ResetOverlayState();
            return _overlayRows;
        }

        var viewport = OverlayViewportRect(rows);
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            ResetOverlayState();
            return _overlayRows;
        }

        if (_overlayScannedUtc == DateTime.MinValue || now - _overlayScannedUtc >= OverlayRescanInterval)
        {
            RescanOverlayCandidates(rows, viewport, now);
            CollectFromCandidates(viewport);
            return _overlayRows;
        }

        // At most one rescan per frame when the band stops covering the viewport.
        if (!CollectFromCandidates(viewport) &&
            now - _overlayScannedUtc >= OverlayCoverageRescanFloor)
        {
            RescanOverlayCandidates(rows, viewport, now);
            CollectFromCandidates(viewport);
        }

        return _overlayRows;
    }

    // Returns the row list, cached for OverlayRowListTtl; false when the sub-tab is not showing.
    private bool TryGetOverlayRowList(DateTime nowUtc, out List<CapturedBeast> rows)
    {
        if (_overlayRowList != null && nowUtc - _overlayRowListUtc < OverlayRowListTtl)
        {
            rows = _overlayRowList;
            return true;
        }

        rows = null;
        var tab = CapturedBeastsTab;
        if (tab == null || tab.ChildCount == 0)
        {
            _overlayRowList = null;
            return false;
        }

        try
        {
            rows = tab.CapturedBeasts;
        }
        catch (Exception ex)
        {
            Log.Debug($"CapturedBeasts read failed: {ex.GetType().Name}");
            _overlayRowList = null;
            return false;
        }

        if (rows == null)
        {
            _overlayRowList = null;
            return false;
        }

        _overlayRowList = rows;
        _overlayRowListUtc = nowUtc;
        return true;
    }

    // Fills _overlayRows from the candidate band; false when the band no longer brackets
    // the viewport.
    private bool CollectFromCandidates(RectangleF viewport)
    {
        _overlayRows.Clear();

        var minTop = float.MaxValue;
        var maxBottom = float.MinValue;
        var sawAny = false;

        try
        {
            foreach (var beast in _overlayCandidates)
            {
                if (beast?.IsVisible != true) continue;

                var rect = beast.GetClientRect();
                if (rect.Width < 16 || rect.Height < 16) continue;

                sawAny = true;
                if (rect.Top < minTop) minTop = rect.Top;
                if (rect.Bottom > maxBottom) maxBottom = rect.Bottom;

                if (ImGuiEx.IsRectMostlyInside(rect, viewport, MinDrawOverlap))
                    _overlayRows.Add(new PositionedBeast(beast, rect));
            }
        }
        catch (Exception ex)
        {
            // A stale candidate is treated as lost coverage so the caller rebuilds.
            Log.Debug($"Bestiary overlay candidate read failed: {ex.GetType().Name}");
            _overlayRows.Clear();
            return false;
        }

        // With no band, coverage cannot be lost.
        if (_overlayCandidatesCoverAll) return true;
        if (!sawAny) return false;

        return minTop <= viewport.Top && maxBottom >= viewport.Bottom;
    }

    // Rebuilds the candidate band with a full pass over the row list.
    private void RescanOverlayCandidates(List<CapturedBeast> rows, RectangleF viewport, DateTime nowUtc)
    {
        _overlayScannedUtc = nowUtc;
        _overlayCandidates.Clear();
        _overlayCandidatesCoverAll = true;

        if (rows == null || rows.Count == 0) return;

        var margin = viewport.Height * OverlayBandViewports;
        var bandTop = viewport.Top - margin;
        var bandBottom = viewport.Bottom + margin;

        try
        {
            foreach (var beast in rows)
            {
                // IsVisible means the row passes the active filter; cheaper than the rect.
                if (beast?.IsVisible != true) continue;

                var rect = beast.GetClientRect();
                if (rect.Width < 16 || rect.Height < 16) continue;

                if (rect.Bottom < bandTop || rect.Top > bandBottom)
                {
                    _overlayCandidatesCoverAll = false;
                    continue;
                }

                _overlayCandidates.Add(beast);
            }
        }
        catch (Exception ex)
        {
            // Cached rows went stale; reset and re-read next frame.
            Log.Debug($"Bestiary overlay rescan failed: {ex.GetType().Name}");
            ResetOverlayState();
        }
    }

    // Returns the viewport rect, deriving the element from the caller's row list once.
    private RectangleF OverlayViewportRect(List<CapturedBeast> rows)
    {
        if (_overlayViewport != null)
        {
            var cached = _overlayViewport.GetClientRect();
            if (cached.Width > 100 && cached.Height > 100) return cached;
            _overlayViewport = null;
        }

        var tab = CapturedBeastsTab;
        if (tab == null) return default;

        // The clip region is the row's ancestor that is a direct child of the tab.
        if (rows != null)
        {
            foreach (var beast in rows)
            {
                for (var cur = beast?.Parent; cur != null; cur = cur.Parent)
                {
                    if (cur.Parent?.Address != tab.Address) continue;
                    _overlayViewport = cur;
                    return cur.GetClientRect();
                }
                break;
            }
        }

        var candidate = tab.GetChildAtIndex(ViewportChildIndex);
        var rect = candidate?.GetClientRect() ?? default;
        if (rect.Width <= 100 || rect.Height <= 100) return default;

        _overlayViewport = candidate;
        return rect;
    }

    private void ResetOverlayState()
    {
        _overlayCandidates.Clear();
        _overlayRowList = null;
        _overlayViewport = null;
        _overlayScannedUtc = DateTime.MinValue;
        _overlayRowListUtc = DateTime.MinValue;
        _overlayCandidatesCoverAll = false;
    }

    // Visible filter matches with the rect each was read at, unsorted.
    public List<PositionedBeast> OnScreenBeasts() => BeastsInViewport(MinDrawOverlap, sort: false);

    private List<PositionedBeast> BeastsInViewport(float minOverlap, bool sort)
    {
        var result = new List<PositionedBeast>();

        // Stale children survive navigating away from the tab.
        if (!IsCapturedBeastsTabOpen) return result;

        // Read once and used for both the viewport derivation and the row scan.
        var beasts = SafeCapturedBeasts(CapturedBeastsTab);
        if (beasts.Count == 0) return result;

        var viewport = ResolveViewportRect(beasts);
        if (viewport.Width <= 0 || viewport.Height <= 0) return result;

        foreach (var beast in beasts)
        {
            // IsVisible means the row passes the active filter.
            if (beast?.IsVisible != true) continue;

            var rect = beast.GetClientRect();
            if (rect.Width < 16 || rect.Height < 16) continue;
            if (!ImGuiEx.IsRectMostlyInside(rect, viewport, minOverlap)) continue;

            result.Add(new PositionedBeast(beast, rect));
        }

        // Sorts into reading order using the rects already read.
        if (sort)
        {
            result = result
                .OrderBy(x => (int)(x.Rect.Top / 8))
                .ThenBy(x => x.Rect.Left)
                .ToList();
        }

        return result;
    }

    // Derives the viewport rect from a row list the caller has already read.
    private RectangleF ResolveViewportRect(List<CapturedBeast> beasts)
    {
        var tab = CapturedBeastsTab;
        if (tab == null) return default;

        foreach (var beast in beasts)
        {
            for (var cur = beast?.Parent; cur != null; cur = cur.Parent)
            {
                if (cur.Parent?.Address == tab.Address) return cur.GetClientRect();
            }
            break;
        }

        var candidate = tab.GetChildAtIndex(ViewportChildIndex);
        var rect = candidate?.GetClientRect() ?? default;
        return rect.Width > 100 && rect.Height > 100 ? rect : default;
    }

    // A row's beast name, read from its tooltip with wrapping hyphens stripped.
    public static string BeastDisplayName(CapturedBeast beast)
    {
        try
        {
            var text = ImGuiEx.GetChildAt(beast?.Tooltip, BeastNamePath)?.Text;
            return text?.Replace("-", string.Empty).Trim();
        }
        catch
        {
            return null;
        }
    }

    // ---- release (delete) buttons --------------------------------------

    // A row's release button; it reports IsVisible false until hovered.
    public Element TryGetReleaseButton(CapturedBeast beast) => beast?.ReleaseButton;

    // True when the cursor is over the release button.
    public bool IsHoveringReleaseButton(Element releaseButton)
    {
        if (releaseButton == null) return false;
        var rect = releaseButton.GetClientRect();
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        if (releaseButton.HasShinyHighlight) return true;

        var address = releaseButton.Address;
        if (address == 0) return false;

        var hover = _game?.IngameState?.UIHoverElement;
        for (var current = hover; current != null; current = current.Parent)
        {
            if (current.Address == address) return true;
        }
        return false;
    }

    // ---- destroy confirmation ------------------------------------------

    // The destroy-confirmation dialog, shown when a release is not ctrl-clicked.
    public PopUpWindow ConfirmationWindow => _game?.IngameState?.IngameUi?.PopUpWindow;

    public bool IsDestroyConfirmationVisible => ConfirmationWindow?.IsVisible == true;

    public Element TryGetDestroyConfirmationButton()
    {
        var window = ConfirmationWindow;
        if (window?.IsVisible != true) return null;
        return window.TwoButtonWindowOk;
    }

    // ---- filter input --------------------------------------------------

    // Current text in the beast filter field, via a fixed path with a footer-scan fallback.
    public string FilterText
    {
        get
        {
            var tab = CapturedBeastsTab;
            if (tab == null) return null;

            var direct = ImGuiEx.GetChildAt(tab, FilterTextPath)?.Text;
            if (!string.IsNullOrWhiteSpace(direct) && !IsFilterChrome(direct)) return direct;

            string best = null;
            CollectFilterCandidates(tab.GetChildAtIndex(FooterChildIndex), ref best, 0);
            return best ?? direct;
        }
    }

    private static void CollectFilterCandidates(Element root, ref string best, int depth)
    {
        if (root == null || depth > 4) return;
        for (var i = 0; i < root.ChildCount; i++)
        {
            var child = root.GetChildAtIndex(i);
            if (child == null) continue;

            var text = child.Text;
            if (!string.IsNullOrWhiteSpace(text) && !IsFilterChrome(text) &&
                (best == null || text.Length > best.Length))
            {
                best = text;
            }
            CollectFilterCandidates(child, ref best, depth + 1);
        }
    }

    private static bool IsFilterChrome(string text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ||
               trimmed == "+" ||
               trimmed.Equals("Filter", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Filter Beasts", StringComparison.OrdinalIgnoreCase);
    }

    // ---- tab selection buttons -----------------------------------------

    // The Bestiary entry in the category bar, picked by its label text rather than by position
    public Element TryGetBestiaryCategoryButton()
    {
        var entriesRoot = ImGuiEx.GetChildAt(ChallengesPanel, ChallengesEntriesRootPath);
        if (entriesRoot == null) return null;

        for (var i = 0; i < entriesRoot.ChildCount; i++)
        {
            var entry = entriesRoot.GetChildAtIndex(i);
            if (entry == null || entry.ChildCount == 0) continue;

            var rect = entry.GetClientRect();
            if (rect.Width <= 8 || rect.Height <= 8) continue;

            string label;
            try { label = ImGuiEx.GetChildAt(entry, CategoryLabelPath)?.Text; }
            catch { continue; }

            if (string.Equals(label?.Trim(), "Bestiary", StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        return null;
    }

    // The tall narrow sub-tab strip on the right of the Bestiary tab, found by shape.
    public Element SubTabStrip
    {
        get
        {
            var tab = BestiaryTab;
            if (tab == null) return null;

            Element best = null;
            var bestHeight = 0f;
            for (var i = 0; i < tab.ChildCount; i++)
            {
                var child = tab.GetChildAtIndex(i);
                if (child == null || child.ChildCount < 4) continue;

                var rect = child.GetClientRect();
                if (rect.Width <= 8 || rect.Height <= 8) continue;
                // The strip is much taller than it is wide.
                if (rect.Height < rect.Width * 3f) continue;

                if (rect.Height > bestHeight)
                {
                    bestHeight = rect.Height;
                    best = child;
                }
            }
            return best;
        }
    }

    // The Captured Beasts sub-tab, found by its tooltip label.
    // IsVisible is unreliable here, so callers must verify the tab opened after clicking.
    public Element TryGetCapturedBeastsButtonToClick()
    {
        var strip = SubTabStrip;
        if (strip == null) return null;

        for (var i = 0; i < strip.ChildCount; i++)
        {
            var child = strip.GetChildAtIndex(i);
            var rect = child?.GetClientRect() ?? default;
            if (rect.Width <= 8 || rect.Height <= 8) continue;

            string label;
            try { label = child.Tooltip?.Text; }
            catch { continue; }

            if (string.Equals(label?.Trim(), "Captured Beasts", StringComparison.OrdinalIgnoreCase))
                return child;
        }

        return null;
    }
}
