import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  INDEX_FILE, NOTES_DIR, checkAll, checkNote, gotchasOf, loadNotes, parseNote, renderIndex,
  renderTemplate, searchNotes,
} from '../lib.mjs';

const GOOD = `---
title: Coffin exit is the Respawn input
game: V Rising
game_build: Steam 23521359
progenitor: abc12345
side: both
area: bots
tags: coffin, input
route: hold Respawn
status: verified-live
date: 2026-09-27
evidence: research/bot-coffin-exit.md
---

## Goal

Leave the coffin.

## Route

Hold Respawn.

## What the engine did

SleepInsideSystem reads the Respawn down edge.

## Verification

Verified live.

## Gotchas

### Interact does nothing

- **Symptom:** buff stays.
- **Cause:** exit reads Respawn only.
- **Fix:** hold Respawn.
`;

function tempNotes(files) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'kb-'));
  const notes = path.join(dir, 'notes');
  fs.mkdirSync(notes);
  for (const [name, text] of Object.entries(files)) fs.writeFileSync(path.join(notes, name), text);
  return { dir, notes, index: path.join(dir, 'INDEX.md') };
}

const check = (text, name = 'good-note.md', opts) => checkNote(text, path.join(NOTES_DIR, name), opts);
const withStatus = (status, text = GOOD) => text.replace('status: verified-live', `status: ${status}`);

test('a complete note parses and passes', () => {
  const note = parseNote(GOOD);
  assert.equal(note.meta.title, 'Coffin exit is the Respawn input');
  assert.deepEqual(note.meta.tags, ['coffin', 'input']);
  assert.deepEqual(gotchasOf(note), [{ name: 'Interact does nothing', symptom: 'buff stays.', cause: 'exit reads Respawn only.', fix: 'hold Respawn.' }]);
  const r = check(GOOD);
  assert.deepEqual(r.errors, []);
  assert.deepEqual(r.warnings, []);
});

test('CRLF notes parse the same', () => {
  assert.deepEqual(check(GOOD.replace(/\n/g, '\r\n')).errors, []);
});

