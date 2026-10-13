import fs from 'node:fs';
import path from 'node:path';
import { isLiveServerPath } from './bridge.mjs';
import { protection, rearmProtection, queryCarriers } from './checks.mjs';
import { readLatestSeq, waitForLogs } from './logs.mjs';
import { realClock } from './clock.mjs';

export const SAMPLE_DLL = 'Progenitor.Sample.dll';
export const HOLD_SUFFIX = '.rig-hold';
export const RELOAD_LOG_TIMEOUT_MS = 20000;

/** Sample-only: Scratch or Dev unload/load lines must not satisfy these. */
export const SAMPLE_REMOVAL_MATCHERS = [
  (text) => /Progenitor\.Sample\.dll removed, unloading \(unload reason=Removal\)/.test(text),
  (text) => /unloaded Progenitor Sample/.test(text),
];

export const SAMPLE_SWAP_MATCHERS = [
  (text) => /Progenitor\.Sample\.dll changed, reloading \(unload reason=Swap\)/.test(text),
  (text) => /loaded Progenitor Sample .+from Progenitor\.Sample\.dll/.test(text),
];

export const SAMPLE_LOAD_MATCHERS = [
  (text) => /loaded Progenitor Sample .+from Progenitor\.Sample\.dll/.test(text),
];

/**
 * Core debug-buff readiness after a Core install/swap or a new world. Core prefixes the
 * reconcile summary with `Debug: `. "no server world yet" is a deferral, not readiness.
 * A Sample swap never writes this line: the debug buff is Core-owned.
 */
export function isDebugReconcileReadyLine(text) {
  const line = String(text ?? '');
  if (!/Debug: world-ready reconcile/.test(line)) return false;
  if (/no server world yet/.test(line)) return false;
  return true;
}

export const DEBUG_RECONCILE_MATCHERS = [
  (text) => isDebugReconcileReadyLine(text),
];

/** Recorded as `reconcile.reason` on a server Sample swap (no reconcile wait). */
export const SAMPLE_SWAP_RECONCILE_SKIP_REASON = 'protection is Core-owned; a Sample swap does not reconcile';

/**
 * SampleHandshake.AttachedLine when ctx.IsServer is false, written through
 * SampleLog.Info("handshake", …) as `Sample handshake: declared N row(s) on the client side…`.
 * Live: cases-3 charm-client-handshake-logs.json seq 5198 (2026-09-07 client reinstall).
 * Not the server debug-buff reconcile (that line does not exist on the client).
 */
export function isSampleClientReadyLine(text) {
  const line = String(text ?? '');
  if (!/Sample handshake:/.test(line)) return false;
  if (!/on the client side/.test(line)) return false;
  return true;
}

/** Client swap readiness: Core load line, or the Sample client handshake attach line. */
export function isSampleClientSwapReadyLine(text) {
  return SAMPLE_LOAD_MATCHERS[0](text) || isSampleClientReadyLine(text);
}

export const SAMPLE_CLIENT_READY_MATCHERS = [
  (text) => isSampleClientReadyLine(text),
];

export const SAMPLE_HANDSHAKE_MATCHERS = [
  (text) => /Sample handshake:/i.test(text) || /Handshake:/.test(text),
];

/** Client Receive logs this only when the enabled flag actually changes. */
export const FROSTSTYLE_CLIENT_ENABLED_RE = /FROSTSTYLE client enabled=(True|False)/;

export function countFroststyleClientEnabledLines(lines) {
  let n = 0;
  for (const line of lines ?? []) {
    if (FROSTSTYLE_CLIENT_ENABLED_RE.test(String(line?.text ?? line ?? ''))) n += 1;
  }
  return n;
}

/**
 * Bounded wait for exactly one `FROSTSTYLE client enabled=` line. Two lines in the
 * window is a leftover handler from the previous generation.
 */
