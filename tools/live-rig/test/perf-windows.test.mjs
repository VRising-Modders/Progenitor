import test from 'node:test';
import assert from 'node:assert/strict';
import {
  parsePerfSpec,
  clampWindowSeconds,
  percentile,
  latencyStats,
  latencyTable,
  readPerfStatus,
  windowEntry,
  gapEntry,
  correlateFreezeWindow,
  freezeOverlapsInstant,
  sampleGapStats,
  pingLatencies,
  perfStatusLatencies,
  responsivenessSummary,
  applyFreezeCorrelation,
  PERF_MAX_WINDOW_SECONDS,
  RESPONSIVENESS_STALL_MS,
  NO_WINDOW_ACTIVE,
  MISSING_INTERVAL_NOTE,
} from '../lib/perfWindows.mjs';

test('parsePerfSpec clamps the window to 300 s and keeps the gap', () => {
  assert.equal(parsePerfSpec(null), null);
  assert.equal(parsePerfSpec(false), null);
  assert.deepEqual(parsePerfSpec({ windowSeconds: 300, gapSeconds: 2 }), {
    windowSeconds: 300,
    gapSeconds: 2,
  });
  assert.equal(parsePerfSpec({ windowSeconds: 900 }).windowSeconds, PERF_MAX_WINDOW_SECONDS);
  assert.equal(clampWindowSeconds(0), 1);
  assert.equal(parsePerfSpec(true).gapSeconds, 2);
});

test('percentile interpolates and latencyStats expose max/p50/p95/p99', () => {
  assert.equal(percentile([], 50), null);
  assert.equal(percentile([10], 50), 10);
  assert.equal(percentile([1, 2, 3, 4], 0), 1);
  assert.equal(percentile([1, 2, 3, 4], 100), 4);
  const even = [10, 20, 30, 40];
  assert.equal(percentile(even, 50), 25);
  const stats = latencyStats([1, 2, 3, 4, 100]);
  assert.equal(stats.samples, 5);
  assert.equal(stats.max, 100);
  assert.equal(stats.p50, 3);
  assert.ok(stats.p95 >= stats.p50);
  assert.ok(stats.p99 >= stats.p95);
  const table = latencyTable({ pingMs: [4, 5, 29924], perfStatusMs: [1, 2, 3] });
  assert.equal(table.ping.max, 29924);
  assert.equal(table.perfStatus.samples, 3);
});

test('readPerfStatus maps nested progress scalars including captureId', () => {
  const live = readPerfStatus({
    running: true,
    captureId: 3,
    progress: {
      frames: 120,
      sampleSilenceMs: 12.5,
      lastFrameMs: 16.6,
      maxFrameMs: 41,
    },
  }, 7);
  assert.equal(live.running, true);
  assert.equal(live.historical, false);
  assert.equal(live.frameCount, 120);
  assert.equal(live.lastIntervalMs, 16.6);
  assert.equal(live.maxIntervalMs, 41);
  assert.equal(live.sampleSilenceMs, 12.5);
  assert.equal(live.captureId, 3);
  assert.equal(live.elapsedMs, 7);
  const historical = readPerfStatus({
    running: false,
    captureId: 3,
    progress: { frames: 120, sampleSilenceMs: 4000, lastFrameMs: 16.6, maxFrameMs: 41 },
  }, 2);
  assert.equal(historical.historical, true);
  assert.equal(historical.running, false);
});

