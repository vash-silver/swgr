# SWGR tools

Overlays and helpers for **SWG Restoration** (the Star Wars Galaxies private server): a target HP overlay, a DPS meter, floating combat text, a threat panel, a ship part evaluator and more, all in one tray app.

They **only read** the game: what's in the client's memory, and the chat log the game writes to disk. They never write to the game, inject code or send anything over the network. See [Safety](#safety) before you use them.

## What's in it

All of these live in one tray app, `swgr-tools.exe`. Turn each one on or off in its window (double-click the tray icon → **Overlays**).

| Panel | What it shows |
|---|---|
| **Target HP overlay** | Your target's name, level, Health / Action / Mind with numbers, its buffs and debuffs with timers, and a damage trail. Six styles. Works on lairs and turrets, and switches to shields / armor / systems / chassis for ships in space |
| **Self overlay** | The same panel for your own character |
| **Mob details** | Everything the client knows about your target: level, pools, posture, flags, faction, con |
| **Nearby mobs** | Every NPC or creature around you with an HP bar, marked green when it's on you and red when it's loose on someone else |
| **Threat panel** | Mobs attacking a player near you, loose ones first, plus an ON YOU / ON *name* strip on the target overlay |
| **DPS meter** | Your damage, hits, crits, damage taken and healing per fight, and a party view in groups. Plus a Damage page with every fight of the session |
| **Combat text** | Floating damage and heal numbers in three zones you can move (dealt / taken / heals) |
| **XP tracker** | Experience earned this session, per type and per hour |
| **Credits tracker** | Cash, bank, session earned / spent and credits per hour, live |
| **Kill sound and kill streak** | A sound when your mob dies, and a streak counter on screen |
| **Part verdict** | Click a ship component in your inventory, or open **Item Details** on one at a vendor or the bazaar: it says whether the part is worth 1M+ credits on the value sheet, stat by stat |
| **Part scan** | Rates every ship component in your inventory and bags at once (tools window → Part scan) |

You can edit the value sheet's thresholds in the tools window (**Value sheet**). Text size, font and per-window opacity are on the **Appearance** page.

There's also an optional **font mod** for the game itself (Encode Sans for names, flytext and the bold UI font): see [Font mod](#font-mod).

## Requirements

- Windows 10 or 11, 64-bit.
- The SWG Restoration client (the DX12 client, `x64\dx12\SwgClient_r.exe`). The tools are tied to the client build they were made for, see [After a game patch](#after-a-game-patch).
- Nothing else: the build uses the C# compiler that ships with Windows (.NET Framework 4.x).

## Build and run

1. Download the repo (**Code → Download ZIP** on GitHub, then unzip, or `git clone`).
2. Double-click **`build.cmd`** in the top folder. It writes `bin\swgr-tools.exe` and `bin\swgr-parts.exe`.
3. Start the game, then run **`bin\swgr-tools.exe`**. It sits in the system tray (the teal ring with a gold dot). Double-click it to open the tools window.

Windows SmartScreen or your antivirus may warn about a freshly built, unsigned exe that reads another program's memory. That's expected for a tool like this. Read the source if you want to check it first.

To quit, right-click the tray icon → **Exit SWGR tools**.

### Game settings the panels need

- **DPS meter and combat text** read the game's chat log, so combat messages must be on: **Options → Chat → Combat Message Filter** = **Self** (or **Group** for the party view).
- **Combat text:** turn off the game's own numbers if you don't want both (**Options → Combat → Show Damage Numbers over Others / over Self**).
- **Part scan:** open your inventory, and any bag you want scanned, before scanning. The game only knows an item's stats once its container has been opened.

## Settings and files

Everything is saved next to the exe in `bin\`, as small text files:

| File | Holds |
|---|---|
| `swgr-tools.cfg` | Which panels are on |
| `*.pos` | Each panel's position (and size or style) |
| `swgr-ui.cfg`, `swgr-opacity.cfg`, `swgr-ql.cfg` | Text size, font, opacity, bar colours |
| `swgr-credits.cfg`, `swgr-streak.cfg` | The credits session and kill streak stats |
| `swgr-values.cfg` | Your value sheet edits (delete it, or click Reset to defaults, to go back) |
| `swgr-clock.cfg` | The buff timer calibration |

To uninstall, quit the app and delete the folder.

`bin\data\` holds the game text the panels need (mob and xp names, buff icons and text, the ship component table). Without it, names show as raw keys.

## Safety

- **Read-only:** the tools open the game process with **query and read rights only**. The code never calls anything that writes to the game, injects into it or debugs it (no WriteProcessMemory, VirtualProtectEx, CreateRemoteThread, DLL injection or debugger attach). You can check: `grep -rn "WriteProcessMemory\|VirtualProtectEx\|CreateRemoteThread" tools/` only finds comments that forbid them.
- **No network:** nothing is sent anywhere. The DPS meter and combat text read your chat log locally, and nothing from it is stored.
- **Server rules:** reading client memory is still third-party software. The author got SWG Restoration staff approval for their own use. **Check SWGR's current rules, or ask staff, before you use it on your account.** You use it at your own risk.

## After a game patch

The memory offsets are specific to one client build. After SWGR patches the client, some panels may go blank or show nonsense until the offsets are updated in the code (they're documented in [`notes/memory.md`](notes/memory.md)). Pull the latest version of this repo, then run `build.cmd` again.

## Font mod

`mod/package/` is a ready-made font replacement: Encode Sans Medium for overhead names, combat flytext and the bold UI font. To install it, quit the game and copy `mod/package/user.cfg` and the `mod/package/flytext_mod/` folder into your `C:\SWG Restoration\` folder. If you already have a `user.cfg` there, add the lines from ours to yours instead of replacing it. To remove the mod, delete the `flytext_mod` folder and those lines. Details: [`notes/ui-fonts.md`](notes/ui-fonts.md).

## For developers

- [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) is the full reference: every panel's behaviour and settings, the standalone builds (`build.cmd all`), preview renders (`--render`), and the data pack.
- [`notes/`](notes/) has how the client was mapped: the memory layout (`memory.md`), the HUD and DPS meter (`hud.md`), the font mod (`ui-fonts.md`) and the Jedi unlock research (`jedi-unlock.md`).
- `CLAUDE.md` holds the working rules used when developing this with Claude Code.

## License

The code is under the [MIT License](LICENSE). The bundled fonts (Encode Sans and the other candidates in `mod/fonts/`) are under their own licenses, in the `OFL-*.txt` / `LIC-*.txt` files next to them. Star Wars Galaxies and SWG Restoration content belongs to its owners. This project isn't affiliated with either.
