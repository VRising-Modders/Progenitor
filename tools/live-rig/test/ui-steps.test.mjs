import test from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { createTracker } from '../lib/tracker.mjs';
import fs, { mkdirSync, writeFileSync } from 'node:fs';
import {
  executeStep,
  STEP_KINDS,
  matchExpect,
  getByPath,
  expandArgRefs,
} from '../lib/steps.mjs';
import { MAIN_THREAD_OPS, SOCKET_THREAD_OPS, isMainThreadOp, isSocketThreadOp } from '../lib/bridge.mjs';
import { fakeClock } from '../lib/clock.mjs';
import { applyOpenSitePlaceholder, resolveRunSite, CASE_WORLD_CONNECTED, CASE_WORLD_MAIN_MENU } from '../lib/run.mjs';
import { createFakeWorld, tempDir, testBridge, TOOL_DIR } from './helpers.mjs';

function ctxFor(tmp, world, extra = {}) {
  const clock = extra.clock ?? fakeClock();
  const bridge = extra.bridge ?? testBridge(tmp.dir, world);
  const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
  return {
    bridge,
    tracker,
    clock,
    side: extra.side ?? 'client',
    user: 'TestVampire',
    records: extra.records ?? {},
    findings: [],
    stepName: 'test',
    caseSite: extra.caseSite,
    outDir: extra.outDir ?? tmp.dir,
    ...extra,
  };
}

function timeoutPing(world, { after = 1, advancing = false } = {}) {
  const orig = world.request.bind(world);
  let pings = 0;
  let failed = false;
  world.request = async (side, op, args = {}) => {
    if (op === 'logs' && failed && advancing) {
      world.pushLog(side, 'Sample: heartbeat', { source: 'Progenitor Sample' });
    }
    if (op === 'ping') {
      pings += 1;
      if (pings >= after) {
        failed = true;
        const err = new Error(`${side} bridge timed out on 'ping' after 1 ms`);
        err.timeout = true;
        throw err;
      }
    }
    return orig(side, op, args);
  };
  return {
    get pings() { return pings; },
    get failed() { return failed; },
    stopTimeout() { after = Number.POSITIVE_INFINITY; },
  };
}

test('STEP_KINDS includes op, recover, and soak but not consentClose', () => {
  assert.ok(STEP_KINDS.includes('op'));
  assert.ok(STEP_KINDS.includes('recover'));
  assert.ok(STEP_KINDS.includes('soak'));
  assert.equal(STEP_KINDS.includes('consentClose'), false);
});

test('matchExpect path map pass and fail, and regex on JSON', () => {
  const result = { ok: true, action: 'status', after: { open: false, paneCount: 0 } };
  assert.equal(matchExpect({ ok: true, 'after.open': false }, result).ok, true);
  const fail = matchExpect({ 'after.open': true }, result);
  assert.equal(fail.ok, false);
  assert.match(fail.error, /after\.open/);
  assert.equal(matchExpect('"action":"status"', result).ok, true);
  assert.equal(matchExpect('missing-token', result).ok, false);
  assert.equal(getByPath(result, 'after.paneCount'), 0);
});

test('op step expect pass records the result and fail does not pass', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    const pass = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
      record: 'status0',
      expect: { ok: true, action: 'status', 'after.open': false },
    }, ctx);
    assert.equal(pass.ok, true, pass.error);
    assert.equal(ctx.records.status0.after.open, false);

    const fail = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
      expect: { 'after.open': true },
    }, ctx);
    assert.equal(fail.ok, false);
    assert.match(String(fail.error), /after\.open/);

    const regexPass = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'ping',
      expect: '"side":"client"',
    }, ctx);
    assert.equal(regexPass.ok, true, regexPass.error);
  } finally {
    tmp.cleanup();
  }
});

test('expandArgRefs reads @record paths', () => {
  const ctx = { records: { baselineResolution: { after: { width: 3840, height: 1080, mode: 'fullscreenwindow' } } } };
  const args = expandArgRefs({
    action: 'resolution',
    width: '@record:baselineResolution.after.width',
    height: '@record:baselineResolution.after.height',
    mode: '@record:baselineResolution.after.mode',
  }, ctx);
  assert.equal(args.width, 3840);
  assert.equal(args.height, 1080);
  assert.equal(args.mode, 'fullscreenwindow');
});

