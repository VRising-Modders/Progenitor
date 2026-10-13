#!/usr/bin/env node
// Knowledge-base CLI. See knowledge/README.md.
//
//   node tools/kb/kb.mjs search <words...> [--limit N] [--json]
//   node tools/kb/kb.mjs new <slug> --title "..." [--area A] [--tags a,b]
//   node tools/kb/kb.mjs check [note.md...]
//   node tools/kb/kb.mjs index

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import {
  INDEX_FILE, NOTES_DIR, REPO, checkAll, loadNotes, readSteamBuild, rel, renderIndex,
  renderTemplate, searchNotes, validSlug,
} from './lib.mjs';

// Flags that take no value; every other --flag takes the next argument.
const BOOLEAN_FLAGS = new Set(['json']);

const [cmd, ...rest] = process.argv.slice(2);
const { positional, flags } = parseArgs(rest);

const commands = { search, new: newNote, check, index };
if (!commands[cmd]) {
  console.error('usage: kb.mjs search|new|check|index ...  (see knowledge/README.md)');
  process.exit(2);
}
process.exitCode = commands[cmd]() ?? 0;

function parseArgs(args) {
  const positional = [];
  const flags = {};
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (!a.startsWith('--')) { positional.push(a); continue; }
    const key = a.slice(2);
    const next = args[i + 1];
    if (BOOLEAN_FLAGS.has(key) || next === undefined || next.startsWith('--')) flags[key] = true;
    else { flags[key] = next; i++; }
  }
  return { positional, flags };
}

function search() {
  const query = positional.join(' ');
  if (!query.trim()) { console.error('search: give some words'); return 2; }
  const limit = Number(flags.limit ?? 10);
  if (!Number.isInteger(limit) || limit < 1) { console.error('search: --limit must be a positive whole number'); return 2; }
  const hits = searchNotes(loadNotes(), query, { limit });
  if (flags.json) { console.log(JSON.stringify(hits, null, 2)); return 0; }
  if (hits.length === 0) { console.log(`no notes match "${query}"`); return 0; }
  if (hits[0].partial) console.log('no note matches every word; closest partial matches:');
  for (const h of hits) {
    console.log(`${h.path}  [${h.status}, ${h.side}, ${h.game_build}]`);
    console.log(`  ${h.title}`);
    if (h.partial) console.log(`  (missed: ${h.missed.join(', ')})`);
    if (h.gotchas.length) console.log(`  gotchas: ${h.gotchas.join('; ')}`);
    if (h.snippet) console.log(`  > ${h.snippet}`);
  }
  return 0;
}

function newNote() {
  const slug = positional[0];
  if (!slug || !validSlug(slug)) { console.error('new: give a lowercase-hyphen slug, e.g. robe-hood-winding'); return 2; }
  if (typeof flags.title !== 'string') { console.error('new: --title "..." is required'); return 2; }
  const file = path.join(NOTES_DIR, `${slug}.md`);
  if (fs.existsSync(file)) { console.error(`new: ${rel(file)} already exists`); return 1; }
  const head = git(['rev-parse', '--short=8', 'HEAD']);
  fs.mkdirSync(NOTES_DIR, { recursive: true });
  fs.writeFileSync(file, renderTemplate({
    title: flags.title,
    area: typeof flags.area === 'string' ? flags.area : 'other',
    tags: typeof flags.tags === 'string' ? flags.tags.split(',').map(s => s.trim()).filter(Boolean) : [],
    gameBuild: readSteamBuild(),
    progenitor: head.ok ? head.out : null,
    date: new Date().toISOString().slice(0, 10),
  }));
  console.log(`wrote ${rel(file)}; fill it in, then run "node tools/kb/kb.mjs index" and "node tools/kb/kb.mjs check", and commit it with the change it describes`);
  return 0;
}

function check() {
  const r = checkAll({ files: positional });
  for (const w of r.warnings) console.log(`warning: ${w}`);
  for (const e of r.errors) console.log(`error: ${e}`);
  console.log(`${r.count} note(s) checked, ${r.errors.length} error(s), ${r.warnings.length} warning(s)`);
  return r.errors.length ? 1 : 0;
}

function index() {
  const text = renderIndex(loadNotes());
  fs.writeFileSync(INDEX_FILE, text);
  console.log(`wrote ${rel(INDEX_FILE)}`);
  return 0;
}

function git(args) {
  const r = spawnSync('git', args, { cwd: REPO, encoding: 'utf8' });
  return { ok: r.status === 0, out: (r.stdout ?? '').trim() };
}
