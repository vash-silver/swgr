// Find MSVC x64 RTTI for classes in a PE and print their vtable RVAs (primary vtable, offset 0).
// usage: node rtti.js <file.exe> [ClassName ...]   (no names: list every class with a primary vtable)
'use strict';
const fs = require('fs');

const [file, ...names] = process.argv.slice(2);
const d = fs.readFileSync(file);
const pe = d.readUInt32LE(0x3c);
const nsec = d.readUInt16LE(pe + 6), optSize = d.readUInt16LE(pe + 20), opt = pe + 24;
const imageBase = d.readBigUInt64LE(opt + 24);
const secs = [];
for (let i = 0; i < nsec; i++) {
  const s = opt + optSize + 40 * i;
  secs.push({ name: d.toString('latin1', s, s + 8).replace(/\0.*/, ''), va: d.readUInt32LE(s + 12), vsize: d.readUInt32LE(s + 8), raw: d.readUInt32LE(s + 20), rsize: d.readUInt32LE(s + 16) });
}
const off2rva = (o) => { const s = secs.find((s) => o >= s.raw && o < s.raw + s.rsize); return s ? o - s.raw + s.va : -1; };
const rva2off = (r) => { const s = secs.find((s) => r >= s.va && r < s.va + s.rsize); return s ? r - s.va + s.raw : -1; };

// TypeDescriptor: { pVFTable u64, spare u64, char name[] } -> its RVA is name RVA - 16
const tds = new Map();
for (let i = d.indexOf('.?AV'); i >= 0; i = d.indexOf('.?AV', i + 4)) {
  const end = d.indexOf(0, i);
  const n = d.toString('latin1', i + 4, end - 2); // strip ".?AV" and "@@"
  if (!names.length || names.includes(n)) tds.set(off2rva(i - 16), n);
}

// CompleteObjectLocator (x64): { sig=1, offset, cdOffset, tdRVA, chdRVA, selfRVA }, 4-byte aligned
const cols = new Map();
for (const s of secs.filter((s) => s.name === '.rdata' || s.name === '.data')) {
  for (let o = s.raw; o + 24 <= s.raw + s.rsize; o += 4) {
    if (d.readUInt32LE(o) !== 1 || d.readUInt32LE(o + 4) !== 0) continue;
    const td = d.readUInt32LE(o + 12);
    if (tds.has(td) && d.readUInt32LE(o + 20) === off2rva(o)) cols.set(BigInt(off2rva(o)) + imageBase, tds.get(td));
  }
}

// vtable[-1] holds the absolute address of the COL; vtable = that slot + 8
const out = [];
for (const s of secs.filter((s) => s.name === '.rdata')) {
  for (let o = s.raw; o + 8 <= s.raw + s.rsize; o += 8) {
    const v = d.readBigUInt64LE(o);
    if (cols.has(v)) out.push([cols.get(v), off2rva(o + 8)]);
  }
}
console.log(`imageBase 0x${imageBase.toString(16)}`);
for (const [n, rva] of out.sort((a, b) => a[0].localeCompare(b[0]))) console.log(`${n}\tvtable RVA 0x${rva.toString(16)}`);
void rva2off;
