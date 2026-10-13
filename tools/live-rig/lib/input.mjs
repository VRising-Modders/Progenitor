import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import {
  trackSpawnedChild,
  isRunCancelled,
  childRegistry,
} from './steps.mjs';

/** Evidence label for observe.ps1 SendInput. Never handler-invocation or physical. */
export const INPUT_EVIDENCE_LABEL = 'os-injected';
export const INPUT_DEFAULT_TIMEOUT_MS = 10000;
export const INPUT_DEFAULT_WINDOW = 'client';
export const INPUT_ACTIONS = Object.freeze(['click', 'move', 'scroll', 'key', 'type']);
export const INPUT_POINTER_ACTIONS = Object.freeze(['click', 'move', 'scroll']);
export const INPUT_DEFAULT_SCROLL_DELTA = -3;
const TAIL_CHARS = 4000;

export function windowsPowerShell51Path() {
  const root = process.env.SystemRoot || process.env.WINDIR || 'C:\\Windows';
  return path.join(root, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
}

export function observePs1Path(repoRoot) {
  return path.join(repoRoot, 'tools', 'observe.ps1');
}

function quoteArg(value) {
  const t = String(value);
  if (t === '') return '""';
  if (!/[\s"]/.test(t)) return t;
  return `"${t.replace(/"/g, '\\"')}"`;
}

export function formatCommandLine(cmd, args) {
  return [cmd, ...(args ?? [])].map(quoteArg).join(' ');
}

/**
 * observe.ps1 argv for an OS-injected action.
 * Coordinates are client-area pixels (no -ScreenCoords). Game input requires -AllowGameInput.
 * -Json puts the result object on stdout (explanatory lines on stderr).
 */
export function observeInputArgv({
  script,
  action,
  x,
  y,
  keys,
  text,
  delta,
  window = INPUT_DEFAULT_WINDOW,
} = {}) {
  const argv = [
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', script,
    action,
  ];
  if (action === 'key') argv.push(String(keys ?? ''));
  if (action === 'type') argv.push(String(text ?? ''));
  argv.push('-Window', window);
  if (x != null && Number.isFinite(Number(x))) argv.push('-X', String(Math.round(Number(x))));
  if (y != null && Number.isFinite(Number(y))) argv.push('-Y', String(Math.round(Number(y))));
  if (action === 'scroll') {
    const d = Number.isFinite(Number(delta)) ? Number(delta) : INPUT_DEFAULT_SCROLL_DELTA;
    argv.push('-Delta', String(d));
  }
  argv.push('-AllowGameInput', '-Json');
  return argv;
}

export function clickPointFromRect(rect, offset) {
  const x = Number(rect?.x ?? rect?.left ?? 0);
  const y = Number(rect?.y ?? rect?.top ?? 0);
  const width = Number(rect?.width ?? ((rect?.right != null && rect?.left != null) ? rect.right - rect.left : 0));
  const height = Number(rect?.height ?? ((rect?.bottom != null && rect?.top != null) ? rect.bottom - rect.top : 0));
  const ox = Number(offset?.x ?? 0);
  const oy = Number(offset?.y ?? 0);
  return {
    x: Math.round(x + width / 2 + ox),
    y: Math.round(y + height / 2 + oy),
  };
}

/**
 * Case-language `target.rectOf` aliases → live `mods_ui rects` `id`.
 * Patterned selectors (`sidebarRow:<plugin>`, `nativeTab:<label>`,
 * `scrollbar:<pane>:handle|track`, `widget:<label>:input|toggle|apply`)
 * are resolved in `findControl`.
 */
export const RECT_SELECTOR_ALIASES = Object.freeze({
  topTab: 'top-tab',
  mainMenuButton: 'main-menu',
  close: 'close-back',
  search: 'search',
});

/** Map a 1:1 alias (`topTab` → `top-tab`). Patterned selectors are unchanged. */
export function canonicalRectId(selector) {
  const want = String(selector ?? '');
  if (Object.prototype.hasOwnProperty.call(RECT_SELECTOR_ALIASES, want)) {
    return RECT_SELECTOR_ALIASES[want];
  }
  if (want.startsWith('scrollbar:')) {
    const parts = want.split(':');
    if (parts.length === 3 && parts[1] && parts[2]) return `${parts[1]}-scrollbar-${parts[2]}`;
  }
  return want;
}

function asControlList(payload) {
  const detail = payload?.detail && typeof payload.detail === 'object' ? payload.detail : {};
  const list = [];
  const push = (row, fallbackId) => {
    if (row == null) return;
    if (typeof row !== 'object') return;
    list.push({
      id: row.id ?? row.selector ?? fallbackId,
      selector: row.selector ?? row.id ?? fallbackId,
      ...row,
    });
  };
  const bags = [payload, detail];
  for (const bag of bags) {
    if (!bag || typeof bag !== 'object') continue;
    if (Array.isArray(bag.controls)) {
      for (const row of bag.controls) push(row);
    }
    if (Array.isArray(bag.rects)) {
      for (const row of bag.rects) push(row);
    }
    if (bag.rects && typeof bag.rects === 'object' && !Array.isArray(bag.rects)) {
      for (const [key, row] of Object.entries(bag.rects)) push(row, key);
    }
    if (bag.controls && typeof bag.controls === 'object' && !Array.isArray(bag.controls)) {
      for (const [key, row] of Object.entries(bag.controls)) push(row, key);
    }
  }
  return list;
}

function qualifyControl(row) {
  const id = String(row?.id ?? '');
  if (id === 'native-tab' && row.label) return `native-tab:${row.label}`;
  if (id === 'sidebar-row' && row.plugin) return `sidebar-row:${row.plugin}`;
  if (id === 'widget' && row.label) return `widget:${row.label}`;
  return id || String(row?.selector ?? row?.name ?? '');
}

export function describeAvailableControls(payload) {
  const names = [];
  const seen = new Set();
  const add = (name) => {
    const t = String(name ?? '');
    if (!t || seen.has(t)) return;
    seen.add(t);
    names.push(t);
  };
  for (const row of asControlList(payload)) {
    add(qualifyControl(row));
    add(row.id);
    const label = row.label != null ? String(row.label) : '';
    if (Array.isArray(row.controls)) {
      for (const child of row.controls) {
        if (!child || typeof child !== 'object') continue;
        const cid = String(child.id ?? '');
        if (row.id === 'widget' && label && cid) add(`widget:${label}:${cid}`);
        else if (cid) add(`${qualifyControl(row)}:${cid}`);
      }
    }
  }
  return names;
}

function sameText(a, b) {
  return String(a ?? '') === String(b ?? '');
}

function nestedControls(row) {
  return Array.isArray(row?.controls) ? row.controls.filter((c) => c && typeof c === 'object') : [];
}

function matchNestedPart(widget, part) {
  const want = String(part ?? '');
  if (!want) return null;
  return nestedControls(widget).find((c) => c.id === want || c.kind === want || sameText(c.label, want)) ?? null;
}

/**
 * Resolve a case-language `rectOf` against a live `mods_ui rects` payload
 * (`detail.rects` rows with `id` / `kind` / `label` / `plugin` / `index` / `controls`).
 */
export function findControl(payload, selector) {
  const want = String(selector ?? '');
  if (!want) return null;
  const list = asControlList(payload);
  const exact = list.find((row) => row.selector === want || row.id === want || String(row.name ?? '') === want);
  if (exact) return exact;

  const mapped = RECT_SELECTOR_ALIASES[want];
  if (mapped) return list.find((row) => row.id === mapped) ?? null;

  if (want.startsWith('sidebarRow:')) {
    const plugin = want.slice('sidebarRow:'.length);
    return list.find((row) => row.id === 'sidebar-row' && sameText(row.plugin, plugin)) ?? null;
  }
  if (want.startsWith('nativeTab:')) {
    const label = want.slice('nativeTab:'.length);
    return list.find((row) => row.id === 'native-tab' && sameText(row.label, label)) ?? null;
  }
  if (want.startsWith('scrollbar:')) {
    const id = canonicalRectId(want);
    if (id !== want) return list.find((row) => row.id === id) ?? null;
  }
  if (want.startsWith('widget:')) {
    const rest = want.slice('widget:'.length);
    const colon = rest.lastIndexOf(':');
    const label = colon >= 0 ? rest.slice(0, colon) : rest;
    const part = colon >= 0 ? rest.slice(colon + 1) : '';
    const widget = list.find((row) => row.id === 'widget' && sameText(row.label, label)) ?? null;
    if (!part) return widget;
    const nested = widget ? matchNestedPart(widget, part) : null;
    if (nested) return nested;
    if (part === 'apply') {
      return list.find((row) => row.id === 'widget' && sameText(row.label, `Apply ${label}`)) ?? null;
    }
    return null;
  }
  return null;
}

export function occludedByOf(control) {
  const raw = control?.occludedBy;
  if (raw == null || raw === false) return [];
  if (Array.isArray(raw)) return raw.filter((x) => x != null && x !== '' && x !== false).map(String);
  if (typeof raw === 'string') return raw ? [raw] : [];
  return [String(raw)];
}

export function controlRefusal(control, selector, payload) {
  if (!control) {
    const available = payload != null ? describeAvailableControls(payload) : [];
    const listed = available.length ? available.join(', ') : '(none)';
    return `target ${JSON.stringify(selector)} was not in mods_ui rects (available ids: ${listed})`;
  }
  if (control.visible === false) return `target ${JSON.stringify(selector)} is not visible`;
  if (control.interactable === false) return `target ${JSON.stringify(selector)} is not interactable`;
  const occluded = occludedByOf(control);
  if (occluded.length) return `target ${JSON.stringify(selector)} is occluded by ${occluded.join(', ')}`;
  return null;
}

export function parseObserveStdout(text) {
  const lines = String(text ?? '').split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  for (let i = lines.length - 1; i >= 0; i--) {
    try {
      return JSON.parse(lines[i]);
    } catch {
      /* keep scanning */
    }
  }
  return null;
}

function clockNowMs(ctx) {
  const n = ctx?.clock?.now?.();
  return Number.isFinite(n) ? n : Date.now();
}

function errorText(err) {
  if (err == null) return 'unknown';
  if (typeof err === 'string') return err;
  if (typeof err === 'object' && err.message) return err.message;
  return String(err);
}

function lookupRecordPath(ctx, spec) {
  const text = String(spec ?? '');
  const dot = text.indexOf('.');
  const name = dot < 0 ? text : text.slice(0, dot);
  const rest = dot < 0 ? '' : text.slice(dot + 1);
  if (!name) throw new Error('@record: missing name');
  if (!(name in (ctx.records ?? {}))) throw new Error(`@record:${name} is missing`);
  const rec = ctx.records[name];
  if (!rest) return rec;
  const parts = rest.split('.');
  let cur = rec;
  for (const part of parts) {
    if (cur == null) throw new Error(`@record:${name}.${rest} is missing`);
    cur = Array.isArray(cur) && /^\d+$/.test(part) ? cur[Number(part)] : cur[part];
  }
  if (cur === undefined) throw new Error(`@record:${name}.${rest} is missing`);
  return cur;
}

function expandRefs(value, ctx) {
  if (typeof value === 'string' && value.startsWith('@record:')) {
    return lookupRecordPath(ctx, value.slice('@record:'.length));
  }
  if (Array.isArray(value)) return value.map((v) => expandRefs(v, ctx));
  if (value && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = expandRefs(v, ctx);
    return out;
  }
  return value;
}

function getByPath(obj, pathStr) {
  if (pathStr == null || pathStr === '') return obj;
  const parts = String(pathStr).split('.');
  let cur = obj;
  for (const part of parts) {
    if (cur == null) return undefined;
    cur = Array.isArray(cur) && /^\d+$/.test(part) ? cur[Number(part)] : cur[part];
  }
  return cur;
}

function valuesEqual(got, want) {
  if (Object.is(got, want)) return true;
  if (want != null && got != null && typeof want === 'object') {
    try { return JSON.stringify(got) === JSON.stringify(want); } catch { return false; }
  }
  return false;
}

function matchExpect(expect, result) {
  if (expect == null) return { ok: true };
  if (typeof expect === 'string' || expect instanceof RegExp) {
    let json;
    try { json = JSON.stringify(result); } catch { json = String(result); }
    const re = expect instanceof RegExp ? expect : new RegExp(expect);
    if (!re.test(json)) return { ok: false, error: `expect regex ${re} did not match ${json}` };
    return { ok: true };
  }
  if (typeof expect !== 'object' || Array.isArray(expect)) {
    return { ok: false, error: `expect must be a path map or a regex, got ${typeof expect}` };
  }
  for (const [p, want] of Object.entries(expect)) {
    const got = getByPath(result, p);
    if (want && typeof want === 'object' && !Array.isArray(want) && Object.prototype.hasOwnProperty.call(want, 'neq') && Object.keys(want).length === 1) {
      if (valuesEqual(got, want.neq)) {
        return { ok: false, error: `expect ${p}: wanted not ${JSON.stringify(want.neq)}, got ${JSON.stringify(got)}` };
      }
      continue;
    }
    if (!valuesEqual(got, want)) {
      return { ok: false, error: `expect ${p}: wanted ${JSON.stringify(want)}, got ${JSON.stringify(got)}` };
    }
  }
  return { ok: true };
}

function wrapSpawnSyncAsSpawn(spawnSyncFn) {
  return (cmd, args, opts) => {
    const child = new EventEmitter();
    const stdout = new PassThrough();
    const stderr = new PassThrough();
    child.stdout = stdout;
    child.stderr = stderr;
    child.pid = 0;
    child.exitCode = null;
    child.killed = false;
    child.kill = () => { child.killed = true; };
    queueMicrotask(() => {
      if (child.killed) {
        stdout.end();
        stderr.end();
        child.emit('close', null, 'SIGTERM');
        return;
      }
      let spawned;
      try {
        spawned = spawnSyncFn(cmd, args, opts);
      } catch (e) {
        stdout.end();
        stderr.end();
        child.emit('error', e);
        child.emit('close', null, null);
        return;
      }
      const out = spawned?.stdout;
      const err = spawned?.stderr;
      if (out) stdout.write(Buffer.isBuffer(out) ? out : String(out));
      if (err) stderr.write(Buffer.isBuffer(err) ? err : String(err));
      stdout.end();
      stderr.end();
      child.exitCode = spawned?.status ?? null;
      const code = spawned?.error && spawned.error.code;
      if (code === 'ETIMEDOUT' || code === 'ETIME') child.emit('error', spawned.error);
      child.emit('close', spawned?.status ?? null, spawned?.signal ?? null);
    });
    return child;
  };
}

function resolveSpawn(ctx) {
  if (typeof ctx.spawn === 'function') return ctx.spawn;
  if (typeof ctx.spawnSync === 'function') return wrapSpawnSyncAsSpawn(ctx.spawnSync);
  return spawn;
}

function refusesUnderTest(ctx) {
  return !!(process.env.NODE_TEST_CONTEXT
    && typeof ctx.spawn !== 'function'
    && typeof ctx.spawnSync !== 'function');
}

function attachStreamTail(stream) {
  let buf = '';
  if (stream && typeof stream.on === 'function') {
    stream.on('data', (chunk) => {
      buf += Buffer.isBuffer(chunk) ? chunk.toString('utf8') : String(chunk);
      if (buf.length > TAIL_CHARS) buf = buf.slice(buf.length - TAIL_CHARS);
    });
  }
  return () => buf;
}

function waitForSpawnedChild(child) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (status, signal, error) => {
      if (settled) return;
      settled = true;
      resolve({ status, signal, error });
    };
    if (!child || typeof child.once !== 'function') {
      finish(null, null, new Error('input spawn did not return a child process'));
      return;
    }
    child.once('error', (err) => finish(child.exitCode ?? null, null, err));
    child.once('close', (code, signal) => finish(code, signal, null));
  });
}

