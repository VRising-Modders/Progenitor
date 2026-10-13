#!/usr/bin/env node
// recipe.mjs - offline search and inspection of the maintained recipe layer (see README.md).
//
//   node tools/catalog/recipe.mjs list
//   node tools/catalog/recipe.mjs validate
//   node tools/catalog/recipe.mjs search <query> [--limit n] [--inventory] [--text]
//   node tools/catalog/recipe.mjs inspect <recipe-id> [--text]
//   node tools/catalog/recipe.mjs associations <recipe-id>
//
// Options: --recipes <dir>  --generated <dir>  --json (default) / --text
// Exit codes: 0 ok, 2 invalid input, 3 unknown recipe, 4 invalid recipe data, 5 unexpected error.
//
// No game process is started or contacted, nothing is loaded, cast or previewed, and the
// generated inventory is never (re)built here: a missing inventory is reported, not scanned for.

import { loadInventory } from './lib/inventory.mjs';
import { DEFAULT_GENERATED_DIR, DEFAULT_RECIPE_DIR, RecipeError, inspectRecipe, listRecipes, loadRecipes, searchRecipes, validateRecipeDirectory } from './lib/recipes.mjs';

function usage() {
  console.log(`usage: node tools/catalog/recipe.mjs <command> [args] [options]
       (cwd = repository root; from tools/catalog, node recipe.mjs also works)

  list                                  every recipe id with family, kind and capture date
  validate                              validate the recipe directory (per-file schema, unique ids);
                                        exit 4 when any problem exists
  search <query> [--limit n] [--inventory]
                                        query is free text, a bare number (any GUID domain), or a
                                        typed identity: prefab:<int32> sequence:<int32>
                                        archive:<32hex> object:<32hex>/<pathID> name:<m_Name>
                                        --inventory also lists generated-inventory name matches
  inspect <recipe-id>                   requirements, relationships, controls, evidence,
                                        limitations, implementation links, inventory associations
  associations <recipe-id>              only the inventory associations of one recipe

options
  --recipes <dir>     recipe directory (default ${DEFAULT_RECIPE_DIR})
  --generated <dir>   where catalog-summary.json / catalog-inventory.json live (default ${DEFAULT_GENERATED_DIR})
  --limit <n>         1..200, default 20
  --json | --text     output format (default json)
`);
}

function parseArgs(argv) {
  const a = { command: argv[2], positional: [], recipes: DEFAULT_RECIPE_DIR, generated: DEFAULT_GENERATED_DIR, limit: undefined, inventory: false, text: false };
  let optionsEnded = false;
  for (let i = 3; i < argv.length; i++) {
    const k = argv[i];
    if (optionsEnded) { a.positional.push(k); continue; }
    if (k === '--') optionsEnded = true;
    else if (k === '--recipes') a.recipes = argv[++i];
    else if (k === '--generated') a.generated = argv[++i];
    else if (k === '--limit') a.limit = argv[++i];
    else if (k === '--inventory') a.inventory = true;
    else if (k === '--text') a.text = true;
    else if (k === '--json') a.text = false;
    else if (k === '-h' || k === '--help') { usage(); process.exit(0); }
    else if (k.startsWith('--')) throw new RecipeError('invalid-option', `unknown option ${k}`, { option: k });
    else a.positional.push(k);
  }
  return a;
}

function emit(obj, text, renderer) {
  if (text && renderer) console.log(renderer(obj));
  else console.log(JSON.stringify(obj, null, 2));
}

function renderSearch(r) {
  const lines = [`search ${JSON.stringify(r.query)} (${r.parsed.kind}${r.parsed.domain ? ' ' + r.parsed.domain : ''}) -> ${r.total} recipe(s)${r.truncated ? `, showing ${r.limit}` : ''}`];
  for (const x of r.results) {
    lines.push(`  ${x.recipeId}  [${x.family.id} / ${x.kind}]  score ${x.score}`);
    for (const m of x.matched) lines.push(`      matched ${m.on}${m.domain ? ' ' + m.domain : ''}${m.value !== undefined ? ' ' + JSON.stringify(m.value) : ''}${m.note ? '  (' + m.note + ')' : ''}`);
  }
  if (r.inventory) {
    lines.push(`inventory: ${r.inventory.state}${r.inventory.reason ? ' - ' + r.inventory.reason : ''}; ${r.inventory.total} name match(es)${r.inventory.truncated ? ', truncated' : ''}`);
    for (const row of r.inventory.rows) lines.push(`  ${row.type} :: ${row.name}  object:${row.archiveId}/${row.pathID}`);
    if (r.inventory.note) lines.push(`  note: ${r.inventory.note}`);
  }
  if (Array.isArray(r.suggestions)) {
    lines.push(`suggestions: ${r.suggestions.length ? r.suggestions.join(', ') : '(none)'}`);
  }
  lines.push(`scope: ${r.scope}`);
  return lines.join('\n');
}

