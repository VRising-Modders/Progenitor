## What changed

<!-- One coherent change. Link the issue it addresses, if any. -->

## How it was checked

<!-- Built, tested offline, or observed in the game (side, game build). State what was not tested. -->

- [ ] `dotnet test tests/Progenitor.Tests` and `dotnet test tests/Progenitor.Testing.Tests` pass
- [ ] Built the projects CI cannot build (Core, Bootstrap, Dev, samples) against a local game install, if touched
- [ ] Observed in the game (say which side, and on your own client or test server only)

## Checklist

- [ ] Docs under `docs/` updated in this pull request (API status, module reference, tutorial), with honest verification labels (developer docs only)
- [ ] Help guide (`docs/guide/progenitor-guide.html`) updated for any user-facing feature added, changed or removed: Getting Started for basics, the reference chapters for how-to and settings; it describes behavior only, with no testing or verification status
- [ ] Keys go through `ctx.Keybinds`; nothing polls input directly
- [ ] Unload releases everything this change registers
- [ ] Original code only; no game files, interop assemblies or personal identifiers committed

## Knowledge note

<!-- Did this work hit a trap that cost real time and whose fix you saw work in the game? Add a note in this PR (knowledge/README.md). -->

- [ ] Added or updated a knowledge note
- [ ] No note needed
