// Copies the shared help-page renderer (style, layout, script) from templates/help/index.html into
// other help pages, keeping each page's own content block (HELP-CONTENT START ... HELP-CONTENT END).
//
//   node tools/help-guide/sync.mjs           update docs/guide/progenitor-guide.html
//   node tools/help-guide/sync.mjs --check   exit 1 when it is out of date
//   node tools/help-guide/sync.mjs <file>... update these pages instead
import { readFileSync, writeFileSync } from "node:fs";
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
if (targets.length === 0) targets.push(join(root, "docs", "guide", "progenitor-guide.html"));

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
if (check && stale) process.exit(1);
if (check) console.log("help pages are in sync");
