import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createBridge } from './bridge.mjs';
import { realClock } from './clock.mjs';
import {
  createTracker,
  DEFAULT_CLEANUP_TIMEOUT_MS,
  DEFAULT_CLEANUP_POLL_MS,
  DEFAULT_FINALIZE_TIMEOUT_MS,
  DEFAULT_FINALIZE_POLL_MS,
  playerOwnedIds,
  isDeniedPlayerPrefab,
  prefabDisplayName,
  isThiefOrDiagRow,
} from './tracker.mjs';
import { createReloader, sampleHashFromPing, lastReloadWasRemovalWithoutReinstall } from './reload.mjs';
import {
  runFinalChecks,
  protection as protectionCheck,
  rearmProtection,
  PROTECTION_UNAVAILABLE,
  queryCarriers,
  cleanupClientCarriers,
  NETWORKED_SEQUENCE_PREFAB,
  carrierEntityId,
  carrierPrefabName,
  isNetworkedSequenceCarrier,
} from './checks.mjs';
import {
  executeStep,
  stepName,
  createChildRegistry,
  cancelTrackedChildren,
  refuseNewChildren,
  isRunCancelled,
  trackSpawnedChild,
  makeCancelledChild,
} from './steps.mjs';
import { existsRequest, parseResult } from './entityOp.mjs';
import { scanServerLogSignals, emptyLogSignals, hasFatalLogSignals } from './logs.mjs';
import { collectBuildStamps, stampsForReport, finalizeReportStamps, DEFAULT_GAME_INSTALL } from './stamps.mjs';

/** Ping has no stable session id; start time is now minus uptimeSeconds, matched within this window. */
export const SESSION_START_TOLERANCE_MS = 5000;

/** cleanupAll destroy requests run first; these readbacks follow. */
export const FINAL_CHECKS_ORDER = [
  'cleanupAll',
  'playerHealth',
  'protection',
  'adminRestore',
  'adminState',
  'remainingTestEntities',
  'carriers',
];

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const TOOL_DIR = path.resolve(HERE, '..');
export const REPO_ROOT = path.resolve(HERE, '..', '..', '..');
export const DEFAULT_CASES_DIR = path.join(TOOL_DIR, 'cases');
export const CASES_UI_DIR = path.join(TOOL_DIR, 'cases-ui');
export const CASES_CATALOG_DIR = path.join(TOOL_DIR, 'cases-catalog');
export const DEFAULT_OUT_DIR = path.join(TOOL_DIR, 'runs');
export const CASE_SEARCH_DIRS = Object.freeze([DEFAULT_CASES_DIR, CASES_UI_DIR, CASES_CATALOG_DIR]);

export function utcStamp(d = new Date()) {
  return d.toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');
}

export function loadCase(file) {
  const resolved = path.resolve(file);
  if (fs.existsSync(resolved) && fs.statSync(resolved).isFile()) {
    return { file: resolved, doc: JSON.parse(fs.readFileSync(resolved, 'utf8')) };
  }
  const name = file.endsWith('.json') ? file : `${file}.json`;
  const base = path.basename(name);
  for (const dir of CASE_SEARCH_DIRS) {
    const fallback = path.join(dir, base);
    if (fs.existsSync(fallback)) {
      return { file: fallback, doc: JSON.parse(fs.readFileSync(fallback, 'utf8')) };
    }
  }
  throw new Error(`case not found: ${file}`);
}

export function listCases(dir = DEFAULT_CASES_DIR) {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir)
    .filter((f) => f.endsWith('.json'))
    .sort()
    .map((f) => {
      const file = path.join(dir, f);
      const doc = JSON.parse(fs.readFileSync(file, 'utf8'));
      return { file, name: doc.name ?? path.basename(f, '.json'), summary: doc.summary ?? '', steps: (doc.steps ?? []).length };
    });
}

function pickPlayer(players, users) {
  const list = players?.players ?? [];
  const connected = list.find((p) => p.connected) ?? list[0];
  if (!connected) return null;
  const userList = users?.users ?? [];
  const user = userList.find((u) => u.name === connected.name)
    ?? userList.find((u) => u.character === connected.entity)
    ?? userList.find((u) => u.entity === connected.userEntity);
  return { player: connected, user };
}

/** Client game world name on Dev ping. Absent at the main menu. */
export const CLIENT_GAME_WORLD = 'Client_0';

export const CASE_WORLD_CONNECTED = 'connected';
export const CASE_WORLD_MAIN_MENU = 'main-menu';
export const CASE_WORLD_ANY = 'any';

/** Open sites accepted by `mods_ui` `open` and by `--site`. */
export const CASE_SITES = Object.freeze(['main-menu', 'top-tab', 'shortcut']);
export const SITE_PLACEHOLDER = '@site';

/** Case-level world. Omitted or empty is `connected` (today's start state). */
export function caseRequestedWorld(doc) {
  const w = doc?.world;
  if (w == null || w === '') return CASE_WORLD_CONNECTED;
  if (w === CASE_WORLD_CONNECTED || w === CASE_WORLD_MAIN_MENU || w === CASE_WORLD_ANY) return w;
  throw new Error(`case world must be "connected", "main-menu", or "any"; got ${JSON.stringify(w)}`);
}

/**
 * World the case ends in, for final-check selection. Omitted or empty follows the
 * resolved start world. Not `any` — name the end state.
 */
export function caseEndWorld(doc) {
  const w = doc?.endWorld;
  if (w == null || w === '') return null;
  if (w === CASE_WORLD_CONNECTED || w === CASE_WORLD_MAIN_MENU) return w;
  throw new Error(`case endWorld must be "connected" or "main-menu"; got ${JSON.stringify(w)}`);
}

export function pingWorldNames(ping) {
  const worlds = ping?.worlds;
  if (!Array.isArray(worlds)) return [];
  const names = [];
  for (const w of worlds) {
    if (typeof w === 'string' && w) {
      names.push(w);
      continue;
    }
    if (w && typeof w === 'object') {
      const name = w.name ?? w.id ?? w.Name;
      if (name != null && String(name)) names.push(String(name));
    }
  }
  return names;
}

export function pingHasClientGameWorld(ping) {
  return pingWorldNames(ping).includes(CLIENT_GAME_WORLD);
}

/** Detected world from the client ping: `Client_0` present → connected, else main-menu. */
export function detectWorldFromPing(ping) {
  return pingHasClientGameWorld(ping) ? CASE_WORLD_CONNECTED : CASE_WORLD_MAIN_MENU;
}

/** Default open site when `--site` is omitted: main-menu with no `Client_0`, top-tab when connected. */
export function defaultSiteForWorld(world) {
  return world === CASE_WORLD_MAIN_MENU ? CASE_WORLD_MAIN_MENU : 'top-tab';
}

/**
 * `--site` wins. Else a concrete case `site` (not `@site`). Else main-menu when the
 * detected world has no `Client_0`, top-tab when connected.
 */
export function resolveRunSite(options = {}, detectedWorld = CASE_WORLD_CONNECTED, caseDoc = null) {
  const requested = options.site;
  if (requested != null && requested !== '') {
    if (!CASE_SITES.includes(requested)) {
      throw new Error(`--site must be main-menu, top-tab, or shortcut; got ${JSON.stringify(requested)}`);
    }
    return requested;
  }
  const fromCase = caseDoc?.site;
  if (fromCase && fromCase !== SITE_PLACEHOLDER) return fromCase;
  return defaultSiteForWorld(detectedWorld === CASE_WORLD_MAIN_MENU ? CASE_WORLD_MAIN_MENU : CASE_WORLD_CONNECTED);
}

/** Replace `args.site: "@site"` on a `mods_ui` open step. Other steps are returned unchanged. */
export function applyOpenSitePlaceholder(step, site) {
  if (site == null || site === '' || !step || typeof step !== 'object') return step;
  if (step.kind !== 'op') return step;
  if (String(step.op ?? '').toLowerCase() !== 'mods_ui') return step;
  const args = step.args;
  if (!args || typeof args !== 'object') return step;
  if (args.action !== 'open') return step;
  if (args.site !== SITE_PLACEHOLDER) return step;
  return { ...step, args: { ...args, site } };
}

function imageFromPing(ping) {
  return {
    side: ping?.side,
    progenitor: ping?.progenitor,
    reloadFolder: ping?.reloadFolder,
    reloadedPlugins: ping?.reloadedPlugins ?? [],
    logSeq: ping?.logSeq,
    generation: ping?.reload?.generation,
    alcName: ping?.reload?.alcName,
    instance: ping?.instance ?? '',
    sampleHash: sampleHashFromPing(ping),
  };
}

async function pingOrNull(bridge, side) {
  try {
    return await bridge.call(side, 'ping');
  } catch {
    return null;
  }
}

function isoNow(clock) {
  return new Date(clock.now()).toISOString();
}

function applyStampPhases(startCollected, finalCollected, startAt, finalAt, findings) {
  const { stamps, transitionFindings } = finalizeReportStamps(startCollected, finalCollected, {
    startAt,
    finalAt,
  });
  if (Array.isArray(findings)) {
    for (const line of transitionFindings) findings.push(line);
  }
  return stamps;
}

function printLine(log, ok, name, ms, extra) {
  const mark = ok ? 'PASS' : 'FAIL';
  const suffix = extra ? ` ${extra}` : '';
  log(`${mark} ${name} (${ms}ms)${suffix}`);
}

function formatRemaining(rows) {
  return (rows ?? []).map((e) => {
    const bits = [e.entity, e.prefab, e.hostile ? 'hostile' : null, e.skipCleanup ? 'skipCleanup' : null].filter(Boolean);
    return bits.join(' ');
  });
}

function formatUnconfirmedLine(rows) {
  return (rows ?? []).map((e) => {
    const bits = [e.entity, e.prefab, e.hostile ? 'hostile' : null, e.error ? `error=${e.error}` : null].filter(Boolean);
    return bits.join(' ');
  });
}

function leftoverPrefabName(row) {
  if (row == null || typeof row === 'string') return null;
  return carrierPrefabName(row) ?? prefabDisplayName(row.prefab) ?? null;
}

