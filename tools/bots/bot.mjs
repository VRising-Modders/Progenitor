// EXPERIMENTAL, EARLY DEVELOPMENT, development-only: headless test-client bots, not a supported framework feature and
// not part of the Release package. Read tools/bots/README.md first; may change or be removed.
// One headless player: eyes (client bot_look), hands (client bot), and the server-side actions a
// key press maps to (server bot_cast, bot_inventory). Skills are plain async functions over these.
import os from "node:os";
import path from "node:path";
import { call } from "./bridge.mjs";
import { Progress, routeLength, interruptReason } from "./reach.mjs";
import { requireWorkspacePath } from "../common/workspace.mjs";

const TEMP = process.env.TEMP ?? os.tmpdir();

const PATH_EXPANSIONS = 150000;
const DASH_COOLDOWN_MS = 8000; // Veil of Shadow; assumed, a refused cast costs nothing
export const SERVER = {
  port: Number(process.env.PROGENITOR_BOT_SERVER_PORT ?? 27362),
  tokenFile: process.env.PROGENITOR_BOT_SERVER_TOKEN_FILE
    ?? requireWorkspacePath("PROGENITOR_BOT_SERVER_TOKEN_FILE", "_testserver", "headless", "server", "BepInEx", "config", "progenitor-dev.token"),
};

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const dist2 = (a, b) => Math.hypot(a.x - b.x, a.z - b.z);
const APPROACH_MAX_DISTANCE = 60; // try ring approach points only for targets this close
const DANGER_POLL_MS = 2000;       // danger() reads vitals at most this often
const DANGER_HEALTH_DROP = 5;      // health lost between two reads that counts as "taking damage"...
const DANGER_HEALTH_FRACTION = 0.8; // ...but only once health is below this fraction (players stop to deal with it early)
const HOSTILE_UNIT = /Skeleton|Ghoul|Bandit|Wolf|Bear|Spider|Zombie|Undead|Militia|Guard|Archer|Knight/i; // = progression HOSTILE

export class Bot {
  constructor(index, name) {
    this.index = index;
    this.name = name;
    this.client = {
      port: 27341 + 10 * index,
      tokenFile: path.join(TEMP, `progenitor-client-${index}`, "progenitor-dev.token"),
    };
  }

  log(...parts) { console.log(`[${new Date().toISOString().slice(11, 19)}] ${this.name}:`, ...parts); }

  // ---- eyes -------------------------------------------------------------------------------
  look(opts = {}) { return call(this.client, "bot_look", opts); }
  async self() { return (await this.look({ radius: 1, limit: 1 })).self; }

  // ---- hands (client input) -----------------------------------------------------------------
  drive(args) { return call(this.client, "bot", args); }
  stop() { return this.drive({ action: "stop" }); }
  aim(p) { return this.drive({ action: "aim", x: p.x, y: p.y ?? 5, z: p.z }); }

  /**
   * Why a long action should hand control back to the brain right now, or null: down, burning or
   * charging in the sun, or losing health fast. Vitals are read at most every 2 s. Live 2026-09-24 a
   * bot burned to death at dawn in the middle of a 35 s walk to a rock, because the brain's sun rule
   * only runs between actions.
   */
  async danger() {
    if (Date.now() - (this._dangerAt ?? 0) < DANGER_POLL_MS) return this.survival ? null : this._danger ?? null;
    this._dangerAt = Date.now();
    const v = await this.vitals().catch(() => null);
    let why = null;
    if (v) {
      if (v.dead || (v.health ?? 1) <= 0) why = "down";
      // Exposure is a budget (5 s of sun from 0 to 1, damage only at 1): a day hop spends most of it
      // on purpose, anything else hands back at half.
      else if (v.sun?.burning || (v.sun?.inSun && (v.sun.charge ?? 0) > (this.dayWalking ? 0.7 : 0.3))) why = "sun"; // 0.3: shade searches need the budget (bots entering them at 0.7-0.87 burned, live)
      // Chip damage while healthy does not stop work or a trip (a player keeps running when a
      // crossbow plinks from range; live, trips out of the graveyard stalled on every hit).
      else if (!this.fighting && this._lastHealth != null && v.health < this._lastHealth - DANGER_HEALTH_DROP
        && v.health < (v.maxHealth ?? 100) * DANGER_HEALTH_FRACTION) why = "taking damage";
      this._lastHealth = v.health;
    }
    this._danger = why;
    // The survival action itself (shade, flee, recover) must not yield, but health is still tracked
    // so the next action does not see a stale drop as fresh damage.
    return this.survival ? null : why;
  }

