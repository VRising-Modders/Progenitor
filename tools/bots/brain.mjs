#!/usr/bin/env node
// EXPERIMENTAL, EARLY DEVELOPMENT, development-only: headless test-client bots, not a supported framework feature and
// not part of the Release package. Read tools/bots/README.md first; may change or be removed.
// Layer 5: the scripted goal-priority brain. Each tick it observes (vitals, bot_look, drops, day/night,
// journal), picks ONE action with the pure `decide(snapshot)`, runs that bounded skill, and records
// the outcome (cooldowns, per-target failure counts) so a failing target or skill cannot loop.
//   node tools/bots/brain.mjs --bots 1:HeadlessOne,2:HeadlessTwo,3:HeadlessThree --minutes 20
import { pathToFileURL } from "node:url";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { call } from "./bridge.mjs";
import { Bot, SERVER, sleep, reachable } from "./bot.mjs";
import { blacklist, blacklisted, pruneBlacklist, failureKind } from "./reach.mjs";
import { recover, leaveCrypt, inCrypt, feedOn, eatCarried, catchCritter, safePrey, gather, fight, dist, feedOnWounded, FEEDABLE, isFood, HEALING } from "./skills.mjs";
import { HOSTILE, findDrops, pickupNearby, followJournal, planJournal, buildHeart, myCastle, travel, pantry } from "./progression.mjs";
import { personaOf, planGoals, planHome, runStep, SHADE_WORK_RADIUS, START_ZONE_Z, dayTripOk } from "./goals.mjs";
import { bossTooStrong, unlockedVBlood, bossFits, BOSS_MIN_BLOOD, BOSSES } from "./boss.mjs";
import { kitForLevel, rungFor } from "./arena.mjs";
import { castleLayout, roomPlan } from "./layout.mjs";
import { recordGround, bestGround } from "./grounds.mjs";
import { shadeSpots, shadowDir, recordShade, provenShade, shadeAnchors, sunSlack, timeToBurn } from "./shade.mjs";
import { newTracks, updateTracks, threatAt, threatOf, threatSummary, strength, PREY_BUDGET, TRAIN_BUDGET, OUTMATCHED, PACKED } from "./threat.mjs";

// ---- tuning -------------------------------------------------------------------------------------
export const TICK_MS = 1500;                  // pause between decisions
export const LOOK_RADIUS = 60;
// Act early: 5 s of sun from 0 to 1 (live), and a shade search can take seconds (burns at 0.5, live).
export const SLACK_SEEK_S = 1.5;              // seek shade when the sun budget left after the walk to shade is under this
export const SUN_CHARGE_HARD = 0.6;           // ...or past this charge whatever the budget says
export const ANCHOR_REFRESH_MS = 3000;        // shade anchors re-scored this often by day
export const SUN_CHARGE_LIMIT = 0.25;         // sun charge (0..1; burning at 1) above which the bot seeks shade
export const FLEE_HEALTH = 0.35;              // fraction of max health
export const FLEE_HOSTILE_RADIUS = 20;        // only flee from a hostile this close
export const VEIL_SLOT = 2;                   // AB_Vampire_VeilOfShadow_Group on every character (live slots read)
export const FEED_BLOOD = 45;                 // blood below this: feed (early, while still healthy enough to hunt; bots hit 0 at 30)
export const FEED_DESPERATE_BLOOD = 5;        // below this: feed even near hostiles
// Below 10% blood (Blood.LowBloodSequenceStartFactor 0.1, live) the low-blood debuff drains health to 0,
// and at 0 a vampire can neither move nor die (live 2026-09-25: two bots frozen for 40 min). A respawn
// gives ~10 blood, right at that edge, so feeding outranks everything else below 20.
export const BLOOD_CRITICAL = 20;             // below this feeding outranks resting, guards, goals and trips
export const TRIP_MIN_BLOOD = 40;             // a trip starts only above this (trips drain 0.02/s)
export const BLOOD_TOP_UP = 60;               // below this, take an easy source close by (a critter)
export const FOOD_RESERVE = 2;                // carried rats to keep for later
export const EASY_SOURCE_RADIUS = 15;         // "close by" for opportunistic feeding and stocking
// Players heal to full between fights (health lost in a fight stays lost until it ends) and never
// pull the next enemy half dead (bot-survival-combat-tips.md, change 2).
export const HUNT_HEALTH = 0.85;              // only start fights above this fraction
export const WEAK_UNIT_HP = 50;               // "weak" = current health below this
export const GUARD_RADIUS = 15;               // a weak hostile this close is fought before feeding
export const ENGAGED_RADIUS = 3.5;            // a hostile this close has engaged us: fight back
// Keep this far from a boss the gear cannot take: the Alpha Wolf caught and killed a bot fleeing from
// 6 m twice in 15 min (live 2026-09-25); units are seen out to 60 m now.
export const BOSS_AVOID_RADIUS = 50;
export const LEVEL_AVOID_MARGIN = 3;          // units more than this many levels above gear are avoided...
export const BRUTE_NEAR = 12;                  // an out-leveled unit this close is fled even when idle
export const LEVEL_AVOID_RADIUS = 25;         // ...when this close (15 m was inside a wolf's charge)
/**
 * Prey a player would take on its own: the threat around it from everything else (threat.mjs
 * threatAt, the prey itself excluded) under PREY_BUDGET. That rules out any other hostile within
 * 15 m, a pack within ~25 m (three Forest_Wolf L10 took a bot walking to "a lone wolf", live
 * 2026-09-25) and a V Blood or out-leveled unit within ~35 m (a food roam walked into Rufus and his
 * level-14 woodcutters, live). `tracks` are the remembered hostiles (a Map) or the hostiles in view
 * (an array), `gear` the bot's gear level.
 */
export function safePreyUnit(u, tracks, gear) {
  return threatAt(tracks, u, gear, { exclude: u }) < PREY_BUDGET;
}
export const FAST_UNIT = /Wolf/;               // units faster than the bot: fleeing them does not work
export const LEVEL_FIGHT_MARGIN = 2;          // picked fights (guards, prey) only up to gear + this
export const TRAIN_RADIUS = 35;               // the trainer looks for fights this far
// Arenas by the trainer's gear level (unit levels live-read): the starting graveyard (undead 1-4), and the
// woods past the Farbane graveyard exit (deer 8, wolves 10, bandits 10-16, bears 18, spiders 26).
export const ARENAS = [
  { name: "graveyard", minGear: 0 },
  { name: "Farbane woods", minGear: 15, at: { x: -960, z: -1780 } },
];
export const ARENA_RADIUS = 150;
export const TRAIN_FIGHTS_PER_WEAPON = 6;     // the trainer switches weapon this often
export const RUN_PAST_HEALTH = 0.5;           // above this, a trip home runs past hostiles too strong to fight
export const HARMLESS = /Deer|(?<!Hostile)Villager/; // feedable units that flee rather than fight back
export const SHOT_RADIUS = 15;
export const AGGRO_RADIUS = 40;               // units aggroed on the bot are noticed this far (casters hit from 20+ m)                // a unit aggroed on the bot within this is engaging it (ranged)
export const HOME_TRIP_HEALTH = 0.9;          // a trip home starts only above this (rest first)
export const HEART_FUEL_MS = 30 * 60000;      // top the heart up at most this often
export const BAG_RUN_MAX = 400;               // meters: go back for the bag dropped at death within this
export const BAG_RUN_MINUTES = 15;            // ...and for this long after the death
export const BLOOD_MEND = -1996241419;        // AB_Shapeshift_BloodMend_Group
export const BLOOD_MEND_MIN_BLOOD = 35;       // mend only with blood to spare (it costs 1 per second)
export const REST_BELOW = 0.85;               // out of a fight below this health: rest...
export const REST_UNTIL = 0.98;               // ...until full (live: +0.31 HP/s out of combat, 65 -> 84 in 60 s)
export const REST_CLEAR_RADIUS = 15;          // only with no hostile this close
export const FIGHT_BACK_HEALTH = 0.15;        // ...unless almost dead
export const MAX_TARGET_FAILURES = 2;         // failed attempts on one entity before it is ignored...
export const FAILURE_FORGET_MS = 60000;       // ...for this long
export const SKIP_FORGET_MS = 600000;         // journal skips and refused build spots are retried after this
export const MAX_JOURNAL_FAILURES = 3;        // failed attempts on one subtask before it is skipped
export const MAX_HEART_INTERRUPTS = 6;        // interrupted heart trips that count as one attempt
export const MAX_HEART_ATTEMPTS = 1;          // castle heart: one full trip per session (a failed trip is expensive)
export const SHADE_MEMORY_RADIUS = 40;        // walk back to a remembered shaded spot within this range
export const DAYTIME_REFRESH_MS = 15000;
export const QUESTS_REFRESH_MS = 30000;
export const KNOWLEDGE_REFRESH_MS = 60000;    // unlocked recipes/blueprints and the castle
export const KIT_REPLACE_BELOW = 0.2;          // trainer kit pieces below this durability are replaced
export const ROOM_SHADE_RADIUS = 60;          // the castle room is the shade of choice this close
export const PRE_DAWN_S = 90;                 // seconds before sunrise to head for cover
export const PRE_DAWN_FEED_S = 360;           // the last 6 min of night: top up to the forecast target
export const DAWN_BLOOD_MIN = 60;             // ...but at least this much
export const BLOOD_DRAIN_GUESS = 0.03;        // blood/s before a measurement (walking ~0.02, combat ~0.08)
export const BRAZIER_TEND_MS = 300000;        // feed/check the mist brazier at most every 5 minutes
export const SUN_RESET_CHARGE = 0.1;          // by day, wait in shade until sun charge is back under this
export const QUESTS_ERROR_BACKOFF_MS = 300000; // bot_quests missing or failing: retry rarely
export const ERROR_BACKOFF_BASE_MS = 2000;
export const ERROR_BACKOFF_MAX_MS = 30000;
// Cooldown after a failed action, so the next tick falls through to a lower priority.
export const FAIL_COOLDOWN_MS = {
  leaveCrypt: 30000, flee: 3000, feed: 20000, pickup: 10000, journal: 20000, buildHeart: 600000,
  retrieve: 30000, hunt: 10000, gather: 15000, seekShade: 20000, shelter: 0, recover: 5000, goal: 20000, stock: 20000,
};

// ---- the pure decision ----------------------------------------------------------------------------

const ready = (s, key) => !((s.cooldowns?.[key] ?? 0) > s.now);
// A target that failed too often is left alone for a while, not forever (a written-off skeleton
// otherwise blocked every trip past it, review 2026-09-24). An unreachable or stalled one is
// blacklisted at once for 5 minutes (reach.mjs), by entity and by name within 3 m. The blacklist
// rides in `failures.blacklist` so the snapshot carries it with the counts.
const usable = (s, t) => !blacklisted(s.failures?.blacklist, t, s.now ?? Date.now())
  && ((s.failures?.[t.entity] ?? 0) < MAX_TARGET_FAILURES
    || (s.now ?? 0) - (s.failureAt?.[t.entity] ?? s.now ?? 0) > FAILURE_FORGET_MS);
const frac = (v) => (v?.maxHealth ? (v.health ?? 0) / v.maxHealth : 1);

/**
 * Pick one action from a plain snapshot. Priorities, first match wins:
 *  1 dead/downed -> recover        2 in crypt -> leaveCrypt       3 sun danger -> seekShade
 *  4 low health + hostile near -> flee                             5 low blood -> feed
 *  6 drop nearby -> pickup
 *  7 the persona's first goal with a doable step (journal, heart, castle, gear; goals.mjs)
 *  8 the persona's stock rotation: gather names, or hunt a weak unit      9 idle
 * Snapshot: { now, vitals:{health,maxHealth,dead,blood,sun:{inSun,charge,burning}}, self, inCrypt,
 * daytime:{isDay,sunDamage}|null, hostiles[], weakUnits[], drops[], journal:{claims,next}|null,
 * hasHeart, heartAttempts, cooldowns:{key:untilMs}, failures:{entity:count}, gatherTurn,
 * persona, bag, equipped, recipes, blueprints, castle, refused }.
 */
