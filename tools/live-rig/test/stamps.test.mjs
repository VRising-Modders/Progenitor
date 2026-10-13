import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import {
  hashContentCatalog,
  contentCatalogPath,
  composeGameIdentity,
  gameIdentityFromPing,
  frameworkFromPing,
  frameworkFromPings,
  collectBuildStamps,
  stampsForReport,
  stampPhaseSnapshot,
  stampTransitionFindings,
  finalizeReportStamps,
  CONTENT_CATALOG_REL,
} from '../lib/stamps.mjs';
import { runCase } from '../lib/run.mjs';
import { createFakeWorld, tempDir, runOpts, DEFAULT_SAMPLE_HASH, SWAP_SAMPLE_HASH } from './helpers.mjs';
import { buildStampsFromRun } from '../../catalog/lib/rigRuns.mjs';

const CORE_HASH = 'corehashaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const DEV_HASH = 'devhashbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const CLIENT_SAMPLE = 'clientsha256bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const SERVER_SAMPLE = DEFAULT_SAMPLE_HASH;
const CLIENT_CORE = 'clientcorecccccccccccccccccccccccccccccccccccccccccccccccccccc';
const SERVER_CORE = CORE_HASH;
const VERSION_DATA = {
  Major: 1,
  Minor: 1,
  Patch: 13,
  Revision: 0,
  VcsRevision: 99712,
  Build: 17,
  NetworkVersion: 25,
};
const EXPECTED_IDENTITY = 'vrising/1.1.13.0+r99712+b17/net25';
const OTHER_IDENTITY = 'vrising/1.1.13.0+r99712+b18/net25';

function writeFakeCatalog(root, bytes = Buffer.from('fake-content-catalog')) {
  const file = contentCatalogPath(root);
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, bytes);
  return { file, sha256: createHash('sha256').update(bytes).digest('hex') };
}

function pingWith(opts = {}) {
  const row = {
    reload: { generation: opts.generation ?? 4 },
  };
  if (opts.sample !== undefined) row.sampleHash = opts.sample;
  if (opts.core !== undefined) row.coreHash = opts.core;
  if (opts.dev !== undefined) row.devHash = opts.dev;
  if (opts.gameIdentity !== undefined) row.gameIdentity = opts.gameIdentity;
  else row.gameIdentity = EXPECTED_IDENTITY;
  if (opts.images !== undefined) row.images = opts.images;
  return row;
}

function frameworkString(opts = {}) {
  const generation = opts.generation ?? 4;
  const core = opts.core === undefined ? CORE_HASH : opts.core;
  const dev = opts.dev === undefined ? DEV_HASH : opts.dev;
  const sample = opts.sample === undefined ? DEFAULT_SAMPLE_HASH : opts.sample;
  const token = (v) => (v == null || v === '' ? 'null' : String(v));
  return `generation:${token(generation)}; core:${token(core)}; dev:${token(dev)}; sample:${token(sample)}`;
}

test('hashContentCatalog matches sha256 of archive_dependencies.bin and is null when missing', () => {
  const tmp = tempDir();
  try {
    const missing = hashContentCatalog(path.join(tmp.dir, 'absent'));
    assert.equal(missing.sha256, null);
    assert.match(missing.error, /unreadable/);
    assert.ok(missing.file.endsWith(CONTENT_CATALOG_REL) || missing.file.includes('archive_dependencies.bin'));

    const bytes = Buffer.from('catalog-bytes-for-stamp');
    const { sha256 } = writeFakeCatalog(tmp.dir, bytes);
    const got = hashContentCatalog(tmp.dir);
    assert.equal(got.sha256, sha256);
    assert.equal(got.error, null);
  } finally {
    tmp.cleanup();
  }
});

test('gameIdentityFromPing maps ping version fields to vrising/<version>+r<revision>+b<build>/net<n>', () => {
  assert.equal(composeGameIdentity({
    major: 1, minor: 1, patch: 13, revision: 0, vcsRevision: 99712, build: 17, networkVersion: 25,
  }), EXPECTED_IDENTITY);

  assert.equal(gameIdentityFromPing({ gameIdentity: EXPECTED_IDENTITY }).identity, EXPECTED_IDENTITY);
  assert.equal(gameIdentityFromPing({ versionData: VERSION_DATA }).identity, EXPECTED_IDENTITY);
  assert.equal(gameIdentityFromPing({
    versionData: { ...VERSION_DATA, runtime: 'net25' },
  }).identity, EXPECTED_IDENTITY);

  const empty = gameIdentityFromPing({
    side: 'server',
    progenitor: '0.1.0',
    reload: { generation: 4 },
  });
  assert.equal(empty.identity, null);
  assert.match(empty.error, /no game version\/build identity/);
});

