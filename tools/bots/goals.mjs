// Long-term goals and personas. Survival stays in brain.mjs's decide(); once the bot is safe and fed,
// its persona's ordered goal list decides what the bot works toward (build a castle, get better gear,
// follow the journal). Planners are pure functions over a `knowledge` snapshot and return ONE concrete
// step ({action, ...}) or null; runStep() executes a step with the skills.
import { call } from "./bridge.mjs";
import { planBoss, bossFight } from "./boss.mjs";
import { wallSpotsFromFloors, objectSpotsFromFloors, roomPlan } from "./layout.mjs";
import { SERVER } from "./bot.mjs";
import { gather } from "./skills.mjs";
import { ITEM_SOURCES, craft, huntNamed, placeStructure, clearFootprint, interactWith, tendBrazier, fuelHeart, travel, refineAt, REFINING, HEART_PREFAB, START_ZONE_Z, pantry, learnedRoute } from "./progression.mjs";

// ---- personas ---------------------------------------------------------------------------------------

/**
 * A persona orders the long-term goals; the first goal with a doable step wins each tick.
 *   home     be where the work is: walk back to the castle, or leave the starting graveyard
 *   journal  follow the journal (claims, tracked items, crafts)
 *   heart    place the first castle heart
 *   castle   build the castle layout around the heart (builder: the full layout)
 *   gear     craft and equip the best unlocked gear for every slot
 *   boss     hunt the next V Blood boss the gear level supports (boss.mjs), feed on it
 * `rotate`: start from a different goal each tick (turn-taking) instead of strict priority, so one
 * long goal cannot starve the others.
 * When no goal has a step, the brain works through the persona's `stock` rotation (gather names, or
 * "hunt" for a weak unit), so a bot never runs out of useful work.
 */
export const PERSONAS = {
  // blood: the blood types each persona hangs on to a feed for (skills.wantBlood).
  balanced: { goals: ["home", "journal", "heart", "gear", "castle", "boss"], rotate: true, castle: "starter", weapons: ["Sword", "Axe", "Mace"], stock: ["Rock", "Tree", "hunt"], blood: ["Brute", "Warrior", "Rogue", "Creature"] },
  builder: { goals: ["home", "heart", "castle", "journal", "gear", "boss"], castle: "full", weapons: ["Axe", "Mace", "Sword"], stock: ["Tree", "Rock", "Tree", "hunt"], blood: ["Worker", "Creature"] },
  // The combat trainer: no long-term goals, it stays where the practice is and fights fair targets
  // all day so combat can be measured and tuned (every fight goes to combat.jsonl).
  // Its armor comes from the gear goal (it fought in no armor with 1000+ bones, live); weapons stay
  // out of it (empty list), so the rotation keeps choosing them.
  // It climbs the V Blood ladder in order with a gear kit matched to each boss (brain.mjs, arena.mjs).
  trainer: { goals: ["gear", "boss"], castle: "starter", weapons: [], kitOnly: true, stock: ["train"], blood: ["Warrior", "Brute", "Rogue"] },
  warrior: { goals: ["home", "gear", "boss", "journal", "heart", "castle"], castle: "starter", weapons: ["Sword", "Spear", "Mace", "Axe"], stock: ["hunt", "Rock", "hunt", "Tree"], blood: ["Warrior", "Brute", "Rogue"] },
};
export const personaOf = (name) => PERSONAS[name] ?? PERSONAS.balanced;

// ---- gear ---------------------------------------------------------------------------------------------

/** Equipment slot an item goes in, from its prefab name (live names: Item_Weapon_Sword_T01_Bone, Item_Gloves_T01_Bone). */
export function slotOf(item = "") {
  const w = /^Item_Weapon_([A-Za-z]+)_/.exec(item);
  if (w) return `weapon:${w[1]}`;
  // Item_MagicSource_* (the bone ring and later jewels) goes in the equipment's grimoire slot, whose
  // level is the spell part of gear level.
  for (const [slot, re] of [["chest", /_Chest_/], ["legs", /_Legs_/], ["gloves", /_Gloves_/], ["boots", /_Boots_/], ["headgear", /_Headgear_|_Hat_/], ["cloak", /_Cloak_/], ["grimoire", /^Item_MagicSource_/]]) {
    if (re.test(item)) return slot;
  }
  return null;
}
/** Gear tier from the _T0N_ naming; unknown tiers rank lowest. */
export const tierOf = (item = "") => Number(/_T(\d+)/.exec(item)?.[1] ?? 0);

