// Unit tests for the persona goal planners (goals.mjs) and the castle layout. No bridge or game.
import test from "node:test";
import assert from "node:assert/strict";
import { PERSONAS, slotOf, tierOf, planGear, planCastle, planGoals, missingFor, acquire } from "../goals.mjs";
import { castleLayout, FLOOR_ORDER } from "../layout.mjs";
import { decide, newState } from "../brain.mjs";

const BONE = { item: "Item_Ingredient_Bone", guid: 1 };
const HIDE = { item: "Item_Ingredient_RuggedHide", guid: 2 };
const STONE = { item: "Item_Ingredient_Stone", guid: 3 };
const bag = (...rows) => ({ items: rows.map(([r, amount]) => ({ ...r, amount })), occupied: [] });
const recipe = (out, reqs, extra = {}) => ({
  recipe: out.replace(/^Item_/, "Recipe_"), unlocked: true, characterCraftable: true, stations: [],
  outputs: [{ item: out, amount: 1 }], requirements: reqs.map(([r, amount]) => ({ ...r, amount })), ...extra,
});

test("slots and tiers from item names", () => {
  assert.equal(slotOf("Item_Weapon_Sword_T01_Bone"), "weapon:Sword");
  assert.equal(slotOf("Item_Gloves_T01_Bone"), "gloves");
  assert.equal(slotOf("Item_Chest_T02_Bone_Reinforced"), "chest");
  assert.equal(slotOf("Item_Ingredient_Bone"), null);
  assert.equal(tierOf("Item_Chest_T02_Bone_Reinforced"), 2);
});

test("gear: wear an owned upgrade before crafting anything", () => {
  const k = { equipped: [], bag: { items: [], occupied: [{ slot: 4, item: "Item_Weapon_Sword_T01_Bone" }] }, recipes: [] };
  const s = planGear(k, PERSONAS.warrior);
  assert.equal(s.action, "equip");
  assert.equal(s.bagSlot, 4);
});

test("gear: a worn weapon is not swapped for an equal-tier non-preferred type", () => {
  const k = { equipped: [{ slot: "weapon", item: "Item_Weapon_Sword_T01_Bone" }], bag: { items: [], occupied: [{ slot: 2, item: "Item_Weapon_Axe_T01_Bone" }] }, recipes: [] };
  assert.equal(planGear(k, PERSONAS.warrior), null);
  assert.equal(planGear(k, PERSONAS.builder).action, "equip"); // the builder prefers axes
});

test("gear: affordable unlocked in-hand recipe -> craft; unaffordable -> acquire the missing ingredient", () => {
  const gloves = recipe("Item_Gloves_T01_Bone", [[BONE, 16], [HIDE, 32]]);
  const rich = { equipped: [], bag: bag([BONE, 20], [HIDE, 40]), recipes: [gloves] };
  const c = planGear(rich, PERSONAS.warrior);
  assert.equal(c.action, "craft");
  assert.equal(c.recipe, "Recipe_Gloves_T01_Bone");
  const poor = { equipped: [], bag: bag([HIDE, 40]), recipes: [gloves] };
  const g = planGear(poor, PERSONAS.warrior);
  assert.equal(g.action, "gather");
  assert.match(g.why, /Bone x16/);
});

test("gear: a station recipe needs that station in the castle", () => {
  const chest = recipe("Item_Chest_T02_Bone_Reinforced", [[BONE, 1]], { characterCraftable: false, stations: [{ name: "TM_CraftingStation_SimpleCraftingBench" }] });
  const k = { equipped: [], bag: bag([BONE, 5]), recipes: [chest] };
  assert.equal(planGear(k, PERSONAS.warrior), null);
  k.castle = { buildings: [{ name: "TM_CraftingStation_SimpleCraftingBench", x: 1, z: 2 }] };
  const s = planGear(k, PERSONAS.warrior);
  assert.equal(s.action, "craft");
  assert.deepEqual(s.station, { name: "TM_CraftingStation_SimpleCraftingBench", x: 1, z: 2 });
});

test("gear: nothing better to make -> null", () => {
  const sword = recipe("Item_Weapon_Sword_T01_Bone", [[BONE, 16]]);
  const k = { equipped: [{ slot: "weapon", item: "Item_Weapon_Sword_T01_Bone" }], bag: bag(), recipes: [sword] };
  assert.equal(planGear(k, PERSONAS.warrior), null);
});

