// Layer 4: skills. Each is an async function over one Bot that either succeeds or reports why not.
import fs from "node:fs";
import { sleep, FEED_GROUP, SERVER } from "./bot.mjs";
import { nearestShade, provenShade } from "./shade.mjs";
import { call } from "./bridge.mjs";
import { Progress } from "./reach.mjs";

const dist = (a, b) => Math.hypot(a.x - b.x, a.z - b.z);
const VEIL_SLOT = 2;            // Veil of Shadow, the dash every character has (live slots read)
const VEIL_COOLDOWN_MS = 8000;  // assumed; a refused cast costs nothing
const SPELL_RANGE = 18;
const REACT_RADIUS = 5;         // meters: melee wind-ups this close are answered

/**
 * Weapon knowledge, live-read from the weapons' prefabs (data/weapon-combos.json, from
 * internal research notes): every bone melee weapon is a 3-step combo that resets
 * after ~1.5 s idle, each step = cast + post-cast (+ a 0.55-0.7 s cooldown on step 3); a press up to
 * 0.3 s early is buffered. Hits are a 3 m / 120 degree cone (spear: a 4.1 m x 1.5 m box). Knockback
 * (0.5 m + 0.5 s stun) lands only on low-resistance units (skeletons, crossbows, deer, thugs), never on
 * ghouls, wolves or bears. Spear +25% vs beasts, reaper +25% vs undead; axe/mace +25% vs wood/stone.
 * The crossbow is a 1.0 s rooted draw, ~1.8 s per shot, 12 m. The steps agree with the live
 * calibration (sword 6/8 casts at 400 ms, mace at 600 ms).
 */
const COMBOS = JSON.parse(fs.readFileSync(new URL("./data/weapon-combos.json", import.meta.url), "utf8"));
const UNARMED = { type: "Unarmed", steps: [{ ms: 600 }], resetMs: 1500, ranged: false, reach: 1.8, standoff: 1.4 };
export const weaponType = (item = "") => /Item_Weapon_([A-Za-z]+)_/.exec(item ?? "")?.[1] ?? "Unarmed";
export function weaponProfile(item = "") {
  const type = weaponType(item);
  const c = COMBOS[type];
  if (!c) return UNARMED;
  const steps = c.combo.map((st) => ({ ms: Math.round((st.castTime + st.postCast + (st.cooldown ?? 0)) * 1000) + 50, knockback: st.knockback?.range ?? 0 }));
  if (c.ranged) return { type, steps, resetMs: (c.resetDelay ?? 2) * 1000, ranged: true, keep: [7, 11], reach: 12, standoff: 9 };
  const spear = type === "Spear";
  return { type, steps, resetMs: (c.resetDelay ?? 1.5) * 1000, ranged: false, reach: spear ? 3.6 : 2.6, standoff: spear ? 3.0 : 2.0, bonus: c.itemBonus };
}
const HYBRID_MELEE_BELOW = 4;   // hybrid: swap to melee when the enemy is this close
const HYBRID_RANGED_ABOVE = 8;  // ...and back to the crossbow when it is this far
const SWAP_MIN_MS = 1500;

/** A crossbow plus the best melee weapon for this target, both carried or worn; else null. */
export function hybridPair(targetName, bag) {
  const all = [...(bag?.occupied ?? []), ...(bag?.equipped ?? []).filter((e) => e.slot === "weapon")].filter((o) => /^Item_Weapon_/.test(o.item ?? ""));
  const bow = all.find((o) => weaponType(o.item) === "Crossbow");
  const melee = all.filter((o) => weaponType(o.item) !== "Crossbow");
  if (!bow || !melee.length) return null;
  const prefer = UNDEAD.test(targetName ?? "") ? ["Reaper", "Sword", "Slashers", "Axe", "Mace", "Spear"]
    : BEAST.test(targetName ?? "") ? ["Spear", "Sword", "Slashers", "Axe", "Mace", "Reaper"] : ["Sword", "Slashers", "Spear", "Axe", "Mace", "Reaper"];
  const pick = prefer.map((t) => melee.find((o) => weaponType(o.item) === t)).find(Boolean) ?? melee[0];
  return { ranged: bow.item, melee: pick.item };
}

// Knockback-able targets: they get pushed 0.5 m and stunned, so the bot steps back in after hits.
export const KNOCKBACKABLE = /Skeleton|Crossbow|Deer|Thug/i;
const UNDEAD = /Undead|Skeleton|Ghoul|Zombie|Bishop/i;
const FLEES = /Deer|Rabbit/i;
const BEAST = /Wolf|Bear|Deer|Moose|Boar|Spider|Rat|Forest_/i;

/**
 * The weapon a player would pick for this target among those carried: reaper vs undead, spear vs
 * beasts (both +25%), otherwise the sword. Returns {item, bagSlot} to equip, or null to keep the
 * current one.
 */
export function bestWeaponFor(targetName, bag) {
  const carried = (bag?.occupied ?? []).filter((o) => /^Item_Weapon_/.test(o.item ?? ""));
  const worn = (bag?.equipped ?? []).find((e) => e.slot === "weapon")?.item ?? "";
  // Prey that runs (deer) is shot: melee could not catch one, 8 feeds timed out or fled (live).
  const prefer = FLEES.test(targetName ?? "") ? ["Crossbow", "Spear", "Sword"]
    : UNDEAD.test(targetName ?? "") ? ["Reaper", "Sword", "Slashers"] : BEAST.test(targetName ?? "") ? ["Spear", "Sword", "Slashers"] : ["Sword", "Slashers", "Spear"];
  for (const type of prefer) {
    if (weaponType(worn) === type) return null;
    const c = carried.find((o) => weaponType(o.item) === type);
    if (c) return { item: c.item, bagSlot: c.slot };
  }
  return null;
}
const RANGED_UNIT = /Crossbow|Archer|Ranger|Hunter|Frostarrow|Rifle|Musket/i;
const SPELL_RETRY_MS = 4000;    // per slot; the real cooldowns differ, a refused cast costs nothing

