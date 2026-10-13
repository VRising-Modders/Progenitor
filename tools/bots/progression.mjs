// Layer 4 (continued): progression skills. Loot pickup, crafting, equipping, the first castle heart
// and the journal. Each is an async function over one Bot that either succeeds or reports why not.
//
// These use two ops a teammate is adding in parallel (not verified live yet):
//   client bot_event {kind:"build"|"craft"|"equip"|"claim", ...}: sends the event a player's client
//     would send (BuildTileModelEvent, StartCharacterCraftItemEvent / StartCraftItemEvent,
//     EquipItemEvent, ClaimAchievementEvent). The server validates and may silently refuse, so every
//     skill here confirms the effect by observation (bag, bot_look), never by the send.
//   server bot_quests {user}: the journal (Achievement system) with per-subtask progress.
// Mechanics: internal research notes.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { call } from "./bridge.mjs";
import { SERVER, sleep } from "./bot.mjs";
import { gather, fight, leaveCrypt, inCrypt, dist } from "./skills.mjs";
import { dayHop } from "./shade.mjs";

// Names of creatures that fight back. skills.mjs keeps its copy private; the brain needs it too.
export const HOSTILE = /Skeleton|Ghoul|Bandit|Wolf|Bear|Spider|Zombie|Undead|Militia|Guard|Archer|Knight/i;

export const HEART_PREFAB = -485210554; // TM_BloodFountain_CastleHeart (live, castle-module.md)
const VEGETATION = /Tree|Spruce|Pine|Birch|Oak|Cypress|Bush|Plant|Shrub|Mushroom/i;
// A trip outside the graveyard stops to feed below this: a respawned bot has ~10 blood, walking drains
// 0.02/s, and at 0 health drains too (live 2026-09-25 a bot reached Farbane at 1 HP and blood 0).
const TRAVEL_STARVING_BLOOD = 15;
export const START_ZONE_Z = -2850;       // the starting graveyard/crypt exits lie south of this (live z -2960..-2990)
const SPOT_QUERY_RADIUS = 40;
const RUN_PAST_HEALTH = 0.5;             // a home trip runs past hostiles while health is above this           // meters; covers the footprint box across terrain height differences
const BUILD_CELL = 5;                   // build grid cell = 10 tiles = 5 world units
const PICKUP_RADIUS = 15;

const event = (bot, args) => call(bot.client, "bot_event", args);

// ---- pure helpers (unit-tested) -----------------------------------------------------------------

/** What the bag gained between two bot_inventory reads: { itemName: +amount }. */
export function inventoryGain(before, after) {
  const gained = {};
  for (const it of after?.items ?? []) {
    const prev = (before?.items ?? []).find((b) => b.guid === it.guid)?.amount ?? 0;
    if (it.amount > prev) gained[it.item ?? it.guid] = it.amount - prev;
  }
  return gained;
}

/** Every item whose amount changed, either way (crafting consumes inputs and adds outputs). */
export function inventoryDelta(before, after) {
  const delta = {};
  const keys = new Map();
  for (const it of [...(before?.items ?? []), ...(after?.items ?? [])]) keys.set(it.guid, it.item ?? it.guid);
  for (const [guid, name] of keys) {
    const a = (after?.items ?? []).find((i) => i.guid === guid)?.amount ?? 0;
    const b = (before?.items ?? []).find((i) => i.guid === guid)?.amount ?? 0;
    if (a !== b) delta[name] = a - b;
  }
  return delta;
}

/**
 * Center of the 5-unit build cell containing (x, z): world X/Z = 2.5 (mod 5), where the live heart
 * and floor both sit (bot-building.md section 3).
 */
export function snapToCell(v) { return Math.floor(v / BUILD_CELL) * BUILD_CELL + BUILD_CELL / 2; }

// AchievementProgressType (bot-progression.md section 3.1). bot_quests may send the name or the number.
const PROGRESS_TYPES = ["PossessItem", "Kill", "Craft", "Hit", "Spawn", "ConsumeVBlood", "ObtainItem", "UnlockResearch",
  "EnterCompleteRoom", "EquipmentLevel", "InfuseVBlood", "StoneCoffinBound", "InteractWithTier2BloodFountain", "UseVBloodMenu",
  "InteractWithTier4BloodFountain", "InteractWithTier5BloodFountain", "LeftFarbane", "OpenedBuildMenu", "UnlockNewSpell",
  "InteractWithTier3BloodFountain", "OpenedMap", "EnteredTerritory", "OpenedCharacterMenu", "EnterCompleteRoomTreasury"];
export const progressTypeName = (t) => (typeof t === "number" ? PROGRESS_TYPES[t] ?? String(t) : String(t ?? ""));

// Where a player finds an item: resource-node / breakable / unit name fragments for bot_look. The
// first entry is tried first. Guesses from prefab naming, not a drop-table read: bones are expected
// from skeletons and crypt urns/remains (bot-progression.md section 4.2, inferred).
export const ITEM_SOURCES = [
  { item: /Bone/i, gather: ["Urn", "Bone", "Remains"], hunt: ["Skeleton"] },
  { item: /Lumber|Wood|Plank/i, gather: ["Tree"] },
  { item: /Stone|Rock/i, gather: ["Rock"] },
  { item: /Copper/i, gather: ["Copper"] },
  { item: /PlantFiber|Fiber|Cotton/i, gather: ["Fiber", "Cotton"] },
  // Animal hides (bone armor needs Item_Ingredient_RuggedHide: live bot_recipes 2026-09-24).
  { item: /Hide|Leather|Pelt/i, hunt: ["Wolf", "Deer", "Boar", "Bear", "Moose"] },
  { item: /BloodEssence/i, hunt: ["Critter", "Wolf", "Deer", "Bandit"] },
  { item: /Consumable_Eat_Rat/i, hunt: ["Critter_Rat"] }, // killed rats drop the rat item
];

// Journal "Interact_X" -> a name fragment of the castle building to use.
const INTERACT_TARGETS = { BloodFountain: "CastleHeart", SimpleWorkbench: "SimpleCraftingBench", Throne: "Throne" };

/** Plan for one journal subtask: {action, ...}. "none" means a bot cannot (or need not) do it. */
export function planSubtask(sub) {
  const type = progressTypeName(sub?.type);
  const text = `${sub?.name ?? ""}`;
  const item = sub?.trackedItem?.name ?? "";
  if (/crypt|Farbane/i.test(text) || type === "LeftFarbane") return { action: "leaveCrypt" };
  // Menu subtasks count the empty event the client sends when the menu opens (live 2026-09-24:
  // OpenMap, OpenBuildMenu and OpenInventory each went to 1/1 on one bot_event ui).
  const menu = { OpenedMap: "map", OpenedBuildMenu: "build", OpenedCharacterMenu: "character", UseVBloodMenu: "vblood" }[type];
  if (menu) return { action: "ui", menu };
  // "Interact with X": hover + Interact on the bot's own structure (castle building name fragment).
  const interact = /Interact_(BloodFountain|SimpleWorkbench|Sawmill|BloodPress|ServantCoffin|ResearchDesk|Throne|Waypoint|StoneCoffin)/.exec(text);
  if (interact) return { action: "interact", target: INTERACT_TARGETS[interact[1]] ?? interact[1] };
  // Spawn_* subtasks are built by the castle goal (goals.mjs), not here.
  if (/Spawn_/.test(text) && !/Exit_/.test(text)) return { action: "none", why: "built by the castle goal" };
  if (type === "Kill") return item ? { action: "hunt", names: [item] } : { action: "none", why: "kill target unknown" };
  // Craft: the recipe prefab is the item name with Item_ -> Recipe_ (live: Item_Weapon_Sword_T01_Bone
  // is made by Recipe_Weapon_Sword_T01_Bone).
  if (type === "Craft" && /^Item_/.test(item)) return { action: "craft", recipe: item.replace(/^Item_/, "Recipe_"), item };
  if (["PossessItem", "ObtainItem"].includes(type) || (item && !type)) {
    const src = ITEM_SOURCES.find((s) => s.item.test(item) || s.item.test(text));
    if (!src) return { action: "none", why: `no known source for ${item || text}` };
    return src.gather ? { action: "gather", names: src.gather, hunt: src.hunt ?? [] } : { action: "hunt", names: src.hunt };
  }
  // Menu-opening, building, research, V Blood: not something the bot's ops can do yet.
  return { action: "none", why: `unsupported subtask type ${type || "?"}` };
}