test('frameworkFromPing names generation and Core/Dev/Sample hashes with null parts allowed', () => {
  const ping = {
    reload: { generation: 4 },
    sampleHash: DEFAULT_SAMPLE_HASH,
    coreHash: CORE_HASH,
    devHash: DEV_HASH,
  };
  assert.equal(
    frameworkFromPing(ping),
    `generation:4; core:${CORE_HASH}; dev:${DEV_HASH}; sample:${DEFAULT_SAMPLE_HASH}`,
  );
  assert.equal(
    frameworkFromPings({ reload: { generation: 4 }, sampleHash: DEFAULT_SAMPLE_HASH }),
    `generation:4; core:null; dev:null; sample:${DEFAULT_SAMPLE_HASH}`,
  );
  assert.equal(frameworkFromPing(null), null);
});

test('frameworkFromPing reads the Dev ping images[] stem names and gameIdentity as-is', () => {
  const coreSha = 'c'.repeat(64);
  const devSha = 'd'.repeat(64);
  const ping = {
    gameIdentity: EXPECTED_IDENTITY,
    images: [
      { name: 'Core', sha256: coreSha, generation: 4 },
      { name: 'Progenitor.Dev', sha256: devSha },
      { name: 'Progenitor.Sample', sha256: DEFAULT_SAMPLE_HASH },
    ],
  };
  assert.equal(gameIdentityFromPing(ping).identity, EXPECTED_IDENTITY);
  assert.equal(
    frameworkFromPing(ping),
    `generation:4; core:${coreSha}; dev:${devSha}; sample:${DEFAULT_SAMPLE_HASH}`,
  );
});

test('frameworkFromPing with images null does not guess hashes', () => {
  const ping = { gameIdentity: EXPECTED_IDENTITY, images: null };
  assert.equal(gameIdentityFromPing(ping).identity, EXPECTED_IDENTITY);
  assert.equal(
    frameworkFromPing(ping),
    'generation:null; core:null; dev:null; sample:null',
  );
});

test('frameworkFromPings does not merge a second ping into the first side', () => {
  const server = pingWith({ sample: SERVER_SAMPLE, core: SERVER_CORE, dev: DEV_HASH });
  const client = pingWith({ sample: CLIENT_SAMPLE, core: CLIENT_CORE, dev: DEV_HASH });
  assert.equal(frameworkFromPings(server, client), frameworkFromPing(server));
  assert.ok(!frameworkFromPings(server, client).includes(CLIENT_SAMPLE));
  assert.equal(frameworkFromPing(client), frameworkString({ sample: CLIENT_SAMPLE, core: CLIENT_CORE }));
});

test('collectBuildStamps records catalog hash, gameIdentity and per-side framework from a fake ping and install file', () => {
  const tmp = tempDir();
  try {
    const bytes = Buffer.from('install-catalog');
    const { sha256 } = writeFakeCatalog(tmp.dir, bytes);
    const ping = {
      gameIdentity: EXPECTED_IDENTITY,
      reload: { generation: 4 },
      sampleHash: DEFAULT_SAMPLE_HASH,
      coreHash: CORE_HASH,
      devHash: DEV_HASH,
      images: [
        { file: 'Progenitor.Sample.dll', sha256: DEFAULT_SAMPLE_HASH },
        { file: 'Progenitor.Core.dll', sha256: CORE_HASH },
        { file: 'Progenitor.Dev.dll', sha256: DEV_HASH },
      ],
    };
    const stamps = collectBuildStamps({ pingServer: ping, pingClient: ping, gameInstall: tmp.dir });
    assert.equal(stamps.contentCatalogSha256, sha256);
    assert.equal(stamps.gameIdentity, EXPECTED_IDENTITY);
    assert.equal(stamps.framework, null);
    assert.equal(
      stamps.frameworkSides.server,
      `generation:4; core:${CORE_HASH}; dev:${DEV_HASH}; sample:${DEFAULT_SAMPLE_HASH}`,
    );
    assert.equal(stamps.frameworkSides.client, stamps.frameworkSides.server);
    assert.deepEqual(stamps.stampErrors, []);

    const onReport = { session: { sampleHash: DEFAULT_SAMPLE_HASH, generation: 4 }, ...stampsForReport(stamps) };
    assert.equal(onReport.framework, null);
    assert.equal(onReport.frameworkSides.server, stamps.frameworkSides.server);
    const fromCatalog = buildStampsFromRun(onReport);
    assert.equal(fromCatalog.contentCatalogSha256, sha256);
    assert.equal(fromCatalog.gameIdentity, EXPECTED_IDENTITY);
  } finally {
    tmp.cleanup();
  }
});

