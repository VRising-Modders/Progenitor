// Progenitor's signed release list: reading a release package, the list's format and its signature.
// The C# side is src/Progenitor.Core/Install/SelfFeed.cs; docs/auto-update.md (Release list) and docs/releasing.md.
// No dependency beyond Node itself.

import crypto from 'node:crypto';
import zlib from 'node:zlib';

export const SCHEMA = 1;
export const PACKAGE = 'VRising_Modders-Progenitor';
export const CHANNEL = 'release';
export const ASSET_PREFIX = 'https://github.com/VRising-Modders/Progenitor/releases/download/';
export const RELEASE_PAGE_PREFIX = 'https://github.com/VRising-Modders/Progenitor/releases/tag/';
export const PAYLOAD = 'progenitor-files.zip';

// The protocol constants a release is judged by (SelfImageReader in SelfUpdateModel.cs).
export const CORE_PROTOCOL_TYPE = 'Progenitor.BootstrapLink';
export const BOOTSTRAP_PROTOCOL_TYPE = 'Progenitor.Bootstrap.CoreLayout';
export const HANDSHAKE_PROTOCOL_TYPE = 'Progenitor.Networking.Handshake';
export const PROTOCOL_FIELD = 'ProtocolVersion';

export const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');

// ---- zip ----------------------------------------------------------------------------------------------------

/** The entries of a zip archive: [{ name, data }] (stored or deflated; no zip64, no encryption). */
export function readZip(buf) {
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 22 - 65535); i--) {
    if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error('not a zip archive (no end of central directory)');
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  const entries = [];
  for (let n = 0; n < count; n++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error('bad central directory entry');
    const method = buf.readUInt16LE(p + 10);
    const compressed = buf.readUInt32LE(p + 20);
    const nameLen = buf.readUInt16LE(p + 28);
    const extraLen = buf.readUInt16LE(p + 30);
    const commentLen = buf.readUInt16LE(p + 32);
    const local = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nameLen).replace(/\\/g, '/').replace(/^\/+/, '');
    p += 46 + nameLen + extraLen + commentLen;
    if (name.endsWith('/')) continue;
    if (buf.readUInt32LE(local) !== 0x04034b50) throw new Error('bad local header for ' + name);
    const start = local + 30 + buf.readUInt16LE(local + 26) + buf.readUInt16LE(local + 28);
    const raw = buf.subarray(start, start + compressed);
    let data;
    if (method === 0) data = Buffer.from(raw);
    else if (method === 8) data = zlib.inflateRawSync(raw);
    else throw new Error('unsupported compression method ' + method + ' for ' + name);
    entries.push({ name, data });
  }
  return entries;
}

/** A stored (uncompressed) zip of [{ name, data }] (tests). */
export function writeZip(files) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const f of files) {
    const name = Buffer.from(f.name, 'utf8');
    const data = Buffer.from(f.data);
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18); local.writeUInt32LE(data.length, 22); local.writeUInt16LE(name.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20); central.writeUInt32LE(data.length, 24); central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE(offset, 42);
    locals.push(local, name, data);
    centrals.push(central, name);
    offset += 30 + name.length + data.length;
  }
  const dir = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(files.length, 8); end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(dir.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, dir, end]);
}

let crcTable;
function crc32(buf) {
  if (!crcTable) {
    crcTable = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      crcTable[n] = c >>> 0;
    }
  }
  let c = 0xffffffff;
  for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

// ---- .NET metadata: an int constant on a type ----------------------------------------------------------------

/**
 * The value of `<typeName>.ProtocolVersion` (an Int32 literal) in a managed image, or 0 when it has none. Reads the
 * metadata tables up to Constant (ECMA-335 II.22); nothing is loaded or run.
 */
export function readProtocol(image, typeName) {
  const md = metadata(image);
  const dot = typeName.lastIndexOf('.');
  const ns = typeName.slice(0, dot), name = typeName.slice(dot + 1);
  for (let t = 0; t < md.rows[0x02]; t++) {
    const td = md.typeDef(t);
    if (td.name !== name || td.namespace !== ns) continue;
    const end = t + 1 < md.rows[0x02] ? md.typeDef(t + 1).fieldList : md.rows[0x04] + 1;
    for (let f = td.fieldList; f < end; f++) {
      if (md.fieldName(f) !== PROTOCOL_FIELD) continue;
      const c = md.constantOf((f << 2) | 0);
      if (!c || c.type !== 0x08 || c.value.length < 4) return 0;
      return c.value.readInt32LE(0);
    }
  }
  return 0;
}

