#!/usr/bin/env node
// index-catalog.mjs - offline index of V Rising's shipped Unity Entities content catalog.
//
// Reads   <game>/VRising_Data/StreamingAssets/ContentArchives/archive_dependencies.bin
// (a BlobAssetReference<Unity.Entities.Content.RuntimeContentCatalogData>, see README.md)
// and emits catalog-index.json: every object id -> archive file, type and any name recovered,
// plus a summary of counts per type and per archive.
//
// No game process, no Unity, no dependencies. Read-only on the game install.
//
// See README.md for the binary layout, the metadata layouts and the known gaps.

import { readFileSync, writeFileSync, existsSync, statSync, openSync, readSync, closeSync, readdirSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------- arguments

const DEFAULT_GAME = 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRising';

function parseArgs(argv) {
  const a = {
    game: DEFAULT_GAME,
    out: HERE,
    archives: false,   // parse every archive's UnityFS header + directory
    deep: false,       // + decompress serialized-file metadata for archives holding catalog objects
    deepCap: 8 * 1024 * 1024,   // per-archive decompression budget in --deep
    nameCap: 64 * 1024 * 1024,  // extra budget when an object body must be reached for m_Name
    pretty: false,
    verify: false,     // cross-check against the shipped archive_dependencies.txt
    limitDeep: 0,      // 0 = no limit
    inventory: false,  // enumerate the SerializedFile object table of every archive
    inventoryCap: 4 * 1024 * 1024,
  };
  for (let i = 2; i < argv.length; i++) {
    const k = argv[i];
    if (k === '--game') a.game = argv[++i];
    else if (k === '--out') a.out = argv[++i];
    else if (k === '--archives') a.archives = true;
    else if (k === '--deep') { a.deep = true; a.archives = true; }
    else if (k === '--deep-cap') a.deepCap = Number(argv[++i]);
    else if (k === '--name-cap') a.nameCap = Number(argv[++i]);
    else if (k === '--limit-deep') a.limitDeep = Number(argv[++i]);
    else if (k === '--inventory') { a.inventory = true; a.archives = true; }
    else if (k === '--inventory-cap') a.inventoryCap = Number(argv[++i]);
    else if (k === '--pretty') a.pretty = true;
    else if (k === '--verify') a.verify = true;
    else if (k === '-h' || k === '--help') { usage(); process.exit(0); }
    else { console.error(`unknown argument: ${k}`); usage(); process.exit(2); }
  }
  return a;
}

function usage() {
  console.log(`usage: node index-catalog.mjs [options]

  --game <dir>        V Rising install root (default: ${DEFAULT_GAME})
  --out <dir>         output directory (default: this script's folder)
  --archives          also parse every ContentArchive's UnityFS header + directory
                      (inner file name, block table, node table) - ~4.4k small reads
  --deep              --archives, plus LZ4-decompress the SerializedFile metadata of the
                      archives that hold catalog objects, to recover Unity class ids and,
                      where the object body falls inside the budget, m_Name
  --deep-cap <bytes>  per-archive decompression budget for --deep (default 8388608)
  --name-cap <bytes>  extra budget to reach an object body and read m_Name (default 67108864)
  --inventory         enumerate every archive's SerializedFile object table (class ids and,
                      within the budget, m_Name) into catalog-inventory.json
  --inventory-cap <b> per-archive decompression budget for --inventory (default 4194304)
  --limit-deep <n>    only deep-parse the first n archives (debugging)
  --verify            cross-check the decode against the shipped archive_dependencies.txt
  --pretty            pretty-print catalog-index.json (much larger)
`);
}

// ---------------------------------------------------------------- Hash128

// Unity's Hash128 text form writes each byte low-nibble first (verified against the shipped
// archive_dependencies.txt: blob bytes 1e72d726... print as e1277d62...).
const HEX = '0123456789abcdef';
function hash128(buf, off) {
  let s = '';
  for (let i = 0; i < 16; i++) {
    const b = buf[off + i];
    s += HEX[b & 0x0f] + HEX[b >> 4];
  }
  return s;
}

// ---------------------------------------------------------------- catalog blob

// Unity.Entities.Serialization.BlobAssetHeader (32 bytes) preceded by a 4-byte version int:
//   int version | void* ValidationPtr | int Length | Allocator | ulong Hash | ulong Padding
const BLOB_BASE = 36;

// RuntimeContentCatalogData
//   BlobArray<ContentArchiveLocation> Archives  0x00
//   BlobArray<ContentFileLocation>    Files     0x08
//   BlobArray<ContentObjectLocation>  Objects   0x10
//   BlobArray<ContentSceneLocation>   Scenes    0x18
//   BlobArray<BlobArray<int>>         Dependencies 0x20
//   BlobArray<ContentBlobLocation>    Blobs     0x28
const ROOT_FIELDS = ['Archives', 'Files', 'Objects', 'Scenes', 'Dependencies', 'Blobs'];

// A Unity BlobArray<T> is { int m_OffsetPtr; int m_Length; } where the payload starts at
// (address of m_OffsetPtr) + m_OffsetPtr.
function blobArray(buf, fieldBlobOffset) {
  const off = buf.readInt32LE(BLOB_BASE + fieldBlobOffset);
  const len = buf.readInt32LE(BLOB_BASE + fieldBlobOffset + 4);
  return { base: fieldBlobOffset + off, length: len, rawOffset: off };
}

const GENERATION_TYPE = { // WeakReferenceGenerationType
  0: 'Unknown', 1: 'UnityObject', 2: 'Texture', 3: 'GameObjectScene',
  4: 'EntityScene', 5: 'EntityPrefab', 6: 'SubSceneObjectReferences',
};

function readCatalog(binPath) {
  const buf = readFileSync(binPath);
  const version = buf.readInt32LE(0);
  const validationPtr = buf.readBigInt64LE(4);
  const length = buf.readInt32LE(12);
  const allocator = buf.readInt32LE(16);
  const blobHash = buf.readBigUInt64LE(20);
  const padding = buf.readBigUInt64LE(28);

  const header = {
    version, validationPtr: validationPtr.toString(), blobLength: length,
    allocator, blobHash: '0x' + blobHash.toString(16), padding: padding.toString(),
    fileSize: buf.length, blobDataStart: BLOB_BASE,
    headerConsistent: length + BLOB_BASE === buf.length,
  };
  if (!header.headerConsistent) {
    throw new Error(`catalog header inconsistent: ${length} + ${BLOB_BASE} != ${buf.length}`);
  }

  const roots = {};
  ROOT_FIELDS.forEach((n, i) => { roots[n] = blobArray(buf, i * 8); });

  // --- Archives: ContentArchiveLocation { ContentArchiveId ArchiveId (Hash128) }  16 bytes
  const archives = [];
  for (let i = 0; i < roots.Archives.length; i++) {
    archives.push({ index: i, id: hash128(buf, BLOB_BASE + roots.Archives.base + i * 16) });
  }

  // --- Files: ContentFileLocation { ContentFileId FileId 0x0; int ArchiveIndex 0x10;
  //                                  int DependencyIndex 0x14 }  24 bytes
  const files = [];
  for (let i = 0; i < roots.Files.length; i++) {
    const o = BLOB_BASE + roots.Files.base + i * 24;
    files.push({
      index: i,
      id: hash128(buf, o),
      archiveIndex: buf.readInt32LE(o + 16),
      dependencyIndex: buf.readInt32LE(o + 20),
    });
  }

  // --- Dependencies: BlobArray<BlobArray<int>>; each inner list holds file indices.
  const dependencySets = [];
  for (let i = 0; i < roots.Dependencies.length; i++) {
    const field = roots.Dependencies.base + i * 8;
    const off = buf.readInt32LE(BLOB_BASE + field);
    const len = buf.readInt32LE(BLOB_BASE + field + 4);
    const list = [];
    for (let j = 0; j < len; j++) list.push(buf.readInt32LE(BLOB_BASE + field + off + j * 4));
    dependencySets.push(list);
  }

  // --- Objects: ContentObjectLocation { UnsafeUntypedWeakReferenceId ObjectId 0x0 (48 bytes);
  //     int FileIndex 0x30; long LocalIdentifierInFile 0x38 }  64 bytes
  //     UnsafeUntypedWeakReferenceId = RuntimeGlobalObjectId GlobalId 0x0 (40 bytes)
  //                                    + short GenerationType 0x28
  //     RuntimeGlobalObjectId = long SceneObjectIdentifier0 0x0; long SceneObjectIdentifier1 0x8;
  //                             Hash128 AssetGUID 0x10; int IdentifierType 0x20
  const objects = [];
  for (let i = 0; i < roots.Objects.length; i++) {
    const o = BLOB_BASE + roots.Objects.base + i * 64;
    const genType = buf.readInt16LE(o + 0x28);
    objects.push({
      index: i,
      assetGuid: hash128(buf, o + 0x10),
      sceneObjectIdentifier0: buf.readBigInt64LE(o + 0x00).toString(),
      sceneObjectIdentifier1: buf.readBigInt64LE(o + 0x08).toString(),
      identifierType: buf.readInt32LE(o + 0x20),
      generationType: genType,
      generationTypeName: GENERATION_TYPE[genType] ?? `Unknown(${genType})`,
      fileIndex: buf.readInt32LE(0x30 + o),
      localIdentifierInFile: buf.readBigInt64LE(o + 0x38).toString(),
    });
  }

  // --- Scenes: ContentSceneLocation { UnsafeUntypedWeakReferenceId SceneId 0x0;
  //     int FileIndex 0x30; FixedString128Bytes SceneName 0x34 }
  const scenes = [];
  for (let i = 0; i < roots.Scenes.length; i++) {
    const o = BLOB_BASE + roots.Scenes.base + i * 180;
    const nameLen = buf.readUInt16LE(o + 0x34);
    scenes.push({
      index: i,
      assetGuid: hash128(buf, o + 0x10),
      generationType: buf.readInt16LE(o + 0x28),
      fileIndex: buf.readInt32LE(o + 0x30),
      sceneName: buf.toString('utf8', o + 0x36, o + 0x36 + nameLen),
    });
  }

  // --- Blobs: ContentBlobLocation { ObjectId 0x0; int FileIndex 0x30; long Offset 0x38;
  //     long Length 0x40 }  72 bytes
  const blobs = [];
  for (let i = 0; i < roots.Blobs.length; i++) {
    const o = BLOB_BASE + roots.Blobs.base + i * 72;
    blobs.push({
      index: i,
      assetGuid: hash128(buf, o + 0x10),
      fileIndex: buf.readInt32LE(o + 0x30),
      offset: buf.readBigInt64LE(o + 0x38).toString(),
      length: buf.readBigInt64LE(o + 0x40).toString(),
    });
  }

  // Region accounting - a per-byte coverage map of the blob, so anything we could not
  // attribute to a decoded array is reported as an explicit unattributed range.
  const cover = new Uint8Array(length);
  const regions = [];
  const claim = (name, start, end) => {
    if (end <= start) return;
    regions.push({ name, start, end, bytes: end - start });
    for (let i = start; i < end && i < length; i++) cover[i] = 1;
  };
  claim('root (RuntimeContentCatalogData)', 0, 48);
  claim('Dependencies index (BlobArray<BlobArray<int>>)', roots.Dependencies.base, roots.Dependencies.base + roots.Dependencies.length * 8);
  claim('Archives (ContentArchiveLocation[16])', roots.Archives.base, roots.Archives.base + roots.Archives.length * 16);
  claim('Files (ContentFileLocation[24])', roots.Files.base, roots.Files.base + roots.Files.length * 24);
  claim('Objects (ContentObjectLocation[64])', roots.Objects.base, roots.Objects.base + roots.Objects.length * 64);
  claim('Scenes (ContentSceneLocation[180])', roots.Scenes.base, roots.Scenes.base + roots.Scenes.length * 180);
  claim('Blobs (ContentBlobLocation[72])', roots.Blobs.base, roots.Blobs.base + roots.Blobs.length * 72);
  let depPayloadBytes = 0;
  for (let i = 0; i < roots.Dependencies.length; i++) {
    const field = roots.Dependencies.base + i * 8;
    const off = buf.readInt32LE(BLOB_BASE + field);
    const len = buf.readInt32LE(BLOB_BASE + field + 4);
    if (len === 0) continue;
    for (let k = field + off; k < field + off + len * 4 && k < length; k++) cover[k] = 1;
    depPayloadBytes += len * 4;
  }
  regions.push({ name: 'Dependencies payload (int[] file indices, scattered)', bytes: depPayloadBytes });
  regions.sort((a, b) => (a.start ?? 1e12) - (b.start ?? 1e12));
  const gaps = [];
  for (let i = 0; i < length;) {
    if (cover[i]) { i++; continue; }
    let j = i; while (j < length && !cover[j]) j++;
    gaps.push({ start: i, end: j, bytes: j - i, firstBytes: buf.subarray(BLOB_BASE + i, BLOB_BASE + Math.min(j, i + 32)).toString('hex') });
    i = j;
  }
  const attributed = cover.reduce((s, x) => s + x, 0);

  return { header, roots, archives, files, objects, scenes, blobs, dependencySets, regions, gaps, attributed, buf };
}

// ---------------------------------------------------------------- LZ4 block

function lz4Decompress(src, destSize) {
  const dst = Buffer.allocUnsafe(destSize);
  let s = 0, d = 0;
  while (s < src.length) {
    const token = src[s++];
    let litLen = token >> 4;
    if (litLen === 15) { let b; do { b = src[s++]; litLen += b; } while (b === 255); }
    for (let i = 0; i < litLen; i++) dst[d++] = src[s++];
    if (s >= src.length) break;
    const offset = src[s] | (src[s + 1] << 8); s += 2;
    let matchLen = (token & 0x0f) + 4;
    if ((token & 0x0f) === 15) { let b; do { b = src[s++]; matchLen += b; } while (b === 255); }
    let m = d - offset;
    for (let i = 0; i < matchLen; i++) dst[d++] = dst[m++];
  }
  return dst.subarray(0, d);
}

// ---------------------------------------------------------------- UnityFS

class BE {
  constructor(buf) { this.b = buf; this.p = 0; }
  u8() { return this.b[this.p++]; }
  u16() { const v = this.b.readUInt16BE(this.p); this.p += 2; return v; }
  u32() { const v = this.b.readUInt32BE(this.p); this.p += 4; return v; }
  i64() { const v = this.b.readBigInt64BE(this.p); this.p += 8; return v; }
  cstr() { const i = this.b.indexOf(0, this.p); const s = this.b.toString('utf8', this.p, i); this.p = i + 1; return s; }
  align(n) { const r = this.p % n; if (r) this.p += n - r; }
}

// Reads the UnityFS header and the (LZ4-compressed) blocks/directory info without touching
// the payload. Returns { unityVersion, unityRevision, size, flags, compression, blocks, nodes }.
function readUnityFsDirectory(file) {
  const fd = openSync(file, 'r');
  try {
    const head = Buffer.alloc(4096);
    const got = readSync(fd, head, 0, 4096, 0);
    const r = new BE(head.subarray(0, got));
    const signature = r.cstr();
    if (signature !== 'UnityFS') return { error: `not UnityFS (signature=${JSON.stringify(signature)})` };
    const version = r.u32();
    const unityVersion = r.cstr();
    const unityRevision = r.cstr();
    const size = Number(r.i64());
    const compressedBlocksInfoSize = r.u32();
    const uncompressedBlocksInfoSize = r.u32();
    const flags = r.u32();
    if (version >= 7) r.align(16);
    const compression = flags & 0x3f;
    const blocksInfoAtEnd = (flags & 0x80) !== 0;

    const start = blocksInfoAtEnd ? size - compressedBlocksInfoSize : r.p;
    const raw = Buffer.alloc(compressedBlocksInfoSize);
    readSync(fd, raw, 0, compressedBlocksInfoSize, start);
    let info;
    if (compression === 0) info = raw;
    else if (compression === 2 || compression === 3) info = lz4Decompress(raw, uncompressedBlocksInfoSize);
    else return { error: `unsupported blocksInfo compression ${compression}` };

    const b = new BE(info);
    const uncompressedDataHash = info.subarray(0, 16).toString('hex'); b.p = 16;
    const blockCount = b.u32();
    const blocks = [];
    for (let i = 0; i < blockCount; i++) {
      blocks.push({ uncompressedSize: b.u32(), compressedSize: b.u32(), flags: b.u16() });
    }
    const nodeCount = b.u32();
    const nodes = [];
    for (let i = 0; i < nodeCount; i++) {
      nodes.push({ offset: Number(b.i64()), size: Number(b.i64()), flags: b.u32(), path: b.cstr() });
    }
    // flags & 0x200 (BlockInfoNeedPaddingAtStart): the data blocks begin at the next
    // 16-byte boundary. Verified empirically - reading a block from the unaligned offset
    // decodes to the wrong length (e.g. 1616 instead of 1612 for archive 2cc06ff8...).
    let dataStart = blocksInfoAtEnd ? r.p : start + compressedBlocksInfoSize;
    if (flags & 0x200) dataStart = Math.ceil(dataStart / 16) * 16;
    return {
      version, unityVersion, unityRevision, size, flags,
      compression, blocksInfoAtEnd, uncompressedDataHash,
      dataStart, blocks, nodes,
    };
  } finally { closeSync(fd); }
}

// Decompress the leading blocks of a UnityFS payload up to `cap` uncompressed bytes.
function readUnityFsPrefix(file, dir, cap) {
  const fd = openSync(file, 'r');
  try {
    const parts = [];
    let pos = dir.dataStart, total = 0;
    for (const blk of dir.blocks) {
      if (total >= cap) break;
      const raw = Buffer.alloc(blk.compressedSize);
      readSync(fd, raw, 0, blk.compressedSize, pos);
      pos += blk.compressedSize;
      const comp = blk.flags & 0x3f;
      let out;
      if (comp === 0) out = raw;
      else if (comp === 2 || comp === 3) out = lz4Decompress(raw, blk.uncompressedSize);
      else break;
      parts.push(out);
      total += out.length;
    }
    return { data: Buffer.concat(parts), truncated: total < dir.blocks.reduce((s, b) => s + b.uncompressedSize, 0) };
  } finally { closeSync(fd); }
}

// ---------------------------------------------------------------- SerializedFile

// Unity class ids we care about naming. Anything else is reported as `Class<id>`.
const CLASS_NAMES = {
  0: 'Object', 1: 'GameObject', 2: 'Component', 4: 'Transform', 8: 'Behaviour', 18: 'EditorExtension',
  20: 'Camera', 21: 'Material', 23: 'MeshRenderer', 25: 'Renderer', 26: 'ParticleRenderer',
  27: 'Texture', 28: 'Texture2D', 33: 'MeshFilter', 43: 'Mesh', 48: 'Shader', 49: 'TextAsset',
  54: 'Rigidbody', 64: 'MeshCollider', 65: 'BoxCollider', 74: 'AnimationClip', 82: 'AudioSource',
  83: 'AudioClip', 89: 'Cubemap', 91: 'AnimatorController', 95: 'Animator', 108: 'Light',
  111: 'Animation', 114: 'MonoBehaviour', 115: 'MonoScript', 117: 'Texture3D',
  128: 'Font', 135: 'SphereCollider', 137: 'SkinnedMeshRenderer', 150: 'PreloadData',
  156: 'TerrainData', 187: 'Texture2DArray', 198: 'ParticleSystem', 199: 'ParticleSystemRenderer',
  212: 'SpriteRenderer', 213: 'Sprite', 222: 'CanvasRenderer', 223: 'Canvas',
  224: 'RectTransform', 225: 'CanvasGroup', 226: 'BillboardAsset', 240: 'AudioMixer',
  290: 'AssetBundleManifest', 72: 'ComputeShader', 90: 'Avatar', 96: 'TrailRenderer',
  120: 'LineRenderer', 136: 'CapsuleCollider', 205: 'LODGroup', 221: 'AnimatorOverrideController',
  244: 'AudioMixerGroupController', 245: 'AudioMixerSnapshotController',
  328: 'VideoPlayer', 329: 'VideoClip',
  850595691: 'LightingSettings', 1480428607: 'SceneRoots', 1953259897: 'TerrainLayer',
  2058629509: 'VisualEffectAsset', 2058629511: 'VisualEffectResource',
  156049354: 'Grid', 1742807556: 'GraphicsStateCollection',
};

// A SerializedFile is little-endian in shipped players; only header sizes are big-endian.
function readSerializedFile(data) {
  if (data.length < 48) return { error: 'too short' };
  const guard = (fn) => { try { return fn(); } catch { return { error: 'short read' }; } };
  return guard(() => readSerializedFileInner(data));
}

function readSerializedFileInner(data) {
  const be = new BE(data);
  /* legacy */ be.u32(); /* metadataSize */
  /* legacy */ be.u32(); /* fileSize */
  const version = be.u32();
  /* legacy */ be.u32(); /* dataOffset */
  if (version < 22) return { error: `unsupported SerializedFile version ${version}` };
  const bigEndian = be.u8(); be.p += 3;
  const metadataSize = be.u32();
  const fileSize = Number(be.i64());
  const dataOffset = Number(be.i64());
  be.i64(); // unknown
  if (bigEndian) return { error: 'big-endian SerializedFile' };

  let p = be.p;
  const rd = {
    u8: () => data[p++],
    i16: () => { const v = data.readInt16LE(p); p += 2; return v; },
    i32: () => { const v = data.readInt32LE(p); p += 4; return v; },
    u32: () => { const v = data.readUInt32LE(p); p += 4; return v; },
    i64: () => { const v = data.readBigInt64LE(p); p += 8; return v; },
    cstr: () => { const i = data.indexOf(0, p); const s = data.toString('utf8', p, i); p = i + 1; return s; },
    align: () => { const r = p % 4; if (r) p += 4 - r; },
  };
  const unityVersion = rd.cstr();
  const targetPlatform = rd.i32();
  const enableTypeTree = rd.u8() !== 0;
  const typeCount = rd.i32();
  if (typeCount < 0 || typeCount > 200000) return { error: `implausible typeCount ${typeCount}` };
  const types = [];
  for (let i = 0; i < typeCount; i++) {
    const classID = rd.i32();
    const isStripped = rd.u8();
    const scriptTypeIndex = rd.i16();
    if (classID === 114) p += 16; // script id hash
    p += 16;                      // old type hash
    if (enableTypeTree) {
      // Blob-form type tree (version >= 12): int nodeCount, int stringBufferSize,
      // nodeCount fixed-size node records (32 bytes for version >= 19), then the string blob.
      const nodeCount = rd.i32();
      const stringBufferSize = rd.i32();
      p += nodeCount * (version >= 19 ? 32 : 24) + stringBufferSize;
      if (version >= 21) { const depCount = rd.i32(); p += depCount * 4; } // m_TypeDependencies
    }
    types.push({ classID, isStripped, scriptTypeIndex });
  }
  const objectCount = rd.i32();
  if (objectCount < 0 || objectCount > 5000000) return { error: `implausible objectCount ${objectCount}` };
  const objects = [];
  for (let i = 0; i < objectCount; i++) {
    rd.align();
    const pathID = rd.i64();
    const byteStart = Number(rd.i64());
    const byteSize = rd.u32();
    const typeID = rd.i32();
    objects.push({
      pathID: pathID.toString(),
      byteStart, byteSize, typeID,
      classID: types[typeID]?.classID ?? -1,
    });
  }
  // m_ScriptTypes (version >= 11), then m_Externals - the files this one references by
  // PPtr.m_FileID (1-based into this list). For a content archive an external's pathName
  // is the sibling archive/file id.
  const scriptTypeCount = rd.i32();
  for (let i = 0; i < scriptTypeCount; i++) { rd.i32(); rd.align(); rd.i64(); }
  const externalCount = rd.i32();
  const externals = [];
  if (externalCount >= 0 && externalCount < 100000) {
    for (let i = 0; i < externalCount; i++) {
      rd.cstr();          // tempEmpty
      p += 16;            // guid
      const type = rd.i32();
      externals.push({ type, pathName: rd.cstr() });
    }
  }
  return { version, unityVersion, targetPlatform, metadataSize, fileSize, dataOffset, types, objects, externals };
}

// Byte offset of m_Name inside an object body, per class.
//   NamedObject subclasses serialize m_Name (int32 length + utf8, align 4) first.
//   MonoBehaviour serializes m_GameObject (PPtr, 12) + m_Enabled (bool, aligned to 4)
//   + m_Script (PPtr, 12) before m_Name, i.e. offset 28.
const NAME_AT_ZERO = new Set([
  21, 27, 28, 43, 48, 49, 74, 83, 89, 91, 111, 117, 128, 156, 187, 213, 221, 226, 240,
  2058629509, 2058629511, 1953259897,
]);
function nameOffsetFor(classID) {
  if (NAME_AT_ZERO.has(classID)) return 0;
  if (classID === 114) return 28;  // MonoBehaviour: m_GameObject(12) + m_Enabled(4) + m_Script(12)
  if (classID === 1) return -2;    // GameObject: variable, computed from m_Component's length
  return -1;
}

// A MonoBehaviour body starts with m_GameObject (PPtr, 12 bytes) then m_Enabled (bool,
// padded to 4). m_Script is the PPtr at offset 16: { int m_FileID; long m_PathID }.
function readScriptPPtr(data, bodyOffset) {
  if (bodyOffset + 28 > data.length) return null;
  return { fileID: data.readInt32LE(bodyOffset + 16), pathID: data.readBigInt64LE(bodyOffset + 20).toString() };
}

function readObjectName(data, bodyOffset, classID) {
  let rel = nameOffsetFor(classID);
  if (rel === -2) {
    // GameObject: int m_Component count, count * PPtr<Component> (12 bytes),
    // int m_Layer, then m_Name.
    if (bodyOffset + 4 > data.length) return null;
    const n = data.readInt32LE(bodyOffset);
    if (n < 0 || n > 4096) return null;
    rel = 4 + n * 12 + 4;
  }
  if (rel < 0) return null;
  const at = bodyOffset + rel;
  if (at + 4 > data.length) return null;
  const len = data.readInt32LE(at);
  if (len < 1 || len > 512 || at + 4 + len > data.length) return null;
  const s = data.toString('utf8', at + 4, at + 4 + len);
  return /^[\x20-\x7e]+$/.test(s) ? s : null;
}

// Resolve a MonoBehaviour's m_Script PPtr to the MonoScript's class name. m_FileID 0 means
// "this file"; anything else is a 1-based index into m_Externals, whose pathName is the
// sibling content archive's id. A MonoScript serializes m_Name first, and in IL2CPP player
// builds that is the class name.
function resolveScriptClass(archiveDir, sf, ptr, cache, a) {
  if (ptr.fileID === 0) return null; // in-file MonoScript: not seen in these archives
  const ext = sf.externals?.[ptr.fileID - 1];
  if (!ext) return null;
  const id = ext.pathName;
  if (!/^[0-9a-f]{32}$/.test(id)) return null;
  let table = cache.get(id);
  if (table === undefined) {
    table = null;
    try {
      const dir = readUnityFsDirectory(path.join(archiveDir, id));
      if (!dir.error && dir.nodes.length) {
        const node = dir.nodes[0];
        const pre = readUnityFsPrefix(path.join(archiveDir, id), dir, Math.max(a.deepCap, 4 * 1024 * 1024));
        const inner = readSerializedFile(pre.data.subarray(node.offset));
        if (!inner.error) {
          table = new Map();
          for (const o of inner.objects) {
            if (o.classID !== 115) continue; // MonoScript
            const at = node.offset + inner.dataOffset + o.byteStart;
            const n = readObjectName(pre.data, at, 115) ?? readMonoScriptName(pre.data, at);
            if (n) table.set(o.pathID, n);
          }
        }
      }
    } catch { table = null; }
    cache.set(id, table);
  }
  return table?.get(ptr.pathID) ?? null;
}

// MonoScript.m_Name is at offset 0 like any NamedObject; class 115 is not in NAME_AT_ZERO
// because it is resolved through this path instead.
function readMonoScriptName(data, at) {
  if (at + 4 > data.length) return null;
  const len = data.readInt32LE(at);
  if (len < 1 || len > 256 || at + 4 + len > data.length) return null;
  const s = data.toString('utf8', at + 4, at + 4 + len);
  return /^[\x20-\x7e]+$/.test(s) ? s : null;
}

// ---------------------------------------------------------------- main

function main() {
  const a = parseArgs(process.argv);
  const sa = path.join(a.game, 'VRising_Data', 'StreamingAssets');
  const archiveDir = path.join(sa, 'ContentArchives');
  const binPath = path.join(archiveDir, 'archive_dependencies.bin');
  const txtPath = path.join(archiveDir, 'archive_dependencies.txt');
  if (!existsSync(binPath)) {
    console.error(`catalog not found: ${binPath}\n(pass --game <VRising install root>)`);
    process.exit(1);
  }

  const t0 = Date.now();
  const cat = readCatalog(binPath);
  const buf = cat.buf; delete cat.buf;

  // --- archive files on disk
  let onDisk = new Set();
  try { onDisk = new Set(readdirSync(archiveDir).filter(f => /^[0-9a-f]{32}$/.test(f))); } catch { }
  for (const ar of cat.archives) {
    ar.path = `ContentArchives/${ar.id}`;                     // DefaultArchivePathFunc, literal 30914
    ar.present = onDisk.has(ar.id);
    if (ar.present) { try { ar.bytes = statSync(path.join(archiveDir, ar.id)).size; } catch { } }
  }
  for (const f of cat.files) {
    f.mountedName = `ns:/RTC/${f.id}`;                         // DefaultContentFileNameFunc, literal 30902
    f.archiveId = cat.archives[f.archiveIndex]?.id ?? null;
    f.dependencyCount = cat.dependencySets[f.dependencyIndex]?.length ?? 0;
  }

  // --- optional archive directory pass
  const archiveDirInfo = new Map();
  if (a.archives) {
    let n = 0, bad = 0;
    for (const ar of cat.archives) {
      if (!ar.present) continue;
      const info = readUnityFsDirectory(path.join(archiveDir, ar.id));
      if (info.error) { ar.unityfsError = info.error; bad++; continue; }
      archiveDirInfo.set(ar.id, info);
      ar.unityVersion = info.unityRevision;
      ar.blockCount = info.blocks.length;
      ar.uncompressedBytes = info.blocks.reduce((s, b) => s + b.uncompressedSize, 0);
      ar.innerFiles = info.nodes.map(nd => nd.path);
      ar.primaryInnerFile = info.nodes[0]?.path ?? null;
      ar.primaryInnerIsArchiveId = ar.primaryInnerFile === ar.id;
      n++;
    }
    console.error(`archives: read ${n} UnityFS directories (${bad} unreadable)`);
  }

  // --- optional deep pass: SerializedFile metadata for archives that hold catalog objects
  const typeByObject = new Map();
  if (a.deep) {
    const wanted = new Map(); // archiveId -> [{ objIndex, localId }]
    for (const o of cat.objects) {
      const f = cat.files[o.fileIndex];
      if (!f) continue;
      const ar = cat.archives[f.archiveIndex];
      if (!ar?.present) continue;
      if (!wanted.has(ar.id)) wanted.set(ar.id, []);
      wanted.get(ar.id).push(o);
    }
    let done = 0, named = 0, failed = 0, scripted = 0;
    const monoScriptCache = new Map();
    for (const [archiveId, objs] of wanted) {
      if (a.limitDeep && done >= a.limitDeep) break;
      const dir = archiveDirInfo.get(archiveId);
      if (!dir) { failed++; continue; }
      const node = dir.nodes[0];
      if (!node) { failed++; continue; }
      const file = path.join(archiveDir, archiveId);
      let prefix, sf;
      try {
        // Stage 1: enough for the SerializedFile header, which names the metadata size.
        prefix = readUnityFsPrefix(file, dir, Math.min(a.deepCap, 512 * 1024));
        sf = readSerializedFile(prefix.data.subarray(node.offset));
        // Stage 2: the object table lives in the metadata, i.e. below dataOffset. Grow the
        // window until the whole metadata is resident (this is what --deep-cap bounds).
        const needed = node.offset + (sf.dataOffset ?? 0) + 64;
        if (sf.error === 'short read' || (sf.dataOffset && prefix.data.length < needed)) {
          prefix = readUnityFsPrefix(file, dir, Math.max(a.deepCap, needed));
          sf = readSerializedFile(prefix.data.subarray(node.offset));
        }
      } catch (e) {
        for (const o of objs) typeByObject.set(o.index, { error: `unpack failed: ${e.message}` });
        failed++; done++; continue;
      }
      if (sf.error) {
        for (const o of objs) typeByObject.set(o.index, { error: sf.error });
        failed++; done++; continue;
      }
      const byPath = new Map(sf.objects.map(x => [x.pathID, x]));
      // Stage 3: grow the window far enough to reach the bodies of the objects we care about,
      // so m_Name can be read. Bounded by --name-cap.
      let needName = 0;
      for (const o of objs) {
        const e = byPath.get(o.localIdentifierInFile);
        if (!e || nameOffsetFor(e.classID) < 0) continue;
        needName = Math.max(needName, node.offset + sf.dataOffset + e.byteStart + Math.min(e.byteSize, 1024) + 8);
      }
      if (needName > prefix.data.length && needName <= a.nameCap) {
        try { prefix = readUnityFsPrefix(file, dir, needName); } catch { /* keep what we have */ }
      }
      for (const o of objs) {
        const e = byPath.get(o.localIdentifierInFile);
        if (!e) { typeByObject.set(o.index, { error: 'pathID not in SerializedFile' }); continue; }
        const abs = node.offset + sf.dataOffset + e.byteStart;
        const name = readObjectName(prefix.data, abs, e.classID);
        if (name) named++;
        const rec = {
          classID: e.classID,
          type: CLASS_NAMES[e.classID] ?? `Class<${e.classID}>`,
          byteSize: e.byteSize,
          name: name ?? undefined,
          bodyAvailable: abs < prefix.data.length,
        };
        if (e.classID === 114 && rec.bodyAvailable) {
          const ptr = readScriptPPtr(prefix.data, abs);
          if (ptr) {
            const cls = resolveScriptClass(archiveDir, sf, ptr, monoScriptCache, a);
            if (cls) { rec.scriptClass = cls; scripted++; }
            else rec.scriptRef = `${ptr.fileID}:${ptr.pathID}`;
          }
        }
        typeByObject.set(o.index, rec);
      }
      done++;
      if (done % 100 === 0) console.error(`deep: ${done}/${wanted.size} archives, ${named} names, ${scripted} script classes`);
    }
    console.error(`deep: ${done} archives parsed, ${failed} failed, ${named} names, ${scripted} script classes`);
  }

  // --- optional inventory pass: what is actually *inside* every archive.
  // The catalog itself only indexes 1151 entry points (see README); the real asset
  // inventory is the SerializedFile object table of each of the 4409 archives.
  let inventory = null;
  if (a.inventory) {
    inventory = { archives: [], byClass: {}, names: 0, objects: 0, failed: 0 };
    let n = 0;
    for (const ar of cat.archives) {
      if (!ar.present) continue;
      const dir = archiveDirInfo.get(ar.id);
      if (!dir || !dir.nodes.length) { inventory.failed++; continue; }
      const file = path.join(archiveDir, ar.id);
      const node = dir.nodes[0];
      let sf, pre;
      try {
        pre = readUnityFsPrefix(file, dir, Math.min(a.inventoryCap, 1024 * 1024));
        sf = readSerializedFile(pre.data.subarray(node.offset));
        const needed = node.offset + (sf.dataOffset ?? 0) + 64;
        if (sf.error === 'short read' || (sf.dataOffset && pre.data.length < needed)) {
          pre = readUnityFsPrefix(file, dir, Math.max(a.inventoryCap, needed));
          sf = readSerializedFile(pre.data.subarray(node.offset));
        } else if (pre.truncated && a.inventoryCap > pre.data.length) {
          // The first read stops at 1 MB; object bodies (and so their names) past it need the
          // full budget. Large prefab archives keep their root GameObject names there.
          pre = readUnityFsPrefix(file, dir, a.inventoryCap);
          sf = readSerializedFile(pre.data.subarray(node.offset));
        }
      } catch (e) { inventory.failed++; continue; }
      if (sf.error) { inventory.failed++; continue; }
      const hist = {}; const named = [];
      for (const o of sf.objects) {
        const t = CLASS_NAMES[o.classID] ?? `Class<${o.classID}>`;
        hist[t] = (hist[t] ?? 0) + 1;
        inventory.byClass[t] = (inventory.byClass[t] ?? 0) + 1;
        inventory.objects++;
        const at = node.offset + sf.dataOffset + o.byteStart;
        if (at + 4 <= pre.data.length) {
          const nm = readObjectName(pre.data, at, o.classID);
          if (nm) { named.push({ pathID: o.pathID, type: t, name: nm }); inventory.names++; }
        }
      }
      inventory.archives.push({
        archiveId: ar.id, objectCount: sf.objects.length, byClass: hist,
        namesResolved: named.length, named,
        bodiesTruncated: pre.truncated,
      });
      if (++n % 500 === 0) console.error(`inventory: ${n} archives, ${inventory.objects} objects, ${inventory.names} names`);
    }
    inventory.byClass = Object.fromEntries(Object.entries(inventory.byClass).sort((p, q) => q[1] - p[1]));
    console.error(`inventory: ${n} archives, ${inventory.objects} objects, ${inventory.names} names, ${inventory.failed} failed`);
  }

  for (const o of cat.objects) {
    const f = cat.files[o.fileIndex];
    o.fileId = f?.id ?? null;
    o.archiveIndex = f?.archiveIndex ?? -1;
    o.archiveId = cat.archives[o.archiveIndex]?.id ?? null;
    o.archivePath = cat.archives[o.archiveIndex]?.path ?? null;
    o.mountedName = f?.mountedName ?? null;
    const t = typeByObject.get(o.index);
    if (t) {
      o.type = t.type; o.classID = t.classID;
      if (t.name) o.name = t.name;
      if (t.scriptClass) o.scriptClass = t.scriptClass;
      if (t.scriptRef) o.scriptRef = t.scriptRef;
      if (t.error) o.typeError = t.error;
    }
  }

  // --- summary
  const countBy = (arr, fn) => {
    const m = {};
    for (const x of arr) { const k = fn(x); m[k] = (m[k] ?? 0) + 1; }
    return Object.fromEntries(Object.entries(m).sort((p, q) => q[1] - p[1]));
  };
  const objectsPerArchive = countBy(cat.objects, o => o.archiveId ?? '(none)');
  const summary = {
    counts: {
      archives: cat.archives.length,
      archivesPresentOnDisk: cat.archives.filter(x => x.present).length,
      files: cat.files.length,
      objects: cat.objects.length,
      scenes: cat.scenes.length,
      blobs: cat.blobs.length,
      dependencySets: cat.dependencySets.length,
      dependencyEdgesUnique: cat.dependencySets.reduce((s, d) => s + d.length, 0),
      dependencyEdgesExpanded: cat.files.reduce((s, f) => s + f.dependencyCount, 0),
    },
    objectsByGenerationType: countBy(cat.objects, o => o.generationTypeName),
    objectsByIdentifierType: countBy(cat.objects, o => String(o.identifierType)),
    objectsByUnityType: a.deep ? countBy(cat.objects, o => o.type ?? '(unresolved)') : null,
    objectsByScriptClass: a.deep ? countBy(cat.objects, o => o.scriptClass ?? '(none)') : null,
    objectsWithScriptClass: cat.objects.filter(o => o.scriptClass).length,
    objectsNamed: cat.objects.filter(o => o.name).length,
    distinctAssetGuids: new Set(cat.objects.map(o => o.assetGuid)).size,
    topArchivesByObjectCount: Object.entries(objectsPerArchive).slice(0, 20)
      .map(([id, n]) => ({ archiveId: id, objects: n })),
    filesPerArchive: {
      max: Math.max(...Object.values(countBy(cat.files, f => f.archiveId))),
      archivesWithFiles: Object.keys(countBy(cat.files, f => f.archiveId)).length,
    },
    blobBytesAttributed: cat.attributed,
    blobBytesTotal: cat.header.blobLength,
    unattributedBlobRegions: cat.gaps,
    archivesWithPrimaryInnerFileEqualToArchiveId: a.archives
      ? cat.archives.filter(x => x.primaryInnerIsArchiveId).length : null,
    archivesWithResSSidecar: a.archives
      ? cat.archives.filter(x => x.innerFiles?.some(n => n.endsWith('.resS'))).length : null,
  };

  // --- optional cross-check against the shipped text dump
  let verify = null;
  if (a.verify && existsSync(txtPath)) {
    const txt = readFileSync(txtPath, 'utf8');
    const txtArchives = new Set([...txt.matchAll(/^Archive: ([0-9a-f]{32})$/gm)].map(m => m[1]));
    const txtObjects = new Set([...txt.matchAll(/^\t\tObject: ([0-9a-f]{32}):(-?\d+)$/gm)].map(m => `${m[1]}:${m[2]}`));
    const ourArchives = new Set(cat.archives.map(x => x.id));
    const ourObjects = new Set(cat.objects.map(o => `${o.assetGuid}:${o.sceneObjectIdentifier0}`));
    const depTotal = (txt.match(/^\t\tDependency: /gm) ?? []).length;
    verify = {
      textFile: txtPath,
      archives: { text: txtArchives.size, decoded: ourArchives.size, missing: [...txtArchives].filter(x => !ourArchives.has(x)).length },
      objects: { text: txtObjects.size, decoded: ourObjects.size, missing: [...txtObjects].filter(x => !ourObjects.has(x)).length },
      dependencyEdges: { text: depTotal, decoded: summary.counts.dependencyEdgesExpanded },
    };
    verify.pass = verify.archives.missing === 0 && verify.objects.missing === 0
      && verify.dependencyEdges.text === verify.dependencyEdges.decoded;
  }

  const out = {
    tool: 'index-catalog.mjs',
    generated: new Date().toISOString(),
    source: {
      game: a.game,
      catalog: binPath,
      catalogSha256: createHash('sha256').update(buf).digest('hex'),
      catalogBytes: buf.length,
      catalogMTime: statSync(binPath).mtime.toISOString(),
      textDump: existsSync(txtPath) ? txtPath : null,
      archiveDirectory: archiveDir,
    },
    blobHeader: cat.header,
    blobRoots: cat.roots,
    blobRegions: cat.regions,
    passes: { archives: a.archives, deep: a.deep, inventory: a.inventory, deepCapBytes: a.deep ? a.deepCap : null, inventoryCapBytes: a.inventory ? a.inventoryCap : null },
    summary,
    verify,
    archives: cat.archives,
    files: cat.files,
    dependencySets: cat.dependencySets,
    objects: cat.objects,
    scenes: cat.scenes,
    blobs: cat.blobs,
  };

  const outFile = path.join(a.out, 'catalog-index.json');
  writeFileSync(outFile, a.pretty ? JSON.stringify(out, null, 2) : JSON.stringify(out));
  if (inventory) {
    inventory.generated = out.generated;
    inventory.source = {
      catalogSha256: out.source.catalogSha256,
      catalogBytes: out.source.catalogBytes,
      catalogMTime: out.source.catalogMTime,
      game: out.source.game,
    };
    writeFileSync(path.join(a.out, 'catalog-inventory.json'), JSON.stringify(inventory));
    summary.inventory = {
      archivesParsed: inventory.archives.length, archivesFailed: inventory.failed,
      objects: inventory.objects, namesResolved: inventory.names, byClass: inventory.byClass,
      generated: out.generated,
      catalogSha256: out.source.catalogSha256,
    };
  }
  const sumFile = path.join(a.out, 'catalog-summary.json');
  writeFileSync(sumFile, JSON.stringify({
    generated: out.generated, source: out.source, blobHeader: out.blobHeader,
    blobRoots: out.blobRoots, blobRegions: out.blobRegions, passes: out.passes,
    summary, verify,
  }, null, 2));

  console.log(`catalog     ${binPath}`);
  console.log(`            ${buf.length} bytes, blob ${cat.header.blobLength}, hash ${cat.header.blobHash}`);
  console.log(`archives    ${summary.counts.archives} (${summary.counts.archivesPresentOnDisk} on disk)`);
  console.log(`files       ${summary.counts.files}`);
  console.log(`objects     ${summary.counts.objects} across ${summary.distinctAssetGuids} asset guids`);
  console.log(`scenes      ${summary.counts.scenes}   blobs ${summary.counts.blobs}`);
  console.log(`dep sets    ${summary.counts.dependencySets} (${summary.counts.dependencyEdgesUnique} unique edges, ${summary.counts.dependencyEdgesExpanded} expanded)`);
  console.log(`gen types   ${JSON.stringify(summary.objectsByGenerationType)}`);
  console.log(`id types    ${JSON.stringify(summary.objectsByIdentifierType)}`);
  if (summary.objectsByUnityType) console.log(`unity types ${JSON.stringify(summary.objectsByUnityType)}`);
  if (summary.objectsByScriptClass) console.log(`script cls  ${JSON.stringify(summary.objectsByScriptClass)}`);
  console.log(`named       ${summary.objectsNamed}`);
  if (cat.gaps.length) console.log(`gaps        ${JSON.stringify(cat.gaps)}`);
  if (verify) console.log(`verify      ${verify.pass ? 'PASS' : 'FAIL'} ${JSON.stringify(verify)}`);
  console.log(`wrote       ${outFile}`);
  console.log(`wrote       ${sumFile}`);
  console.log(`elapsed     ${Date.now() - t0} ms`);
}

main();
