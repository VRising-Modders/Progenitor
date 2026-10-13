import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { existsSync, writeFileSync } from 'node:fs';
import { loadRecipes, searchRecipes, inspectRecipe, validateRecipe, validateRecipeDirectory, RecipeError, MAX_LIMIT } from '../lib/recipes.mjs';
import { loadInventory, searchInventory, GENERATED_FILES } from '../lib/inventory.mjs';
import { REAL_RECIPES, FIXTURE_GENERATED, FIXTURE_SHA, makeRecipe, evidence, op, tempRecipes } from './helpers.mjs';

// Recorded rig runs are local evidence outside the repository. The tests that read them run only
// where PROGENITOR_LOCAL_EVIDENCE names that folder; elsewhere they are skipped, not failed.
const LOCAL_EVIDENCE = process.env.PROGENITOR_LOCAL_EVIDENCE;
const NEEDS_EVIDENCE = LOCAL_EVIDENCE && existsSync(LOCAL_EVIDENCE)
  ? {}
  : { skip: 'needs PROGENITOR_LOCAL_EVIDENCE with the recorded rig runs' };

const real = loadRecipes(REAL_RECIPES);
const fixtureInventory = loadInventory(FIXTURE_GENERATED);

const A7_CORE = '42c9c081d87bae5f79d1deecdfa40711b23bef05142cedfecd72f4b202e3c402';
const A7_DEV = '9edb9edcfd908ce0d501c55d20dcf245bab21345e1c9d33e3d2db07535e59b1d';
const A7_SAMPLE = '6c81848ba12cba715c31614757a46939444dd22be6a42906a4c5b10146927f07';
const A7_SERVER = `generation:4; core:${A7_CORE}; dev:${A7_DEV}; sample:${A7_SAMPLE}`;
const A7_CLIENT = `generation:5; core:${A7_CORE}; dev:${A7_DEV}; sample:${A7_SAMPLE}`;
const A10_DEV = '85cd21a21623e5da7da5349523997b7ff76656e45816807a42a8700a6fa7337c';
const A10_SERVER = `generation:1; core:${A7_CORE}; dev:${A10_DEV}; sample:d698ee0499bf72ee7fdf6f6fd8b12cf3215ec74cce4a25bf05ed02b00ff00679`;
const A10_CLIENT = `generation:2; core:${A7_CORE}; dev:${A10_DEV}; sample:b089d45112510541e3dea1f50102570795a4d42212086919a3e72c5f425bc640`;
const A11_SERVER = A10_SERVER;
const A11_CLIENT = `generation:1; core:${A7_CORE}; dev:${A10_DEV}; sample:b089d45112510541e3dea1f50102570795a4d42212086919a3e72c5f425bc640`;
const NULL_HASH_SIDES = {
  server: 'generation:4; core:null; dev:null; sample:null',
  client: 'generation:5; core:null; dev:null; sample:null',
};

// ------------------------------------------------------------ the checked-in recipes

test('checked-in recipes load, validate and carry no asset-wide verified flag', () => {
  assert.ok(real.recipes.length >= 5);
  for (const r of real.recipes) {
    assert.deepEqual(validateRecipe(r), []);
    assert.equal('verified' in r, false);
  }
});

test("'nova', 'Wendigo' and the signed prefab GUID resolve to the same family in the prefab-guid domain", () => {
  const byText = (q) => searchRecipes(real, q);
  const nova = byText('nova'), wendigo = byText('Wendigo');
  assert.deepEqual(nova.families, ['wendigo-frost-nova']);
  assert.deepEqual(wendigo.families, ['wendigo-frost-nova']);
  assert.equal(nova.results[0].recipeId, 'wendigo-frost-nova-ring', 'the payload recipe (exact alias) ranks first');

  const typed = byText('prefab:-1227952025');
  assert.equal(typed.parsed.domain, 'prefab-guid');
  assert.deepEqual(typed.families, ['wendigo-frost-nova']);
  for (const r of typed.results) {
    assert.equal(r.score, 100);
    assert.ok(r.matched.every(m => m.on === 'identity' && m.domain === 'prefab-guid' && m.value === '-1227952025' && m.exact));
  }
  const bare = byText('-1227952025');
  assert.equal(bare.parsed.kind, 'number');
  assert.deepEqual(bare.results.map(r => r.recipeId), typed.results.map(r => r.recipeId));
  assert.ok(bare.results.every(r => r.matched.every(m => m.domain === 'prefab-guid')), 'a bare hash reports which domain it hit');

  // The ring sequence is a different domain and must not be reported as a prefab GUID.
  const seq = byText('sequence:-40367522');
  assert.deepEqual(seq.results.map(r => r.recipeId), ['wendigo-frost-nova-ring']);
  assert.equal(searchRecipes(real, 'prefab:-40367522').total, 0);
});

