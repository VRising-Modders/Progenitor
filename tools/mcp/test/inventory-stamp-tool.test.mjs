import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { z } from 'zod';

// Runs the real progenitor_inventory_stamp registration with a transport double: no game, no token files.
function load() {
  const source = fs.readFileSync(new URL('../server.mjs', import.meta.url), 'utf8');
  const start = source.indexOf('server.tool("progenitor_inventory_stamp"');
  const end = source.indexOf('server.tool("progenitor_photo"');
  assert.ok(start > 0 && end > start, 'progenitor_inventory_stamp is registered before progenitor_photo');
  const registrations = new Map();
  const calls = [];
  vm.runInNewContext(source.slice(start, end), {
    z,
    server: { tool: (name, description, schema, handler) => registrations.set(name, { description, schema: z.object(schema), handler }) },
    bridge: async (...args) => { calls.push(args); return { ok: true }; },
    text: (value) => value,
    fail: (error) => { throw error; },
  });
  return { tool: registrations.get('progenitor_inventory_stamp'), calls };
}

test('inventory stamp tool sends the containers and since to the server bridge op', async () => {
  const { tool, calls } = load();
  assert.ok(tool);
  assert.match(tool.description, /not live-verified/);
  await tool.handler(tool.schema.parse({ containers: ['123:1', '456:2#1'] }));
  assert.deepEqual(calls[0], ['server', 'inventory_stamp', { containers: ['123:1', '456:2#1'] }]);
  await tool.handler(tool.schema.parse({ containers: '123:1,789:3/out', since: 4242 }));
  assert.deepEqual(calls[1], ['server', 'inventory_stamp', { containers: '123:1,789:3/out', since: 4242 }]);
});

test('inventory stamp tool schema needs containers and a whole-number since', () => {
  const { tool } = load();
  assert.equal(tool.schema.safeParse({}).success, false);
  assert.equal(tool.schema.safeParse({ containers: ['1:1'], since: 1.5 }).success, false);
  assert.equal(tool.schema.safeParse({ containers: ['1:1'], since: 10 }).success, true);
});