function metadata(image) {
  const pe = image.readUInt32LE(0x3c);
  if (image.readUInt32LE(pe) !== 0x00004550) throw new Error('not a PE image');
  const coff = pe + 4;
  const sections = image.readUInt16LE(coff + 2);
  const optSize = image.readUInt16LE(coff + 16);
  const opt = coff + 20;
  const magic = image.readUInt16LE(opt);
  const dirs = opt + (magic === 0x20b ? 112 : 96);
  const cliAddress = image.readUInt32LE(dirs + 14 * 8);
  if (!cliAddress) throw new Error('not a managed image');
  const secTable = opt + optSize;
  const toOffset = (address) => {
    for (let s = 0; s < sections; s++) {
      const h = secTable + s * 40;
      const va = image.readUInt32LE(h + 12), size = Math.max(image.readUInt32LE(h + 8), image.readUInt32LE(h + 16));
      if (address >= va && address < va + size) return address - va + image.readUInt32LE(h + 20);
    }
    throw new Error('an address outside every section');
  };
  const cli = toOffset(cliAddress);
  const root = toOffset(image.readUInt32LE(cli + 8));
  if (image.readUInt32LE(root) !== 0x424a5342) throw new Error('no metadata root');
  const verLen = image.readUInt32LE(root + 12);
  let p = root + 16 + verLen;
  const streamCount = image.readUInt16LE(p + 2);
  p += 4;
  const streams = {};
  for (let i = 0; i < streamCount; i++) {
    const off = image.readUInt32LE(p), size = image.readUInt32LE(p + 4);
    const nameStart = p + 8;
    let q = nameStart, n = '';
    while (image[q] !== 0) n += String.fromCharCode(image[q++]);
    streams[n] = { off: root + off, size };
    // The name and its terminator, padded to four bytes from where the name starts.
    p = nameStart + ((q - nameStart + 1 + 3) & ~3);
  }
  const tilde = streams['#~'] || streams['#-'];
  if (!tilde || !streams['#Strings'] || !streams['#Blob']) throw new Error('metadata streams missing');
  const t = tilde.off;
  const heap = image[t + 6];
  const strIdx = heap & 1 ? 4 : 2, blobIdx = heap & 4 ? 4 : 2, guidIdx = heap & 2 ? 4 : 2;
  const validLo = image.readUInt32LE(t + 8), validHi = image.readUInt32LE(t + 12);
  const rows = new Array(64).fill(0);
  let r = t + 24;
  for (let i = 0; i < 64; i++) {
    const present = i < 32 ? (validLo >>> i) & 1 : (validHi >>> (i - 32)) & 1;
    if (present) { rows[i] = image.readUInt32LE(r); r += 4; }
  }
  const idx = (table) => (rows[table] < 0x10000 ? 2 : 4);
  const coded = (bits, tables) => (Math.max(...tables.map((x) => rows[x])) < 1 << (16 - bits) ? 2 : 4);
  const typeDefOrRef = coded(2, [0x02, 0x01, 0x1b]);
  const resolutionScope = coded(2, [0x00, 0x1a, 0x23, 0x01]);
  const hasConstant = coded(2, [0x04, 0x08, 0x17]);
  const memberRefParent = coded(3, [0x02, 0x01, 0x1a, 0x06, 0x1b]);
  const sizes = [];
  sizes[0x00] = 2 + strIdx + 3 * guidIdx;
  sizes[0x01] = resolutionScope + 2 * strIdx;
  sizes[0x02] = 4 + 2 * strIdx + typeDefOrRef + idx(0x04) + idx(0x06);
  sizes[0x03] = idx(0x04);
  sizes[0x04] = 2 + strIdx + blobIdx;
  sizes[0x05] = idx(0x06);
  sizes[0x06] = 4 + 2 + 2 + strIdx + blobIdx + idx(0x08);
  sizes[0x07] = idx(0x08);
  sizes[0x08] = 2 + 2 + strIdx;
  sizes[0x09] = idx(0x02) + typeDefOrRef;
  sizes[0x0a] = memberRefParent + strIdx + blobIdx;
  sizes[0x0b] = 2 + hasConstant + blobIdx;
  const start = [];
  let at = r;
  for (let i = 0; i <= 0x0b; i++) { start[i] = at; at += rows[i] * sizes[i]; }
  const read = (o, n) => (n === 2 ? image.readUInt16LE(o) : image.readUInt32LE(o));
  const str = (i) => {
    let q = streams['#Strings'].off + i;
    const s = q;
    while (image[q] !== 0) q++;
    return image.toString('utf8', s, q);
  };
  const blob = (i) => {
    let q = streams['#Blob'].off + i;
    const b = image[q];
    let len;
    if ((b & 0x80) === 0) { len = b; q += 1; }
    else if ((b & 0xc0) === 0x80) { len = ((b & 0x3f) << 8) | image[q + 1]; q += 2; }
    else { len = ((b & 0x1f) << 24) | (image[q + 1] << 16) | (image[q + 2] << 8) | image[q + 3]; q += 4; }
    return image.subarray(q, q + len);
  };
  return {
    rows,
    typeDef(i) {
      const o = start[0x02] + i * sizes[0x02];
      return {
        name: str(read(o + 4, strIdx)),
        namespace: str(read(o + 4 + strIdx, strIdx)),
        fieldList: read(o + 4 + 2 * strIdx + typeDefOrRef, idx(0x04)),
      };
    },
    fieldName(f) { return str(read(start[0x04] + (f - 1) * sizes[0x04] + 2, strIdx)); },
    constantOf(parent) {
      for (let i = 0; i < rows[0x0b]; i++) {
        const o = start[0x0b] + i * sizes[0x0b];
        if (read(o + 2, hasConstant) !== parent) continue;
        return { type: image[o], value: blob(read(o + 2 + hasConstant, blobIdx)) };
      }
      return null;
    },
  };
}

