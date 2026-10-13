import test from "node:test";
import assert from "node:assert/strict";
import { roomPlan } from "../layout.mjs";

// A castle like HeadlessTwo's (relative to a heart at 0,0): the coffin off the heart's 15 m footprint.
const heart = { x: 0, z: 0 };
const floor = (x, z) => ({ name: "TM_Castle_Floor_Outdoor_Plain01", x, z });
const wall = (x, z) => ({ name: "TM_Castle_Wall_Tier01_Wood", x, z });
const buildings = [
  { name: "TM_BloodFountain_CastleHeart", x: 0, z: 0 },
  floor(5, 0), floor(0, -5), floor(-5, 0), floor(0, 5), floor(5, -5), floor(5, 5),
  floor(10, 0), floor(10, -5), floor(10, 5),
  wall(12.5, -5.5), wall(12.5, -0.5), wall(10, -7.5),
  { name: "TM_SpecialStation_WoodenCoffin", x: 10, z: 0 },
  { name: "TM_Castle_MistBrazier", x: 10, z: -5 },
];

test("roomPlan closes the coffin's block with its walled neighbor off the heart, entrance first from a floor", () => {
  const r = roomPlan(buildings, heart);
  assert.deepEqual(r.cells, [{ x: 10, z: 0 }, { x: 10, z: -5 }]);
  assert.equal(r.missing.length, 3);
  assert.deepEqual(r.missing[0], { x: 7.5, z: 0, rotation: 1, entrance: true }); // (5,0) is a floor
  assert.ok(r.missing.slice(1).every((e) => !e.entrance));
});

test("roomPlan skips rooms with a refused edge and reports a closed room as nothing missing", () => {
  const r = roomPlan(buildings, heart, new Set(["7.5,-5"]));
  assert.ok(!r.missing.some((e) => e.x === 7.5 && e.z === -5));
  const closed = roomPlan([...buildings, { name: "TM_Castle_Wall_Tier01_Wood_Entrance", x: 7.5, z: 0 }, wall(10, 2.5), wall(7.5, -5)], heart);
  assert.equal(closed.missing.length, 0);
  assert.deepEqual(closed.center, { x: 10, z: -2.5 });
});

test("roomPlan never rings blocks on the heart's footprint (HeadlessOne's ring there stayed IsMissingWalls)", () => {
  const onHeart = [{ name: "TM_BloodFountain_CastleHeart", x: 0, z: 0 }, floor(5, 0), floor(5, -5), floor(0, -10),
    { name: "TM_SpecialStation_WoodenCoffin", x: 5, z: 0 }];
  const r = roomPlan(onHeart, heart);
  assert.deepEqual(r.cells, [{ x: 0, z: -10 }]); // the off-footprint floor nearest the coffin
});

test("roomPlan needs a coffin on a floor", () => {
  assert.equal(roomPlan(buildings.filter((b) => !/Coffin/.test(b.name)), heart), null);
});

import { shadeAnchors, sunSlack, timeToBurn } from "../shade.mjs";

test("shade anchors rank a near likely shadow over a far one, and the room over guesses", () => {
  const bot = { name: "test-none", shadeMap: [], badShade: new Set() };
  const me = { x: 0, y: 0, z: 0 };
  const nearby = [{ name: "TM_Spruce01_Large", kind: "breakable", x: 5, y: 0, z: 0 }, { name: "TM_Pine_01_Stage1", kind: "breakable", x: 30, y: 0, z: 0 }];
  const daytime = { sunRotation: [65, 90, 0] }; // shadows toward +x
  const a = shadeAnchors(bot, me, nearby, daytime);
  assert.equal(a[0].caster, "TM_Spruce01_Large");
  assert.ok(a[0].p > 0.8 && a[0].walkS < 3);
  const withRoom = shadeAnchors(bot, me, nearby, daytime, { room: { x: 3, z: 3 } });
  assert.equal(withRoom[0].caster, "the castle room");
  assert.ok(timeToBurn({ inSun: true, charge: 0.5, chargeUpTime: 5 }) === 2.5);
  assert.ok(sunSlack({ inSun: true, charge: 0.5, chargeUpTime: 5 }, a) < 2.5);
  assert.ok(sunSlack({ inSun: true, charge: 0 }, []) < 0); // nothing in view: no budget
});