/**
 * Client leftover carriers from `cleanup.clientCarriers.remaining` plus leftover
 * client `NetworkedSequence` rows on the carriers check. Adopted on the client side.
 */
export function leftoverClientCarrierRows(report) {
  const byId = new Map();

  function add(entry) {
    const id = typeof entry === 'string' ? entry : carrierEntityId(entry);
    if (!id) return;
    const key = String(id);
    const prev = byId.get(key);
    const prefab = leftoverPrefabName(entry) ?? prev?.prefab ?? null;
    const side = (typeof entry === 'object' && entry?.side) || prev?.side || 'client';
    byId.set(key, {
      name: (typeof entry === 'object' && entry?.name) || prev?.name || `client-carrier:${key}`,
      entity: key,
      side,
      prefab,
      hostile: false,
      skipCleanup: false,
    });
  }

  for (const row of report?.cleanup?.clientCarriers?.remaining ?? []) add(row);

  const carriersCheck = (report?.finalChecks ?? []).find((c) => c.name === 'carriers');
  for (const row of carriersCheck?.value?.leftover?.client ?? []) {
    const id = carrierEntityId(row);
    if (!id) continue;
    if (isNetworkedSequenceCarrier(row) || byId.has(String(id))) add(row);
  }
  return [...byId.values()];
}

function leftoverSources(report) {
  return [
    report?.cleanup?.remaining,
    report?.cleanup?.unconfirmed,
    report?.teardown?.remaining,
    report?.teardown?.unconfirmed,
    leftoverClientCarrierRows(report),
  ];
}

function leftoverEntryId(e) {
  if (e == null) return null;
  if (typeof e === 'string' && e) return String(e);
  if (typeof e === 'object' && e.entity) return String(e.entity);
  return null;
}

function normalizeLeftoverEntry(e, extra = {}) {
  const id = leftoverEntryId(e);
  if (!id) return null;
  if (typeof e === 'string') {
    return {
      name: extra.name ?? `leftover:${id}`,
      entity: id,
      side: extra.side ?? 'server',
      prefab: extra.prefab ?? null,
      hostile: false,
      skipCleanup: false,
    };
  }
  return {
    name: e.name ?? extra.name ?? `leftover:${id}`,
    entity: id,
    side: e.side ?? extra.side ?? 'server',
    prefab: leftoverPrefabName(e) ?? extra.prefab ?? null,
    hostile: !!e.hostile,
    skipCleanup: !!e.skipCleanup,
    error: e.error,
  };
}

/** `--attach-readback` defaults on for thief/DIAG cases (name, summary, or a thief prefab step). */
export function caseWantsAttachReadback(doc) {
  const text = `${doc?.name ?? ''} ${doc?.summary ?? ''}`;
  if (/thief|diag/i.test(text)) return true;
  for (const step of doc?.steps ?? []) {
    if (isThiefOrDiagRow({ name: step.name, prefab: step.prefab })) return true;
  }
  return false;
}

export function leftoverEntitiesFromReport(report) {
  const rows = [];
  const seen = new Set();
  for (const src of leftoverSources(report)) {
    for (const e of src ?? []) {
      const row = normalizeLeftoverEntry(e);
      const id = row?.entity;
      if (!id || seen.has(id)) continue;
      seen.add(id);
      rows.push(row);
    }
  }
  return rows;
}

/** Rows listed as remaining/unconfirmed, including entries that lack an entity id. */
export function sourceReportListedLeftoverCount(report) {
  let n = 0;
  for (const src of leftoverSources(report)) n += (src ?? []).length;
  return n;
}

/**
 * Session pin from a server ping. Ping has no stable session field, so this is
 * Core generation + Sample hash + (now − uptimeSeconds). Compare with
 * {@link SESSION_START_TOLERANCE_MS}.
 */
export function sessionPinFromPing(ping, nowMs = Date.now()) {
  const uptime = Number(ping?.uptimeSeconds);
  const startedMs = Number.isFinite(uptime) ? nowMs - uptime * 1000 : null;
  return {
    serverStartedUtc: startedMs != null && Number.isFinite(startedMs)
      ? new Date(startedMs).toISOString()
      : null,
    sampleHash: sampleHashFromPing(ping) ?? null,
    generation: ping?.reload?.generation ?? ping?.generation ?? null,
  };
}

export function sessionPinsMatch(previous, current, toleranceMs = SESSION_START_TOLERANCE_MS) {
  if (!previous || !current) return false;
  if (previous.generation !== current.generation) return false;
  if ((previous.sampleHash ?? null) !== (current.sampleHash ?? null)) return false;
  const a = Date.parse(previous.serverStartedUtc);
  const b = Date.parse(current.serverStartedUtc);
  if (!Number.isFinite(a) || !Number.isFinite(b)) return false;
  return Math.abs(a - b) <= toleranceMs;
}

export function prefabKey(prefab) {
  if (prefab == null || prefab === '') return null;
  if (typeof prefab === 'string' || typeof prefab === 'number') return String(prefab);
  if (typeof prefab === 'object') {
    if (prefab.name != null && prefab.name !== '') return String(prefab.name);
    if (prefab.hash != null && prefab.hash !== '') return String(prefab.hash);
  }
  return null;
}

export function prefabsMatch(recorded, live) {
  const a = prefabKey(recorded);
  const b = prefabKey(live);
  if (a == null && b == null) return true;
  if (a == null || b == null) return false;
  return a === b;
}

function formatLeftoverRow(row) {
  return {
    name: row.name,
    side: row.side ?? 'server',
    entity: row.entity,
    prefab: row.prefab ?? null,
    hostile: !!row.hostile,
    skipCleanup: !!row.skipCleanup,
  };
}

function isClientNetworkedSequence(row) {
  return (row.side ?? 'server') === 'client'
    && (leftoverPrefabName(row) === NETWORKED_SEQUENCE_PREFAB);
}

/** Client leftovers that are not NetworkedSequence are listed, never destroyed. */
function shouldDestroyLeftover(row) {
  if ((row.side ?? 'server') !== 'client') return true;
  return isClientNetworkedSequence(row);
}

async function refusePrefabMismatches(bridge, leftovers) {
  const accepted = [];
  const refusals = [];
  if (!leftovers.length) return { accepted, refusals };
  const bySide = new Map();
  for (const row of leftovers) {
    const side = row.side ?? 'server';
    if (!bySide.has(side)) bySide.set(side, []);
    bySide.get(side).push(row);
  }
  for (const [side, rows] of bySide) {
    let parsed;
    try {
      parsed = parseResult(await bridge.call(side, 'entity', existsRequest(rows.map((e) => e.entity))));
    } catch {
      accepted.push(...rows);
      continue;
    }
    const byId = new Map((parsed.entries ?? []).map((e) => [e.entity, e]));
    for (const row of rows) {
      const hit = byId.get(row.entity);
      if (!hit || hit.exists === undefined || hit.exists === null) {
        accepted.push(row);
        continue;
      }
      if (hit.exists === false) {
        accepted.push(row);
        continue;
      }
      const liveName = prefabDisplayName(hit.prefab);
      if (row.prefab == null || row.prefab === '') {
        accepted.push({ ...row, prefab: liveName ?? null });
        continue;
      }
      if (!prefabsMatch(row.prefab, hit.prefab)) {
        refusals.push({
          name: row.name,
          entity: row.entity,
          reason: 'prefab mismatch',
          recordedPrefab: row.prefab ?? null,
          livePrefab: hit.prefab ?? null,
          hostile: !!row.hostile,
          side,
        });
        continue;
      }
      accepted.push(row);
    }
  }
  return { accepted, refusals };
}

function uniqueTrackedName(tracker, base) {
  const used = new Set(tracker.list().map((e) => e.name));
  let name = base || 'leftover';
  if (!used.has(name)) return name;
  let n = 2;
  while (used.has(`${name}#${n}`)) n++;
  return `${name}#${n}`;
}

/** CLI exit: 0 only for complete. incomplete and aborted are 1. Usage errors stay 2 in rig.mjs. */
export function exitCodeForStatus(status) {
  return status === 'complete' ? 0 : 1;
}

export function createRestoreRegistry() {
  const items = [];
  return {
    get items() { return items; },
    requireAck(item, extra = {}) {
      const name = String(item ?? '').trim();
      if (!name) return null;
      const row = {
        item: name,
        ackRequired: true,
        ackObserved: false,
        observedAt: null,
        step: extra.step ?? null,
        expect: extra.expect ?? null,
        savedAck: name === 'settings' ? false : undefined,
        valueAck: name === 'settings' ? false : undefined,
      };
      items.push(row);
      return row;
    },
    observeAck(item, extra = {}) {
      const name = String(item ?? '').trim();
      const row = [...items].reverse().find((r) => r.item === name && r.ackRequired && !r.ackObserved);
      if (!row) return null;
      row.ackObserved = true;
      row.observedAt = extra.at ?? new Date().toISOString();
      return row;
    },
    pending() {
      return items.filter((r) => r.ackRequired && !r.ackObserved);
    },
    snapshot() {
      return {
        items: items.map((r) => ({
          item: r.item,
          ackRequired: r.ackRequired,
          ackObserved: r.ackObserved,
          observedAt: r.observedAt,
          step: r.step,
        })),
        unconfirmed: items.filter((r) => r.ackRequired && !r.ackObserved).map((r) => r.item),
      };
    },
  };
}

function inferRestoreItem(step) {
  if (!step || step.kind !== 'op') return null;
  const op = String(step.op ?? '').toLowerCase();
  const args = step.args ?? {};
  if (op === 'mods_ui' && args.action === 'widget_click' && /apply/i.test(String(args.label ?? ''))) {
    return 'settings';
  }
  if (op === 'mods_ui' && args.action === 'resolution') return 'resolution';
  if (op === 'set' && args.component === 'User' && args.fields && Object.prototype.hasOwnProperty.call(args.fields, 'IsAdmin')) {
    // A grant (IsAdmin true) is not a restoration. Restoring the original
    // admin flag is IsAdmin false in the cases this rig ships.
    if (args.fields.IsAdmin === true) return null;
    return 'admin';
  }
  return null;
}

