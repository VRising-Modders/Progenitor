// Agent-facing catalog workflows: search then inspect through the MCP wrappers
// (same JSON as searchRecipes / inspectRecipe / listRecipes). A hit is not
// availability; inspect reports the supported operation and its limits.
import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  progenitor_catalog_inspect as inspect,
  progenitor_catalog_list as list,
  progenitor_catalog_search as search,
} from '../../mcp/catalog-tools.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const README = readFileSync(path.join(HERE, '..', 'README.md'), 'utf8');

const APPEARANCE = 'wendigo-frost-nova-appearance';
const RING = 'wendigo-frost-nova-ring';
const CASTER = 'charm-caster-link-sequence';
const TARGET = 'charm-target-channel-sequence';
const BORROW = 'charm-loop-particle-borrow';

const QUALIFIED_VERIFIED = /unverified|not verified|not yet a verified|no verified|not a verified|verified live|compile-verified|live-verified|verified.{0,20}at rest|verified for THIS|verified only|verified with|NOT been verified/i;

function recipeIds(result) {
  return result.results.map((r) => r.recipeId);
}

function verifiedHits(json) {
  const hits = [];
  const re = /\bverified\b/gi;
  let m;
  while ((m = re.exec(json))) {
    const from = Math.max(0, m.index - 140);
    const to = Math.min(json.length, m.index + m[0].length + 140);
    hits.push({ index: m.index, word: m[0], window: json.slice(from, to) });
  }
  return hits;
}

function listedRecipes() {
  const listed = list({});
  return listed.recipes ?? listed;
}

function inspectAgentReportedOnly() {
  const ids = [];
  for (const row of listedRecipes()) {
    const r = inspect({ recipe: row.recipeId });
    for (const [key, op] of Object.entries(r.operations)) {
      if (op.support?.agentReportedOnly === true) ids.push(`${r.recipeId}.${key}`);
    }
  }
  return ids.sort();
}

