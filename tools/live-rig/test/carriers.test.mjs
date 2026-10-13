import test from 'node:test';
import assert from 'node:assert/strict';
import { runCase, cleanupLeftovers, leftoverEntitiesFromReport } from '../lib/run.mjs';
import { CARRIER_QUERY_LIMIT } from '../lib/checks.mjs';
import { createFakeWorld, tempDir, runOpts } from './helpers.mjs';

function isCarrierQuery(args) {
  return Array.isArray(args?.all) && args.all.includes('SpawnSequenceForEntity');
}

function destroyedIds(world, side) {
  return world.calls
    .filter((c) => c.op === 'entity' && c.args?.action === 'destroy' && (!side || c.side === side))
    .flatMap((c) => c.args.entities ?? []);
}

/** Inject a carrier after the start-state client+server queries (the 3rd carrier query). */
function addCarrierAfterBaseline(world, side, spec) {
  const orig = world.request.bind(world);
  let n = 0;
  world.request = async (s, op, args) => {
    if (op === 'query' && isCarrierQuery(args)) {
      n++;
      if (n === 3) world.addCarrier(side, spec);
    }
    return orig(s, op, args);
  };
}

test('a client delta NetworkedSequence is destroyed, recorded as a finding, and the run is complete', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  addCarrierAfterBaseline(world, 'client', { entity: '99448:20', prefab: 'NetworkedSequence' });
  try {
    const { report } = await runCase({ name: 'client-carrier-delta', steps: [] }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.ok(report.findings.includes('client carrier outlived server disposal: 99448:20'));
    assert.deepEqual(report.cleanup.clientCarriers.destroyed, ['99448:20']);
    assert.deepEqual(report.cleanup.clientCarriers.remaining, []);
    assert.equal(world.entities.get('99448:20').exists, false);
    assert.ok(destroyedIds(world, 'client').includes('99448:20'));
    assert.ok(!destroyedIds(world, 'server').includes('99448:20'));
    const carriersCheck = report.finalChecks.find((c) => c.name === 'carriers');
    assert.equal(carriersCheck.ok, true);
    assert.equal(report.leftoverSummary.carriersLeftover, false);
  } finally {
    tmp.cleanup();
  }
});

test('a server delta carrier that persists makes the run incomplete and is not destroyed', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  addCarrierAfterBaseline(world, 'server', { entity: '567936:14', prefab: 'NetworkedSequence' });
  try {
    const { report } = await runCase({ name: 'server-carrier-delta', steps: [] }, runOpts(tmp, world));
    assert.equal(report.status, 'incomplete');
    assert.match(String(report.reason), /carriers/);
    assert.equal(world.entities.get('567936:14').exists, true);
    assert.ok(!destroyedIds(world).includes('567936:14'));
    assert.deepEqual(report.cleanup.clientCarriers.destroyed, []);
    const carriersCheck = report.finalChecks.find((c) => c.name === 'carriers');
    assert.equal(carriersCheck.ok, false);
    assert.equal(carriersCheck.value.leftover.server[0].entity, '567936:14');
    assert.equal(report.leftoverSummary.carriersLeftover, true);
  } finally {
    tmp.cleanup();
  }
});

test('a baseline carrier is never destroyed', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    carrierEntities: {
      client: [{ entity: '10:1', prefab: 'NetworkedSequence' }],
      server: [],
    },
  });
  addCarrierAfterBaseline(world, 'client', { entity: '99448:20', prefab: 'NetworkedSequence' });
  try {
    const { report } = await runCase({ name: 'baseline-carrier', steps: [] }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.deepEqual(report.startState.carriers.client.ids, ['10:1']);
    assert.ok(report.findings.includes('client carrier outlived server disposal: 99448:20'));
    assert.deepEqual(report.cleanup.clientCarriers.destroyed, ['99448:20']);
    assert.ok(!destroyedIds(world).includes('10:1'));
    assert.ok(destroyedIds(world, 'client').includes('99448:20'));
    assert.equal(world.entities.get('10:1').exists, true);
    assert.equal(world.entities.get('99448:20').exists, false);
  } finally {
    tmp.cleanup();
  }
});

