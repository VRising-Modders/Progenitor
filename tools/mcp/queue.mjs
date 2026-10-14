#!/usr/bin/env node
// Bridge queue: which AI session may drive the client and the test server, who waits, and for how
// long (docs/bridge.md, "Bridge queue"). Several agents share one client and one test server; the
// queue keeps them from stomping on each other and releases a side as soon as its holder is done,
// gone or idle.
//
// One JSON file for the whole workspace (PROGENITOR_QUEUE_FILE, default <workspace>/_build/
// bridge-queue.json), shared by every MCP process, call.mjs and this file's CLI. Each entry claims one
// or both sides. Strict first-come order per side: a waiter becomes active as soon as no active entry
// and no earlier waiter claims any of its sides, so a server-only job can run beside a client-only one.
//
// An active entry must state the Progenitor version and every mod it runs, and an estimate in minutes.
// It shows how it can be reached (MCP process ID and host, last activity, the session's own address)
// so others can see whether it is really still working. Only the next two waiters for a holder's
// sides may contact or evict it; everyone else waits.
//
// Mailbox: the next two waiters for a holder can message it (`message`) and the holder can reply.
// Messages and the "N waiting" reminder are delivered wherever the recipient works: MCP tool results,
// any queue CLI call, and call.mjs (stderr; PowerShell scripts that parse its output opt out with
// PROGENITOR_CALL_NOTES=0). A holder can hand back one side and keep the other (`release`).
// A sender keeps watching for the reply (`wait` returns when mail arrives); one that cannot must
// `retract`. Leaving the queue as a waiter, an exited process, a dropped waiter or an idle release
// retracts the session's open messages automatically; a delivered one leaves the recipient a notice.
//
// Released automatically: a holder or waiter whose MCP process has exited; a holder idle for
// IDLE_RELEASE_WAITING_MS while someone waits for its sides, or IDLE_RELEASE_MS regardless; a waiter
// without a live process that has not checked in for WAITER_STALE_MS.
//
// CLI (agents without the MCP server):
//   node tools/mcp/queue.mjs status
//   node tools/mcp/queue.mjs join --sides client,server --minutes 30 --progenitor 0.1.0+abc1234 \
//        --mods "Example Mod@1.2.0,Other@0.3.1" --purpose "hot reload check" [--contact "..."]
//   node tools/mcp/queue.mjs update|leave|probe|evict --reason "..."
//   node tools/mcp/queue.mjs message --text "need the client for 15 min" [--to <position>]
//   node tools/mcp/queue.mjs release --sides client   (holder: hand back one side, keep the other)
//   node tools/mcp/queue.mjs wait --seconds 300        (returns when you hold your sides or mail arrives)
//   node tools/mcp/queue.mjs retract [--reason "..."]  (withdraw messages you can no longer watch)
//   node tools/mcp/queue.mjs gate --sides client --op deploy   (scripts: exit 5 when another session holds a side)
// The CLI is a short-lived process, so its entries are kept alive by activity (any bridge call through
// call.mjs or another CLI call from the same session), not by a process ID.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { workspacePath } from "../common/workspace.mjs";
import { describeUser, detectIdentity } from "./identity.mjs";

export const SIDES = ["client", "server"];
export const MAX_CONTACTS = 2;
export const IDLE_RELEASE_WAITING_MS = 15 * 60_000;
export const IDLE_RELEASE_MS = 30 * 60_000;
export const WAITER_STALE_MS = 20 * 60_000;
export const EVICT_IDLE_MS = 10 * 60_000;
export const TOUCH_THROTTLE_MS = 30_000;
export const MAX_HISTORY = 50;
export const MAX_MESSAGES = 100;
export const MESSAGE_KEEP_MS = 6 * 60 * 60_000;
export const UNANSWERED_MS = 10 * 60_000;
export const REMIND_EVERY_MS = 5 * 60_000;
const MAX_UNREAD_PER_SENDER = 2;
const MAX_MESSAGE = 300;
export const MONITOR_INSTRUCTION = "Keep watching for the reply: progenitor_queue action=wait (returns as soon as mail arrives) or status, or any MCP or call.mjs call. If you cannot keep watching (session ending, long offline work), retract it: progenitor_queue action=retract.";
export const OBSERVE_REFRESH_MS = 60_000;
export const OVERRIDE_NOTICE_MS = 5 * 60_000;
const MAX_EVENTS = 20;
const MAX_TEXT = 200;

/** Bridge ops that only read. Every other op changes the game and needs the side's queue slot. */
const READ_ONLY_OPS = new Set([
  "ping", "logs", "ops", "status", "users", "players", "worlds", "query", "get", "components", "inspect",
  "prefab", "snapshot", "castles", "bridge_status", "bridge_users", "reload_status", "perf_status",
  "versions", "versions_journal", "versions_plan", "catalog_status", "datetime_read", "rejoin_read", "tree",
  // BRIDGEUI1: widget readback, and a capture the game writes to a file (the game state is unchanged).
  "ui_read", "screenshot",
]);
const READ_ONLY_ACTIONS = new Set(["status", "list", "live"]);
/** Ops whose default action (no `action` given) is status. */
const STATUS_BY_DEFAULT = new Set(["time", "photo", "unlock"]);
/** mods_ui actions that change nothing (ModsUiOps.IsLightRead plus status and widget_read). */
const MODS_UI_READS = new Set(["status", "widget_read", "menu_tab_state", "nav_state", "rects", "window", "scene", "tabs", "perf", "settings_places"]);

/** True when a bridge op cannot change the game: no queue slot needed. */
export function isReadOnlyOp(op, args = {}) {
  if (READ_ONLY_OPS.has(op)) return true;
  const a = args && typeof args === "object" ? args : {};
  if (op === "mods_ui") {
    const action = String(a.action ?? "status");
    const sub = String(a.op ?? "read").trim().toLowerCase();
    if (MODS_UI_READS.has(action)) return true;
    if (action === "restart") return sub === "read";
    if (action === "keys") return ["read", "conflict", "conflict-read"].includes(sub);
    if (action === "menu") return a.op === "status";
    return false;
  }
  if (READ_ONLY_ACTIONS.has(a.action)) return true;
  if (a.action === undefined && STATUS_BY_DEFAULT.has(op)) return true;
  if (a.statusOnly === true) return true;
  if (op === "store" && a.value === undefined) return true;
  return false;
}

export function queueFile(env = process.env) {
  return env.PROGENITOR_QUEUE_FILE || workspacePath("_build", "bridge-queue.json");
}

/** Gating is on unless PROGENITOR_QUEUE=off (an escape hatch for a single-agent machine). */
export const gatingEnabled = (env = process.env) => String(env.PROGENITOR_QUEUE ?? "").toLowerCase() !== "off";

const clip = (s, n = MAX_TEXT) => {
  const t = String(s ?? "").replace(/[\u0000-\u001f\u007f]/g, " ").trim();
  return t.length > n ? t.slice(0, n) : t;
};

/**
 * Queue key of a session: its session ID, else ai + name, plus "mcp" and the pid for a long-lived MCP
 * process. Scripts of one unidentified session (call.mjs, live rig, queue CLI) share ai + name.
 */
export function keyOf(who) {
  if (who?.id) return `id:${who.id}`;
  const mcp = who?.tool === "mcp" ? `|mcp|${who.pid ?? ""}` : "";
  return `${who?.ai ?? "unknown"}|${who?.name ?? ""}${mcp}`;
}

/**
 * For scripts only: may this caller be the holder itself under another key? The same AI kind with a
 * session ID missing on one side (Codex sets its thread ID for shell commands but not for the MCP
 * server it starts). Not provable, so scripts get the benefit of the doubt; MCP does not.
 */
function maybeSameSession(holder, who) {
  const h = holder?.who ?? {};
  return !!who?.ai && who.ai !== "unknown" && h.ai === who.ai && (!h.id || !who.id);
}

