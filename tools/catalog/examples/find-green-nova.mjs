#!/usr/bin/env node
// Answer "Find a green nova effect and show me the supported way to use it"
// from the offline catalog. Does not load assets or start the game.
//
//   node tools/catalog/examples/find-green-nova.mjs

import {
  progenitor_catalog_inspect,
  progenitor_catalog_search,
} from '../../mcp/catalog-tools.mjs';
import { printRecipeAnswer } from './print-recipe.mjs';

const APPEARANCE = 'wendigo-frost-nova-appearance';
const RING = 'wendigo-frost-nova-ring';

const found = progenitor_catalog_search({ query: 'green nova' });
if (found.error) {
  console.error(JSON.stringify(found, null, 2));
  process.exit(1);
}

console.log(`search query: ${JSON.stringify(found.query)}`);
console.log(`scope: ${found.scope}`);
console.log('hits:');
for (const hit of found.results) {
  console.log(`  ${hit.recipeId}  [${hit.kind}]  ${hit.title}`);
}

const appearance = found.results[0];
if (!appearance || appearance.recipeId !== APPEARANCE) {
  console.error(`expected first hit ${APPEARANCE}, got ${appearance?.recipeId ?? '(none)'}`);
  process.exit(1);
}
if (!found.results.some((r) => r.recipeId === RING && r.kind === 'gameplay-payload')) {
  console.error(`expected ${RING} as the payload the appearance restyles`);
  process.exit(1);
}

const look = progenitor_catalog_inspect({ recipe: appearance.recipeId });
if (look.error) {
  console.error(JSON.stringify(look, null, 2));
  process.exit(1);
}

console.log('');
printRecipeAnswer(look, {
  supported: 'After spawning wendigo-frost-nova-ring, SampleFrostNovaAppearance writes EndRadius=18 and per-slot crystal colour on owned rings only (not a generic recolour API).',
});

console.log('controls:');
for (const c of look.controls) {
  const tested = c.tested.length
    ? c.tested.map((t) => JSON.stringify(t.value)).join('; ')
    : 'untested';
  console.log(`  ${c.name}: tested ${tested}`);
  console.log(`    limitations: ${c.limitations}`);
  if (c.implementation) console.log(`    implementation: ${c.implementation}`);
}
console.log('implementation:');
for (const im of look.implementation) {
  console.log(`  ${im.path}${im.symbol ? ' :: ' + im.symbol : ''}  [${im.status}]`);
  console.log(`    file: ${im.file.present ? 'present' : 'MISSING'}`);
}