test('a non-NetworkedSequence client delta is reported and not destroyed', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  addCarrierAfterBaseline(world, 'client', { entity: '77:1', prefab: 'NotASequence' });
  try {
    const { report } = await runCase({ name: 'non-sequence-delta', steps: [] }, runOpts(tmp, world));
    assert.equal(report.status, 'incomplete');
    assert.ok(!report.findings.some((f) => /client carrier outlived/.test(f)));
    assert.deepEqual(report.cleanup.clientCarriers.destroyed, []);
    assert.ok(report.cleanup.clientCarriers.remaining.includes('77:1'));
    assert.ok(!destroyedIds(world).includes('77:1'));
    assert.equal(world.entities.get('77:1').exists, true);
    const carriersCheck = report.finalChecks.find((c) => c.name === 'carriers');
    assert.equal(carriersCheck.ok, false);
    assert.equal(carriersCheck.value.leftover.client[0].entity, '77:1');
  } finally {
    tmp.cleanup();
  }
});

test('cleanupLeftovers of a non-NetworkedSequence client delta is not complete while the entity exists', async () => {
  const tmp1 = tempDir();
  const tmp2 = tempDir();
  const world = createFakeWorld();
  addCarrierAfterBaseline(world, 'client', { entity: '77:1', prefab: 'NotASequence' });
  try {
    const first = await runCase({ name: 'non-sequence-delta', steps: [] }, runOpts(tmp1, world));
    assert.equal(first.report.status, 'incomplete');
    assert.ok(first.report.cleanup.clientCarriers.remaining.includes('77:1'));
    assert.ok(leftoverEntitiesFromReport(first.report).some((e) => e.entity === '77:1' && e.side === 'client'));

    const cleaned = await cleanupLeftovers(first.runDir, runOpts(tmp2, world));
    assert.notEqual(cleaned.report.status, 'complete');
    assert.notEqual(cleaned.report.reason, 'nothing to clean');
    assert.equal(world.entities.get('77:1').exists, true);
    assert.ok(!destroyedIds(world).includes('77:1'));
    assert.ok((cleaned.report.cleanup.remaining ?? []).some((e) => e.entity === '77:1'));
  } finally {
    tmp1.cleanup();
    tmp2.cleanup();
  }
});

test('cleanupLeftovers adopts a client NetworkedSequence whose first destroy threw', async () => {
  const tmp1 = tempDir();
  const tmp2 = tempDir();
  const world = createFakeWorld();
  addCarrierAfterBaseline(world, 'client', { entity: '99448:20', prefab: 'NetworkedSequence' });
  world.throwOn('entity', 'client destroy failed', { side: 'client', action: 'destroy' });
  try {
    const first = await runCase({ name: 'ns-destroy-throw', steps: [] }, runOpts(tmp1, world));
    assert.equal(first.report.status, 'incomplete');
    assert.ok(first.report.cleanup.clientCarriers.remaining.includes('99448:20'));
    assert.equal(world.entities.get('99448:20').exists, true);

    const cleaned = await cleanupLeftovers(first.runDir, runOpts(tmp2, world));
    assert.equal(cleaned.report.status, 'complete', cleaned.report.reason);
    assert.equal(world.entities.get('99448:20').exists, false);
    assert.ok(cleaned.report.adopted.some((e) => e.entity === '99448:20' && e.side === 'client'));
    assert.ok(destroyedIds(world, 'client').includes('99448:20'));
  } finally {
    tmp1.cleanup();
    tmp2.cleanup();
  }
});

test('201 client NetworkedSequence at start is not complete and the reason names the cap', async () => {
  const tmp = tempDir();
  const client = [];
  for (let i = 0; i < 200; i++) client.push({ entity: `${1000 + i}:1`, prefab: 'NetworkedSequence' });
  client.push({ entity: '9999:1', prefab: 'NetworkedSequence' });
  const world = createFakeWorld({ carrierEntities: { client, server: [] } });
  try {
    const { report } = await runCase({ name: 'carrier-cap', steps: [] }, runOpts(tmp, world));
    assert.notEqual(report.status, 'complete');
    assert.match(String(report.reason), /cap/);
    assert.match(String(report.reason), new RegExp(String(CARRIER_QUERY_LIMIT)));
    const q = world.calls.find((c) => c.op === 'query' && Array.isArray(c.args?.all) && c.args.all.includes('SpawnSequenceForEntity'));
    assert.equal(q.args.total, true);
    assert.equal(q.args.limit, CARRIER_QUERY_LIMIT);
    assert.equal(report.startState.carriers.client.ids.includes('9999:1'), false);
    assert.equal(report.startState.carriers.client.ids.length, CARRIER_QUERY_LIMIT);
  } finally {
    tmp.cleanup();
  }
});