export function decide(s) {
  const v = s.vitals ?? {};
  // Starved, not downed: at blood 0 health drains to exactly 0 without death or a downed state, so
  // giving up and respawning do nothing (live 2026-09-25 a bot "recovered" every 17 s for 10 min).
  // When a recovery just changed nothing, the way out is blood: eat or feed.
  const starvedAlive = !v.dead && (v.health ?? 1) <= 0 && s.noRecoverUntil > (s.now ?? 0);
  if (starvedAlive) {
    // Frozen at 0 health the bot cannot walk: only carried food helps; otherwise wait (normal work
    // would only pile up failures).
    return ready(s, "feed") ? { action: "feed", reason: "health 0 from starvation (not downed): eat", frozen: true }
      : { action: "idle", reason: "frozen at 0 health from starvation: waiting" };
  }
  if (v.dead || ((v.health ?? 1) <= 0 && !starvedAlive)) return { action: "recover", reason: v.dead ? "dead" : "downed (health 0)" };

  if (s.inCrypt && ready(s, "leaveCrypt")) return { action: "leaveCrypt", reason: "still in the starting crypt" };

  // Sun: charge must reach 1 before any damage, so act at half charge; night never burns. Without a
  // day/night read, trust the server's inSun (it is already cleared outside the damage window).
  const sun = v.sun ?? {};
  const sunWindow = s.daytime ? !!s.daytime.sunDamage : true;
  // One sun model: seconds to burning less the walk to the nearest likely shade (s.sunSlack). The bot
  // heads for shade when that budget runs low, wherever it is, instead of at a fixed charge (a far
  // shade and a near one need different starts). Without anchors in view, the charge limit.
  const lowSlack = typeof s.sunSlack === "number" && s.sunSlack < SLACK_SEEK_S;
  const overCharge = (sun.charge ?? 0) > (typeof s.sunSlack === "number" ? SUN_CHARGE_HARD : SUN_CHARGE_LIMIT);
  if (sun.burning || (sunWindow && sun.inSun && (lowSlack || overCharge))) {
    return { action: "seekShade", reason: sun.burning ? "burning in the sun" : `in sun, charge ${(sun.charge ?? 0).toFixed(2)}, slack ${s.sunSlack?.toFixed(1) ?? "?"} s` };
  }

  const hp = frac(v);
  // Blood is a rising priority as it falls (V Rising: at blood 0 health drains instead of
  // regenerating; live, a bot starved to 1 HP resting). Carried food is instant and safe: eat it as
  // soon as blood is low, whatever else is going on.
  const blood = typeof v.blood === "number" ? v.blood : 100;
  const food = (s.bag?.items ?? []).filter((i) => isFood(i.item)).reduce((n, i) => n + i.amount, 0);
  if (blood < FEED_BLOOD && food > 0 && ready(s, "feed")) return { action: "feed", reason: `blood ${blood.toFixed(1)}: eat carried food (${food})`, carried: true };
  // A V Blood boss the bot's gear cannot take yet: keep away from it (level difference: it takes
  // ~0.6x damage and deals ~1.6x), whatever the bot's health. The Alpha Wolf and Goreswine roam
  // next to the bots' plots (live boss map).
  const gear = s.bag?.gear?.total ?? s.gearTotal ?? 0; // last known when a bag read failed (review: 0 fled a level-5 skeleton)
  const boss = (s.hostiles ?? []).filter((h) => h.distance <= BOSS_AVOID_RADIUS && bossTooStrong(h.name, gear)).sort((a, b) => a.distance - b.distance)[0];
  if (boss && ready(s, "flee")) return { action: "flee", reason: `${boss.name} at ${boss.distance.toFixed(0)} m is too strong at gear ${gear}`, target: boss };
  // Any unit well above the bot's level, as the game shows it (live around the castles: bears and
  // armored skeletons 18-20, spiders 26, stone golems 36 against gear 6): keep away.
  // Harmless units (a bandit camp's prisoner villager, L14) do not count: fleeing one abandoned a fair
  // fight at 45% health with two bandits on the bot, and it died (live 2026-09-25).
  // Only one that is on the bot, coming closer or very near: a level-38 golem standing idle 20 m off was
  // "fled" 32 times in 45 min while the bot never moved (live 2026-09-25). Idle ones are kept clear of
  // by the threat measure on routes and picks instead.
  const brute = (s.hostiles ?? []).filter((h) => h.distance <= LEVEL_AVOID_RADIUS && (h.level ?? 0) > gear + LEVEL_AVOID_MARGIN && !HARMLESS.test(h.name ?? "")
    && (h.onMe || h.approaching || h.distance <= BRUTE_NEAR || h.approaching === undefined && h.onMe === undefined && s.hostilesTracked !== true)).sort((a, b) => a.distance - b.distance)[0];
  if (brute && ready(s, "flee")) return { action: "flee", reason: `${brute.name} (level ${brute.level}) at ${brute.distance.toFixed(0)} m is far above gear ${gear}`, target: brute };
  // Engaged: a hostile on top of the bot. Fleeing from 1.6 m while pinned against it lost every time
  // (live 2026-09-24, the skeleton guarding the route to the rats), so fight back.
  const critterNear = (s.critters ?? []).find((c) => c.distance <= EASY_SOURCE_RADIUS && usable(s, c));
  const inGraveyard = !s.inCrypt && (s.self?.z ?? 0) < START_ZONE_Z;
  // Get out of the killing ground before anything else that walks: a freshly respawned bot has ~9
  // blood, and feeding first sent every bot to the rats among the graveyard skeletons, where it died
  // again (live 2026-09-24, a respawn loop). Players leave the graveyard first and feed in the woods.
  // The same holds for walking home to the castle. Desperate hunger still feeds on the spot.
  const day = s.daytime ? { sunOut: !!s.daytime.sunDamage, dawnIn: s.daytime.isDay ? 0 : s.daytime.secondsUntilDay } : null;
  const home = personaOf(s.persona).goals.includes("home") ? planHome({ self: s.inCrypt ? null : s.self, castle: s.castle, frontier: s.frontier, day }) : null;
  // Engaged by a unit the bot can take: fight back, then carry on (a player walks out of the
  // graveyard killing the level 1-4 undead that aggro, one at a time). Running past them instead
  // ended every home trip in a death: the bots walked straight into fences with 3-5 skeletons on
  // them (live 2026-09-25, 66 respawns). Always, whatever its failure count: travel and work hand
  // over at this range, so skipping it here froze bots next to it.
  // Engaged: on top of the bot, or aggroed on it within shooting range (live 2026-09-25 a bot was
  // shot to death on its way home with nothing within 3.5 m: crossbow skeletons fire from 10+ m).
  const engaged = (s.hostiles ?? []).filter((h) => h.distance <= ENGAGED_RADIUS || (h.onMe && h.distance <= SHOT_RADIUS))
    .sort((a, b) => a.distance - b.distance)[0];
  const canTake = (u) => (u.level ?? 0) <= gear + LEVEL_FIGHT_MARGIN;
  // Low health: running is right only from what the bot cannot beat (a stronger unit, or two or more
  // on it); a single fair enemy chases and hits a fleeing bot, so it is finished instead (about 11 of
  // 54 deaths were bots running from weak enemies, bot-survival-combat-tips.md change 1).
  const attackers = (s.hostiles ?? []).filter((h) => h.onMe || h.distance <= ENGAGED_RADIUS);
  // Remembered hostiles (threat.mjs tracks; the view when a snapshot has none), for every pack check.
  const tracks = s.tracks ?? s.hostiles ?? [];
  // One threat measure (threat.mjs): the attackers' summed strength is outmatched from two weak ones
  // or one above gear + 2, and a pack closing in around the bot counts before it has engaged.
  const onMe = threatOf(attackers, gear);
  const outmatched = onMe >= OUTMATCHED || attackers.some((h) => (h.level ?? 0) > gear + LEVEL_FIGHT_MARGIN)
    || (attackers.length > 0 && (s.self?.x !== undefined ? threatAt(tracks, s.self, gear) : threatOf(s.hostiles?.filter((h) => h.distance <= 15), gear)) >= PACKED);
  // A pack (three weak ones on the bot) is left early: four level-10 bandits killed the gear-23
  // trainer that broke off at 5% (live).
  const packed = onMe >= PACKED && hp < 0.6;
  // Wolves outrun a vampire on foot: running from them only turns the bot's back (two bots fled two
  // Forest_Wolf L10 at 46-49% health and died, live 2026-09-25). A fight it can win is fought out.
  const cannotOutrun = attackers.length > 0 && attackers.every((h) => FAST_UNIT.test(h.name ?? "") && !/VBlood/.test(h.name ?? "") && canTake(h));
  const mustRun = !cannotOutrun && ((hp < FLEE_HEALTH && outmatched) || packed);
  // Wolves on the bot: fought to the end, weakest first, whatever the hunt cooldown (a fight broken
  // off at 15% left a 3% bot with two wolves on it walking off to build, review 2026-09-25).
  if (cannotOutrun) {
    const t = [...attackers].sort((a, b) => (a.health ?? Infinity) - (b.health ?? Infinity) || a.distance - b.distance)[0];
    return { action: "hunt", reason: `${attackers.length} x ${t.name} on the bot, which cannot outrun them: fight it out (weakest first)`, target: t, fleeBelow: 0.02 };
  }
  if (engaged && canTake(engaged) && hp > FIGHT_BACK_HEALTH && !mustRun && ready(s, "hunt")) {
    return { action: "hunt", reason: `engaged by ${engaged.name} at ${engaged.distance} m: fight back`, target: engaged, fleeBelow: FIGHT_BACK_HEALTH };
  }
  // Corpse run: death drops everything carried in a container where the bot fell. A player goes
  // back for it before anything else that walks (materials for the castle were being lost at every
  // graveyard death, live 2026-09-25).
  // A walk starts by day only from shade with the sun exposure reset: otherwise the trip stops at
  // half charge, seekShade walks back, and the bot shuttles (review 2026-09-25).
  // Trips cross daylight by hopping shade (shade.mjs), so they only need to start with exposure in
  // hand, not reset.
  const sunReady = !sunWindow || !sun.inSun || (sun.charge ?? 0) <= 0.5;
  const bag = s.lostBag;
  if (bag && s.now - bag.at < BAG_RUN_MINUTES * 60000 && hp > HOME_TRIP_HEALTH && blood >= BLOOD_CRITICAL && sunReady && s.self && ready(s, "retrieve")) {
    const far = Math.hypot(bag.x - s.self.x, bag.z - s.self.z);
    if (far <= BAG_RUN_MAX) return { action: "retrieve", reason: `go back for the bag dropped at death (${far.toFixed(0)} m)`, spot: bag };
  }
  // A trip home, once healthy (rest comes first below): it runs past only units too strong to fight.
  const startZone = !s.inCrypt && (s.self?.z ?? 0) < START_ZONE_Z;
  // In the graveyard a hungry bot may still leave when no rat is at hand (the exit is instant).
  const tripFed = blood >= TRIP_MIN_BLOOD || (startZone && !critterNear);
  const homeByDay = !sunWindow || !home || dayTripOk({ self: s.self }, home.to);
  if (home && hp > HOME_TRIP_HEALTH && sunReady && tripFed && homeByDay && ready(s, "goal:home")) return { action: "goal", goal: "home", step: home, reason: `home: ${home.why}` };
  if (engaged && hp > FIGHT_BACK_HEALTH && !mustRun && ready(s, "hunt")) {
    return { action: "hunt", reason: `engaged by ${engaged.name} (level ${engaged.level}) at ${engaged.distance} m: fight back`, target: engaged, fleeBelow: FIGHT_BACK_HEALTH };
  }
  const threat = (s.hostiles ?? []).filter((h) => h.distance <= FLEE_HOSTILE_RADIUS).sort((a, b) => a.distance - b.distance)[0];
  if (cannotOutrun && hp < FLEE_HEALTH && ready(s, "hunt")) {
    const t = [...attackers].sort((a, b) => (a.health ?? Infinity) - (b.health ?? Infinity) || a.distance - b.distance)[0];
    return { action: "hunt", reason: `health ${(hp * 100).toFixed(0)}%: ${t.name} cannot be outrun, fight it out (weakest first)`, target: t, fleeBelow: 0.02 };
  }
  if (hp < FLEE_HEALTH && attackers.length === 1 && !outmatched && ready(s, "hunt")) {
    return { action: "hunt", reason: `health ${(hp * 100).toFixed(0)}%: finish ${attackers[0].name} rather than run`, target: attackers[0], fleeBelow: 0.05 };
  }
  if ((hp < FLEE_HEALTH || packed) && threat && (outmatched || !attackers.length) && !cannotOutrun && ready(s, "flee")) {
    return { action: "flee", reason: `health ${(hp * 100).toFixed(0)}% with ${threat.name} at ${threat.distance} m${outmatched ? " (outmatched)" : ""}`, target: threat };
  }

  // Rest: health comes back out of combat (~0.3 HP/s), so a hurt bot recovers before its next fight or
  // trip instead of walking into it at 30% (the graveyard deaths). Hysteresis: once resting, until 90%.
  // Quiet: nobody close, nobody aggroed on the bot at any range, and health not falling (a bot
  // "rested" with Blood Mend while a level-27 Undead Priest shot it from beyond 15 m, live).
  const quiet = !(s.hostiles ?? []).some((h) => h.distance <= REST_CLEAR_RADIUS || h.onMe) && !s.hpFalling;
  // Only with blood in the pool: at blood 0 health drains instead of regenerating (live: the trainer
  // "rested" at 1 HP and blood 0 for half an hour), so a starving bot feeds first.
  const fed = typeof v.blood !== "number" || v.blood >= FEED_BLOOD;
  // Never rest in the sun by day (a bot rested at 0.39 exposure and burned, live): shade first.
  const restSun = sunWindow && v.sun?.inSun;
  if (restSun && quiet && !s.inCrypt && hp < REST_BELOW && ready(s, "seekShade")) return { action: "seekShade", reason: "hurt in the sun: shade before resting" };
  if (quiet && fed && !s.inCrypt && !restSun && (hp < REST_BELOW || (s.resting && hp < REST_UNTIL))) {
    return { action: "rest", reason: `health ${(hp * 100).toFixed(0)}%: recovering out of combat` };
  }

  // Clear the guard: while still healthy, fight a weak hostile close by before doing anything else
  // (live 2026-09-24: both bots died walking to a rat past a withered skeleton at low health).
  // Only an isolated one: withered skeletons caused most deaths, in groups (live death log).
  const isolated = (h) => threatAt(tracks, h, gear, { exclude: h }) < PREY_BUDGET;
  const fair = (u) => (u.level ?? 0) <= gear + LEVEL_FIGHT_MARGIN;
  const guard = (s.hostiles ?? []).filter((h) => h.distance <= GUARD_RADIUS && (h.health ?? Infinity) <= WEAK_UNIT_HP && usable(s, h) && isolated(h) && fair(h))
    .sort((a, b) => a.distance - b.distance)[0];
  if (hp > HUNT_HEALTH && guard && ready(s, "hunt")) {
    return { action: "hunt", reason: `clear ${guard.name} (${guard.health} hp) at ${guard.distance} m before anything else`, target: guard };
  }

  // In the starting graveyard even a starving bot leaves: its prey there is rats among skeletons, and
  // the undead have no blood (live: desperate feeding there ended in death every time).
  const inStart = !s.inCrypt && (s.self?.z ?? 0) < START_ZONE_Z;
  if (home && hp > HOME_TRIP_HEALTH && sunReady && tripFed && homeByDay && ready(s, "goal:home")) {
    return { action: "goal", goal: "home", step: home, reason: `home: ${home.why}` };
  }

  // Critical blood: feeding comes before goals and trips (resting already yields: no regeneration
  // without blood). In the starting graveyard only a critter close by counts (the undead there have
  // no blood); otherwise leaving for the woods, where living prey is, comes first.
  if (blood < BLOOD_CRITICAL && ready(s, "feed") && (!inGraveyard || critterNear)) {
    return { action: "feed", reason: `blood ${blood.toFixed(1)} critical: feed first`, relaxed: blood < FEED_DESPERATE_BLOOD };
  }
  // The last minutes of night: top up, so the day (no food roams by day) starts fed (both builders sat
  // at 10 blood in shade by day with no prey in sight, live 2026-09-25).
  // Forecast, not the current value: enter the day with what the day will drain (x1.5) plus a margin,
  // and by day feed on what comes by once the blood projected at nightfall falls under critical.
  const drain = s.bloodDrain ?? BLOOD_DRAIN_GUESS;
  const dawnSoon = s.daytime && !s.daytime.isDay && (s.daytime.secondsUntilDay ?? Infinity) < PRE_DAWN_FEED_S;
  const dawnTarget = Math.min(90, Math.max(DAWN_BLOOD_MIN, drain * (s.daytime?.dayLength ?? 360) * 1.5 + 25));
  if (dawnSoon && blood < dawnTarget && hp > HUNT_HEALTH && ready(s, "feed")) return { action: "feed", reason: `blood ${blood.toFixed(0)}: top up before sunrise (target ${dawnTarget.toFixed(0)}, drain ${drain.toFixed(3)}/s)` };
  const atNight = s.daytime?.sunDamage ? blood - drain * ((s.daytime.secondsUntilNight ?? 0) + 60) : blood;
  if (s.daytime?.sunDamage && atNight < BLOOD_CRITICAL && blood < FEED_BLOOD + 20 && ready(s, "feed")) {
    return { action: "feed", reason: `blood ${blood.toFixed(0)}: ${atNight.toFixed(0)} projected at nightfall` };
  }
  if (typeof v.blood === "number" && v.blood < FEED_BLOOD && ready(s, "feed")) {
    const relaxed = v.blood < FEED_DESPERATE_BLOOD;
    return { action: "feed", reason: `blood ${v.blood.toFixed(1)}${relaxed ? " (desperate: ignoring hostiles)" : ""}`, relaxed };
  }
  // Living prey close by below 60 blood: a feed fills to ~100 (a rat gives 20); only while healthy,
  // and only fair or harmless prey (change 9).
  // Alone: a bandit "prey" 39 m off stood in its camp and the bot died there at full health (live).
  const alonePrey = (u) => safePreyUnit(u, tracks, gear);
  const prey = (s.feedable ?? []).filter((u) => u.distance <= 40 && usable(s, u) && alonePrey(u) && ((u.level ?? 0) <= gear + LEVEL_FIGHT_MARGIN || HARMLESS.test(u.name ?? "")))
    .sort((a, b) => a.distance - b.distance)[0];
  if (prey && blood < BLOOD_TOP_UP && hp > HUNT_HEALTH && ready(s, "feed")) {
    return { action: "feed", reason: `blood ${blood.toFixed(0)}: feed on ${prey.name} (${prey.distance.toFixed(0)} m)` };
  }
  // Easy sources close by: top up below 60, or stock rats to carry while the reserve is short.
  const safeCritter = critterNear && threatAt(tracks, critterNear, gear) < PREY_BUDGET;
  if (safeCritter && ready(s, "stock") && (blood < BLOOD_TOP_UP || food < FOOD_RESERVE)) {
    return { action: "stock", reason: blood < BLOOD_TOP_UP ? `blood ${blood.toFixed(0)}: top up from ${critterNear.name}` : `stock food (${food}/${FOOD_RESERVE})`, target: critterNear, eat: blood < BLOOD_TOP_UP };
  }

  // Before a boss the pool is topped up (players never start one on thin blood): a boss the gear
  // allows, waiting only on blood, sends the bot feeding (living prey fills to ~100).
  if (personaOf(s.persona).goals.includes("boss") && blood < BOSS_MIN_BLOOD && hp > HUNT_HEALTH && ready(s, "feed")) {
    const next = bossFits({ gear, vbloods: s.vbloods });
    if (next) return { action: "feed", reason: `blood ${blood.toFixed(0)}: top up before ${next.label}`, topUp: true };
  }

  const drop = (s.drops ?? []).find((d) => usable(s, d));
  if (drop && ready(s, "pickup")) return { action: "pickup", reason: `drop ${drop.name ?? drop.entity} at ${drop.distance?.toFixed?.(1) ?? "?"} m`, target: drop };

  // Long-term goals, in the persona's order (goals.mjs). The journal and heart keep their own skills
  // and cooldown keys; the other goals share the generic "goal" action.
  const persona = personaOf(s.persona);
  // The home trip has the same health and sun gate here as in the rules above (review 2026-09-25).
  const goalReady = (g) => (g === "journal" ? ready(s, "journal") : g === "heart" ? ready(s, "buildHeart")
    : g === "home" ? hp > HOME_TRIP_HEALTH && sunReady && tripFed && ready(s, "goal:home") : ready(s, `goal:${g}`));
  const knowledge = {
    journal: s.journal, hasHeart: s.hasHeart,
    heartAllowed: !s.inCrypt && (s.heartAttempts ?? 0) < MAX_HEART_ATTEMPTS,
    bag: s.bag, equipped: s.equipped, recipes: s.recipes, blueprints: s.blueprints, castle: s.castle, refused: s.refused,
    vitals: v, gear, vbloods: s.vbloods,
    self: s.inCrypt ? null : s.self, frontier: s.frontier, brazierDue: (s.now - (s.lastTend ?? 0)) > BRAZIER_TEND_MS,
    fuelDue: (s.now - (s.lastFuel ?? 0)) > HEART_FUEL_MS,
    now: s.now, refineLast: s.refineLast ?? {}, // per-station refining visits
    durability: s.durability, // worn gear, for repairs
    day,
    shadeNear: !!s.shade && !!s.self && Math.hypot(s.shade.x - s.self.x, s.shade.z - s.self.z) <= SHADE_WORK_RADIUS,
  };
  // Daylight, shade-hopping like a player: charge builds only in the sun and resets in shade. After
  // a run into the sun, wait in the shade until the exposure has reset; with no shade known nearby,
  // find some first (tree and structure sides), since all daytime work is done from it.
  if (knowledge.day?.sunOut && !s.inCrypt) {
    const sun = v.sun ?? {};
    if (!sun.inSun && (sun.charge ?? 0) > SUN_RESET_CHARGE) return { action: "shelter", reason: `in shade, letting sun exposure reset (${(sun.charge ?? 0).toFixed(2)})` };
    if (!knowledge.shadeNear) {
      return ready(s, "seekShade") ? { action: "seekShade", reason: "sun is up: find shade to work from" }
        : { action: "shelter", reason: "no shade found nearby: holding still until the next search" };
    }
  }
  // Shortly before sunrise, get to cover first (90 s: dawn caught bots in the open, change 3).
  if (!s.inCrypt && knowledge.day && !knowledge.day.sunOut && knowledge.day.dawnIn < PRE_DAWN_S) return { action: "shelter", reason: `sunrise in ${knowledge.day.dawnIn.toFixed(0)} s: get to cover` };
  const step = planGoals(knowledge, persona, { ready: goalReady, layout: castleLayout, turn: s.goalTurn ?? 0 });
  if (step?.action === "journal") {
    const j = s.journal;
    const why = j.claims > 0 ? `${j.claims} journal entr${j.claims === 1 ? "y" : "ies"} to claim`
      : `journal: ${j.next.subtask?.name ?? "subtask"} -> ${j.next.plan.action}`;
    return { action: "journal", reason: why };
  }
  if (step?.action === "buildHeart") return { action: "buildHeart", reason: "no castle heart yet" };
  if (step) return { action: "goal", goal: step.goal, step, reason: `${step.goal}: ${step.why ?? step.action}` };

  // Daylight: only sun-safe steps got through above; the stock rotation below still works by
  // shade-hopping (short radius). Shortly before sunrise, get to cover.
  if (!s.inCrypt && knowledge.day && !knowledge.day.sunOut && knowledge.day.dawnIn < PRE_DAWN_S) return { action: "shelter", reason: `sunrise in ${knowledge.day.dawnIn.toFixed(0)} s: get to cover` };

  // Stock rotation: the persona's staple work. The hunt slot needs health and a weak target;
  // otherwise (or while that slot is cooling down) the next ready gather name is used.
  const cycle = persona.stock;
  const turn = s.gatherTurn ?? 0;
  for (let k = 0; k < cycle.length; k++) {
    const slot = cycle[(turn + k) % cycle.length];
    if (slot === "train") {
      // The arena that matches the trainer's gear (it is handed gear to scale its opponents up, the
      // user's instruction): go there first, then fight the strongest fair opponent in view, isolated
      // ones first; nothing in view: roam the arena.
      const arena = [...ARENAS].reverse().find((a) => gear >= a.minGear);
      if (arena?.at && s.self && Math.hypot(arena.at.x - s.self.x, arena.at.z - s.self.z) > ARENA_RADIUS && (!sunWindow || dayTripOk({ self: s.self }, arena.at)) && ready(s, "goal:arena")) {
        return { action: "goal", goal: "arena", step: { action: "travel", to: arena.at, why: `to the ${arena.name} arena (gear ${gear})` }, reason: `arena: ${arena.name} for gear ${gear}` };
      }
      // Opponents equivalent to its gear first (within 6 levels below), then anything fair.
      const near = (h) => (h.level ?? 0) >= gear - 6;
      // Not into a camp: one companion is fine, two are a pack (both trainer deaths in one window were
      // three Bandit_Scouts on it at the Farbane camp, live 2026-09-25): the threat around the target
      // from the others stays under TRAIN_BUDGET.
      const company = (h) => threatAt(tracks, h, gear, { exclude: h });
      const foes = (s.hostiles ?? []).filter((h) => fair(h) && usable(s, h) && h.distance <= TRAIN_RADIUS && !bossTooStrong(h.name, gear) && company(h) < TRAIN_BUDGET)
        .sort((a, b) => (near(b) - near(a)) || (isolated(b) - isolated(a)) || (b.level ?? 0) - (a.level ?? 0) || a.distance - b.distance);
      // The top three go along: the hunt path-checks them in order before committing (actOnce).
      if (foes[0] && hp > HUNT_HEALTH && ready(s, "hunt")) return { action: "hunt", reason: `train on ${foes[0].name} (level ${foes[0].level ?? "?"})`, target: foes[0], candidates: foes.slice(0, 3), turn: turn + k };
      if (arena?.at && !foes.length && hp > HUNT_HEALTH && !sunWindow && ready(s, "goal:arena")) {
        const a = ((s.now ?? 0) / 1000) % (2 * Math.PI);
        return { action: "goal", goal: "arena", step: { action: "travel", to: { x: arena.at.x + Math.sin(a) * 60, z: arena.at.z + Math.cos(a) * 60 }, why: "roam the arena for opponents" }, reason: `arena: nothing in view, roam` };
      }
      continue;
    }
    if (slot === "hunt") {
      const weak = hp > HUNT_HEALTH && ready(s, "hunt")
        ? (s.weakUnits ?? []).filter((u) => usable(s, u) && fair(u)).sort((a, b) => a.distance - b.distance) : [];
      const prey = weak[0];
      if (prey) return { action: "hunt", reason: `weak ${prey.name} (${prey.health} hp) at ${prey.distance} m`, target: prey, candidates: weak.slice(0, 3), turn: turn + k };
      continue;
    }
    if (ready(s, "gather") && ready(s, `gather:${slot}`)) {
      return { action: "gather", reason: `rotation: ${slot}`, name: slot, turn: turn + k, ...(knowledge.day?.sunOut ? { radius: SHADE_WORK_RADIUS } : {}) };
    }
  }
  return { action: "idle", reason: "nothing ready (all on cooldown)" };
}