async function callModsUi(ctx, action, args = {}) {
  const side = 'client';
  const result = await ctx.bridge.call(side, 'mods_ui', { action, ...args });
  return result;
}

function siteEntrySelector(ctx) {
  const site = ctx.caseSite ?? ctx.caseDoc?.site;
  if (site === 'top-tab') return 'topTab';
  if (site === 'main-menu') return 'mainMenuButton';
  return null;
}

function resolveSelector(raw, ctx) {
  const sel = String(raw ?? '');
  if (sel === '@site') {
    const mapped = siteEntrySelector(ctx);
    if (!mapped) throw new Error("target.rectOf '@site' needs caseSite main-menu or top-tab");
    return mapped;
  }
  return sel;
}

async function readSince(ctx) {
  try {
    const snap = await ctx.bridge.call('client', 'logs', { since: -1, max: 1, filter: '' });
    if (typeof snap?.latestSeq === 'number') return snap.latestSeq;
  } catch {
    /* still run */
  }
  return -1;
}

function stampRecord(step, ctx, recorded, since) {
  if (!step.record) return;
  const seenAt = clockNowMs(ctx);
  ctx.records[step.record] = { ...recorded, since, logSeq: since, seenAt };
}

function evidenceLabel(step) {
  const label = step.label == null || step.label === '' ? INPUT_EVIDENCE_LABEL : String(step.label);
  return label;
}