/** How another session can reach this one, from what the environment says about the host app. */
export function defaultContact(who, env = process.env) {
  const parts = [];
  if (who?.ai === "claude" && env.CLAUDE_CODE_HOST_SESSION_ID) parts.push(`Claude desktop session ${env.CLAUDE_CODE_HOST_SESSION_ID} (session send_message)`);
  else if (who?.ai === "claude" && who.id) parts.push(`Claude Code session ${who.id}`);
  else if (who?.ai === "codex" && who.id) parts.push(`Codex thread ${who.id}`);
  else if (who?.ai === "grok" && who.id) parts.push(`Grok session ${who.id}`);
  else if (who?.id) parts.push(`session ${who.id}`);
  return parts.join("; ");
}

export function normalizeSides(sides) {
  const list = (Array.isArray(sides) ? sides : String(sides ?? "").split(/[,\s+]+/))
    .map((s) => String(s).trim().toLowerCase()).filter(Boolean);
  const expanded = list.flatMap((s) => (s === "both" ? SIDES : [s]));
  const bad = expanded.filter((s) => !SIDES.includes(s));
  if (bad.length) throw new Error(`unknown side ${bad.join(", ")}; use client, server or both`);
  const out = SIDES.filter((s) => expanded.includes(s));
  if (!out.length) throw new Error("name at least one side: client, server or both");
  return out;
}

/** [{name, version}] from an array of objects or "name@version" strings, or a comma list. */
export function normalizeMods(mods) {
  if (mods == null) return null;
  const list = Array.isArray(mods) ? mods : String(mods).split(",").map((s) => s.trim()).filter(Boolean);
  return list.map((m) => {
    if (m && typeof m === "object") {
      if (!m.name || !m.version) throw new Error("each mod needs a name and a version");
      return { name: clip(m.name, 80), version: clip(m.version, 60) };
    }
    const s = String(m);
    const at = s.lastIndexOf("@");
    if (at <= 0 || at === s.length - 1) throw new Error(`mod '${s}' needs a version: name@version`);
    return { name: clip(s.slice(0, at), 80), version: clip(s.slice(at + 1), 60) };
  });
}

function emptyState() { return { version: 1, entries: [], events: [], observed: {}, history: [], messages: [] }; }

function readState(file) {
  try {
    const s = JSON.parse(fs.readFileSync(file, "utf8"));
    if (s && Array.isArray(s.entries)) {
      return {
        version: 1, entries: s.entries, events: Array.isArray(s.events) ? s.events : [],
        observed: s.observed && typeof s.observed === "object" ? s.observed : {},
        history: Array.isArray(s.history) ? s.history : [],
        messages: Array.isArray(s.messages) ? s.messages : [],
        overrideNotices: s.overrideNotices && typeof s.overrideNotices === "object" ? s.overrideNotices : {},
      };
    }
  } catch { /* missing or corrupt: start empty */ }
  return emptyState();
}

function writeState(file, state) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(state, null, 2));
  // Windows refuses a rename over a file another process (an editor, antivirus) has open: retry briefly.
  for (let i = 0; ; i++) {
    try { fs.renameSync(tmp, file); return; }
    catch (e) {
      if (i >= 10 || !["EPERM", "EBUSY", "EACCES"].includes(e.code)) { try { fs.unlinkSync(tmp); } catch { /* ignore */ } throw e; }
      sleepSync(20);
    }
  }
}

const sleepSync = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
const LOCK_STALE_MS = 3000;
const LOCK_WAIT_MS = 5000;

/**
 * Runs fn under an exclusive lock file holding a per-call token. A queue transaction takes
 * milliseconds, so a lock older than LOCK_STALE_MS belongs to a crashed process; it is taken over by
 * renaming it away (atomic: one taker wins). Release removes the lock only while it holds our token.
 */