test('missing front matter, fields and sections are errors', () => {
  assert.match(check('# no front matter').errors[0], /missing front matter/);
  assert.ok(check(GOOD.replace(/^route: .*\n/m, '')).errors.some(e => /"route" is required/.test(e)));
  const noRoute = GOOD.replace(/## Route\n\nHold Respawn.\n\n/, '');
  assert.ok(check(noRoute).errors.some(e => /missing section "## Route"/.test(e)));
});

test('only observed statuses pass, with evidence, an exact build, a commit and a side', () => {
  for (const bad of ['works', 'source-checked', 'not-run'])
    assert.ok(check(withStatus(bad)).errors.some(e => /not one of/.test(e)), bad);
  assert.ok(check(GOOD.replace(/^evidence: .*$/m, 'evidence:')).errors.some(e => /"evidence" is required/.test(e)));
  assert.ok(check(GOOD.replace('game_build: Steam 23521359', 'game_build: unrecorded')).errors.some(e => /must name the exact build/.test(e)));
  assert.ok(check(GOOD.replace('progenitor: abc12345', 'progenitor: main')).errors.some(e => /must name a commit/.test(e)));
  assert.deepEqual(check(GOOD.replace('progenitor: abc12345', 'progenitor: PR 107')).errors, []);
  assert.ok(check(GOOD.replace('side: both', 'side: everywhere')).errors.some(e => /side "everywhere"/.test(e)));
  assert.ok(check(GOOD.replace(/^side: .*\n/m, '')).errors.some(e => /"side" is required/.test(e)));
});

test('failed passes; reported and in-game compile-verified pass with a warning', () => {
  const failed = check(withStatus('failed'));
  assert.deepEqual([failed.errors, failed.warnings], [[], []]);
  const reported = check(withStatus('reported'));
  assert.deepEqual(reported.errors, []);
  assert.match(reported.warnings[0], /not yet reproduced/);
  assert.match(check(withStatus('compile-verified')).warnings[0], /build or tooling/);
  assert.deepEqual(check(withStatus('compile-verified').replace('side: both', 'side: offline')).warnings, []);
});

test('gotchas need symptom, cause and fix', () => {
  const noFix = GOOD.replace('- **Fix:** hold Respawn.\n', '');
  assert.ok(check(noFix).errors.some(e => /missing "- \*\*Fix:\*\*"/.test(e)));
  const none = GOOD.replace(/### Interact[\s\S]*$/, 'Nothing yet.\n');
  assert.ok(check(none).errors.some(e => /at least one "### <name>"/.test(e)));
});

test('the new-note template fails until filled in', () => {
  const errors = check(renderTemplate({ title: 'X', date: '2026-10-04' }), 'x.md').errors;
  for (const re of [/section "## Goal" is empty/, /missing "- \*\*Symptom:\*\*"/, /status "<verified-live/, /side "<client/, /exact build/, /must name a commit/])
    assert.ok(errors.some(e => re.test(e)), `${re}\n${errors.join('\n')}`);
});

test('analysis listings, metadata lines and payloads are rejected', () => {
  // Label prefixes and the address marker are assembled at run time so this file never carries a
  // raw listing line.
  const fn = 'FU' + 'N_', dat = 'DA' + 'T_', addr = 'R' + 'VA';
  for (const line of [
    `undefined8 ${fn}180135ba0(longlong param_1)`,
    `if (${dat}1891a2b30 == 0) {`,
    `public void OnUpdate() { } // ${addr}: 0x6380060 Offset: 0x637F460 VA: 0x6380060`,
    `[Address(${addr} = "0x6380060", Offset = "0x637F460")]`,
    'A'.repeat(240),
  ]) {
    const r = check(GOOD.replace('SleepInsideSystem reads the Respawn down edge.', line));
    assert.ok(r.errors.some(e => /must not contain native/.test(e)), line);
  }
  // A method name in a sentence is fine.
  assert.deepEqual(check(GOOD.replace('reads the Respawn', 'in SleepInsideSystem.OnUpdate reads the Respawn')).errors, []);
});

test('links to game files, broken repo links and links outside the repository are rejected', () => {
  assert.ok(check(GOOD.replace('Hold Respawn.', 'See [the dll](../../x/ProjectM.dll).')).errors.some(e => /game\/binary file/.test(e)));
  assert.ok(check(GOOD.replace('Hold Respawn.', 'See [doc](../../docs/no-such-page.md).')).errors.some(e => /broken link/.test(e)));
  assert.deepEqual(check(GOOD.replace('Hold Respawn.', 'See [doc](../../docs/wearables.md#x).')).errors, []);
  const outside = path.join(os.tmpdir(), 'notes-outside-the-repo');
  const link = path.relative(NOTES_DIR, outside).split(path.sep).join('/');
  const r = check(GOOD.replace('Hold Respawn.', `See [r](${link}/a.md).`), 'good-note.md');
  assert.ok(r.errors.some(e => /links outside the repository/.test(e)), r.errors.join('\n'));
});

test('overlong notes are rejected', () => {
  assert.ok(check(GOOD + '\nmore\n'.repeat(80)).errors.some(e => /keep a note under/.test(e)));
});

test('checkAll flags stray files, duplicate titles and a stale index', () => {
  const t = tempNotes({ 'a-note.md': GOOD, 'b-note.md': GOOD, 'dump.bin': 'x' });
  const r = checkAll({ notesDir: t.notes, indexFile: t.index });
  assert.ok(r.errors.some(e => /only \.md notes/.test(e)));
  assert.ok(r.errors.some(e => /duplicate title/.test(e)));
  assert.ok(r.errors.some(e => /out of date/.test(e)));
  fs.rmSync(path.join(t.notes, 'dump.bin'));
  fs.writeFileSync(path.join(t.notes, 'b-note.md'), GOOD.replace('Coffin exit', 'Other'));
  fs.writeFileSync(t.index, renderIndex(loadNotes(t.notes), t.dir));
  assert.deepEqual(checkAll({ notesDir: t.notes, indexFile: t.index }).errors, []);
});

test('search requires every term and ranks title/tag hits above body hits', () => {
  const t = tempNotes({
    'coffin.md': GOOD,
    'other.md': GOOD.replace('Coffin exit is the Respawn input', 'Hood winding').replace('tags: coffin, input', 'tags: hood').replace('Leave the coffin.', 'Unrelated to a coffin.'),
  });
  const notes = loadNotes(t.notes);
  const hits = searchNotes(notes, 'coffin');
  assert.deepEqual(hits.map(h => h.slug), ['coffin', 'other']);
  assert.ok(hits[0].score > hits[1].score);
  assert.equal(hits[0].side, 'both');
  assert.deepEqual(searchNotes(notes, 'coffin winding').map(h => h.slug), ['other']);
  assert.deepEqual(searchNotes(notes, 'grand theft auto'), []);
  assert.deepEqual(searchNotes(notes, 'coffins').map(h => h.slug), ['coffin', 'other']);
  assert.match(hits[0].snippet, /coffin/i);
});

test('the repository notes and index pass', () => {
  assert.deepEqual(checkAll({}).errors, []);
  assert.ok(fs.existsSync(INDEX_FILE));
});

test('one malformed note does not break search, index or checkAll', () => {
  const t = tempNotes({ 'good.md': GOOD, 'draft.md': '# half written', 'untitled.md': GOOD.replace(/^title: .*\n/m, ''), 'untitled-two.md': GOOD.replace(/^title: .*\n/m, '') });
  const notes = loadNotes(t.notes);
  assert.deepEqual(notes.map(n => n.slug).sort(), ['good', 'untitled', 'untitled-two']);
  assert.ok(searchNotes(notes, 'coffin').length > 0);
  assert.doesNotThrow(() => renderIndex(notes, t.dir));
  const r = checkAll({ notesDir: t.notes, indexFile: t.index });
  assert.ok(r.errors.some(e => /draft\.md: missing front matter/.test(e)));
  assert.ok(r.errors.some(e => /"title" is required/.test(e)));
});

test('malformed links, duplicate keys, section order and fenced headings are handled', () => {
  assert.ok(check(GOOD.replace('Hold Respawn.', 'See [a](%E0%A4%A).')).errors.some(e => /malformed link/.test(e)));
  assert.ok(check(GOOD.replace('side: both\n', 'side: both\nside: client\n')).errors.some(e => /appears twice/.test(e)));
  const swapped = GOOD.replace('## Goal\n\nLeave the coffin.\n\n## Route\n\nHold Respawn.', '## Route\n\nHold Respawn.\n\n## Goal\n\nLeave the coffin.');
  assert.ok(check(swapped).errors.some(e => /must be in the order/.test(e)));
  const fenced = GOOD.replace('## Route\n\nHold Respawn.', '```md\n## Route\n```');
  assert.ok(check(fenced).errors.some(e => /missing section "## Route"/.test(e)));
});

test('natural-language queries match, and partial matches are a labelled fallback', () => {
  const notes = loadNotes(tempNotes({ 'coffin.md': GOOD.replace('Leave the coffin.', 'Leaving the coffin.') }).notes);
  assert.deepEqual(searchNotes(notes, 'leave coffin').map(h => h.slug), ['coffin']);
  assert.deepEqual(searchNotes(notes, 'how do I leave a coffin').map(h => h.slug), ['coffin']);
  const partial = searchNotes(notes, 'coffin winding');
  assert.equal(partial[0].partial, true);
  assert.deepEqual(partial[0].missed, ['wind']);
  assert.equal(searchNotes(notes, 'coffin').some(h => h.partial), false);
});