/**
 * Next gear step: for each slot (weapons: the persona's preferred types, first only), the best
 * unlocked recipe whose tier beats what the bot owns (equipped or in the bag). Owned but not worn:
 * equip it. Craftable in hand (or at a known station) and affordable: craft. Otherwise acquire the
 * first missing ingredient. Returns null when nothing improves.
 */
// Repairs (internal research notes, live): each death drains 12.5% durability
// and a broken piece counts for nothing in gear level. The character screen's repair takes bag bones:
// 16 for a bone armor piece or ring, 8 for a bone weapon, scaled by the missing fraction.
export const REPAIR_BELOW = 0.3;
const repairCost = (d) => Math.ceil((d.slot === "weapon" ? 8 : 16) * (1 - d.durability / d.max));

export function planRepair(k) {
  // Bone gear only: its repair recipe costs bones (live). Other tiers repair with other materials,
  // not read yet; planning bones for them would loop on refused repairs.
  // (T01 bone only: reinforced bone repair was refused with 105 bones in the bag, live.)
  const worn = (k.durability ?? []).filter((d) => d.max > 0 && /_T01_Bone/.test(d.item ?? "") && (d.broken || d.durability / d.max < REPAIR_BELOW));
  if (!worn.length) return null;
  const bones = k.bag?.items?.find((i) => i.item === "Item_Ingredient_Bone")?.amount ?? 0;
  const slots = [];
  let spend = 0;
  for (const d of worn.sort((a, b) => a.durability / a.max - b.durability / b.max)) {
    if (spend + repairCost(d) <= bones) { slots.push(d.slot); spend += repairCost(d); }
  }
  if (slots.length) return { action: "repair", slots, why: `repair ${slots.join(", ")} (${spend} bones)` };
  const get = acquire("Item_Ingredient_Bone");
  return get ? { ...get, why: `need bones x${repairCost(worn[0]) - bones} to repair ${worn[0].slot}` } : null;
}

