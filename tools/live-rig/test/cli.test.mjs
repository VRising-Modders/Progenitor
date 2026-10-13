import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { exitCodeForStatus, runCase } from '../lib/run.mjs';
import { contentCatalogPath } from '../lib/stamps.mjs';
import { REPO_ROOT, TOOL_DIR, createFakeWorld, tempDir, runOpts } from './helpers.mjs';

const CLI = path.join(TOOL_DIR, 'rig.mjs');

function run(...args) {
  return spawnSync(process.execPath, [CLI, ...args], { encoding: 'utf8', cwd: REPO_ROOT });
}

test('list prints the shipped cases', () => {
  const r = run('list');
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /cleanup-ok/);
  assert.match(r.stdout, /cleanup-timeout/);
  assert.match(r.stdout, /swap/);
  assert.match(r.stdout, /removal-reinstall/);
});

test('missing command and unknown flags are usage errors', () => {
  const none = run();
  assert.equal(none.status, 2);
  const bad = run('run', 'cleanup-ok', '--nope');
  assert.equal(bad.status, 2);
});

test('--site must be main-menu, top-tab, or shortcut', () => {
  const bad = run('run', 'cleanup-ok', '--site', 'pause-menu');
  assert.equal(bad.status, 2);
  assert.match(bad.stderr, /--site must be main-menu, top-tab, or shortcut/);
});

test('stamps prints contentCatalogSha256 of a fake install and null when the file is missing', () => {
  const tmp = tempDir();
  try {
    const missing = run('stamps', '--game', path.join(tmp.dir, 'absent'));
    assert.equal(missing.status, 0, missing.stderr);
    assert.equal(missing.stdout.trim(), 'null');

    const bytes = Buffer.from('cli-catalog');
    const file = contentCatalogPath(tmp.dir);
    mkdirSync(path.dirname(file), { recursive: true });
    writeFileSync(file, bytes);
    const sha = createHash('sha256').update(bytes).digest('hex');
    const got = run('stamps', '--game', tmp.dir);
    assert.equal(got.status, 0, got.stderr);
    assert.equal(got.stdout.trim(), sha);
  } finally {
    tmp.cleanup();
  }
});

test('cleanup without a runDir is a usage error and the help names the command', () => {
  const none = run();
  assert.match(none.stdout, /cleanup <runDir>/);
  const missing = run('cleanup');
  assert.equal(missing.status, 2);
  assert.match(missing.stdout, /cleanup <runDir>/);
  const bad = run('cleanup', 'some-dir', '--nope');
  assert.equal(bad.status, 2);
});

test('cleanup --entity without an id is a usage error and the help names the flag', () => {
  const none = run();
  assert.match(none.stdout, /cleanup --entity/);
  const missing = run('cleanup', '--entity');
  assert.equal(missing.status, 2);
  assert.match(missing.stdout, /cleanup --entity/);
  const both = run('cleanup', 'some-dir', '--entity', '1:1');
  assert.equal(both.status, 2);
});

test('CLI exit codes: complete is 0, incomplete and aborted are 1', () => {
  assert.equal(exitCodeForStatus('complete'), 0);
  assert.equal(exitCodeForStatus('incomplete'), 1);
  assert.equal(exitCodeForStatus('aborted'), 1);
});

test('a leftover entity is never complete and maps to CLI exit 1', async () => {
  // rig.mjs `run` always constructs a real bridge (sockets + token files).
  // spawnSync cannot inject the fake world that cli.test.mjs does not have a
  // hook for, so this test drives runCase the same way run.test.mjs does and
  // applies exitCodeForStatus — the function rig.mjs uses for the process exit.
  const tmp = tempDir();
  const world = createFakeWorld();
  const lines = [];
  try {
    const { report } = await runCase({
      name: 'cleanup-timeout',
      steps: [
        { kind: 'spawn', name: 'rat', prefab: 'CHAR_Critter_Rat', offset: { x: 3, y: 0, z: 0 }, hostile: false, track: true },
        { kind: 'skipCleanupFor', name: 'rat' },
      ],
    }, runOpts(tmp, world, { lines }));
    assert.equal(report.status, 'incomplete');
    assert.equal(report.cleanup.ok, false);
    assert.equal(report.cleanup.remaining.length, 1);
    const entityId = report.cleanup.remaining[0].entity;
    assert.ok(entityId);
    assert.ok(lines.some((l) => l.includes(entityId)), `stdout missing remaining entity ${entityId}: ${lines.join('\n')}`);
    assert.ok(!lines.some((l) => l.startsWith('status: complete')));
    assert.ok(lines.some((l) => l.startsWith('status: incomplete')));
    assert.equal(exitCodeForStatus(report.status), 1);
  } finally {
    tmp.cleanup();
  }
});
