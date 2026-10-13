import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createBridge, isBridgeDownError, BridgeError } from '../lib/bridge.mjs';
import { fakeClock } from '../lib/clock.mjs';
import { createFakeWorld, tempDir, testBridge } from './helpers.mjs';

function refused(code = 'ECONNREFUSED') {
  const err = new Error(`connect ${code} 127.0.0.1:27341`);
  err.code = code;
  return err;
}

test('performance progress bypasses a pending main-thread request', async () => {
  const tmp = tempDir();
  let release;
  const pending = new Promise((resolve) => { release = resolve; });
  const bridge = createBridge({
    runDir: tmp.dir,
    transport: { request: async (_side, op) => op === 'ping'
      ? pending : { ok: true, result: { running: true } } },
  });
  const ping = bridge.call('client', 'ping');
  try {
    const progress = await Promise.race([
      bridge.call('client', 'perf_status'),
      new Promise((_, reject) => {
        const timer = setTimeout(() => reject(new Error('progress queued behind ping')), 1000);
        timer.unref();
      }),
    ]);
    assert.equal(progress.running, true);
  } finally {
    release({ ok: true, result: {} });
    await ping;
    tmp.cleanup();
  }
});

function readTrace(dir) {
  const raw = readFileSync(path.join(dir, 'trace.jsonl'), 'utf8').trim();
  if (!raw) return [];
  return raw.split('\n').map((l) => JSON.parse(l));
}

test('a token path under C:\\VRisingServer is refused', () => {
  const tmp = tempDir();
  try {
    assert.throws(
      () => createBridge({
        runDir: tmp.dir,
        transport: { request: async () => ({ ok: true, result: {} }) },
        sides: {
          client: { port: 1, tokenFile: 'C:\\tmp\\progenitor-dev.token' },
          server: { port: 2, tokenFile: 'C:\\VRisingServer\\BepInEx\\config\\progenitor-dev.token' },
        },
      }),
      /live dedicated server/,
    );
  } finally {
    tmp.cleanup();
  }
});

test('the trace file receives a request line and a response/error line per call, sharing an id', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const bridge = testBridge(tmp.dir, world);
    await bridge.call('server', 'ping');
    await bridge.call('server', 'players');
    await assert.rejects(() => bridge.call('server', 'nope'));
    const rows = readTrace(tmp.dir);
    assert.equal(rows.length, 6);
    assert.equal(rows[0].type, 'request');
    assert.equal(rows[0].op, 'ping');
    assert.equal(rows[0].side, 'server');
    assert.ok(rows[0].id);
    assert.equal(rows[1].type, 'response');
    assert.equal(rows[1].id, rows[0].id);
    assert.equal(rows[1].ok, true);
    assert.ok(rows[1].result);
    assert.equal(rows[2].type, 'request');
    assert.equal(rows[2].op, 'players');
    assert.equal(rows[3].type, 'response');
    assert.equal(rows[3].id, rows[2].id);
    assert.equal(rows[4].type, 'request');
    assert.equal(rows[4].op, 'nope');
    assert.equal(rows[5].type, 'error');
    assert.equal(rows[5].id, rows[4].id);
    assert.equal(rows[5].ok, false);
    assert.match(String(rows[5].error), /unknown op/);
  } finally {
    tmp.cleanup();
  }
});

test('a request line is on disk before the transport returns', async () => {
  const tmp = tempDir();
  let release;
  const hung = new Promise((resolve) => { release = resolve; });
  try {
    const bridge = createBridge({
      runDir: tmp.dir,
      transport: { request: () => hung },
    });
    const pending = bridge.call('server', 'ping');
    await new Promise((r) => setImmediate(r));
    const mid = readTrace(tmp.dir);
    assert.equal(mid.length, 1);
    assert.equal(mid[0].type, 'request');
    assert.equal(mid[0].op, 'ping');
    assert.ok(mid[0].id);
    release({ ok: true, result: { side: 'server' } });
    await pending;
    const done = readTrace(tmp.dir);
    assert.equal(done.length, 2);
    assert.equal(done[1].type, 'response');
    assert.equal(done[1].id, mid[0].id);
  } finally {
    tmp.cleanup();
  }
});

test('logs still run while a main-thread ping is in flight', async () => {
  const tmp = tempDir();
  let release;
  const hung = new Promise((resolve) => { release = resolve; });
  try {
    const bridge = createBridge({
      runDir: tmp.dir,
      transport: {
        request: (side, op) => {
          if (op === 'ping') return hung;
          return Promise.resolve({ ok: true, result: { latestSeq: 4, count: 0, lines: [] } });
        },
      },
    });
    const pending = bridge.call('client', 'ping');
    await new Promise((r) => setImmediate(r));
    const logs = await bridge.call('client', 'logs', { since: -1, max: 1 });
    assert.equal(logs.latestSeq, 4);
    release({ ok: true, result: { side: 'client' } });
    await pending;
  } finally {
    tmp.cleanup();
  }
});

