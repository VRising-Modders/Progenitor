import test from 'node:test';
import assert from 'node:assert/strict';
import { z } from 'zod';
import { registerUiTools, compact, uiInputTimeout } from '../ui-tools.mjs';
import { isReadOnlyOp } from '../queue.mjs';

function register() {
  const tools = new Map();
  const calls = [];
  registerUiTools(
    { tool: (name, description, schema, handler) => tools.set(name, { description, schema: z.object(schema), handler }) },
    {
      z,
      bridge: async (...args) => { calls.push(args); return { ok: true }; },
      text: (value) => value,
      fail: (error) => { throw error; },
    });
  return { tools, calls };
}

test('the UI tools are registered with descriptions', () => {
  const { tools } = register();
  for (const name of ['progenitor_ui_input', 'progenitor_ui_read', 'progenitor_nav', 'progenitor_display', 'progenitor_screenshot']) {
    assert.ok(tools.has(name), name);
    assert.ok(tools.get(name).description.length > 80, name);
  }
});

test('ui_input maps hover to move, drops unset fields and sends a target', async () => {
  const { tools, calls } = register();
  const t = tools.get('progenitor_ui_input');
  const args = t.schema.parse({ action: 'hover', row: 'demo-table:header', cell: 'c:reserve' });
  await t.handler(args);
  assert.equal(calls[0][0], 'client');
  assert.equal(calls[0][1], 'ui_input');
  assert.deepEqual(calls[0][2], { action: 'move', row: 'demo-table:header', cell: 'c:reserve' });
});

test('ui_input click takes shift and ctrl, refuses alt, and a drag target is an object', async () => {
  const { tools, calls } = register();
  const t = tools.get('progenitor_ui_input');
  assert.equal(t.schema.safeParse({ action: 'click', row: 'r', modifiers: ['shift', 'ctrl'] }).success, true);
  assert.equal(t.schema.safeParse({ action: 'click', row: 'r', modifiers: ['alt'] }).success, false);
  assert.equal(t.schema.safeParse({ action: 'wheel', row: 'r', notches: 21 }).success, false);
  await t.handler(t.schema.parse({ action: 'drag', row: 'demo-list:row0', cell: 'grip', to: { row: 'demo-list:row3', fy: 0.1 }, steps: 30 }));
  assert.deepEqual(calls[0][2].to, { row: 'demo-list:row3', fy: 0.1 });
  assert.ok(calls[0][3] >= 25000);
});

test('ui_input timeouts grow with long drags and typing, within bounds', () => {
  assert.equal(uiInputTimeout({}), 25000);
  assert.ok(uiInputTimeout({ steps: 600 }) > 25000);
  assert.equal(uiInputTimeout({ text: 'x'.repeat(2000), steps: 600 }), 120000);
});

test('ui_read, nav, display and screenshot reach their ops', async () => {
  const { tools, calls } = register();
  await tools.get('progenitor_ui_read').handler(tools.get('progenitor_ui_read').schema.parse({ what: 'widget', key: 'demo-table' }));
  await tools.get('progenitor_nav').handler(tools.get('progenitor_nav').schema.parse({ action: 'stick', y: -1, ms: 400 }));
  await tools.get('progenitor_display').handler(tools.get('progenitor_display').schema.parse({ action: 'set', width: 2560, height: 1440 }));
  await tools.get('progenitor_screenshot').handler(tools.get('progenitor_screenshot').schema.parse({ superSize: 2, panel: 'Tips' }));
  assert.deepEqual(calls.map((c) => c[1]), ['ui_read', 'nav', 'display', 'screenshot']);
  assert.deepEqual(calls[0][2], { what: 'widget', key: 'demo-table' });
  assert.equal(tools.get('progenitor_nav').schema.safeParse({ action: 'stick', y: 2 }).success, false);
  assert.equal(tools.get('progenitor_display').schema.safeParse({ action: 'set', width: 100, height: 100 }).success, false);
});

test('compact drops undefined and null', () => {
  assert.deepEqual(compact({ a: 1, b: undefined, c: null, d: 0 }), { a: 1, d: 0 });
});

test('the queue lets readback and screenshots through and gates input', () => {
  assert.ok(isReadOnlyOp('ui_read', { what: 'state' }));
  assert.ok(isReadOnlyOp('screenshot', {}));
  assert.ok(isReadOnlyOp('ui_input', { action: 'status' }));
  assert.ok(isReadOnlyOp('display', { action: 'status' }));
  assert.ok(!isReadOnlyOp('ui_input', { action: 'click' }));
  assert.ok(!isReadOnlyOp('display', { action: 'set' }));
});