test('main-thread timeout marks the side suspect; later main-thread steps fail fast; logs continue', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  timeoutPing(world, { after: 1 });
  try {
    const ctx = ctxFor(tmp, world);
    const ping = await executeStep({ kind: 'op', side: 'client', op: 'ping' }, ctx);
    assert.equal(ping.ok, false);
    assert.equal(ping.timeout, true);
    const held = ctx.bridge.mainThreadSuspect('client');
    assert.ok(held);
    assert.equal(held.op, 'ping');
    assert.ok(Number.isFinite(held.id));

    const before = world.calls.length;
    const blocked = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
    }, ctx);
    assert.equal(blocked.ok, false);
    assert.equal(blocked.suspect, true);
    assert.match(String(blocked.error), /main thread suspect since .+ after ping id \d+/);
    assert.equal(world.calls.length, before, 'no further main-thread op was sent');

    const logs = await ctx.bridge.call('client', 'logs', { since: -1, max: 1, filter: '' });
    assert.ok(logs);
    assert.equal(typeof logs.latestSeq, 'number');
    const ops = await ctx.bridge.call('client', 'ops', {});
    assert.ok(Array.isArray(ops.ops));
    const status = await ctx.bridge.call('client', 'bridge_status', {});
    assert.equal(status.entered, false);
    assert.equal(status.abandoned, false);
    assert.equal(typeof status.frame, 'number');
    assert.equal(status.lastDrainAt, null);
  } finally {
    tmp.cleanup();
  }
});

test('recover is the only way to try one bounded ping after a timeout', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const ctl = timeoutPing(world, { after: 1 });
  try {
    const ctx = ctxFor(tmp, world);
    const ping = await executeStep({ kind: 'op', side: 'client', op: 'ping' }, ctx);
    assert.equal(ping.ok, false);
    ctl.stopTimeout();

    const recovered = await executeStep({ kind: 'recover', side: 'client', timeoutMs: 1000 }, ctx);
    assert.equal(recovered.ok, true, recovered.error);
    assert.equal(recovered.recovered, true);
    assert.equal(ctx.bridge.mainThreadSuspect('client'), null);

    const after = await executeStep({ kind: 'op', side: 'client', op: 'ping', expect: { side: 'client' } }, ctx);
    assert.equal(after.ok, true, after.error);
  } finally {
    tmp.cleanup();
  }
});

test('recover ping timeout leaves the side suspect', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  timeoutPing(world, { after: 1 });
  try {
    const ctx = ctxFor(tmp, world);
    await executeStep({ kind: 'op', side: 'client', op: 'ping' }, ctx);
    const recover = await executeStep({ kind: 'recover', side: 'client', timeoutMs: 1000 }, ctx);
    assert.equal(recover.ok, false);
    assert.equal(recover.recovered, false);
    const held = ctx.bridge.mainThreadSuspect('client');
    assert.ok(held);
    assert.equal(held.op, 'ping');
  } finally {
    tmp.cleanup();
  }
});

test('soak records samples and reports logSeq advancing after a ping timeout', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const ctl = timeoutPing(world, { after: 1, advancing: true });
  try {
    const ctx = ctxFor(tmp, world);
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 2,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      capture: null,
      stopOnFirstFailure: true,
    }, ctx);
    assert.equal(result.ok, false);
    assert.ok(result.failures >= 1);
    assert.ok(result.firstFailureTime);
    assert.equal(result.logSeqAfterFailure, 'advancing');
    assert.ok(result.samples.length >= 4);
    assert.equal(result.pingStopped, true);
    assert.ok(result.samples.some((s) => s.ping && s.ping.skipped));
    assert.equal(ctl.pings, 1, 'no further pings after the timeout');
    assert.ok(world.calls.some((c) => c.op === 'logs'));
    const statusCalls = world.calls.filter((c) => c.op === 'bridge_status');
    assert.ok(statusCalls.length >= 4, `expected soak to keep sampling bridge_status, got ${statusCalls.length}`);
    assert.ok(result.samples.every((s) => Object.prototype.hasOwnProperty.call(s, 'entered')));
    assert.ok(result.samples.every((s) => Object.prototype.hasOwnProperty.call(s, 'abandoned')));
    assert.ok(result.samples.every((s) => Object.prototype.hasOwnProperty.call(s, 'frame')));
    assert.ok(result.samples.every((s) => Object.prototype.hasOwnProperty.call(s, 'lastDrainAt')));
    const afterTimeout = result.samples.filter((s) => s.ping && s.ping.skipped);
    assert.ok(afterTimeout.length >= 1);
    assert.ok(afterTimeout.every((s) => s.bridge_status || s.bridgeStatusError));
    assert.match(String(result.error), /logSeq advancing after the failure/);
  } finally {
    tmp.cleanup();
  }
});

