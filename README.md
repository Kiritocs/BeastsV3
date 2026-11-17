# Beasts V3

Bestiary farming plugin for **ExileCore / PoeHelper** (Path of Exile 1). It marks and prices red
beasts, shows what your Bestiary Threads rolled, tracks profit per map, and automates restocking,
loading the Map Device, itemizing and listing at Faustus.

> **Automation moves your cursor and sends clicks and keys to the game.** It ships switched off
> with no hotkeys bound.

**Requires the English game client.**

## First-time setup

Everything is in the plugin's settings menu. Do the steps in order.

1. **Enable** the plugin.
2. **Tracking: Prices** - press **Refresh Prices**, then **Select Tracked Beasts >=15c** (or tick
   beasts yourself).
3. **Automation: Panic Stop** - bind it. It stops any run at once.
4. Match two keys to your game keybinds:
   - **Automation: Bestiary → Challenges Window Hotkey**
   - **Automation: Restock → Inventory Toggle Hotkey**
5. Open your stash once, then set **Automation: Bestiary → Itemized Beasts Stash Tabs**.
6. **Automation: Restock**:
   - **Map Target** - **Item Name** (e.g. `Map (Tier 16)`), **Quantity**, **Stash Tab**.
   - **Selected Atlas Map** - the map to run.
   - **Middle Thread Slot** - **Thread** `Bestiary Thread` and a **Count**.
7. Talk to Faustus once, then set **Automation: Faustus → Faustus Shop Tabs**.
8. Bind the **Restock**, **Load Map Device**, **Regex Itemize** and **Faustus List** hotkeys.

## Your first map

1. Open your stash and press **Restock**.
2. Open the Map Device and press **Load Map Device**. Start the map yourself.
3. Capture the red beasts. The overlays show them, the Thread effects and which tracked beasts
   can be in the map.
4. In your hideout, press **Regex Itemize**, then **Faustus List**.

Watch one full run before you trust it. **Automation: Full Sequence** does step 4 with one key.

Also included: profit analytics with a local dashboard (`http://localhost:18422`, nothing is sent
anywhere) and an experimental exploration route. Every overlay can be moved or switched off.

## Troubleshooting

When a run stops, the message at the top of the screen names the failed step. The same line is in
`config/BeastsV3Logs/BeastsV3.log`.

| Problem | Fix |
|---|---|
| A hotkey does nothing | Enable the plugin and bind the hotkey. |
| Bestiary panel does not open | **Challenges Window Hotkey** differs from your game key. |
| Itemized beasts stay on the ground | **Inventory Toggle Hotkey** differs from your game key. |
| Beasts are not stashed | Set **Itemized Beasts Stash Tabs**. Open your stash once to fill the list. |
| Restock fails | Open the stash first and set **Map Target → Stash Tab**. |
| Load Map Device fails | Stand next to the Map Device. A slot on `Leave as is` is not touched. |
| No Faustus tabs to pick | Talk to Faustus once. |
| Counter or Thread slots do not work | The game client is not in English. |
| Mouse and keyboard are blocked | **Lock User Input During Automation** does that. Press Panic Stop. |
| Broken after a game patch | Wait for an ExileCore and plugin update. |

## Reporting a bug

1. Reproduce it, then press **Diagnostics: Log File → Dump Diagnostics**.
2. Press **Open Log Folder** and attach `BeastsV3.log`.
3. Send it with what you did and expected, any of these ways:
   - Message me on Discord: [kirito2769](https://discord.com/users/210910788557340672)
   - The [Discord thread](https://discord.com/channels/1315088615071678535/1492696960715456666)
   - A [GitHub issue](https://github.com/Kiritocs/BeastsV3/issues)
