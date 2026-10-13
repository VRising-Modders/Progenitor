import test from 'node:test';
import assert from 'node:assert/strict';
import {
  isExecPreflightStall,
  snapshotAfterExecPreflight,
  formatExecPreflightStall,
  EXEC_PREFLIGHT_LOG_FILTER,
} from '../exec-preflight.mjs';

test('ping timeout and already-running are preflight stalls', () => {
  assert.equal(isExecPreflightStall(new Error("client bridge timed out on 'ping'")), true);
  assert.equal(isExecPreflightStall(new Error('a main-thread op is already running; retry after it finishes')), true);
  assert.equal(isExecPreflightStall(new Error('client bridge not reachable')), false);
  assert.equal(isExecPreflightStall(new Error("server bridge timed out on 'reload'")), false);
});

test('snapshot retries bridge_status once and never a mutation', async () => {
  const calls = [];
  const snapshot = await snapshotAfterExecPreflight(
    async () => {
      calls.push('status');
      if (calls.filter((c) => c === 'status').length === 1) throw new Error('status down');
      return { entered: true, op: 'ping', id: 1 };
    },
    async () => {
      calls.push('logs');
      return { latestSeq: 9, lines: [{ text: 'tick hb frame=1 drainMs=0 queued=0 delayed=0 gate=busy:ping#1' }] };
    },
  );
  assert.deepEqual(calls, ['status', 'status', 'logs']);
  assert.equal(snapshot.statusError, null);
  assert.equal(snapshot.status.op, 'ping');
  assert.equal(snapshot.logs.latestSeq, 9);
});

test('status retry is bounded at one and logs still run after both status failures', async () => {
  let statusCalls = 0;
  let logCalls = 0;
  const snapshot = await snapshotAfterExecPreflight(
    async () => {
      statusCalls += 1;
      throw new Error('status down');
    },
    async () => {
      logCalls += 1;
      return { latestSeq: 1, lines: [] };
    },
  );
  assert.equal(statusCalls, 2);
  assert.equal(logCalls, 1);
  assert.equal(snapshot.status, null);
  assert.match(snapshot.statusError, /status down/);
  assert.equal(snapshot.logs.latestSeq, 1);
});

test('stall payload names bridge_status as the liveness probe and does not mention retrying ping', () => {
  const text = formatExecPreflightStall(
    new Error("client bridge timed out on 'ping'"),
    { status: { entered: true, op: 'ping' }, logs: { latestSeq: 3 } },
  );
  assert.match(text, /not sending another main-thread op/);
  assert.match(text, /bridge_status/);
  assert.doesNotMatch(text, /retry.*ping/i);
  const parsed = JSON.parse(text);
  assert.equal(parsed.bridge_status.op, 'ping');
});

test('log filter matches the diagnosis soak', () => {
  assert.equal(EXEC_PREFLIGHT_LOG_FILTER, 'tick hb|bridge gate|scratch:|MainThread');
});