test('client-only Sample change leaves the server framework side unchanged', () => {
  const tmp = tempDir();
  try {
    writeFakeCatalog(tmp.dir);
    const server = pingWith({ sample: SERVER_SAMPLE, core: SERVER_CORE, dev: DEV_HASH });
    const clientBefore = pingWith({ sample: CLIENT_SAMPLE, core: CLIENT_CORE, dev: DEV_HASH });
    const clientAfter = pingWith({ sample: SWAP_SAMPLE_HASH, core: CLIENT_CORE, dev: DEV_HASH });
    const before = collectBuildStamps({ pingServer: server, pingClient: clientBefore, gameInstall: tmp.dir });
    const after = collectBuildStamps({ pingServer: server, pingClient: clientAfter, gameInstall: tmp.dir });
    assert.equal(before.framework, null);
    assert.equal(after.framework, null);
    assert.equal(before.frameworkSides.server, after.frameworkSides.server);
    assert.notEqual(before.frameworkSides.client, after.frameworkSides.client);
    assert.ok(before.frameworkSides.client.includes(CLIENT_SAMPLE));
    assert.ok(after.frameworkSides.client.includes(SWAP_SAMPLE_HASH));
    assert.ok(!after.frameworkSides.server.includes(SWAP_SAMPLE_HASH));
  } finally {
    tmp.cleanup();
  }
});

test('server-only Sample change leaves the client framework side unchanged', () => {
  const tmp = tempDir();
  try {
    writeFakeCatalog(tmp.dir);
    const client = pingWith({ sample: CLIENT_SAMPLE, core: CLIENT_CORE, dev: DEV_HASH });
    const serverBefore = pingWith({ sample: SERVER_SAMPLE, core: SERVER_CORE, dev: DEV_HASH });
    const serverAfter = pingWith({ sample: SWAP_SAMPLE_HASH, core: SERVER_CORE, dev: DEV_HASH });
    const before = collectBuildStamps({ pingServer: serverBefore, pingClient: client, gameInstall: tmp.dir });
    const after = collectBuildStamps({ pingServer: serverAfter, pingClient: client, gameInstall: tmp.dir });
    assert.equal(before.frameworkSides.client, after.frameworkSides.client);
    assert.notEqual(before.frameworkSides.server, after.frameworkSides.server);
    assert.ok(after.frameworkSides.server.includes(SWAP_SAMPLE_HASH));
    assert.ok(!after.frameworkSides.client.includes(SWAP_SAMPLE_HASH));
  } finally {
    tmp.cleanup();
  }
});

test('changing both sides changes both frameworkSides strings', () => {
  const tmp = tempDir();
  try {
    writeFakeCatalog(tmp.dir);
    const before = collectBuildStamps({
      pingServer: pingWith({ sample: SERVER_SAMPLE, core: SERVER_CORE, dev: DEV_HASH }),
      pingClient: pingWith({ sample: CLIENT_SAMPLE, core: CLIENT_CORE, dev: DEV_HASH }),
      gameInstall: tmp.dir,
    });
    const after = collectBuildStamps({
      pingServer: pingWith({ sample: SWAP_SAMPLE_HASH, core: SERVER_CORE, dev: DEV_HASH }),
      pingClient: pingWith({ sample: 'clientchangeddddddddddddddddddddddddddddddddddddddddddddddd', core: CLIENT_CORE, dev: DEV_HASH }),
      gameInstall: tmp.dir,
    });
    assert.notEqual(before.frameworkSides.server, after.frameworkSides.server);
    assert.notEqual(before.frameworkSides.client, after.frameworkSides.client);
  } finally {
    tmp.cleanup();
  }
});

test('missing client ping is null and does not inherit the server stamp', () => {
  const tmp = tempDir();
  try {
    writeFakeCatalog(tmp.dir);
    const server = pingWith({ sample: SERVER_SAMPLE, core: SERVER_CORE, dev: DEV_HASH });
    const stamps = collectBuildStamps({ pingServer: server, pingClient: null, gameInstall: tmp.dir });
    assert.equal(stamps.framework, null);
    assert.equal(stamps.frameworkSides.server, frameworkString({ sample: SERVER_SAMPLE, core: SERVER_CORE }));
    assert.equal(stamps.frameworkSides.client, null);
    assert.ok(stamps.stampErrors.some((e) => e.side === 'client' && /client ping missing/.test(e.error)));
    assert.ok(!stamps.stampErrors.some((e) => e.side === 'server' && /ping missing/.test(e.error)));
  } finally {
    tmp.cleanup();
  }
});

