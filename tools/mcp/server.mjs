#!/usr/bin/env node
// Progenitor MCP server.
//
// Exposes the in-game debug bridge (Progenitor.Dev) on the client and the test server as tools, plus
// the build, deploy, and test-server scripts. Runs as a normal local process; the game never sees it.
//
// Configuration (environment, all optional):
//   PROGENITOR_REPO       repo root (default: two levels up from this file)
//   PROGENITOR_GAME_DIR   V Rising client install
//   PROGENITOR_TEST_ROOT  staged test server root (default <workspace>/_testserver)
//   PROGENITOR_WORKSPACE  folder holding the main checkout (default: resolved from git metadata,
//                         tools/common/workspace.mjs)
//   PROGENITOR_CLIENT_PORT / PROGENITOR_SERVER_PORT   bridge ports (27341 / 27342)
//   PROGENITOR_CLIENT_TOKEN_FILE                      extra-client token path (default: Steam BepInEx config)

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import net from "node:net";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawn, execFileSync } from "node:child_process";
import {
  isExecPreflightStall,
  snapshotAfterExecPreflight,
  formatExecPreflightStall,
  EXEC_PREFLIGHT_LOG_FILTER,
} from "./exec-preflight.mjs";
import { requireWorkspacePath } from "../common/workspace.mjs";
import { AsyncLocalStorage } from "node:async_hooks";
import { createIdentity, wireWho, sharingNote, formatBridgeUsers } from "./identity.mjs";
import { createQueue, keyOf, revisionOf } from "./queue.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const REPO = process.env.PROGENITOR_REPO ?? path.resolve(here, "..", "..");
const GAME_DIR = process.env.PROGENITOR_GAME_DIR ?? "C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRising";
// <workspace>/_testserver unless PROGENITOR_TEST_ROOT says otherwise (tools/common/workspace.mjs).
const TEST_ROOT = process.env.PROGENITOR_TEST_ROOT ?? requireWorkspacePath("PROGENITOR_TEST_ROOT", "_testserver");

const SIDES = {
  client: {
    port: Number(process.env.PROGENITOR_CLIENT_PORT ?? 27341),
    tokenFile: process.env.PROGENITOR_CLIENT_TOKEN_FILE || path.join(GAME_DIR, "BepInEx", "config", "progenitor-dev.token"),
    logFile: path.join(GAME_DIR, "BepInEx", "LogOutput.log"),
    reloadFolder: path.join(GAME_DIR, "BepInEx", "progenitor-reload"),
  },
  server: {
    port: Number(process.env.PROGENITOR_SERVER_PORT ?? 27342),
    tokenFile: process.env.PROGENITOR_SERVER_TOKEN_FILE || path.join(TEST_ROOT, "server", "BepInEx", "config", "progenitor-dev.token"),
    logFile: path.join(TEST_ROOT, "server", "BepInEx", "LogOutput.log"),
    reloadFolder: path.join(TEST_ROOT, "server", "BepInEx", "progenitor-reload"),
  },
};

const SCRATCH_DIR = path.join(REPO, "tools", "scratch");
const SCRATCH_SNIPPET = path.join(SCRATCH_DIR, "Snippet.cs");
const SCRATCH_DLL = path.join(SCRATCH_DIR, "bin", "Debug", "net6.0", "Progenitor.Scratch.dll");

const SNIPPET_TEMPLATE = `using System;
using System.Linq;
using System.Text;
using Unity.Entities;
using Unity.Collections;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Progenitor;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Hooks;
using Progenitor.Networking;

public static class Snippet
{
    public static object Run()
    {
{{BODY}}
    }
}
`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function ensureReturn(code) {
  const body = (code ?? "").trim();
  if (!body) return 'return "ok";';
  const last = body.split(/\n/).pop().trim();
  if (/^return\b/.test(last)) return body;
  return `${body}\nreturn "ok";`;
}

/**
 * Known process-pinning shapes, refused before the snippet is compiled (interop trap 2, evidence 219).
 * Textual only: no compile-time analysis, so a name that merely looks like one of these is refused too.
 * The `// exec: allow-generic-interop` marker line bypasses the whole scan for a deliberate experiment.
 */
