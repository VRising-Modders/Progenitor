#!/usr/bin/env node
// Progenitor's signed release list (docs/releasing.md, docs/auto-update.md "Release list").
//
//   node tools/release-feed/release-feed.mjs keygen --out <file> [--slot current|next] [--no-source]
//       A new ECDSA P-256 release key. The private key (PKCS#8 PEM) is written to <file>, which must be outside the
//       repository or git-ignored; the public key is written into src/Progenitor.Core/Install/SelfFeedKeys.cs (slot
//       current by default; next to roll the key) and printed. Add the private key as the PROGENITOR_RELEASE_KEY secret.
//   node tools/release-feed/release-feed.mjs add --package <Progenitor-<version>.zip> --release <release page URL>
//                                                [--version <v>] [--url <asset URL>] [--published <ISO time>]
//                                                [--replace] [--no-sign] [--feed <releases.json>] [--key <file>]...
//       Reads the package as Progenitor reads it (sha256, size, the Core and Bootstrap sha256 and their protocols), adds
//       the release to the list, newest first, and signs the list. --release is the GitHub release page
//       (https://github.com/VRising-Modders/Progenitor/releases/tag/v<version>): the asset URL is
//       .../releases/download/v<version>/<zip name> unless --url names it, and the notes link is the page.
//   node tools/release-feed/release-feed.mjs sign   [--feed <releases.json>] [--key <file>]...
//   node tools/release-feed/release-feed.mjs verify [--feed <releases.json>]   (with the keys SelfFeedKeys.cs builds in)
//
// Keys come from each --key <file>, else PROGENITOR_RELEASE_KEY (PEM text or base64 PKCS#8) and, while a key is rolled,
// PROGENITOR_RELEASE_KEY_NEXT; every one given signs. The default list is docs/updates/releases.json and its signature
// docs/updates/releases.json.sig. No dependency beyond Node itself.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import {
  ASSET_PREFIX, CHANNEL, RELEASE_PAGE_PREFIX, addRelease, emptyFeed, feedBytes, generateKey, privateKey, readPackage,
  readSourceKeys, sign, signatureBytes, verify, writeSourceKey,
} from './lib.mjs';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const KEYS_SOURCE = path.join(REPO, 'src', 'Progenitor.Core', 'Install', 'SelfFeedKeys.cs');
const DEFAULT_FEED = path.join(REPO, 'docs', 'updates', 'releases.json');
const BOOLEAN = new Set(['replace', 'no-sign', 'no-source', 'dev']);

const [cmd, ...rest] = process.argv.slice(2);
const flags = parseArgs(rest);
const commands = { keygen, add, sign: signCmd, verify: verifyCmd };
if (!commands[cmd]) {
  console.error('usage: release-feed.mjs keygen|add|sign|verify ...  (see docs/releasing.md)');
  process.exit(2);
}
try {
  process.exitCode = commands[cmd]() ?? 0;
} catch (e) {
  console.error('release-feed: ' + (e && e.message ? e.message : e));
  process.exitCode = 1;
}

function parseArgs(args) {
  const out = { key: [] };
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (!a.startsWith('--')) throw new Error('unexpected argument ' + a);
    const name = a.slice(2);
    if (BOOLEAN.has(name)) { out[name] = true; continue; }
    const value = args[++i];
    if (value === undefined) throw new Error(a + ' needs a value');
    if (name === 'key') out.key.push(value); else out[name] = value;
  }
  return out;
}

function feedPath() { return path.resolve(flags.feed || DEFAULT_FEED); }

function readFeed(file) {
  if (!fs.existsSync(file)) return emptyFeed();
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function signingKeys() {
  const keys = flags.key.map((f) => privateKey(fs.readFileSync(f, 'utf8')));
  if (keys.length === 0) {
    for (const name of ['PROGENITOR_RELEASE_KEY', 'PROGENITOR_RELEASE_KEY_NEXT']) {
      if (process.env[name] && process.env[name].trim()) keys.push(privateKey(process.env[name]));
    }
  }
  if (keys.length === 0) throw new Error('no release key: pass --key <file> or set PROGENITOR_RELEASE_KEY (or pass --no-sign)');
  return keys;
}

function writeSigned(file, feed, sign_) {
  const bytes = feedBytes(feed);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, bytes);
  console.log('wrote ' + file + ' (' + feed.releases.length + ' release(s))');
  if (!sign_) {
    console.log('not signed (--no-sign): run sign before publishing, or the list is not used');
    return;
  }
  const sig = sign(bytes, signingKeys());
  fs.writeFileSync(file + '.sig', signatureBytes(sig));
  console.log('wrote ' + file + '.sig (key ' + sig.signatures.map((s) => s.key).join(', ') + ')');
}

/** Refuse to write a private key into the repository unless git ignores that path. */
function assertOutsideOrIgnored(file) {
  const rel = path.relative(REPO, file);
  if (rel.startsWith('..') || path.isAbsolute(rel)) return;
  const r = spawnSync('git', ['-C', REPO, 'check-ignore', '-q', rel], { encoding: 'utf8' });
  if (r.status !== 0) throw new Error(file + ' is inside the repository and not ignored by git; write the key outside the repository');
}

