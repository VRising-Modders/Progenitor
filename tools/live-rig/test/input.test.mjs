import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { createTracker } from '../lib/tracker.mjs';
import {
  executeStep,
  STEP_KINDS,
  createChildRegistry,
  cancelTrackedChildren,
} from '../lib/steps.mjs';
import { existsSync, readFileSync } from 'node:fs';
import {
  clickPointFromRect,
  controlRefusal,
  findControl,
  INPUT_DEFAULT_TIMEOUT_MS,
  INPUT_EVIDENCE_LABEL,
  observeInputArgv,
  observePs1Path,
  RECT_SELECTOR_ALIASES,
  windowsPowerShell51Path,
} from '../lib/input.mjs';
import { fakeClock } from '../lib/clock.mjs';
import { researchRoot } from '../../common/workspace.mjs';
import {
  createFakeWorld,
  fakeObserveSpawner,
  fakeObserveAsyncSpawner,
  REPO_ROOT,
  tempDir,
  testBridge,
} from './helpers.mjs';

function ctxFor(tmp, world, extra = {}) {
  const clock = extra.clock ?? fakeClock();
  const bridge = extra.bridge ?? testBridge(tmp.dir, world);
  const tracker = createTracker({ bridge, clock, timeoutMs: 500, pollMs: 100 });
  return {
    bridge,
    tracker,
    clock,
    side: extra.side ?? 'client',
    user: 'TestVampire',
    records: extra.records ?? {},
    findings: [],
    stepName: extra.stepName ?? 'test',
    caseSite: extra.caseSite,
    outDir: extra.outDir ?? tmp.dir,
    repoRoot: extra.repoRoot ?? tmp.dir,
    spawnSync: extra.spawnSync,
    spawn: extra.spawn,
    ...extra,
  };
}

function flag(args, name) {
  const i = args.indexOf(name);
  if (i < 0) return undefined;
  return args[i + 1];
}

test('STEP_KINDS includes input and assertWindow', () => {
  assert.ok(STEP_KINDS.includes('input'));
  assert.ok(STEP_KINDS.includes('assertWindow'));
});

test('target resolution: click point is rect centre plus offset in client pixels', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner();
  try {
    const ctx = ctxFor(tmp, world, { spawnSync: spawn });
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'resolution', width: 1280, height: 720, mode: 'windowed' },
    }, ctx);

    const rects = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'rects' },
    }, ctx);
    assert.ok(Array.isArray(rects.result.detail.rects), 'live mods_ui rects uses detail.rects');
    const control = findControl(rects.result, 'mainMenuButton');
    assert.ok(control, 'expected mainMenuButton → id main-menu');
    assert.equal(control.id, 'main-menu');
    const want = clickPointFromRect(control, { x: 4, y: -2 });

    const result = await executeStep({
      kind: 'input',
      action: 'click',
      target: { rectOf: 'mainMenuButton', offset: { x: 4, y: -2 } },
      label: 'os-injected',
      record: 'click720',
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.point.x, want.x);
    assert.equal(result.point.y, want.y);
    assert.equal(spawn.calls.length, 1);
    const args = spawn.calls[0].args;
    assert.equal(args[5], 'click');
    assert.equal(flag(args, '-Window'), 'client');
    assert.equal(flag(args, '-X'), String(want.x));
    assert.equal(flag(args, '-Y'), String(want.y));
    assert.ok(args.includes('-AllowGameInput'));
    assert.ok(args.includes('-Json'));
    assert.equal(spawn.calls[0].spawnOpts.timeout, INPUT_DEFAULT_TIMEOUT_MS);
    assert.equal(spawn.calls[0].spawnOpts.stdio[0], 'ignore');
    const expected = observeInputArgv({
      script: observePs1Path(tmp.dir),
      action: 'click',
      x: want.x,
      y: want.y,
    });
    assert.deepEqual(args, expected);
    assert.equal(spawn.calls[0].cmd, windowsPowerShell51Path());
    assert.equal(ctx.records.click720.point.x, want.x);
  } finally {
    tmp.cleanup();
  }
});