test('a missing part on one side stays null and is not filled from the other side', () => {
  const tmp = tempDir();
  try {
    writeFakeCatalog(tmp.dir);
    const server = pingWith({ sample: SERVER_SAMPLE, core: SERVER_CORE, dev: DEV_HASH });
    const client = pingWith({ sample: CLIENT_SAMPLE, core: undefined, dev: DEV_HASH });
    delete client.coreHash;
    const stamps = collectBuildStamps({ pingServer: server, pingClient: client, gameInstall: tmp.dir });
    assert.match(stamps.frameworkSides.server, new RegExp(`core:${SERVER_CORE}`));
    assert.match(stamps.frameworkSides.client, /core:null/);
    assert.ok(!stamps.frameworkSides.client.includes(SERVER_CORE));
    assert.ok(stamps.stampErrors.some((e) => e.side === 'client' && e.part === 'core' && /client core missing/.test(e.error)));
    assert.ok(!stamps.stampErrors.some((e) => e.side === 'server' && e.part === 'core'));
  } finally {
    tmp.cleanup();
  }
});

test('disagreeing game identities record both sides and leave gameIdentity null', () => {
  const tmp = tempDir();
  try {
    writeFakeCatalog(tmp.dir);
    const stamps = collectBuildStamps({
      pingServer: pingWith({ sample: SERVER_SAMPLE, core: SERVER_CORE, dev: DEV_HASH, gameIdentity: EXPECTED_IDENTITY }),
      pingClient: pingWith({ sample: CLIENT_SAMPLE, core: CLIENT_CORE, dev: DEV_HASH, gameIdentity: OTHER_IDENTITY }),
      gameInstall: tmp.dir,
    });
    assert.equal(stamps.gameIdentity, null);
    assert.deepEqual(stamps.gameIdentitySides, { server: EXPECTED_IDENTITY, client: OTHER_IDENTITY });
    assert.ok(stamps.stampErrors.some((e) => e.stamp === 'gameIdentity' && /disagree/.test(e.error)));
  } finally {
    tmp.cleanup();
  }
});

test('stampPhases record a side transition as stampTransition plus a finding', () => {
  const tmp = tempDir();
  try {
    writeFakeCatalog(tmp.dir);
    const start = collectBuildStamps({
      pingServer: pingWith({ sample: SERVER_SAMPLE, core: SERVER_CORE, dev: DEV_HASH }),
      pingClient: pingWith({ sample: CLIENT_SAMPLE, core: CLIENT_CORE, dev: DEV_HASH }),
      gameInstall: tmp.dir,
    });
    const final = collectBuildStamps({
      pingServer: pingWith({ sample: SERVER_SAMPLE, core: SERVER_CORE, dev: DEV_HASH }),
      pingClient: pingWith({ sample: SWAP_SAMPLE_HASH, core: CLIENT_CORE, dev: DEV_HASH }),
      gameInstall: tmp.dir,
    });
    const { stamps, transitionFindings } = finalizeReportStamps(start, final, {
      startAt: 't0',
      finalAt: 't1',
    });
    assert.equal(stamps.stampTransition, true);
    assert.equal(stamps.framework, null);
    assert.equal(stamps.stampPhases.start.frameworkSides.client, start.frameworkSides.client);
    assert.equal(stamps.stampPhases.final.frameworkSides.client, final.frameworkSides.client);
    assert.equal(stamps.stampPhases.start.at, 't0');
    assert.equal(stamps.stampPhases.final.at, 't1');
    assert.equal(stamps.frameworkSides.server, start.frameworkSides.server);
    assert.deepEqual(transitionFindings, [
      `image changed during run: client ${start.frameworkSides.client} -> ${final.frameworkSides.client}`,
    ]);
    assert.deepEqual(stampTransitionFindings(stampPhaseSnapshot(start), stampPhaseSnapshot(start)), []);
  } finally {
    tmp.cleanup();
  }
});

