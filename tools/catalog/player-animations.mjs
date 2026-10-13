// Offline name evidence only. This report never resolves bindings or contacts the game.
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadInventory } from './lib/inventory.mjs';

const TYPES = new Set(['AnimationClip', 'AnimatorController', 'AnimatorOverrideController']);
const PREFIX = /^(VampireMale_|VampireFemale_|1HWhip_Vampire_|2HLongbow_VampireMale_|2HLongbow_Vampire_|VampireCrossbow_|WeaponLongbow_Vampire_)/;
// Authored suffix tokens, preserved literally; their meaning/order is not inferred.
const PHASE = /_(Antip|Exec|ExecPost|MovingPost|Post|Loop|Hold|Init|Repeat|Travel|Landing|Reg)$/;
const FAMILIES = [
  ['locomotion', /walk|run|strafe|trott|locomotion|idle/i],
  ['combat', /swing|attack|combo|shoot|strike|lash|block|sweep|weapon|crossbow|longbow|whip|sword|mace|spear|scythe|pistol|claw|axe|slash/i],
  ['spell', /spell|cast|bolt|blast|magic|channel|chaos|frost|blood|unholy|illusion|storm/i],
  ['mounted', /mount|horse|ride|riding/i],
  ['interaction', /interact|gather|harvest|fish|coffin|gate|craft|drink|feed|consume|pickup|loot|sit/i],
  ['social', /emote|dance|wave|salute|clap|laugh|taunt|(?:^|_)bow(?:_|$)|cheer/i],
  ['lifecycle', /death|dead|die|spawn|respawn|revive|resurrect|incapacitated|pinneddown/i],
  ['movement-ability', /dash|jump|leap|teleport|dodge|(?:^|_)roll(?:_|$)|flip/i],
  ['equipment', /equip|unequip|weaponcoating/i],
  ['reaction', /stun|hitreact|hurt|knockback|knockdown|flinch|impaled/i],
  ['feeding', /feed|consume|bloodsuck|bite/i],
  ['transformation', /transform|shapeshift|formchange/i],
];

function classify(row) {
  const match = PREFIX.exec(row.name);
  const candidate = Boolean(match);
  const bodyType = /^VampireMale_|^2HLongbow_VampireMale_/.test(row.name) ? 'male'
    : /^VampireFemale_/.test(row.name) ? 'female' : 'unspecified';
  const phase = candidate && row.type === 'AnimationClip' ? PHASE.exec(row.name)?.[1] ?? null : null;
  let normalizedName = candidate ? row.name.slice(match[0].length) : null;
  if (phase) normalizedName = normalizedName.slice(0, -(phase.length + 1));
  // Preserve weapon context when normalizing special player prefixes.
  if (candidate && !/^Vampire(?:Male|Female)_/.test(match[0])) {
    normalizedName = `${match[0].replace(/_Vampire(?:Male)?_$|_$/, '')}_${normalizedName}`;
  }
  const familyTags = candidate && row.type === 'AnimationClip' ? FAMILIES.filter(([, pattern]) => pattern.test(row.name.slice(match[0].length))).map(([tag]) => tag) : [];
  return {
    archiveId: row.archiveId, pathID: row.pathID, name: row.name, type: row.type,
    playerCandidate: candidate, classification: candidate ? 'name-inferred' : 'unclassified-context',
    bodyType: candidate ? bodyType : 'unspecified', familyTags: familyTags.length ? familyTags : ['unclassified'],
    phase, normalizedName, controllerLinks: null, timings: null,
    verification: { binding: 'not-observed', playback: 'not-observed', sharing: 'not-observed' },
  };
}

