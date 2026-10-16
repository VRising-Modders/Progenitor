import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { z } from 'zod';

// Runs the real progenitor_servants registration with a transport double: no game, no token files.
function load() {
  const source = fs.readFileSync(new URL('../server.mjs', import.meta.url), 'utf8');
  const start = source.indexOf('server.tool("progenitor_servants"');
  const end = source.indexOf('server.tool("progenitor_photo"');
  assert.ok(start > 0 && end > start, 'progenitor_servants is registered before progenitor_photo');
  const registrations = new Map();
  const calls = [];
  vm.runInNewContext(source.slice(start, end), {
    z,
    server: { tool: (name, description, schema, handler) => registrations.set(name, { description, schema: z.object(schema), handler }) },
    bridge: async (...args) => { calls.push(args); return { ok: true }; },
    text: (value) => value,
    fail: (error) => { throw error; },
  });
  return { tool: registrations.get('progenitor_servants'), calls };
}

test('servants tool sends every action to the server bridge op', async () => {
  const { tool, calls } = load();
  assert.ok(tool);
  assert.match(tool.description, /Built, not run live/);
  await tool.handler(tool.schema.parse({ territory: 4 }));
  assert.deepEqual(calls[0], ['server', 'servants', { territory: 4 }]);
  await tool.handler(tool.schema.parse({ action: 'send', territory: 4, servants: ['Alda', '#2'], zone: '1,-2#3', slot: 1 }));
  assert.equal(calls[1][0], 'server');
  assert.equal(calls[1][1], 'servants');
  assert.deepEqual(calls[1][2].servants, ['Alda', '#2']);
  await tool.handler(tool.schema.parse({ action: 'finish', heart: '12:1', method: 'row' }));
  assert.equal(calls[2][2].method, 'row');
  await tool.handler(tool.schema.parse({ action: 'timers', off: false }));
  assert.equal(calls[3][2].off, false);
  await tool.handler(tool.schema.parse({ action: 'empty', territory: 4, to: '88:2', discard: true }));
  assert.deepEqual(calls[4], ['server', 'servants', { action: 'empty', territory: 4, to: '88:2', discard: true }]);
});

test('servants tool schema rejects unknown actions and methods', () => {
  const { tool } = load();
  assert.equal(tool.schema.safeParse({ action: 'hunt' }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'finish', method: 'now' }).success, false);
  assert.equal(tool.schema.safeParse({ action: 'send', servants: 'Alda,Bren' }).success, true);
  assert.equal(tool.schema.safeParse({ territory: 1.5 }).success, false);
});
