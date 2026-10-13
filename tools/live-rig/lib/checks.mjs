import { readLatestSeq, waitForLogs, lineContains } from './logs.mjs';
import { realClock } from './clock.mjs';
import { prefabDisplayName } from './tracker.mjs';

/** Client replicas of this prefab outlive server disposal; the rig destroys only those deltas. */
export const NETWORKED_SEQUENCE_PREFAB = 'NetworkedSequence';

/** EntityOps row limit used for the carrier query. Hitting it is not a complete world. */
export const CARRIER_QUERY_LIMIT = 200;

const PROTECTION_RE = /flags=(\d+)\s+immortal=(\S+)\s+guard=(\S+)\s+prefixes=(\d+)\/(\d+)\s+drain=(\S+)\s+aggro=(\S+)\s+persists=(\S+)/;

/** Dispatcher reply when `.debug status` is sent by a non-admin. Never a dropped-guard parse. */
export const NON_ADMIN_PROTECTION_REFUSAL_RE = /this command is admin-only|only (?:server )?administrators can (?:change|run|use)/i;

/** Report evidence when the status command was refused for lack of admin. */
export const PROTECTION_UNAVAILABLE = 'unavailable (non-admin refusal)';

function isTrue(v) {
  return String(v).toLowerCase() === 'true';
}

export function isNonAdminProtectionRefusal(text) {
  return NON_ADMIN_PROTECTION_REFUSAL_RE.test(String(text ?? ''));
}

/**
 * Parse a Core `.debug status` log line (`Debug: status character=... flags=...`).
 * ok only when flags=7, guard=True, prefixes a==b, drain/aggro/persists True.
 * Extra flag bits (live frost-dash `flags=68719477263`) are not accepted: SampleFrostDash
 * does not write BuffableFlagState, so the extra bits are unexplained by source.
 * A non-admin refusal is never parsed as guard=False: value stays null and unavailable is set.
 */
export function parseProtectionLine(text) {
  const line = String(text ?? '');
  if (isNonAdminProtectionRefusal(line)) {
    return {
      ok: false,
      unavailable: true,
      value: null,
      evidence: line,
      reason: PROTECTION_UNAVAILABLE,
    };
  }
  const m = PROTECTION_RE.exec(line);
  if (!m) {
    return { ok: false, unavailable: false, value: null, evidence: line, reason: 'no flags=... protection fields' };
  }
  const flags = Number(m[1]);
  const immortal = m[2];
  const guard = m[3];
  const prefixesA = Number(m[4]);
  const prefixesB = Number(m[5]);
  const drain = m[6];
  const aggro = m[7];
  const persists = m[8];
  const value = {
    flags,
    immortal: isTrue(immortal),
    guard: isTrue(guard),
    prefixes: `${prefixesA}/${prefixesB}`,
    prefixesA,
    prefixesB,
    drain: isTrue(drain),
    aggro: isTrue(aggro),
    persists: isTrue(persists),
  };
  const ok = flags === 7
    && value.guard
    && prefixesA === prefixesB
    && value.drain
    && value.aggro
    && value.persists;
  return { ok, unavailable: false, value, evidence: line };
}

function unwrapNumber(v) {
  if (typeof v === 'number') return v;
  if (v && typeof v === 'object') {
    if (typeof v.Value === 'number') return v.Value;
    if (typeof v._Value === 'number') return v._Value;
  }
  return v;
}

function unwrapBool(v) {
  if (typeof v === 'boolean') return v;
  if (v && typeof v === 'object') {
    if (typeof v.Value === 'boolean') return v.Value;
    if (typeof v._Value === 'boolean') return v._Value;
  }
  if (String(v).toLowerCase() === 'true') return true;
  if (String(v).toLowerCase() === 'false') return false;
  return v;
}

function checkResult(name, ok, value, evidence) {
  return { name, ok: !!ok, value, evidence };
}

