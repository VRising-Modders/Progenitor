#!/usr/bin/env node
// Answer "which Charm recipe is the beam?" from the offline catalog:
// caster draws nothing, target draws particles but no beam, borrow is an
// isolated particle. Does not load assets or start the game.
//
//   node tools/catalog/examples/charm-particles-vs-beam.mjs

import {
  progenitor_catalog_inspect,
  progenitor_catalog_search,
} from '../../mcp/catalog-tools.mjs';
import { printRecipeAnswer } from './print-recipe.mjs';

const CASTER = 'charm-caster-link-sequence';
const TARGET = 'charm-target-channel-sequence';
const BORROW = 'charm-loop-particle-borrow';

const found = progenitor_catalog_search({ query: 'charm beam' });
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

if (found.results[0]?.recipeId !== TARGET) {
  console.error(`expected first hit ${TARGET}, got ${found.results[0]?.recipeId ?? '(none)'}`);
  process.exit(1);
}

const casterHit = found.results.find((r) => r.recipeId === CASTER);
if (!casterHit) {
  console.error(`expected ${CASTER} on the same search`);
  process.exit(1);
}

const target = progenitor_catalog_inspect({ recipe: TARGET });
const caster = progenitor_catalog_inspect({ recipe: CASTER });
const borrow = progenitor_catalog_inspect({ recipe: BORROW });
for (const look of [target, caster, borrow]) {
  if (look.error) {
    console.error(JSON.stringify(look, null, 2));
    process.exit(1);
  }
}

console.log('');
console.log('how to tell particles from the beam:');
console.log(`  ${CASTER}: appearance=${caster.operations.appearance.status}  (draws nothing, no beam)`);
console.log(`  ${TARGET}: appearance=${target.operations.appearance.status}  (particles observed, no beam drawn)`);
console.log(`  ${BORROW}: kind=${borrow.kind}  (isolated particle; not the spell, not a beam)`);
console.log('read observed-partial / observed-failure from operations.appearance.status; do not promote either to observed-success.');
console.log('');

console.log('--- caster (draws nothing) ---');
printRecipeAnswer(caster, {
  supported: 'ctx.Effects.ServerBeamLink(source, target, seconds: 20f, sequenceGuid: -389499105) — Link default; nothing drawn.',
});
console.log('');
console.log('--- target (particles, no beam) ---');
printRecipeAnswer(target, {
  supported: 'ctx.Effects.ServerBeamLink(source, target, seconds: 20f, sequenceGuid: -541113174) — particles at the player, no beam between endpoints.',
});
console.log('');
console.log('--- borrow (isolated particle) ---');
printRecipeAnswer(borrow, {
  supported: 'ctx.Effects.Borrow(AssetKind.Particle, "Part_Charm_Loop_01") — owned clone of one particle body, not the Charm spell.',
});
