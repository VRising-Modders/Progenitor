// rigRuns.mjs — read a live-rig run directory for catalog inspect.
//
// A recipe may cite a run by path (kind "rig-run"). This module opens that
// directory's report.json and returns a compact summary. It never reads
// trace.jsonl in full: only a bounded prefix, and only when a named step is
// requested. Nothing here talks to the game or promotes a run to "verified".

import { closeSync, existsSync, fstatSync, lstatSync, openSync, readSync, readlinkSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const REPO_ROOT = path.resolve(HERE, '..', '..', '..');

export const MAX_TRACE_MATCHES = 20;
export const MAX_TRACE_SCAN_LINES = 200;
export const MAX_TRACE_BYTES = 256 * 1024;
export const MAX_REPORT_BYTES = 4 * 1024 * 1024;

function isPlainObject(v) {
  return v !== null && typeof v === 'object' && !Array.isArray(v);
}

function summarizeSession(session) {
  if (!isPlainObject(session)) return null;
  return {
    serverStartedUtc: session.serverStartedUtc ?? null,
    sampleHash: session.sampleHash ?? null,
    generation: session.generation ?? null,
  };
}

function summarizeSideImage(side, label) {
  if (!isPlainObject(side)) return null;
  return {
    side: side.side ?? label,
    progenitor: side.progenitor ?? null,
    generation: side.generation ?? null,
    sampleHash: side.sampleHash ?? null,
    alcName: side.alcName ?? null,
  };
}

function summarizeImages(images) {
  if (!isPlainObject(images)) return null;
  return {
    client: summarizeSideImage(images.client, 'client'),
    server: summarizeSideImage(images.server, 'server'),
  };
}

function summarizeSteps(steps) {
  if (!Array.isArray(steps)) return { count: 0, ok: 0, failed: [], names: [] };
  const failed = [];
  const names = [];
  let ok = 0;
  for (const s of steps) {
    const name = isPlainObject(s) && typeof s.name === 'string' ? s.name : '(unnamed)';
    names.push(name);
    if (isPlainObject(s) && s.ok === true) ok++;
    else failed.push(name);
  }
  return { count: steps.length, ok, failed, names };
}

function summarizeFinalChecks(checks) {
  if (!Array.isArray(checks)) return { count: 0, passed: 0, failed: [] };
  const failed = [];
  let passed = 0;
  for (const c of checks) {
    const name = isPlainObject(c) && typeof c.name === 'string' ? c.name : '(unnamed)';
    if (isPlainObject(c) && c.ok === true) passed++;
    else failed.push(name);
  }
  return { count: checks.length, passed, failed };
}

function countList(v) {
  return Array.isArray(v) ? v.length : 0;
}

function summarizeCleanup(cleanup) {
  if (!isPlainObject(cleanup)) return { ok: null, remaining: 0, unconfirmed: 0, entities: 0 };
  return {
    ok: cleanup.ok ?? null,
    remaining: countList(cleanup.remaining),
    unconfirmed: countList(cleanup.unconfirmed),
    entities: countList(cleanup.entities),
  };
}

function nonemptyString(v) {
  return typeof v === 'string' && v.length > 0 ? v : null;
}

function stampErrorsOf(v) {
  return Array.isArray(v.stampErrors) ? v.stampErrors : [];
}

export const FRAMEWORK_SIDE_NAMES = Object.freeze(['server', 'client']);
export const DEFAULT_OPERATION_SIDES = Object.freeze(['server', 'client']);

function derivedFramework(runOrReport) {
  const session = isPlainObject(runOrReport.session) ? runOrReport.session : {};
  const images = isPlainObject(runOrReport.images) ? runOrReport.images : {};
  const server = isPlainObject(images.server) ? images.server : {};
  const client = isPlainObject(images.client) ? images.client : {};
  return session.sampleHash
    ?? server.sampleHash
    ?? (session.generation != null ? `generation:${session.generation}` : null)
    ?? server.progenitor
    ?? client.progenitor
    ?? null;
}

function sidesObject(v) {
  if (!isPlainObject(v)) return { server: null, client: null };
  return {
    server: nonemptyString(v.server),
    client: nonemptyString(v.client),
  };
}

function sidesObjectPresent(v) {
  return isPlainObject(v) && (nonemptyString(v.server) != null || nonemptyString(v.client) != null);
}

/** True when at least one of server/client is a non-empty string. */
export function frameworkSidesRecorded(v) {
  return sidesObjectPresent(v);
}

/**
 * Canonical required sides for an operation: ["server"], ["client"], or ["server","client"].
 * Absent / empty / unknown values default conservatively to both.
 */
export function normalizeOperationSides(sides) {
  if (!Array.isArray(sides) || sides.length === 0) return [...DEFAULT_OPERATION_SIDES];
  const set = new Set(sides);
  const out = [];
  if (set.has('server')) out.push('server');
  if (set.has('client')) out.push('client');
  return out.length ? out : [...DEFAULT_OPERATION_SIDES];
}

/** True when `sides` is one of the three allowed operation declarations. */
export function isValidOperationSides(sides) {
  if (!Array.isArray(sides) || sides.length === 0) return false;
  const seen = new Set();
  for (const s of sides) {
    if (s !== 'server' && s !== 'client') return false;
    if (seen.has(s)) return false;
    seen.add(s);
  }
  return true;
}

/**
 * Sides whose image identity changed during a run (`stampTransition`).
 * Accepts `{server, client}` truthy flags, `{sides: ['client']}`, an array of
 * side names, or `true` (both unknown). Never infers a hybrid from the other side.
 */
export function sidesChangedDuringRun(stampTransition) {
  if (stampTransition == null || stampTransition === false) return [];
  if (stampTransition === true) return [...FRAMEWORK_SIDE_NAMES];
  if (Array.isArray(stampTransition)) {
    return FRAMEWORK_SIDE_NAMES.filter((s) => stampTransition.includes(s));
  }
  if (!isPlainObject(stampTransition)) return [];
  const out = [];
  if (stampTransition.server) out.push('server');
  if (stampTransition.client) out.push('client');
  if (Array.isArray(stampTransition.sides)) {
    for (const s of stampTransition.sides) {
      if ((s === 'server' || s === 'client') && !out.includes(s)) out.push(s);
    }
  }
  return out;
}

/**
 * Parse a framework stamp.
 * Full form: `generation:<n>; core:<hash>; dev:<hash>; sample:<hash>` (`null` tokens allowed).
 * Generation-only: `generation:<n>`. Anything else that starts with `generation:` is unparseable.
 * Older runs stored an opaque Sample hash / progenitor string with no `generation:` prefix.
 * Generation-only stamps and full forms whose core/dev/sample parts are the token `null` are
 * insufficient: they never match a current build.
 * @returns {{kind:'full', generation:string, core:string, dev:string, sample:string}
 *   | {kind:'generation', generation:string}
 *   | {kind:'opaque', value:string}
 *   | null}
 */
export function parseFrameworkStamp(value) {
  if (typeof value !== 'string' || value.length === 0) return null;
  if (!value.startsWith('generation:')) return { kind: 'opaque', value };
  const full = /^generation:([^;]+); core:([^;]+); dev:([^;]+); sample:([^;]+)$/.exec(value);
  if (full) {
    return {
      kind: 'full',
      generation: full[1],
      core: full[2],
      dev: full[3],
      sample: full[4],
    };
  }
  const gen = /^generation:([^;]+)$/.exec(value);
  if (gen) return { kind: 'generation', generation: gen[1] };
  return null;
}

function tokenPresent(t) {
  return t != null && t !== '' && t !== 'null';
}

/** True when the stamp carries at least one image hash (opaque sample hash, or a non-null core/dev/sample). */
export function frameworkStampHasImageHashes(value) {
  const p = parseFrameworkStamp(value);
  if (!p) return false;
  if (p.kind === 'opaque') return tokenPresent(p.value);
  if (p.kind === 'full') return tokenPresent(p.core) || tokenPresent(p.dev) || tokenPresent(p.sample);
  return false;
}

/**
 * Three-way framework stamp comparison.
 * Only stamps that carry image hashes compare. They match when generation and every recorded
 * hash (core, dev, sample where present on both sides) are equal. A hash present on one side
 * and missing on the other is unknown, never a match. Generation-only stamps and full forms
 * whose hash parts are the token `null` are insufficient (unknown). Unparseable stamps are a
 * mismatch. Null or empty values are unknown.
 * @returns {'match'|'mismatch'|'unknown'}
 */
export function frameworkStampCompare(a, b) {
  if (typeof a !== 'string' || a.length === 0 || typeof b !== 'string' || b.length === 0) return 'unknown';
  const pa = parseFrameworkStamp(a);
  const pb = parseFrameworkStamp(b);
  if (!pa || !pb) return 'mismatch';
  if (!frameworkStampHasImageHashes(a) || !frameworkStampHasImageHashes(b)) return 'unknown';

  if (pa.kind === 'opaque' && pb.kind === 'opaque') {
    return pa.value === pb.value ? 'match' : 'mismatch';
  }
  if (pa.kind === 'opaque' || pb.kind === 'opaque') {
    return 'unknown';
  }

  if (!tokenPresent(pa.generation) || !tokenPresent(pb.generation)) return 'unknown';
  if (pa.generation !== pb.generation) return 'mismatch';

  let unknown = false;
  for (const field of ['core', 'dev', 'sample']) {
    const aPresent = tokenPresent(pa[field]);
    const bPresent = tokenPresent(pb[field]);
    if (aPresent && bPresent) {
      if (pa[field] !== pb[field]) return 'mismatch';
    } else if (aPresent !== bPresent) {
      unknown = true;
    }
  }
  return unknown ? 'unknown' : 'match';
}

/**
 * Framework stamp equality for one side's `generation:; core:; dev:; sample:` string.
 * Only stamps that carry image hashes can match, and only when generation and every overlapping
 * hash are equal. A generation-only stamp, a stamp whose core/dev/sample parts are the token
 * `null`, a hash present on one side and missing on the other, and a null or unparseable stamp
 * never match, including identical `generation:null` strings.
 * A legacy-combined (server-first hybrid) string is not compared here; use
 * {@link frameworkSidesCompare} for claims.
 */
export function frameworkStampsMatch(a, b) {
  return frameworkStampCompare(a, b) === 'match';
}

/** Per-key stamp equality. Null never matches. Framework uses {@link frameworkStampsMatch}. */
export function stampValuesMatch(key, a, b) {
  if (a == null || b == null) return false;
  if (key === 'framework') return frameworkStampsMatch(a, b);
  return a === b;
}

/**
 * Compare required process sides of recorded vs current `frameworkSides`.
 * Never assembles a hybrid from the other side or from a combined `framework` string.
 * A missing/null/insufficient required side is unknown. A changed hash on any required
 * side is mismatch. Equality on a side the operation did not declare is ignored.
 * A legacy report with no `frameworkSides` is unknown for any claim (never a match).
 *
 * Shared by CLI inspect, MCP inspect, and library applicability.
 * @returns {'match'|'mismatch'|'unknown'}
 */
export function frameworkSidesCompare(recordedSides, currentSides, requiredSides) {
  const sides = normalizeOperationSides(requiredSides);
  if (!isPlainObject(recordedSides) || !isPlainObject(currentSides)) return 'unknown';
  let unknown = false;
  for (const side of sides) {
    const rec = nonemptyString(recordedSides[side]);
    const cur = nonemptyString(currentSides[side]);
    if (rec == null || cur == null) {
      unknown = true;
      continue;
    }
    const rel = frameworkStampCompare(rec, cur);
    if (rel === 'mismatch') return 'mismatch';
    if (rel !== 'match') unknown = true;
  }
  return unknown ? 'unknown' : 'match';
}

/** True when any required current side carries image hashes (used for runsOtherBuild). */
export function requiredSidesHaveImageHashes(currentSides, requiredSides) {
  const sides = normalizeOperationSides(requiredSides);
  if (!isPlainObject(currentSides)) return false;
  return sides.some((side) => frameworkStampHasImageHashes(currentSides[side]));
}

/**
 * Stamps that can be compared with a recipe evidence `build` / inspect `currentBuild`.
 * `frameworkSides` is the report's own per-side strings (new recorder) or `{server:null,
 * client:null}` when absent. The combined `framework` string is retained as
 * `legacy-combined` display only: it is never copied onto both sides and never used as
 * a match. `stampErrors` is copied from the report (empty on old runs).
 * Null fields were not recorded and stay unknown; they are never back-filled from the
 * other side, from launch notes, or from today's binaries.
 */
export function buildStampsFromRun(runOrReport) {
  if (!isPlainObject(runOrReport)) {
    return {
      contentCatalogSha256: null,
      gameIdentity: null,
      framework: null,
      frameworkSides: { server: null, client: null },
      stampErrors: [],
      stampPhases: null,
      stampTransition: null,
      gameIdentitySides: null,
    };
  }
  const topFramework = nonemptyString(runOrReport.framework);
  const frameworkSides = sidesObject(runOrReport.frameworkSides);
  return {
    contentCatalogSha256: nonemptyString(runOrReport.contentCatalogSha256),
    gameIdentity: nonemptyString(runOrReport.gameIdentity),
    framework: topFramework ?? derivedFramework(runOrReport),
    frameworkSides,
    stampErrors: stampErrorsOf(runOrReport),
    stampPhases: isPlainObject(runOrReport.stampPhases) ? runOrReport.stampPhases : null,
    stampTransition: runOrReport.stampTransition ?? null,
    gameIdentitySides: isPlainObject(runOrReport.gameIdentitySides) ? runOrReport.gameIdentitySides : null,
  };
}

/**
 * Applicability of a found run against `currentBuild`.
 * Catalog and game-identity strings compare as before. Framework comparison uses
 * {@link frameworkSidesCompare} on `requiredSides` (default both). A combined
 * `framework` string is legacy-combined: it is never a match and is not compared
 * when `frameworkSides` is present. Missing or insufficient required-side hashes
 * stay `unknown-stamps`. A changed hash on any required side is `stale`.
 * A side that changed during the run (`stampTransition`) is unknown for that claim.
 * Returns null when there is nothing to compare (caller keeps the recipe-evidence
 * applicability).
 */
export function applicabilityFromRun(run, currentBuild, requiredSides = DEFAULT_OPERATION_SIDES) {
  if (!run?.found) return null;
  const recorded = buildStampsFromRun(run);
  const sides = normalizeOperationSides(requiredSides);
  let compared = 0;
  let stale = false;
  let unknown = false;
  for (const k of ['contentCatalogSha256', 'gameIdentity']) {
    const e = recorded[k];
    if (e == null) continue;
    compared++;
    const c = currentBuild?.[k] ?? null;
    if (c == null) unknown = true;
    else if (!stampValuesMatch(k, e, c)) stale = true;
  }

  const changed = sidesChangedDuringRun(recorded.stampTransition);
  const recSides = recorded.frameworkSides;
  const curSides = isPlainObject(currentBuild?.frameworkSides) ? currentBuild.frameworkSides : null;
  if (sidesObjectPresent(recSides)) {
    compared++;
    if (changed.some((s) => sides.includes(s))) {
      unknown = true;
    } else {
      const rel = frameworkSidesCompare(recSides, curSides ?? { server: null, client: null }, sides);
      if (rel === 'mismatch') stale = true;
      else if (rel !== 'match') unknown = true;
    }
  }
  // A combined `framework` string is legacy-combined display only: never a match
  // and never copied onto both sides. Recipe `build.frameworkSides` is the
  // recovered claim when the report has no per-side field.

  if (compared === 0) return null;
  if (stale) return 'stale';
  if (unknown) return 'unknown-stamps';
  return 'matches-checkable-stamps';
}

function readBoundedTrace(absDir, stepName) {
  const tracePath = path.join(absDir, 'trace.jsonl');
  if (!existsSync(tracePath)) return { found: false, reason: 'trace.jsonl not found', lines: [] };
  const needle = String(stepName).toLowerCase();
  let fd;
  try {
    fd = openSync(tracePath, 'r');
  } catch (e) {
    return { found: false, reason: `trace.jsonl unreadable: ${e.message}`, lines: [] };
  }
  try {
    const size = fstatSync(fd).size;
    const want = Math.min(MAX_TRACE_BYTES, size);
    const buf = Buffer.alloc(want);
    const n = readSync(fd, buf, 0, want, 0);
    const text = buf.slice(0, n).toString('utf8');
    const rawLines = text.split(/\r?\n/);
    const lines = [];
    let scanned = 0;
    for (const line of rawLines) {
      if (!line) continue;
      scanned++;
      if (scanned > MAX_TRACE_SCAN_LINES) break;
      if (!line.toLowerCase().includes(needle)) continue;
      try { lines.push(JSON.parse(line)); }
      catch { lines.push({ raw: line.slice(0, 240) }); }
      if (lines.length >= MAX_TRACE_MATCHES) break;
    }
    return {
      found: true,
      scanned,
      truncated: scanned >= MAX_TRACE_SCAN_LINES || size > MAX_TRACE_BYTES || lines.length >= MAX_TRACE_MATCHES,
      lines,
    };
  } finally {
    closeSync(fd);
  }
}

/**
 * Read one live-rig run directory.
 * `opts.step` — if set, attach a bounded trace excerpt whose lines mention that step name.
 * `opts.repoRoot` — resolve a repo-relative path; defaults to the Progenitor root.
 *
 * @returns {{found:true, status, session, images, finalChecks, steps, cleanup,
 *            contentCatalogSha256, gameIdentity, framework, stampErrors, agentReported:false}}
 *          or `{found:false, reason}`
 */
export function readRigRun(dir, opts = {}) {
  const repoRoot = opts.repoRoot ?? REPO_ROOT;
  if (typeof dir !== 'string' || dir.trim().length === 0) return { found: false, reason: 'path required' };
  const abs = path.isAbsolute(dir) ? dir : path.resolve(repoRoot, dir);
  let dirStat;
  try { dirStat = lstatSync(abs); }
  catch { return { found: false, reason: 'run directory not found' }; }
  if (dirStat.isSymbolicLink()) return { found: false, reason: 'symlinked directory refused' };
  try {
    readlinkSync(abs);
    return { found: false, reason: 'symlinked directory refused' };
  } catch {
    // not a reparse point / junction
  }
  if (!dirStat.isDirectory()) return { found: false, reason: 'path is not a directory' };

  const reportPath = path.join(abs, 'report.json');
  let fd;
  try {
    fd = openSync(reportPath, 'r');
  } catch {
    return { found: false, reason: 'report.json not found' };
  }
  try {
    const size = fstatSync(fd).size;
    if (size > MAX_REPORT_BYTES) {
      return { found: false, reason: `report.json exceeds ${MAX_REPORT_BYTES} bytes` };
    }
    const buf = Buffer.alloc(size);
    const n = readSync(fd, buf, 0, size, 0);
    let report;
    try {
      report = JSON.parse(buf.slice(0, n).toString('utf8'));
    } catch (e) {
      return { found: false, reason: `unreadable report.json: ${e.message}` };
    }
    if (!isPlainObject(report)) return { found: false, reason: 'report.json is not an object' };

    const out = {
      found: true,
      status: typeof report.status === 'string' ? report.status : null,
      session: summarizeSession(report.session),
      images: summarizeImages(report.images),
      finalChecks: summarizeFinalChecks(report.finalChecks),
      steps: summarizeSteps(report.steps),
      cleanup: summarizeCleanup(report.cleanup),
      contentCatalogSha256: nonemptyString(report.contentCatalogSha256),
      gameIdentity: nonemptyString(report.gameIdentity),
      gameIdentitySides: isPlainObject(report.gameIdentitySides) ? {
        server: nonemptyString(report.gameIdentitySides.server),
        client: nonemptyString(report.gameIdentitySides.client),
      } : null,
      framework: nonemptyString(report.framework),
      frameworkSides: isPlainObject(report.frameworkSides) ? sidesObject(report.frameworkSides) : null,
      stampErrors: stampErrorsOf(report),
      stampPhases: isPlainObject(report.stampPhases) ? report.stampPhases : null,
      stampTransition: report.stampTransition ?? null,
      agentReported: false,
    };
    if (opts.step != null && String(opts.step).length > 0) {
      out.trace = readBoundedTrace(abs, opts.step);
    }
    return out;
  } finally {
    closeSync(fd);
  }
}
