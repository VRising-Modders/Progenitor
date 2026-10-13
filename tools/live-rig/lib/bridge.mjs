// Library equivalent of tools/mcp/call.mjs: same ports, token files, env overrides,
// JSON-line protocol, live-server token refusal, 30 s default timeout, per-call override.
// Every call writes a "request" line to <runDir>/trace.jsonl before sending, then a
// "response" / "error" / "timeout" line after, sharing an id so a kill mid-call still
// leaves the request on disk.
// Tests inject a transport so nothing here opens a socket.

import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import { requireWorkspacePath } from '../../common/workspace.mjs';
import { detectIdentity, wireWho } from '../../mcp/identity.mjs';
import { createQueue } from '../../mcp/queue.mjs';

export const DEFAULT_TIMEOUT_MS = 30000;
export const LIVE_SERVER_PREFIX = 'c:\\vrisingserver';

/**
 * One table of Dev ops and the thread they run on.
 * Derived from docs/bridge.md (Main thread) and docs/bridge-ops.md (logs / ops), Handlers.MainThreadOps,
 * plus `mods_ui` (client UI) and `exec` (MCP progenitor_exec posted to main via reload).
 * Socket-thread ops stay safe after a main-thread timeout; everything else is treated
 * as main-thread (fail closed for unknown names). `bridge_status` and `dump` are
 * gate-free on the Dev socket (Handlers.cs); they must not go through
 * enqueueMainThread / suspect. `dump` is the freeze-stack request (socket thread).
 */
export const BRIDGE_OPS = Object.freeze([
  { op: 'ping', thread: 'main' },
  { op: 'reload', thread: 'main' },
  { op: 'core_reload', thread: 'main' },
  { op: 'reload_status', thread: 'main' },
  { op: 'users', thread: 'main' },
  { op: 'worlds', thread: 'main' },
  { op: 'command', thread: 'main' },
  { op: 'chat_observe', thread: 'main' },
  { op: 'broadcast', thread: 'main' },
  { op: 'ghost', thread: 'main' },
  { op: 'query', thread: 'main' },
  { op: 'get', thread: 'main' },
  { op: 'set', thread: 'main' },
  { op: 'components', thread: 'main' },
  { op: 'spawn', thread: 'main' },
  { op: 'entity', thread: 'main' },
  { op: 'prefab', thread: 'main' },
  { op: 'buff', thread: 'main' },
  { op: 'heal', thread: 'main' },
  { op: 'teleport', thread: 'main' },
  { op: 'give', thread: 'main' },
  { op: 'inspect', thread: 'main' },
  { op: 'snapshot', thread: 'main' },
  { op: 'restore', thread: 'main' },
  { op: 'players', thread: 'main' },
  { op: 'status', thread: 'main' },
  { op: 'save', thread: 'main' },
  { op: 'world_save', thread: 'main' },
  { op: 'store', thread: 'main' },
  { op: 'connect', thread: 'main' },
  { op: 'host', thread: 'main' },
  { op: 'host_capture', thread: 'main' },
  { op: 'capture', thread: 'main' },
  { op: 'perf', thread: 'main' },
  { op: 'disconnect', thread: 'main' },
  { op: 'quit', thread: 'main' },
  { op: 'castles', thread: 'main' },
  { op: 'mods_ui', thread: 'main' },
  { op: 'keybind_press', thread: 'main' },
  { op: 'exec', thread: 'main' },
  { op: 'logs', thread: 'socket' },
  { op: 'ops', thread: 'socket' },
  { op: 'bridge_status', thread: 'socket' },
  { op: 'bridge_users', thread: 'socket' },
  { op: 'perf_status', thread: 'socket' }, // live progress; must not enqueue behind a main-thread op
  { op: 'dump', thread: 'socket' },
]);

export const SOCKET_THREAD_OPS = Object.freeze(new Set(
  BRIDGE_OPS.filter((row) => row.thread === 'socket').map((row) => row.op),
));

export const MAIN_THREAD_OPS = Object.freeze(new Set(
  BRIDGE_OPS.filter((row) => row.thread === 'main').map((row) => row.op),
));

export function isSocketThreadOp(op) {
  return SOCKET_THREAD_OPS.has(String(op ?? '').toLowerCase());
}

/** True for listed main-thread ops and for any name that is not socket-thread. */
export function isMainThreadOp(op) {
  return !isSocketThreadOp(op);
}

export function suspectMessage(row) {
  if (!row) return 'main thread suspect';
  return `main thread suspect since ${row.since} after ${row.op} id ${row.id}`;
}

const GAME_DIR = process.env.PROGENITOR_GAME_DIR ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRising';
// <workspace>/_testserver unless PROGENITOR_TEST_ROOT says otherwise (tools/common/workspace.mjs).
const TEST_ROOT = process.env.PROGENITOR_TEST_ROOT ?? requireWorkspacePath('PROGENITOR_TEST_ROOT', '_testserver');

