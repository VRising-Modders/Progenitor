import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { existsSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { createTracker } from '../lib/tracker.mjs';
import {
  executeStep,
  STEP_KINDS,
  cancelTrackedChildren,
  createChildRegistry,
  parseLogTimeMs,
  expandTrackedRefs,
  parsePoolCensus,
  assertPoolCensus,
  POOL_REUSE_CAPTURE,
  CAPTURE_DEFAULT_METHOD,
  CAPTURE_DEFAULT_TIMEOUT_MS,
  CAPTURE_DEFAULT_MAX_CENSUS_DELAY_MS,
  CAPTURE_INSPECT_NOTE,
  observeShotArgv,
  windowsPowerShell51Path,
  formatCommandLine,
} from '../lib/steps.mjs';
import { fakeClock } from '../lib/clock.mjs';
import { createFakeWorld, tempDir, testBridge, TOOL_DIR, fakeObserveSpawner, fakeObserveAsyncSpawner } from './helpers.mjs';

function ctxFor(tmp, world, extra = {}) {
  const clock = extra.clock ?? fakeClock();
  const bridge = testBridge(tmp.dir, world);
  const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
  const character = extra.character ?? '1:1';
  const userEntity = extra.userEntity ?? '2:1';
  tracker.setForbidden([character, userEntity]);
  return {
    bridge,
    tracker,
    clock,
    side: extra.side ?? 'server',
    user: 'TestVampire',
    character,
    userEntity,
    playerIds: extra.playerIds ?? new Set([character, userEntity]),
    records: extra.records ?? {},
    findings: [],
    stepName: 'test',
    fs: extra.fs,
    repoRoot: extra.repoRoot,
    spawnSync: extra.spawnSync,
    spawn: extra.spawn,
    scheduleCapture: extra.scheduleCapture,
    delayReal: extra.delayReal,
    snippetPrepared: extra.snippetPrepared ?? {},
    outDir: extra.outDir ?? tmp.dir,
  };
}

function sha256(data) {
  return createHash('sha256').update(data).digest('hex');
}

/** Emit log lines on the next `logs` poll so they fall after a log step's default `since`. */
function pushLogsOnNextLogs(world, side, texts, extra = {}) {
  const orig = world.request.bind(world);
  const queue = Array.isArray(texts) ? texts.slice() : [texts];
  world.request = async (s, op, args = {}) => {
    if (queue.length && op === 'logs' && s === side) {
      for (const text of queue.splice(0)) world.pushLog(side, text, extra);
    }
    return orig(s, op, args);
  };
}

function snippetMemoryFs(files = {}) {
  const store = { ...files };
  const mtimes = Object.create(null);
  for (const key of Object.keys(store)) mtimes[key] = 0;
  const utimes = [];
  const copies = [];
  function copyFileSync(src, dest) {
    copies.push({ src, dest });
    store[dest] = store[src] ?? store[dest];
    if (mtimes[src] != null) mtimes[dest] = mtimes[src];
  }
  return {
    store,
    mtimes,
    utimes,
    copies,
    existsSync: (p) => Object.prototype.hasOwnProperty.call(store, p),
    readFileSync: (p) => {
      if (!Object.prototype.hasOwnProperty.call(store, p)) throw new Error(`ENOENT ${p}`);
      return store[p];
    },
    copyFileSync,
    baseCopyFileSync: copyFileSync,
    utimesSync: (p, atime, mtime) => {
      utimes.push({ p, atime, mtime });
      const ms = mtime instanceof Date ? mtime.getTime() : Number(mtime);
      mtimes[p] = Number.isFinite(ms) ? ms : 0;
    },
    statSync: (p) => {
      if (!Object.prototype.hasOwnProperty.call(store, p)) {
        const err = new Error(`ENOENT ${p}`);
        err.code = 'ENOENT';
        throw err;
      }
      const ms = mtimes[p] ?? 0;
      const data = store[p];
      const size = data == null ? 0 : Buffer.byteLength(data);
      return { mtimeMs: ms, mtime: new Date(ms), size };
    },
    mkdirSync: () => {},
    unlinkSync: (p) => { delete store[p]; delete mtimes[p]; },
    writeFileSync: (p, data) => {
      store[p] = Buffer.isBuffer(data) ? data : Buffer.from(data ?? '');
      if (mtimes[p] == null) mtimes[p] = 0;
    },
  };
}

function writeBuiltDll(io, dllSrc, bytes, clock) {
  io.store[dllSrc] = bytes;
  const t = Number(clock?.now?.());
  io.mtimes[dllSrc] = (Number.isFinite(t) ? t : 0) + 1;
}

function snippetPaths(tmp, world) {
  const repoRoot = tmp.dir;
  return {
    repoRoot,
    destSnippet: path.join(repoRoot, 'tools', 'scratch', 'Snippet.cs'),
    dllSrc: path.join(repoRoot, 'tools', 'scratch', 'bin', 'Debug', 'net6.0', 'Progenitor.Scratch.dll'),
    dllDest: path.join(world.reloadFolder.client, 'Progenitor.Scratch.dll'),
    srcFile: path.join(repoRoot, 'charm-root.cs'),
  };
}

function pushScratchLoad(world, dllDest, io, hash, bytes, resultLine) {
  const originalCopy = io.baseCopyFileSync ?? io.copyFileSync;
  io.copyFileSync = (src, dest) => {
    originalCopy(src, dest);
    if (dest === dllDest) {
      world.pushLog('client', `Reload: image Progenitor.Scratch.dll sha256=${hash} size=${bytes.length} asm=Progenitor.Scratch`);
      world.pushLog('client', resultLine);
    }
  };
}

test('logEntity adopts the captured index:version from the latest matching log line', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    world.pushLog('server', 'FROSTDASH edge=Start frame=1 ring=Entity(566313:130) position=(0, 0, 0)');
    world.entities.set('566313:130', { exists: true, prefab: 'frost-ring' });
    const result = await executeStep({
      kind: 'logEntity',
      name: 'ring',
      side: 'server',
      filter: 'FROSTDASH',
      regex: 'edge=Start.*ring=Entity\\((\\d+:\\d+)\\)',
      record: 'ringLog',
    }, ctx);
    assert.equal(result.ok, true);
    assert.equal(result.entity, '566313:130');
    assert.equal(ctx.tracker.get('ring').entity, '566313:130');
    assert.equal(ctx.tracker.get('ring').hostile, false);
    assert.equal(ctx.tracker.get('ring').skipCleanup, true);
    assert.equal(ctx.records.ringLog.entity, '566313:130');
    assert.equal(ctx.tracker.get('ring').seenAt, 0);
  } finally {
    tmp.cleanup();
  }
});

test('parseLogTimeMs reads HH:MM:SS.mmm and ISO timestamps', () => {
  assert.equal(parseLogTimeMs('09:32:23.674'), ((9 * 3600) + (32 * 60) + 23) * 1000 + 674);
  assert.equal(parseLogTimeMs('09:32:23.969') - parseLogTimeMs('09:32:23.674'), 295);
  assert.equal(parseLogTimeMs('2026-09-07T09:32:23.674Z'), Date.parse('2026-09-07T09:32:23.674Z'));
  assert.equal(parseLogTimeMs(null), null);
});

test('existsAt records timestamps and fails when maxAgeMs is exceeded even if exists matches expect', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const clock = fakeClock();
  try {
    const ctx = ctxFor(tmp, world, { clock });
    world.pushLog('server', 'FROSTDASH edge=Start frame=1 ring=Entity(7:1) position=(0, 0, 0)');
    world.entities.set('7:1', { exists: false, prefab: 'frost-ring' });
    await executeStep({
      kind: 'logEntity',
      name: 'ring',
      filter: 'FROSTDASH',
      regex: 'ring=Entity\\((\\d+:\\d+)\\)',
    }, ctx);
    clock.set(2000);
    const result = await executeStep({
      kind: 'existsAt',
      entity: '@tracked:ring',
      expect: false,
      maxAgeMs: 1500,
      record: 'ringAfterUnload',
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.exists, false);
    assert.equal(result.expect, false);
    assert.ok(result.timestamps.ageMs > 1500);
    assert.match(String(result.error), /older than 1500ms/);
    assert.equal(ctx.records.ringAfterUnload.exists, false);
  } finally {
    tmp.cleanup();
  }
});

test('existsAt ageClock log uses log timestamps, not wall clock', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const clock = fakeClock();
  try {
    const ctx = ctxFor(tmp, world, { clock });
    world.pushLog('server', 'FROSTDASH edge=Start frame=1 ring=Entity(7:1)', { time: '09:32:23.674' });
    world.entities.set('7:1', { exists: true, prefab: 'frost-ring' });
    await executeStep({
      kind: 'logEntity',
      name: 'ring',
      filter: 'FROSTDASH',
      regex: 'ring=Entity\\((\\d+:\\d+)\\)',
    }, ctx);
    clock.set(5000);
    world.pushLog('server', 'Reload: Progenitor.Sample.dll removed, unloading (unload reason=Removal).', { time: '09:32:23.969' });
    const result = await executeStep({
      kind: 'existsAt',
      entity: '@tracked:ring',
      expect: 'any',
      after: { regex: 'Progenitor\\.Sample\\.dll removed' },
      maxAgeMs: 1500,
      ageClock: 'log',
      record: 'ringAfterUnload',
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.timestamps.logAgeMs, 295);
    assert.ok(result.timestamps.ageMs > 1500);
    assert.equal(result.timestamps.ageClock, 'log');
    assert.equal(ctx.records.ringAfterUnload.logAgeMs, 295);
  } finally {
    tmp.cleanup();
  }
});

test('existsAt ageClock log fails when the log delta exceeds maxAgeMs', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    world.pushLog('server', 'FROSTDASH edge=Start frame=1 ring=Entity(7:1)', { time: '09:32:23.000' });
    world.entities.set('7:1', { exists: false, prefab: 'frost-ring' });
    await executeStep({
      kind: 'logEntity',
      name: 'ring',
      filter: 'FROSTDASH',
      regex: 'ring=Entity\\((\\d+:\\d+)\\)',
    }, ctx);
    world.pushLog('server', 'Reload: Progenitor.Sample.dll removed, unloading (unload reason=Removal).', { time: '09:32:25.000' });
    const result = await executeStep({
      kind: 'existsAt',
      entity: '@tracked:ring',
      expect: 'any',
      after: { regex: 'Progenitor\\.Sample\\.dll removed' },
      maxAgeMs: 1500,
      ageClock: 'log',
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.timestamps.logAgeMs, 2000);
    assert.match(String(result.error), /older than 1500ms/);
  } finally {
    tmp.cleanup();
  }
});

test('adopt tracks an existing entity for cleanup', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    world.entities.set('331934:1', { exists: true, prefab: 'CHAR_Critter_Rat' });
    const result = await executeStep({
      kind: 'adopt',
      entity: '331934:1',
      name: 'rat',
      hostile: false,
      prefab: 'CHAR_Critter_Rat',
    }, ctx);
    assert.equal(result.ok, true);
    assert.equal(ctx.tracker.get('rat').entity, '331934:1');
    assert.equal(ctx.tracker.get('rat').prefab, 'CHAR_Critter_Rat');
    assert.equal(ctx.tracker.get('rat').hostile, false);
    assert.equal(ctx.tracker.get('rat').skipCleanup, false);
    const cleanup = await ctx.tracker.cleanupAll();
    assert.equal(cleanup.ok, true);
    assert.equal(world.entities.get('331934:1').exists, false);
  } finally {
    tmp.cleanup();
  }
});

