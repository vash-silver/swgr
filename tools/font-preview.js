// Render flytext-style sample lines with the stock font and one or more rebuilt fonts into one PNG, using the
// engine's draw model: glyph at pen+AdvancePre, pen += Advance, texture RGB tinted by the text colour.
// usage: node font-preview.js <stockRoot> <out.png> <label=modRoot>... [--face verdana_bold] [--sizes 16,22] [--scale 2]
'use strict';
const fs = require('fs');
const path = require('path');
const L = require('./font-lib.js');

const argv = process.argv.slice(2);
const opt = (k, d) => { const i = argv.indexOf('--' + k); return i < 0 ? d : argv[i + 1]; };
const pos = argv.filter((a, i) => !a.startsWith('--') && !(i > 0 && argv[i - 1].startsWith('--')));
const [stockRoot, outPng, ...modArgs] = pos;
const face = opt('face', 'verdana_bold');
const sizes = opt('sizes', '16,22').split(',').map(Number);
const scale = +opt('scale', '2');
const fonts = [[`Stock (${face})`, stockRoot], ...modArgs.map((a) => a.split('='))];

function loadFont(root, name) {
  const style = L.parseInc(fs.readFileSync(path.join(root, 'ui/font', name + '.inc'), 'latin1'));
  const pages = {};
  for (const g of style.glyphs) pages[g.file] ||= L.readDds(fs.readFileSync(path.join(root, 'texture', g.file + '.dds')));
  const byCode = new Map(style.glyphs.map((g) => [g.code, g]));
  return { style, pages, byCode, fallback: style.glyphs.find((g) => g.code === null) };
}

// Each line is a list of [text, colour] segments drawn side by side
const lines = [
  [['-1,284', [255, 70, 60]], ['CRITICAL! 3,571', [255, 220, 60]], ['+425 Health', [80, 255, 110]]],
  [['Dodge  Miss  Glancing Blow', [235, 235, 235]], ['+250 Combat XP', [130, 190, 255]]],
];
const label = loadFont(stockRoot, 'verdana_bold_14');

const W = 1400;
const blockH = (n) => 20 + lines.length * (n * scale + 4) + 10;
const H = 8 + fonts.length * sizes.reduce((s, n) => s + blockH(n), 0);
const img = { w: W, h: H, px: Buffer.alloc(W * H * 4) };
// Busy, game-like backdrop: sand to sky gradient with darker blotches, so light and dark contrast both show
for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
  const t = x / W, n = (Math.sin(x * 0.045) * Math.cos(y * 0.07) + Math.sin(x * 0.011 + y * 0.03)) * 0.25 + 0.5;
  const i = (y * W + x) * 4;
  img.px[i] = 120 + 110 * t * n; img.px[i + 1] = 130 + 80 * n; img.px[i + 2] = 150 + 90 * (1 - t) * n; img.px[i + 3] = 255;
}

// Returns the pen position after the text, in screen pixels
function drawText(f, text, x0, y0, color, k) {
  let pen = 0;
  for (const ch of text) {
    const g = f.byCode.get(ch.codePointAt(0)) || f.fallback;
    const tex = f.pages[g.file], [sx0, sy0, sx1, sy1] = g.rect;
    for (let y = sy0; y < sy1; y++) for (let x = sx0; x < sx1; x++) {
      const s = (y * tex.w + x) * 4, a = tex.px[s + 3] / 255;
      if (!a) continue;
      for (let dy = 0; dy < k; dy++) for (let dx = 0; dx < k; dx++) {
        const px = x0 + (pen + g.pre + x - sx0) * k + dx, py = y0 + (y - sy0) * k + dy;
        if (px < 0 || py < 0 || px >= img.w || py >= img.h) continue;
        const d = (py * img.w + px) * 4;
        for (let c = 0; c < 3; c++) img.px[d + c] = img.px[d + c] * (1 - a) + (tex.px[s + c] * color[c] / 255) * a;
      }
    }
    pen += g.advance;
  }
  return x0 + pen * k;
}

let y = 8;
fonts.forEach(([name, root], i) => {
  for (const n of sizes) {
    const f = loadFont(root, `${face}_${n}`);
    drawText(label, `${i ? i + '. ' : ''}${name}  -  size ${n}, shown at ${scale}x`, 8, y, [255, 255, 255], 1);
    y += 20;
    for (const segs of lines) {
      let x = 8;
      for (const [text, color] of segs) x = drawText(f, text, x, y, color, scale) + 14 * scale;
      y += n * scale + 4;
    }
    y += 10;
  }
});
fs.writeFileSync(outPng, L.writePng(img));
console.log('wrote', outPng, `${img.w}x${img.h}`);
