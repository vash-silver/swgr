// Build the buff icon set for the target overlay: resolve each buff's ICON style (e.g. buffs.genericBuff) through
// ui/ui_styles.inc to an atlas + rect, crop it from texture/<atlas>.dds, and write <out>/<icon>.png plus <out>/buffs.tsv
// (crc, name, icon file, debuff, visible, max stacks, display name) for tools/buff-bar.cs.
// usage: node buff-icons.js <extractedRoot> <buff.tsv> <ui_buff strings .txt> <outDir> [--list-sources]
'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./font-lib.js');

const [root, buffTsv, stringsTxt, outDir] = process.argv.slice(2);
const listOnly = process.argv.includes('--list-sources');

// SWG Crc::calculate (MSB-first CRC-32, poly 0x04C11DB7, init and final xor 0xFFFFFFFF); keys of the buff map
function crc(s) {
  let c = 0xffffffff;
  for (const ch of Buffer.from(s, 'latin1')) {
    c ^= ch << 24;
    for (let k = 0; k < 8; k++) c = c & 0x80000000 ? (c << 1) ^ 0x04c11db7 : c << 1;
    c >>>= 0;
  }
  return (c ^ 0xffffffff) >>> 0;
}

// Every ImageStyle with its dotted namespace path, e.g. "...buffs.genericBuff" -> { source, rect }
function parseStyles(text) {
  const styles = new Map(), stack = [];
  const re = /<(\/?)(Namespace|ImageStyle)\b([^>]*?)(\/?)>/g;
  let m;
  while ((m = re.exec(text))) {
    const [, close, tag, attrs, selfClose] = m;
    const attr = (k) => { const a = new RegExp(`\\b${k}='([^']*)'`).exec(attrs); return a ? a[1] : null; };
    if (tag === 'Namespace') {
      if (close) stack.pop(); else if (!selfClose) stack.push(attr('Name') || '');
    } else if (!close) {
      const full = [...stack, attr('Name')].join('.');
      if (!styles.has(full)) styles.set(full, { source: attr('Source'), rect: (attr('SourceRect') || '').split(',').map(Number) });
    }
  }
  return styles;
}

function resolve(styles, icon) {
  let best = null;
  for (const [full, s] of styles) if ((full === icon || full.endsWith('.' + icon)) && (!best || full.length < best[0].length)) best = [full, s];
  return best && best[1];
}

const styles = parseStyles(fs.readFileSync(path.join(root, 'ui/ui_styles.inc'), 'latin1'));
const rows = fs.readFileSync(buffTsv, 'utf8').split(/\r?\n/);
const head = rows[0].split('\t'), col = (n) => head.indexOf(n);
const buffs = rows.slice(2).map((r) => r.split('\t')).filter((r) => r[0]);

const sources = new Set(), missing = new Set();
for (const b of buffs) {
  const icon = b[col('ICON')];
  if (!icon) continue;
  const s = resolve(styles, icon);
  if (s && s.source) sources.add(s.source); else missing.add(icon);
}
if (listOnly) {
  console.log([...sources].sort().join('\n'));
  console.error(`${sources.size} atlases, ${missing.size} unresolved icon styles: ${[...missing].slice(0, 10).join(', ')}`);
  process.exit(0);
}

// "key = value" string files (values keep a literal backslash-n for line breaks), keyed by lowercased key
function readStrings(file) {
  const map = new Map();
  if (fs.existsSync(file)) for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
    const m = /^(\S+) = (.*)$/.exec(line);
    if (m) map.set(m[1].toLowerCase(), m[2]);
  }
  return map;
}

// ui_buff: "Title\nDescription" per lowercased buff name
const names = readStrings(stringsTxt);
// Static effect lines, as the game's buff tooltip shows them: EFFECTn_PARAM -> effect_mapping (TYPE, SUBTYPE) ->
// ui_effect "<type>_<subtype>", with %DF / %DI replaced by EFFECTn_VALUE
const uiEffect = readStrings(path.join(path.dirname(stringsTxt), 'ui_effect.txt'));
const mapping = new Map();
const mapFile = path.join(path.dirname(buffTsv), 'effect_mapping.tsv');
if (fs.existsSync(mapFile)) for (const r of fs.readFileSync(mapFile, 'utf8').split(/\r?\n/).slice(2)) {
  const f = r.split('\t');
  if (f[0]) mapping.set(f[0].toLowerCase(), (f[1] + '_' + f[2]).toLowerCase());
}
const fill = (text, v) => text.replace(/%DI/g, String(Math.round(v))).replace(/%DF/g, String(Math.round(v * 100) / 100));
function effectLines(b) {
  const lines = [];
  for (let i = 1; i <= 5; i++) {
    const param = (b[col(`EFFECT${i}_PARAM`)] || '').toLowerCase();
    if (!param) continue;
    const text = uiEffect.get(mapping.get(param) || param);
    if (text) lines.push(fill(text, +b[col(`EFFECT${i}_VALUE`)] || 0));
  }
  return lines;
}

fs.mkdirSync(outDir, { recursive: true });
const atlases = new Map(), written = new Set(), out = ['crc\tname\ticon\tdebuff\tvisible\tmax_stacks\tdisplay\tdescription\teffects'];
for (const b of buffs) {
  const name = b[0], icon = b[col('ICON')];
  const s = icon && resolve(styles, icon);
  let file = '';
  if (s && s.source) {
    if (!atlases.has(s.source)) {
      const f = path.join(root, 'texture', s.source + '.dds');
      atlases.set(s.source, fs.existsSync(f) ? L.readDds(fs.readFileSync(f)) : null);
    }
    const tex = atlases.get(s.source), [x0, y0, x1, y1] = s.rect;
    if (tex && x1 > x0 && y1 > y0 && x1 <= tex.w && y1 <= tex.h) {
      file = icon + '.png';
      if (!written.has(file)) {
        const w = x1 - x0, h = y1 - y0, px = Buffer.alloc(w * h * 4);
        for (let y = 0; y < h; y++) tex.px.copy(px, y * w * 4, ((y0 + y) * tex.w + x0) * 4, ((y0 + y) * tex.w + x1) * 4);
        fs.writeFileSync(path.join(outDir, file), L.writePng({ w, h, px }));
        written.add(file);
      }
    }
  }
  const [display = '', ...desc] = (names.get(name.toLowerCase()) || '').split('\\n');
  out.push([crc(name.toLowerCase()).toString(16).padStart(8, '0'), name, file, b[col('DEBUFF')] || '0', b[col('VISIBLE')] || '1', b[col('MAX_STACKS')] || '1',
    display.trim(), desc.join('\\n').trim(), effectLines(b).join('\\n')].join('\t')); // multi-line columns keep a literal \n
}
fs.writeFileSync(path.join(outDir, 'buffs.tsv'), out.join('\n') + '\n');
console.log(`${buffs.length} buffs, ${written.size} icons written, ${buffs.length - out.filter((l) => /\t[^\t]+\.png\t/.test(l)).length} without an icon`);
