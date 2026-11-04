# Live client memory (read-only)

The user has SWGR approval to **read** client memory. Never write or modify it. `tools/memread.cs` opens the process with `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ` only, with no injection, debugger attach or write calls. Keep it that way.

Build: `cd tools && csc -nologo -platform:x64 -optimize -out:../bin/memread.exe memread.cs` (csc from `C:\Windows\Microsoft.NET\Framework64\v4.0.30319`). Run it from a folder-relative path; csc mangles `tools/x.cs` arguments under Git Bash.

## Target

- The running client is `x64\dx12\SwgClient_r.exe`. A pristine copy is in `original/dx12/`.
- It's ASLR'd, with image base `0x140000000` in the file. Add RVAs to the live module base (`memread info`).
- MSVC RTTI is present. `node tools/rtti.js original/dx12/SwgClient_r.exe <Class>` prints primary vtable RVAs:

| Class | vtable RVA |
|---|---|
| Object | 0x1c7db78 |
| ClientObject | 0x1c811c8 |
| TangibleObject | 0x1c85608 |
| CreatureObject | 0x1c86368 |
| PlayerObject | 0x1c9ac88 |
| SwgCuiHudGround | 0x1cf2138 |
| SwgCuiMfdStatus | 0x1d041e8 (0 live instances found by heap scan) |

## Client patch 2026-10-30 (current addresses)

The client was patched on 2026-10-30 (`x64\dx12\SwgClient_r.exe`, 37,222,952 bytes; copy in `original/dx12-2026-10-30/`). Every RVA moved, but the CreatureObject field offsets (name, HAM, target, level, template, StringId) did not. The addresses elsewhere in this file are from the 2026-10-28 build. Current values (used by `tools/`):

| What | Old RVA | New RVA |
|---|---|---|
| Player global (`[base+X] → CreatureObject`) | 0x216baf0 | 0x21f22c0 (also 0x22ec540, 0x22ef088) |
| ObjectAttributeManager map | 0x219f948 | 0x2229418 |
| CuiMediatorFactory map pointer | 0x21ba0a0 | 0x2243c30 (holds "HudGround" → `Constructor<SwgCuiHudGround>`). Not 0x219d5c0: that string-keyed map holds saved window positions ("SwgCuiInventory_<id>_") |
| CreatureObject / TangibleObject vtable | 0x1c86368 / 0x1c85608 | 0x1cf7648 / 0x1cf68c8 |
| SlottedContainer / VolumeContainer | 0x1cc2428 / 0x1cc2518 | 0x1d34488 / 0x1d34578 |
| `Constructor<SwgCuiHudGround>` / SwgCuiHudGround | 0x1cb7798 / 0x1cf2138 | 0x1d29548 / 0x1d64438 |
| SwgCuiHudWindowManagerGround / SwgCuiInventory / SwgCuiInventoryInfo | 0x1d29ca8 / 0x1cf26c8 / 0x1d291b8 | 0x1d9bee8 / 0x1d649c8 / 0x1d9b3f8 |
| `Watcher<Object>` | 0x1c7d848 | 0x1cee9e8 |
| `Constructor<SwgCuiHudSpace>` / SwgCuiHudSpace / SwgCuiHudWindowManagerSpace | – | 0x1d295e8 / 0x1d64658 / 0x1d9c688 |

- In space the client uses a separate HUD (`SwgCuiHudSpace`) with its own window manager and inventory window. The chain is the same (`+0x30`, `+0x348`, `+0x58`, `+0x158`, `+0x188`, `+8`). The ground HUD stays alive with a stale, empty selection, so `parts.cs` tries the space chain first.

- After the patch the inventory's name at `+0x280` is empty until later, so `parts.cs` identifies the inventory by id (player id + 1) instead of by name. `swgr-parts.exe --chain` prints every link of the selected-item chain with its vtable check.
- To find the factory: for each string-keyed map from `memread modmaps`, run `memread strmap <mapObj>` and look for a value whose vtable is `Constructor<SwgCuiHudGround>`.

