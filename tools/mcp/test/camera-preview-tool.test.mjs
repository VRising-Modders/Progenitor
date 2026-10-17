import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { z } from 'zod';
import { isReadOnlyOp } from '../queue.mjs';

// Runs the real progenitor_camera_preview registration with a transport double: no game, no token files.
function load() {
  const source = fs.readFileSync(new URL('../server.mjs', import.meta.url), 'utf8');
  const start = source.indexOf('server.tool("progenitor_camera_preview"');
  const end = source.indexOf('server.tool("progenitor_locate"', start);
  assert.ok(start > 0 && end > start, 'progenitor_camera_preview is registered just before progenitor_locate');
  const registrations = new Map();
  const calls = [];
  vm.runInNewContext(source.slice(start, end), {
    z,
    server: { tool: (name, description, schema, handler) => registrations.set(name, { description, schema: z.object(schema), handler }) },
    bridge: async (...args) => { calls.push(args); return { active: false }; },
    text: (value) => value,
    fail: (error) => { throw error; },
  });
  return { tool: registrations.get('progenitor_camera_preview'), calls };
}

test('camera preview tool reads the client bridge op with no arguments', async () => {
  const { tool, calls } = load();
  assert.ok(tool);
  assert.deepEqual(await tool.handler(tool.schema.parse({})), { active: false });
  assert.equal(JSON.stringify(calls[0]), JSON.stringify(['client', 'camera_preview', {}])); // the {} is made in the tool's own context
  assert.match(tool.description, /screenX/);
});

test('camera preview readback needs no queue slot', () => {
  assert.equal(isReadOnlyOp('camera_preview', {}), true);
});