// Map knowledge the bots learned on this server (2026-09-24), like a player remembering the way out
// of the starting crypt. The crypt is its own area and EVERY PLAYER HAS A COPY of it at a different
// x (live: x -1968, -2274, -2444), so the route is stored relative to that copy's TM_ExitGate_01,
// which the client can see. The gate is a teleporter to the world.
const CRYPT_ROUTE = {                                     // offsets from the exit gate (gx, gz)
  ledge: { dx: -2.4, y: 5, dz: -79.9 },                   // north edge of the wake-up room (y 5 over a y 0 floor)
  // Corridor from the lower floor to the gate, as the pathfinder solved it once (steps up to y 4 and
  // y 9 on the way). Later requests from mid-corridor failed, so it is replayed.
  corridor: [{ dx: -0.4, dz: -26.4 }, { dx: -0.4, dz: -23.4 }, { dx: -0.4, dz: -10.4 }, { dx: -0.4, dz: -8.4 }, { dx: -0.4, dz: -2.4 }],
  door: { dx: -0.2, dz: 0 },                              // stand here (within ~1 m) and Interact
};
// After the gate each copy lands on its own plateau (live: x 89, -545, -865); the ramp down is at the
// same offset from the landing point: 8.8 m west, 3.6 m south.
const RAMP_FROM_LANDING = { dx: -8.8, dz: -3.6 };

export function inCrypt(me) { return me.x < -1900 && me.z < -3000; }

async function cryptGate(bot) {
  const { call } = await import("./bridge.mjs");
  const me = await bot.self();
  const q = await call(bot.client, "query", { all: ["PrefabGUID", "Translation"], near: { x: me.x, y: me.y, z: me.z, radius: 150 }, limit: 2000 });
  const gates = q.entities.filter((e) => (e.prefab?.name ?? "").includes("ExitGate") && e.position);
  gates.sort((a, b) => dist(a.position, me) - dist(b.position, me));
  return gates[0]?.position ?? null;
}

/** Leave the starting crypt: jump off the ledge, walk the corridor to the gate, Interact, walk down the ramp. */
export async function leaveCrypt(bot) {
  let me = await bot.self();
  if (!inCrypt(me)) return { ok: true, note: "already outside" };
  const g = await cryptGate(bot);
  if (!g) return { ok: false, reason: "no exit gate in view" };
  const at = (o) => ({ x: g.x + o.dx, y: o.y ?? 0, z: g.z + o.dz });
  if (me.y > 3) {
    await bot.goTo(at(CRYPT_ROUTE.ledge), { stop: 0.8, attempts: 3, timeoutMs: 60000 });
    await bot.drive({ action: "hold", buttons: "CliffJump", seconds: 3 });
    await bot.drive({ action: "walk", dx: 0, dz: 1, seconds: 2.5 });
    await sleep(2800);
  }
  me = await bot.self();
  const first = at(CRYPT_ROUTE.corridor[0]);
  if (me.z < first.z - 1) {
    const r = await bot.goTo(first, { stop: 1, attempts: 3, timeoutMs: 60000 });
    if (!r.arrived) return { ok: false, reason: "could not reach the crypt corridor", r };
  }
  for (const o of CRYPT_ROUTE.corridor) {
    const w = at(o);
    me = await bot.self();
    if (me.z > w.z + 0.5) continue; // already past this point
    const r = await bot.moveTo(w, { stop: 0.7, timeoutMs: 8000 });
    if (!r.arrived) return { ok: false, reason: "stuck in the crypt corridor", r };
  }
  await bot.moveTo(at(CRYPT_ROUTE.door), { stop: 0.4, timeoutMs: 5000 });
  await bot.aim({ x: g.x, y: g.y, z: g.z + 1 });
  for (let i = 0; i < 3; i++) {
    await bot.drive({ action: "hold", buttons: "Interact", seconds: 1.5 });
    await sleep(2500);
    me = await bot.self();
    if (!inCrypt(me)) break;
  }
  if (inCrypt(me)) return { ok: false, reason: "gate did not open" };
  await bot.stop();
  await sleep(1500);
  const landing = await bot.self();
  await bot.moveTo({ x: landing.x + RAMP_FROM_LANDING.dx, z: landing.z + RAMP_FROM_LANDING.dz }, { stop: 0.8, timeoutMs: 8000 });
  await bot.drive({ action: "hold", buttons: "CliffJump", seconds: 3 });
  await bot.drive({ action: "walk", dx: -0.7, dz: -0.2, seconds: 2.5 });
  await sleep(2800);
  me = await bot.self();
  return { ok: me.y < 9, position: me };
}

/**
 * Downed (health 0, not yet dead) or dead: do what the death screen offers a player: give up the
 * downed wait (GiveUpReviveEvent), respawn (CharacterRespawnEvent), then leave the crypt again.
 */
export async function recover(bot) {
  let v = await bot.vitals();
  const downed = !v.dead && (v.health ?? 1) <= 0;
  if (!v.dead && !downed) return { ok: true, note: "alive" };
  if (downed) {
    await bot.stop();
    await bot.giveUp();
    for (let i = 0; i < 20 && !v.dead; i++) { await sleep(500); v = await bot.vitals(); }
  }
  await bot.respawn();
  await sleep(6000);
  return { ok: true, respawned: true, ...(await leaveCrypt(bot)) };
}

/**
 * Eat a carried blood consumable (killed rats drop Item_Consumable_Eat_Rat): the safe way to feed.
 * Live 2026-09-24 one rat raised blood 7.6 -> 27.4 within a second through bot_use.
 */
