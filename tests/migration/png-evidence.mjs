import assert from 'node:assert/strict';
import { inflateSync } from 'node:zlib';

export function pngRgb(bytes) {
  let width, height, channels;
  const chunks = [];
  for (let offset = 8; offset < bytes.length;) {
    const length = bytes.readUInt32BE(offset), kind = bytes.subarray(offset + 4, offset + 8).toString();
    const data = bytes.subarray(offset + 8, offset + 8 + length);
    if (kind === 'IHDR') {
      width = data.readUInt32BE(0); height = data.readUInt32BE(4);
      assert.equal(data[8], 8);
      channels = data[9] === 6 ? 4 : data[9] === 2 ? 3 : 0;
      assert(channels);
    }
    if (kind === 'IDAT') chunks.push(data);
    offset += length + 12;
  }
  const raw = inflateSync(Buffer.concat(chunks)), stride = width * channels;
  const rgb = Buffer.alloc(width * height * 3);
  let previous = Buffer.alloc(stride);
  const paeth = (a, b, c) => {
    const p = a + b - c, x = Math.abs(p - a), y = Math.abs(p - b), z = Math.abs(p - c);
    return x <= y && x <= z ? a : y <= z ? b : c;
  };
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const row = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let x = 0; x < stride; x++) {
      const left = x >= channels ? row[x - channels] : 0, up = previous[x], diagonal = x >= channels ? previous[x - channels] : 0;
      const predictor = [0, left, up, Math.floor((left + up) / 2), paeth(left, up, diagonal)][filter];
      assert.notEqual(predictor, undefined);
      row[x] = (row[x] + predictor) & 255;
    }
    for (let x = 0; x < width; x++) row.copy(rgb, (y * width + x) * 3, x * channels, x * channels + 3);
    previous = row;
  }
  return { width, height, rgb };
}

export function pixelDifference(beforeBytes, afterBytes, regions) {
  const before = pngRgb(beforeBytes), after = pngRgb(afterBytes);
  assert.equal(after.width, before.width, 'Camera screenshot width changed');
  assert.equal(after.height, before.height, 'Camera screenshot height changed');
  return regions.map(region => {
    assert([region.x, region.y, region.width, region.height].every(Number.isInteger)
      && region.x >= 0 && region.y >= 0 && region.width > 0 && region.height > 0
      && region.x + region.width <= before.width && region.y + region.height <= before.height,
    `Invalid camera comparison region: ${region.name}`);
    let changedPixels = 0;
    for (let y = region.y; y < region.y + region.height; y++) for (let x = region.x; x < region.x + region.width; x++) {
      const at = (y * before.width + x) * 3;
      if ([0, 1, 2].some(channel => Math.abs(before.rgb[at + channel] - after.rgb[at + channel]) > 8)) changedPixels++;
    }
    const pixels = region.width * region.height;
    return { ...region, pixels, changedPixels, changedFraction: changedPixels / pixels };
  });
}
