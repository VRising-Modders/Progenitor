// Copies the shared help-page renderer (style, layout, script) from templates/help/index.html into
// other help pages, keeping each page's own content block (HELP-CONTENT START ... HELP-CONTENT END).
//
//   node tools/help-guide/sync.mjs           update docs/guide/progenitor-guide.html and the mod template's help page
//   node tools/help-guide/sync.mjs --check   exit 1 when it is out of date
//   node tools/help-guide/sync.mjs <file>... update these pages instead
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const START = "<!-- HELP-CONTENT START -->";
const END = "<!-- HELP-CONTENT END -->";

function split(text, file) {
  const a = text.indexOf(START);
  const b = text.indexOf(END);
  if (a < 0 || b < a) throw new Error(`${file}: no ${START} ... ${END} block`);
  return { before: text.slice(0, a), block: text.slice(a, b + END.length), after: text.slice(b + END.length) };
}

const args = process.argv.slice(2);
const check = args.includes("--check");
const targets = args.filter((a) => a !== "--check");
if (targets.length === 0) {
  targets.push(join(root, "docs", "guide", "progenitor-guide.html"));
  // The dotnet new template's help page (templates/progenitor-mod), when the repository has it.
  const modHelp = join(root, "templates", "progenitor-mod", "help", "index.html");
  if (existsSync(modHelp)) targets.push(modHelp);
}

const templatePath = join(root, "templates", "help", "index.html");
const template = split(readFileSync(templatePath, "utf8"), templatePath);

let stale = 0;
for (const file of targets) {
  const current = readFileSync(file, "utf8");
  const next = template.before + split(current, file).block + template.after;
  if (next === current) continue;
  stale++;
  if (check) console.error(`${file}: renderer differs from templates/help/index.html; run node tools/help-guide/sync.mjs`);
  else { writeFileSync(file, next); console.log(`updated ${file}`); }
}
// A help page ships on its own (the guide inside the game package, a mod's help/index.html), so a
// relative link leaving the file points at nothing there. Links must be in-page (#...) or absolute.
// A value starting with a quote is the renderer building markup in script ('...src="' + H.logo + '"').
const ALLOWED = /^(#|https?:|mailto:|data:|')/;
let relative = 0;
for (const file of [templatePath, ...targets]) {
  const text = readFileSync(file, "utf8");
  for (const m of text.matchAll(/\b(?:href|src)=\\?"([^"\\]*)/g)) {
    if (ALLOWED.test(m[1])) continue;
    relative++;
    const line = text.slice(0, m.index).split("\n").length;
    console.error(`${file}:${line}: relative link "${m[1]}"; use an absolute URL (https://github.com/VRising-Modders/Progenitor/blob/main/<path>) or an in-page #link`);
  }
}

// Screenshots are the one relative reference allowed: shot: { src: "img/x.webp" } names a file that ships beside the page
// (package.ps1 and deploy.ps1 copy docs/guide/img with the guide; a mod's help/* files travel with its help page).
// Each one must exist, inside the page's own folder.
let missingShots = 0;
for (const file of targets) {
  const text = readFileSync(file, "utf8");
  const dir = dirname(file);
  for (const m of text.matchAll(/\bshot:\s*\{\s*src:\s*"([^"]+)"/g)) {
    const src = m[1];
    const line = text.slice(0, m.index).split("\n").length;
    if (/^(https?:|data:)/.test(src)) continue;
    if (src.startsWith("/") || src.includes("..") || !existsSync(join(dir, src))) {
      missingShots++;
      console.error(`${file}:${line}: screenshot "${src}" is not a file inside the page's folder`);
    }
  }
}

if (check && (stale || relative || missingShots)) process.exit(1);
if (missingShots) process.exit(1);
if (relative) process.exit(1);
if (check) console.log("help pages are in sync, have no relative links, and every screenshot exists");
