import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

export const stages = [1, 10, 50, 100, 128, 129, 250, 256, 257, 500, 1000, 10000];
const json = value => JSON.stringify(value, null, 2) + '\n';

// Offline fixture writer only: never contacts a process or installs content.
export function generate(output, reserved = 0) {
  if (!Number.isInteger(reserved) || reserved < 0 || reserved > 256)
    throw new Error('reserved must be an integer from 0 through 256');
  const root = path.resolve(output);
  if (fs.existsSync(root)) throw new Error('Output must be a new directory; existing content is never overwritten');
  // Refuse obvious runtime destinations even when the requested leaf does not exist.
  if (root.split(/[\\/]/).some(p => /^(bepinex|vrisingserver|progenitor-content)$/i.test(p)))
    throw new Error('Use an offline scratch directory, outside game content roots');
  fs.mkdirSync(root, { recursive: true });
  const matrix = [];
  for (const count of stages) {
    const stage = `count-${String(count).padStart(4, '0')}`;
    let bytes = 0;
    const packs = [];
    for (let start = 0; start < count; start += 128) {
      const id = `scale.bone-sword.p${String(start / 128 + 1).padStart(3, '0')}`;
      const items = Array.from({ length: Math.min(128, count - start) }, (_, offset) => {
        const number = String(start + offset + 1).padStart(5, '0');
        return {
          id: `${id}.item${number}`, cloneOf: 'Item_Weapon_Sword_T01_Bone',
          name: `Scale Bone Sword ${number}`,
          description: 'Scale fixture: +100 health, physical power and spell power.',
          statBonuses: { maxHealth: 100, physicalPower: 100, spellPower: 100 }
        };
      });
      const content = Buffer.from(json({ schema: 1, items }), 'utf8');
      if (content.length > 256 * 1024) throw new Error('Generated pack exceeds file-size guard');
      const directory = path.join(root, stage, id);
      fs.mkdirSync(directory, { recursive: true });
      fs.writeFileSync(path.join(directory, 'items.json'), content);
      fs.writeFileSync(path.join(directory, 'pack.json'), json({
        schema: 1, id, version: '1.0.0', content: 'items.json',
        sha256: createHash('sha256').update(content).digest('hex')
      }));
      bytes += content.length;
      packs.push({ id, count: items.length });
    }
    matrix.push({ stage, count, packs, contentBytes: bytes,
      totalDefinitionsWithReserved: count + reserved,
      expectation: count + reserved <= 256 ? 'within-default-definition-guard' : 'guard-refusal-not-capacity-test',
      liveStatus: 'not-run' });
  }
  fs.writeFileSync(path.join(root, 'matrix.json'), json({
    schema: 1, reservedDefinitions: reserved, definitionGuard: 256,
    prerequisite: 'Bone sword equip/unequip and compatible-client synchronization must pass first.',
    stages: matrix
  }));
  fs.writeFileSync(path.join(root, 'measurements.csv'),
    'stage,run,mode,registered_definitions,client_definitions,load_ms,join_ms,server_memory_mb,client_memory_mb,frame_p50_ms,frame_p95_ms,inventory_open_ms,errors,save_restart,stat_delta,notes\n');
  return matrix;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    if (process.argv.length < 3 || process.argv.length > 4)
      throw new Error('Usage: node tools/content-pack-scale/generate.mjs <new-output-directory> [reserved-definitions]');
    const matrix = generate(process.argv[2], Number(process.argv[3] ?? 0));
    console.log(`Prepared ${matrix.length} offline stages. Nothing installed or granted.`);
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
