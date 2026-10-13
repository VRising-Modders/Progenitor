import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { inspectRecipe, loadRecipes, validateRecipe } from '../lib/recipes.mjs';
import {
  MAX_REPORT_BYTES,
  applicabilityFromRun,
  buildStampsFromRun,
  frameworkStampsMatch,
  parseFrameworkStamp,
  readRigRun,
  stampValuesMatch,
} from '../lib/rigRuns.mjs';
import { evidence, fixtureFrameworkSides, makeRecipe, makeRunReport, op, tempRecipes, tempRunDir } from './helpers.mjs';

const MATCHING_SIDES = fixtureFrameworkSides('sample-aaa', 1);
const MATCHING_BUILD = {
  contentCatalogSha256: 'catalog-now',
  gameIdentity: 'vrising/fixture',
  framework: 'sample-aaa',
  frameworkSides: MATCHING_SIDES,
};

function recipeWithEvidence(ev, operations) {
  return makeRecipe({
    id: 'fixture-rig',
    evidence: ev,
    operations: {
      discovery: op('untested'),
      playback: op('untested'),
      appearance: op('untested'),
      controls: op('untested'),
      gameplayHits: op('not-applicable'),
      naturalCleanup: op('untested'),
      explicitCleanup: op('untested'),
      poolReuse: op('untested'),
      ...operations,
    },
  });
}

test('validateRecipe accepts kind rig-run with a path and refuses it without one', () => {
  const ok = makeRecipe({
    evidence: [evidence('R1', { kind: 'rig-run', path: 'C:/tmp/run', summary: 'a run' })],
    operations: { ...makeRecipe().operations, playback: op('observed-success', ['R1']) },
  });
  assert.deepEqual(validateRecipe(ok), []);
  const missingPath = makeRecipe({
    evidence: [evidence('R1', { kind: 'rig-run', path: null, summary: 'a run' })],
    operations: { ...makeRecipe().operations, playback: op('observed-success', ['R1']) },
  });
  assert.ok(validateRecipe(missingPath).some(p => /rig-run/.test(p)));
});

