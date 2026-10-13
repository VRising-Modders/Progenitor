import test from 'node:test';
import assert from 'node:assert/strict';
import fs, { readFileSync, existsSync, writeFileSync, copyFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import {
  loadCase,
  runCase,
  cleanupLeftovers,
  cleanupEntity,
  FINAL_CHECKS_ORDER,
  leftoverEntitiesFromReport,
  sourceReportListedLeftoverCount,
  sessionPinFromPing,
  sessionPinsMatch,
  SESSION_START_TOLERANCE_MS,
  prefabsMatch,
  resolveRunSite,
  applyOpenSitePlaceholder,
  defaultSiteForWorld,
  SITE_PLACEHOLDER,
  CASE_WORLD_CONNECTED,
  CASE_WORLD_MAIN_MENU,
  caseEndWorld,
  createRestoreRegistry,
  noteStepRestoration,
} from '../lib/run.mjs';
import { fakeClock } from '../lib/clock.mjs';
import { STEP_KINDS } from '../lib/steps.mjs';
import { PROTECTION_UNAVAILABLE } from '../lib/checks.mjs';
import { createFakeWorld, tempDir, runOpts, installFakeSample, TOOL_DIR, DEFAULT_SAMPLE_HASH, SWAP_SAMPLE_HASH } from './helpers.mjs';

function readReport(runDir) {
  return JSON.parse(readFileSync(path.join(runDir, 'report.json'), 'utf8'));
}

test('a cleanup that succeeds is complete', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  try {
    const { report, runDir } = await runCase({
      name: 'cleanup-ok',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
        { kind: 'wait', ms: 1000 },
        { kind: 'destroyTracked', name: 'rat' },
        { kind: 'exists', entity: '@tracked:rat', expect: false },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'complete');
    assert.equal(report.cleanup.ok, true);
    assert.deepEqual(report.cleanup.remaining, []);
    assert.ok(report.finalChecks.every((c) => c.ok));
    assert.ok(lines.some((l) => l.startsWith('status: complete')));
    assert.equal(readReport(runDir).status, 'complete');
    assert.deepEqual(report.finalChecksOrder, FINAL_CHECKS_ORDER);
    assert.equal(report.finalChecksOrder[0], 'cleanupAll');
    assert.equal(report.leftoverSummary.trackedRemaining, 0);
    assert.equal(report.leftoverSummary.unconfirmed, 0);
    assert.equal(report.leftoverSummary.carriersLeftover, false);
    assert.equal(report.leftoverSummary.adminRestored, true);
    assert.equal(report.leftoverSummary.protectionOk, true);
    assert.equal(report.startState.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.startState.world, 'connected');
  } finally {
    tmp.cleanup();
  }
});

test('a tracked entity left behind makes the run incomplete and lists it', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  try {
    const { report } = await runCase({
      name: 'cleanup-timeout',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
        { kind: 'skipCleanupFor', name: 'rat' },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'incomplete');
    assert.equal(report.cleanup.ok, false);
    assert.equal(report.cleanup.remaining.length, 1);
    assert.equal(report.cleanup.remaining[0].prefab, 'CHAR_Critter_Rat');
    assert.equal(report.cleanup.remaining[0].skipCleanup, true);
    assert.ok(!lines.some((l) => /\bcomplete\b/.test(l) && l.startsWith('status:')));
    assert.ok(lines.some((l) => l.startsWith('status: incomplete')));
    assert.ok(lines.some((l) => /CHAR_Critter_Rat/.test(l)));
    const remainingCheck = report.finalChecks.find((c) => c.name === 'remainingTestEntities');
    assert.equal(remainingCheck.ok, false);
    assert.equal(report.leftoverSummary.trackedRemaining, 1);
    assert.equal(report.leftoverSummary.unconfirmed, 0);
    assert.equal(report.leftoverSummary.protectionOk, true);
  } finally {
    tmp.cleanup();
  }
});

test('a failed step still runs case teardown and cannot complete', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'teardown-on-abort',
      steps: [
        { kind: 'exists', entity: '999:1', expect: true },
        { kind: 'command', text: '.should-not-run' },
      ],
      teardown: [{ kind: 'command', text: '.frostdash off' }],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'aborted');
    assert.ok(!world.calls.some((c) => c.op === 'command' && c.args.text === '.should-not-run'));
    assert.ok(world.calls.some((c) => c.op === 'command' && c.args.text === '.frostdash off'));
    assert.ok(report.teardown);
    assert.equal(report.teardown.steps.length, 1);
    assert.equal(report.teardown.steps[0].ok, true);
    assert.match(report.teardown.steps[0].name, /frostdash off/);
  } finally {
    tmp.cleanup();
  }
});

test('teardown after a removal restores first and then the command is handled', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
  });
  installFakeSample(world, 'server');
  try {
    const { report } = await runCase({
      name: 'teardown-restore-then-off',
      steps: [{ kind: 'removal' }],
      teardown: [{
        kind: 'command',
        text: '.frostdash off',
        expectLog: { filter: 'FROSTDASH', regex: 'FROSTDASH cleanup alive=' },
      }],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.ok(report.teardown?.restore);
    assert.equal(report.teardown.restore.ok, true);
    assert.equal(typeof report.teardown.restore.elapsedMs, 'number');
    const restoreLogs = (report.teardown.restore.logs ?? []).map((l) => String(l.text ?? l));
    assert.ok(restoreLogs.some((t) => /loaded Progenitor Sample .+from Progenitor\.Sample\.dll/.test(t)));
    assert.equal(report.teardown.steps.length, 1);
    assert.equal(report.teardown.steps[0].ok, true);
    assert.match(String(report.teardown.steps[0].detail.line), /FROSTDASH cleanup alive=/);
    const removalReload = world.calls.findIndex((c) => c.op === 'reload');
    const offIdx = world.calls.findIndex((c) => c.op === 'command' && c.args.text === '.frostdash off');
    const restoreReload = world.calls.findIndex((c, i) => i > removalReload && c.op === 'reload');
    assert.ok(removalReload >= 0);
    assert.ok(restoreReload > removalReload);
    assert.ok(offIdx > restoreReload, 'teardown command ran before hold-restore');
  } finally {
    tmp.cleanup();
  }
});

test('teardown command without a handler line fails and the run is incomplete', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'teardown-no-handler',
      steps: [],
      teardown: [{ kind: 'command', text: '.no-such-command' }],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'incomplete');
    assert.notEqual(report.status, 'complete');
    assert.match(String(report.reason), /teardown command \.no-such-command failed/);
    assert.match(String(report.reason), /no handler line/);
    assert.equal(report.teardown.steps.length, 1);
    assert.equal(report.teardown.steps[0].ok, false);
    assert.match(String(report.teardown.steps[0].detail.error), /no handler line for \.no-such-command/);
    assert.ok(!report.teardown.restore);
  } finally {
    tmp.cleanup();
  }
});

test('teardown on a normal abort runs immediately without hold-restore', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'teardown-immediate-abort',
      steps: [
        { kind: 'exists', entity: '999:1', expect: true },
      ],
      teardown: [{ kind: 'command', text: '.frostdash off' }],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'aborted');
    assert.ok(!report.teardown.restore);
    assert.equal(world.calls.filter((c) => c.op === 'reload').length, 0);
    const offIdx = world.calls.findIndex((c) => c.op === 'command' && c.args.text === '.frostdash off');
    assert.ok(offIdx >= 0);
    assert.equal(report.teardown.steps[0].ok, true);
  } finally {
    tmp.cleanup();
  }
});

test('a step failure aborts remaining steps but still runs cleanup and checks', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  try {
    const { report } = await runCase({
      name: 'abort-rest',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, track: true },
        { kind: 'exists', entity: '@tracked:rat', expect: false },
        { kind: 'command', text: '.should-not-run' },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'aborted');
    assert.equal(report.steps.length, 2);
    assert.equal(report.steps[0].ok, true);
    assert.equal(report.steps[1].ok, false);
    assert.ok(!world.calls.some((c) => c.op === 'command' && c.args.text === '.should-not-run'));
    assert.ok(world.calls.some((c) => c.op === 'entity' && c.args.action === 'destroy'));
    assert.equal(report.cleanup.ok, true);
    assert.ok(report.finalChecks.length >= 5);
    assert.ok(report.finalChecks.every((c) => c.ok));
    assert.ok(lines.some((l) => l.startsWith('status: aborted')));
  } finally {
    tmp.cleanup();
  }
});

const COMPLETE_STEPS = [
  { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
  { kind: 'wait', ms: 1000 },
  { kind: 'destroyTracked', name: 'rat' },
  { kind: 'exists', entity: '@tracked:rat', expect: false },
];

test('a non-admin user is granted admin for the run and restored before the admin-state check', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    assert.equal(world.users[0].admin, false);
    const { report } = await runCase({ name: 'admin-grant', steps: COMPLETE_STEPS }, runOpts(tmp, world));
    assert.equal(report.status, 'complete');
    assert.ok(report.adminGranted);
    assert.equal(report.adminGranted.userEntity, world.users[0].entity);
    assert.equal(report.adminGranted.userName, 'TestVampire');
    assert.ok(report.adminGranted.at);
    assert.ok(report.adminGranted.restoredAt);
    assert.deepEqual(world.adminWrites, [{ IsAdmin: true }, { IsAdmin: false }]);
    const adminCheck = report.finalChecks.find((c) => c.name === 'adminState');
    assert.equal(adminCheck.ok, true);
    assert.equal(adminCheck.value.expected, false);
    assert.equal(adminCheck.value.admin, false);
    assert.equal(world.users[0].admin, false);
  } finally {
    tmp.cleanup();
  }
});

test('an already-admin user is left unchanged and adminGranted is null', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  try {
    const { report } = await runCase({ name: 'admin-keep', steps: COMPLETE_STEPS }, runOpts(tmp, world));
    assert.equal(report.status, 'complete');
    assert.equal(report.adminGranted, null);
    assert.deepEqual(world.adminWrites, []);
    assert.ok(!world.calls.some((c) => c.op === 'set'));
    const adminCheck = report.finalChecks.find((c) => c.name === 'adminState');
    assert.equal(adminCheck.ok, true);
    assert.equal(adminCheck.value.expected, true);
    assert.equal(world.users[0].admin, true);
  } finally {
    tmp.cleanup();
  }
});

test('admin grant is restored when startState never records (exception after grant)', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  world.throwOn('query', 'carriers failed');
  try {
    const { report, runDir } = await runCase({ name: 'grant-before-start', steps: COMPLETE_STEPS }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'aborted');
    assert.match(report.reason, /carriers failed/);
    assert.equal(report.startState, null);
    assert.ok(report.adminGranted);
    assert.ok(report.adminGranted.restoredAt);
    assert.equal(world.users[0].admin, false);
    assert.deepEqual(world.adminWrites, [{ IsAdmin: true }, { IsAdmin: false }]);
    assert.ok(lines.some((l) => l.includes('admin: restored')));
    assert.equal(JSON.parse(readFileSync(path.join(runDir, 'report.json'), 'utf8')).status, 'aborted');
  } finally {
    tmp.cleanup();
  }
});

test('an exists-poll timeout during cleanup still restores admin and writes report.json', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  const orig = world.request.bind(world);
  world.request = async (side, op, args) => {
    if (op === 'entity' && args.action === 'exists') {
      throw new Error("server bridge timed out on 'entity' after 30 ms");
    }
    return orig(side, op, args);
  };
  try {
    const { report, runDir } = await runCase({
      name: 'exists-timeout',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, track: true },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.notEqual(report.status, 'complete');
    assert.equal(report.cleanup.remaining.length, 0);
    assert.ok(report.cleanup.unconfirmed.length >= 1);
    assert.match(String(report.cleanup.unconfirmed[0].error || report.cleanup.error || report.reason), /timed out/);
    assert.equal(report.leftoverSummary.unconfirmed, report.cleanup.unconfirmed.length);
    assert.equal(report.leftoverSummary.trackedRemaining, 0);
    assert.ok(report.adminGranted);
    assert.ok(report.adminGranted.restoredAt);
    assert.equal(world.users[0].admin, false);
    assert.ok(existsSync(path.join(runDir, 'report.json')));
    assert.equal(JSON.parse(readFileSync(path.join(runDir, 'report.json'), 'utf8')).status, report.status);
  } finally {
    tmp.cleanup();
  }
});

