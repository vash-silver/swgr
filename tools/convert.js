// Convert extracted SWG string tables (.stf) to "key = value" text and
// datatables (IFF FORM DTII) to TSV.
// usage: node convert.js <extractedRoot> <outRoot>
'use strict';
const fs = require('fs');
const path = require('path');

function stfToText(d) {
  if (d.readUInt32LE(0) !== 0xabcd) throw new Error('bad stf magic');
  const count = d.readUInt32LE(9);
  let o = 13;
  const vals = new Map();
  for (let i = 0; i < count; i++) {
    const id = d.readUInt32LE(o), len = d.readUInt32LE(o + 8);
    o += 12;
    vals.set(id, d.toString('utf16le', o, o + 2 * len));
    o += 2 * len;
  }
  const lines = [];
  for (let i = 0; i < count; i++) {
    const id = d.readUInt32LE(o), len = d.readUInt32LE(o + 4);
    o += 8;
    const key = d.toString('latin1', o, o + len);
    o += len;
    lines.push(key + ' = ' + (vals.get(id) || '').replace(/\r?\n/g, '\\n'));
  }
  return lines.join('\n') + '\n';
}

// Minimal IFF walker: returns {tag, data} for chunks and {tag, children} for FORMs
function parseIff(d, o = 0, end = d.length) {
  const out = [];
  while (o + 8 <= end) {
    const tag = d.toString('latin1', o, o + 4), size = d.readUInt32BE(o + 4);
    if (tag === 'FORM') out.push({ tag: d.toString('latin1', o + 8, o + 12), children: parseIff(d, o + 12, o + 8 + size) });
    else out.push({ tag, data: d.subarray(o + 8, o + 8 + size) });
    o += 8 + size;
  }
  return out;
}

function cstrings(buf, start, n) {
  const res = [];
  let o = start;
  for (let i = 0; i < n; i++) { const z = buf.indexOf(0, o); res.push(buf.toString('latin1', o, z)); o = z + 1; }
  return { res, o };
}

function datatableToTsv(d) {
  const top = parseIff(d)[0];
  if (!top || top.tag !== 'DTII') throw new Error('not DTII');
  const ver = top.children[0];
  const chunk = (t) => ver.children.find(c => c.tag === t).data;
  const colsBuf = chunk('COLS'), typeBuf = chunk('TYPE'), rowsBuf = chunk('ROWS');
  const ncols = colsBuf.readUInt32LE(0);
  const cols = cstrings(colsBuf, 4, ncols).res;
  const types = cstrings(typeBuf, 0, ncols).res;
  const nrows = rowsBuf.readUInt32LE(0);
  const lines = [cols.join('\t'), types.join('\t')];
  let o = 4;
  for (let r = 0; r < nrows; r++) {
    const row = [];
    for (const t of types) {
      const k = t[0].toLowerCase();
      if (k === 'f') { row.push(+rowsBuf.readFloatLE(o).toFixed(4)); o += 4; }
      else if ('ihbe'.includes(k)) { row.push(rowsBuf.readInt32LE(o)); o += 4; }
      else { const z = rowsBuf.indexOf(0, o); row.push(rowsBuf.toString('latin1', o, z).replace(/[\t\r\n]/g, ' ')); o = z + 1; }
    }
    lines.push(row.join('\t'));
  }
  return lines.join('\n') + '\n';
}

module.exports = { stfToText, datatableToTsv, parseIff };

if (require.main === module) {
  const [inRoot, outRoot] = process.argv.slice(2).map(p => path.resolve(p));
  let n = 0, bad = 0;
  (function walk(dir) {
    for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, ent.name);
      if (ent.isDirectory()) { walk(p); continue; }
      const rel = path.relative(inRoot, p);
      let text = null, ext = null;
      try {
        const d = fs.readFileSync(p);
        if (p.endsWith('.stf')) { text = stfToText(d); ext = '.txt'; }
        else if (rel.startsWith('datatables') && d.toString('latin1', 8, 12) === 'DTII') { text = datatableToTsv(d); ext = '.tsv'; }
      } catch (err) { bad++; console.error('failed', rel, err.message); }
      if (text === null) continue;
      const dest = path.join(outRoot, rel.replace(/\.[^.\\/]+$/, '') + ext);
      fs.mkdirSync(path.dirname(dest), { recursive: true });
      fs.writeFileSync(dest, text);
      n++;
    }
  })(inRoot);
  console.log(`converted ${n} files${bad ? `, ${bad} failed` : ''}`);
}
