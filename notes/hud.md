# HUD status bars (health / action / mind)

## Layout

- The player and target windows are `ui/ui_ground_hud_mfd_status(_skinned).inc` and `ui_ground_hud_targets(_skinned).inc`. Group, pet and secondary-target windows use the same pattern.
- Each bar is a page `status.h` / `status.a` / `status.m` whose `CodeData` maps only `back`, `cap`, `juice` and `moves`. The client code resizes those pages. There's no text slot, so no layout in any of the 12 files that use `juice=` gets a value written into it.
- The UI markup has no data binding. Text widgets only show values the C++ code sets, so a UI-file mod can't display HAM numbers.

## The hidden "show health text" option

- `ui/ui_options.inc` has a checkbox `checkShowHealthBarText` (`Checked='true'`, `Visible='false'`, label `@ui_opt:show_health_text`), wired into the options `CodeData`. It sits next to an equally hidden `checkShowToolbarCooldownText`.
- Neither key appears in `original/SwgClient_r.exe` or the DX12 `x64\dx12\SwgClient_r.exe`, and `show_health_text` isn't in the string tables. Other keys from the same `CodeData` (e.g. `sliderFlyTextSize`) are present, so this is UI scaffolding with no client code behind it, either unfinished or from another branch.
- Unhiding the checkbox would do nothing. Showing the numbers needs client code: either SWGR adds it, or the exe is patched or injected, which is a much riskier kind of mod on a live server.

## DPS meter (built in)

- Options > Interface > "Show Damage Statistics Meter" (`checkDpsMeter`, saved as the `dpsMeterEnabled` option). The code is `SwgCuiDpsMeter` and the window is `ui/ui_pda_dps_meter.inc` (`dpsMeter`, movable, hidden by default).
- It shows DPS, total damage dealt, damage taken and total taken, with a Reset button.
