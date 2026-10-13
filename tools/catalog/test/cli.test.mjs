import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { inspectRecipe, loadRecipes } from '../lib/recipes.mjs';
import path from 'node:path';
import { TOOL_DIR, FIXTURE_GENERATED, evidence, makeRecipe, makeRunReport, op, tempRecipes, tempRunDir } from './helpers.mjs';

const CLI = path.join(TOOL_DIR, 'recipe.mjs');

function run(...args) {
  const r = spawnSync(process.execPath, [CLI, ...args], { encoding: 'utf8', cwd: TOOL_DIR });
  let json = null;
  try { json = JSON.parse(r.stdout); } catch { /* text output */ }
  return { code: r.status, out: r.stdout, err: r.stderr, json };
}

test('validate and list succeed on the checked-in recipes', () => {
  const v = run('validate');
  assert.equal(v.code, 0, v.out);
  assert.equal(v.json.invalid, 0);
  const l = run('list');
  assert.equal(l.code, 0);
  assert.ok(l.json.recipes.some(r => r.recipeId === 'wendigo-frost-nova-ring'));
  assert.ok(l.json.recipes.some(r => r.recipeId === 'charm-target-channel-sequence'));
});

test('search returns structured results and honours --limit, typed identities and bare negatives', () => {
  const s = run('search', 'nova', '--limit', '1');
  assert.equal(s.code, 0);
  assert.equal(s.json.truncated, true);
  assert.equal(s.json.results.length, 1);
  assert.equal(s.json.results[0].family.id, 'wendigo-frost-nova');
  const bare = run('search', '-1227952025');
  assert.equal(bare.code, 0);
  assert.equal(bare.json.parsed.kind, 'number');
  assert.ok(bare.json.results.length >= 1);
  const typed = run('search', 'prefab:-1227952025', '--text');
  assert.equal(typed.code, 0);
  assert.match(typed.out, /identity prefab-guid "-1227952025"/);
});

test('invalid input exits 2 with a JSON error, unknown recipe exits 3', () => {
  const bad = run('search', 'bogus:1');
  assert.equal(bad.code, 2);
  assert.equal(bad.json.error.code, 'invalid-identity-domain');
  const lim = run('search', 'nova', '--limit', '0');
  assert.equal(lim.code, 2);
  assert.equal(lim.json.error.code, 'invalid-limit');
  const opt = run('search', 'nova', '--nope');
  assert.equal(opt.code, 2);
  const none = run('inspect', 'no-such-recipe');
  assert.equal(none.code, 3);
  assert.equal(none.json.error.code, 'unknown-recipe');
  assert.ok(Array.isArray(none.json.error.known));
  const cmd = run('frobnicate');
  assert.equal(cmd.code, 2);
});

test('inspect with a missing generated directory reports inventory-unavailable and still answers', () => {
  const r = run('inspect', 'charm-target-channel-sequence', '--generated', path.join(FIXTURE_GENERATED, 'missing'));
  assert.equal(r.code, 0);
  assert.equal(r.json.inventory.state, 'inventory-unavailable');
  assert.equal(r.json.inventory.note, 'index presence only; runtime availability is not observed offline');
  assert.ok(r.json.inventoryAssociations.every(a => a.status === 'inventory-unavailable'));
  assert.equal(r.json.operations.discovery.status, 'observed-success');
  assert.equal(r.json.currentVerification.claimable, false);
  const a = run('associations', 'charm-target-channel-sequence', '--generated', FIXTURE_GENERATED);
  assert.equal(a.code, 0);
  assert.equal(a.json.inventory.state, 'indexed');
  assert.equal(a.json.inventory.note, 'index presence only; runtime availability is not observed offline');
  assert.equal(a.json.currentVerification.claimable, false);
  assert.ok(a.json.inventoryAssociations.every(x => x.status === 'unresolved'), 'the fixture inventory holds none of the Charm names');
});

test('an invalid recipe directory exits 4 and names the problems', () => {
  const r = run('validate', '--recipes', path.join(TOOL_DIR, 'test', 'fixtures', 'generated-a'));
  assert.equal(r.code, 4, 'the fixture JSON files are not recipes and must be reported invalid');
  assert.ok(r.json.invalid >= 1);
  assert.ok(r.json.report.every(x => x.valid || x.problems.length > 0));
  const s = run('search', 'anything', '--recipes', path.join(TOOL_DIR, 'test', 'fixtures', 'generated-a'));
  assert.equal(s.code, 4);
  assert.equal(s.json.error.code, 'invalid-recipe-file');
});