export function planGear(k, persona) {
  // Broken gear first: it is worth nothing until repaired.
  const repair = planRepair(k);
  if (repair) return repair;
  // The trainer's gear is its arena kit (arena.mjs): repairs only, no wearing or crafting (it
  // re-equipped a higher-tier set over the kit and fought at gear 29 instead of 17, live).
  if (persona.kitOnly) return null;
  const owned = new Map(); // slot -> {item, tier, worn, bagSlot}
  const note = (item, worn, bagSlot) => {
    const slot = slotOf(item);
    if (!slot) return;
    const tier = tierOf(item);
    const cur = owned.get(slot);
    if (!cur || tier > cur.tier || (tier === cur.tier && worn)) owned.set(slot, { item, tier, worn, bagSlot });
  };
  for (const e of k.equipped ?? []) note(e.item, true);
  for (const o of k.bag?.occupied ?? []) note(o.item, false, o.slot);

  // Wear anything better than what is in that equipment slot. All weapons share the one weapon slot:
  // an empty hand takes any weapon; otherwise only a higher tier, or the preferred type at equal tier.
  for (const [slot, o] of owned) {
    if (o.worn) continue;
    const isWeapon = slot.startsWith("weapon:");
    // No weapon preference (the trainer): its weapon rotation chooses weapons; equipping here fought
    // the rotation in a loop for over an hour (live 2026-09-25).
    if (isWeapon && !persona.weapons.length) continue;
    const w = (k.equipped ?? []).find((e) => e.slot === (isWeapon ? "weapon" : slot));
    const better = !w || o.tier > tierOf(w.item)
      || (isWeapon && o.tier === tierOf(w.item) && slot === `weapon:${persona.weapons[0]}` && slotOf(w.item) !== slot);
    if (better) return { action: "equip", item: o.item, bagSlot: o.bagSlot, why: `wear ${o.item}` };
  }

  // Learned recipes only: of 58 "unlocked" gear recipes on a fresh character, 57 were
  // AlwaysUnlocked cosmetics/DLC pieces (Blackfang, Dracula headgear, T0X sets; live 2026-09-24).
  // Headgear and cloaks are cosmetic slots, so they are not planned either.
  // A crossbow is carried as a second weapon: fleeing prey (deer) is shot, and fights swap to it at
  // range (hybrid). It stays in the bag; fights pick it when they need it.
  // Cheapest upgrades first: the workbench's reinforced-bone weapon (planks and stone) and blood-bone
  // ring (16 blood essence) before armor (64 rugged hide a piece).
  const wanted = [...(persona.weapons.length ? [`weapon:${persona.weapons[0]}`] : []), "grimoire", "chest", "legs", "gloves", "boots", ...(persona.weapons.length ? ["weapon:Crossbow"] : [])];
  const recipes = (k.recipes ?? []).filter((r) => r.unlocked && (!r.alwaysUnlocked || WORKBENCH_UPGRADE.test(r.outputs?.[0]?.item ?? "")) && r.outputs?.length && /_T\d\d_/.test(r.outputs[0].item ?? ""));
  for (const slot of wanted) {
    // The highest tier affordable now, else the next tier up (bone, reinforced bone, then copper, as a
    // player climbs: a copper plan would hold the cheap reinforced upgrade back behind copper ore).
    const ownTier = owned.get(slot)?.tier ?? -1;
    const options = recipes
      .map((r) => ({ r, item: r.outputs[0].item }))
      .filter((x) => slotOf(x.item) === slot && canCraftHere(x.r, k) && tierOf(x.item) > ownTier);
    const best = options.filter((x) => !missingFor(x.r.requirements, k.bag)).sort((a, b) => tierOf(b.item) - tierOf(a.item))[0]
      ?? options.sort((a, b) => tierOf(a.item) - tierOf(b.item))[0];
    if (!best) continue;
    const missing = missingFor(best.r.requirements, k.bag);
    if (!missing) return { action: "craft", recipe: best.r.recipe, item: best.item, station: stationFor(best.r, k), why: `craft ${best.item} for ${slot}` };
    // An upgrade consumes the lower piece from the bag: the worn one stays on, a spare is made first.
    const input = /^Item_(Weapon|Chest|Legs|Gloves|Boots|MagicSource)_/.test(missing.item)
      && recipes.find((r) => r.outputs[0].item === missing.item && canCraftHere(r, k));
    if (input) {
      const short = missingFor(input.requirements, k.bag);
      if (!short) return { action: "craft", recipe: input.recipe, item: input.outputs[0].item, station: stationFor(input, k), why: `a spare ${missing.item} for ${best.item}` };
      const get = acquire(short.item);
      if (get) return { ...get, why: `need ${short.item} x${short.short} for a spare ${missing.item} (${best.item})` };
      continue;
    }
    const get = acquire(missing.item);
    if (get) return { ...get, why: `need ${missing.item} x${missing.short} for ${best.item}` };
  }
  // Supplies: keep healing salves on hand once the recipe is learned (Vermin Salve, journal reward).
  const healing = (k.recipes ?? []).find((r) => r.unlocked && !r.alwaysUnlocked && /Salve|HealingPotion/.test(r.outputs?.[0]?.item ?? r.recipe ?? "") && canCraftHere(r, k));
  const carried = (k.bag?.items ?? []).filter((i) => /Item_Consumable_(Salve|HealingPotion)/.test(i.item ?? "")).reduce((n, i) => n + i.amount, 0);
  if (healing && carried < SALVE_STOCK) {
    const missing = missingFor(healing.requirements, k.bag);
    if (!missing) return { action: "craft", recipe: healing.recipe, item: healing.outputs?.[0]?.item, station: stationFor(healing, k), why: `craft a healing salve (${carried}/${SALVE_STOCK})` };
    const get = acquire(missing.item);
    if (get) return { ...get, why: `need ${missing.item} x${missing.short} for a healing salve` };
  }
  return null;
}
const SALVE_STOCK = 3;
// Workbench upgrades flagged AlwaysUnlocked in the game data (live bot_recipes 2026-09-25): real
// progression recipes, unlike the AlwaysUnlocked cosmetic and DLC sets the recipe filter excludes.
export const WORKBENCH_UPGRADE = /_T0[23]_(Bone_?Reinforced|BloodBoneRing|Copper)$/;

function canCraftHere(r, k) { return r.characterCraftable || !!stationFor(r, k); }
/** A station of the right kind in the bot's castle ({name, x, z}), or null for in-hand crafting. */
function stationFor(r, k) {
  if (r.characterCraftable) return null;
  const names = new Set((r.stations ?? []).map((s) => s.name));
  const b = (k.castle?.buildings ?? []).find((x) => names.has(x.name));
  return b ? { name: b.name, x: b.x, z: b.z } : null;
}

/** First requirement the bag cannot pay: {item, guid, short}; null if affordable. */
export function missingFor(requirements = [], bag) {
  for (const c of requirements) {
    const have = bag?.items?.find((i) => i.guid === c.guid)?.amount ?? 0;
    if (have < c.amount) return { item: c.item, guid: c.guid, short: c.amount - have };
  }
  return null;
}