// A subtask whose required count is unknown (bot_quests reports 0 when the subtask prefab could not be
// read) is NOT done: treating it as done made the achievement claimable forever.
const subtaskDone = (s) => (s.required ?? 0) > 0 && (s.progress ?? 0) >= s.required;
// bot_quests state strings are not fixed yet: accept the obvious spellings, whole-word only so
// "Unclaimed"/"Incomplete" never match.
const isClaimed = (a) => /^claimed$/i.test(a.state ?? "");
const isClaimable = (a) => /^(complete|completed|claimable|done)$/i.test(a.state ?? "");

/**
 * From a bot_quests reply: achievements to claim (every subtask done, not yet claimed) and the
 * first incomplete subtask with its plan. Unsupported subtasks are skipped so one menu-only step
 * does not stall the journal; `skip` holds subtask GUIDs that failed too often.
 */
export function planJournal(quests, skip = new Set(), claimSkip = new Set(), { craftable = null } = {}) {
  const claims = [];
  let next = null;
  const huntReady = (quests?.achievements ?? []).some((a) => /GettingReadyForTheHunt/.test(a.achievement ?? a.name ?? "") && isClaimed(a));
  for (const a of quests?.achievements ?? []) {
    if (isClaimed(a) || claimSkip.has(a.achievement)) continue;
    const subs = a.subtasks ?? [];
    if (isClaimable(a) || (subs.length && subs.every(subtaskDone))) { claims.push(a); continue; }
    if (next) continue;
    for (const s of subs) {
      if (subtaskDone(s) || skip.has(s.subtask)) continue;
      const plan = planSubtask(s);
      // A craft whose recipe is not learned yet waits for the entry that unlocks it (`craftable`:
      // output items of the bot's unlocked recipes, when known).
      if (plan.action === "craft" && craftable && !craftable.has(plan.item)) continue;
      // The V Blood menu only counts once it is unlocked, by claiming Journal_GettingReadyForTheHunt
      // (sawmill, workbench, gear 20); before that the server drops the event silently
      // (internal research notes; 75+ wasted sends live).
      if (plan.action === "ui" && plan.menu === "vblood" && !huntReady) continue;
      if (plan.action !== "none") { next = { achievement: a, subtask: s, plan }; break; }
    }
  }
  return { claims, next };
}

// ---- skills -------------------------------------------------------------------------------------

/**
 * Loot drops near the bot. A drop is an ItemPickup world entity that reaches the bag only through an
 * Interact on it (bot-progression.md section 4.1, inferred; walking over it does nothing). bot_look lists
 * only entities with Health, EntityCategory or SpawnPhysicsObjectOnDeath, which drops may lack, so
 * this also asks the client's own world by component (generic `query` op, what the client already knows).
 */
export async function findDrops(bot, { radius = PICKUP_RADIUS } = {}) {
  const view = await bot.look({ radius, limit: 20, name: "Item" });
  const drops = (view.nearby ?? []).filter((n) => n.kind !== "unit" && n.kind !== "player");
  const me = view.self;
  if (me) {
    try {
      const q = await call(bot.client, "query", {
        all: ["ProjectM.ItemPickup"], includeDisabled: false, limit: 20,
        near: { x: me.x, y: me.y, z: me.z, radius },
      });
      for (const r of q.entities ?? []) {
        if (!r.position || drops.some((d) => d.entity === r.entity)) continue;
        drops.push({ entity: r.entity, name: r.prefab?.name, kind: "drop", ...r.position, distance: dist(me, r.position) });
      }
    } catch { /* the component query is a best-effort second source */ }
  }
  return drops.sort((a, b) => a.distance - b.distance);
}

/** Walk to the nearest drop, hover it, hold Interact ~1 s, and confirm the bag grew. */
export async function pickupNearby(bot, { radius = PICKUP_RADIUS, skip = new Set(), name = null } = {}) {
  const target = (await findDrops(bot, { radius })).find((d) => !skip.has(d.entity) && (!name || name.test(d.name ?? "")));
  if (!target) return { ok: false, reason: `no drop within ${radius} m` };
  const before = await bot.inventory();
  // Timed by the distance (a 7 m emerald took 30 s of path retries in the sun and the bot burned,
  // live 2026-09-25); unreached, it is skipped.
  const walk = await bot.goTo(target, { stop: 1.0, timeoutMs: Math.max(6000, (target.distance ?? 10) * 600 + 4000) });
  if (walk.interrupted) return { ok: false, interrupted: walk.interrupted, target };
  if (!walk.arrived) { skip.add(target.entity); return { ok: false, reason: "could not reach", target }; }
  await bot.aim(target);
  await bot.hover(target.entity);
  await bot.drive({ action: "hold", buttons: "Interact", seconds: 1 });
  await sleep(1500);
  // A death container (the bag a bot dropped when it died) only opens on Interact: take everything,
  // as a player's "take all" does (MoveAllItemsBetweenInventoriesEventV2 with no item filter).
  if (/DeathContainer/i.test(target.name ?? "")) {
    await event(bot, { kind: "moveall", from: target.entity, to: "self" }).catch((e) => bot.log(`loot: ${e.message}`));
    await sleep(1200);
  }
  await bot.drive({ action: "unhover" });
  const gained = inventoryGain(before, await bot.inventory());
  const ok = Object.keys(gained).length > 0;
  if (!ok) skip.add(target.entity);
  return { ok, target, gained, ...(ok ? {} : { reason: "bag unchanged after Interact" }) };
}

/**
 * Craft `recipe` (GUID or name) in hand, or at `station` (entity "index:version"). The server checks
 * unlock and costs and refuses silently, so success = the bag changed within `waitMs` (craft time is
 * RecipeData.CraftDuration, unknown here).
 */
export async function craft(bot, recipe, station, { waitMs = 15000 } = {}) {
  const before = await bot.inventory();
  const sent = await event(bot, { kind: "craft", recipe, ...(station ? { station } : {}) });
  const until = Date.now() + waitMs;
  let delta = {};
  while (Date.now() < until) {
    await sleep(1000);
    delta = inventoryDelta(before, await bot.inventory());
    if (Object.values(delta).some((n) => n > 0)) return { ok: true, recipe, delta, sent };
  }
  return { ok: false, reason: Object.keys(delta).length ? "inputs changed but no output yet" : "bag unchanged (locked, missing costs or no station?)", delta, sent };
}

const WEAPON = /Item_Weapon_/i;

/**
 * Equip a weapon from the bag. EquipItemEvent takes a bag slot index, but bot_inventory reports totals
 * rather than slots, so without `slot` this tries slots 0.. in order until the weapon count in the
 * bag drops (the equipped item leaves the bag). Other gear equipped on the way is harmless.
 */
/**
 * Equip the first weapon in the bag. The client-sent EquipItemEvent reached the server but never
 * equipped (live 2026-09-24, every slot 0-14); the server-side injection Core's Items.Equip uses (Dev
 * `equip` op) equips it, and the character's slot 0 then casts the weapon's attack group. The bag
 * slot comes from bot_inventory's `occupied` rows.
 */
export async function equipBest(bot) {
  const bag = await bot.inventory();
  const weapon = (bag.occupied ?? []).find((o) => WEAPON.test(o.item ?? ""));
  if (!weapon) return { ok: false, reason: "no weapon in the bag" };
  const r = await call(SERVER, "equip", { user: bot.name, slot: weapon.slot });
  return { ok: !!r?.equipped, slot: weapon.slot, item: weapon.item, result: r };
}

/**
 * First leg toward dest with a pathfinder route: straight, then fanning out to 90 degrees, then at
 * half length. Only the straight full-length probe can end at dest itself (`final`).
 */
/**
 * Leave the starting graveyard as a player does (internal research notes): it has no
 * walking exit. Walk to the TM_SelectStartGraveyardGate in the north archway (within the server's
 * 15 m; the spot 13 m in front of it on the y 5 terrace), Interact on it (the map opens), and pick the
 * Farbane exit nearest the destination (server op bot_exit_graveyard sends the map's
 * TeleportToWaypointEvent as this player). Verified by the bot's new position.
 */
