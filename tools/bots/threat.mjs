// Perception with memory and one threat measure (internal research notes
// sections 3 and 5). Pure: no bridge calls; the brain feeds it what it sees.
//
// Tracks: one entry per hostile the bot has seen, kept after it leaves the view (a pack out of view
// after a turn, or behind the 80-unit look limit, is still there). Keyed by prefab name and a 3 m
// cell: bot_look (client) and bot_threats (server) name different entity ids for one unit
// (skills.mjs sameUnit), so identity is name + position. A moving unit is matched to its own track
// by name within the distance it could have walked since, and re-keyed. A track inside the area the
// view covered that was not seen again is gone (killed or moved on) and is dropped; others expire
// after 90 s (V Blood 10 min). Recent death spots (bot.dangerZones) are fixed entries.
//
// threatAt: the summed strength of remembered hostiles around a point, each weighted by distance
// (influence-map style: a pack of weak units adds up where one unit's level would not):
//   strength = max(0.75, 1 + (level - gear) / 5); V Blood at least 5; harmless units 0
//   weight   = strength * (1 inside the unit's reach, fading linearly to 0 over the next 15 m)
//   reach    = 15 m; 25 m for a V Blood or a unit more than 3 levels above gear
// The 0.75 floor makes any hostile standing next to a target count against it, as the old
// "no other hostile within 12 m" rules did, and two weak ones count as more than one fair one.

export const TRACK_MS = 90000;               // a hostile out of view is remembered this long...
export const VBLOOD_TRACK_MS = 10 * 60000;   // ...a V Blood this long (they roam near the castles)
export const CELL = 3;                       // meters: the key's position cell
export const REACH = 15;                     // full weight within this of a hostile...
export const WIDE_REACH = 25;                // ...of a V Blood or an out-leveled unit
export const FADE = 15;                      // then fading to 0 over this many meters
export const AVOID_MARGIN = 3;               // "out-leveled": more than this many levels above gear
export const STRENGTH_FLOOR = 0.75;
export const VBLOOD_STRENGTH = 5;
// Where a bot died recently (and brutes seen on trips): a fixed entry. Above the prey budget, so
// a food roam does not walk back to the prey spot next to the camp that just killed the bot.
export const ZONE_STRENGTH = 0.8, ZONE_REACH = 15, ZONE_MS = 30 * 60000;
// Budgets (threat from others, the target itself excluded; strictly below):
export const PREY_BUDGET = 0.75;   // prey, guards, critters, roam spots: no other hostile at full weight
export const TRAIN_BUDGET = 1.5;   // the trainer: one companion up to gear + 2, not two
export const OUTMATCHED = 1.5;     // engaged strength: two weak attackers, or one above gear + 2
export const PACKED = 2.25;        // three weak attackers (four level-10 bandits killed a gear-23 bot)
// Units that flee rather than fight back, or never fight: no threat (a bandit camp's prisoner
// villager, level 14, made a bot abandon a fair fight, live 2026-09-25). brain.mjs HARMLESS plus
// prisoners and critters.
export const HARMLESS_THREAT = /Deer|(?<!Hostile)Villager|Prisoner|Critter|Vermin|Rabbit/;

const isVBlood = (t) => !!t.vblood || /VBlood/.test(t.name ?? "");
const values = (tracks) => (tracks == null ? [] : tracks instanceof Map ? [...tracks.values()] : [...tracks]);

/** Map key: name and 3 m cell. */
export const trackKey = (name, x, z) => `${name}@${Math.floor((x ?? 0) / CELL)},${Math.floor((z ?? 0) / CELL)}`;

/** The same unit (name within `r` meters), whichever op reported it. */
export const sameTrack = (a, b, r = CELL) => !!a && !!b && a.name === b.name && Math.hypot((a.x ?? 0) - (b.x ?? 0), (a.z ?? 0) - (b.z ?? 0)) < r;

/** A hostile's strength against the bot's gear level; 0 for harmless units. */
export function strength(t, gear = 0) {
  if (t.fixed) return t.strength ?? ZONE_STRENGTH;
  if (HARMLESS_THREAT.test(t.name ?? "")) return 0;
  const base = Math.max(STRENGTH_FLOOR, 1 + ((t.level ?? gear) - gear) / 5);
  return isVBlood(t) ? Math.max(VBLOOD_STRENGTH, base) : base;
}

/** The distance within which a hostile weighs fully; `radius` overrides the ordinary 15 m. */
export function reachOf(t, gear = 0, radius = REACH) {
  if (t.fixed) return t.reach ?? ZONE_REACH;
  return isVBlood(t) || (t.level ?? 0) > gear + AVOID_MARGIN ? Math.max(radius, WIDE_REACH) : radius;
}

const falloff = (d, reach) => (d <= reach ? 1 : d >= reach + FADE ? 0 : (reach + FADE - d) / FADE);

/**
 * Each remembered hostile's weight at `point` (sorted, heaviest first; zero weights left out).
 * `tracks`: a Map from newTracks/updateTracks, or an array of units ({name, level, x, z}).
 * Options: `radius` (ordinary reach), `exclude` (a unit whose own weight is not counted: the
 * target), `onlyOnMe` (units aggroed on the bot only). A unit without a position is taken at its
 * `distance` (from the bot), else at the point.
 */