test('soak reports logSeq frozen after a ping timeout when logs do not advance', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  timeoutPing(world, { after: 1, advancing: false });
  try {
    const ctx = ctxFor(tmp, world);
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 2,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      capture: null,
      stopOnFirstFailure: true,
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.logSeqAfterFailure, 'frozen');
    assert.match(String(result.error), /logSeq frozen after the failure/);
  } finally {
    tmp.cleanup();
  }
});

function holdFakeFrame(world, frame = 42) {
  world.setBridgeFrame(frame);
  world.freezeBridgeFrame(true);
}

function unfreezeAfterStatusCalls(world, afterCalls) {
  const orig = world.request.bind(world);
  let n = 0;
  world.request = async (side, op, args) => {
    const result = await orig(side, op, args);
    if (op === 'bridge_status') {
      n += 1;
      if (n >= afterCalls) world.freezeBridgeFrame(false);
    }
    return result;
  };
}

test('soak detects a freeze episode, requests dump once, and records recovery', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  holdFakeFrame(world, 42);
  unfreezeAfterStatusCalls(world, 5);
  try {
    const ctx = ctxFor(tmp, world);
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 1,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      capture: null,
      dumpAfterSeconds: 15,
      failAfterSeconds: 90,
      stopOnFirstFailure: true,
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.freezes.length, 1);
    const episode = result.freezes[0];
    assert.equal(episode.frame, 42);
    assert.equal(episode.dumpRequested, true);
    assert.equal(episode.dumpSuppressed, false);
    assert.equal(episode.dump?.reason, 'rig freeze');
    assert.ok(episode.recoverySample);
    assert.ok(Number.isFinite(episode.mainThreadFrozenMs));
    assert.ok(episode.mainThreadFrozenMs >= 15000);
    assert.equal(world.dumpCalls.length, 1);
    assert.equal(world.dumpCalls[0].args.reason, 'rig freeze');
    const pings = world.calls.filter((c) => c.op === 'ping');
    assert.ok(pings.length >= 2, 'ping at start and again after recovery');
    const frozenSamples = result.samples.filter((s) => s.mainThreadFrozenSince);
    assert.ok(frozenSamples.length >= 1);
    const recovered = result.samples.filter((s) => s.mainThreadFrozenMs != null);
    assert.ok(recovered.length >= 1);
  } finally {
    tmp.cleanup();
  }
});

test('soak suppresses the rig dump when bridge_status.lastFreezeDump is newer than the freeze start', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  holdFakeFrame(world, 42);
  world.setLastFreezeDump(new Date(1).toISOString());
  try {
    const ctx = ctxFor(tmp, world);
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 1,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      capture: null,
      dumpAfterSeconds: 15,
      failAfterSeconds: 90,
    }, ctx);
    assert.equal(world.dumpCalls.length, 0, 'product watchdog dump must suppress the rig dump');
    assert.ok(result.freezes.length >= 1);
    assert.equal(result.freezes[0].dumpSuppressed, true);
    assert.equal(result.freezes[0].dumpRequested, false);
    assert.equal(result.freezes[0].dump, null);
  } finally {
    tmp.cleanup();
  }
});

