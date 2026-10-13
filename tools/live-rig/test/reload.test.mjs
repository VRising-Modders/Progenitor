import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { mkdirSync, writeFileSync } from 'node:fs';
import { createTracker } from '../lib/tracker.mjs';
import {
  createReloader,
  SAMPLE_DLL,
  SAMPLE_REMOVAL_MATCHERS,
  SAMPLE_SWAP_MATCHERS,
  SAMPLE_LOAD_MATCHERS,
  DEBUG_RECONCILE_MATCHERS,
  SAMPLE_SWAP_RECONCILE_SKIP_REASON,
  SAMPLE_CLIENT_READY_MATCHERS,
  isDebugReconcileReadyLine,
  isSampleClientReadyLine,
  isSampleClientSwapReadyLine,
  waitForSingleFroststyleClientLine,
  countFroststyleClientEnabledLines,
  sampleHashFromPing,
  lastReloadWasRemovalWithoutReinstall,
} from '../lib/reload.mjs';
import { fakeClock } from '../lib/clock.mjs';
import { createFakeWorld, tempDir, testBridge, SWAP_SAMPLE_HASH } from './helpers.mjs';

function memoryFs(files = {}, opts = {}) {
  const store = { ...files };
  const mtime = { ...(opts.mtime ?? {}) };
  const copies = [];
  const utimes = [];
  const defaultMtime = opts.defaultMtime ?? 1000;
  for (const p of Object.keys(store)) {
    if (mtime[p] == null) mtime[p] = defaultMtime;
  }
  const toMs = (value) => {
    if (value instanceof Date) return value.getTime();
    const n = Number(value);
    return Number.isFinite(n) ? n : defaultMtime;
  };
  return {
    store,
    mtime,
    copies,
    utimes,
    existsSync: (p) => Object.prototype.hasOwnProperty.call(store, p),
    copyFileSync: (src, dest) => {
      copies.push({ src, dest });
      store[dest] = store[src] ?? store[dest] ?? 'dll';
      mtime[dest] = mtime[src] ?? mtime[dest] ?? defaultMtime;
    },
    renameSync: (src, dest) => {
      store[dest] = store[src];
      mtime[dest] = mtime[src] ?? defaultMtime;
      delete store[src];
      delete mtime[src];
    },
    statSync: (p) => {
      if (!Object.prototype.hasOwnProperty.call(store, p)) {
        const err = new Error(`ENOENT: ${p}`);
        err.code = 'ENOENT';
        throw err;
      }
      const ms = mtime[p] ?? defaultMtime;
      return { mtimeMs: ms, mtime: new Date(ms) };
    },
    utimesSync: (p, atime, mtimeVal) => {
      utimes.push({ p, atime, mtime: mtimeVal });
      if (opts.utimesNoop) return;
      mtime[p] = toMs(mtimeVal);
    },
  };
}

function sampleLines() {
  return {
    removal: 'Reload: Progenitor.Sample.dll removed, unloading (unload reason=Removal).',
    unloaded: 'Reload: unloaded Progenitor Sample: clean',
    swap: 'Reload: Progenitor.Sample.dll changed, reloading (unload reason=Swap).',
    loaded: 'Reload: loaded Progenitor Sample 0.1.0 from Progenitor.Sample.dll.',
  };
}

test('lastReloadWasRemovalWithoutReinstall is true only until a later reinstall or swap', () => {
  assert.equal(lastReloadWasRemovalWithoutReinstall([]), false);
  assert.equal(lastReloadWasRemovalWithoutReinstall([{ ok: true, detail: { op: 'swap' } }]), false);
  assert.equal(lastReloadWasRemovalWithoutReinstall([{ ok: true, detail: { op: 'removal' } }]), true);
  assert.equal(lastReloadWasRemovalWithoutReinstall([
    { ok: true, detail: { op: 'removal' } },
    { ok: true, detail: { op: 'reinstall' } },
  ]), false);
  assert.equal(lastReloadWasRemovalWithoutReinstall([
    { ok: true, detail: { op: 'removal' } },
    { ok: false, detail: { op: 'reinstall' } },
  ]), true);
  assert.equal(lastReloadWasRemovalWithoutReinstall([
    { ok: false, detail: { op: 'removal' } },
  ]), false);
});

