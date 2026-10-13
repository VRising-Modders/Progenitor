#!/usr/bin/env node
// Run a spike snippet at one or more Step values: rewrite tools/scratch/Snippet.cs, build, hot-load on one
// side, wait for "scratch: result"/"scratch: error", print the scratch log lines, unload. Usage:
//   node tools/mcp/spike.mjs <server|client> <snippet> <step[,step...]> [timeoutSeconds]
// <snippet> is a path to a .cs file, or a name resolved as <name>.snippet.cs under PROGENITOR_SNIPPET_DIR.
// Snippets are kept outside this repository and declare `public const int Step = N;`.
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { requireWorkspacePath } from "../common/workspace.mjs";

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const GAME_DIR = process.env.PROGENITOR_GAME_DIR ?? "C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRising";
// <workspace>/_testserver unless PROGENITOR_TEST_ROOT says otherwise (tools/common/workspace.mjs).
const TEST_ROOT = process.env.PROGENITOR_TEST_ROOT ?? requireWorkspacePath("PROGENITOR_TEST_ROOT", "_testserver");
const RELOAD = {
  client: path.join(GAME_DIR, "BepInEx", "progenitor-reload"),
  server: process.env.PROGENITOR_SERVER_RELOAD ?? path.join(TEST_ROOT, "server", "BepInEx", "progenitor-reload"),
};
// Never write into the live dedicated server, whatever the environment says.
const LIVE_SERVER = "C:\\VRisingServer";
for (const [k, v] of Object.entries(RELOAD)) {
  if (path.resolve(v).toLowerCase().startsWith(LIVE_SERVER.toLowerCase())) { console.error(`refusing reload folder under the live server for ${k}: ${v}`); process.exit(3); }
}
const SCRATCH_DIR = path.join(REPO, "tools", "scratch");
const SCRATCH_DLL = path.join(SCRATCH_DIR, "bin", "Debug", "net6.0", "Progenitor.Scratch.dll");
const TMP = process.env.TMP ?? process.env.TEMP ?? SCRATCH_DIR;

const [side, name, stepsArg, timeoutArg] = process.argv.slice(2);
if (!RELOAD[side] || !name || !stepsArg) { console.error("usage: spike.mjs <server|client> <snippet> <step[,step]> [timeoutSeconds]"); process.exit(2); }
const steps = stepsArg.split(",").map((s) => s.trim()).filter(Boolean);
const timeoutSeconds = Number(timeoutArg ?? 30);
const snippetDir = process.env.PROGENITOR_SNIPPET_DIR;
if (!/\.cs$/i.test(name) && !snippetDir) { console.error("pass a snippet .cs path, or set PROGENITOR_SNIPPET_DIR to resolve a snippet name"); process.exit(2); }
const snippetPath = /\.cs$/i.test(name) ? path.resolve(name) : path.join(snippetDir, `${name}.snippet.cs`);
const source = fs.readFileSync(snippetPath, "utf8");
if (!/public const int Step = \d+;/.test(source)) { console.error("snippet has no `public const int Step = N;`"); process.exit(2); }

function bridge(op, payload) {
  const args = [path.join(REPO, "tools", "mcp", "call.mjs"), side, op];
  if (payload !== undefined) {
    const f = path.join(TMP, `spike-${process.pid}-${op}.json`);
    fs.writeFileSync(f, JSON.stringify(payload));
    args.push("@" + f);
  }
  const r = spawnSync(process.execPath, args, { encoding: "utf8" });
  if (r.status !== 0) throw new Error(`bridge ${op} failed: ${(r.stdout + r.stderr).trim()}`);
  if (r.stderr) process.stderr.write(r.stderr); // bridge-queue mail and notes for whoever runs the spike
  return JSON.parse(r.stdout);
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let failed = false;
for (const step of steps) {
  console.log(`=== ${name} step ${step} (${side})`);
  fs.writeFileSync(path.join(SCRATCH_DIR, "Snippet.cs"), source.replace(/public const int Step = \d+;/, `public const int Step = ${step};`));
  const built = spawnSync("dotnet", ["build", path.join(SCRATCH_DIR, "Progenitor.Scratch.csproj"), "-nologo", "-v", "q"], { encoding: "utf8" });
  if (built.status !== 0) {
    console.log("compile failed:\n" + (built.stdout + built.stderr).split(/\r?\n/).filter((l) => /error |Build FAILED/.test(l)).join("\n"));
    failed = true; break;
  }
  const dest = path.join(RELOAD[side], "Progenitor.Scratch.dll");
  let since = bridge("ping").logSeq ?? -1;
  fs.mkdirSync(RELOAD[side], { recursive: true });
  fs.copyFileSync(SCRATCH_DLL, dest);
  const deadline = Date.now() + timeoutSeconds * 1000;
  let finished = false;
  try {
    while (Date.now() < deadline && !finished) {
      const r = bridge("logs", { since, max: 200, filter: "scratch" });
      since = r.latestSeq ?? since;
      for (const line of r.lines ?? []) {
        const t = String(line.text ?? "");
        console.log(t.length > 400 ? t.slice(0, 400) + "..." : t);
        if (t.includes("scratch: result") || t.includes("scratch: error")) { finished = true; if (t.includes("scratch: error")) failed = true; }
      }
      if (!finished) await sleep(400);
    }
    if (!finished) { console.log(`timeout after ${timeoutSeconds}s waiting for scratch: result/error`); failed = true; }
  } finally {
    try { fs.unlinkSync(dest); } catch { /* gone */ }
    await sleep(1500); // let the reloader notice the unload before the next step
  }
  if (failed) break;
}
process.exit(failed ? 1 : 0);