test('Charm inspection shows caster and target roles and does not let particle evidence verify the beam', NEEDS_EVIDENCE, () => {
  const charm = searchRecipes(real, 'charm');
  assert.deepEqual(charm.families, ['charm-channeling']);
  assert.ok(charm.results.length >= 3);

  const target = inspectRecipe(real, 'charm-target-channel-sequence');
  const ids = Object.fromEntries(target.identities.map(i => [i.ref, i]));
  assert.equal(ids['target-debuff'].value, '-1760277810');
  assert.equal(ids['target-debuff'].domain, 'prefab-guid');
  assert.equal(ids['target-sequence'].value, '-541113174');
  assert.equal(ids['target-sequence'].domain, 'sequence-guid');
  assert.equal(ids['caster-buff'].value, '1002852223');
  const row = target.relationships.find(r => r.from === 'target-debuff' && r.to === 'target-sequence');
  assert.equal(row.status, 'known');
  assert.equal(row.provenance.method, 'live-log');
  const sides = target.relationships.find(r => r.from === 'caster-buff' && r.to === 'target-debuff');
  assert.equal(sides.status, 'inferred');
  assert.ok(target.relationships.some(r => r.status === 'unresolved' && /endpoint/i.test(r.to)), 'beam endpoint inputs stay unresolved');
  assert.equal(target.operations.appearance.status, 'observed-partial');
  assert.equal(target.operations.controls.status, 'observed-partial');
  assert.notEqual(target.operations.appearance.status, 'observed-success');
  assert.equal(target.currentVerification.claimable, false);

  const caster = inspectRecipe(real, 'charm-caster-link-sequence');
  assert.equal(caster.identities.find(i => i.ref === 'caster-sequence').value, '-389499105');
  assert.equal(caster.operations.appearance.status, 'observed-failure');
  assert.deepEqual(caster.doc.assetReferences.map(a => a.name), ['mat_Transparent_Weapon01']);

  const borrow = inspectRecipe(real, 'charm-loop-particle-borrow');
  assert.equal(borrow.kind, 'isolated-particle');
  assert.equal(borrow.operations.playback.status, 'observed-success');
  assert.equal(borrow.operations.poolReuse.status, 'untested');
  assert.equal(borrow.operations.explicitCleanup.status, 'observed-success');
  assert.equal(borrow.operations.explicitCleanup.support.agentReportedOnly, false);
  assert.ok(borrow.operations.explicitCleanup.support.runsComplete.includes('B5'));
  assert.ok(borrow.operations.explicitCleanup.support.runsComplete.includes('B6'));
  assert.deepEqual(borrow.operations.explicitCleanup.evidence, ['E4', 'B1', 'B3', 'B5', 'B6']);
  assert.match(borrow.operations.explicitCleanup.note, /Removal/);
  assert.match(borrow.operations.explicitCleanup.note, /Swap/);
  assert.match(borrow.operations.explicitCleanup.note, /manual recovery/);
  const b5 = borrow.evidence.find(e => e.id === 'B5');
  assert.equal(b5.kind, 'rig-run');
  assert.equal(b5.run.found, true);
  assert.equal(b5.run.status, 'complete');
  assert.equal(b5.run.agentReported, false);
  const b6 = borrow.evidence.find(e => e.id === 'B6');
  assert.equal(b6.kind, 'rig-run');
  assert.equal(b6.run.found, true);
  assert.equal(b6.run.status, 'complete');
  assert.equal(b6.run.agentReported, false);
  assert.equal(b6.path, '<local-evidence>/reviews/2026-09-08-client-sample-swaps/20260908T045301Z-swap-client');
  assert.equal(b6.build.contentCatalogSha256, 'b8bd8fa6cb1619330bad310ff082b77872e02ec27e4d1cd88a846125a25c1a01');
  assert.equal(b6.build.gameIdentity, 'vrising/1.1.13.0+r99712+b17/net25');
  assert.equal(b6.build.framework, null);
  assert.equal(b6.build.frameworkSides.server, A11_SERVER);
  assert.equal(b6.build.frameworkSides.client, A11_CLIENT);
  assert.equal(b6.stamps.framework.state, 'legacy-combined');
  assert.equal(b6.applicability, 'unknown-stamps');
  assert.match(b6.summary, /reason=Swap/);
  assert.match(b6.summary, /three consecutive client Sample swaps/i);
  assert.match(b6.summary, /manual recovery/);
});