export async function waitForSingleFroststyleClientLine(bridge, opts = {}) {
  const clock = opts.clock ?? realClock();
  const timeoutMs = opts.timeoutMs ?? 5000;
  const expected = opts.enabled;
  const match = (text) => {
    const m = FROSTSTYLE_CLIENT_ENABLED_RE.exec(String(text ?? ''));
    if (!m) return false;
    if (expected != null && m[1] !== String(expected)) return false;
    return true;
  };
  const waited = await waitForLogs(bridge, 'client', {
    since: opts.since ?? -1,
    filter: 'FROSTSTYLE',
    match,
    timeoutMs,
    pollMs: opts.pollMs ?? 100,
    clock,
  });
  let extraLines = [];
  try {
    const extra = await bridge.call('client', 'logs', {
      since: waited.latestSeq,
      max: 200,
      filter: 'FROSTSTYLE',
    });
    extraLines = extra?.lines ?? [];
  } catch {
    extraLines = [];
  }
  const collected = [...(waited.collected ?? []), ...extraLines];
  const matches = collected.filter((l) => match(String(l.text ?? '')));
  const count = matches.length;
  if (count !== 1) {
    return {
      ok: false,
      count,
      line: matches[0] ?? null,
      lines: matches,
      collected,
      elapsedMs: waited.elapsedMs,
      error: count > 1
        ? `duplicate FROSTSTYLE handler: ${count} FROSTSTYLE client enabled= lines`
        : 'no FROSTSTYLE client enabled= line',
    };
  }
  return {
    ok: true,
    count: 1,
    line: matches[0],
    lines: matches,
    collected,
    elapsedMs: waited.elapsedMs,
  };
}

const SAMPLE_NAME_RE = /Progenitor\.Sample\.dll/i;

function isSamplePingName(name) {
  const raw = String(name);
  if (SAMPLE_NAME_RE.test(raw) || /^sample$/i.test(raw)) return true;
  const stem = raw.replace(/\\/g, '/').split('/').pop().replace(/\.dll$/i, '');
  return /^Progenitor\.Sample$/i.test(stem) || /^sample$/i.test(stem);
}

/** Sample image sha256 from a ping payload (images list, sampleHash, or plugin objects). */
export function sampleHashFromPing(ping) {
  if (!ping || typeof ping !== 'object') return null;
  const direct = ping.sampleHash ?? ping.sampleSha256 ?? ping.sample?.sha256 ?? ping.sample?.hash;
  if (typeof direct === 'string' && direct) return direct;
  const lists = [];
  if (Array.isArray(ping.images)) lists.push(ping.images);
  if (Array.isArray(ping.reload?.images)) lists.push(ping.reload.images);
  if (Array.isArray(ping.loadedImages)) lists.push(ping.loadedImages);
  if (Array.isArray(ping.plugins)) lists.push(ping.plugins);
  for (const list of lists) {
    const hit = list.find((row) => {
      const name = String(row?.file ?? row?.name ?? row?.dll ?? row?.path ?? '');
      return isSamplePingName(name);
    });
    const hash = hit?.sha256 ?? hit?.hash ?? hit?.sampleHash;
    if (typeof hash === 'string' && hash) return hash;
  }
  if (ping.images && typeof ping.images === 'object' && !Array.isArray(ping.images)) {
    const sample = ping.images.sample
      ?? ping.images['Progenitor.Sample.dll']
      ?? ping.images['Progenitor.Sample']
      ?? ping.images.Sample;
    if (typeof sample === 'string' && sample) return sample;
    const hash = sample?.sha256 ?? sample?.hash;
    if (typeof hash === 'string' && hash) return hash;
  }
  if (Array.isArray(ping.reloadedPlugins)) {
    for (const p of ping.reloadedPlugins) {
      if (p && typeof p === 'object') {
        const name = String(p.name ?? p.file ?? p.dll ?? '');
        if (/Sample/i.test(name) && (p.sha256 || p.hash)) return p.sha256 ?? p.hash;
      }
    }
  }
  return null;
}