test("castle: builds unlocked blueprints in plan order at layout spots, acquiring what is missing", () => {
  const heart = { x: -1142.5, z: -1722.5 };
  const floor = { guid: 10, name: "TM_Castle_Floor_Outdoor_Plain01", cost: [{ ...STONE, amount: 10 }] };
  const k = { castle: { heart, buildings: [{ prefab: -485210554, name: "TM_BloodFountain_CastleHeart" }] }, blueprints: [floor], bag: bag([STONE, 30]) };
  const s = planCastle(k, PERSONAS.balanced, castleLayout);
  assert.equal(s.action, "build");
  assert.equal(s.kind, "floor");
  assert.deepEqual([s.spot.x, s.spot.z], [heart.x + FLOOR_ORDER[0][0], heart.z + FLOOR_ORDER[0][1]]);
  k.bag = bag();
  assert.equal(planCastle(k, PERSONAS.balanced, castleLayout).action, "gather");
  // a refused spot is skipped
  k.bag = bag([STONE, 30]);
  k.refused = new Set(["floor:0"]);
  assert.equal(planCastle(k, PERSONAS.balanced, castleLayout).index, 1);
});

test("castle: nothing unlocked or no heart -> null", () => {
  assert.equal(planCastle({ castle: null }, PERSONAS.builder, castleLayout), null);
  const k = { castle: { heart: { x: 0, z: 0 }, buildings: [] }, blueprints: [{ guid: -485210554, name: "TM_BloodFountain_CastleHeart", cost: [] }] };
  assert.equal(planCastle(k, PERSONAS.builder, castleLayout), null);
});

test("layout: floors grow from the heart's center block, each touching one already built", () => {
  const h = { x: -1142.5, z: -1722.5 };
  const built = [[0, 0]];
  for (let i = 0; i < FLOOR_ORDER.length; i++) {
    const p = castleLayout("floor", i, h);
    assert.equal(((p.x % 5) + 5) % 5, 2.5);
    const [dx, dz] = [p.x - h.x, p.z - h.z];
    assert.ok(built.some(([x, z]) => Math.abs(x - dx) + Math.abs(z - dz) === 5), `floor ${i} touches a built block`);
    built.push([dx, dz]);
  }
  assert.deepEqual(FLOOR_ORDER.slice(0, 4), [[5, 0], [0, -5], [-5, 0], [0, 5]]); // live rule: next to the center
  assert.equal(castleLayout("floor", FLOOR_ORDER.length, h), null);
  // objects stand on floors among the first 12 (the starter plan), clear of the heart model
  for (const kind of ["coffin", "stash", "brazier", "workbench"]) {
    const o = castleLayout(kind, 0, h);
    const idx = FLOOR_ORDER.findIndex(([x, z]) => x === o.x - h.x && z === o.z - h.z);
    assert.ok(idx >= 0 && idx < 12, `${kind} on a starter floor`);
    assert.ok(Math.max(Math.abs(o.x - h.x), Math.abs(o.z - h.z)) >= 10);
  }
  assert.equal(castleLayout("coffin", 1, h), null);
  const e = castleLayout("wall", 0, h); // east edge, runs along Z: x = 0, z = 2.5 (mod 5), rotation 0
  assert.deepEqual([((e.x % 5) + 5) % 5, ((e.z % 5) + 5) % 5, e.rotation], [0, 2.5, 0]);
  const w = castleLayout("wall", 3, h); // south edge, runs along X
  assert.deepEqual([((w.x % 5) + 5) % 5, ((w.z % 5) + 5) % 5, w.rotation], [2.5, 0, 1]);
});

test("personas order the goals differently", () => {
  const k = {
    journal: { claims: 1, next: null }, hasHeart: false, heartAllowed: true,
    equipped: [], bag: { items: [], occupied: [{ slot: 0, item: "Item_Weapon_Sword_T01_Bone" }] }, recipes: [],
  };
  assert.equal(planGoals(k, PERSONAS.balanced).action, "journal");
  assert.equal(planGoals(k, PERSONAS.builder).action, "buildHeart");
  assert.equal(planGoals(k, PERSONAS.warrior).action, "equip");
});

test("brain: a builder with a heart and materials builds; the stock rotation follows the persona", () => {
  const st = {
    ...newState(), persona: "builder", now: 1, vitals: { health: 100, maxHealth: 100, blood: 80, sun: {} },
    hostiles: [], weakUnits: [], drops: [], journal: null, hasHeart: true,
    castle: { heart: { x: 2.5, z: 2.5 }, buildings: [] }, blueprints: [{ guid: 10, name: "TM_Castle_Floor_Outdoor_Plain01", cost: [] }],
    bag: bag(), equipped: [], recipes: [],
  };
  const d = decide(st);
  assert.equal(d.action, "goal");
  assert.equal(d.step.action, "build");
  const idle = decide({ ...st, blueprints: [] });
  assert.equal(idle.action, "gather");
  assert.equal(idle.name, "Tree"); // the builder's stock starts with wood
});

