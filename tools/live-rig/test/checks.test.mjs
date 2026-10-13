import test from 'node:test';
import assert from 'node:assert/strict';
import { parseProtectionLine, protection, PROTECTION_UNAVAILABLE } from '../lib/checks.mjs';
import { fakeClock } from '../lib/clock.mjs';

const OK = 'Debug: status character=331182:1 buff=566718:45 flags=7 immortal=True guard=True prefixes=3/3 drain=True aggro=True persists=True reconcileGaveUpGrants=0 reconcileGaveUpCharacters=(none) reconcilePassGaveUp=False reconcileThrowingPasses=0';
const OFF = 'Debug: status character=331182:1 buff=none flags=0 immortal=True guard=False prefixes=3/3 drain=False aggro=False persists=False reconcileGaveUpGrants=0 reconcileGaveUpCharacters=(none) reconcilePassGaveUp=False reconcileThrowingPasses=0';

test('protection parser accepts the armed Sample status line', () => {
  const r = parseProtectionLine(OK);
  assert.equal(r.ok, true);
  assert.equal(r.value.flags, 7);
  assert.equal(r.value.guard, true);
  assert.equal(r.value.prefixesA, 3);
  assert.equal(r.value.prefixesB, 3);
  assert.equal(r.value.drain, true);
  assert.equal(r.value.aggro, true);
  assert.equal(r.value.persists, true);
  assert.equal(r.evidence, OK);
});

test('protection parser rejects a dropped-buff status line', () => {
  const r = parseProtectionLine(OFF);
  assert.equal(r.ok, false);
  assert.equal(r.value.flags, 0);
  assert.equal(r.value.guard, false);
  assert.equal(r.value.drain, false);
  assert.equal(r.value.aggro, false);
  assert.equal(r.value.persists, false);
  assert.ok(r.evidence.includes('buff=none'));
});

test('protection parser requires flags=7 even when prefixes match', () => {
  const line = OK.replace('flags=7', 'flags=3');
  assert.equal(parseProtectionLine(line).ok, false);
});

test('protection parser does not accept extra flag bits unexplained by frost dash', () => {
  // Live catalog-cases-3: flags=68719477263 on the same debug buff while frost dash was on.
  // SampleFrostDash does not write BuffableFlagState, so the parser stays exact flags=7.
  const line = OK.replace('flags=7', 'flags=68719477263');
  const r = parseProtectionLine(line);
  assert.equal(r.ok, false);
  assert.equal(r.value.flags, 68719477263);
  assert.equal(r.value.guard, true);
});

const ADMIN_ONLY = 'This command is admin-only.';

test('protection parser never treats a non-admin refusal as guard=False', () => {
  const r = parseProtectionLine(ADMIN_ONLY);
  assert.equal(r.ok, false);
  assert.equal(r.unavailable, true);
  assert.equal(r.value, null);
  assert.equal(r.reason, PROTECTION_UNAVAILABLE);
  assert.ok(!Object.prototype.hasOwnProperty.call(r.value ?? {}, 'guard'));
});

test('protection check reports unavailable when the status command answers with a non-admin refusal', async () => {
  let seq = 40;
  const bridge = {
    async call(_side, op) {
      if (op === 'ping') return { logSeq: seq };
      if (op === 'command') {
        seq += 1;
        return { injectedAs: 'TestPlayer' };
      }
      if (op === 'logs') {
        return { latestSeq: seq, lines: [{ seq, text: ADMIN_ONLY }] };
      }
      throw new Error('unexpected op ' + op);
    },
  };
  const r = await protection({ bridge, user: 'TestPlayer', clock: fakeClock(), timeoutMs: 1000 });
  assert.equal(r.name, 'protection');
  assert.equal(r.ok, false);
  assert.equal(r.unavailable, true);
  assert.equal(r.value, null);
  assert.equal(r.evidence, PROTECTION_UNAVAILABLE);
});