// ---- observation ----------------------------------------------------------------------------------

/**
 * Put learned spells on the action bar, as a player does from the spell book: a spell group known to
 * the character (listed among the hidden ability slots, e.g. AB_Blood_BloodRite in slot 12) but not
 * on the bar is sent with ActivateVBloodAbilityEvent (bot_event spell). The first spell slot comes
 * with Journal_WieldTheSword, the second with Journal_ControllingMagic; a locked slot just refuses.
 */
async function slotSpells(bot) {
  const r = await call(SERVER, "bot_cast", { user: bot.name, action: "slots" });
  const SPELL = /^AB_(Blood|Chaos|Frost|Illusion|Storm|Unholy)_/;
  const bar = (r.slots ?? []).filter((x) => x.slot >= 1 && x.slot <= 8 && SPELL.test(x.name ?? ""));
  const known = (r.slots ?? []).filter((x) => x.slot > 8 && SPELL.test(x.name ?? "") && !bar.some((b) => b.name === x.name));
  if (!known.length || bar.length >= 2) return;
  await call(bot.client, "bot_event", { kind: "spell", ability: known[0].name, primary: bar.length === 0 });
  bot._spellsAt = 0; // re-read the bar in fights
}

/**
 * Output items the bot can craft now: unlocked recipes that are in-hand or have their station in the
 * castle (station-only journal crafts otherwise failed three times and were skipped). Null before the
 * first read.
 */