test("helpers", () => {
  assert.deepEqual(missingFor([{ ...BONE, amount: 5 }], bag([BONE, 2])), { item: "Item_Ingredient_Bone", guid: 1, short: 3 });
  assert.equal(missingFor([{ ...BONE, amount: 5 }], bag([BONE, 5])), null);
  assert.equal(acquire("Item_Ingredient_Stone").action, "gather");
  assert.equal(acquire("Item_Unknown_Thing"), null);
});

test("journal: a craft subtask waits until its recipe is learned", async () => {
  const { planJournal } = await import("../progression.mjs");
  const quests = { achievements: [{ achievement: 1, name: "Armor", state: "in progress", subtasks: [
    { subtask: 11, name: "Journal_SubTask_Craft_Armor_BoneGloves", type: "Craft", progress: 0, required: 1, trackedItem: { name: "Item_Gloves_T01_Bone" } },
    { subtask: 12, name: "Journal_SubTask_OpenMap", type: "OpenedMap", progress: 0, required: 1 },
  ] }] };
  assert.equal(planJournal(quests).next.subtask.subtask, 11); // unknown learned set: try it
  assert.equal(planJournal(quests, new Set(), new Set(), { craftable: new Set() }).next.subtask.subtask, 12);
  assert.equal(planJournal(quests, new Set(), new Set(), { craftable: new Set(["Item_Gloves_T01_Bone"]) }).next.subtask.subtask, 11);
});

test("gear: AlwaysUnlocked cosmetics are never planned", () => {
  const hat = recipe("Item_Headgear_T01_Blackfang", [], { alwaysUnlocked: true });
  const dlc = recipe("Item_Chest_T0X_PMK", [], { alwaysUnlocked: true });
  assert.equal(planGear({ equipped: [], bag: bag(), recipes: [hat, dlc] }, PERSONAS.warrior), null);
});

test("home: walk back to a far castle, leave the graveyard without one, else nothing", async () => {
  const { planHome } = await import("../goals.mjs");
  const heart = { x: -1142.5, z: -1722.5 };
  const far = planHome({ self: { x: 70, z: -2986 }, castle: { heart } });
  assert.equal(far.action, "travel");
  assert.deepEqual(far.to, { x: heart.x, z: heart.z + 12 });
  assert.equal(planHome({ self: { x: -1140, z: -1700 }, castle: { heart } }), null);
  const lost = planHome({ self: { x: 70, z: -2986 }, castle: null, frontier: { x: -1017, z: -1874 } });
  assert.deepEqual(lost.to, { x: -1017, z: -1874 });
  assert.equal(planHome({ self: { x: -1100, z: -1700 }, castle: null, frontier: { x: 0, z: 0 } }), null);
  // home outranks everything, even for a rotating persona
  const k = { self: { x: 70, z: -2986 }, castle: { heart }, journal: { claims: 1, next: null } };
  for (const turn of [0, 1, 2, 3]) assert.equal(planGoals(k, PERSONAS.balanced, { turn }).goal, "home");
});

test("day: trips go day or night (shade-hopping by day); daylight keeps other work sun-safe", async () => {
  const { planHome, sunSafe } = await import("../goals.mjs");
  const heart = { x: -1142.5, z: -1722.5 };
  const base = { self: { x: 70, z: -2986 }, castle: { heart } }; // ~1750 m from home
  assert.equal(planHome({ ...base, day: { sunOut: false, dawnIn: 1000 } }).action, "travel");
  assert.match(planHome({ ...base, day: { sunOut: false, dawnIn: 200 } }).why, /hopping shade/); // would end in daylight
  assert.match(planHome({ ...base, day: { sunOut: true, dawnIn: 0 } }).why, /hopping shade/);
  assert.equal(sunSafe({ action: "travel" }, {}), true);
  const k = { self: { x: 0, z: 0 }, journal: { claims: 0, next: { plan: { action: "gather" } } } };
  assert.equal(sunSafe({ action: "craft" }, k), true);
  assert.equal(sunSafe({ action: "craft", station: { name: "x" } }, k), false);
  assert.equal(sunSafe({ action: "build", spot: { x: 30, z: 40 } }, k), true);
  assert.equal(sunSafe({ action: "build", spot: { x: 300, z: 40 } }, k), false);
  assert.equal(sunSafe({ action: "gather" }, k), false);
  assert.equal(sunSafe({ action: "journal" }, k), false);
});

