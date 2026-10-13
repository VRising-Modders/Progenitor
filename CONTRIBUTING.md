# Contributing to Progenitor

Thank you for helping. Progenitor is a V Rising modding framework; most changes touch code that runs inside the game, so this guide covers what you need installed, how to build and test, and the rules that keep hot reload and the game stable. Everyone taking part follows the [code of conduct](CODE_OF_CONDUCT.md). Report security problems privately as described in [SECURITY.md](SECURITY.md), not in an issue.

If you use an AI coding agent, point it at [AGENTS.md](AGENTS.md) and [CLAUDE.md](CLAUDE.md); they carry the same rules in more technical detail.

## Prerequisites

| What | Needed for |
|---|---|
| .NET 8 SDK | Everything |
| Node.js 18 or newer | The knowledge-note tool and the MCP server under `tools/` |
| V Rising with **BepInExPack_V_Rising** (BepInEx 6, IL2CPP), launched once | Building Core, Bootstrap, Dev, the preloader and the samples, and every in-game test |

The first game launch with BepInEx generates `<game>\BepInEx\interop`, the managed interop assemblies Progenitor compiles against. They are never committed or redistributed. Do not replace them with BepInEx packages from NuGet: the generated interop may need types those packages lack.

If the game is not in the default Steam folder, set `VRISING_GAME_DIR` (and optionally `VRISING_INTEROP_DIR`) or create a git-ignored `Directory.Build.local.props` at the repository root:

```xml
<Project>
  <PropertyGroup>
    <VRisingGameDir>D:\Games\VRising</VRisingGameDir>
  </PropertyGroup>
</Project>
```

## Build and test

```
dotnet build
dotnet test tests/Progenitor.Tests
dotnet test tests/Progenitor.Testing.Tests
node tools/kb/kb.mjs check
node --test "tools/kb/test/*.test.mjs"
```

The two test projects and the Node suites are game-free: they build and run without V Rising. `dotnet build` of the whole solution, and of Core, Bootstrap, Dev and the samples, needs the game's interop assemblies. For the MCP server tests, run `npm ci` in `tools/mcp` first, then the test files CI lists (`node --test test/kb-tools.test.mjs test/identity.test.mjs …` there; see the workflow for the current list).

**CI runs only the game-free checks** (`.github/workflows/tests.yml`): both test projects, the knowledge-note checker and tests, the MCP tool tests, and a check that every `<Compile Include>` path resolves at every commit. CI has no game install, so it cannot build Core or the samples and cannot test anything in the game. Build those locally and say in your pull request what you built and ran.

### Testing in the game

The [development loop](docs/dev-loop.md) covers deploying into a client and a local test server; the [debug bridge](docs/bridge.md), its [operations](docs/bridge-ops.md) and the [MCP tools](docs/mcp.md) have their own pages. The [hot-reload guide](docs/hot-reload.md) covers which changes swap in place and which need a restart.

- **Only test on your own client and on a server you run yourself**: a private game hosted from your client, or a throwaway dedicated server staged with `tools/stage-test-server.ps1`. Never test against a public or shared server, and never deploy samples or the dev bridge (`Progenitor.Dev.dll`) to one.
- After testing, remove the samples and test plugins you deployed from `BepInEx/progenitor-reload`.

## Coding rules

These apply to every contributor, human or AI.

- **Original code only.** Do not copy, translate or adapt code from other modding frameworks or mods, whatever their license. BepInEx is the only runtime dependency; propose a new dependency in an issue first.
- **Register through `ctx`.** Plugins subclass `ProgenitorPlugin<TContext>` with an empty context constructor and register work through the context facades, so unload can reverse it. Additional disposables go in `ctx.Track`; external callbacks that are not tracked must detach explicitly.
- **Unload cleanly on every reason.** Release units, effects, UI, callbacks and registrations. Keep only plain persisted state a later generation can rediscover. Never persist a raw entity ID as a permanent identity; stamp entities with the world epoch.
- **Patches through `ctx.Patches`.** `ctx.UnsafeHarmony` opts the plugin out of hot reload. Do not inject collectible plugin types into IL2CPP.
- **Keys through `ctx.Keybinds`.** Every key a plugin, sample or tool acts on is registered there, so players can see and remap it. Never poll `UnityEngine.Input`, `Keyboard.current`, `Mouse.current` or game input directly; a source-scan test enforces this.
- **Interop care.** Never guess a native calling convention. Avoid new generic interop instantiations over game value types. Write components only through the existing access and layout guards. Use `TryCast<T>()` or `GetComponent<T>()` instead of C# `as`/`is` on interop objects. [CLAUDE.md](CLAUDE.md#native-and-interop-constraints) lists the full set.
- **No dedicated-versus-hosted branches.** Server code behaves the same on a dedicated server and in a private game hosted from the client.
- **Bootstrap is special.** Core and plugins hot reload; a Bootstrap change needs a game restart. Keep process-lifetime work in Bootstrap small.
- **Docs in the same change.** Update the affected page under `docs/` (API status, module reference, tutorial, game defaults you discovered) in the same pull request, following the [documentation rules](docs/documentation.md). Label claims honestly: source-checked, compile-verified or verified live.
- **US English** in code, UI strings and docs (color, gray, center).
- **No game files.** Do not commit game assets, binaries, interop assemblies or native code, and do not put personal identifiers (names, account IDs, IP addresses, local paths) in tracked files.

