import test from "node:test";
import assert from "node:assert/strict";
import {
  newTracks, updateTracks, threatAt, threatNear, threatOf, strength, trackKey, threatSummary,
  PREY_BUDGET, TRAIN_BUDGET, OUTMATCHED, PACKED, TRACK_MS, VBLOOD_TRACK_MS, ZONE_MS,
} from "../threat.mjs";
import { safePreyUnit } from "../brain.mjs";

const wolf = (entity, x, z, level = 10) => ({ entity, name: "CHAR_Forest_Wolf", level, x, z });

test("threat: strength by level against gear, V Blood heavy, harmless units zero", () => {
  assert.equal(strength({ name: "CHAR_Forest_Wolf", level: 10 }, 10), 1);
  assert.equal(strength({ name: "CHAR_Forest_Wolf", level: 15 }, 10), 2);
  assert.equal(strength({ name: "CHAR_Undead_Skeleton", level: 1 }, 20), 0.75); // floor
  assert.ok(strength({ name: "CHAR_Forest_Wolf_VBlood", level: 16 }, 20) >= 5);
  for (const n of ["CHAR_Forest_Deer", "CHAR_Farmlands_Villager_Female", "CHAR_Bandit_Prisoner_Villager_Male", "CHAR_Critter_Rat"]) {
    assert.equal(strength({ name: n, level: 30 }, 5), 0, n);
  }
  assert.ok(strength({ name: "CHAR_Farmlands_HostileVillager_Male", level: 10 }, 10) > 0);
});

test("threat: weight fades with distance, and a pack adds up", () => {
  const p = { x: 0, z: 0 };
  assert.equal(threatAt([wolf("a", 10, 0)], p, 10), 1);          // inside 15 m: full
  assert.ok(Math.abs(threatAt([wolf("a", 22.5, 0)], p, 10) - 0.5) < 1e-9); // halfway through the fade
  assert.equal(threatAt([wolf("a", 31, 0)], p, 10), 0);          // past 30 m: nothing
  assert.equal(threatAt([wolf("a", 5, 0), wolf("b", 6, 0), wolf("c", 7, 0)], p, 10), 3);
  // A unit with no position counts at its distance from the bot.
  assert.equal(threatAt([{ name: "CHAR_Forest_Wolf", level: 10, distance: 40 }], p, 10), 0);
  // threatNear lists the contributors heaviest first.
  const near = threatNear([wolf("a", 20, 0), wolf("b", 5, 0)], p, 10);
  assert.deepEqual(near.map((x) => x.track.entity), ["b", "a"]);
});

test("threat: prey safety keeps the old cases (lone wolf, wolf pack, V Blood, level)", () => {
  const prey = wolf("p", 0, 0);
  assert.ok(safePreyUnit(prey, [prey], 10));                                   // a lone wolf is fine
  assert.ok(!safePreyUnit(prey, [prey, wolf("b", 8, 0), wolf("c", -6, 4)], 10)); // three wolves are not
  assert.ok(!safePreyUnit(prey, [prey, wolf("b", 2, 1)], 10));                  // a packmate in the same cell counts
  assert.ok(!safePreyUnit(prey, [prey, { name: "CHAR_Undead_Skeleton", level: 2, x: 10, z: 0 }], 10)); // any hostile within 12 m
  const rufus = { name: "CHAR_Bandit_Foreman_VBlood", level: 20, x: 34, z: 0 };
  assert.ok(!safePreyUnit(prey, [prey, rufus], 30));                           // a V Blood within ~35 m poisons prey
  assert.ok(safePreyUnit(prey, [prey, { ...rufus, x: 45 }], 30));
  const bear = { name: "CHAR_Forest_Bear_Standard", level: 18, x: 30, z: 0 };
  assert.ok(!safePreyUnit(prey, [prey, bear], 6));                             // out-leveled within ~35 m
  assert.ok(safePreyUnit(prey, [prey, { name: "CHAR_Bandit_Prisoner_Villager_Male", level: 14, x: 3, z: 0 }], 6)); // harmless
  // Prey remembered without an entity id (preySeen) excludes only itself, by name and position.
  assert.ok(safePreyUnit({ name: "CHAR_Forest_Wolf", x: 0.5, z: 0 }, [prey], 10));
});

test("threat: trainer and engagement thresholds", () => {
  const t = { name: "CHAR_Bandit_Scout", level: 10, x: 0, z: 0, entity: "t" };
  const at = (others, gear) => threatAt([t, ...others], t, gear, { exclude: t });
  const scout = (e, x) => ({ name: "CHAR_Bandit_Scout", level: 10, x, z: 0, entity: e });
  assert.ok(at([scout("a", 5)], 23) < TRAIN_BUDGET);                 // one companion is fine
  assert.ok(at([scout("a", 5), scout("b", 8)], 23) >= TRAIN_BUDGET); // two are a camp
  assert.ok(at([{ ...scout("a", 5), level: 12 }], 10) < TRAIN_BUDGET); // one at gear + 2 still fine
  const skel = { name: "CHAR_Undead_SkeletonSoldier_Withered", level: 1 };
  assert.ok(threatOf([skel], 8) < OUTMATCHED);
  assert.ok(threatOf([skel, skel], 8) >= OUTMATCHED);               // two weak attackers
  assert.ok(threatOf([{ ...skel, level: 10 }], 8) < OUTMATCHED);    // one at gear + 2
  assert.ok(threatOf([{ ...skel, level: 11 }], 8) >= OUTMATCHED);   // one above it
  assert.ok(threatOf([skel, skel], 8) < PACKED);
  assert.ok(threatOf([skel, skel, skel], 8) >= PACKED);             // three on the bot: a pack
});