// What carried food restores (item data, internal research notes): rats +20 (live),
// hearts looted from deer/wolves/bandits +20 to +100.
export const FOOD_BLOOD = { Item_Consumable_Eat_Rat: 20, Item_Consumable_Heart_T02_Tainted: 20, Item_Consumable_Heart_T03_Unsullied: 50,
  Item_Consumable_Heart_T04_Pristine: 35, Item_Consumable_Heart_T04_Exquisite: 100 };
export const isFood = (item = "") => /Consumable_(Eat_|Heart_)/.test(item);

export async function eatCarried(bot, { skip = new Set() } = {}) {
  const bag = await bot.inventory();
  const deficit = 100 - ((await bot.vitals().catch(() => null))?.blood ?? 0);
  // The smallest item that covers the deficit, else the biggest: no waste of an Exquisite heart on 20.
  const foods = (bag.occupied ?? []).filter((o) => isFood(o.item) && !skip.has(o.item))
    .map((o) => ({ ...o, gain: FOOD_BLOOD[o.item] ?? 20 })).sort((a, b) => a.gain - b.gain);
  const food = foods.find((f) => f.gain >= deficit) ?? foods[foods.length - 1];
  if (!food) return { ok: false, reason: "nothing to eat" };
  const before = await bot.vitals();
  let after = before;
  // Live 2026-09-25 a rat eaten right after it was caught did nothing, the same rat 20 s later
  // worked: one retry after a pause.
  for (let attempt = 0; attempt < 2 && !(after.blood > before.blood + 0.5); attempt++) {
    if (attempt) await sleep(2000);
    await bot.useSlot(food.slot);
    for (let i = 0; i < 6; i++) {
      await sleep(500);
      after = await bot.vitals();
      if (after.blood > before.blood + 0.5) break;
    }
  }
  const ok = after.blood > before.blood + 0.5;
  return { ok, ate: ok ? food.item : undefined, item: ok ? undefined : food.item, bloodBefore: before.blood, bloodAfter: after.blood };
}

/**
 * Catch a critter the way a player does: get within arm's reach (rats move, so re-approach), hover it
 * and hold Interact. Live 2026-09-24: this put an Item_Consumable_Eat_Rat in the bag (swinging at rats
 * missed for 30 s at a time); eating that gives ~20 blood.
 */
export async function catchCritter(bot, target, { tries = 4 } = {}) {
  let t = target;
  for (let i = 0; i < tries; i++) {
    const walk = await bot.goTo(t, { stop: 1.0, timeoutMs: 30000 });
    if (walk.interrupted) return { ok: false, interrupted: walk.interrupted };
    // A rat runs: keep it in sight out to 25 m and chase again (10 m lost most of them).
    t = (await bot.look({ radius: 25, limit: 40, kind: "unit" })).nearby?.find((n) => n.entity === target.entity);
    if (!t) return { ok: false, reason: "critter gone" };
    if (t.distance < 2) break;
  }
  if (!t || t.distance >= 2) return { ok: false, reason: "could not get close" };
  const before = await bot.inventory();
  await bot.stop();
  await bot.aim(t);
  await bot.hover(t.entity);
  await bot.drive({ action: "hold", buttons: "Interact", seconds: 2.5 });
  await sleep(3500);
  await bot.drive({ action: "unhover" });
  const after = await bot.inventory();
  const caught = (after.items ?? []).some((i) => /Consumable_Eat_/.test(i.item ?? "") && i.amount > (before.items.find((b) => b.guid === i.guid)?.amount ?? 0));
  return { ok: caught, reason: caught ? undefined : "nothing caught" };
}

/**
 * Whether to hang on to a feed for this victim's blood (a player's call): when the pool is low, when
 * the bot carries no blood type yet (rats give none), or when the victim's blood is clearly better
 * quality and a type this bot wants (its persona's list; blood types give different bonuses:
 * Warrior/Brute for fighting, Rogue for crits and speed, Worker for yields, Creature for speed and
 * sun resistance). Otherwise the feed is only a finisher, so a good blood is not swapped for a worse.
 */
export function wantBlood(v, victim, prefs = []) {
  if (!v || !victim) return false;
  if ((v.blood ?? 100) < BITE_HOLD_BELOW) return true;
  const mine = String(v.bloodType ?? "BloodType_None"), theirs = String(victim.bloodType ?? "");
  if (/None/.test(mine) && theirs && !/None/.test(theirs)) return true;
  const wanted = prefs.some((p) => theirs.includes(p));
  return wanted && (victim.bloodQuality ?? 0) > (v.bloodQuality ?? 0) + 5;
}

/**
 * The same unit seen by two ops: bot_threats (server) and bot_look (client) name different entity
 * ids for one unit, so match by prefab name and position.
 */
export const sameUnit = (a, b) => !!a && !!b && a.name === b.name && Math.hypot((a.x ?? 0) - (b.x ?? 0), (a.z ?? 0) - (b.z ?? 0)) < 2;

export const HEALING = /Item_Consumable_(Salve|HealingPotion)/;
const POTION_BELOW = 0.4, POTION_EVERY_MS = 20000;
const BITE_HOLD_BELOW = 90; // in a fight, hang on for the blood below this; above it the feed is only a finisher
// Living units a vampire can feed on once wounded (AB_Feed_01_Initiate; bot-blood-sources.md). Not
// every enemy can be fed on: the server's bot_threats says so per unit (`feedable`: it carries
// BloodConsumeSource and is not a V Blood). This name list is only a cheap prefilter for planning.
// V Blood bosses are never fed on wounded: only once downed, and that is the boss feed (boss.mjs).
export const FEEDABLE = /^CHAR_(?!.*VBlood)(Forest_(Deer|Wolf|Moose|Bear\w*)|Bandit_(Scout|Rascal|Worker_\w+|Thug|Prisoner_\w+|Mugger|Hunter)|Farmlands_(Hostile)?Villager_\w+|Vermin_(GiantRat|WickedRat_Rare))/;
const STALL_MS = 25000;     // backstop: no swing at all in this long is "unreachable" (Progress usually says so first)
const CHASE_LEG_MS = 6000;  // one routed chase leg; the loop re-reads the target between legs
const NO_ROUTE_LEGS = 2;    // chase legs in a row with no route to the target or around it: unreachable
export const woundedAt = (name = "") => (/Bear/.test(name) ? 0.23 : 0.28); // feed unlocks at 30% (bears 25%), a margin below

