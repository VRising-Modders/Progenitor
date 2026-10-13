// End to end without a game: the real MCP server against a fake bridge on a free loopback port.
// Checks that every request carries `who`, that a reported previous user becomes a sharing note,
// and that ping shows the bridge-user summary.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { createQueue } from '../queue.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));

function fakeBridge(reply) {
  const requests = [];
  const srv = net.createServer((sock) => {
    let buf = '';
    sock.setEncoding('utf8');
    sock.on('data', (chunk) => {
      buf += chunk;
      let nl;
      while ((nl = buf.indexOf('\n')) >= 0) {
        const req = JSON.parse(buf.slice(0, nl));
        buf = buf.slice(nl + 1);
        requests.push(req);
        sock.write(JSON.stringify({ id: req.id, ok: true, ...reply(req) }) + '\n');
      }
    });
    sock.on('error', () => {});
  });
  return new Promise((resolve) => srv.listen(0, '127.0.0.1', () => resolve({ srv, port: srv.address().port, requests })));
}

test('MCP sends who, appends the sharing note once, and summarizes ping users', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-who-'));
  const tokenFile = path.join(dir, 'progenitor-dev.token');
  fs.writeFileSync(tokenFile, 'test-token');
  const other = { ai: 'codex', name: 'Example other session', id: 'other-1', lastSeen: '2026-10-10T12:00:00Z', ageSeconds: 120, lastOp: 'connect' };
  const { srv, port, requests } = await fakeBridge((req) => ({
    result: req.op === 'ping'
      ? { side: 'server', progenitor: '2026.10.3', images: [{ name: 'Core', sha256: 'c'.repeat(64), generation: 7, version: '2026.10.3+fedcba9' }], bridgeUsers: { current: { ai: 'claude', name: 'Example session', id: 'me-1', requests: 2, lastOp: 'ping' }, recent: [other] } }
      : { lines: [], latestSeq: 0, count: 0 },
    bridgeUser: other,
  }));

  const env = {};
  for (const [k, v] of Object.entries(process.env)) {
    // A clean identity: no Claude / Codex / Grok markers from the session running the tests.
    if (/^(CLAUDE|CODEX|GROK|AI_AGENT|PROGENITOR_)/.test(k)) continue;
    env[k] = v;
  }
  Object.assign(env, {
    USERPROFILE: dir, HOME: dir, APPDATA: path.join(dir, 'AppData'),
    PROGENITOR_TEST_ROOT: dir,
    PROGENITOR_QUEUE_FILE: path.join(dir, 'bridge-queue.json'),
    PROGENITOR_SERVER_PORT: String(port),
    PROGENITOR_SERVER_TOKEN_FILE: tokenFile,
    PROGENITOR_AGENT: 'claude', PROGENITOR_SESSION_NAME: 'Example session', PROGENITOR_SESSION_ID: 'me-1',
  });
  const transport = new StdioClientTransport({ command: process.execPath, args: [path.join(here, '..', 'server.mjs')], env, stderr: 'ignore' });
  const client = new Client({ name: 'who-test-client', version: '9.9' });
  try {
    await client.connect(transport);

    const first = await client.callTool({ name: 'progenitor_logs', arguments: { side: 'server' } });
    const firstText = first.content.map((c) => c.text).join('\n');
    assert.match(firstText, /Note: Codex session "Example other session" \(id other-1\) used the server bridge 2 min ago \(last op: connect\)\./);

    const second = await client.callTool({ name: 'progenitor_logs', arguments: { side: 'server' } });
    assert.ok(!second.content.some((c) => /Note:/.test(c.text)), 'same activity is noted once');

    const ping = await client.callTool({ name: 'progenitor_ping', arguments: { side: 'server' } });
    assert.match(ping.content[0].text, /^Bridge user now: Claude session "Example session" \(id me-1\)/);

    const who = requests[0].who;
    assert.equal(requests[0].token, 'test-token');
    assert.deepEqual([who.ai, who.name, who.id, who.tool, who.client], ['claude', 'Example session', 'me-1', 'mcp', 'who-test-client 9.9']);
    assert.equal(typeof who.pid, 'number');

    const whoami = await client.callTool({ name: 'progenitor_whoami', arguments: { session_name: 'Renamed' } });
    assert.equal(JSON.parse(whoami.content[0].text).name, 'Renamed');
    await client.callTool({ name: 'progenitor_logs', arguments: { side: 'server' } });
    assert.equal(requests.at(-1).who.name, 'Renamed');

    // Bridge queue: a changing op needs the slot; join grants a free side at once; leave releases it.
    const refused = await client.callTool({ name: 'progenitor_reload', arguments: { side: 'server' } });
    assert.equal(refused.isError, true);
    assert.match(refused.content[0].text, /bridge is free, but you have not claimed it/);
    assert.ok(!requests.some((r) => r.op === 'reload'), 'refused op never reaches the bridge');
    const joined = await client.callTool({ name: 'progenitor_queue', arguments: { action: 'join', sides: ['server'], estimated_minutes: 15, progenitor_version: '0.1.0+test', mods: ['Example Mod@1.0.0'], purpose: 'queue check' } });
    assert.match(joined.content[0].text, /You hold the server bridge/);
    assert.match(joined.content[0].text, /versions: Progenitor 0\.1\.0\+test; mods: Example Mod 1\.0\.0/);
    assert.match(joined.content[0].text, /MCP pid \d+ alive/);
    // Mailbox: another session waits and writes; the holder's next tool result carries the message.
    const otherQueue = createQueue({ file: path.join(dir, 'bridge-queue.json'), env: {} });
    const waiter = { ai: 'grok', name: 'Waiter', id: 'w-1', tool: 'mcp', pid: process.pid };
    otherQueue.join(waiter, { sides: ['server'], estimatedMinutes: 15, progenitor: '0.1.0+abc1234', mods: [] });
    otherQueue.message(waiter, 'need the server for 15 min');
    const mailed = await client.callTool({ name: 'progenitor_logs', arguments: { side: 'server' } });
    const mailText = mailed.content.map((c) => c.text).join('\n');
    assert.match(mailText, /Queue message from Grok session "Waiter" .*: "need the server for 15 min"/);
    assert.match(mailText, /1 waiting: Grok session "Waiter".*needs server for 15 min/);
    const reply = await client.callTool({ name: 'progenitor_queue', arguments: { action: 'message', text: 'done in 5' } });
    assert.match(reply.content[0].text, /Sent to Grok session "Waiter"/);
    assert.match(otherQueue.notes(waiter), /Queue message from Claude session "Renamed".*"done in 5"/);
    otherQueue.leave(waiter);

    const reloaded = await client.callTool({ name: 'progenitor_reload', arguments: { side: 'server' } });
    assert.ok(!reloaded.isError);
    assert.deepEqual(requests.slice(-2).map((r) => r.op), ['reload', 'ping'], 'a reload is followed by one ping that records what is loaded');
    const status = await client.callTool({ name: 'progenitor_queue', arguments: {} });
    assert.match(status.content[0].text, /server: Core 2026\.10\.3\+fedcba9 gen 7 \(sha cccccccc\)/, 'the earlier ping was recorded');
    assert.match(status.content[0].text, /revision fedcba9 is not in this checkout/);
    const left = await client.callTool({ name: 'progenitor_queue', arguments: { action: 'leave' } });
    assert.match(left.content[0].text, /^Released\./);
    const hist = await client.callTool({ name: 'progenitor_queue', arguments: { action: 'history' } });
    assert.match(hist.content[0].text, /server: Claude session "Renamed".*queue check/);
    assert.match(hist.content[0].text, /loaded at start: Core 2026\.10\.3\+fedcba9 gen 7/);
  } finally {
    await client.close().catch(() => {});
    srv.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
