import test from 'node:test';
import assert from 'node:assert/strict';
import { createBridge } from '../lib/bridge.mjs';
import { fakeClock } from '../lib/clock.mjs';
import {
  executeStep,
  STEP_KINDS,
  CORE_SWAP_POLL_MS,
} from '../lib/steps.mjs';
import { tempDir } from './helpers.mjs';

function refused(code = 'ECONNREFUSED') {
  const err = new Error(`connect ${code} 127.0.0.1:27341`);
  err.code = code;
  return err;
}

function coreSwapTransport({
  generationBefore = 3,
  generationAfter = 4,
  refuseCount = 0,
  outcome = 'ok',
  inFlightPolls = 0,
  hang = false,
  alcNameAfter = null,
  previousAliveAfter = false,
  collectedAfter = undefined,
  lastReloadCollected = undefined,
  images = {
    core: 'corehashcorehashcorehashcorehash',
    dev: 'devhashdevhashdevhashdevhashdevhash',
    sample: 'samplehashsamplehashsamplehashsa',
  },
} = {}) {
  const alcAfter = alcNameAfter ?? `progenitor-core:${generationAfter}`;
  let reloads = 0;
  let refusedPolls = 0;
  let postReloadStatus = 0;
  return {
    request: async (side, op) => {
      if (op === 'reload_status') {
        if (reloads === 0) {
          return {
            ok: true,
            result: {
              ok: true,
              status: {
                generation: generationBefore,
                alcName: `progenitor-core:${generationBefore}`,
                inFlight: false,
                previousAlive: false,
                lastReload: {
                  gen: generationBefore,
                  outcome: 'ok',
                  reason: 'boot',
                  timestamp: 't0',
                },
              },
            },
          };
        }
        if (refusedPolls < refuseCount) {
          refusedPolls += 1;
          throw refused('ECONNREFUSED');
        }
        postReloadStatus += 1;
        const inFlight = hang || postReloadStatus <= inFlightPolls;
        const gen = inFlight ? generationBefore : generationAfter;
        const doneOutcome = inFlight ? 'ok' : outcome;
        const lastReload = {
          gen,
          outcome: doneOutcome,
          reason: 'bridge core_reload',
          timestamp: 't1',
        };
        if (lastReloadCollected !== undefined && !inFlight) lastReload.collected = lastReloadCollected;
        const status = {
          generation: gen,
          alcName: inFlight ? `progenitor-core:${generationBefore}` : alcAfter,
          inFlight,
          previousAlive: inFlight ? false : previousAliveAfter,
          lastReload,
        };
        if (collectedAfter !== undefined && !inFlight) status.collected = collectedAfter;
        return {
          ok: true,
          result: {
            ok: true,
            status,
          },
        };
      }
      if (op === 'core_reload') {
        reloads += 1;
        return {
          ok: true,
          result: {
            ok: true,
            scheduled: true,
            result: `scheduled gen=${generationAfter}`,
          },
        };
      }
      if (op === 'ping') {
        return {
          ok: true,
          result: {
            side,
            coreHash: images.core,
            devHash: images.dev,
            sampleHash: images.sample,
            images: [
              { name: 'Core', sha256: images.core, generation: generationAfter },
              { name: 'Progenitor.Dev', sha256: images.dev },
              { name: 'Progenitor.Sample', sha256: images.sample },
            ],
            reload: { generation: generationAfter, alcName: alcAfter },
          },
        };
      }
      return { ok: false, error: `unknown op '${op}'` };
    },
  };
}

function ctxFor(tmp, transport, extra = {}) {
  const clock = extra.clock ?? fakeClock();
  const bridge = extra.bridge ?? createBridge({
    runDir: tmp.dir,
    transport,
    clock,
  });
  return {
    bridge,
    clock,
    side: extra.side ?? 'client',
    records: extra.records ?? {},
    findings: [],
    stepName: extra.stepName ?? 'test',
  };
}

test('STEP_KINDS includes coreSwap', () => {
  assert.ok(STEP_KINDS.includes('coreSwap'));
  assert.equal(CORE_SWAP_POLL_MS, 3000);
});