const PIN_MARKER = "// exec: allow-generic-interop";
const PIN_PATTERNS = [
  {
    re: /\.With(?:All|Any|None)(?:RW)?\s*</,
    what: "EntityQueryBuilder .WithAll<T> / .WithAny<T> / .WithNone<T> / .WithAllRW<T>",
    instead: "Progenitor.Ecs.Query.Entities(...) (non-generic component names)",
  },
  {
    re: /\bGetSingleton(?:RW)?\s*</,
    what: "GetSingleton<T> / GetSingletonRW<T>",
    instead: "Progenitor.Ecs.Singletons (GetSingletonEntity + a raw read)",
  },
  {
    re: /\bnew\s+Native(?:List|HashMap|ParallelHashMap)\s*</,
    needsAlso: /\.Add\s*\(/,
    what: "a NativeList<T> / NativeHashMap<K,V> / NativeParallelHashMap<K,V> built here and added to",
    instead: "Progenitor.Ecs.Query.Entities / EntityExtensions, or a container the game already instantiated",
  },
];

/** Null when the snippet is allowed; otherwise the refusal text handed straight back to the caller. */
function scanSnippetForPins(code) {
  const source = String(code ?? "");
  if (source.split(/\r?\n/).some((l) => l.trim().startsWith(PIN_MARKER))) return null;

  const hits = [];
  for (const p of PIN_PATTERNS) {
    if (!p.re.test(source)) continue;
    if (p.needsAlso && !p.needsAlso.test(source)) continue;
    const lines = source.split(/\r?\n/);
    const n = lines.findIndex((l) => p.re.test(l)) + 1;
    hits.push(`  line ${n || "?"}: ${p.what} -> use ${p.instead}`);
  }
  if (!hits.length) return null;

  return [
    "refused before compiling: this snippet uses generic interop calls with a known process-pinning shape.",
    ...hits,
    "",
    "Interop trap 2 (CLAUDE.md): a generic interop call over a game value type can hard-crash, and when it throws instead, the runtime caches the failure as a TypeInitializationException whose captured frames root this throwaway plugin's allocator and the Core generation it referenced. The Core generation is then pinned and the next Core swap is refused until the process restarts (evidence 219; research core-hot-reload.md, \"Pin class: a retained exception's keepalive array\").",
    "Use Core's non-generic helpers instead: Progenitor.Ecs.Query.Entities, Progenitor.Ecs.EntityExtensions, Progenitor.Ecs.Singletons.",
    `To run it anyway as a deliberate experiment, put the line \`${PIN_MARKER}\` in the snippet - and expect to restart the process afterwards.`,
  ].join("\n");
}

function writeSnippet(code) {
  const body = ensureReturn(code);
  const indented = body.split(/\n/).map((l) => (l.length ? "        " + l : l)).join("\n");
  fs.writeFileSync(SCRATCH_SNIPPET, SNIPPET_TEMPLATE.replace("{{BODY}}", indented), "utf8");
}

function formatLogLines(lines) {
  return lines.map((l) => `${l.seq} ${l.time} [${l.level}:${l.source}] ${l.text}`).join("\n");
}

let nextId = 1;

// Who this MCP process speaks for (tools/mcp/identity.mjs). Sent as `who` on every request.
const identity = createIdentity({ tool: "mcp", getClientInfo: () => server?.server?.getClientVersion?.() ?? null });
// Per tool call: the previous bridge user each side reported, for the sharing note.
const callNotes = new AsyncLocalStorage();
// Last note shown per side, so an idle earlier user is mentioned once, not on every call.
const lastNoted = {};
// Bridge queue (tools/mcp/queue.mjs): an op that changes a game needs this session to hold the side.
// The queue key is chosen once per process (at the first tool call, after initialize), so a later
// progenitor_whoami override or a session title change cannot strand this session's own slot.
let queueKey = null;
const myQueueKey = () => (queueKey ??= keyOf(identity.get()));
const queue = createQueue({ key: () => myQueueKey(), relate: relateToBranch });

// How a loaded Core's build revision relates to this checkout's HEAD, so an agent sees before a swap
// whether the running Core is older than its rebase (safe to replace) or has commits it lacks (an
// older Core swapped in over it gets kicked: rebase first). git runs synchronously, so each answer,
// failures included, is cached per revision for a minute and every git call is bounded to 2 s.
const relateCache = new Map();
function relateToBranch(version) {
  const r = revisionOf(version);
  if (!r) return "";
  const hit = relateCache.get(version);
  if (hit && Date.now() - hit.at < 60_000) return hit.text;
  const git = (...a) => execFileSync("git", ["-C", REPO, ...a], { stdio: ["ignore", "pipe", "ignore"], windowsHide: true, timeout: 2000 }).toString().trim();
  let text = "";
  try {
    const head = git("rev-parse", "--short", "HEAD");
    let known = true;
    try { git("cat-file", "-e", `${r.rev}^{commit}`); } catch { known = false; }
    if (!known) text = `revision ${r.rev} is not in this checkout (fetch, or it was built from an unpushed branch)`;
    else if (r.rev.startsWith(head) || head.startsWith(r.rev)) text = `revision ${r.rev} is your HEAD`;
    else {
      let behind = false;
      try { git("merge-base", "--is-ancestor", r.rev, "HEAD"); behind = true; } catch { behind = false; }
      text = behind
        ? `revision ${r.rev} is in your branch (your HEAD ${head} is the same or newer)`
        : `revision ${r.rev} has commits your HEAD ${head} lacks: rebase before swapping Core`;
    }
    if (r.dirty) text += "; built with uncommitted changes";
  } catch { text = ""; }
  relateCache.set(version, { text, at: Date.now() });
  return text;
}

/** Null when this session may run `op` on every side in `sides` now, else the refusal text. */
function queueGate(sides, op, args) {
  for (const s of sides) {
    try {
      const refusal = queue.gate(identity.get(), s, op, args);
      if (refusal) return refusal;
    } catch (e) {
      // A busy lock refuses; an unresolvable queue file must not take the bridge down with it.
      if (/locked/.test(e.message)) return `refused: ${e.message}`;
      console.error(`[progenitor-mcp] bridge queue unavailable: ${e.message}`);
    }
  }
  return null;
}

function readToken(side) {
  const f = SIDES[side].tokenFile;
  if (!fs.existsSync(f)) throw new Error(`no token file for ${side} at ${f}; has the game started with Progenitor.Dev loaded?`);
  return fs.readFileSync(f, "utf8").trim();
}

/** One request over a fresh connection. The bridge speaks one JSON object per line. */
function bridge(side, op, args = {}, timeoutMs = 25000) {
  const { port } = SIDES[side];
  const refusal = queueGate([side], op, args);
  if (refusal) return Promise.reject(new Error(refusal));
  const token = readToken(side);
  return new Promise((resolve, reject) => {
    const id = nextId++;
    const sock = net.createConnection({ host: "127.0.0.1", port }, () => {
      let who;
      try { who = wireWho(identity.get()); } catch { who = undefined; }
      sock.write(JSON.stringify({ id, token, op, args, who }) + "\n");
    });
    let buf = "";
    const timer = setTimeout(() => { sock.destroy(); reject(new Error(`${side} bridge timed out on '${op}'`)); }, timeoutMs);
    sock.setEncoding("utf8");
    sock.on("data", (chunk) => {
      buf += chunk;
      const nl = buf.indexOf("\n");
      if (nl < 0) return;
      clearTimeout(timer);
      const line = buf.slice(0, nl);
      sock.end();
      try {
        const msg = JSON.parse(line);
        if (msg.bridgeUser) callNotes.getStore()?.set(side, msg.bridgeUser);
        if (msg.ok && op === "ping") { try { queue.observe(identity.get(), side, msg.result); } catch { /* best effort */ } }
        if (msg.ok) resolve(msg.result);
        else reject(new Error(msg.error ?? "bridge error"));
      } catch (e) { reject(e); }
    });
    sock.on("error", (e) => { clearTimeout(timer); reject(new Error(`${side} bridge not reachable on 127.0.0.1:${port}: ${e.message}`)); });
  });
}

function run(cmd, args, cwd = REPO, timeoutMs = 600000) {
  return new Promise((resolve) => {
    // Scripts it starts (deploy.ps1 calls call.mjs) count as this session in the bridge queue.
    let env = process.env;
    try { env = { ...process.env, PROGENITOR_QUEUE_KEY: process.env.PROGENITOR_QUEUE_KEY || myQueueKey() }; } catch { /* identity is best effort */ }
    const child = spawn(cmd, args, { cwd, windowsHide: true, env });
    let out = "";
    const timer = setTimeout(() => { child.kill(); out += "\n[timed out]"; }, timeoutMs);
    child.stdout.on("data", (d) => (out += d));
    child.stderr.on("data", (d) => (out += d));
    child.on("close", (code) => { clearTimeout(timer); resolve({ code, out }); });
  });
}

const ps = (script, args) => run("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", `& '${path.join(REPO, "tools", script)}' ${args}`]);

const text = (obj) => ({ content: [{ type: "text", text: typeof obj === "string" ? obj : JSON.stringify(obj, null, 2) }] });
const fail = (e) => ({ content: [{ type: "text", text: `error: ${e.message ?? e}` }], isError: true });

const server = new McpServer({
  name: "progenitor",
  version: "0.1.0",
}, {
  instructions: [
    "Progenitor dev bridge for the V Rising client and the isolated test server.",
    "Before an op that changes either game, hold the side in the bridge queue (progenitor_queue join), even when the user says go ahead; if someone else holds it and it is urgent, message the holder (progenitor_queue message, or notify) before acting. Leave the queue as soon as your live work is done, and then tell the user plainly that you are done and hold nothing; do not wait to be asked. PROGENITOR_QUEUE=off is only for a machine with a single agent: under it, a changing op on a side another session holds is still recorded on that holder and sent to it as a notice.",
    "Player death or downed: read the state first (dead = ProjectM.Dead on the character; downed = Buff_General_Vampire_Wounded_Buff / VampireDownedBuff without Dead). If a person is playing that character, report and ask; do not revive or respawn them unasked (they can press Space). Downed: progenitor_heal restores them in place (fills health and blood, removes the downed buff; nothing is lost). Dead: bridge respawn op (node tools/mcp/call.mjs server respawn with user), then progenitor_teleport back to where they were, then progenitor_heal; heal refuses a dead character. Death costs (blood type and quality reset, durability) are not undone. A death screen where Space does nothing: check the user's respawn list for a broken entry and clear it with the bridge bind op {\"clear\":true}. Protections block death mechanics: progenitor.debug and Admin_Invulnerable_Buff stop damage and make enemies ignore the player; never strip Immortal from a player (vanilla uses it for the downed state). Target dummies never attack, and bridge-spawned enemies attack only an unprotected player.",
  ].join("\n"),
});
const side = z.enum(["client", "server"]).describe("Which game process to talk to.");

/** Appends "Note: <other session> used the <side> bridge N min ago" when a bridge reported one. */
function withSharingNotes(result, notes) {
  if (!notes?.size || !result || !Array.isArray(result.content)) return result;
  const me = identity.get();
  const lines = [];
  for (const [s, prev] of notes) {
    const note = sharingNote(s, prev, me);
    if (!note) continue;
    const key = `${prev.id || prev.ai + "|" + prev.name}|${prev.lastSeen}`;
    if (lastNoted[s] === key) continue;
    lastNoted[s] = key;
    lines.push(note);
  }
  if (!lines.length) return result;
  return { ...result, content: [...result.content, { type: "text", text: lines.join("\n") }] };
}

// Every tool handler runs inside a note scope, so any bridge call it makes can report sharing.
// Installed before the first registration; catalog and kb tools are wrapped too (no bridge calls).
{
  const register = server.tool.bind(server);
  server.tool = (...a) => {
    const handler = a[a.length - 1];
    if (typeof handler === "function") {
      a[a.length - 1] = async (...args) => {
        const notes = new Map();
        try { queue.touch(identity.get()); } catch { /* best effort */ }
        const r = await callNotes.run(notes, () => handler(...args));
        let out = r;
        try { out = withSharingNotes(r, notes); } catch { /* keep r */ }
        try { out = withQueueReminder(out); } catch { /* keep out */ }
        return out;
      };
    }
    return register(...a);
  };
}