function restoreItemForStep(step, ctx) {
  if (!step) return null;
  if (step.ackRequired != null && step.ackRequired !== false) {
    if (step.ackRequired === true) return inferRestoreItem(step);
    return String(step.ackRequired);
  }
  const inferred = inferRestoreItem(step);
  if (!inferred) return null;
  if (ctx?.teardown) return inferred;
  if (/\brestore\b/i.test(String(step.name ?? ''))) return inferred;
  return null;
}

function detailBlob(detail) {
  try {
    return JSON.stringify(detail ?? {});
  } catch {
    return String(detail ?? '');
  }
}

function unwrapAdminFlag(v) {
  if (typeof v === 'boolean') return v;
  if (v && typeof v === 'object') {
    if (typeof v.Value === 'boolean') return v.Value;
    if (typeof v._Value === 'boolean') return v._Value;
  }
  if (String(v).toLowerCase() === 'true') return true;
  if (String(v).toLowerCase() === 'false') return false;
  return undefined;
}

function resultAfter(detail) {
  const result = detail?.result && typeof detail.result === 'object' ? detail.result : detail;
  if (result?.after && typeof result.after === 'object') return result.after;
  return result && typeof result === 'object' ? result : null;
}

function resolutionExpectFrom(step, detail) {
  const args = (detail && detail.args && typeof detail.args === 'object') ? detail.args : (step?.args ?? {});
  return {
    width: args.width,
    height: args.height,
    mode: args.mode,
  };
}

function resolutionFieldsMatch(expect, fields) {
  if (!fields || typeof fields !== 'object') return false;
  const width = fields.width ?? fields.clientWidth ?? fields.requestedWidth;
  const height = fields.height ?? fields.clientHeight ?? fields.requestedHeight;
  const mode = fields.mode ?? fields.fullScreenMode ?? fields.requestedMode;
  const widthOk = expect.width == null || String(width) === String(expect.width);
  const heightOk = expect.height == null || String(height) === String(expect.height);
  const modeOk = expect.mode == null
    || String(mode ?? '').toLowerCase() === String(expect.mode).toLowerCase();
  return widthOk && heightOk && modeOk;
}

function isResolutionReadbackStep(step) {
  if (!step) return false;
  if (step.kind === 'assertWindow') return true;
  if (step.kind !== 'op') return false;
  const op = String(step.op ?? '').toLowerCase();
  if (op === 'mods_ui') {
    const action = String(step.args?.action ?? '').toLowerCase();
    return action === 'status' || action === 'window';
  }
  return false;
}

function isAdminReadbackStep(step) {
  if (!step) return false;
  if (step.kind === 'get' && String(step.component ?? '') === 'User') return true;
  if (step.kind !== 'op') return false;
  const op = String(step.op ?? '').toLowerCase();
  return op === 'get' || op === 'users';
}

function adminFlagFromDetail(detail, entity) {
  const result = detail?.result && typeof detail.result === 'object' ? detail.result : detail;
  if (!result || typeof result !== 'object') return undefined;
  const fields = result.fields ?? result;
  if (fields && Object.prototype.hasOwnProperty.call(fields, 'IsAdmin')) {
    return unwrapAdminFlag(fields.IsAdmin);
  }
  const users = result.users ?? result;
  if (Array.isArray(users)) {
    const row = (entity
      ? users.find((u) => u.entity === entity)
      : null) ?? users[0];
    if (row && Object.prototype.hasOwnProperty.call(row, 'admin')) return unwrapAdminFlag(row.admin);
  }
  return undefined;
}

function maybeObserveSettings(restore, detail) {
  const blob = detailBlob(detail);
  const saved = /Saved by the server/i.test(blob);
  const value = /"value"\s*:/i.test(blob) || /\bvalue=/.test(blob);
  if (!saved && !value) return;
  for (const row of restore.items) {
    if (row.item !== 'settings' || !row.ackRequired || row.ackObserved) continue;
    if (saved) row.savedAck = true;
    if (value) row.valueAck = true;
    if (row.savedAck && row.valueAck) restore.observeAck('settings');
  }
}

export function noteStepRestoration(step, detail, restore, ctx) {
  if (!restore || !step) return;
  const item = restoreItemForStep(step, ctx);
  if (item && detail?.ok !== false) {
    const extra = { step: step.name ?? step.kind };
    if (item === 'resolution') extra.expect = resolutionExpectFrom(step, detail);
    if (item === 'admin') {
      const args = (detail && detail.args && typeof detail.args === 'object') ? detail.args : (step.args ?? {});
      extra.expect = { admin: args?.fields?.IsAdmin, entity: args?.entity };
    }
    restore.requireAck(item, extra);
    // Acknowledgments are later readbacks, not the mutation itself:
    // settings Apply -> polled Saved + value; admin set -> get/users;
    // resolution -> window/status/assertWindow.
  }
  const op = String(step.op ?? '').toLowerCase();
  const args = step.args ?? {};
  if (step.kind === 'op' && op === 'mods_ui' && args.action === 'widget_read') {
    maybeObserveSettings(restore, detail);
  }
  if (detail?.ok !== false && isResolutionReadbackStep(step)) {
    for (const row of restore.pending()) {
      if (row.item !== 'resolution') continue;
      const fields = resultAfter(detail) ?? detail;
      if (resolutionFieldsMatch(row.expect ?? {}, fields)) restore.observeAck('resolution');
    }
  }
  if (detail?.ok !== false && isAdminReadbackStep(step)) {
    for (const row of restore.pending()) {
      if (row.item !== 'admin') continue;
      const flag = adminFlagFromDetail(detail, row.expect?.entity);
      const expected = row.expect && Object.prototype.hasOwnProperty.call(row.expect, 'admin')
        ? row.expect.admin
        : false;
      if (flag === expected) restore.observeAck('admin');
    }
  }
}

function finalCheckCountsAsPass(c) {
  if (!c) return false;
  if (c.unavailable) return true;
  return !!c.ok;
}

/**
 * Swap/removal hostiles.findings must be findings produced during that step.
 * Tracker cleanup returns the run-level findings array by reference; copying
 * a slice at record time keeps later run findings out of earlier step records.
 */
function snapshotHostilesFindings(detail, runFindings, fromIndex) {
  if (!detail || typeof detail !== 'object') return;
  if (!detail.hostiles || typeof detail.hostiles !== 'object') return;
  detail.hostiles.findings = Array.isArray(runFindings) ? runFindings.slice(fromIndex) : [];
}

/**
 * Execute one case. Always runs cleanupAll and final checks, even after a step abort.
 * status is "complete" only when every step passed, cleanup ok, and every final check ok.
 * Admin grant/restore and admin-only chat commands always use the SERVER user entity;
 * `--side` only selects the reload folder/image and the default snippet side.
 */
