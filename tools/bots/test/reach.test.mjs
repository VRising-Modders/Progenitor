// Unit tests for reachability bookkeeping: the Progress monitor, the unreachable-target blacklist,
// typed failures, reachable() over a stub pathfinder, and decide() honoring the blacklist.
// No bridge, game or network: run with `node --test "tools/bots/test/*.test.mjs"`.
import test from "node:test";
import assert from "node:assert/strict";
import {
  Progress, cellOf, routeLength, blacklist, blacklisted, blacklistKeys, pruneBlacklist, failureKind, interruptReason,
  BLACKLIST_MS, WALK_SPEED,
} from "../reach.mjs";
import { reachable } from "../bot.mjs";
import { decide } from "../brain.mjs";

// ---- Progress ------------------------------------------------------------------------------------

test("Progress: walking at speed toward the goal never stalls", () => {
  const p = new Progress();
  const goal = { x: 200, z: 0 };
  for (let t = 0; t <= 30000; t += 500) {
    const r = p.update({ position: { x: (WALK_SPEED * t) / 1000, z: 0 }, goal, now: t });
    assert.ok(!r?.stalled, `stalled at ${t} ms`);
  }
});

test("Progress: running in place stalls after two windows, with the cell", () => {
  const p = new Progress();
  const at = { x: 10, z: 20 };
  const results = [];
  for (let t = 0; t <= 13000; t += 500) {
    const r = p.update({ position: at, goal: { x: 40, z: 20 }, now: t });
    if (r) results.push(r);
  }
  assert.equal(results[0].ok, false);
  assert.ok(!results[0].stalled, "one short window is not a stall");
  assert.equal(results[1].stalled, true);
  assert.equal(results[1].cell, cellOf(at));
});

test("Progress: a good window clears the count; reset() forgets it", () => {
  const p = new Progress();
  const goal = { x: 100, z: 0 };
  p.update({ position: { x: 0, z: 0 }, goal, now: 0 });
  assert.equal(p.update({ position: { x: 0, z: 0 }, goal, now: 6000 }).ok, false); // stood still
  assert.equal(p.update({ position: { x: 20, z: 0 }, goal, now: 12000 }).ok, true); // walked 20 m
  assert.equal(p.failures, 0);
  p.update({ position: { x: 20, z: 0 }, goal, now: 18000 }); // short again: 1
  p.reset();
  assert.equal(p.failures, 0);
  assert.equal(p.update({ position: { x: 20, z: 0 }, goal, now: 19000 }), null); // opens a window
});

test("Progress: near the goal the gain needed shrinks to what is left", () => {
  const p = new Progress({ near: 3 });
  p.update({ position: { x: 0, z: 0 }, goal: { x: 5, z: 0 }, now: 0 });
  // 5 m away, needs 2 m (not the 10 m walking would give in 6 s): closing to 2.5 m is enough.
  assert.equal(p.update({ position: { x: 2.5, z: 0 }, goal: { x: 5, z: 0 }, now: 6000 }).ok, true);
  // Already inside `near`: fine without moving.
  p.update({ position: { x: 2.5, z: 0 }, goal: { x: 5, z: 0 }, now: 12000 });
  assert.equal(p.update({ position: { x: 2.5, z: 0 }, goal: { x: 5, z: 0 }, now: 18000 }).ok, true);
});

test("Progress: a target running off still counts while the bot runs at where it was", () => {
  const p = new Progress();
  // The deer runs away as fast as the bot: the distance between them never shrinks.
  p.update({ position: { x: 0, z: 0 }, goal: { x: 30, z: 0 }, now: 0 });
  const r = p.update({ position: { x: 25, z: 0 }, goal: { x: 55, z: 0 }, now: 6000 });
  assert.equal(r.ok, true);
});

test("Progress: remaining meters along a route; restart() keeps the failure count", () => {
  const p = new Progress();
  p.update({ position: { x: 0, z: 0 }, remaining: 40, now: 0 });
  assert.equal(p.update({ position: { x: 0, z: 0 }, remaining: 39, now: 6000 }).ok, false);
  p.restart(); // a new route
  p.update({ position: { x: 0, z: 0 }, remaining: 60, now: 7000 });
  assert.equal(p.update({ position: { x: 1, z: 0 }, remaining: 59, now: 13000 }).stalled, true);
});

test("routeLength sums ground distance from the start through the waypoints", () => {
  assert.equal(routeLength({ x: 0, z: 0 }, [{ x: 3, z: 4 }, { x: 3, z: 10 }]), 11);
  assert.equal(routeLength({ x: 0, z: 0 }, []), 0);
});

// ---- blacklist -----------------------------------------------------------------------------------

const skel = (over = {}) => ({ entity: "10:1", name: "CHAR_Undead_Skeleton", x: 100.5, z: -50.2, ...over });

test("blacklist: keys by entity and by name in a 3 m cell", () => {
  assert.deepEqual(blacklistKeys(skel()), ["e:10:1", `n:CHAR_Undead_Skeleton@${cellOf(skel())}`]);
  assert.deepEqual(blacklistKeys({ entity: "1:1" }), ["e:1:1"]);
});