test('a throw in final checks still restores admin and writes an aborted report', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  world.throwOn('get', 'health read failed');
  try {
    const { report, runDir } = await runCase({ name: 'check-throw', steps: COMPLETE_STEPS }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'aborted');
    assert.ok(report.adminGranted?.restoredAt);
    assert.equal(world.users[0].admin, false);
    assert.ok(lines.some((l) => l.includes('admin: restored')));
    assert.equal(JSON.parse(readFileSync(path.join(runDir, 'report.json'), 'utf8')).status, 'aborted');
  } finally {
    tmp.cleanup();
  }
});

test('final protection re-grants if the case already restored admin false', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  try {
    const { report } = await runCase({
      name: 'admin-dropped-before-checks',
      steps: [
        {
          kind: 'op',
          name: 'drop admin',
          side: 'server',
          op: 'set',
          args: { entity: world.users[0].entity, component: 'User', fields: { IsAdmin: false } },
        },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'complete', report.reason);
    const protection = report.finalChecks.find((c) => c.name === 'protection');
    assert.equal(protection.ok, true, protection?.evidence);
    const adminCheck = report.finalChecks.find((c) => c.name === 'adminState');
    assert.equal(adminCheck.ok, true);
    assert.equal(adminCheck.value.expected, false);
    assert.equal(adminCheck.value.admin, false);
    assert.equal(world.users[0].admin, false);
    assert.ok(lines.some((l) => l.includes('admin: re-granted for protection readback')));
    const calls = world.calls;
    const firstFalse = calls.findIndex((c) => c.op === 'set' && c.args?.fields?.IsAdmin === false);
    assert.ok(firstFalse >= 0, 'case did not drop admin');
    const regrant = calls.findIndex((c, i) => i > firstFalse && c.op === 'set' && c.args?.fields?.IsAdmin === true);
    assert.ok(regrant > firstFalse, 'final protection did not re-grant after the case dropped admin');
    const statusIdxs = calls
      .map((c, i) => (c.op === 'command' && c.args?.text === '.debug status' ? i : -1))
      .filter((i) => i >= 0);
    const lastStatus = statusIdxs[statusIdxs.length - 1];
    assert.ok(lastStatus > regrant, 'final .debug status must run after the re-grant');
    const restore = calls.findIndex((c, i) => i > lastStatus && c.op === 'set' && c.args?.fields?.IsAdmin === false);
    assert.ok(restore > lastStatus, 'admin restore must follow the protection readback');
  } finally {
    tmp.cleanup();
  }
});

test('teardown restore-10 polls Saved before run-level admin restore', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const userEntity = world.users[0].entity;
  try {
    const { report } = await runCase({
      name: 'restore-10-poll',
      world: 'connected',
      site: 'top-tab',
      steps: [
        { kind: 'op', name: 'open', side: 'client', op: 'mods_ui', args: { action: 'open', site: 'top-tab' }, expect: { ok: true } },
        { kind: 'op', name: 'select Server', side: 'client', op: 'mods_ui', args: { action: 'select', plugin: 'progenitor.sample', page: 'settings', group: 'Server' }, expect: { ok: true } },
        { kind: 'op', name: 'type 11', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', index: 3, value: '11' }, expect: { ok: true } },
        { kind: 'op', name: 'Apply 11', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', label: 'Apply ExampleLimit' }, expect: { ok: true } },
        { kind: 'op', name: 'read 11', side: 'client', op: 'mods_ui', args: { action: 'widget_read', pane: 'content' }, poll: { intervalMs: 500, timeoutMs: 10000 }, expect: 'Saved by the server' },
      ],
      teardown: [
        { kind: 'op', name: 'teardown grant', side: 'server', op: 'set', args: { entity: userEntity, component: 'User', fields: { IsAdmin: true } } },
        { kind: 'op', name: 'teardown open', side: 'client', op: 'mods_ui', args: { action: 'open', site: 'top-tab' } },
        { kind: 'op', name: 'teardown select', side: 'client', op: 'mods_ui', args: { action: 'select', plugin: 'progenitor.sample', page: 'settings', group: 'Server' } },
        { kind: 'op', name: 'teardown type 10', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', index: 3, value: '10' } },
        { kind: 'op', name: 'teardown Apply 10', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', label: 'Apply ExampleLimit' } },
        { kind: 'op', name: 'teardown read saved', side: 'client', op: 'mods_ui', args: { action: 'widget_read', pane: 'content' }, poll: { intervalMs: 500, timeoutMs: 10000 }, record: 'teardownRestoreSaved', expect: 'Saved by the server' },
        { kind: 'op', name: 'teardown read value', side: 'client', op: 'mods_ui', args: { action: 'widget_read', pane: 'content' }, expect: '"value":"10"' },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.deepEqual(report.restorationUnconfirmed, []);
    assert.ok(report.restore.items.some((i) => i.item === 'settings' && i.ackRequired && i.ackObserved));
    assert.equal(world.modsUi.committedExampleLimit, '10');
    assert.equal(world.modsUi.values.ExampleLimit, '10');
    const saved = report.teardown.steps.find((s) => s.name === 'teardown read saved');
    assert.equal(saved?.ok, true, saved?.detail?.error);
    assert.ok((saved?.detail?.polls ?? 0) >= 2);
    assert.match(JSON.stringify(saved?.detail?.result ?? {}), /Saved by the server/);
    const value = report.teardown.steps.find((s) => s.name === 'teardown read value');
    assert.equal(value?.ok, true, value?.detail?.error);
    const protection = report.finalChecks.find((c) => c.name === 'protection');
    assert.equal(protection.ok, true, protection?.evidence);
    const adminCheck = report.finalChecks.find((c) => c.name === 'adminState');
    assert.equal(adminCheck.ok, true);
    assert.equal(adminCheck.value.admin, false);
    assert.equal(world.users[0].admin, false);
    const lastTeardownRead = world.calls.reduce((acc, c, i) => (
      c.op === 'mods_ui' && c.args?.action === 'widget_read' ? i : acc
    ), -1);
    const restoreIdx = world.calls.reduce((acc, c, i) => (
      c.op === 'set' && c.args?.fields?.IsAdmin === false ? i : acc
    ), -1);
    assert.ok(restoreIdx >= 0);
    assert.ok(lastTeardownRead >= 0 && lastTeardownRead < restoreIdx, 'teardown Saved poll must complete before run-level admin restore');
  } finally {
    tmp.cleanup();
  }
});

test('final protection re-grants after teardown restored admin false', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  const userEntity = world.users[0].entity;
  try {
    const { report } = await runCase({
      name: 'teardown-dropped-admin',
      steps: [],
      teardown: [
        {
          kind: 'op',
          name: 'teardown restore admin false',
          side: 'server',
          op: 'set',
          args: { entity: userEntity, component: 'User', fields: { IsAdmin: false } },
        },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'complete', report.reason);
    const protection = report.finalChecks.find((c) => c.name === 'protection');
    assert.equal(protection.ok, true, protection?.evidence);
    assert.ok(lines.some((l) => l.includes('admin: re-granted for protection readback')));
    assert.equal(world.users[0].admin, false);
  } finally {
    tmp.cleanup();
  }
});

test('teardown resolution is confirmed by a status readback', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'restore-resolution',
      world: 'connected',
      steps: [],
      teardown: [
        {
          kind: 'op',
          name: 'teardown restore resolution',
          side: 'client',
          op: 'mods_ui',
          args: { action: 'resolution', width: 1920, height: 1080, mode: 'window' },
        },
        {
          kind: 'op',
          name: 'teardown status closed',
          side: 'client',
          op: 'mods_ui',
          args: { action: 'status' },
        },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.deepEqual(report.restorationUnconfirmed, []);
    assert.ok(report.restore.items.some((i) => i.item === 'resolution' && i.ackRequired && i.ackObserved));
  } finally {
    tmp.cleanup();
  }
});

test('already-admin case that drops admin re-grants for protection and leaves original true', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.users[0].admin = true;
  const lines = [];
  try {
    const { report } = await runCase({
      name: 'started-admin-dropped',
      steps: [
        {
          kind: 'op',
          name: 'drop admin',
          side: 'server',
          op: 'set',
          args: { entity: world.users[0].entity, component: 'User', fields: { IsAdmin: false } },
        },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.adminGranted, null);
    const protection = report.finalChecks.find((c) => c.name === 'protection');
    assert.equal(protection.ok, true, protection?.evidence);
    const adminCheck = report.finalChecks.find((c) => c.name === 'adminState');
    assert.equal(adminCheck.ok, true);
    assert.equal(adminCheck.value.expected, true);
    assert.equal(world.users[0].admin, true);
    assert.ok(lines.some((l) => l.includes('admin: re-granted for protection readback')));
  } finally {
    tmp.cleanup();
  }
});

test('non-admin refusal at final protection is unavailable and does not fail the run by itself', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const orig = world.request.bind(world);
  let adminTrueSets = 0;
  world.request = async (side, op, args) => {
    if (op === 'set' && args?.fields?.IsAdmin === true) {
      adminTrueSets += 1;
      if (adminTrueSets > 1) {
        return { ok: true, result: { entity: args.entity, component: 'User', fields: { IsAdmin: true } } };
      }
    }
    if (op === 'command' && args?.text === '.debug status') {
      const caller = world.users.find((u) => u.name === args.user) ?? world.users[0];
      if (!caller?.admin) {
        world.pushLog('server', 'This command is admin-only.');
        return { ok: true, result: { injectedAs: args.user, text: args.text } };
      }
    }
    return orig(side, op, args);
  };
  try {
    const { report } = await runCase({
      name: 'protection-unavailable',
      steps: [
        {
          kind: 'op',
          name: 'drop admin',
          side: 'server',
          op: 'set',
          args: { entity: world.users[0].entity, component: 'User', fields: { IsAdmin: false } },
        },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    const protection = report.finalChecks.find((c) => c.name === 'protection');
    assert.equal(protection.unavailable, true);
    assert.equal(protection.ok, false);
    assert.equal(protection.value, null);
    assert.equal(protection.evidence, PROTECTION_UNAVAILABLE);
    assert.equal(report.leftoverSummary.protectionOk, false);
    const adminCheck = report.finalChecks.find((c) => c.name === 'adminState');
    assert.equal(adminCheck.ok, true);
  } finally {
    tmp.cleanup();
  }
});

test('restore registry observes settings only after Saved and a value readback', () => {
  const restore = createRestoreRegistry();
  noteStepRestoration({
    kind: 'op',
    name: 'teardown Apply 10',
    op: 'mods_ui',
    args: { action: 'widget_click', label: 'Apply ExampleLimit' },
  }, { ok: true }, restore, { teardown: true });
  assert.deepEqual(restore.pending().map((r) => r.item), ['settings']);
  noteStepRestoration({
    kind: 'op',
    op: 'mods_ui',
    args: { action: 'widget_read', pane: 'content' },
  }, { ok: true, result: { after: { note: 'Waiting for the server…' } } }, restore, { teardown: true });
  assert.equal(restore.pending().length, 1);
  noteStepRestoration({
    kind: 'op',
    op: 'mods_ui',
    args: { action: 'widget_read', pane: 'content' },
  }, { ok: true, result: { after: { note: 'Saved by the server.' } } }, restore, { teardown: true });
  assert.equal(restore.pending().length, 1, 'Saved without a value is not enough');
  noteStepRestoration({
    kind: 'op',
    op: 'mods_ui',
    args: { action: 'widget_read', pane: 'content' },
  }, { ok: true, result: { after: { note: 'Saved by the server.', value: '10' } } }, restore, { teardown: true });
  assert.equal(restore.pending().length, 0);
  assert.equal(restore.snapshot().unconfirmed.length, 0);
});

test('restore registry observes admin on get/users and resolution on window/status', () => {
  const restore = createRestoreRegistry();
  noteStepRestoration({
    kind: 'op',
    name: 'restore admin false',
    op: 'set',
    args: { entity: '2:1', component: 'User', fields: { IsAdmin: false } },
  }, { ok: true, args: { entity: '2:1', component: 'User', fields: { IsAdmin: false } } }, restore, {});
  assert.deepEqual(restore.pending().map((r) => r.item), ['admin']);
  noteStepRestoration({
    kind: 'op',
    op: 'get',
    args: { entity: '2:1', component: 'User' },
  }, { ok: true, result: { fields: { IsAdmin: false } } }, restore, {});
  assert.equal(restore.pending().length, 0);

  noteStepRestoration({
    kind: 'op',
    name: 'teardown restore resolution',
    op: 'mods_ui',
    args: { action: 'resolution', width: 1920, height: 1080, mode: 'windowed' },
  }, { ok: true, args: { action: 'resolution', width: 1920, height: 1080, mode: 'windowed' } }, restore, { teardown: true });
  assert.deepEqual(restore.pending().map((r) => r.item), ['resolution']);
  noteStepRestoration({
    kind: 'op',
    op: 'mods_ui',
    args: { action: 'status' },
  }, { ok: true, result: { after: { width: 1920, height: 1080, mode: 'windowed' } } }, restore, { teardown: true });
  assert.equal(restore.pending().length, 0);
});

test('settings Apply without Saved ack is restoration unconfirmed and incomplete', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const userEntity = world.users[0].entity;
  const lines = [];
  try {
    const { report } = await runCase({
      name: 'restore-unconfirmed-settings',
      world: 'connected',
      steps: [],
      teardown: [
        { kind: 'op', name: 'teardown grant', side: 'server', op: 'set', args: { entity: userEntity, component: 'User', fields: { IsAdmin: true } } },
        { kind: 'op', name: 'teardown open', side: 'client', op: 'mods_ui', args: { action: 'open', site: 'top-tab' } },
        { kind: 'op', name: 'teardown select', side: 'client', op: 'mods_ui', args: { action: 'select', plugin: 'progenitor.sample', page: 'settings', group: 'Server' } },
        { kind: 'op', name: 'teardown type 10', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', index: 3, value: '10' } },
        { kind: 'op', name: 'teardown Apply 10', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', label: 'Apply ExampleLimit' } },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'incomplete', report.reason);
    assert.match(report.reason, /restoration unconfirmed: settings/);
    assert.ok(report.findings.includes('restoration unconfirmed: settings'));
    assert.ok(report.findings.includes('admin restored despite unconfirmed restoration'));
    assert.ok(report.restore.unconfirmed.includes('settings'));
    assert.deepEqual(report.restorationUnconfirmed, ['settings']);
    assert.equal(report.adminRestoredDespiteUnconfirmed, true);
    assert.ok(report.restore.items.some((i) => i.item === 'settings' && i.ackRequired && !i.ackObserved));
    assert.ok(lines.some((l) => l.includes('restoration unconfirmed: settings')));
    assert.ok(lines.some((l) => l.includes('admin restored despite unconfirmed restoration')));
    assert.equal(world.users[0].admin, false);
    assert.equal(report.adminGranted.restored, true);
  } finally {
    tmp.cleanup();
  }
});

function hangingRunSpawn({ exitOn = 'SIGTERM', onSpawn } = {}) {
  const calls = [];
  function spawn(cmd, args, spawnOpts) {
    calls.push({ cmd, args, spawnOpts });
    const child = new EventEmitter();
    child.stdout = new PassThrough();
    child.stderr = new PassThrough();
    child.pid = 1;
    child.exitCode = null;
    child.killed = false;
    child.signals = [];
    let done = false;
    function finish(status, signal) {
      if (done) return;
      done = true;
      child.exitCode = status;
      child.signalCode = signal ?? null;
      child.stdout.end();
      child.stderr.end();
      child.emit('close', status, signal ?? null);
    }
    child.kill = (sig = 'SIGTERM') => {
      child.signals.push(sig);
      if (sig === exitOn || sig === 'SIGKILL') {
        child.killed = true;
        finish(null, sig);
      }
    };
    if (typeof onSpawn === 'function') onSpawn(child);
    return child;
  }
  spawn.calls = calls;
  return spawn;
}

test('stop kills a long-running input child and records cancelled', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  let stop;
  const spawn = hangingRunSpawn({
    exitOn: 'SIGKILL',
    onSpawn() {
      queueMicrotask(() => { if (typeof stop === 'function') stop(); });
    },
  });
  try {
    const { report } = await runCase({
      name: 'cancel-input',
      steps: [{ kind: 'input', action: 'key', keys: 'F10', label: 'os-injected', timeoutMs: 30000 }],
    }, runOpts(tmp, world, {
      spawn,
      childKillGraceMs: 20,
      registerStop(fn) { stop = fn; },
    }));
    assert.equal(report.status, 'aborted');
    assert.match(String(report.reason), /cancelled/);
    assert.ok(report.cancelled, 'report.cancelled missing');
    assert.equal(report.cancelled.requested, true);
    assert.equal(report.cancelled.children, 1);
    assert.equal(report.cancelled.killed, 1);
    assert.equal(typeof report.cancelled.waitedMs, 'number');
    assert.ok(report.cancelled.waitedMs >= 0);
  } finally {
    tmp.cleanup();
  }
});

test('stop kills a long-running capture child and records cancelled', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  let stop;
  const spawn = hangingRunSpawn({
    exitOn: 'SIGKILL',
    onSpawn() {
      queueMicrotask(() => { if (typeof stop === 'function') stop(); });
    },
  });
  try {
    const { report } = await runCase({
      name: 'cancel-capture',
      steps: [{ kind: 'capture', name: 'hang', timeoutMs: 30000, requireFrames: false }],
    }, runOpts(tmp, world, {
      spawn,
      childKillGraceMs: 20,
      registerStop(fn) { stop = fn; },
    }));
    assert.equal(report.status, 'aborted');
    assert.match(String(report.reason), /cancelled/);
    assert.ok(report.cancelled, 'report.cancelled missing');
    assert.equal(report.cancelled.requested, true);
    assert.equal(report.cancelled.children, 1);
    assert.equal(report.cancelled.killed, 1);
    assert.equal(typeof report.cancelled.waitedMs, 'number');
    assert.ok(report.cancelled.waitedMs >= 0);
  } finally {
    tmp.cleanup();
  }
});

test('stop records cancelled when a child exits on the first signal', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  let stop;
  const spawn = hangingRunSpawn({
    exitOn: 'SIGTERM',
    onSpawn() {
      queueMicrotask(() => { if (typeof stop === 'function') stop(); });
    },
  });
  try {
    const { report } = await runCase({
      name: 'cancel-first-signal',
      steps: [{ kind: 'capture', name: 'hang', timeoutMs: 30000, requireFrames: false }],
    }, runOpts(tmp, world, {
      spawn,
      childKillGraceMs: 2000,
      registerStop(fn) { stop = fn; },
    }));
    assert.equal(report.status, 'aborted');
    assert.equal(report.cancelled.requested, true);
    assert.equal(report.cancelled.children, 1);
    assert.equal(report.cancelled.killed, 1);
    assert.ok(report.cancelled.waitedMs < 500, `first-signal exit waitedMs=${report.cancelled.waitedMs}`);
  } finally {
    tmp.cleanup();
  }
});

test('a failed admin restore prints NOT restored, is recorded, and is not complete', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  const orig = world.request.bind(world);
  world.request = async (side, op, args) => {
    if (op === 'set' && args.fields && args.fields.IsAdmin === false) {
      throw new Error('restore set failed');
    }
    return orig(side, op, args);
  };
  try {
    const { report } = await runCase({ name: 'restore-fail', steps: COMPLETE_STEPS }, runOpts(tmp, world, { lines }));
    assert.notEqual(report.status, 'complete');
    assert.ok(report.adminGranted);
    assert.equal(report.adminGranted.restoredAt, undefined);
    assert.match(String(report.adminGranted.restoreError), /restore set failed/);
    assert.ok(lines.some((l) => l.includes('admin: NOT restored')));
    assert.equal(world.users[0].admin, true);
  } finally {
    tmp.cleanup();
  }
});

test('an exception after the run directory exists still writes report.json with aborted and the error', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  world.throwOn('ping', 'ping exploded');
  try {
    const { report, runDir } = await runCase({ name: 'start-boom', steps: [] }, runOpts(tmp, world));
    assert.equal(report.status, 'aborted');
    assert.match(report.reason, /ping exploded/);
    const onDisk = JSON.parse(readFileSync(path.join(runDir, 'report.json'), 'utf8'));
    assert.equal(onDisk.status, 'aborted');
    assert.match(onDisk.reason, /ping exploded/);
  } finally {
    tmp.cleanup();
  }
});

test('--side client still grants, commands, and checks admin on the server user', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    clientPlayers: [{
      entity: '10:1',
      name: 'TestVampire',
      connected: true,
      userEntity: '20:1',
      position: { x: 0, y: 0, z: 0 },
    }],
    clientUsers: [{
      index: 1,
      name: 'TestVampire',
      connected: true,
      admin: false,
      entity: '20:1',
      character: '10:1',
      platformId: '1',
    }],
  });
  try {
    const { report } = await runCase({ name: 'side-client-admin', steps: COMPLETE_STEPS }, runOpts(tmp, world, { side: 'client' }));
    assert.equal(report.status, 'complete');
    const sets = world.calls.filter((c) => c.op === 'set');
    assert.ok(sets.length >= 2);
    assert.ok(sets.every((c) => c.side === 'server'));
    assert.ok(sets.every((c) => c.args.entity === world.users[0].entity));
    assert.ok(world.calls.filter((c) => c.op === 'command').every((c) => c.side === 'server'));
    assert.ok(world.calls.filter((c) => c.op === 'users').every((c) => c.side === 'server'));
    assert.ok(world.calls.filter((c) => c.op === 'players').every((c) => c.side === 'server'));
    assert.equal(world.users[0].admin, false);
    assert.ok(!sets.some((c) => c.args.entity === '20:1'));
    const adminCheck = report.finalChecks.find((c) => c.name === 'adminState');
    assert.equal(adminCheck.ok, true);
    assert.equal(adminCheck.value.expected, false);
  } finally {
    tmp.cleanup();
  }
});

test('active-ring-unload can complete only when the ring is still inside its lifetime at unload', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
  });
  installFakeSample(world, 'server');
  const { doc } = loadCase(path.join(TOOL_DIR, 'cases-catalog', 'active-ring-unload.json'));
  try {
    const { report } = await runCase(doc, runOpts(tmp, world));
    assert.equal(report.status, 'complete', JSON.stringify(report.steps.map((s) => ({ name: s.name, ok: s.ok, error: s.detail?.error })), null, 2));
    assert.ok(report.records.ringLog);
    assert.ok(report.records.ringLog.entity);
    assert.ok(report.records.ringUnloadCleanup);
    assert.match(report.records.ringUnloadCleanup.text, /FROSTDASH cleanup alive=1 destroyed=\[/);
    assert.match(report.records.ringUnloadCleanup.text, new RegExp(report.records.ringLog.entity.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    assert.ok(report.records.ringAfterUnload);
    assert.equal(report.records.ringAfterUnload.ageClock, 'log');
    assert.ok(report.records.ringAfterUnload.logAgeMs <= 1500);
    assert.ok(report.records.ringLog.logTime != null || report.records.ringAfterUnload.atLogTime != null);
    assert.equal(report.records.ringGoneAfterUnload.exists, false);
    assert.ok(world.calls.some((c) => c.op === 'command' && c.args.text === '.frostdash off'));
    assert.ok(report.teardown?.steps?.some((s) => /frostdash off/.test(s.name)));
  } finally {
    tmp.cleanup();
  }
});

test('active-ring-unload wall-clock removal wait does not fail ageClock log', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
  });
  installFakeSample(world, 'server');
  const { doc } = loadCase(path.join(TOOL_DIR, 'cases-catalog', 'active-ring-unload.json'));
  const clock = fakeClock();
  const orig = world.request.bind(world);
  world.request = async (side, op, args) => {
    if (op === 'reload') clock.set(clock.now() + 2000);
    return orig(side, op, args);
  };
  try {
    const { report } = await runCase(doc, runOpts(tmp, world, { clock }));
    assert.equal(report.status, 'complete', report.reason);
    assert.ok(report.records.ringAfterUnload.ageMs > 1500);
    assert.ok(report.records.ringAfterUnload.logAgeMs <= 1500);
  } finally {
    tmp.cleanup();
  }
});

test('active-ring-unload aborts when the Sample cleanup line is missing', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
  });
  installFakeSample(world, 'server');
  const origPush = world.pushLog.bind(world);
  world.pushLog = (side, text, extra = {}) => {
    if (/FROSTDASH cleanup/.test(text)) return { seq: 0, time: extra.time ?? '00:00:00.000', text, level: 'Info', source: 'Progenitor' };
    return origPush(side, text, extra);
  };
  const { doc } = loadCase(path.join(TOOL_DIR, 'cases-catalog', 'active-ring-unload.json'));
  try {
    const { report } = await runCase(doc, runOpts(tmp, world));
    assert.equal(report.status, 'aborted');
    const logStep = report.steps.find((s) => s.name === 'ringUnloadCleanup');
    assert.ok(logStep);
    assert.equal(logStep.ok, false);
    assert.match(String(logStep.detail.error), /no log line matching/);
    assert.ok(world.calls.some((c) => c.op === 'command' && c.args.text === '.frostdash off'));
  } finally {
    tmp.cleanup();
  }
});