test('a timed-out transport writes a timeout line sharing the request id', async () => {
  const tmp = tempDir();
  try {
    const bridge = createBridge({
      runDir: tmp.dir,
      transport: {
        request: () => {
          const err = new Error('server bridge timed out on \'ping\' after 1 ms');
          err.timeout = true;
          return Promise.reject(err);
        },
      },
    });
    await assert.rejects(() => bridge.call('server', 'ping'), /timed out/);
    const rows = readTrace(tmp.dir);
    assert.equal(rows.length, 2);
    assert.equal(rows[0].type, 'request');
    assert.equal(rows[1].type, 'timeout');
    assert.equal(rows[1].id, rows[0].id);
    assert.equal(rows[1].ok, false);
  } finally {
    tmp.cleanup();
  }
});

test('isBridgeDownError matches refusal codes and not timeouts', () => {
  const refusedConn = refused('ECONNREFUSED');
  assert.equal(isBridgeDownError(refusedConn), true);
  assert.equal(isBridgeDownError(refused('ECONNRESET')), true);
  assert.equal(isBridgeDownError(refused('EPIPE')), true);
  const byMessage = new Error('client bridge connection failed: connect ECONNREFUSED 127.0.0.1:27341');
  assert.equal(isBridgeDownError(byMessage), true);
  const timed = new BridgeError("server bridge timed out on 'ping' after 1 ms", { timeout: true });
  assert.equal(isBridgeDownError(timed), false);
  const timedFlag = new Error("server bridge timed out on 'ping' after 1 ms");
  timedFlag.timeout = true;
  timedFlag.code = 'ECONNREFUSED';
  assert.equal(isBridgeDownError(timedFlag), false);
});

test('a socket refusal is bridgeDown and does not mark the side main-thread suspect', async () => {
  const tmp = tempDir();
  let calls = 0;
  try {
    const bridge = createBridge({
      runDir: tmp.dir,
      transport: {
        request: async (_side, op) => {
          calls += 1;
          if (calls === 1) throw refused('ECONNREFUSED');
          return { ok: true, result: { side: 'client', op } };
        },
      },
    });
    await assert.rejects(
      () => bridge.call('client', 'reload_status'),
      (e) => {
        assert.equal(e.bridgeDown, true);
        assert.equal(e.timeout, false);
        assert.equal(e.suspect, false);
        assert.equal(e.code, 'ECONNREFUSED');
        return /ECONNREFUSED/.test(e.message);
      },
    );
    assert.equal(bridge.mainThreadSuspect('client'), null);
    const down = bridge.bridgeDown('client');
    assert.ok(down);
    assert.ok(down.since);
    assert.equal(down.op, 'reload_status');
    const rows = readTrace(tmp.dir);
    assert.equal(rows.length, 2);
    assert.equal(rows[1].type, 'error');
    assert.equal(rows[1].bridgeDown, true);
    assert.notEqual(rows[1].type, 'timeout');

    const ping = await bridge.call('client', 'ping');
    assert.equal(ping.side, 'client');
    assert.equal(bridge.mainThreadSuspect('client'), null);
  } finally {
    tmp.cleanup();
  }
});

test('ECONNRESET and EPIPE before a response are bridgeDown, not suspect', async () => {
  const tmp = tempDir();
  try {
    for (const code of ['ECONNRESET', 'EPIPE']) {
      const bridge = createBridge({
        runDir: tmp.dir,
        transport: {
          request: async () => {
            throw refused(code);
          },
        },
      });
      await assert.rejects(() => bridge.call('client', 'ping'), (e) => e.bridgeDown === true && e.code === code);
      assert.equal(bridge.mainThreadSuspect('client'), null);
      assert.equal(bridge.bridgeDown('client').code, code);
    }
  } finally {
    tmp.cleanup();
  }
});

test('a main-thread timeout still marks the side suspect', async () => {
  const tmp = tempDir();
  try {
    const bridge = createBridge({
      runDir: tmp.dir,
      transport: {
        request: () => {
          const err = new Error("client bridge timed out on 'ping' after 1 ms");
          err.timeout = true;
          return Promise.reject(err);
        },
      },
    });
    await assert.rejects(() => bridge.call('client', 'ping'), /timed out/);
    const held = bridge.mainThreadSuspect('client');
    assert.ok(held);
    assert.equal(held.op, 'ping');
    assert.equal(bridge.bridgeDown('client'), null);
  } finally {
    tmp.cleanup();
  }
});

test('call with tolerateBridgeDownMs retries a refusal and does not mark suspect', async () => {
  const tmp = tempDir();
  const clock = fakeClock();
  let n = 0;
  try {
    const bridge = createBridge({
      runDir: tmp.dir,
      clock,
      transport: {
        request: async () => {
          n += 1;
          if (n <= 2) throw refused('ECONNREFUSED');
          return { ok: true, result: { side: 'client', n } };
        },
      },
    });
    const result = await bridge.call('client', 'ping', {}, {
      tolerateBridgeDownMs: 1000,
      bridgeDownRetryMs: 250,
    });
    assert.equal(result.side, 'client');
    assert.equal(n, 3);
    assert.equal(bridge.mainThreadSuspect('client'), null);
    assert.ok(bridge.bridgeDown('client'));
    assert.ok(clock.now() >= 500);
  } finally {
    tmp.cleanup();
  }
});
