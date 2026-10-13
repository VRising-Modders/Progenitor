// recipes.mjs - the shared query implementation for the maintained recipe layer.
//
// Loads ../recipes/*.json, validates them against schema progenitor.catalog.recipe/1
// (../recipes/SCHEMA.md), and answers search / inspect questions offline. The CLI
// (../recipe.mjs) is a thin wrapper; a later MCP op or in-game browser can call these
// functions directly. Nothing here loads, casts, previews or touches the game.

import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { DOMAINS, parseQuery, splitArchiveObject, validateIdentityValue } from './identity.mjs';
import { INVENTORY_NOTE, lookupByName, searchInventory } from './inventory.mjs';
import {
  DEFAULT_OPERATION_SIDES,
  applicabilityFromRun,
  buildStampsFromRun,
  frameworkSidesCompare,
  frameworkSidesRecorded,
  frameworkStampCompare,
  isValidOperationSides,
  normalizeOperationSides,
  readRigRun,
  requiredSidesHaveImageHashes,
  stampValuesMatch,
} from './rigRuns.mjs';
import { mainCheckoutRoot } from '../../common/workspace.mjs';

export { DEFAULT_OPERATION_SIDES, normalizeOperationSides } from './rigRuns.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const DEFAULT_RECIPE_DIR = path.join(HERE, '..', 'recipes');
export const DEFAULT_GENERATED_DIR = path.join(HERE, '..');
export const REPO_ROOT = path.resolve(HERE, '..', '..', '..');

export const SCHEMA = 'progenitor.catalog.recipe/1';
export const RECIPE_KINDS = Object.freeze(['gameplay-payload', 'visual-sequence', 'isolated-particle', 'visual-instance-override', 'full-ability-cast']);
export const RELATIONSHIP_STATUSES = Object.freeze(['known', 'inferred', 'unresolved']);
export const KNOWN_METHODS = Object.freeze(['live-log', 'live-readback', 'native-analyze', 'source-inspection', 'dump']);
export const INFERRED_METHODS = Object.freeze(['name-match', 'report', 'reasoning']);
export const OPERATION_KEYS = Object.freeze(['discovery', 'playback', 'appearance', 'controls', 'gameplayHits', 'naturalCleanup', 'explicitCleanup', 'poolReuse']);
export const OPERATION_STATUSES = Object.freeze(['observed-success', 'observed-partial', 'observed-failure', 'inferred', 'untested', 'not-applicable']);
export const EVIDENCE_KINDS = Object.freeze(['log', 'screenshot', 'clip', 'source-inspection', 'native-analyze', 'report', 'readback-file', 'rig-run']);
export const BUILD_STAMPS = Object.freeze(['contentCatalogSha256', 'gameIdentity', 'framework']);
export const PROVENANCE_CLASSES = Object.freeze(['media', 'log', 'readback', 'source', 'agent-reported', 'missing']);
export const PROVENANCE_RANK = Object.freeze({ media: 5, log: 4, readback: 3, source: 2, 'agent-reported': 1, missing: 0, none: -1 });
export const SIDES = Object.freeze(['server', 'client', 'both']);
export const DEFAULT_LIMIT = 20;
export const MAX_LIMIT = 200;
export const IMPLEMENTATION_STATUS_LABELS = Object.freeze([
  'source-checked',
  'compile-verified',
  'observed live',
  'acceptance procedure not run',
]);
export { INVENTORY_NOTE };

const STEM_GROUPS = Object.freeze([
  Object.freeze(['colour', 'color', 'colours', 'colors', 'recolour', 'recolor']),
  Object.freeze(['radius', 'radii']),
  Object.freeze(['damage', 'damaging', 'damaged']),
  Object.freeze(['hit', 'hits', 'gameplayhits']),
  Object.freeze(['nova']),
  Object.freeze(['frost']),
  Object.freeze(['wendigo']),
  Object.freeze(['charm']),
  Object.freeze(['beam', 'beams']),
  Object.freeze(['particle', 'particles']),
  Object.freeze(['borrow', 'borrowed', 'borrowing']),
  Object.freeze(['cleanup', 'naturalcleanup', 'explicitcleanup']),
]);

const RE_ID = /^[a-z0-9]+(-[a-z0-9]+)*$/;
const RE_REF = /^[a-z0-9]+(-[a-z0-9]+)*$/;

export class RecipeError extends Error {
  constructor(code, message, details = {}) { super(message); this.code = code; this.details = details; }
  toJSON() { return { error: { code: this.code, message: this.message, ...this.details } }; }
}

// ---------------------------------------------------------------- validation

function isPlainObject(v) { return v !== null && typeof v === 'object' && !Array.isArray(v); }
function isNonEmptyString(v) { return typeof v === 'string' && v.trim().length > 0; }

function implementationStatusOk(status) {
  const s = status.trim().toLowerCase();
  return IMPLEMENTATION_STATUS_LABELS.some(label => (
    s === label
    || s.startsWith(`${label} `)
    || s.startsWith(`${label};`)
    || s.startsWith(`${label},`)
    || s.startsWith(`${label}:`)
  ));
}

