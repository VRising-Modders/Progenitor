import test from 'node:test';
import assert from 'node:assert/strict';
import { copyFileSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { loadInventory } from '../lib/inventory.mjs';
import { FIXTURE_GENERATED, FIXTURE_SHA } from './helpers.mjs';

function tempDir() {
  const dir = mkdtempSync(path.join(tmpdir(), 'progenitor-inventory-'));
  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) };
}

function copyFixture(dir) {
  copyFileSync(path.join(FIXTURE_GENERATED, 'catalog-inventory.json'), path.join(dir, 'catalog-inventory.json'));
  copyFileSync(path.join(FIXTURE_GENERATED, 'catalog-summary.json'), path.join(dir, 'catalog-summary.json'));
}

test('stamped inventory matching the summary is available', () => {
  const inv = loadInventory(FIXTURE_GENERATED);
  assert.equal(inv.available, true);
  assert.equal(inv.generation, 'stamped');
  assert.equal(inv.source.catalogSha256, FIXTURE_SHA);
  assert.equal(inv.generated, '2026-09-06T00:00:00.000Z');
  assert.ok(inv.rows.length > 0);
  assert.ok(inv.byName.size > 0);
});

test('stamped inventory whose catalog sha differs from the summary is mixed and unavailable', () => {
  const t = tempDir();
  try {
    copyFixture(t.dir);
    const summaryPath = path.join(t.dir, 'catalog-summary.json');
    const summary = JSON.parse(readFileSync(summaryPath, 'utf8'));
    const inventory = JSON.parse(readFileSync(path.join(t.dir, 'catalog-inventory.json'), 'utf8'));
    const mixedSha = 'b'.repeat(64);
    summary.source.catalogSha256 = mixedSha;
    writeFileSync(summaryPath, JSON.stringify(summary));
    const inv = loadInventory(t.dir);
    assert.equal(inv.available, false);
    assert.equal(inv.generation, 'mixed');
    assert.match(inv.reason, new RegExp(FIXTURE_SHA));
    assert.match(inv.reason, new RegExp(mixedSha));
    assert.match(inv.reason, new RegExp(String(inventory.generated).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    assert.match(inv.reason, new RegExp(String(summary.generated).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    assert.match(inv.reason, /index-catalog\.mjs --inventory/);
    assert.deepEqual(inv.rows, []);
    assert.equal(inv.byName.size, 0);
  } finally { t.cleanup(); }
});

test('unstamped inventory is unavailable and does not inherit the summary hash', () => {
  const t = tempDir();
  try {
    copyFixture(t.dir);
    const inventory = JSON.parse(readFileSync(path.join(t.dir, 'catalog-inventory.json'), 'utf8'));
    delete inventory.generated;
    delete inventory.source;
    writeFileSync(path.join(t.dir, 'catalog-inventory.json'), JSON.stringify(inventory));
    const inv = loadInventory(t.dir);
    assert.equal(inv.available, false);
    assert.equal(inv.generation, 'unstamped');
    assert.match(inv.reason, /predate/i);
    assert.match(inv.reason, /stamp/i);
    assert.notEqual(inv.source?.catalogSha256, FIXTURE_SHA);
    assert.deepEqual(inv.rows, []);
    assert.equal(inv.byName.size, 0);
  } finally { t.cleanup(); }
});

test('stamped inventory remains available when the summary is missing', () => {
  const t = tempDir();
  try {
    copyFileSync(path.join(FIXTURE_GENERATED, 'catalog-inventory.json'), path.join(t.dir, 'catalog-inventory.json'));
    const inv = loadInventory(t.dir);
    assert.equal(inv.available, true);
    assert.equal(inv.generation, 'stamped');
    assert.equal(inv.summaryMissing, true);
    assert.equal(inv.source.catalogSha256, FIXTURE_SHA);
    assert.ok(inv.rows.length > 0);
  } finally { t.cleanup(); }
});

test('a numeric pathID is kept as a string only when it is a safe integer; otherwise the row is rejected', () => {
  const t = tempDir();
  try {
    copyFixture(t.dir);
    const inventory = JSON.parse(readFileSync(path.join(t.dir, 'catalog-inventory.json'), 'utf8'));
    inventory.archives.push({
      archiveId: 'dddddddddddddddddddddddddddddddd',
      named: [
        { pathID: 42, type: 'GameObject', name: 'Safe_Numeric' },
        { pathID: Number.MAX_SAFE_INTEGER + 1, type: 'GameObject', name: 'Unsafe_Numeric' },
      ],
    });
    writeFileSync(path.join(t.dir, 'catalog-inventory.json'), JSON.stringify(inventory));
    const inv = loadInventory(t.dir);
    assert.equal(inv.available, true);
    const kept = inv.rows.find(r => r.name === 'Safe_Numeric');
    assert.ok(kept);
    assert.equal(kept.pathID, '42');
    assert.equal(typeof kept.pathID, 'string');
    assert.equal(inv.rows.some(r => r.name === 'Unsafe_Numeric'), false);
    assert.equal(inv.counts.rejectedRows, 1);
    assert.ok(Array.isArray(inv.warnings) && inv.warnings.length >= 1);
  } finally { t.cleanup(); }
});
