# Catalog usage examples

Offline Node scripts. They import [`tools/mcp/catalog-tools.mjs`](../../mcp/catalog-tools.mjs) and print the answer an agent should give after `search` then `inspect`. They do not load assets or start the game.

Working directory: the repository root. Prerequisites: Node 18+. Cleanup: none.

```
node tools/catalog/examples/find-green-nova.mjs
node tools/catalog/examples/charm-particles-vs-beam.mjs
```

| Script | Question |
|---|---|
| [`find-green-nova.mjs`](find-green-nova.mjs) | Find a green nova effect and show the supported way to use it. |
| [`charm-particles-vs-beam.mjs`](charm-particles-vs-beam.mjs) | Tell Charm particles from the beam (three recipes). |

Shared printer: [`print-recipe.mjs`](print-recipe.mjs) (support lines match `inspect --text`). Walkthrough, including captured output: [`docs/catalog-usage.md`](../../../docs/catalog-usage.md). Command reference: [`../README.md`](../README.md#recipe-layer-maintained-usage-recipes).
