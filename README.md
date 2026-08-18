# Beasts V3

Bestiary farming plugin for **ExileCore / PoeHelper** (Path of Exile 1). Tracks beast spawns,
prices them from poe.ninja, records per-map analytics, and automates the tedious half of a run -
itemizing, stashing, restocking and listing to Faustus.

> **Automation drives the real game UI**, moving your cursor and sending clicks and keys.
> Everything ships disabled with no hotkeys bound; nothing runs until you set it up.

## Install

**PluginUpdater** - `Add` tab → paste `https://github.com/Kiritocs/BeastsV3` → `Clone` → restart
ExileApi (or `Core` settings → `Reload Plugins`).

**From source** - drop the `BeastsV3` folder into `Plugins/Source/`, launch ExileApi, let it
compile, enable **Beasts V3**.

## First run

1. **Tracking: Prices** - leave **Auto Sync League** on, press **Refresh Prices**, then pick beasts.
   **Select Tracked Beasts >=15c** is a good start.
2. **Overlays** - turn on what you want. They hide themselves in town, hideout and behind
   fullscreen panels.
3. **Automation** - last. Bind **one** hotkey, watch a full run, then bind more.

Before you bind anything:

- **Panic Stop** halts any run instantly. Bind it first.
- **Bestiary → Challenges Window Hotkey** must match your in-game Challenges keybind, or the
  Bestiary panel can't be opened outside the Menagerie.
- **Itemized Beasts Stash Tabs** must be set before auto-stash does anything - with none set it is
  skipped rather than guessing.
- **Lock User Input During Automation** (on by default) suppresses your mouse and keyboard mid-run;
  hotkeys still get through.

## What it does

| | |
|---|---|
| **Tracking** | World labels and map markers for tracked beasts, with capture state and talismans. |
| **Prices** | poe.ninja fetch with auto-refresh, plus price overlays in inventory, stash, merchant and Bestiary panels. |
| **Counter** | Beast count and map completion, on screen. |
| **Analytics** | Per-map and per-session records - cost, yield, atlas tree - autosaved, with a local dashboard. |
| **Automation** | Regex and yellow itemize, delete, auto-stash, Faustus listing, map device load, restock, and a one-key full sequence. |
| **Route** | *Experimental.* A route covering every beast spawn. Off by default. |

## Privacy

The dashboard is a local server on `http://localhost:18422`, localhost-only, and can be turned off.

**Community Data Sharing - please leave it off for now.** It is off by default and nothing is sent
unless you turn it on. I'll announce shortly what it collects and what it is for.

## Reporting a bug

1. Reproduce it.
2. Press **Diagnostics: Log File → Dump Diagnostics** (bind its hotkey - the useful moment is
   usually mid-run). It records build, non-default settings, area, tracker state, map cost and
   which UI panels were reachable.
3. **Open Log Folder** → attach `BeastsV3.log`, plus `BeastsV3.prev.log` if the issue predates a
   rollover. Logs live in `config/BeastsV3Logs/` and roll at 8 MB.

Include what you were doing, what you expected, and whether automation was running.

## Limitations

- Path of Exile 1 only - not a PoE 2 plugin.
- Automation reads and clicks the real UI, so a patch that moves panels can break it.
- Prices lag the market and depend on poe.ninja being up.
- The exploration route is experimental and picks poor routes in unusual layouts.