export async function leaveGraveyard(bot, dest) {
  const ask = () => call(SERVER, "bot_exit_graveyard", { user: bot.name, x: dest.x, z: dest.z });
  let r = await ask();
  if (!r.sent) {
    const g = r.gate;
    if (!g) return { ok: false, reason: r.reason ?? "no gate" };
    const spots = [{ x: g.x, y: 5, z: g.z - 13 }, { x: g.x, y: 5, z: g.z - 6 }, { x: g.x - 6, y: 5, z: g.z - 11 }, { x: g.x + 6, y: 5, z: g.z - 11 }];
    for (const spot of spots) {
      const walk = await bot.goTo(spot, { stop: 1.5, attempts: 2, timeoutMs: 45000 });
      if (walk.interrupted) return { ok: false, interrupted: walk.interrupted };
      r = await ask();
      if (r.sent || !r.gate) break;
    }
    if (!r.sent) return { ok: false, reason: `could not get within reach of the graveyard gate: ${r.reason}` };
  }
  const before = await bot.self();
  // The player's Interact on the gate (opens the map) happens before the pick; the server does not
  // require it (native handler), the event above is the pick. Wait for the move.
  let after = before;
  for (let i = 0; i < 16; i++) {
    await sleep(500);
    after = await bot.self();
    if (Math.hypot(after.x - before.x, after.z - before.z) > 100) break;
  }
  const moved = Math.hypot(after.x - before.x, after.z - before.z) > 100;
  return { ok: moved, exit: r.exit, position: after, ...(moved ? {} : { reason: "sent, but the bot did not move" }) };
}

/**
 * Open a door that blocks the way, as a player does: walk up, Interact; if the route is still shut,
 * break it (fence gates carry Health). A closed door is a wall to the pathfinder (live 2026-09-25: the
 * Farbane exit lands in a walled graveyard whose only way out is TM_IronFenceGate_01_Door). Each door is
 * tried once per trip (`tried`). Returns the door handled, or null when none is close.
 */
export async function openNearbyDoor(bot, dest, tried = new Set()) {
  const view = await bot.look({ radius: 30, limit: 200 });
  const me = view.self;
  if (!me) return null;
  // Doors toward the destination first.
  // Only a door ahead (within 60 degrees of the destination's bearing) and close: one behind the bot
  // or in a house off the route is not in the way.
  const bearing = Math.atan2(dest.x - me.x, dest.z - me.z);
  const ahead = (n) => { const a = Math.atan2(n.x - me.x, n.z - me.z) - bearing; return Math.abs(Math.atan2(Math.sin(a), Math.cos(a))) <= Math.PI / 3; };
  const door = (view.nearby ?? []).filter((n) => /Door|FenceGate/i.test(n.name ?? "") && !tried.has(n.entity) && n.distance <= 15 && ahead(n))
    .sort((a, b) => a.distance - b.distance)[0];
  if (!door) return null;
  tried.add(door.entity);
  const walk = await bot.goTo(door, { stop: 1.5, attempts: 2, timeoutMs: 20000 });
  await bot.stop();
  const near = await bot.self();
  if (!walk.arrived && Math.hypot(near.x - door.x, near.z - door.z) > 2.5) return { door: door.name, opened: false, reason: "could not reach it" };
  await bot.aim(door);
  await bot.hover(door.entity);
  await bot.drive({ action: "hold", buttons: "Interact", seconds: 0.6 });
  await sleep(1200);
  await bot.drive({ action: "unhover" });
  const at = await bot.self();
  // A point 3 m past the door, away from the bot.
  const len = Math.max(0.5, Math.hypot(door.x - at.x, door.z - at.z));
  const through = { x: door.x + ((door.x - at.x) / len) * 3, y: door.y, z: door.z + ((door.z - at.z) / len) * 3 };
  let open = !!(await bot.path({ x: at.x, y: at.y, z: at.z }, through, LEG_EXPANSIONS).catch(() => null));
  if (!open) { // still shut: break it
    for (let i = 0; i < 12 && !open; i++) {
      await bot.aim(door);
      await bot.castSlot(0);
      await sleep(600);
      if (i % 4 === 3) open = !(await bot.look({ radius: 6, limit: 30 })).nearby?.some((n) => n.entity === door.entity);
    }
  }
  return { door: door.name, opened: open };
}

/** In a starting-graveyard copy (south of the Farbane exits; each player's copy sits near z -2860..-2990). */
export const inStartGraveyard = (me) => !!me && me.z < -2840 && me.z > -3000 && me.x > -1000 && me.x < 300;

const LEG_EXPANSIONS = 150000;
const DANGER_RADIUS = 35, DANGER_MS = 30 * 60000;
/** A point within DANGER_RADIUS of a recent death, unless the destination itself is there. */
function inDanger(bot, p, dest) {
  return (bot.dangerZones ?? []).some((z) => Date.now() - z.at < DANGER_MS && Math.hypot(z.x - p.x, z.z - p.z) < DANGER_RADIUS
    && Math.hypot(z.x - dest.x, z.z - dest.z) > DANGER_RADIUS);
}
const LEG_SEARCH_MS = 15000;
const NO_PROGRESS_MS = 60000;
export async function routableLeg(bot, me, dest, leg = 50, { budgetMs = LEG_SEARCH_MS } = {}) {
  // Capped: 14 directions x 3 end points at up to 10.5 s each kept a bot inside one trip for 20 min
  // with no route (live 2026-09-25), and the brain never got a turn.
  const deadline = Date.now() + budgetMs;
  const d = Math.hypot(dest.x - me.x, dest.z - me.z);
  const bearing = Math.atan2(dest.x - me.x, dest.z - me.z);
  const first = Math.min(leg, d);
  for (const len of [first, first / 2]) {
    for (const off of [0, 30, -30, 60, -60, 90, -90]) {
      if (off !== 0 && len >= d) continue; // the final leg goes to the destination itself
      const a = bearing + (off * Math.PI) / 180;
      const t = { x: me.x + Math.sin(a) * len, y: me.y, z: me.z + Math.cos(a) * len };
      // A leg end on a blocked tile (a gravestone, a fence) fails at once (live 2026-09-25, ~0.3 s), so
      // two points 3 m to either side are tried too. The graveyard clutter needs a large search
      // budget: 8000 expansions failed 40 m legs that 300000 solved in 0.3 s.
      for (const side of [0, 3, -3]) {
        const p = side ? { x: t.x + Math.cos(a) * side, y: t.y, z: t.z - Math.sin(a) * side } : t;
        if (inDanger(bot, p, dest)) continue; // around a place where the bot died lately
        if (Date.now() > deadline) return null;
        const path = await bot.path({ x: me.x, y: me.y, z: me.z }, p, LEG_EXPANSIONS, deadline);
        if (path) return { path, off, len, final: off === 0 && len >= d && !side };
      }
    }
  }
  return null;
}

/**
 * Cross a long distance in ~50 m legs, each routed by the pathfinder (with the ledge and exploration
 * fallbacks of goTo). One path request over a kilometer exceeds the solver's search budget. Between
 * legs the trip yields to the brain (`interrupted`) for a close hostile, sun or hunger, so fighting,
 * shade and feeding keep their priority.
 */
// ---- remembered routes ---------------------------------------------------------------------------
// A player remembers the way after walking it once. The pathfinder kept routing bots from the Farbane
// exit into a small fenced grave plot (live 2026-09-25: both bots stalled at the same spot every trip,
// minutes each), though each had walked home from there before. Every trip that arrives saves the path
// it walked (a point every 10 m, the part after any teleport), and later trips starting within 40 m
// of a saved path's start, toward a destination within 60 m of its end, walk its points.
const LEARNED = path.join(os.tmpdir(), "progenitor-bots", "routes-learned.json");
const loadLearned = () => { try { return JSON.parse(fs.readFileSync(LEARNED, "utf8")); } catch { return []; } };
function saveLearned(points, dest) {
  // Keep the part after the last jump (the graveyard gate teleports), and only real trips.
  let from = 0;
  for (let i = 1; i < points.length; i++) if (Math.hypot(points[i].x - points[i - 1].x, points[i].z - points[i - 1].z) > 60) from = i;
  const walk = points.slice(from);
  const length = walk.reduce((n, q, i) => (i ? n + Math.hypot(q.x - walk[i - 1].x, q.z - walk[i - 1].z) : 0), 0);
  if (walk.length < 3 || length < 80) return;
  const all = loadLearned().filter((r) => !(Math.hypot(r.points[0].x - walk[0].x, r.points[0].z - walk[0].z) < 40 && Math.hypot(r.dest.x - dest.x, r.dest.z - dest.z) < 60));
  all.push({ at: new Date().toISOString(), dest: { x: dest.x, z: dest.z }, points: walk.map((q) => ({ x: Math.round(q.x * 10) / 10, y: q.y, z: Math.round(q.z * 10) / 10 })) });
  try { fs.mkdirSync(path.dirname(LEARNED), { recursive: true }); fs.writeFileSync(LEARNED, JSON.stringify(all.slice(-40))); } catch { /* best effort */ }
}
export const SHORT_TRIP_M = 30;
export function learnedRoute(from, dest) {
  // Joined anywhere along it, not only at its start: routes are shared by all bots, and the trainer
  // leaving its arena (which the graveyard-to-castle route passes) sat 7 min "enclosed" on its own
  // way to the Alpha Wolf, next to that castle (live 2026-09-25). The nearest joining point wins, and
  // it must be closer to the start than the destination is (a route that only passes behind the bot
  // would lead it back).
  const d = (a, b) => Math.hypot(a.x - b.x, a.z - b.z);
  if (d(from, dest) < 60) return null; // short trips walk (a 5 m bag run followed a castle route)
  let best = null;
  for (const r of loadLearned()) {
    if (d(r.dest, dest) >= 60) continue;
    const join = Math.min(...r.points.map((q) => d(q, from)));
    if (join < 40 && join < d(from, dest) && (!best || join < best.join)) best = { r, join };
  }
  return best?.r ?? null;
}