/**
 * Feed on a living unit the way a player does: fight it down to wounded, stop attacking, hover it and
 * tap Interact (AB_Feed_01_Initiate), then send NO attack for ~5.5 s: during the feed the primary
 * attack becomes an execute bite that kills with no blood. Blood goes to ~100 with the victim's type.
 */
export async function feedOnWounded(bot, target) {
  const before = await bot.vitals();
  // The weakest melee weapon carried: a strong hit jumps past the ~30% window where the feed is
  // possible (live: a copper crossbow killed a deer outright, "no blood: killed it").
  const bag = await bot.inventory().catch(() => null);
  const tierOf = (i) => Number(/_T(\d+)/.exec(i ?? "")?.[1] ?? 99);
  const weak = [...(bag?.occupied ?? [])].filter((o) => /^Item_Weapon_/.test(o.item ?? "") && !/Crossbow|Longbow|Pistols/.test(o.item))
    .sort((a, b) => tierOf(a.item) - tierOf(b.item))[0];
  const worn = (bag?.equipped ?? []).find((e) => e.slot === "weapon")?.item;
  if (weak && tierOf(weak.item) < tierOf(worn)) { await call(SERVER, "equip", { user: bot.name, slot: weak.slot }).catch(() => null); await sleep(300); }
  const r = await fight(bot, target, { fleeBelow: 0.3, timeoutMs: 40000, chooseWeapon: false, swap: false, bite: "hold" });
  const after = await bot.vitals().catch(() => before);
  const ok = after.blood > before.blood + 5;
  // The fight's typed failure travels with the result, so the brain blacklists an unreachable victim.
  const typed = ok ? {} : { ...(r.reason === "unreachable" || r.reason === "stalled" ? { [r.reason]: true } : {}), ...(r.interrupted ? { interrupted: r.interrupted } : {}) };
  return { ok, reason: ok ? undefined : `no blood: ${r.reason ?? (r.bitten ? "feed did not take" : r.killed ? "killed it" : "?")}`,
    fed: target.name, bloodBefore: before.blood, bloodAfter: after.blood, stats: r.stats, ...typed };
}

/**
 * Grab a wounded unit with the feed. Feeding is also a finisher (the user's tip): hanging on for
 * the ~5 s feed drains its blood into the bot; aborting with an attack kills it at once with no
 * blood. Returns whether the feed started (the bot's cast counter moved).
 */
async function bite(bot, t, { hold }) {
  const c0 = await bot.castCounter().catch(() => 0);
  if (t.distance > 1.8) await bot.goTo(t, { stop: 1.5, attempts: 1, timeoutMs: 3000 });
  await bot.stop();
  await bot.aim(t);
  await bot.hover(t.entity);
  await bot.drive({ action: "hold", buttons: "Interact", seconds: 0.4 });
  const started = (await bot.castCounter().catch(() => c0)) > c0;
  if (started) {
    if (hold) await sleep(5800); // the feed runs ~5 s; any attack now would end it without blood
    else { await sleep(250); await bot.castSlot(0); await sleep(600); } // abort: the instant kill
  }
  await bot.drive({ action: "unhover" });
  return started;
}

/** Feed on a weakened creature (critters always qualify; others below ~25% health). */
export async function feedOn(bot, target, { waitMs = 6000 } = {}) {
  const before = await bot.vitals();
  const walk = await bot.goTo(target, { stop: 1.4, timeoutMs: 60000 });
  if (!walk.arrived) return { ok: false, reason: "could not reach", walk };
  const fresh = (await bot.look({ radius: 6, limit: 10 })).nearby?.find((n) => n.entity === target.entity) ?? target;
  await bot.aim(fresh);
  await bot.hover(target.entity);
  let started = false;
  for (let i = 0; i < 8 && !started; i++) {
    await bot.castGroup(FEED_GROUP);
    await sleep(150);
    started = (await bot.castCounter()) > before.castStartedCounter;
  }
  const until = Date.now() + waitMs;
  let after = before;
  while (Date.now() < until) {
    await sleep(500);
    after = await bot.vitals();
    if (after.blood > before.blood + 0.5) break;
  }
  await bot.drive({ action: "unhover" });
  return { ok: after.blood > before.blood + 0.5, started, bloodBefore: before.blood, bloodAfter: after.blood };
}

/**
 * Fight one unit with the basic attack. Re-targets the moving enemy every ~0.3 s (a committed
 * straight walk let a skeleton kite out of reach while crossbows shot the bot, live 2026-09-24),
 * swings only within reach, paced at 600 ms (faster requests are discarded mid-combo; at 600 ms
 * every request became a swing), and retreats below `fleeBelow` of max health.
 */
export async function fight(bot, target, opts = {}) {
  // In a fight the bot expects to be hit: only the sun (not damage) interrupts its approach.
  bot.fighting = true;
  const stats = { dodges: 0, counters: 0, windups: 0, t0: Date.now() };
  const before = await bot.vitals().catch(() => null);
  try {
    const r = await fightOnce(bot, target, { ...opts, stats });
    const after = await bot.vitals().catch(() => null);
    r.stats = {
      enemy: target.name, level: target.level ?? null, weapon: stats.weapon, seconds: Math.round((Date.now() - stats.t0) / 100) / 10,
      hpLost: before && after ? Math.round((before.health - after.health) * 10) / 10 : null,
      dodges: stats.dodges, counters: stats.counters, windups: stats.windups, bite: stats.bite ?? null, minFrac: stats.minFrac, swaps: stats.swaps, shadeSteps: stats.shadeSteps, blood: stats.blood, shots: stats.shots, hits: stats.hits, potions: stats.potions,
    };
    return r;
  } finally { bot.fighting = false; }
}

