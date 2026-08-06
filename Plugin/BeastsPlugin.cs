using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using BeastsV3.Analytics;
using BeastsV3.Analytics.Web;
using BeastsV3.Automation;
using BeastsV3.Automation.Input;
using BeastsV3.Automation.Ui;
using BeastsV3.Automation.Workflows;
using BeastsV3.Beasts;
using BeastsV3.Plugin.Settings;
using BeastsV3.Plugin.Settings.Menu;
using BeastsV3.Prices;
using BeastsV3.Rendering;
using BeastsV3.Route;
using BeastsV3.Shared;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;

namespace BeastsV3.Plugin;

// Plugin entry point: constructs the feature objects, wires settings buttons and
// dispatches lifecycle events.
public class BeastsPlugin : BaseSettingsPlugin<BeastsSettings>
{
    private LogFile _logFile;
    private LogFilePanel _logFilePanel;

    // Kept across OnClose, since the host still draws the settings object after an unload.
    private SettingsMenu _settingsMenu;

    private BeastTracker _tracker;
    private DetectionHeartbeat _heartbeat;
    private PriceService _prices;
    private PricePanel _pricePanel;
    private TabPickers _tabPickers;
    private Counter _counter;

    private SessionStore _sessionStore;
    private CostTracker _cost;
    private SessionRecorder _recorder;
    private AreaTransitions _areaTransitions;
    private AnalyticsOverlay _analyticsOverlay;

    private WorldLabels _worldLabels;
    private MapOverlay _mapOverlay;
    private PricePanels _pricePanels;
    private ExplorationRoute _explorationRoute;
    private WebHost _webHost;

    // Automation infrastructure.
    private RuntimeState _automationState;
    private InputLock _inputLock;
    private HotkeyTracker _hotkeyTracker;
    private AutomationInput _automationInput;
    private Waits _waits;
    private UiCleanup _uiCleanup;
    private Runner _runner;
    private BeastsV3.Automation.Navigation.Navigate _navigate;
    private BeastsV3.Automation.Navigation.WorldEntity _worldEntity;
    private AutomationStatus _automationStatusOverlay;

    // UI adapters and workflows.
    private InventoryUi _inventoryUi;
    private BestiaryUi _bestiaryUi;
    private MenagerieRightClick _menagerieRightClick;
    private QuickButtons _quickButtons;
    private ClipboardAutoPaste _clipboardAutoPaste;
    private Bestiary _bestiary;
    private MerchantUi _merchantUi;
    private FaustusList _faustusList;
    private StashUi _stashUi;
    private MapStashUi _mapStashUi;
    private Restock _restock;
    private CapturedMonsterStash _capturedMonsterStash;
    private MapDeviceUi _mapDeviceUi;
    private AtlasUi _atlasUi;
    private MapDeviceLoad _mapDeviceLoad;
    private FullSequence _fullSequence;

    private bool _analyticsEnabledLastFrame;

    public BeastsPlugin()
    {
        Name = "Beasts V3";
    }

    public override bool Initialise() => true;