test('adopt refuses a missing entity, a prefab mismatch, and a player character', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    world.entities.set('200:1', { exists: true, prefab: 'CHAR_Bandit_Thief' });

    const missing = await executeStep({
      kind: 'adopt',
      entity: '999:1',
      name: 'missing',
      hostile: false,
    }, ctx);
    assert.equal(missing.ok, false);
    assert.match(missing.error, /does not exist/);

    const mismatch = await executeStep({
      kind: 'adopt',
      entity: '200:1',
      name: 'wrong',
      prefab: 'CHAR_Critter_Rat',
    }, ctx);
    assert.equal(mismatch.ok, false);
    assert.match(mismatch.error, /prefab mismatch/);
    assert.throws(() => ctx.tracker.get('wrong'), /no tracked entity/);

    const before = world.calls.filter((c) => c.op === 'entity').length;
    const player = await executeStep({
      kind: 'adopt',
      entity: '1:1',
      name: 'player',
      hostile: false,
    }, ctx);
    assert.equal(player.ok, false);
    assert.match(player.error, /player or user entity/);
    assert.equal(world.calls.filter((c) => c.op === 'entity').length, before);
    assert.throws(() => ctx.tracker.get('player'), /no tracked entity/);
    assert.equal(world.entities.get('1:1').exists, true);
  } finally {
    tmp.cleanup();
  }
});

test('snippetPrepare accepts an identical DLL from identical source when mtime is later than copy time', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  const { repoRoot, destSnippet, dllSrc, dllDest, srcFile } = snippetPaths(tmp, world);
  const source = Buffer.from('same snippet');
  const dll = Buffer.from('same-dll-bytes');
  const dllHash = sha256(dll);
  const io = snippetMemoryFs({
    [destSnippet]: source,
    [dllSrc]: dll,
    [srcFile]: source,
  });
  let buildArgs;
  try {
    const ctx = ctxFor(tmp, world, {
      side: 'client',
      fs: io,
      repoRoot,
      spawnSync: (_cmd, args) => {
        buildArgs = args;
        writeBuiltDll(io, dllSrc, dll, ctx.clock);
        return { status: 0, stdout: '', stderr: '' };
      },
    });
    const result = await executeStep({
      kind: 'snippetPrepare',
      file: srcFile,
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.dllHash, dllHash);
    assert.equal(result.prevDllHash, dllHash);
    assert.equal(result.dllUnchanged, true);
    assert.ok(result.dllMtime > result.copiedAt);
    assert.equal(typeof result.buildElapsedMs, 'number');
    assert.ok(buildArgs.includes('--no-incremental'));
    assert.ok(!io.copies.some((c) => c.dest === dllDest));
  } finally {
    tmp.cleanup();
  }
});

test('snippetPrepare fails when the output DLL is older than the copy time', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  const { repoRoot, destSnippet, dllSrc, dllDest, srcFile } = snippetPaths(tmp, world);
  const dll = Buffer.from('stale-or-reused-dll');
  const io = snippetMemoryFs({
    [destSnippet]: Buffer.from('previous snippet'),
    [dllSrc]: dll,
    [srcFile]: Buffer.from('next snippet'),
  });
  let built = false;
  try {
    const ctx = ctxFor(tmp, world, {
      side: 'client',
      fs: io,
      repoRoot,
      spawnSync: () => {
        built = true;
        io.store[dllSrc] = dll;
        io.mtimes[dllSrc] = 0;
        return { status: 0, stdout: '', stderr: '' };
      },
    });
    ctx.clock.set(1000);
    const result = await executeStep({
      kind: 'snippetPrepare',
      file: srcFile,
    }, ctx);
    assert.equal(built, true);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /older than copy time/);
    assert.equal(result.dllMtime, 0);
    assert.equal(result.copiedAt, 1000);
    assert.ok(!io.copies.some((c) => c.dest === dllDest));
  } finally {
    tmp.cleanup();
  }
});

test('snippetPrepare fails when the scratch build exits non-zero', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  const { repoRoot, destSnippet, dllSrc, dllDest, srcFile } = snippetPaths(tmp, world);
  const io = snippetMemoryFs({
    [destSnippet]: Buffer.from('previous snippet'),
    [dllSrc]: Buffer.from('old-dll'),
    [srcFile]: Buffer.from('next snippet'),
  });
  try {
    const ctx = ctxFor(tmp, world, {
      side: 'client',
      fs: io,
      repoRoot,
      spawnSync: () => ({ status: 1, stdout: 'error CS0000', stderr: '' }),
    });
    const result = await executeStep({
      kind: 'snippetPrepare',
      file: srcFile,
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.error, 'snippet compile failed');
    assert.equal(result.code, 1);
    assert.match(String(result.compile), /error CS0000/);
    assert.ok(!io.copies.some((c) => c.dest === dllDest));
  } finally {
    tmp.cleanup();
  }
});

test('snippet load without prepare fails with a reason', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  const { repoRoot, destSnippet, dllSrc, dllDest, srcFile } = snippetPaths(tmp, world);
  const io = snippetMemoryFs({
    [destSnippet]: Buffer.from('pool census snippet'),
    [dllSrc]: Buffer.from('pool-census-dll'),
    [srcFile]: Buffer.from('pool census snippet'),
  });
  let built = false;
  try {
    const ctx = ctxFor(tmp, world, {
      side: 'client',
      fs: io,
      repoRoot,
      spawnSync: () => {
        built = true;
        return { status: 0, stdout: '', stderr: '' };
      },
    });
    const result = await executeStep({
      kind: 'snippet',
      file: srcFile,
      side: 'client',
      expect: 'POOLID summary renderers=',
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.error, 'snippet not prepared');
    assert.equal(typeof result.elapsedMs, 'number');
    assert.equal(built, false);
    assert.ok(!io.copies.some((c) => c.dest === dllDest));
  } finally {
    tmp.cleanup();
  }
});

test('snippet prepare-then-load records timing and does not rebuild on load', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  const { repoRoot, destSnippet, dllSrc, dllDest, srcFile } = snippetPaths(tmp, world);
  const staleDll = Buffer.from('pool-census-dll');
  const rebuiltDll = Buffer.from('charm-root-dll');
  const rebuiltHash = sha256(rebuiltDll);
  const io = snippetMemoryFs({
    [destSnippet]: Buffer.from('pool census snippet'),
    [dllSrc]: staleDll,
    [srcFile]: Buffer.from('charm root snippet'),
  });
  let builds = 0;
  try {
    const ctx = ctxFor(tmp, world, {
      side: 'client',
      fs: io,
      repoRoot,
      spawnSync: (_cmd, args) => {
        builds += 1;
        assert.ok(args.includes('--no-incremental'));
        ctx.clock.set(ctx.clock.now() + 3000);
        writeBuiltDll(io, dllSrc, rebuiltDll, ctx.clock);
        return { status: 0, stdout: '', stderr: '' };
      },
    });
    const originalCopy = io.baseCopyFileSync;
    io.copyFileSync = (src, dest) => {
      originalCopy(src, dest);
      if (dest === dllDest) {
        ctx.clock.set(ctx.clock.now() + 40);
        world.pushLog('client', `Reload: image Progenitor.Scratch.dll sha256=${rebuiltHash} size=${rebuiltDll.length} asm=Progenitor.Scratch`);
        world.pushLog('client', 'scratch: result: CHARMROOT children=1 playingChildren=1 failures=0');
      }
    };
    const prepared = await executeStep({
      kind: 'snippetPrepare',
      file: srcFile,
      record: 'poolCensusPrepared',
    }, ctx);
    assert.equal(prepared.ok, true, prepared.error);
    assert.equal(builds, 1);
    assert.equal(prepared.dllHash, rebuiltHash);
    assert.equal(prepared.buildElapsedMs, 3000);
    assert.ok(prepared.dllMtime > prepared.copiedAt);
    assert.ok(!io.copies.some((c) => c.dest === dllDest));
    assert.equal(ctx.records.poolCensusPrepared.dllHash, rebuiltHash);

    const loaded = await executeStep({
      kind: 'snippet',
      file: srcFile,
      side: 'client',
      expect: 'CHARMROOT children=[1-9]',
    }, ctx);
    assert.equal(loaded.ok, true, loaded.error);
    assert.equal(builds, 1, 'timed snippet must not rebuild');
    assert.equal(loaded.dllHash, rebuiltHash);
    assert.equal(loaded.elapsedMs, 40);
    assert.match(String(loaded.imageLine), new RegExp(`sha256=${rebuiltHash}`));
    assert.match(String(loaded.line), /CHARMROOT children=1/);
    assert.equal(io.utimes.length, 1);
    assert.ok(io.copies.some((c) => c.dest === dllDest));
    assert.equal(io.store[dllDest], undefined);
  } finally {
    tmp.cleanup();
  }
});

test('snippet fails when the load-line hash does not match the prepared DLL', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  const { repoRoot, destSnippet, dllSrc, dllDest, srcFile } = snippetPaths(tmp, world);
  const rebuiltDll = Buffer.from('charm-root-dll');
  const rebuiltHash = sha256(rebuiltDll);
  const io = snippetMemoryFs({
    [destSnippet]: Buffer.from('pool census snippet'),
    [dllSrc]: Buffer.from('pool-census-dll'),
    [srcFile]: Buffer.from('charm root snippet'),
  });
  let builds = 0;
  try {
    const ctx = ctxFor(tmp, world, {
      side: 'client',
      fs: io,
      repoRoot,
      spawnSync: () => {
        builds += 1;
        writeBuiltDll(io, dllSrc, rebuiltDll, ctx.clock);
        return { status: 0, stdout: '', stderr: '' };
      },
    });
    const prepared = await executeStep({ kind: 'snippetPrepare', file: srcFile }, ctx);
    assert.equal(prepared.ok, true, prepared.error);
    pushScratchLoad(world, dllDest, io, 'deadbeef', rebuiltDll, 'scratch: result: CHARMROOT children=1 playingChildren=1 failures=0');
    const result = await executeStep({
      kind: 'snippet',
      file: srcFile,
      side: 'client',
      expect: 'CHARMROOT children=[1-9]',
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.error, 'snippet DLL hash mismatch');
    assert.equal(result.dllHash, rebuiltHash);
    assert.equal(result.loggedHash, 'deadbeef');
    assert.equal(builds, 1);
  } finally {
    tmp.cleanup();
  }
});

test('charm-tracked-unload step kinds are registered', () => {
  const doc = JSON.parse(readFileSync(path.join(TOOL_DIR, 'cases-catalog', 'charm-tracked-unload.json'), 'utf8'));
  assert.ok(Array.isArray(doc.steps) && doc.steps.length > 0);
  for (const step of doc.steps) {
    assert.ok(STEP_KINDS.includes(step.kind), `unknown step kind ${step.kind}`);
  }
});

test('active-ring-unload step and teardown kinds are registered', () => {
  const doc = JSON.parse(readFileSync(path.join(TOOL_DIR, 'cases-catalog', 'active-ring-unload.json'), 'utf8'));
  assert.ok(Array.isArray(doc.steps) && doc.steps.length > 0);
  assert.ok(Array.isArray(doc.teardown) && doc.teardown.length > 0);
  for (const step of [...doc.steps, ...doc.teardown]) {
    assert.ok(STEP_KINDS.includes(step.kind), `unknown step kind ${step.kind}`);
  }
  const off = doc.teardown.find((s) => s.text === '.frostdash off');
  assert.ok(off, 'frostdash off must live in teardown');
  assert.ok(!doc.steps.some((s) => s.text === '.frostdash off'));
  assert.ok(off.expectLog, 'teardown off must require the Sample handler line');
  assert.match(String(off.expectLog.regex), /FROSTDASH cleanup alive=/);
});

async function adoptRing(tmp, world, id = '7:1') {
  const ctx = ctxFor(tmp, world);
  world.entities.set(id, { exists: true, prefab: 'frost-ring' });
  world.pushLog('server', `FROSTDASH edge=Start frame=1 ring=Entity(${id})`, { time: '09:32:23.674' });
  await executeStep({
    kind: 'logEntity',
    name: 'ring',
    filter: 'FROSTDASH',
    regex: 'ring=Entity\\((\\d+:\\d+)\\)',
    record: 'ringLog',
  }, ctx);
  return ctx;
}

