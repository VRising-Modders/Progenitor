import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { existsRequest, parseResult } from './entityOp.mjs';
import { protection } from './checks.mjs';
import { readLatestSeq, waitForLogs, waitForLatestLog, lineMatches } from './logs.mjs';
import { isLiveServerPath, isBridgeDownError } from './bridge.mjs';
import { isDeniedPlayerPrefab, prefabDisplayName } from './tracker.mjs';
import {
  parsePerfSpec,
  readPerfStatus,
  latencyTable,
  pingLatencies,
  perfStatusLatencies,
  sampleGapStats,
  responsivenessSummary,
  windowEntry,
  gapEntry,
  freezeOverlapsInstant,
  applyFreezeCorrelation,
} from './perfWindows.mjs';

const SCRATCH_IMAGE_RE = /Reload: image Progenitor\.Scratch\.dll sha256=([0-9a-fA-F]+)/i;

function sha256Hex(data) {
  return createHash('sha256').update(data).digest('hex');
}

function fileSha256(io, file) {
  if (typeof io.existsSync === 'function' && !io.existsSync(file)) return null;
  let data;
  try { data = io.readFileSync(file); }
  catch { return null; }
  if (data == null) return null;
  return sha256Hex(data);
}

function fileMtimeMs(io, file) {
  try {
    if (typeof io.existsSync === 'function' && !io.existsSync(file)) return null;
    const st = (typeof io.statSync === 'function' ? io.statSync : fs.statSync)(file);
    if (st == null) return null;
    if (typeof st.mtimeMs === 'number' && Number.isFinite(st.mtimeMs)) return st.mtimeMs;
    if (st.mtime instanceof Date) {
      const t = st.mtime.getTime();
      return Number.isFinite(t) ? t : null;
    }
    if (typeof st.mtime === 'number' && Number.isFinite(st.mtime)) return st.mtime;
    return null;
  } catch {
    return null;
  }
}

function clockNowMs(ctx) {
  const n = ctx?.clock?.now?.();
  return Number.isFinite(n) ? n : Date.now();
}

function isoFromMs(ms) {
  const n = Number(ms);
  const t = Number.isFinite(n) ? n : Date.now();
  return new Date(t).toISOString();
}

/** Parse a freeze-dump timestamp (ISO string, epoch ms, or `{at|time|writtenAt|timestamp}`). */
export function parseTimestampMs(value) {
  if (value == null || value === '') return null;
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value === 'string') {
    const n = Date.parse(value);
    return Number.isFinite(n) ? n : null;
  }
  if (typeof value === 'object') {
    return parseTimestampMs(value.at ?? value.time ?? value.writtenAt ?? value.timestamp ?? value.utc ?? value.iso);
  }
  return null;
}

function lastFreezeDumpNewerThan(dump, startMs) {
  const t = parseTimestampMs(dump);
  if (t == null || !Number.isFinite(Number(startMs))) return false;
  return t >= Number(startMs);
}

function statusFrameOf(st) {
  if (st == null || typeof st !== 'object') return null;
  const n = Number(st.frame);
  return Number.isFinite(n) ? n : null;
}

function captureGateSide(spec, ctx) {
  return spec?.side ?? ctx?.side ?? 'client';
}

function mainThreadInFlightRow(ctx, side) {
  const fn = ctx?.bridge?.mainThreadInFlight;
  if (typeof fn !== 'function') return null;
  return fn.call(ctx.bridge, side) ?? null;
}

/** Yield without advancing a fake soak clock, so a hung main-thread op can finish. */
function yieldEventLoop() {
  return new Promise((resolve) => {
    if (typeof setImmediate === 'function') setImmediate(resolve);
    else setTimeout(resolve, 0);
  });
}

async function waitWhileMainThreadInFlight(ctx, side) {
  let held = mainThreadInFlightRow(ctx, side);
  if (!held) return null;
  while ((held = mainThreadInFlightRow(ctx, side))) {
    if (isRunCancelled(ctx)) return held;
    await yieldEventLoop();
  }
  return null;
}

function pingCaptureOverlap(aStart, aEnd, bStart, bEnd) {
  return aStart < bEnd && bStart < aEnd;
}

function nearestPingWindow(startedAtMs, endedAtMs, pingWindows) {
  if (!Array.isArray(pingWindows) || pingWindows.length === 0) return null;
  const capStart = Number(startedAtMs);
  const capEnd = Number(endedAtMs);
  if (!Number.isFinite(capStart)) return null;
  const capMid = Number.isFinite(capEnd) ? (capStart + capEnd) / 2 : capStart;
  let best = null;
  let bestDist = Infinity;
  for (const ping of pingWindows) {
    const pStart = Number(ping.startedAtMs);
    const pEnd = Number(ping.endedAtMs);
    if (!Number.isFinite(pStart)) continue;
    const pMid = Number.isFinite(pEnd) ? (pStart + pEnd) / 2 : pStart;
    const dist = Math.abs(capMid - pMid);
    if (dist >= bestDist) continue;
    bestDist = dist;
    const overlap = Number.isFinite(pEnd) && Number.isFinite(capEnd)
      && pingCaptureOverlap(capStart, capEnd, pStart, pEnd);
    let gapMs = null;
    if (overlap) gapMs = 0;
    else if (Number.isFinite(pEnd) && capStart >= pEnd) gapMs = capStart - pEnd;
    else if (Number.isFinite(capEnd) && pStart >= capEnd) gapMs = pStart - capEnd;
    best = {
      startedAt: ping.startedAt ?? isoFromMs(pStart),
      endedAt: ping.endedAt ?? (Number.isFinite(pEnd) ? isoFromMs(pEnd) : null),
      startedAtMs: pStart,
      endedAtMs: Number.isFinite(pEnd) ? pEnd : null,
      overlap: overlap === true,
      gapMs,
    };
  }
  return best;
}

function captureWindowFromFrame(frame, pingWindows) {
  const startedAtMs = Number.isFinite(Number(frame?.startedMs)) ? Number(frame.startedMs) : null;
  const endedAtMs = Number.isFinite(Number(frame?.endedMs)) ? Number(frame.endedMs) : startedAtMs;
  return {
    startedAt: startedAtMs != null ? isoFromMs(startedAtMs) : (frame?.startedAt ?? null),
    endedAt: endedAtMs != null ? isoFromMs(endedAtMs) : (frame?.endedAt ?? null),
    startedAtMs,
    endedAtMs,
    path: frame?.path ?? null,
    nearestPing: startedAtMs != null ? nearestPingWindow(startedAtMs, endedAtMs, pingWindows) : null,
  };
}

function scratchImageHashMatches(dllHash, logged) {
  const have = String(dllHash ?? '').toLowerCase();
  const seen = String(logged ?? '').toLowerCase();
  if (!have || !seen) return false;
  return have.startsWith(seen) || seen.startsWith(have);
}

function isInsideRepo(file, repoRoot) {
  const resolved = path.resolve(file);
  const root = path.resolve(repoRoot);
  const rel = path.relative(root, resolved);
  return rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel));
}

function recordSince(rec) {
  if (rec == null) return -1;
  if (typeof rec === 'number') return rec;
  if (typeof rec.since === 'number') return rec.since;
  if (typeof rec.logSeq === 'number') return rec.logSeq;
  if (typeof rec.latestSeq === 'number') return rec.latestSeq;
  return -1;
}

function escapeRegex(s) {
  return String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** Adopted `index:version` as a regex token: not a substring of another id. */
function trackedIdToken(entity) {
  return `(?<![\\d:])${escapeRegex(entity)}(?![\\d:])`;
}

/** Replace `@tracked:name` in a regex with the adopted entity id (escaped, whole token). */
export function expandTrackedRefs(pattern, ctx) {
  return String(pattern ?? '').replace(/@tracked:([A-Za-z0-9_-]+)/g, (_, name) => {
    const row = ctx.tracker?.get?.(name);
    const entity = row?.entity;
    if (!entity) throw new Error(`@tracked:${name} is not adopted`);
    return trackedIdToken(entity);
  });
}

/** Parse a log `time` field to milliseconds. Accepts ISO timestamps or `HH:MM:SS[.mmm]`. */
export function parseLogTimeMs(value) {
  if (value == null || value === '') return null;
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  const s = String(value).trim();
  const iso = Date.parse(s);
  if (Number.isFinite(iso)) return iso;
  const m = /^(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d{1,9}))?/.exec(s);
  if (!m) return null;
  const hour = Number(m[1]);
  const min = Number(m[2]);
  const sec = Number(m[3]);
  let ms = 0;
  if (m[4] != null) ms = Number(m[4].slice(0, 3).padEnd(3, '0'));
  if (![hour, min, sec, ms].every(Number.isFinite)) return null;
  return ((hour * 3600) + (min * 60) + sec) * 1000 + ms;
}

function existsExpect(step) {
  if (step.expect === 'any' || step.expect === null) return { skip: true, expect: null };
  return { skip: false, expect: step.expect !== false };
}

async function playerPosition(ctx) {
  const got = await ctx.bridge.call('server', 'players');
  const players = got?.players ?? [];
  const row = players.find((p) => p.name === ctx.user) ?? players.find((p) => p.connected) ?? players[0];
  if (!row) throw new Error('no player found for spawn offset');
  const pos = row.position ?? { x: 0, y: 0, z: 0 };
  return { player: row, position: pos };
}

function resolveDll(step, ctx) {
  const raw = step.dll;
  if (raw && raw !== '<path>' && ctx.fs.existsSync(raw)) return raw;
  if (ctx.dll && ctx.fs.existsSync(ctx.dll)) return ctx.dll;
  if (ctx.dll) return ctx.dll;
  throw new Error('swap step requires --dll <path> (a different Sample image)');
}

async function stepCommand(step, ctx) {
  const since = await readLatestSeq(ctx.bridge, 'server');
  const sent = await ctx.bridge.call('server', 'command', { text: step.text, user: ctx.user });
  const out = { ok: true, sent, since };
  const expectLog = step.expectLog
    ?? (ctx.teardown ? { regex: escapeRegex(String(step.text ?? '')), withinMs: 3000 } : null);
  if (expectLog) {
    const filter = expectLog.filter;
    const withinMs = expectLog.withinMs ?? 3000;
    const waited = await waitForLogs(ctx.bridge, 'server', {
      since,
      filter,
      match: lineMatches(expectLog.regex ?? '.'),
      timeoutMs: withinMs,
      pollMs: 100,
      clock: ctx.clock,
    });
    const line = waited.lines[0];
    out.ok = waited.ok;
    out.line = line ? line.text : null;
    out.collected = waited.collected;
    out.elapsedMs = waited.elapsedMs;
    if (!waited.ok) {
      out.error = `no handler line for ${step.text}`;
    }
  }
  if (step.record) ctx.records[step.record] = { since, sent, line: out.line };
  return out;
}

function livePrefabName(prefab) {
  return prefabDisplayName(prefab);
}

function playerIdsFromCtx(ctx) {
  if (ctx.playerIds instanceof Set) return ctx.playerIds;
  const ids = new Set();
  if (ctx.character) ids.add(String(ctx.character));
  if (ctx.userEntity) ids.add(String(ctx.userEntity));
  return ids;
}

async function stepAdopt(step, ctx) {
  if (!step.name) throw new Error('adopt requires name');
  const entity = step.entity != null ? String(step.entity) : '';
  if (!/^\d+:\d+$/.test(entity)) {
    return { ok: false, error: `adopt entity must be index:version, got ${JSON.stringify(step.entity)}` };
  }
  const owned = playerIdsFromCtx(ctx);
  if (owned.has(entity)) {
    return { ok: false, error: `refusing to adopt player or user entity ${entity}` };
  }
  const parsed = parseResult(await ctx.bridge.call('server', 'entity', existsRequest([entity])));
  const entry = parsed.entries.find((e) => e.entity === entity) ?? parsed.entries[0];
  if (!entry || entry.exists !== true) {
    return { ok: false, error: `entity ${entity} does not exist`, entity, result: parsed };
  }
  if (isDeniedPlayerPrefab(entry.prefab)) {
    return {
      ok: false,
      error: `refusing to adopt player prefab ${livePrefabName(entry.prefab)}`,
      entity,
      prefab: entry.prefab,
      result: parsed,
    };
  }
  if (step.prefab != null && step.prefab !== '') {
    const live = livePrefabName(entry.prefab);
    if (live !== String(step.prefab)) {
      return {
        ok: false,
        error: `prefab mismatch: expected ${step.prefab}, live ${live}`,
        entity,
        expectedPrefab: step.prefab,
        livePrefab: entry.prefab,
        result: parsed,
      };
    }
  }
  const tracked = ctx.tracker.adopt({
    name: step.name,
    entity,
    prefab: step.prefab ?? livePrefabName(entry.prefab),
    side: step.side ?? 'server',
    by: ctx.stepName,
    hostile: !!step.hostile,
  });
  return { ok: true, entity, prefab: tracked.prefab, hostile: !!tracked.hostile, tracked: tracked.entity };
}

async function stepSpawn(step, ctx) {
  const { position } = await playerPosition(ctx);
  const offset = step.offset ?? { x: 0, y: 0, z: 0 };
  const args = {
    prefab: step.prefab,
    x: (position.x ?? 0) + (offset.x ?? 0),
    y: (position.y ?? 0) + (offset.y ?? 0),
    z: (position.z ?? 0) + (offset.z ?? 0),
    count: step.count ?? 1,
  };
  const result = await ctx.bridge.call('server', 'spawn', args);
  const name = step.name ?? step.prefab ?? 'spawn';
  let tracked = [];
  if (step.track !== false) {
    tracked = ctx.tracker.adoptSpawn(name, result, {
      side: 'server',
      by: ctx.stepName,
      hostile: !!step.hostile,
      prefab: step.prefab,
    });
  }
  return { ok: true, args, result, tracked: tracked.map((t) => t.entity) };
}

async function stepDestroyTracked(step, ctx) {
  const result = await ctx.tracker.destroyNamed(step.name);
  return { ok: result.ok, ...result, remaining: ctx.tracker.formatRemaining(result.remaining) };
}

async function stepWait(step, ctx) {
  await ctx.clock.sleep(step.ms ?? 0);
  return { ok: true, ms: step.ms ?? 0 };
}

async function stepGet(step, ctx) {
  const ref = ctx.tracker.resolve(step.entity, step.side ?? 'server');
  const result = await ctx.bridge.call(ref.side ?? 'server', 'get', {
    entity: ref.entity,
    component: step.component,
  });
  if (step.record) ctx.records[step.record] = result;
  return { ok: true, entity: ref.entity, component: step.component, result, record: step.record };
}

async function stepExists(step, ctx) {
  const ref = ctx.tracker.resolve(step.entity, step.side ?? 'server');
  const parsed = parseResult(await ctx.bridge.call(ref.side ?? 'server', 'entity', existsRequest([ref.entity])));
  const entry = parsed.entries.find((e) => e.entity === ref.entity) ?? parsed.entries[0];
  const exists = entry ? entry.exists !== false : true;
  const expect = step.expect !== false;
  return { ok: exists === expect, entity: ref.entity, exists, expect, result: parsed };
}

async function stepSwap(step, ctx) {
  const dll = resolveDll(step, ctx);
  const result = await ctx.reloader.swap(ctx.side, dll);
  return { dll, ...result, ok: result.ok !== false };
}

async function stepRemoval(step, ctx) {
  const result = await ctx.reloader.removal(ctx.side);
  if (result.logs && !ctx.records.sampleUnload) {
    const unload = (result.logs ?? []).find((l) => /Progenitor\.Sample\.dll removed/.test(String(l.text ?? '')));
    if (unload) {
      ctx.records.sampleUnload = {
        logTime: unload.time,
        logSeq: unload.seq,
        seenAt: ctx.clock.now(),
        text: unload.text,
      };
    }
  }
  return { ok: result.ok !== false, ...result };
}

async function stepReinstall(step, ctx) {
  const result = await ctx.reloader.reinstall(ctx.side);
  return { ok: result.ok !== false, ...result };
}

async function stepLogEntity(step, ctx) {
  if (!step.name) throw new Error('logEntity requires name');
  if (!step.regex) throw new Error('logEntity requires regex with a capture group for index:version');
  const side = step.side ?? ctx.side;
  let since = -1;
  if (step.sinceRecord) {
    if (!(step.sinceRecord in (ctx.records ?? {}))) {
      throw new Error(`logEntity sinceRecord '${step.sinceRecord}' is missing`);
    }
    since = recordSince(ctx.records[step.sinceRecord]);
  } else if (typeof step.since === 'number') {
    since = step.since;
  }
  const waited = await waitForLatestLog(ctx.bridge, side, {
    since,
    filter: step.filter,
    regex: step.regex,
    timeoutMs: step.timeoutMs ?? 5000,
    pollMs: 100,
    clock: ctx.clock,
  });
  if (!waited.ok || !waited.line || !waited.match) {
    return { ok: false, error: `no log line matching ${step.regex} on ${side}` };
  }
  const captured = waited.match[1];
  if (!captured || !/^\d+:\d+$/.test(captured)) {
    return { ok: false, error: `logEntity capture was not index:version: ${JSON.stringify(captured)}` };
  }
  const seenAt = ctx.clock.now();
  ctx.tracker.adopt({
    name: step.name,
    entity: captured,
    side,
    by: ctx.stepName,
    hostile: false,
    prefab: step.prefab ?? null,
    logTime: waited.line.time ?? null,
    logSeq: waited.line.seq ?? null,
    seenAt,
  });
  if (step.track !== true) ctx.tracker.markSkipCleanup(step.name);
  const stamp = {
    entity: captured,
    logTime: waited.line.time ?? null,
    logSeq: waited.line.seq ?? null,
    seenAt,
    text: waited.line.text,
  };
  if (step.record) ctx.records[step.record] = stamp;
  return { ok: true, ...stamp, line: waited.line };
}

