import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import vm from 'node:vm';

const [originalPath, correctedPath, mode] = process.argv.slice(2);
const original = fs.readFileSync(originalPath, 'utf8');
const corrected = fs.readFileSync(correctedPath, 'utf8');
const functionText = source => {
  const start = source.indexOf('function nativeWindowFocus(');
  const end = source.indexOf('\n}\n', start);
  assert(start >= 0 && end > start);
  return source.slice(start, end + 2);
};
const guard = functionText(corrected).match(/assert\.match\(process\.env\.DISPLAY \?\? '', (\/.*\/)\);/);
assert(guard, 'Test must inspect the corrected actual consumer guard');
const pattern = vm.runInNewContext(guard[1]);
for (const display of [':0', ':310', '127.0.0.1:310']) assert(pattern.test(display), display);
for (const display of ['', 'remote:310', 'localhost:310', '127.0.0.2:310', '192.0.2.1:310',
  '127.0.0.1:310.0', '127.0.0.1:', '127.0.0.1:310\n', '::1:310', 'evil127.0.0.1:310']) {
  assert(!pattern.test(display), `Remote/malformed DISPLAY must remain rejected: ${JSON.stringify(display)}`);
}
if (mode !== '--syntax-only') {
  assert.match(process.env.DISPLAY ?? '', /^127\.0\.0\.1:\d+$/);
  const authority = fs.statSync(process.env.XAUTHORITY);
  assert.equal(authority.uid, process.getuid());
  assert.equal(authority.mode & 0o777, 0o600);
  const load = source => vm.runInNewContext(`(${functionText(source)})`, { assert, execFileSync, process });
  assert.throws(() => load(original)(), { code: 'ERR_ASSERTION' },
    'The original guard must reject this actual authenticated launcher DISPLAY');
  const focus = load(corrected);
  const previous = focus();
  assert.match(previous, /^\d+$/);
  const restoredFrom = focus(previous);
  assert.match(restoredFrom, /^\d+$/);
  console.log(JSON.stringify({ actualAuthenticatedLoopbackDisplay: true, originalGuardRejected: true,
    nativeX11FocusAndRestorePassed: true, authorityMode: '0600', remoteSyntaxRejected: true }));
} else {
  console.log(JSON.stringify({ localSyntaxAccepted: true, remoteSyntaxRejected: true }));
}