/** A step that gets more of `item` (gather or hunt), from the known item sources; null if unknown. */
export function acquire(item) {
  const src = ITEM_SOURCES.find((s) => s.item.test(item ?? ""));
  if (!src) return null;
  return src.gather ? { action: "gather", names: src.gather, hunt: src.hunt ?? [], item } : { action: "hunt", names: src.hunt, item };
}

// ---- castle -------------------------------------------------------------------------------------------

/**
 * What to build, in order, per castle plan. `match` finds the unlocked blueprint by prefab name;
 * `at(i)` gives the i-th placement of that kind relative to the heart pivot (world meters, cell
 * grid = 5 m, heart footprint = 3 x 3 cells). Layout offsets are filled from castle research;
 * anything marked inferred is verified by placement read-back (a refused spot is skipped).
 */
// Wall blueprints: Journal_Settling unlocked BP_Castle_Wall_Tier01_Wood_Plain (80 wood + 30 fiber),
// its _Entrance, a pillar and a palisade door together with the plain floor (20 stone), live
// 2026-09-24. Journal_Fortify then asks for 3 floors and 3 walls; the coffin, stash and brazier
// come with later entries, so walls and floors come first.
const HEART_TOP_UP_ESSENCE = 20;
const UPKEEP_RADIUS = 60;             // meters from the heart: fuel, brazier and refining only here
const REFINE_LOAD_MS = 3 * 60000, REFINE_COLLECT_MS = 10 * 60000;
// The blueprint (BP_Castle_Wall_Tier01_Wood_Plain) places TM_Castle_Wall_Tier01_Wood plus two pillars; both
// names match here, the pillars, entrances and doors do not.
// The first roofed room around the coffin (layout.mjs roomPlan): the castle's shade by day.
const ROOM = { kind: "room", match: /^$/, count: 1 };
const ROOM_WALL = /Wall_Tier01_Wood_Plain$/i, ROOM_ENTRANCE = /Wall_Tier01_Wood_Entrance$/i;
const WALL = { match: /Wall_Tier01_Wood(_Plain)?$|Palisade/i, not: /Pillar|Entrance|Gate|Door/i };
export const CASTLE_PLANS = {
  // Unlock chain (bot-refining.md, live): Journal_Fortify (3 floors, 3 walls) unlocks the coffin,
  // stash and mist brazier; Journal_Shelter (all three built) unlocks the workbench, sawmill and
  // furnace. The coffin comes first of all, as soon as it unlocks: a bound coffin makes deaths
  // respawn at home instead of the graveyard 1.5 km away (live 2026-09-25 the bots were stuck in
  // graveyard respawn loops); its spot is floor 5. The workbench costs planks, so the sawmill goes
  // before it. Floors grow from the heart's center block; locked entries are skipped until unlocked.
  // The mist brazier right after the coffin (same unlock), next to it: its mist is the castle's
  // daytime shade, and a respawn at the coffin by day burned a bot four times in four minutes (live).
  starter: [
    { kind: "floor", match: /Floor/i, count: 5 },
    { kind: "coffin", match: /Coffin/i, not: /Servant|Stone/i, count: 1 },
    { kind: "brazier", match: /MistBrazier|Brazier/i, count: 1 },
    { kind: "wall", ...WALL, count: 3 },
    ROOM,
    { kind: "floor", match: /Floor/i, count: 12 },
    { kind: "stash", match: /Stash|Storage|Chest/i, count: 1 },
    { kind: "brazier", match: /MistBrazier|Brazier/i, count: 1 },
    { kind: "floor", match: /Floor/i, count: 17 },               // sawmill and furnace spots are floor indices 13-16
    { kind: "sawmill", match: /Sawmill/i, count: 1 },
    { kind: "workbench", match: /SimpleCraftingBench|Workbench/i, count: 1 },
    { kind: "furnace", match: /Furnace/i, count: 1 },
  ],
  // The builder's castle: the same start, then a wider floor and a walled perimeter.
  full: [
    { kind: "floor", match: /Floor/i, count: 5 },
    { kind: "coffin", match: /Coffin/i, not: /Servant|Stone/i, count: 1 },
    { kind: "brazier", match: /MistBrazier|Brazier/i, count: 1 },
    { kind: "wall", ...WALL, count: 3 },
    ROOM,
    { kind: "floor", match: /Floor/i, count: 12 },
    { kind: "stash", match: /Stash|Storage|Chest/i, count: 1 },
    { kind: "brazier", match: /MistBrazier|Brazier/i, count: 1 },
    { kind: "floor", match: /Floor/i, count: 17 },               // sawmill and furnace spots are floor indices 13-16
    { kind: "sawmill", match: /Sawmill/i, count: 1 },
    { kind: "workbench", match: /SimpleCraftingBench|Workbench/i, count: 1 },
    { kind: "furnace", match: /Furnace/i, count: 1 },
    { kind: "floor", match: /Floor/i, count: 24 },
    { kind: "stash", match: /Stash|Storage|Chest/i, count: 2 },   // on an outer floor, after it exists
    { kind: "wall", ...WALL, count: 20 },
  ],
};