test('occlusion refusal does not spawn observe.ps1', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    modsUiRectOverrides: { close: { occludedBy: 'curtain' } },
  });
  const spawn = fakeObserveSpawner();
  try {
    const ctx = ctxFor(tmp, world, { spawnSync: spawn });
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'main-menu' },
    }, ctx);
    const result = await executeStep({
      kind: 'input',
      action: 'click',
      target: { rectOf: 'close' },
      label: 'os-injected',
    }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /occluded by curtain/);
    assert.equal(spawn.calls.length, 0);
    assert.equal(result.label, INPUT_EVIDENCE_LABEL);
  } finally {
    tmp.cleanup();
  }
});

test('observe failure fails the input step and records exit/stdout', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner({
    inputExitCode: 3,
    stderr: 'observe: session locked\n',
    stdout: JSON.stringify({ ok: false, action: 'click', exitCode: 3 }),
  });
  try {
    const ctx = ctxFor(tmp, world, { spawnSync: spawn });
    const result = await executeStep({
      kind: 'input',
      action: 'click',
      target: { rectOf: 'mainMenuButton' },
      label: 'os-injected',
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.exitCode, 3);
    assert.match(String(result.error), /observe\.ps1 click exited 3/);
    assert.match(String(result.stderrTail), /session locked/);
    assert.equal(spawn.calls.length, 1);
  } finally {
    tmp.cleanup();
  }
});

test('input step records the os-injected evidence label', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner();
  try {
    const ctx = ctxFor(tmp, world, { spawnSync: spawn });
    const result = await executeStep({
      kind: 'input',
      action: 'click',
      target: { rectOf: 'mainMenuButton' },
      label: 'os-injected',
      record: 'entryClick',
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.label, 'os-injected');
    assert.equal(result.evidence, 'os-injected');
    assert.equal(ctx.records.entryClick.label, 'os-injected');
    assert.equal(ctx.records.entryClick.evidence, 'os-injected');
    assert.ok(result.before);
    assert.ok(result.after);
    assert.ok(result.rect);
    assert.ok(result.point);
    assert.ok(result.observe.command);

    const refused = await executeStep({
      kind: 'input',
      action: 'click',
      target: { rectOf: 'mainMenuButton' },
      label: 'handler-invocation',
    }, ctx);
    assert.equal(refused.ok, false);
    assert.match(String(refused.error), /os-injected/);
    assert.match(String(refused.error), /handler-invocation/);
  } finally {
    tmp.cleanup();
  }
});

test('assertWindow passes on client size/mode and fails on mismatch', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'resolution', width: 1280, height: 720, mode: 'windowed' },
    }, ctx);
    const raw = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'window' },
    }, ctx);
    const detail = raw.result?.detail;
    assert.equal(typeof detail?.client, 'object', 'mods_ui window detail must be nested client/screen/mode');
    assert.equal(detail.client.width, 1280);
    assert.equal(detail.client.height, 720);
    assert.equal(detail.mode, 'windowed');
    assert.equal(Object.prototype.hasOwnProperty.call(detail, 'clientWidth'), false, 'flat clientWidth on the fake would hide a live nested miss');
    assert.equal(Object.prototype.hasOwnProperty.call(detail, 'clientHeight'), false);

    const pass = await executeStep({
      kind: 'assertWindow',
      expect: { clientWidth: 1280, clientHeight: 720, mode: 'windowed' },
      record: 'win',
    }, ctx);
    assert.equal(pass.ok, true, pass.error);
    assert.equal(pass.clientWidth, 1280);
    assert.equal(pass.clientHeight, 720);
    assert.equal(pass.mode, 'windowed');
    assert.equal(pass.result.detail.client.width, 1280);
    assert.equal(ctx.records.win.clientWidth, 1280);
    assert.equal(ctx.records.win.result.detail.client.width, 1280);

    const fail = await executeStep({
      kind: 'assertWindow',
      expect: { clientWidth: 1920, clientHeight: 1080, mode: 'windowed' },
    }, ctx);
    assert.equal(fail.ok, false);
    assert.match(String(fail.error), /clientWidth/);
    assert.match(String(fail.error), /1920/);
  } finally {
    tmp.cleanup();
  }
});