async function stepLog(step, ctx) {
  const side = step.side ?? ctx.side ?? 'server';
  let since = -1;
  let sinceRec = null;
  if (step.sinceRecord) {
    if (!(step.sinceRecord in (ctx.records ?? {}))) {
      return { ok: false, error: `log sinceRecord '${step.sinceRecord}' is missing` };
    }
    sinceRec = ctx.records[step.sinceRecord];
    since = recordSince(sinceRec);
  } else if (typeof step.since === 'number') {
    since = step.since;
  } else {
    // Default: only lines emitted after this step began can satisfy it.
    since = await readLatestSeq(ctx.bridge, side);
  }
  let regexSource;
  try {
    regexSource = expandTrackedRefs(step.regex ?? '.', ctx);
  } catch (e) {
    return { ok: false, error: e && e.message ? e.message : String(e), since };
  }
  const re = new RegExp(regexSource);
  const waited = await waitForLatestLog(ctx.bridge, side, {
    since,
    filter: step.filter,
    regex: re,
    timeoutMs: step.timeoutMs ?? 5000,
    pollMs: 100,
    clock: ctx.clock,
  });
  const matching = (waited.collected ?? []).filter((l) => re.test(String(l.text ?? '')));
  const n = matching.length;
  const wantCount = step.count != null && Number.isFinite(Number(step.count));
  const expectedCount = wantCount ? Number(step.count) : null;
  let ok = true;
  let error;
  if (wantCount) {
    if (n > expectedCount) {
      ok = false;
      error = `duplicate handler: ${n} lines matching ${regexSource}`;
    } else if (n < expectedCount) {
      ok = false;
      error = `expected ${expectedCount} matching line(s), got ${n}`;
    }
  } else if (!waited.ok || !waited.line) {
    return { ok: false, error: `no log line matching ${regexSource} on ${side}`, since };
  }
  const line = matching[matching.length - 1] ?? waited.line ?? null;
  if (!line) {
    if (wantCount && expectedCount === 0 && ok) {
      const atMs = ctx.clock.now();
      const stamp = {
        since,
        logTime: null,
        logSeq: null,
        seenAt: atMs,
        text: null,
        match: null,
        count: 0,
      };
      if (step.record) ctx.records[step.record] = stamp;
      return { ok: true, count: 0, ...stamp, line: null, elapsedMs: waited.elapsedMs };
    }
    return {
      ok: false,
      error: error ?? `no log line matching ${regexSource} on ${side}`,
      count: n,
      since,
    };
  }
  const match = re.exec(String(line.text ?? ''));
  const atMs = ctx.clock.now();
  const ageClock = step.ageClock === 'log' ? 'log' : 'wall';
  const timestamps = {
    sinceLogTime: sinceRec?.logTime ?? null,
    sinceSeenAt: sinceRec?.seenAt ?? null,
    atLogTime: line.time ?? null,
    atLogSeq: line.seq ?? null,
    atMs,
    ageClock,
  };
  if (sinceRec?.seenAt != null) timestamps.ageMs = atMs - sinceRec.seenAt;
  const sinceLogMs = parseLogTimeMs(sinceRec?.logTime);
  const atLogMs = parseLogTimeMs(line.time);
  if (sinceLogMs != null && atLogMs != null) timestamps.logAgeMs = atLogMs - sinceLogMs;
  if (ok && step.maxAgeMs != null) {
    timestamps.maxAgeMs = step.maxAgeMs;
    const age = ageClock === 'log' ? timestamps.logAgeMs : timestamps.ageMs;
    if (age == null) {
      ok = false;
      error = `log age could not be measured on the ${ageClock} clock`;
    } else if (age > step.maxAgeMs) {
      ok = false;
      error = `log event was older than ${step.maxAgeMs}ms at this check (age ${age}ms); it may already have expired before the event`;
    }
  }
  const stamp = {
    since,
    logTime: line.time ?? null,
    logSeq: line.seq ?? null,
    seenAt: atMs,
    text: line.text,
    match: match ? [...match] : null,
    timestamps,
  };
  if (wantCount) stamp.count = n;
  if (step.record) ctx.records[step.record] = stamp;
  return { ok, error, ...(wantCount ? { count: n } : {}), ...stamp, line };
}

async function stepExistsAt(step, ctx) {
  const ref = ctx.tracker.resolve(step.entity, step.side ?? 'server');
  let atLog = null;
  if (step.after) {
    const afterSide = step.after.side ?? ref.side ?? 'server';
    let since = -1;
    if (step.after.sinceRecord) {
      if (!(step.after.sinceRecord in (ctx.records ?? {}))) {
        return { ok: false, error: `existsAt after.sinceRecord '${step.after.sinceRecord}' is missing` };
      }
      since = recordSince(ctx.records[step.after.sinceRecord]);
    } else if (typeof step.after.since === 'number') {
      since = step.after.since;
    }
    const waited = await waitForLatestLog(ctx.bridge, afterSide, {
      since,
      filter: step.after.filter,
      regex: step.after.regex ?? '.',
      timeoutMs: step.after.timeoutMs ?? 20000,
      pollMs: 100,
      clock: ctx.clock,
    });
    if (!waited.ok || !waited.line) {
      return { ok: false, error: 'existsAt did not see the after log line', timestamps: { entityLogTime: ref.logTime, entitySeenAt: ref.seenAt } };
    }
    atLog = waited.line;
  }
  const parsed = parseResult(await ctx.bridge.call(ref.side ?? 'server', 'entity', existsRequest([ref.entity])));
  const entry = parsed.entries.find((e) => e.entity === ref.entity) ?? parsed.entries[0];
  const exists = entry ? entry.exists !== false : true;
  const { skip: skipExists, expect } = existsExpect(step);
  const atMs = ctx.clock.now();
  const ageClock = step.ageClock === 'log' ? 'log' : 'wall';
  const timestamps = {
    entityLogTime: ref.logTime ?? null,
    entitySeenAt: ref.seenAt ?? null,
    atLogTime: atLog?.time ?? null,
    atLogSeq: atLog?.seq ?? null,
    atMs,
    ageClock,
  };
  if (ref.seenAt != null) timestamps.ageMs = atMs - ref.seenAt;
  const entityLogMs = parseLogTimeMs(ref.logTime);
  const atLogMs = parseLogTimeMs(atLog?.time);
  if (entityLogMs != null && atLogMs != null) timestamps.logAgeMs = atLogMs - entityLogMs;
  let ok = skipExists ? true : exists === expect;
  let error;
  if (step.maxAgeMs != null) {
    timestamps.maxAgeMs = step.maxAgeMs;
    const age = ageClock === 'log' ? timestamps.logAgeMs : timestamps.ageMs;
    if (age == null) {
      if (ok) {
        ok = false;
        error = `entity age could not be measured on the ${ageClock} clock`;
      }
    } else if (ok && age > step.maxAgeMs) {
      ok = false;
      error = `entity was older than ${step.maxAgeMs}ms at this check (age ${age}ms); it may already have expired before the event`;
    }
  }
  if (step.record) ctx.records[step.record] = { ...timestamps, exists, expect };
  return { ok, entity: ref.entity, exists, expect, timestamps, atLog: atLog ? atLog.text : null, error, result: parsed };
}

async function stepProtectionCheck(step, ctx) {
  const check = await protection({
    bridge: ctx.bridge,
    user: ctx.user,
    clock: ctx.clock,
    timeoutMs: 3000,
  });
  const expect = step.expect !== false;
  return { ok: check.ok === expect, expect, check };
}

async function stepSkipCleanupFor(step, ctx) {
  const row = ctx.tracker.markSkipCleanup(step.name);
  return { ok: true, skipped: row };
}

/** SampleFrostNovaAppearance ColorParticle _BaseColor (source-checked). */
export const POOL_OVERRIDE_COLOR = Object.freeze([0.12, 0.65, 0.25, 1]);
export const POOL_HISTORICAL_SCENE_COUNT = 122;
export const POOL_REUSE_CAPTURE = Object.freeze({
  available: false,
  reason: 'no capture configured on this poolReuse step',
});

/** Default bound for one observe.ps1 `shot`. */
export const CAPTURE_DEFAULT_TIMEOUT_MS = 10000;
/** Compact review width from docs/observe-capture.md; 0 keeps native resolution. */
export const CAPTURE_DEFAULT_MAX_WIDTH = 1920;
export const CAPTURE_DEFAULT_WINDOW = 'client';
export const CAPTURE_DEFAULT_REGION = 'window';
export const CAPTURE_DEFAULT_METHOD = 'PrintWindow';
export const CAPTURE_INSPECT_NOTE = 'A frame is inspectable evidence only after a person or the reviewing agent has viewed it. The rig does not analyse pixels.';
/** Fail a timed census when dash-to-census exceeds this (PrintWindow must not sit on that path). */
export const CAPTURE_DEFAULT_MAX_CENSUS_DELAY_MS = 2000;
/** Concurrent observe.ps1 children; retries must not grow an unbounded spawn set. */
export const CAPTURE_DEFAULT_MAX_IN_FLIGHT = 4;
/** SIGTERM then wait this long before SIGKILL on cancel/abort. */
export const CHILD_KILL_GRACE_MS = 2000;

const CAPTURE_TAIL_CHARS = 4000;
const INPUT_OBSERVE_ACTIONS = new Set(['key', 'type', 'move', 'click', 'scroll']);

