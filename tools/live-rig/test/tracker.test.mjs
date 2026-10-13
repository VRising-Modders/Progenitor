import test from 'node:test';
import assert from 'node:assert/strict';
import { createTracker, isDeniedPlayerPrefab, playerOwnedIds, contractMismatchFinding } from '../lib/tracker.mjs';
import { fakeClock } from '../lib/clock.mjs';
import { createFakeWorld, tempDir, testBridge } from './helpers.mjs';

function entityCalls(world, action) {
  return world.calls.filter((c) => c.op === 'entity' && c.args.action === action);
}

function inspectCalls(world) {
  return world.calls.filter((c) => c.op === 'inspect');
}

function attachedLength(inspectResult) {
  const comps = inspectResult?.components ?? [];
  const buf = comps.find((c) => /AttachedBuffer/i.test(c.component ?? ''));
  return buf?.length ?? 0;
}

test('cleanupAll times out when a tracked entity stays present', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 1000, pollMs: 250 });
    tracker.adopt({ name: 'rat', entity: '100:1', prefab: 'CHAR_Critter_Rat', by: 'test', hostile: false });
    world.entities.set('100:1', { exists: true, prefab: 'CHAR_Critter_Rat' });
    world.sticky.add('100:1');
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, false);
    assert.equal(result.remaining.length, 1);
    assert.equal(result.remaining[0].entity, '100:1');
    assert.ok(result.elapsedMs >= 1000);
  } finally {
    tmp.cleanup();
  }
});

test('cleanupAll succeeds when destroy makes the entity absent', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 1000, pollMs: 250 });
    tracker.adopt({ name: 'rat', entity: '100:1', prefab: 'CHAR_Critter_Rat', by: 'test', hostile: false });
    world.entities.set('100:1', { exists: true, prefab: 'CHAR_Critter_Rat' });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, true);
    assert.deepEqual(result.remaining, []);
    const sweep = await tracker.existsSweep();
    assert.equal(sweep.ok, true);
  } finally {
    tmp.cleanup();
  }
});

test('the player character cannot be adopted', () => {
  const tracker = createTracker({ bridge: { call: async () => ({}) } });
  tracker.setForbidden(['1:1', '2:1']);
  assert.throws(
    () => tracker.adopt({ name: 'player', entity: '1:1', prefab: 'CHAR_Vampire', by: 'test' }),
    /player's character or user/,
  );
});

test('CHAR_Vampire* prefabs and players/users ids are the adopt deny list', () => {
  assert.equal(isDeniedPlayerPrefab('CHAR_VampireMale'), true);
  assert.equal(isDeniedPlayerPrefab({ hash: 0, name: 'CHAR_VampireFemale' }), true);
  assert.equal(isDeniedPlayerPrefab('CHAR_Critter_Rat'), false);
  assert.equal(isDeniedPlayerPrefab('User'), false);
  const ids = playerOwnedIds(
    { players: [{ entity: '1:1', userEntity: '2:1' }] },
    { users: [{ entity: '2:1', character: '1:1' }] },
  );
  assert.equal(ids.has('1:1'), true);
  assert.equal(ids.has('2:1'), true);
});

test('an already-absent entity is never listed as remaining', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 1000, pollMs: 250 });
    tracker.adopt({ name: 'thief', entity: '318945:79', prefab: 'CHAR_Bandit_Thief', by: 'test', hostile: true });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, true);
    assert.deepEqual(result.remaining, []);
    assert.deepEqual(result.unconfirmed, []);
    const sweep = await tracker.existsSweep();
    assert.equal(sweep.ok, true);
    assert.deepEqual(sweep.remaining, []);
  } finally {
    tmp.cleanup();
  }
});

test('exists query failure lists the entity as unconfirmed, not remaining or gone', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
    tracker.adopt({ name: 'thief', entity: '318945:79', prefab: 'CHAR_Bandit_Thief', by: 'test', hostile: true });
    world.entities.set('318945:79', { exists: false, prefab: 'CHAR_Bandit_Thief' });
    world.throwOn('entity', 'connect ECONNREFUSED 127.0.0.1:27342', { action: 'exists', once: false });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, false);
    assert.equal(result.remaining.length, 0);
    assert.equal(result.unconfirmed.length, 1);
    assert.equal(result.unconfirmed[0].entity, '318945:79');
    assert.match(result.unconfirmed[0].error, /ECONNREFUSED/);
    const sweep = await tracker.existsSweep();
    assert.equal(sweep.ok, false);
    assert.equal(sweep.remaining.length, 0);
    assert.equal(sweep.unconfirmed.length, 1);
    assert.match(sweep.unconfirmed[0].error, /ECONNREFUSED/);
  } finally {
    tmp.cleanup();
  }
});

