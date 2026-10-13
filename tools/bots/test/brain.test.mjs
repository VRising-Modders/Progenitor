// Unit tests for the pure parts of the bot brain: decide() priorities and the journal/build planners.
// No bridge, game or network: run with `node --test "tools/bots/test/*.test.mjs"`.
import test from "node:test";
import assert from "node:assert/strict";
import {
  decide, FEED_BLOOD, FEED_DESPERATE_BLOOD, MAX_HEART_ATTEMPTS, MAX_TARGET_FAILURES,
} from "../brain.mjs";
import { planSubtask, planJournal, snapToCell, inventoryGain, inventoryDelta, HOSTILE } from "../progression.mjs";

const NOW = 1_000_000;
const base = (over = {}) => ({
  now: NOW,
  vitals: { health: 100, maxHealth: 100, dead: false, blood: 80, sun: { inSun: false, charge: 0, burning: false } },
  self: { x: 0, y: 0, z: 0 },
  inCrypt: false,
  daytime: { isDay: false, sunDamage: false },
  hostiles: [], weakUnits: [], drops: [], journal: null,
  hasHeart: true, heartAttempts: 0, cooldowns: {}, failures: {}, gatherTurn: 0,
  ...over,
});
const vit = (over) => ({ ...base().vitals, ...over });
const skel = (distance, extra = {}) => ({ entity: `s${distance}`, name: "CHAR_Undead_Skeleton", distance, x: distance, z: 0, ...extra });

test("1 dead -> recover, above everything else", () => {
  const d = decide(base({ vitals: vit({ dead: true, health: 0 }), inCrypt: true }));
  assert.equal(d.action, "recover");
});

test("1 downed (health 0, not dead) -> recover", () => {
  assert.equal(decide(base({ vitals: vit({ health: 0 }) })).action, "recover");
});

test("2 in crypt -> leaveCrypt, unless it is cooling down", () => {
  assert.equal(decide(base({ inCrypt: true })).action, "leaveCrypt");
  const d = decide(base({ inCrypt: true, cooldowns: { leaveCrypt: NOW + 1 } }));
  assert.notEqual(d.action, "leaveCrypt");
  assert.notEqual(d.action, "buildHeart"); // never build inside the crypt
});

test("3 burning -> seekShade even at night or with no day read", () => {
  assert.equal(decide(base({ vitals: vit({ sun: { inSun: true, charge: 1, burning: true } }) })).action, "seekShade");
});

test("3 in sun with charge above the limit during the damage window -> seekShade", () => {
  const sun = { inSun: true, charge: 0.6, burning: false };
  assert.equal(decide(base({ vitals: vit({ sun }), daytime: { isDay: true, sunDamage: true } })).action, "seekShade");
  assert.equal(decide(base({ vitals: vit({ sun }), daytime: null })).action, "seekShade");
  // low charge with shade known close by: keep working (shade-hopping); outside the damage window: keep working
  const shade = { x: 1, y: 0, z: 1 };
  assert.notEqual(decide(base({ vitals: vit({ sun: { ...sun, charge: 0.2 } }), daytime: { isDay: true, sunDamage: true }, shade })).action, "seekShade");
  assert.notEqual(decide(base({ vitals: vit({ sun }), daytime: { isDay: true, sunDamage: false } })).action, "seekShade");
});

test("4 low health with a hostile close -> flee from the nearest", () => {
  const d = decide(base({ vitals: vit({ health: 30 }), hostiles: [skel(15), skel(5)] }));
  assert.equal(d.action, "flee");
  assert.equal(d.target.distance, 5);
});

test("4 low health but hostiles far or none -> no flee", () => {
  assert.notEqual(decide(base({ vitals: vit({ health: 30 }), hostiles: [skel(35)] })).action, "flee");
  assert.notEqual(decide(base({ vitals: vit({ health: 30 }) })).action, "flee");
});

test("5 low blood -> feed; desperate below the relaxed threshold", () => {
  const d = decide(base({ vitals: vit({ blood: FEED_BLOOD - 1 }) }));
  assert.equal(d.action, "feed");
  assert.equal(d.relaxed, false);
  assert.equal(decide(base({ vitals: vit({ blood: FEED_DESPERATE_BLOOD - 1 }) })).relaxed, true);
  assert.notEqual(decide(base({ vitals: vit({ blood: 10 }), cooldowns: { feed: NOW + 5 } })).action, "feed");
});

