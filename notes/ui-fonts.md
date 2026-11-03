# UI fonts and flytext

## Format

- Every UI font is a pre-rendered bitmap. There's no TTF rendering in the client.
- `ui/ui_textstylemanager.inc` maps faces to font families per locale. For `en`: `default`=`verdana`, `bold`=`verdana_bold`, `fixed`=`lucida_console`, `starwars`=`starwarslogo_optimized`, `aurabesh`=`aurabesh`. `Fonts_en` lists the `.inc` files loaded.
- `ui/font/<family>_<n>.inc` holds one `<TextStyle Name Leading>` with a `<FontCharacter>` per glyph:
  - `SourceRect='x0,y0,x1,y1'`: exclusive, and always the full cell height (`y1-y0 == Leading`), so vertical placement is baked into the cell.
  - `AdvancePre`: x offset of the bitmap from the pen (may be negative, defaults to 0).
  - `Advance`: total pen advance.
  - The entry with no `Code` (Name `0000`) is the fallback glyph.
  - Codes 32–126, 160–254 and 8364 (€).
- `ui/font2/` is byte-identical to `ui/font/` apart from line endings.
- `texture/font/<family>_<n>_NNN.dds`: 256×256 uncompressed A8R8G8B8, one mip, white RGB with coverage in alpha. The engine tints RGB by the text colour, so a baked black outline stays black.
- Sizes for `verdana`/`verdana_bold`: 12, 13, 14, 16, 17, 18, 20, 22, 24 (+ bold 15).

## Flytext

- The exe builds font names at runtime with `/Fonts.%s_%d` (face, size). The size list `12,13,14,16,18,20,22,24` sits next to it, so the "Fly Text Size" option (`flyTextSize`) appears to snap to stock sizes.
- `/Fonts.arial_black_14` is referenced in the exe, but no such font ships (dead reference).
- **Confirmed in game** with a tagged build (`font-build.js --tag` turns `-` into `[size]`), at default option settings:
  - Combat flytext uses regular **`verdana_22`**.
  - Overhead names use **`verdana_18`**.
  - Chat uses `verdana_16`. Most other UI uses the smaller regular sizes and the `verdana_bold` family (system messages, target window, dialogs).
- The fly-text and object-name size options probably pick other sizes, so changing them in game would move flytext/names back to stock sizes.
- The engine draws its own dark drop shadow under overhead names, so a shadow baked into the glyphs doubles it and reads as a smudge (seen with Encode Sans at `--shadow 0.6`). Build world fonts with `--shadow 0`.
- The 1.7× stretch is the client's UI scale: `options.cfg` → `[ClientUserInterface] uiScalingFactor=1.750000` (the user plays at 3840×2160). Every UI bitmap, fonts included, is drawn at texel size × uiScalingFactor, so names are always a little soft at 1.75.
- Tested 2026-10-30 with `font-build.js --hires 2`:
  - All metrics ×2 (Leading 36, advances ×2, 2× texture): text is sharp but twice as big. The engine doesn't normalise by Leading.
  - 2× texture with 1× Leading/Advance (`--keep-metrics`): glyphs overlap and the bottom half is clipped. Each glyph quad is sized from its `SourceRect`, so more texels always means bigger text.
  - Conclusion: font files can't beat the uiScalingFactor stretch. The only levers are a lower `uiScalingFactor` (shrinks the whole UI) or the in-game Object Name Font Size option combined with a 2× font. The Object Name Font Size slider did nothing visible.
  - Final choice: a fractional `--hires` on `verdana_18`/`22` only. 1.4× was sharp but too big; **1.15×** was "perfect". Test packages: `mod/package-hires-test/` (2×), `mod/package-hires-test2/` (2× texture, 1× metrics, broken), `mod/package-hires-1.4/`, `mod/package-hires-1.15/` (= `mod/package/`).
- In-world text is drawn from these bitmap fonts, scaled. There's no TTF/DirectWrite renderer in either exe or the DLLs.
- The client in use is the DX12 build `x64\dx12\SwgClient_r.exe` (a different, larger exe from `original/`). `dx12.cfg` makes it run from `x64\`; `searchAbsolute=13` puts loose files in `x64\` at priority 13.
- Some stock `.inc` files (`verdana_13/14/20`, `verdana_bold_15`) use an older lowercase format with unquoted values. `verdana_14_000.dds` and the `starwarslogo` atlases are DXT5.

## Stock Verdana Bold metrics (last ink row of `H` = baseline − 1)

| size | cap rows | baseline |
|---|---|---|
| 12 | 3–9 | 10 |
| 14 | 3–11 | 12 |
| 16 | 4–12 | 13 |
| 18 | 4–14 | 15 |
| 20 | 5–16 | 17 |
| 22 | 5–17 | 18 |
| 24 | 5–18 | 19 |

## Loading a mod

- The client runs from `x64\` and reads `x64\client.cfg`. That file lists the stock archives as `searchTree_00_6..11=../restoration_N.tre` and `searchTOC_00..03_N=../skuN_client.toc` with `maxSearchPriority=12` and `TOCTreePath=../`, and ends with `.include "../user.cfg"`. The root `client.cfg` is a separate copy.
- Every path is relative to `x64\`, so a mod folder in the install root is `../flytext_mod`.
- `searchPath%d` and `r3SearchTree%d` are plain-text strings in the exe, and `x64\test.cfg` has `# searchPath1=../staging`. `searchTree_`/`searchTOC_` don't appear as plain text (they're built at runtime or obfuscated).
- Attempt 1 (`searchPath10=flytext_mod`, no `../`) did nothing in game.
- Attempt 2 works. A root `user.cfg` with `[SharedFile] searchPath10=../flytext_mod` and `searchPath_00_12=../flytext_mod` loads loose files over the TREs, and the login dialog's bold text shows Russo One. It's not yet known which of the two lines does it. Loose files don't need encrypting.

## Vendors (for a possible sell-all mod)

- `junk_dealer.stf` already has `mnu_sell_all_junk = Sell All Junk`, `mnu_flag_no_sale_junk = Flag Sell/No Sell` and `prose_sold_all_junk`. `loot_dealer`/`relic_dealer` have `btn_sell_all`. Selling is server-scripted (SUI list), so the client only renders it.
- `chassis_npc.stf` only covers buying/converting blueprints. No sell strings.
