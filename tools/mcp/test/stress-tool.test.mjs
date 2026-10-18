import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { z } from 'zod';

// Runs the real progenitor_stress registration with a transport double: no game, no token files.
function load() {
  const source = fs.readFileSync(new URL('../server.mjs', import.meta.url), 'utf8');
  const start = source.indexOf('server.tool("progenitor_stress"');
  const end = source.indexOf('// BRIDGEUI1', start);
  assert.ok(start > 0 && end > start, 'progenitor_stress is registered before the BRIDGEUI1 block');
  const registrations = new Map();
  const calls = [];
  vm.runInNewContext(source.slice(start, end), {
    z,
    server: { tool: (name, description, schema, handler) => registrations.set(name, { description, schema: z.object(schema), handler }) },
    // Through JSON, as the wire does: the handler builds its args object inside the vm realm.
    bridge: async (...args) => { calls.push(JSON.parse(JSON.stringify(args))); return { ok: true }; },
    text: (value) => value,
    fail: (error) => { throw error; },
  });
  return { tool: registrations.get('progenitor_stress'), calls };
}

test('stress tool maps each action to its server op and passes the arguments through', async () => {
  const { tool, calls } = load();
  assert.ok(tool);
  assert.match(tool.description, /not run live/);
  assert.match(tool.description, /C:\\VRisingServer/);
  await tool.handler(tool.schema.parse({}));
  assert.deepEqual(calls[0], ['server', 'stress_status', {}]);
  await tool.handler(tool.schema.parse({ action: 'populate', seed: 7, confirm: 'stress', plots: 'free', chests: [10, 25], stations: [6, 10], fill: 0.5, partial: true }));
  assert.deepEqual(calls[1], ['server', 'stress_populate', { seed: 7, confirm: 'stress', plots: 'free', chests: [10, 25], stations: [6, 10], fill: 0.5, partial: true }]);
  await tool.handler(tool.schema.parse({ action: 'populate', seed: 7, plots: [4, 9], dryRun: true }));
  assert.deepEqual(calls[2][2].plots, [4, 9]);
  await tool.handler(tool.schema.parse({ action: 'clear', seed: 7, confirm: 'stress' }));
  assert.deepEqual(calls[3], ['server', 'stress_clear', { seed: 7, confirm: 'stress' }]);
  await tool.handler(tool.schema.parse({ action: 'status', plot: 12, rows: false }));
  assert.deepEqual(calls[4], ['server', 'stress_status', { plot: 12, rows: false }]);
  await tool.handler(tool.schema.parse({ action: 'team', seed: 7, heart: '1234:5' }));
  assert.deepEqual(calls[5], ['server', 'stress_team', { seed: 7, heart: '1234:5' }]);
  assert.match(tool.description, /never claims/);
});

test('stress tool maps the staged actions to their server ops', async () => {
  const { tool, calls } = load();
  await tool.handler(tool.schema.parse({ action: 'groups', seed: 18, groups: [8, 6, 4, 2] }));
  assert.deepEqual(calls[0], ['server', 'stress_owner_groups', { seed: 18, groups: [8, 6, 4, 2] }]);
  await tool.handler(tool.schema.parse({ action: 'clan', seed: 18, clan: 1, experiment: true, confirm: 'stress' }));
  assert.deepEqual(calls[1], ['server', 'stress_clan', { seed: 18, clan: 1, experiment: true, confirm: 'stress' }]);
  await tool.handler(tool.schema.parse({ action: 'refine', seed: 18, plots: 'stress', hours: 1, supply: 'spread', emptyChests: 3, dryRun: true }));
  assert.deepEqual(calls[2], ['server', 'stress_refine', { seed: 18, plots: 'stress', hours: 1, supply: 'spread', emptyChests: 3, dryRun: true }]);
  await tool.handler(tool.schema.parse({ action: 'stations', seed: 18, plots: [4], perCastle: 20, dryRun: true }));
  assert.deepEqual(calls[3], ['server', 'stress_stations', { seed: 18, plots: [4], perCastle: 20, dryRun: true }]);
  await tool.handler(tool.schema.parse({ action: 'stage' }));
  assert.deepEqual(calls[4], ['server', 'stress_stage_status', {}]);
  assert.match(tool.description, /writes refused/);
  assert.equal(tool.schema.safeParse({ action: 'refine', supply: 'far' }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'groups', groups: [1] }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'stations', perCastle: 40 }).success, false);
});

test('stress tool schema rejects bad values', () => {
  const { tool } = load();
  assert.equal(tool.schema.safeParse({ action: 'fill' }).success, false);
  assert.equal(tool.schema.safeParse({ seed: 10000 }).success, false);
  assert.equal(tool.schema.safeParse({ seed: 1.5 }).success, false);
  assert.equal(tool.schema.safeParse({ plots: 'all' }).success, false);
  assert.equal(tool.schema.safeParse({ chests: [10] }).success, false);
  assert.equal(tool.schema.safeParse({ fill: 2 }).success, false);
  assert.equal(tool.schema.safeParse({ budgetMs: 50 }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'team', seed: 7, heart: 'heart' }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'populate', seed: 0, plots: [0, 3] }).success, true);
});
