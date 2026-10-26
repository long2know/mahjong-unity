import assert from 'node:assert/strict';
import { randomBytes, randomInt } from 'node:crypto';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';

// X11 normally writes sockets/locks outside the evidence mount. A cookie-authenticated
// TCP display keeps every owned file in /probe, without disabling X access control.
const directory = process.env.TMPDIR;
assert(directory?.startsWith('/probe/'));
const display = randomInt(100, 500);
const authority = path.join(directory, `xauthority-${process.pid}`);
const field = bytes => {
  const length = Buffer.alloc(2);
  length.writeUInt16BE(bytes.length);
  return Buffer.concat([length, bytes]);
};
fs.writeFileSync(authority, Buffer.concat([
  Buffer.from([255, 255]), field(Buffer.alloc(0)), field(Buffer.from(String(display))),
  field(Buffer.from('MIT-MAGIC-COOKIE-1')), field(randomBytes(16)),
]), { mode: 0o600, flag: 'wx' });
const server = spawn('Xvfb', [
  `:${display}`, '-screen', '0', '1280x1024x24', '-nolock', '-nolisten', 'unix',
  '-listen', 'tcp', '-auth', authority, '-noreset',
], { stdio: ['ignore', 'ignore', 'inherit'] });
const serverExited = once(server, 'exit');
let child;
try {
  const deadline = Date.now() + 10000;
  while (true) {
    assert(server.exitCode === null, 'Private Xvfb exited before readiness');
    const listening = await new Promise(resolve => {
      const socket = net.connect({ host: '127.0.0.1', port: 6000 + display });
      socket.once('connect', () => { socket.destroy(); resolve(true); });
      socket.once('error', () => { socket.destroy(); resolve(false); });
    });
    if (listening) break;
    assert(Date.now() < deadline, 'Private Xvfb did not become responsive');
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  child = spawn(process.execPath, process.argv.slice(2), {
    stdio: 'inherit', env: { ...process.env, DISPLAY: `127.0.0.1:${display}`, XAUTHORITY: authority },
  });
  const [code, signal] = await once(child, 'exit');
  process.exitCode = signal ? 1 : code;
} finally {
  if (child && child.exitCode === null && child.signalCode === null) child.kill('SIGTERM');
  if (server.exitCode === null && server.signalCode === null) server.kill('SIGTERM');
  await serverExited;
  fs.rmSync(authority);
}