/** Appends queue mail for this session and the holder reminder (over estimate, who waits), at most every 5 minutes. */
function withQueueReminder(result) {
  if (!result || !Array.isArray(result.content)) return result;
  // Unread queue messages for this session (marked delivered) and a due holder reminder.
  const note = queue.notes(identity.get());
  if (!note) return result;
  return { ...result, content: [...result.content, { type: "text", text: note }] };
}

/** ping/status result with a readable bridge-user summary above the JSON. */
function withUsers(result) {
  const summary = formatBridgeUsers(result?.bridgeUsers);
  const body = JSON.stringify(result, null, 2);
  return text(summary ? `${summary}\n${body}` : body);
}

server.tool("progenitor_ping", "Bridge health for one side: worlds, uptime, reloaded plugins, latest log sequence, and bridgeUsers (who drove this bridge: the current AI session and the last three others).", { side }, async ({ side }) => {
  try { return withUsers(await bridge(side, "ping")); } catch (e) { return fail(e); }
});

server.tool("progenitor_whoami", "Show the identity this MCP process sends to the dev bridges (ai, session name, session id, MCP client, tool, pid, model). Pass ai / session_name / session_id to override them for the rest of this MCP process when auto-detection got them wrong; an empty string clears that override. Bridges keep the last users so agents sharing the client and test server can see who else touched them.",
  {
    ai: z.string().max(40).optional().describe("AI name, e.g. claude, codex, grok."),
    session_name: z.string().max(120).optional().describe("Human-readable session name."),
    session_id: z.string().max(120).optional().describe("Stable session ID."),
  },
  async ({ ai, session_name, session_id }) => {
    try {
      const who = (ai !== undefined || session_name !== undefined || session_id !== undefined)
        ? identity.override({ ai, name: session_name, id: session_id })
        : identity.get();
      return text({ ...who, overrides: identity.overrides() });
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_queue", "Bridge queue: agents share one client and one test server, so a session must hold a side before any op that changes that game (read-only ops such as ping, logs, query, get, inspect and status always work). status (default) first shows LOADED NOW: the Core (version+revision, generation, sha), Bootstrap and plugins last seen on each side, and whether that Core revision is in your branch or newer than your HEAD (rebase before swapping Core); refresh=true pings both sides first. Then the holder of each side with its versions, expected time and how to reach it, then every waiter with its position, estimate and expected start. history lists the last connections (the queue keeps 50) with how each ended and every Core swap seen during it. join claims sides with an honest estimated_minutes, the progenitor_version and every mod you will run (name@version, or [] for none); a free side is granted at once, otherwise you wait in first-come order. wait blocks up to `seconds` (default 120, max 600) until you hold your sides. update changes your estimate (for a holder: minutes still needed), purpose, versions or contact. leave releases your slot: leave as soon as your live work is done (standing rule); never hold the client while idle. release hands back some sides and keeps the rest (a waiter that needs only the client can go while you keep the server). message is the mailbox: a waiter among the next two writes to the holder, the holder replies; messages arrive in the recipient's next MCP tool result, queue.mjs call or call.mjs call, and status shows them with UNANSWERED after 10 min of silence while the holder is active. After sending, keep watching for the reply (wait returns as soon as mail arrives); if you cannot (session ending, long offline work), retract your open messages. Leaving the queue as a waiter, an exited session or an idle release retracts them automatically. Only the next two waiters for a holder may contact it: probe shows whether its process is alive, its last activity and its address, and records the check; evict releases a holder that has been idle 10+ min and is past its estimate (reason required). Holders are released automatically when their MCP process exits, or after 15 min idle while someone waits (30 min regardless).",
  {
    action: z.enum(["status", "join", "wait", "update", "leave", "release", "message", "retract", "notify", "probe", "evict", "history"]).optional().describe("Default status."),
    text: z.string().max(300).optional().describe("message: what to say. A waiter among the next two writes to the holder; the holder replies."),
    session_id: z.string().max(120).optional().describe("notify: the recipient's session ID (from status or history); it need not be in the queue."),
    no_reply: z.boolean().optional().describe("message: a notice (FYI, a request that needs no answer). Never retracted when you leave, never UNANSWERED, and you need not watch for a reply."),
    to: z.number().int().min(1).optional().describe("message from a holder: the waiter's position. Default: the waiters who messaged you, else every waiter for your sides."),
    refresh: z.boolean().optional().describe("status: ping both sides first so LOADED NOW is current (one main-thread op per side; don't poll it while someone plays)."),
    count: z.number().int().min(1).max(50).optional().describe("history: how many connections, newest first (default 10; the queue keeps 50)."),
    sides: z.array(z.enum(["client", "server", "both"])).optional().describe("join: sides you will drive. release: sides to hand back (the rest stay yours)."),
    estimated_minutes: z.number().int().min(1).max(480).optional().describe("join/update: how long your live work will take (update on a holder: minutes still needed)."),
    progenitor_version: z.string().max(80).optional().describe("join/update: Progenitor version or commit you run, e.g. 0.1.0+abc1234."),
    mods: z.array(z.union([z.string(), z.object({ name: z.string(), version: z.string() })])).optional().describe("join/update: every mod you run, as name@version or {name, version}; [] for none."),
    purpose: z.string().max(200).optional().describe("join/update: what you are testing."),
    contact: z.string().max(200).optional().describe("join/update: how another session can reach you, when the detected address is not enough."),
    seconds: z.number().int().min(1).max(600).optional().describe("wait: how long to block."),
    reason: z.string().max(200).optional().describe("evict: what the probe showed. probe: optional note. retract: why (shown to a recipient that already read it)."),
  },
  async ({ action, sides, estimated_minutes, progenitor_version, mods, purpose, contact, seconds, reason, refresh, count, text: body, to, no_reply, session_id }) => {
    try {
      const who = identity.get();
      switch (action ?? "status") {
        case "join": return text(queue.join(who, { sides, estimatedMinutes: estimated_minutes, purpose, progenitor: progenitor_version, mods, contact }));
        case "wait": return text(await queue.wait(who, seconds, sleep));
        case "update": return text(queue.update(who, { estimatedMinutes: estimated_minutes, purpose, progenitor: progenitor_version, mods, contact }));
        case "leave": return text(queue.leave(who));
        case "probe": return text(queue.probe(who, reason));
        case "evict": return text(queue.evict(who, reason));
        case "history": return text(queue.history(who, count));
        case "message": return text(queue.message(who, body, { to, noReply: no_reply === true }));
        case "release": return text(queue.release(who, sides));
        case "retract": return text(queue.retract(who, reason));
        case "notify": return text(queue.notify(who, session_id, body));
        default:
          if (refresh) for (const s of ["client", "server"]) { try { await bridge(s, "ping", {}, 8000); } catch { /* side down */ } }
          return text(queue.status(who));
      }
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_logs", "Recent BepInEx log lines from a side's in-memory buffer. Use since=latestSeq from a previous call to get only new lines.",
  { side, since: z.number().int().optional().describe("Return lines with seq greater than this."), max: z.number().int().min(1).max(2000).optional(), filter: z.string().optional().describe("Case-insensitive substring on text or source."), level: z.enum(["Fatal", "Error", "Warning", "Message", "Info", "Debug"]).optional().describe("Minimum severity.") },
  async ({ side, since, max, filter, level }) => {
    try {
      const r = await bridge(side, "logs", { since: since ?? -1, max: max ?? 200, filter, level });
      const lines = r.lines.map((l) => `${l.seq} ${l.time} [${l.level}:${l.source}] ${l.text}`).join("\n");
      return text(`latestSeq=${r.latestSeq} count=${r.count}\n${lines}`);
    } catch (e) { return fail(e); }
  });

/** One ping per side after a change, so the queue's LOADED NOW and connection history see it. */
async function recordLoaded(sides) {
  for (const s of sides) { try { await bridge(s, "ping", {}, 8000); } catch { /* side down or busy */ } }
}

server.tool("progenitor_reload", "Rescan the reload folder now on one side and report what loaded or unloaded.", { side }, async ({ side }) => {
  try { const r = await bridge(side, "reload"); await recordLoaded([side]); return text(r); } catch (e) { return fail(e); }
});

server.tool("progenitor_users", "Users known to a side's world: index, name, connected, admin.", { side }, async ({ side }) => {
  try { return text(await bridge(side, "users")); } catch (e) { return fail(e); }
});

server.tool("progenitor_command", "Inject a chat line on the test server as if a connected user typed it (e.g. '.spike2 give' or 'hello'). The game never sends a player's own line back to them (their client shows it as they type), so by default the speaker also gets the line from the server (echo); other players get it by the game's own rules (Local: within 48 m; Global: everyone). Replies go to that user's client and to the log if the handler logs them; follow with progenitor_logs. Ghosts from progenitor_ghost work: pass user 'Ghost1' or userIndex 10001.",
  {
    text: z.string(),
    user: z.string().optional().describe("Character name or numeric User.Index to speak as (Ghost1, 10001). Defaults to the first connected user."),
    userIndex: z.number().int().optional().describe("User.Index to speak as. Wins over user when both are set."),
    channel: z.enum(["local", "global", "clan"]).optional().describe("The channel the line is typed in. Default local."),
    echo: z.boolean().optional().describe("Also show the line in the speaker's own chat, as typing it would. Default true."),
  },
  async ({ text: t, user, userIndex, channel, echo }) => {
    try {
      const args = { text: t };
      if (user) args.user = user;
      if (userIndex != null) args.userIndex = userIndex;
      if (channel) args.channel = channel;
      if (echo != null) args.echo = echo;
      return text(await bridge("server", "command", args));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_chat_observe", "Server only: inject a chat line and watch SendPacketToUser for windowMs (default 1500). Returns whether any outbound packet contained the injected text. A handled command (.ember 1) must have containsTextAnywhere=false and no recipient with containsText=true — the command was canceled before the chat job serialized it. An unmatched line (hello) is the negative control.",
  {
    text: z.string().describe("Chat line to inject, e.g. '.ember 1' or 'hello'."),
    userIndex: z.number().int().optional().describe("User.Index to speak as. Wins over characterName when both are set."),
    characterName: z.string().optional().describe("Character name to speak as; defaults to the first connected user. Alias of the command op's 'user'."),
    windowMs: z.number().int().min(50).max(15000).optional().describe("How long to watch SendPacketToUser after inject. Default 1500."),
  },
  async ({ text: t, userIndex, characterName, windowMs }) => {
    try {
      const args = { text: t };
      if (userIndex != null) args.userIndex = userIndex;
      if (characterName) args.characterName = characterName;
      if (windowMs != null) args.windowMs = windowMs;
      const timeout = Math.max(25000, (windowMs ?? 1500) + 10000);
      return text(await bridge("server", "chat_observe", args, timeout));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_capture", "Client only: watch the game draw its own beams/effects while the user triggers them. start takes a baseline of every sequence-shaped entity (SpawnSequenceForEntity, SequenceInstance, SequenceGUID, BuffSequence, PreviewPlacementSequence, ProfessorCoilBeam_*, LineRendererPositionElement) plus every GameObject under ParticleLifetimeSystem._ParticleParent and every LineRenderer/HybridLineRendererComponent in the scene, then records what appears and disappears each frame; stop writes BepInEx/progenitor-capture/<timestamp>.json and returns the path plus a summary (new sequence guids with their prefab/GO names, endpoint components and whether they changed, which lines moved); status returns running/elapsed/counts. Workflow: start, tell the user to trigger the effect (build-mode invalid placement = two red beams, feed on an enemy = red feed beam, charm/subdue channeling), stop, read the report.",
  {
    action: z.enum(["start", "stop", "status"]).describe("start a window, stop it and write the report, or poll it."),
    seconds: z.number().min(1).max(120).optional().describe("start only: window length, default 10. The window outlives the bridge call; stop early or read the report afterwards."),
    filter: z.string().optional().describe("start only: '|'-separated case-insensitive substrings matched against new GameObject/component/parent names. Default 'beam|sequence|line'. Empty string records every new object."),
    maxEntities: z.number().int().min(16).max(20000).optional().describe("start only: cap on entities pulled from the interest query each frame. Default 2000."),
  },
  async ({ action, seconds, filter, maxEntities }) => {
    try {
      const args = { action };
      if (seconds != null) args.seconds = seconds;
      if (filter != null) args.filter = filter;
      if (maxEntities != null) args.maxEntities = maxEntities;
      return text(await bridge("client", "capture", args));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_perf", "Either side (written for the server): measure what Progenitor costs per frame, without a profiler. start opens a window that samples every frame — the game's frame delta, Progenitor's own main-thread work split by source (core:effects = ServerBeam keep-alive poll, core:units = unit arrival poll, core:content = prefab refresh/edit flush/handshake watch, core:networking = manifest + kick watches, core:events, core:hooks, core:systems, plugin:<assembly> for each consumer plugin, hook:<pluginId> for system hooks and event:<pluginId> for event handlers), the main-thread queue depth, GC allocated bytes per frame and managed heap size. stop writes BepInEx/progenitor-perf/<timestamp>.json and returns a compact summary: p50/p95/p99/max frame ms, Progenitor ms per frame total and per source, allocation rate, and the worst frame with its per-source breakdown. status returns running/elapsed/frames. Ring buffers are preallocated (60 s at 120 fps); a longer run wraps and the report says so. Bootstrap drains the main-thread queue before Core's tick and is not instrumented, so drain time is not attributed — queueDepth is the proxy.",
  {
    side: z.enum(["client", "server"]).describe("Which process to profile. 'server' is the staged test server."),
    action: z.enum(["start", "stop", "status", "live"]).describe("start a window, stop it and write the report, or poll it. live reads scalar progress (running, historical, captureId) on the socket thread even when the main thread is stalled; status needs the main thread."),
    seconds: z.number().min(1).max(300).optional().describe("start only: window length, default 30. The window outlives the bridge call; stop early or let the deadline close it."),
  },
  async ({ side, action, seconds }) => {
    try {
      const args = { action };
      if (seconds != null) args.seconds = seconds;
      return text(await bridge(side, action === "live" ? "perf_status" : "perf", args));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_ghost","Server only: create, destroy, or list User-only ghost users (no network peer) for multi-user tests without extra processes. create makes N ghosts (default 1) named <namePrefix><n> (default Ghost1) with Index from 10001 and PlatformId 90000000000000001+n. destroy removes tracked ghosts and leftover User entities in that PlatformId range. list returns tracked ghosts (entity/index/name). Caveats: no character, not in the approved-user table, command replies vanish (FailedNotConnected), destroy before autosave unless you want a fake offline user in the save. Dev Unload also destroys them.",
  {
    action: z.enum(["create", "destroy", "list"]).describe("create N ghosts, destroy all tracked + leftover range, or list tracked ghosts."),
    count: z.number().int().min(1).max(32).optional().describe("How many to create. Default 1, max 32."),
    namePrefix: z.string().optional().describe("Name prefix; first ghost is <prefix>1. Default Ghost."),
    admin: z.boolean().optional().describe("Set User.IsAdmin. Default false."),
  },
  async ({ action, count, namePrefix, admin }) => {
    try {
      const args = { action };
      if (count != null) args.count = count;
      if (namePrefix) args.namePrefix = namePrefix;
      if (admin != null) args.admin = admin;
      return text(await bridge("server", "ghost", args));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_exec", "Compile a C# snippet into a throwaway plugin, hot-load it on one side, wait for scratch: result or scratch: error in the log, then unload it. `code` is the body of Snippet.Run(); a return statement is appended if the last line is not already one. Snippets using generic interop calls with a known process-pinning shape (WithAll<T>/WithAny<T>/WithNone<T>, GetSingleton<T>, a locally built NativeList/NativeHashMap that is added to) are refused before compiling; a `// exec: allow-generic-interop` line bypasses that. A type-initializer failure in the snippet is reported as a process pin.",
  {
    side,
    code: z.string().describe("Body of Snippet.Run(). Must end with a return statement, or 'return \"ok\";' is appended."),
    timeoutSeconds: z.number().int().min(1).max(120).optional().describe("How long to wait for scratch: result/error after deploy (default 20)."),
  },
  async ({ side, code, timeoutSeconds }) => {
    const dest = path.join(SIDES[side].reloadFolder, "Progenitor.Scratch.dll");
    let copied = false;
    try {
      const held = queueGate([side], "exec", {});
      if (held) return fail(held);
      const refusal = scanSnippetForPins(code);
      if (refusal) return { content: [{ type: "text", text: refusal }], isError: true };
      writeSnippet(code);
      const built = await run("dotnet", ["build", path.join("tools", "scratch", "Progenitor.Scratch.csproj"), "-nologo", "-v", "q"]);
      if (built.code !== 0) {
        const errLines = built.out.split(/\r?\n/).filter((l) => /error |warning CS|Build FAILED/.test(l));
        return { content: [{ type: "text", text: `compile failed (exit ${built.code})\n${(errLines.length ? errLines : built.out.split(/\r?\n/)).join("\n")}` }], isError: true };
      }

      let ping;
      try {
        ping = await bridge(side, "ping");
      } catch (e) {
        if (isExecPreflightStall(e)) {
          const snapshot = await snapshotAfterExecPreflight(
            () => bridge(side, "bridge_status"),
            () => bridge(side, "logs", { since: -1, max: 200, filter: EXEC_PREFLIGHT_LOG_FILTER }),
          );
          return { content: [{ type: "text", text: formatExecPreflightStall(e, snapshot) }], isError: true };
        }
        return fail(e);
      }
      let since = ping.logSeq ?? -1;

      fs.mkdirSync(SIDES[side].reloadFolder, { recursive: true });
      fs.copyFileSync(SCRATCH_DLL, dest);
      copied = true;

      const seconds = timeoutSeconds ?? 20;
      const deadline = Date.now() + seconds * 1000;
      const collected = [];
      let finished = false;
      while (Date.now() < deadline) {
        const r = await bridge(side, "logs", { since, max: 200, filter: "scratch" });
        since = r.latestSeq ?? since;
        for (const line of r.lines ?? []) {
          collected.push(line);
          const t = String(line.text ?? "");
          if (t.includes("scratch: result") || t.includes("scratch: error")) {
            finished = true;
            break;
          }
        }
        if (finished) break;
        await sleep(400);
      }

      if (!finished) {
        return text(`timeout after ${seconds}s waiting for scratch: result/error\n${formatLogLines(collected)}`);
      }
      return text(formatLogLines(collected));
    } catch (e) {
      return fail(e);
    } finally {
      if (copied) {
        try { fs.unlinkSync(dest); } catch { /* already gone */ }
      }
    }
  });

server.tool("progenitor_build", "dotnet build of the Progenitor solution (or one project). Returns compiler output.",
  { project: z.string().optional().describe("Project folder name under src/, e.g. Progenitor.Dev. Omit for the whole solution.") },
  async ({ project }) => {
    let target = path.join(REPO, "Progenitor.sln");
    if (project) {
      for (const dir of ["src"]) {
        const p = path.join(REPO, dir, project, `${project}.csproj`);
        if (fs.existsSync(p)) { target = p; break; }
      }
    }
    const r = await run("dotnet", ["build", target, "-nologo", "-v", "q"]);
    const lines = r.out.split(/\r?\n/).filter((l) => / error |warning CS|Build succeeded|Build FAILED/.test(l));
    return text(`exit ${r.code}\n${[...new Set(lines)].join("\n")}`);
  });

server.tool("progenitor_deploy", "Deploy selected projects to explicit client/server development targets. Reloadable plugins go to progenitor-reload; Core goes to progenitor-core and compatible Bootstrap can swap it in process. Bootstrap goes to plugins and requires restarting each affected process. Specify projects and side. Unless build=false, the script builds the whole solution. Inspect reload_status through the direct bridge and logs for completion; deployment alone is not proof of success.",
  { projects: z.array(z.string()).optional().describe("Project names; default Progenitor.Bootstrap, Progenitor.Core, Progenitor.Dev."), side: z.enum(["client", "server", "both"]).optional(), build: z.boolean().optional().describe("Run dotnet build first (default true).") },
  async ({ projects, side, build }) => {
    const refusal = queueGate(side === "client" || side === "server" ? [side] : ["client", "server"], "deploy", {});
    if (refusal) return fail(refusal);
    const list = (projects ?? ["Progenitor.Bootstrap", "Progenitor.Core", "Progenitor.Dev"]).join(",");
    const r = await ps("deploy.ps1", `-Projects ${list} -Side ${side ?? "both"} ${build === false ? "-NoBuild" : ""}`);
    if (r.code === 0) await recordLoaded(side === "client" || side === "server" ? [side] : ["client", "server"]);
    return text(`exit ${r.code}\n${r.out}`);
  });

server.tool("progenitor_test_server", "Control the staged test server (never the live one): start, stop, restart, status, or tail its log file.",
  { action: z.enum(["start", "stop", "restart", "status", "log"]), lines: z.number().int().optional(), filter: z.string().optional() },
  async ({ action, lines, filter }) => {
    const refusal = queueGate(["server"], "test_server", { action: action === "log" ? "status" : action });
    if (refusal) return fail(refusal);
    const extra = action === "log" ? `-Lines ${lines ?? 60} ${filter ? `-Filter '${filter.replace(/'/g, "''")}'` : ""}` : "";
    const r = await ps("test-server.ps1", `${action} ${extra}`);
    return text(`exit ${r.code}\n${r.out}`);
  });

server.tool("progenitor_logfile", "Tail a side's BepInEx LogOutput.log from disk (fallback when the bridge is not up, e.g. during boot or after a crash).",
  { side, lines: z.number().int().min(1).max(2000).optional(), filter: z.string().optional() },
  async ({ side, lines, filter }) => {
    try {
      const f = SIDES[side].logFile;
      let all = fs.readFileSync(f, "utf8").split(/\r?\n/);
      if (filter) { const re = new RegExp(filter, "i"); all = all.filter((l) => re.test(l)); }
      return text(all.slice(-(lines ?? 80)).join("\n"));
    } catch (e) { return fail(e); }
  });


server.tool("progenitor_query", "Find entities on one side by component names (e.g. ['PlayerCharacter'] or ['ItemData','Prefab']). Optional none list, limit (default 50), includeDisabled (default true), near {x,y,z,radius}. Returns entity ids, prefab GUID and name, position.",
  { side, all: z.array(z.string()), none: z.array(z.string()).optional(), limit: z.number().int().optional(), includeDisabled: z.boolean().optional(), near: z.object({ x: z.number(), y: z.number(), z: z.number(), radius: z.number() }).optional() },
  async ({ side, ...args }) => { try { return text(await bridge(side, "query", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_get", "Read one component of an entity as JSON fields. entity is 'index:version' from progenitor_query; component is a type name like 'Health' or 'Unity.Transforms.Translation'.",
  { side, entity: z.string(), component: z.string() },
  async ({ side, ...args }) => { try { return text(await bridge(side, "get", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_set", "Write listed fields of one component on an entity (primitives, Entity {index,version}, PrefabGUID int, float3 {x,y,z}). Returns the component after the write.",
  { side, entity: z.string(), component: z.string(), fields: z.record(z.any()) },
  async ({ side, ...args }) => { try { return text(await bridge(side, "set", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_components", "List the component type names on an entity.",
  { side, entity: z.string() },
  async ({ side, ...args }) => { try { return text(await bridge(side, "components", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_spawn", "Server only: instantiate a prefab by name or GUID hash at a position (count default 1). Returns new entity ids. Units may need extra setup to behave.",
  { prefab: z.union([z.string(), z.number().int()]), x: z.number(), y: z.number(), z: z.number(), count: z.number().int().optional() },
  async (args) => { try { return text(await bridge("server", "spawn", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_prefab", "Look a prefab up in PrefabCollectionSystem._PrefabGuidToEntityMap without spawning it. prefab is a spawnable name or integer GUID hash (same three lookups as spawn). Returns guid, name, the collection entity id, and component type names. Optional component adds a get-style field dump of that type.",
  {
    side,
    prefab: z.union([z.string(), z.number().int()]).describe("Spawnable name or integer PrefabGUID hash."),
    component: z.string().optional().describe("Optional component type name to dump fields for, same as progenitor_get."),
  },
  async ({ side, prefab, component }) => {
    try {
      const args = { prefab };
      if (component) args.component = component;
      return text(await bridge(side, "prefab", args));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_heal", "Server only: set Health.Value to Health.MaxHealth._Value (read from the component, never a hardcoded 125) and Blood.Value to Blood.MaxBlood._Value (100 when MaxBlood reads 0), and clear Health.IsDead. user is a character name; entity is index:version of a character (or User). Returns before/after health and blood, and downed {found, removed, buff, guid}. DOWNED: heal restores a downed player in place (fills health first, then removes Buff_General_Vampire_Wounded_Buff; other buffs stay). DEAD (ProjectM.Dead): refused; use the bridge respawn op, then progenitor_teleport back, then heal. Do not revive a character a person is playing unless they ask.",
  {
    user: z.string().optional().describe("Character name from progenitor_users. entity wins when both are set."),
    entity: z.string().optional().describe("Character entity 'index:version'. A User entity uses LocalCharacter."),
  },
  async ({ user, entity }) => {
    try {
      const args = {};
      if (user) args.user = user;
      if (entity) args.entity = entity;
      return text(await bridge("server", "heal", args));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_teleport", "Server only: move a character. Destination is either x/y/z or to (another character's name) / toEntity ('index:version'); toEntity wins over to, which wins over the point. Subject is user (character name) or entity ('index:version'); entity wins. Goes through the game's own ProjectM.TeleportUtilityServer.Teleport, falling back to writing Translation + LastTranslation + PlayerLastValidPosition if that helper is gone - the response's method says which ran. The response is deferred one second so after is the live position and arrived says whether the move stuck.",
  {
    user: z.string().optional().describe("Character name from progenitor_users. entity wins when both are set."),
    entity: z.string().optional().describe("Character entity 'index:version'. A User entity uses LocalCharacter."),
    x: z.number().optional(), y: z.number().optional(), z: z.number().optional(),
    to: z.string().optional().describe("Another character's name to teleport onto."),
    toEntity: z.string().optional().describe("Another entity 'index:version' to teleport onto. Wins over to."),
  },
  async (args) => {
    try {
      const body = {};
      for (const [k, v] of Object.entries(args)) if (v != null) body[k] = v;
      return text(await bridge("server", "teleport", body));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_give", "Server only: add an item stack to a character's inventory through the game's own ServerGameManager.TryAddInventoryItem. prefab is a spawnable name or integer PrefabGUID hash (same lookups as progenitor_spawn); amount defaults to 1 (max 10000). Returns requested / added / didNotFit plus the AddItemResponse result, slot and item entity. A prefab that is not in GameDataSystem.ItemHashLookupMap (the usual case for an unregistered clone) is an error, not a silent zero.",
  {
    user: z.string().optional().describe("Character name from progenitor_users. entity wins when both are set."),
    entity: z.string().optional().describe("Character entity 'index:version'. A User entity uses LocalCharacter; a chest or station also works."),
    prefab: z.union([z.string(), z.number().int()]).describe("Spawnable name (Item_Ingredient_Stone) or integer PrefabGUID hash."),
    amount: z.number().int().optional().describe("Default 1, clamped to 1..10000."),
  },
  async (args) => {
    try {
      const body = {};
      for (const [k, v] of Object.entries(args)) if (v != null) body[k] = v;
      return text(await bridge("server", "give", body));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_unlock", "Server only, isolated test server only (refused under C:\\VRisingServer). TEST SETUP: unlock build-menu blueprints and crafting recipes for one player, including prefabs mods add. status (default, read only): UnlockedProgressionElement / UnlockedBlueprintElement / UnlockedRecipeElement counts, blueprint and recipe prefab totals, and which are still locked (capped by limit). all: every BlueprintData and RecipeData prefab; prefabs: the listed names or GUID hashes. Both add the GUIDs to UnlockedProgressionElement and raise the game's UnlockProgressionDebugEvent once so its handler rebuilds the derived buffers; the GUIDs added are recorded in BepInEx/config/progenitor-unlock-<user>.json. lock removes listed GUIDs from UnlockedProgressionElement (a GUID a remaining tech grants stays unlocked); revert removes exactly the recorded GUIDs and deletes the record. dryRun plans without writing. Writes return after a one-second read-back. `all` and `status` ran live 2026-10-04 (a connected client sees the result after rejoining); `lock` and `revert` not run live.",
  {
    user: z.union([z.string(), z.number().int()]).describe("Character name from progenitor_users, or the User.Index as a number."),
    action: z.enum(["status", "all", "prefabs", "lock", "revert"]).optional().describe("Default status."),
    prefabs: z.array(z.union([z.string(), z.number().int()])).optional().describe("Prefab names or integer PrefabGUID hashes. Required by prefabs and lock; refused by the other actions."),
    dryRun: z.boolean().optional().describe("Plan and report without writing anything."),
    limit: z.number().int().min(0).max(1000).optional().describe("Most GUIDs listed per list in the result. Default 50."),
  },
  async (args) => {
    try {
      const body = {};
      for (const [k, v] of Object.entries(args)) if (v != null) body[k] = v;
      return text(await bridge("server", "unlock", body));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_inspect", "Read a whole entity in one call: every component with its decoded fields, instead of progenitor_components followed by one progenitor_get per type. depth decodes that many elements of each buffer (default 0 = names and lengths only, max 256); max is the decoded-byte budget (default 65536, max 1048576) and hitting it sets truncated with truncatedAt naming where to resume; only limits decoding to the listed component names (the rest are still listed). A component that cannot be decoded gets a skipped reason rather than failing the call.",
  {
    side,
    entity: z.string().describe("'index:version' from progenitor_query or progenitor_users."),
    depth: z.number().int().min(0).max(256).optional().describe("Buffer elements decoded per buffer. Default 0."),
    max: z.number().int().optional().describe("Decoded-byte budget. Default 65536."),
    only: z.array(z.string()).optional().describe("Component names to decode (simple or full)."),
  },
  async ({ side, ...args }) => { try { return text(await bridge(side, "inspect", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_snapshot", "Copy one entity's unmanaged component blobs out as base64 plus the decoded view, so an experiment can be undone with progenitor_restore. NOT A SAVE: one entity, one world generation, unmanaged IComponentData only - buffers, managed and shared components, the archetype and anything the entity points at are not captured, and Entity fields inside a restored blob only mean anything while that other entity still lives at the same index:version. Use progenitor_world_save for a real save. Hand the whole result back to progenitor_restore as snapshot.",
  {
    side,
    entity: z.string().describe("'index:version'."),
    max: z.number().int().optional().describe("Raw-byte budget. Default 65536, max 1048576."),
    only: z.array(z.string()).optional().describe("Component names to capture."),
  },
  async ({ side, ...args }) => { try { return text(await bridge(side, "snapshot", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_restore", "Write a progenitor_snapshot's raw bytes back onto an entity. Fail-closed: a component is written only if it is still on the entity, still unmanaged IComponentData, and still exactly the byte size the snapshot recorded; a size mismatch, a managed/shared/buffer component and a component that is now missing are all refused with a reason. With strict (the default) one refusal aborts the whole call and writes nothing; strict:false writes what still matches and reports the rest. Logs one line per component actually written.",
  {
    side,
    entity: z.string().describe("'index:version' to write onto - usually the same entity the snapshot came from."),
    snapshot: z.record(z.any()).describe("The whole object progenitor_snapshot returned."),
    strict: z.boolean().optional().describe("Default true: refuse the whole restore if any component fails."),
  },
  async ({ side, ...args }) => { try { return text(await bridge(side, "restore", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_players", "Player characters on one side, including offline ones: entity id, name, position.",
  { side },
  async ({ side }) => { try { return text(await bridge(side, "players", {})); } catch (e) { return fail(e); } });

server.tool("progenitor_status", "Framework status on one side: version, uptime, module snapshots (prefabs, hosted launcher, persistence directory), and bridgeUsers (current and recent AI sessions on this bridge).",
  { side },
  async ({ side }) => { try { return withUsers(await bridge(side, "status")); } catch (e) { return fail(e); } });

server.tool("progenitor_save", "Flush every in-memory ModSave store to disk now (server only; the client op errors). Returns { ok, dir }. Does not write a world .save.gz — use progenitor_world_save for that.",
  { side },
  async ({ side }) => { try { return text(await bridge(side, "save")); } catch (e) { return fail(e); } });

server.tool("progenitor_world_save", "Server only: request a real world save the same way the game does (TriggerPersistenceSaveSystem.TriggerSave; falls back to creating a SavePersistentDataEvent entity if that call throws), then flush ModSave JSON so both land together. The progenitor_save tool only flushes mod JSON. Returns { ok, method, fileName, note }. Live check: a new WorldSave.save.gz (or AutoSave_N.save.gz) under save-data/Saves/v4/test1 plus 'ModSave: wrote' in the log.",
  {
    side,
    fileName: z.string().optional().describe("Save leaf ending in .save. Default WorldSave.save. A trailing .gz is stripped; TriggerSave appends it when CompressSaveFiles is on."),
    reason: z.string().optional().describe("SaveReason: ManualSave (default), AutoSave, or QuickSave."),
  },
  async ({ side, fileName, reason }) => {
    try {
      const args = {};
      if (fileName) args.fileName = fileName;
      if (reason) args.reason = reason;
      return text(await bridge(side, "world_save", args));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_store", "Read or write one mod's ModSave key-value bag. Omit key to list keys; pass key to get; pass key and value to set.",
  {
    side,
    mod: z.string().describe("Mod GUID used as the store file name (e.g. progenitor)."),
    key: z.string().optional().describe("Store key. Omit to list keys for the mod."),
    value: z.any().optional().describe("JSON value to store. Omit to get or list."),
  },
  async ({ side, ...args }) => { try { return text(await bridge(side, "store", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_connect", "Client only: from the main menu, join a dedicated server the same way Direct Connect does (GameHelper.LaunchGame). Default port 9976 (the staged test server). Fails if already in a world. Join is async; watch progenitor_ping for Client_0.",
  {
    address: z.string().describe("IPv4 address, e.g. 127.0.0.1."),
    port: z.number().int().min(1).max(65535).optional().describe("Game port. Default 9976 (test server). Query port 9977 is not the connect port."),
    password: z.string().optional().describe("Join password. Omit when the server has none."),
  },
  async ({ address, port, password }) => {
    try {
      const args = { address, port: port ?? 9976 };
      if (password) args.password = password;
      return text(await bridge("client", "connect", args));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_host", "Client only: from the main menu, start a private (hosted) game the same way Private Game / Host New does (GameHelper.LaunchGame with ShouldHost). Default saveName progenitor-hosted, lan true. Fails if already in a world. Spawn and join are async; watch progenitor_ping for Client_0. Exercises Core's Hosted launcher.",
  {
    saveName: z.string().optional().describe("Save / world folder name. Default progenitor-hosted. Re-using a name continues that world if it already exists."),
    password: z.string().optional().describe("Host password. Omit for none. Also used by the hosting client to join the child server."),
    lan: z.boolean().optional().describe("LAN mode for the child server (-lanMode). Default true. GameConnect has no LAN field; this is poked onto in-memory ServerHostSettings."),
  },
  async ({ saveName, password, lan }) => {
    try {
      const args = { saveName: saveName ?? "progenitor-hosted", lan: lan ?? true };
      if (password) args.password = password;
      return text(await bridge("client", "host", args));
    } catch (e) { return fail(e); }
  });

server.tool("progenitor_disconnect", "Client only: request the production Leave-to-menu transition without focus or OS input. Returns pending, not success of leaving; poll statusOnly=true until verified (no client world and visible MainMenu). Repeated pending calls do not reissue. Native path verified offline; live acceptance pending.",
  { statusOnly: z.boolean().optional().describe("Observe only; never initiate leaving. Default false.") },
  async ({ statusOnly }) => {
    try { return text(await bridge("client", "disconnect", { statusOnly: statusOnly ?? false })); } catch (e) { return fail(e); }
  });

server.tool("progenitor_quit", "Request UnityEngine.Application.Quit on the given side (client or server). Returns {ok:true} immediately; the process exits after the current frame. Core's Application.quitting handler promotes a staged Progenitor.dll.next. Logs 'quit requested via bridge'. Does not refuse.",
  { side },
  async ({ side }) => {
    try { return text(await bridge(side, "quit")); } catch (e) { return fail(e); }
  });

server.tool("progenitor_castles", "Server-oriented castle reads: plots (territory index, bounds, owner, clan, level, raided, decaying), tiles (row-major floor cells with floor type including treasury, room, wall/door bits), or buildings (prefab, footprint, rotation, kind). Block coords are skipped until a live GetBuffer probe. plot is a territory index; omit it to list every plot (tiles/buildings walk each plot up to limit).",
  {
    side,
    what: z.enum(["plots", "tiles", "buildings", "index"]).optional().describe("Which read. Default plots. index: the castle container index counters (scans, hits, why each scan ran)."),
    plot: z.number().int().optional().describe("CastleTerritoryIndex. Omit to include every plot."),
    limit: z.number().int().optional().describe("Max rows returned. Default 200, hard cap 2000."),
  },
  async ({ side, ...args }) => { try { return text(await bridge(side, "castles", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_time", "Server world speed over the game's replicated TimeScale (the same leases as ctx.Time.Freeze / SlowMotion). status: value (1 normal, 0 frozen), paused, engineScale, every live lease with its owner (the newest governs). speed {value in [0.001, 1]}: the bridge holds that speed. freeze: the bridge holds 0. resume / release: drop the bridge's own lease (the previous lease governs, else the speed from before the first lease, normally 1, returns). The bridge keeps one lease; Core unload, a world load and Dev unload release it. Every action returns the status after it. Affects every connected player.",
  {
    action: z.enum(["status", "speed", "freeze", "resume", "release"]).optional().describe("Default status."),
    value: z.number().optional().describe("speed only: world speed in [0.001, 1]; 0.25 is quarter speed. Use freeze for 0."),
  },
  async (args) => { try { return text(await bridge("server", "time", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_servants", "Server only. Castle servants and hunts (ctx.Servants through the Dev bridge). Name the castle by territory (index) or heart (index:version). list: the throne's servants (row #n, name, state, injury end, proficiency, perks, item stacks) and the heart's hunts (mission, zone x,y#z, length slot, start/end, servants). zones: zones with a servant mission the owner (or user) discovered (all:true also undiscovered), with active (a hunt of this castle is out there), servantSlots, and the hunt length slots. send: servants (names, #n rows or index:version; omitted = the free servants) on mission (GUID) or zone (x,y#z); neither picks the first discovered zone with no hunt and room; slot default 0. Returns the send or the refusal reason. finish: make the castle's hunts (or one mission) end now so the game returns them on its next update and rolls each servant's success there (servants can die or come back injured). method timers (default): turns the game's ServantTimersDisabled debug setting on until those hunts are gone, then off again (server-wide: every hunt on the server returns); method row: moves only those rows' start time back. timers: read, or off:true|false to set ServantTimersDisabled (server-wide; new sends last 1e-5 of their length; the game saves it with the world). empty: move servants' inventories (servants, default every servant at home) into the castle's ordinary containers, or into `to`, with the inventory engine's TransferAll (conserving; never destroys); reports moved/left per servant and 'no room' for what did not fit. discard:true (test only) removes what did not fit and logs a loud warning. Built, not run live.",
  {
    action: z.enum(["list", "zones", "send", "finish", "timers", "empty", "chance", "caps"]).optional().describe("Default list. chance: the send's arguments; each servant's success chance and whether the send would be accepted, no writes. caps: the success cap's status (book loaded, caps, forced failures, restores; lateRestores must stay 0), no castle."),
    maxSuccess: z.number().min(0).max(1).optional().describe("send: success cap 0..1 for the hunt (a capped hunt can kill a servant the game would have brought home). chance: preview only."),
    territory: z.number().int().optional().describe("Castle territory index (or give heart)."),
    heart: z.string().optional().describe("Castle heart (or anything connected to it) as index:version."),
    servants: z.union([z.array(z.string()), z.string()]).optional().describe("send: up to 3 servants: names, #n rows of list, or index:version; omitted = the castle's free servants."),
    mission: z.number().int().optional().describe("send/finish: mission prefab GUID hash. finish without it ends every hunt of the castle."),
    zone: z.string().optional().describe("send: map zone x,y#zone as list/zones print it."),
    slot: z.number().int().optional().describe("send: hunt length choice (zones lists durationSlots). Default 0."),
    user: z.string().optional().describe("zones/send: user entity index:version whose map discovery is checked. Default the heart's owner."),
    checkDiscovered: z.boolean().optional().describe("send: check discovery (default true)."),
    all: z.boolean().optional().describe("zones: also list undiscovered zones."),
    method: z.enum(["timers", "row"]).optional().describe("finish: timers (default; the game's debug setting, server-wide) or row (rewrite only the matching rows' start)."),
    off: z.boolean().optional().describe("timers: true disables servant timers, false enables them; omit to read."),
    to: z.string().optional().describe("empty: target container of this castle as index:version. Default the castle's ordinary containers."),
    discard: z.boolean().optional().describe("empty, TEST ONLY: remove what did not fit (logged as a warning). Default false: nothing is ever removed."),
  },
  async (args) => { try { return text(await bridge("server", "servants", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_inventory_stamp", "Server only, read only. ctx.Inventory.ChangeStamp of the named containers in one batch: per container the resolved inventory entity and the stamp (hex; equal = slots unchanged, unequal = may have changed), the 64-bit clock, and with since (a clock from an earlier reply) whether AnyChangedSince says any changed. Also the stamp service's counters: misses (audit saw content change under an unchanged stamp; must stay 0), audits, freshStamps (reads that failed), resolves, entries, typeScans and failures by raw-read check. Source-checked and unit-tested, not live-verified.",
  {
    containers: z.union([z.array(z.string()), z.string()]).describe("Owner ids index:version (chest, station, heart, player character, servant); append #1 or /out for a refinement station's output. Array or comma list."),
    since: z.number().int().optional().describe("A clock value from an earlier reply: also report AnyChangedSince(containers, since)."),
  },
  async (args) => { try { return text(await bridge("server", "inventory_stamp", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_photo", "Photo mode (built-in, off by default): fly camera, HUD hide, admin-only world pause. on/off run on the client: on enters photo mode (enable:true first turns [PhotoMode] Enabled on in Core's ui cfg), off leaves it and resumes any pause it asked for. status (either side): enabled, active, paused, hudHidden, pose, speed, fov, holdsInput; on the server, serverPauses (users holding a pause). The pause itself is the P keybind (keybind_press PhotoModePause) and shows up in progenitor_time status as a 'photo mode: <name>' lease. Takes the camera and holds the character while active.",
  {
    side: z.enum(["client", "server"]).optional().describe("Default client. Use server only for status."),
    action: z.enum(["status", "on", "off", "lock", "unlock"]).optional().describe("Default status. lock/unlock: photo-mode lock-on (client, photo mode active)."),
    enable: z.boolean().optional().describe("on only: turn [PhotoMode] Enabled on first."),
    entity: z.string().optional().describe("lock only: client-world entity 'index:version' of a living unit."),
  },
  async ({ side, ...args }) => { try { return text(await bridge(side ?? "client", "photo", args)); } catch (e) { return fail(e); } });

server.tool("progenitor_camera_preview", "Client only, read-only. The newest running ctx.Camera.Preview (any plugin): active, held, point, anchor (the orbit anchor last written), yaw, screenOffset and wantedScreenX (0.5 + offset), screenX (where the previewed point projects on the live camera now, as a share of the screen width), viewport (x, y, depth), error (screenX - wantedScreenX), and the framing's readings: measured, camera (name), fov, aspect, rect (x, width), depth, viewportTarget, lookAtViewportX, oneMeterRightViewportX, lateral (meters the point sits right of the look-at line), pinholeLateral (the ideal-pinhole model's value, for comparison) and rightAgreement (the camera's right axis against the yaw model; about 1 or -1). { active: false } when no preview runs.",
  {},
  async () => { try { return text(await bridge("client", "camera_preview", {})); } catch (e) { return fail(e); } });

server.tool("progenitor_locate", "Client only. ctx.Hud.Locate pings through the Dev bridge (the same presenter plugins use). pin: a ping at world point x, y (up), z in the game's meters (Translation), owned by the bridge; label, seconds (default 30, 0 = until cleared), arrival (meters, 3D, default 0 = never) and maps (world map and minimap diamonds). status (default): every live ping (any owner) with its last drawn frame: labelText, meters, from (the character position the distance was measured from) and character (its entity), onScreen, screen (pixels, origin bottom-left), viewport, angleDegrees, screenSize, cameraPixels, camera (name, position, fov, aspect) and pinholeViewport (a plain projection of the same point from the camera pose; it should match viewport). Frames are recorded only for 15 s after any locate call: read status a frame or more after pin (or after a first status). pin refuses (ok:false, cap reached) while 8 pings are live. clear: end the bridge's pings.",
  {
    action: z.enum(["status", "pin", "clear"]).optional().describe("Default status."),
    x: z.number().optional().describe("pin: world x (meters)."),
    y: z.number().optional().describe("pin: world y, up (meters). The marker floats 1.6 m above it."),
    z: z.number().optional().describe("pin: world z (meters)."),
    label: z.string().optional().describe("pin: the label before the distance."),
    seconds: z.number().min(0).optional().describe("pin: lifetime, default 30; 0 = until cleared."),
    arrival: z.number().min(0).optional().describe("pin: end the ping when the character is within this many meters (3D). Default 0 = never."),
    maps: z.boolean().optional().describe("pin: also show it on the world map and minimap."),
  },
  async (args) => { try { return text(await bridge("client", "locate", args)); } catch (e) { return fail(e); } });

// BRIDGEUI1: focus-free UI acceptance tools (ui_input, ui_read, nav, display, screenshot). Implementations live in
// ui-tools.mjs; a load error must not block the other tools.
{
  try {
    const ui = await import("./ui-tools.mjs");
    ui.registerUiTools(server, { z, bridge, text, fail });
  } catch (e) {
    console.error(`[progenitor-mcp] ui tools not registered: ${e?.message ?? e}`);
  }
}

// ---------------------------------------------------------------------------
// Offline catalog recipe tools. Implementations live in catalog-tools.mjs;
// this block only registers them. Other agents own the rest of this file —
// do not fold these tools into the bridge registrations above, and do not
// edit this block from a non-catalog job. Restart the MCP process to pick
// the tools up. A catalog-tools load error must not block progenitor_ping.
// ---------------------------------------------------------------------------
{
  try {
    const catalog = await import("./catalog-tools.mjs");
    catalog.registerCatalogTools(server, { text, fail });
  } catch (e) {
    console.error(`[progenitor-mcp] catalog tools not registered: ${e?.message ?? e}`);
  }
}

// Offline knowledge-note tools (knowledge/README.md). Implementations live in kb-tools.mjs.
{
  try {
    const kb = await import("./kb-tools.mjs");
    kb.registerKbTools(server, { text, fail });
  } catch (e) {
    console.error(`[progenitor-mcp] kb tools not registered: ${e?.message ?? e}`);
  }
}

const transport = new StdioServerTransport();
await server.connect(transport);
