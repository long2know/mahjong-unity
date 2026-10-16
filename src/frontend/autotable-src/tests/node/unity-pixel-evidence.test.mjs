import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { inflateSync, deflateSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { runInNewContext } from 'node:vm';
import { root, require, plain } from './unity-test-support.mjs';

const ts = require('typescript');
const text = fs.readFileSync(path.resolve(root, '../../../tests/migration/browser-player.mjs'), 'utf8');
const source = ts.createSourceFile('browser-player.mjs', text, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
const functions = source.statements.filter(node => ts.isFunctionDeclaration(node)
  && ['pngPixels', 'pixelDifference'].includes(node.name?.text)).map(node => node.getText(source)).join('\n');
const { pixelDifference } = runInNewContext(`${functions}\n({ pixelDifference });`, { assert, inflateSync, Buffer, createHash });

function png(width, height, color = () => [20, 70, 40]) {
  const chunk = (type, data) => {
    const name = Buffer.from(type);
    const out = Buffer.alloc(data.length + 12);
    out.writeUInt32BE(data.length);
    name.copy(out, 4);
    data.copy(out, 8);
    let crc = 0xffffffff;
    for (const byte of Buffer.concat([name, data])) {
      crc ^= byte;
      for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
    }
    out.writeUInt32BE((crc ^ 0xffffffff) >>> 0, out.length - 4);
    return out;
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width);
  header.writeUInt32BE(height, 4);
  header[8] = 8;
  header[9] = 2;
  const scanlines = [];
  for (let y = 0; y < height; y++) {
    const row = Buffer.alloc(width * 3 + 1);
    for (let x = 0; x < width; x++) Buffer.from(color(x, y)).copy(row, 1 + x * 3);
    scanlines.push(row);
  }
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header),
    chunk('IDAT', deflateSync(Buffer.concat(scanlines))), chunk('IEND', Buffer.alloc(0))]);
}

const regions = [
  { name: 'board', x: 0, y: 0, width: 4, height: 2 },
  { name: 'hand', x: 0, y: 2, width: 4, height: 2 },
];

test('actual screenshot decoder measures table movement independently of fixed hand pixels', () => {
  const before = png(4, 4);
  const moved = png(4, 4, (x, y) => x === 1 && y === 0 ? [90, 10, 20] : [20, 70, 40]);
  const result = plain(pixelDifference(before, moved, regions));
  assert.equal(result[0].changedPixels, 1);
  assert.equal(result[0].pixels, 8);
  assert.equal(result[0].changedFraction, 0.125);
  assert.equal(result[1].changedFraction, 0);
  assert(pixelDifference(before, before, regions).every(region => region.changedPixels === 0));
});

test('real hand pixel mutation fails fixed-hand evidence while tiny color noise stays below its declared threshold', () => {
  const before = png(4, 4);
  const changedHand = png(4, 4, (x, y) => x === 2 && y === 3 ? [200, 200, 200] : [20, 70, 40]);
  const changed = pixelDifference(before, changedHand, regions);
  assert.equal(changed[0].changedFraction, 0);
  assert.equal(changed[1].changedFraction, 0.125);
  assert(changed[1].changedFraction > 0.001, 'the compiled-browser fixed-hand gate must reject this artifact');
  const noise = png(4, 4, () => [25, 75, 45]);
  assert(pixelDifference(before, noise, regions).every(region => region.changedFraction === 0));
});

test('camera pixel evidence cannot silently compare mismatched dimensions or out-of-image regions', () => {
  const before = png(4, 4);
  assert.throws(() => pixelDifference(before, png(5, 4), regions), /Camera screenshot width changed/);
  assert.throws(() => pixelDifference(before, before, [{ ...regions[0], x: -1 }]), /Invalid camera comparison region/);
  assert.throws(() => pixelDifference(before, before, [{ ...regions[0], width: 5 }]), /Invalid camera comparison region/);
});
