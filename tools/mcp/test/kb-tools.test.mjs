import test from 'node:test';
import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { TOOL_NAMES, kbRead, kbSearch, registerKbTools } from '../kb-tools.mjs';
import { loadNotes, searchNotes } from '../../kb/lib.mjs';

const text = obj => ({ content: [{ type: 'text', text: typeof obj === 'string' ? obj : JSON.stringify(obj, null, 2) }] });
const fail = e => ({ content: [{ type: 'text', text: `error: ${e.message ?? e}` }], isError: true });

async function connect() {
  const server = new McpServer({ name: 'kb-test', version: '0' });
  registerKbTools(server, { text, fail });
  const [a, b] = InMemoryTransport.createLinkedPair();
  await server.connect(a);
  const client = new Client({ name: 'kb-test-client', version: '0' });
  await client.connect(b);
  return client;
}

test('kb tools register and search matches the library', async () => {
  const client = await connect();
  const names = (await client.listTools()).tools.map(t => t.name);
  for (const n of TOOL_NAMES) assert.ok(names.includes(n), n);
  const r = await client.callTool({ name: 'progenitor_kb_search', arguments: { query: 'coffin exit' } });
  assert.equal(JSON.stringify(JSON.parse(r.content[0].text)), JSON.stringify(searchNotes(loadNotes(), 'coffin exit', { limit: 10 })));
  assert.deepEqual(kbSearch({ query: 'coffin exit' }).map(h => h.slug), ['coffin-exit-respawn-input']);
});

test('kb read returns a note by slug and refuses paths outside the notes folder', async () => {
  const client = await connect();
  const r = await client.callTool({ name: 'progenitor_kb_read', arguments: { slug: 'coffin-exit-respawn-input' } });
  assert.match(r.content[0].text, /^---\ntitle: Leaving a coffin/);
  assert.throws(() => kbRead({ slug: '../../CLAUDE' }), /no note knowledge\/notes\/CLAUDE|not a note slug/);
  assert.throws(() => kbRead({ slug: 'no-such-note' }), /no note/);
  const bad = await client.callTool({ name: 'progenitor_kb_read', arguments: { slug: '..\\..\\AGENTS' } });
  assert.equal(bad.isError, true);
});
