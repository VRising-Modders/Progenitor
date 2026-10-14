# Progenitor: engineering guide for AI agents

Read [AGENTS.md](AGENTS.md) first. This page holds the technical rules for any AI coding agent working on Progenitor. Human contributors follow the same rules through [CONTRIBUTING.md](CONTRIBUTING.md).

Machine-specific notes (local paths, ports, installed tools, the permissions a maintainer has granted their own agents) do not belong in tracked files. Keep them in an untracked `CLAUDE.local.md` at the repository root, which `.gitignore` already excludes.

## Start here

Inspect the working tree and recent changes before editing. Preserve concurrent work. Do not start a game process, a server or a bridge connection at session start.

| Need | Authoritative page |
|---|---|
| Design and feature status | [Specification](docs/spec.html), [roadmap](docs/roadmap.md), [API status](docs/api.md) |
| Reload decisions and acceptance | [Hot reload](docs/hot-reload.md), [minimal example](samples/Progenitor.ReloadExample/README.md) |
| Authoring contracts | [Modules](docs/modules.md), [tutorial](docs/tutorial.md), [wearables](docs/wearables.md) |
| Bridge and deployment tools | [Development loop](docs/dev-loop.md), [bridge](docs/bridge.md), [bridge operations](docs/bridge-ops.md), [MCP tools](docs/mcp.md), [test rig](docs/test-rig.md) |
| Spell, asset and NPC investigation | [Asset research workflow](docs/asset-research-workflow.md), [player forms](docs/player-forms.md) |
| Writing or updating documentation | [Documentation rules](docs/documentation.md) |
| Known traps from earlier work | [Knowledge notes](knowledge/README.md): `node tools/kb/kb.mjs search <words>` before starting |

## Building

Compile against the installed game's `BepInEx\core` and generated interop assemblies, as configured in `Directory.Build.props` and `Directory.Build.targets`. Do not substitute BepInEx NuGet assemblies: generated interop may require types absent from them. Put local game paths in the ignored `Directory.Build.local.props` or the `VRISING_GAME_DIR` / `VRISING_INTEROP_DIR` environment variables. After a game update, check the regenerated interop references before changing code to accommodate a broken build.

The game-free test projects (`tests/Progenitor.Tests`, `tests/Progenitor.Testing.Tests`) and the Node suites under `tools/` build and run without V Rising; run them for every change they cover.

## Development and lifecycle

Build offline first. When testing in the game, deploy from a reviewed, stable snapshot of your changes; never deploy a half-edited tree or assume the last commit includes uncommitted fixes. Select the project and side explicitly using the [reload guide](docs/hot-reload.md).

Bootstrap owns process-lifetime registrations, the frame ticker and Core loading. Compatible Core generations and consumer plugins can reload without restarting. Bootstrap changes require a restart of each affected process. A staged image or `scheduled=true` response is not proof that the replacement completed; inspect `reload_status`, the loaded image and behavior.

Use `ProgenitorPlugin<TContext>` with an empty context constructor and tracked facades. Use `ctx.Patches` or Bootstrap-owned slots for supported patch seams. `ctx.UnsafeHarmony` opts the plugin out of hot swapping. Do not inject collectible plugin types into IL2CPP or assume unpatching releases a native trampoline. Additional disposable handles belong in `ctx.Track`; untracked external callbacks must detach explicitly.