test("5 feed ranks below flee", () => {
  assert.equal(decide(base({ vitals: vit({ health: 20, blood: 1 }), hostiles: [skel(8)] })).action, "flee");
});

test("6 drop nearby -> pickup, skipping targets that failed too often", () => {
  const drop = { entity: "d1", name: "Item_Ingredient_Bone", distance: 3 };
  assert.equal(decide(base({ drops: [drop] })).action, "pickup");
  assert.notEqual(decide(base({ drops: [drop], failures: { d1: MAX_TARGET_FAILURES } })).action, "pickup");
});

test("7 journal step or claim -> journal", () => {
  const next = { subtask: { name: "Collect Bones" }, plan: { action: "gather", names: ["Urn"] } };
  assert.equal(decide(base({ journal: { claims: 0, next } })).action, "journal");
  assert.equal(decide(base({ journal: { claims: 1, next: null } })).action, "journal");
  assert.notEqual(decide(base({ journal: { claims: 0, next: null } })).action, "journal");
  assert.notEqual(decide(base({ journal: { claims: 0, next }, cooldowns: { journal: NOW + 1 } })).action, "journal");
});

test("8 no heart -> buildHeart, limited attempts per session", () => {
  assert.equal(decide(base({ hasHeart: false })).action, "buildHeart");
  assert.notEqual(decide(base({ hasHeart: false, heartAttempts: MAX_HEART_ATTEMPTS })).action, "buildHeart");
});

test("9 default: gather rotation Rock, Tree, then hunt a weak unit when healthy", () => {
  assert.deepEqual([0, 1].map((t) => decide(base({ gatherTurn: t })).name), ["Rock", "Tree"]);
  const weak = { entity: "c1", name: "CHAR_Critter_Rat", health: 10, distance: 4 };
  const d = decide(base({ gatherTurn: 2, weakUnits: [weak] }));
  assert.equal(d.action, "hunt");
  assert.equal(d.turn, 2);
  // hunt slot with no healthy bot or no target falls back to the next gather name
  assert.equal(decide(base({ gatherTurn: 2, weakUnits: [weak], vitals: vit({ health: 60 }) })).action, "rest"); // hurt: heal to full first
  assert.equal(decide(base({ gatherTurn: 2 })).name, "Rock");
});

test("9 a missing resource is skipped by its own cooldown", () => {
  const d = decide(base({ gatherTurn: 0, cooldowns: { "gather:Rock": NOW + 1 } }));
  assert.equal(d.name, "Tree");
});

test("10 everything cooling down -> idle", () => {
  assert.equal(decide(base({ cooldowns: { gather: NOW + 1 } })).action, "idle");
});

// ---- progression planners -------------------------------------------------------------------------

test("planSubtask maps journal subtasks to actions", () => {
  assert.equal(planSubtask({ name: "Leave the Crypt", type: "LeftFarbane" }).action, "leaveCrypt");
  const bones = planSubtask({ name: "Collect Bones", type: "PossessItem", trackedItem: { name: "Item_Ingredient_Bone" } });
  assert.equal(bones.action, "gather");
  assert.ok(bones.names.includes("Urn"));
  assert.deepEqual(bones.hunt, ["Skeleton"]);
  assert.equal(planSubtask({ name: "Kill", type: 1, trackedItem: { name: "CHAR_Wolf" } }).action, "hunt");
  assert.deepEqual(planSubtask({ name: "Open the map", type: "OpenedMap" }), { action: "ui", menu: "map" });
  assert.deepEqual(planSubtask({ name: "Journal_SubTask_Interact_BloodFountain_T01", type: "Spawn" }), { action: "interact", target: "CastleHeart" });
  assert.equal(planSubtask({ name: "Journal_SubTask_Spawn_WoodenCoffin", type: "Spawn" }).action, "none");
  assert.equal(planSubtask({ name: "Unlock a spell", type: "UnlockResearch" }).action, "none");
});

test("planJournal claims finished entries and skips unsupported subtasks", () => {
  const quests = {
    achievements: [
      { achievement: 1, name: "Done", state: "InProgress", subtasks: [{ subtask: 10, progress: 1, required: 1, type: "LeftFarbane" }] },
      { achievement: 2, name: "Claimed", state: "Claimed", subtasks: [] },
      { achievement: 3, name: "Remains", state: "InProgress", subtasks: [
        { subtask: 30, name: "Unlock a spell", progress: 0, required: 1, type: "UnlockNewSpell" },
        { subtask: 31, name: "Collect Bones", progress: 4, required: 30, type: "PossessItem", trackedItem: { name: "Item_Ingredient_Bone" } },
      ] },
    ],
  };
  const { claims, next } = planJournal(quests);
  assert.deepEqual(claims.map((a) => a.achievement), [1]);
  assert.equal(next.subtask.subtask, 31);
  assert.equal(planJournal(quests, new Set([31])).next, null);
  assert.deepEqual(planJournal({ achievements: [{ achievement: 4, state: "Incomplete", subtasks: [] }] }).claims, []);
});