function collectRecipeProblems(r) {
  const p = [];
  if (!isPlainObject(r)) return ['recipe must be a JSON object'];
  if (r.schema !== SCHEMA) p.push(`schema must be '${SCHEMA}' (got ${JSON.stringify(r.schema)})`);
  if (!isNonEmptyString(r.id) || !RE_ID.test(r.id)) p.push('id must be kebab-case');
  if (!Number.isInteger(r.revision) || r.revision < 1) p.push('revision must be a positive integer');
  if (!isNonEmptyString(r.title)) p.push('title is required');
  if (!isPlainObject(r.family) || !isNonEmptyString(r.family.id) || !RE_ID.test(r.family.id) || !isNonEmptyString(r.family.title)) p.push('family must be {id (kebab-case), title}');
  if (!RECIPE_KINDS.includes(r.kind)) p.push(`kind must be one of ${RECIPE_KINDS.join(', ')}`);
  if (!Array.isArray(r.aliases) || r.aliases.some(a => !isNonEmptyString(a))) p.push('aliases must be an array of non-empty strings');
  if (!isNonEmptyString(r.summary)) p.push('summary is required');
  if (r.verified !== undefined) p.push("an asset-wide 'verified' flag is not allowed; record per-operation status instead");

  // identities
  const refs = new Set();
  if (!Array.isArray(r.identities) || r.identities.length === 0) p.push('identities must be a non-empty array');
  else r.identities.forEach((it, i) => {
    const at = `identities[${i}]`;
    if (!isPlainObject(it)) { p.push(`${at} must be an object`); return; }
    if (!isNonEmptyString(it.ref) || !RE_REF.test(it.ref)) p.push(`${at}.ref must be kebab-case`);
    else if (refs.has(it.ref)) p.push(`${at}.ref '${it.ref}' is duplicated`); else refs.add(it.ref);
    if (!isNonEmptyString(it.role)) p.push(`${at}.role is required`);
    if (it.domain === 'entity' || it.domain === 'entity-index') p.push(`${at}: a runtime entity index:version is session evidence, not a catalog identity`);
    else if (!DOMAINS.includes(it.domain)) p.push(`${at}.domain must be one of ${DOMAINS.join(', ')}`);
    else {
      if (typeof it.value === 'number') p.push(`${at}.value must be a string (numbers lose precision or sign context)`);
      else { const why = validateIdentityValue(it.domain, it.value); if (why) p.push(`${at}.value: ${why}`); }
    }
    if (it.name !== undefined && !isNonEmptyString(it.name)) p.push(`${at}.name must be a non-empty string when present`);
    if (!isPlainObject(it.provenance) || !isNonEmptyString(it.provenance.source)) p.push(`${at}.provenance.source is required`);
  });

  // asset references (names read live or from source; never identities on their own)
  const assetNames = new Set();
  if (r.assetReferences !== undefined) {
    if (!Array.isArray(r.assetReferences)) p.push('assetReferences must be an array');
    else r.assetReferences.forEach((a, i) => {
      const at = `assetReferences[${i}]`;
      if (!isPlainObject(a) || !isNonEmptyString(a.name)) { p.push(`${at}.name is required`); return; }
      if (a.unityType !== undefined && !isNonEmptyString(a.unityType)) p.push(`${at}.unityType must be a string`);
      if (!isNonEmptyString(a.role)) p.push(`${at}.role is required`);
      if (!isPlainObject(a.provenance) || !isNonEmptyString(a.provenance.source)) p.push(`${at}.provenance.source is required`);
      assetNames.add(a.name);
    });
  }

  const nodeOk = (n) => typeof n === 'string' && (refs.has(n) || (n.startsWith('asset:') && assetNames.has(n.slice(6))) || n.startsWith('external:'));

  // relationships
  if (!Array.isArray(r.relationships)) p.push('relationships must be an array');
  else r.relationships.forEach((rel, i) => {
    const at = `relationships[${i}]`;
    if (!isPlainObject(rel)) { p.push(`${at} must be an object`); return; }
    if (!nodeOk(rel.from)) p.push(`${at}.from '${rel.from}' is not an identity ref, asset:<name> or external:<text>`);
    if (!nodeOk(rel.to)) p.push(`${at}.to '${rel.to}' is not an identity ref, asset:<name> or external:<text>`);
    if (!isNonEmptyString(rel.role)) p.push(`${at}.role is required`);
    if (!RELATIONSHIP_STATUSES.includes(rel.status)) p.push(`${at}.status must be one of ${RELATIONSHIP_STATUSES.join(', ')}`);
    if (!isPlainObject(rel.provenance) || !isNonEmptyString(rel.provenance.source) || !isNonEmptyString(rel.provenance.method)) p.push(`${at}.provenance needs source and method`);
    else {
      const m = rel.provenance.method;
      if (rel.status === 'known' && !KNOWN_METHODS.includes(m)) p.push(`${at}: status 'known' needs a method in ${KNOWN_METHODS.join(', ')} (got '${m}')`);
      if (rel.status === 'inferred' && !INFERRED_METHODS.includes(m) && !KNOWN_METHODS.includes(m)) p.push(`${at}: status 'inferred' needs a method in ${[...INFERRED_METHODS, ...KNOWN_METHODS].join(', ')} (got '${m}')`);
      const identities = Array.isArray(r.identities) ? r.identities : [];
      const touchesArchive = [rel.from, rel.to].some(n => {
        const it = identities.find(x => isPlainObject(x) && x.ref === n);
        return it && (it.domain === 'archive-object' || it.domain === 'archive');
      });
      if (touchesArchive && rel.status === 'known' && m === 'name-match') p.push(`${at}: an archive object may not be 'known' by name-match; a gameplay GUID is never silently equated to an archive object`);
    }
    if ((typeof rel.from === 'string' && rel.from.startsWith('external:')) || (typeof rel.to === 'string' && rel.to.startsWith('external:'))) {
      if (rel.status === 'known') p.push(`${at}: an external:<text> endpoint cannot be 'known'`);
    }
  });

  // requirements
  const q = r.requirements;
  if (!isPlainObject(q)) p.push('requirements must be an object');
  else {
    if (!SIDES.includes(q.side)) p.push(`requirements.side must be one of ${SIDES.join(', ')}`);
    if (!isNonEmptyString(q.world)) p.push('requirements.world is required');
    if (!isPlainObject(q.context)) p.push('requirements.context must be an object');
    if (!Array.isArray(q.initialization) || q.initialization.some(s => !isNonEmptyString(s))) p.push('requirements.initialization must be an ordered array of strings');
    if (!Array.isArray(q.cleanup) || q.cleanup.some(s => !isNonEmptyString(s))) p.push('requirements.cleanup must be an array of strings');
  }

  // evidence
  const evidenceIds = new Set();
  if (!Array.isArray(r.evidence)) p.push('evidence must be an array');
  else r.evidence.forEach((e, i) => {
    const at = `evidence[${i}]`;
    if (!isPlainObject(e)) { p.push(`${at} must be an object`); return; }
    if (!isNonEmptyString(e.id) || !/^[A-Za-z0-9-]+$/.test(e.id)) p.push(`${at}.id is required`);
    else if (evidenceIds.has(e.id)) p.push(`${at}.id '${e.id}' is duplicated`); else evidenceIds.add(e.id);
    if (!EVIDENCE_KINDS.includes(e.kind)) p.push(`${at}.kind must be one of ${EVIDENCE_KINDS.join(', ')}`);
    if (e.date !== null && !(typeof e.date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(e.date))) p.push(`${at}.date must be YYYY-MM-DD or null when unknown`);
    if (!isNonEmptyString(e.summary)) p.push(`${at}.summary is required`);
    if (e.path !== undefined && e.path !== null && !isNonEmptyString(e.path)) p.push(`${at}.path must be a string or null`);
    if (e.kind === 'rig-run' && !isNonEmptyString(e.path)) p.push(`${at}.path is required for kind rig-run (the run directory)`);
    if (e.step !== undefined && !isNonEmptyString(e.step)) p.push(`${at}.step must be a non-empty string when present`);
    if (!isPlainObject(e.build)) p.push(`${at}.build must be an object with ${BUILD_STAMPS.join(', ')} (null when not recorded)`);
    else {
      for (const k of BUILD_STAMPS) {
        if (!(k in e.build)) p.push(`${at}.build.${k} is missing (use null when the recording did not stamp it)`);
        else if (e.build[k] !== null && !isNonEmptyString(e.build[k])) p.push(`${at}.build.${k} must be a string or null`);
      }
      if (e.build.frameworkSides !== undefined) {
        const fs = e.build.frameworkSides;
        if (!isPlainObject(fs) || !('server' in fs) || !('client' in fs)) {
          p.push(`${at}.build.frameworkSides must be {server, client} (each a string or null)`);
        } else {
          for (const side of ['server', 'client']) {
            if (fs[side] !== null && !isNonEmptyString(fs[side])) p.push(`${at}.build.frameworkSides.${side} must be a string or null`);
          }
        }
      }
    }
    if (e.observer !== undefined && !isNonEmptyString(e.observer)) p.push(`${at}.observer must be a string`);
  });

  // operations
  if (!isPlainObject(r.operations)) p.push('operations must be an object');
  else {
    for (const k of OPERATION_KEYS) {
      const op = r.operations[k];
      const at = `operations.${k}`;
      if (!isPlainObject(op)) { p.push(`${at} is required`); continue; }
      if (!OPERATION_STATUSES.includes(op.status)) p.push(`${at}.status must be one of ${OPERATION_STATUSES.join(', ')}`);
      if (!isNonEmptyString(op.note)) p.push(`${at}.note is required`);
      if (!Array.isArray(op.evidence)) p.push(`${at}.evidence must be an array of evidence ids`);
      else {
        for (const id of op.evidence) if (!evidenceIds.has(id)) p.push(`${at}.evidence references unknown evidence '${id}'`);
        if (typeof op.status === 'string' && op.status.startsWith('observed') && op.evidence.length === 0) p.push(`${at}: an observed status needs at least one evidence id`);
      }
      if (op.sides !== undefined && !isValidOperationSides(op.sides)) {
        p.push(`${at}.sides must be ["client"], ["server"], or ["server","client"] (images the observation depends on)`);
      }
    }
    for (const k of Object.keys(r.operations)) if (!OPERATION_KEYS.includes(k)) p.push(`operations.${k} is not a known operation`);
  }

  // controls
  if (!Array.isArray(r.controls)) p.push('controls must be an array');
  else r.controls.forEach((c, i) => {
    const at = `controls[${i}]`;
    if (!isPlainObject(c) || !isNonEmptyString(c.name)) { p.push(`${at}.name is required`); return; }
    if (!Array.isArray(c.tested)) p.push(`${at}.tested must be an array`);
    else c.tested.forEach((t, j) => {
      const tat = `${at}.tested[${j}]`;
      if (!isPlainObject(t)) { p.push(`${tat} must be an object`); return; }
      if (t.value === undefined) p.push(`${tat}.value is required`);
      if (!Array.isArray(t.evidence) || t.evidence.length === 0) p.push(`${tat}.evidence must list evidence ids`);
      else for (const id of t.evidence) if (!evidenceIds.has(id)) p.push(`${tat} references unknown evidence '${id}'`);
    });
    if (!isNonEmptyString(c.limitations)) p.push(`${at}.limitations is required`);
    if (c.implementation !== undefined && !isNonEmptyString(c.implementation)) p.push(`${at}.implementation must be a string`);
  });

  // implementation links
  if (!Array.isArray(r.implementation)) p.push('implementation must be an array');
  else r.implementation.forEach((im, i) => {
    const at = `implementation[${i}]`;
    if (!isPlainObject(im)) { p.push(`${at} must be an object`); return; }
    if (!isNonEmptyString(im.path)) p.push(`${at}.path is required`);
    if (!isNonEmptyString(im.status)) p.push(`${at}.status is required (source-checked / compile-verified / observed live / acceptance procedure not run, plus date and image hash)`);
    else if (!implementationStatusOk(im.status)) p.push(`${at}.status must start with one of ${IMPLEMENTATION_STATUS_LABELS.join(', ')}`);
  });

  if (!Array.isArray(r.limitations) || r.limitations.some(s => !isNonEmptyString(s))) p.push('limitations must be an array of strings');
  if (!isNonEmptyString(r.captured)) p.push("captured is required ('YYYY-MM-DD' of the recipe's newest evidence, or 'unknown')");
  return p;
}

