// Offline recipe-catalog MCP tool implementations.
//
// Calls tools/catalog/lib (the same functions recipe.mjs uses) so MCP and CLI JSON
// payloads agree. Never runs index-catalog.mjs, never loads an asset, never contacts
// the game. A search hit is not availability; inspect is the place that reports
// requirements, evidence, stamp states and the offline verification limits.
// query/recipe JSON scalars are coerced to string before the library so a bare GUID
// number reaches searchRecipes. Zod schemas live here so MCP validation cannot rewrite
// the documented CLI error objects.

import { z } from 'zod';
import { loadInventory } from '../catalog/lib/inventory.mjs';
import {
  DEFAULT_GENERATED_DIR,
  DEFAULT_RECIPE_DIR,
  RecipeError,
  inspectRecipe,
  listRecipes,
  loadRecipes,
  searchRecipes,
} from '../catalog/lib/recipes.mjs';

export {
  DEFAULT_GENERATED_DIR,
  DEFAULT_RECIPE_DIR,
  RecipeError,
  inspectRecipe,
  listRecipes,
  loadRecipes,
  loadInventory,
  searchRecipes,
};

export const TOOL_NAMES = Object.freeze([
  'progenitor_catalog_search',
  'progenitor_catalog_inspect',
  'progenitor_catalog_associations',
  'progenitor_catalog_list',
]);

export const TOOL_DESCRIPTIONS = Object.freeze({
  progenitor_catalog_search:
    'Search the offline recipe catalog by free text, a bare GUID number, or a typed identity (prefab:, sequence:, archive:, object:, name:). Does not start the game, load any asset, or run the indexer; a hit means a maintained recipe exists, not that the asset is loaded, loadable, or safe to play. Use progenitor_catalog_inspect for the supported usage, requirements, evidence, stamp states, and verification limits (currentVerification.claimable is always false offline; runtimeAvailability is not-observed-offline). Empty hits include suggestions (closest recipe ids by word overlap).',
  progenitor_catalog_inspect:
    'Inspect one offline recipe by id: identities, requirements, operations with applicability, evidence with media presence and stamp states, inventory associations, and the supported usage example. Does not load assets or contact the game. A listed recipe is not availability; currentVerification.claimable is always false offline and runtimeAvailability is not-observed-offline. inventory.state is index presence only (indexed | inventory-unavailable | not-loaded). Read limitations and operations on this result rather than treating a search hit as a playable effect.',
  progenitor_catalog_associations:
    'Return the generated-inventory index state and name-match associations for one offline recipe. Does not load assets, start the game, or regenerate the inventory; a missing inventory is reported as inventory-unavailable. inventory.state is index presence only; runtime availability is not observed offline. An inferred or unique name match is a candidate, not proof the object is loaded or usable. Use progenitor_catalog_inspect for requirements, evidence, and the offline verification limits.',
  progenitor_catalog_list:
    'List every maintained offline recipe id with family, kind, revision and capture date. Same object shape as the CLI list: {dir, count, recipes}. Does not load assets or contact the game; presence on this list is not availability or current verification. Use progenitor_catalog_inspect for the supported usage, requirements, evidence, and the offline verification limits (claimable false, runtimeAvailability not-observed-offline).',
});

const jsonScalar = z.union([z.string(), z.number(), z.boolean()]);

export const CATALOG_QUERY_SCHEMA = jsonScalar.optional().describe(
  'Free text, a bare GUID number, or a typed identity: prefab:<int32> sequence:<int32> archive:<32hex> object:<32hex>/<pathID> name:<m_Name>. JSON numbers and booleans are coerced to string before the catalog library.',
);
export const CATALOG_RECIPE_SCHEMA = jsonScalar.optional().describe(
  'Recipe id, e.g. charm-target-channel-sequence or wendigo-frost-nova-appearance. JSON scalars are coerced to string before the catalog library.',
);
export const CATALOG_LIMIT_SCHEMA = z.union([z.number(), z.string()]).optional().describe(
  "Max recipe hits, 1..200, default 20. Out-of-range values are returned as {error:{code:'invalid-limit'}} by the catalog library, not rejected here.",
);

export const SEARCH_INPUT_SCHEMA = {
  query: CATALOG_QUERY_SCHEMA,
  limit: CATALOG_LIMIT_SCHEMA,
  includeInventory: z.boolean().optional().describe('When true, also list generated-inventory name matches. Default false. A name hit is not availability.'),
};
export const RECIPE_INPUT_SCHEMA = {
  recipe: CATALOG_RECIPE_SCHEMA,
};

function catalogCall(fn) {
  try {
    return fn();
  } catch (e) {
    if (e instanceof RecipeError) return e.toJSON();
    throw e;
  }
}

/** Coerce a JSON scalar to string. Non-scalars are returned unchanged so the library can reject them. */
export function coerceCatalogScalar(value) {
  if (value === undefined || value === null) return value;
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  return value;
}