test("build pivots snap to 5-unit cell centers (x, z = 2.5 mod 5)", () => {
  assert.equal(snapToCell(-1806), -1807.5);
  assert.equal(snapToCell(12.4), 12.5);
});

test("inventory diffs", () => {
  const a = { items: [{ guid: 1, item: "Bone", amount: 2 }, { guid: 2, item: "Stone", amount: 5 }] };
  const b = { items: [{ guid: 1, item: "Bone", amount: 5 }, { guid: 3, item: "Sword", amount: 1 }] };
  assert.deepEqual(inventoryGain(a, b), { Bone: 3, Sword: 1 });
  assert.deepEqual(inventoryDelta(a, b), { Bone: 3, Stone: -5, Sword: 1 });
});

test("HOSTILE recognizes skeletons but not critters", () => {
  assert.ok(HOSTILE.test("CHAR_Undead_SkeletonSoldier"));
  assert.ok(!HOSTILE.test("CHAR_Critter_Rat"));
});

test("a healthy bot clears a weak nearby hostile before feeding", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const st = { ...newState(), vitals: { health: 120, maxHealth: 125, blood: 2, sun: {} },
    hostiles: [{ entity: "1:1", name: "CHAR_Undead_SkeletonSoldier_Withered", health: 31, distance: 9 }] };
  const d = decide(st);
  assert.equal(d.action, "hunt");
  assert.equal(d.target.entity, "1:1");
});

test("a weak bot feeds instead of clearing the guard", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const st = { ...newState(), vitals: { health: 60, maxHealth: 125, blood: 2, sun: {} },
    hostiles: [{ entity: "1:1", name: "CHAR_Undead_SkeletonSoldier_Withered", health: 31, distance: 9 }] };
  assert.equal(decide(st).action, "feed");
});

test("an engaged bot fights back instead of fleeing", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const st = { ...newState(), vitals: { health: 40, maxHealth: 125, blood: 0, sun: {} },
    hostiles: [{ entity: "2:2", name: "CHAR_Undead_SkeletonSoldier_Withered", health: 31, distance: 1.6 }] };
  const d = decide(st);
  assert.equal(d.action, "hunt");
  assert.match(d.reason, /fight back/);
});

test("a subtask with an unknown required count never makes its entry claimable", () => {
  const quests = { achievements: [{ achievement: 7, name: "Unknown", state: "in progress",
    subtasks: [{ subtask: 70, name: "Mystery", progress: 0, required: 0, type: "PossessItem" }] }] };
  assert.deepEqual(planJournal(quests).claims, []);
});

test("an entry whose claim failed twice is skipped", () => {
  const quests = { achievements: [{ achievement: 8, name: "Done", state: "complete", subtasks: [] }] };
  assert.equal(planJournal(quests).claims.length, 1);
  assert.equal(planJournal(quests, new Set(), new Set([8])).claims.length, 0);
});

test("a Craft subtask plans the recipe named after the item", () => {
  const p = planSubtask({ name: "Journal_SubTask_Craft_BoneSword", type: "Craft", trackedItem: { name: "Item_Weapon_Sword_T01_Bone" } });
  assert.equal(p.action, "craft");
  assert.equal(p.recipe, "Recipe_Weapon_Sword_T01_Bone");
});

test("a trip home fights a fair unit that engages first, runs only once healthy", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const trip = (over) => ({ ...newState(), persona: "builder", self: { x: 0, y: 5, z: -2950 }, castle: { heart: { x: 0, z: -1500 } },
    daytime: { isDay: false, sunDamage: false, secondsUntilDay: 99999 }, bag: { gear: { total: 8 }, items: [] }, ...over });
  const fair = { entity: "1:1", name: "CHAR_Undead_SkeletonSoldier_Withered", level: 1, health: 31, distance: 2 };
  assert.equal(decide(trip({ vitals: vit({ health: 95 }), hostiles: [fair] })).action, "hunt");
  assert.equal(decide(trip({ vitals: vit({ health: 95 }) })).goal, "home");
  assert.equal(decide(trip({ vitals: vit({ health: 50 }) })).action, "rest"); // rest first
  // A unit too strong to fight (above gear + 2, not so far above that the bot flees) is run past.
  const strong = { ...fair, entity: "2:2", name: "CHAR_Undead_Ghoul_Armored", level: 11 }; // above gear + 2, not above gear + 3
  assert.equal(decide(trip({ vitals: vit({ health: 95 }), hostiles: [strong] })).goal, "home");
});