/**
 * Cross a long distance: out of the starting graveyard through its gate, then along a remembered
 * route when one starts here, else by routed legs (travelLegs). A trip that arrives is remembered.
 */
export async function travel(bot, dest, opts = {}) {
  let start = await bot.self();
  if (inStartGraveyard(start) && !inStartGraveyard(dest)) {
    const out = await leaveGraveyard(bot, dest);
    if (!out.ok) return { arrived: false, interrupted: out.interrupted, reason: out.reason ?? "graveyard exit failed" };
    start = await bot.self();
  }
  // A short trip is a walk (a dash, then straight there): a 5 m bag run took a learned route and
  // hopped between sunny walls until the bot burned, twice (live 2026-09-25).
  const span = Math.hypot(dest.x - start.x, dest.z - start.z);
  if (span <= SHORT_TRIP_M) {
    if (span > 4) await bot.dash(dest);
    // By day the walk takes the hop budget (danger() to 0.7), not the 0.3 hand-back (review).
    const was = bot.dayWalking;
    bot.dayWalking = !!bot.daytime?.sunDamage;
    let r;
    try { r = await bot.goTo(dest, { stop: opts.stop ?? 2, attempts: 2, timeoutMs: 8000 }); } finally { bot.dayWalking = was; }
    if (r.arrived || r.interrupted) return { ...r, arrived: !!r.arrived };
  }
  const crumbs = [start];
  const known = opts.learned === false ? null : learnedRoute(start, dest);
  if (known) {
    // Resume at the remembered point nearest the bot, then walk the rest point by point.
    let i = known.points.reduce((best, q, k) => (Math.hypot(q.x - start.x, q.z - start.z) < Math.hypot(known.points[best].x - start.x, known.points[best].z - start.z) ? k : best), 0);
    bot.log?.(`travel: following a remembered route (${known.points.length - i} points)`);
    for (; i < known.points.length; i++) {
      const r = await travelLegs(bot, known.points[i], { ...opts, stop: 5, crumbs });
      if (!r.arrived) return r;
    }
  }
  const r = await travelLegs(bot, dest, { ...opts, crumbs });
  if (r.arrived) saveLearned([...crumbs, r.position ?? (await bot.self())], dest);
  return r;
}