/**
 * Next castle step: the first plan entry with fewer built than wanted and an unlocked blueprint.
 * Affordable: build at the next free layout spot. Otherwise acquire the first missing material.
 * k.castle = myCastle() ({heart:{x,z}, buildings:[{name,x,z}]}); k.blueprints = bot_blueprints rows;
 * k.refused = Set of "kind:index" spots the server refused (skipped, the next index is tried).
 */
export function planCastle(k, persona, layout) {
  if (!k.castle?.heart || !layout) return null;
  // Upkeep only at home: a bot far away walked 1.7 km to fuel its heart at blood 0 and starved on the
  // way (live 2026-09-25); getting home is the home goal's job.
  const atHome = !k.self || Math.hypot(k.castle.heart.x - k.self.x, k.castle.heart.z - k.self.z) <= UPKEEP_RADIUS;
  // Upkeep first: a decaying heart gets the blood essence the bot carries.
  const essence = k.bag?.items?.find((i) => i.item === "Item_BloodEssence_T01")?.amount ?? 0;
  const heart = (k.castle.buildings ?? []).find((b) => b.prefab === HEART_PREFAB);
  if (atHome && k.castle.decaying && heart && essence >= 5) return { action: "fuel", building: heart, why: `fuel the heart (${essence} blood essence)` };
  // Players keep the heart stocked rather than waiting for decay (~180 essence a day at level 1,
  // bot-player-playbook.md A6): top it up with what the bot carries, paced like the brazier.
  if (atHome && heart && k.fuelDue && essence >= HEART_TOP_UP_ESSENCE) return { action: "fuel", building: heart, why: `top up the heart (${essence} blood essence)` };
  // A built mist brazier with bones in the bag gets fed and switched on (brazierDue:
  // the brain paces this, at most every few minutes).
  const brazier = (k.castle.buildings ?? []).find((b) => /MistBrazier/i.test(b.name ?? ""));
  const bones = k.bag?.items?.find((i) => /Item_Ingredient_Bone$/.test(i.item ?? ""))?.amount ?? 0;
  if (atHome && brazier && k.brazierDue && bones >= BRAZIER_MIN_BONES) return { action: "tend", building: brazier, why: `feed the mist brazier (${bones} bones)` };
  // Refining: visit a station to collect what it made and load raw material (planks and copper ingots
  // come only from here; workbench, copper gear and journal 11 depend on them).
  // Pantry: spare food goes into the castle stash (rats from 4, hearts from 2, with blood to spare),
  // for the next respawn at the coffin.
  const stash = (k.castle.buildings ?? []).find((b) => /Stash|Chest/i.test(b.name ?? ""));
  if (atHome && stash && (k.vitals?.blood ?? 0) >= 60) {
    const spare = (k.bag?.items ?? []).filter((i) => (/Eat_Rat/.test(i.item ?? "") && i.amount >= 4) || (/Consumable_Heart_/.test(i.item ?? "") && i.amount >= 2)).map((i) => i.item);
    if (spare.length) return { action: "pantry", op: "deposit", items: spare, building: stash, why: `stock the pantry (${spare.map((i) => i.replace("Item_Consumable_", "")).join(", ")})` };
  }
  // Each station on its own timer (k.refineLast[name]): a load visit at most every 3 min, a collect
  // visit every 10, so one station cannot starve the next.
  for (const rule of atHome ? REFINING : []) {
    const station = (k.castle.buildings ?? []).find((b) => rule.station.test(b.name ?? ""));
    if (!station) continue;
    const since = (k.now ?? 0) - (k.refineLast?.[station.name] ?? 0);
    const have = k.bag?.items?.find((i) => i.item === rule.input)?.amount ?? 0;
    if ((have >= rule.min && since > REFINE_LOAD_MS) || since > REFINE_COLLECT_MS) {
      return { action: "refine", building: station, rule, why: `refine at ${station.name} (${have} ${rule.input.replace(/^Item_Ingredient_/, "")})` };
    }
  }
  for (const entry of CASTLE_PLANS[persona.castle] ?? CASTLE_PLANS.starter) {
    if (entry.kind === "room") {
      const refusedEdges = new Set([...(k.refused ?? [])].filter((r) => r.startsWith("room:")).map((r) => r.slice(5)));
      const plan = roomPlan(k.castle.buildings ?? [], k.castle.heart, refusedEdges);
      const e = plan?.missing?.[0];
      if (!e) continue;
      const bp = (k.blueprints ?? []).find((b) => (e.entrance ? ROOM_ENTRANCE : ROOM_WALL).test(b.name ?? ""));
      if (!bp) continue;
      const spot = { x: e.x, z: e.z, rotation: e.rotation, ...(typeof k.castle.heart.y === "number" ? { y: k.castle.heart.y } : {}) };
      const missing = missingFor(bp.cost, k.bag);
      const far = k.self ? Math.hypot(spot.x - k.self.x, spot.z - k.self.z) : 0;
      if (!missing && far > BUILD_RANGE && !tripFits(k, far)) continue;
      if (!missing) return { action: "build", kind: "room", index: `${e.x},${e.z}`, prefab: bp.guid, name: bp.name, spot, why: `close the room: ${e.entrance ? "entrance" : "wall"} (${plan.missing.length} to go)` };
      const get = acquire(missing.item);
      if (get) return { ...get, why: `need ${missing.item} x${missing.short} for the room` };
      continue;
    }
    const fits = (name) => entry.match.test(name ?? "") && !entry.not?.test(name ?? "");
    const built = (k.castle.buildings ?? []).filter((b) => b.prefab !== HEART_PREFAB && fits(b.name)).length;
    if (built >= entry.count) continue;
    const bp = (k.blueprints ?? []).find((b) => b.guid !== HEART_PREFAB && fits(b.name));
    if (!bp) continue; // not unlocked yet: the journal unlocks it
    let index = built, spot = null;
    for (; index < built + 8 && !spot; index++) {
      if (k.refused?.has(`${entry.kind}:${index}`)) continue;
      spot = entry.kind === "wall"
        ? wallSpotsFromFloors((k.castle.buildings ?? []).filter((b) => /Floor/i.test(b.name ?? "")), k.castle.heart)[index] ?? null
        : entry.kind === "floor" ? layout(entry.kind, index, k.castle.heart)
        : objectSpotsFromFloors(k.castle.buildings ?? [], k.castle.heart, entry.kind === "brazier" ? (k.castle.buildings ?? []).find((b) => /Coffin/i.test(b.name ?? "")) : null)[index - built] ?? null;
    }
    if (spot && typeof k.castle.heart.y === "number") spot = { ...spot, y: k.castle.heart.y };
    if (!spot) continue;
    const missing = missingFor(bp.cost, k.bag);
    const far = k.self ? Math.hypot(spot.x - k.self.x, spot.z - k.self.z) : 0;
    if (!missing && far > BUILD_RANGE && !tripFits(k, far)) continue; // the walk to the castle would end in daylight
    if (!missing) return { action: "build", kind: entry.kind, index: index - 1, prefab: bp.guid, name: bp.name, spot, why: `build ${entry.kind} ${built + 1}/${entry.count}` };
    const get = acquire(missing.item);
    if (get) return { ...get, why: `need ${missing.item} x${missing.short} for ${bp.name}` };
  }
  return null;
}

