// Offline authored bindings. No playback, fallback or weapon-enum behavior is inferred.
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { loadInventory } from './lib/inventory.mjs';

function id(value) {
  if (typeof value === 'string' && /^-?\d+$/.test(value)) return value;
  if (Number.isSafeInteger(value)) return String(value);
  throw new Error('Path identity must be a lossless decimal string or safe integer.');
}
const key = (archiveId, pathID) => `${archiveId}:${id(pathID)}`;
function checked(object) {
  if (!object || object.error || !Number.isInteger(object.size) || object.size < 0 || object.consumed !== object.size || !object.value) throw new Error(`Incomplete decode: ${object?.pathID ?? 'missing object'}`);
  return object;
}

export function buildPlayerBindings({ inventory, collection, prefabs, overrides = [] }) {
  if (!inventory?.available) throw new Error(inventory?.reason ?? 'Inventory unavailable.');
  const files = [collection, ...Object.values(prefabs ?? {}), ...overrides];
  const objects = new Map();
  const names = new Map((inventory.rows ?? []).map(row => [key(row.archiveId, row.pathID), row]));
  for (const file of files) {
    if (!file?.archiveId || !Array.isArray(file.decoded) || !Array.isArray(file.externals)) throw new Error('Expected archiveId, decoded and externals in each decode file.');
    for (const object of file.decoded) {
      const identity = key(file.archiveId, object.pathID);
      if (objects.has(identity)) throw new Error(`Duplicate decoded identity: ${identity}`);
      objects.set(identity, { file, object });
    }
  }
  const unresolved = [];
  function resolve(file, pointer, field) {
    if (!pointer || !Number.isInteger(pointer.m_FileID) || pointer.m_FileID < 0) throw new Error(`Invalid PPtr at ${field}`);
    const pathID = id(pointer.m_PathID);
    if (pathID === '0') return { status: 'null', pointer, fallback: 'unproven' };
    const archiveId = pointer.m_FileID === 0 ? file.archiveId : file.externals[pointer.m_FileID - 1]?.pathName;
    if (!archiveId) { const result = { status: 'unresolved', pointer, reason: 'external-file-slot-missing', field }; unresolved.push(result); return result; }
    const identity = key(archiveId, pathID);
    const decoded = objects.get(identity);
    if (decoded) checked(decoded.object);
    const named = names.get(identity);
    const result = { archiveId, pathID, pointer, status: decoded || named ? 'metadata-resolved' : 'unresolved', resolution: decoded ? 'decoded-object' : named ? 'inventory-name-only' : 'missing', name: decoded?.object.value.m_Name || named?.name || null, type: named?.type ?? decoded?.object.typeName ?? null };
    if (result.status === 'unresolved') unresolved.push({ ...result, field, reason: 'target-not-in-input-or-inventory' });
    return result;
  }
  function target(reference) { return reference.archiveId ? objects.get(key(reference.archiveId, reference.pathID)) : null; }
  function clip(file, pointer, field) {
    const reference = resolve(file, pointer, field);
    const decoded = target(reference);
    if ((decoded && decoded.object.classID !== 74) || (reference.type && reference.type !== 'AnimationClip')) throw new Error(`Expected AnimationClip at ${field}`);
    return reference;
  }
  function typed(file, pointer, field, classes, types) {
    const reference = resolve(file, pointer, field);
    const decoded = target(reference);
    if ((decoded && !classes.includes(decoded.object.classID)) || (reference.type && !types.includes(reference.type))) throw new Error(`Incorrect target class at ${field}`);
    if (!decoded && reference.status === 'metadata-resolved' && !reference.type) {
      reference.status = 'unresolved'; reference.resolution = 'type-unknown';
      unresolved.push({ ...reference, field, reason: 'target-type-unknown' });
    }
    return reference;
  }
  const controller = (file, pointer, field) => typed(file, pointer, field, [91, 221], ['AnimatorController', 'AnimatorOverrideController']);
  const avatar = (file, pointer, field) => typed(file, pointer, field, [90], ['Avatar']);
  const collections = collection.decoded.filter(object => Array.isArray(object.value?.AnimationList));
  if (collections.length !== 1) throw new Error('Expected exactly one authored AnimationCollection.');
  const selected = checked(collections[0]);
  const sharedIdentity = { archiveId: collection.archiveId, pathID: id(selected.pathID), name: selected.value.m_Name };
  const actions = selected.value.AnimationList.map((raw, index) => {
    if (typeof raw.Name !== 'string' || !raw.Name || !raw.AdvancedAnimationClip || !Array.isArray(raw.AdvancedAnimationClip.AnimationClips) || !raw.AdvancedAnimationClip.AnimationClips.length) throw new Error(`Malformed authored action at actions[${index}]`);
    return { logicalName: raw.Name, raw, clipMembers: raw.AdvancedAnimationClip.AnimationClips.map((member, memberIndex) => ({ raw: member, clip: clip(collection, member.AnimationClip, `actions[${index}].clips[${memberIndex}]`) })) };
  });
  const bodies = {};
  for (const [bodyType, file] of Object.entries(prefabs ?? {})) {
    const candidates = file.decoded.filter(object => object.value?.AnimationCollection);
    const pairs = candidates.flatMap(animation => file.decoded.filter(weapon => Array.isArray(weapon.value?.WeaponControllers) && weapon.value.m_GameObject.m_FileID === animation.value.m_GameObject.m_FileID && id(weapon.value.m_GameObject.m_PathID) === id(animation.value.m_GameObject.m_PathID)).map(weapon => ({ animation, weapon })));
    if (pairs.length !== 1) throw new Error(`Expected one root animation/weapon component pair for ${bodyType}.`);
    const { animation, weapon } = pairs[0]; checked(animation); checked(weapon);
    const rootReference = resolve(file, animation.value.m_GameObject, `${bodyType}.root`);
    const root = target(rootReference); if (!root || checked(root.object).classID !== 1) throw new Error(`Missing root GameObject for ${bodyType}.`);
    const components = root.object.value.m_Component.map(entry => entry.component);
    for (const component of [animation, weapon]) if (!components.some(ref => ref.m_FileID === 0 && root.file.archiveId === file.archiveId && id(ref.m_PathID) === id(component.pathID))) throw new Error(`Animation component not attached to root for ${bodyType}.`);
    const collectionReference = resolve(file, animation.value.AnimationCollection, `${bodyType}.collection`);
    if (collectionReference.archiveId !== sharedIdentity.archiveId || collectionReference.pathID !== sharedIdentity.pathID) throw new Error(`Collection input does not match ${bodyType} prefab.`);
    const animator = resolve(file, animation.value.Animator, `${bodyType}.animator`);
    const animatorObject = target(animator);
    if (!animatorObject) unresolved.push({ ...animator, field: `${bodyType}.animator`, reason: 'animator-decode-not-supplied' });
    if (animatorObject && checked(animatorObject.object).classID !== 95) throw new Error(`Animator pointer has incorrect class for ${bodyType}.`);
    const weaponAnimator = resolve(file, weapon.value.Animator, `${bodyType}.weaponAnimator`);
    if (animator.archiveId !== weaponAnimator.archiveId || animator.pathID !== weaponAnimator.pathID) throw new Error(`Different animation/weapon Animator pointers for ${bodyType}.`);
    bodies[bodyType] = { root: rootReference, collection: collectionReference, animator, animatorAvatar: animatorObject?.object.value.m_Avatar ? avatar(animatorObject.file, animatorObject.object.value.m_Avatar, `${bodyType}.animator.avatar`) : null, animatorSettings: animatorObject ? Object.fromEntries(Object.entries(animatorObject.object.value).filter(([name]) => !['m_GameObject', 'm_Avatar', 'm_Controller'].includes(name))) : null, animatorController: animatorObject?.object.value.m_Controller ? controller(animatorObject.file, animatorObject.object.value.m_Controller, `${bodyType}.animator.controller`) : null, weaponComponent: { archiveId: file.archiveId, pathID: id(weapon.pathID) }, equippedWeaponIndex: weapon.value.EquippedWeaponIndex, weaponSlots: weapon.value.WeaponControllers.map((slot, index) => ({ index, raw: slot, controller: controller(file, slot.AnimationController, `${bodyType}.weaponSlots[${index}]`), mountIKPreset: slot.MountIKPreset })), provenance: 'metadata-resolved; playback-not-observed; slot-enum-association-unproven' };
  }
  const overrideControllers = overrides.flatMap(file => file.decoded.filter(object => object.classID === 221).map(object => {
    checked(object);
    return { archiveId: file.archiveId, pathID: id(object.pathID), name: object.value.m_Name, baseController: controller(file, object.value.m_Controller, 'override.baseController'), mappings: object.value.m_Clips.map((entry, index) => ({ index, raw: entry, original: clip(file, entry.m_OriginalClip, 'override.original'), replacement: clip(file, entry.m_OverrideClip, 'override.replacement'), fallback: id(entry.m_OverrideClip.m_PathID) === '0' ? 'unproven' : null })) };
  }));
  const requiredAvatar = selected.value.RequiredAvatar ? avatar(collection, selected.value.RequiredAvatar, 'collection.requiredAvatar') : null;
  const collectionControllers = (selected.value.Controllers?.array ?? []).map((pointer, index) => controller(collection, pointer, `collection.controllers[${index}]`));
  return { schemaVersion: 1, source: inventory.source ?? null, inventoryGenerated: inventory.generated ?? null, decodeInputGameStamp: 'not-independently-verified; decoded inputs lack a game stamp', provenance: 'metadata-resolved; playback-not-observed', bindingResolution: unresolved.length ? 'partial' : 'complete', coverageNote: 'Resolved targets may be inventory names only; complete pointer resolution does not prove complete controller coverage or playback.', sharedCollection: sharedIdentity, requiredAvatar, collectionControllers, collectionRaw: selected.value, logicalActions: actions, bodies, overrideControllers, unresolved, warnings: inventory.warnings ?? [] };
}

