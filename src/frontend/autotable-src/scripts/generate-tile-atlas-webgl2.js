#!/usr/bin/env node
/* eslint-disable */
// Phase K Wave 17 — Generate the WebGL2 canonical tile atlas
// (`img/tiles-atlas-webgl2.auto.png`) for the Phase L renderer.
//
// Owned by Hicks (frontend).  Re-run with:
//
//   node scripts/generate-tile-atlas-webgl2.js
//
// The W16 `renderer-webgl2/tile-atlas.ts` loader looks for a 3 × 34
// cell PNG at `/img/tiles-atlas-webgl2.auto.png`:
//
//   col 0 — tile FRONT face (the labelled side)
//   col 1 — tile BACK face  (yellow generic back)
//   col 2 — tile SIDE face  (cream solid)
//   34 rows × 64 px = one row per mahjong tile face id (0..33)
//
// Until W17 the loader synthesized a fallback atlas at runtime
// because the canonical asset was not committed.  W17 generates the
// asset offline + commits it, so the production path reaches a real
// PNG load + UV mapping (Phase L W3 deliverable #1).
//
// Visual source: same hue-shift palette + tile-id label that
// `tile-atlas.ts:buildFallbackAtlas()` produces, so the smoke render
// looks identical with and without the canonical asset on disk.  A
// future wave (W4+) will swap to a Blender-rendered tile-face source
// derived from `img/tiles-labels.auto.png` once the Python+PIL atlas
// extractor lands; that ships under the same path + the loader code
// is unchanged.

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const COLS = 3;
const ROWS = 34;
const CELL = 64;
const W = COLS * CELL;
const H = ROWS * CELL;
const OUT = path.resolve(__dirname, '..', 'img', 'tiles-atlas-webgl2.auto.png');

// hsl → rgb conversion (canonical Wikipedia formula).
function hslToRgb(h, s, l) {
  h = ((h % 360) + 360) % 360;
  s = Math.max(0, Math.min(1, s));
  l = Math.max(0, Math.min(1, l));
  const c = (1 - Math.abs(2 * l - 1)) * s;
  const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
  const m = l - c / 2;
  let r1 = 0, g1 = 0, b1 = 0;
  if (h < 60)       { r1 = c; g1 = x; b1 = 0; }
  else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
  else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
  else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
  else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
  else              { r1 = c; g1 = 0; b1 = x; }
  return [
    Math.round((r1 + m) * 255),
    Math.round((g1 + m) * 255),
    Math.round((b1 + m) * 255),
    255,
  ];
}

// ── 7×7 monospace bitmap font (digits + dash) ─────────────────────
//
// Used to label the FRONT column with the tile-id (0..33) so the
// smoke render's "rotate one tile to face up" interaction has a
// clear visual answer.  Hand-laid pixels to avoid pulling in canvas
// /system-font dependencies.
const FONT = {
  '0': ['.###.','#...#','#..##','#.#.#','##..#','#...#','.###.'],
  '1': ['..#..','.##..','..#..','..#..','..#..','..#..','.###.'],
  '2': ['.###.','#...#','....#','...#.','..#..','.#...','#####'],
  '3': ['.###.','#...#','....#','..##.','....#','#...#','.###.'],
  '4': ['...#.','..##.','.#.#.','#..#.','#####','...#.','...#.'],
  '5': ['#####','#....','####.','....#','....#','#...#','.###.'],
  '6': ['.###.','#...#','#....','####.','#...#','#...#','.###.'],
  '7': ['#####','....#','...#.','..#..','.#...','.#...','.#...'],
  '8': ['.###.','#...#','#...#','.###.','#...#','#...#','.###.'],
  '9': ['.###.','#...#','#...#','.####','....#','#...#','.###.'],
};
const GLYPH_W = 5;
const GLYPH_H = 7;
const GLYPH_PAD = 1;

function drawGlyph(buf, x, y, glyph, rgba) {
  for (let row = 0; row < GLYPH_H; row++) {
    const line = glyph[row];
    for (let col = 0; col < GLYPH_W; col++) {
      if (line[col] === '#') {
        setPixel(buf, x + col, y + row, rgba);
      }
    }
  }
}

function drawLabel(buf, cx, cy, label, rgba) {
  const total = label.length * (GLYPH_W + GLYPH_PAD) - GLYPH_PAD;
  let cursor = cx - Math.floor(total / 2);
  for (const ch of label) {
    const glyph = FONT[ch];
    if (glyph) {
      drawGlyph(buf, cursor, cy - Math.floor(GLYPH_H / 2), glyph, rgba);
    }
    cursor += GLYPH_W + GLYPH_PAD;
  }
}

