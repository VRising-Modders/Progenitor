// Block-scale planning experiment. A candidate is never native placement approval.
export const key = (x, y) => `${x},${y}`;
const steps = [[0, 1], [1, 0], [0, -1], [-1, 0]];
const adjacent = c => steps.map(([x, y]) => key(c.x + x, c.y + y));
export function flood(cells, start, excluded = new Set()) {
  if (!cells.has(start) || excluded.has(start)) return new Set();
  const found = new Set([start]), queue = [start];
  for (let i = 0; i < queue.length; i++) {
    const c = cells.get(queue[i]);
    for (const n of adjacent(c)) {
      if (found.has(n) || excluded.has(n) || !cells.has(n)) continue;
      if (Math.abs(c.height - cells.get(n).height) > .25) continue;
      found.add(n); queue.push(n);
    }
  }
  return found;
}

export function analyze(snapshot, options = {}) {
  const coverage = options.coverage ?? .4;
  if (!Number.isFinite(coverage) || coverage < 0 || coverage > 1)
    throw Error('coverage must be between 0 and 1');
  if (!Number.isInteger(snapshot.floorLimit) || snapshot.floorLimit < 0 ||
      !Number.isInteger(snapshot.floorCount) || snapshot.floorCount < 0)
    throw Error('A readable running-server floor limit and usage are required');
  const cells = new Map(), rejected = [];
  for (const b of snapshot.blocks) {
    const k = key(b.x, b.y);
    if (cells.has(k)) throw Error('Duplicate plot block');
    const h = b.heights;
    if (!h || h.length !== 5 || h.some(v => !Number.isFinite(v)) || Math.max(...h) - Math.min(...h) > .25) {
      rejected.push({ x: b.x, y: b.y, reason: 'missing or uneven ground samples' }); continue;
    }
    cells.set(k, { x: b.x, y: b.y, height: h[0] });
  }
  // Preserve buildings on every storey. A footprint is a conservative reservation:
  // we do not propose building underneath existing stairs, walls, or upper floors.
  const occupied = new Set();
  // The heart does not necessarily have CastleHeartConnection, so it can be absent
  // from the building list and must be reserved independently.
  if (snapshot.heart) {
    if (![snapshot.heart.x, snapshot.heart.y].every(Number.isInteger))
      throw Error('Unreadable heart block');
    occupied.add(key(snapshot.heart.x, snapshot.heart.y));
  }
  for (const b of snapshot.buildings ?? []) {
    if (![b.minX,b.minY,b.maxX,b.maxY].every(Number.isFinite)) throw Error('Unreadable building bounds');
    for (let y = Math.floor(b.minY / 10); y <= Math.floor((Math.max(b.minY + 1,b.maxY) - 1) / 10); y++)
      for (let x = Math.floor(b.minX / 10); x <= Math.floor((Math.max(b.minX + 1,b.maxX) - 1) / 10); x++)
        occupied.add(key(x,y));
  }
  for (const f of snapshot.floors ?? []) occupied.add(key(Math.floor(f.x/10),Math.floor(f.y/10)));
  const free = new Map([...cells].filter(([k]) => !occupied.has(k)));
  // Approaches must be supplied by a terrain/access audit. Never invent a cliff entrance.
  const entrances = [...new Set(options.entrances ?? [])];
  const reserve = new Set(options.reserve ?? []);
  for (const k of [...entrances, ...reserve])
    if (!free.has(k)) throw Error(`Reserved access ${k} is not a free, level block`);
  const reasons = [...(snapshot.notChecked ?? [])];
  if (!snapshot.heart) reasons.push('Heart position missing from this snapshot');
  if (!entrances.length) reasons.push('No externally verified plot entrance supplied');
  const reachable = entrances.length ? flood(free, entrances[0]) : new Set();
  if (entrances.some(k => !reachable.has(k))) reasons.push('Entrances are disconnected at ground level');
  for (const k of entrances) reserve.add(k);
  const usable = [...free.keys()].filter(k => reachable.has(k) && !reserve.has(k));
  const available = Math.max(0, snapshot.floorLimit - snapshot.floorCount);
  const target = Math.min(available, Math.floor(usable.length * coverage));
  const selected = new Set(), rooms = [];
  // Larger complete rectangles first; never partial strips to hit a percentage.
  const sizes = [[4,4],[4,3],[3,4],[3,3],[3,2],[2,3],[2,2]];
  const candidates = [...free.values()].sort((a,b) => a.y-b.y || a.x-b.x);
  for (const [width,height] of sizes) {
    for (const origin of candidates) {
      if (selected.size + width*height > target) continue;
      const body = [];
      for (let y=origin.y;y<origin.y+height;y++)
        for (let x=origin.x;x<origin.x+width;x++) body.push(key(x,y));
      if (body.some(k => !reachable.has(k) || reserve.has(k) || selected.has(k) ||
          !free.has(k) || Math.abs(free.get(k).height-origin.height) > .25)) continue;
      const excluded = new Set([...selected,...body]);
      const outside = flood(free, entrances[0], excluded);
      if ([...reserve].some(k => !outside.has(k))) continue;
      // Keep every previously chosen doorway accessible when later rooms are added.
      if (rooms.some(r => !outside.has(r.door.outside))) continue;
      let door;
      for (const k of body) {
        const c = free.get(k);
        const exit = adjacent(c).find(n => outside.has(n) &&
          Math.abs(free.get(n).height-c.height) <= .25);
        if (exit) { door = { inside:k, outside:exit }; break; }
      }
      if (!door) continue;
      const roomCells = new Map(body.map(k => [k, free.get(k)]));
      if (flood(roomCells, door.inside).size !== body.length) continue;
      body.forEach(k => selected.add(k));
      rooms.push({ x:origin.x,y:origin.y,width,height,groundHeight:origin.height,door,cells:body });
    }
  }
  return {
    schema:1,plot:snapshot.plot,heartLevel:snapshot.heartLevel,
    floorLimit:snapshot.floorLimit,existingFloors:snapshot.floorCount,remainingFloorAllowance:available,
    freeGroundBlocks:free.size,eligibleGroundBlocks:usable.length,coverageRequested:coverage,
    floorTarget:target,newFloors:selected.size,remainingAfterPlan:available-selected.size,
    coverageAchieved:usable.length ? selected.size/usable.length : 0,
    rooms,entrances,reserved:[...reserve],occupied:[...occupied],rejected,
    blocks:[...cells.values()],constructionReady:false,
    notChecked:[...new Set([...reasons,'Native roof formation','Precise player and station clearance',
      'Buildable tile mask and native placement rules','Resource and unlock authorization'])],
    explanation: rooms.length ? 'Complete room candidates; spare capacity intentionally unused.'
      : 'No complete accessible room fits the supplied constraints.'
  };
}