export async function travelLegs(bot, dest, { stop = 2, leg = 50, timeoutMs = 20 * 60 * 1000, threatRadius = 3.5, runPast = false, hungerStop = true, crumbs = null } = {}) {
  const until = Date.now() + timeoutMs;
  let stalls = 0, stuckLegs = 0, lastDash = 0, hops = 0;
  const doorsTried = new Set();
  let bestLeft = Infinity, flatHops = 0, dayStalls = 0;
  const watch = { best: Infinity, at: Date.now(), step: 0 };
  const passed = {}; // hostiles run past, by name: where trips go wrong
  const start = await bot.self();
  // The starting graveyard has no walking exit: the gate teleports to Farbane (every trip out of it,
  // and after every death until a coffin is bound).
  if (inStartGraveyard(start) && !inStartGraveyard(dest)) {
    const out = await leaveGraveyard(bot, dest);
    if (!out.ok) return { arrived: false, interrupted: out.interrupted, reason: out.reason ?? "graveyard exit failed" };
  }
  // Run past only what the bot cannot take (level above gear + 2); a fair unit ends the trip so the
  // brain fights it, as a player clears the level 1-4 graveyard undead instead of dragging them along.
  const gear = (await bot.inventory().catch(() => null))?.gear?.total ?? 0;
  const tooStrong = (u) => (u.level ?? 0) > gear + 2;
  while (Date.now() < until) {
    // Live 2026-09-24 a withered skeleton downed a bot snagged on gravestones mid-trip. The radius
    // matches the brain's fight-back range, so an interrupt always leads to a fight or a flight.
    const view = await bot.look({ radius: threatRadius, limit: 20, kind: "unit" });
    if (crumbs && view.self && Math.hypot(view.self.x - crumbs[crumbs.length - 1].x, view.self.z - crumbs[crumbs.length - 1].z) > 10) crumbs.push({ x: view.self.x, y: view.self.y, z: view.self.z });
    let threat = view.nearby?.find((n) => HOSTILE.test(n.name ?? "") && (n.health ?? 1) > 0);
    // Much stronger units (level above gear + 3) are spotted at 20 m, not when they are on the bot: a
    // gear-10 bot walked into three level-20 armored skeletons on a trip (live 2026-09-25). The brain
    // then steers around them (its avoid rule).
    if (!threat && !(runPast && bot.dayWalking)) {
      const wide = (await bot.look({ radius: 20, limit: 40, kind: "unit" }).catch(() => null))?.nearby ?? [];
      const brute = wide.find((n) => HOSTILE.test(n.name ?? "") && (n.health ?? 1) > 0 && (n.level ?? 0) > gear + 3);
      if (brute) {
        (bot.dangerZones ??= []).push({ x: brute.x, z: brute.z, at: Date.now() }); // legs route around it
        await bot.stop(); return { arrived: false, interrupted: `strong ${brute.name} (level ${brute.level}) ahead`, threat: brute };
      }
    }
    // A unit aggroed on the bot within shooting range counts too: crossbow skeletons shot a bot to
    // death on its way home with nothing within 3.5 m (live 2026-09-25).
    if (!threat) {
      const aggro = await call(SERVER, "bot_threats", { user: bot.name, radius: 15, limit: 10 }).catch(() => null);
      const shooter = (aggro?.threats ?? []).find((x) => x.onMe && HOSTILE.test(x.name ?? ""));
      // bot_threats names server entities; hand the brain the client unit (bot_look) at that spot.
      if (shooter) threat = (await bot.look({ radius: 20, limit: 30, kind: "unit" })).nearby
        ?.find((n) => n.name === shooter.name && Math.hypot(n.x - shooter.x, n.z - shooter.z) < 2) ?? null;
    }
    const v = await bot.vitals();
    // Running home a player runs past mobs, dashing through one that blocks the way, and turns to
    // fight only when hurt (live: every home trip out of the graveyard stopped at the first skeleton
    // and ended in a fight, a death and a graveyard respawn).
    const healthy = (v.health ?? 0) > (v.maxHealth ?? 100) * RUN_PAST_HEALTH;
    if (threat) passed[threat.name] = (passed[threat.name] ?? 0) + 1;
    if (threat && runPast && healthy && tooStrong(threat)) {
      // Keep moving: standing still to plan a route with a ghoul on it killed a bot 5 m from its
      // start (live). Dash, then walk toward the destination along a short routed path (walking
      // straight at it pinned bots against the graveyard fences, live 2026-09-25).
      const me0 = view.self ?? {};
      const b = Math.atan2(dest.x - (me0.x ?? 0), dest.z - (me0.z ?? 0));
      const ahead = { x: (me0.x ?? 0) + Math.sin(b) * 15, y: me0.y ?? 0, z: (me0.z ?? 0) + Math.cos(b) * 15 };
      const p = await bot.path({ x: me0.x, y: me0.y, z: me0.z }, ahead, 3000, Date.now() + 1500).catch(() => null); // brief: a mob is on the bot
      const via = Array.isArray(p) && p.length > 1 ? p[Math.min(2, p.length - 1)] : ahead;
      const a = Math.atan2((via.x ?? ahead.x) - (me0.x ?? 0), (via.z ?? ahead.z) - (me0.z ?? 0));
      if (Date.now() - lastDash > 8000) {
        await bot.aim({ x: (view.self?.x ?? 0) + Math.sin(a) * 6, y: view.self?.y ?? 0, z: (view.self?.z ?? 0) + Math.cos(a) * 6 });
        await bot.castSlot(2); // Veil of Shadow
        lastDash = Date.now();
      }
      await bot.drive({ action: "walk", dx: Math.sin(a), dz: Math.cos(a), seconds: 2.5 });
      await sleep(2300);
      continue;
    } else if (threat) { await bot.stop(); return { arrived: false, interrupted: `hostile ${threat.name}`, threat }; }
    if (v.dead || (v.health ?? 1) <= 0) {
      const at = view.self ?? {};
      return { arrived: false, reason: "died on the way", at: { x: Math.round(at.x), z: Math.round(at.z) }, from: { x: Math.round(start.x), z: Math.round(start.z) }, passed };
    }
    // A daylight trip burned a bot to death at the plot (live), and death drops the bag.
    // Progress watchdog, day and night: legs that route inside an enclosure (a walled yard whose way out
    // is a shut door or a ledge drop) move the bot without bringing it closer (live 2026-09-25: 13 min
    // circling the Farbane exit yard). No 5 m gained in a minute escalates: open a door, then jump down
    // a ledge toward the goal (ring search with CliffJump held), then give the trip back to the brain.
    {
      const here = view.self ?? {};
      const left = Math.hypot(dest.x - (here.x ?? 0), dest.z - (here.z ?? 0));
      if (left < watch.best - 5) { watch.best = left; watch.at = Date.now(); watch.step = 0; }
      else if (Date.now() - watch.at > NO_PROGRESS_MS) {
        watch.at = Date.now();
        watch.step++;
        if (watch.step === 1) {
          const door = await openNearbyDoor(bot, dest, doorsTried);
          bot.log(`travel: no progress for a minute; ${door ? `${door.door} ${door.opened ? "opened" : "still shut"}` : "no door near"}`);
          if (!door) watch.step++;
        }
        if (watch.step === 2 && bot.daytime?.sunDamage) watch.step++; // the ring search walks blind to the sun
        if (watch.step === 2) {
          const out = await bot.explore(dest, await bot.self(), { rings: 2, deadline: Date.now() + 90000 });
          bot.log(`travel: ledge/ring search ${out ? "found a way" : "found nothing"}`);
        }
        if (watch.step >= 3) return { arrived: false, stalled: true, reason: "no progress: enclosed", position: await bot.self() };
      }
    }
    // Starving outside the starting graveyard: stop to feed, day or night (in the graveyard the brain
    // keeps going, its prey there is rats among skeletons).
    if (hungerStop && typeof v.blood === "number" && v.blood < TRAVEL_STARVING_BLOOD && (view.self?.z ?? 0) > START_ZONE_Z) {
      await bot.stop(); return { arrived: false, interrupted: "hungry" };
    }
    // By day the trip hops from shadow to shadow (shade.mjs) instead of stopping at the first sun:
    // the exposure budget is 5 s of sun, a shadow resets it in 2.5 s.
    if (bot.daytime?.sunDamage) {
      bot.dayWalking = true; // danger() lets a hop spend its exposure budget
      let hop;
      try { hop = await dayHop(bot, dest, { stop }); } finally { bot.dayWalking = false; }
      if (hop.arrived) return { arrived: true, position: hop.position };
      if (hop.burning) { await bot.stop(); return { arrived: false, interrupted: "sun" }; } // no shadow in view: the brain's seekShade
      hops = (hops ?? 0) + 1;
      // Hops that gain no ground: a shut door (or wall) is in the way; try a door, as by night.
      const here = await bot.self();
      const gone = Math.hypot(dest.x - here.x, dest.z - here.z);
      if (gone < bestLeft - 5) { bestLeft = gone; flatHops = 0; dayStalls = 0; }
      else if (++flatHops >= 4) {
        flatHops = 0;
        const door = await openNearbyDoor(bot, dest, doorsTried);
        if (door) bot.log(`travel: ${door.door} ${door.opened ? "opened" : "still shut"}`);
        else if (++dayStalls >= 3) return { arrived: false, stalled: true, position: await bot.self() };
      }
      if (hop.hopped || hop.exposed) bot.log(`day hop ${hops}: ${hop.hopped ? `to ${hop.spot?.caster ?? "goal"} ${hop.shaded ? "shaded" : "SUNNY"}` : "no shade ahead"}${hop.learned !== undefined ? ` (learned: ${hop.learned} deg from the sun yaw)` : ""}`);
      continue;
    }
    if (v.sun?.burning || (v.sun?.inSun && (v.sun.charge ?? 0) > 0.5)) { await bot.stop(); return { arrived: false, interrupted: "sun" }; }
    const me = view.self ?? await bot.self();
    const d = Math.hypot(dest.x - me.x, dest.z - me.z);
    if (d <= stop + 0.5) return { arrived: true, position: me };
    // Probe leg candidates with the pathfinder before walking: live 2026-09-24 the straight 50 m leg
    // often had no route while one 60 degrees off did, and exploring blind for it cost minutes.
    let route = await routableLeg(bot, me, dest, leg);
    // No route: a door may be shut in the way. Open it and plan again.
    if (!route) {
      const door = await openNearbyDoor(bot, dest, doorsTried);
      if (door) { bot.log(`travel: ${door.door} ${door.opened ? "opened" : "still shut"}`); route = await routableLeg(bot, await bot.self(), dest, leg); }
    }
    if (process.env.BOT_DEBUG) bot.log(`travel: d ${d.toFixed(0)} leg ${route ? `${route.off}deg ${route.len.toFixed(0)}m ${route.path.length} pts` : "none"}`);
    if (route) {
      const r = await bot.followRoute(route.path, me, route.final ? stop : 4, { dash: true });
      if (process.env.BOT_DEBUG && !r.arrived) bot.log(`travel: leg stopped ${JSON.stringify(r).slice(0, 200)}`);
      if (r.interrupted) return { arrived: false, interrupted: r.interrupted }; // sun, damage: the brain decides
      if (!r.stuck) stuckLegs = 0;
      else {
        // Live 2026-09-24 a bot stuck on the same waypoint of every re-planned route in a forest: the
        // nav graph passes something the body cannot. Step sideways (alternating) before re-planning.
        if (++stuckLegs > 6) return { arrived: false, stalled: true, position: await bot.self() };
        const a = Math.atan2(dest.x - me.x, dest.z - me.z) + (stuckLegs % 2 ? 1 : -1) * (Math.PI / 2);
        await bot.drive({ action: "walk", dx: Math.sin(a), dz: Math.cos(a), seconds: 1.5 });
        await sleep(1700);
      }
    }
    else {
      const f = Math.min(1, leg / d);
      const r = await bot.goTo({ x: me.x + (dest.x - me.x) * f, z: me.z + (dest.z - me.z) * f }, { stop: f < 1 ? 4 : stop, attempts: 3, timeoutMs: 90000 });
      if (r.interrupted) return { arrived: false, interrupted: r.interrupted };
    }
    // A stall is a leg that did not move the bot; a detour leg may add distance and still be progress.
    const after = await bot.self();
    if (Math.hypot(after.x - me.x, after.z - me.z) < 3) { if (++stalls >= 4) return { arrived: false, stalled: true, position: after }; }
    else stalls = 0;
  }
  return { arrived: false, timedOut: true };
}

/**
 * Blueprints this bot's user has unlocked. The user's `ProgressionMapper.ProgressionEntity`
 * (NetworkedEntity: Entity index/version in its first 8 bytes) carries the
 * `UnlockedBlueprintElement` buffer (bot-building.md section 4). Live 2026-09-24: empty for a new
 * character; claiming Journal_Gathering added the castle heart (-485210554).
 */
