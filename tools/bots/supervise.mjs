#!/usr/bin/env node
// EXPERIMENTAL, EARLY DEVELOPMENT, development-only: headless test-client bots, not a supported framework feature and
// not part of the Release package. Read tools/bots/README.md first; may change or be removed.
// Keep the bots playing: runs the brain in rounds (so code changes are picked up at the next round),
// restarts it after a crash, and never runs twice (a second driver on the same bots corrupted a whole
// live run, 2026-09-24). Stop it by creating the stop file, or kill the pid in the lock file.
//   node tools/bots/supervise.mjs --bots 1:HeadlessOne:builder,2:HeadlessTwo:warrior,3:HeadlessThree:balanced --hours 8
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const argv = process.argv.slice(2);
const opt = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 ? argv[i + 1] : d; };
const bots = opt("bots", "1:HeadlessOne:builder,2:HeadlessTwo:warrior,3:HeadlessThree:balanced");
const hours = Number(opt("hours", "8"));
const round = Number(opt("round", "20"));            // minutes per brain run
const dir = opt("logs", path.join(os.tmpdir(), "progenitor-bots"));
fs.mkdirSync(dir, { recursive: true });
const lock = path.join(dir, "supervisor.pid");
const stopFile = path.join(dir, "STOP");

const alive = (pid) => { try { process.kill(pid, 0); return true; } catch { return false; } };
if (fs.existsSync(lock)) {
  const pid = Number(fs.readFileSync(lock, "utf8"));
  if (pid && alive(pid)) { console.error(`supervisor already running (pid ${pid}); refusing to start a second driver`); process.exit(1); }
}
fs.writeFileSync(lock, String(process.pid));
fs.rmSync(stopFile, { force: true });
let child = null;
const cleanup = () => { try { child?.kill(); } catch { /* gone */ } fs.rmSync(lock, { force: true }); };
process.on("exit", cleanup);
for (const sig of ["SIGINT", "SIGTERM"]) process.on(sig, () => process.exit(0));

const brain = path.join(path.dirname(fileURLToPath(import.meta.url)), "brain.mjs");
const until = Date.now() + hours * 3600000;
let n = 0;
while (Date.now() < until && !fs.existsSync(stopFile)) {
  n++;
  const log = path.join(dir, `brain-${new Date().toISOString().slice(0, 13).replace(/[:T]/g, "-")}.log`);
  const out = fs.openSync(log, "a");
  const minutes = Math.max(1, Math.min(round, (until - Date.now()) / 60000));
  fs.writeSync(out, `\n=== round ${n} ${new Date().toISOString()} (${minutes.toFixed(0)} min) ===\n`);
  child = spawn(process.execPath, [brain, "--bots", bots, "--minutes", String(minutes)], { stdio: ["ignore", out, out] });
  const code = await new Promise((resolve) => child.on("exit", resolve));
  child = null;
  fs.writeSync(out, `=== round ${n} exited ${code} ===\n`);
  fs.closeSync(out);
  if (code !== 0) await new Promise((r) => setTimeout(r, 5000)); // crash: brief pause, then again
}
console.log(`supervisor done after ${n} rounds`);
