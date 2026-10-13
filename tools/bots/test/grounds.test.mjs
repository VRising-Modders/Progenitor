import test from "node:test";
import assert from "node:assert/strict";
import { recordGround, bestGround, _setGrounds } from "../grounds.mjs";

test("hunting grounds: fed-on and often-seen cells win, weighted by distance and age, visited ones skipped", () => {
  _setGrounds({});
  const now = 1_000_000_000;
  for (let i = 0; i < 3; i++) recordGround({ x: 100, z: 0 }, { name: "CHAR_Forest_Deer", now });
  recordGround({ x: 300, z: 0 }, { fed: true, name: "CHAR_Forest_Deer", now });
  recordGround({ x: 300, z: 5 }, { fed: true, now });
  recordGround({ x: 310, z: 5 }, { fed: true, now });
  const me = { x: 0, z: 0 };
  const g = bestGround(me, { now });
  assert.equal(Math.round(g.x), 315); // three feeds (9) at ~315 m beat three sightings at ~105 m
  assert.equal(bestGround(me, { now, visited: { [g.key]: now } }).names[0], "CHAR_Forest_Deer");
  assert.equal(bestGround(me, { now, ok: () => false }), null);
  assert.equal(bestGround(me, { now: now + 10 * 3600000, maxDist: 50 }), null);
});