const CLEANUP_REGEX = 'FROSTDASH cleanup alive=([1-9]\\d*) destroyed=\\[[^\\]]*@tracked:ring';
const ENTITY_REGEX = 'ring=Entity\\(@tracked:ring\\)';

function expandedTest(pattern, ctx) {
  return new RegExp(expandTrackedRefs(pattern, ctx));
}

async function logCleanupLine(tmp, text, regex = CLEANUP_REGEX) {
  const world = createFakeWorld();
  const ctx = await adoptRing(tmp, world, '7:1');
  world.pushLog('server', text);
  return executeStep({
    kind: 'log',
    sinceRecord: 'ringLog',
    filter: 'FROSTDASH',
    regex,
    timeoutMs: 200,
  }, ctx);
}

test('log step parses FROSTDASH cleanup alive= and requires the tracked ring in destroyed', async () => {
  const tmp = tempDir();
  try {
    const world = createFakeWorld();
    const ctx = await adoptRing(tmp, world, '7:1');
    world.pushLog('server', 'FROSTDASH cleanup alive=1 destroyed=[7:1]', { time: '09:32:23.800' });
    const ok = await executeStep({
      kind: 'log',
      name: 'ringUnloadCleanup',
      side: 'server',
      sinceRecord: 'ringLog',
      filter: 'FROSTDASH',
      regex: CLEANUP_REGEX,
      record: 'ringUnloadCleanup',
    }, ctx);
    assert.equal(ok.ok, true, ok.error);
    assert.equal(ok.match[1], '1');
    assert.match(ok.text, /destroyed=\[7:1\]/);
    assert.equal(ctx.records.ringUnloadCleanup.text, ok.text);

    const emptyWorld = createFakeWorld();
    const emptyCtx = await adoptRing(tmp, emptyWorld, '7:1');
    emptyWorld.pushLog('server', 'FROSTDASH cleanup alive=0 destroyed=[]');
    const empty = await executeStep({
      kind: 'log',
      sinceRecord: 'ringLog',
      filter: 'FROSTDASH',
      regex: CLEANUP_REGEX,
      timeoutMs: 200,
    }, emptyCtx);
    assert.equal(empty.ok, false, 'alive=0 must not satisfy alive>=1');

    const missWorld = createFakeWorld();
    const missCtx = await adoptRing(tmp, missWorld, '7:1');
    missWorld.pushLog('server', 'FROSTDASH cleanup alive=1 destroyed=[9:9]');
    const missed = await executeStep({
      kind: 'log',
      sinceRecord: 'ringLog',
      filter: 'FROSTDASH',
      regex: CLEANUP_REGEX,
      timeoutMs: 200,
    }, missCtx);
    assert.equal(missed.ok, false, 'destroyed must contain the tracked ring id');
  } finally {
    tmp.cleanup();
  }
});

test('a tracked id substituted into a regex matches only as a whole token', async () => {
  const tmp = tempDir();
  try {
    const world = createFakeWorld();
    const ctx = await adoptRing(tmp, world, '7:1');
    const listRe = expandedTest(CLEANUP_REGEX, ctx);
    const entityRe = expandedTest(ENTITY_REGEX, ctx);

    assert.equal(listRe.test('FROSTDASH cleanup alive=1 destroyed=[7:1]'), true);
    assert.equal(listRe.test('FROSTDASH cleanup alive=1 destroyed=[3:2,7:1]'), true);
    assert.equal(listRe.test('FROSTDASH cleanup alive=1 destroyed=[7:1,9:9]'), true);
    assert.equal(listRe.test('FROSTDASH cleanup alive=1 destroyed=[17:1]'), false);
    assert.equal(listRe.test('FROSTDASH cleanup alive=1 destroyed=[7:10]'), false);
    assert.equal(entityRe.test('FROSTDASH edge=Start ring=Entity(7:1)'), true);
    assert.equal(entityRe.test('FROSTDASH edge=Start ring=Entity(17:1)'), false);
    assert.equal(entityRe.test('FROSTDASH edge=Start ring=Entity(7:10)'), false);

    const hits = [
      'FROSTDASH cleanup alive=1 destroyed=[7:1]',
      'FROSTDASH cleanup alive=1 destroyed=[3:2,7:1]',
      'FROSTDASH cleanup alive=1 destroyed=[7:1,9:9]',
    ];
    for (const text of hits) {
      const result = await logCleanupLine(tmp, text);
      assert.equal(result.ok, true, text);
    }
    const misses = [
      'FROSTDASH cleanup alive=1 destroyed=[17:1]',
      'FROSTDASH cleanup alive=1 destroyed=[7:10]',
    ];
    for (const text of misses) {
      const result = await logCleanupLine(tmp, text);
      assert.equal(result.ok, false, text);
    }

    const entityOk = await logCleanupLine(tmp, 'FROSTDASH edge=Start ring=Entity(7:1)', ENTITY_REGEX);
    assert.equal(entityOk.ok, true, entityOk.error);
  } finally {
    tmp.cleanup();
  }
});

test('log step with count fails through the step when a duplicate matching line is collected', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world, { side: 'client' });
    pushLogsOnNextLogs(world, 'client', [
      'FROSTSTYLE client enabled=False',
      'FROSTSTYLE client enabled=False',
    ], { source: 'Progenitor Sample' });
    const result = await executeStep({
      kind: 'log',
      name: 'froststyle client off',
      side: 'client',
      filter: 'FROSTSTYLE',
      regex: 'FROSTSTYLE client enabled=False',
      count: 1,
      timeoutMs: 500,
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.count, 2);
    assert.equal(typeof result.since, 'number');
    assert.equal(result.error, 'duplicate handler: 2 lines matching FROSTSTYLE client enabled=False');
  } finally {
    tmp.cleanup();
  }
});

test('log step with count fails when fewer matching lines are collected', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world, { side: 'client' });
    pushLogsOnNextLogs(world, 'client', ['FROSTSTYLE client enabled=True'], { source: 'Progenitor Sample' });
    const result = await executeStep({
      kind: 'log',
      side: 'client',
      filter: 'FROSTSTYLE',
      regex: 'FROSTSTYLE client enabled=True',
      count: 2,
      timeoutMs: 500,
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.count, 1);
    assert.equal(typeof result.since, 'number');
    assert.equal(result.error, 'expected 2 matching line(s), got 1');
  } finally {
    tmp.cleanup();
  }
});

test('log step with count 1 passes when exactly one matching line is collected', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world, { side: 'client' });
    pushLogsOnNextLogs(world, 'client', ['FROSTSTYLE client enabled=True'], { source: 'Progenitor Sample' });
    const result = await executeStep({
      kind: 'log',
      side: 'client',
      filter: 'FROSTSTYLE',
      regex: 'FROSTSTYLE client enabled=True',
      count: 1,
      timeoutMs: 500,
      record: 'froststyleClientOn',
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.count, 1);
    assert.equal(typeof result.since, 'number');
    assert.equal(ctx.records.froststyleClientOn.count, 1);
    assert.equal(ctx.records.froststyleClientOn.since, result.since);
    assert.match(String(result.line?.text ?? ''), /FROSTSTYLE client enabled=True/);
  } finally {
    tmp.cleanup();
  }
});

test('anchored log step counts a client FROSTSTYLE line emitted during the command wait once and rejects a duplicate', async () => {
  async function runWithClientLines(texts) {
    const tmp = tempDir();
    const world = createFakeWorld();
    try {
      const ctx = ctxFor(tmp, world, { side: 'client' });
      const orig = world.request.bind(world);
      world.request = async (side, op, args = {}) => {
        const result = await orig(side, op, args);
        if (op === 'command' && args.text === '.froststyle off') {
          world.pushLog('server', 'FROSTSTYLE enabled=False restored=0', { source: 'Progenitor Sample' });
          for (const text of texts) {
            world.pushLog('client', text, { source: 'Progenitor Sample' });
          }
        }
        return result;
      };
      const cmd = await executeStep({
        kind: 'command',
        name: 'froststyle off',
        text: '.froststyle off',
        expectLog: { filter: 'FROSTSTYLE', regex: 'FROSTSTYLE enabled=False' },
        record: 'froststyleOff',
      }, ctx);
      assert.equal(cmd.ok, true, cmd.error);
      assert.equal(typeof ctx.records.froststyleOff.since, 'number');
      const result = await executeStep({
        kind: 'log',
        name: 'froststyle client off',
        side: 'client',
        filter: 'FROSTSTYLE',
        regex: 'FROSTSTYLE client enabled=False',
        timeoutMs: 500,
        count: 1,
        sinceRecord: 'froststyleOff',
        record: 'froststyleClientOff',
      }, ctx);
      return { tmp, result, since: ctx.records.froststyleOff.since };
    } catch (err) {
      tmp.cleanup();
      throw err;
    }
  }

  const one = await runWithClientLines(['FROSTSTYLE client enabled=False']);
  try {
    assert.equal(one.result.ok, true, one.result.error);
    assert.equal(one.result.count, 1);
    assert.equal(one.result.since, one.since);
    assert.match(String(one.result.line?.text ?? ''), /FROSTSTYLE client enabled=False/);
  } finally {
    one.tmp.cleanup();
  }

  const dup = await runWithClientLines([
    'FROSTSTYLE client enabled=False',
    'FROSTSTYLE client enabled=False',
  ]);
  try {
    assert.equal(dup.result.ok, false);
    assert.equal(dup.result.count, 2);
    assert.equal(dup.result.since, dup.since);
    assert.equal(dup.result.error, 'duplicate handler: 2 lines matching FROSTSTYLE client enabled=False');
  } finally {
    dup.tmp.cleanup();
  }
});

test('log step without sinceRecord ignores a stale matching line and accepts a fresh one', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world, { side: 'client' });
    world.pushLog('client', 'CATALOGEX status handle=Borrowed enabled=True cycle=stale', { source: 'Progenitor Sample' });
    const staleSeq = world.logs.client[world.logs.client.length - 1].seq;
    pushLogsOnNextLogs(world, 'client', [
      'CATALOGEX status handle=Borrowed enabled=True cycle=fresh',
    ], { source: 'Progenitor Sample' });
    const result = await executeStep({
      kind: 'log',
      name: 'catalogex status',
      side: 'client',
      filter: 'CATALOGEX',
      regex: 'CATALOGEX status handle=(?!null)\\S+ enabled=True',
      timeoutMs: 500,
      count: 1,
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.since, staleSeq);
    assert.equal(result.count, 1);
    assert.match(String(result.text ?? result.line?.text ?? ''), /cycle=fresh/);
    assert.notEqual(result.logSeq, staleSeq);
  } finally {
    tmp.cleanup();
  }
});

function loadPoolReuseCase() {
  return JSON.parse(readFileSync(path.join(TOOL_DIR, 'cases-catalog', 'pool-reuse-identity.json'), 'utf8'));
}

