// Long routes, like a player who has looked at the map: the game's pathfinder solves at most ~100 m
// (its search bounds are +-200 tiles of 0.5 m), and from the starting graveyard the way home is a
// detour (live 2026-09-25: from HeadlessOne's plateau only east to south was reachable; north and west
// are walled off, so walking straight at the castle paced a fence line for an hour). planRoute chains
// pathfinder queries best-first from reachable end points toward the goal, entirely by query (no
// walking into dead ends), and caches the result, so the way is found once and remembered.
//
//   node tools/bots/routes.mjs --bot 1:HeadlessOne --to -1142,-1710    precompute and cache a route
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { Bot } from "./bot.mjs";

const CACHE = path.join(os.tmpdir(), "progenitor-bots", "routes.json");
const HOP = [80, 45];                 // meters per chained query (the solver's reach is ~100 m)
const DIRS = 16;
const SIDES = [0, 5, -5];             // a hop end on a blocked tile fails at once: try beside it
const CELL = 35;                      // visited grid, meters
const EXPANSIONS = 300000;            // live: 0.3 s per 40 m query in the graveyard clutter

function load() { try { return JSON.parse(fs.readFileSync(CACHE, "utf8")); } catch { return {}; } }
function save(c) { try { fs.mkdirSync(path.dirname(CACHE), { recursive: true }); fs.writeFileSync(CACHE, JSON.stringify(c)); } catch { /* best effort */ } }
const keyOf = (from, to) => `${Math.round(from.x / 25)},${Math.round(from.z / 25)}>${Math.round(to.x / 50)},${Math.round(to.z / 50)}`;
const d2 = (a, b) => Math.hypot(a.x - b.x, a.z - b.z);

/** A cached route from near `from` to near `to`, or null. */
export function cachedRoute(from, to) {
  const c = load();
  return c[keyOf(from, to)] ?? null;
}

/**
 * Best-first search over pathfinder hops. Returns {waypoints:[{x,y,z}], queries} (the hop end points,
 * to be walked one by one with goTo/routes), or {failed, queries, best} with the closest point reached.
 */
export async function planRoute(bot, from, to, { maxQueries = 500, goalRadius = 60, log = () => {} } = {}) {
  const cached = cachedRoute(from, to);
  if (cached) return { waypoints: cached, cached: true, queries: 0 };
  const open = [{ p: { x: from.x, y: from.y, z: from.z }, g: 0, parent: null }];
  const seen = new Set([`${Math.round(from.x / CELL)},${Math.round(from.z / CELL)}`]);
  let queries = 0, best = open[0];
  while (open.length && queries < maxQueries) {
    open.sort((a, b) => (a.g + 1.5 * d2(a.p, to)) - (b.g + 1.5 * d2(b.p, to)));
    const node = open.shift();
    if (d2(node.p, to) < d2(best.p, to)) best = node;
    if (d2(node.p, to) <= goalRadius) return finish(node);
    const bearing = Math.atan2(to.x - node.p.x, to.z - node.p.z);
    // Directions nearest the goal first; each direction's first reachable hop becomes a child.
    const dirs = [...Array(DIRS).keys()].map((k) => bearing + (k % 2 ? 1 : -1) * Math.ceil(k / 2) * (2 * Math.PI / DIRS));
    for (const a of dirs) {
      for (const hop of HOP) {
        let got = null;
        for (const side of SIDES) {
          const t = { x: node.p.x + Math.sin(a) * hop + Math.cos(a) * side, y: node.p.y, z: node.p.z + Math.cos(a) * hop - Math.sin(a) * side };
          const cell = `${Math.round(t.x / CELL)},${Math.round(t.z / CELL)}`;
          if (seen.has(cell)) break;
          queries++;
          const p = await bot.path(node.p, t, EXPANSIONS).catch(() => null);
          if (p?.length) { got = p.at(-1); break; }
        }
        if (!got) continue;
        const cell = `${Math.round(got.x / CELL)},${Math.round(got.z / CELL)}`;
        if (seen.has(cell)) continue;
        seen.add(cell);
        // Waypoints carry y = -1 (no height): keep the parent's height for the next query.
        open.push({ p: { x: got.x, y: typeof got.y === "number" && got.y >= 0 ? got.y : node.p.y, z: got.z }, g: node.g + d2(node.p, got), parent: node });
        break; // the longest hop that works in this direction
      }
      if (queries >= maxQueries) break;
    }
    log(`route: ${queries} queries, open ${open.length}, best ${d2(best.p, to).toFixed(0)} m from the goal`);
  }
  return { failed: true, queries, best: best.p, partial: chain(best) };

  function chain(n) { const out = []; for (; n; n = n.parent) out.unshift(n.p); return out.slice(1); }
  function finish(n) {
    const waypoints = chain(n);
    const c = load(); c[keyOf(from, to)] = waypoints; save(c);
    return { waypoints, queries };
  }
}

// CLI: precompute a route for a bot's current position.
if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  const arg = (k) => { const i = process.argv.indexOf(`--${k}`); return i > 0 ? process.argv[i + 1] : null; };
  const [i, name] = (arg("bot") ?? "1:HeadlessOne").split(":");
  const [tx, tz] = (arg("to") ?? "").split(",").map(Number);
  const bot = new Bot(Number(i), name);
  const me = await bot.self();
  const t0 = Date.now();
  const r = await planRoute(bot, me, { x: tx, z: tz }, { maxQueries: Number(arg("queries") ?? 600), log: (m) => console.log(m) });
  console.log(JSON.stringify({ ...r, seconds: Math.round((Date.now() - t0) / 1000), waypoints: r.waypoints?.map((p) => [Math.round(p.x), Math.round(p.z)]), partial: r.partial?.map((p) => [Math.round(p.x), Math.round(p.z)]) }));
}