## Share what you learned: knowledge notes

Modding V Rising means running into engine traps: a component that has to be present, an interop wrapper that crashes, an input read differently than you expect. When you hit one and prove the fix in the game, write it down so the next modder, or their AI agent, does not lose the same hours. Notes live in [knowledge/](knowledge/README.md), and agents search them before starting work.

**The bar is "observed in the game."** A note needs the exact game build, the side, the evidence, and an honest statement of what was and was not tested. Guesses and untested ideas do not qualify. The full standard is in [knowledge/README.md](knowledge/README.md#what-qualifies).

There are two ways to contribute one:

- **In a pull request to Progenitor.** If your change came with a trap worth keeping, add a note in the same pull request:

  ```bash
  node tools/kb/kb.mjs new <slug> --title "<the finding in one line>" --area <area> --tags <a,b>
  ```

  Fill it in, run `node tools/kb/kb.mjs index` and `node tools/kb/kb.mjs check`, and commit it with your change.

- **From your own mod project.** Open a [Knowledge note issue](../../issues/new?template=knowledge-note.yml). A maintainer adds it with status `reported`, then marks it `verified-live` after reproducing it.

## Pull requests

- Open an issue first for a large change or a new dependency, so the design can be agreed before you write it.
- Keep each pull request to one coherent change, with tests where the code is testable without the game.
- Fill in the [pull request template](.github/pull_request_template.md): say exactly what you checked (built, tested offline, or observed in the game, with the side and game build) and what you did not test.
- Make sure the CI checks pass, and run the local build for anything CI cannot build.
- Do not bump version numbers; versions are computed from git ([versioning](docs/versioning.md)).
- A sign-off line (DCO) is not required.

By contributing, you agree that your contribution is licensed under the repository's license, LGPL-2.1-only.

## How contributions get in

Anyone can contribute without asking first:

1. Fork the repository on GitHub (the **Fork** button), or click the pencil icon on a file for a small fix; GitHub creates the fork for you.
2. Make the change on a branch in your fork.
3. Open a pull request from your fork to this repository's `main`.

A maintainer reviews it, asks for changes if needed, and merges it or explains why not. Merged commits keep your name. Only maintainers can push to this repository or merge; `main` is protected, so every change, including a maintainer's, goes through a pull request.

## Becoming a maintainer

Maintainers review and merge other people's pull requests, triage issues, and help decide the project's direction. GitHub has no way to apply to an organization; maintainers are invited. The path is:

1. **Contribute.** Several merged pull requests over time, plus helpful reviews or issue triage, show how you work. Quality and care for the rules above matter more than volume.
2. **Ask.** When you have a track record, say you'd like to help maintain: open an issue titled "Maintainer request" or ask an existing maintainer. Mention what you'd like to look after (for example the Mods menu, samples, or docs).
3. **Get invited.** Existing maintainers decide together. New maintainers usually start with write access to this repository (review and merge), and take on more over time.

Maintainers are expected to:

- review and merge through pull requests, never push straight to `main`;
- build and test locally anything CI cannot build (Core, Bootstrap, Dev and the samples need a V Rising install);
- follow the [code of conduct](CODE_OF_CONDUCT.md) and handle security reports privately ([SECURITY.md](SECURITY.md));
- step back or hand an area over when they no longer have time; stepping down is normal and welcome.

Maintainer access can be removed for inactivity (after asking) or for breaking the code of conduct.
