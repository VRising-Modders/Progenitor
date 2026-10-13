import test from 'node:test';
import assert from 'node:assert/strict';
import { buildPlayerBindings } from '../player-animation-bindings.mjs';

const ptr = (pathID, m_FileID = 0) => ({ m_FileID, m_PathID: String(pathID) });
const obj = (pathID, classID, value) => ({ pathID: String(pathID), classID, size: 10, consumed: 10, value });
function fixture() {
  const collection = { archiveId: 'collection', externals: [], decoded: [obj(1, 114, { m_Name: 'Shared', AnimationList: [{ Name: 'Attack', AdvancedAnimationClip: { Type: 0, AnimationClips: [{ AnimationClip: ptr(99), Speed: 0.5, Threshold: 0 }] } }] }), obj(2, 91, { m_Name: 'Base' })] };
  const prefab = body => ({ archiveId: body, externals: [{ pathName: 'collection' }, { pathName: `${body}-overrides` }], decoded: [obj(1, 1, { m_Name: body, m_Component: [{ component: ptr(2) }, { component: ptr(3) }, { component: ptr(4) }] }), obj(2, 114, { m_GameObject: ptr(1), AnimationCollection: ptr(1, 1), Animator: ptr(4) }), obj(3, 114, { m_GameObject: ptr(1), Animator: ptr(4), EquippedWeaponIndex: 0, WeaponControllers: [{ AnimationController: ptr(10, 2), MountIKPreset: 1 }] }), obj(4, 95, { m_Controller: ptr(10, 2) })] });
  const override = body => ({ archiveId: `${body}-overrides`, externals: [{ pathName: 'collection' }], decoded: [obj(10, 221, { m_Name: `${body} Controller`, m_Controller: ptr(2, 1), m_Clips: [{ m_OriginalClip: ptr(99, 1), m_OverrideClip: ptr(0) }] })] });
  return { inventory: { available: true, source: { catalogSha256: 'stamp' }, rows: [{ archiveId: 'collection', pathID: '99', type: 'AnimationClip', name: 'Correct' }, { archiveId: 'male', pathID: '99', type: 'AnimationClip', name: 'Wrong' }] }, collection, prefabs: { male: prefab('male'), female: prefab('female') }, overrides: [override('male'), override('female')] };
}
test('preserves exact archive/path identity, shared collection and distinct body controllers', () => {
  const result = buildPlayerBindings(fixture());
  assert.equal(result.logicalActions[0].clipMembers[0].clip.name, 'Correct');
  assert.equal(result.logicalActions[0].raw.AdvancedAnimationClip.AnimationClips[0].Speed, 0.5);
  assert.equal(result.bodies.male.collection.archiveId, 'collection');
  assert.equal(result.bodies.female.collection.pathID, '1');
  assert.equal(result.bodies.male.weaponSlots[0].controller.archiveId, 'male-overrides');
  assert.equal(result.bodies.female.animatorController.name, 'female Controller');
  assert.equal(result.overrideControllers[0].baseController.name, 'Base');
  assert.equal(result.overrideControllers[0].mappings[0].replacement.status, 'null');
  assert.equal(result.overrideControllers[0].mappings[0].fallback, 'unproven');
});
test('rejects partial and failed relevant decodes', () => {
  for (const mutate of [input => { input.collection.decoded[0].consumed = 9; }, input => { input.prefabs.male.decoded[3].error = 'failed'; }, input => { input.overrides[0].decoded[0].consumed = 1; }]) {
    const input = fixture(); mutate(input); assert.throws(() => buildPlayerBindings(input), /Incomplete decode/);
  }
});
test('reports missing references and absent external slots explicitly', () => {
  const input = fixture();
  input.collection.decoded[0].value.AnimationList[0].AdvancedAnimationClip.AnimationClips[0].AnimationClip = ptr(987);
  input.overrides[0].decoded[0].value.m_Clips[0].m_OverrideClip = ptr(555, 2);
  const result = buildPlayerBindings(input);
  assert.equal(result.logicalActions[0].clipMembers[0].clip.status, 'unresolved');
  assert.ok(result.unresolved.some(ref => ref.reason === 'external-file-slot-missing'));
  assert.ok(result.unresolved.some(ref => ref.archiveId === 'collection' && ref.pathID === '987'));
});
test('refuses mismatched collection and detached root components', () => {
  const input = fixture(); input.prefabs.female.decoded[1].value.AnimationCollection = ptr(2, 1);
  assert.throws(() => buildPlayerBindings(input), /does not match/);
  const detached = fixture(); detached.prefabs.male.decoded[0].value.m_Component = [];
  assert.throws(() => buildPlayerBindings(detached), /not attached/);
});
test('rejects lossy path identities', () => {
  const input = fixture(); input.collection.decoded[0].value.AnimationList[0].AdvancedAnimationClip.AnimationClips[0].AnimationClip = ptr(1); input.collection.decoded[0].value.AnimationList[0].AdvancedAnimationClip.AnimationClips[0].AnimationClip.m_PathID = 9007199254740992;
  assert.throws(() => buildPlayerBindings(input), /lossless/);
});
test('rejects negative sizes and wrong clip classes', () => {
  const negative = fixture(); negative.collection.decoded[0].size = -1; negative.collection.decoded[0].consumed = -1;
  assert.throws(() => buildPlayerBindings(negative), /Incomplete decode/);
  const wrong = fixture(); wrong.inventory.rows[0].type = 'Avatar';
  assert.throws(() => buildPlayerBindings(wrong), /Expected AnimationClip/);
});
test('reports avatar identity and root-motion settings; missing animator decode is partial', () => {
  const input = fixture(); input.collection.decoded[0].value.RequiredAvatar = ptr(8); input.collection.decoded.push(obj(8, 90, { m_Name: 'RequiredAvatar' }));
  input.prefabs.female.decoded[3].value.m_Avatar = ptr(9); input.prefabs.female.decoded[3].value.m_ApplyRootMotion = false;
  input.inventory.rows.push({ archiveId: 'female', pathID: '9', type: 'Avatar', name: 'FemaleAvatar' });
  const result = buildPlayerBindings(input);
  assert.equal(result.requiredAvatar.name, 'RequiredAvatar'); assert.equal(result.bodies.female.animatorAvatar.name, 'FemaleAvatar'); assert.equal(result.bodies.female.animatorSettings.m_ApplyRootMotion, false);
  assert.equal(result.bindingResolution, 'complete');
  input.prefabs.male.decoded.pop(); const partial = buildPlayerBindings(input);
  assert.equal(partial.bindingResolution, 'partial'); assert.ok(partial.unresolved.some(ref => ref.reason === 'animator-decode-not-supplied'));
});
test('rejects missing or empty authored member arrays', () => {
  for (const value of [undefined, {}, { AnimationClips: null }, { AnimationClips: [] }]) {
    const input = fixture(); input.collection.decoded[0].value.AnimationList[0].AdvancedAnimationClip = value;
    assert.throws(() => buildPlayerBindings(input), /Malformed authored action/);
  }
});
test('rejects controller and avatar references to wrong decoded classes', () => {
  const base = fixture(); base.collection.decoded[1].classID = 1;
  assert.throws(() => buildPlayerBindings(base), /Incorrect target class/);
  const wrongAvatar = fixture(); wrongAvatar.collection.decoded[0].value.RequiredAvatar = ptr(2);
  assert.throws(() => buildPlayerBindings(wrongAvatar), /Incorrect target class/);
  const wrongSlot = fixture(); wrongSlot.prefabs.male.decoded[2].value.WeaponControllers[0].AnimationController = ptr(1);
  assert.throws(() => buildPlayerBindings(wrongSlot), /Incorrect target class/);
});
test('unknown inventory-only controller type remains partial', () => {
  const input = fixture(); input.overrides[0].decoded[0].value.m_Controller = ptr(555, 1);
  input.inventory.rows.push({ archiveId: 'collection', pathID: '555', name: 'Unknown' });
  const result = buildPlayerBindings(input);
  assert.equal(result.bindingResolution, 'partial');
  assert.ok(result.unresolved.some(ref => ref.reason === 'target-type-unknown'));
});