test('a complete run backs an operation as readback support', () => {
  const run = tempRunDir(makeRunReport());
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'complete cleanup-ok',
      build: { contentCatalogSha256: 'catalog-now', gameIdentity: 'vrising/fixture', framework: 'sample-aaa', frameworkSides: MATCHING_SIDES },
    })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig', { currentBuild: MATCHING_BUILD });
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.provenanceClass, 'readback');
    assert.equal(ev.run.found, true);
    assert.equal(ev.run.status, 'complete');
    assert.equal(ev.run.agentReported, false);
    assert.equal(ev.run.session.sampleHash, 'sample-aaa');
    assert.equal(ev.run.session.generation, 1);
    assert.ok(ev.run.session.serverStartedUtc);
    assert.equal(ev.run.images.server.sampleHash, 'sample-aaa');
    assert.equal(ev.run.steps.ok, 1);
    assert.equal(ev.run.cleanup.ok, true);
    assert.equal(ev.run.finalChecks.passed, 1);
    assert.equal(r.operations.playback.support.strongest, 'readback');
    assert.deepEqual(r.operations.playback.support.classes, ['readback']);
    assert.deepEqual(r.operations.playback.support.runsNotComplete, []);
    assert.deepEqual(r.operations.playback.support.runsComplete, ['R1']);
    assert.equal(r.operations.playback.support.agentReportedOnly, false);
    assert.equal(r.currentVerification.claimable, false);
    assert.equal(ev.applicability, 'matches-checkable-stamps');
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('an incomplete run is flagged and does not count as support for observed-success', () => {
  const run = tempRunDir(makeRunReport({ status: 'incomplete', reason: 'checks: protection' }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', { kind: 'rig-run', path: run.dir, summary: 'incomplete run' })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.run.found, true);
    assert.equal(ev.run.status, 'incomplete');
    assert.equal(ev.provenanceClass, 'readback');
    assert.deepEqual(r.operations.playback.support.runsNotComplete, ['R1']);
    assert.deepEqual(r.operations.playback.support.runsComplete, []);
    assert.equal(r.operations.playback.support.strongest, 'none');
    assert.ok(!r.operations.playback.support.classes.includes('readback'));
    assert.equal(r.operations.playback.status, 'observed-success', 'historical status is not rewritten');
    assert.equal(r.currentVerification.claimable, false);
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('a missing run directory is found:false and provenanceClass missing', () => {
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', { kind: 'rig-run', path: 'C:\\definitely\\no\\such\\run-dir', summary: 'absent run' })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.run.found, false);
    assert.match(ev.run.reason, /not found/);
    assert.equal(ev.provenanceClass, 'missing');
    assert.deepEqual(r.operations.playback.support.missing, ['R1']);
    assert.equal(r.operations.playback.support.strongest, 'missing');
  } finally {
    t.cleanup();
  }
});

test('readRigRun returns found:false when report.json is absent from an existing directory', () => {
  const run = tempRunDir(makeRunReport());
  try {
    unlinkSync(path.join(run.dir, 'report.json'));
    const got = readRigRun(run.dir);
    assert.equal(got.found, false);
    assert.match(got.reason, /report\.json/);
  } finally {
    run.cleanup();
  }
});

test('an agent-reported-only operation is flagged and is not independently recorded', () => {
  const root = tempRecipes([]);
  const responses = path.join(root.root, 'bridge-responses.md');
  writeFileSync(responses, '# agent notes\n');
  const t = tempRecipes([recipeWithEvidence(
    [evidence('A1', { kind: 'report', path: responses, summary: 'Agent-reported damage 9 HP' })],
    { gameplayHits: op('observed-partial', ['A1'], 'hits from bridge notes') },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    const ev = r.evidence.find(e => e.id === 'A1');
    assert.equal(ev.provenanceClass, 'agent-reported');
    assert.equal(r.operations.gameplayHits.support.agentReportedOnly, true);
    assert.equal(r.operations.gameplayHits.support.strongest, 'agent-reported');
    assert.equal(r.operations.poolReuse.support.strongest, 'none');
    assert.equal(r.operations.gameplayHits.status, 'observed-partial');
  } finally {
    t.cleanup();
    root.cleanup();
  }
});

test('a missing media path cannot raise strongest above source', () => {
  const t = tempRecipes([recipeWithEvidence(
    [
      evidence('S1', { kind: 'source-inspection', path: null, summary: 'read the dump' }),
      evidence('M1', { kind: 'screenshot', path: 'C:/definitely/not/here/shot.png', summary: 'a missing shot' }),
    ],
    { appearance: op('observed-success', ['S1', 'M1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    const byId = Object.fromEntries(r.evidence.map(e => [e.id, e]));
    assert.equal(byId.S1.provenanceClass, 'source');
    assert.equal(byId.M1.provenanceClass, 'missing');
    assert.equal(r.operations.appearance.support.strongest, 'source');
    assert.ok(r.operations.appearance.support.missing.includes('M1'));
    assert.ok(!r.operations.appearance.support.classes.includes('media'));
  } finally {
    t.cleanup();
  }
});

test('stale build stamps on a run make applicability stale', () => {
  const oldSides = fixtureFrameworkSides('old-hash', 1);
  const run = tempRunDir(makeRunReport({
    session: { serverStartedUtc: '2026-09-07T00:00:00.000Z', sampleHash: 'old-hash', generation: 1 },
    images: {
      client: { side: 'client', progenitor: '0.1.0', generation: 1, sampleHash: 'old-hash' },
      server: { side: 'server', progenitor: '0.1.0', generation: 1, sampleHash: 'old-hash' },
    },
    frameworkSides: oldSides,
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'run from another build',
      build: { contentCatalogSha256: null, gameIdentity: null, framework: null, frameworkSides: oldSides },
    })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig', {
      currentBuild: {
        contentCatalogSha256: 'catalog-now',
        gameIdentity: 'vrising/fixture',
        framework: 'new-hash',
        frameworkSides: fixtureFrameworkSides('new-hash', 1),
      },
    });
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.run.found, true);
    assert.equal(ev.applicability, 'stale');
    assert.equal(r.operations.playback.applicability, 'stale');
    assert.equal(r.buildSummary.stale, 1);
    assert.equal(r.currentVerification.claimable, false);
    assert.deepEqual(r.operations.playback.support.runsOtherBuild, ['R1']);
    assert.deepEqual(r.operations.playback.support.runsComplete, []);
    assert.notEqual(r.operations.playback.support.strongest, 'readback');
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('untested and not-applicable operations have support.strongest none', () => {
  const t = tempRecipes([recipeWithEvidence([], {})]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    assert.equal(r.operations.playback.status, 'untested');
    assert.equal(r.operations.playback.support.strongest, 'none');
    assert.deepEqual(r.operations.playback.support.classes, []);
    assert.equal(r.operations.gameplayHits.status, 'not-applicable');
    assert.equal(r.operations.gameplayHits.support.strongest, 'none');
    assert.equal(r.buildSummary.stamped, 0);
    assert.equal(r.buildSummary.unstamped, 0);
  } finally {
    t.cleanup();
  }
});

test('readRigRun does not load trace.jsonl unless a step is named, and then only a bounded excerpt', () => {
  const lines = [];
  for (let i = 0; i < 5; i++) {
    lines.push(JSON.stringify({ t: '2026-09-07T00:00:00.000Z', id: i + 1, type: 'request', op: 'ping', args: {} }));
  }
  lines.push(JSON.stringify({ t: '2026-09-07T00:00:01.000Z', id: 9, type: 'request', op: 'spawn', args: { prefab: 'CHAR_Critter_Rat' } }));
  const run = tempRunDir(makeRunReport(), { trace: lines.join('\n') + '\n' });
  try {
    const silent = readRigRun(run.dir);
    assert.equal(silent.found, true);
    assert.equal(silent.trace, undefined);
    const excerpt = readRigRun(run.dir, { step: 'spawn' });
    assert.equal(excerpt.found, true);
    assert.equal(excerpt.trace.found, true);
    assert.ok(excerpt.trace.lines.length >= 1);
    assert.ok(excerpt.trace.lines.length <= 20);
    assert.ok(excerpt.trace.lines.some(l => l.op === 'spawn'));
  } finally {
    run.cleanup();
  }
});

test('an aborted run is not complete support either', () => {
  const run = tempRunDir(makeRunReport({ status: 'aborted', reason: 'step failed' }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', { kind: 'rig-run', path: run.dir, summary: 'aborted run' })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    assert.deepEqual(r.operations.playback.support.runsNotComplete, ['R1']);
    assert.equal(r.operations.playback.support.strongest, 'none');
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('path-is-bridge-responses.md is agent-reported even when the summary does not start that way', () => {
  const root = tempRecipes([]);
  const responses = path.join(root.root, 'bridge-responses.md');
  writeFileSync(responses, 'notes\n');
  const t = tempRecipes([recipeWithEvidence(
    [evidence('B1', { kind: 'report', path: responses, summary: 'carrier query transcribed after the fact' })],
    { gameplayHits: op('observed-partial', ['B1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    assert.equal(r.evidence[0].provenanceClass, 'agent-reported');
    assert.equal(r.operations.gameplayHits.support.agentReportedOnly, true);
  } finally {
    t.cleanup();
    root.cleanup();
  }
});

test('mkdir-only directory without report is missing', () => {
  const root = tempRecipes([]);
  const emptyDir = path.join(root.root, 'empty-run');
  mkdirSync(emptyDir);
  try {
    const got = readRigRun(emptyDir);
    assert.equal(got.found, false);
    assert.match(got.reason, /report\.json/);
  } finally {
    root.cleanup();
  }
});

test('readRigRun refuses a path that is not a directory', () => {
  const root = tempRecipes([]);
  const filePath = path.join(root.root, 'not-a-dir.json');
  writeFileSync(filePath, JSON.stringify({ status: 'complete' }));
  try {
    const got = readRigRun(filePath);
    assert.equal(got.found, false);
    assert.match(got.reason, /not a directory/);
  } finally {
    root.cleanup();
  }
});

test('readRigRun refuses a symlinked directory', () => {
  const run = tempRunDir(makeRunReport());
  const root = tempRecipes([]);
  const link = path.join(root.root, 'linked-run');
  try {
    symlinkSync(run.dir, link, 'junction');
    const got = readRigRun(link);
    assert.equal(got.found, false);
    assert.match(got.reason, /symlink/i);
  } catch (e) {
    if (e.code === 'EPERM' || e.code === 'ENOTSUP') {
      assert.ok(true, `symlink not permitted (${e.code}); skipped`);
    } else {
      throw e;
    }
  } finally {
    run.cleanup();
    root.cleanup();
  }
});

test('readRigRun caps report.json at 4 MB', () => {
  const run = tempRunDir(makeRunReport());
  try {
    writeFileSync(path.join(run.dir, 'report.json'), 'x'.repeat(MAX_REPORT_BYTES + 1));
    const got = readRigRun(run.dir);
    assert.equal(got.found, false);
    assert.match(got.reason, /exceeds/);
  } finally {
    run.cleanup();
  }
});

test('a complete run on another build is runsOtherBuild and cannot raise strongest above source', () => {
  const oldSides = fixtureFrameworkSides('old-hash', 1);
  const run = tempRunDir(makeRunReport({
    session: { serverStartedUtc: '2026-09-07T00:00:00.000Z', sampleHash: 'old-hash', generation: 1 },
    images: {
      client: { side: 'client', progenitor: '0.1.0', generation: 1, sampleHash: 'old-hash' },
      server: { side: 'server', progenitor: '0.1.0', generation: 1, sampleHash: 'old-hash' },
    },
    frameworkSides: oldSides,
  }));
  const t = tempRecipes([recipeWithEvidence(
    [
      evidence('S1', { kind: 'source-inspection', path: null, summary: 'dump' }),
      evidence('R1', {
        kind: 'rig-run',
        path: run.dir,
        summary: 'complete on another sample hash',
        build: { contentCatalogSha256: null, gameIdentity: null, framework: null, frameworkSides: oldSides },
      }),
    ],
    { playback: op('observed-success', ['S1', 'R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig', {
      currentBuild: {
        contentCatalogSha256: 'catalog-now',
        gameIdentity: 'vrising/fixture',
        framework: 'new-hash',
        frameworkSides: fixtureFrameworkSides('new-hash', 1),
      },
    });
    assert.deepEqual(r.operations.playback.support.runsOtherBuild, ['R1']);
    assert.deepEqual(r.operations.playback.support.runsComplete, []);
    assert.equal(r.operations.playback.support.strongest, 'source');
    assert.ok(!r.operations.playback.support.classes.includes('readback'));
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

const FULL_FRAMEWORK = 'generation:4; core:corehash; dev:devhash; sample:samplehash';
const FULL_SIDES = { server: FULL_FRAMEWORK, client: FULL_FRAMEWORK };
const NULL_PARTS_FRAMEWORK = 'generation:4; core:null; dev:null; sample:null';
const NULL_GENERATION_FRAMEWORK = 'generation:null; core:null; dev:null; sample:null';
const A7_FULL = 'generation:4; core:42c9c081d87bae5f79d1deecdfa40711b23bef05142cedfecd72f4b202e3c402; dev:9edb9edcfd908ce0d501c55d20dcf245bab21345e1c9d33e3d2db07535e59b1d; sample:6c81848ba12cba715c31614757a46939444dd22be6a42906a4c5b10146927f07';
const A7_CATALOG = 'b8bd8fa6cb1619330bad310ff082b77872e02ec27e4d1cd88a846125a25c1a01';
const A7_GAME = 'vrising/1.1.13.0+r99712+b17/net25';
const A7_SIDES = { server: A7_FULL, client: A7_FULL };
const A7_BUILD = { contentCatalogSha256: A7_CATALOG, gameIdentity: A7_GAME, framework: A7_FULL, frameworkSides: A7_SIDES };
const STAMP_ERRORS = [
  { stamp: 'contentCatalogSha256', error: 'content catalog unreadable: missing.bin' },
  { stamp: 'gameIdentity', error: 'ping has no game version/build identity' },
];

test('buildStampsFromRun prefers the report top-level framework string over session.sampleHash', () => {
  const report = makeRunReport({
    contentCatalogSha256: 'catalog-now',
    gameIdentity: 'vrising/fixture',
    framework: FULL_FRAMEWORK,
    stampErrors: [],
  });
  const stamps = buildStampsFromRun(report);
  assert.equal(stamps.contentCatalogSha256, 'catalog-now');
  assert.equal(stamps.gameIdentity, 'vrising/fixture');
  assert.equal(stamps.framework, FULL_FRAMEWORK);
  assert.notEqual(stamps.framework, report.session.sampleHash);
  assert.deepEqual(stamps.stampErrors, []);
});

test('buildStampsFromRun falls back to the session.sampleHash-derived form when top-level framework is absent', () => {
  const oldReport = makeRunReport();
  const stamps = buildStampsFromRun(oldReport);
  assert.equal(stamps.contentCatalogSha256, null);
  assert.equal(stamps.gameIdentity, null);
  assert.equal(stamps.framework, 'sample-aaa');
  assert.deepEqual(stamps.stampErrors, []);

  const generationOnly = makeRunReport({
    session: { serverStartedUtc: '2026-09-07T00:00:00.000Z', sampleHash: null, generation: 4 },
    images: {
      client: { side: 'client', progenitor: '0.1.0', generation: 4, sampleHash: null },
      server: { side: 'server', progenitor: '0.1.0', generation: 4, sampleHash: null },
    },
  });
  assert.equal(buildStampsFromRun(generationOnly).framework, 'generation:4');
});

test('buildStampsFromRun keeps a framework string whose parts are the token null, and copies stampErrors', () => {
  const report = makeRunReport({
    contentCatalogSha256: null,
    gameIdentity: null,
    framework: NULL_PARTS_FRAMEWORK,
    stampErrors: STAMP_ERRORS,
  });
  const stamps = buildStampsFromRun(report);
  assert.equal(stamps.contentCatalogSha256, null);
  assert.equal(stamps.gameIdentity, null);
  assert.equal(stamps.framework, NULL_PARTS_FRAMEWORK);
  assert.deepEqual(stamps.stampErrors, STAMP_ERRORS);
});

test('readRigRun surfaces top-level stamps and stampErrors on the inspect run object', () => {
  const run = tempRunDir(makeRunReport({
    contentCatalogSha256: null,
    gameIdentity: null,
    framework: NULL_GENERATION_FRAMEWORK,
    stampErrors: STAMP_ERRORS,
  }));
  try {
    const got = readRigRun(run.dir);
    assert.equal(got.found, true);
    assert.equal(got.contentCatalogSha256, null);
    assert.equal(got.gameIdentity, null);
    assert.equal(got.framework, NULL_GENERATION_FRAMEWORK);
    assert.deepEqual(got.stampErrors, STAMP_ERRORS);
    const stamps = buildStampsFromRun(got);
    assert.equal(stamps.framework, NULL_GENERATION_FRAMEWORK);
    assert.deepEqual(stamps.stampErrors, STAMP_ERRORS);
  } finally {
    run.cleanup();
  }
});

test('readRigRun on an old report without stamp fields leaves them null and stampErrors empty', () => {
  const run = tempRunDir(makeRunReport());
  try {
    const got = readRigRun(run.dir);
    assert.equal(got.found, true);
    assert.equal(got.contentCatalogSha256, null);
    assert.equal(got.gameIdentity, null);
    assert.equal(got.framework, null);
    assert.deepEqual(got.stampErrors, []);
    assert.equal(buildStampsFromRun(got).framework, 'sample-aaa');
  } finally {
    run.cleanup();
  }
});

test('inspect copies stampErrors onto evidence.run so a null stamp shows why', () => {
  const run = tempRunDir(makeRunReport({
    contentCatalogSha256: null,
    gameIdentity: null,
    framework: NULL_GENERATION_FRAMEWORK,
    stampErrors: STAMP_ERRORS,
    session: { serverStartedUtc: '2026-09-07T00:00:00.000Z', sampleHash: null, generation: 4 },
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'run with null stamps and errors',
      build: { contentCatalogSha256: null, gameIdentity: null, framework: null },
    })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.run.found, true);
    assert.deepEqual(ev.run.stampErrors, STAMP_ERRORS);
    assert.equal(ev.run.framework, NULL_GENERATION_FRAMEWORK);
    assert.ok(ev.run.stampErrors.some(err => err.stamp === 'contentCatalogSha256'));
    assert.ok(ev.run.stampErrors.some(err => err.stamp === 'gameIdentity'));
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('frameworkStampsMatch never treats a generation-only or null-parts stamp as a match', () => {
  assert.equal(frameworkStampsMatch('generation:4', FULL_FRAMEWORK), false);
  assert.equal(frameworkStampsMatch(FULL_FRAMEWORK, 'generation:4'), false);
  assert.equal(frameworkStampsMatch('generation:4', 'generation:4'), false);
  assert.equal(frameworkStampsMatch(NULL_PARTS_FRAMEWORK, FULL_FRAMEWORK), false);
  assert.equal(frameworkStampsMatch(NULL_PARTS_FRAMEWORK, NULL_PARTS_FRAMEWORK), false);
  assert.equal(frameworkStampsMatch(FULL_FRAMEWORK, FULL_FRAMEWORK), true);
  assert.equal(frameworkStampsMatch('generation:4', 'generation:5; core:corehash; dev:devhash; sample:samplehash'), false);
  assert.equal(frameworkStampsMatch('generation:4', NULL_GENERATION_FRAMEWORK), false);
  assert.equal(frameworkStampsMatch(null, null), false);
  assert.equal(frameworkStampsMatch(null, FULL_FRAMEWORK), false);
  assert.equal(frameworkStampsMatch(FULL_FRAMEWORK, null), false);
  assert.equal(frameworkStampsMatch('generation:null', 'generation:null'), false);
  assert.equal(frameworkStampsMatch(NULL_GENERATION_FRAMEWORK, NULL_GENERATION_FRAMEWORK), false);
  assert.equal(frameworkStampsMatch('generation:4;foo', 'generation:4;foo'), false);
  assert.equal(frameworkStampsMatch('generation:', FULL_FRAMEWORK), false);
  assert.equal(frameworkStampsMatch('sample-aaa', 'sample-aaa'), true);
  assert.equal(frameworkStampsMatch('sample-aaa', FULL_FRAMEWORK), false);
  assert.equal(stampValuesMatch('framework', 'generation:4', FULL_FRAMEWORK), false);
  assert.equal(stampValuesMatch('framework', null, null), false);
  assert.equal(stampValuesMatch('contentCatalogSha256', 'aaa', 'aaa'), true);
  assert.equal(stampValuesMatch('contentCatalogSha256', null, null), false);
  assert.equal(parseFrameworkStamp(FULL_FRAMEWORK).kind, 'full');
  assert.equal(parseFrameworkStamp('generation:4').kind, 'generation');
  assert.equal(parseFrameworkStamp('generation:4;foo'), null);
});

test('applicabilityFromRun treats generation-only and null-parts framework stamps as unknown-stamps', () => {
  const run = {
    found: true,
    contentCatalogSha256: null,
    gameIdentity: null,
    framework: FULL_FRAMEWORK,
    frameworkSides: FULL_SIDES,
    stampErrors: [],
    session: { sampleHash: 'samplehash', generation: 4 },
  };
  assert.equal(
    applicabilityFromRun(run, { contentCatalogSha256: null, gameIdentity: null, framework: 'generation:4', frameworkSides: { server: 'generation:4', client: 'generation:4' } }),
    'unknown-stamps',
  );
  assert.equal(
    applicabilityFromRun(run, { contentCatalogSha256: null, gameIdentity: null, framework: FULL_FRAMEWORK, frameworkSides: FULL_SIDES }),
    'matches-checkable-stamps',
  );
  assert.equal(
    applicabilityFromRun(run, { contentCatalogSha256: null, gameIdentity: null, framework: 'generation:5; core:corehash; dev:devhash; sample:samplehash', frameworkSides: { server: 'generation:5; core:corehash; dev:devhash; sample:samplehash', client: 'generation:5; core:corehash; dev:devhash; sample:samplehash' } }),
    'stale',
  );
  assert.equal(
    applicabilityFromRun(run, { contentCatalogSha256: null, gameIdentity: null, framework: null, frameworkSides: { server: null, client: null } }),
    'unknown-stamps',
  );
  const genOnly = { ...run, framework: 'generation:4', frameworkSides: { server: 'generation:4', client: 'generation:4' } };
  assert.equal(
    applicabilityFromRun(genOnly, { contentCatalogSha256: null, gameIdentity: null, framework: FULL_FRAMEWORK, frameworkSides: FULL_SIDES }),
    'unknown-stamps',
  );
  const nullGen = { ...run, framework: NULL_GENERATION_FRAMEWORK, frameworkSides: { server: NULL_GENERATION_FRAMEWORK, client: NULL_GENERATION_FRAMEWORK } };
  assert.equal(
    applicabilityFromRun(nullGen, { contentCatalogSha256: null, gameIdentity: null, framework: NULL_GENERATION_FRAMEWORK, frameworkSides: { server: NULL_GENERATION_FRAMEWORK, client: NULL_GENERATION_FRAMEWORK } }),
    'unknown-stamps',
    'identical null-generation stamps are insufficient, not a match',
  );
  const unparseable = { ...run, framework: 'generation:4;foo', frameworkSides: { server: 'generation:4;foo', client: 'generation:4;foo' } };
  assert.equal(
    applicabilityFromRun(unparseable, { contentCatalogSha256: null, gameIdentity: null, framework: 'generation:4;foo', frameworkSides: { server: 'generation:4;foo', client: 'generation:4;foo' } }),
    'stale',
    'identical unparseable stamps do not match',
  );
});

test('inspect does not treat a generation-only recipe framework as matching a full report framework', () => {
  const run = tempRunDir(makeRunReport({
    contentCatalogSha256: null,
    gameIdentity: null,
    framework: FULL_FRAMEWORK,
    frameworkSides: FULL_SIDES,
    stampErrors: [],
    session: { serverStartedUtc: '2026-09-07T00:00:00.000Z', sampleHash: 'samplehash', generation: 4 },
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'generation-only recipe stamp vs full report stamp',
      build: { contentCatalogSha256: null, gameIdentity: null, framework: 'generation:4', frameworkSides: { server: 'generation:4', client: 'generation:4' } },
    })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig', {
      currentBuild: { contentCatalogSha256: null, gameIdentity: null, framework: FULL_FRAMEWORK, frameworkSides: FULL_SIDES },
    });
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.run.framework, FULL_FRAMEWORK);
    assert.equal(frameworkStampsMatch(ev.build.framework, ev.run.framework), false);
    assert.equal(ev.stamps.framework.state, 'legacy-combined');
    assert.equal(ev.applicability, 'unknown-stamps');
    assert.deepEqual(r.operations.playback.support.runsOtherBuild, []);
    assert.deepEqual(r.operations.playback.support.runsComplete, ['R1']);
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('full-hash framework stamps match when generation and every overlapping hash are equal', () => {
  assert.equal(frameworkStampsMatch(FULL_FRAMEWORK, FULL_FRAMEWORK), true);
  assert.equal(frameworkStampsMatch(A7_FULL, A7_FULL), true);
  const run = {
    found: true,
    contentCatalogSha256: A7_CATALOG,
    gameIdentity: A7_GAME,
    framework: A7_FULL,
    frameworkSides: A7_SIDES,
    stampErrors: [],
    session: { sampleHash: '6c81848ba12cba715c31614757a46939444dd22be6a42906a4c5b10146927f07', generation: 4 },
  };
  assert.equal(applicabilityFromRun(run, A7_BUILD), 'matches-checkable-stamps');

  const dir = tempRunDir(makeRunReport({
    contentCatalogSha256: A7_CATALOG,
    gameIdentity: A7_GAME,
    framework: A7_FULL,
    frameworkSides: A7_SIDES,
    stampErrors: [],
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: dir.dir,
      summary: 'full-hash match',
      build: { contentCatalogSha256: A7_CATALOG, gameIdentity: A7_GAME, framework: A7_FULL, frameworkSides: A7_SIDES },
    })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig', { currentBuild: A7_BUILD });
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.applicability, 'matches-checkable-stamps');
    assert.equal(ev.stamps.framework.state, 'legacy-combined');
    assert.equal(ev.stamps.frameworkSides.server.state, 'matches');
    assert.equal(ev.stamps.frameworkSides.client.state, 'matches');
    assert.deepEqual(r.operations.playback.support.runsComplete, ['R1']);
    assert.deepEqual(r.operations.playback.support.runsOtherBuild, []);
  } finally {
    t.cleanup();
    dir.cleanup();
  }
});

test('one differing framework hash is a mismatch (stale), never a match', () => {
  const otherSample = 'generation:4; core:corehash; dev:devhash; sample:otherhash';
  assert.equal(frameworkStampsMatch(FULL_FRAMEWORK, otherSample), false);
  const missingSample = 'generation:4; core:corehash; dev:devhash; sample:null';
  assert.equal(frameworkStampsMatch(FULL_FRAMEWORK, missingSample), false);

  const run = {
    found: true,
    contentCatalogSha256: null,
    gameIdentity: null,
    framework: FULL_FRAMEWORK,
    frameworkSides: FULL_SIDES,
    stampErrors: [],
    session: { sampleHash: 'samplehash', generation: 4 },
  };
  assert.equal(
    applicabilityFromRun(run, { contentCatalogSha256: null, gameIdentity: null, framework: otherSample, frameworkSides: { server: otherSample, client: otherSample } }),
    'stale',
  );
  assert.equal(
    applicabilityFromRun(run, { contentCatalogSha256: null, gameIdentity: null, framework: missingSample, frameworkSides: { server: missingSample, client: missingSample } }),
    'unknown-stamps',
    'a hash present on one side and missing on the other is mismatch-unknown',
  );

  const dir = tempRunDir(makeRunReport({
    contentCatalogSha256: A7_CATALOG,
    gameIdentity: A7_GAME,
    framework: A7_FULL,
    frameworkSides: A7_SIDES,
    stampErrors: [],
  }));
  const otherStamp = 'generation:4; core:42c9c081d87bae5f79d1deecdfa40711b23bef05142cedfecd72f4b202e3c402; dev:9edb9edcfd908ce0d501c55d20dcf245bab21345e1c9d33e3d2db07535e59b1d; sample:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const otherBuild = {
    contentCatalogSha256: A7_CATALOG,
    gameIdentity: A7_GAME,
    framework: otherStamp,
    frameworkSides: { server: otherStamp, client: otherStamp },
  };
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: dir.dir,
      summary: 'differing sample hash',
      build: { contentCatalogSha256: A7_CATALOG, gameIdentity: A7_GAME, framework: A7_FULL, frameworkSides: A7_SIDES },
    })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig', { currentBuild: otherBuild });
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.applicability, 'stale');
    assert.equal(ev.stamps.framework.state, 'legacy-combined');
    assert.equal(ev.stamps.frameworkSides.server.state, 'changed');
    assert.deepEqual(r.operations.playback.support.runsComplete, []);
    assert.deepEqual(r.operations.playback.support.runsOtherBuild, ['R1']);
  } finally {
    t.cleanup();
    dir.cleanup();
  }
});

test('a current build with unknown framework (offline) stays unknown-stamps', () => {
  const run = {
    found: true,
    contentCatalogSha256: A7_CATALOG,
    gameIdentity: A7_GAME,
    framework: A7_FULL,
    frameworkSides: A7_SIDES,
    stampErrors: [],
    session: { sampleHash: 'samplehash', generation: 4 },
  };
  assert.equal(
    applicabilityFromRun(run, { contentCatalogSha256: A7_CATALOG, gameIdentity: null, framework: null, frameworkSides: { server: null, client: null } }),
    'unknown-stamps',
  );

  const dir = tempRunDir(makeRunReport({
    contentCatalogSha256: A7_CATALOG,
    gameIdentity: A7_GAME,
    framework: A7_FULL,
    frameworkSides: A7_SIDES,
    stampErrors: [],
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: dir.dir,
      summary: 'offline current build',
      build: { contentCatalogSha256: A7_CATALOG, gameIdentity: A7_GAME, framework: A7_FULL, frameworkSides: A7_SIDES },
    })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    assert.equal(r.currentBuild.framework, null);
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.applicability, 'unknown-stamps');
    assert.equal(r.operations.playback.applicability, 'unknown-stamps');
    assert.deepEqual(r.operations.playback.support.runsComplete, ['R1']);
    assert.deepEqual(r.operations.playback.support.runsOtherBuild, []);
  } finally {
    t.cleanup();
    dir.cleanup();
  }
});

test('R74: A6/B5-style generation-only report does not match a current build with full hashes on generation 4', () => {
  const a6Style = makeRunReport({
    contentCatalogSha256: null,
    gameIdentity: null,
    framework: null,
    session: { serverStartedUtc: '2026-09-07T00:00:00.000Z', sampleHash: null, generation: 4 },
    images: {
      client: { side: 'client', progenitor: '0.1.0', generation: 4, sampleHash: null },
      server: { side: 'server', progenitor: '0.1.0', generation: 4, sampleHash: null },
    },
  });
  const derived = buildStampsFromRun(a6Style);
  assert.equal(derived.framework, 'generation:4');
  assert.equal(frameworkStampsMatch('generation:4', A7_FULL), false);
  assert.equal(frameworkStampsMatch(A7_FULL, 'generation:4'), false);
  assert.equal(stampValuesMatch('framework', 'generation:4', A7_FULL), false);

  const genSides = { server: 'generation:4', client: 'generation:4' };
  const run = {
    found: true,
    contentCatalogSha256: null,
    gameIdentity: null,
    framework: 'generation:4',
    frameworkSides: genSides,
    stampErrors: [],
    session: a6Style.session,
  };
  assert.equal(applicabilityFromRun(run, A7_BUILD), 'unknown-stamps');
  assert.equal(
    applicabilityFromRun({ ...run, contentCatalogSha256: A7_CATALOG }, A7_BUILD),
    'unknown-stamps',
    'insufficient framework is unknown-stamps even when the catalog hash matches',
  );

  const reportWithNullParts = { ...run, framework: NULL_PARTS_FRAMEWORK, frameworkSides: { server: NULL_PARTS_FRAMEWORK, client: NULL_PARTS_FRAMEWORK } };
  assert.equal(frameworkStampsMatch(NULL_PARTS_FRAMEWORK, A7_FULL), false);
  assert.equal(applicabilityFromRun(reportWithNullParts, A7_BUILD), 'unknown-stamps');

  const dir = tempRunDir(a6Style);
  const t = tempRecipes([recipeWithEvidence(
    [evidence('A6', {
      kind: 'rig-run',
      path: dir.dir,
      summary: 'A6/B5-style generation-only report',
      build: { contentCatalogSha256: null, gameIdentity: null, framework: 'generation:4', frameworkSides: genSides },
    })],
    { poolReuse: op('observed-success', ['A6']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig', { currentBuild: A7_BUILD });
    const ev = r.evidence.find(e => e.id === 'A6');
    assert.equal(ev.applicability, 'unknown-stamps');
    assert.notEqual(ev.applicability, 'matches-checkable-stamps');
    assert.deepEqual(r.operations.poolReuse.support.runsComplete, []);
    assert.deepEqual(r.operations.poolReuse.support.runsOtherBuild, ['A6']);
  } finally {
    t.cleanup();
    dir.cleanup();
  }
});

test('inspect of a report with the new stamp fields keeps a matching full framework as complete support', () => {
  const run = tempRunDir(makeRunReport({
    contentCatalogSha256: 'catalog-now',
    gameIdentity: 'vrising/fixture',
    framework: FULL_FRAMEWORK,
    frameworkSides: FULL_SIDES,
    stampErrors: [],
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'stamped run',
      build: { contentCatalogSha256: 'catalog-now', gameIdentity: 'vrising/fixture', framework: FULL_FRAMEWORK, frameworkSides: FULL_SIDES },
    })],
    { playback: op('observed-success', ['R1']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig', {
      currentBuild: { contentCatalogSha256: 'catalog-now', gameIdentity: 'vrising/fixture', framework: FULL_FRAMEWORK, frameworkSides: FULL_SIDES },
    });
    const ev = r.evidence.find(e => e.id === 'R1');
    assert.equal(ev.run.found, true);
    assert.equal(ev.run.framework, FULL_FRAMEWORK);
    assert.deepEqual(ev.run.stampErrors, []);
    assert.equal(ev.applicability, 'matches-checkable-stamps');
    assert.deepEqual(r.operations.playback.support.runsComplete, ['R1']);
    assert.deepEqual(r.operations.playback.support.runsOtherBuild, []);
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('kind report with Coordinating agent summary and a present markdown path is agent-reported, never source', () => {
  const root = tempRecipes([]);
  const md = path.join(root.root, 'coordinating.md');
  writeFileSync(md, '# coordinating\n');
  const t = tempRecipes([recipeWithEvidence(
    [evidence('E3', { kind: 'report', path: md, summary: "Coordinating agent's report: particles, not a beam" })],
    { appearance: op('observed-partial', ['E3']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-rig');
    assert.equal(r.evidence[0].provenanceClass, 'agent-reported');
    assert.equal(r.operations.appearance.support.strongest, 'agent-reported');
    assert.equal(r.operations.appearance.mediaPresent, null);
  } finally {
    t.cleanup();
    root.cleanup();
  }
});