  /**
   * Walk to (x,z); resolves when arrived, the move timed out, `timeoutMs` passed, or danger() fired.
   * `watch(position)` (a Progress check) ends the walk when it reports {stalled}.
   */
  async moveTo(p, { stop = 1.3, timeoutMs = 20000, watch = null } = {}) {
    await this.drive({ action: "move", x: p.x, z: p.z, stop, timeout: timeoutMs / 1000 });
    const until = Date.now() + timeoutMs + 1000;
    let last = null, stuckSince = Date.now();
    while (Date.now() < until) {
      await sleep(150);
      const why = await this.danger();
      if (why) { await this.stop(); return { arrived: false, interrupted: why }; }
      const s = await this.drive({ action: "status" });
      if (s.mode === "None") return { arrived: s.arrived, position: s.position, issue: s.issue };
      const w = watch && s.position ? watch(s.position) : null;
      if (w?.stalled) { await this.stop(); return { arrived: false, stalled: true, cell: w.cell, position: s.position }; }
      if (last && dist2(last, s.position) < 0.05) {
        if (Date.now() - stuckSince > 2500) { await this.stop(); return { arrived: false, stuck: true, position: s.position }; }
      } else stuckSince = Date.now();
      last = s.position;
    }
    await this.stop();
    return { arrived: false, timedOut: true };
  }

  /**
   * Ask the server's pathfinder for a route; returns start->target waypoints, or null. Polls up to
   * the server's own 10 s solve window and always releases the request (cancel) when giving up, so
   * no request entity is left behind on the server.
   */
  // 150k expansions: 8000 failed 40 m routes through the graveyard clutter that 300k solved in 0.3 s
  // (live 2026-09-25), which starved bots chasing rats with goTo timing out.
  async path(from, to, maxExpansions = PATH_EXPANSIONS, deadline = Infinity) {
    return (await this.pathResult(from, to, maxExpansions, deadline)).route;
  }

  /**
   * The same query with the kind of answer: {route, status}, status "Success", "Failed" (no route
   * within the expansions), "TimedOut" (the server's 10 s window) or "deadline" (the caller's budget
   * ran out first, so nothing is known).
   */
  async pathResult(from, to, maxExpansions = PATH_EXPANSIONS, deadline = Infinity) {
    const req = await call(SERVER, "bot_path", { action: "request", from, to, maxExpansions });
    let done = false;
    try {
      const until = Math.min(Date.now() + 10500, deadline);
      while (Date.now() < until) {
        await sleep(80);
        const res = await call(SERVER, "bot_path", { action: "result", id: req.id });
        if (res.status === "AwaitingPath") continue;
        done = true;
        const ok = res.status === "Success" && res.waypoints?.length > 0;
        return { route: ok ? [...res.waypoints].reverse() : null, status: ok ? "Success" : res.status }; // buffer runs target -> start
      }
      return { route: null, status: "deadline" };
    } finally {
      if (!done) await call(SERVER, "bot_path", { action: "result", id: req.id, cancel: true }).catch(() => {});
    }
  }

