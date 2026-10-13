// V Blood boss hunting. Facts from the internal research notes (live-read 2026-09-24):
// bosses are Immortal and go Downed at 0 health for ~125 s; the feed is an interact ability on the
// downed boss (hover + hold Interact: 1.2 s cast, then an 8.5 s channel that heals the feeder 50%);
// a feed adds the boss to the user's UnlockedVBlood buffer and grants its tech. Bosses reset when
// dragged past their leash radius from where combat started. Damage scales with the gear-level
// difference, so a bot hunts a boss only at gear >= boss level - 3.
import { call } from "./bridge.mjs";
import { SERVER, sleep } from "./bot.mjs";
import { travel, HOSTILE } from "./progression.mjs";
import { fight, sameUnit } from "./skills.mjs";

// Early bosses in the order players take them: for the unlock needed next, not strictly by level
// (internal research notes): Alpha Wolf (journal, Wolf Form), Keely (Tannery:
// leather gates armor), Errol (storage), Rufus (Woodworking Bench), Grayson (whetstones), Goreswine.
// Spawner positions live-read; levels from the units. planBoss takes the first one the gear allows.
export const BOSSES = [
  { name: "CHAR_Forest_Wolf_VBlood", label: "Alpha Wolf", level: 16, home: { x: -1202, z: -1635 }, leash: 70 },
  { name: "CHAR_Bandit_Frostarrow_VBlood", label: "Keely", level: 20, home: { x: -1024, z: -1357 }, leash: 50 },
  { name: "CHAR_Bandit_StoneBreaker_VBlood", label: "Errol", level: 20, home: { x: -1544, z: -1475 }, leash: 50 },
  { name: "CHAR_Bandit_Foreman_VBlood", label: "Rufus", level: 20, home: { x: -1206, z: -1551 }, leash: 35 },
  { name: "CHAR_Bandit_Stalker_VBlood", label: "Grayson", level: 27, home: { x: -1683, z: -1402 }, leash: 50 },
  { name: "CHAR_Undead_BishopOfDeath_VBlood", label: "Goreswine", level: 27, home: { x: -1216, z: -1700 }, leash: 70 },
];
// Top up before a boss below this (80 kept the trainer roaming a preyless graveyard for minutes at
// 70-75 blood, live 2026-09-25; 65 still leaves the fight well fed).
export const BOSS_MIN_BLOOD = 65;
export const BOSS_GEAR_MARGIN = 3;     // hunt at gear >= level - 3 (damage within ~6% of even)
export const BOSS_FIGHT_SECONDS = 150; // night left needed beyond the trip
export const isBoss = (name = "") => /_VBlood/.test(name);
export const bossFor = (name = "") => BOSSES.find((b) => b.name === name);

/**
 * The next boss worth hunting, or null: the lowest-level boss not yet fed on whose level the bot's
 * gear supports, with full health and blood and enough night left for the trip plus the fight.
 * k: {gear, vbloods:Set, vitals, self, day}.
 */
export function planBoss(k) {
  const gear = k.gear ?? 0;
  const v = k.vitals ?? {};
  // Players never start a boss on a thin pool: full health and blood topped up (playbook A3).
  if ((v.maxHealth ? v.health / v.maxHealth : 0) < 0.95 || (v.blood ?? 0) < BOSS_MIN_BLOOD) return null;
  const boss = BOSSES.find((b) => !k.vbloods?.has(b.name) && gear >= b.level - BOSS_GEAR_MARGIN);
  if (!boss) return null;
  if (k.day) {
    if (k.day.sunOut) return null;
    const d = k.self ? Math.hypot(boss.home.x - k.self.x, boss.home.z - k.self.z) : 0;
    if (d / 4 + BOSS_FIGHT_SECONDS > (k.day.dawnIn ?? Infinity)) return null;
  }
  return { action: "boss", boss, why: `hunt ${boss.label} (level ${boss.level}) at gear ${gear}` };
}

/** The next boss the gear allows and the bot has not fed on (ignoring health, blood and time). */
export function bossFits(k) {
  const gear = k.gear ?? 0;
  return BOSSES.find((b) => !k.vbloods?.has(b.name) && gear >= b.level - BOSS_GEAR_MARGIN) ?? null;
}

/**
 * Bosses a bot at this gear level should stay away from: any V Blood unit it would hunt later.
 * Underleveled, even the wolf deals ~1.6x damage and takes ~0.6x (level-difference table).
 */
export function bossTooStrong(name, gear) {
  const b = bossFor(name);
  return isBoss(name) && (!b || (gear ?? 0) < b.level - BOSS_GEAR_MARGIN);
}

