// Exact request/result shape of the Dev bridge `entity` op. Adjust only here when that
// contract moves. Request: {action, entities, reason?, buffs?}. Result: {action, count, entries, errors}.
//
// DIAG5 destroy protocol (two actions): destroy destroys copied buffs and returns
// phase "buffs". finalize is waiting-buffs (listed ids still exist) | buffs (new live
// buffs, cycle; cap 4 -> stuck) | tagged (unit DestroyTagged). There is no detach /
// waiting-detach phase. exists is unchanged.

export const ENTITY_OP = 'entity';
export const INSPECT_OP = 'inspect';

export const ACTIONS = Object.freeze(['exists', 'destroy', 'finalize']);
export const REASONS = Object.freeze(['None', 'TryRemoveBuff']);

export const ACTION_EXISTS = 'exists';
export const ACTION_DESTROY = 'destroy';
export const ACTION_FINALIZE = 'finalize';
export const REASON_NONE = 'None';
export const REASON_TRY_REMOVE_BUFF = 'TryRemoveBuff';

/** Older Dev image; destroy returning this is a contract mismatch (no fallback). */
export const PHASE_DETACH = 'detach';
export const PHASE_WAITING_DETACH = 'waiting-detach';

export const PHASE_BUFFS = 'buffs';
export const PHASE_WAITING_BUFFS = 'waiting-buffs';
export const PHASE_TAGGED = 'tagged';
export const PHASE_STUCK = 'stuck';

/** New-buff finalize cycles before the op (and the tracker) report stuck. */
export const MAX_BUFF_CYCLES = 4;

/** Ordered finalize phases for the DIAG5 contract. */
export const FINALIZE_PHASES = Object.freeze([
  PHASE_WAITING_BUFFS,
  PHASE_BUFFS,
  PHASE_TAGGED,
  PHASE_STUCK,
]);

export const ATTACHED_BUFFER_INSPECT = Object.freeze({
  only: Object.freeze(['AttachedBuffer']),
  depth: 16,
});

export function contractMismatchFinding(observed) {
  return `entity op contract mismatch: ${observed}`;
}

export function observeDestroyContract(entry) {
  if (!entry || entry.phase == null || entry.phase === '') return 'no phase';
  return String(entry.phase);
}

/** True when destroy returned the DIAG5 `buffs` entry. */
export function isExpectedDestroyPhase(entry) {
  return !!(entry && entry.phase === PHASE_BUFFS);
}

/**
 * Older images: destroy omitted `phase`, or returned `detach` (DIAG4).
 * Either is a contract mismatch: do not finalize or tag.
 */
export function isLegacyDestroyContract(entry) {
  const observed = observeDestroyContract(entry);
  return observed === 'no phase' || observed === PHASE_DETACH;
}

export function inspectAttachedBufferRequest(entity) {
  return { entity: String(entity), only: [...ATTACHED_BUFFER_INSPECT.only], depth: ATTACHED_BUFFER_INSPECT.depth };
}

function asEntityList(entities) {
  if (entities == null) return [];
  const list = Array.isArray(entities) ? entities : [entities];
  return list.map((e) => {
    if (typeof e === 'string') return e;
    if (e && typeof e === 'object' && e.entity) return String(e.entity);
    throw new Error(`entity id must be "index:version", got ${JSON.stringify(e)}`);
  });
}

function asBuffMap(buffsByEntity) {
  const out = {};
  if (buffsByEntity == null) return out;
  if (typeof buffsByEntity !== 'object' || Array.isArray(buffsByEntity)) {
    throw new Error(`finalize buffs must be { "<unit id>": [buff ids] }, got ${JSON.stringify(buffsByEntity)}`);
  }
  for (const [id, buffs] of Object.entries(buffsByEntity)) {
    out[String(id)] = asEntityList(buffs ?? []);
  }
  return out;
}

/** Build args for op "entity". `reason` is sent only for destroy. `buffs` only for finalize. */
export function buildArgs(action, entities, extra) {
  if (!ACTIONS.includes(action)) throw new Error(`entity action must be exists|destroy|finalize, got ${action}`);
  const args = { action, entities: asEntityList(entities) };
  if (action === ACTION_DESTROY) {
    const r = extra && typeof extra === 'object' && !Array.isArray(extra) && extra.reason !== undefined
      ? extra.reason
      : (extra ?? REASON_NONE);
    if (!REASONS.includes(r)) throw new Error(`entity reason must be None|TryRemoveBuff, got ${r}`);
    args.reason = r;
  }
  if (action === ACTION_FINALIZE) {
    args.buffs = extra && typeof extra === 'object' && extra.buffs !== undefined
      ? asBuffMap(extra.buffs)
      : asBuffMap(extra);
  }
  return args;
}

export function existsRequest(entities) {
  return buildArgs(ACTION_EXISTS, entities);
}

export function destroyRequest(entities, reason = REASON_NONE) {
  return buildArgs(ACTION_DESTROY, entities, reason);
}

/** Finalize: drive waiting-buffs | buffs (cycle) | tagged. Unit DestroyTag only at tagged. */
export function finalizeRequest(entities, buffsByEntity = {}) {
  return buildArgs(ACTION_FINALIZE, entities, { buffs: buffsByEntity });
}

/**
 * Normalize a result to {action, count, entries:[{entity, exists, ...}], errors:[]}.
 * Unknown extra fields on each entry are preserved.
 */
export function parseResult(result) {
  if (result == null || typeof result !== 'object') {
    return { action: undefined, count: 0, entries: [], errors: [{ error: 'empty entity result' }] };
  }
  const entries = Array.isArray(result.entries) ? result.entries.map(normalizeEntry) : [];
  const errors = Array.isArray(result.errors) ? result.errors : [];
  const count = typeof result.count === 'number' ? result.count : entries.length;
  return { action: result.action, count, entries, errors };
}

function normalizeEntry(row) {
  if (row == null || typeof row !== 'object') return { entity: String(row), exists: undefined };
  return { ...row, entity: row.entity != null ? String(row.entity) : undefined, exists: row.exists };
}

/** Entries that still report exists=true (missing exists is treated as present). */
export function stillPresent(result) {
  const parsed = result && result.entries ? result : parseResult(result);
  return parsed.entries.filter((e) => e.exists !== false);
}

export const ENTITY_OP_CONTRACT = Object.freeze({
  op: ENTITY_OP,
  request: {
    action: 'exists | destroy | finalize',
    entities: ['index:version'],
    reason: 'None | TryRemoveBuff  (destroy only)',
    buffs: { '<unit id>': ['index:version'] },
  },
  result: {
    action: 'exists | destroy | finalize',
    count: 'number',
    entries: [{
      entity: 'index:version',
      exists: 'boolean (exists action)',
      phase: 'buffs (destroy) | waiting-buffs | buffs (cycle) | tagged | stuck (finalize)',
      buffs: ['index:version'],
      staleRows: '...',
      buffSweepComplete: 'boolean',
      reason: '...',
      pending: ['index:version'],
      cycle: 'number (finalize buffs)',
      destroyTagAdded: 'boolean',
      extra: '...',
    }],
    errors: ['...'],
  },
  inspectAttachedBuffer: {
    op: INSPECT_OP,
    args: { entity: 'index:version', only: ['AttachedBuffer'], depth: 16 },
  },
});
