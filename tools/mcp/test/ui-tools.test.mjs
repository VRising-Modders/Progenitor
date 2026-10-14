import test from 'node:test';
import assert from 'node:assert/strict';
import { z } from 'zod';
import { registerUiTools, compact, consoleTimeoutMs, UI_INPUT_TIMEOUT_MS } from '../ui-tools.mjs';
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
  for (const name of ['progenitor_ui_input', 'progenitor_ui_read', 'progenitor_nav', 'progenitor_display', 'progenitor_screenshot', 'progenitor_console']) {
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
  assert.equal(calls[0][3], UI_INPUT_TIMEOUT_MS);
});

test('ui_input waits no longer than the bridge cap and bounds its frames and text', () => {
  const t = register().tools.get('progenitor_ui_input');
  assert.equal(UI_INPUT_TIMEOUT_MS, 30000);
  assert.equal(t.schema.safeParse({ action: 'drag', x: 1, y: 1, toX: 2, toY: 2, steps: 181 }).success, false);
  assert.equal(t.schema.safeParse({ action: 'click', x: 1, y: 1, holdFrames: 121 }).success, false);
  assert.equal(t.schema.safeParse({ action: 'type', text: 'x'.repeat(401) }).success, false);
  assert.equal(t.schema.safeParse({ action: 'key', key: 'Escape', frames: 31 }).success, false);
});

test('display offers no exclusive mode and takes allowInWorld; screenshot takes only a plain file name', () => {
  const { tools } = register();
  const d = tools.get('progenitor_display').schema;
  assert.equal(d.safeParse({ action: 'set', width: 1920, height: 1080, mode: 'exclusive' }).success, false);
  assert.equal(d.safeParse({ action: 'set', width: 1920, height: 1080, allowInWorld: true }).success, true);
  const s = tools.get('progenitor_screenshot').schema;
  assert.equal(s.safeParse({ name: 'table-1080.png' }).success, true);
  for (const bad of ['../x.png', 'C:/x.png', 'a/b.png', 'x.jpg', '.png'])
    assert.equal(s.safeParse({ name: bad }).success, false, bad);
  assert.equal(s.safeParse({ path: 'C:/Windows/x.png' }).data?.path, undefined);
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

test('ui_input takes submit with a target', async () => {
  const { tools, calls } = register();
  const t = tools.get('progenitor_ui_input');
  await t.handler(t.schema.parse({ action: 'submit', x: 640, y: 360 }));
  assert.deepEqual(calls[0][2], { action: 'submit', x: 640, y: 360 });
});

test('console sends one command line to the client and waits for its output', async () => {
  const { tools, calls } = register();
  const t = tools.get('progenitor_console');
  assert.ok(t.description.includes('TryCommand'));
  await t.handler(t.schema.parse({ text: 'adminauth' }));
  assert.equal(calls[0][0], 'client');
  assert.equal(calls[0][1], 'console');
  assert.deepEqual(calls[0][2], { text: 'adminauth' });
  assert.equal(calls[0][3], consoleTimeoutMs(undefined));
  await t.handler(t.schema.parse({ text: 'adminauth', waitMs: 4000, echo: false }));
  assert.deepEqual(calls[1][2], { text: 'adminauth', waitMs: 4000, echo: false });
  assert.equal(calls[1][3], 14000);
  assert.equal(t.schema.safeParse({ text: '' }).success, false);
  assert.equal(t.schema.safeParse({ text: 'x'.repeat(513) }).success, false);
  assert.equal(t.schema.safeParse({ text: 'a', waitMs: 10001 }).success, false);
  assert.ok(!isReadOnlyOp('console', { text: 'adminauth' }));
});

test('compact drops undefined and null', () => {
  assert.deepEqual(compact({ a: 1, b: undefined, c: null, d: 0 }), { a: 1, d: 0 });
});

test('the queue lets readback through and gates input, display and screenshots', () => {
  assert.ok(isReadOnlyOp('ui_read', { what: 'state' }));
  assert.ok(!isReadOnlyOp('screenshot', {}));
  assert.ok(isReadOnlyOp('ui_input', { action: 'status' }));
  assert.ok(isReadOnlyOp('display', { action: 'status' }));
  assert.ok(!isReadOnlyOp('ui_input', { action: 'click' }));
  assert.ok(!isReadOnlyOp('display', { action: 'set' }));
});