export async function playerHealth(ctx) {
  const { bridge, character, side } = ctx;
  const got = await bridge.call(side, 'get', { entity: character, component: 'Health' });
  const fields = got?.fields ?? {};
  const value = unwrapNumber(fields.Value);
  const isDead = unwrapBool(fields.IsDead);
  const ok = isDead === false;
  return checkResult('playerHealth', ok, { Value: value, IsDead: isDead }, got);
}

function protectionUnavailableResult(evidence) {
  return {
    name: 'protection',
    ok: false,
    unavailable: true,
    value: null,
    evidence: evidence || PROTECTION_UNAVAILABLE,
  };
}

export async function protection(ctx) {
  const { bridge, user, clock = realClock(), timeoutMs = 3000 } = ctx;
  const since = await readLatestSeq(bridge, 'server');
  await bridge.call('server', 'command', { text: '.debug status', user });
  // Core logs the readback as "Debug: status ... flags=...". When the Sample is loaded its
  // chat logger may also echo "Sample chat: saw ... text=\".debug status\"". Only the
  // "Debug: status" line carrying a numeric flags= is the readback; an earlier qualification
  // run picked the echo and reported the protection as unreadable.
  // A non-admin caller gets "This command is admin-only." (no flags=). That refusal must
  // not be treated as a dropped guard, and an empty filter is required so the reply is
  // collected (it does not contain "Debug: status").
  const isStatusLine = (text) => /Debug: status /.test(text) && /flags=\d+/.test(text);
  const isRefusalLine = (text) => isNonAdminProtectionRefusal(text);
  const waited = await waitForLogs(bridge, 'server', {
    since,
    filter: '',
    match: (text) => isStatusLine(text) || isRefusalLine(text),
    timeoutMs,
    pollMs: 100,
    clock,
  });
  const collected = [...(waited.collected ?? [])].reverse();
  const statusLine = collected.find((l) => isStatusLine(String(l.text ?? '')));
  const refusalLine = collected.find((l) => isRefusalLine(String(l.text ?? '')));
  if (statusLine) {
    const text = String(statusLine.text ?? '');
    const parsed = parseProtectionLine(text);
    if (parsed.unavailable) return protectionUnavailableResult(PROTECTION_UNAVAILABLE);
    return checkResult('protection', parsed.ok, parsed.value, parsed.evidence);
  }
  if (refusalLine) {
    return protectionUnavailableResult(PROTECTION_UNAVAILABLE);
  }
  const text = '';
  const parsed = parseProtectionLine(text);
  if (!waited.ok && !parsed.value) {
    return checkResult('protection', false, parsed.value, `no debug status line within ${timeoutMs} ms`);
  }
  return checkResult('protection', parsed.ok, parsed.value, parsed.evidence);
}

export async function adminState(ctx) {
  const { bridge, userName, expectedAdmin, side } = ctx;
  const got = await bridge.call(side, 'users');
  const users = got?.users ?? [];
  const row = users.find((u) => u.name === userName || String(u.index) === String(userName));
  if (!row) {
    return checkResult('adminState', false, null, { error: `user '${userName}' not in users`, users });
  }
  const ok = row.admin === expectedAdmin;
  return checkResult('adminState', ok, { name: row.name, admin: row.admin, expected: expectedAdmin }, got);
}

export function carrierEntityId(row) {
  if (row == null) return null;
  if (typeof row === 'string' && row) return row;
  if (typeof row.entity === 'string' && row.entity) return row.entity;
  return null;
}

export function carrierPrefabName(row) {
  if (row == null) return null;
  return prefabDisplayName(row.prefab) ?? (typeof row === 'string' ? null : prefabDisplayName(row));
}

export function isNetworkedSequenceCarrier(row) {
  return carrierPrefabName(row) === NETWORKED_SEQUENCE_PREFAB;
}

export function idsFromCarrierQuery(got) {
  return (got?.entities ?? []).map(carrierEntityId).filter(Boolean).map(String);
}

