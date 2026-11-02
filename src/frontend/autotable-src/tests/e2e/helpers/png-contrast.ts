// Minimal PNG→luminance decoder (hudson-1-owned) for REAL-PIXEL readability proofs
// (G5). Uses Node's built-in zlib — no external deps. Supports 8-bit RGB/RGBA
// (colorType 2/6), the shapes Chromium screenshots emit. Returns a luminance
// sampler + a two-cluster (text vs background) contrast estimate over a region.
import * as zlib from 'zlib';

interface Decoded { width: number; height: number; channels: number; data: Buffer; }

function decodePng(buffer: Buffer): Decoded {
  if (buffer.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG');
  let pos = 8; let width = 0, height = 0, bitDepth = 0, colorType = 0; const idat: Buffer[] = [];
  while (pos < buffer.length) {
    const len = buffer.readUInt32BE(pos); const type = buffer.toString('ascii', pos + 4, pos + 8);
    const start = pos + 8;
    if (type === 'IHDR') { width = buffer.readUInt32BE(start); height = buffer.readUInt32BE(start + 4); bitDepth = buffer[start + 8]; colorType = buffer[start + 9]; }
    else if (type === 'IDAT') idat.push(buffer.subarray(start, start + len));
    else if (type === 'IEND') break;
    pos = start + len + 4;
  }
  if (bitDepth !== 8 || (colorType !== 2 && colorType !== 6)) throw new Error(`unsupported PNG bitDepth=${bitDepth} colorType=${colorType}`);
  const channels = colorType === 6 ? 4 : 3;
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = width * channels; const data = Buffer.alloc(height * stride);
  let ri = 0;
  const paeth = (a: number, b: number, c: number) => { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; };
  for (let y = 0; y < height; y++) {
    const filter = raw[ri++]; const row = y * stride; const prev = (y - 1) * stride;
    for (let x = 0; x < stride; x++) {
      const rawByte = raw[ri++]; const a = x >= channels ? data[row + x - channels] : 0; const b = y > 0 ? data[prev + x] : 0; const c = (x >= channels && y > 0) ? data[prev + x - channels] : 0;
      let val = rawByte;
      if (filter === 1) val = rawByte + a; else if (filter === 2) val = rawByte + b; else if (filter === 3) val = rawByte + ((a + b) >> 1); else if (filter === 4) val = rawByte + paeth(a, b, c);
      data[row + x] = val & 0xff;
    }
  }
  return { width, height, channels, data };
}

function lum(r: number, g: number, b: number): number {
  const f = (v: number) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
}
function contrast(l1: number, l2: number): number { const a = Math.max(l1, l2), b = Math.min(l1, l2); return (a + 0.05) / (b + 0.05); }

// Two-cluster text/background contrast over the whole image (a rendered popup
// clip). Buckets pixel luminance; the two most-populated luminance modes are the
// background (majority) and the text (a distinct minority). Returns their WCAG
// contrast ratio and coverage so callers can require e.g. >= 4.5:1.
export function popupTextContrast(pngBuffer: Buffer): { width: number; height: number; contrast: number; bgLum: number; textLum: number; textCoverage: number } {
  const img = decodePng(pngBuffer);
  const { width, height, channels, data } = img;
  const buckets = new Array(21).fill(0); const bucketLum = new Array(21).fill(0);
  let n = 0;
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const i = (y * width + x) * channels; const L = lum(data[i], data[i + 1], data[i + 2]);
    const bi = Math.min(20, Math.floor(L * 20)); buckets[bi]++; bucketLum[bi] += L; n++;
  }
  // background = most populated bucket; text = most populated bucket far from bg.
  let bg = 0; for (let i = 1; i < 21; i++) if (buckets[i] > buckets[bg]) bg = i;
  const bgLum = bucketLum[bg] / Math.max(1, buckets[bg]);
  let text = -1;
  for (let i = 0; i < 21; i++) { if (Math.abs(i - bg) < 4) continue; if (buckets[i] > 0 && (text < 0 || buckets[i] > buckets[text])) text = i; }
  const textLum = text >= 0 ? bucketLum[text] / Math.max(1, buckets[text]) : bgLum;
  const textCoverage = text >= 0 ? buckets[text] / n : 0;
  return { width, height, contrast: contrast(bgLum, textLum), bgLum, textLum, textCoverage };
}
