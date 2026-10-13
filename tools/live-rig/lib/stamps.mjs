// Build stamps recorded on every live-rig report so catalog inspect can compare
// a run with recipe evidence `build` / `currentBuild`.
//
// tools/catalog/lib/rigRuns.mjs reads:
//   contentCatalogSha256, gameIdentity  — top-level strings on the report
//   frameworkSides                      — { server, client } per-side strings
//   framework                           — always null on new reports (a non-null
//                                         value is a legacy combined stamp)
//   stampErrors, stampPhases, stampTransition, gameIdentitySides
//
// inventory.mjs does not hash the game install: it copies source.catalogSha256
// from the generated inventory, which index-catalog.mjs sets to
// createHash('sha256').update(archive_dependencies.bin).digest('hex').
// There is no exported catalog hasher; this file repeats that exact hash on
// the client install, read-only. Never guess: unreadable file → null + stampErrors.
// Never fill a client framework part from the server ping or vice versa.

import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { sampleHashFromPing } from './reload.mjs';

/** Same default as tools/catalog/index-catalog.mjs and lib/bridge.mjs. */
export const DEFAULT_GAME_INSTALL = process.env.PROGENITOR_GAME_DIR
  ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRising';

/** Relative to the game install root. index-catalog.mjs k_ContentCatalogFilename. */
export const CONTENT_CATALOG_REL = path.join(
  'VRising_Data',
  'StreamingAssets',
  'ContentArchives',
  'archive_dependencies.bin',
);

/**
 * Ping → gameIdentity mapping (PrefabEditBuildIdentity.Compose / recipe form
 * `vrising/<version>+r<revision>+b<build>/<runtime>` e.g. vrising/1.1.13.0+r99712+b17/net25).
 *
 * Dev `ping` (Handlers.Ping) returns `gameIdentity` (that Compose string, or null)
 * and `images`: [{name, sha256, generation?}] for Core plus each reloaded plugin.
 * Core is named "Core" and carries generation; plugin rows use the file stem
 * ("Progenitor.Dev", "Progenitor.Sample") with no generation. sha256 and images
 * may be null. When identity fields are absent the stamp is null and stampErrors
 * records that — never inferred from progenitor or the catalog hash.
 *
 * Accepted when present, in order:
 *   1. ping.gameIdentity — used as-is when it starts with `vrising/`
 *   2. composed from version bags ping.versionData | ping.gameVersion | ping.version
 *      (object) | ping.game | ping itself, reading Major/major, Minor/minor,
 *      Patch/patch, Revision/revision, VcsRevision/vcsRevision, Build/build,
 *      NetworkVersion/networkVersion (or runtime/net as `net<n>` or an int)
 */
export const GAME_IDENTITY_PING_FIELDS = Object.freeze([
  'gameIdentity',
  'versionData',
  'gameVersion',
  'version',
  'game',
  'Major', 'major', 'Minor', 'minor', 'Patch', 'patch',
  'Revision', 'revision', 'VcsRevision', 'vcsRevision',
  'Build', 'build', 'NetworkVersion', 'networkVersion', 'runtime', 'net',
]);

const GAME_IDENTITY_PREFIX = 'vrising/';
const FRAMEWORK_SIDES = Object.freeze(['server', 'client']);
const FRAMEWORK_PARTS = Object.freeze(['generation', 'core', 'dev', 'sample']);

function sha256Hex(data) {
  return createHash('sha256').update(data).digest('hex');
}

export function contentCatalogPath(gameInstall = DEFAULT_GAME_INSTALL) {
  return path.join(gameInstall, CONTENT_CATALOG_REL);
}

/**
 * sha256 of the shipped content catalog, matching index-catalog.mjs
 * `source.catalogSha256` / inventory `source.catalogSha256`.
 * @returns {{ sha256: string|null, file: string, error: string|null }}
 */
export function hashContentCatalog(gameInstall = DEFAULT_GAME_INSTALL, io = fs) {
  const file = contentCatalogPath(gameInstall);
  let data;
  try {
    data = io.readFileSync(file);
  } catch (e) {
    return {
      sha256: null,
      file,
      error: `content catalog unreadable: ${file} (${e && e.message ? e.message : e})`,
    };
  }
  if (data == null) {
    return { sha256: null, file, error: `content catalog unreadable: ${file}` };
  }
  return { sha256: sha256Hex(data), file, error: null };
}