test('soak fails when a freeze lasts longer than failAfterSeconds and preserves evidence', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  holdFakeFrame(world, 7);
  try {
    const ctx = ctxFor(tmp, world);
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 1,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      capture: null,
      dumpAfterSeconds: 15,
      failAfterSeconds: 25,
      stopOnFirstFailure: true,
    }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /main thread frozen/);
    assert.match(String(result.error), /failAfterSeconds 25/);
    assert.equal(result.freezes.length, 1);
    assert.equal(result.freezes[0].dumpRequested, true);
    assert.equal(world.dumpCalls.length, 1);
    assert.equal(world.dumpCalls[0].args.reason, 'rig freeze');
    assert.ok(result.freezes[0].exceededFailAfter);
    assert.ok((result.freezes[0].mainThreadFrozenMs ?? 0) > 25000);
    assert.ok(result.samples.length >= 4, 'evidence samples are preserved');
    assert.ok(result.samples.some((s) => s.bridge_status));
    const pingsAfterStart = world.calls.filter((c) => c.op === 'ping');
    assert.equal(pingsAfterStart.length, 1, 'pings stop while frozen so they do not occupy the gate');
  } finally {
    tmp.cleanup();
  }
});

test('the main-thread op table lists ping/mods_ui and treats logs/ops/bridge_status as socket-thread', () => {
  assert.ok(MAIN_THREAD_OPS.has('ping'));
  assert.ok(MAIN_THREAD_OPS.has('mods_ui'));
  assert.ok(MAIN_THREAD_OPS.has('command'));
  assert.ok(MAIN_THREAD_OPS.has('entity'));
  assert.ok(MAIN_THREAD_OPS.has('reload'));
  assert.ok(MAIN_THREAD_OPS.has('core_reload'));
  assert.ok(MAIN_THREAD_OPS.has('reload_status'));
  assert.ok(MAIN_THREAD_OPS.has('users'));
  assert.ok(MAIN_THREAD_OPS.has('connect'));
  assert.ok(MAIN_THREAD_OPS.has('disconnect'));
  assert.ok(MAIN_THREAD_OPS.has('quit'));
  assert.ok(MAIN_THREAD_OPS.has('perf'));
  assert.ok(MAIN_THREAD_OPS.has('exec'));
  assert.ok(SOCKET_THREAD_OPS.has('logs'));
  assert.ok(SOCKET_THREAD_OPS.has('ops'));
  assert.ok(SOCKET_THREAD_OPS.has('bridge_status'));
  assert.ok(SOCKET_THREAD_OPS.has('dump'));
  assert.equal(isSocketThreadOp('logs'), true);
  assert.equal(isSocketThreadOp('bridge_status'), true);
  assert.equal(isSocketThreadOp('dump'), true);
  assert.equal(isMainThreadOp('logs'), false);
  assert.equal(isMainThreadOp('bridge_status'), false);
  assert.equal(isMainThreadOp('dump'), false);
  assert.equal(isMainThreadOp('mods_ui'), true);
  assert.equal(isMainThreadOp('unknown_future_op'), true);
});

test('matchExpect neq rejects an unchanged value and passes a moved one', () => {
  assert.equal(matchExpect({ generation: { neq: 4 } }, { generation: 4 }).ok, false);
  assert.equal(matchExpect({ generation: { neq: 4 } }, { generation: 5 }).ok, true);
  assert.match(matchExpect({ generation: { neq: 4 } }, { generation: 4 }).error, /wanted not 4/);
});

test('op stamps since on the record so a later count log can anchor before the call', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.pushLog('client', 'before');
  try {
    const ctx = ctxFor(tmp, world);
    const click = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
      record: 'click0',
      expect: { ok: true },
    }, ctx);
    assert.equal(click.ok, true, click.error);
    assert.equal(typeof ctx.records.click0.since, 'number');
    world.pushLog('client', 'Sample menu: action handled.');
    const counted = await executeStep({
      kind: 'log',
      side: 'client',
      filter: 'Sample menu',
      regex: 'Sample menu: action handled',
      count: 1,
      sinceRecord: 'click0',
      timeoutMs: 50,
    }, ctx);
    assert.equal(counted.ok, true, counted.error);
    assert.equal(counted.count, 1);
  } finally {
    tmp.cleanup();
  }
});