test('active-ring-unload fails when log-time Start-to-unload exceeds maxAgeMs', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
  });
  installFakeSample(world, 'server');
  const origPush = world.pushLog.bind(world);
  world.pushLog = (side, text, extra = {}) => {
    if (/FROSTDASH edge=Start/.test(text)) extra = { ...extra, time: '09:32:23.000' };
    if (/Progenitor\.Sample\.dll removed/.test(text)) extra = { ...extra, time: '09:32:25.000' };
    return origPush(side, text, extra);
  };
  const { doc } = loadCase(path.join(TOOL_DIR, 'cases-catalog', 'active-ring-unload.json'));
  try {
    const { report } = await runCase(doc, runOpts(tmp, world));
    assert.notEqual(report.status, 'complete');
    const existsAfter = report.steps.find((s) => s.detail?.timestamps?.ageClock === 'log');
    assert.ok(existsAfter);
    assert.equal(existsAfter.ok, false);
    assert.match(String(existsAfter.detail.error), /older than 1500ms/);
    assert.equal(existsAfter.detail.timestamps.logAgeMs, 2000);
    assert.ok(world.calls.some((c) => c.op === 'command' && c.args.text === '.frostdash off'));
  } finally {
    tmp.cleanup();
  }
});

test('start-state protection is read after the admin grant and records the user, time, and Sample hash', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ sampleHash: DEFAULT_SAMPLE_HASH });
  try {
    assert.equal(world.users[0].admin, false);
    const { report } = await runCase({ name: 'baseline-order', steps: COMPLETE_STEPS }, runOpts(tmp, world));
    assert.equal(report.status, 'complete');
    assert.equal(report.startState.protection.rearmed, false);
    assert.equal(report.startState.protection.initial.ok, true);
    assert.equal(report.startState.protection.initial.value.flags, 7);
    assert.ok(!world.calls.some((c) => c.op === 'command' && c.args.text === '.debug on'));
    assert.equal(report.startState.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.images.server.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.startState.clientSampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.images.client.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.adminGranted.userName, 'TestVampire');
    assert.equal(report.adminGranted.userEntity, world.users[0].entity);
    assert.ok(report.adminGranted.at);
    const setIdx = world.calls.findIndex((c) => c.op === 'set' && c.args.fields?.IsAdmin === true);
    const statusIdx = world.calls.findIndex((c) => c.op === 'command' && c.args.text === '.debug status');
    assert.ok(setIdx >= 0, 'admin grant was not issued');
    assert.ok(statusIdx > setIdx, 'protection readback ran before the admin grant');
  } finally {
    tmp.cleanup();
  }
});