test('key and type spawn observe.ps1 with the matching action and no click coordinates', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const spawn = fakeObserveSpawner();
  try {
    const ctx = ctxFor(tmp, world, { spawnSync: spawn });
    const key = await executeStep({
      kind: 'input',
      action: 'key',
      keys: 'F10',
      label: 'os-injected',
    }, ctx);
    assert.equal(key.ok, true, key.error);
    assert.equal(spawn.calls[0].args[5], 'key');
    assert.equal(spawn.calls[0].args[6], 'F10');
    assert.equal(flag(spawn.calls[0].args, '-X'), undefined);
    assert.ok(spawn.calls[0].args.includes('-AllowGameInput'));

    const typed = await executeStep({
      kind: 'input',
      action: 'type',
      text: '12',
      label: 'os-injected',
    }, ctx);
    assert.equal(typed.ok, true, typed.error);
    assert.equal(spawn.calls[1].args[5], 'type');
    assert.equal(spawn.calls[1].args[6], '12');

    const scrolled = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'main-menu' },
    }, ctx);
    assert.equal(scrolled.ok, true, scrolled.error);
    const wheel = await executeStep({
      kind: 'input',
      action: 'scroll',
      target: { rectOf: 'scrollbar:list:handle' },
      delta: -3,
      label: 'os-injected',
    }, ctx);
    assert.equal(wheel.ok, true, wheel.error);
    assert.equal(spawn.calls[2].args[5], 'scroll');
    assert.equal(flag(spawn.calls[2].args, '-Delta'), '-3');
    assert.ok(flag(spawn.calls[2].args, '-X'));
  } finally {
    tmp.cleanup();
  }
});

test('input refuses to spawn observe.ps1 under the test runner without an injected spawn', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    delete ctx.spawn;
    delete ctx.spawnSync;
    const result = await executeStep({
      kind: 'input',
      action: 'click',
      target: { rectOf: 'mainMenuButton' },
      label: 'os-injected',
    }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /without an injected spawn/);
  } finally {
    tmp.cleanup();
  }
});

test('not-visible and not-interactable targets fail before spawn', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    modsUiRectOverrides: {
      close: { visible: false },
      search: { interactable: false },
    },
  });
  const spawn = fakeObserveSpawner();
  try {
    const ctx = ctxFor(tmp, world, { spawnSync: spawn });
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'main-menu' },
    }, ctx);
    const hidden = await executeStep({
      kind: 'input',
      action: 'click',
      target: { rectOf: 'close' },
      label: 'os-injected',
    }, ctx);
    assert.equal(hidden.ok, false);
    assert.match(String(hidden.error), /not visible/);
    const inert = await executeStep({
      kind: 'input',
      action: 'click',
      target: { rectOf: 'search' },
      label: 'os-injected',
    }, ctx);
    assert.equal(inert.ok, false);
    assert.match(String(inert.error), /not interactable/);
    assert.equal(spawn.calls.length, 0);
  } finally {
    tmp.cleanup();
  }
});