test('op expands @record in expect and polls until the path matches', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  let pings = 0;
  const orig = world.request.bind(world);
  world.request = async (side, op, args = {}) => {
    const msg = await orig(side, op, args);
    if (op === 'ping') {
      pings += 1;
      const worlds = pings < 3 ? ['Default'] : ['Client_0'];
      return { ok: true, result: { ...msg.result, worlds, generation: pings < 3 ? 4 : 5 } };
    }
    return msg;
  };
  try {
    const ctx = ctxFor(tmp, world);
    const before = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'ping',
      record: 'genBefore',
    }, ctx);
    assert.equal(before.ok, true, before.error);
    assert.equal(ctx.records.genBefore.generation, 4);
    const polled = await executeStep({
      kind: 'op',
      name: 'wait generation moved',
      side: 'client',
      op: 'ping',
      pollMs: 10,
      timeoutMs: 1000,
      record: 'genAfter',
      expect: {
        'worlds.0': 'Client_0',
        generation: { neq: '@record:genBefore.generation' },
      },
    }, ctx);
    assert.equal(polled.ok, true, polled.error);
    assert.ok(polled.polls >= 2, `expected at least 2 polls, got ${polled.polls}`);
    assert.equal(pings, 3);
    assert.equal(ctx.records.genAfter.generation, 5);
    assert.equal(typeof polled.elapsedMs, 'number');
    assert.ok(polled.elapsedMs >= 10, `expected poll latency, got ${polled.elapsedMs}`);
  } finally {
    tmp.cleanup();
  }
});

test('op poll {intervalMs, timeoutMs} retries until expect holds and records elapsedMs', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  let pings = 0;
  const orig = world.request.bind(world);
  world.request = async (side, op, args = {}) => {
    const msg = await orig(side, op, args);
    if (op === 'ping') {
      pings += 1;
      const worlds = pings < 3 ? ['Default'] : ['Client_0'];
      return { ok: true, result: { ...msg.result, worlds } };
    }
    return msg;
  };
  try {
    const ctx = ctxFor(tmp, world);
    const polled = await executeStep({
      kind: 'op',
      name: 'wait Client_0',
      side: 'client',
      op: 'ping',
      poll: { intervalMs: 10, timeoutMs: 1000 },
      record: 'joined',
      expect: { 'worlds.0': 'Client_0' },
    }, ctx);
    assert.equal(polled.ok, true, polled.error);
    assert.ok(polled.polls >= 2, `expected at least 2 polls, got ${polled.polls}`);
    assert.equal(typeof polled.elapsedMs, 'number');
    assert.ok(polled.elapsedMs >= 10, `expected recorded latency, got ${polled.elapsedMs}`);
    assert.equal(ctx.records.joined.elapsedMs, polled.elapsedMs);
    assert.equal(ctx.records.joined.worlds[0], 'Client_0');
  } finally {
    tmp.cleanup();
  }
});

async function openServerSettings(ctx) {
  const open = await executeStep({
    kind: 'op',
    side: 'client',
    op: 'mods_ui',
    args: { action: 'open', site: 'top-tab' },
    expect: { ok: true, 'after.open': true },
  }, ctx);
  assert.equal(open.ok, true, open.error);
  const select = await executeStep({
    kind: 'op',
    side: 'client',
    op: 'mods_ui',
    args: { action: 'select', plugin: 'progenitor.sample', page: 'settings', group: 'Server' },
    expect: { ok: true, 'after.pageId': 'Server' },
  }, ctx);
  assert.equal(select.ok, true, select.error);
}

test('fake Apply ExampleLimit serves Waiting first then the async refusal note', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    await openServerSettings(ctx);
    const typed = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'widget_click', pane: 'content', index: 3, value: '12' },
      expect: { ok: true },
    }, ctx);
    assert.equal(typed.ok, true, typed.error);
    const applied = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'widget_click', pane: 'content', label: 'Apply ExampleLimit' },
      expect: { ok: true },
    }, ctx);
    assert.equal(applied.ok, true, applied.error);

    const immediate = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'widget_read', pane: 'content' },
      expect: 'Only server administrators can change this setting',
    }, ctx);
    assert.equal(immediate.ok, false, 'first read must still be Waiting for the server');
    assert.match(String(immediate.error), /Waiting for the server/);
  } finally {
    tmp.cleanup();
  }
});