/** Validate one recipe document. Returns an array of problem strings (empty when valid). Never throws. */
export function validateRecipe(r) {
  try {
    return collectRecipeProblems(r);
  } catch (e) {
    return [`validator error: ${e && e.message != null ? e.message : e}`];
  }
}

// ---------------------------------------------------------------- loading

function duplicateIdProblem(id, files) {
  return `recipe id '${id}' appears in ${files.join(' and ')}`;
}

/**
 * Directory-wide recipe check shared by `loadRecipes` and `recipe.mjs validate`.
 * Reports per-file schema problems, unreadable JSON, duplicate ids, and a missing directory.
 * `invalid` is non-zero iff any of those problems exist.
 */
export function validateRecipeDirectory(dir) {
  return scanRecipeDirectory(dir).result;
}

function scanRecipeDirectory(dir) {
  const directoryProblems = [];
  const report = [];
  const loaded = [];
  const missingMessage = `recipe directory not found: ${dir}`;

  if (!existsSync(dir)) {
    directoryProblems.push(missingMessage);
    return {
      missing: true,
      loaded,
      result: { dir, files: 0, report, directoryProblems, invalid: directoryProblems.length },
    };
  }

  let names;
  try {
    names = readdirSync(dir).filter(f => f.endsWith('.json')).sort();
  } catch {
    directoryProblems.push(missingMessage);
    return {
      missing: true,
      loaded,
      result: { dir, files: 0, report, directoryProblems, invalid: directoryProblems.length },
    };
  }

  const filesById = new Map();
  for (const f of names) {
    const abs = path.join(dir, f);
    let doc;
    try { doc = JSON.parse(readFileSync(abs, 'utf8')); }
    catch (e) {
      report.push({ file: f, valid: false, problems: [`unreadable: ${e.message}`] });
      continue;
    }
    const problems = validateRecipe(doc);
    const valid = problems.length === 0;
    report.push({ file: f, valid, problems });
    if (isPlainObject(doc) && typeof doc.id === 'string' && doc.id.length > 0) {
      const list = filesById.get(doc.id);
      if (list) list.push(f); else filesById.set(doc.id, [f]);
    }
    if (valid) loaded.push({ file: f, doc });
  }

  for (const [id, files] of filesById) {
    if (files.length > 1) directoryProblems.push(duplicateIdProblem(id, files));
  }

  const invalid = report.filter(x => !x.valid).length + directoryProblems.length;
  return {
    missing: false,
    loaded,
    result: { dir, files: names.length, report, directoryProblems, invalid },
  };
}

