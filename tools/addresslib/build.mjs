#!/usr/bin/env node
// Build a per-game-build address library from the method-address listing in the game's metadata.
//
//   node tools/addresslib/build.mjs
//   node tools/addresslib/build.mjs --side client
//   node tools/addresslib/build.mjs --side server --all
//   node tools/addresslib/build.mjs --verify
//   node tools/addresslib/build.mjs --side client --verify
//
// The listing's Address values are relative addresses (the loaded image base is added at run time).
// If a value is >= --image-base it is treated as a VA and the base is subtracted.
// Ids are the metadata method Name (namespace.Type$$Method, generics kept); overloads that
// share a Name in the FULL listing get "#TypeSignature", then "#2", "#3" if that still
// collides. Assignment runs on every ScriptMethod, then the wanted list (or --all)
// selects which ids to emit, so expanding wanted later does not rename a previously
// unique name. Build id is "{fileSize}-{peTimeDateStamp hex}-{peCheckSum hex}-{first
// 16 hex chars of SHA-256 of the first 64 KiB}". Stamp and checksum are 0 when the
// bytes are not a PE. Must stay in lockstep with AddressId.ComputeBuildId.
//
// Reads only: the exported metadata under PROGENITOR_METADATA_DIR (default
// <tools>\interop-dump; one folder per side, the listing is the .json file whose top-level key is
// ScriptMethod) and GameAssembly copies under <tools>\bin, where <tools> = <workspace>\_tools
// (tools/common/workspace.mjs; override with PROGENITOR_WORKSPACE). Never writes outside
// tools/addresslib/.

import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import readline from "node:readline";
import { fileURLToPath } from "node:url";
import { workspacePath } from "../common/workspace.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..", "..");
// <workspace>/_tools; falls back to the repository's sibling folder when no workspace resolves.
const toolsRoot = workspacePath("_tools") ?? path.resolve(repoRoot, "..", "_tools");
// The exported metadata: PROGENITOR_METADATA_DIR, else <tools>/interop-dump. One folder per side.
const metadataRoot = process.env.PROGENITOR_METADATA_DIR || path.join(toolsRoot, "interop-dump");
const DEFAULT_IMAGE_BASE = 0x180000000;
const SHA_PREFIX_CHARS = 16;
const BUILD_ID_PREFIX_BYTES = 65536;
const SIDES = ["client", "server"];

function printHelp() {
  const text = `Usage: node tools/addresslib/build.mjs [options]

  --side client|server   One side. Default: both.
  --script <path>        Method-address listing (default: the .json in <metadata>/<side> that
                         starts with ScriptMethod; <metadata> = PROGENITOR_METADATA_DIR or
                         _tools/interop-dump)
  --dll <path>           GameAssembly.dll used for the build id (default: _tools/bin/<side>/GameAssembly.dll)
  --wanted <path>        Pattern file (default: tools/addresslib/wanted-<side>.txt)
  --out <path>           Output JSON (default: tools/addresslib/<side>-<build>.json)
  --image-base <hex|dec> Listing image base (default: 0x180000000)
  --build <id>           Skip hashing GameAssembly.dll and use this build id
  --all                  Write every ScriptMethod, ignoring the wanted file
  --verify               Compare each wanted name's relative address in the listing with the
                         current table and print OK / DRIFT / MISSING / EXTRA. Does
                         not write. Exit 1 when any wanted id drifted or is missing.
  --help                 This text
`;
  process.stdout.write(text);
}

function parseArgs(argv) {
  const args = {
    sides: null,
    script: null,
    dll: null,
    wanted: null,
    out: null,
    imageBase: DEFAULT_IMAGE_BASE,
    build: null,
    all: false,
    verify: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      const v = argv[++i];
      if (v == null || v.startsWith("--")) throw new Error(`missing value for ${a}`);
      return v;
    };
    if (a === "--help" || a === "-h") {
      printHelp();
      process.exit(0);
    } else if (a === "--side") {
      const side = next().toLowerCase();
      if (!SIDES.includes(side)) throw new Error(`--side must be client or server, got ${side}`);
      args.sides = [side];
    } else if (a === "--script") {
      args.script = next();
    } else if (a === "--dll") {
      args.dll = next();
    } else if (a === "--wanted") {
      args.wanted = next();
    } else if (a === "--out") {
      args.out = next();
    } else if (a === "--image-base") {
      args.imageBase = parseImageBase(next());
    } else if (a === "--build") {
      args.build = next();
    } else if (a === "--all") {
      args.all = true;
    } else if (a === "--verify") {
      args.verify = true;
    } else {
      throw new Error(`unknown argument: ${a}`);
    }
  }
  if (!args.sides) args.sides = SIDES.slice();
  if (args.sides.length > 1 && (args.script || args.dll || args.wanted || args.out || args.build)) {
    throw new Error("--script / --dll / --wanted / --out / --build require --side");
  }
  return args;
}