/** True when a successful Removal is the last Sample reload op (no later reinstall/swap). */
export function lastReloadWasRemovalWithoutReinstall(steps) {
  let last = null;
  for (const step of steps ?? []) {
    const op = step?.detail?.op;
    if (op === 'removal' && step.ok !== false) last = 'removal';
    else if ((op === 'reinstall' || op === 'swap') && step.ok !== false) last = 'loaded';
  }
  return last === 'removal';
}

export function sampleHashFromLogs(lines) {
  for (const line of [...(lines ?? [])].reverse()) {
    const text = String(line?.text ?? line ?? '');
    const m = /Progenitor\.Sample\.dll[^\n]*sha256=([0-9a-fA-F]+)/i.exec(text);
    if (m) return m[1].toLowerCase();
  }
  return null;
}

function samplePath(reloadFolder) {
  return path.join(reloadFolder, SAMPLE_DLL);
}

function holdPath(reloadFolder) {
  return samplePath(reloadFolder) + HOLD_SUFFIX;
}

async function pingFolder(bridge, side) {
  const ping = await bridge.call(side, 'ping');
  const folder = ping?.reloadFolder;
  if (!folder) throw new Error(`ping for ${side} did not return reloadFolder`);
  if (isLiveServerPath(folder)) {
    throw new Error(`refusing a reload folder under the live dedicated server for ${side}: ${folder}`);
  }
  return { ping, folder };
}

/**
 * Reload the Sample image on one side. Never issues a file change while a hostile
 * tracked entity still exists. Swap and Removal both destroy hostiles first and
 * record protection before/after. Reinstall uses the same hostile gate.
 */