const craftableItems = (recipes, castle) => {
  if (!recipes) return null;
  const stations = new Set((castle?.buildings ?? []).map((b) => b.name));
  return new Set(recipes.filter((r) => r.characterCraftable || (r.stations ?? []).some((x) => stations.has(x.name)))
    .flatMap((r) => (r.outputs ?? []).map((o) => o.item)));
};

export function newState() {
  return {
    cooldowns: {}, failures: {}, journalFailures: {}, journalSkip: new Set(), claimSkip: new Set(), claimFailures: {}, skip: new Set(),
    gatherTurn: 0, hasHeart: false, heartAttempts: 0, heartInterrupts: 0, heartCache: {}, inedible: new Set(), lastShade: null, last: null,
    persona: "balanced", goalTurn: 0, frontier: null, recipes: null, blueprints: null, castle: null, knowledgeAt: 0, castleAt: 0,
    refusedAt: {}, failureAt: {}, journalSkipAt: {},
    daytime: null, daytimeAt: 0, quests: null, questsAt: 0, consecutiveErrors: 0,
  };
}

async function observe(bot, st) {
  const now = Date.now();
  // Units only: the 80 nearest objects of every kind reached ~25 m in the woods (trees), so prey and
  // packs beyond were unseen (live 2026-09-25: a full 200-object view reached 47 m and held 9 of 20 units).
  const [vitals, view, aggro] = await Promise.all([bot.vitals(), bot.look({ radius: LOOK_RADIUS, limit: 80, kind: "unit" }),
    call(SERVER, "bot_threats", { user: bot.name, radius: AGGRO_RADIUS, limit: 30 }).catch(() => null)]);
  // Units aggroed on the bot (bot_threats onMe): a crossbow skeleton shoots from 10+ m. bot_threats
  // names server entities and bot_look client ones, so they are matched by name and position.
  const aggroed = (aggro?.threats ?? []).filter((x) => x.onMe);
  const isOnMe = (u) => aggroed.some((x) => x.name === u.name && Math.hypot(x.x - u.x, x.z - u.z) < 2);
  const self = view.self ?? {};
  const units = (view.nearby ?? []).filter((n) => n.kind === "unit");
  if (now - st.daytimeAt > DAYTIME_REFRESH_MS) {
    st.daytimeAt = now;
    try { st.daytime = await call(SERVER, "bot_daytime", {}); } catch { st.daytime = null; }
    try { st.durability = (await call(SERVER, "bot_repair", { user: bot.name })).slots; } catch { /* keep */ }
    // TEST SETUP, trainer only (the user's instruction): its kit matches the next V Blood it has not
    // fed on, so it climbs the ladder in order against equivalent opponents (arena.mjs LADDER).
    if (st.persona === "trainer" && st.vbloods) {
      const next = BOSSES.find((b) => !st.vbloods.has(b.name));
      const rung = next ? rungFor(next.level) : null;
      // Worn-out kit pieces are replaced like the rest of the kit (its reinforced bone is not repaired
      // with bones: refused with 105 in the bag, live).
      const fresh = (st.durability ?? []).filter((d) => d.max > 0 && d.slot !== "weapon" && d.durability / d.max < KIT_REPLACE_BELOW).map((d) => d.item);
      if (rung && (st.rung !== rung.upTo || fresh.length)) {
        const r = await kitForLevel(bot, next.level, { fresh }).catch((e) => ({ error: e.message }));
        if (fresh.length) bot.log(`arena kit: fresh ${fresh.join(", ")}`);
        if (!r.error) { st.rung = rung.upTo; st.weaponTierCap = rung.weapons; bot.log(`arena kit for ${next.label} (level ${next.level}): gear ${r.gear?.total}`); }
      }
    }
    bot.daytime = st.daytime; // travel and fights read the sun from here
  }
  if (now - st.questsAt > QUESTS_REFRESH_MS) {
    st.questsAt = now;
    // Owning a heart is server state, not session state: a later session must not build a second one.
    if (!st.hasHeart) {
      try { st.hasHeart = ((await call(SERVER, "castles", {})).plots ?? []).some((p) => p.owner?.name === bot.name); } catch { /* keep */ }
    }
    try { st.quests = await call(SERVER, "bot_quests", { user: bot.name }); }
    catch { st.quests = null; st.questsAt = now + QUESTS_ERROR_BACKOFF_MS - QUESTS_REFRESH_MS; }
  }
  // What the bot knows for its goals: unlocked recipes and blueprints, its castle (slow-changing,
  // refreshed each minute or after a build), and its bag and gear (every tick; cheap).
  // Skips expire: a journal step that needed a station, or a build spot refused before its floor
  // existed, becomes worth another try once the castle has grown.
  for (const [id, at] of Object.entries(st.journalSkipAt)) {
    if (now - at > SKIP_FORGET_MS) { st.journalSkip.delete(Number(id)); st.journalSkip.delete(id); delete st.journalSkipAt[id]; delete st.journalFailures[id]; }
  }
  for (const [key, at] of Object.entries(st.refusedAt)) if (now - at > SKIP_FORGET_MS) delete st.refusedAt[key];
  if (now - st.knowledgeAt > KNOWLEDGE_REFRESH_MS) {
    st.knowledgeAt = now;
    st.castleAt = 0;
    try { st.recipes = (await call(SERVER, "bot_recipes", { user: bot.name, onlyUnlocked: true, limit: 1000 })).recipes; } catch { /* keep */ }
    try { st.blueprints = (await call(SERVER, "bot_blueprints", { user: bot.name, limit: 500 })).blueprints; } catch { /* keep */ }
    try { st.vbloods = (await unlockedVBlood(bot)) ?? st.vbloods; } catch { /* keep */ }
    try { await slotSpells(bot); } catch { /* optional */ }
  }
  if (!st.castleAt) {
    st.castleAt = now;
    try { st.castle = await myCastle(bot); if (st.castle) st.hasHeart = true; } catch { /* keep */ }
    // A closed room is shade only once the server gave it a roof (CastleFloorRoof.RoofEntity set on
    // its floor): HeadlessOne's first ring stayed IsMissingWalls, HasRoof false (live 2026-09-25).
    st.roomRoofed = await roomHasRoof(st.castle).catch(() => false);
    // Nearest free plot center: where a castle-less bot heads when it leaves the graveyard.
    if (!st.castle && !st.frontier && self.x !== undefined) {
      try {
        const t = (await call(SERVER, "bot_territories", { x: self.x, z: self.z, limit: 5, onlyFree: true })).results?.find((r) => r.region && r.region !== "None");
        if (t?.centre) st.frontier = { x: t.centre.x, z: t.centre.z };
      } catch { /* keep */ }
    }
  }
  let bag = null;
  try { bag = await bot.inventory(); st.gearTotal = bag?.gear?.total ?? st.gearTotal; } catch { /* optional */ }
  let drops = [];
  try { drops = await findDrops(bot); } catch { /* optional */ }
  const craftable = craftableItems(st.recipes, st.castle);
  const journal = st.quests ? (({ claims, next }) => ({ claims: claims.length, next }))(planJournal(st.quests, st.journalSkip, st.claimSkip, { craftable })) : null;
  // Remember where shade was found during the damage window, to walk back to it later.
  if (st.daytime?.sunDamage && vitals.sun && !vitals.sun.inSun && !inCrypt(self)) {
    st.lastShade = { x: self.x, y: self.y, z: self.z };
    recordShade(bot, self, st.daytime.sunRotation?.[1]);
  }
  // Shade anchors, scored before they are needed (shade.mjs shadeAnchors), and the sun budget.
  if (st.daytime?.sunDamage && self.x !== undefined && !inCrypt(self) && now - (st.anchorsAt ?? 0) > ANCHOR_REFRESH_MS) {
    st.anchorsAt = now;
    const around = await bot.look({ radius: 35, limit: 250 }).catch(() => ({ nearby: [] }));
    st.shadeAnchors = shadeAnchors(bot, self, around.nearby, st.daytime, homeShade(st, self));
  } else if (!st.daytime?.sunDamage) st.shadeAnchors = [];
  // Walk times from where the bot is now (it may have moved since the anchors were scored).
  const anchors = self.x === undefined ? [] : (st.shadeAnchors ?? []).map((a) => ({ ...a, walkS: Math.hypot(a.x - self.x, a.z - self.z) / 4.3 }));
  const slack = anchors.length ? sunSlack(vitals.sun, anchors) : null;
  // Remembered hostiles (threat.mjs): this view, bot_threats rows and recent death spots. The view is
  // complete out to LOOK_RADIUS unless the unit limit cut it short.
  const covered = units.length >= 80 ? Math.max(0, ...units.map((u) => u.distance ?? 0)) : LOOK_RADIUS;
  st.tracks = updateTracks(st.tracks ?? newTracks(), units.filter((u) => HOSTILE.test(u.name ?? "") || /VBlood/.test(u.name ?? "")),
    { now, threats: aggro?.threats ?? [], self: self.x === undefined ? null : self, covered, zones: bot.dangerZones });
  // Approaching: the unit itself moved more than 1 m toward the bot's current position since a tick
  // ago (per client entity; the bot's own walking does not count, review), so an idle strong unit is
  // kept clear of rather than fled from over and over.
  const lastPos = st.lastPos ?? {};
  const hostilesNow = units.filter((u) => HOSTILE.test(u.name ?? "")).map((u) => {
    const lp = lastPos[u.entity];
    const approaching = !!lp && self.x !== undefined && Math.hypot(lp.x - self.x, lp.z - self.z) - Math.hypot(u.x - self.x, u.z - self.z) > 1;
    return { ...u, ...(isOnMe(u) ? { onMe: true } : {}), ...(approaching ? { approaching: true } : {}) };
  });
  st.lastPos = Object.fromEntries(hostilesNow.map((u) => [u.entity, { x: u.x, z: u.z }]));
  // Where prey lives: remembered for the hunt when none is in sight.
  const myGear = st.gearTotal ?? 0;
  for (const u of units) {
    // Only prey the bot can feed on (hunting trips walked to remembered bears, level 18, that it
    // was never going to take).
    const fairPrey = FEEDABLE.test(u.name ?? "") && ((u.level ?? 0) <= myGear + LEVEL_FIGHT_MARGIN || HARMLESS.test(u.name ?? ""));
    if (!(fairPrey || /Critter/i.test(u.name ?? ""))) continue;
    if (!safePreyUnit(u, st.tracks, myGear)) continue; // not prey by a V Blood or a pack
    (st.preySeen ??= []).push({ name: u.name, x: u.x, z: u.z, at: now });
    recordGround(u, { name: u.name, now }); // hunting grounds, kept across rounds (grounds.mjs)
  }
  if ((st.preySeen?.length ?? 0) > 60) st.preySeen = st.preySeen.slice(-60);
  // Blood drain, measured (an EMA of the per-second fall; feeding rises are skipped): combat drains
  // ~4x walking, so a fight-heavy night and day need more in hand (gap analysis section 8).
  if (typeof vitals.blood === "number" && typeof st.lastBlood === "number" && st.lastBloodAt) {
    const dt = (now - st.lastBloodAt) / 1000;
    // Steady drain only: a drop of more than 3 in one tick is a death, a respawn or an ability's cost
    // (the forecast read -42 at nightfall from blood 65 after a respawn, live), and the rate is capped.
    const fall = st.lastBlood - vitals.blood;
    if (dt > 0.5 && dt < 30 && fall >= 0 && fall <= 3) st.bloodDrain = 0.9 * (st.bloodDrain ?? BLOOD_DRAIN_GUESS) + 0.1 * Math.min(0.12, fall / dt);
  }
  st.lastBlood = vitals.blood; st.lastBloodAt = now;
  const hpFalling = typeof vitals.health === "number" && typeof st.lastHp === "number" && vitals.health < st.lastHp - 3;
  st.lastHp = vitals.health;
  return {
    now, vitals, self, inCrypt: self.x !== undefined && inCrypt(self), daytime: st.daytime, hpFalling, shadeAnchors: anchors, sunSlack: slack, bloodDrain: st.bloodDrain ?? BLOOD_DRAIN_GUESS,
    gearTotal: st.gearTotal, tracks: st.tracks, threat: self.x === undefined ? null : threatSummary(st.tracks, self, myGear),
    hostiles: hostilesNow, hostilesTracked: true, // approach is known: idle strong units are not fled (decide brute)
    // Creatures only: a bot "hunted" its own wooden coffin (25 hp, a TM_ structure) and died on the way.
    weakUnits: units.filter((u) => typeof u.health === "number" && u.health > 0 && u.health < WEAK_UNIT_HP && /^CHAR_/.test(u.name ?? "")),
    // Reachable critters only: live 2026-09-25 bots chased a rat at y -50 (under the ground) and one on a
    // ledge above them for half-minute tries, over and over.
    critters: units.filter((u) => /Critter/i.test(u.name ?? "") && Math.abs((u.y ?? 0) - (self.y ?? 0)) < 2.5
      && !(st.badPreySpots ?? []).some((b) => now - b.at < 600000 && Math.hypot(b.x - u.x, b.z - u.z) < 6)),
    feedable: units.filter((u) => FEEDABLE.test(u.name ?? "") && u.distance <= 60),
    drops, journal, hasHeart: st.hasHeart, heartAttempts: st.heartAttempts,
    cooldowns: st.cooldowns, failures: st.failures, gatherTurn: st.gatherTurn,
    persona: st.persona, goalTurn: st.goalTurn, resting: st.resting, bag, equipped: bag?.equipped ?? [], recipes: st.recipes, blueprints: st.blueprints,
    castle: st.castle, vbloods: st.vbloods, refused: new Set(Object.keys(st.refusedAt)), frontier: st.frontier, shade: st.lastShade, lastTend: st.lastTend, lastFuel: st.lastFuel, refineLast: st.refineLast, noRecoverUntil: st.noRecoverUntil, lostBag: st.lostBag, durability: st.durability,
    failureAt: st.failureAt,
  };
}

