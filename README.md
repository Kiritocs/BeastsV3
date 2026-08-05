# Beasts V3

Bestiary farming plugin for **ExileCore / PoeHelper** (Path of Exile 1). Tracks beast spawns,
prices them from poe.ninja, records per-map analytics, and automates the tedious parts of a
bestiary run — itemizing, stashing, restocking and listing to Faustus.

> **Early testing release (0.1.0).** Expect rough edges. Please read
> [Reporting a bug](#reporting-a-bug) before you start — the log file is what makes a report
> actionable.

---

## Install With PluginUpdater

1. Open ExileApi.
2. Open the `PluginUpdater` plugin.
3. Click the `Add` tab.
4. Paste `https://github.com/Kiritocs/BeastsV3` into `Repository URL`.
5. Click `Clone`.
6. Either restart ExileApi, or open ExileApi `Core` settings, scroll down, and press `Reload Plugins`.

## Install From Source Folder

1. Download or clone this repository.
2. Place the `BeastsV3` folder inside your `Plugins/Source/` directory.
3. Launch ExileApi.
4. Let the host compile the plugin.
5. Enable `Beasts V3` in the plugin settings.
6. Follow the first-time setup flow in `README.md`.

## First run

Recommended order the first time you open the settings menu:

1. **Tracking: Prices** — set your league (or leave **Auto Sync League** on), hit refresh, then
   pick tracked beasts. **Select Tracked Beasts >=15c** is a reasonable starting point.
2. **Overlays** — turn on what you want to see. Overlays auto-hide in town/hideout and behind
   fullscreen panels by default.
3. **Automation** — leave this alone until the overlays look right. When you do start, bind
   **one** hotkey and watch a full run before binding more.
4. **Diagnostics: Log File** — already on. Leave it on for the test period.

### Before you bind automation hotkeys

- **Automation: Bestiary → Challenges Window Hotkey** must match your in-game Challenges keybind,
  or the plugin can't open the Bestiary panel outside the Menagerie.
- Configure **Itemized Beasts Stash Tabs** before using auto-stash. With no tabs set, auto-stash
  is skipped rather than guessing.
- **Automation: Timing → Lock User Input During Automation** is on by default. Your mouse and
  keyboard are suppressed while a run is in progress; trigger hotkeys still pass through.
- On higher-ping connections, enable **Include Server Latency In Delays** if actions land early.

---

## Features

| Area | What it does |
|---|---|
| **Beast tracking** | In-world labels and large-map markers for tracked beast spawns, with capture state and talisman support. |
| **Prices** | poe.ninja fetch with configurable auto-refresh; price overlays on captured-monster items in inventory, stash, merchant and Bestiary panels. |
| **Counter overlay** | On-screen beast counter and map completion progress. |
| **Exploration route** | *Experimental.* Generates a route through the map covering all beast spawns. Off by default. |
| **Analytics** | Per-session and per-map records: cost, yield, area transitions. Autosaved. |
| **Web dashboard** | Optional local HTTP server (default port `18421`, localhost-only) serving analytics with rolling stats and A/B comparison. Off by default. |
| **Automation** | Bestiary regex-itemize / delete, auto-stash, Faustus listing, map device load, stash restock, and a one-key full sequence chaining them. |

---

## Known limitations

- **Path of Exile 1 only.** Not a PoE 2 plugin.
- **Exploration route is experimental** and can produce poor routes in unusual layouts.
- **Automation is UI-driven.** It reads and clicks the real game UI.
- **Auto-stash needs configured tabs.** With none set, it is skipped silently by design.
- **Dashboard network access** requires elevated permissions on Windows and is off by default.
- Prices depend on poe.ninja availability and lag the real market.

---

## Reporting a bug

The log file is on by default and records everything, including detail the console hides.

1. Reproduce the problem.
2. Press **Diagnostics: Log File → Dump Diagnostics** (or bind **Dump Diagnostics Hotkey** — worth
   doing up front, since the useful moment is usually mid-run and you can't open settings then).
   This writes a full snapshot: build, non-default settings, area, tracker state, markers, map
   cost, quest text, and which UI panels were reachable.
3. **Open Log Folder** → attach `BeastsV3.log` (and `BeastsV3.prev.log` if the issue predates a
   rollover).

Logs live in `config/BeastsV3Logs/` under your host directory and roll over at 8 MB by default.

Include: what you were doing, what you expected, what happened and whether automation was running.