/** Build from the result of loadInventory; incomplete inputs remain explicitly incomplete. */
export function buildPlayerAnimationReport(inventory, rawInventory = null) {
  const warnings = [...(inventory.warnings ?? [])];
  if (!inventory.available) warnings.push(inventory.reason ?? 'Inventory unavailable.');
  if (inventory.summaryMissing) warnings.push('Adjacent summary missing; inventory stamp retained.');
  if (inventory.counts?.failedArchives) warnings.push(`${inventory.counts.failedArchives} archives failed inventory generation; coverage is incomplete.`);
  if (inventory.counts?.rejectedRows) warnings.push(`${inventory.counts.rejectedRows} rows rejected by the inventory loader; coverage is incomplete.`);
  const rows = inventory.available ? inventory.rows.filter(row => TYPES.has(row.type)).map(classify) : [];
  const objectTableAnimationClipCount = rawInventory?.byClass?.AnimationClip ?? null;
  const recoveredNamedAnimationClipCount = rows.filter(row => row.type === 'AnimationClip').length;
  const bodiesTruncatedArchiveCount = rawInventory ? (rawInventory.archives ?? []).filter(archive => archive.bodiesTruncated).length : null;
  if (bodiesTruncatedArchiveCount) warnings.push(`${bodiesTruncatedArchiveCount} archives had truncated body reads; named animation coverage is incomplete.`);
  if (objectTableAnimationClipCount !== null && recoveredNamedAnimationClipCount < objectTableAnimationClipCount) warnings.push(`Only ${recoveredNamedAnimationClipCount} of ${objectTableAnimationClipCount} object-table AnimationClip entries have recovered names.`);
  const groups = new Map();
  for (const row of rows.filter(row => row.playerCandidate)) {
    const key = `${row.type}:${row.normalizedName}`;
    if (!groups.has(key)) groups.set(key, { normalizedName: row.normalizedName, type: row.type, sharingConfirmed: false, verification: 'not-observed', members: [] });
    groups.get(key).members.push({ archiveId: row.archiveId, pathID: row.pathID, name: row.name, bodyType: row.bodyType, phase: row.phase });
  }
  return {
    schemaVersion: 1, source: inventory.source ?? null, inventoryGenerated: inventory.generated ?? null,
    inventoryState: inventory.available ? 'stamped' : 'unavailable',
    coverage: inventory.available && inventory.counts?.failedArchives === 0 && inventory.counts?.rejectedRows === 0 && bodiesTruncatedArchiveCount === 0 && objectTableAnimationClipCount === recoveredNamedAnimationClipCount ? 'loaded-named-rows' : 'incomplete-or-unknown',
    note: 'Names suggest candidates only. Named inventory coverage is not all game animations. Bindings, phase order, timings, runtime playback and body sharing are not observed.',
    verification: 'not-observed', warnings,
    counts: { inventory: inventory.counts ?? null, objectTableAnimationClipCount, recoveredNamedAnimationClipCount, bodiesTruncatedArchiveCount, animationRows: rows.length, playerCandidates: rows.filter(row => row.playerCandidate).length, unclassifiedContext: rows.filter(row => !row.playerCandidate).length, byType: Object.fromEntries([...TYPES].map(type => [type, rows.filter(row => row.type === type).length])), actionGroups: groups.size },
    rows, actionGroups: [...groups.values()],
  };
}

export function main(args = process.argv.slice(2)) {
  if (args.length !== 4 || args[0] !== '--inventory' || args[2] !== '--out') throw new Error('Usage: node tools/catalog/player-animations.mjs --inventory <dir> --out <new JSON path>');
  const inventory = loadInventory(path.resolve(args[1]));
  if (!inventory.available) throw new Error(inventory.reason);
  const rawInventory = JSON.parse(readFileSync(path.join(inventory.dir, 'catalog-inventory.json'), 'utf8'));
  if (rawInventory.source?.catalogSha256 !== inventory.source.catalogSha256 || rawInventory.generated !== (inventory.generated ?? undefined)) throw new Error('Inventory changed while being read; retry with a stable inventory.');
  const report = buildPlayerAnimationReport(inventory, rawInventory);
  writeFileSync(path.resolve(args[3]), `${JSON.stringify(report, null, 2)}\n`, { flag: 'wx' });
  return report;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
