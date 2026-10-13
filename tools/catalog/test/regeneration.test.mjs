// Regenerating the inventory must never delete or overwrite maintained recipes.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { TOOL_DIR, REAL_RECIPES } from './helpers.mjs';
import { GENERATED_FILES, loadInventory } from '../lib/inventory.mjs';

const INSTALLED_BIN = 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRising\\VRising_Data\\StreamingAssets\\ContentArchives\\archive_dependencies.bin';

function sha(p) { return createHash('sha256').update(readFileSync(p)).digest('hex'); }

test('index-catalog.mjs writes only its generated files and leaves recipes/ byte-identical', { skip: existsSync(INSTALLED_BIN) ? false : 'installed catalog not present; the indexer has nothing to decode' }, () => {
  const root = mkdtempSync(path.join(tmpdir(), 'progenitor-regen-'));
  try {
    const game = path.join(root, 'game');
    const archives = path.join(game, 'VRising_Data', 'StreamingAssets', 'ContentArchives');
    mkdirSync(archives, { recursive: true });
    copyFileSync(INSTALLED_BIN, path.join(archives, 'archive_dependencies.bin'));
    const out = path.join(root, 'out');
    const recipes = path.join(out, 'recipes');
    mkdirSync(recipes, { recursive: true });
    for (const f of readdirSync(REAL_RECIPES)) copyFileSync(path.join(REAL_RECIPES, f), path.join(recipes, f));
    writeFileSync(path.join(out, 'catalog-summary.json'), '{"stale":true}');
    const before = Object.fromEntries(readdirSync(recipes).map(f => [f, sha(path.join(recipes, f))]));

    const r = spawnSync(process.execPath, [path.join(TOOL_DIR, 'index-catalog.mjs'), '--game', game, '--out', out], { encoding: 'utf8' });
    assert.equal(r.status, 0, r.stderr);

    const after = Object.fromEntries(readdirSync(recipes).map(f => [f, sha(path.join(recipes, f))]));
    assert.deepEqual(after, before, 'every recipe file is byte-identical after regeneration');
    const written = readdirSync(out).filter(f => f !== 'recipes');
    assert.ok(written.every(f => GENERATED_FILES.includes(f)), `unexpected output ${written}`);
    assert.notEqual(readFileSync(path.join(out, 'catalog-summary.json'), 'utf8'), '{"stale":true}', 'the generated file was replaced');
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('index-catalog.mjs without --inventory leaves a stamped inventory byte-identical and loadInventory reports mixed', { skip: existsSync(INSTALLED_BIN) ? false : 'installed catalog not present; the indexer has nothing to decode' }, () => {
  const root = mkdtempSync(path.join(tmpdir(), 'progenitor-regen-partial-'));
  try {
    const archives = path.join(root, 'VRising_Data', 'StreamingAssets', 'ContentArchives');
    mkdirSync(archives, { recursive: true });
    copyFileSync(INSTALLED_BIN, path.join(archives, 'archive_dependencies.bin'));
    const realSha = sha(path.join(archives, 'archive_dependencies.bin'));
    const staleSha = 'a'.repeat(64);
    assert.notEqual(staleSha, realSha);
    const inventoryPath = path.join(root, 'catalog-inventory.json');
    const stamped = {
      generated: '2020-01-01T00:00:00.000Z',
      source: { catalogSha256: staleSha, catalogBytes: 1, catalogMTime: '2020-01-01T00:00:00.000Z', game: root },
      objects: 0, names: 0, failed: 0, byClass: {}, archives: [],
    };
    writeFileSync(inventoryPath, JSON.stringify(stamped));
    const before = readFileSync(inventoryPath);

    const r = spawnSync(process.execPath, [path.join(TOOL_DIR, 'index-catalog.mjs'), '--game', root, '--out', root], { encoding: 'utf8' });
    assert.equal(r.status, 0, r.stderr + r.stdout);

    const after = readFileSync(inventoryPath);
    assert.ok(Buffer.from(before).equals(Buffer.from(after)), 'inventory bytes are untouched when --inventory is omitted');
    const summary = JSON.parse(readFileSync(path.join(root, 'catalog-summary.json'), 'utf8'));
    assert.equal(summary.source.catalogSha256, realSha);
    const inv = loadInventory(root);
    assert.equal(inv.generation, 'mixed');
    assert.equal(inv.available, false);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the recipe directory is not one of the generated outputs', () => {
  assert.ok(existsSync(REAL_RECIPES));
  for (const f of GENERATED_FILES) assert.ok(!existsSync(path.join(REAL_RECIPES, f)), `${f} must not live under recipes/`);
});