function keygen() {
  if (!flags.out) throw new Error('keygen needs --out <file> (outside the repository)');
  const out = path.resolve(flags.out);
  if (fs.existsSync(out)) throw new Error(out + ' exists; never overwrite a release key');
  assertOutsideOrIgnored(out);
  const k = generateKey();
  fs.mkdirSync(path.dirname(out), { recursive: true });
  fs.writeFileSync(out, k.pem, { mode: 0o600 });
  console.log('wrote the private key to ' + out + ' (keep it out of git; add it as the PROGENITOR_RELEASE_KEY secret)');
  console.log('public key (SPKI, base64): ' + k.spkiBase64);
  console.log('fingerprint: ' + k.fingerprint);
  if (!flags['no-source']) {
    const slot = flags.slot || 'current';
    fs.writeFileSync(KEYS_SOURCE, writeSourceKey(fs.readFileSync(KEYS_SOURCE, 'utf8'), slot, k.spkiBase64));
    console.log('wrote the public key into ' + path.relative(REPO, KEYS_SOURCE) + ' (' + slot + '); commit it before building the next release');
  }
  return 0;
}

function add() {
  if (!flags.package) throw new Error('add needs --package <zip>');
  const zipPath = path.resolve(flags.package);
  const zipName = path.basename(zipPath);
  const pkg = readPackage(fs.readFileSync(zipPath));
  const fromName = /^Progenitor-(\d+\.\d+\.\d+(?:\.\d+)?)\.zip$/.exec(zipName);
  const version = flags.version || (pkg.record && pkg.record.version) || (fromName && fromName[1]);
  if (!version) throw new Error('cannot tell the version from ' + zipName + '; pass --version');
  if (pkg.record && pkg.record.version && pkg.record.version !== version) throw new Error('the package records ' + pkg.record.version + ', not ' + version);
  if (pkg.record && (pkg.record.coreSha256 !== pkg.coreSha256 || pkg.record.bootstrapSha256 !== pkg.bootstrapSha256))
    throw new Error('the package\'s self-install record does not name its own Core and Bootstrap');
  if (/^\d+\.\d+\.\d+\.\d+$/.test(version)) throw new Error(version + ' is a development build (a sub-release); never list it');
  let notes = flags.notes || flags.release || '';
  let url = flags.url;
  if (!url) {
    if (!flags.release) throw new Error('add needs --release <release page URL> or --url <asset URL>');
    if (!flags.release.startsWith(RELEASE_PAGE_PREFIX)) throw new Error('--release must be a page under ' + RELEASE_PAGE_PREFIX);
    const tag = flags.release.slice(RELEASE_PAGE_PREFIX.length).replace(/\/+$/, '');
    url = ASSET_PREFIX + tag + '/' + zipName;
  }
  if (!flags.dev && !url.startsWith(ASSET_PREFIX)) throw new Error('the asset URL must start with ' + ASSET_PREFIX);
  const entry = {
    version,
    published: flags.published || new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
    channel: CHANNEL,
    url,
    size: pkg.size,
    sha256: pkg.sha256,
    coreSha256: pkg.coreSha256,
    bootstrapSha256: pkg.bootstrapSha256,
    bootstrapProtocol: pkg.bootstrapProtocol,
    coreProtocol: pkg.coreProtocol,
    handshakeProtocol: pkg.handshakeProtocol,
    notes,
  };
  const file = feedPath();
  const feed = addRelease(readFeed(file), entry, { replace: !!flags.replace });
  console.log('Progenitor ' + version + ': ' + pkg.size + ' bytes, sha256 ' + pkg.sha256 + ', protocols bootstrap ' + pkg.bootstrapProtocol
    + ' / handshake ' + pkg.handshakeProtocol + (pkg.record ? ', carries its self-install record' : ', no self-install record'));
  writeSigned(file, feed, !flags['no-sign']);
  return 0;
}

function signCmd() {
  const file = feedPath();
  if (!fs.existsSync(file)) throw new Error(file + ' does not exist');
  writeSigned(file, readFeed(file), true);
  return 0;
}

function verifyCmd() {
  const file = feedPath();
  const keys = readSourceKeys(fs.readFileSync(KEYS_SOURCE, 'utf8'));
  const pubs = [keys.current, keys.next].filter(Boolean);
  if (pubs.length === 0) { console.error('SelfFeedKeys.cs builds in no key; run keygen first'); return 1; }
  if (!fs.existsSync(file + '.sig')) { console.error(file + '.sig does not exist'); return 1; }
  const ok = verify(fs.readFileSync(file), JSON.parse(fs.readFileSync(file + '.sig', 'utf8')), pubs);
  console.log(ok ? 'verified: ' + file + ' is signed by a built-in key' : 'NOT verified: ' + file);
  return ok ? 0 : 1;
}