async function fightOnce(bot, target, { fleeBelow = 0.35, timeoutMs = 45000, reach: reachArg, stats = {}, chooseWeapon = false, stopAtFraction = 0, bite: biteMode = "auto", leash = null, swap = chooseWeapon } = {}) {
  const until = Date.now() + timeoutMs;
  let bag = await bot.inventory().catch(() => null);
  if (chooseWeapon) {
    const better = bestWeaponFor(target.name, bag);
    if (better) { await call(SERVER, "equip", { user: bot.name, slot: better.bagSlot }).catch(() => null); await sleep(300); bag = await bot.inventory().catch(() => bag); }
  }
  // The equipped weapon decides combo timing, reach and whether to fight at range.
  const weapon = (bag?.equipped ?? []).find((e) => e.slot === "weapon")?.item ?? "Unarmed";
  let profile = weaponProfile(weapon);
  stats.weapon = profile.type;
  let reach = reachArg ?? profile.reach;
  let step = 0; // index into the combo of the NEXT swing
  // Hybrid: carrying a crossbow and a melee weapon, a player swaps to fit the range: opens with
  // bolts, swaps to melee when the enemy closes, and goes back out to range (a Veil away, or when it
  // backs off). With only a crossbow the ranged kiting below does the whole fight.
  let hybrid = swap ? hybridPair(target.name, bag) : null;
  let lastSwap = 0;
  const wield = async (item) => {
    const b = await bot.inventory().catch(() => null);
    const o = (b?.occupied ?? []).find((x) => x.item === item);
    lastSwap = Date.now();
    // Only trust the swap once the server confirms it; a failed swap ends hybrid mode for this fight
    // (a crossbow profile with a sword in hand would "shoot" from 10 m).
    const r = o ? await call(SERVER, "equip", { user: bot.name, slot: o.slot }).catch(() => null) : null;
    if (!r) { hybrid = null; return false; } // the equip op throws when refused
    await sleep(250);
    profile = weaponProfile(item);
    reach = reachArg ?? profile.reach;
    step = 0;
    lastSwap = Date.now();
    stats.swaps = (stats.swaps ?? 0) + 1;
    return true;
  };
  if (hybrid) {
    stats.weapon = `${weaponType(hybrid.ranged)}+${weaponType(hybrid.melee)}`;
    const want = target.distance > HYBRID_MELEE_BELOW + 2 ? hybrid.ranged : hybrid.melee;
    if (weaponType(want) !== profile.type) await wield(want);
  }
  let swings = 0, lastSwing = 0, lastSeen = target, lastVeil = 0, lastBite = 0, bitten = false;
  const startedAt = Date.now();
  let vel = { x: 0, z: 0 }, seenAt = Date.now(); // the target's ground velocity, for leading shots
  let lastPotion = 0, sunAt = 0;
  const lastSpell = {};
  const reacted = new Set(); // "entity:castCounter" already answered
  // The chase is measured against walking speed (6 s windows, 2 short ones = stalled) toward where the
  // target stood when the window opened, so a deer running off still counts while the bot runs at it.
  // Each chase step samples it before moving (the first opens the window, so two routed 6 s legs
  // without gain end the fight after ~12 s); a swing or a shot resets it.
  const chase = new Progress({ near: Math.max(reach, 3) });
  const chaseStalled = async (me, t) => {
    const p = chase.update({ position: me, goal: t });
    if (!p?.stalled) return null;
    await bot.stop();
    return { ok: false, reason: "stalled", cell: p.cell, gained: p.gained, swings };
  };
  let noRouteLegs = 0;
  while (Date.now() < until) {
    // Units only: in the woods 200 nearest objects were all trees and rocks, and a deer 40 m off was
    // "lost" before the first swing (5 of 8 deer feeds, live 2026-09-25). Shade reads its own look.
    const view = await bot.look({ radius: 90, limit: 80, kind: "unit" });
    const t = view.nearby?.find((n) => n.entity === target.entity);
    const me = view.self;
    if (!t) {
      await bot.stop();
      // Gone from view: a kill only if it vanished while close; far away it just walked off.
      return lastSeen.distance < 8 ? { ok: true, killed: true, bitten, swings } : { ok: false, reason: "lost target", swings };
    }
    // Not a single swing in 25 s: out of reach (on a ledge, behind a wall); four 45-50 s timeouts with
    // no swing against graveyard undead in one window, live 2026-09-25. The chase monitor (chaseStalled)
    // usually says so after ~12 s; this is the backstop for a bot that never chases (spells, dodges).
    if (swings === 0 && Date.now() - startedAt > STALL_MS) { await bot.stop(); return { ok: false, reason: "unreachable", swings }; }
    const now = Date.now(), dt = (now - seenAt) / 1000;
    if (dt > 0.05 && dt < 2) { // smoothed, so one jittery sample does not throw the lead
      vel = { x: vel.x * 0.4 + ((t.x - lastSeen.x) / dt) * 0.6, z: vel.z * 0.4 + ((t.z - lastSeen.z) / dt) * 0.6 };
    }
    seenAt = now;
    lastSeen = t;
    if (me.dead || (me.health ?? 1) <= 0) return { ok: false, reason: "downed", swings };
    // A V Blood boss is Immortal: at 0 health it is downed, not dead, and waits for the feed.
    if (typeof t.health === "number" && t.health <= 0) { await bot.stop(); return { ok: true, downed: true, target: t, swings }; }

    if (stopAtFraction > 0 && t.maxHealth && t.health / t.maxHealth <= stopAtFraction) {
      await bot.drive({ action: "stop" });
      return { ok: true, wounded: true, target: t, swings };
    }
    // A wounded living unit is grabbed with the feed: hang on for its blood when the bot wants blood
    // and nothing else is on it, otherwise abort the feed as a finisher. (Blood type/quality is not
    // read yet; when it is, an unwanted type also means "finish".)
    // Near the feed window, the server's health (bot_threats): damage lands there first and the client
    // view lags, so the view jumped from above 30% to dead and no bite was ever tried (0 bites in 67
    // feed fights, "no blood: killed it" on every prey type, live 2026-09-25).
    let frac = t.maxHealth ? t.health / t.maxHealth : 1;
    if (biteMode !== "never" && FEEDABLE.test(t.name ?? "") && frac < 0.65) {
      const rows = await call(SERVER, "bot_threats", { user: bot.name, radius: 15, limit: 10 }).catch(() => null);
      const srv = (rows?.threats ?? []).find((x) => sameUnit(x, t) || (x.name === t.name && Math.hypot(x.x - t.x, x.z - t.z) < 4));
      if (srv?.maxHealth) frac = Math.min(frac, srv.health / srv.maxHealth);
      stats.minFrac = Math.min(stats.minFrac ?? 1, frac);
    }
    if (biteMode !== "never" && FEEDABLE.test(t.name ?? "") && t.maxHealth && frac <= woundedAt(t.name) && frac > 0 && Date.now() - lastBite > 3000) {
      lastBite = Date.now();
      const others = await call(SERVER, "bot_threats", { user: bot.name, radius: 12, limit: 10 }).catch(() => null);
      const row = (others?.threats ?? []).find((x) => sameUnit(x, t));
      if (row && !row.feedable) biteMode = "never"; // the server says this unit cannot be fed on
      if (row?.feedable) {
      let hold = biteMode === "hold";
      if (biteMode === "auto") {
        const v = await bot.vitals().catch(() => null);
        const alone = !(others?.threats ?? []).some((x) => x.onMe && !sameUnit(x, t));
        hold = alone && wantBlood(v, row, bot.bloodPrefs);
        stats.blood = `${row.bloodType ?? "?"} q${Math.round(row.bloodQuality ?? 0)}`;
      }
      if (await bite(bot, t, { hold })) { bitten = true; stats.bite = hold ? "fed" : "finisher"; continue; }
      }
    }
    // Fighting by day: when sun exposure builds, step into the nearest shadow and let the enemy come
    // (a player fights with a tree between them and the sun). Checked every 1.5 s.
    if (bot.daytime?.sunDamage && Date.now() - sunAt > 1500) {
      sunAt = Date.now();
      const sv = (await bot.vitals().catch(() => null))?.sun;
      // Past 0.6 the fight is left to the brain's shade search (a 45 s fight in the sun ended in a burn).
      if (sv?.inSun && (sv.charge ?? 0) > 0.6) { await bot.stop(); return { ok: false, reason: "interrupted:sun", interrupted: "sun", swings }; }
      if (sv?.inSun && (sv.charge ?? 0) > 0.3) {
        const spot = provenShade(bot, view.self, bot.daytime, 12)[0]
          ?? nearestShade(await bot.look({ radius: 30, limit: 200 }).catch(() => ({ nearby: [], self: view.self })), bot.daytime, bot.shadeOffset ?? 0, 12);
        if (spot) {
          stats.shadeSteps = (stats.shadeSteps ?? 0) + 1;
          const was = bot.survival;
          bot.survival = true; // the step into shade must not be cut short by the sun it avoids
          try { await bot.goTo(spot, { stop: 0.6, attempts: 1, timeoutMs: 3000 }); } finally { bot.survival = was; }
          continue;
        }
      }
    }
    // Salves are not drunk mid-fight: taking damage breaks their heal (bot-survival-combat-tips.md);
    // the brain drinks one once clear, before resting.
    if (me.health / me.maxHealth < fleeBelow) {
      const away = { x: me.x + (me.x - t.x) * 4, y: me.y, z: me.z + (me.z - t.z) * 4 };
      await bot.moveTo(away, { stop: 1, timeoutMs: 4000 });
      return { ok: false, reason: "fled", health: me.health, swings };
    }
    // Stay inside a leash circle ({x, z, r}): dragged past it a boss resets and heals. Checked after
    // the salve and the retreat, so a hurt bot does not walk back beside the boss first.
    if (leash && Math.hypot(me.x - leash.x, me.z - leash.z) > leash.r) {
      await bot.moveTo({ x: leash.x, y: me.y, z: leash.z }, { stop: leash.r / 2, timeoutMs: 4000 });
      continue;
    }
    if (process.env.BOT_DEBUG) bot.log(`fight: dist ${t.distance} dy ${(t.y - me.y).toFixed(1)} hp ${me.health} target hp ${t.health}`);
    // React to wind-ups like a player: a unit aggroed on the bot that starts a melee cast close by
    // (live: withered skeleton swings wind up 0.7 s, rotting ghoul 1.5 s; bot_threats reads the
    // cast from AbilityBar_Shared) gets a sideways Veil of Shadow dash, or Blood Rite (a counter) while
    // the dash cools down.
    const near = await call(SERVER, "bot_threats", { user: bot.name, radius: REACT_RADIUS, limit: 6 }).catch(() => null);
    const windup = (near?.threats ?? []).find((x) => x.onMe && x.cast?.state === "casting" && !reacted.has(`${x.entity}:${x.cast.counter}`));
    if (windup) {
      reacted.add(`${windup.entity}:${windup.cast.counter}`);
      stats.windups = (stats.windups ?? 0) + 1;
      const counter = (await bot.spellSlots()).find((sp) => /BloodRite/.test(sp.name));
      if (Date.now() - lastVeil > VEIL_COOLDOWN_MS) {
        const a = Math.atan2(windup.x - me.x, windup.z - me.z) + (stats.windups % 2 ? 1 : -1) * Math.PI / 2;
        await bot.aim({ x: me.x + Math.sin(a) * 5, y: me.y, z: me.z + Math.cos(a) * 5 });
        await bot.castSlot(VEIL_SLOT);
        lastVeil = Date.now();
        bot.lastDash = lastVeil;
        stats.dodges = (stats.dodges ?? 0) + 1;
        await sleep(300);
        continue;
      }
      if (counter && Date.now() - (lastSpell[counter.slot] ?? 0) > SPELL_RETRY_MS) {
        await bot.aim(windup);
        await bot.castSlot(counter.slot);
        lastSpell[counter.slot] = Date.now();
        stats.counters = (stats.counters ?? 0) + 1;
        await sleep(300);
        continue;
      }
    }
    // A crossbow or archer shooting at the bot: Blood Rite blocks the bolts and its hits leech
    // (bot-survival-combat-tips.md change 4).
    const shooter = (near?.threats ?? []).find((x) => x.onMe && RANGED_UNIT.test(x.name ?? ""));
    const rite = shooter ? (await bot.spellSlots()).find((sp) => /BloodRite/.test(sp.name)) : null;
    if (rite && Date.now() - (lastSpell[rite.slot] ?? 0) > SPELL_RETRY_MS) {
      await bot.aim(shooter);
      await bot.castSlot(rite.slot);
      lastSpell[rite.slot] = Date.now();
      stats.counters = (stats.counters ?? 0) + 1;
      await sleep(300);
      continue;
    }
    // Spells whenever off cooldown, point-blank too: Shadow Bolt applies Leech, so the bot's hits on
    // the target heal it (players keep it up); a refused cast costs nothing.
    const spell = t.distance <= SPELL_RANGE && Math.abs(t.y - me.y) <= 1.5
      ? (await bot.spellSlots()).find((sp) => !/BloodRite/.test(sp.name) && Date.now() - (lastSpell[sp.slot] ?? 0) > SPELL_RETRY_MS) : null;
    if (spell) {
      await bot.aim(t);
      await bot.castSlot(spell.slot);
      lastSpell[spell.slot] = Date.now();
      await sleep(500);
      continue;
    }
    if (t.distance > 6 || Math.abs(t.y - me.y) > 1.5) {
      // Far, or on another level: route there in bounded legs, then steer the last meters. No ledge
      // drops or ring walks in a fight (goTo explore: false): they ran blind for up to 60 s and
      // swallowed the rest of the fight's timeout before the no-swing check could run (review
      // 2026-09-25). No route to it or around it on two legs running: unreachable.
      const stalled = await chaseStalled(me, t);
      if (stalled) return stalled;
      const leg = await bot.goTo(t, { stop: 3, attempts: 1, explore: false, deadline: Math.min(until, Date.now() + CHASE_LEG_MS) });
      noRouteLegs = leg.unreachable ? noRouteLegs + 1 : 0;
      if (noRouteLegs >= NO_ROUTE_LEGS) { await bot.stop(); return { ok: false, reason: "unreachable", swings }; }
      continue;
    }
    // Hybrid swaps (at most every SWAP_MIN_MS): bolts while it is out of reach, melee once it closes,
    // and back out to range when hurt with the dash ready (dash away, then shoot).
    if (hybrid && Date.now() - lastSwap > SWAP_MIN_MS) {
      if (profile.ranged && t.distance < HYBRID_MELEE_BELOW) { await wield(hybrid.melee); continue; }
      if (!profile.ranged && t.distance > HYBRID_RANGED_ABOVE) { await wield(hybrid.ranged); continue; }
      if (!profile.ranged && me.health / me.maxHealth < 0.5 && Date.now() - lastVeil > VEIL_COOLDOWN_MS) {
        await bot.aim({ x: me.x + (me.x - t.x) * 3, y: me.y, z: me.z + (me.z - t.z) * 3 });
        await bot.castSlot(VEIL_SLOT);
        lastVeil = Date.now();
        bot.lastDash = lastVeil;
        await sleep(350);
        await wield(hybrid.ranged);
        continue;
      }
    }
    // Close the gap with Veil of Shadow (the dash also empowers the next attack), on its cooldown.
    // Below half health the dash is kept for getting away (change 5).
    if (!profile.ranged && t.distance > 3 && t.distance < 7 && me.health / me.maxHealth >= 0.5 && Date.now() - lastVeil > VEIL_COOLDOWN_MS) {
      await bot.aim(t);
      await bot.castSlot(VEIL_SLOT);
      lastVeil = Date.now();
      await sleep(350);
      continue;
    }
    if (profile.ranged) {
      // Ranged (crossbow data: a 1.0 s ROOTED draw, bolt 28 m/s, 0.5 m radius, 12 m). Hold 7-11 m:
      // a chaser covers meters during the draw, so the draw starts only with room to finish it.
      // Too close: Veil away when ready, else strafe sideways (walking straight back never opened
      // distance on a chaser: 0 bolts in 30 trainer fights).
      const [near, far] = profile.keep;
      const closing = Math.max(0, (vel.x * (me.x - t.x) + vel.z * (me.z - t.z)) / Math.max(t.distance, 0.1)); // m/s toward the bot
      if (t.distance - closing * 1.2 < 3) {
        if (Date.now() - lastVeil > VEIL_COOLDOWN_MS) {
          await bot.aim({ x: me.x + (me.x - t.x) * 3, y: me.y, z: me.z + (me.z - t.z) * 3 });
          await bot.castSlot(VEIL_SLOT);
          lastVeil = Date.now();
          await sleep(350);
        } else {
          const side = (stats.strafe = (stats.strafe ?? 0) + 1) % 2 ? 1 : -1;
          const a = Math.atan2(me.x - t.x, me.z - t.z) + side * Math.PI / 3; // back and to the side
          await bot.drive({ action: "move", x: me.x + Math.sin(a) * 6, z: me.z + Math.cos(a) * 6, stop: 0.5, timeout: 0.9 });
          await sleep(500);
        }
        continue;
      }
      if (t.distance > far) {
        const stalled = await chaseStalled(me, t);
        if (stalled) return stalled;
        await bot.drive({ action: "move", x: t.x, z: t.z, stop: far - 2, timeout: 1 });
        await sleep(250);
        continue;
      }
      // Lead the shot: aim where the target will be when the bolt arrives, re-aiming through the draw.
      await bot.drive({ action: "stop" });
      const hp0 = t.health, drawMs = 1000;
      const lead = (left) => { const fly = left + t.distance / 28; return { x: t.x + vel.x * fly, y: t.y, z: t.z + vel.z * fly }; };
      await bot.aim(lead(drawMs / 1000));
      await bot.castSlot(0);
      const t0 = Date.now();
      for (const at of [350, 700]) { await sleep(Math.max(0, at - (Date.now() - t0))); await bot.aim(lead((drawMs - at) / 1000)); }
      await sleep(Math.max(0, drawMs + 250 + t.distance / 28 * 1000 - (Date.now() - t0)));
      stats.shots = (stats.shots ?? 0) + 1;
      const after = (await bot.look({ radius: 30, limit: 40, kind: "unit" }).catch(() => null))?.nearby?.find((n) => n.entity === t.entity);
      if (!after || after.health < hp0) stats.hits = (stats.hits ?? 0) + 1;
      lastSwing = Date.now();
      swings++;
      chase.reset();
      continue;
    } else if (t.distance > reach) {
      // Out of the cone: step in (also after a knockback pushed a skeleton back mid-combo).
      const stalled = await chaseStalled(me, t);
      if (stalled) return stalled;
      await bot.drive({ action: "move", x: t.x, z: t.z, stop: profile.standoff, timeout: 1 });
      await sleep(200);
      continue;
    }
    await bot.drive({ action: "stop" });
    await bot.aim(t);
    // Combo timing: the next press waits for the current step (cast + recovery; step 3 adds its
    // cooldown), and the combo restarts after the reset delay. Right after a Veil dash the empowered
    // hit is thrown at once (it must land within 2.2 s).
    if (Date.now() - lastSwing > profile.resetMs) step = 0;
    // Close to the feed window with blood wanted: single opening hits, each read back before the next
    // (combos carried deer from above 30% straight to dead: "no blood: killed it", live).
    const nearWindow = biteMode === "hold" && frac < 0.6;
    if (nearWindow && step !== 0) { step = 0; await sleep(profile.resetMs); continue; }
    const prev = profile.steps[(step + profile.steps.length - 1) % profile.steps.length];
    const empowered = Date.now() - lastVeil < 2000;
    const wait = step === 0 || empowered ? 0 : prev.ms - (Date.now() - lastSwing);
    if (wait > 0) await sleep(wait);
    await bot.castSlot(0);
    lastSwing = Date.now();
    step = (step + 1) % profile.steps.length;
    swings++;
    chase.reset();
  }
  await bot.stop();
  return { ok: false, reason: "timeout", swings };
}