function withLock(file, fn, waitMs = LOCK_WAIT_MS) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const lock = `${file}.lock`;
  const token = `${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const deadline = Date.now() + waitMs;
  for (;;) {
    try { fs.writeFileSync(lock, token, { flag: "wx" }); break; }
    catch (e) {
      if (e.code !== "EEXIST") throw e;
      try {
        if (Date.now() - fs.statSync(lock).mtimeMs > LOCK_STALE_MS) {
          const aside = `${lock}.${token}.stale`;
          fs.renameSync(lock, aside);
          fs.unlinkSync(aside);
          continue;
        }
      } catch { continue; /* another taker won, or the owner released */ }
      if (Date.now() > deadline) throw new Error(`bridge queue is locked (${lock}); retry in a moment`);
      sleepSync(25);
    }
  }
  try { return fn(); }
  finally {
    try { if (fs.readFileSync(lock, "utf8") === token) fs.unlinkSync(lock); } catch { /* already gone */ }
  }
}

export function pidAlive(pid) {
  if (!pid) return false;
  try { process.kill(pid, 0); return true; } catch (e) { return e.code === "EPERM"; }
}

const overlaps = (a, b) => a.sides.some((s) => b.sides.includes(s));
const iso = (ms) => new Date(ms).toISOString();
const ms = (t) => Date.parse(t);

function event(state, now, text) {
  state.events.push({ at: iso(now), text: clip(text, 300) });
  if (state.events.length > MAX_EVENTS) state.events.splice(0, state.events.length - MAX_EVENTS);
}

// Connection history ------------------------------------------------------------------------------
// One record per hold (a waiter that becomes active), newest last, at most MAX_HISTORY. Each record
// lists the builds seen loaded on its sides while it held them, so agents can see which Core every
// earlier session ran and when it was swapped.

const newId = (now) => `${now.toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

function startConnection(state, e, now) {
  const id = newId(now);
  const rec = {
    id, who: { ...e.who }, sides: [...e.sides], startedAt: iso(now), endedAt: null, end: null,
    estimatedMinutes: e.estimatedMinutes, purpose: e.purpose ?? "", declared: { progenitor: e.progenitor, mods: e.mods ?? [] },
    builds: e.sides.filter((side) => state.observed?.[side]).map((side) => ({ at: iso(now), side, build: buildLine(state.observed[side]), change: "loaded at start" })),
  };
  state.history.push(rec);
  if (state.history.length > MAX_HISTORY) state.history.splice(0, state.history.length - MAX_HISTORY);
  e.historyId = id;
}

function endConnection(state, e, now, end) {
  const rec = e.historyId && state.history.find((h) => h.id === e.historyId);
  if (!rec || rec.endedAt) return;
  rec.endedAt = iso(now);
  rec.end = end;
  rec.declared = { progenitor: e.progenitor, mods: e.mods ?? [] };
}

// Mailbox helpers ----------------------------------------------------------------------------------

/**
 * Has the recipient written back to the sender after reading `m`? Log order, so same-millisecond
 * replies count; a message that crossed `m` (sent before it was read) is not an answer.
 */
export function answered(state, m) {
  if (!m.readAt) return false;
  const list = state.messages ?? [];
  const i = list.findIndex((x) => x === m || (m.id && x.id === m.id));
  if (i < 0) return false;
  return list.some((r, j) => j > i && !r.system && r.fromKey === m.toKey && r.toKey === m.fromKey && ms(r.at) >= ms(m.readAt));
}

/** True when `key` has sent messages that are still open (no answer, not retracted). */
export function hasOpenSent(state, key) {
  return (state.messages ?? []).some((m) => m.fromKey === key && isOpen(state, m));
}

/** Open: a real message, not retracted, and its recipient has not written back to its sender since. */
export function isOpen(state, m) {
  if (m.system || m.noReply || m.retractedAt) return false;
  return !answered(state, m);
}

/**
 * Retracts every open message `fromKey` sent. An undelivered one is never shown; a delivered one
 * leaves its recipient a one-line notice, so nobody acts on a request nobody is waiting for any more.
 * Returns how many were retracted.
 */
export function retractOpen(state, fromKey, now, reason) {
  state.messages ??= [];
  let n = 0;
  for (const m of [...state.messages]) {
    if (m.fromKey !== fromKey || !isOpen(state, m)) continue;
    m.retractedAt = iso(now);
    m.retractReason = reason;
    n++;
    if (m.readAt && state.entries.some((e) => e.key === m.toKey)) {
      state.messages.push({
        id: `${now.toString(36)}-${Math.random().toString(36).slice(2, 8)}`, at: iso(now), system: true,
        fromKey, fromName: m.fromName, toKey: m.toKey, toName: m.toName, readAt: null,
        text: `${m.fromName} retracted its message "${m.text}" (${reason}); no reply is expected`,
      });
    }
  }
  return n;
}

/**
 * Drops dead and idle entries and promotes waiters. Mutates and returns state.
 * @param {{now:number, alive:(pid:number)=>boolean, host:string}} o
 */
export function normalize(state, { now, alive = pidAlive, host = os.hostname() }) {
  const liveProcess = (e) => e.pid && e.host === host && alive(e.pid);
  const processGone = (e) => e.pid && e.host === host && !alive(e.pid);
  const keep = [];
  for (const e of state.entries) {
    const idle = now - ms(e.lastActivityAt);
    if (processGone(e)) {
      event(state, now, `${describeUser(e.who)} left the ${e.sides.join("+")} queue: its process ${e.pid} exited`);
      if (e.state === "active") endConnection(state, e, now, "process exited");
      retractOpen(state, e.key, now, "its session exited");
      continue;
    }
    if (e.state === "waiting" && !liveProcess(e) && idle >= WAITER_STALE_MS) {
      event(state, now, `${describeUser(e.who)} dropped from the ${e.sides.join("+")} queue: no check-in for ${Math.round(idle / 60_000)} min`);
      retractOpen(state, e.key, now, "it stopped checking in");
      continue;
    }
    if (e.state === "active") {
      const waiting = state.entries.some((w) => w.state === "waiting" && overlaps(w, e));
      if (idle >= IDLE_RELEASE_MS || (waiting && idle >= IDLE_RELEASE_WAITING_MS)) {
        event(state, now, `${describeUser(e.who)} released the ${e.sides.join("+")} bridge: idle ${Math.round(idle / 60_000)} min${waiting ? " with sessions waiting" : ""}`);
        endConnection(state, e, now, `idle ${Math.round(idle / 60_000)} min`);
        retractOpen(state, e.key, now, "it was released for being idle");
        continue;
      }
    }
    keep.push(e);
  }
  state.entries = keep;
  state.messages = (state.messages ?? []).filter((m) => now - ms(m.at) < MESSAGE_KEEP_MS).slice(-MAX_MESSAGES);
  const blocked = new Set();
  for (const e of state.entries) if (e.state === "active") e.sides.forEach((s) => blocked.add(s));
  for (const e of state.entries) {
    if (e.state !== "waiting") continue;
    if (!e.sides.some((s) => blocked.has(s))) {
      e.state = "active";
      e.startedAt = iso(now);
      e.probes = [];
      event(state, now, `${describeUser(e.who)} now holds the ${e.sides.join("+")} bridge (est. ${e.estimatedMinutes} min)`);
      startConnection(state, e, now);
    }
    e.sides.forEach((s) => blocked.add(s));
  }
  return state;
}

/** Expected start and end of every entry, plus waiter positions and who may contact which holder. */
export function schedule(state, now) {
  const free = Object.fromEntries(SIDES.map((s) => [s, now]));
  const rows = [];
  let position = 0;
  for (const e of state.entries) {
    let startAt, endAt;
    if (e.state === "active") {
      startAt = ms(e.startedAt);
      endAt = startAt + e.estimatedMinutes * 60_000;
      const busyUntil = Math.max(endAt, now);
      e.sides.forEach((s) => (free[s] = Math.max(free[s], busyUntil)));
    } else {
      startAt = Math.max(now, ...e.sides.map((s) => free[s]));
      endAt = startAt + e.estimatedMinutes * 60_000;
      e.sides.forEach((s) => (free[s] = endAt));
    }
    rows.push({ entry: e, startAt, endAt, position: e.state === "waiting" ? ++position : 0, mayContact: [] });
  }
  for (const h of rows.filter((r) => r.entry.state === "active")) {
    rows.filter((r) => r.entry.state === "waiting" && overlaps(r.entry, h.entry))
      .slice(0, MAX_CONTACTS)
      .forEach((r) => r.mayContact.push(h.entry.key));
  }
  return rows;
}

const shortSha = (sha) => (sha ? String(sha).slice(0, 8) : "?");
const img = (i) => `${i.name} ${i.version ?? "?"}`;

/** What a ping says is loaded on one side: Core, Bootstrap and reloaded plugins with versions and hashes. */
export function observationFromPing(ping, at, by) {
  const images = Array.isArray(ping?.images) ? ping.images : [];
  const pick = (name) => images.find((i) => i?.name === name);
  const core = pick("Core");
  const boot = pick("Bootstrap");
  return {
    at: iso(at), by,
    progenitor: clip(ping?.progenitor ?? "", 80),
    core: core ? { version: clip(core.version ?? "", 80) || null, sha: core.sha256 ?? null, generation: core.generation ?? ping?.reload?.generation ?? null } : null,
    bootstrap: boot ? { version: clip(boot.version ?? "", 80) || null, sha: boot.sha256 ?? null } : null,
    plugins: images.filter((i) => i?.name && i.name !== "Core" && i.name !== "Bootstrap")
      .map((i) => ({ name: clip(i.name, 80), version: clip(i.version ?? "", 80) || null, sha: i.sha256 ?? null })),
  };
}

/**
 * "Core 2026.10.3+abc1234 gen 32 (sha 1a2b3c4d)". ping's top-level `progenitor` is not used as the
 * Core version: it is compiled into the Dev bridge and can differ from the Core actually loaded.
 */
export function coreLine(o) {
  if (!o?.core) return "Core unknown (no image data: the Dev bridge predates image hashes)";
  const version = o.core.version ?? "version not reported (Dev bridge older than 2026.10.171)";
  return `Core ${version} gen ${o.core.generation ?? "?"} (sha ${shortSha(o.core.sha)})`;
}

/** One-line summary of everything loaded. */
export function buildLine(o) {
  const parts = [coreLine(o)];
  if (o?.bootstrap) parts.push(`Bootstrap ${o.bootstrap.version ?? "?"}`);
  if (o?.plugins?.length) parts.push(`plugins: ${o.plugins.map(img).join(", ")}`);
  return parts.join("; ");
}

/** What changed between two observations of one side, or "" when nothing did. */
export function describeChange(prev, next) {
  if (!prev) return "first seen";
  const bits = [];
  const pc = prev.core ?? {}, nc = next.core ?? {};
  if (pc.sha !== nc.sha || pc.generation !== nc.generation) {
    bits.push(`Core ${pc.version ?? "?"} gen ${pc.generation ?? "?"} -> ${nc.version ?? "?"} gen ${nc.generation ?? "?"}`);
  }
  if ((prev.bootstrap?.sha ?? null) !== (next.bootstrap?.sha ?? null)) bits.push(`Bootstrap ${prev.bootstrap?.version ?? "?"} -> ${next.bootstrap?.version ?? "?"}`);
  const before = new Map((prev.plugins ?? []).map((x) => [x.name, x]));
  const after = new Map((next.plugins ?? []).map((x) => [x.name, x]));
  for (const [n, x] of after) {
    const b = before.get(n);
    if (!b) bits.push(`+${img(x)}`);
    else if (b.sha !== x.sha || b.version !== x.version) bits.push(`${n} ${b.version ?? "?"} -> ${x.version ?? "?"}`);
  }
  for (const [n, b] of before) if (!after.has(n)) bits.push(`-${img(b)}`);
  return bits.join("; ");
}

/** The revision part of a stamped version ("2026.10.3+abc1234.dirty" -> {rev:"abc1234", dirty:true}). */
export function revisionOf(version) {
  const m = /\+([0-9a-f]{7,40})(\.dirty)?$/i.exec(String(version ?? ""));
  return m ? { rev: m[1], dirty: !!m[2] } : null;
}

/**
 * Does a declared version name this build revision? The build stamps a 10-character sha; agents
 * usually declare 7 to 9 (git rev-parse --short), so any hex run of 7+ that is a prefix either way matches.
 */
export function declaresRevision(declared, rev) {
  const r = String(rev ?? "").toLowerCase();
  if (!r) return false;
  return (String(declared ?? "").toLowerCase().match(/[0-9a-f]{7,40}/g) ?? []).some((h) => h.startsWith(r) || r.startsWith(h));
}

export function holderOf(state, side) {
  return state.entries.find((e) => e.state === "active" && e.sides.includes(side)) ?? null;
}

const minutes = (msv) => Math.max(0, Math.round(msv / 60_000));
const clock = (t) => new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
const ago = (msv) => (msv < 60_000 ? `${Math.max(0, Math.round(msv / 1000))} s ago` : `${minutes(msv)} min ago`);

export function versionsLine(e) {
  const mods = e.mods?.length ? e.mods.map((m) => `${m.name} ${m.version}`).join(", ") : "none";
  return `Progenitor ${e.progenitor}; mods: ${mods}`;
}

export function reachLine(e, now, { alive = pidAlive, host = os.hostname() } = {}) {
  const proc = e.pid
    ? (e.host === host ? `MCP pid ${e.pid} ${alive(e.pid) ? "alive" : "gone"} on ${e.host}` : `pid ${e.pid} on ${e.host} (another machine)`)
    : `${e.who?.tool || "cli"} (no long-lived process)`;
  return `${proc}; last activity ${ago(now - ms(e.lastActivityAt))}${e.contact ? `; reach: ${e.contact}` : ""}`;
}

/**
 * The latest message from each sender to holder `e` that the holder has not answered for
 * UNANSWERED_MS although it has been active since (so silence is a choice, not an absence).
 */
export function unanswered(state, e, now) {
  const latest = new Map();
  for (const m of state.messages ?? []) if (!m.system && !m.noReply && m.toKey === e.key && ms(m.at) >= ms(e.startedAt)) latest.set(m.fromKey, m);
  return [...latest.values()].filter((m) => !m.retractedAt && now - ms(m.at) >= UNANSWERED_MS
    && ms(e.lastActivityAt) > ms(m.at)
    && !answered(state, m));
}

/** Human-readable queue for the agent, from `me`'s point of view. */
export function formatQueue(state, now, meKey, opts = {}) {
  const rows = schedule(state, now);
  const lines = [`Bridge queue at ${clock(now)} (${rows.length ? `${rows.filter((r) => r.position).length} waiting` : "empty: both sides free"})`];
  const seen = SIDES.filter((side) => state.observed?.[side]);
  if (seen.length) lines.push("LOADED NOW (last seen through a ping on that side)");
  for (const side of seen) {
    const o = state.observed[side];
    lines.push(`  ${side}: ${coreLine(o)}; seen ${ago(now - ms(o.at))}${o.by ? ` via ${o.by}` : ""}`);
    const rel = o.core?.version && opts.relate ? opts.relate(o.core.version) : "";
    if (rel) lines.push(`    ${rel}`);
    if (o.bootstrap) lines.push(`    Bootstrap ${o.bootstrap.version ?? "?"} (sha ${shortSha(o.bootstrap.sha)})`);
    if (o.plugins?.length) lines.push(`    plugins: ${o.plugins.map(img).join(", ")}`);
  }
  for (const r of rows.filter((x) => x.entry.state === "active")) {
    const e = r.entry;
    const left = r.endAt - now;
    const timing = left >= 0
      ? `running ${minutes(now - r.startAt)} min of est. ${e.estimatedMinutes} (about ${minutes(left)} min left, until ${clock(r.endAt)})`
      : `running ${minutes(now - r.startAt)} min, OVER its est. ${e.estimatedMinutes} min by ${minutes(-left)} min`;
    lines.push(`ACTIVE ${e.sides.join("+")}: ${describeUser(e.who)}${e.key === meKey ? " (you)" : ""}${e.purpose ? ` - ${e.purpose}` : ""}`);
    lines.push(`  ${timing}`);
    lines.push(`  versions: ${versionsLine(e)}`);
    for (const side of e.sides) {
      const o = state.observed?.[side];
      const r = revisionOf(o?.core?.version);
      if (r && !declaresRevision(e.progenitor, r.rev) && !String(e.progenitor).includes(o.core.version)) {
        lines.push(`  note: declared Progenitor ${e.progenitor}, but the ${side} runs ${o.core.version}`);
      }
    }
    lines.push(`  ${reachLine(e, now, opts)}`);
    for (const p of e.probes ?? []) lines.push(`  checked by ${p.by} ${ago(now - ms(p.at))}${p.note ? `: ${p.note}` : ""}`);
    for (const o of e.overrides ?? []) lines.push(`  OVERRIDE: ${o.by} last ran '${o.op}' on the ${o.side} under PROGENITOR_QUEUE=off ${ago(now - ms(o.at))}, while this session holds it`);
    const since = ms(e.startedAt);
    const mail = (state.messages ?? []).filter((m) => !m.system && ms(m.at) >= since && (m.toKey === e.key || m.fromKey === e.key));
    for (const m of mail.slice(-6)) {
      const dir = m.fromKey === e.key ? `to ${m.toName}` : `from ${m.fromName}`;
      const st = m.retractedAt ? `retracted ${ago(now - ms(m.retractedAt))}: ${m.retractReason}` : m.readAt ? `delivered ${ago(now - ms(m.readAt))}` : "not delivered yet";
      lines.push(`  message ${dir} ${ago(now - ms(m.at))}: "${m.text}" (${st})`);
    }
    for (const u of unanswered(state, e, now)) {
      lines.push(`  UNANSWERED for ${minutes(now - ms(u.at))} min: ${u.fromName}'s message, while the holder was active ${ago(now - ms(e.lastActivityAt))}`);
    }
  }
  const waiting = rows.filter((x) => x.position);
  if (waiting.length) lines.push("WAITING");
  for (const r of waiting) {
    const e = r.entry;
    const contact = r.mayContact.length ? "may check on the holder (probe)" : "do not contact the holder; wait";
    lines.push(`  ${r.position}. ${e.sides.join("+")}: ${describeUser(e.who)}${e.key === meKey ? " (you)" : ""}${e.purpose ? ` - ${e.purpose}` : ""}`);
    lines.push(`     est. ${e.estimatedMinutes} min; starts ~${clock(r.startAt)} (in ${minutes(r.startAt - now)} min), done ~${clock(r.endAt)}; ${contact}`);
    lines.push(`     versions: ${versionsLine(e)}`);
  }
  if (opts.events && state.events.length) {
    lines.push("Recent:");
    for (const ev of state.events.slice(-5)) lines.push(`  ${clock(ms(ev.at))} ${ev.text}`);
  }
  return lines.join("\n");
}

/**
 * The queue for one workspace file. Every operation locks, reads, normalizes, acts and writes.
 * `key` fixes the caller's queue key (MCP: chosen once per process, so a later identity override or a
 * session title change cannot strand its own slot).
 * `relate` turns a loaded Core version into a note about the caller's own branch (MCP: git).
 * @param {{file?:string, now?:()=>number, alive?:(pid:number)=>boolean, host?:string, env?:object, key?:(who:object)=>string, relate?:(version:string)=>string}} o
 */
export function createQueue({ file = queueFile(), now = () => Date.now(), alive = pidAlive, host = os.hostname(), env = process.env, key = keyOf, relate = null } = {}) {
  const opts = { alive, host };
  // A script the MCP server starts (deploy.ps1 -> call.mjs) acts for the MCP session that started it.
  const keyFor = (who) => env.PROGENITOR_QUEUE_KEY || key(who);
  const lastTouch = new Map();

  /** Lock, read, normalize, act, write. `fn` may set ctx.write = false when it changed nothing. */
  function transact(fn, { waitMs = LOCK_WAIT_MS } = {}) {
    if (!file) throw new Error("cannot resolve the bridge queue file; set PROGENITOR_QUEUE_FILE or PROGENITOR_WORKSPACE");
    return withLock(file, () => {
      const t = now();
      const state = normalize(readState(file), { now: t, ...opts });
      const ctx = { write: true };
      const out = fn(state, t, ctx);
      if (ctx.write) writeState(file, state);
      return out;
    }, waitMs);
  }

  /**
   * The caller's entry. A script (call.mjs, queue CLI) whose own key has no entry falls back to the one
   * entry of the same AI kind that maybeSameSession accepts (Codex sets its thread ID for shell
   * commands but not for the MCP server it starts), so its mail and activity still reach that entry.
   */
  function find(state, who) {
    const key = keyFor(who);
    const own = state.entries.find((e) => e.key === key);
    if (own || who?.tool === "mcp" || env.PROGENITOR_QUEUE_KEY) return own ?? null;
    const same = state.entries.filter((e) => maybeSameSession(e, who));
    return same.length === 1 ? same[0] : null;
  }
  /** Mailbox key of the caller: its entry's key when it has one. */
  const mailKey = (state, who) => find(state, who)?.key ?? keyFor(who);

  function touch(state, who, t) {
    const e = find(state, who);
    if (e) e.lastActivityAt = iso(t);
    return e;
  }

  /** Waiting-reminder text for a holder ("" when nothing to say). */
  function reminderText(state, me, t) {
    if (!me || me.state !== "active") return "";
    const over = t - (ms(me.startedAt) + me.estimatedMinutes * 60_000);
    const waiters = state.entries.filter((e) => e.state === "waiting" && overlaps(e, me));
    const bits = [];
    if (over > 0) bits.push(`your estimate ran out ${minutes(over)} min ago (update estimated_minutes)`);
    if (waiters.length) {
      const needs = waiters.map((w) => `${describeUser(w.who)} needs ${w.sides.join("+")} for ${w.estimatedMinutes} min`).join("; ");
      bits.push(`${waiters.length} waiting: ${needs}`);
    }
    if (!bits.length) return "";
    const partial = waiters.length && waiters.every((w) => w.sides.length < me.sides.length);
    return `Queue: ${bits.join("; ")}. Leave as soon as your live work is done${partial ? ", or release just the side they need (release)" : ""}.`;
  }

  /**
   * Unread messages for the caller, marked delivered, plus the holder reminder when one is due
   * (at most every REMIND_EVERY_MS). Mutates state; call inside a transaction.
   */
  function deliver(state, who, t) {
    const key = mailKey(state, who);
    const out = [];
    let real = 0;
    for (const m of state.messages ?? []) {
      if (m.toKey !== key || m.readAt || m.retractedAt) continue;
      m.readAt = iso(t);
      if (m.system) { out.push(`Queue notice (${clock(ms(m.at))}): ${m.text}`); continue; }
      if (!m.noReply) real++;
      out.push(`Queue ${m.noReply ? "notice" : "message"} from ${m.fromName} (${clock(ms(m.at))}): "${m.text}"${m.noReply ? " (no reply needed)" : ""}`);
    }
    if (real) out.push("Reply with progenitor_queue action=message text=\"...\" (CLI: node tools/mcp/queue.mjs message --text \"...\").");
    const me = find(state, who);
    const rem = reminderText(state, me, t);
    if (rem && (out.length || t - ms(me.remindedAt ?? 0) >= REMIND_EVERY_MS)) { me.remindedAt = iso(t); out.push(rem); }
    return out.join("\n");
  }

  function view(state, t, who, extra = "") {
    const me = find(state, who);
    const mail = deliver(state, who, t);
    if (mail) extra = extra ? `${extra}\n${mail}` : mail;
    let head = "";
    if (me?.state === "active") head = `You hold the ${me.sides.join("+")} bridge. Leave the queue (progenitor_queue leave) as soon as your live work is done.\n`;
    else if (me) head = `You are waiting (position ${schedule(state, t).find((r) => r.entry === me)?.position}).\n`;
    return `${extra ? extra + "\n" : ""}${head}${formatQueue(state, t, keyFor(who), { ...opts, relate, events: true })}`;
  }

  return {
    file,
    status(who) {
      return transact((state, t) => { touch(state, who, t); return view(state, t, who); });
    },
    /**
     * Records what a ping says is loaded on `side`. A change is added to the open connection of the
     * session holding that side, or, with no holder, as its own record for the caller. A ping from an
     * extra or hosted instance (non-empty `instance`) is not the side's main process and is ignored.
     * Writes only on a change or once a minute, and skips rather than waits when the queue is locked
     * (deploy.ps1 pings about once a second during a Core swap). Never throws.
     */
    observe(who, side, ping) {
      if (!SIDES.includes(side) || !ping || typeof ping !== "object" || ping.instance) return;
      try {
        transact((state, t, ctx) => {
          const next = observationFromPing(ping, t, describeUser(who));
          const prev = state.observed[side] ?? null;
          const change = describeChange(prev, next);
          if (!change && prev && t - ms(prev.at) < OBSERVE_REFRESH_MS) { ctx.write = false; return; }
          state.observed[side] = next;
          if (!change) return;
          const holder = holderOf(state, side);
          let rec = holder?.historyId && state.history.find((h) => h.id === holder.historyId && !h.endedAt);
          if (holder && !rec) {
            // A hold older than its history record (trimmed, or from before history existed).
            startConnection(state, holder, t);
            rec = state.history.at(-1);
          }
          if (!rec) {
            rec = { id: newId(t), who: { ...who }, sides: [side], startedAt: iso(t), endedAt: iso(t), end: "outside the queue", estimatedMinutes: 0, purpose: "", declared: null, builds: [] };
            state.history.push(rec);
            if (state.history.length > MAX_HISTORY) state.history.splice(0, state.history.length - MAX_HISTORY);
          }
          rec.builds.push({ at: iso(t), side, build: buildLine(next), change, by: describeUser(who) });
          if (rec.builds.length > 20) rec.builds.splice(0, rec.builds.length - 20);
          event(state, t, `${side}: ${change} (seen via ${describeUser(who)})`);
        }, { waitMs: 0 });
      } catch { /* observation is best effort */ }
    },
    /** The last `count` connections (default 10, at most MAX_HISTORY), newest first. */
    history(who, count = 10) {
      return transact((state, t) => {
        touch(state, who, t);
        const n = Math.max(1, Math.min(MAX_HISTORY, Number(count) || 10));
        const recs = state.history.slice(-n).reverse();
        if (!recs.length) return "No connections recorded yet.";
        const out = [`Last ${recs.length} of ${state.history.length} connections (newest first; the queue keeps ${MAX_HISTORY})`];
        for (const h of recs) {
          const end = h.endedAt ? `${clock(ms(h.endedAt))} (${h.end})` : "now (holding)";
          const dur = minutes((h.endedAt ? ms(h.endedAt) : t) - ms(h.startedAt));
          const rel = (h.released ?? []).map((x) => ` (${x.sides.join("+")} released ${clock(ms(x.at))})`).join("");
          out.push(`${clock(ms(h.startedAt))}-${end} ${h.sides.join("+")}${rel}: ${describeUser(h.who)}${h.purpose ? ` - ${h.purpose}` : ""}, ${dur} min${h.estimatedMinutes ? ` of est. ${h.estimatedMinutes}` : ""}`);
          if (h.declared) out.push(`  declared: ${versionsLine(h.declared)}`);
          for (const b of h.builds ?? []) out.push(`  ${clock(ms(b.at))} ${b.side}: ${b.change}${!b.build ? "" : b.change === "loaded at start" || b.change === "first seen" ? `: ${b.build}` : `; now ${b.build}`}`);
        }
        return out.join("\n");
      });
    },
    /** Raw normalized state (tests and gating). */
    snapshot() { return transact((state) => JSON.parse(JSON.stringify(state))); },
    join(who, { sides, estimatedMinutes, purpose, progenitor, mods, contact }) {
      const s = normalizeSides(sides);
      const est = Number(estimatedMinutes);
      if (!Number.isFinite(est) || est < 1 || est > 480) throw new Error("estimated_minutes must be 1 to 480: how long your live work on the bridge will take");
      if (!clip(progenitor)) throw new Error("progenitor_version is required: the Progenitor version or commit you will run (e.g. 0.1.0+abc1234)");
      const m = normalizeMods(mods);
      if (m == null) throw new Error("mods is required: every mod you will run as name@version, or an empty list");
      return transact((state, t) => {
        let e = find(state, who);
        const fields = {
          sides: s, estimatedMinutes: Math.round(est), purpose: clip(purpose), progenitor: clip(progenitor, 80), mods: m,
          contact: clip(contact) || defaultContact(who, env), lastActivityAt: iso(t),
        };
        if (e && e.state === "active" && s.some((x) => !e.sides.includes(x))) {
          // Widening an active claim means waiting for the new side like everyone else.
          state.entries.splice(state.entries.indexOf(e), 1);
          event(state, t, `${describeUser(who)} gave up the ${e.sides.join("+")} bridge to re-queue for ${s.join("+")}`);
          endConnection(state, e, t, `re-queued for ${s.join("+")}`);
          e = null;
        }
        // An MCP process re-joining an entry its CLI made gives it a process to watch.
        if (e) Object.assign(e, fields, { who: { ...who } }, who.tool === "mcp" ? { pid: who.pid, host } : {});
        else {
          state.entries.push({ key: keyFor(who), who: { ...who }, pid: who.tool === "mcp" ? who.pid : null, host, state: "waiting", joinedAt: iso(t), startedAt: null, probes: [], ...fields });
          event(state, t, `${describeUser(who)} joined the queue for ${s.join("+")} (est. ${fields.estimatedMinutes} min)`);
        }
        normalize(state, { now: t, ...opts });
        return view(state, t, who);
      });
    },
    update(who, { estimatedMinutes, purpose, progenitor, mods, contact }) {
      return transact((state, t) => {
        const e = touch(state, who, t);
        if (!e) throw new Error("you are not in the bridge queue; join first");
        if (estimatedMinutes != null) {
          const est = Number(estimatedMinutes);
          if (!Number.isFinite(est) || est < 1 || est > 480) throw new Error("estimated_minutes must be 1 to 480");
          // For a holder the estimate counts from now: "I need N more minutes".
          e.estimatedMinutes = Math.round(e.state === "active" ? (t - ms(e.startedAt)) / 60_000 + est : est);
        }
        if (purpose != null) e.purpose = clip(purpose);
        if (progenitor != null) { if (!clip(progenitor)) throw new Error("progenitor_version cannot be empty"); e.progenitor = clip(progenitor, 80); }
        if (mods != null) e.mods = normalizeMods(mods);
        if (contact != null) e.contact = clip(contact) || defaultContact(who, env);
        const rec = e.historyId && state.history.find((h) => h.id === e.historyId);
        if (rec && (progenitor != null || mods != null)) rec.declared = { progenitor: e.progenitor, mods: e.mods ?? [] };
        return view(state, t, who);
      });
    },
    leave(who) {
      return transact((state, t) => {
        const e = find(state, who);
        if (!e) return view(state, t, who, "You were not in the bridge queue.");
        state.entries.splice(state.entries.indexOf(e), 1);
        event(state, t, `${describeUser(who)} ${e.state === "active" ? "released the " + e.sides.join("+") + " bridge" : "left the queue"}`);
        if (e.state === "active") endConnection(state, e, t, "left");
        retractOpen(state, e.key, t, "it left the queue");
        normalize(state, { now: t, ...opts });
        return view(state, t, who, e.state === "active" ? "Released." : "Left the queue.");
      });
    },
    /** Next-two waiters only: check whether the holder of the caller's sides is really still working. */
    probe(who, note) {
      return transact((state, t) => {
        const me = touch(state, who, t);
        if (!me || me.state !== "waiting") throw new Error("only a waiting session can probe the holder");
        const rows = schedule(state, t);
        const mine = rows.find((r) => r.entry === me);
        if (!mine.mayContact.length) throw new Error(`you are position ${mine.position}; only the next ${MAX_CONTACTS} waiters for a holder may contact it. Wait for your turn.`);
        const out = [];
        for (const key of mine.mayContact) {
          const h = state.entries.find((e) => e.key === key);
          const r = rows.find((x) => x.entry === h);
          h.probes = [...(h.probes ?? []).slice(-2), { by: describeUser(who), at: iso(t), note: clip(note, 120) }];
          const idle = t - ms(h.lastActivityAt);
          const over = t - r.endAt;
          const evictable = idle >= EVICT_IDLE_MS && over > 0;
          out.push([
            `Holder of ${h.sides.join("+")}: ${describeUser(h.who)}`,
            `  ${reachLine(h, t, opts)}`,
            `  ${over > 0 ? `over its estimate by ${minutes(over)} min` : `about ${minutes(-over)} min left of its estimate`}; idle ${minutes(idle)} min`,
            h.contact ? `  To ask whether it is done, message it at: ${h.contact}` : "  It left no address beyond its process; its activity is the signal.",
            evictable ? `  It is idle for ${minutes(idle)} min and past its estimate: you may evict it (progenitor_queue evict, with a reason).` : "  Not evictable: it is active or still inside its estimate.",
          ].join("\n"));
        }
        return out.join("\n");
      });
    },
    /** Next-two waiters only: release a holder that is past its estimate and idle for EVICT_IDLE_MS. */
    evict(who, reason) {
      if (!clip(reason)) throw new Error("evict needs a reason (what the probe showed)");
      return transact((state, t) => {
        const me = touch(state, who, t);
        if (!me || me.state !== "waiting") throw new Error("only a waiting session can evict the holder");
        const rows = schedule(state, t);
        const mine = rows.find((r) => r.entry === me);
        if (!mine.mayContact.length) throw new Error(`you are position ${mine.position}; only the next ${MAX_CONTACTS} waiters may evict a holder`);
        const evicted = [];
        for (const key of mine.mayContact) {
          const h = state.entries.find((e) => e.key === key);
          const r = rows.find((x) => x.entry === h);
          const idle = t - ms(h.lastActivityAt);
          if (idle < EVICT_IDLE_MS || t <= r.endAt) throw new Error(`${describeUser(h.who)} is not evictable: idle ${minutes(idle)} min (needs ${minutes(EVICT_IDLE_MS)}), ${t <= r.endAt ? "still inside its estimate" : "past its estimate"}`);
          state.entries.splice(state.entries.indexOf(h), 1);
          endConnection(state, h, t, `evicted by ${describeUser(who)}: ${clip(reason, 120)}`);
          retractOpen(state, h.key, t, "it was evicted as idle");
          event(state, t, `${describeUser(who)} evicted ${describeUser(h.who)} from the ${h.sides.join("+")} bridge: ${clip(reason, 160)}`);
          evicted.push(describeUser(h.who));
        }
        normalize(state, { now: t, ...opts });
        return view(state, t, who, `Evicted ${evicted.join(", ")}.`);
      });
    },
    /**
     * Gate for one bridge call. Returns null when allowed, else the refusal text. A read-only op is
     * always allowed. `requireSlot` (MCP) refuses a changing op unless the caller holds the side;
     * without it (call.mjs, scripts) only another session's hold refuses. Records the caller's
     * activity at most every TOUCH_THROTTLE_MS.
     */
    gate(who, side, op, args, { requireSlot = true } = {}) {
      const readOnly = isReadOnlyOp(op, args);
      const key = keyFor(who);
      if (readOnly) { this.touch(who); return null; }
      if (!gatingEnabled(env)) {
        // PROGENITOR_QUEUE=off lets the op through, but never invisibly: a changing op on a side
        // another session holds is recorded on that holder (shown in status) and the holder gets a
        // notice, at most once per OVERRIDE_NOTICE_MS (2026-10-11: an override run hid one
        // session's use of the client from everyone else).
        try {
          transact((state, t, ctx) => {
            touch(state, who, t);
            const holder = holderOf(state, side);
            if (!holder || holder.key === key || maybeSameSession(holder, who)) { ctx.write = !!find(state, who); return; }
            const by = describeUser(who);
            holder.overrides = (holder.overrides ?? []).filter((o) => o.by !== by || o.side !== side);
            const last = (state.overrideNotices ?? {})[`${key}|${holder.key}`] ?? 0;
            holder.overrides.push({ by, side, op, at: iso(t) });
            if (holder.overrides.length > 5) holder.overrides.splice(0, holder.overrides.length - 5);
            if (t - last >= OVERRIDE_NOTICE_MS) {
              state.overrideNotices = { ...(state.overrideNotices ?? {}), [`${key}|${holder.key}`]: t };
              state.messages.push({ id: newId(t), at: iso(t), fromKey: key, fromName: by, toKey: holder.key, toName: describeUser(holder.who),
                text: `ran '${op}' on the ${side} under PROGENITOR_QUEUE=off while you hold it`, readAt: null, noReply: true });
              event(state, t, `${by} ran '${op}' on the ${side} under PROGENITOR_QUEUE=off while ${describeUser(holder.who)} holds it`);
            }
          }, { waitMs: 500 });
        } catch { /* the override must still work when the queue file is busy or broken */ }
        return null;
      }
      return transact((state, t) => {
        const me = touch(state, who, t);
        lastTouch.set(key, t);
        const holder = holderOf(state, side);
        if (holder && holder.key === key) return null;
        if (holder && !requireSlot && maybeSameSession(holder, who)) return null;
        const join = `Join with progenitor_queue action=join (sides, estimated_minutes, progenitor_version, mods), then wait.`;
        if (holder) {
          const r = schedule(state, t).find((x) => x.entry === holder);
          const pos = me ? `you are waiting at position ${schedule(state, t).find((x) => x.entry === me).position}` : "you are not in the queue";
          return `refused: the ${side} bridge is held by ${describeUser(holder.who)} until ~${clock(r.endAt)} (${pos}). '${op}' changes the game; read-only ops still work. ${me ? "Use progenitor_queue action=wait." : join}`;
        }
        if (!requireSlot) return null;
        if (me) return `refused: you are queued but do not hold the ${side} bridge yet. Use progenitor_queue action=wait.`;
        return `refused: '${op}' changes the ${side} game and the ${side} bridge is free, but you have not claimed it. ${join} A free side is granted at once.`;
      });
    },
    /** Records activity for the caller's entry, at most every TOUCH_THROTTLE_MS. Never throws. */
    touch(who) {
      const key = keyFor(who);
      if (now() - (lastTouch.get(key) ?? 0) < TOUCH_THROTTLE_MS) return;
      lastTouch.set(key, now());
      try { transact((state, t) => { touch(state, who, t); }); } catch { /* activity is best effort */ }
    },
    /** Reminder text for a holder, without delivering anything. "" when nothing to say. */
    reminder(who) {
      try {
        const t = now();
        const state = normalize(readState(file), { now: t, ...opts });
        return reminderText(state, find(state, who), t);
      } catch { return ""; }
    },
    /**
     * Pending queue notes for the caller: unread messages (marked delivered) and a due holder
     * reminder. "" when there is nothing; a cheap unlocked read decides whether to lock at all.
     * Never throws.
     */
    notes(who) {
      try {
        const t = now();
        const peek = normalize(readState(file), { now: t, ...opts });
        const key = mailKey(peek, who);
        const me = find(peek, who);
        const unread = (peek.messages ?? []).some((m) => m.toKey === key && !m.readAt && !m.retractedAt);
        const due = !!reminderText(peek, me, t) && t - ms(me.remindedAt ?? 0) >= REMIND_EVERY_MS;
        if (!unread && !due) return "";
        return transact((state, tt) => deliver(state, who, tt), { waitMs: 500 });
      } catch { return ""; }
    },
    /** True when notes(who) would return something; reads without locking or marking anything. */
    hasNotes(who) {
      try {
        const t = now();
        const peek = normalize(readState(file), { now: t, ...opts });
        const key = mailKey(peek, who);
        const me = find(peek, who);
        if ((peek.messages ?? []).some((m) => m.toKey === key && !m.readAt && !m.retractedAt)) return true;
        return !!reminderText(peek, me, t) && t - ms(me.remindedAt ?? 0) >= REMIND_EVERY_MS;
      } catch { return false; }
    },
    /**
     * Mailbox. A waiter among the next two for a holder messages that holder; a holder messages the
     * waiter at position `to`, else every waiter that messaged it during this hold, else every waiter
     * for its sides. At most MAX_UNREAD_PER_SENDER undelivered messages per sender and recipient.
     */
    message(who, text, { to, noReply = false } = {}) {
      const body = clip(text, MAX_MESSAGE);
      if (!body) throw new Error("message needs text");
      return transact((state, t) => {
        const me = touch(state, who, t);
        if (!me) throw new Error("join the queue first: only queued sessions can message each other");
        const rows = schedule(state, t);
        let targets;
        if (me.state === "waiting") {
          const mine = rows.find((r) => r.entry === me);
          if (!mine.mayContact.length) throw new Error(`you are position ${mine.position}; only the next ${MAX_CONTACTS} waiters for a holder may message it. Wait for your turn.`);
          targets = mine.mayContact.map((k) => state.entries.find((e) => e.key === k));
        } else {
          const waiters = rows.filter((r) => r.entry.state === "waiting" && overlaps(r.entry, me));
          if (to != null) {
            const r = waiters.find((x) => x.position === Number(to));
            if (!r) throw new Error(`no waiter for your sides at position ${to}`);
            targets = [r.entry];
          } else {
            const askers = new Set(state.messages.filter((m) => m.toKey === me.key && ms(m.at) >= ms(me.startedAt)).map((m) => m.fromKey));
            targets = waiters.filter((r) => askers.has(r.entry.key)).map((r) => r.entry);
            if (!targets.length) targets = waiters.map((r) => r.entry);
          }
          if (!targets.length) throw new Error("nobody is waiting for your sides");
        }
        const sent = [];
        const full = [];
        for (const target of targets) {
          const pending = state.messages.filter((m) => !m.system && !m.retractedAt && m.fromKey === me.key && m.toKey === target.key && !m.readAt).length;
          if (pending >= MAX_UNREAD_PER_SENDER) { full.push(describeUser(target.who)); continue; }
          state.messages.push({ id: newId(t), at: iso(t), fromKey: me.key, fromName: describeUser(who), toKey: target.key, toName: describeUser(target.who), text: body, readAt: null, ...(noReply ? { noReply: true } : {}) });
          sent.push(describeUser(target.who));
        }
        const fullNote = full.length ? `Not sent to ${full.join(", ")}: ${MAX_UNREAD_PER_SENDER} of your messages are still waiting to be picked up.` : "";
        if (!sent.length) throw new Error(fullNote);
        state.messages = state.messages.slice(-MAX_MESSAGES);
        event(state, t, `${describeUser(who)} messaged ${sent.join(", ")}`);
        return view(state, t, who, [
          `Sent to ${sent.join(", ")}. It is delivered with their next queue, MCP or call.mjs call.${fullNote ? ` ${fullNote}` : ""}`,
          noReply ? "Sent as a notice: no reply expected, so it is not retracted when you leave." : MONITOR_INSTRUCTION,
        ].join("\n"));
      });
    },
    /** Withdraws the caller's open messages (no reply yet); delivered ones leave the recipient a notice. */
    retract(who, reason) {
      return transact((state, t) => {
        touch(state, who, t);
        const n = retractOpen(state, mailKey(state, who), t, clip(reason, 120) || "the sender stopped watching for a reply");
        if (n) event(state, t, `${describeUser(who)} retracted ${n} message${n > 1 ? "s" : ""}`);
        return view(state, t, who, n ? `Retracted ${n} message${n > 1 ? "s" : ""}.` : "You have no open messages to retract.");
      });
    },
    /**
     * A no-reply notice to a session by its session ID, whether or not it is in the queue (for
     * example "main moved, please rebase"). Delivered once with that session's next queue, MCP or
     * call.mjs call. Anyone may send one; at most MAX_UNREAD_PER_SENDER undelivered per pair.
     */
    notify(who, sessionId, text) {
      const body = clip(text, MAX_MESSAGE);
      const id = clip(sessionId, 120);
      if (!body) throw new Error("notify needs text");
      if (!id) throw new Error("notify needs the recipient's session ID (status shows it in each entry)");
      return transact((state, t) => {
        touch(state, who, t);
        const toKey = `id:${id}`;
        const entry = state.entries.find((e) => e.key === toKey);
        const past = entry ?? [...(state.history ?? [])].reverse().find((h) => h.who?.id === id);
        const toName = past ? describeUser(past.who) : `session ${id}`;
        const fromKey = mailKey(state, who);
        const pending = state.messages.filter((m) => m.fromKey === fromKey && m.toKey === toKey && !m.readAt && !m.retractedAt).length;
        if (pending >= MAX_UNREAD_PER_SENDER) throw new Error(`${toName} has not picked up your last ${pending} notices yet`);
        state.messages.push({ id: newId(t), at: iso(t), fromKey, fromName: describeUser(who), toKey, toName, text: body, readAt: null, noReply: true });
        state.messages = state.messages.slice(-MAX_MESSAGES);
        event(state, t, `${describeUser(who)} sent a notice to ${toName}`);
        return `Notice queued for ${toName}${entry ? "" : " (not in the queue now)"}: delivered once with its next queue, MCP or call.mjs call. No reply expected.`;
      });
    },
    /** A holder hands back some of its sides and keeps the rest; all of them is the same as leave. */
    release(who, sides) {
      const give = normalizeSides(sides);
      return transact((state, t) => {
        const me = touch(state, who, t);
        if (!me || me.state !== "active") throw new Error("only the holder of a side can release it");
        const keep = me.sides.filter((x) => !give.includes(x));
        const freed = me.sides.filter((x) => give.includes(x));
        if (!freed.length) throw new Error(`you hold ${me.sides.join("+")}, not ${give.join("+")}`);
        if (!keep.length) {
          state.entries.splice(state.entries.indexOf(me), 1);
          endConnection(state, me, t, "left");
          retractOpen(state, me.key, t, "it left the queue");
        } else {
          me.sides = keep;
          const rec = me.historyId && state.history.find((h) => h.id === me.historyId);
          if (rec) {
            rec.builds.push({ at: iso(t), side: freed.join("+"), build: "", change: `released ${freed.join("+")}, kept ${keep.join("+")}` });
            rec.released = [...(rec.released ?? []), { sides: freed, at: iso(t) }];
          }
        }
        event(state, t, `${describeUser(who)} released the ${freed.join("+")} bridge${keep.length ? `, kept ${keep.join("+")}` : ""}`);
        normalize(state, { now: t, ...opts });
        return view(state, t, who, `Released ${freed.join("+")}.`);
      });
    },
    wait(who, seconds, sleep) {
      const secs = Number(seconds);
      const until = now() + Math.max(1, Math.min(600, Number.isFinite(secs) && secs > 0 ? secs : 120)) * 1000;
      const step = async () => {
        for (;;) {
          const state = this.snapshot();
          const me = state.entries.find((e) => e.key === keyFor(who));
          if (!me) return this.status(who).replace(/^/, "You are not in the bridge queue.\n");
          if (this.hasNotes(who)) return this.status(who).replace(/^/, "Mail arrived.\n");
          // A holder waits only for replies to its own open messages; otherwise it already has its sides.
          if (me.state === "active" && !hasOpenSent(state, me.key)) return this.status(who);
          if (now() >= until) return this.status(who).replace(/^/, "Still waiting.\n");
          // Check in without delivering mail: the final status() below shows it.
          transact((state, t) => { touch(state, who, t); });
          await sleep(5000);
        }
      };
      return step();
    },
  };
}

// CLI ---------------------------------------------------------------------------------------------
function parseArgs(argv) {
  const out = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) { out[a.slice(2)] = argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : "true"; }
    else out._.push(a);
  }
  return out;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const a = parseArgs(process.argv.slice(2));
  const action = a._[0] ?? "status";
  const who = detectIdentity({ tool: "queue.mjs" });
  const q = createQueue();
  try {
    let out;
    if (action === "status") out = q.status(who);
    else if (action === "join") out = q.join(who, { sides: a.sides, estimatedMinutes: a.minutes, purpose: a.purpose, progenitor: a.progenitor, mods: a.mods ?? (a["no-mods"] ? [] : undefined), contact: a.contact });
    else if (action === "update") out = q.update(who, { estimatedMinutes: a.minutes, purpose: a.purpose, progenitor: a.progenitor, mods: a.mods, contact: a.contact });
    else if (action === "leave") out = q.leave(who);
    else if (action === "history") out = q.history(who, a.count);
    else if (action === "message") out = q.message(who, a.text, { to: a.to, noReply: a["no-reply"] === "true" });
    else if (action === "release") out = q.release(who, a.sides);
    else if (action === "retract") out = q.retract(who, a.reason);
    else if (action === "notify") out = q.notify(who, a.session, a.text);
    else if (action === "wait") out = await q.wait(who, Number(a.seconds) || 120, (ms) => new Promise((r) => setTimeout(r, ms)));
    else if (action === "probe") out = q.probe(who, a.note);
    else if (action === "evict") out = q.evict(who, a.reason);
    else if (action === "gate") {
      // For scripts (deploy.ps1): exit 5 with the refusal on stdout when another session holds a side.
      // Prints to stdout only: Windows PowerShell under ErrorActionPreference=Stop turns stderr into errors.
      try {
        for (const s of normalizeSides(a.sides ?? "both")) {
          const r = q.gate(who, s, a.op ?? "deploy", {}, { requireSlot: false });
          if (r) { console.log(r.replace(/progenitor_queue action=(\w+)/g, "node tools/mcp/queue.mjs $1")); process.exit(5); }
        }
      } catch (e) {
        if (/locked/.test(e.message)) { console.log(`bridge queue: ${e.message}`); process.exit(5); }
        console.log(`bridge queue unavailable, not gating: ${e.message}`);
      }
      out = "ok";
    }
    else { console.error("usage: queue.mjs status|history|join|wait|update|leave|release|message|notify|retract|probe|evict|gate [--sides client,server] [--minutes N] [--progenitor V] [--mods name@v,...|--no-mods] [--purpose text] [--contact text] [--reason text] [--op name] [--count N] [--text message] [--to position] [--seconds N] [--session id]"); process.exit(2); }
    // CLI users get CLI commands in hints written for MCP.
    console.log(String(out).replace(/progenitor_queue action=(\w+)( text="\.\.\." \(CLI: [^)]*\))?/g, (_, act, txt) => `node tools/mcp/queue.mjs ${act}${txt ? ' --text "..."' : ""}`).replace(/progenitor_queue (leave|update|release|retract|wait)/g, "node tools/mcp/queue.mjs $1"));
  } catch (e) { console.error(`error: ${e.message}`); process.exit(1); }
}

export const _internal = { readState, writeState, fileURLToPath };
