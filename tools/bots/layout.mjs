// Castle layout around a heart pivot (hx, hz), world X/Z = 2.5 (mod 5). Rules, live-read from the
// server's placement log (internal research notes, 2026-09-24):
//  - every floor, the heart included, is ONE 5 m block (its pivot cell). A new floor must share a face
//    (north/east/south/west) with an existing floor block at the same height; the heart's 15 m
//    footprint does not count, so the first floors go right next to the heart's center (+-5 m,
//    overlapping its footprint, which carries no floor type) and the castle grows outward from there.
//    Every floor sent two blocks out (heart + (-5,-10) etc.) was refused: RequiresFloorConnection.
//  - a floor whose 12 x 12-tile footprint touches the plot edge is refused (CastleAreaNotOwnedByUser);
//    live trees and rocks in it block it, bushes and rubble do not.
//  - walls/palisades snap to cell-edge midpoints (Edge10x10): rotation 0 on an edge at x = 0 (mod 5)
//    running along Z, rotation 1 on an edge at z = 0 (mod 5) running along X.
// The server refuses silently, so the brain reads each placement back and skips a refused spot.
export const CELL = 5;

// Floor blocks in build order: breadth-first from the heart's center block, so each one touches one
// already built. East first (the side found clear on HeadlessOne's plot), then south, west, north.
export const FLOOR_ORDER = (() => {
  const order = [];
  const done = new Set(["0,0"]);
  let frontier = [[0, 0]];
  const dirs = [[5, 0], [0, -5], [-5, 0], [0, 5]];
  while (frontier.length) {
    const next = [];
    for (const [x, z] of frontier) {
      for (const [dx, dz] of dirs) {
        const c = [x + dx, z + dz];
        const key = `${c[0]},${c[1]}`;
        if (done.has(key) || Math.max(Math.abs(c[0]), Math.abs(c[1])) > 15) continue;
        done.add(key);
        order.push(c);
        next.push(c);
      }
    }
    frontier = next;
  }
  return order;
})();

// Objects stand on floor blocks clear of the heart model (its footprint is +-7.5 m), each one among
// the first floors built (inferred: most objects need a floor under them).
const OBJECT_SPOTS = {
  coffin: [[10, 0]],
  stash: [[0, -10], [-10, 5]],      // floor indices 7, 21 (0-based FLOOR_ORDER)
  brazier: [[-10, 0]],
  workbench: [[0, 10]],
  sawmill: [[10, 5], [5, 10]],       // floor indices 14, 16
  furnace: [[10, -5], [5, -10]],     // floor indices 13, 15
};

/** Wall edges: the outer edge of the east column first, then around the grown floor area. */
function wallEdges() {
  const edges = [];
  for (const z of [0, -5, 5]) edges.push([12.5, z, 0]);
  for (const x of [0, 5, -5, 10, -10]) edges.push([x, -12.5, 1], [x, 12.5, 1]);
  for (const z of [0, -5, 5]) edges.push([-12.5, z, 0]);
  for (const z of [-10, 10]) edges.push([12.5, z, 0], [-12.5, z, 0]);
  return edges;
}
const WALLS = wallEdges();

/** The i-th spot for a structure kind around heart pivot `h` ({x, z}); null when the layout has no more. */
/**
 * Wall spots on the outer edges of the floors actually placed (internal research notes):
 * a wall's length runs along its local X, so east/west edges (cx +- 2.5, cz) take rotation 1 and
 * north/south edges (cx, cz +- 2.5) rotation 0; an edge needs a real floor on one side (the heart's own
 * tiles do not count) and no floor on the other. East edges first (the old fixed layout assumed a floor a
 * pine had blocked, and sent rotation 0 on east edges: every wall was refused, live 2026-09-25).
 */
export function wallSpotsFromFloors(floors, heart) {
  const key = (x, z) => `${Math.round(x * 2)},${Math.round(z * 2)}`;
  const cells = new Set(floors.map((f) => key(f.x, f.z)));
  const heartCell = heart ? key(heart.x, heart.z) : null;
  const spots = [];
  const seen = new Set();
  for (const f of floors) {
    for (const [dx, dz, rotation, side] of [[5, 0, 1, 0], [0, -5, 0, 1], [0, 5, 0, 1], [-5, 0, 1, 2]]) {
      const nx = f.x + dx, nz = f.z + dz;
      if (cells.has(key(nx, nz)) || key(nx, nz) === heartCell) continue; // an inner edge
      const x = f.x + dx / 2, z = f.z + dz / 2;
      if (seen.has(key(x, z))) continue;
      seen.add(key(x, z));
      spots.push({ x, z, rotation, side, d: heart ? Math.hypot(x - heart.x, z - heart.z) : 0 });
    }
  }
  return spots.sort((a, b) => a.side - b.side || b.x - a.x || a.d - b.d).map(({ x, z, rotation }) => ({ x, z, rotation }));
}

/**
 * Spots for castle objects (coffin, stash, brazier, stations) on the floors actually placed: floor
 * cells with no other building on them, farthest from the heart first (the fixed layout put the coffin
 * on a cell a pine had kept free of floor).
 */