test('coreSwap with a simulated refusal window completes and records it', async () => {
  const tmp = tempDir();
  try {
    const images = {
      core: 'aaa111corehash',
      dev: 'bbb222devhash',
      sample: 'ccc333samplehash',
    };
    const ctx = ctxFor(tmp, coreSwapTransport({
      generationBefore: 3,
      generationAfter: 4,
      refuseCount: 2,
      images,
    }));
    const result = await executeStep({
      kind: 'coreSwap',
      side: 'client',
      expectGenerationDelta: 1,
      maxWaitMs: 90000,
      record: 'swap1',
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.generationBefore, 3);
    assert.equal(result.generationAfter, 4);
    assert.equal(result.alcName, 'progenitor-core:4');
    assert.equal(result.lastReloadOutcome, 'ok');
    assert.equal(result.lastReload.outcome, 'ok');
    assert.equal(result.previousAlive, false);
    assert.equal(result.collected, null);
    assert.equal(result.inFlight, false);
    assert.ok(result.refusalWindow);
    assert.ok(result.refusalWindow.first);
    assert.ok(result.refusalWindow.last);
    assert.equal(result.refusalWindow.attempts, 2);
    assert.equal(result.imageHashes.core, images.core);
    assert.equal(result.imageHashes.dev, images.dev);
    assert.equal(result.imageHashes.sample, images.sample);
    assert.equal(result.coreHash, images.core);
    assert.equal(result.devHash, images.dev);
    assert.equal(result.sampleHash, images.sample);
    assert.ok(Array.isArray(result.images));
    assert.equal(ctx.bridge.mainThreadSuspect('client'), null);
    assert.ok(ctx.bridge.bridgeDown('client'));
    assert.equal(ctx.records.swap1.generationAfter, 4);
  } finally {
    tmp.cleanup();
  }
});

test('coreSwap timeout fails while inFlight stays true', async () => {
  const tmp = tempDir();
  try {
    const ctx = ctxFor(tmp, coreSwapTransport({ hang: true }));
    const result = await executeStep({
      kind: 'coreSwap',
      side: 'client',
      expectGenerationDelta: 1,
      maxWaitMs: 9000,
      pollMs: 3000,
    }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /timed out/);
    assert.equal(result.generationBefore, 3);
    assert.equal(ctx.bridge.mainThreadSuspect('client'), null);
  } finally {
    tmp.cleanup();
  }
});

test('coreSwap fails when lastReload.outcome is not ok', async () => {
  const tmp = tempDir();
  try {
    const ctx = ctxFor(tmp, coreSwapTransport({
      generationBefore: 3,
      generationAfter: 4,
      outcome: 'failed',
    }));
    const result = await executeStep({
      kind: 'coreSwap',
      side: 'client',
      expectGenerationDelta: 1,
      maxWaitMs: 90000,
    }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /lastReload\.outcome is failed/);
    assert.equal(result.lastReloadOutcome, 'failed');
    assert.equal(result.generationAfter, 4);
    assert.equal(ctx.bridge.mainThreadSuspect('client'), null);
  } finally {
    tmp.cleanup();
  }
});

test('coreSwap fails when generation does not move', async () => {
  const tmp = tempDir();
  try {
    const ctx = ctxFor(tmp, coreSwapTransport({
      generationBefore: 3,
      generationAfter: 3,
      outcome: 'ok',
    }));
    const result = await executeStep({
      kind: 'coreSwap',
      side: 'client',
      expectGenerationDelta: 1,
      maxWaitMs: 90000,
    }, ctx);
    assert.equal(result.ok, false);
    assert.match(String(result.error), /generation did not move/);
    assert.equal(result.generationBefore, 3);
    assert.equal(result.generationAfter, 3);
  } finally {
    tmp.cleanup();
  }
});

test('op tolerateBridgeDownMs retries a refusal and does not mark suspect', async () => {
  const tmp = tempDir();
  let n = 0;
  try {
    const transport = {
      request: async (_side, op) => {
        if (op === 'logs') {
          return { ok: true, result: { latestSeq: 1, count: 0, lines: [] } };
        }
        n += 1;
        if (n <= 2) throw refused('ECONNREFUSED');
        return {
          ok: true,
          result: { ok: true, action: 'status', after: { open: false } },
        };
      },
    };
    const ctx = ctxFor(tmp, transport);
    const result = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'mods_ui',
      args: { action: 'status' },
      tolerateBridgeDownMs: 1000,
      expect: { ok: true, action: 'status' },
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(n, 3);
    assert.equal(ctx.bridge.mainThreadSuspect('client'), null);
    assert.ok(ctx.bridge.bridgeDown('client'));
  } finally {
    tmp.cleanup();
  }
});

test('coreSwap fails when previousAlive is true after the swap', async () => {
  const tmp = tempDir();
  try {
    const ctx = ctxFor(tmp, coreSwapTransport({
      generationBefore: 3,
      generationAfter: 4,
      previousAliveAfter: true,
      collectedAfter: false,
    }));
    const result = await executeStep({
      kind: 'coreSwap',
      side: 'client',
      expectGenerationDelta: 1,
      maxWaitMs: 90000,
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.reason, 'previous generation still alive');
    assert.equal(result.error, 'previous generation still alive');
    assert.equal(result.previousAlive, true);
    assert.equal(result.collected, false);
    assert.equal(result.generationBefore, 3);
    assert.equal(result.generationAfter, 4);
    assert.equal(result.lastReloadOutcome, 'ok');
    assert.equal(result.inFlight, false);
    assert.equal(ctx.findings.length, 0);
  } finally {
    tmp.cleanup();
  }
});

test('coreSwap with allowPreviousAlive records a finding when previousAlive is true', async () => {
  const tmp = tempDir();
  try {
    const ctx = ctxFor(tmp, coreSwapTransport({
      generationBefore: 3,
      generationAfter: 4,
      previousAliveAfter: true,
      collectedAfter: false,
    }));
    const result = await executeStep({
      kind: 'coreSwap',
      side: 'client',
      expectGenerationDelta: 1,
      maxWaitMs: 90000,
      allowPreviousAlive: true,
      record: 'swapPin',
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.previousAlive, true);
    assert.equal(result.collected, false);
    assert.equal(result.generationAfter, 4);
    assert.deepEqual(ctx.findings, ['previous generation still alive']);
    assert.equal(ctx.records.swapPin.previousAlive, true);
    assert.equal(ctx.records.swapPin.collected, false);
  } finally {
    tmp.cleanup();
  }
});

test('coreSwap records collected from lastReload when reload_status has no collected', async () => {
  const tmp = tempDir();
  try {
    const ctx = ctxFor(tmp, coreSwapTransport({
      generationBefore: 3,
      generationAfter: 4,
      lastReloadCollected: true,
    }));
    const result = await executeStep({
      kind: 'coreSwap',
      side: 'client',
      expectGenerationDelta: 1,
      maxWaitMs: 90000,
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.collected, true);
    assert.equal(result.previousAlive, false);
  } finally {
    tmp.cleanup();
  }
});

test('coreSwap records collected from reload_status when present', async () => {
  const tmp = tempDir();
  try {
    const ctx = ctxFor(tmp, coreSwapTransport({
      generationBefore: 5,
      generationAfter: 6,
      collectedAfter: true,
    }));
    const result = await executeStep({
      kind: 'coreSwap',
      side: 'client',
      expectGenerationDelta: 1,
      maxWaitMs: 90000,
    }, ctx);
    assert.equal(result.ok, true, result.error);
    assert.equal(result.collected, true);
  } finally {
    tmp.cleanup();
  }
});

test('op without tolerateBridgeDownMs fails a refusal and does not mark suspect', async () => {
  const tmp = tempDir();
  try {
    const transport = {
      request: async () => {
        throw refused('ECONNREFUSED');
      },
    };
    const ctx = ctxFor(tmp, transport);
    const result = await executeStep({
      kind: 'op',
      side: 'client',
      op: 'reload_status',
    }, ctx);
    assert.equal(result.ok, false);
    assert.equal(result.bridgeDown, true);
    assert.equal(result.timeout, false);
    assert.equal(result.suspect, false);
    assert.equal(ctx.bridge.mainThreadSuspect('client'), null);
  } finally {
    tmp.cleanup();
  }
});