// ---- home ---------------------------------------------------------------------------------------------

export const HOME_RADIUS = 120;           // further than this from the heart: walk home
export const WALK_SPEED = 4;              // m/s, a little under the live 4.5 m/s, for trip timing
export const DAWN_MARGIN_S = 30;          // a trip must end this long before the sun comes up
export const BUILD_RANGE = 80;            // build without walking when this close (server range 90 m)
// By day, work only this close to a known shaded spot: 15 m is ~3.5 s of walking, inside the 5 s
// exposure budget with a margin (25 m was 5.8 s, more than the whole budget: gap analysis s. 4).
export const SHADE_WORK_RADIUS = 15;
export const BRAZIER_MIN_BONES = 10;      // tend the brazier from this many bones (it takes them all): lit early, its mist is the castle's shade
export { START_ZONE_Z };

/**
 * Where the bot should be. With a castle: within HOME_RADIUS of the heart (a death respawns it in the
 * graveyard until a coffin is bound). Without one: out of the starting graveyard, where withered
 * skeletons downed every bot that lingered (live 2026-09-24), toward the nearest free plot
 * (`k.frontier`), which is also where its heart will go.
 */
export function planHome(k) {
  const me = k.self;
  if (!me || me.x === undefined) return null;
  const heart = k.castle?.heart;
  let to = null, why = null;
  if (heart && Math.hypot(heart.x - me.x, heart.z - me.z) > HOME_RADIUS) { to = { x: heart.x, z: heart.z + 12 }; why = "go home"; }
  else if (!heart && me.z < START_ZONE_Z && k.frontier) { to = k.frontier; why = "leave the graveyard for the nearest free plot"; }
  if (!to) return null;
  const d = Math.hypot(to.x - me.x, to.z - me.z);
  // Day or night: waiting out the day in the graveyard is not what a player does (a day here is 6
  // min, a night 12, the walk home ~6.5: respawned bots mid-night never set off, live 2026-09-25).
  // By day the trip hops shade: it stops when sun exposure builds, the brain seeks shade, and the
  // trip resumes once the exposure has reset.
  const sunny = !!k.day && !tripFits(k, d);
  return { action: "travel", to, why: `${why} (${d.toFixed(0)} m${sunny ? ", hopping shade" : ""})` };
}

