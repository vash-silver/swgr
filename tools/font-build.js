// Rebuild a stock SWG bitmap font family from a TTF, keeping each style's name, Leading, glyph set and
// baseline so existing UI layouts still fit. Adds an optional baked outline and drop shadow.
// usage: node font-build.js <font.ttf> <stockRoot> <outRoot> [--face verdana_bold] [--sizes 12,13,...]
//          [--outline auto|0|1|1.5|...] [--shadow 0.5] [--cap +1] [--track 0] [--hint gridfit|aa] [--tag] [--hires 2]
// --hires k renders every size k times larger (em, cell, baseline and Leading), for testing whether the engine draws
// a higher-resolution bitmap at the same on-screen size (sharper at 4K) or simply k times bigger.
// --tag is a diagnostic: the '-' glyph of each size is drawn as "[<size>]" so screenshots show which size is in use.
// stockRoot holds ui/font/<face>_<n>.inc and texture/font/<face>_<n>_000.dds (as extracted).
// Writes outRoot/ui/font/, outRoot/ui/font2/ (identical copies) and outRoot/texture/font/.
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');
const L = require('./font-lib.js');

const argv = process.argv.slice(2);
const opt = (k, d) => { const i = argv.indexOf('--' + k); return i < 0 ? d : argv[i + 1]; };
const [ttf, stockRoot, outRoot] = argv;
const face = opt('face', 'verdana_bold');
const sizes = opt('sizes', '12,13,14,16,17,18,20,22,24').split(',').map(Number);
const outlineOpt = opt('outline', 'auto');
const shadow = +opt('shadow', '0.5');
const capBoost = +opt('cap', '1');
const track = +opt('track', '0');
const hint = opt('hint', 'gridfit');
const tag = argv.includes('--tag');
const hires = +opt('hires', '1');
// --keep-metrics (with --hires): write the stock 1x Leading/Advance/AdvancePre while SourceRect stays in hi-res texels,
// to test whether the engine sizes glyph quads from the metrics (sharp at normal size) or from SourceRect (overlap)
const keepMetrics = argv.includes('--keep-metrics');
const k = keepMetrics ? hires : 1;
const PAGE = 256, GAP = 2;

// unitsPerEm and OS/2 sCapHeight from the TTF
function ttfMetrics(buf) {
  const n = buf.readUInt16BE(4), t = {};
  for (let i = 0; i < n; i++) { const o = 12 + 16 * i; t[buf.toString('latin1', o, o + 4)] = buf.readUInt32BE(o + 8); }
  const upem = buf.readUInt16BE(t.head + 18);
  // sCapHeight only exists from OS/2 version 2; older fonts get a typical 0.7 em
  const cap = buf.readUInt16BE(t['OS/2']) >= 2 ? buf.readInt16BE(t['OS/2'] + 88) : 0;
  return { upem, cap: cap > 0 ? cap : 0.7 * upem };
}

// Ink rows of a stock glyph, used to find the baseline and cap height to match
function inkRows(style, tex, ch) {
  const g = style.glyphs.find((g) => g.code === ch.charCodeAt(0));
  const [x0, y0, x1, y1] = g.rect;
  let top = 1e9, bot = -1;
  for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) if (tex.px[(y * tex.w + x) * 4 + 3] > 100) { top = Math.min(top, y - y0); bot = Math.max(bot, y - y0); }
  return { top, bot };
}

function raster(em, cellH, baseline, codes) {
  const tmp = path.join(os.tmpdir(), `glyph-raster-${process.pid}.json`);
  execFileSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(__dirname, 'glyph-raster.ps1'),
    path.resolve(ttf), String(em), String(cellH), String(baseline), codes.join(','), tmp, hint], { stdio: 'inherit' });
  const j = JSON.parse(fs.readFileSync(tmp, 'utf8'));
  fs.unlinkSync(tmp);
  return j.map((g) => ({ ...g, a: Buffer.from(g.a, 'base64') }));
}

// Composite fill (white) over outline (black) over shadow (black, offset 1,1). Returns RGBA w*h.
function effects(g, radius) {
  const { w, h, a } = g;
  const at = (x, y) => (x < 0 || y < 0 || x >= w || y >= h ? 0 : a[y * w + x] / 255);
  const ol = new Float32Array(w * h);
  if (radius > 0) {
    const R = Math.ceil(radius + 0.5);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
      let m = 0;
      for (let dy = -R; dy <= R; dy++) for (let dx = -R; dx <= R; dx++) {
        const k = Math.min(1, Math.max(0, radius + 0.5 - Math.hypot(dx, dy)));
        if (k > 0) m = Math.max(m, at(x + dx, y + dy) * k);
      }
      ol[y * w + x] = m;
    }
  } else for (let i = 0; i < w * h; i++) ol[i] = a[i] / 255;
  const px = Buffer.alloc(w * h * 4);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = y * w + x;
    const sh = shadow * (x > 0 && y > 0 ? ol[(y - 1) * w + x - 1] : 0);
    const f = a[i] / 255;
    let A = ol[i] + sh * (1 - ol[i]);          // black outline over black shadow
    let C = 0;
    C = f + C * (1 - f); A = f + A * (1 - f);  // white fill over the black layers (premultiplied)
    px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = A > 0 ? Math.round((C / A) * 255) : 255;
    px[i * 4 + 3] = Math.round(A * 255);
  }
  return px;
}