test('fake Apply ExampleLimit poll waits for the async refusal note and records latency', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    await openServerSettings(ctx);
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'widget_click', pane: 'content', index: 3, value: '12' },
      expect: { ok: true },
    }, ctx);
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'widget_click', pane: 'content', label: 'Apply ExampleLimit' },
      expect: { ok: true },
    }, ctx);
    const polled = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'widget_read', pane: 'content' },
      poll: { intervalMs: 500, timeoutMs: 10000 },
      record: 'nonAdminRefusal',
      expect: 'Only server administrators can change this setting',
    }, ctx);
    assert.equal(polled.ok, true, polled.error);
    assert.ok(polled.polls >= 2, `expected a retry after Waiting, got ${polled.polls} polls`);
    assert.ok(polled.elapsedMs >= 500, `expected recorded latency, got ${polled.elapsedMs}`);
    assert.equal(ctx.records.nonAdminRefusal.elapsedMs, polled.elapsedMs);
  } finally {
    tmp.cleanup();
  }
});

test('log count 0 passes when no matching line arrives in the window', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.pushLog('client', 'unrelated');
  try {
    const ctx = ctxFor(tmp, world);
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
      record: 'beforeCount0',
    }, ctx);
    const none = await executeStep({
      kind: 'log',
      side: 'client',
      filter: 'ServerSettings',
      regex: 'ServerSettings callback:|dead widget',
      count: 0,
      sinceRecord: 'beforeCount0',
      timeoutMs: 20,
    }, ctx);
    assert.equal(none.ok, true, none.error);
    assert.equal(none.count, 0);
  } finally {
    tmp.cleanup();
  }
});

test('fake mods_ui refuses closed mutations with workspace closed and echoes resolution', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    const closed = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'select', plugin: 'progenitor.sample', page: 'settings', group: 'Server' },
      expect: { ok: false, 'detail.error': 'workspace closed' },
    }, ctx);
    assert.equal(closed.ok, true, closed.error);

    const baseline = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
      record: 'baseline',
      expect: { ok: true, 'after.open': false, 'after.modsCapture': false, 'after.panes': 0 },
    }, ctx);
    assert.equal(baseline.ok, true, baseline.error);
    assert.equal(ctx.records.baseline.after.width, 1920);

    const setRes = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: {
        action: 'resolution',
        width: '@record:baseline.after.width',
        height: 720,
        mode: '@record:baseline.after.mode',
      },
      expect: { ok: true, 'after.width': 1920, 'after.height': 720, 'after.mode': 'window' },
    }, ctx);
    assert.equal(setRes.ok, true, setRes.error);

    const open = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'main-menu' },
      expect: { ok: true, 'after.open': true, 'after.curtain': true, 'after.panes': 2, 'after.observers': 2, 'after.modsCapture': true, 'after.inputCapture': true },
    }, ctx);
    assert.equal(open.ok, true, open.error);

    const back = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'select', plugin: 'progenitor.sample', page: 'custom', group: 'Example' },
      expect: { ok: true, 'after.page': 'custom' },
    }, ctx);
    assert.equal(back.ok, true, back.error);
    const backed = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'back' },
      expect: { ok: true, 'after.open': true, 'after.page': 'overview' },
    }, ctx);
    assert.equal(backed.ok, true, backed.error);

    const close = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'close' },
      expect: {
        ok: true,
        'after.open': false,
        'after.modsCapture': false,
        'after.panes': '@record:baseline.after.panes',
      },
    }, ctx);
    assert.equal(close.ok, true, close.error);
    assert.equal(ctx.records.baseline.after.modsCapture, false);
    assert.equal(ctx.records.baseline.after.inputCapture, false);
    assert.equal(ctx.records.baseline.after.capture, false);
  } finally {
    tmp.cleanup();
  }
});

test('fake mods_ui open top-tab records menuOpenedBy and can refuse when the button is not attached', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    const open = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'top-tab' },
      record: 'topTabOpen',
      expect: { ok: true, 'after.open': true, 'after.modsCapture': true, 'detail.menuOpenedBy': { neq: null } },
    }, ctx);
    assert.equal(open.ok, true, open.error);
    assert.equal(ctx.records.topTabOpen.detail.menuOpenedBy, 'open');

    const again = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'top-tab' },
      record: 'topTabAgain',
      expect: { ok: true, 'after.open': true, 'detail.alreadyOpen': true, 'detail.menuOpenedBy': 'already' },
    }, ctx);
    assert.equal(again.ok, true, again.error);
  } finally {
    tmp.cleanup();
  }

  const refusedTmp = tempDir();
  const refusedWorld = createFakeWorld({ modsUiRefuseTopTabAttach: true });
  try {
    const ctx = ctxFor(refusedTmp, refusedWorld);
    const refused = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'top-tab' },
      expect: { ok: false, 'detail.error': 'top-tab button not attached' },
    }, ctx);
    assert.equal(refused.ok, true, refused.error);
  } finally {
    refusedTmp.cleanup();
  }
});