test('cancelTrackedChildren kills a fake long-running input child and refuses new ones', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const spawn = fakeObserveAsyncSpawner({ hang: true });
    const ctx = ctxFor(tmp, world, {
      spawn,
      children: createChildRegistry(),
      childKillGraceMs: 20,
    });
    const started = executeStep({
      kind: 'input',
      action: 'key',
      keys: 'F10',
      label: 'os-injected',
      timeoutMs: 30000,
    }, ctx);
    for (let i = 0; i < 50 && spawn.calls.length === 0; i++) {
      await new Promise((resolve) => setImmediate(resolve));
    }
    assert.equal(spawn.calls.length, 1);
    assert.equal(ctx.children.entries[0]?.kind, 'input');
    const summary = await cancelTrackedChildren(ctx, { graceMs: 20 });
    assert.equal(summary.requested, true);
    assert.equal(summary.children, 1);
    assert.equal(summary.killed, 1);
    const result = await started;
    assert.equal(result.ok, false);
    assert.equal(result.cancelled, true);
    const refused = await executeStep({
      kind: 'input',
      action: 'key',
      keys: 'F10',
      label: 'os-injected',
      timeoutMs: 1000,
    }, ctx);
    assert.equal(refused.ok, false);
    assert.equal(refused.cancelled, true);
    assert.match(String(refused.error), /cancelled/);
    assert.equal(spawn.calls.length, 1);
  } finally {
    tmp.cleanup();
  }
});

test('observeInputArgv command lines match click/move/scroll/key/type', () => {
  const script = path.join('C:', 'repo', 'tools', 'observe.ps1');
  assert.deepEqual(
    observeInputArgv({ script, action: 'click', x: 820, y: 512 }),
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, 'click', '-Window', 'client', '-X', '820', '-Y', '512', '-AllowGameInput', '-Json'],
  );
  assert.deepEqual(
    observeInputArgv({ script, action: 'move', x: 10, y: 20 }),
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, 'move', '-Window', 'client', '-X', '10', '-Y', '20', '-AllowGameInput', '-Json'],
  );
  assert.deepEqual(
    observeInputArgv({ script, action: 'scroll', x: 300, y: 180, delta: -3 }),
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, 'scroll', '-Window', 'client', '-X', '300', '-Y', '180', '-Delta', '-3', '-AllowGameInput', '-Json'],
  );
  assert.deepEqual(
    observeInputArgv({ script, action: 'key', keys: 'F10' }),
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, 'key', 'F10', '-Window', 'client', '-AllowGameInput', '-Json'],
  );
  assert.deepEqual(
    observeInputArgv({ script, action: 'type', text: '12' }),
    ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, 'type', '12', '-Window', 'client', '-AllowGameInput', '-Json'],
  );
});

test('selector aliases resolve to live rect ids on the fake world', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'open', site: 'top-tab' },
    }, ctx);
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: {
        action: 'select',
        plugin: 'progenitor.sample',
        page: 'settings',
        group: 'Server',
      },
    }, ctx);
    const rects = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'rects' },
    }, ctx);
    const payload = rects.result;
    assert.equal(Object.prototype.hasOwnProperty.call(payload.detail, 'controls'), false, 'live payload is detail.rects, not detail.controls');
    const hit = (sel, id, extra = {}) => {
      const row = findControl(payload, sel);
      assert.ok(row, `expected ${sel} → ${id}`);
      assert.equal(row.id, id, sel);
      for (const [k, v] of Object.entries(extra)) assert.equal(row[k], v, `${sel} ${k}`);
      return row;
    };
    assert.equal(RECT_SELECTOR_ALIASES.topTab, 'top-tab');
    hit('topTab', 'top-tab', { label: 'Mods' });
    hit('mainMenuButton', 'main-menu', { label: 'Mods' });
    hit('close', 'close-back');
    hit('search', 'search');
    hit('sidebarRow:progenitor.sample', 'sidebar-row', { plugin: 'progenitor.sample' });
    hit('nativeTab:Spellbook', 'native-tab', { label: 'Spellbook' });
    hit('scrollbar:list:handle', 'list-scrollbar-handle');
    hit('scrollbar:content:handle', 'content-scrollbar-handle');
    const input = hit('widget:ExampleLimit:input', 'input');
    assert.ok(input.width < 400, 'widget:ExampleLimit:input is the nested input, not the row');
    hit('widget:ExampleLimit:apply', 'widget', { label: 'Apply ExampleLimit' });
    hit('widget:ShowHints:toggle', 'toggle');
    assert.equal(findControl(payload, 'consent:Accept'), null, 'no consent rows after server distribution was removed');

    const miss = findControl(payload, 'no-such-control');
    assert.equal(miss, null);
    const refused = controlRefusal(miss, 'no-such-control', payload);
    assert.match(refused, /available ids/);
    assert.match(refused, /top-tab/);
    assert.match(refused, /close-back/);
    assert.match(refused, /sidebar-row:progenitor.sample/);
    assert.match(refused, /native-tab:Spellbook/);
  } finally {
    tmp.cleanup();
  }
});