  /**
   * Walk a route waypoint by waypoint. `deadline`: no waypoint starts after it and each walk ends
   * 1.5 s past it (a fight's 6 s chase leg otherwise walked on for up to 15 s per waypoint).
   * `progress` (a Progress) measures the meters still to go along the route and ends the walk with
   * {stalled} when the bot stops gaining.
   */
  async followRoute(route, from, stop, { dash = false, deadline = Infinity, progress = null } = {}) {
    const tail = new Array(route.length).fill(0); // meters from waypoint i to the end
    for (let i = route.length - 2; i >= 0; i--) tail[i] = tail[i + 1] + Math.hypot(route[i + 1].x - route[i].x, route[i + 1].z - route[i].z);
    progress?.restart(); // a new route: its distances are not comparable with the last one's
    for (let i = 0; i < route.length; i++) {
      const last = i === route.length - 1;
      if (!last && Math.hypot(route[i].x - from.x, route[i].z - from.z) < 1) continue;
      if (Date.now() > deadline) return { arrived: false, timedOut: true, waypoint: i, of: route.length };
      // Dash along the route when it is ready: faster than walking (the segment is routed, so the dash
      // goes where the path goes).
      if (dash && Math.hypot(route[i].x - from.x, route[i].z - from.z) > 8) await this.dash(route[i]);
      const watch = progress ? (pos) => progress.update({ position: pos, remaining: Math.hypot(route[i].x - pos.x, route[i].z - pos.z) + tail[i] }) : null;
      const timeoutMs = Math.min(15000, Math.max(3000, deadline - Date.now() + 1500));
      const r = await this.moveTo(route[i], { stop: last ? stop : 0.6, timeoutMs, watch });
      if (r.stalled || (!r.arrived && !last)) return { ...r, waypoint: i, of: route.length };
      if (last) return r;
    }
    return { arrived: true };
  }

  /**
   * Walk to p like a player who knows the map. The server's pathfinder plans within one level; levels
   * joined only by a drop (the crypt ledge, the plateau outside the crypt gate) are not linked in its
   * graph (live 2026-09-24), so when no route exists the bot walks to the farthest point it can reach
   * on its own level toward the target and holds CliffJump walking on, which is how a player jumps
   * down, then re-plans. A sidestep breaks ties when a jump gains nothing.
   *
   * `explore: false` skips the ledge drop and the ring search and returns {unreachable} when neither
   * the target nor a ring point around it routes (a fight chase must not walk 60 s of blind rings).
   * `progress` (a Progress) ends the walk with {stalled} when the bot stops gaining along its route.
   */
  async goTo(p, { stop = 1.3, attempts = 8, timeoutMs = 90000, deadline = Date.now() + timeoutMs, explore = true, progress = null } = {}) {
    let lastResult = { arrived: false };
    for (let attempt = 0; attempt < attempts; attempt++) {
      if (Date.now() > deadline) return { ...lastResult, arrived: false, timedOut: true };
      const me = await this.self();
      if (Math.hypot(p.x - me.x, p.z - me.z) <= stop + 0.3) return { arrived: true, position: me };
      const target = { x: p.x, y: p.y ?? me.y, z: p.z };
      const route = await this.path({ x: me.x, y: me.y, z: me.z }, target, PATH_EXPANSIONS, deadline);
      if (route) {
        lastResult = await this.followRoute(route, me, stop, { deadline, progress });
        if (lastResult.arrived || lastResult.interrupted || lastResult.stalled) return lastResult;
        // The route runs through something the body cannot pass (live: bots wedged at the foot of a
        // crypt-gate ramp on every re-planned route while plain walks, a dash or a jump moved them):
        // shake loose before re-planning.
        if (lastResult.stuck) await this.unstick(p, attempt);
        continue;
      }
      // No route to the target's own tile: a rock, tree or unit occupies it (live 2026-09-24: every
      // rock 10-18 m away had no direct route while points 1.5-2.5 m around it routed in 1-3
      // waypoints). Approach a ring point instead, nearest to the bot first.
      const near = Math.hypot(p.x - me.x, p.z - me.z) < APPROACH_MAX_DISTANCE;
      const ring = near ? await this.approachRoute(me, target, deadline) : null;
      if (ring?.interrupted) return { arrived: false, interrupted: ring.interrupted, position: me };
      if (ring) {
        lastResult = await this.followRoute(ring.route, me, 0.6, { deadline, progress });
        if (lastResult.interrupted || lastResult.stalled) return lastResult;
        const now = await this.self();
        if (Math.hypot(p.x - now.x, p.z - now.z) <= Math.max(stop, ring.radius) + 0.6) return { arrived: true, position: now, approached: ring.radius };
        continue;
      }
      if (!explore) return Date.now() > deadline ? { arrived: false, timedOut: true, position: me } : { arrived: false, unreachable: true, gaveUp: true, position: me };
      // No route at all: explore locally like a player would, preferring directions toward the target,
      // holding CliffJump so ledges are jumped down (live: the crypt-gate plateau has no nav link
      // down, but walking southwest descends a ramp).
      // Target lower than us: the usual reason for no route is a ledge between; walking at it with
      // CliffJump held drops off the edge (cheap, try first).
      if ((p.y ?? me.y) < me.y - 1) {
        const dx = p.x - me.x, dz = p.z - me.z, len = Math.hypot(dx, dz) || 1;
        await this.drive({ action: "hold", buttons: "CliffJump", seconds: 2.2 });
        await this.drive({ action: "walk", dx: dx / len, dz: dz / len, seconds: 2 });
        await sleep(2300);
        const after = await this.self();
        if (after.y < me.y - 1) continue;
      }
      const escaped = await this.explore(p, me, { deadline });
      if (!escaped) {
        const why = await this.danger(); // explore stops early for danger; this read is the cached one
        return why ? { arrived: false, interrupted: why, position: me } : { arrived: false, gaveUp: true, position: me };
      }
    }
    return { ...lastResult, arrived: false, gaveUp: true };
  }

