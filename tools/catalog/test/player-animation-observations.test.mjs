import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { summarize, runCli } from '../player-animation-observations.mjs';

const states = () => ({ controllers: [{ name: 'Base', archiveId: 'archive', pathID: '1', stateMachines: [{ stateMachineIndex: 0,
  states: [{ stateIndex: 0, name: 'Idle', fullPath: 'Base.Idle', serializedSettings: { m_FullPathID: 4294967295 } }] }] }] });
const layer = (index = 0, weight = 0, hash = -1) => ({ index, name: `Layer ${index}`, weight, fullPathHash: hash });
const row = (changes = {}) => ({ root: 'Male', instance: -2, active: true, controller: 'Override', avatar: 'Rig', layers: [layer()], ...changes });
const capture = (rows = [row()]) => [{ utc: '2026-10-08T18:52:11Z', frame: 0, rows }];

test('signed hashes retain every ambiguous serialized candidate', () => {
  const input = states(); input.controllers.push({ ...structuredClone(input.controllers[0]), archiveId: 'other' });
  const report = summarize(input, capture());
  const observed = report.profiles[0].states[0];
  assert.equal(observed.unsignedFullPathID, 4294967295);
  assert.equal(observed.status, 'ambiguous');
  assert.deepEqual(observed.candidates.map(candidate => candidate.archiveId), ['archive', 'other']);
  assert.match(report.caveat, /not complete/);
  assert.deepEqual(input, { controllers: [states().controllers[0], { ...states().controllers[0], archiveId: 'other' }] });
});

test('active rows and base layer zero weight contribute; zero overlay does not', () => {
  const report = summarize(states(), capture([row({ layers: [layer(), layer(1, 0), layer(2, 0.2, 7)] }), row({ instance: 3, active: false })]));
  assert.equal(report.inactiveRows, 1); assert.equal(report.excludedLayerSamples, 1);
  assert.equal(report.profiles.length, 1); assert.equal(report.profiles[0].sampleCount, 1);
  assert.equal(report.profiles[0].contributingLayerSampleCount, 2);
  assert.equal(report.unknownStates[0].unsignedFullPathID, 7);
  assert.equal(report.profiles[0].states[0].status, 'hash-match');
});

test('separates root, instance, controller and avatar profiles and counts samples', () => {
  const rows = [row(), row({ root: 'Female' }), row({ instance: 4 }), row({ controller: 'Other' }), row({ avatar: 'Other' })];
  const report = summarize(states(), [...capture(rows), { ...capture()[0], frame: 1, utc: '2026-10-08T18:52:12.123Z' }]);
  assert.equal(report.profiles.length, 5); assert.equal(report.profiles[0].sampleCount, 2);
  assert.equal(report.profiles[0].states[0].sampleCount, 2);
  assert.equal(report.profiles[0].firstObservedUtc, '2026-10-08T18:52:11Z');
  assert.equal(report.profiles[0].lastObservedUtc, '2026-10-08T18:52:12.123Z');
});

test('null animator references remain unavailable and cannot qualify a profile', () => {
  const report = summarize(states(), capture([row({ controller: null }), row({ instance: 3, avatar: null }),
    row({ instance: 4, controller: null, avatar: null, active: false }), row()]));
  assert.equal(report.unavailableProfileRows, 3);
  assert.equal(report.inactiveRows, 1);
  assert.equal(report.profiles.length, 1);
  assert.equal(report.profiles[0].sampleCount, 1);
  for (const changes of [{ controller: 0 }, { avatar: false }, { controller: undefined },
    { active: false, controller: null, layers: [layer(0, NaN)] }, { controller: null, active: 'true' }]) {
    assert.throws(() => summarize(states(), capture([row(changes)])));
  }
});

test('rejects malformed samples instead of coercing or truncating', () => {
  for (const changes of [{ instance: 2147483648 }, { active: 'true' }, { layers: [layer(0, NaN)] },
    { layers: [layer(0, 1, 2147483648)] }, { layers: [layer(0, -0.1)] }, { layers: [layer(0, 2)] },
    { layers: [layer(0.2)] }, { layers: [layer(), layer()] }, { root: null },
    { layers: [{ ...layer(), normalizedTime: Infinity }] }]) {
    assert.throws(() => summarize(states(), capture([row(changes)])));
  }
  for (const hash of [-1, 4294967296, 1.5, '1']) {
    const input = states(); input.controllers[0].stateMachines[0].states[0].serializedSettings.m_FullPathID = hash;
    assert.throws(() => summarize(input, capture()), /m_FullPathID/);
  }
  assert.throws(() => summarize(states(), [{ utc: 'bad', frame: 0, rows: [] }]));
  assert.throws(() => summarize(states(), [{ utc: '2026-10-08', frame: -1, rows: [] }]));
  assert.throws(() => summarize(states(), capture([row(), row()])));
});

test('timestamps require real UTC calendar dates and preserve recorder precision', () => {
  for (const utc of ['1', '2026-10-08T18:52:11', '2026-02-30T18:52:11Z', '2026-10-08T24:00:00Z']) {
    assert.throws(() => summarize(states(), [{ ...capture()[0], utc }]), /UTC ISO/);
  }
  const utc = '2026-10-08T18:52:11.9303459Z';
  assert.equal(summarize(states(), [{ ...capture()[0], utc }]).profiles[0].firstObservedUtc, utc);
});

test('CLI records input byte provenance and refuses output collisions', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'animation-observations-'));
  try {
    const statePath = join(directory, 'states.json'), capturePath = join(directory, 'capture.json'), output = join(directory, 'out.json');
    const bytes = JSON.stringify(states());
    await writeFile(statePath, bytes); await writeFile(capturePath, JSON.stringify(capture()));
    const args = ['--states', statePath, '--capture', capturePath, '--out', output];
    const report = await runCli(args);
    assert.equal(report.provenance.states.sha256, createHash('sha256').update(bytes).digest('hex'));
    const original = await readFile(output, 'utf8');
    await assert.rejects(runCli(args), { code: 'EEXIST' });
    assert.equal(await readFile(output, 'utf8'), original);
    await assert.rejects(runCli(['--states', statePath, '--capture', capturePath, '--out', statePath]), { code: 'EEXIST' });
    assert.equal(await readFile(statePath, 'utf8'), bytes);
    await assert.rejects(runCli(['--states', statePath, '--states', capturePath]), /Usage/);
  } finally { await rm(directory, { recursive: true, force: true }); }
});