test('assertWindow baseline record path is nested detail.client.width', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const ctx = ctxFor(tmp, world);
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'window' },
      record: 'baselineWindow',
    }, ctx);
    assert.equal(ctx.records.baselineWindow.detail.client.width, 1920);
    assert.equal(Object.prototype.hasOwnProperty.call(ctx.records.baselineWindow.detail, 'clientWidth'), false);
    const pass = await executeStep({
      kind: 'assertWindow',
      expect: {
        clientWidth: '@record:baselineWindow.detail.client.width',
        clientHeight: '@record:baselineWindow.detail.client.height',
        mode: '@record:baselineWindow.detail.mode',
      },
    }, ctx);
    assert.equal(pass.ok, true, pass.error);
    assert.equal(pass.clientWidth, 1920);
  } finally {
    tmp.cleanup();
  }
});

test('shortcut key uses status.shortcut, else the case shortcut field', async () => {
  const tmp = tempDir();
  const spawn = fakeObserveSpawner();
  try {
    const withStatus = createFakeWorld({ modsUiShortcut: 'F9' });
    const ctx = ctxFor(tmp, withStatus, { spawnSync: spawn, caseDoc: { shortcut: 'F10' } });
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
      record: 'baselineResolution',
    }, ctx);
    const fromStatus = await executeStep({
      kind: 'input',
      action: 'key',
      keys: '@record:baselineResolution.after.shortcut',
      label: 'os-injected',
    }, ctx);
    assert.equal(fromStatus.ok, true, fromStatus.error);
    assert.equal(fromStatus.keys, 'F9');
    assert.equal(spawn.calls[0].args[6], 'F9');

    const omitted = createFakeWorld({ modsUiOmitShortcut: true });
    const spawn2 = fakeObserveSpawner();
    const ctx2 = ctxFor(tmp, omitted, { spawnSync: spawn2, caseDoc: { shortcut: 'F10' } });
    await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
      record: 'baselineResolution',
    }, ctx2);
    assert.equal(Object.prototype.hasOwnProperty.call(ctx2.records.baselineResolution.after, 'shortcut'), false);
    const fromCase = await executeStep({
      kind: 'input',
      action: 'key',
      keys: '@record:baselineResolution.after.shortcut',
      label: 'os-injected',
    }, ctx2);
    assert.equal(fromCase.ok, true, fromCase.error);
    assert.equal(fromCase.keys, 'F10');
    assert.equal(spawn2.calls[0].args[6], 'F10');
  } finally {
    tmp.cleanup();
  }
});

// The recorded live fixtures are local evidence outside the repository (PROGENITOR_LOCAL_EVIDENCE,
// tools/common/workspace.mjs); without it the tests that replay them are skipped.
const EVIDENCE_ROOT = researchRoot();
const LIVE_RECTS_DIR = EVIDENCE_ROOT
  ? path.resolve(EVIDENCE_ROOT, 'reviews', '2026-09-09-mods-ui-usability', 'os-injected-input')
  : null;

// The live fixtures were recorded before the 2026-10 id rename, when Progenitor's plugin ids carried a
// one-segment vendor prefix (`<vendor>.progenitor.sample`). Read them with the current ids, as Core's
// LegacyIds does for state an older build wrote.
const LEGACY_ID_PREFIX = /(?<=["':\s])[A-Za-z0-9_-]+\.(?=progenitor(?:[."\\\s]|$))/g;

