import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import {
  DEFAULT_GENERATED_DIR,
  CATALOG_QUERY_SCHEMA,
  CATALOG_RECIPE_SCHEMA,
  SEARCH_INPUT_SCHEMA,
  inspectRecipe,
  listRecipes,
  loadInventory,
  loadRecipes,
  progenitor_catalog_associations,
  progenitor_catalog_inspect,
  progenitor_catalog_list,
  progenitor_catalog_search,
  registerCatalogTools,
  searchRecipes,
  TOOL_DESCRIPTIONS,
} from '../catalog-tools.mjs';

const CATALOG_TOOLS_URL = new URL('../catalog-tools.mjs', import.meta.url);

function importedSpecs(fileUrl) {
  const src = readFileSync(fileURLToPath(fileUrl), 'utf8');
  return [...src.matchAll(/\bfrom\s+['"]([^'"]+)['"]/g)].map((m) => m[1]);
}

function assertNoGameSideChannels(fileUrl, seen = new Set()) {
  const file = fileURLToPath(fileUrl);
  if (seen.has(file)) return;
  seen.add(file);
  const src = readFileSync(file, 'utf8');
  assert.doesNotMatch(src, /['"]node:net['"]/, `${file} must not import node:net`);
  assert.doesNotMatch(src, /['"]net['"]/, `${file} must not import net`);
  assert.doesNotMatch(src, /['"]node:child_process['"]/, `${file} must not import node:child_process`);
  assert.doesNotMatch(src, /['"]child_process['"]/, `${file} must not import child_process`);
  for (const spec of importedSpecs(fileUrl)) {
    assert.notEqual(spec, 'node:net');
    assert.notEqual(spec, 'node:child_process');
    if (spec.startsWith('.') || spec.startsWith('/')) {
      assertNoGameSideChannels(new URL(spec, fileUrl), seen);
    }
  }
}

function identicalJson(actual, expected) {
  assert.equal(JSON.stringify(actual), JSON.stringify(expected));
}

test('progenitor_catalog_search("nova") JSON matches searchRecipes byte-for-byte', () => {
  const store = loadRecipes();
  const fromLib = searchRecipes(store, 'nova');
  const fromMcp = progenitor_catalog_search('nova');
  identicalJson(fromMcp, fromLib);
  assert.ok(fromMcp.total > 0);
  assert.ok(fromMcp.families.includes('wendigo-frost-nova'));
  assert.match(fromMcp.scope, /offline recipe search/);
});

test('inspect JSON exposes operation sides and rig-run frameworkSides; offline claimable stays false', () => {
  const fromMcp = progenitor_catalog_inspect('wendigo-frost-nova-appearance');
  assert.equal(fromMcp.currentVerification.claimable, false);
  assert.equal(fromMcp.runtimeAvailability.state, 'not-observed-offline');
  assert.deepEqual(fromMcp.operations.appearance.sides, ['server', 'client']);
  assert.deepEqual(fromMcp.operations.poolReuse.sides, ['server', 'client']);
  assert.deepEqual(fromMcp.operations.explicitCleanup.sides, ['server']);
  const a10 = fromMcp.evidence.find((e) => e.id === 'A10');
  assert.equal(a10.kind, 'rig-run');
  assert.equal(a10.stamps.framework.state, 'legacy-combined');
  assert.ok(a10.stamps.frameworkSides);
  assert.equal(a10.applicability, 'unknown-stamps');
  assert.match(a10.build.frameworkSides.client, /b089d45112510541e3dea1f50102570795a4d42212086919a3e72c5f425bc640/);
  assert.match(a10.build.frameworkSides.server, /d698ee0499bf72ee7fdf6f6fd8b12cf3215ec74cce4a25bf05ed02b00ff00679/);
  assert.notEqual(a10.build.frameworkSides.client, a10.build.frameworkSides.server);

  const a10Core = '42c9c081d87bae5f79d1deecdfa40711b23bef05142cedfecd72f4b202e3c402';
  const a10Dev = '85cd21a21623e5da7da5349523997b7ff76656e45816807a42a8700a6fa7337c';
  const serverStamp = `generation:1; core:${a10Core}; dev:${a10Dev}; sample:d698ee0499bf72ee7fdf6f6fd8b12cf3215ec74cce4a25bf05ed02b00ff00679`;
  const clientStamp = `generation:2; core:${a10Core}; dev:${a10Dev}; sample:b089d45112510541e3dea1f50102570795a4d42212086919a3e72c5f425bc640`;
  const store = loadRecipes();
  const matched = inspectRecipe(store, 'wendigo-frost-nova-appearance', {
    currentBuild: {
      contentCatalogSha256: a10.build.contentCatalogSha256,
      gameIdentity: a10.build.gameIdentity,
      framework: null,
      frameworkSides: { server: serverStamp, client: clientStamp },
    },
  });
  assert.equal(matched.evidence.find((e) => e.id === 'A10').applicability, 'matches-checkable-stamps');
  assert.notEqual(matched.operations.poolReuse.applicability, 'matches-checkable-stamps', 'A6 remains unknown; the operation cannot be currently verified from A10 alone');
  const clientChanged = inspectRecipe(store, 'wendigo-frost-nova-appearance', {
    currentBuild: {
      contentCatalogSha256: a10.build.contentCatalogSha256,
      gameIdentity: a10.build.gameIdentity,
      framework: null,
      frameworkSides: { server: serverStamp, client: serverStamp },
    },
  });
  assert.equal(clientChanged.evidence.find((e) => e.id === 'A10').applicability, 'stale');
  assert.equal(clientChanged.operations.poolReuse.applicability, 'stale');
  assert.notEqual(clientChanged.operations.poolReuse.applicability, 'matches-checkable-stamps');
});

test('progenitor_catalog_inspect("charm-target-channel-sequence") JSON matches inspectRecipe', () => {
  const store = loadRecipes();
  const inventory = loadInventory(DEFAULT_GENERATED_DIR);
  const fromLib = inspectRecipe(store, 'charm-target-channel-sequence', { inventory });
  const fromMcp = progenitor_catalog_inspect('charm-target-channel-sequence');
  identicalJson(fromMcp, fromLib);
  assert.equal(fromMcp.currentVerification.claimable, false);
  assert.equal(fromMcp.runtimeAvailability.state, 'not-observed-offline');
  assert.ok(fromMcp.inventory.state === 'indexed' || fromMcp.inventory.state === 'inventory-unavailable' || fromMcp.inventory.state === 'not-loaded');
  assert.equal(fromMcp.inventory.availability, undefined);
  assert.equal(fromMcp.inventory.note, 'index presence only; runtime availability is not observed offline');
  assert.ok(Array.isArray(fromMcp.inventoryAssociations));
  assert.ok(fromMcp.operations.discovery);
  assert.ok(fromMcp.evidence.every((e) => e.media && e.stamps && e.applicability));
});

test('unknown recipe returns a structured error object, not an exception', () => {
  const r = progenitor_catalog_inspect({ recipe: 'no-such-recipe' });
  assert.equal(r.error.code, 'unknown-recipe');
  assert.ok(Array.isArray(r.error.suggestions));
  assert.ok(Array.isArray(r.error.known));
  assert.ok(r.error.known.includes('charm-target-channel-sequence'));
  assert.equal(r.error.id, 'no-such-recipe');
});

test('an invalid limit returns invalid-limit as a structured error', () => {
  const r = progenitor_catalog_search({ query: 'nova', limit: 0 });
  assert.equal(r.error.code, 'invalid-limit');
  assert.equal(r.error.limit, 0);
});

test('search never imports node:net, node:child_process, or the indexer', () => {
  const src = readFileSync(fileURLToPath(CATALOG_TOOLS_URL), 'utf8');
  assert.doesNotMatch(src, /\bimport\b[\s\S]*index-catalog/);
  assert.ok(!importedSpecs(CATALOG_TOOLS_URL).some((s) => s.includes('index-catalog')));
  assertNoGameSideChannels(CATALOG_TOOLS_URL);
  progenitor_catalog_search({ query: 'nova' });
});

test('list and associations match the shared library JSON; descriptions name the offline limits', () => {
  const store = loadRecipes();
  identicalJson(progenitor_catalog_list({}), { dir: store.dir, count: store.recipes.length, recipes: listRecipes(store) });
  const inspected = progenitor_catalog_inspect({ recipe: 'wendigo-frost-nova-appearance' });
  identicalJson(
    progenitor_catalog_associations({ recipe: 'wendigo-frost-nova-appearance' }),
    {
      recipeId: inspected.recipeId,
      inventory: inspected.inventory,
      inventoryAssociations: inspected.inventoryAssociations,
      currentVerification: inspected.currentVerification,
      runtimeAvailability: inspected.runtimeAvailability,
    },
  );
  for (const [name, text] of Object.entries(TOOL_DESCRIPTIONS)) {
    assert.match(text, /offline/i, name);
    assert.match(text, /not (availability|proof)|not that the asset is loaded|presence on this list is not availability/i, name);
    assert.match(text, /progenitor_catalog_inspect|inspect/i, name);
  }
});

test('query and recipe schemas accept JSON scalars; missing query equals the CLI invalid-query object', () => {
  const searchShape = z.object(SEARCH_INPUT_SCHEMA);
  assert.equal(searchShape.safeParse({ query: -1227952025 }).success, true);
  assert.equal(searchShape.safeParse({ query: true }).success, true);
  assert.equal(searchShape.safeParse({ query: 'nova' }).success, true);
  assert.equal(searchShape.safeParse({}).success, true);
  assert.equal(CATALOG_QUERY_SCHEMA.safeParse(-1227952025).success, true);
  assert.equal(CATALOG_RECIPE_SCHEMA.safeParse('wendigo-frost-nova-ring').success, true);

  const missing = progenitor_catalog_search({});
  assert.equal(missing.error.code, 'invalid-query');
  assert.match(missing.error.message, /search needs exactly one query argument/);

  const numeric = progenitor_catalog_search({ query: -1227952025 });
  assert.equal(numeric.error, undefined);
  assert.ok(numeric.total > 0);
  assert.equal(numeric.query, '-1227952025');
  assert.equal(numeric.parsed.kind, 'number');
  assert.equal(numeric.results[0].recipeId, 'wendigo-frost-nova-ring');
});

test('registered schemas accept a numeric query through the SDK in-memory transport', async () => {
  const mcp = new McpServer({ name: 'catalog-test', version: '0.0.0' });
  const text = (obj) => ({ content: [{ type: 'text', text: typeof obj === 'string' ? obj : JSON.stringify(obj) }] });
  const fail = (e) => ({ content: [{ type: 'text', text: `error: ${e.message ?? e}` }], isError: true });
  registerCatalogTools(mcp, { text, fail });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'catalog-test-client', version: '0.0.0' });
  await mcp.connect(serverTransport);
  await client.connect(clientTransport);
  try {
    const result = await client.callTool({ name: 'progenitor_catalog_search', arguments: { query: -1227952025 } });
    assert.equal(result.isError, undefined);
    const payload = JSON.parse(result.content[0].text);
    assert.equal(payload.error, undefined);
    assert.equal(payload.parsed.kind, 'number');
    assert.equal(payload.query, '-1227952025');
    assert.ok(payload.total > 0);
    assert.equal(payload.results[0].recipeId, 'wendigo-frost-nova-ring');
  } finally {
    await client.close();
    await mcp.close();
  }
});