// ---- a release package -----------------------------------------------------------------------------------------

/**
 * What the release list records about a package zip, read the way SelfImageReader reads it: Bootstrap from
 * BepInEx/plugins, Core from BepInEx/progenitor-core (as files, or inside BepInEx/patchers/progenitor-files.zip), and the
 * self-install record when the zip carries one.
 */
export function readPackage(zipBytes) {
  let core = null, boot = null, record = null;
  const take = (path, data) => {
    const lower = path.toLowerCase();
    if (lower.endsWith('/plugins/progenitor.bootstrap.dll') || lower === 'plugins/progenitor.bootstrap.dll') boot = data;
    else if (lower.endsWith('/progenitor-core/progenitor.dll') || lower === 'progenitor-core/progenitor.dll') core = data;
  };
  for (const e of readZip(zipBytes)) {
    const lower = e.name.toLowerCase();
    if (lower.endsWith('/patchers/' + PAYLOAD) || lower === 'patchers/' + PAYLOAD) {
      for (const n of readZip(e.data)) take(n.name, n.data);
    } else if (lower === 'bepinex/progenitor-self-install.json') {
      try { record = JSON.parse(e.data.toString('utf8')); } catch { record = null; }
    } else take(e.name, e.data);
  }
  if (!core) throw new Error('the package has no BepInEx/progenitor-core/Progenitor.dll');
  if (!boot) throw new Error('the package has no BepInEx/plugins/Progenitor.Bootstrap.dll');
  const coreProtocol = readProtocol(core, CORE_PROTOCOL_TYPE);
  const bootstrapProtocol = readProtocol(boot, BOOTSTRAP_PROTOCOL_TYPE);
  const handshakeProtocol = readProtocol(core, HANDSHAKE_PROTOCOL_TYPE);
  if (!coreProtocol) throw new Error('Core has no ' + CORE_PROTOCOL_TYPE + '.' + PROTOCOL_FIELD);
  if (!bootstrapProtocol) throw new Error('Bootstrap has no ' + BOOTSTRAP_PROTOCOL_TYPE + '.' + PROTOCOL_FIELD);
  if (coreProtocol !== bootstrapProtocol) throw new Error('Core (protocol ' + coreProtocol + ') and Bootstrap (protocol ' + bootstrapProtocol + ') do not match');
  return {
    sha256: sha256(zipBytes),
    size: zipBytes.length,
    coreSha256: sha256(core),
    bootstrapSha256: sha256(boot),
    coreProtocol,
    bootstrapProtocol,
    handshakeProtocol,
    record,
  };
}

// ---- the list --------------------------------------------------------------------------------------------------

export function emptyFeed() {
  return { schema: SCHEMA, package: PACKAGE, updated: '', releases: [] };
}

/** Compare two date versions (year.month.build[.sub]). */
export function compareVersions(a, b) {
  const pa = String(a).split('.').map((x) => parseInt(x, 10) || 0), pb = String(b).split('.').map((x) => parseInt(x, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d !== 0) return d;
  }
  return 0;
}

/** Add (or with replace, replace) one release; the list stays newest first. */
export function addRelease(feed, entry, { replace = false, now = new Date() } = {}) {
  const out = { ...feed, releases: [...(feed.releases || [])] };
  const at = out.releases.findIndex((r) => r.version === entry.version);
  if (at >= 0 && !replace) throw new Error('the release list already has ' + entry.version + ' (pass --replace to replace it)');
  if (at >= 0) out.releases.splice(at, 1);
  out.releases.push(entry);
  out.releases.sort((x, y) => compareVersions(y.version, x.version));
  return touchFeed(out, now);
}

