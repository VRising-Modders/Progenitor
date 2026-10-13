// Working by day the way a player does: the sun is an exposure budget, not a wall. Sun exposure
// (TakeDamageInSun.ChargeValue) rises 0 -> 1 over SunChargeUpTime while in sun and burns only at 1;
// in shade it falls back over SunCooldownTime (live 2026-09-25: 5 s and 2.5 s). So a bot crosses open
// ground in hops of up to ~20 m between shadows, pausing a second or two in each, and fights with
// its back in a shadow when the fight drags on in the sun.
//
// Shadows fall away from the sun: ProjectM.Sun.Rotation (bot_daytime sunRotation, Euler degrees). Live
// 2026-09-25: pitch stays 65, the yaw moves ~0.33 deg/s through the 6-minute day (75 -> 120 in ~2 min),
// so the shadow side turns during the day. How the yaw maps to the shadow side is not known, so the bot
// learns it: a spot that turns out sunny is followed by probes of the other sides of the same caster,
// and the angle (relative to the yaw) that proved shaded is kept (bot.shadeOffset). Before this a bot
// burned on three sunny tree/rock spots in a row (live).
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { sleep } from "./bot.mjs";

const WALK = 4.3;            // m/s, a little under the measured 4.5
const MARGIN_S = 1.2;        // seconds of exposure kept in hand
const COOL_TO = 0.15;        // in shade, go on once the charge is under this (2.5 s from full)
// Tall things that cast a shadow a vampire fits in (live bot_look names: TM_Pine_05_Stage1,
// TM_LargePineOld01_Large, TM_DeadSpooky_02_Stage1, TM_Tomb, TM_Sarcofag_01_Full, TM_Rock_02_Stage1_Resource,
// TM_VampireGravestone_Big). Small gravestones, urns, fences, felled logs and saplings do not count.
// Measured over 370 live hops (2026-09-25): spruces 88%, pines 76-100%, dead trees 89%, tombs 95%
// shaded; rocks 24% (too low to stand in their shadow), castle pillars and walls ~50%. Rocks and
// pillars are not shade.
const CASTER = /Tree|Pine|Spruce|Birch|Oak|Cypress|Willow|DeadSpooky|Cliff|Wall|Castle|Ruin|Statue|Tomb|Sarcofag|Crypt|Mausoleum|House|Building|Tower|Gravestone_Big/i;
// Castle walls are out too: thin, ~50% shaded (measured), and day hops to them came up SUNNY four times
// in one burning bag run (live 2026-09-25); a closed room's roof is the castle's shade (roomPlan).
const NOT_CASTER = /Bush|Grass|Flower|Fiber|Sapling|Plant|Mushroom|Drop|Critter|Urn|Fence|DryWood|Stage0|Pickup|Rock|Boulder|Pillar|Heart|BloodFountain|Castle_Wall/i;

// ---- the shade map: shade the bot has stood in -------------------------------------------------------
// Guessing where a shadow falls failed too often (SUNNY hops, 40 s shade searches ending in a burn,
// live 2026-09-25). The server's inSun at the bot's own position is authoritative, so every moment the
// bot stands in shade by day is a proven shade spot for that sun angle (yaw). Recorded every tick
// (brain observe), kept per bot on disk across rounds, and tried first by shade searches and day hops
// when the sun's yaw is within 20 degrees of when it was recorded (shadows turn with the sun).
const MAP_FILE = (name) => path.join(os.tmpdir(), "progenitor-bots", `shade-${name}.json`);
const YAW_MATCH = 20;
export function loadShadeMap(bot) {
  if (bot.shadeMap) return bot.shadeMap;
  try { bot.shadeMap = JSON.parse(fs.readFileSync(MAP_FILE(bot.name), "utf8")); } catch { bot.shadeMap = []; }
  return bot.shadeMap;
}
export function recordShade(bot, at, yaw) {
  if (typeof yaw !== "number" || !at) return;
  const map = loadShadeMap(bot);
  if (map.some((p) => Math.hypot(p.x - at.x, p.z - at.z) < 3 && Math.abs(yawDiff(p.yaw, yaw)) < 10)) return;
  map.push({ x: Math.round(at.x * 10) / 10, y: at.y, z: Math.round(at.z * 10) / 10, yaw: Math.round(yaw), caster: "proven shade" });
  if (map.length > 3000) map.splice(0, map.length - 3000);
  if ((bot._shadeSaves = (bot._shadeSaves ?? 0) + 1) % 10 === 0) {
    try { fs.mkdirSync(path.dirname(MAP_FILE(bot.name)), { recursive: true }); fs.writeFileSync(MAP_FILE(bot.name), JSON.stringify(map)); } catch { /* best effort */ }
  }
}
const yawDiff = (a, b) => ((a - b + 540) % 360) - 180;
/** Proven shade spots near `me` for the current sun yaw, nearest first. */
export function provenShade(bot, me, daytime, radius = 30) {
  const yaw = daytime?.sunRotation?.[1];
  if (typeof yaw !== "number" || !me) return [];
  return loadShadeMap(bot).filter((p) => Math.abs(yawDiff(p.yaw, yaw)) <= YAW_MATCH && Math.hypot(p.x - me.x, p.z - me.z) <= radius)
    .sort((a, b) => Math.hypot(a.x - me.x, a.z - me.z) - Math.hypot(b.x - me.x, b.z - me.z));
}