export function carrierIdsEqual(a, b) {
  const left = new Set((a ?? []).map(String));
  const right = new Set((b ?? []).map(String));
  if (left.size !== right.size) return false;
  for (const id of left) {
    if (!right.has(id)) return false;
  }
  return true;
}

export function expectedCarrierIds(expected, side) {
  const v = expected?.[side];
  if (Array.isArray(v)) return v.map(String);
  if (v && typeof v === 'object' && Array.isArray(v.ids)) return v.ids.map(String);
  return [];
}

function leftoverCarrierEntities(got, expectedIds) {
  const baseline = new Set((expectedIds ?? []).map(String));
  return (got?.entities ?? []).filter((row) => {
    const id = carrierEntityId(row);
    return id && !baseline.has(String(id));
  });
}

/**
 * True when the carrier query cannot be treated as the full match set.
 * `truncated` is the hard scan cap; stopping at the row limit is not truncated, so
 * `total > entities.length` and `entities.length === limit` are also a cap.
 */
export function carrierQueryCapReason(got, side, limit = CARRIER_QUERY_LIMIT) {
  const entities = got?.entities ?? [];
  const n = entities.length;
  const label = side ? `${side} carrier query` : 'carrier query';
  if (got?.truncated === true) {
    return `${label} truncated (scan cap)`;
  }
  const total = got?.total;
  if (typeof total === 'number' && total > n) {
    return `${label} hit the ${limit}-row cap (total ${total} > ${n} rows)`;
  }
  if (n === limit) {
    return `${label} hit the ${limit}-row cap`;
  }
  return null;
}

export async function queryCarriers(bridge, position, radius = 60) {
  const near = { x: position.x, y: position.y, z: position.z, radius };
  const counts = {};
  const ids = {};
  const evidence = {};
  const totals = {};
  const truncated = {};
  const capReasons = [];
  for (const side of ['client', 'server']) {
    const got = await bridge.call(side, 'query', {
      all: ['SpawnSequenceForEntity'],
      none: [],
      near,
      limit: CARRIER_QUERY_LIMIT,
      total: true,
    });
    const extracted = idsFromCarrierQuery(got);
    ids[side] = extracted;
    counts[side] = typeof got?.count === 'number' ? got.count : extracted.length;
    totals[side] = typeof got?.total === 'number' ? got.total : extracted.length;
    truncated[side] = got?.truncated === true;
    evidence[side] = got;
    const cap = carrierQueryCapReason(got, side);
    if (cap) capReasons.push(cap);
  }
  return {
    counts,
    ids,
    evidence,
    totals,
    truncated,
    capReason: capReasons.length ? capReasons.join('; ') : null,
  };
}

/**
 * Destroy client-side delta NetworkedSequence carriers through the entity op.
 * Baseline ids, server-side carriers, and any other prefab are left untouched.
 */
export async function cleanupClientCarriers(ctx) {
  const { bridge, tracker, position, baselineIds = [], findings, radius = 60 } = ctx;
  const baseline = new Set((baselineIds ?? []).map(String));
  const current = await queryCarriers(bridge, position, radius);
  const delta = leftoverCarrierEntities(current.evidence.client, [...baseline]);

  const toDestroy = [];
  const remaining = [];
  for (const row of delta) {
    const id = String(carrierEntityId(row));
    if (isNetworkedSequenceCarrier(row)) {
      toDestroy.push({
        name: `client-carrier:${id}`,
        entity: id,
        side: 'client',
        prefab: NETWORKED_SEQUENCE_PREFAB,
        hostile: false,
        skipCleanup: false,
      });
    } else {
      remaining.push(id);
    }
  }

  const destroyed = [];
  if (toDestroy.length) {
    const msg = `client carrier outlived server disposal: ${toDestroy.map((r) => r.entity).join(', ')}`;
    if (Array.isArray(findings) && !findings.includes(msg)) findings.push(msg);
    const result = await tracker.destroyRows(toDestroy);
    const failed = new Set([
      ...(result.remaining ?? []).map((r) => String(r.entity)),
      ...(result.unconfirmed ?? []).map((r) => String(r.entity)),
    ]);
    for (const row of toDestroy) {
      if (failed.has(row.entity)) remaining.push(row.entity);
      else destroyed.push(row.entity);
    }
  }

  return { destroyed, remaining };
}