test("brain: daylight shade-hopping; before dawn it heads for cover", () => {
  const snap = {
    ...newState(), persona: "warrior", now: 1, vitals: { health: 100, maxHealth: 100, blood: 80, sun: { inSun: false, charge: 0 } },
    hostiles: [], weakUnits: [], drops: [], journal: null, hasHeart: true, bag: bag(), equipped: [], recipes: [],
    self: { x: 0, y: 0, z: 0 },
  };
  const day = { isDay: true, sunDamage: true };
  assert.equal(decide({ ...snap, daytime: day }).action, "seekShade"); // no shade known: find some first
  const shade = { x: 3, y: 0, z: 4 };
  const work = decide({ ...snap, daytime: day, shade });
  assert.equal(work.action, "gather"); // works from the shade...
  assert.equal(work.radius, 15); // ...within reach of it (the sun budget tether)
  const reset = decide({ ...snap, daytime: day, shade, vitals: { ...snap.vitals, sun: { inSun: false, charge: 0.4 } } });
  assert.equal(reset.action, "shelter"); // back in shade: let the exposure reset first
  assert.equal(decide({ ...snap, daytime: { isDay: false, sunDamage: false, secondsUntilDay: 30 } }).action, "shelter");
  assert.notEqual(decide({ ...snap, daytime: { isDay: false, sunDamage: false, secondsUntilDay: 600 } }).action, "shelter");
});

test("castle upkeep: a built mist brazier is fed when due and bones are in the bag", () => {
  const brazier = { name: "TM_MistBrazier_01", entity: "9:1", x: 1, z: 2 };
  const k = {
    castle: { heart: { x: 2.5, z: 2.5 }, buildings: [brazier] }, blueprints: [],
    bag: bag([{ item: "Item_Ingredient_Bone", guid: 1 }, 50]), brazierDue: true,
  };
  const s = planCastle(k, PERSONAS.builder, castleLayout);
  assert.equal(s.action, "tend");
  assert.equal(s.building, brazier);
  assert.equal(planCastle({ ...k, brazierDue: false }, PERSONAS.builder, castleLayout), null);
  assert.equal(planCastle({ ...k, bag: bag() }, PERSONAS.builder, castleLayout), null);
  // it takes every bone, so a small supply is kept for crafting
  assert.equal(planCastle({ ...k, bag: bag([{ item: "Item_Ingredient_Bone", guid: 1 }, 5]) }, PERSONAS.builder, castleLayout), null);
});

test("brain: leaving the graveyard outranks non-desperate feeding", () => {
  const snap = {
    ...newState(), persona: "balanced", now: 1, hostiles: [], weakUnits: [], drops: [], journal: null,
    bag: bag(), equipped: [], recipes: [], self: { x: 70, y: 5, z: -2986 }, frontier: { x: -1017, z: -1874 },
    daytime: { isDay: false, sunDamage: false, secondsUntilDay: 670 },
  };
  const hungry = decide({ ...snap, vitals: { health: 125, maxHealth: 125, blood: 9, sun: {} } });
  assert.equal(hungry.action, "goal");
  assert.equal(hungry.step.action, "travel");
  const starving = decide({ ...snap, self: { x: -1100, y: 0, z: -1700 }, castle: { heart: { x: 70, z: -2986 } }, vitals: { health: 125, maxHealth: 125, blood: 2, sun: {} } });
  assert.equal(starving.action, "feed");
});

test("brain: in the starting graveyard even a starving bot leaves", () => {
  const snap = {
    ...newState(), persona: "warrior", now: 1, hostiles: [], weakUnits: [], drops: [], journal: null,
    bag: bag(), equipped: [], recipes: [], self: { x: 70, y: 5, z: -2986 }, frontier: { x: -1017, z: -1874 },
    daytime: { isDay: false, sunDamage: false, secondsUntilDay: 670 }, vitals: { health: 125, maxHealth: 125, blood: 1, sun: {} },
  };
  assert.equal(decide(snap).step?.action, "travel");
});

