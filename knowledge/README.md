# Knowledge notes

Short notes one contributor (human or agent) leaves for the next. Each note records one trap that cost real time in work that was tested in the game: the exact game build, the route taken and why, what the engine really did, how it was verified, and the gotchas as symptom, cause and fix. Browse [INDEX.md](INDEX.md).

Notes are not a wiki of the game and do not replace the authoritative pages. API contracts stay in [docs/](../docs/api.md); investigations, logs and captures stay with whoever ran them. A note is the two-minute summary that keeps the next person out of the same trap, with a link to the evidence.

## Search before starting work

```bash
node tools/kb/kb.mjs search coffin exit
```

Agents connected to the Progenitor MCP server (`tools/mcp`) can call `progenitor_kb_search` and `progenitor_kb_read` instead. Every query word must match somewhere in a note; title and tag matches rank first. Each result shows its status, side and game build. A note for an older game build may no longer hold.

## What qualifies

Add a note only when all of these hold:

1. **It was observed.** You saw it happen in the game, or saw a route fail in the game. Reading source or native code and "this should work" do not qualify; keep them in your own research until someone tests them.
2. **The evidence is pinned.** Exact game build, the Progenitor commit or PR, the side (client, server or both), the date, and a pointer to the evidence: a screenshot, a component read-back, a log excerpt or an issue.
3. **It is scoped honestly.** The Verification section says what passed and what was not tested.
4. **It is a real trap.** At least one symptom, cause and fix that cost real time. "It worked first try" needs no note.
5. **It is short and clean.** Under 150 lines. No game files, no native code, nothing that helps cheat against other players.

Statuses:

| Status | Meaning |
|---|---|
| `verified-live` | Observed in the game as described |
| `failed` | A route tried in the game that did not work; say why |
| `compile-verified` | Build or tooling gotchas only (`side: offline`); game behavior needs `verified-live` |
| `reported` | An outside contributor's observation that a maintainer has not reproduced yet. A maintainer changes it to `verified-live` after reproducing it on a test rig |

If a trap can become a check, a guard or a test instead, do that; a note is for what cannot.

## Adding a note

Add the note in the same pull request as the change it describes. Review of that pull request is the review of the note.

1. Create the skeleton. It stamps the installed Steam build and the current commit:

   ```bash
   node tools/kb/kb.mjs new coffin-exit-respawn-input --title "Leaving a coffin is the Respawn input" --area bots --tags coffin,input
   ```

2. Fill in every section and replace each `<placeholder>`.
3. Regenerate the index:

   ```bash
   node tools/kb/kb.mjs index
   ```

4. Check, which CI also runs:

   ```bash
   node tools/kb/kb.mjs check
   ```

Update an existing note rather than adding a near-duplicate. When a later game build or Progenitor change invalidates a note, correct it or remove it in the change that invalidates it.

Found a trap while building your own mod outside this repository? Open a **Knowledge note** issue with the same fields; a maintainer will add it as `reported`. See [CONTRIBUTING.md](../CONTRIBUTING.md).

## Format

Front matter is one `key: value` per line. Lists are comma-separated.

| Field | Meaning |
|---|---|
| `title` | One line that states the finding, not the task |
| `game`, `game_build` | `V Rising`, and the Steam build id, for example `Steam 23521359` |
| `progenitor` | Commit (7+ hex digits) or `PR <n>` the note was written against |
| `side` | `client`, `server`, `both` or `offline` |
| `area` | Index section: `wearables`, `buffs`, `bots`, `ui`, `interop`, ... |
| `tags` | Search words a future reader would type |
| `route` | The API or native path used, in one line |
| `status` | See above |
| `date` | When the observation was made, `YYYY-MM-DD` |
| `evidence` | Comma-separated pointers to the evidence |

Required sections, in order: `## Goal`, `## Route`, `## What the engine did`, `## Verification`, `## Gotchas`. Each gotcha is a `### <short name>` with `- **Symptom:**`, `- **Cause:**` and `- **Fix:**` lines.

`check` enforces the fields, statuses, sections, gotcha lines and length, rejects raw native-code labels, metadata line citations, binary payloads and links to game files, and fails when `INDEX.md` is stale. Tests: `node --test "tools/kb/test/*.test.mjs"`.