test('startState records client Sample hash from the client ping separately from the server hash', async () => {
  const tmp = tempDir();
  const clientHash = 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc';
  const world = createFakeWorld({
    sampleHash: DEFAULT_SAMPLE_HASH,
    clientSampleHash: clientHash,
  });
  try {
    const { report } = await runCase({ name: 'client-hash', steps: COMPLETE_STEPS }, runOpts(tmp, world));
    assert.equal(report.status, 'complete');
    assert.equal(report.images.server.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.startState.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.images.client.sampleHash, clientHash);
    assert.equal(report.startState.clientSampleHash, clientHash);
  } finally {
    tmp.cleanup();
  }
});

test('a Sample swap that drops the Core guard fails the step and aborts even if rearm succeeds', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
    dropGuardOnSwap: true,
    sampleHash: DEFAULT_SAMPLE_HASH,
  });
  installFakeSample(world, 'server');
  const dll = path.join(tmp.dir, 'swap.dll');
  writeFileSync(dll, 'swap-bytes');
  try {
    const { report } = await runCase({
      name: 'swap',
      steps: [
        { kind: 'swap', dll },
        { kind: 'protectionCheck', expect: true },
      ],
    }, runOpts(tmp, world, { dll }));
    assert.equal(report.status, 'aborted');
    assert.ok(report.findings.includes('protection dropped by swap'));
    assert.equal(report.steps.length, 1);
    assert.equal(report.steps[0].name, 'swap');
    assert.equal(report.steps[0].ok, false);
    assert.equal(report.steps[0].detail.error, 'protection dropped by swap');
    assert.equal(report.steps[0].detail.protectionAfter.value.flags, 7);
    assert.equal(report.steps[0].detail.protectionAfter.value.guard, false);
    assert.equal(report.steps[0].detail.protectionRearmed.ok, true);
    assert.equal(report.startState.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.steps[0].detail.sampleHashAfter, SWAP_SAMPLE_HASH);
    assert.equal(world.protectionGuard, true);
    assert.notEqual(report.status, 'complete');
    assert.equal(report.leftoverSummary.protectionOk, true);
    assert.equal(report.steps[0].detail.reconcile.ok, true);
    assert.equal(report.steps[0].detail.reconcile.skipped, true);
    assert.match(report.steps[0].detail.reconcile.reason, /protection is Core-owned/);
    assert.equal(report.steps[0].detail.protectionRearmed.ok, true);
  } finally {
    tmp.cleanup();
  }
});

test('swap.json performs three consecutive swaps', () => {
  const { doc } = loadCase(path.join(TOOL_DIR, 'cases', 'swap.json'));
  const swaps = (doc.steps ?? []).filter((s) => s.kind === 'swap');
  assert.equal(swaps.length, 3);
  assert.ok(swaps.every((s) => s.dll === '<path>'));
});

function fsNoopUtimes() {
  return new Proxy(fs, {
    get(target, prop, receiver) {
      if (prop === 'utimesSync') return () => {};
      const val = Reflect.get(target, prop, receiver);
      return typeof val === 'function' ? val.bind(target) : val;
    },
  });
}

test('three consecutive swaps stay complete with identity, hash, handshake and carriers recorded', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
    sampleHash: DEFAULT_SAMPLE_HASH,
  });
  installFakeSample(world, 'server');
  const dll = path.join(tmp.dir, 'swap.dll');
  writeFileSync(dll, 'swap-bytes');
  try {
    const { report } = await runCase({
      name: 'swap',
      steps: [
        { kind: 'swap', dll },
        { kind: 'protectionCheck', expect: true },
        { kind: 'swap', dll },
        { kind: 'protectionCheck', expect: true },
        { kind: 'swap', dll },
        { kind: 'protectionCheck', expect: true },
      ],
    }, runOpts(tmp, world, { dll }));
    assert.equal(report.status, 'complete');
    const swapSteps = report.steps.filter((s) => s.name === 'swap');
    assert.equal(swapSteps.length, 3);
    for (const step of swapSteps) {
      assert.equal(step.ok, true);
      assert.equal(step.detail.protectionAfter.ok, true);
      assert.equal(step.detail.playerIdentity.entity, report.startState.player.entity);
      assert.equal(step.detail.playerIdentity.characterChanged, false);
      assert.equal(step.detail.playerIdentity.connected, true);
      assert.equal(step.detail.handshake.ok, true);
      assert.match(String(step.detail.handshake.line?.text ?? ''), /Sample handshake:/);
      assert.equal(step.detail.sampleHashAfter, SWAP_SAMPLE_HASH);
      assert.ok(Array.isArray(step.detail.pingImages));
      assert.equal(step.detail.carriers.ok, true);
      assert.equal(step.detail.reconcile.ok, true);
    }
  } finally {
    tmp.cleanup();
  }
});

test('three swaps of the same file each bump destination LastWrite', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
    sampleHash: DEFAULT_SAMPLE_HASH,
  });
  const dest = installFakeSample(world, 'server');
  const dll = path.join(tmp.dir, 'swap.dll');
  copyFileSync(dest, dll);
  try {
    const { report } = await runCase({
      name: 'swap',
      steps: [
        { kind: 'swap', dll },
        { kind: 'swap', dll },
        { kind: 'swap', dll },
      ],
    }, runOpts(tmp, world, { dll }));
    assert.equal(report.status, 'complete', report.reason);
    const writes = report.steps.filter((s) => s.name === 'swap').map((s) => s.detail.destLastWrite);
    assert.equal(writes.length, 3);
    assert.ok(writes.every((w) => Number.isFinite(w)));
    assert.ok(writes[0] < writes[1], `${writes[0]} < ${writes[1]}`);
    assert.ok(writes[1] < writes[2], `${writes[1]} < ${writes[2]}`);
    assert.equal(statSync(dest).mtimeMs, writes[2]);
  } finally {
    tmp.cleanup();
  }
});

const CLIENT_HANDSHAKE =
  'Sample handshake: declared 4 row(s) on the client side costing 2 of 16 slot(s); every one is weaker than this plugin\'s own ServerMustHave/Exact row, so no new join can be refused because of them. `.hs` prints them.';

function attachSwapClientWorld(world) {
  const orig = world.request.bind(world);
  world.users[0].handshake = 'ok';
  const pendingClient = [];
  world.request = async (side, op, args = {}) => {
    if (op === 'logs' && side === 'client' && pendingClient.length) {
      for (const text of pendingClient.splice(0)) {
        world.pushLog('client', text, { source: 'Progenitor Sample' });
      }
    }
    const result = await orig(side, op, args);
    if (op === 'command' && typeof args.text === 'string') {
      const text = args.text;
      if (text === '.catalogex warm') {
        world.pushLog('server', 'CATALOGEX warm');
      } else if (text === '.catalogex borrow') {
        pendingClient.push('CATALOGEX borrow enabled=True');
      } else if (text === '.catalogex status') {
        pendingClient.push('CATALOGEX status handle=charm1 enabled=True');
      } else if (text === '.catalogex dispose') {
        pendingClient.push('CATALOGEX dispose');
        world.pushLog('server', 'catalogex server dispose');
      } else if (text === '.froststyle off') {
        world.pushLog('server', 'FROSTSTYLE enabled=False restored=0', { source: 'Progenitor Sample' });
        pendingClient.push('FROSTSTYLE client enabled=False');
      } else if (text === '.froststyle on') {
        world.pushLog('server', 'FROSTSTYLE enabled=True restored=0', { source: 'Progenitor Sample' });
        pendingClient.push('FROSTSTYLE client enabled=True');
      }
    }
    if (op === 'reload' && side === 'client' && world.sampleLoaded) {
      world.pushLog('client', CLIENT_HANDSHAKE, { source: 'Progenitor Sample' });
      world.pushLog('client', "Effects: disposed Particle 'Part_Charm_Loop_01'", { source: 'Progenitor' });
    }
    return result;
  };
}