/** Horizontal direction shadows fall toward ({x, z} unit vector), or null without a sun read. */
export function shadowDir(daytime, offset = 0) {
  const yaw = daytime?.sunRotation?.[1];
  if (typeof yaw !== "number") return null;
  const a = ((yaw + (offset === true ? 180 : Number(offset) || 0)) * Math.PI) / 180;
  return { x: Math.sin(a), z: Math.cos(a) };
}

/**
 * Candidate shade points: just behind each shadow caster in the view (trees, rocks, walls, ruins),
 * on the side away from the sun. Without a direction, all four sides are candidates.
 */
export function shadeSpots(nearby = [], dir) {
  const spots = [];
  for (const n of nearby) {
    if (n.kind === "unit" || n.kind === "player" || n.kind === "drop") continue;
    const name = n.name ?? "";
    if (!CASTER.test(name) || NOT_CASTER.test(name)) continue;
    // A tree's shade is its canopy's shadow, a few meters out at a 65 degree sun; rocks and tombs are
    // low, their shadow hugs them.
    const off = /Pine|Spruce|Birch|Oak|Cypress|Willow|Tree|DeadSpooky/i.test(name) ? (/Large|Big/i.test(name) ? 4 : 3.2) : 2.2;
    const sides = dir ? [dir] : [{ x: 1, z: 0 }, { x: -1, z: 0 }, { x: 0, z: 1 }, { x: 0, z: -1 }];
    for (const d of sides) spots.push({ x: n.x + d.x * off, y: n.y, z: n.z + d.z * off, caster: name, cx: n.x, cz: n.z, off });
  }
  return spots;
}

/** Seconds of sun the bot can still take before it burns, less a margin. */
export function exposureLeft(sun) {
  const up = sun?.chargeUpTime ?? 5;
  return Math.max(0, (1 - (sun?.charge ?? 0)) * up - MARGIN_S);
}

const key = (p) => `${Math.round(p.x)},${Math.round(p.z)}`;

/**
 * One daylight hop toward `dest`: from shade, spend the exposure budget walking to the shade spot
 * that gains the most ground; in shade with charge left over, wait the second or two it takes to
 * cool. Returns {arrived} at the destination, {hopped, spot, shaded}, {cooled}, or {exposed} when no
 * shade lies ahead within reach (the bot walks half its budget toward the goal and hops from there).
 */
export async function dayHop(bot, dest, { stop = 2 } = {}) {
  const v = await bot.vitals();
  const sun = v.sun ?? {};
  const view = await bot.look({ radius: 30, limit: 150 });
  const me = view.self ?? (await bot.self());
  const dNow = Math.hypot(dest.x - me.x, dest.z - me.z);
  if (dNow <= stop + 0.5) return { arrived: true, position: me };
  bot.badShade ??= new Set();
  const dir = shadowDir(bot.daytime, bot.shadeOffset ?? 0);
  const spots = [...provenShade(bot, me, bot.daytime, 40), ...shadeSpots(view.nearby, dir)].filter((p) => !bot.badShade.has(key(p)));
  const reach = exposureLeft(sun) * WALK;

  // Burning, nearly, or with too little budget to reach anything ahead: the nearest shade in any
  // direction, now (the walk must not be stopped by the sun it runs from: survival mode).
  if (sun.burning || (sun.inSun && ((sun.charge ?? 0) > 0.8 || reach < 2))) {
    // A spot proven shaded a moment ago beats a guess.
    const proven = bot.lastShaded && Date.now() - bot.lastShaded.at < 60000 && Math.hypot(bot.lastShaded.x - me.x, bot.lastShaded.z - me.z) < 15 ? [bot.lastShaded] : [];
    const near = [...proven, ...spots.sort((a, b) => Math.hypot(a.x - me.x, a.z - me.z) - Math.hypot(b.x - me.x, b.z - me.z))][0];
    if (!near) return { exposed: true, burning: true };
    const was = bot.survival;
    bot.survival = true;
    try { return await reachSpot(bot, near, 4000); } finally { bot.survival = was; }
  }
  // In shade with exposure still high: a short cool-down (full reset takes cooldownTime, 2.5 s).
  if (!sun.inSun && (sun.charge ?? 0) > COOL_TO) {
    await sleep(Math.min(3000, (sun.charge ?? 0) * (sun.cooldownTime ?? 2.5) * 1000) + 150);
    return { cooled: true };
  }
  if (dNow <= reach) {
    const r = await bot.goTo(dest, { stop, attempts: 1, timeoutMs: (reach / WALK) * 1000 + 1500 });
    return r.arrived ? { arrived: true, position: r.position } : { hopped: true, spot: dest };
  }
  // The shade spot within reach that gains the most ground.
  const ahead = spots
    .map((p) => ({ p, from: Math.hypot(p.x - me.x, p.z - me.z), left: Math.hypot(dest.x - p.x, dest.z - p.z) }))
    .filter((c) => c.from <= reach && c.left < dNow - 2)
    .sort((a, b) => a.left - b.left)[0];
  if (ahead) return reachSpot(bot, ahead.p, (ahead.from / WALK) * 1000 + 1500);
  // No shade ahead within reach: walk half the budget toward the goal; the next hop finds cover.
  const a = Math.atan2(dest.x - me.x, dest.z - me.z);
  await bot.drive({ action: "walk", dx: Math.sin(a), dz: Math.cos(a), seconds: Math.max(0.5, reach / WALK / 2) });
  await sleep(Math.max(500, (reach / WALK / 2) * 1000));
  return { exposed: true };
}