test('isDebugReconcileReadyLine accepts a Core pass, rejects a world deferral and the old Sample prefix', () => {
  assert.equal(isDebugReconcileReadyLine(
    'Debug: world-ready reconcile over 1 own grant(s): adopted 1, removed 0, pre-existing kept 0, unresolved 0 (unreadable 0, gave up 0), dropped 0',
  ), true);
  assert.equal(isDebugReconcileReadyLine(
    'Debug: world-ready reconcile: 0 own grant(s) recorded, nothing to do',
  ), true);
  assert.equal(isDebugReconcileReadyLine(
    'Debug: world-ready reconcile: no server world yet, deferred to the watchdog',
  ), false);
  assert.equal(isDebugReconcileReadyLine(
    'Sample: nodrain world-ready reconcile over 1 own grant(s): adopted 1, removed 0, pre-existing kept 0, unresolved 0 (unreadable 0, gave up 0), dropped 0',
  ), false);
  assert.equal(isDebugReconcileReadyLine('Reload: loaded Progenitor Sample 0.1.0 from Progenitor.Sample.dll.'), false);
  assert.equal(DEBUG_RECONCILE_MATCHERS[0](
    'Debug: world-ready reconcile over 1 own grant(s): adopted 1, removed 0, pre-existing kept 0, unresolved 0 (unreadable 0, gave up 0), dropped 0',
  ), true);
});

test('Sample matchers reject Scratch and Dev unload/load lines', () => {
  const scratchRemoval = 'Reload: Progenitor.Scratch.dll removed, unloading (unload reason=Removal).';
  const scratchUnloaded = 'Reload: unloaded Progenitor Scratch: clean';
  const scratchLoaded = 'Reload: loaded Progenitor Scratch 0.1.0 from Progenitor.Scratch.dll.';
  const devRemoval = 'Reload: Progenitor.Dev.dll removed, unloading (unload reason=Removal).';
  const devUnloaded = 'Reload: unloaded Progenitor Dev Bridge: clean';
  const s = sampleLines();
  assert.equal(SAMPLE_REMOVAL_MATCHERS[0](s.removal), true);
  assert.equal(SAMPLE_REMOVAL_MATCHERS[1](s.unloaded), true);
  assert.equal(SAMPLE_REMOVAL_MATCHERS[0](scratchRemoval), false);
  assert.equal(SAMPLE_REMOVAL_MATCHERS[1](scratchUnloaded), false);
  assert.equal(SAMPLE_REMOVAL_MATCHERS[0](devRemoval), false);
  assert.equal(SAMPLE_REMOVAL_MATCHERS[1](devUnloaded), false);
  assert.equal(SAMPLE_SWAP_MATCHERS[0](s.swap), true);
  assert.equal(SAMPLE_SWAP_MATCHERS[1](s.loaded), true);
  assert.equal(SAMPLE_SWAP_MATCHERS[0](scratchRemoval), false);
  assert.equal(SAMPLE_SWAP_MATCHERS[1](scratchLoaded), false);
  assert.equal(SAMPLE_LOAD_MATCHERS[0](s.loaded), true);
  assert.equal(SAMPLE_LOAD_MATCHERS[0](scratchLoaded), false);
});

test('reload refuses to run while a hostile entity is tracked', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  let copied = false;
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
    tracker.adopt({
      name: 'bandit',
      entity: '9:1',
      prefab: 'CHAR_Bandit_Thief',
      by: 'test',
      hostile: true,
    });
    world.entities.set('9:1', { exists: true, prefab: 'CHAR_Bandit_Thief' });
    world.sticky.add('9:1');
    const reloader = createReloader({
      bridge,
      tracker,
      clock,
      user: 'TestVampire',
      fs: {
        existsSync: () => true,
        copyFileSync: () => { copied = true; },
        renameSync: () => { copied = true; },
      },
      logTimeoutMs: 500,
    });
    await assert.rejects(() => reloader.swap('server', 'new.dll'), /hostile tracked entity/);
    assert.equal(copied, false);
    await assert.rejects(() => reloader.removal('server'), /hostile tracked entity/);
    assert.equal(copied, false);
    await assert.rejects(() => reloader.reinstall('server'), /hostile tracked entity/);
    assert.equal(copied, false);
  } finally {
    tmp.cleanup();
  }
});

