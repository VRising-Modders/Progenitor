// Knowledge-base notes: parse, check, search and index knowledge/notes/*.md.
//
// Zero dependencies so the CLI (kb.mjs), the MCP tools (tools/mcp/kb-tools.mjs) and the
// tests share one implementation. Nothing here contacts the game or the network.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const REPO = path.resolve(here, '..', '..');
export const KB_DIR = path.join(REPO, 'knowledge');
export const NOTES_DIR = path.join(KB_DIR, 'notes');
export const INDEX_FILE = path.join(KB_DIR, 'INDEX.md');

// The bar is "observed": verified-live (seen in the game), failed (a route tried in the game that
// did not work), compile-verified (build or tooling gotchas only) and reported (an outside
// contributor's observation a maintainer has not reproduced yet). Inference stays in research.
export const STATUSES = Object.freeze(['verified-live', 'failed', 'compile-verified', 'reported']);
export const SIDES = Object.freeze(['client', 'server', 'both', 'offline']);
export const REQUIRED_FIELDS = Object.freeze(['title', 'game', 'game_build', 'progenitor', 'side', 'area', 'tags', 'route', 'status', 'date', 'evidence']);
export const REQUIRED_SECTIONS = Object.freeze(['Goal', 'Route', 'What the engine did', 'Verification', 'Gotchas']);
export const MAX_LINES = 150;
const SLUG = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