test('swap-client.json completes three client swaps in the fake world', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
    sampleHash: DEFAULT_SAMPLE_HASH,
    clientSampleHash: DEFAULT_SAMPLE_HASH,
  });
  installFakeSample(world, 'client');
  attachSwapClientWorld(world);
  const dll = path.join(tmp.dir, 'swap.dll');
  writeFileSync(dll, 'swap-bytes');
  const { doc, file } = loadCase(path.join(TOOL_DIR, 'cases', 'swap-client.json'));
  try {
    for (const step of [...(doc.steps ?? []), ...(doc.teardown ?? [])]) {
      assert.ok(STEP_KINDS.includes(step.kind), `${file}: unknown step kind ${step.kind}`);
    }
    const { report } = await runCase(doc, runOpts(tmp, world, { dll, side: 'client' }));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.startState.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.startState.clientSampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.images.client.sampleHash, DEFAULT_SAMPLE_HASH);
    const swapSteps = report.steps.filter((s) => s.detail?.op === 'swap' || /^client swap /.test(s.name));
    assert.equal(swapSteps.length, 3);
    for (const step of swapSteps) {
      assert.equal(step.ok, true, step.detail?.error);
      assert.equal(step.detail.op, 'swap');
      assert.equal(step.detail.reconcile.skipped, true);
      assert.equal(step.detail.clientReady.ok, true);
      assert.match(String(step.detail.clientReady.line?.text ?? ''), /on the client side/);
      assert.equal(step.detail.sampleHashAfter, SWAP_SAMPLE_HASH);
      assert.equal(step.detail.playerIdentity.connected, true);
      assert.equal(step.detail.playerIdentity.handshake, 'ok');
      assert.equal(step.detail.protectionAfter.ok, true);
    }
    const froststyleClient = report.steps.filter((s) => /froststyle client /.test(s.name));
    assert.equal(froststyleClient.length, 6);
    for (const step of froststyleClient) {
      assert.equal(step.ok, true, step.detail?.error);
      assert.equal(step.detail.count, 1);
    }
    assert.equal(world.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(world.clientSampleHash, SWAP_SAMPLE_HASH);
  } finally {
    tmp.cleanup();
  }
});

test('swap-step hostiles.findings is a per-step snapshot, not the run findings array', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
    sampleHash: DEFAULT_SAMPLE_HASH,
  });
  installFakeSample(world, 'server');
  const dll = path.join(tmp.dir, 'swap.dll');
  writeFileSync(dll, 'swap-bytes');
  const orig = world.request.bind(world);
  let reloads = 0;
  world.request = async (side, op, args = {}) => {
    const result = await orig(side, op, args);
    if (op === 'reload') {
      reloads += 1;
      if (reloads === 1) world.addCarrier('client', { entity: '99503:37', prefab: 'NetworkedSequence' });
      if (reloads === 2) world.addCarrier('client', { entity: '99471:87', prefab: 'NetworkedSequence' });
    }
    return result;
  };
  try {
    const { report } = await runCase({
      name: 'swap',
      steps: [
        { kind: 'swap', dll },
        { kind: 'swap', dll },
      ],
    }, runOpts(tmp, world, { dll }));
    assert.equal(report.status, 'complete', report.reason);
    const swapSteps = report.steps.filter((s) => s.detail?.op === 'swap');
    assert.equal(swapSteps.length, 2);
    const firstFindings = swapSteps[0].detail.hostiles.findings;
    const secondFindings = swapSteps[1].detail.hostiles.findings;
    assert.ok(Array.isArray(firstFindings));
    assert.ok(Array.isArray(secondFindings));
    assert.notEqual(firstFindings, report.findings);
    assert.notEqual(secondFindings, report.findings);
    assert.notEqual(firstFindings, secondFindings);
    const later = report.findings.find((f) => /client carrier outlived/.test(String(f)));
    assert.ok(later, `expected cleanup finding, got ${JSON.stringify(report.findings)}`);
    assert.match(later, /99471:87/);
    assert.ok(!firstFindings.includes(later), `swap 1 hostiles.findings leaked later finding: ${JSON.stringify(firstFindings)}`);
    assert.ok(!secondFindings.includes(later), `swap 2 hostiles.findings leaked later finding: ${JSON.stringify(secondFindings)}`);
    report.findings.push('finding added later');
    assert.ok(!firstFindings.includes('finding added later'));
    assert.ok(!secondFindings.includes('finding added later'));
  } finally {
    tmp.cleanup();
  }
});

test('unchanged LastWrite fails the swap step fast instead of a 20s timeout', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
  });
  const dest = installFakeSample(world, 'server');
  const dll = path.join(tmp.dir, 'swap.dll');
  copyFileSync(dest, dll);
  const clock = fakeClock();
  try {
    const { report } = await runCase({
      name: 'swap',
      steps: [{ kind: 'swap', dll }],
    }, runOpts(tmp, world, { dll, fs: fsNoopUtimes(), clock, reloadLogTimeoutMs: 20000 }));
    assert.equal(report.status, 'aborted');
    assert.match(String(report.reason), /LastWrite unchanged:/);
    assert.ok(!/timed out waiting for reload log lines/.test(String(report.reason)));
    assert.equal(world.calls.filter((c) => c.op === 'reload').length, 0);
    assert.ok(clock.now() < 20000);
    assert.equal(report.steps[0].ok, false);
    assert.equal(report.steps[0].detail.destLastWrite, report.steps[0].detail.previousLastWrite);
  } finally {
    tmp.cleanup();
  }
});

test('a server Sample swap passes without a reconcile wait or a manual rearm', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
    sampleHash: DEFAULT_SAMPLE_HASH,
  });
  installFakeSample(world, 'server');
  const dll = path.join(tmp.dir, 'swap.dll');
  writeFileSync(dll, 'swap-bytes');
  try {
    const { report } = await runCase({
      name: 'swap',
      steps: [
        { kind: 'swap', dll },
        { kind: 'protectionCheck', expect: true },
      ],
    }, runOpts(tmp, world, { dll, reloadLogTimeoutMs: 2000 }));
    assert.equal(report.status, 'complete');
    assert.equal(report.steps[0].ok, true);
    assert.equal(report.steps[0].detail.protectionAfter.ok, true);
    assert.equal(report.steps[0].detail.protectionRearmed, undefined);
    assert.equal(report.steps[0].detail.reconcile.skipped, true);
    assert.equal(report.steps[0].detail.reconcile.reason, 'protection is Core-owned; a Sample swap does not reconcile');
    assert.ok(!world.calls.some((c) => c.op === 'logs' && /world-ready/.test(String(c.args?.filter ?? ''))));
    assert.ok(!world.calls.some((c) => c.op === 'command' && c.args?.text === '.debug on'));
    assert.ok(!report.findings.includes('protection dropped by swap'));
  } finally {
    tmp.cleanup();
  }
});

test('Sample removal leaves Core protection intact and reinstall does not rearm an ok readback', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    serverReload: path.join(tmp.dir, 'reload-server'),
    clientReload: path.join(tmp.dir, 'reload-client'),
    sampleHash: DEFAULT_SAMPLE_HASH,
  });
  installFakeSample(world, 'server');
  try {
    const { report } = await runCase({
      name: 'removal-reinstall',
      steps: [
        { kind: 'removal' },
        { kind: 'protectionCheck', expect: true },
        { kind: 'reinstall' },
        { kind: 'protectionCheck', expect: true },
      ],
    }, runOpts(tmp, world, { reloadLogTimeoutMs: 2000 }));
    assert.equal(report.status, 'complete');
    const [removal, afterRemoval, reinstall, afterReinstall] = report.steps;
    assert.equal(removal.ok, true);
    assert.equal(removal.detail.protectionAfter.ok, true);
    assert.equal(afterRemoval.ok, true);
    assert.equal(reinstall.ok, true);
    assert.equal(reinstall.detail.protectionAfter.ok, true);
    assert.equal(reinstall.detail.protectionAfter.rearmed, false);
    assert.equal(afterReinstall.ok, true);
    assert.ok(!world.calls.some((c) => c.op === 'command' && c.args?.text === '.debug on'));
  } finally {
    tmp.cleanup();
  }
});

test('--force-teardown only destroys this run skipCleanup entities; cleanup <runDir> destroys previous leftovers', async () => {
  const world = createFakeWorld();
  const tmp1 = tempDir();
  const tmp2 = tempDir();
  const tmp3 = tempDir();
  const timeoutSteps = [
    { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, track: true },
    { kind: 'skipCleanupFor', name: 'rat' },
  ];
  try {
    const first = await runCase({ name: 'cleanup-timeout', steps: timeoutSteps }, runOpts(tmp1, world));
    assert.equal(first.report.status, 'incomplete');
    const leftover = first.report.cleanup.remaining[0].entity;
    assert.equal(world.entities.get(leftover).exists, true);

    const second = await runCase(
      { name: 'cleanup-timeout', steps: timeoutSteps },
      runOpts(tmp2, world, { forceTeardown: true }),
    );
    assert.equal(second.report.teardown.ok, true);
    assert.deepEqual(second.report.teardown.remaining, []);
    const secondId = second.report.cleanup.remaining[0].entity;
    assert.notEqual(secondId, leftover);
    assert.equal(world.entities.get(leftover).exists, true);
    assert.equal(world.entities.get(secondId).exists, false);

    const cleaned = await cleanupLeftovers(first.runDir, runOpts(tmp3, world));
    assert.equal(cleaned.report.status, 'complete');
    assert.deepEqual(cleaned.report.cleanup.remaining, []);
    assert.deepEqual(cleaned.report.cleanup.unconfirmed, []);
    assert.equal(world.entities.get(leftover).exists, false);
    assert.equal(cleaned.report.leftoverSummary.trackedRemaining, 0);
    assert.equal(cleaned.report.leftoverSummary.unconfirmed, 0);
  } finally {
    tmp1.cleanup();
    tmp2.cleanup();
    tmp3.cleanup();
  }
});

test('abort after a confirmed-absent hostile does not list it as remaining', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const orig = world.request.bind(world);
  let destroyed = false;
  world.request = async (side, op, args) => {
    if (op === 'entity' && args.action === 'destroy') destroyed = true;
    if (destroyed && op === 'entity' && args.action === 'exists') {
      throw new Error('connect ECONNREFUSED 127.0.0.1:27342');
    }
    return orig(side, op, args);
  };
  try {
    const { report } = await runCase({
      name: 'abort-gone-hostile',
      steps: [
        { kind: 'spawn', name: 'bandit', prefab: 'CHAR_Bandit_Thief', offset: { x: 4, y: 0, z: 0 }, hostile: true, track: true },
        { kind: 'destroyTracked', name: 'bandit' },
      ],
    }, runOpts(tmp, world));
    assert.notEqual(report.status, 'complete');
    const remainingIds = (report.cleanup.remaining ?? []).map((e) => e.entity);
    assert.ok(!remainingIds.includes(report.steps[0].detail.tracked[0]));
    assert.ok((report.cleanup.unconfirmed ?? []).length >= 1);
    assert.match(String(report.cleanup.unconfirmed[0].error), /ECONNREFUSED/);
  } finally {
    tmp.cleanup();
  }
});

function sessionForFakeWorld(nowMs = 0, extra = {}) {
  return {
    serverStartedUtc: new Date(nowMs - 1000).toISOString(),
    sampleHash: extra.sampleHash ?? DEFAULT_SAMPLE_HASH,
    generation: extra.generation ?? 1,
  };
}

function writePrevReport(dir, report) {
  writeFileSync(path.join(dir, 'report.json'), JSON.stringify(report, null, 2), 'utf8');
}

test('leftoverEntitiesFromReport adopts remaining and unconfirmed from cleanup and teardown', () => {
  const report = {
    cleanup: {
      remaining: [{ entity: '1:1', prefab: 'CHAR_Critter_Rat' }],
      unconfirmed: [{ entity: '2:1', prefab: 'CHAR_Bandit_Thief', error: 'timed out' }],
    },
    teardown: {
      remaining: [{ entity: '3:1', prefab: 'CHAR_Critter_Rat' }],
      unconfirmed: [{ entity: '2:1', prefab: 'CHAR_Bandit_Thief', error: 'dup' }],
    },
  };
  const rows = leftoverEntitiesFromReport(report);
  assert.deepEqual(rows.map((r) => r.entity), ['1:1', '2:1', '3:1']);
  assert.equal(sourceReportListedLeftoverCount(report), 4);
  assert.equal(sourceReportListedLeftoverCount({ cleanup: { remaining: [], unconfirmed: [] } }), 0);
});