How they were re-found: `rtti.js` on the new exe for the vtables (template classes are listed by their mangled names, e.g. `?$Watcher@VObject@@`), `memread creatures <newVtable>` to locate the player, `memread findmod <player>` for the player global, and `memread modmaps 0x2100000 0x2400000 1` (std::maps held by module globals, with size and root key) for the attribute cache (network-id keys, root value `condition = "953/994"`) and the mediator factory (string keys such as "SwgCuiCommandBrowser").

## Buffs

- **Map at CreatureObject `+0x1508`**: MSVC `std::map<uint32, PackedBuff>` (head pointer at `+0x1508`, size at `+0x1510`). The key is SWG's CRC of the lowercased buff name (`NAME` in `datatables/buff/buff.tsv`; the same CRC as the faction values). Found with `memread buffscan beautified/datatables/buff/buff.tsv`: all 9 keys on the player matched (citizenship, city_presence, adrenalBoost_1, the injections, buildabuff_inspiration).
- Node value, after the key at `+0x20`: `+0x28` end time (uint32 game seconds, `0xffffffff` = permanent), `+0x2c` value (float), `+0x30` duration in seconds (6000 for injections), `+0x38` caster network id, `+0x40` stack count, and a vector of dynamic effects at `+0x48`/`+0x50`.
- **Dynamic effects** (build-a-buff, city buffs): 0x90-byte entries `{ std::string name, std::string table (+0x20), std::string text key (+0x40), float value (+0x68), std::wstring text (+0x70) }`. Examples: `buff_builder:flush_with_success_prose` with 15.0 → "15% increase to the experience points gained from all sources." (`%DI`/`%DF` = value), and `city_buff:city_name` with "New Imp City" (`%TT` = the wstring).
- **Tooltip text** as the game shows it: "Time Remaining: hh:mm:ss", the `ui_buff` title, the `ui_buff` description, the dynamic lines, then static lines. Static lines come from `EFFECTn_PARAM` → `effect_mapping.tsv` (TYPE, SUBTYPE) → `ui_effect:<type>_<subtype>` (e.g. `skill_combat_haste`, `buildabuff_buildabuff` = "You feel inspired"), with `%DF`/`%DI` = `EFFECTn_VALUE`.
- Clock check on 2026-10-30: the game tooltip showed Inspiration (end 173,099) with 02:40:21 left, so the game time was 163,478 at that moment and the offset was 1,793,226,694 (Unix − game).
- **The offset is not stable across logins.** It was 1,793,226,694 on 2026-10-30, 1,793,258,992 at 11:47 on 2026-10-31 and 1,793,265,464 at 14:02 on 2026-10-31 (Inspiration end 215,764 with 02:11:18 left). The server re-bases buff end times when the character logs back in, so the time left is kept while offline. A stale offset made the overlay show 23 min instead of 2 h 11 min (long buffs like Inspiration, 19,875 s, still passed the sanity check). `tools/overlay.cs` now stores the player's timed buff end times (`crc:end`) as the second line of `bin/swgr-clock.cfg`. When a persisting buff's end moves without looking freshly cast, it shifts the offset by the same amount.
- Game clock: not found as a stored value. No ticking counter in module data or heap (`memread ticks`, `memread hclock`), and searching for a constant offset only gives noise. `tools/overlay.cs` calibrates instead: a buff that appears while watched gives game time = end − duration. `memread buffwatch` does the same from the CLI, but it must be started after the client is (it doesn't reattach).

## CreatureObject layout (in progress)

- Instances are found by scanning private RW memory for the vtable pointer: 7–12 live, about `0x1810` bytes apart, so they appear to be pooled fixed-size objects. A full heap scan takes about 50s.
- The body is mostly AutoDelta containers: a container vtable pointer (same value repeated) followed by begin/end/capacity pointers, e.g. at +0x140, +0x1b0, +0x1e8, +0x220.
- Two int-array containers hold the HAM values. Each container header has its begin pointer at the offset below and its end pointer 8 bytes later (13 ints, 0x34 bytes):
  - `+0xa00 → int[]`: index 0 = Health, 2 = Action, 4 = Mind. For the player at full health: `3000 30 1000 40 1000 10`.
  - `+0xa70 → int[]`: the same indexes. For the player: `3000 1000 1000 500 1000 1000`.
  - Found with `memread deepfind 0x1c86368 0x1810 3000` while the character sheet showed Health 3000/3000 (Action and Mind 100%).
  - **Confirmed in combat:** `+0xa00` is **current** (Health went 2817 → 2761 → 2758 → 2705 while mobs attacked) and `+0xa70` is **max** (showed 2999 during the fight). The odd indexes are something else; index 1 of the current array also moved (30 → 7).
  - Two creature objects can carry the player's values. The live one changes during combat. The other stayed at a static 3000/3000, probably a display copy (character-sheet avatar).
- `memread ptrints 0x1c86368 0xa00,0xa70 6` reads both arrays for all creatures. Mob health seen nearby ranged from 116 to 6009.
- `+0x30`: 64-bit network id (e.g. `35dcedf889`).
- `+0x280`: an MSVC `std::wstring` (correction, 2026-10-30): names under 8 characters (e.g. "Tri'lie") sit inline in the 16-byte buffer, and longer ones sit behind a pointer. Size is at `+0x290` and capacity at `+0x298`. Reading it as a plain `wchar_t*` loses every short name; this was probably also why the inventory's name looked empty. Display name as UTF-16 text for player characters, named NPCs and every corpse ("corpse of a vicious Rill"). About half the creatures (generic mobs and NPCs) have no text there; their name presumably comes from a string-table key. Not resolved yet.
- Dead creatures have current health 0, e.g. `0/299`.
- `memread creatures 0x1c86368` lists address, id, HP/action/mind (current/max) and name for every creature.
- **Current target (confirmed):** on the player's CreatureObject, `+0xd08` and `+0xd20` hold the target's network id and `+0xd18` points to the target's CreatureObject.
  - Found by recording every location holding the five candidate worrt ids (`memread findq id1,id2,...`), switching target, then re-reading only those locations (`memread q addr,...`). Exactly the player's `+0xd08`/`+0xd20` (plus a few non-player copies) flipped from the worrt id to the new target's id.
  - An earlier read showed a corpse there, because looting targets the corpse.
- **Player pointer (no scan needed):** module globals at RVA `0x216baf0`, `0x2215f40` and `0x2218a88` all point to the player's CreatureObject (vtable verified as `CreatureObject`). Find with `memread findmod <playerAddr>`.
- **Level:** `int16` at `+0x1320`, duplicated at `+0x1322`. Matched player 80 / Rill 3, and it tracks HP across all creatures (level 1 ≈ 120 HP, level 10 ≈ 1,150, level-100 city NPCs 15–17k).
- **Position:** floats at `+0x84` / `+0x94` / `+0xa4` (x, y, z), the translation column of the object-to-parent transform (3×4 row-major starting near `+0x78`). These are parent-relative, so they're cell-local for creatures inside buildings. Player (3666.1, 7.4, -4968.9) vs Rill (3675.0, 8.4, -4970.9) gives 9.2 m in 3D; the game's target window showed 8m.
- **Full chain:** `[base+0x216baf0] → player`, `[player+0xd18] → target`, then target `+0x280` name, `[+0xa00][0]` current HP, `[+0xa70][0]` max HP. Verified: "a vicious Rill", 298/298. Resolves in microseconds in-process.
- **Overlay status:** `tools/overlay.cs` uses exactly this chain at 10 Hz and checks the CreatureObject vtable on both player and target before trusting either.
- **Open:**
  - Distance reads about 1 m above the game's (centre to centre vs. probably minus collision radius).
- Name: the player's name (UTF-16) sits behind pointers at +0x280, +0x2a0, +0x300, +0x320 and +0x1660 (string at +2 in the target buffer). A literal mob name ("rockmite") wasn't found, so mob names are probably string-table keys (`@mob/creature_names:…`). Still open.
- Generic mob names: solved, see "Creature details" below.
- The creature count varies a lot (7 → 223 after a relog), and the pool spacing isn't fixed, so always scan by vtable.

## Creature details (mob details panel)

Found by dumping 0x1900 bytes of every creature near Jabba's palace (dune lizards, thugs, Jawas, palace NPCs, droids, players) and comparing types, and dead vs. live lizards. Used by `tools/mob-details.cs`.

- **Name StringId:** std::string table at `+0x368`, std::string key at `+0x388` (second copy at `+0x3b0`/`+0x3d0`). E.g. `mob/creature_names` + `dune_lizard`, resolved through `beautified/string/en/mob/creature_names.txt` to "a dune lizard". Other tables seen: `npc_name`, `species` (players), `monster_name`, `droid_name`, `npc_spawner_n`, `theme_park_name`, `ep3/npc_names`. All of them resolved.
- **Template:** `[obj+0x18]` is the ObjectTemplate (`ClientCreatureObjectTemplate`, vtable `0x1c8d2e8`). `[template+0x20]` is a char* path, e.g. `object/mobile/shared_dune_lizard_hue.iff`. Players are `object/creature/player/...`.
- **Posture:** byte at `+0x988` (copies at `+0x9b8`/`+0x9b9`), using the SWG posture enum (0 Upright ... 12 Knocked down, 13 Incapacitated, 14 Dead). All 5 dead lizards had 0x0e, all 23 live ones 0. Other postures are unverified.
- **Condition bits:** uint32 at `+0x720` (copy at `+0x728`), matching SWG's `TangibleObject` conditions: 0x8 conversable, 0x10 hibernating, 0x40 aggressive, 0x80 wantSawAttackTriggers, 0x100 invulnerable, 0x800 interesting (quest giver). Seen: dune lizard `0xc0`, player `0x80`, palace thugs/Jawas `0x110`, Gamorrean guards `0x100`, droids `0x180`, Jabba `0x118`, quest NPCs `0x918`.
- **Faction:** uint32 CRC at `+0x780` (copy at `+0x784`): Rebel `0x16148850`, Imperial `0xdb4acc54`, 0 none.
- `+0x744` dword: 2 on invulnerable NPCs, 3 on dune lizards, 0x110 on players. Probably PvP/attackable status bits. Unconfirmed and not used.
- **Examine on a creature** caches only `got_milk` and `consider` (e.g. `#808080***#. a dune lizard is no match for you.#.`; the colour is the con colour). No stats or loot come from the server this way.
- Not found yet: combat state (states bitmask), difficulty (normal/elite/boss), buffs. To find them, diff a mob before and during combat, and compare a known elite or boss with a normal mob.

## Containers and the examine-attribute cache (ship part scanner)

Confirmed with a Hoersch-Kessel Military-Grade Blaster (weapon, RE level 4) whose examine window showed mass 5687.6: every stat matched.

- **Container links:** `Object +0xb8` → the object's container property: `SlottedContainer` (vtable RVA `0x1cc2428`) on the player, `VolumeContainer` (`0x1cc2518`) on the inventory and bags. Its contents vector has begin/end at `+0x20`/`+0x28`. Entries are 0x18-byte `CachedNetworkId`s: `+0` network id, `+8` watcher vtable, `+0x10` `Object*`.
  - The player's slots hold the inventory (its name at `+0x280` is "Inventory", and its id is the player id + 1), datapad, equipment and so on. Recurse through `+0xb8` for backpacks.
  - Reverse link: `Object +0xf8` → `ContainedByProperty` (`0x1cb0d08`), with the container id at `+0x18` and the container `Object*` at `+0x28`.
  - During a zone change the watchers are briefly null and the vtables change while objects are torn down. Rescan after loading, and never cache addresses.
- **Attribute cache (ObjectAttributeManager):** a module global at RVA `0x219f948` holds the `std::map` head, with the size at `+8` (102 entries on a fresh login with the inventory open). Each red-black node holds left/parent/right, isnil at `+0x19`, the key `NetworkId` at `+0x20`, then a `std::vector<std::pair<std::string, std::wstring>>` (64-byte pairs) at `+0x28`, and a revision int at `+0x40`.
  - Keys are `category.name`, e.g. `ship_component.ship_component_mass`, `ship_component.ship_component_weapon_damage` (`913.5-1287.0`) and `ship_component.ship_component_armor` (`258.8/258.8`). The level is `reverseEngineeringLevel` (no category). The certification is `weapon_cert_status.@cmd_n:cert_ordnance_level4` = `Yes`. `space_evaluation.*` holds the coloured comparison percentages (`\#00ff00124.87%`).
  - Opening the inventory fills the cache for every item in it. A bag's contents (names included) arrive only after the bag is opened.
  - Freed copies of old attribute vectors stay in the heap. Use only the ones the map reaches.
- `memread attrpairs <key>` / `memread attrvec <pairAddr>` / `memread findstr <text> [ascii|utf16]` are the inspection commands used to find this.
- `bin/swgr-parts.exe` (`tools/parts.cs`) reads it all in about 0.2 s, with no heap scan.

## Selected inventory item (part verdict panel)

The inventory window's info panel (left side, filled when you click an item) holds the selected object. Found by recording every pointer to the shield object while it was shown, then re-reading after switching to the blaster. A heap `Watcher<Object>` followed the selection.
- Root: the module global at RVA `0x21ba0a0` → `CuiMediatorFactory` map object `{head, size}` (`std::map<std::string, ConstructorBase*>`, 119 entries). Each node holds its name `std::string` at `+0x20` (e.g. "Inventory") and the constructor at `+0x40`. Find the `Constructor<SwgCuiHudGround>` node by its vtable (RVA `0x1cb7798`).
- Chain: constructor `+0x30` → `SwgCuiHudGround` (`0x1cf2138`) `+0x348` → `SwgCuiHudWindowManagerGround` (`0x1d29ca8`) `+0x58` → `SwgCuiInventory` (`0x1cf26c8`) `+0x158` → `SwgCuiInventoryInfo` (`0x1d291b8`) `+0x188` → `Watcher<Object>` (`0x1c7d848`) `+8` → `TangibleObject`. Check each vtable on every read.
- `Constructor<SwgCuiInventory>` holds only its UI path (`/Inv…`), not the window. Inventory windows are owned by the HUD window manager.
- Hover leaves no clean trace: none of the id/pointer locations followed the hovered item reliably. Bag windows opened separately from the inventory aren't covered by `+0x58`.
- `memread path <from> <to,...> [depth] [span]` searches pointer paths breadth-first (it found coincidental paths through small heap chunks too; confirm with vtables).

## Vendor / bazaar item details (part verdict panel)

Found 2026-11-03 with a vendor's "Item Details" window open on an engine (pitch 58.9 matched): `memread attrpairs` found the stat pairs, `findq` their owner, `findmod` the global.
- Module global at RVA `0x222d4a8`: `std::map<NetworkId, details>` head, size at `+8`. It holds only the item whose details window is open: it empties when the window closes and refills on the next item (logged with `swgr-parts --vendor-watch`).
- Node: key NetworkId `+0x20` (again at `+0x28`), attribute `std::vector<std::pair<std::string, std::wstring>>` at `+0x50` (same 64-byte pairs as the ObjectAttributeManager), object template `std::string` at `+0x68` (e.g. `object/tangible/ship/components/engine/shared_eng_koensayr_tuned_small_ionjet.iff`).
- The vector has the `ship_component.*`, `space_evaluation.*` and `reverseEngineered` entries but no `original_name`, no `weapon_cert_status` and no `reverseEngineeringLevel`. The window's "Certified Level N Ship Equipment" line comes from the shared template's `certificationsRequired` (`cert_ordnance_levelN`), so the level is looked up per template (`tools/ship-part-table.js` → `bin/ship-parts.tsv`). Copies of the full 18-pair list (with cert and original name) sit in the heap unowned: temporaries, not usable.
- The seller's custom item name (shown in the window) wasn't located.

## Lairs and other tangibles (HP overlay)

Found on 2026-10-31. A lair ("a giant sand beetle lair") is a plain `TangibleObject` (vtable RVA `0x1cf68c8`), template `object/tangible/lair/base/shared_poi_all_lair_*.iff`. The player's `+0xd18` points to it like any target. `InstallationObject` (`0x1d40ca8`, turrets and so on) derives from it and has the same base fields.
- **Damage taken:** AutoDelta int, delta index 10, value at `+0x600` (copy `+0x604`). **Max hitpoints:** index 11, value at `+0x630` (copy `+0x634`). The variables are 0x30 apart: vtable at `+0x5e8`/`+0x618`, index at `+0x5f0`/`+0x620`, owner at `+0x5f8`/`+0x628`. HP = max − damage. Lairs seen: 4,320 to 30,000 max. One object briefly showed 7,459 damage / 30,000, but that address was reused (as a house) soon after, so the damage field still needs a live check on a lair you're hitting.
- **Name:** the same StringId as creatures, at `+0x368`/`+0x388`, e.g. `lair_n:mound` ("a mound"), `lair_n:insecthill`, `lair_n:giant_sand_beetle`. Buildings use `building_name:…`. The wstring at `+0x280` is empty.
- Tangibles without hitpoints (max 0) are skipped by the overlay.

## Ships (space target panel)

Found on 2026-10-31 (2026-10-30 build) by dumping a targeted Mercenary Cruiser, then logging its fields every 200 ms through two fights while the user took screenshots of the game's target box. Used by `tools/overlay.cs`.

- **Target:** in space the player's `+0xd18` points to a `ShipObject` (vtable RVA `0x1cfc748`, about 0x1d10 bytes per instance). It's the same CreatureObject field as on the ground.
- **The player's ship:** player `+0xf8` → ContainedByProperty `+0x28` → the player's own `ShipObject` (the pilot sits inside it). In a multi-crew ship the chain is longer (2026-11-01): player → seat/turret TangibleObject → cell (vtable RVA `0x1d06108`) → ShipObject, so the overlay walks up to six containers until it reaches a ship (`memread ship` prints the chain). Positions (`+0x84/+0x94/+0xa4`) are world coordinates on the ship. The pilot's are ship-relative.
- **Name and strings:** name wstring at `+0x280` ("Mercenary Cruiser"; empty on player ships), StringId at `+0x368`/`+0x388` (`space/ship_names:ep3_merc_cruiser`, player ships `space_ship:player_…`). There are AutoDelta std::strings for the mobile type at `+0x1aa8`/`+0x1ac8` (`merchant_cruiser_light_tier3` → `space/space_mobile_type` "HC-556 Light Cruiser"), difficulty at `+0x1b00`/`+0x1b20` (`tier3` → `space/space_difficulty` "Tier 3") and faction at `+0x1b58`/`+0x1b78` (`mercenary`).
- **AutoDelta float variables** are 0x20 bytes: vtable (RVA `0x1cf2d38`), u16 delta index at `+8`, owner at `+0x10`, value at `+0x18` (copy at `+0x1c`).
  - Chassis: current at variable `+0x9d0` (value `+0x9e8`) and max at `+0x9f0` (value `+0xa08`). Confirmed: it fell 12000 → 8288 while max stayed.
  - Shields: front max `+0x14c0`, back max `+0x14e0`, front current `+0x1500`, back current `+0x1520` (value addresses). Confirmed for `+0x1500` (went to 0 under fire and regenerates about 20 per 1.5 s). The other three stayed at max, and the pairing follows the delta indexes (0x1a/0x1b and 0x16/0x17).
- **Per-slot component maps:** AutoDelta `std::map<int slot, float>` objects (vtable RVA `0x1cfcf28`, 0x68 bytes), with the head at `+0x18` and the size at `+0x20`. The node key int is at `+0x1c` and the float value at `+0x20` (not `+0x20`/`+0x24`: pair<int,float> is 4-aligned).
  - `+0xbb0` armor max, `+0xc18` armor current, `+0xe80` hitpoints current, `+0xee8` hitpoints max (note that the two pairs are in opposite order). Confirmed in a fight: armor went down first, then hitpoints. The player's own ship also shows `+0xc18` < `+0xbb0` while damaged.
  - Other maps hold efficiency, energy, mass and so on; the weapon maps (`+0x1098`…) are keyed by weapon slots only.
  - Slots (ShipChassisSlotType): 0 reactor, 1 engine, 2/3 shield, 4/5 armor plating, 6 capacitor, 7 booster, 8 droid interface, …, weapons from 15. The cruiser had 0,1,2,4,5,6,15, and the player's ship had 0,1,2,4,5,6,7,8,15,16,17.
- **Targeted component** (the game's green "Engine 63%" line): an AutoDelta int on the **player's own ship**. The variable is at `+0x1980` (vtable RVA `0x1cfdbe8`, delta index 0x13), with the value at `+0x1998` and a copy at `+0x199c` (`+0x1bf0` follows too). Logged while the user cycled sub-targets: 0 → 1 (engine) → 15 → 16 → 17 (weapons). It reads **115 = none** (15 fixed slots + 100 weapon slots, `SCST_num_types`), and it resets to 115 when you pick a new target. The game cycles 0 (reactor) → 1 (engine) → 15, 16, 17 (weapons) → 0 and skips shield, armor and capacitor, so 0 is the reactor. (The watcher only logged values in −1..40, so the 115 → 0 step from a fresh target never showed.) The pilot creature's `+0xd48` changed in step during the log, but it later held a pointer, so it isn't used.
  - The game's component percentage includes armor. "Engine 63%" was shown while Systems read 100%, so the engine's hitpoints were full. The overlay uses (armor + hp) / (armorMax + hpMax); "armor only" would also fit that screenshot and isn't ruled out yet.
- **The game's target box** (all rounded down), checked in two screenshots:
  - Shields = (front + back current) / (front + back max), e.g. 0 + 43200 / 86400 = 50%.
  - Armor = the armor of slots 4 and 5 only: (23040 + 15588) / 46080 = 83%, and later (23040 + 0) / 46080 = 50%.
  - Systems = the hitpoints of every non-armor slot: 17242 / 54000 = 31%. With armor included it would have been 16%.
  - Chassis = current / max: 8288 / 12000 = 69%.

## Experience (PlayerObject)

- PlayerObject vtable RVA `0x1d0c838` (2026-10-30 build). The player's PlayerObject (the "ghost") is a child in the player creature's slotted container (`+0xb8`, entry object at `+0x10`). Its id is the creature id + 0x1f. The game recreates it (for example on zoning), so look it up again each read. `tools/xp-tracker.cs` does this.
- **Experience map at PlayerObject `+0x690`**: `AutoDeltaMap<std::string, int, PlayerObject>` (vtable RVA `0x1d0d538`). It contains an MSVC `std::map` with its head at `+0x690+0x18` and its size at `+0x690+0x20`. Each node's key `std::string` is at `+0x20` and its int value at `+0x40`. The keys are the xp types (`combat_general`, `space_combat_general`, `combat_meleespecialize_unarmed`, `crafting_general`, `prestige_mercenary`, …). Found with `memread objfind` for the map vtable, then `memread strmap <PlayerObject+0x6a8>`, which prints the int in the value's low 32 bits.
- A second map of the same type at `+0x598` was empty.
- **Skills at CreatureObject `+0xe00`**: `AutoDeltaSet<const SkillObject*, CreatureObject>` (vtable RVA `0x1cf8b08`). It contains an MSVC `std::set` with its head at `+0xe00+0x18` and its size at `+0xe00+0x20`. Each node key at `+0x20` points to a `SkillObject` (vtable `0x1d3cf18`) whose name `std::string` is at `+0xb0` (e.g. `pilot_neutral_procedures_03`). The XP tracker matches these against `datatables/skill/skills.tsv` (XP_TYPE, XP_COST, SKILLS_REQUIRED) to show current xp out of the next box's cost, the same number as the game's skill tooltip.

## Aggro (threat panel)

Found on 2026-11-01 (2026-10-30 build) with `memread aggro` while the user tanked desert eopies near other players' bantha fight.

- **A creature's `+0xd18` is its combat target**, the same field that holds the player's own target. Mobs fighting showed it pointing at the player they were hitting (two eopies → the user, several dune banthas → three nearby players), and those players' `+0xd18` pointed back at a bantha. Idle mobs hold 0. `+0xd08` holds the target's network id alongside.
- **Nearby creatures:** a module-global `std::vector<Object*>` at RVA **`0x22d85d0`** (begin, end, capacity at +0/+8/+0x10). It held ~52 creatures plus 2 tangibles, every one within ~120 m of the player; the heap scan found 136 creature objects in total (far ones and UI copies). Found with `memread objlists <CreatureVtable>` (heap arrays of creature pointers), then `memread range` for module values pointing into the busiest array. Neighbours: `0x22d8568` is another vector (75 entries, mostly vtable `0x1ceed28`, plus 19 creatures), `0x22d85f0` a small one. Not yet identified.
- **Players vs NPCs:** player characters' template path starts with `object/creature/player/` (ObjectTemplate* at `+0x18`, char* path at `+0x20`), and their name StringId table is `species`. NPCs use `mob/creature_names` or `monster_name`.
- `memread aggro [0x22d85d0]` lists creatures (all, or from the vector) with distance, HP, target (`YOU` / address) and StringId. `memread vecobjs <RVA>` counts a vector's entries by vtable.
- Not found yet: the group member list (to tell groupmates from strangers), and a threat/hate value (the hate list is probably server-side only).

## Credits (credits tracker)

Cash and bank are `AutoDeltaVariable<int>`s on the player's **CreatureObject** (found 2026-11-01 by tipping 1,234 credits and diffing the creature with `memread moneysnap` / `moneydiff`; cash dropped by exactly that):

| Field | Variable at | Vtable RVA | Value |
|---|---|---|---|
| Cash | `+0x500` | `0x1cf2e58` | int at `+0x518`, copy at `+0x51c` |
| Bank | `+0x530` | `0x1cf2f18` | int at `+0x548`, copy at `+0x54c` |

The two vtables differ, so `credits.cs` checks them and rescans the first 0x2000 bytes of the creature for them if a patch moves the fields. Bank is the neighbouring variable (172,170 at the time). It hasn't yet been confirmed by a bank-only change.

While a character loads, both values read 0 for a moment before the real balances arrive. Taking that as the baseline logged the whole balance as earned (2026-11-03). `credits.cs` now skips 0/0 readings. For 5 s after a new baseline, any change only moves the baseline.

## Chat tabs (live combat lines)

Found 2026-11-04 because the chat log is written in batches of exactly 50 lines, 3–10 s apart in combat, so combat text read from the log trailed by seconds. The tab text in memory updates on every line.

Every chat tab is a heap object whose **first qword is base + `0x1d9a4c8`**. That's module data with no RTTI, not a primary vtable. `SwgCuiChatWindow` is `0x1ceab28`, but no heap object starts with it. In one session there were 7 such objects, one per tab: CityChat, GroupChat, GuildChat, Chat, Combat, Instant Messages, System Message.

| Offset | Type | Contents |
|---|---|---|
| `+0x70` | `std::wstring` | the tab's first channel name, e.g. `Combat` |
| `+0xa8` | `std::wstring` | the tab's text, the **last 1001 lines** (a rolling window, about 66k chars): `hh:mm:ss \#rrggbb<text>\#.` per line, separated by `\n` |
| `+0xc8` | `std::wstring` | a longer, growing copy (207k chars after a morning's play). Reallocated as it grows |

With the colour codes (`\#rrggbb`, `\#.`) removed and `[Combat]  ` prepended, a line is identical to the log's, and `CombatParser` reads it unchanged. A tab shows each line once. The doubled lines in the log come from two tabs showing Combat. Timestamps are local time with 1 s resolution.

No fixed pointer path is known yet. The tabs are referenced from small heap blocks: a 3-entry `vector<Tab*>` per chat window, whose begin/end/capacity sit in a pool of small nodes, not in an object with a vtable. There's no path within 4 hops of the HUD window manager. So `chat-live.cs` finds them with a multi-threaded heap scan for the marker on a background thread: about 8 s, against 56 s for `memread objs` single-threaded. It caches the result per game process in `bin/swgr-chatlive.cfg`, checks the marker on every read, and rescans every 30 s while no tab is found. It reads the string header and the last 2048 chars (4 KB) per poll, and finds new lines by matching the previous read's last 4 lines. Measured lag: every line arrived within the second of its own timestamp (mean 0.6 s, which is just the 1 s rounding), against 3–10 s from the log.

After a client patch, re-check the marker: `memread objs 0x1d9a4c8` should list the tabs. If not, search the heap for a recent combat line (`memread findstr " pts" utf16`), find the big strings holding many lines, and follow `refs` to the tab object.

The multi-crew sub-target investigation added `memread dumpchain <prefix>`. It dumps the player, seat, cell and ship (0x3000 bytes each) for diffing. In a gunner seat, the ship's `+0x1998` stays 0 with no target, so the overlay only uses that slot when you sit directly in the ship (a fighter).
