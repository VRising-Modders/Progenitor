// Knowledge-note MCP tools: search and read knowledge/notes (see knowledge/README.md).
//
// Calls tools/kb/lib.mjs, the same functions the kb.mjs CLI uses. Offline and read-only:
// never writes a note, never contacts the game.

import fs from 'node:fs';
import path from 'node:path';
import { z } from 'zod';
import { NOTES_DIR, loadNotes, searchNotes, validSlug } from '../kb/lib.mjs';

export const TOOL_NAMES = Object.freeze(['progenitor_kb_search', 'progenitor_kb_read']);

export function kbSearch({ query, limit }) {
  return searchNotes(loadNotes(), query, { limit: limit ?? 10 });
}

export function kbRead({ slug }) {
  const name = String(slug).replace(/\.md$/, '').split(/[\\/]/).pop();
  if (!validSlug(name)) throw new Error(`not a note slug: ${slug}`);
  const file = path.join(NOTES_DIR, `${name}.md`);
  if (!fs.existsSync(file)) throw new Error(`no note knowledge/notes/${name}.md; search first`);
  return fs.readFileSync(file, 'utf8');
}

export function registerKbTools(server, { text, fail }) {
  server.tool('progenitor_kb_search',
    'Search the knowledge notes (knowledge/notes): short records of shipped work with the game build, route, observed engine behavior, verification status and gotchas as symptom/cause/fix. Search before starting work in an area. Every query word must match; title and tag hits rank first. Status is verified-live, failed, compile-verified (build or tooling only) or reported (an outside observation not yet reproduced: a lead, not a result).',
    {
      query: z.string().min(1).describe('Free-text words, e.g. "coffin exit" or "hood palette crash".'),
      limit: z.number().int().min(1).max(50).optional().describe('Default 10.'),
    },
    async args => { try { return text(kbSearch(args)); } catch (e) { return fail(e); } });

  server.tool('progenitor_kb_read',
    'Read one knowledge note in full by slug (the file name without .md, as progenitor_kb_search returns).',
    { slug: z.string().min(1) },
    async args => { try { return { content: [{ type: 'text', text: kbRead(args) }] }; } catch (e) { return fail(e); } });
}