/**
 * The list with `updated` moved to now, and always later than before (a client refuses a list signed earlier than the one
 * it has cached, so each signing must move it forward).
 */
export function touchFeed(feed, now = new Date()) {
  const prev = Date.parse(feed.updated || '');
  let t = Math.floor(now.getTime() / 1000) * 1000;
  if (!Number.isNaN(prev) && t <= prev) t = prev + 1000;
  return { ...feed, updated: new Date(t).toISOString().replace(/\.\d{3}Z$/, 'Z') };
}

/** The list's exact bytes: two-space JSON, LF, one trailing newline (the signature covers these bytes). */
export const feedBytes = (feed) => Buffer.from(JSON.stringify(feed, null, 2) + '\n', 'utf8');

// ---- keys and signatures ---------------------------------------------------------------------------------------

/** A private key from PEM text, or base64 PKCS#8 DER. */
export function privateKey(text) {
  const t = String(text || '').trim();
  if (!t) throw new Error('the release key is empty');
  if (t.includes('-----BEGIN')) return crypto.createPrivateKey(t);
  return crypto.createPrivateKey({ key: Buffer.from(t, 'base64'), format: 'der', type: 'pkcs8' });
}

export const spkiOf = (key) => crypto.createPublicKey(key).export({ type: 'spki', format: 'der' });
export const fingerprint = (spki) => crypto.createHash('sha256').update(spki).digest('hex').slice(0, 16);

export function generateKey() {
  const { privateKey: priv } = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  return {
    pem: priv.export({ type: 'pkcs8', format: 'pem' }),
    spkiBase64: spkiOf(priv).toString('base64'),
    fingerprint: fingerprint(spkiOf(priv)),
  };
}

/** The detached signature file: one signature per key, each ECDSA P-256 + SHA-256, IEEE P1363 r||s as hex. */
export function sign(bytes, keys) {
  if (!keys.length) throw new Error('no release key to sign with');
  return {
    schema: SCHEMA,
    algorithm: 'ECDSA P-256 + SHA-256',
    signatures: keys.map((k) => {
      const key = typeof k === 'string' ? privateKey(k) : k;
      if (key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails?.namedCurve !== 'prime256v1') throw new Error('the release key is not an ECDSA P-256 key');
      return {
        key: fingerprint(spkiOf(key)),
        signature: crypto.sign('sha256', bytes, { key, dsaEncoding: 'ieee-p1363' }).toString('hex'),
      };
    }),
  };
}

export const signatureBytes = (sig) => Buffer.from(JSON.stringify(sig) + '\n', 'utf8');

/** Whether one signature verifies with one of the public keys (SPKI base64). */
export function verify(bytes, sig, publicKeys) {
  for (const pub of publicKeys) {
    if (!pub) continue;
    const spki = Buffer.from(pub, 'base64');
    const key = crypto.createPublicKey({ key: spki, format: 'der', type: 'spki' });
    const fp = fingerprint(spki);
    for (const s of sig.signatures || []) {
      if (s.key && s.key !== fp) continue;
      if (crypto.verify('sha256', bytes, { key, dsaEncoding: 'ieee-p1363' }, Buffer.from(s.signature, 'hex'))) return true;
    }
  }
  return false;
}

// ---- the built-in keys (src/Progenitor.Core/Install/SelfFeedKeys.cs) --------------------------------------------

const SLOT = { current: 'Current', next: 'Next' };

/** The keys the C# source builds in: { current, next } (empty strings for empty slots). */
export function readSourceKeys(source) {
  const get = (name) => {
    const m = new RegExp('public static readonly string ' + name + ' = "([^"]*)";').exec(source);
    if (!m) throw new Error('SelfFeedKeys.cs has no ' + name + ' slot');
    return m[1];
  };
  return { current: get('Current'), next: get('Next') };
}

/** The C# source with one slot set to a public key. */
export function writeSourceKey(source, slot, spkiBase64) {
  const name = SLOT[slot];
  if (!name) throw new Error('--slot is current or next');
  if (!/^[A-Za-z0-9+/=]+$/.test(spkiBase64)) throw new Error('not a base64 key');
  const re = new RegExp('(public static readonly string ' + name + ' = ")[^"]*(";)');
  if (!re.test(source)) throw new Error('SelfFeedKeys.cs has no ' + name + ' slot');
  return source.replace(re, '$1' + spkiBase64 + '$2');
}