export function createReloader(opts = {}) {
  const bridge = opts.bridge;
  const tracker = opts.tracker;
  const clock = opts.clock ?? realClock();
  const io = opts.fs ?? fs;
  const logTimeoutMs = opts.logTimeoutMs ?? RELOAD_LOG_TIMEOUT_MS;
  const findings = opts.findings ?? [];
  let user = opts.user;
  let character = opts.character;
  const checkCtx = () => ({
    bridge,
    user,
    clock,
    timeoutMs: opts.protectionTimeoutMs ?? 3000,
  });

  /**
   * Read protection back and rearm with `.debug on` only when the readback is not ok.
   * The debug buff is Core-owned, so a Sample removal or reinstall should leave it intact.
   */
  async function readOrRearmProtection() {
    const read = await protection(checkCtx());
    if (read.ok) return { ...read, rearmed: false };
    const after = await rearmProtection(checkCtx());
    return { ...after, rearmed: true, before: read };
  }

  function formatEntityIds(rows) {
    return (rows ?? []).map((e) => e.entity ?? e.name).filter(Boolean).join(', ');
  }

  async function assertNoHostiles() {
    const cleanup = await tracker.cleanupHostile();
    if (!cleanup.ok) {
      const ids = formatEntityIds(cleanup.remaining);
      throw new Error(`refusing reload while a hostile tracked entity exists: ${ids}`);
    }
    // An accepted destroy is not enough. existsSweep must run; hostile leftovers fail
    // every reload op. Swap additionally requires the whole sweep to pass (below).
    if (typeof tracker.existsSweep !== 'function') {
      throw new Error('refusing reload: tracker.existsSweep is missing');
    }
    const sweep = await tracker.existsSweep();
    const remaining = sweep.remaining ?? [];
    const unconfirmed = sweep.unconfirmed ?? [];
    const hostileRemaining = remaining.filter((e) => e.hostile);
    const hostileUnconfirmed = unconfirmed.filter((e) => e.hostile);
    if (hostileRemaining.length || hostileUnconfirmed.length) {
      const bits = [
        hostileRemaining.length ? `remaining ${formatEntityIds(hostileRemaining)}` : null,
        hostileUnconfirmed.length ? `unconfirmed ${formatEntityIds(hostileUnconfirmed)}` : null,
      ].filter(Boolean).join('; ');
      throw new Error(`refusing reload: existsSweep did not confirm hostile test entities absent (${bits})`);
    }
    return { ...cleanup, existsSweep: sweep };
  }

  async function readPlayerIdentity() {
    const got = await bridge.call('server', 'players');
    let usersGot = null;
    try {
      usersGot = await bridge.call('server', 'users');
    } catch {
      usersGot = null;
    }
    const list = got?.players ?? [];
    const userList = usersGot?.users ?? [];
    const row = (user ? list.find((p) => p.name === user) : null)
      ?? list.find((p) => p.connected)
      ?? list[0]
      ?? null;
    const userRow = (row?.name ? userList.find((u) => u.name === row.name) : null)
      ?? userList.find((u) => u.character === row?.entity)
      ?? userList.find((u) => u.entity === row?.userEntity)
      ?? null;
    return {
      entity: row?.entity ?? character ?? null,
      name: row?.name ?? user ?? null,
      connected: row?.connected === true,
      userEntity: row?.userEntity ?? userRow?.entity ?? null,
      position: row?.position ?? null,
      handshake: userRow?.handshake ?? null,
      admin: userRow?.admin,
    };
  }

  function carrierLeftover(after, before) {
    const beforeIds = {
      client: new Set((before?.ids?.client ?? []).map(String)),
      server: new Set((before?.ids?.server ?? []).map(String)),
    };
    return {
      client: (after?.ids?.client ?? []).filter((id) => !beforeIds.client.has(String(id))),
      server: (after?.ids?.server ?? []).filter((id) => !beforeIds.server.has(String(id))),
    };
  }

  async function waitReload(side, since, matchers) {
    const waited = await waitForLogs(bridge, side, {
      since,
      filter: 'Reload:',
      match: matchers,
      timeoutMs: logTimeoutMs,
      pollMs: 250,
      clock,
    });
    if (!waited.ok) {
      const missing = matchers.map((m, i) => waited.lines[i] ? null : i).filter((i) => i != null);
      throw new Error(`timed out waiting for reload log lines on ${side} (unmatched ${missing.join(',')})`);
    }
    return waited;
  }

  const loadedLastWrite = new Map();

  function lastWriteMs(file) {
    try {
      const st = (typeof io.statSync === 'function' ? io.statSync : fs.statSync)(file);
      if (st == null) return null;
      if (typeof st.mtimeMs === 'number' && Number.isFinite(st.mtimeMs)) return st.mtimeMs;
      if (st.mtime instanceof Date) {
        const t = st.mtime.getTime();
        return Number.isFinite(t) ? t : null;
      }
      return null;
    } catch {
      return null;
    }
  }

  function formatLastWrite(ms) {
    if (ms == null || !Number.isFinite(ms)) return '(missing)';
    return new Date(ms).toISOString();
  }

  /** Windows CopyFile keeps the source mtime; Core skips a Swap when LastWrite is unchanged. */
  function bumpLastWrite(file, previousMs) {
    let ms = clock.now();
    if (!Number.isFinite(ms)) ms = Date.now();
    if (previousMs != null && Number.isFinite(previousMs) && ms <= previousMs) ms = previousMs + 1;
    const stamp = new Date(ms);
    (io.utimesSync ?? fs.utimesSync)(file, stamp, stamp);
    return lastWriteMs(file);
  }

  function putHoldBack(folder) {
    const dest = samplePath(folder);
    const hold = holdPath(folder);
    if (!io.existsSync(hold)) return { ok: false, dest, hold, error: `held Sample DLL not found: ${hold}` };
    io.renameSync(hold, dest);
    bumpLastWrite(dest, lastWriteMs(dest));
    return { ok: true, dest, hold };
  }

  async function failSafeReinstall(side, folder) {
    const outcome = { op: 'reinstall', failSafe: true };
    try {
      const moved = putHoldBack(folder);
      Object.assign(outcome, moved);
      if (!moved.ok) return outcome;
      const since = await readLatestSeq(bridge, side);
      await bridge.call(side, 'reload');
      try {
        const logs = await waitReload(side, since, SAMPLE_LOAD_MATCHERS);
        outcome.logs = logs.collected;
      } catch (e) {
        outcome.loadError = e.message;
      }
      outcome.protectionAfter = await readOrRearmProtection();
      outcome.ok = true;
      return outcome;
    } catch (e) {
      outcome.ok = false;
      outcome.error = e.message;
      try {
        if (io.existsSync(holdPath(folder))) io.renameSync(holdPath(folder), samplePath(folder));
        outcome.moved = true;
      } catch (moveErr) {
        outcome.moveError = moveErr.message;
      }
      return outcome;
    }
  }

  async function observeAfterSwap(side, since, identityBefore, carriersBefore) {
    const identity = await readPlayerIdentity();
    if (character && identity.entity && String(identity.entity) !== String(character)) {
      identity.characterChanged = true;
      identity.expectedEntity = character;
    } else if (identityBefore?.entity && identity.entity
      && String(identity.entity) !== String(identityBefore.entity)) {
      identity.characterChanged = true;
      identity.expectedEntity = identityBefore.entity;
    } else {
      identity.characterChanged = false;
    }
    let pingAfter = null;
    try {
      pingAfter = await bridge.call(side, 'ping');
    } catch {
      pingAfter = null;
    }
    const pingImages = pingAfter?.images ?? null;
    const sampleHashFromImages = sampleHashFromPing(pingAfter);
    let handshake = { ok: false, line: null, elapsedMs: 0 };
    try {
      const waited = await waitForLogs(bridge, side, {
        since,
        filter: 'handshake',
        match: SAMPLE_HANDSHAKE_MATCHERS,
        timeoutMs: Math.min(logTimeoutMs, 5000),
        pollMs: 100,
        clock,
      });
      handshake = {
        ok: waited.ok,
        line: waited.lines?.[0] ?? null,
        elapsedMs: waited.elapsedMs,
        collected: waited.collected,
      };
    } catch (e) {
      handshake = { ok: false, line: null, error: e && e.message ? e.message : String(e) };
    }
    const pos = identity.position ?? identityBefore?.position ?? { x: 0, y: 0, z: 0 };
    let carriers = null;
    try {
      const afterCarriers = await queryCarriers(bridge, pos);
      const leftover = carrierLeftover(afterCarriers, carriersBefore);
      carriers = {
        counts: afterCarriers.counts,
        ids: afterCarriers.ids,
        leftover,
        ok: leftover.client.length === 0 && leftover.server.length === 0
          && !afterCarriers.capReason,
        capReason: afterCarriers.capReason ?? null,
      };
    } catch (e) {
      carriers = { ok: false, error: e && e.message ? e.message : String(e) };
    }
    return {
      playerIdentity: identity,
      pingImages,
      sampleHashFromImages,
      handshake,
      carriers,
      pingAfter,
    };
  }

  async function swap(side, newDllPath) {
    if (!newDllPath) throw new Error('swap requires a dll path');
    if (!io.existsSync(newDllPath)) throw new Error(`swap dll does not exist: ${newDllPath}`);
    const hostiles = await assertNoHostiles();
    const sweep = hostiles.existsSweep;
    if (!sweep || !sweep.ok || (sweep.remaining ?? []).length || (sweep.unconfirmed ?? []).length) {
      const bits = [
        (sweep?.remaining ?? []).length ? `remaining ${formatEntityIds(sweep.remaining)}` : null,
        (sweep?.unconfirmed ?? []).length ? `unconfirmed ${formatEntityIds(sweep.unconfirmed)}` : null,
      ].filter(Boolean).join('; ');
      throw new Error(`refusing swap: existsSweep did not confirm test entities absent${bits ? ` (${bits})` : ''}`);
    }
    const identityBefore = await readPlayerIdentity();
    if (identityBefore.entity && !character) character = identityBefore.entity;
    const pos = identityBefore.position ?? { x: 0, y: 0, z: 0 };
    let carriersBefore = { ids: { client: [], server: [] }, counts: { client: 0, server: 0 } };
    try {
      carriersBefore = await queryCarriers(bridge, pos);
    } catch {
      /* still swap; the after-check records the failure */
    }
    const before = await protection(checkCtx());
    const { ping, folder } = await pingFolder(bridge, side);
    const sampleHashBefore = sampleHashFromPing(ping);
    const dest = samplePath(folder);
    const preCopyWrite = lastWriteMs(dest);
    const baseline = loadedLastWrite.has(dest) ? loadedLastWrite.get(dest) : preCopyWrite;
    const since = await readLatestSeq(bridge, side);
    io.copyFileSync(newDllPath, dest);
    const destWrite = bumpLastWrite(dest, baseline);
    if (destWrite == null || (baseline != null && destWrite === baseline)) {
      return {
        op: 'swap',
        ok: false,
        error: `LastWrite unchanged: ${formatLastWrite(destWrite ?? baseline)}`,
        dest,
        destLastWrite: destWrite,
        previousLastWrite: baseline,
        hostiles,
        protectionBefore: before,
        sampleHashBefore,
      };
    }
    await bridge.call(side, 'reload');
    const logs = await waitReload(side, since, SAMPLE_SWAP_MATCHERS);
    const loadLine = logs.lines?.[1] ?? logs.collected?.find((l) => SAMPLE_LOAD_MATCHERS[0](String(l.text ?? '')));
    const clientSwap = side === 'client';
    // The debug buff, its ledger and its reconcile are Core-owned: no Sample swap (either
    // side) writes a reconcile line, so none is awaited. The post-swap protection readback
    // below still fails the step closed when the guard dropped.
    const reconcile = {
      ok: true,
      skipped: true,
      reason: clientSwap
        ? 'client swap does not wait for the server debug-buff reconcile'
        : SAMPLE_SWAP_RECONCILE_SKIP_REASON,
    };
    let clientReady = { skipped: true };
    if (clientSwap) {
      try {
        const waited = await waitForLogs(bridge, side, {
          since,
          filter: 'handshake',
          match: SAMPLE_CLIENT_READY_MATCHERS,
          timeoutMs: logTimeoutMs,
          pollMs: 250,
          clock,
        });
        clientReady = {
          ok: waited.ok,
          skipped: false,
          line: waited.lines?.[0] ?? null,
          elapsedMs: waited.elapsedMs,
          collected: waited.collected,
        };
      } catch (e) {
        clientReady = {
          ok: false,
          skipped: false,
          line: null,
          error: e && e.message ? e.message : String(e),
        };
      }
    }
    let sampleHashAfter = sampleHashFromLogs(logs.collected);
    const observed = await observeAfterSwap(side, since, identityBefore, carriersBefore);
    sampleHashAfter = observed.sampleHashFromImages ?? sampleHashAfter;
    const after = await protection(checkCtx());
    const hashes = {
      sampleHashBefore,
      sampleHashAfter,
      pingImages: observed.pingImages,
    };
    loadedLastWrite.set(dest, destWrite);
    const extra = {
      hostiles,
      protectionBefore: before,
      protectionAfter: after,
      logs: logs.collected,
      dest,
      destLastWrite: destWrite,
      previousLastWrite: baseline,
      findings: [...findings],
      reconcile,
      clientReady,
      loadLine: loadLine ?? null,
      playerIdentity: observed.playerIdentity,
      handshake: observed.handshake,
      carriers: observed.carriers,
      ...hashes,
    };
    const identityOk = observed.playerIdentity?.characterChanged !== true;
    const dropped = !after.ok;
    const readyOk = clientSwap ? clientReady.ok === true : reconcile.ok;
    const connectedOk = observed.playerIdentity?.connected === true;
    const usersHandshake = observed.playerIdentity?.handshake;
    const handshakeOk = usersHandshake == null || usersHandshake === 'ok';
    if (!readyOk || dropped || !identityOk || (clientSwap && (!connectedOk || !handshakeOk))) {
      if (dropped) findings.push('protection dropped by swap');
      extra.findings = [...findings];
      let error = 'protection dropped by swap';
      if (!readyOk) {
        error = 'timed out waiting for client Sample ready (loaded + handshake)';
      } else if (!identityOk) error = `player identity changed: ${observed.playerIdentity.expectedEntity} -> ${observed.playerIdentity.entity}`;
      else if (clientSwap && !connectedOk) error = 'client disconnected after swap';
      else if (clientSwap && !handshakeOk) error = `server users handshake after client swap: ${usersHandshake}`;
      else if (dropped) error = 'protection dropped by swap';
      // Rearm so the character is not left unprotected, but the swap step
      // itself stays failed: the protection is Core-owned, so a Sample swap
      // that drops it is a Core defect even when this later command succeeds.
      // A later manual rearm must never turn the run into complete.
      let rearmed = null;
      try {
        rearmed = await rearmProtection(checkCtx());
      } catch (e) {
        return {
          op: 'swap',
          ok: false,
          error: e && e.message ? e.message : String(e),
          protectionRearmed: { ok: false, error: e && e.message ? e.message : String(e) },
          ...extra,
        };
      }
      return {
        op: 'swap',
        ok: false,
        error,
        protectionRearmed: rearmed,
        ...extra,
      };
    }
    return {
      op: 'swap',
      ok: true,
      ...extra,
    };
  }

  async function removal(side) {
    const hostiles = await assertNoHostiles();
    const before = await protection(checkCtx());
    const { folder } = await pingFolder(bridge, side);
    const dest = samplePath(folder);
    const hold = holdPath(folder);
    if (!io.existsSync(dest)) throw new Error(`Sample DLL not in reload folder: ${dest}`);
    const since = await readLatestSeq(bridge, side);
    io.renameSync(dest, hold);
    let waitError = null;
    let logs;
    let reinstallOutcome = null;
    try {
      await bridge.call(side, 'reload');
      logs = await waitReload(side, since, SAMPLE_REMOVAL_MATCHERS);
    } catch (e) {
      waitError = e;
    } finally {
      if (waitError) {
        reinstallOutcome = await failSafeReinstall(side, folder);
      }
    }
    if (waitError) {
      return {
        op: 'removal',
        ok: false,
        error: waitError.message,
        reinstall: reinstallOutcome,
        hostiles,
        protectionBefore: before,
        hold,
      };
    }
    const after = await protection(checkCtx());
    return { op: 'removal', ok: true, hostiles, protectionBefore: before, protectionAfter: after, logs: logs.collected, hold };
  }

  async function reinstall(side) {
    const hostiles = await assertNoHostiles();
    const { folder } = await pingFolder(bridge, side);
    const dest = samplePath(folder);
    const hold = holdPath(folder);
    if (!io.existsSync(hold)) throw new Error(`held Sample DLL not found: ${hold}`);
    const since = await readLatestSeq(bridge, side);
    io.renameSync(hold, dest);
    bumpLastWrite(dest, lastWriteMs(dest));
    await bridge.call(side, 'reload');
    const logs = await waitReload(side, since, SAMPLE_LOAD_MATCHERS);
    const protectionAfter = await readOrRearmProtection();
    return { op: 'reinstall', ok: true, dest, logs: logs.collected, protectionAfter, hostiles };
  }

  async function restoreHoldIfPresent(side) {
    const { folder } = await pingFolder(bridge, side);
    if (!io.existsSync(holdPath(folder))) return null;
    return reinstall(side);
  }

  async function sampleReloadState(side) {
    const { ping, folder } = await pingFolder(bridge, side);
    const dest = samplePath(folder);
    const hold = holdPath(folder);
    const holdPresent = io.existsSync(hold);
    const samplePresent = io.existsSync(dest);
    return {
      folder,
      dest,
      hold,
      holdPresent,
      samplePresent,
      needsRestore: holdPresent || !samplePresent,
      ping,
    };
  }

  return {
    swap,
    removal,
    reinstall,
    restoreHoldIfPresent,
    sampleReloadState,
    findings,
    samplePath,
    holdPath,
    get user() { return user; },
    set user(v) { user = v; },
    get character() { return character; },
    set character(v) { character = v; },
  };
}