test("after a death the bot goes back for its bag when it is close, recent and the bot is healthy", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const at = (over) => ({ ...newState(), now: NOW, persona: "builder", self: { x: 0, y: 5, z: -2980 }, vitals: vit({ health: 95 }),
    daytime: { isDay: false, sunDamage: false, secondsUntilDay: 99999 }, bag: { gear: { total: 8 }, items: [] }, ...over });
  assert.equal(decide(at({ lostBag: { x: 0, z: -2880, at: NOW - 60000 } })).action, "retrieve");
  assert.notEqual(decide(at({ lostBag: { x: 0, z: -2880, at: NOW - 20 * 60000 } })).action, "retrieve"); // too old
  assert.notEqual(decide(at({ lostBag: { x: 0, z: -2000, at: NOW - 60000 } })).action, "retrieve");      // too far
});

test("wantBlood: hang on when low, when typeless, or for clearly better wanted blood", async () => {
  const { wantBlood } = await import("../skills.mjs");
  const deer = { bloodType: "BloodType_Creature", bloodQuality: 44 };
  assert.ok(wantBlood({ blood: 50, bloodType: "BloodType_Warrior", bloodQuality: 60 }, deer, ["Warrior"]));   // low pool
  assert.ok(wantBlood({ blood: 95, bloodType: "BloodType_None", bloodQuality: 0 }, deer, ["Warrior"]));       // no type yet
  assert.ok(!wantBlood({ blood: 95, bloodType: "BloodType_Warrior", bloodQuality: 30 }, deer, ["Warrior"]));  // not wanted: finish
  assert.ok(wantBlood({ blood: 95, bloodType: "BloodType_Creature", bloodQuality: 20 }, deer, ["Creature"])); // better wanted
  assert.ok(!wantBlood({ blood: 95, bloodType: "BloodType_Creature", bloodQuality: 42 }, deer, ["Creature"])); // not clearly better
});

test("a crossbow aggroed on the bot from 12 m counts as engaging it", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const st = { ...newState(), now: NOW, vitals: vit({ health: 90 }), self: { x: 0, y: 0, z: 0 }, bag: { gear: { total: 8 }, items: [] },
    hostiles: [{ entity: "3:3", name: "CHAR_Undead_SkeletonCrossbow_Graveyard", level: 2, health: 39, distance: 12, onMe: true }] };
  assert.equal(decide(st).action, "hunt");
  assert.match(decide(st).reason, /fight back/);
});

test("hybridPair: a crossbow plus the best melee weapon for the target", async () => {
  const { hybridPair } = await import("../skills.mjs");
  const bag = { occupied: [{ slot: 1, item: "Item_Weapon_Crossbow_T01_Bone" }, { slot: 2, item: "Item_Weapon_Spear_T01_Bone" }, { slot: 3, item: "Item_Weapon_Reaper_T01_Bone" }],
    equipped: [{ slot: "weapon", item: "Item_Weapon_Sword_T01_Bone" }] };
  assert.deepEqual(hybridPair("CHAR_Undead_RottingGhoul", bag), { ranged: "Item_Weapon_Crossbow_T01_Bone", melee: "Item_Weapon_Reaper_T01_Bone" });
  assert.equal(hybridPair("CHAR_Forest_Wolf", bag).melee, "Item_Weapon_Spear_T01_Bone");
  assert.equal(hybridPair("CHAR_Forest_Wolf", { occupied: [{ slot: 1, item: "Item_Weapon_Sword_T01_Bone" }] }), null); // no crossbow
});

