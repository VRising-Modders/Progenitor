import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { buildPlayerAnimationReport, main } from '../player-animations.mjs';

const row = (name, pathID = '9007199254740993', type = 'AnimationClip', archiveId = 'archive') => ({ name, pathID, type, archiveId });
const inventory = rows => ({ available: true, source: { catalogSha256: 'stamp' }, counts: { failedArchives: 0, rejectedRows: 0 }, rows });

test('anchored player candidates exclude NPC and substring false positives; retain controller context', () => {
  const report = buildPlayerAnimationReport(inventory([
    ...['VampireMale_Run', 'VampireFemale_Run', '1HWhip_Vampire_Lash', '2HLongbow_Vampire_Shoot', '2HLongbow_VampireMale_EquipReady', 'VampireCrossbow_Shoot', 'WeaponLongbow_Vampire_Shoot'].map(name => row(name)),
    ...['NPCVampire_Run', 'CreatureWitheredVampire_Run', 'Other_VampireMale_Run', 'VampireMalevolent_Run'].map(name => row(name)),
    row('PlayerController', '2', 'AnimatorController'), row('Overrides', '3', 'AnimatorOverrideController'), row('VampireMale_Run', '4', 'GameObject'),
  ]));
  assert.equal(report.counts.playerCandidates, 7);
  assert.equal(report.counts.unclassifiedContext, 6);
  assert.equal(report.counts.animationRows, 13);
  assert.equal(report.rows[4].bodyType, 'male');
  assert.equal(report.rows[2].bodyType, 'unspecified');
});

test('duplicate names and body/phase groups preserve distinct identities without sharing claims', () => {
  const report = buildPlayerAnimationReport(inventory([row('VampireMale_Cast_Antip'), row('VampireMale_Cast_Antip', '2', 'AnimationClip', 'other'), row('VampireFemale_Cast_Post')]));
  assert.equal(report.rows[0].pathID, '9007199254740993');
  assert.equal(report.rows.length, 3);
  assert.equal(report.actionGroups.length, 1);
  assert.equal(report.actionGroups[0].members.length, 3);
  assert.equal(report.actionGroups[0].sharingConfirmed, false);
  assert.deepEqual(report.rows.map(r => r.phase), ['Antip', 'Antip', 'Post']);
  for (const r of report.rows) { assert.equal(r.controllerLinks, null); assert.equal(r.timings, null); assert.equal(r.verification.playback, 'not-observed'); }
});

test('suffix tokens do not imply transitions or lifecycle', () => {
  const report = buildPlayerAnimationReport(inventory([row('VampireMale_Unknown_ExecPost'), row('VampireMale_Unknown_ReadyToIdle')]));
  assert.equal(report.rows[0].phase, 'ExecPost');
  assert.deepEqual(report.rows[0].familyTags, ['unclassified']);
  assert.equal(report.rows[1].phase, null);
  assert.equal(report.rows[1].normalizedName, 'Unknown_ReadyToIdle');
  assert.ok(report.rows.every(r => !r.familyTags.includes('lifecycle')));
});

test('authored Reg phase and action families avoid controller and weapon substring false positives', () => {
  const report = buildPlayerAnimationReport(inventory([
    row('VampireMale_Crossbow_Shoot_Reg'), row('VampireMale_AnimatorController', '2', 'AnimatorController'),
    row('VampireMale_Bow_Exec'), row('VampireMale_EquipReady'), row('VampireMale_Stunned'),
    row('VampireMale_Feed_Loop'), row('VampireMale_Transform_Exec'),
  ]));
  assert.equal(report.rows[0].phase, 'Reg');
  assert.equal(report.rows[0].normalizedName, 'Crossbow_Shoot');
  assert.ok(!report.rows[0].familyTags.includes('social'));
  assert.deepEqual(report.rows[1].familyTags, ['unclassified']);
  assert.ok(report.rows[2].familyTags.includes('social'));
  for (const [index, family] of [[3, 'equipment'], [4, 'reaction'], [5, 'feeding'], [6, 'transformation']]) assert.ok(report.rows[index].familyTags.includes(family));
});

test('unavailable and incomplete inventory is explicit and retains provenance', () => {
  const missing = buildPlayerAnimationReport({ available: false, reason: 'unstamped', rows: [] });
  assert.equal(missing.coverage, 'incomplete-or-unknown');
  assert.deepEqual(missing.warnings, ['unstamped']);
  const partial = buildPlayerAnimationReport({ ...inventory([row('VampireMale_Run')]), counts: { failedArchives: 1, rejectedRows: 2 }, warnings: ['bad row'] });
  assert.equal(partial.source.catalogSha256, 'stamp');
  assert.equal(partial.coverage, 'incomplete-or-unknown');
  assert.equal(partial.warnings.length, 3);
  const capped = buildPlayerAnimationReport(inventory([row('VampireMale_Run')]), { byClass: { AnimationClip: 3 }, archives: [{ bodiesTruncated: true }] });
  assert.equal(capped.counts.objectTableAnimationClipCount, 3);
  assert.equal(capped.counts.recoveredNamedAnimationClipCount, 1);
  assert.equal(capped.counts.bodiesTruncatedArchiveCount, 1);
  assert.equal(capped.coverage, 'incomplete-or-unknown');
  assert.equal(capped.warnings.length, 2);
});

test('CLI reads stamped inventory and exclusively creates output', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'player-animations-'));
  try {
    writeFileSync(path.join(dir, 'catalog-inventory.json'), JSON.stringify({ source: { catalogSha256: 'stamp' }, failed: 0, archives: [{ archiveId: 'archive', named: [row('VampireMale_Run')] }] }));
    const out = path.join(dir, 'report.json');
    main(['--inventory', dir, '--out', out]);
    const original = readFileSync(out, 'utf8');
    assert.equal(JSON.parse(original).counts.playerCandidates, 1);
    assert.throws(() => main(['--inventory', dir, '--out', out]), /EEXIST/);
    assert.equal(readFileSync(out, 'utf8'), original);
    assert.throws(() => main([]), /Usage:/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('impalement and incapacitation retain coverage families', () => {
  const report = buildPlayerAnimationReport(inventory([row('VampireFemale_Impaled_Loop'), row('VampireFemale_Incapacitated_Lying_Post')]));
  assert.ok(report.rows[0].familyTags.includes('reaction'));
  assert.ok(report.rows[1].familyTags.includes('lifecycle'));
});