export function main(args = process.argv.slice(2)) {
  if (args.length !== 4 || args[0] !== '--input' || args[2] !== '--out') throw new Error('Usage: node tools/catalog/player-animation-bindings.mjs --input <manifest.json> --out <fresh.json>');
  const manifestPath = path.resolve(args[1]);
  const fingerprint = (file, bytes = readFileSync(file)) => ({ path: file, bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') });
  const manifestBytes = readFileSync(manifestPath);
  const manifest = JSON.parse(manifestBytes.toString('utf8'));
  if (manifest.schemaVersion !== 1 || !manifest.inventoryDirectory || !manifest.collection || !manifest.prefabs?.male || !manifest.prefabs?.female || !Array.isArray(manifest.overrides)) throw new Error('Invalid bindings manifest schemaVersion 1.');
  const local = value => path.resolve(path.dirname(manifestPath), value);
  const decodedInputs = [];
  const read = value => { const file = local(value); const bytes = readFileSync(file); decodedInputs.push(fingerprint(file, bytes)); return JSON.parse(bytes.toString('utf8')); };
  const inventoryPath = path.join(local(manifest.inventoryDirectory), 'catalog-inventory.json');
  const inventoryStamp = fingerprint(inventoryPath);
  const inventory = loadInventory(local(manifest.inventoryDirectory));
  if (fingerprint(inventoryPath).sha256 !== inventoryStamp.sha256) throw new Error('Inventory changed while being read.');
  const report = buildPlayerBindings({ inventory, collection: read(manifest.collection), prefabs: Object.fromEntries(Object.entries(manifest.prefabs).map(([body, value]) => [body, read(value)])), overrides: manifest.overrides.map(read) });
  report.inputProvenance = { manifest: fingerprint(manifestPath, manifestBytes), decodedInputs, inventory: inventoryStamp, gameStampVerification: 'not-independently-verified' };
  writeFileSync(path.resolve(args[3]), `${JSON.stringify(report, null, 2)}\n`, { flag: 'wx' });
  return report;
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