  /**
   * Get out of a spot the pathfinder cannot see is blocked, like a player wiggling free: alternate a
   * back-off-and-sidestep, a Veil of Shadow dash across the blocker's line, and a CliffJump hop.
   */
  async unstick(toward, attempt = 0) {
    const me = await this.self();
    const a = Math.atan2(toward.x - me.x, toward.z - me.z);
    const side = attempt % 2 ? 1 : -1;
    const kind = attempt % 3;
    if (kind === 0) {
      await this.drive({ action: "walk", dx: -Math.sin(a), dz: -Math.cos(a), seconds: 0.7 });
      await sleep(800);
      await this.drive({ action: "walk", dx: Math.sin(a + side * Math.PI / 2), dz: Math.cos(a + side * Math.PI / 2), seconds: 1 });
      await sleep(1100);
    } else if (kind === 1) {
      const d = a + side * Math.PI / 4;
      await this.aim({ x: me.x + Math.sin(d) * 6, y: me.y, z: me.z + Math.cos(d) * 6 });
      await this.castSlot(2); // Veil of Shadow
      await sleep(900);
    } else {
      await this.drive({ action: "hold", buttons: "CliffJump", seconds: 1.6 });
      await this.drive({ action: "walk", dx: Math.sin(a + side * Math.PI / 3), dz: Math.cos(a + side * Math.PI / 3), seconds: 1.5 });
      await sleep(1700);
    }
    await this.stop();
  }

  /**
   * A routable point 1.5 or 2.5 m around `target`, the side facing the bot first; null if none, or
   * {interrupted} when danger() fires between queries (up to 40 of them, with the bot standing still).
   */
  async approachRoute(me, target, deadline = Infinity) {
    const facing = Math.atan2(me.x - target.x, me.z - target.z);
    // Nodes and units: 1.5-2.5 m. Large structures (the heart's footprint is 7.5 m): further out.
    for (const radius of [1.5, 2.5, 4, 6, 8.5]) {
      for (const k of [0, 1, -1, 2, -2, 3, -3, 4]) {
        if (Date.now() > deadline) return null;
        const why = await this.danger();
        if (why) return { interrupted: why };
        const a = facing + (k * Math.PI) / 4;
        const p = { x: target.x + Math.sin(a) * radius, y: target.y, z: target.z + Math.cos(a) * radius };
        const route = await this.path({ x: me.x, y: me.y, z: me.z }, p, PATH_EXPANSIONS, deadline);
        if (route) return { route, radius, point: p };
      }
    }
    return null;
  }