export function defaultSides() {
  return {
    client: {
      port: Number(process.env.PROGENITOR_CLIENT_PORT ?? 27341),
      tokenFile: process.env.PROGENITOR_CLIENT_TOKEN_FILE || path.join(GAME_DIR, 'BepInEx', 'config', 'progenitor-dev.token'),
    },
    server: {
      port: Number(process.env.PROGENITOR_SERVER_PORT ?? 27342),
      tokenFile: process.env.PROGENITOR_SERVER_TOKEN_FILE || path.join(TEST_ROOT, 'server', 'BepInEx', 'config', 'progenitor-dev.token'),
    },
  };
}

export function isLiveServerPath(p) {
  if (p == null) return false;
  const raw = String(p).toLowerCase();
  if (raw.startsWith(LIVE_SERVER_PREFIX)) return true;
  try {
    return path.resolve(String(p)).toLowerCase().startsWith(LIVE_SERVER_PREFIX);
  } catch {
    return raw.startsWith(LIVE_SERVER_PREFIX);
  }
}

export function assertTokenPathAllowed(side, tokenFile) {
  if (isLiveServerPath(tokenFile)) {
    throw new Error(`refusing a token file under the live dedicated server for ${side}: ${tokenFile}`);
  }
}

function timeoutFrom(name) {
  const raw = process.env[name];
  if (raw === undefined || raw === null || String(raw).trim() === '') return null;
  const n = Number(raw);
  if (!Number.isFinite(n) || n <= 0) return null;
  return n;
}

export function defaultTimeoutMs() {
  return timeoutFrom('PROGENITOR_CALL_TIMEOUT_MS') ?? timeoutFrom('PROGENITOR_TIMEOUT_MS') ?? DEFAULT_TIMEOUT_MS;
}

/** Socket codes that mean the Dev listener was gone before any response. */
export const BRIDGE_DOWN_CODES = Object.freeze(['ECONNREFUSED', 'ECONNRESET', 'EPIPE']);

const BRIDGE_DOWN_CODE_SET = new Set(BRIDGE_DOWN_CODES);
const BRIDGE_DOWN_MESSAGE_RE = /\bECONNREFUSED\b|\bECONNRESET\b|\bEPIPE\b/;

export function isBridgeDownCode(code) {
  return BRIDGE_DOWN_CODE_SET.has(String(code ?? ''));
}

/**
 * Socket-level connection refusal/reset before any response (Dev unloaded, bind
 * gap). Not a main-thread timeout: the call never entered the gate.
 */
export function isBridgeDownError(err) {
  if (err == null) return false;
  if (err.timeout === true) return false;
  if (err.bridgeDown === true) return true;
  if (isBridgeDownCode(err.code) || isBridgeDownCode(err.errno)) return true;
  return BRIDGE_DOWN_MESSAGE_RE.test(String(err.message ?? err));
}

export class BridgeError extends Error {
  constructor(message, { side, op, timeout = false, suspect = false, bridgeDown = false, code } = {}) {
    super(message);
    this.name = 'BridgeError';
    this.side = side;
    this.op = op;
    this.timeout = timeout;
    this.suspect = suspect;
    this.bridgeDown = bridgeDown;
    if (code != null) this.code = code;
  }
}

function appendTrace(runDir, row) {
  if (!runDir) throw new Error('bridge call requires a runDir so the request is traced');
  fs.mkdirSync(runDir, { recursive: true });
  fs.appendFileSync(path.join(runDir, 'trace.jsonl'), JSON.stringify(row) + '\n', 'utf8');
}