export function windowsPowerShell51Path() {
  const root = process.env.SystemRoot || process.env.WINDIR || 'C:\\Windows';
  return path.join(root, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
}

export function observePs1Path(repoRoot) {
  return path.join(repoRoot, 'tools', 'observe.ps1');
}

export function observeShotArgv({
  script,
  outPath,
  window = CAPTURE_DEFAULT_WINDOW,
  region = CAPTURE_DEFAULT_REGION,
  maxWidth = CAPTURE_DEFAULT_MAX_WIDTH,
}) {
  const width = Number(maxWidth);
  return [
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', script,
    'shot',
    '-Window', window,
    '-Out', outPath,
    '-Region', region,
    '-Method', CAPTURE_DEFAULT_METHOD,
    '-MaxWidth', String(Number.isFinite(width) ? width : CAPTURE_DEFAULT_MAX_WIDTH),
  ];
}

function quoteArg(value) {
  const t = String(value);
  if (t === '') return '""';
  if (!/[\s"]/.test(t)) return t;
  return `"${t.replace(/"/g, '\\"')}"`;
}

export function formatCommandLine(cmd, args) {
  return [cmd, ...(args ?? [])].map(quoteArg).join(' ');
}

const COLOR_EPS = Object.freeze({ r: 0.05, g: 0.08, b: 0.08, a: 0.05 });
const ROW_NEW = /^POOLID rendererId=(-?\d+) goId=(-?\d+) active=(True|False) slot=(-?\d+) hasBlock=(True|False) baseColor=(\S+) go=(.*)$/;
const ROW_OLD = /^POOLID rendererId=(-?\d+) goId=(-?\d+) go=(.*) activeInHierarchy=(True|False) slot=(-?\d+) hasBlock=(True|False) baseColor=(\S+)$/;

export function poolRowId(row) {
  return `${row.rendererId}/${row.goId}/${row.slot}`;
}

export function poolPairId(row) {
  return `${row.rendererId}/${row.goId}`;
}

function normalizePoolText(text) {
  const t = String(text ?? '');
  const idx = t.indexOf('POOLID ');
  if (idx < 0) return null;
  return t.slice(idx).trim();
}

function parseColorToken(token) {
  if (token == null || token === '' || token === 'none' || token === '-') return null;
  if (String(token).startsWith('err:')) return undefined;
  const parts = String(token).split(',');
  if (parts.length !== 4) return undefined;
  const nums = parts.map(Number);
  if (nums.some((n) => !Number.isFinite(n))) return undefined;
  return nums;
}

function colorsEqual(a, b, eps = COLOR_EPS) {
  if (a == null && b == null) return true;
  if (a == null || b == null) return false;
  return Math.abs(a[0] - b[0]) < eps.r
    && Math.abs(a[1] - b[1]) < eps.g
    && Math.abs(a[2] - b[2]) < eps.b
    && Math.abs((a[3] ?? 1) - (b[3] ?? 1)) < eps.a;
}

export function parseOverrideColor(value) {
  if (Array.isArray(value) && value.length >= 3) {
    return [Number(value[0]), Number(value[1]), Number(value[2]), Number(value[3] ?? 1)];
  }
  if (typeof value === 'string') {
    const parsed = parseColorToken(value);
    if (parsed) return parsed;
  }
  return [...POOL_OVERRIDE_COLOR];
}

function isOverrideColor(color, override) {
  return colorsEqual(color, override);
}

function parsePoolRow(text) {
  const t = normalizePoolText(text);
  if (!t || !t.startsWith('POOLID rendererId=')) return { skip: true };
  let m = ROW_NEW.exec(t);
  if (m) {
    const color = parseColorToken(m[6]);
    if (m[6] !== 'none' && m[6] !== '-' && color === undefined) return { malformed: t };
    return {
      row: {
        rendererId: Number(m[1]),
        goId: Number(m[2]),
        active: m[3] === 'True',
        slot: Number(m[4]),
        hasBlock: m[5] === 'True',
        baseColor: m[6] === '-' ? 'none' : m[6],
        color,
        go: m[7] ?? '',
      },
    };
  }
  m = ROW_OLD.exec(t);
  if (m) {
    const color = parseColorToken(m[7]);
    if (m[7] !== 'none' && m[7] !== '-' && color === undefined) return { malformed: t };
    return {
      row: {
        rendererId: Number(m[1]),
        goId: Number(m[2]),
        go: m[3] ?? '',
        active: m[4] === 'True',
        slot: Number(m[5]),
        hasBlock: m[6] === 'True',
        baseColor: m[7] === '-' ? 'none' : m[7],
        color,
      },
    };
  }
  return { malformed: t };
}

function parseSummaryLine(text) {
  const t = normalizePoolText(text);
  if (!t || !t.startsWith('POOLID summary')) return null;
  const errorM = /^POOLID summary error=(.*)$/.exec(t);
  if (errorM) return { error: errorM[1].trim(), raw: t };
  const kv = {};
  const re = /(\w+)=(\S+)/g;
  let m;
  while ((m = re.exec(t))) kv[m[1]] = m[2];
  const num = (k) => {
    if (kv[k] == null) return null;
    const n = Number(kv[k]);
    return Number.isFinite(n) ? n : kv[k];
  };
  return {
    raw: t,
    renderers: num('renderers'),
    active: num('active'),
    withBlock: num('withBlock'),
    green: num('green'),
    failures: num('failures'),
    truncated: kv.truncated === 'True',
    cap: num('cap'),
    radius: kv.radius ?? null,
    origin: kv.origin ?? null,
    scanned: num('scanned'),
    emitted: num('emitted'),
    skippedRadius: num('skippedRadius'),
    skippedCap: num('skippedCap'),
    ids: kv.ids ?? '',
  };
}

export function parsePoolCensus(lines) {
  const rows = [];
  const malformed = [];
  let summary = null;
  for (const line of lines ?? []) {
    const text = typeof line === 'string' ? line : String(line?.text ?? '');
    const normalized = normalizePoolText(text);
    if (!normalized) continue;
    if (normalized.startsWith('POOLID summary')) {
      summary = parseSummaryLine(normalized);
      continue;
    }
    if (!normalized.startsWith('POOLID rendererId=')) continue;
    const parsed = parsePoolRow(normalized);
    if (parsed.malformed) {
      malformed.push(parsed.malformed);
      continue;
    }
    if (parsed.skip || !parsed.row) continue;
    const row = parsed.row;
    row.id = poolRowId(row);
    rows.push(row);
  }
  const byId = {};
  for (const row of rows) byId[row.id] = row;
  return {
    rows,
    byId,
    malformed,
    summary,
    truncated: summary?.truncated === true,
  };
}

function wholeSceneInformational(a, b, label) {
  const setOf = (census) => {
    const s = new Set();
    for (const row of census?.rows ?? []) s.add(poolPairId(row));
    return s;
  };
  const sa = setOf(a);
  const sb = setOf(b);
  const onlyA = [...sa].filter((k) => !sb.has(k));
  const onlyB = [...sb].filter((k) => !sa.has(k));
  return {
    informational: true,
    note: 'historical whole-scene 122-id equality is not a pass condition',
    historicalReferenceCount: POOL_HISTORICAL_SCENE_COUNT,
    label,
    aCount: sa.size,
    bCount: sb.size,
    equal: onlyA.length === 0 && onlyB.length === 0,
    onlyInA: onlyA,
    onlyInB: onlyB,
  };
}

function tintedParticipants(census, override) {
  const T = [];
  for (const row of census?.rows ?? []) {
    if (!row.active) continue;
    if (!isOverrideColor(row.color, override)) continue;
    T.push(row.id);
  }
  return T;
}

function lookupRecord(ctx, name) {
  if (!name) return null;
  return ctx?.records?.[name] ?? null;
}

function censusRowById(census, id) {
  if (id == null || id === '') return null;
  const fromMap = census?.byId?.[id];
  if (fromMap) return fromMap;
  return (census?.rows ?? []).find((r) => r.id === id) ?? null;
}

function defaultColourFor(row, baselineRow) {
  if (!row.hasBlock) return true;
  if (baselineRow?.hasBlock && colorsEqual(row.color, baselineRow.color)) return true;
  return false;
}

export function assertPoolCensus(spec, census, ctx = {}) {
  const phase = spec?.phase ?? 'baseline';
  const override = parseOverrideColor(spec?.overrideColor);
  const out = {
    phase,
    ok: true,
    participants: [],
    reused: [],
    qualifiedTinted: 0,
    qualifiedReused: 0,
    informational: {},
    truncated: census?.truncated === true,
  };

  if (!census) {
    return { ...out, ok: false, error: 'malformed census line' };
  }
  if (census.summary?.error) {
    return { ...out, ok: false, error: `census error: ${census.summary.error}` };
  }
  if ((census.malformed ?? []).length > 0) {
    return { ...out, ok: false, error: 'malformed census line', malformed: census.malformed };
  }

  const baseline = lookupRecord(ctx, spec?.baselineRecord);
  const tinted = lookupRecord(ctx, spec?.tintedRecord);
  if (baseline) out.informational.baselineVsThis = wholeSceneInformational(baseline, census, `${phase} vs baseline`);
  if (tinted && phase !== 'tinted') {
    out.informational.tintedVsThis = wholeSceneInformational(tinted, census, `${phase} vs tinted`);
  }

  if (phase === 'baseline') return out;

  if (phase === 'tinted') {
    const T = tintedParticipants(census, override);
    out.participants = T;
    out.qualifiedTinted = T.length;
    out.qualifiedTintedRenderers = new Set(T.map((id) => String(id).split('/')[0])).size;
    const inBaseline = T.filter((id) => baseline?.byId?.[id]);
    out.informational.tintedInBaseline = inBaseline.length;
    out.informational.tintedFresh = T.length - inBaseline.length;
    if (T.length === 0) return { ...out, ok: false, error: 'zero participants' };
    return out;
  }

  const T = tinted?.participants ?? tintedParticipants(tinted ?? { rows: [] }, override);
  out.participants = T;
  out.qualifiedTinted = T.length;
  out.qualifiedTintedRenderers = new Set(T.map((id) => String(id).split('/')[0])).size;

  if (phase === 'rest') {
    if (T.length === 0) return { ...out, ok: false, error: 'zero participants' };
    const missing = [];
    for (const id of T) {
      const row = censusRowById(census, id);
      const baseRow = censusRowById(baseline, id);
      if (!row) {
        missing.push(id);
        continue;
      }
      if (row.active) return { ...out, ok: false, error: 'residual tint', residual: id };
      if (isOverrideColor(row.color, override) && !isOverrideColor(baseRow?.color, override)) {
        return { ...out, ok: false, error: 'residual tint', residual: id };
      }
      if (!row.hasBlock) continue;
      if (baseRow && colorsEqual(row.color, baseRow.color) && baseRow.hasBlock === true) continue;
      if (isOverrideColor(row.color, override)) return { ...out, ok: false, error: 'residual tint', residual: id };
      return { ...out, ok: false, error: 'rest state does not match baseline', residual: id };
    }
    if (missing.length > 0) {
      return { ...out, ok: false, error: `tinted object missing at rest: ${missing.join(', ')}`, missing };
    }
    return out;
  }

  if (phase === 'reuse') {
    if (T.length === 0) return { ...out, ok: false, error: 'zero participants' };
    const tSet = new Set(T);
    const R = [];
    for (const id of T) {
      const row = censusRowById(census, id);
      if (!row) continue;
      if (!row.active) continue;
      const baseRow = censusRowById(baseline, id);
      const stillTinted = isOverrideColor(row.color, override) && !isOverrideColor(baseRow?.color, override);
      const isDefault = defaultColourFor(row, baseRow) && !stillTinted;
      if (isDefault) R.push(id);
    }
    let activeDefaultNotInT = 0;
    for (const row of census.rows ?? []) {
      if (!row.active) continue;
      if (tSet.has(row.id)) continue;
      const baseRow = censusRowById(baseline, row.id);
      const stillTinted = isOverrideColor(row.color, override) && !isOverrideColor(baseRow?.color, override);
      const isDefault = defaultColourFor(row, baseRow) && !stillTinted;
      if (isDefault) activeDefaultNotInT += 1;
    }
    const presentR = R.filter((id) => censusRowById(census, id));
    out.reused = presentR;
    out.qualifiedReused = presentR.length;
    if (presentR.length > 0) return out;
    if (activeDefaultNotInT > 0) {
      return { ...out, ok: false, error: 'fresh allocation without reused participant' };
    }
    return { ...out, ok: false, error: 'reuse not observed' };
  }

  return { ...out, ok: false, error: `unknown pool assert phase ${phase}` };
}

async function collectPoolLines(ctx, side, since, already) {
  const merged = [];
  const seen = new Set();
  const push = (line) => {
    const text = typeof line === 'string' ? line : String(line?.text ?? '');
    const key = line?.seq != null ? `seq:${line.seq}` : text;
    if (seen.has(key)) return;
    seen.add(key);
    merged.push(typeof line === 'string' ? { text: line } : line);
  };
  for (const line of already ?? []) push(line);
  try {
    const extra = await ctx.bridge.call(side, 'logs', { since, max: 600, filter: 'POOLID' });
    for (const line of extra?.lines ?? []) push(line);
  } catch {
    /* keep what we have */
  }
  return merged;
}

function attachCensusRecord(step, ctx, census, asserted) {
  const recorded = {
    ...census,
    participants: asserted?.participants ?? [],
    reused: asserted?.reused ?? [],
    qualifiedTinted: asserted?.qualifiedTinted ?? 0,
    qualifiedReused: asserted?.qualifiedReused ?? 0,
    informational: asserted?.informational ?? {},
  };
  if (step.record) ctx.records[step.record] = recorded;
  return recorded;
}

function snippetPathsFor(step, ctx) {
  const repoRoot = ctx.repoRoot;
  const file = step.file;
  if (!file) throw new Error('snippet step requires file');
  if (!isInsideRepo(file, repoRoot)) {
    throw new Error(`refusing snippet file outside the repository: ${file}`);
  }
  return {
    repoRoot,
    file,
    resolvedFile: path.resolve(file),
    destSnippet: path.join(repoRoot, 'tools', 'scratch', 'Snippet.cs'),
    dllSrc: path.join(repoRoot, 'tools', 'scratch', 'bin', 'Debug', 'net6.0', 'Progenitor.Scratch.dll'),
  };
}

function preparedSnippets(ctx) {
  if (!ctx.snippetPrepared) ctx.snippetPrepared = Object.create(null);
  return ctx.snippetPrepared;
}

function lookupPreparedSnippet(ctx, resolvedFile) {
  return preparedSnippets(ctx)[resolvedFile] ?? null;
}

function rememberPreparedSnippet(ctx, rec) {
  preparedSnippets(ctx)[rec.resolvedFile] = rec;
}

async function stepSnippetPrepare(step, ctx) {
  const io = ctx.fs;
  const { repoRoot, resolvedFile, destSnippet, dllSrc } = snippetPathsFor(step, ctx);
  const prevSourceHash = fileSha256(io, destSnippet);
  const prevDllHash = fileSha256(io, dllSrc);
  io.copyFileSync(resolvedFile, destSnippet);
  // Windows CopyFile keeps the source mtime, so incremental MSBuild would skip a
  // different Snippet.cs that is older than the last built DLL.
  const copiedAt = clockNowMs(ctx);
  const stamp = new Date(copiedAt);
  (io.utimesSync ?? fs.utimesSync)(destSnippet, stamp, stamp);
  const sourceHash = fileSha256(io, destSnippet);
  try { io.unlinkSync(dllSrc); } catch { /* rebuild writes a fresh output */ }
  if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
    return { ok: false, error: 'run cancelled', cancelled: true, copiedAt };
  }
  const buildStartedAt = clockNowMs(ctx);
  const buildArgs = [
    'build', path.join('tools', 'scratch', 'Progenitor.Scratch.csproj'), '-nologo', '-v', 'q',
    '--no-incremental',
  ];
  let build;
  if (typeof ctx.spawnSync === 'function') {
    build = ctx.spawnSync('dotnet', buildArgs, { encoding: 'utf8', cwd: repoRoot });
  } else {
    const child = spawn('dotnet', buildArgs, {
      cwd: repoRoot,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    trackSpawnedChild(ctx, child, { kind: 'snippet', cmd: 'dotnet', args: buildArgs });
    const readStdout = attachStreamTail(child?.stdout);
    const readStderr = attachStreamTail(child?.stderr);
    const ended = await waitForSpawnedChild(child);
    build = {
      status: ended.status,
      stdout: readStdout(),
      stderr: readStderr(),
    };
  }
  const buildElapsedMs = clockNowMs(ctx) - buildStartedAt;
  if (build.status !== 0) {
    return {
      ok: false,
      error: 'snippet compile failed',
      compile: (build.stdout || '') + (build.stderr || ''),
      code: build.status,
      dllHash: fileSha256(io, dllSrc),
      copiedAt,
      buildElapsedMs,
    };
  }
  const dllHash = fileSha256(io, dllSrc);
  if (!dllHash) {
    return { ok: false, error: 'snippet DLL missing after build', dllHash: null, copiedAt, buildElapsedMs };
  }
  const dllMtime = fileMtimeMs(io, dllSrc);
  // Equal DLL bytes with a different recorded source is not a stale build: the
  // same source may already have been compiled through tools/scratch.
  const dllUnchanged = !!(prevDllHash && dllHash === prevDllHash);
  if (dllMtime == null) {
    return {
      ok: false,
      error: 'snippet DLL mtime missing after build',
      dllHash,
      prevDllHash,
      sourceHash,
      prevSourceHash,
      copiedAt,
      dllUnchanged,
      buildElapsedMs,
    };
  }
  if (dllMtime <= copiedAt) {
    return {
      ok: false,
      error: 'snippet DLL older than copy time',
      dllHash,
      prevDllHash,
      sourceHash,
      prevSourceHash,
      dllMtime,
      copiedAt,
      dllUnchanged,
      buildElapsedMs,
    };
  }
  const prepared = {
    resolvedFile,
    destSnippet,
    dllSrc,
    dllHash,
    sourceHash,
    prevDllHash,
    prevSourceHash,
    copiedAt,
    dllMtime,
    dllUnchanged,
    buildElapsedMs,
  };
  rememberPreparedSnippet(ctx, prepared);
  if (step.record) ctx.records[step.record] = prepared;
  return { ok: true, ...prepared };
}

async function stepSnippetLoad(step, ctx) {
  const startedAt = clockNowMs(ctx);
  const elapsed = () => clockNowMs(ctx) - startedAt;
  const io = ctx.fs;
  const { resolvedFile, destSnippet, dllSrc } = snippetPathsFor(step, ctx);
  const prepared = lookupPreparedSnippet(ctx, resolvedFile);
  if (!prepared) {
    return { ok: false, error: 'snippet not prepared', elapsedMs: elapsed() };
  }
  const caseSourceHash = fileSha256(io, resolvedFile);
  const destHash = fileSha256(io, destSnippet);
  if (caseSourceHash !== prepared.sourceHash || destHash !== prepared.sourceHash) {
    return {
      ok: false,
      error: 'snippet source changed after prepare',
      elapsedMs: elapsed(),
      sourceHash: destHash,
      caseSourceHash,
      preparedSourceHash: prepared.sourceHash,
      dllHash: prepared.dllHash,
    };
  }
  const dllHash = fileSha256(io, dllSrc);
  if (!dllHash || dllHash !== prepared.dllHash) {
    return {
      ok: false,
      error: 'snippet DLL changed after prepare',
      elapsedMs: elapsed(),
      dllHash,
      preparedHash: prepared.dllHash,
      sourceHash: prepared.sourceHash,
    };
  }
  const snippetSide = step.side ?? ctx.side;
  const timeoutMs = step.timeoutMs ?? 20000;
  const ping = await ctx.bridge.call(snippetSide, 'ping');
  const folder = ping?.reloadFolder;
  if (!folder) throw new Error(`ping for ${snippetSide} did not return reloadFolder`);
  if (isLiveServerPath(folder)) {
    throw new Error(`refusing a reload folder under the live dedicated server for ${snippetSide}: ${folder}`);
  }
  const dllDest = path.join(folder, 'Progenitor.Scratch.dll');
  const since = await readLatestSeq(ctx.bridge, snippetSide);
  io.mkdirSync(folder, { recursive: true });
  io.copyFileSync(dllSrc, dllDest);
  let waited;
  try {
    waited = await waitForLogs(ctx.bridge, snippetSide, {
      since,
      filter: 'scratch',
      match: [
        (text) => SCRATCH_IMAGE_RE.test(text),
        (text) => /scratch: result/.test(text) || /scratch: error/.test(text),
      ],
      timeoutMs,
      pollMs: 250,
      clock: ctx.clock,
    });
  } finally {
    try { io.unlinkSync(dllDest); } catch { /* gone */ }
  }
  const elapsedMs = elapsed();
  const imageLine = waited.lines[0] ? String(waited.lines[0].text) : '';
  const line = waited.lines[1] ? String(waited.lines[1].text) : '';
  const loggedHash = (SCRATCH_IMAGE_RE.exec(imageLine) ?? [])[1] ?? '';
  const hashOk = scratchImageHashMatches(dllHash, loggedHash);
  const preparedFields = {
    dllHash,
    preparedHash: prepared.dllHash,
    sourceHash: prepared.sourceHash,
    copiedAt: prepared.copiedAt,
    dllMtime: prepared.dllMtime,
    dllUnchanged: prepared.dllUnchanged,
    buildElapsedMs: prepared.buildElapsedMs,
    elapsedMs,
  };
  if (!waited.ok) {
    return {
      ok: false,
      error: 'snippet logs not seen',
      line,
      imageLine,
      matched: false,
      expect: step.expect,
      ...preparedFields,
    };
  }
  if (!hashOk) {
    return {
      ok: false,
      error: 'snippet DLL hash mismatch',
      line,
      imageLine,
      loggedHash,
      matched: false,
      expect: step.expect,
      ...preparedFields,
    };
  }
  const isError = /scratch: error/.test(line);
  const re = step.expect ? new RegExp(step.expect) : /scratch: result/;
  const matched = re.test(line);
  const wantCensus = !!(step.assert || step.record || step.collectPool);
  let census = null;
  if (wantCensus) {
    const poolLines = await collectPoolLines(ctx, snippetSide, since, waited.collected);
    census = parsePoolCensus(poolLines);
  }
  let ok = !isError && matched;
  let error;
  if (isError) error = 'scratch error';
  else if (!matched) error = 'snippet expect mismatch';
  let asserted;
  if (step.assert) {
    asserted = assertPoolCensus(step.assert, census, ctx);
    if (!asserted.ok) {
      ok = false;
      error = asserted.error;
    }
  } else if (census && (census.malformed ?? []).length > 0 && wantCensus) {
    ok = false;
    error = 'malformed census line';
  }
  const recorded = census ? attachCensusRecord(step, ctx, census, asserted) : null;
  return {
    ok,
    error,
    line,
    imageLine,
    matched,
    expect: step.expect,
    census: recorded,
    assert: asserted,
    qualifiedTinted: asserted?.qualifiedTinted,
    informational: asserted?.informational,
    ...preparedFields,
  };
}

async function stepSnippet(step, ctx) {
  if (step.prepare === true) return stepSnippetPrepare(step, ctx);
  return stepSnippetLoad(step, ctx);
}

function captureIo(ctx) {
  return ctx.fs ?? fs;
}

function captureOutDir(ctx) {
  return ctx.outDir ?? ctx.bridge?.runDir ?? ctx.runDir ?? null;
}

function captureSlug(value, fallback = 'capture') {
  const s = String(value ?? '').trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return s || fallback;
}

function captureIsoStamp(ms) {
  const t = Number.isFinite(ms) ? ms : Date.now();
  return new Date(t).toISOString().replace(/:/g, '-');
}

function tailText(value, max = CAPTURE_TAIL_CHARS) {
  const s = value == null ? '' : String(value);
  if (s.length <= max) return s;
  return s.slice(s.length - max);
}

function fileSizeBytes(io, file) {
  try {
    if (typeof io.existsSync === 'function' && !io.existsSync(file)) return null;
    const st = (typeof io.statSync === 'function' ? io.statSync : fs.statSync)(file);
    if (st == null) return null;
    if (typeof st.size === 'number' && Number.isFinite(st.size)) return st.size;
    return null;
  } catch {
    return null;
  }
}

function readLastTraceId(ctx) {
  const runDir = ctx.bridge?.runDir;
  if (!runDir) return 0;
  const file = path.join(runDir, 'trace.jsonl');
  try {
    if (!fs.existsSync(file)) return 0;
    const text = fs.readFileSync(file, 'utf8');
    let max = 0;
    for (const line of text.split(/\r?\n/)) {
      if (!line.trim()) continue;
      try {
        const row = JSON.parse(line);
        if (typeof row.id === 'number' && Number.isFinite(row.id) && row.id > max) max = row.id;
      } catch {
        /* skip a bad line */
      }
    }
    return max;
  } catch {
    return 0;
  }
}

function spawnTimedOut(spawned) {
  if (!spawned) return false;
  const code = spawned.error && spawned.error.code;
  if (code === 'ETIMEDOUT' || code === 'ETIME') return true;
  return false;
}

function captureRefusesInput(spec) {
  if (!spec) return null;
  if (spec.allowGameInput || spec.AllowGameInput) {
    return 'capture must not send input or take focus; refusing allowGameInput';
  }
  const action = spec.action != null ? String(spec.action) : '';
  if (INPUT_OBSERVE_ACTIONS.has(action)) {
    return `capture must not run observe.ps1 ${action}`;
  }
  if (spec.method != null && String(spec.method) !== CAPTURE_DEFAULT_METHOD) {
    return `capture must use -Method PrintWindow (focus-free); refusing ${spec.method}`;
  }
  if (spec.region === 'monitor' || spec.region === 'virtual') {
    return `capture -Region ${spec.region} is not a single window; PrintWindow cannot capture it`;
  }
  return null;
}

function emptyVisual(frames = []) {
  return {
    frames,
    verified: false,
    verifiedBy: null,
    verifiedAt: null,
    note: CAPTURE_INSPECT_NOTE,
  };
}

function frameEvidence(frame) {
  return {
    path: frame.path,
    bytes: frame.bytes,
    wallMs: frame.wallMs,
    offsetMs: frame.offsetMs,
    startedMs: frame.startedMs ?? null,
    endedMs: frame.endedMs ?? null,
    startedAt: frame.startedAt ?? frame.at ?? null,
    endedAt: frame.endedAt ?? null,
    at: frame.at ?? null,
    traceIdRange: frame.traceIdRange,
    exitCode: frame.exitCode,
    stdoutTail: frame.stdoutTail,
    stderrTail: frame.stderrTail,
    command: frame.command,
    timedOut: frame.timedOut === true,
    skipped: frame.skipped === true,
  };
}

function captureFlightState(ctx) {
  if (!ctx._captureFlight) ctx._captureFlight = { inFlight: 0 };
  return ctx._captureFlight;
}

function tryAcquireFlight(ctx, cap) {
  const flight = captureFlightState(ctx);
  if (flight.inFlight >= cap) return false;
  flight.inFlight += 1;
  return true;
}

function releaseFlight(ctx) {
  const flight = captureFlightState(ctx);
  flight.inFlight = Math.max(0, flight.inFlight - 1);
}

function captureRequireFrames(spec) {
  if (!spec) return false;
  return spec.requireFrames !== false;
}

function captureMaxCensusDelayMs(spec) {
  const n = Number(spec?.maxCensusDelayMs);
  return Number.isFinite(n) ? n : CAPTURE_DEFAULT_MAX_CENSUS_DELAY_MS;
}

function captureMaxInFlight(spec) {
  const n = Number(spec?.maxInFlight);
  return Number.isFinite(n) && n >= 1 ? Math.floor(n) : CAPTURE_DEFAULT_MAX_IN_FLIGHT;
}

function censusLateError(delayMs) {
  return `census late: ${Math.round(delayMs)} ms`;
}

function wrapSpawnSyncAsSpawn(spawnSyncFn) {
  return (cmd, args, opts) => {
    const child = new EventEmitter();
    const stdout = new PassThrough();
    const stderr = new PassThrough();
    child.stdout = stdout;
    child.stderr = stderr;
    child.pid = 0;
    child.exitCode = null;
    child.killed = false;
    child.kill = () => {
      child.killed = true;
    };
    queueMicrotask(() => {
      if (child.killed) {
        stdout.end();
        stderr.end();
        child.emit('close', null, 'SIGTERM');
        return;
      }
      let spawned;
      try {
        spawned = spawnSyncFn(cmd, args, opts);
      } catch (e) {
        stdout.end();
        stderr.end();
        child.emit('error', e);
        child.emit('close', null, null);
        return;
      }
      const out = spawned?.stdout;
      const err = spawned?.stderr;
      if (out) stdout.write(Buffer.isBuffer(out) ? out : String(out));
      if (err) stderr.write(Buffer.isBuffer(err) ? err : String(err));
      stdout.end();
      stderr.end();
      child.exitCode = spawned?.status ?? null;
      if (spawnTimedOut(spawned) && spawned.error) child.emit('error', spawned.error);
      child.emit('close', spawned?.status ?? null, spawned?.signal ?? null);
    });
    return child;
  };
}

function resolveCaptureSpawn(ctx) {
  if (typeof ctx.spawn === 'function') return ctx.spawn;
  if (typeof ctx.spawnSync === 'function') return wrapSpawnSyncAsSpawn(ctx.spawnSync);
  return spawn;
}

function captureRefusesUnderTest(ctx) {
  return !!(process.env.NODE_TEST_CONTEXT
    && typeof ctx.spawn !== 'function'
    && typeof ctx.spawnSync !== 'function');
}

function waitCaptureOffset(ctx, startAt, offsetMs) {
  const delay = Math.max(0, (startAt + offsetMs) - clockNowMs(ctx));
  if (typeof ctx.scheduleCapture === 'function') {
    return new Promise((resolve, reject) => {
      Promise.resolve(ctx.scheduleCapture(() => resolve(), delay)).catch(reject);
    });
  }
  if (process.env.NODE_TEST_CONTEXT) return Promise.resolve();
  if (delay <= 0) return Promise.resolve();
  return new Promise((resolve) => setTimeout(resolve, delay));
}

function delayReal(ctx, ms) {
  if (typeof ctx.delayReal === 'function') return ctx.delayReal(ms);
  if (ms <= 0) return Promise.resolve();
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function attachStreamTail(stream) {
  let buf = '';
  if (stream && typeof stream.on === 'function') {
    stream.on('data', (chunk) => {
      buf += Buffer.isBuffer(chunk) ? chunk.toString('utf8') : String(chunk);
      if (buf.length > CAPTURE_TAIL_CHARS) buf = buf.slice(buf.length - CAPTURE_TAIL_CHARS);
    });
  }
  return () => buf;
}

function hookChildExit(child) {
  if (!child || child._rigExitHooked) return;
  child._rigExitHooked = true;
  const mark = (code, signal) => {
    child._rigExited = true;
    if (code != null && child.exitCode == null) child.exitCode = code;
    if (signal != null && child.signalCode == null) child.signalCode = signal;
  };
  if (child.exitCode != null || child.signalCode != null) {
    child._rigExited = true;
    return;
  }
  if (typeof child.once === 'function') {
    child.once('close', mark);
    child.once('exit', mark);
  }
}

function childHasExited(child) {
  if (!child) return true;
  return child._rigExited === true || child.exitCode != null || child.signalCode != null;
}

function waitForSpawnedChild(child) {
  hookChildExit(child);
  return new Promise((resolve) => {
    let settled = false;
    const finish = (status, signal, error) => {
      if (settled) return;
      settled = true;
      resolve({ status, signal, error });
    };
    if (!child || typeof child.once !== 'function') {
      finish(null, null, new Error('capture spawn did not return a child process'));
      return;
    }
    if (childHasExited(child)) {
      finish(child.exitCode ?? null, child.signalCode ?? null, null);
      return;
    }
    child.once('error', (err) => finish(child.exitCode ?? null, null, err));
    child.once('close', (code, signal) => finish(code, signal, null));
  });
}

export function createChildRegistry() {
  return {
    entries: [],
    refuseNew: false,
    children: 0,
    killed: 0,
  };
}

export function childRegistry(ctx) {
  if (!ctx || typeof ctx !== 'object') return createChildRegistry();
  if (!ctx.children) ctx.children = createChildRegistry();
  return ctx.children;
}

export function isRunCancelled(ctx) {
  if (!ctx) return false;
  if (ctx.cancelled === true) return true;
  if (typeof ctx.cancelled === 'function') {
    try {
      if (ctx.cancelled()) return true;
    } catch { /* treat as not canceled */ }
  }
  if (ctx.children?.refuseNew) return true;
  if (typeof ctx.bridge?.isCancelled === 'function' && ctx.bridge.isCancelled()) return true;
  return false;
}

export function refuseNewChildren(ctx) {
  const reg = childRegistry(ctx);
  reg.refuseNew = true;
  return reg;
}

export function inferChildKind(cmd, args = []) {
  const base = String(cmd ?? '').replace(/\\/g, '/').toLowerCase();
  const argv = (args ?? []).map(String);
  if (base.includes('dotnet')) return 'snippet';
  const isPs = base.endsWith('powershell.exe') || base.endsWith('powershell') || base.includes('windowspowershell');
  if (isPs) {
    if (argv.includes('shot')) return 'capture';
    if (argv.some((a) => INPUT_OBSERVE_ACTIONS.has(String(a)))) return 'input';
  }
  return 'capture';
}

export function trackSpawnedChild(ctx, child, meta = {}) {
  if (!child) return null;
  const reg = childRegistry(ctx);
  const existing = reg.entries.find((e) => e.child === child);
  if (existing) return existing;
  hookChildExit(child);
  const entry = {
    child,
    kind: meta.kind ?? inferChildKind(meta.cmd, meta.args) ?? 'capture',
    killed: false,
  };
  reg.entries.push(entry);
  reg.children += 1;
  return entry;
}

function delayRealMs(ms) {
  const n = Number(ms);
  if (!Number.isFinite(n) || n <= 0) return Promise.resolve();
  return new Promise((resolve) => setTimeout(resolve, n));
}

export function makeCancelledChild() {
  const child = new EventEmitter();
  const stdout = new PassThrough();
  const stderr = new PassThrough();
  child.stdout = stdout;
  child.stderr = stderr;
  child.pid = 0;
  child.exitCode = null;
  child.killed = true;
  child.signalCode = 'SIGTERM';
  child.kill = () => {};
  queueMicrotask(() => {
    stdout.end();
    stderr.end();
    child.exitCode = 1;
    child.emit('close', 1, 'SIGTERM');
  });
  return child;
}

/**
 * Spawn through the injected or real spawn function and register the child.
 * Returns a already-closing dummy when the run has refused new children.
 */
export function spawnTracked(ctx, cmd, args, opts, meta = {}) {
  if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
    return makeCancelledChild();
  }
  const spawnFn = typeof ctx?.spawn === 'function' ? ctx.spawn : spawn;
  const child = spawnFn(cmd, args, opts);
  trackSpawnedChild(ctx, child, { kind: meta.kind ?? inferChildKind(cmd, args), cmd, args });
  if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
    try { child?.kill?.('SIGTERM'); } catch { /* gone */ }
  }
  return child;
}

/**
 * Kill every tracked helper (captures, observe.ps1 input, snippet builds),
 * wait a bounded grace, SIGKILL leftovers, and wait again. Refuses further
 * spawns. Returns `{requested, children, killed, waitedMs}` for the run report.
 */
export async function cancelTrackedChildren(ctx, opts = {}) {
  const t0 = Date.now();
  const reg = childRegistry(ctx);
  refuseNewChildren(ctx);
  if (ctx && typeof ctx === 'object') ctx.cancelled = true;
  const graceRaw = opts.graceMs ?? ctx?.childKillGraceMs ?? CHILD_KILL_GRACE_MS;
  const graceMs = Number.isFinite(Number(graceRaw)) ? Math.max(0, Number(graceRaw)) : CHILD_KILL_GRACE_MS;

  const liveEntries = () => reg.entries.filter((e) => e.child && !childHasExited(e.child));
  const waitAll = () => Promise.all(
    reg.entries.filter((e) => e.child).map((e) => waitForSpawnedChild(e.child)),
  );

  for (const entry of liveEntries()) {
    try {
      entry.child.kill?.('SIGTERM');
      entry.killed = true;
    } catch { /* gone */ }
  }

  if (liveEntries().length) {
    await Promise.race([
      waitAll(),
      delayRealMs(graceMs),
    ]);
  }

  for (const entry of liveEntries()) {
    try {
      entry.child.kill?.('SIGKILL');
      entry.killed = true;
    } catch { /* gone */ }
  }

  if (liveEntries().length) {
    await Promise.race([
      waitAll(),
      delayRealMs(graceMs),
    ]);
  }

  reg.killed = reg.entries.filter((e) => e.killed).length;
  return {
    requested: true,
    children: reg.children,
    killed: reg.killed,
    waitedMs: Date.now() - t0,
  };
}

function cancelledCaptureFrame(opts = {}) {
  return {
    ok: false,
    error: 'run cancelled',
    cancelled: true,
    discarded: !!opts.warmup,
    offsetMs: opts.offsetMs ?? 0,
    path: null,
    bytes: null,
    wallMs: 0,
    exitCode: null,
  };
}

function captureSummary(frames, { configured }) {
  if (!configured) {
    return {
      capture: { ...POOL_REUSE_CAPTURE, note: CAPTURE_INSPECT_NOTE },
      visual: emptyVisual([]),
    };
  }
  const saved = (frames ?? []).filter((f) => f && f.ok && f.path && f.bytes > 0 && !f.discarded);
  const paths = saved.map((f) => f.path);
  const capture = {
    available: paths.length > 0,
    note: CAPTURE_INSPECT_NOTE,
  };
  if (paths.length > 0) capture.paths = paths;
  else capture.reason = 'capture produced no saved frames';
  return { capture, visual: emptyVisual(saved.map(frameEvidence)) };
}

function capturePngPath(ctx, spec, { index = 0, warmup = false, name } = {}) {
  const outDir = captureOutDir(ctx);
  const iso = captureIsoStamp(clockNowMs(ctx));
  const label = warmup
    ? 'warmup'
    : captureSlug(name ?? spec.name ?? spec.record, 'capture');
  const file = `${label}-${iso}-f${index}.png`;
  return path.join(outDir, 'captures', file);
}

function timeoutFrame(job, ctx, timeoutMs, extra = {}) {
  const endedMs = clockNowMs(ctx);
  const startedMs = job?.startedMs ?? endedMs;
  return {
    ok: false,
    error: `capture timed out after ${timeoutMs}ms`,
    timedOut: true,
    path: job?.outPath ?? extra.path ?? null,
    bytes: job?.outPath ? fileSizeBytes(captureIo(ctx), job.outPath) : extra.bytes ?? null,
    wallMs: Math.max(0, endedMs - startedMs),
    offsetMs: job?.offsetMs ?? extra.offsetMs ?? 0,
    startedMs,
    endedMs,
    startedAt: captureIsoStamp(startedMs),
    endedAt: captureIsoStamp(endedMs),
    at: captureIsoStamp(startedMs),
    exitCode: extra.exitCode ?? null,
    stdoutTail: extra.stdoutTail ?? '',
    stderrTail: extra.stderrTail ?? '',
    command: extra.command,
    argv: extra.argv,
    discarded: extra.discarded === true,
  };
}

async function runSpawnedShot(spec, ctx, opts = {}) {
  if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
    return cancelledCaptureFrame(opts);
  }
  const refused = captureRefusesInput(spec);
  if (refused) {
    return { ok: false, error: refused, discarded: !!opts.warmup, offsetMs: opts.offsetMs ?? 0 };
  }
  if (captureRefusesUnderTest(ctx)) {
    return {
      ok: false,
      error: 'capture refuses to spawn observe.ps1 under the Node test runner without an injected spawnSync',
      discarded: !!opts.warmup,
      offsetMs: opts.offsetMs ?? 0,
    };
  }
  const repoRoot = ctx.repoRoot;
  if (!repoRoot) {
    return { ok: false, error: 'capture requires repoRoot', discarded: !!opts.warmup, offsetMs: opts.offsetMs ?? 0 };
  }
  const outDir = captureOutDir(ctx);
  if (!outDir) {
    return { ok: false, error: 'capture requires a run out dir', discarded: !!opts.warmup, offsetMs: opts.offsetMs ?? 0 };
  }
  const timeoutMs = Number.isFinite(Number(spec.timeoutMs))
    ? Math.max(1, Number(spec.timeoutMs))
    : CAPTURE_DEFAULT_TIMEOUT_MS;
  const window = spec.window ?? CAPTURE_DEFAULT_WINDOW;
  const region = spec.region ?? CAPTURE_DEFAULT_REGION;
  const maxWidth = spec.maxWidth == null ? CAPTURE_DEFAULT_MAX_WIDTH : spec.maxWidth;
  const powershell = spec.powershellPath ?? windowsPowerShell51Path();
  const script = observePs1Path(repoRoot);
  const io = captureIo(ctx);
  const outPath = opts.outPath ?? capturePngPath(ctx, spec, opts);
  if (opts.job) opts.job.outPath = outPath;
  const argv = observeShotArgv({
    script,
    outPath,
    window,
    region,
    maxWidth,
  });
  const command = formatCommandLine(powershell, argv);
  const spawnFn = resolveCaptureSpawn(ctx);
  if (spawnFn === spawn) {
    if (!fs.existsSync(powershell)) {
      return {
        ok: false,
        error: `Windows PowerShell 5.1 is required for screenshot interop; runtime not found: ${powershell}`,
        command,
        argv,
        discarded: !!opts.warmup,
        offsetMs: opts.offsetMs ?? 0,
      };
    }
    if (!fs.existsSync(script)) {
      return {
        ok: false,
        error: `observe.ps1 missing: ${script}`,
        command,
        argv,
        discarded: !!opts.warmup,
        offsetMs: opts.offsetMs ?? 0,
      };
    }
  }
  try { io.mkdirSync(path.dirname(outPath), { recursive: true }); } catch { /* spawn still records the path */ }
  const fromId = readLastTraceId(ctx);
  const startedMs = clockNowMs(ctx);
  if (opts.job) opts.job.startedMs = startedMs;
  const spawnOpts = {
    cwd: repoRoot,
    timeout: timeoutMs,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  };
  let child;
  try {
    if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
      return cancelledCaptureFrame(opts);
    }
    child = spawnFn(powershell, argv, spawnOpts);
  } catch (e) {
    const endedMs = clockNowMs(ctx);
    return {
      ok: false,
      error: e && e.message ? e.message : String(e),
      command,
      argv,
      path: outPath,
      wallMs: endedMs - startedMs,
      offsetMs: opts.offsetMs ?? 0,
      startedMs,
      endedMs,
      startedAt: captureIsoStamp(startedMs),
      endedAt: captureIsoStamp(endedMs),
      at: captureIsoStamp(startedMs),
      traceIdRange: { from: fromId, to: readLastTraceId(ctx) },
      exitCode: null,
      stdoutTail: '',
      stderrTail: '',
      discarded: !!opts.warmup,
    };
  }
  if (opts.job) opts.job.child = child;
  trackSpawnedChild(ctx, child, { kind: 'capture', cmd: powershell, args: argv });
  if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
    try { child?.kill?.('SIGTERM'); } catch { /* gone */ }
    return cancelledCaptureFrame(opts);
  }
  const readStdout = attachStreamTail(child?.stdout);
  const readStderr = attachStreamTail(child?.stderr);
  let shotTimer;
  const timeoutRace = new Promise((resolve) => {
    const fire = () => {
      try { child?.kill?.(); } catch { /* gone */ }
      resolve({ timedOut: true, status: null, signal: 'SIGTERM', error: { code: 'ETIMEDOUT' } });
    };
    shotTimer = setTimeout(fire, timeoutMs);
  });
  const ended = await Promise.race([
    waitForSpawnedChild(child).then((r) => ({ timedOut: false, ...r })),
    timeoutRace,
  ]);
  if (shotTimer) clearTimeout(shotTimer);
  const endedMs = clockNowMs(ctx);
  if (opts.job) opts.job.endedMs = endedMs;
  const toId = readLastTraceId(ctx);
  const stdoutTail = tailText(readStdout());
  const stderrTail = tailText(readStderr());
  const timedOut = ended.timedOut === true || (ended.error && (ended.error.code === 'ETIMEDOUT' || ended.error.code === 'ETIME'));
  const exitCode = ended.status == null ? null : ended.status;
  const base = {
    command,
    argv,
    path: outPath,
    wallMs: Math.max(0, endedMs - startedMs),
    offsetMs: opts.offsetMs ?? 0,
    startedMs,
    endedMs,
    startedAt: captureIsoStamp(startedMs),
    endedAt: captureIsoStamp(endedMs),
    at: captureIsoStamp(startedMs),
    traceIdRange: { from: fromId, to: toId },
    exitCode,
    stdoutTail,
    stderrTail,
    method: CAPTURE_DEFAULT_METHOD,
    window,
    region,
    maxWidth,
    discarded: false,
  };
  const discard = () => {
    try { io.unlinkSync(outPath); } catch { /* already gone */ }
  };
  if (timedOut) {
    if (opts.keep === false) discard();
    return {
      ok: false,
      error: `capture timed out after ${timeoutMs}ms`,
      timedOut: true,
      bytes: fileSizeBytes(io, outPath),
      ...base,
      discarded: opts.keep === false,
    };
  }
  if (exitCode !== 0) {
    if (opts.keep === false) discard();
    return {
      ok: false,
      error: `observe.ps1 shot exited ${exitCode}`,
      bytes: fileSizeBytes(io, outPath),
      ...base,
      discarded: opts.keep === false,
    };
  }
  const exists = typeof io.existsSync === 'function' ? io.existsSync(outPath) : fs.existsSync(outPath);
  if (!exists) {
    return {
      ok: false,
      error: `capture file missing: ${outPath}`,
      bytes: null,
      ...base,
      discarded: opts.keep === false,
    };
  }
  const bytes = fileSizeBytes(io, outPath);
  if (bytes == null || bytes <= 0) {
    if (opts.keep === false) discard();
    return {
      ok: false,
      error: `capture file empty: ${outPath}`,
      bytes: bytes ?? 0,
      ...base,
      discarded: opts.keep === false,
    };
  }
  if (opts.keep === false) {
    discard();
    const still = typeof io.existsSync === 'function' ? io.existsSync(outPath) : fs.existsSync(outPath);
    if (still) {
      return {
        ok: false,
        error: 'warmup frame was not discarded',
        bytes,
        ...base,
        discarded: false,
      };
    }
    return { ok: true, bytes, ...base, path: null, discarded: true };
  }
  return { ok: true, bytes, ...base };
}