  /**
   * Ring search for when the pathfinder has no route (levels joined only by drops or ramps the nav
   * graph does not link, live 2026-09-24 on the crypt-gate plateau). From an origin, walk ~4 s in
   * each of 16 directions (target bearing first, CliffJump held so ledges are jumped), succeeding
   * as soon as the bot changes level or a route to the target opens; otherwise walk back. If a ring
   * fails, move to the farthest point it reached and ring again.
   */
  async explore(p, home, { rings = 3, seconds = 4, deadline = Date.now() + 60000 } = {}) {
    let origin = home;
    const origins = [home];
    for (let ring = 0; ring < rings; ring++) {
      const bearing = Math.atan2(p.x - origin.x, p.z - origin.z);
      const order = [0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6, 7, -7, 8];
      let farthest = null;
      for (const k of order) {
        if (Date.now() > deadline) return false;
        // Walks in the open with nothing else watching (the review found it blind to the sun): the
        // sun or damage ends it, and goTo reports why from the same (cached) danger() read.
        if (await this.danger()) { await this.stop(); return false; }
        const a = bearing + (k * Math.PI) / 8;
        await this.drive({ action: "hold", buttons: "CliffJump", seconds: seconds + 0.1 });
        await this.drive({ action: "walk", dx: Math.sin(a), dz: Math.cos(a), seconds });
        await sleep(seconds * 1000 + 250);
        const now = await this.self();
        const d = Math.hypot(now.x - origin.x, now.z - origin.z);
        if (process.env.BOT_DEBUG) this.log(`ring ${ring} ${Math.round((a * 180) / Math.PI)}deg -> (${now.x.toFixed(1)},${now.y.toFixed(1)},${now.z.toFixed(1)}) d=${d.toFixed(1)}`);
        if (Math.abs(now.y - home.y) > 1.5) return true;
        if (d > 2 && (await this.path({ x: now.x, y: now.y, z: now.z }, { x: p.x, y: p.y ?? now.y, z: p.z }, PATH_EXPANSIONS, deadline))) return true;
        const fresh = origins.every((o) => Math.hypot(now.x - o.x, now.z - o.z) > 4);
        if (fresh && (!farthest || d > farthest.d)) farthest = { ...now, d };
        if (d > 1) await this.moveTo(origin, { stop: 0.7, timeoutMs: seconds * 1000 + 2000 });
      }
      if (!farthest) return false;
      await this.moveTo(farthest, { stop: 0.7, timeoutMs: 8000 });
      origin = await this.self();
      origins.push(origin);
    }
    return false;
  }

  // ---- server-side actions (what a key press maps to) -----------------------------------------
  // An emptied bar slot (the bar changes with weapons and forms) drops the spell cache rather than
  // throwing out of a fight ("bot_cast: slot 5 is empty" ended a fight against four bandits, live).
  castSlot(slot = 0) {
    return call(SERVER, "bot_cast", { user: this.name, slot }).catch((e) => {
      if (!/is empty/.test(e.message)) throw e;
      this._spells = null;
      return { error: e.message };
    });
  }

  /**
   * Veil of Shadow, the Space evade: a quick dash toward p when its cooldown allows. Players use it to
   * move faster, to get away and to reach shadows (the user's tip). One cooldown per bot, shared by
   * fights, flights and travel (this.lastDash).
   */
  async dash(p, { cooldownMs = DASH_COOLDOWN_MS } = {}) {
    if (Date.now() - (this.lastDash ?? 0) < cooldownMs) return false;
    this.lastDash = Date.now();
    await this.aim(p);
    await this.castSlot(2);
    await sleep(350);
    return true;
  }
  /**
   * Spell slots on the action bar (live: after Journal_WieldTheSword, AB_Blood_Shadowbolt sat in
   * slot 5). Slots 0 (weapon) and 2 (Veil of Shadow) are handled by the skills themselves; the bar
   * is slots 1-8. Cached for a minute.
   */
  async spellSlots() {
    if (this._spells && Date.now() - this._spellsAt < 60000) return this._spells;
    const r = await call(SERVER, "bot_cast", { user: this.name, action: "slots" }).catch(() => null);
    this._spells = (r?.slots ?? []).filter((s) => s.slot >= 1 && s.slot <= 8 && s.slot !== 2 && s.group && /^AB_(Blood|Chaos|Frost|Illusion|Storm|Unholy)_/.test(s.name ?? ""))
      .map((s) => ({ slot: s.slot, name: s.name }));
    this._spellsAt = Date.now();
    return this._spells;
  }
  castCounter() { return call(SERVER, "bot_cast", { user: this.name, action: "status" }).then((r) => r.castStartedCounter); }
  inventory(filter) { return call(SERVER, "bot_inventory", filter ? { user: this.name, name: filter } : { user: this.name }); }
  useSlot(slot) { return call(SERVER, "bot_use", { user: this.name, slot }); }
  wake() { return call(SERVER, "wake", { user: this.name }); }
  vitals() { return call(SERVER, "bot_cast", { user: this.name, action: "vitals" }); }
  hover(entity) { return this.drive({ action: "hover", entity }); }
  castGroup(group) { return call(SERVER, "bot_cast", { user: this.name, group }); }
  respawn() { return call(SERVER, "respawn", { user: this.name }); }
  giveUp() { return call(SERVER, "downed", { user: this.name, action: "finish" }); }