// ---- actions --------------------------------------------------------------------------------------

// ---- flight (internal research notes) ------------------------
// Fleeing is a position choice, made as a tactical query: candidate points around the bot, scored
// by cheap terms, the best few path-checked. The old flight ran from the nearest chaser only, so it
// ran into whatever stood on the far side, and by day into open sun (20 s of flight is four full
// exposure budgets, and flee, a survival action, is never stopped by danger()).
export const FLEE_NEAR = 14, FLEE_FAR = 20;   // candidate ring (m), alternating per direction
export const FLEE_POINTS = 12;                // directions, 30 degrees apart, the first straight away
export const FLEE_HOME_RADIUS = 200;          // the castle is the refuge within this
// A cleared, floored plot is safe from wandering creatures (the user): a meter toward home is worth
// a meter straight away, so with the castle to one side the flight bends toward it, and with the
// chaser between the two the threat and away terms win (the old rule: home unless the chaser stands
// within 60 degrees of the way home).
const FLEE_HOME_W = 1;
const LIKELY_SHADE = 0.75;                    // an anchor this likely is shade (shade.mjs sunSlack)
const FLEE_SHADE_NEAR = 8;                    // by day a point this close to a likely anchor scores its p...
const FLEE_SHADE_FAR = 15;                    // ...and one farther than this from all of them is open ground
const FLEE_OPEN_PENALTY = 0.5;
const FLEE_PATH_CHECKS = 3;                   // path-check this many of the best points
const FLEE_PATH_MS = 2500;                    // path budget per leg (as before), shared by the checks
// A leg is 3-5 s in the open: with less sun budget than this left after the walk to shade, another
// leg burns the bot, so the flight ends in shade instead.
export const FLEE_SUN_SLACK_S = 3;
// Ending in shade: anchor score (p x walk decay) less this x its threat. A chaser follows the bot to
// any anchor, so one unit costs about the gain of a 2 s nearer walk; a camp beside an anchor loses it.
const FLEE_SHADE_THREAT_W = 0.5;

/** Strength-weighted center of the units fled from (a boss outweighs the skeleton beside the bot). */
export function fleeCentroid(from, gear = 0) {
  const units = (from ?? []).filter((u) => u?.x !== undefined);
  if (!units.length) return null;
  const w = units.map((u) => strength(u, gear));
  const sum = w.reduce((n, x) => n + x, 0);
  const wt = (i) => (sum > 0 ? w[i] / sum : 1 / units.length);
  return { x: units.reduce((n, u, i) => n + u.x * wt(i), 0), z: units.reduce((n, u, i) => n + u.z * wt(i), 0) };
}

/**
 * Flight candidates around `me`: FLEE_POINTS directions from bearing `face` (straight away),
 * alternately FLEE_FAR and FLEE_NEAR out; by day also the likely shade anchors 8-25 m away, so a
 * flight can end on shade rather than beside it. Each {x, y, z, bearing, r, anchor?}.
 */
export function fleeCandidates(me, { anchors = null, face = 0, count = FLEE_POINTS, near = FLEE_NEAR, far = FLEE_FAR } = {}) {
  const out = [];
  for (let i = 0; i < count; i++) {
    const a = face + (i * 2 * Math.PI) / count, r = i % 2 ? near : far;
    out.push({ x: me.x + Math.sin(a) * r, y: me.y, z: me.z + Math.cos(a) * r, bearing: a, r });
  }
  for (const s of anchors ?? []) {
    const d = Math.hypot(s.x - me.x, s.z - me.z);
    if (s.p >= LIKELY_SHADE && d >= 8 && d <= 25) out.push({ x: s.x, y: s.y ?? me.y, z: s.z, bearing: Math.atan2(s.x - me.x, s.z - me.z), r: d, anchor: s });
  }
  return out;
}

/**
 * Score flight points, best first (pure). Terms, each about -1..1 for a 20 m leg:
 *   - threat: threatAt(tracks, p), every remembered hostile: a pack on the far side, a death spot
 *   + away:   the leg's run along the line away from the centroid of `from` (every unit fled from,
 *             not the nearest), per FLEE_FAR; a point past the chaser scores negative
 *   + home:   meters gained toward `home` (the castle heart, within FLEE_HOME_RADIUS), per FLEE_FAR
 *   + shade:  by day (`anchors` given), p of a likely anchor near the point, fading to 0 at
 *             FLEE_SHADE_NEAR; -FLEE_OPEN_PENALTY beyond FLEE_SHADE_FAR from every likely anchor
 * `points` defaults to fleeCandidates facing away from the centroid.
 */
export function scoreFleePoints(me, { points = null, tracks = [], from = [], gear = 0, home = null, anchors = null } = {}) {
  const c = fleeCentroid(from, gear);
  const cd = c ? Math.hypot(me.x - c.x, me.z - c.z) : 0;
  // No away line when the bot stands among them (surrounded): the threat term decides.
  const ux = cd > 1 ? (me.x - c.x) / cd : 0, uz = cd > 1 ? (me.z - c.z) / cd : 0;
  const homeD = home?.x !== undefined ? Math.hypot(home.x - me.x, home.z - me.z) : Infinity;
  const likely = (anchors ?? []).filter((a) => a.p >= LIKELY_SHADE);
  const pts = points ?? fleeCandidates(me, { anchors, face: cd > 1 ? Math.atan2(ux, uz) : 0 });
  return pts.map((p) => {
    const threat = threatAt(tracks, p, gear);
    const away = ((p.x - me.x) * ux + (p.z - me.z) * uz) / FLEE_FAR;
    const toHome = homeD < FLEE_HOME_RADIUS ? (homeD - Math.hypot(home.x - p.x, home.z - p.z)) / FLEE_FAR : 0;
    let shade = 0, shadeD = Infinity;
    for (const a of likely) {
      const d = Math.hypot(a.x - p.x, a.z - p.z);
      shadeD = Math.min(shadeD, d);
      shade = Math.max(shade, a.p * Math.max(0, 1 - d / FLEE_SHADE_NEAR));
    }
    if (likely.length && shadeD > FLEE_SHADE_FAR) shade = -FLEE_OPEN_PENALTY;
    const score = -threat + away + FLEE_HOME_W * toHome + shade;
    return { ...p, score, threat, away, home: toHome, shade, shadeD };
  }).sort((a, b) => b.score - a.score);
}

/**
 * By day, between legs: is the sun budget too low for another leg? In sun and burning, past
 * SUN_CHARGE_HARD, or under FLEE_SUN_SLACK_S after the walk to the nearest likely anchor (sunSlack).
 */
export function fleeExposed(sun, anchors) {
  if (!sun?.inSun) return false;
  return !!sun.burning || (sun.charge ?? 0) >= SUN_CHARGE_HARD || (anchors?.length ? sunSlack(sun, anchors) < FLEE_SUN_SLACK_S : false);
}

/** Where a flight ends in shade: the likely anchor (any, if none is likely) best by score less its threat. */
export function fleeShade(anchors, tracks, gear = 0) {
  const likely = (anchors ?? []).filter((a) => a.p >= LIKELY_SHADE);
  return (likely.length ? likely : anchors ?? [])
    .map((a) => ({ ...a, rank: (a.score ?? a.p) - FLEE_SHADE_THREAT_W * threatAt(tracks, a, gear) }))
    .sort((a, b) => b.rank - a.rank)[0] ?? null;
}