test('Nova inspection separates tested radius/colour from hit attribution, cleanup and pool-reuse statuses', NEEDS_EVIDENCE, () => {
  const ring = inspectRecipe(real, 'wendigo-frost-nova-ring');
  assert.equal(ring.kind, 'gameplay-payload');
  assert.equal(ring.operations.playback.status, 'observed-success');
  assert.equal(ring.operations.naturalCleanup.status, 'observed-success');
  assert.equal(ring.operations.gameplayHits.status, 'observed-partial', 'measured by the rig; per-hit attribution unresolved');
  assert.equal(ring.operations.explicitCleanup.status, 'observed-success');
  assert.deepEqual(ring.operations.explicitCleanup.evidence, ['L5', 'L6', 'L9']);
  assert.equal(ring.operations.explicitCleanup.support.agentReportedOnly, false);
  assert.ok(ring.operations.explicitCleanup.support.runsComplete.includes('L9'));
  assert.match(ring.operations.explicitCleanup.note, /active ring observed at teardown/i);
  assert.match(ring.operations.explicitCleanup.note, /later absence/i);
  assert.match(ring.operations.explicitCleanup.note, /not a completed-destruction latency/i);
  assert.match(ring.operations.explicitCleanup.note, /Start-to-unload timing is not that/);
  assert.doesNotMatch(ring.operations.explicitCleanup.note, /no rig-run of its own/);
  assert.doesNotMatch(ring.operations.explicitCleanup.note, /not exercised/);
  const l9 = ring.evidence.find(e => e.id === 'L9');
  assert.equal(l9.kind, 'rig-run');
  assert.equal(l9.run.found, true);
  assert.equal(l9.run.status, 'complete');
  assert.equal(l9.run.agentReported, false);
  assert.equal(l9.path, '<local-evidence>/reviews/2026-09-07-catalog-cases-5/20260907T105917Z-active-ring-unload');
  assert.equal(l9.build.contentCatalogSha256, 'b8bd8fa6cb1619330bad310ff082b77872e02ec27e4d1cd88a846125a25c1a01');
  assert.equal(l9.build.gameIdentity, 'vrising/1.1.13.0+r99712+b17/net25');
  assert.equal(l9.build.framework, 'generation:4; core:42c9c081d87bae5f79d1deecdfa40711b23bef05142cedfecd72f4b202e3c402; dev:9edb9edcfd908ce0d501c55d20dcf245bab21345e1c9d33e3d2db07535e59b1d; sample:6c81848ba12cba715c31614757a46939444dd22be6a42906a4c5b10146927f07');
  assert.equal(l9.build.frameworkSides.server, A7_SERVER);
  assert.equal(l9.build.frameworkSides.client, A7_CLIENT);
  assert.deepEqual(ring.operations.gameplayHits.sides, ['server']);
  assert.deepEqual(ring.operations.explicitCleanup.sides, ['server']);
  assert.match(l9.summary, /active ring observed at teardown/i);
  assert.match(l9.summary, /not a completed-destruction latency/i);
  assert.doesNotMatch(l9.summary, /Case sequencing gap/);
  assert.equal(ring.operations.poolReuse.status, 'untested');
  assert.equal(ring.operations.poolReuse.support.strongest, 'none');
  assert.ok(ring.operations.gameplayHits.evidence.length > 0);
  assert.equal(ring.operations.gameplayHits.support.agentReportedOnly, false, 'hits independently recorded by the rig');
  assert.equal(ring.operations.gameplayHits.support.strongest, 'readback');
  assert.ok(ring.operations.gameplayHits.support.runsComplete.includes('L8'));
  const l8 = ring.evidence.find(e => e.id === 'L8');
  assert.equal(l8.kind, 'rig-run');
  assert.equal(l8.build.contentCatalogSha256, null, 'L8 stamps only what the run recorded');
  assert.equal(l8.build.gameIdentity, null, 'L8 stamps only what the run recorded');
  assert.equal(l8.build.framework, 'generation:4');
  assert.equal(l8.stamps.contentCatalogSha256.state, 'unknown');
  assert.equal(l8.stamps.gameIdentity.state, 'unknown');
  assert.equal(l8.stamps.framework.state, 'legacy-combined');
  assert.ok(l8.stamps.frameworkSides);
  assert.equal(l8.applicability, 'unknown-stamps');
  assert.equal(ring.operations.gameplayHits.applicability, 'unknown-stamps');
  assert.match(ring.operations.gameplayHits.note, /attribution/i, 'hits carry an attribution caveat');
  assert.match(ring.operations.gameplayHits.note, /independently recorded/i);
  assert.match(ring.operations.gameplayHits.note, /4\.0/);
  assert.equal(ring.currentVerification.claimable, false);
  assert.ok(ring.buildSummary);
  assert.equal(typeof ring.buildSummary.stamped, 'number');

  const look = inspectRecipe(real, 'wendigo-frost-nova-appearance');
  const radius = look.controls.find(c => c.name === 'radius');
  assert.equal(radius.tested[0].value, 18);
  assert.equal(radius.tested[0].authored, 12);
  assert.ok(radius.tested[0].evidence.length > 0);
  const colour = look.controls.find(c => c.name === 'crystal colour');
  assert.ok(colour.tested[0].evidence.length > 0);
  assert.match(colour.limitations, /Sample-specific/);
  assert.equal(look.operations.gameplayHits.status, 'untested');
  assert.equal(look.operations.poolReuse.status, 'observed-success', 'same-instance subsequent untinted reuse of previously tinted slots');
  assert.equal(look.operations.poolReuse.support.agentReportedOnly, false);
  assert.deepEqual(look.operations.poolReuse.evidence, ['A3', 'A4', 'A6', 'A10', 'A11']);
  assert.ok(look.operations.poolReuse.support.runsComplete.includes('A10'));
  assert.ok(look.operations.poolReuse.support.runsComplete.includes('A11'));
  assert.ok(look.operations.poolReuse.support.runsComplete.includes('A6'), 'A6 remains historical complete-run support offline');
  assert.match(look.operations.poolReuse.note, /10 previously tinted/);
  assert.match(look.operations.poolReuse.note, /A11/);
  assert.match(look.operations.poolReuse.note, /A10/);
  assert.match(look.operations.poolReuse.note, /readback and viewed visual/);
  assert.match(look.operations.poolReuse.note, /visual\.verified=false/);
  assert.match(look.operations.poolReuse.note, /unverified by the rig/);
  assert.match(look.operations.poolReuse.note, /does not analyse pixels/);
  assert.match(look.operations.poolReuse.note, /not the pass proof/);
  assert.match(look.operations.poolReuse.note, /122 identical rendererId\/goId pairs/);
  assert.match(look.operations.poolReuse.note, /No vanilla Wendigo/);
  assert.doesNotMatch(look.operations.poolReuse.note, /not captured \(readback and log only\)/);
  assert.deepEqual(look.operations.appearance.sides, ['server', 'client']);
  assert.deepEqual(look.operations.poolReuse.sides, ['server', 'client']);
  assert.deepEqual(look.operations.explicitCleanup.sides, ['server']);
  assert.match(look.operations.poolReuse.note, /appearance restore at unload \(Restore\(\)\) is still unobserved/);
  const a6 = look.evidence.find(e => e.id === 'A6');
  assert.equal(a6.kind, 'rig-run');
  assert.equal(a6.build.contentCatalogSha256, null, 'A6 stamps only what the run recorded');
  assert.equal(a6.build.gameIdentity, null, 'A6 stamps only what the run recorded');
  assert.equal(a6.build.framework, 'generation:4');
  assert.equal(a6.stamps.contentCatalogSha256.state, 'unknown');
  assert.equal(a6.stamps.gameIdentity.state, 'unknown');
  assert.equal(a6.stamps.framework.state, 'legacy-combined');
  assert.equal(a6.applicability, 'unknown-stamps');
  const a10 = look.evidence.find(e => e.id === 'A10');
  assert.equal(a10.kind, 'rig-run');
  assert.equal(a10.run.found, true);
  assert.equal(a10.run.status, 'complete');
  assert.equal(a10.run.agentReported, false);
  assert.equal(a10.path, '<local-evidence>/reviews/2026-09-07-catalog-cases-10/20260907T215010Z-pool-reuse-identity');
  assert.equal(a10.build.contentCatalogSha256, 'b8bd8fa6cb1619330bad310ff082b77872e02ec27e4d1cd88a846125a25c1a01');
  assert.equal(a10.build.gameIdentity, 'vrising/1.1.13.0+r99712+b17/net25');
  assert.equal(a10.build.framework, 'generation:1; core:42c9c081d87bae5f79d1deecdfa40711b23bef05142cedfecd72f4b202e3c402; dev:85cd21a21623e5da7da5349523997b7ff76656e45816807a42a8700a6fa7337c; sample:d698ee0499bf72ee7fdf6f6fd8b12cf3215ec74cce4a25bf05ed02b00ff00679');
  assert.equal(a10.build.frameworkSides.server, A10_SERVER);
  assert.equal(a10.build.frameworkSides.client, A10_CLIENT);
  assert.notEqual(a10.build.frameworkSides.client, a10.build.frameworkSides.server, 'A10 client Sample is not the combined/server Sample');
  assert.equal(a10.stamps.contentCatalogSha256.state, 'not-checkable-offline');
  assert.equal(a10.stamps.gameIdentity.state, 'not-checkable-offline');
  assert.equal(a10.stamps.framework.state, 'legacy-combined');
  assert.equal(a10.applicability, 'unknown-stamps');
  assert.match(a10.summary, /T=10 crystal slots/);
  assert.match(a10.summary, /R=T \(10\)/);
  assert.match(a10.summary, /capture\.available false/);
  const a11 = look.evidence.find(e => e.id === 'A11');
  assert.equal(a11.kind, 'rig-run');
  assert.equal(a11.run.found, true);
  assert.equal(a11.run.status, 'complete');
  assert.equal(a11.run.agentReported, false);
  assert.equal(a11.path, '<local-evidence>/reviews/2026-09-08-pool-reuse-visual-2/20260908T051452Z-pool-reuse-identity');
  assert.equal(a11.build.contentCatalogSha256, 'b8bd8fa6cb1619330bad310ff082b77872e02ec27e4d1cd88a846125a25c1a01');
  assert.equal(a11.build.gameIdentity, 'vrising/1.1.13.0+r99712+b17/net25');
  assert.equal(a11.build.framework, null);
  assert.equal(a11.build.frameworkSides.server, A11_SERVER);
  assert.equal(a11.build.frameworkSides.client, A11_CLIENT);
  assert.notEqual(a11.build.frameworkSides.client, a11.build.frameworkSides.server, 'A11 client Sample is not the server Sample');
  assert.notEqual(a11.build.frameworkSides.client, A10_CLIENT, 'A11 client generation is 1, not A10 generation 2');
  assert.equal(a11.stamps.contentCatalogSha256.state, 'not-checkable-offline');
  assert.equal(a11.stamps.gameIdentity.state, 'not-checkable-offline');
  assert.equal(a11.stamps.framework.state, 'legacy-combined');
  assert.equal(a11.applicability, 'unknown-stamps');
  assert.match(a11.summary, /T=10/);
  assert.match(a11.summary, /R=10 on attempt 1/);
  assert.match(a11.summary, /censusDelayMs 1500/);
  assert.match(a11.summary, /visual\.verified=false/);
  assert.match(a11.summary, /tinted frame green/);
  assert.match(a11.summary, /default ice-blue, no green/);
  assert.match(a11.summary, /Failed first attempt/);
  assert.equal(look.operations.poolReuse.applicability, 'unknown-stamps');
  assert.equal(look.operations.explicitCleanup.status, 'observed-success', 'unload with a live tinted ring: Sample ClearRings destroyed the live ring');
  assert.equal(look.operations.explicitCleanup.support.agentReportedOnly, false);
  assert.ok(look.operations.explicitCleanup.support.runsComplete.includes('A7'));
  assert.ok(look.operations.explicitCleanup.support.runsComplete.includes('A8'));
  assert.ok(look.operations.explicitCleanup.support.runsComplete.includes('A9'));
  assert.deepEqual(look.operations.explicitCleanup.evidence, ['E4', 'A3', 'A7', 'A8', 'A9']);
  assert.match(look.operations.explicitCleanup.note, /active ring observed at teardown/i);
  assert.match(look.operations.explicitCleanup.note, /later absence/i);
  assert.match(look.operations.explicitCleanup.note, /not a completed-destruction latency/i);
  assert.match(look.operations.explicitCleanup.note, /appearance restore at unload \(Restore\(\)\) logs nothing and was not separately observed/);
  const a7 = look.evidence.find(e => e.id === 'A7');
  const a8 = look.evidence.find(e => e.id === 'A8');
  const a9 = look.evidence.find(e => e.id === 'A9');
  assert.match(a7.summary, /Case sequencing gap: teardown `\.frostdash off` was not handled because the plugin was on hold/);
  assert.match(a8.summary, /Case sequencing gap: teardown `\.frostdash off` was not handled because the plugin was on hold/);
  assert.equal(a7.path, '<local-evidence>/reviews/2026-09-07-catalog-cases-4/20260907T104023Z-active-ring-unload');
  assert.equal(a8.path, '<local-evidence>/reviews/2026-09-07-catalog-cases-4/20260907T104052Z-active-ring-unload');
  assert.equal(a9.path, '<local-evidence>/reviews/2026-09-07-catalog-cases-5/20260907T105917Z-active-ring-unload');
  assert.match(a9.summary, /active ring observed at teardown/i);
  assert.match(a9.summary, /not a completed-destruction latency/i);
  assert.doesNotMatch(a9.summary, /Case sequencing gap/);
  for (const [id, ev] of [['A7', a7], ['A8', a8], ['A9', a9]]) {
    assert.equal(ev.kind, 'rig-run', id);
    assert.equal(ev.run.found, true, id);
    assert.equal(ev.run.status, 'complete', id);
    assert.equal(ev.run.agentReported, false, id);
    assert.equal(ev.build.contentCatalogSha256, 'b8bd8fa6cb1619330bad310ff082b77872e02ec27e4d1cd88a846125a25c1a01', id);
    assert.equal(ev.build.gameIdentity, 'vrising/1.1.13.0+r99712+b17/net25', id);
    assert.equal(ev.build.framework, 'generation:4; core:42c9c081d87bae5f79d1deecdfa40711b23bef05142cedfecd72f4b202e3c402; dev:9edb9edcfd908ce0d501c55d20dcf245bab21345e1c9d33e3d2db07535e59b1d; sample:6c81848ba12cba715c31614757a46939444dd22be6a42906a4c5b10146927f07', id);
    assert.equal(ev.stamps.contentCatalogSha256.state, 'not-checkable-offline', `${id} catalog stamp is recorded; inspect without inventory cannot compare it`);
    assert.equal(ev.stamps.gameIdentity.state, 'not-checkable-offline', id);
    assert.equal(ev.stamps.framework.state, 'legacy-combined', id);
    assert.equal(ev.build.frameworkSides.server, A7_SERVER, id);
    assert.equal(ev.build.frameworkSides.client, A7_CLIENT, id);
    assert.equal(ev.applicability, 'unknown-stamps', id);
  }
  assert.equal(look.operations.explicitCleanup.applicability, 'unknown-stamps');
  assert.equal(look.currentVerification.claimable, false);
  assert.equal(look.runtimeAvailability.state, 'not-observed-offline');
  // Every stamp on the 2026-09-06 recordings is unknown and stays unknown; the 2026-09-07 ones are stamped.
  assert.ok(look.evidence.some(e => e.date === '2026-09-07' && typeof e.stamps.contentCatalogSha256.evidence === 'string' && e.stamps.gameIdentity.evidence === 'vrising/1.1.13.0+r99712+b17/net25'), 'the live entries carry full stamps');
  for (const e of look.evidence.filter(e => e.date === '2026-09-06')) {
    assert.equal(e.stamps.framework.state, 'unknown');
    assert.equal(e.stamps.gameIdentity.state, 'unknown');
    assert.equal(e.applicability, 'unknown-stamps');
  }
});