test("review fixes: engaged always fights back at its own threshold; written-off targets are forgiven later", () => {
  const skel = { entity: "7:1", name: "CHAR_Undead_SkeletonSoldier_Withered", distance: 1.6, health: 60 };
  const snap = {
    ...newState(), now: 100000, vitals: { health: 30, maxHealth: 125, blood: 50, sun: {} },
    hostiles: [skel], weakUnits: [], drops: [], journal: null, bag: bag(), equipped: [], recipes: [],
    failures: { "7:1": 5 }, failureAt: { "7:1": 99000 },
  };
  const d = decide(snap);
  assert.equal(d.action, "hunt");
  assert.equal(d.fleeBelow, 0.15); // the fight does not retreat at 35% while the rule fights to 15%
  const far = { ...skel, distance: 10, health: 20 };
  const healthy = { ...snap, vitals: { ...snap.vitals, health: 120 }, hostiles: [far] };
  assert.notEqual(decide(healthy).action, "hunt"); // recently written off: not hunted as a guard
  assert.equal(decide({ ...healthy, now: 99000 + 61000 }).action, "hunt"); // a minute later: fair game again
});

test("review fixes: a castle build that needs a trip into daylight waits", () => {
  const floor = { guid: 10, name: "TM_Castle_Floor_Outdoor_Plain01", cost: [] };
  const k = { castle: { heart: { x: 0, z: 0 }, buildings: [] }, blueprints: [floor], bag: bag(), self: { x: 500, z: 0 } };
  assert.equal(planCastle({ ...k, day: { sunOut: false, dawnIn: 60 } }, PERSONAS.builder, castleLayout), null);
  assert.equal(planCastle({ ...k, day: { sunOut: false, dawnIn: 600 } }, PERSONAS.builder, castleLayout).action, "build");
  assert.equal(planCastle({ ...k, self: { x: 20, z: 0 }, day: { sunOut: false, dawnIn: 60 } }, PERSONAS.builder, castleLayout).action, "build"); // in range
});

test("rest: a hurt bot with nobody near recovers to full before going on", () => {
  const snap = {
    ...newState(), persona: "warrior", now: 1, hostiles: [], weakUnits: [], drops: [], journal: null,
    bag: bag(), equipped: [], recipes: [], self: { x: 0, y: 0, z: 0 }, vitals: { health: 60, maxHealth: 125, blood: 50, sun: {} },
  };
  assert.equal(decide(snap).action, "rest");
  const better = { ...snap, vitals: { ...snap.vitals, health: 110 } };
  assert.equal(decide({ ...better, resting: true }).action, "rest"); // 88%: keep resting to full
  assert.notEqual(decide(better).action, "rest"); // 88% and not resting: go on
  const busy = { ...snap, hostiles: [{ entity: "1:1", name: "CHAR_Undead_SkeletonSoldier_Withered", distance: 10, health: 60 }] };
  assert.notEqual(decide(busy).action, "rest"); // not with a hostile close
});

test("castle upkeep: a decaying heart is fueled with carried blood essence", () => {
  const heart = { name: "TM_BloodFountain_CastleHeart", prefab: -485210554, entity: "1:1", x: 2.5, z: 2.5 };
  const k = { castle: { heart: { x: 2.5, z: 2.5 }, buildings: [heart], decaying: true }, blueprints: [],
    bag: bag([{ item: "Item_BloodEssence_T01", guid: 862477668 }, 20]) };
  const s = planCastle(k, PERSONAS.builder, castleLayout);
  assert.equal(s.action, "fuel");
  assert.equal(planCastle({ ...k, castle: { ...k.castle, decaying: false } }, PERSONAS.builder, castleLayout), null);
});

test("bosses: hunted only at gear >= level - 3, full health, blood and night; avoided when too strong", async () => {
  const { planBoss, bossTooStrong } = await import("../boss.mjs");
  const fit = { vitals: { health: 125, maxHealth: 125, blood: 80 }, self: { x: -1150, z: -1650 }, day: { sunOut: false, dawnIn: 600 }, vbloods: new Set() };
  assert.equal(planBoss({ ...fit, gear: 6 }), null);
  const s = planBoss({ ...fit, gear: 13 });
  assert.equal(s.boss.label, "Alpha Wolf");
  assert.equal(planBoss({ ...fit, gear: 13, vbloods: new Set(["CHAR_Forest_Wolf_VBlood"]) }), null); // next boss needs 17
  assert.equal(planBoss({ ...fit, gear: 17, vbloods: new Set(["CHAR_Forest_Wolf_VBlood"]) }).boss.label, "Keely"); // the Tannery first: players take bosses for the unlock they need next
  assert.equal(planBoss({ ...fit, gear: 13, vitals: { health: 125, maxHealth: 125, blood: 60 } }), null);
  assert.equal(planBoss({ ...fit, gear: 13, day: { sunOut: true, dawnIn: 0 } }), null);
  assert.equal(planBoss({ ...fit, gear: 13, vitals: { health: 60, maxHealth: 125, blood: 80 } }), null);
  assert.ok(bossTooStrong("CHAR_Forest_Wolf_VBlood", 6));
  assert.ok(!bossTooStrong("CHAR_Forest_Wolf_VBlood", 13));
  assert.ok(!bossTooStrong("CHAR_Forest_Wolf", 0)); // an ordinary wolf is not a boss
});

