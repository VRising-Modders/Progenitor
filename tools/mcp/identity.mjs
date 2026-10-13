// Who is calling the dev bridge: which AI (Claude, Codex, Grok, other), the session name and the
// session ID. server.mjs and call.mjs send the result as the `who` object on every bridge request;
// the Dev bridge keeps the current user and the last three previous ones (docs/bridge.md,
// "Bridge users"). Detection is best effort and never throws: an undetected field is empty.
//
// Order, first match wins for the AI kind:
//   1. PROGENITOR_AGENT / PROGENITOR_SESSION_NAME / PROGENITOR_SESSION_ID (explicit, always win)
//   2. Claude Code: CLAUDECODE / CLAUDE_CODE_SESSION_ID. Name from the desktop app's session file
//      (CLAUDE_CODE_HOST_SESSION_ID) or the last custom-title line of the session transcript.
//   3. Codex: CODEX_THREAD_ID (set for commands Codex runs). Name from CODEX_HOME/session_index.jsonl.
//   4. Grok: GROK_SESSION_ID, or the parent process ID listed in GROK_HOME/active_sessions.json.
//      Name from the session's summary.json. GROK_CC_* variables come from a Claude plugin and are
//      ignored; Claude markers win over everything Grok-shaped.
//   5. The MCP client name from `initialize` (claude-code, codex-mcp-client, ...).
import fs from "node:fs";
import path from "node:path";

export const REFRESH_MS = 60_000;
const MAX_FIELD = 120;
const TAIL_STEPS = [256 * 1024, 4 * 1024 * 1024];

const clip = (s) => {
  if (s == null) return "";
  const t = String(s).replace(/[\u0000-\u001f\u007f]/g, " ").trim();
  return t.length > MAX_FIELD ? t.slice(0, MAX_FIELD) : t;
};

const home = (env) => env.USERPROFILE || env.HOME || "";
/** path.join under the user's home, or "" when the environment names no home (never the cwd). */
const underHome = (env, ...parts) => (home(env) ? path.join(home(env), ...parts) : "");

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, "utf8")); } catch { return null; }
}

function listDirs(dir) {
  try { return fs.readdirSync(dir, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => path.join(dir, d.name)); }
  catch { return []; }
}

/** Last JSON line in the file's tail whose text contains `marker` and passes `accept`. Reads at most 4 MiB. */
export function lastJsonLine(file, marker, accept = () => true) {
  let fd;
  try {
    fd = fs.openSync(file, "r");
    const size = fs.fstatSync(fd).size;
    for (const step of TAIL_STEPS) {
      const len = Math.min(size, step);
      const buf = Buffer.alloc(len);
      fs.readSync(fd, buf, 0, len, size - len);
      const lines = buf.toString("utf8").split("\n");
      if (len < size) lines.shift(); // first line is partial
      for (let i = lines.length - 1; i >= 0; i--) {
        if (!lines[i].includes(marker)) continue;
        try {
          const obj = JSON.parse(lines[i]);
          if (accept(obj)) return obj;
        } catch { /* partial or foreign line */ }
      }
      if (len === size) break;
    }
  } catch { /* missing or unreadable */ }
  finally { if (fd !== undefined) try { fs.closeSync(fd); } catch { /* ignore */ } }
  return null;
}

function claudeDesktopSession(env, hostId) {
  if (!hostId || !/^[\w.-]+$/.test(hostId)) return null;
  const appData = env.APPDATA || underHome(env, "AppData", "Roaming");
  if (!appData) return null;
  const root = path.join(appData, "Claude", "claude-code-sessions");
  for (const account of listDirs(root)) {
    for (const org of listDirs(account)) {
      const j = readJson(path.join(org, `${hostId}.json`));
      if (j) return j;
    }
  }
  return null;
}

