#!/usr/bin/env node
// Direct debug-bridge call without MCP: node tools/mcp/call.mjs <client|server> <op> [jsonArgs]
// Same wire protocol as server.mjs (one JSON object per line, token from the side's token file).
// Timeout: PROGENITOR_CALL_TIMEOUT_MS (this call) beats PROGENITOR_TIMEOUT_MS (the whole shell),
// default 30000. Both bound connect AND reply, which is what a wedged bridge needs.
import net from "node:net";
import fs from "node:fs";
import { execFileSync } from "node:child_process";
import path from "node:path";
import { requireWorkspacePath } from "../common/workspace.mjs";
import { detectIdentity, wireWho, sharingNote } from "./identity.mjs";
import { createQueue } from "./queue.mjs";
import { asciiJson, asciiText } from "./ascii-json.mjs";

const GAME_DIR = process.env.PROGENITOR_GAME_DIR ?? "C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRising";
// <workspace>/_testserver unless PROGENITOR_TEST_ROOT says otherwise (tools/common/workspace.mjs).
const TEST_ROOT = process.env.PROGENITOR_TEST_ROOT ?? requireWorkspacePath("PROGENITOR_TEST_ROOT", "_testserver");
const SIDES = {
  client: {
    port: Number(process.env.PROGENITOR_CLIENT_PORT ?? 27341),
    tokenFile: process.env.PROGENITOR_CLIENT_TOKEN_FILE || path.join(GAME_DIR, "BepInEx", "config", "progenitor-dev.token"),
  },
  server: { port: Number(process.env.PROGENITOR_SERVER_PORT ?? 27342), tokenFile: process.env.PROGENITOR_SERVER_TOKEN_FILE || path.join(TEST_ROOT, "server", "BepInEx", "config", "progenitor-dev.token") },
};

const [side, op, argsJson] = process.argv.slice(2);
if (!SIDES[side] || !op) {
  console.error("usage: call.mjs <client|server> <op> [jsonArgs]");
  process.exit(2);
}
// "@path" reads the JSON from a file: PowerShell strips double quotes from arguments it hands to
// native programs, so scripts write the JSON to a temp file instead of quoting it on the command line.
const argsText = argsJson && argsJson.startsWith("@") ? fs.readFileSync(argsJson.slice(1), "utf8") : argsJson;
const args = argsText ? JSON.parse(argsText) : {};
const token = fs.readFileSync(SIDES[side].tokenFile, "utf8").trim();

// How long one call may take, end to end (connect + reply). The default is unchanged at 30 s.
//
// PER-CALL OVERRIDE (R69 finding 2). A wedged Dev bridge ACCEPTS the TCP connection and then
// never writes a line, so this timer -- not the connection error path -- is what ends the call,
// and 30 s of it is far longer than a caller that polls in a loop can afford: tools/deploy.ps1's
// swap hold has a 90 s window in total and needs a miss to cost seconds, not half a minute.
// PROGENITOR_CALL_TIMEOUT_MS is that caller's knob; PROGENITOR_TIMEOUT_MS stays as the
// process-wide one it always was, and an unset, unparseable or non-positive value in either
// falls through to the next source rather than producing a timer that never fires (Number("")
// is 0 and Number("abc") is NaN, both of which would break the call in a different way).
function timeoutFrom(name) {
  const raw = process.env[name];
  if (raw === undefined || raw === null || String(raw).trim() === "") return null;
  const n = Number(raw);
  if (!Number.isFinite(n) || n <= 0) {
    console.error(`ignoring ${name}=${raw}: not a positive number of milliseconds`);
    return null;
  }
  return n;
}
const timeoutMs = timeoutFrom("PROGENITOR_CALL_TIMEOUT_MS") ?? timeoutFrom("PROGENITOR_TIMEOUT_MS") ?? 30000;
for (const [k, v] of Object.entries(SIDES)) {
  if (String(v.tokenFile).toLowerCase().startsWith("c:\\vrisingserver")) { console.error(`refusing a token file under the live dedicated server for ${k}: ${v.tokenFile}`); process.exit(3); }
}

// `who` tells the bridge which AI session is calling (tools/mcp/identity.mjs). Best effort.
let who;
try { who = wireWho(detectIdentity({ tool: "call.mjs" })); } catch { who = undefined; }