export async function unlockedBlueprints(bot) {
  const players = await call(SERVER, "players", {});
  const p = (players.players ?? []).find((x) => x.name === bot.name);
  if (!p?.userEntity) return null;
  const mapper = await call(SERVER, "get", { entity: p.userEntity, component: "ProgressionMapper" });
  const hex = mapper.fields?.ProgressionEntity?.bytes;
  if (!hex || hex.length < 16) return null;
  const le = (h) => Buffer.from(h, "hex").readInt32LE(0);
  const progression = `${le(hex.slice(0, 8))}:${le(hex.slice(8, 16))}`;
  const r = await call(SERVER, "inspect", { entity: progression, depth: 256, only: ["UnlockedBlueprintElement"] });
  const buf = r.components?.find((c) => c.component?.endsWith("UnlockedBlueprintElement"));
  return new Set((buf?.elements ?? []).map((e) => e.UnlockedBlueprint));
}

/**
 * Heart pivots in the free plots nearest `me`, best first. bot_territories checks only the
 * buildable tile mask, so each pivot's 3 x 3-cell footprint (half-width 7.5 m) is checked for
 * colliding tile models (`TileCollisionTag`; fiber bushes lack it). Live 2026-09-24: a pivot among
 * player-ruin walls and pillars was refused, while a heart was placed with two spruces inside its
 * footprint, so structures rank as blockers ahead of vegetation. The query is narrowed to
 * `TileCollisionTag` because a PrefabGUID+Translation scan of this world hits the 200k scan cap.
 * Scanning stops at the first plot that has a structure-free pivot.
 */
export async function heartSpots(me, { plots = 10, step = 12, half = 7.5 } = {}) {
  const terrs = ((await call(SERVER, "bot_territories", { x: me.x, z: me.z, limit: plots + 2, onlyFree: true }).catch(() => null))?.results ?? [])
    .filter((t) => t.region && t.region !== "None" && t.aabb).slice(0, plots); // "None": territory 0 beside the crypts, not a plot
  const spots = [];
  for (const t of terrs) {
    const seen = new Set();
    for (let x = t.aabb.minX + 8; x <= t.aabb.maxX - 8; x += step) {
      for (let z = t.aabb.minZ + 8; z <= t.aabb.maxZ - 8; z += step) {
        const r = (await call(SERVER, "bot_territories", { x, z, limit: 1, onlyFree: true, spot: "near" })).results?.[0];
        const h = r?.heart;
        if (!h || r.index !== t.index || seen.has(`${h.x},${h.z}`)) continue;
        seen.add(`${h.x},${h.z}`);
        // `near` is a 3D sphere and the ground height is unknown, so ask wide and filter the XZ box.
        const q = await call(SERVER, "query", { all: ["TileCollisionTag", "Translation", "PrefabGUID"], near: { x: h.x, y: me.y, z: h.z, radius: SPOT_QUERY_RADIUS }, limit: 2000 });
        const inBox = (q.entities ?? []).filter((e) => e.position && Math.abs(e.position.x - h.x) <= half && Math.abs(e.position.z - h.z) <= half);
        const vegetation = inBox.filter((e) => VEGETATION.test(e.prefab?.name ?? "")).length;
        // A truncated read cannot prove a spot clear.
        const structures = q.truncated ? Math.max(inBox.length - vegetation, 1) : inBox.length - vegetation;
        spots.push({ x: h.x, z: h.z, plot: t.index, region: t.region, structures, vegetation, distance: Math.hypot(h.x - me.x, h.z - me.z) });
      }
    }
    if (spots.some((sp) => sp.plot === t.index && sp.structures === 0)) break;
  }
  return spots.sort((a, b) => a.structures - b.structures || a.vegetation - b.vegetation || a.distance - b.distance);
}

/** World X/Z of a castle tile coordinate (CastleGeometry: world = (tile - 6400) * 0.5; live for the heart). */
export const tileToWorld = (t) => (t - 6400) * 0.5;

/**
 * The bot's castle, read from the server's `castles` op: plot index, heart position and buildings
 * ({name, prefab, x, z, kind, entity, networkId}); null if the bot owns no heart.
 */
export async function myCastle(bot) {
  const plots = (await call(SERVER, "castles", {})).plots ?? [];
  const plot = plots.find((p) => p.owner?.name === bot.name);
  if (!plot) return null;
  const b = await call(SERVER, "castles", { what: "buildings", plot: plot.territoryIndex });
  const buildings = (b.buildings ?? []).map((x) => ({
    name: x.prefabName, prefab: x.prefab, kind: x.kind, entity: x.entity, networkId: x.networkId,
    x: tileToWorld(x.origin.x), z: tileToWorld(x.origin.y), height: x.origin.height,
  }));
  const heart = buildings.find((x) => x.prefab === HEART_PREFAB);
  // Pieces go at the heart's height (live: floors sent at y 0 beside a y 5 heart sat one level down
  // and could never connect).
  const hy = heart ? (await call(SERVER, "get", { entity: heart.entity, component: "Translation" }).catch(() => null))?.fields?.Value?.y : undefined;
  return { plot: plot.territoryIndex, heart: heart ? { x: heart.x, y: hy, z: heart.z } : null, buildings, decaying: !!plot.decaying };
}

/**
 * Use a structure the way a player does: walk up, aim, hover it and hold Interact. Opens a station
 * (required before a station craft: the server checks an InteractBuff on the station,
 * bot-castle-building.md section 4), binds a coffin, or credits a journal "Interact_" subtask.
 * `target` = {name, x, z}; the client entity is found in view near that point.
 */
// Food a player keeps at home (rats, hearts; item names from the live bag), so a respawn at the
// coffin with ~10 blood eats before it walks out (the respawn-starve loop was the top killer, live).
export const PANTRY_FOOD = ["Item_Consumable_Eat_Rat", "Item_Consumable_Heart_T02_Tainted", "Item_Consumable_Heart_T03_Unsullied",
  "Item_Consumable_Heart_T04_Exquisite"]; // (no Pristine heart prefab in this build, live)

/** Open the castle stash and move food in ("deposit", the given items) or out ("withdraw", all food). */
export async function pantry(bot, stash, op, items = PANTRY_FOOD) {
  const opened = await interactWith(bot, stash);
  if (!opened.ok) return opened;
  for (const item of items) {
    await event(bot, op === "deposit" ? { kind: "moveall", from: "self", to: opened.entity, item } : { kind: "moveall", from: opened.entity, to: "self", item })
      .catch((e) => bot.log(`pantry ${op} ${item}: ${e.message}`));
  }
  await sleep(800);
  return { ok: true, op, items };
}

export async function interactWith(bot, target, { seconds = 1 } = {}) {
  // Big structures: the heart's center is 7.5 m inside its own footprint (live: a 1.8 m stop on the
  // heart never arrived), so stand at the edge; goTo's approach ring reaches that far.
  const stop = /CastleHeart|BloodFountain/i.test(target.name ?? "") ? 8 : 1.8;
  const walk = await bot.goTo(target, { stop, timeoutMs: 60000 });
  if (walk.interrupted) return { ok: false, interrupted: walk.interrupted };
  if (!walk.arrived) return { ok: false, reason: `could not reach ${target.name}` };
  const seen = ((await bot.look({ radius: 10, limit: 10, name: target.name.replace(/^TM_/, "").slice(0, 24) })).nearby ?? [])
    .sort((a, b) => Math.hypot(a.x - target.x, a.z - target.z) - Math.hypot(b.x - target.x, b.z - target.z))[0];
  if (!seen) return { ok: false, reason: `${target.name} not in view` };
  await bot.stop();
  await bot.aim(seen);
  await bot.hover(seen.entity);
  await bot.drive({ action: "hold", buttons: "Interact", seconds });
  await sleep(seconds * 1000 + 500);
  return { ok: true, entity: seen.entity, name: seen.name };
}

/**
 * Fuel the castle heart with blood essence, as a player does from its menu: an unfueled heart
 * decays. Live 2026-09-24: interacting with the heart and moving 30 Item_BloodEssence_T01 into it
 * (bot_event moveall) turned the castles op's `decaying` from true to false.
 */
export async function fuelHeart(bot, heart, { essence = "Item_BloodEssence_T01" } = {}) {
  const opened = await interactWith(bot, heart);
  if (!opened.ok) return opened;
  const moved = await event(bot, { kind: "moveall", from: "self", to: opened.entity, item: essence }).catch((e) => ({ error: e.message }));
  await sleep(1500);
  const plot = ((await call(SERVER, "castles", {})).plots ?? []).find((p) => p.owner?.name === bot.name);
  return { ok: plot ? !plot.decaying : false, moved, decaying: plot?.decaying };
}