function readmeAgentReportedSection(readme) {
  const start = readme.indexOf('### Agent-reported-only operations');
  assert.ok(start >= 0, 'README must have ### Agent-reported-only operations');
  const rest = readme.slice(start);
  const next = rest.search(/\n### /);
  return next >= 0 ? rest.slice(0, next) : rest;
}

function readmeAgentReportedOnly(readme) {
  return [...readmeAgentReportedSection(readme).matchAll(/`([a-z0-9-]+\.[A-Za-z]+)`/g)].map((m) => m[1]).sort();
}

// Recorded rig runs are local evidence outside the repository. The workflows that read them run
// only where PROGENITOR_LOCAL_EVIDENCE names that folder; elsewhere they are skipped, not failed.
const LOCAL_EVIDENCE = process.env.PROGENITOR_LOCAL_EVIDENCE;
const NEEDS_EVIDENCE = LOCAL_EVIDENCE && existsSync(LOCAL_EVIDENCE)
  ? {}
  : { skip: 'needs PROGENITOR_LOCAL_EVIDENCE with the recorded rig runs' };

test('1. green nova: appearance first, ring payload, inspect exposes supported Sample usage', () => {
  const found = search({ query: 'green nova' });
  assert.match(found.scope, /a hit says a recipe exists, not that its asset is loaded/);
  assert.equal(found.results[0].recipeId, APPEARANCE);
  assert.equal(found.results[0].kind, 'visual-instance-override');
  const ringHit = found.results.find((r) => r.recipeId === RING);
  assert.ok(ringHit, 'the ring recipe must appear as the payload the appearance restyles');
  assert.equal(ringHit.kind, 'gameplay-payload');

  const look = inspect({ recipe: APPEARANCE });
  assert.equal(look.currentVerification.claimable, false);
  assert.equal(look.runtimeAvailability.state, 'not-observed-offline');
  assert.equal(look.kind, 'visual-instance-override');

  assert.equal(look.controls.length, 2);
  const radius = look.controls.find((c) => c.name === 'radius');
  const colour = look.controls.find((c) => c.name === 'crystal colour');
  assert.ok(radius, 'radius control');
  assert.ok(colour, 'crystal colour control');
  assert.ok(radius.tested.length > 0 && radius.tested[0].value === 18);
  assert.ok(colour.tested.length > 0 && colour.tested[0].value != null);
  assert.match(colour.limitations, /Sample-specific/);

  assert.ok(Array.isArray(look.requirements.initialization) && look.requirements.initialization.length > 0);
  assert.ok(look.requirements.initialization.some((s) => s.includes(RING) || /after spawning the ring/i.test(s)));

  const impl = look.implementation.map((i) => `${i.path} ${i.symbol ?? ''}`).join('\n');
  assert.match(impl, /SampleFrostNovaAppearance/);
  assert.match(impl, /SampleFrostDash/);

  for (const [key, op] of Object.entries(look.operations)) {
    assert.ok(op.support, `${key} support`);
    assert.ok('strongest' in op.support, `${key} support.strongest`);
    assert.ok(Array.isArray(op.support.classes), `${key} support.classes (provenance)`);
    assert.ok(typeof op.status === 'string', `${key} status`);
  }

  assert.ok(look.limitations.some((l) => /Sample-specific recolour/i.test(l)));

  const json = JSON.stringify(look);
  const hits = verifiedHits(json);
  for (const h of hits) {
    assert.ok(
      QUALIFIED_VERIFIED.test(h.window),
      `bare verified in ${APPEARANCE} inspect JSON at ${h.index}: ${h.window}`,
    );
  }
  assert.ok(hits.length > 0, `qualified verified occurrences: ${JSON.stringify(hits.map((h) => h.window))}`);
});

test('2. radius / colour / color find the appearance recipe', () => {
  for (const q of ['radius', 'colour', 'color']) {
    const found = search({ query: q });
    assert.ok(found.total > 0, q);
    assert.equal(found.results[0].recipeId, APPEARANCE, q);
    assert.match(found.scope, /not that its asset is loaded/);
  }
});

test('3. charm beam vs particles: target first (partial), caster draws nothing, borrow is isolated', () => {
  const found = search({ query: 'charm beam' });
  assert.equal(found.results[0].recipeId, TARGET);
  assert.equal(found.results[0].kind, 'visual-sequence');

  const target = inspect({ recipe: found.results[0].recipeId });
  assert.equal(target.operations.appearance.status, 'observed-partial');

  const casterHit = found.results.find((r) => r.recipeId === CASTER);
  assert.ok(casterHit, 'caster observed-failure must be visible on the same search');
  const caster = inspect({ recipe: casterHit.recipeId });
  assert.equal(caster.operations.appearance.status, 'observed-failure');
  assert.match(caster.summary, /nothing drawn/i);
  assert.match(caster.summary, /no beam/i);
  assert.match(caster.title, /nothing drawn/i);

  const borrow = inspect({ recipe: BORROW });
  assert.equal(borrow.kind, 'isolated-particle');
  assert.match(borrow.summary, /isolated particle/i);
  assert.match(borrow.summary, /not the Charm spell/i);
  assert.match(borrow.summary, /not a beam/i);

  assert.match(target.summary, /no beam/i);
  assert.match(caster.summary, /no beam|nothing drawn/i);
  assert.match(borrow.summary, /not a beam/i);
});

test('4. ambiguous names: nova (2), charm (3), frost excludes Charm, Part_Charm_Loop_01 match reasons', () => {
  const nova = search({ query: 'nova' });
  assert.equal(nova.total, 2);
  assert.deepEqual(nova.families, ['wendigo-frost-nova']);
  assert.deepEqual(new Set(recipeIds(nova)), new Set([RING, APPEARANCE]));
  assert.notEqual(nova.results[0].kind, nova.results[1].kind);
  assert.deepEqual(new Set(nova.results.map((r) => r.kind)), new Set(['gameplay-payload', 'visual-instance-override']));

  const charm = search({ query: 'charm' });
  assert.equal(charm.total, 3);
  assert.deepEqual(charm.families, ['charm-channeling']);
  assert.deepEqual(new Set(recipeIds(charm)), new Set([CASTER, TARGET, BORROW]));
  const charmKinds = new Set(charm.results.map((r) => r.kind));
  assert.ok(charmKinds.has('visual-sequence'));
  assert.ok(charmKinds.has('isolated-particle'));
  assert.equal(charm.results.filter((r) => r.kind === 'visual-sequence').length, 2);
  assert.equal(new Set(charm.results.map((r) => `${r.recipeId}:${r.kind}`)).size, 3);

  const frost = search({ query: 'frost' });
  assert.ok(frost.total > 0);
  assert.ok(!frost.results.some((r) => r.family.id === 'charm-channeling'));
  assert.ok(!recipeIds(frost).some((id) => id.startsWith('charm-')));

  const part = search({ query: 'Part_Charm_Loop_01' });
  const borrowHit = part.results.find((r) => r.recipeId === BORROW);
  const targetHit = part.results.find((r) => r.recipeId === TARGET);
  assert.ok(borrowHit, 'borrow recipe by identity-name');
  assert.ok(targetHit, 'target recipe by asset reference');
  assert.ok(
    borrowHit.matched.some((m) => m.on === 'identity-name' && (m.value === 'Part_Charm_Loop_01' || m.name === 'Part_Charm_Loop_01')),
    `borrow match reasons: ${JSON.stringify(borrowHit.matched)}`,
  );
  assert.ok(
    targetHit.matched.some((m) => m.on === 'asset-reference' && m.value === 'Part_Charm_Loop_01'),
    `target match reasons: ${JSON.stringify(targetHit.matched)}`,
  );
});

test('5. unsupported requests return zero recipes; README refuses load/preview and lists cannot-do', () => {
  for (const q of ['fireball', 'styx', 'possession', 'load asset', 'preview']) {
    const found = search({ query: q });
    assert.equal(found.total, 0, q);
    assert.deepEqual(found.results, [], q);
    assert.match(found.scope, /a hit says a recipe exists, not that its asset is loaded, loadable or safe to play/);
  }

  assert.match(README, /What the catalog cannot do/);
  assert.match(README, /no loading/i);
  assert.match(README, /no preview/i);
  assert.match(README, /no possession/i);
  assert.match(README, /no archive mounting/i);
  assert.match(README, /no runtime availability/i);
  assert.match(README, /loading or preview are not offered|do not offer loading or preview/i);
});

test('6. exact identities name the domain in the match', () => {
  const prefab = search({ query: 'prefab:-1227952025' });
  assert.equal(prefab.parsed.kind, 'identity');
  assert.equal(prefab.parsed.domain, 'prefab-guid');
  assert.deepEqual(new Set(recipeIds(prefab)), new Set([RING, APPEARANCE]));
  for (const r of prefab.results) {
    assert.ok(r.matched.some((m) => m.on === 'identity' && m.domain === 'prefab-guid' && m.value === '-1227952025'));
  }

  const seq = search({ query: 'sequence:-541113174' });
  assert.equal(seq.parsed.domain, 'sequence-guid');
  assert.deepEqual(new Set(recipeIds(seq)), new Set([TARGET, BORROW]));
  for (const r of seq.results) {
    assert.ok(r.matched.some((m) => m.on === 'identity' && m.domain === 'sequence-guid' && m.value === '-541113174'));
  }

  const bare = search({ query: '-389499105' });
  assert.equal(bare.parsed.kind, 'number');
  assert.deepEqual(recipeIds(bare), [CASTER]);
  assert.ok(bare.results[0].matched.some((m) => m.on === 'identity' && m.domain === 'sequence-guid' && m.value === '-389499105'));
});

test('7. evidence honesty: observed-* has support; agent-reported-only carries the warning; README lists them from inspect', NEEDS_EVIDENCE, () => {
  const listed = [];
  for (const row of listedRecipes()) {
    const r = inspect({ recipe: row.recipeId });
    assert.equal(r.currentVerification.claimable, false, r.recipeId);
    for (const [key, op] of Object.entries(r.operations)) {
      if (typeof op.status === 'string' && op.status.startsWith('observed-')) {
        assert.notEqual(op.support.strongest, 'none', `${r.recipeId}.${key} observed but strongest=none`);
      }
      if (op.support.agentReportedOnly === true) {
        listed.push(`${r.recipeId}.${key}`);
        assert.match(op.note, /agent-reported/i, `${r.recipeId}.${key} must carry the agent-reported warning in its note`);
        assert.ok(
          op.support.classes.includes('agent-reported'),
          `${r.recipeId}.${key} agentReportedOnly without provenance class`,
        );
      }
    }
  }
  listed.sort();
  const fromInspect = inspectAgentReportedOnly();
  const fromReadme = readmeAgentReportedOnly(README);
  assert.deepEqual(fromReadme, fromInspect);
  assert.deepEqual(fromReadme, listed);
  if (fromInspect.length === 0) {
    assert.match(
      readmeAgentReportedSection(README),
      /\bnone\b|\bempty\b|no agent-reported-only/i,
      'README must say explicitly that the agent-reported-only list is empty',
    );
  }
});

test('8. agent queries: frost nova radius, wendigo damage, particle, bare GUID, empty suggestions', () => {
  const radius = search({ query: 'frost nova radius' });
  assert.ok(radius.total > 0, 'frost nova radius must hit the radius-18 appearance recipe');
  assert.equal(radius.results[0].recipeId, APPEARANCE);
  assert.equal(inspect({ recipe: radius.results[0].recipeId }).kind, 'visual-instance-override');

  const damage = search({ query: 'wendigo damage' });
  assert.ok(damage.total > 0, 'wendigo damage must hit the ring payload');
  assert.equal(damage.results[0].recipeId, RING);
  assert.equal(inspect({ recipe: damage.results[0].recipeId }).kind, 'gameplay-payload');

  const particle = search({ query: 'particle' });
  assert.ok(particle.total > 0);
  assert.equal(particle.results[0].recipeId, BORROW);
  const particleIds = recipeIds(particle);
  assert.ok(particleIds.includes(APPEARANCE), 'appearance must match particle');
  assert.notEqual(particleIds[particleIds.length - 1], APPEARANCE, 'appearance must not be last when it matches');

  const bare = search({ query: '-541113174' });
  assert.equal(bare.parsed.kind, 'number');
  assert.equal(bare.results[0].recipeId, TARGET, 'payload/primary sequence ranks first, not the counterpart borrow');
  for (const r of bare.results) {
    assert.ok(r.matched.some((m) => m.on === 'identity' && m.domain === 'sequence-guid' && m.value === '-541113174'), JSON.stringify(r.matched));
  }

  const prefab = search({ query: '-1227952025' });
  assert.equal(prefab.results[0].recipeId, RING);
  for (const r of prefab.results) {
    assert.ok(r.matched.some((m) => m.on === 'identity' && m.domain === 'prefab-guid' && m.value === '-1227952025'));
  }

  const empty = search({ query: 'fireball' });
  assert.equal(empty.total, 0);
  assert.ok(Array.isArray(empty.suggestions));
});

test('9. inventory.state is index presence, never available; gameplayHits mediaPresent is honest; implementation labels', NEEDS_EVIDENCE, () => {
  const ring = inspect({ recipe: RING });
  assert.ok(ring.inventory.state === 'indexed' || ring.inventory.state === 'inventory-unavailable' || ring.inventory.state === 'not-loaded');
  assert.notEqual(ring.inventory.state, 'available');
  assert.equal(ring.inventory.availability, undefined);
  assert.equal(ring.inventory.note, 'index presence only; runtime availability is not observed offline');
  const hits = ring.operations.gameplayHits;
  assert.ok(hits.support, 'gameplayHits support');
  assert.equal(hits.mediaPresent, null, 'hits cite no media');
  assert.equal(hits.support.classes.includes('media'), false, 'mediaPresent null must match support without media class');
  assert.match(ring.summary, /observed live on 2026-09-07/i);
  assert.doesNotMatch(ring.summary, /^Live 2026-09-07/);
  for (const im of ring.implementation) {
    assert.match(im.status, /^(source-checked|compile-verified|observed live|acceptance procedure not run)\b/i, im.status);
    assert.doesNotMatch(im.status, /live-tested|live-verified/i);
  }

  const target = inspect({ recipe: TARGET });
  const e3 = target.evidence.find((e) => e.id === 'E3');
  assert.ok(e3);
  assert.equal(e3.provenanceClass, 'agent-reported', 'kind report / .md is never source');
  assert.match(target.summary, /ServerBeamLink/);
  assert.doesNotMatch(target.summary, /ServerBeam\.Link/);
});
