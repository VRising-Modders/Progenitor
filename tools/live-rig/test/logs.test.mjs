import test from 'node:test';
import assert from 'node:assert/strict';
import {
  classifyLogSignals,
  hasFatalLogSignals,
  LOG_SCAN_MAX_LINES,
  LOG_SCAN_FILTER,
} from '../lib/logs.mjs';

test('expected detach lines are counted and are not fatal', () => {
  const got = classifyLogSignals([
    { text: 'Found entity: 1:1 that has been destroyed without being detached. Info: Buff' },
    { text: 'DetachEntity information. Entity: 1:1 EntityInBuffer: 1:1 Parent: 2:1' },
  ]);
  assert.equal(got.expected.destroyedWithoutBeingDetached, 1);
  assert.equal(got.expected.detachEntityInformation, 1);
  assert.equal(got.fatal.triedToDetachAttachedBuffer, 0);
  assert.equal(got.fatalLine, null);
  assert.equal(hasFatalLogSignals({ fatal: got.fatal }), false);
  assert.equal(LOG_SCAN_MAX_LINES, 400);
  assert.equal(LOG_SCAN_FILTER, '');
});

test('Tried to detach with AttachedBuffer is fatal', () => {
  const got = classifyLogSignals([
    { text: 'Tried to detach entity 1:1 from AttachedBuffer' },
  ]);
  assert.equal(got.fatal.triedToDetachAttachedBuffer, 1);
  assert.match(got.fatalLine, /Tried to detach/);
  assert.equal(hasFatalLogSignals({ fatal: { ...got.fatal, bridgeUnanswered: 0 } }), true);
});

test('Tried to detach without AttachedBuffer is not the missing-row signal', () => {
  const got = classifyLogSignals([{ text: 'Tried to detach something else' }]);
  assert.equal(got.fatal.triedToDetachAttachedBuffer, 0);
});

test('buff.Target and Burst abort lines are fatal', () => {
  const target = classifyLogSignals([{ text: 'Job 4 points at buff.Target of a destroyed entity' }]);
  assert.equal(target.fatal.pointsAtBuffTarget, 1);
  const burst = classifyLogSignals([{ text: 'This Exception was thrown from a job compiled with Burst' }]);
  assert.equal(burst.fatal.burstException, 1);
});