test('pool-reuse-identity step and teardown kinds are registered', () => {
  const doc = loadPoolReuseCase();
  assert.ok(Array.isArray(doc.steps) && doc.steps.length > 0);
  assert.ok(Array.isArray(doc.teardown) && doc.teardown.length > 0);
  for (const step of [...doc.steps, ...doc.teardown]) {
    assert.ok(STEP_KINDS.includes(step.kind), `unknown step kind ${step.kind}`);
  }
  assert.equal(doc.steps[0].kind, 'snippetPrepare');
  assert.equal(doc.steps[0].file, 'tools/live-rig/cases-catalog/pool-census.Snippet.cs');
  assert.equal(doc.steps[1].kind, 'capturePrepare');
  const timedSnippets = doc.steps.filter((s) => s.kind === 'snippet');
  assert.ok(timedSnippets.length >= 4);
  assert.ok(timedSnippets.every((s) => s.prepare !== true));
  const tinted = doc.steps.find((s) => s.name === 'pool census tinted');
  assert.equal(tinted.assert.phase, 'tinted');
  const dashIdx = doc.steps.findIndex((s) => s.kind === 'command' && s.text === '.frostdash dash');
  assert.equal(doc.steps[dashIdx + 1].kind, 'capture');
  assert.equal(doc.steps[dashIdx + 1].name, 'tinted window');
  assert.equal(doc.steps[dashIdx + 1].delayMs, 100);
  assert.equal(doc.steps[dashIdx + 1].waitMs, 1500);
  assert.equal(doc.steps[dashIdx + 1].maxCensusDelayMs, 2000);
  assert.equal(doc.steps[dashIdx + 1].method, 'PrintWindow');
  assert.equal(doc.steps[dashIdx + 2].name, 'pool census tinted');
  const styleOff = doc.steps.find((s) => s.text === '.froststyle off');
  assert.ok(styleOff, 'froststyle off must run after rest');
  assert.ok(styleOff.expectLog, 'froststyle off must require a FROSTSTYLE handler line');
  assert.match(String(styleOff.expectLog.regex), /FROSTSTYLE enabled=False/);
  const clientOff = doc.steps.find((s) => s.kind === 'log' && /FROSTSTYLE client enabled=False/.test(String(s.regex ?? '')));
  assert.ok(clientOff, 'client must wait for FROSTSTYLE client enabled=False');
  assert.equal(clientOff.side, 'client');
  const reuse = doc.steps.find((s) => s.kind === 'poolReuse');
  assert.ok(reuse, 'untinted reuse must use poolReuse');
  assert.equal(reuse.dash, '.frostdash dash');
  assert.equal(reuse.waitMs, 1500);
  assert.equal(reuse.maxCensusDelayMs, 2000);
  assert.equal(reuse.retries, 3);
  assert.equal(reuse.capture.delayMs, 100);
  assert.equal(reuse.capture.burst.count, 3);
  assert.equal(reuse.capture.burst.intervalMs, 400);
  assert.equal(reuse.capture.delayMs + (reuse.capture.burst.count - 1) * reuse.capture.burst.intervalMs, 900);
  assert.equal(reuse.capture.requireFrames, true);
  assert.equal(reuse.capture.method, 'PrintWindow');
  const styleOn = doc.steps.find((s) => s.text === '.froststyle on');
  assert.ok(styleOn, 'froststyle on must re-enable after reuse');
  assert.match(String(styleOn.expectLog.regex), /FROSTSTYLE enabled=True/);
  const finalRest = doc.steps.find((s) => s.name === 'pool census rest after reuse');
  assert.equal(finalRest.assert.phase, 'rest');
  const off = doc.teardown.find((s) => s.text === '.frostdash off');
  assert.ok(off, 'frostdash off must live in teardown');
  assert.match(String(off.expectLog.regex), /FROSTDASH cleanup alive=/);
});

function installFroststyleHandler(world, { restoredOff = 5 } = {}) {
  const orig = world.request.bind(world);
  const pendingClient = [];
  world.request = async (side, op, args = {}) => {
    if (op === 'logs' && side === 'client' && pendingClient.length) {
      for (const text of pendingClient.splice(0)) {
        world.pushLog('client', text, { source: 'Progenitor Sample' });
      }
    }
    const result = await orig(side, op, args);
    if (op === 'command' && typeof args.text === 'string' && /^\.froststyle\b/.test(args.text)) {
      if (/\boff\b/.test(args.text)) {
        world.pushLog('server', `FROSTSTYLE enabled=False restored=${restoredOff}`, { source: 'Progenitor Sample' });
        pendingClient.push('FROSTSTYLE client enabled=False');
      } else if (/\bon\b/.test(args.text)) {
        world.pushLog('server', 'FROSTSTYLE enabled=True restored=0', { source: 'Progenitor Sample' });
        pendingClient.push('FROSTSTYLE client enabled=True');
      }
    }
    return result;
  };
}

function poolLine({
  rendererId,
  goId,
  active = false,
  slot = 0,
  hasBlock = false,
  baseColor = 'none',
  go = 'Impact_CrystalCenter_Spikes01',
}) {
  return `POOLID rendererId=${rendererId} goId=${goId} active=${active ? 'True' : 'False'} slot=${slot} hasBlock=${hasBlock ? 'True' : 'False'} baseColor=${baseColor} go=${go}`;
}

function poolSummaryLine({
  renderers = 1,
  active = 0,
  withBlock = 0,
  green = 0,
  ids = '-10',
  truncated = false,
} = {}) {
  return `POOLID summary renderers=${renderers} active=${active} withBlock=${withBlock} green=${green} failures=0 truncated=${truncated ? 'True' : 'False'} cap=256 radius=60 origin=none scanned=${renderers} emitted=${renderers} skippedRadius=0 skippedCap=0 ids=${ids}`;
}

const GREEN = '0.12,0.65,0.25,1';
const A = { rendererId: -10, goId: -11, go: 'Impact_CrystalCenter_SpikeCenter' };
const B = { rendererId: -20, goId: -21, go: 'Impact_CrystalCenter_Spikes05' };
const C = { rendererId: -30, goId: -31, go: 'Impact_CrystalCenter_Spikes02' };
const ID_A = '-10/-11/0';
const ID_B = '-20/-21/0';

function censusFrom(lines) {
  return parsePoolCensus(lines);
}

function recordsWith(baselineLines, tintedLines) {
  const baseline = censusFrom(baselineLines);
  const tintedAssert = assertPoolCensus({
    phase: 'tinted',
    baselineRecord: 'poolBaseline',
    overrideColor: GREEN,
  }, censusFrom(tintedLines), { records: { poolBaseline: baseline } });
  const poolTinted = { ...censusFrom(tintedLines), participants: tintedAssert.participants, qualifiedTinted: tintedAssert.qualifiedTinted };
  return { poolBaseline: baseline, poolTinted };
}

async function executePoolSnippet(tmp, world, step, lines, extra = {}) {
  const repoRoot = extra.repoRoot ?? tmp.dir;
  const dllSrc = path.join(repoRoot, 'tools', 'scratch', 'bin', 'Debug', 'net6.0', 'Progenitor.Scratch.dll');
  const dllDest = path.join(world.reloadFolder.client, 'Progenitor.Scratch.dll');
  const srcFile = extra.srcFile ?? path.join(repoRoot, 'pool-census.cs');
  const io = extra.io ?? snippetMemoryFs({
    [srcFile]: Buffer.from('pool census snippet'),
    [dllSrc]: Buffer.from('stale-pool-dll'),
  });
  const rebuiltDll = extra.dll
    ?? extra.ctx?.poolDll
    ?? Buffer.from(extra.dllBytes ?? 'pool-census-dll');
  const rebuiltHash = sha256(rebuiltDll);
  const originalCopy = io.baseCopyFileSync ?? io.copyFileSync;
  const lineFn = typeof lines === 'function' ? lines : () => lines;
  io.copyFileSync = (src, dest) => {
    originalCopy(src, dest);
    if (dest === dllDest) {
      const batch = lineFn();
      world.pushLog('client', `Reload: image Progenitor.Scratch.dll sha256=${rebuiltHash} size=${rebuiltDll.length} asm=Progenitor.Scratch`);
      for (const text of batch) world.pushLog('client', text, { source: 'Progenitor Scratch' });
      const summary = batch.find((l) => String(l).includes('POOLID summary')) ?? poolSummaryLine();
      world.pushLog('client', `scratch: result: ${summary}`);
    }
  };
  const ctx = extra.ctx ?? ctxFor(tmp, world, {
    side: 'client',
    fs: io,
    repoRoot,
    records: extra.records ?? {},
  });
  if (!extra.ctx) {
    ctx.fs = io;
    ctx.repoRoot = repoRoot;
    ctx.snippetBuildCount = 0;
    ctx.spawnSync = () => {
      ctx.snippetBuildCount += 1;
      writeBuiltDll(io, dllSrc, ctx.poolDll ?? rebuiltDll, ctx.clock);
      return { status: 0, stdout: '', stderr: '' };
    };
  }
  ctx.poolDll = rebuiltDll;
  const resolved = path.resolve(srcFile);
  if (!ctx.snippetPrepared?.[resolved]) {
    const prepared = await executeStep({
      kind: 'snippetPrepare',
      file: srcFile,
    }, ctx);
    if (!prepared.ok) return { result: prepared, ctx, rebuiltHash, io, srcFile };
  }
  const result = await executeStep({
    kind: 'snippet',
    file: srcFile,
    side: 'client',
    expect: 'POOLID summary renderers=',
    ...step,
  }, ctx);
  return { result, ctx, rebuiltHash, io, srcFile };
}

test('pool census assertions: pass path records T, rest-restores per id, and reuse finds R', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  try {
    const baselineLines = [
      poolLine({ ...A, active: false, hasBlock: false }),
      poolLine({ ...B, active: false, hasBlock: false }),
      poolLine({ ...C, active: false, hasBlock: true, baseColor: '0.8,0.8,0.9,1' }),
      poolSummaryLine({ renderers: 3, active: 0, withBlock: 1, ids: '-30,-20,-10' }),
    ];
    const tintedLines = [
      poolLine({ ...A, active: true, hasBlock: true, baseColor: GREEN }),
      poolLine({ ...B, active: true, hasBlock: true, baseColor: GREEN }),
      poolLine({ ...C, active: false, hasBlock: true, baseColor: '0.8,0.8,0.9,1' }),
      poolSummaryLine({ renderers: 3, active: 2, withBlock: 3, green: 2, ids: '-30,-20,-10' }),
    ];
    const restLines = [
      poolLine({ ...A, active: false, hasBlock: false }),
      poolLine({ ...B, active: false, hasBlock: false }),
      poolLine({ ...C, active: false, hasBlock: true, baseColor: '0.8,0.8,0.9,1' }),
      poolSummaryLine({ renderers: 3, active: 0, withBlock: 1, ids: '-30,-20,-10' }),
    ];
    const reuseLines = [
      poolLine({ ...A, active: true, hasBlock: false }),
      poolLine({ ...B, active: false, hasBlock: false }),
      poolLine({ ...C, active: false, hasBlock: true, baseColor: '0.8,0.8,0.9,1' }),
      poolSummaryLine({ renderers: 3, active: 1, withBlock: 1, ids: '-30,-20,-10' }),
    ];

    const batch = { lines: baselineLines };
    const emit = () => batch.lines;
    const base = await executePoolSnippet(tmp, world, {
      name: 'pool census baseline',
      record: 'poolBaseline',
      assert: { phase: 'baseline' },
    }, emit);
    assert.equal(base.result.ok, true, base.result.error);

    batch.lines = tintedLines;
    const tinted = await executePoolSnippet(tmp, world, {
      name: 'pool census tinted',
      record: 'poolTinted',
      assert: { phase: 'tinted', baselineRecord: 'poolBaseline', overrideColor: GREEN },
    }, emit, { ctx: base.ctx, io: base.io, srcFile: base.srcFile });
    assert.equal(tinted.result.ok, true, tinted.result.error);
    assert.deepEqual(tinted.result.assert.participants.sort(), [ID_A, ID_B]);
    assert.equal(tinted.result.assert.qualifiedTinted, 2);
    assert.equal(tinted.result.assert.informational.tintedInBaseline, 2);
    assert.equal(tinted.result.assert.informational.tintedFresh, 0);
    assert.equal(tinted.result.informational.baselineVsThis.equal, true);
    assert.equal(tinted.result.informational.baselineVsThis.historicalReferenceCount, 122);
    assert.equal(tinted.result.informational.baselineVsThis.informational, true);

    batch.lines = restLines;
    const rest = await executePoolSnippet(tmp, world, {
      name: 'pool census rest',
      record: 'poolRest',
      assert: {
        phase: 'rest',
        baselineRecord: 'poolBaseline',
        tintedRecord: 'poolTinted',
        overrideColor: GREEN,
      },
    }, emit, { ctx: base.ctx, io: base.io, srcFile: base.srcFile });
    assert.equal(rest.result.ok, true, rest.result.error);

    const doc = loadPoolReuseCase();
    installFroststyleHandler(world);
    const styleOff = await executeStep(doc.steps.find((s) => s.text === '.froststyle off'), base.ctx);
    assert.equal(styleOff.ok, true, styleOff.error);
    assert.match(String(styleOff.line), /FROSTSTYLE enabled=False restored=/);
    const clientOff = await executeStep(doc.steps.find((s) => s.kind === 'log' && /FROSTSTYLE client enabled=False/.test(String(s.regex ?? ''))), base.ctx);
    assert.equal(clientOff.ok, true, clientOff.error);

    batch.lines = reuseLines;
    const buildsBeforeReuse = base.ctx.snippetBuildCount ?? 1;
    const observe = fakeObserveSpawner({ inner: base.ctx.spawnSync, fs: base.ctx.fs });
    base.ctx.spawnSync = observe;
    const reuseStep = {
      ...doc.steps.find((s) => s.kind === 'poolReuse'),
      waitMs: 1500,
      retryWaitMs: 10,
      snippet: {
        side: 'client',
        file: base.srcFile,
        expect: 'POOLID summary renderers=',
      },
    };
    const reuse = await executeStep(reuseStep, base.ctx);
    assert.equal(reuse.ok, true, reuse.error);
    assert.equal(base.ctx.snippetBuildCount, buildsBeforeReuse, 'poolReuse must not rebuild the census DLL');
    assert.deepEqual(reuse.reused, [ID_A]);
    assert.equal(reuse.qualifiedReused, 1);
    assert.equal(reuse.reusedUntinted, 1);
    assert.equal(reuse.qualifiedTinted, 2);
    assert.equal(reuse.retriesUsed, 1);
    assert.equal(reuse.capture.available, true);
    assert.equal(reuse.capture.paths.length, 3);
    assert.equal(reuse.visual.verified, false);
    assert.equal(reuse.visual.verifiedBy, null);
    assert.equal(reuse.visual.verifiedAt, null);
    assert.equal(reuse.visual.frames.length, 3);
    assert.deepEqual(reuse.visual.frames.map((f) => f.offsetMs), [100, 500, 900]);
    assert.equal(reuse.censusDelayMs, 1500);
    assert.match(String(reuse.capture.note), /does not analyse pixels/);
    assert.equal(observe.calls.filter((c) => (c.args ?? []).includes('shot')).length, 3);

    const styleOn = await executeStep(doc.steps.find((s) => s.text === '.froststyle on'), base.ctx);
    assert.equal(styleOn.ok, true, styleOn.error);
    assert.match(String(styleOn.line), /FROSTSTYLE enabled=True restored=0/);

    batch.lines = restLines;
    const finalRest = await executePoolSnippet(tmp, world, {
      name: 'pool census rest after reuse',
      record: 'poolRestAfterReuse',
      assert: {
        phase: 'rest',
        baselineRecord: 'poolBaseline',
        tintedRecord: 'poolTinted',
        overrideColor: GREEN,
      },
    }, emit, { ctx: base.ctx, io: base.io, srcFile: base.srcFile });
    assert.equal(finalRest.result.ok, true, finalRest.result.error);
  } finally {
    tmp.cleanup();
  }
});

