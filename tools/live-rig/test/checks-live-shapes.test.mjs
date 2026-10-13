// Shapes from the first qualification run (2026-09-07), with the Core `Debug: ` prefix:
// the readback line plus a Sample chat-echo line for `.debug status`, and the game spawns a
// short-lived NetworkedSequence carrier at a spawn point that disappears on its own.
import test from 'node:test';
import assert from 'node:assert/strict';
import { protection, carriers } from '../lib/checks.mjs';
import { fakeClock } from '../lib/clock.mjs';

const STATUS = 'Debug: status character=331182:1 buff=566718:45 flags=7 immortal=True guard=True prefixes=3/3 drain=True aggro=True persists=True reconcileGaveUpGrants=0 reconcileGaveUpCharacters=(none) reconcilePassGaveUp=False reconcileThrowingPasses=0';
const ECHO = 'Sample chat: saw type=Local from=TestPlayer handled=True text=".debug status"';

function stubBridge(handlers) {
  const calls = [];
  return {
    calls,
    async call(side, op, args) {
      calls.push({ side, op, args });
      const h = handlers[op];
      if (!h) throw new Error('unexpected op ' + op);
      return h(side, args, calls);
    },
  };
}

test('protection reads the flags= line even when the chat echo follows it in the same poll', async () => {
  let seq = 100;
  const bridge = stubBridge({
    ping: () => ({ logSeq: seq }),
    command: () => { seq += 2; return { injectedAs: 'TestPlayer' }; },
    logs: () => ({ latestSeq: seq, lines: [{ seq: seq - 1, text: STATUS }, { seq, text: ECHO }] }),
  });
  const r = await protection({ bridge, user: 'TestPlayer', clock: fakeClock(), timeoutMs: 1000 });
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal(r.value.flags, 7);
  assert.match(String(r.evidence), /flags=7/);
});

test('protection fails, not throws, when only the chat echo is logged', async () => {
  let seq = 100;
  const bridge = stubBridge({
    ping: () => ({ logSeq: seq }),
    command: () => { seq += 1; return {}; },
    logs: () => ({ latestSeq: seq, lines: [{ seq, text: ECHO }] }),
  });
  const r = await protection({ bridge, user: 'TestPlayer', clock: fakeClock(), timeoutMs: 300 });
  assert.equal(r.ok, false);
});

test('carriers waits a bounded time for a transient carrier to expire, then passes', async () => {
  let queries = 0;
  const bridge = stubBridge({
    query: (side) => {
      queries++;
      const linger = queries <= 4; // first two polls (both sides) still see the spawn carrier
      return linger
        ? { total: 1, count: 1, entities: [{ entity: '71481:414', prefab: { name: 'NetworkedSequence' } }] }
        : { total: 0, count: 0, entities: [] };
    },
  });
  const r = await carriers({ bridge, position: { x: 0, y: 0, z: 0 }, expected: { client: [], server: [] }, clock: fakeClock(), settleMs: 10000, pollMs: 500 });
  assert.equal(r.ok, true, JSON.stringify(r.value));
  assert.ok(r.value.polls >= 3);
});

test('carriers reports the leftover entities when the id set never settles', async () => {
  const bridge = stubBridge({
    query: () => ({ total: 1, count: 1, entities: [{ entity: '9:9', prefab: { name: 'NetworkedSequence' } }] }),
  });
  const r = await carriers({ bridge, position: { x: 0, y: 0, z: 0 }, expected: { client: [], server: [] }, clock: fakeClock(), settleMs: 2000, pollMs: 500 });
  assert.equal(r.ok, false);
  assert.equal(r.value.leftover.server[0].entity, '9:9');
  assert.ok(r.value.elapsedMs >= 2000);
});

test('carriers fails when the id set differs even if the count matches', async () => {
  const bridge = stubBridge({
    query: (side) => (side === 'client'
      ? { total: 1, count: 1, entities: [{ entity: '2:2', prefab: { name: 'NetworkedSequence' } }] }
      : { total: 0, count: 0, entities: [] }),
  });
  const r = await carriers({
    bridge,
    position: { x: 0, y: 0, z: 0 },
    expected: { client: ['1:1'], server: [] },
    clock: fakeClock(),
    settleMs: 2000,
    pollMs: 500,
  });
  assert.equal(r.ok, false);
  assert.equal(r.value.leftover.client[0].entity, '2:2');
  assert.deepEqual(r.value.ids.client, ['2:2']);
});