/** Gather from the nearest resource whose name contains `name`; reports what the bag gained. */
export async function gather(bot, name, opts = {}) {
  const { breakNearest } = await import("./bot.mjs");
  let before = await bot.inventory();
  // The right tool, as a player swaps: axe +25% vs wood, mace +25% vs stone (weapon prefab data).
  const tool = { Tree: "Axe", Rock: "Mace" }[name];
  const worn = (before.equipped ?? []).find((e) => e.slot === "weapon")?.item ?? "";
  const carried = tool && weaponType(worn) !== tool ? (before.occupied ?? []).find((o) => weaponType(o.item) === tool) : null;
  if (carried) { await call(SERVER, "equip", { user: bot.name, slot: carried.slot }).catch(() => null); await sleep(300); before = await bot.inventory(); }
  const r = await breakNearest(bot, name, opts);
  const after = await bot.inventory();
  const gained = {};
  for (const it of after.items) {
    const prev = before.items.find((b) => b.guid === it.guid)?.amount ?? 0;
    if (it.amount > prev) gained[it.item ?? it.guid] = it.amount - prev;
  }
  return { ...r, gained };
}

const HOSTILE = /Skeleton|Ghoul|Bandit|Wolf|Bear|Spider|Zombie|Undead|Militia|Guard|Archer|Knight/i;

/** Nearest creature to feed on (critters always qualify) with no hostile within `safety` meters of it. */
export async function safePrey(bot, { name = "Critter", radius = 80, safety = 12, skip = new Set(), avoid = () => false } = {}) {
  const v = await bot.look({ radius, limit: 120, kind: "unit" });
  const hostiles = v.nearby.filter((n) => HOSTILE.test(n.name ?? ""));
  const y = v.self?.y ?? 0; // reachable: about the bot's height (not under the ground or up a ledge)
  return v.nearby.find((n) => (n.name ?? "").includes(name) && !skip.has(n.entity) && !avoid(n) && Math.abs((n.y ?? 0) - y) < 2.5
    && hostiles.every((h) => dist(h, n) > safety)) ?? null;
}

/** Nearest visible thing matching a filter, or null. */
export async function nearest(bot, { name, kind, radius = 60 } = {}) {
  const v = await bot.look({ radius, limit: 30, ...(name ? { name } : {}), ...(kind ? { kind } : {}) });
  return v.nearby?.[0] ?? null;
}

export { dist };