export async function remainingTestEntities(ctx) {
  try {
    const sweep = await ctx.tracker.existsSweep();
    const remaining = ctx.tracker.formatRemaining(sweep.remaining);
    const unconfirmed = ctx.tracker.formatUnconfirmed(sweep.unconfirmed ?? []);
    const ok = !!sweep.ok && remaining.length === 0 && unconfirmed.length === 0;
    return checkResult('remainingTestEntities', ok, { remaining, unconfirmed }, sweep);
  } catch (e) {
    const unconfirmed = ctx.tracker.formatUnconfirmed(
      ctx.tracker.list().map((row) => ({ ...row, error: e.message })),
    );
    return checkResult('remainingTestEntities', false, { remaining: [], unconfirmed, error: e.message }, e.message);
  }
}

export async function carriers(ctx) {
  const { bridge, position, expected, radius = 60, clock = realClock(), settleMs = 10000, pollMs = 500 } = ctx;
  // Compare id sets, not counts. Client NetworkedSequence deltas are destroyed at
  // cleanup; this check waits for server-side transients (and anything else still
  // present) to match the start-of-run baseline. It never destroys anything.
  const expectedIds = {
    client: expectedCarrierIds(expected, 'client'),
    server: expectedCarrierIds(expected, 'server'),
  };
  const start = clock.now();
  let last = { counts: {}, ids: {}, evidence: {} };
  let polls = 0;
  for (;;) {
    last = await queryCarriers(bridge, position, radius);
    polls++;
    if (last.capReason) {
      return checkResult('carriers', false, {
        counts: last.counts,
        ids: last.ids,
        expected: expectedIds,
        polls,
        elapsedMs: clock.now() - start,
        capReason: last.capReason,
        leftover: {
          client: leftoverCarrierEntities(last.evidence.client, expectedIds.client),
          server: leftoverCarrierEntities(last.evidence.server, expectedIds.server),
        },
      }, last.evidence);
    }
    const settled = carrierIdsEqual(last.ids.client, expectedIds.client)
      && carrierIdsEqual(last.ids.server, expectedIds.server);
    if (settled) {
      return checkResult('carriers', true, {
        counts: last.counts,
        ids: last.ids,
        expected: expectedIds,
        polls,
        elapsedMs: clock.now() - start,
      }, last.evidence);
    }
    if (clock.now() - start >= settleMs) {
      return checkResult('carriers', false, {
        counts: last.counts,
        ids: last.ids,
        expected: expectedIds,
        polls,
        elapsedMs: clock.now() - start,
        leftover: {
          client: leftoverCarrierEntities(last.evidence.client, expectedIds.client),
          server: leftoverCarrierEntities(last.evidence.server, expectedIds.server),
        },
      }, last.evidence);
    }
    await clock.sleep(pollMs);
  }
}

export async function runFinalChecks(ctx) {
  const out = [];
  const skipConnected = ctx.world === 'main-menu';
  if (!skipConnected) {
    out.push(await playerHealth(ctx));
    out.push(await protection(ctx));
    if (typeof ctx.beforeAdminState === 'function') await ctx.beforeAdminState();
    out.push(await adminState(ctx));
  }
  out.push(await remainingTestEntities(ctx));
  if (!skipConnected) {
    out.push(await carriers(ctx));
  }
  return out;
}

export async function rearmProtection(ctx) {
  await ctx.bridge.call('server', 'command', { text: '.debug on', user: ctx.user });
  return protection(ctx);
}