/**
 * Run from what the bot cannot beat, and keep running until nothing chases: one 14 m dash left the
 * chaser on the bot and the flight repeated until death (bot-survival-combat-tips.md change 1). Each
 * leg goes to the best point of scoreFleePoints the pathfinder can route (the top FLEE_PATH_CHECKS
 * checked with reachable, a timeout counting as usable: running straight away jammed bots against
 * graveyard walls, live), with Veil of Shadow down it (bot.dash, slot 2, its shared cooldown), until
 * no unit is aggroed on the bot within 25 m (bot_threats onMe) or 20 s pass. By day the sun is read
 * between legs and the flight ends on the best shade anchor when the budget runs low.
 */
async function flee(bot, st, snap, target, home = null) {
  const until = Date.now() + 20000;
  const gear = snap.bag?.gear?.total ?? st.gearTotal ?? 0;
  const tracks = () => st.tracks ?? snap.hostiles ?? [];
  // Fled from: the target (a boss up to 50 m out) and every hostile on the bot or within FLEE_HOSTILE_RADIUS.
  const near = (rows) => rows.filter((h) => h.onMe || (h.distance ?? Infinity) <= FLEE_HOSTILE_RADIUS);
  let me = snap.self, legs = 0, from = [target, ...near(snap.hostiles ?? []).filter((h) => h !== target)];
  while (Date.now() < until) {
    let anchors = null;
    if (st.daytime?.sunDamage) {
      // Anchors around where the bot is now (a leg moves it 20 m); 12, so most points have one near.
      const [v, view] = await Promise.all([bot.vitals().catch(() => null), bot.look({ radius: 35, limit: 250 }).catch(() => null)]);
      me = view?.self ?? me;
      anchors = view ? shadeAnchors(bot, me, view.nearby, st.daytime, homeShade(st, me), { limit: 12 }) : [];
      if (fleeExposed(v?.sun, anchors)) {
        const a = fleeShade(anchors, tracks(), gear);
        const charge = (v.sun.charge ?? 0).toFixed(2);
        if (!a) return { ok: false, interrupted: true, reason: `interrupted: sun (charge ${charge}), no shade in sight`, legs, position: me };
        if (a.walkS * 4.3 > 4) await bot.dash(a);
        const r = await bot.goTo(a, { stop: 0.6, attempts: 1, timeoutMs: a.walkS * 1000 + 2500 });
        await sleep(700); // the sun raycast is time-sliced
        const after = await bot.vitals().catch(() => null);
        const shaded = !!r.arrived && !!after?.sun && !after.sun.inSun;
        if (shaded) { st.lastShade = a; bot.lastShaded = { ...a, at: Date.now() }; } else if (r.arrived) (bot.badShade ??= new Set()).add(`${Math.round(a.x)},${Math.round(a.z)}`);
        bot.log(`   flee: sun charge ${charge} after ${legs} legs: to ${a.caster ?? "anchor"} (p ${a.p.toFixed(2)}, ${a.walkS.toFixed(1)} s), ${shaded ? "shaded" : "not shaded"}`);
        return { ok: false, interrupted: true, reason: "interrupted: sun, flight ended at shade", shaded, legs, position: await bot.self().catch(() => me) };
      }
    }
    const scored = scoreFleePoints(me, { tracks: tracks(), from, gear, home, anchors });
    // The expensive check last, on the best few only (gap analysis s. 4 and 9).
    const pathUntil = Date.now() + FLEE_PATH_MS;
    let dest = null, path = "unchecked";
    for (const p of scored.slice(0, FLEE_PATH_CHECKS)) {
      const left = pathUntil - Date.now();
      if (left <= 0) { dest = p; break; }
      const r = await reachable(bot, p, { budgetMs: left, from: me }).catch(() => ({ ok: null }));
      if (r.ok !== false) { dest = p; path = r.ok ? "routed" : "path timeout"; break; }
    }
    // The best few have no route: the next best, unchecked, still scored away from the threat.
    if (!dest) { dest = scored[FLEE_PATH_CHECKS] ?? scored[0]; path = `top ${FLEE_PATH_CHECKS} unreachable`; }
    const legM = Math.hypot(dest.x - me.x, dest.z - me.z);
    bot.log(`   flee leg ${legs + 1}: ${legM.toFixed(0)} m to (${dest.x.toFixed(0)}, ${dest.z.toFixed(0)})${dest.anchor ? ` at ${dest.anchor.caster ?? "shade"}` : ""}, score ${dest.score.toFixed(2)} (threat ${dest.threat.toFixed(2)}, away ${dest.away.toFixed(2)}, home ${dest.home.toFixed(2)}, shade ${dest.shade.toFixed(2)}), ${path}; from ${from.length}`);
    await bot.dash(dest);
    await bot.goTo(dest, { stop: 1.5, attempts: 1, timeoutMs: Math.min(7000, (legM / 4.3) * 1000 + 1500) });
    legs++;
    me = await bot.self().catch(() => me);
    const rows = (await call(SERVER, "bot_threats", { user: bot.name, radius: 30, limit: 20 }).catch(() => null))?.threats ?? [];
    // The chasers moved with the bot: fold the server rows into the tracks the next leg scores
    // against (entity ids left out: the server's are another space than the view's).
    if (st.tracks) updateTracks(st.tracks, rows.map(({ entity, ...r }) => r), { now: Date.now(), threats: rows });
    const chasers = rows.filter((x) => x.onMe && (x.distance ?? 0) <= 25);
    if (!chasers.length) return { ok: true, legs, position: me };
    from = near(rows);
  }
  return { ok: false, reason: "still chased", legs, position: me };
}

/**
 * Shade heuristic. There is no safe "is this point shaded" query (bot-sun-and-daynight.md section 3), so
 * sense instead of predicting: the server's inSun at the bot's own position is authoritative.
 *  1. Walk back to the last spot that read shaded during the damage window, if it is close.
 *  2. Otherwise try the four sides (1.6 m out) of the nearest trees/structures: one side is in the
 *     shadow whatever the sun's direction. Read inSun after each move (the raycast is time-sliced, so wait).
 *  3. Nothing works: stand still next to the nearest tree and wait; charge decays only in shade, but
 *     walking in the open does not help either. The next tick re-evaluates (night ends the danger).
 */
async function roomHasRoof(castle) {
  const room = castle?.heart ? roomPlan(castle.buildings ?? [], castle.heart) : null;
  if (!room || room.missing.length) return false;
  const cell = room.cells[0];
  const floor = (castle.buildings ?? []).find((b) => /Floor/i.test(b.name ?? "") && Math.abs(b.x - cell.x) < 1 && Math.abs(b.z - cell.z) < 1);
  if (!floor?.entity) return false;
  const r = await call(SERVER, "inspect", { entity: floor.entity });
  const bytes = r?.components?.find((c) => /CastleFloorRoof$/.test(c.component))?.fields?.RoofEntity?.bytes ?? "";
  return /[1-9a-f]/i.test(bytes);
}

/** Home shade for the anchors: a roofed castle room's center and the mist brazier, within 60 m. */
function homeShade(st, me) {
  const b = st.castle?.buildings ?? [];
  const room = st.castle?.heart ? roomPlan(b, st.castle.heart) : null;
  const brazier = b.find((x) => /MistBrazier/i.test(x.name ?? ""));
  const near = (p) => p && me && Math.hypot(p.x - me.x, p.z - me.z) < 60;
  return { room: room && !room.missing.length && st.roomRoofed && near(room.center) ? { ...room.center, y: st.castle.heart.y } : null, brazier: near(brazier) ? brazier : null };
}

async function seekShade(bot, st, snap) {
  const shaded = async () => { await sleep(700); const v = await bot.vitals(); return !v.sun?.inSun; };
  // The scored anchors first (the room, proven spots, the brazier and likely tree sides by score):
  // straight to the best, the next only while the budget allows (bot-game-ai-fundamentals.md s. 4).
  const fresh = await bot.look({ radius: 35, limit: 250 }).catch(() => null);
  const here = fresh?.self ?? snap.self;
  const anchors = fresh && here ? shadeAnchors(bot, here, fresh.nearby, st.daytime, homeShade(st, here), { limit: 4 }) : [];
  for (const a of anchors.slice(0, 3)) {
    const v0 = await bot.vitals().catch(() => null);
    if (a !== anchors[0] && v0?.sun && timeToBurn(v0.sun) < a.walkS + 0.5 && !v0.sun.burning) break; // no budget for another guess
    if (a.walkS * 4.3 > 4) await bot.dash(a);
    const r = await bot.goTo(a, { stop: 0.6, attempts: 1, timeoutMs: a.walkS * 1000 + 2500 });
    if (r.arrived && (await shaded())) { st.lastShade = a; bot.lastShaded = { ...a, at: Date.now() }; return { ok: true, how: `${a.caster ?? "anchor"} (p ${a.p.toFixed(2)}, ${a.walkS.toFixed(1)} s)` }; }
    // Bad only when reached and sunny, or with no way there: a walk cut short says nothing (review).
    if (r.arrived || r.unreachable) (bot.badShade ??= new Set()).add(`${Math.round(a.x)},${Math.round(a.z)}`);
    if (r.interrupted) break;
  }
  // The anchors failed (the room, proven spots and likely sides are all in them): no more guessing
  // spot by spot (29-53 s failed searches, live 2026-09-25); straight into the thickest trees.
  const me = here ?? snap.self;
  // Go where the trees are thickest (the sector of view with the most tall
  // casters), dashing; a forest is shade whatever the sun does. Live, 50 s of probing ended in burns.
  const casters = shadeSpots((await bot.look({ radius: 45, limit: 250 }).catch(() => ({ nearby: [] }))).nearby, null);
  const sectors = new Array(8).fill(0);
  for (const c of casters) sectors[Math.floor(((Math.atan2(c.x - me.x, c.z - me.z) + Math.PI) / (2 * Math.PI)) * 8) % 8]++;
  const best = sectors.indexOf(Math.max(...sectors));
  if (sectors[best] > 0) {
    const a = ((best + 0.5) / 8) * 2 * Math.PI - Math.PI;
    const to = { x: me.x + Math.sin(a) * 20, z: me.z + Math.cos(a) * 20 };
    await bot.dash(to);
    await bot.moveTo(to, { stop: 2, timeoutMs: 6000 });
    if (await shaded()) { st.lastShade = await bot.self(); return { ok: true, how: "into the trees" }; }
  }
  await bot.stop();
  return { ok: false, reason: anchors.length ? "no shaded side found" : "no shadow caster in sight" };
}

// Survival actions deal with the danger that interrupts every other action, so they run without it.
const SURVIVAL = new Set(["recover", "leaveCrypt", "seekShade", "flee", "shelter", "rest"]);

async function act(bot, st, snap, d) {
  bot.survival = SURVIVAL.has(d.action);
  st.resting = d.action === "rest";
  try { return await actOnce(bot, st, snap, d); } finally { bot.survival = false; }
}

