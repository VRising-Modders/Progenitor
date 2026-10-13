// call.mjs delivers bridge-queue mail on stderr, so a session that drives the game only through
// call.mjs (a Grok or Codex shell) hears from waiters. Fake bridge on a free loopback port; no game.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createQueue } from '../queue.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));

function fakeBridge() {
  const srv = net.createServer((sock) => {
    let buf = '';
    sock.setEncoding('utf8');
    sock.on('data', (chunk) => {
      buf += chunk;
      const nl = buf.indexOf('\n');
      if (nl < 0) return;
      const req = JSON.parse(buf.slice(0, nl));
      sock.write(JSON.stringify({ id: req.id, ok: true, result: { lines: [], latestSeq: 0, count: 0 } }) + '\n');
    });
    sock.on('error', () => {});
  });
  return new Promise((resolve) => srv.listen(0, '127.0.0.1', () => resolve({ srv, port: srv.address().port })));
}

function runCall(env, args) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [path.join(here, '..', 'call.mjs'), ...args], { env, windowsHide: true });
    let out = '', err = '';
    child.stdout.on('data', (d) => (out += d));
    child.stderr.on('data', (d) => (err += d));
    child.on('close', (code) => resolve({ code, out, err }));
  });
}

test('call.mjs prints queue mail on stderr, keeps stdout JSON, and PROGENITOR_CALL_NOTES=0 defers it', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-callmail-'));
  const { srv, port } = await fakeBridge();
  try {
    const tokenFile = path.join(dir, 'token');
    fs.writeFileSync(tokenFile, 't');
    const queueFile = path.join(dir, 'q.json');
    const env = {};
    for (const [k, v] of Object.entries(process.env)) if (!/^(CLAUDE|CODEX|GROK|AI_AGENT|PROGENITOR_)/.test(k)) env[k] = v;
    Object.assign(env, {
      USERPROFILE: dir, HOME: dir, APPDATA: path.join(dir, 'AppData'), PROGENITOR_TEST_ROOT: dir,
      PROGENITOR_SERVER_PORT: String(port), PROGENITOR_SERVER_TOKEN_FILE: tokenFile, PROGENITOR_QUEUE_FILE: queueFile,
      PROGENITOR_AGENT: 'grok', PROGENITOR_SESSION_NAME: 'Holder', PROGENITOR_SESSION_ID: 'g-1',
    });
    const q = createQueue({ file: queueFile, env: {}, alive: () => true });
    const holder = { ai: 'grok', name: 'Holder', id: 'g-1', tool: 'queue.mjs' };
    const waiter = { ai: 'claude', name: 'Waiter', id: 'w-1', tool: 'mcp', pid: process.pid };
    const job = { estimatedMinutes: 30, progenitor: '0.1.0+abc1234', mods: [] };
    q.join(holder, { ...job, sides: ['server'] });
    q.join(waiter, { ...job, sides: ['server'], estimatedMinutes: 15 });
    q.message(waiter, 'can I have the server for 15 min?');

    const quiet = await runCall({ ...env, PROGENITOR_CALL_NOTES: '0' }, ['server', 'logs']);
    assert.equal(quiet.code, 0);
    assert.doesNotMatch(quiet.err, /Queue message/);
    assert.ok(q.snapshot().messages.every((m) => !m.readAt), 'deferred, still unread');

    const loud = await runCall(env, ['server', 'logs']);
    assert.equal(loud.code, 0);
    assert.deepEqual(JSON.parse(loud.out), { lines: [], latestSeq: 0, count: 0 }, 'stdout stays plain JSON');
    assert.match(loud.err, /Queue message from Claude session "Waiter" .*: "can I have the server for 15 min\?"/);
    assert.match(loud.err, /node tools\/mcp\/queue\.mjs message/);
    assert.match(loud.err, /1 waiting: Claude session "Waiter".*needs server for 15 min/);

    const again = await runCall(env, ['server', 'logs']);
    assert.doesNotMatch(again.err, /Queue message/, 'delivered once');
  } finally {
    srv.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