const autoOutline = (n) => (n <= 14 ? 0.75 : n <= 20 ? 1 : 1.25);
const m = ttfMetrics(fs.readFileSync(ttf));

for (const n of sizes) {
  const name = `${face}_${n}`;
  const style = L.parseInc(fs.readFileSync(path.join(stockRoot, 'ui/font', name + '.inc'), 'latin1'));
  const tex = L.readDds(fs.readFileSync(path.join(stockRoot, 'texture/font', name + '_000.dds')));
  const H = inkRows(style, tex, 'H');
  const leading = Math.round(style.leading * hires);
  const baseline = Math.round((H.bot + 1) * hires), cap = (H.bot - H.top + 1 + capBoost) * hires;
  const em = (cap * m.upem) / m.cap;
  const radius = (outlineOpt === 'auto' ? autoOutline(n) : +outlineOpt) * hires;
  // The unnamed fallback glyph (no Code) is drawn as '?'
  const codes = style.glyphs.map((g) => (g.code === null ? 63 : g.code));
  const rs = raster(em, leading, baseline, codes);
  if (tag) {
    // Lay out the rasterized "[", digits and "]" side by side into the '-' glyph
    const parts = [...`[${n}]`].map((c) => rs[codes.indexOf(c.charCodeAt(0))]);
    const ox = parts[0].ox, h = parts[0].h;
    const adv = parts.reduce((s, p) => s + Math.round(p.adv), 0), w = adv + 2 * ox;
    const a = Buffer.alloc(w * h);
    let pen = 0;
    for (const p of parts) {
      for (let y = 0; y < h; y++) for (let x = 0; x < p.w; x++) {
        const tx = pen + x;
        if (tx < w) a[y * w + tx] = Math.max(a[y * w + tx], p.a[y * p.w + x]);
      }
      pen += Math.round(p.adv);
    }
    rs[codes.indexOf(45)] = { code: 45, adv, w, h, ox, a };
  }

  const pages = [];
  let page = null, cx = 0, cy = 0, clipped = 0;
  const glyphs = style.glyphs.map((sg, i) => {
    const g = rs[i], rgba = effects(g, radius);
    let l = g.w, r = -1;
    for (let y = 0; y < g.h; y++) for (let x = 0; x < g.w; x++) if (rgba[(y * g.w + x) * 4 + 3]) { l = Math.min(l, x); r = Math.max(r, x); }
    for (let x = 0; x < g.w; x++) if (rgba[x * 4 + 3] > 64 || rgba[((g.h - 1) * g.w + x) * 4 + 3] > 64) clipped++;
    if (r < 0) { l = g.ox; r = g.ox; }          // blank glyph (space): keep a 1px cell like the stock fonts
    const gw = r - l + 1;
    if (!page || cx + gw > PAGE) { cx = 0; cy = page ? cy + leading + GAP : 0; }
    if (!page || cy + leading > PAGE) { page = { w: PAGE, h: PAGE, px: Buffer.alloc(PAGE * PAGE * 4) }; pages.push(page); cx = 0; cy = 0; }
    for (let y = 0; y < g.h; y++) rgba.copy(page.px, ((cy + y) * PAGE + cx) * 4, (y * g.w + l) * 4, (y * g.w + r + 1) * 4);
    const out = {
      ...sg,
      advance: Math.max(1, Math.round((g.adv + track * hires) / k)),
      pre: Math.round((l - g.ox) / k),
      file: `font/${name}_${String(pages.length - 1).padStart(3, '0')}`,
      rect: [cx, cy, cx + gw, cy + leading],
    };
    cx += gw + GAP;
    return out;
  });
  // Stock white texels where alpha is 0: keep RGB white so bilinear filtering never pulls in dark fringes
  for (const p of pages) for (let i = 0; i < p.px.length; i += 4) if (!p.px[i + 3]) p.px[i] = p.px[i + 1] = p.px[i + 2] = 255;

  for (const d of ['ui/font', 'ui/font2', 'texture/font']) fs.mkdirSync(path.join(outRoot, d), { recursive: true });
  const inc = L.writeInc({ name: style.name, leading: keepMetrics ? style.leading : leading, glyphs });
  fs.writeFileSync(path.join(outRoot, 'ui/font', name + '.inc'), inc, 'latin1');
  fs.writeFileSync(path.join(outRoot, 'ui/font2', name + '.inc'), inc, 'latin1');
  pages.forEach((p, i) => fs.writeFileSync(path.join(outRoot, 'texture/font', `${name}_${String(i).padStart(3, '0')}.dds`), L.writeDds(p)));
  console.log(`${name}: em ${em.toFixed(2)}px, cap ${cap}, baseline ${baseline}, outline ${radius}, ${pages.length} page(s)` + (clipped ? `, ${clipped} edge texels clipped` : ''));
}