test('happy path: buffs -> waiting-buffs -> tagged, readbacks recorded', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 1000, pollMs: 250, attachReadback: true });
    tracker.adopt({ name: 'rat', entity: '100:1', prefab: 'CHAR_Critter_Rat', by: 'test', hostile: false });
    world.entities.set('100:1', { exists: true, prefab: 'CHAR_Critter_Rat' });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, true);
    assert.deepEqual(result.remaining, []);
    assert.deepEqual(result.unconfirmed, []);
    assert.equal(world.entities.get('100:1').exists, false);
    assert.equal(entityCalls(world, 'destroy').length, 1);
    assert.equal(entityCalls(world, 'finalize').length, 2);
    const fin = entityCalls(world, 'finalize')[0].args;
    assert.deepEqual(fin.entities, ['100:1']);
    assert.ok(Array.isArray(fin.buffs['100:1']));
    assert.equal(fin.buffs['100:1'].length, 2);
    assert.equal(result.entities.length, 1);
    const rec = result.entities[0];
    assert.equal(rec.entity, '100:1');
    assert.equal(rec.phase, 'tagged');
    assert.deepEqual(rec.buffs, fin.buffs['100:1']);
    assert.equal(rec.cycles, 0);
    assert.equal(rec.finalizePolls, 2);
    assert.equal(typeof rec.absenceLatencyMs, 'number');
    assert.equal(typeof rec.buffsAt, 'string');
    assert.equal(typeof rec.taggedAt, 'string');
    assert.ok(rec.buffsAt <= rec.taggedAt, `buffsAt ${rec.buffsAt} vs taggedAt ${rec.taggedAt}`);
    assert.equal('detachAt' in rec, false);
    assert.ok(rec.attachedBuffer, 'attachedBuffer missing from cleanup entity');
    assert.ok(rec.attachedBuffer.before, 'before readback missing');
    assert.ok(rec.attachedBuffer.atTagged, 'atTagged readback missing');
    assert.ok(attachedLength(rec.attachedBuffer.before) >= 1);
    assert.equal(attachedLength(rec.attachedBuffer.atTagged), 0);
    const inspects = inspectCalls(world);
    assert.equal(inspects.length, 2);
    assert.deepEqual(inspects[0].args.only, ['AttachedBuffer']);
    assert.equal(inspects[0].args.depth, 16);
    assert.equal(inspects[0].args.entity, '100:1');
    const destroyIdx = world.calls.findIndex((c) => c.op === 'entity' && c.args.action === 'destroy');
    const firstInspectIdx = world.calls.findIndex((c) => c.op === 'inspect');
    assert.ok(firstInspectIdx < destroyIdx, 'AttachedBuffer inspect must run before destroy');
  } finally {
    tmp.cleanup();
  }
});

test('new-buff cycle: finalize buffs accumulates ids then tags', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 1000, pollMs: 250 });
    tracker.adopt({ name: 'rat', entity: '100:1', prefab: 'CHAR_Critter_Rat', by: 'test', hostile: false });
    world.entities.set('100:1', { exists: true, prefab: 'CHAR_Critter_Rat', newBuffCycles: 1 });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, true);
    assert.deepEqual(result.unconfirmed, []);
    const rec = result.entities[0];
    assert.equal(rec.phase, 'tagged');
    assert.equal(rec.cycles, 1);
    assert.ok(rec.buffs.length >= 3, `accumulated buffs ${JSON.stringify(rec.buffs)}`);
    const fin = entityCalls(world, 'finalize');
    assert.ok(fin.length >= 3);
    const listedLast = fin[fin.length - 1].args.buffs['100:1'];
    assert.ok(listedLast.length >= 3, `last finalize listed ${JSON.stringify(listedLast)}`);
  } finally {
    tmp.cleanup();
  }
});

test('stuck: cap 4 new-buff cycles is unconfirmed', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({
      bridge,
      clock,
      timeoutMs: 1000,
      pollMs: 250,
      finalizeTimeoutMs: 8000,
      finalizePollMs: 250,
    });
    tracker.adopt({ name: 'rat', entity: '100:1', prefab: 'CHAR_Critter_Rat', by: 'test', hostile: false });
    world.entities.set('100:1', { exists: true, prefab: 'CHAR_Critter_Rat', newBuffCycles: 5 });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, false);
    assert.equal(result.remaining.length, 0);
    assert.equal(result.unconfirmed.length, 1);
    assert.equal(result.unconfirmed[0].entity, '100:1');
    assert.match(result.unconfirmed[0].error, /buff cycle cap|stuck in stuck/);
    assert.equal(world.entities.get('100:1').exists, true);
    const rec = result.entities[0];
    assert.equal(rec.phase, 'stuck');
    assert.equal(rec.taggedAt, null);
    assert.equal(rec.absenceLatencyMs, null);
    assert.ok(rec.finalizePolls >= 1);
    assert.ok(rec.cycles >= 4);
  } finally {
    tmp.cleanup();
  }
});

