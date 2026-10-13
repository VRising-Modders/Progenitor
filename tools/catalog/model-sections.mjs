#!/usr/bin/env node
// model-sections.mjs - the name table ctx.Models reads: prefab name -> entity-scene section.
//
//   node tools/catalog/index-catalog.mjs --inventory          (writes catalog-index.json + catalog-inventory.json)
//   node tools/catalog/model-sections.mjs [--generated <dir>] [--out <file>] [--prefix HYB_,VampireFemale_,VampireMale_] [--all-names]
//
// Every hybrid character model (HYB_*) ships as one section of the hybrid-model entity scene; the
// catalog lists that section's SubSceneObjectReferences object as "<scene guid>:<section>" in the
// archive that holds the prefab. This joins the catalog objects (index) with the root GameObject
// names inside each archive (inventory) and writes
//   { "catalogSha256": "...", "generated": "...", "models": [ { "name", "section", "archive" } ] }
// sorted by name. A name found in two archives is kept once (first by archive id) and reported.
// --prefix takes a comma-separated list. Equipment (VampireFemale_*, VampireMale_*) streams the same
// way from its own scene (34436c53... on the 2026-08-25 build); a match can also be a rig child
// (VampireFemale_ArmorSet04_Dracula_LOD00_rig), which maps to the same section and is harmless.
//
// Derived data for one game build: regenerate after an update. Nothing is loaded or contacted.
// Exit codes: 0 ok, 2 bad arguments, 3 missing generated input.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

export function parseArgs(argv) {
  const a = { generated: here, out: null, prefix: 'HYB_,VampireFemale_,VampireMale_', allNames: false };
  for (let i = 0; i < argv.length; i++) {
    const k = argv[i];
    if (k === '--generated') a.generated = argv[++i];
    else if (k === '--out') a.out = argv[++i];
    else if (k === '--prefix') a.prefix = argv[++i] ?? '';
    else if (k === '--all-names') a.allNames = true;
    else if (k === '--help' || k === '-h') a.help = true;
    else throw new Error(`unknown argument '${k}'`);
  }
  return a;
}

/**
 * Pure join. `index` is catalog-index.json, `inventory` is catalog-inventory.json.
 * Returns { models, duplicates, sceneObjects } without touching the disk.
 */
export function buildModelSections(index, inventory, { prefix = 'HYB_', allNames = false } = {}) {
  const prefixes = String(prefix).split(',').map(x => x.trim()).filter(Boolean);
  const sectionByArchive = new Map();
  let sceneObjects = 0;
  for (const o of index.objects ?? []) {
    if (o.generationTypeName !== 'SubSceneObjectReferences' || !o.archiveId || !o.assetGuid) continue;
    sceneObjects++;
    sectionByArchive.set(o.archiveId, `${o.assetGuid}:${o.sceneObjectIdentifier0}`);
  }

  const byName = new Map();
  const duplicates = [];
  const archives = [...(inventory.archives ?? [])].sort((p, q) => p.archiveId.localeCompare(q.archiveId));
  for (const ar of archives) {
    const section = sectionByArchive.get(ar.archiveId);
    if (!section) continue;
    for (const o of ar.named ?? []) {
      if (o.type !== 'GameObject') continue;
      if (!prefixes.some(p => o.name.startsWith(p))) continue;
      // Only the prefab root carries the HYB_ name; bones and parts are named after joints.
      if (!allNames && byName.has(o.name) && byName.get(o.name).archive === ar.archiveId) continue;
      if (byName.has(o.name)) {
        if (byName.get(o.name).archive !== ar.archiveId) duplicates.push({ name: o.name, kept: byName.get(o.name).archive, skipped: ar.archiveId });
        continue;
      }
      byName.set(o.name, { name: o.name, section, archive: ar.archiveId });
    }
  }

  const models = [...byName.values()].sort((p, q) => p.name.localeCompare(q.name));
  return { models, duplicates, sceneObjects };
}

function main() {
  let a;
  try { a = parseArgs(process.argv.slice(2)); }
  catch (e) { console.error(e.message); process.exit(2); }
  if (a.help) {
    console.log('usage: node tools/catalog/model-sections.mjs [--generated <dir>] [--out <file>] [--prefix HYB_,VampireFemale_,VampireMale_] [--all-names]');
    return;
  }
  const indexPath = path.join(a.generated, 'catalog-index.json');
  const inventoryPath = path.join(a.generated, 'catalog-inventory.json');
  for (const p of [indexPath, inventoryPath]) {
    if (!fs.existsSync(p)) {
      console.error(`missing ${p}; run: node tools/catalog/index-catalog.mjs --inventory --out ${a.generated}`);
      process.exit(3);
    }
  }
  const index = JSON.parse(fs.readFileSync(indexPath, 'utf8'));
  const inventory = JSON.parse(fs.readFileSync(inventoryPath, 'utf8'));
  const { models, duplicates, sceneObjects } = buildModelSections(index, inventory, a);
  const out = a.out ?? path.join(a.generated, 'model-sections.json');
  const doc = { catalogSha256: index.source?.catalogSha256 ?? null, generated: new Date().toISOString(), prefix: a.prefix, models };
  fs.writeFileSync(out, JSON.stringify(doc, null, 1) + '\n');
  const truncated = (inventory.archives ?? []).filter(x => x.bodiesTruncated).length;
  console.error(`models      ${models.length} (${a.prefix}*) from ${sceneObjects} scene-section objects`);
  if (duplicates.length) console.error(`duplicates  ${duplicates.length} (kept the first archive): ${duplicates.slice(0, 5).map(d => d.name).join(', ')}`);
  if (truncated) console.error(`note        ${truncated} archives were read only in part; raise --inventory-cap on index-catalog.mjs if a name is missing`);
  console.error(`wrote       ${out}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
