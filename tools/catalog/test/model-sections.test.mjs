import test from 'node:test';
import assert from 'node:assert/strict';
import { buildModelSections, parseArgs } from '../model-sections.mjs';

const scene = 'ddfc6a4db4009e2448b8f8b6f0965b8f';

function fixture() {
  const index = {
    source: { catalogSha256: 'abc' },
    objects: [
      { generationTypeName: 'SubSceneObjectReferences', assetGuid: scene, sceneObjectIdentifier0: '373', archiveId: 'a7111' },
      { generationTypeName: 'SubSceneObjectReferences', assetGuid: scene, sceneObjectIdentifier0: '374', archiveId: 'a2327' },
      { generationTypeName: 'SubSceneObjectReferences', assetGuid: scene, sceneObjectIdentifier0: '9', archiveId: 'adup' },
      { generationTypeName: 'Mesh', assetGuid: 'x', sceneObjectIdentifier0: '0', archiveId: 'amesh' },
    ],
  };
  const inventory = {
    archives: [
      { archiveId: 'a7111', named: [
        { type: 'Material', name: 'NPCDracula_Evolved_Wings' },
        { type: 'GameObject', name: 'Root_JNT' },
        { type: 'GameObject', name: 'HYB_NPCDracula_Evolved' },
      ] },
      { archiveId: 'a2327', named: [{ type: 'GameObject', name: 'HYB_NPCDracula_Standard' }] },
      { archiveId: 'adup', named: [{ type: 'GameObject', name: 'HYB_NPCDracula_Standard' }] },
      // A prefab archive that is not a scene section (a dependency) is not addressable by section.
      { archiveId: 'b6c0', named: [{ type: 'GameObject', name: 'HYB_VampireFemale_Prefab' }] },
      { archiveId: 'amesh', named: [{ type: 'GameObject', name: 'HYB_Mesh_Only' }] },
    ],
  };
  return { index, inventory };
}

test('HYB_ roots map to the scene section of the archive that holds them', () => {
  const { index, inventory } = fixture();
  const { models, sceneObjects } = buildModelSections(index, inventory);
  assert.equal(sceneObjects, 3);
  assert.deepEqual(models.map(m => [m.name, m.section, m.archive]), [
    ['HYB_NPCDracula_Evolved', `${scene}:373`, 'a7111'],
    ['HYB_NPCDracula_Standard', `${scene}:374`, 'a2327'],
  ]);
});

test('a name in two section archives is kept once, first archive by id, and reported', () => {
  const { index, inventory } = fixture();
  const { models, duplicates } = buildModelSections(index, inventory);
  assert.equal(models.filter(m => m.name === 'HYB_NPCDracula_Standard').length, 1);
  assert.deepEqual(duplicates, [{ name: 'HYB_NPCDracula_Standard', kept: 'a2327', skipped: 'adup' }]);
});

test('bones, materials, non-section archives and other prefixes are not models', () => {
  const { index, inventory } = fixture();
  const names = buildModelSections(index, inventory).models.map(m => m.name);
  assert.ok(!names.includes('Root_JNT'));
  assert.ok(!names.includes('NPCDracula_Evolved_Wings'));
  assert.ok(!names.includes('HYB_VampireFemale_Prefab'));
  assert.ok(!names.includes('HYB_Mesh_Only'));
  assert.deepEqual(buildModelSections(index, inventory, { prefix: 'Root_' }).models.map(m => m.name), ['Root_JNT']);
});

test('a comma-separated prefix list matches any of them', () => {
  const { index, inventory } = fixture();
  const names = buildModelSections(index, inventory, { prefix: 'Root_, HYB_NPCDracula_E' }).models.map(m => m.name);
  assert.deepEqual(names, ['HYB_NPCDracula_Evolved', 'Root_JNT']);
});

test('arguments', () => {
  assert.equal(parseArgs(['--prefix', 'CHAR_']).prefix, 'CHAR_');
  assert.equal(parseArgs(['--out', 'x.json']).out, 'x.json');
  assert.throws(() => parseArgs(['--bogus']), /unknown argument/);
});