test('removal fail-safe puts the hold file back when unload lines never arrive', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const folder = world.reloadFolder.server;
  const dest = path.join(folder, SAMPLE_DLL);
  const hold = dest + '.rig-hold';
  const io = memoryFs({ [dest]: 'dll' });
  world.setSamplePresent(() => io.existsSync(dest));
  const orig = world.request;
  world.request = async (side, op, args) => {
    if (op === 'reload') {
      world.pushLog(side, 'Reload: Progenitor.Scratch.dll removed, unloading (unload reason=Removal).');
      world.pushLog(side, 'Reload: unloaded Progenitor Scratch: clean');
      return { ok: true, result: { events: [], loaded: [] } };
    }
    return orig.call(world, side, op, args);
  };
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
    const reloader = createReloader({
      bridge,
      tracker,
      clock,
      user: 'TestVampire',
      fs: io,
      logTimeoutMs: 500,
    });
    const result = await reloader.removal('server');
    assert.equal(result.ok, false);
    assert.match(result.error, /timed out waiting for reload log lines/);
    assert.ok(result.reinstall);
    assert.equal(io.existsSync(dest), true);
    assert.equal(io.existsSync(hold), false);
    assert.equal(result.reinstall.ok, true);
    assert.equal(result.reinstall.failSafe, true);
    assert.ok(result.reinstall.protectionAfter);
  } finally {
    tmp.cleanup();
  }
});

test('reinstall rearms with .debug on only when the protection readback is not ok', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  const folder = world.reloadFolder.server;
  const dest = path.join(folder, SAMPLE_DLL);
  const io = memoryFs({ [dest]: 'dll' });
  world.setSamplePresent(() => io.existsSync(dest));
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
    const reloader = createReloader({ bridge, tracker, clock, user: 'TestVampire', fs: io, logTimeoutMs: 2000 });
    const removed = await reloader.removal('server');
    assert.equal(removed.ok, true, removed.error);
    assert.equal(removed.protectionAfter.ok, true, 'a Sample removal leaves the Core debug buff alone');
    const kept = await reloader.reinstall('server');
    assert.equal(kept.protectionAfter.ok, true);
    assert.equal(kept.protectionAfter.rearmed, false);
    assert.ok(!world.calls.some((c) => c.op === 'command' && c.args?.text === '.debug on'));

    assert.equal((await reloader.removal('server')).ok, true);
    world.setProtection(false);
    const rearmed = await reloader.reinstall('server');
    assert.equal(rearmed.protectionAfter.ok, true);
    assert.equal(rearmed.protectionAfter.rearmed, true);
    assert.equal(rearmed.protectionAfter.before.ok, false);
    assert.equal(world.calls.filter((c) => c.op === 'command' && c.args?.text === '.debug on').length, 1);
  } finally {
    tmp.cleanup();
  }
});

function swapReloader(tmp, world, extra = {}) {
  const folder = world.reloadFolder.server;
  mkdirSync(folder, { recursive: true });
  writeFileSync(path.join(folder, SAMPLE_DLL), 'old');
  const dll = path.join(tmp.dir, 'new.dll');
  writeFileSync(dll, 'new');
  const bridge = testBridge(tmp.dir, world);
  const clock = fakeClock();
  const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
  const findings = [];
  const reloader = createReloader({
    bridge,
    tracker,
    clock,
    user: 'TestVampire',
    findings,
    logTimeoutMs: extra.logTimeoutMs ?? 500,
    ...extra,
  });
  return { reloader, findings, dll, tracker };
}

test('swap records protection dropped by swap and returns ok false even when rearm succeeds', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ dropGuardOnSwap: true });
  world.users[0].admin = true;
  try {
    const { reloader, findings, dll } = swapReloader(tmp, world);
    const result = await reloader.swap('server', dll);
    assert.equal(result.ok, false);
    assert.equal(result.error, 'protection dropped by swap');
    assert.ok(findings.includes('protection dropped by swap'));
    assert.equal(result.protectionAfter.ok, false);
    assert.equal(result.protectionAfter.value.flags, 7);
    assert.equal(result.protectionAfter.value.guard, false);
    assert.equal(result.protectionRearmed.ok, true);
    assert.equal(result.protectionRearmed.value.guard, true);
    assert.equal(world.protectionGuard, true);
    assert.equal(result.sampleHashAfter, SWAP_SAMPLE_HASH);
  } finally {
    tmp.cleanup();
  }
});