test('a run report writes per-side stamps at start and final readback and keeps the session pin', async () => {
  const tmp = tempDir();
  const { sha256 } = writeFakeCatalog(tmp.dir, Buffer.from('run-catalog'));
  const world = createFakeWorld({
    gameIdentity: EXPECTED_IDENTITY,
    coreHash: CORE_HASH,
    devHash: DEV_HASH,
    sampleHash: DEFAULT_SAMPLE_HASH,
    generation: 4,
  });
  try {
    const { report } = await runCase({
      name: 'stamp-run',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
        { kind: 'destroyTracked', name: 'rat' },
      ],
    }, runOpts(tmp, world, { gameInstall: tmp.dir }));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.contentCatalogSha256, sha256);
    assert.equal(report.gameIdentity, EXPECTED_IDENTITY);
    assert.equal(report.framework, null);
    const expected = `generation:4; core:${CORE_HASH}; dev:${DEV_HASH}; sample:${DEFAULT_SAMPLE_HASH}`;
    assert.equal(report.frameworkSides.server, expected);
    assert.equal(report.frameworkSides.client, expected);
    assert.deepEqual(report.stampErrors, []);
    assert.equal(report.stampPhases.start.frameworkSides.server, expected);
    assert.equal(report.stampPhases.final.frameworkSides.server, expected);
    assert.equal(report.stampPhases.start.contentCatalogSha256, sha256);
    assert.equal(report.stampPhases.final.contentCatalogSha256, sha256);
    assert.ok(report.stampPhases.start.at);
    assert.ok(report.stampPhases.final.at);
    assert.equal(report.stampTransition, undefined);
    assert.equal(report.session.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.equal(report.session.generation, 4);
    assert.ok(report.session.serverStartedUtc);

    const fromCatalog = buildStampsFromRun(report);
    assert.equal(fromCatalog.contentCatalogSha256, sha256);
    assert.equal(fromCatalog.gameIdentity, EXPECTED_IDENTITY);
  } finally {
    tmp.cleanup();
  }
});

test('a client Sample change during the run sets stampTransition and a finding', async () => {
  const tmp = tempDir();
  writeFakeCatalog(tmp.dir, Buffer.from('run-catalog'));
  const world = createFakeWorld({
    gameIdentity: EXPECTED_IDENTITY,
    coreHash: CORE_HASH,
    devHash: DEV_HASH,
    sampleHash: DEFAULT_SAMPLE_HASH,
    generation: 4,
  });
  const orig = world.request.bind(world);
  let pingCount = 0;
  world.request = async (side, op, args) => {
    if (op === 'ping') pingCount += 1;
    const result = await orig(side, op, args);
    if (op === 'ping' && pingCount > 2 && side === 'client' && result?.result) {
      result.result.sampleHash = SWAP_SAMPLE_HASH;
      if (Array.isArray(result.result.images)) {
        result.result.images = result.result.images.map((row) => ({ ...row, sha256: SWAP_SAMPLE_HASH }));
      }
    }
    return result;
  };
  try {
    const { report } = await runCase({
      name: 'stamp-transition',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
        { kind: 'destroyTracked', name: 'rat' },
      ],
    }, runOpts(tmp, world, { gameInstall: tmp.dir }));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.stampTransition, true);
    assert.equal(report.framework, null);
    assert.ok(report.stampPhases.start.frameworkSides.client.includes(DEFAULT_SAMPLE_HASH));
    assert.ok(report.stampPhases.final.frameworkSides.client.includes(SWAP_SAMPLE_HASH));
    assert.equal(report.stampPhases.start.frameworkSides.server, report.stampPhases.final.frameworkSides.server);
    assert.ok(report.findings.some((f) => /^image changed during run: client /.test(f)));
  } finally {
    tmp.cleanup();
  }
});

test('unreadable catalog and a ping without version fields record null plus stampErrors, never a guess', async () => {
  const tmp = tempDir();
  const world = createFakeWorld();
  try {
    const { report } = await runCase({
      name: 'stamp-missing',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
        { kind: 'destroyTracked', name: 'rat' },
      ],
    }, runOpts(tmp, world));
    assert.equal(report.status, 'complete', report.reason);
    assert.equal(report.contentCatalogSha256, null);
    assert.equal(report.gameIdentity, null);
    assert.equal(report.framework, null);
    assert.ok(report.stampErrors.some((e) => e.stamp === 'contentCatalogSha256'));
    assert.ok(report.stampErrors.some((e) => e.stamp === 'gameIdentity'));
    assert.ok(report.stampErrors.some((e) => e.side === 'server' && e.part === 'core'));
    assert.ok(report.stampErrors.some((e) => e.side === 'client' && e.part === 'core'));
    assert.equal(report.session.sampleHash, DEFAULT_SAMPLE_HASH);
    assert.match(report.frameworkSides.server, /^generation:1; core:null; dev:null; sample:/);
    assert.match(report.frameworkSides.client, /^generation:1; core:null; dev:null; sample:/);
  } finally {
    tmp.cleanup();
  }
});