export async function runCase(caseDoc, options = {}) {
  const clock = options.clock ?? realClock();
  const io = options.fs ?? fs;
  const reloadSide = options.side ?? 'server';
  const started = new Date();
  const caseName = caseDoc.name ?? 'case';
  const outRoot = options.outDir ?? DEFAULT_OUT_DIR;
  const runDir = options.runDir ?? path.join(outRoot, `${utcStamp(started)}-${caseName}`);
  io.mkdirSync(runDir, { recursive: true });

  const bridge = options.bridge ?? createBridge({ runDir, transport: options.transport, sides: options.sides, timeoutMs: options.bridgeTimeoutMs });
  const findings = [];
  const tracker = options.tracker ?? createTracker({
    bridge,
    clock,
    side: 'server',
    timeoutMs: options.cleanupTimeoutMs ?? DEFAULT_CLEANUP_TIMEOUT_MS,
    pollMs: options.cleanupPollMs ?? DEFAULT_CLEANUP_POLL_MS,
    finalizeTimeoutMs: options.finalizeTimeoutMs ?? DEFAULT_FINALIZE_TIMEOUT_MS,
    finalizePollMs: options.finalizePollMs ?? DEFAULT_FINALIZE_POLL_MS,
    attachReadback: options.attachReadback !== undefined
      ? options.attachReadback
      : (caseWantsAttachReadback(caseDoc) ? true : undefined),
    findings,
  });
  const records = {};
  const childReg = options.children ?? createChildRegistry();
  const restore = options.restore ?? createRestoreRegistry();
  let stopRequested = false;
  let stopPromise = null;
  let cancelledSummary = null;
  const reloader = options.reloader ?? createReloader({
    bridge,
    tracker,
    clock,
    fs: io,
    user: undefined,
    findings,
    logTimeoutMs: options.reloadLogTimeoutMs,
  });

  const log = options.log ?? console.log;

  const images = {};
  let startState = null;
  let resolvedWorld = CASE_WORLD_CONNECTED;
  let resolvedEndWorld = CASE_WORLD_CONNECTED;
  let resolvedSite = null;
  let adminGranted = null;
  const stepsOut = [];
  let aborted = false;
  let abortReason = null;
  let ctx;
  let cleanup = { ok: false, remaining: [], unconfirmed: [], elapsedMs: 0, clientCarriers: { destroyed: [], remaining: [] } };
  let remaining = [];
  let unconfirmed = [];
  const finalChecks = [];
  let holdRestore = null;
  let adminRestoredDespiteUnconfirmed = false;
  let teardown = null;
  let teardownSteps = null;
  let teardownRestore = null;
  let teardownFailed = false;
  let teardownFailReason = null;
  let report = null;
  let session = null;
  let startLogSeq = -1;
  let expectedDetachLogs = { destroyedWithoutBeingDetached: 0, detachEntityInformation: 0 };
  let logSignals = emptyLogSignals({ sinceSeq: -1 });
  const gameInstall = options.gameInstall ?? DEFAULT_GAME_INSTALL;
  let startCollected = collectBuildStamps({ gameInstall, fs: io });
  let startAt = isoNow(clock);
  let buildStamps = stampsForReport(startCollected);

  function applyStamps(reportObj) {
    Object.assign(reportObj, buildStamps);
    return reportObj;
  }

  function emptyCancelledSummary() {
    return {
      requested: true,
      children: childReg.children,
      killed: childReg.killed,
      waitedMs: 0,
    };
  }

  function requestStop(reason = 'cancelled') {
    stopRequested = true;
    aborted = true;
    if (!abortReason) abortReason = reason;
    if (ctx) ctx.cancelled = true;
    refuseNewChildren({ children: childReg });
    if (typeof bridge.cancel === 'function') bridge.cancel(reason);
    if (!stopPromise) {
      stopPromise = cancelTrackedChildren({
        children: childReg,
        childKillGraceMs: options.childKillGraceMs,
        cancelled: true,
      }).then((summary) => {
        cancelledSummary = {
          requested: true,
          children: summary?.children ?? childReg.children,
          killed: summary?.killed ?? childReg.killed,
          waitedMs: summary?.waitedMs ?? 0,
        };
        return cancelledSummary;
      }).catch(() => {
        cancelledSummary = emptyCancelledSummary();
        return cancelledSummary;
      });
    }
    return stopPromise;
  }

  if (typeof options.registerStop === 'function') {
    options.registerStop(() => { void requestStop('cancelled'); });
  }

  function recordUnconfirmedRestorations() {
    const pending = restore.pending();
    for (const row of pending) {
      const msg = `restoration unconfirmed: ${row.item}`;
      if (!findings.includes(msg)) {
        findings.push(msg);
        log(msg);
      }
    }
    return pending;
  }

  async function confirmPendingRestorations() {
    const pending = restore.pending();
    if (!pending.length) return;
    const userEntity = adminGranted?.userEntity
      ?? startState?.admin?.userEntity
      ?? startState?.player?.userEntity;
    const userName = adminGranted?.userName ?? startState?.player?.name;
    for (const row of pending) {
      if (row.item === 'admin') {
        const expected = row.expect && Object.prototype.hasOwnProperty.call(row.expect, 'admin')
          ? row.expect.admin
          : false;
        let flag;
        try {
          const got = await bridge.call('server', 'get', {
            entity: userEntity,
            component: 'User',
          }, { bypassSuspect: true, bypassCancel: true });
          flag = unwrapAdminFlag(got?.fields?.IsAdmin);
        } catch { /* users readback below */ }
        if (flag !== expected) {
          try {
            const liveUsers = await bridge.call('server', 'users', {}, { bypassSuspect: true, bypassCancel: true });
            const userRow = (liveUsers?.users ?? []).find((u) => u.entity === userEntity)
              ?? (liveUsers?.users ?? []).find((u) => u.name === userName)
              ?? (liveUsers?.users ?? [])[0];
            if (userRow && Object.prototype.hasOwnProperty.call(userRow, 'admin')) {
              flag = unwrapAdminFlag(userRow.admin);
            }
          } catch { /* leave unconfirmed */ }
        }
        if (flag === expected) restore.observeAck('admin');
      }
      if (row.item === 'resolution') {
        try {
          const got = await bridge.call('client', 'mods_ui', { action: 'status' }, { bypassSuspect: true, bypassCancel: true });
          const after = got?.after ?? got;
          if (resolutionFieldsMatch(row.expect ?? {}, after ?? {})) restore.observeAck('resolution');
        } catch { /* leave unconfirmed */ }
        if (!restore.pending().some((r) => r === row || (r.item === 'resolution' && !r.ackObserved))) continue;
        try {
          const got = await bridge.call('client', 'mods_ui', { action: 'window' }, { bypassSuspect: true, bypassCancel: true });
          const after = got?.after ?? got;
          if (resolutionFieldsMatch(row.expect ?? {}, after ?? {})) restore.observeAck('resolution');
        } catch { /* leave unconfirmed */ }
      }
    }
  }

  async function restoreAdminIfGranted({ force = false } = {}) {
    await confirmPendingRestorations();
    const pending = recordUnconfirmedRestorations();
    const hadUnconfirmed = pending.length > 0;
    if (hadUnconfirmed && !force) {
      return adminGranted;
    }
    if (!adminGranted || adminGranted.restoredAt) return adminGranted;
    try {
      await bridge.call('server', 'set', {
        entity: adminGranted.userEntity,
        component: 'User',
        fields: { IsAdmin: false },
      }, { bypassSuspect: true, bypassCancel: true });
      adminGranted.restoredAt = new Date().toISOString();
      adminGranted.restored = true;
      if (hadUnconfirmed) {
        adminRestoredDespiteUnconfirmed = true;
        const msg = 'admin restored despite unconfirmed restoration';
        if (!findings.includes(msg)) {
          findings.push(msg);
          log(msg);
        }
      }
      log('admin: restored to the original value');
    } catch (e) {
      adminGranted.restored = false;
      adminGranted.restoreError = e && e.message ? e.message : String(e);
      log('admin: NOT restored');
    }
    return adminGranted;
  }

  function leftoverSummary() {
    const protection = finalChecks.find((c) => c.name === 'protection');
    const carriersCheck = finalChecks.find((c) => c.name === 'carriers');
    const leftoverCarriers = !!(carriersCheck && (
      !carriersCheck.ok
      || (carriersCheck.value?.leftover?.client?.length)
      || (carriersCheck.value?.leftover?.server?.length)
    ));
    return {
      trackedRemaining: remaining.length,
      unconfirmed: unconfirmed.length,
      carriersLeftover: leftoverCarriers,
      adminRestored: adminGranted ? !!adminGranted.restored : true,
      protectionOk: protection
        ? (!protection.unavailable && !!protection.ok)
        : resolvedEndWorld === CASE_WORLD_MAIN_MENU,
    };
  }

  function writeReport(status, reason) {
    const ended = new Date();
    report = {
      case: caseName,
      startedUtc: started.toISOString(),
      endedUtc: ended.toISOString(),
      images,
      startState,
      endWorld: resolvedEndWorld,
      steps: stepsOut,
      records,
      findings,
      cleanup: {
        ok: cleanup.ok,
        remaining,
        unconfirmed,
        elapsedMs: cleanup.elapsedMs,
        error: cleanup.error,
        entities: cleanup.entities ?? [],
        clientCarriers: cleanup.clientCarriers ?? { destroyed: [], remaining: [] },
      },
      adminGranted,
      holdRestore,
      finalChecks,
      finalChecksOrder: FINAL_CHECKS_ORDER,
      finalChecksNote: 'cleanupAll destroy requests run before these readbacks. Protection is not re-read before destroy.',
      leftoverSummary: leftoverSummary(),
      restore: restore.snapshot(),
      restorationUnconfirmed: restore.pending().map((r) => r.item),
      adminRestoredDespiteUnconfirmed,
      session,
      expectedDetachLogs,
      logSignals,
      status,
      reason,
    };
    if (stopRequested || cancelledSummary) {
      report.cancelled = cancelledSummary ?? emptyCancelledSummary();
    }
    applyStamps(report);
    if (teardown || teardownSteps || teardownRestore) {
      report.teardown = {
        ...(teardown && typeof teardown === 'object' ? teardown : {}),
        ...(teardownRestore ? { restore: teardownRestore } : {}),
        ...(teardownSteps ? { steps: teardownSteps } : {}),
      };
    }
    io.writeFileSync(path.join(runDir, 'report.json'), JSON.stringify(report, null, 2), 'utf8');
    return report;
  }

  function assignCtx(extra = {}) {
    ctx = {
      bridge,
      tracker,
      reloader,
      clock,
      fs: io,
      side: reloadSide,
      user: undefined,
      character: undefined,
      userEntity: undefined,
      position: undefined,
      records,
      findings,
      dll: options.dll,
      repoRoot: options.repoRoot ?? REPO_ROOT,
      spawnSync: options.spawnSync,
      spawn: options.spawn,
      stepName: '',
      playerIds: [],
      caseDoc,
      caseSite: resolvedSite ?? options.site ?? (caseDoc.site !== SITE_PLACEHOLDER ? caseDoc.site : undefined),
      outDir: runDir,
      children: childReg,
      restore,
      cancelled: stopRequested,
      childKillGraceMs: options.childKillGraceMs,
      ...extra,
    };
    if (stopRequested) ctx.cancelled = true;
    const innerSpawn = ctx.spawn;
    if (typeof innerSpawn === 'function') {
      ctx.spawn = (cmd, args, spawnOpts) => {
        if (stopRequested || childReg.refuseNew) return makeCancelledChild();
        const child = innerSpawn(cmd, args, spawnOpts);
        trackSpawnedChild({ children: childReg }, child, { cmd, args });
        if (stopRequested || childReg.refuseNew) {
          try { child?.kill?.('SIGTERM'); } catch { /* gone */ }
        }
        return child;
      };
    }
  }

  async function executeCaseSteps() {
    const caseSteps = caseDoc.steps ?? [];
    for (let i = 0; i < caseSteps.length; i++) {
      if (stopRequested || isRunCancelled(ctx)) {
        aborted = true;
        if (!abortReason) abortReason = 'cancelled';
        await requestStop(abortReason);
        break;
      }
      const step = applyOpenSitePlaceholder(caseSteps[i], resolvedSite);
      const name = stepName(step, i);
      ctx.stepName = name;
      const t0 = clock.now();
      const findingsAtStart = findings.length;
      let ok = false;
      let detail;
      try {
        detail = await executeStep(step, ctx);
        ok = detail?.ok !== false;
      } catch (e) {
        detail = { error: e.message };
        ok = false;
      }
      snapshotHostilesFindings(detail, findings, findingsAtStart);
      noteStepRestoration(step, detail, restore, ctx);
      const ms = clock.now() - t0;
      stepsOut.push({ name, ok, ms, detail });
      printLine(log, ok, name, ms, ok ? '' : (detail?.error ? `- ${detail.error}` : ''));
      if (!ok) {
        aborted = true;
        if (!stopRequested) abortReason = detail?.error ?? `${name} failed`;
        await requestStop(abortReason ?? detail?.error ?? `${name} failed`);
        break;
      }
    }
  }

  try {
    try {
      const pingClient = await bridge.call('client', 'ping');
      const pingServer = await bridge.call('server', 'ping');
      images.client = imageFromPing(pingClient);
      images.server = imageFromPing(pingServer);
      session = sessionPinFromPing(pingServer, clock.now());
      startLogSeq = pingServer?.logSeq ?? -1;
      startAt = isoNow(clock);
      startCollected = collectBuildStamps({
        pingServer,
        pingClient,
        gameInstall,
        fs: io,
      });
      buildStamps = stampsForReport(startCollected);

      const requestedWorld = caseRequestedWorld(caseDoc);
      const requestedEndWorld = caseEndWorld(caseDoc);
      const detectedWorld = detectWorldFromPing(pingClient);
      resolvedWorld = requestedWorld === CASE_WORLD_ANY ? detectedWorld : requestedWorld;
      resolvedEndWorld = requestedEndWorld ?? resolvedWorld;
      resolvedSite = resolveRunSite(options, detectedWorld, caseDoc);
      const startHashes = {
        sampleHash: images.server?.sampleHash ?? sampleHashFromPing(pingServer),
        clientSampleHash: images.client?.sampleHash ?? sampleHashFromPing(pingClient),
      };

      if (resolvedWorld === CASE_WORLD_MAIN_MENU) {
        startState = { world: CASE_WORLD_MAIN_MENU, site: resolvedSite, ...startHashes };
        if (pingHasClientGameWorld(pingClient)) {
          const worlds = pingWorldNames(pingClient).join(', ') || '(none)';
          aborted = true;
          abortReason = `client has a ${CLIENT_GAME_WORLD} world (${worlds}); this case requires the main menu (no ${CLIENT_GAME_WORLD})`;
          log(`FAIL start: ${abortReason}`);
        } else {
          assignCtx();
          await executeCaseSteps();
        }
      } else {
        const players = await bridge.call('server', 'players');
        const users = await bridge.call('server', 'users');
        const picked = pickPlayer(players, users);
        if (!picked) throw new Error('no player found; the rig needs a character to test against');
        const character = picked.player.entity;
        const userEntity = picked.player.userEntity;
        const userName = picked.player.name;
        const admin = picked.user ? picked.user.admin : undefined;
        const position = picked.player.position ?? { x: 0, y: 0, z: 0 };
        tracker.setForbidden([...playerOwnedIds(players, users)]);

        // Protection readback, rearm, and catalog commands are admin-only server chat.
        // Grant and restore always write User.IsAdmin on the SERVER user entity, even
        // when --side is client. Restore runs in finally on every path.
        if (admin === false && picked.user?.entity) {
          await bridge.call('server', 'set', { entity: picked.user.entity, component: 'User', fields: { IsAdmin: true } });
          adminGranted = {
            userEntity: picked.user.entity,
            userName,
            at: new Date().toISOString(),
          };
          log('admin: granted to the test user for this run (restored in finally)');
        }

        reloader.user = userName;
        reloader.character = character;
        // Protection is admin-only. Read it after the grant so a non-admin user
        // does not produce a false "no flags= line" baseline. Not-ok is a
        // precondition: rearm with .debug on and re-read before any step.
        const protectionStart = await protectionCheck({ bridge, user: userName, clock });
        let protectionState;
        if (protectionStart.ok) {
          protectionState = { initial: protectionStart, rearmed: false };
        } else {
          let after;
          try {
            after = await rearmProtection({ bridge, user: userName, clock });
          } catch (e) {
            after = {
              name: 'protection',
              ok: false,
              value: null,
              evidence: e && e.message ? e.message : String(e),
            };
          }
          protectionState = { initial: protectionStart, rearmed: true, after };
        }

        const carriersStart = await queryCarriers(bridge, position);

        startState = {
          world: CASE_WORLD_CONNECTED,
          site: resolvedSite,
          player: { entity: character, name: userName, userEntity, position, connected: picked.player.connected },
          admin: { name: userName, admin, userEntity: picked.user?.entity, userIndex: picked.user?.index },
          carriers: {
            client: {
              count: carriersStart.counts.client,
              ids: carriersStart.ids.client,
              total: carriersStart.totals?.client,
              truncated: carriersStart.truncated?.client === true,
            },
            server: {
              count: carriersStart.counts.server,
              ids: carriersStart.ids.server,
              total: carriersStart.totals?.server,
              truncated: carriersStart.truncated?.server === true,
            },
          },
          protection: protectionState,
          ...startHashes,
        };

        if (protectionState.rearmed && !protectionState.after?.ok) {
          aborted = true;
          abortReason = 'protection could not be armed';
          log(`FAIL start: ${abortReason}`);
        } else if (carriersStart.capReason) {
          aborted = true;
          abortReason = carriersStart.capReason;
          log(`FAIL start: ${abortReason}`);
        } else {
          assignCtx({
            user: userName,
            character,
            userEntity,
            position,
            playerIds: playerOwnedIds(players, users),
          });
          await executeCaseSteps();
        }
      }
    } catch (e) {
      aborted = true;
      abortReason = e.message;
      log(`FAIL start: ${e.message}`);
    }

    // Case teardown runs after the step loop (including abort/fail) and before
    // cleanupAll. Steps that need the plugin run only when it is loaded: a
    // leftover hold/removal is restored first (same fail-safe as hold restore).
    // A passing teardown cannot clear aborted; a failing teardown step makes
    // the run incomplete (never complete).
    if (ctx && Array.isArray(caseDoc.teardown) && caseDoc.teardown.length) {
      teardownSteps = [];
      ctx.teardown = true;
      try {
        let needsRestore = lastReloadWasRemovalWithoutReinstall(stepsOut);
        if (!needsRestore && typeof reloader.sampleReloadState === 'function') {
          try {
            const state = await reloader.sampleReloadState(reloadSide);
            needsRestore = !!(state.holdPresent || !state.samplePresent);
          } catch {
            /* ping/folder inspect failed; last-reload check already applied */
          }
        }
        if (needsRestore) {
          const t0 = clock.now();
          try {
            const restored = await reloader.restoreHoldIfPresent(reloadSide);
            teardownRestore = restored
              ? { ...restored, elapsedMs: clock.now() - t0 }
              : {
                ok: false,
                error: 'plugin on hold or removed but held Sample DLL was not found',
                elapsedMs: clock.now() - t0,
              };
            log(`teardown restore: ${teardownRestore.ok === false ? 'FAIL' : 'ok'}`);
          } catch (e) {
            teardownRestore = {
              ok: false,
              error: e && e.message ? e.message : String(e),
              elapsedMs: clock.now() - t0,
            };
            log(`teardown restore: FAIL ${teardownRestore.error}`);
          }
        }

        for (let i = 0; i < caseDoc.teardown.length; i++) {
          const step = applyOpenSitePlaceholder(caseDoc.teardown[i], resolvedSite);
          const name = stepName(step, i);
          ctx.stepName = `teardown ${name}`;
          const t0 = clock.now();
          const findingsAtStart = findings.length;
          let ok = false;
          let detail;
          try {
            detail = await executeStep(step, ctx);
            ok = detail?.ok !== false;
          } catch (e) {
            detail = { error: e && e.message ? e.message : String(e) };
            ok = false;
          }
          snapshotHostilesFindings(detail, findings, findingsAtStart);
          noteStepRestoration(step, detail, restore, ctx);
          const ms = clock.now() - t0;
          teardownSteps.push({ name, ok, ms, detail });
          printLine(log, ok, `teardown ${name}`, ms, ok ? '' : (detail?.error ? `- ${detail.error}` : ''));
          if (!ok) {
            teardownFailed = true;
            if (!teardownFailReason) {
              teardownFailReason = detail?.error
                ? `teardown ${name} failed: ${detail.error}`
                : `teardown ${name} failed`;
            }
          }
        }
      } finally {
        ctx.teardown = false;
      }
    }

    try {
      cleanup = await tracker.cleanupAll();
    } catch (e) {
      aborted = true;
      abortReason = e.message;
      try {
        const sweep = await tracker.existsSweep();
        cleanup = {
          ok: false,
          remaining: sweep.remaining,
          unconfirmed: sweep.unconfirmed,
          elapsedMs: 0,
          error: e.message,
        };
      } catch (e2) {
        cleanup = {
          ok: false,
          remaining: [],
          unconfirmed: tracker.list().map((row) => ({ ...row, error: e.message })),
          elapsedMs: 0,
          error: e.message,
        };
      }
      log(`cleanup: FAIL ${e.message}`);
    }

    cleanup.clientCarriers = { destroyed: [], remaining: [] };
    if (resolvedEndWorld !== CASE_WORLD_MAIN_MENU && startState?.player?.position) {
      try {
        cleanup.clientCarriers = await cleanupClientCarriers({
          bridge,
          tracker,
          position: startState.player.position,
          baselineIds: startState.carriers?.client?.ids ?? [],
          findings,
        });
        const cc = cleanup.clientCarriers;
        if ((cc.destroyed?.length || cc.remaining?.length)) {
          log(`client carriers: destroyed ${(cc.destroyed ?? []).length} remaining ${(cc.remaining ?? []).length}`);
        }
      } catch (e) {
        cleanup.clientCarriers = {
          destroyed: [],
          remaining: [],
          error: e && e.message ? e.message : String(e),
        };
        log(`client carriers: FAIL ${cleanup.clientCarriers.error}`);
      }
    }

    remaining = tracker.formatRemaining(cleanup.remaining ?? []);
    unconfirmed = tracker.formatUnconfirmed(cleanup.unconfirmed ?? []);
    if (cleanup.ok) {
      log(`cleanup: ok (${cleanup.elapsedMs}ms)`);
    } else if (!cleanup.error) {
      log(`cleanup: FAIL remaining ${remaining.length} unconfirmed ${unconfirmed.length} after ${cleanup.elapsedMs}ms`);
      for (const row of formatRemaining(remaining)) log(`  ${row}`);
      for (const row of formatUnconfirmedLine(unconfirmed)) log(`  unconfirmed ${row}`);
    } else {
      log(`cleanup: FAIL ${cleanup.error}`);
      if (unconfirmed.length) {
        for (const row of formatUnconfirmedLine(unconfirmed)) log(`  unconfirmed ${row}`);
      }
    }

    try {
      holdRestore = await reloader.restoreHoldIfPresent(reloadSide);
      if (holdRestore) log(`hold restore: ${holdRestore.ok === false ? 'FAIL' : 'ok'}`);
    } catch (e) {
      holdRestore = { ok: false, error: e.message };
      log(`hold restore: FAIL ${e.message}`);
    }

    if (startState) {
      try {
        // Confirm case-level restoration acks (admin get/users, resolution
        // window/status) before the protection re-grant overwrites admin.
        await confirmPendingRestorations();
        // `.debug status` is admin-only. Re-grant explicitly for this one
        // readback on every path (case dropped admin, teardown restored, or
        // the run-level grant was already restored). Logged; never silent.
        // beforeAdminState then restores again so adminState is the original value.
        // Final checks follow endWorld when the case ends in a different world
        // than it started (leave to menu, or join from the menu).
        let checkPlayer = startState.player;
        let checkAdmin = startState.admin;
        let checkCarriers = startState.carriers;
        if (resolvedEndWorld === CASE_WORLD_CONNECTED && !checkPlayer) {
          const players = await bridge.call('server', 'players', {}, { bypassSuspect: true, bypassCancel: true });
          const users = await bridge.call('server', 'users', {}, { bypassSuspect: true, bypassCancel: true });
          const picked = pickPlayer(players, users);
          if (!picked) throw new Error('no player found; the rig needs a character to test against');
          checkPlayer = {
            entity: picked.player.entity,
            name: picked.player.name,
            userEntity: picked.player.userEntity,
            position: picked.player.position ?? { x: 0, y: 0, z: 0 },
            connected: picked.player.connected,
          };
          checkAdmin = {
            name: picked.player.name,
            admin: picked.user ? picked.user.admin : undefined,
            userEntity: picked.user?.entity,
            userIndex: picked.user?.index,
          };
        }
        if (resolvedEndWorld === CASE_WORLD_CONNECTED && !checkCarriers && checkPlayer?.position) {
          const snap = await queryCarriers(bridge, checkPlayer.position);
          checkCarriers = {
            client: {
              count: snap.counts.client,
              ids: snap.ids.client,
              total: snap.totals?.client,
              truncated: snap.truncated?.client === true,
            },
            server: {
              count: snap.counts.server,
              ids: snap.ids.server,
              total: snap.totals?.server,
              truncated: snap.truncated?.server === true,
            },
          };
        }
        if (resolvedEndWorld !== CASE_WORLD_MAIN_MENU) {
          const entity = adminGranted?.userEntity
            ?? checkAdmin?.userEntity
            ?? checkPlayer?.userEntity;
          const originalAdmin = checkAdmin?.admin;
          const mayElevate = !!adminGranted || originalAdmin === true;
          if (entity && mayElevate) {
            try {
              const liveUsers = await bridge.call('server', 'users', {}, { bypassSuspect: true, bypassCancel: true });
              const row = (liveUsers?.users ?? []).find((u) => u.entity === entity)
                ?? (liveUsers?.users ?? []).find((u) => u.name === (adminGranted?.userName ?? checkPlayer?.name));
              if (row?.admin !== true) {
                await bridge.call('server', 'set', {
                  entity,
                  component: 'User',
                  fields: { IsAdmin: true },
                }, { bypassSuspect: true, bypassCancel: true });
                if (adminGranted) {
                  adminGranted.regrantedForProtection = true;
                  adminGranted.restoredAt = undefined;
                  adminGranted.restored = false;
                }
                log('admin: re-granted for protection readback');
              }
            } catch {
              /* protection may fail; restore still runs */
            }
          }
        }
        const checks = await runFinalChecks({
          bridge,
          tracker,
          side: 'server',
          world: resolvedEndWorld,
          user: checkPlayer?.name,
          character: checkPlayer?.entity,
          clock,
          expectedAdmin: checkAdmin?.admin,
          beforeAdminState: () => restoreAdminIfGranted({ force: false }),
          userName: checkPlayer?.name,
          position: checkPlayer?.position,
          expected: checkCarriers,
        });
        for (const c of checks) {
          if (c.name === 'protection' && !c.ok && !c.unavailable
            && /no debug status line/i.test(String(c.evidence ?? ''))) {
            try {
              const entity = adminGranted?.userEntity
                ?? checkAdmin?.userEntity
                ?? checkPlayer?.userEntity;
              const liveUsers = await bridge.call('server', 'users', {}, { bypassSuspect: true, bypassCancel: true });
              const row = (liveUsers?.users ?? []).find((u) => u.entity === entity)
                ?? (liveUsers?.users ?? []).find((u) => u.name === (checkPlayer?.name ?? startState.player?.name));
              if (row?.admin !== true) {
                c.unavailable = true;
                c.value = null;
                c.evidence = PROTECTION_UNAVAILABLE;
              }
            } catch {
              /* leave as a failed parse */
            }
          }
          finalChecks.push(c);
          if (c.unavailable) log(`${c.name}: ${c.evidence || PROTECTION_UNAVAILABLE}`);
          else log(`${c.name}: ${c.ok ? 'PASS' : 'FAIL'}`);
        }
      } catch (e) {
        aborted = true;
        abortReason = e.message;
        finalChecks.push({ name: 'finalChecks', ok: false, value: null, evidence: e.message });
        log(`FAIL finalChecks: ${e.message}`);
      }
    } else {
      log('final checks skipped (run did not record start state)');
    }

    if (options.forceTeardown) {
      // Only this run's skipCleanup entities. Previous-run leftovers are not
      // in this tracker; use `cleanup <runDir>` for those.
      teardown = await tracker.teardownSkipped();
      teardown.remaining = tracker.formatRemaining(teardown.remaining);
      teardown.unconfirmed = tracker.formatUnconfirmed(teardown.unconfirmed ?? []);
      log(`teardown: ${teardown.ok ? 'ok' : 'FAIL'} remaining ${(teardown.remaining ?? []).length} after ${teardown.elapsedMs}ms`);
    }
  } catch (e) {
    aborted = true;
    abortReason = e.message;
    log(`FAIL: ${e.message}`);
    await requestStop(abortReason);
  } finally {
    if (stopPromise) {
      try { await stopPromise; } catch { /* already recorded */ }
    }
    try {
      await restoreAdminIfGranted({ force: true });
    } catch (e) {
      if (adminGranted) {
        adminGranted.restored = false;
        adminGranted.restoreError = e && e.message ? e.message : String(e);
      }
      log('admin: NOT restored');
    }

    try {
      const scanned = await scanServerLogSignals(bridge, { since: startLogSeq });
      expectedDetachLogs = scanned.expectedDetachLogs;
      logSignals = scanned.logSignals;
    } catch (e) {
      logSignals = emptyLogSignals({
        scanned: false,
        sinceSeq: startLogSeq,
        reason: e && e.message ? e.message : String(e),
        error: e && e.message ? e.message : String(e),
      });
      logSignals.fatal.bridgeUnanswered = 1;
      expectedDetachLogs = { ...logSignals.expected };
    }

    if (stopPromise) {
      try { await stopPromise; } catch { /* summary already recorded */ }
    }

    const checksOk = finalChecks.length > 0 && finalChecks.every(finalCheckCountsAsPass);
    const restoreUnconfirmed = restore.pending().map((r) => `restoration unconfirmed: ${r.item}`);
    let status;
    let reason;
    if (aborted) {
      status = 'aborted';
      reason = abortReason ?? 'step failed';
    } else if (teardownFailed) {
      status = 'incomplete';
      reason = teardownFailReason ?? 'teardown failed';
    } else if (!cleanup.ok || !checksOk) {
      status = 'incomplete';
      const parts = [];
      if (!cleanup.ok) {
        if (remaining.length) parts.push(`cleanup left ${remaining.length} entit${remaining.length === 1 ? 'y' : 'ies'}`);
        if (unconfirmed.length) parts.push(`${unconfirmed.length} unconfirmed`);
        if (!remaining.length && !unconfirmed.length) parts.push('cleanup failed');
      }
      const failed = finalChecks.filter((c) => !finalCheckCountsAsPass(c)).map((c) => c.name);
      if (failed.length) parts.push(`checks: ${failed.join(', ')}`);
      reason = parts.join('; ') || 'incomplete';
    } else {
      status = 'complete';
      reason = 'all steps, cleanup and checks passed';
    }

    if (restoreUnconfirmed.length) {
      for (const msg of restoreUnconfirmed) {
        if (!findings.includes(msg)) findings.push(msg);
      }
      if (status === 'complete') {
        status = 'incomplete';
        reason = restoreUnconfirmed.join('; ');
      } else if (status === 'incomplete') {
        reason = [reason, ...restoreUnconfirmed].filter(Boolean).join('; ');
      }
    }

    if (status !== 'aborted' && hasFatalLogSignals(logSignals)) {
      status = 'incomplete';
      reason = logSignals.reason ?? 'fatal log signal';
    }

    if (status === 'complete' && (remaining.length || unconfirmed.length)) {
      status = 'incomplete';
      const parts = [];
      if (remaining.length) parts.push(`cleanup left ${remaining.length} entit${remaining.length === 1 ? 'y' : 'ies'}`);
      if (unconfirmed.length) parts.push(`${unconfirmed.length} unconfirmed`);
      reason = parts.join('; ');
    }

    if (adminGranted && !adminGranted.restoredAt) {
      if (status === 'complete') {
        status = 'aborted';
        reason = 'admin grant was not restored';
      }
      if (!adminGranted.restoreError) log('admin: NOT restored');
    }

    const pingClientFinal = await pingOrNull(bridge, 'client');
    const pingServerFinal = await pingOrNull(bridge, 'server');
    const finalCollected = collectBuildStamps({
      pingServer: pingServerFinal,
      pingClient: pingClientFinal,
      gameInstall,
      fs: io,
    });
    buildStamps = applyStampPhases(startCollected, finalCollected, startAt, isoNow(clock), findings);

    try {
      writeReport(status, reason);
    } catch (e) {
      log(`FAIL report: ${e.message}`);
      if (!report) {
        report = {
          case: caseName,
          startedUtc: started.toISOString(),
          endedUtc: new Date().toISOString(),
          images,
          startState,
          endWorld: resolvedEndWorld,
          steps: stepsOut,
          records,
          findings,
          cleanup: { ok: cleanup.ok, remaining, unconfirmed, elapsedMs: cleanup.elapsedMs, error: cleanup.error, entities: cleanup.entities ?? [], clientCarriers: cleanup.clientCarriers ?? { destroyed: [], remaining: [] } },
          adminGranted,
          holdRestore,
          finalChecks,
          finalChecksOrder: FINAL_CHECKS_ORDER,
          leftoverSummary: leftoverSummary(),
          restore: restore.snapshot(),
          restorationUnconfirmed: restore.pending().map((r) => r.item),
          adminRestoredDespiteUnconfirmed,
          session,
          expectedDetachLogs,
          logSignals,
          status: 'aborted',
          reason: e.message,
        };
        if (stopRequested || cancelledSummary) {
          report.cancelled = cancelledSummary ?? emptyCancelledSummary();
        }
        applyStamps(report);
        try {
          io.writeFileSync(path.join(runDir, 'report.json'), JSON.stringify(report, null, 2), 'utf8');
        } catch {
          /* last resort: caller still receives the in-memory report */
        }
      }
    }

    if (report.status === 'complete') {
      log(`status: complete`);
    } else {
      log(`status: ${report.status} (${report.reason})`);
      if (remaining.length) {
        log('remaining entities:');
        for (const row of formatRemaining(remaining)) log(`  ${row}`);
      }
      if (unconfirmed.length) {
        log('unconfirmed entities:');
        for (const row of formatUnconfirmedLine(unconfirmed)) log(`  ${row}`);
      }
    }
    log(`report: ${path.join(runDir, 'report.json')}`);
  }

  return { report, runDir };
}

