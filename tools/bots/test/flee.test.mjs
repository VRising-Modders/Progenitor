// Unit tests for the flight query (brain.mjs scoreFleePoints and helpers; gap analysis section 9).
// Pure: no bridge, game or network. Run with `node --test "tools/bots/test/*.test.mjs"`.
import test from "node:test";
import assert from "node:assert/strict";
import {
  fleeCandidates, fleeCentroid, scoreFleePoints, fleeExposed, fleeShade,
  FLEE_NEAR, FLEE_FAR, FLEE_POINTS,
} from "../brain.mjs";

const ME = { x: 0, y: 0, z: 0 };
const unit = (name, x, z, level = 10, extra = {}) => ({ name, level, x, z, distance: Math.hypot(x, z), ...extra });
const skel = (x, z, level = 10) => unit("CHAR_Undead_Skeleton", x, z, level);
const bandit = (x, z, level = 10) => unit("CHAR_Bandit_Thug", x, z, level);
const near = (a, b, eps = 1e-6) => Math.abs(a - b) < eps;

test("flee candidates: 12 directions alternating 20/14 m, the first straight along `face`", () => {
  const pts = fleeCandidates(ME, { face: -Math.PI / 2 });
  assert.equal(pts.length, FLEE_POINTS);
  assert.ok(near(pts[0].x, -FLEE_FAR) && near(pts[0].z, 0, 1e-9), "first point straight along face");
  pts.forEach((p, i) => assert.ok(near(Math.hypot(p.x, p.z), i % 2 ? FLEE_NEAR : FLEE_FAR), `radius of ${i}`));
});

test("flee candidates: by day likely anchors 8-25 m out are points too", () => {
  const anchors = [
    { x: 0, z: 12, p: 0.95, caster: "proven" },        // in
    { x: 3, z: 0, p: 0.95 },                           // too close: the ring covers it
    { x: 40, z: 0, p: 0.95 },                          // too far for one leg
    { x: -15, z: 0, p: 0.3 },                          // a guess, not likely shade
  ];
  const extra = fleeCandidates(ME, { anchors }).slice(FLEE_POINTS);
  assert.equal(extra.length, 1);
  assert.equal(extra[0].anchor, anchors[0]);
});

test("flee centroid: weighted by strength, so a V Blood outweighs a skeleton", () => {
  const c = fleeCentroid([skel(0, -5, 5), unit("CHAR_Forest_Wolf_VBlood", 0, 30, 16)], 10);
  assert.ok(c.z > 20, `centroid z ${c.z}`);
  assert.equal(fleeCentroid([], 10), null);
});

test("flee: away from a single chaser, not past it", () => {
  const s = skel(3, 0);
  const best = scoreFleePoints(ME, { tracks: [s], from: [s], gear: 10 })[0];
  assert.ok(best.x < -10, `best at (${best.x.toFixed(1)}, ${best.z.toFixed(1)})`);
  assert.ok(best.away > 0.9);
});

test("flee: every hostile counts, so it does not run into a pack on the far side", () => {
  // The old flight ran straight away from the nearest chaser: west, into three bandits 25 m out.
  const s = skel(3, 0);
  const pack = [bandit(-25, 0), bandit(-26, 2), bandit(-26, -2)];
  const scored = scoreFleePoints(ME, { tracks: [s, ...pack], from: [s], gear: 10 });
  const best = scored[0];
  const west = scored.find((p) => near(p.x, -FLEE_FAR, 1e-6) && near(p.z, 0, 1e-6));
  assert.ok(best.x > -12, `best at (${best.x.toFixed(1)}, ${best.z.toFixed(1)}) runs toward the pack`);
  assert.ok(west.score < best.score && west.threat > 3);
});

test("flee: the centroid of all chasers, not the nearest", () => {
  // One skeleton 3 m east (nearest) and two 5 m north: straight away from the nearest (west) keeps
  // the pair alongside; the best leg heads south-west, away from all three.
  const chasers = [skel(3, 0), skel(1, 5), skel(-1, 5)];
  const best = scoreFleePoints(ME, { tracks: chasers, from: chasers, gear: 10 })[0];
  assert.ok(best.z < -5 && best.x < 0, `best at (${best.x.toFixed(1)}, ${best.z.toFixed(1)})`);
});