test('swap fails on any not-ok protection parse, including a dropped buff', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  const orig = world.request.bind(world);
  world.request = async (side, op, args) => {
    const r = await orig(side, op, args);
    if (op === 'reload') world.setProtection(false);
    return r;
  };
  try {
    const { reloader, findings, dll } = swapReloader(tmp, world);
    const result = await reloader.swap('server', dll);
    assert.equal(result.ok, false);
    assert.ok(findings.includes('protection dropped by swap'));
    assert.equal(result.protectionAfter.ok, false);
    assert.equal(result.protectionRearmed.ok, true);
  } finally {
    tmp.cleanup();
  }
});

test('sampleHashFromPing reads the Sample sha256 off a ping payload', () => {
  assert.equal(sampleHashFromPing({ sampleHash: 'abc' }), 'abc');
  assert.equal(sampleHashFromPing({
    images: [{ file: 'Progenitor.Sample.dll', sha256: 'def456' }],
  }), 'def456');
  assert.equal(sampleHashFromPing({
    images: [{ name: 'Progenitor.Sample', sha256: 'stemhash' }],
  }), 'stemhash');
  assert.equal(sampleHashFromPing({ images: null }), null);
  assert.equal(sampleHashFromPing({ reload: { generation: 1 } }), null);
});

test('Scratch unload lines do not satisfy Sample removal wait', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const folder = world.reloadFolder.server;
  const dest = path.join(folder, SAMPLE_DLL);
  const io = memoryFs({ [dest]: 'dll' });
  world.setSamplePresent(() => io.existsSync(dest));
  const orig = world.request;
  world.request = async (side, op, args) => {
    if (op === 'reload') {
      world.pushLog(side, 'Reload: Progenitor.Scratch.dll removed, unloading (unload reason=Removal).');
      world.pushLog(side, 'Reload: unloaded Progenitor Scratch: clean');
      return { ok: true, result: { events: [], loaded: [] } };
    }
    return orig(side, op, args);
  };
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
    const reloader = createReloader({
      bridge,
      tracker,
      clock,
      user: 'TestVampire',
      fs: io,
      logTimeoutMs: 400,
    });
    const result = await reloader.removal('server');
    assert.equal(result.ok, false);
    assert.match(result.error, /unmatched/);
  } finally {
    tmp.cleanup();
  }
});

test('server Sample swap does not wait for a reconcile; Core protection readback passes', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  try {
    const { reloader, dll } = swapReloader(tmp, world, { logTimeoutMs: 2000 });
    const result = await reloader.swap('server', dll);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.reconcile.ok, true);
    assert.equal(result.reconcile.skipped, true);
    assert.equal(result.reconcile.reason, SAMPLE_SWAP_RECONCILE_SKIP_REASON);
    assert.match(result.reconcile.reason, /protection is Core-owned; a Sample swap does not reconcile/);
    assert.ok(!world.calls.some((c) => c.op === 'logs' && /world-ready/.test(String(c.args?.filter ?? ''))),
      'a Sample swap must not wait for a world-ready reconcile');
    assert.ok(!world.calls.some((c) => c.op === 'command' && c.args?.text === '.debug on'),
      'a passing swap must not rearm');
    assert.equal(result.protectionAfter.ok, true);
    assert.equal(result.protectionAfter.value.guard, true);
    assert.equal(result.protectionRearmed, undefined);
    assert.equal(result.playerIdentity.entity, '1:1');
    assert.equal(result.playerIdentity.connected, true);
    assert.equal(result.playerIdentity.characterChanged, false);
    assert.equal(result.handshake.ok, true);
    assert.match(String(result.handshake.line?.text ?? ''), /Sample handshake:/);
    assert.ok(Array.isArray(result.pingImages));
    assert.equal(result.sampleHashAfter, SWAP_SAMPLE_HASH);
    assert.equal(result.carriers.ok, true);
  } finally {
    tmp.cleanup();
  }
});

