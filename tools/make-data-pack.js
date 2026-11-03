// Build bin/data/: the small set of game data the panels read at runtime, copied from a local extraction, so the
// tools work on a machine without one (friends who build from the repo get it with the repo).
//   string/en/...        mob, lair, building and ship names, xp and skill names, buff text, space mobile types
//   datatables/skill/skills.tsv   skill costs for the XP tracker
//   buff-icons/          icons + buffs.tsv for the HP overlay's buff row (tools/buff-icons.js output)
//   ship-parts.tsv       template -> cert level + name for vendor verdicts (tools/ship-part-table.js output)
// Everything is optional: a panel falls back to raw keys (or no icons) when a file is missing.
// usage: node tools/make-data-pack.js [beautifiedDir] [binDir]     (defaults: beautified bin)
'use strict';
const fs = require('fs'), path = require('path');
const src = process.argv[2] || 'beautified', bin = process.argv[3] || 'bin', out = path.join(bin, 'data');

const STRINGS = ['mob', 'space', 'lair_n.txt', 'building_name.txt', 'exp_n.txt', 'skl_n.txt', 'ui_buff.txt'];
const files = [];

function copy(from, to) {
  if (!fs.existsSync(from)) { console.warn('missing ' + from); return; }
  if (fs.statSync(from).isDirectory()) { for (const e of fs.readdirSync(from)) copy(path.join(from, e), path.join(to, e)); return; }
  fs.mkdirSync(path.dirname(to), { recursive: true });
  fs.copyFileSync(from, to);
  files.push(to);
}

fs.rmSync(out, { recursive: true, force: true });
for (const s of STRINGS) copy(path.join(src, 'string', 'en', s), path.join(out, 'string', 'en', s));
copy(path.join(src, 'datatables', 'skill', 'skills.tsv'), path.join(out, 'datatables', 'skill', 'skills.tsv'));
copy(path.join(bin, 'buff-icons'), path.join(out, 'buff-icons'));
copy(path.join(bin, 'ship-parts.tsv'), path.join(out, 'ship-parts.tsv'));
const bytes = files.reduce((n, f) => n + fs.statSync(f).size, 0);
console.log(`${files.length} files, ${(bytes / 1048576).toFixed(1)} MB -> ${out}`);
