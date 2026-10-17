import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { z } from 'zod';
import { isReadOnlyOp } from '../queue.mjs';

// Runs the real progenitor_locate registration with a transport double: no game, no token files.
function load() {
  const source = fs.readFileSync(new URL('../server.mjs', import.meta.url), 'utf8');
  const start = source.indexOf('server.tool("progenitor_locate"');
  const end = source.indexOf('// BRIDGEUI1', start);
  assert.ok(start > 0 && end > start, 'progenitor_locate is registered before the BRIDGEUI1 tools');
  const registrations = new Map();
  const calls = [];
  vm.runInNewContext(source.slice(start, end), {
    z,
    server: { tool: (name, description, schema, handler) => registrations.set(name, { description, schema: z.object(schema), handler }) },
    bridge: async (...args) => { calls.push(args); return { ok: true }; },
    text: (value) => value,
    fail: (error) => { throw error; },
  });
  return { tool: registrations.get('progenitor_locate'), calls };
}

test('locate tool sends pin, status and clear to the client bridge op', async () => {
  const { tool, calls } = load();
  assert.ok(tool);
  await tool.handler(tool.schema.parse({}));
  assert.deepEqual(calls[0], ['client', 'locate', {}]);
  await tool.handler(tool.schema.parse({ action: 'pin', x: -1444, y: 5.02, z: -740, label: 'Minerals', seconds: 60, arrival: 3, maps: true }));
  assert.deepEqual(calls[1], ['client', 'locate', { action: 'pin', x: -1444, y: 5.02, z: -740, label: 'Minerals', seconds: 60, arrival: 3, maps: true }]);
  await tool.handler(tool.schema.parse({ action: 'clear' }));
  assert.deepEqual(calls[2], ['client', 'locate', { action: 'clear' }]);
});

test('locate tool schema rejects unknown actions and negative distances', () => {
  const { tool } = load();
  assert.equal(tool.schema.safeParse({ action: 'move' }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'pin', x: 1, y: 2, z: 3, arrival: -1 }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'pin', x: 1, y: 2, z: 3, seconds: -5 }).success, false);
});

test('locate status reads without the queue slot; pin and clear take it', () => {
  assert.equal(isReadOnlyOp('locate', {}), true);
  assert.equal(isReadOnlyOp('locate', { action: 'status' }), true);
  assert.equal(isReadOnlyOp('locate', { action: 'pin', x: 0, y: 0, z: 0 }), false);
  assert.equal(isReadOnlyOp('locate', { action: 'clear' }), false);
});