function throwForDirectoryScan(dir, scan) {
  const { result } = scan;
  if (scan.missing || result.directoryProblems.some(p => p.startsWith('recipe directory not found:'))) {
    throw new RecipeError('recipes-missing', `recipe directory not found: ${dir}`, { dir });
  }
  const firstBad = result.report.find(x => !x.valid);
  if (firstBad) {
    const abs = path.join(dir, firstBad.file);
    const unread = firstBad.problems.length === 1 && typeof firstBad.problems[0] === 'string' && firstBad.problems[0].startsWith('unreadable:');
    if (unread) throw new RecipeError('invalid-recipe-file', `${firstBad.file}: ${firstBad.problems[0].slice('unreadable: '.length)}`, { file: abs });
    throw new RecipeError('invalid-recipe-file', `${firstBad.file} has ${firstBad.problems.length} problem(s)`, { file: abs, problems: firstBad.problems });
  }
  const dup = result.directoryProblems.find(p => p.startsWith("recipe id '"));
  if (dup) {
    const m = /^recipe id '([^']+)' /.exec(dup);
    throw new RecipeError('duplicate-recipe-id', dup, { id: m ? m[1] : undefined });
  }
  if (result.directoryProblems.length) {
    throw new RecipeError('invalid-recipe-file', result.directoryProblems[0], { dir });
  }
}

/**
 * Load every *.json recipe from `dir`. Throws RecipeError('invalid-recipe-file' |
 * 'duplicate-recipe-id' | 'recipes-missing') exactly when validateRecipeDirectory reports a
 * problem. Uses that same scan, so validate and query loading cannot disagree.
 */
export function loadRecipes(dir = DEFAULT_RECIPE_DIR) {
  const scan = scanRecipeDirectory(dir);
  throwForDirectoryScan(dir, scan);
  const recipes = scan.loaded.map(({ file, doc }) => ({ ...doc, _file: file }));
  recipes.sort((a, b) => a.family.id.localeCompare(b.family.id) || a.id.localeCompare(b.id));
  return { dir, recipes };
}

// ---------------------------------------------------------------- search

function normalizeLimit(limit) {
  if (limit === undefined || limit === null) return DEFAULT_LIMIT;
  if (typeof limit === 'string' && /^\d+$/.test(limit)) limit = Number(limit);
  if (!Number.isInteger(limit) || limit < 1 || limit > MAX_LIMIT) {
    throw new RecipeError('invalid-limit', `limit must be an integer between 1 and ${MAX_LIMIT}`, { limit, max: MAX_LIMIT });
  }
  return limit;
}

function identityMatches(recipe, parsed) {
  const out = [];
  for (const it of recipe.identities) {
    if (parsed.kind === 'identity') {
      if (it.domain === parsed.domain && it.value === parsed.value) out.push({ on: 'identity', ref: it.ref, domain: it.domain, value: it.value, exact: true });
    } else if (parsed.kind === 'number') {
      if ((it.domain === 'prefab-guid' || it.domain === 'sequence-guid') && it.value === parsed.value) out.push({ on: 'identity', ref: it.ref, domain: it.domain, value: it.value, exact: true });
      else if (it.domain === 'archive-object' && splitArchiveObject(it.value)?.pathID === parsed.value) out.push({ on: 'identity', ref: it.ref, domain: it.domain, value: it.value, exact: false, note: 'pathID matched without its archive; give object:<archive>/<pathID> for an exact match' });
    }
  }
  return out;
}

function needleVariants(needle) {
  const n = needle.toLowerCase();
  for (const group of STEM_GROUPS) {
    if (group.includes(n)) return group;
  }
  return [n];
}

function hayIncludes(hay, needle) {
  if (typeof hay !== 'string' || hay.length === 0) return false;
  const h = hay.toLowerCase();
  return needleVariants(needle).some(v => h.includes(v));
}

function queryWords(q) {
  return String(q).trim().toLowerCase().split(/\s+/).filter(Boolean);
}

function textMatches(recipe, needle) {
  const out = [];
  const n = needle.toLowerCase();
  if (recipe.id.toLowerCase() === n || hayIncludes(recipe.id, n)) {
    out.push({ on: 'id', value: recipe.id, score: recipe.id.toLowerCase() === n ? 95 : 70 });
  }
  if (recipe.family.id.toLowerCase() === n || hayIncludes(recipe.family.id, n)) {
    out.push({ on: 'family', value: recipe.family.id, score: recipe.family.id.toLowerCase() === n ? 90 : 70 });
  }
  for (const a of recipe.aliases) {
    const al = a.toLowerCase();
    if (al === n) out.push({ on: 'alias', value: a, score: n.includes(' ') ? 95 : 80 });
    else if (hayIncludes(al, n)) out.push({ on: 'alias', value: a, score: 60 });
  }
  if (hayIncludes(recipe.title, n)) out.push({ on: 'title', value: recipe.title, score: 60 });
  if (hayIncludes(recipe.family.title, n)) out.push({ on: 'family-title', value: recipe.family.title, score: 55 });
  for (const it of recipe.identities) {
    if (it.name && hayIncludes(it.name, n)) out.push({ on: 'identity-name', ref: it.ref, domain: it.domain, value: it.value, name: it.name, score: 50 });
    else if (it.domain === 'unity-name' && hayIncludes(it.value, n)) out.push({ on: 'identity-name', ref: it.ref, domain: it.domain, value: it.value, score: 50 });
  }
  for (const a of recipe.assetReferences ?? []) if (hayIncludes(a.name, n)) out.push({ on: 'asset-reference', value: a.name, score: 45 });
  for (const c of recipe.controls ?? []) {
    const cn = (c.name ?? '').toLowerCase();
    if (cn === n) out.push({ on: 'control', value: c.name, score: 80 });
    else if (hayIncludes(cn, n)) out.push({ on: 'control', value: c.name, score: 55 });
  }
  for (const k of OPERATION_KEYS) {
    if (k.toLowerCase() === n || hayIncludes(k, n)) out.push({ on: 'operation', value: k, score: 40 });
  }
  if (hayIncludes(recipe.kind, n)) out.push({ on: 'kind', value: recipe.kind, score: 50 });
  if (hayIncludes(recipe.summary, n)) out.push({ on: 'summary', score: 30 });
  return out;
}

