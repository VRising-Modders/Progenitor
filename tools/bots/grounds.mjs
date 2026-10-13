// Hunting grounds: where living prey has been seen and fed on, kept across rounds and shared by all
// bots, the way a player learns where the deer graze. Food roams went in random 50 m directions and
// found nothing 25 times in 45 minutes (999 s, live 2026-09-25); a roam now heads for the best ground.
// A ground is a 30 m cell: sightings and feeds (a feed counts 3), with the time last seen.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const FILE = path.join(os.tmpdir(), "progenitor-bots", "hunt-grounds.json");
const CELL = 30;
const HALF_LIFE_MS = 60 * 60000;   // a ground's weight halves per hour since last seen
let grounds = null, dirty = 0, persist = true;

function load() {
  if (grounds) return grounds;
  try { grounds = JSON.parse(fs.readFileSync(FILE, "utf8")); } catch { grounds = {}; }
  return grounds;
}
function save() {
  if (!persist) return;
  try { fs.mkdirSync(path.dirname(FILE), { recursive: true }); fs.writeFileSync(FILE, JSON.stringify(grounds)); } catch { /* best effort */ }
}
const cellOf = (p) => `${Math.floor(p.x / CELL)},${Math.floor(p.z / CELL)}`;

/** Count prey at `p` (a sighting weighs 1, a feed 3). Saved every 20 records. */
export function recordGround(p, { fed = false, name = "", now = Date.now() } = {}) {
  const g = load();
  const k = cellOf(p);
  const e = (g[k] ??= { x: (Math.floor(p.x / CELL) + 0.5) * CELL, z: (Math.floor(p.z / CELL) + 0.5) * CELL, n: 0, fed: 0, at: 0, names: [] });
  e.n += fed ? 3 : 1;
  if (fed) e.fed++;
  e.at = now;
  if (name && !e.names.includes(name) && e.names.length < 5) e.names.push(name);
  if (++dirty % 20 === 0 || fed) save();
}

/**
 * The best ground to roam to from `me`: weight (decayed by age) over distance, within `maxDist`, not
 * visited in the last 5 min (`visited`: cell key -> time), and `ok(point)` (calm, no threat) true.
 */
export function bestGround(me, { maxDist = 400, minDist = 60, visited = {}, ok = () => true, now = Date.now() } = {}) {
  let best = null;
  for (const [k, e] of Object.entries(load())) {
    const d = Math.hypot(e.x - me.x, e.z - me.z);
    if (d > maxDist || d < minDist || (visited[k] && now - visited[k] < 5 * 60000) || !ok(e)) continue;
    const w = e.n * Math.pow(0.5, (now - e.at) / HALF_LIFE_MS);
    const score = w / (1 + d / 100);
    if (!best || score > best.score) best = { ...e, key: k, score, distance: d };
  }
  return best;
}

/** For tests: replace the store (and never write it to disk). */
export function _setGrounds(g) { grounds = g; persist = false; }
export { cellOf };