test('leftoverEntitiesFromReport includes leftover client carriers', () => {
  const report = {
    cleanup: {
      remaining: [],
      unconfirmed: [],
      clientCarriers: { destroyed: [], remaining: ['77:1'] },
    },
    finalChecks: [{
      name: 'carriers',
      ok: false,
      value: {
        leftover: {
          client: [{ entity: '77:1', prefab: { name: 'NotASequence' } }, { entity: '99448:20', prefab: { name: 'NetworkedSequence' } }],
          server: [],
        },
      },
    }],
  };
  const rows = leftoverEntitiesFromReport(report);
  assert.ok(rows.some((r) => r.entity === '77:1' && r.side === 'client' && r.prefab === 'NotASequence'));
  assert.ok(rows.some((r) => r.entity === '99448:20' && r.side === 'client' && r.prefab === 'NetworkedSequence'));
  assert.ok(sourceReportListedLeftoverCount(report) >= 2);
});

test('sessionPinsMatch uses generation, sampleHash, and uptime-derived start within 5s', () => {
  const ping = { uptimeSeconds: 10, sampleHash: 'abc', reload: { generation: 3 } };
  const pin = sessionPinFromPing(ping, 20_000);
  assert.equal(pin.generation, 3);
  assert.equal(pin.sampleHash, 'abc');
  assert.equal(pin.serverStartedUtc, new Date(10_000).toISOString());
  assert.equal(SESSION_START_TOLERANCE_MS, 5000);
  assert.equal(sessionPinsMatch(pin, sessionPinFromPing(ping, 24_000)), true);
  assert.equal(sessionPinsMatch(pin, sessionPinFromPing(ping, 26_000)), false);
  assert.equal(sessionPinsMatch(pin, sessionPinFromPing({ ...ping, reload: { generation: 4 } }, 20_000)), false);
  assert.equal(sessionPinsMatch(pin, sessionPinFromPing({ ...ping, sampleHash: 'def' }, 20_000)), false);
  assert.equal(sessionPinsMatch(null, pin), false);
});

test('prefabsMatch compares leftover recorded prefab to exists readback', () => {
  assert.equal(prefabsMatch('CHAR_Critter_Rat', { hash: 0, name: 'CHAR_Critter_Rat' }), true);
  assert.equal(prefabsMatch('CHAR_Critter_Rat', { hash: 0, name: 'CHAR_Bandit_Thief' }), false);
  assert.equal(prefabsMatch('CHAR_Critter_Rat', null), false);
  assert.equal(prefabsMatch(null, null), true);
});

test('cleanup of a report with no remaining and no unconfirmed is nothing to clean', async () => {
  const tmpPrev = tempDir();
  const tmpOut = tempDir();
  const world = createFakeWorld();
  try {
    writePrevReport(tmpPrev.dir, {
      status: 'complete',
      session: sessionForFakeWorld(),
      cleanup: { remaining: [], unconfirmed: [] },
    });
    const cleaned = await cleanupLeftovers(tmpPrev.dir, runOpts(tmpOut, world));
    assert.equal(cleaned.report.status, 'complete');
    assert.equal(cleaned.report.reason, 'nothing to clean');
    assert.deepEqual(cleaned.report.cleanup.remaining, []);
    assert.deepEqual(cleaned.report.cleanup.unconfirmed, []);
  } finally {
    tmpPrev.cleanup();
    tmpOut.cleanup();
  }
});

test('cleanup <runDir> adopts unconfirmed leftovers and is not complete while that id still exists', async () => {
  const tmp1 = tempDir();
  const tmp2 = tempDir();
  const world = createFakeWorld();
  const orig = world.request.bind(world);
  world.request = async (side, op, args) => {
    if (op === 'entity') {
      throw new Error("server bridge timed out on 'entity' after 30 ms");
    }
    return orig(side, op, args);
  };
  try {
    const first = await runCase({
      name: 'exists-timeout',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, track: true },
      ],
    }, runOpts(tmp1, world));
    assert.equal(first.report.cleanup.remaining.length, 0);
    assert.ok(first.report.cleanup.unconfirmed.length >= 1);
    const leftover = first.report.cleanup.unconfirmed[0].entity;
    assert.equal(world.entities.get(leftover).exists, true);
    world.sticky.add(leftover);
    world.request = orig;

    const cleaned = await cleanupLeftovers(first.runDir, runOpts(tmp2, world));
    assert.notEqual(cleaned.report.status, 'complete');
    assert.notEqual(cleaned.report.reason, 'nothing to clean');
    assert.notEqual(cleaned.report.reason, 'no leftovers listed');
    assert.equal(world.entities.get(leftover).exists, true);
    assert.ok(
      (cleaned.report.cleanup.remaining ?? []).some((e) => e.entity === leftover)
      || (cleaned.report.cleanup.unconfirmed ?? []).some((e) => e.entity === leftover),
    );
  } finally {
    tmp1.cleanup();
    tmp2.cleanup();
  }
});

test('cleanup <runDir> destroys an unconfirmed leftover once exists queries work again', async () => {
  const tmp1 = tempDir();
  const tmp2 = tempDir();
  const world = createFakeWorld();
  const orig = world.request.bind(world);
  world.request = async (side, op, args) => {
    if (op === 'entity') {
      throw new Error("server bridge timed out on 'entity' after 30 ms");
    }
    return orig(side, op, args);
  };
  try {
    const first = await runCase({
      name: 'exists-timeout',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, track: true },
      ],
    }, runOpts(tmp1, world));
    const leftover = first.report.cleanup.unconfirmed[0].entity;
    world.request = orig;
    const cleaned = await cleanupLeftovers(first.runDir, runOpts(tmp2, world));
    assert.equal(cleaned.report.status, 'complete');
    assert.equal(world.entities.get(leftover).exists, false);
    assert.deepEqual(cleaned.report.cleanup.remaining, []);
    assert.deepEqual(cleaned.report.cleanup.unconfirmed, []);
  } finally {
    tmp1.cleanup();
    tmp2.cleanup();
  }
});

test('cleanup refuses to destroy when the session pin does not match the current ping', async () => {
  const tmpPrev = tempDir();
  const tmpOut = tempDir();
  const world = createFakeWorld();
  const leftover = '100:1';
  try {
    world.entities.set(leftover, { exists: true, prefab: 'CHAR_Critter_Rat' });
    writePrevReport(tmpPrev.dir, {
      status: 'incomplete',
      session: sessionForFakeWorld(0, { generation: 99 }),
      cleanup: {
        remaining: [{ name: 'rat', entity: leftover, prefab: 'CHAR_Critter_Rat', hostile: false }],
        unconfirmed: [],
      },
    });
    const cleaned = await cleanupLeftovers(tmpPrev.dir, runOpts(tmpOut, world));
    assert.equal(cleaned.report.status, 'incomplete');
    assert.equal(cleaned.report.reason, 'session pin mismatch');
    assert.equal(world.entities.get(leftover).exists, true);
    assert.ok(cleaned.report.refusals.some((r) => r.reason === 'session pin mismatch'));
    assert.ok(!world.calls.some((c) => c.op === 'entity' && c.args.action === 'destroy'));
  } finally {
    tmpPrev.cleanup();
    tmpOut.cleanup();
  }
});

test('cleanup refuses to destroy when the live prefab differs from the leftover record', async () => {
  const tmpPrev = tempDir();
  const tmpOut = tempDir();
  const world = createFakeWorld();
  const leftover = '100:1';
  try {
    world.entities.set(leftover, { exists: true, prefab: 'CHAR_Bandit_Thief' });
    writePrevReport(tmpPrev.dir, {
      status: 'incomplete',
      session: sessionForFakeWorld(),
      cleanup: {
        remaining: [{ name: 'rat', entity: leftover, prefab: 'CHAR_Critter_Rat', hostile: false }],
        unconfirmed: [],
      },
    });
    const cleaned = await cleanupLeftovers(tmpPrev.dir, runOpts(tmpOut, world));
    assert.notEqual(cleaned.report.status, 'complete');
    assert.equal(world.entities.get(leftover).exists, true);
    assert.ok(cleaned.report.refusals.some((r) => r.entity === leftover && r.reason === 'prefab mismatch'));
    assert.ok(!world.calls.some((c) => c.op === 'entity' && c.args.action === 'destroy'));
  } finally {
    tmpPrev.cleanup();
    tmpOut.cleanup();
  }
});

test('start-state protection rearms with .debug on when the initial readback is not ok', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ protectionGuard: false });
  try {
    const { report } = await runCase({ name: 'rearm-ok', steps: COMPLETE_STEPS }, runOpts(tmp, world));
    assert.equal(report.status, 'complete');
    assert.equal(report.startState.protection.rearmed, true);
    assert.equal(report.startState.protection.initial.ok, false);
    assert.equal(report.startState.protection.after.ok, true);
    assert.equal(report.startState.protection.after.value.guard, true);
    const ops = world.calls.filter((c) => c.op === 'command' || c.op === 'spawn' || c.op === 'set');
    const statusIdx = ops.findIndex((c) => c.op === 'command' && c.args.text === '.debug status');
    const onIdx = ops.findIndex((c) => c.op === 'command' && c.args.text === '.debug on');
    const spawnIdx = ops.findIndex((c) => c.op === 'spawn');
    const statusAfter = ops.findIndex((c, i) => i > onIdx && c.op === 'command' && c.args.text === '.debug status');
    assert.ok(statusIdx >= 0 && onIdx > statusIdx, 'rearm did not follow the initial status readback');
    assert.ok(statusAfter > onIdx, 'flags= was not re-read after .debug on');
    assert.ok(spawnIdx > statusAfter, 'a step ran before protection was armed');
    assert.equal(world.protectionGuard, true);
  } finally {
    tmp.cleanup();
  }
});

test('rearm failure aborts before any step and does not spawn', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ protectionGuard: false, rearmFails: true });
  const lines = [];
  try {
    const { report, runDir } = await runCase({
      name: 'rearm-fail',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, track: true },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'aborted');
    assert.equal(report.reason, 'protection could not be armed');
    assert.deepEqual(report.steps, []);
    assert.equal(report.startState.protection.rearmed, true);
    assert.equal(report.startState.protection.initial.ok, false);
    assert.equal(report.startState.protection.after.ok, false);
    assert.ok(!world.calls.some((c) => c.op === 'spawn'));
    const trace = readFileSync(path.join(runDir, 'trace.jsonl'), 'utf8');
    assert.ok(!/"op":"spawn"/.test(trace), `spawn appeared in the trace:\n${trace}`);
    assert.ok(lines.some((l) => /protection could not be armed/.test(l)));
  } finally {
    tmp.cleanup();
  }
});

test('adopt of the player character aborts without an entity request', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'adopt-player',
      steps: [{ kind: 'adopt', entity: '1:1', name: 'player', hostile: false }],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'aborted');
    assert.match(String(report.reason), /player or user entity/);
    assert.ok(!world.calls.some((c) => c.op === 'entity'));
    assert.equal(world.entities.get('1:1').exists, true);
  } finally {
    tmp.cleanup();
  }
});