function mergeMatches(groups) {
  const out = [];
  const seen = new Set();
  for (const group of groups) {
    for (const m of group) {
      const key = `${m.on}\0${m.ref ?? ''}\0${m.domain ?? ''}\0${m.value ?? ''}\0${m.name ?? ''}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push(m);
    }
  }
  return out;
}

function identityPriority(recipe, matched) {
  let pri = 0;
  for (const m of matched) {
    if (m.on !== 'identity') continue;
    const it = recipe.identities.find(i => i.ref === m.ref);
    const role = (it?.role ?? '').toLowerCase();
    if (role.includes('payload')) pri = Math.max(pri, 4);
    else if (role.includes('counterpart')) pri = Math.max(pri, 0);
    else if (it && recipe.identities[0]?.ref === it.ref) pri = Math.max(pri, 3);
    else pri = Math.max(pri, 1);
    if (m.domain === 'prefab-guid' && recipe.kind === 'gameplay-payload') pri = Math.max(pri, 3);
    if (m.domain === 'sequence-guid' && recipe.kind === 'visual-sequence') pri = Math.max(pri, 3);
  }
  return pri;
}

function compareSearchHits(a, b) {
  return (b.wordsMatched ?? 0) - (a.wordsMatched ?? 0)
    || b.score - a.score
    || (b.identityPriority ?? 0) - (a.identityPriority ?? 0)
    || a.family.id.localeCompare(b.family.id)
    || a.recipeId.localeCompare(b.recipeId);
}

function spreadByFamily(results) {
  const first = [];
  const later = [];
  const seen = new Set();
  for (const r of results) {
    if (!seen.has(r.family.id)) {
      seen.add(r.family.id);
      first.push(r);
    } else {
      later.push(r);
    }
  }
  return first.concat(later);
}

function searchableHaystack(recipe) {
  const bits = [
    recipe.id,
    recipe.family?.id,
    recipe.family?.title,
    recipe.kind,
    recipe.title,
    recipe.summary,
    ...(recipe.aliases ?? []),
    ...(recipe.identities ?? []).flatMap(it => [it.name, it.domain === 'unity-name' ? it.value : null]),
    ...(recipe.assetReferences ?? []).map(a => a.name),
    ...(recipe.controls ?? []).map(c => c.name),
    ...OPERATION_KEYS,
  ];
  return bits.filter(Boolean).join('\n').toLowerCase();
}

function suggestByOverlap(store, words, limit = 5) {
  if (!words.length) return [];
  const scored = [];
  for (const r of store.recipes) {
    const hay = searchableHaystack(r);
    const overlap = words.filter(w => hayIncludes(hay, w)).length;
    if (overlap > 0) scored.push({ recipeId: r.id, overlap });
  }
  scored.sort((a, b) => b.overlap - a.overlap || a.recipeId.localeCompare(b.recipeId));
  return scored.slice(0, limit).map(x => x.recipeId);
}

function inventoryBlock(inv, extra = {}) {
  const note = INVENTORY_NOTE;
  if (!inv) return { state: 'not-loaded', note, total: extra.total ?? 0, truncated: extra.truncated ?? false, rows: extra.rows ?? [], ...extra };
  if (!inv.available) {
    return {
      state: 'inventory-unavailable',
      note,
      generation: inv.generation ?? null,
      reason: inv.reason,
      total: extra.total ?? 0,
      truncated: extra.truncated ?? false,
      rows: extra.rows ?? [],
      ...extra,
    };
  }
  return { state: 'indexed', note, ...extra };
}

/**
 * Search the loaded recipes. `opts`: { limit, inventory (a loadInventory result), includeInventory }.
 * Multi-word text queries match when every word (or its stem) hits searchable text. Results are
 * ordered by words matched, then score, then identity priority, family id, recipe id, with one
 * best hit per family first for text queries, and bounded by limit.
 */
export function searchRecipes(store, query, opts = {}) {
  const limit = normalizeLimit(opts.limit);
  const parsed = parseQuery(query);
  if (parsed.kind === 'invalid') throw new RecipeError(parsed.code, parsed.message, { query });
  let results = [];
  const words = parsed.kind === 'text' ? queryWords(parsed.value) : [];
  for (const r of store.recipes) {
    let matched = [];
    let score = 0;
    let wordsMatched = 0;
    let priority = 0;
    if (parsed.kind === 'text') {
      const perWord = words.map(w => textMatches(r, w));
      wordsMatched = perWord.filter(m => m.length > 0).length;
      const phrase = textMatches(r, parsed.value);
      matched = mergeMatches([phrase, ...perWord]);
      score = matched.reduce((m, x) => Math.max(m, x.score ?? 0), 0);
      if (words.length > 1) {
        if (wordsMatched < words.length) continue;
      } else if (matched.length === 0) {
        continue;
      }
    } else {
      matched = identityMatches(r, parsed);
      score = matched.some(m => m.exact) ? 100 : matched.length ? 70 : 0;
      wordsMatched = matched.length ? 1 : 0;
      priority = identityPriority(r, matched);
      if (matched.length === 0) continue;
    }
    results.push({
      recipeId: r.id, revision: r.revision, family: r.family, kind: r.kind, title: r.title, score,
      wordsMatched, identityPriority: priority,
      matched: matched.map(({ score: _s, ...m }) => m),
    });
  }
  results.sort(compareSearchHits);
  if (parsed.kind === 'text') results = spreadByFamily(results);
  const out = {
    query, parsed, limit, total: results.length, truncated: results.length > limit,
    results: results.slice(0, limit),
    families: [...new Set(results.map(r => r.family.id))].sort(),
    scope: 'offline recipe search; a hit says a recipe exists, not that its asset is loaded, loadable or safe to play',
  };
  if (results.length === 0) {
    out.suggestions = parsed.kind === 'text' ? suggestByOverlap(store, words) : suggestByOverlap(store, queryWords(String(query ?? '')));
  }
  if (opts.includeInventory) {
    const inv = opts.inventory;
    if (!inv) out.inventory = inventoryBlock(null, { total: 0, truncated: false, rows: [] });
    else if (parsed.kind === 'identity' && parsed.domain === 'archive-object') {
      const { archiveId, pathID } = splitArchiveObject(parsed.value);
      const row = inv.available ? inv.rows.find(x => x.archiveId === archiveId && x.pathID === pathID) : null;
      out.inventory = inv.available
        ? inventoryBlock(inv, {
          total: row ? 1 : 0,
          truncated: false,
          rows: row ? [row] : [],
          detail: row ? undefined : 'no named row for that object; the inventory names only objects whose body fell inside the decompression budget',
        })
        : inventoryBlock(inv, { total: 0, truncated: false, rows: [] });
    } else if (parsed.kind === 'text') {
      out.inventory = searchInventory(inv, parsed.value, limit);
    } else {
      out.inventory = inventoryBlock(inv, {
        total: 0,
        truncated: false,
        rows: [],
        detail: 'GUID hashes are not archive identities; the inventory is searched by name or object:<archive>/<pathID>',
      });
    }
    if (out.inventory.rows?.length) out.inventory.scope = 'indexed names only: presence in an archive says nothing about whether the object is loaded, loadable through a mount, or usable';
  }
  return out;
}

// ---------------------------------------------------------------- inspect

function sideStampState(e, c) {
  if (e == null) return { evidence: null, current: c ?? null, state: 'unknown' };
  if (c == null) return { evidence: e, current: null, state: 'not-checkable-offline' };
  const rel = frameworkStampCompare(e, c);
  const state = rel === 'match' ? 'matches' : rel === 'mismatch' ? 'changed' : 'unknown';
  return { evidence: e, current: c, state };
}

function stampComparison(evidenceBuild, current, opts = {}) {
  const kind = opts.kind;
  const requiredSides = normalizeOperationSides(opts.sides);
  const stamps = {};
  for (const k of BUILD_STAMPS) {
    const e = evidenceBuild?.[k];
    const c = current?.[k] ?? null;
    if (k === 'framework' && kind === 'rig-run') {
      stamps.framework = {
        evidence: e ?? null,
        current: c,
        state: 'legacy-combined',
        note: 'legacy-combined (server-first, possibly hybrid); never matches a current build. Compare frameworkSides.',
      };
      continue;
    }
    if (e === null || e === undefined) stamps[k] = { evidence: null, current: c, state: 'unknown' };
    else if (c === null) stamps[k] = { evidence: e, current: null, state: 'not-checkable-offline' };
    else if (k === 'framework') {
      const rel = frameworkStampCompare(e, c);
      const state = rel === 'match' ? 'matches' : rel === 'mismatch' ? 'changed' : 'unknown';
      stamps[k] = { evidence: e, current: c, state };
    } else stamps[k] = { evidence: e, current: c, state: stampValuesMatch(k, e, c) ? 'matches' : 'changed' };
  }

  const recSides = isPlainObject(evidenceBuild?.frameworkSides)
    ? evidenceBuild.frameworkSides
    : { server: null, client: null };
  const curSides = isPlainObject(current?.frameworkSides)
    ? current.frameworkSides
    : { server: null, client: null };
  stamps.frameworkSides = {
    server: sideStampState(recSides.server ?? null, curSides.server ?? null),
    client: sideStampState(recSides.client ?? null, curSides.client ?? null),
  };

  const states = [];
  for (const k of ['contentCatalogSha256', 'gameIdentity']) states.push(stamps[k].state);
  if (kind === 'rig-run') {
    const rel = frameworkSidesCompare(recSides, curSides, requiredSides);
    if (rel === 'mismatch') states.push('changed');
    else if (rel === 'match') states.push('matches');
    else states.push('unknown');
  } else {
    states.push(stamps.framework.state);
  }

  let applicability;
  if (states.includes('changed')) applicability = 'stale';
  else if (states.includes('unknown') || states.includes('not-checkable-offline')) applicability = 'unknown-stamps';
  else applicability = 'matches-checkable-stamps';
  return { stamps, applicability };
}

// Prefix for evidence kept outside both repositories on the machine that recorded it. It resolves
// against PROGENITOR_LOCAL_EVIDENCE when set and otherwise reports the file as missing.
export const LOCAL_EVIDENCE_PREFIX = '<local-evidence>/';

/**
 * Where a recipe path points on this machine, or null when it cannot be placed.
 * `<local-evidence>/...` resolves under PROGENITOR_LOCAL_EVIDENCE. A path whose first segment
 * is `..` (`../evidence/...`) resolves against the MAIN checkout, so a linked worktree
 * finds a sibling folder beside the main checkout rather than beside itself. Any other
 * relative path resolves against this checkout.
 */
export function resolveRecipePath(p) {
  if (typeof p !== 'string' || p.length === 0) return null;
  const slashed = p.replace(/\\/g, '/');
  if (slashed.startsWith(LOCAL_EVIDENCE_PREFIX)) {
    const base = process.env.PROGENITOR_LOCAL_EVIDENCE;
    return base ? path.resolve(base, slashed.slice(LOCAL_EVIDENCE_PREFIX.length)) : null;
  }
  if (path.isAbsolute(p)) return p;
  if (slashed === '..' || slashed.startsWith('../')) return path.resolve(mainCheckoutRoot() ?? REPO_ROOT, p);
  return path.resolve(REPO_ROOT, p);
}

function pathPresence(p) {
  if (!p) return { path: null, present: null };
  const abs = resolveRecipePath(p);
  if (abs === null) return { path: p, resolved: null, present: false };
  try { const st = statSync(abs); return { path: p, resolved: abs, present: true, bytes: st.size }; }
  catch { return { path: p, resolved: abs, present: false }; }
}

function combineApplicability(recipeApp, runApp) {
  if (runApp == null) return recipeApp;
  if (recipeApp === 'stale' || runApp === 'stale') return 'stale';
  if (recipeApp === 'matches-checkable-stamps' && runApp === 'matches-checkable-stamps') return 'matches-checkable-stamps';
  return 'unknown-stamps';
}

function pathBasename(p) {
  if (typeof p !== 'string') return '';
  return p.replace(/\\/g, '/').split('/').pop().toLowerCase();
}

function isMarkdownPath(p) {
  if (typeof p !== 'string') return false;
  return p.replace(/\\/g, '/').toLowerCase().endsWith('.md');
}

function isAgentReportedEvidence(e) {
  if (e.kind === 'report') return true;
  if (isMarkdownPath(e.path)) return true;
  const summary = typeof e.summary === 'string' ? e.summary : '';
  if (/agent[- ]reported/i.test(summary) || summary.startsWith('Agent-reported')) return true;
  return pathBasename(e.path) === 'bridge-responses.md';
}

function runCitedButNotComplete(e) {
  return e.kind === 'rig-run' && e.run?.found === true && e.run.status !== 'complete';
}

function runIsOtherBuild(e, currentBuild, requiredSides = DEFAULT_OPERATION_SIDES) {
  if (e.kind !== 'rig-run' || e.run?.found !== true) return false;
  const sides = normalizeOperationSides(requiredSides);
  const recorded = buildStampsFromRun(e.run);
  for (const k of ['contentCatalogSha256', 'gameIdentity']) {
    const r = recorded[k];
    if (r == null) continue;
    const current = currentBuild?.[k] ?? null;
    const recipeStamp = e.build?.[k] ?? null;
    if (current != null && !stampValuesMatch(k, r, current)) return true;
    if (recipeStamp != null && !stampValuesMatch(k, r, recipeStamp)) return true;
  }

  const recSides = recorded.frameworkSides;
  const recipeSides = isPlainObject(e.build?.frameworkSides) ? e.build.frameworkSides : null;
  const currentSides = isPlainObject(currentBuild?.frameworkSides) ? currentBuild.frameworkSides : null;
  const recordedOnRun = frameworkSidesRecorded(recSides);
  if (currentSides) {
    const against = recordedOnRun ? recSides : recipeSides;
    if (against && frameworkSidesRecorded(against)) {
      const rel = frameworkSidesCompare(against, currentSides, sides);
      if (rel === 'mismatch') return true;
      if (rel === 'unknown' && requiredSidesHaveImageHashes(currentSides, sides)) return true;
    } else if (requiredSidesHaveImageHashes(currentSides, sides)) {
      return true;
    }
  }
  if (recordedOnRun && recipeSides && frameworkSidesRecorded(recipeSides)) {
    if (frameworkSidesCompare(recSides, recipeSides, sides) === 'mismatch') return true;
  }
  return applicabilityFromRun(e.run, currentBuild, sides) === 'stale';
}

function mediaPresentOf(refs) {
  const media = refs.filter(e => e && (e.kind === 'screenshot' || e.kind === 'clip'));
  if (media.length === 0) return null;
  if (media.some(e => e.media?.present === false)) return false;
  if (media.some(e => e.media?.present === true)) return true;
  return null;
}

/** Classify one inspect-time evidence entry. Presence is required for file-based kinds. */
export function classifyProvenance(e) {
  const pathGiven = typeof e.path === 'string' && e.path.length > 0;
  const present = e.media?.present === true;
  if (e.kind === 'rig-run') {
    if (e.run?.found === true) return 'readback';
    return 'missing';
  }
  if (pathGiven && !present) return 'missing';
  if (e.kind === 'report' || isMarkdownPath(e.path)) return 'agent-reported';
  if ((e.kind === 'screenshot' || e.kind === 'clip') && present) return 'media';
  if (e.kind === 'log' && present) return 'log';
  if (e.kind === 'readback-file' && present) return 'readback';
  if (e.kind === 'source-inspection' || e.kind === 'native-analyze') return 'source';
  if (e.kind === 'log' || e.kind === 'screenshot' || e.kind === 'clip' || e.kind === 'readback-file') return 'missing';
  return pathGiven && !present ? 'missing' : 'source';
}

function strongestOf(classes) {
  let best = 'none';
  let rank = PROVENANCE_RANK.none;
  for (const c of classes) {
    const r = PROVENANCE_RANK[c] ?? -1;
    if (r > rank) { rank = r; best = c; }
  }
  return best;
}

function operationSupport(op, refs, currentBuild) {
  const sides = normalizeOperationSides(op.sides);
  const missing = [];
  const runsNotComplete = [];
  const runsComplete = [];
  const runsOtherBuild = [];
  const classSet = new Set();
  for (const e of refs) {
    if (!e) continue;
    if (e.provenanceClass === 'missing') missing.push(e.id);
    if (runCitedButNotComplete(e)) runsNotComplete.push(e.id);
    const otherBuild = e.kind === 'rig-run' && e.run?.found === true && e.run.status === 'complete' && runIsOtherBuild(e, currentBuild, sides);
    if (otherBuild) runsOtherBuild.push(e.id);
    else if (e.kind === 'rig-run' && e.run?.found === true && e.run.status === 'complete') runsComplete.push(e.id);
    if (e.kind === 'rig-run' && e.run?.found === true && e.run.status !== 'complete') continue;
    if (otherBuild) continue;
    if (e.provenanceClass) classSet.add(e.provenanceClass);
  }
  const classes = PROVENANCE_CLASSES.filter(c => classSet.has(c));
  const strongest = (refs.length === 0 || op.status === 'untested' || op.status === 'not-applicable')
    ? 'none'
    : strongestOf(classes);
  const agentReportedOnly = refs.length > 0
    && refs.every(e => e.provenanceClass === 'agent-reported' || e.provenanceClass === 'missing');
  return { classes, strongest, missing, agentReportedOnly, runsNotComplete, runsComplete, runsOtherBuild };
}

function recipeBuildSummary(evidence) {
  let stamped = 0, unstamped = 0, stale = 0, matchesCatalog = 0;
  for (const e of evidence) {
    const hasStamp = BUILD_STAMPS.some(k => e.build?.[k] != null);
    if (hasStamp) stamped++; else unstamped++;
    if (e.applicability === 'stale') stale++;
    if (e.stamps?.contentCatalogSha256?.state === 'matches') matchesCatalog++;
  }
  return { stamped, unstamped, stale, matchesCatalog };
}

function associate(recipe, inv) {
  const out = [];
  for (const a of recipe.assetReferences ?? []) {
    if (!inv || !inv.available) {
      out.push({ name: a.name, unityType: a.unityType ?? null, role: a.role, status: 'inventory-unavailable', method: 'name-match', candidates: [], note: inv ? inv.reason : 'inventory not loaded' });
      continue;
    }
    const rows = lookupByName(inv, a.name, a.unityType);
    if (rows.length === 1) out.push({ name: a.name, unityType: a.unityType ?? null, role: a.role, status: 'inferred', method: 'name-match', candidates: rows, note: 'unique name+type match in the generated inventory; a name equality is a candidate association, not a verified dependency' });
    else if (rows.length > 1) out.push({ name: a.name, unityType: a.unityType ?? null, role: a.role, status: 'ambiguous', method: 'name-match', candidates: rows, note: `${rows.length} distinct objects share this name; none is selected` });
    else out.push({ name: a.name, unityType: a.unityType ?? null, role: a.role, status: 'unresolved', method: 'name-match', candidates: [], note: 'no named inventory row; the name may live in an object body outside the decompression budget (README known gap 2) or under another m_Name' });
  }
  return out;
}

/**
 * Inspect one recipe. `opts`: { inventory (loadInventory result), currentBuild:
 * {contentCatalogSha256, gameIdentity, framework, frameworkSides:{server,client}} }.
 * `currentBuild` defaults to the inventory's catalog sha256 with live-only stamps null.
 * Search/inspect never contact the game. Offline `claimable` stays false.
 */
export function inspectRecipe(store, id, opts = {}) {
  if (typeof id !== 'string' || id.trim().length === 0) throw new RecipeError('invalid-recipe-id', 'recipe id is required');
  const recipe = store.recipes.find(r => r.id === id);
  if (!recipe) {
    const n = id.toLowerCase();
    const suggestions = store.recipes.filter(r => r.id.includes(n) || r.aliases.some(a => a.toLowerCase().includes(n))).map(r => r.id).slice(0, 10);
    throw new RecipeError('unknown-recipe', `no recipe with id '${id}'`, { id, suggestions, known: store.recipes.map(r => r.id) });
  }
  const inv = opts.inventory ?? null;
  const incoming = opts.currentBuild;
  const currentBuild = incoming
    ? {
      contentCatalogSha256: incoming.contentCatalogSha256 ?? null,
      gameIdentity: incoming.gameIdentity ?? null,
      framework: incoming.framework ?? null,
      frameworkSides: isPlainObject(incoming.frameworkSides)
        ? { server: incoming.frameworkSides.server ?? null, client: incoming.frameworkSides.client ?? null }
        : { server: null, client: null },
    }
    : {
      contentCatalogSha256: inv?.available ? inv.source.catalogSha256 : null,
      gameIdentity: null,
      framework: null,
      frameworkSides: { server: null, client: null },
    };
  const evidence = recipe.evidence.map(e => {
    const media = pathPresence(e.path);
    const compared = stampComparison(e.build, currentBuild, { kind: e.kind, sides: DEFAULT_OPERATION_SIDES });
    const entry = { ...e, media, stamps: compared.stamps, applicability: compared.applicability };
    if (e.kind === 'rig-run') {
      const runDir = resolveRecipePath(e.path);
      const run = runDir === null
        ? { found: false, reason: 'run directory not resolvable on this machine' }
        : readRigRun(runDir, { step: e.step, repoRoot: REPO_ROOT });
      entry.run = run;
      entry.applicability = combineApplicability(
        compared.applicability,
        applicabilityFromRun(run, currentBuild, DEFAULT_OPERATION_SIDES),
      );
    }
    entry.provenanceClass = classifyProvenance(entry);
    return entry;
  });
  const evidenceById = new Map(evidence.map(e => [e.id, e]));
  const applicabilityOfRef = (e, sides) => {
    if (!e) return 'unknown-stamps';
    const compared = stampComparison(e.build, currentBuild, { kind: e.kind, sides });
    if (e.kind === 'rig-run') {
      return combineApplicability(compared.applicability, applicabilityFromRun(e.run, currentBuild, sides));
    }
    return compared.applicability;
  };
  const summarizeOp = (op) => {
    const sides = normalizeOperationSides(op.sides);
    const refs = op.evidence.map(id => evidenceById.get(id));
    const apps = refs.map(e => applicabilityOfRef(e, sides));
    const applicability = refs.length === 0 ? 'no-evidence'
      : apps.some(a => a === 'stale') ? 'stale'
      : apps.every(a => a === 'matches-checkable-stamps') ? 'matches-checkable-stamps'
      : 'unknown-stamps';
    return {
      ...op,
      sides,
      applicability,
      mediaPresent: mediaPresentOf(refs),
      support: operationSupport({ ...op, sides }, refs, currentBuild),
    };
  };
  const operations = {};
  for (const k of OPERATION_KEYS) operations[k] = summarizeOp(recipe.operations[k]);
  const { _file, ...doc } = recipe;
  return {
    recipeId: recipe.id, revision: recipe.revision, file: _file,
    title: recipe.title, family: recipe.family, kind: recipe.kind, aliases: recipe.aliases, summary: recipe.summary, captured: recipe.captured,
    identities: recipe.identities,
    relationships: recipe.relationships,
    requirements: recipe.requirements,
    controls: recipe.controls.map(c => ({ ...c, tested: c.tested.map(t => ({ ...t, applicability: summarizeOp({ evidence: t.evidence }).applicability })) })),
    operations,
    evidence,
    buildSummary: recipeBuildSummary(evidence),
    implementation: recipe.implementation.map(im => ({ ...im, file: pathPresence(im.path) })),
    limitations: recipe.limitations,
    inventory: inv
      ? (inv.available
        ? {
          state: 'indexed',
          note: INVENTORY_NOTE,
          generation: inv.generation,
          summaryMissing: inv.summaryMissing === true,
          dir: inv.dir,
          generated: inv.generated,
          source: inv.source,
          counts: inv.counts,
          warnings: inv.warnings ?? [],
        }
        : {
          state: 'inventory-unavailable',
          note: INVENTORY_NOTE,
          generation: inv.generation ?? null,
          dir: inv.dir,
          reason: inv.reason,
        })
      : { state: 'not-loaded', note: INVENTORY_NOTE },
    inventoryAssociations: associate(recipe, inv),
    currentBuild,
    currentVerification: {
      claimable: false,
      reason: 'offline inspection sees neither the running game nor the framework build; per-operation statuses above are historical observations stamped with what their recordings recorded. Only a live re-run on the current build can claim current verification.',
    },
    runtimeAvailability: {
      state: 'not-observed-offline',
      reason: 'whether the referenced objects are loaded, loadable or ready to play is a live readback (ObjectLoadingStatus / a mount in our own namespace); this tool does not perform it.',
    },
    schema: SCHEMA, doc,
  };
}

/** Plain list for `recipe.mjs list`. */
export function listRecipes(store) {
  return store.recipes.map(r => ({ recipeId: r.id, revision: r.revision, family: r.family.id, kind: r.kind, title: r.title, captured: r.captured, file: r._file }));
}