/** Walk to a shade spot and check it with the server's inSun; a sunny one is remembered as bad. */
async function reachSpot(bot, spot, timeoutMs) {
  const prev = bot.dayWalking;
  bot.dayWalking = true; // danger() lets the charge run up to the budget on the way
  let walk;
  try {
    // Dash for a shadow more than a few meters off: less time in the sun (the user's tip).
    const here = await bot.self().catch(() => null);
    if (here && Math.hypot(spot.x - here.x, spot.z - here.z) > 4) await bot.dash(spot);
    walk = await bot.goTo(spot, { stop: 0.6, attempts: 1, timeoutMs });
  } finally { bot.dayWalking = prev; }
  await sleep(350); // the in-sun raycast is time-sliced
  const v = await bot.vitals().catch(() => null);
  const shaded = v ? !v.sun?.inSun : false;
  // Only a spot actually reached says anything about the shadow side (a walk cut short, stuck or
  // timed out would flip the side wrongly).
  if (!walk?.arrived) { bot.badShade.add(key(spot)); return { hopped: true, spot, shaded, reached: false }; } // unreachable: not again (12 hops to one tree, live)
  if (shaded) { bot.lastShaded = { ...spot, at: Date.now() }; return { hopped: true, spot, shaded }; }
  bot.badShade.add(key(spot));
  // A remembered shade spot that is sunny now (shadows turn with the sun): forget it, and drop it from
  // the map at this sun angle (a bot went back to the same sunny spot three times and burned, live).
  if (bot.lastShaded && Math.hypot(bot.lastShaded.x - spot.x, bot.lastShaded.z - spot.z) < 2) bot.lastShaded = null;
  const yawNow = bot.daytime?.sunRotation?.[1];
  if (typeof yawNow === "number") {
    const map = loadShadeMap(bot);
    for (let i = map.length - 1; i >= 0; i--) {
      if (Math.hypot(map[i].x - spot.x, map[i].z - spot.z) < 2.5 && Math.abs(yawDiff(map[i].yaw, yawNow)) <= YAW_MATCH) map.splice(i, 1);
    }
  }
  // Sunny: probe the other sides of the same caster (opposite first) while the exposure allows, and
  // keep the angle that proves shaded.
  if (spot.cx !== undefined && typeof bot.daytime?.sunRotation?.[1] === "number") {
    const base = bot.shadeOffset ?? 0;
    for (const rel of [180, 90, -90]) {
      const now = (await bot.vitals().catch(() => null))?.sun;
      if (!now || exposureLeft(now) < 1) break;
      const d = shadowDir(bot.daytime, base + rel);
      const p = { x: spot.cx + d.x * spot.off, y: spot.y, z: spot.cz + d.z * spot.off, caster: spot.caster, cx: spot.cx, cz: spot.cz, off: spot.off };
      const was = bot.survival;
      bot.survival = true;
      let w;
      try { w = await bot.goTo(p, { stop: 0.4, attempts: 1, timeoutMs: 3000 }); } finally { bot.survival = was; }
      await sleep(350);
      const inSun = (await bot.vitals().catch(() => null))?.sun?.inSun;
      if (w?.arrived && inSun === false) {
        bot.shadeOffset = (((base + rel) % 360) + 360) % 360;
        bot.lastShaded = { ...p, at: Date.now() };
        return { hopped: true, spot: p, shaded: true, learned: bot.shadeOffset };
      }
    }
  }
  return { hopped: true, spot, shaded };
}