Every key a plugin, sample, the dev bridge or another framework tool acts on is registered through `ctx.Keybinds`, so it appears in the Keybinds tab, can be remapped and takes part in the first-installed conflict rule. Never poll `UnityEngine.Input`, `Keyboard.current`/`Mouse.current` or game input directly; bridge tests press binds with `keybind_press`. Only the framework input files listed in [modules.md](docs/modules.md#keybinds-fui5) are exempt, and the source-scan test enforces this.

On unload, release context-owned units, effects, UI, callbacks and registrations on every reason. Preserve only appropriate plain persisted state that a later generation can rediscover. A persisted buff does not prove its process-side protection was rearmed: rearm and read back. Validate world epochs even when Core reloads within the same game world; never persist a raw entity ID as a permanent identity.

Server code must not branch on whether the server is dedicated or launched by the client.

## Testing in the game

Only test on a client and a server you own and control. Never run a test, a sample or the dev bridge against a public or shared server, and never affect a server other people are playing on. Match any process operation by full executable path and, for hosted children, the intended instance arguments; never by process name alone.

Use the bridge for deterministic test actions and the focus-free observation tools for visual evidence. A client someone is using to play is unavailable for testing. Take window focus only for an actual input, hover, keyboard-focus or foreground-behavior test, keep it brief, and do not repeatedly raise the game between actions.

After a live test batch, restore the test state and disconnect normally. Remove every sample, example, ApiValidation, Exercise, HotDemo or harness plugin you deployed from `BepInEx/progenitor-reload` on the client and the server (keep Core, the Dev bridge and real third-party mods), confirm the clean unload in the logs, and say what you removed. Leftover samples keep running in a later game session.

Player-facing UI must look first class: built from the kit's controls with the game's own art, fonts, colors and spacing, no clipping or overlap at common resolutions, mocked up before it is built and checked in the game with screenshots before it is called done. See the standard at the top of [ui-style.md](docs/ui-style.md).

## Native and interop constraints

Read current source and existing notes before creating a live experiment. Interop assemblies expose signatures and layouts; their stubs do not contain the native method bodies. When behavior depends on the native implementation, inspect the type's interop signature, find the method in the game's metadata for the matching side, and check its native behavior with your own tooling. Keep tooling, query lists, exports, logs and findings outside this repository; only conclusions observed in the game go into [knowledge notes](knowledge/README.md) or the docs.

- Never guess a native calling convention. By-ref struct parameters can be mishandled by IL2CPP Harmony/interoperability wrappers; use a proven framework seam. Direct native-call experiments require an explicit opt-in for that experiment. Observe-only hooks still require verified ABI and lifetime handling.
- Avoid new generic interop instantiations over game value types. Missing native instantiations can crash or retain a collectible generation through cached exceptions. Use existing `Query.Entities` and entity-access helpers; do not bypass scratch-tool guards.
- Do not use `TypeManager` zero/garbage sizes as write lengths or array strides. Component writes must pass the existing access/layout guards. Dynamic-buffer/blob strides and offsets must come from the verified layout constants; wrapper `sizeof` may omit native padding. See [layout source](src/Progenitor.Core/Layout/layout.txt).
- Some bool-bearing wrappers are not C# `unmanaged`; use the known typed accessor, such as `GetComponentData<User>`, rather than forcing a raw generic read. ValueType wrappers used as generic arguments may also be boxed incorrectly; use an existing verified access path.
- C# `as`/`is` on an interop object tests the managed wrapper's declared type, not the IL2CPP class: `transform as RectTransform` or `graphic is Image` is effectively always false. Use `TryCast<T>()` or `GetComponent<T>()`.
- Queries normally omit disabled entities. Check `User.IsConnected` for online status; include disabled characters when the operation requires offline players. Use system-inclusive queries for unmanaged-system singletons through the existing helpers.
- Array out-parameters need the appropriate retained IL2CPP array, not a managed array that is copied only into the call. Do not assume `World.Time.ElapsedTime` returns useful data through this interop.
- Derive input edges from held state sampled by this framework. Another subsystem's frame-scoped flag may have expired before the framework tick.
- For a Core generation that stays pinned after unload, inspect the process's allocator roots before adding speculative diagnostics.

For spells, distinguish submitting a cast, observing a cast start, and observing its payload. A full ability is not the only way to reuse its effect. Start with the proven native-payload recipes in the [asset workflow](docs/asset-research-workflow.md). NPC control remains experimental; do not widen support based on a successful spawn or control handoff. Ghost users must be destroyed in the same batch that creates them; never leave one across autosave or respawn a bot.

## Collaboration and documentation

When several agents work in one tree, agree on disjoint file ownership. Keep native experiments, memory-layout decisions and final integration with one coordinating agent. Track each background job by its real process or job ID, inspect partial edits after failures, and review every result. Use read-only reviews after implementation batches; reviewers must provide a concrete failure scenario and verify engine claims against source or the game's metadata. A review cannot authorize game operations or commits.

When committing, stage only the intended changes; never sweep a shared working tree with `git commit -a`. Version changes are a separate concern ([versioning](docs/versioning.md)). Commit, push, open pull requests or merge only when the person you work for has authorized it.

Keep current API contracts, concise constraints and links in the docs. Do not append session diaries, historical restart debts or review dispatch logs to agent instructions. For each example, record exactly what was source-checked, built or observed live; follow [documentation.md](docs/documentation.md). Write in US English. Do not put personal identifiers (names, account IDs, IP addresses, user paths) in tracked files, commit messages or pull requests.