function parseImageBase(text) {
  const n = text.startsWith("0x") || text.startsWith("0X") ? Number.parseInt(text, 16) : Number(text);
  if (!Number.isFinite(n) || n < 0) throw new Error(`bad --image-base: ${text}`);
  return n;
}

function loadWanted(filePath) {
  const text = fs.readFileSync(filePath, "utf8");
  const patterns = [];
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    patterns.push(line);
  }
  return patterns;
}

function nameMatches(name, patterns) {
  for (const p of patterns) {
    if (name === p || name.includes(p)) return true;
  }
  return false;
}

export function toImageOffset(address, imageBase) {
  const addr = Number(address);
  if (!Number.isFinite(addr)) return 0;
  if (imageBase > 0 && addr >= imageBase) return addr - imageBase;
  return addr;
}

/**
 * Stable id from a metadata ScriptMethod.Name. Unique names stay as-is
 * (generics and nested types already sit in the Name). Shared names append
 * "#" + TypeSignature; a leftover collision appends "#2", "#3", ...
 *
 * Pass the FULL dump as `rows`. Filter to the wanted subset afterwards
 * (`assignWanted`). Counting uniqueness on a filtered list would give a
 * bare id to a method that has overloads outside the wanted set.
 */
export function assignIds(rows) {
  // Lockstep with AddressId.Assign: output[i] is rows[i]. Grouping by Name
  // must not reorder; assignWanted looks up assigned[i] by original index.
  const byName = new Map();
  for (let i = 0; i < rows.length; i++) {
    const name = rows[i].name;
    if (!byName.has(name)) byName.set(name, []);
    byName.get(name).push(i);
  }
  const used = new Set();
  const out = new Array(rows.length);
  for (const [name, group] of byName) {
    const disambiguate = group.length > 1;
    for (const index of group) {
      const row = rows[index];
      let id = disambiguate ? `${name}#${row.typeSignature || ""}` : name;
      if (used.has(id)) {
        let n = 2;
        while (used.has(`${id}#${n}`)) n++;
        id = `${id}#${n}`;
      }
      used.add(id);
      out[index] = { id, imageOffset: row.imageOffset, name: row.name };
    }
  }
  return out;
}

/**
 * Assign against the full dump, then keep rows whose (name, typeSignature, imageOffset)
 * is in `wanted`. Overload suffixes come from the full Name occupancy, not from
 * the position in the wanted list.
 */
export function assignWanted(allRows, wanted) {
  if (!wanted || wanted.length === 0) return [];
  const assigned = assignIds(allRows || []);
  const byKey = new Map();
  for (let i = 0; i < (allRows || []).length; i++) {
    const row = allRows[i];
    const key = `${row.name}\0${row.typeSignature || ""}\0${row.imageOffset}`;
    if (!byKey.has(key)) byKey.set(key, assigned[i].id);
  }
  const seen = new Set();
  const out = [];
  for (const row of wanted) {
    const key = `${row.name}\0${row.typeSignature || ""}\0${row.imageOffset}`;
    const id = byKey.get(key);
    if (!id || seen.has(id)) continue;
    seen.add(id);
    out.push({ id, imageOffset: row.imageOffset, name: row.name });
  }
  return out;
}

export function computeBuildId(fileSize, sha256, timeDateStamp = 0, checkSum = 0) {
  const hex = Buffer.isBuffer(sha256) || sha256 instanceof Uint8Array
    ? Buffer.from(sha256).toString("hex")
    : String(sha256);
  const prefix = hex.slice(0, SHA_PREFIX_CHARS).toLowerCase();
  const stamp = Number(timeDateStamp >>> 0).toString(16);
  const sum = Number(checkSum >>> 0).toString(16);
  return `${fileSize}-${stamp}-${sum}-${prefix}`;
}