  /** One attack with the ability on `slot`, waiting until the game really started it. */
  async attack(slot = 0, { timeoutMs = 2500 } = {}) {
    const before = await this.castCounter();
    const until = Date.now() + timeoutMs;
    while (Date.now() < until) {
      const r = await this.castSlot(slot);
      await sleep(120);
      if ((await this.castCounter()) > before) return true;
      if (!r.requested && r.why && !/pending/i.test(r.why)) return false;
    }
    return false;
  }
}

// ---- reachability ---------------------------------------------------------------------------------

// Where reachable() looks when the target's own tile does not route (a rock, tree or unit occupies
// it): 4 of goTo's ring points, [radius, 45-degree steps from the side facing the bot].
const REACH_RING = [[1.5, 0], [1.5, 1], [1.5, -1], [2.5, 0]];

/**
 * Can the bot walk to `target` (a bot_look row or {x,y,z}), asked BEFORE committing to it: one path
 * query to the target, then at most 4 ring points 1.5-2.5 m around it, all inside `budgetMs`.
 * Returns {ok, cost, reason}: ok true with cost = meters of route; ok false (reason "unreachable")
 * when every query came back without a route; ok null (reason "timeout") when the budget ran out
 * first, which says nothing either way. Live 2026-09-24/25 the fight and the gather discovered an
 * unreachable target by walking: 25-50 s fights with no swing, 60 s "could not reach" gathers.
 * The pathfinder plans within one level (bot.path), so a target below a drop reads unreachable here
 * although goTo could jump down to it.
 */
export async function reachable(bot, target, { budgetMs = 1500, from = null } = {}) {
  const deadline = Date.now() + budgetMs;
  const me = from ?? await bot.self();
  const start = { x: me.x, y: me.y, z: me.z };
  const to = { x: target.x, y: target.y ?? me.y, z: target.z };
  const direct = await bot.pathResult(start, to, PATH_EXPANSIONS, deadline);
  if (direct.route) return { ok: true, cost: Math.round(routeLength(start, direct.route) * 10) / 10, reason: "route" };
  let unknown = direct.status !== "Failed";
  const facing = Math.atan2(me.x - to.x, me.z - to.z);
  for (const [radius, k] of REACH_RING) {
    if (Date.now() > deadline) { unknown = true; break; }
    const a = facing + (k * Math.PI) / 4;
    const p = { x: to.x + Math.sin(a) * radius, y: to.y, z: to.z + Math.cos(a) * radius };
    const r = await bot.pathResult(start, p, PATH_EXPANSIONS, deadline);
    if (r.route) return { ok: true, cost: Math.round((routeLength(start, r.route) + radius) * 10) / 10, reason: "approach" };
    if (r.status !== "Failed") unknown = true;
  }
  return unknown ? { ok: null, cost: null, reason: "timeout" } : { ok: false, cost: null, reason: "unreachable" };
}

// ---- skills -------------------------------------------------------------------------------------

/**
 * Break the nearest thing whose prefab name contains `nameFilter` (urns, trees, rocks, crates...):
 * walk up, aim, and attack until it is gone from view or `maxSwings` is spent.
 */