/**
 * Keep the castle's mist brazier going, the player's answer to daylight at home: it burns bones from
 * its own inventory during the day and casts a mist shadow around itself
 * (internal research notes). Open it (the server requires the interaction),
 * move every bone from the bag into it, and switch it on if it is off (the toggle flips, so read
 * BurnContainer.Enabled first).
 */
// Refining stations (internal research notes, live recipe reads): what each turns into
// what. Refining starts by itself once input is in and the castle heart powers the station.
// Loading moves every unit of the item (move-all cannot split a stack), so wood goes in only from a
// large stock (the builder regathers what walls need); hides are left out: bone armor needs them,
// and the Tannery comes later (Keely).
export const REFINING = [
  { station: /Sawmill/i, input: "Item_Ingredient_Wood_Standard", min: 300 },     // 20 wood -> 1 plank + sawdust, 20 s
  { station: /Furnace/i, input: "Item_Ingredient_Mineral_CopperOre", min: 20 },  // 20 ore -> 1 copper ingot, 30 s
];

/**
 * A player's refining visit: open the station, take what it made, and load the raw material the bag
 * carries. Loading moves every unit of that item (MoveAllItemsBetweenInventoriesEventV2 with the item
 * filter: without one the whole bag would go in); collecting names the output inventory's own network
 * id (bot_station), because addressing the station takes from its input.
 */
export async function refineAt(bot, station, rule) {
  const opened = await interactWith(bot, station);
  if (!opened.ok) return opened;
  const read = () => call(SERVER, "bot_station", { x: station.x, z: station.z }).catch((e) => ({ error: e.message }));
  let st = await read();
  if (!st?.found) return { ok: false, reason: `no refining station at ${station.x},${station.z}`, st };
  const collected = {};
  for (const it of st.output?.items ?? []) {
    if (!st.output.networkId) break;
    await event(bot, { kind: "moveall", fromNetworkId: st.output.networkId, to: "self", item: it.guid }).catch((e) => bot.log(`collect: ${e.message}`));
    collected[it.item] = it.amount;
  }
  const bag = await bot.inventory();
  const have = bag.items?.find((i) => i.item === rule.input)?.amount ?? 0;
  let loaded = 0;
  if (have >= rule.min) {
    await event(bot, { kind: "moveall", from: "self", to: opened.entity, item: rule.input }).catch((e) => bot.log(`load: ${e.message}`));
    loaded = have;
  }
  await sleep(1200);
  st = await read();
  const after = await bot.inventory();
  const left = after.items?.find((i) => i.item === rule.input)?.amount ?? 0;
  return { ok: Object.keys(collected).length > 0 || (loaded > 0 && left < have), collected, loaded: loaded ? have - left : 0, status: st?.status, recipe: st?.recipe };
}

export async function tendBrazier(bot, brazier, { bone = "Item_Ingredient_Bone" } = {}) {
  const opened = await interactWith(bot, brazier);
  if (!opened.ok) return opened;
  const moved = await event(bot, { kind: "moveall", from: "self", to: opened.entity, item: bone }).catch((e) => ({ error: e.message }));
  await sleep(800);
  const enabled = async () => {
    const bc = await call(SERVER, "get", { entity: brazier.entity, component: "BurnContainer" }).catch(() => null);
    const f = bc?.fields?.Enabled;
    return f ? (f.value ?? f.bytes === "01") === true || f.value === "True" : null;
  };
  let on = await enabled();
  let toggled = false;
  if (on === false) {
    await event(bot, { kind: "toggle", target: opened.entity });
    toggled = true;
    await sleep(800);
    on = await enabled();
  }
  return { ok: on === true, moved, toggled, enabled: on };
}

/**
 * Clear a build footprint the way a player does before placing: cut the trees, break the rocks and
 * pick the bushes standing in it (live 2026-09-24: every floor cell around HeadlessOne's heart held
 * pines, spruces, fiber bushes or a rock, and floors were refused there). Read from the bot's own
 * view (bot_look: breakables and resources), not a server query, which hits the 200k scan cap here;
 * the box is padded because a tree's tile bounds reach past its trunk. Breaking them also yields the
 * wood, stone and fiber a builder needs.
 */
export async function clearFootprint(bot, spot, { half = 2.5, pad = 1.5, maxNodes = 12 } = {}) {
  const { breakTarget } = await import("./bot.mjs");
  const box = half + pad;
  const inBox = (n) => Math.abs(n.x - spot.x) <= box && Math.abs(n.z - spot.z) <= box;
  const cleared = [], stuck = [];
  for (let i = 0; i < maxNodes; i++) {
    const view = await bot.look({ radius: 60, limit: 200 });
    const node = (view.nearby ?? []).find((n) => (n.kind === "breakable" || n.kind === "resource") && /^TM_/.test(n.name ?? "")
      && !/Castle|BloodFountain/.test(n.name ?? "") && inBox(n) && !stuck.includes(n.entity));
    if (!node) break;
    const r = await breakTarget(bot, node, { maxSwings: 40 });
    if (r.interrupted) return { ok: false, interrupted: r.interrupted, cleared, stuck };
    if (r.ok) cleared.push(node.name); else stuck.push(node.entity);
  }
  return { ok: stuck.length === 0, cleared, stuck: stuck.length };
}

/**
 * Place a tile model (any unlocked blueprint) at `spot` from where the bot stands, and confirm it by
 * reading the castle back: a refused build is silent (live). SharedInventory (0) first, as for the
 * heart. The bot must be within build range; the caller travels first.
 */
const SPAWNS = { [-1571980899]: [792289367], [106693260]: [-1293632412] };

export async function placeStructure(bot, prefab, spot, { rotation = 0, waitMs = 5000 } = {}) {
  const here = await bot.self();
  const y = spot.y ?? here.y;
  const tried = [];
  // A blueprint places other prefabs: the wall blueprint spawns TM_Castle_Wall_Tier01_Wood (792289367) and
  // pillars, the entrance its entrance piece (bot-wall-placement.md).
  const placedAs = new Set([prefab, ...(SPAWNS[prefab] ?? [])]);
  const near = (c) => (c?.buildings ?? []).filter((x) => placedAs.has(x.prefab) && Math.hypot(x.x - spot.x, x.z - spot.z) < 3);
  const before = new Set(near(await myCastle(bot)).map((x) => x.entity));
  // consume 0 only: all 33 live placements used it and the consume-1 retry never placed anything
  // (it only doubled the wait on a refusal); a placement shows up within ~2.5 s.
  for (const consume of [0]) {
    await bot.stop();
    await event(bot, { kind: "build", prefab, x: spot.x, y, z: spot.z, rotation, consume });
    const until = Date.now() + waitMs;
    while (Date.now() < until) {
      await sleep(2000);
      const placed = near(await myCastle(bot)).find((x) => !before.has(x.entity));
      if (placed) return { ok: true, entity: placed.entity, name: placed.name, at: { x: placed.x, z: placed.z }, spot, consume };
    }
    tried.push(consume);
  }
  return { ok: false, refused: true, reason: "not placed (refused silently: range, grid, connection or cost)", spot, tried };
}

