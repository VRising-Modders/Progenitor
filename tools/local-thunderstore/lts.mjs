#!/usr/bin/env node
// LOCALTS1 (dev only): a local stand-in for Thunderstore that new versions can be published to.
//
//   node tools/local-thunderstore/lts.mjs serve   --data <dir> [--port 27450]
//   node tools/local-thunderstore/lts.mjs publish --data <dir> --zip <Owner-Name-1.2.3.zip> [--as <Owner-Name-1.3.0>]
//                                                 [--changelog <text>] [--deprecated]
//       --as re-packs the zip under another package identity: manifest.json name/version_number are rewritten. The plugin
//       image and progenitor.json are unchanged (admission requires progenitor.json to match the image's [BepInPlugin]
//       version), so one tools/make-dummy-package.ps1 build can be published as several package versions; --changelog
//       writes CHANGELOG.md into the package.
//   node tools/local-thunderstore/lts.mjs unpublish --data <dir> --package <Owner-Name> [--version <v>]
//   node tools/local-thunderstore/lts.mjs list    --data <dir>
//   node tools/local-thunderstore/lts.mjs touch   --data <dir>        (bump Last-Modified only)
//
// The server listens on 127.0.0.1 only and serves, at the paths Progenitor fetches:
//   GET /c/v-rising/api/v1/package/                       the v1 index (Last-Modified, If-Modified-Since -> 304, gzip)
//   GET /package/download/<owner>/<name>/<version>/        the package zip
//   GET /icons/<owner>-<name>-<version>.png                the package icon
//   GET /api/experimental/package/<o>/<n>/<v>/readme/      {"markdown": README.md}
//   GET /api/experimental/package/<o>/<n>/<v>/changelog/   {"markdown": CHANGELOG.md}
//   GET /status                                           what is published, and the index's Last-Modified
// State lives in <data>/state.json and <data>/packages/; publish works while the server runs (each request re-reads it).
// No dependency beyond Node itself.

import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import zlib from 'node:zlib';
import crypto from 'node:crypto';

const INDEX_PATH = '/c/v-rising/api/v1/package/';

function args(argv) {
  const out = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) out[key] = true;
      else { out[key] = next; i++; }
    } else out._.push(a);
  }
  return out;
}

function fail(message) {
  console.error('lts: ' + message);
  process.exit(1);
}

function statePath(data) { return path.join(data, 'state.json'); }

function readState(data) {
  try {
    const s = JSON.parse(fs.readFileSync(statePath(data), 'utf8'));
    if (s && Array.isArray(s.packages)) return s;
  } catch { /* new */ }
  return { schema: 1, lastModified: new Date().toUTCString(), packages: [] };
}

function writeState(data, state) {
  fs.mkdirSync(data, { recursive: true });
  // HTTP dates have one-second resolution: a publish within the same second as the last one still moves the date.
  const prev = Date.parse(state.lastModified || 0) || 0;
  const now = Math.max(Date.now(), prev + 1000);
  state.lastModified = new Date(Math.floor(now / 1000) * 1000).toUTCString();
  const tmp = statePath(data) + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(state, null, 2));
  fs.renameSync(tmp, statePath(data));
}

// ---- a minimal zip reader (central directory, stored and deflate entries) ----
function readZipEntries(buf) {
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 65557); i--) {
    if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error('not a zip (no end of central directory)');
  const count = buf.readUInt16LE(eocd + 10);
  let off = buf.readUInt32LE(eocd + 16);
  const entries = new Map();
  for (let n = 0; n < count; n++) {
    if (buf.readUInt32LE(off) !== 0x02014b50) throw new Error('bad central directory');
    const method = buf.readUInt16LE(off + 10);
    const compSize = buf.readUInt32LE(off + 20);
    const nameLen = buf.readUInt16LE(off + 28);
    const extraLen = buf.readUInt16LE(off + 30);
    const commentLen = buf.readUInt16LE(off + 32);
    const local = buf.readUInt32LE(off + 42);
    const name = buf.toString('utf8', off + 46, off + 46 + nameLen);
    entries.set(name.replace(/\\/g, '/'), { method, compSize, local });
    off += 46 + nameLen + extraLen + commentLen;
  }
  return {
    names: [...entries.keys()],
    read(name) {
      const e = entries.get(name);
      if (!e) return null;
      const lNameLen = buf.readUInt16LE(e.local + 26);
      const lExtraLen = buf.readUInt16LE(e.local + 28);
      const start = e.local + 30 + lNameLen + lExtraLen;
      const data = buf.subarray(start, start + e.compSize);
      if (e.method === 0) return Buffer.from(data);
      if (e.method === 8) return zlib.inflateRawSync(data);
      throw new Error('unsupported zip method ' + e.method + ' for ' + name);
    },
  };
}

