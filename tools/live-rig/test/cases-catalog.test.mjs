import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { executeStep, STEP_KINDS } from '../lib/steps.mjs';
import { fakeClock } from '../lib/clock.mjs';
import { applyOpenSitePlaceholder, resolveRunSite, CASE_WORLD_CONNECTED, CASE_WORLD_MAIN_MENU } from '../lib/run.mjs';
import { createFakeWorld, tempDir, testBridge, TOOL_DIR } from './helpers.mjs';

function loadShippedCases() {
  const out = [];
  for (const dir of ['cases', 'cases-catalog', 'cases-ui']) {
    const abs = path.join(TOOL_DIR, dir);
    if (!existsSync(abs)) continue;
    for (const name of readdirSync(abs).filter((f) => f.endsWith('.json')).sort()) {
      const file = path.join(abs, name);
      out.push({
        label: `${dir}/${name}`,
        doc: JSON.parse(readFileSync(file, 'utf8')),
      });
    }
  }
  return out;
}

function caseSteps(doc) {
  return [...(doc.steps ?? []), ...(doc.teardown ?? [])];
}

test('shipped cases use known step kinds and prepare every snippet before load', () => {
  const cases = loadShippedCases();
  assert.ok(cases.length > 0, 'expected JSON under tools/live-rig/cases and cases-catalog');
  for (const { label, doc } of cases) {
    const prepared = new Set();
    for (const step of caseSteps(doc)) {
      assert.ok(STEP_KINDS.includes(step.kind), `${label}: unknown step kind ${JSON.stringify(step.kind)}`);
      if (step.kind === 'snippetPrepare' || (step.kind === 'snippet' && step.prepare === true)) {
        assert.ok(step.file, `${label}: ${step.kind} requires file`);
        prepared.add(step.file);
      }
      if (step.kind === 'snippet' && step.prepare !== true) {
        assert.ok(
          prepared.has(step.file),
          `${label}: snippet ${step.file} is not preceded by snippetPrepare for the same Snippet.cs`,
        );
      }
    }
  }
});

test('swap-client is a three-cycle client Sample swap using known step kinds', () => {
  const cases = loadShippedCases();
  const hit = cases.find((c) => c.label === 'cases/swap-client.json' || c.doc.name === 'swap-client');
  assert.ok(hit, 'expected tools/live-rig/cases/swap-client.json');
  const { doc } = hit;
  const steps = caseSteps(doc);
  const kinds = new Set(steps.map((s) => s.kind));
  for (const kind of kinds) {
    assert.ok(STEP_KINDS.includes(kind), `swap-client: unknown step kind ${kind}`);
  }
  const swaps = (doc.steps ?? []).filter((s) => s.kind === 'swap');
  assert.equal(swaps.length, 3);
  assert.ok(swaps.every((s) => s.dll === '<path>'));
  assert.equal((doc.steps ?? []).filter((s) => s.kind === 'spawn').length, 0);
  assert.equal((doc.steps ?? []).filter((s) => s.kind === 'removal' || s.kind === 'reinstall').length, 0);
  const commands = (doc.steps ?? []).filter((s) => s.kind === 'command').map((s) => s.text);
  assert.ok(commands.filter((t) => t === '.catalogex warm').length >= 3);
  assert.ok(commands.filter((t) => t === '.catalogex borrow').length >= 6);
  assert.ok(commands.filter((t) => t === '.catalogex dispose').length >= 3);
  assert.equal(commands.filter((t) => t === '.froststyle off').length, 3);
  assert.equal(commands.filter((t) => t === '.froststyle on').length, 3);
  const clientLogs = (doc.steps ?? []).filter((s) => s.kind === 'log' && s.side === 'client');
  assert.ok(clientLogs.some((s) => /CATALOGEX borrow enabled=True/.test(String(s.regex))));
  assert.ok(clientLogs.some((s) => /CATALOGEX status handle=/.test(String(s.regex))));
  assert.ok(clientLogs.some((s) => /Effects: disposed Particle/.test(String(s.regex))));
  const froststyleClient = clientLogs.filter((s) => /FROSTSTYLE client enabled=/.test(String(s.regex)));
  assert.equal(froststyleClient.length, 6);
  assert.ok(froststyleClient.every((s) => s.count === 1), 'each froststyle client log requires exactly one line');
  const notes = (doc.notes ?? []).join('\n');
  assert.match(notes, /--side client/);
  assert.match(notes, /froststyle enabled=True/);
  assert.match(notes, /frostdash off/);
  assert.match(notes, /admin false/);
  assert.match(notes, /Core-owned, a Sample swap does not reconcile/);
  const teardown = doc.teardown ?? [];
  assert.ok(teardown.some((s) => s.text === '.froststyle on'));
  assert.ok(teardown.some((s) => s.text === '.frostdash off'));
  assert.ok(teardown.some((s) => s.text === '.catalogex dispose'));
});

test('every FROSTSTYLE client log step has a sinceRecord', () => {
  for (const { label, doc } of loadShippedCases()) {
    for (const step of caseSteps(doc)) {
      if (step.kind !== 'log') continue;
      if (!/FROSTSTYLE client/.test(String(step.regex ?? ''))) continue;
      assert.ok(
        step.sinceRecord,
        `${label}: log ${JSON.stringify(step.name ?? step.regex)} must set sinceRecord`,
      );
    }
  }
});

test('every count log step has a sinceRecord', () => {
  for (const { label, doc } of loadShippedCases()) {
    for (const step of caseSteps(doc)) {
      if (step.kind !== 'log') continue;
      if (step.count == null) continue;
      assert.ok(
        step.sinceRecord,
        `${label}: count log ${JSON.stringify(step.name ?? step.regex)} must set sinceRecord`,
      );
    }
  }
});