/** Nearest shade spot within `radius` of the bot (for fighting from a shadow), or null. */
export function nearestShade(view, daytime, offset, radius = 10) {
  const me = view.self;
  if (!me) return null;
  return shadeSpots(view.nearby, shadowDir(daytime, offset ?? 0))
    .map((p) => ({ p, d: Math.hypot(p.x - me.x, p.z - me.z) }))
    .filter((c) => c.d <= radius)
    .sort((a, b) => a.d - b.d)[0]?.p ?? null;
}

// ---- shade anchors: one sun model and scored shade candidates ----------------------------------------
// Tactical position selection (Game AI Pro ch. 26; internal research notes
// section 4): candidates are generated and scored every daytime tick, before the bot needs them, and
// a shade search walks straight to the best one instead of probing guesses one by one (15-30 s
// searches against a 5 s exposure budget burned bots, live 2026-09-25).

/** How likely a shade candidate is to be shaded now (measured caster rates, shade.mjs header). */
export function casterRate(name = "") {
  if (/Tomb|Sarcofag|Crypt|Mausoleum/i.test(name)) return 0.95;
  if (/DeadSpooky/i.test(name)) return 0.89;
  if (/Spruce/i.test(name)) return 0.88;
  if (/Pine|Tree|Birch|Oak|Cypress|Willow/i.test(name)) return 0.8;
  return 0.5; // walls, ruins, statues: not measured
}

/** Seconds of sun from the current exposure to burning (the full charge-up from shade). */
export function timeToBurn(sun) {
  const up = sun?.chargeUpTime ?? 5;
  return sun?.inSun ? Math.max(0, (1 - (sun.charge ?? 0)) * up) : up;
}

const ANCHOR_SLACK_S = 3; // score falloff: an anchor 3 s away is worth 1/e of one at hand

/**
 * Shade anchors around `me`, best first: {x, y, z, p, walkS, score, caster}. Sources, with the
 * probability of shade: a closed castle room (0.99, the roof ignores the sun angle), proven spots at
 * this sun yaw (0.95), the mist brazier (0.8), caster sides on the learned shadow side (casterRate),
 * other sides (casterRate x 0.3). Score = p x exp(-walkS / 3 s). Spots marked bad are left out.
 */
export function shadeAnchors(bot, me, nearby, daytime, { room = null, brazier = null, limit = 5 } = {}) {
  if (!me) return [];
  const walkS = (p) => Math.hypot(p.x - me.x, p.z - me.z) / WALK;
  const cands = [];
  if (room) cands.push({ x: room.x, y: room.y ?? me.y, z: room.z, p: 0.99, caster: "the castle room" });
  for (const s of provenShade(bot, me, daytime, 40).slice(0, 10)) cands.push({ ...s, p: 0.95 });
  if (brazier) for (const [dx, dz] of [[1.2, 0], [-1.2, 0], [0, 1.2], [0, -1.2]]) cands.push({ x: brazier.x + dx, y: brazier.y, z: brazier.z + dz, p: 0.8, caster: "the mist brazier" });
  const dir = shadowDir(daytime, bot.shadeOffset ?? 0);
  const sunward = new Set(dir ? shadeSpots(nearby, dir).map((s) => key(s)) : []);
  for (const s of shadeSpots(nearby, null)) cands.push({ ...s, p: casterRate(s.caster) * (sunward.has(key(s)) ? 1 : 0.3) });
  if (dir) for (const s of shadeSpots(nearby, dir)) cands.push({ ...s, p: casterRate(s.caster) });
  const seen = new Set();
  return cands
    .filter((c) => !bot.badShade?.has(key(c)))
    .map((c) => ({ ...c, walkS: walkS(c), score: c.p * Math.exp(-walkS(c) / ANCHOR_SLACK_S) }))
    .sort((a, b) => b.score - a.score)
    .filter((c) => (seen.has(key(c)) ? false : seen.add(key(c))))
    .slice(0, limit);
}

/**
 * The sun budget in seconds: time to burning less the walk to the best anchor (negative: the bot
 * cannot reach shade before it burns). The one exposure model the brain's sun rules read.
 */
export function sunSlack(sun, anchors) {
  // The nearest likely anchor (p >= 0.75); guesses only when nothing likely is in view.
  const likely = (anchors ?? []).filter((a) => a.p >= 0.75);
  const best = (likely.length ? likely : anchors ?? []).reduce((m, a) => Math.min(m, a.walkS), Infinity);
  return timeToBurn(sun) - (Number.isFinite(best) ? best : 99);
}