/** PE COFF TimeDateStamp (PE+8) and optional-header CheckSum (optional+64). Lockstep with AddressId.TryReadPeIdentifiers. */
export function tryReadPeIdentifiers(prefix) {
  const buf = Buffer.isBuffer(prefix) ? prefix : Buffer.from(prefix);
  if (buf.length < 0x40) return { ok: false, timeDateStamp: 0, checkSum: 0 };
  if (buf[0] !== 0x4d || buf[1] !== 0x5a) return { ok: false, timeDateStamp: 0, checkSum: 0 };
  const pe = buf.readInt32LE(0x3c);
  if (pe < 0 || pe + 88 + 4 > buf.length) return { ok: false, timeDateStamp: 0, checkSum: 0 };
  if (buf[pe] !== 0x50 || buf[pe + 1] !== 0x45 || buf[pe + 2] !== 0 || buf[pe + 3] !== 0) {
    return { ok: false, timeDateStamp: 0, checkSum: 0 };
  }
  const timeDateStamp = buf.readUInt32LE(pe + 8);
  const optional = pe + 24;
  if (optional + 66 > buf.length) return { ok: false, timeDateStamp: 0, checkSum: 0 };
  const checkSum = buf.readUInt32LE(optional + 64);
  return { ok: true, timeDateStamp, checkSum };
}

export function computeBuildIdFromPrefix(prefix, fileSize) {
  const buf = Buffer.isBuffer(prefix) ? prefix : Buffer.from(prefix);
  const hash = crypto.createHash("sha256").update(buf).digest();
  const pe = tryReadPeIdentifiers(buf);
  return computeBuildId(fileSize, hash, pe.timeDateStamp, pe.checkSum);
}

function hashDll(dllPath) {
  const st = fs.statSync(dllPath);
  const n = Math.min(BUILD_ID_PREFIX_BYTES, st.size);
  const buf = Buffer.alloc(n);
  const fd = fs.openSync(dllPath, "r");
  try {
    let off = 0;
    while (off < n) {
      const got = fs.readSync(fd, buf, off, n - off, off);
      if (got <= 0) break;
      off += got;
    }
    return computeBuildIdFromPrefix(buf.subarray(0, off), st.size);
  } finally {
    fs.closeSync(fd);
  }
}

function parseJsonString(quoted) {
  try {
    return JSON.parse(quoted);
  } catch {
    return null;
  }
}

async function readScriptMethods(scriptPath) {
  const stream = fs.createReadStream(scriptPath, { encoding: "utf8", highWaterMark: 8 * 1024 * 1024 });
  const rl = readline.createInterface({ input: stream, crlfDelay: Infinity });
  let inMethods = false;
  let current = {};
  const rows = [];
  let scanned = 0;

  for await (const line of rl) {
    const t = line.trim();
    if (!inMethods) {
      if (t.startsWith('"ScriptMethod"')) inMethods = true;
      continue;
    }
    if (t.startsWith('"ScriptString"') || t.startsWith('"ScriptMetadata"') || t.startsWith('"ScriptMetadataMethod"') || t.startsWith('"Addresses"')) {
      break;
    }

    if (t.startsWith('"Address"')) {
      const m = t.match(/^"Address"\s*:\s*(\d+)/);
      if (m) current.Address = Number(m[1]);
      continue;
    }
    if (t.startsWith('"Name"')) {
      const m = t.match(/^"Name"\s*:\s*(".*")\s*,?\s*$/);
      if (m) current.Name = parseJsonString(m[1]);
      continue;
    }
    if (t.startsWith('"Signature"')) {
      const m = t.match(/^"Signature"\s*:\s*(".*")\s*,?\s*$/);
      if (m) current.Signature = parseJsonString(m[1]);
      continue;
    }
    if (t.startsWith('"TypeSignature"')) {
      const m = t.match(/^"TypeSignature"\s*:\s*(".*")\s*,?\s*$/);
      if (m) current.TypeSignature = parseJsonString(m[1]);
      continue;
    }
    if (t === "}," || t === "}") {
      const name = current.Name;
      const address = current.Address;
      if (typeof name === "string" && name.length > 0 && Number.isFinite(address)) {
        scanned++;
        rows.push({
          name,
          typeSignature: typeof current.TypeSignature === "string" ? current.TypeSignature : "",
          address,
        });
      }
      current = {};
    }
  }

  rl.close();
  stream.destroy();
  return { rows, scanned };
}

