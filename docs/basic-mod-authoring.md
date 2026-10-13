# Basic mod authoring improvements

**Planned; acceptance not run.** Extend existing Progenitor services rather than introduce parallel frameworks. These requirements address common authoring tasks; they do not claim complete game API coverage. Exact new member names remain a design decision, not callable examples.

Implement independently from game contracts and Progenitor's own code, research and tests. Do not copy or translate other mods' implementation code, algorithms, tests or documentation, and do not add them as dependencies.

Keep portable ownership, scheduling, identity and capability contracts in Progenitor; put V Rising prefab names, components, application rules and engine integration in Progenitor_VRising. Do not force other games to use V Rising's buff or ECS model.

## A1 — Complete buff lifecycle (first priority)

Extend the existing buff-definition path with a supported apply/query/remove workflow and a small documented set of stat modifiers. Authors should not need DebugEventsSystem, raw component writes or private Sample internals for a basic timed buff. Define duration, refresh versus stacking, application source, invalid targets, death/disconnect and unload behavior explicitly. Return an owned application handle and structured outcomes; a requested application is not confirmed active.

Use a verified vanilla base when required by the adapter, and explain client requirements for custom identity/icon/description. Validate target, game readiness and authority before changing state. Cleanup removes only effects owned by that application, restores contributions without overwriting another mod's changes, and is idempotent. Unsupported combinations refuse with an actionable reason. Do not promise safe arbitrary component mutations.

Acceptance: a visible timed stat buff; expiry and manual removal; refresh/stack policy; two owners affecting the same stat; rejected application; target death/disconnect; plugin swap/removal. Verify gameplay value, UI where supported and absence of residual effects. Document the exact supported modifiers.

## A2 — Convenient owned creature spawning (first priority)

Build on ctx.Units and its handles. Add or document one bounded workflow for spawning an existing creature with optional lifetime and verified common options. Distinguish requested, initialized/usable, failed and despawned states; callback timing must be explicit. Do not report successful initialization merely because an entity was allocated. Support cancellation and cleanup if a world changes or the owner unloads during initialization.

Do not silently add flight, faction, invulnerability or other gameplay changes. Preserve vanilla behavior unless the caller chooses a supported option. Keep follow/move/despawn through existing handles; arbitrary behavior-tree edits and boss possession are outside this scope.

Acceptance: known creature spawns near a connected player, remains for the requested lifetime, then disappears; early disposal and unload leave no orphan. Verify invalid prefab, unavailable world, canceled initialization, player-range limitations and repeated spawn/cleanup. Persistent spawning requires a separate explicit contract.

## A3 — Player lookup and common operations

Consolidate existing lookup helpers behind a documented player directory: stable identity, display name, online state, and adapter-supported lookup by account identity or name. Duplicate/ambiguous names return candidates, not an arbitrary match. Cached results report freshness and invalidate on join, disconnect, rename, world replacement and reload. A stable identity is not a persisted raw entity handle.

Offer narrowly scoped common actions by composing existing services, starting with buff targeting and read-only inventory information where supported. Offline identity discovery does not imply an operable character. Enforce execution-side and server authority rules. Player-MCP exposure must use the separate permission-aware capability registry; internal developer access is not player authorization.

Acceptance: duplicate names, reconnect/rename, offline records, destroyed character, world replacement and forbidden player-facing query; no stale entity used for mutation.

## A4 — Scheduled actions and watched configuration

First audit existing scheduling and file-watch APIs. Fill only evidenced gaps: owner-bound cancellation, one-shot/repeating work, defined clock and pause behavior, exception isolation, and no callbacks after unload. File changes should be debounced and parsed away from game-thread mutation; apply validated changes on the supported game thread. Invalid files retain last-good state and return a useful error. Do not replay missed scheduled actions after downtime unless explicitly requested.

Acceptance: timer cancellation, reload while work is queued, malformed and partial file writes, rapid updates, and callback failure. Reuse existing services when these cases already pass.

## A5 — Mod data policy for new worlds and server wipes (later)

Delivered first (2026-10-07): the default split. World state in `ctx.Save` is archived by a wipe and rolled back by a restore; data meant to survive wipes goes in `ctx.ServerSave` (`<persistentDataPath>\progenitor-server`), which neither touches ([Where should my data go?](modules.md#where-should-my-data-go)). Still open:

Provide an opt-in policy for a mod's own stored data: preserve, migrate, or reset when a reliably identified new-world/wipe event occurs. Define identity and evidence before implementing detection. A missing/unreadable save or changed folder alone must not trigger destructive reset. Preserve a recoverable copy and restrict operations to the registered mod namespace; never erase vanilla saves or another mod's data. Report uncertain identity and defer reset.

Acceptance on disposable fixtures: ordinary restart, new world, restored backup, copied/renamed save directory, uncertain identity and interrupted reset. Distinguish this small policy feature from the separate post-MVP world-salvage project.

## A6 — Beginner and AI acceptance

Deliver two small standalone, buildable examples: temporary creature spawn and timed custom stat buff. Each includes setup, supported execution side, UI/command entry point using one shared handler, expected observations and cleanup. Public docs and developer discovery should lead to these examples without private research notes. Keep unsupported operations explicit; the developer MCP and player MCP remain distinct.

Give a fresh Sonnet agent only the public documentation, supported developer tools and each task. It must build, exercise, inspect and clean up both examples on an isolated test pair without expert hints, raw memory/native ABI experiments or copying framework samples wholesale. Record interventions, wrong API guesses and missing discoverability externally. Repeat after fixes; two independent successful runs are the acceptance target, not a guarantee about every model or mod.

## Delivery order

Develop the core authoring API in parallel with mod-manager acceptance, with separate file ownership and one coordinated owner of live client testing. Prioritize A1/A2 with A6, then A3; audit A4 and close gaps required for the supported beginner workflows. Use small disposable acceptance plugins, not an existing-mod migration, to prove these contracts. Include failure, cleanup, reload and required client/server cases. Keep A5 later.

The migration gate is documented, buildable and live-validated support for those core workflows, plus the fresh-agent acceptance in A6. It does not require exposing every game asset/function or finishing experimental possession, world placement and save salvage. After the gate passes, migrate an existing mod as broader integration validation. Track new work as planned until demonstrated; existing low-level capabilities retain their narrower verification labels.


## Players, combat events, and spawn options

Use `ctx.Players` for online lookup, subscribe to combat events via `ctx.Events` / the combat bus (`UnitDied`, `PlayerDied`, `DamageDealt`), and pass `UnitSpawnOptions` (`LifetimeSeconds`, `Stats`, `StripVBlood`, `Configure`) when a spawn should differ from the prefab.