test('swap refuses when existsSweep still sees a tracked entity', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  try {
    const { reloader, dll, tracker } = swapReloader(tmp, world);
    tracker.adopt({
      name: 'rat',
      entity: '9:1',
      prefab: 'CHAR_Critter_Rat',
      by: 'test',
      hostile: false,
    });
    world.entities.set('9:1', { exists: true, prefab: 'CHAR_Critter_Rat' });
    await assert.rejects(() => reloader.swap('server', dll), /existsSweep/);
  } finally {
    tmp.cleanup();
  }
});

test('three consecutive swaps record identity, hash, handshake and carriers after each', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  try {
    const { reloader, dll } = swapReloader(tmp, world, { logTimeoutMs: 2000 });
    const seen = [];
    for (let i = 0; i < 3; i++) {
      const result = await reloader.swap('server', dll);
      assert.equal(result.ok, true, `swap ${i + 1}: ${result.error}`);
      assert.equal(result.protectionAfter.ok, true);
      assert.equal(result.playerIdentity.entity, '1:1');
      assert.equal(result.playerIdentity.characterChanged, false);
      assert.equal(result.handshake.ok, true);
      assert.equal(result.sampleHashAfter, SWAP_SAMPLE_HASH);
      assert.ok(result.pingImages.some((row) => row.sha256 === SWAP_SAMPLE_HASH));
      assert.equal(result.carriers.ok, true);
      seen.push(result);
    }
    assert.equal(seen.length, 3);
  } finally {
    tmp.cleanup();
  }
});

function memorySwap(tmp, world, extra = {}) {
  const folder = world.reloadFolder.server;
  const dest = path.join(folder, SAMPLE_DLL);
  const dll = extra.dllPath ?? path.join(tmp.dir, 'new.dll');
  const io = extra.fs ?? memoryFs({ [dest]: 'old', [dll]: extra.dllBytes ?? 'new' }, extra.fsOpts ?? {});
  world.setSamplePresent(() => io.existsSync(dest));
  const clock = extra.clock ?? fakeClock(5_000);
  const bridge = testBridge(tmp.dir, world);
  const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
  const reloader = createReloader({
    bridge,
    tracker,
    clock,
    user: 'TestVampire',
    fs: io,
    logTimeoutMs: extra.logTimeoutMs ?? 500,
  });
  return { reloader, io, dest, dll, clock, folder };
}

test('swap copy sets destination mtime to now', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  try {
    const { reloader, io, dest, dll } = memorySwap(tmp, world);
    const result = await reloader.swap('server', dll);
    assert.equal(result.ok, true, result.error);
    assert.equal(io.copies.length, 1);
    assert.equal(io.copies[0].dest, dest);
    assert.equal(io.utimes.length, 1);
    assert.equal(io.utimes[0].p, dest);
    const stamped = new Date(io.utimes[0].mtime).getTime();
    assert.ok(stamped !== 1000);
    assert.equal(io.mtime[dest], stamped);
    assert.equal(result.destLastWrite, stamped);
    assert.ok(stamped >= 5000);
  } finally {
    tmp.cleanup();
  }
});

test('unchanged LastWrite fails fast without waiting for reload lines', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  let reloads = 0;
  const orig = world.request.bind(world);
  world.request = async (side, op, args) => {
    if (op === 'reload') reloads += 1;
    return orig(side, op, args);
  };
  try {
    const clock = fakeClock(5_000);
    const { reloader, dest, dll, io } = memorySwap(tmp, world, {
      clock,
      fsOpts: { defaultMtime: 1000, utimesNoop: true },
    });
    const result = await reloader.swap('server', dll);
    assert.equal(result.ok, false);
    assert.match(result.error, /LastWrite unchanged: 1970-01-01T00:00:01\.000Z/);
    assert.equal(result.destLastWrite, 1000);
    assert.equal(result.previousLastWrite, 1000);
    assert.equal(io.mtime[dest], 1000);
    assert.equal(reloads, 0);
    assert.ok(clock.now() < 20000);
  } finally {
    tmp.cleanup();
  }
});

