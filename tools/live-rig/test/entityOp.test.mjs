import test from 'node:test';
import assert from 'node:assert/strict';
import {
  ACTIONS,
  ACTION_FINALIZE,
  PHASE_DETACH,
  PHASE_BUFFS,
  PHASE_WAITING_BUFFS,
  PHASE_TAGGED,
  PHASE_STUCK,
  FINALIZE_PHASES,
  MAX_BUFF_CYCLES,
  ATTACHED_BUFFER_INSPECT,
  buildArgs,
  destroyRequest,
  existsRequest,
  finalizeRequest,
  inspectAttachedBufferRequest,
  isExpectedDestroyPhase,
  isLegacyDestroyContract,
  observeDestroyContract,
  parseResult,
  contractMismatchFinding,
} from '../lib/entityOp.mjs';

test('entity op actions include finalize and destroy still sends reason', () => {
  assert.deepEqual([...ACTIONS], ['exists', 'destroy', 'finalize']);
  assert.deepEqual(existsRequest('1:1'), { action: 'exists', entities: ['1:1'] });
  assert.deepEqual(destroyRequest(['1:1', '2:1']), {
    action: 'destroy',
    entities: ['1:1', '2:1'],
    reason: 'None',
  });
  assert.deepEqual(finalizeRequest(['1:1'], { '1:1': ['10:1', '11:1'] }), {
    action: ACTION_FINALIZE,
    entities: ['1:1'],
    buffs: { '1:1': ['10:1', '11:1'] },
  });
  assert.throws(() => buildArgs('nope', ['1:1']), /exists\|destroy\|finalize/);
});

test('destroy contract is buffs; detach or no phase is a mismatch', () => {
  assert.equal(isExpectedDestroyPhase({ entity: '1:1', exists: true }), false);
  assert.equal(isExpectedDestroyPhase({ entity: '1:1', phase: PHASE_BUFFS, buffs: [] }), true);
  assert.equal(isExpectedDestroyPhase({ entity: '1:1', phase: PHASE_DETACH, buffs: [] }), false);
  assert.equal(isLegacyDestroyContract({ entity: '1:1' }), true);
  assert.equal(isLegacyDestroyContract({ entity: '1:1', phase: PHASE_DETACH }), true);
  assert.equal(isLegacyDestroyContract({ entity: '1:1', phase: PHASE_BUFFS }), false);
  assert.equal(observeDestroyContract({ entity: '1:1' }), 'no phase');
  assert.equal(observeDestroyContract({ entity: '1:1', phase: PHASE_DETACH }), PHASE_DETACH);
  assert.equal(contractMismatchFinding('detach'), 'entity op contract mismatch: detach');
  assert.equal(contractMismatchFinding('no phase'), 'entity op contract mismatch: no phase');
  assert.equal(MAX_BUFF_CYCLES, 4);
  assert.deepEqual([...FINALIZE_PHASES], [
    PHASE_WAITING_BUFFS,
    PHASE_BUFFS,
    PHASE_TAGGED,
    PHASE_STUCK,
  ]);
  const parsed = parseResult({
    action: 'destroy',
    count: 1,
    entries: [{ entity: '1:1', phase: PHASE_BUFFS, buffs: ['2:1'], staleRows: [], buffSweepComplete: true }],
    errors: [],
  });
  assert.equal(parsed.entries[0].phase, PHASE_BUFFS);
  assert.deepEqual(parsed.entries[0].buffs, ['2:1']);
});

test('AttachedBuffer inspect args are only + depth 16', () => {
  assert.deepEqual(ATTACHED_BUFFER_INSPECT, { only: ['AttachedBuffer'], depth: 16 });
  assert.deepEqual(inspectAttachedBufferRequest('100:1'), {
    entity: '100:1',
    only: ['AttachedBuffer'],
    depth: 16,
  });
});
