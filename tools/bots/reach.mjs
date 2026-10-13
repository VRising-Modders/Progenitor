// Reachability and progress bookkeeping, pure (no bridge): the Progress monitor for approaches and
// chases, the unreachable-target blacklist, and typed failure reasons. The path queries themselves
// live on the Bot (bot.mjs reachable()); the brain acts on the types (brain.mjs record()).
// Background: internal research notes.

export const WALK_SPEED = 4.3;        // m/s, measured walk (4.3-4.5)
export const PROGRESS_WINDOW_MS = 6000;
export const PROGRESS_FACTOR = 0.4;   // a window must gain this share of what walking would give
export const PROGRESS_FAILURES = 2;   // consecutive short windows that make a stall
export const REACH_CELL = 3;          // meters: one unit seen by the client and the server (ids differ)
export const BLACKLIST_MS = 300000;   // unreachable/stalled targets are left alone for 5 minutes

export const cellOf = (p, size = REACH_CELL) => `${Math.floor((p?.x ?? 0) / size)},${Math.floor((p?.z ?? 0) / size)}`;

/** Meters along a route (start -> waypoints), on the ground plane. */
export function routeLength(from, route) {
  let len = 0, prev = from;
  for (const w of route ?? []) { len += Math.hypot(w.x - prev.x, w.z - prev.z); prev = w; }
  return len;
}

/**
 * Progress toward a goal, checked against what walking should give: over each window the gain must
 * reach `speed * dt * factor` (or the whole remaining distance, when that is less); after `failures`
 * short windows in a row it reports {stalled, cell}. A standstill check (moveTo: 0.05 m in 2.5 s)
 * misses a bot that runs in place against a ledge, circles a fence or creeps; a 25 s no-swing timer
 * found them only after 25-50 s (live 2026-09-25).
 *
 * update({position, remaining}) measures meters still to go (along a route); update({position, goal})
 * measures toward the goal as it stood when the window opened, so chasing a unit that runs off still
 * counts as progress while the bot runs at where it was.
 */
export class Progress {
  constructor({ speed = WALK_SPEED, windowMs = PROGRESS_WINDOW_MS, factor = PROGRESS_FACTOR, failures = PROGRESS_FAILURES, near = 1.5 } = {}) {
    Object.assign(this, { speed, windowMs, factor, maxFailures: failures, near });
    this.failures = 0;
    this.window = null;
  }

  /** Forget everything (the bot got there, or started something that is not an approach). */
  reset() { this.failures = 0; this.window = null; }
  /** Start a fresh window but keep the failure count (a new route: its distances are not comparable). */
  restart() { this.window = null; }

  update({ position, remaining, goal, now = Date.now() }) {
    const left = remaining ?? Math.hypot(goal.x - position.x, goal.z - position.z);
    const w = this.window;
    if (!w) { this.window = { t0: now, r0: left, goal }; return null; }
    const measured = remaining ?? Math.hypot(w.goal.x - position.x, w.goal.z - position.z);
    const dt = now - w.t0;
    if (dt < this.windowMs) return null;
    const gained = w.r0 - measured;
    const expected = this.speed * (dt / 1000) * this.factor;
    const ok = w.r0 <= this.near || gained >= Math.min(expected, w.r0 - this.near);
    this.failures = ok ? 0 : this.failures + 1;
    this.window = { t0: now, r0: left, goal };
    const r = { ok, gained: Math.round(gained * 10) / 10, expected: Math.round(expected * 10) / 10 };
    return this.failures >= this.maxFailures ? { ...r, stalled: true, cell: cellOf(position) } : r;
  }
}

// ---- blacklist ----------------------------------------------------------------------------------
// Keyed by entity AND by prefab name in a 3 m cell: bot_threats (server) and bot_look (client) name
// different entity ids for one unit (skills.mjs sameUnit), and a unit that respawns at the same
// unreachable spot comes back with a new id.

export const blacklistKeys = (t) => [
  ...(t?.entity != null ? [`e:${t.entity}`] : []),
  ...(t?.name && typeof t.x === "number" && typeof t.z === "number" ? [`n:${t.name}@${cellOf(t)}`] : []),
];

/** Leave `t` alone until now + ms. `bl` is a plain object {key: {until, x?, z?}}. */
export function blacklist(bl, t, now = Date.now(), ms = BLACKLIST_MS) {
  for (const k of blacklistKeys(t)) bl[k] = { until: now + ms, ...(k.startsWith("n:") ? { x: t.x, z: t.z } : {}) };
  return bl;
}

/** Whether `t` is blacklisted: its entity, or the same name within REACH_CELL meters (any cell). */
export function blacklisted(bl, t, now = Date.now()) {
  if (!bl || !t) return false;
  const live = (e) => e && e.until > now;
  if (t.entity != null && live(bl[`e:${t.entity}`])) return true;
  if (!t.name || typeof t.x !== "number" || typeof t.z !== "number") return false;
  const cx = Math.floor(t.x / REACH_CELL), cz = Math.floor(t.z / REACH_CELL);
  for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) {
    const e = bl[`n:${t.name}@${cx + dx},${cz + dz}`];
    if (live(e) && Math.hypot(e.x - t.x, e.z - t.z) < REACH_CELL) return true;
  }
  return false;
}

export function pruneBlacklist(bl, now = Date.now()) {
  for (const [k, e] of Object.entries(bl ?? {})) if (!(e.until > now)) delete bl[k];
  return bl;
}

// ---- typed failures -----------------------------------------------------------------------------

/** A danger() / work-interrupt reason as a typed failure: interrupted:sun | interrupted:down | interrupted:threat. */
export function interruptReason(why) {
  if (!why) return null;
  if (/sun|burn/i.test(why)) return "interrupted:sun";
  if (/^down/i.test(why)) return "interrupted:down";
  return "interrupted:threat"; // taking damage, a hostile at arm's length
}

/**
 * What a failed skill result means for its target: "unreachable" and "stalled" blacklist it at once;
 * "interrupted" (sun, threat, down) says nothing about the target; anything else ("fled", "lost
 * target", "timeout") counts as one ordinary failure.
 */
export function failureKind(r) {
  if (!r || r.ok) return null;
  const reason = String(r.reason ?? "");
  if (reason === "unreachable" || r.unreachable) return "unreachable";
  if (reason === "stalled" || r.stalled) return "stalled";
  if (r.interrupted || reason.startsWith("interrupted")) return "interrupted";
  return "other";
}