// Gather names are resource families, not literal prefab fragments: "Tree" as a substring also hits
// fiber bushes (TM_PlantfiberBushyTree_01_Pickup, live) while real trees are TM_Spruce*/TM_*Pine*.
export const RESOURCE_FAMILIES = {
  Tree: /^TM_(Spruce|.*Pine|Birch|Oak|Cypress|Tree)(?!.*Pickup)/i,
  Rock: /^TM_Rock_.*Resource|^TM_Stone.*Resource/i,
  Fiber: /^TM_Plantfiber.*Pickup/i,
  Urn: /Urn/i,
};

export async function breakNearest(bot, nameFilter, { radius = 60, maxSwings = 25, skip = new Set() } = {}) {
  const family = RESOURCE_FAMILIES[nameFilter];
  const view = family ? await bot.look({ radius, limit: 150 }) : await bot.look({ radius, limit: 20, name: nameFilter });
  const matches = (t) => (family ? family.test(t.name ?? "") : true);
  const candidates = (view.nearby ?? []).filter((t) => matches(t) && !skip.has(t.entity)).slice(0, 3);
  if (!candidates.length) return { ok: false, reason: `no ${nameFilter} within ${radius} m` };
  // Path-check the nearest three before walking (a node on a ledge or behind a fence cost a 60 s
  // "could not reach" walk each); the first that routes, or whose check ran out of time, is taken.
  let target = null;
  const unreachable = [];
  for (const c of candidates) {
    const why = await bot.danger();
    if (why) return { ok: false, reason: interruptReason(why), interrupted: why };
    const reach = await reachable(bot, c).catch(() => ({ ok: null }));
    if (reach.ok !== false) { target = c; break; }
    skip.add(c.entity);
    unreachable.push(c.name);
  }
  if (!target) return { ok: false, reason: "unreachable", unreachable };
  return breakTarget(bot, target, { maxSwings, skip, still: family ? {} : { name: nameFilter } });
}

/** Walk to one resource node or breakable (a bot_look row) and swing until it is gone. */
export async function breakTarget(bot, target, { maxSwings = 25, skip = new Set(), still: stillFilter = {} } = {}) {
  // Progress, not the 60 s timeout, ends a walk that stopped gaining (circling a fence, running in place).
  const walk = await bot.goTo(target, { stop: 1.2, timeoutMs: 60000, progress: new Progress() });
  if (walk.interrupted) return { ok: false, reason: interruptReason(walk.interrupted), interrupted: walk.interrupted, target };
  if (!walk.arrived) { skip.add(target.entity); return { ok: false, reason: walk.stalled ? "stalled" : "unreachable", cell: walk.cell, target }; }
  await bot.aim(target);
  let lastHealth = null;
  for (let swing = 1; swing <= maxSwings; swing++) {
    await bot.attack(0);
    const still = await bot.look({ radius: 5, limit: 30, ...stillFilter });
    if (!still.nearby?.some((t) => t.entity === target.entity)) return { ok: true, target, swings: swing };
    // Work is not a trance: stop for a hostile at arm's length or for taking damage (live 2026-09-24 a
    // bot was downed mid-swing at a copper node), so the brain's fight/flee rules take over.
    const me = still.self ?? {};
    // 3.5 m = the brain's fight-back range, so an interrupt always leads to a fight or a flight.
    const hostile = (still.nearby ?? []).find((n) => n.kind === "unit" && HOSTILE_UNIT.test(n.name ?? "") && n.distance <= 3.5 && (n.health ?? 1) > 0);
    const hurt = lastHealth !== null && (me.health ?? lastHealth) < lastHealth - 1;
    lastHealth = me.health ?? lastHealth;
    if (hostile || hurt) { await bot.stop(); return { ok: false, reason: "interrupted:threat", interrupted: hostile ? `hostile ${hostile.name}` : "taking damage", target, swings: swing }; }
  }
  skip.add(target.entity);
  return { ok: false, reason: "still standing", target };
}

export const FEED_GROUP = -376210658; // AB_Feed_01_Initiate_AbilityGroup