test('adopt of an existing leftover then cleanup is complete', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  const leftover = '331934:1';
  try {
    world.entities.set(leftover, { exists: true, prefab: 'CHAR_Critter_Rat' });
    const { report } = await runCase({
      name: 'adopt-leftover',
      steps: [
        { kind: 'adopt', entity: leftover, name: 'rat', hostile: false, prefab: 'CHAR_Critter_Rat' },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete');
    assert.equal(report.steps[0].ok, true);
    assert.equal(report.steps[0].detail.entity, leftover);
    assert.equal(world.entities.get(leftover).exists, false);
    assert.deepEqual(report.cleanup.remaining, []);
    assert.equal(report.leftoverSummary.trackedRemaining, 0);
  } finally {
    tmp.cleanup();
  }
});

test('a complete run records the session pin from the server ping', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({ name: 'session-pin', steps: COMPLETE_STEPS }, runOpts(tmp, world));
    assert.equal(report.status, 'complete');
    assert.ok(report.session);
    assert.equal(report.session.generation, 1);
    assert.equal(report.session.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.ok(report.session.serverStartedUtc);
  } finally {
    tmp.cleanup();
  }
});

test('cleanup --entity writes a report and is complete only when the entity is gone', async () => {
  const leftover = '331934:1';
  const tmpOk = tempDir();
  const tmpSticky = tempDir();
  const worldOk = createFakeWorld();
  const worldSticky = createFakeWorld();
  try {
    worldOk.entities.set(leftover, { exists: true, prefab: 'CHAR_Critter_Rat' });
    const cleaned = await cleanupEntity(leftover, runOpts(tmpOk, worldOk, { prefab: 'CHAR_Critter_Rat' }));
    assert.equal(cleaned.report.status, 'complete');
    assert.equal(worldOk.entities.get(leftover).exists, false);
    assert.ok(existsSync(path.join(cleaned.runDir, 'report.json')));
    assert.equal(JSON.parse(readFileSync(path.join(cleaned.runDir, 'report.json'), 'utf8')).status, 'complete');
    assert.equal(cleaned.report.session.generation, 1);
    assert.deepEqual(cleaned.report.cleanup.remaining, []);
    assert.deepEqual(cleaned.report.cleanup.unconfirmed, []);

    worldSticky.entities.set(leftover, { exists: true, prefab: 'CHAR_Critter_Rat' });
    worldSticky.sticky.add(leftover);
    const stuck = await cleanupEntity(leftover, runOpts(tmpSticky, worldSticky, { prefab: 'CHAR_Critter_Rat' }));
    assert.notEqual(stuck.report.status, 'complete');
    assert.equal(worldSticky.entities.get(leftover).exists, true);
    assert.ok(existsSync(path.join(stuck.runDir, 'report.json')));
    assert.ok(
      (stuck.report.cleanup.remaining ?? []).some((e) => e.entity === leftover)
      || (stuck.report.cleanup.unconfirmed ?? []).some((e) => e.entity === leftover),
    );
  } finally {
    tmpOk.cleanup();
    tmpSticky.cleanup();
  }
});

test('cleanup --entity refuses a missing entity and a prefab mismatch', async () => {
  const tmpMissing = tempDir();
  const tmpMismatch = tempDir();
  const world = createFakeWorld();
  const leftover = '331934:1';
  try {
    const missing = await cleanupEntity('999:1', runOpts(tmpMissing, world));
    assert.notEqual(missing.report.status, 'complete');
    assert.equal(missing.report.reason, 'entity does not exist');
    assert.ok(existsSync(path.join(missing.runDir, 'report.json')));

    world.entities.set(leftover, { exists: true, prefab: 'CHAR_Bandit_Thief' });
    const mismatch = await cleanupEntity(leftover, runOpts(tmpMismatch, world, { prefab: 'CHAR_Critter_Rat' }));
    assert.notEqual(mismatch.report.status, 'complete');
    assert.equal(mismatch.report.reason, 'prefab mismatch');
    assert.equal(world.entities.get(leftover).exists, true);
    assert.ok(!world.calls.some((c) => c.op === 'entity' && c.args.action === 'destroy'));
  } finally {
    tmpMissing.cleanup();
    tmpMismatch.cleanup();
  }
});

test('cleanup --entity refuses a player character without an entity request', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const cleaned = await cleanupEntity('1:1', runOpts(tmp, world));
    assert.notEqual(cleaned.report.status, 'complete');
    assert.match(cleaned.report.reason, /player or user/);
    assert.ok(!world.calls.some((c) => c.op === 'entity'));
    assert.equal(world.entities.get('1:1').exists, true);
    assert.ok(existsSync(path.join(cleaned.runDir, 'report.json')));
  } finally {
    tmp.cleanup();
  }
});

test('a destroy records timestamps, buffs and both AttachedBuffer readbacks', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'cleanup-ok',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
      ],
    }, runOpts(tmp, world, { attachReadback: true }));
    assert.equal(report.status, 'complete');
    const rec = (report.cleanup.entities ?? []).find((e) => e.name === 'rat')
      ?? (report.cleanup.entities ?? [])[0];
    assert.ok(rec, `expected cleanup.entities, got ${JSON.stringify(report.cleanup.entities)}`);
    assert.equal(rec.phase, 'tagged');
    assert.ok(Array.isArray(rec.buffs));
    assert.ok(rec.buffs.length >= 1, `buffs ${JSON.stringify(rec.buffs)}`);
    assert.equal(rec.finalizePolls, 2);
    assert.equal(typeof rec.absenceLatencyMs, 'number');
    assert.equal(typeof rec.buffsAt, 'string');
    assert.equal(typeof rec.taggedAt, 'string');
    assert.ok(rec.attachedBuffer?.before, 'before readback missing from report');
    assert.ok(rec.attachedBuffer?.atTagged, 'atTagged readback missing from report');
    assert.ok(world.calls.some((c) => c.op === 'entity' && c.args.action === 'finalize'));
    assert.ok(world.calls.some((c) => c.op === 'inspect'));
  } finally {
    tmp.cleanup();
  }
});

test('buffs that never vanish make the run incomplete with unconfirmed leftover', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ keepBuffPresent: true });
  try {
    const { report } = await runCase({
      name: 'buffs-linger',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
      ],
    }, runOpts(tmp, world, { finalizeTimeoutMs: 500, finalizePollMs: 250 }));
    assert.equal(report.status, 'incomplete');
    assert.equal(report.cleanup.ok, false);
    assert.equal(report.cleanup.remaining.length, 0);
    assert.ok(report.cleanup.unconfirmed.length >= 1);
    assert.match(String(report.cleanup.unconfirmed[0].error), /stuck in waiting-buffs:/);
    assert.equal(report.leftoverSummary.unconfirmed, report.cleanup.unconfirmed.length);
    assert.ok(world.calls.some((c) => c.op === 'entity' && c.args.action === 'finalize'));
  } finally {
    tmp.cleanup();
  }
});

test('an older entity op without phase is unconfirmed with a contract-mismatch finding', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ omitDestroyPhase: true });
  try {
    const { report } = await runCase({
      name: 'legacy-destroy',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'incomplete');
    assert.ok(report.findings.includes('entity op contract mismatch: no phase'));
    assert.ok(!world.calls.some((c) => c.op === 'entity' && c.args.action === 'finalize'));
    assert.ok((report.cleanup.unconfirmed ?? []).length >= 1);
    assert.equal(world.entities.get(report.cleanup.unconfirmed[0].entity)?.exists, true);
  } finally {
    tmp.cleanup();
  }
});

test('older entity op with phase detach is unconfirmed with a contract-mismatch finding', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ oldDetachPhase: true });
  try {
    const { report } = await runCase({
      name: 'legacy-detach',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'incomplete');
    assert.ok(report.findings.includes('entity op contract mismatch: detach'));
    assert.ok(!world.calls.some((c) => c.op === 'entity' && c.args.action === 'finalize'));
    assert.ok((report.cleanup.unconfirmed ?? []).length >= 1);
  } finally {
    tmp.cleanup();
  }
});

test('fatal log signal makes the run incomplete and names the line', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    fatalLog: 'Tried to detach entity 1000900:1 from AttachedBuffer',
  });
  try {
    const { report } = await runCase({
      name: 'fatal-detach',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'incomplete');
    assert.match(String(report.reason), /Tried to detach/);
    assert.ok((report.logSignals?.fatal?.triedToDetachAttachedBuffer ?? 0) >= 1);
    assert.ok((report.expectedDetachLogs?.destroyedWithoutBeingDetached ?? 0) >= 1);
  } finally {
    tmp.cleanup();
  }
});

test('expected detach lines still complete and record the count', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'cleanup-ok',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete');
    assert.ok(report.expectedDetachLogs.destroyedWithoutBeingDetached >= 1);
    assert.ok(report.expectedDetachLogs.detachEntityInformation >= 1);
    assert.equal(report.logSignals.fatal.triedToDetachAttachedBuffer, 0);
    assert.equal(report.logSignals.fatal.pointsAtBuffTarget, 0);
    assert.equal(report.logSignals.fatal.burstException, 0);
    assert.equal(report.logSignals.fatal.bridgeUnanswered, 0);
  } finally {
    tmp.cleanup();
  }
});

test('leftover cleanup still reads a legacy report that has framework and no frameworkSides', async () => {
  const tmpPrev = tempDir();
  const tmpOut = tempDir();
  const world = createFakeWorld();
  const leftover = '100:1';
  const legacyFramework = `generation:1; core:null; dev:null; sample:${DEFAULT_SAMPLE_HASH}`;
  try {
    world.entities.set(leftover, { exists: true, prefab: 'CHAR_Critter_Rat' });
    const legacy = {
      status: 'incomplete',
      session: sessionForFakeWorld(),
      framework: legacyFramework,
      stampErrors: [],
      images: {
        server: { side: 'server', generation: 1, sampleHash: DEFAULT_SAMPLE_HASH },
        client: { side: 'client', generation: 1, sampleHash: DEFAULT_SAMPLE_HASH },
      },
      cleanup: {
        remaining: [{ name: 'rat', entity: leftover, prefab: 'CHAR_Critter_Rat', hostile: false }],
        unconfirmed: [],
      },
    };
    assert.equal(legacy.frameworkSides, undefined);
    writePrevReport(tmpPrev.dir, legacy);

    const rows = leftoverEntitiesFromReport(legacy);
    assert.equal(rows.length, 1);
    assert.equal(rows[0].entity, leftover);

    const cleaned = await cleanupLeftovers(tmpPrev.dir, runOpts(tmpOut, world));
    assert.equal(cleaned.report.status, 'complete', cleaned.report.reason);
    assert.equal(world.entities.get(leftover).exists, false);
    assert.deepEqual(cleaned.report.cleanup.remaining, []);
    assert.equal(cleaned.report.framework, null);
    assert.ok(cleaned.report.frameworkSides);
    assert.equal(typeof cleaned.report.frameworkSides.server, 'string');
  } finally {
    tmpPrev.cleanup();
    tmpOut.cleanup();
  }
});

function mainMenuFakeWorld(extra = {}) {
  return createFakeWorld({
    players: [],
    users: [],
    pingExtra: { worlds: ['Default World', 'LoadingWorld0'] },
    ...extra,
  });
}

const MENU_TEARDOWN = [
  {
    kind: 'op',
    name: 'teardown close',
    side: 'client',
    op: 'mods_ui',
    args: { action: 'close' },
    expect: { ok: true, 'after.open': false },
  },
];