test("brain: an underleveled bot flees a nearby boss even at full health", () => {
  const snap = {
    ...newState(), persona: "warrior", now: 1, weakUnits: [], drops: [], journal: null, recipes: [], equipped: [],
    bag: { ...bag(), gear: { total: 6 } }, self: { x: 0, y: 0, z: 0 }, vitals: { health: 125, maxHealth: 125, blood: 80, sun: {} },
    hostiles: [{ entity: "5:1", name: "CHAR_Forest_Wolf_VBlood", distance: 18, health: 416 }],
  };
  assert.equal(decide(snap).action, "flee");
});

test("brain: clears an isolated weak hostile, not one standing in a group", () => {
  const snap = {
    ...newState(), persona: "warrior", now: 1, weakUnits: [], drops: [], journal: null, recipes: [], equipped: [],
    bag: { ...bag(), gear: { total: 6 } }, self: { x: 0, y: 0, z: 0 }, vitals: { health: 125, maxHealth: 125, blood: 80, sun: {} },
  };
  const a = { entity: "1:1", name: "CHAR_Undead_SkeletonSoldier_Withered", distance: 10, health: 31, x: 10, z: 0 };
  const b = { entity: "2:1", name: "CHAR_Undead_SkeletonSoldier_Withered", distance: 12, health: 31, x: 12, z: 0 };
  assert.equal(decide({ ...snap, hostiles: [a] }).action, "hunt");
  assert.notEqual(decide({ ...snap, hostiles: [a, b] }).action, "hunt");
});

test("gear: the bone ring is a grimoire-slot item and gets crafted and worn", () => {
  assert.equal(slotOf("Item_MagicSource_General_T01_BoneRing"), "grimoire");
  const ring = recipe("Item_MagicSource_General_T01_BoneRing", [[BONE, 10]]);
  assert.equal(planGear({ equipped: [], bag: bag([BONE, 20]), recipes: [ring] }, PERSONAS.warrior).action, "craft");
  const k = { equipped: [], bag: { items: [], occupied: [{ slot: 3, item: "Item_MagicSource_General_T01_BoneRing" }] }, recipes: [] };
  assert.equal(planGear(k, PERSONAS.warrior).action, "equip");
});

test("levels: flee a unit far above gear, never pick a fight above gear + 2", () => {
  const base = {
    ...newState(), persona: "warrior", now: 1, drops: [], journal: null, recipes: [], equipped: [], weakUnits: [],
    bag: { ...bag(), gear: { total: 6 } }, self: { x: 0, y: 0, z: 0 }, vitals: { health: 125, maxHealth: 125, blood: 80, sun: {} },
  };
  const bear = { entity: "9:1", name: "CHAR_Forest_Bear_Standard", distance: 12, health: 120, level: 18, x: 12, z: 0 };
  assert.equal(decide({ ...base, hostiles: [bear] }).action, "flee");
  const wolf = { entity: "8:1", name: "CHAR_Forest_Wolf", distance: 10, health: 40, level: 10, x: 10, z: 0 };
  assert.notEqual(decide({ ...base, hostiles: [wolf] }).action, "hunt"); // level 10 > 6 + 2
  const withered = { ...wolf, name: "CHAR_Undead_SkeletonSoldier_Withered", level: 1 };
  assert.equal(decide({ ...base, hostiles: [withered] }).action, "hunt");
});

test("weapons: combo timing from the prefab data and the right weapon per target", async () => {
  const { weaponProfile, bestWeaponFor } = await import("../skills.mjs");
  const sword = weaponProfile("Item_Weapon_Sword_T01_Bone");
  assert.deepEqual(sword.steps.map((x) => x.ms), [400, 400, 1000]); // live calibration: 6/8 casts at 400 ms
  assert.equal(weaponProfile("Item_Weapon_Mace_T01_Bone").steps[0].ms, 600);
  assert.ok(weaponProfile("Item_Weapon_Crossbow_T01_Bone").ranged);
  assert.ok(weaponProfile("Item_Weapon_Spear_T01_Bone").reach > sword.reach);
  const bag = { occupied: [{ slot: 1, item: "Item_Weapon_Reaper_T01_Bone" }, { slot: 2, item: "Item_Weapon_Spear_T01_Bone" }], equipped: [{ slot: "weapon", item: "Item_Weapon_Sword_T01_Bone" }] };
  assert.equal(bestWeaponFor("CHAR_Undead_RottingGhoul", bag).item, "Item_Weapon_Reaper_T01_Bone");
  assert.equal(bestWeaponFor("CHAR_Forest_Wolf", bag).item, "Item_Weapon_Spear_T01_Bone");
  assert.equal(bestWeaponFor("CHAR_Bandit_Thug", bag), null); // the sword it wears is right
});