function createTcpTransport(sides) {
  let nextId = 1;
  // Which AI session drives the rig, sent as `who` (tools/mcp/identity.mjs). Detected once per transport.
  let who;
  try { who = wireWho(detectIdentity({ tool: 'live-rig' })); } catch { who = undefined; }
  // Bridge queue (tools/mcp/queue.mjs): like call.mjs, refuse a changing op while another session
  // holds the side. A broken queue file never blocks the rig; a busy lock does.
  let queue = null;
  try { queue = createQueue(); } catch { queue = null; }
  return {
    request(side, op, args, timeoutMs) {
      const cfg = sides[side];
      if (!cfg) return Promise.reject(new Error(`unknown side '${side}'`));
      assertTokenPathAllowed(side, cfg.tokenFile);
      try {
        const refusal = queue?.gate(who ?? {}, side, op, args ?? {}, { requireSlot: false });
        if (refusal) return Promise.reject(new BridgeError(refusal, { side, op }));
      } catch (e) {
        if (/locked/.test(e.message)) return Promise.reject(new BridgeError(`bridge queue: ${e.message}`, { side, op }));
      }
      const token = fs.readFileSync(cfg.tokenFile, 'utf8').trim();
      const id = nextId++;
      const payload = JSON.stringify({ id, token, op, args: args ?? {}, who }) + '\n';
      return new Promise((resolve, reject) => {
        let gotData = false;
        const sock = net.createConnection({ host: '127.0.0.1', port: cfg.port }, () => {
          sock.write(payload);
        });
        let buf = '';
        const timer = setTimeout(() => {
          sock.destroy();
          reject(new BridgeError(`${side} bridge timed out on '${op}' after ${timeoutMs} ms`, { side, op, timeout: true }));
        }, timeoutMs);
        sock.setEncoding('utf8');
        sock.on('data', (chunk) => {
          gotData = true;
          buf += chunk;
          const nl = buf.indexOf('\n');
          if (nl < 0) return;
          clearTimeout(timer);
          sock.end();
          try {
            const msg = JSON.parse(buf.slice(0, nl));
            resolve(msg);
          } catch (e) {
            reject(e);
          }
        });
        sock.on('error', (e) => {
          clearTimeout(timer);
          const down = !gotData && isBridgeDownError(e);
          reject(new BridgeError(`${side} bridge connection failed: ${e.message}`, {
            side,
            op,
            bridgeDown: down,
            code: e && e.code,
          }));
        });
      });
    },
  };
}

/**
 * @param {object} opts
 * @param {string} opts.runDir  directory that receives trace.jsonl
 * @param {{request(side, op, args, timeoutMs): Promise<object>}} [opts.transport] injected; skips sockets
 * @param {object} [opts.sides] port/tokenFile per side
 * @param {number} [opts.timeoutMs] default per-call timeout
 * @param {{now(): number, sleep(ms: number): Promise<void>}} [opts.clock] used by tolerateBridgeDownMs
 */
