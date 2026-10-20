// node --test tools/release-feed/test/release-feed.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  ASSET_PREFIX, addRelease, compareVersions, emptyFeed, feedBytes, generateKey, readPackage, readProtocol, readSourceKeys,
  readZip, sign, touchFeed, verify, writeSourceKey, writeZip,
} from '../lib.mjs';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

test('a zip written here reads back, nested archives included', () => {
  const inner = writeZip([{ name: 'progenitor-core/Progenitor.dll', data: Buffer.from('core') }]);
  const outer = writeZip([{ name: 'BepInEx/patchers/progenitor-files.zip', data: inner }, { name: 'a.txt', data: Buffer.from('x') }]);
  const entries = readZip(outer);
  assert.deepEqual(entries.map((e) => e.name), ['BepInEx/patchers/progenitor-files.zip', 'a.txt']);
  assert.equal(readZip(entries[0].data)[0].data.toString(), 'core');
});

test('the list stays newest first and refuses a duplicate unless replaced', () => {
  let feed = emptyFeed();
  feed = addRelease(feed, { version: '2026.10.162' });
  feed = addRelease(feed, { version: '2026.11.3' });
  feed = addRelease(feed, { version: '2026.10.200' });
  assert.deepEqual(feed.releases.map((r) => r.version), ['2026.11.3', '2026.10.200', '2026.10.162']);
  assert.throws(() => addRelease(feed, { version: '2026.11.3' }), /already has/);
  assert.equal(addRelease(feed, { version: '2026.11.3', url: 'x' }, { replace: true }).releases[0].url, 'x');
  assert.ok(compareVersions('2026.10.162', '2026.10.20') > 0);
});

test('each signing moves updated forward, even within the same second', () => {
  const now = new Date('2026-11-02T10:00:00.500Z');
  const a = touchFeed(emptyFeed(), now);
  assert.equal(a.updated, '2026-11-02T10:00:00Z');
  const b = touchFeed(a, now);
  assert.equal(b.updated, '2026-11-02T10:00:01Z');
  assert.equal(touchFeed(b, new Date('2026-11-03T00:00:00Z')).updated, '2026-11-03T00:00:00Z');
  assert.deepEqual(Object.keys(b), Object.keys(emptyFeed()));
});

test('signatures verify with the signing key, rolled keys and nothing else', () => {
  const a = generateKey(), b = generateKey();
  const bytes = feedBytes(addRelease(emptyFeed(), { version: '2026.11.3', url: ASSET_PREFIX + 'v2026.11.3/Progenitor-2026.11.3.zip' }));
  const sig = sign(bytes, [a.pem]);
  assert.equal(sig.signatures[0].key, a.fingerprint);
  assert.equal(sig.signatures[0].signature.length, 128);
  assert.ok(verify(bytes, sig, [a.spkiBase64]));
  assert.ok(!verify(bytes, sig, [b.spkiBase64]));
  const both = sign(bytes, [a.pem, b.pem]);
  assert.ok(verify(bytes, both, [b.spkiBase64]));
  const tampered = Buffer.from(bytes.toString('utf8').replace('2026.11.3', '2026.11.4'));
  assert.ok(!verify(tampered, sig, [a.spkiBase64]));
});

test('the key slots in SelfFeedKeys.cs read and write', () => {
  const src = fs.readFileSync(path.join(REPO, 'src', 'Progenitor.Core', 'Install', 'SelfFeedKeys.cs'), 'utf8');
  const keys = readSourceKeys(src);
  assert.equal(typeof keys.current, 'string');
  const k = generateKey();
  const next = writeSourceKey(src, 'next', k.spkiBase64);
  assert.equal(readSourceKeys(next).next, k.spkiBase64);
  assert.equal(readSourceKeys(next).current, keys.current);
  assert.throws(() => writeSourceKey(src, 'other', k.spkiBase64));
});

// The protocol reader on real images, when this checkout has built them (dotnet build).
const built = (p) => ['Debug', 'Release'].map((c) => path.join(REPO, 'src', p, 'bin', c, 'net6.0')).find((d) => fs.existsSync(d));
test('the protocols are read from built images', { skip: !built('Progenitor.Core') || !built('Progenitor.Bootstrap') }, () => {
  const core = fs.readFileSync(path.join(built('Progenitor.Core'), 'Progenitor.dll'));
  const boot = fs.readFileSync(path.join(built('Progenitor.Bootstrap'), 'Progenitor.Bootstrap.dll'));
  const cp = readProtocol(core, 'Progenitor.BootstrapLink');
  assert.ok(cp > 0);
  assert.equal(readProtocol(boot, 'Progenitor.Bootstrap.CoreLayout'), cp);
  assert.ok(readProtocol(core, 'Progenitor.Networking.Handshake') > 0);
  assert.equal(readProtocol(core, 'No.Such.Type'), 0);
  const zip = writeZip([
    { name: 'BepInEx/plugins/Progenitor.Bootstrap.dll', data: boot },
    { name: 'BepInEx/progenitor-core/Progenitor.dll', data: core },
  ]);
  const pkg = readPackage(zip);
  assert.equal(pkg.coreProtocol, cp);
  assert.equal(pkg.size, zip.length);
  assert.equal(pkg.record, null);
});