test('R74: A6 and B5 generation-only runs do not match a current build with A7 full hashes', NEEDS_EVIDENCE, () => {
  const currentBuild = {
    contentCatalogSha256: 'b8bd8fa6cb1619330bad310ff082b77872e02ec27e4d1cd88a846125a25c1a01',
    gameIdentity: 'vrising/1.1.13.0+r99712+b17/net25',
    framework: null,
    frameworkSides: { server: A7_SERVER, client: A7_CLIENT },
  };
  const look = inspectRecipe(real, 'wendigo-frost-nova-appearance', { currentBuild });
  const a6 = look.evidence.find(e => e.id === 'A6');
  const a10Current = look.evidence.find(e => e.id === 'A10');
  const a11Current = look.evidence.find(e => e.id === 'A11');
  assert.deepEqual(a6.build.frameworkSides, NULL_HASH_SIDES);
  assert.equal(a6.applicability, 'unknown-stamps');
  assert.ok(look.operations.poolReuse.support.runsOtherBuild.includes('A6'));
  assert.ok(!look.operations.poolReuse.support.runsComplete.includes('A6'));
  assert.equal(a10Current.applicability, 'stale', 'A10 framework hashes differ from the A7 current build');
  assert.ok(look.operations.poolReuse.support.runsOtherBuild.includes('A10'));
  assert.ok(!look.operations.poolReuse.support.runsComplete.includes('A10'));
  assert.equal(a11Current.applicability, 'stale', 'A11 framework hashes differ from the A7 current build');
  assert.ok(look.operations.poolReuse.support.runsOtherBuild.includes('A11'));
  assert.ok(!look.operations.poolReuse.support.runsComplete.includes('A11'));
  const a7 = look.evidence.find(e => e.id === 'A7');
  const a8 = look.evidence.find(e => e.id === 'A8');
  const a9 = look.evidence.find(e => e.id === 'A9');
  assert.equal(a7.applicability, 'matches-checkable-stamps');
  assert.equal(a8.applicability, 'matches-checkable-stamps');
  assert.equal(a9.applicability, 'matches-checkable-stamps');
  assert.ok(look.operations.explicitCleanup.support.runsComplete.includes('A7'));
  assert.ok(look.operations.explicitCleanup.support.runsComplete.includes('A8'));
  assert.ok(look.operations.explicitCleanup.support.runsComplete.includes('A9'));
  assert.ok(!look.operations.explicitCleanup.support.runsOtherBuild.includes('A7'));
  assert.ok(!look.operations.explicitCleanup.support.runsOtherBuild.includes('A9'));

  const borrow = inspectRecipe(real, 'charm-loop-particle-borrow', { currentBuild });
  const b5 = borrow.evidence.find(e => e.id === 'B5');
  const b6Current = borrow.evidence.find(e => e.id === 'B6');
  assert.deepEqual(b5.build.frameworkSides, NULL_HASH_SIDES);
  assert.deepEqual(borrow.operations.explicitCleanup.sides, ['client']);
  assert.equal(b5.applicability, 'unknown-stamps');
  assert.ok(borrow.operations.explicitCleanup.support.runsOtherBuild.includes('B5'));
  assert.ok(!borrow.operations.explicitCleanup.support.runsComplete.includes('B5'));
  assert.equal(b6Current.applicability, 'stale', 'B6 framework hashes differ from the A7 current build');
  assert.ok(borrow.operations.explicitCleanup.support.runsOtherBuild.includes('B6'));
  assert.ok(!borrow.operations.explicitCleanup.support.runsComplete.includes('B6'));

  const otherStamp = 'generation:4; core:42c9c081d87bae5f79d1deecdfa40711b23bef05142cedfecd72f4b202e3c402; dev:9edb9edcfd908ce0d501c55d20dcf245bab21345e1c9d33e3d2db07535e59b1d; sample:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const otherSample = {
    ...currentBuild,
    framework: otherStamp,
    frameworkSides: { server: otherStamp, client: otherStamp },
  };
  const staleLook = inspectRecipe(real, 'wendigo-frost-nova-appearance', { currentBuild: otherSample });
  assert.equal(staleLook.evidence.find(e => e.id === 'A7').applicability, 'stale');
  assert.ok(staleLook.operations.explicitCleanup.support.runsOtherBuild.includes('A7'));
  assert.ok(!staleLook.operations.explicitCleanup.support.runsComplete.includes('A7'));

  const offline = inspectRecipe(real, 'wendigo-frost-nova-appearance');
  assert.equal(offline.currentBuild.framework, null);
  assert.equal(offline.currentBuild.frameworkSides.server, null);
  assert.equal(offline.currentBuild.frameworkSides.client, null);

  const a10ClientChanged = inspectRecipe(real, 'wendigo-frost-nova-appearance', {
    currentBuild: {
      ...currentBuild,
      frameworkSides: {
        server: A10_SERVER,
        client: A10_SERVER,
      },
    },
  });
  assert.equal(
    a10ClientChanged.operations.poolReuse.applicability,
    'stale',
    'equal server hashes with a different client Sample must not match a paired A10 claim',
  );
  assert.notEqual(a10ClientChanged.operations.poolReuse.applicability, 'matches-checkable-stamps');
  assert.equal(offline.evidence.find(e => e.id === 'A7').applicability, 'unknown-stamps');
  assert.equal(offline.evidence.find(e => e.id === 'A9').applicability, 'unknown-stamps');
  assert.equal(offline.evidence.find(e => e.id === 'A10').applicability, 'unknown-stamps');
  assert.equal(offline.evidence.find(e => e.id === 'A11').applicability, 'unknown-stamps');
  assert.equal(offline.operations.explicitCleanup.applicability, 'unknown-stamps');
  assert.equal(offline.operations.poolReuse.applicability, 'unknown-stamps');
  assert.ok(offline.operations.explicitCleanup.support.runsComplete.includes('A7'));
  assert.ok(offline.operations.explicitCleanup.support.runsComplete.includes('A9'));
  assert.ok(offline.operations.poolReuse.support.runsComplete.includes('A10'));
  assert.ok(offline.operations.poolReuse.support.runsComplete.includes('A11'));

  const ringCurrent = inspectRecipe(real, 'wendigo-frost-nova-ring', { currentBuild });
  const l9 = ringCurrent.evidence.find(e => e.id === 'L9');
  assert.equal(l9.applicability, 'matches-checkable-stamps');
  assert.ok(ringCurrent.operations.explicitCleanup.support.runsComplete.includes('L9'));
  assert.equal(l8StampsStayUnknown(ringCurrent), true);
});