    public override void OnLoad()
    {
        // Opened first so every later line reaches the file.
        if (Settings.LogFile.Enabled.Value)
        {
            _logFile = new LogFile(maxBytes: Math.Max(1, Settings.LogFile.MaxSizeMb.Value) * 1024L * 1024L);
        }

        Log.Attach(
            LogMessage,
            (message, ex) => LogError(ex is null ? message : $"{message}: {ex}"),
            () => Settings.DebugLogging.Value,
            _logFile);

        // Catches exceptions from tasks nobody awaited.
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _logFilePanel = new LogFilePanel(Settings, () => _logFile);

        _prices = new PriceService(Settings, () => GameHelpers.TryGetServerLeague(GameController));
        _tracker = new BeastTracker(GameController, Settings);
        _heartbeat = new DetectionHeartbeat(GameController, Settings, _tracker);
        _pricePanel = new PricePanel(Settings, _prices);
        // Deferred so it also works before _recorder exists and after an unload.
        Func<bool> isInFinalizedMap = () => _recorder?.State.IsInFinalizedMap == true;

        _counter = new Counter(GameController, Settings, _tracker, isInFinalizedMap);

        _sessionStore = new SessionStore();
        _cost = new CostTracker(GameController, Settings, _prices);
        _recorder = new SessionRecorder(GameController, Settings, _tracker, _prices, _cost, _sessionStore);
        _areaTransitions = new AreaTransitions(_recorder.State);
        _analyticsOverlay = new AnalyticsOverlay(GameController, Settings, _recorder);

        _inventoryUi = new InventoryUi(GameController);
        _bestiaryUi = new BestiaryUi(GameController);

        _worldLabels = new WorldLabels(GameController, Settings, _tracker, _prices, isInFinalizedMap);
        _explorationRoute = new ExplorationRoute(GameController, Settings);
        _mapOverlay = new MapOverlay(GameController, Settings, _tracker, _prices, _worldLabels, _explorationRoute, isInFinalizedMap);
        _pricePanels = new PricePanels(GameController, Settings, _prices, _bestiaryUi);
        _webHost = new WebHost(Settings, _recorder, _cost, _sessionStore, _prices);

        _automationState = new RuntimeState();
        _inputLock = new InputLock(_automationState, Settings);
        _hotkeyTracker = new HotkeyTracker(_automationState);
        _automationInput = new AutomationInput(GameController, Settings, _automationState, _inputLock);
        _waits = new Waits(_automationInput);
        _uiCleanup = new UiCleanup(GameController, Settings, _automationInput);
        _runner = new Runner(_automationState, Settings, _hotkeyTracker, _inputLock, _uiCleanup, _automationInput);
        _navigate = new BeastsV3.Automation.Navigation.Navigate(GameController, Settings, _automationInput);
        _worldEntity = new BeastsV3.Automation.Navigation.WorldEntity(GameController, _automationInput, _waits, _navigate, Settings);
        _automationStatusOverlay = new AutomationStatus(GameController, Settings, _runner);

        // Ahead of MenagerieRightClick, which releases beasts from an open stash tab too.
        _stashUi = new StashUi(GameController, _automationInput, _waits, Settings, _worldEntity);

        _menagerieRightClick = new MenagerieRightClick(
            _runner, _automationInput, _waits, Settings, _inventoryUi, _stashUi, _bestiaryUi,
            isInMenagerie: () => string.Equals(
                GameController?.Area?.CurrentArea?.Name,
                GameHelpers.MenagerieAreaName, System.StringComparison.OrdinalIgnoreCase));
        _quickButtons = new QuickButtons(GameController, Settings, _runner, _bestiaryUi, _menagerieRightClick);
        _clipboardAutoPaste = new ClipboardAutoPaste(Settings, _runner, _automationInput, _bestiaryUi);
        _merchantUi = new MerchantUi(GameController, _worldEntity);
        _faustusList = new FaustusList(_runner, _automationInput, _waits, Settings, _prices, _merchantUi, _inventoryUi);
        _mapStashUi = new MapStashUi(GameController, _automationInput, _waits, Settings, _stashUi);
        _restock = new Restock(_runner, _automationInput, _waits, Settings, _stashUi, _mapStashUi, _inventoryUi);
        _capturedMonsterStash = new CapturedMonsterStash(_automationInput, _waits, Settings, _stashUi, _inventoryUi,
            _uiCleanup, msg => _runner.UpdateStatus(msg));

        _mapDeviceUi = new MapDeviceUi(GameController, _worldEntity);
        _atlasUi = new AtlasUi(GameController, _automationInput, _waits, Settings);
        _tabPickers = new TabPickers(Settings, _stashUi, _merchantUi, _atlasUi);
        _mapDeviceLoad = new MapDeviceLoad(_runner, _automationInput, _waits, Settings, GameController, _mapDeviceUi, _atlasUi, _inventoryUi, _cost, _prices, _restock);

        _bestiary = new Bestiary(_runner, _automationInput, _waits, Settings, _bestiaryUi, _inventoryUi, _clipboardAutoPaste, _capturedMonsterStash);
        _quickButtons.StartItemizeAll = () => Log.FireAndForget(() => _bestiary.ItemizeAllAsync(), "Bestiary itemize all");
        _quickButtons.StartDeleteAll = () => Log.FireAndForget(() => _bestiary.DeleteAllAsync(), "Bestiary delete all");

        _fullSequence = new FullSequence(_runner, _automationInput, _waits, Settings, GameController, _bestiary, _faustusList, _merchantUi, _inventoryUi);

        _prices.LoadPersisted();
        _prices.SyncLeagueFromServerData();
        _prices.QueueFetch();

        _analyticsEnabledLastFrame = Settings.Analytics.Enable.Value;
        if (_analyticsEnabledLastFrame)
        {
            _sessionStore.EnsureAutoSaveMaintenance();
        }

        InitializeCurrentAreaTracking(DateTime.UtcNow);

        WireSettingsButtons();

        if (_logFile != null) Log.Info($"Session log: {_logFile.FilePath}");

        // Written every session as the header of the log file.
        Diagnostics.LogSessionHeader(GameController);
        Diagnostics.LogNonDefaultSettings(Settings);

        Log.Info("Beasts V3 loaded.");
    }