// Bridge queue (tools/mcp/queue.mjs): refuse an op that changes the game while another session holds
// this side. Unlike MCP, scripts may still act on a free side without joining. A broken queue file
// never blocks a call; a busy lock does.
try {
  const refusal = createQueue().gate(who ?? {}, side, op, args, { requireSlot: false });
  if (refusal) { console.error(refusal.replace(/progenitor_queue action=(\w+)/g, "node tools/mcp/queue.mjs $1")); process.exit(5); }
} catch (e) {
  if (/locked/.test(e.message)) { console.error(`bridge queue: ${e.message}`); process.exit(5); }
}

/** May queue mail go to stderr? Not when Windows PowerShell 5.1 (powershell.exe) captures our output. */
function mailSafe() {
  if (process.env.PROGENITOR_CALL_NOTES === "1" || process.stderr.isTTY) return true;
  if (process.platform !== "win32" || process.stdout.isTTY) return true;
  try {
    const out = execFileSync("tasklist", ["/FI", `PID eq ${process.ppid}`, "/FO", "CSV", "/NH"], { encoding: "utf8", windowsHide: true, timeout: 3000 });
    return !/^"powershell\.exe"/i.test(out.trim());
  } catch { return false; }
}

const sock = net.createConnection({ host: "127.0.0.1", port: SIDES[side].port }, () => {
  sock.write(JSON.stringify({ id: 1, token, op, args, who }) + "\n");
});
let buf = "";
const timer = setTimeout(() => { sock.destroy(); console.error(`${side} bridge timed out on '${op}' after ${timeoutMs} ms`); process.exit(3); }, timeoutMs);
sock.setEncoding("utf8");
sock.on("data", (chunk) => {
  buf += chunk;
  const nl = buf.indexOf("\n");
  if (nl < 0) return;
  clearTimeout(timer);
  sock.end();
  const msg = JSON.parse(buf.slice(0, nl));
  // Sharing note on stderr, only on a terminal or with PROGENITOR_CALL_NOTES=1: PowerShell callers
  // under ErrorActionPreference=Stop merge stderr (2>&1) and would treat the note as a failure.
  if (process.stderr.isTTY || process.env.PROGENITOR_CALL_NOTES === "1") try {
    const note = sharingNote(side, msg.bridgeUser, who);
    if (note) console.error(note);
  } catch { /* never fail a call over the note */ }
  // Queue mail (messages from other sessions, the "N waiting" reminder) on stderr, so a session that
  // drives the game only through call.mjs still hears from the queue. Held back (left unread for the
  // next queue or MCP call) with PROGENITOR_CALL_NOTES=0, which the repo's PowerShell scripts set, and
  // when Windows PowerShell 5.1 captures this call's output: there a stderr line under
  // ErrorActionPreference=Stop with 2>&1 becomes a terminating error.
  if (process.env.PROGENITOR_CALL_NOTES !== "0") try {
    const queue = createQueue();
    if (queue.hasNotes(who ?? {}) && mailSafe()) {
      const mail = queue.notes(who ?? {});
      if (mail) console.error(mail.replace(/progenitor_queue action=(\w+)/g, "node tools/mcp/queue.mjs $1"));
    }
  } catch { /* never fail a call over queue mail */ }
  if (msg.ok) {
    const r = msg.result;
    // A ping says what is loaded on this side: record it for the queue's LOADED NOW and history.
    if (op === "ping") try { createQueue().observe(who ?? {}, side, r); } catch { /* best effort */ }
    // ASCII-only output, JSON or a plain-string result (tools/mcp/ascii-json.mjs): the bridge sends non-ASCII as \uXXXX, and plain
    // JSON.stringify would turn it back into raw UTF-8 bytes that Windows PowerShell decodes with the
    // console code page, so "·" read back as "Â·".
    console.log(typeof r === "string" ? asciiText(r) : asciiJson(r, 2));
  } else {
    console.error("bridge error: " + (msg.error ?? "unknown"));
    process.exit(1);
  }
});
sock.on("error", (e) => { clearTimeout(timer); console.error(`${side} bridge connection failed: ${e.message}`); process.exit(4); });