test('pool census tinted fails on zero participants', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  try {
    const baselineLines = [
      poolLine({ ...A, active: false }),
      poolSummaryLine({ renderers: 1, ids: '-10' }),
    ];
    const tintedLines = [
      poolLine({ ...A, active: false }),
      poolSummaryLine({ renderers: 1, ids: '-10' }),
    ];
    const base = await executePoolSnippet(tmp, world, {
      record: 'poolBaseline',
      assert: { phase: 'baseline' },
    }, baselineLines);
    const tinted = await executePoolSnippet(tmp, world, {
      record: 'poolTinted',
      assert: { phase: 'tinted', baselineRecord: 'poolBaseline', overrideColor: GREEN },
    }, tintedLines, { ctx: base.ctx, io: base.io, srcFile: base.srcFile });
    assert.equal(tinted.result.ok, false);
    assert.equal(tinted.result.error, 'zero participants');
  } finally {
    tmp.cleanup();
  }
});

test('pool census tinted accepts fresh allocation ids that were not in the baseline', () => {
  const baselineLines = [
    poolLine({ ...A, active: false }),
    poolSummaryLine({ renderers: 1, ids: '-10' }),
  ];
  const tintedLines = [
    poolLine({ ...A, active: false }),
    poolLine({ ...B, active: true, hasBlock: true, baseColor: GREEN, go: 'Impact_CrystalCenter_Spikes05' }),
    poolSummaryLine({ renderers: 2, active: 1, withBlock: 1, green: 1, ids: '-20,-10' }),
  ];
  const tinted = assertPoolCensus({
    phase: 'tinted',
    baselineRecord: 'poolBaseline',
    overrideColor: GREEN,
  }, censusFrom(tintedLines), { records: { poolBaseline: censusFrom(baselineLines) } });
  assert.equal(tinted.ok, true, tinted.error);
  assert.deepEqual(tinted.participants, [ID_B]);
  assert.equal(tinted.qualifiedTinted, 1);
  assert.equal(tinted.informational.tintedInBaseline, 0);
  assert.equal(tinted.informational.tintedFresh, 1);
  assert.notEqual(tinted.error, 'missing identity');

  const rest = assertPoolCensus({
    phase: 'rest',
    baselineRecord: 'poolBaseline',
    tintedRecord: 'poolTinted',
    overrideColor: GREEN,
  }, censusFrom([
    poolLine({ ...A, active: false }),
    poolLine({ ...B, active: false, hasBlock: false }),
    poolSummaryLine({ renderers: 2, ids: '-20,-10' }),
  ]), {
    records: {
      poolBaseline: censusFrom(baselineLines),
      poolTinted: { ...censusFrom(tintedLines), participants: tinted.participants, qualifiedTinted: 1 },
    },
  });
  assert.equal(rest.ok, true, rest.error);

  const restGone = assertPoolCensus({
    phase: 'rest',
    baselineRecord: 'poolBaseline',
    tintedRecord: 'poolTinted',
    overrideColor: GREEN,
  }, censusFrom([
    poolLine({ ...A, active: false }),
    poolSummaryLine({ renderers: 1, ids: '-10' }),
  ]), {
    records: {
      poolBaseline: censusFrom(baselineLines),
      poolTinted: { participants: tinted.participants, qualifiedTinted: 1 },
    },
  });
  assert.equal(restGone.ok, false);
  assert.equal(restGone.error, `tinted object missing at rest: ${ID_B}`);

  const reuse = assertPoolCensus({
    phase: 'reuse',
    baselineRecord: 'poolBaseline',
    tintedRecord: 'poolTinted',
    overrideColor: GREEN,
  }, censusFrom([
    poolLine({ ...A, active: false }),
    poolLine({ ...B, active: true, hasBlock: false }),
    poolSummaryLine({ renderers: 2, active: 1, ids: '-20,-10' }),
  ]), {
    records: {
      poolBaseline: censusFrom(baselineLines),
      poolTinted: { participants: tinted.participants, qualifiedTinted: 1 },
    },
  });
  assert.equal(reuse.ok, true, reuse.error);
  assert.deepEqual(reuse.reused, [ID_B]);
});

test('pool census rest fails when a tinted id is missing from the rest census', () => {
  const baselineLines = [
    poolLine({ ...A, active: false }),
    poolLine({ ...B, active: false }),
    poolSummaryLine({ renderers: 2, ids: '-20,-10' }),
  ];
  const tintedLines = [
    poolLine({ ...A, active: true, hasBlock: true, baseColor: GREEN }),
    poolLine({ ...B, active: true, hasBlock: true, baseColor: GREEN }),
    poolSummaryLine({ renderers: 2, active: 2, withBlock: 2, green: 2, ids: '-20,-10' }),
  ];
  const rest = assertPoolCensus({
    phase: 'rest',
    baselineRecord: 'poolBaseline',
    tintedRecord: 'poolTinted',
    overrideColor: GREEN,
  }, censusFrom([
    poolLine({ ...A, active: false, hasBlock: false }),
    poolSummaryLine({ renderers: 1, ids: '-10' }),
  ]), { records: recordsWith(baselineLines, tintedLines) });
  assert.equal(rest.ok, false);
  assert.equal(rest.error, `tinted object missing at rest: ${ID_B}`);
});

test('pool census rest accepts the cases-10 shape (12 rows, all T present)', () => {
  const crystals = [
    { rendererId: -779438, goId: -779432, go: 'Impact_CrystalCenter_Spikes05' },
    { rendererId: -779430, goId: -779424, go: 'Impact_CrystalCenter_Spikes03' },
    { rendererId: -779422, goId: -779416, go: 'Impact_CrystalCenter_Spikes02' },
    { rendererId: -779414, goId: -779408, go: 'Impact_CrystalCenter_Spikes01' },
    { rendererId: -779478, goId: -779472, go: 'Impact_CrystalCenter_SpikeCenter' },
    { rendererId: -775186, goId: -775180, go: 'Impact_CrystalCenter_Spikes05' },
    { rendererId: -775178, goId: -775172, go: 'Impact_CrystalCenter_Spikes03' },
    { rendererId: -775170, goId: -775164, go: 'Impact_CrystalCenter_Spikes02' },
    { rendererId: -775162, goId: -775156, go: 'Impact_CrystalCenter_Spikes01' },
    { rendererId: -775226, goId: -775220, go: 'Impact_CrystalCenter_SpikeCenter' },
  ];
  const debris = [
    { rendererId: -775080, goId: -775074, go: 'Impact_Debris_GroundLimit 3 (1)' },
    { rendererId: -775072, goId: -775066, go: 'Impact_Debris_GroundLimit 3' },
  ];
  const all = [...crystals, ...debris];
  const ids = all.map((r) => r.rendererId).join(',');
  const idle = (row) => poolLine({ ...row, active: false, hasBlock: false });
  const tinted = (row) => poolLine({ ...row, active: true, hasBlock: true, baseColor: GREEN });
  const baselineLines = [
    ...all.map(idle),
    poolSummaryLine({ renderers: 12, active: 0, withBlock: 0, ids }),
  ];
  const tintedLines = [
    ...crystals.map(tinted),
    ...debris.map(idle),
    poolSummaryLine({ renderers: 12, active: 10, withBlock: 10, green: 10, ids }),
  ];
  const restLines = [
    ...all.map(idle),
    poolSummaryLine({ renderers: 12, active: 0, withBlock: 0, ids }),
  ];
  const ctx = { records: recordsWith(baselineLines, tintedLines) };
  assert.equal(ctx.records.poolTinted.qualifiedTinted, 10);
  assert.equal(ctx.records.poolTinted.participants.length, 10);
  const rest = assertPoolCensus({
    phase: 'rest',
    baselineRecord: 'poolBaseline',
    tintedRecord: 'poolTinted',
    overrideColor: GREEN,
  }, censusFrom(restLines), ctx);
  assert.equal(rest.ok, true, rest.error);
  assert.equal(rest.qualifiedTinted, 10);
  assert.equal(censusFrom(restLines).rows.length, 12);
  for (const id of ctx.records.poolTinted.participants) {
    assert.ok(censusFrom(restLines).byId[id], `T id ${id} must be a rest row`);
  }

  const reuseLines = [
    ...crystals.map((row) => poolLine({ ...row, active: true, hasBlock: false })),
    ...debris.map(idle),
    poolSummaryLine({ renderers: 12, active: 10, withBlock: 0, ids }),
  ];
  const reuse = assertPoolCensus({
    phase: 'reuse',
    baselineRecord: 'poolBaseline',
    tintedRecord: 'poolTinted',
    overrideColor: GREEN,
  }, censusFrom(reuseLines), ctx);
  assert.equal(reuse.ok, true, reuse.error);
  assert.deepEqual(reuse.reused, ctx.records.poolTinted.participants);
  assert.equal(reuse.qualifiedReused, 10);
});