function claudeTranscriptTitle(env, sessionId) {
  if (!sessionId || !/^[\w-]+$/.test(sessionId)) return "";
  const configDir = env.CLAUDE_CONFIG_DIR || underHome(env, ".claude");
  if (!configDir) return "";
  for (const project of listDirs(path.join(configDir, "projects"))) {
    const file = path.join(project, `${sessionId}.jsonl`);
    if (!fs.existsSync(file)) continue;
    const line = lastJsonLine(file, '"custom-title"', (o) => o?.type === "custom-title" && o.customTitle);
    return line ? line.customTitle : "";
  }
  return "";
}

function codexThreadName(env, threadId) {
  if (!threadId) return "";
  const codexHome = env.CODEX_HOME || underHome(env, ".codex");
  if (!codexHome) return "";
  const line = lastJsonLine(path.join(codexHome, "session_index.jsonl"), threadId, (o) => o?.id === threadId && o.thread_name);
  return line ? line.thread_name : "";
}

function grokHome(env) {
  return env.GROK_HOME || underHome(env, ".grok");
}

function grokSessionForPid(env, pid) {
  if (!pid || !grokHome(env)) return "";
  const list = readJson(path.join(grokHome(env), "active_sessions.json"));
  if (!Array.isArray(list)) return "";
  const hit = list.find((s) => s && Number(s.pid) === Number(pid) && s.session_id);
  return hit ? String(hit.session_id) : "";
}

function grokSummary(env, sessionId) {
  if (!sessionId || !/^[\w-]+$/.test(sessionId) || !grokHome(env)) return null;
  for (const cwdDir of listDirs(path.join(grokHome(env), "sessions"))) {
    const j = readJson(path.join(cwdDir, sessionId, "summary.json"));
    if (j) return j;
  }
  return null;
}

/** AI kind implied by an MCP client name, or "". */
export function aiFromClientName(name) {
  const n = String(name ?? "").toLowerCase();
  if (n.includes("claude")) return "claude";
  if (n.includes("codex")) return "codex";
  if (n.includes("grok")) return "grok";
  return "";
}

/**
 * Detect the calling AI session. Pure apart from reading the files the environment points at.
 * @param {object} o
 * @param {Record<string,string|undefined>} [o.env]   environment (default process.env)
 * @param {string} [o.tool]       "mcp" or "call.mjs"
 * @param {number} [o.pid]        this process
 * @param {number} [o.ppid]       parent process (Grok session lookup)
 * @param {{name?:string,version?:string}} [o.clientInfo]  MCP initialize clientInfo
 * @param {{ai?:string,name?:string,id?:string}} [o.overrides]  progenitor_whoami settings
 */
export function detectIdentity({ env = process.env, tool = "", pid = process.pid, ppid = process.ppid, clientInfo = null, overrides = null } = {}) {
  const who = { ai: "", name: "", id: "", client: "", tool, pid, model: "" };
  try {
    if (clientInfo?.name) who.client = clip(`${clientInfo.name}${clientInfo.version ? " " + clientInfo.version : ""}`);

    // The innermost host wins: Codex and Grok set their markers for the commands they run, while
    // Claude's variables are inherited by anything a Claude session starts (including Codex or Grok).
    const grokId = env.GROK_SESSION_ID || grokSessionForPid(env, ppid);
    if (env.CODEX_THREAD_ID || env.CODEX_SESSION_ID) {
      who.ai = "codex";
      who.id = env.CODEX_THREAD_ID || env.CODEX_SESSION_ID;
      who.name = codexThreadName(env, who.id);
    } else if (grokId) {
      who.ai = "grok";
      who.id = grokId;
      const s = grokSummary(env, grokId);
      if (s) {
        who.name = s.generated_title || s.session_summary || "";
        who.model = s.current_model_id || "";
      }
    } else if (env.CLAUDECODE === "1" || !!env.CLAUDE_CODE_SESSION_ID) {
      who.ai = "claude";
      who.id = env.CLAUDE_CODE_SESSION_ID || "";
      if (!who.client) who.client = env.AI_AGENT || (env.CLAUDE_CODE_ENTRYPOINT ? `claude-code ${env.CLAUDE_CODE_ENTRYPOINT}` : "claude-code");
      const desktop = claudeDesktopSession(env, env.CLAUDE_CODE_HOST_SESSION_ID);
      if (desktop) {
        who.name = desktop.title || "";
        who.model = desktop.model || "";
        if (!who.id && desktop.cliSessionId) who.id = desktop.cliSessionId;
      }
      if (!who.name) who.name = claudeTranscriptTitle(env, who.id);
    }
    if (!who.ai) who.ai = aiFromClientName(clientInfo?.name);
  } catch { /* detection must never fail a call */ }

  if (env.PROGENITOR_AGENT) who.ai = env.PROGENITOR_AGENT;
  if (env.PROGENITOR_SESSION_NAME) who.name = env.PROGENITOR_SESSION_NAME;
  if (env.PROGENITOR_SESSION_ID) who.id = env.PROGENITOR_SESSION_ID;
  if (overrides?.ai) who.ai = overrides.ai;
  if (overrides?.name) who.name = overrides.name;
  if (overrides?.id) who.id = overrides.id;

  for (const k of ["ai", "name", "id", "client", "tool", "model"]) who[k] = clip(who[k]);
  if (!who.ai) who.ai = "unknown";
  return who;
}