function readJsonStripBom(file) {
  assert.ok(existsSync(file), `live fixture missing: ${file}`);
  return JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, '').replace(LEGACY_ID_PREFIX, ''));
}

function liveRectsFromCallSave(file) {
  const wrap = readJsonStripBom(file);
  return JSON.parse(wrap.stdout);
}

function liveRectsFromReport(file) {
  const report = readJsonStripBom(file);
  const step = (report.steps ?? []).find((s) => s.detail?.rects?.action === 'rects');
  assert.ok(step, `expected an input step that recorded rects in ${file}`);
  return step.detail.rects;
}

test('live rects fixtures resolve case selectors; a mismatch fails offline', { skip: LIVE_RECTS_DIR && existsSync(LIVE_RECTS_DIR) ? false : 'needs PROGENITOR_LOCAL_EVIDENCE with the recorded live fixtures' }, () => {
  const closed = liveRectsFromCallSave(path.join(LIVE_RECTS_DIR, '00-baseline', '08-client-mods_ui-rects.json'));
  assert.ok(Array.isArray(closed.detail.rects));
  const closedHit = (sel, id, extra = {}) => {
    const row = findControl(closed, sel);
    assert.ok(row, `closed live rects: ${sel} → ${id}`);
    assert.equal(row.id, id, sel);
    for (const [k, v] of Object.entries(extra)) assert.equal(row[k], v, `${sel} ${k}`);
    return row;
  };
  closedHit('mainMenuButton', 'main-menu', { label: 'Mods' });
  closedHit('topTab', 'top-tab', { label: 'Mods' });
  closedHit('close', 'close-back', { label: 'Close Mods' });
  closedHit('search', 'search');
  closedHit('nativeTab:Spellbook', 'native-tab', { label: 'Spellbook', index: 2 });
  closedHit('sidebarRow:progenitor.sample', 'sidebar-row', { plugin: 'progenitor.sample' });
  closedHit('scrollbar:list:handle', 'list-scrollbar-handle');
  closedHit('scrollbar:content:handle', 'content-scrollbar-handle');
  assert.equal(findControl(closed, 'topTab').x, 2451);
  assert.equal(findControl(closed, 'topTab').width, 112);

  const open = liveRectsFromReport(path.join(LIVE_RECTS_DIR, '20260909T125244Z-input-list-and-scroll', 'report.json'));
  const sample = findControl(open, 'sidebarRow:progenitor.sample');
  assert.ok(sample, 'open live rects: sidebarRow:progenitor.sample');
  assert.equal(sample.id, 'sidebar-row');
  assert.equal(sample.plugin, 'progenitor.sample');
  assert.equal(findControl(open, 'scrollbar:list:handle').id, 'list-scrollbar-handle');
  assert.equal(findControl(open, 'nativeTab:Spellbook').label, 'Spellbook');

  const settings = liveRectsFromReport(path.join(LIVE_RECTS_DIR, '20260909T125249Z-input-settings', 'report.json'));
  const input = findControl(settings, 'widget:ExampleLimit:input');
  assert.ok(input, 'settings live rects: widget:ExampleLimit:input');
  assert.equal(input.id, 'input');
  assert.equal(input.width, 180);
  const apply = findControl(settings, 'widget:ExampleLimit:apply');
  assert.ok(apply, 'settings live rects: widget:ExampleLimit:apply');
  assert.equal(apply.id, 'widget');
  assert.equal(apply.label, 'Apply ExampleLimit');

  const miss = findControl(closed, 'widget:ExampleLimit:input');
  assert.equal(miss, null, 'closed baseline has no ExampleLimit widget');
  const refused = controlRefusal(miss, 'widget:ExampleLimit:input', closed);
  assert.match(refused, /available ids/);
  assert.match(refused, /top-tab/);
});