test('fake mods_ui keeps global inputCapture when HUD is capturing and Mods is closed', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ modsUiHudCapture: true });
  try {
    const ctx = ctxFor(tmp, world);
    const closed = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
      record: 'hudClosed',
      expect: { ok: true, 'after.open': false, 'after.modsCapture': false, 'after.inputCapture': true, 'after.capture': true },
    }, ctx);
    assert.equal(closed.ok, true, closed.error);

    const opened = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'top-tab' },
      expect: { ok: true, 'after.modsCapture': true, 'after.inputCapture': true, 'detail.menuOpenedBy': { neq: null } },
    }, ctx);
    assert.equal(opened.ok, true, opened.error);

    const released = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'close' },
      expect: { ok: true, 'after.open': false, 'after.modsCapture': false, 'after.inputCapture': true, 'after.capture': true },
    }, ctx);
    assert.equal(released.ok, true, released.error);
  } finally {
    tmp.cleanup();
  }
});

test('fake mods_ui status reports the workspace never suspended and no consent prompt', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    const opened = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'top-tab' },
      expect: { ok: true, 'after.open': true, 'after.suspended': false, 'after.suspendReason': null },
    }, ctx);
    assert.equal(opened.ok, true, opened.error);
    assert.equal(Object.prototype.hasOwnProperty.call(opened.result.after, 'consentPromptOpen'), false);
    const selected = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'select', plugin: 'progenitor.sample', page: 'settings', group: 'Server' },
      expect: { ok: true, 'after.page': 'settings' },
    }, ctx);
    assert.equal(selected.ok, true, selected.error);
    const rects = await executeStep({ kind: 'op', side: 'client', op: 'mods_ui', args: { action: 'rects' } }, ctx);
    assert.equal(rects.ok, true, rects.error);
    assert.equal(rects.result.detail.rects.some((row) => row.id === 'consent' || row.occludedBy === 'consent'), false);
  } finally {
    tmp.cleanup();
  }
});

test('consentClose is no longer a step kind', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    await assert.rejects(
      executeStep({ kind: 'consentClose', side: 'client' }, ctxFor(tmp, world)),
      /unknown step kind 'consentClose'/,
    );
  } finally {
    tmp.cleanup();
  }
});

const CONTRACT_CASES = [
  'ui-baseline',
  'ui-open-close-cycles',
  'ui-list-and-search',
  'ui-resolutions',
  'ui-native-tab-return',
  'ui-soak-menu',
  'ui-soak-settings',
  'ui-settings',
];

test('cases-ui contract cases run against the fake mods_ui world', async () => {
  const dir = path.join(TOOL_DIR, 'cases-ui');
  for (const name of CONTRACT_CASES) {
    const doc = JSON.parse(readFileSync(path.join(dir, `${name}.json`), 'utf8'));
    const tmp = tempDir();
    const world = createFakeWorld();
    const detected = doc.world === 'main-menu' ? CASE_WORLD_MAIN_MENU : CASE_WORLD_CONNECTED;
    const site = resolveRunSite({}, detected, doc);
    try {
      const ctx = ctxFor(tmp, world, { caseSite: site, caseDoc: doc });
      for (const step of [...(doc.steps ?? []), ...(doc.teardown ?? [])]) {
        if (step.kind === 'capture' || step.kind === 'capturePrepare' || step.kind === 'snippetPrepare') continue;
        const run = step.kind === 'soak' ? { ...step, capture: null, minutes: Math.min(Number(step.minutes) || 1, 2) } : step;
        const result = await executeStep(applyOpenSitePlaceholder(run, site), ctx);
        assert.equal(result.ok, true, `${name} ${step.name ?? step.kind}: ${result.error}`);
      }
    } finally {
      tmp.cleanup();
    }
  }
  assert.ok(readdirSync(dir).some((f) => f.endsWith('.json')));
});
