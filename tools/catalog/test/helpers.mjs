// Shared test helpers: a minimal valid recipe builder and temp directories.
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const HERE = path.dirname(fileURLToPath(import.meta.url));
export const TOOL_DIR = path.resolve(HERE, '..');
export const REAL_RECIPES = path.join(TOOL_DIR, 'recipes');
export const FIXTURE_GENERATED = path.join(HERE, 'fixtures', 'generated-a');
export const FIXTURE_SHA = 'fixturecatalogsha256aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';

const NO_STAMPS = { contentCatalogSha256: null, gameIdentity: null, framework: null };

export function evidence(id, extra = {}) {
  return { id, kind: 'log', date: '2026-09-06', summary: `evidence ${id}`, path: null, build: { ...NO_STAMPS }, ...extra };
}

export function op(status, evidenceIds = [], note = 'fixture', sides) {
  const o = { status, evidence: evidenceIds, note };
  if (sides) o.sides = sides;
  return o;
}

/** One side's `generation:; core:; dev:; sample:` string (`null` tokens allowed). */
export function frameworkSideStamp(generation, hashes = {}) {
  const part = (v) => (v == null || v === '' ? 'null' : String(v));
  return `generation:${part(generation)}; core:${part(hashes.core)}; dev:${part(hashes.dev)}; sample:${part(hashes.sample)}`;
}

export function fixtureFrameworkSides(sample = 'sample-aaa', generation = 1) {
  const stamp = frameworkSideStamp(generation, { sample });
  return { server: stamp, client: stamp };
}

/** A minimal valid recipe; every field can be overridden. */
export function makeRecipe(overrides = {}) {
  const base = {
    schema: 'progenitor.catalog.recipe/1',
    id: 'fixture-recipe',
    revision: 1,
    title: 'Fixture recipe',
    family: { id: 'fixture-family', title: 'Fixture family' },
    kind: 'visual-sequence',
    aliases: ['fixture'],
    summary: 'A fixture.',
    captured: '2026-09-06',
    identities: [
      { ref: 'main-prefab', role: 'prefab', domain: 'prefab-guid', value: '-1', provenance: { source: 'fixture', method: 'source-inspection' } },
    ],
    assetReferences: [],
    relationships: [],
    requirements: { side: 'client', world: 'client', context: {}, initialization: ['step'], cleanup: ['dispose'] },
    controls: [],
    operations: {
      discovery: op('untested'), playback: op('untested'), appearance: op('untested'), controls: op('untested'),
      gameplayHits: op('not-applicable'), naturalCleanup: op('untested'), explicitCleanup: op('untested'), poolReuse: op('untested'),
    },
    evidence: [],
    implementation: [],
    limitations: [],
  };
  return { ...base, ...overrides };
}

/** Write recipes into a fresh temp dir; returns { dir, cleanup }. */
export function tempRecipes(recipes) {
  const root = mkdtempSync(path.join(tmpdir(), 'progenitor-recipes-'));
  const dir = path.join(root, 'recipes');
  mkdirSync(dir);
  for (const r of recipes) writeFileSync(path.join(dir, `${r.id}.json`), JSON.stringify(r, null, 2));
  return { root, dir, cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

/** Minimal live-rig report.json body for inspect fixtures. */
export function makeRunReport(overrides = {}) {
  return {
    case: 'cleanup-ok',
    startedUtc: '2026-09-07T05:09:38.000Z',
    endedUtc: '2026-09-07T05:09:41.000Z',
    images: {
      client: { side: 'client', progenitor: '0.1.0', generation: 1, sampleHash: 'sample-aaa', alcName: 'progenitor-core:1' },
      server: { side: 'server', progenitor: '0.1.0', generation: 1, sampleHash: 'sample-aaa', alcName: 'progenitor-core:1' },
    },
    startState: {},
    steps: [{ name: 'spawn rat', ok: true, ms: 10 }],
    records: {},
    cleanup: { ok: true, remaining: [], unconfirmed: [], entities: [] },
    finalChecks: [{ name: 'playerHealth', ok: true }],
    leftoverSummary: { trackedRemaining: 0, unconfirmed: 0, carriersLeftover: false, adminRestored: true, protectionOk: true },
    session: { serverStartedUtc: '2026-09-07T00:00:00.000Z', sampleHash: 'sample-aaa', generation: 1 },
    status: 'complete',
    reason: 'all steps, cleanup and checks passed',
    ...overrides,
  };
}

/** Write a fake live-rig run directory (report.json plus optional trace.jsonl). */
export function tempRunDir(report, extra = {}) {
  const dir = mkdtempSync(path.join(tmpdir(), 'progenitor-rigrun-'));
  writeFileSync(path.join(dir, 'report.json'), JSON.stringify(report, null, 2));
  if (extra.trace != null) writeFileSync(path.join(dir, 'trace.jsonl'), extra.trace);
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) };
}
