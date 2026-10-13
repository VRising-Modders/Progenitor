// inventory.mjs - read-only access to the generated inventory next to the recipes.
//
// The generated files (catalog-summary.json, catalog-inventory.json) are produced by
// index-catalog.mjs and are git-ignored; this module never writes them and never runs the
// indexer. When they are missing, every function reports that honestly instead of scanning
// the game install.

import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';

export const GENERATED_FILES = Object.freeze(['catalog-index.json', 'catalog-summary.json', 'catalog-inventory.json']);

function emptyRows() {
  return { rows: [], byName: new Map() };
}

function sourceFromInventory(inventory) {
  const source = inventory?.source ?? {};
  return {
    catalogSha256: source.catalogSha256 ?? null,
    catalogBytes: source.catalogBytes ?? null,
    catalogMTime: source.catalogMTime ?? null,
    game: source.game ?? null,
  };
}

function inventoryCatalogSha(inventory) {
  const sha = inventory?.source?.catalogSha256;
  return typeof sha === 'string' && sha.length > 0 ? sha : null;
}

function collectRows(inventory) {
  const rows = [];
  const byName = new Map();
  const warnings = [];
  let rejectedRows = 0;
  for (const archive of inventory.archives ?? []) {
    for (const o of archive.named ?? []) {
      const value = o.pathID;
      let pathID;
      if (typeof value === 'string') {
        pathID = value;
      } else if (typeof value === 'number' && Number.isSafeInteger(value)) {
        pathID = String(value);
      } else {
        rejectedRows++;
        warnings.push(`skipped inventory row in archive ${archive.archiveId} with non-lossless pathID ${JSON.stringify(value)}`);
        continue;
      }
      const row = { archiveId: archive.archiveId, pathID, type: o.type, name: o.name };
      rows.push(row);
      let list = byName.get(row.name);
      if (!list) byName.set(row.name, list = []);
      list.push(row);
    }
  }
  return { rows, byName, warnings, rejectedRows };
}

function availableInventory(dir, inventory, extra = {}) {
  const { rows, byName, warnings, rejectedRows } = collectRows(inventory);
  return {
    available: true, dir, generation: 'stamped',
    source: sourceFromInventory(inventory),
    generated: inventory.generated ?? null,
    counts: {
      archives: inventory.archives?.length ?? 0,
      objects: inventory.objects ?? null,
      named: inventory.names ?? rows.length,
      failedArchives: inventory.failed ?? null,
      rejectedRows,
    },
    warnings, rows, byName, ...extra,
  };
}

function unavailable(dir, generation, reason, extra = {}) {
  return { available: false, dir, generation, reason, ...emptyRows(), ...extra };
}

/**
 * Load the inventory from `dir`. Provenance comes from catalog-inventory.json's own stamp,
 * never from whichever catalog-summary.json happens to sit next to an older file.
 *
 *   { available: true, generation: "stamped", dir, source, generated, rows, byName, counts, warnings }
 *   { available: false, generation: "mixed"|"unstamped"|null, dir, reason, rows: [], byName, missing? }
 *
 * `rows` are {archiveId, pathID, type, name} with pathID kept as a string.
 */
export function loadInventory(dir) {
  const summaryPath = path.join(dir, 'catalog-summary.json');
  const inventoryPath = path.join(dir, 'catalog-inventory.json');
  if (!existsSync(inventoryPath)) {
    const missing = [];
    if (!existsSync(summaryPath)) missing.push('catalog-summary.json');
    missing.push('catalog-inventory.json');
    return unavailable(dir, null,
      `generated inventory not present in ${dir} (missing ${missing.join(', ')}); run index-catalog.mjs --inventory to produce it. Recipes still inspect; inventory associations report as inventory-unavailable.`,
      { missing });
  }
  let inventory;
  try {
    inventory = JSON.parse(readFileSync(inventoryPath, 'utf8'));
  } catch (e) {
    return unavailable(dir, null, `generated inventory unreadable: ${e.message}`, { missing: [] });
  }
  const invSha = inventoryCatalogSha(inventory);
  if (!invSha) {
    return unavailable(dir, 'unstamped',
      'generated inventory predates stamping and must be regenerated; run index-catalog.mjs --inventory to produce a stamped catalog-inventory.json. The adjacent catalog summary hash is not this inventory\'s provenance.');
  }

  const summaryMissing = !existsSync(summaryPath);
  if (summaryMissing) {
    return availableInventory(dir, inventory, { summaryMissing: true });
  }

  let summary;
  try {
    summary = JSON.parse(readFileSync(summaryPath, 'utf8'));
  } catch (e) {
    return unavailable(dir, null, `generated inventory unreadable: ${e.message}`, { missing: [] });
  }

  const sumSha = summary.source?.catalogSha256 ?? null;
  if (invSha === sumSha) {
    return availableInventory(dir, inventory);
  }

  const invGenerated = inventory.generated ?? null;
  const sumGenerated = summary.generated ?? null;
  return unavailable(dir, 'mixed',
    `generated inventory does not match the catalog summary (inventory catalogSha256 ${invSha} generated ${invGenerated}; summary catalogSha256 ${sumSha} generated ${sumGenerated}); run index-catalog.mjs --inventory to regenerate the inventory with the current catalog.`);
}

function compareRows(a, b) {
  return a.name.localeCompare(b.name) || a.type.localeCompare(b.type)
    || a.archiveId.localeCompare(b.archiveId) || comparePathId(a.pathID, b.pathID);
}

function comparePathId(a, b) {
  const x = BigInt(a), y = BigInt(b);
  return x < y ? -1 : x > y ? 1 : 0;
}

export const INVENTORY_NOTE = 'index presence only; runtime availability is not observed offline';

/**
 * Bounded, stably ordered name search over the inventory. Distinct objects with the same name
 * stay distinct (one row per archiveId/pathID). Returns { state, note, total, truncated, rows }.
 */
export function searchInventory(inv, text, limit) {
  if (!inv.available) {
    return {
      state: 'inventory-unavailable',
      note: INVENTORY_NOTE,
      generation: inv.generation ?? null,
      reason: inv.reason,
      total: 0,
      truncated: false,
      rows: [],
    };
  }
  const needle = text.toLowerCase();
  const hits = [];
  for (const row of inv.rows) if (row.name.toLowerCase().includes(needle)) hits.push(row);
  hits.sort(compareRows);
  return {
    state: 'indexed',
    note: INVENTORY_NOTE,
    total: hits.length,
    truncated: hits.length > limit,
    rows: hits.slice(0, limit),
  };
}

/** Exact lookup by name (and, when given, Unity type). Returns the distinct rows, ordered. */
export function lookupByName(inv, name, type) {
  if (!inv.available) return null;
  const list = inv.byName.get(name) ?? [];
  const rows = type ? list.filter(r => r.type === type) : list.slice();
  rows.sort(compareRows);
  return rows;
}

/** Exact lookup of one archive object. */
export function lookupArchiveObject(inv, archiveId, pathID) {
  if (!inv.available) return null;
  for (const row of inv.rows) if (row.archiveId === archiveId && row.pathID === pathID) return row;
  return undefined;
}