async function actOnce(bot, st, snap, d) {
  switch (d.action) {
    case "recover": {
      // Where the bot fell is where its bag is (the death container).
      if (snap.self) st.lostBag = { x: snap.self.x, y: snap.self.y, z: snap.self.z, at: Date.now() };
      // Where it died is dangerous for a while (a bandit camp killed a bot twice on the same walk
      // home, live): trips route around it (routableLeg).
      // Only a death with an attacker near: a sun or starvation death at the castle would otherwise
      // poison prey next to home for 30 min (threat.mjs zones).
      const killed = (snap.hostiles ?? []).some((h) => h.onMe || h.distance <= 10);
      if (killed && snap.self && !(snap.self.z < START_ZONE_Z)) (bot.dangerZones ??= []).push({ x: snap.self.x, z: snap.self.z, at: Date.now() });
      const r = await recover(bot);
      const after = await bot.vitals().catch(() => null);
      // Nothing happened (not dead, still 0 health): starved rather than downed; feed for a while.
      if (after && !after.dead && (after.health ?? 1) <= 0) { st.noRecoverUntil = Date.now() + 90000; return { ...r, ok: false, reason: "still at 0 health: starved, not downed" }; }
      return r;
    }
    case "retrieve": {
      const trip = await travel(bot, d.spot, { stop: 3, runPast: true });
      if (!trip.arrived) return { ok: false, reason: `bag run: ${trip.interrupted ?? trip.reason ?? "not there"}`, trip };
      const drops = (await findDrops(bot, { radius: 12 })).filter((x) => /DeathContainer/i.test(x.name ?? ""));
      if (!drops.length) { st.lostBag = null; return { ok: false, reason: "no death container left at the spot" }; }
      const r = await pickupNearby(bot, { radius: 12, skip: st.skip, name: /DeathContainer/i });
      if (r.ok || (st.lostBag.tries = (st.lostBag.tries ?? 0) + 1) >= 3) st.lostBag = null;
      return r;
    }
    case "leaveCrypt": return leaveCrypt(bot);
    case "seekShade": return seekShade(bot, st, snap);
    case "rest": {
      await bot.stop();
      // Clear of the fight and hurt: a salve now (damage would break it mid-fight), then rest.
      if (frac(snap.vitals) < 0.6 && Date.now() - (st.lastSalve ?? 0) > 20000) {
        const salve = (snap.bag?.occupied ?? []).find((o) => HEALING.test(o.item ?? ""));
        if (salve) { await bot.useSlot(salve.slot); st.lastSalve = Date.now(); }
      }
      // Blood Mend with blood to spare: 5% max health a second for 1 blood (to full in ~20 s, against
      // ~0.3 HP/s resting); moving, damage or a cast ends it. Live 2026-09-25: +6.3 HP (5% of 125) and
      // -1 blood the first second (bot_event shapeshift, internal research notes).
      let mended = false;
      if ((snap.vitals?.blood ?? 0) >= BLOOD_MEND_MIN_BLOOD) {
        await call(bot.client, "bot_event", { kind: "shapeshift", ability: BLOOD_MEND }).then(() => { mended = true; }).catch(() => null);
      }
      await sleep(4000);
      return { ok: true, health: snap.vitals?.health, mended };
    }
    case "shelter": {
      // Already shaded (or night), or the last search found nothing: hold position; the next tick
      // re-plans. Otherwise find shade (searching is rate-limited by its own cooldown).
      const shaded = !snap.vitals?.sun?.inSun && (st.lastShade || snap.daytime?.sunDamage);
      if (shaded || !(st.cooldowns.seekShade <= Date.now() || !st.cooldowns.seekShade)) { await bot.stop(); await sleep(TICK_MS); return { ok: true, how: "holding still" }; }
      const r = await seekShade(bot, st, snap);
      if (!r.ok) st.cooldowns.seekShade = Date.now() + FAIL_COOLDOWN_MS.seekShade;
      return r;
    }
    case "flee": return flee(bot, st, snap, d.target, st.castle?.heart);
    case "stock": {
      const caught = await catchCritter(bot, d.target);
      if (!caught.ok && !caught.interrupted) (st.badPreySpots ??= []).push({ x: d.target.x, z: d.target.z, at: Date.now() });
      if (!caught.ok || !d.eat) return { ...caught, target: d.target };
      return { ...(await eatCarried(bot, { skip: st.inedible })), caught: d.target.name, target: d.target };
    }
    case "feed": {
      const ate = await eatCarried(bot, { skip: st.inedible }); // carried food first: no walk past hostiles
      if (ate.ok) return ate;
      if (ate.item) st.inedible.add(ate.item); // used but no blood: stop retrying it
      if (d.frozen) return { ok: false, reason: "frozen and nothing carried to eat" };
      // At home: the pantry first (food kept in the castle stash).
      const stash = (st.castle?.buildings ?? []).find((b) => /Stash|Chest/i.test(b.name ?? ""));
      if (stash && snap.self && dist(stash, snap.self) < 60 && !((st.pantryEmptyUntil ?? 0) > Date.now())) {
        const got = await pantry(bot, stash, "withdraw");
        const ate2 = got.ok ? await eatCarried(bot, { skip: st.inedible }) : { ok: false };
        if (ate2.ok) return { ...ate2, from: "pantry" };
        st.pantryEmptyUntil = Date.now() + 5 * 60000; // nothing there: hunt instead, look again in 5 min
      }
      // Living prey first: a wounded deer, wolf or bandit fills blood to ~100 (a rat gives 20).
      // A fight, so only while healthy, except harmless prey that flees instead of fighting back (deer,
      // villagers, rabbits): a hurt, starving bot takes those like a player does (live 2026-09-25 bots
      // starved at 1 HP near the Farbane exit with deer around and no rats).
      // Starving (blood under 20) is worse than a fair fight: from 40% health too (live 2026-09-25:
      // hurt, starving bots walked to wolves they were then not allowed to feed on, and starved).
      const healthy = frac(snap.vitals ?? {}) > HUNT_HEALTH || ((snap.vitals?.blood ?? 100) < BLOOD_CRITICAL && frac(snap.vitals ?? {}) > 0.4);
      const lone = (u) => safePreyUnit(u, st.tracks ?? snap.hostiles, snap.bag?.gear?.total ?? 0);
      const livings = (snap.feedable ?? []).filter((u) => (healthy || HARMLESS.test(u.name ?? "")) && lone(u) && usable({ ...st, now: Date.now() }, u)
        && (u.level ?? 0) <= (snap.bag?.gear?.total ?? 0) + LEVEL_FIGHT_MARGIN + (HARMLESS.test(u.name ?? "") ? 6 : 0))
        .sort((a, b) => a.distance - b.distance);
      const living = livings.length ? await firstReachable(bot, st, livings.slice(0, 3)) : null;
      if (living) {
        const fed = await feedOnWounded(bot, living);
        if (fed.ok) recordGround(living, { fed: true, name: living.name });
        return { ...fed, target: living };
      }
      const badSpot = (p) => (st.badPreySpots ?? []).some((b) => Date.now() - b.at < 600000 && Math.hypot(b.x - p.x, b.z - p.z) < 6);
      const prey = await safePrey(bot, { ...(d.relaxed ? { safety: 0, radius: 120 } : {}), skip: st.skip, avoid: badSpot });
      // Starving is certain death (health drains below 10 blood), the sun only a risk: below 20 blood
      // the bot roams by day too, hopping between shade (travel's day hops).
      if (!prey && st.daytime?.sunDamage && (snap.vitals?.blood ?? 100) >= BLOOD_CRITICAL) {
        // By day no roaming across open ground for food (5 of 10 deaths in one window were starving
        // bots burning on food roams, live): shade, and hunt what comes by; roam at dusk.
        const sh = await seekShade(bot, st, snap);
        return { ok: false, reason: `daylight: no prey in sight, waiting in shade (${sh.how ?? sh.reason})` };
      }
      if (!prey) {
        // Nothing in sight: go hunting, as a player does, toward where prey was last seen (deer,
        // villagers, rats remembered for 10 min), else roam 50 m in a fresh direction. Live 2026-09-25 a
        // bot starved at its castle retrying "no prey in sight" every 20 s.
        const me = snap.self;
        // Not back to where a pack, a V Blood or a recent death was (tracks, threat.mjs): a roam for
        // food otherwise returned to the prey spot next to the camp that had just killed the bot.
        const gearNow = st.gearTotal ?? 0;
        const calm = (p) => threatAt(st.tracks ?? [], p, gearNow, { exclude: p }) < PREY_BUDGET;
        const seen = (st.preySeen ?? []).filter((p) => Date.now() - p.at < 600000 && Math.hypot(p.x - me.x, p.z - me.z) > 15 && calm(p) && !blacklisted(st.failures?.blacklist, p, Date.now()))
          .sort((a, b) => Math.hypot(a.x - me.x, a.z - me.z) - Math.hypot(b.x - me.x, b.z - me.z))[0];
        // By day, roam toward the densest trees (shade on the way); by night, a fresh direction.
        let a = (st.roamAngle = (st.roamAngle ?? Math.random() * 2 * Math.PI) + 2.4);
        if (st.daytime?.sunDamage) {
          const casters = shadeSpots((await bot.look({ radius: 60, limit: 250 }).catch(() => ({ nearby: [] }))).nearby, null);
          const sectors = new Array(8).fill(0);
          for (const c of casters) sectors[Math.floor(((Math.atan2(c.x - me.x, c.z - me.z) + Math.PI) / (2 * Math.PI)) * 8) % 8]++;
          const best = sectors.indexOf(Math.max(...sectors));
          if (sectors[best] > 0) a = ((best + 0.5) / 8) * 2 * Math.PI - Math.PI;
        }
        // A fresh direction that does not head into remembered threat (the calmest of eight turns).
        const roamTo = (ang) => ({ x: me.x + Math.sin(ang) * 50, z: me.z + Math.cos(ang) * 50 });
        if (!calm(roamTo(a))) {
          const turns = [...Array(8).keys()].map((k) => a + (k * Math.PI) / 4);
          a = turns.reduce((b, t) => (threatAt(st.tracks ?? [], roamTo(t), gearNow) < threatAt(st.tracks ?? [], roamTo(b), gearNow) ? t : b), a);
        }
        // Else the best remembered hunting ground (grounds.mjs: where prey was seen and fed on, shared
        // by all bots), else a fresh direction.
        const ground = seen && Math.hypot(seen.x - me.x, seen.z - me.z) < 400 ? null : bestGround(me, { visited: (st.groundsVisited ??= {}), ok: calm });
        if (ground) { st.groundsVisited[ground.key] = Date.now(); bot.log(`   hunting ground ${ground.distance.toFixed(0)} m off (${ground.names.join("/")}, weight ${ground.score.toFixed(1)})`); }
        const to = seen && Math.hypot(seen.x - me.x, seen.z - me.z) < 400 ? seen : ground ?? roamTo(a);
        // A trip to find food must not stop for hunger (it ended at once below 15 blood, live).
        const trip = await travel(bot, to, { stop: 6, runPast: true, timeoutMs: 90000, hungerStop: false });
        // Arrived where prey was: look again, wider, and feed at once (the next try came 20 s later
        // and the deer had moved on: 53 hunting trips, no feed, in one hour, live).
        if (trip.arrived) {
          const wide = await bot.look({ radius: 90, limit: 200, kind: "unit" }).catch(() => ({ nearby: [] }));
          const around = wide.nearby ?? [];
          // The wider look goes into the tracks too (nothing is dropped: its coverage is not checked).
          st.tracks = updateTracks(st.tracks ?? newTracks(), around.filter((o) => HOSTILE.test(o.name ?? "") || /VBlood/.test(o.name ?? "")), { now: Date.now() });
          const hostileNear = (u) => !safePreyUnit(u, st.tracks, gearNow);
          const target = around.filter((u) => FEEDABLE.test(u.name ?? "") && !hostileNear(u) && !st.skip.has(u.entity) && !blacklisted(st.failures?.blacklist, u, Date.now())
            && ((u.level ?? 0) <= gearNow + LEVEL_FIGHT_MARGIN || HARMLESS.test(u.name ?? ""))).sort((a, b) => a.distance - b.distance)[0];
          if (target) {
            const fed = await feedOnWounded(bot, target);
            if (fed.ok) recordGround(target, { fed: true, name: target.name });
            return { ...fed, target, hunted: true };
          }
          const rat = await safePrey(bot, { radius: 90, skip: st.skip, avoid: badSpot });
          if (rat) {
            const caught = await catchCritter(bot, rat);
            if (caught.ok) return { ...(await eatCarried(bot, { skip: st.inedible })), caught: rat.name, hunted: true };
            st.skip.add(rat.entity);
          }
        }
        return { ok: false, reason: `no prey in sight: went ${seen ? `toward ${seen.name}` : "roaming"}`, trip: trip.arrived ? "arrived, none there" : trip.interrupted ?? trip.reason };
      }
      // A critter is caught (hover + Interact) and then eaten, as a player does (live: the catch put
      // an Item_Consumable_Eat_Rat in the bag; swinging at rats and the feed cast both failed).
      if (/Critter/i.test(prey.name ?? "")) {
        const caught = await catchCritter(bot, prey);
        if (!caught.ok) {
          // Not this rat again, nor the spot: rats there sit where the bot cannot get (a ledge, a nest
          // behind a wall; live, one spot cost 33 s per rat, rat after rat).
          st.skip.add(prey.entity);
          (st.badPreySpots ??= []).push({ x: prey.x, z: prey.z, at: Date.now() });
          return { ...caught, target: prey };
        }
        const ate = await eatCarried(bot, { skip: st.inedible });
        return { ...ate, caught: prey.name, target: prey };
      }
      return { ...(await feedOn(bot, prey)), target: prey };
    }
    case "pickup": return pickupNearby(bot, { skip: st.skip });
    case "journal": {
      const r = await followJournal(bot, { quests: st.quests, skip: st.journalSkip, targetSkip: st.skip, claimSkip: st.claimSkip, claimFailures: st.claimFailures, craftable: craftableItems(st.recipes, st.castle) });
      st.questsAt = 0; // progress changed: re-read next tick
      return r;
    }
    case "buildHeart": {
      const r = await buildHeart(bot, { cache: st.heartCache });
      // A locked blueprint, not affording it, or a fight on the way is not an attempt; the cooldown
      // paces retries. Repeated interrupts do count, so a trip that never gets through ends.
      if (r.interrupted && ++st.heartInterrupts % MAX_HEART_INTERRUPTS === 0) st.heartAttempts++;
      else if (!r.locked && !r.unaffordable && !r.interrupted) st.heartAttempts++;
      if (r.ok) st.hasHeart = true;
      return r;
    }
    case "goal": {
      const r = await runStep(bot, d.step, { skip: st.skip });
      if (d.step.action === "tend") st.lastTend = Date.now();
      if (d.step.action === "fuel") st.lastFuel = Date.now();
      if (d.step.action === "refine") (st.refineLast ??= {})[d.step.building.name] = Date.now();
      if (d.step.action === "repair") st.daytimeAt = 0; // re-read durability with the clock next tick
      if (d.step.action === "build") {
        // Only a silent placement refusal skips the spot (not a failed trip), and only for a while.
        if (!r.ok && r.refused) st.refusedAt[`${d.step.kind}:${d.step.index}`] = Date.now();
        st.castleAt = 0; // re-read the castle (not the recipe/blueprint lists)
      }
      if (["craft", "equip"].includes(d.step.action) || r.ok) st.questsAt = 0;
      return r;
    }
    case "hunt": {
      // Picked (not engaged) targets are path-checked before the fight commits to one.
      if (d.candidates?.length) {
        const pick = await firstReachable(bot, st, d.candidates);
        if (!pick) return { ok: false, reason: "unreachable", candidates: d.candidates.map((c) => c.name) };
        if (pick !== d.target) bot.log(`   hunt: ${d.target.name} has no route, taking ${pick.name} (${pick.distance} m)`);
        d.target = pick;
      }
      if (st.persona === "trainer") await rotateWeapon(bot, st);
      // Everyone but the trainer (which compares weapons on purpose) takes the best carried weapon.
      const trainer = st.persona === "trainer";
      return fight(bot, d.target, { fleeBelow: d.fleeBelow ?? FLEE_HEALTH, chooseWeapon: !trainer, swap: trainer ? !!st.trainHybrid : true });
    }
    case "gather": return gather(bot, d.name, { skip: st.skip, ...(d.radius ? { radius: d.radius } : {}) });
    default: await sleep(TICK_MS); return { ok: true };
  }
}