    // How this assembly's frames appear in a stack trace, used to tell our own faults apart
    // from the rest of the host's.
    private const string OwnStackMarker = "BeastsV3.";

    // TaskScheduler.UnobservedTaskException is process-wide, not per-plugin: it fires for
    // ExileCore and for every other plugin loaded alongside this one, and the finalizer
    // raises it at an arbitrary later time, so the entry lands next to whatever we happened
    // to be doing.
    //
    // Logging all of that at ERROR put other components' failures in our log under our name.
    // That is worse than silence in a bug report - it points people at the wrong codebase.
    // So anything without our frames in it is recorded quietly, as context rather than as a
    // fault of ours.
    private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        var aggregate = e.Exception?.Flatten();

        if (aggregate == null || !IsFromThisPlugin(aggregate))
        {
            Log.Debug("Unobserved task exception from elsewhere in the host process, not Beasts V3: " +
                      DescribeAggregate(aggregate));
        }
        else
        {
            Log.Error("Unobserved task exception", aggregate);
        }

        // Marked observed either way so the runtime does not tear down the host process.
        e.SetObserved();
    }

    private static bool IsFromThisPlugin(AggregateException aggregate)
    {
        foreach (var inner in aggregate.InnerExceptions)
        {
            for (var current = inner; current != null; current = current.InnerException)
            {
                if (current.StackTrace?.Contains(OwnStackMarker, StringComparison.Ordinal) == true)
                    return true;

                // A fault thrown before any frame was recorded still names its method.
                if (current.TargetSite?.DeclaringType?.FullName?
                        .StartsWith(OwnStackMarker, StringComparison.Ordinal) == true)
                    return true;
            }
        }
        return false;
    }

    private static string DescribeAggregate(AggregateException aggregate)
    {
        if (aggregate == null) return "no exception detail available.";

        var parts = new List<string>();
        foreach (var inner in aggregate.InnerExceptions)
            parts.Add($"{inner.GetType().Name}: {inner.Message}");

        return parts.Count > 0 ? string.Join(" | ", parts) : $"{aggregate.GetType().Name}: {aggregate.Message}";
    }

    public override void OnClose()
    {
        base.OnClose();
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _prices?.SavePersisted();
        if (Settings?.Analytics?.Enable?.Value == true && _recorder != null &&
            (_recorder.State.MapHistory.Count > 0 || _recorder.State.SessionBeastsFound > 0))
        {
            _recorder.AutoSave();
        }
        _webHost?.DisposeServer();
        _inputLock?.Dispose();
        _recorder?.Detach();

        _tracker = null;
        _prices = null;
        _pricePanel = null;
        _counter = null;
        _sessionStore = null;
        _cost = null;
        _recorder = null;
        _areaTransitions = null;
        _analyticsOverlay = null;
        _worldLabels = null;
        _mapOverlay = null;
        _pricePanels = null;
        _explorationRoute = null;
        _webHost = null;
        _automationState = null;
        _inputLock = null;
        _hotkeyTracker = null;
        _automationInput = null;
        _waits = null;
        _uiCleanup = null;
        _runner = null;
        _navigate = null;
        _worldEntity = null;
        _automationStatusOverlay = null;
        _inventoryUi = null;
        _bestiaryUi = null;
        _menagerieRightClick = null;
        _quickButtons = null;
        _clipboardAutoPaste = null;
        _bestiary = null;
        _merchantUi = null;
        _faustusList = null;
        _stashUi = null;
        _mapStashUi = null;
        _restock = null;
        _capturedMonsterStash = null;
        _mapDeviceUi = null;
        _atlasUi = null;
        _mapDeviceLoad = null;
        _fullSequence = null;

        _logFilePanel = null;
        _heartbeat = null;

        Log.Info("Beasts V3 unloaded.");

        // Detach before Dispose so nothing enqueues into a closing file.
        Log.Detach();
        _logFile?.Dispose();
        _logFile = null;
    }

    public override void AreaChange(AreaInstance area)
    {
        var now = DateTime.UtcNow;
        var hasCurrentMapProgress = (_recorder?.State.CurrentMapBeastsFound ?? 0) > 0;

        // Evaluated before anything resets, since the reset depends on the classification.
        var decision = _areaTransitions?.Evaluate(area, hasCurrentMapProgress);

        // An unclassifiable transition is treated as a new map.
        var startingNewMap = decision is null || decision.Kind == AreaTransitionKind.EnteredNewTrackableMap;

        _tracker?.OnAreaChanged(startingNewMap);
        _counter?.OnAreaChanged();
        _explorationRoute?.OnAreaChanged();
        _navigate?.InvalidateNavigator();

        // The cost tracker is reset by the recorder, after the previous map is finalized.

        if (decision != null)
        {
            // One line per area change.
            Log.Info($"Area change -> {decision.Kind}. area='{decision.NewAreaName}' " +
                     $"hash={Describe(decision.NewAreaHash)} instance={decision.NewAreaInstanceId} " +
                     $"from='{decision.PreviousAreaName}' finalizePrevious={decision.ShouldFinalizePreviousMap} " +
                     $"startingNewMap={startingNewMap}");

            _recorder?.OnAreaTransition(decision, now);
        }
    }

    private static string Describe(string value) => string.IsNullOrWhiteSpace(value) ? "(empty)" : value;

    // Writes the plugin's current state to the log in one block. Reads cached state only,
    // so it is safe to call mid-run.
    private void DumpDiagnostics()
    {
        try
        {
            var state = _recorder?.State;
            var markers = _tracker?.Markers;
            var liveMarkers = 0;
            if (markers != null)
            {
                foreach (var marker in markers)
                {
                    if (marker.IsLive) liveMarkers++;
                }
            }

            Diagnostics.DumpSnapshot(GameController, Settings,
                ("Area trackable", $"{state?.IsCurrentAreaTrackable}"),
                ("In finalized map", $"{state?.IsInFinalizedMap}"),
                ("Map was complete", $"{state?.CurrentMapWasComplete}"),
                ("Map elapsed", $"{state?.CurrentMapElapsed}"),
                ("Map clock running", $"{state?.CurrentMapStartUtc.HasValue}"),
                ("Map beasts / red", $"{state?.CurrentMapBeastsFound} / {state?.CurrentMapRedBeastsFound}"),
                ("Session maps", $"{state?.CompletedMapCount} completed, {state?.MapHistory.Count} in history"),
                ("Rare beasts found", $"{_tracker?.RareBeastsFound}"),
                ("Live tracked", $"{_tracker?.LiveTracked.Count}"),
                ("Markers", $"{markers?.Count ?? 0} ({liveMarkers} live, {(markers?.Count ?? 0) - liveMarkers} cached)"),
                ("All tracked captured", $"{_tracker?.AllTrackedValuableBeastsCaptured()}"),
                ("Map cost", $"{_cost?.Current.Count ?? 0} line(s), dupScarab={_cost?.CurrentMapUsesDuplicatingScarab}"),
                ("Prepared cost", $"{_cost?.Prepared.Count ?? 0} line(s)"),
                ("Prices updated", Settings.BeastPrices.LastUpdated),
                ("Tracked beasts", $"{Settings.BeastPrices.EnabledBeasts.Count} enabled"),
                ("Talismans", $"{Settings.BeastPrices.EnabledTalismans.Count} enabled, tracking={Settings.BeastPrices.TrackTalismanPrices.Value}"),
                ("Automation", _runner?.IsRunning == true ? "running" : "idle"),
                ("Web dashboard", _webHost?.IsRunning == true ? _webHost.Url : "stopped"),
                ("Log dropped lines", $"{_logFile?.DroppedLines ?? 0}"));

            Log.Info("Diagnostics written to the log file.");
            _runner?.UpdateStatus("Diagnostics written to the log file.");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to write diagnostics", ex);
        }
    }

    public override void EntityAdded(Entity entity) => _tracker?.OnEntityAdded(entity);

    public override void EntityRemoved(Entity entity) => _tracker?.OnEntityRemoved(entity);

    public override void Render()
    {
        if (!Settings.Enable.Value) return;
        if (_tracker == null) return;

        var now = DateTime.UtcNow;

        HandleAnalyticsToggleEdge(now);

        _tracker.Reconcile();
        _heartbeat?.Tick(now);
        _prices.MaybeAutoRefresh(now);

        if (Settings.Analytics.Enable.Value)
        {
            _cost.MaybePoll(now);
            _recorder.Tick(now);
            _webHost.MaybeRefreshSnapshot(now);
        }
        _webHost.EnsureServerState();

        _counter.Render();
        _worldLabels.RenderInWorld();
        _mapOverlay.Render();
        _worldLabels.RenderTrackedBeastsWindow();
        _worldLabels.RenderStylePreview();
        _pricePanels.Render();
        _analyticsOverlay.Render();
        _automationStatusOverlay.Render();
        _quickButtons.Render();
        _clipboardAutoPaste.Tick();

        _runner.CheckHotkey(Settings.BestiaryAutomation.DeleteHotkey, "Bestiary delete",
            () => _bestiary.DeleteAllAsync());
        _runner.CheckHotkey(Settings.BestiaryAutomation.RegexItemizeHotkey, "Bestiary regex itemize",
            () => _bestiary.RegexItemizeAsync());
        _runner.CheckHotkey(Settings.MerchantAutomation.FaustusListHotkey, "Faustus list",
            () => _faustusList.RunAsync());
        _runner.CheckHotkey(Settings.Restock.RestockHotkey, "Restock",
            () => _restock.RunAsync());
        _runner.CheckHotkey(Settings.Restock.LoadMapDeviceHotkey, "Load Map Device",
            () => _mapDeviceLoad.RunAsync());
        _runner.CheckHotkey(Settings.FullSequenceHotkey, "Full sequence",
            () => _fullSequence.RunAsync());

        // Uses the runner's edge detection, but the work itself is synchronous.
        _runner.CheckHotkey(Settings.LogFile.DumpDiagnosticsHotkey, "Dump diagnostics",
            () => { DumpDiagnostics(); return Task.CompletedTask; });
    }

    // Draws the custom settings menu, falling back to the host's rendering on error.
    public override void DrawSettings()
    {
        _settingsMenu ??= new SettingsMenu(Settings, new MenuContext
        {
            Session = () => _recorder?.State,
            Automation = () => _automationState,
            DashboardUrl = () => _webHost?.Url,
        });

        try
        {
            _settingsMenu.Draw();
        }
        catch (Exception ex)
        {
            Log.Error("Custom settings menu failed to draw; falling back to the default menu", ex);
            _settingsMenu = null;
            base.DrawSettings();
        }
    }

    // Clears live session state when analytics is toggled off.
    private void HandleAnalyticsToggleEdge(DateTime now)
    {
        var enabled = Settings.Analytics.Enable.Value;
        if (_analyticsEnabledLastFrame && !enabled)
        {
            _recorder.ResetSession(now, startNewCurrentMapTimer: false);
        }
        _analyticsEnabledLastFrame = enabled;
    }

    private void InitializeCurrentAreaTracking(DateTime nowUtc)
    {
        var current = GameController?.Area?.CurrentArea;
        var state = _recorder.State;
        state.SessionStartUtc = nowUtc;
        if (GameHelpers.IsRunnableMap(current))
        {
            state.IsCurrentAreaTrackable = true;
            state.ActiveMapAreaHash = GameHelpers.TryGetAreaHashText(current) ?? string.Empty;
            state.ActiveMapAreaName = GameHelpers.TryGetAreaName(current) ?? string.Empty;
            state.ActiveMapInstanceId = GameHelpers.TryGetAreaInstanceId(current);
            state.CurrentMapStartUtc = nowUtc;
        }
    }

    private void WireSettingsButtons()
    {
        // Static, so it keeps drawing after an unload.
        Settings.Changelog.Panel.DrawDelegate = Changelog.Draw;

        Settings.LogFile.StatusPanel.DrawDelegate = () => _logFilePanel?.Draw();
        Settings.LogFile.DumpDiagnostics.OnPressed = DumpDiagnostics;
        Settings.LogFile.OpenFolder.OnPressed = () =>
        {
            // Falls back to the default location when logging is off.
            var folder = _logFile?.DirectoryPath
                ?? System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "config", "BeastsV3Logs");
            try
            {
                System.IO.Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log.Error($"Failed to open the log folder ({folder})", ex); }
        };

        Settings.BeastPrices.FetchPrices.OnPressed = () => _prices.QueueFetch();
        Settings.BeastPrices.SelectAll.OnPressed = () => _prices.SetAllEnabled(true);
        Settings.BeastPrices.DeselectAll.OnPressed = () => _prices.SetAllEnabled(false);
        Settings.BeastPrices.Select15cPlus.OnPressed = () => _prices.EnableOnlyPricedAtLeast(15f);
        Settings.BeastPrices.SelectAllTalismans.OnPressed = () => _prices.SetAllTalismansEnabled(true);
        Settings.BeastPrices.DeselectAllTalismans.OnPressed = () => _prices.SetAllTalismansEnabled(false);
        Settings.BeastPrices.SelectTalismans15cPlus.OnPressed = () => _prices.EnableTalismansPricedAtLeast(15f);
        Settings.BeastPrices.BeastPicker.DrawDelegate = () => _pricePanel.Draw();

        Settings.BestiaryAutomation.ItemizedBeastTabPicker.DrawDelegate = () => _tabPickers.DrawItemizedBeastTab();
        Settings.BestiaryAutomation.RedBeastTabPicker.DrawDelegate = () => _tabPickers.DrawRedBeastTab();
        Settings.MerchantAutomation.FaustusShopTabPicker.DrawDelegate = () => _tabPickers.DrawFaustusShopTab();
        Settings.Restock.AtlasMapPicker.DrawDelegate = () => _tabPickers.DrawAtlasMap();

        var restockTargets = new[]
        {
            Settings.Restock.Target1, Settings.Restock.Target2, Settings.Restock.Target3,
            Settings.Restock.Target4, Settings.Restock.Target5, Settings.Restock.Target6,
        };
        for (var i = 0; i < restockTargets.Length; i++)
        {
            // Captured per iteration so each delegate binds its own target.
            var target = restockTargets[i];
            var slot = i + 1;
            target.StashTabPicker.DrawDelegate = () => _tabPickers.DrawRestockTargetTab(target, slot);
        }

        Settings.Analytics.ResetSession.OnPressed = () =>
        {
            if (!IsShiftHeld()) { Log.Info("Reset Session: hold Shift and click to confirm."); return; }
            _recorder.ResetSession(DateTime.UtcNow, startNewCurrentMapTimer: true);
            Log.Info("Session reset.");
        };
        Settings.Analytics.ResetMapAverage.OnPressed = () =>
        {
            if (!IsShiftHeld()) { Log.Info("Reset Map Average: hold Shift and click to confirm."); return; }
            _recorder.ResetMapAverage();
            Log.Info("Map average reset.");
        };
        Settings.Analytics.SaveSessionSnapshot.OnPressed = () =>
        {
            var ok = _recorder.SaveNamed(name: null);
            Log.Info(ok ? "Session snapshot saved." : "Session snapshot save failed.");
        };

        Settings.ExplorationRoute.Recalculate.OnPressed = () => _explorationRoute.RequestRegen();
        Settings.ExplorationRoute.ExcludedEntityPathsList.DrawDelegate = () => _explorationRoute.DrawExcludedEntityPathsPanel();

        Settings.Analytics.Web.CopyUrl.OnPressed = () =>
        {
            try
            {
                ImGuiNET.ImGui.SetClipboardText(_webHost.Url);
                Log.Info($"Dashboard URL copied: {_webHost.Url}");
            }
            catch (Exception ex) { Log.Error("Failed to copy dashboard URL", ex); }
        };
        Settings.Analytics.Web.OpenInBrowser.OnPressed = () =>
        {
            try
            {
                _webHost.EnsureServerState();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _webHost.Url,
                    UseShellExecute = true,
                });
                Log.Info($"Opened dashboard in browser: {_webHost.Url}");
            }
            catch (Exception ex) { Log.Error("Failed to open dashboard in browser", ex); }
        };

        Settings.Timing.ResetToDefaults.OnPressed = () =>
        {
            // Fresh instances reset every node to its default.
            Settings.Timing.General = new BeastsV3.Plugin.Settings.TimingGeneralSettings();
            Settings.Timing.Clicks = new BeastsV3.Plugin.Settings.TimingClicksSettings();
            Settings.Timing.Polling = new BeastsV3.Plugin.Settings.TimingPollingSettings();
            Settings.Timing.Timeouts = new BeastsV3.Plugin.Settings.TimingTimeoutsSettings();
            Log.Info("Timing settings reset to defaults.");
        };
    }

    private static bool IsShiftHeld() =>
        (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
}