test('pool census rest fails on residual tint', async () => {
  const baselineLines = [
    poolLine({ ...A, active: false }),
    poolSummaryLine({ renderers: 1, ids: '-10' }),
  ];
  const tintedLines = [
    poolLine({ ...A, active: true, hasBlock: true, baseColor: GREEN }),
    poolSummaryLine({ renderers: 1, active: 1, withBlock: 1, green: 1, ids: '-10' }),
  ];
  const restLines = [
    poolLine({ ...A, active: false, hasBlock: true, baseColor: GREEN }),
    poolSummaryLine({ renderers: 1, active: 0, withBlock: 1, green: 1, ids: '-10' }),
  ];
  const ctx = { records: recordsWith(baselineLines, tintedLines) };
  const rest = assertPoolCensus({
    phase: 'rest',
    baselineRecord: 'poolBaseline',
    tintedRecord: 'poolTinted',
    overrideColor: GREEN,
  }, censusFrom(restLines), ctx);
  assert.equal(rest.ok, false);
  assert.equal(rest.error, 'residual tint');
});

test('poolReuse fails with reuse not observed after retries', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  try {
    const baselineLines = [
      poolLine({ ...A, active: false }),
      poolSummaryLine({ renderers: 1, ids: '-10' }),
    ];
    const tintedLines = [
      poolLine({ ...A, active: true, hasBlock: true, baseColor: GREEN }),
      poolSummaryLine({ renderers: 1, active: 1, withBlock: 1, green: 1, ids: '-10' }),
    ];
    const idleLines = [
      poolLine({ ...A, active: false }),
      poolSummaryLine({ renderers: 1, ids: '-10' }),
    ];
    const base = await executePoolSnippet(tmp, world, {
      record: 'poolBaseline',
      assert: { phase: 'baseline' },
    }, baselineLines);
    const tinted = await executePoolSnippet(tmp, world, {
      record: 'poolTinted',
      assert: { phase: 'tinted', baselineRecord: 'poolBaseline', overrideColor: GREEN },
    }, tintedLines, { ctx: base.ctx, io: base.io, srcFile: base.srcFile });
    assert.equal(tinted.result.ok, true, tinted.result.error);

    const dllDest = path.join(world.reloadFolder.client, 'Progenitor.Scratch.dll');
    const idleDll = base.ctx.poolDll;
    const idleHash = sha256(idleDll);
    const rawCopy = base.io.baseCopyFileSync;
    base.io.copyFileSync = (src, dest) => {
      rawCopy(src, dest);
      if (dest === dllDest) {
        world.pushLog('client', `Reload: image Progenitor.Scratch.dll sha256=${idleHash} size=${idleDll.length} asm=Progenitor.Scratch`);
        for (const text of idleLines) world.pushLog('client', text, { source: 'Progenitor Scratch' });
        world.pushLog('client', `scratch: result: ${idleLines[idleLines.length - 1]}`);
      }
    };

    const reuse = await executeStep({
      kind: 'poolReuse',
      dash: '.frostdash dash',
      waitMs: 5,
      retryWaitMs: 5,
      retries: 3,
      baselineRecord: 'poolBaseline',
      tintedRecord: 'poolTinted',
      overrideColor: GREEN,
      snippet: { side: 'client', file: base.srcFile, expect: 'POOLID summary renderers=' },
    }, base.ctx);
    assert.equal(reuse.ok, false);
    assert.equal(reuse.error, 'reuse not observed');
    assert.equal(reuse.attemptResults.length, 3);
    assert.equal(reuse.retriesUsed, 3);
    assert.equal(reuse.reusedUntinted, 0);
    assert.equal(reuse.capture.available, POOL_REUSE_CAPTURE.available);
    assert.equal(reuse.capture.reason, 'no capture configured on this poolReuse step');
    assert.equal(reuse.capture.reason, POOL_REUSE_CAPTURE.reason);
    assert.equal(reuse.visual.verified, false);
    assert.equal(reuse.visual.frames.length, 0);
  } finally {
    tmp.cleanup();
  }
});

test('missing FROSTSTYLE handler fails the froststyle off step', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    const doc = loadPoolReuseCase();
    const off = doc.steps.find((s) => s.text === '.froststyle off');
    assert.ok(off?.expectLog, 'froststyle off must require a handler line');
    const result = await executeStep(off, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /no handler line for \.froststyle off/);
  } finally {
    tmp.cleanup();
  }
});

test('poolReuse fails on fresh allocation without a reused participant', () => {
  const baselineLines = [
    poolLine({ ...A, active: false }),
    poolSummaryLine({ renderers: 1, ids: '-10' }),
  ];
  const tintedLines = [
    poolLine({ ...A, active: true, hasBlock: true, baseColor: GREEN }),
    poolSummaryLine({ renderers: 1, active: 1, withBlock: 1, green: 1, ids: '-10' }),
  ];
  const freshLines = [
    poolLine({ ...A, active: false }),
    poolLine({ ...B, active: true, hasBlock: false }),
    poolSummaryLine({ renderers: 2, active: 1, ids: '-20,-10' }),
  ];
  const ctx = { records: recordsWith(baselineLines, tintedLines) };
  const reuse = assertPoolCensus({
    phase: 'reuse',
    baselineRecord: 'poolBaseline',
    tintedRecord: 'poolTinted',
    overrideColor: GREEN,
  }, censusFrom(freshLines), ctx);
  assert.equal(reuse.ok, false);
  assert.equal(reuse.error, 'fresh allocation without reused participant');
});

test('pool census fails on a malformed line', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  try {
    const lines = [
      'POOLID rendererId=not-a-number goId=-11 active=False slot=0 hasBlock=False baseColor=none go=bad',
      poolSummaryLine({ renderers: 1, ids: '-10' }),
    ];
    const ran = await executePoolSnippet(tmp, world, {
      record: 'poolBaseline',
      assert: { phase: 'baseline' },
    }, lines);
    assert.equal(ran.result.ok, false);
    assert.equal(ran.result.error, 'malformed census line');
  } finally {
    tmp.cleanup();
  }
});

test('logEntity with track true is destroyed on cleanup', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    world.pushLog('server', 'FROSTDASH edge=Start ring=Entity(8:1)');
    world.entities.set('8:1', { exists: true });
    await executeStep({
      kind: 'logEntity',
      name: 'ring',
      regex: 'ring=Entity\\((\\d+:\\d+)\\)',
      track: true,
    }, ctx);
    assert.equal(ctx.tracker.get('ring').skipCleanup, false);
  } finally {
    tmp.cleanup();
  }
});

function captureCtx(tmp, world, extra = {}) {
  const spawnSyncFn = extra.spawnSync ?? (extra.spawn ? undefined : fakeObserveSpawner(extra.observe ?? {}));
  return ctxFor(tmp, world, {
    ...extra,
    repoRoot: extra.repoRoot ?? tmp.dir,
    outDir: extra.outDir ?? tmp.dir,
    spawnSync: spawnSyncFn,
    spawn: extra.spawn,
  });
}

function assertObserveShotCommand(call, { outPath } = {}) {
  assert.match(String(call.cmd).replace(/\\/g, '/'), /WindowsPowerShell\/v1\.0\/powershell\.exe$/i);
  assert.deepEqual(call.args.slice(0, 4), ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File']);
  assert.ok(String(call.args[4]).replace(/\\/g, '/').endsWith('tools/observe.ps1'));
  assert.equal(call.args[5], 'shot');
  const flag = (name) => call.args[call.args.indexOf(name) + 1];
  assert.equal(flag('-Window'), 'client');
  assert.equal(flag('-Method'), CAPTURE_DEFAULT_METHOD);
  assert.equal(flag('-Region'), 'window');
  assert.equal(flag('-MaxWidth'), '1920');
  if (outPath) assert.equal(flag('-Out'), outPath);
  assert.ok(!call.args.some((a) => /AllowGameInput|^key$|^type$|^click$|^move$|^scroll$/i.test(String(a))));
  assert.equal(call.spawnOpts.stdio[0], 'ignore');
  assert.equal(call.spawnOpts.timeout, CAPTURE_DEFAULT_TIMEOUT_MS);
  assert.equal(call.spawnOpts.windowsHide, true);
}

test('capture step succeeds with a fake spawner that writes a PNG', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner({ stdout: 'saved\n', stderr: 'observe: PrintWindow\n' });
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const result = await executeStep({
      kind: 'capture',
      name: 'effect',
      record: 'shot1',
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(spawn.calls.length, 1);
    assertObserveShotCommand(spawn.calls[0], { outPath: result.path });
    assert.equal(existsSync(result.path), true);
    assert.equal(statSync(result.path).size, 128);
    assert.equal(result.bytes, 128);
    assert.equal(typeof result.wallMs, 'number');
    assert.ok(result.traceIdRange && typeof result.traceIdRange.from === 'number');
    assert.equal(result.exitCode, 0);
    assert.match(String(result.stdoutTail), /saved/);
    assert.match(String(result.stderrTail), /PrintWindow/);
    assert.equal(result.visual.verified, false);
    assert.equal(ctx.records.shot1.path, result.path);
    const expectedArgv = observeShotArgv({
      script: path.join(tmp.dir, 'tools', 'observe.ps1'),
      outPath: result.path,
    });
    assert.deepEqual(spawn.calls[0].args, expectedArgv);
    assert.equal(spawn.calls[0].cmd, windowsPowerShell51Path());
    assert.equal(result.command, formatCommandLine(windowsPowerShell51Path(), expectedArgv));
  } finally {
    tmp.cleanup();
  }
});

test('capture step fails on a non-zero observe exit', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner({ exitCode: 5, stderr: 'observe: blank image\n' });
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const result = await executeStep({ kind: 'capture', name: 'blank' }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.exitCode, 5);
    assert.match(String(result.error), /observe\.ps1 shot exited 5/);
    assert.match(String(result.stderrTail), /blank image/);
  } finally {
    tmp.cleanup();
  }
});

test('capture step fails on a zero-byte file', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner({ zeroByte: true });
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const result = await executeStep({ kind: 'capture', name: 'empty' }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /capture file empty/);
  } finally {
    tmp.cleanup();
  }
});

test('capture step fails when the file is missing', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner({ missingFile: true });
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const result = await executeStep({ kind: 'capture', name: 'missing' }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /capture file missing/);
  } finally {
    tmp.cleanup();
  }
});

test('capture step fails on timeout', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner({ timeout: true });
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const result = await executeStep({ kind: 'capture', name: 'slow', timeoutMs: 10000 }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /capture timed out after 10000ms/);
    assert.equal(spawn.calls[0].spawnOpts.timeout, 10000);
  } finally {
    tmp.cleanup();
  }
});

test('capture burst records N frames', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner();
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const result = await executeStep({
      kind: 'capture',
      name: 'burst',
      delayMs: 0,
      burst: { count: 3, intervalMs: 10 },
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.frames.length, 3);
    assert.equal(spawn.calls.length, 3);
    assert.equal(result.capture.available, true);
    assert.equal(result.capture.paths.length, 3);
    for (const frame of result.frames) {
      assert.equal(existsSync(frame.path), true);
      assert.ok(frame.bytes > 0);
      assert.equal(frame.exitCode, 0);
    }
    assert.deepEqual(result.frames.map((f) => f.offsetMs), [0, 10, 20]);
  } finally {
    tmp.cleanup();
  }
});

test('capturePrepare records warmup duration and discards the file', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner();
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const result = await executeStep({
      kind: 'capturePrepare',
      name: 'warmup PrintWindow',
      record: 'capturePrepared',
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.discarded, true);
    assert.equal(result.path, null);
    assert.equal(typeof result.warmupMs, 'number');
    assert.equal(spawn.calls.length, 1);
    assertObserveShotCommand(spawn.calls[0]);
    const out = spawn.calls[0].args[spawn.calls[0].args.indexOf('-Out') + 1];
    assert.equal(existsSync(out), false);
    assert.equal(ctx.records.capturePrepared.discarded, true);
    assert.equal(ctx.records.capturePrepared.ok, true);
  } finally {
    tmp.cleanup();
  }
});