function caseShortcut(ctx, step) {
  const fromStep = step?.shortcut;
  const fromCase = ctx?.caseDoc?.shortcut;
  const raw = fromStep != null && String(fromStep) !== '' ? fromStep : fromCase;
  if (raw == null || String(raw) === '') return null;
  return String(raw);
}

/** Prefer `mods_ui status.shortcut`; if absent, the case/step `shortcut` field (e.g. "F10"). */
function expandKeysWithShortcutFallback(step, ctx) {
  if (step.keys == null) return undefined;
  try {
    const expanded = expandRefs(step.keys, ctx);
    if (expanded != null && String(expanded) !== '') return expanded;
  } catch (e) {
    const fallback = caseShortcut(ctx, step);
    if (fallback != null) return fallback;
    throw e;
  }
  const fallback = caseShortcut(ctx, step);
  if (fallback != null) return fallback;
  return expandRefs(step.keys, ctx);
}

/** Live `mods_ui window` detail: `{client:{x,y,width,height,screenX,screenY}, screen, fullScreenMode, mode}`. */
function windowPayload(result) {
  if (result?.detail && typeof result.detail === 'object' && result.detail.client) return result.detail;
  if (result?.client && typeof result.client === 'object') return result;
  if (result?.after && typeof result.after === 'object' && result.after.client) return result.after;
  if (result?.detail && typeof result.detail === 'object') return result.detail;
  return result && typeof result === 'object' ? result : {};
}