test('three swaps of the same file each bump destination mtime', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  try {
    const { reloader, io, dest, dll } = memorySwap(tmp, world);
    const writes = [];
    for (let i = 0; i < 3; i++) {
      const result = await reloader.swap('server', dll);
      assert.equal(result.ok, true, `swap ${i + 1}: ${result.error}`);
      writes.push(result.destLastWrite);
      assert.equal(io.mtime[dest], result.destLastWrite);
    }
    assert.equal(writes.length, 3);
    assert.ok(writes.every((w) => Number.isFinite(w)));
    assert.ok(writes[0] < writes[1], `${writes[0]} < ${writes[1]}`);
    assert.ok(writes[1] < writes[2], `${writes[1]} < ${writes[2]}`);
    assert.equal(io.copies.length, 3);
    assert.ok(io.copies.every((c) => c.src === dll && c.dest === dest));
    assert.equal(io.utimes.length, 3);
  } finally {
    tmp.cleanup();
  }
});

const CLIENT_HANDSHAKE =
  'Sample handshake: declared 4 row(s) on the client side costing 2 of 16 slot(s); every one is weaker than this plugin\'s own ServerMustHave/Exact row, so no new join can be refused because of them. `.hs` prints them.';
const SERVER_HANDSHAKE =
  'Sample handshake: declared 4 row(s) on the server side costing 2 of 16 slot(s); every one is weaker than this plugin\'s own ServerMustHave/Exact row, so no new join can be refused because of them. `.hs` prints them.';
const CLIENT_LOAD = 'Reload: loaded Progenitor Sample 0.1.0 from Progenitor.Sample.dll.';
const SERVER_RECONCILE =
  'Debug: world-ready reconcile over 1 own grant(s): adopted 1, removed 0, pre-existing kept 0, unresolved 0 (unreadable 0, gave up 0), dropped 0';

function attachClientHandshake(world) {
  const orig = world.request.bind(world);
  world.request = async (side, op, args) => {
    const r = await orig(side, op, args);
    if (op === 'reload' && side === 'client' && world.sampleLoaded) {
      world.pushLog('client', CLIENT_HANDSHAKE, { source: 'Progenitor Sample' });
    }
    return r;
  };
}

function swapReloaderOn(tmp, world, side, extra = {}) {
  const folder = world.reloadFolder[side];
  mkdirSync(folder, { recursive: true });
  writeFileSync(path.join(folder, SAMPLE_DLL), 'old');
  const dll = extra.dllPath ?? path.join(tmp.dir, 'new.dll');
  writeFileSync(dll, extra.dllBytes ?? 'new');
  const bridge = testBridge(tmp.dir, world);
  const clock = extra.clock ?? fakeClock();
  const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
  const findings = [];
  const reloader = createReloader({
    bridge,
    tracker,
    clock,
    user: 'TestVampire',
    findings,
    logTimeoutMs: extra.logTimeoutMs ?? 500,
    ...extra,
  });
  return { reloader, findings, dll, tracker, dest: path.join(folder, SAMPLE_DLL), clock, bridge };
}

test('client readiness matcher matches the client load line and does not require the server reconcile', () => {
  assert.equal(SAMPLE_LOAD_MATCHERS[0](CLIENT_LOAD), true);
  assert.equal(isSampleClientSwapReadyLine(CLIENT_LOAD), true);
  assert.equal(isSampleClientReadyLine(CLIENT_HANDSHAKE), true);
  assert.equal(isSampleClientSwapReadyLine(CLIENT_HANDSHAKE), true);
  assert.equal(SAMPLE_CLIENT_READY_MATCHERS[0](CLIENT_HANDSHAKE), true);
  assert.equal(isSampleClientReadyLine(SERVER_HANDSHAKE), false);
  assert.equal(SAMPLE_CLIENT_READY_MATCHERS[0](SERVER_HANDSHAKE), false);
  assert.equal(isSampleClientReadyLine(CLIENT_LOAD), false);
  assert.equal(isSampleClientReadyLine(SERVER_RECONCILE), false);
  assert.equal(isSampleClientSwapReadyLine(SERVER_RECONCILE), false);
  assert.equal(SAMPLE_CLIENT_READY_MATCHERS[0](SERVER_RECONCILE), false);
  assert.equal(DEBUG_RECONCILE_MATCHERS[0](CLIENT_HANDSHAKE), false);
  assert.equal(DEBUG_RECONCILE_MATCHERS[0](CLIENT_LOAD), false);
  assert.equal(isDebugReconcileReadyLine(CLIENT_HANDSHAKE), false);
});

