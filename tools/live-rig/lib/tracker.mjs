import {
  destroyRequest,
  existsRequest,
  finalizeRequest,
  inspectAttachedBufferRequest,
  parseResult,
  contractMismatchFinding,
  observeDestroyContract,
  isExpectedDestroyPhase,
  MAX_BUFF_CYCLES,
  PHASE_BUFFS,
  PHASE_STUCK,
  PHASE_TAGGED,
  PHASE_WAITING_BUFFS,
  REASON_NONE,
  INSPECT_OP,
} from './entityOp.mjs';
import { realClock } from './clock.mjs';

export const DEFAULT_CLEANUP_TIMEOUT_MS = 10000;
export const DEFAULT_CLEANUP_POLL_MS = 250;
export const DEFAULT_FINALIZE_TIMEOUT_MS = 8000;
export const DEFAULT_FINALIZE_POLL_MS = 250;
export { contractMismatchFinding, MAX_BUFF_CYCLES };

const KNOWN_FINALIZE_PHASES = new Set([
  PHASE_WAITING_BUFFS,
  PHASE_BUFFS,
  PHASE_TAGGED,
  PHASE_STUCK,
]);

/** Player character prefabs (CHAR_Vampire*). The entity op also refuses these; the rig must not request them. */
export const PLAYER_PREFAB_DENY_PREFIX = 'CHAR_Vampire';

/** Prefab name from an exists readback (`{hash, name}` or a string). Hash-only is not a name. */
export function prefabDisplayName(prefab) {
  if (prefab == null || prefab === '') return null;
  if (typeof prefab === 'string') return prefab;
  if (typeof prefab === 'object' && prefab.name != null && prefab.name !== '') return String(prefab.name);
  return null;
}

export function isDeniedPlayerPrefab(prefab) {
  const name = prefabDisplayName(prefab);
  return !!name && name.startsWith(PLAYER_PREFAB_DENY_PREFIX);
}

/** Character and user entity ids from server `players` / `users` results. */
export function playerOwnedIds(players, users) {
  const ids = new Set();
  const playerList = Array.isArray(players) ? players : (players?.players ?? []);
  const userList = Array.isArray(users) ? users : (users?.users ?? []);
  for (const p of playerList) {
    if (p?.entity) ids.add(String(p.entity));
    if (p?.userEntity) ids.add(String(p.userEntity));
  }
  for (const u of userList) {
    if (u?.entity) ids.add(String(u.entity));
    if (u?.character) ids.add(String(u.character));
  }
  return ids;
}

export function isThiefOrDiagRow(row) {
  const prefab = prefabDisplayName(row?.prefab) ?? '';
  const name = String(row?.name ?? '');
  return /thief/i.test(prefab) || /thief/i.test(name) || /diag/i.test(name);
}

/**
 * Registry of every test entity the run creates or adopts.
 * Tracked rows are never the player's character or user (refused on adopt).
 */