function formatSupport(op) {
  const s = op.support ?? { classes: [], strongest: 'none', missing: [], agentReportedOnly: false, runsNotComplete: [], runsComplete: [], runsOtherBuild: [] };
  if (op.status === 'untested' || op.status === 'not-applicable' || (s.strongest === 'none' && !op.evidence.length)) {
    return s.runsNotComplete.length ? 'no evidence; untested; run not complete' : 'no evidence; untested';
  }
  const bits = [];
  if (s.agentReportedOnly) bits.push('agent-reported only');
  else {
    const shown = s.classes.filter(c => c !== 'missing');
    if (shown.length) bits.push(shown.join(', '));
  }
  bits.push(s.classes.includes('media') ? 'media present' : 'media none');
  if (s.runsNotComplete.length) bits.push('run not complete');
  else if ((s.runsOtherBuild ?? []).length) bits.push('run other build');
  else if ((s.runsComplete ?? []).length) bits.push('run complete');
  else bits.push('run none');
  return bits.join('; ');
}

function notIndependentlyRecorded(op) {
  return op.evidence.length > 0 && op.support?.agentReportedOnly === true;
}

function formatRun(e) {
  if (!e.run) return '';
  if (!e.run.found) return `\n      run: missing (${e.run.reason})`;
  const sess = e.run.session;
  const sessionBit = sess
    ? ` session=${sess.serverStartedUtc ?? 'n/a'} gen=${sess.generation ?? 'n/a'} sample=${sess.sampleHash ?? 'n/a'}`
    : '';
  return `\n      run: status=${e.run.status ?? 'unknown'}${sessionBit} agentReported=${e.run.agentReported}`;
}

function renderInspect(r) {
  const L = [];
  L.push(`${r.recipeId} (rev ${r.revision})  ${r.title}`);
  L.push(`family ${r.family.id} (${r.family.title})  kind ${r.kind}  captured ${r.captured}`);
  L.push(`summary: ${r.summary}`);
  L.push('identities:');
  for (const it of r.identities) L.push(`  ${it.ref}: ${it.domain} ${it.value}${it.name ? ' ' + it.name : ''}  - ${it.role}`);
  L.push('relationships:');
  for (const rel of r.relationships) L.push(`  [${rel.status}] ${rel.from} --${rel.role}--> ${rel.to}  (${rel.provenance.method})`);
  L.push(`requirements: side=${r.requirements.side}; world=${r.requirements.world}`);
  for (const [k, v] of Object.entries(r.requirements.context)) L.push(`  ${k}: ${v}`);
  L.push('  initialization:');
  r.requirements.initialization.forEach((s, i) => L.push(`    ${i + 1}. ${s}`));
  L.push('  cleanup:');
  for (const s of r.requirements.cleanup) L.push(`    - ${s}`);
  L.push('controls:');
  for (const c of r.controls) {
    L.push(`  ${c.name}: ${c.tested.length ? c.tested.map(t => `tested ${JSON.stringify(t.value)} [${t.applicability}; evidence ${t.evidence.join(',')}]`).join('; ') : 'untested'}`);
    L.push(`    limitations: ${c.limitations}`);
    if (c.implementation) L.push(`    implementation: ${c.implementation}`);
  }
  L.push('operations:');
  for (const [k, op] of Object.entries(r.operations)) {
    L.push(`  ${k.padEnd(16)} ${op.status.padEnd(17)} [${formatSupport(op)}] sides=${(op.sides ?? ['server', 'client']).join(',')} applicability=${op.applicability}${op.evidence.length ? ' evidence=' + op.evidence.join(',') : ''}  ${op.note}`);
    if (notIndependentlyRecorded(op)) L.push('    not independently recorded');
  }
  const bs = r.buildSummary;
  if (bs) L.push(`build summary: stamped=${bs.stamped} unstamped=${bs.unstamped} stale=${bs.stale} matchesCatalog=${bs.matchesCatalog}`);
  L.push('evidence:');
  for (const e of r.evidence) {
    const fwSides = e.stamps.frameworkSides
      ? ` frameworkSides server=${e.stamps.frameworkSides.server.state} client=${e.stamps.frameworkSides.client.state}`
      : '';
    L.push(`  ${e.id} ${e.kind} ${e.date ?? 'date unknown'} class=${e.provenanceClass} media=${e.media.present === null ? 'none' : e.media.present ? 'present' : 'MISSING'} applicability=${e.applicability} stamps: catalog=${e.stamps.contentCatalogSha256.state} game=${e.stamps.gameIdentity.state} framework=${e.stamps.framework.state}${fwSides}\n      ${e.summary}${e.path ? '\n      ' + e.path : ''}${formatRun(e)}`);
  }
  L.push('implementation:');
  for (const im of r.implementation) L.push(`  ${im.path}${im.symbol ? ' :: ' + im.symbol : ''}  [${im.status}; file ${im.file.present ? 'present' : 'MISSING'}]`);
  L.push('limitations:');
  for (const s of r.limitations) L.push(`  - ${s}`);
  L.push(`inventory: ${r.inventory.state}${r.inventory.generation ? ' (generation ' + r.inventory.generation + ')' : ''}${r.inventory.reason ? ' - ' + r.inventory.reason : ''}${r.inventory.source ? ' catalog sha256 ' + r.inventory.source.catalogSha256 : ''}${r.inventory.note ? '\n  note: ' + r.inventory.note : ''}`);
  L.push('inventory associations:');
  if (r.inventoryAssociations.length === 0) L.push('  (the recipe lists no asset references)');
  for (const a of r.inventoryAssociations) {
    L.push(`  ${a.name}${a.unityType ? ' (' + a.unityType + ')' : ''}: ${a.status}  ${a.note}`);
    for (const c of a.candidates) L.push(`      object:${c.archiveId}/${c.pathID} ${c.type}`);
  }
  const curSides = r.currentBuild.frameworkSides ?? {};
  L.push(`current build (offline view): catalog=${r.currentBuild.contentCatalogSha256 ?? 'unknown'} game=${r.currentBuild.gameIdentity ?? 'not observable offline'} framework=${r.currentBuild.framework ?? 'not observable offline'} frameworkSides server=${curSides.server ?? 'not observable offline'} client=${curSides.client ?? 'not observable offline'}`);
  L.push(`current verification claimable: ${r.currentVerification.claimable} - ${r.currentVerification.reason}`);
  L.push(`runtime availability: ${r.runtimeAvailability.state} - ${r.runtimeAvailability.reason}`);
  return L.join('\n');
}

