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

function fakeBridge(result = { lines: [], latestSeq: 0, count: 0 }) {
  const srv = net.createServer((sock) => {
    let buf = '';
    sock.setEncoding('utf8');
    sock.on('data', (chunk) => {
      buf += chunk;
      const nl = buf.indexOf('\n');
      if (nl < 0) return;
      const req = JSON.parse(buf.slice(0, nl));
      sock.write(JSON.stringify({ id: req.id, ok: true, result }) + '\n');
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

// Live 2026-10-14: a mods_ui status read through call.mjs from Windows PowerShell showed the footer
// "Progenitor Core ... Â· Bootstrap ..." although the bridge sends U+00B7 escaped. call.mjs
// re-stringified the parsed reply as raw UTF-8, which PowerShell decoded with the console code page.
// Its stdout is now ASCII-only JSON that parses back to the exact U+00B7 under any decoder.
test('call.mjs prints ASCII-only JSON, so a middle dot survives any console code page', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-callascii-'));
  const footer = 'Progenitor Core 2026.10.14.5 · Bootstrap 2026.10.14';
  const subtitle = '2.0.0 · progenitor-reload';
  const { srv, port } = await fakeBridge({ footer, entries: [{ subtitle }], glyph: '»', wide: '\u{1F600}' });
  try {
    const tokenFile = path.join(dir, 'token');
    fs.writeFileSync(tokenFile, 't');
    const env = {};
    for (const [k, v] of Object.entries(process.env)) if (!/^(CLAUDE|CODEX|GROK|AI_AGENT|PROGENITOR_)/.test(k)) env[k] = v;
    Object.assign(env, {
      USERPROFILE: dir, HOME: dir, APPDATA: path.join(dir, 'AppData'), PROGENITOR_TEST_ROOT: dir,
      PROGENITOR_SERVER_PORT: String(port), PROGENITOR_SERVER_TOKEN_FILE: tokenFile,
      PROGENITOR_QUEUE_FILE: path.join(dir, 'q.json'), PROGENITOR_CALL_NOTES: '0',
    });
    const r = await runCall(env, ['server', 'mods_ui', '{"action":"status"}']);
    assert.equal(r.code, 0, r.err);
    for (const ch of r.out) assert.ok(ch.charCodeAt(0) < 0x80, 'non-ASCII on stdout: U+' + ch.charCodeAt(0).toString(16));
    assert.match(r.out, /\\u00b7/);
    assert.doesNotMatch(r.out, /\\u00c2/i, 'nothing double-encoded');
    // Decoding the bytes as Latin-1 (what a code-page reader does) now changes nothing.
    const parsed = JSON.parse(Buffer.from(r.out, 'utf8').toString('latin1'));
    assert.equal(parsed.footer, footer);
    assert.equal(parsed.entries[0].subtitle, subtitle);
    assert.ok(parsed.footer.includes('·') && !parsed.footer.includes('Â'));
    assert.equal(parsed.glyph, '»');
    assert.equal(parsed.wide, '\u{1F600}');
  } finally {
    srv.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// Review nit: a plain-string result is escaped the same way (no raw UTF-8 on stdout).
test('call.mjs escapes a plain-string result too', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-callascii-'));
  const { srv, port } = await fakeBridge('Core 1 \u00b7 Bootstrap 1');
  try {
    const tokenFile = path.join(dir, 'token');
    fs.writeFileSync(tokenFile, 't');
    const env = {};
    for (const [k, v] of Object.entries(process.env)) if (!/^(CLAUDE|CODEX|GROK|AI_AGENT|PROGENITOR_)/.test(k)) env[k] = v;
    Object.assign(env, {
      USERPROFILE: dir, HOME: dir, APPDATA: path.join(dir, 'AppData'), PROGENITOR_TEST_ROOT: dir,
      PROGENITOR_SERVER_PORT: String(port), PROGENITOR_SERVER_TOKEN_FILE: tokenFile,
      PROGENITOR_QUEUE_FILE: path.join(dir, 'q.json'), PROGENITOR_CALL_NOTES: '0',
    });
    const r = await runCall(env, ['server', 'logs']);
    assert.equal(r.code, 0, r.err);
    assert.equal(r.out.trim(), 'Core 1 \\u00b7 Bootstrap 1');
  } finally {
    srv.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