export function objectSpotsFromFloors(buildings, heart, near = null) {
  const floors = buildings.filter((b) => /Floor/i.test(b.name ?? ""));
  const others = buildings.filter((b) => !/Floor|Wall|Pillar|CastleHeart|BloodFountain/i.test(b.name ?? ""));
  return floors.filter((f) => !others.some((o) => Math.hypot(o.x - f.x, o.z - f.z) < 2.5))
    // Next to `near` (the brazier by the coffin), else farthest from the heart first.
    .sort((a, b) => near ? Math.hypot(a.x - near.x, a.z - near.z) - Math.hypot(b.x - near.x, b.z - near.z)
      : Math.hypot(b.x - heart.x, b.z - heart.z) - Math.hypot(a.x - heart.x, a.z - heart.z))
    .map((f) => ({ x: f.x, z: f.z, rotation: 0 }));
}

export function castleLayout(kind, i, h) {
  const at = ([dx, dz], rotation = 0) => ({ x: h.x + dx, z: h.z + dz, rotation });
  if (kind === "floor") return i < FLOOR_ORDER.length ? at(FLOOR_ORDER[i]) : null;
  if (kind === "wall") return i < WALLS.length ? at(WALLS[i], WALLS[i][2]) : null;
  const spots = OBJECT_SPOTS[kind];
  return spots && i < spots.length ? at(spots[i]) : null;
}

/**
 * The first roofed room: a ring of castle walls, one of them the entrance, around the coffin's floor
 * block (alone or with one neighboring floor). Only an enclosed room gets a roof, and the roof test
 * ignores the sun angle (internal research notes, HasRoof): the castle's day shade.
 * Candidates are scored by pieces still missing (fewest first, then the larger room). Returns
 * {cells, missing: [{x, z, rotation, entrance}], center} or null (no coffin or floors yet); `refused`
 * holds "x,z" edges the server refused, whose rooms are skipped.
 */
export function roomPlan(buildings, heart, refused = new Set()) {
  const key = (x, z) => `${Math.round(x * 2)},${Math.round(z * 2)}`;
  const floors = buildings.filter((b) => /Floor/i.test(b.name ?? ""));
  const cells = new Map(floors.map((f) => [key(f.x, f.z), f]));
  const coffin = buildings.find((b) => /Coffin/i.test(b.name ?? ""));
  if (!coffin || !heart) return null;
  // Not on the heart's footprint (15 m, blocks within 5 m of its center): HeadlessOne's closed ring
  // there read CastleRoom {IsMissingWalls: true, HasRoof: false}, one room with every floor, live
  // 2026-09-25; the heart's tiles carry no floor types (bot-wall-placement.md). Hypothesis: a room
  // needs blocks off the footprint. Else the coffin's block, else the off-footprint floor nearest it.
  const onHeart = (c) => Math.abs(c.x - heart.x) <= 5.1 && Math.abs(c.z - heart.z) <= 5.1;
  const off = floors.filter((f) => !onHeart(f));
  const home = off.find((f) => Math.abs(f.x - coffin.x) <= 2.5 && Math.abs(f.z - coffin.z) <= 2.5)
    ?? off.sort((a, b) => Math.hypot(a.x - coffin.x, a.z - coffin.z) - Math.hypot(b.x - coffin.x, b.z - coffin.z))[0];
  if (!home) return null;
  const heartKey = key(heart.x, heart.z);
  const walls = buildings.filter((b) => /Wall|Entrance|Gate|Door/i.test(b.name ?? "") && !/Pillar/i.test(b.name ?? ""));
  const DIRS = [[5, 0, 1], [-5, 0, 1], [0, 5, 0], [0, -5, 0]];
  const candidates = [[home], ...DIRS.map(([dx, dz]) => cells.get(key(home.x + dx, home.z + dz))).filter((c) => c && key(c.x, c.z) !== heartKey && !onHeart(c)).map((c) => [home, c])];
  let best = null;
  for (const room of candidates) {
    const inRoom = new Set(room.map((c) => key(c.x, c.z)));
    const edges = [];
    for (const c of room) {
      for (const [dx, dz, rotation] of DIRS) {
        if (inRoom.has(key(c.x + dx, c.z + dz))) continue;
        const x = c.x + dx / 2, z = c.z + dz / 2;
        const outside = cells.get(key(c.x + dx, c.z + dz));
        edges.push({ x, z, rotation, fromFloor: !!outside && key(outside.x, outside.z) !== heartKey, built: walls.some((w) => Math.hypot(w.x - x, w.z - z) < 1.5) });
      }
    }
    if (edges.some((e) => refused.has(`${e.x},${e.z}`))) continue;
    const hasEntrance = walls.some((w) => /Entrance|Gate|Door/i.test(w.name ?? "") && edges.some((e) => Math.hypot(w.x - e.x, w.z - e.z) < 1.5));
    const missing = edges.filter((e) => !e.built);
    // The entrance goes where the bot walks in from a floor, else on any open edge.
    if (!hasEntrance && missing.length) {
      const door = missing.find((e) => e.fromFloor) ?? missing[0];
      door.entrance = true;
    }
    const score = missing.length * 10 - room.length;
    if (!best || score < best.score) {
      best = { score, cells: room.map((c) => ({ x: c.x, z: c.z })), missing: missing.map(({ x, z, rotation, entrance }) => ({ x, z, rotation, entrance: !!entrance })),
        center: { x: room.reduce((s, c) => s + c.x, 0) / room.length, z: room.reduce((s, c) => s + c.z, 0) / room.length } };
    }
  }
  if (!best) return null;
  // The entrance first: once the walls stand, a closed ring would shut the bot out (or in).
  best.missing.sort((a, b) => b.entrance - a.entrance);
  return best;
}
