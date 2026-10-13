import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { generate, stages } from './generate.mjs';

test('stages are deterministic, bounded packs with stable increasing identities and exact hashes', () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'progenitor-scale-'));
  try {
    const first = path.join(temp, 'first'), second = path.join(temp, 'second');
    const matrix = generate(first, 6);
    assert.deepEqual(matrix, generate(second, 6));
    assert.deepEqual(matrix.map(s => s.count), stages);
    const large = matrix.find(s => s.count === 10000);
    assert.ok(large);
    assert.equal(large.packs.length, 79);
    assert.equal(large.packs.at(-1).count, 16);
    assert.equal(large.packs.reduce((sum, pack) => sum + pack.count, 0), 10000);
    let previous = new Map();
    for (const stage of matrix) {
      const current = new Map();
      assert.equal(stage.expectation, stage.count + 6 <= 256
        ? 'within-default-definition-guard' : 'guard-refusal-not-capacity-test');
      for (const pack of stage.packs) {
        const dir = path.join(first, stage.stage, pack.id);
        assert.deepEqual(fs.readdirSync(dir).sort(), ['items.json', 'pack.json']);
        const bytes = fs.readFileSync(path.join(dir, 'items.json'));
        assert.deepEqual(bytes, fs.readFileSync(path.join(second, stage.stage, pack.id, 'items.json')));
        const manifest = JSON.parse(fs.readFileSync(path.join(dir, 'pack.json')));
        assert.equal(manifest.id, pack.id);
        assert.equal(manifest.sha256, createHash('sha256').update(bytes).digest('hex'));
        assert.ok(bytes.length <= 256 * 1024);
        const items = JSON.parse(bytes).items;
        assert.equal(items.length, pack.count);
        assert.ok(items.length <= 128);
        for (const item of items) {
          assert.ok(item.id.startsWith(pack.id + '.'));
          assert.equal(current.has(item.id), false);
          assert.equal(item.cloneOf, 'Item_Weapon_Sword_T01_Bone');
          assert.deepEqual(item.statBonuses, { maxHealth: 100, physicalPower: 100, spellPower: 100 });
          if (previous.has(item.id)) assert.deepEqual(item, previous.get(item.id));
          current.set(item.id, item);
        }
      }
      assert.equal(current.size, stage.count);
      for (const id of previous.keys()) assert.ok(current.has(id));
      previous = current;
    }
    assert.throws(() => generate(first), /never overwritten/);
  } finally { fs.rmSync(temp, { recursive: true }); }
});

test('invalid reservations and runtime destinations are refused without writes', () => {
  for (const value of [-1, 257, 1.5, NaN]) assert.throws(() => generate('unused', value), /reserved/);
  assert.throws(() => generate(path.join(os.tmpdir(), 'BepInEx', 'scale-new')), /offline scratch/);
});