test("flee: home within 200 m bends the flight toward it; the chaser between them does not", () => {
  const s = skel(3, 0);
  const side = scoreFleePoints(ME, { tracks: [s], from: [s], gear: 10, home: { x: 0, z: 100 } })[0];
  assert.ok(side.z > 5 && side.x < 0, `home north: best at (${side.x.toFixed(1)}, ${side.z.toFixed(1)})`);
  const behind = scoreFleePoints(ME, { tracks: [s], from: [s], gear: 10, home: { x: 100, z: 0 } })[0];
  assert.ok(behind.x < -10, `home behind the chaser: best at (${behind.x.toFixed(1)}, ${behind.z.toFixed(1)})`);
  const far = scoreFleePoints(ME, { tracks: [s], from: [s], gear: 10, home: { x: 0, z: 500 } })[0];
  assert.equal(far.home, 0);
  assert.ok(far.x < -10);
});

test("flee: a boss the gear cannot take, fled from 40 m, whatever else is near", () => {
  const boss = unit("CHAR_Forest_Wolf_VBlood", 0, 40, 16);
  const best = scoreFleePoints(ME, { tracks: [boss], from: [boss], gear: 6 })[0];
  assert.ok(best.z < -10, `best at (${best.x.toFixed(1)}, ${best.z.toFixed(1)})`);
});

test("flee by day: a point near a likely shade anchor wins; open ground is marked down; night ignores shade", () => {
  const s = skel(3, 0);
  const anchors = [{ x: -2, z: -19, p: 0.9, caster: "TM_Spruce" }];
  const day = scoreFleePoints(ME, { tracks: [s], from: [s], gear: 10, anchors });
  assert.equal(day[0].anchor, anchors[0], `best at (${day[0].x.toFixed(1)}, ${day[0].z.toFixed(1)})`);
  const west = day.find((p) => near(p.x, -FLEE_FAR) && near(p.z, 0));
  assert.equal(west.shade, -0.5);
  const night = scoreFleePoints(ME, { tracks: [s], from: [s], gear: 10 });
  assert.ok(night[0].x < -10 && night[0].shade === 0);
});

test("flee sun check: exposed when burning, past the hard charge, or short of slack; never in shade", () => {
  const anchors = [{ x: 4, z: 0, p: 0.9, walkS: 1 }];
  assert.equal(fleeExposed({ inSun: false, charge: 0.9 }, anchors), false);
  assert.equal(fleeExposed({ inSun: true, burning: true, charge: 1 }, anchors), true);
  assert.equal(fleeExposed({ inSun: true, charge: 0.65, chargeUpTime: 5 }, anchors), true);  // past SUN_CHARGE_HARD
  assert.equal(fleeExposed({ inSun: true, charge: 0.1, chargeUpTime: 5 }, anchors), false);  // 4.5 s to burn - 1 s walk = 3.5 s
  assert.equal(fleeExposed({ inSun: true, charge: 0.4, chargeUpTime: 5 }, anchors), true);   // 3 s to burn - 1 s walk = 2 s
  assert.equal(fleeExposed({ inSun: true, charge: 0.4, chargeUpTime: 5 }, []), false);       // no anchors: the charge alone
});

test("flee shade: the likely anchor away from a camp beats a nearer one beside it", () => {
  const a = { x: 5, z: 0, p: 0.95, walkS: 1.2, score: 0.95 * Math.exp(-1.2 / 3) };
  const b = { x: -12, z: 0, p: 0.9, walkS: 2.8, score: 0.9 * Math.exp(-2.8 / 3) };
  const guess = { x: 2, z: 0, p: 0.3, walkS: 0.5, score: 0.3 * Math.exp(-0.5 / 3) };
  assert.equal(fleeShade([a, b, guess], [bandit(9, 0), bandit(9, 2), bandit(10, -1)], 10).x, b.x);
  assert.equal(fleeShade([a, b, guess], [skel(7, 0)], 10).x, a.x); // one chaser follows anywhere: the nearer
  assert.equal(fleeShade([a, b, guess], [], 10).x, a.x);
  assert.equal(fleeShade([guess], [], 10).x, guess.x); // nothing likely: the best guess
  assert.equal(fleeShade([], [], 10), null);
});