test('a main-menu case completes without a connected user', async () => {
  const tmp = tempDir();
  const world = mainMenuFakeWorld();
  try {
    const { report } = await runCase({
      name: 'menu-no-user',
      world: 'main-menu',
      steps: [{ kind: 'op', name: 'ping', side: 'client', op: 'ping', expect: { side: 'client' } }],
      teardown: MENU_TEARDOWN,
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.startState.world, 'main-menu');
    assert.equal(report.endWorld, 'main-menu');
    assert.equal(report.startState.player, undefined);
    assert.equal(report.startState.protection, undefined);
    assert.equal(report.adminGranted, null);
    assert.ok(!world.calls.some((c) => c.op === 'players' || c.op === 'users'));
    assert.ok(!world.calls.some((c) => c.op === 'command'));
    assert.ok(!world.calls.some((c) => c.op === 'set'));
    assert.ok(!world.calls.some((c) => c.op === 'query'));
    assert.ok(!world.calls.some((c) => c.op === 'get'));
    const checkNames = report.finalChecks.map((c) => c.name);
    assert.deepEqual(checkNames, ['remainingTestEntities']);
    assert.equal(report.finalChecks[0].ok, true);
    assert.ok(report.teardown?.steps?.length >= 1);
    assert.equal(report.teardown.steps[0].ok, true);
    assert.ok(report.logSignals);
    assert.equal(report.leftoverSummary.protectionOk, true);
    assert.equal(report.leftoverSummary.adminRestored, true);
  } finally {
    tmp.cleanup();
  }
});

test('a connected case still aborts without a connected user', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({ players: [], users: [] });
  try {
    const { report } = await runCase({
      name: 'connected-no-user',
      world: 'connected',
      steps: [{ kind: 'op', name: 'ping', side: 'client', op: 'ping', expect: { side: 'client' } }],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'aborted');
    assert.match(String(report.reason), /no player found/);
    assert.ok(world.calls.some((c) => c.op === 'players'));
    assert.ok(world.calls.some((c) => c.op === 'users'));
  } finally {
    tmp.cleanup();
  }
});

test('an any case records the detected world', async () => {
  const tmpMenu = tempDir();
  const tmpConnected = tempDir();
  const menuWorld = mainMenuFakeWorld();
  const connectedWorld = createFakeWorld();
  try {
    const menu = await runCase({
      name: 'any-menu',
      world: 'any',
      steps: [],
      teardown: MENU_TEARDOWN,
    }, runOpts(tmpMenu, menuWorld));
    assert.equal(menu.report.status, 'complete', menu.report.reason);
    assert.equal(menu.report.startState.world, 'main-menu');
    assert.equal(menu.report.endWorld, 'main-menu');
    assert.ok(!menuWorld.calls.some((c) => c.op === 'players'));

    const connected = await runCase({
      name: 'any-connected',
      world: 'any',
      steps: [],
    }, runOpts(tmpConnected, connectedWorld));
    assert.equal(connected.report.status, 'complete', connected.report.reason);
    assert.equal(connected.report.startState.world, 'connected');
    assert.equal(connected.report.endWorld, 'connected');
    assert.ok(connectedWorld.calls.some((c) => c.op === 'players'));
    assert.ok(connected.report.startState.player);
    assert.ok(connected.report.finalChecks.some((c) => c.name === 'playerHealth'));
    assert.ok(connected.report.finalChecks.some((c) => c.name === 'protection'));
  } finally {
    tmpMenu.cleanup();
    tmpConnected.cleanup();
  }
});

test('a main-menu case aborts when the client has a Client_0 world', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'menu-with-client0',
      world: 'main-menu',
      steps: [{ kind: 'op', name: 'ping', side: 'client', op: 'ping', expect: { side: 'client' } }],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'aborted');
    assert.match(String(report.reason), /Client_0/);
    assert.match(String(report.reason), /main menu/);
    assert.equal(report.startState.world, 'main-menu');
    assert.deepEqual(report.steps, []);
    assert.ok(!world.calls.some((c) => c.op === 'command'));
    assert.ok(!world.calls.some((c) => c.op === 'set'));
    assert.deepEqual(report.finalChecks.map((c) => c.name), ['remainingTestEntities']);
  } finally {
    tmp.cleanup();
  }
});

test('resolveRunSite prefers --site, else a concrete case site, else the world default', () => {
  assert.equal(defaultSiteForWorld(CASE_WORLD_MAIN_MENU), 'main-menu');
  assert.equal(defaultSiteForWorld(CASE_WORLD_CONNECTED), 'top-tab');
  assert.equal(resolveRunSite({ site: 'shortcut' }, CASE_WORLD_CONNECTED, { site: 'main-menu' }), 'shortcut');
  assert.equal(resolveRunSite({}, CASE_WORLD_CONNECTED, { site: 'main-menu' }), 'main-menu');
  assert.equal(resolveRunSite({}, CASE_WORLD_CONNECTED, { site: SITE_PLACEHOLDER }), 'top-tab');
  assert.equal(resolveRunSite({}, CASE_WORLD_MAIN_MENU, { site: SITE_PLACEHOLDER }), 'main-menu');
  assert.equal(resolveRunSite({}, CASE_WORLD_CONNECTED, {}), 'top-tab');
  assert.throws(
    () => resolveRunSite({ site: 'pause-menu' }, CASE_WORLD_CONNECTED, {}),
    /--site must be main-menu, top-tab, or shortcut/,
  );
});

test('applyOpenSitePlaceholder substitutes @site on mods_ui open only', () => {
  const open = { kind: 'op', op: 'mods_ui', args: { action: 'open', site: SITE_PLACEHOLDER } };
  const got = applyOpenSitePlaceholder(open, 'top-tab');
  assert.equal(got.args.site, 'top-tab');
  assert.equal(open.args.site, SITE_PLACEHOLDER);
  const close = { kind: 'op', op: 'mods_ui', args: { action: 'close' } };
  assert.equal(applyOpenSitePlaceholder(close, 'top-tab'), close);
  const concrete = { kind: 'op', op: 'mods_ui', args: { action: 'open', site: 'main-menu' } };
  assert.equal(applyOpenSitePlaceholder(concrete, 'top-tab'), concrete);
});

test('runCase substitutes @site from --site and records startState.site', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'site-override',
      world: 'connected',
      site: SITE_PLACEHOLDER,
      steps: [
        {
          kind: 'op',
          name: 'open',
          side: 'client',
          op: 'mods_ui',
          args: { action: 'open', site: SITE_PLACEHOLDER },
          expect: { ok: true, 'after.open': true },
        },
        {
          kind: 'op',
          name: 'close',
          side: 'client',
          op: 'mods_ui',
          args: { action: 'close' },
          expect: { ok: true, 'after.open': false },
        },
      ],
      teardown: MENU_TEARDOWN,
    }, runOpts(tmp, world, { site: 'shortcut' }));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.startState.site, 'shortcut');
    assert.ok(world.calls.some((c) => c.op === 'mods_ui' && c.args?.action === 'open' && c.args?.site === 'shortcut'));
    assert.ok(!world.calls.some((c) => c.args?.site === SITE_PLACEHOLDER));
  } finally {
    tmp.cleanup();
  }
});

test('omitted --site is top-tab when Client_0 is present and main-menu when it is not', async () => {
  const tmpConnected = tempDir();
  const tmpMenu = tempDir();
  const connectedWorld = createFakeWorld();
  const menuWorld = mainMenuFakeWorld();
  const openClose = {
    site: SITE_PLACEHOLDER,
    steps: [
      {
        kind: 'op',
        name: 'open',
        side: 'client',
        op: 'mods_ui',
        args: { action: 'open', site: SITE_PLACEHOLDER },
        expect: { ok: true, 'after.open': true },
      },
      {
        kind: 'op',
        name: 'close',
        side: 'client',
        op: 'mods_ui',
        args: { action: 'close' },
        expect: { ok: true, 'after.open': false },
      },
    ],
    teardown: MENU_TEARDOWN,
  };
  try {
    const connected = await runCase({
      name: 'site-connected-default',
      world: 'connected',
      ...openClose,
    }, runOpts(tmpConnected, connectedWorld));
    assert.equal(connected.report.status, 'complete', connected.report.reason);
    assert.equal(connected.report.startState.site, 'top-tab');
    assert.ok(connectedWorld.calls.some((c) => c.op === 'mods_ui' && c.args?.action === 'open' && c.args?.site === 'top-tab'));

    const menu = await runCase({
      name: 'site-menu-default',
      world: 'main-menu',
      ...openClose,
    }, runOpts(tmpMenu, menuWorld));
    assert.equal(menu.report.status, 'complete', menu.report.reason);
    assert.equal(menu.report.startState.site, 'main-menu');
    assert.ok(menuWorld.calls.some((c) => c.op === 'mods_ui' && c.args?.action === 'open' && c.args?.site === 'main-menu'));
    assert.ok(!menuWorld.calls.some((c) => c.args?.site === SITE_PLACEHOLDER));
  } finally {
    tmpConnected.cleanup();
    tmpMenu.cleanup();
  }
});

test('caseEndWorld is connected or main-menu; omitted follows the start world', () => {
  assert.equal(caseEndWorld({}), null);
  assert.equal(caseEndWorld({ endWorld: '' }), null);
  assert.equal(caseEndWorld({ endWorld: 'connected' }), CASE_WORLD_CONNECTED);
  assert.equal(caseEndWorld({ endWorld: 'main-menu' }), CASE_WORLD_MAIN_MENU);
  assert.throws(() => caseEndWorld({ endWorld: 'any' }), /endWorld must be "connected" or "main-menu"/);
});

test('endWorld main-menu skips connected final checks after a leave', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'leave-end-menu',
      world: 'any',
      endWorld: 'main-menu',
      steps: [
        {
          kind: 'op',
          name: 'disconnect',
          side: 'client',
          op: 'disconnect',
          expect: { ok: true, requested: true, state: { neq: 'superseded' } },
        },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.startState.world, 'connected');
    assert.equal(report.endWorld, 'main-menu');
    assert.ok(!world.clientWorlds.includes('Client_0'));
    assert.deepEqual(report.finalChecks.map((c) => c.name), ['remainingTestEntities']);
    const disconnectIdx = world.calls.findIndex((c) => c.op === 'disconnect');
    assert.ok(disconnectIdx >= 0);
    assert.ok(
      !world.calls.some((c, i) => i > disconnectIdx && c.op === 'query'),
      'endWorld main-menu must not query carriers after the leave',
    );
    assert.equal(report.leftoverSummary.protectionOk, true);
  } finally {
    tmp.cleanup();
  }
});

test('a leave without endWorld still runs connected final checks at the main menu', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'leave-no-endworld',
      world: 'any',
      steps: [
        {
          kind: 'op',
          name: 'disconnect',
          side: 'client',
          op: 'disconnect',
          expect: { ok: true, requested: true },
        },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'aborted');
    assert.match(String(report.reason), /game world does not exist yet|no connected user/);
    assert.equal(report.startState.world, 'connected');
    assert.equal(report.endWorld, 'connected');
    assert.ok(!world.clientWorlds.includes('Client_0'));
  } finally {
    tmp.cleanup();
  }
});

test('endWorld connected runs connected final checks after a join from the main menu', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    clientWorlds: ['Default World'],
    serverContextLive: false,
  });
  try {
    const { report } = await runCase({
      name: 'join-end-connected',
      world: 'main-menu',
      endWorld: 'connected',
      steps: [
        {
          kind: 'op',
          name: 'connect',
          side: 'client',
          op: 'connect',
          args: { address: '127.0.0.1', port: 9976 },
          expect: { ok: true },
        },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.startState.world, 'main-menu');
    assert.equal(report.endWorld, 'connected');
    assert.ok(world.clientWorlds.includes('Client_0'));
    const checkNames = report.finalChecks.map((c) => c.name);
    assert.ok(checkNames.includes('playerHealth'));
    assert.ok(checkNames.includes('protection'));
    assert.ok(checkNames.includes('adminState'));
    assert.ok(checkNames.includes('carriers'));
    assert.ok(checkNames.includes('remainingTestEntities'));
    assert.ok(world.calls.some((c) => c.op === 'players'));
    assert.ok(world.calls.some((c) => c.op === 'query'));
  } finally {
    tmp.cleanup();
  }
});

test('statusOnly poll clears a superseded latch then leave uses main-menu final checks', async () => {
  const tmp = tempDir();
  const world = createFakeWorld({
    leaveRequested: true,
    leaveEpoch: 1,
    clientWorldEpoch: 2,
  });
  try {
    const { report } = await runCase({
      name: 'latch-leave-menu',
      world: 'any',
      endWorld: 'main-menu',
      steps: [
        {
          kind: 'op',
          name: 'poll disconnect latch',
          side: 'client',
          op: 'disconnect',
          args: { statusOnly: true },
          pollMs: 1000,
          timeoutMs: 120000,
          expect: { ok: true, requested: false, state: { neq: 'superseded' } },
        },
        {
          kind: 'op',
          name: 'disconnect',
          side: 'client',
          op: 'disconnect',
          expect: { ok: true, requested: true, state: { neq: 'superseded' } },
        },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.endWorld, 'main-menu');
    const disconnectCalls = world.calls.filter((c) => c.op === 'disconnect');
    const statusOnly = disconnectCalls.filter((c) => c.args?.statusOnly === true);
    const leave = disconnectCalls.filter((c) => c.args?.statusOnly !== true);
    assert.ok(statusOnly.length >= 2, `superseded observe then idle latch, got ${statusOnly.length} statusOnly`);
    assert.equal(leave.length, 1);
    assert.ok(disconnectCalls.indexOf(statusOnly[0]) < disconnectCalls.indexOf(leave[0]));
    assert.equal(world.leaveRequested, true);
    assert.ok(!world.clientWorlds.includes('Client_0'));
    assert.deepEqual(report.finalChecks.map((c) => c.name), ['remainingTestEntities']);
  } finally {
    tmp.cleanup();
  }
});