test('client swap waits for the client handshake and does not wait for the debug-buff reconcile', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  attachClientHandshake(world);
  try {
    const { reloader, dll, dest } = swapReloaderOn(tmp, world, 'client', { logTimeoutMs: 2000 });
    const result = await reloader.swap('client', dll);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.dest, dest);
    assert.equal(result.reconcile.skipped, true);
    assert.equal(result.reconcile.ok, true);
    assert.match(String(result.reconcile.reason ?? ''), /does not wait for the server debug-buff reconcile/);
    assert.equal(result.clientReady.ok, true);
    assert.match(String(result.clientReady.line?.text ?? ''), /on the client side/);
    assert.match(String(result.loadLine?.text ?? result.logs?.map((l) => l.text).join('\n') ?? ''), /loaded Progenitor Sample/);
    assert.equal(result.playerIdentity.connected, true);
    assert.equal(result.sampleHashAfter, SWAP_SAMPLE_HASH);
    const clientLogFilters = world.calls
      .filter((c) => c.op === 'logs' && c.side === 'client')
      .map((c) => String(c.args?.filter ?? ''));
    assert.ok(clientLogFilters.some((f) => /handshake/i.test(f)), 'client swap waits on handshake');
    assert.ok(!clientLogFilters.some((f) => /world-ready/.test(f)), 'client swap must not wait for a reconcile');
    assert.ok(!world.calls.some((c) => c.op === 'logs' && c.side === 'server' && /world-ready/.test(String(c.args?.filter ?? ''))));
  } finally {
    tmp.cleanup();
  }
});

test('client swap fails when the client handshake line never arrives', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  try {
    const { reloader, dll } = swapReloaderOn(tmp, world, 'client', { logTimeoutMs: 400 });
    const result = await reloader.swap('client', dll);
    assert.equal(result.ok, false);
    assert.match(result.error, /client Sample ready/);
    assert.equal(result.reconcile.skipped, true);
    assert.equal(result.clientReady.ok, false);
  } finally {
    tmp.cleanup();
  }
});

test('three consecutive client swaps copy into the client reload folder', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  attachClientHandshake(world);
  try {
    const { reloader, dll, dest } = swapReloaderOn(tmp, world, 'client', { logTimeoutMs: 2000 });
    for (let i = 0; i < 3; i++) {
      const result = await reloader.swap('client', dll);
      assert.equal(result.ok, true, `client swap ${i + 1}: ${result.error}`);
      assert.equal(result.dest, dest);
      assert.equal(result.reconcile.skipped, true);
      assert.equal(result.clientReady.ok, true);
      assert.equal(result.playerIdentity.connected, true);
      assert.equal(result.playerIdentity.characterChanged, false);
      assert.equal(result.sampleHashAfter, SWAP_SAMPLE_HASH);
    }
    assert.ok(!dest.includes(world.reloadFolder.server) || world.reloadFolder.server === world.reloadFolder.client);
    assert.ok(dest.startsWith(world.reloadFolder.client));
  } finally {
    tmp.cleanup();
  }
});

test('a duplicate FROSTSTYLE client enabled line fails the handler-count check', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const clock = fakeClock();
  const bridge = testBridge(tmp.dir, world);
  try {
    world.pushLog('client', 'FROSTSTYLE client enabled=False', { source: 'Progenitor Sample' });
    world.pushLog('client', 'FROSTSTYLE client enabled=False', { source: 'Progenitor Sample' });
    const dup = await waitForSingleFroststyleClientLine(bridge, { clock, timeoutMs: 500, enabled: 'False' });
    assert.equal(dup.ok, false);
    assert.equal(dup.count, 2);
    assert.match(dup.error, /duplicate FROSTSTYLE handler/);
    assert.equal(countFroststyleClientEnabledLines(dup.collected), 2);

    world.pushLog('client', 'FROSTSTYLE client enabled=True', { source: 'Progenitor Sample' });
    const since = world.logs.client[world.logs.client.length - 1].seq - 1;
    const one = await waitForSingleFroststyleClientLine(bridge, {
      clock,
      timeoutMs: 500,
      enabled: 'True',
      since,
    });
    assert.equal(one.ok, true, one.error);
    assert.equal(one.count, 1);
    assert.match(String(one.line?.text ?? ''), /FROSTSTYLE client enabled=True/);
  } finally {
    tmp.cleanup();
  }
});