test("review 2026-09-25: day trips start with exposure in hand, corpse runs need blood, 65% is not healthy, boss top-up", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const at = (over) => ({ ...newState(), now: NOW, persona: "warrior", self: { x: 0, y: 5, z: -2950 }, castle: { heart: { x: 0, z: -1500 } },
    daytime: { isDay: false, sunDamage: false, secondsUntilDay: 99999 }, bag: { gear: { total: 8 }, items: [] }, vitals: vit({ health: 95 }), ...over });
  // by day a trip hops shade: it starts from shade with exposure in hand, not from the sun at high charge
  const day = { isDay: true, sunDamage: true, secondsUntilNight: 300 };
  // a long trip with no remembered route waits for dusk by day (home trips start beyond 120 m)
  assert.notEqual(decide(at({ daytime: day, vitals: vit({ health: 95, sun: { inSun: false, charge: 0.4 } }) })).goal, "home");
  assert.notEqual(decide(at({ daytime: day, vitals: vit({ health: 95, sun: { inSun: true, charge: 0.7 } }) })).goal, "home");
  // corpse run at blood 5: no
  assert.notEqual(decide(at({ vitals: vit({ health: 95, blood: 5 }), lostBag: { x: 0, z: -2880, at: NOW - 60000 } })).action, "retrieve");
  // 65% with a hostile at 10 m (no rest): no trip
  assert.notEqual(decide(at({ vitals: vit({ health: 65 }), hostiles: [{ entity: "9:9", name: "CHAR_Undead_Skeleton", level: 1, distance: 10, x: 10, z: -2950 }] })).goal, "home");
  // a boss the gear allows, blood 60: top up first
  const near = at({ self: { x: 0, y: 5, z: -1510 }, bag: { gear: { total: 14 }, items: [] }, vitals: vit({ health: 100, blood: 60 }), vbloods: new Set() });
  const d = decide(near);
  assert.equal(d.action, "feed");
  assert.match(d.reason, /top up before Alpha Wolf/);
});

test("shade: spots sit behind casters away from the sun; the exposure budget", async () => {
  const { shadowDir, shadeSpots, exposureLeft } = await import("../shade.mjs");
  const dir = shadowDir({ sunRotation: [65, 90, 0] }); // yaw 90: shadows toward +x
  assert.ok(Math.abs(dir.x - 1) < 1e-9 && Math.abs(dir.z) < 1e-9);
  const spots = shadeSpots([{ name: "Resource_Tree_Pine_01", kind: "resource", x: 10, y: 0, z: 5 }, { name: "Resource_Bush_Fiber", kind: "resource", x: 0, y: 0, z: 0 }], dir);
  assert.equal(spots.length, 1);
  assert.ok(Math.abs(spots[0].x - 13.2) < 1e-9 && spots[0].z === 5); // a tree: its canopy shadow, 3.2 m out
  assert.equal(shadeSpots([{ name: "Resource_Tree_Oak", kind: "resource", x: 0, y: 0, z: 0 }], null).length, 4); // no sun read: all sides
  assert.ok(Math.abs(exposureLeft({ charge: 0, chargeUpTime: 5 }) - 3.8) < 1e-9);
  assert.equal(exposureLeft({ charge: 0.9, chargeUpTime: 5 }), 0);
});

test("health 0 without death after a recovery that changed nothing: starved, feed instead of recovering", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const st = { ...newState(), now: NOW, self: { x: -900, y: 0, z: -1870 }, vitals: vit({ health: 0, blood: 0 }) };
  assert.equal(decide(st).action, "recover");
  const d = decide({ ...st, noRecoverUntil: NOW + 60000 });
  assert.equal(d.action, "feed");
  assert.equal(decide({ ...st, noRecoverUntil: NOW + 60000, vitals: vit({ health: 0, dead: true }) }).action, "recover");
});

test("blood under 20 is critical; trips need 15, except leaving the graveyard with no rat at hand", async () => {
  const { decide, newState, BLOOD_CRITICAL } = await import("../brain.mjs");
  assert.equal(BLOOD_CRITICAL, 20);
  const at = (over) => ({ ...newState(), now: NOW, persona: "builder", self: { x: -900, y: 5, z: -1900 }, castle: { heart: { x: -1142, z: -1722 } },
    daytime: { isDay: false, sunDamage: false, secondsUntilDay: 99999 }, bag: { gear: { total: 8 }, items: [] }, ...over });
  assert.notEqual(decide(at({ vitals: vit({ health: 95, blood: 12 }) })).goal, "home"); // outside the graveyard: feed first
  const gy = at({ self: { x: -570, y: 5, z: -2950 }, vitals: vit({ health: 95, blood: 12 }) });
  assert.equal(decide(gy).goal, "home"); // graveyard, no rat at hand: leave (the exit is instant)
  const rat = { entity: "r:1", name: "CHAR_Critter_Rat", distance: 6, x: -565, z: -2950 };
  assert.notEqual(decide({ ...gy, critters: [rat] }).goal, "home"); // a rat at hand: eat it first
});