function startCaptureBurst(spec, ctx, opts = {}) {
  const burst = spec.burst && typeof spec.burst === 'object' ? spec.burst : {};
  const count = Number.isFinite(Number(burst.count)) ? Math.max(1, Number(burst.count)) : 1;
  const intervalMs = Number.isFinite(Number(burst.intervalMs)) ? Math.max(0, Number(burst.intervalMs)) : 0;
  const delayMs = Number.isFinite(Number(spec.delayMs)) ? Math.max(0, Number(spec.delayMs)) : 0;
  const cap = captureMaxInFlight(spec);
  const startAt = opts.startAt ?? clockNowMs(ctx);
  const jobs = [];
  for (let i = 0; i < count; i++) {
    const offsetMs = delayMs + i * intervalMs;
    const job = {
      offsetMs,
      index: i,
      child: null,
      startedMs: null,
      endedMs: null,
      outPath: null,
      result: null,
    };
    job.promise = (async () => {
      await waitCaptureOffset(ctx, startAt, offsetMs);
      if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
        const skipped = cancelledCaptureFrame({ offsetMs, warmup: opts.warmup });
        job.result = skipped;
        return skipped;
      }
      await waitWhileMainThreadInFlight(ctx, captureGateSide(spec, ctx));
      if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
        const skipped = cancelledCaptureFrame({ offsetMs, warmup: opts.warmup });
        job.result = skipped;
        return skipped;
      }
      if (!tryAcquireFlight(ctx, cap)) {
        const now = clockNowMs(ctx);
        const skipped = {
          ok: false,
          error: 'capture in-flight cap',
          skipped: true,
          offsetMs,
          startedMs: now,
          endedMs: now,
          startedAt: captureIsoStamp(now),
          endedAt: captureIsoStamp(now),
          at: captureIsoStamp(now),
          path: null,
          bytes: null,
          wallMs: 0,
          exitCode: null,
          discarded: opts.keep === false,
        };
        job.result = skipped;
        return skipped;
      }
      try {
        const result = await runSpawnedShot(spec, ctx, {
          index: i,
          offsetMs,
          keep: opts.keep !== false,
          name: opts.name,
          warmup: opts.warmup === true,
          outPath: opts.outPath,
          job,
        });
        job.result = result;
        return result;
      } finally {
        releaseFlight(ctx);
      }
    })();
    jobs.push(job);
  }
  return { jobs, startAt, spec, cap, collected: false };
}