test("rest: a starving bot feeds instead of resting (no regeneration at blood 0)", () => {
  const snap = {
    ...newState(), persona: "trainer", now: 1, hostiles: [], weakUnits: [], drops: [], journal: null,
    bag: bag(), equipped: [], recipes: [], self: { x: 0, y: 0, z: 0 }, vitals: { health: 1, maxHealth: 125, blood: 0, sun: {} },
  };
  assert.equal(decide(snap).action, "feed");
});

test("blood: carried food first, critical outranks trips outside the graveyard, stock rats when safe", () => {
  const rat = { item: "Item_Consumable_Eat_Rat", guid: 5 };
  const base = {
    ...newState(), persona: "warrior", now: 1, hostiles: [], weakUnits: [], drops: [], journal: null, recipes: [], equipped: [],
    self: { x: -1100, y: 0, z: -1900 }, vitals: { health: 125, maxHealth: 125, blood: 20, sun: {} },
    castle: { heart: { x: -1172.5, z: -1607.5 }, buildings: [] }, daytime: { isDay: false, sunDamage: false, secondsUntilDay: 700 },
  };
  assert.equal(decide({ ...base, bag: bag([rat, 1]) }).carried, true); // eat what it carries
  const starving = decide({ ...base, bag: bag(), vitals: { ...base.vitals, blood: 4 } });
  assert.equal(starving.action, "feed"); // not the trip home
  const critter = { entity: "7:1", name: "CHAR_Critter_Rat", distance: 8, x: -1100, z: -1892, health: 1 };
  const atHome = { ...base, self: { x: -1170, y: 0, z: -1620 } }; // far from home it would walk home first
  const critterHome = { ...critter, x: -1170, z: -1612 };
  const stock = decide({ ...atHome, bag: bag(), vitals: { ...base.vitals, blood: 90 }, critters: [critterHome] });
  assert.equal(stock.action, "stock"); // full, but its reserve is empty
  assert.equal(stock.eat, false);
  assert.notEqual(decide({ ...atHome, bag: bag([rat, 2]), vitals: { ...base.vitals, blood: 90 }, critters: [critterHome] }).action, "stock");
});

test("castle upkeep: a sawmill visit when carrying 300+ wood, or to collect every 10 min", () => {
  const heart = { x: 0, z: 0 };
  const WOOD = { item: "Item_Ingredient_Wood_Standard", guid: 7 };
  const k = { castle: { heart, buildings: [{ prefab: -485210554, name: "TM_BloodFountain_CastleHeart" }, { name: "TM_RefinementStation_Sawmill", x: 10, z: 5 }] },
    blueprints: [], bag: bag([WOOD, 340]), now: 10 * 60000, refineLast: { TM_RefinementStation_Sawmill: 6 * 60000 } };
  const s = planCastle(k, PERSONAS.builder, castleLayout);
  assert.equal(s.action, "refine"); // loads: 340 wood, last visit 4 min ago
  assert.match(s.why, /Sawmill/);
  assert.equal(planCastle({ ...k, bag: bag([WOOD, 50]) }, PERSONAS.builder, castleLayout), null); // nothing to load, collected 4 min ago
  assert.equal(planCastle({ ...k, bag: bag([WOOD, 50]), now: 17 * 60000 }, PERSONAS.builder, castleLayout).action, "refine"); // collect after 10 min
});

