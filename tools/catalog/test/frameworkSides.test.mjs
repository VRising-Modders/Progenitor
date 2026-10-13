import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { inspectRecipe, loadRecipes, validateRecipe } from '../lib/recipes.mjs';
import {
  DEFAULT_OPERATION_SIDES,
  applicabilityFromRun,
  frameworkSidesCompare,
  frameworkStampCompare,
  frameworkStampsMatch,
  normalizeOperationSides,
} from '../lib/rigRuns.mjs';
import {
  FIXTURE_GENERATED,
  TOOL_DIR,
  evidence,
  frameworkSideStamp,
  makeRecipe,
  makeRunReport,
  op,
  tempRecipes,
  tempRunDir,
} from './helpers.mjs';

const CLI = path.join(TOOL_DIR, 'recipe.mjs');

const CORE = 'corehashcorehashcorehashcorehashcorehashcorehashcorehashcorehashco';
const DEV = 'devhashdevhashdevhashdevhashdevhashdevhashdevhashdevhashdevhashde';
const SAMPLE_S = 'samplesamplesamplesamplesamplesamplesamplesamplesamplesamplesa';
const SAMPLE_C = 'clientclientclientclientclientclientclientclientclientclientcl';
const SAMPLE_C2 = 'changedclientchangedclientchangedclientchangedclientchangedcli';
const SAMPLE_S2 = 'changedserverchangedserverchangedserverchangedserverchangedser';

const SERVER_STAMP = frameworkSideStamp(4, { core: CORE, dev: DEV, sample: SAMPLE_S });
const CLIENT_STAMP = frameworkSideStamp(5, { core: CORE, dev: DEV, sample: SAMPLE_C });
const CLIENT_CHANGED = frameworkSideStamp(5, { core: CORE, dev: DEV, sample: SAMPLE_C2 });
const SERVER_CHANGED = frameworkSideStamp(4, { core: CORE, dev: DEV, sample: SAMPLE_S2 });
const CLIENT_CORE_CHANGED = frameworkSideStamp(5, { core: SAMPLE_C2, dev: DEV, sample: SAMPLE_C });
const CLIENT_DEV_CHANGED = frameworkSideStamp(5, { core: CORE, dev: SAMPLE_C2, sample: SAMPLE_C });
const SERVER_MISSING_SAMPLE = frameworkSideStamp(4, { core: CORE, dev: DEV, sample: null });
const CLIENT_MISSING_SAMPLE = frameworkSideStamp(5, { core: CORE, dev: DEV, sample: null });

const PAIRED_SIDES = { server: SERVER_STAMP, client: CLIENT_STAMP };
const PAIRED_BUILD = {
  contentCatalogSha256: 'catalog-now',
  gameIdentity: 'vrising/fixture',
  framework: null,
  frameworkSides: PAIRED_SIDES,
};

