import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { z } from 'zod';
import { isReadOnlyOp } from '../queue.mjs';

// The handler's rest-spread object comes from the vm context (another Object prototype): compare plain copies.
const plain = (value) => JSON.parse(JSON.stringify(value));

// Runs the real progenitor_container_preview registration with a transport double: no game, no token files.
function load() {
  const source = fs.readFileSync(new URL('../server.mjs', import.meta.url), 'utf8');
  const start = source.indexOf('server.tool("progenitor_container_preview"');
  const end = source.indexOf('// BRIDGEUI1', start);
  assert.ok(start > 0 && end > start, 'progenitor_container_preview is registered before the BRIDGEUI1 tools');
  const registrations = new Map();
  const calls = [];
  vm.runInNewContext(source.slice(start, end), {
    z,
    server: { tool: (name, description, schema, handler) => registrations.set(name, { description, schema: z.object(schema), handler }) },
    bridge: async (...args) => { calls.push(args); return { ok: true }; },
    text: (value) => value,
    fail: (error) => { throw error; },
  });
  return { tool: registrations.get('progenitor_container_preview'), calls };
}

test('container preview tool sends open, status, close and bag to the container_preview op', async () => {
  const { tool, calls } = load();
  assert.ok(tool);
  await tool.handler(tool.schema.parse({}));
  assert.deepEqual(plain(calls[0]), ['client', 'container_preview', {}]);
  const id = 'c-' + '0123456789abcdef'.repeat(2);
  await tool.handler(tool.schema.parse({ action: 'open', id, keepOpen: true, hideBag: false, replicationTimeout: 8, dropEvents: false, hudGate: false, syncInventory: false }));
  assert.deepEqual(plain(calls[1]), ['client', 'container_preview', { action: 'open', id, keepOpen: true, hideBag: false, replicationTimeout: 8, dropEvents: false, hudGate: false, syncInventory: false }]);
  await tool.handler(tool.schema.parse({ side: 'server' }));
  assert.deepEqual(plain(calls[2]), ['server', 'container_preview', {}]);
  await tool.handler(tool.schema.parse({ action: 'close' }));
  assert.deepEqual(plain(calls[3]), ['client', 'container_preview', { action: 'close' }]);
  await tool.handler(tool.schema.parse({ action: 'bag', mode: 'alpha' }));
  assert.deepEqual(plain(calls[4]), ['client', 'container_preview', { action: 'bag', mode: 'alpha' }]);
});

test('container preview tool schema rejects unknown actions and out-of-range lengths', () => {
  const { tool } = load();
  assert.equal(tool.schema.safeParse({ action: 'move' }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'open', id: 'c-x', seconds: 0 }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'open', id: 'c-x', seconds: 601 }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'open', id: 'c-x', replicationTimeout: 31 }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'bag', mode: 'hidden' }).success, false);
});

test('container preview guard goes to the server bridge and takes the queue slot', async () => {
  const { tool, calls } = load();
  await tool.handler(tool.schema.parse({ side: 'server', action: 'guard', on: false }));
  assert.deepEqual(plain(calls[0]), ['server', 'container_preview', { action: 'guard', on: false }]);
  assert.equal(isReadOnlyOp('container_preview', { action: 'guard', on: false }), false);
});

test('container preview status reads without the queue slot; open, close and bag take it', () => {
  assert.equal(isReadOnlyOp('container_preview', {}), true);
  assert.equal(isReadOnlyOp('container_preview', { action: 'status' }), true);
  assert.equal(isReadOnlyOp('container_preview', { action: 'open', id: 'c-x' }), false);
  assert.equal(isReadOnlyOp('container_preview', { action: 'close' }), false);
  assert.equal(isReadOnlyOp('container_preview', { action: 'bag', mode: 'alpha' }), false);
});