/** The `who` object sent on the wire: empty fields dropped. */
export function wireWho(who) {
  const out = {};
  for (const [k, v] of Object.entries(who ?? {})) {
    if (v === "" || v == null) continue;
    out[k] = v;
  }
  return out;
}

/** Cached identity, re-detected at most every REFRESH_MS (a session title can change). */
export function createIdentity({ tool, env = process.env, now = () => Date.now(), getClientInfo = () => null } = {}) {
  let cached = null;
  let at = 0;
  let overrides = {};
  return {
    get() {
      if (!cached || now() - at >= REFRESH_MS) {
        cached = detectIdentity({ env, tool, clientInfo: getClientInfo(), overrides });
        at = now();
      }
      return cached;
    },
    /** Sets or clears (empty string) caller overrides for the rest of this process. */
    override(o) {
      for (const k of ["ai", "name", "id"]) {
        if (o?.[k] === undefined) continue;
        if (o[k] === "") delete overrides[k];
        else overrides[k] = clip(o[k]);
      }
      cached = null;
      return this.get();
    },
    overrides: () => ({ ...overrides }),
  };
}

/** "Codex session "Example" (id x)" */
export function describeUser(u) {
  const ai = u?.ai && u.ai !== "unknown" ? u.ai[0].toUpperCase() + u.ai.slice(1) : "Unknown";
  let s = `${ai} session`;
  if (u?.name) s += ` "${u.name}"`;
  if (u?.id) s += ` (id ${u.id})`;
  else if (u?.tool) s += ` (${u.tool}${u.pid ? ` pid ${u.pid}` : ""})`;
  return s;
}

const age = (sec) => (sec < 60 ? `${Math.max(0, Math.round(sec))} s` : `${Math.round(sec / 60)} min`);

/** The sharing note for a previous bridge user, or "" when it is old or the same session. */
export function sharingNote(side, prev, me, windowSeconds = 600) {
  if (!prev || typeof prev !== "object") return "";
  const sec = Number(prev.ageSeconds ?? Infinity);
  if (!(sec <= windowSeconds)) return "";
  if (me?.id && prev.id && me.id === prev.id) return "";
  const op = prev.lastOp ? ` (last op: ${prev.lastOp})` : "";
  return `Note: ${describeUser(prev)} used the ${side} bridge ${age(sec)} ago${op}.`;
}

/** One-line summary of a bridgeUsers block for ping/status. */
export function formatBridgeUsers(bu) {
  if (!bu || typeof bu !== "object") return "";
  const lines = [];
  if (bu.current) lines.push(`Bridge user now: ${describeUser(bu.current)}, ${bu.current.requests ?? 0} requests, last op ${bu.current.lastOp || "?"}`);
  for (const r of Array.isArray(bu.recent) ? bu.recent : []) {
    lines.push(`  before: ${describeUser(r)}, ${age(r.ageSeconds ?? 0)} ago, last op ${r.lastOp || "?"}`);
  }
  return lines.join("\n");
}
