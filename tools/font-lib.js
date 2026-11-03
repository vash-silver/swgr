// Shared helpers for SWG UI bitmap fonts: .inc glyph tables and 32-bit ARGB .dds atlases.
'use strict';
const fs = require('fs');
const zlib = require('zlib');

// Parse a ui/font/*.inc TextStyle. Returns { name, leading, glyphs: [{code, name, advance, pre, file, rect:[x0,y0,x1,y1]}] }
// Some stock files use an older lowercase form with unquoted values (<textstyle name='x'><fontcharacter code=32 .../>).
function parseInc(text) {
  const attr = (s, k) => { const m = s.match(new RegExp('\\b' + k + "=(?:'([^']*)'|([^\\s'/>]+))", 'i')); return m ? (m[1] ?? m[2]) : undefined; };
  const head = text.match(/<TextStyle([^>]*)>/i)[1];
  const glyphs = [];
  for (const m of text.matchAll(/<FontCharacter([^>]*)\/>/gi)) {
    const s = m[1];
    glyphs.push({
      code: attr(s, 'Code') === undefined ? null : +attr(s, 'Code'),
      name: attr(s, 'Name'),
      advance: +attr(s, 'Advance'),
      pre: +(attr(s, 'AdvancePre') || 0),
      file: attr(s, 'SourceFile'),
      rect: attr(s, 'SourceRect').split(',').map(Number),
    });
  }
  return { name: attr(head, 'Name'), leading: +attr(head, 'Leading'), glyphs };
}

function writeInc(style) {
  const L = ['\t\t<TextStyle', `\t\t\tLeading='${style.leading}'`, `\t\t\tName='${style.name}'`, '\t\t>'];
  for (const g of style.glyphs) {
    L.push('\t\t\t<FontCharacter', `\t\t\t\tAdvance='${g.advance}'`);
    if (g.pre) L.push(`\t\t\t\tAdvancePre='${g.pre}'`);
    if (g.code !== null) L.push(`\t\t\t\tCode='${g.code}'`);
    L.push(`\t\t\t\tName='${g.name}'`, `\t\t\t\tSourceFile='${g.file}'`, `\t\t\t\tSourceRect='${g.rect.join(',')}'`, '\t\t\t/>');
  }
  L.push('\t\t</TextStyle>');
  return L.join('\r\n') + '\r\n';
}

// Decode DXT5 (BC3) blocks into RGBA bytes
function decodeDxt5(src, w, h) {
  const px = Buffer.alloc(w * h * 4);
  const c565 = (c) => [((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31];
  let o = 0;
  for (let by = 0; by < h; by += 4) for (let bx = 0; bx < w; bx += 4, o += 16) {
    const a0 = src[o], a1 = src[o + 1], al = [a0, a1];
    for (let i = 1; i < (a0 > a1 ? 7 : 5); i++) al.push(a0 > a1 ? ((7 - i) * a0 + i * a1) / 7 : ((5 - i) * a0 + i * a1) / 5);
    if (a0 <= a1) al.push(0, 255);
    let abits = 0n;
    for (let i = 0; i < 6; i++) abits |= BigInt(src[o + 2 + i]) << BigInt(8 * i);
    const c0 = src.readUInt16LE(o + 8), c1 = src.readUInt16LE(o + 10), p0 = c565(c0), p1 = c565(c1);
    const pal = [p0, p1, p0.map((v, i) => (2 * v + p1[i]) / 3), p0.map((v, i) => (v + 2 * p1[i]) / 3)];
    const cbits = src.readUInt32LE(o + 12);
    for (let i = 0; i < 16; i++) {
      const x = bx + (i & 3), y = by + (i >> 2);
      if (x >= w || y >= h) continue;
      const d = (y * w + x) * 4, c = pal[(cbits >> (2 * i)) & 3];
      px[d] = c[0]; px[d + 1] = c[1]; px[d + 2] = c[2];
      px[d + 3] = Math.round(al[Number((abits >> BigInt(3 * i)) & 7n)]);
    }
  }
  return px;
}

// Read an uncompressed A8R8G8B8 or DXT5 DDS. Returns { w, h, px } with px as RGBA bytes.
function readDds(buf) {
  if (buf.toString('latin1', 0, 4) !== 'DDS ') throw new Error('not a DDS');
  const h = buf.readUInt32LE(12), w = buf.readUInt32LE(16);
  if (buf.toString('latin1', 84, 88) === 'DXT5') return { w, h, px: decodeDxt5(buf.subarray(128), w, h) };
  if (buf.readUInt32LE(88) !== 32 || buf.readUInt32LE(92) !== 0xff0000) throw new Error('expected A8R8G8B8 or DXT5');
  const src = buf.subarray(128), px = Buffer.alloc(w * h * 4);
  for (let i = 0; i < w * h * 4; i += 4) { px[i] = src[i + 2]; px[i + 1] = src[i + 1]; px[i + 2] = src[i]; px[i + 3] = src[i + 3]; }
  return { w, h, px };
}

// Write an uncompressed A8R8G8B8 DDS with one mip level, matching the stock font atlases.
function writeDds({ w, h, px }) {
  const hd = Buffer.alloc(128);
  hd.write('DDS ', 0, 'latin1');
  hd.writeUInt32LE(124, 4);
  hd.writeUInt32LE(0x100f, 8);           // CAPS | HEIGHT | WIDTH | PITCH | PIXELFORMAT
  hd.writeUInt32LE(h, 12); hd.writeUInt32LE(w, 16); hd.writeUInt32LE(w * 4, 20);
  hd.writeUInt32LE(1, 28);
  hd.writeUInt32LE(32, 76);
  hd.writeUInt32LE(0x41, 80);            // DDPF_RGB | DDPF_ALPHAPIXELS
  hd.writeUInt32LE(32, 88);
  hd.writeUInt32LE(0xff0000, 92); hd.writeUInt32LE(0xff00, 96); hd.writeUInt32LE(0xff, 100); hd.writeUInt32LE(0xff000000, 104);
  hd.writeUInt32LE(0x1000, 108);         // DDSCAPS_TEXTURE
  const out = Buffer.alloc(w * h * 4);
  for (let i = 0; i < w * h * 4; i += 4) { out[i] = px[i + 2]; out[i + 1] = px[i + 1]; out[i + 2] = px[i]; out[i + 3] = px[i + 3]; }
  return Buffer.concat([hd, out]);
}

function crc32(b) {
  let c = ~0;
  for (let i = 0; i < b.length; i++) { c ^= b[i]; for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1)); }
  return ~c >>> 0;
}

// Write an 8-bit RGBA PNG (for previews).
function writePng({ w, h, px }) {
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) px.copy(raw, y * (w * 4 + 1) + 1, y * w * 4, (y + 1) * w * 4);
  const chunk = (t, d) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(d.length);
    const td = Buffer.concat([Buffer.from(t, 'latin1'), d]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
    return Buffer.concat([len, td, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

const readFile = (p) => fs.readFileSync(p);
module.exports = { parseInc, writeInc, readDds, writeDds, writePng, readFile };