/** V Blood bosses the user has fed on (UnlockedVBlood on the progression entity), as prefab names. */
export async function unlockedVBlood(bot) {
  const players = await call(SERVER, "players", {});
  const p = (players.players ?? []).find((x) => x.name === bot.name);
  if (!p?.userEntity) return null;
  const mapper = await call(SERVER, "get", { entity: p.userEntity, component: "ProgressionMapper" });
  const hex = mapper.fields?.ProgressionEntity?.bytes;
  if (!hex || hex.length < 16) return null;
  const le = (h) => Buffer.from(h, "hex").readInt32LE(0);
  const r = await call(SERVER, "inspect", { entity: `${le(hex.slice(0, 8))}:${le(hex.slice(8, 16))}`, depth: 64, only: ["UnlockedVBlood"] });
  const guids = (r.components?.find((c) => c.component?.endsWith("UnlockedVBlood"))?.elements ?? []).map((e) => e.VBlood);
  const names = new Set();
  for (const g of guids) {
    const pf = await call(SERVER, "bot_cost", { prefab: g }).catch(() => null); // resolves a GUID to its prefab name
    names.add(pf?.prefab ?? String(g));
  }
  return names;
}

/**
 * Travel to the boss's home, find it, and fight it the way a player does, with the same fight skill
 * as any unit (weapon profile and combos, wind-up dodges, spells at range): first the adds near it
 * (camp bandits, the Alpha Wolf's pack) while the boss is not yet on the bot, then the boss, inside
 * its leash. Retreat at 40% health: a player leaves early, and a leash reset only heals the boss.
 * Downed (Immortal at 0 health), it is fed on.
 */
export async function bossFight(bot, boss, { timeoutMs = 300000 } = {}) {
  const trip = await travel(bot, { x: boss.home.x + 20, z: boss.home.z + 20 }, { stop: 6 });
  if (trip.interrupted) return { ok: false, interrupted: trip.interrupted };
  if (!trip.arrived) return { ok: false, reason: `could not reach ${boss.label}`, trip };
  const start = await bot.self();
  const leash = { x: start.x, z: start.z, r: Math.max(15, boss.leash - 10) };
  const until = Date.now() + timeoutMs;
  let b = null, adds = 0;
  while (Date.now() < until) {
    const view = await bot.look({ radius: 60, limit: 60, kind: "unit" });
    b = (view.nearby ?? []).find((n) => n.name === boss.name);
    if (!b) {
      await bot.goTo({ x: boss.home.x, z: boss.home.z }, { stop: 8, attempts: 1, timeoutMs: 15000 });
      const again = (await bot.look({ radius: 60, limit: 60, kind: "unit" })).nearby ?? [];
      if (!again.some((n) => n.name === boss.name)) return { ok: false, reason: `${boss.label} not found at its home` };
      continue;
    }
    if ((b.health ?? 1) <= 0) break; // downed
    const threats = await call(SERVER, "bot_threats", { user: bot.name, radius: 40, limit: 20 }).catch(() => null);
    const bossOnMe = (threats?.threats ?? []).some((x) => sameUnit(x, b) && x.onMe);
    const add = (view.nearby ?? []).filter((n) => n.entity !== b.entity && HOSTILE.test(n.name ?? "") && !/VBlood/.test(n.name ?? "")
      && Math.hypot(n.x - b.x, n.z - b.z) < 15).sort((x, y) => x.distance - y.distance)[0];
    const target = add && (!bossOnMe || add.distance < b.distance - 3) ? add : b;
    if (target === add) adds++;
    const r = await fight(bot, target, { fleeBelow: 0.4, chooseWeapon: true, bite: target === b ? "never" : "auto", leash, timeoutMs: Math.max(5000, until - Date.now()) });
    if (r.reason === "downed") return { ok: false, reason: "downed", boss: boss.label, adds };
    if (r.reason === "fled") return { ok: false, reason: "retreat (health)", bossHealth: b.health, boss: boss.label, adds, stats: r.stats };
    if (target === b && r.downed) { b = r.target; break; }
    if (target === b && r.reason === "lost target") return { ok: false, reason: `${boss.label} reset or left`, adds };
  }
  if (!b || (b.health ?? 1) > 0) return { ok: false, reason: "boss not downed", boss: boss.label, adds };
  return feedOnBoss(bot, boss, b);
}

/** Hover the downed boss and hold Interact; the feed channel then runs ~8.5 s. Verified by UnlockedVBlood. */
export async function feedOnBoss(bot, boss, target) {
  const before = await unlockedVBlood(bot).catch(() => null);
  for (let attempt = 0; attempt < 3; attempt++) {
    await bot.goTo(target, { stop: 1.5, attempts: 1, timeoutMs: 10000 });
    await bot.stop();
    await bot.aim(target);
    await bot.hover(target.entity);
    await bot.drive({ action: "hold", buttons: "Interact", seconds: 2 });
    await sleep(10500);
    await bot.drive({ action: "unhover" });
    const after = await unlockedVBlood(bot).catch(() => null);
    if (after?.has(boss.name) && !before?.has(boss.name)) return { ok: true, fed: boss.label };
  }
  return { ok: false, reason: `feed on ${boss.label} not confirmed` };
}