test("repairs: broken or worn gear is repaired first when bones allow, else bones are gathered", async () => {
  const { planRepair } = await import("../goals.mjs");
  const BONES = { item: "Item_Ingredient_Bone", guid: 1 };
  const durability = [{ slot: "chest", item: "Item_Chest_T01_Bone", durability: 0, max: 346, broken: true }, { slot: "weapon", item: "Item_Weapon_Sword_T01_Bone", durability: 600, max: 817, broken: false },
    { slot: "legs", item: "Item_Legs_T01_Bone", durability: 18, max: 346, broken: false }];
  assert.deepEqual(planRepair({ durability, bag: bag([BONES, 40]) }).slots, ["chest", "legs"]); // 16 + 16
  assert.deepEqual(planRepair({ durability, bag: bag([BONES, 20]) }).slots, ["chest"]);
  assert.equal(planRepair({ durability, bag: bag() }).action, "gather");
  assert.equal(planRepair({ durability: [{ slot: "weapon", item: "Item_Weapon_Sword_T01_Bone", durability: 600, max: 817 }], bag: bag() }), null);
  // non-bone gear repairs with other materials: not planned with bones (it would loop on refusals)
  assert.equal(planRepair({ durability: [{ slot: "weapon", item: "Item_Weapon_Sword_T02_Copper", durability: 0, max: 900, broken: true }], bag: bag([BONES, 99]) }), null);
  const s = planGoals({ durability, bag: bag([BONES, 40]) }, PERSONAS.builder, { layout: castleLayout });
  assert.equal(s.action, "repair");
});

test("walls sit on the outer edges of real floors: east/west rotation 1, north/south 0, east first", async () => {
  const { wallSpotsFromFloors } = await import("../layout.mjs");
  const h = { x: -1142.5, z: -1722.5 };
  const floors = [[5, 0], [0, -5], [-5, 0], [0, 5], [5, -5]].map(([dx, dz]) => ({ x: h.x + dx, z: h.z + dz }));
  const spots = wallSpotsFromFloors(floors, h);
  assert.deepEqual(spots[0], { x: -1135, z: -1722.5, rotation: 1 }); // east edge of the (5,0) floor (live-checked strip)
  assert.ok(spots.every((s) => (Math.abs(((s.x % 5) + 5) % 5) < 1e-9) === (s.rotation === 1))); // x on a grid line <=> east/west
  assert.ok(!spots.some((s) => Math.hypot(s.x - (h.x + 2.5), s.z - h.z) < 0.1)); // no wall between a floor and the heart
});

test("gear: a persona with no weapon preference (the trainer) never equips weapons", () => {
  const k = { equipped: [{ slot: "weapon", item: "Item_Weapon_Spear_T01_Bone" }], bag: { items: [], occupied: [{ slot: 2, item: "Item_Weapon_Reaper_T03_Copper" }] }, recipes: [] };
  assert.equal(planGear(k, PERSONAS.trainer), null);
});

test("planGear climbs to the workbench's reinforced bone weapon, making a spare bone one to upgrade", () => {
  const bench = { name: "TM_CraftingStation_SimpleCraftingBench", x: 0, z: 0 };
  const rec = (item, reqs, extra = {}) => ({ recipe: item, unlocked: true, outputs: [{ item }], stations: [{ name: bench.name }], requirements: reqs.map(([i, n]) => ({ item: i, guid: i, amount: n })), ...extra });
  const recipes = [
    rec("Item_Weapon_Mace_T01_Bone", [["Item_Ingredient_Bone", 16]], { characterCraftable: true }),
    rec("Item_Weapon_Mace_T02_Bone_Reinforced", [["Item_Weapon_Mace_T01_Bone", 1], ["Item_Ingredient_Plank", 4], ["Item_Ingredient_Stone", 128]], { alwaysUnlocked: true }),
    rec("Item_Weapon_Mace_T03_Copper", [["Item_Ingredient_Mineral_CopperIngot", 16]], { alwaysUnlocked: true }),
  ];
  const k = { recipes, castle: { buildings: [bench] }, equipped: [{ slot: "weapon", item: "Item_Weapon_Mace_T01_Bone" }],
    bag: { items: [{ item: "Item_Ingredient_Bone", guid: "Item_Ingredient_Bone", amount: 20 }, { item: "Item_Ingredient_Plank", guid: "Item_Ingredient_Plank", amount: 4 }, { item: "Item_Ingredient_Stone", guid: "Item_Ingredient_Stone", amount: 200 }], occupied: [] } };
  const s = planGear(k, { ...PERSONAS.builder, weapons: ["Mace"] });
  assert.equal(s.action, "craft");
  assert.equal(s.item, "Item_Weapon_Mace_T01_Bone"); // the spare for the upgrade
  k.bag.items.push({ item: "Item_Weapon_Mace_T01_Bone", guid: "Item_Weapon_Mace_T01_Bone", amount: 1 });
  assert.equal(planGear(k, { ...PERSONAS.builder, weapons: ["Mace"] }).item, "Item_Weapon_Mace_T02_Bone_Reinforced");
});