export function threatNear(tracks, point, gear = 0, { radius = REACH, exclude = null, onlyOnMe = false } = {}) {
  const out = [];
  const all = values(tracks);
  // The excluded unit is one track: by identity or entity id, else the nearest of its name within
  // 3 m (a packmate standing next to it still counts).
  let skip = null;
  if (exclude) {
    skip = all.find((t) => t === exclude || (t.entity !== undefined && t.entity === exclude.entity))
      ?? all.filter((t) => sameTrack(t, exclude)).sort((a, b) => Math.hypot(a.x - exclude.x, a.z - exclude.z) - Math.hypot(b.x - exclude.x, b.z - exclude.z))[0] ?? null;
  }
  for (const t of all) {
    if (t === skip) continue;
    if (onlyOnMe && !t.onMe) continue;
    const s = strength(t, gear);
    if (s <= 0) continue;
    const d = t.x === undefined || point?.x === undefined ? (t.distance ?? 0) : Math.hypot(t.x - point.x, (t.z ?? 0) - (point.z ?? 0));
    const w = s * falloff(d, reachOf(t, gear, radius));
    if (w > 0) out.push({ track: t, weight: w, distance: d });
  }
  return out.sort((a, b) => b.weight - a.weight);
}

/** The weighted threat at `point`: see threatNear for the options. */
export const threatAt = (tracks, point, gear = 0, opts = {}) => threatNear(tracks, point, gear, opts).reduce((n, x) => n + x.weight, 0);

/** Summed strength of a set of units regardless of distance (the ones already on the bot). */
export const threatOf = (units, gear = 0) => values(units).reduce((n, u) => n + strength(u, gear), 0);

export const newTracks = () => new Map();

const lifetime = (t) => (t.fixed ? Infinity : isVBlood(t) ? VBLOOD_TRACK_MS : TRACK_MS);

/**
 * Fold one observation into `tracks` (mutated and returned).
 *  units:   hostiles in the view (bot_look rows: name, level, x, z, entity)
 *  threats: bot_threats rows (server; name, level, x, z, onMe), optional
 *  self, covered: the bot's position and the radius the view is complete within; an unseen track
 *           inside it is gone. Pass covered 0 (or no self) to drop nothing.
 *  zones:   bot.dangerZones ({x, z, at}), refreshed as fixed entries
 */
export function updateTracks(tracks, units = [], { now = Date.now(), threats = [], self = null, covered = 0, zones = [] } = {}) {
  for (const [k, t] of tracks) if (t.fixed ? t.expires <= now : now - t.seenAt > lifetime(t)) tracks.delete(k);
  const seen = new Set();
  // `client`: a bot_look row, whose entity id is kept (server ids from bot_threats are another space).
  const place = (t, u, client = true) => {
    if (tracks.get(t.key) === t) tracks.delete(t.key);
    // Two of a pack can share a cell: a suffix keeps both.
    const base = trackKey(t.name, u.x, u.z);
    let key = base;
    for (let i = 1; tracks.has(key); i++) key = `${base}#${i}`;
    Object.assign(t, { x: u.x, y: u.y ?? t.y, z: u.z, level: u.level ?? t.level, seenAt: now, key });
    if (client && u.entity !== undefined) t.entity = u.entity;
    if (u.vblood) t.vblood = true;
    tracks.set(key, t);
    seen.add(t);
  };
  const live = () => [...tracks.values()].filter((t) => !t.fixed);
  for (const u of units ?? []) {
    if (u?.name === undefined || u.x === undefined) continue;
    // The nearest unmatched track of the same name within the distance the unit could have run.
    let best = null, bestD = Infinity;
    for (const t of live()) {
      if (t.name !== u.name || seen.has(t)) continue;
      const d = Math.hypot(t.x - u.x, t.z - u.z);
      const reach = Math.min(25, CELL + ((now - t.seenAt) / 1000) * 7);
      if (d <= reach && d < bestD) { best = t; bestD = d; }
    }
    const t = best ?? { name: u.name, onMe: false };
    t.onMe = false; // set again below from bot_threats
    place(t, u);
  }
  for (const r of threats ?? []) {
    if (r?.name === undefined || r.x === undefined) continue;
    // Server rows: the same unit as a client row within a few meters (interpolation lag).
    const same = live().filter((t) => t.name === r.name && Math.hypot(t.x - r.x, t.z - r.z) < 4)
      .sort((a, b) => (seen.has(b) - seen.has(a)) || Math.hypot(a.x - r.x, a.z - r.z) - Math.hypot(b.x - r.x, b.z - r.z))[0];
    if (same) {
      same.onMe = !!r.onMe;
      if (r.level !== undefined) same.level = r.level;
      if (r.vblood) same.vblood = true;
      same.seenAt = now;
      seen.add(same);
    } else place({ name: r.name, onMe: !!r.onMe }, r, false);
  }
  if (self?.x !== undefined && covered > 0) {
    for (const t of live()) {
      if (!seen.has(t) && Math.hypot(t.x - self.x, t.z - self.z) < covered - 5) tracks.delete(t.key);
    }
  }
  for (const z of zones ?? []) {
    if (z?.x === undefined || now - (z.at ?? 0) >= ZONE_MS) continue;
    const key = trackKey("zone", z.x, z.z);
    tracks.set(key, { key, name: "danger zone", fixed: true, x: z.x, z: z.z, strength: ZONE_STRENGTH, reach: ZONE_REACH, expires: (z.at ?? now) + ZONE_MS, seenAt: z.at ?? now });
  }
  return tracks;
}

/** A compact summary for logs: the threat at the bot and the three heaviest contributors. */
export function threatSummary(tracks, self, gear = 0) {
  const near = threatNear(tracks, self, gear);
  return {
    here: Math.round(near.reduce((n, x) => n + x.weight, 0) * 100) / 100,
    tracks: tracks instanceof Map ? tracks.size : values(tracks).length,
    top: near.slice(0, 3).map((x) => `${(x.track.name ?? "").replace(/^CHAR_/, "")}@${x.distance.toFixed(0)}m=${x.weight.toFixed(2)}`),
  };
}