test("tracks: remember out of view, follow a moving unit, drop what the view no longer shows", () => {
  const tr = newTracks();
  const self = { x: 0, z: 0 };
  updateTracks(tr, [wolf("a", 10, 0), wolf("b", 12, 0)], { now: 0, self, covered: 60 });
  assert.equal(tr.size, 2);
  // Moved 4 m in 1.5 s: the same track, re-keyed.
  updateTracks(tr, [wolf("a", 14, 0), wolf("b", 12, 0)], { now: 1500, self, covered: 60 });
  assert.equal(tr.size, 2);
  assert.ok(tr.has(trackKey("CHAR_Forest_Wolf", 14, 0)));
  // Out of view (the bot turned away / walked on): remembered, still a pack for prey near there.
  const far = { x: 200, z: 0 };
  updateTracks(tr, [], { now: 30000, self: far, covered: 60 });
  assert.equal(tr.size, 2);
  assert.ok(!safePreyUnit(wolf("p", 13, 3), tr, 10));
  // ...until 90 s after it was last seen.
  updateTracks(tr, [], { now: 1500 + TRACK_MS + 1, self: far, covered: 60 });
  assert.equal(tr.size, 0);
  // Inside the covered view and not seen again: gone (killed, or moved on).
  updateTracks(tr, [wolf("a", 10, 0)], { now: 0, self, covered: 60 });
  updateTracks(tr, [], { now: 1000, self, covered: 60 });
  assert.equal(tr.size, 0);
  // A view cut short by its unit limit covers less: a track past it is kept.
  updateTracks(tr, [wolf("a", 50, 0)], { now: 0, self, covered: 60 });
  updateTracks(tr, [], { now: 1000, self, covered: 30 });
  assert.equal(tr.size, 1);
});

test("tracks: bot_threats rows merge with the view (onMe) and add units the view missed", () => {
  const tr = newTracks();
  const self = { x: 0, z: 0 };
  updateTracks(tr, [wolf("c1", 10, 0)], {
    now: 0, self, covered: 60,
    threats: [{ entity: "s1", name: "CHAR_Forest_Wolf", level: 10, x: 11, z: 0.5, onMe: true },
      { entity: "s2", name: "CHAR_Bandit_Scout", level: 12, x: 30, z: 0, onMe: false }],
  });
  assert.equal(tr.size, 2);
  const w = [...tr.values()].find((t) => t.name === "CHAR_Forest_Wolf");
  assert.equal(w.onMe, true);
  assert.equal(w.entity, "c1"); // the client id, not the server one
  assert.equal(threatAt(tr, self, 10, { onlyOnMe: true }), 1);
});

test("tracks: V Blood kept 10 minutes, danger zones fixed for 30", () => {
  const tr = newTracks();
  updateTracks(tr, [{ name: "CHAR_Forest_Wolf_VBlood", level: 16, x: 0, z: 0 }], { now: 0 });
  updateTracks(tr, [], { now: TRACK_MS + 1000 });
  assert.equal(tr.size, 1);
  updateTracks(tr, [], { now: VBLOOD_TRACK_MS + 1000 });
  assert.equal(tr.size, 0);
  const zones = [{ x: 100, z: 100, at: 0 }];
  updateTracks(tr, [], { now: 1000, zones, self: { x: 100, z: 100 }, covered: 60 }); // a zone is never "not seen"
  assert.equal(tr.size, 1);
  assert.ok(threatAt(tr, { x: 105, z: 100 }, 10) >= PREY_BUDGET); // a food roam does not go back there
  assert.equal(threatAt(tr, { x: 150, z: 100 }, 10), 0);
  updateTracks(tr, [], { now: ZONE_MS + 1, zones });
  assert.equal(tr.size, 0);
  const s = threatSummary(updateTracks(tr, [wolf("a", 3, 0)], { now: 0 }), { x: 0, z: 0 }, 10);
  assert.equal(s.here, 1);
  assert.equal(s.top.length, 1);
});

test("brain: prey next to a pack remembered out of view is not taken", async () => {
  const { decide, newState } = await import("../brain.mjs");
  const deer = { entity: "d", name: "CHAR_Forest_Deer", level: 8, x: 20, z: 0, distance: 20, health: 100 };
  const snap = (tracks) => ({
    ...newState(), persona: "warrior", now: 1, weakUnits: [], drops: [], journal: null, recipes: [], equipped: [], hostiles: [],
    bag: { items: [], occupied: [], gear: { total: 10 } }, self: { x: 0, y: 0, z: 0 }, feedable: [deer],
    vitals: { health: 125, maxHealth: 125, blood: 50, sun: {} }, tracks,
  });
  assert.equal(decide(snap(newTracks())).action, "feed");
  const tr = updateTracks(newTracks(), [wolf("a", 26, 4), wolf("b", 28, -3)], { now: 0 });
  assert.notEqual(decide(snap(tr)).action, "feed");
});