test("blacklist: the same entity, and the same unit under another id (client vs server) within 3 m", () => {
  const bl = blacklist({}, skel(), 0);
  assert.ok(blacklisted(bl, skel(), 1000));
  assert.ok(blacklisted(bl, skel({ entity: "999:3", x: 102, z: -49 }), 1000), "other id, 1.8 m off");
  // Across a cell border (x 100.5 -> 98.9 crosses the 99 line) still matches by distance.
  assert.ok(blacklisted(bl, skel({ entity: "999:3", x: 98.9, z: -50.2 }), 1000));
});

test("blacklist: not another name at the spot, not the same name 4 m away, not after 5 minutes", () => {
  const bl = blacklist({}, skel(), 0);
  assert.ok(!blacklisted(bl, skel({ entity: "2:1", name: "CHAR_Undead_Ghoul" }), 1000));
  assert.ok(!blacklisted(bl, skel({ entity: "2:1", x: 104.5 }), 1000));
  assert.ok(!blacklisted(bl, skel(), BLACKLIST_MS + 1));
  pruneBlacklist(bl, BLACKLIST_MS + 1);
  assert.deepEqual(bl, {});
  assert.ok(!blacklisted(undefined, skel(), 0));
});

// ---- typed failures ------------------------------------------------------------------------------

test("failureKind: unreachable and stalled, interrupted, other", () => {
  assert.equal(failureKind({ ok: true }), null);
  assert.equal(failureKind({ ok: false, reason: "unreachable" }), "unreachable");
  assert.equal(failureKind({ ok: false, reason: "no blood: unreachable", unreachable: true }), "unreachable");
  assert.equal(failureKind({ ok: false, reason: "stalled", cell: "1,2" }), "stalled");
  assert.equal(failureKind({ ok: false, reason: "interrupted:sun", interrupted: "sun" }), "interrupted");
  assert.equal(failureKind({ ok: false, interrupted: "taking damage" }), "interrupted");
  assert.equal(failureKind({ ok: false, reason: "fled" }), "other");
  assert.equal(interruptReason("sun"), "interrupted:sun");
  assert.equal(interruptReason("down"), "interrupted:down");
  assert.equal(interruptReason("taking damage"), "interrupted:threat");
  assert.equal(interruptReason("hostile CHAR_Bandit_Thug"), "interrupted:threat");
});

// ---- reachable() over a stub pathfinder ----------------------------------------------------------

const stubBot = (answers) => {
  const asked = [];
  return {
    asked,
    self: async () => ({ x: 0, y: 0, z: 0 }),
    pathResult: async (from, to) => { asked.push(to); const a = answers(asked.length, to); return typeof a === "function" ? a() : a; },
  };
};

test("reachable: a direct route gives ok with its length", async () => {
  const bot = stubBot(() => ({ route: [{ x: 3, z: 4 }, { x: 3, z: 10 }], status: "Success" }));
  assert.deepEqual(await reachable(bot, { x: 3, z: 10 }), { ok: true, cost: 11, reason: "route" });
});

test("reachable: an occupied tile routes through a ring point", async () => {
  const bot = stubBot((n) => (n === 1 ? { route: null, status: "Failed" } : { route: [{ x: 0, z: 8.5 }], status: "Success" }));
  const r = await reachable(bot, { x: 0, z: 10 });
  assert.equal(r.ok, true);
  assert.equal(r.reason, "approach");
  assert.equal(bot.asked.length, 2);
});

test("reachable: every query failing is unreachable after at most 5 queries", async () => {
  const bot = stubBot(() => ({ route: null, status: "Failed" }));
  assert.deepEqual(await reachable(bot, { x: 20, z: 0 }), { ok: false, cost: null, reason: "unreachable" });
  assert.equal(bot.asked.length, 5);
});

test("reachable: a query cut off by the budget says nothing (ok null)", async () => {
  const bot = stubBot((n) => (n === 1 ? { route: null, status: "deadline" } : { route: null, status: "Failed" }));
  assert.equal((await reachable(bot, { x: 20, z: 0 })).ok, null);
});

// ---- decide() honors the blacklist --------------------------------------------------------------

const base = (over = {}) => ({
  now: 1_000_000,
  vitals: { health: 100, maxHealth: 100, dead: false, blood: 80, sun: { inSun: false, charge: 0, burning: false } },
  self: { x: 0, y: 0, z: 0 }, inCrypt: false, daytime: { isDay: false, sunDamage: false },
  hostiles: [], weakUnits: [], drops: [], journal: null, hasHeart: true, heartAttempts: 0, cooldowns: {}, failures: {}, gatherTurn: 0,
  persona: "warrior",
  ...over,
});

test("decide: a blacklisted weak unit is not hunted, even under a new entity id; the hunt carries candidates", () => {
  const rat = (entity, x) => ({ entity, name: "CHAR_Vermin_Rat", x, z: 0, distance: x, health: 10, level: 1 });
  const snap = base({ weakUnits: [rat("a", 10), rat("b", 20)] });
  const free = decide(snap);
  assert.equal(free.action, "hunt"); // the warrior's rotation starts with "hunt"
  assert.equal(free.target.entity, "a");
  assert.deepEqual(free.candidates.map((c) => c.entity), ["a", "b"]);
  const bl = blacklist({}, rat("a", 10), snap.now);
  const d = decide({ ...snap, failures: { blacklist: bl }, weakUnits: [rat("a2", 10.5), rat("b", 20)] });
  assert.equal(d.target.entity, "b");
});
