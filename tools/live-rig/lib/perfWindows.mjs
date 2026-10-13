/** Bounded soak perf windows, latency percentiles, and freeze correlation. */

export const PERF_MAX_WINDOW_SECONDS = 300;
export const PERF_DEFAULT_WINDOW_SECONDS = 300;
export const PERF_DEFAULT_GAP_SECONDS = 2;
export const RESPONSIVENESS_STALL_MS = 5000;
export const NO_WINDOW_ACTIVE = 'no window active';
export const MISSING_INTERVAL_NOTE = 'window closed during a freeze; long interval may be absent';

function finiteNumber(value) {
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

export function clampWindowSeconds(value) {
  const n = finiteNumber(value);
  if (n == null) return PERF_DEFAULT_WINDOW_SECONDS;
  return Math.max(1, Math.min(PERF_MAX_WINDOW_SECONDS, n));
}

/** `perf: {windowSeconds, gapSeconds}` on a soak step. Null when omitted. */
export function parsePerfSpec(perf) {
  if (perf == null || perf === false) return null;
  const src = perf === true ? {} : (typeof perf === 'object' ? perf : null);
  if (src == null) return null;
  const windowSeconds = clampWindowSeconds(src.windowSeconds ?? src.seconds);
  const gapRaw = finiteNumber(src.gapSeconds);
  const gapSeconds = gapRaw == null || gapRaw < 0 ? PERF_DEFAULT_GAP_SECONDS : gapRaw;
  return { windowSeconds, gapSeconds };
}

/**
 * Inclusive percentile with linear interpolation.
 * `p` is 0–100. Empty input is null.
 */
export function percentile(values, p) {
  const list = (values ?? []).map(Number).filter((n) => Number.isFinite(n)).sort((a, b) => a - b);
  if (list.length === 0) return null;
  const rank = Number(p);
  if (!Number.isFinite(rank) || rank <= 0) return list[0];
  if (rank >= 100) return list[list.length - 1];
  const idx = (rank / 100) * (list.length - 1);
  const lo = Math.floor(idx);
  const hi = Math.ceil(idx);
  if (lo === hi) return list[lo];
  const w = idx - lo;
  return list[lo] * (1 - w) + list[hi] * w;
}

export function latencyStats(values) {
  const list = (values ?? []).map(Number).filter((n) => Number.isFinite(n));
  if (list.length === 0) {
    return { max: null, p50: null, p95: null, p99: null, samples: 0 };
  }
  return {
    max: Math.max(...list),
    p50: percentile(list, 50),
    p95: percentile(list, 95),
    p99: percentile(list, 99),
    samples: list.length,
  };
}

export function latencyTable({ pingMs = [], perfStatusMs = [] } = {}) {
  return {
    ping: latencyStats(pingMs),
    perfStatus: latencyStats(perfStatusMs),
  };
}

function progressOf(result) {
  if (result == null || typeof result !== 'object') return null;
  if (result.progress && typeof result.progress === 'object') return result.progress;
  return result;
}

/** Map a `perf_status` (or flattened) payload to the soak sample fields. */
export function readPerfStatus(result, elapsedMs) {
  const progress = result?.progress && typeof result.progress === 'object' ? result.progress : null;
  const src = progress ?? ((result && typeof result === 'object') ? result : null);
  const running = result?.running === true;
  const frameCount = finiteNumber(src?.frames ?? result?.frameCount);
  const lastIntervalMs = finiteNumber(src?.lastFrameMs ?? result?.lastIntervalMs);
  const maxIntervalMs = finiteNumber(src?.maxFrameMs ?? result?.maxIntervalMs);
  const sampleSilenceMs = finiteNumber(src?.sampleSilenceMs ?? result?.sampleSilenceMs);
  const captureId = result?.captureId ?? src?.captureId ?? null;
  const historical = result?.historical === true || (!running && progress != null);
  return {
    frameCount,
    lastIntervalMs,
    maxIntervalMs,
    sampleSilenceMs,
    running,
    historical,
    captureId,
    elapsedMs: finiteNumber(elapsedMs),
  };
}

export function windowEntry(fields) {
  return {
    kind: 'window',
    captureId: fields.captureId ?? null,
    startedAt: fields.startedAt ?? null,
    endedAt: fields.endedAt ?? null,
    startedAtMs: finiteNumber(fields.startedAtMs),
    endedAtMs: finiteNumber(fields.endedAtMs),
    frames: finiteNumber(fields.frames) ?? 0,
    maxIntervalMs: finiteNumber(fields.maxIntervalMs),
    missingInterval: fields.missingInterval === true,
    missingIntervalNote: fields.missingInterval === true
      ? (fields.missingIntervalNote ?? MISSING_INTERVAL_NOTE)
      : null,
    report: fields.report ?? null,
    stopReason: fields.stopReason ?? null,
  };
}

export function gapEntry({ startedAt, endedAt, startedAtMs, endedAtMs, gapSeconds }) {
  return {
    kind: 'gap',
    startedAt: startedAt ?? null,
    endedAt: endedAt ?? null,
    startedAtMs: finiteNumber(startedAtMs),
    endedAtMs: finiteNumber(endedAtMs),
    gapSeconds: finiteNumber(gapSeconds),
    frames: null,
    maxIntervalMs: null,
    missingInterval: false,
    missingIntervalNote: null,
  };
}

function intervalOverlaps(startA, endA, startB, endB) {
  const a0 = Number(startA);
  const a1 = Number.isFinite(Number(endA)) ? Number(endA) : Infinity;
  const b0 = Number(startB);
  const b1 = Number.isFinite(Number(endB)) ? Number(endB) : Infinity;
  if (!Number.isFinite(a0) || !Number.isFinite(b0)) return false;
  return a0 <= b1 && b0 <= a1;
}

export function windowsOnly(perfWindows) {
  return (perfWindows ?? []).filter((row) => row && row.kind !== 'gap');
}

/**
 * Perf window active at the freeze start, or {@link NO_WINDOW_ACTIVE}.
 */
export function correlateFreezeWindow(freeze, perfWindows) {
  const start = finiteNumber(freeze?.startedAtMs);
  if (start == null) return NO_WINDOW_ACTIVE;
  const end = finiteNumber(freeze?.endedAtMs) ?? start;
  const windows = windowsOnly(perfWindows);
  let hit = null;
  for (const win of windows) {
    const wStart = finiteNumber(win.startedAtMs);
    if (wStart == null) continue;
    const wEnd = finiteNumber(win.endedAtMs);
    if (intervalOverlaps(start, end, wStart, wEnd)) {
      // Prefer the window that contains the freeze start.
      const containsStart = wStart <= start && (wEnd == null || start <= wEnd);
      if (containsStart) {
        hit = win;
        break;
      }
      if (hit == null) hit = win;
    }
  }
  if (!hit) return NO_WINDOW_ACTIVE;
  return {
    captureId: hit.captureId ?? null,
    startedAt: hit.startedAt ?? null,
    endedAt: hit.endedAt ?? null,
    startedAtMs: hit.startedAtMs ?? null,
    endedAtMs: hit.endedAtMs ?? null,
  };
}

export function freezeOverlapsInstant(freezes, ms) {
  const t = finiteNumber(ms);
  if (t == null) return false;
  for (const f of freezes ?? []) {
    const start = finiteNumber(f?.startedAtMs);
    if (start == null) continue;
    const end = finiteNumber(f?.endedAtMs);
    if (start <= t && (end == null || t <= end)) return true;
  }
  return false;
}

export function sampleGapStats(samples, expectedIntervalMs) {
  const rows = samples ?? [];
  const expected = finiteNumber(expectedIntervalMs);
  const oversized = [];
  let maxGapMs = 0;
  for (let i = 1; i < rows.length; i++) {
    const prev = finiteNumber(rows[i - 1]?.atMs);
    const cur = finiteNumber(rows[i]?.atMs);
    if (prev == null || cur == null) continue;
    const gapMs = cur - prev;
    if (gapMs > maxGapMs) maxGapMs = gapMs;
    if (expected != null && gapMs > expected * 1.5) {
      oversized.push({
        from: rows[i - 1].at ?? null,
        to: rows[i].at ?? null,
        fromMs: prev,
        toMs: cur,
        gapMs,
      });
    }
  }
  return {
    expectedIntervalMs: expected,
    maxGapMs,
    oversized,
  };
}

export function pingLatencies(samples) {
  const out = [];
  for (const sample of samples ?? []) {
    const ping = sample?.ping;
    if (!ping || ping.skipped === true) continue;
    const n = finiteNumber(ping.elapsedMs);
    if (n != null) out.push(n);
  }
  return out;
}

export function perfStatusLatencies(samples) {
  const out = [];
  for (const sample of samples ?? []) {
    const n = finiteNumber(sample?.perf?.elapsedMs);
    if (n != null) out.push(n);
  }
  return out;
}

/**
 * A recovered stall above 5 s still fails responsiveness.
 * `ok` on the soak step stays independent (failAfterSeconds / ping failures).
 */
export function responsivenessSummary({ pingMs = [], freezes = [], stallMs = RESPONSIVENESS_STALL_MS } = {}) {
  const threshold = finiteNumber(stallMs) ?? RESPONSIVENESS_STALL_MS;
  let maxStallMs = 0;
  for (const n of pingMs) {
    if (Number.isFinite(n) && n > maxStallMs) maxStallMs = n;
  }
  for (const f of freezes ?? []) {
    const n = finiteNumber(f?.mainThreadFrozenMs);
    if (n != null && n > maxStallMs) maxStallMs = n;
  }
  const responsivenessFailed = maxStallMs > threshold;
  return {
    responsivenessFailed,
    maxStallMs,
    stallThresholdMs: threshold,
  };
}

export function applyFreezeCorrelation(freezes, perfWindows) {
  for (const episode of freezes ?? []) {
    episode.perfWindow = correlateFreezeWindow(episode, perfWindows);
  }
  return freezes;
}

/** @internal exported for tests that want the nested-or-flat progress reader. */
export function progressScalars(result) {
  return progressOf(result);
}