test('capture refuses Screen method and game input', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner();
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const screen = await executeStep({ kind: 'capture', method: 'Screen' }, ctx);
    assert.equal(screen.ok, false);
    assert.match(String(screen.error), /PrintWindow/);
    const input = await executeStep({ kind: 'capture', allowGameInput: true }, ctx);
    assert.equal(input.ok, false);
    assert.match(String(input.error), /must not send input/);
    assert.equal(spawn.calls.length, 0);
  } finally {
    tmp.cleanup();
  }
});

test('capture refuses to spawn observe.ps1 without an injected spawnSync under the test runner', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world, { repoRoot: tmp.dir });
    const result = await executeStep({ kind: 'capture', name: 'uninjected' }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /refuses to spawn observe\.ps1/);
  } finally {
    tmp.cleanup();
  }
});

test('pool-reuse-identity JSON includes capturePrepare, tinted frame, and reuse burst', () => {
  const doc = loadPoolReuseCase();
  assert.ok(STEP_KINDS.includes('capture'));
  assert.ok(STEP_KINDS.includes('capturePrepare'));
  assert.equal(doc.steps[1].kind, 'capturePrepare');
  const tinted = doc.steps.find((s) => s.kind === 'capture' && s.prepare !== true);
  assert.ok(tinted);
  assert.equal(tinted.method, 'PrintWindow');
  const reuse = doc.steps.find((s) => s.kind === 'poolReuse');
  assert.equal(reuse.capture.delayMs, 100);
  assert.equal(reuse.capture.burst.count, 3);
  assert.equal(reuse.capture.burst.intervalMs, 400);
  assert.equal(reuse.capture.delayMs + (reuse.capture.burst.count - 1) * reuse.capture.burst.intervalMs, 900);
  assert.ok(reuse.capture.delayMs + (reuse.capture.burst.count - 1) * reuse.capture.burst.intervalMs < reuse.waitMs);
  assert.equal(reuse.maxCensusDelayMs, CAPTURE_DEFAULT_MAX_CENSUS_DELAY_MS);
  assert.match(CAPTURE_INSPECT_NOTE, /does not analyse pixels/);
});

async function setupReuseCensus(tmp, { reuseActive = true } = {}) {
  const world = createFakeWorld({ clientReload: path.join(tmp.dir, 'reload-client') });
  const baselineLines = [
    poolLine({ ...A, active: false }),
    poolSummaryLine({ renderers: 1, ids: '-10' }),
  ];
  const tintedLines = [
    poolLine({ ...A, active: true, hasBlock: true, baseColor: GREEN }),
    poolSummaryLine({ renderers: 1, active: 1, withBlock: 1, green: 1, ids: '-10' }),
  ];
  const reuseLines = reuseActive
    ? [
      poolLine({ ...A, active: true, hasBlock: false }),
      poolSummaryLine({ renderers: 1, active: 1, ids: '-10' }),
    ]
    : [
      poolLine({ ...A, active: false }),
      poolSummaryLine({ renderers: 1, ids: '-10' }),
    ];
  const base = await executePoolSnippet(tmp, world, {
    record: 'poolBaseline',
    assert: { phase: 'baseline' },
  }, baselineLines);
  const tinted = await executePoolSnippet(tmp, world, {
    record: 'poolTinted',
    assert: { phase: 'tinted', baselineRecord: 'poolBaseline', overrideColor: GREEN },
  }, tintedLines, { ctx: base.ctx, io: base.io, srcFile: base.srcFile });
  assert.equal(tinted.result.ok, true, tinted.result.error);
  const dllDest = path.join(world.reloadFolder.client, 'Progenitor.Scratch.dll');
  const dll = base.ctx.poolDll;
  const hash = sha256(dll);
  const rawCopy = base.io.baseCopyFileSync;
  base.io.copyFileSync = (src, dest) => {
    rawCopy(src, dest);
    if (dest === dllDest) {
      world.pushLog('client', `Reload: image Progenitor.Scratch.dll sha256=${hash} size=${dll.length} asm=Progenitor.Scratch`);
      for (const text of reuseLines) world.pushLog('client', text, { source: 'Progenitor Scratch' });
      world.pushLog('client', `scratch: result: ${reuseLines[reuseLines.length - 1]}`);
    }
  };
  return { world, ctx: base.ctx, srcFile: base.srcFile };
}

function reuseCaptureSpec(extra = {}) {
  return {
    delayMs: 100,
    burst: { count: 3, intervalMs: 400 },
    timeoutMs: extra.timeoutMs ?? 10000,
    requireFrames: extra.requireFrames,
    maxInFlight: extra.maxInFlight,
    collectTimeoutMs: extra.collectTimeoutMs,
    maxWidth: 1920,
    window: 'client',
    region: 'window',
    method: 'PrintWindow',
  };
}

function reuseStep(srcFile, extra = {}) {
  return {
    kind: 'poolReuse',
    name: 'untinted reuse',
    dash: '.frostdash dash',
    waitMs: extra.waitMs ?? 1500,
    maxCensusDelayMs: extra.maxCensusDelayMs ?? 2000,
    retries: extra.retries ?? 1,
    retryWaitMs: extra.retryWaitMs ?? 5,
    baselineRecord: 'poolBaseline',
    tintedRecord: 'poolTinted',
    overrideColor: GREEN,
    capture: extra.capture === null ? undefined : reuseCaptureSpec(extra.capture ?? extra),
    snippet: { side: 'client', file: srcFile, expect: 'POOLID summary renderers=' },
  };
}

test('poolReuse census delay stays ~1500 ms while 1 s shots run in the background', async () => {
  const tmp = tempDir();
  try {
    const { ctx, srcFile } = await setupReuseCensus(tmp);
    const spawn = fakeObserveAsyncSpawner({ durationMs: 1000, fs: ctx.fs });
    ctx.spawn = spawn;
    const reuse = await executeStep(reuseStep(srcFile), ctx);
    assert.equal(reuse.ok, true, reuse.error);
    assert.equal(reuse.censusDelayMs, 1500);
    assert.ok(reuse.censusDelayMs <= CAPTURE_DEFAULT_MAX_CENSUS_DELAY_MS);
    assert.equal(reuse.frames.length, 3);
    assert.deepEqual(reuse.frames.map((f) => f.offsetMs), [100, 500, 900]);
    for (const frame of reuse.frames) {
      assert.equal(typeof frame.startedMs, 'number');
      assert.equal(typeof frame.endedMs, 'number');
      assert.ok(frame.endedMs >= frame.startedMs);
      assert.ok(frame.startedAt);
      assert.ok(frame.endedAt);
      assert.equal(frame.ok, true);
      assert.ok(frame.bytes > 0);
    }
    assert.equal(spawn.calls.length, 3);
    assert.ok(spawn.maxInFlight <= 4);
  } finally {
    tmp.cleanup();
  }
});

test('poolReuse fails census late when dash-to-census exceeds the bound', async () => {
  const tmp = tempDir();
  try {
    const { ctx, srcFile } = await setupReuseCensus(tmp);
    ctx.spawn = fakeObserveAsyncSpawner({ fs: ctx.fs });
    const reuse = await executeStep(reuseStep(srcFile, {
      waitMs: 2500,
      maxCensusDelayMs: 2000,
    }), ctx);
    assert.equal(reuse.ok, false);
    assert.equal(reuse.error, 'census late: 2500 ms');
    assert.equal(reuse.censusDelayMs, 2500);
    assert.equal(reuse.maxCensusDelayMs, 2000);
  } finally {
    tmp.cleanup();
  }
});

test('tinted capture waitMs is not stretched by a 1 s PrintWindow shot', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveAsyncSpawner({ durationMs: 1000 });
  try {
    const ctx = captureCtx(tmp, world, { spawn, spawnSync: fakeObserveSpawner() });
    const result = await executeStep({
      kind: 'capture',
      name: 'tinted window',
      delayMs: 100,
      waitMs: 1500,
      maxCensusDelayMs: 2000,
      timeoutMs: 10000,
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.censusDelayMs, 1500);
    assert.equal(typeof result.startedMs, 'number');
    assert.equal(typeof result.endedMs, 'number');
    assert.ok(result.endedMs >= result.startedMs);
    assert.equal(spawn.calls.length, 1);
  } finally {
    tmp.cleanup();
  }
});

test('a shot still running at the collect bound is timed out and fails when requireFrames is true', async () => {
  const tmp = tempDir();
  try {
    const { ctx, srcFile } = await setupReuseCensus(tmp);
    const spawn = fakeObserveAsyncSpawner({ hang: true, fs: ctx.fs });
    ctx.spawn = spawn;
    const reuse = await executeStep(reuseStep(srcFile, {
      waitMs: 1500,
      capture: {
        timeoutMs: 80,
        collectTimeoutMs: 40,
        requireFrames: true,
      },
    }), ctx);
    assert.equal(reuse.ok, false);
    assert.match(String(reuse.error), /capture timed out after 40ms/);
    assert.equal(reuse.censusDelayMs, 1500);
    assert.ok(reuse.frames.length >= 1);
    assert.ok(reuse.frames.every((f) => f.timedOut === true));
  } finally {
    tmp.cleanup();
  }
});

test('a timed-out shot does not fail poolReuse when requireFrames is false', async () => {
  const tmp = tempDir();
  try {
    const { ctx, srcFile } = await setupReuseCensus(tmp);
    const spawn = fakeObserveAsyncSpawner({ hang: true, fs: ctx.fs });
    ctx.spawn = spawn;
    const reuse = await executeStep(reuseStep(srcFile, {
      waitMs: 1500,
      capture: {
        timeoutMs: 80,
        collectTimeoutMs: 40,
        requireFrames: false,
      },
    }), ctx);
    assert.equal(reuse.ok, true, reuse.error);
    assert.equal(reuse.censusDelayMs, 1500);
    assert.equal(reuse.reusedUntinted, 1);
    assert.ok(reuse.frames.some((f) => f.timedOut === true));
  } finally {
    tmp.cleanup();
  }
});

function hangingChildSpawn({ exitOn = 'SIGTERM' } = {}) {
  const calls = [];
  function spawn(cmd, args, spawnOpts) {
    calls.push({ cmd, args, spawnOpts });
    const child = new EventEmitter();
    child.stdout = new PassThrough();
    child.stderr = new PassThrough();
    child.pid = 1;
    child.exitCode = null;
    child.killed = false;
    child.signals = [];
    let done = false;
    function finish(status, signal) {
      if (done) return;
      done = true;
      child.exitCode = status;
      child.signalCode = signal ?? null;
      child.stdout.end();
      child.stderr.end();
      child.emit('close', status, signal ?? null);
    }
    child.kill = (sig = 'SIGTERM') => {
      child.signals.push(sig);
      if (sig === exitOn || sig === 'SIGKILL') {
        child.killed = true;
        finish(null, sig);
      }
    };
    return child;
  }
  spawn.calls = calls;
  return spawn;
}

test('cancelTrackedChildren kills a fake long-running capture child and refuses new ones', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const spawn = hangingChildSpawn({ exitOn: 'SIGKILL' });
    const ctx = captureCtx(tmp, world, { spawn, children: createChildRegistry(), childKillGraceMs: 20 });
    const started = executeStep({
      kind: 'capture',
      name: 'hang',
      timeoutMs: 30000,
      requireFrames: false,
    }, ctx);
    for (let i = 0; i < 50 && spawn.calls.length === 0; i++) {
      await new Promise((resolve) => setImmediate(resolve));
    }
    assert.equal(spawn.calls.length, 1);
    const summary = await cancelTrackedChildren(ctx, { graceMs: 20 });
    assert.equal(summary.requested, true);
    assert.equal(summary.children, 1);
    assert.equal(summary.killed, 1);
    assert.ok(summary.waitedMs >= 10, `expected grace wait, got ${summary.waitedMs}`);
    const result = await started;
    assert.equal(result.cancelled === true || result.ok === false || result.ok === true, true);
    const refused = await executeStep({
      kind: 'capture',
      name: 'after-stop',
      timeoutMs: 1000,
    }, ctx);
    assert.equal(refused.cancelled, true);
    assert.match(String(refused.error), /cancelled/);
    assert.equal(spawn.calls.length, 1);
  } finally {
    tmp.cleanup();
  }
});