function writeZip(files) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const [name, data] of files) {
    const nameBuf = Buffer.from(name, "utf8");
    const comp = zlib.deflateRawSync(data);
    const crc = zlib.crc32(data) >>> 0;
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0x0800, 6); local.writeUInt16LE(8, 8);
    local.writeUInt32LE(crc, 14); local.writeUInt32LE(comp.length, 18); local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(8, 10); central.writeUInt32LE(crc, 16); central.writeUInt32LE(comp.length, 20); central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBuf.length, 28); central.writeUInt32LE(offset, 42);
    locals.push(local, nameBuf, comp);
    centrals.push(central, nameBuf);
    offset += 30 + nameBuf.length + comp.length;
  }
  const cd = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(files.length, 8); end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}

function repack(bytes, name, version, changelog) {
  const zip = readZipEntries(bytes);
  const files = [];
  let sawChangelog = false;
  for (const entry of zip.names) {
    if (entry.endsWith("/")) continue;
    let data = zip.read(entry);
    const lower = entry.toLowerCase();
    if (lower === "manifest.json") {
      const m = JSON.parse(data.toString("utf8").replace(/^\uFEFF/, ""));
      m.name = name;
      m.version_number = version;
      data = Buffer.from(JSON.stringify(m, null, 2));
    } else if (lower === "changelog.md" && changelog) {
      data = Buffer.from(changelog);
      sawChangelog = true;
    }
    files.push([entry, data]);
  }
  if (changelog && !sawChangelog) files.push(["CHANGELOG.md", Buffer.from(changelog)]);
  return writeZip(files);
}

function rootEntry(zip, file) {
  const hit = zip.names.find(n => n.toLowerCase() === file.toLowerCase());
  return hit ? zip.read(hit) : null;
}

function publish(data, zipPath, opts) {
  if (!zipPath || !fs.existsSync(zipPath)) fail('--zip <file> is required and must exist');
  const base = typeof opts.as === "string" ? opts.as : path.basename(zipPath, '.zip');
  const m = /^([^-]+)-(.+)-(\d+(?:\.\d+)*)$/.exec(base);
  if (!m) fail('the zip name must be <Owner>-<Name>-<version>.zip (what tools/make-dummy-package.ps1 writes): ' + base);
  const [, owner, name, version] = m;
  let bytes = fs.readFileSync(zipPath);
  if (typeof opts.as === "string" || typeof opts.changelog === "string")
    bytes = repack(bytes, name, version, typeof opts.changelog === "string" ? "# " + version + "\n\n" + opts.changelog + "\n" : null);
  const zip = readZipEntries(bytes);
  let manifest = {};
  const manifestBytes = rootEntry(zip, 'manifest.json');
  if (manifestBytes) {
    try { manifest = JSON.parse(manifestBytes.toString('utf8').replace(/^\uFEFF/, '')); } catch { manifest = {}; }
  }
  if (manifest.version_number && manifest.version_number !== version)
    fail('manifest.json says version ' + manifest.version_number + ' but the zip name says ' + version);

  const dir = path.join(data, 'packages', owner, name, version);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'package.zip'), bytes);
  for (const doc of ['README.md', 'CHANGELOG.md', 'icon.png']) {
    const b = rootEntry(zip, doc);
    if (b) fs.writeFileSync(path.join(dir, doc), b);
  }

  const state = readState(data);
  const full = owner + '-' + name;
  let pkg = state.packages.find(p => p.full_name === full);
  const now = new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
  if (!pkg) {
    pkg = {
      name, full_name: full, owner, package_url: '', date_created: now, date_updated: now,
      is_deprecated: false, is_pinned: false, has_nsfw_content: false,
      categories: ['Client', 'Server', 'Oakveil Update', 'Mods'], versions: [],
    };
    state.packages.push(pkg);
  }
  pkg.versions = pkg.versions.filter(v => v.version_number !== version);
  pkg.versions.unshift({
    name, full_name: full + '-' + version,
    description: manifest.description || 'Local test package.',
    version_number: version,
    dependencies: Array.isArray(manifest.dependencies) ? manifest.dependencies : [],
    downloads: 0, date_created: now, website_url: manifest.website_url || '',
    is_active: true, uuid4: crypto.randomUUID(), file_size: bytes.length,
  });
  pkg.versions.sort((a, b) => compareVersions(b.version_number, a.version_number));
  pkg.date_updated = now;
  if (opts.deprecated) pkg.is_deprecated = true;
  writeState(data, state);
  console.log('published ' + full + ' ' + version + ' (' + bytes.length + ' bytes); index Last-Modified ' + state.lastModified);
}