test('validate and list agree that two files claiming one id are invalid', () => {
  const t = tempRecipes([makeRecipe({ id: 'shared-id' })]);
  try {
    writeFileSync(path.join(t.dir, 'copy.json'), JSON.stringify(makeRecipe({ id: 'shared-id', aliases: ['copy'] })));
    const v = run('validate', '--recipes', t.dir);
    assert.equal(v.code, 4, v.out);
    const named = JSON.stringify(v.json);
    assert.match(named, /shared-id/);
    assert.match(named, /shared-id\.json/);
    assert.match(named, /copy\.json/);
    assert.ok(v.json.directoryProblems.some(p => p.includes('shared-id') && p.includes('shared-id.json') && p.includes('copy.json')));
    const vt = run('validate', '--recipes', t.dir, '--text');
    assert.equal(vt.code, 4, vt.out);
    assert.match(vt.out, /shared-id/);
    assert.match(vt.out, /shared-id\.json/);
    assert.match(vt.out, /copy\.json/);
    const l = run('list', '--recipes', t.dir);
    assert.equal(l.code, 4, l.out);
    assert.equal(l.json.error.code, 'duplicate-recipe-id');
    assert.match(l.json.error.message, /shared-id/);
    assert.match(l.json.error.message, /shared-id\.json/);
    assert.match(l.json.error.message, /copy\.json/);
  } finally { t.cleanup(); }
});

test('inspect --text marks untested operations and warns on agent-reported-only evidence', () => {
  const root = tempRecipes([]);
  const responses = path.join(root.root, 'bridge-responses.md');
  writeFileSync(responses, 'notes\n');
  const t = tempRecipes([makeRecipe({
    id: 'fixture-text',
    evidence: [evidence('A1', { kind: 'report', path: responses, summary: 'Agent-reported 9 HP' })],
    operations: {
      discovery: op('untested'), playback: op('untested'), appearance: op('not-applicable'), controls: op('untested'),
      gameplayHits: op('observed-partial', ['A1'], 'hits'), naturalCleanup: op('untested'),
      explicitCleanup: op('untested'), poolReuse: op('untested'),
    },
  })]);
  try {
    const r = run('inspect', 'fixture-text', '--recipes', t.dir, '--generated', FIXTURE_GENERATED, '--text');
    assert.equal(r.code, 0, r.out);
    assert.match(r.out, /gameplayHits\s+observed-partial\s+\[agent-reported only; media none; run none\]/);
    assert.match(r.out, /not independently recorded/);
    assert.match(r.out, /poolReuse\s+untested\s+\[no evidence; untested\]/);
    assert.match(r.out, /appearance\s+not-applicable\s+\[no evidence; untested\]/);
    assert.match(r.out, /build summary: stamped=0 unstamped=1 stale=0 matchesCatalog=0/);
    assert.match(r.out, /current verification claimable: false/);
  } finally {
    t.cleanup();
    root.cleanup();
  }
});

test('inspect --text flags a cited incomplete run and does not treat it as support', () => {
  const runDir = tempRunDir(makeRunReport({ status: 'incomplete', reason: 'leftover' }));
  const t = tempRecipes([makeRecipe({
    id: 'fixture-text-run',
    evidence: [evidence('R1', { kind: 'rig-run', path: runDir.dir, summary: 'incomplete cleanup' })],
    operations: {
      discovery: op('untested'), playback: op('observed-success', ['R1']), appearance: op('untested'),
      controls: op('untested'), gameplayHits: op('not-applicable'), naturalCleanup: op('untested'),
      explicitCleanup: op('untested'), poolReuse: op('untested'),
    },
  })]);
  try {
    const r = run('inspect', 'fixture-text-run', '--recipes', t.dir, '--generated', FIXTURE_GENERATED, '--text');
    assert.equal(r.code, 0, r.out);
    assert.match(r.out, /playback\s+observed-success\s+\[[^\]]*run not complete/);
    assert.doesNotMatch(r.out, /playback[^\n]*run complete/);
    const store = loadRecipes(t.dir);
    const json = inspectRecipe(store, 'fixture-text-run');
    assert.deepEqual(json.operations.playback.support.runsNotComplete, ['R1']);
    assert.equal(json.operations.playback.support.strongest, 'none');
  } finally {
    t.cleanup();
    runDir.cleanup();
  }
});

test('malformed recipe fields make list exit 4 with invalid-recipe-file, never unexpected', () => {
  const missingStatus = makeRecipe();
  delete missingStatus.operations.playback.status;
  const numericStatus = makeRecipe();
  numericStatus.operations.playback.status = 7;
  const cases = [
    missingStatus,
    numericStatus,
    makeRecipe({ controls: [{ name: 'x', tested: null, limitations: 'n' }] }),
    makeRecipe({ controls: [{ name: 'x', tested: [null], limitations: 'n' }] }),
    makeRecipe({ evidence: null }),
  ];
  for (const doc of cases) {
    const t = tempRecipes([doc]);
    try {
      const l = run('list', '--recipes', t.dir);
      assert.equal(l.code, 4, l.out);
      assert.equal(l.json.error.code, 'invalid-recipe-file');
      assert.notEqual(l.code, 5);
    } finally { t.cleanup(); }
  }
});