export async function buildHeart(bot, { waitMs = 8000, cache = {} } = {}) {
  const me = await bot.self();
  if (inCrypt(me)) return { ok: false, reason: "in the crypt" };
  // The blueprint comes from the journal (live: unlocked by claiming Journal_Gathering), so a bot
  // without it keeps following the journal instead of traveling.
  const unlocked = await unlockedBlueprints(bot).catch(() => null);
  if (unlocked && !unlocked.has(HEART_PREFAB)) return { ok: false, reason: "castle heart blueprint not unlocked yet (journal: Gathering)", locked: true };
  // Afford it first (live: the heart costs 30 Item_BloodEssence_T01 + 240 Item_Ingredient_Stone).
  const cost = (await call(SERVER, "bot_cost", { prefab: HEART_PREFAB })).cost ?? [];
  const bag = await bot.inventory();
  const missing = cost.filter((c) => (bag.items.find((i) => i.guid === c.guid)?.amount ?? 0) < c.amount)
    .map((c) => `${c.item} ${bag.items.find((i) => i.guid === c.guid)?.amount ?? 0}/${c.amount}`);
  if (missing.length) return { ok: false, reason: `cannot afford: ${missing.join(", ")}`, unaffordable: true };
  // A heart needs a free castle territory (live: refused silently in the graveyard, which has none)
  // and a footprint free of structures. The scan costs hundreds of bridge calls; the brain passes a
  // cache so a retry after an interrupt reuses it.
  const spots = cache.spots ?? (cache.spots = await heartSpots(me));
  const best = spots[0];
  if (!best) return { ok: false, reason: "no free territory with a heart spot" };
  if (best.structures > 0) return { ok: false, reason: `no structure-free heart spot in the nearest plots (best has ${best.structures})`, spot: best };
  // Which vegetation blocks is not established, so try the plot's best few pivots once there.
  const candidates = spots.filter((sp) => sp.plot === best.plot && sp.structures === 0).slice(0, 3);
  // Stand beside the footprint (half-width 7.5 m); the heart was placed live from as close as 1.7 m
  // to the pivot, so the exact standing point is not critical.
  const trip = await travel(bot, { x: best.x + 9, z: best.z }, { stop: 3 });
  if (trip.interrupted) return { ok: false, reason: `trip interrupted: ${trip.interrupted}`, interrupted: true };
  if (!trip.arrived) return { ok: false, reason: `could not reach territory ${best.plot} (${best.region})`, trip };
  const tried = [];
  for (const spot of candidates) {
    if (spot !== best) await bot.goTo({ x: spot.x + 9, z: spot.z }, { stop: 3, attempts: 2, timeoutMs: 30000 });
    const here = await bot.self();
    // SharedInventory (0) first: live 2026-09-24 the heart appeared after a consume-0 event and took
    // its cost from the bag; the LocalInventory (1) event just before it had not placed within 6 s.
    for (const consume of [0, 1]) {
      await bot.stop(); // a placement may run as an ability cast on the character: hold still
      const sent = await event(bot, { kind: "build", prefab: HEART_PREFAB, x: spot.x, y: here.y, z: spot.z, rotation: 0, consume });
      const until = Date.now() + waitMs;
      while (Date.now() < until) {
        await sleep(1000);
        const q = await call(SERVER, "query", { all: ["CastleHeart"], near: { x: spot.x, y: here.y, z: spot.z, radius: 4 }, limit: 3 });
        const heart = q.entities?.[0];
        if (heart) return { ok: true, heart: heart.entity, spot, consume, sent };
      }
      tried.push({ x: spot.x, z: spot.z, vegetation: spot.vegetation, consume });
    }
  }
  return { ok: false, reason: "no heart appeared (range, height or an unchecked restriction refused it)", tried };
}

/**
 * One step of the journal: claim every finished entry, then work on the first incomplete subtask a
 * bot can do (leave the crypt, gather/break a source of the tracked item, or hunt). Pass `quests` to
 * reuse a fresh bot_quests read.
 */
export async function followJournal(bot, { quests, skip = new Set(), targetSkip = new Set(), claimSkip = new Set(), claimFailures = {}, craftable = null } = {}) {
  quests ??= await call(SERVER, "bot_quests", { user: bot.name });
  const { claims, next } = planJournal(quests, skip, claimSkip, { craftable });
  const claimed = [];
  if (claims.length) {
    for (const a of claims) {
      try { await event(bot, { kind: "claim", achievement: a.achievement }); }
      catch (e) { bot.log(`claim ${a.name ?? a.achievement} failed to send: ${e.message}`); }
    }
    // A sent claim is not a claim: the server refuses silently. Confirm on a fresh read, and stop
    // retrying an entry that failed twice (it would otherwise win the journal priority every tick).
    await sleep(1500);
    const after = await call(SERVER, "bot_quests", { user: bot.name, scope: "all" }).catch(() => null);
    for (const a of claims) {
      const now = after?.achievements?.find((x) => x.achievement === a.achievement);
      if (now && isClaimed(now)) { claimed.push(a.name ?? a.achievement); continue; }
      claimFailures[a.achievement] = (claimFailures[a.achievement] ?? 0) + 1;
      if (claimFailures[a.achievement] >= 2) claimSkip.add(a.achievement);
    }
  }
  if (!next) return { ok: claimed.length > 0, claimed, reason: claimed.length ? undefined : "no actionable subtask or confirmed claim" };
  const { subtask, plan } = next;
  let result;
  if (plan.action === "leaveCrypt") result = await leaveCrypt(bot);
  else if (plan.action === "ui") {
    // Judge by journal progress, not by the send: "Use_VBloodMenu" was answered 75 times with the
    // open-menu event, which does not count for it (live).
    const sent = await event(bot, { kind: "ui", menu: plan.menu });
    await sleep(1500);
    const after = await call(SERVER, "bot_quests", { user: bot.name, scope: "all" }).catch(() => null);
    const now = after?.achievements?.flatMap((a) => a.subtasks ?? []).find((x) => x.subtask === subtask.subtask);
    result = { ok: (now?.progress ?? 0) > (subtask.progress ?? 0), sent, reason: "menu event sent but the subtask did not advance" };
  }
  else if (plan.action === "interact") {
    const castle = await myCastle(bot);
    const b = castle?.buildings.find((x) => (x.name ?? "").includes(plan.target));
    result = b ? await interactWith(bot, b) : { ok: false, reason: `no ${plan.target} in the castle yet` };
  }
  else if (plan.action === "gather") {
    result = { ok: false, reason: "no source found" };
    for (const name of plan.names) {
      result = await gather(bot, name, { skip: targetSkip, radius: 60 });
      if (result.ok || !/no .* within/.test(result.reason ?? "")) break;
    }
    if (!result.ok && plan.hunt?.length) result = await huntNamed(bot, plan.hunt, targetSkip);
  } else if (plan.action === "hunt") result = await huntNamed(bot, plan.names, targetSkip);
  else if (plan.action === "craft") {
    // The recipe is looked up by its output (names do not follow the item: Item_Gloves_T01_Bone is
    // made by Recipe_Armor_Gloves_T01_Bone, live bot_recipes). Afford it first; otherwise gather what
    // is missing (the same sources the journal uses).
    const rec = (await call(SERVER, "bot_recipes", { user: bot.name, output: plan.item }).catch(() => null))?.recipes
      ?.find((r) => r.outputs?.some((o) => o.item === plan.item));
    const cost = rec?.requirements;
    if (!rec) result = { ok: false, reason: `no recipe makes ${plan.item}` };
    else if (!rec.unlocked) result = { ok: false, reason: `${rec.recipe} not unlocked yet` };
    else if (!rec.characterCraftable) result = { ok: false, reason: `${rec.recipe} needs a station (${rec.stations.map((s) => s.name).join(", ")})` };
    else {
      const bag = await bot.inventory();
      const missing = cost.find((c) => (bag.items.find((i) => i.guid === c.guid)?.amount ?? 0) < c.amount);
      if (missing) {
        const sub = planSubtask({ type: "PossessItem", trackedItem: { name: missing.item } });
        result = sub.action === "gather" ? await gather(bot, sub.names[0], { skip: targetSkip, radius: 60 })
          : sub.action === "hunt" ? await huntNamed(bot, sub.names, targetSkip)
          : { ok: false, reason: `missing ${missing.item} and no known source` };
      } else {
        result = await craft(bot, rec.recipe);
        if (result.ok && WEAPON.test(plan.item)) result.equip = await equipBest(bot);
      }
    }
  }
  return { ok: !!result?.ok, claimed, subtask: subtask.name ?? subtask.subtask, subtaskId: subtask.subtask, progress: `${subtask.progress}/${subtask.required}`, plan, result };
}

export async function huntNamed(bot, names, skip) {
  // Pick like a player pulling: a fair level (gear + 2 at most) and alone (no other hostile within
  // 8 m of it), nearest first (bot-survival-combat-tips.md change 8: journal and gear hunts walked
  // into groups and above-level units).
  const gear = (await bot.inventory().catch(() => null))?.gear?.total ?? 0;
  const all = (await bot.look({ radius: 60, limit: 120, kind: "unit" })).nearby ?? [];
  const hostiles = all.filter((n) => HOSTILE.test(n.name ?? ""));
  const alone = (t) => !hostiles.some((o) => o.entity !== t.entity && Math.hypot(o.x - t.x, o.z - t.z) < 8);
  for (const name of names) {
    const t = all.filter((n) => (n.name ?? "").includes(name) && !skip.has(n.entity) && (n.level ?? 0) <= gear + 2 && alone(n))
      .sort((a, b) => a.distance - b.distance)[0];
    if (!t) continue;
    const r = await fight(bot, t, { chooseWeapon: true });
    if (!r.ok) skip.add(t.entity);
    return { ...r, target: t.name };
  }
  return { ok: false, reason: `no lone, fair ${names.join("/")} in sight` };
}