async function collectCaptureBurst(handle, ctx, spec) {
  if (!handle?.jobs?.length) return { ok: true, frames: [] };
  if (handle.collected && handle.frames) {
    const failed = handle.frames.find((f) => f && !f.ok);
    return { ok: !failed, error: failed?.error, frames: handle.frames };
  }
  const boundRaw = spec?.collectTimeoutMs;
  const boundMs = Number.isFinite(Number(boundRaw))
    ? Math.max(0, Number(boundRaw))
    : (Number.isFinite(Number(spec?.timeoutMs)) ? Number(spec.timeoutMs) : CAPTURE_DEFAULT_TIMEOUT_MS);
  const all = Promise.all(handle.jobs.map((job) => job.promise));
  let frames;
  if (!Number.isFinite(Number(boundRaw))) {
    frames = await all;
  } else {
    let timedOut = false;
    const winner = await Promise.race([
      all.then((rows) => ({ type: 'done', rows })),
      delayReal(ctx, boundMs).then(() => {
        timedOut = true;
        return { type: 'timeout' };
      }),
    ]);
    if (winner.type === 'done') {
      frames = winner.rows;
    } else {
      frames = handle.jobs.map((job) => {
        if (job.result) return { ...job.result, timedOut: job.result.timedOut === true || timedOut };
        try { job.child?.kill?.(); } catch { /* gone */ }
        job.promise.catch(() => {});
        return timeoutFrame(job, ctx, boundMs);
      });
    }
  }
  handle.collected = true;
  handle.frames = frames;
  const failed = (frames ?? []).find((f) => f && !f.ok);
  return { ok: !failed, error: failed?.error, frames: frames ?? [] };
}

async function takeOneShot(spec, ctx, opts = {}) {
  const handle = startCaptureBurst({ ...spec, burst: undefined, delayMs: 0 }, ctx, {
    ...opts,
    startAt: clockNowMs(ctx),
  });
  const collected = await collectCaptureBurst(handle, ctx, spec);
  return collected.frames[0] ?? { ok: false, error: 'capture produced no saved frames', discarded: !!opts.warmup };
}

function summarizeCaptureFrames(frames, { configured, error }) {
  const summary = captureSummary(frames, { configured });
  const failed = (frames ?? []).find((f) => f && !f.ok);
  return {
    ok: configured ? !failed : true,
    error: error ?? failed?.error,
    frames: frames ?? [],
    ...summary,
  };
}

async function runCaptureWindow(spec, ctx, opts = {}) {
  if (!spec) {
    return { ok: true, frames: [], ...captureSummary([], { configured: false }) };
  }
  const refused = captureRefusesInput(spec);
  if (refused) {
    return { ok: false, error: refused, frames: [], ...captureSummary([], { configured: true }) };
  }
  const startAt = opts.startAt ?? clockNowMs(ctx);
  const handle = startCaptureBurst(spec, ctx, { ...opts, startAt });
  const waitMs = Number.isFinite(Number(spec.waitMs)) ? Math.max(0, Number(spec.waitMs)) : null;
  let censusDelayMs = null;
  if (waitMs != null) {
    const elapsed = clockNowMs(ctx) - startAt;
    if (elapsed < waitMs) await ctx.clock.sleep(waitMs - elapsed);
    censusDelayMs = clockNowMs(ctx) - startAt;
    const maxCensusDelayMs = captureMaxCensusDelayMs(spec);
    if (censusDelayMs > maxCensusDelayMs) {
      const collected = await collectCaptureBurst(handle, ctx, spec);
      return {
        ...summarizeCaptureFrames(collected.frames, { configured: true, error: censusLateError(censusDelayMs) }),
        censusDelayMs,
        maxCensusDelayMs,
      };
    }
  }
  const collected = await collectCaptureBurst(handle, ctx, spec);
  return {
    ...summarizeCaptureFrames(collected.frames, { configured: true, error: collected.error }),
    censusDelayMs,
  };
}

async function stepCapturePrepare(step, ctx) {
  const shot = await takeOneShot({ ...step, burst: undefined, delayMs: 0 }, ctx, {
    index: 0,
    offsetMs: 0,
    keep: false,
    name: step.name ?? 'capture-warmup',
    warmup: true,
  });
  const recorded = {
    warmupMs: shot.wallMs ?? 0,
    discarded: true,
    ok: shot.ok,
    error: shot.error,
    exitCode: shot.exitCode,
    traceIdRange: shot.traceIdRange,
    command: shot.command,
  };
  if (step.record) ctx.records[step.record] = recorded;
  return {
    ok: shot.ok,
    error: shot.error,
    warmupMs: recorded.warmupMs,
    discarded: true,
    path: null,
    bytes: null,
    wallMs: recorded.warmupMs,
    traceIdRange: shot.traceIdRange,
    exitCode: shot.exitCode,
    stdoutTail: shot.stdoutTail,
    stderrTail: shot.stderrTail,
    command: shot.command,
    argv: shot.argv,
    note: 'warmup shot is discarded; it is not evidence',
  };
}

function mapCaptureFrames(frames) {
  return (frames ?? []).map((f) => (f.ok ? { ...frameEvidence(f), ok: true } : {
    ...frameEvidence(f),
    error: f.error,
    ok: false,
  }));
}

async function stepCapture(step, ctx) {
  if (step.prepare === true) return stepCapturePrepare(step, ctx);
  const ran = await runCaptureWindow(step, ctx, {
    startAt: clockNowMs(ctx),
    name: step.name ?? step.record ?? 'capture',
    keep: true,
  });
  const frames = ran.frames ?? [];
  const first = frames.find((f) => f && f.ok) ?? frames[0];
  const requireFrames = captureRequireFrames(step);
  const framesFailed = frames.some((f) => f && !f.ok);
  const late = ran.error && String(ran.error).startsWith('census late:');
  let ok = ran.ok;
  let error = ran.error;
  if (!late && framesFailed && !requireFrames) {
    ok = true;
    error = undefined;
  }
  const out = {
    ok,
    error,
    frames: mapCaptureFrames(frames),
    capture: ran.capture,
    visual: ran.visual ?? emptyVisual(frames.filter((f) => f.ok).map(frameEvidence)),
    path: first?.path ?? null,
    bytes: first?.bytes ?? null,
    wallMs: first?.wallMs ?? null,
    startedMs: first?.startedMs ?? null,
    endedMs: first?.endedMs ?? null,
    startedAt: first?.startedAt ?? first?.at ?? null,
    endedAt: first?.endedAt ?? null,
    traceIdRange: first?.traceIdRange ?? null,
    exitCode: first?.exitCode ?? null,
    stdoutTail: first?.stdoutTail ?? '',
    stderrTail: first?.stderrTail ?? '',
    command: first?.command ?? null,
    argv: first?.argv ?? null,
    censusDelayMs: ran.censusDelayMs ?? null,
    maxCensusDelayMs: ran.maxCensusDelayMs,
    note: CAPTURE_INSPECT_NOTE,
  };
  if (first?.cancelled === true || isRunCancelled(ctx)) out.cancelled = true;
  if (step.record) ctx.records[step.record] = out;
  return out;
}

async function stepPoolReuse(step, ctx) {
  const retries = Number.isFinite(Number(step.retries)) ? Math.max(1, Number(step.retries)) : 3;
  const waitMs = step.waitMs ?? 1500;
  const retryWaitMs = step.retryWaitMs ?? 20000;
  const dash = step.dash ?? '.frostdash dash';
  const snippetStep = step.snippet ?? {};
  const captureConfigured = step.capture != null && step.capture !== false;
  const captureSpec = captureConfigured ? step.capture : null;
  const requireFrames = captureRequireFrames(captureSpec);
  const maxCensusDelayMs = captureMaxCensusDelayMs(step.maxCensusDelayMs != null ? step : captureSpec);
  const savedFrames = [];
  const allCaptureFrames = [];
  const attemptResults = [];
  let lastAssert = null;
  let lastCensus = null;
  let lastCensusDelayMs = null;
  let leftoverHandle = null;

  const reuseFields = (attempt, asserted = null) => {
    const reused = asserted?.reused ?? [];
    const reusedUntinted = asserted?.qualifiedReused ?? reused.length;
    const summary = captureSummary(savedFrames, { configured: captureConfigured });
    return {
      attempt,
      retries,
      retriesUsed: attempt,
      capture: summary.capture,
      visual: summary.visual,
      frames: mapCaptureFrames(allCaptureFrames),
      reused,
      qualifiedTinted: asserted?.qualifiedTinted ?? 0,
      qualifiedReused: reusedUntinted,
      reusedUntinted,
      censusDelayMs: lastCensusDelayMs,
      maxCensusDelayMs: captureConfigured || step.maxCensusDelayMs != null ? maxCensusDelayMs : undefined,
    };
  };

  const collectHandle = async (handle) => {
    if (!handle || handle.collected) {
      return { ok: true, frames: handle?.frames ?? [] };
    }
    const collected = await collectCaptureBurst(handle, ctx, captureSpec ?? {});
    for (const frame of collected.frames ?? []) {
      allCaptureFrames.push(frame);
      if (frame && frame.ok && frame.path && !frame.discarded) savedFrames.push(frame);
    }
    return collected;
  };

  for (let attempt = 1; attempt <= retries; attempt++) {
    if (isRunCancelled(ctx)) {
      await collectHandle(leftoverHandle);
      leftoverHandle = null;
      return {
        ok: false,
        error: 'run cancelled',
        cancelled: true,
        ...reuseFields(attempt),
        attemptResults,
      };
    }
    if (attempt > 1) {
      await ctx.clock.sleep(retryWaitMs);
      if (leftoverHandle) {
        await collectHandle(leftoverHandle);
        leftoverHandle = null;
      }
    }
    const cmd = await stepCommand({ kind: 'command', text: dash }, ctx);
    if (!cmd.ok) {
      await collectHandle(leftoverHandle);
      return {
        ok: false,
        error: cmd.error ?? 'dash command failed',
        ...reuseFields(attempt),
        attemptResults,
      };
    }
    const dashAt = clockNowMs(ctx);
    let captureHandle = null;
    if (captureConfigured) {
      captureHandle = startCaptureBurst(captureSpec, ctx, {
        startAt: dashAt,
        name: step.name ?? step.record ?? 'poolReuse',
        keep: true,
      });
      leftoverHandle = captureHandle;
    }
    const elapsed = clockNowMs(ctx) - dashAt;
    if (elapsed < waitMs) await ctx.clock.sleep(waitMs - elapsed);
    const censusDelayMs = clockNowMs(ctx) - dashAt;
    lastCensusDelayMs = censusDelayMs;
    if (censusDelayMs > maxCensusDelayMs) {
      const collected = await collectHandle(captureHandle);
      leftoverHandle = null;
      return {
        ok: false,
        error: censusLateError(censusDelayMs),
        censusDelayMs,
        maxCensusDelayMs,
        captureCollect: collected,
        ...reuseFields(attempt),
        attemptResults,
      };
    }
    const snip = await stepSnippet({
      kind: 'snippet',
      name: step.name ?? 'pool census reuse',
      side: snippetStep.side ?? 'client',
      file: snippetStep.file ?? step.file,
      expect: snippetStep.expect ?? 'POOLID summary renderers=',
      timeoutMs: snippetStep.timeoutMs ?? step.timeoutMs ?? 20000,
      collectPool: true,
      record: step.record,
    }, ctx);
    const collected = await collectHandle(captureHandle);
    leftoverHandle = null;
    const frameFail = collected.frames?.find((f) => f && !f.ok);
    if (frameFail && requireFrames) {
      return {
        ok: false,
        error: collected.error ?? frameFail.error ?? 'capture failed',
        censusDelayMs,
        ...reuseFields(attempt),
        snippet: snip,
        attemptResults,
      };
    }
    if (snip.compile || (snip.code != null && snip.code !== 0)) {
      return {
        ok: false,
        error: 'snippet compile failed',
        censusDelayMs,
        ...reuseFields(attempt),
        snippet: snip,
        attemptResults,
      };
    }
    if (snip.error === 'snippet logs not seen') {
      return {
        ok: false,
        error: 'timeout',
        censusDelayMs,
        ...reuseFields(attempt),
        snippet: snip,
        attemptResults,
      };
    }
    if (snip.error === 'snippet DLL hash mismatch'
      || snip.error === 'snippet DLL older than copy time'
      || snip.error === 'snippet DLL mtime missing after build'
      || snip.error === 'snippet DLL missing after build'
      || snip.error === 'snippet not prepared'
      || snip.error === 'snippet source changed after prepare'
      || snip.error === 'snippet DLL changed after prepare'
      || snip.error === 'scratch error'
      || snip.error === 'snippet expect mismatch') {
      return {
        ok: false,
        error: snip.error,
        censusDelayMs,
        ...reuseFields(attempt),
        snippet: snip,
        attemptResults,
      };
    }
    const census = snip.census ?? parsePoolCensus([]);
    lastCensus = census;
    if ((census.malformed ?? []).length) {
      return {
        ok: false,
        error: 'malformed census line',
        censusDelayMs,
        ...reuseFields(attempt),
        census,
        attemptResults,
      };
    }
    const asserted = assertPoolCensus({
      phase: 'reuse',
      baselineRecord: step.baselineRecord,
      tintedRecord: step.tintedRecord,
      overrideColor: step.overrideColor,
    }, census, ctx);
    lastAssert = asserted;
    attemptResults.push({
      attempt,
      ok: asserted.ok,
      error: asserted.error,
      reused: asserted.reused,
      qualifiedTinted: asserted.qualifiedTinted,
      qualifiedReused: asserted.qualifiedReused,
      reusedUntinted: asserted.qualifiedReused,
      censusDelayMs,
    });
    if (asserted.ok) {
      attachCensusRecord(step, ctx, census, asserted);
      return {
        ok: true,
        censusDelayMs,
        ...reuseFields(attempt, asserted),
        informational: asserted.informational,
        census,
        assert: asserted,
        attemptResults,
      };
    }
    if (asserted.error === 'malformed census line') {
      return {
        ok: false,
        error: asserted.error,
        censusDelayMs,
        ...reuseFields(attempt, asserted),
        assert: asserted,
        attemptResults,
      };
    }
  }

  const error = lastAssert?.error === 'fresh allocation without reused participant'
    ? lastAssert.error
    : 'reuse not observed';
  if (lastCensus && lastAssert) attachCensusRecord(step, ctx, lastCensus, lastAssert);
  await collectHandle(leftoverHandle);
  return {
    ok: false,
    error,
    attempts: retries,
    censusDelayMs: lastCensusDelayMs,
    ...reuseFields(retries, lastAssert),
    assert: lastAssert,
    informational: lastAssert?.informational,
    attemptResults,
  };
}

