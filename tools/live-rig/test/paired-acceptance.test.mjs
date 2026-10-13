import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { STEP_KINDS } from '../lib/steps.mjs';
import { TOOL_DIR } from './helpers.mjs';

function loadCase(name) {
  const file = path.join(TOOL_DIR, 'cases', `${name}.json`);
  assert.ok(existsSync(file), `expected ${file}`);
  const doc = JSON.parse(readFileSync(file, 'utf8'));
  return { file, doc };
}

function allSteps(doc) {
  return [...(doc.steps ?? []), ...(doc.teardown ?? [])];
}

test('paired acceptance cases use known step kinds and track cleanup', () => {
  for (const name of ['m2-12-key-table', 'm2-4-crystal-maxamount', 'm2-12-incompatible-client']) {
    const { doc } = loadCase(name);
    assert.equal(doc.name, name);
    assert.equal(doc.world, 'connected');
    assert.match(String(doc.notes?.join('\n') ?? ''), /incomplete/i);
    for (const step of allSteps(doc)) {
      assert.ok(STEP_KINDS.includes(step.kind), `${name}: unknown step kind ${JSON.stringify(step.kind)}`);
    }
    const pings = allSteps(doc).filter((s) => s.kind === 'op' && s.op === 'ping' && s.expect != null);
    assert.ok(pings.length >= 2, `${name}: expected ping stamps`);
    for (const step of pings) {
      assert.equal(step.expect.ok, undefined, `${name}: ping has no ok field`);
      assert.ok(step.expect.side === 'server' || step.expect.side === 'client', `${name}: ping expect.side`);
    }
  }
});

test('m2-12-key-table encodes PrfI-before-PrfD, matching indices, ember guid, and define', () => {
  const { doc } = loadCase('m2-12-key-table');
  const steps = doc.steps ?? [];
  assert.ok(steps.some((s) => s.kind === 'log' && s.side === 'server' && /on-connect PrfI/.test(String(s.regex))));
  assert.ok(steps.some((s) => s.kind === 'log' && s.side === 'client' && /accepted server key-table order/.test(String(s.regex))));
  assert.ok(steps.some((s) => s.kind === 'log' && s.side === 'server' && /on-connect 'sample\\.ember'/.test(String(s.regex))));
  const emberLogs = steps.filter((s) => s.kind === 'log' && /applied 'sample\\.ember'/.test(String(s.regex)) && /index=/.test(String(s.regex)));
  assert.equal(emberLogs.length, 2);
  assert.ok(emberLogs.some((s) => s.side === 'server'));
  assert.ok(emberLogs.some((s) => s.side === 'client'));
  assert.ok(steps.some((s) => s.kind === 'command' && s.text === '.ember 1'));
  const inspect = steps.filter((s) => s.kind === 'op' && s.op === 'inspect');
  assert.equal(inspect.length, 2);
  assert.ok(inspect.every((s) => String(s.expect).includes('480926629')));
  assert.ok(steps.some((s) => s.kind === 'command' && s.text === '.content define'));
  const cinderLogs = steps.filter((s) => s.kind === 'log' && /sample\\.cinder/.test(String(s.regex)));
  assert.equal(cinderLogs.length, 2);
  assert.ok((doc.teardown ?? []).some((s) => s.text === '.content drop'));
});

test('m2-4-crystal-maxamount encodes B, 40, PrfE lines, swap, and failed-restore stop', () => {
  const { doc } = loadCase('m2-4-crystal-maxamount');
  const steps = doc.steps ?? [];
  assert.ok(steps.filter((s) => s.kind === 'removal').length >= 2);
  assert.ok(steps.some((s) => s.kind === 'reinstall'));
  assert.ok(steps.some((s) => s.kind === 'swap' && s.dll === '<path>'));
  assert.ok(steps.some((s) => s.kind === 'log' && /sent PrfE for -257494203/.test(String(s.regex))));
  assert.ok(steps.some((s) => s.kind === 'log' && /applied PrfE for -257494203/.test(String(s.regex))));
  const prefabOps = steps.filter((s) => s.kind === 'op' && s.op === 'prefab');
  assert.ok(prefabOps.length >= 6);
  assert.ok(prefabOps.every((s) => s.args?.prefab === -257494203 && s.args?.component === 'ProjectM.ItemData'));
  const forties = prefabOps.filter((s) => JSON.stringify(s.expect).includes('40'));
  assert.ok(forties.length >= 4);
  const restores = steps.filter((s) => s.record === 'crystalRestoredServer' || s.record === 'crystalRestoredClient');
  assert.equal(restores.length, 2);
  assert.ok(restores.every((s) => String(JSON.stringify(s.expect)).includes('@record:crystalBaseline')));
});

test('m2-12-incompatible-client encodes missing-ledger refusal not a dropped-packet pass', () => {
  const { doc } = loadCase('m2-12-incompatible-client');
  const notes = (doc.notes ?? []).join('\n');
  assert.match(notes, /Dropping PrfI is not compatibility|dropping PrfI is not compatibility/i);
  assert.match(notes, /unusable/);
  assert.match(notes, /OnClientKeyLedger/);
  const steps = doc.steps ?? [];
  const wrongImage = steps.find((s) => s.kind === 'op' && s.op === 'logs');
  assert.ok(wrongImage, 'early logs op aborts a matched pair');
  assert.match(JSON.stringify(wrongImage.args ?? {}), /accepted server key-table order/);
  assert.equal(wrongImage.expect?.count, 0);
  assert.ok(steps.some((s) => s.kind === 'log' && /on-connect PrfI/.test(String(s.regex))));
  assert.ok(steps.some((s) => s.kind === 'log' && /no PrfI key-table order was accepted/.test(String(s.regex))));
  assert.ok(steps.some((s) => s.kind === 'log' && /the item stays unusable/.test(String(s.regex))));
  assert.ok(!steps.some((s) => /dropped not passed/.test(String(s.regex ?? ''))));
});