function windowFields(result) {
  const src = windowPayload(result);
  const client = src.client && typeof src.client === 'object' ? src.client : {};
  const screen = src.screen && typeof src.screen === 'object' ? src.screen : {};
  const primary = src.primaryDisplay && typeof src.primaryDisplay === 'object' ? src.primaryDisplay : {};
  return {
    clientWidth: client.width,
    clientHeight: client.height,
    clientX: client.x,
    clientY: client.y,
    screenX: client.screenX,
    screenY: client.screenY,
    screenWidth: screen.width,
    screenHeight: screen.height,
    mode: src.mode ?? src.fullScreenMode,
    fullScreenMode: src.fullScreenMode ?? src.mode,
    displayWidth: primary.width,
    displayHeight: primary.height,
  };
}

function normalizeMode(value) {
  const t = String(value ?? '').trim().toLowerCase();
  if (t === 'window' || t === 'windowed') return 'windowed';
  return t;
}

async function spawnObserve(ctx, argv, timeoutMs) {
  const repoRoot = ctx.repoRoot;
  const powershell = ctx.powershellPath ?? windowsPowerShell51Path();
  const command = formatCommandLine(powershell, argv);
  if (refusesUnderTest(ctx)) {
    return {
      ok: false,
      error: 'input refuses to spawn observe.ps1 under the Node test runner without an injected spawn',
      command,
      argv,
      exitCode: null,
      stdout: '',
      stderr: '',
      stdoutTail: '',
      stderrTail: '',
      timedOut: false,
    };
  }
  if (!repoRoot) {
    return {
      ok: false,
      error: 'input requires repoRoot',
      command,
      argv,
      exitCode: null,
      stdout: '',
      stderr: '',
      stdoutTail: '',
      stderrTail: '',
      timedOut: false,
    };
  }
  const spawnFn = resolveSpawn(ctx);
  if (spawnFn === spawn) {
    if (!fs.existsSync(powershell)) {
      return {
        ok: false,
        error: `Windows PowerShell 5.1 is required for OS-injected input; runtime not found: ${powershell}`,
        command,
        argv,
        exitCode: null,
        stdout: '',
        stderr: '',
        stdoutTail: '',
        stderrTail: '',
        timedOut: false,
      };
    }
    const script = argv[argv.indexOf('-File') + 1];
    if (script && !fs.existsSync(script)) {
      return {
        ok: false,
        error: `observe.ps1 missing: ${script}`,
        command,
        argv,
        exitCode: null,
        stdout: '',
        stderr: '',
        stdoutTail: '',
        stderrTail: '',
        timedOut: false,
      };
    }
  }
  if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
    return {
      ok: false,
      error: 'run cancelled',
      cancelled: true,
      command,
      argv,
      exitCode: null,
      stdout: '',
      stderr: '',
      stdoutTail: '',
      stderrTail: '',
      timedOut: false,
    };
  }
  const startedMs = clockNowMs(ctx);
  const spawnOpts = {
    cwd: repoRoot,
    timeout: timeoutMs,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  };
  let child;
  try {
    child = spawnFn(powershell, argv, spawnOpts);
  } catch (e) {
    return {
      ok: false,
      error: errorText(e),
      command,
      argv,
      exitCode: null,
      stdout: '',
      stderr: '',
      stdoutTail: '',
      stderrTail: '',
      timedOut: false,
      wallMs: clockNowMs(ctx) - startedMs,
    };
  }
  trackSpawnedChild(ctx, child, { kind: 'input', cmd: powershell, args: argv });
  if (isRunCancelled(ctx) || childRegistry(ctx).refuseNew) {
    try { child?.kill?.('SIGTERM'); } catch { /* gone */ }
  }
  const readStdout = attachStreamTail(child?.stdout);
  const readStderr = attachStreamTail(child?.stderr);
  let shotTimer;
  const timeoutRace = new Promise((resolve) => {
    const fire = () => {
      try { child?.kill?.(); } catch { /* gone */ }
      resolve({ timedOut: true, status: null, signal: 'SIGTERM', error: { code: 'ETIMEDOUT' } });
    };
    shotTimer = setTimeout(fire, timeoutMs);
  });
  const ended = await Promise.race([
    waitForSpawnedChild(child).then((r) => ({ timedOut: false, ...r })),
    timeoutRace,
  ]);
  if (shotTimer) clearTimeout(shotTimer);
  const stdout = readStdout();
  const stderr = readStderr();
  const timedOut = ended.timedOut === true
    || (ended.error && (ended.error.code === 'ETIMEDOUT' || ended.error.code === 'ETIME'));
  const exitCode = ended.status == null ? null : ended.status;
  const wallMs = Math.max(0, clockNowMs(ctx) - startedMs);
  const json = parseObserveStdout(stdout);
  if (isRunCancelled(ctx)) {
    return {
      ok: false,
      error: 'run cancelled',
      cancelled: true,
      timedOut: false,
      command,
      argv,
      exitCode,
      stdout,
      stderr,
      stdoutTail: stdout,
      stderrTail: stderr,
      json,
      wallMs,
    };
  }
  if (timedOut) {
    return {
      ok: false,
      error: `observe.ps1 ${argv[5] ?? 'input'} timed out after ${timeoutMs}ms`,
      timedOut: true,
      command,
      argv,
      exitCode,
      stdout,
      stderr,
      stdoutTail: stdout,
      stderrTail: stderr,
      json,
      wallMs,
    };
  }
  if (exitCode !== 0) {
    return {
      ok: false,
      error: `observe.ps1 ${argv[5] ?? 'input'} exited ${exitCode}`,
      timedOut: false,
      command,
      argv,
      exitCode,
      stdout,
      stderr,
      stdoutTail: stdout,
      stderrTail: stderr,
      json,
      wallMs,
    };
  }
  return {
    ok: true,
    timedOut: false,
    command,
    argv,
    exitCode,
    stdout,
    stderr,
    stdoutTail: stdout,
    stderrTail: stderr,
    json,
    wallMs,
  };
}