function unpublish(data, full, version) {
  if (!full) fail('--package <Owner-Name> is required');
  const state = readState(data);
  const pkg = state.packages.find(p => p.full_name === full);
  if (!pkg) fail(full + ' is not published');
  if (version) pkg.versions = pkg.versions.filter(v => v.version_number !== version);
  if (!version || pkg.versions.length === 0) state.packages = state.packages.filter(p => p !== pkg);
  const [owner, ...rest] = full.split('-');
  const target = path.join(data, 'packages', owner, rest.join('-'), version || '');
  fs.rmSync(target, { recursive: true, force: true });
  writeState(data, state);
  console.log('unpublished ' + full + (version ? ' ' + version : '') + '; index Last-Modified ' + state.lastModified);
}

function compareVersions(a, b) {
  const pa = String(a).split('.').map(n => parseInt(n, 10) || 0);
  const pb = String(b).split('.').map(n => parseInt(n, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d !== 0) return d;
  }
  return 0;
}

// ---- the server ----
function indexBody(state, origin) {
  return state.packages.map(p => ({
    ...p,
    versions: p.versions.map(v => ({
      ...v,
      icon: origin + '/icons/' + v.full_name + '.png',
      download_url: origin + '/package/download/' + p.owner + '/' + p.name + '/' + v.version_number + '/',
    })),
  }));
}