/**
 * Adopt leftovers listed in a previous run's report.json (remaining and unconfirmed,
 * plus teardown.* when present, plus leftover client carriers), destroy them, and
 * confirm absence. Client leftovers are adopted on the client side. Status is
 * complete only when remaining and unconfirmed are empty. An empty adoption set
 * is "nothing to clean" / complete only when the previous report itself listed
 * none — including `cleanup.clientCarriers.remaining`. Session pin and prefab
 * must match before destroy.
 */
export async function cleanupLeftovers(previousRunDir, options = {}) {
  const clock = options.clock ?? realClock();
  const io = options.fs ?? fs;
  const log = options.log ?? console.log;
  const started = new Date();
  const outRoot = options.outDir ?? DEFAULT_OUT_DIR;
  const resolvedPrev = path.resolve(previousRunDir);
  const reportPath = path.join(resolvedPrev, 'report.json');
  if (!io.existsSync(reportPath)) throw new Error(`no report.json in ${previousRunDir}`);
  const prev = JSON.parse(io.readFileSync(reportPath, 'utf8'));
  const leftovers = leftoverEntitiesFromReport(prev);
  const listedCount = sourceReportListedLeftoverCount(prev);
  const runDir = options.runDir ?? path.join(outRoot, `${utcStamp(started)}-cleanup`);
  io.mkdirSync(runDir, { recursive: true });

  const bridge = options.bridge ?? createBridge({
    runDir,
    transport: options.transport,
    sides: options.sides,
    timeoutMs: options.bridgeTimeoutMs,
  });
  const findings = [];
  const tracker = options.tracker ?? createTracker({
    bridge,
    clock,
    side: 'server',
    timeoutMs: options.cleanupTimeoutMs ?? DEFAULT_CLEANUP_TIMEOUT_MS,
    pollMs: options.cleanupPollMs ?? DEFAULT_CLEANUP_POLL_MS,
    finalizeTimeoutMs: options.finalizeTimeoutMs ?? DEFAULT_FINALIZE_TIMEOUT_MS,
    finalizePollMs: options.finalizePollMs ?? DEFAULT_FINALIZE_POLL_MS,
    attachReadback: options.attachReadback,
    findings,
  });

  let cleanup = { ok: false, remaining: [], unconfirmed: [], elapsedMs: 0 };
  let remaining = [];
  let unconfirmed = [];
  let status = 'aborted';
  let reason = 'cleanup did not finish';
  const adopted = [];
  const refusals = [];
  let session = null;
  let sessionMatch = null;
  const gameInstall = options.gameInstall ?? DEFAULT_GAME_INSTALL;
  let startCollected = collectBuildStamps({ gameInstall, fs: io });
  let startAt = isoNow(clock);
  let buildStamps = stampsForReport(startCollected);

  try {
    try {
      const players = await bridge.call('server', 'players');
      const users = await bridge.call('server', 'users');
      tracker.setForbidden([...playerOwnedIds(players, users)]);
    } catch {
      /* still try to destroy listed leftovers */
    }

    const pingServer = await pingOrNull(bridge, 'server');
    const pingClient = await pingOrNull(bridge, 'client');
    if (pingServer) session = sessionPinFromPing(pingServer, clock.now());
    else session = null;
    startAt = isoNow(clock);
    startCollected = collectBuildStamps({ pingServer, pingClient, gameInstall, fs: io });
    buildStamps = stampsForReport(startCollected);

    if (!leftovers.length) {
      if (listedCount === 0) {
        cleanup = { ok: true, remaining: [], unconfirmed: [], elapsedMs: 0 };
        remaining = [];
        unconfirmed = [];
        status = 'complete';
        reason = 'nothing to clean';
        log('cleanup: ok (nothing to clean)');
      } else {
        cleanup = { ok: false, remaining: [], unconfirmed: [], elapsedMs: 0 };
        status = 'incomplete';
        reason = 'previous leftovers could not be adopted';
        findings.push(reason);
        log(`cleanup: FAIL ${reason}`);
      }
    } else {
      sessionMatch = sessionPinsMatch(prev.session ?? null, session);
      if (!sessionMatch) {
        refusals.push({
          reason: 'session pin mismatch',
          previous: prev.session ?? null,
          current: session,
        });
        findings.push('session pin mismatch');
        remaining = leftovers.map(formatLeftoverRow);
        unconfirmed = [];
        cleanup = { ok: false, remaining, unconfirmed, elapsedMs: 0 };
        status = 'incomplete';
        reason = 'session pin mismatch';
        log('cleanup: FAIL session pin mismatch');
        for (const row of formatRemaining(remaining)) log(`  ${row}`);
      } else {
        const filtered = await refusePrefabMismatches(bridge, leftovers);
        for (const row of filtered.refusals) {
          refusals.push(row);
          findings.push(`refused ${row.entity}: prefab mismatch`);
        }

        const keptClient = [];
        for (const row of filtered.accepted) {
          if (!shouldDestroyLeftover(row)) {
            keptClient.push(formatLeftoverRow(row));
            continue;
          }
          const name = uniqueTrackedName(tracker, row.name || 'leftover');
          try {
            const adoptedRow = tracker.adopt({
              name,
              entity: row.entity,
              prefab: row.prefab ?? null,
              side: row.side ?? 'server',
              by: 'cleanup-leftover',
              hostile: !!row.hostile,
            });
            adopted.push(adoptedRow);
          } catch (e) {
            unconfirmed.push({
              name,
              side: row.side ?? 'server',
              entity: row.entity,
              prefab: row.prefab ?? null,
              hostile: !!row.hostile,
              skipCleanup: false,
              error: e.message,
            });
            findings.push(`refused ${row.entity}: ${e.message}`);
          }
        }

        cleanup = await tracker.cleanupAll({ includeSkipped: true });
        remaining = [
          ...tracker.formatRemaining(cleanup.remaining ?? []),
          ...filtered.refusals.map((row) => formatLeftoverRow({
            name: row.name || 'leftover',
            entity: row.entity,
            prefab: row.recordedPrefab ?? null,
            hostile: row.hostile,
            side: row.side ?? 'server',
          })),
          ...keptClient,
        ];
        unconfirmed = [...unconfirmed, ...tracker.formatUnconfirmed(cleanup.unconfirmed ?? [])];
        cleanup.ok = remaining.length === 0 && unconfirmed.length === 0;
        if (cleanup.ok) {
          status = 'complete';
          reason = 'previous leftovers destroyed and confirmed absent';
          log(`cleanup: ok (${cleanup.elapsedMs}ms)`);
        } else {
          status = 'incomplete';
          const parts = [];
          if (remaining.length) parts.push(`left ${remaining.length}`);
          if (unconfirmed.length) parts.push(`${unconfirmed.length} unconfirmed`);
          if (filtered.refusals.length) parts.push(`${filtered.refusals.length} prefab mismatch`);
          reason = parts.join('; ') || 'cleanup incomplete';
          log(`cleanup: FAIL ${reason} after ${cleanup.elapsedMs}ms`);
          for (const row of formatRemaining(remaining)) log(`  ${row}`);
          for (const row of formatUnconfirmedLine(unconfirmed)) log(`  unconfirmed ${row}`);
        }
      }
    }
  } catch (e) {
    status = 'aborted';
    reason = e.message;
    try {
      const sweep = await tracker.existsSweep();
      remaining = tracker.formatRemaining(sweep.remaining ?? []);
      unconfirmed = [...unconfirmed, ...tracker.formatUnconfirmed(sweep.unconfirmed ?? [])];
    } catch {
      unconfirmed = [
        ...unconfirmed,
        ...tracker.formatUnconfirmed(tracker.list().map((row) => ({ ...row, error: e.message }))),
      ];
      remaining = [];
    }
    cleanup = { ok: false, remaining, unconfirmed, elapsedMs: cleanup.elapsedMs ?? 0, error: e.message };
    log(`cleanup: FAIL ${e.message}`);
  }

  const pingServerFinal = await pingOrNull(bridge, 'server');
  const pingClientFinal = await pingOrNull(bridge, 'client');
  const finalCollected = collectBuildStamps({
    pingServer: pingServerFinal,
    pingClient: pingClientFinal,
    gameInstall,
    fs: io,
  });
  buildStamps = applyStampPhases(startCollected, finalCollected, startAt, isoNow(clock), findings);

  const report = {
    case: 'cleanup',
    sourceRun: resolvedPrev,
    startedUtc: started.toISOString(),
    endedUtc: new Date().toISOString(),
    ...buildStamps,
    session,
    sessionMatch,
    adopted: adopted.map((e) => ({ name: e.name, entity: e.entity, prefab: e.prefab, hostile: !!e.hostile, side: e.side ?? 'server' })),
    refusals,
    findings,
    cleanup: { ok: cleanup.ok, remaining, unconfirmed, elapsedMs: cleanup.elapsedMs, error: cleanup.error, entities: cleanup.entities ?? [] },
    leftoverSummary: {
      trackedRemaining: remaining.length,
      unconfirmed: unconfirmed.length,
      carriersLeftover: false,
      adminRestored: true,
      protectionOk: false,
    },
    status,
    reason,
  };
  io.writeFileSync(path.join(runDir, 'report.json'), JSON.stringify(report, null, 2), 'utf8');
  log(`status: ${report.status}${report.status === 'complete' ? '' : ` (${report.reason})`}`);
  log(`report: ${path.join(runDir, 'report.json')}`);
  return { report, runDir };
}

