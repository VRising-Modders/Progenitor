# Documentation rules

Write current instructions that a programmer or AI can follow without reconstructing a conversation. These rules apply to future documentation and agent-instruction updates.

## Put information in the right repository

| Keep in Progenitor | Keep in the internal research notes |
|---|---|
| Current API contracts, supported workflows and limitations | Investigations, native-code analysis, experiments and failed approaches |
| Buildable examples and repeatable acceptance procedures | Dated test results, logs, screenshots, dumps and recordings |
| Setup, deployment and troubleshooting instructions | Chronological change narratives and review/fix histories |
| A short reason for a constraint, with an evidence link | Detailed justification, alternatives considered and raw evidence |
| [Knowledge notes](../knowledge/README.md): short records of traps observed in the game, as symptom, cause and fix | The investigation each note points to as evidence |

Product specifications describe intended behavior and clearly label planned functionality. Roadmaps track status. Agent files contain current working rules and pointers; they are not session diaries. New research, spike results and review reports belong outside this repository, including reports that previously went into `docs/spikes` or `docs/reviews`.

## Keep guidance concise and consistent

- Lead with the task and prerequisites. Use numbered steps for procedures, tables for choices, and short paragraphs for explanations.
- Maintain one authoritative page per workflow. Link to it from tutorials, API references and agent files instead of copying its commands and status.
- Describe the final behavior. Remove job IDs, review-by-review narratives, superseded commands and conversational history from operational guidance.
- Explain a hazard in one or two sentences, then link to the research. Preserve the actual limitation when shortening text.
- Use the current API names. When changing a name or contract, search and correct every affected example and summary.
- Keep current user restrictions separate from generic setup. A machine path or previous authorization is not permission to operate a running game in a later session.

## Keep the help guide current

Every PR that adds, changes or removes a user-facing feature (player, admin, or mod author) updates the help guide ([docs/guide/progenitor-guide.html](guide/progenitor-guide.html)) in the same PR: Getting Started for basics only, the reference chapters for full how-to and settings.

The guide describes current behavior only. A new feature still gets its guide pages in its own PR; they say what it does, not how far it was tested. Never add testing or verification status to it ("not yet verified in game", "tested outside the game only", "hasn't been tested on a live server"): that belongs in the developer docs, the PR body and the research notes. A real limitation a player would hit stays, written as plain behavior ("There's no item search yet", "Not available on dedicated servers").

- Each setting gets a row in its feature's table: the label the player sees, the default, what it does. Server-side settings go in the Server Admins chapters.
- Remove what was removed; leave out what is not on main.
- Keep the glossary consistent: every `[[term|...]]` has an entry and every entry is used.
- Edit only the content block (`templates/help/README.md`), then run `node tools/help-guide/sync.mjs --check`.
- `HelpGuideCoverageTests` fails when a row on Progenitor's own page or the Server tab is not named in the guide, or when the guide contains a testing-status phrase.

## Make examples executable and claims verifiable

Prefer one small buildable example per concept. Include its path, prerequisites, exact working directory/commands, expected observations and cleanup. Reuse its source from other pages instead of maintaining divergent code copies. In PowerShell examples, use a JSON file and `@path` for structured bridge arguments; do not rely on escaped inline JSON surviving shell parsing.

Write bridge request files as UTF-8 **without a BOM**; `Set-Content -Encoding UTF8` adds one in Windows PowerShell 5.1 and the bridge caller does not strip it. Use the `WriteAllText` pattern in the reload guide. Explicitly build examples outside the solution before deployment: `-Projects` selects copying, while deployment normally builds the whole solution.

Use these labels accurately in developer docs (`api.md`, `modules.md`, `chat.md` and the other pages under `docs/`), PR bodies and research notes. They never appear in the player and admin help guide, which states current behavior only.

- **Source-checked:** names, arguments or behavior inspected in code. Not a runtime result.
- **Compile-verified:** the exact example built; record the command, source revision/hash and outcome externally.
- **Verified live:** the stated operation was observed in the actual game. Link dated evidence with game/framework identity and tested side. State exactly what passed.
- **Acceptance procedure — not run:** reproducible steps awaiting a test session. Show expected results as expectations, not captured output.

Do not manufacture expected logs as evidence. A successful build does not prove reload, a scheduled swap does not prove completion, and one successful reload does not prove repeated cleanup. Do not carry old verification onto changed code without explaining its scope. Keep experimental steps outside the beginner's verified path.

## Before finishing an update

1. Check the exact commands and API names against current source. Build new/changed examples when possible without operating the game.
2. Check relative links, heading anchors and code fences. Repair links affected by moving sections.
3. Search for contradictory instructions, obsolete API names and stale status claims in the pages that introduce this workflow.
4. Preserve removed investigation/history outside the repository before replacing it; add an external index entry and a clear historical label.
5. Record what was checked and what was not. Never run an unauthorized live test to make a documentation claim green.

The authoritative reload procedure is [hot-reload.md](hot-reload.md). Historical material extracted during this cleanup is indexed in the internal research notes.