/** A trip of `d` meters fits if it is night-time and ends before sunrise (players travel by night). */
export function tripFits(k, d) {
  if (!k.day) return true; // no clock read: do not block
  if (k.day.sunOut) return false;
  return d / WALK_SPEED + DAWN_MARGIN_S < (k.day.dawnIn ?? Infinity);
}

/**
 * Work a player does while the sun is out. From shade: claims and menu steps, equipping, in-hand
 * crafts, and builds within range. With a known shaded spot close by (k.shadeNear) the bot also
 * shade-hops: it goes out to gather, hunt or use a station nearby, the danger check brings it back
 * to the shade when sun charge builds up, and it goes out again once the charge has reset. Trips
 * go by day too, the same way: stopped by sun exposure, on again from shade.
 */
export const DAY_TRIP_M = 100;
export function dayTripOk(k, to) {
  if (!k.self || !to) return true;
  return Math.hypot(to.x - k.self.x, to.z - k.self.z) <= DAY_TRIP_M || !!learnedRoute(k.self, to);
}

export function sunSafe(step, k) {
  if (step.action === "journal") {
    const a = k.journal?.next?.plan?.action;
    return (k.journal?.claims ?? 0) > 0 || a === "ui" || (!!k.shadeNear && ["gather", "hunt", "interact", "craft"].includes(a));
  }
  if (step.action === "equip" || step.action === "tend" || step.action === "fuel") return true; // tending: the brazier is the shade
  // By day only short trips or remembered routes: crossing open ground hops between guessed shadows,
  // right only ~2 times in 3 (live), and two misses burn. Long unknown trips wait for dusk.
  if (step.action === "travel") return dayTripOk(k, step.to);
  if (step.action === "craft") return !step.station || !!k.shadeNear;
  if (step.action === "build") return !!k.self && Math.hypot(step.spot.x - k.self.x, step.spot.z - k.self.z) <= BUILD_RANGE;
  if (step.action === "gather" || step.action === "hunt") return !!k.shadeNear;
  return false;
}

// ---- choosing and running ----------------------------------------------------------------------------

/**
 * The persona's first doable goal step, or null (the brain then runs the stock rotation).
 * `ready(goal)` is the brain's cooldown check; the journal and heart goals are delegated to their
 * existing skills and only need a yes/no here.
 */