/**
 * The method-address listing in <metadata>/<side>: the .json file whose first key is
 * "ScriptMethod". Returns a path inside the side folder even when none is found, so the
 * not-found error names where it looked.
 */
function findMethodListing(side) {
  const dir = path.join(metadataRoot, side);
  const fallback = path.join(dir, "method-listing.json");
  if (!fs.existsSync(dir)) return fallback;
  for (const name of fs.readdirSync(dir)) {
    if (!name.toLowerCase().endsWith(".json")) continue;
    const full = path.join(dir, name);
    let head = "";
    try {
      const fd = fs.openSync(full, "r");
      try {
        const buf = Buffer.alloc(256);
        const n = fs.readSync(fd, buf, 0, buf.length, 0);
        head = buf.subarray(0, n).toString("utf8");
      } finally {
        fs.closeSync(fd);
      }
    } catch {
      continue;
    }
    if (/^\uFEFF?\s*\{\s*"ScriptMethod"/.test(head)) return full;
  }
  return fallback;
}

function defaultPaths(side) {
  return {
    script: findMethodListing(side),
    dll: path.join(toolsRoot, "bin", side, "GameAssembly.dll"),
    wanted: path.join(here, `wanted-${side}.txt`),
  };
}

async function buildSide(side, args) {
  const defaults = defaultPaths(side);
  const scriptPath = args.script ?? defaults.script;
  const dllPath = args.dll ?? defaults.dll;
  const wantedPath = args.wanted ?? defaults.wanted;
  const imageBase = args.imageBase;

  if (!fs.existsSync(scriptPath)) throw new Error(`method-address listing not found: ${scriptPath}`);
  const patterns = args.all ? [] : loadWanted(wantedPath);
  if (!args.all && patterns.length === 0) throw new Error(`wanted file is empty: ${wantedPath}`);

  const build = args.build ?? hashDll(dllPath);
  const { rows, scanned } = await readScriptMethods(scriptPath);

  const withImageOffset = rows.map((r) => ({
    name: r.name,
    typeSignature: r.typeSignature,
    imageOffset: toImageOffset(r.address, imageBase),
  }));
  const wanted = args.all ? withImageOffset : withImageOffset.filter((r) => nameMatches(r.name, patterns));
  const matched = wanted.length;
  const assigned = assignWanted(withImageOffset, wanted);
  assigned.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));

  const entries = {};
  for (const row of assigned) entries[row.id] = row.imageOffset;

  const table = {
    side,
    build,
    imageBase,
    generatedUtc: new Date().toISOString().replace(/\.\d{3}Z$/, "Z"),
    count: assigned.length,
    entries,
  };

  const outPath = args.out ?? path.join(here, `${side}-${build}.json`);
  if (args.verify) {
    return verifySide(side, build, outPath, assigned, scanned, matched, imageBase);
  }
  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  fs.writeFileSync(outPath, JSON.stringify(table, null, 2) + "\n", "utf8");
  const size = fs.statSync(outPath).size;
  return { side, build, outPath, size, count: table.count, scanned, matched, all: args.all };
}

function findExistingTable(side, build, preferred) {
  if (preferred && fs.existsSync(preferred)) return preferred;
  const exact = path.join(here, `${side}-${build}.json`);
  if (fs.existsSync(exact)) return exact;
  let files = [];
  try { files = fs.readdirSync(here); } catch { files = []; }
  const prefix = `${side}-`;
  const matches = files.filter((n) => n.startsWith(prefix) && n.endsWith(".json"));
  if (matches.length === 1) return path.join(here, matches[0]);
  if (matches.length > 1) {
    matches.sort();
    return path.join(here, matches[matches.length - 1]);
  }
  return preferred ?? exact;
}