export async function stepInput(step, ctx) {
  const action = String(step.action ?? '').trim().toLowerCase();
  const label = evidenceLabel(step);
  const timeoutMs = Number.isFinite(Number(step.timeoutMs))
    ? Math.max(1, Number(step.timeoutMs))
    : INPUT_DEFAULT_TIMEOUT_MS;
  const since = step.record ? await readSince(ctx) : -1;

  const fail = (error, extra = {}) => {
    const out = {
      ok: false,
      error,
      action,
      label,
      evidence: label,
      since,
      ...extra,
    };
    stampRecord(step, ctx, out, since);
    return out;
  };

  if (!INPUT_ACTIONS.includes(action)) {
    return fail(`input action must be ${INPUT_ACTIONS.join('|')}, got ${JSON.stringify(step.action)}`);
  }
  if (label !== INPUT_EVIDENCE_LABEL) {
    return fail(`input evidence label must be ${INPUT_EVIDENCE_LABEL} (never handler-invocation or physical), got ${JSON.stringify(label)}`);
  }

  let keys;
  let text;
  try {
    keys = step.keys != null ? expandKeysWithShortcutFallback(step, ctx) : undefined;
    text = step.text != null ? expandRefs(step.text, ctx) : undefined;
  } catch (e) {
    return fail(errorText(e));
  }
  if (action === 'key' && (keys == null || String(keys) === '')) {
    return fail('input key requires keys');
  }
  if (action === 'type' && text == null) {
    return fail('input type requires text');
  }

  const needsTarget = INPUT_POINTER_ACTIONS.includes(action) || step.target != null;
  let selector = null;
  let rect = null;
  let point = null;
  let rectsResult = null;
  if (needsTarget) {
    const rectOf = step.target?.rectOf ?? step.target?.selector ?? step.rectOf;
    if (rectOf == null || String(rectOf) === '') {
      return fail(`input ${action} requires target.rectOf`);
    }
    try {
      selector = resolveSelector(expandRefs(rectOf, ctx), ctx);
    } catch (e) {
      return fail(errorText(e));
    }
  }

  let before;
  try {
    before = await callModsUi(ctx, 'status');
  } catch (e) {
    return fail(errorText(e));
  }

  if (selector) {
    try {
      rectsResult = await callModsUi(ctx, 'rects');
    } catch (e) {
      return fail(errorText(e), { before });
    }
    rect = findControl(rectsResult, selector);
    const refused = controlRefusal(rect, selector, rectsResult);
    if (refused) return fail(refused, { before, rects: rectsResult, rect, target: selector });
    let offset = step.target?.offset ?? step.offset;
    try { offset = offset != null ? expandRefs(offset, ctx) : offset; } catch (e) {
      return fail(errorText(e), { before, rect, target: selector });
    }
    point = clickPointFromRect(rect, offset);
  }

  const repoRoot = ctx.repoRoot;
  const script = observePs1Path(repoRoot ?? '');
  const argv = observeInputArgv({
    script,
    action,
    x: point?.x,
    y: point?.y,
    keys,
    text,
    delta: step.delta,
    window: step.window ?? INPUT_DEFAULT_WINDOW,
  });
  const observe = await spawnObserve(ctx, argv, timeoutMs);

  let after;
  try {
    after = await callModsUi(ctx, 'status');
  } catch (e) {
    after = { ok: false, error: errorText(e) };
  }

  const out = {
    ok: observe.ok,
    error: observe.error,
    action,
    label,
    evidence: INPUT_EVIDENCE_LABEL,
    target: selector,
    rect,
    point,
    keys: keys ?? null,
    text: text ?? null,
    delta: action === 'scroll' ? (step.delta ?? INPUT_DEFAULT_SCROLL_DELTA) : null,
    before: before?.after ?? before,
    after: after?.after ?? after,
    status: { before, after },
    cancelled: observe.cancelled === true,
    observe: {
      command: observe.command,
      argv: observe.argv,
      exitCode: observe.exitCode,
      stdout: observe.stdout,
      stderr: observe.stderr,
      stdoutTail: observe.stdoutTail,
      stderrTail: observe.stderrTail,
      json: observe.json ?? null,
      timedOut: observe.timedOut === true,
      cancelled: observe.cancelled === true,
      wallMs: observe.wallMs,
    },
    command: observe.command,
    argv: observe.argv,
    exitCode: observe.exitCode,
    stdoutTail: observe.stdoutTail,
    stderrTail: observe.stderrTail,
    since,
  };

  if (out.ok && step.expect != null) {
    let expect;
    try { expect = expandRefs(step.expect, ctx); } catch (e) {
      out.ok = false;
      out.error = errorText(e);
      stampRecord(step, ctx, out, since);
      return out;
    }
    const matched = matchExpect(expect, { ...out, ...(out.after && typeof out.after === 'object' ? { after: out.after } : {}) });
    if (!matched.ok) {
      out.ok = false;
      out.error = matched.error;
    }
  }

  stampRecord(step, ctx, out, since);
  return out;
}

