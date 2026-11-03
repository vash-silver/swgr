// Build bin/ship-parts.tsv for the part verdict panel's vendor mode: each ship component template's certification
// level (certificationsRequired = cert_ordnance_levelN, which the game turns into "Certified Level N Ship Equipment")
// and its display name (objectName -> string table). Vendor "Item Details" carry neither, only the template.
// usage: node ship-part-table.js <extractedRoot> <beautified/string/en> <out.tsv>
// Needs the object/tangible/ship/(components|crafted)/**/shared_*.iff templates from a local extraction (not in the repo).
'use strict';
const fs = require('fs'), path = require('path');
const [root, strDir, out] = process.argv.slice(2);
if (!out) { console.error('usage: node ship-part-table.js <extractedRoot> <stringDir> <out.tsv>'); process.exit(1); }

// Printable runs of 4+ bytes, in file order (IFF chunk names and template values)
function strings(buf) {
  const o = []; let s = '';
  for (const c of buf) { if (c >= 32 && c < 127) s += String.fromCharCode(c); else { if (s.length >= 4) o.push(s); s = ''; } }
  if (s.length >= 4) o.push(s);
  return o;
}

const tables = new Map();
function lookup(table, key) {
  if (!tables.has(table)) {
    const m = new Map();
    try {
      for (const l of fs.readFileSync(path.join(strDir, table.replace(/\\/g, '/') + '.txt'), 'utf8').split('\n')) {
        const i = l.indexOf(' = ');
        if (i > 0) m.set(l.slice(0, i).trim(), l.slice(i + 3).trim());
      }
    } catch (e) { }
    tables.set(table, m);
  }
  return tables.get(table).get(key);
}

const files = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p); else if (/^shared_.*\.iff$/.test(e.name)) files.push(p);
  }
})(path.join(root, 'object/tangible/ship'));

const rows = [];
for (const f of files) {
  const s = strings(fs.readFileSync(f));
  let level = '', name = '';
  for (let i = 0; i < s.length; i++) {
    const m = /cert_ordnance_level(\d+|ten)$/.exec(s[i]);
    if (m && /certificationsRequired$/.test(s[i - 1] || '')) level = m[1] === 'ten' ? '10' : m[1];
    if (/objectName$/.test(s[i]) && s[i + 1] && s[i + 2]) name = lookup(s[i + 1], s[i + 2]) || '';
  }
  const rel = path.relative(root, f).replace(/\\/g, '/');
  if (level || name) rows.push([rel, level, name].join('\t'));
}
rows.sort();
fs.writeFileSync(out, '# template\tcert level\tname   (tools/ship-part-table.js; game-derived, not synced)\n' + rows.join('\n') + '\n');
console.log(`${files.length} templates, ${rows.length} rows, ${rows.filter(r => r.split('\t')[1]).length} with a level -> ${out}`);