/**
 * Adopt one leftover entity by id, then destroy and confirm absence. Prefab
 * (when given) must match the exists readback. Player/user ids from
 * players/users are refused without an entity request; CHAR_Vampire* prefabs
 * are refused after exists. Session is pinned from the current server ping
 * onto the report so a later `cleanup <runDir>` can match it.
 */
export async function cleanupEntity(entity, options = {}) {
  const clock = options.clock ?? realClock();
  const io = options.fs ?? fs;
  const log = options.log ?? console.log;
  const started = new Date();
  const outRoot = options.outDir ?? DEFAULT_OUT_DIR;
  const expectedPrefab = options.prefab;
  const id = String(entity ?? '');
  const runDir = options.runDir ?? path.join(outRoot, `${utcStamp(started)}-cleanup`);
  io.mkdirSync(runDir, { recursive: true });

  const bridge = options.bridge ?? createBridge({
    runDir,
    transport: options.transport,
    sides: options.sides,
    timeoutMs: options.bridgeTimeoutMs,
  });
  const findings = [];
  const tracker = options.tracker ?? createTracker({
    bridge,
    clock,
    side: 'server',
    timeoutMs: options.cleanupTimeoutMs ?? DEFAULT_CLEANUP_TIMEOUT_MS,
    pollMs: options.cleanupPollMs ?? DEFAULT_CLEANUP_POLL_MS,
    finalizeTimeoutMs: options.finalizeTimeoutMs ?? DEFAULT_FINALIZE_TIMEOUT_MS,
    finalizePollMs: options.finalizePollMs ?? DEFAULT_FINALIZE_POLL_MS,
    attachReadback: options.attachReadback !== undefined
      ? options.attachReadback
      : (/thief/i.test(String(expectedPrefab ?? '')) ? true : undefined),
    findings,
  });

  let cleanup = { ok: false, remaining: [], unconfirmed: [], elapsedMs: 0 };
  let remaining = [];
  let unconfirmed = [];
  let status = 'aborted';
  let reason = 'cleanup did not finish';
  const adopted = [];
  const refusals = [];
  let session = null;
  let owned = new Set();
  const gameInstall = options.gameInstall ?? DEFAULT_GAME_INSTALL;
  let startCollected = collectBuildStamps({ gameInstall, fs: io });
  let startAt = isoNow(clock);
  let buildStamps = stampsForReport(startCollected);

  function writeCleanupReport() {
    buildStamps = applyStampPhases(startCollected, startCollected, startAt, startAt, findings);
    const report = {
      case: 'cleanup',
      sourceEntity: id,
      expectedPrefab: expectedPrefab ?? null,
      startedUtc: started.toISOString(),
      endedUtc: new Date().toISOString(),
      ...buildStamps,
      session,
      adopted: adopted.map((e) => ({ name: e.name, entity: e.entity, prefab: e.prefab, hostile: !!e.hostile, side: e.side ?? 'server' })),
      refusals,
      findings,
      cleanup: { ok: cleanup.ok, remaining, unconfirmed, elapsedMs: cleanup.elapsedMs, error: cleanup.error, entities: cleanup.entities ?? [] },
      leftoverSummary: {
        trackedRemaining: remaining.length,
        unconfirmed: unconfirmed.length,
        carriersLeftover: false,
        adminRestored: true,
        protectionOk: false,
      },
      status,
      reason,
    };
    io.writeFileSync(path.join(runDir, 'report.json'), JSON.stringify(report, null, 2), 'utf8');
    log(`status: ${report.status}${report.status === 'complete' ? '' : ` (${report.reason})`}`);
    log(`report: ${path.join(runDir, 'report.json')}`);
    return report;
  }

  try {
    if (!/^\d+:\d+$/.test(id)) {
      status = 'aborted';
      reason = `entity id must be index:version, got ${JSON.stringify(entity)}`;
      findings.push(reason);
      log(`cleanup: FAIL ${reason}`);
      return { report: writeCleanupReport(), runDir };
    }

    try {
      const players = await bridge.call('server', 'players');
      const users = await bridge.call('server', 'users');
      owned = playerOwnedIds(players, users);
      tracker.setForbidden([...owned]);
    } catch {
      /* still try the leftover unless it is already known as a player id */
    }

    const pingServer = await pingOrNull(bridge, 'server');
    const pingClient = await pingOrNull(bridge, 'client');
    if (pingServer) session = sessionPinFromPing(pingServer, clock.now());
    else session = null;
    startAt = isoNow(clock);
    startCollected = collectBuildStamps({ pingServer, pingClient, gameInstall, fs: io });
    buildStamps = stampsForReport(startCollected);

    if (owned.has(id)) {
      refusals.push({ entity: id, reason: 'player or user entity' });
      findings.push(`refused ${id}: player or user entity`);
      status = 'aborted';
      reason = 'refusing player or user entity';
      log(`cleanup: FAIL ${reason}`);
      return { report: writeCleanupReport(), runDir };
    }

    let parsed;
    try {
      parsed = parseResult(await bridge.call('server', 'entity', existsRequest([id])));
    } catch (e) {
      status = 'aborted';
      reason = e && e.message ? e.message : String(e);
      findings.push(reason);
      unconfirmed = [{
        name: 'leftover',
        side: 'server',
        entity: id,
        prefab: expectedPrefab ?? null,
        hostile: false,
        skipCleanup: false,
        error: reason,
      }];
      cleanup = { ok: false, remaining: [], unconfirmed, elapsedMs: 0, error: reason };
      log(`cleanup: FAIL ${reason}`);
      return { report: writeCleanupReport(), runDir };
    }

    const hit = (parsed.entries ?? []).find((e) => e.entity === id) ?? parsed.entries[0];
    if (!hit || hit.exists !== true) {
      findings.push(`entity ${id} does not exist`);
      status = 'aborted';
      reason = 'entity does not exist';
      log(`cleanup: FAIL ${reason}`);
      return { report: writeCleanupReport(), runDir };
    }

    if (isDeniedPlayerPrefab(hit.prefab)) {
      refusals.push({
        entity: id,
        reason: 'player prefab',
        livePrefab: hit.prefab ?? null,
      });
      findings.push(`refused ${id}: player prefab`);
      status = 'aborted';
      reason = 'refusing player or user entity';
      log(`cleanup: FAIL ${reason}`);
      return { report: writeCleanupReport(), runDir };
    }

    if (expectedPrefab != null && expectedPrefab !== '' && !prefabsMatch(expectedPrefab, hit.prefab)) {
      refusals.push({
        entity: id,
        reason: 'prefab mismatch',
        recordedPrefab: expectedPrefab,
        livePrefab: hit.prefab ?? null,
      });
      findings.push(`refused ${id}: prefab mismatch`);
      remaining = [formatLeftoverRow({
        name: 'leftover',
        entity: id,
        prefab: expectedPrefab,
        hostile: false,
        side: 'server',
      })];
      cleanup = { ok: false, remaining, unconfirmed: [], elapsedMs: 0 };
      status = 'incomplete';
      reason = 'prefab mismatch';
      log(`cleanup: FAIL ${reason}`);
      return { report: writeCleanupReport(), runDir };
    }

    const name = uniqueTrackedName(tracker, options.name || 'leftover');
    const adoptedRow = tracker.adopt({
      name,
      entity: id,
      prefab: expectedPrefab ?? prefabDisplayName(hit.prefab) ?? hit.prefab ?? null,
      side: 'server',
      by: 'cleanup-entity',
      hostile: false,
    });
    adopted.push(adoptedRow);

    cleanup = await tracker.cleanupAll({ includeSkipped: true });
    remaining = tracker.formatRemaining(cleanup.remaining ?? []);
    unconfirmed = tracker.formatUnconfirmed(cleanup.unconfirmed ?? []);
    cleanup.ok = remaining.length === 0 && unconfirmed.length === 0;
    if (cleanup.ok) {
      status = 'complete';
      reason = 'entity destroyed and confirmed absent';
      log(`cleanup: ok (${cleanup.elapsedMs}ms)`);
    } else {
      status = 'incomplete';
      const parts = [];
      if (remaining.length) parts.push(`left ${remaining.length}`);
      if (unconfirmed.length) parts.push(`${unconfirmed.length} unconfirmed`);
      reason = parts.join('; ') || 'cleanup incomplete';
      log(`cleanup: FAIL ${reason} after ${cleanup.elapsedMs}ms`);
      for (const row of formatRemaining(remaining)) log(`  ${row}`);
      for (const row of formatUnconfirmedLine(unconfirmed)) log(`  unconfirmed ${row}`);
    }
  } catch (e) {
    status = 'aborted';
    reason = e.message;
    try {
      const sweep = await tracker.existsSweep();
      remaining = tracker.formatRemaining(sweep.remaining ?? []);
      unconfirmed = [...unconfirmed, ...tracker.formatUnconfirmed(sweep.unconfirmed ?? [])];
    } catch {
      unconfirmed = [
        ...unconfirmed,
        ...tracker.formatUnconfirmed(tracker.list().map((row) => ({ ...row, error: e.message }))),
      ];
      remaining = [];
    }
    cleanup = { ok: false, remaining, unconfirmed, elapsedMs: cleanup.elapsedMs ?? 0, error: e.message };
    log(`cleanup: FAIL ${e.message}`);
  }

  return { report: writeCleanupReport(), runDir };
}