test('cancelTrackedChildren records a child that exits on the first signal', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const spawn = hangingChildSpawn({ exitOn: 'SIGTERM' });
    const ctx = captureCtx(tmp, world, { spawn, children: createChildRegistry(), childKillGraceMs: 2000 });
    const started = executeStep({
      kind: 'capture',
      name: 'hang',
      timeoutMs: 30000,
      requireFrames: false,
    }, ctx);
    for (let i = 0; i < 50 && spawn.calls.length === 0; i++) {
      await new Promise((resolve) => setImmediate(resolve));
    }
    const t0 = Date.now();
    const summary = await cancelTrackedChildren(ctx, { graceMs: 2000 });
    const waited = Date.now() - t0;
    assert.equal(summary.requested, true);
    assert.equal(summary.children, 1);
    assert.equal(summary.killed, 1);
    assert.ok(summary.waitedMs < 500, `first-signal exit should not wait the grace; waitedMs=${summary.waitedMs}`);
    assert.ok(waited < 500, `cancel should return promptly, took ${waited}ms`);
    await started;
  } finally {
    tmp.cleanup();
  }
});

test('capture in-flight cap prevents unbounded spawn', async () => {
  const tmp = tempDir();
  try {
    const { ctx, srcFile } = await setupReuseCensus(tmp);
    const spawn = fakeObserveAsyncSpawner({ fs: ctx.fs });
    ctx.spawn = spawn;
    const reuse = await executeStep(reuseStep(srcFile, {
      waitMs: 1500,
      capture: {
        requireFrames: true,
        maxInFlight: 3,
      },
    }), ctx);
    const capped = await executeStep({
      ...reuseStep(srcFile, {
        waitMs: 1500,
        capture: {
          requireFrames: true,
          maxInFlight: 3,
        },
      }),
      capture: {
        ...reuseCaptureSpec({ requireFrames: true, maxInFlight: 3 }),
        delayMs: 0,
        burst: { count: 6, intervalMs: 0 },
      },
    }, ctx);
    assert.equal(capped.ok, false);
    assert.match(String(capped.error), /capture in-flight cap/);
    assert.equal(spawn.calls.length, 3 + 3);
    assert.ok(spawn.maxInFlight <= 3);
    assert.equal(reuse.ok, true, reuse.error);
  } finally {
    tmp.cleanup();
  }
});

test('capture is deferred while a main-thread op is in flight', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner();
  let release;
  const orig = world.request.bind(world);
  world.request = (side, op, args) => {
    if (op === 'ping') {
      return new Promise((resolve) => {
        release = () => resolve(orig(side, op, args));
      });
    }
    return orig(side, op, args);
  };
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const pendingPing = ctx.bridge.call('client', 'ping');
    await new Promise((r) => setImmediate(r));
    const held = ctx.bridge.mainThreadInFlight('client');
    assert.ok(held, 'ping should be in flight');
    assert.equal(held.op, 'ping');
    assert.equal(ctx.bridge.mainThreadInFlight('server'), null);

    let finished = false;
    const captureP = executeStep({ kind: 'capture', name: 'deferred', side: 'client' }, ctx).then((res) => {
      finished = true;
      return res;
    });
    await new Promise((r) => setTimeout(r, 30));
    assert.equal(spawn.calls.length, 0, 'must not spawn PrintWindow while ping is in flight');
    assert.equal(finished, false);

    release();
    await pendingPing;
    const result = await captureP;
    assert.equal(result.ok, true, result.error);
    assert.equal(spawn.calls.length, 1);
    assert.equal(ctx.bridge.mainThreadInFlight('client'), null);
  } finally {
    tmp.cleanup();
  }
});

test('soak schedules captures between samples and records captureWindows against ping windows', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner();
  try {
    const ctx = captureCtx(tmp, world, { spawnSync: spawn });
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 2,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      capture: { intervalSeconds: 60 },
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.ok(Array.isArray(result.captureWindows));
    assert.ok(result.captureWindows.length >= 2, `expected at least 2 capture windows, got ${result.captureWindows.length}`);
    assert.equal(spawn.calls.length, result.captureWindows.length);
    const pings = (result.samples ?? []).filter((s) => s.ping && s.ping.ok && s.ping.startedAtMs != null);
    assert.ok(pings.length >= 2);
    for (const win of result.captureWindows) {
      assert.ok(win.startedAt);
      assert.ok(win.endedAt);
      assert.ok(Number.isFinite(win.startedAtMs));
      assert.ok(Number.isFinite(win.endedAtMs));
      assert.ok(win.nearestPing);
      assert.equal(win.nearestPing.overlap, false);
      assert.ok(win.nearestPing.gapMs == null || win.nearestPing.gapMs >= 0);
      for (const ping of pings) {
        const overlap = win.startedAtMs < ping.endedAtMs && ping.startedAtMs < win.endedAtMs;
        assert.equal(overlap, false, `capture ${win.startedAtMs}-${win.endedAtMs} overlapped ping ${ping.startedAtMs}-${ping.endedAtMs}`);
      }
    }
    const started = (result.samples ?? []).filter((s) => s.captureStarted);
    assert.ok(started.length >= 1);
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

test('soak perf windows rotate with explicit boundaries and gaps', async () => {
  const tmp = tempDir();
  const clock = fakeClock();
  const world = createFakeWorld({ now: () => clock.now() });
  try {
    const ctx = ctxFor(tmp, world, { clock, side: 'client' });
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 2,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      bridgeStatusIntervalSeconds: 10,
      capture: null,
      perf: { windowSeconds: 20, gapSeconds: 2 },
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.deepEqual(result.perf, { windowSeconds: 20, gapSeconds: 2 });
    const rows = result.perfWindows ?? [];
    const windows = rows.filter((r) => r.kind === 'window');
    const gaps = rows.filter((r) => r.kind === 'gap');
    assert.ok(windows.length >= 2, `expected several windows, got ${windows.length}`);
    assert.ok(gaps.length >= 1, 'gaps must be listed explicitly');
    const soakMs = 2 * 60 * 1000;
    for (const win of windows) {
      assert.ok(win.startedAt);
      assert.ok(win.endedAt);
      assert.ok(Number.isFinite(win.startedAtMs));
      assert.ok(Number.isFinite(win.endedAtMs));
      const dur = win.endedAtMs - win.startedAtMs;
      assert.ok(dur < soakMs, 'a window must not cover the whole soak');
      assert.ok(dur <= 45_000, `window ${win.startedAtMs}-${win.endedAtMs} lasted ${dur} ms`);
      assert.equal(win.missingInterval, false);
    }
    for (const gap of gaps) {
      assert.equal(gap.kind, 'gap');
      assert.equal(gap.gapSeconds, 2);
      assert.ok(Number.isFinite(gap.startedAtMs));
      assert.ok(Number.isFinite(gap.endedAtMs));
      assert.equal(gap.endedAtMs - gap.startedAtMs, 2000);
    }
    assert.ok(rows.some((r, i) => r.kind === 'window' && rows[i + 1]?.kind === 'gap'));
    const starts = world.calls.filter((c) => c.op === 'perf' && c.args?.action === 'start');
    assert.ok(starts.length >= 2);
    assert.ok(starts.every((c) => c.args.seconds === 20));
    const live = world.calls.filter((c) => c.op === 'perf_status');
    assert.ok(live.length >= result.samples.length);
    assert.ok(result.samples.every((s) => s.perf && Object.prototype.hasOwnProperty.call(s.perf, 'frameCount')));
    assert.ok(result.samples.every((s) => Object.prototype.hasOwnProperty.call(s.perf, 'sampleSilenceMs')));
    assert.ok(result.samples.every((s) => Object.prototype.hasOwnProperty.call(s.perf, 'running')));
    assert.ok(result.samples.every((s) => Object.prototype.hasOwnProperty.call(s.perf, 'captureId')));
    assert.ok(Number.isFinite(result.perfLatency.ping.max) || result.perfLatency.ping.samples === 0);
    assert.ok(result.perfLatency.perfStatus.samples >= 1);
    assert.equal(result.responsivenessFailed, false);
  } finally {
    tmp.cleanup();
  }
});

test('soak records a missing interval when a perf window closes during a freeze', async () => {
  const tmp = tempDir();
  const clock = fakeClock();
  const world = createFakeWorld({ now: () => clock.now() });
  holdFakeFrame(world, 7);
  try {
    const ctx = ctxFor(tmp, world, { clock, side: 'client' });
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 1,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      capture: null,
      dumpAfterSeconds: 15,
      failAfterSeconds: 90,
      perf: { windowSeconds: 20, gapSeconds: 2 },
    }, ctx);
    const windows = (result.perfWindows ?? []).filter((r) => r.kind === 'window');
    assert.ok(windows.length >= 1, 'expected a window that closed during the freeze');
    const missing = windows.filter((w) => w.missingInterval === true);
    assert.ok(missing.length >= 1, 'window that closed during a freeze must note the missing interval');
    assert.match(String(missing[0].missingIntervalNote), /freeze|absent/i);
    assert.ok(result.freezes.length >= 1);
    const correlated = result.freezes[0].perfWindow;
    assert.notEqual(correlated, 'no window active');
    assert.equal(correlated.captureId, missing[0].captureId);
  } finally {
    tmp.cleanup();
  }
});

test('soak percentiles are computed for ping RTT and perf_status duration', async () => {
  const tmp = tempDir();
  const clock = fakeClock();
  const world = createFakeWorld({ now: () => clock.now() });
  try {
    const ctx = ctxFor(tmp, world, { clock, side: 'client' });
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 1,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      capture: null,
      perf: { windowSeconds: 30, gapSeconds: 2 },
    }, ctx);
    assert.equal(result.ok, true, result.error);
    for (const key of ['ping', 'perfStatus']) {
      const row = result.perfLatency[key];
      assert.ok(row, key);
      assert.ok(Object.prototype.hasOwnProperty.call(row, 'max'));
      assert.ok(Object.prototype.hasOwnProperty.call(row, 'p50'));
      assert.ok(Object.prototype.hasOwnProperty.call(row, 'p95'));
      assert.ok(Object.prototype.hasOwnProperty.call(row, 'p99'));
      assert.ok(Number.isFinite(row.samples));
    }
    assert.ok(result.perfLatency.perfStatus.samples >= 1);
    assert.ok(result.perfLatency.ping.samples >= 1);
    assert.ok(result.sampleGaps);
    assert.ok(Number.isFinite(result.sampleGaps.maxGapMs));
  } finally {
    tmp.cleanup();
  }
});

test('a recovered 30 s freeze fails soak responsiveness', async () => {
  const tmp = tempDir();
  const clock = fakeClock();
  const world = createFakeWorld({ now: () => clock.now() });
  holdFakeFrame(world, 42);
  unfreezeAfterStatusCalls(world, 5);
  try {
    const ctx = ctxFor(tmp, world, { clock, side: 'client' });
    const result = await executeStep({
      kind: 'soak',
      side: 'client',
      minutes: 1,
      pingIntervalSeconds: 30,
      logsIntervalSeconds: 15,
      capture: null,
      dumpAfterSeconds: 15,
      failAfterSeconds: 90,
      perf: { windowSeconds: 300, gapSeconds: 2 },
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.freezes.length, 1);
    assert.ok(result.freezes[0].recoverySample);
    assert.ok(result.freezes[0].mainThreadFrozenMs >= 30_000);
    assert.equal(result.responsivenessFailed, true);
    assert.ok(result.maxStallMs >= 30_000);
    assert.equal(result.stallThresholdMs, 5000);
    assert.notEqual(result.freezes[0].perfWindow, 'no window active');
  } finally {
    tmp.cleanup();
  }
});