function num(v) {
  if (v == null || v === '') return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

function networkVersionOf(v) {
  if (v == null || v === '') return null;
  if (typeof v === 'string') {
    const m = /^net(\d+)$/i.exec(v.trim());
    if (m) return Number(m[1]);
    return num(v);
  }
  return num(v);
}

function factsFromBag(b) {
  if (!b || typeof b !== 'object') return null;
  const major = num(b.major ?? b.Major);
  const minor = num(b.minor ?? b.Minor);
  const patch = num(b.patch ?? b.Patch);
  const revision = num(b.revision ?? b.Revision);
  const vcsRevision = num(b.vcsRevision ?? b.VcsRevision ?? b.vcs);
  const build = num(b.build ?? b.Build);
  const networkVersion = networkVersionOf(b.networkVersion ?? b.NetworkVersion ?? b.runtime ?? b.net);
  if ([major, minor, patch, revision, vcsRevision, build, networkVersion].some((n) => n == null)) {
    return null;
  }
  if (major < 0 || minor < 0 || patch < 0 || revision < 0 || vcsRevision < 0 || build < 0) {
    return null;
  }
  if (major === 0 && minor === 0 && patch === 0 && revision === 0) return null;
  return { major, minor, patch, revision, vcsRevision, build, networkVersion };
}

/** Compose the catalog/recipe identity string from version facts. */
export function composeGameIdentity(facts) {
  if (!facts) return null;
  return `vrising/${facts.major}.${facts.minor}.${facts.patch}.${facts.revision}+r${facts.vcsRevision}+b${facts.build}/net${facts.networkVersion}`;
}

function versionBags(ping) {
  const bags = [];
  if (ping.versionData && typeof ping.versionData === 'object') bags.push(ping.versionData);
  if (ping.gameVersion && typeof ping.gameVersion === 'object') bags.push(ping.gameVersion);
  if (ping.version && typeof ping.version === 'object') bags.push(ping.version);
  if (ping.game && typeof ping.game === 'object') bags.push(ping.game);
  bags.push(ping);
  return bags;
}

/**
 * @returns {{ identity: string|null, error: string|null }}
 */
export function gameIdentityFromPing(ping) {
  if (!ping || typeof ping !== 'object') {
    return { identity: null, error: 'ping is missing; no game version/build identity fields' };
  }
  if (typeof ping.gameIdentity === 'string' && ping.gameIdentity.startsWith(GAME_IDENTITY_PREFIX)) {
    return { identity: ping.gameIdentity, error: null };
  }
  for (const bag of versionBags(ping)) {
    const facts = factsFromBag(bag);
    if (!facts) continue;
    const identity = composeGameIdentity(facts);
    if (identity) return { identity, error: null };
  }
  return {
    identity: null,
    error: 'ping has no game version/build identity (looked at gameIdentity, versionData, gameVersion, version, game, and Major/Minor/Patch/Revision/VcsRevision/Build/NetworkVersion)',
  };
}

function hashFromRow(row) {
  if (typeof row === 'string' && row) return row;
  if (!row || typeof row !== 'object') return null;
  const hash = row.sha256 ?? row.hash ?? row.coreHash ?? row.devHash ?? row.sampleHash;
  return typeof hash === 'string' && hash ? hash : null;
}

function rowName(row) {
  if (typeof row === 'string') return row;
  return String(row?.file ?? row?.name ?? row?.dll ?? row?.path ?? '');
}

function imageLists(ping) {
  const lists = [];
  if (Array.isArray(ping.images)) lists.push(ping.images);
  if (Array.isArray(ping.reload?.images)) lists.push(ping.reload.images);
  if (Array.isArray(ping.loadedImages)) lists.push(ping.loadedImages);
  if (Array.isArray(ping.plugins)) lists.push(ping.plugins);
  if (Array.isArray(ping.reloadedPlugins)) lists.push(ping.reloadedPlugins);
  return lists;
}

function imageHashFromPing(ping, nameTest) {
  if (!ping || typeof ping !== 'object') return null;
  for (const list of imageLists(ping)) {
    const hit = list.find((row) => nameTest(rowName(row)));
    const hash = hashFromRow(hit);
    if (hash) return hash;
  }
  if (ping.images && typeof ping.images === 'object' && !Array.isArray(ping.images)) {
    for (const [k, v] of Object.entries(ping.images)) {
      if (!nameTest(k)) continue;
      const hash = hashFromRow(v);
      if (hash) return hash;
    }
  }
  return null;
}

function fileStem(name) {
  return String(name).replace(/\\/g, '/').split('/').pop().replace(/\.dll$/i, '');
}

function isCoreName(name) {
  const n = fileStem(name);
  return /^Progenitor\.Core$/i.test(n)
    || /^Progenitor$/i.test(n)
    || /^core$/i.test(n);
}

function isDevName(name) {
  const n = fileStem(name);
  return /^Progenitor\.Dev$/i.test(n)
    || /^dev$/i.test(n)
    || /Dev Bridge/i.test(String(name));
}

function firstDefined(values) {
  for (const v of values) {
    if (v != null && v !== '') return v;
  }
  return null;
}

function coreHashFromPing(ping) {
  if (!ping || typeof ping !== 'object') return null;
  return firstDefined([
    typeof ping.coreHash === 'string' ? ping.coreHash : null,
    typeof ping.coreSha256 === 'string' ? ping.coreSha256 : null,
    ping.core && typeof ping.core === 'object' ? hashFromRow(ping.core) : null,
    ping.reload?.coreHash,
    ping.reload?.core && typeof ping.reload.core === 'object' ? hashFromRow(ping.reload.core) : null,
    imageHashFromPing(ping, isCoreName),
  ]);
}

function devHashFromPing(ping) {
  if (!ping || typeof ping !== 'object') return null;
  return firstDefined([
    typeof ping.devHash === 'string' ? ping.devHash : null,
    typeof ping.devSha256 === 'string' ? ping.devSha256 : null,
    ping.dev && typeof ping.dev === 'object' ? hashFromRow(ping.dev) : null,
    imageHashFromPing(ping, isDevName),
  ]);
}

function generationFromPing(ping) {
  if (!ping || typeof ping !== 'object') return null;
  const g = ping.reload?.generation ?? ping.generation;
  if (g != null && g !== '') return g;
  for (const list of imageLists(ping)) {
    const hit = list.find((row) => isCoreName(rowName(row)));
    if (hit && typeof hit === 'object' && hit.generation != null && hit.generation !== '') {
      return hit.generation;
    }
  }
  return null;
}

function part(v) {
  return v == null || v === '' ? 'null' : String(v);
}

function frameworkPartsFromPing(ping) {
  return {
    generation: generationFromPing(ping),
    core: coreHashFromPing(ping),
    dev: devHashFromPing(ping),
    sample: sampleHashFromPing(ping),
  };
}

function composeFrameworkStamp(parts) {
  return `generation:${part(parts.generation)}; core:${part(parts.core)}; dev:${part(parts.dev)}; sample:${part(parts.sample)}`;
}

/**
 * Per-side framework stamp from one ping. Form:
 * `generation:<n>; core:<hash>; dev:<hash>; sample:<hash>` with token `null`
 * for a missing part. Does not read any other side. A missing ping is `null`
 * (not a string of null tokens).
 */
export function frameworkFromPing(ping) {
  if (!ping || typeof ping !== 'object') return null;
  return composeFrameworkStamp(frameworkPartsFromPing(ping));
}

/**
 * Per-side framework stamp. Extra arguments are ignored — never merge client
 * and server pings into one combined string.
 */
export function frameworkFromPings(ping) {
  return frameworkFromPing(ping);
}

function stampError(stamp, error, extra = {}) {
  const row = { stamp, error };
  if (extra.side) row.side = extra.side;
  if (extra.part) row.part = extra.part;
  return row;
}

function emptyFrameworkSides() {
  return { server: null, client: null };
}

function frameworkErrorsForSide(side, ping) {
  if (!ping || typeof ping !== 'object') {
    return [stampError('frameworkSides', `${side} ping missing`, { side })];
  }
  const parts = frameworkPartsFromPing(ping);
  const errors = [];
  for (const name of FRAMEWORK_PARTS) {
    const value = parts[name];
    if (value == null || value === '') {
      errors.push(stampError('frameworkSides', `${side} ${name} missing`, { side, part: name }));
    }
  }
  return errors;
}

/**
 * Collect catalog build stamps plus stampErrors.
 * Catalog hash is from the client game install (read-only). Each framework
 * side is composed only from that side's ping. `framework` is always null.
 */
export function collectBuildStamps(opts = {}) {
  const io = opts.fs ?? fs;
  const gameInstall = opts.gameInstall ?? DEFAULT_GAME_INSTALL;
  const stampErrors = [];
  const catalog = hashContentCatalog(gameInstall, io);
  if (catalog.error) stampErrors.push(stampError('contentCatalogSha256', catalog.error));

  const pingServer = opts.pingServer ?? null;
  const pingClient = opts.pingClient ?? null;
  const pings = { server: pingServer, client: pingClient };

  const frameworkSides = emptyFrameworkSides();
  for (const side of FRAMEWORK_SIDES) {
    frameworkSides[side] = frameworkFromPing(pings[side]);
    stampErrors.push(...frameworkErrorsForSide(side, pings[side]));
  }

  let gameIdentity = null;
  let gameIdentitySides = null;
  const fromServer = gameIdentityFromPing(pingServer);
  const fromClient = gameIdentityFromPing(pingClient);
  const serverIdentity = pingServer ? fromServer.identity : null;
  const clientIdentity = pingClient ? fromClient.identity : null;

  if (!pingServer && !pingClient) {
    stampErrors.push(stampError('gameIdentity', 'no ping; cannot derive game version/build identity'));
  } else if (serverIdentity && clientIdentity && serverIdentity !== clientIdentity) {
    gameIdentity = null;
    gameIdentitySides = { server: serverIdentity, client: clientIdentity };
    stampErrors.push(stampError(
      'gameIdentity',
      `server and client gameIdentity disagree: ${serverIdentity} vs ${clientIdentity}`,
    ));
  } else {
    gameIdentity = serverIdentity ?? clientIdentity;
    if (!gameIdentity) {
      const err = (pingServer ? fromServer.error : null)
        ?? (pingClient ? fromClient.error : null)
        ?? 'no ping; cannot derive game version/build identity';
      stampErrors.push(stampError('gameIdentity', err));
    }
  }

  const out = {
    contentCatalogSha256: catalog.sha256,
    gameIdentity,
    framework: null,
    frameworkSides,
    stampErrors,
  };
  if (gameIdentitySides) out.gameIdentitySides = gameIdentitySides;
  return out;
}

/** One stampPhases entry: per-side framework strings, catalog hash, game identity, time. */
export function stampPhaseSnapshot(stamps, at = null) {
  return {
    frameworkSides: {
      server: stamps?.frameworkSides?.server ?? null,
      client: stamps?.frameworkSides?.client ?? null,
    },
    contentCatalogSha256: stamps?.contentCatalogSha256 ?? null,
    gameIdentity: stamps?.gameIdentity ?? null,
    at: at ?? null,
  };
}

/**
 * Finding strings when a side's framework string differs between phases.
 * @returns {string[]}
 */
export function stampTransitionFindings(startPhase, finalPhase) {
  const findings = [];
  for (const side of FRAMEWORK_SIDES) {
    const before = startPhase?.frameworkSides?.[side] ?? null;
    const after = finalPhase?.frameworkSides?.[side] ?? null;
    if (before !== after) {
      findings.push(`image changed during run: ${side} ${before} -> ${after}`);
    }
  }
  return findings;
}

/**
 * Fields written at report top-level.
 * `framework` is always null on new reports. Readers treat a non-null value as
 * a legacy combined stamp. `stampTransition` is present only when true.
 */
export function stampsForReport(stamps) {
  const frameworkSides = {
    server: stamps?.frameworkSides?.server ?? null,
    client: stamps?.frameworkSides?.client ?? null,
  };
  const stampPhases = stamps?.stampPhases ?? {
    start: stampPhaseSnapshot(stamps, null),
    final: stampPhaseSnapshot(stamps, null),
  };
  const out = {
    contentCatalogSha256: stamps?.contentCatalogSha256 ?? null,
    gameIdentity: stamps?.gameIdentity ?? null,
    framework: null,
    frameworkSides,
    stampErrors: Array.isArray(stamps?.stampErrors) ? stamps.stampErrors : [],
    stampPhases,
  };
  if (stamps?.gameIdentitySides) {
    out.gameIdentitySides = {
      server: stamps.gameIdentitySides.server ?? null,
      client: stamps.gameIdentitySides.client ?? null,
    };
  }
  if (stamps?.stampTransition) out.stampTransition = true;
  return out;
}

/**
 * Combine a start collection and a final collection into the report stamp
 * fields, including stampPhases and stampTransition findings.
 */
export function finalizeReportStamps(startCollected, finalCollected, opts = {}) {
  const start = stampPhaseSnapshot(startCollected, opts.startAt ?? null);
  const final = stampPhaseSnapshot(finalCollected, opts.finalAt ?? null);
  const transitionFindings = stampTransitionFindings(start, final);
  return {
    stamps: stampsForReport({
      ...startCollected,
      stampPhases: { start, final },
      stampTransition: transitionFindings.length > 0,
    }),
    transitionFindings,
  };
}
