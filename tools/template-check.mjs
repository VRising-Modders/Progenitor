#!/usr/bin/env node
// Instantiates templates/progenitor-mod into a temporary folder and builds it against this checkout.
//
//   node tools/template-check.mjs              full build when the game's interop is present, else game-free
//   node tools/template-check.mjs --game-free  only the game-free test project (what CI runs)
//   node tools/template-check.mjs --keep       keep the temporary folder
//
// The template goes into a private hive (--debug:custom-hive), so your own `dotnet new` list is not touched.
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const args = new Set(process.argv.slice(2));
const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const name = "TemplateCheckMod";
const work = mkdtempSync(join(tmpdir(), "progenitor-template-"));
const hive = join(work, "hive");
const out = join(work, name);

function run(what, cmd, cmdArgs) {
  console.log(`== ${what}`);
  const r = spawnSync(cmd, cmdArgs, { stdio: "inherit", shell: false });
  if (r.status !== 0) throw new Error(`${what} failed (exit ${r.status})`);
}

function files(dir) {
  return readdirSync(dir).flatMap((n) => {
    const p = join(dir, n);
    return statSync(p).isDirectory() ? files(p) : [p];
  });
}

let gameFree = args.has("--game-free");
if (!gameFree) {
  const game = process.env.VRISING_GAME_DIR || "C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRising";
  const interop = process.env.VRISING_INTEROP_DIR || join(game, "BepInEx", "interop");
  if (!existsSync(join(interop, "ProjectM.dll"))) {
    console.log(`No game interop at ${interop}: checking the game-free test project only.`);
    gameFree = true;
  }
}

try {
  run("install the template (private hive)", "dotnet", ["new", "install", join(repo, "templates", "progenitor-mod"), "--debug:custom-hive", hive]);
  run(`dotnet new progenitor-mod -n ${name}`, "dotnet", [
    "new", "progenitor-mod", "-n", name, "--team", "CheckTeam", "--display-name", "Template Check",
    "--progenitor-repo", repo, "-o", out, "--debug:custom-hive", hive,
  ]);

  const tokens = ["ProgenitorMod", "yourteam", "YourTeam", "Progenitor Mod Display Name", "<ProgenitorRepoFromTemplate>progenitor-repo-auto</ProgenitorRepoFromTemplate>"];
  const left = [];
  for (const f of files(out)) {
    const rel = relative(out, f);
    const text = readFileSync(f, "utf8");
    for (const t of tokens) if (text.includes(t) || rel.includes(t)) left.push(`${rel}: ${t}`);
  }
  if (left.length) throw new Error("template tokens left in the output:\n  " + left.join("\n  "));
  const plugin = readFileSync(join(out, name, `${name}.csproj`), "utf8");
  if (!plugin.includes(`<BepInExPluginGuid>checkteam.${name.toLowerCase()}</BepInExPluginGuid>`))
    throw new Error("the plugin id is not <team>.<name> in lower case");
  for (const e of [`${name}.sln`, join(name, "Plugin.cs"), join(`${name}.Tests`, `${name}.Tests.csproj`), "README.md"])
    if (!existsSync(join(out, e))) throw new Error(`missing ${e} in the output`);
  console.log("== output has no template tokens");

  // An explicit property beats PROGENITOR_REPO in the environment, so this always builds against this checkout.
  const repoProp = `-p:ProgenitorRepo=${repo}`;
  const tests = join(out, `${name}.Tests`, `${name}.Tests.csproj`);
  if (gameFree) {
    run("test (game-free project)", "dotnet", ["test", tests, repoProp]);
  } else {
    run("build the solution", "dotnet", ["build", join(out, `${name}.sln`), repoProp]);
    run("test", "dotnet", ["test", tests, repoProp, "--no-build"]);
    const dll = join(out, name, "bin", "Debug", "net6.0", `${name}.dll`);
    if (!existsSync(dll)) throw new Error(`the mod DLL was not built: ${dll}`);
  }
  console.log(`template check passed (${gameFree ? "game-free" : "full build"})`);
} catch (e) {
  console.error(e.message);
  process.exitCode = 1;
} finally {
  if (args.has("--keep")) console.log(`kept ${work}`);
  else rmSync(work, { recursive: true, force: true });
}