function asQuery(args) {
  if (typeof args === 'string' || typeof args === 'number' || typeof args === 'boolean') return coerceCatalogScalar(args);
  return coerceCatalogScalar(args?.query);
}

function asRecipeId(args) {
  if (typeof args === 'string' || typeof args === 'number' || typeof args === 'boolean') return coerceCatalogScalar(args);
  return coerceCatalogScalar(args?.recipe);
}

function asSearchOpts(args) {
  if (args == null || typeof args === 'string' || typeof args === 'number' || typeof args === 'boolean') {
    return { includeInventory: false, limit: undefined };
  }
  return { includeInventory: args.includeInventory === true, limit: args.limit };
}

function requireQuery(args) {
  const q = asQuery(args);
  if (q === undefined || q === null) {
    throw new RecipeError('invalid-query', 'search needs exactly one query argument (quote it when it contains spaces)');
  }
  return q;
}

function requireRecipeId(args, command) {
  const id = asRecipeId(args);
  if (id === undefined || id === null) {
    throw new RecipeError('invalid-recipe-id', `${command} needs exactly one recipe id`);
  }
  return id;
}

/** {query, limit?, includeInventory?} — same JSON as searchRecipes(). */
export function progenitor_catalog_search(args) {
  return catalogCall(() => {
    const query = requireQuery(args);
    const { includeInventory, limit } = asSearchOpts(args);
    const store = loadRecipes(DEFAULT_RECIPE_DIR);
    const inventory = includeInventory ? loadInventory(DEFAULT_GENERATED_DIR) : undefined;
    return searchRecipes(store, query, { limit, includeInventory, inventory });
  });
}

/** {recipe} — same JSON as inspectRecipe() with the tools/catalog inventory loaded. */
export function progenitor_catalog_inspect(args) {
  return catalogCall(() => {
    const id = requireRecipeId(args, 'inspect');
    const store = loadRecipes(DEFAULT_RECIPE_DIR);
    const inventory = loadInventory(DEFAULT_GENERATED_DIR);
    return inspectRecipe(store, id, { inventory });
  });
}

/** {recipe} — inspectRecipe inventory + inventoryAssociations subset, plus the offline verification limits. */
export function progenitor_catalog_associations(args) {
  return catalogCall(() => {
    const id = requireRecipeId(args, 'associations');
    const store = loadRecipes(DEFAULT_RECIPE_DIR);
    const inventory = loadInventory(DEFAULT_GENERATED_DIR);
    const r = inspectRecipe(store, id, { inventory });
    return {
      recipeId: r.recipeId,
      inventory: r.inventory,
      inventoryAssociations: r.inventoryAssociations,
      currentVerification: r.currentVerification,
      runtimeAvailability: r.runtimeAvailability,
    };
  });
}

/** {} — same JSON as the CLI list: {dir, count, recipes}. */
export function progenitor_catalog_list(_args) {
  return catalogCall(() => {
    const store = loadRecipes(DEFAULT_RECIPE_DIR);
    return { dir: store.dir, count: store.recipes.length, recipes: listRecipes(store) };
  });
}

/**
 * Register the four catalog tools on an MCP server. Used by server.mjs and by the
 * in-memory transport test. `text` / `fail` are the same helpers the rest of the
 * server uses; catalog JSON errors set isError on the text result.
 */
export function registerCatalogTools(server, { text, fail }) {
  const catalogText = (obj) => {
    const r = text(obj);
    if (obj && typeof obj === 'object' && obj.error) r.isError = true;
    return r;
  };

  server.tool(
    'progenitor_catalog_search',
    TOOL_DESCRIPTIONS.progenitor_catalog_search,
    SEARCH_INPUT_SCHEMA,
    async (args) => {
      try { return catalogText(progenitor_catalog_search(args)); } catch (e) { return fail(e); }
    },
  );

  server.tool(
    'progenitor_catalog_inspect',
    TOOL_DESCRIPTIONS.progenitor_catalog_inspect,
    RECIPE_INPUT_SCHEMA,
    async (args) => {
      try { return catalogText(progenitor_catalog_inspect(args)); } catch (e) { return fail(e); }
    },
  );

  server.tool(
    'progenitor_catalog_associations',
    TOOL_DESCRIPTIONS.progenitor_catalog_associations,
    RECIPE_INPUT_SCHEMA,
    async (args) => {
      try { return catalogText(progenitor_catalog_associations(args)); } catch (e) { return fail(e); }
    },
  );

  server.tool(
    'progenitor_catalog_list',
    TOOL_DESCRIPTIONS.progenitor_catalog_list,
    {},
    async (args) => {
      try { return catalogText(progenitor_catalog_list(args)); } catch (e) { return fail(e); }
    },
  );
}