function verifySide(side, build, preferredPath, assigned, scanned, matched, imageBase) {
  const tablePath = findExistingTable(side, build, preferredPath);
  const dump = {};
  for (const row of assigned) dump[row.id] = row.imageOffset;

  if (!fs.existsSync(tablePath)) {
    process.stdout.write(
      `${side}: MISSING table for build ${build} at ${tablePath}. Rebuild with: node tools/addresslib/build.mjs --side ${side}\n`,
    );
    return {
      side, build, outPath: tablePath, size: 0, count: assigned.length, scanned, matched,
      verify: true, ok: 0, drift: 0, missing: assigned.length, extra: 0, failed: true,
    };
  }

  let table;
  try {
    table = JSON.parse(fs.readFileSync(tablePath, "utf8"));
  } catch (e) {
    process.stdout.write(`${side}: table did not parse (${tablePath}): ${e.message}\n`);
    return {
      side, build, outPath: tablePath, size: 0, count: assigned.length, scanned, matched,
      verify: true, ok: 0, drift: 0, missing: assigned.length, extra: 0, failed: true,
    };
  }

  const tableBuild = table && table.build != null ? String(table.build) : "";
  const tableSide = table && table.side != null ? String(table.side) : "";
  const entries = table && table.entries && typeof table.entries === "object" ? table.entries : {};
  let ok = 0;
  let drift = 0;
  let missing = 0;
  let extra = 0;

  process.stdout.write(
    `${side}: verify dump-build=${build} table=${tablePath} table-build=${tableBuild || "(none)"} table-side=${tableSide || "(none)"} imageBase=0x${Number(imageBase).toString(16)}\n`,
  );
  if (tableSide && tableSide.toLowerCase() !== side) {
    process.stdout.write(`  DRIFT side table=${tableSide} expected=${side}\n`);
    drift++;
  }
  if (tableBuild && tableBuild !== build) {
    process.stdout.write(`  DRIFT build table=${tableBuild} dump=${build}\n`);
    drift++;
  }

  const ids = Object.keys(dump).sort();
  for (const id of ids) {
    const dumpImageOffset = dump[id];
    if (!Object.prototype.hasOwnProperty.call(entries, id)) {
      process.stdout.write(`  MISSING ${id}  dump=0x${Number(dumpImageOffset).toString(16)}\n`);
      missing++;
      continue;
    }
    const tableImageOffset = Number(entries[id]);
    if (tableImageOffset !== dumpImageOffset) {
      process.stdout.write(
        `  DRIFT   ${id}  table=0x${tableImageOffset.toString(16)} dump=0x${Number(dumpImageOffset).toString(16)}\n`,
      );
      drift++;
    } else {
      process.stdout.write(`  OK      ${id}  offset=0x${Number(dumpImageOffset).toString(16)}\n`);
      ok++;
    }
  }

  const extraIds = Object.keys(entries).filter((id) => !Object.prototype.hasOwnProperty.call(dump, id)).sort();
  for (const id of extraIds) {
    process.stdout.write(`  EXTRA   ${id}  table=0x${Number(entries[id]).toString(16)}\n`);
    extra++;
  }

  const failed = drift > 0 || missing > 0;
  process.stdout.write(
    `${side}: ok=${ok} drift=${drift} missing=${missing} extra=${extra}${failed ? " FAILED" : " passed"}\n`,
  );
  return {
    side, build, outPath: tablePath, size: fs.statSync(tablePath).size,
    count: assigned.length, scanned, matched, verify: true, ok, drift, missing, extra, failed,
  };
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const results = [];
  let failed = false;
  for (const side of args.sides) {
    const r = await buildSide(side, args);
    results.push(r);
    if (r.verify) {
      if (r.failed) failed = true;
      continue;
    }
    process.stdout.write(
      `${r.side}: build=${r.build} count=${r.count} scanned=${r.scanned} matched=${r.matched} bytes=${r.size} -> ${r.outPath}\n`,
    );
  }
  if (failed) process.exitCode = 1;
  return results;
}

const invokedAsCli = process.argv[1] && path.basename(process.argv[1]).toLowerCase() === "build.mjs";
if (invokedAsCli) {
  main().catch((e) => {
    process.stderr.write(String(e && e.stack ? e.stack : e) + "\n");
    process.exit(1);
  });
}