test('stuck in waiting-buffs until the bound is unconfirmed', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ keepBuffPresent: true });
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({
      bridge,
      clock,
      timeoutMs: 1000,
      pollMs: 250,
      finalizeTimeoutMs: 500,
      finalizePollMs: 250,
    });
    tracker.adopt({ name: 'rat', entity: '100:1', prefab: 'CHAR_Critter_Rat', by: 'test', hostile: false });
    world.entities.set('100:1', { exists: true, prefab: 'CHAR_Critter_Rat' });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, false);
    assert.equal(result.remaining.length, 0);
    assert.equal(result.unconfirmed.length, 1);
    assert.equal(result.unconfirmed[0].entity, '100:1');
    assert.match(result.unconfirmed[0].error, /stuck in waiting-buffs:/);
    assert.ok(entityCalls(world, 'finalize').length >= 1);
    assert.equal(world.entities.get('100:1').exists, true);
    const rec = result.entities[0];
    assert.equal(rec.phase, 'waiting-buffs');
    assert.equal(rec.absenceLatencyMs, null);
    assert.equal(typeof rec.buffsAt, 'string');
    assert.equal(rec.taggedAt, null);
    assert.ok(rec.finalizePolls >= 1);
  } finally {
    tmp.cleanup();
  }
});

test('incomplete sweep is unconfirmed and never calls finalize', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ sweepIncomplete: true, sweepIncompleteReason: 'BuffBuffer length above cap' });
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 1000, pollMs: 250 });
    tracker.adopt({ name: 'rat', entity: '100:1', prefab: 'CHAR_Critter_Rat', by: 'test', hostile: false });
    world.entities.set('100:1', { exists: true, prefab: 'CHAR_Critter_Rat', sweepIncomplete: true, sweepReason: 'BuffBuffer length above cap' });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, false);
    assert.equal(result.remaining.length, 0);
    assert.equal(result.unconfirmed.length, 1);
    assert.equal(result.unconfirmed[0].entity, '100:1');
    assert.match(result.unconfirmed[0].error, /BuffBuffer length above cap/);
    assert.equal(entityCalls(world, 'destroy').length, 1);
    assert.equal(entityCalls(world, 'finalize').length, 0);
    assert.equal(world.entities.get('100:1').exists, true);
    const rec = result.entities[0];
    assert.equal(rec.phase, 'buffs');
    assert.equal(rec.buffSweepComplete, false);
    assert.equal(rec.finalizePolls, 0);
    assert.equal(rec.absenceLatencyMs, null);
  } finally {
    tmp.cleanup();
  }
});

test('older contract (no phase) is unconfirmed with a mismatch finding', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ omitDestroyPhase: true });
  const findings = [];
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 1000, pollMs: 250, findings });
    tracker.adopt({ name: 'rat', entity: '100:1', prefab: 'CHAR_Critter_Rat', by: 'test', hostile: false });
    world.entities.set('100:1', { exists: true, prefab: 'CHAR_Critter_Rat' });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, false);
    assert.equal(result.remaining.length, 0);
    assert.equal(result.unconfirmed.length, 1);
    assert.equal(world.entities.get('100:1').exists, true);
    assert.equal(entityCalls(world, 'destroy').length, 1);
    assert.equal(entityCalls(world, 'finalize').length, 0);
    const finding = contractMismatchFinding('no phase');
    assert.ok(findings.includes(finding), `findings ${JSON.stringify(findings)}`);
    assert.equal(result.entities[0].error, finding);
    assert.equal(result.entities[0].finalizePolls, 0);
    assert.equal(result.entities[0].absenceLatencyMs, null);
  } finally {
    tmp.cleanup();
  }
});

test('older contract (phase detach) is unconfirmed with a mismatch finding', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ oldDetachPhase: true });
  const findings = [];
  try {
    const bridge = testBridge(tmp.dir, world);
    const clock = fakeClock();
    const tracker = createTracker({ bridge, clock, timeoutMs: 1000, pollMs: 250, findings });
    tracker.adopt({ name: 'rat', entity: '100:1', prefab: 'CHAR_Critter_Rat', by: 'test', hostile: false });
    world.entities.set('100:1', { exists: true, prefab: 'CHAR_Critter_Rat' });
    const result = await tracker.cleanupAll();
    assert.equal(result.ok, false);
    assert.equal(result.remaining.length, 0);
    assert.equal(result.unconfirmed.length, 1);
    assert.equal(world.entities.get('100:1').exists, true);
    assert.equal(entityCalls(world, 'finalize').length, 0);
    const finding = contractMismatchFinding('detach');
    assert.ok(findings.includes(finding), `findings ${JSON.stringify(findings)}`);
    assert.match(result.unconfirmed[0].error, /entity op contract mismatch: detach/);
  } finally {
    tmp.cleanup();
  }
});
