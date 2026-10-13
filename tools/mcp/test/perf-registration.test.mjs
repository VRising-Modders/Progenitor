import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import { z } from 'zod';

test('registered performance live action reaches socket status; capture rejects it', async () => {
  const source = fs.readFileSync(new URL('../server.mjs', import.meta.url), 'utf8');
  const registrations = new Map();
  const calls = [];
  // Execute the actual registration blocks with a transport double, no game or token files.
  vm.runInNewContext(source.slice(source.indexOf('server.tool("progenitor_capture"'),
    source.indexOf('server.tool("progenitor_ghost"')), {
    z, server: { tool: (name, description, schema, handler) =>
      registrations.set(name, { schema: z.object(schema), handler }) },
    bridge: async (...args) => { calls.push(args); return { running: true }; },
    text: (value) => value, fail: (error) => { throw error; },
  });
  const perf = registrations.get('progenitor_perf');
  const capture = registrations.get('progenitor_capture');
  assert.equal(perf.schema.safeParse({ side: 'client', action: 'live' }).success, true);
  assert.equal(perf.schema.safeParse({ side: 'client', action: 'status' }).success, true);
  assert.equal(capture.schema.safeParse({ action: 'live' }).success, false);
  assert.equal(capture.schema.safeParse({ action: 'status' }).success, true);
  await perf.handler(perf.schema.parse({ side: 'client', action: 'live' }));
  assert.equal(calls[0][0], 'client');
  assert.equal(calls[0][1], 'perf_status');
  await perf.handler(perf.schema.parse({ side: 'server', action: 'start', seconds: 120 }));
  assert.equal(calls[1][1], 'perf');
  assert.equal(calls[1][2].seconds, 120);
  await perf.handler(perf.schema.parse({ side: 'client', action: 'status' }));
  assert.equal(calls[2][1], 'perf');
  assert.equal(calls[2][2].action, 'status');
});