function recipeWithEvidence(ev, operations) {
  return makeRecipe({
    id: 'fixture-sides',
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

function runCli(...args) {
  const r = spawnSync(process.execPath, [CLI, ...args], { encoding: 'utf8', cwd: TOOL_DIR });
  let json = null;
  try { json = JSON.parse(r.stdout); } catch { /* text */ }
  return { code: r.status, out: r.stdout, json };
}

test('normalizeOperationSides defaults conservatively to both; accepts client-only and server-only', () => {
  assert.deepEqual(normalizeOperationSides(undefined), [...DEFAULT_OPERATION_SIDES]);
  assert.deepEqual(normalizeOperationSides(null), ['server', 'client']);
  assert.deepEqual(normalizeOperationSides(['client']), ['client']);
  assert.deepEqual(normalizeOperationSides(['server']), ['server']);
  assert.deepEqual(normalizeOperationSides(['client', 'server']), ['server', 'client']);
});

test('frameworkSidesCompare: equal server hashes with different client hashes are mismatch for a paired claim', () => {
  const recorded = { server: SERVER_STAMP, client: CLIENT_STAMP };
  const current = { server: SERVER_STAMP, client: CLIENT_CHANGED };
  assert.equal(frameworkSidesCompare(recorded, current, ['server', 'client']), 'mismatch');
  assert.equal(frameworkStampCompare(recorded.server, current.server), 'match');
  assert.notEqual(frameworkSidesCompare(recorded, current, ['server', 'client']), 'match');
});

test('frameworkSidesCompare: a changed client Core/Dev/Sample invalidates a client or paired claim', () => {
  const recorded = PAIRED_SIDES;
  assert.equal(frameworkSidesCompare(recorded, { server: SERVER_STAMP, client: CLIENT_CHANGED }, ['client']), 'mismatch');
  assert.equal(frameworkSidesCompare(recorded, { server: SERVER_STAMP, client: CLIENT_CORE_CHANGED }, ['client']), 'mismatch');
  assert.equal(frameworkSidesCompare(recorded, { server: SERVER_STAMP, client: CLIENT_DEV_CHANGED }, ['client']), 'mismatch');
  assert.equal(frameworkSidesCompare(recorded, { server: SERVER_STAMP, client: CLIENT_CHANGED }, ['server', 'client']), 'mismatch');
  assert.equal(frameworkSidesCompare(recorded, { server: SERVER_STAMP, client: CLIENT_CHANGED }, ['server']), 'match');
});

test('frameworkSidesCompare: a changed server-only dependency invalidates a server claim', () => {
  const recorded = PAIRED_SIDES;
  assert.equal(frameworkSidesCompare(recorded, { server: SERVER_CHANGED, client: CLIENT_STAMP }, ['server']), 'mismatch');
  assert.equal(frameworkSidesCompare(recorded, { server: SERVER_CHANGED, client: CLIENT_STAMP }, ['server', 'client']), 'mismatch');
  assert.equal(frameworkSidesCompare(recorded, { server: SERVER_CHANGED, client: CLIENT_STAMP }, ['client']), 'match');
});

test('frameworkSidesCompare: missing hashes on either required side stay unknown', () => {
  assert.equal(
    frameworkSidesCompare({ server: SERVER_STAMP, client: CLIENT_STAMP }, { server: SERVER_MISSING_SAMPLE, client: CLIENT_STAMP }, ['server']),
    'unknown',
  );
  assert.equal(
    frameworkSidesCompare({ server: SERVER_STAMP, client: CLIENT_MISSING_SAMPLE }, { server: SERVER_STAMP, client: CLIENT_STAMP }, ['client']),
    'unknown',
  );
  assert.equal(
    frameworkSidesCompare({ server: SERVER_STAMP, client: null }, { server: SERVER_STAMP, client: CLIENT_STAMP }, ['server', 'client']),
    'unknown',
  );
  assert.equal(
    frameworkSidesCompare({ server: SERVER_STAMP, client: CLIENT_STAMP }, { server: SERVER_STAMP, client: null }, ['client']),
    'unknown',
  );
  assert.equal(frameworkSidesCompare(null, PAIRED_SIDES, ['server', 'client']), 'unknown');
  assert.equal(frameworkSidesCompare(PAIRED_SIDES, null, ['server', 'client']), 'unknown');
});

test('frameworkSidesCompare: a legacy-combined stamp is unknown for any claim and never matches', () => {
  const combined = SERVER_STAMP;
  assert.equal(frameworkSidesCompare(undefined, PAIRED_SIDES, ['server', 'client']), 'unknown');
  assert.equal(frameworkSidesCompare({ server: null, client: null }, PAIRED_SIDES, ['server', 'client']), 'unknown');
  assert.notEqual(frameworkStampsMatch(combined, combined) && frameworkSidesCompare(null, { server: combined, client: combined }, ['server', 'client']), 'match');
  assert.equal(frameworkSidesCompare({ server: null, client: null }, { server: combined, client: combined }, ['server', 'client']), 'unknown');
});

test('frameworkSidesCompare never assembles a hybrid: filling a missing client from the server is not a match', () => {
  const recorded = { server: SERVER_STAMP, client: null };
  const current = { server: SERVER_STAMP, client: SERVER_STAMP };
  assert.equal(frameworkSidesCompare(recorded, current, ['server', 'client']), 'unknown');
  assert.notEqual(frameworkSidesCompare(recorded, current, ['server', 'client']), 'match');
});

test('an irrelevant-side change for an explicitly single-side operation still matches', () => {
  assert.equal(
    frameworkSidesCompare(PAIRED_SIDES, { server: SERVER_STAMP, client: CLIENT_CHANGED }, ['server']),
    'match',
  );
  assert.equal(
    frameworkSidesCompare(PAIRED_SIDES, { server: SERVER_CHANGED, client: CLIENT_STAMP }, ['client']),
    'match',
  );
});

test('inspect: changed client Sample on a paired operation is stale, not match (reviewer reproduction)', () => {
  const run = tempRunDir(makeRunReport({
    contentCatalogSha256: 'catalog-now',
    gameIdentity: 'vrising/fixture',
    framework: null,
    frameworkSides: PAIRED_SIDES,
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'paired observation',
      build: {
        contentCatalogSha256: 'catalog-now',
        gameIdentity: 'vrising/fixture',
        framework: SERVER_STAMP,
        frameworkSides: PAIRED_SIDES,
      },
    })],
    { appearance: op('observed-success', ['R1'], 'client crystals plus server procedure', ['server', 'client']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const match = inspectRecipe(store, 'fixture-sides', { currentBuild: PAIRED_BUILD });
    assert.equal(match.operations.appearance.applicability, 'matches-checkable-stamps');
    assert.deepEqual(match.operations.appearance.sides, ['server', 'client']);
    assert.equal(match.evidence[0].stamps.framework.state, 'legacy-combined');
    assert.equal(match.evidence[0].stamps.frameworkSides.server.state, 'matches');
    assert.equal(match.evidence[0].stamps.frameworkSides.client.state, 'matches');

    const clientOnlyChange = inspectRecipe(store, 'fixture-sides', {
      currentBuild: { ...PAIRED_BUILD, frameworkSides: { server: SERVER_STAMP, client: CLIENT_CHANGED } },
    });
    assert.equal(clientOnlyChange.operations.appearance.applicability, 'stale');
    assert.notEqual(clientOnlyChange.operations.appearance.applicability, 'matches-checkable-stamps');
    assert.equal(clientOnlyChange.evidence[0].stamps.frameworkSides.server.state, 'matches');
    assert.equal(clientOnlyChange.evidence[0].stamps.frameworkSides.client.state, 'changed');
    assert.deepEqual(clientOnlyChange.operations.appearance.support.runsOtherBuild, ['R1']);
    assert.equal(clientOnlyChange.currentVerification.claimable, false);
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('inspect: server-only operation stays matching when only the client hash changes', () => {
  const run = tempRunDir(makeRunReport({
    contentCatalogSha256: 'catalog-now',
    gameIdentity: 'vrising/fixture',
    framework: null,
    frameworkSides: PAIRED_SIDES,
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'server health',
      build: {
        contentCatalogSha256: 'catalog-now',
        gameIdentity: 'vrising/fixture',
        framework: SERVER_STAMP,
        frameworkSides: PAIRED_SIDES,
      },
    })],
    { gameplayHits: op('observed-partial', ['R1'], 'server Health.Value', ['server']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-sides', {
      currentBuild: { ...PAIRED_BUILD, frameworkSides: { server: SERVER_STAMP, client: CLIENT_CHANGED } },
    });
    assert.deepEqual(r.operations.gameplayHits.sides, ['server']);
    assert.equal(r.operations.gameplayHits.applicability, 'matches-checkable-stamps');
    assert.deepEqual(r.operations.gameplayHits.support.runsComplete, ['R1']);
    assert.deepEqual(r.operations.gameplayHits.support.runsOtherBuild, []);
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('inspect: missing hashes on a required side stay unknown-stamps', () => {
  const run = tempRunDir(makeRunReport({
    contentCatalogSha256: 'catalog-now',
    gameIdentity: 'vrising/fixture',
    framework: null,
    frameworkSides: { server: SERVER_STAMP, client: CLIENT_MISSING_SAMPLE },
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'client missing sample',
      build: {
        contentCatalogSha256: 'catalog-now',
        gameIdentity: 'vrising/fixture',
        framework: SERVER_STAMP,
        frameworkSides: { server: SERVER_STAMP, client: CLIENT_MISSING_SAMPLE },
      },
    })],
    { appearance: op('observed-success', ['R1'], 'paired', ['server', 'client']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-sides', { currentBuild: PAIRED_BUILD });
    assert.equal(r.operations.appearance.applicability, 'unknown-stamps');
    assert.notEqual(r.operations.appearance.applicability, 'matches-checkable-stamps');
    assert.equal(r.currentVerification.claimable, false);
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('inspect: legacy combined-only stamps on a paired claim are unknown, never match', () => {
  const run = tempRunDir(makeRunReport({
    contentCatalogSha256: 'catalog-now',
    gameIdentity: 'vrising/fixture',
    framework: SERVER_STAMP,
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'legacy combined only',
      build: {
        contentCatalogSha256: 'catalog-now',
        gameIdentity: 'vrising/fixture',
        framework: SERVER_STAMP,
      },
    })],
    { appearance: op('observed-success', ['R1'], 'paired', ['server', 'client']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-sides', { currentBuild: PAIRED_BUILD });
    assert.equal(r.evidence[0].stamps.framework.state, 'legacy-combined');
    assert.equal(r.operations.appearance.applicability, 'unknown-stamps');
    assert.notEqual(r.operations.appearance.applicability, 'matches-checkable-stamps');
    assert.ok(r.operations.appearance.support.runsOtherBuild.includes('R1'));
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('offline default currentBuild stays unknown-stamps with claimable false', () => {
  const run = tempRunDir(makeRunReport({
    contentCatalogSha256: 'catalog-now',
    gameIdentity: 'vrising/fixture',
    framework: null,
    frameworkSides: PAIRED_SIDES,
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'offline',
      build: {
        contentCatalogSha256: 'catalog-now',
        gameIdentity: 'vrising/fixture',
        framework: SERVER_STAMP,
        frameworkSides: PAIRED_SIDES,
      },
    })],
    { appearance: op('observed-success', ['R1'], 'paired', ['server', 'client']) },
  )]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-sides');
    assert.equal(r.currentBuild.framework, null);
    assert.equal(r.currentBuild.frameworkSides.server, null);
    assert.equal(r.currentBuild.frameworkSides.client, null);
    assert.equal(r.operations.appearance.applicability, 'unknown-stamps');
    assert.equal(r.currentVerification.claimable, false);
    assert.equal(r.runtimeAvailability.state, 'not-observed-offline');
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('applicabilityFromRun uses required sides and ignores the combined framework string', () => {
  const run = {
    found: true,
    contentCatalogSha256: 'catalog-now',
    gameIdentity: 'vrising/fixture',
    framework: SERVER_STAMP,
    frameworkSides: PAIRED_SIDES,
    stampErrors: [],
    session: { sampleHash: SAMPLE_S, generation: 4 },
  };
  assert.equal(applicabilityFromRun(run, PAIRED_BUILD, ['server', 'client']), 'matches-checkable-stamps');
  assert.equal(
    applicabilityFromRun(run, { ...PAIRED_BUILD, frameworkSides: { server: SERVER_STAMP, client: CLIENT_CHANGED } }, ['server', 'client']),
    'stale',
  );
  assert.equal(
    applicabilityFromRun(run, { ...PAIRED_BUILD, frameworkSides: { server: SERVER_STAMP, client: CLIENT_CHANGED } }, ['server']),
    'matches-checkable-stamps',
  );
  const legacy = { ...run, frameworkSides: { server: null, client: null } };
  assert.equal(applicabilityFromRun(legacy, PAIRED_BUILD, ['server', 'client']), 'matches-checkable-stamps');
});

test('validateRecipe accepts operation sides and rig-run frameworkSides; refuses a bad sides list', () => {
  const ok = makeRecipe({
    evidence: [evidence('R1', {
      kind: 'rig-run',
      path: 'C:/tmp/run',
      summary: 'a run',
      build: {
        contentCatalogSha256: null,
        gameIdentity: null,
        framework: 'generation:4',
        frameworkSides: { server: SERVER_STAMP, client: null },
      },
    })],
    operations: { ...makeRecipe().operations, gameplayHits: op('observed-success', ['R1'], 'server hits', ['server']) },
  });
  assert.deepEqual(validateRecipe(ok), []);
  const bad = makeRecipe({
    operations: { ...makeRecipe().operations, appearance: { status: 'untested', evidence: [], note: 'n', sides: ['both'] } },
  });
  assert.ok(validateRecipe(bad).some((p) => /sides/.test(p)));
});

test('CLI inspect JSON carries sides, frameworkSides and legacy-combined; claimable stays false', () => {
  const run = tempRunDir(makeRunReport({
    framework: SERVER_STAMP,
    frameworkSides: PAIRED_SIDES,
  }));
  const t = tempRecipes([recipeWithEvidence(
    [evidence('R1', {
      kind: 'rig-run',
      path: run.dir,
      summary: 'cli paired',
      build: {
        contentCatalogSha256: null,
        gameIdentity: null,
        framework: SERVER_STAMP,
        frameworkSides: PAIRED_SIDES,
      },
    })],
    { appearance: op('observed-success', ['R1'], 'paired', ['server', 'client']) },
  )]);
  try {
    const r = runCli('inspect', 'fixture-sides', '--recipes', t.dir, '--generated', FIXTURE_GENERATED);
    assert.equal(r.code, 0, r.out);
    assert.equal(r.json.currentVerification.claimable, false);
    assert.deepEqual(r.json.operations.appearance.sides, ['server', 'client']);
    assert.equal(r.json.evidence[0].stamps.framework.state, 'legacy-combined');
    assert.ok(r.json.evidence[0].stamps.frameworkSides);
    assert.equal(r.json.evidence[0].stamps.frameworkSides.client.state, 'not-checkable-offline');
    assert.equal(r.json.operations.appearance.applicability, 'unknown-stamps');
    assert.equal(r.json.currentBuild.framework, null);
    assert.equal(r.json.currentBuild.frameworkSides.server, null);
    const text = runCli('inspect', 'fixture-sides', '--recipes', t.dir, '--generated', FIXTURE_GENERATED, '--text');
    assert.equal(text.code, 0, text.out);
    assert.match(text.out, /sides=server,client/);
    assert.match(text.out, /legacy-combined|frameworkSides/);
    assert.match(text.out, /claimable: false/);
  } finally {
    t.cleanup();
    run.cleanup();
  }
});

test('absent operation sides default to both at inspect time', () => {
  const t = tempRecipes([recipeWithEvidence([], {})]);
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-sides');
    assert.deepEqual(r.operations.playback.sides, ['server', 'client']);
  } finally {
    t.cleanup();
  }
});
