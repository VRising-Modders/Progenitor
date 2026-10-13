import { readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

function object(value, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label} must be an object`);
}
function array(value, label) {
  if (!Array.isArray(value)) throw new Error(`${label} must be an array`);
}
function string(value, label) {
  if (typeof value !== 'string' || !value.length) throw new Error(`${label} must be a nonempty string`);
}
function integer(value, min, max, label) {
  if (!Number.isInteger(value) || value < min || value > max) throw new Error(`${label} must be an integer in [${min}, ${max}]`);
}

/** Correlates sampled current states only; hashes do not establish controller identity. */
export function summarize(states, capture) {
  object(states, 'states'); array(states.controllers, 'controllers'); array(capture, 'capture');
  const byHash = new Map();
  for (const controller of states.controllers) {
    object(controller, 'controller'); string(controller.name, 'controller.name');
    string(controller.archiveId, 'controller.archiveId'); string(controller.pathID, 'controller.pathID');
    array(controller.stateMachines, 'stateMachines');
    for (const machine of controller.stateMachines) {
      object(machine, 'stateMachine'); integer(machine.stateMachineIndex, 0, 2147483647, 'stateMachineIndex');
      array(machine.states, 'machine.states');
      for (const state of machine.states) {
        object(state, 'state'); object(state.serializedSettings, 'serializedSettings');
        const hash = state.serializedSettings.m_FullPathID;
        integer(hash, 0, 4294967295, 'm_FullPathID');
        integer(state.stateIndex, 0, 2147483647, 'stateIndex');
        string(state.name, 'state.name'); string(state.fullPath, 'state.fullPath');
        const candidate = { archiveId: controller.archiveId, pathID: controller.pathID, controller: controller.name,
          stateMachineIndex: machine.stateMachineIndex, stateIndex: state.stateIndex, name: state.name, fullPath: state.fullPath };
        if (!byHash.has(hash)) byHash.set(hash, []);
        byHash.get(hash).push(candidate);
      }
    }
  }
  const profiles = new Map(); let inactiveRows = 0; let unavailableProfileRows = 0; let excludedLayerSamples = 0;
  for (const [sampleIndex, sample] of capture.entries()) {
    object(sample, `capture[${sampleIndex}]`); array(sample.rows, 'sample.rows');
    integer(sample.frame, 0, Number.MAX_SAFE_INTEGER, 'sample.frame');
    string(sample.utc, 'sample.utc');
    if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(sample.utc)
      || !Number.isFinite(Date.parse(sample.utc))
      || new Date(sample.utc).toISOString().slice(0, 19) !== sample.utc.slice(0, 19)) {
      throw new Error('sample.utc must be a valid explicit UTC ISO timestamp');
    }
    const seen = new Set();
    for (const row of sample.rows) {
      object(row, 'row');
      string(row.root, 'row.root');
      for (const field of ['controller', 'avatar']) {
        if (row[field] !== null) string(row[field], `row.${field}`);
      }
      integer(row.instance, -2147483648, 2147483647, 'row.instance');
      if (typeof row.active !== 'boolean') throw new Error('row.active must be boolean');
      array(row.layers, 'row.layers');
      const indices = new Set();
      for (const layer of row.layers) {
        object(layer, 'layer'); integer(layer.index, 0, 2147483647, 'layer.index');
        if (indices.has(layer.index)) throw new Error('duplicate layer index in row');
        indices.add(layer.index); string(layer.name, 'layer.name');
        integer(layer.fullPathHash, -2147483648, 2147483647, 'fullPathHash');
        if (!Number.isFinite(layer.weight) || layer.weight < 0 || layer.weight > 1) throw new Error('layer.weight must be finite in [0, 1]');
        for (const field of ['normalizedTime', 'length', 'speed', 'speedMultiplier']) {
          if (field in layer && !Number.isFinite(layer[field])) throw new Error(`layer.${field} must be finite`);
        }
        if ('length' in layer && layer.length < 0) throw new Error('layer.length must be nonnegative');
        for (const field of ['loop', 'transition']) {
          if (field in layer && typeof layer[field] !== 'boolean') throw new Error(`layer.${field} must be boolean`);
        }
      }
      const key = JSON.stringify([row.root, row.instance, row.controller, row.avatar]);
      if (seen.has(key)) throw new Error('duplicate profile in sample');
      seen.add(key);
      if (!row.active) inactiveRows++;
      if (row.controller === null || row.avatar === null) { unavailableProfileRows++; continue; }
      if (!row.active) continue;
      if (!profiles.has(key)) profiles.set(key, { root: row.root, instance: row.instance, controller: row.controller,
        avatar: row.avatar, sampleCount: 0, firstObservedUtc: sample.utc, lastObservedUtc: sample.utc,
        contributingLayerSampleCount: 0, states: new Map() });
      const profile = profiles.get(key); profile.sampleCount++; profile.lastObservedUtc = sample.utc;
      for (const layer of row.layers) {
        if (layer.index !== 0 && layer.weight === 0) { excludedLayerSamples++; continue; }
        profile.contributingLayerSampleCount++;
        const hash = layer.fullPathHash >>> 0;
        const stateKey = JSON.stringify([layer.index, layer.name, hash]);
        if (!profile.states.has(stateKey)) {
          const candidates = byHash.get(hash) ?? [];
          profile.states.set(stateKey, { layerIndex: layer.index, layerName: layer.name, fullPathHash: layer.fullPathHash,
            unsignedFullPathID: hash, sampleCount: 0, status: candidates.length === 0 ? 'unknown' : candidates.length === 1 ? 'hash-match' : 'ambiguous',
            candidates: candidates.map(candidate => ({ ...candidate })) });
        }
        profile.states.get(stateKey).sampleCount++;
      }
    }
  }
  const resultProfiles = [...profiles.values()].map(profile => ({ ...profile, states: [...profile.states.values()] }));
  return { schemaVersion: 1, verification: 'sampled-current-state-hash-correlation',
    caveat: 'Sampled observations are not complete animation coverage, all rendered frames, exact state durations, clip playback proof or proof of controller identity. All serialized hash candidates are retained; override/base controller relationships are not inferred.',
    sampleCount: capture.length, inactiveRows, unavailableProfileRows, excludedLayerSamples, profiles: resultProfiles,
    unknownStates: resultProfiles.flatMap(profile => profile.states.filter(state => state.status === 'unknown').map(state => ({ root: profile.root,
      instance: profile.instance, controller: profile.controller, avatar: profile.avatar, ...state }))) };
}

export async function runCli(args) {
  const options = {};
  for (let i = 0; i < args.length; i += 2) {
    if (!['--states', '--capture', '--out'].includes(args[i]) || !args[i + 1] || args[i + 1].startsWith('--') || options[args[i]]) {
      throw new Error('Usage: node player-animation-observations.mjs --states file.json --capture file.json --out report.json');
    }
    options[args[i]] = resolve(args[i + 1]);
  }
  if (Object.keys(options).length !== 3) throw new Error('Required: --states, --capture, --out');
  const [stateBytes, captureBytes] = await Promise.all([readFile(options['--states']), readFile(options['--capture'])]);
  const report = summarize(JSON.parse(stateBytes.toString('utf8')), JSON.parse(captureBytes.toString('utf8')));
  report.provenance = Object.fromEntries([['states', stateBytes], ['capture', captureBytes]].map(([key, bytes]) => [key,
    { path: options[`--${key}`], bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') }]));
  await writeFile(options['--out'], `${JSON.stringify(report, null, 2)}\n`, { flag: 'wx' });
  return report;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  runCli(process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = 1; });
}