export const SOAK_DEFAULT_PING_INTERVAL_SECONDS = 30;
export const SOAK_DEFAULT_LOGS_INTERVAL_SECONDS = 15;
export const SOAK_DEFAULT_BRIDGE_STATUS_INTERVAL_SECONDS = 10;
export const SOAK_DEFAULT_LOGS_MAX = 20;
export const SOAK_DEFAULT_LOGS_FILTER = 'Progenitor';
export const SOAK_NEWEST_LINE_LIMIT = 5;
export const SOAK_DEFAULT_DUMP_AFTER_SECONDS = 15;
export const SOAK_DEFAULT_FAIL_AFTER_SECONDS = 60;
export const SOAK_FREEZE_DUMP_REASON = 'rig freeze';

export function getByPath(obj, path) {
  if (path == null || path === '') return obj;
  const parts = String(path).split('.');
  let cur = obj;
  for (const part of parts) {
    if (cur == null) return undefined;
    if (Array.isArray(cur) && /^\d+$/.test(part)) cur = cur[Number(part)];
    else cur = cur[part];
  }
  return cur;
}

function isNeqExpect(want) {
  return !!want
    && typeof want === 'object'
    && !Array.isArray(want)
    && Object.prototype.hasOwnProperty.call(want, 'neq')
    && Object.keys(want).length === 1;
}

function valuesEqual(got, want) {
  if (Object.is(got, want)) return true;
  if (want != null && got != null && typeof want === 'object') {
    try {
      return JSON.stringify(got) === JSON.stringify(want);
    } catch {
      return false;
    }
  }
  return false;
}