// Text that suggests pasted native output, metadata lines or binary payloads. Short factual
// references (a type or method name) are fine; listings are not. The address marker is assembled
// at run time so this file names no listing format literally.
const ADDR = 'R' + 'VA';
const DUMP_MARKERS = [
  { re: /\bFUN_[0-9a-f]{6,}\b/i, why: 'analysis function label' },
  { re: /\b(?:DAT|PTR|LAB)_[0-9a-f]{6,}\b/i, why: 'analysis data label' },
  { re: /\bundefined[1-8]\b/, why: 'analysis placeholder type' },
  { re: new RegExp(`// ${ADDR}: 0x[0-9a-f]+ Offset: 0x`, 'i'), why: 'metadata method line' },
  { re: new RegExp(`\\[Address\\(${ADDR} = "0x`, 'i'), why: 'metadata attribute line' },
  { re: /[A-Za-z0-9+/]{200,}={0,2}/, why: 'long base64/binary-looking payload' },
];
const GAME_FILE_LINK = /\.(?:dll|exe|bundle|assets|resS|save(?:\.gz)?|pdb|bin)(?:[#?)]|$)/i;

export class KbError extends Error {}

// ---------------------------------------------------------------------------
// Parsing

export function parseNote(text, file = '<note>') {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  if (lines[0] !== '---') throw new KbError(`${file}: missing front matter (first line must be ---)`);
  const end = lines.indexOf('---', 1);
  if (end < 0) throw new KbError(`${file}: front matter is not closed with ---`);
  const meta = {};
  for (let i = 1; i < end; i++) {
    const line = lines[i];
    if (!line.trim() || line.trimStart().startsWith('#')) continue;
    const m = /^([a-z_]+):\s*(.*)$/.exec(line);
    if (!m) throw new KbError(`${file}:${i + 1}: front matter line is not "key: value"`);
    if (m[1] in meta) throw new KbError(`${file}:${i + 1}: front matter key "${m[1]}" appears twice`);
    meta[m[1]] = m[2].trim();
  }
  meta.tags = splitList(meta.tags);
  meta.evidence = splitList(meta.evidence);
  const body = lines.slice(end + 1).join('\n');
  return { meta, body, lineCount: lines.length, bodyStartLine: end + 2, sections: sectionsOf(body) };
}

function splitList(v) {
  if (v === undefined) return [];
  if (Array.isArray(v)) return v;
  return v.replace(/^\[|\]$/g, '').split(',').map(s => s.trim()).filter(Boolean);
}

function sectionsOf(body) {
  const out = new Map();
  let cur = null;
  let fence = false;
  for (const line of body.split('\n')) {
    if (/^\s*```/.test(line)) fence = !fence;
    const h = !fence && /^## (.+?)\s*$/.exec(line);
    if (h) { cur = h[1]; out.set(cur, []); continue; }
    if (cur) out.get(cur).push(line);
  }
  return out;
}

export function gotchasOf(note) {
  const lines = note.sections.get('Gotchas') ?? [];
  const items = [];
  let cur = null;
  for (const line of lines) {
    const h = /^### (.+?)\s*$/.exec(line);
    if (h) { cur = { name: h[1], symptom: '', cause: '', fix: '' }; items.push(cur); continue; }
    const f = /^- \*\*(Symptom|Cause|Fix):\*\*\s*(.*)$/.exec(line);
    if (f && cur) cur[f[1].toLowerCase()] = f[2].trim();
  }
  return items;
}

// ---------------------------------------------------------------------------
// Loading

export function noteFiles(dir = NOTES_DIR) {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir).filter(f => f.endsWith('.md')).sort().map(f => path.join(dir, f));
}

// Unparseable notes are skipped so one half-written file cannot take search down;
// checkNote reports them.
export function loadNotes(dir = NOTES_DIR) {
  const out = [];
  for (const file of noteFiles(dir)) {
    const text = fs.readFileSync(file, 'utf8');
    let note;
    try { note = parseNote(text, rel(file)); } catch (e) { if (e instanceof KbError) continue; throw e; }
    out.push({ ...note, file, slug: path.basename(file, '.md'), text });
  }
  return out;
}

export function rel(file) {
  return path.relative(REPO, file).split(path.sep).join('/');
}

// ---------------------------------------------------------------------------
// Checking

// Returns { errors: string[], warnings: string[] } for one note's text. A relative link must stay
// inside the repository: evidence outside it is cited in words, never linked.
export function checkNote(text, file, opts = {}) {
  const errors = [];
  const warnings = [];
  const where = rel(file);
  let note;
  try { note = parseNote(text, where); } catch (e) { return { errors: [e.message], warnings }; }
  const { meta } = note;
  const slug = path.basename(file, '.md');

  if (!SLUG.test(slug)) errors.push(`${where}: file name must be a lowercase-hyphen slug`);
  for (const k of REQUIRED_FIELDS) {
    const v = meta[k];
    if (v === undefined || (Array.isArray(v) ? v.length === 0 : v === '')) errors.push(`${where}: front matter "${k}" is required`);
  }
  if (meta.status && !STATUSES.includes(meta.status)) errors.push(`${where}: status "${meta.status}" is not one of ${STATUSES.join(', ')}`);
  if (meta.date && !DATE.test(meta.date)) errors.push(`${where}: date must be YYYY-MM-DD`);
  if (meta.side && !SIDES.includes(meta.side)) errors.push(`${where}: side "${meta.side}" is not one of ${SIDES.join(', ')}`);
  if (meta.game_build && !/\d/.test(meta.game_build))
    errors.push(`${where}: game_build must name the exact build, for example "Steam 23521359"`);
  if (meta.progenitor && !/\b[0-9a-f]{7,40}\b|\bPR \d+/i.test(meta.progenitor))
    errors.push(`${where}: progenitor must name a commit (7+ hex digits) or "PR <n>"`);
  if (meta.status === 'compile-verified' && meta.side !== 'offline')
    warnings.push(`${where}: compile-verified is for build or tooling gotchas (side: offline); game behavior needs verified-live`);
  if (meta.status === 'reported') warnings.push(`${where}: reported, not yet reproduced by a maintainer`);

  for (const s of REQUIRED_SECTIONS) {
    const lines = note.sections.get(s);
    if (!lines) errors.push(`${where}: missing section "## ${s}"`);
    else if (!lines.some(l => l.trim() && !l.trim().startsWith('<!--'))) errors.push(`${where}: section "## ${s}" is empty`);
  }
  const order = [...note.sections.keys()].filter(s => REQUIRED_SECTIONS.includes(s));
  if (order.length === REQUIRED_SECTIONS.length && order.some((s, i) => s !== REQUIRED_SECTIONS[i]))
    errors.push(`${where}: sections must be in the order ${REQUIRED_SECTIONS.join(', ')}`);
  const gotchas = gotchasOf(note);
  if (note.sections.has('Gotchas')) {
    if (gotchas.length === 0) errors.push(`${where}: Gotchas needs at least one "### <name>" entry with Symptom, Cause and Fix`);
    for (const g of gotchas)
      for (const k of ['symptom', 'cause', 'fix'])
        if (!g[k]) errors.push(`${where}: gotcha "${g.name}" is missing "- **${k[0].toUpperCase() + k.slice(1)}:**"`);
  }

  if (note.lineCount > MAX_LINES) errors.push(`${where}: ${note.lineCount} lines; keep a note under ${MAX_LINES} and leave the detail in the research notes`);

  const lines = text.replace(/\r\n/g, '\n').split('\n');
  lines.forEach((line, i) => {
    for (const { re, why } of DUMP_MARKERS)
      if (re.test(line)) errors.push(`${where}:${i + 1}: ${why}; notes must not contain native code, dumps or binary payloads`);
  });

  for (const { target, line } of linksOf(lines)) {
    if (/^[a-z]+:/i.test(target) || target.startsWith('#')) continue;
    let clean;
    try { clean = decodeURI(target.split('#')[0]); } catch { errors.push(`${where}:${line}: malformed link ${target}`); continue; }
    if (GAME_FILE_LINK.test(clean)) { errors.push(`${where}:${line}: links a game/binary file (${clean}); notes never carry game files`); continue; }
    const abs = path.resolve(path.dirname(file), clean);
    const repoRoot = opts.repoRoot ?? REPO;
    const inRepo = abs.toLowerCase() === path.resolve(repoRoot).toLowerCase()
      || abs.toLowerCase().startsWith(path.resolve(repoRoot).toLowerCase() + path.sep);
    if (!inRepo) { errors.push(`${where}:${line}: links outside the repository (${clean}); cite outside evidence in words`); continue; }
    if (!fs.existsSync(abs)) errors.push(`${where}:${line}: broken link ${clean}`);
  }
  return { errors, warnings, note };
}

function linksOf(lines) {
  const out = [];
  let fence = false;
  lines.forEach((l, i) => {
    if (/^\s*```/.test(l)) fence = !fence;
    if (fence) return;
    for (const m of l.matchAll(/\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g)) out.push({ target: m[1], line: i + 1 });
  });
  return out;
}

export function checkAll(opts = {}) {
  const errors = [];
  const warnings = [];
  const dir = opts.notesDir ?? NOTES_DIR;
  if (fs.existsSync(dir))
    for (const f of fs.readdirSync(dir))
      if (!f.endsWith('.md')) errors.push(`${rel(path.join(dir, f))}: only .md notes belong in knowledge/notes`);
  const files = opts.files?.length ? opts.files.map(f => path.resolve(f)) : noteFiles(dir);
  const titles = new Map();
  for (const file of files) {
    const r = checkNote(fs.readFileSync(file, 'utf8'), file, opts);
    errors.push(...r.errors);
    warnings.push(...r.warnings);
    const t = r.note?.meta.title?.toLowerCase();
    if (t) { if (titles.has(t)) errors.push(`${rel(file)}: duplicate title of ${titles.get(t)}`); else titles.set(t, rel(file)); }
  }
  if (!opts.files?.length && opts.indexFile !== null) {
    const indexFile = opts.indexFile ?? INDEX_FILE;
    const want = renderIndex(loadNotes(dir), path.dirname(indexFile));
    const have = fs.existsSync(indexFile) ? fs.readFileSync(indexFile, 'utf8').replace(/\r\n/g, '\n') : '';
    if (have !== want) errors.push(`${rel(indexFile)}: out of date; run "node tools/kb/kb.mjs index"`);
  }
  return { errors, warnings, count: files.length };
}

// ---------------------------------------------------------------------------
// Index

export function renderIndex(notes, indexDir = KB_DIR) {
  const byArea = new Map();
  for (const n of notes) {
    const a = n.meta.area || 'other';
    if (!byArea.has(a)) byArea.set(a, []);
    byArea.get(a).push(n);
  }
  const out = [
    '# Knowledge index',
    '',
    'Generated by `node tools/kb/kb.mjs index`; do not edit by hand. Format and rules: [README.md](README.md).',
    '',
  ];
  if (notes.length === 0) out.push('No notes yet.', '');
  for (const area of [...byArea.keys()].sort()) {
    out.push(`## ${area}`, '', '| Note | Status | Side | Game build | Tags |', '|---|---|---|---|---|');
    for (const n of byArea.get(area).sort((a, b) => String(a.meta.title ?? '').localeCompare(String(b.meta.title ?? '')) || a.slug.localeCompare(b.slug))) {
      const link = path.relative(indexDir, n.file).split(path.sep).join('/');
      out.push(`| [${esc(n.meta.title)}](${link}) | ${n.meta.status} | ${esc(n.meta.side)} | ${esc(n.meta.game_build)} | ${n.meta.tags.map(esc).join(', ')} |`);
    }
    out.push('');
  }
  return out.join('\n');
}

const esc = s => String(s ?? '').replace(/\|/g, '\\|');

// ---------------------------------------------------------------------------
// Search

export function tokenize(s) {
  return String(s).toLowerCase().split(/[^a-z0-9_.]+/).map(t => t.replace(/^\.+|\.+$/g, '')).filter(t => t.length > 1);
}

const STOP_WORDS = new Set('a an and are as at be but by can do does for from how i in is it my of on or so that the this to was what when where which why with you'.split(' '));

function stem(t) {
  if (t.length > 4) t = t.replace(/(?:ing|ed|es|s)$/, '');
  return t.length > 3 ? t.replace(/e$/, '') : t;
}

// Ranks notes against a free-text query (prefix match on stems; title/tags/route hits
// outrank body hits). Notes matching every term come first; when none does, notes
// matching some terms are returned with partial: true and the terms they missed.
export function searchNotes(notes, query, { limit = 10 } = {}) {
  const words = tokenize(query);
  const kept = words.filter(w => !STOP_WORDS.has(w));
  const terms = [...new Set((kept.length ? kept : words).map(stem))];
  if (terms.length === 0) return [];
  const results = [];
  const partial = [];
  for (const n of notes) {
    const fields = {
      title: tokenize(n.meta.title).map(stem),
      tags: n.meta.tags.flatMap(tokenize).map(stem),
      meta: tokenize(`${n.meta.area} ${n.meta.route} ${n.meta.status} ${n.slug}`).map(stem),
      body: tokenize(n.body).map(stem),
    };
    const weights = { title: 6, tags: 5, meta: 3, body: 1 };
    let score = 0;
    const missed = [];
    for (const term of terms) {
      let termScore = 0;
      for (const [k, words] of Object.entries(fields)) {
        const hits = words.filter(w => w.startsWith(term)).length;
        termScore += Math.min(hits, 5) * weights[k];
      }
      if (termScore === 0) missed.push(term);
      score += termScore;
    }
    if (missed.length === terms.length) continue;
    (missed.length ? partial : results).push({
      slug: n.slug,
      path: rel(n.file),
      title: n.meta.title,
      status: n.meta.status,
      side: n.meta.side,
      game_build: n.meta.game_build,
      score,
      snippet: snippet(n, terms),
      gotchas: gotchasOf(n).map(g => g.name),
      ...(missed.length ? { partial: true, missed, matched: terms.length - missed.length } : {}),
    });
  }
  if (results.length)
    return results.sort((a, b) => b.score - a.score || a.slug.localeCompare(b.slug)).slice(0, limit);
  return partial.sort((a, b) => b.matched - a.matched || b.score - a.score || a.slug.localeCompare(b.slug)).slice(0, limit);
}

function snippet(n, terms) {
  let best = { line: '', hits: 0 };
  for (const line of n.body.split('\n')) {
    if (!line.trim() || line.startsWith('#')) continue;
    const words = tokenize(line).map(stem);
    const hits = terms.filter(t => words.some(w => w.startsWith(t))).length;
    if (hits > best.hits) best = { line, hits };
  }
  const s = best.line.trim().replace(/\s+/g, ' ');
  return s.length > 200 ? s.slice(0, 197) + '...' : s;
}

// ---------------------------------------------------------------------------
// New note

export function readSteamBuild(gameDir = process.env.PROGENITOR_GAME_DIR ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRising') {
  try {
    const manifest = path.resolve(gameDir, '..', '..', 'appmanifest_1604030.acf');
    const m = /"buildid"\s+"(\d+)"/.exec(fs.readFileSync(manifest, 'utf8'));
    return m ? `Steam ${m[1]}` : null;
  } catch { return null; }
}

export function renderTemplate({ title, area = 'other', tags = [], gameBuild, progenitor, date }) {
  return `---
title: ${title}
game: V Rising
game_build: ${gameBuild || '<Steam build id>'}
progenitor: ${progenitor || '<commit>'}
side: <client|server|both|offline>
area: ${area}
tags: ${tags.join(', ') || 'todo'}
route: <one line: the API or native path used>
status: <verified-live|failed|compile-verified|reported>
date: ${date}
evidence: <capture, read-back, log or issue link>
---

## Goal

<!-- What the mod or feature had to do, in one or two sentences. -->

## Route

<!-- The route taken and why; name the rejected alternatives in one line each. -->

## What the engine did

<!-- Observed or source-checked engine behavior that the next agent needs. Label which. -->

## Verification

<!-- Exactly what was checked: command, build, side, date, evidence link. Use the labels in docs/documentation.md. -->

## Gotchas

### <short name>

- **Symptom:**
- **Cause:**
- **Fix:**
`;
}

export function validSlug(s) { return SLUG.test(s); }