export function planGoals(k, persona, { ready = () => true, layout, turn = 0 } = {}) {
  // A repair the bag can pay for comes before any goal: it is instant (no station) and broken gear
  // is worth nothing in a fight. Gathering the bones for one stays with the gear goal.
  const fix = planRepair(k); // every persona, the trainer too (it fought on broken armor, live)
  if (fix?.action === "repair" && ready("gear")) return { ...fix, goal: "gear" };
  // "home" is always checked first; rotating personas rotate the rest.
  const rest = persona.goals.filter((g) => g !== "home");
  const start = persona.rotate ? turn % rest.length : 0;
  const order = [...(persona.goals.includes("home") ? ["home"] : []), ...rest.slice(start), ...rest.slice(0, start)];
  for (const goal of order) {
    if (!ready(goal)) continue;
    let step = null;
    if (goal === "home") step = planHome(k);
    else if (goal === "journal" && k.journal && (k.journal.claims > 0 || k.journal.next)) step = { action: "journal" };
    else if (goal === "heart" && !k.hasHeart && k.heartAllowed) step = { action: "buildHeart" };
    else if (goal === "gear") step = planGear(k, persona);
    else if (goal === "castle") step = planCastle(k, persona, layout);
    else if (goal === "boss") step = planBoss(k);
    if (step && k.day?.sunOut && !sunSafe(step, k)) continue;
    // By day, gathering stays within reach of the shade the bot works from.
    if (step && k.day?.sunOut && (step.action === "gather" || step.action === "hunt")) step.radius = SHADE_WORK_RADIUS;
    if (step) return { goal, ...step };
  }
  return null;
}

/** Execute one goal step (journal/buildHeart are run by the brain with its own state). */
export async function runStep(bot, step, { skip = new Set() } = {}) {
  switch (step.action) {
    case "gather": {
      let r = { ok: false, reason: "no source" };
      for (const name of step.names) {
        r = await gather(bot, name, { skip, radius: step.radius ?? 60 });
        if (r.ok || !/no .* within/.test(r.reason ?? "")) break;
      }
      if (!r.ok && step.hunt?.length) r = await huntNamed(bot, step.hunt, skip);
      return r;
    }
    case "hunt": return huntNamed(bot, step.names, skip);
    case "tend": return tendBrazier(bot, step.building);
    case "refine": return refineAt(bot, step.building, step.rule);
    case "pantry": return pantry(bot, step.building, step.op, step.items);
    case "repair": {
      const before = await call(SERVER, "bot_repair", { user: bot.name });
      await call(SERVER, "bot_repair", { user: bot.name, action: "repair", slots: step.slots });
      await new Promise((r) => setTimeout(r, 1500));
      const after = await call(SERVER, "bot_repair", { user: bot.name });
      const fixed = step.slots.filter((s) => (after.slots.find((x) => x.slot === s)?.durability ?? 0) > (before.slots.find((x) => x.slot === s)?.durability ?? 0));
      return { ok: fixed.length > 0, repaired: fixed, ...(fixed.length ? {} : { reason: "repair refused (materials?)" }) };
    }
    case "boss": return bossFight(bot, step.boss);
    case "fuel": return fuelHeart(bot, step.building);
    case "travel": {
      const trip = await travel(bot, step.to, { stop: 4, runPast: true });
      return trip.interrupted ? { ok: false, reason: `trip interrupted: ${trip.interrupted}`, interrupted: true } : { ok: !!trip.arrived, ...trip };
    }
    case "craft": {
      if (!step.station) return craft(bot, step.recipe);
      // A station craft is refused unless the bot is interacting with that station (its InteractBuff,
      // bot-castle-building.md section 4): open it, then send the craft naming its client entity.
      const opened = await interactWith(bot, step.station);
      if (!opened.ok) return opened;
      return craft(bot, step.recipe, opened.entity);
    }
    case "equip": {
      const r = await call(SERVER, "equip", { user: bot.name, slot: step.bagSlot });
      return { ok: !!r?.equipped, item: step.item, result: r };
    }
    case "build": {
      const me = await bot.self();
      if (Math.hypot(step.spot.x - me.x, step.spot.z - me.z) > BUILD_RANGE) {
        const trip = await travel(bot, { x: step.spot.x + 4, z: step.spot.z + 4 }, { stop: 3 });
        if (trip.interrupted) return { ok: false, reason: `trip interrupted: ${trip.interrupted}`, interrupted: true };
        if (!trip.arrived) return { ok: false, reason: "could not reach the castle", trip };
      }
      const cleared = await clearFootprint(bot, step.spot, { half: step.kind === "floor" ? 2.5 : 1.2 });
      if (cleared.interrupted) return { ok: false, interrupted: cleared.interrupted, cleared };
      const placed = await placeStructure(bot, step.prefab, step.spot, { rotation: step.spot.rotation ?? 0 });
      placed.cleared = cleared;
      // A wooden coffin becomes the respawn point once bound; interacting ("Bind Coffin") is the
      // vanilla way and cheap insurance (placement probably binds it already, research section 3.2).
      if (placed.ok && step.kind === "coffin") placed.bind = await interactWith(bot, { name: placed.name, ...placed.at });
      return placed;
    }
    default: return { ok: false, reason: `unknown step ${step.action}` };
  }
}