test('cases-ui JSON uses known step kinds and teardowns close the workspace', () => {
  const ui = loadShippedCases().filter((c) => c.label.startsWith('cases-ui/'));
  assert.ok(ui.length >= 9, `expected cases-ui JSON, got ${ui.length}`);
  for (const { label, doc } of ui) {
    const teardown = doc.teardown ?? [];
    assert.ok(teardown.length > 0, `${label}: missing teardown`);
    const close = teardown.find((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'close');
    const status = teardown.find((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'status');
    const restore = teardown.find((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'resolution');
    assert.ok(close, `${label}: teardown must close the workspace`);
    assert.ok(restore, `${label}: teardown must restore resolution`);
    assert.ok(status, `${label}: teardown must assert status`);
    assert.equal(status.expect['after.open'], false, `${label}: teardown status expects closed`);
    assert.equal(status.expect['after.curtain'], false, `${label}: teardown status expects curtain released`);
    assert.equal(status.expect['after.modsCapture'], false, `${label}: teardown status expects after.modsCapture false`);
    assert.equal(status.expect['after.capture'], undefined, `${label}: teardown must not expect deprecated after.capture`);
    assert.equal(status.expect['after.inputCapture'], undefined, `${label}: teardown must not expect global after.inputCapture`);
    assert.equal(
      status.expect['after.panes'],
      '@record:baselineResolution.after.panes',
      `${label}: teardown panes must equal the recorded baseline`,
    );
    assert.equal(restore.args.width, '@record:baselineResolution.after.width', `${label}: restore width from status`);
    assert.equal(restore.args.height, '@record:baselineResolution.after.height', `${label}: restore height from status`);
    assert.equal(restore.args.mode, '@record:baselineResolution.after.mode', `${label}: restore requires mode`);
    const record = (doc.steps ?? []).find((s) => s.record === 'baselineResolution');
    assert.ok(record, `${label}: must record baselineResolution`);
    assert.equal(record.op, 'mods_ui');
    assert.equal(record.args?.action, 'status', `${label}: baseline resolution is recorded from status, not a bare resolution action`);
  }
});

test('ui-open-close-cycles is 20 open/close cycles with a capture every 5', () => {
  const hit = loadShippedCases().find((c) => c.doc.name === 'ui-open-close-cycles');
  assert.ok(hit, 'expected ui-open-close-cycles');
  const opens = (hit.doc.steps ?? []).filter((s) => s.kind === 'op' && s.args?.action === 'open');
  const closes = (hit.doc.steps ?? []).filter((s) => s.kind === 'op' && s.args?.action === 'close');
  const captures = (hit.doc.steps ?? []).filter((s) => s.kind === 'capture');
  assert.equal(opens.length, 20);
  assert.equal(closes.length, 20);
  assert.equal(captures.length, 4);
  assert.equal(hit.doc.site, '@site');
  assert.ok(opens.every((s) => s.args.site === '@site'));
});

test('ui soak cases include no-capture controls and 60s capture variants', () => {
  const byName = Object.fromEntries(loadShippedCases().map((c) => [c.doc.name, c.doc]));
  for (const name of ['ui-soak-menu', 'ui-soak-settings']) {
    const soak = (byName[name].steps ?? []).find((s) => s.kind === 'soak');
    assert.ok(soak, `${name} missing soak`);
    assert.equal(soak.minutes, 15);
    assert.equal(soak.capture, null);
    assert.equal(soak.stopOnFirstFailure, true);
    const cap = (byName[`${name}-capture`].steps ?? []).find((s) => s.kind === 'soak');
    assert.equal(cap.capture.intervalSeconds, 60);
  }
  const baseline = (byName['ui-baseline'].steps ?? []).find((s) => s.kind === 'soak');
  assert.equal(baseline.minutes, 2);
  assert.equal(baseline.capture, null);
  const baselineConnected = (byName['ui-baseline-connected'].steps ?? []).find((s) => s.kind === 'soak');
  assert.equal(baselineConnected.minutes, 2);
  assert.equal(baselineConnected.capture, null);
});

test('cases-ui world is connected, main-menu, or any as specified', () => {
  const ui = loadShippedCases().filter((c) => c.label.startsWith('cases-ui/'));
  const byName = Object.fromEntries(ui.map((c) => [c.doc.name, c.doc]));
  for (const name of ['ui-baseline', 'ui-soak-menu', 'ui-soak-menu-capture', 'ui-core-swap-main-menu', 'ui-reconnect-only']) {
    assert.equal(byName[name].world, 'main-menu', name);
  }
  assert.equal(byName['ui-open-close-cycles'].world, 'any');
  assert.equal(byName['ui-disconnect-only'].world, 'any');
  assert.equal(byName['ui-disconnect-only'].endWorld, 'main-menu');
  assert.equal(byName['ui-reconnect-only'].endWorld, 'connected');
  assert.equal(byName['ui-baseline-connected'].world, 'connected');
  for (const name of [
    'ui-core-swap-open',
    'ui-disconnect-reconnect',
    'ui-list-and-search',
    'ui-main-menu-set',
    'ui-native-tab-return',
    'ui-removal-reinstall',
    'ui-resolutions',
    'ui-sample-swap-page-open',
    'ui-settings',
    'ui-soak-settings',
    'ui-soak-settings-capture',
  ]) {
    assert.equal(byName[name].world, 'connected', name);
  }
  for (const { label, doc } of ui) {
    assert.ok(['connected', 'main-menu', 'any'].includes(doc.world), `${label}: world`);
    if (doc.endWorld != null && doc.endWorld !== '') {
      assert.ok(['connected', 'main-menu'].includes(doc.endWorld), `${label}: endWorld`);
    }
  }
  assert.equal(byName['input-entry-buttons'].world, 'any');
  for (const name of [
    'input-list-and-scroll',
    'input-settings',
    'input-native-tab-return',
    'input-shortcut-and-escape',
    'input-resolutions',
  ]) {
    assert.equal(byName[name].world, 'connected', name);
  }
});

function uiByName(name) {
  const hit = loadShippedCases().find((c) => c.doc.name === name);
  assert.ok(hit, `expected cases-ui/${name}.json`);
  return hit.doc;
}

function uiOps(doc, action) {
  return (doc.steps ?? []).filter((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === action);
}

test('Gate 3/4 UI cases are present, top-tab or main-menu, and restore capture+counts', () => {
  for (const name of [
    'ui-sample-swap-page-open',
    'ui-core-swap-open',
    'ui-core-swap-main-menu',
    'ui-removal-reinstall',
    'ui-settings',
    'ui-disconnect-reconnect',
  ]) {
    const doc = uiByName(name);
    assert.ok((doc.teardown ?? []).length > 0, `${name}: teardown`);
    const status = (doc.teardown ?? []).find((s) => s.kind === 'op' && s.args?.action === 'status');
    assert.equal(status.expect['after.modsCapture'], false, `${name}: teardown uses after.modsCapture`);
    assert.equal(status.expect['after.capture'], undefined, `${name}: teardown must not expect deprecated after.capture`);
    assert.equal(status.expect['after.panes'], '@record:baselineResolution.after.panes', `${name}: teardown panes at closed baseline`);
    const restore = (doc.teardown ?? []).find((s) => s.kind === 'op' && s.args?.action === 'resolution');
    assert.equal(restore.args.width, '@record:baselineResolution.after.width');
    assert.equal(restore.args.height, '@record:baselineResolution.after.height');
    assert.equal(restore.args.mode, '@record:baselineResolution.after.mode');
  }
});

test('ui-sample-swap-page-open is three client Sample swaps with Example open', () => {
  const doc = uiByName('ui-sample-swap-page-open');
  assert.equal(doc.site, 'top-tab');
  const swaps = (doc.steps ?? []).filter((s) => s.kind === 'swap');
  assert.equal(swaps.length, 3);
  assert.ok(swaps.every((s) => s.dll === '<path>'));
  assert.equal((doc.steps ?? []).filter((s) => s.kind === 'removal' || s.kind === 'reinstall').length, 0);
  const selects = uiOps(doc, 'select').filter((s) => s.args.page === 'custom' && s.args.group === 'Example');
  assert.ok(selects.length >= 6, 'open Example before each swap and again on the new generation');
  const clicks = uiOps(doc, 'widget_click').filter((s) => /Try an action/i.test(String(s.args.label ?? '')));
  assert.equal(clicks.length, 3);
  const actionLogs = (doc.steps ?? []).filter((s) => s.kind === 'log' && /Sample menu: action handled/.test(String(s.regex ?? '')));
  assert.equal(actionLogs.length, 3);
  assert.ok(actionLogs.every((s) => s.count === 1 && s.sinceRecord));
  assert.equal((doc.steps ?? []).filter((s) => s.kind === 'capture').length, 3);
  const notes = (doc.notes ?? []).join('\n');
  assert.match(notes, /--side client/);
  assert.match(notes, /LastWrite/);
  assert.match(notes, /Never a Core swap/i);
});

function afterEachCoreSwap(steps, swap) {
  const idx = steps.indexOf(swap);
  return steps.slice(idx + 1, idx + 12);
}

test('ui-core-swap-open is three coreSwap cycles with reopen, counts, close, and handshake', () => {
  const doc = uiByName('ui-core-swap-open');
  assert.equal(doc.site, 'top-tab');
  const steps = doc.steps ?? [];
  const swaps = steps.filter((s) => s.kind === 'coreSwap');
  assert.equal(swaps.length, 3);
  assert.equal(steps.filter((s) => s.kind === 'op' && (s.op === 'core_reload' || s.op === 'reload_status')).length, 0);
  for (const [i, step] of swaps.entries()) {
    assert.equal(step.side, 'client');
    assert.equal(step.expectGenerationDelta, 1);
    assert.equal(step.maxWaitMs, 90000);
    assert.ok(step.record, `coreSwap ${i + 1} must record imageHashes`);
    const after = afterEachCoreSwap(steps, step);
    assert.ok(after.some((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'open'), `reopen after coreSwap ${i + 1}`);
    assert.ok(
      after.some((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'status'
        && s.expect && s.expect['after.observers'] === '@record:openBaseline.after.observers'
        && s.expect['after.panes'] === '@record:openBaseline.after.panes'),
      `status counts after coreSwap ${i + 1}`,
    );
    assert.ok(after.some((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'close'), `close after coreSwap ${i + 1}`);
    const users = after.find((s) => s.kind === 'op' && s.op === 'users');
    assert.ok(users, `connection after coreSwap ${i + 1}`);
    assert.match(String(users.expect), /connected/);
    assert.match(String(users.expect), /handshake/);
  }
  const notes = (doc.notes ?? []).join('\n');
  assert.match(notes, /previousAlive/);
  assert.match(notes, /imageHashes/);
  assert.match(notes, /refusalWindow|ECONNREFUSED/);
});

test('ui-core-swap-main-menu is the main-menu Core swap variant without connection assertions', () => {
  const doc = uiByName('ui-core-swap-main-menu');
  assert.equal(doc.site, 'main-menu');
  const steps = doc.steps ?? [];
  const swaps = steps.filter((s) => s.kind === 'coreSwap');
  assert.equal(swaps.length, 3);
  assert.equal(steps.filter((s) => s.kind === 'op' && (s.op === 'core_reload' || s.op === 'reload_status')).length, 0);
  assert.equal(steps.filter((s) => s.kind === 'op' && s.op === 'users').length, 0);
  for (const [i, step] of swaps.entries()) {
    assert.equal(step.side, 'client');
    assert.equal(step.expectGenerationDelta, 1);
    assert.equal(step.maxWaitMs, 90000);
    assert.ok(step.record, `coreSwap ${i + 1} must record imageHashes`);
    const after = afterEachCoreSwap(steps, step);
    assert.ok(after.some((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'open'), `reopen after coreSwap ${i + 1}`);
    assert.ok(
      after.some((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'status'
        && s.expect && s.expect['after.observers'] === '@record:openBaseline.after.observers'),
      `status counts after coreSwap ${i + 1}`,
    );
    assert.ok(after.some((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'close'), `close after coreSwap ${i + 1}`);
  }
  const notes = (doc.notes ?? []).join('\n');
  assert.match(notes, /main-menu/);
  assert.match(notes, /no connection/i);
  assert.match(notes, /imageHashes/);
});

test('cases-ui does not poll reload_status through op steps without tolerateBridgeDownMs', () => {
  const ui = loadShippedCases().filter((c) => c.label.startsWith('cases-ui/'));
  assert.ok(ui.length > 0, 'expected cases-ui JSON');
  const bad = [];
  for (const { label, doc } of ui) {
    for (const step of caseSteps(doc)) {
      if (step.kind !== 'op' || step.op !== 'reload_status') continue;
      const nested = step.poll && typeof step.poll === 'object' ? Number(step.poll.intervalMs) : Number.NaN;
      const polls = (Number.isFinite(Number(step.pollMs)) && Number(step.pollMs) >= 0)
        || (Number.isFinite(nested) && nested >= 0);
      if (!polls) continue;
      const tolerate = Number(step.tolerateBridgeDownMs);
      if (!(Number.isFinite(tolerate) && tolerate > 0)) {
        bad.push(`${label}: ${JSON.stringify(step.name ?? 'reload_status')} polls reload_status without tolerateBridgeDownMs`);
      }
    }
  }
  assert.deepEqual(bad, [], `cases-ui op reload_status polls must set tolerateBridgeDownMs:\n${bad.join('\n')}`);
});

test('ui-removal-reinstall is not a swap acceptance', () => {
  const doc = uiByName('ui-removal-reinstall');
  assert.equal((doc.steps ?? []).filter((s) => s.kind === 'swap').length, 0);
  assert.equal((doc.steps ?? []).filter((s) => s.kind === 'removal').length, 1);
  assert.equal((doc.steps ?? []).filter((s) => s.kind === 'reinstall').length, 1);
  const afterRemovalClick = uiOps(doc, 'widget_click').find((s) => s.expect && s.expect.ok === false);
  assert.ok(afterRemovalClick, 'widget_click after removal must expect ok false');
  const notes = (doc.notes ?? []).join('\n');
  assert.match(notes, /swap acceptance/i);
});

function isAdminFalseSet(step) {
  return step.kind === 'op' && step.op === 'set' && step.args?.fields?.IsAdmin === false;
}

function assertAdminApplyPolledBeforeDrop(doc, applyName, noteRe) {
  const steps = caseSteps(doc);
  const applyIdx = steps.findIndex((s) => s.name === applyName);
  assert.ok(applyIdx >= 0, applyName);
  const dropIdx = steps.findIndex((s, i) => i > applyIdx && isAdminFalseSet(s));
  const pollIdx = steps.findIndex((s, i) => i > applyIdx && (dropIdx < 0 || i < dropIdx)
    && s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'widget_read'
    && s.poll);
  assert.ok(pollIdx >= 0, `${applyName} must be followed by a polled widget_read before any admin drop`);
  assert.equal(steps[pollIdx].poll.intervalMs, 500, applyName);
  assert.equal(steps[pollIdx].poll.timeoutMs, 10000, applyName);
  assert.equal(steps[pollIdx].pollMs, undefined, `${applyName}: polled note must use poll, not pollMs`);
  assert.match(String(steps[pollIdx].expect), noteRe, applyName);
  return { steps, applyIdx, pollIdx, dropIdx };
}

test('ui-settings covers personal, synced-readonly, ExampleLimit, pending, inventory', () => {
  const doc = uiByName('ui-settings');
  const clicks = uiOps(doc, 'widget_click');
  assert.ok(clicks.some((s) => /ShowHints/i.test(String(s.args.label ?? ''))));
  assert.ok(clicks.some((s) => /Apply ExampleLimit/i.test(String(s.args.label ?? ''))));
  assert.ok(clicks.some((s) => s.args.value === '12'));
  assert.ok(clicks.some((s) => s.args.value === '999'));
  assert.ok(clicks.some((s) => s.args.value === '10'));
  assert.ok((doc.steps ?? []).some((s) => s.kind === 'op' && s.op === 'set'));
  const zeroLogs = (doc.steps ?? []).filter((s) => s.kind === 'log' && s.count === 0);
  assert.ok(zeroLogs.length >= 1);
  assert.ok(zeroLogs.every((s) => s.sinceRecord));
  const notes = (doc.notes ?? []).join('\n');
  assert.match(notes, /not exercisable through the UI/);
  assert.match(notes, /Installed/);
  assert.match(notes, /Enabled/);
  const teardownSets = (doc.teardown ?? []).filter((s) => s.kind === 'op' && s.op === 'set');
  assert.ok(teardownSets.some((s) => s.args?.fields?.IsAdmin === false));
  for (const name of [
    'read non-admin refusal',
    'read admin 12',
    'read bounds refusal',
    'read admin 11',
    'read restore 10 saved',
  ]) {
    const step = (doc.steps ?? []).find((s) => s.name === name);
    assert.ok(step, name);
    assert.equal(step.poll?.intervalMs, 500, name);
    assert.equal(step.poll?.timeoutMs, 10000, name);
    assert.equal(step.pollMs, undefined, `${name} must poll, not a single immediate read`);
    assert.ok(step.record, `${name} must record observed latency`);
  }
  assertAdminApplyPolledBeforeDrop(doc, 'Apply 12 admin', /Saved by the server/);
  assertAdminApplyPolledBeforeDrop(doc, 'Apply 999 bounds', /Outside the values this setting accepts/);
  assertAdminApplyPolledBeforeDrop(doc, 'Apply 11 admin', /Saved by the server/);
  const restore = assertAdminApplyPolledBeforeDrop(doc, 'Apply 10 restore', /Saved by the server/);
  const restoreValue = restore.steps.findIndex((s, i) => i > restore.pollIdx
    && (restore.dropIdx < 0 || i < restore.dropIdx)
    && s.args?.action === 'widget_read'
    && /"value":"10"/.test(String(s.expect)));
  assert.ok(restoreValue >= 0, 'restore 10 must read back value 10 after Saved and before admin drop');
  const teardownRestore = assertAdminApplyPolledBeforeDrop(doc, 'teardown Apply 10', /Saved by the server/);
  const teardownValue = teardownRestore.steps.findIndex((s, i) => i > teardownRestore.pollIdx
    && (teardownRestore.dropIdx < 0 || i < teardownRestore.dropIdx)
    && s.args?.action === 'widget_read'
    && /"value":"10"/.test(String(s.expect)));
  assert.ok(teardownValue >= 0, 'teardown restore 10 must read back value 10 after Saved and before admin drop');
  assert.ok(teardownRestore.dropIdx > teardownRestore.pollIdx, 'teardown Saved poll must finish before run-level admin restore');
});

test('ui-disconnect-reconnect disconnects to the main menu then reconnects', () => {
  const doc = uiByName('ui-disconnect-reconnect');
  const steps = doc.steps ?? [];
  const disconnect = steps.find((s) => s.kind === 'op' && s.op === 'disconnect');
  assert.ok(disconnect);
  assert.equal(disconnect.record, 'beforeLeave');
  const connect = steps.find((s) => s.kind === 'op' && s.op === 'connect');
  assert.ok(connect);
  assert.match(String(connect.args?.address ?? ''), /127\.0\.0\.1/);
  assert.equal(connect.args?.port ?? 9976, 9976);
  assert.equal(connect.record, 'beforeConnect');
  const pingPolls = steps.filter((s) => s.kind === 'op' && s.op === 'ping' && s.pollMs != null);
  assert.ok(pingPolls.length >= 2, 'poll ping for leave (no Client_0) and join (Client_0)');
  const cleared = steps.find((s) => s.kind === 'log' && /cleared reason=disconnect/.test(String(s.regex ?? '')));
  assert.ok(cleared, 'must wait for Mods inventory: server context cleared reason=disconnect');
  assert.equal(cleared.side, 'client');
  assert.equal(cleared.sinceRecord, 'beforeLeave');
  assert.equal(cleared.record, 'inventoryCleared');
  assert.ok(Number(cleared.timeoutMs) > 0);
  const restored = steps.find((s) => s.kind === 'log' && /restored reason=handshake-ok/.test(String(s.regex ?? '')));
  assert.ok(restored, 'must wait for Mods inventory: server context restored reason=handshake-ok');
  assert.equal(restored.side, 'client');
  assert.equal(restored.sinceRecord, 'beforeConnect');
  assert.equal(restored.record, 'inventoryRestored');
  const unavailable = steps.find((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'status'
    && s.expect && s.expect['after.serverList.state'] === 'unavailable');
  assert.ok(unavailable, 'must assert after.serverList.state unavailable after disconnect');
  assert.ok(steps.indexOf(unavailable) > steps.indexOf(cleared));
  const available = steps.find((s) => s.kind === 'op' && s.op === 'mods_ui' && s.args?.action === 'status'
    && s.expect && s.expect['after.serverList.state'] === 'available');
  assert.ok(available, 'must assert after.serverList.state available after reconnect restore');
  assert.ok(steps.indexOf(available) > steps.indexOf(restored));
  const closed = steps.find((s) => s.name === 'status workspace closed after disconnect');
  assert.ok(closed, 'must assert workspace closed after disconnect');
  assert.equal(closed.expect['after.open'], false);
  assert.equal(closed.expect['after.modsCapture'], false);
  const firstAuth = steps.find((s) => s.record === 'firstAuthoritative');
  assert.ok(firstAuth);
  const handshake = steps.find((s) => s.name === 'wait users connected handshake ok');
  const selectAfter = steps.find((s) => s.name === 'select Server after reconnect');
  assert.ok(handshake, 'handshake wait');
  assert.ok(selectAfter, 'select after reconnect');
  assert.ok(steps.indexOf(selectAfter) > steps.indexOf(handshake), 'select after handshake');
  assert.ok(!steps.some((s) => s.kind === 'op' && s.args?.site === 'shortcut'), 'no shortcut priming');
  assert.ok(!steps.some((s) => s.kind === 'op' && s.args?.site === 'main-menu'), 'keep the parameterized open path');
  const opens = uiOps(doc, 'open');
  assert.ok(opens.length >= 1);
  assert.ok(opens.every((s) => s.args.site === '@site'));
  const notes = (doc.notes ?? []).join('\n');
  assert.match(notes, /recovery/i);
  assert.match(notes, /firstAuthoritative\.seenAt/);
  assert.match(notes, /inventoryRestored\.seenAt/);
  assert.match(notes, /handshake-ok/);
  assert.match(notes, /shortcut/i);
});

test('no shipped case uses the removed consentClose step or consent-close snippet', () => {
  // Server-to-client mod distribution and its join-time consent prompt were removed (2026-10-05).
  const bad = [];
  for (const { label, doc } of loadShippedCases()) {
    for (const step of caseSteps(doc)) {
      if (step.kind === 'consentClose') bad.push(`${label}: consentClose step ${JSON.stringify(step.name ?? '')}`);
      if (/consent-close/i.test(String(step.file ?? ''))) bad.push(`${label}: ${step.kind} ${step.file}`);
    }
    const prose = [doc.summary ?? '', ...(doc.notes ?? [])].join('\n');
    if (/consent/i.test(prose)) bad.push(`${label}: summary/notes still mention consent`);
  }
  assert.deepEqual(bad, [], bad.join('\n'));
  assert.equal(STEP_KINDS.includes('consentClose'), false);
});

test('ui-disconnect-only and ui-reconnect-only are the stock leave/rejoin ops ending on the handshake checks', () => {
  const leave = uiByName('ui-disconnect-only');
  assert.equal(leave.world, 'any');
  assert.equal(leave.endWorld, 'main-menu');
  const leaveSteps = leave.steps ?? [];
  const latchPoll = leaveSteps.find((s) => s.kind === 'op' && s.op === 'disconnect' && s.args?.statusOnly === true);
  assert.ok(latchPoll, 'must poll disconnect statusOnly before requesting a leave');
  assert.equal(latchPoll.expect?.requested, false);
  assert.equal(latchPoll.expect?.state?.neq, 'superseded');
  const leaveOp = leaveSteps.find((s) => s.kind === 'op' && s.op === 'disconnect' && s.args?.statusOnly !== true);
  assert.ok(leaveOp);
  assert.equal(leaveOp.record, 'beforeLeave');
  assert.equal(leaveOp.expect?.requested, true);
  assert.equal(leaveOp.expect?.state?.neq, 'superseded');
  assert.ok(leaveSteps.indexOf(latchPoll) < leaveSteps.indexOf(leaveOp), 'statusOnly poll before the leave request');
  assert.ok(leaveSteps.some((s) => s.kind === 'op' && s.op === 'ping' && /Client_0/.test(String(s.expect))));
  assert.ok(leaveSteps.some((s) => s.kind === 'log' && /cleared reason=disconnect/.test(String(s.regex ?? ''))));
  assert.equal(leaveSteps.filter((s) => s.kind === 'op' && s.op === 'connect').length, 0);
  const cleared = leaveSteps.findIndex((s) => s.kind === 'log' && /cleared reason=disconnect/.test(String(s.regex ?? '')));
  const lastLeave = leaveSteps[leaveSteps.length - 1];
  assert.ok(leaveSteps.length - 1 > cleared, 'status checks follow the inventory-cleared handshake');
  assert.equal(lastLeave.op, 'mods_ui');
  assert.equal(lastLeave.expect?.['after.serverList.state'], 'unavailable');

  const join = uiByName('ui-reconnect-only');
  assert.equal(join.world, 'main-menu');
  assert.equal(join.endWorld, 'connected');
  const joinSteps = join.steps ?? [];
  const connect = joinSteps.find((s) => s.kind === 'op' && s.op === 'connect');
  assert.ok(connect);
  assert.match(String(connect.args?.address ?? ''), /127\.0\.0\.1/);
  assert.equal(connect.args?.port ?? 9976, 9976);
  assert.ok(joinSteps.some((s) => s.kind === 'op' && s.op === 'ping' && /Client_0/.test(String(s.expect))));
  assert.ok(joinSteps.some((s) => s.kind === 'log' && /restored reason=handshake-ok/.test(String(s.regex ?? ''))));
  assert.equal(joinSteps.filter((s) => s.kind === 'op' && s.op === 'disconnect').length, 0);
  const lastJoin = joinSteps[joinSteps.length - 1];
  assert.equal(lastJoin.op, 'users');
  assert.match(String(lastJoin.expect), /handshake/);
  assert.match((join.notes ?? []).join('\n'), /Lidgren|protocol/);
});

test('ui-main-menu-set disconnects then documents the main-menu sequence without inlining soak', () => {
  const doc = uiByName('ui-main-menu-set');
  assert.deepEqual(doc.sequence, [
    'ui-soak-menu',
    'ui-core-swap-main-menu',
    'ui-open-close-cycles',
    'ui-reconnect-only',
  ]);
  const steps = doc.steps ?? [];
  assert.ok(steps.some((s) => s.kind === 'op' && s.op === 'disconnect'));
  assert.ok(steps.some((s) => s.kind === 'op' && s.op === 'ping' && /Client_0/.test(String(s.expect))));
  assert.equal(steps.some((s) => s.kind === 'soak'), false, 'do not inline ui-soak-menu');
  assert.equal(steps.filter((s) => s.kind === 'coreSwap').length, 0);
  assert.equal(steps.filter((s) => s.kind === 'op' && s.op === 'connect').length, 0);
  assert.match((doc.notes ?? []).join('\n'), /--site main-menu/);
  assert.match((doc.notes ?? []).join('\n'), /127\.0\.0\.1:9976/);
});

/** Documented `mods_ui` envelope / status / detail field names (api.md + live ModsUiResult). */
const MODS_UI_ENVELOPE = new Set(['ok', 'action', 'before', 'after', 'detail', 'elapsedMs']);
const MODS_UI_STATUS = new Set([
  'open', 'site', 'plugin', 'page', 'pageId', 'search', 'entries',
  'curtain', 'modsCapture', 'inputCapture', 'capture',
  'width', 'height', 'mode', 'panes', 'observers',
  'serverList', 'serverListEpoch',
  'shortcut', 'scroll',
  'suspended', 'suspendReason',
]);
const MODS_UI_DETAIL = new Set([
  'error', 'readback', 'path', 'handler', 'alreadyOpen', 'menuOpenedBy', 'text', 'pane', 'count', 'rows',
  'to', 'normalized', 'tab', 'same', 'requestedWidth', 'requestedHeight', 'requestedMode',
  'note', 'invoked', 'label', 'index', 'plugin', 'page', 'group', 'source',
  'controls', 'rects', 'visible', 'interactable', 'occludedBy', 'selector',
  'x', 'y', 'width', 'height',
  'clientWidth', 'clientHeight', 'clientX', 'clientY',
  'screenWidth', 'screenHeight', 'fullScreenMode', 'displayWidth', 'displayHeight', 'mode',
]);
const MODS_UI_EXPECT_DEPRECATED = new Set(['capture']);

function modsUiExpectPathOk(path) {
  const parts = String(path).split('.');
  const root = parts[0];
  if (parts.length === 1) return MODS_UI_ENVELOPE.has(root);
  if (root === 'before' || root === 'after') {
    return parts.length >= 2 && MODS_UI_STATUS.has(parts[1]);
  }
  if (root === 'detail') {
    return parts.length >= 2 && MODS_UI_DETAIL.has(parts[1]);
  }
  return false;
}

test('every cases-ui mods_ui expect path names a documented status/detail field', () => {
  const ui = loadShippedCases().filter((c) => c.label.startsWith('cases-ui/'));
  const unknown = [];
  const deprecated = [];
  for (const { label, doc } of ui) {
    for (const step of caseSteps(doc)) {
      if (step.kind !== 'op' || step.op !== 'mods_ui') continue;
      if (step.expect == null || typeof step.expect !== 'object' || Array.isArray(step.expect)) continue;
      for (const path of Object.keys(step.expect)) {
        if (!modsUiExpectPathOk(path)) unknown.push(`${label} ${step.name ?? step.args?.action}: ${path}`);
        const parts = String(path).split('.');
        if ((parts[0] === 'before' || parts[0] === 'after') && MODS_UI_EXPECT_DEPRECATED.has(parts[1])) {
          deprecated.push(`${label} ${step.name ?? step.args?.action}: ${path} is deprecated; use after.modsCapture`);
        }
      }
    }
  }
  assert.deepEqual(unknown, [], `unknown mods_ui expect paths:\n${unknown.join('\n')}`);
  assert.deepEqual(deprecated, [], `deprecated mods_ui expect paths (capture is a global alias; cases must expect modsCapture):\n${deprecated.join('\n')}`);
});

test('cases-ui mods_ui args match the live op contract', () => {
  const ui = loadShippedCases().filter((c) => c.label.startsWith('cases-ui/'));
  for (const { label, doc } of ui) {
    let open = false;
    for (const step of caseSteps(doc)) {
      if (step.kind !== 'op' || step.op !== 'mods_ui') continue;
      const action = step.args?.action;
      const expect = step.expect && typeof step.expect === 'object' && !Array.isArray(step.expect)
        ? step.expect
        : null;
      if (expect) {
        assert.equal(expect['after.paneCount'], undefined, `${label}: ${step.name} must not use after.paneCount`);
        assert.equal(expect['after.resolution.width'], undefined, `${label}: ${step.name} must not use after.resolution`);
        assert.equal(expect['after.capture'], undefined, `${label}: ${step.name} after.capture is deprecated; use after.modsCapture`);
        assert.equal(expect['before.capture'], undefined, `${label}: ${step.name} before.capture is deprecated; use modsCapture`);
        if (expect['after.inputCapture'] === false) {
          assert.fail(`${label}: ${step.name} must not expect after.inputCapture false (use after.modsCapture)`);
        }
      }
      if (action === 'open') open = true;
      if (action === 'close' || action === 'native_tab') open = false;
      if (action === 'select') {
        assert.ok(open, `${label}: ${step.name} select only after open`);
        assert.ok(['overview', 'settings', 'custom'].includes(step.args?.page), `${label}: ${step.name} select.page`);
      }
      if (action === 'search') {
        assert.ok(Object.prototype.hasOwnProperty.call(step.args ?? {}, 'text'), `${label}: ${step.name} search uses args.text`);
        assert.equal(step.args?.query, undefined, `${label}: ${step.name} must not use args.query`);
      }
      if (action === 'scroll') {
        assert.ok(step.args?.pane === 'list' || step.args?.pane === 'content', `${label}: ${step.name} scroll pane`);
        assert.ok(step.args?.to != null, `${label}: ${step.name} scroll uses args.to`);
      }
      if (action === 'native_tab') {
        assert.equal(step.args?.which, undefined, `${label}: ${step.name} must not use args.which`);
        assert.ok(step.args?.same === true || step.args?.tab != null, `${label}: ${step.name} native_tab needs same or tab`);
      }
      if (action === 'resolution') {
        assert.ok(step.args?.width != null, `${label}: ${step.name} resolution width`);
        assert.ok(step.args?.height != null, `${label}: ${step.name} resolution height`);
        assert.ok(step.args?.mode != null, `${label}: ${step.name} resolution mode`);
      }
      if (action === 'open') {
        assert.notEqual(step.args?.site, 'shortcut', `${label}: ${step.name} must not use shortcut; top-tab open brings up the fullscreen menu`);
        if (step.args?.site === 'top-tab' && expect) {
          assert.notEqual(
            expect['detail.menuOpenedBy'],
            undefined,
            `${label}: ${step.name} top-tab open must record detail.menuOpenedBy`,
          );
        }
      }
      if (action === 'close' && expect) {
        assert.equal(expect['after.open'], false, `${label}: ${step.name} close after.open`);
        assert.equal(expect['after.modsCapture'], false, `${label}: ${step.name} close after.modsCapture`);
        assert.equal(expect['after.capture'], undefined, `${label}: ${step.name} close must not expect after.capture`);
        assert.equal(expect['after.inputCapture'], undefined, `${label}: ${step.name} close must not expect after.inputCapture`);
        assert.equal(expect['after.panes'], '@record:baselineResolution.after.panes', `${label}: ${step.name} close after.panes`);
      }
      if (action === 'widget_read' && step.args?.pane != null && step.args.pane !== '') {
        assert.ok(step.args.pane === 'list' || step.args.pane === 'content', `${label}: ${step.name} widget pane`);
      }
    }
  }
});

test('ui-list-and-search exercises back; ui-native-tab-return keeps native_tab', () => {
  const list = uiByName('ui-list-and-search');
  assert.ok(uiOps(list, 'back').length >= 1, 'list-and-search must include a back step');
  assert.equal(uiOps(list, 'native_tab').length, 0);
  const native = uiByName('ui-native-tab-return');
  assert.ok(uiOps(native, 'native_tab').length >= 6);
  assert.equal(uiOps(native, 'back').length, 0, 'native-tab-return keeps native_tab');
  assert.ok(uiOps(native, 'native_tab').some((s) => s.args?.same === true));
  assert.ok(uiOps(native, 'native_tab').some((s) => s.args?.tab != null));
});

test('parameterized UI cases open at @site', () => {
  for (const name of ['ui-list-and-search', 'ui-native-tab-return', 'ui-open-close-cycles', 'ui-disconnect-reconnect', 'ui-resolutions']) {
    const doc = uiByName(name);
    assert.equal(doc.site, '@site', name);
    const opens = uiOps(doc, 'open');
    assert.ok(opens.length > 0, name);
    assert.ok(opens.every((s) => s.args.site === '@site'), name);
  }
});

test('ui-native-tab-return captures after each native_tab and after the final close', () => {
  const doc = uiByName('ui-native-tab-return');
  const steps = doc.steps ?? [];
  const nativeTabs = steps.filter((s) => s.kind === 'op' && s.args?.action === 'native_tab');
  assert.equal(nativeTabs.length, 6);
  for (const step of nativeTabs) {
    const idx = steps.indexOf(step);
    assert.equal(steps[idx + 1]?.kind, 'capture', `capture after ${step.name}`);
  }
  const captures = steps.filter((s) => s.kind === 'capture');
  assert.equal(captures.length, 7);
  assert.equal(steps[steps.length - 1]?.kind, 'capture');
  assert.equal(steps[steps.length - 1]?.name, 'after final close');
});

const FAKE_CORE_HASH = 'aa'.repeat(32);
const FAKE_DEV_HASH = 'bb'.repeat(32);

function resolvedSiteForUiCase(doc, extra = {}) {
  const detected = extra.detectedWorld
    ?? (doc.world === 'main-menu' ? CASE_WORLD_MAIN_MENU : CASE_WORLD_CONNECTED);
  return extra.site ?? resolveRunSite({ site: extra.cliSite }, detected, doc);
}

function ctxForUiCase(tmp, world, doc, site) {
  return {
    bridge: testBridge(tmp.dir, world),
    clock: fakeClock(),
    side: 'client',
    records: {},
    findings: [],
    stepName: doc.name,
    caseSite: site ?? resolvedSiteForUiCase(doc),
    caseDoc: doc,
    outDir: tmp.dir,
  };
}

async function runUiCaseSteps(doc, world, extra = {}) {
  const tmp = tempDir();
  const site = resolvedSiteForUiCase(doc, extra);
  const ctx = ctxForUiCase(tmp, world, doc, site);
  try {
    for (const step of [...(doc.steps ?? []), ...(doc.teardown ?? [])]) {
      if (step.kind === 'capture' || step.kind === 'capturePrepare' || step.kind === 'snippetPrepare') continue;
      const result = await executeStep(applyOpenSitePlaceholder(step, site), ctx);
      assert.equal(result.ok, true, `${doc.name} ${step.name ?? step.kind}: ${result.error}`);
    }
    return { ctx, tmp, site };
  } catch (err) {
    tmp.cleanup();
    throw err;
  }
}

function assertCoreSwapRecord(record, { before, after, core }) {
  assert.equal(record.ok, true, record.error);
  assert.equal(record.generationBefore, before);
  assert.equal(record.generationAfter, after);
  assert.equal(record.lastReloadOutcome, 'ok');
  assert.equal(record.previousAlive, false);
  assert.equal(record.inFlight, false);
  assert.ok(record.imageHashes);
  assert.equal(record.imageHashes.core, core);
  assert.ok(Object.prototype.hasOwnProperty.call(record, 'refusalWindow'));
}

test('ui-core-swap-open runs three coreSwap cycles against the fake', async () => {
  const doc = uiByName('ui-core-swap-open');
  const world = createFakeWorld({
    coreHash: FAKE_CORE_HASH,
    devHash: FAKE_DEV_HASH,
    generation: 3,
  });
  const { ctx, tmp } = await runUiCaseSteps(doc, world);
  try {
    assertCoreSwapRecord(ctx.records.coreSwap1, { before: 3, after: 4, core: FAKE_CORE_HASH });
    assertCoreSwapRecord(ctx.records.coreSwap2, { before: 4, after: 5, core: FAKE_CORE_HASH });
    assertCoreSwapRecord(ctx.records.coreSwap3, { before: 5, after: 6, core: FAKE_CORE_HASH });
    assert.ok(world.calls.some((c) => c.op === 'core_reload'));
    assert.ok(world.calls.some((c) => c.op === 'reload_status'));
  } finally {
    tmp.cleanup();
  }
});

test('ui-core-swap-main-menu runs three coreSwap cycles against the fake', async () => {
  const doc = uiByName('ui-core-swap-main-menu');
  const world = createFakeWorld({
    coreHash: FAKE_CORE_HASH,
    devHash: FAKE_DEV_HASH,
    generation: 7,
    pingExtra: { worlds: ['Default World'] },
  });
  const { ctx, tmp } = await runUiCaseSteps(doc, world);
  try {
    assertCoreSwapRecord(ctx.records.coreSwap1, { before: 7, after: 8, core: FAKE_CORE_HASH });
    assertCoreSwapRecord(ctx.records.coreSwap2, { before: 8, after: 9, core: FAKE_CORE_HASH });
    assertCoreSwapRecord(ctx.records.coreSwap3, { before: 9, after: 10, core: FAKE_CORE_HASH });
    assert.ok(!world.calls.some((c) => c.op === 'users'));
  } finally {
    tmp.cleanup();
  }
});

test('ui-disconnect-only, ui-reconnect-only, and ui-main-menu-set run against the fake', async () => {
  const leave = uiByName('ui-disconnect-only');
  const leaveWorld = createFakeWorld();
  const { tmp: leaveTmp } = await runUiCaseSteps(leave, leaveWorld);
  try {
    const disconnectCalls = leaveWorld.calls.filter((c) => c.op === 'disconnect');
    assert.ok(disconnectCalls[0]?.args?.statusOnly === true, 'first disconnect is statusOnly');
    assert.ok(disconnectCalls.some((c) => c.args?.statusOnly !== true), 'must request a leave');
    assert.ok(!leaveWorld.calls.some((c) => c.op === 'connect'));
    assert.ok(!leaveWorld.clientWorlds.includes('Client_0'));
  } finally {
    leaveTmp.cleanup();
  }

  const join = uiByName('ui-reconnect-only');
  const joinWorld = createFakeWorld({
    clientWorlds: ['Default World'],
    serverContextLive: false,
  });
  const { tmp: joinTmp } = await runUiCaseSteps(join, joinWorld);
  try {
    assert.ok(joinWorld.calls.some((c) => c.op === 'connect'));
    assert.match(String(joinWorld.calls.find((c) => c.op === 'connect')?.args?.address ?? ''), /127\.0\.0\.1/);
    assert.equal(joinWorld.calls.find((c) => c.op === 'connect')?.args?.port, 9976);
  } finally {
    joinTmp.cleanup();
  }

  const set = uiByName('ui-main-menu-set');
  const setWorld = createFakeWorld();
  const { tmp: setTmp } = await runUiCaseSteps(set, setWorld);
  try {
    assert.ok(setWorld.calls.some((c) => c.op === 'disconnect'));
    assert.ok(!setWorld.calls.some((c) => c.op === 'connect'));
  } finally {
    setTmp.cleanup();
  }
});

test('ui-disconnect-only polls statusOnly until a leftover leave latch is cleared then requests a leave', async () => {
  const doc = uiByName('ui-disconnect-only');
  const world = createFakeWorld({
    leaveRequested: true,
    leaveEpoch: 1,
    clientWorldEpoch: 2,
  });
  assert.equal(world.leaveRequested, true);
  assert.ok(world.clientWorlds.includes('Client_0'));
  const { tmp } = await runUiCaseSteps(doc, world);
  try {
    const disconnectCalls = world.calls.filter((c) => c.op === 'disconnect');
    const statusOnly = disconnectCalls.filter((c) => c.args?.statusOnly === true);
    const leave = disconnectCalls.filter((c) => c.args?.statusOnly !== true);
    assert.ok(statusOnly.length >= 2, `superseded observe then idle latch, got ${statusOnly.length} statusOnly`);
    assert.equal(statusOnly[0].args.statusOnly, true);
    assert.equal(leave.length, 1, 'exactly one leave request after the latch is clear');
    assert.ok(disconnectCalls.indexOf(statusOnly[0]) < disconnectCalls.indexOf(leave[0]));
    assert.ok(!world.clientWorlds.includes('Client_0'));
    assert.equal(world.leaveRequested, true);
    assert.ok(!world.calls.some((c) => c.op === 'connect'));
  } finally {
    tmp.cleanup();
  }
});

test('ui-disconnect-reconnect runs against the fake inventory clear/restore lines', async () => {
  const doc = uiByName('ui-disconnect-reconnect');
  const world = createFakeWorld();
  const { ctx, tmp } = await runUiCaseSteps(doc, world);
  try {
    assert.match(String(ctx.records.inventoryCleared?.text ?? ''), /server context cleared reason=disconnect/);
    assert.match(String(ctx.records.inventoryRestored?.text ?? ''), /server context restored reason=handshake-ok/);
    assert.equal(typeof ctx.records.firstAuthoritative?.seenAt, 'number');
    assert.equal(typeof ctx.records.inventoryRestored?.seenAt, 'number');
    const recoveryMs = ctx.records.firstAuthoritative.seenAt - ctx.records.inventoryRestored.seenAt;
    assert.ok(recoveryMs >= 0, `recovery interval should be firstAuthoritative.seenAt - inventoryRestored.seenAt, got ${recoveryMs}`);
    assert.ok(world.calls.some((c) => c.op === 'disconnect'));
    assert.ok(world.calls.some((c) => c.op === 'connect'));
    assert.ok(!world.calls.some((c) => c.op === 'mods_ui' && c.args?.site === 'shortcut'));
    assert.ok(!world.calls.some((c) => c.op === 'mods_ui' && c.args?.site === '@site'));
    assert.ok(world.calls.some((c) => c.op === 'mods_ui' && c.args?.action === 'open' && c.args?.site === 'top-tab'));
    assert.equal(world.modsUi.open, false);
    assert.equal(world.modsUi.modsCapture, false);
  } finally {
    tmp.cleanup();
  }
});

test('parameterized UI cases substitute @site against the fake world', async () => {
  for (const name of ['ui-list-and-search', 'ui-native-tab-return', 'ui-open-close-cycles', 'ui-resolutions']) {
    const doc = uiByName(name);
    const world = createFakeWorld();
    const { tmp, site } = await runUiCaseSteps(doc, world);
    try {
      assert.equal(site, 'top-tab', name);
      assert.ok(world.calls.some((c) => c.op === 'mods_ui' && c.args?.action === 'open' && c.args?.site === 'top-tab'), name);
      assert.ok(!world.calls.some((c) => c.args?.site === '@site'), name);
    } finally {
      tmp.cleanup();
    }
  }
});

test('ui-settings polls async ExampleLimit notes against the fake', async () => {
  const doc = uiByName('ui-settings');
  const world = createFakeWorld();
  const { ctx, tmp } = await runUiCaseSteps(doc, world);
  try {
    assert.match(JSON.stringify(ctx.records.nonAdminRefusal), /Only server administrators can change this setting/);
    assert.match(JSON.stringify(ctx.records.adminSaved), /Saved by the server/);
    assert.match(JSON.stringify(ctx.records.boundsRefusal), /Outside the values this setting accepts/);
    assert.match(JSON.stringify(ctx.records.adminSaved11), /Saved by the server/);
    assert.match(JSON.stringify(ctx.records.restoreSaved), /Saved by the server/);
    assert.match(JSON.stringify(ctx.records.teardownRestoreSaved), /Saved by the server/);
    assert.ok(ctx.records.nonAdminRefusal.elapsedMs >= 500, ctx.records.nonAdminRefusal.elapsedMs);
    assert.ok(ctx.records.adminSaved.elapsedMs >= 500, ctx.records.adminSaved.elapsedMs);
    assert.ok(ctx.records.boundsRefusal.elapsedMs >= 500, ctx.records.boundsRefusal.elapsedMs);
    assert.ok(ctx.records.adminSaved11.elapsedMs >= 500, ctx.records.adminSaved11.elapsedMs);
    assert.ok(ctx.records.restoreSaved.elapsedMs >= 500, ctx.records.restoreSaved.elapsedMs);
    assert.ok(ctx.records.teardownRestoreSaved.elapsedMs >= 500, ctx.records.teardownRestoreSaved.elapsedMs);
    assert.ok((ctx.records.nonAdminRefusal.polls ?? 0) >= 2);
    assert.ok((ctx.records.restoreSaved.polls ?? 0) >= 2);
    assert.ok((ctx.records.teardownRestoreSaved.polls ?? 0) >= 2);
    assert.equal(world.modsUi.values.ExampleLimit, '10');
    assert.equal(world.modsUi.committedExampleLimit, '10');
  } finally {
    tmp.cleanup();
  }
});

test('fake restore 10 does not commit if admin is dropped while the note is pending', async () => {
  const world = createFakeWorld();
  world.users[0].admin = true;
  const tmp = tempDir();
  const ctx = ctxForUiCase(tmp, world, { name: 'restore-race' }, 'top-tab');
  try {
    for (const step of [
      { kind: 'op', side: 'client', op: 'mods_ui', args: { action: 'open', site: 'top-tab' } },
      { kind: 'op', side: 'client', op: 'mods_ui', args: { action: 'select', plugin: 'progenitor.sample', page: 'settings', group: 'Server' } },
      { kind: 'op', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', index: 3, value: '11' } },
      { kind: 'op', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', label: 'Apply ExampleLimit' } },
      { kind: 'op', side: 'client', op: 'mods_ui', args: { action: 'widget_read', pane: 'content' }, poll: { intervalMs: 500, timeoutMs: 10000 }, expect: 'Saved by the server' },
      { kind: 'op', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', index: 3, value: '10' } },
      { kind: 'op', side: 'client', op: 'mods_ui', args: { action: 'widget_click', pane: 'content', label: 'Apply ExampleLimit' } },
      { kind: 'op', side: 'server', op: 'set', args: { entity: world.users[0].entity, component: 'User', fields: { IsAdmin: false } } },
    ]) {
      const result = await executeStep(step, ctx);
      assert.equal(result.ok, true, result.error);
    }
    assert.equal(world.modsUi.committedExampleLimit, '11');
    const pending = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'widget_read', pane: 'content' },
      expect: 'Saved by the server',
    }, ctx);
    assert.equal(pending.ok, false, 'Saved must not appear after admin was dropped mid-apply');
    assert.equal(world.modsUi.committedExampleLimit, '11');
  } finally {
    tmp.cleanup();
  }
});

test('OS-injected input cases use input/assertWindow, os-injected labels, and windowed assertWindow', () => {
  const names = [
    'input-entry-buttons',
    'input-list-and-scroll',
    'input-settings',
    'input-native-tab-return',
    'input-shortcut-and-escape',
    'input-resolutions',
  ];
  for (const name of names) {
    const doc = uiByName(name);
    assert.ok(STEP_KINDS.includes('input'));
    assert.ok(STEP_KINDS.includes('assertWindow'));
    for (const step of caseSteps(doc)) {
      if (step.kind === 'input') {
        assert.equal(step.label, 'os-injected', `${name}: ${step.name} label`);
        assert.ok(['click', 'move', 'scroll', 'key', 'type'].includes(step.action), `${name}: ${step.name} action`);
        assert.notEqual(step.label, 'handler-invocation');
        assert.notEqual(step.label, 'physical');
      }
    }
  }

  const entry = uiByName('input-entry-buttons');
  assert.equal(entry.site, '@site');
  const entryClicks = (entry.steps ?? []).filter((s) => s.kind === 'input' && s.action === 'click');
  assert.ok(entryClicks.some((s) => s.target?.rectOf === '@site'));
  assert.ok(entryClicks.some((s) => s.target?.rectOf === 'close'));

  const list = uiByName('input-list-and-scroll');
  const listClicks = (list.steps ?? []).filter((s) => s.kind === 'input');
  assert.ok(listClicks.some((s) => s.target?.rectOf === 'sidebarRow:progenitor.sample'));
  assert.ok(listClicks.some((s) => s.target?.rectOf === 'scrollbar:list:handle'));
  assert.ok(listClicks.some((s) => s.target?.rectOf === 'scrollbar:content:handle'));
  assert.ok((list.steps ?? []).some((s) => s.expect && s.expect['after.scroll.list']));
  assert.ok((list.steps ?? []).some((s) => s.expect && s.expect['after.scroll.content']));

  const settings = uiByName('input-settings');
  const settingsInputs = (settings.steps ?? []).filter((s) => s.kind === 'input');
  assert.ok(settingsInputs.some((s) => s.target?.rectOf === 'widget:ExampleLimit:input'));
  assert.ok(settingsInputs.some((s) => s.action === 'type' && s.text === '12'));
  assert.ok(settingsInputs.some((s) => s.target?.rectOf === 'widget:ExampleLimit:apply'));
  assertAdminApplyPolledBeforeDrop(settings, 'Apply 10 restore', /Saved by the server/);
  assertAdminApplyPolledBeforeDrop(settings, 'teardown Apply 10', /Saved by the server/);

  const native = uiByName('input-native-tab-return');
  const spellbook = (native.steps ?? []).filter((s) => s.kind === 'input' && s.target?.rectOf === 'nativeTab:Spellbook');
  assert.equal(spellbook.length, 2);

  const shortcut = uiByName('input-shortcut-and-escape');
  const keys = (shortcut.steps ?? []).filter((s) => s.kind === 'input' && s.action === 'key');
  assert.ok(keys.some((s) => String(s.keys).includes('baselineResolution.after.shortcut') || s.keys === '@record:baselineResolution.after.shortcut'));
  assert.ok(keys.some((s) => s.keys === 'Escape'));
  assert.equal(shortcut.shortcut, 'F10', 'shortcut case must name the chord fallback when status.shortcut is absent');
  const zero = (shortcut.steps ?? []).find((s) => s.kind === 'log' && s.count === 0);
  assert.ok(zero, 'shortcut case must count 0 keybind/ability lines');
  assert.equal(zero.sinceRecord, 'typedInSearch');
  assert.match(String(zero.regex), /keybind|ability/);

  const res = uiByName('input-resolutions');
  const windowed = (res.steps ?? []).filter((s) => s.kind === 'op' && s.args?.action === 'resolution' && s.args?.mode === 'windowed');
  assert.ok(windowed.some((s) => s.args.width === 1280 && s.args.height === 720));
  assert.ok(windowed.some((s) => s.args.width === 1920 && s.args.height === 1080));
  const asserts = (res.steps ?? []).filter((s) => s.kind === 'assertWindow');
  assert.ok(asserts.some((s) => s.expect?.clientWidth === 1280 && s.expect?.clientHeight === 720 && s.expect?.mode === 'windowed'));
  assert.ok(asserts.some((s) => s.expect?.clientWidth === 1920 && s.expect?.clientHeight === 1080 && s.expect?.mode === 'windowed'));
  const baselineAssert = asserts.find((s) => s.name === 'assertWindow baseline');
  assert.ok(baselineAssert, 'input-resolutions must assertWindow the restored baseline');
  assert.match(String(baselineAssert.expect?.clientWidth), /detail\.client\.width/, 'baseline assertWindow uses nested detail.client.width');
  assert.match(String(baselineAssert.expect?.clientHeight), /detail\.client\.height/, 'baseline assertWindow uses nested detail.client.height');
  assert.equal(String(baselineAssert.expect?.mode).includes('clientWidth'), false);
  const rects = (res.steps ?? []).filter((s) => s.kind === 'op' && s.args?.action === 'rects');
  assert.ok(rects.some((s) => s.args.pane === 'list'));
  assert.ok(rects.some((s) => s.args.pane === 'content'));
  const resSteps = res.steps ?? [];
  const idx = (pred) => resSteps.findIndex(pred);
  for (const size of ['720', '1080']) {
    const openAt = idx((s) => s.kind === 'op' && s.args?.action === 'open' && String(s.name ?? '').includes(size));
    const captures = resSteps
      .map((s, i) => ({ s, i }))
      .filter(({ s }) => s.kind === 'capture' && String(s.name ?? '').includes(size));
    assert.ok(openAt >= 0, `input-resolutions must open at ${size}`);
    assert.ok(captures.length >= 2, `input-resolutions must capture both panes at ${size}`);
    assert.ok(captures.every(({ i }) => i > openAt), `input-resolutions must capture ${size} after open`);
    assert.ok(captures.some(({ s }) => /list/i.test(String(s.name))), `input-resolutions ${size} list pane capture`);
    assert.ok(captures.some(({ s }) => /content/i.test(String(s.name))), `input-resolutions ${size} content pane capture`);
  }
});