function stringifyForExpect(value) {
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

/** `expect` is `{ path: value, ... }` or a regex (string / RegExp) matched against JSON.stringify(result). */
export function matchExpect(expect, result) {
  if (expect == null) return { ok: true };
  if (typeof expect === 'string' || expect instanceof RegExp) {
    const json = stringifyForExpect(result);
    const re = expect instanceof RegExp ? expect : new RegExp(expect);
    if (!re.test(json)) {
      return { ok: false, error: `expect regex ${re} did not match ${json}` };
    }
    return { ok: true };
  }
  if (typeof expect !== 'object' || Array.isArray(expect)) {
    return { ok: false, error: `expect must be a path map or a regex, got ${typeof expect}` };
  }
  for (const [path, want] of Object.entries(expect)) {
    const got = getByPath(result, path);
    if (isNeqExpect(want)) {
      if (valuesEqual(got, want.neq)) {
        return {
          ok: false,
          error: `expect ${path}: wanted not ${stringifyForExpect(want.neq)}, got ${stringifyForExpect(got)}`,
        };
      }
      continue;
    }
    if (!valuesEqual(got, want)) {
      return {
        ok: false,
        error: `expect ${path}: wanted ${stringifyForExpect(want)}, got ${stringifyForExpect(got)}`,
      };
    }
  }
  return { ok: true };
}

function lookupRecordPath(ctx, spec) {
  const text = String(spec ?? '');
  const dot = text.indexOf('.');
  const name = dot < 0 ? text : text.slice(0, dot);
  const rest = dot < 0 ? '' : text.slice(dot + 1);
  if (!name) throw new Error('@record: missing name');
  if (!(name in (ctx.records ?? {}))) throw new Error(`@record:${name} is missing`);
  const rec = ctx.records[name];
  if (!rest) return rec;
  const got = getByPath(rec, rest);
  if (got === undefined) throw new Error(`@record:${name}.${rest} is missing`);
  return got;
}

export function expandArgRefs(value, ctx) {
  if (typeof value === 'string' && value.startsWith('@record:')) {
    return lookupRecordPath(ctx, value.slice('@record:'.length));
  }
  if (Array.isArray(value)) return value.map((v) => expandArgRefs(v, ctx));
  if (value && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = expandArgRefs(v, ctx);
    return out;
  }
  return value;
}

function resolveOpArgs(step, ctx) {
  const args = expandArgRefs(step.args ?? {}, ctx);
  if (String(step.op ?? '').toLowerCase() === 'mods_ui'
    && args && typeof args === 'object'
    && args.action === 'open'
    && (args.site == null || args.site === '')) {
    const site = step.site ?? ctx.caseSite ?? ctx.caseDoc?.site;
    if (site) args.site = site;
  }
  return args;
}

function errorText(err) {
  if (err == null) return 'unknown';
  if (typeof err === 'string') return err;
  if (typeof err === 'object' && err.message) return err.message;
  return String(err);
}

function resolveOpExpect(step, ctx) {
  if (step.expect == null) return null;
  return expandArgRefs(step.expect, ctx);
}

async function readOpSince(ctx, side) {
  try {
    const snap = await ctx.bridge.call(side, 'logs', { since: -1, max: 1, filter: '' });
    if (typeof snap?.latestSeq === 'number') return snap.latestSeq;
  } catch {
    /* still run the op; since stays -1 */
  }
  return -1;
}

function stampOpRecord(step, ctx, result, since, extra = {}) {
  if (!step.record) return;
  const seenAt = clockNowMs(ctx);
  if (result && typeof result === 'object' && !Array.isArray(result)) {
    ctx.records[step.record] = { ...result, since, logSeq: since, seenAt, ...extra };
  } else {
    ctx.records[step.record] = { value: result, since, logSeq: since, seenAt, ...extra };
  }
}

/** `poll: {intervalMs, timeoutMs}` or the older `pollMs` + `timeoutMs`. */
function opPollSpec(step) {
  const nested = step.poll && typeof step.poll === 'object' && !Array.isArray(step.poll)
    ? step.poll
    : null;
  let intervalMs = Number.NaN;
  let timeoutMs = Number.NaN;
  if (nested) {
    intervalMs = Number(nested.intervalMs);
    timeoutMs = Number(nested.timeoutMs);
  }
  if (!Number.isFinite(intervalMs)) intervalMs = Number(step.pollMs);
  if (!Number.isFinite(timeoutMs)) timeoutMs = Number(step.timeoutMs);
  const polling = Number.isFinite(intervalMs) && intervalMs >= 0 && step.expect != null;
  const loopTimeoutMs = Number.isFinite(timeoutMs) ? timeoutMs : (polling ? 30000 : null);
  return { intervalMs, timeoutMs, loopTimeoutMs, polling };
}

async function stepOp(step, ctx) {
  const side = step.side ?? ctx.side;
  const op = step.op;
  if (!op) return { ok: false, error: 'op step requires op' };
  if (!side) return { ok: false, error: 'op step requires side' };
  let args;
  try {
    args = resolveOpArgs(step, ctx);
  } catch (e) {
    return { ok: false, error: errorText(e), side, op };
  }
  const { intervalMs: pollMs, timeoutMs: stepTimeoutMs, loopTimeoutMs, polling } = opPollSpec(step);
  const tolerateDownMs = Number(step.tolerateBridgeDownMs);
  const tolerateDown = Number.isFinite(tolerateDownMs) && tolerateDownMs > 0;
  const callOpts = {};
  if (!polling && Number.isFinite(stepTimeoutMs)) callOpts.timeoutMs = stepTimeoutMs;
  const since = step.record ? await readOpSince(ctx, side) : -1;
  const startedMs = clockNowMs(ctx);
  const elapsed = () => clockNowMs(ctx) - startedMs;
  const deadlineMs = polling && loopTimeoutMs != null ? startedMs + loopTimeoutMs : null;
  const downDeadlineMs = tolerateDown ? startedMs + tolerateDownMs : null;
  const retrySleepMs = Math.max(1, Number.isFinite(pollMs) && pollMs >= 0 ? pollMs : 250);

  let result;
  let matched = { ok: true };
  let polls = 0;
  let downStartedMs = null;
  while (true) {
    polls += 1;
    try {
      result = await ctx.bridge.call(side, op, args, callOpts);
    } catch (e) {
      const error = errorText(e);
      const down = isBridgeDownError(e);
      const now = clockNowMs(ctx);
      if (down && tolerateDown) {
        if (downStartedMs == null) downStartedMs = now;
        const withinDown = downDeadlineMs == null || now < downDeadlineMs;
        const withinLoop = deadlineMs == null || now < deadlineMs;
        if (withinDown && withinLoop) {
          await ctx.clock.sleep(retrySleepMs);
          continue;
        }
      }
      const out = {
        ok: false,
        error,
        side,
        op,
        args,
        timeout: !!(e && e.timeout) || /timed out/.test(error),
        suspect: !!(e && e.suspect) || /main thread suspect/.test(error),
        bridgeDown: down,
        polls,
        elapsedMs: elapsed(),
      };
      stampOpRecord(step, ctx, out, since, { elapsedMs: out.elapsedMs, polls });
      return out;
    }
    downStartedMs = null;
    if (step.expect == null && result && typeof result === 'object' && result.ok === false) {
      const elapsedMs = elapsed();
      stampOpRecord(step, ctx, result, since, { elapsedMs, polls });
      return {
        ok: false,
        error: result.error ?? `${op} returned ok:false`,
        side,
        op,
        args,
        result,
        polls,
        elapsedMs,
      };
    }
    let expect;
    try {
      expect = resolveOpExpect(step, ctx);
    } catch (e) {
      const elapsedMs = elapsed();
      stampOpRecord(step, ctx, result, since, { elapsedMs, polls });
      return { ok: false, error: errorText(e), side, op, args, result, polls, elapsedMs };
    }
    matched = matchExpect(expect, result);
    if (matched.ok) break;
    if (!polling || deadlineMs == null || clockNowMs(ctx) >= deadlineMs) {
      const elapsedMs = elapsed();
      stampOpRecord(step, ctx, result, since, { elapsedMs, polls });
      return {
        ok: false,
        error: matched.error,
        side,
        op,
        args,
        result,
        expect,
        polls,
        elapsedMs,
      };
    }
    await ctx.clock.sleep(pollMs);
  }
  const elapsedMs = elapsed();
  stampOpRecord(step, ctx, result, since, { elapsedMs, polls });
  return {
    ok: true,
    side,
    op,
    args,
    result,
    record: step.record,
    polls,
    elapsedMs,
  };
}

export const CORE_SWAP_POLL_MS = 3000;
export const CORE_SWAP_DEFAULT_MAX_WAIT_MS = 90000;
export const CORE_SWAP_DEFAULT_GENERATION_DELTA = 1;

function coreStatusFrom(result) {
  if (!result || typeof result !== 'object') return {};
  if (result.status && typeof result.status === 'object') return result.status;
  return result;
}

const PREVIOUS_GENERATION_STILL_ALIVE = 'previous generation still alive';

/** `collected` from reload_status, else lastReload, when either object carries the field. */
function collectedFromReload(status, lastReload) {
  if (status && typeof status === 'object' && Object.prototype.hasOwnProperty.call(status, 'collected')) {
    return status.collected;
  }
  if (lastReload && typeof lastReload === 'object' && Object.prototype.hasOwnProperty.call(lastReload, 'collected')) {
    return lastReload.collected;
  }
  return null;
}

function recordPreviousAliveFinding(ctx) {
  if (!Array.isArray(ctx?.findings)) return;
  if (!ctx.findings.includes(PREVIOUS_GENERATION_STILL_ALIVE)) {
    ctx.findings.push(PREVIOUS_GENERATION_STILL_ALIVE);
  }
}

function imageHashesFromPing(ping) {
  const list = Array.isArray(ping?.images) ? ping.images : [];
  const hashOf = (row) => (row && typeof row === 'object' ? (row.sha256 ?? row.hash ?? null) : null);
  const nameOf = (row) => String(row?.file ?? row?.name ?? row?.dll ?? '');
  const stem = (name) => String(name).replace(/\\/g, '/').split('/').pop().replace(/\.dll$/i, '');
  const find = (test) => {
    const row = list.find((r) => test(nameOf(r), stem(nameOf(r))));
    return hashOf(row);
  };
  return {
    core: ping?.coreHash ?? ping?.coreSha256 ?? find((_, n) => /^(Core|Progenitor(\.Core)?)$/i.test(n)) ?? null,
    dev: ping?.devHash ?? ping?.devSha256 ?? find((raw, n) => /Dev/i.test(raw) || /^Progenitor\.Dev$/i.test(n)) ?? null,
    sample: ping?.sampleHash ?? ping?.sampleSha256 ?? find((raw, n) => /Sample/i.test(raw) || /^Progenitor\.Sample$/i.test(n)) ?? null,
  };
}

function refusalWindowFrom(refusals) {
  if (!refusals.length) return null;
  return {
    first: refusals[0].at,
    last: refusals[refusals.length - 1].at,
    attempts: refusals.length,
  };
}

function stampCoreSwapRecord(step, ctx, out) {
  if (step.record) ctx.records[step.record] = out;
}

async function stepCoreSwap(step, ctx) {
  const side = step.side ?? ctx.side;
  if (!side) return { ok: false, error: 'coreSwap requires side' };
  const expectGenerationDelta = Number.isFinite(Number(step.expectGenerationDelta))
    ? Number(step.expectGenerationDelta)
    : CORE_SWAP_DEFAULT_GENERATION_DELTA;
  const maxWaitMs = Number.isFinite(Number(step.maxWaitMs))
    ? Number(step.maxWaitMs)
    : CORE_SWAP_DEFAULT_MAX_WAIT_MS;
  const pollMs = Number.isFinite(Number(step.pollMs)) && Number(step.pollMs) >= 0
    ? Number(step.pollMs)
    : CORE_SWAP_POLL_MS;

  let before;
  try {
    before = await ctx.bridge.call(side, 'reload_status', {});
  } catch (e) {
    const out = {
      ok: false,
      error: `coreSwap reload_status (before) failed: ${errorText(e)}`,
      side,
      timeout: !!(e && e.timeout),
      suspect: !!(e && e.suspect),
      bridgeDown: isBridgeDownError(e),
    };
    stampCoreSwapRecord(step, ctx, out);
    return out;
  }
  const beforeStatus = coreStatusFrom(before);
  const generationBefore = beforeStatus.generation;
  if (generationBefore == null) {
    const out = {
      ok: false,
      error: 'coreSwap reload_status (before) had no generation',
      side,
      result: before,
    };
    stampCoreSwapRecord(step, ctx, out);
    return out;
  }

  let scheduled;
  try {
    scheduled = await ctx.bridge.call(side, 'core_reload', {});
  } catch (e) {
    const out = {
      ok: false,
      error: `coreSwap core_reload failed: ${errorText(e)}`,
      side,
      generationBefore,
      timeout: !!(e && e.timeout),
      suspect: !!(e && e.suspect),
      bridgeDown: isBridgeDownError(e),
    };
    stampCoreSwapRecord(step, ctx, out);
    return out;
  }
  if (scheduled && typeof scheduled === 'object' && scheduled.ok === false) {
    const out = {
      ok: false,
      error: scheduled.error ?? 'core_reload returned ok:false',
      side,
      generationBefore,
      scheduled,
    };
    stampCoreSwapRecord(step, ctx, out);
    return out;
  }
  if (scheduled?.scheduled !== true) {
    const out = {
      ok: false,
      error: 'core_reload did not return scheduled:true',
      side,
      generationBefore,
      scheduled,
    };
    stampCoreSwapRecord(step, ctx, out);
    return out;
  }

  const waitStartedMs = clockNowMs(ctx);
  const deadlineMs = waitStartedMs + maxWaitMs;
  const refusals = [];
  let polls = 0;
  let lastStatus = null;
  let generationAfter = null;
  let alcName = beforeStatus.alcName ?? null;
  let lastReload = beforeStatus.lastReload ?? null;
  let previousAlive = beforeStatus.previousAlive ?? null;
  let collected = collectedFromReload(beforeStatus, lastReload);
  let inFlight = null;

  while (true) {
    const now = clockNowMs(ctx);
    if (now >= deadlineMs) {
      const out = {
        ok: false,
        error: `coreSwap timed out after ${maxWaitMs} ms waiting for generation ${generationBefore} + ${expectGenerationDelta}`,
        side,
        generationBefore,
        generationAfter,
        alcName,
        lastReloadOutcome: lastReload?.outcome ?? null,
        lastReload,
        previousAlive,
        collected,
        inFlight,
        refusalWindow: refusalWindowFrom(refusals),
        polls,
        elapsedMs: now - waitStartedMs,
        scheduled,
      };
      stampCoreSwapRecord(step, ctx, out);
      return out;
    }

    polls += 1;
    try {
      lastStatus = await ctx.bridge.call(side, 'reload_status', {});
    } catch (e) {
      if (isBridgeDownError(e)) {
        refusals.push({ at: new Date().toISOString(), atMs: clockNowMs(ctx) });
        await ctx.clock.sleep(Math.max(1, pollMs));
        continue;
      }
      const error = errorText(e);
      const out = {
        ok: false,
        error: `coreSwap reload_status poll failed: ${error}`,
        side,
        generationBefore,
        timeout: !!(e && e.timeout) || /timed out/.test(error),
        suspect: !!(e && e.suspect) || /main thread suspect/.test(error),
        bridgeDown: false,
        refusalWindow: refusalWindowFrom(refusals),
        polls,
        elapsedMs: clockNowMs(ctx) - waitStartedMs,
      };
      stampCoreSwapRecord(step, ctx, out);
      return out;
    }

    const status = coreStatusFrom(lastStatus);
    generationAfter = status.generation;
    alcName = status.alcName ?? alcName;
    lastReload = status.lastReload ?? lastReload;
    previousAlive = status.previousAlive ?? previousAlive;
    collected = collectedFromReload(status, lastReload);
    inFlight = status.inFlight;
    const outcome = lastReload?.outcome ?? null;
    const movedBy = generationAfter != null && generationBefore != null
      ? Number(generationAfter) - Number(generationBefore)
      : null;

    if (inFlight === false) {
      if (outcome !== 'ok') {
        const out = {
          ok: false,
          error: `coreSwap lastReload.outcome is ${outcome}`,
          side,
          generationBefore,
          generationAfter,
          alcName,
          lastReloadOutcome: outcome,
          lastReload,
          previousAlive,
          collected,
          inFlight,
          refusalWindow: refusalWindowFrom(refusals),
          polls,
          elapsedMs: clockNowMs(ctx) - waitStartedMs,
          scheduled,
        };
        stampCoreSwapRecord(step, ctx, out);
        return out;
      }
      if (movedBy !== expectGenerationDelta) {
        const out = {
          ok: false,
          error: `coreSwap generation did not move by ${expectGenerationDelta} (before ${generationBefore}, after ${generationAfter})`,
          side,
          generationBefore,
          generationAfter,
          alcName,
          lastReloadOutcome: outcome,
          lastReload,
          previousAlive,
          collected,
          inFlight,
          refusalWindow: refusalWindowFrom(refusals),
          polls,
          elapsedMs: clockNowMs(ctx) - waitStartedMs,
          scheduled,
        };
        stampCoreSwapRecord(step, ctx, out);
        return out;
      }
      if (previousAlive === true && step.allowPreviousAlive !== true) {
        const out = {
          ok: false,
          error: PREVIOUS_GENERATION_STILL_ALIVE,
          reason: PREVIOUS_GENERATION_STILL_ALIVE,
          side,
          generationBefore,
          generationAfter,
          alcName,
          lastReloadOutcome: outcome,
          lastReload,
          previousAlive,
          collected,
          inFlight,
          refusalWindow: refusalWindowFrom(refusals),
          polls,
          elapsedMs: clockNowMs(ctx) - waitStartedMs,
          scheduled,
        };
        stampCoreSwapRecord(step, ctx, out);
        return out;
      }
      if (previousAlive === true) {
        recordPreviousAliveFinding(ctx);
      }

      let ping = null;
      let imageHashes = { core: null, dev: null, sample: null };
      try {
        ping = await ctx.bridge.call(side, 'ping', {});
        imageHashes = imageHashesFromPing(ping);
      } catch (e) {
        const out = {
          ok: false,
          error: `coreSwap ping after swap failed: ${errorText(e)}`,
          side,
          generationBefore,
          generationAfter,
          alcName,
          lastReloadOutcome: outcome,
          lastReload,
          previousAlive,
          collected,
          inFlight,
          refusalWindow: refusalWindowFrom(refusals),
          polls,
          elapsedMs: clockNowMs(ctx) - waitStartedMs,
          scheduled,
        };
        stampCoreSwapRecord(step, ctx, out);
        return out;
      }

      const out = {
        ok: true,
        side,
        generationBefore,
        generationAfter,
        alcName,
        lastReloadOutcome: outcome,
        lastReload,
        previousAlive,
        collected,
        inFlight,
        refusalWindow: refusalWindowFrom(refusals),
        images: ping?.images ?? null,
        imageHashes,
        coreHash: imageHashes.core,
        devHash: imageHashes.dev,
        sampleHash: imageHashes.sample,
        polls,
        elapsedMs: clockNowMs(ctx) - waitStartedMs,
        scheduled,
      };
      stampCoreSwapRecord(step, ctx, out);
      return out;
    }

    await ctx.clock.sleep(Math.max(1, pollMs));
  }
}

async function stepRecover(step, ctx) {
  const side = step.side ?? ctx.side;
  if (!side) return { ok: false, error: 'recover requires side', recovered: false };
  const timeoutMs = Number.isFinite(Number(step.timeoutMs)) ? Number(step.timeoutMs) : 30000;
  const was = typeof ctx.bridge.mainThreadSuspect === 'function'
    ? ctx.bridge.mainThreadSuspect(side)
    : null;
  const t0 = clockNowMs(ctx);
  try {
    const result = await ctx.bridge.call(side, 'ping', {}, { timeoutMs, recover: true });
    return {
      ok: true,
      recovered: true,
      side,
      wasSuspect: !!was,
      suspect: typeof ctx.bridge.mainThreadSuspect === 'function'
        ? ctx.bridge.mainThreadSuspect(side)
        : null,
      result,
      elapsedMs: clockNowMs(ctx) - t0,
    };
  } catch (e) {
    const error = errorText(e);
    return {
      ok: false,
      recovered: false,
      side,
      wasSuspect: true,
      suspect: typeof ctx.bridge.mainThreadSuspect === 'function'
        ? ctx.bridge.mainThreadSuspect(side)
        : was,
      error,
      timeout: !!(e && e.timeout) || /timed out/.test(error),
      elapsedMs: clockNowMs(ctx) - t0,
    };
  }
}

function newestProgenitorLines(lines, limit = SOAK_NEWEST_LINE_LIMIT) {
  const newest = [];
  for (let i = (lines ?? []).length - 1; i >= 0 && newest.length < limit; i--) {
    const line = lines[i];
    const src = String(line?.source ?? '');
    const text = String(line?.text ?? '');
    if (/Progenitor|Sample/i.test(src) || /Progenitor|Sample/i.test(text)) {
      newest.push({
        seq: line.seq ?? null,
        source: src,
        text,
        time: line.time ?? null,
      });
    }
  }
  return newest;
}

function soakIntervalSeconds(value, fallback) {
  const n = Number(value);
  if (!Number.isFinite(n) || n <= 0) return fallback;
  return n;
}

async function collectSoakCaptures(handles, ctx, spec) {
  const frames = [];
  for (const handle of handles) {
    try {
      const collected = await collectCaptureBurst(handle, ctx, spec ?? {});
      for (const frame of collected.frames ?? []) frames.push(frame);
    } catch {
      /* still kill remaining children below */
    }
  }
  return frames;
}

function soakSecondsOrDefault(value, fallback) {
  const n = Number(value);
  if (!Number.isFinite(n) || n < 0) return fallback;
  return n;
}

async function stepSoak(step, ctx) {
  const side = step.side ?? ctx.side;
  if (!side) return { ok: false, error: 'soak requires side' };
  const minutes = Number(step.minutes);
  if (!Number.isFinite(minutes) || minutes <= 0) {
    return { ok: false, error: 'soak requires minutes > 0' };
  }
  const pingIntervalSeconds = soakIntervalSeconds(step.pingIntervalSeconds, SOAK_DEFAULT_PING_INTERVAL_SECONDS);
  const logsIntervalSeconds = soakIntervalSeconds(step.logsIntervalSeconds, SOAK_DEFAULT_LOGS_INTERVAL_SECONDS);
  const bridgeStatusIntervalSeconds = soakIntervalSeconds(
    step.bridgeStatusIntervalSeconds,
    SOAK_DEFAULT_BRIDGE_STATUS_INTERVAL_SECONDS,
  );
  const dumpAfterSeconds = soakSecondsOrDefault(step.dumpAfterSeconds, SOAK_DEFAULT_DUMP_AFTER_SECONDS);
  const failAfterSeconds = soakSecondsOrDefault(step.failAfterSeconds, SOAK_DEFAULT_FAIL_AFTER_SECONDS);
  const pingIntervalMs = pingIntervalSeconds * 1000;
  const logsIntervalMs = logsIntervalSeconds * 1000;
  const statusIntervalMs = bridgeStatusIntervalSeconds * 1000;
  const dumpAfterMs = dumpAfterSeconds * 1000;
  const failAfterMs = failAfterSeconds * 1000;
  const stopOnFirstFailure = step.stopOnFirstFailure !== false;
  const captureSpec = step.capture && typeof step.capture === 'object' ? step.capture : null;
  const captureIntervalMs = captureSpec && Number.isFinite(Number(captureSpec.intervalSeconds))
    ? Math.max(1, Number(captureSpec.intervalSeconds)) * 1000
    : null;
  const perfSpec = parsePerfSpec(step.perf);
  const perfGapMs = perfSpec ? perfSpec.gapSeconds * 1000 : 0;
  const logsFilter = step.logsFilter ?? SOAK_DEFAULT_LOGS_FILTER;
  const logsMax = Number.isFinite(Number(step.logsMax)) ? Number(step.logsMax) : SOAK_DEFAULT_LOGS_MAX;
  const pingTimeoutMs = Number.isFinite(Number(step.timeoutMs)) ? Number(step.timeoutMs) : undefined;
  const startMs = clockNowMs(ctx);
  const durationMs = minutes * 60 * 1000;
  const endMs = startMs + durationMs;
  let failEndMs = endMs;
  if (Number.isFinite(Number(step.maxAfterFailureMinutes))) {
    /* applied after the first failure */
  }
  const samples = [];
  const failureRows = [];
  const captureHandles = [];
  const pingWindows = [];
  const freezes = [];
  const perfWindows = [];
  let lastLogSeq = null;
  let pingStopped = false;
  let freezeHoldsPing = false;
  let firstFailureTime = null;
  let firstFailureAtMs = null;
  let logSeqAtFailure = null;
  let nextPingMs = startMs;
  let nextLogsMs = startMs;
  let nextStatusMs = startMs;
  let nextCaptureMs = captureIntervalMs != null ? startMs : Infinity;
  let nextPerfStartMs = perfSpec ? startMs : Infinity;
  let lastStatusFrame = null;
  let lastStatusAtMs = null;
  let openFreeze = null;
  let openPerfWindow = null;
  let pendingPerfReport = null;
  let nextPerfCaptureId = 1;

  const pingWaitMs = () => (pingStopped || freezeHoldsPing ? Infinity : nextPingMs);

  const killCaptures = async () => {
    const frames = await collectSoakCaptures(captureHandles, ctx, captureSpec ?? {});
    captureHandles.length = 0;
    return frames;
  };

  const closeOpenFreeze = (now) => {
    if (!openFreeze) return;
    openFreeze.mainThreadFrozenMs = now - openFreeze.startedAtMs;
    if (openFreeze.endedAt == null) {
      openFreeze.endedAt = null;
      openFreeze.endedAtMs = null;
    }
    freezes.push(openFreeze);
    openFreeze = null;
    freezeHoldsPing = false;
  };

  const mainThreadSuspectRow = () => (
    typeof ctx.bridge.mainThreadSuspect === 'function'
      ? ctx.bridge.mainThreadSuspect(side)
      : null
  );

  const mainThreadResponsive = () => (
    !freezeHoldsPing && !openFreeze && !mainThreadSuspectRow() && !mainThreadInFlightRow(ctx, side)
  );

  const applyPerfSampleToWindow = (sample) => {
    if (!openPerfWindow || !sample?.perf) return;
    const count = Number(sample.perf.frameCount);
    if (Number.isFinite(count)) openPerfWindow.frames = count;
    const maxMs = Number(sample.perf.maxIntervalMs ?? sample.perf.lastIntervalMs);
    if (Number.isFinite(maxMs)) {
      openPerfWindow.maxIntervalMs = openPerfWindow.maxIntervalMs == null
        ? maxMs
        : Math.max(openPerfWindow.maxIntervalMs, maxMs);
    }
    if (sample.perf.captureId != null && openPerfWindow.captureId == null) {
      openPerfWindow.captureId = sample.perf.captureId;
    }
  };

  const closePerfWindow = (now, { missingInterval = false, stopReason = null, pushGap = true } = {}) => {
    if (!openPerfWindow) return null;
    const closed = windowEntry({
      ...openPerfWindow,
      endedAt: isoFromMs(now),
      endedAtMs: now,
      missingInterval: missingInterval === true,
      stopReason: stopReason ?? openPerfWindow.stopReason ?? null,
    });
    perfWindows.push(closed);
    pendingPerfReport = closed;
    openPerfWindow = null;
    if (pushGap && perfSpec) {
      const gapEnd = now + perfGapMs;
      perfWindows.push(gapEntry({
        startedAt: isoFromMs(now),
        endedAt: isoFromMs(gapEnd),
        startedAtMs: now,
        endedAtMs: gapEnd,
        gapSeconds: perfSpec.gapSeconds,
      }));
      nextPerfStartMs = gapEnd;
    } else {
      nextPerfStartMs = Infinity;
    }
    return closed;
  };

  async function startPerfWindow() {
    if (!perfSpec) return false;
    if (openPerfWindow) return false;
    if (!mainThreadResponsive()) return false;
    const startedAtMs = clockNowMs(ctx);
    try {
      const result = await ctx.bridge.call(side, 'perf', {
        action: 'start',
        seconds: perfSpec.windowSeconds,
      });
      const captureId = result?.captureId ?? nextPerfCaptureId;
      nextPerfCaptureId = Math.max(nextPerfCaptureId, Number(captureId) || nextPerfCaptureId) + 1;
      openPerfWindow = {
        captureId,
        startedAt: isoFromMs(startedAtMs),
        startedAtMs,
        endedAt: null,
        endedAtMs: null,
        frames: 0,
        maxIntervalMs: null,
        missingInterval: false,
        report: null,
        stopReason: null,
      };
      nextPerfStartMs = Infinity;
      return true;
    } catch (e) {
      const timedOut = !!(e && e.timeout) || /timed out/.test(errorText(e));
      const suspect = !!(e && e.suspect) || /main thread suspect/.test(errorText(e));
      perfWindows.push(windowEntry({
        captureId: null,
        startedAt: isoFromMs(startedAtMs),
        startedAtMs,
        endedAt: isoFromMs(clockNowMs(ctx)),
        endedAtMs: clockNowMs(ctx),
        frames: 0,
        maxIntervalMs: null,
        missingInterval: false,
        report: { error: errorText(e), timeout: timedOut, suspect },
        stopReason: 'start-failed',
      }));
      if (timedOut || suspect) nextPerfStartMs = Infinity;
      else nextPerfStartMs = clockNowMs(ctx) + Math.max(perfGapMs, 1000);
      return false;
    }
  }

  async function fetchPerfReport(row) {
    if (!row || row.report) return row;
    if (!mainThreadResponsive()) return row;
    try {
      let result = await ctx.bridge.call(side, 'perf', { action: 'status' });
      if (result?.path == null && result?.summary == null) {
        result = await ctx.bridge.call(side, 'perf', { action: 'stop' });
      }
      row.report = {
        path: result?.path ?? null,
        summary: result?.summary ?? null,
        running: result?.running === true,
      };
      if (result?.summary?.stopReason) row.stopReason = result.summary.stopReason;
    } catch (e) {
      row.report = { error: errorText(e) };
    }
    return row;
  }

  async function stopOpenPerfWindow(now) {
    if (!openPerfWindow) return null;
    const missing = freezeOverlapsInstant(freezes, now) || !!openFreeze;
    let stopReason = missing ? 'soak-end-during-freeze' : 'soak-end';
    if (mainThreadResponsive()) {
      try {
        const result = await ctx.bridge.call(side, 'perf', { action: 'stop' });
        openPerfWindow.report = {
          path: result?.path ?? null,
          summary: result?.summary ?? null,
          running: result?.running === true,
        };
        stopReason = result?.summary?.stopReason ?? 'stop';
        if (Number.isFinite(Number(result?.summary?.frames))) {
          openPerfWindow.frames = Number(result.summary.frames);
        }
        const maxMs = Number(result?.summary?.frameMs?.max);
        if (Number.isFinite(maxMs)) openPerfWindow.maxIntervalMs = maxMs;
      } catch (e) {
        openPerfWindow.report = { error: errorText(e) };
      }
    }
    return closePerfWindow(now, { missingInterval: missing, stopReason, pushGap: false });
  }

  const soakResultExtras = () => {
    applyFreezeCorrelation(freezes, perfWindows);
    const pingMs = pingLatencies(samples);
    const perfStatusMs = perfStatusLatencies(samples);
    const stall = responsivenessSummary({ pingMs, freezes });
    return {
      perf: perfSpec,
      perfWindows,
      perfLatency: latencyTable({ pingMs, perfStatusMs }),
      sampleGaps: sampleGapStats(samples, statusIntervalMs),
      responsivenessFailed: stall.responsivenessFailed,
      maxStallMs: stall.maxStallMs,
      stallThresholdMs: stall.stallThresholdMs,
    };
  };

  const soakFields = (frames, extra = {}) => {
    const mapped = mapCaptureFrames(frames);
    const captureWindows = (frames ?? [])
      .filter((f) => f && (f.startedMs != null || f.startedAt))
      .map((f) => captureWindowFromFrame(f, pingWindows));
    return {
      samples,
      failures: failureRows.length,
      firstFailureTime,
      logSeqAtFailure,
      pingStopped,
      freezePingHeld: freezeHoldsPing,
      captureWindows,
      freezes,
      dumpAfterSeconds,
      failAfterSeconds,
      frames: mapped,
      ...captureSummary(frames, { configured: !!captureSpec }),
      ...soakResultExtras(),
      ...extra,
    };
  };

  async function observeFreeze(sample, st, now, statusDue) {
    sample.lastFreezeDump = st?.lastFreezeDump ?? null;
    sample.mainThreadFrozenSince = openFreeze?.startedAt ?? null;
    if (!statusDue) return;
    const frame = statusFrameOf(st);
    const answered = st != null && sample.bridgeStatusError == null;
    if (!answered || frame == null) {
      lastStatusFrame = frame;
      lastStatusAtMs = now;
      return;
    }
    if (lastStatusFrame != null && frame === lastStatusFrame) {
      if (!openFreeze) {
        openFreeze = {
          startedAt: isoFromMs(lastStatusAtMs ?? now),
          startedAtMs: lastStatusAtMs ?? now,
          frame,
          dumpRequested: false,
          dumpSuppressed: false,
          dump: null,
          dumpError: null,
          lastFreezeDump: st.lastFreezeDump ?? null,
          recoveredAt: null,
          recoverySample: null,
          mainThreadFrozenMs: null,
          endedAt: null,
          endedAtMs: null,
          exceededFailAfter: false,
        };
        freezeHoldsPing = true;
      }
      sample.mainThreadFrozenSince = openFreeze.startedAt;
      const frozenMs = now - openFreeze.startedAtMs;
      openFreeze.lastFreezeDump = st.lastFreezeDump ?? openFreeze.lastFreezeDump;
      if (!openFreeze.dumpRequested && !openFreeze.dumpSuppressed && frozenMs >= dumpAfterMs) {
        if (lastFreezeDumpNewerThan(st.lastFreezeDump, openFreeze.startedAtMs)) {
          openFreeze.dumpSuppressed = true;
          openFreeze.lastFreezeDump = st.lastFreezeDump;
        } else {
          openFreeze.dumpRequested = true;
          try {
            const dumpResult = await ctx.bridge.call(side, 'dump', { reason: SOAK_FREEZE_DUMP_REASON });
            openFreeze.dump = dumpResult;
            sample.freezeDump = dumpResult;
          } catch (e) {
            openFreeze.dumpError = errorText(e);
            sample.freezeDumpError = openFreeze.dumpError;
          }
        }
      }
      if (frozenMs > failAfterMs) {
        openFreeze.exceededFailAfter = true;
        openFreeze.mainThreadFrozenMs = frozenMs;
      }
    } else if (openFreeze) {
      openFreeze.endedAt = isoFromMs(now);
      openFreeze.endedAtMs = now;
      openFreeze.mainThreadFrozenMs = now - openFreeze.startedAtMs;
      openFreeze.recoveredAt = sample.at;
      openFreeze.recoverySample = { at: sample.at, atMs: now, frame };
      sample.mainThreadFrozenMs = openFreeze.mainThreadFrozenMs;
      sample.mainThreadFrozenSince = null;
      freezes.push(openFreeze);
      openFreeze = null;
      freezeHoldsPing = false;
    }
    lastStatusFrame = frame;
    lastStatusAtMs = now;
  }

  try {
    try {
      const initial = await ctx.bridge.call(side, 'logs', { since: -1, max: 1, filter: '' });
      lastLogSeq = initial?.latestSeq ?? 0;
    } catch (e) {
      const frames = await killCaptures();
      return {
        ok: false,
        error: `soak could not read logs: ${errorText(e)}`,
        logSeqAfterFailure: null,
        ...soakFields(frames),
      };
    }

    let spins = 0;
    while (spins++ < 100000) {
      if (isRunCancelled(ctx)) break;
      const now = clockNowMs(ctx);
      const limit = pingStopped ? failEndMs : endMs;
      if (now >= limit) break;

      const pingWouldBeDue = now >= nextPingMs;
      const pingDue = !pingStopped && !freezeHoldsPing && pingWouldBeDue;
      const logsDue = now >= nextLogsMs;
      const statusDue = now >= nextStatusMs;
      const captureDue = captureIntervalMs != null && now >= nextCaptureMs;
      const perfStartDue = !!(perfSpec && !openPerfWindow && now >= nextPerfStartMs);
      const sampleDue = pingDue || logsDue || statusDue || (pingStopped && pingWouldBeDue);
      if (!sampleDue && !captureDue && !perfStartDue) {
        const next = Math.min(
          pingWaitMs(),
          nextLogsMs,
          nextStatusMs,
          captureIntervalMs != null ? nextCaptureMs : Infinity,
          nextPerfStartMs,
          limit,
        );
        const wait = next - now;
        if (wait > 0) {
          const slice = Math.min(100, wait);
          await ctx.clock.sleep(slice);
        } else await ctx.clock.sleep(1);
        continue;
      }

      if (!sampleDue && captureDue) {
        if (mainThreadInFlightRow(ctx, side)) {
          await yieldEventLoop();
          await ctx.clock.sleep(1);
          continue;
        }
        const handle = startCaptureBurst({
          ...captureSpec,
          side,
          name: step.name ?? 'soak',
          requireFrames: captureSpec.requireFrames === true,
        }, ctx, {
          name: `${captureSlug(step.name, 'soak')}-${samples.length}`,
          keep: true,
        });
        captureHandles.push(handle);
        if (samples.length) samples[samples.length - 1].captureStarted = true;
        nextCaptureMs += captureIntervalMs;
        if (nextCaptureMs <= now) nextCaptureMs = now + captureIntervalMs;
        continue;
      }

      if (!sampleDue && perfStartDue) {
        if (mainThreadInFlightRow(ctx, side) || !mainThreadResponsive()) {
          const next = Math.min(
            pingWaitMs(),
            nextLogsMs,
            nextStatusMs,
            captureIntervalMs != null ? nextCaptureMs : Infinity,
            limit,
          );
          const wait = next - now;
          if (wait > 0) await ctx.clock.sleep(Math.min(100, wait));
          else {
            await yieldEventLoop();
            await ctx.clock.sleep(1);
          }
          continue;
        }
        await startPerfWindow();
        continue;
      }

      if (perfStartDue) await startPerfWindow();

      const sample = {
        at: isoFromMs(now),
        atMs: now,
        ping: null,
        logSeq: lastLogSeq,
        logSeqDelta: 0,
        newest: [],
        capture: null,
        entered: null,
        abandoned: null,
        frame: null,
        lastDrainAt: null,
        lastFreezeDump: null,
        mainThreadFrozenSince: null,
        bridge_status: null,
        perf: null,
      };

      try {
        const st = await ctx.bridge.call(side, 'bridge_status', {});
        sample.bridge_status = st;
        sample.entered = st?.entered ?? null;
        sample.abandoned = st?.abandoned ?? null;
        sample.frame = st?.frame ?? null;
        sample.lastDrainAt = st?.lastDrainAt ?? null;
        await observeFreeze(sample, st, now, statusDue);
      } catch (e) {
        sample.bridgeStatusError = errorText(e);
        await observeFreeze(sample, null, now, statusDue);
      }
      if (statusDue) nextStatusMs = now + statusIntervalMs;

      if (perfSpec) {
        const perfReq0 = Date.now();
        try {
          const live = await ctx.bridge.call(side, 'perf_status', {});
          sample.perf = readPerfStatus(live, Date.now() - perfReq0);
        } catch (e) {
          sample.perf = {
            error: errorText(e),
            elapsedMs: Date.now() - perfReq0,
            running: false,
            historical: false,
            frameCount: null,
            lastIntervalMs: null,
            maxIntervalMs: null,
            sampleSilenceMs: null,
            captureId: openPerfWindow?.captureId ?? null,
          };
        }
        applyPerfSampleToWindow(sample);
        if (openPerfWindow && sample.perf && sample.perf.running === false && sample.perf.error == null) {
          const missing = !!openFreeze || freezeOverlapsInstant(freezes, now);
          closePerfWindow(now, {
            missingInterval: missing,
            stopReason: missing ? 'closed-during-freeze' : 'deadline',
            pushGap: now < limit,
          });
        }
      }

      if (logsDue || pingDue) {
        try {
          const since = lastLogSeq ?? -1;
          const logsResult = await ctx.bridge.call(side, 'logs', {
            since,
            max: logsMax,
            filter: logsFilter,
          });
          const newSeq = logsResult?.latestSeq ?? lastLogSeq ?? 0;
          sample.logSeq = newSeq;
          sample.logSeqDelta = lastLogSeq == null ? 0 : newSeq - lastLogSeq;
          sample.newest = newestProgenitorLines(logsResult?.lines ?? []);
          lastLogSeq = newSeq;
        } catch (e) {
          sample.logsError = errorText(e);
        }
        nextLogsMs = now + logsIntervalMs;
      }

      if (pingDue) {
        const pingStartedMs = clockNowMs(ctx);
        const pingWall0 = Date.now();
        try {
          const pingResult = await ctx.bridge.call(
            side,
            'ping',
            {},
            pingTimeoutMs != null ? { timeoutMs: pingTimeoutMs } : {},
          );
          const pingEndedMs = clockNowMs(ctx);
          const window = {
            startedAt: isoFromMs(pingStartedMs),
            endedAt: isoFromMs(pingEndedMs),
            startedAtMs: pingStartedMs,
            endedAtMs: pingEndedMs,
          };
          pingWindows.push(window);
          sample.ping = {
            ok: true,
            elapsedMs: Date.now() - pingWall0,
            logSeq: pingResult?.logSeq ?? null,
            ...window,
          };
        } catch (e) {
          const pingEndedMs = clockNowMs(ctx);
          const window = {
            startedAt: isoFromMs(pingStartedMs),
            endedAt: isoFromMs(pingEndedMs),
            startedAtMs: pingStartedMs,
            endedAtMs: pingEndedMs,
          };
          pingWindows.push(window);
          const error = errorText(e);
          const timedOut = !!(e && e.timeout) || /timed out/.test(error);
          sample.ping = {
            ok: false,
            elapsedMs: Date.now() - pingWall0,
            error,
            timeout: timedOut,
            suspect: !!(e && e.suspect) || /main thread suspect/.test(error),
            ...window,
          };
          failureRows.push({ at: sample.at, atMs: now, error });
          if (firstFailureTime == null) {
            firstFailureTime = sample.at;
            firstFailureAtMs = now;
            logSeqAtFailure = sample.logSeq ?? lastLogSeq;
            if (Number.isFinite(Number(step.maxAfterFailureMinutes))) {
              failEndMs = Math.min(endMs, now + Number(step.maxAfterFailureMinutes) * 60 * 1000);
            }
          }
          if (stopOnFirstFailure) pingStopped = true;
        }
        nextPingMs = now + pingIntervalMs;
      } else if (pingStopped) {
        sample.ping = { ok: false, skipped: true, elapsedMs: 0 };
        if (pingWouldBeDue) nextPingMs = now + pingIntervalMs;
      } else if (freezeHoldsPing && pingWouldBeDue) {
        sample.ping = { ok: false, skipped: true, elapsedMs: 0, frozen: true };
      }

      if (pendingPerfReport) {
        await fetchPerfReport(pendingPerfReport);
        if (pendingPerfReport.report) pendingPerfReport = null;
      }

      samples.push(sample);
      if (captureDue) await ctx.clock.sleep(1);
    }

    const frames = await killCaptures();
    closeOpenFreeze(clockNowMs(ctx));
    await stopOpenPerfWindow(clockNowMs(ctx));
    if (pendingPerfReport) await fetchPerfReport(pendingPerfReport);
    const saved = frames.filter((f) => f && f.ok && f.path && f.bytes > 0 && !f.discarded);
    let savedIndex = 0;
    for (const sample of samples) {
      if (!sample.captureStarted) continue;
      const frame = saved[savedIndex++];
      sample.capture = frame?.path ?? null;
    }

    let logSeqAfterFailure = null;
    if (firstFailureTime != null) {
      const later = samples.filter((s) => s.atMs > firstFailureAtMs && s.logSeq != null);
      const last = later.length ? later[later.length - 1].logSeq : logSeqAtFailure;
      const progressed = later.some((s) => s.logSeq > logSeqAtFailure);
      logSeqAfterFailure = progressed || (last != null && logSeqAtFailure != null && last > logSeqAtFailure)
        ? 'advancing'
        : 'frozen';
    }

    const longestFreezeMs = freezes.reduce((m, f) => Math.max(m, Number(f.mainThreadFrozenMs) || 0), 0);
    const freezeFailed = freezes.some((f) => f.exceededFailAfter === true || (Number(f.mainThreadFrozenMs) || 0) > failAfterMs);
    const ok = failureRows.length === 0 && !freezeFailed;
    const parts = [];
    if (failureRows.length) {
      parts.push(`${failureRows.length} failure(s), first at ${firstFailureTime}, logSeq ${logSeqAfterFailure} after the failure`);
    }
    if (freezeFailed) {
      parts.push(`main thread frozen for ${Math.round(longestFreezeMs)} ms (failAfterSeconds ${failAfterSeconds})`);
    }
    return {
      ok,
      error: ok ? undefined : `soak failed: ${parts.join('; ')}`,
      logSeqAfterFailure,
      ...soakFields(frames),
    };
  } catch (e) {
    const frames = await killCaptures();
    closeOpenFreeze(clockNowMs(ctx));
    await stopOpenPerfWindow(clockNowMs(ctx));
    if (pendingPerfReport) await fetchPerfReport(pendingPerfReport);
    return {
      ok: false,
      error: errorText(e),
      logSeqAfterFailure: firstFailureTime == null ? null : 'frozen',
      ...soakFields(frames),
    };
  }
}

const HANDLERS = {
  command: stepCommand,
  adopt: stepAdopt,
  spawn: stepSpawn,
  destroyTracked: stepDestroyTracked,
  wait: stepWait,
  get: stepGet,
  exists: stepExists,
  existsAt: stepExistsAt,
  logEntity: stepLogEntity,
  log: stepLog,
  swap: stepSwap,
  removal: stepRemoval,
  reinstall: stepReinstall,
  protectionCheck: stepProtectionCheck,
  skipCleanupFor: stepSkipCleanupFor,
  snippetPrepare: stepSnippetPrepare,
  snippet: stepSnippet,
  capturePrepare: stepCapturePrepare,
  capture: stepCapture,
  poolReuse: stepPoolReuse,
  op: stepOp,
  coreSwap: stepCoreSwap,
  recover: stepRecover,
  soak: stepSoak,
  input: (step, ctx) => import('./input.mjs').then((m) => m.stepInput(step, ctx)),
  assertWindow: (step, ctx) => import('./input.mjs').then((m) => m.stepAssertWindow(step, ctx)),
};

export const STEP_KINDS = Object.freeze(Object.keys(HANDLERS));

export function stepName(step, index) {
  if (step.name && step.kind !== 'spawn' && step.kind !== 'destroyTracked' && step.kind !== 'skipCleanupFor' && step.kind !== 'adopt') {
    return step.name;
  }
  if (step.kind === 'spawn') return `spawn ${step.name ?? step.prefab ?? index}`;
  if (step.kind === 'adopt') return `adopt ${step.name ?? step.entity ?? index}`;
  if (step.kind === 'destroyTracked') return `destroyTracked ${step.name}`;
  if (step.kind === 'skipCleanupFor') return `skipCleanupFor ${step.name}`;
  if (step.kind === 'command') return `command ${step.text ?? ''}`.trim();
  if (step.kind === 'get') return `get ${step.component ?? ''}`.trim();
  if (step.kind === 'exists') return `exists ${step.entity ?? ''}`.trim();
  if (step.kind === 'existsAt') {
    const exp = (step.expect === 'any' || step.expect === null) ? 'any' : (step.expect !== false);
    return `existsAt ${step.entity ?? ''} expect=${exp}`;
  }
  if (step.kind === 'logEntity') return `logEntity ${step.name ?? ''}`.trim();
  if (step.kind === 'log') return `log ${step.record ?? step.filter ?? ''}`.trim();
  if (step.kind === 'wait') return `wait ${step.ms ?? 0}ms`;
  if (step.kind === 'protectionCheck') return `protectionCheck expect=${step.expect !== false}`;
  if (step.kind === 'snippetPrepare' || (step.kind === 'snippet' && step.prepare === true)) {
    return `snippetPrepare ${step.file ?? ''}`.trim();
  }
  if (step.kind === 'snippet') return `snippet ${step.file ?? ''}`.trim();
  if (step.kind === 'capturePrepare' || (step.kind === 'capture' && step.prepare === true)) {
    return `capturePrepare ${step.name ?? ''}`.trim();
  }
  if (step.kind === 'capture') return `capture ${step.name ?? step.record ?? ''}`.trim();
  if (step.kind === 'poolReuse') return `poolReuse ${step.name ?? step.record ?? ''}`.trim();
  if (step.kind === 'op') {
    if (step.name) return step.name;
    const bits = ['op', step.side, step.op].filter((x) => x != null && x !== '');
    return bits.join(' ');
  }
  if (step.kind === 'coreSwap') return step.name ?? `coreSwap ${step.side ?? ''}`.trim();
  if (step.kind === 'recover') return step.name ?? `recover ${step.side ?? ''}`.trim();
  if (step.kind === 'soak') {
    if (step.name) return step.name;
    const mins = step.minutes != null ? `${step.minutes}min` : '';
    return `soak ${step.side ?? ''} ${mins}`.trim();
  }
  return step.kind ?? `step#${index}`;
}

export async function executeStep(step, ctx) {
  const handler = HANDLERS[step.kind];
  if (!handler) throw new Error(`unknown step kind '${step.kind}'`);
  return handler(step, ctx);
}

export { isInsideRepo };