// ── Image buffer + PNG writer (raw, no deps) ──────────────────────

function makeBuffer(w, h) {
  return Buffer.alloc(w * h * 4, 255); // RGBA, default white
}

function setPixel(buf, x, y, rgba) {
  if (x < 0 || y < 0 || x >= W || y >= H) return;
  const off = (y * W + x) * 4;
  buf[off] = rgba[0]; buf[off + 1] = rgba[1]; buf[off + 2] = rgba[2]; buf[off + 3] = rgba[3];
}

function fillRect(buf, x0, y0, w, h, rgba) {
  for (let y = y0; y < y0 + h; y++) {
    for (let x = x0; x < x0 + w; x++) {
      setPixel(buf, x, y, rgba);
    }
  }
}

function strokeRect(buf, x0, y0, w, h, rgba) {
  for (let x = x0; x < x0 + w; x++) {
    setPixel(buf, x, y0, rgba);
    setPixel(buf, x, y0 + h - 1, rgba);
  }
  for (let y = y0; y < y0 + h; y++) {
    setPixel(buf, x0, y, rgba);
    setPixel(buf, x0 + w - 1, y, rgba);
  }
}

// PNG encoder — minimal but spec-compliant (8-bit RGBA, single IDAT).
function writePng(buf, w, h, outPath) {
  const sig = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8;   // bit depth
  ihdr[9] = 6;   // colour type: truecolour + alpha
  ihdr[10] = 0;  // compression
  ihdr[11] = 0;  // filter
  ihdr[12] = 0;  // interlace
  const ihdrChunk = makeChunk('IHDR', ihdr);

  // Raw scanlines with filter byte 0 (None) prepended to each row.
  const raw = Buffer.alloc((w * 4 + 1) * h);
  let off = 0;
  for (let y = 0; y < h; y++) {
    raw[off++] = 0;
    buf.copy(raw, off, y * w * 4, (y + 1) * w * 4);
    off += w * 4;
  }
  const idat = zlib.deflateSync(raw);
  const idatChunk = makeChunk('IDAT', idat);
  const iendChunk = makeChunk('IEND', Buffer.alloc(0));

  fs.writeFileSync(outPath, Buffer.concat([sig, ihdrChunk, idatChunk, iendChunk]));
}

function makeChunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const typeBuf = Buffer.from(type, 'ascii');
  const body = Buffer.concat([typeBuf, data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body) >>> 0, 0);
  return Buffer.concat([len, body, crc]);
}

// CRC32 — IEEE polynomial 0xEDB88320 (PNG spec).
const CRC_TABLE = (() => {
  const tbl = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) {
      c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
    }
    tbl[n] = c >>> 0;
  }
  return tbl;
})();
function crc32(buf) {
  let c = 0xFFFFFFFF;
  for (let i = 0; i < buf.length; i++) {
    c = CRC_TABLE[(c ^ buf[i]) & 0xFF] ^ (c >>> 8);
  }
  return (c ^ 0xFFFFFFFF) >>> 0;
}

// ── Compose the 3 × 34 atlas ──────────────────────────────────────

function main() {
  const buf = makeBuffer(W, H);

  for (let row = 0; row < ROWS; row++) {
    // Hue cycles through 0..360 — 34 distinct mahjong face ids
    // produce a perceptually-distinct row colour with a step of
    // 360/34 ≈ 10.6°.
    const hue = (row * 360 / ROWS);
    const colHueShift = [0, 22, 38]; // front pure; back warm; side cool.

    for (let col = 0; col < COLS; col++) {
      const x0 = col * CELL;
      const y0 = row * CELL;
      // Saturation + lightness:  front = vivid; back = cream-warm;
      // side = soft cream.  Matches the W16 fallback synth.
      const sat = col === 0 ? 0.70 : col === 1 ? 0.30 : 0.18;
      const light = col === 2 ? 0.78 : 0.60;
      const bg = hslToRgb(hue + colHueShift[col], sat, light);
      fillRect(buf, x0, y0, CELL, CELL, bg);
      // Black-ish border so the cell grid is visible.
      strokeRect(buf, x0, y0, CELL, CELL, [16, 16, 16, 255]);
    }

    // Per-row label: tile-id number, painted on the front cell so
    // the rotate-to-face-up smoke test renders a recognisable label.
    drawLabel(
      buf,
      0 * CELL + CELL / 2,
      0 * CELL + (row * CELL) + Math.floor(CELL / 2),
      String(row),
      [16, 16, 16, 255],
    );
  }

  writePng(buf, W, H, OUT);
  console.log(`[tile-atlas-webgl2] wrote ${OUT} (${W}×${H}, ${fs.statSync(OUT).size} bytes)`);
}

main();