function main() {
  const a = parseArgs(process.argv);
  if (!a.command || a.command === 'help') { usage(); return 0; }
  if (a.command === 'validate') {
    const result = validateRecipeDirectory(a.recipes);
    emit(result, a.text, r => {
      const lines = r.report.map(x => `${x.valid ? 'ok     ' : 'INVALID'} ${x.file}${x.problems.map(p => '\n    ' + p).join('')}`);
      for (const p of r.directoryProblems) lines.push(`INVALID ${p}`);
      return lines.join('\n');
    });
    return result.invalid ? 4 : 0;
  }
  const store = loadRecipes(a.recipes);
  if (a.command === 'list') {
    emit({ dir: store.dir, count: store.recipes.length, recipes: listRecipes(store) }, a.text, r => r.recipes.map(x => `${x.recipeId.padEnd(34)} ${x.family.padEnd(20)} ${x.kind.padEnd(26)} rev ${x.revision}  captured ${x.captured}`).join('\n'));
    return 0;
  }
  if (a.command === 'search') {
    if (a.positional.length !== 1) throw new RecipeError('invalid-query', 'search needs exactly one query argument (quote it when it contains spaces)');
    const inventory = a.inventory ? loadInventory(a.generated) : undefined;
    emit(searchRecipes(store, a.positional[0], { limit: a.limit, includeInventory: a.inventory, inventory }), a.text, renderSearch);
    return 0;
  }
  if (a.command === 'inspect' || a.command === 'associations') {
    if (a.positional.length !== 1) throw new RecipeError('invalid-recipe-id', `${a.command} needs exactly one recipe id`);
    const inventory = loadInventory(a.generated);
    const r = inspectRecipe(store, a.positional[0], { inventory });
    if (a.command === 'associations') {
      const lines = renderInspect(r).split('\n');
      const start = lines.findIndex(l => l.startsWith('inventory:'));
      const end = lines.findIndex(l => l.startsWith('current build'));
      emit({
        recipeId: r.recipeId,
        inventory: r.inventory,
        inventoryAssociations: r.inventoryAssociations,
        currentVerification: r.currentVerification,
        runtimeAvailability: r.runtimeAvailability,
      }, a.text, () => lines.slice(start, end).join('\n'));
    } else emit(r, a.text, renderInspect);
    return 0;
  }
  throw new RecipeError('invalid-command', `unknown command '${a.command}'`, { command: a.command });
}

try {
  process.exitCode = main();
} catch (e) {
  if (e instanceof RecipeError) {
    console.log(JSON.stringify(e.toJSON(), null, 2));
    process.exitCode = e.code === 'unknown-recipe' ? 3 : (e.code === 'invalid-recipe-file' || e.code === 'duplicate-recipe-id' || e.code === 'recipes-missing') ? 4 : 2;
  } else {
    console.log(JSON.stringify({ error: { code: 'unexpected', message: e.message } }, null, 2));
    process.exitCode = 5;
  }
}