function l8StampsStayUnknown(ring) {
  const l8 = ring.evidence.find(e => e.id === 'L8');
  return l8.build.contentCatalogSha256 === null
    && l8.build.gameIdentity === null
    && l8.build.framework === 'generation:4'
    && l8.stamps.framework.state === 'legacy-combined'
    && l8.applicability === 'unknown-stamps';
}

// ------------------------------------------------------------ fixture recipes

function fixtureStore() {
  const bigIds = makeRecipe({
    id: 'fixture-big-ids', family: { id: 'fixture-b', title: 'B' }, aliases: ['twin', 'bigids'],
    identities: [
      { ref: 'obj-a', role: 'archive object', domain: 'archive-object', value: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/9142015999717103345', provenance: { source: 'fixture' } },
      { ref: 'obj-max', role: 'archive object', domain: 'archive-object', value: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/9223372036854775807', provenance: { source: 'fixture' } },
      { ref: 'obj-53', role: 'archive object', domain: 'archive-object', value: 'cccccccccccccccccccccccccccccccc/9007199254740993', provenance: { source: 'fixture' } },
      { ref: 'guid', role: 'prefab', domain: 'prefab-guid', value: '-7', provenance: { source: 'fixture' } },
    ],
    assetReferences: [
      { name: 'Icon', unityType: 'GameObject', role: 'dup', provenance: { source: 'fixture' } },
      { name: 'Part_Fixture_Loop_01', unityType: 'GameObject', role: 'dup2', provenance: { source: 'fixture' } },
      { name: 'Beam_Fixture_Channeling_01', unityType: 'GameObject', role: 'unique', provenance: { source: 'fixture' } },
      { name: 'Nope_Missing', unityType: 'GameObject', role: 'absent', provenance: { source: 'fixture' } },
      { name: 'Icon', unityType: 'Material', role: 'dup-material', provenance: { source: 'fixture' } },
    ],
    relationships: [
      { from: 'guid', to: 'asset:Icon', role: 'candidate', status: 'inferred', provenance: { source: 'fixture', method: 'name-match' } },
      { from: 'guid', to: 'obj-a', role: 'archive-association', status: 'unresolved', provenance: { source: 'fixture', method: 'reasoning' } },
    ],
    evidence: [
      evidence('E-match', { build: { contentCatalogSha256: FIXTURE_SHA, gameIdentity: 'vrising/fixture', framework: 'gen-1' } }),
      evidence('E-stale', { build: { contentCatalogSha256: 'olderbuildsha', gameIdentity: 'vrising/fixture', framework: 'gen-1' } }),
      evidence('E-unknown', { build: { contentCatalogSha256: FIXTURE_SHA, gameIdentity: 'vrising/fixture', framework: null } }),
      evidence('E-missing-media', { kind: 'screenshot', path: 'C:/definitely/not/here/shot.png' }),
    ],
    operations: {
      discovery: op('observed-success', ['E-match']), playback: op('observed-success', ['E-stale']), appearance: op('observed-success', ['E-unknown']),
      controls: op('observed-success', ['E-missing-media']), gameplayHits: op('untested'), naturalCleanup: op('untested'),
      explicitCleanup: op('observed-success', ['E-match', 'E-stale']), poolReuse: op('untested'),
    },
  });
  const alpha = makeRecipe({ id: 'fixture-alpha', family: { id: 'fixture-a', title: 'A' }, aliases: ['twin'] });
  const zeta = makeRecipe({ id: 'fixture-zeta', family: { id: 'fixture-a', title: 'A' }, aliases: ['twin'] });
  return tempRecipes([bigIds, alpha, zeta]);
}

test('large object identifiers round-trip through load, query and output with no precision loss', () => {
  const t = fixtureStore();
  try {
    const store = loadRecipes(t.dir);
    const r = store.recipes.find(x => x.id === 'fixture-big-ids');
    assert.equal(r.identities.find(i => i.ref === 'obj-max').value, 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/9223372036854775807');
    const hit = searchRecipes(store, 'object:cccccccccccccccccccccccccccccccc/9007199254740993');
    assert.equal(hit.total, 1);
    assert.equal(hit.results[0].matched[0].value, 'cccccccccccccccccccccccccccccccc/9007199254740993');
    const out = JSON.stringify(inspectRecipe(store, 'fixture-big-ids', { inventory: fixtureInventory }));
    assert.ok(out.includes('9223372036854775807'));
    assert.ok(out.includes('9007199254740993'));
    assert.ok(out.includes('-9223372036854775808'), 'the inventory candidate with the most negative pathID is emitted verbatim');
    assert.ok(!out.includes('9007199254740992'), 'no double rounding anywhere in the output');
    const bare = searchRecipes(store, '9223372036854775807');
    assert.equal(bare.total, 1);
    assert.equal(bare.results[0].matched[0].exact, false, 'a pathID without its archive is not an exact identity match');
  } finally { t.cleanup(); }
});

test('duplicate names stay distinct in inventory search and make associations ambiguous, absent names unresolved', () => {
  assert.equal(fixtureInventory.available, true);
  const icons = searchInventory(fixtureInventory, 'icon', 10);
  assert.equal(icons.total, 3);
  assert.deepEqual(icons.rows.map(r => `${r.type}:${r.archiveId}/${r.pathID}`), [
    'GameObject:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/9142015999717103345',
    'GameObject:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/9223372036854775807',
    'Material:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/42',
  ]);
  const t = fixtureStore();
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-big-ids', { inventory: fixtureInventory });
    const byRole = Object.fromEntries(r.inventoryAssociations.map(a => [a.role, a]));
    assert.equal(byRole.dup.status, 'ambiguous');
    assert.equal(byRole.dup.candidates.length, 2, 'the Material named Icon is excluded by type');
    assert.equal(byRole['dup-material'].status, 'inferred');
    assert.equal(byRole['dup-material'].candidates[0].pathID, '42');
    assert.equal(byRole.dup2.status, 'ambiguous');
    assert.equal(byRole.unique.status, 'inferred');
    assert.equal(byRole.unique.candidates[0].pathID, '1805408021845029941');
    assert.equal(byRole.absent.status, 'unresolved');
    assert.deepEqual(byRole.absent.candidates, []);
    assert.ok(r.inventoryAssociations.every(a => a.method === 'name-match'));
    // The unresolved archive link on the recipe itself is preserved as unresolved.
    assert.equal(r.relationships.find(x => x.role === 'archive-association').status, 'unresolved');
  } finally { t.cleanup(); }
});

test('a changed build fingerprint cannot inherit a current claim; history stays readable; unknown stamps stay unknown', () => {
  const t = fixtureStore();
  try {
    const store = loadRecipes(t.dir);
    const live = { contentCatalogSha256: FIXTURE_SHA, gameIdentity: 'vrising/fixture', framework: 'gen-1' };
    const r = inspectRecipe(store, 'fixture-big-ids', { inventory: fixtureInventory, currentBuild: live });
    const ev = Object.fromEntries(r.evidence.map(e => [e.id, e]));
    assert.equal(ev['E-match'].applicability, 'matches-checkable-stamps');
    assert.equal(ev['E-stale'].applicability, 'stale');
    assert.equal(ev['E-stale'].stamps.contentCatalogSha256.state, 'changed');
    assert.equal(ev['E-stale'].stamps.contentCatalogSha256.evidence, 'olderbuildsha', 'the historical stamp is still reported');
    assert.equal(ev['E-unknown'].applicability, 'unknown-stamps');
    assert.equal(ev['E-unknown'].stamps.framework.state, 'unknown');
    assert.equal(ev['E-unknown'].stamps.framework.evidence, null);
    assert.equal(r.operations.playback.status, 'observed-success', 'the historical observation is not rewritten');
    assert.equal(r.operations.playback.applicability, 'stale');
    assert.equal(r.operations.explicitCleanup.applicability, 'stale', 'one stale recording taints the operation');
    assert.equal(r.operations.discovery.applicability, 'matches-checkable-stamps');
    assert.equal(r.operations.appearance.applicability, 'unknown-stamps');
    assert.equal(r.currentVerification.claimable, false);

    // Offline default view: the live-only stamps are not checkable, so nothing can match fully.
    const offline = inspectRecipe(store, 'fixture-big-ids', { inventory: fixtureInventory });
    assert.equal(offline.currentBuild.contentCatalogSha256, FIXTURE_SHA);
    assert.equal(offline.currentBuild.gameIdentity, null);
    assert.equal(offline.evidence.find(e => e.id === 'E-match').stamps.gameIdentity.state, 'not-checkable-offline');
    assert.equal(offline.evidence.find(e => e.id === 'E-match').applicability, 'unknown-stamps');
    assert.equal(offline.evidence.find(e => e.id === 'E-stale').applicability, 'stale');
  } finally { t.cleanup(); }
});

test('missing inventory and missing media are reported separately from the historical claim', () => {
  const missingInv = loadInventory(path.join(FIXTURE_GENERATED, 'does-not-exist'));
  assert.equal(missingInv.available, false);
  assert.ok(missingInv.reason.includes('index-catalog.mjs'));
  const t = fixtureStore();
  try {
    const store = loadRecipes(t.dir);
    const r = inspectRecipe(store, 'fixture-big-ids', { inventory: missingInv });
    assert.equal(r.inventory.state, 'inventory-unavailable');
    assert.equal(r.inventory.note, 'index presence only; runtime availability is not observed offline');
    assert.ok(r.inventoryAssociations.length > 0);
    assert.ok(r.inventoryAssociations.every(a => a.status === 'inventory-unavailable' && a.candidates.length === 0));
    assert.equal(r.operations.discovery.status, 'observed-success', 'the claim is untouched by inventory absence');
    assert.equal(r.currentBuild.contentCatalogSha256, null);
    const shot = r.evidence.find(e => e.id === 'E-missing-media');
    assert.equal(shot.media.present, false);
    assert.equal(r.operations.controls.status, 'observed-success');
    assert.equal(r.operations.controls.mediaPresent, false, 'cited screenshot missing → mediaPresent false');
    assert.equal(r.operations.discovery.mediaPresent, null, 'no media cited → mediaPresent null');
    const s = searchRecipes(store, 'twin', { includeInventory: true, inventory: missingInv });
    assert.equal(s.inventory.state, 'inventory-unavailable');
    assert.equal(s.inventory.note, 'index presence only; runtime availability is not observed offline');
    assert.equal(s.total, 3);
    const none = inspectRecipe(store, 'fixture-big-ids');
    assert.equal(none.inventory.state, 'not-loaded');
    assert.equal(none.inventory.note, 'index presence only; runtime availability is not observed offline');
  } finally { t.cleanup(); }
});

test('queries are bounded, stably ordered and validated; unknown ids are structured errors', () => {
  const t = fixtureStore();
  try {
    const store = loadRecipes(t.dir);
    const all = searchRecipes(store, 'twin');
    assert.deepEqual(all.results.map(r => r.recipeId), ['fixture-alpha', 'fixture-big-ids', 'fixture-zeta'], 'equal scores: one best hit per family first, then remaining by family id then recipe id');
    const two = searchRecipes(store, 'twin', { limit: 2 });
    assert.equal(two.total, 3);
    assert.equal(two.truncated, true);
    assert.deepEqual(two.results.map(r => r.recipeId), ['fixture-alpha', 'fixture-big-ids']);
    assert.deepEqual(searchRecipes(store, 'twin', { limit: '2' }).results.map(r => r.recipeId), ['fixture-alpha', 'fixture-big-ids']);
    for (const bad of [0, -1, MAX_LIMIT + 1, 1.5, 'abc', '']) {
      assert.throws(() => searchRecipes(store, 'twin', { limit: bad }), e => e instanceof RecipeError && e.code === 'invalid-limit', String(bad));
    }
    assert.throws(() => searchRecipes(store, ''), e => e.code === 'invalid-query');
    assert.throws(() => searchRecipes(store, 'bogus:1'), e => e.code === 'invalid-identity-domain');
    assert.throws(() => searchRecipes(store, 'object:zz/1'), e => e.code === 'invalid-identity-value');
    const empty = searchRecipes(store, 'no-such-thing');
    assert.equal(empty.total, 0);
    assert.ok(Array.isArray(empty.suggestions), 'empty search must carry suggestions so an agent does not conclude no recipe from total 0 alone');
    try { inspectRecipe(store, 'fixture-nope'); assert.fail('expected unknown-recipe'); }
    catch (e) {
      assert.equal(e.code, 'unknown-recipe');
      assert.deepEqual(e.details.suggestions, []);
      assert.equal(e.toJSON().error.known.length, 3);
    }
    try { inspectRecipe(store, 'big'); assert.fail('expected unknown-recipe'); }
    catch (e) { assert.deepEqual(e.details.suggestions, ['fixture-big-ids']); }
    assert.throws(() => inspectRecipe(store, ''), e => e.code === 'invalid-recipe-id');
  } finally { t.cleanup(); }
});

test('the validator refuses entity identities, name-matched archive facts, asset-wide flags and numeric GUIDs', () => {
  const ok = makeRecipe();
  assert.deepEqual(validateRecipe(ok), []);
  const entity = makeRecipe({ identities: [{ ref: 'e', role: 'x', domain: 'entity', value: '566313:130', provenance: { source: 'log' } }] });
  assert.ok(validateRecipe(entity).some(p => /session evidence/.test(p)));
  const numeric = makeRecipe({ identities: [{ ref: 'g', role: 'x', domain: 'prefab-guid', value: -1227952025, provenance: { source: 'src' } }] });
  assert.ok(validateRecipe(numeric).some(p => /must be a string/.test(p)));
  const flag = makeRecipe({ verified: true });
  assert.ok(validateRecipe(flag).some(p => /asset-wide/.test(p)));
  const equated = makeRecipe({
    identities: [
      { ref: 'g', role: 'x', domain: 'prefab-guid', value: '-1', provenance: { source: 's' } },
      { ref: 'o', role: 'y', domain: 'archive-object', value: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/1', provenance: { source: 's' } },
    ],
    relationships: [{ from: 'g', to: 'o', role: 'is', status: 'known', provenance: { source: 's', method: 'name-match' } }],
  });
  assert.ok(validateRecipe(equated).some(p => /never silently equated/.test(p)));
  const external = makeRecipe({ relationships: [{ from: 'main-prefab', to: 'external:thing', role: 'r', status: 'known', provenance: { source: 's', method: 'live-log' } }] });
  assert.ok(validateRecipe(external).some(p => /external/.test(p)));
  const liveTested = makeRecipe({ implementation: [{ path: 'x.cs', status: 'live-tested 2026-09-07; file present' }] });
  assert.ok(validateRecipe(liveTested).some(p => /status must start with/.test(p)));
  const observedWithout = makeRecipe({ operations: { ...makeRecipe().operations, playback: op('observed-success', []) } });
  assert.ok(validateRecipe(observedWithout).some(p => /needs at least one evidence id/.test(p)));
  const danglingRef = makeRecipe({ relationships: [{ from: 'main-prefab', to: 'nope', role: 'r', status: 'unresolved', provenance: { source: 's', method: 'reasoning' } }] });
  assert.ok(validateRecipe(danglingRef).some(p => /is not an identity ref/.test(p)));
  const badStamp = makeRecipe({ evidence: [evidence('E1', { build: { contentCatalogSha256: null } })] });
  assert.ok(validateRecipe(badStamp).some(p => /build\.gameIdentity is missing/.test(p)));
});

test('loading refuses a directory with two files claiming one id, and an invalid file names its problems', () => {
  const t = tempRecipes([makeRecipe({ id: 'same' })]);
  try {
    writeFileSync(path.join(t.dir, 'same-copy.json'), JSON.stringify(makeRecipe({ id: 'same', aliases: ['other'] })));
    const dup = validateRecipeDirectory(t.dir);
    assert.ok(dup.invalid >= 1);
    assert.ok(dup.directoryProblems.some(p => p.includes('same') && p.includes('same.json') && p.includes('same-copy.json')));
    assert.ok(dup.report.every(x => x.valid), 'each file is individually valid; the directory is not');
    assert.throws(() => loadRecipes(t.dir), e => e instanceof RecipeError && e.code === 'duplicate-recipe-id' && /same\.json/.test(e.message) && /same-copy\.json/.test(e.message));
    writeFileSync(path.join(t.dir, 'same-copy.json'), JSON.stringify(makeRecipe({ id: 'broken', verified: true, kind: 'nope' })));
    try { loadRecipes(t.dir); assert.fail('expected invalid-recipe-file'); }
    catch (e) {
      assert.equal(e.code, 'invalid-recipe-file');
      assert.ok(e.details.problems.length >= 2);
    }
    const broken = validateRecipeDirectory(t.dir);
    assert.ok(broken.report.some(x => x.file === 'same-copy.json' && x.problems.length >= 2));
    writeFileSync(path.join(t.dir, 'same-copy.json'), '{ not json');
    assert.throws(() => loadRecipes(t.dir), e => e.code === 'invalid-recipe-file');
    const unread = validateRecipeDirectory(t.dir);
    assert.ok(unread.report.some(x => x.file === 'same-copy.json' && x.problems.some(p => p.startsWith('unreadable:'))));
  } finally { t.cleanup(); }
  const missingDir = path.join(FIXTURE_GENERATED, 'nowhere');
  const missing = validateRecipeDirectory(missingDir);
  assert.ok(missing.invalid >= 1);
  assert.ok(missing.directoryProblems.some(p => /not found/.test(p)));
  assert.throws(() => loadRecipes(missingDir), e => e.code === 'recipes-missing');
});

function malformedRecipes() {
  const missingStatus = makeRecipe();
  delete missingStatus.operations.playback.status;
  const numericStatus = makeRecipe();
  numericStatus.operations.playback.status = 7;
  return [
    ['playback.status deleted', missingStatus],
    ['playback.status = 7', numericStatus],
    ['controls[0].tested = null', makeRecipe({ controls: [{ name: 'x', tested: null, limitations: 'n' }] })],
    ['controls[0].tested = [null]', makeRecipe({ controls: [{ name: 'x', tested: [null], limitations: 'n' }] })],
    ['evidence = null', makeRecipe({ evidence: null })],
  ];
}

test('validateRecipe is total: malformed and hostile documents yield problems, never an exception', () => {
  for (const [label, doc] of malformedRecipes()) {
    let problems;
    assert.doesNotThrow(() => { problems = validateRecipe(doc); }, label);
    assert.ok(Array.isArray(problems) && problems.length > 0, label);
  }
  const hostile = {
    schema: 'progenitor.catalog.recipe/1',
    operations: 5,
    controls: 'x',
    identities: [null],
    evidence: {},
    relationships: 3,
    requirements: null,
  };
  let problems;
  assert.doesNotThrow(() => { problems = validateRecipe(hostile); });
  assert.ok(Array.isArray(problems) && problems.length > 0);
  assert.ok(!problems.some(p => /^validator error:/.test(p)), problems.join('; '));
});

test('the generated file set and the recipe directory are disjoint by construction', () => {
  assert.deepEqual([...GENERATED_FILES].sort(), ['catalog-index.json', 'catalog-inventory.json', 'catalog-summary.json']);
  assert.ok(!GENERATED_FILES.some(f => f.startsWith('recipes')));
});