export function createBridge(opts = {}) {
  const runDir = opts.runDir;
  if (!runDir) throw new Error('createBridge requires runDir');
  const sides = opts.sides ?? defaultSides();
  for (const [k, v] of Object.entries(sides)) {
    if (v && v.tokenFile) assertTokenPathAllowed(k, v.tokenFile);
  }
  const defaultMs = opts.timeoutMs ?? defaultTimeoutMs();
  const transport = opts.transport ?? createTcpTransport(sides);
  const clock = opts.clock ?? {
    now: () => Date.now(),
    sleep: (ms) => new Promise((resolve) => setTimeout(resolve, Math.max(0, Number(ms) || 0))),
  };
  fs.mkdirSync(runDir, { recursive: true });
  let nextTraceId = 1;
  const suspect = Object.create(null);
  const down = Object.create(null);
  const mainThreadChain = Object.create(null);
  const inFlight = Object.create(null);
  let cancelled = false;
  let cancelReason = null;
  const cancelListeners = [];

  function mainThreadSuspect(side) {
    return suspect[side] ?? null;
  }

  /** Current main-thread op on this side, or null. Socket-thread ops never appear here. */
  function mainThreadInFlight(side) {
    return inFlight[side] ?? null;
  }

  function markSuspect(side, { since, op, id }) {
    suspect[side] = { since, op, id };
    return suspect[side];
  }

  function clearSuspect(side) {
    delete suspect[side];
  }

  function bridgeDown(side) {
    return down[side] ?? null;
  }

  function markBridgeDown(side, { since, last, op, id, code }) {
    const prev = down[side];
    down[side] = {
      since: prev?.since ?? since,
      last: last ?? since,
      op,
      id,
      code: code ?? prev?.code ?? null,
    };
    return down[side];
  }

  function clearBridgeDown(side) {
    delete down[side];
  }

  function enqueueMainThread(side, fn) {
    const prev = mainThreadChain[side] ?? Promise.resolve();
    const run = prev.catch(() => {}).then(fn);
    mainThreadChain[side] = run.catch(() => {});
    return run;
  }

  function cancel(reason = 'cancelled') {
    if (cancelled) return;
    cancelled = true;
    cancelReason = reason;
    for (const fn of cancelListeners.splice(0)) {
      try { fn(reason); } catch { /* listener must not break cancel */ }
    }
  }

  function isCancelled() {
    return cancelled;
  }

  function onCancel(fn) {
    if (typeof fn !== 'function') return () => {};
    if (cancelled) {
      try { fn(cancelReason); } catch { /* ignore */ }
      return () => {};
    }
    cancelListeners.push(fn);
    return () => {
      const i = cancelListeners.indexOf(fn);
      if (i >= 0) cancelListeners.splice(i, 1);
    };
  }

  function sleepMs(ms, callOpts) {
    if (cancelled && callOpts?.bypassCancel !== true) {
      return Promise.reject(new BridgeError(cancelReason ?? 'bridge cancelled', {
        timeout: false,
        cancelled: true,
      }));
    }
    const c = callOpts?.clock ?? clock;
    if (typeof c.sleep === 'function') return c.sleep(ms);
    return new Promise((resolve) => setTimeout(resolve, Math.max(0, Number(ms) || 0)));
  }

  function nowMs(callOpts) {
    const c = callOpts?.clock ?? clock;
    if (typeof c.now === 'function') return c.now();
    return Date.now();
  }

  async function dispatch(side, op, args, callOpts) {
    const timeoutMs = callOpts.timeoutMs ?? defaultMs;
    const t0 = Date.now();
    const stamp = new Date().toISOString();
    const id = nextTraceId++;
    const main = isMainThreadOp(op);
    if (main) inFlight[side] = { op, id, since: stamp };
    appendTrace(runDir, { t: stamp, id, type: 'request', side, op, args: args ?? {} });
    let ok = false;
    let result;
    let error;
    let timedOut = false;
    let downErr = false;
    let errCode;
    try {
      try {
        const msg = await transport.request(side, op, args ?? {}, timeoutMs);
        if (msg && typeof msg === 'object' && 'ok' in msg) {
          ok = !!msg.ok;
          result = msg.result;
          error = msg.error;
        } else {
          ok = true;
          result = msg;
        }
      } catch (e) {
        ok = false;
        error = e && e.message ? e.message : String(e);
        timedOut = !!(e && e.timeout) || /timed out/.test(String(error));
        downErr = !timedOut && isBridgeDownError(e);
        errCode = e && e.code != null ? e.code : undefined;
      }
      const ms = Date.now() - t0;
      const type = ok ? 'response' : (timedOut ? 'timeout' : 'error');
      const row = { t: new Date().toISOString(), id, type, side, op, args: args ?? {}, ms, ok };
      if (ok) row.result = result;
      else row.error = error ?? 'unknown';
      if (downErr) row.bridgeDown = true;
      if (errCode != null) row.code = errCode;
      appendTrace(runDir, row);
      if (!ok) {
        if (downErr) {
          markBridgeDown(side, { since: row.t, last: row.t, op, id, code: errCode });
        } else if (timedOut && isMainThreadOp(op)) {
          markSuspect(side, { since: row.t, op, id });
        }
        throw new BridgeError(row.error, {
          side,
          op,
          timeout: downErr ? false : (timedOut || /timed out/.test(String(row.error))),
          bridgeDown: downErr,
          code: errCode,
        });
      }
      return result;
    } finally {
      if (main && inFlight[side]?.id === id) delete inFlight[side];
    }
  }

  async function attempt(side, op, args, callOpts) {
    const recoverPing = callOpts.recover === true && String(op).toLowerCase() === 'ping';
    const bypassSuspect = recoverPing || callOpts.bypassSuspect === true;
    if (!isMainThreadOp(op)) {
      return dispatch(side, op, args, callOpts);
    }
    return enqueueMainThread(side, async () => {
      const held = mainThreadSuspect(side);
      if (held && !bypassSuspect) {
        throw new BridgeError(suspectMessage(held), {
          side,
          op,
          suspect: true,
        });
      }
      const result = await dispatch(side, op, args, callOpts);
      if (recoverPing) clearSuspect(side);
      return result;
    });
  }

  async function call(side, op, args = {}, callOpts = {}) {
    if (!sides[side] && !opts.transport) throw new Error(`unknown side '${side}'`);
    const tolerateMs = Number(callOpts.tolerateBridgeDownMs);
    const waitDown = Number.isFinite(tolerateMs) && tolerateMs > 0;
    if (!waitDown) return attempt(side, op, args, callOpts);

    const t0 = nowMs(callOpts);
    while (true) {
      try {
        return await attempt(side, op, args, callOpts);
      } catch (e) {
        if (!isBridgeDownError(e)) throw e;
        if (cancelled && callOpts.bypassCancel !== true) throw e;
        const elapsed = nowMs(callOpts) - t0;
        if (elapsed >= tolerateMs) throw e;
        const retryMs = Number(callOpts.bridgeDownRetryMs);
        const interval = Number.isFinite(retryMs) && retryMs >= 0 ? retryMs : 250;
        const remaining = tolerateMs - elapsed;
        await sleepMs(Math.max(1, Math.min(interval, remaining)), callOpts);
      }
    }
  }

  return {
    call,
    runDir,
    sides,
    timeoutMs: defaultMs,
    mainThreadSuspect,
    mainThreadInFlight,
    markSuspect,
    clearSuspect,
    bridgeDown,
    markBridgeDown,
    clearBridgeDown,
    isMainThreadOp,
    isSocketThreadOp,
    cancel,
    isCancelled,
    onCancel,
  };
}