function serve(data, port) {
  fs.mkdirSync(data, { recursive: true });
  const server = http.createServer((req, res) => {
    const origin = 'http://' + (req.headers.host || ('127.0.0.1:' + port));
    const url = new URL(req.url, origin);
    // Each segment is decoded on its own and refused when it could leave the data folder (review: ..%5C on Windows).
    const rawSegments = url.pathname.split("/");
    let segments;
    try { segments = rawSegments.map(x => decodeURIComponent(x)); } catch { segments = null; }
    if (!segments || segments.some(x => x.includes("\\") || x.includes("/") || x === ".." || x === "." || x.includes("\u0000"))) {
      res.writeHead(400); res.end(); console.log(new Date().toISOString() + " " + req.method + " " + url.pathname + " -> 400 bad path");
      return;
    }
    const p = segments.join("/");
    const log = (status, extra) => console.log(new Date().toISOString() + ' ' + req.method + ' ' + p + ' -> ' + status + (extra ? ' ' + extra : ''));
    try {
      if (req.method !== 'GET' && req.method !== 'HEAD') { res.writeHead(405); res.end(); log(405); return; }
      const state = readState(data);
      if (p === INDEX_PATH || p === INDEX_PATH.slice(0, -1)) {
        const since = Date.parse(req.headers['if-modified-since'] || '');
        const modified = Date.parse(state.lastModified);
        if (!Number.isNaN(since) && !Number.isNaN(modified) && modified <= since) {
          res.writeHead(304, { 'Last-Modified': state.lastModified });
          res.end();
          log(304);
          return;
        }
        let body = Buffer.from(JSON.stringify(indexBody(state, origin)));
        const headers = { 'Content-Type': 'application/json', 'Last-Modified': state.lastModified };
        if (/\bgzip\b/.test(req.headers['accept-encoding'] || '')) {
          body = zlib.gzipSync(body);
          headers['Content-Encoding'] = 'gzip';
        }
        headers['Content-Length'] = body.length;
        res.writeHead(200, headers);
        res.end(req.method === 'HEAD' ? undefined : body);
        log(200, body.length + ' bytes' + (headers['Content-Encoding'] ? ' gzip' : ''));
        return;
      }
      let m = /^\/package\/download\/([^/]+)\/([^/]+)\/([^/]+)\/?$/.exec(p);
      if (m) return sendFile(res, path.join(data, 'packages', m[1], m[2], m[3], 'package.zip'), 'application/zip', log);
      m = /^\/icons\/([^-/]+)-(.+)-(\d+(?:\.\d+)*)\.png$/.exec(p);
      if (m) return sendFile(res, path.join(data, 'packages', m[1], m[2], m[3], 'icon.png'), 'image/png', log);
      m = /^\/api\/experimental\/package\/([^/]+)\/([^/]+)\/([^/]+)\/(readme|changelog)\/?$/.exec(p);
      if (m) {
        const file = path.join(data, 'packages', m[1], m[2], m[3], m[4] === 'readme' ? 'README.md' : 'CHANGELOG.md');
        if (!fs.existsSync(file)) { res.writeHead(404); res.end(); log(404); return; }
        const body = Buffer.from(JSON.stringify({ markdown: fs.readFileSync(file, 'utf8') }));
        res.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': body.length });
        res.end(body);
        log(200);
        return;
      }
      if (p === '/status') {
        const body = Buffer.from(JSON.stringify({
          lastModified: state.lastModified,
          packages: state.packages.map(x => ({ package: x.full_name, versions: x.versions.map(v => v.version_number), deprecated: x.is_deprecated })),
        }, null, 2));
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(body);
        return;
      }
      res.writeHead(404); res.end(); log(404);
    } catch (e) {
      res.writeHead(500); res.end(String(e && e.message || e)); log(500, String(e && e.message || e));
    }
  });
  server.listen(port, '127.0.0.1', () => {
    console.log('local Thunderstore on http://127.0.0.1:' + port + ' (data ' + path.resolve(data) + ')');
    console.log('index: http://127.0.0.1:' + port + INDEX_PATH);
  });
}

function sendFile(res, file, type, log) {
  if (!fs.existsSync(file)) { res.writeHead(404); res.end(); log(404); return; }
  const body = fs.readFileSync(file);
  res.writeHead(200, { 'Content-Type': type, 'Content-Length': body.length });
  res.end(body);
  log(200, body.length + ' bytes');
}

const a = args(process.argv.slice(2));
const cmd = a._[0];
const data = typeof a.data === 'string' ? a.data : null;
if (!cmd || !data) {
  console.log('usage: lts.mjs serve|publish|unpublish|list|touch --data <dir> [--port 27450] [--zip <file>] [--package <Owner-Name>] [--version <v>]');
  process.exit(cmd ? 1 : 0);
}
switch (cmd) {
  case 'serve': serve(data, parseInt(a.port || '27450', 10)); break;
  case 'publish': publish(data, a.zip, { deprecated: !!a.deprecated, as: a.as, changelog: a.changelog }); break;
  case 'unpublish': unpublish(data, a.package, typeof a.version === 'string' ? a.version : null); break;
  case 'touch': { const s = readState(data); writeState(data, s); console.log('Last-Modified ' + s.lastModified); break; }
  case 'list': {
    const s = readState(data);
    console.log('Last-Modified ' + s.lastModified);
    for (const p of s.packages) console.log(p.full_name + ': ' + p.versions.map(v => v.version_number).join(', ') + (p.is_deprecated ? ' (deprecated)' : ''));
    break;
  }
  default: fail('unknown command ' + cmd);
}