test("HARMLESS: deer and peaceful villagers, not hostile villagers", async () => {
  const { HARMLESS } = await import("../brain.mjs");
  assert.ok(HARMLESS.test("CHAR_Forest_Deer"));
  assert.ok(HARMLESS.test("CHAR_Farmlands_Villager_Female"));
  assert.ok(!HARMLESS.test("CHAR_Farmlands_HostileVillager_Male_Club"));
  assert.ok(!HARMLESS.test("CHAR_Forest_Wolf"));
});

test("tips: at low health a single fair attacker is finished, not run from; two or a stronger one are fled", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const st = (hostiles) => ({ ...newState(), now: NOW, self: { x: 0, y: 0, z: 0 }, vitals: vit({ health: 25 }), bag: { gear: { total: 8 }, items: [] }, hostiles });
  const skel = (id, level, d) => ({ entity: id, name: "CHAR_Undead_SkeletonSoldier_Withered", level, distance: d, health: 20, onMe: true });
  assert.equal(decide(st([skel("a", 1, 6)])).action, "hunt");
  assert.equal(decide(st([skel("a", 1, 6), skel("b", 1, 8)])).action, "flee");
  assert.equal(decide(st([skel("a", 12, 6)])).action, "flee");
});

test("trainer: goes to the arena its gear matches, then fights the strongest fair opponent", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const tr = (over) => ({ ...newState(), now: NOW, persona: "trainer", vitals: vit({ health: 100 }), bag: { gear: { total: 29 }, items: [] }, daytime: { isDay: false, sunDamage: false, secondsUntilDay: 99999 }, ...over });
  const d = decide(tr({ self: { x: -900, y: 0, z: -2920 } })); // in the graveyard
  assert.equal(d.action, "goal");
  assert.match(d.reason, /boss: hunt Alpha Wolf/); // the V Blood ladder in order first
  const { BOSSES } = await import("../boss.mjs");
  const allFed = new Set(BOSSES.map((b) => b.name));
  assert.match(decide(tr({ self: { x: -900, y: 0, z: -2920 }, vbloods: allFed })).reason, /Farbane woods/);
  const foes = [{ entity: "w", name: "CHAR_Forest_Wolf", level: 10, distance: 8, x: -955, z: -1775, health: 120 }, // healthy: not a "weak guard"
    { entity: "b", name: "CHAR_Forest_Bear_Standard", level: 18, distance: 20, x: -940, z: -1760, health: 300 }];
  const f = decide(tr({ self: { x: -960, y: 0, z: -1780 }, hostiles: foes, vbloods: allFed }));
  assert.equal(f.action, "hunt");
  assert.equal(f.target.entity, "b"); // the level-18 bear over the level-10 wolf
});

test("brain: two fair wolves on a hurt bot are fought out, not fled (wolves outrun it)", async () => {
  const { decide } = await import("../brain.mjs");
  const wolf = (x, health) => ({ name: "CHAR_Forest_Wolf", entity: `w${x}`, level: 10, x, z: 0, distance: 1.6, onMe: true, health, maxHealth: 200 });
  const d = decide({ now: 1, cooldowns: {}, failures: {}, failureAt: {}, self: { x: 0, y: 0, z: 0 }, vitals: { health: 30, maxHealth: 214, blood: 80, sun: { inSun: false, charge: 0 } },
    bag: { gear: { total: 10 }, items: [] }, hostiles: [wolf(1, 150), wolf(-1, 60)], feedable: [], critters: [], weakUnits: [] });
  assert.equal(d.action, "hunt");
  assert.equal(d.target.entity, "w-1"); // the weaker one first
});

test("brain: an idle out-leveled unit 20 m off is not fled; the same unit approaching is", async () => {
  const { decide } = await import("../brain.mjs");
  const golem = { name: "CHAR_Undead_SkeletonGolem", entity: "g", level: 38, x: 20, z: 0, distance: 20 };
  const base = { now: 1, cooldowns: {}, failures: {}, failureAt: {}, self: { x: 0, y: 0, z: 0 }, vitals: { health: 214, maxHealth: 214, blood: 80, sun: { inSun: false, charge: 0 } },
    bag: { gear: { total: 10 }, items: [] }, feedable: [], critters: [], weakUnits: [], hostilesTracked: true };
  assert.notEqual(decide({ ...base, hostiles: [golem] }).action, "flee");
  assert.equal(decide({ ...base, hostiles: [{ ...golem, approaching: true }] }).action, "flee");
});