export async function stepAssertWindow(step, ctx) {
  const since = step.record ? await readSince(ctx) : -1;
  let result;
  try {
    result = await callModsUi(ctx, 'window');
  } catch (e) {
    const out = { ok: false, error: errorText(e), action: 'window', since };
    stampRecord(step, ctx, out, since);
    return out;
  }
  const fields = windowFields(result);
  let expect;
  try {
    expect = step.expect != null ? expandRefs(step.expect, ctx) : null;
  } catch (e) {
    const out = { ok: false, error: errorText(e), action: 'window', ...fields, result, since };
    stampRecord(step, ctx, out, since);
    return out;
  }
  const mismatches = [];
  if (expect && typeof expect === 'object' && !Array.isArray(expect)) {
    if (Object.prototype.hasOwnProperty.call(expect, 'clientWidth') && Number(fields.clientWidth) !== Number(expect.clientWidth)) {
      mismatches.push(`clientWidth: wanted ${expect.clientWidth}, got ${fields.clientWidth}`);
    }
    if (Object.prototype.hasOwnProperty.call(expect, 'clientHeight') && Number(fields.clientHeight) !== Number(expect.clientHeight)) {
      mismatches.push(`clientHeight: wanted ${expect.clientHeight}, got ${fields.clientHeight}`);
    }
    if (Object.prototype.hasOwnProperty.call(expect, 'mode') && normalizeMode(fields.mode) !== normalizeMode(expect.mode)) {
      mismatches.push(`mode: wanted ${expect.mode}, got ${fields.mode ?? fields.fullScreenMode}`);
    }
  }
  const out = {
    ok: mismatches.length === 0,
    error: mismatches.length ? `assertWindow mismatch: ${mismatches.join('; ')}` : undefined,
    action: 'window',
    ...fields,
    result,
    expect,
    since,
  };
  stampRecord(step, ctx, out, since);
  return out;
}