/**
 * The first of `candidates` (best first) that the pathfinder can route to, or whose check ran out
 * of time (it says nothing, so the walk's own Progress monitor decides); null if none. Candidates
 * with no route are blacklisted on the spot (a skeleton on a ledge otherwise cost two 25-45 s fights
 * before it was dropped, and only for 60 s: bot-game-ai-fundamentals.md section 1).
 */
async function firstReachable(bot, st, candidates) {
  for (const c of candidates) {
    const reach = await reachable(bot, c).catch(() => ({ ok: null }));
    if (reach.ok !== false) return c;
    blacklist((st.failures.blacklist ??= {}), c);
  }
  return null;
}

/** Book-keeping after an action: cooldowns on failure, per-target and per-subtask failure counts. */
function record(st, snap, d, r) {
  st.last = { action: d.action, ok: !!r?.ok, at: Date.now() };
  if (r?.stats) logFight(st, snap, d, r);
  if (d.action === "gather" || d.action === "hunt") st.gatherTurn = (d.turn ?? st.gatherTurn) + 1;
  if (["goal", "journal", "buildHeart"].includes(d.action)) st.goalTurn++; // rotating personas take turns
  pruneBlacklist(st.failures.blacklist);
  if (r?.ok) return;
  const cool = r?.interrupted ? 5000 : FAIL_COOLDOWN_MS[d.action] ?? 0; // interrupted: resume after the fight
  const key = d.action === "goal" ? `goal:${d.goal}` : d.action;
  if (cool) st.cooldowns[key] = Date.now() + cool;
  // Failures by type: no route or no progress blacklists the target at once (entity and name within
  // 3 m, 5 min); the sun or a threat interrupting says nothing about the target; the rest (fled, lost
  // target, timeout) count toward MAX_TARGET_FAILURES as before.
  const kind = failureKind(r);
  const target = d.target?.entity != null ? d.target : r?.target ?? d.target;
  const entity = target?.entity;
  if (target && (kind === "unreachable" || kind === "stalled")) blacklist((st.failures.blacklist ??= {}), target);
  if (entity && kind !== "interrupted") { st.failures[entity] = (st.failures[entity] ?? 0) + 1; st.failureAt[entity] = Date.now(); }
  if (d.action === "gather" && /no .* within/.test(r?.reason ?? "")) {
    delete st.cooldowns.gather; // only this resource is missing; let the rotation move on
    st.cooldowns[`gather:${d.name}`] = Date.now() + 60000;
  }
  if (d.action === "journal" && r?.subtaskId !== undefined) {
    const n = (st.journalFailures[r.subtaskId] = (st.journalFailures[r.subtaskId] ?? 0) + 1);
    if (n >= MAX_JOURNAL_FAILURES) { st.journalSkip.add(r.subtaskId); st.journalSkipAt[r.subtaskId] = Date.now(); }
  }
}

/**
 * The trainer compares weapons: every TRAIN_FIGHTS_PER_WEAPON fights it wears the next weapon type
 * in its bag (the lab bot was granted one of each bone weapon), so combat.jsonl has fights per weapon.
 */
async function rotateWeapon(bot, st) {
  st.trainFights = (st.trainFights ?? 0) + 1;
  if ((st.trainFights - 1) % TRAIN_FIGHTS_PER_WEAPON !== 0) return;
  const bag = await bot.inventory().catch(() => null);
  // The best tier of each weapon type (the trainer is handed higher tiers; the old bone ones stay in
  // the bag and would otherwise take their turns).
  const tier = (i) => Number(/_T(\d+)/.exec(i ?? "")?.[1] ?? 0);
  const type = (i) => /Item_Weapon_([A-Za-z]+)_/.exec(i ?? "")?.[1];
  const best = new Map();
  const cap = st.weaponTierCap ?? 99; // the ladder rung's weapon tier (weapons above it would out-level the opponents)
  for (const o of (bag?.occupied ?? []).filter((x) => /^Item_Weapon_/.test(x.item ?? "") && tier(x.item) <= cap)) {
    const cur = best.get(type(o.item));
    if (!cur || tier(o.item) > tier(cur.item)) best.set(type(o.item), o);
  }
  const worn = (bag?.equipped ?? []).find((e) => e.slot === "weapon")?.item;
  if (worn && tier(worn) <= cap && best.has(type(worn)) && tier(worn) >= tier(best.get(type(worn)).item)) best.delete(type(worn));
  const weapons = [...best.values()];
  if (!weapons.length) { st.trainHybrid = false; return; }
  // One extra turn in the rotation fights hybrid (crossbow at range, melee up close, swapping).
  const turn = (st.weaponTurn = (st.weaponTurn ?? -1) + 1) % (weapons.length + 1);
  st.trainHybrid = turn === weapons.length;
  if (!st.trainHybrid) await call(SERVER, "equip", { user: bot.name, slot: weapons[turn].slot }).catch(() => null);
}

/** One line per fight in %TEMP%/progenitor-bots/combat.jsonl: what the combat tuning is measured on. */
const vitalsBrief = (snap) => {
  const v = snap.vitals ?? {};
  return { hp: Math.round(v.health ?? 0), maxHp: Math.round(v.maxHealth ?? 0), blood: Math.round((v.blood ?? 0) * 10) / 10,
    sun: v.sun?.inSun ? Math.round((v.sun.charge ?? 0) * 100) / 100 : 0, gear: snap.bag?.gear?.total ?? null,
    at: snap.self ? [Math.round(snap.self.x), Math.round(snap.self.y), Math.round(snap.self.z)] : null,
    hostiles: (snap.hostiles ?? []).slice(0, 4).map((h) => `${(h.name ?? "").replace(/^CHAR_/, "")} L${h.level ?? "?"} ${h.distance}m${h.onMe ? " onMe" : ""}`) };
};

/** One line per death in %TEMP%/progenitor-bots/deaths.jsonl: the last action and state before it. */
function logDeath(st, snap, d) {
  try {
    const dir = path.join(os.tmpdir(), "progenitor-bots");
    fs.mkdirSync(dir, { recursive: true });
    fs.appendFileSync(path.join(dir, "deaths.jsonl"), JSON.stringify({
      at: new Date().toISOString(), bot: st.name, persona: st.persona, state: d.reason, before: st.before ?? null, now: vitalsBrief(snap),
    }) + "\n");
  } catch { /* logging never breaks the brain */ }
}

function logFight(st, snap, d, r) {
  try {
    const dir = path.join(os.tmpdir(), "progenitor-bots");
    fs.mkdirSync(dir, { recursive: true });
    fs.appendFileSync(path.join(dir, "combat.jsonl"), JSON.stringify({
      at: new Date().toISOString(), bot: st.name, persona: st.persona, gear: snap.bag?.gear?.total ?? null,
      result: r.killed ? "won" : r.reason ?? (r.ok ? "ok" : "failed"), swings: r.swings, why: d.reason, ...r.stats,
    }) + "\n");
  } catch { /* logging never breaks the brain */ }
}

const brief = (r) => JSON.stringify(r, (k, v) => (k === "sent" || k === "quests" ? undefined : v))?.slice(0, 300);

/** Run one bot's brain for `minutes`. Never throws: every tick's error is logged and backed off. */
export async function runBrain(bot, { minutes = 20, persona = "balanced", log = (...a) => bot.log(...a) } = {}) {
  const st = newState();
  st.persona = persona;
  st.name = bot.name;
  const until = Date.now() + minutes * 60000;
  log(`brain start for ${minutes} min as ${persona}`);
  while (Date.now() < until) {
    try {
      const snap = await observe(bot, st);
      const d = decide(snap);
      log(`-> ${d.action}: ${d.reason}`);
      if (d.action === "recover" && st.last?.action !== "recover") logDeath(st, snap, d);
      const t0 = Date.now();
      const r = await act(bot, st, snap, d);
      record(st, snap, d, r);
      // What the bot was doing and how it stood, for the death log (deaths.jsonl) next tick.
      st.before = { action: d.action, why: d.reason, result: brief(r)?.slice(0, 160), ...vitalsBrief(snap) };
      if (d.action !== "idle") log(`   ${d.action} ${r?.ok ? "ok" : "failed"} in ${((Date.now() - t0) / 1000).toFixed(1)} s ${brief(r)}`);
      st.consecutiveErrors = 0;
      await sleep(TICK_MS);
    } catch (e) {
      st.consecutiveErrors++;
      const wait = Math.min(ERROR_BACKOFF_MAX_MS, ERROR_BACKOFF_BASE_MS * 2 ** (st.consecutiveErrors - 1));
      log(`tick error (${st.consecutiveErrors}): ${e.message}; backing off ${wait / 1000} s`);
      try { await bot.stop(); } catch { /* bridge may be down */ }
      await sleep(wait);
    }
  }
  try { await bot.drive({ action: "unhover" }); await bot.stop(); } catch { /* ignore */ }
  log("brain done");
}

// ---- CLI ------------------------------------------------------------------------------------------

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const argv = process.argv.slice(2);
  const opt = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 ? argv[i + 1] : d; };
  // --bots 1:HeadlessOne:builder,2:HeadlessTwo:warrior (persona optional, default balanced)
  const bots = opt("bots", "1:HeadlessOne:builder,2:HeadlessTwo:warrior,3:HeadlessThree:balanced")
    .split(",").map((s) => { const [i, n, p] = s.split(":"); const b = new Bot(Number(i), n); b.persona = p ?? "balanced"; b.bloodPrefs = personaOf(b.persona).blood ?? []; return b; });
  const minutes = Number(opt("minutes", "20"));
  const results = await Promise.allSettled(bots.map((b) => runBrain(b, { minutes, persona: b.persona })));
  for (const [i, r] of results.entries()) if (r.status === "rejected") console.error(`${bots[i].name}: ${r.reason?.message ?? r.reason}`);
}
