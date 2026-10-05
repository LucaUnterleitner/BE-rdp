'use strict';
// Generates the app icon (PLACEHOLDER until the approved BearingPoint app mark is available):
// a dark rounded square with a white monitor outline. Writes PNGs and a multi-size .ico into assets/.
// Usage: node tools/make-icons.js

const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

const OUT = path.join(__dirname, '..', 'assets');
const BG = [23, 23, 23];
const FG = [255, 255, 255];

function sdRoundRect(px, py, cx, cy, hw, hh, r) {
  const dx = Math.abs(px - cx) - (hw - r);
  const dy = Math.abs(py - cy) - (hh - r);
  const ox = Math.max(dx, 0);
  const oy = Math.max(dy, 0);
  return Math.min(Math.max(dx, dy), 0) + Math.hypot(ox, oy) - r;
}

/** Coverage of the shapes at a point in a 0..1 coordinate system: [background alpha, foreground alpha]. */
function sample(x, y) {
  const bg = sdRoundRect(x, y, 0.5, 0.5, 0.5, 0.5, 0.2) <= 0 ? 1 : 0;
  // Monitor outline
  const outer = sdRoundRect(x, y, 0.5, 0.44, 0.29, 0.2, 0.04);
  const ring = Math.abs(outer + 0.03) <= 0.03 ? 1 : 0;
  // Stand
  const stem = Math.abs(x - 0.5) <= 0.03 && y >= 0.64 && y <= 0.74 ? 1 : 0;
  const base = Math.abs(x - 0.5) <= 0.14 && Math.abs(y - 0.755) <= 0.028 ? 1 : 0;
  return [bg, Math.max(ring, stem, base)];
}

function render(size) {
  const ss = 4;
  const px = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let a = 0; let f = 0;
      for (let sy = 0; sy < ss; sy++) {
        for (let sx = 0; sx < ss; sx++) {
          const [b, fg] = sample((x + (sx + 0.5) / ss) / size, (y + (sy + 0.5) / ss) / size);
          a += b; f += fg * b;
        }
      }
      a /= ss * ss; f /= ss * ss;
      const t = a ? f / a : 0;
      const i = (y * size + x) * 4;
      for (let c = 0; c < 3; c++) px[i + c] = Math.round(BG[c] * (1 - t) + FG[c] * t);
      px[i + 3] = Math.round(a * 255);
    }
  }
  return encodePng(size, px);
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
function crc32(buf) {
  let c = 0xffffffff;
  for (const b of buf) c = CRC_TABLE[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}
function encodePng(size, rgba) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) rgba.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0)),
  ]);
}
function encodeIco(images) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(images.length, 4);
  let offset = 6 + 16 * images.length;
  const entries = images.map(({ size, png }) => {
    const e = Buffer.alloc(16);
    e[0] = size >= 256 ? 0 : size; e[1] = size >= 256 ? 0 : size;
    e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6);
    e.writeUInt32LE(png.length, 8); e.writeUInt32LE(offset, 12);
    offset += png.length;
    return e;
  });
  return Buffer.concat([header, ...entries, ...images.map((i) => i.png)]);
}

fs.mkdirSync(OUT, { recursive: true });
const sizes = [16, 24, 32, 48, 64, 256];
const images = sizes.map((size) => ({ size, png: render(size) }));
for (const { size, png } of images) if ([16, 32, 256].includes(size)) fs.writeFileSync(path.join(OUT, size === 256 ? 'icon.png' : `icon-${size}.png`), png);
fs.writeFileSync(path.join(OUT, 'icon.ico'), encodeIco(images));
console.log('Icons written to', OUT);