test('window and gap entries stay distinct so one window cannot cover a soak', () => {
  const win = windowEntry({
    captureId: 1,
    startedAt: 't0',
    endedAt: 't1',
    startedAtMs: 0,
    endedAtMs: 20_000,
    frames: 900,
    maxIntervalMs: 18,
  });
  const gap = gapEntry({
    startedAt: 't1',
    endedAt: 't2',
    startedAtMs: 20_000,
    endedAtMs: 22_000,
    gapSeconds: 2,
  });
  assert.equal(win.kind, 'window');
  assert.equal(win.missingInterval, false);
  assert.equal(gap.kind, 'gap');
  assert.equal(gap.gapSeconds, 2);
  assert.equal(gap.frames, null);
  const closed = windowEntry({
    captureId: 1,
    startedAtMs: 0,
    endedAtMs: 20_000,
    missingInterval: true,
  });
  assert.equal(closed.missingInterval, true);
  assert.equal(closed.missingIntervalNote, MISSING_INTERVAL_NOTE);
});

test('freeze correlation names the active window or no window active', () => {
  const rows = [
    windowEntry({ captureId: 1, startedAt: 'a', endedAt: 'b', startedAtMs: 0, endedAtMs: 20_000, frames: 10 }),
    gapEntry({ startedAt: 'b', endedAt: 'c', startedAtMs: 20_000, endedAtMs: 22_000, gapSeconds: 2 }),
    windowEntry({ captureId: 2, startedAt: 'c', endedAt: 'd', startedAtMs: 22_000, endedAtMs: 42_000, frames: 10 }),
  ];
  const during = correlateFreezeWindow({ startedAtMs: 5_000, endedAtMs: 15_000 }, rows);
  assert.equal(during.captureId, 1);
  const inGap = correlateFreezeWindow({ startedAtMs: 20_500, endedAtMs: 21_000 }, rows);
  assert.equal(inGap, NO_WINDOW_ACTIVE);
  const none = correlateFreezeWindow({ startedAtMs: 90_000, endedAtMs: 91_000 }, rows);
  assert.equal(none, NO_WINDOW_ACTIVE);
  const freeze = { startedAtMs: 25_000, endedAtMs: 26_000 };
  applyFreezeCorrelation([freeze], rows);
  assert.equal(freeze.perfWindow.captureId, 2);
  assert.equal(freezeOverlapsInstant([{ startedAtMs: 10_000, endedAtMs: 12_000 }], 11_000), true);
  assert.equal(freezeOverlapsInstant([{ startedAtMs: 10_000, endedAtMs: 12_000 }], 20_000), false);
});

test('a recovered 30 s freeze fails responsiveness even when the process stayed connected', () => {
  const pingMs = [4, 5, 6];
  const freezes = [{ mainThreadFrozenMs: 30_000, recoveredAt: 't' }];
  const summary = responsivenessSummary({ pingMs, freezes });
  assert.equal(summary.responsivenessFailed, true);
  assert.equal(summary.maxStallMs, 30_000);
  assert.equal(summary.stallThresholdMs, RESPONSIVENESS_STALL_MS);
  const ok = responsivenessSummary({ pingMs: [4, 14], freezes: [] });
  assert.equal(ok.responsivenessFailed, false);
  assert.equal(ok.maxStallMs, 14);
  const slowPing = responsivenessSummary({ pingMs: [4, 29924], freezes: [] });
  assert.equal(slowPing.responsivenessFailed, true);
  assert.equal(slowPing.maxStallMs, 29924);
});

test('sample gap stats and per-sample latency extractors', () => {
  const samples = [
    { at: 'a', atMs: 0, ping: { ok: true, elapsedMs: 4 }, perf: { elapsedMs: 1 } },
    { at: 'b', atMs: 10_000, ping: { skipped: true, elapsedMs: 0 }, perf: { elapsedMs: 2 } },
    { at: 'c', atMs: 40_000, ping: { ok: true, elapsedMs: 12 }, perf: { elapsedMs: 3 } },
  ];
  const gaps = sampleGapStats(samples, 10_000);
  assert.equal(gaps.maxGapMs, 30_000);
  assert.equal(gaps.oversized.length, 1);
  assert.deepEqual(pingLatencies(samples), [4, 12]);
  assert.deepEqual(perfStatusLatencies(samples), [1, 2, 3]);
});