export function createTracker(opts = {}) {
  const bridge = opts.bridge;
  const clock = opts.clock ?? realClock();
  const timeoutMs = opts.timeoutMs ?? DEFAULT_CLEANUP_TIMEOUT_MS;
  const pollMs = opts.pollMs ?? DEFAULT_CLEANUP_POLL_MS;
  const finalizeTimeoutMs = opts.finalizeTimeoutMs ?? DEFAULT_FINALIZE_TIMEOUT_MS;
  const finalizePollMs = opts.finalizePollMs ?? DEFAULT_FINALIZE_POLL_MS;
  const findings = opts.findings ?? [];
  const defaultSide = opts.side ?? 'server';
  const attachReadbackOpt = opts.attachReadback;
  const entries = [];
  const forbidden = new Set();

  function setForbidden(ids) {
    forbidden.clear();
    for (const id of ids ?? []) {
      if (id) forbidden.add(String(id));
    }
  }

  function list() {
    return entries.map((e) => ({ ...e }));
  }

  function get(name) {
    const row = entries.find((e) => e.name === name);
    if (!row) throw new Error(`no tracked entity named '${name}'`);
    return { ...row };
  }

  function resolve(ref, fallbackSide = defaultSide) {
    if (ref == null) throw new Error('entity reference is empty');
    const s = String(ref);
    if (s.startsWith('@tracked:')) return get(s.slice('@tracked:'.length));
    return { entity: s, side: fallbackSide, name: undefined };
  }

  function adopt(spec) {
    const name = spec.name;
    if (!name) throw new Error('adopt requires a name');
    if (entries.some((e) => e.name === name)) throw new Error(`tracked entity '${name}' already exists`);
    const entity = spec.entity;
    if (!entity || !/^\d+:\d+$/.test(String(entity))) {
      throw new Error(`tracked entity id must be index:version, got ${JSON.stringify(entity)}`);
    }
    if (forbidden.has(String(entity))) {
      throw new Error(`refusing to track the player's character or user ${entity}`);
    }
    const row = {
      name,
      side: spec.side ?? defaultSide,
      entity: String(entity),
      prefab: spec.prefab ?? null,
      createdAt: spec.createdAt ?? new Date().toISOString(),
      by: spec.by ?? 'adopt',
      hostile: !!spec.hostile,
      skipCleanup: false,
      logTime: spec.logTime ?? null,
      logSeq: spec.logSeq ?? null,
      seenAt: spec.seenAt ?? null,
    };
    entries.push(row);
    return { ...row };
  }

  function adoptSpawn(name, spawnResult, spec = {}) {
    const rows = spawnResult?.entities ?? [];
    if (!rows.length) throw new Error(`spawn returned no entities to track for '${name}'`);
    const adopted = [];
    rows.forEach((row, i) => {
      const n = i === 0 ? name : `${name}#${i + 1}`;
      adopted.push(adopt({
        name: n,
        entity: row.entity,
        prefab: spec.prefab ?? row.prefab?.name ?? row.prefab ?? null,
        side: spec.side ?? defaultSide,
        by: spec.by ?? 'spawn',
        hostile: !!spec.hostile,
      }));
    });
    return adopted;
  }

  function markSkipCleanup(name) {
    const row = entries.find((e) => e.name === name);
    if (!row) throw new Error(`no tracked entity named '${name}'`);
    row.skipCleanup = true;
    return { ...row };
  }

  function groupBySide(rows) {
    const map = new Map();
    for (const row of rows) {
      const side = row.side ?? defaultSide;
      if (!map.has(side)) map.set(side, []);
      map.get(side).push(row);
    }
    return map;
  }

  function errorText(err) {
    if (err == null) return 'unknown';
    if (typeof err === 'string') return err;
    if (typeof err === 'object') return err.error ?? err.message ?? JSON.stringify(err);
    return String(err);
  }

  function noteFinding(msg) {
    if (!findings.includes(msg)) findings.push(msg);
  }

  function asBuffIds(value) {
    if (value == null) return [];
    if (Array.isArray(value)) return value.map((id) => String(id));
    return [String(value)];
  }

  function mergeBuffIds(base, extra) {
    const out = asBuffIds(base);
    const seen = new Set(out);
    for (const id of asBuffIds(extra)) {
      if (!seen.has(id)) {
        seen.add(id);
        out.push(id);
      }
    }
    return out;
  }

  function asStaleRows(value) {
    if (value == null) return [];
    if (Array.isArray(value)) return value;
    return [value];
  }

  function isoNow() {
    return new Date(clock.now()).toISOString();
  }

  function stuckReason(phase, ids) {
    return `stuck in ${phase}: [${asBuffIds(ids).join(', ')}]`;
  }

  function sweepIncompleteReason(entry) {
    if (entry && entry.reason) return errorText(entry.reason);
    return 'buff sweep incomplete';
  }

  function entitySnapshot(row, extra = {}) {
    return {
      name: row.name,
      side: row.side,
      entity: row.entity,
      prefab: row.prefab,
      hostile: !!row.hostile,
      skipCleanup: !!row.skipCleanup,
      ...extra,
    };
  }

  function resolveAttachReadback(rows, callOpts) {
    const v = callOpts.attachReadback ?? attachReadbackOpt;
    if (v === true) return true;
    if (v === false) return false;
    return (rows ?? []).some(isThiefOrDiagRow);
  }

  function ensureAttachedBuffer(rec) {
    if (!rec.attachedBuffer || typeof rec.attachedBuffer !== 'object') {
      rec.attachedBuffer = { before: null, atTagged: null };
    }
    return rec.attachedBuffer;
  }

  async function inspectAttachedBuffer(side, entity) {
    try {
      return await bridge.call(side, INSPECT_OP, inspectAttachedBufferRequest(entity));
    } catch (e) {
      return { error: errorText(e && e.message ? e.message : e) };
    }
  }

  /**
   * Fresh exists readback. exists=false is gone (never remaining). A bridge
   * error/timeout or omitted exists field is unconfirmed, never remaining or gone.
   */
  async function requestExists(rows) {
    const remaining = [];
    const unconfirmed = [];
    for (const [side, batch] of groupBySide(rows)) {
      if (!batch.length) continue;
      try {
        const result = parseResult(await bridge.call(side, 'entity', existsRequest(batch.map((e) => e.entity))));
        const byId = new Map((result.entries ?? []).map((e) => [e.entity, e]));
        for (const row of batch) {
          const hit = byId.get(row.entity);
          if (!hit || hit.exists === undefined || hit.exists === null) {
            unconfirmed.push({
              ...row,
              error: hit ? 'exists field missing' : `exists result omitted ${row.entity}`,
            });
            continue;
          }
          if (hit.exists === false) continue;
          remaining.push({ ...row, exists: true });
        }
      } catch (e) {
        const msg = errorText(e && e.message ? e.message : e);
        for (const row of batch) unconfirmed.push({ ...row, error: msg });
      }
    }
    return { remaining, unconfirmed };
  }

  async function pollAbsent(rows, opts = {}) {
    const limit = opts.timeoutMs ?? timeoutMs;
    const interval = opts.pollMs ?? pollMs;
    const start = clock.now();
    const deadline = start + limit;
    let remaining = [];
    let unconfirmed = [];
    let spins = 0;
    while (spins++ < 10000) {
      const sweep = rows.length ? await requestExists(rows) : { remaining: [], unconfirmed: [] };
      remaining = sweep.remaining;
      unconfirmed = sweep.unconfirmed;
      if (remaining.length === 0 && unconfirmed.length === 0) {
        return { ok: true, remaining: [], unconfirmed: [], elapsedMs: clock.now() - start };
      }
      // A timed-out / failed exists query is unconfirmed. Do not grow a retry
      // queue of main-thread exists calls after that failure.
      if (unconfirmed.length > 0) {
        return {
          ok: false,
          remaining,
          unconfirmed,
          elapsedMs: clock.now() - start,
        };
      }
      if (clock.now() >= deadline) break;
      await clock.sleep(interval);
    }
    return {
      ok: remaining.length === 0 && unconfirmed.length === 0,
      remaining,
      unconfirmed,
      elapsedMs: clock.now() - start,
    };
  }

  function errorByEntity(errors) {
    const map = new Map();
    for (const err of errors ?? []) {
      if (err && err.entity) map.set(String(err.entity), errorText(err));
    }
    return map;
  }

  /**
   * Destroy copied buffs, then bounded finalize until tagged.
   * Sweep-incomplete, contract-mismatch, stuck and finalize-timeout rows are
   * unconfirmed and skip exists polling. A detach-phase destroy is a mismatch
   * with no fallback.
   */
  async function threePhaseDestroy(rows, opts = {}) {
    const reason = opts.reason ?? REASON_NONE;
    const attachReadback = resolveAttachReadback(rows, opts);
    const ordered = [
      ...rows.filter((e) => e.hostile),
      ...rows.filter((e) => !e.hostile),
    ];
    const entities = [];
    const unconfirmed = [];
    const toFinalize = [];
    const toExists = [];

    for (const [side, batch] of groupBySide(ordered)) {
      if (!batch.length) continue;

      const beforeInspect = new Map();
      if (attachReadback) {
        for (const row of batch) {
          beforeInspect.set(row.entity, await inspectAttachedBuffer(side, row.entity));
        }
      }

      let result;
      try {
        result = parseResult(await bridge.call(side, 'entity', destroyRequest(batch.map((e) => e.entity), reason)));
      } catch (e) {
        const msg = errorText(e && e.message ? e.message : e);
        for (const row of batch) {
          const rec = entitySnapshot(row, { error: msg, finalizePolls: 0, cycles: 0, buffsAt: null, taggedAt: null, absenceLatencyMs: null });
          if (attachReadback) {
            const store = ensureAttachedBuffer(rec);
            store.before = beforeInspect.get(row.entity) ?? null;
          }
          toExists.push(row);
          entities.push(rec);
        }
        continue;
      }

      const byId = new Map((result.entries ?? []).map((e) => [e.entity, e]));
      const errors = errorByEntity(result.errors);

      for (const row of batch) {
        const hit = byId.get(row.entity);
        if (!hit) {
          const rec = entitySnapshot(row, {
            error: errors.get(row.entity) ?? `destroy result omitted ${row.entity}`,
            finalizePolls: 0,
            cycles: 0,
            buffsAt: null,
            taggedAt: null,
            absenceLatencyMs: null,
          });
          if (attachReadback) {
            const store = ensureAttachedBuffer(rec);
            store.before = beforeInspect.get(row.entity) ?? null;
          }
          unconfirmed.push({ ...row, error: rec.error });
          entities.push(rec);
          continue;
        }

        if (!isExpectedDestroyPhase(hit)) {
          const observed = observeDestroyContract(hit);
          const msg = contractMismatchFinding(observed);
          noteFinding(msg);
          const rec = entitySnapshot(row, {
            phase: hit.phase ?? undefined,
            buffs: asBuffIds(hit.buffs ?? hit.buffsFound),
            staleRows: asStaleRows(hit.staleRows),
            buffSweepComplete: hit.buffSweepComplete,
            cycles: 0,
            buffsAt: null,
            taggedAt: null,
            finalizeWaitMs: null,
            finalizePolls: 0,
            absenceLatencyMs: null,
            error: msg,
          });
          if (attachReadback) {
            const store = ensureAttachedBuffer(rec);
            store.before = beforeInspect.get(row.entity) ?? null;
          }
          unconfirmed.push({ ...row, error: msg, phase: rec.phase, pending: rec.buffs });
          entities.push(rec);
          continue;
        }

        const buffs = asBuffIds(hit.buffs);
        const rec = entitySnapshot(row, {
          phase: PHASE_BUFFS,
          buffs,
          staleRows: asStaleRows(hit.staleRows),
          buffSweepComplete: hit.buffSweepComplete,
          reason: hit.reason,
          cycles: 0,
          buffsAt: isoNow(),
          taggedAt: null,
          finalizeWaitMs: null,
          finalizePolls: 0,
          absenceLatencyMs: null,
        });
        if (attachReadback) {
          const store = ensureAttachedBuffer(rec);
          store.before = beforeInspect.get(row.entity) ?? null;
        }

        if (hit.buffSweepComplete === false) {
          rec.error = sweepIncompleteReason(hit);
          unconfirmed.push({ ...row, error: rec.error, pending: buffs, phase: PHASE_BUFFS });
          entities.push(rec);
          continue;
        }

        toFinalize.push({ row, side, buffs, listed: buffs.slice(), rec, attachReadback });
        entities.push(rec);
      }
    }

    const finalized = await pollFinalize(toFinalize, opts);
    toExists.push(...finalized.tagged);
    unconfirmed.push(...finalized.unconfirmed);

    return { entities, unconfirmed, toExists, findings };
  }

  async function pollFinalize(items, opts = {}) {
    const tagged = [];
    const unconfirmed = [];
    if (!items.length) return { tagged, unconfirmed };

    const limit = opts.finalizeTimeoutMs ?? finalizeTimeoutMs;
    const interval = opts.finalizePollMs ?? finalizePollMs;
    const start = clock.now();
    const deadline = start + limit;
    const pending = items.map((item) => ({
      ...item,
      listed: (item.listed ?? item.buffs).slice(),
      pending: (item.listed ?? item.buffs).slice(),
    }));
    let spins = 0;

    function drop(item) {
      const idx = pending.indexOf(item);
      if (idx >= 0) pending.splice(idx, 1);
    }

    function markUnconfirmed(item, msg, extra = {}) {
      item.rec.error = msg;
      item.rec.pending = extra.pending ?? item.pending;
      if (extra.phase != null) item.rec.phase = extra.phase;
      unconfirmed.push({
        ...item.row,
        error: msg,
        pending: item.rec.pending,
        phase: item.rec.phase,
        buffs: item.rec.buffs,
        staleRows: item.rec.staleRows,
        cycles: item.rec.cycles,
      });
      drop(item);
    }

    while (pending.length && spins++ < 10000) {
      const bySide = new Map();
      for (const item of pending) {
        const side = item.side ?? item.row.side ?? defaultSide;
        if (!bySide.has(side)) bySide.set(side, []);
        bySide.get(side).push(item);
      }
      for (const [side, group] of bySide) {
        if (!group.length) continue;
        const buffs = {};
        for (const item of group) buffs[item.row.entity] = item.listed ?? item.buffs;
        let result;
        try {
          result = parseResult(await bridge.call(side, 'entity', finalizeRequest(group.map((g) => g.row.entity), buffs)));
        } catch (e) {
          const msg = errorText(e && e.message ? e.message : e);
          for (const item of group) {
            item.rec.finalizePolls = (item.rec.finalizePolls ?? 0) + 1;
            item.rec.finalizeWaitMs = clock.now() - start;
            markUnconfirmed(item, msg);
          }
          continue;
        }

        const byId = new Map((result.entries ?? []).map((e) => [e.entity, e]));
        const errors = errorByEntity(result.errors);
        const now = clock.now();
        for (const item of group) {
          if (!pending.includes(item)) continue;
          item.rec.finalizePolls = (item.rec.finalizePolls ?? 0) + 1;
          item.rec.finalizeWaitMs = now - start;
          const hit = byId.get(item.row.entity);
          if (!hit) {
            const msg = errors.get(item.row.entity) ?? `finalize result omitted ${item.row.entity}`;
            markUnconfirmed(item, msg);
            continue;
          }

          if (hit.staleRows != null) item.rec.staleRows = asStaleRows(hit.staleRows);

          if (hit.phase === PHASE_TAGGED) {
            item.rec.phase = PHASE_TAGGED;
            item.rec.taggedAt = isoNow();
            item.rec.destroyTagAdded = hit.destroyTagAdded === true;
            item.rec.pending = [];
            if (item.attachReadback) {
              const store = ensureAttachedBuffer(item.rec);
              store.atTagged = await inspectAttachedBuffer(side, item.row.entity);
            }
            tagged.push(item.row);
            drop(item);
            continue;
          }

          if (hit.phase === PHASE_STUCK) {
            const msg = hit.reason ? errorText(hit.reason) : stuckReason(PHASE_STUCK, item.listed ?? item.pending);
            markUnconfirmed(item, msg, { phase: PHASE_STUCK, pending: asBuffIds(hit.pending ?? item.pending) });
            continue;
          }

          if (hit.phase === PHASE_BUFFS) {
            const neu = asBuffIds(hit.buffs);
            item.listed = mergeBuffIds(item.listed ?? item.buffs, neu);
            item.rec.buffs = item.listed;
            const nextCycle = typeof hit.cycle === 'number' ? hit.cycle : (item.rec.cycles ?? 0) + 1;
            item.rec.cycles = nextCycle;
            item.rec.phase = PHASE_BUFFS;
            if (item.rec.buffsAt == null) item.rec.buffsAt = isoNow();
            if (nextCycle > MAX_BUFF_CYCLES) {
              const msg = hit.reason ? errorText(hit.reason) : stuckReason(PHASE_STUCK, item.listed);
              markUnconfirmed(item, msg, { phase: PHASE_STUCK, pending: item.listed });
            }
            continue;
          }

          if (hit.phase === PHASE_WAITING_BUFFS) {
            const still = asBuffIds(hit.pending ?? item.pending);
            item.pending = still;
            item.rec.pending = still;
            item.rec.phase = PHASE_WAITING_BUFFS;
            continue;
          }

          const observed = hit.phase == null || hit.phase === '' ? 'no phase' : String(hit.phase);
          const still = asBuffIds(hit.pending ?? item.pending);
          if (KNOWN_FINALIZE_PHASES.has(hit.phase)) {
            markUnconfirmed(item, stuckReason(hit.phase, still), { phase: hit.phase, pending: still });
          } else {
            const msg = contractMismatchFinding(observed);
            noteFinding(msg);
            markUnconfirmed(item, msg, { phase: hit.phase ?? item.rec.phase, pending: still });
          }
        }
      }

      if (!pending.length) break;
      if (clock.now() >= deadline) break;
      await clock.sleep(interval);
    }

    for (const item of pending) {
      const still = asBuffIds(item.pending ?? item.listed ?? item.buffs);
      const phase = item.rec.phase ?? PHASE_WAITING_BUFFS;
      const msg = stuckReason(phase, still);
      item.rec.error = msg;
      item.rec.pending = still;
      item.rec.finalizeWaitMs = clock.now() - start;
      unconfirmed.push({
        ...item.row,
        error: msg,
        pending: still,
        phase,
        buffs: item.rec.buffs,
        staleRows: item.rec.staleRows,
        cycles: item.rec.cycles,
      });
    }

    return { tagged, unconfirmed };
  }

  function attachAbsenceLatency(entityRecs, rows, elapsedMs) {
    const ids = new Set(rows.map((r) => r.entity));
    for (const rec of entityRecs) {
      if (ids.has(rec.entity) && rec.error == null) rec.absenceLatencyMs = elapsedMs;
    }
  }

  async function destroyThenConfirm(toDestroy, toCheck, opts = {}) {
    const start = clock.now();
    let destroyError = null;
    let phase = { entities: [], unconfirmed: [], toExists: [] };
    try {
      if (toDestroy.length) phase = await threePhaseDestroy(toDestroy, opts);
    } catch (e) {
      destroyError = e && e.message ? e.message : String(e);
    }

    const skip = new Set(phase.unconfirmed.map((e) => e.entity));
    const check = (toCheck ?? toDestroy).filter((e) => !skip.has(e.entity));
    const poll = await pollAbsent(check, opts);
    attachAbsenceLatency(phase.entities, phase.toExists, poll.elapsedMs);

    const unconfirmed = [
      ...phase.unconfirmed.map((row) => {
        const rec = phase.entities.find((e) => e.entity === row.entity);
        return rec ? { ...row, ...rec, error: row.error ?? rec.error } : row;
      }),
      ...poll.unconfirmed,
    ];
    const result = {
      ok: poll.remaining.length === 0 && unconfirmed.length === 0,
      remaining: poll.remaining,
      unconfirmed,
      elapsedMs: clock.now() - start,
      entities: formatDestroyEntities(phase.entities),
      findings,
    };
    if (destroyError) result.error = destroyError;
    return result;
  }

  async function cleanupHostile(opts = {}) {
    const hostiles = entries.filter((e) => e.hostile && !e.skipCleanup);
    const check = entries.filter((e) => e.hostile);
    return destroyThenConfirm(hostiles, check, opts);
  }

  async function cleanupAll(opts = {}) {
    const includeSkipped = !!opts.includeSkipped;
    const targets = entries.filter((e) => includeSkipped || !e.skipCleanup);
    const ordered = [
      ...targets.filter((e) => e.hostile),
      ...targets.filter((e) => !e.hostile),
    ];
    return destroyThenConfirm(ordered, entries, opts);
  }

  async function destroyNamed(name, opts = {}) {
    const row = entries.find((e) => e.name === name);
    if (!row) throw new Error(`no tracked entity named '${name}'`);
    return destroyThenConfirm([row], [row], opts);
  }

  /** Destroy the given rows through the entity op without adopting them into the tracker. */
  async function destroyRows(rows, opts = {}) {
    const list = rows ?? [];
    if (!list.length) return { ok: true, remaining: [], unconfirmed: [], elapsedMs: 0, entities: [] };
    return destroyThenConfirm(list, list, opts);
  }

  async function existsSweep() {
    if (!entries.length) return { ok: true, remaining: [], unconfirmed: [] };
    const sweep = await requestExists(entries);
    return {
      ok: sweep.remaining.length === 0 && sweep.unconfirmed.length === 0,
      remaining: sweep.remaining,
      unconfirmed: sweep.unconfirmed,
    };
  }

  async function teardownSkipped(opts = {}) {
    const skipped = entries.filter((e) => e.skipCleanup);
    if (!skipped.length) return { ok: true, remaining: [], unconfirmed: [], elapsedMs: 0, entities: [] };
    return destroyThenConfirm(skipped, skipped, opts);
  }

  function hasHostile() {
    return entries.some((e) => e.hostile);
  }

  function formatRemaining(rows) {
    return (rows ?? []).map((e) => ({
      name: e.name,
      side: e.side,
      entity: e.entity,
      prefab: e.prefab,
      hostile: !!e.hostile,
      skipCleanup: !!e.skipCleanup,
    }));
  }

  function formatUnconfirmed(rows) {
    return (rows ?? []).map((e) => ({
      name: e.name,
      side: e.side,
      entity: e.entity,
      prefab: e.prefab,
      hostile: !!e.hostile,
      skipCleanup: !!e.skipCleanup,
      error: errorText(e.error),
      pending: e.pending,
      phase: e.phase,
      buffs: e.buffs ?? e.buffsFound,
      staleRows: e.staleRows,
      cycles: e.cycles ?? 0,
      buffsAt: e.buffsAt ?? null,
      taggedAt: e.taggedAt ?? null,
      finalizeWaitMs: e.finalizeWaitMs,
      finalizePolls: e.finalizePolls,
      attachedBuffer: e.attachedBuffer,
    }));
  }

  function formatDestroyEntities(rows) {
    return (rows ?? []).map((e) => ({
      name: e.name,
      side: e.side,
      entity: e.entity,
      prefab: e.prefab,
      hostile: !!e.hostile,
      skipCleanup: !!e.skipCleanup,
      phase: e.phase,
      buffs: e.buffs ?? e.buffsFound ?? [],
      staleRows: e.staleRows ?? [],
      buffSweepComplete: e.buffSweepComplete,
      cycles: e.cycles ?? 0,
      buffsAt: e.buffsAt ?? null,
      taggedAt: e.taggedAt ?? null,
      finalizeWaitMs: e.finalizeWaitMs ?? null,
      finalizePolls: e.finalizePolls ?? 0,
      absenceLatencyMs: e.absenceLatencyMs ?? null,
      destroyTagAdded: e.destroyTagAdded,
      pending: e.pending,
      attachedBuffer: e.attachedBuffer,
      error: e.error != null ? errorText(e.error) : undefined,
    }));
  }

  return {
    adopt,
    adoptSpawn,
    get,
    list,
    resolve,
    markSkipCleanup,
    setForbidden,
    cleanupHostile,
    cleanupAll,
    destroyNamed,
    destroyRows,
    existsSweep,
    teardownSkipped,
    hasHostile,
    formatRemaining,
    formatUnconfirmed,
    formatDestroyEntities,
    findings,
  };
}
