# Module reference

Public API for consumer plugins. Signatures come from `src/Progenitor.Core`. Start with the [hot-reload quickstart](hot-reload.md) and the small [ReloadExample project](../samples/Progenitor.ReloadExample). The larger [Sample project](../samples/Progenitor.Sample) demonstrates content, commands, persistence and gameplay features; each feature's guide states its verification limits.

## Plugins

Use `Progenitor.Plugins.ProgenitorPlugin<TContext>` with `TContext : PluginContext, new()`. Add `[BepInPlugin(...)]` to the concrete plugin class and `[BepInDependency("progenitor")]`. Place the compiled plugin in `BepInEx/progenitor-reload/`.

Implement `OnLoad(TContext ctx)`. `Load` and `Unload` are sealed; the base class owns registration cleanup. Keep the context constructor empty: its logger and API wrappers are attached after construction, before `OnLoad`. Put mutable plugin state in the context.

To publish a mod on Thunderstore (package layout, `progenitor.json`, dependencies, the pre-publish checker), follow [publishing.md](publishing.md).

`[ProgenitorSide(Side)]` selects the process role. `Side.Server = 1`, `Side.Client = 2`, and `Side.Both = Server | Client`; an omitted attribute means `Both`. A mismatch leaves the plugin dormant without creating a context. `ctx.Side` is always `Server` or `Client`. Dedicated and hosted servers use the same server APIs. The server's signed mod catalog lists a `Side.Server` plugin as server-only, and clients never offer to install it. Handshake requirements are unaffected; see [server-only plugins](mod-manifest.md#server-only-plugins).

`[NetworkCompatibility(level, version)]` declares the join rule; the base class calls `ctx.Handshake.DeclareSelf()` after `OnLoad` when the attribute is present. Calling `DeclareSelf` yourself is idempotent. Without the attribute, only plugins that opt in through `Require` or `DeclareSelf` participate in the default exact-match check; extra client plugins are ignored. See [Networking and handshake](#networking).

`[Counterpart(CounterpartNeed.Limited | Optional | Required)]` (SETTINGS1 stage 3) says how much of the mod works without its counterpart on the other side; `Limited` and `Optional` declare a `ServerMustHave` row as `NoNeedForSync` so the mod is never refused or paused for a server that lacks the counterpart. A plugin the player turned off in the Mods menu does not run `OnLoad` at all (nor does one that hard-depends on it); `PluginUnloadReason.Suspend` is also the player's live switch. Rules: [mod-settings.md](mod-settings.md#counterparts), [the switch](mod-settings.md#stage-3-the-enable-switch). A client plugin reads whether its server part runs on the joined server with `ctx.Handshake.ServerPart` (`CounterpartPresence` `Unknown` / `Running` / `Absent`). Reference examples for both directions and for settings scopes: [server-led](../samples/Progenitor.SampleServerLed/README.md) and [client-led](../samples/Progenitor.SampleClientLed/README.md) samples ([mod-settings.md](mod-settings.md#reference-examples-counterparts-and-scopes)).

On a matching side, loading attaches the context, loads and tracks localization files beside the DLL, then calls `OnLoad`. A localization error is logged without failing the plugin. An `OnLoad` exception reverses recorded registrations, including localization, and clears the context.

### Context API

| Member | Purpose |
|---|---|
| `Log`, `Guid`, `Name`, `Version` | Logger and plugin identity. |
| `Config`, `Bind<T>(...)` | Settings in the plugin's `GUID.cfg`; `Bind` supports synchronized settings. See [config sync](config-sync.md). |
| `Events`, `Hooks`, `Systems` | Tracked event subscriptions, ECS update hooks and named virtual-system callbacks. |
| `Commands`, `Chat` | Command registration and server chat sends. Client chat sends return `0` or `false`. |
| `Content` | Item/buff definitions and prefab edits owned by this plugin; `Register()` needs no owner argument. |
| `Save` | Per-save `IModStore` for this plugin GUID; client calls use a no-op store. |
| `ServerSave` | Server-level `IModStore` that survives wipes and world restores ([where should my data go?](#where-should-my-data-go)); client calls use a no-op store. |
| `Net`, `Handshake` | Custom messages and version-handshake registrations. |
| `Features`, `Peer`, `Peers`, `Messages` | Feature negotiation and versioned, tolerant messages between two versions of a mod. See [Feature negotiation and versioned messages](#feature-negotiation-and-versioned-messages-mod-compatibility-c1c2). |
| `Requests`, `Subscriptions` | Typed request and reply, chunked above one packet, and snapshot-then-delta subscriptions to keyed server state. See [Requests, large messages and subscriptions](#requests-large-messages-and-subscriptions). |
| `Castles`, `Effects`, `Units` | World readers and owned effect/unit handles; see their sections for side requirements. |
| `Triggers` | Server area enter and leave for connected players. A circle or an axis-aligned rectangle, both with a finite height. The action can spawn units. See [Triggers](#triggers). |
| `Patches`, `GodMode` | Patches routed through Bootstrap slots and owner-tagged native kill protection. |
| `WorldState`, `Keybinds` | World-load epoch checks and tracked client keybinds; `Keybinds` is the only supported way for a plugin to act on a key (see [Keybinds](#keybinds-fui5)). |
| `Menu`, `Settings` | The Mods-workspace registration (pages, header, groups, server-edit opt-in) and the settings page builder (`Settings.Page`, SETTINGS1 stage 2). Both tracked on every unload reason. See [mod settings](mod-settings.md). |
| `Dialogs` | Modal dialogs in the framework's layout: `Confirm`, `Notice`, `Choose`, `Prompt`, and label/value facts (`DialogFact`, DIALOGS2). Client only; tracked on every unload reason. See [dialogs](ui.md#dialogs). |
| `Time` | Server time of day: read, move or freeze the world clock; clients' lighting follows. See [Time of day](#time-of-day). Also world speed: freeze or slow the whole simulation for every player; see [World speed](#world-speed). |
| `Side`, `IsServer`, `IsClient`, `World` | Process role and its current ECS world, which may be null. |
| `Owner` | The BepInEx plugin instance; pass it to APIs that require an explicit owner, including `MainThread.Post`, `Run` and `PostDelayed`. |
| `Track(IDisposable)` | Dispose an extra resource on unload or failed load. Tracking does not make an otherwise non-reloadable native registration safe. |
| `UnsafeHarmony` | Raw Harmony with this plugin's GUID. Reading it opts the plugin out of in-process swaps; prefer `Patches`. |

`ProgenitorPlugin.WhenWorldReady(ctx, Action<World>)` invokes the callback immediately if the side's world exists, otherwise once on the matching `WorldReady`. It fires at most once. Dispose its returned handle to cancel a pending wait; plugin unload also cancels the tracked subscription.

`Progenitor.Worlds` provides `Find(name)`, `Server` (`"Server"`) and `Client` (`"Client_0"`). Do not retain an entity across a save load without checking its `WorldState` epoch.

<a id="unload-reason-fbuff2"></a>

### Unload reason

Override `OnUnload(TContext ctx, PluginUnloadReason reason)` for game-state cleanup that depends on why the plugin is leaving. The default delegates to the older `OnUnload(TContext ctx)` overload. Choose one overload; the base class does not independently invoke both. Custom cleanup runs before tracked resources are disposed in reverse registration order.

| Reason | Trigger | Persistent game state |
|---|---|---|
| `Swap` | Core generation change or an allowed replacement of the plugin DLL. | Keep state that the new plugin can rediscover, such as its persisted buff instances or save rows. |
| `Removal` | Plugin DLL removed, or replacement rejected by the allow policy. | Undo state that only this plugin can remove. |
| `Shutdown` | Graceful process exit. | Preserve recoverable persisted state. Exit callbacks are best-effort; a forced kill may run none. |
| `Unknown` | No reliable unload reason was available. | Preserve recoverable state. Destructive removal requires a positive `Removal`. |
| `Suspend` | Client only: [per-server mod pause](mods-menu.md#per-server-mod-pause) unloaded the plugin because the joined server does not run it. The DLL stays; the plugin loads again when the client leaves or a later handshake asks for it. | Keep, like `Swap`. |

A replacement that is temporarily unreadable leaves the running plugin intact for a later scan. An allowed replacement can still fail during loading after the old plugin has unloaded as `Swap`; inspect the log and restore a working build rather than assuming the replacement is active.

**Registrations and transient handles are always cleaned up, regardless of reason.** Events, commands, hooks, patches, scheduled work, localization, content registrations, spawned units and effect handles belong to the retiring plugin/Core generation. Keeping those handles would orphan their resources. Persisted game entities that a new generation can find again are a separate responsibility: retain them on `Swap`, and reacquire fresh references in `OnLoad`.

The reloader independently attempts its owner-based cleanup even when plugin teardown throws. This is a fallback, not a substitute for tracking every resource. Each unload logs its reason and source, for example `Reload: Sample unload reason=Swap (bootstrap-slot)`. `Unknown` or cleanup errors require inspection before treating the reload as successful.

## Hot reload

Use the [quickstart](hot-reload.md) for build, deployment and verification steps. These are different operations:

| Change | Mechanism | Restart required? |
|---|---|---|
| Reloadable plugin DLL | Unload its collectible assembly context, then load the replacement from `progenitor-reload/`. | Normally no, provided the plugin obeys the rules below. |
| Core (`Progenitor.dll`) | Bootstrap unloads plugins, uninstalls Core, waits for collection, installs the next generation and reloads plugins. | Normally no; guards can refuse a swap. |
| Bootstrap (`Progenitor.Bootstrap.dll`) | Process-lifetime code in the BepInEx chainloader. | Yes, for each affected process. |
| Method-body Edit-and-Continue delta | Changes a supported Debug assembly in place without replaying load or registration. | Experimental; in-game verification remains outstanding. |

### Ownership and pin constraints

- Register through `ctx.Events`, `ctx.Hooks`, `ctx.Systems`, `ctx.Patches` and other owner-aware APIs. Track extra disposable resources; detach external callbacks and stop background work on unload.
- Do not inject plugin-defined IL2CPP types or `SystemBase` subclasses. Use named virtual systems instead. A reload-folder plugin that does inject is **process-lifetime** (below).

### Plugins that inject IL2CPP types are process-lifetime

`ClassInjector.RegisterTypeInIl2Cpp` has no unregister and keys the IL2CPP class by managed full name for the life of the process. A plugin that injected a type cannot be reloaded in place: the next load's registration throws `ArgumentException: Type with FullName … is already injected`, and whatever the plugin built on that type never comes back (2026-10-06: a Core swap cascaded a reload of a third-party chat plugin; every later world bind logged `Canvas capture failed` and the chat box no longer opened until the game restarted).

Bootstrap's reloader detects these plugins two ways:

- **At load, from the image.** Before the image loads, its metadata is scanned for member references to `Il2CppInterop.Runtime.Injection.ClassInjector::RegisterTypeInIl2Cpp*` (including `…WithInterfaces` and the generic overloads), `BepInEx.Unity.IL2CPP.BasePlugin::AddComponent` and `IL2CPPChainloader::AddUnityComponent` (both register their type argument). A reference counts even if the call has not run yet: that chat plugin injects only when a world binds. Only the plugin's own image is scanned; bundled helper DLLs are not.
- **At runtime.** Before any unload decision, `ClassInjector`'s injected-name set is compared with the types the plugin's assembly defines. This catches injection through an unscanned helper once it has happened. The finding is kept for the rest of the process.

A detected plugin logs `Reload: <file> registers its own IL2CPP types (…); … it is process-lifetime …` once at load, and then:

| Event | What happens |
|---|---|
| File changed | The running build stays loaded. One line: `Reload: <file> changed, but it registers its own IL2CPP types and cannot be reloaded in place; the running build stays loaded. Restart required …`. The new build loads at the next start. |
| File removed | It stays loaded until the game restarts (`… was removed, but … it stays loaded until the game restarts.`). A file that returns to the running build's stamp clears the pending state. |
| Another file with the same assembly name | Refused (`Reload: skipped <file>; it is another copy of <holder> …`): it would inject the same names again. |
| Core swap, plugin does not reference Progenitor (the chat plugin) | Kept loaded across the swap like a `BepInEx/plugins` plugin (`Reload: Core swap keeps <file> loaded …`). Its context is excluded from the plugin-ALC wait and latch. |
| Core swap, plugin references Progenitor | The swap is refused before anything freezes: `Core reload refused: process-lifetime plugin <file> registers its own IL2CPP types and references this Core; … restart the process.` The watcher parks on this refusal. Every Core swap is refused until the process restarts. |
| Per-server pause | Refused for the whole request, as for `ctx.UnsafeHarmony`. |
| Install tab replacement/removal | The admission snapshot reports `ProcessLifetimeMarked`. Hot admission refuses with `ProcessLifetimeMarked`, and a version swap takes the restart route (pending copy plus `.uninstall` marker). |
| Process exit | Unloaded normally (`Shutdown`). |

**Mods tab.** The mod shows the problem kind `restart-required`. The problem is a warning, `Restart required`, while a changed or removed file waits for the restart. Otherwise it is an informational note, `Updates need a restart`. Bootstrap's `ProcessHost.ProcessLifetimePlugins()` supplies the data. An older Bootstrap does not provide that method, and Core shows nothing.

**`reload_status`** lists the plugins as `reload.processLifetimePlugins[]`: `{ file, guids, names, coreBound, restartPending ("" | "changed" | "removed"), sites }`. `tools/deploy.ps1` reads the list. It withholds dependent plugins before staging a Core when any entry has `coreBound`. It also warns that a copied DLL is not a live reload when the running process keeps that file.

This is a Bootstrap change: it takes effect only after each process restarts with the new Bootstrap. Core's own `PluginReloader` (used only when Bootstrap does not own plugin lifetime) does not apply it. Source-checked and game-free tested (`Il2CppInjectionPolicyTests`, `DeployHoldTests`). The scan was also run offline against the installed chat plugin's DLL. It found `ClassInjector::RegisterTypeInIl2Cpp` and no Progenitor reference. This work was first source-checked and tested game-free.

Verified live on the Steam client, 2026-10-06:

- The chat plugin was detected at load.
- A bridge Core swap in a world logged `Core swap keeps <chat plugin>.dll loaded` and finished `ok`, with the old generation collected.
- A rejoin after the swap logged the chat plugin's `Game canvas captured`, with no `already injected` error.
- Later the same day, on a client restarted with the same build, a human Enter press opened chat in a world. The bridge `key` op does not reach the chat plugin's input, so a person has to make this check.

### Reload-folder load order follows dependencies

BepInEx's chainloader orders plugins by `[BepInDependency]`. Bootstrap's reloader does the same for `BepInEx/progenitor-reload/`. It reads each DLL's metadata before loading anything: its plugin GUIDs, its `[BepInDependency]` entries (hard, soft, or with a minimum version), its assembly name and the assemblies it references.

- **Order.** A DLL loads after every DLL it depends on by GUID, or whose assembly it references. A library DLL with no `[BepInPlugin]` counts as a provider through its assembly name. Ties keep file-name order, so a folder without dependencies loads exactly as before. When the order differs from file-name order, one line names it: `Reload: load order ExampleCommandFramework.dll -> AutoLatchGates.dll (dependencies first).` A dependency cycle loads in file-name order with one `Reload: dependency cycle between …` line, and plugins that depend on the cycle still load after it.
- **Resolution.** Each reload-folder plugin has its own collectible load context. A context resolves Progenitor, Bootstrap and any assembly another loaded reload-folder plugin provides, by simple name. A mod can therefore reference another reload-folder mod, as a mod built on a command library references that library. Before this, such a reference fell through to the default context and failed.
- **Waiting, not failed.** A hard dependency that is not in the folder, not loaded, and not a `BepInEx/plugins` plugin makes the DLL wait. It is logged once (`Reload: <file> is waiting for <guid>, which it needs …`), listed in `reload_status.waitingPlugins[]` (`{ file, guids, waitingFor, reason }`) and shown on the Mods tab (`dependency-missing`, `Waiting for a dependency`). It loads by itself once the dependency arrives. A plugin that needs a waiting plugin waits too, and so does one whose provider failed to load in the same pass. A soft dependency only orders; it never makes a plugin wait. If the chainloader's plugin list cannot be read, a dependency outside the folder never makes anything wait, which was the old behavior.
- **Dependents unload first.** When a DLL is replaced or removed, every loaded DLL that needs it at runtime unloads first, dependents before providers. The runtime edge is a hard `[BepInDependency]` or an assembly reference. Those dependents use unload reason `Swap` and keep their icons. They load again after the new provider, or wait if it was removed. If one of them cannot be unloaded in place (`ctx.UnsafeHarmony`, or process-lifetime), the provider's change is held instead: `Reload: <provider> changed, but <dependent> needs it and cannot be reloaded in place; … Restart required.`
- **Core swaps and pauses** use the same graph. The unload order includes assembly-reference edges. A Core swap also keeps loaded every reload-folder DLL that a kept process-lifetime plugin needs. If one of those references Progenitor, the swap is refused, as for the plugin itself.

Dependencies on plugins in `BepInEx/plugins` are only checked by GUID. A `BepInEx/plugins` mod cannot depend on a reload-folder mod, because the chainloader runs first. Version constraints are not enforced: a minimum-version dependency is treated as hard. This is a Bootstrap change; restart each process to take it. Source-checked and game-free tested (`PluginLoadOrderTests`). The tests include a .NET check that a collectible context can bind a sibling context's assembly, and that both are collected when unloaded dependent-first with their `Resolving` handlers detached.
- Do not hand raw Harmony a patch method from a collectible plugin or Core assembly, even for a managed target. Harmony caches can retain that assembly after unpatching, preventing collection. `ctx.Patches` uses Bootstrap-owned trampolines whose plugin handlers can be cleared.
- `System.Text.Json` caches a converter per serialized type, and its process-wide default options (`JsonSerializer.Serialize(value)` with no options) outlive every collectible context. Core's unload sweep drops those entries, and `IModStore`'s, for collectible types after each plugin `Unload` and again at Core uninstall (`Reload/JsonCacheSweep.cs`), so serializing your own types, including anonymous ones, no longer pins the plugin or Core. Stop background work that serializes in `OnUnload`: a type cached again after the sweep refuses the Core swap. Other serializers and your own `Type`-keyed statics in non-collectible assemblies are not swept.
- For an existing typed Bootstrap site, `HarmonySlotHandoff.TryBindPlugin` / `BindPlugin` bind the handler. `BindPlugin` returns `IDisposable?`: null means binding failed; track a non-null handle. `TryBindPlugin` reports failure as `false`; `ClearPlugin(site, owner)` clears an explicitly owned binding. Owner defaults to the handler's assembly/context name. Empty owners and a second owner for the same slot are refused. Unload clears the plugin's slots; an empty slot calls the vanilla original.
- `ctx.Harmony` has been replaced by `ctx.UnsafeHarmony`. Reading `UnsafeHarmony` warns once and marks the plugin non-hot-swappable for the rest of the process. Both reloaders refuse replacement and keep the running build. Removing the file still unloads it, but neither unpatching nor a Core swap clears a process-lifetime pin. Restart each affected process after removing the unsafe patch usage.

The [Sample's patch demonstration](../samples/Progenitor.Sample/Plugin.cs) uses `ctx.Patches`; its supported shapes and refusals are documented under [Patches](#patches). A logged unload or a changed file alone does not prove the old assembly was collected or the replacement loaded. Check the loaded image and completed reload outcome as described in the quickstart.

### Image identity and allowlist

Loads report the assembly name, byte size and SHA-256; the `image` log line contains the full hash. An optional `BepInEx/config/progenitor-allow.txt` limits automatic loading and promotion to exact hash/name pairs. With no file, logs report `allowlist=none`. An unreadable, unresolved or malformed allowlist fails closed.

Each row is a 64-hex SHA-256 followed by whitespace and a leaf filename. Blank lines and `#` comments are allowed. Release `SHA256SUMS` rows in `container!path` form are also accepted and matched by the path's leaf name. A staged `.next` is checked against the live filename's row. Use the actual release/build hash; a filename match alone is insufficient.

A refused plugin DLL remains on disk but is not loaded; a refused staged Core or Bootstrap image is quarantined with a `failed-<timestamp>` name. Fix the logged refusal before retrying. Promotion verifies the bytes written to the live path; verification failures and rollback failures are explicit errors. Preserve the staged image, backup and logs when investigating a failed update. See the [deployment reference](dev-loop.md#the-three-pieces) for recovery steps.

### Core and Bootstrap compatibility

Bootstrap loads Core from `BepInEx/progenitor-core/`. Automatic Core watching uses `[Reload] WatchCore`, which defaults to `false`; development deployment tools can enable it. A staged image must be stable across scans and pass the same validation as an explicit `core_reload` request. Hosted children do not watch; a client hosting a live private-game child refuses this automatic swap. Watch refusals and collection failures are not successful deployments.

Bootstrap and Core must agree on their protocol. The source constants are `CoreLayout.ProtocolVersion` and `BootstrapLink.ProtocolVersion` (both currently `4`). Maintainers must bump both together when changing the install/uninstall contract, host interfaces, shared slot names/signatures or shared memory layout. A mismatch refuses installation and can leave the process without Core until compatible files are installed.

Deploy a coupled Bootstrap/Core change with the affected processes stopped, then restart them with the matched pair. Bootstrap already loaded in memory cannot replace itself; a `.next` promoted at startup may only take effect on the following launch. Use the quickstart's loaded-version checks before declaring deployment complete.

### Edit-and-Continue status

`tools/enc --watch` is an opt-in experimental loop. It requires a Debug build and matching deployed portable PDB, a process started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, an unrewritten loaded image, matching baseline hash, the next expected delta sequence and allowlist approval when configured. An unsupported or out-of-step delta is refused; rebuild and redeploy for a normal generation swap.

A delta does not rerun `OnLoad` or recreate registrations. Source edits that need that lifecycle, or that the conservative classifier rejects, require a DLL swap. Ordinary-host tests do not establish operation inside V Rising's embedded runtime; this feature still needs that live verification. Use DLL reloads for the documented standard workflow.

Implementation investigations (Core hot reload, Harmony patch lifetime, Edit-and-Continue, the promotion threat model), failure histories, raw evidence and the historical narrative removed from this section are kept in the internal research notes, outside this repository.

## Events

`Progenitor.Events.EventBus` is typed pub/sub. Core installs each game hook once. Handlers run on the main thread, descending `priority` (higher first). An exception in one is logged and does not stop the others. Later handlers still see a canceled event, with `ICancellable.IsCancelled` set.

**How the three hooks are installed** (job FHARM7). Bootstrap-owned typed slots first (`HarmonySlotHandoff`: `ChatOnUpdate`, `UserConnected`, `UserDisconnected`), which pin nothing. When Bootstrap does not own a site, the fallback now registers through the *same* `Progenitor.Bootstrap.PatchSlots` pool plugin patches use, under the Core owner id `<guid>.core` — none of the three bodies needs `__result` or a prefix-to-postfix `__state`, and the chat body takes its `EntityManager` off the server world rather than off `__instance`, so the slot shape costs nothing. Arguments are bound by the original's own parameter name; a name that is not on this build leaves the seam unpatched with a line naming it. The old direct `Harmony.Patch` survives only for a Core running with **no** Bootstrap at all, and says so in one warning naming the generation it pins. See [Patches](#patches).

```csharp
ctx.Events.On<ChatMessage>(e => ctx.Log.LogInfo(e.Text));          // priority 0
ctx.Events.On<PlayerConnected>(OnJoin, priority: 10);
// or: EventBus.On<WorldReady>(e => ..., priority: 0, owner: this);
// EventBus.RemoveAll(owner); EventBus.Publish(ev); EventBus.SubscriberCount<T>();
```

`PluginEvents.On<T>(Action<T> handler, int priority = 0)` records the subscription on the context. Direct `EventBus.On<T>(handler, priority, owner)` returns `IDisposable`; pass `owner` if you will `RemoveAll`.

Shipped events (`GameEvents.cs`):

| Type | Side | Notes |
|---|---|---|
| `WorldReady` | both | `World`, `IsServer`. Fires when `"Server"` / `"Client_0"` first exists this process, and again after a client rejoin. |
| `PlayerConnected` | server | After `OnUserConnected`. `UserEntity`, `User`, `UserIndex`, `CharacterName`. |
| `PlayerDisconnected` | server | Prefix of `OnUserDisconnected` (before the game processes it). `Reason` is the `ConnectionStatusChangeReason` name. |
| `ChatMessage` | server | Before broadcast. `ICancellable`. `UserEntity`, `CharacterEntity` (`FromCharacter.Character`, the speaker), `User`, `Text`, `Type`. Commands cancel what they handle. |

Chat Harmony priority is 300 (ahead of another chat-command framework, typically at 200). Destroyed when canceled.

## Hooks

`Progenitor.Hooks.SystemHooks`. Prefix/postfix on any ECS system's update, managed (`SystemBase.Update`) or unmanaged (`WorldUnmanagedImpl.UnmanagedUpdate`). Registrations return `IDisposable`; Harmony patches stay.

```csharp
ctx.Hooks.Before<ServerBootstrapSystem>(ctx => { /* ... */ });
ctx.Hooks.After<ProjectM.MoveEntitySystem>(ctx => { },
    new HookOptions { OnlyWhenSystemRuns = true, EveryNFrames = 10 });
ctx.Hooks.WhenWorldExists("Server", world => { });
```

**Worked example: `samples/Progenitor.Sample/SampleHooks.cs`**, driven by `.hooks` / `.hooks skip [1-5]` (`SampleCommands.cs:293`). It registers a Before and an After hook on the managed `ProjectM.ServerBootstrapSystem` and the Burst `ProjectM.MoveEntitySystem` — the same pair the framework's own live check used — and `.hooks` reports both counters. `.hooks skip [1-5]` arms a Before hook that sets `ctx.Skip = true` on `MoveEntitySystem` for a bounded number of frames, and the log shows the thing the doc says two paragraphs down: **an After hook left on the default `OnlyWhenSystemRuns = true` does not run on a frame a Before hook skipped**, so the After counter stops advancing while the Skip budget drains.

**What that skip costs, stated properly** (job FEX2, R56 finding 8). `MoveEntitySystem` (server metadata) has **three** queries and a persistent `NativeList<ColliderCastHit>`. Only `__query_1244541736_0` has a generated enumerable in the game's metadata: `Translation` / `Velocity` / `MoveVelocity` / `Rotation` read-write, `Movement` / `EntityInput` / `NetworkInterpolated_Shared` read-only. Skipping *that* query drops a frame of integration and consumes nothing. **`_1` and `_2` were not traced** — no `IFE_`, no `ResolvedChunk`, no type handle anywhere in the metadata — which is precisely why the demo is bounded to five frames rather than being a switch. And because the query carries `EntityInput` and `NetworkInterpolated_Shared`, the entities are client-input-driven and include player characters: a **server-side** skip is not "one frame of stutter", it is a frame the client already predicted and the server did not, resolved by a position correction. Five of them is a visible rubber-band for every moving player. The `.hooks skip` reply says so to the admin who arms it.

`SystemHookContext`: `World`, `SystemType`, `SystemName`, `Skip`. Set `Skip` from a **Before** hook to skip that system's update this frame (Harmony prefix returns false). Setting it from After is ignored (one warning logged). Nested group updates get a fresh context so they cannot clobber each other's `Skip`.

`HookOptions`: `OnlyWhenSystemRuns` (default true — skip disabled / `ShouldRunSystem` false); `EveryNFrames` (default 1; values below 1 treated as 1). Pass null for those defaults.

`WhenWorldExists(worldName, callback)` fires immediately if the world is already created, otherwise from Core's per-frame tick. Dispose cancels a wait that has not fired.

Gotchas: the type must have an IL2CPP class pointer (a game `SystemBase` or `ISystem`); a missing wrapper throws at register rather than becoming a silent no-op. Do not put `ref SystemState` in a Harmony signature. `GetSystemTypeIndex<T>()` over an `ISystem` struct is the generic-instantiation crash; Core uses the non-generic lookup. Registering before the first world exists is fine: TypeManager has no system table yet (the client loads Core at the title screen), so the registration waits and the per-frame tick places it once the table appears, without logging. A type the initialized table does not list (a system that only exists on the other side) stays inactive and logs one warning naming it; its hooks never fire in that process.

**When the hooked system throws, your After hook does not run** (jobs FLATCH1, FHOOK1, FHOOK2, FHOOK3). Harmony skips the postfix when the patched original throws, so a Before that fired has no matching After for that call, and the hand-off it pushed is a *strand*. That is normal-and-survivable rather than fatal, because a system group catches per-system exceptions and keeps going — write After hooks so that a missed call is tolerable, and do not use one to release something a Before acquired unless you can also release it on the next call. Two mechanisms clean up behind it and both are on the pin dump: Bootstrap's `AfterCallStack` pairs prefix to postfix by a **token** carried in Harmony's `__state`, so the *caller* that catches the exception releases its own hand-off and drops the strand it was covering; and a **rendered-frame sweep** on both that stack and Core's `HookCallStack` releases the strand nothing encloses (a single update cannot span two rendered frames, so anything still pushed from an earlier one is abandoned by construction). One warning per site per Core generation says a strand was released; the throw itself is logged by whatever caught it. Job FHOOK3 gave Core's `HookCallStack` the same token, carried across the ALC boundary by the widened `ManagedUpdatePostfix`/`UnmanagedUpdatePostfix` slot delegates (`long callToken`, protocol 4), so **both** stacks now pair by identity. That closed the last residue: a caller that catches used to dispatch the inner system's After handlers and silently skip its own, and the outer caller is routinely hooked because `ctx.Systems.Add` on the default anchor registers an After hook on `SimulationSystemGroup`. The frame sweep stays as the liveness backstop for a strand no enclosing patched `Update` ever releases.

## Systems

Research notes (internal): why virtual systems instead of injected `SystemBase`; After `SimulationSystemGroup` verified live on the dedicated server; `DefaultAnchorWatch` fallback from WorldReady, recovery when the group later ticks.

`Progenitor.Systems.VirtualSystems`. Named managed callbacks that Core runs from **one** `SystemHooks` Before/After per vanilla system type. This is `ctx.Systems.Add`. It is **not** an injected `SystemBase`: no TypeManager row, no `World.CreateSystemManaged`, reload is dispose. Sit inside a group (between two vanilla children) with `ctx.Hooks.Before<T>` / `After<T>`, or with `AddAfter<T>` / `AddBefore<T>` when you also want a name, owner tracking, and diagnostics.

Default anchor is After `Unity.Entities.SimulationSystemGroup` (there is no `ProjectM.ServerSimulationSystemGroup`). If that group's `Update` never fires through `SystemBase.Update` within 600 `Plugin.Frame` ticks **after the side's world exists**, Core logs a warning once and falls back to After `ServerBootstrapSystem` / `ClientBootstrapSystem` for the default anchors only. The countdown starts at 0 on `WorldReady` (and on the first sighting of the world); frames before that do not count. If `SimulationSystemGroup` later ticks, default anchors **switch back** to the group even after fallback already engaged (one info log; bootstrap relays are released). Explicit `AddAfter<T>` / `AddBefore<T>` on a vanilla type are not affected by that fallback.

```csharp
// default: After SimulationSystemGroup, this plugin's side world
ctx.Systems.Add("sample.users", vs =>
{
    if (vs.Frame % 300 != 0) return;
    var users = Query.Entities(vs.EntityManager, includeDisabled: false, includeSystems: false,
        ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
});

ctx.Systems.Add("order-board.init", VirtualSystemAnchor.After(typeof(InitializationSystemGroup)), vs => { }, order: 10);
ctx.Systems.AddAfter<ProjectM.DealDamageSystem>("order-board.damage", vs => { });
// or: VirtualSystems.Add(name, anchor, onUpdate, owner, order);
// VirtualSystems.RemoveAll(owner); VirtualSystems.Describe();
```

`VirtualSystemAnchor`: `AfterSimulation` / `BeforeSimulation`; `After(Type, Side)` / `Before(Type, Side)`; generic `After<T>(Side)` / `Before<T>(Side)`. `Side` filters `"Server"` / `"Client_0"` (Both still only those two names). `PluginSystems.Add` further ANDs the anchor with this plugin's process side.

`VirtualSystemContext`: `World`, `EntityManager`, `DeltaTime` (`World.Time.DeltaTime`; 0 if the read throws), `Frame` (this callback's invocation count, 1 on the first call — use this, not `World.Time.ElapsedTime` which reads 0 through the interop), `Name`, `Stop()` (unregister from inside). Lower `order` runs first; equal order keeps registration sequence. Same name from the same owner replaces. An exception in one callback is logged (`LastError` / `LastDuration` kept per slot) and does not stop the others.

`PluginSystems.Add` / `AddAfter<T>` / `AddBefore<T>` record the handle on the context. Unload also `RemoveAll(owner)`. One Harmony relay is kept per distinct (type, phase) and **released when that anchor's live slot count hits 0**; bootstrap fallback hooks are dropped when they are not the live default-simulation anchor (or when no default slot remains). Nested group updates save/restore the ambient world rather than nulling it, so a nested relay cannot drop the outer dispatch. `Describe()` is the diagnostic string for the status op (`fallback=group|bootstrap|pending`, `pluginFrames`, `groupFired`, `worldReady`, then each live slot).

Do **not** ship `Add<TSystem>() where TSystem : SystemBase`. Injecting a `SystemBase` subclass is a later Core-only spike; reloadable plugins must not register IL2CPP types.

## Commands

Routing, conflicts and administrator aliases: [commands.md](commands.md). Command-routing MVP is implemented offline and independently reviewed; coexistence with another chat-command framework is unverified and cross-framework collision checks are unavailable. Live gate and verification limits: that page.

`Progenitor.Commands`. Chat lines starting with `CommandRegistry.Prefix` (default `"."`). First parameter of every method must be `ICommandContext`. Remaining parameters are parsed from the text after the name. Overloads of the same name are allowed.

```csharp
internal static class SampleCommands
{
    [Command("ember", usage: ".ember [count]", description: "Give yourself Ember Shards (the sample cloned item).")]
    public static void Ember(ICommandContext ctx, int count = 1)
    {
        if (ctx.CharacterEntity == Entity.Null)
        {
            ctx.Reply("No character entity on this user; cannot add items.");
            return;
        }
        ctx.Reply($"count={count}");
    }

    [Command("joins", description: "Show how many times you (and others) have joined this save.")]
    public static void Joins(ICommandContext ctx) =>
        ctx.Reply("...");
}
// in OnLoad, server only. C# static classes cannot be generic type arguments:
ctx.Commands.Register(typeof(SampleCommands));
// instance class (parameterless ctor), or an already-constructed object that holds PluginContext:
// ctx.Commands.Register<MyCommands>();
// ctx.Commands.Register(new MyCommands(ctx));
```

`PluginCommands.Register<T>()` / `Register(Type)` discover `[Command]` methods on that type. `T` may be an instance class (one parameterless constructor, including non-public, shared by instance methods) or a type that only has static command methods (invoked with a null target). A C# `static` class cannot be a generic type argument — use `Register(typeof(SampleCommands))` for those. `Register(object)` uses that instance for instance methods so the command class can hold a `PluginContext`; static methods on the same type still run with a null target. `RegisterAssembly()` discovers every `[Command]` in the plugin assembly (or a referenced one). Dispose (or unload) drops the commands so a reload replaces rather than stacks.

Attributes: `[Command(name, shortHand?, adminOnly: false, usage?, description?)]` on a method; `[CommandGroup(name, shortHand?)]` on a class (shared first token: `.group name`); `[Remainder]` on the last parameter, which must be `string` (verbatim rest of the line).

**Admin gating — the pattern to copy.** Any command that changes world state the caller could not otherwise reach — granting a buff or an item, spawning a unit, spawning a networked effect entity, healing, teleporting — carries `adminOnly: true`. That is the whole gate: the dispatcher filters the overload set on `ICommandContext.IsAdmin` (the game's own `User.IsAdmin`, the same flag the Dev `users` op reads) **before** binding any argument, replies `This command is admin-only.` with `CommandResult.Denied`, never enters the method, and `.help` hides the command from non-admins. Do not hand-roll the check in the body — a body check runs after argument binding, is easy to forget on a second overload, and is invisible to `.help`.

```csharp
[Command("grant", adminOnly: true, usage: ".grant <item> [count]",
         description: "Admin only. Give yourself an item: …")]
public static void Grant(ICommandContext ctx, string item, int count = 1) { … }
```

Gating is per overload, so a read-only form and a mutating form of the same name can differ. Start the `description` with `Admin only.` so the intent survives into `.help` for the people who can see it. Core's `.robes` is gated this way. The exception is a Core command that must follow the server's admin list rather than the game's flag (`ServerAdminList.AuthorizeShared`: on the admin list, else authenticated with `adminauth`, else the host of a private game). Every [`.debug`](#debug-tools-debug) form (`src/Progenitor.Core/Debugging/DebugCommands.cs`, decided 2026-10-09) is not `adminOnly` but carries Core's internal `[AdminListGate]` attribute: the dispatcher applies that rule before binding any argument, refuses everyone else with the same `This command is admin-only.` and `CommandResult.Denied`, and `.help`, group listings and the chat command hints hide the command from them; the body checks the rule again before it parses. The attribute is internal to Core, so a plugin cannot use it. `.stop-server` still checks the rule only in its body, so `.help` lists it for everyone. In the Sample `.beam` and `.rat` are gated, while `.joins`, `.order`, `.mods`, `.ui` and `.fx` (a cosmetic packet to the caller's own client) are not. Before R27 finding 3 the Sample gated nothing, and its debug buff (now `.debug`) was self-service for every player on any server running the Sample.

`ICommandContext`: `UserEntity`, `CharacterEntity` (or `Entity.Null`), `User`, `IsAdmin`, `RawText`, `World`, `Reply(string)`. Replies longer than 500 characters are split to fit `FixedString512Bytes`. `CharacterEntity` is the chat event's speaker: `ChatMessage.CharacterEntity`, which is `FromCharacter.Character`. When that is `Entity.Null` (the debug-bridge `command` op does not pass a character), the dispatcher falls back to `User.LocalCharacter`.

Supported argument types: `string`, `int`, `long`, `float`, `double`, `bool`. Quotes, escapes, and conversion rules live on `CommandParser` (public `Tokenize`; self-check runs at install).

Dispatcher (`CommandDispatcher.TryExecute` → `CommandResult`):

- Client process always returns `NotACommand`.
- No prefix → `NotACommand`. Prefixed but unknown → `Unmatched` (left for other frameworks).
- Known command that ran, or usage reply for a bind failure / group listing → `Executed`.
- Every remaining overload is admin-only → `Denied` ("This command is admin-only.").
- Throw or missing `User` → `Error` ("Command failed.").
- Overload pick: highest conversion specificity, then fewer defaults. A tie lists the colliding usages.
- Conflicting short routes are disabled for every competing owner; qualified routes stay. See [commands.md](commands.md). `UnregisterOwner(owner)` drops a plugin's set.
- Instance methods share one parameterless (including non-public) constructor unless `Register(object)` supplied the instance; abstract types skip instance methods when registering by type.
- Chat subscriber priority 500; cancels on every result other than `NotACommand` / `Unmatched`.

Built-ins: `.help` / `.h`, `.progenitor` / `.prog` and the admin-only `.progenitor logs` ([server logs](commands.md#progenitor-logs-server-logs)) (owner `progenitor`). A plugin that claims `.help` conflicts; both short routes disable until an administrator assigns the token. Qualified routes remain. See [commands.md](commands.md).

## Chat

Research notes (internal): the vanilla job never echoes Global/Local to the speaker; `ServerChatUtils.SendChatMessage` is one entity per `ConnectedUser.UserIndex`.

`Progenitor.Chat.Chat`. Server-only send that **skips** `ChatMessageSystem`. Injecting a `ChatMessageEvent` is the wrong tool for a mod announcement: with one connected player, Global and Local both produce zero packets. Through `PluginContext`, `ctx.Chat` is the same surface bound to this plugin's world (0 / false when the world is not ready or this is the client).

```csharp
if (ctx.IsServer)
{
    ctx.Chat.Broadcast("The night is coming.");                    // System, every ConnectedUser
    ctx.Chat.Broadcast("Hello hunters.", ChatMessageType.Global);  // includes the speaker
    ctx.Chat.SendTo(userEntity, "Only you see this.");
}
```

**Worked example: `samples/Progenitor.Sample/SampleChat.cs`**, driven by `.shout <text>` and `.whisper <name> <text>` (`SampleCommands.cs:321,346`). `.shout` calls `ctx.Chat.Broadcast` at `System` and replies with the recipient count, so the server log alone proves it; `.whisper` resolves one connected player and calls `ctx.Chat.SendTo`. The same file carries the module's only compiled `ChatMessage` subscriber, registered at **priority -100** — deliberately *below* the commands module's 500, because the bus runs every handler in descending priority regardless of cancellation, so subscribing under commands is the only way to observe whether a line was handled.

The non-context path is `Chat.Broadcast(text, type?)` / `Chat.Broadcast(world, text, type?)` / `Chat.SendTo(user, text, type?)` (and the world overloads). Each call creates one outbound entity per recipient:

`NetworkEventType` + `ChatMessageServerEvent` + `SendNetworkEventTag` + `SendEventToUsers` (one-bit `UserBitMask128`).

That is the shape native `ServerChatUtils.SendChatMessage` builds. The helper is not called: both overloads take `ref FixedString512Bytes` and `ref NetworkId`. `NetworkEvents.SendEvent<T>` is also skipped (generic interop over a game value type). Recipients come from a default `ConnectedUser` query (a disconnect removes `ConnectedUser` from the user entity, so offline users are not in the list — the entity itself is never disabled). `ConnectedUser.UserIndex` is a byte slot 0..127, not `User.Index`. Text longer than 509 UTF-8 bytes is truncated on a rune boundary (the inbound job's `0x1fd` cap). `FromUser` / `FromCharacter` are default `NetworkId` unless a speaker entity is passed to the world overload.

| Call | Who receives | Outbound `ServerChatMessageType` |
|---|---|---|
| `Broadcast(text)` | every `ConnectedUser`, including whoever would be the speaker | System (6) |
| `Broadcast(text, Global)` | same | Global (1) |
| `SendTo(user, text)` | that entity's `ConnectedUser.UserIndex` only | System |

Inbound `ChatMessageType.Whisper` becomes outbound `WhisperFrom`. Region still sends (`ServerChatMessageType.Region`); the vanilla job's Region case is a no-op, this API is not.

Fire-and-forget: unload has nothing to reverse. `SerializeAndSendServerEventsSystem` consumes the entities the same frame and calls `SendPacketToUser(..., reliable: true)`.

Debug bridge: `broadcast` op (`{text, type?: ChatMessageType name}`). Returns `{recipients, type, text}`.

**Verified live 2026-09-03.** One connected player: `recipients` is 1, the player sees the line, and `chat_observe` with the same `match` reports `chatServerEvents: 1` (`Chat.Broadcast` / `SendTo` per connected user as the game does; evidence 48). Two clients connected at once: `broadcast` `recipients: 2` (evidence 55, 58, 60). A handled command is not broadcast to the other client.

## Networking

`Progenitor.Networking`. Custom messages on the game's reliable events channel. Payload is an `unmanaged` struct (or a raw span). One custom event per packet. Event ids should be large and distinctive — the game's own ids are hashes and a collision is silent. Max payload `CustomEvents.MaxPayloadBytes` (15 KiB). The `byte*` on a receive handler is only valid during the call.

**Event ids: the reserved range and the one-owner rule** (job FEX2, R56 finding 7; job FEX4, R57 findings 3, 4 and 10; job FEX6, R58 findings 1, 5 and 12). Ids are process-wide and one handler owns one id, so a collision is not a compile error, not a runtime error and not visible to a player — it used to be one warning line and last-registrant-wins. Two rules now, enforced on all three directions into the handler table: `CustomEvents.OnReceive` / `OnReceiveFromClient`, every plugin-facing send, and every removal.

1. **`0x5072____` ("Pr\*\*") is Progenitor's own range.** Every one of Core's twelve protocol ids is in it (`PrhH` / `PrhR` / `PrhQ`, `PrfD` / `PrfU` / `PrfK` / `PrfE` / `PrfI`, `PrcS`, `PrmM` / `PrmR` / `PrmC`; `PrmR` and `PrmC` are retired since 2026-10-05 and their handlers only drop the packet) and every future one will be, so the **range**, not the twelve, is what a plugin stays out of. A registration inside it from any assembly but Core's is **refused**: one error line, no handler stored, nothing added to Bootstrap's owned set. The failure it prevents is a mod silently taking `PrhQ` and turning a mod-mismatch kick into an unexplained disconnect. **"Core" here is an assembly identity, not a name** (R58 finding 12): the test is `ReferenceEquals` against `typeof(CustomEvents).Assembly`, so a plugin whose `<AssemblyName>` is `Progenitor` — a fork of this tree with the project file unchanged, a mod shipping a private copy of `Progenitor.dll` — is just another plugin. It used to be a string comparison, and that shape is the one accident where the collision takes the protection away instead of triggering it.
2. **An id a different owner already holds is refused, not replaced.** Ownership is the registering delegate's assembly compared by *simple name*, so a plugin hot reload — a fresh collectible assembly with the same name — is still the same owner and may re-register its own ids. A handler nothing can attribute to an assembly (a dynamic method, a delegate over a global function, a throwing `GetName()`) is **its own identity per registration**, not a shared `""`: two of them can neither take nor lose an id to each other, which is what the shared name used to allow — the rule off for exactly the delegates it could not name. One warning line per Core generation says an owner could not be attributed. (Rule 2 keeps the by-name comparison; only rule 1's Core test moved to identity, because the two questions are different — a name collision between plugins costs them an id, a name collision with Core makes a plugin *into* Core.)

**Rule 1 applies on the way out as well** (R57 finding 3). `SendToUser`, `SendToAll`, `SendToServer` and their five `PluginNet` wrappers refuse a reserved id from anyone but Core: the send returns false (or 0) and logs one error line per id per Core generation. Until FEX4 the six send entry points implemented none of it, so an author who picked `0x50726D4D` because it looked distinctive was told their `OnReceive` was refused — which reads as "this id does nothing for me" — while their `SendToAll` still handed every connected client's `ModManifest` handler a foreign payload, and a client's `SendToServer(0x50726852)` still reached Core's handshake. Core's own sends are unaffected and ordinary sends pay a mask and a compare: the caller is only attributed once the id is known to be in the range (`Assembly.GetCallingAssembly()` at each public entry point; `PluginNet` is Core code, so its wrappers pass the *plugin's* assembly instead of being attributed to Core, and that path is never Core whatever the plugin is called). Every entry point that asks `GetCallingAssembly()` carries `[MethodImpl(MethodImplOptions.NoInlining)]`, because the frame that must survive is the one that asks — inlined into a plugin's handler, the crawl returns whoever invoked the handler, which for an event-bus or command callback is Progenitor (R58 finding 5). Nothing here stops a hostile peer, which can forge any id and always could — this is the honest author's half.

**And on the way out of the table: only the owner, or Core, may remove** (R58 finding 1). `RemoveHandler(id)` / `RemoveClientHandler(id)` used to drop the handler *and the owner row* for any id at all, so the two rules above held only against a plugin that asked politely: `RemoveHandler(theirId)` followed by `OnReceive(theirId, mine)` walked around rule 2 in two lines, and `RemoveHandler(0x50726644)` silenced Core's prefab-definition handler for the rest of the generation — strictly worse than the foreign-payload case, because the remover can then neither register nor send on that id, `ReceiveSlotHandoff` still routes the fourcc, and the packet is dropped on the miss with nothing in the log. Removal is now the registration rule read backwards: the remover is the calling assembly (Core by reference, as above), it must be the recorded owner or Core, and a refusal writes one error line per id per Core generation and returns false. The four removal methods return `bool`. **All four attribute the remover the same way: the calling assembly** (R59 findings 2 and 7). The handler-matched overloads — the ones `PluginNet`'s unload disposables use, so unloading plugin A cannot silence plugin B — used to derive the remover from the presented handler, which is how the recorded owner was computed from that same delegate at registration, so the two sides were equal by construction and **no argument pair reachable through the public API could be refused**: a plugin holding another plugin's `CustomEventHandler` — from a shared registry, a published field, an event payload — removed it, got `true`, and nothing was logged. The docstring, this page and a test all said otherwise; the test drove a remover the production call never constructed. One attribution also ends the asymmetry the old wording volunteered half of: the id-only form always asked the caller, so one removal was refused through one door and allowed through the other. The cost is stated rather than hidden: **a handler declared in a library you ship must be removed from that library**, or through `PluginNet`'s handle (its disposable calls from Core), and an **un-attributable holder can be removed only by Core**, because no calling assembly can equal an identity that is a delegate instance. Removing an id nobody holds is not a refusal, just `false`.

Neither refusal throws. The caller gets a handle that removes nothing, and the log names both parties. Pick ids from your own four-letter tag: the Sample's are `Sm??` (`SmFx`, `SmUi`, `SmMd`, `SmNp`, `SmNr`).

**Where the rules live.** `Networking/CustomEventPolicy.cs` — game-free, so it is compiled into the test suite and the refusals are *driven* rather than pinned. It holds the predicate, all four refusal sentences (register, send, remove, clear), the `EventIdOwner` identity and `EventHandlerTable<T>`, which is the only thing that stores or erases a handler or an owner: a refusal returns out of it before either, so there is no store and no removal for a refusal to reach. The whole-table erasure asks a decision too — `ClearForUninstall` refuses anyone but Core (R59 finding 8); it was a bare `Clear()` with no decision in it, eleven lines under a docstring saying the two `Remove` overloads were the only things that erase a row. Both its callers were Core and correct, so nothing was reachable — but a false structural sentence in the one file whose claim is that its refusals are driven costs a reader the audit. That structure is the fix for R57 finding 4, which measured the old source pin to be worth nothing — deleting both `return;` statements after the refusal's log line, so every refusal stored the handler anyway, passed all twenty-five tests including the one named for it.

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct PingPayload { public int N; }

const int EventId = 0x53414D50; // "SAMP" - your own tag, and never 0x5072____

if (ctx.IsServer)
    ctx.Net.OnReceiveFromClient(EventId, (id, userEntity, user, payload, length) =>
    {
        if (CustomEvents.TryRead<PingPayload>(payload, length, out var p))
            ctx.Net.SendToUser(userEntity, EventId, p);
    });
else
{
    ctx.Net.OnReceive(EventId, (id, payload, length) => { });
    ctx.Net.SendToServer(EventId, new PingPayload { N = 1 });
}
```

Send: `SendToUser(World, Entity userEntity, eventId, in T)`, `SendToAll(World, eventId, in T)` (connected users only; returns how many sends were accepted), `SendToServer(eventId, in T)` — each returns false / 0 for a reserved id as well. `PluginNet` overloads that omit `World` use `ctx.World` and return false / 0 if it is not ready. Receive: `OnReceive` (client), `OnReceiveFromClient` (server), subject to the two id rules above. Unload removes **that event id** — one handler per id — and only if this plugin still owns it.

**Worked example: `samples/Progenitor.Sample/SampleNet.cs`**, the only compiled round trip in the repo and the one that exercises the client→server direction. A **rebindable F8** keybind (registered through `ctx.Keybinds`, and deliberately *not* `allowWhileCaptured`, so it is suppressed while a panel holds input capture) calls `ctx.Net.SendToServer`; the server's `ctx.Net.OnReceiveFromClient` runs the whole refusal ladder in one game-free `Admit` function — payload size (`CustomEvents.TryRead<T>`), magic word, counter bounds, the sender's handshake answer being `ok`, and a per-user packet cap for this **session** (the row is dropped on `PlayerDisconnected`, because `User.Index` is a connection slot the game recycles and an inherited budget would refuse the next player in that slot on their first press; R56 finding 6) — and only then answers with `ctx.Net.SendToUser` to the sender plus `ctx.Net.SendToAll` so every connected client logs the echo. Every refusal logs one line naming the sender and the reason.

Harmony cannot patch either receive path (by-ref struct). Native receive detours are process-lifetime: when `Progenitor.Bootstrap` is present it applies them once per process and Core writes its dispatchers into a slot (cleared on Unload, never `Dispose`d by Core). When Bootstrap is absent, Core owns the detour as before and logs that mode. Receive handlers are installed on first use. A packet that arrives while the slot is empty is passed to the game unless it is a Progenitor fourcc (`PrhH` / `PrhR` / `PrhQ` / `PrfD` / `PrfU` / `PrfK` / `PrfE` / `PrfI` / `PrcS` / `PrmM` / `PrmR` / `PrmC`), which is consumed and dropped so a Core swap cannot feed unknown event ids to the game. `ReceiveSlotPolicy.CoreProtocolIds` is the list Bootstrap drops while the slot is empty, and a new Progenitor fourcc belongs there as well as in Core. **Complete as of 2026-09-10:** Core registers twelve ids and the seed names all twelve, including `PrefabKeyIndex` (`PrfI`, `0x50726649`). Bootstrap change: effective at each process's next start.

**Server dispatch rules.** `ServerBootstrapSystem.OnDataReceivedServer` is the entry for *every* packet a client sends, including one from a connection the game has not approved yet. `TryDispatchServer` therefore makes exactly **one** call into `Networking/ReceiveGate.cs` — `Admit(...)` — and then does what the verdict says. All of the policy is in that one method, which is why the unit tests can drive the real ordering instead of a copy of it. Three stages, in order:

1. **Per-sender rate, first.** A token bucket keyed on the approved row's **`User.PlatformId`** once this connection has resolved once, and on the connection handle before that. Config `Networking.ReceiveBurstPackets` (default 60) and `Networking.ReceivePacketsPerSecond` (default 20) in `progenitor.cfg`. Over budget the packet is dropped and consumed (it is a Progenitor fourcc; the game has no handler for it) with one line per **sender** per minute: `CustomEvents: rate limit tripped for player 7656…; dropping Progenitor packets from it (20/s, burst 60). One line per 60 s.`
2. **Global backstop.** One O(1) bucket, capacity `ReceiveGate.GlobalCapacity` = `burst x (GlobalPerPlayer x players + GlobalFloorPlayers)` — 2 whole per-sender budgets for every tracked player, plus a 16-budget floor for connections with no approved identity yet. It is charged **only** by packets already inside their own sender's allowance. Over budget: `CustomEvents: server-wide Progenitor receive budget exhausted (…/s, burst …); dropping packets from every connection. One line per 60 s.`
3. **Resolution.** `EventHooks.TryFindUser` resolves the connection to an approved-user row. Its result used to be discarded and handlers were handed `user = default` — `User.Index == 0`, a real assignable index, not "no user". The first packet on a handle has its stage-1 charge *moved* from the pre-approval bucket onto that player's bucket, so a churning handle cannot mint budget. Nothing is wrapped in an IL2CPP object before this point: the dispatcher hands the resolver raw pointers and the `ServerBootstrapSystem` wrapper is cached for the life of the world, so a refused packet costs no allocation and no GC-handle registration.

**Fair share: the backstop is a ceiling, never a budget senders compete for.** The order above is the whole point. Charging one server-wide bucket *first* — what EQ8 did — makes every sender share one pool: a single client at 400 packets/s drained it and every other player was dropped at stage 1 with a line that named nobody, while the per-sender limiter never ran at all; and 40 honest players at 8 packets/s each hit the same ceiling with no misbehavior. Two rules fix it. **A sender over its own limit is dropped before the backstop is consulted**, so its flood costs the backstop nothing and cannot take an honest player's packet with it. **The backstop is sized per connected player**, and every player contributes `GlobalPerPlayer` (2) whole per-sender budgets while it can spend at most one — so the honest population can never exhaust it, and the backstop only trips on traffic no per-sender bucket accounted for (a peer churning connection handles, which mints a fresh pre-approval bucket each time). A player joining raises the capacity and brings its own allowance with it.

**Why the platform id and not the connection handle.** `Stunlock.Network.NetConnectionId` is an abstract class with **no fields**; identity lives in the concrete subclass (`SteamNetConnectionId.ID`, `LidgrenNetConnectionId.IPEndPoint`, `EosNetConnectionId.RemoteUserId`), each of which overrides `Equals`/`GetHashCode` with content semantics. The game keys three of its own dictionaries by the object and so relies on content equality, never on reference identity. Native analysis confirms the instance *is* reused per connection today, but that is an observation rather than a contract, and a pointer is recycled across connections anyway. Keying on the approved row's platform id makes the budget follow the player: a re-boxed or recycled handle cannot mint a fresh full bucket, and a reconnect never inherits a stranger's. `ModManifest`'s disconnect handler retires the bucket (`ReceiveGate.ForgetPlatform`).

**An unresolved sender is held, not dropped.** A legitimate join cannot reach the dispatcher without a row — `ServerNetworkLayer.OnDataReceived` forwards to this seam only in `UserConnectState.Connected`, and the transition into that state fires `OnUserConnected`, which is itself only reachable after `ApproveClient` wrote the row. The rule is still fail *safe* rather than fail closed, because a hosted-child row is documented as carrying a null `NetConnectionId` that no `Equals` can match: a connection seen for the first time within `UnresolvedGraceSeconds` (5 s) has its payload copied and held — at most 4 packets per connection, 32 in total, 128 KiB — and retried once per frame; it dispatches as soon as the row appears. Past the window, or with the hold queue full, it is dropped and consumed with one line per connection per minute: `CustomEvents: refusing event PrhR from unresolved connection 0x… (no approved-user row, past the 5 s hold window); no handler ran. One line per 60 s.` A held packet that never resolves logs `CustomEvents: dropping held event PrhR from connection 0x…; no approved-user row appeared within 5 s.` The gate releases the byte charge *it* made when a hold is released, never a number the caller supplies, so the 128 KiB budget cannot drift into a permanently-full state.

**The hold list and its drain policy are game-free** (`ReceiveHoldQueue<T>` in `Networking/ReceiveGate.cs`), so they are unit-tested rather than trapped in the `unsafe` dispatcher. A held packet's handler may trigger a Core reload, which uninstalls the receive path and detaches the drain from the middle of the drain's own loop. The drain carries the generation it started on: once detached, a re-hold is refused, the remaining packets are dropped with one line — `CustomEvents: dropping N held packet(s); the receive drain was detached (Core swap or uninstall).` — and their gate accounting is released. Without that the packets sat in a list nothing drained, the hold queue read as permanently full for the life of the process (so every genuinely first-seen connection was refused), and their connection rows kept `Queued > 0`, which both passes of the table prune skip, so those slots could never be evicted either.

**The limiter is visible.** Once per minute in which any Progenitor packet arrived: `ReceiveGate: N connections tracked, M dropped this window`. A limiter that never limits, or a table churning towards its 256-row cap with two players connected, shows up in the operator's log instead of being silent.

So an `OnReceiveFromClient` handler only ever sees a sender that resolved to a real `User` entity. Handlers should still treat everything in the payload as hostile, and a handler reached by any other path must refuse a null `userEntity` itself (Core's own handlers call `ReceiveGate.HandlerMustRefuse` and log `{module}: {fourcc} from an unresolved connection (no user entity); ignoring.`). Both tables are bounded (256 rows, idle entries dropped after 5 minutes).

**Handshake.** Server `Require(guid, version)` / `RequireSelf()`; client `Provide(guid, version)` / `ProvideSelf()`. `DeclareSelf()` is the side-agnostic form: `RequireSelf` on the server, `ProvideSelf` on the client, so a `Side.Both` plugin can call it once. Overloads `Require`/`Provide(guid, version, level, versionRule)` record a **declared** row. `DeclareSelf` reads `[NetworkCompatibility]` when present.

`[NetworkCompatibility(NetworkCompatibilityLevel, NetworkVersionRule)]` is the per-plugin declaration; `[NetworkCompatibility(level)]` leaves the rule undeclared and `Peers = "..."` declares a range ([C3](#compatibility-ranges-and-version-matching-mod-compatibility-c3)). Levels:

| Level | Meaning |
|---|---|
| `ServerMustHave` | The other side must have this plugin. Version follows the rule. |
| `NoNeedForSync` | Either side may lack it. If both have it, version follows the rule. |
| `ClientOnly` | Server ignores it (client-only UI). Version rule is unused. A server-side ClientOnly row is not sent and does not consume a handshake slot. |

Version rules: `Exact` (case-insensitive whole string; `1.0` ≠ `1.0.0`), `Minor` (a proper `major.minor` parse; `1.2.3` accepts `1.2.9`, rejects `1.3.0`; leading `v`, patch, and further dotted numeric components ignored — `1.2.3.4` is the same minor as `1.2.9`; a `-` pre-release token is part of the compare — `1.0-Beta` vs `1.0.5` is a mismatch; `+` build metadata ignored; identical strings always match, even when unparseable; an unparseable version that is not identical to the peer, including a lone `1` or `1.2.3foo`, is a mismatch), `None` (presence only).

**Progenitor's own version is a date, not SemVer** (decided 2026-10-09). `<year>.<month>.<build>` ([versioning.md](versioning.md)): `Minor` on a Progenitor version means "same year and month", which says nothing about compatibility. Progenitor's compatibility and what counts as a breaking Progenitor update come from its protocols (the Bootstrap protocol and `Handshake.ProtocolVersion`) and capability bits, never from its version number. **Major versions** in the update settings applies to mods only ([auto-update.md](auto-update.md#progenitor-itself)); a mod with SemVer versions keeps the rules above.

**Default without the attribute** (the original behavior on the wire; between two C3 Cores the version is judged by the [same-minor default](#compatibility-ranges-and-version-matching-mod-compatibility-c3)): `Require` / `DeclareSelf` is opt-in. The server’s required list is exact-match on guid and version; a missing guid or empty version is `{guid} missing` via `FindProblems`, and extra mods the client reports are ignored.

**Always request.** The server always sends the handshake, including when `Required` is empty (`Count = 0`). That is how a client-declared `ServerMustHave` is evaluated: the client volunteers extras on the reply. A v1 client sends none; reverse stays off.

**A re-ask never disconnects a user who already answered (REASK1, live 2026-10-07).** The server remembers who answered successfully this session in a plain process slot (an AppDomain data string of user index and SteamID; forgotten on disconnect, on a failing answer and at every join), so a new Core generation still knows. Its re-ask of such a user is soft: the same request, resent after 5, 10, 20 and 40 s while unanswered; no deadline, no notice, no kick. After the last wait the user stays connected and the log has `Handshake: ADMIN NOTICE: <name> has not re-answered after a Core generation change …`. The join-time rules (the answer deadline below, Players without Progenitor: Keep them out, the client mod policy's wait for a report) apply only at a join; an actual answer is still checked and a failing one still refuses. A client swapping its own Core at the same moment drops the request; its refresh after the swap (`client Core refresh requested`) is now answered while a soft re-ask waits. Before, the server answered a refresh only for an ok handshake, the new generation's status was pending, and the refresh was dropped, so the client never saw the request. A user who answered before this change was deployed is not in the slot yet, so the first swap after the deploy still asks them as a join. Source-checked and game-free tested (`HandshakeReaskTests`); not live-verified.

**Fail-closed answer deadline.** After the server asks at a join (and at the re-ask after a Core generation change of a user it does not remember as answered), it starts a per-user deadline: default 20 s, config `Networking.HandshakeAnswerTimeoutSeconds` in `progenitor.cfg`, 0 disables. The timer runs on the main thread through `MainThread.PostDelayed` (not `System.Threading.Timer`). It is canceled on answer and on disconnect, and re-arming (the Core-swap re-ask) invalidates the previous delayed post. If no answer arrives, the server kicks with the same readable-reason path as a mismatch (full text in the log, then Result + `Disconnect` 2 s later, truncated to 479 ASCII): `Progenitor: no handshake answer within N s (server requires: {guid} {version} {Level}/{Rule}, …)`.

Same-ALC Core restore Uninstalls Handshake (drops delayed posts, clears the watch, nulls the timeout `ConfigEntry`) then Installs on the same statics. That Install re-asks every currently connected user, including disabled, the same way a join does — one `Handshake: re-armed {name} after Install` line per user — and rebinds the timeout entry. A failed `SendToUser` does not arm a deadline the client cannot satisfy: the ask is retried once next frame, then the server waits 2 s (the same Ok back-off as a timeout kick) before sending a failure Result (`Progenitor: could not ask for a handshake answer`). After that Result it Arms the normal 20 s answer deadline, so a real reply at t=5 s is accepted rather than kicked at 4 s. A second 2 s hop then disconnects only when a mismatch that arrived after Result owns the kick (`DisconnectReasonAfterCouldNotAskResult`); an armed Pending row is left to the 20 s deadline. If Arm does not take (timeout 0) the second hop still disconnects with the could-not-ask reason. Timeout is already on the no-answer path and does not double-send. A retry never overwrites an `ok` row with a fresh deadline. `Unrequire` during a wait-miss refreshes the pending kick reason; the expire callback re-checks whether anything still requires an answer and cancels to unverified when the list is empty.

Enforced only when at least one server plugin is a level other than `NoNeedForSync` / `ClientOnly` (undeclared `Require` rows count: they are `ServerMustHave`/`Exact`). Otherwise the server logs once that the client is unverified and leaves them connected. Skip is fail-closed: only a positive ghost (`User.IsBot` and no peer) or hosted child (approved-user row with a null `NetConnectionId`) is skipped. A throw, a missing approved-user table, or a missing row for a non-bot arms the deadline. The `users` bridge op reports `handshake` per user (disabled User entities included). Tokens: `none` (not waiting: never asked, ghost, timeout disabled, disconnected), `asked` (waiting; renamed from `pending`), `ok`, `mismatch`, `timeout`, `unverified` (connected, nothing on the server requires an answer), `awaiting-install` (job FMAN18: answered, failed, and inside the install window below).

`ctx.Events.On` passes `ctx.Guid` as `pluginId` so equal-priority order is stable across a plugin reload (load-order slot is sticky per guid). `ctx.Config.Bind(..., synced: true)` throws; use `ctx.Bind(..., synced: true)` so unload unregisters. Synced config snapshots use Handshake's `NetworkVersionRule`: Exact mismatch is omitted; Minor includes the same major.minor and applies only when the client has the same section/key **and** BCL type (schema-drift otherwise; a v1 packet with no type name is refused under Minor with an explicit type-gate line); None is presence only (versions not compared) and still includes the snapshot. Plugin unload while still in the session keeps the lock; a server push in that gap updates the session lock, and a re-bind re-attaches to the newest server value. Explicit `ConfigFile.Save()` while locked writes the pre-join local (orphaned), not the server value, including keys bound after the lock. Later config pushes walk the same connected-user set as handshake re-arm, including disabled `User` entities. A config record that cannot fit `MaxPacketBytes` is logged and omitted.

**Mod manifest (phase 2).** After an accepted handshake the server sends a signed catalog (`PrmM`) — handshake result, then the config snapshot, then the catalog. `ModManifest.Pending` is the last received envelope plus missing/mismatched lists and the first-contact / host-key-change flags. The first verified catalog from an address with no pin pins its operator key in `BepInEx/config/progenitor-trusted-operators.json` (trust on first use, 2026-10-05); a later key change for that address is flagged and never re-pinned. `TrustOperator(fingerprint, always)` remains the explicit, client-origin pin (atomic replace; read once per join). Trust is host-aware: a pin made at one address does not trust the same key at another unless it was deliberately recorded host-less. **Servers never send mod files (removed 2026-10-05; [client-mod-policy.md](client-mod-policy.md#security)).** `ctx.Handshake.OfferPackage(path)` and `ctx.Handshake.TryGetOfferedPackageState(out sha256, out size, out reason)` (and `ModManifest.Offer` / `ModManifest.TryGetOfferTransferState`) are `[Obsolete]` no-ops kept so an older plugin still loads: `OfferPackage` returns a handle whose `Dispose` does nothing and logs one warning per mod id per Core generation (`ModManifest: OfferPackage(<id>) does nothing: servers no longer distribute mod files; …`); `TryGetOfferedPackageState` returns false with `reason` `"removed"`, an empty hash and size 0. To let joining players install a mod, publish it on Thunderstore and name its `package` in the server's `progenitor-manifest.json`. The retired `PrmR`/`PrmC` ids keep handlers that drop the packet with one log line. Full detail: [mod-manifest.md](mod-manifest.md). Sample `.mods` / `.mods trust [always]` forwards the action to the calling client.

**Every post-`ok` effect is behind one per-user 5 s bound when the user was already `ok`** (job FMAN22, R53 finding 3; `Handshake.RunPostOkEffects`): there are exactly three of them — the synced-config snapshot (`ConfigSync.OnHandshakeOk`), the signed catalog (`ModManifest.OnHandshakeOk`) and the prefab edit-layer resend (`PrefabRegistry.ResendEditLayersTo`, re-answer only) — and a second `DeclareSelf` inside `HandshakeReAnswer.MinSecondsBetween` runs none of them and logs one line, because otherwise a client looping the packet buys a full re-serialization of the server's synced config plus a catalog rebuild per packet on the receive thread for the cost of one small inbound packet (FMAN21 had bounded only the resend, which was also the only one already deduped per user per payload hash). The **first** answer of a join neither consults the bound nor spends it, so a join never waits for its config; the bound is on a repeated `ok` and not on a change of state, so a re-answer whose rows now fail takes the mismatch branch and is kicked immediately, and a user whose status has left `Ok` comes back through as a first answer.

**Mod manifest phase 3 (consent and install) was removed 2026-10-05.** The consent panel, the install of a server's copy into `progenitor-pending/` or `progenitor-reload/`, and the Settings panel's **Server mods** group are gone. Once per Core generation the client removes what an older Core installed from a server (`LegacyServerMods.CleanUpAtStart`: only files whose bytes still match the recorded sha256) and the version store's kept server copies ([mod-manifest.md](mod-manifest.md#legacy-cleanup-at-core-start)). Bootstrap's start-time promote of `progenitor-pending/` (`PendingInstalls`) is unchanged; the Install tab's restart route uses it. `ReloadAllowStore.TryWriteAllowRow` still never creates `progenitor-allow.txt`, and `ReloadAllowStore.TryCreateAllowList(path, rows, coreImagePath, out error)` still hashes Core's image itself and refuses content that does not list it.

**Join state ends with the connection.** The previous server's catalog and join screens are dropped when the client's server context is cleared: connection end (kick, timeout or walk to the menu, with `Client_0` still alive), a new join's handshake request, or `Client_0` ending. A refusal stays on the disconnect screen until the client reaches the main menu. A Core-only refresh of a live connection clears nothing. Not live-verified.

**Deferred kick and install window (job FMAN18, retargeted 2026-10-05).** When a handshake answer fails and **every** failing row is one this server enforces and the client can fix during the join (`Handshake.Compare.DecideMiss` with `MissFix` from `Networking/InstallWindowFixes.cs`: a Thunderstore package the signed catalog names for the row that serves the needed version, read by the server in the background through `ServerPackageAvailability` with `[Install] LocalIndexPath` in front, a package not read yet counting as no fix; or a kept or turned-off copy the client declares in the reserved `progenitor.hs.fix` answer row), the server does not kick. It opens a per-user window — default 60 s, config `Networking.InstallGraceSeconds` in `progenitor.cfg`, 0 disables, **one per join, never extended** — sends the `Result` packet with `Ok = Handshake.ResultDeferred` (2) and the ASCII message `Mods needed: {names}. Install from the join screens within 60 s, or this join is refused.`, then the same narrowed signed descriptor the deny path sends. The user is never added to `HandshakeOkUsers`. The client fixes the rows from its join screens and answers again; that answer closes the window and is re-evaluated from scratch by the same code path as the first. No passing re-answer and the window lapses with `Mod mismatch: {names} was not confirmed within 60 s (this client did not answer again in time). If it is installed now, join again; if not, install it from the join screens first.` The client mod policy uses the same window (`InstallWindowFixes.PolicyFixable`, [client-mod-policy.md](client-mod-policy.md#rule-4-as-built-2026-10-05)). **The deferral is gated on the client's declared capability** (job FMAN20): `Handshake.Capability.InstallWindow` in the reserved `progenitor.hs.cap` row (`ClientOnly`, appended last, never compared as a plugin); the fix row follows it only when a slot is free, and an older server ignores both. A client that declares nothing is kicked immediately. **A Core swap does not refund the window:** the next `Install` re-derives the spent mark from the connection (`Handshake.MarkCarriedOverInstallGrace`). **The wire is unchanged** — same event id, same 481 bytes, same `ProtocolVersion` 2. Full flow and log lines: [mod-manifest.md](mod-manifest.md#phase-5--the-deferred-kick-and-the-install-window-job-fman18-server-half). Source-checked and game-free tested; not live-verified.

**The client half of the install window (job FMAN19).** `Ok == 2` is classified once (`ClientInstallWindow.Classify`) as *deferred* rather than denied, so the read-only deny report stays shut and the join screens open in install-window mode; a second deferred `Result` in one join is ignored. The window itself installs nothing (`Networking/ModInstallWindow.cs`): every fix is the join screens' (a Thunderstore batch, Use kept, Turn on, Pause for this server), and each re-answers the handshake once its fix is declared (`Handshake.ReDeclareSelf`, from the remembered `ServerAsked` rows on the same connection). The deferred `Result` carries no window length as a number, so the screens show the server's sentence and no countdown.

**With the attribute:** the server evaluates each of its plugins against the client’s list, then each *declared* client plugin against the server’s (undeclared extras stay ignored). One reason string per failing guid: `{guid}: expected {want}, got {got}` for a version mismatch. F25 (2026-10-07; built and game-free tested in `HandshakeEvaluateTests`, not live-verified): a missing mod names the side that lacks it and its fix. A server row this client lacks reads `{guid}: the server needs it ({want}), but your game doesn't have it. Install it from the join screens`. This client's declared must-have mod that the server lacks reads `{guid}: your game needs it on the server ({want}), but the server doesn't have it. Turn it off, or ask the admins to install it`. It used to read `expected {want}, got missing` both ways. A mismatch is logged in full on the server, then both the result packet and `PlayerControl.Disconnect` (after 2 s) get the same string truncated to 479 ASCII with `...`.

**16-mod cap.** The wire is still 16 slots. Packing prefers declared `ServerMustHave` over weaker rows. Recording a 17th declared guid on the **server** evicts an undeclared slot-consuming row when one exists; otherwise that guid is refused (logged at Require, not recorded) and the first 16 stay. That refusal does **not** kick joiners — a 17th server plugin is invisible to the handshake rather than a global ban. `Unrequire` retries a previously refused declared row when a slot frees.

**Client mod policy (CLIENTPOLICY1 stage 1; source-checked, built and game-free tested, not live-verified).** An admin can require client mods the server does not run and limit players to approved client mods ([client-mod-policy.md](client-mod-policy.md)). With a policy active the server's request carries the capability row with `Handshake.Capability.ClientPolicy` (128); a client that sees it sends its plugin inventory on the reserved event `'PrCm'` right after each handshake answer, and declares the same bit in its answer. The server holds an accepted first answer until the report arrives (inside `Networking.HandshakeAnswerTimeoutSeconds`), then accepts or refuses with `Mods needed: …` / `Mods not allowed on this server: …`. While a policy needs it (a required client mod, or Only approved with Refuse them), a client that never answers is refused by the answer deadline even when no server plugin requires an answer. Every check relies on the client's own report: house rules, not anti-cheat. `NetworkVersionRule` is unchanged; the admins' `AtLeast` rule is `Handshake.Compare.AtLeast` and applies only to their rows.

**What a player without Progenitor sees (NOPROGNOTICE; source-checked, built, game-free tested in `NoAnswerNoticeTests`, and live-verified for a new player).** **Live 2026-10-05 (unmodded Steam client, BepInEx off, joining a fresh save on the isolated test server as a new player).** Through character creation nothing was sent and nothing timed out; once the character Carthel spawned in the crypt the server logged `has a character now; the 20 s answer countdown starts`, then notices at 15, 12, 9, 6 and 3 s left. On screen the short Local line floated over the character ("This server needs Progenitor. Disconnecting in 15 s.", counting down) and the full System line listed the four required mods with "Install them with r2modman or the Thunderstore app, then join again" (the wording at the time; since 2026-10-05 the default names only Progenitor and where to get it, below); then the stock `You were kicked from the server` screen. Seen: the log names the player as blank while the account had no character name yet. A client without Progenitor (or with BepInEx off) never answers the handshake, so the answer deadline kicks it. **Live 2026-10-04** (an unmodded Steam client joined the isolated test server): the player loaded fully into the world, played about 20 s, then saw only the game's stock `Communication with server was lost. Reason: You were kicked from the server`; none of Progenitor's text reached them (the server logged `Handshake: TestPlayer failed: Progenitor: no handshake answer within 20 s (server requires: …)`, then the kick). **Live 2026-10-05**, kicking the Steam client through `PlayerControl.Disconnect` with a message under different reasons: the disconnect screen never shows the message. `Kicked` reads `Reason: You were kicked from the server`; `IncorrectVersion` reads `Reason: IncorrectVersion` (the raw enum name); `Banned` treats the text as a ban-expiry date (`You are banned (Monday, January 1, 0001 12:00:00 AM)`); `Unknown` reads `Reason: k_ESteamNetConnectionEnd_App_Min`. So the deadline kick keeps `Kicked`, and chat is the only channel for the server's words. Two chat channels were tested live on 2026-10-05 (the vanilla UI is the same on an unmodded client):

| Channel | What the player sees |
|---|---|
| **Local chat addressed to the player as the speaker** (`Chat.SendTo(server, user, text, ChatMessageType.Local, speakerUser: user)`) | The text as floating speech over their own character, mid-screen, plus a chat line `[Local] <name> …`. Only that user receives it. The prominent channel. |
| **System chat** (`Chat.SendTo(…, ChatMessageType.System)`) | The chat box pops open, unfocused, bottom-left, small orange text, for about 14 s: visible but easy to miss (a first test saw it only after pressing Enter). |

So the deadline runs as a countdown with a notice, for that one user only:

1. **Held during character creation.** A new player creates a character first, and that screen has no chat or HUD: a notice there is invisible, and the old deadline kicked them mid-creation. While the user has no character in the world (`User.LocalCharacter` unset or not existing), both the notice and the kick wait. The countdown starts when the character is in the world; a user whose character already existed at the ask counts from the ask, as before. After `Networking.NoAnswerCreationLimitSeconds` (default 600) from the ask without a character, the existing kick happens anyway.
2. **The notice.** `Networking.NoAnswerNoticeSeconds` (default 5; 0: no chat message, the disconnect still happens; at or past the deadline it is moved to one second before it) after the countdown's start, then every `Networking.NoAnswerNoticeRepeatSeconds` (default 3, at least 2) until the kick, each with the seconds left. Every notice sends the short line as Local chat to self, `This server needs the Progenitor mod. Disconnecting in 12 s.` (fixed format, at most 70 characters, one line over the head); the first and every other one also send the full line as System chat: `This server uses mods. To join, install the Progenitor mod: get it free with a mod manager, r2modman or the Thunderstore app (thunderstore.io, game: V Rising). Progenitor sets up the rest of this server's mods when you join again. You'll be disconnected in 15 seconds.` (decided 2026-10-05: name only Progenitor, say it is a mod and where to get it; once installed, its join screens offer the rest. An admin's own text can still use `{mods}`: the server's enforced mods with their readable names and versions, then the admins' required client mods; the first five, then `and N more`; printable ASCII, no chat markup, inside the 509-byte chat cap). Admins can replace the full line with **Message without Progenitor** on Progenitor's page (Server settings; [join-flow.md](join-flow.md#server-message-settings-admins)), tokens `{mods}` and `{seconds}`; empty sends the default.
3. **The kick** is the existing timeout kick (`Kicked`) at the countdown's start plus `HandshakeAnswerTimeoutSeconds`. A client that answers at any point, leaves, or stops being required to answer (a plugin or the client mod policy changed) stops the countdown at once.

Ghosts and a hosted private game's own client are never asked, so they get neither. The same countdown and notice apply when the [client mod policy](client-mod-policy.md) makes the server require an answer (a required client mod, or Only approved with Refuse them). **The channel is one seam:** `Handshake.NoAnswerNoticeSender` (`INoProgenitorNoticeSender.Send(Entity user, NoProgenitorNotice notice)`, default `ChatNoticeSender`), so another channel can replace or join it without touching the timing or the words. Log lines: `Handshake: notice to <name> (no handshake answer; N s left): sent=true "…"`, `Handshake: <name> has a character now; the 20 s answer countdown starts`. Bridge: `server join_admin {"action":"notices"}` ([bridge-ops-join.md](bridge-ops-join.md#join_admin)).

**Before they join**, the server's description in the game's **Join Game** browser ends with `Requires the Progenitor mod (thunderstore.io)` unless an admin turned the Progenitor signature off (SERVERDESC1, [server-list-description.md](server-list-description.md); source-checked, built and game-free tested, not live-verified). A dedicated server running Progenitor publishes it at start.

On the **client** reply, a dropped `NoNeedForSync` extra is omitted only: it does not fail closed and never overwrites an asked-row answer. Only a dropped declared `ServerMustHave` extra (or 16 declared `ServerMustHave` rows that need a dedicated `progenitor.hs.meta` record and cannot fit it) fail closed, with `progenitor.hs.overflow` on the wire (`handshake cap: {guid} did not fit in 16 slots`). The server never puts that overflow marker on its request.

Wire is backward compatible: still at most `Handshake.MaxMods` (16) ASCII entries (guid 63 bytes, version 23). Declared level and version rule for versions of at most 21 characters are packed into version-slot bytes 22–23 (high bit of byte 22 set) so a v1 ASCII read still stops at NUL. Longer legal versions keep all 23 characters; their metadata travels in a dedicated `progenitor.hs.meta` record (a v1 peer ignores that extra guid). `Handshake.ProtocolVersion` is 2 for that layout; packet size and event ids are unchanged. The client replies with the asked rows plus any extra declared mods that fit. Unload drops the row by guid so a reload does not kick the next join for a stale version. `Handshake.LastClientResult` is the last result string seen on the client. Evaluation is `Handshake.Compare.Evaluate` (pure; two `ModRecord` lists).

### Feature negotiation and versioned messages (mod compatibility C1/C2)

Two versions of a mod can talk to each other when the mod asks what the other side can do (`ctx.Features`, `ctx.Peer`, `ctx.Peers`) and sends versioned messages (`ctx.Messages`) instead of fixed-size `ctx.Net` structs. Design and later phases: [mod-compatibility.md](mod-compatibility.md). Worked example: the test plugin [`samples/Progenitor.CompatTest`](../samples/Progenitor.CompatTest/README.md).

**Status:** source-checked, game-free tested (`FeatureTableTests`, `ModMessageCodecTests`, `ModMessageGateTests`, `ModCompatEndToEndTests`) and verified live on 2026-10-09 (Steam client and the isolated test server, the test plugin at 1.0.0 and 1.1.0, each side newer in turn, with hot reloads and a client Core swap in session). Of the `SendResult` values only `Sent` and `PeerLacksMod` were observed live. Not observed live: `PeerTooOld` (a peer on an older Core), `PeerNotReady`, `NotConnected`, `TooLarge`, `Invalid`, `TransportFailed`, a `MinReaderVersion` drop, the `more` truncation, the merged-table cap and the held table of a not-yet-accepted user (the review fixes after the live run; game-free tested only), a server Core swap with players connected, and a headless client.

**Offer features** (both sides, in `OnLoad`). A name is 1 to 48 characters of `[a-z0-9][a-z0-9._-]*` and is scoped to this mod's GUID; a level is 1 to 65535 and only grows; a mod offers at most 32 features. A bad name or level, or a 33rd feature, throws `ArgumentException`. Every offer is tracked: unload withdraws it.

```csharp
ctx.Features.Offer("trade");            // level 1
ctx.Features.Offer("trade", level: 2);  // the level peers see is the highest live offer
IReadOnlyDictionary<string, int> mine = ctx.Features.Offered;
```

**Ask the other side.** `IPeerView` has `Known`, `Has(feature, minLevel = 1)` (this mod's feature), `Has(modGuid, feature, minLevel = 1)` (any mod's), `ModVersion(modGuid)` and `CoreVersion`. `Known` is false until the peer's table arrives, and always false for a peer whose Progenitor predates feature negotiation; every `Has` is false while it is. On the client, `ModVersion` falls back to the server's handshake rows for a mod the table does not list.

```csharp
// Client: the joined server. On the server ctx.Peer.Known is always false.
ctx.Peer.Changed += () =>               // the server's table arrived, changed (a hot reload there) or went away
{
    if (ctx.Peer.Has("trade", minLevel: 2)) SendTradeV2(); else if (ctx.Peer.Has("trade")) SendTradeV1();
};

// Server: one view per connected client. On a client every view answers Known = false.
ctx.Peers.Changed += user =>            // that user's table arrived or changed, or the user left
{
    if (ctx.Peers.Of(user).Has("trade")) ctx.Log.LogInfo("this client trades");
};
```

Both events run on the main thread and are tracked (unload unsubscribes). `Peers.Changed` also fires when a user leaves; `Of(user).Known` is then false.

**Send versioned messages.** A message is a class with `[ModMessage(name, Version = n)]` and numbered `[Field(n)]` fields or properties. Supported types: `bool`, every integer type, `char`, `float`, `double`, `string` (UTF-8, at most 4 KiB), `byte[]`, enums, `PrefabGUID`, nested classes with a public parameterless constructor, and `T[]` / `List<T>` of these (no lists of lists). A field number is never reused or retyped. Decoding starts from `new T()`, so a field the sender did not write keeps its initializer, and a field the reader does not know is skipped. An enum value the reader's type does not define keeps the default and logs once.

```csharp
[ModMessage("trade.offer", Version = 2)]
public sealed class TradeOffer
{
    [Field(1)] public int ItemId;
    [Field(2)] public int Count;
    [Field(3, Since = 2)] public string Note = "";   // a 1.x sender omits it: the reader keeps ""
}

ctx.Messages.On<TradeOffer>((from, msg, info) =>    // from: the sender's user entity (server) or Entity.Null (client)
    ctx.Log.LogInfo($"offer v{info.SenderVersion} from mod {info.SenderModVersion}: {msg.ItemId} x{msg.Count} {msg.Note}"));

SendResult r = ctx.Messages.SendToServer(new TradeOffer { ItemId = 7, Count = 3 });   // client
ctx.Messages.SendTo(userEntity, offer);                                                // server, one user
int n = ctx.Messages.SendToAll(offer);                                                 // server, users whose client runs this mod
```

`SendResult` says why nothing went out: `NotConnected` (no joined server, or the user is not connected or not past the handshake), `PeerTooOld` (the peer did not declare `ModMessages`), `PeerNotReady` (its table has not arrived), `PeerLacksMod` (its table does not list this mod), `TooLarge` (over `ModMessageEnvelope.MaxPayloadBytes`, 15 360 − 16 bytes, or a string over 4 KiB), `Invalid` (a bad name or version, a hash collision, a type that is not a valid message; one error line) or `TransportFailed`. `On<T>` and `On(name, …)` throw `InvalidOperationException` for an invalid message type (including a nested class with no `[Field]` members, a collection other than `T[]` / `List<T>`, or a game interop type other than `PrefabGUID`) and when the registration is refused: a second handler for the same message, a message name or mod GUID whose 32-bit hash collides with one already registered (the message names both), or a bad name or version. That is the same contract as `Features.Offer`: a mistake fails `OnLoad` rather than leaving a handler that never runs.

**Threads.** `Send*` run on the game's main thread only, the thread `OnLoad`, event handlers, message handlers and `MainThread.Post` callbacks run on; from any other thread they throw `InvalidOperationException`. Post the send with `MainThread.Post` instead.

**Breaking changes** raise `MinReaderVersion`: `[ModMessage("trade.offer", Version = 3, MinReaderVersion = 3)]`. A reader below it drops the message with one line per message name per Core generation, `Messages: dropped trade.offer v3 from <sender>: needs reader 3, this mod reads 2`. Check `Peer.Has` first; the compatibility contract makes a breaking message change a major version.

**Hand-written codec** for hot paths, wire-compatible with the classes (signed integers are zigzag varints):

```csharp
ctx.Messages.On("trade.tick", (from, r, info) => Tick(r.Int(1, 0), r.String(3, "")), readerVersion: 1);   // r: MessageReader
ctx.Messages.SendToServer("trade.tick", new MessageWriter().Int(1, 42).String(3, "x"), version: 1);
```

**What Core does on the wire.** Two Core event ids, registered through the existing owned-id path, so a peer without a mod's handler drops the packet in Core rather than handing it to the game:

- `'PrFt'` (`0x50724674`), the feature table, `[version u8][kind u8][seq u16le][count u16le][nbytes u16le][bytes]`, kind 1 full and kind 2 delta, at most 32 KiB in at most three packets. The body is UTF-8 JSON, `{"v":1,"core":…,"coreSha":…,"bootstrapProtocol":5,"mods":[{"id":…,"ver":…,"f":{"trade":2}}],"more":true?}`; a delta lists changed rows whole and `"gone":[ids]`. Every loaded Progenitor plugin is listed. Unknown keys are ignored.
- `'PrMs'` (`0x50724D73`), the message envelope, `[envelope version u8][flags u8][mod hash u32le][message hash u32le][message version u16le][min reader version u16le][payload length u16le][payload]` (16-byte header). Hashes are FNV-1a 32 of the lowercased mod GUID and of the message name. Received messages pass the per-connection `ReceiveGate` first; on the server they are dropped until that user's handshake is ok, and a message for a mod or message this process does not handle is dropped with one line per mod or message per Core generation.

Two capability bits gate both: `Handshake.Capability.ModFeatures` (32768) and `ModMessages` (65536). Every server request carries them, and so does the request the server sends after a client Core swap. The client sends its full table right after each handshake answer to a server that declared `ModFeatures`; the server sends its full table after an `ok` to a client that declared it. Changes (an offer, a plugin load or unload) are collected for one second and sent as one delta to every peer that has the table, at most one per second. After a Core swap the new generation's re-handshake sends both full tables again. The server does not hold the join for the client's table (the join-time wait the design describes comes with the range evaluation of phase C3, which needs it). A table that arrives before that user's handshake is `ok` is held, one per user, and applied at the `ok`; until then the user's deltas are dropped and `Peers.Changed` does not fire. A delta that would be cut (rows over the byte cap, or more than 256 removals) goes out as a full table instead. A peer's merged table never holds more than 256 mods however many deltas arrive (the rest set `more`), and each delta costs only its own rows.

**Raw `ctx.Net` warning.** When a mod sends through `ctx.Net` while a peer's table lists the same mod, and the two may be on different versions (the versions differ, or the mod declares no `[NetworkCompatibility]` or a rule other than `Exact`), Core logs once per mod and event id per Core generation: `Messages: example.trade sends raw ctx.Net messages (id TrOf); peers on another version may misread them. Use ctx.Messages.` It never blocks the send. `ctx.Net` itself is unchanged.

### Compatibility ranges and version matching (mod compatibility C3)

**Status: source-checked, built and game-free tested (`CompatRangeTests`, `VersionMatchingTests`, `VersionMatchingSettingTests`, `CatalogDualSignTests`, `CatalogMatchingClientTests`, `ConfigSyncRangeTests`, `PackageCompatRangeTests`, `PreJoinRangeTests`, `StrictMatchPauseTests`); not verified live. Coverage and what remains: [C3 as built](mod-compatibility.md#c3-as-built).** Design: [mod-compatibility.md](mod-compatibility.md#2-declared-compatibility-range).

A mod declares which **peer versions of itself** it works with. When the server and the client run different versions, the **newer** side's range must include the older version; equal versions always pass.

```csharp
[BepInPlugin("example.trade", "Trade", "1.4.0")]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, Peers = ">=1.2.0 <2.0.0")]
public sealed class Plugin : ProgenitorPlugin<PluginContext> { ... }
```

```json
{ "id": "example.trade", "version": "1.4.0", "side": "both",
  "compatibility": { "peers": ">=1.2.0 <2.0.0" } }
```

| Syntax | Means |
|---|---|
| `>=1.2.0 <2.0.0` | Space-separated comparators (`>=`, `>`, `<=`, `<`, `=`); all must hold |
| `1.2.0`, `=1.2.0` | Exactly that version (`=nightly-3` matches that text when it is not numeric) |
| `1.4.x`, `1.x` | The same minor, the same major |
| `*` | Any version |
| `1.2.x \|\| 1.4.x` | Alternatives |

At most 64 characters. Versions compare as numbers with missing parts as zero (`1.2` equals `1.2.0`), a leading `v` and `+` build metadata ignored, and a prerelease below its release; `1.4.x` takes in `1.4.0-beta` and not `1.5.0-beta`. `CompatRange.TryParse` / `Includes` / `Describe` are public for mods that want the same rules. An invalid `Peers` is ignored with one warning line at load.

**The two declarations must agree.** Package inspection refuses a package whose `progenitor.json` `compatibility.peers` contradicts the attribute in its plugin image; `tools/make-dummy-package.ps1` writes the field from the built image's attribute ([mod-manifest.md](mod-manifest.md)).

**A mod that declares nothing gets the same minor version.** The effective range of a build is its `Peers`; else an explicit rule (`Exact`: exactly this version, `Minor`: the same minor, `None`: any); else the same minor (`1.4.x` for 1.4.0). **Behavior change:** before C3 a mod without the attribute, or one whose attribute omitted the rule, was held to `Exact`; between two C3 Cores it is now held to the same minor. `[NetworkCompatibility(level)]` (no rule) is a new constructor, so a plugin compiled before C3 always carries an explicit rule and keeps `Exact` until it is rebuilt. The handshake row still carries the attribute's rule byte (`Exact` when undeclared), so a Progenitor older than C3 on the other side sees exactly the old behavior.

**Who is judged with ranges.** The client declares `Handshake.Capability.VersionRanges` (8388608) and its feature table lists every mod's effective range (`peers`). The server waits for that table after the client's answer, at most 3 seconds (`FeatureTableHold`), then judges; if the table does not come it judges the handshake rows alone. A client whose Progenitor predates C3 sends no ranges: the server uses its own declared `Peers` when it has one, else the row's rule, which is the old behavior; for a row the client itself declared, when the client's build is the newer one, the client's own rule decides, as before C3. The server holds the answer's deadline while it waits for the table, and a mod report that arrives during the wait is kept for the client mod policy. Accepted differences log `Handshake: example.trade client 1.2.3 accepted by server 1.4.0's range >=1.2.0 <2.0.0`.

**Refusals read as sentences** after `Mod mismatch: ` (the mod's id first):

| Case | Reason |
|---|---|
| The server is newer and its range excludes the client | `example.trade: this server's 1.4.0 works with 1.2.0 or newer, below 2.0.0; you have 1.1.0` |
| The client is newer and its range excludes the server | `example.trade: your 1.4.0 works with any 1.4.x version; this server has 1.2.0` |
| Exact version for all, or an Exact rule on the mod | `example.trade: this server requires exactly 1.2.0 (you have 1.3.0)` |
| Same minor for all, or a Same minor rule | `example.trade: this server requires the same minor version as 1.2.0 (1.2.x; you have 1.3.0)` |
| An "or newer" rule on the mod | `example.trade: this server requires 1.2.0 or newer (you have 1.1.0)` |
| A client older than C3, no declared range | unchanged: `example.trade: expected 1.4.0, got 1.4.1` |

**Version matching** is the server admins' choice on the Server tab (Mods players need): **Use each mod's declared range** (the default), **Same minor for all** or **Exact version for all**, plus a per-mod rule for any mod the server runs, which wins over the server-wide choice ([client-mod-policy.md](client-mod-policy.md#version-matching-mod-compatibility-c3)). A per-mod rule is written into the request row's rule byte in every mode (`Exact`, `Minor`; **Any version** and **or newer** as `None`, so no client pauses or swaps away a newer copy an "or newer" server accepts). Under a strict mode the server writes the strict rule into its handshake request rows and sets `Handshake.Capability.MatchSameMinor` (16777216) or `MatchExact` (33554432), so any client predicts the judgment: a hot-loadable optional (`NoNeedForSync`) mod both sides run at another version is paused for that server before the client answers, and a mod that cannot be paused is refused. Required client mods keep the rule on their own row. Exact for Progenitor itself is not part of C3 (it is C4).

**Synced settings follow the same judgment.** A plugin's synced settings go to a client whose version the join accepted, by range or rule. A C3 client locks a synced key the server's snapshot did not carry (the server's mod predates it) to the key's declared default for the session; the local value comes back on disconnect ([config-sync.md](config-sync.md)). **Behavior change:** before C3 such a key kept the client's local value, unlocked.

**The catalog carries ranges for clients that read them.** The server signs its catalog twice with the same key: the old form for clients older than C3 (an older client drops a key it does not know and would fail the signature) and a form whose rows add `peers` and `match`, and whose body adds the server's `matching`, for C3 clients ([mod-manifest.md](mod-manifest.md)). The pre-join prompt, the update cap and the kept-copy choice use them ([auto-update.md](auto-update.md)); until a server's catalog has given a mod's facts (a first join, a server older than C3), they reason with the request's rule byte only, never with the client's own range. A change of Version matching or of a per-mod rule re-signs the catalog at once and resends it to connected players, whose clients then reason with the new mode; the join judgment itself changes from each player's next join.

### Testing your mod across versions (mod compatibility C5)

**Status: source-checked, built and game-free tested (`MessageCompatTests` in `tests/Progenitor.Testing.Tests`, `ModMessageRecordTests`, and the samples' replay tests); not verified live. The `messages_record` bridge op has not run in the game.** Coverage: [C5 as built](mod-compatibility.md#c5-as-built). Step by step: [tutorial](tutorial.md#6c-make-your-mod-work-across-versions).

`Progenitor.Testing` (`src/Progenitor.Testing`) is a library for a mod's **own test project**. It has no game, BepInEx or IL2CPP reference: it compiles Core's message codec, envelope and range sources, so a payload encodes and decodes in a test exactly as it does in the game. It checks what `ctx.Messages` promises across versions: an older build reads a newer build's messages (unknown fields skipped), a newer build reads an older build's (missing fields keep their initializer), no field number was retyped, and a declared `Peers` range is backed by recorded messages.

**Set up the test project.**

1. Keep the mod's `[ModMessage]` classes in their own file that uses only `Progenitor.Networking` (no game types; a `PrefabGUID` field needs the game, so carry its hash as an `int` in a message you want to test).
2. In the test project (any framework; the samples use xUnit), reference `src/Progenitor.Testing/Progenitor.Testing.csproj` and link the message file: `<Compile Include="..\MyMod\MyModMessages.cs" Link="Linked\MyModMessages.cs" />`. Linked as source, the classes bind to the library's copy of the attributes. Referencing the mod's built DLL instead does not work: `MessageTypes.In` refuses a class that carries the game assembly's `[ModMessage]`.
3. Keep fixtures in a folder of the test project and copy them to the output: `<None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />`.

**Fixtures.** One fixture is one recorded payload (the bytes after the 16-byte `'PrMs'` header) and a JSON sidecar beside it:

```
Fixtures/<mod version>/<message>@v<n>/<sha12>.bin
Fixtures/<mod version>/<message>@v<n>/<sha12>.json   {"format":1,"mod":…,"modVersion":"1.2.0","message":"trade.offer","version":2,"minReader":1,"direction":"to-server","source":"live","recorded":…,"sha256":…,"schema":[{"n":1,"name":"ItemId","type":"int"},{"n":3,"name":"Note","type":"string"}]}
```

`<mod version>` is the version of the mod that **wrote** the payload. The file name is the payload's SHA-256 prefix, so a payload recorded twice is one fixture. The sidecar is authoritative; the folders only sort. `schema` is the writer's field schema (number, name, codec type such as `int`, `uint`, `enum:int`, `string`, `bytes`, `message` with its own `fields`, and `list` for a repeated field); it is what lets a later build see a retype that keeps the wire type (`int` to `uint`, `string` to `byte[]`). Fixtures written by a test and live recordings of a typed send carry it; a live recording of a received or hand-written message does not, and is then checked by wire types only (the result says so). `MessageFixtures.Load` refuses (throws) a sidecar of another format, a missing payload or a payload whose SHA-256 does not match, so a damaged fixture never passes silently. Fixtures come from two places:

- **Live**, with the Dev bridge's [`messages_record`](bridge-ops-join.md#messages_record) op: every message one mod sends or receives on that side, written under `BepInEx/progenitor-dev/messages/<mod>/` in this layout (`source: "live"`). Copy that folder into the test project.
- **From a test**, with `MessageFixtures.SaveRelease(dir, mod, modVersion, messages)` (`source: "test"`): every message a release sends, once. It refuses when `<dir>/<modVersion>` already exists: a released version's fixtures are frozen. `MessageFixtures.Save` adds one fixture and never rewrites or deletes one. The samples write theirs this way, behind an environment variable, and check them in.

**The workflow: record at each release and keep the fixtures.** A range is only backed by fixtures of *other* versions. At each release, record what it sends (live or with `SaveRelease`) and check the folder in; never edit or regenerate it. The next release's tests replay those frozen fixtures and run `CheckRange` with its own version, so a field removed, renumbered or retyped since a version inside the range fails. A mod's first release has nothing older to check against: `CheckRange(…, allowSelfOnly: true)` lets it pass with a "not proven" note.

**API** (namespace `Progenitor.Testing`; every check returns a `ReplayResult` with `Ok`, `Checked`, `Failures`, `Notes`, `Versions`, `OtherVersions` and `ThrowIfFailed()`):

| Call | Checks |
|---|---|
| `MessageFixtures.Load(dir, mod = null)` / `.All` / `.For<T>()` / `.For(name)` / `.ModVersions`; `MessageFixture.HasSchema` | Reads fixtures (recursively); a missing folder is empty |
| `MessageFixtures.SaveRelease(dir, mod, modVersion, messages, direction = null)` | Writes a release's fixtures with their schema; refuses an existing version folder |
| `MessageFixtures.Save<T>(dir, mod, modVersion, message, direction = "any")` | Adds one fixture of today's encoding with its schema |
| `MessageCompat.Replay<T>(fixtures, (msg, fixture) => …)` | Every fixture of `T`'s message name decodes (also with unknown fields added), keeps every field `T` knows with the same wire type and value (nested messages field by field), matches the fixture's recorded schema (a field number whose type changed fails, recursively, even with the same wire type), and runs through the handler without throwing. A field the fixture has that `T` lacks is a note here. A fixture whose minimum reader version is above `T.Version` is dropped, as in the game (a note). No fixture at all is a failure |
| `MessageCompat.Replay(name, fixtures, (MessageReader r, fixture) => …, readerVersion)` | The same for a hand-written message (no schema) |
| `MessageCompat.ReplayAll(fixtures, types)` | `Replay<T>`'s checks for every fixture with the type that reads its name; a name no type reads is a failure (the game drops it) |
| `MessageCompat.CheckRange(peers, currentVersion, fixtures, types, allowSelfOnly = false)` | Every fixture from a version inside `peers` works both ways with today's types: no drop, no lost value, no message this build cannot read, no field of that version (in its payload or its recorded schema) missing or renumbered (removing a field is a major change), no field retyped, and each of today's types readable by that version (`MinReaderVersion` no higher than the version it wrote). `OtherVersions` lists the versions other than `currentVersion` that passed: they are what backs the range. Only `currentVersion`'s own fixtures inside the range fails as "not proven" unless `allowSelfOnly`. Fixtures outside the range are notes; no recorded version inside it fails |
| `MessageCompat.AssertSkipsUnknownFields(message)` / `WithUnknownFields(payload, seed)` | A reader skips fields a newer version adds (four fields, one per wire type, at numbers from `MessageCompat.UnknownFieldBase`) |
| `MessageCompat.Encode(message)` / `Decode<T>(payload)` | The game's encoding, byte for byte; over 15 360 − 16 bytes throws as the game's `TooLarge` |
| `MessageTypes.In(assembly, ns)` | Every `[ModMessage]` class in the test assembly (optionally one namespace); a class whose attributes cannot be read without the game is refused with the advice to link its source |

```csharp
static readonly MessageFixtures Fixtures = MessageFixtures.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures"));
static readonly IReadOnlyList<Type> Types = MessageTypes.In(typeof(TradeOffer).Assembly, typeof(TradeOffer).Namespace);

[Fact] public void Every_recorded_offer_still_reads() =>
    MessageCompat.Replay<TradeOffer>(Fixtures, (offer, f) => TradePolicy.Accept(offer)).ThrowIfFailed();

[Fact] public void The_declared_range_is_backed_by_older_releases() =>
    MessageCompat.CheckRange(">=1.2.0 <2.0.0", "1.4.0", Fixtures, Types).ThrowIfFailed();
```

Replay runs the mod's **game-free** handler logic (a policy method the real `ctx.Messages.On` handler calls), not the plugin itself. It checks that the bytes parse and survive, not that two versions agree on what a field means ([what this cannot solve](mod-compatibility.md#what-this-cannot-solve)).

**On the Mods menu.** A mod that declares a range that admits other versions than its own shows **Works across versions**; its page has a collapsed developer section with the declared range, the network level and rule, the features it offers and the messages it reads and sends ([mods-menu.md](mods-menu.md)).

### Requests, large messages and subscriptions

`ctx.Requests` sends a typed request from a client and gets the server handler's typed reply, or a refusal with a reason. `ctx.Subscriptions` gives an open panel a snapshot of keyed server state (a container, a board) followed by deltas until the client unsubscribes or leaves. Both carry payloads larger than one packet: Core splits and reassembles them. Requests, replies, snapshots and deltas are ordinary `[ModMessage]` classes, so the [versioned-message rules](#feature-negotiation-and-versioned-messages-mod-compatibility-c1c2) and [replay tests](#testing-your-mod-across-versions-mod-compatibility-c5) apply unchanged. Worked example: the Sample's `.request` and `.subscribe` ([`SampleExchange.cs`](../samples/Progenitor.Sample/SampleExchange.cs)).

**Status:** landed. Source-checked, built and game-free tested (`ExchangeWireTests`, `ExchangeEngineTests`, `SampleExchangeTests`, and `SampleExchangeRoundTripTests` in `tests/Progenitor.Testing.Tests`). It is not verified live. The [acceptance procedure](#live-acceptance-procedure-not-run) below has not been run.

**Request and reply.** The server registers one handler per request type. The handler returns the reply. To refuse, it throws `RequestRefusedException(reason)`, and the client gets `Refused` with that reason. Any other exception is logged on the server, and the client gets `Failed` without the exception text.

```csharp
[ModMessage("stash.page.get")] public sealed class PageRequest { [Field(1)] public string Container = ""; [Field(2)] public int Page; }
[ModMessage("stash.page")]     public sealed class Page        { [Field(1)] public List<Row> Rows = new(); }

// Server, in OnLoad. Tracked: unload removes it (waiting requests are answered Unloaded).
ctx.Requests.Handle<PageRequest, Page>((user, req, info) =>
{
    if (!CanOpen(user, req.Container)) throw new RequestRefusedException("not your container");
    return BuildPage(req.Container, req.Page);
}, new RequestOptions { MaxRequestBytes = 256, Burst = 10, PerSecond = 5 });

// Server, an answer that is not ready this frame:
ctx.Requests.HandleDeferred<PageRequest, Page>((user, req, info, reply) =>
    Later(() => { if (reply.IsOpen) reply.Reply(BuildPage(req.Container, req.Page)); }));   // or reply.Refuse("...")

// Client, main thread. The callback runs exactly once, on the main thread.
RequestHandle h = ctx.Requests.Send<PageRequest, Page>(new PageRequest { Container = id }, r =>
{
    if (r.Ok) Show(r.Reply!); else ctx.Log.LogWarning("page: " + r);   // r.Status, r.Reason, r.NotSent
}, timeoutSeconds: 10);
h.Cancel();                                                          // optional: ends it Cancelled and tells the server
RequestResult<Page> r2 = await ctx.Requests.SendAsync<PageRequest, Page>(req);   // never faults; read r2.Status
```

`RequestStatus`: `Ok`, `Refused` (the handler's reason), `NotSent` (`NotSent` holds the `SendResult`: no server, an older Progenitor, or the server lacks the mod), `Timeout`, `Cancelled` (`Cancel`, or this mod unloaded), `Disconnected`, `NoHandler`, `RateLimited`, `Busy` (too many waiting requests, or a full send queue), `TooLarge`, `Failed`, `Unsupported` (a minimum reader version above the other side's), `Unloaded` (the server's mod unloaded first), `Malformed` and `NotNegotiated` (the server does not see this mod in the client's feature table, usually right after a join or a reload; the handler did not run). Each request has a 32-bit correlation id that starts at a random point per Core generation, so answers that arrive in any order reach their own callbacks and an answer meant for a previous generation is not taken for a new request. An answer that arrives after a timeout or a cancel is dropped. A client timeout also cancels the request on the server, so a deferred responder sees `IsOpen == false`.

**Subscriptions.** The server declares a topic with a snapshot function. A client subscribes to one key. It gets the snapshot, then every delta the server publishes for that key, in order. Each subscription has its own sequence numbers. If a delta is missing, the client stops applying deltas and asks for a fresh snapshot, which also covers everything it missed.

```csharp
[ModMessage("stash.state")] public sealed class StashState  { [Field(1)] public List<Row> Rows = new(); }
[ModMessage("stash.delta")] public sealed class StashChange { [Field(1)] public int Slot; [Field(2)] public Row? Row; }

// Server. The snapshot function runs on every subscribe and every resync, so access is re-checked each time.
var stash = ctx.Subscriptions.Topic<StashState, StashChange>("stash", (user, key) =>
    CanOpen(user, key) ? StateOf(key) : throw new RequestRefusedException("not your container"),
    new TopicOptions { MaxKeyBytes = 64, MaxPerUser = 4 });
stash.Opened += (user, key) => StartWatching(key);       // stash.Closed when a user unsubscribes, leaves or resets
int n = stash.Publish(containerKey, new StashChange { Slot = 3, Row = row });   // to every subscriber of that key
stash.Resync(containerKey);                              // a fresh snapshot to each of them (after a bulk change)

// Client: open the panel.
var sub = ctx.Subscriptions.Subscribe<StashState, StashChange>("stash", containerKey,
    (state, info) => panel.Replace(state),               // first, and again after every resync
    (change, info) => panel.Apply(change),               // in order
    (reason, text) => panel.Closed(reason, text));       // the server or the connection ended it
sub.Dispose();                                           // close the panel: unsubscribes; no end callback
```

`SubscriptionState`: `Pending` (waiting for the snapshot), `Live`, `Resyncing`, `Retrying` and `Ended`. `SubscriptionEndReason`: `Closed`, `Refused`, `NoTopic`, `Unloaded`, `Disconnected`, `Timeout`, `RateLimited`, `Busy`, `Unsupported`, `NotSent`, `Failed`, `BadKey`, `Reset` and `NotNegotiated`.

- **Lost snapshot.** If the snapshot does not arrive within 10 s, the client asks again. After three tries, the subscription ends with `Timeout`.
- **Server mod reload.** When the server's mod unloads, subscribers get `Unloaded`. When the server forgets a client's state, the subscriber gets `Reset`. In both cases the client resubscribes after 1, 2, 4, 8 and 16 s, and stays `Retrying` until then. If the topic does not come back, the subscription ends with the last reason.
- **Full send queue.** When a subscriber's send queue is full, a publish skips that subscriber. Once there is room, the subscriber gets a fresh snapshot. The server sends at most one snapshot per second to each subscription.

**Limits.** Core enforces every limit; a mod cannot raise them. Server-side checks treat every request, subscribe and resync as untrusted input: a resync spends the same per-topic budget as a subscribe, a snapshot is built only when the user's send queue has room for it (at most one attempt per second per subscription, and at most 8 builds per frame for the whole server), and a request is answered only for a client whose feature table, as the server sees it, lists the mod.

| Limit | Value | When exceeded |
|---|---|---|
| Request size | `RequestOptions.MaxRequestBytes`, default 16 KiB, at most 256 KiB (`ExchangeLimits.MaxRequestBytes`) | Refused at the first chunk, before anything is stored. The client gets `TooLarge` and the handler does not run. |
| Reply, snapshot or delta size | 1 MiB (`ExchangeLimits.MaxBodyBytes`) | Refused at the sender. A reply becomes `TooLarge` for the client. An oversize snapshot ends the subscription. An oversize delta is not sent, and one line is logged. |
| Request rate per user and handler | `Burst` (default 10), then `PerSecond` (default 5) | `RateLimited` |
| Deferred requests waiting | `MaxPending` per user and handler (default 4), and 16 per user in total | `Busy` |
| Deferred answer time | `TimeoutSeconds`, default 30 s, at most 300 s | `Timeout` |
| Client timeout | default 15 s, at most 300 s | `Timeout` |
| Key | `TopicOptions.MaxKeyBytes` UTF-8 bytes, default 64, at most 128 | `BadKey` |
| Subscriptions | `MaxPerUser` per topic (default 8), and 64 per user in total | `Busy` |
| Subscribe rate per user and topic | `Burst` (default 10), then `PerSecond` (default 2) | `RateLimited` |
| Client waiting requests and open subscriptions | 64 requests and 32 subscriptions per mod | `Busy` |
| Subscribe and resync rate per user and topic | The topic's `Burst` and `PerSecond`, shared by subscribes and resyncs | A subscribe ends `RateLimited`; a resync is dropped and the client asks again after its snapshot timeout |
| Reassembly memory | One body at a time per connection: 256 KiB plus a 280-byte header on the server, 1 MiB plus the same header on the client; a body whose next chunk has not come for 30 s is dropped | Over the cap: skipped. Out of order: the open body is dropped. |
| Send queue | 4 MiB per user and 64 MiB for all users on the server, 1 MiB on the client; a body is queued whole or not at all | `Busy`; for a subscription, a later snapshot |
| Pacing | Server to each client: 512 KiB/s after a 128 KiB burst. Client to server: 8 packets/s after a burst of 20, under the server's `[Networking] ReceivePacketsPerSecond` and `ReceiveBurstPackets` defaults (20 and 60) | Queued, not dropped |

A 1 MiB reply takes about two seconds. A 256 KiB request is 18 packets, which fits in the client's burst. If a server lowers its receive budget, its mods should keep requests small: the server's receive gate counts every Progenitor packet, and a dropped chunk drops its whole body. That request then times out.

**Lifetime.** Everything is tracked; no handler or callback needs explicit cleanup.

- **Client unload.** The mod's requests end `Cancelled` (callbacks still run, so an awaited task completes). Its subscriptions end `Closed`, and the server is told.
- **Server unload.** Handlers and topics are removed. Waiting requests are answered `Unloaded`. Subscribers get `Unloaded` and resubscribe, so a hot reload carries an open panel through. Disposing a topic yourself ends its subscriptions with `Closed`, and they do not retry.
- **Disconnect.** Everything on the client ends `Disconnected`. On the server, that user's subscriptions close and `Closed` runs for each of them.
- **Re-handshake.** When a user's handshake passes again with the post-join steps (after a client Core swap, for example), the server resets what it held for them. Live subscriptions get `Reset`, so the client resubscribes; waiting deferred requests are answered, and the client sees `Cancelled` with the reason `the server reset this connection`. A quick re-answer that skips those steps does not reset; random request ids keep a stale answer from matching a new request.
- **Core swap.** Best effort, before the outgoing Core goes. The server's Core sends each subscriber an `Unloaded` end frame and answers each waiting request `Unloaded`. A client's Core sends the server an `Unsubscribe` per open subscription and a `Cancel` per waiting request, as far as its packet burst (20) allows; the server cleans up the rest when the client's next handshake resets it.

**Threads.** Everything runs on the game's main thread: `Send`, `Subscribe`, `Publish`, `Resync`, the responder methods, and `Dispose` of a subscription. From another thread these throw `InvalidOperationException`; post the call with `MainThread.Post`. Handlers and callbacks run on the main thread, inside the receive dispatch or the frame tick.

**Registration errors.** `Handle`, `HandleDeferred` and `Topic` throw `InvalidOperationException` in these cases:

- the call runs on a client;
- an option is out of range;
- a type is not a valid message type;
- the request and reply share a name, or the topic, snapshot and delta names are not all different;
- the request or topic already has a handler;
- a name's hash collides with another name. Requests, replies, topics and plain messages of one mod share one name table.

This is the `ctx.Messages.On` contract: a mistake fails `OnLoad`. `Send` and `Subscribe` throw the same exception for a type that is not a valid message type, and for a topic name that is not valid.

**What Core does on the wire.** One Core event id, `'PrXc'` (`0x50725863`), carries both directions. It is registered through the owned-id path, so a peer without a handler drops the packet in Core.

- **Gating.** Exchange traffic goes only to a peer that declared `Handshake.Capability.ModExchange` (134217728) and whose feature table lists the mod. Every server request carries the bit. Otherwise nothing is sent, and the result is `NotSent` with `PeerTooOld`, `PeerNotReady` or `PeerLacksMod`. On the server, packets from a user whose handshake has not passed are dropped, after the per-connection `ReceiveGate`.
- **Chunk frame.** `[frame version u8][flags u8][transfer u32le][seq u16le][count u16le][total u32le][data]` (14-byte header). One packet carries at most 15 360 − 14 bytes. Each connection's send queue is first in, first out, and never interleaves two bodies.
- **Body.** `[op u8][code u8][mod hash u32le][message hash u32le][message version u16le][min reader version u16le][id u32le][seq u32le][text length u16le][text][payload]` (24-byte header). The hashes, version and minimum reader version are those of the `'PrMs'` envelope.
  - Ops: `Request`, `Reply`, `Error`, `Cancel`, `Subscribe`, `Snapshot`, `Delta`, `Unsubscribe`, `Resync` and `Ended`.
  - `id` is the correlation or subscription id; `seq` is the delta number.
  - `text` (at most 256 bytes) is a subscription key or a refusal reason.
  - A client may send only requests and the small control frames (at most 4 KiB). Anything else is dropped at the first chunk.
- **Ordering and loss.** Packets travel on the game's reliable events channel (`SendPacketToUser` / `SendPacketToServer` with `reliable: true`). This design assumes that channel delivers in order, as Progenitor's other chunked channels do. It detects a violation instead of trusting it:
  - A chunk out of order drops its whole body, and the request times out.
  - A gap in a subscription's sequence triggers a resync.
  - A packet the transport refuses stays at the head of the queue and is tried again next frame. After 300 frames of refusals, that connection's queue is dropped.
- **Warnings.** Drop and refusal lines are rate-limited per kind (one line per kind per minute, the next one counting what was held back), keyed on fixed kinds rather than anything a peer sends, so a flood of bad packets cannot silence other warnings.

**Testing** (`Progenitor.Testing`). The request, reply, snapshot and delta types record and replay like any message, with `MessageCompat.Replay<T>`. Record them with `ExchangeCompat.SaveRelease` (or `ExchangeCompat.Save`), which takes payloads up to 1 MiB; `MessageFixtures.SaveRelease` keeps a plain `ctx.Messages` type to one packet's payload. `ExchangeCompat` runs game-free round trips through Core's real exchange engines and chunk frames:

```csharp
ExchangeCompat.AssertRequestPair<PageRequest, Page>();
ExchangeCall<Page> call = ExchangeCompat.Call<PageRequest, Page>(new PageRequest { Container = "c1" }, (req, info) => StashPolicy.Page(req));
Assert.True(call.Ok); Assert.Equal(3, call.PacketsDown);    // the reply took three packets

var topic = ExchangeCompat.Topic<StashState, StashChange>("stash", (user, key) => StashPolicy.State(key));
TopicClient<StashState, StashChange> a = topic.Subscribe(1, "c1");
topic.Publish("c1", new StashChange { Slot = 3 });
Assert.Single(a.Snapshots); Assert.Single(a.Deltas);
```

`ExchangeCompat.PacketsFor(message)` counts the packets a payload needs. `ExchangeCompat.Encode` encodes a payload up to the 1 MiB limit.

#### Live acceptance procedure (not run)

Run this on the isolated test server and the test client, with this branch's Core and the Sample deployed to both sides. The Sample uses `Exact`, so both sides run the same build. Rejoin after the Core swap so the handshake negotiates `ModExchange`.

1. **Request larger than one packet.** In chat (or through the server bridge's `command` op as the joined user), run `.request 64`.
   - Expected chat reply: `Asked your client to request 64 KiB.`
   - Expected server log: `Sample exchange: answered a 64 KiB request from user <n> (request v1).`
   - Expected client log: `Sample exchange: request 64 KiB: Ok in <ms> ms, 65536 bytes in about 5 packet(s), checksum ok.`
   - Expected whisper: `Sample exchange (your client): request 64 KiB: Ok …`
   - Repeat with `.request 1000`. Expect `checksum ok` after about two seconds.
2. **Limits.** Run `.request` four times quickly. The handler's burst is 3 and its rate 0.5 per second, so expect one `RateLimited (too many exchange.blob.get requests; …)` line. `.request 0` is refused by the command's usage line. The handler's own refusal is game-free tested only.
3. **Subscription.** Run `.subscribe start lobby`.
   - Expected client log: `Sample exchange: board lobby: snapshot rev 0, 0 line(s), 20 KiB padding.` The snapshot is two packets.
   - Expected server log: `Sample exchange: board lobby: a player subscribed (1 now).`
   - Run `.subscribe post hello` twice. Expect `board lobby: rev 1 posted to 1 subscriber(s).`, and client lines `board lobby: rev 1 'hello'.` and `rev 2`.
   - `.subscribe status` lists the subscriber.
4. **Fan-out.** If a second client is available (a headless client of the rig), subscribe it to `lobby` too. One post reaches `2 subscriber(s)`, and both clients log the delta.
5. **Reload.** Hot-reload the server's Sample while subscribed.
   - Expected: the client's subscription goes `Retrying`, with no end line.
   - Within about 2 s the client logs a new `snapshot rev 0` (the board is in memory and starts empty after a reload).
   - A post then reaches the client again.
6. **Unsubscribe and disconnect.** Run `.subscribe stop lobby`. Expect `unsubscribed after <n> delta(s).` on the client and `a player unsubscribed (0 now).` on the server. Subscribe again, then disconnect. Expect the server's `unsubscribed` line for that user.
7. **Clean up.** Remove the Sample from both reload folders, confirm clean unloads in both logs, and return the client to the title screen.

Not covered by this procedure: a transport that reorders or refuses packets, the 300-frame queue drop, a server Core swap with subscribers connected, and a reply over 1 MiB. These are game-free tested only, or not tested at all.

## Content

**Data files (FJSON1, phase 1):** a plugin folder `content/*.json` beside `localization/` defines cloned items through the same `DefineItem` builder, read after `OnLoad` and owned by the plugin; the format, refusals and lifetime are in [`content-files.md`](content-files.md).

Join-time item-index agreement (`PrfI`) and fail-closed missing-ledger behavior: [key-table.md](key-table.md). Workstation recipes: [recipes.md](recipes.md). Research notes (internal): the `ItemKeyLookup` layout, apply gates, tombstones / `PrfK`; clone ownership across a Core swap (evidence 97); cloned buffs spawn from `NetSnapshot` PrefabGUID; hot reload of a content plugin with a connected player. Spike: internal research notes.

`Progenitor.Content.PrefabRegistry`. Clones a vanilla **item** or **buff** prefab under a deterministic GUID and keeps every lookup in sync, including a client that never shipped that GUID. Through `PluginContext`, `ctx.Content.DefineItem` / `ctx.Content.DefineBuff` are the same fluent surfaces as `PrefabRegistry.DefineItem` / `DefineBuff` except `Register()` takes no owner (this plugin is the owner) and the handle is reversed on unload.

```csharp
ctx.Content.DefineItem("sample.ember")   // ASCII, 1–63 bytes; also the spawnable name
    .CloneOf(new PrefabGUID(-257494203))    // or CloneOf("Item_Ingredient_Crystal")
    .MaxAmount(50)                          // omit to keep the source stack size
    .Named("Ember Shard")                   // 1–63 ASCII; client inventory name
    .Described("A shard that still holds a little heat.")  // 1–63 ASCII tooltip; omit to keep source, or to use <id>.desc from en.json
    .Register();                            // this plugin is the owner; dispose / unload undoes

ctx.Content.DefineBuff("mymod.ward")    // same id / GUID / PrfD path; Kind=Buff on the wire
    .CloneOf("Buff_General_Haste")       // visible source: ShowOnBuffBar + client ShowOnBuffBarData
    .Named("Ward")                       // 1–63 ASCII; client registers id.name and writes Entry.Header
    .Described("Hostiles ignore you.")   // id.desc; both keys clear UseParentNameAndDescription
    .Icon("icons/ward.png")              // PNG under this plugin's assets/ folder; client only
    .RefreshLiveInstances()              // opt-in: destroy+reapply live instances after a later definition change
    .Register();                         // source must have Buff; no MaxAmount; no ItemKeyLookup row
```

Owned item clones can opt out of the donor's loss rules: `.BloodBound()` keeps the item on death (`ItemCategory.BloodBound` on the clone and its item lookup row) and `.Indestructible()` stops every durability loss, including the death drain (`Durability.LossType` None, take-damage factor 0, full durability, `LoseDurabilityOnDeath` cleared). Both apply at the definition level, so the result does not depend on how the item was obtained; the server also rearms existing instances at registration and at the first player connect of each world load, and re-checks the clone's bits on every connect. Register them on both sides like `ArmorBaseline`. Status: source-checked, built, game-free tested; not live-verified. See [api.md](api.md#content).

Worn armor and weapons use the same builder: `.Wearable(WearableLooks.RobeChest())` (or `WearableGraphics.Define`). The sketch is not sent on `PrfD`. Both sides register it. The client builds a skinned mesh on the wearer and, when the clone has them, writes the male and female model prefab fields. Details, bone names and the robe set: [`wearables.md`](wearables.md).

The non-context path is still `PrefabRegistry.DefineItem(id).…Register(owner)` / `PrefabRegistry.DefineBuff(id).…Register(owner)` (or `UnregisterAll(owner)` from `Unload` if the handle was not kept). `id` is hashed with `PrefabGUID.CalculateGuidHash` — it must be identical on every peer. GUID is not negotiated. `CloneOf` is required before `Register`. `DefineItem` source must have `ItemData` (refuse: `source has no ItemData (DefineItem requires an item prefab)`). `DefineBuff` source must have `Buff` (refuse: `source has no Buff (DefineBuff requires a buff prefab)`). Wrong-kind refuses are fatal, not retried. Buff clones skip `ItemKeyLookup` / `ItemHashLookupMap` / `ManagedItemData` so they cannot shift join-time count+hash; they still send `PrfD` and `PrfU`, but never `PrfK` (no key row to tombstone). `Register` twice on the same builder throws. Same GUID from another owner replaces the previous registration (warning). `Named` / `Described` are optional; both are 1–63 ASCII (the same bound as `id`) so the definition packet can encode them. Items register the name under `id` and the description under `id.desc`. Buffs use the same display-text semantics but register the name under `id.name` (and `id.desc`) and write those keys onto every `ShowOnBuffBarData.Entries[i].Header` / `Description.Key`. **Both** `Named` and `Described` (or `id.name` + `id.desc` from JSON) are required to take over the HUD: that pair clears `UseParentNameAndDescription` so the source tooltip ("Haste / Increases movement speed") does not win. `Named` alone writes Header and keeps the cloned parent description / flag (log: `Named without Described on 'id' (parent description kept; call Described or add id.desc)`). Omit `Described` to keep the source prefab's tooltip, unless Localization already has `<id>.desc` from a plugin JSON file (see Localization); omit `Named` on a buff the same way (`id.name` from JSON still applies). Buff localization keys are registered with the definition's owner on `Register`, not as apply undo: soft unapply of the clone does not drop `id.name` / `id.desc` while the entity still points at them; plugin unload (`Localization.RemoveAll`) still reverses them, and the next `Register` puts them back before apply. **Same-id reload.** A later `Register` of the same id (hot-reload of the plugin, or another owner replacing it) keeps the GUID. `Named` / `Described` / `MaxAmount` / `Icon` still update the surviving clone in place when `Kind` and resolved `CloneOf` are unchanged. A changed `CloneOf` does **not** patch the old archetype: when no live instance has that `PrefabGUID` (cached `PrefabGUID` + `Buff` or `ItemData` query, exclude `Prefab`, include disabled; a thrown query **fails closed** and is treated as instances exist so retarget is refused; inventory stacks are not entities and do not count), apply Instantiates the new clone first, then unmaps and destroys the old one — GUID kept, editors re-run, `NetSnapshot` GUID rewritten, icon/name re-applied. The apply log fragment is `retargeted (was X, now Y)`. After a Core swap the previous `CloneOf` hash is restored from Bootstrap ContentMemory (`ReplaceCloneSources` / the reclaim snapshot) so two same-schema sources are still distinguished; the NetSnapshot fingerprint is only the unknown-hash fallback. When any live instance exists, that definition is refused and the old clone stays:

```
PrefabRegistry: 'mymod.ward' source changed (was 111, now 222) but 3 live instance(s) exist; restart to retarget
```

A successful retarget of a networked buff on the server always sends `PrfD` again so connected clients rebuild their clone before any instance replicates:

```
PrefabRegistry: sent PrfD after retarget of 'mymod.ward' so connected clients rebuild the clone
```

Same-id `DefineItem` → `DefineBuff` or the reverse is always refused (a kind change is a different prefab; pick a new id):

```
PrefabRegistry: 'foo' kind changed (was Item, now Buff); use a new id
```

Both refuses are fatal for that definition, not the batch, and are not retried. An appended `ItemKeyLookup` row whose live entry is no longer an item is tombstoned (`PrfK`) so join-time count+hash still matches. A failed apply that had just appended a key row rolls that row back (`RollbackKey`) so the server table never exceeds `PrfD`+`PrfK`.

The HUD buff bar shows a clone only when the **source** already carries `ShowOnBuffBar` (and, on the client, `ShowOnBuffBarData` with a `Sprite` icon) — clone a visible vanilla buff (haste, a potion), not `Buff_General_Immaterial`. `Icon("icons/foo.png")` is optional and **client-only**: a path relative to the plugin's `assets` folder (deploy copies it to `<reload>/<AssemblyName>/assets`, the same probe Localization uses for JSON). Absolute paths and `..` are refused. The path is **not** a `PrfD` field — the same plugin DLL registers the same `DefineBuff` on the client, which loads the PNG (`Texture2D` + `ImageConversion.LoadImage` + `Sprite.Create`) and writes it onto every `ShowOnBuffBarData.Entries[i].Icon`. The server skips that step. **Icon lifetime across Core swaps.** The Sprite and its Texture2D are one Bootstrap `IconCache` entry (key = plugin id + relative path + SHA-256 of the PNG bytes, `hideFlags = HideAndDontSave`). Core Uninstall only drops leases — it never `Destroy`s a cached pair — so a collectible Core swap cannot leave the buff bar holding a dead texture (black square, then white after `.nodrain off`/`on`; evidence 112 / 122). File-removal (`IconCache.ReleasePlugin`) does **not** destroy a pair while a live clone still holds it (refcount from apply): the row is marked pending and the Unity objects stay until Content reports the clone gone, or until process exit. Non-GUID owners are keyed by assembly simple name, the same id ReloaderHost releases. **Both halves of the key are case-normalized** — the path and the plugin id, on write and on lookup — so `icons/A.png` / `icons/a.png` and `MyPlugin` / `myplugin` each share one row. Leaving the id ordinal while the plugin+path index was case-insensitive turned a casing difference into a `Replace` that retired (or, at refcount 0, destroyed) the pair a live clone was drawing, and made a file removal reported with different casing release nothing (R27 finding 14b). At swap re-apply the plugin asset root is often missing (the file is not in the reloader `ByPath` until `Load` returns); if the cache still has the sprite, the clone is written from it (`PrefabRegistry: icon 'icons/ward.png' from cache on 'mymod.ward'`). A cache entry whose texture fails the Unity null check is rebuilt from stored bytes (`icon cache entry '<key>' had a dead texture; recreated`). A cache row is **never** destroyed under a live HUD. A replaced pair (the plugin PNG changed, so plugin+path now hashes to a new key) is destroyed only when nothing references it; otherwise it is retired — kept alive under the HUD that is drawing it — and logged `icon cache entry '<key>' replaced but kept alive; N live reference(s)`. **Retire is a replacement rule, never a generation rule.** A pair is parked in the retired table only when the key's **live** row was replaced — a new PNG for the same plugin+path, so plugin+path hashes to a different canonical key. A Core swap is not that: for an unchanged PNG the cache hands the same Sprite and Texture2D objects to the new generation (`Hit` / `Recreate`, no replaced key), so a swap retires nothing at all. `IconCachePolicy.ShouldRetireOnReplace(liveRowReplaced, refs)` is that rule, and `IconCache.RetirePair` is the single seam that may call it.

**The retired list is bounded, and the bound never destroys a pair that is still on screen.** Two things make it finite, and the cap is only the backstop for the pathological rest. First, a retired pair keeps the refcount it had when it was dropped, and `Release` on that key destroys it as soon as the last reference goes (`icon cache entry '<key>' retired pair released; destroyed`). Second, **a replacement hands the superseded holder on**: whatever was drawing the pair retired by the *previous* replacement of that plugin+path is exactly what this replacement is re-pointing at the new sprite, so that row drops one reference and is destroyed the moment its count reaches zero (`icon cache handed a superseded holder on for '<plugin|path>'; the previously retired pair had no references left and was destroyed.`). Re-saving one icon through 200 generations therefore holds **one** retired row, not 200. Without the hand-off the cap was a report rather than a bound: rows over it are referenced by construction, so nothing could be freed and `_rows` grew for the life of the process, every row "referenced" by a Core generation that had already unloaded (R29 finding 10). The list caps at `IconCachePolicy.MaxRetiredPairs` (64), and the cap may only take rows **nothing references**, oldest first (`icon cache retired list over cap; destroyed N oldest unreferenced pair(s) (cap 64)`). A row is retired precisely because a live clone was drawing that pair, so popping the oldest regardless of its refcount destroyed the sprite under the HUD — the black square retire-not-destroy exists to prevent, reintroduced at a bound (R28 finding 4a). When the list is over the cap and every row is still referenced, it is allowed to grow and says so **once per episode**: `icon cache retired list over cap and every pair is still referenced; holding N (cap 64). Nothing was destroyed: a referenced pair is still on screen.` A leak that logs beats a sprite destroyed under a clone; `pin-diag IconCache count=N retired=M` is where to watch it. One key has exactly **one** retired row: `Retire` merges (reference counts add, both Unity pairs are kept, and the pair is freed only when the last holder of that key lets go). Two rows for one key — the ordinary "save icon v2, revert to v1, save v2 again" loop — sent every release to the *older* row, which reached zero first and was destroyed while its real holders were still drawing it, while the newer row's count never dropped at all (R28 finding 4b). For the same reason a release only reaches the retired table when no **live** row owns that key. Iterating on an icon changes its SHA-256 on every save, so every save is a `Replace`; with no exit but process teardown that leaked one `HideAndDontSave` Sprite+Texture2D pair per save, and Unity's unused-asset sweep will not reclaim those (R27 finding 14a). For one key the Sprite and Texture2D are the **same objects in every Core generation**; only a texture that fails the Unity null check is recreated. The first icon apply of each generation logs a self-check, `PrefabRegistry: icon cache check 'mymod.ward' sprite=-1234 texture=-1235 alive=True` (Warning, with an explanatory suffix, when the pair is dead) — those two instance ids must not move between generations, or every live instance is drawing a sprite the cache has replaced. The client nudge writes that cached Sprite in place onto `Entries[i].Icon`: it never decodes a new one, never writes a dead pair over a good one, and never replaces or clears the `Entries` list. Pin-diag: `pin-diag IconCache count=N retired=M`. Success logs: `PrefabRegistry: icon 'icons/ward.png' -> ShowOnBuffBarData entries=N on 'mymod.ward'` and `PrefabRegistry: name/description -> ShowOnBuffBarData entries=N on 'mymod.ward'`. Icon failures log `file missing`, `decode failed`, `asset root missing`, or `no ShowOnBuffBarData on the clone`. Name/description failures log `ShowOnBuffBarData present but unreadable…`, `ShowOnBuffBarData has no entries`, or `localization key unregistered`. HideStacks / lifetime flags stay as the source cloned them. Source visual sequences (`BuffSequence`, consumed by `BuffSequenceSystem_Spawn`) are **not** stripped by `DefineBuff` — the Core's debug buff prefab pass (`DebugBuff.PatchPrefab` / `StripSourceVisuals`) removes `BuffSequence` / `BuffSequenceSpawned` / `SpawnSequenceForEntity` / `SequenceGUID` on the **clone prefab** (Prefab-tag gated) so the `.debug` buff has no haste glow. `PatchInstance` never touches those buffers. Never strip `BuffSequenceSpawned` from a live instance: the game's destroy path is what stops the sequence (evidence 109). Editors may be re-run on live instances (registry-driven refresh); prefab-only strips must check the Prefab tag.

**Live instances.** Prefab-level edits (`DefineBuff` Named/Described/Icon, `Content.Edit`) do not reach buffs that already exist on characters or that a save restores. `DefineBuff(...).RefreshLiveInstances()` opts that definition into a registry-driven refresh: after the clone is re-applied **and the definition hash moved** (kind, resolved `CloneOf`, name/description/icon keys, and the ordered edit identity), the server queries `Buff` + `PrefabGUID`, destroys each instance through `DestroyUtility.Destroy(..., TryRemoveBuff)`, and re-applies through `DebugEventsSystem.ApplyBuff` — the same path Core's `.debug on` uses — on the main thread, **one instance per frame**. A Core swap or plugin reload whose definition hash matches the last apply does **not** refresh:

```
PrefabRegistry: refresh skipped: definition unchanged ('mymod.ward')
```

**The hash is a property of the plugin build, not of the generation it loaded into.** The edit part is the ordered list of `plugin id + priority + optional Version` — never the registration index or the invalidation counter, both of which start over when the same plugin build re-registers. And an apply is a **single decision**: a Core swap re-applies the clone before the plugin's `Content.Edit` passes are back and runs the editors a moment later, so both phases only note; the registry settles once (two frames after the last phase, so a coalesced `InputsChanged` flush is included), computes the hash with this generation's editors in place, records it, and logs **one** line. Recording mid-apply is what made an unchanged Core swap log both the skip above *and* `refreshed 1 live instance(s) … (definition changed)`, destroying the buff and re-firing its on-apply effects on every swap (R26 finding 4, fixed in FU). An unchanged plugin therefore logs exactly one `refresh skipped: definition unchanged` per swap and never `definition changed`.

**The previous hash crosses the swap.** The map of last-settled hashes is a Core static, and Core `Uninstall` clears it with the rest of the generation, so on its own the new generation would compare every definition against `null` — which is not a change — and the opt-in refresh could never fire on the one transition it exists for (R27 finding 1). It rides across in Bootstrap `ContentMemory`, the same process-lifetime store that already carries the cloned-GUID set and the remembered `CloneOf` hashes: `PrefabRegistry.ExportProcessMemory` writes it (from `Runtime.SnapshotContent`, which runs before `Plugin.Uninstall`) and `ImportProcessMemory` merges it back before plugins re-`Register`. Ints and strings only, so it cannot pin the collectible ALC. Import never overwrites a hash this generation already settled. Logs: `PrefabRegistry: snapshot N live-refresh fingerprint(s).` / `PrefabRegistry: rehydrated N live-refresh fingerprint(s).` Because the store lives in Bootstrap, a Core built with this handoff loaded into an older Bootstrap simply skips it (the reflective lookup misses) — **restart the process to get the handoff.**

**Neither half of the handoff fails quietly, and neither can destroy the other's rows.** The two directions used to be asymmetric — the export was unconditional and the import swallowed everything — so one swallowed rehydrate destroyed the handoff for the rest of the process: generation N+1 came up with an empty map (a throw in `ImportClonesAndKeys` lands *before* the fingerprint read), and *its* export then wrote two empty arrays over the store, after which every swap forever compared against no previous fingerprint at all. That is R27 finding 1's behavior restored permanently and in silence (R28 finding 16). Five rules now hold, and the first three apply equally to the `CloneOf` hash store beside it:

- **An empty export is never written as an empty store.** The import side merges, so an empty array on its own carries no information. `ContentMemory.ReplaceFingerprints` / `ReplaceCloneSources` return a `ContentMemoryStore` (`Replaced`, `KeptNothingOffered`, `KeptLengthMismatch`, `Cleared`) rather than `void`; `KeptNothingOffered` / `KeptLengthMismatch` keep what is stored and are logged as a Warning with both array lengths.
- **But emptiness that is real must still be sayable, and the exporter is what says it.** `PrefabLiveRefresh.ExportFingerprints` reports a `FingerprintExport` beside the rows: `Rows`, `AuthoritativelyEmpty` (empty *and* this generation's first apply pass settled, so nothing is registered here), or `Unsettled` (empty and the pass never settled — indistinguishable from a failed export). Only `AuthoritativelyEmpty` reaches `ContentMemory.ClearFingerprints()`, the one method that can empty the store; `Unsettled` keeps it and logs `live-refresh fingerprint export is empty but this generation's first apply pass never settled, so the emptiness proves nothing`. An export that *throws* never gets that far at all — the write is skipped and the store keeps what it holds. Without the distinction a generation that legitimately removed its definitions could never clear rows an older build wrote, and those rows were merged into later generations that had no business seeing them (R29 finding 11). `ClearFingerprints` is deliberately a separate method rather than an overload or a flag: Core resolves these by simple name through reflection, so an overload would be an `AmbiguousMatchException`.
- **A rehydrated fingerprint nobody re-registers is dropped at the end of the first apply pass.** An imported row is *provisional* until this generation applies the definition it belongs to (`PrefabLiveRefresh.NoteRegistered`, called from `NoteLiveRefresh`). The settle — the end of the first apply pass, since every apply re-arms it — sweeps whatever is still unclaimed and logs the count: `dropped N rehydrated live-refresh fingerprint(s) whose definition was not re-registered in this generation's first apply pass.` The export sweeps again on the way out for a generation that never settled. Dropping is the safe direction: a definition that comes back later simply has no previous fingerprint, and a missing previous fingerprint is not a refresh trigger, so the cost is one skipped refresh rather than a live buff destroyed against a fingerprint from a build nobody is running.
- **One bad row costs one row.** A fingerprint that is null or empty is not a settled value and would read on the far side as a real previous value that differs from everything — a spurious `definition changed` that destroys and re-applies an opted-in buff. It is skipped and counted (`snapshot skipped N empty live-refresh fingerprint row(s); M still cross the swap`); the same per-row skip covers the tombstone rows, which run inside `Runtime.SnapshotContent` and used to abort the whole cascade.
- **A rehydrate that takes nothing says why.** `ImportFingerprints` reports how many rows it actually merged and a `FingerprintImport` reason (`Taken`, `AllAlreadyPresent`, `NothingOffered`, `LengthMismatch`). The first two log at Info; the last two log at Warning and name the consequence: `live-refresh fingerprint rehydrate took nothing: … The previous generation's fingerprints are lost; the next definition comparison sees no previous value.` Grep `live-refresh fingerprint rehydrate`.

**The settle window is extended by every note, not armed once.** A plugin that registers `Content.Edit` late — from a config callback, a `WorldReady` handler, frame 3 or later — would otherwise miss a window armed by the first apply phase; the settle would then record an editor-less hash and the late edit pass would read as `definition changed` and destroy the opted-in buff for nothing (R27 finding 21). Every note re-arms the full two frames, so the decision is taken after the last phase; a drip of applies only delays the decision, it never loses one. The countdown and its frame subscription live behind one lock (`RefreshSettleCountdown`), so `Plugin.Frame` can neither be double-subscribed (two settles, two decisions, two log lines for one apply) nor lose its subscription and leave `refreshSettle=N` stuck in `pin-diag` forever. A **disarmed** countdown ticks to `false`: `Plugin.Frame` snapshots its invocation list per frame, so a handler `Stop()` removed earlier in the same frame still runs that frame, and the old `_remaining > 0 && --_remaining > 0` short-circuited at zero and returned *true* — settling on a window nobody had armed (R28 finding 28.1). This window is also unrelated to how long the refresh queue takes to drain (one item per frame, so 40 holders is seconds): the fingerprint describes the definition, not its instances, so a drain still in flight cannot change what the settle records.

A changed definition (a different source, a different icon/name/description, an added, removed or reordered edit), a retarget, or an `Invalidate` whose editors then rewrote the clone logs:

```
PrefabRegistry: refreshed N live instance(s) of 'mymod.ward' (definition changed)
```

Reasons: `definition changed`, `retarget`, `edit`. `reload` is no longer a refresh trigger. Captured editor values that move without a re-registration are invisible to a build-stable hash, so `Content.Edit` callers still use `Invalidate` (or a new `Version` token) for a config reload to reach live instances. An instance that expired, was cleansed, or whose entity was recycled between enqueue and dequeue is **not** re-applied. `Buff.Stacks` and remaining `LifeTime.Duration` are read before destroy and written after apply. Not preserved: on-apply effects re-fire; `Buff.StartTimeUnmodified` (age); runtime buffers including `BuffSequenceSpawned` (recreated by `ApplyBuff`). Destroy is one frame, re-apply the next so `BuffSystem_Spawn` does not see a `DestroyTag` duplicate. That is a two-frame state machine (`PrefabLiveRefresh.Advance`, `RefreshStep` Skip/Destroy/Wait/Apply) and its `RefreshState` **must** be carried back onto the re-queued item by reference: an item that forgets it destroyed the instance comes back looking never-destroyed with its entity already gone, is dropped as "lost", and the buff is destroyed and never re-applied (FT / evidence 131 — `.nodrain off` answered "not present" after a Core swap). A re-queue is never de-duplicated away for the same reason.

**Stacks ride in the apply event; duration does not.** `DebugEventsSystem.ApplyBuff` only queues an `ApplyBuffDebugEvent` — the game builds the buff entity on a later update — so the restore is its own third phase: after the apply the item stays queued, looks for the new instance for up to `RestoreDurationFrames` (8) frames, writes `LifeTime.Duration` onto it, and if it never appears logs `PrefabRegistry: live refresh of '<id>' duration not restored (<n>s): …` instead of dropping it in silence (R27 finding 12).

**An item that destroyed its instance is never dropped.** Between the destroy frame and the apply frame the item *owes* the holder an `ApplyBuff`. A world teardown landing in that window, a throw inside the refresh, or a second Core swap arriving one frame in used to discard it, and the holder lost the buff permanently with nothing in the log (R27 finding 13). All three re-queue instead, with a retry budget (`PrefabLiveRefresh.MaxOwedApplyRetries`, 30 frames) and a loud line per attempt — `PrefabRegistry: live refresh of '<id>' re-apply owed after destroy (<why>); retry n/30` — falling back to `… destroyed but never re-applied (<why>); the holder lost this buff` when the budget runs out. `Uninstall` drains the owed items with a synchronous `ApplyBuff` before clearing the queue (`PrefabRegistry: live refresh drained N owed re-apply(s) before uninstall.`); that drain skips the duration restore, because a re-applied buff with the clone's authored duration beats a holder who silently lost it.

Default is **off**: a plugin that stacks its own state on a live buff must not have Core destroy it. Never on first process start (save-restored instances of an unchanged definition are left alone). Core's debug buff (`progenitor.debug`) opts in. The client never destroys; the replicated instance follows the server. Client-only HUD fields (icon Sprite on the instance's own `ShowOnBuffBarData` copy — evidence 112; name/description keys) are nudged through the same apply helpers / a HUD copy from the clone, never by removing buffers, after **both** the swap-time apply and the late plugin-reload icon write. The Named-only Info line (`Named without Described on 'id'`) is logged once per definition from the clone apply, never per instance on a nudge.

```
PrefabRegistry: nudged N live instance(s) of 'mymod.ward' (icon)
```

**A buff that outlives its own commands must be swept on unload.** `PrefabRegistry` unapply is deliberately soft — it drops bookkeeping, never destroys the clone and never touches live instances — because a Core swap has to leave them alone. That means a *plugin* unload (hot swap, or the DLL removed from the reload folder) takes the plugin's commands away while its instances stay on characters. For an infinite-duration buff that persists through death, whose only removal path was that plugin's own command, the holder is stuck with it for good; with FW/FX's components on it that is permanent invulnerability with nothing in the game able to lift it (R27 finding 2). So a plugin whose buff can outlive its commands sweeps it in `OnUnload`: query live `Buff` + `PrefabGUID` instances of its GUID on the server (`includeDisabled: true`, skip the `Prefab`-tagged clone), destroy each through the same `DestroyUtility.Destroy(em, e, DestroyDebugReason.TryRemoveBuff)` the removal command uses, and log the count. Sample: `SampleBuffs.Reset()`, called from `Plugin.OnUnload`, logs

```
Debug: reset removed N live instance(s) via TryRemoveBuff
```

(zero included, so an operator can see the sweep ran). This is a *plugin* responsibility, not Core's: only the plugin knows whether its buff is safe to leave behind.

**But only on `Removal`.** That sweep ran on every unload, and a Core swap runs an unload every time, so the Sample's debug buff (now Core's) destroyed instances the world save had just restored — the FBUFF diagnosis, `LogOutput.log:154` of build 3820036, eleven lines after the load. Take the [unload reason](#unload-reason-fbuff2): sweep on `PluginUnloadReason.Removal`, keep on `Swap` and `Shutdown`. `DebugBuff.Reset(reason)` (Core's debug buff, which as a Core module only ever unloads as `Swap`) logs the keep branch too:

```
Debug: unload reason=Swap, keeping N live instance(s)
```

**Replication.** Item clones travel as inventory/drop data through `ItemKeyLookup` / `ItemHashLookupMap` (the slot is an index, not a spawned prefab). Networked entity clones (buffs, units) spawn on the client from the `NetSnapshot` blob: `PrefabGUID` at byte 0, `NetworkSnapshotType` (ushort schema id, not a GUID) at byte 4. `Instantiate` copies the source blob, so apply rewrites byte 0 to the clone GUID. `NetworkSnapshotType` stays the source schema — do not `SetSharedComponentData` it (not GUID-based; the generic is not a closed IL2CPP instantiation). A non-networked source (`Buff_General_Immaterial`) has no `NetSnapshot` and does not replicate. The apply log fragment is `net=<cloneGuid> type=<ushort> (was <sourceGuid>)` when rewritten, `net=<cloneGuid> type=<ushort>` when already the clone, `net=none` when there is no buffer:

```
PrefabRegistry: applied 'sample.nodrain' (650563057) in Server: itemKey=skipped (buff); source entity …; clone …; kind=buff; net=650563057 type=N (was 900030866); maps: …
PrefabRegistry: applied 'sample.nodrain' (650563057) in Client_0: itemKey=skipped (buff); source entity …; clone …; kind=buff; net=650563057 type=N (was 900030866); maps: …
```

These two lines were observed with the Sample's former `sample.nodrain` clone (GUID `650563057`). A correct apply then shows the client instance as PrefabGUID `650563057` with `ModifyBloodDrainBuff` and **no** `ModifyMovementSpeedBuff`. Before the rewrite the client instantiated `Buff_General_Haste` (`900030866`).

**Ordered edits.** `ctx.Content.Edit(guid, editor, priority = Default, before: null, after: null)` (or `PrefabRegistry.Edit(..., owner, pluginId, pluginName)`) runs after the registry has cloned/registered that GUID — or, for a vanilla prefab, once `PrefabCollectionSystem` has the entity — and **before** a definition packet is sent. Both sides apply the same pass locally, and since job **FEDW1** the server's result is what the client ends up with: after a pass that wrote anything, the server serializes the touched components and ships them as a `PrfE` packet (see *Edit results on the wire* below), and the client re-applies that layer after every local pass of its own. `PrfD` still carries only clone-time `MaxAmount`, name and description; a vanilla GUID still never sends `PrfD`, but it does send `PrfE`. A client without Progenitor at all keeps unedited stats. A plugin that calls `Content.Edit` **must** still declare `[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)]` (or `Minor`) so the handshake kicks a peer that does not have it. The attribute is the fallback at registration (editors run from `OnLoad` before `DeclareSelf`). After that the **live handshake table** is the gate: `Require` / `Unrequire` (and the client Provide table) are re-checked, and editors whose level no longer requires the peer are dropped with one Warn. `PrefabRegistry.Edit` `owner` must be the registering plugin (`ctx.Owner`); a decoy instance whose type has the attribute is refused (log once). `NoNeedForSync`, `ClientOnly`, `ServerMustHave` + `None`, or a missing attribute **refuses** the editor at registration (the row is not kept; the handle is already invalid):

```
PrefabRegistry: refused edit from opt.mod on guid 42: Content.Edit requires [NetworkCompatibility(ServerMustHave, Exact or Minor)] so both sides apply the same pass; this plugin declared NoNeedForSync/Exact. Set [NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact)] (or Minor).
```

```
PrefabRegistry: dropped edits from opt.mod: handshake is NoNeedForSync/Exact; Content.Edit requires ServerMustHave + Exact or Minor
PrefabRegistry: refused edit from opt.mod on guid 42: owner is not the registering plugin (pass ctx.Owner, not a decoy with a matching attribute)
```

Edits of a **vanilla** prefab GUID (not a clone this process registered) additionally log once per plugin + guid per Core generation:

```
PrefabRegistry: edit of vanilla prefab guid 42 by mod.a changes no definition; the edited component values reach Progenitor clients as a server layer (PrfE). A client without Progenitor keeps unedited stats.
```

**Edit-layer contract.** Order is declared priority (then before/after, load order, registration index), not registration order. Last local writer of a component wins; a stored server `PrfE` layer is applied after every local pass and is the effective value both processes derive from the same layer set (`PrefabEditEffective`). A received `PrfE` on the server is refused (`client-originated`) with no store or entity write; a type off Core's wire-safe list (`PrefabEditWireTypes`) is refused per record the same way. A layer for a prefab the client does not have is not written onto some other entity; the bounded retry gives up with `PrfE never applied`. Dispose, plugin unload, or `UnregisterOwner` restores the captured baseline. A restore callback that throws is logged and skipped; the remaining baselines still run; if any were owed and not restored the log is `restore incomplete` and the prefab is not vanilla.

**Two-process example.** `samples/Progenitor.Sample/SampleEditLayers.cs` (registered from `Plugin.OnLoad`) edits vanilla `Item_Ingredient_Crystal` (`-257494203`) `ItemData.MaxAmount`: Default writes 10, Late writes 40. Effective value on both sides is 40; the server ships that as `PrfE`. Unload restores the captured vanilla stack.

**Acceptance procedure — not run.** Isolated pair, both bridges. The executable case is `tools/live-rig/cases/m2-4-crystal-maxamount.json`:

```
node tools/live-rig/rig.mjs run m2-4-crystal-maxamount --dll samples/Progenitor.Sample/bin/Debug/net6.0/Progenitor.Sample.dll
```

It reads Crystal `MaxAmount` after a Sample removal (baseline *B*), after reinstall (40 on both sides with `sent PrfE` / `applied PrfE` lines), after a Sample swap (both still match), and after unload (*B* on both sides). A failed restore fails the step and the run is never `complete`. Tracked entities, request/response traces and cleanup are the rig's.

### Verified and open

**Offline-tested only. Not in a candidate.** Live gates, isolated pair, not run: `tools/live-rig/cases/m2-4-crystal-maxamount.json` (this section), `tools/live-rig/cases/m2-12-key-table.json` and `tools/live-rig/cases/m2-12-incompatible-client.json` ([key-table](key-table.md)). Independently reviewed (internal review R87); finding 1 (Core swap after a refused key-table ledger) remains an open qualification gate. **Structural `EntityManager` edits are not restored** — see the limit below.

The `ServerMustHave` requirement stays for now: it is what guarantees both sides have the module at a compatible version, and phase 2 of the design relaxes it to a warning once the two-process check has passed on a real client. Broad **server-only vanilla-edit mode** (no plugin on the client) remains that same phase-2 gate.

The editor receives a `PrefabEditHandle`: `Entity` / `World` / `Guid`, `Has<T>` / `Read<T>` / `TryRead<T>` / `Write<T>` through the ECS helpers (typed writes go through `RawWriteGuard`), and `GetComponent<T>` / `SetComponent<T>` for `ItemData` (not C# unmanaged; `SetComponent` also goes through `RawWriteGuard`, and a non-`ItemData` type is refused with a Warn naming `Write<T>`). Editors may change component values (stack size, recipe fields, icon references). They must not retarget `PrefabGUID` or strip the Prefab tag; those writes are skipped and logged (`PrefabRegistry: refused structural write of PrefabGUID by {plugin} on guid {hash}`). An `ItemData` write keeps `ItemTypeGUID` equal to the prefab GUID and refreshes `ItemHashLookupMap`. An editor that throws is skipped (later editors still run); the pass is **not** fingerprinted, so the next trigger retries, and the Error names the plugin id and exception **once per Core generation**:

```
PrefabRegistry: edit from boom.id on guid 42 threw: System.InvalidOperationException: nope
```

Order is deterministic: numeric `PrefabEditPriority` (`Early = -1000` under, `Default = 0`, `Late = 1000` on top; intermediate ints are allowed, e.g. `(int)Early + 5`), then Harmony-style `before` / `after` plugin ids (missing ids are ignored), then first-seen plugin load order, then registration index. Last writer of a component wins.

**Removing an editor restores vanilla.** The first tracked write of a component in a Core generation captures the value it had before that write (a baseline, keyed by prefab GUID + component type + the entity's `index:version`). Dispose the handle, unload the plugin (`UnregisterAll`), or lose the editors to the handshake `Unrequire` sweep, and the next frame's coalesced pass writes those captured values back:

* **Editors remain:** the prefab is restored to vanilla and the remaining ordered set re-runs on top of it, so the removed editor's components come back unless a remaining editor writes them (last-writer-wins is unchanged). `PrefabRegistry: editor set changed on guid 42; restored 2 component(s) before re-running 1 editor(s)`.
* **That was the last editor:** restore-only, then the baselines are forgotten — the entity is vanilla again. `PrefabRegistry: restored 1 component(s) on guid 42 after its last editor was removed: ItemData`.
* **Core uninstall** (generation swap) restores every captured baseline first: `PrefabRegistry: restored 2 component(s) on 2 prefab(s) at Core uninstall`. Baselines live in the collectible generation and are not handed through Bootstrap, so restoring here is what lets the next generation's first write capture true vanilla; the clone carries vanilla values for the few frames of a swap.
* A restore callback that throws is logged (`PrefabRegistry: restore of ItemData on guid 42 threw: …`) and skipped; the others still run. If any owed baseline was not restored, `PrefabRegistry: restore incomplete on guid 42: restored N of M captured baseline(s)…` is logged and the prefab is not vanilla. Baselines are then forgotten, so a throwing restore is not retried.
* **A pass that restored nothing is retried — when something is actually owed.** The coalesced flush takes each removal off its pending set before it runs, so a pass that could not do the work — no live world, a pass for that prefab already in flight, the *last* editor's bind failing — would otherwise lose it: for a prefab whose last editor is gone nothing else ever visits the GUID again, because restore-only happens on the `EditorsChanged` trigger and only a drained removal produces one. The removal is put back and the flush re-armed, up to 30 frames, after which one line says the prefab keeps the removed editor's values: `PrefabRegistry: removal never serviced on guid 42 after 30 retry(s): the last answer was NoWorld and 1 baseline(s) are still owed, …`.
  Three cases are **not** retried (job FEDW2, R50 finding 6). A GUID with **zero captured baselines** owes nothing — its editors only read, or never ran — so it is serviced by definition rather than burning thirty frames and then warning about values that were never written. A **bind failure on a prefab that still has editors** is owned by the next `CollectionReady` sweep (the removal already cleared the applied fingerprint), which is the ordinary case of hot-reloading before the prefab collection is full. A **priority cycle** is a property of the registered editors, so a retry cannot break it, and re-queueing it logged the cycle line once per re-queue, thirty-one times in a row. The last two print one Info line naming the reason: `PrefabRegistry: removal not retried on guid 42: the pass answered BindFailed and the prefab still has editors, so the next CollectionReady sweep owns it`. Both terminal lines are printed once per GUID per Core generation.

Every trigger except `NewClone` restores before it re-runs the editors, so **a pass is idempotent by construction**: an editor doing read-modify-write (`Write(Read<T>() * 2)`) sees vanilla again on each re-run instead of accumulating. `NewClone` does not restore — the applicator just wrote definition state onto a new or reset entity — and drops that prefab's baselines. A baseline whose stored entity identity no longer matches (a re-clone, a new client world) is dropped, never written.

**Limit: only the handle's value writes are snapshotted.** `Write<T>` and `SetComponent<T>` are the tracked surface. Structural changes an editor makes through `handle.EntityManager` — `AddComponent`, `RemoveComponent`, buffer edits such as the Sample's `StripSourceVisuals` — are **not** captured and **not** restored; they survive the editor's removal until the prefab is re-cloned. Reaching for `handle.EntityManager` logs once per editor: `PrefabRegistry: unrestorable structural edit via EntityManager by {plugin} on guid {hash}: AddComponent, RemoveComponent and buffer edits are not captured and are not restored on unload`. An editor that needs its structural change undone must do it in its own `Unload`.

A `before`/`after` cycle **refuses** the editor that would close it (the row is not kept; the handle is already invalid). Content.Edit uses the same `OrderSorter.TryAccept` as EventBus and SystemHooks, so the Warn names the same plugin. The **closing** registration is the node at which DFS (walking plugin ids then registration index, not subscribe order in a frame) observes the back edge that completes the cycle. Incremental `Edit` / `On` still drops the candidate being added (already-accepted rows stay); for a two-plugin cycle that is the later `Edit`/`On` call.

```
PrefabRegistry: refused edit from mod.b on guid 42: before/after cycle with mod.a -> mod.b -> mod.a
```

Two plugins writing the same component of the same prefab produce one Warn per loser, naming both, the winner, and why (later priority / before-after / later plugin load order / later registration). Each distinct conflict (prefab guid + component + the two plugin ids) is logged **once per Core generation**. A later apply of the same editor set (a joining client, a second `Apply` of an already-cloned GUID) does not repeat it. The line is re-logged after a Core reload or when the set of editors for that prefab changes.

A new clone (`InsertNew` / `UpdateExisting` resetting definition values) legitimately re-runs the pass so last-writer values land on the new or reset entity. Sending `PrfD` / `PrfK` to a joining client does **not**: that send does not create a clone, and re-running it used to print the same conflict three times per join. `CollectionReady` (WorldReady / load prefix / vanilla sweep) is idempotent: it skips when this GUID already ran with the current **fingerprint** (plugin id + priority + registration index + optional `version` token + invalidation generation). That fingerprint does **not** hash the editor delegate or anything the closure captured. An editor that reads a synced `ConfigEntry` (or any other live input) will keep the first-pass values until the plugin says the inputs changed.

**Invalidate when captured values change.** `PrefabRegistry.Invalidate(prefabGuid)` / `ctx.Content.Invalidate(prefabGuid)` bumps that GUID's generation (the InputsChanged version counter in the fingerprint) so the next pass sees a mismatch. Re-runs are coalesced: many `SettingChanged` events from one cfg Reload produce **one** re-run per affected prefab on the next frame; an Invalidate during an apply pass marks the prefab dirty and re-runs **once** after that pass instead of re-entering. `PrefabRegistry.InvalidateEdits(owner)` / `ctx.Content.InvalidateEdits(ctx.Owner)` queues every prefab this plugin registered an editor on. Call it from `SettingChanged` (or any other input change). Do **not** write the clone yourself — Late (or whichever editor reads the entry) must be the writer, through `PrefabEditHandle.SetComponent` / `Write`, so `ItemHashLookupMap` stays in sync and last-writer-wins stays visible. Optional: `PrefabRegistry.Edit(..., version: "80")` puts a caller token in the fingerprint; changing it requires a new `Edit` registration. Prefer `Invalidate` when the editor is already registered.

**Apply-before-packet.** The server runs pending applies and the NewClone edit pass **before** it sends `PrfD` / `PrfK` (`PlayerConnected` opens the gate and applies, then the packets go out). Reliable events keep that order, so the client treats the first packet as "the table is complete", applies locally, and runs its own editors on the new clone. The client pass uses **this process's** captured values at that moment, and the server's `PrfE` for that prefab lands after `PrfD` / `PrfK` and overwrites them.

Typical join with a synced config entry (Sample `Sample.EmberStack`):

1. Client apply / NewClone runs on the **local** cfg (often the default 99). Fingerprint remembered.
2. Handshake result, then `ConfigSync` sets the server value. `SettingChanged` fires.
3. The plugin must `InvalidateEdits` on that event. CollectionReady will **not** converge on its own (the editor set did not change). The next pass — the Invalidate re-run, or a later NewClone — is what writes `MaxAmount`.

Two orderings, one finished state after handshake: if the config packet arrives before the clone exists, `Invalidate` no-ops the entity bind, the bumped generation makes the later NewClone Late already read the server entry. If apply ran first, Invalidate re-runs Late now. **Before handshake** the clone may show the client-local default; `.ember` / tooltips in that window are not the server cap. A stack created in the window uses that cap.

```
PrefabRegistry: conflict on prefab guid=42 component=ItemData: Alpha (mod.a) vs Beta (mod.b); winner=Beta (mod.b) because later priority (1000 over 0)
```

```csharp
ctx.Content.Edit(guid, prefab =>
{
    if (!prefab.Has<ItemData>()) return;
    var data = prefab.GetComponent<ItemData>();
    data.MaxAmount = 99;
    prefab.SetComponent(data);
}, PrefabEditPriority.Late, after: new[] { "other.mod.guid" });
```

**Worked example: a spell's reach.** A projectile flies at `Projectile.Speed` until its `LifeTime.Duration` runs out, so its reach is speed × lifetime; `Projectile.Range` is the designer's number, and the game destroys the projectile on `LifeTime`. Read live on 2026-09-25: `AB_Unholy_CorruptedSkull_Projectile` (917769627) flies at 22 m/s for 0.545 s, and its two wave skulls (608176448, 422968059) at 18 m/s for 0.667 s, so every Corrupted Skull reaches 12 m. `LifeTime` is a float and an enum, so it is written through `Read`/`Write` with no new generic instantiation, and the edit is undone when the plugin unloads. The necro army sample (`SkullReach`) gives the skull 20 m this way; both sides then read 0.909 s and 1.111 s. Without the `[NetworkCompatibility(ServerMustHave, Exact)]` attribute above, the edit is refused at registration and the prefab keeps its value, which is how the sample first failed.

```csharp
const float Reach = 20f;
foreach (var (guid, speed) in new[] { (917769627, 22f), (608176448, 18f), (422968059, 18f) })
    ctx.Content.Edit(new PrefabGUID(guid), prefab =>
    {
        if (!prefab.Has<LifeTime>()) return;
        var life = prefab.Read<LifeTime>();
        life.Duration = Reach / speed;
        prefab.Write(life);
    });
```

`samples/Progenitor.Sample` is the live check. Clone-time `DefineItem("sample.ember").MaxAmount(50)` is the **before**-form (the applicator writes `ItemData` while cloning). `ctx.Content.Edit` is the **after**-form (mutate the live prefab entity). The sample registers a Default editor that writes `MaxAmount = 20`, then a Late editor that writes `CurrentEmberStack()` (synced `Sample.EmberStack`, default 99) on the same component so last-writer-wins is visible. `SettingChanged` on that entry calls `ctx.Content.InvalidateEdits(ctx.Owner)` so a server cfg change (or the handshake lock) converges through the edit pass, not a side write on the clone. Core **skips** a conflict Warn when both writers share a plugin id (same-plugin stacking is silent); the Late pass therefore uses a distinct `pluginId` (`progenitor.sample.late`, display name `Sample Late`) with this plugin as owner so unload still reverses it and the Warn actually fires:

```
PrefabRegistry: conflict on prefab guid=802060347 component=ItemData: Progenitor Sample (progenitor.sample) vs Sample Late (progenitor.sample.late); winner=Sample Late (progenitor.sample.late) because later priority (1000 over 0)
```

Live value after both editors: `ItemData.MaxAmount` is the synced `Sample.EmberStack` (default 99). Bridge `status` on **both** sides reports the same fragments: registry `edits=2`, and per prefab `itemMap max=N` plus `edits=2 ItemData` on `sample.ember` (`node tools/mcp/call.mjs client status` matches the server line when the client Sample ran the same editors). `.ember` prints `max=N`. Live recipe: edit `BepInEx/config/progenitor.sample.cfg` on the server (`[Sample]` / `EmberStack = 80`); file-watch fires `SettingChanged`; server log `Sample: EmberStack changed to 80; invalidated edits`; ConfigSync pushes; client log `Sample: EmberStack applied/locked to 80`; `status` fragment `itemMap max=80`. No reconnect. Before handshake the clone may still show the client-local default.

```csharp
ctx.Content.Edit(Plugin.EmberGuid, prefab =>
{
    if (!prefab.Has<ItemData>()) return;
    var data = prefab.GetComponent<ItemData>();
    data.MaxAmount = 20;                 // Default; loser of the intentional conflict
    prefab.SetComponent(data);
}, PrefabEditPriority.Default);

ctx.Track(PrefabRegistry.Edit(
    Plugin.EmberGuid,
    prefab =>
    {
        if (!prefab.Has<ItemData>()) return;
        var data = prefab.GetComponent<ItemData>();
        data.MaxAmount = Plugin.CurrentEmberStack(); // Late; last writer, live stack size
        prefab.SetComponent(data);
    },
    ctx.Owner,
    PrefabEditPriority.Late,
    pluginId: "progenitor.sample.late",
    pluginName: "Sample Late"));

// SettingChanged (handshake lock or server cfg watch): re-run the pass, do not write the clone.
ctx.Content.InvalidateEdits(ctx.Owner);
```

Delegates live in the plugin ALC. Core Uninstall drops the table; plugins re-register from `OnLoad` after a Core swap the same way they re-`Register` cloned items. Bootstrap `ContentMemory` does not hold editors.

**What is synced to clients.** One reliable packet per definition (`PrefabDefinitionPacket`, event id `'PrfD'` / `0x50726644`, **204 bytes**: three little-endian ints then three 64-byte ASCII fields). Fields: new GUID, source GUID, `MaxAmount`, 64-byte ASCII `id`, 64-byte ASCII localized name, 64-byte ASCII localized description. Sent on `PlayerConnected` (bus priority 2000; reliable events keep order, so they land before join-time validation) **after** the server has applied pending definitions and key-table placeholders — unapplied entries are skipped so the packet the client treats as "table is complete" describes rows this process actually appended. Also sent to currently connected users when a definition is applied. The client inserts the same lookups. The first `PrfI`, `PrfD` **or** `PrfK` of a join opens the client apply gate; `PrfI` does not dump local registrations in plugin-load order.

**Unregister (soft unapply).** Dispose the handle (or `UnregisterAll`) is a **soft unapply**: the clone entity and every GUID map stay so inventories that still hold the item keep resolving; only the definition's undo list (icon lease) and this registry's bookkeeping are cleared. Localization keys stay with the owner until `Localization.RemoveAll` / plugin unload. The server then sends an unregister packet (`PrefabUnregisterPacket`, event id `'PrfU'` / `0x50726655`) to currently connected clients so they unapply too: GUID int32 then the same 64-byte ASCII id. Distinct from the definition packet so the two cannot be mistaken for each other. A later `Register` of the same id drops the tombstone and **updates the surviving clone in place** (ItemData, managed name and description) when `Kind` and resolved `CloneOf` are unchanged. A changed source with no live instances re-clones under the same GUID; live instances or an Item↔Buff kind change refuse that definition. The existing key-table index is reused (never compacted).

**Core reload.** A Core ALC swap unloads plugins first (soft unapply, entity kept), snapshots clone ownership into Bootstrap `ContentMemory`, and the next generation rehydrates that set before plugins re-`Register`. The snapshot also carries each clone's resolved `CloneOf` hash (`ContentMemory.ReplaceCloneSources`, independent of the 6-arg cloned/appended/tombstone `Replace` Runtime already calls) so a same-id source change after the swap retargets or refuses by hash, not by NetSnapshot schema type. A GUID this process cloned in any earlier generation — **item or buff** — is re-claimed (in-place when `Kind` and `CloneOf` match; otherwise retarget or refuse). Buffs never occupy `ItemKeyLookup`, so they are not tombstoned; retirement still records the GUID as ours in the cloned set (`ownership kept for snapshot`). A GUID cloned by another process or mod (not in the snapshot) is still refused (`already in _PrefabGuidToEntityMap and was not cloned by this process`). Healthy re-register after a swap: `unregistered '…': … retired (entity kept) …; ownership kept for snapshot`, then `snapshot cloned=N` / `rehydrated cloned=N` including that GUID, then `applied '…': … updated existing clone …` (or `retargeted (was X, now Y)` when `CloneOf` changed and no live instances exist).

**Tombstones and PrfK.** ItemKeyLookup rows are never removed (see below). A GUID that was appended in this process and then unregistered is kept as a tombstone and sent to new joiners as a key-table placeholder (same `PrefabUnregisterPacket` layout, event id `'PrfK'` / `0x5072664B`) so they append the same row without registering a prefab. Placeholders also go to currently connected clients right after `PrfU` (reliable events keep order: unregister first, then placeholder). Already-present is success. `PrefabRegistry.Describe()` reports `registered=N tombstones=M`. A later definition of the same GUID drops the tombstone and applies (full clone if this client never had the entity; in-place update if a prior apply left the clone retired-but-resolvable).

**Edit results on the wire (`PrfE`).** Job **FEDW1**, design [`edits-on-the-wire.md`](edits-on-the-wire.md). The server is authoritative for edited component values: after an ordered edit pass writes anything, it serializes exactly the components that pass touched and sends them as event id `'PrfE'` / `0x50726645`. Layout (little-endian, variable length): `int32 guidHash`, `int32 buildHash` (FNV-1a of the **shared game identity**, `PrefabEditBuildIdentity.Compose`: `vrising/<major>.<minor>.<patch>.<revision>+r<vcs>+b<build>/net<networkVersion>`, read from `GameVersionUtils.GetVersionData()` and `GetNetworkVersion()`; `0` when the version could not be read, and `0` matches only `0`), `int32 editIdentityHash` (FNV-1a of the prefab's editor stamp), `uint8 count` (≤ 16), then per record `uint8 nameLength` (1–127) + ASCII type full name + `uint16 payloadLength` (1–1024) + payload. The whole packet is capped at 15 KiB (the transport's own limit); a record that would break a cap is refused by name before the packet is built, never truncated into it.

The reader closures are built at the write site next to the baseline capture, and **both** produce the marshaled layout — a fresh typed read followed by `Marshal.StructureToPtr` into a `Marshal.SizeOf(type)` buffer, which is exactly what the receiver validates the length against and what `Marshal.PtrToStructure` parses. One measure on this wire, and nothing is measured at runtime and multiplied. (FEDW1 had the typed reader emit `MemoryMarshal.AsBytes`, the *managed* layout; FEDW2 corrected it — the two disagree for every bool-carrying struct, and this game is full of them.) Sends: to each **handshaken, connected** user when a pass changes the bytes (a payload hash per prefab; an unchanged re-run sends nothing, and the hash is asked *before* the serialize so a no-change pass costs a read rather than a full frame-up), at most **one packet per prefab per second** with the suppressed send coalesced into a trailing one; to a joining user after its `PrfD` / `PrfK` (reliable events keep order, so definition, key row, then edited values; that send follows `PrfD`'s own join policy and is not gated on the handshake, which has not answered yet at `OnUserConnected`); and **again to one user on demand**, through `PrefabRegistry.ResendEditLayersTo(userIndex)` — the seam the handshake calls when an **already-ok** user re-answers, which is what a *client-side* Core swap looks like from the server (the client rebuilds its table empty and re-declares itself, but the server's answer status for that user never leaves `ok`, so the handshake-ok *edge* the wire also polls cannot fire; that edge still catches a **server** Core generation, where every user re-answers). Every per-user send is deduped **per user**, so the join send, the poll and the resend cannot duplicate each other and a resend to a client that already has the layers sends nothing. When the last editor of a prefab is removed, or a pass stops touching it, the server sends a **withdraw** — a `PrfE` with `count == 0` — and the client drops the layer and restores the baseline; without it a layer pushed onto a *vanilla* prefab could never be retracted, because `PrfU` only reaches prefabs with a registry entry.

The client resolves each type by full name from assemblies **already loaded** whose simple name starts with `ProjectM`, `Stunlock` or `Unity` — nothing is ever loaded to satisfy a packet — and refuses per record, each with its own line, when the name resolves nowhere, is not a value type, is generic, **carries a managed reference** (`RuntimeHelpers.IsReferenceOrContainsReferences`: `MakeGenericMethod` does not enforce `Write<T>`'s `unmanaged` constraint, so "value type" alone would let a struct with a `string` field be memcpy'd into a chunk), is an identity component (`PrefabGUID`, the `Prefab` tag, anything `PrefabEditIdentity` names), is **entirely reference-shaped**, or the declared length disagrees with the receiver's own `Marshal.SizeOf`. **The guid is not constrained** — a server may name any prefab the client carries, vanilla included, which is inherent to the feature and is why the shape rule is what it is.

**The boundary is a list of components, not a rule about fields (job FEDW4, R52 finding 1).** `Content/PrefabEditWireTypes.cs` is an explicit allowlist of the component types Core vouches for on this wire, each with a per-field policy (which fields are taken from the wire, which are kept from the receiver, and the expected `Marshal.SizeOf`). A record whose type is not on it is refused on **both** ends — the server registers no reader for it, so it is never serialized, and the client never applies it — with one line per type per Core generation naming the list. The seed is exactly what the Sample edits: `ProjectM.ItemData` (48; keeps `Entity`, `ItemTypeGUID`, `DropItemPrefab`, `DropItemArc`), `LifeTime` (8), `BuffModificationFlagData` (16), `DisableAggroBuff` (4), `ModifyBloodDrainBuff` (36). **Why a list and not a smarter classifier:** R52 measured that an integer the game uses as an index into a native table is indistinguishable from an integer it uses as a quantity — `Unity.Entities.TypeIndex` is `struct { int Value; }` and is carried by 4,585 structs, `RuntimeBlobHeaderRef` keeps a blob pointer in a plain `long`, and 351 raw `*Index`/`*Offset` fields are plain numbers — so no walk over fields can close the class, and the decision of what a stranger's server may write into a player's process is made by name, by a human, once. **A plugin cannot extend the list over the wire**, and there is deliberately no packet that carries one: an author who needs another component to travel opens a Progenitor change with the dump evidence (fields and offsets) and the per-field policy.

**The value domain: what may be *in* an admitted field (job FEDW5, R53 finding 2).** Being on the list says a field may travel; it does not say what may be in it. Each admitted field therefore carries a value policy or an explicit "none, and here is why": a **declared enum member** (read from the receiver's own copy of the enum by reflection, so the domain follows the build), **declared bits** for a `[Flags]` enum or a bare-integer bitfield over a named enum, an explicit **range** with its reason and dump citation, **finite** for a float, or **0 or 1** for a marshaled bool. The seed's fifteen policies cover the three dense enums R53 named (`ItemData.ItemType`, `LifeTime.EndAction`, and the byte-wide `ModificationType` with 247 undefined values), the `BuffModificationTypes` bitfield, `DisableAggroBuffMode`, `ItemData.MaxAmount` (`0..2^30-1`), the four floats and the four bools. The check runs on **both** ends — the sender will not frame an out-of-domain record, the receiver refuses it after the preservation mask and before the write — refuses that record only, and logs once per type and field per Core generation, keyed on the type and the field and never on the reason, because the reason carries the attacker-chosen value. A domain that cannot be resolved (a policy naming a field this build lacks, an enum rule over a field that is no longer an enum) refuses every record of the type with the same "the wire-safe list is stale" shape. **Every rule fails closed, including the one that used to fail open (FEDW6, R54 #3).** A bare-integer bitfield names its enum in a string, and that name used to be looked for in one assembly and, when it was not there, dropped silently onto the dump-cited literal beside it - no log, no marker, and "the domain follows the build" quietly untrue for that field. The enum is now resolved across the set the wire's type resolver already walks (the naming type's assembly, then every loaded `ProjectM*` / `Stunlock*` / `Unity*` assembly, then `Type.GetType`; nothing is ever loaded to satisfy a packet), because this interop's split is not the stable thing a single-assembly lookup assumes - `ItemData` and `LifeTime` are in `ProjectM.Shared.dll` while `DisableAggroBuff` and `ModifyBloodDrainBuff` are in `ProjectM.dll`, and `ProjectM.ModificationType`, used by a struct in `ProjectM.dll`, is in `ProjectM.Shared.dll`. An enum that still does not resolve refuses every record of its component. **And no step of that resolution is decided by load order (FEDW7, R55 #6):** the enum is looked for first in the assembly the seed *names* (every `NamedEnumBits` policy records it — `ProjectM.BuffModificationTypes` is declared by `ProjectM.Shared`, read off the interop with the IL viewer, and a seed test refuses an entry that omits it), then in the naming type's own assembly — two lookups in one named assembly each, not searches — and only then across the allowed set, where **every** match is collected and two or more declaring the same full name *refuse* the component as `stale … ambiguous` instead of the first one winning; `Type.GetType` closes the list for an already-qualified name, and nothing is loaded on any step. That matters because the allowlist is a `StartsWith` prefix test (`ProjectMagic.dll` passes it) and, unlike a component type, a wrong enum has no downstream backstop — it silently becomes the mask enforced against bytes another machine wrote. The literal is kept as a **cross-check**: it is never what the wire enforces, and a resolved mask that differs from it logs `value domain mask for ...` once per Core generation as a citation to re-derive. The suite declares a real `ProjectM.BuffModificationTypes` so the fixtures take the same path a live client takes - a branch that exists only for tests is a branch a game update takes silently. **And the dump citations are checked, not trusted (FEDW6, R54 #5):** R54 found eleven of the per-field ones off by one or two lines, `ItemData.MaxAmount` - the one field with a numeric bound - citing the line of a `PrefabGUID` this same list preserves. All of them were re-derived, and a `[DumpCitationFact]` now parses the citations out of the policy reasons and the doc table and requires each cited line to name what it is cited for, skipping with a reason on a machine without the dump. The **per-field analysis** — what the game does with each of the twenty-seven fields, its policy, and the worst case a hostile server gets from it — is the table in [`docs/edits-on-the-wire.md`](edits-on-the-wire.md), mirrored as a comment beside the seed, with a test comparing the field names in both against the list itself.

**The shape rule (job FEDW3, R51 finding 1), now the second gate inside the list.** It decides *how much* of a vouched component travels, and it must **agree** with the per-field policy: a size the build disagrees with, a field the build added or removed, or a field the list calls a value that the classifier calls reference-shaped refuses the record with a "the wire-safe list is stale" line. A game update that moves a layout therefore fails closed. A field's bytes come from the wire only when it is a primitive numeric, `bool`, an enum, or a struct of those, recursively. Anything else — a pointer, `IntPtr`/`UIntPtr`, `Entity`/`ModifiableEntity`, `PrefabGUID`, `BlobAssetReference`/`BlobArray`/`BlobPtr`, `FixedString*`, any `Native*`/`Unsafe*` handle, a nested struct reflection could not open, an integer-encoded handle matched by name (`TypeIndex`, `RuntimeBlobHeaderRef`, `JobHandle`, `ColliderKey`, `AllocatorHandle`, or any type or *field* name ending `Index`/`Offset`/`Handle`/`Ref`), or a form it could not name — is **reference-shaped**: the receiver builds a byte mask of those ranges from *its own* `Marshal.OffsetOf`/`SizeOf` and copies **its own current component bytes** over them before writing, so `ItemData.Entity` and any pointer stay exactly what the client already had. A component whose every byte is reference-shaped is refused, and so is one whose fields reflection could not enumerate at all — the 1,916 structs R52 measured used to be written *whole* from the wire, the inverse of this rule — and the same classification runs on the server at send time, so such a component is never shipped. One Info line per type per Core generation names the preserved fields. Without this the filter was an identity denylist with no shape check at all, and R51 measured 169 interop value types with a direct `BlobAssetReference` field, 781 with an `Entity`, 286 with a raw pointer — all C# `unmanaged`, therefore all writable by any server the player joined, on any prefab it named. `ItemData` is held to the rule like every other type; it used to be the one path with no shape check whatsoever. **One measure on this wire:** every reader produces `Marshal.StructureToPtr` bytes over `Marshal.SizeOf(type)` and the receiver validates and parses the same layout (job FEDW2; FEDW1's typed reader had produced the managed `sizeof` layout instead, which disagrees for the bool-carrying structs this game is full of). `ProjectM.ItemData` is written through `SetComponentData<ItemData>` plus the `ItemHashLookupMap` re-sync; every other type through `EntityExtensions.Write<T>` closed with `MakeGenericMethod`, which is a **managed** CoreCLR generic over `SetComponentDataRaw` (interop trap 2 is about instantiating IL2CPP generics, which this path never does) and still goes through `RawWriteGuard`. The layer is stored per guid and **re-applied after every local edit pass** on that prefab, so a client-side editor cannot win; it is captured as a baseline like any other write, so `PrfU` and the removal of the last editor restore vanilla; it is dropped on a new client world and at Core uninstall. A layer that arrives before `PrefabCollectionSystem` has the entity is **queued and retried** on a low-rate frame with a bounded budget (240 attempts, about a minute) and one line on give-up, rather than dropped in silence — for a client with no local editor for that prefab, nothing would ever have revisited it. An identity mismatch — two processes on different game builds, or one that could not read its version — drops the whole packet with one line per Core generation.

**What a server may push, per join.** Each prefab charges the per-join synced-row budget once (the cap bounds how many prefabs a server may reach into, not how often it corrects them), so the *packet* has its own bounds: at most **512 `PrfE` packets** and **1 MiB of payload** per join, charged before the packet is parsed, with one line when spent; then a **receive-side payload dedupe** per prefab, so an identical repeat costs a parse and nothing else — no store, no entity write, no log line. Type names on the wire must be **printable ASCII** (the parser used to mask control bytes and keep them, which let a name forge whole log lines), and every name that reaches a log line is additionally sanitized to printable ASCII, 64 characters, with the line saying when it was altered.

Log lines: `sent PrfE for <guid>: N component(s) to M user(s)`, `sent PrfE withdraw for <guid> to N user(s)`, `applied PrfE for <guid>: <types> (server layer)`, `withdrew PrfE for <guid>: …`, `PrfE <type> for guid <g>: kept this client's own N reference-shaped field(s) …`, `refused PrfE record <type> for guid <g>: <reason>`, `dropped a PrfE packet: <error>`, `PrfE build mismatch on guid <g>: …`, `PrfE never applied for guid <g> after N attempt(s): …`, `server exceeded the per-join PrfE budget …`, `re-sent N PrfE layer(s) to <who> after its handshake completed`.

**When it applies — never before the world is ready.** Applies are held until a side-specific gate opens. A per-frame retry that succeeded while the game was still building ItemKeyLookup once appended the first custom item at index **1077** into a table that later held 1084 rows, so the row moved and join-time count+hash broke.

- **Client:** the gate opens on the **first `PrfI`, `PrfD` or `PrfK` of a join**, not on `WorldReady`. `PrfI` opens the gate **without** dumping local registrations in plugin-load order; the client rebuilds the tail in server order or fail-closes if no ledger is accepted. A first `PrfD`/`PrfK` with no accepted ledger still opens the gate but skips custom-item appends. Historical live evidence: at `WorldReady` the game is still building `ItemKeyLookup` (1077 of a final 1084 rows), and a row appended then ends up at a different index than the server's (Hermit's Shawl). The server sends `PrfI` then `PrfD`/`PrfK` from its `OnUserConnected` postfix, after the table is complete. Further packets of the same join do not dump pending again. Paired live agreement is not run. A Core swap after a refused ledger must not append in local order (internal review R87 finding 1 — open at time of writing).
- **Client connection end** (job FSCOPE1; live-accepted 2026-09-28 on the isolated test server with the `fix/client-server-switch-scope` build, before the port to main; the port is offline-tested only and the check below has not been re-run on a main build: one Steam client process joined server A (Sample, NecroArmyExample and others), left, joined server B (Progenitor Core and Dev only), then rejoined A; see below): everything a server sent is scoped to that connection. When the client's connection ends — `ClientState` Error or Idle after a held connection (kick, walk to the menu, timeout; `Client_0` may survive), or `Client_0` ending — the gate closes and the registry, in this order: withdraws every stored `PrfE` layer through the same path as a withdraw packet (captured baselines go back and this client's own editors re-run, when the world still exists); drops the server-synced definitions (undo run) and every string that server supplied; reverts the fields a `PrfD` filled into a **local** definition of the same GUID (stack size, `CloneOf`, name, description, buff promotion); and clears tombstones and the join session. Locally-owned `Register`s and local editors stay. Server text is registered under the server-sync owner, not the plugin, so it goes with the connection. `Ai/InertClones` also drops the server's inert-clone announcements on this signal. A `Reconnecting` state counts as still connected, so a failed reconnect that ends in Error still raises the signal. The signal is `Handshake.ClientConnectionEnded` (internal), which, unlike the Mods-workspace `ServerContextCleared`, is not raised on a handshake epoch change inside the next join. Log lines: `withdrew N server edit layer(s) at connection end`, `dropped N server-synced definition(s) at connection end`, `restored N local definition(s) the server had filled and dropped M server string(s) at connection end`.
- **Client rejoin (new client world):** a new `Client_0` world closes the gate again and repeats the connection-end drop as a backstop. It also forgets the **world-scoped** bookkeeping of the previous world: the clone/key-row ledger (`PrefabCloneLedger`), the edit-pass fingerprints and write records, the captured baselines, and the recipe station claims and clones (`RecipeApply.ForgetClientWorld`). The reset runs in a WorldReady handler at priority 10000, ahead of every plugin and Core subscriber. Before FSCOPE1 the ledger survived, so a client's own item recorded at row 1083 in the first join failed every later join with `apply 'sample.ember' failed: key index mismatch guid=480926629 saved=1083 now=-1` and was dropped (client logs 2026-09-20 and 2026-09-27), and a surviving fingerprint made `CollectionReady` skip this client's own edit of a vanilla prefab in every world after the first. None of this is forgotten at connection end, because `Client_0` can outlive the connection, nor on the `WorldReady` a Core swap republishes for the same world. Placeholders are **not** appended at that `WorldReady` — that was the 1077-of-1084 failure on rejoin. Packets of the new join arrive after this event. Log line: `forgot N clone/key row(s) and M edit pass(es) of the previous client world`.

  **Live evidence (FSCOPE1, 2026-09-28).** The case is one client process joining server A, then server B, then server A again, with the `fix/client-server-switch-scope` branch's Core on both sides (not a main build).
  - Joining A logged `forgot 9 clone/key row(s) and 3 edit pass(es)`. `sample.ember` applied at `index=1083 (ok)`, and six `PrfE` layers applied. Client and server readbacks matched: LifeTime 1.111/1.111/0.909 on guids 422968059/608176448/917769627, and Crystal `MaxAmount` 40.
  - Leaving A logged `withdrew 6 server edit layer(s) at connection end`, `dropped 6 server-synced definition(s)` and `restored 2 local definition(s) the server had filled`.
  - On B, no `PrfE` applied. The client read 0.667/0.667/0.545, not A's values. `sample.ember` failed closed because B's key table does not know it.
  - Rejoining A logged `forgot 5 clone/key row(s)`. `sample.ember` applied at 1083 again with no `key index mismatch`, and every layer re-applied with the same readbacks.
  - When B's server process died, the client ran the connection-end reset with `Client_0` still alive. B's crash was a separate server-side issue.
  - Not observed: pixel-level UI or tooltip text, and a server that sends server strings. `dropped 0 server string(s)` in both runs.
- **Client-only items** (a local `Register` with no server packet: single-player, or a server without the mod) **never open this gate**. The frame retry still runs while closed so the give-up budget is not infinite; after 3600 frames Core logs once and stops. They require a `PrfD` or `PrfK` from the server. The next `WorldReady` or packet will try again.
- **Server:** the Harmony prefix on `LoadPersistenceSystemV2.DeserializeLoadFile` is the authoritative seam (unknown GUIDs are dropped on load if the clone is not already in the maps). It opens the gate and applies. `WorldReady` on the server does **not** open the gate — the load takes many frames after it, and retrying in between only burns the give-up budget. **`PlayerConnected` opens and applies before any send**, so a joiner never receives a definition that is not yet in the server table (`PrfD` is skipped for entries that have not landed in this process's lookups). A brand-new world never loads a file, so the server also opens the gate **30 s after WorldReady** as a no-player fallback; a connect in that window opens it earlier.
- Both sides retry on `Plugin.Frame` while an apply is pending because the source collection is not populated yet (give-up after 3600 frames). The server does **not** count those frames while the gate is closed.

**Key-table index rule.** Inventory slots travel as an index into `ItemKeyLookup`, validated at join by count and hash, so both sides must append the same GUID at the **same index**. On this save the vanilla table has 1083 rows (indices 0–1082); the first custom item lands at index **1083** on both sides and those indices must match. Do not apply before the world is ready (see above) or the row can land earlier and then shift.

`Definitions` is the in-memory list (including not-yet-applied). `Registered` fires when a definition is accepted, which may be before the live world is ready. `Describe()` is the registry summary (registered + tombstones). `Describe(world, guid)` is a diagnostic string for the debug bridge (a retired prefab whose clone still resolves is marked `retired (entity kept)`; a key-table placeholder with no clone stays a tombstone).

**Item-key caveat.** Inventory slots do **not** travel as GUIDs. They are an index into `ProjectM.ItemKeyLookup` (`NativeList<PrefabGUID>` + reverse map). A GUID missing from one side's table serializes as a bad index and the client shows whatever item that index maps to. The registry appends the clone on both sides through the raw Collections 2.x layout — generic `GetSingleton<ItemKeyLookup>` / `NativeList<PrefabGUID>.Add` hard-crash. Append refuses if the list or map would need to grow (no native allocator call; a guessed convention crashed a process). Already-present is success.

**ItemKeyLookup rows are never removed.** Unregistering a middle entry and compact-shifting later indices would remap every later slot, desync connected clients, and make live inventories show the wrong item. The GUID stays in the table as a tombstone. Soft unapply also **keeps** the clone entity and the other GUID maps (the item stays resolvable); only localization undo and registry bookkeeping are cleared.

**What a server may push at the client, per join.** `PrfD` and `PrfK` come from whichever server the player joined, and the client is not required to trust it (threat model surface 6). Three rules on the receive path:

- **Field validation before anything is cloned or appended.** The wire struct is fixed-size, so a wrong `length` is already dropped by `CustomEvents.TryRead`. On top of that both handlers require a 1–63 ASCII `id` (`PrefabWire.TryValidateAscii`) and `NewGuid == PrefabGUID.CalculateGuidHash(id)` — `PrfK` is now held to the same self-consistency rule as `PrfD`, so a server cannot append an arbitrary hash under a name that does not derive it. Refusals log `PrefabRegistry: ignoring PrfD/PrfK with an invalid id: {error}` or `… NewGuid=… != CalculateGuidHash …`.
- **A shared distinct-identity cap** (`PrefabSyncBudget`, config `Networking.MaxSyncedPrefabsPerJoin`, default 256). Definitions, tombstones and first edits share a GUID charge; replacing an existing identity reuses its charge, and unregistering does not refund it. The cap resets for a new join. Beyond it, new identities are dropped with one warning per join; existing identities can still update. Definition/placeholder packets also share a 1,024-packet, one-second receive window. Stat packets have a separate window of the same size. Pack scans are five seconds apart; plugin authors must avoid bursts beyond these limits because dropped updates are not retried automatically. Edit packets retain their separate per-join packet/byte limits.
- **One summary line per join**, written when the next join's gate opens: `PrefabRegistry: server added N definition(s) and M key placeholder(s) in the previous join.`

The client's table is **not** part of a save and needs no sweep: `BuildItemKeyLookupSystem.OnUpdate` rebuilds it every session from live `RegisterPrefab` entities (add-if-not-contains, `IntroSort`, rebuild `ItemToIndex`), and `ItemKeyLookup` is two `NativeContainer`s on a system entity. Rows a server added live only as long as this session's clone entities; connection end drops last-join `SyncedOwner` entries, and a new client world also forgets which rows this process appended, before the new join's packets arrive. Removing a middle row by hand is never the recovery (it desyncs every later slot); the cap is the bound. Code refs: `Content/PrefabRegistry.cs` `OnClientDefinition` / `OnClientKeyPlaceholder`, `Content/PrefabWire.cs` `PrefabSyncBudget`, research `item-key-table.md` §5 and §11.

**Join after unload.** A new joiner who connects after an unload (and without a re-register) receives a `PrfK` placeholder so they append the same key-table row. Join-time count+hash then matches. They do not get a clone until a later `PrfD` (or a local `Register` of the same id).

## Where should my data go?

A world wipe and a world restore act on the **world save folder only** (`<persistentDataPath>\Saves\v4\<save>`). The game's scheduled wipe archives that folder as `<save>_<ticks>` and starts an empty one; a [world restore](world-restore.md) replaces it with an earlier copy. Anything a mod keeps somewhere else is neither archived, reset nor rolled back with the world. Pick the store by what must happen to the data at a wipe and at a restore:

| Data | Examples | Where | At a wipe | At a world restore |
|---|---|---|---|---|
| **World state** | claims, quest progress, a ledger of what this mod spawned or granted, per-world counters | `ctx.Save` ([`ModSave`](#persistence)): `Saves\v4\<save>\progenitor\<guid>.json` | archived with the old world; the new world starts with none | goes back to the restored world's copy |
| **Server settings** | rates, limits, feature switches | the mod's config through Progenitor's settings: `ctx.Bind(..., synced: true)` or a Server-wide setting ([config sync](config-sync.md), [setting scopes](settings-scopes.md)): `BepInEx\config\<guid>.cfg` | unchanged | unchanged |
| **Data meant to survive wipes** | season stats, a hall of fame, bans, rewards carried into the next wipe | `ctx.ServerSave` ([`ServerSave`](#server-level-store-serversave)): `<persistentDataPath>\progenitor-server\<guid>.json` | unchanged | unchanged: **not** rolled back |
| **Player preferences** | HUD layout, a personal toggle, keys | client side: `ctx.Config` on the player's own `<guid>.cfg`, a Personal setting, `ctx.Keybinds` | unchanged (on the player's PC) | unchanged |

```csharp
using Progenitor.Persistence;

// World state: archived by a wipe, rolled back by a restore. A read-modify-write goes in WhenReady.
ctx.Save.WhenReady(s => s.Set("bossKills", s.Get("bossKills", 0) + 1));

// Survives wipes and restores. Key by platform id, never by entity or character name.
var wins = ctx.ServerSave.Get("seasonWins", new Dictionary<ulong, int>());
wins[platformId] = wins.GetValueOrDefault(platformId) + 1;
ctx.ServerSave.Set("seasonWins", wins);
ServerSave.SaveAll();   // optional: on disk now (a ban), not at the next save trigger

// Server setting: the server's cfg, pushed to clients for the session.
var rate = ctx.Bind("Loot", "Multiplier", 2.0f, "Loot multiplier for everyone.", synced: true);

// Player preference: the player's own cfg, on their PC.
var showHud = ctx.Config.Bind("Hud", "Show", true, "Show this mod's HUD.");
```

Rules that follow from the table:

- **Do not keep world state in your own file.** A file under `BepInEx\config`, `BepInEx\plugins` or anywhere else outside the world folder is not archived by a wipe, so the new world inherits the old world's state, and it is not rolled back by a restore, so the restored world and your file disagree. Progenitor restores the world folder only; other mods' own files are never restored.
- **Choose by what the value describes.** A fact about this world (a castle, a granted item, a spawned unit) goes in `ctx.Save` even if it is about a player, because it must go back in time with the world. A fact about the person across worlds (season wins, a ban) goes in `ctx.ServerSave`, and a restore will not undo it: decide what your mod does when a restored world meets newer server data (for example, re-grant a reward only when the world's own ledger lacks it).
- **No settings in a store.** A switch an admin flips belongs in config, where the settings page, config sync and audit see it.
- Both stores are server-only. In a client process `ctx.Save` and `ctx.ServerSave` are the same silent no-op store, so shared code needs no side check. In a hosted private game the bundled server is a server process: the game starts it with `-persistentDataPath` set to the client's own persistent data path ([Hosted](#hosted)), so its server store is shared by every private game hosted from that PC. Source-checked against the launch template, not live-verified.

## Persistence

Research notes (internal): `GetCombinedFullSavePath` returns the save file; `Saves\v4\<name>` layout; pending-store merge; relocate keep-old-path on failed move.

`Progenitor.Persistence`. Per-mod JSON that lives with the world save, not with the plugin. Disposing the plugin does not delete the file. Server only; on the client `ctx.Save` / `ModSave.Store` return a silent no-op so shared plugin code does not need a side check.

```csharp
var store = ctx.Save;                          // same as ModSave.Store(ctx.Guid); client is a no-op
var book = store.Get("joins", new JoinBook());
book.Players.Add(new JoinRecord { Name = ev.CharacterName, Count = 1 });
store.Set("joins", book);
store.Remove("joins");
foreach (var key in store.Keys) { }            // snapshot, not live
```

`IModStore`: `Get<T>(key)`, `Get<T>(key, fallback)`, `Set<T>(key, value)`, `Remove(key)`, `Keys`, `WhenReady(callback)`. Values are `System.Text.Json` (indented, case-insensitive, include fields). `modGuid` must not contain a path; invalid filename characters become `_`.

**Where.** Beside the game's `AutoSave_N.save.gz` files, in a `progenitor` subfolder, so a copied save takes mod data with it. On this build that is typically `<persistentDataPath>\Saves\v4\<saveName>\progenitor\<modGuid>.json`. `ModSave.CurrentSaveDirectory` is that save **folder**, or null until resolved — mods should not build paths from it themselves. The game's `ServerRuntimeSettings.GetCombinedFullSavePath` returns the save **file** (`…\<save>\AutoSave_N.save.gz`), not the folder; ModSave takes `Path.GetDirectoryName` of that path so stores sit next to the gzip, not inside a nested file-named directory.

**Pending stores (disk-first merge).** `Store(guid)` before the save folder is known returns a pending store: writes stay in memory and `SaveToDisk` is a no-op (there is no path yet, and a write would shadow the real JSON). When the folder is resolved, disk JSON is the base and in-memory dirty keys overlay it (dirty keys win, including a pending `Remove`). An `OnLoad` `Set` on a store created before `WorldReady` therefore cannot wipe the rest of the file on the next save.

**`WhenReady`, and the one operation the merge does not make safe (R58 finding 2).** The overlay protects a *blind* write. It does the opposite for a **read-modify-write**: on a pending store `Get` reads an empty document, so `Set(k, Get(k, 0) + 1)` from `OnLoad` computes `1`, and the merge then lays that `1` over the `17` in the file — permanently, on every start. The rule:

- A **read-modify-write** goes in `store.WhenReady(s => …)`. The callback runs once the store is backed by the save folder, with the disk value visible. Three cases, not two (R59 finding 9): **in line, on the call**, when the folder is already known and the caller is on the main thread (a hot reload mid-session, every `OnLoad` once a world is up); **on the next main-thread drain** when the folder is already known but the caller is not on the main thread (a background task — there is no merge to be after, which the old "otherwise … after the pending merge" wording got wrong); and **on the drain after the merge** when the store is still pending.
- A **blind `Set`** from `OnLoad` is fine, but only for keys the file does not already hold — a boot timestamp, a session id. For anything the file may already hold, use `WhenReady`.

`WhenReady` returns an `IDisposable` that cancels the registration. Three things drop it without you: `ModSave.RemoveAll(owner, owned)` (matched on the owner's assembly **and every other assembly in the plugin's collectible context** — the reloader sweep in `Reload/PluginReloader.cs` calls it on every unload with the list it already holds, so a callback declared in a helper library the plugin ships is dropped too; R59 finding 10. The match is on **assembly identity, not simple name**: load context + module MVID + name, all value data so nothing pins a collectible context. Two plugins that each vendor a `Shared.dll` used to cancel each other's pending callbacks, which silently lost exactly the read-modify-write this section tells you to put here; R60 finding 3), `ModSave.Uninstall` (Core uninstall drops every registration), and the dispatch itself. **All three work right up to the moment the callback runs**, the posted-but-not-yet-drained frame included (R59 finding 3): the registration used to be pulled from the list and its delegate exchanged away at *post* time, so for that frame all three droppers walked past a row that had already left and canceled a field the queued closure was holding in a local. The queue is a second dropper rather than a backstop for the first: when the callback has a target — a lambda or an instance method, nearly all of them — the queued work carries that object's assembly and the reloader's `DropOwnedBy` discards it, but a **static method group** has no target for Core to tag with, and the queue's retired-*context* check reads the posting closure's context, which is Core's. For that shape the sweep above is the mechanism. On the client the store is a no-op and the callback never runs. Worked example: `SampleStore.ArmLoadCounter` (`.store` reports the counter it maintains).

**Relocate.** When the resolved save folder changes, Core moves each store file to the new `progenitor\<modGuid>.json`. Destination-already-exists does **not** overwrite: old-file keys are merged as an extra base (`old < dest < dirty`), old-only keys are marked dirty so a later dest load cannot drop them, and the old file is renamed to a sidecar `{stem}.merged-{utc-timestamp}.json` beside the original (for example `progenitor.sample.merged-20260902T201530000Z.json`). Load skips `*.merged-*.json` so they are not registered as a second mod guid. A failed move that leaves dest missing **keeps the old path** so a later load cannot treat a missing dest as empty. If the sidecar rename fails, the original is left in place rather than silently orphaned.

**When.** Load on server `WorldReady`. Save is a Harmony postfix on `TriggerPersistenceSaveSystem.TriggerSave` (the one-shot entry: autosave and admin/manual; `OnUpdate` every frame is the wrong seam). A false `__result` means the game declined the save and Core does not write. Also a flush on `PlayerDisconnected`. Writes go to `.tmp` then move. `ModSave.SaveAll()` is the same flush (debug `save` op). Fallback path: BepInEx config `Persistence.Directory` when `ServerRuntimeSettings.SaveDirectoryPath` cannot be read.

### Server-level store (ServerSave)

`ctx.ServerSave` / `ServerSave.Store(guid)`: the same `IModStore` contract as `ctx.Save`, for data that belongs to the server rather than to one world ([where should my data go?](#where-should-my-data-go)). Built and game-free tested (`ServerSaveTests`), not live-verified.

| Member | What it does |
|---|---|
| `ctx.ServerSave` | This plugin's server store (`ServerSave.Store(ctx.Guid)`). Inside a subclass of `PluginContext` the property hides the static class; write `Progenitor.Persistence.ServerSave` there. |
| `ServerSave.Store(guid)` | The cached store for an id, from code with no context. Same id rules as `ModSave.Store` (no path; invalid file-name characters become `_`). |
| `ServerSave.SaveAll()` | Write every server store that changed. |
| `ServerSave.CurrentDirectory` | The `progenitor-server` folder, or null until known. Diagnostic only. |
| `ServerSave.RemoveAll(owner, owned)` | Drop a plugin's pending `WhenReady` callbacks; the reloader sweep calls it on every unload, as it does `ModSave.RemoveAll`. |

**Where.** `<persistentDataPath>\progenitor-server\<guid>.json`, beside the game's `Saves\` folder and never inside a world folder. The root is the server's `-persistentDataPath` argument, else the game's `StunApplication.persistentDataPath`, else the root above the world save folder `ModSave` resolved (`Saves\v<N>\<save>` or `CloudSaves\<user>\v<N>\<save>`). It is set once per Core generation; a later, different answer is logged and ignored rather than splitting the data.

**Wipes and world restores do not touch it.** The game's wipe moves the world folder to `<save>_<ticks>`; a restore replaces the world folder and backs up the old one under `progenitor-backups`. Neither reads or writes `progenitor-server`, so a server store keeps every key through both, including a restore to a world older than the data.

**When.** The root is normally known when Core installs, so the store is real from the first call and `WhenReady` runs in line. If it is not, the store is pending exactly like a world store (writes held, merged over disk when the root arrives; Core retries on `WorldReady`). Written on the same triggers as `ModSave` (`ModSave.SaveAll`, which the save trigger, a disconnect, Core unload and the debug `save` op call, flushes both) and on `ServerSave.SaveAll()`; only stores that changed are written, to `.tmp` and then moved over the file. Files written under a plugin's old id are renamed the same way world stores are ([renamed ids](hot-reload.md#renamed-ids-2026-10-deploy-bootstrap-and-restart-every-process)). A Core hot swap flushes on uninstall and the next generation reloads from disk; a plugin reload keeps the store, which belongs to Core, not to the plugin.

**Sides.** Server process only, hosted private games included (see the table above). A client process gets the no-op store.

### What happens to a save when a mod is missing

Research notes (internal; jobs RD3 + FSAVE + FSAVE2): the native `CopyDataFromPrefabs`, `SetupOldToNewEntityRemappingHashMap`, `ReinstantiateMegaStatics`, `Debug_ValidateAllEntityDataCopied`, `get_EntityCount`, `LoadReferredPrefabsOnDemand` and `TriggerSave`.

A world save records a mod prefab GUID exactly like a vanilla one — there is no "this prefab came from a mod" marker anywhere in the format. That gives two very different outcomes when the server is next started **without** the plugin that defined it:

| What the save holds | Vanilla outcome |
|---|---|
| The GUID only as **data** — an `InventoryBuffer.ItemType` slot, an `UnlockedRecipeElement`, a queued craft — | **Kept verbatim.** Nothing validates it at load, and `RepairBrokenInventoryItemsSystem` jumps straight to the next slot on an item-lookup miss. The slot comes back intact when the plugin returns with the same id. Progenitor does not touch these. |
| The GUID on a live **root entity** — a persisted buff, a spawned unit, a placed object — | **The whole load throws.** `LoadPersistenceSystemV2.CopyDataFromPrefabs` logs `Failed to load due to missing prefab {guid}` and throws; `OnUpdate` catches it into `PersistenceV2 - Load Persistence Failed` and `ServerStartupState.Failed`. The world is half loaded — **and the server keeps autosaving**, so the next autosave writes the half-loaded world over the good save (`PersistenceV2 - Saving game even though the server startup didn't fully succeed!`). There is no fallback prefab: `LoadReferredPrefabsOnDemand` is a `NotImplementedException` stub on the shipped server. |

**The guard.** Bootstrap owns a Harmony **prefix and postfix** on `LoadPersistenceSystemV2.CopyDataFromPrefabs(DeserializationJobData jobData)` — server process only, `Progenitor.Bootstrap.SaveSafety`. It reproduces the loader's own predicate (archetype carries `PrefabGUID`; entity has no `Unity.Transforms.Parent`; `PrefabLookupMap.TryGetConvertedPrefab` misses) and, when the blast radius is inside the caps below, removes each match from `DeserializedArchetype.NewEntities` **and** destroys it, so the load runs to completion and the world keeps everything else. Both halves are required: removing without destroying leaves an orphan, destroying without removing makes the loader read a dead entity and throw in the same place.

One guarded load runs in four stages, and the irreversible one is last:

1. **Plan**, mutating nothing — tally the unresolvable roots, then expand them through each root's `LinkedEntityGroup` and through the `Parent` chain, so a child of a dropped root is dropped with it and shows up in the counts instead of being left pointing at a dead parent.
2. **Validate** — the caps below. Over the cap, nothing is touched.
3. **Edit the lists** — every doomed entity is compacted out of `NewEntities` *and* `OldEntities` **in lockstep and in order**; a snapshot is taken first, so a throw partway through rolls the whole edit back and nothing has been destroyed.
4. **Destroy**, in the postfix, after `CopyDataFromPrefabs` has returned. If the loader throws instead, Harmony skips the postfix and nothing is destroyed — the vanilla failed load. The postfix first removes every `BuffBuffer` row on a deserialized entity that names a dropped entity, no entity at all, or an entity whose `PrefabGUID` is not the row's. It then retires each dropped entity: the entity is stripped of every component except `LinkedEntityGroup` and handed to the game's `DestroyUtility.Destroy`, with linked members going before their roots. A survivor is re-checked every ten seconds and destroyed directly only after six checks.

**Why the destroy goes through the game (2026-09-28 connect crash).** A bare `EntityManager.DestroyEntity` on a dropped buff left two references behind. The wearer's `BuffBuffer` row (`{ Entity @0x0; PrefabGUID @0x8 }`, persisted) kept pointing at the destroyed buff; the wearer was offline, so nothing read the row until that player connected, and then a Burst job walking the buffer threw `The entity does not exist` and aborted the server. The wearer also kept the buff's registered stat and flag modifications (the Sample buff's invulnerable and sun-immune bits), because vanilla clears a source's modifications only in `ModificationCleanupSystem`, which runs for entities carrying `DestroyTag`. The row repair also runs on loads that drop nothing, so a save autosaved after the old guard dropped a buff loads without the dangling row. A clean save has no such rows and is not edited. `SaveSafety.FailLoadInstead=true` reports the rows and removes none.

**Why order-preserving compaction and not `RemoveAtSwapBack`.** `OldEntities[i]` is paired with `NewEntities[i]` by ordinal, but only in `SetupOldToNewEntityRemappingHashMap`, which runs at `OnUpdate` step 10 — six steps *before* this seam at step 16. `CopyDataFromPrefabs` itself reads only `NewEntities` and `ComponentTypes` and uses the index as a cursor, so a reorder here cannot rebind anything on this build. Compacting in order costs the same and removes the question permanently.

**Why the destroy is deferred.** `EntityManager.DestroyEntity` is a structural change. The loader makes its own in this window — `ReinstantiateMegaStatics` (step 12) calls `EntityManager.Instantiate` and `AddComponent` — so it is legal here; the guard still keeps its only structural change until after the loader has finished reading, which also means a mid-edit failure can always fall back with nothing lost.

It lives in Bootstrap, not Core, because it has to work in a process where **no Core and no plugin is loaded at all** — which is exactly the operator who stopped the server and deleted a DLL. It is not a Core Harmony slot, it is not in `HarmonySlotPolicy.AllSites`, and `Diagnostics.SkipCoreHarmony` does not empty it.

**The fail-safe rule.** Vanilla's outcome for an unresolvable prefab is a **failed load** — never a silent loss. The save on disk is untouched until the next autosave, and the operator sees it. The guard is therefore only allowed to replace that outcome when the evidence says "a mod is missing", and it falls back to the vanilla failure whenever it does not:

- the prefab lookup must be **populated** — `PrefabLookupMap.Count()` at or above `SaveSafety.MinPrefabLookupCount` (default 5000; a real V Rising collection holds tens of thousands);
- the lookup must **resolve a known vanilla GUID** (`Item_Ingredient_Crystal` and two vanilla equip-buff bases) — a populated map that cannot is not trusted;
- the total to drop, **children included**, must be at or under `SaveSafety.MaxDropEntities` (default 5000) and at or under `SaveSafety.MaxDropPercent` of the entities scanned (default 0.5 %).

Any refusal logs and then lets the vanilla throw happen:

```
SaveSafety: refusing to drop 39000 of 40000 entities (over SaveSafety.MaxDropPercent=0.5% of the entities scanned); falling back to the vanilla load failure
```

Without those caps an unpopulated lookup would miss on *every* root, the guard would delete the world, the load would "succeed", and the next autosave would make it permanent. That is why `DropUnknownPrefabs` may default to `true` only alongside them.

**It is lossy by construction.** The buff, unit, or placed object is gone. That is the correct trade against "the save does not load and the next autosave overwrites it", but it is logged loudly, once per GUID with a count, plus the plan, the destroy result, a summary, and a warning that names the save:

```
SaveSafety: dropped 2 entity(ies) with unknown prefab guid 650563057 (mod missing?) so the load can continue
SaveSafety: mode=Drop; removing 3 entity(ies) (1 child(ren)) across 1 unknown prefab guid(s) out of 41000 scanned from DeserializedArchetype.NewEntities and OldEntities in lockstep, order preserved; they are destroyed after CopyDataFromPrefabs returns.
SaveSafety: BuffBuffer check removed 1 row(s) naming a dropped entity, 0 row(s) naming no entity and 0 row(s) naming an entity of another prefab (dropped 650563057x1) on 1 of 5210 buff holder(s) (38 ms).
SaveSafety: retired 3 of 3 planned entity(ies) after CopyDataFromPrefabs returned: stripped to no components and handed to DestroyUtility.Destroy, so the game's ModificationCleanupSystem clears the stat/flag modifications they were the source of.
SaveSafety: load guard removed 3 entity(ies) (1 of them children of a dropped root) across 1 unknown prefab guid(s) out of 41000 entity(ies) scanned in 812 ms; the rest of the save loaded normally.
SaveSafety: WARNING — save 'test1' lost 3 entity(ies) across 1 unknown prefab guid(s), and the NEXT AUTOSAVE WILL NOT CONTAIN THEM. …
```

When the delayed check finds them all gone: `SaveSafety: 3 of 3 retired entity(ies) were destroyed by the game.` A clean load logs `SaveSafety: load guard scanned N entity(ies) with a prefab guid in T ms; every prefab guid resolved, nothing dropped.` and `SaveSafety: BuffBuffer check: N deserialized buff holder(s), every row names a live entity (T ms).` The save name comes from Bootstrap's `ServerWorldManager.BeginCreateServerWorld` postfix (`LoadPersistenceSystemV2` has no readable save-path field). A dropped GUID that is in Bootstrap's own `ContentMemory` clone set is called out separately — that is a plugin that failed to apply before the load, not a missing mod.

**The switches.** Five entries in the shared `progenitor.cfg`, bound by Bootstrap so they exist with no Core, and all read live per load:

| Key | Default | Effect |
|---|---|---|
| `SaveSafety.DropUnknownPrefabs` | `true` | Drop and destroy unresolvable root entities (and their children) so the load completes, **within the caps below**. |
| `SaveSafety.FailLoadInstead` | `false` | Report the unknown GUIDs and let the vanilla load abort. **Overrides** `DropUnknownPrefabs`, so an operator who prefers the vanilla failure only has to set this one. Stop the server before the next autosave. |
| `SaveSafety.MaxDropPercent` | `0.5` | Ceiling on one load's drop as a percentage of the entities scanned. Over it: drop nothing, let the vanilla load fail. `0` disables dropping. |
| `SaveSafety.MaxDropEntities` | `5000` | Absolute ceiling on one load's drop, children included. Over it: drop nothing, let the vanilla load fail. `0` disables dropping. |
| `SaveSafety.MinPrefabLookupCount` | `5000` | Minimum `PrefabLookupMap.Count()` before a lookup miss is believed at all. Clamped to at least `1`, so an empty map always refuses. |

Setting `DropUnknownPrefabs=false` with `FailLoadInstead=false` turns the guard off entirely (no scan, vanilla behavior, including the autosave-over-good-save failure mode).

**Belt and braces.** A plugin should still remove its own live instances when it is really being removed — the debug buff's `DebugBuff.Reset(reason)` sweep (formerly the Sample's `OnUnload`), `PluginUnloadReason.Removal` only (`DestroyUtility.Destroy(..., TryRemoveBuff)`; a Core swap keeps them), is the pattern, and it keeps a *graceful* unload out of the save in the first place. Core adds nothing on top of that: a graceful unload is not the case this guard exists for.

## Config

`Progenitor.Config.ConfigHelper`. Typed bind plus a disk watch: BepInEx only reloads when asked, so an edit to the `.cfg` while the process is up would otherwise wait until restart.

```csharp
var enabled = ConfigHelper.Bind(ctx.Config.File, "General", "Enabled", true,
    "Turn the feature off without unloading the plugin.");
ConfigHelper.Reloaded += file => ctx.Log.LogInfo("reloaded " + file.ConfigFilePath);
```

**`[Install] CurrentUpdateTag`** (shared `progenitor.cfg`, string, default empty; `ConfigHelper.BindCurrentUpdateTag`, bound on first use by the Install tab): the Thunderstore update tag treated as the current game update. Empty defers to the V Rising game-build adapter; the literal `unknown` forces the unknown state (nothing hidden, no stale warnings). Resolution and readback are described under the Install search filters in [mods-menu.md](mods-menu.md).

`Bind<T>(ConfigFile, section, key, default, description)` is `ConfigFile.Bind` plus `Watch`. `Watch(file)` returns `IDisposable`; a second watch on the same path shares the first handle. Reloads are debounced 1 s and marshaled onto the main thread (`ConfigFile.Reload` mutates live `ConfigEntry<T>` values). `Reloaded` fires after that, on the main thread.

**One edit, one reload: the watch ignores the write-back its own reload caused (FCFG8).** A reload writes the file. That is not our code and not a guess — `BepInEx.Core.dll` analyzes to `ConfigFile.Reload()` handing every line it recognizes to `ConfigEntryBase.SetSerializedValue`, which sets `ConfigEntry<T>.BoxedValue` and so `Value`, whose setter raises `ConfigFile.OnSettingChanged`, which calls `Save()` while `SaveOnConfigSet` is true (its default, and the default nothing in Core turns off outside a `ConfigSync` lock). So the file changed on disk *during* the reload, the `FileSystemWatcher` saw it, and one hand edit of a plugin `.cfg` produced two `ConfigHelper: reloaded` lines and two `Reloaded` callbacks exactly one debounce apart, the second reporting every value unchanged (evidence 207). It stopped at two only because `Value`'s setter is equality-checked: the second reload assigned the same values, saved nothing, and the third event never came. A plugin counting reloads, re-reading a remote, or rebuilding UI on `Reloaded` did all of it twice for every edit.

The filter is the file's **bytes**. After each reload `WatchState` records a SHA-256 of what is then on disk — which is precisely BepInEx's write-back — and a later event whose bytes still hash to that value is dropped with one `ConfigHelper: ignoring the write-back of <path>` line (once per recorded write-back, not once per OS notification: an editor's temp-file rename delivers several). Length plus timestamp would not do: BepInEx's write-back is often the same length, in the same second, as the edit that caused it. The hash is recorded **before** `Reloaded` fires, not after, so an edit that lands while a subscriber is still working hashes differently and still fires — hashing afterwards would swallow it for good. A file that cannot be read (missing, locked, mid-rename) hashes to null, which never matches: the watch fails open, because a spurious reload is cheap and a lost edit is not. The 1 s debounce and main-thread delivery are unchanged.

**The filter is armed only by a write that happened, and it expires (FCFG9, R64 #1; FCFG10, R65 #1 and #6).** "Our own write-back" is a claim with two bounds in it, and the first cut had neither. **`SaveOnConfigSet` is not always true, and Core is what turns it off:** `ConfigSync` holds it `false` for the *whole* `ConfigFile` while any synced slot on it is locked (it is a property of the file; BepInEx offers nothing narrower). On such a file a reload writes nothing, so the bytes recorded after it are the **operator's own**, and the filter degenerates into "ignore every future event whose content equals what was loaded" — an operator whose value has since been overridden by the server saves the same file again to force it, and gets silence plus a log line about a write-back nobody made. So the hash is armed only when the reload could have written and the bytes actually changed across `Reload()`; a reload that wrote nothing records nothing. **"The bytes changed across `Reload()`" is not by itself "the reload wrote" (FCFG10, R65 #1).** That span includes every `SettingChanged` subscriber the reload runs synchronously, so it covers other people's writes too — and on a save-off file it covers *only* theirs, because our side cannot write there at all. Inferring the arm from the bytes therefore turned the one case the bound was written for inside out: an operator's save landing while the reload was running was recorded as our own echo, and the notification for it — the first delivery of that edit — was dropped, with the log claiming a write-back nobody made. The flag itself is the signal: `SaveOnConfigSet` is read off the file immediately **before** `Reload()` (a subscriber inside it could flip it, and the question is what *this* reload was allowed to do), and an unreadable flag counts as "wrote nothing", which is the fail-open direction. The changed-bytes test stays as the second half: a save-on reload that assigned nothing saved nothing. **And a write-back's notification cannot arrive late:** BepInEx writes the file inside `Reload()`, synchronously, and the window is measured from that moment — the arm's timestamp is taken as soon as `Reload()` returns, not when some later notification is processed — so anything matching more than `ConfigHelper.SelfWriteWindowSeconds` after that write is past the point where it is still treatable as our echo, and reloads. **The window is three debounce intervals, not two (FCFG10, R65 #6).** The debounce is *restartable*: every notification posts a fresh `MainThread.PostDelayed(ConfigHelper.DebounceSeconds, …)` and only the newest callback survives, so the arm written in one debounced reload cannot be read before arm + one interval. Two intervals of window left one of slack, and any further notification inside that second spent it. The window is now expressed as `DebounceSeconds * 3` (**3 s**) so the two constants cannot drift, leaving two intervals for restarts. That *bounds* the interaction rather than removing it — a notification stream faster than the debounce can still push the read past the window — and the bounded cost is one extra reload of bytes we already hold. **The expiry line says only what is known.** It used to read "was saved with the bytes our last reload wrote", which asserts a deliberate save, in the one case the slack makes reachable: our own echo delayed by our own debounce, where no operator saved anything and the line sends them looking for an editor that does not exist. It now reads `ConfigHelper: <path> holds the bytes our last reload wrote and the write-back window has passed; reloading it`. The deliberate restores it was written about — Ctrl+Z and save, a `git checkout` of a config, an editor rewriting what it read — reload exactly as before. The window is what still lets the several notifications one temp-file rename delivers for a single write be recognized as the one write they are. A clock that steps backwards reads as fresh rather than expired, because the alternative is a reload loop.

**The watch reloads the ConfigFile a path *has*, not the one it was created with (FCFG11).** A watch is keyed by path, and the `ConfigFile` behind a path is not stable: BepInEx builds a **new** one in every `BasePlugin` constructor — `Config = new ConfigFile(Paths.ConfigPath/GUID.cfg, saveOnInit: false, metadata)`, read out of `BepInEx.Unity.IL2CPP.dll` — so every plugin hot reload inside one Core generation hands the plugin a different object for the same file. The watch used to hold the instance captured at the first `Bind`/`Watch`, which after one reload is the unloaded instance's. Evidence 222 is what that looks like live: the rig's `config-watch` case kept failing because every watch reload printed the *old* instance's values as unchanged while the disk held the operator's edit, and the Sample's own `ctx.Config.File.Reload()` read the file correctly. Earlier hand checks passed only because each ran on the first plugin instance of its Core generation.

Reloading an orphaned instance is worse than useless: `Reload()` assigns through `ConfigEntry<T>.Value`, whose setter saves the whole file, so the dead instance writes **its** stale values back over the operator's edit — FCFG2's silent revert by another route — while the live instance never learns of it. `ConfigHelper` therefore keeps, per path, the set of `ConfigFile` instances to reload, and a reload reloads **every** registered one and raises `Reloaded` **once**, whatever number of instances that path has (R69 #4, below). A path with no live instance reloads nothing and says so once per empty spell (`no live ConfigFile is registered for it`) rather than reviving the last dead one.

**Every registered instance is reloaded, and none is evicted for being older (FCFG12, R68 #8).** FCFG11 first wrote the opposite rule beside that loop: a new, distinct instance **superseded** every earlier one at registration, justified as a construction guarantee — BepInEx has one live `ConfigFile` per path, so an earlier object is by construction a dead plugin instance's. That is true of every caller *in this repository* (`PluginContext` uses `plugin.Config`, Bootstrap constructs no `ConfigFile`, `ConfigSync` reuses `entry.ConfigFile`) and it is not true of the public API: `ConfigHelper.Bind(ConfigFile, …)` and `ConfigHelper.Watch(ConfigFile)` take any file, so two plugins of one suite that both bind into a shared `MySuite.cfg` — each holding its own `new ConfigFile(path, true)` — are two live owners of one path. Supersede evicted the first of them from the watch permanently while it stayed live with `SaveOnConfigSet = true`, so its next `Save` wrote its now-stale values over the operator's edit: FCFG2's silent revert by a third route, produced by the rule written to close the second. Nothing available at registration time can tell that case from a hot reload — an unloaded plugin's `ConfigFile` is still a live object until it is collected — so the rule is the one that is right for both: reload them all. Reloading is what makes an instance *agree with the disk*, so an undropped dead instance is not merely ignored, the stale write-back it was carrying is disarmed. A path that gains a second instance says so once, naming both objects (identity hash and key count) and both explanations, because only the plugin author can tell an undropped hot reload from two live owners: `<path> now has 2 registered ConfigFile instances […]`.

Instances leave that set two ways, and neither is age. `ConfigHelper.DropWatchedFile(file)` is the explicit drop — it stops the pointless reload (and the `SettingChanged` handlers that unloaded instance still carries) for as long as something keeps the object alive, and it drops the reload registration only, never the `FileSystemWatcher` lease. And collection: the references are weak, so this registry can never be what keeps an unloaded plugin's `ConfigFile`, entries and handlers alive, and a plugin that crashed on unload and called nothing stops being reloaded the moment nothing else holds its object.

**A plugin no longer has to make that call (FCFG13).** FCFG12 left the explicit drop entirely to the plugin author, which meant an instance was dropped promptly only when somebody had remembered to write the line — and a plugin that had not was reloaded, with its `SettingChanged` handlers, on every watch event until the GC happened to take it. Core makes the call now, on the object it handed the plugin in the first place: `PluginContext.Attach` tracks `ConfigHelper.DropWatchedFileOnUnload(ctx.Config.File)`, so the registration goes when the context is torn down — every unload reason, and a failed `OnLoad` too, since that path runs the same `UndoAll`. `PluginReloader.SweepRegistrations` repeats the drop for the two unloads `UndoAll` never sees: a raw `BasePlugin` (no context at all) and a `Unload` that threw before reaching it. The drop is idempotent, so the second of the two finds nothing and returns `false` — which is also what makes the Sample's own `SampleConfig.Detach` call harmless: it runs from `OnUnload`, ahead of `UndoAll`, and is kept as the example of an API that is still the way to drop a `ConfigFile` Core does not know about (one a plugin constructed itself, a shared suite file) or to drop one before unload.

**A reload reads the file and does not write it (FCFG14, R69 #3).** Reloading every instance was not enough on its own, because of a second asymmetry in BepInEx: `Reload()` assigns only the keys the file **still names** — a key it does not find is left at its in-memory value — while `Save()` writes **every** entry plus the orphans (both read out of `BepInEx.Core.dll`). Instances are reloaded oldest registration first, and the oldest is normally the dead plugin generation's — so on the edit an operator makes when they want a default back (change one key, **delete** the line of another), the dead instance reloaded first, its assignment of the changed key saved the whole file, and the deleted key was rewritten at the dead generation's value *before the live instance had read a byte*. The live instance then loaded that. FCFG2's silent revert by a fourth route, produced — as FCFG12 was, of FCFG11 — by the rule written to close the third. The fix is to stop the reload writing at all: `SaveOnConfigSet` is held off on every registered instance for the whole loop and restored afterwards, the maneuver `ConfigSync` already owns for its locks (`HoldSaveOff`). The operator's bytes stay on disk exactly as they were typed, no instance can re-inject a key they deleted, and N instances cost N reads instead of N reads and N rewrites. The restore puts back **what it found, while it is still off**: a file `ConfigSync` is holding save-off stays save-off, and a subscriber that switched saving back on inside the reload keeps it on. The hold is best effort — it is a write to somebody else's object, and the flag is read **back** after it rather than assumed — so the self-write filter above stays for the one instance it could not hold, which is now the only thing that can arm it; an instance whose flag would not take is named in a warning.

**`Reloaded` is one fact per reload, not one per instance (FCFG14, R69 #4).** FCFG12 raised it once per registered instance, on the argument that a subscriber which cares reads the `ConfigFile` it is handed. Nothing on a `ConfigFile` tells two instances of one path apart: `ConfigFilePath` is the same string by construction, and it is the only thing the one subscriber in the repository (the Sample's `OnReloaded`) reads off it. Per-instance dispatch was therefore N copies of one fact — on the Sample, two `Sample config: reload #N` lines for one hand edit, the second reporting both keys unchanged because the first had already moved its before-values, which is exactly the line the verification rig's `config-watch` case counts. The event means "this path now agrees with the disk", which happens once; it is handed the **most recently registered** live instance, for a subscriber that holds no `ConfigFile` of its own, and every other registered instance has already been reloaded when it fires. A handler still filters on `ConfigFilePath`, because the event is shared by every watched file.

**A reload that loaded nothing is not a reload.** `Reloaded` is raised, and `ConfigHelper: reloaded <path>` is logged, only when at least one registered instance's `Reload()` returned. The argument is the newest instance that loaded, never one that threw. A partial reload logs `reloaded <path> into N of M registered ConfigFile instances`. When every instance threw (a converter that throws, or a file held by an editor's exclusive save or atomic rename), the entries still hold their pre-edit values, so Core raises nothing and logs `ConfigHelper: no registered ConfigFile for '<path>' could be reloaded; its entries still hold their pre-edit values and Reloaded was not raised`. It then retries once, one debounce interval later. The budget is one retry per file notification: a new save refills it, and the retry itself does not, so a file that stays broken is not reloaded in a loop. A later successful reload also refills it. Offline-tested in `ConfigHelperTests`; not live-verified.

**Shared file (`progenitor.cfg`).** That file is owned by Bootstrap's chainloader plugin and lives for the process. `ConfigEntry<T>` of a Core or plugin type (a Core enum was enough) pins the collectible generation — evidence 65 named `ConfigEntry<HostedServerPluginsMode>` plus its `SettingChanged` handler. Core therefore binds on that file only through `ConfigHelper`, which refuses any `T` that is not a BCL primitive, `string`, or BCL enum (throws `ArgumentException` at bind time). Core enums use `BindEnum<TEnum>` (same section/key, value stored as the enum name so existing cfg files still load) and `ReadEnum` (bad values log a warning and use the default). `Diagnostics.SkipCoreModules` including `Config` makes `Bind` throw `InvalidOperationException` naming the module.

**How Core reaches the shared file.** Core installs from inside Bootstrap's `Load`. At that point `IL2CPPChainloader.Plugins` already lists Bootstrap, but its `Instance` is still null, so a chainloader lookup finds nothing at process start. Core modules therefore get the file from `Config.SharedConfig` and never look it up themselves. `SharedConfig` tries Bootstrap's handover (`ProcessHost.SharedConfig`) first, then the chainloader lookup, which works once the chainloader has finished. A section bound at install uses `SharedConfig.BindWhenAvailable(section, bind)`. When neither source has the file (a Bootstrap from before the handover), that call logs `Config: [Section] not bound yet: …`, keeps the section's defaults and binds later on the main thread. A late bind logs `Config: [Section] bound late from progenitor.cfg (…)`. The section is retried once a second for about a minute. A watched reload of the shared file also retries it, but only when another bind has already made `ConfigHelper` watch that file. If the file is still missing after that, Core logs `Config: [Section] could not be bound: …` once per section.

`UninstallAll` (called from Core Uninstall before the other modules, and idempotent) unsubscribes every `SettingChanged` handler Core added and `ConfigFile.Remove`s every definition Core bound on the shared file. BepInEx `Remove` only drops the in-memory `Entries` row — it does not copy the value into `OrphanedEntries` and does not write the cfg — so Core re-adds the serialized value as an orphan. A later `Save` or the next generation's `Bind` therefore keeps the on-disk value.

**One key, one tracked bind, and no empty orphan (FCFG2).** A key bound twice in one generation is **one** tracked bind: `Bind` skips the record when `(file, definition)` is already tracked, and `EvictToOrphan` parks a value only when it actually read one off a live entry. Both halves are needed, and the second is the load-bearing one — the rule is that eviction must be idempotent, because a module can legitimately be installed from two places. `ModManifest.Install` is: `Handshake.Install` calls it, and so does the first-frame kick, and both called `BindTransferLimits` (today `BindDenyDescriptorSwitch`: the four `ModTransfer*` keys went with server-sent mod files on 2026-10-05). That put two tracked records on each of the five `Networking.ModTransfer*` / `DenyTimeModDescriptor` keys, so `UninstallAll` evicted each one twice: pass 1 parked the operator's value as a BepInEx orphan, pass 2 found the entry already removed, computed `serialized = ""` and wrote the empty string over it. The next generation's `Bind` then consumed `""`, BepInEx logged `could not be parsed … Value: ` with a blank value, the entry fell back to its default — and because `SaveOnConfigSet` Saves inside `Bind`, that default was written straight back to the cfg. An operator's transfer cap and their `DenyTimeModDescriptor = false` privacy choice were silently reverted on every Core swap. **An empty serialized value must never reach the orphan table.** That rule now lives in `TryAddOrphan` rather than in each caller (FCFG3, R49 #3), so both park paths get it: an empty string never overwrites a parked value, and `EvictToOrphan` parks only when a serialization attempt actually succeeded (not merely when the entry was there to ask). **"Not read" is a signal, never the shape of the string (FCFG4, R50 #3).** FCFG3 also had `TryParkValueForSave` — the path that holds an operator's local value aside while a locked entry is Saved — refuse an empty string outright, on the premise that its caller's `slot.LocalToml ?? ""` is empty only when `ReadToml` threw. `ReadToml` is total (it catches around `GetSerializedValue`, falls through to `ConfigSyncToml.Format`, catches again, and ends at `BoxedValue?.ToString() ?? ""`), and `GetSerializedValue()` answers `""` as the normal, successful result for a `string` entry the operator has cleared — so the guard fired on the one case it most had to protect: clear a synced `string` (a MOTD, a chat prefix), let the server push its own text, and the refused park left the live row holding the server value for the next `Save` to write into the cfg. `ConfigSync.Slot` now carries `LocalTomlRead` (set beside every `ReadToml`, carried across a session lock) and passes it to `TryParkValueForSave`, which parks an empty value that was read and refuses only one that was not; `TryAddOrphan` takes `emptyIsAValue:` for that one caller. **A caller that is about to drop the live row honors the park's answer (FCFG4, R50 #7).** `EvictToOrphan` parked after removing the row and discarded `TryAddOrphan`'s bool, so an unreachable `OrphanedEntries` left the value in neither table and the next `Save` wrote the freshly bound default — FCFG2's revert by another route. It now parks first and keeps the live row when the park fails; nothing read needs the row gone first, and a read that produced nothing still evicts, because there is then nothing to lose. **Whether a failed park is a reason to keep the row is the caller's answer, not the function's (FCFG5, R52 #6).** FCFG4 gave `EvictToOrphan` one answer for both callers, and it is the wrong one for the enum-as-string upgrade: that eviction exists *only* so the `Bind<T>` on the next line does not meet a row of another `SettingType`, and BepInEx's `Bind` returns the **existing** entry for a definition it already has — so on a BepInEx whose `OrphanedEntries` cannot be reached, keeping the row turned a silently reverted value into an `InvalidCastException` thrown out of `ConfigHelper.Bind` (`EvictMismatchedEntry`'s own try/catch wraps the eviction, which returns normally). `EvictToOrphan` now takes `mustEvict:`; `UninstallAll` passes false and keeps the row, the mismatch path passes true, logs, and drops it anyway, because a row `Bind` will throw on is worse than one lost value. **The park's inverse writes back before it unparks (FCFG5, R52 #4).** `ConfigSync.RestoreSlot` had the ordering FCFG4 had just fixed one function away: it called `TryRestoreParkedEntry` first — putting the live row, still holding the *server's* value, back into `Entries` and deleting the orphan holding the local — and then threw away the write-back's own boolean and set `ok = true` on the next line. A write-back that did nothing (a `TrySetSerialized` that logs and continues, a `BoxedValue` setter that throws) therefore left the operator's value in neither table, and the `Save` that `RestoreLocal` runs next wrote the server's value into their `.cfg` — on every disconnect, new client world and Core uninstall. The restore now writes, re-reads the entry to confirm the value took, and only then unparks; a failure keeps both the park and the lock so the next restore retries, and returns false so the file is not saved. A slot with nothing to restore at all (`LocalTomlRead == false` **and** `LocalBoxed == null`, both captures having thrown) is not restored: it only releases the lock, because unparking could then only drop an orphan it never wrote. `LocalTomlRead` has two consumers now; FCFG4 gave it one where it needed two. **The retry ends, and the record is honored before it is dropped (FCFG6, R53 #4).** Keeping the lock on a failed write-back was right; keeping it forever was not. **The save hold is per `ConfigFile`, not per entry** — `SaveOnConfigSet` is a property of the file, BepInEx offers nothing narrower, and `ReleaseSaveOff` is a *query* over every slot on that file rather than a refcount — so one stuck slot switches autosave off for **every** entry in that `.cfg`, including entries no server ever touched. Because the blast radius is the whole file, the give-up is short — and it is measured in **seconds**, not in restores. **A budget is only a budget if something spends it (FCFG7, R54 #2).** FCFG6 counted three failed restores of one key, and `RestoreLocal` has exactly two production callers: `OnWorldReady` on a client world, and `Uninstall`. There is no frame hook in this module, so the unit was really *“the client built a new world”* — a leave-to-menu — and a persistent failure spent one attempt per leave; since BepInEx does not call `Plugin.Unload` on a normal quit, `Uninstall` never ran either, and the per-file autosave freeze outlived the session anyway. The retry now posts itself: a failed restore schedules the next attempt through `MainThread.PostDelayed` (`ConfigSync.RestoreRetryIntervalSeconds`, **1 s**, one post outstanding per key), and gives up once `ConfigSync.RestoreRetryBudgetSeconds` (**5 s**) have passed since that key's *first* failure — about six attempts, with `MaxRestoreAttempts` (**8**) standing behind the clock in case the clock stands still. The give-up itself is unchanged: an error line naming the key, the value and the reason, the operator's value re-parked as an orphan so it is still what a `Save` writes to disk, and the lock and the file's hold released. The *first* failure inside `Uninstall` still gives up immediately — it has no next attempt — and `Uninstall` drops every outstanding retry post (they are owned by a Core token, dropped with `MainThread.DropOwnedBy`) before running that final pass. **The park's `localValueWasRead` argument is reported, never asserted (FCFG7, R54 #8).** The give-up hard-coded `true`, and that flag is what lets `TryParkValueForSave` write an empty string over a good orphan; it has a branch where no local Toml was ever read (only a boxed capture), and on that branch there is no operator value to park at all. It passes `slot.LocalTomlRead` now, and when that is false it writes nothing and says so. A reverted setting is a smaller failure than a config file that has silently stopped saving; it is the same trade `EvictToOrphan(mustEvict: true)` makes. And `Uninstall` no longer clears `SaveHolds` silently: it runs its last restore with `finalAttempt: true`, then puts back by hand — with a loud line each — any hold still standing. `SaveHolds` is a Core static that dies with the collectible generation while the `ConfigFile` is BepInEx's and does not, so clearing the record without honoring it left `SaveOnConfigSet = false` on that file for the life of the process, with the next generation's `HoldSaveOff` recording that `false` as the value to go back to. **A Core static must never be the only record of a change made to a process-lifetime object.**

**Bounds on a synced entry (FCFG).** `ctx.Bind` takes `acceptableValues:` (an `AcceptableValueRange` / `AcceptableValueList`) or, through the other overload, a whole `ConfigDescription` — on a synced entry as well as a local one, so a plugin no longer has to reach past `ctx` to `ctx.Config.File.Bind` to get a range. BepInEx writes the set into the cfg comment and the settings panel prints it on the row (synced rows stay read-only and say `(set by the server)`; they show their bounds like any other row).

Bounds are **local metadata and never on the wire**: the sync record still carries the value alone. So a server that binds `1..50` and a client that binds `1..10` disagree, and a v2 server can push a value the client's own bind refuses. BepInEx would take that silently — `ConfigEntry.Value` clamps rather than throwing, and `AcceptableValueList.Clamp` answers element 0 for anything invalid, not the nearest — leaving the client on a value nobody chose with nothing in the log. `Progenitor.Config.SyncedBounds.Check` (duck-typed off `Description.AcceptableValues` for the same reason `Ui.SettingsBounds` is: the unit-test shim of `BepInEx.Configuration` has no `ConfigDescription`) is therefore the **one gate every path that applies a server value goes through**, always against the entry it is about to write:

- `ApplyOne`, the ordinary push and snapshot path;
- `ApplyToSessionLock`, the push that lands while the plugin is unbound (a hot reload in flight);
- `ReattachSession`, which writes the locked value into the newly bound entry — and re-asks, because the reload may have **narrowed** the bounds, so the session lock's own answer is not the new entry's answer.

Each of the three records what the gate returned, so the lock's write comparison, the save park and the disconnect restore never disagree with what the entry holds (R40 #7 was the session-lock pair recording `'50'` while the entry held `10`, which made `TryRefuseWrite` refuse even a write of the value the entry really had).

**Range shape.** An out-of-range value is **clamped to the local bounds and applied clamped**, never out of range, with one warning naming both halves — `ConfigSync: server value '50' for example.mod/Sample/SpawnMultiplier is outside this client's bounds [1 to 10]; clamped to '10'`.

**List shape (R40 #8).** `Clamp` is never called: a list is an unordered set, so element 0 is not a "nearest" — for `{Low, Med, High}` and a pushed `Extreme` it is the *quietest* setting, the opposite end from what the server asked for. A value outside the list is **refused**: nothing is written, the local value stays, the entry does not lock, and the line names the allowed set — `… is outside this client's bounds [one of: Low, Med, High]; keeping the local value`. The one exception is an entry whose own declared `DefaultValue` is in the list; that default is applied instead, logged as `clamped to this entry's default 'Med'`. A re-attach that is refused this way leaves the entry unlocked on the value `Bind` produced, with `not re-attaching … the locked server value is outside the new bind's bounds`.

An entry with no bounds, bounds that cannot be read or questioned, or an incoming value that does not parse is applied exactly as before. Range-shaped bounds that refuse the value but offer no usable `Clamp` and no readable ends are refused rather than applied, for the same reason as the list: applying it would hand the decision back to BepInEx's silent clamp.

`PluginContext.Config.Bind` does **not** start a watch — it is only a thin bind over this plugin's `GUID.cfg`. It refuses the shared Progenitor file (that bind belongs on `ConfigHelper`). Call `ConfigHelper.Watch(ctx.Config.File)` (or bind through `ConfigHelper`) if you want live reloads; you do **not** have to drop it again, because Core drops `ctx.Config.File` from the reload registry when your context is torn down (FCFG13, above). `ConfigHelper.DropWatchedFile(file)` is still there for a `ConfigFile` that is not `ctx.Config.File` and for dropping one before unload. `UninstallAll` drops every watch; plugin-file entries stay valid.

## Localization

`Progenitor.Localization.Localization`. Owns strings the game looks up by `LocalizationKey` (item names, tooltips). Vanilla keys are Unity asset GUIDs; a mod string has no asset, so the GUID is MD5 of the UTF-8 key packed into `AssetGuid`'s four ints — stable across launches.

A `ProgenitorPlugin` loads files automatically before `OnLoad` (`Localization.LoadForPlugin` on the plugin assembly, owner = the plugin instance). Drop `en.json` / `de.json` next to the DLL; no extra call is required. `LoadForPlugin` probes `<dir>/<AssemblyName>/localization` then `<dir>/localization` and loads the first that exists (`dir` is `Assembly.Location`'s folder, or `PluginReloader.TryGetSourcePath` when the assembly was loaded from a byte stream). It logs the chosen folder at info and logs nothing when neither exists, so a plugin without translations is not a warning. `deploy.ps1` copies a plugin's `localization` folder to `<reload>/<AssemblyName>/localization` so that first probe finds it after a hot reload.

```csharp
ctx.Track(Localization.Register("sample.ember", "Ember Shard", language: "English", owner: ctx.Owner));
ctx.Track(Localization.Register("sample.ember.desc", "A shard that still holds a little heat.", owner: ctx.Owner));
var key = Localization.KeyFor("sample.ember");
if (Localization.TryGetGameString(key, out var text)) ctx.Log.LogInfo(text);
Localization.ApplyName(managedItemData, "sample.ember");
Localization.ApplyDescription(managedItemData, "sample.ember.desc");
Localization.RemoveAll(ctx.Owner);
// or, for a translator-editable folder next to the DLL:
ctx.Track(Localization.LoadDirectory(path, ctx.Owner));
```

`Register(key, text, language = "English", owner = null)` → `IDisposable`. On the client, if that language is loaded, the string is written into `Stunlock.Localization.Localization.LocalizedStrings`. The server has no string table, so `Register` only records. Language changes wipe the game table; a Harmony postfix on `SetLanguage` writes the matching text (or English fallback) back in. Last registration for a key+language wins until disposed — except identical text twice (a plugin's `en.json` plus `Named()` on the same id) is **not** a conflict.

`LoadDirectory(dir, owner)` reads every `*.json` in that folder. The file stem is the language (`en.json`, `de.json`); `LocalizationParser.CanonicalLanguage` maps ISO codes onto the Steam names the game uses (`en` → `English`, `de` → `German`, `ko` → `Koreana`, …) so an English client actually sees `en` strings. Unknown tokens are kept as-is. Missing directories are a no-op; malformed files are logged and skipped. One disposable reverses the whole directory.

**English fallback.** If the player's language has no registration for a key, the last English / `en` text is used (`en.json` fills gaps). That keeps a cloned item from showing a missing-key blank when the player is not on a language the mod shipped.

`TryGetGameString` is a true view of what UI will see; always false on the server. `ApplyName` only writes `ManagedItemData.Name`; the string must already be registered. `PrefabRegistry.Named` does this on the client when applying a clone.

`ApplyDescription` writes `ManagedItemData.Description.Key` and clears inherited format parameters (a mod description is a plain string, not a templated vanilla tooltip). The key is `<id>.desc`. `PrefabRegistry.Described` registers that key on the client when applying a clone. If `Described()` was omitted but Localization already has `<id>.desc` — typically from a plugin `en.json` such as `"sample.ember.desc": "A shard that still holds a little heat."` — the applicator still points the clone at that key and does not `Register` again. `IsRegistered(key)` is true on both sides as long as `Register` ran (it does not consult the game table), which is how the file-based fallback is detected.

`AssetGuid` is four ints with no reference fields, so the dictionary indexer is safe (the `PrefabLookupKey` marshal trap does not apply).

## ECS toolkit

`Progenitor.Ecs`. Crash-safe access that avoids IL2CPP generic instantiations over game value types.

**Rule:** do not call `EntityQuery.GetSingleton<T>`, `NativeList<T>.Add`, `NativeHashMap<K,V>.Add`, `TypeManager.GetTypeIndex<T>()`, `HasComponent<T>`, `GetBuffer<T>`, or `ComponentType.ReadOnly<T>()` for game structs unless that instantiation is known to exist. A missing instantiation hard-crashes with no exception. Never pass a `ValueType`-derived interop wrapper (a struct with reference fields, e.g. `PrefabLookupKey`) as a generic argument. Interop structs with `bool` fields (`User`) are not C# `unmanaged` — use `EntityManager.GetComponentData<User>`, not `Read<T>` / `Write<T>`. `TypeManager.GetTypeInfo(typeIndex).TypeSize` often reads 0 through the interop; the toolkit trusts `sizeof(T)` in that case and only cross-checks when TypeSize is nonzero. Zero-sized ECS tags: use `Add<T>()` / `Remove<T>` / `Has<T>`, not the value-taking `Add`/`Write` (an empty C# struct is 1 byte). Buffer access is omitted on purpose.

```csharp
var em = world.EntityManager;
var players = Query.Entities(em, includeDisabled: true,
    all: ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly));
foreach (var e in players)
{
    if (!e.Has<Translation>(em)) continue;
    var t = e.Read<Translation>(em);
    t.Value.x += 1f;
    e.Write(em, t);
}
if (PrefabLookup.TryGetGuidByName(world, "Item_Ingredient_Crystal", out var guid))
    PrefabLookup.TryGetEntity(world, guid, out var prefab);
```

- `ComponentIndex.IndexOf<T>()` / `IndexOf(Type)` / `Of<T>(AccessMode)` — `Il2CppType.Of<T>()` + non-generic `TypeManager.GetTypeIndex`.
- `Entity` extensions: `Exists`, `Has<T>`, `Read<T>` / `TryRead<T>` / `Write<T>` (`unmanaged`, raw RO/RW + `MemoryCopy`, size-checked), `Add<T>()` / `Add<T>(in T)`, `Remove<T>`, `Describe`.
- `Query.Build(em, includeDisabled = true, includeSystems = false, params ComponentType[] all)` — caller disposes the query. `Query.Entities(...)` copies to a `List<Entity>` and disposes. Default queries skip disabled entities (units out of player range, offline characters); `Build` includes them unless you opt out. An offline player's `User` entity is not disabled — a disconnect only removes `ConnectedUser`/`IsConnected` — so "online" is `User.IsConnected`, never the query option. Unmanaged-system singletons need `includeSystems: true`.
- `Singletons.TryGetEntity(em, ComponentType, out Entity)` — count must be exactly one; uses `GetSingletonEntity`, not `GetSingleton<T>`. `TryGetRawRW(em, TypeIndex, out IntPtr)`.
  - **A zero-sized (tag) singleton is refused before the engine is called.** `TryGetRawRW` returns `false` with one warning naming the component when the `TypeIndex` carries `TypeManager.ZeroSizeInChunkTypeFlag` (`0x40000000`), because `GetComponentDataRawRW` throws `ArgumentException("This operation can not be called with a zero sized component")` for it — evidence 208, `.ecs singleton UserMuteSingleton`. A tag has no bytes: use `TryGetEntity` / `Has<T>` instead. The test is the flag bit in the `TypeIndex` value, **not** a size: `TypeSize` reads 0 for ordinary components through this interop (trap 7), and those still return a valid pointer. `Progenitor.Ecs.SingletonsZeroSize` holds the gate and is game-free, so it is unit-tested.
- `PrefabLookup.TryGetEntity` / `TryGetName` / `TryGetGuidByName` — the specific maps the prefab spike already used; not a license to `TryGetValue` on other native maps.

## World

`Progenitor.WorldState`. One number: **the world-load epoch**. Reached as `ctx.WorldState`.

**The rule.** Any `Entity` a mod keeps past the current frame must be kept with `ctx.WorldState.LoadEpoch` beside it, and dropped when `ctx.WorldState.IsStale(stamped)`.

**Why a counter and not `EntityManager.Exists`.** A V Rising save reload rebuilds every entity in the live world from index 0 upward **at version 1**. The research reload table (`research/castle-module.md`) shows it: heart `327390:1` -> `325974:1`, owner `326690:1` -> `326205:1`, character `324767:1` -> `327864:1` - every id on *both* sides is version 1. That is a load creating entities fresh, not an `EntityManager` bumping the version of a recycled index. So after a reload `Exists(327390:1)` can answer **true** while `327390:1` is now somebody else's heart, and every staleness check keyed on `index:version` - the Castles liveness check, the god-mode guarded set - is blind to the one event it was built for (R32 findings 1 and 6). The epoch is a positive signal about the load itself, so it is correct whichever way the version behavior actually falls; the liveness check stays as a second guard for a death or demolition *within* one epoch.

**The server seam.** A Core-owned Harmony **postfix**, registered through `PatchRegistry` under `<guid>.core` and **routed through a Bootstrap patch slot** like every other patch (job FHARM6, R40 finding 1 — handing HarmonyX this Core `MethodInfo` pinned the Core generation for the life of the process, on every server that reached a world load, and the unpatch did not undo it), on `ProjectM.LoadPersistenceSystemV2.CopyDataFromPrefabs(DeserializationJobData)` (server), step 16 of the eighteen call sites in `LoadPersistenceSystemV2.OnUpdate` (the table is in the internal research notes). It is the single-shot step: `DeserializeLoadFile` (step 9) creates the entities but runs once per archetype file, several times per load, while `CopyDataFromPrefabs` runs exactly once after all of them. Postfix, not prefix, so a load that threw - the vanilla failed load Bootstrap's `SaveSafety` guard is built around - leaves the epoch where it was. Not a `SystemHooks.After` on the system: `LoadPersistenceSystemV2.OnUpdate` ticks every frame in `PersistenceGroup` and does nothing on the frames with no load pending. The postfix needs no `__result` and no argument — it bumps a counter — so the slot's signature-agnostic shape costs nothing here. If even the slot registration is refused (an exhausted or unavailable pool), the fallback is a **typed** Bootstrap site next to the existing `SaveSafetyHook.Postfix` on this same method (described in `WorldLoadHook`, not implemented - it needs a change to the one assembly that cannot be hot-swapped).

**The client seam.** The client process never runs `LoadPersistenceSystemV2`, but its `World` (`Client_0`) is destroyed and rebuilt from scratch on every disconnect, reconnect and server change - the same consequence for a retained `index:version` pair, and far more frequent than a server reload (R33 finding 5). So on the client the epoch moves **once when the client world is created and once when it ends**, with the same log line and the same `WorldLoaded` event; `source=` reads `ClientWorld.Created` / `ClientWorld.Ended`. The identity is `World.SequenceNumber` - set once in the `World` constructor (`get_SequenceNumber`, no setter emitted), which is exactly why it is useless for a *save reload*, which runs inside the existing world (R31 finding 2), and exactly right for an event that *is* a new `World`. Two observations feed one de-duplicating decision (`ClientWorldEpoch.Note`): the `WorldReady` publication Core already makes for every world creation including rejoins, and a poll of the world list - needed because nothing publishes a world *ending*, and a disconnect must invalidate ids at the disconnect rather than at the next join. The poll is per-frame off the cached wrapper while connected, and bounded to one walk in 30 frames while there is no client world (R36 finding 4).

**What the poll costs** (R34 finding 16, R36 finding 4). It used to call `Worlds.Client` every frame, which reads `World.s_AllWorlds` (client) and, for each entry, `IsCreated` and then `Name` - and `Name` is an Il2Cpp string property, so every world on every frame marshaled a fresh managed `string` to be compared and dropped. A list walk plus an allocation per world per frame, at frame rate, for the whole session, whether or not any plugin had ever read the epoch. It now caches the `World` the last scan found, and the scan rule (`ClientWorldScanPolicy`, game-free and unit-tested) is:

| Frame | What it does | Cost |
| --- | --- | --- |
| Connected (nearly every frame of a session) | reads the cached wrapper's `IsCreated` and `SequenceNumber` (`:1336829`) and compares a `ulong` | two property reads, no walk, no `Name`, no allocation |
| No cached client world (main menu, loading screen, after a disconnect) | 29 frames in 30 read `World.s_AllWorlds` and its `Count` and stop; the 30th walks | one bounded walk per 30 frames, and that walk reads `Name` only for a world it has never classified - so zero string marshals in the steady state |
| Cached world gone (disconnect, server change) | the full `Worlds.Find` walk | a few frames per disconnect |
| World list empty and nothing cached | reads `World.s_AllWorlds.Count` | two property reads; a list with nothing in it cannot hold `Client_0` |

**The count is not an identity** (R35 finding 7, job FEPOCH4). The first cut also skipped the walk while `World.s_AllWorlds.Count` was unchanged since the last scan, and said the separate "the cached world went away" check covered the case a count cannot see - a world destroyed and another created between two frames, leaving the count where it was. It covered that case only when the destroyed world *was* the cached client world. With nothing cached - every frame before a join, and every frame after a disconnect until the next one - the count was the only signal, so a frame gap that created the client world and destroyed some other world left the watch blind until the count next moved for an unrelated reason. An unchanged count is therefore no longer a reason to skip: with no live cached world the watch looks.

Covered now: paired churn with a cached world (the cache reports not-`IsCreated`, and that frame walks immediately), paired churn with no cached world (the bounded poll below walks it within 30 frames), and any create the `WorldReady` publication sees (within one frame). **Not** covered: a client world created *and* destroyed entirely between two frames while another is cached and live - nothing a per-frame watch can observe sees a world that never existed on a frame boundary.

**The uncached walk is bounded, not per frame** (R36 finding 4, job FEPOCH5). FEPOCH4 closed the paired-churn hole by walking on *every* uncached frame, and that state is not a few frames: `World.s_AllWorlds` is non-empty for the whole main menu, the whole loading screen, and every frame after a disconnect. Five minutes on the server browser at 144 fps is ~43,200 frames, and the first cut paid a full walk on each one - the pre-R34 cost restored, in the one state where nothing can be reading the epoch (there is no client world, so no handle can be stamped against one). Two changes keep the coverage and drop the cost:

- **The poll is bounded.** With nothing cached the walk runs on the first such frame and then once every `ClientWorldScanPolicy.UncachedScanIntervalFrames` (30) frames - **1,440 walks per five minutes at 144 fps instead of 43,200**. The interval *is* the bound on how late a creation the event missed can be seen: ~0.2 s at 144 fps, 0.5 s at 60 fps. Leaving the uncached state (a live cached world, or the frame a cached world was lost) resets the interval, so entering it always walks on the first frame; and the frame a cached world is **lost** is never deferred, because that frame has an *ending* to report and nothing publishes one. An empty world list still costs two property reads and spends no interval budget.
- **`WorldReady` is the fast path.** It is the moment a client world appears, so `WorldLoadHook.OnWorldReady` notes the epoch, seeds the cached wrapper, and clears the interval: a creation is seen **on the frame it happens**, and the poll is only the backstop for a creation the event missed and for the ending.

**And the walk no longer marshals a string per world** (`ClientWorldNameFilter`). `Worlds.Find` reads `Name` for every entry, and `Name` is an Il2Cpp string property - one managed allocation per world per walk. A `World.SequenceNumber` is assigned once in the constructor and never reused, so it is a permanent identity for a wrapper: the walk reads the allocation-free `ulong` first and reads `Name` only for a sequence number it has not already classified as not-`Client_0`. After the first walk of a stable list, later walks marshal **zero** strings. The set of classified sequence numbers is capped (64) and cleared on overflow, so it cannot leak, and a client world is recorded separately from the rejects so it can never be filtered out of a later walk.

Per-frame calls, as the unit tests measure them: **connected** - `IsCreated` + `SequenceNumber` on the cached wrapper, no list read; **uncached with worlds present** - 29 frames in 30 read `s_AllWorlds` and `Count` only, the 30th walks (`IsCreated` + `SequenceNumber` per world, `Name` only for an unclassified one); **uncached with an empty list** - the same two reads, never a walk; **the disconnect frame** - one walk, immediately.

**Lifetime: one Core generation, not the process** (R33 finding 13). `WorldEpoch` is a static in `Progenitor.dll`, which lives in a collectible ALC and is replaced wholesale on a Core hot-swap, so `Loads` counts *this generation's* loads and a new generation starts over. Bootstrap's `ContentMemory` is the only store that crosses a swap and it carries two fixed row kinds (`CloneOf` hashes, live-refresh fingerprints); a durable epoch would need a new row kind there, i.e. a change to the one assembly that cannot be hot-swapped, so it is deliberately not done. Instead the epoch is **generation-stamped**: each generation takes a stamp in `[1, 2047]` and the epoch is `stamp << 20 | loadsThisGeneration + 1`. A stamp taken in an earlier generation - including one a plugin persisted through `ModSave` - therefore carries the old stamp, and `IsStale` answers true. Treat `LoadEpoch` as an opaque token to compare, never a number to do arithmetic on.

**The stamp is Bootstrap's generation number, read not guessed** (R34 finding 17). The first cut drew `Guid.NewGuid().GetHashCode() & 2047`, which two consecutive generations collide on about 1 time in 2048 - and the colliding case is the common one: a hot swap with no world load on either side leaves both generations at load count 0, so an old stamp compares *equal* and `IsStale` answers **false**. Nothing has to be carried across the swap to fix that, because Bootstrap already names the collectible context after the generation - `CoreReloadPolicy.AlcName(int)` builds `progenitor-core:{generation}`, first load is generation 1 - so Core reads its own generation back with one BCL call, `AssemblyLoadContext.GetLoadContext(typeof(WorldEpoch).Assembly)?.Name`. A read-only observation of the running process: no Bootstrap change and no new stored row, which is what R33 finding 13 declined. The stamp is `((generation - 1) % 2047) + 1`, so no two of any 2047 consecutive generations share one and two adjacent generations never do - staleness across a swap is exact, not probabilistic. Core loaded **outside** Bootstrap (the unit tests, a dev host loading it into the default context) has no such name to read and falls back to `Environment.TickCount` at generation start, folded into the same 11 bits; there the old caveat holds honestly, about 1 collision in 2048. `WorldLoadHook` logs which path ran at install:

```
world: epoch generation 3 (exact, from ALC progenitor-core:3).
```

| Member | Meaning |
|---|---|
| `ctx.WorldState.LoadEpoch` | The epoch the world is in - an opaque, generation-stamped token. Never 0 or negative (so a default `int` field cannot read as current); it changes once per completed load on the server, once per client world create/end on the client. |
| `ctx.WorldState.Loads` | Loads completed **in this Core generation** (a hot-swap starts over). |
| `ctx.WorldState.IsStale(stampedEpoch)` | True when the world was rebuilt since that epoch was read - or when the stamp came from an earlier Core generation. |
| `ctx.WorldState.Describe()` | `world: epoch=2 loads=1` |
| `WorldLoaded` event | Published on the normal `EventBus` once per increment: `Epoch`, `PreviousEpoch`, `Source`. Subscribe through `ctx.Events`. |

One log line per increment:

```
world: load epoch 1 -> 2 (loads=1, source=LoadPersistenceSystemV2.CopyDataFromPrefabs); entity ids recorded before this are no longer valid.
world: load epoch 2 -> 3 (loads=2, source=ClientWorld.Ended); entity ids recorded before this are no longer valid.
```

(In a live log those numbers are the generation-stamped tokens, so they start high and step by one; the examples above are the test generation, nonce 0.)

Named `WorldState` because `ctx.World` is already this plugin's `Unity.Entities.World`.

## Time of day

`Progenitor.Clock`, reached as `ctx.Time` (`PluginTime`). Server only: every member throws on the
client. Main thread. **Landed, not live-verified**: nothing below has been run in the game yet. The
game facts come from the server metadata and the game's native code (internal research notes).

```csharp
var reading = ctx.Time.GetTimeOfDay();          // day 3 14:05 day (running; sunrise 09:00, ...)
var hold = ctx.Time.HoldTimeOfDay(0);           // freeze at midnight for the length of a take
if (!hold.IsLive) ctx.Log.LogWarning(hold.RefusedReason);
hold.Dispose();                                 // the world's own time returns
var shift = ctx.Time.SetTimeOfDay(21.5);        // jump to 21:30 and keep running; dispose to undo
```

| Member | What it does |
|---|---|
| `GetTimeOfDay()` | `TimeOfDayReading`: `Day`, `Hour` (fraction = minutes), `IsDay`, `DayLengthSeconds`, `SunriseHour`, `SunsetHour`, `WorldClockStopped`, `Override`. Null when the world is not ready. |
| `SetTimeOfDay(hour)` | Move the clock to `hour` on the current game day; it keeps running. Returns a tracked `TimeOfDayHandle`; check `IsLive`, and dispose it (or unload) to bring the world's own time back. |
| `HoldTimeOfDay(hour)` / `HoldTimeOfDay(hour, bloodMoon)` | Freeze the clock at `hour`. Returns a tracked `TimeOfDayHandle`. Avoids a blood moon night unless `bloodMoon` is true, which asks for one (see Blood moon). |
| `HasOverride` | True while this plugin has a live override. |
| `ResumeWorldClock()` | Clear the game's `DayNightCycleDisabled` when no override is live (recovery; see Saves). |

Hours are in [0, 24); anything else throws `ArgumentOutOfRangeException`. The day number never
changes: `HoldTimeOfDay(0)` at 14:00 on day 3 goes back to 00:00 on day 3. That keeps day-keyed game
logic, such as the blood-moon day, where it was. There is no `day` argument for the same reason.

**When to take one.** Overrides are refused (`IsLive` false, `RefusedReason` says why) until this
Core generation has seen the current world finish loading: a completed save load, or a Core install
into a world that was already running. An override taken mid-load would be overwritten by the
save's own flag. Overrides do not survive a world load; a new world epoch drops them. Take them from
a `WorldLoaded` handler (`ctx.Events`) or later, for example from a command. A brand-new world whose
first start never runs the save loader has no completed load to wait for, so overrides stay refused
until a restart. This case is inferred and has not been checked live.

`IsDay` mirrors the game's `get_TimeOfDay`: sunrise inclusive, sunset exclusive, no wrap past
midnight. A configured day that ends after midnight has not been checked.

### How the game runs the clock

| Fact | Source |
|---|---|
| `ProjectM.DayNightCycle` singleton: `Time` f64 @0x0, `ServerTime` f64 @0x8 **[SyncDouble]**, `DayNightCycleStartTime` f32 @0x10 (not synced), `DayDurationInSeconds` f32 @0x14 **[SyncRangedFloat]**, `DayTimeStartInSeconds` @0x40, `DayTimeDurationInSeconds` @0x44, `NightTimeDurationInSeconds` @0x48 | server metadata; same offsets in the client metadata |
| `ProjectM.ServerTime` singleton, `Time` f64 @0x0, is the game clock. `ServerTimeSystem_Server` adds the frame delta every frame | server metadata; native `ServerTimeSystem_Server.OnUpdate` |
| `SyncedServerDebugSettings.DayNightCycleDisabled` bool @0xA **[SyncBool]** on a `[NetworkedComponent]` singleton. `SetupSyncedServerDebugSettingsForWorld_*` copies the world's singleton into a process-wide `SharedStatic` every frame; systems read that copy | server and client metadata; native `SetupSyncedServerDebugSettingsForWorld_Base.OnUpdate` |
| `DayNightCycleSystem.OnUpdate` runs on server and client. Flag false: each side sets `Time = DayNightCycleStartTime + ServerTime.Time` (`DayNightCycleExtensions.SetTime`). Flag true on the server: `ServerTime` field = `Time`, and `Time` is not advanced. Flag true on a client: `Time` = `ServerTime` field, then `UpdateTime` | native `DayNightCycleSystem.OnUpdate`, `DayNightCycleExtensions.SetTime` |
| Midnight is `Time % DayDurationInSeconds == 0`; one game hour is `DayDurationInSeconds / 24`; `DayTimeStartInSeconds = DayStartHour * TimePerHour + DayStartMinute * TimePerMinute`; night = day length - daytime | native `get_TimeSinceMidnight`, the `AfterLoadUpdateDayNightCycleSystem` lambda body |
| The admin `SetTimeOfDayEvent` (`Day` @0x0, `Hour` @0x4, `Type` @0x8, Set 0 / Add 1) is handled by `VariousMigratedDebugEventsSystem.HandleSetTimeOfDayEvent`. It sets the **global** `ServerTime.Time` to the requested hour of the **next** day, `(floor((start + T) / D) + 1) * D + hour * D / 24 + day * D - start`, then `DayNightCycle.Time = start + newServerTime`. `Add` also shifts servant-mission and mount timers (lambda jobs 3-6) | server metadata; native `HandleSetTimeOfDayEvent` and its lambda job bodies |
| The settings singleton is read back from the world save (`ReactToDeserializedDebugSettingsSystem_Server`). The game's own toggle, `SyncedServerDebugSettings.SetSetting`, also writes the debug settings file | native `ReactToDeserializedDebugSettingsSystem_Server` lambda body, `SetSetting` |

A client derives its own clock from its copy of `ServerTime.Time` and a start time that is never
sent. A server-only write to `DayNightCycle.Time` or `DayNightCycleStartTime` is overwritten on the
next frame and never reaches a client. The game itself moves a client's sun in two ways: through
the global game clock (the admin event) or through the replicated `DayNightCycleDisabled` flag and
`ServerTime` field.

### Blood moon

A live hold at 01:00 landed on a blood moon night. That is the game working as designed. The game
facts come from the game's native code on the server and the client (internal research notes).

| Fact | Source |
|---|---|
| `DayNightCycle.NextBloodMoonDay` int @0x5C **[SyncInt]**. `ctx.Time` reads it and never writes it (`DayNightCycleNextBloodMoonDayOffset`) | server and client metadata |
| `IsBloodMoonDay()`: `(TotalDays == N && t >= DayTimeStart) \|\| (TotalDays - N == 1 && t <= DayTimeStart)`, with `t = fmod(Time, DayDurationInSeconds)`. Identical in the client binary | native `ProjectM.DayNightCycle.IsBloodMoonDay` |
| Every consumer also requires night: `BloodMoonSystem` (server only) skips daytime, and the blood-bonus getters require `TimeOfDay == Night`. The effective rule is **blood moon = night and `IsBloodMoonDay`**: from sunset of day N to sunrise of day N+1 | native `BloodMoonSystem`, `GetBloodMoonBuffBonus` |
| The schedule: `CalculateNextBloodMoonDay` runs every server frame, including while the clock is held. When `N + 1 < TotalDays` or `N == 0`, it re-rolls `N = TotalDays + Min + rand(Max - Min)`. N therefore holds through day N+1 and re-rolls when day N+2 begins. The value is saved with the world | native `ProjectM.DayNightCycle.CalculateNextBloodMoonDay`, `DayNightCycleSystem.OnUpdate` |
| On the server, `BloodMoonSystem` gives every living character that carries `BloodMoonBuff` the blood moon buff, and night blood bonuses gain `BloodMoonBuffBonus`. Clients read the synced `N` for the clock tint and the announcement | research B1 |

`TimeOfDayMath.IsBloodMoonNight` is that rule. `TimeOfDayReading.BloodMoon` and `NextBloodMoonDay`
report it.

**Holds avoid it by stepping back a day.** `HoldTimeOfDay(hour)` (and `bloodMoon: false`) computes
the hour on the current day. If that time is a blood moon night, the hold goes to the same hour one
day earlier (`TimeOfDayMath.HoldTarget`):

- Pre-dawn on N+1 becomes pre-dawn on N.
- Evening on N becomes evening on N-1.

One step is always enough, and it never moves `TotalDays` past N+1, so the saved schedule is never
re-rolled. It never steps forward, because on day N+2 the server re-rolls `NextBloodMoonDay` and
that value outlives the hold. During such a hold the reading's `Day` (and `GameDateTimeNow`) is one
lower. On release the natural clock returns. The step-back is logged
(`hold 01:00 (day 1132; day 1133 is a blood moon night; held one day earlier)`).

**`bloodMoon: true` holds the blood moon night.** An evening hour holds on day N and a pre-dawn hour
on day N+1. That can move `Time` up to about 18 days forward for the length of the hold, but
`TotalDays` stays at or below N+1, so nothing re-rolls. The call is refused for a daytime hour (no
blood moon shows by day) and while `NextBloodMoonDay` is 0 (schedule not initialized).

A hold re-resolves its target against the live `NextBloodMoonDay` whenever it governs. The value is
constant while it governs, and it is correct again when the override stack returns to an older hold
after a shift.

**`SetTimeOfDay` stays on the natural schedule.** A shift does not avoid or force a blood moon; it
follows the game's schedule as the moved clock reaches it. A shift that carries `TotalDays` past
N+1 makes the server re-roll `NextBloodMoonDay`, and that new value is saved and **persists after
release**. The next blood moon can therefore move by up to the size of the shift.

Not live-verified. Still to check live:

- A 01:00 hold on the day after a blood moon shows a normal night, and the character has no
  blood moon buff.
- A forced hold shows the red night, the clock tint and the buff.
- Neither changes `NextBloodMoonDay` after release.
- Holding a day earlier has no other visible effect.

### What `ctx.Time` writes, and why

While any override is live, `ctx.Time` writes these fields in the server world:

1. `SyncedServerDebugSettings.DayNightCycleDisabled` = 1, only when it is 0.
2. `DayNightCycle.Time` and `DayNightCycle.ServerTime` = the target, only when either differs by
   more than 0.1 ms. Hold: `floor(Time / D) * D + hour * D / 24`, written once. Shift: the game's
   own `DayNightCycleStartTime + ServerTime.Time` plus a constant offset, written every frame.

With the flag set, the server's `DayNightCycleSystem` leaves `Time` alone and copies it into the
replicated field. Each client copies that value into its own `Time`, so the client's sun, shadows and
lighting follow. When the last override is released, the flag returns to its value before the
first override. The game recomputes `Time = start + ServerTime.Time`, so the sun jumps back to where
the world would have been. Every write is a raw field poke through `AiRuntime.RO/RW` at
`layout.txt` offsets (`DayNightCycle*Offset`, `ServerTimeTimeOffset`,
`SyncedServerDebugSettingsDayNightCycleDisabledOffset`).

**The admin event is not used.** Its handler moves the global `ServerTime.Time` forward by up to a
day on every call. Cooldowns, buff durations, castle timers and respawn timers use that clock, so
each call ages the whole world. It also cannot hold.

**Stepping.** A hold is exact: nothing advances the clock, so the sun neither moves nor snaps back.
A shift is written every server frame. A client sees it at the snapshot rate, where one step is a
fraction of a game minute; a 1080 s day moves one game minute every 0.75 s.

**Side effects of the flag.** It is a game-wide debug setting and replicates to every client. Any
other system that reads `DayNightCycleDisabled` sees it too. Only `DayNightCycleSystem` is known to
read it; the other readers were not enumerated. While the flag is set, the server does not refresh
`DayNightCycle.GameDateTimeNow`, because the game's disabled branch skips that update. Sun damage
should follow the held hour, so holding noon would make players in the open burn continuously. That
is inferred from the sun-damage window in `CheckInSunSystem`, which reads `DayNightCycle.Time`, and
has not been verified live.

**An admin's day/night toggle is overridden.** The game's own `DayNightCycleDisabled` debug setting
is the flag an override uses. While an override is live the flag is re-asserted every frame, so an
admin turning the cycle back on has no lasting effect. On release the flag returns to the value
recorded before the first override, replacing whatever the admin set in between.

**Saves and the ownership marker.** The game saves the settings singleton with the world. So the
first override records the flag's prior value in a plain per-save marker: `ModSave` store
`progenitor.core.time`, key `dayNightCycleDisabledBefore`, value `"true"` or `"false"`, no entity
ids. The marker is written in memory at that first override and reaches disk with the next world
save (and on the `ModSave` flushes at Core unload and client disconnect). It is cleared only after
a restore that read the flag back. Recovery runs after every world load and on a Core install into
a running world. If a marker is present and no override is live, the flag is set to the marker's
prior value and the marker is cleared (`TimeOfDayPolicy.Decide`). The first override after a restart
records the marker's prior value rather than the loaded flag (`PriorForFirstTake`), so a stop that
was saved is never mistaken for the world's own setting. A failed restore keeps the marker for the
next recovery. Residual case: a Core unload or disconnect flush that writes the cleared marker after
an autosave captured the stopped flag, followed by a crash before the next world save, loads with
no marker. `GetTimeOfDay().WorldClockStopped` shows this, and `ResumeWorldClock()` (which also clears
a leftover marker) restarts the clock. An override taken while the flag was genuinely already set
logs a warning and leaves the flag set on release.

**Lifetime.** Overrides from every plugin stack, and the newest wins. A handle is released on
`Dispose`, on plugin unload (tracked), in a new world epoch (dropped without touching the new world,
and logged; the marker recovery handles that world's flag), and on Core uninstall (flag restored).
A released handle is removed from the plugin's tracked list. If the first write inside a take
throws, the override is removed again and the take is refused; no orphaned override is left.

Worked example: `samples/Progenitor.Sample/SampleTime.cs` (`.time status | set <hour> | hold <hour> | release | resume`).
The Sample takes the new handle before disposing the old one, so the clock never falls back to
world time between two `.time` commands. A refusal leaves the previous override in place.

### Live test (not run)

Use the isolated test server with a connected client, the Sample loaded, and an admin character
outdoors:

1. Run `.time status` and note the day, hour and `running`.
2. Run `.time hold 0`. The reply says `held`, and the client's scene turns to night within a second.
   Wait two minutes and run `.time status`: the hour has not changed.
3. Run `.time hold 12`. It is daylight; while the character stands in the open, sun charge rises as
   at a natural noon.
4. Run `.time set 16.9`. The sun sets about 0.1 game hours later, a few seconds on a 1080 s day,
   and `.time status` shows the hour advancing.
5. Run `.time release`. The client's sun returns to the world's own hour, and `.time status` shows
   `running`.
6. Hold again, then hot-swap the Sample. Unload releases the hold, and the clock runs.
7. Hold, disconnect and reconnect the client. The reconnected client shows the held hour.
8. Across steps 2-5, confirm the character's cooldowns and buff timers are unaffected.
8a. Blood moon: run `.time status`, note `NextBloodMoonDay` (N), and on day N+1 run `.time hold 1`.
    The night is ordinary, `Day` reads N, and the character has no blood moon buff. Then run
    `.time hold 1 bloodmoon`. The night is red, the clock is tinted and the buff is present. After
    `.time release`, N is unchanged.
9. Hold, trigger a world save, and kill the server process without releasing. Restart. The log
   shows `restored it to False` and the cycle runs. `.time hold 0` followed by `.time release` then
   leaves it running.
10. With the client's admin day/night toggle, stop the cycle, then `.time hold 3`, `.time release`:
    the cycle is stopped again (the pre-override value).

## World speed

`ctx.Time.Freeze()` and `ctx.Time.SlowMotion(scale)` stop or slow the whole world, for filming: freeze
on a spell impact, fly the camera around the held moment, resume; or play a death at quarter speed.
Server only; client calls throw. Main thread. **Landed, not live-verified**: source-checked against
the server and client metadata and the game's native code; unit-tested. The mechanism was
live-checked on 2026-10-02 on the isolated server: the `TimeScale` singleton exists in the server
world, and writing `Value` = 0.5 with the bridge's generic `set` replicated to the Steam client (its
singleton read 0.5) before 1 was restored. The `ctx.Time` members themselves have not run in the game.

```csharp
var freeze = ctx.Time.Freeze();                  // world speed 0 on the server and every client
if (!freeze.IsLive) ctx.Log.LogWarning(freeze.RefusedReason);
// ... move the camera (a free shot keeps flying while frozen), remove objects ...
freeze.Dispose();                                // the previous speed (normally 1) returns

using var slow = ctx.Time.SlowMotion(0.25f);     // quarter speed until disposed or unload
ctx.Log.LogInfo(ctx.Time.GetTimeScale()?.ToString()); // 0.25x (SlowMotion; engine 0.25, not paused)
```

| Member | What it does |
|---|---|
| `Freeze()` | World speed 0 until the returned `TimeScaleHandle` is disposed or the plugin unloads. |
| `SlowMotion(float scale)` | World speed `scale`, 0.001 to 1 (0.25 is quarter speed). Anything else throws `ArgumentOutOfRangeException`; use `Freeze()` for 0. |
| `GetTimeScale()` | `TimeScaleReading`: `Scale` (the game's value), `Paused` (the game's `IsPaused`, never written), `EngineScale` (this process's `UnityEngine.Time.timeScale`), `Override` (`TimeScaleOverrideKind`). Null when the world or the singleton is not ready. |
| `HasTimeScaleOverride` | True while this plugin has a live `Freeze` / `SlowMotion`. |

**Mechanism.** It writes the game's own world-speed setting, the networked singleton
`ProjectM.TimeScale` (`Value`), in the server world, the field the game's debug time control
writes. The game's `TimeScaleSystem` copies it into `UnityEngine.Time.timeScale` every frame on the
server and on every client, and the server's ECS delta time, its `ServerTime` game clock and each
client's copy of that clock are all derived from the scaled Unity delta. No Harmony patch, no
native call.

**What stops (expected from source, not observed).** Everything simulated from the ECS delta time,
on the server and every connected client: units, players, projectiles, movement, AI, buff
lifetimes, cooldowns, damage over time, the game clock `ServerTime` and everything measured in it,
the day/night cycle (it derives from `ServerTime`), and on clients everything Unity scales:
Animator animation, VFX Graph effects and particle systems.

**What keeps running.** Unity frames, the network connection (expected; a live check is owed), `Plugin.Frame`, chat commands,
`MainThread.PostDelayed` and other wall-clock timers, and any plugin logic driven by them; orders a
plugin gives during a freeze are acted on when time resumes. The game's free camera: while the
value is exactly 0 it smooths on real time (its update reads `TimeScale` and switches to real
delta), so `ctx.Camera.FreeShot` glides normally. The orbit camera smooths on scaled time, so
while frozen an `Orbit` shot moves only with `instant: true`; in slow motion both cameras' smoothing
slows by the same factor. Game code that reads real time (audio, some UI tweens) is not affected;
which game systems do this was not enumerated.

**Reach.** Global: every player on that server is frozen or slowed, including the director's own
character. A client in the character-creation menu stays at normal speed (`ForceNormalTimeScale`).
Clients receive the value with 3 decimals (`SyncRangedFloat(0, 100, 3, 1)`).

**Stacking and release.** Leases from every plugin stack: the newest governs (last in wins, the same
rule as time-of-day overrides; a deterministic rule that needs no priorities); releasing it hands
the speed to the previous one; releasing the last writes back the value from before the first
(normally 1). The value is re-asserted every frame while a lease lives, so an admin's debug-menu
speed change is overridden. `IsPaused` is never touched. Leases are `ctx.Track`ed and released on
dispose, plugin unload (every reason), a new world epoch and Core uninstall, so a Core hot swap
during a freeze resumes normal speed. Refused (`IsLive` false, `RefusedReason` set, logged) until
the current world has finished loading and when the singleton is missing.

**Saves.** Whether the game saves `TimeScale` with the world is not known. The first lease records
the prior value in a plain per-save marker (`ModSave` store `progenitor.core.timescale`, key
`timeScaleBefore`, `TimeScalePolicy`), cleared after a confirmed restore. After a world load a
marker with no live lease is resolved by writing the prior value back.

**Separate from time of day.** `HoldTimeOfDay` stops only the sun and leaves every timer running;
`Freeze` stops every timer. Both can be live at once.

Worked example: `samples/Progenitor.Sample/SampleSpeed.cs` (`.speed status | freeze | slow <scale> | release`,
admin only). It takes the new lease before disposing the old one, so the world never snaps back to
full speed between two `.speed` commands.

**Dev bridge.** The `time` op (MCP `progenitor_time`) drives the same leases without a plugin:
`status` lists every live lease with its owner (plugin GUID, `progenitor.dev bridge`, or
`photo mode: <name>`), `speed {value}` and `freeze` hold one bridge lease, `resume` / `release` drop
it. See [bridge-ops-player.md](bridge-ops-player.md#time).

### Game defaults and facts (source-checked)

| Fact | Source |
|---|---|
| `TimeScale.Value` float @0x0 `[SyncRangedFloat(0, 100, 3, 1)]`, `IsPaused` bool @0x4 `[SyncBool]`, `[NetworkedComponent]`, server and client worlds | server and client metadata |
| `TimeScaleSystem` (`LateUpdateGroup`, both worlds): `Time.timeScale = ForceNormalTimeScale present ? 1 : Value` every frame; `IsPaused` swaps the `GameIsPaused` / `GameIsPlaying` tags | native `ProjectM.TimeScaleSystem.OnUpdate` |
| `TimeScaleEventSystem` (server only): a `SetTimeScaleEvent` writes `Value` and `IsPaused` when the sender is an admin, `AdminOnlyDebugEvents` is off, or the sender is the local host | native `ProjectM.TimeScaleEventSystem.OnUpdate` |
| Debug time control (`TimeControl`, client): increase from 0 is +0.025, below 0.5 triples, above 0.5 adds 0.2; decrease above 0.5 subtracts 0.2, otherwise halves, floor 0; reset is 1 | native `TimeControl.IncreaseSpeed` / `DecreaseSpeed`, `DebugMenuMapper` |
| `UpdateWorldTimeSystem`: world delta = scaled `Time.deltaTime`, capped at the world's `MaximumDeltaTime` | native `Unity.Entities.UpdateWorldTimeSystem.OnUpdate` (both sides) |
| Server `ServerTime` += world delta each frame; a client's copy snaps to the server's when more than 0.5 s off, otherwise closes 10 % of its delta per frame | native `ServerTimeSystem_Server.OnUpdate`, `ServerTimeSystem_Client.UpdateTime` |
| Free camera uses real delta when `TimeScale.Value` is 0, scaled delta otherwise; orbit camera always scaled | native `FreeCameraSystem.CameraUpdateJob.UpdateCamera`, `OrbitCameraSystem.CameraUpdateJob.UpdateCameraLerp` |
| Normal speed is 1 | inferred (the game's reset value); not read live |

### Live test (not run)

Isolated test server, one connected client, the Sample loaded, an admin character near enemies.
Every `.speed` step can be driven from the bridge instead (`time` with `status` / `freeze` /
`speed {"value":0.25}` / `release`), which also shows each lease's owner:

1. `.speed status`: expect `1x (no lease; engine 1, not paused)`. A `not ready` reply means the
   `TimeScale` singleton was not found; stop and report.
2. Start a fight or cast a projectile, then `.speed freeze`. Expect units, projectiles, VFX and
   animation to stop on the client within a frame; `.speed status` reads `frozen (Freeze; engine 0, ...)`.
3. While frozen: switch to the free camera and move it; it should glide. Wait 30 s: buff timers and
   cooldowns shown in the HUD should not advance. The connection must stay up (no timeout).
4. `.speed release`: everything resumes where it stopped; `.speed status` reads `1x`.
5. `.speed slow 0.25`: movement and animation at quarter speed; then `.speed slow 0.5` (no snap to
   1x in between); then `.speed release`.
6. Freeze, then hot-swap the Sample: unload releases the lease and speed returns to 1x.
7. Freeze, disconnect and reconnect the client: the reconnected client is frozen too.
8. Freeze, trigger a world save, kill the server without releasing, restart: the world runs at 1x
   (either the game did not save `TimeScale`, or the log shows `restored 1x`).
9. Check the server log for NaN/Infinity errors from game systems while frozen (the world delta is 0).

## Castles

Research notes (internal): component layouts, proposed reads, buffer probe. Requirement: plot / tile / building reads first; occupancy and placement are later spikes.

`Progenitor.Castles.CastleReader`. Server-side snapshot of territories, floors, and placed tile-models. Through `PluginContext`, `ctx.Castles` is the same surface bound to this plugin's world (empty when the world is not ready). Read-only: unload has nothing to reverse.

```csharp
if (ctx.IsServer)
{
    foreach (var plot in ctx.Castles.Plots())
    {
        ctx.Log.LogInfo($"{plot.OwnerName} plot {plot.TerritoryIndex} lv{plot.Level} raided={plot.Raided}");
        var tiles = ctx.Castles.Tiles(plot);          // row-major: Tile.y, Tile.x, height
        var buildings = ctx.Castles.Buildings(plot);  // NetworkId.Normal_Index then Normal_Generation
    }
    var here = ctx.Castles.PlotOf(someHeartOrTerritory); // null if not on a plot
}
```

The non-context path is `CastleReader.Plots(world)` / `Tiles(world, plot)` / `Buildings(world, plot)` / `PlotOf(world, entity)`. Each reader is try/catch per entity and never throws for a missing component (one warning per component name, then silent). Queries use `Query.Entities` with disabled entities included. Structs with a bool or `NetworkedEntity` (`CastleHeart`, `CastleTerritory`, `CastleFloor`, `UserOwner`, `User`, `Door`, …) go through `GetComponentData<T>`; blittable ones (`TilePosition`, `PrefabGUID`, `NetworkId`, `TileBounds`) use `EntityExtensions.TryRead<T>`. Never `GetSingleton<T>`, never `GetBuffer<T>` on this path.

| Read | Returns |
|---|---|
| Plot | `CastleTerritoryIndex`, tile AABB (`WorldBounds`), heart entity, owner user (entity + platform id + name), clan entity + name, level, raided (`ActiveEvent == Raided`), decaying (`CastleTerritoryDecay.Decaying`). Block count **and** coordinates from a raw walk of `CastleTerritoryBlocks` (`int2` per element, stride 8, x at 0x0, y at 0x4 — server metadata) through the non-generic `GetBufferLength` / `GetBufferRawRO` pair; a length past the block count the plot's own `WorldBounds` allow is refused and `BlocksSkipped` stays set. |
| Tiles | Floor cells only (empty AABB cells omitted). Per tile: x/y/height, floor present, `CastleFloorTypes` including treasury (14), room entity/id, NESW wall bits, NESW door bits (`Door` on the wall entity). Sort is row-major. Floors without `CastleHeartConnection` are kept only inside the plot AABB (min inclusive, max exclusive). |
| Buildings | Every `TileModel` + `CastleHeartConnection` matching the plot heart. Prefab GUID + `PrefabLookup` name, origin tile + `TileRotation`, `TileBounds` footprint, `Interactable` presence, kind heart/container/station/decor (`CastleHeart` wins over `InventoryOwner` over `CastleWorkstation`/`Refinementstation`). Sort is `NetworkId.Normal_Index` then `Normal_Generation`. `CastleHeartHelpers.GetConnectedTileModels` is not called (`NativeList<Entity>` is not live-verified). |

`CastleLogic` is the game-free half (sort, kind, NESW bits, floor-type names, limit clamp) so tests and the live reader cannot drift.

Debug bridge: `castles` op (`{what: "plots"|"tiles"|"buildings", plot?: territory index, limit}`). MCP: `progenitor_castles`.

**Verified live 2026-09-04** on TestPlayer's plot (territory 13, a blood-fountain heart and one outdoor floor): the `CastleHeart` query returns the plot, floors carry `CastleHeartConnection`, the `TilePosition.HeightLevel` getter works through the interop (11, no fall back to `CompressedHeight`), and `CastleRoomConnection.RoomEntity` resolves. Readings are in the internal research notes.

### Handles: `Castles.All` / `ForUser` / `IsInside` / `CanPlace`

`Progenitor.Castles.Castles` is the mod-facing layer over the reader. Everything it returns is game-free (`CastleId`, `CastleVec3`, `CastleSnapshot`, `CastleBuildingInfo`, `CastleTileInfo`), so the test project compiles the whole module except its one Unity file (`UnityCastleRuntime.cs`) — the `IUnitRuntime` / `UnityUnitRuntime` split Units uses. Read-only, so nothing is `Track`ed and unload has nothing to reverse. **Server-only**: the client returns empty lists and disabled handles with one Debug line, like Effects and Units, and every entry point throws off the game main thread (EntityManager is not thread-safe).

```csharp
// userEntity comes from an ICommandContext or a PlayerConnected event — PluginContext has none.
var castle = ctx.Castles.ForUser(userEntity);       // never null; check Enabled
if (castle.Enabled)
{
    ctx.Log.LogInfo($"{castle.Owner} plot {castle.Territory.Index} lv{castle.Level} floors {castle.FloorCount}");
    foreach (var b in castle.Buildings())                 // NetworkId order
        ctx.Log.LogInfo($"{b.PrefabName} @{b.TileX},{b.TileY} h{b.HeightLevel}");
    foreach (var row in castle.BuildingsByPrefab())       // count desc, then name
        ctx.Log.LogInfo($"{row.Key} x{row.Value}");

    bool here = castle.IsInside(somePosition);            // X/Z; tile mask when readable, else bounds
    bool standing = castle.IsInside(CastleId.From(characterEntity)); // TilePosition when it has one
    var check = castle.CanPlace(prefabGuidHash, somePosition);
}

foreach (var c in ctx.Castles.All()) { }                  // ctx.Castles.AllForUser / ForTerritory / At
```

| Member | Returns |
|---|---|
| `Castles.All`, `ctx.Castles.All()` | Every castle, territory-index order. |
| `ForUser(user)` / `AllForUser(user)` | Castles owned by that user **or their clan**. `ForUser` never returns null — a player with no heart gets a disabled handle. |
| `ForTerritory(index)` | The reload-safe lookup: territory index is durable, `Entity` is not (see below). |
| `ForHeart(heart)`, `At(position)`, `At(entity)` | By heart entity, or the castle whose territory contains a point / an entity. |
| `ICastleHandle` | `Heart`, `Owner` (user + platform id + name + clan), `Territory` (index, entity, bounds, height band, block count, `Width`/`Depth`/`TileCount`), `Level`, `FloorCount`, `Raided`, `Decaying`, `Buildings()`, `BuildingsByPrefab()`, `Tiles()`, `IsInside`, `IsTilePrecise`, `IsStale`, `DisabledReason`, `CanPlace`, `Refresh()`. |

**Entity ids are session-scoped, and a handle knows it.** Reloading the save re-assigned every entity index on the live plot while territory index, `NetworkId`, bounds and tile coordinates stayed identical. Persist `CastleTerritory.CastleTerritoryIndex` and `NetworkId`; never persist an `Entity`.

A handle carries **two** staleness checks, in this order. First the **world-load epoch** (see [World](#world)): the handle stamps `WorldEpoch.Current` at construction, and when the counter has moved it is stale whatever the world says about its ids. That is the check that actually covers a reload — R32 finding 1: the reload can re-issue the same index at the same version 1, so the liveness check below answers "live" for another castle's heart. The `DisabledReason` names **both** epochs (`stamped in world load epoch 1, the world is now in epoch 2`) as well as the territory index to re-resolve with. Second, kept as an additional guard for a demolition or a heart destroyed *within* one epoch, the handle checks on every live read whether its own two entities — the heart and the territory entity — are still live at that exact `index:version`. When either check fires the handle is **stale**: `IsStale` is `true`, `Enabled` flips to `false`, `DisabledReason` names the dead id and the territory index to re-resolve with, and no live read re-enters the world with ids that may now belong to another plot (`Buildings()` / `Tiles()` are empty, `IsInside` is `false`, `CanPlace` returns the disabled check carrying that reason). `Refresh()` re-resolves through `Territory.Index` — never `ForHeart` — so it finds the same durable castle under its new heart entity. Handles are cheap: prefer taking a fresh one over retaining one across frames.

The signal is deliberately **not** `World.SequenceNumber`: that number is set once in the `World` constructor (server, no setter emitted) and V Rising loads a save *inside* the running server world — `TriggerNewLoad(World world, …)` and `DeserializeWorldData(…, out NativeArray<int> existingVersionsByEntityIndex, …)` both take the existing world, as a normal tick of `PersistenceGroup` — so it cannot move across the event it was meant to detect. The entity **version** does move, which is exactly what that `existingVersionsByEntityIndex` parameter is about, and `EntityManager.Exists` reads it. A runtime that cannot answer the liveness question at all (no world, the read threw) is treated as **stale**, not as "cannot be checked": an unreadable signal is a changed signal, the same rule Effects settled for its world signal. It says so once in the log, and it does not latch — the handle is readable again as soon as the signal is.

**Tile grid.** `CastleGeometry` converts world to tile: `tile = floor(world / 0.5) + 6400`, both constants derived from two live tile-models and pinned by a test. There is **no** world-Y to `TilePosition.HeightLevel` mapping, so position tests are X/Z only and the height band is tested only for entities that carry their own `TilePosition`.

**`CanPlace` is a documented subset, not the game's answer.** The game's validator, `GetPlacementResult.GetResult(ref Operation, ref JobData)`, is not callable read-only on this build: `JobData` is a 0x10E8-byte Burst struct built only from a live `SystemState`, and reaching it means by-ref struct parameters (trap 1), ~30 `ComponentLookup<T>` instantiations (trap 2) and a guessed native convention (trap 5). So `CanPlace` reproduces territory containment, the buildable-tile mask when readable, the height band, and occupancy against the plot's own placed models — `CastlePlacementCheck.Complete` is always `false` and `.NotChecked` lists the eleven vanilla rules it skips (placement type requirements, map zones, restriction areas and roads, contests, rebuild history, teleporters, siege, terrain collision, attach/replace resolution, team rules, build cost). Footprints come from placed instances, because a prefab's own `TileBounds` reads all zeros (the local bounds live in the `TileData` blob); an unseen prefab falls back to one tile with `FootprintResolved = false`. Placement itself (writing) is still gated on the `BuildTileModelEvent` spike.

**Events.** `CastleHeartPlaced` / `CastleHeartDestroyed` on the normal `EventBus`, from a once-per-second change detector on the server that runs **only while something is subscribed**. There is no cheaper seam: the spawn and event systems tick every frame regardless, and destruction has no single system. The first poll of a world reports every existing castle with `InitialScan = true`.

**Territory buffers.** Both are read through the non-generic `GetBufferLength` / `GetBufferRawRO` pair Effects already uses live — never `GetBuffer<T>` (trap 2). Element layouts come from the game's metadata, never from `sizeof` or `TypeManager` (the EV17 rule), and live as constants in the game-free `CastleTerritoryBuffers`, measured through the decoders by `CastlesTests.Territory_buffer_strides_and_offsets_are_the_ones_the_decoders_walk`:

| Buffer | Element (server metadata) | Stride / offsets | Guard |
|---|---|---|---|
| `CastleTerritoryBlocks` | `struct CastleTerritoryBlocks : IBufferElementData { int2 BlockCoordinate; }` | 8; x at 0x0, y at 0x4 | length must be within `MaxBlocksForBounds` — the count of 10×10 blocks the plot's own `WorldBounds` touch (273 on territory 13), capped at 65 536 |
| `CastleTerritoryTiles` | `struct CastleTerritoryTiles : IBufferElementData { bool InsideBuildableTerritory; }` | 1; flag at 0x0 | length must **equal** the caller's own `BoundsTileCount` (21 504 on territory 13) |

Both guards bound the walk with a number derived from the territory's bounds, never from the length being checked: the caller sizes its span as `length * stride`, so a guard that compares the length against that span can only fire in a unit test while a wrong `GetBufferLength` (R27 finding 17) walks off the allocation. A territory whose bounds did not read gives a maximum of 0, and 0 means the blocks buffer is not walked at all. Block size is 10 tiles per axis, native from the `/ 10` and `* 10 + 1` / `* 10 + 10` arithmetic in the native `GetPlacementResult.IsArenaStationOverlappingOutsideBlock` and consistent with `BlockReason.RequireGridAlignment10x10`.

The tile mask is **row-major** over `WorldBounds`, and that is now native rather than inferred. The native body of `BoundsMinMax.GetLocalIndexFromBoundsTile` tests `Min.x <= x`, `y >= Min.y`, `x < Max.x`, `y < Max.y` (inclusive min, exclusive max) and returns `(y - Min.y) * (Max.x - Min.x) + (x - Min.x)` — exactly `CastleGeometry.LocalTileIndex`. Column-major and `width + 1` variants are ruled out. The buffer itself is indexed with that same expression: `GetPlacementResult.IsArenaStationOverlappingOutsideBlock` hands each tile with `territoryBounds` and the buffer to `CastleTerritoryExtensions.IsTileInTerritory`, whose three-argument overload computes `(x - Min.x) + (y - Min.y) * (Max.x - Min.x)`, bounds-checks it against the buffer's `Length`, and returns the flag at that element. When the length check passes, `IsInside` and `CanPlace` answer from the mask and `IsTilePrecise` is `true` (the sample's `bounds-only` marker disappears); when it fails, the handle falls back to the AABB and the territory logs **one info line** (`Logged once per territory`), not a warning per `.castle` call.

**Occupancy is keyed by (tile, height level).** A floor on level 11 does not occupy the same tile on level 12 — that is how furniture stands on floors. There is no verified world-Y to `HeightLevel` mapping, so `CanPlace` cannot derive the level the caller meant from the position: on a single-level territory it uses that level, and on a multi-level one (the live plot is 10–12) it evaluates the band **one level at a time** and returns the first level that allows the placement, with `HeightAmbiguous = true` and `HeightLevel` naming the level the verdict belongs to. A refusal with `HeightAmbiguous` means no level in the band was free. Rotated footprints are half-open on every axis: rotating `[min, max)` about the pivot gives `[1 - max, 1 - min)` on each negated axis, so a 90/180/270 step keeps the box's tile set instead of shifting it one tile (the rotation itself is still inferred — nothing on the test save is rotated).

Three questions stay open on this module: the build-grid step and the `TileBounds` margin (footprint overlap is conservative until a row of adjacent floors is placed on the test save); a `TileRotation != None` footprint (nothing on the save is rotated); and world-Y to `HeightLevel` (one sample cannot fix it, so position tests are X/Z only).

## Stations

`Progenitor.Stations`, reached as `ctx.Stations` (`PluginStations`). Server only: every member
except `CraftsPossible` throws on the client (`CraftsPossible` is pure and works on either side). Main thread. **Landed, not live-verified**: the reads, the switch and the
sample are source-checked against the game's metadata and its native code (internal research
notes), built, and game-free tested; nothing has been run in the game yet. Inventories (what sits in
a station's input and output) are not part of this module.

```csharp
foreach (var info in ctx.Stations.Near(position, 20f))           // recipe stations, nearest first
{
    foreach (var r in ctx.Stations.Recipes(info.Station))
        ctx.Log.LogInfo($"{r.Name} on={r.Enabled} {r.DurationSeconds}s");
    var p = ctx.Stations.Progress(info.Station);                 // State, Fraction, RemainingSeconds
}
var result = ctx.Stations.SetRecipeEnabled(furnace, sulfurRecipeGuid, on: false);
if (!result.Ok) ctx.Log.LogWarning(result.Reason);
int crafts = ctx.Stations.CraftsPossible(recipe, new Dictionary<int, long> { [copperOre] = 75 }); // 3 at 20 each
```

| Member | What it does |
|---|---|
| `Kind(station)` | `Refinement` (sawmill, furnace, tannery, loom, ...), `Workstation` (workbench, smithy, ...) or `None`. |
| `Recipes(station)` | `StationRecipe` rows in the station's order: `RecipeGuid`, `Name`, `Inputs`, `Outputs`, `DurationSeconds`, `Enabled`, `CanToggle`, `Unlocked`, `HiddenInStation`. Empty for a non-station. |
| `SetRecipeEnabled(station, recipe, on)` | Refinement only. Sets the station window's per-recipe checkbox to `on`, together with the recipes the recipe links. Returns `StationToggleResult`; `Ok` false with a `Reason` on a workstation, a non-station or a recipe the station does not list. |
| `CraftsPossible(recipe, sources)` | Whole crafts the item counts in `sources` pay for (the smallest `have / need` over the inputs, rounded down). Pure. |
| `IsWorking(station)` | True while `Progress(station).State` is `Working`. |
| `Progress(station)` | `StationProgress`: `State`, `RawStatus`, `RecipeGuid`, `Fraction`, `ElapsedSeconds`, `DurationSeconds`, `RemainingSeconds`, `QueueLength`. Null for a non-station. |
| `Near(position, radius)` | `StationInfo` (entity, prefab, kind, position, distance) for recipe stations within `radius` meters. Scans every station in the world: call it on demand (a command, a panel opening), not every tick. |

### How the game stores it (source-checked)

| Fact | Detail |
|---|---|
| Refinement recipe list | `RefinementstationRecipesBuffer` on the station instance: `RecipeGuid`, `Unlocked`, `Disabled`. Networked, so every client's station window reads it. |
| Refinement work | `Refinementstation`: `Status` (`RefinementStatus`), `CurrentRecipeGuid`, `RefiningStartTime` (server time when the batch started), `IsWorking`. There is no stored fraction. Refining picks an enabled recipe the input inventory can pay for and runs one batch at a time; a station runs only while its castle heart burns blood essence. |
| The checkbox | The client sends `ToggleRefiningRecipeEvent`; `ToggleRefiningRecipeSystem` checks the sender is using the station, flips the row's `Disabled`, sets every row whose recipe the recipe prefab names in `RecipeLinkBuffer` to the same value, and sets `CastleWorkstation.ForceUpdateInStation` so refining re-picks a recipe. |
| Workstation recipe list | `WorkstationRecipesBuffer` on the instance. No per-recipe switch exists; recipe unlocks are per player. |
| Workstation work | `QueuedWorkstationCraftAction` rows; only the head row progresses. `ProgressTime` counts toward `RecipeData.CraftDuration` and is kept while the station is stopped. `Status` is `CraftingStatus`. |
| Recipe details | The recipe prefab: `RecipeData.CraftDuration` and `HideInStation`, `RecipeRequirementBuffer` (inputs), `RecipeOutputBuffer` (outputs). |

`SetRecipeEnabled` makes the same writes as the game's toggle, directly on the server, so no player
needs the station open: the rows' `Disabled` byte, then `ForceUpdateInStation`. Unlike the checkbox
it sets a state rather than flipping one, so repeating a call is harmless (`Changed` false). The
plan is `StationLogic.PlanToggle`, game-free tested. Every read and write is raw at the declared
[layout constants](#layout-constants) through the non-generic buffer and component accessors.

**Limits.** `Inputs` are the recipe's base amounts: a station on its matching floor needs fewer
(the game multiplies the cost), so `CraftsPossible` over base amounts never overcounts but can
undercount. `DurationSeconds` and `RemainingSeconds` ignore station speed bonuses (a closed room),
so a boosted station finishes sooner than `RemainingSeconds` says. Refinement `Fraction` is
`(server time - RefiningStartTime) / CraftDuration` while `Working`, 1 while `OutputFull`, and 0
otherwise. Station entities are valid for the current world load only; do not persist them. A
stable station id comes with the castle containers and stations work.

### Live test (acceptance procedure, not run)

Isolated test server with a connected client, the Sample loaded, and an admin character standing at
a powered furnace (castle heart burning blood essence) that lists the copper and sulfur recipes.

1. `.stations list`. The reply lists the furnace with an `index:version@epoch` id, `refine`, and a
   distance under 20 m. The server log has a Sample line `stations: list radius=20 found=N`.
2. `.stations recipes`. One line per recipe, for example
   `[on] CopperBar: 20 CopperOre -> 1 CopperIngot, 30 s`. Compare with the furnace window: the same
   recipes and amounts, and every `[on]` row is checked.
3. Open the furnace window and keep it open. `.stations toggle Sulfur off`. The reply says
   `Sulfur is now off`, and the server log has `[stations] recipe <guid> disabled on station <id>`.
   The window's Sulfur checkbox clears within a second, without reopening it.
4. Put 40 sulfur ore and 40 copper ore in the furnace input. `.stations status` shows
   `working, CopperBar`, and the sulfur ore stays untouched while the copper smelts.
5. `.stations toggle Sulfur off` again. The reply ends `(no change)`.
6. Tick the Sulfur checkbox in the window by hand, then `.stations recipes`: the row reads `[on]`.
7. `.stations status` twice, ten seconds apart, during a batch: the percentage rises and the seconds
   left fall by about ten. Let the heart run out of fuel: the state reads `not powered`.
8. At a workbench with a queued craft, `.stations status <id>` shows `Workstation: working`, the
   queued recipe and `queue N`; `.stations toggle <recipe> off <id>` is refused with
   `crafting workstations have no per-recipe switch`.
9. Disconnect and reconnect the client; the furnace window still shows Sulfur unchecked. After a
   server save reload, `.stations status <id from step 1>` is refused with
   `That id is from an earlier world load`.
10. Clean up: `.stations toggle Sulfur on`, take the ore out, and remove the Sample from the reload
    folders.

## Inventory

`ctx.Inventory` (`Progenitor.Inventory`) reads and moves items in any inventory on the server:
chests, a station's input and output, a castle heart, a servant, a player's bag. Server only and
main-thread only; on the client every call throws `InvalidOperationException` ("ctx.Inventory.X
runs on the server side only"). Built and game-free tested (`InventoryEngineTests`,
`ContainerIdTests`, `CastleContainersTests`); **not live-verified** — the acceptance procedure is
below. API table: [api.md, Inventory](api.md#inventory).

```csharp
// Two chests in the castle the player stands on, then a move of 50 iron ore that conserves the total.
var castle = ctx.Castles.At(characterEntity);
var chests = castle.Containers();
var from = chests[0].Main;
var to = chests[1].Main;
int ore = ironOreGuid; // PrefabGUID hash
InventoryResult r = ctx.Inventory.Transfer(from, to, ore, 50);
ctx.Log.LogInfo($"{r.Outcome}: moved {r.Done}; source {r.SourceBefore}->{r.SourceAfter}, target {r.TargetBefore}->{r.TargetAfter}");

// Persist the chest, not the entity: the id survives restarts.
ContainerId keep = chests[0].Id;
if (ctx.Inventory.TryFind(keep, out var chest, out var why)) { /* chest is the live entity */ }
```

### Which inventory

`InventoryRef` is a session handle: owner entity, instance and the world epoch it was taken in.
A reference from before a save reload is refused as stale; keep a `ContainerId` instead.

| Owner | Where the slots are (game facts, server metadata) | `InventoryRef` |
|---|---|---|
| Chest, castle heart, player bag entity | The entity carries `InventoryBuffer` itself | `Of(owner)` (instance 0 only) |
| Player character, servant | `InventoryInstanceElement` rows point at external inventory entities (one row on a character: the bag) | `Of(character)` |
| Refinement station (furnace, sawmill, …) | Two external inventories, spawned input then output (8 slots each, unrestricted) | `InputOf(station)` = instance 0, `OutputOf(station)` = instance 1 |
| Crafting station | One inventory | `InputOf(station)` / `Of(station)` |

`Instances(owner)` says how many there are. The heart's inventory is reached with
`Of(heart)`; which slots are its fuel pads is read from its level table
(`ICastleHandle.HeartFuel().UnlockedSlots`), see [Castles: containers, stations and the heart](#castles-containers-stations-and-the-heart).

### Rules the reads follow

Item facts, the stack size included, come from the server item table (`GameDataSystem.ItemHashLookupMap`),
the same table the game's own add and move read. An item missing from the table is refused ("not in the
server item table").

- **Restrictions** (`RestrictedInventory` on the inventory entity, exposed as
  `InventoryRestriction`): a container admits an item when both fields are 0, or when the type is
  0 or the item, **and** the category is 0 or shares a flag with the item's
  `ItemData.ItemCategory`. So a storage restricted to precious categories refuses Blood Essence.
- **Capacity**: a slot holds its `MaxAmountOverride` when that is above 0, otherwise the item's
  stack size. The game stacks onto matching slots below capacity first, then fills empty slots
  (item 0).
- **Player bags**: the game keeps stackables, jewels and tech items off the eight action-bar
  slots first, so `FreeSpace` on a bag is an upper bound; every write reads its result back
  regardless.

### Writes

| Call | Game path | Guarantee |
|---|---|---|
| `TryAdd` | `ServerGameManager.TryAddInventoryItem` (the `ctx.Items.Grant` path); the character as target for a player's bag, the inventory entity otherwise | Partial adds allowed; `Done` and `Remainder` are read-backs. |
| `TryRemove` | `InventoryUtilitiesServer.RemoveItemGetRemainder` on that inventory | Never asks for more than the inventory holds. Asking for more would make the game fall back to unequipping worn gear, which this call never reaches. |
| `Transfer` | `InventoryUtilitiesServer.TryMoveItem`, one source slot at a time, with the target instance index set | Conserves the total of interchangeable items; for any other item a step that does not conserve the total is detected and the transfer stops (`Inconsistent`) without creating or deleting a copy (below). A completed game move keeps the item's own entity (durability, stats). |
| `TransferAll` | One `Transfer` per item type the filter accepts | Stops at the first `Inconsistent` row. |

**How the transfer keeps the total.** The game's slot move adds into the target first, clears the
source slot only when that add succeeded, and puts the part that did not fit back into the slot it
came from. Around each step the engine reads both containers back and checks that the source lost
exactly what the target gained.

- **Equipped on arrival.** The game's move equips gear when the target's owner (a player, a
  servant) has a free matching slot, so the unit leaves the target inventory for the owner's
  equipment. The engine counts the owner's worn copies (`Equipment`, `ServantEquipment`) before and
  after each step and reports those units as moved (`InventoryResult.Equipped`); nothing is
  restored for them.
- **Restore only for interchangeable items.** When a step does not conserve the total and the item
  is a stackable or consumable without its own item entity in either container, the restore step
  adds the missing units back to the source (never anywhere else) or removes the extra units from
  the target (then the source), never more than that side holds. For anything else — gear, or any
  copy with its own entity — nothing is added or removed: the transfer stops with
  `InventoryOutcome.Inconsistent` instead of guessing which copy to recreate or delete.
- **Worn copies are counted by entity.** A wielded weapon can keep its bag slot; a worn copy whose
  entity is also a row of the target inventory is counted once, as an inventory unit. For a bag
  entity the owner is the character its `InventoryConnection` names. `TryAdd` applies the same
  accounting.
- If the total still differs, the transfer stops with `Inconsistent`, logs `Inventory: transfer …`
  as an error, and reports both containers' counts.
- Two references that resolve to the same inventory entity (a character and its own bag entity)
  are refused.
- The move keeps relic and soulbound rules on (`MoveItemResult` `Failed_ItemIsSoulBound` /
  `Failed_BlockedByRelicRules` end the transfer as `Nothing` with the reason).

### Watch

`Watch(container, handler, interval)` compares a content fingerprint (slot count, then each slot's
item and amount) every `interval` (default 500 ms, minimum 100 ms; at most 1024 watches on a
server). Cost per watch per check: one buffer read and one pass over the slots, no item-table
lookups. A `ctx.Inventory` write pokes the watches
on the containers it touched, so those report on the next frame. The handler gets
`InventoryChangeKind.Changed`, or `Gone` once (owner destroyed or world reloaded) after which the
watch stops. Watches are tracked on the plugin context.

The game does create `InventoryChangedEvent` entities on its add, remove and move paths, but which
system consumes them and whether every inventory writer creates one is not established, so the
watch does not depend on them.

### Stable container ids

`ContainerId` (`c-` plus 32 hex digits) names a placed container, station or heart across
restarts. `IdOf(entity)`, `Status(id)` (`Unknown`, `Live`, `Unbound`, `Gone`) and
`TryFind(id, out entity, out reason)`; castle rows carry it as `CastleContainerInfo.Id` and
`ICastleHandle.HeartId`.

How it works: the framework assigns a random id the first time a container is asked about and
records it against the container's **anchor** — territory index, prefab, origin tile, height level
and rotation, which the world save keeps for every placed model and which no two live models of one
prefab share. The book lives in that world's mod save (`progenitor/progenitor.inventory.json` beside
the autosaves) and carries that world's identity (`progenitor/inventory-world-id.txt`, copied with the save), so a book from another world is ignored; new ids reach the store at the end of the frame and before every
world save, and the store is written to disk with each world save. After a load the framework re-reads it, binds each
saved id to the container standing on its anchor and retires ids whose anchor holds nothing; while
the server runs, a sweep every two seconds retires ids whose container was destroyed and moves the
anchor of a model that was moved, so a new chest on a freed or destroyed tile gets a new id. Ids
are random, so a retired id is never issued again.

Failure modes, by design:

1. A container destroyed and replaced on the same tile while Progenitor was not running (or within
   the two-second sweep window before a shutdown save) inherits the old id.
2. A world rolled back to an older autosave can bring back a container whose id was already
   retired; it gets a new id.
3. Relocating a castle rebuilds and moves every model, so every container gets a new id. (A model moved while the server runs keeps its id: the sweep follows it to its new tile.)
4. A model moved within the two seconds before a save (before the sweep saw the move) is
   found on its old tile after a restart, so it loses its id and gets a new one.
5. Player bags and servants have no id; address them through their owner.

Ids are not available until the world's mod save has loaded and the castles are visible after a
load (`IdOf` returns `ContainerId.None` with the reason meanwhile).

### Castles: containers, stations and the heart

| Member | What |
|---|---|
| `ICastleHandle.Containers()` | Placed models with an inventory that are not a station or the heart. |
| `ICastleHandle.Stations()` | `CastleWorkstation` or `Refinementstation` models; refinement rows carry `Output`. |
| `ICastleHandle.Name` | The heart's nameplate. |
| `ICastleHandle.HeartId` | The heart's stable id. |
| `ICastleHandle.HeartFuel()` | `Level` (`CastleHeart.Level`), `UnlockedSlots` (the `ItemSlots` of that level's row in the heart's own level table, `CastleHeart.Data`), `LevelRows`, `FuelQuantity`. |
| `ctx.Castles.Nameplate(entity)` / `TrySetNameplate(entity, name, out reason)` | `NameableInteractable.Name`, a networked fixed string: at most 61 UTF-8 bytes, no control characters, empty clears. Written through the component overlay at the metadata offset. |
| `ctx.Castles.RelationTo(user, castle)` | `Owner`, `ClanMember` (in the owner's clan), `Allied` (the game's `Team.IsAllies` between the user's character and the heart), or `None`. |

Other castle inventories, from the server metadata (fields verified, behavior inferred, not
live-checked): a horse stable carries `Stables_Shared` (the mount is referenced by
`MountEntity`, it is not an item) and has an inventory whose items feed stat upgrades (items with
`StablesIngredient`) and receive `MountItemGainPerSecond`; unit spawners (`UnitSpawnerstation`)
run recipes from an inventory, and whether they have one or two instances is not in the metadata —
`Instances(entity)` answers it live; a servant coffin has no inventory of its own (the servant unit
does), and a prison cell references its prisoner.

### Clans

`ctx.Clans.Of(user)` returns a `ClanRef` (`Entity`, `ClanGuid` from `ClanTeam` — the key to
persist — and `Name`), `Members(clan)` every user whose `User.ClanEntity` is the clan (offline
included, ordered by name) and `Castles(clan)` every castle whose owner's clan it is. Server only.

### Presence events

| Event | When | Cost |
|---|---|---|
| `PlayerTerritoryChanged` | A connected player now stands on another territory (`OldTerritory`, `NewTerritory`, -1 for none, `Castle`, `Initial` for the first reading on a plot). Debounced over two consecutive readings. | Only while subscribed: once a second, one position read per connected player and an `IsInside` test per castle; the castle list is re-read every 10 s or after a load. |
| `PlayerReady` | Once per session, after `PlayerConnected`, when the user's `LocalCharacter` is live and `User.LastFullyAckedFrame` has advanced (the client is simulating the world). **Experimental**: the signal is inferred, not live-verified. Players already in the world when Core installs are not announced. | Four checks a second per pending session, nothing after. |

`PlayerConnected` remains the earlier signal (connection approved, the character may not be in the
world yet).

### Sample commands

`samples/Progenitor.Sample/SampleInventory.cs`, admin only, through `.items`:
`chests`, `slots <c>`, `count <c> <item>`, `free <c> <item>`, `move <from> <to> <item> <amount>`,
`moveall <from> <to>`, `add|remove <c> <item> <n>`, `name <c> "text"`, `id <c>`, `watch <c>`,
`heart`, `clan`. A container `<c>` is `#n` (row of `.items chests`), a `c-…` id, an
`index:version` entity, `bag` or `heart`; append `/out` for a refinement station's output. Items are
a spawnable name or a GUID hash. Every action logs a `Sample items: …` line; the sample also logs
`PlayerReady` and `PlayerTerritoryChanged`.

### Live acceptance (procedure — not run)

Goal: move 50 Iron Ore between two chests of a castle on a copy of a real save with the total conserved and
counts that match the vanilla container panels. Run on the isolated test server only, with a copy
of the save made for this test.

1. Deploy Core and `Progenitor.Sample` to the test server ([dev-loop.md](dev-loop.md)), join with
   an admin character, and stand inside a castle that has at least two chests.
2. `.items chests`. Expect one reply per row, `#1 <entity> c-<id> "<name>" <prefab> slots=<n>`,
   and the same rows in the server log as `Sample items: chest #1 …`. Without a client, the
   bridge `castles` op (`{what: "buildings", plot: <territory>}`) lists the same entities.
3. Pick a source chest holding Iron Ore: `.items count #1 Item_Ingredient_Mineral_IronOre`. If it
   holds fewer than 50, `.items add #1 Item_Ingredient_Mineral_IronOre 60` first. Note both
   chests' counts and open each chest's vanilla panel to compare.
4. `.items move #1 #2 Item_Ingredient_Mineral_IronOre 50`. Expect the reply
   `Moved 50/50 Item_Ingredient_Mineral_IronOre. #1: A -> A-50, #2: B -> B+50 (Complete)` and the
   log line `Sample items: move from=#1 to=#2 item=<guid> requested=50 moved=50 outcome=Complete
   from_before=A from_after=A-50 to_before=B to_after=B+50 total_before=A+B total_after=A+B steps=<k>
   compensated=0`. The vanilla panels show the same counts.
5. Conservation checks: fill the target so only part fits and repeat (expect `Partial` with
   `total_before == total_after`); move into a refusing target such as the heart
   (`.items move #1 heart … 1`, expect `Nothing … refuses this item`). No `Inventory: transfer …
   could not be restored` error appears.
6. Gear into an owner that can wear it: put one armor piece in chest #1 (`.items add #1
   <armor prefab> 1`), take off the matching piece on your character, then `.items move #1 bag
   <armor prefab> 1`. Expect `Moved 1/1 … (Complete; 1 equipped by the target's owner)`, the piece
   worn and no copy left in the chest (`.items count #1 <armor prefab>` is 0). Repeat into a
   servant with an empty matching slot (`.items move #1 <servant index:version> <armor prefab> 1`,
   the entity from the bridge `castles`/`get` ops): the servant wears it and the chest has none.
   Then with the slot filled: the piece lands in the bag or servant inventory instead.
   **Weapon:** with an empty weapon slot, `.items move #1 bag <weapon prefab> 1`. Expect `Moved 1/1`
   and `Complete`, never `Inconsistent`: whether the game also keeps the wielded weapon in an
   action-bar slot or not, it is counted once (`equipped` is 1 only when it left the bag). Then two
   weapons of one prefab with one free slot: `Moved 2/2`, one wielded and one in the bag.
7. Ids: note `.items id #1`, restart the test server, rejoin, `.items id #1` again (same id,
   status `Live`); destroy a chest, wait two seconds, `.items id c-<old id>` (expect `was
   destroyed`); place a new chest on the same tile (expect a new id).
8. Optional: `.items watch #1`, move items by hand in the panel, expect `Sample items: watch #1
   Changed`; `.items heart`, `.items clan`, `.items name #1 "Ore"` (the panel shows the name).
9. Clean up: remove the Sample from the test server's reload folder and confirm the clean unload.

## Item catalog

`ctx.Items.Catalog()` is a read-only list of every item the world knows, for pickers, category
rules and settings pages: `IReadOnlyList<ItemCatalogEntry>` with `PrefabGuid`, `PrefabName`,
`DisplayName`, `MaxAmount`, `ItemCategory`, `ItemType` and `IsCustom`. `ctx.Items.CatalogEntry(guid)`
looks one up from the same build; `ctx.Items.CatalogInfo()` reports the last build. Status:
**landed** (source-checked against the game's metadata, built, game-free tested in
`ItemCatalogTests`); not live-verified. Signatures: [api.md](api.md#item-catalog).

```csharp
foreach (var item in ctx.Items.Catalog())
    if ((item.ItemCategory & someCategoryBits) != 0 && item.MaxAmount > 1)
        Log.LogInfo(item.PrefabName + " " + (item.DisplayName ?? "(no name on this side)"));

var ore = ctx.Items.CatalogEntry(1332980397);   // Item_Ingredient_Mineral_IronOre, or null
```

**Where it comes from.** A prefab-inclusive query for entities with `ItemData`, `PrefabGUID` and
`Prefab` (so item copies sitting in inventories are not visited), then
`GameDataSystem.ItemHashLookupMap` for the stack size, category and type, the same table
`ctx.Inventory` and the game's own add and move code read. A prefab missing from that table is
not listed. Prefab names come from the prefab collection's lookup map. Custom items cloned with
`DefineItem` or a data pack are included on each side once their clone is applied there
(`IsCustom` true).

**Display names.** The client resolves `ManagedItemData.Name` through the game's string table,
the resolver chat item links already use, so names follow the client's language. The dedicated
server ships no localization tables, so a vanilla item's `DisplayName` is null there; Progenitor
does not load tables on the server. A custom item's `Named(...)` text is its display name on both
sides. Show names from the client, or send GUIDs and let the client name them.

**When it builds.** Lazily, on the first call after a world load; the result is cached for that
world, its world-load epoch and the content version (bumped when a custom item is applied or
removed on that side). Repeated calls return the same list object and allocate nothing. Before the
world's prefabs exist (main menu, early join) the result is empty and not cached, so a later call
builds. On the client, a call made while the join is still loading prefabs could cache a partial
list for that world; call it once the local character exists (for example when a panel opens), or call
`InvalidateCatalog()` first. A vanilla stack-size or category edit made after the first build in the same world load is
picked up by `ctx.Items.InvalidateCatalog()` or the next load. Main thread only.

**Cost.** One prefab query plus one lookup-table read and one name lookup per item, once per world
load; the entry count is a few thousand. The measured count and build time (`CatalogInfo().Count`,
`BuildMilliseconds`) are recorded by the live acceptance below; until then they are unmeasured.

**Sample and bridge.** `.items catalog [filter]` (admin, `SampleItemCatalog.cs`) prints the summary
and up to eight matching rows on the server; the filter is a GUID or a case-insensitive part of the
prefab or display name. The Dev bridge op `item_catalog` returns the same data from either side,
which is how to see the client's display names.

### Live acceptance (procedure, not run)

On the isolated test server with Core, Dev and `Progenitor.Sample` deployed, an admin client joined:

1. Server: `.items catalog`. Expect `Item catalog (server): N items, C with display names, C custom,
   built in T ms. N match.` with N in the low thousands and C equal to the number of named custom
   items (0 without a data pack), and the log line `Sample items: catalog side=server count=N named=C
   custom=C buildMs=T builds=1 filter=`. A second `.items catalog` logs the same `builds` value.
2. `.items catalog IronOre`: a row `1332980397 Item_Ingredient_Mineral_IronOre stack <n> type <t>
   cat 0x<c>` with no quoted name; `.items catalog 1332980397` returns the same row. Compare the
   stack with the vanilla tooltip.
3. Client: `node tools/mcp/call.mjs client item_catalog '{"filter":"Iron Ore"}'`. Expect
   `side: "client"`, `count` equal to the server's N, `withDisplayName` close to `count`, and an
   entry `{prefab: "Item_Ingredient_Mineral_IronOre", display: "Iron Ore"}` with the server's
   `maxAmount`. Repeat with `"Copper Ore"` and `"Blood Essence"`.
4. `{"rebuild": true}` on both bridges: record `count` and `buildMs` for each side in the Cost
   paragraph above.
5. Restart the test server (or load another save) and call `item_catalog` again: `epoch` changes and
   `builds` rises by one on the first call only.
6. With a data pack that defines a named item: that item appears on both sides with `custom: true`
   and its own name.
7. Clean up: remove the Sample from both reload folders and confirm the clean unload in the logs.

## Main thread

Research notes (internal): `MainThread` ownership, collectible-ALC sweep.

`Progenitor.Dev.MainThread`. Queue work that must run on the game's main thread (worlds, entities, systems). Drained once per frame from Core's `FrameTicker`. The debug bridge's socket thread uses this; a reloadable plugin that posts from a worker thread must too.

```csharp
MainThread.Post(() => DoWorldWork(), owner: ctx.Owner);
MainThread.PostDelayed(2.0, () => DoWorldWork(), owner: ctx.Owner);
var n = await MainThread.Run(() => CountUsers(), owner: ctx.Owner);
if (MainThread.IsMainThread) { /* already on the drain thread */ }
```

`Post(Action, owner?)`, `PostDelayed(seconds, Action, owner?)`, `Run<T>(Func<T>, owner?)`. Pass **`ctx.Owner`** (the BepInEx plugin instance) so the reloader can drop the work when this plugin unloads. A null owner still tags the item with `action.Method.DeclaringType.Assembly`'s **full name** (a string, never a live `Assembly` — that would pin a collectible load context), so an unowned `Post(() => …)` from a reloadable plugin is still dropped with that assembly. The **delegate's** collectible context is always recorded, even when `owner` is a Core object: a plugin lambda posted with a Core owner is dropped when that plugin unloads. Core-owned delegates (declaring type in a non-collectible context) are not retired by a plugin unload.

`Run<T>` after the owner is retired returns a faulted task (`MainThread work was dropped because its owning plugin is unloading`) instead of hanging a waiting thread. Each drained action is its own try; an exception is logged and does not stop the rest. Delayed work is checked once per frame.

## Hosted

Research notes (internal): the child `VRisingServer.exe`, its argv template and wait constants; joining a dedicated / test server from the main menu through `GameHelper.LaunchGame` (verified live); the private-game field map for the debug-bridge `host` op. The `connect` / `host` / `disconnect` ops, IPv4 address forms, and launch-in-flight guard are in [bridge-ops-player.md](bridge-ops-player.md#connect).

`Progenitor.Hosted.HostedServerLauncher`. Client-only. The bundled private / single-player game is a child `VRising_Server/VRisingServer.exe`, not an in-process server; without this it has no BepInEx loader. `Plugin.Load` calls `Install` on the client (no-op in a server process).

A Harmony prefix on `StunProcess.Start` / `StunProcess_PInvoke.Start` / `StunProcess_MONO.Start` watches for a file name ending in `VRisingServer.exe`, then `Staging.EnsureStaged(serverDir)`:

**Which prefix, though** (job FHARM7). Production is Bootstrap's typed `StunProcessStartPrefix` / `StunProcessStartFinalizer` slots and pins nothing. `TryPatchStart`'s own `Harmony.Patch` is a prefix/finalizer pair joined by `__state` — the Enter bit that says whether *this* `Start` entered the Doorstop-strip nest, and a finalizer that Leaves a nest it did not Enter corrupts the depth for real spawns — which the slot pool's `(__instance, __args)` shape cannot carry. So it is no longer a general fallback: with Bootstrap present but not owning the site the module logs `patches: refused direct fallback hosted StunProcess.Start reason=needs-direct-shape …` and stays uninstalled (update Bootstrap, then restart both); with Bootstrap absent it patches directly and names the Core generation it pins. See [Patches](#patches).

- Copies the Doorstop shim (`winhttp.dll`) and rewrites `doorstop_config.ini` with absolute client paths (`target_assembly`, `coreclr_path`, `corlib_dir`).
- Junctions (else symlink, else copy) `core`, `patchers`, `unity-libs`, `plugins`, `progenitor-reload` from the client's BepInEx. `config`, `cache`, and `interop` stay local so the child generates its own interop.
- Idempotent stamp against the client's `BepInEx.Core.dll` write time. Refuses to write into the client game root or the client's BepInEx tree.
- On first such spawn, stretches `ClientBootstrapSystem.TIME_TO_WAIT_FOR_LOCAL_SERVER_STARTUP` ×4 and `MAX_CONNECT_RETRIES` ×2 (`RETRY_DELAY` unchanged) so staging can finish before the client gives up.

Config (`[Hosted]` in the shared `progenitor.cfg`; see [Config](#config)). An old `progenitor.hosted.cfg` is no longer read; Core logs one warning at install when it exists:

| Key | Default | Meaning |
|---|---|---|
| `Hosted.Enabled` | `true` | When false, the Start prefix does not stage. |
| `Hosted.DryRun` | `false` | Log the work without writing. |

`HostedServerLauncher.Describe()` is the diagnostic string the `status` op prints. `Staging.LastSummary` is the last staging report.

**Verified live 2026-09-03** (evidence 29 and 34): a private-game launch with `HostDedicated=True` spawned the bundled `VRising_Server` child; staging wrote Doorstop + BepInEx junctions; clearing inherited `DOORSTOP_*` around `StunProcess.Start` let the child load Core + Sample; the client handshake passed. `DryRun=true` still logs what would be written without touching disk.

## Collect before world load

Research notes (internal): CoreCLR crash triage of 2026-10-04 (22:58 join hang).

Client only. A join froze when a .NET garbage collection, started on IL2CPP's finalizer thread through Il2CppInterop's finalizer hook, was still running when Unity's asset unload stopped the IL2CPP world; every thread that then entered managed code waited for that collection forever. As a mitigation, `Progenitor.Networking.GcBeforeLaunch` runs a full blocking .NET collection, waits up to 2 s for pending .NET finalizers, and collects again, immediately before each `GameHelper.LaunchGame`. This makes an in-flight collection during the load less likely; it does not remove the race.

**Live (2026-10-04, Steam client joining the isolated test server, Core from this change).** At install the client logged `GcBeforeLaunch: armed on Bootstrap's GameHelper.LaunchGame slot; every world load collects first.` In three joins in a row each logged one collection right before the load:

| Join | Time | Gen2 count | Heap |
|---|---|---|---|
| 1 | 90 ms | 36→38 | 364.9→126.1 MB |
| 2 | 31 ms | 38→40 | 192.7→83.8 MB |
| 3 | 35 ms | 40→42 | 138.6→80.8 MB |

All three handshakes were ok. The Dev bridge's `host_capture` LaunchGame prefix still ran (chained behind Core).

Not yet shown:
- Whether it prevents the join hang. The hang is intermittent, so this needs time.
- A join from the game's own server list, which goes through the same patched `LaunchGame`.

| Key | Default | Meaning |
|---|---|---|
| `[Diagnostics] CollectBeforeWorldLoad` | `true` | Collect before every world load. Read at each launch; set `false` if the join stutters. |

**Coverage.** Bootstrap already patches `GameHelper.LaunchGame` on every client (no Bootstrap change). Core fills that plugin slot at install, so every launch is covered, including the game's server list, Direct Connect, Private Game, the bridge's `connect` / `host` and JoinFlow's rejoin. Core's dispatcher runs a chained plugin handler first (Dev's `host_capture` binds the same site through `HarmonySlotHandoff.TryBindPlugin`), then collects. If the slot cannot be armed, because Bootstrap lacks it or another owner bound it first, only Progenitor's own `connect`, `host` and rejoin collect.

Leaving a world (`ExitGame`, then the main-menu scene) also unloads assets, but Bootstrap has no slot there. Leave is therefore not covered.

Log lines (source `Progenitor`):

```
GcBeforeLaunch: armed on Bootstrap's GameHelper.LaunchGame slot; every world load collects first.
GcBeforeLaunch: LaunchGame slot not armed (<reason>); only Progenitor's own connect/host/rejoin collect.
GcBeforeLaunch: collected before LaunchGame in <ms> ms (gen2 count <a>-><b>, heap <x>-><y> MB)
GcBeforeLaunch: skipped before LaunchGame (<site>): [Diagnostics] CollectBeforeWorldLoad=false
```

A `[finalizer wait exceeded 2000 ms]` suffix means a .NET finalizer was still running after the bounded wait. The launch continued.

**Status:** source-checked, built (Debug and Release) and covered by game-free tests (`GcBeforeLaunchTests`). Not verified live: no join has been observed with this change, and it has not been shown to prevent the hang.

## Native

Generator: `tools/addresslib/build.mjs`.

`Progenitor.Native.AddressLibrary`. Versioned lookup of native function pointers by a stable metadata method name (`namespace.Type$$Method`, generics kept; overloads append `#` + TypeSignature). The JSON is per side and per GameAssembly.dll build (`{fileSize}-{peTimeDateStamp hex}-{peCheckSum hex}-{sha256 of the first 64 KiB, 16 hex chars}`; stamp and checksum are 0 when the bytes are not a PE). `TryResolve(id, out address)` is **image base of the loaded GameAssembly module + relative address**. A relative address that is negative or not strictly inside the loaded module returns false. Missing ids, a missing table, and a build mismatch return false and log; they never throw. A failed load is **not latched**: the next `TryResolve` retries so a matching JSON dropped later can succeed without a restart. `Attach` / `Reset` are internal (tests only); a plugin cannot point Core at chosen addresses.

Every detour site has a stable id in `AddressId` (`ClientOnReliableEventsReceived`, `ServerOnDataReceivedServer`, `ManagedDataFindEntry`). `DetourSite.Resolve(site, id, prefixFallback)` is the install path: **table first**, then `MethodPointerByPrefix` **only when the table for the running build is missing**. A loaded table that lacks the id does not fall back (that is a wanted-list gap). One log line per site names the path and relative address (`site=receive.client id=… path=table|fallback offset=0x… address=0x…`). When both fail the detour is not installed and one error names the site, the build id, and `node tools/addresslib/build.mjs --side {side}`. Bootstrap `ReceiveDetours` uses the same order against disk JSON next to Core (it cannot compile-reference Core at JMP-install time) and records pipe lines on public `ReceiveDetourPing.NativePingLines` so collectible Core can merge them by reflection.

JSON is loaded only from Core-controlled locations, **embedded resources first**, then the exact `{side}-{build}.json` next to Core in `BepInEx/progenitor-core/` (or in that folder's `addresslib/` subfolder). `PROGENITOR_ADDRESSLIB_DIR`, `BepInEx/plugins`, and the game root are not search paths. After parse, `table.Side` and `table.Build` must equal the running process; a planted file whose *name* matches but whose fields do not is refused. If `GameAssembly.dll` is not in the module list yet, the load is not marked attempted so a later frame retries.

Ping/status `native` payload (Core, no Dev field required): `AddressLibrary.DescribeForPing()` / `AddressLibrary.Describe()` / `NativePingSnapshot.ToStatus()`. Shape: `side`, `build`, `tablePresent`, `status`, `line`, `sites[]` (`site`, `id`, `path` = table|fallback|missing, `offset` (the image offset)). Grep line: `native side=client build=… table=yes|no sites=receive.client:table@0x…`. Bootstrap receive outcomes are merged in.

```csharp
var resolved = DetourSite.Resolve(
    AddressId.SiteReceiveClient,
    AddressId.ClientOnReliableEventsReceived,
    () => Il2CppNative.MethodPointerByPrefix(typeof(ClientBootstrapSystem), nameof(ClientBootstrapSystem.OnReliableEventsReceived)));
if (!resolved.Ok) return; // already logged; do not CreateAndApply
```

Rebuild after a game patch, then confirm the game's metadata still matches the table:

```
node tools/addresslib/build.mjs --side client
node tools/addresslib/build.mjs --side server
node tools/addresslib/build.mjs --side client --verify
node tools/addresslib/build.mjs --side server --verify
```

That reads the method-address listing in the game's metadata for `<side>` and fingerprints `_tools/bin/<side>/GameAssembly.dll` (size + PE TimeDateStamp + PE CheckSum + SHA-256 of the first 64 KiB), then writes `tools/addresslib/<side>-<build>.json` for names listed in `wanted-<side>.txt` (substring or exact). `--verify` does not write: it prints `OK` / `DRIFT` / `MISSING` / `EXTRA` per wanted id and exits 1 on drift or missing. Ids are assigned against the **full** ScriptMethod list, then the wanted subset is emitted — expanding the wanted list later does not rename a previously unique `Type$$Method` that had other overloads in the metadata. `--all` writes every ScriptMethod. Core embeds those JSON files. The listing's `Address` values are relative addresses (a value ≥ `0x180000000` is treated as a VA and the image base is subtracted).

patches: apply swept prefix X owner=x (best-effort unpatch of a failed apply)
patches: apply sweep FAILED prefix X owner=x: <message> (a HarmonyX-side remnant may pin the collectible ALC)
patches: group refused owner=progenitor.godmode member=godmode.killentity rolled-back=1 of=2 (nothing registered)
`AddressId` / `AddressTable` / `DetourSite` are the game-free half (id assignment, build id, JSON parse, table-then-prefix order) so the unit tests and the generator cannot drift. `KeyTableProbe` is the Core home of the spike's observe-only FindEntry detour; it is opt-in (`Install` returns `IDisposable`) and uses `DetourSite.Resolve`.

## Patches

Job FHARM. Design, prior art and the collectible-ALC pin analysis are in the internal research notes. Namespace `Progenitor.Patches`, sources in `src/Progenitor.Core/Patches/`.

`ctx.Patches.Prefix(target, prefix, priority)` / `.Postfix` / `.Finalizer` returns a `PatchHandle?`. Every entry is tagged with the plugin GUID; `PluginReloader.SweepRegistrations` calls `PatchRegistry.RemoveAll(plugin)` on **every** unload reason, and `Plugin.Uninstall` calls `Patches.Uninstall()` — which unpatches everything left, Core-generation-owned included — before the ALC unload.

**The unpatch is not the guarantee (job FHARM5 corrects FHARM).** FHARM's design rested on "a patch pins the ALC *for as long as it is applied*, so a guaranteed unpatch removes the pin". That is measurably false. On .NET 6 with HarmonyX 2.10.2 — the runtime and the version the game runs — a patch applied from a collectible ALC and then `Unpatch`ed still leaves the ALC alive after `Unload` and repeated GCs, against a control that unloads cleanly. The root, found by walking the Harmony/MonoMod statics after the unload:

```
HarmonyLib.Internal.RuntimeFixes.StackTraceFixes.RealMethodMap[key]
  .m_methodHandle.m_keepalive.m_scope.m_tokens[].m_value -> MethodBase P.Patcher::Pre
```

`RealMethodMap` is a static `Dictionary<MethodBase, MethodBase>` written on every detour apply (`RealMethodMap[val.Target] = val.Method`) and never cleared; its key is the generated replacement, whose token scope keeps the patch method — and its ALC — alive. This fires for **every** target, managed or IL2CPP. For an IL2CPP target `Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher.DelegateCache` (a static `List<object>` with exactly one `Add` and no removal anywhere in the assembly) pins it a second time. The full write-up, probe source and control are in the internal research notes.

So `RemoveAll` still matters — it is what stops a dead ALC's code from being *called* — but it is **not** what frees the ALC. The generalized rule is stronger than the old IL2CPP-only one: **no Harmony patch body of any kind may live in a collectible ALC.**

**Plugin patches therefore go through a Bootstrap slot.** `Progenitor.Bootstrap.PatchSlots` is a fixed pool of **32** slots, each owning six process-lifetime static methods (prefix / postfix / finalizer × instance-target / static-target). `ctx.Patches` parks the plugin's handler in a slot and hands Harmony *that slot's static method*, so every append-only row names `Progenitor.Bootstrap`, which is not collectible. `RemoveAll` unpatches **and** releases the slot; the release is the half that frees the ALC. This is the [Bootstrap slot pattern](#hot-reload) generalized from Core's typed sites to arbitrary plugin patches — the body is in Bootstrap, the behavior is generational.

The price, stated plainly: a pool body is signature-agnostic, so a slot-routed patch sees `__instance` and `__args` and **has no `__result`**. A patch that needs the return value is not expressible through a slot; use a Bootstrap-owned typed site (`HarmonySlotHandoff`) instead. When the pool is full the patch is **refused** — growing it would mean emitting static methods into a non-collectible assembly at run time, which is the problem the file exists to avoid — with `reason=slots-exhausted`. Raising the 32 is a Bootstrap change, so it needs a restart of both.

**The registry is deliberately unload-reason-blind**: unlike a buff instance there is nothing to keep across a `Swap`, because the plugin assembly is going away in every reason and a patch whose method lives in a dead ALC is a crash, not a convenience.

**Core's own patches go through the same pool, under one owner id** (job FHARM6, R40 finding 1). Core is collectible too, so a Core-owned direct patch pins the Core generation exactly as hard as a plugin's pins the plugin's — and the world-load epoch postfix was not opt-in, so *every* server was paying it and every `.corereload` leaked a generation while `patches: Uninstall removed=1 left=0` read clean. The world-load postfix and god mode's three kill prefixes are now slot-routed under `<guid>.core`: one id, because they share one lifetime (this Core generation) and one sweep, so Core's `patches:` lines grep together. God mode keeps its typed prefix bodies and adapts them onto `__args` at registration time through `SlotArgs` / `PatchRegistry.ArgIndex` (job FHARM7 lifted that mechanism out of `GodMode.cs`, which references `ProjectM` and cannot be linked into the game-free tests — R41 noted its fail-closed claim had no test; `SlotArgsTests` is that test), resolving each argument by the original's own **parameter name** — the binding Harmony was doing — and writing the 11-arg `KillEntity`'s `ref Health` back into `__args`; a name that is not on the original refuses the whole group rather than reading an argument by a guessed position.

`PatchRegistry.Prefix` / `.Postfix` / `.Finalizer` — the direct path — still exist, for tests and for a caller that has measured that it can accept a permanent pin, but every direct registration now logs `patches: WARNING <kind> <target> owner=<id> is a direct patch: …`, so the leak is one grep rather than a silence.

**No unslotted Core patch survives a present Bootstrap** (job FHARM7). FHARM6 audited the direct `Harmony.Patch` calls left outside the registry and listed them; listing is not a mitigation. Each was a "fallback taken when Bootstrap does not own the site", which a Bootstrap one version behind takes on **every** boot, silently pinning the generation. `CorePatchFallbackPolicy.Decide(slotShapeFits, bootstrapPresent)` is now the one rule, and every one of those call sites asks it:

| Site | Shape | With Bootstrap present | With Bootstrap absent |
|---|---|---|---|
| `EventHooks` `ChatMessageSystem.OnUpdate` | `__instance` only (and the body takes its `EntityManager` off the server world, so not even that) | **slot**, owner `<guid>.core` | direct + warning |
| `EventHooks` `ServerBootstrapSystem.OnUserConnected` | `__instance` + `netConnectionId` | **slot**, owner `<guid>.core` | direct + warning |
| `EventHooks` `ServerBootstrapSystem.OnUserDisconnected` | `__instance` + `netConnectionId` + `connectionStatusReason` | **slot**, owner `<guid>.core` | direct + warning |
| `HostedServerLauncher.TryPatchStart` | prefix + finalizer joined by `__state` (the Doorstop-strip nest Enter bit) | **refused**, site left unpatched | direct + warning |
| `HarmonyFallback.TryPatchManagedSystemUpdate` | prefix + postfix joined by `__state` (the call frame) | **refused**, system hooks off | direct + warning |
| `HarmonyFallback.TryPatchUnmanagedSystemUpdate` | prefix + postfix joined by `__state` | **refused**, system hooks off | direct + warning |
| `HarmonyFallback.PatchTick` | `PatchAll` on a tick postfix | n/a — `Plugin.cs` already calls it only under `if (!bootstrap)` | direct + warning |
| `PrefabRegistry.BindDeserializeSites` (`LoadPersistenceSystemV2.DeserializeLoadFile`) | prefix + postfix, **no arguments**, nothing carried between them | **slot ×2**, owner `<guid>.core` | direct + warning |
| `Localization.EnsureHooked` (`GameLocalization.SetLanguage`) | postfix, one by-value argument bound by name | **slot**, owner `<guid>.core` | direct + warning |
| `ModSave.BindSaveSites` (`ServerWorldManager.BeginCreateServerWorld`) | postfix, one by-value argument bound by name | **slot**, owner `<guid>.core` | direct + warning |
| `ModSave.BindSaveSites` (`TriggerPersistenceSaveSystem.TriggerSave`) | postfix whose entire body is a test of **`__result`** | **refused**, site left unpatched | direct + warning |

The slot-routed three bind their arguments **by the original's own parameter name** (`PatchRegistry.ArgIndex`), the binding Harmony was doing; a name that is not on this build leaves the seam unpatched with a line naming the parameter, rather than reading an argument by a guessed position. **A refused route turns a module off, and the log says so at the moment it matters** (job FHARM8, R42 finding 5). `Refused` leaves a seam unpatched, and two modules used to report success anyway. `SystemHooks.Install` logged "installed Harmony patches on SystemBase.Update and WorldUnmanagedImpl.UnmanagedUpdate" unconditionally — the last word in the log on the subject, and false — so it now names which of the two seams actually armed and follows a partial or total failure with an explicit "system Before/After hooks are OFF" line. `HostedServerLauncher` returned before `_installed` with `Enabled` still reading on, so the refusal was one error line at boot and the consequence was a child spawned without the Doorstop strip minutes later; it now latches the refusal, exposes `Armed` (`Enabled` **and** the Start sites hooked) beside `Enabled` in `Describe()`, and logs one line saying the launcher is disarmed, what pressing Play will actually do, and that the fix is a newer Bootstrap plus a restart of both.

The two lines the rule writes:

```
patches: WARNING direct fallback events ChatMessageSystem.OnUpdate: Progenitor.Bootstrap is absent, so HarmonyX is handed a Progenitor.Core method and pins Core generation 7 ...
patches: refused direct fallback hosted StunProcess.Start reason=needs-direct-shape: the slot pool cannot carry this site's shape ... update Bootstrap so it owns the site, then restart both.
```

`ctx.Harmony` was the last unslotted plugin-facing path and is gone; see [Hot reload](#hot-reload) for `ctx.UnsafeHarmony`, which replaces it.

**The last three fallbacks are routed** (job FHARM8). `PrefabRegistry`, `Localization` and `ModSave` each took the same "Bootstrap did not own the site" fallback unconditionally, as a `PatchAll` over an attributed hook type — so each was a live, unconditional pin of the Core generation on any host whose Bootstrap is a version behind. Each hook type is now unpicked into a slot body and asks `CorePatchFallbackPolicy` like everybody else (the four new rows above). Two consequences worth stating rather than discovering:

* **`TriggerSave` is refused, not routed.** Its whole body is `if (!__result) return;` and a slot body never sees `__result`. With Bootstrap present the save hook is off for that Core generation: mod stores are still written on `Uninstall` and on player disconnect, but **not on each autosave**. The fix is a Bootstrap that owns the typed site, then a restart of both — not a silent permanent pin.
* **The slot-routed arguments are bound by the original's own parameter name** (`PatchRegistry.ArgIndex`), so `SetLanguage`'s `language` and `BeginCreateServerWorld`'s `saveConfiguration` move with a game update that inserts a parameter, and a *renamed* one leaves that seam unpatched with a line naming it. `PrefabRegistry`'s pair needs no arguments at all; its prefix and postfix are registered independently so losing the diagnostic postfix cannot cost the prefix that makes modded GUIDs resolve out of the save.

**The pool budget: 32 slots, 12 of them reserved for Core** (job FHARM7, R41 finding 2). `Bind` is first-come, and Core arms **lazily** — god mode's three kill prefixes on the first `.godmode`, the world-load epoch postfix on the first world load, the event fallbacks only against a Bootstrap that does not own their sites. A plugin that patched 32 things at load could therefore decide, hours later and silently, that god mode cannot arm: its three prefixes are an atomic group, so two free slots are as good as none. Quarantine made it worse, being one-way attrition against the same 32.

| Constant | Value | Meaning |
|---|---|---|
| `PatchSlots.Capacity` | 32 | Total slots. Fixed; growing it at run time is the problem the pool exists to avoid. |
| `PatchSlots.CoreReserve` | 12 | Slots no plugin may take. Twelve Core-owned registrations, **enumerated** in `Progenitor.Patches.CorePatchBudget.Sites`: world-load postfix ×1, god mode ×3, event fallbacks ×3, FHARM8's four (`DeserializeLoadFile` prefix + postfix, `SetLanguage`, `BeginCreateServerWorld`), and the server's join-chat prefix on `OnUserConnected` ×1 ([server-list-description.md](server-list-description.md#the-description-in-chat)). The spare is used; the next Core patch needs a raise. The event and FHARM8 fallbacks arm only when Bootstrap does not own their sites, so a normal server uses fewer. `PatchSlotBudgetTests` asserts `CoreReserve >= CorePatchBudget.Count`, so a new Core patch that is not paid for is a red build rather than a guard that silently refuses to arm hours into a session. Raising it is a Bootstrap change: restart both. |
| `PatchSlots.NonCoreCapacity` | 20 | What every loaded plugin shares. |
| `PatchSlots.LowFreeWarnThreshold` | 4 | Warns once per crossing — measured on **`NonCoreFree`**, the number a plugin bind is actually refused against (job FHARM8, R42 finding 2). It used to be measured on total `Free`, which sits *above* `CoreReserve`, so the warning that exists to precede the first plugin refusal could only fire after it. The whole pool is watched on a second latch, so Core eating its reserve is not silent either, and the warning is now raised from the **refusal** path as well as the success path. |

Rules: an owner id ending `.core` may use any free slot; anyone else is capped at `NonCoreCapacity` and is refused with one of **three** separate reasons, because the three have different fixes: `slots-exhausted` (every slot is held — unload a plugin), `slots-reserved` (slots are physically free but they are Core's — unload a plugin, or raise the reserve), and `slots-lost-to-quarantine` (job FHARM8, R42 finding 2: the plugin budget is gone because slots were **permanently stranded by unpatches that threw**, so unloading a plugin frees nothing and the only remedy is a restart). Each refusal line now carries its remedy sentence (`PatchSitePolicy.SlotRefusalRemedy`) rather than leaving it in a doc comment — the third one is precisely the case where the other two's advice does nothing. **Quarantine attrition is charged to the plugin budget first**, so a lost slot shrinks what plugins may take before it touches the reservation, and every quarantine warns — including the line that says the attrition has reached the reserve, the state in which a Core seam can be refused at the next Core install. `Describe()` carries the whole budget: `patch-slots: in-use=5/32 quarantined=0 faults=0 core=2 plugin-free=18/20 reserve=12`. The pool raises at most one pending warning, which the registry drains and logs after each `Bind`/`Quarantine` — a string, not a callback, because a delegate handed to the non-collectible Bootstrap would pin the Core generation this whole design exists to free.

**A slot is addressed by a token, not an index, and a slot whose unpatch threw is quarantined** (job FHARM6, R40 finding 2). `Bind` returns the index in the low 8 bits with the slot's **generation** above it, and every later address of that slot — `Release`, `Quarantine` — carries the generation it bound, so a stale registry entry can never release, or take over, a slot that has since been handed to somebody else. `Release`, `ReleaseOwner` and `ReleaseAll` all bump the generation.

A generation cannot be checked on the *dispatch* path, and the docs should not pretend otherwise: Harmony is handed `Prefix_I00`, which is compiled once and calls `Pre(0, …)` forever, so a detour that survived a failed unpatch carries no token and nothing inside the pool can tell it from a live call. That is what the quarantine is for. When `Undo`'s unpatch throws, the slot is emptied (the release, not the unpatch, is what frees the ALC) but is **never rebound this process**: `Bind` hands out the lowest free index first, so recycling it would turn the stranded detour into a live call into the next owner's body, with the old target's `__instance` and `__args` — the next plugin's prefix running, and possibly returning `false`, on a method nobody patched. The lost slot is charged against the 32 and shown in `patch-slots: in-use=3/32 quarantined=1 faults=0`, so exhaustion arrives visibly instead of the loss being absorbed.

**Refusals (fail closed).** Before applying, the target is resolved: the **address library first** for a target given an id with `PatchSites.Register` (`DetourSite.Resolve` → `path=table`, else the interop `NativeMethodInfoPtr_*` prefix fallback), otherwise `Il2CppNative.MethodPointerByPrefix` on the declaring interop type (`path=fallback`), otherwise `path=unknown` — which is allowed, since almost no method has a table entry. Two classes of target are then refused outright:

* the **shared empty-body thunk** — the AOT compiler deduplicates every empty method body onto one native address per side (48727 methods share it on the client in v1.1.13, 49458 on the server). Detouring one detours all of them; this rule is enforced in code;
* **Burst jobs** — a `[BurstCompile]` job's IL2CPP `Execute` is not the body that runs (it is in `lib_burst_generated.dll`; see FDR §3 on `StatChangeSystem.ApplyChanges_Execute`).

**What "fail closed" means, exactly.** A refusal, a null target and an apply that **throws** all return `null` and leave **the registry** holding nothing: no entry, no handle, nothing in `Count`, nothing for a later sweep to trip over. That is a claim about this registry and **not** about HarmonyX's internal state. `Harmony.Patch` records the patch against the original's `PatchInfo` in the process-lifetime `0Harmony` assembly *before* it compiles and installs the replacement, so an apply that throws part-way can leave a remnant the registry never got a token for — and that remnant would pin the collectible ALC while `patches: Uninstall removed=0 left=0` still read clean. The registry therefore follows every throwing apply with one **best-effort `Unpatch(original, patchMethod)`** (the two things it still holds; a no-op when HarmonyX has nothing recorded) and logs which way it went: `apply swept` when the undo came back clean, `apply sweep FAILED … (a HarmonyX-side remnant may pin the collectible ALC)` when it did not. The second line is the one honest statement of the gap, and a live `pin-diag Harmony core-id patches=0` after a deliberately-failing apply (described in the internal research notes) is the check that would close it.

**Atomic groups.** `PatchRegistry.All(owner, ownerId, params PatchRequest[])` registers several patches as one unit for a caller whose correctness needs the whole set. The first member that is refused or throws unpatches the ones that already landed (in reverse, each logging its own `patches: unpatch` line), logs `patches: group refused …` and returns `null`; on success it returns one handle per request, in request order. God mode's three kill-path prefixes are the reason it exists — with `Kill` patched and `KillEntity` not, a guarded character is alive, unkillable, and reads as dead to everything that tests `Health.IsDead`.

One line per patch and per unpatch:

```
patches: prefix ProjectM.DeathUtilities::Kill owner=progenitor.godmode path=table offset=0x… priority=800 id=1
patches: unpatch prefix ProjectM.DeathUtilities::Kill owner=progenitor.godmode id=1
patches: refused prefix ProjectM.GameplayInputSystem::GetConsumedInputs owner=x reason=shared-empty-stub offset=0x…
patches: refused prefix ProjectM.SomeType::SomeMethod owner=x reason=slots-exhausted offset=0x…
patches: refused prefix ProjectM.SomeType::SomeMethod owner=x reason=slot-signature offset=?
patches: refused prefix ProjectM.SomeType::SomeMethod owner=x reason=slots-unavailable offset=?
patches: apply failed prefix X owner=x: <message> (not registered)
patches: WARNING postfix ProjectM.SomeType::SomeMethod owner=x is a direct patch: HarmonyX keeps the patch method for the life of the process, ...
patches: slot quarantined index=3 owner=progenitor.sample id=7 (the unpatch threw, so the detour may survive; ...)
patches: RemoveAll owner=progenitor.sample removed=2 left=0
patches: Uninstall removed=2 left=0
```

An unpatch that itself throws logs `patches: unpatch FAILED … (this pins the collectible ALC)` and the sweep continues, so one bad entry cannot strand the rest. `PatchKind` / `PatchSitePolicy` / `IPatchApplier` / `PatchRegistry` are the game-free half (unit-tested without a V Rising install); `HarmonyPatchApplier` is the only file in the module that names a HarmonyLib type, and it uses one `Harmony` instance per owner with id `{ownerId}.patches` so the existing `HarmonyFallback.UnpatchOwnerAndSuffixes(guid)` net in `SweepRegistrations` still catches anything that escapes the registry.

```csharp
var target = typeof(SomeGameType).GetMethod(nameof(SomeGameType.SomeMethod));

// Slot shape: instance is Harmony's __instance (null for a static target), args is __args.
// Return false to skip the original. There is no __result — see the slot note above.
ctx.Patches.Prefix(target, (instance, args) => { Log(args.Length); return true; });

// Or by MethodInfo, if the body has to be `static bool F(object, object[])`:
var prefix = typeof(MyPatches).GetMethod(nameof(MyPatches.Prefix), BindingFlags.NonPublic | BindingFlags.Static);
ctx.Patches.Prefix(target, prefix);   // null = refused or threw; already logged. Unload sweeps it either way.
```

### God mode (debug tooling)

`Progenitor.GodMode`, sources in `src/Progenitor.Core/GodMode/`. The registry's first consumer, and the answer to the buff research's FBUFF5 "described, not implemented".

**Server-only.** `Add` refuses in a client process — before it patches anything — and says so:

```
godmode: server-only; guard refused for entity=45:1 — the kill paths it patches (StatChangeUtility.KillEntity, DeathUtilities.Kill) are server-side, and nothing was patched in this process.
```

Same shape as `ctx.Units` returning a disabled handle, so shared plugin code needs no side check. The kill surface is server-authoritative — the `StatChangeSystem` damage pipeline, `KillOrDestroyEntity` and the death writer all run there — so a client-side guard would keep nobody alive. That is also why **none of the three sites is in `tools/addresslib/wanted-client.txt`**, and deliberately so: an id with no table row degrades to path `unknown`, which still patches but with the **shared-empty-stub guard off** (`PatchSites`' own contract), and neither `KillEntity` overload has a prefix fallback either, since `MethodPointerByPrefix` refuses an overloaded name. Refusing on the side where the guard could not work is cheaper and honest; adding client rows would only have made a useless patch land (R34 finding 14).

**Slot-routed, under `<guid>.core`** (job FHARM6, R40 finding 1). The three prefixes used to be handed to HarmonyX as `GodMode` `MethodInfo`s, which pins this collectible Core generation for the life of the process; being an opt-in debug guard only meant the pin arrived when a developer used it, and the developer who turns god mode on is the same one who then hot-swaps Core. The typed bodies are unchanged — `KillPrefix(Entity killed)`, `KillEntityPrefix(Entity entity, ref Health health)`, `KillEntitySixPrefix(Entity entity)`, each still carrying its own reasoning — but `EnsurePatched` now wraps each one in a slot prefix that reads its arguments out of Harmony's `__args` **by the original's own parameter names**, and writes the 11-arg overload's `ref Health` back into `__args` after the clamp. A parameter name that is not on the original on this build logs `godmode: <method> has no parameter named '<name>' …` and refuses the whole atomic group — god mode stays off, which is the same fail-closed rule the argument-count resolvers already followed. The `ref` write-back through `__args` is Harmony's documented managed contract and has **not** been measured against an IL2CPP by-ref parameter; if it turns out not to reach one, the *clamp* stops working and the *block* does not, and the fix is a typed Bootstrap site for that one target.

`ctx.GodMode.Add(entity)` / `.Remove(entity)` marks an entity the game's unguarded kill paths may not kill. **`Add` returns `false` unless every prefix is live** — it does not add a guard that protects nothing — and logs `godmode: NOT installed (prefixes live=0 of 3); guard refused for entity=45:1 — it would not be protected.` So check the return: the alternative is an admin reading `Debug on.` and dying to the first hit, with the install warnings buried hundreds of lines earlier at plugin-load time. `Describe()` (`godmode: guarded=1 patched=3`) distinguishes "already guarded" from "not installed". `BuffableFlagState.Invulnerable` guards only the stat-change pipeline, and `ProjectM.Immortal` is read on exactly one of the five kill entry points and only while that call passes `killImmortals: false`. Three paths honor nothing at all (FDR §4 rows 4, 7, 8): the 2-arg `StatChangeUtility.KillEntity(em, entity, currentTime, reason)` (hard-codes `killImmortals: true`), `KillOrDestroyEntity` on an entity **without** `Health`, and `DeathUtilities.Kill`, the raw writer.

> **2026-10-07 change: the 11-arg `KillEntity` is no longer patched.** It takes `ref Health` and `in HealthConstants`, and the game's Il2CppInterop (1.4.6-dev, BepInEx `3a12d18`) marshals a by-ref struct through an 8-byte `Ldind_I`/`Stind_I` pair: every call while the patch was installed wrote 8 bytes over `Health.MaxHealth` in the caller's local, which the caller then stored. That is the `MaxHealth = 4.03e20` from the facade smoke. `PatchRegistry` now refuses every target with a by-ref struct parameter (`reason=byref-struct`). The group is `DeathUtilities.Kill`, `StatChangeUtility.KillOrDestroyEntity` (`ProjectM.StatChangeUtility$$KillOrDestroyEntity`) and the 6-arg `KillEntity`. The two `Health` write-back callers that can be prefixed are blocked before their read. The damage pipeline (`ApplyHealthChangeToEntity`, by-ref structs itself) still reaches the 11-arg body: the `Kill` prefix blocks the death and a next-frame repair clears the `IsDead` that its write-back leaves (`godmode: repaired health …`). The table and the clamp text below describe the old group.

Three prefixes, all registered under `progenitor.godmode` at `Priority.First`, applied lazily on the first `Add` **as one atomic group** (`PatchRegistry.All`, so `PatchCount` is 0 or 3 and never in between):

| target | address-library id | why |
|---|---|---|
| `ProjectM.DeathUtilities::Kill` | `ProjectM.DeathUtilities$$Kill` | the choke point all three unguarded paths reach |
| `ProjectM.StatChangeUtility::KillEntity` (11-arg) | `ProjectM.StatChangeUtility$$KillEntity#viiiiidiiifii` | the mechanism: the only body that writes `health.IsDead = true`, and it does so *before* calling `Kill`, so `Kill` alone leaves the character alive but reading as dead |
| `ProjectM.StatChangeUtility::KillEntity` (6-arg) | `ProjectM.StatChangeUtility$$KillEntity#viiidiii` | the 12-caller forwarder; blocking here skips its `Health` read and write-back entirely rather than repairing after them |

`KillOrDestroyEntity` is a **single** method on this build, not a separate no-`Health` overload: its no-`Health` branch reaches `DeathUtilities.Kill` directly and its `Health` branch reaches the 11-arg `KillEntity`, so both are already covered. Each overload is selected by parameter count (11, 6, 4 — unambiguous here), so an added argument in a future build produces one warning and no patch rather than patching a sibling.

**The health clamp is on the argument, at the mechanism** (R34 finding 9). The 11-arg prefix binds `ref Health health` and clamps `health.Value` in place. That covers **all three** of its callers at once, which a per-overload repair does not — the server's native code has exactly these three callers, and every one of them has the same shape: read `Health` into a stack local, hand the 11-arg overload `&local`, write the local back with `SetComponentData<Health>` afterwards.

| caller |
|---|
| `StatChangeUtility.KillEntity` (6-arg forwarder) |
| `StatChangeUtility.KillOrDestroyEntity` |
| `Gameplay.Systems.StatChangeSystem.ApplyHealthChangeToEntity` (the damage pipeline) |

So the caller's copy is authoritative on every path, and a prefix that wrote only the chunk was erased the instant the call returned — on all three. Clamping the by-ref argument makes each caller's own write-back *carry* the restore, including for a caller a future build adds. The chunk write stays as well: harmless where the write-back also lands, and the only repair on a caller that passes a pointer into the chunk and writes nothing back. The 6-arg prefix stays too — skipping the read is still better than repairing after it.

**Half-installed is not a reachable state.** The members fail independently — `Kill` resolves with a plain `GetMethod` and has a prefix fallback; both `KillEntity` overloads are found by walking for a parameter count and have *no* fallback, and the Harmony research names the 11-arg's `ref Health` / `in HealthConstants` signature as the one apply most likely to fail. So the group is registered through `PatchRegistry.All`: if any member is refused, the ones that landed are unpatched (in reverse) before `EnsurePatched` returns, `PatchCount` stays 0, and `Add` refuses. A failed install is **retried on the next `Add`, up to `GodMode.MaxPatchAttempts` (3) times per Core generation** — the address table is read from disk beside Core and `Patches.Install()` runs on the first `Add`, which on a client can be mid-loading-screen — then it stops and says so, rather than re-resolving on every guard forever.

**What a blocked kill leaves behind.** Returning `false` from either `KillEntity` prefix skips `health.IsDead = true`, which is the point — but the damage that got there already drove `Health.Value` to zero or below, and nothing in the skipped body would have clamped it. So those prefixes, which by definition run *before* the original's `IsDead` write, also **restore `Health.Value` to 1 when it observes a non-positive (or `NaN`) value**: a typed `EntityExtensions.Write<Health>` — `ProjectM.Health` is `MaxHealth@0x0`, `TimeOfDeath@0x8`, `Value@0x10`, `MaxRecoveryHealth@0x14`, `IsDead@0x18` (server metadata) — touching `Value` only. Without it the character sits unkillable at zero health, reads as dead to every `Value <= 0` check, shows an empty health bar, and the damage source re-enters the kill path every tick for as long as it persists. One hit point, not a heal: a caller that wants a comfortably survivable character still writes `Immortal` or health itself, which is what the Sample does. The restore logs at most once per entity per 30 s — unlike the blocked-kill line it can genuinely recur, since each new damage source drives health down again.

All three prefixes return `false` for a guarded entity and log **once per entity** (a guarded character standing in the sun would otherwise write a line per tick, per path). Re-adding an entity re-arms that log. The guard key is `index:version`, so a recycled entity index is not guarded **within one world-load epoch** — but `index:version` is *not* reload-proof, because a save reload rebuilds the world from index 0 at version 1 and re-issues `45:1` to whatever the load creates first, plausibly another player's character (R32 finding 6). Every entry therefore also carries the [world-load epoch](#world) it was added in, and an entry from an older epoch is **dropped on its next check** — `IsGuarded`, `ShouldBlock` and `Count` all drop it rather than answer — with one line naming both epochs:

```
godmode: dropped guard on entity=45:1 - stamped in world load epoch 1, the world is now in epoch 2; the save was reloaded and that id names a different entity now. guarded=0
```

`GodModeGuard.SweepStale()` does the whole set in one pass; `Count` calls it, so the number an admin sees is the number of entities actually still guarded. Re-adding the same key in the new epoch is a fresh guard (`Add` returns `true`), not a no-op re-add. `Add` records the plugin so `SweepRegistrations` drops the guard on unload, and `Patches.Uninstall()` clears the whole set: a player left permanently unkillable with nothing able to undo it is the unrecoverable failure (R27 finding 2).

**Guards are reference-counted per (entity, owner)** since FBUFF10 (R40 finding 5). One entity is routinely guarded for two unrelated reasons at once — Core's `.debug` buff and the Sample's `.godmode` debug command guard the same character — and the set used to keep *one* owner per entity with an unconditional `Remove`, so whichever of the two was switched off second silently unguarded a character the other still reported as protected, both replying success. Every entry now holds the list of owners that asked for it: the entity is guarded while **any** owner holds it, `Remove(key, owner)` drops one owner's hold only, and `Add` returns `true` for a second owner taking a hold on an entity someone else already guards (the hold is new, and it outlives the other owner's removal). One plugin can be several owners: `ctx.GodMode.Scope("label")` returns a handle whose `Add`/`Remove` are scoped to `(plugin, label)`, while the unload sweep — which is handed only the plugin instance — takes every label that plugin took and nothing anyone else holds. `ctx.GodMode.Describe()` now names them: `godmode: guarded=1 patched=3 owners: 45:1=[progenitor.debug,sample.godmode]`, because "already guarded" alone never told an admin that their own `off` would leave the entity guarded by someone else. `ctx.GodMode.HoldsGuard(entity)` asks "do *I* hold it" where `IsGuarded` asks "does anyone". The unowned `GodMode.Remove(entity)` overload survives as Core's own unconditional drop; a plugin clearing a guard it took should always pass its owner.

**Every owner carries a plugin identity, so every guard is sweepable** (FBUFF11, R41 finding 7). The unload sweep reaches an owner token only through its plugin reference, so an owner without one was a guard `RemoveAll` could never match — the permanently-unkillable character of R27 finding 2, arrived at from the other direction. `GodModeGuard.GodModeOwner`'s constructor now **refuses** a null plugin (`ArgumentNullException`, "a god mode guard owner must carry a plugin identity so unload can sweep it"), and a null `owner` argument to `Add` / `Remove(key, owner)` / `IsGuardedBy` is normalized to `GodModeGuard.CoreOwner` — a token over the Core-owned sentinel `GodModeGuard.CorePlugin` — rather than recorded as nothing. So for every entry there is an identity and for every identity exactly one sweep: a plugin instance goes with `RemoveAll(instance)`, a token with `RemoveAll(token.Plugin)`, and Core's own hold with `RemoveAll(GodModeGuard.CorePlugin)` or with `Patches.Uninstall()`'s `Clear()`. It still prints as `core` in `Describe()`.

```
godmode: guard added entity=45:1 guarded=1
godmode: blocked kill site=DeathUtilities.Kill entity=45:1
godmode: guard removed entity=45:1 guarded=0
godmode: NOT installed (prefixes live=0 of 3); guard refused for entity=45:1 — it would not be protected.
godmode: prefix group not installed (attempt 1 of 3); nothing is patched, the next Add retries.
```

Core's [`.debug`](#debug-tools-debug) calls it next to its `Immortal` write and clears it on every removal path (`WriteImmortal` / `ClearImmortal`, so `.debug off`, every unload and the 60-frame watchdog all reach it).

The static `GodMode.Add(entity, owner)` takes the owner per call instead of binding it, which is right
in two cases and wrong everywhere else: Core itself (passes `null`, swept by `GodModeGuard.Clear()`), and
a caller that owns every removal path for the guards it takes. It is wrong for a private token, because
the unload sweep is `RemoveAll(pluginInstance)` and it reaches a plain object only by reference equality;
a token the reloader never sees leaves an unkillable character behind after the plugin is gone.
`.godmode static` in the Sample therefore passes `ctx.Owner`; the visible difference from
`ctx.GodMode.Scope("...")` is what `Describe()` prints for the owner (the plugin type's name rather than
a label).

### Debug tools (`.debug`)

**Status: built and game-free tested (`DebugCommandsTests`, `DebugBuffTests`, `DebugBuffGrantUnloadTests`, `DebugMapTests`); not live-verified.** Sources: `src/Progenitor.Core/Debugging/`. Moved from the Sample's `.nodrain` and `.maptp` on 2026-10-06; the protections and the map powers are the same code, so the Sample's earlier live results (for the map powers, 2026-10-02 on the Steam client and the isolated test server: reveal on both sides, the 1 s Middle Mouse hold teleport, an early release sending nothing, `.maptp` with a server read-back and an off-world refusal) describe the mechanism but have not been repeated with Core's module.

Core's built-in admin debug buff. It is not a plugin: Core registers it on both sides at install, and the command on the server.

| Command | What it does |
|---|---|
| `.debug` or `.debug status` | Reads back what is actually applied to your character: buff instance, no blood drain, ignored by foes, survives death, the three `BuffModificationTypes` bits, `Immortal`, the kill-path guard (with the prefix count), and the two map powers. |
| `.debug on` / `.debug off` | Applies or removes the debug buff on your character. `on` on a character that already wears it re-arms the kill-path guard. `off` also removes a leftover `sample.nodrain` instance from before the move. Both end with the status readout. |
| `.debug on <player>` / `.debug off <player>` / `.debug status <player>` | The same for another **online** player, found by whole display name (case-insensitive, one pair of surrounding quotes ignored) in the current world load. The readout goes to you; the player is told in chat when you turn it on or off for them. |
| `.debug tp <x> <z>` | Teleports you to world X/Z, or the nearest valid spot within the search radius, through the map teleport's server path (requires the buff on you). Replies with the requested point, the chosen point and the distance moved. |
| `.debug build on` / `.debug build off` / `.debug build` | **Server-wide** sandbox build mode: the game's own build debug settings for every player on the server. `off` restores exactly what `on` changed; bare `.debug build` (or `status`) reads it back. See [Build mode](#build-mode-debug-build). The bare `.debug` readout ends with a one-line build mode state. |
| `.debug build detached [remove [<count>\|<id>\|all <count>]]` | Lists castle pieces that belong to no castle (probably built under an earlier build mode), or removes yours, one by id, or all, with a count to confirm and no refund. See [Detached pieces](#build-mode-debug-build). |

**Admin only.** Every `.debug` form (`on`, `off`, `status`, a named player, `tp`, `build`) authorizes with the server's admin rule, `ServerAdminList.AuthorizeShared` (decided 2026-10-09: only players on the admin list): on the admin list, authenticated with `adminauth`, or the host of a private game. A listed admin who has not run `adminauth` since the server restarted is accepted. The command is not `adminOnly`, which follows only the game's `User.IsAdmin` (or the host); it carries Core's internal `[AdminListGate]` instead, so the dispatcher refuses everyone else with `This command is admin-only.` before any argument is parsed, the body checks the rule again before it parses, and `.help`, group listings and the chat command hints hide `.debug` from them. Until 2026-10-09 the root `.debug` was `adminOnly` and refused that listed admin (change source-checked, built and game-free tested; not live-verified). The map teleport request is re-checked on the server (admin, character, buff). Its admin check is the server's one admin rule (`ServerAdminList.AuthorizeShared`, as remote installs and server settings use): on the admin list, authenticated with `adminauth`, or the host of a private game. Until 2026-10-08 it read only the game's `User.IsAdmin`, so a listed admin who had not run `adminauth` since the server restarted got `teleport=False` and the bound key did nothing (fix source-checked and built; not live-verified). There is no `[Debug] Enabled` key: nothing here is reachable by a player. The only server-wide effect is [build mode](#build-mode-debug-build), which an admin turns on explicitly.

**What the buff gives.** `progenitor.debug` is a `DefineBuff` clone of `Buff_General_Haste` (shown on the buff bar as **Debug**, with Core's embedded icon) whose prefab pass sets blood drain to zero with an infinite lifetime, persists through death, adds `BuffModificationFlagData` 7 (invulnerable, sun-immune, hazard-immune) and `DisableAggroBuff` `OthersDontAttackTarget`, and strips haste's speed and glow. The pass runs from `PrefabRegistry.AfterApply` on each side the frame the clone is built (a registry `Content.Edit` needs a plugin owner with a handshake declaration). On the wearer, `.debug on` writes `ProjectM.Immortal` and takes the [kill-path guard](#god-mode-debug-tooling) under the owner label `progenitor.debug`, separate from any other owner's hold.

**Never touches an `Immortal` it did not grant.** Each grant is recorded in a ledger keyed by the account (`User.PlatformId`) and the character's name, never an entity id, together with whether `Immortal` was already on. The ledger is the ModSave store `progenitor.debug` (`<save folder>/progenitor/progenitor.debug.json`). The automatic clears (buff removal through another path, the watchdog, a Core swap unload) remove `Immortal` only when the ledger says this module added it; a pre-existing component (vanilla's, another mod's) loses only this module's guard, and a character with no grant is never visited. An explicit `.debug off` follows the same rule. **The vanilla player prefab `CHAR_VampireMale` carries `Immortal { IsImmortal = true }`**, and `KillEntity` does not kill an immortal character; the game's downed script (`Script_ApplyBuffUnderHealthThreshold`) applies the downed buff on a hit that lands at 0 health, so `Immortal` appears to be what lets a player reach the downed state. Observed live 2026-10-11: with `Immortal` removed, a player died outright at 0 health twice. Not established: whether every live player keeps the prefab's `Immortal` (a respawn reuses the character entity and did not bring it back), so a forced removal was reverted the same day. Unless a debug protection grant (the Dev bridge `protect` op, a plugin's `Protect` / `KeepAlive`) holds the character, `.debug off` also removes `Admin_Invulnerable_Buff` (test harnesses and the bridge `buff` op apply it directly, and it blocks all damage), so debug off leaves a player who can be hurt. A grant that cannot record a readable character identity is refused before anything is written.

**Persistence and reloads.**

- The buff entity is world state: it is saved with the character and survives a death, a Core swap and a server restart.
- `Immortal` and the guard are cleared on every Core uninstall (a Core swap or process exit is a `Swap` unload: the buff and the ledger are kept). The next generation's world-ready reconcile re-applies them from the ledger and reads the guard back; on a server start it keeps retrying until the granted player connects.
- A Core swap into a running world gets no `WorldReady`, so the ledger store may still be pending. The reconcile resolves the save folder first and defers rather than latching on an empty ledger, and `.debug on` refuses with `The world save folder is not known yet…` instead of writing over the ledger.
- A watchdog (every 60 frames) clears `Immortal` and the guard from a granted character whose buff ended some other way (the Dev bridge `buff` op, a game-side destroy).
- The Sample's grant ledger (`nodrain.immortal` in the Sample's ModSave store) is migrated once per world load, on the first reconcile with the ledger ready: every row with a character identity is copied with its pre-existing flag, so an `Immortal` the Sample granted and never cleared (a server stopped before the Sample's unload) is removed when its buff is gone, and a component that pre-dated the Sample's grant is still never removed; this ledger's own row for an account wins, and the legacy key is then deleted (`Debug: migrated the Sample's grant ledger …`). The Sample's buff itself is not migrated: re-run `.debug on`; `.debug off` removes a leftover `sample.nodrain` instance. Removing Progenitor from a server leaves a worn buff in the save; turn it off first ([what happens to a save when a mod is missing](#what-happens-to-a-save-when-a-mod-is-missing)).

**Map powers.** While a character wears the buff, the server pushes its state to that player's client (`'PrDs'`, in Core's reserved event range; the client asks with `'PrDq'` on every new client world and after a Core load). The client reveals the whole map with `WorldMap.RevealAll` (the game's own debug reveal flag; exploration is never written, so removing the buff restores it on every path). **Teleporting on the map: use the game's own admin teleport, Ctrl+Shift+left click on the map** (decided 2026-10-06). It is the game's, not Progenitor's: the client's `MapMenuMapper.OnMapClicked` sends `PlayerTeleportDebugEvent { Target = Self }` with no client-side check, and the server creates the event only for a user whose `AdminUser` is `Level` Admin (2) with `AuthMethod` Authenticated (2) (`[AdminEvent(MinRequiredLevel = 2, MinRequiredAuthMethod = 2)]`), or when `EveryoneIsAdmin` is set; it does not need the debug buff. So it covers an admin who has authenticated (`adminauth`), and not a `User.IsAdmin` player who has not, nor a private-game host whom Progenitor treats as an admin (decided 2026-10-06; that predicate is not implemented yet). For those, Progenitor's own map teleport stays, through a key that is **unbound by default**: bind **Teleport to map point** (`TeleportToMapPoint`, a map-scope Core keybind in the Keybinds tab), then hold it over the open map; a ring fills for `HoldSeconds` and the world point under the cursor goes to the server (`'PrDt'`). Routing the vanilla gesture to Progenitor's path is not done: the game drops the event before any ECS entity exists, so it would need a new native hook. The trace is in the internal research notes. The server searches rings out to `SearchRadius` for a spot with a gameplay floor (`GroundProbe`; the pathfinder walkable check is opt-in and not live-verified), teleports with `PlayerTeleport`, answers (`'PrDr'`) and says `Teleported`, `Teleported (moved N m to the nearest valid spot)` or the refusal in chat. If you bind it to Middle Mouse, note that the game binds Push To Talk there by default and reads it on every screen, so with voice chat on a hold also keys the microphone; the Keybinds tab shows the shared key.

**Changes from the Sample's version.** Same code paths, with these differences:

- Core only ever unloads as a `Swap`, so the `Removal` sweep never runs: the buff stays on a character until `.debug off` (or the bridge `buff` op), and a buff left on when Progenitor is removed stays in the save.
- The prefab pass runs locally on each side from `PrefabRegistry.AfterApply` instead of a registry `Content.Edit`, so there is no server-wins `PrfE` layer for it; both sides run the same Core. Its `PrefabPassVersion` is part of the live-refresh fingerprint, so a changed pass refreshes live instances.
- The map teleport key is unbound by default (it was Middle Mouse), the events moved from the Sample's `'SmM*'` ids to Core's reserved `'PrD*'`, and `.maptp` became `.debug tp`.
- `ctx.Debug.Protect` and the bridge `protect` op now apply `progenitor.debug` (they applied `sample.nodrain` when the Sample was loaded); they keep their own `Immortal` bookkeeping, not this ledger.
- New: another online player by name, and the chat notice to that player.

Settings (cfg-only, see [settings-organization.md](settings-organization.md#operator-diagnostic-and-developer-settings-cfg-only)):

| Key | File | Default | Side |
|---|---|---|---|
| `[DebugMap] SearchRadius` | shared `progenitor.cfg` | 100 (m, clamped 0..250; 0 = exact point only) | server |
| `[DebugMap] SearchStep` | shared | 2 (m, clamped 0.5..10) | server |
| `[DebugMap] WalkableCheck` | shared | false (not live-verified) | server |
| `[DebugMap] HoldSeconds` | ui `progenitor.ui.cfg` | 1 (s) | client |

Log lines to grep (server unless noted): `Debug: installed`, `Debug: on begin`, `Debug: applied instance=`, `Debug: status character=… flags=… immortal=… guard=… prefixes=…`, `Debug: world-ready reconcile over N own grant(s): adopted …`, `Debug: grant recorded`, `Debug: unload reason=Swap, keeping N live instance(s)`, `Debug: unload reason=Swap, cleared Immortal + god-mode guard from N character(s)`, `Debug map: state user=… reveal=… teleport=…`, `Debug map: teleport <name>: requested (…) chosen (…) moved … result=…`; client `Debug map: state reveal=… teleport=…` and `Debug map: hold canceled (…)`.

The acceptance procedure is in the [development loop](bridge-ops.md#core-debug-tools-debug) (not run).

#### Build mode (`.debug build`)

**Status.** Settings write and restore **verified live 2026-10-08** (isolated test server, Steam client in game): `.debug build on` set the settings on the server's `SyncedServerDebugSettings` and the connected client's copy showed the same bytes; a Core swap kept it on; `.debug build off` put every byte back on both sides. **Building under it verified live 2026-10-09** (isolated test server, server-side Core hot swap, the Steam client sending the game's own build events): with the seven settings below, a stone wall and its two pillars built inside the admin's own territory joined that castle's heart and team. **Not live-verified:** the restore after a world load or server restart, an already-on setting surviving `off`, and building through the client's own build menu (the live check sent the same build event the menu sends). Game-free tests: `DebugBuildModeTests` (controller end to end, retired-setting migration) and `DetachedPiecesTests`. Sources: `src/Progenitor.Core/Debugging/DebugBuildMode.cs` (decisions), `DebugBuildController.cs` (state machine), `DebugBuildRuntime.cs` (reads, writes, lifetime hooks), `DetachedPieces.cs` and `DetachedPiecesRuntime.cs` (`.debug build detached`).

**It is server-wide.** These are the game's own server debug settings: one networked singleton that the game replicates to every client. While build mode is on, **every player on the server** builds without costs, with free placement and no castle limits, and sees every recipe unlocked, not only the admin who turned it on. Use it on a sandbox or test server.

What each setting does was traced in the server's placement code (`PlaceTileModelSystem`, `GetPlacementResult`) and, where marked, observed live (internal research notes, 2026-10-09).

| Setting (game field) | Build mode | What the game does with it |
|---|---|---|
| `BuildCostsDisabled` | on | The resource check passes; nothing is consumed. |
| `AllProgressionUnlocked` | on | The blueprint-unlocked check passes; every recipe and other progression unlock reads as unlocked, for building and everything else that checks progression. |
| `FreeBuildingPlacementEnabled` | on | The server places a piece at the position the client sent instead of snapping it to the building grid. Pieces still go through the territory check, so they join the castle (live). |
| `FloorPlacementRestrictionsDisabled` | on | Not read by the placement functions traced; its effect is not established. |
| `CastleLimitsDisabled` | on | The castle's piece-count limits for non-floor pieces are not checked. |
| `CastleDragBuildingEnabled` | on | The game's drag building in build mode (the client's build mode reads it). Not traced on the server. |
| `BuildingProximityRequirementDisabled` | on | The "too far from the piece" range check (`CastleBuildingMaxRange`) is skipped, so you can place pieces far from your character. |
| `BuildingPlacementRestrictionsDisabled` | **never (retired 2026-10-09)** | Skips the castle territory check **and** the per-tile placement requirements. That territory check is also what picks the heart a new piece joins, so with this on every castle piece is built **belonging to no castle**: empty `CastleHeartConnection`, the prefab's own team (1) instead of the castle's. Its owner then cannot hit it (the game's red "Cannot damage an active enemy castle on this server"), it is not part of the castle, and it can overlap world ruins and other blockers. Live 2026-10-09: a wall built with it on had no heart and team 1, while walls built without it had the heart and team 11. Build mode turned it on until this change. |

**Retired setting.** Build mode no longer writes `BuildingPlacementRestrictionsDisabled`, but still knows it: it clears it only when a record names it (a record from before this change): `off` and the after-load restore clear it, an `on` over such a record turns it off and drops it from the record, and a Core swap that adopts such a record turns it off at once (`Debug build: adopted: … turned off retired BuildingPlacementRestrictionsDisabled …`). If it is on and no record names it, it is an operator's own: build mode never clears it, not even `off` with no readable record, and status, `on` and `off` warn: `Warning: BuildingPlacementRestrictionsDisabled is on and build mode did not set it, so pieces built now join no castle; build mode leaves it alone: turn it off in the server's debug settings. …`. What the setting no longer allows: placing over world ruins, rocks and other blockers, and outside your territory; clear those the way a player would.

**Detached pieces (`.debug build detached`).** Pieces already built while the retired setting was on stay detached; nothing in the game re-attaches them. `.debug build detached` lists every detached piece on the server (prefab, position, owner, entity id, a count per owner, at most 20 rows in chat; every row also goes to the server log). A piece counts as detached when its `CastleAreaRequirement` is `AttachToOwnedArea` (walls, floors, pillars, most castle furniture), its `CastleHeartConnection` is empty, and it has a `UserOwner`. Built normally such a piece always has a heart: outside an owned territory the game refuses it (`CastleAreaNotOwnedByUser`), also on a server with the game's `GlobalCastleTerritoryEnabled` on (checked live 2026-10-09: a stone floor sent outside the plot with build mode off was refused for that reason). The list says the cause is probable, not proven per piece. Removing:

- `.debug build detached remove` removes the caller's own detached piece when there is one. With more than one it only previews (`This would remove N detached piece(s) of yours. …`); `.debug build detached remove <N>` with N equal to that count removes them.
- `.debug build detached remove <index:version>` removes one listed piece, any owner, without a count.
- `.debug build detached remove all` only previews the server-wide count; `.debug build detached remove all <N>` with N equal to it removes every detached piece on the server. A count that no longer matches removes nothing.

Each piece is re-checked right before it goes (a piece that is gone, already being destroyed or no longer detached is skipped and logged as a warning), then handed to the game's own `DestroyUtility.Destroy` (reason `DestroyTileModels`), as a destroyed tile model normally ends; there is no hand-written `DestroyTag`. **No refund:** the destroy gives back no building materials and drops nothing. So a piece that may hold items (a chest, a station: any inventory with an item in it, or an inventory Core could not read) is never removed; the list marks it `may hold items` and the remove reports `Has items; empty it first: …`. The items check is source-checked and game-free tested in its decisions only; no piece with an inventory was removed or skipped live. Then build the piece again with build mode on (free, and it joins the castle). A piece that only stood there because the retired setting skipped the placement rules (on a ruin, outside the territory, without a floor) cannot be rebuilt in the same spot until the blocker is cleared. **Why remove instead of re-attach:** joining a heart is done by the placement in one step (heart, territory, team and the castle's own bookkeeping); writing those fields by hand would skip whatever else that step does, so Core does not. Not handled: a piece built normally later and attached on top of a detached one keeps standing when its detached parent is removed. Same admin rule as the rest of `.debug build`. **Verified live 2026-10-09**: the list found the 17 detached floors, walls and pillars of the test castle plus a test wall; `remove <id>` removed the test wall and a test floor through the destroy path with no warnings or errors in the server log (the test floor was then put back as it was). `remove` with a count, `remove all <count>`, the count previews and the items skip are game-free tested only.

Left out on purpose: `CastleHeartBloodEssenceDisabled`. It stops every castle heart on the server from using blood essence, which pauses upkeep and decay for all castles. That is an economy switch, not a building one, and an admin who wants it should set it knowingly. The other debug settings (sun, aggro, NPCs, drops, durability, waypoints and so on) are not building and are not touched.

**Who can run it.** The server's admin rule (`ServerAdminList.AuthorizeShared`, as the map teleport and remote installs use): on the admin list, authenticated with `adminauth`, or the host of a private game. `.debug build` is a literal subcommand registered beside `.debug` and gated the same way as every other `.debug` form (see [Admin only](#debug-tools-debug)): not `adminOnly`, but Core's internal `[AdminListGate]`. Everyone else gets `This command is admin-only.` before the argument is parsed, and `.help` hides the line from them (since 2026-10-09; before that it showed for every player).

**Exact restore.** `on` writes only the settings that read off, and records exactly those names (ModSave store `progenitor.debug`, key `debug.build`, value `on;p=<server process>;` plus the names; no entity ids). `off` turns back only recorded settings that still read on, then drops the record. A setting that was already on before `on` (for example an operator's no-cost server) is never recorded and stays on after `off`. A second `on` keeps the first record and only adds a recorded setting someone switched off in between. Every command re-reads the live settings and the record; nothing is toggled from a cached flag. `on` and `off` refuse while the world save folder is not known yet, rather than writing over a record they could not read.

**When the record is missing or unreadable.** A save made with the mode on can lose its record (a lost store flush, a damaged store, a value that is not a record). Then:

- `.debug build` (status) warns: `Build mode: off, but warning: build settings are on with no record …` or `Build mode: unknown. Warning: its record in this save cannot be read …`, and with a record it warns about any recorded setting that now reads off.
- `.debug build off` turns **every** build setting off, including any that were on before build mode (it can no longer tell them apart), says so, and drops an unreadable record.
- `.debug build on` over an unreadable record is refused (`… on was refused …`) and leaves the record as it is, so names recorded by an earlier `on` are never re-classified as "already on before". Run `off` first.
- A record that names a setting this build does not switch keeps that name; `off` clears every known setting by name and logs the unknown ones.

**Persistence and reloads.**

- **Core swap:** build mode stays on. Core's uninstall writes nothing; the next generation reads the record and, because this server process wrote it, adopts it (`Debug build: adopted: build mode is on from the previous Core generation …`). Core is only uninstalled by a swap or at process exit.
- **A swap right after a restart.** If a Core swap lands after a restart's world load but before the restore ran, the new generation never sees the load; the record's process stamp is another process's, so it restores (`Debug build: Core install after a server restart: …`). A world reloaded inside the same process (a client-hosted game loading another save) followed by such a swap is the one case it cannot tell apart.
- **World load or server restart:** build mode does not survive. The game saves the settings singleton with the world, so a world saved while it was on loads with the settings on; on the first frame after a load with the record readable, Core turns back what the record names and drops it (`Debug build: world load: build mode does not survive a world load; turned back …`). Expected from the source; not yet observed across a real restart. An unreadable record after a load is left alone and logged; `off` then clears everything.
- **Removing Progenitor** with build mode on leaves the settings in the save (nothing is left to restore them). Run `.debug build off` first.
- **Why not the game's own toggle.** The game's admin path (`SetDebugSettingEvent`, handled by `DebugEventsSystem.SetDebugSetting`, which calls `SyncedServerDebugSettings.SetSetting`) writes the same field and then rewrites the server's debug settings file, so a change made that way comes back after every restart and replaces an operator's file. Its handler and `SetSetting` also take the settings struct by reference. Core writes the singleton's bytes instead, at `layout.txt` offsets through the guarded component access ctx.Time uses for `DayNightCycleDisabled`; the game copies the singleton into its shared static every frame and replicates it (internal research notes, 2026-10-08).

Log lines: `Debug build: on by <name> (<adminlist|adminauth|host>): wrote …; record …[; turned off retired …]`, `Debug build: detached listed by …: N piece(s).`, `Debug build: detached <prefab> at x, y, z, owner <name> (<id>).`, `Debug build: detached removed by …: …`, `Debug build: detached <id> not removed: …` (warning when the re-check fails; info when it may hold items), `Debug build: detached remove by … not confirmed: …`, `Debug build: off by … turned back …`, `Debug build: unload: …`, `Debug build: adopted: …`, `Debug build: world load: …`, `Debug build: Core install after a server restart: …`, `Debug build: off by … with no record: …`, `Debug build: the build mode record in this save cannot be read …`.

## Triggers

`ctx.Triggers` (`Progenitor.Triggers`). Server only: a client registration returns an inactive handle and logs once. Contract: [api.md](api.md#triggers).

A watch is a circle or an axis-aligned rectangle, and both have a finite height. A circle is a vertical cylinder (`Center`, `Radius`, and `Height` centered on the center's height). A rectangle is a box from `Min` to `Max` on x, y, and z, so one floor of a castle does not include the floor above or below it. When a connected player crosses in or out, the action runs on the server simulation thread. It may call `ctx.Units.Spawn`. Players are the only bodies that trip it.

```csharp
using Progenitor.Triggers;

ctx.Triggers.Watch(new AreaTriggerOptions
{
    Center = new UnitVec3(-2300f, 17f, -1200f),
    Radius = 8f,
    Height = 4f,
    OnEnter = hit => ctx.Units.Spawn("CHAR_Undead_SkeletonSoldier_Armored_Dunley", hit.Position),
});

ctx.Triggers.Watch(new AreaTriggerOptions
{
    Shape = AreaTriggerShape.Rectangle,
    Min = new UnitVec3(-2312f, 15f, -1212f),
    Max = new UnitVec3(-2288f, 19f, -1188f),
    OnEnter = hit => ctx.Units.Spawn("CHAR_Critter_Rat", hit.Center),
});
```

The first time a player's position is seen, the watch records which side they are on and does not fire. Standing in the area during a plugin reload, a world load, or a reconnect does not spawn. Disconnecting while inside fires leave. After that, connecting again only records a side until they cross. Leave waits until they are 0.5 m past every face (`AreaTriggerOptions.LeaveSkin`), including up and down, so the rim does not chatter. A tick with no readable position, and a player scan that throws, change nothing.

`Watch` reads the options once. Dispose the handle to stop that watch. Unload stops every watch this plugin created. Register on the game main thread.

The Sample command is `.triggers on` ([SampleTriggers.cs](../samples/Progenitor.Sample/SampleTriggers.cs)). It arms both shapes where you stand: the circle spawns `CHAR_Critter_Rat` on enter, and the rectangle logs when you leave the floor. `.triggers off` disposes them. Step out and back in; the first sight does not fire.

Status: source-checked, game-free tested (`AreaTriggerTests`, `SampleTriggerTests`), and the sample enter/leave/spawn path verified live on the isolated test server (2026-10-08). One floor above was not held by teleport, so that case stays game-free.

## Units

`Progenitor.Units`. Server-only spawn and steering (M2.7 first cut). Unit movement and the unit spike are researched in the internal research notes. Skip token: `HarmonySlotHandoff.UnitsModule` (`"Units"`).

The client returns a disabled handle and one Debug line so shared plugin code does not need a side check. Spawn is `PrefabLookup` + `Instantiate` + `Translation` (the Dev `spawn` op path). Follow is a Follower field overlay plus `ExternalBehaviourTreeTransitionRequest { Follow }` (evidence 33) — never a bare marker entity (evidence 32). Prefabs that do not ship `Follower` (`CHAR_Critter_Rat`, evidence 116) get the component added on Follow (`AddComponent` + the same overlay; `Follower` layout from the game's metadata) and removed on GoTo / Despawn / `RemoveAll` if we added it. The steering consumer (`TrySetEntityToFollowTarget`) only requires `Follower` on the unit; `FollowerSystem` (IFE_0 = `Follower` on the follower, IFE_1 = `FollowerBuffer` on the target) adds `FollowerBuffer` to the followed entity via ECB when missing — Follow does not add or remove that buffer (a player already has one; stripping it would drop vanilla pets). GoTo writes `AiMove_Server` the way ApproachPosition.Tick does (evidence 46); never a raw `PathRequest` (evidence §11). Despawn sweeps the unit's buffs (DIAG5), then calls `DestroyUtility.Destroy` alone so the game's destroy flow removes the unit on clients too; `DestroyTag` is only a logged fallback if the unit still exists 120 frames later (`BuffSweep.DestroyUnitReplicated`). An immediate `DestroyTag` removed units on the server only (live 2026-09-24). The sweep calls `DestroyUtility.Destroy` alone on each buff too, so the game also removes the buff's `BuffBuffer` row, and the poll prunes any row naming no entity before it finishes or gives up (`BuffSweep.PruneDanglingRows`). **Across a Core swap:** Core uninstall disposes every live handle and then drops Core's pending work, which used to leave those units in the world without their buffs. `Units.Uninstall` now hands each despawn still waiting on its poll to Bootstrap (`ProcessHost.HandOffDespawns`: index, version and `PrefabGUID` hash, stamped with the server world's `SequenceNumber` and Bootstrap's count of save loads). The next generation's `Units.Install` takes them and despawns each again, its live summons included, and logs `Units: resumed N despawn(s) handed over by the previous Core generation (…)`. Rows from a reloaded or replaced world are dropped by Bootstrap, and an id that is gone, already being destroyed or now another prefab is left alone. The hand-off needs a Bootstrap with those methods (a Bootstrap change: restart the process once); with an older Bootstrap Core logs that the units stay in the world, as before. `ctx.Units` fills owner + plugin GUID and `Track`s the handle; unload and `RemoveAll` despawn what that plugin spawned, and then those units' live summons (see *Summons go with their unit* below). Plugin unload also runs `Units.SweepForUnload(plugin, guid)` from `SweepRegistrations` so a static-API spawn (`Units.Spawn(..., this)`) cannot pin the plugin ALC.

**API-spawn marker.** On the server, every unit spawned through `ctx.Units.Spawn` (every overload) or the static `Units.Spawn` forms gets Core's marker buff `progenitor.core.api-spawned` on the spawn tick. It is checked again at 1 s and 5 s, the faction write's schedule, and applied again if missing. The marker is a Core-owned `DefineBuff` clone of `Buff_General_Haste` with a fixed id and GUID. Core strips it to an infinite, effect-free buff that persists through death, the variant-marker path in `ctx.Ai`, and never puts it on a unit before that strip is checked. Units restored from a save keep it, as variant markers do. It is what scopes [ally-safe damaging hits](ai.md#ally-safe-damaging-hits) to API-spawned casters. The server strip never reached the client's own copy of the clone, so until the client strip below, every API-spawned unit showed haste's cyan shimmer on Progenitor clients (live, 2026-09-25: destroying the marker on 20 units removed their shimmer). See [Core inert clones on the client](#core-inert-clones-on-the-client). The marker itself is not yet verified live beyond that.

#### Core inert clones on the client

A `DefineBuff` clone exists twice: the server's, and each Progenitor client's own copy, built from the definition packet by instantiating the **client's** source prefab. The registry rewrites the clone's `NetSnapshot` prefab GUID to the clone's, so a replicated buff spawns on the client from that client copy. A strip Core makes on the server prefab therefore never reaches what clients draw.

Core now strips its inert clones on both sides. The client strips a clone:
- from a registry hook (`PrefabRegistry.AfterApply`) on the frame the client's clone is built or updated, before anything is instantiated from it;
- inside the receive of a server announcement that names a clone that already exists (below);
- when client Core installs into a live session (a client Core hot swap), for the two fixed ids, found directly by GUID (`CalculateGuidHash(id)`). The new generation's registry list is empty then, so it is not used. Variant markers follow when the server re-announces them.

**Server-authoritative.** Besides Core's two fixed ids, the client strips only clone GUIDs the server announced on Core event `'PrIc'` (`0x50724963`, reserved range; 12 bytes: schema, GUID, role).
- The server announces each `ctx.Ai.Variants` marker when it is defined, **before** registering its clone, so reliable ordering puts the announcement ahead of the definition.
- It sends the whole set to every joiner on `PlayerConnected` at priority 2100, above the registry's definition send (2000).
- It sends it again to a client whose Core swapped (the handshake re-answer from a user that is already ok, in its own `try` next to the edit-layer resend, one `try` per announcement).
- A variant released with its plugin is forgotten (not announced to later joiners) until the next `Define`.
- The client clears the set when it leaves the server.

**An announcement alone strips nothing** (`InertCloneRules.Decide`). The prefab must have `Buff`, and be either one of the two fixed clones or an announced registry clone whose source is `Buff_General_Haste` (resolved through `PrefabLookupMap` first, since a buff is not a spawnable). The source is the applied definition's, or after a client Core swap the source the registry remembered for the clone across the swap. The client never infers a marker from an id's shape, so a plugin's own haste clone keeps its speed modifier on both sides. An announcement naming a clone that already exists strips it inside the receive, as the registry hook does.

| Clone | Recognized on the client by | Server prefab | Client copy |
|---|---|---|---|
| API-spawn marker `progenitor.core.api-spawned` | exact id | `AiVariants.MakeInert` | same, plus `ShowOnBuffBar`, `ShowOnBuffBarData` |
| `ctx.Ai.Variants` markers `<plugin>.ai.<name>` | the server's `'PrIc'` announcement of its GUID | `AiVariants.MakeInert` | same, plus `ShowOnBuffBar`, `ShowOnBuffBarData` |
| Hide-health-bar `progenitor.core.hide-hud` | exact id | `MakeInert` only | same as the markers, plus `HideTargetHUD { Type = -1 }` |
| Aura `progenitor.core.aura.elite` | not stripped | the aura row's list | untouched: the ring **is** its client visual |

`MakeInert` writes `LifeTime {-1, None}`, adds `Buff_Persists_Through_Death`, and removes `CreateGameplayEventsOnSpawn`, `GameplayEventListeners`, `RemoveBuffOnGameplayEvent(Entry)`, `ModifyMovementSpeedBuff(Modification)`, and, on a prefab, `BuffSequence`, `BuffSequenceSpawned`, `SpawnSequenceForEntity` and `SequenceGUID`. `BuffSequence` exists only on the client copy. The lists are `InertCloneRules.ServerStrip` (and its prefab-only part) and `ClientExtraStrip`; both strips iterate them. The buff-bar components are removed by the archetype's own component type, because the cached index of the managed `ShowOnBuffBarData` can disagree with the archetype.

**Why a client strip and not a visual-free source.** A source with no client visuals was considered first (for example `Buff_General_Wounded_Tracker`, 224060472, whose client prefab has no `BuffSequence` but carries `Script_Wounded_DataShared`, a script Core would then have to strip on both sides, unverified). From the source it would not help clients without Progenitor either: the snapshot GUID is the clone's, which such a client has no prefab for, whatever the source. What such a client does with that GUID is not verified. The haste strip, by contrast, is the one the debug buff clone (the Sample's `.nodrain` until 2026-10-06, now Core's `.debug`) has run on both sides since 2026-09-03 (research `buffs.md`, evidence 100 and 109: a client instance without `ModifyMovementSpeedBuff` or sequence components). Changing the marker's source would also refuse the same-id re-clone while live units wear it (a Core hot swap), and the fixed ids and GUIDs must not change.

**What clients see.**
- *Progenitor client with this Core:* no shimmer, no speed change and no buff-bar entry on buffs spawned after its copy was stripped. Instances spawned before that keep what they were spawned with until the server re-sends them. That happens after a client Core hot swap, and in the unexpected case of a variant-marker definition arriving before its announcement (the announcement then strips the prefab in its receive).
- *Progenitor client with an older Core:* the haste visuals, as before. How it treats `'PrIc'` is not verified.

**Bootstrap receive slot.** `'PrIc'`, like `'PruL'`, `'PrSe'`, `'PrhF'` and `'PrsQ'`, is not yet in Bootstrap's `ReceiveSlotPolicy.CoreProtocolIds`. Adding it is a Bootstrap change, left for a later restart window.
- *Client without Progenitor:* not verified. From the source it has no prefab for any of these GUIDs (markers, hide-HUD, aura).

**Game defaults.** `Buff_General_Haste`'s client prefab carries `BuffSequence` + `BuffSequenceSpawned` (the cyan shimmer), `ModifyMovementSpeedBuff` and `ShowOnBuffBar` / `ShowOnBuffBarData` (live, 2026-09-25). `Buff_General_Wounded_Tracker` (224060472), which every unit carries, has on the client `Attach`, `EntityOwner`, `EntityCreator`, `Buff`, `Script_Wounded_DataShared`, `DestroyData`, `DestroyState`, `BuffCategory`, networking and transform components, and no `BuffSequence` (live, 2026-09-25).

Status: source-checked, built and unit-tested (`InertCloneRulesTests`). Not verified live: that the client strip runs and removes the shimmer, speed and buff-bar entry on freshly spawned units; that removing `ShowOnBuffBar` / `ShowOnBuffBarData` from the client copy raises no client errors; the `'PrIc'` announcement and variant markers on a client; a client Core hot swap and the re-answer resend.

**State machine.** Follow and GoTo are exclusive. GoTo after Follow clears `Follower.Followed` (@0x0), `ModeModifiable` (@0x18), `Stationary` (@0x1C), posts `GenericEnemyState.Idle`, and zeros `AiMove_Server.TargettingMode` / `TargetEntity_Movement` before the ApproachPosition write. If Follow added `Follower` because the prefab had none, GoTo removes that component after the clear so `FollowerSystem` stops iterating the unit. Follow after GoTo clears the goal the same way (`TargettingMode = None`, `TargetPosition` zeroed) before the Follower overlay. The handle's `Following` / `GoingTo` is what the entity actually runs. Follow re-posts the BT request only when the target changed. Spawn / Follow / GoTo / Despawn / RemoveAll throw `InvalidOperationException` off the game main thread (Effects does not marshal off-thread either; Units throws because `EntityManager` is not thread-safe). Per-call Follow/GoTo lines are Debug; spawn, despawn, and `Units: added Follower to <entity> (prefab had none)` stay Info. `UnitUnreachable` re-arms on the next Follow/GoTo.

```csharp
if (ctx.IsServer)
{
    var rat = ctx.Units.Spawn("CHAR_Critter_Rat", new UnitVec3(x, y, z));
    rat.Follow(UnitId.From(characterEntity)); // characterEntity from an ICommandContext or an event
    rat.GoTo(new UnitVec3(x + 6f, y, z));
    // later:
    rat.Despawn();
    ctx.Units.RemoveAll();
}
```

`UnitArrived` / `UnitUnreachable` publish on `EventBus` from the main-thread poll (`Plugin.Frame`) so plugin priority / before / after order is the same as every other event. Subscribe with `ctx.Events.On<UnitArrived>(...)`. Arrival is 3D distance ≤ 1 m (ApproachPosition stop band). Follow raises Arrived once inside 3 m of the target; Unreachable if the entity or follow target is gone, or a Follow/GoTo write was refused.

A connected player must stand next to the spawn or the unit is `DisabledDueToNoPlayersInRange` and will not path (evidence §22). Pin home (`SpawnTransform` / `LastTranslation`) so Idle does not become Return.
`ctx.Units` on the client does not throw: `Spawn` returns a disabled handle. `Units.RemoveAll(owner)` is the static form.

**Summons go with their unit.** Every server despawn of a handle (`Despawn`, `RemoveAll`, plugin unload, `ClearArea`) also removes that unit's live summons: units with `BehaviourTreeInstance` whose `EntityOwner` chain reaches the despawned unit and that carry `Minion` or `IsMinion { Value = true }` (for example the Bishop of Dunley's pillar `CHAR_Militia_ConstrainingPole`). A boss keeps summoning while it is being removed, so the sweep surveys up to four times, one second apart, after the last despawn, and uses the same replicated removal. Player-owned entities are never touched. Status: source-checked and unit-tested; not yet verified live.

### Hitbox scale and move speed

A unit this plugin spawned can have a bigger or smaller server-side hitbox and a faster or slower move speed, set at spawn or later:

```csharp
var boss = ctx.Units.Spawn("CHAR_Wendigo_VBlood", pos, new UnitSpawnOptions
{
    HitboxScale = 2f,          // 0.25 to 4; 1 or null keeps the vanilla hitbox
    MoveSpeedMultiplier = 1.5f // 0.1 to 4;  1 or null keeps the vanilla speed
});
ctx.Units.SetHitboxScale(boss, 3f);
bool now = ctx.Units.SetMoveSpeed(boss, 0.5f); // false: the unit cannot move right now; retried
float scale = ctx.Units.GetHitboxScale(boss);  // read off the unit
ctx.Units.SetHitboxScale(boss, 1f);            // vanilla collider again
ctx.Units.SetMoveSpeed(boss, 1f);              // vanilla speed again
```

| Member | Range | What changes |
|---|---|---|
| `SetHitboxScale(handle, scale)` / `UnitSpawnOptions.HitboxScale` | 0.25 to 4; 1 restores | The unit's `PhysicsCollider`: the child on the Hittable collision layer (a capsule or sphere) is scaled with its bottom kept on the ground, so a bigger hitbox grows upwards instead of sinking or floating. Its horizontal offset from the unit's origin scales too. |
| `GetHitboxScale(handle)` | | The scale of the collider copy the unit points at now; 1 for the vanilla collider or one something else put there. |
| `SetMoveSpeed(handle, multiplier)` → `bool` / `UnitSpawnOptions.MoveSpeedMultiplier` | 0.1 to 4 x vanilla; 1 restores | `Movement.Speed`, which chase speed follows, and the four `AiMoveSpeeds` values (walk, run, circle, return) by the same factor. |
| `GetMoveSpeed(handle)` | | The multiplier last set through `ctx.Units`, or 1. |

**Contract.**
- Server only. A disabled handle (client, `Units` module skipped) or a disposed one throws `InvalidOperationException`, as `SetFaction` does. With spawn options, a disabled handle skips them.
- An out-of-range value throws `ArgumentOutOfRangeException`; with spawn options it throws before anything spawns.
- Both are applied immediately and checked again at 1 s and 5 s (the faction write's schedule). A hitbox or speed that something reset is written again, with a warning.
- Despawn and plugin unload need no restore: the unit is gone. Setting 1 afterwards restores the vanilla collider or speed.
- **Hitbox and visual size are independent.** This changes only the server's hit shape. The model's visual size is `SetLook` ([Unit look](#unit-look-size-tint-glow-overlay)), which is client-side and never touches the hitbox. Match the two yourself if a bigger model should also be easier to hit.

**Hitbox mechanism.**
- Every unit checked (Frostmaw, Bishop of Dunley, a pillar) has a compound collider: a body capsule, a capsule on the Hittable layer and a 5 m sensor sphere.
- `LocalTransform.Scale` does not change hits.
- The unit's `PhysicsCollider` is pointed at a private copy of its own blob. In the copy, the Hittable child's radius and end points are scaled and the compound's bounding-volume hierarchy is refitted: the child's leaf box is replaced, and the boxes above it and the bounding radius only grow. Without the refit, the bigger shape is never hit.
- Only this unit points at the copy. Copies are shared per (original collider, scale), allocated for the process and never freed, because physics jobs may still read one.
- `SetHitboxScale(1)` points the unit back at the original collider, but only while it still points at one of these copies.
- A unit whose collider is not compound, whose Hittable child is not a capsule or sphere, or whose blob does not match the verified layout is refused with a message saying why. `SetHitboxScale` throws; a spawn option logs one warning and the unit keeps its vanilla hitbox.

**Speed mechanism.**
- The game layers temporary modifiers on the written value (live: 7 read 14 during a movement effect, then 7 again). So the multiplier is applied as a ratio to the current value, and the factor already applied is tracked per handle, so 2 then 3 is 3x, not 6x.
- When no modifier is active (the value is exactly prefab x the applied factor), the new value is computed from the prefab, so repeated calls do not accumulate float error.
- A multiplicative modifier active during the call is kept exactly. An additive one (+a) leaves an error of a x (new / old - 1) after it ends.
- While a speed value reads 0 (a root or stun), nothing is written: the call returns false and is retried at 1 s and 5 s. If it is still 0 at 5 s, one warning is logged and the request stays pending until the next `SetMoveSpeed`.

**Persistence.**
- Checked against the server's `PersistenceV2Utility..cctor`. `PhysicsCollider` is excluded with `CopyDataFromPrefab`, so a unit restored from a save has its vanilla hitbox.
- `Movement` and `AiMoveSpeeds` carry no exclusion (dump), so they are saved with the unit. A unit restored from a save keeps its scaled speed, but no handle comes back with it.

Logs: `Units: hitbox copy x… of collider 0x… (N bytes) children=[…] leafLanes=… grownLanes=… boundingRadius=…->…`, `Units: hitbox of … restored to its vanilla collider.`, `Units: move speed of … x… -> x… (Movement.Speed … -> …)`.

Status: the mechanism was proven live via scratch on the isolated server on 2026-09-25:
- A pillar with its normal hitbox was missed at 1.6 m lateral. At 4x it was hit at 1.6 m, and at 5x at 2.5 m.
- `Movement.Speed` set from 3 to 12 was measured at 3.1 to 12.1 m/s.

This implementation is unit-tested (`UnitHitboxSpeedTests`, on a synthetic Frostmaw compound) and was verified live on 2026-09-25 (isolated server, a research harness plugin calling `ctx.Units`). Hitbox: a Shadowbolt fired 1.6 m to the side of a Bishop pillar missed at scale 1, hit at scale 4 (61 to 45 health), and missed again after `SetHitboxScale(1)`; the log showed one copy with the leaf lane refitted and the restore. Speed: a Frostmaw spawned with `MoveSpeedMultiplier = 2` chased at 9.2 m/s peak, `SetMoveSpeed(3)` gave 12.9 m/s with `Movement.Speed` 13.5 and `AiMoveSpeeds` 7.5/13.5/13.5/18, and `SetMoveSpeed(1)` returned exactly 4.5 and 2.5/4.5/4.5/6 (4.6 m/s). Not verified live: save and reload (the game saves `Movement` and `AiMoveSpeeds`, not `PhysicsCollider`, so a restored unit keeps its speed, returns with its vanilla hitbox, and has no handle, so `GetMoveSpeed` cannot report it), rooted units, and plugin unload with scaled units. The snippet above is source-checked, not compiled.

### Unit stats

A unit this plugin spawned can be given an absolute level, maximum health and physical and spell power, at spawn or later. For example, a skeleton that hits like a gear-level-91 player and has 5,000 health:

```csharp
var skeleton = ctx.Units.Spawn("CHAR_Undead_SkeletonSoldier_Armored_Dunley", pos, new UnitSpawnOptions
{
    Stats = new UnitStatOverrides { Level = 91, MaxHealth = 5000f, PhysicalPower = 45.885f, SpellPower = 44.03f },
});
bool now = ctx.Units.SetStats(skeleton, new UnitStatOverrides { MaxHealth = 8000f }); // false: waits for the spawn computation
UnitStatOverrides current = ctx.Units.GetStats(skeleton);                              // read off the unit
```

| Member | Range | What changes |
|---|---|---|
| `UnitStatOverrides.Level` | 1 to 120 | `UnitLevel.Level`. |
| `UnitStatOverrides.MaxHealth` | above 0, at most 10,000,000 | `Health.MaxHealth`. Current health (`Health.Value`) and `Health.MaxRecoveryHealth` are set to the new maximum. |
| `UnitStatOverrides.PhysicalPower` / `SpellPower` | 0 to 10,000 | `UnitStats.PhysicalPower` / `SpellPower`. |
| `SetStats(handle, stats)` → `bool` / `UnitSpawnOptions.Stats` | | Writes the set fields; a null field is left as it is. Returns false when the write waits for the spawn computation (below). |
| `GetStats(handle)` → `UnitStatOverrides` | | The unit's current values, temporary modifiers included. `Level` is null when the unit has no `UnitLevel`. |

**Contract.**
- Server only. A disabled handle (client, `Units` module skipped) or a disposed one throws `InvalidOperationException`. With spawn options, a disabled handle skips them.
- Values are absolute, not multipliers. An out-of-range value throws `ArgumentOutOfRangeException`; with spawn options it throws before anything spawns.
- Setting `MaxHealth` is a full heal to the new maximum. On a dead unit (`Health.IsDead` or a `Dead` component) or a downed one (health at or below 0), it throws `InvalidOperationException` instead of reviving the unit. If the unit dies while the write is waiting (below), the refusal is one warning and the other fields are still written.
- Setting power on a unit without `UnitStats`, or level on one without `UnitLevel`, throws. A player character has no `UnitLevel`.
- `Level` feeds the game's level-difference damage scaling between attacker and target. It does **not** recompute power or health: set those explicitly. `UnitLevel.HideLevel` is never touched.
- A later `SetStats` with new values replaces the earlier ones. Nothing is restored on despawn or unload: the unit is gone. To return to the defaults, read them with `GetStats` before changing them, or use the table below.

**When the write happens.**
- `Spawn` instantiates the unit synchronously. The fresh instance reads `Health.MaxHealth` 0, as the prefab does.
- The game fills health and power from the unit's `UnitSpawnData` settings (`HealthSettingsPrefabGuid`, `PhysicalPowerSettingsPrefabGuid`, `SpellPowerSettingsPrefabGuid`) on a later frame. A write before that would be overwritten.
- So while `MaxHealth` reads 0 the request is recorded, `SetStats` returns false, and the first check that sees `MaxHealth` above 0 writes it. Checks run at 0.25 s, 1 s and 5 s after the call. If `MaxHealth` still reads 0 at 5 s, one warning is logged and the request stays pending until the next `SetStats`.
- After the write, the remaining checks write a value again, with a warning, if the game reset it to exactly the value the first write replaced.
- With `UnitSpawnOptions.Stats` the unit has its own stats until the first check that writes, normally 0.25 s after the spawn.

**Modifiers.** The game layers temporary modifiers onto these values in place (live on `Movement.Speed`: 7 read 14 during an effect, then 7 again). When the unit does not read the value `SetStats` last wrote, a modifier is assumed:
- New power or health is applied as a ratio: exact for a multiplicative modifier. An additive +a leaves an error of a x (new / old - 1) after it ends.
- A new level is applied as an offset: exact for an additive modifier.
- The first write cannot tell a modifier from the game's own value.
- A buff that changes the unit's level (`ModifyUnitLevelBuff`) may make the game recompute its stats. Not verified.

**Persistence.** Checked against the server's `PersistenceV2Utility..cctor` and the dump: `Health`, `UnitStats` and `UnitLevel` have no `ExcludeComponent` attribute and are not in the custom exclusion list, so they are saved with the unit. A unit restored from a save keeps its level, health and power, but no handle comes back with it.

**Vanilla limitation: health bars above 100,000.** The game sends current health (`Health.Value`, and `MaxRecoveryHealth`) to clients as a ranged float clamped to 0–100,000 (`[SyncRangedFloat(0, 100000, …)]` in the server dump), while `MaxHealth` is sent without that limit. A unit with more than 100,000 current health therefore shows at most 100,000 against its full maximum, so its bar is too short.

| Max health | Current health on the server | What a client shows |
|---|---|---|
| up to 100,000 | exact | an exact bar |
| 1,000,000 (live, 2026-09-25) | 993,000 | a bar about 10 % full |

This is display only: the server's health, damage and death are unaffected. Keep `MaxHealth` at or below 100,000 when players must read the bar correctly. `SetStats` still accepts up to 10,000,000 because server-side behavior is correct above the limit.

**Defaults (measured live on 2026-09-25, isolated server).**

| Unit | Level | Physical / spell power | Max health | Speed |
|---|---|---|---|---|
| `CHAR_Undead_SkeletonSoldier_Armored_Dunley` | 40 | 26.58 / 26.58 | 99.37 | 3.5 |
| `CHAR_Vampire_Dracula_VBlood` | 91 | 111.41 / 111.41 | 8107 | 3.5 |
| `CHAR_Unholy_SkeletonWarrior_Summon` | 10 | 11.49 / not measured | not measured | not measured |
| `CHAR_Wendigo_VBlood` (Frostmaw) | not measured | not measured | 1291.98 | 4.5 |
| Player at gear level 91 (tier-8 legendary sword, tier-9 armor, Dracula soul shard) | none: a player has no `UnitLevel` (gear level 91 = weapon 30 + armor 36 + spell 25) | 45.885 / 44.03 | 948.3 | 4.0 |

Max health is the value on a live spawned unit; the prefab reads 0. Speed is `Movement.Speed`.

Logs: `Units: stats of …: MaxHealth … -> … PhysicalPower … -> … Level … -> ….`, `Units: stats of … were reset after they were set; writing them again: …`, `Units: stats for … are still waiting: …`.

Status: unit-tested (`UnitStatsTests`: validation, request merging, modifier and re-apply bookkeeping), not live-verified. The layout offsets are declared in `layout.txt` and resolved against the server dump. The snippet above is source-checked, not compiled.

**Live check (2026-09-25):** a Dunley skeleton spawned with `Stats = { Level 91, MaxHealth 52000, PhysicalPower 917.7, SpellPower 880.6 }` logged `MaxHealth 99.375 -> 52000 PhysicalPower 26.579 -> 917.7 SpellPower 26.579 -> 880.6 Level 40 -> 91`. `GetStats` read those values back, and the unit fought with them.

### Unit pet (follow the owner, fight a target)

`ctx.Units.SetPet(handle, owner)` makes a unit this plugin spawned behave as the owner's companion. `ClearPet(handle)` stops it, and `IsPet(handle)` reads it. This section is the default `PetStance.Follow`; `Hold` and `Attack` are in [Pet stances and hold](#pet-stances-and-hold). Every 0.5 s Core checks the unit:

- **No live target:** it follows the owner, and Follow is posted only when it isn't already following them.
- **Live aggro target that isn't the owner:** from `Engage`, or from the game's own proximity aggro. It stops following (`Follower.Followed` is cleared) and Combat is posted.
- **Target dead or gone:** it returns to following.

While it is a pet, its walk speed is kept equal to its run speed, so following uses its full speed, including `SetMoveSpeed`.

**Why not `Follow` plus `Engage`.** Plain `Follow` puts the unit in the game's Follow behavior-tree state (`GenericEnemyState.Follow`, 256), and a set `Follower.Followed` keeps it there. A later `Engage` sets the aggro target but the unit never fights. Live, a following skeleton sat in state 256 next to Dracula with 0 casts while Dracula hit it.

**Game defaults (live, 2026-09-25, `CHAR_Undead_SkeletonSoldier_Armored_Dunley`):**
- Melee cast option: range 0–1.8 m, row cooldown 1.2–2.5 s.
- `AiMoveSpeeds` walk 1.275, run 3.5.
- The AI follows at walk speed.
- The skeleton's faction (29) and Dracula's (16) are hostile.

**Verified live (2026-09-25, isolated server):** a pet skeleton spawned through the API with `Stats`, `MoveSpeedMultiplier` and a red `Look`:
- It followed TestPlayer (state 256), switched to Combat (state 8) when Dracula became its target, and returned to Follow (256) after Dracula was downed.
- At level 91 with 20× a gear-91 player's power (physical 917.7), it downed Dracula (8,107 health) in about 100 s and took 5,013 damage of its 52,000.

Not verified: an owner who disconnects, or several pets.

**After a restart, pets are orphans.** Pet state lives in the plugin, not on the unit. A world autosave taken while pets exist (with `Permanent`, they do not time out) restores the units after a server restart with no handles and no stance: nothing holds, releases or despawns them, and they fight whatever their faction fights. Live, 2026-09-25: 42 army units and Dracula came back and fought; `ClearArea` with `Scope = ClearAreaScope.All` cleared them. See [Spawn options](#spawn-options-permanent-droploot-entrance).

### Pet stances and hold

A pet has a stance, and any unit this plugin spawned can be held passive in place. The army demo: pets raised at pre-mapped spots wait next to a held Dracula, and on a cue they all attack him.

```csharp
var army = new List<IUnitHandle>();
foreach (var spot in spots)
{
    var skeleton = ctx.Units.Spawn("CHAR_Undead_SkeletonSoldier_Armored_Dunley", spot, new UnitSpawnOptions
    {
        Entrance = UnitEntrance.RiseFromGround,
        DropLoot = false,
    });
    ctx.Units.SetPet(skeleton, necromancer, PetStance.Hold); // held at its spawn spot
    army.Add(skeleton);
}
ctx.Units.HoldAt(dracula, arenaCentre);  // any unit this plugin spawned; walked there and held

// The cue:
ctx.Units.Hold(dracula, false);
foreach (var pet in army) ctx.Units.PetAttack(pet, dracula.Entity.ToEntity());
```

`PetStance` is in namespace `Progenitor.Units`: `Follow = 0`, `Hold = 1`, `Attack = 2`.

| Call | Effect |
|---|---|
| `SetPet(handle, owner)` | Makes the unit a pet in `Follow`. The two-argument overload is kept for plugins compiled against it. |
| `SetPet(handle, owner, stance)` | Makes the unit a pet in `Follow` or `Hold`. `Attack` throws `ArgumentException`: it needs a target. |
| `SetPetStance(handle, stance)` | Changes a pet's stance to `Follow` or `Hold`. `Hold` holds it where it stands now. |
| `PetAttack(handle, Entity target)` | Stance `Attack` against `target`. |
| `GetPetStance(handle)` → `PetStance?` | The pet's stance; null when the unit is not a pet. |
| `HoldAt(handle, UnitVec3)` | Holds the unit, pet or not, at that position. A pet's stance becomes `Hold`. |
| `Hold(handle, bool)` | `true` holds the unit where it stands; holding a unit that is already held keeps its position. `false` releases it: a pet goes back to `Follow`, and any other unit is forgotten. |
| `IsHeld(handle)` | True while the unit is held, by `Hold`, `HoldAt` or a pet's `Hold` stance. |
| `ClearPet(handle)` | Stops treating the unit as a pet. A held pet is released first; otherwise it keeps what it is doing. |

**Exceptions.** Every call above throws `InvalidOperationException` off the game main thread. `SetPet`, `SetPetStance`, `PetAttack`, `HoldAt` and `Hold(handle, true)` also throw it for a disabled or disposed handle; `SetPetStance` and `PetAttack` throw it when the unit is not a pet. `ArgumentException` comes from `SetPet` or `SetPetStance` with `Attack` or an undefined stance, and from `PetAttack` with `Entity.Null`, the owner or the pet itself. `Hold(handle, false)`, `ClearPet`, `GetPetStance`, `IsHeld` and `IsPet` never throw for the handle's state. Everything runs on the pet loop, every 0.5 s.

**Follow.** The pet behavior above, unchanged.

**Hold.** When Hold begins, Core reads `AggroConsumer.Active` and saves it. Every step it then:
- writes `Active` 0.
- clears `AggroTarget` and `AlertTarget`.
- clears `Follower.Followed`.
- posts Idle when the unit's tree is in Alert (4), Combat (8), Return (64), Follow (256) or `Relocate_CombatArea` (1048576).
- walks it back when it is more than 2 m (horizontal) from the hold position, with the runtime GoTo write. This is not the handle's `GoTo`: no move ticket, no handle state change, and no `UnitArrived` or `UnitUnreachable`. After a walk back is sent it waits 3 s before sending another.

`Active` is only written when it is on or a target is set, so a quiet unit is not written every step. A unit that is dead (`Dead` or `Health.IsDead`) drops out of the hold with nothing written back. Health 0 alone does not count: **game default** (live, 2026-09-25): a unit instantiated this frame reads `Health.Value` 0 (the prefab carries 0; Dracula's prefab reads `MaxHealth` 0, `Value` 0) until the game's spawn systems fill it, and `SetPet`/`HoldAt` take their first step right after `Spawn`. A health-0 test dropped every unit held at spawn. Not live-verified: that a held unit acquires no aggro and does not fight back when damaged. It can still be damaged.

`Active` is a `ModifiableBool`, owned by the game's modification registry. When a buff that modifies it is added or removed, the game recomputes the value and can overwrite Core's 0, for up to one step (0.5 s) until the next hold step writes 0 again. A buff that sets `Active` 0 keeps it 0.

**Release** runs on a stance change, `PetAttack`, `Hold(handle, false)`, `ClearPet` and plugin unload. It clears the walk-back goal (`AiMove_Server` `TargettingMode` None, `TargetPosition` zeroed) and writes `Active` back to 1 only when all three hold:
- Hold saved 1.
- `Active` still reads the 0 Hold wrote. Something that set it since, such as `Engage`, a buff or the registry, wins.
- No buff on the unit carries `DisableAggroBuff` with `TargetDontAttackOthers` (checked by walking the unit's `BuffBuffer`). Such a buff owns `Active` until it ends.

Otherwise nothing is written and the game's current value stays. When Hold begins while such a buff is on the unit and `Active` reads 0, Core saves 1 rather than 0: the 0 came from the buff. Otherwise a unit held right after a spawn buff would stay passive for good once the buff ends. Whether `Buff_SpawnBuff_Skeleton_EarthCrawl` carries `DisableAggroBuff` is not known: it is not in the research notes, and the dump has no prefab data. The rule covers it either way.

A unit without `AggroConsumer` is still held in place, and nothing is saved or written back for it.

**Attack.** Every step:
- the aggro target is set to the target and `Active` is forced to 1. This is the `Engage` write, made when either is wrong. `Engage` also forces `Active` to 1; on a held unit the next hold step writes 0 again.
- `Follower.Followed` is cleared.
- Combat is posted when the tree is not in Combat.

When the target is dead (`Dead`, `Health.IsDead` or health 0, which is how a downed V Blood reads) or gone, the pet goes back to Follow in the same step. A pet that is itself dead (`Dead` or `Health.IsDead`) drops out of the loop.

**Unload, despawn and crashes.** A despawned, disposed or dead unit drops out of the loop, and nothing needs writing back. Plugin unload runs the release on every held unit whose handle is still live when it runs, then forgets every entry.

The hold's state is on the unit, not only in Core. `AggroConsumer` carries no persistence exclusion in the dump, so a world save made while a unit is held stores `Active` 0 and the walk-back goal. After a crash or kill without an unload, no release runs. A held unit restored from that save keeps `Active` 0: it stays passive, and nothing in Core re-holds or releases it, because stances are not saved. It stays that way until it is despawned or `Engage` turns aggro back on. The same goes for `Permanent` below: a restored unit keeps `LifeTime {-1, None}`. Not live-verified.

**Offsets** (server dump, declared in `layout.txt`):

| Field | Offset |
|---|---|
| `AggroConsumer.AggroTarget` (`NetworkedEntity`) | 0x30 |
| `AggroConsumer.AlertTarget` (`NetworkedEntity`) | 0x3C |
| `AggroConsumer.Active` (`ModifiableBool._Value` @0x0) | 0x50 |
| `NetworkedEntity._WaitingForSync` | 0x8 |
| `DisableAggroBuff.Mode` (`TargetDontAttackOthers` = 2) | 0x0 |

Status: unit-tested (`PetStanceTests`: stance transitions, the save and restore rules, walk-back pacing without the handle, the `Step` re-check, release on unload and on leaving Hold), not live-verified. The snippet above is source-checked, not compiled.

### Spawn options: Permanent, DropLoot, Entrance

```csharp
var knight = ctx.Units.Spawn("CHAR_Unholy_DeathKnight", pos, new UnitSpawnOptions
{
    Permanent = true,                       // no 6 s lifetime
    DropLoot = false,                       // no militia loot when it dies
});
var skeleton = ctx.Units.Spawn("CHAR_Undead_SkeletonMage", pos, new UnitSpawnOptions
{
    Entrance = UnitEntrance.RiseFromGround, // crawls out of the ground
});
```

| Option | Default | Effect on the spawn tick |
|---|---|---|
| `Permanent` | false | A unit with `LifeTime` gets `{Duration -1, EndAction None}`, so it no longer dies or vanishes when the time runs out. A unit without `LifeTime` is unchanged. |
| `DropLoot` | true | False removes the unit's `DropTableBuffer`, `DropTableOnDeath`, `DropTableOnDestroy` and the `DropTable` tag. |
| `Entrance` | `None` | `RiseFromGround` applies `Buff_SpawnBuff_Skeleton_EarthCrawl` right after spawn, with `DebugEventsSystem.ApplyBuff` as auras are applied. An undefined value throws `ArgumentOutOfRangeException` before anything spawns. |

`UnitEntrance` is in namespace `Progenitor.Units`: `None = 0`, `RiseFromGround = 1`. A refused write is one warning, and the unit keeps its lifetime, its drops or its vanilla entrance.

**Permanent.** The write is raw, at `LifeTime.Duration` @0x0 and `LifeTime.EndAction` @0x4 (`LifeTimeEndAction` is None 0, Kill 1, Destroy 2, RemoveBuffStack 3). `LifeTime` carries no persistence exclusion in the dump, so a world save keeps `{-1, None}`: a permanent unit restored from a save stays permanent, whether or not the plugin unloaded cleanly.

**Autosave brings orphans back.** A world autosave taken while API-spawned units exist stores them, and a server restart restores them with **no plugin handles**: nothing holds, releases or despawns them, and plugin unload does not reach them. Observed live on 2026-09-25: 42 army units and Dracula, `Permanent` pets of a demo plugin, came back after a restart and fought. They were cleared with `ctx.Units.ClearArea(center, radius, new ClearAreaOptions { Scope = ClearAreaScope.All })`. A plugin whose units must not outlive the session should clear its area on start, or avoid leaving units alive across an autosave. A unit spawned with `LifetimeSeconds` is the exception: its end is saved with it and Core enforces it after the restart (see [Lifetime across reload and restart](#lifetime-across-reload-and-restart)).

**DropLoot = false.** Removing the whole `DropTableBuffer` removes every drop table, whatever its trigger (OnDestroy 0, OnDeath 1, and the rest). Removing the two trigger components and the `DropTable` tag means no drop system matches the unit. Nothing is left for `OnDeathSystem`'s drop job or `DropInventorySystem`'s destroy job to roll. All four components are server-only and not networked, so clients see no change. The removal is carried in a world save like the rest of the unit. The removal is the non-generic `RemoveComponent`. An item in the unit's own inventory, which `OnDeathSystem`'s inventory drop handles, is not covered. None of the demo units is known to carry one; this is not checked live.

**Defaults** (live, 2026-09-25, isolated server):

| Unit | Prefab | Level | Power (physical / spell) | Health | Faction |
|---|---|---|---|---|---|
| Melee skeleton (Dunley armored) | `CHAR_Undead_SkeletonSoldier_Armored_Dunley` | 40 | 26.6 | 99 | 29 |
| Crossbow skeleton (Dunley armored) | `CHAR_Undead_ArmoredSkeletonCrossbow_Dunley` | 38 | 25.3 | 95 | 29 |
| Skeleton mage | `CHAR_Undead_SkeletonMage` | 44 | 38.8 | 173 | not measured |
| Death knight | `CHAR_Unholy_DeathKnight` | 60 | 55.3 | 82 | 21 |

- **Death knight lifetime.** `CHAR_Unholy_DeathKnight`, the player's Unholy ultimate summon, ships `LifeTime {Duration 6, EndAction Kill}` and dies 6 s after spawn. Writing `{-1, None}` right after spawn kept it alive when checked at 12 s. That is what `Permanent` does.
- **Death knight loot.** It drops `DT_Unit_Dunley_Militia_Rank02` on death, from a `DropTableBuffer` row with trigger OnDeath (1) and `DropTableOnDeath`. Militia loot (thick hide) was seen dropping from a summon. `DropLoot = false` turns this off. Other units carry `DropTableBuffer` too.
- **Entrance buff.** `Buff_SpawnBuff_Skeleton_EarthCrawl` (1820474368) plays a skeleton crawling out of the ground. This was seen on screenshots, not checked on video. Every skeleton also spawns with `Buff_General_Spawn_Unit` (396339796) by itself.
- **Factions.** Skeletons spawn on faction 29. The death knight spawns on 21, the players' faction, through `GetOwnerTeamOnSpawn` with no owner. Use `Faction` to change either.

Status: the three options are unit-tested (`PetStanceTests`: defaults, entrance table and validation), not live-verified. The lifetime write, the drop table and the entrance buff were checked live with the Dev ops, not through this API. `DropLoot = false` is not verified live: kill a unit spawned with it and check that nothing drops. The snippet above is source-checked, not compiled.

### Lifetime across reload and restart

`UnitSpawnOptions.LifetimeSeconds` and `IUnitHandle.TrySetLifetime(seconds)` end a spawned unit `seconds` of server running time after the call, whatever happens to the plugin, Core or the process in between. Time the server is not running does not count.

```csharp
var sheep = ctx.Units.Spawn("CHAR_Farmlands_Sheep", pos, new UnitSpawnOptions { LifetimeSeconds = 90f });
```

**The stamp is on the entity.** The write adds `Age` when the unit has none and sets `LifeTime { Duration = Age + seconds, EndAction = Destroy }`. The game's `LifeTimeSystem` destroys an entity when `EndAction` is not None and `Duration <= Age.Value`, and its query requires `Age`. Before this rule a prefab without `Age` kept its `LifeTime` forever. Neither component has a persistence exclusion in the dump, so a world save keeps the stamp. Nothing Core holds in memory is needed to end the unit.

**Core's sweep is the backstop.** On the server, Core checks units that carry `LifeTime` and `Health` (disabled ones included) once after Core install, 5 s after every world load and every 5 s after that. It acts only on API-spawned units: those wearing the `progenitor.core.api-spawned` marker, or held by a live handle. A unit more than 2 s past its `Duration` with `EndAction = Destroy` is despawned through the ordinary Units path, which removes buffs first and then destroys the unit for clients too. A live handle is disposed in the same step. A unit with a destroying `LifeTime` and no `Age` (one saved before this rule) gets `Age` at 0, so it ends `Duration` seconds after that sweep. Each sweep that acts logs `Units: lifetime sweep (<why>): …`. Entity ids are read and used inside one pass and checked against the world epoch; none is stored.

| Event | What ends the unit |
|---|---|
| Plugin unload (any reason) | A unit spawned through `ctx.Units`, or `Units.Spawn` with the plugin as `owner`, is despawned at unload, before its lifetime runs out. A unit another owner holds keeps its lifetime. |
| Core hot swap | Core uninstall despawns every live handle and hands unfinished despawns to Bootstrap. A unit that still remains (a despawn that ended stuck, or one Bootstrap could not hold) keeps its stamp; the game or the new generation's sweep ends it. |
| World save and load, server restart | The unit is restored with its marker, `LifeTime` and `Age`. `Age` keeps counting from the saved value, and the sweep after the load removes a unit that is already past its end. |
| Unit with `Permanent` | `LifeTime {-1, None}`: no limit, never swept. |

The sweep never touches a unit without the API-spawn marker, a vanilla `Kill` lifetime (the death knight's `{6, Kill}`), or a lifetime on a buff or projectile. `Age` and `LifeTime` are read and written raw at `layout.txt` offsets behind the size gate the other unit writes use. `LifeTime` is networked; `Age` is not.

Status: source-checked against the server dump and the native `LifeTimeSystem.Handle` and `__AssignQueries`, with 14 unit tests (`UnitExpiryTests`: plugin unload, generation change with a lost handle, restart from the persisted stamp, a saved unit without `Age`). Not verified live. Whether a given prefab ships `Age` has not been checked live (for example `CHAR_Farmlands_Sheep`); the rule adds it either way.

### Unit look (size, tint, glow, overlay)

A unit this plugin spawned can be drawn larger or smaller, recolored, made to glow, or filled with a flat color. Each client draws the look on the unit's model:

```csharp
var boss = ctx.Units.Spawn("CHAR_Wendigo_VBlood", pos, new UnitSpawnOptions
{
    Look = new UnitLook { VisualScale = 1.5f, Tint = new EffectColor(0.3f, 0.3f, 1f) }
});
ctx.Units.SetLook(boss, new UnitLook
{
    Tint = new EffectColor(1f, 0.25f, 0.25f), // red
    GlowIntensity = 5f,                       // drawn as tint x 5 in HDR: glows with bloom
});
ctx.Units.SetLook(boss, new UnitLook { Overlay = new EffectColor(1f, 1f, 1f), OverlayStrength = 0.5f });
UnitLook now = ctx.Units.GetLook(boss);       // a copy of the last look set, or a vanilla one
ctx.Units.ClearLook(boss);                    // natural size and vanilla colours again
```

`UnitLook` is in namespace `Progenitor.Units`. Its colors are `Progenitor.Effects.EffectColor`, and their alpha is ignored. Every field defaults to vanilla, and each `SetLook` replaces the whole previous look.

**Measured bounds.** Measured live on 2026-09-25 on Frostmaw in Dracula's arena, with a client-side sweep of size, tint and glow bounds. No step produced client errors or cost frame rate. "Hard" is what `Validate()` accepts; a value outside it throws `ArgumentOutOfRangeException`.

| Field | Vanilla | Recommended | Hard range | What was seen |
|---|---|---|---|---|
| `VisualScale` (x the creature's natural size) | 1 | 0.25 to 3 | 0.1 to 5 | 0.1: tiny and hard to see. 0.25: human-sized on Frostmaw; reads well. 2: a clear giant. 4: fills the view in an arena; the camera sits among its legs. 8 and above: the camera is inside the mesh. |
| `Tint` (per channel) | (1, 1, 1) | Brightest channel at 1; saturated colors such as (1, 0.1, 0.1), (0.3, 1, 0.3), (0.3, 0.3, 1) | 0 to 1 | Multiplies the texture. (1, 0.6, 0.6) is a soft pastel. All channels at 0.3 or below give a near-black silhouette. (0, 0, 0) is dark brown with some shading left. |
| `GlowIntensity` | 1 (none) | 4 to 6 | 1 to 8 | Multiplies the tint in HDR. 3 is bright. 4 to 6 glows with bloom, for example blue (1, 2, 6), green (1, 4, 1) or white (5, 5, 5). 10 is blown out to a featureless silhouette, hence the cap of 8. |
| `Overlay` (per channel) | — | any | 0 to 1 | The fill color. |
| `OverlayStrength` | 0 (off) | 0.3 to 1 | 0 to 1 | 1 is a solid color, 0.5 a half blend and 0 off. |

The glow is the tint's brightness: `_BaseColor = Tint x GlowIntensity`. So a glow on a tint whose brightest channel is 0.5 is half as bright. For a look brighter than vanilla, use `GlowIntensity`, not tint channels above 1. `_EmissiveColor` (up to 64) and `_EmissionColor` had no visible effect on this shader, so they are not used.

**Contract.**
- Server only, like the other `ctx.Units` calls. A disabled handle (client, or the `Units` module skipped), a disposed one or a unit that is gone throws `InvalidOperationException`. With spawn options, an out-of-range look throws before anything spawns, and a disabled handle skips it.
- **Client-side only.** The server sends the look to every connected client and to each client that joins later. A client without Progenitor sees the vanilla unit. Nothing about the unit changes on the server.
- **Independent of the hitbox.** `SetLook` never changes the hitbox, and `SetHitboxScale` never changes the model. At 0.25x the unchanged hitbox is far larger than the body; at 3x it is far smaller.
- A vanilla look is the same as `ClearLook`. Despawn clears the look. Plugin unload clears every look the plugin set; its units are despawned anyway. Server Core unload tells clients to drop every look.
- Visual scale is relative to each creature's natural model scale (Frostmaw 2, Bishop 1.2, Dracula and Arena Champion 1). The natural scale is recorded per model instance before the first write. `LocalTransform.Scale` does not change the model, so it is not used.
- **Hit-flash caveat.** The overlay uses `_BlinkColor`, the property the game's hit flash also uses. While an overlay is set, it replaces the hit flash within a quarter of a second. A look without an overlay never touches `_BlinkColor`, so the hit flash works as normal.
- **Materials without the properties stay vanilla.** Colors are written only to renderers whose material has `_BaseColor` or `_BlinkColor`. Creature bodies use `Shader Graphs/Shader_MasterLit` and are recolored. Effect materials such as `FresnelOverlay`, Dracula's smoke cape and the Arena Champion's `Effect` lack them and keep their look. A renderer with several materials gets one value for all of them.
- The look is not saved with the world. A unit restored from a save looks vanilla, and no handle comes back with it.

**Mechanism.**
- **Wire.** Core event `'PruL'` (`0x5072754C`). It is in Progenitor's reserved range, so no plugin can send on it or register it. The message is a fixed 48 bytes: the operation (set, clear or clear-all), the unit's `NetworkId` (normal index and generation) and the look.
- **When the server sends.** A set goes out once the unit's `NetworkId` is assigned (a frame or so after the spawn tick) and whenever the look changes. A clear goes out on `ClearLook` or despawn. On `PlayerConnected`, the joiner gets a clear-all followed by every current look. The client re-validates each received look against the same ranges and ignores anything else.
- **Client pass.** Every 0.25 s, each tracked `NetworkId` is matched to its client entity with one query over `NetworkId` + `HybridModelUser`. `HybridModelUser.HybridEntity` leads to the model's `UnityEngine.Transform`.
  - The first time a model instance is seen, its `localScale` is recorded as natural.
  - `localScale` is written whenever it is not natural x `VisualScale`.
  - `_BaseColor` and `_BlinkColor` are written through each renderer's own `MaterialPropertyBlock`: read the block, set only these properties, and write it back, so properties the game set are kept.
  - A unit that is not streamed in waits. A new model instance (streaming, or leaving and re-entering view) puts the old one back to vanilla and gets the look from scratch.
- **Removing a look** writes the natural scale back. It sets `_BaseColor` to the material's own value (white on `Shader_MasterLit`) and `_BlinkColor` alpha to 0, but only for the properties the look had written. The block is never cleared.
- **Unload.** Client Core unload puts every live model back to vanilla and drops all state. Leaving a server drops all state. With nothing tracked, the per-frame cost is two integer comparisons.

Logs: `UnitLooks: sent N unit look(s) to <name>.` (server, on join), `UnitLooks: ignored ... on the unit-look channel.` (client, at most 5 lines), `UnitLooks: restored N model(s) to vanilla on unload.` (client).

Status: the bounds above were measured live via scratch on 2026-09-25 (client-side writes on an idle Frostmaw; Bishop, Dracula and the Arena Champion were checked for shader and natural scale). This implementation is unit-tested (`UnitLookTests`) and was **verified live on 2026-09-25** (isolated server plus a client running Core, through a research harness calling `ctx.Units`). A Frostmaw looked right at each step: 2x red (model scale 2 to 4), green glow at intensity 5, blue overlay at strength 0.7, and `ClearLook` back to exactly vanilla (scale 2). After a disconnect and reconnect, the join sync showed the look on a new model instance at scale 4. After streaming out (500 m away) and back in, the new model instance was at scale 4 again. `RemoveAll` left no Frostmaw model object on the client. A Bishop (natural scale 1.2) went to 2.4 at 2x and back to 1.2 on clear; its effect-material trim stayed vanilla. Not verified live: an unmodded client joining, the hit flash with and without an overlay, and the restore on a client Core unload. The snippet above is source-checked, not compiled.

### Look for any character

`SetLook(Entity, UnitLook)`, `GetLook(Entity)` and `ClearLook(Entity)` put the same look on any networked entity with a model, such as a player character, without a handle:

```csharp
ctx.Events.On<PlayerConnected>(ev =>
{
    var character = ev.User.LocalCharacter._Entity;
    if (character != Entity.Null) ctx.Units.SetLook(character, new UnitLook { VisualScale = 1.5f });
});
```

- **Same pipeline.** The server table, the `'PruL'` wire and the client applier are the unit look's, keyed by `NetworkId`. The client finds the entity by `NetworkId` + `HybridModelUser` and scales `HybridModelUser.HybridEntity`'s transform, relative to the model's natural scale.
- **Refused.** An entity without `NetworkId`, or with one whose type is not `Normal`, throws `InvalidOperationException` (clients find a look by a normal network id). So do a missing entity, a call off the server or off the main thread, and a player character whose user is not connected.
- **Ownership.** The look belongs to the plugin that set it last. It is cleared on that plugin's unload, when the entity is destroyed, on a world load (epoch check), and, for a player character, when its user disconnects. The pass also clears it if the user reads offline. A player who reconnects has no look until a plugin sets it again, as in the snippet. The snippet is source-checked, not compiled.
- **New models.** A client that joins gets every look from the join sync (`ClearAll`, then each look). A new model instance on a client, from streaming, leaving and re-entering view, or a shapeshift, is restored and the look applied to the new one, scaled from that model's own natural size. This is the unit look's rebuild path, verified live for a Frostmaw after a reconnect and after streaming.

**Game default.** A player character (`CHAR_VampireMale`) has `HybridModelUser`, and its hybrid model transform's natural `localScale` is 1. Writing 1.5 on the client showed the character at 1.5x (live, 2026-09-25, scratch write).

Status: unit-tested (`UnitLookTests`: user-bound entries, disconnect removal, plugin release, natural scale 1). Not verified live through this API: the look on a player character on another client and on the player's own client, the clear on disconnect, a shapeshift, and a world load.

### Hide a unit's health bar

```csharp
var sentinel = ctx.Units.Spawn("CHAR_Undead_SkeletonSoldier_Armored_Dunley", pos, new UnitSpawnOptions { HideHealthBar = true });
ctx.Units.SetHealthBarHidden(sentinel, false);   // shows it again
bool hidden = ctx.Units.IsHealthBarHidden(sentinel);
```

| Member | Effect |
|---|---|
| `UnitSpawnOptions.HideHealthBar` | Default false. True hides the bar from the spawn tick; a clone that is not ready yet is one warning, and the unit keeps its bar. |
| `SetHealthBarHidden(handle, bool)` → `bool` | True puts Core's hide-HUD buff on the unit; false destroys it. Returns false, changing nothing, while the clone is not verified (one warning per Core generation). |
| `IsHealthBarHidden(handle)` | True when the unit wears the buff, including a unit restored from a save. |

**Mechanism.** The client's `GetCharacterHUDSystem.GetHideTargetHUDBuffs_Job` collects every buff that has `Buff` and `HideTargetHUD`, and the system skips the HUD (health bar and name) of those buffs' targets. The job runs on the client, so the client's copy of the buff must carry the component. Core registers `progenitor.core.hide-hud`, a `Buff_General_Haste` clone with a fixed id and GUID. It strips it exactly like the API-spawn marker on both sides ([Core inert clones on the client](#core-inert-clones-on-the-client)), and adds `HideTargetHUD { Type = -1 }` to the **client's copy only**. The write is a typed `Write<HideTargetHUD>` after checking the interop size against the dump (4 bytes), read back at the dump offset (`Type` @0x0, declared in `layout.txt`).

**Why not on the server prefab.** The server build instantiates `BuffUtility.HasBuff<HideTargetHUD>` (dump), and which server code reads it is not known. Leaving the component off the server copy keeps whatever that reader does out of play; the HUD job that matters runs on the client.

**Why a haste clone and not a vanilla `HideTargetHUD` carrier.** The vanilla carriers bring effects on the unit that Core would have to strip on both sides: `AB_Interact_CoffinSleeping` (845443603) has `SpawnSleepingBuff`, `ServerControlsMovementBuff`, `ServerControlsRotationBuff`, `Age`, team components and `BuffModificationFlagData`; `AB_Vampire_Sword_Shockwave_Recast_TravelEnd` is a travel buff; `Buff_General_Spawn_VBlood_MidGame` has `DisableAggroBuff`, `VbloodGhostBuff_Shared` and a lifetime script. The haste clone's strip is already verified on both sides, and none of those components is present to act. No sleeping, travel, movement-control or lifetime component is on the clone.

**Why `Type = -1`.** `HideTargetHUDType` is a `[Flags]` enum: `None` 0, `CharacterHUD` 1, `TargetInfo` 2. The vanilla carriers read -1 on their server prefab: `AB_Vampire_Sword_Shockwave_Recast_TravelEnd` (821686839) and `AB_Interact_CoffinSleeping` (845443603), read live with the Dev bridge `prefab` op on 2026-09-25. So -1 is the value the client code is exercised with, and it covers both the bar over the unit and the target info. Any flag test that works for vanilla -1 works for it; 3 has never been observed.

**Lifetime.** Applied through `DebugEventsSystem.ApplyBuff`, as auras are, and checked again at 1 s and 5 s. The buff is saved with the unit (fixed GUID). Plugin unload destroys it on every unit that plugin hid and that still exists. A world load forgets the per-plugin record; the restored buff stays, and `IsHealthBarHidden` still reports it.

**Game defaults.** `HideTargetHUD` is on `AB_Interact_CoffinSleeping` (845443603) and `AB_Vampire_Sword_Shockwave_Recast_TravelEnd` (821686839), both `Type` -1 on the server prefab (bridge `prefab` op, 2026-09-25), and on `Buff_General_Spawn_VBlood_MidGame` (-184730451) and `Buff_Wendigo_VBlood_Downed` (-1720429443) (component lists in the 2026-09-24 boss-AI research; `Type` not read).

What a client without Progenitor shows is not verified.

Status: source-checked, built and unit-tested (`InertCloneRulesTests`). Not verified live: that the bar and name disappear on a Progenitor client with the component on the client copy only, that the target info hides too, that the unit is still targetable and attackable by players and AI, `false` bringing the bar back, and a save and restart. The snippet is source-checked, not compiled.

### Join a character's team

```csharp
// A player's army: the player's own projectiles pass through it, and its bars show as allied.
var skeleton = ctx.Units.Spawn("CHAR_Undead_SkeletonSoldier_Armored_Dunley", spot, new UnitSpawnOptions
{
    Faction = "Faction_Players",
    TeamOf = playerCharacter,            // Entity; default Entity.Null keeps the unit team
});
ctx.Units.JoinTeam(otherUnit, playerCharacter);
```

| Member | Effect |
|---|---|
| `UnitSpawnOptions.TeamOf` | Default `Entity.Null`. Otherwise the unit joins that character's team on the spawn tick. A refusal is one warning; the unit keeps its team. |
| `JoinTeam(handle, Entity character)` | Copies the character's `Team.Value` and `TeamReference` onto the unit now. Throws `InvalidOperationException` for a disabled or disposed handle, a gone unit, a character that is `Entity.Null`, the unit itself or missing, when either lacks `Team` or `TeamReference`, or while the unit wears a buff with `ModifyTeamBuff`. |

**What is written.** `Team.Value` (@0x0) and `TeamReference.Value`, a `ModifiableEntity` whose entity is `_Value` @0x0, with field pokes on the `GetComponentDataRawRW` overlay (offsets in `layout.txt`, the path the faction write uses). The unit's own `Team.FactionIndex` (@0x4) is **kept**: it is what the unit's AI and the `Faction` option use to decide whom it fights, and the live test below kept it and the army still fought Dracula. Checked again at 1 s and 5 s against the character's team at that moment and written again with a warning if something reset it. `TeamReference.Value` is a modifiable field owned by the game's modification registry (`ModifiableFieldId.Create<TeamReference, ModifiableEntity>`). A buff with `ModifyTeamBuff` (`UpdateModifyTeamBuffSystem`) owns it while it lasts, and the registry puts the previous team back when it ends. So `JoinTeam` refuses while the unit wears one, and a re-check that finds one only warns and does not write. Nothing is restored on unload or despawn, and there is no leave call: join another team, or despawn.

**Game default (live, 2026-09-25, isolated server).** API-spawned units sit on the generic unit team, not their owner's:
- Units spawned with `Faction = Faction_Players` read `Team.Value` 2 (`Team.UNIT_TEAM`) and `TeamReference` = the unit-team entity, the same as Dracula's.
- A player reads `Team.Value` 11 and `TeamReference` = their own team entity.
- So the player's projectiles (Corrupted Skull) treat such an army as targets.
- After each army unit's `Team.Value` and `TeamReference` were written to the player's (by scratch, not this API), two skulls fired through the army's ranks damaged no one and hit Dracula. The army still fought Dracula, and its health bars on the client turned blue (allied).

**Team and ally-safe hits are separate.** [Ally-safe hits](ai.md#ally-safe-damaging-hits) add a guard to certain vanilla hit conditions, applied only when the caster wears Core's API-spawn marker; they do not read or change `Team`. `JoinTeam` changes the unit's team, which every vanilla ally check reads, including the player's own hits. Use `JoinTeam` to make a player's hits spare their units.

Status: unit-tested (`UnitTeamTests`: what is copied and kept, the match rule, refusals). The mechanism was verified live by scratch writes (above); this API is not live-verified. Not verified: persistence of `TeamReference` across a save and restart, a buff that modifies `TeamReference`, and the unit's reaction to the character's other allies. The snippet is source-checked, not compiled.

### Unit aura

A unit this plugin spawned can wear a server-applied aura. `UnitAura.Elite` is the elite-affix ring: a purple ring and glow around the unit that the game itself draws.

```csharp
var boss = ctx.Units.Spawn("CHAR_Wendigo_VBlood", pos, new UnitSpawnOptions { Aura = UnitAura.Elite });
bool on = ctx.Units.SetAura(boss, UnitAura.Elite); // false: Core has not verified the clone inert yet
UnitAura now = ctx.Units.GetAura(boss);             // read from the unit's buffs
ctx.Units.ClearAura(boss);                          // the ring goes away
```

`UnitAura` is in namespace `Progenitor.Units`: `None = 0`, `Elite = 1`.

**How it differs from `UnitLook` glow.** `SetLook` glow is a client-side HDR tint on the model's material, drawn by Core on each Progenitor client; the server sends only numbers. An aura is a buff on the unit on the server. The game replicates it and draws the ring from the buff's own visual sequence, with no Progenitor client code involved. The two can be combined.

**Contract.**
- Server only, like the other `ctx.Units` calls. A disabled handle (client, or the `Units` module skipped), a disposed one or a unit that is gone throws `InvalidOperationException`. A value that is not a named `UnitAura`, such as `(UnitAura)7`, throws `ArgumentOutOfRangeException`. With spawn options it throws before anything spawns.
- **No gameplay.** The vanilla buff `Shared_Affix_LevelAura_Self` is not applied. `Elite` is a Core clone of it, and Core strips the clone's server prefab so nearby units no longer gain the vanilla buff's +8 levels.
- **Only verified clones are applied.** `SetAura` returns false while the clone is not registered or not verified inert, and logs one warning per aura per Core generation. Nothing is put on the unit then. The clone is registered once prefabs load, so this matters only right after a server start. A spawn option that meets this spawns the unit without the aura.
- **One aura per unit.** Setting another aura removes the old one first. `SetAura(handle, UnitAura.None)` is the same as `ClearAura`.
- **Clear** destroys the aura's buff instance on the server (`DestroyUtility.Destroy`, reason `TryRemoveBuff`, the variant-marker removal). Clients drop the ring with the replicated buff. Nothing happens when the unit has none.
- **Checked again** 1 s and 5 s after `SetAura`, and put back if it is still wanted and missing.
- **Persists.** The buff instance is saved with the unit. The clone's id, and so its GUID, is fixed, and Core registers it again on every start, so a unit restored from a save still wears the aura. No handle comes back with it. `GetAura` reads the unit's buffs, so it reports a restored unit's aura; `ctx.Units.GetAura(Entity)` does this for an entity without a handle.
- **Unload.** Plugin unload despawns the plugin's units, and their auras go with them. Core unload leaves every aura in place, and the next Core generation registers the same clones.
- **Clients.** Every client that has the clone draws the ring. A Progenitor client builds the clone from the registry's definition packet, including the source's client-side visual sequence (`BuffSequence` exists only in the client world). A client without Progenitor has no clone for that GUID. What it shows is not verified; expect no ring.

**Mechanism.**
- **Clone.** At Core install on the server, Core registers `PrefabRegistry.DefineBuff("progenitor.core.aura.elite").CloneOf("Shared_Affix_LevelAura_Self")` under a Core owner, like the [API-spawn marker](#units). One table row per aura (`UnitAuraTable`) gives the id, the source and the strip list, so a new aura is one row.
- **The source's server prefab.** It has `Buff` (type 2, no `LifeTime`, so permanent), `CreateGameplayEventsOnTick` (interval 1 s), `GameplayEventIdMapping`, `SpawnPrefabOnGameplayEvent`, `GameplayEventListeners` and the `Affixes` tag. Every second it spawns `Shared_Affix_LevelAura_Trigger`, a 15 m `HitColliderCast` that applies `Shared_Affix_LevelAura_Buff` (`ModifyUnitLevelBuff` +8, `LifeTime` 2 s) to the units it hits.
- **Server strip.** Core removes these from the clone's server prefab: `CreateGameplayEventsOnTick`, `GameplayEventIdMapping`, `SpawnPrefabOnGameplayEvent`, `GameplayEventListeners` and `Affixes`, all seen on the source. It also removes `CreateGameplayEventsOnSpawn`, `ApplyBuffOnGameplayEvent` and `ModifyUnitLevelBuff`, which the source does not have today, so a game update that adds them cannot bring gameplay back.
- **Check.** None of those components remains, `Buff` is present, and any `LifeTime` does not end the buff. The visual sequence is never touched.
- **Timing.** The strip and check run at Core install and after every world load, retried every 5 s for up to a minute until the clone exists. They run again before every apply.
- **Restored instances.** A save's buff instances are loaded from the prefab before Core can strip it. After every world load, once the prefab is inert, Core strips and checks every live instance of an aura clone and removes any instance that cannot be made inert. Until that sweep runs, a restored unit's aura may briefly give nearby units the +8 levels (the level buff lasts 2 s).
- **Apply.** `DebugEventsSystem.ApplyBuff` with the unit as both user and character, the route the API-spawn marker and variant markers use.

Logs: `Units: aura Elite progenitor.core.aura.elite = <guid> registered (...)` at install; `Units: aura Elite not applied to <unit>: ...` when the clone is not verified; `Units: aura instances after world load: N checked inert, M removed.`; `Units: aura Elite missing on <unit> 1 s after it was set; applying again.`

Status: mechanism verified live on 2026-09-25: a plugin-defined clone of `Shared_Affix_LevelAura_Self`, applied from the server to a Frostmaw, shows the ring on a client (screenshot in the internal research notes). This implementation is unit-tested (`UnitAuraTests`), not live-verified. Not verified live: that the stripped clone still shows the ring, that nearby units get no +8 level buff, that `ClearAura` removes the ring, persistence across a restart, and what a client without Progenitor shows. The snippet above is source-checked, not compiled.

### Clear an area

`ctx.Units.ClearArea(float3 center, float radius, ClearAreaOptions options = null)` removes NPC units, their summons and loot drops within `radius` meters of `center` (horizontal distance, any height) and returns a `ClearAreaResult`. It exists because a test arena kept filling with leftover bosses, summons and loot.

```csharp
var result = ctx.Units.ClearArea(centre, 50f, new ClearAreaOptions
{
    IncludeUnattributedDrops = true, // boss loot records no owner; see below
    OnComplete = r => ctx.Log.LogInfo("clear: " + r),
});
```

| Option | Default | Meaning |
|---|---|---|
| `Scope` | `Owned` | `Owned`: only units this plugin spawned through `ctx.Units`, their summons, and attributable drops. `All`: every non-player NPC unit, summon and drop in the area, including the world's own NPCs and other plugins' units. |
| `IncludeUnits` | `true` | Units that are not summons. |
| `IncludeSummons` | `true` | Units with `Minion` or `IsMinion.Value`. In `Owned` scope, only when their `EntityOwner` chain reaches one of this plugin's units. |
| `IncludeDrops` | `true` | `ItemPickup` entities. In `Owned` scope, only drops whose `EntityOwner` chain reaches one of this plugin's units. |
| `IncludeUnattributedDrops` | `false` | In `Owned` scope, also every other drop in the area. Ignored in `All`. |
| `MaxPasses` | `4` | 1 to 16. |
| `OnComplete` | none | Called once, on the main thread, when the clear ends or is refused. |

**Why `Owned` is the default.** A clear deletes entities the caller did not list. A plugin must never delete the world's NPCs or another plugin's units unless its caller asks for that with `Scope = All`.

**Drops cannot always be attributed.** `ItemPickup` stores the item, amount and name, not who dropped it (dump). A drop counts as this plugin's only when it carries an `EntityOwner` whose chain reaches one of its units. Loot from a killed boss may carry no such owner, so `Owned` scope with the defaults leaves it. Set `IncludeUnattributedDrops` to remove every drop in the area; this also removes loose items the world or another mod left there. A drop whose owner chain reaches a player is never removed.

**Never touched, in any scope:** player characters, `User` entities, anything whose `EntityOwner` chain (up to four links) reaches a player character or user, owner chains that loop or run longer than four links, servants and prisoners (`ServantConnectedCoffin` / `ServantTypeData` / `Imprisoned`), castle-bound units (`CastleHeartConnection`; not `TileModel`, which NPC units also carry, live 2026-09-24), and mounts (`Mountable`) that this plugin did not spawn. Only entities with `BehaviourTreeInstance` (units) or `ItemPickup` (drops) are removed. Surveys include disabled entities, so downed bosses and units away from players are found.

**Passes.** Pass 1 runs inside the call; the result already holds its counts when `ClearArea` returns. Summons that appear after a survey are caught later: the call surveys again every second, on `MainThread.PostDelayed` owned by the plugin, until a pass finds nothing new and nothing it removed still exists, or `MaxPasses` passes have run. It then counts what is left 2.5 s later (`Remaining`, including removals still in flight). If the plugin unloads first, the remaining passes are dropped; unload removes its units and their summons anyway. A world reload during the clear ends it with a `Refusal`.

**Removal.** The same replicated path as `IUnitHandle.Despawn`: DIAG5 buff sweep, then `DestroyUtility.Destroy` alone (`BuffSweep.DestroyUnitReplicated`), so clients drop the entity too. This plugin's own units are disposed through their handles. An entity already being removed is not removed twice. In `All` scope, another plugin's unit is removed without its handle; that plugin's handle then reports the unit gone.

**Result.** `Enabled`, `Refusal`, `UnitsRemoved`, `SummonsRemoved`, `DropsRemoved`, `Total`, `Passes`, `Remaining`, `IsComplete`. On the client, or with the Units module skipped, the call returns a disabled, complete result with a `Refusal` and one Debug line, like the other `ctx.Units` calls. A radius that is not a positive number up to 200 m, or `MaxPasses` outside 1 to 16, throws `ArgumentOutOfRangeException`. The call throws off the game main thread.

Logs: `Units: ClearArea pass N/M scope=… new=… stillAlive=… units=… summons=… drops=…` per pass and `Units: ClearArea done …` at the end; the summon sweep logs `Units: summon sweep removed N summon(s) of despawned unit(s) …`.

Status: source-checked and unit-tested (`ClearAreaPolicyTests`: target classification by scope, player-owned exclusion, owner chains, pass and stop policy); not yet verified live. Example: `.bossvariants clear` in [samples/Progenitor.BossVariantsExample](../samples/Progenitor.BossVariantsExample/README.md).

### Behavior steps (M2.7, jobs FUNIT1 + FUNIT2 — landed, not live-verified)

`unit.Behaviour.Play(...)` / `unit.Behaviour.Stop()`. The spec's "behavior access" promise, built the way the research's v1 recommendation says and no other way.

**The rule: never a behavior-tree blob.** Progenitor does not compile, edit or inject `BehaviourTreeBlob` / `BehaviourTreeNode` — authoring a tree at runtime is not supported from the interop stubs, and a test in `UnitBehaviourTests` fails if either type ever appears in the Units module. Instead the unit runs a **vanilla** tree, the sequence forces `GenericEnemyState.Idle` so that tree neither wanders nor fights, and every step is a module-owned write on top, advanced from the same `Plugin.Frame` poll the rest of Units uses.

```csharp
var rat = ctx.Units.Spawn("CHAR_Critter_Rat", at, behaviourTree: "BEH_Farmlands_Animal_Standard");
rat.Behaviour.Play(
    UnitStep.Face(UnitId.From(ctx.CharacterEntity)),
    UnitStep.Wait(2f),
    UnitStep.GoTo(goal),
    UnitStep.Wait(1f),
    // "Idle" is a placeholder clip name: Animate has no server-side route on this build, so the
    // step is a documented 1.5 s hold reported Skipped. Nothing is written.
    UnitStep.Animate("Idle", 1.5f),
    UnitStep.Follow(UnitId.From(ctx.CharacterEntity)));
// later:
rat.Behaviour.Stop();
```

| Step | Write | Completes on |
|---|---|---|
| `UnitStep.Idle()` | `ExternalBehaviourTreeTransitionRequest { Idle }` | the tick that started it |
| `UnitStep.GoTo(pos)` / `GoTo(pos, timeoutSeconds)` | the same `AiMove_Server` ApproachPosition set `IUnitHandle.GoTo` writes | `UnitArrived`; **fails** on `UnitUnreachable`; **times out** after `timeoutSeconds` — default `UnitStep.DefaultGoToTimeoutSeconds`, **30 s** — with outcome `TimedOut`, one log line naming **which** of the two causes it saw (a discarded notification, i.e. something restarted the walk outside the sequencer, or `Units: GoTo timed out after 30s; the likeliest cause is that the unit's AI is not ticking — a unit does not path with no connected player in range (evidence §22)`), and the sequence ending `Failed`. `0` waits forever — kept, not refused, but no longer silent: one line a minute for as long as the wait lasts |
| `UnitStep.Follow(entity)` | the same Follower overlay + Follow BT request `IUnitHandle.Follow` writes | the tick that started it — Follow is a standing state, not a destination, so the handle's later 3 m Arrived is a plain event and not a step boundary |
| `UnitStep.Face(entity)` / `Face(pos)` | typed `Rotation` write | the tick that started it |
| `UnitStep.Wait(seconds)` | nothing — a **module timer**, never a behavior-tree `Delay` node | the timer |
| `UnitStep.Animate(name, seconds)` | **nothing — the name is a placeholder; there is no server-side route on this build** (see below) | the timer, whether or not the write landed — always `Skipped`, never a failure |
| `UnitStep.Then(action)` | your callback, on the game main thread | the tick that started it |

**One step at a time.** A failed step stops the sequence: `UnitSequenceCompleted` with `Failed` and the reason. The one exception is `Animate`, which reports `UnitStepOutcome.Skipped` and lets the sequence carry on — an animation is cosmetic and must not strand a patrol halfway. A second `Play` replaces a running sequence, reporting the old one `Stopped` before any of the new one runs — **unless the sequence it replaces had already run its last step**, in which case that one ends `Completed` and the new one starts against a stopped sequencer with no “replaced by a new sequence” publish at all (job FUNIT3, R56 finding 2: chaining a second leg from the last step’s `Then` is the most obvious use of `Then`, and it used to be published `Stopped 2/2` — every step run, the count saying so, the outcome word saying the opposite, and a genuine cancellation indistinguishable from a finished patrol). `Stop()` publishes `Stopped`; **a teardown that disposes the handle** (`Despawn`, Core uninstall, and the units `Units.RemoveAll` despawns for its own owner) stops the sequencer *silently* — the handle is on its way out and a subscriber would be handed a disposed unit. The one teardown that **does** publish is the plugin sweep on a sequence it leaves alive: see the sweep paragraph below. Events: `UnitStepCompleted` and `UnitSequenceCompleted` on `EventBus`, same ordering rules as everything else. Logs: one Info line per step start (`Units: step 3/6 GoTo (…) entity=…`) and one per sequence end (`Units: sequence end Completed 6/6 entity=…`).

**Idle is forced once, before step 1, and deliberately never again** (job FUNIT2, R55 finding 2 — six documents said this happened and nothing did it). `Play` posts `ExternalBehaviourTreeTransitionRequest { GenericEnemyState.Idle }` through the same driver seam `UnitStep.Idle()` uses, once, before the first step, and logs `Units: sequence forced Idle before step 1`. It is skipped when step 1 already *is* an `Idle()` — two identical posts in a row would only be noise. A refused force logs `Units: sequence could not force Idle (…); the vanilla tree may wander or fight between steps` and does **not** fail the sequence: it is a safety measure the caller did not ask for, and the steps after it are still worth running. It is not re-posted between steps, and that is a decision, not an omission: an Idle request on a unit that is off its leash does not park it — the tree goes to `Return` and walks the unit home (verified live) — so re-posting before every Face or Wait would *cause* the wandering it is meant to stop, on every unit that has just finished a `GoTo`. If a live run shows the tree taking the unit back, the next lever is `AiMove_Server.FreezeRotationWhenStationary` or pinning `SpawnTransform`, not a second Idle.

**A `Play` or `Stop` issued from inside a callback is deferred to the end of the tick** (job FUNIT2, R55 finding 1). Calling `Behaviour.Play(...)` or `Behaviour.Stop()` from a `UnitStep.Then` callback, from a `UnitStepCompleted` / `UnitSequenceCompleted` subscriber, or from inside a driver write does not run underneath the sequencer: the action is queued, one line says so (`Units: sequence Play deferred to the end of this tick (called from a step callback or an event)`), the running sequence stops advancing immediately, and the queue is applied **once each, in the order it was asked**, when the tick unwinds. Anything queued from inside a queued action joins the back of the same queue rather than recursing, and a deferred action that throws is logged and does not take the tick down. Validation is not deferred: a null step throws `ArgumentException` at the call site whether or not the `Play` was queued, and having thrown it has replaced nothing. Teardown (`Despawn`, `RemoveAll`, plugin unload, Core uninstall) is the one thing that is never deferred — it clears the queue, callbacks included, and an epoch bump stops a walk already on the stack. The measured symptoms of the old inline path were a still-running `GoTo` published as `Completed` and a first step skipped with no event at all.

**The handle’s own `UnitArrived` / `UnitUnreachable` are inside that same deferral, and an arrival belongs to the step that armed it** (job FUNIT3, R56 finding 1). The bus publication and the sequencer’s notify are now **one** sequencer entry, so a `Play` from a `UnitArrived` subscriber — “when the unit gets there, send it somewhere else”, the reason the event is public — is queued like every other callback’s instead of being applied inline. It used to be applied inline, because the publish ran between two ticks with the depth counter at zero: the subscriber’s new sequence went live, and then the *old* arrival was notified and completed the new sequence’s first `GoTo` on the spot. Measured, with the rat standing at (1, 0, 0) and the subscriber chaining a `GoTo` to (50, 0, 0): `GoTo 0/2 Completed`, straight on to `Follow`, `Completed 2/2`, forty-nine meters never walked — and the `UnitUnreachable` twin failed the brand-new sequence’s first step with the old sequence’s reason. Independently of the depth fix, every `GoTo` wait is stamped with the epoch and step index it was armed for, and a notification carrying any other stamp is discarded with a log line (`Units: arrival for step 1 of sequence 3 discarded; the sequence is waiting on step 2 of sequence 4 …`) rather than completing whatever is waiting now — which also covers a mod that calls `handle.GoTo(…)` directly behind a running sequence’s back. **What the sequence then does** (job FUNIT4, R57 finding 2): nothing can complete that step any more except its own arrival, an unreachable, or a `Stop`, so it waits out its `timeoutSeconds` and the timeout line names the discard instead of the player-in-range gate (“… was discarded while this one waited, so the likeliest cause is that something moved this unit outside the sequencer …”). At `timeoutSeconds: 0` it waits forever, and rather than refusing 0 or capping it — “wait forever” is a real contract for a caller whose way out is its own event — the sequencer logs one line every `UnitStep.NoDeadlineReminderSeconds` (60 s) saying how long it has waited and that nothing but an arrival, an unreachable or a `Stop` will end it, so a wedge shows up in the log within a minute instead of at the end of the session. **The reminder is a progress report and only adds a cause when it has one** (job FUNIT5, R58 finding 8): a discarded notification is the sequencer's own evidence and is named at once; otherwise the line names the no-players gate only when the unit has not moved since the previous reminder, and says the cause is *unknown from here* when the handle cannot report a position. Before this, every healthy `timeoutSeconds: 0` walk printed “no connected player in range” once a minute — the exact string an operator greps for after a real incident.

**A `Stop` queued behind a `Play` cancels it; a `Play` queued behind a `Stop` runs** (job FUNIT3, R56 finding 3). Order alone is the right rule for two `Play`s and the wrong one for a `Play` followed by a `Stop`, because applying a `Play` is not free: it posts the forced Idle and writes the first step before anything can stop it. So a deferred `Stop` **clears the queue ahead of it** — the canceled `Play` writes nothing, and **no** `UnitSequenceCompleted` is published for it, because a sequence that never started never ended; the only sequence reported is the one that was actually playing, `Stopped`, once. One line says what went (`Units: sequence Stop cancelled 1 deferred action(s) queued ahead of it; none of them was written to the unit`). A `Play` queued *after* a `Stop` is untouched, so “stop this, then start that” from one callback still works. What `Stop` does **not** do is un-write the unit: a `GoTo` already written keeps its `AiMove_Server` goal, and the sequencer only says so (`Units: GoTo wait step 2 of sequence 4 disarmed (sequence Stopped); its deadline is gone with it …`), because undoing a walk means posting Idle and an Idle request on a unit off its leash walks it home. Park the unit with an `Idle()` step if that is what you want.

**The plugin sweep ends what the unloading plugin owns and takes its delegates out of everything else** (job FUNIT4, R57 finding 1; widened by job FUNIT5, R58 findings 3, 4 and 6; split off `RemoveAll` by job FUNIT6, R59 findings 1 and 4; the reach is FUNIT3’s, R56 finding 10). When a plugin unloads, `SweepRegistrations` calls `Units.SweepForUnload(plugin, <the plugin’s BepInEx GUID>)`, which despawns the units that plugin owns by reference and then visits **every** remaining live handle. That wide sweep is the reloader’s entry point and nothing else calls it: the public `Units.RemoveAll(owner)` does exactly what it says — despawn the units recorded for that one owner — so retiring one subsystem of a plugin that is still running does not end its other subsystems’ sequences.

**Whose handle is it.** A handle is the unloading plugin’s when it was spawned with that plugin’s `owner` object — the same reference, or any object whose type the plugin’s assembly declares (a manager, a helper) — **or** with that plugin’s `pluginId`. Both are recorded on the handle and either alone is enough; the id the sweep matches against is the one the reloader passes it (the `[BepInPlugin]` GUID, the same string `ctx.Guid` records at `Spawn`), never something read off the owner object — no BepInEx plugin type exposes it as a property, which is why FUNIT5’s reflective lookup matched nothing in the game. The assembly test on its own was not: the API takes `object owner` and invites a bare token, and `new object()` is declared in the BCL, so a plugin’s own patrol was filed as a stranger’s and left playing with no event — the exact state R57 finding 1 was written to remove. If your `owner` is a bare token, pass `pluginId` too; `ctx.Units` fills both for you.

**What each one gets.** A sequence the unloading plugin owns ends `Stopped`, published on the bus (`UnitSequenceCompleted`, reason `plugin <assembly> unloaded`) unless the handle is already disposed, with the move wait cleared and logged. That no longer depends on the sequence holding a callback: a patrol of `GoTo` / `Wait` / `Face` steps — the shape `.rat patrol` itself uses — holds no delegate, and gating the visit on one left the commonest case unswept. **Anyone else’s** sequence — another plugin’s, or an ownerless static-API spawn — keeps playing, with only the `UnitStep.Then` steps written in the unloading assembly neutralized. **Any** method from that assembly counts, helper functions included: if plugin B writes `UnitStep.Then(PluginA.Rally)` — the normal way one mod extends another — that step names A, and A’s unload must not stop B’s patrol. Dropping the whole sequence was the earlier behavior and was wrong: it stopped a third party’s patrol with no event at all, left the handle `GoingTo` with its `AiMove_Server` goal written, and discarded the deadline.

**A neutralized `Then` keeps its slot and reports `Skipped`**, with the reason `callback dropped: <assembly> unloaded` (R58 finding 6). The slot is kept so the step indices a `UnitStepCompleted` subscriber sees still line up with the array its owner passed to `Play`; the outcome is `Skipped` rather than `Completed` so a subscriber that confirms its own side effect from the event can tell a no-op apart from a callback that ran. `Skipped` is the outcome that already means “the sequence carries on and this step did not do its job”, the same one a refused `Animate` reports.

**Reacting to the stop.** The published `Stopped` is there so a bystander can park or restart the abandoned unit, and by the time it fires the unloading plugin has no subscribers left — `SweepRegistrations` calls `EventBus.RemoveAll(plugin)` (`PluginReloader.cs:780`) before `Units.SweepForUnload(plugin, guid)` (`:825`) — the unload path is the sweep, never the public `RemoveAll`. A `Play` on **another** handle from inside that publication runs normally, and so does one aimed back at the unit that was just swept: the sweep ended that unit’s sequence but did not dispose its handle, so parking it is exactly what the event is for (job FUNIT6, R59 finding 6 — FUNIT4 dropped that one call, which made this paragraph name a purpose the code refused). Both are deferred the usual way, because the stack is still inside the publication, and both are applied once the unload stop itself has finished (`Units: sequence Play deferred to the end of this tick`). The unit itself stays in every case (it is not the unloading plugin’s to despawn) and one Info line per handle says what happened and to whom (`Units: dropped 1 Then callback(s) written in … from the sequence on entity=… prefab='…' (owner: …); those steps now do nothing and the rest of the sequence keeps running.` or `Units: stopped the sequence on entity=… (owner: …); the plugin that owns it, …, is unloading. The unit itself stays.`), with the “pass `owner:` to `Units.Spawn`” hint still printed for the ownerless ones.

**Face writes `Rotation`, not `AiMove_Server.ForceLookAtTarget`.** `AiForceLookAtTarget` has two non-None values, `Approach` and `Circle`, and the game's own tooltips call them "when moving towards or away from the target" and "when in circle pattern": it is a combat-approach facing *mode* read by `AiMoveSystem_Server.ShouldForceLookAtTarget(in AiMove_Server)` while the AI is moving the unit, not a "turn to face X now" command — and a Face step turns a unit the sequence has just parked with Idle. `Rotation` (`quaternion Value`) is also C# `unmanaged`, so it goes through the toolkit's typed `Write<T>` with `RawWriteGuard` vouching from `sizeof`, instead of a byte\* overlay over the bool-carrying `AiMove_Server`. If a live run shows the tree snapping the facing back, the next thing to try is `AiMove_Server.FreezeRotationWhenStationary` (@0x4D), which `GoTo` already clears.

**Animate has no server-side route on this build, and says so.** The earlier research mapping ("write `AnimationState`, `FixedString64Bytes AnimationName` at offset 0") is **stale**: the game's metadata says `AnimationState` is `double StartTime` @0x0 and an `AnimationNameHash` @0x8 — there is no string — and it is only ever carried by `AnimationStateBuffer`, which is declared `[ComponentInWorld(new[] { 4 })]`, i.e. `WorldType.Client`. Units is server-only, so that buffer is not on the entity; every other animation type in the metadata is Client-world or a `MonoBehaviour`, and no behavior-tree leaf plays a clip. So `Animate` refuses with one cited log line, the step is held for its duration and reported `Skipped`, and no guessed write reaches live memory. The real route is client-side and belongs to Effects / Buffs.

**Tree selection is a spawn-tick option.** `Spawn(..., behaviourTree: "BEH_…" | guid)` writes `BehaviourTreeBinding.PrefabGUID` in the same call as `Instantiate`, while the entity still carries `SpawnTag` and before `BehaviourTreeBindingSystem_Spawn` — `[UpdateInGroup(typeof(ReactToSpawnGroup))]` — resolves it into `BehaviourTreeInstance`. Selecting a tree **after** that frame is refused, not attempted: re-binding would mean re-adding `SpawnTag`, which re-runs *every* ReactToSpawn system on the entity. A GUID whose prefab has no `BehaviourTree` component is refused too, with one log line, and the unit keeps its own prefab's tree — tree selection is a preference, never a precondition, so the sequence still runs. **The claim is read back twice** (job FUNIT2, R55 finding 7). Immediately: the `BehaviourTreeBinding.PrefabGUID` just written must read back equal, or the call is a refusal (`… did not read back on <entity>; keeping the prefab's own tree.`) rather than a reported success — a write that a structural change or an unwilling archetype swallowed used to be announced as a bound tree. And one frame later, through a `MainThread.Post`: `BehaviourTreeInstance.TreeEntity` is what `BehaviourTreeBindingSystem_Spawn` fills from the binding, so reading it is the only thing that says which case a live run actually hit — either `Units: behaviour tree '…' guid=… bound on <entity> (BehaviourTreeInstance.TreeEntity=…)` or the warning `… was written but not bound on <entity>: BehaviourTreeInstance.TreeEntity is still null one frame later, so BehaviourTreeBindingSystem_Spawn did not pick it up and the unit is on its prefab's own tree.` That second read only logs: the `SpawnTag` gate above is a *proxy* for "the spawn group has not run yet" that nothing in the dump establishes, re-binding is refused anyway, so there is nothing to retry. Tree names are **not** in the game's metadata (they live in the prefab collection); the only vanilla tree this project has read off a live unit is `BEH_Farmlands_Animal_Standard` (guid `-1918585836`, evidence 2026-09-02 evening §11 step 2, on the sheep), which is what the sample uses.

**Not live-verified.** A unit does not tick its AI without a connected player nearby (evidence §22), so the live check is `.rat patrol` with the player standing next to the spawn.

### Sample commands

`samples/Progenitor.Sample` is the live consumer. `.rat` is server-side like `.ember`. Pose is the calling character's `LocalToWorld` (forward) with +X fallback.

| Command | Server log prefix | Reply |
|---|---|---|
| `.rat` | `Sample: rat spawned entity=… pos=… distance=3` | `Spawned rat index:version at (x, y, z)` — `CHAR_Critter_Rat` 3 m in front of the caller. |
| `.rat 5` | same, `distance=5` | Same, 5 m in front. |
| `.rat follow` | `Sample: rat follow entity=…` | `Rat index:version following you.` |
| `.rat go` | `Sample: rat go entity=… pos=…` | `Rat index:version walking to (x, y, z).` — 6 m ahead of the caller. |
| `.rat patrol` | `Sample: rat patrol entity=… tree=BEH_Farmlands_Animal_Standard goal=… steps=6 Animate('Idle') is a placeholder; no server-side route` | `Rat index:version patrolling: face, wait, walk to (x, y, z), hold 1.5 s (Animate('Idle') is a placeholder; no server-side route), follow.` — spawns on the quiet vanilla tree, `Play` forces Idle, then Face → Wait 2 s → GoTo 6 m → Wait 1 s → the 1.5 s placeholder hold → Follow. The step stays in the sequence because it really does pause the patrol for 1.5 s and a live observer needs to know where that pause comes from; the reply says it is a placeholder rather than calling it an animation (R55 finding 8). |
| `.rat stop` | `Sample: rat stop entity=… wasPlaying=True` | `Stopped rat index:version's sequence.` |
| `.rat off` | `Sample: rat off despawned N` | `Despawned N sample rat(s).` |

The Units module Info-logs `Units: spawned` / `arrived` / `unreachable` / `despawn` / `swept N unit(s) of <plugin>` / `added Follower to <entity> (prefab had none)`. Follow / GoTo / client-only / disabled-handle lines are Debug. Client `.rat` Debug-logs `Units: Spawn('CHAR_Critter_Rat') is server-only; returning a disabled handle.` A successful `.rat follow` on a rat that lacked the component logs `Units: added Follower to <index>:<version> (prefab had none)` then Debug `Units: follow entity=…` then Sample `Sample: rat follow entity=…`.


### Blood and breakable props

`ctx.Units.SetBlood(character, "Draculin", 100f)` gives a player character a blood type and quality with a full pool. It raises the game's own admin "consume blood" event on the server (`ConsumeBloodAdminEvent` with `NetworkEventType.IsAdminEvent`, `FromCharacter` and `ReceiveNetworkEventTag`, the entity an admin console would send); `VariousMigratedDebugEventsSystem` applies it on its next update, and the blood is saved with the character like any the player drinks. True means queued.

`ctx.Units.ClearBreakables(center, radius, keep)` removes the tile models an area's spawn chains placed (`SpawnChainChild`) so nothing shatters mid-fight. In Dracula's throne hall those were the breakable banners, braziers, vases and pillars; nothing checks that a prop is breakable, so a chain-placed gate or interactable elsewhere would go too (check the area, or `keep` it). They go through `DestroyUtility.Destroy` and the chains are left alone, so the area's reset restores them. Some restore at once: Dracula's four arena pillars (`TM_Destructible_DraculasCastle_Pillar01`) came back within about 20 s, and his AI uses them as points of interest, so the necro sample keeps them (`keep: guid => guid == 1934867279`) and hides them on its client instead.

## Ai

`Progenitor.Ai`, `ctx.Ai` (`PluginAi`). Server-only boss AI; client calls throw. Full contract, safety rules, defaults and persistence: [ai.md](ai.md). API rows: [api.md](api.md#ai). The boss AI anatomy research is in the internal research notes.

- **Reads:** `Describe(bossPrefab)` decodes the tree blob, every cast-option row (runtime cooldown pair, not the dump's editor struct), boss-health gates, the phase script, team and faction masks; `Observe(unit)` reads state, target, current cast and cast history.
- **Writes, each returning a tracked restoring handle:** prefab-wide row edits and health gates (private condition copies, rows repointed), per-instance phase threshold, per-instance faction (`FactionReference` and `Team.FactionIndex` together: the game derives the index from the reference at spawn), per-unit tree variants (a private blob on a private Prefab-tagged tree entity, reached through `ExpectedTreeEntity`), and experimental per-instance cast-option clones.
- **Never:** repoint a shared tree prefab's blob (Burst aborts the server when a running node's type changes, INC-20260924-01), edit a variant after its first apply, free a private blob, or select a cast-option row by index.
- **Layout:** every offset and stride is a `Layout` constant declared in [`layout.txt`](../src/Progenitor.Core/Layout/layout.txt) under the four `Ai:` blocks and resolved against the server dump. The row stride (0x78), condition element stride (0x28) and tree node stride (0x18) are derived from declared fields; `AiCodecTests` checks all three against captured bytes.
- **Game-free half:** `AiModels.cs`, `AiNames.cs`, `AiCodecs.cs`, `TreeVariant.cs`, `VariantBook.cs` compile into the test project. `AiRuntime.cs`, `PluginAi.cs`, `AiVariants.cs` are the live half.
- **Core tree repair:** `Ai.TreeRepair`, installed with Core on the server, points every unit whose saved tree reference came back null at its binding's prefab tree after each world load (live: a private tree reference is not saved). Decision logic: `TreeRepairPolicy` (unit-tested).
- **Status:** landed, unit-tested on captured Frostmaw bytes; faction spawn options and the Sentinel tree variant verified live on Core b366e2da; the rest is not. Example: [`samples/Progenitor.BossVariantsExample`](../samples/Progenitor.BossVariantsExample/README.md).

## Access

Roadmap M3 item 5. The research on what "private game member" even means on an Il2CppInterop stack, with the case table, is in the internal research notes. Namespace `Progenitor.Access`, sources in `src/Progenitor.Core/Access/`, generator in `tools/accessgen/`.

**What it is for.** V Rising is IL2CPP, so a mod never references the game — it references the interop assemblies BepInEx regenerates from `GameAssembly.dll`. Il2CppInterop emits **everything it emits as public**, so "private" almost never means "unreachable" here. The access layer exists for the four cases where it does: a member the interop generator skipped, an IL2CPP **static** field (the generator models few of them), a member reached only through the interop's own `NativeFieldInfoPtr_*` / `NativeMethodInfoPtr_*` statics, and a method you need as a **function pointer** rather than as a stub call. What it is emphatically *not* for is layout: a class field offset is resolved at runtime by the stub and a struct field's `[FieldOffset]` is baked into the regenerated interop assembly, so **no offset is ever written down**, here or anywhere else in Progenitor (the same boundary `Ecs/RawWriteGuard`, the dump-stride rule and the [compatibility rewriter](#compatibility-rewriter) draw).

**The shape**: a text file listing members to *reach*, compiled ahead of time: no bytecode transform, no source generator, no build-time step. The generated file is plain C# and is checked in.

`src/Progenitor.Core/Access/access.txt`:

```
access v1                                  # required first directive
assembly ProjectM.Shared                   # sets the assembly for the lines that follow
type   ProjectM.Internal.Thing             # existence only; no accessor members
field  ProjectM.SomeType someField         # generic get/set + an offset diagnostic
field  ProjectM.SomeType someField : System.Single   # plus a non-generic typed pair
field  ProjectM.SomeType someCounter static          # an IL2CPP static field
method ProjectM.DeathUtilities Kill        # "the only overload"
method ProjectM.SomeType Run(System.Int32, System.Single)   # a named overload
```

Nested types use the metadata spelling `Outer/Nested`. `#` comments, blank lines and whitespace never reach the model, and declarations are sorted canonically before generation — so a reordered or re-commented file produces byte-identical output. A method with no parameter list means "the only overload"; two overloads and no list is a loud failure at first use, not a guess.

**A field line must carry its type.** `il2cpp_field_get_value` takes no size: it copies the *field's* width into whatever buffer it is handed, so a generic accessor whose `T` is narrower than the field writes past the caller's local, and a wider one makes the runtime read adjacent stack into the game's object — silently, in both directions (R39 finding 3). The declared type is what makes that checkable, so `: <Type>` is required rather than decorative, and `AccessResolver` resolves the field's own width once (`il2cpp_field_get_type` → the class's value size, or pointer size for a reference type) and refuses a mismatched `T` with a sentence naming both widths:

```
Access: ProjectM.SomeType.someFlags is 8 bytes on this build; the accessor's type is 4 bytes. Reading or writing it would run past the value; correct the access.txt type
```

**And the width is not the whole question — a reference field is refused outright.** A reference-typed field is `IntPtr.Size` bytes wide, which is the honest size of its pointer slot and therefore *passes* the width check for `sizeof(long)`. A gate that returns a true number about the wrong question is worse than no gate: `Access.X.handle<long>(instance, 0x4141414141414141)` would store an arbitrary integer into a slot the IL2CPP GC dereferences as an object on its next scan, and the crash lands somewhere else on a later frame (R40 finding 3). So `AccessResolver` classifies every field once — `il2cpp_class_is_valuetype` on the field's own class, cached beside the width — and asks the classification **before** the width:

- a **reference** field refuses every unmanaged accessor, whatever its width says, with a sentence naming the fix (*"…is reference-typed on this build … Declare it with the trailing `ref` modifier … and use the `handleObject` pointer accessors and `handleOffset` instead"*);
- a **value** field refuses the raw object-pointer accessors, so `il2cpp_field_set_value_object` is never handed a `float3`;
- and the generator emits the `…Object` pair **only** for a line that says `ref` — it used to emit it for every instance field, which put that call one keystroke away.

**The classification fails closed, and that is deliberately not what the width does.** A build where `il2cpp_field_get_type` or `il2cpp_class_from_type` hands back zero — a generic parameter, a pointer type — or where any of the four calls throws, produces an *unclassified* field, and an unclassified field refuses **every** accessor: the blittable pair, the static pair and the object-pointer pair alike, with a sentence naming the step that did not answer (*"…could not be classified as value- or reference-typed on this build (il2cpp_class_from_type returned 0); every accessor is refused rather than guessed…"*). Both gates used to return silently on it, so the single condition "the runtime would not say" opened the classification *and* the width check together — fail-open on exactly the condition the classification gate exists for, and invisible in the log because the field resolved (R41 finding 1). The failure is cached as a failure, so the refusal is the same on the thousandth call as on the first and a native call that already threw is not re-asked on the game's thread. The width's own unknown stays permissive, which is right for a **number**: an unreadable size would otherwise break every field on a build without `il2cpp_class_value_size`, and the cost of letting it through is a bounded stack read or write of the wrong length. An unreadable classification is not a number — it is an integer written into a slot the GC dereferences, and the crash lands somewhere else entirely. The decision lives in `Access/AccessFieldShape.cs`, apart from the four native calls that answer it, so both failure branches are drivable from a game-free test.

That makes `ref` a *checked* claim about the field rather than an unchecked claim about the author's intent. It is checked twice: at resolution, as above, and **at generation time**, where the tool has the interop assemblies in front of it — or, on a machine that does not, the generated file says in its own header that the second check never ran. `accessgen` reads them with a `PEReader` (metadata only — no `Assembly.Load`, no Il2CppInterop reference, no game process, so the tool still runs on a machine that never installed V Rising), asks whether each declared field's interop member is a class or a struct, and **refuses to write anything** when a line disagrees:

```
accessgen: ProjectM.GameDataSystem.RandomLocalizationKeys is reference-typed in ProjectM.Shared (the interop declares it as a property, reference-typed), but the declaration has no 'ref' modifier: an unmanaged accessor over an object pointer is the R40 finding 3 hazard. Write 'field ProjectM.GameDataSystem RandomLocalizationKeys : ProjectM.RandomLocalizationKeys ref'.
```

The folder comes from `--interop <dir>`, else `VRISING_INTEROP_DIR` / `VRISING_GAME_DIR` — and **a declaration file with field lines is not generated without one**. It used to be: the missing folder printed one informational line among the generation output and generated anyway, so on any machine with no game install — including the machine running the drift-check command this document used to give — the generation-time half of the gate simply did not run, and because the emitted text was byte-identical either way, `--check` reported green on a file nothing had verified (R41 finding 3). *A gate that cannot verify is a gate that did not run.* Skipping it is now an explicit `--unverified` (the old `--no-interop` still parses), and it lands in the artifact:

```
//     verified:     false  (NOT checked against the interop assemblies — generated with --unverified)
```

plus a `public const bool InteropVerified` and a `Describe()` that prints it — `access: resolved=1 declared=2 interop-verified=true`, which is the `.access` line and the resilience diagnostic. **`Describe()` is emitted, not hand-written**, so every facade the tool writes a `DeclaredCount` into can print the verification state of *its own* artifact: a hand-written one lived in Core alone, which left a plugin's `InteropVerified` const with no reader and made the plugin's `.access` print Core's answer beside the plugin's accessors (R42 finding 3). `resolved=` is the shared `AccessResolver` cache, one per Core generation, whichever facade asks. That header line **is** compared by `--check` (unlike `build:`, which is a decoration nothing resolves against), so a verified file and an unverified one are no longer the same bytes, and `--check` names which way it moved rather than saying "the declarations drifted". A file from before the line existed reads as `false`: no record is not a verification. A declaration file of **methods only** is verified vacuously — there is no `ref` modifier to contradict — and needs no folder. A field the interop emitted no member for — a typo'd type name, a member an update removed, the case `il2cpp_class_get_field_from_name` still resolves at runtime — is reported per line and is not an error, but it does mean this emission was not fully checked, so it needs the same `--unverified` and produces the same `verified: false`.

The checked-in `samples/Progenitor.Sample/Access/Access.Generated.cs` says `verified: true`: it declares one `ref` field line, and it was generated with the interop folder in front of the tool. It has to be. An artifact with a field line and `verified: false` cannot be checked green anywhere — on a machine with the folder the regenerated text says `true` and `--check` calls the checked-in file `Stale`; on one without, the tool refuses before it compares (R42 finding 3). Core's `access.txt` declares methods only, so it is verified vacuously and needs no folder. `AccessCheckTests` runs both halves: the Sample's checked-in header must say `verified: true`, and the verification itself is driven — `Verified` against the real interop metadata where the game is installed, `Refused` (never `Unverified`, never `Stale`) where it is not.

**A declared overload is resolved by name, never by position.** When a method name has more than one `NativeMethodInfoPtr_*` static, the layer derives the field name Il2CppInterop's own generator would have given the declared overload — `NativeMethodInfoPtr_{name}_{access}[_Virtual…]_{return}[_{param}]*_{index}`, with `byref_`/`ptr_` prefixes and `Name_1_Arg` for a generic instance — and matches on the return-plus-parameters tail, which one overload can carry and no other can (`Access/AccessNaming.cs`). **The match is anchored on a token boundary**, not a suffix: one overload's tail can be a suffix of another's — `Single_NativeArray_1_float2` is a suffix of `Single_Int32_Single_NativeArray_1_float2` — and a plain `EndsWith` therefore either counted two candidates for the shorter overload (loud) or, when the shorter one's own static is absent on this build, handed back the **longer** overload's pointer with no diagnostic (R40 finding 6). So the tail must occupy the last N `_`-separated segments *and* every segment before it must be one of the interop's visibility/modifier words (`Public`, `Private`, `Internal`, `Protected`, `Abstract`, `Virtual`, `Static`, `Final`, `New` — the grammar above says the head is exactly that and nothing else). A return type that merely starts with one of them is a separate segment and does not confuse the anchor: `…_ReadStaticTransformIndex_Public_Static_StaticTransformIndex_byref_NetBufferIn_0`. Nothing counts positions in `Type.GetMethods()` or `Type.GetFields()`: neither promises an order, the two orders are not the same order, and an inherited same-named method shifts one and not the other, so the old ordinal mapping could return a valid pointer to the wrong function (R39 finding 2). Zero matches or more than one is an `AccessException` naming the derived signature, which is the same refusal `Il2CppNative.MethodPointerByPrefix` and `GodMode`'s `KillEntity` comment already make.

Regenerate after any edit (deterministic; `--check` is the CI form and exits 2 on drift). `--check` compares the declaration hash and the generated body and **ignores the `build:` header line**, so a game build bump is not reported as a stale file; with no `--build` the tool keeps the stamp the checked-in file already carries instead of replacing it with `unspecified` (R39 finding 5). `dotnet test` runs the gate — `AccessCheckTests` regenerates from the checked-in `access.txt` and requires the checked-in `Access.Generated.cs` back byte for byte — so CI covers it without invoking the tool:

```
dotnet run --project tools/accessgen -- \
  --declarations src/Progenitor.Core/Access/access.txt \
  --out src/Progenitor.Core/Access/Generated --build <build id>
```

The generated header carries the build id it was generated against and the sha256 of the canonical declarations, so a stale file is visible without regenerating it.

**Using an accessor.** Everything hangs off `Access` (`using Progenitor.Access;`), one nested class per declared type:

| Declaration | Generated |
|---|---|
| `field T f : X` | `X Access.T.f(instance)` / `Access.T.f(instance, X)`; the generic pair `V Access.T.f<V>(instance)` / `Access.T.f<V>(instance, value)` (`V : unmanaged`, refused when `sizeof(V)` is not the field's width, and refused outright when the field is reference-typed); `Access.T.fOffset()` |
| `field T f : X static` | the same without the instance parameter |
| `field T f : X ref` | only `IntPtr Access.T.fObject(instance)` / `Access.T.fObject(instance, IntPtr)` and `Access.T.fOffset()` — a field holding an object pointer has no blittable reading, and the pointer pair is refused if the field turns out to be value-typed on this build |
| `method T M` | `MethodBase Access.T.M()` (a Harmony target) and `IntPtr Access.T.MPointer()` (a native detour target) |
| `type T` | `System.Type Access.T.Type` |

A method accessor hands you the member, not a call: invoke through `ctx.Patches`, `DetourSite`, or `il2cpp_runtime_invoke`, the same as before. Reads and writes go through `il2cpp_field_get_value` / `il2cpp_field_set_value`, which do the offset arithmetic inside the IL2CPP runtime; `fOffset()` reports `il2cpp_field_get_offset` for a diagnostic and nothing multiplies it.

**When the build does not have it.** The first use throws `AccessException` with one sentence:

```
Access: ProjectM.SomeType.someField not found on this build; the address library/resilience report knows which build
```

and, ahead of that, the **resilience report already names it**: `tools/accessgen` stamps every declaration into the assembly as an assembly-level `AccessMemberAttribute`, and `Resilience/MetadataScan.cs` reads those attribute rows out of the same `PEReader` pass that reads the MemberRef tables. Without that, a generated accessor would be invisible to the scan — it resolves by *name* at first use, so its member is a string literal, not a metadata reference, and the report would have said "all bind" about a member nothing can bind. A declaration that named no overload carries the wildcard signature `*`, which the scan reads as "any member with this name", matching what the accessor asks of the build.

**Caching and lifetime.** Each member resolves once and is cached in `AccessResolver` for this Core generation; `Access.Reset()` empties it. The cache holds IL2CPP pointers and interop `MethodBase`s only — process-lifetime things from the default ALC — so a missed reset pins nothing.

**A miss is cached the same way, at every entry.** A member that is on no build throws its `AccessException` on *every* call, with the identical sentence — but the lookup behind it runs once: the refusal is recorded against the same key the answer would have used and rethrown from there. This is the policy R41 finding 1 settled for the field classification ("the refusal has to be the same on the thousandth call as on the first, and re-asking a native call that already threw is a per-call risk of throwing again on the game's own thread"), and it now holds at `FieldInfo`, `FieldOffset` (which is `FieldInfo`), `MethodPointer`, `Method` and the shape read alike, through one `Memoised` helper that owns every cache write. Only an `AccessException` is cached — and in practice that is every failure the resolver can observe, because `ResolveField` wraps the class constructor's throw and the native class lookup's throw into one before either escapes. So a transient fault caught behind one of those two wraps is cached like a verdict about the build, and `Access.Reset()` is the only escape from it; the typed catch keeps an *unwrapped* exception out of the table, which is what it is actually for (R66 finding 8). Before this, only the shape read followed the rule, so a missing member re-ran `RunClassConstructor`, a reflection lookup, a native class-pointer lookup and `il2cpp_class_get_field_from_name` on the main thread on every call — the file argued for the policy twelve lines above the site where the failures actually happen (R65 finding 7). The cost is the deliberate one: a member that only appears later in the process — a late-loaded image — stays missing until `Access.Reset()` or the next Core generation, so `Reset()` clears the refusals along with the answers. `Describe()`'s `resolved=` counts answers only; the refusals are counted apart in `AccessResolver.MissCount`, one entry per refused *member* — the shape read (`FieldWidth`, `FieldIsReference`, `FieldIsClassified`) asks `FieldInfo` before it memoizes, so a member that is on no build is refused once, under `FieldInfo`'s key, rather than once under each of theirs (R66 finding 8). `MissCount` has no output surface yet: nothing prints it, so a generation with a dozen permanently refused members shows only a smaller `resolved=` than the one before it.
*Closed 2026-09-05 (RD5 §9):* the one-line `Access.Reset()` call is in `Plugin.Uninstall` — `src/Progenitor.Core/Plugin.cs:200-201`, wrapped in a try/catch that logs a warning.

**Core's own first consumers** (`access.txt` above): `Ui/GameInputBlocker.cs` takes the `ProjectM.RotateCameraBlocker.GetConsumedInputs` detour target through `Access.RotateCameraBlocker.GetConsumedInputsPointer()`, and `GodMode/GodMode.cs` takes `ProjectM.DeathUtilities.Kill` as both a Harmony target (`Access.DeathUtilities.Kill()`) and a detour fallback (`Access.DeathUtilities.KillPointer()`). Both used to hand-roll the same lookups against the interop's generated statics.

**A plugin does the same thing.** Ship your own `access.txt`, run the tool with your own output folder, and check the result in:

```
dotnet run --project <progenitor>/tools/accessgen -- \
  --declarations MyMod/access.txt --out MyMod/Access \
  --namespace MyMod.Access --class Access --build <build id> \
  --interop "<game>/BepInEx/interop"
```

The generated code is plain C# with no Progenitor build-time dependency — it calls `Progenitor.Access.AccessResolver`, which your plugin already references. `--interop` (or `VRISING_INTEROP_DIR` / `VRISING_GAME_DIR`) is required as soon as your file has a field line; check the result in with `verified: true` in its header, or the drift check is red for everyone who runs it after you. Your facade gets its **own** `MyMod.Access.Access.Describe()` — `access: resolved=N declared=M interop-verified=X`, where the last one is about your file, not Progenitor's — so print that, not `Progenitor.Access.Access.Describe()`. Add `--no-facade` if you want only accessors (no `DeclaredCount` / `DeclarationsHash` / `InteropVerified` constants, no `Describe()`, no attribute rows); keep the facade on if you want your declarations in the resilience report too.

**Limits.** Generic instantiations are not declarable (`Dictionary<PrefabLookupKey, Il2CppSystem.Object>` has no C# spelling the emitter can write, and `MetadataScan` already counts generic members separately). Neither are Burst-compiled job data, obfuscated names, or private fields of Unity **engine** classes that the interop did not model — for those the answer is still the address library, not an accessor. See the case table in the research note.

## Layout constants

Roadmap M4 item 1.1. Declarations `src/Progenitor.Core/Layout/layout.txt`, generated
`src/Progenitor.Core/Layout/Generated/LayoutConstants.cs` (class `Progenitor.Layout`), tool
`tools/layoutgen`.

**The question it answers.** A byte offset, a native array stride or an enum value taken out of the
game's metadata is a *wrong-memory write* the day the game moves it. Before this module those
numbers were literals scattered through `src/` — `BlockElementStride = 8`, `InputIndexOffset = 0x40`,
`HealthIsDeadOffset = 24` — each with a metadata line citation in a comment, and a comment is not a
check. They are now declared once, emitted once, and resolved against the game's metadata by one command.

```
offset HealthIsDeadOffset       = 24   : Health.IsDead
size   BlackboardElementStride  = 1    : BlackboardElement
enum   AiTargettingModeEntity   = 3    : AiTargettingMode.Entity
match  FixedString128MaxUtf8Bytes = 125 : /utf8MaxLengthInBytes = (\d+);/ in FixedString128Bytes
```

Each line carries the value the code was written and verified against; the dump carries what the
game says today. The metadata files live outside the repository, one folder per side (`server`,
`client`) under `PROGENITOR_METADATA_DIR` (default `<workspace>\_tools\interop-dump`). Regenerate and check:

```
dotnet run --project tools/layoutgen --   --declarations src/Progenitor.Core/Layout/layout.txt   --out src/Progenitor.Core/Layout/Generated   --dump-server <server metadata file> --dump-client <client metadata file>   --game-assembly server=<testserver>\server\GameAssembly.dll   --game-assembly "client=C:\Program Files (x86)\Steam\steamapps\common\VRising\GameAssembly.dll"   --build <build id>

dotnet run --project tools/layoutgen --   --declarations src/Progenitor.Core/Layout/layout.txt   --out src/Progenitor.Core/Layout/Generated --check --dump-server <server metadata file>   --game-assembly server=<testserver>\server\GameAssembly.dll
```

**The two `--game-assembly` arguments on the generate line are not decoration** (job FLAY4). They
are the only thing that writes the header's `game:` line — `--check` never writes a file, so it can
never add one. **A generate merges: the sides it names are re-measured, and every side it does not
name keeps the identity the checked-in header records**, carried forward the way `build:` is (job
FLAY5), so regenerating on a machine with only one game tree no longer erases the other side's
record and no longer turns every two-tree machine's next deploy into a refusal. What a one-sided run
still cannot do is record a side *nothing* has recorded yet — it says so, naming the side and the
argument to add — so name both sides the first time and commit the result. **Which half is which is
written down** (job FLAY6): a `measured:` line under `game:` names the sides the run that last wrote
that line actually stat'd, so a `server=` entry carried forward through three game updates is
visible in the header and in the diff instead of reading as a fresh measurement. Both modes say the
same thing on the console, over the value that mode is actually using — a generate reports what it
**writes** (`game identity written: client=… (carried) server=… (measured)`) and closes with
`measured this run: …; carried from the header: …`. **A check says "compared" only about a side it
was handed a binary for** (job FLAY7): a named side is `(compared)` whether it matches or not, an
unnamed side is `(recorded, not compared: no --game-assembly for this side)`, a side whose binary
was named and still never checked (no dump loaded for it, or the file is not there) is
`(recorded, not compared: this side's binary was not checked)` — telling an operator who passed
`--game-assembly` that they passed none sends them to add an argument they already gave — and a run
given no `--game-assembly` at all opens
`game identity recorded (not compared: no --game-assembly given): …` with no per-side mark, because
there is nothing to distinguish. The check's summary counts the same
fact in the same words (`compared this run: …; recorded but not compared: …`), so the two lines
cannot disagree. That last shape is the ordinary one — the recipe above names no binary — and it
used to announce both identities as compared one line above saying nothing was measured.
Provenance never changes what is compared: a named side whose recorded identity was carried by
somebody else is compared against this machine's binary exactly as a freshly measured one is. The
client path is quoted
whole (`"client=…"`) because it contains spaces.

`--check` exits 2 on any of four things: the checked-in file drifted from the declarations, the
dump **disagrees** with a declared value, the dump can no longer **find** the type or field a
declaration names, or the dump is **not a dump of the game on this machine** (see "the dump is the
oracle" below). It never rewrites a value to match the dump: a field that moved needs the code
fixed and re-verified, and a generator that absorbed the new number would ship an untested offset
under a green build.

**Unresolved is a failure, not a gap in the report** (R53 finding 1). A game update does not only
move offsets — it renames `ProjectM.Health`, or drops `Health.IsDead` for a flags field — and then
the declarations that named them cannot be resolved rather than resolving wrong. That count used to
be taken and discarded: only a disagreement returned 2, so a vanished type printed one `?` line
among forty `ok` lines and the run exited 0 saying *"the dump agrees with every value"*, about
values it had not looked at, and the constants degraded one at a time into unchecked literals with
no signal at all. Unresolved now exits 2, writes nothing, and prints a line per declaration naming
what was not found and the `layout.txt` line it was declared on. The summary sentence is built from
the counts (`LayoutCheckTally.Verdict`), so no branch can claim an agreement it did not measure:
`the dump agrees with all 43 values` / `41 values agree, 2 values could not be resolved in the
dump` / `no dump given, values unconfirmed`. A declaration for a side whose dump this run was *not
given* is the separate, benign case — **skipped**, never a failure — because that is what lets the
game-free half run on a machine with no dump.

**`size` is derived, never stated.** The dump gives field offsets and no sizes, so a size is the
widest field extent rounded up to the widest field alignment, with field widths resolved
recursively out of the dump and only the C# primitives hard-coded. This is the CLAUDE.md rule
"Native array element strides" made mechanical: for a buffer or blob element the stride comes from
the dump, never from `sizeof` (which reads the *unpadded* extent — 73 for a 76-byte
`SequenceInputElement`) and never from `TypeManager`. The resolver refuses rather than guesses when
a width is unavailable, and refuses an open generic outright: `NativeArray<T>` is dumped with every
field at `// 0x0` because there is no laid-out instance, and "resolving" those zeros would report
the code's correct offsets as drift.

**A derived value may be declared when the tool derives it the same way** (R53 finding 7).
`SequenceInputElement`'s 76-byte stride was arithmetic in `SequenceBlackboardLayout` —
`RoundUp(IsInputParameterOffset + 1, 4)` — and so was the one stride in the tree that nothing
checked against the dump, which is exactly the number a live client regression already got wrong
(`stride is 73, not 76`, every beam `path=none`; EV17, R30 finding 22). It is now
`size SequenceInputElementStride = 76 : SequenceInputElement`. The usual objection to declaring a
derived value — that the declaration and the arithmetic can drift apart — does not apply here,
because `layoutgen`'s `size` rule derives it from **the same three offsets by the same round-up
rule**: the two cannot disagree without `--check` failing. `InputElementPackedBytes` stays as the
local cross-check that the declared number really is that arithmetic. Declaring a value derived
some *other* way would not be safe, and is not done.

**What stays a literal, and why.** Three kinds. Arithmetic over other declared constants
(`InputElementPackedBytes` is `InputIsInputParameterOffset + 1`) — declaring the result too would
let the two disagree, unless the tool derives it identically, as above. Progenitor's own numbers:
wire-format offsets
(`Handshake.MetaFlagOffset`), refusal bounds and caps, and encodings the game does not have
(`CastleLogic.WallNorth..WallWest` are our bitmask; the game's `CastleFloor.Wall*` are `Entity`
fields). And facts the game's metadata does not carry: a constant native out of a *method body*
(`CastleTerritoryBuffers.BlockTileSize`) or measured live (`CastleGeometry.TileOrigin`).
`LayoutConstantTests` sweeps every migrated file for a bare numeric `const` and fails on one that is
not on its allow-list, so a new literal has to be argued for rather than added quietly. **That sweep
strips comments before it reads a value** (R53 finding 6): its first version trimmed a trailing `;`
and then treated anything containing a space as "not a number", so `= 0x40; // from the metadata`
walked straight past it — and that is not a contrived mutation, it is the exact shape every one of
these constants had *before* it was migrated. It also reads `internal const byte`, `const long` and
`static readonly int` now, and takes the name from the token before the `=` rather than a fixed
position.

**Where the dump half actually runs.** The dump is 127 MB and lives outside the repo by decision, so
CI *cannot* run the resolve half — which is why, until R53, `--check` appeared in no workflow and no
script and ran only when a human typed it. **`tools/deploy.ps1` is the gate.** Every deploy runs
`layoutgen --check` against `PROGENITOR_METADATA_DIR` (default `<workspace>\_tools\interop-dump`; override with `-DumpDir`) and
`throw`s on exit 2 before a single file is copied, so a game update that moved or renamed a field
fails the deploy rather than shipping a wrong-memory write. It is skipped with a printed note where
no dump is present, and under `-WhatIf` (which builds nothing). `-NoBuild` does **not** skip it:
that switch means "do not rebuild the mod", not "do not check the game".

**The dump is the oracle, so the dump is checked too** (R54 finding 1, job FLAY3). Everything above
rests on one artifact that this repository does not own and nothing regenerates:
the type listing in `<metadata>\<side>`, exported by hand from the game's metadata. Its whole freshness policy
used to be `Test-Path`, and that is a gate with a hole in exactly the shape of the event it exists
for: Stunlock ships a patch, BepInEx regenerates the interop, `dotnet build` succeeds, and the check
resolves all 43 declarations against a dump of the **previous** build, prints *"the dump agrees with
all 43 values"*, and exits 0 — 43 raw memory offsets shipped having been compared with nothing.

How it is kept honest. A dump has no build stamp of its own (none of the exported metadata files
carries a version), so **the identity of a dump is the identity of
the `GameAssembly.dll` it was taken from**: last-write time to the second, UTC, and byte length —
`server=2026-08-25T21:04:30Z/248598344`. That binary is named per side with `--game-assembly
<side>=<path>`, and `--check` exits 2 when

- the dump was written **before** the binary it claims to describe (the game was updated after the
  dump was made),
- the named binary **is not there** (the path this run believes in is wrong, so nothing was
  established),
- the header **records no identity at all** for a side whose binary this run *was* handed — a
  header written without `--game-assembly` and a header that predates the line are
  indistinguishable from inside, so falling through there is how the identity rule silently
  de-arms: the older-than rule keeps passing, the run keeps printing agreement, and nothing says
  the strong half is switched off (job FLAY4, R55 finding 4) — or
- the binary is **not the one the generated header records** in its `game:` line.

**One remedy per kind of failure, not one per run** (`DumpFreshness.Remedies`). A dump older than
its binary, or a binary that is not the recorded one, wants a **re-dump** (`HowToRefresh`). A
missing record wants a **regenerate** with `--game-assembly` (`HowToRecord`) and nothing else: the
dump is current, it is the record that is absent, so printing the re-dump procedure there would
send the reader to re-export the game's metadata over a perfectly good dump. `HowToRecord` is deliberately
not per side — the fix is one generate run naming *both* sides, because a generate records only the
sides it was given.

A side whose dump was loaded and whose binary was *not named* is **reported** as unchecked rather
than passing quietly — a gate an omitted argument turns off is a gate nobody notices losing — and a
side with no dump at all stays the benign skip that lets the game-free half run in CI. The identity
rule binds under `--check` only: a regenerate is precisely how a new identity gets recorded after a
re-dump, so it records rather than refuses. The older-than rule binds in both modes.

`deploy.ps1` passes both sides' binaries from **its own roots** — `$GameDir\GameAssembly.dll` and
`$TestRoot\server\GameAssembly.dll`, both already past the live-server fence — and refuses up front
if a side has a dump, a tree, and no binary in it. Where a side's tree is absent entirely, that
side's dump is **not used** (an unusable dump is skipped, never trusted). The refusal names the
stale side and the re-dump procedure:
re-export the game's metadata from that side's `GameAssembly.dll` and
`global-metadata.dat` into `<metadata>\<side>`, then re-verify each declaration, regenerate,
re-run. Nothing is deployed.

Two notes for the next reader. The `game:` header line is ignored by the staleness diff exactly as
`build:` is (a game bump is not a declaration drift, and freshness has its own verdict and its own
message), and it is emitted only when something was measured, so a file generated before FLAY3
regenerates byte for byte. And the existing `build: 248598344` is not an unrelated number: it is the
server `GameAssembly.dll`'s byte length, which is why the identity line and it agree on this
machine.

The game-free half is covered inside `dotnet test`:
`LayoutConstantTests.The_checked_in_generated_file_matches_the_checked_in_declarations` regenerates
the artifact and asserts it is byte-identical,
`Every_declared_value_is_the_value_the_compiled_constant_carries` asserts the same over the compiled
`Layout` surface, and the exit-code rule itself (`LayoutCheckTally`) is driven over a hand-written
fake dump — a vanished type and a renamed field must both fail, and the summary wording must not say
"agrees" about a value it did not check. The freshness rule is covered the same way, over fabricated
files with fabricated timestamps (`DumpFreshness`): a dump written after the binary passes and
records its identity, one written before it fails naming both timestamps, a named-but-absent binary
fails while an unnamed one is reported, and the header carries the identity without making a game
update read as a hand-edited file. Run the dump-resolving command by hand after a game update
too, before trusting any raw write; the deploy gate is the one that catches you when you forget.

**Plugins.** `Layout` lives in Core, so a plugin can read `Progenitor.Layout.<Name>` directly, or run
`tools/layoutgen` against its own declarations with `--namespace MyMod --class MyLayout`. Note
`Progenitor.Bootstrap` cannot: it must not depend on Core, so the handful of layout numbers there
(`ReceiveSlotPolicy.NativeArray*Offset`, `UiInputContext.BackAction`) stay literals until Bootstrap
gets its own generated file.

## Update resilience

Roadmap M4 item 1. The design, prior art and what the check can and cannot detect are in the internal research notes. Namespace `Progenitor.Resilience`, sources in `src/Progenitor.Core/Resilience/`. Offline tool: `tools/resilience-report.ps1`.

**The question it answers.** On the first run after a V Rising patch, what moved and what of ours breaks — *before* a plugin loads and fails. `Plugin.Install` points `Resilience.LogSink` at the `Progenitor` log source and calls `Resilience.Install()`, which starts one below-normal background thread. It never blocks the load, never refuses one, never patches anything, and writes exactly one log block.

**Uninstall cancels, then joins.** `Plugin.Uninstall` drops the sink, bumps a generation counter, **cancels** the pass (`ResilienceOptions.Cancellation`, checked between phases, per interop file and per resolved row) and waits `Resilience.UninstallJoinMilliseconds` (250 ms) for the thread. The generation bump alone was not enough: a thread with stack frames in Core methods roots the collectible `LoaderAllocator`, and `CoreLoader` runs `PrepareUnload()` / `Unload()` immediately after `Plugin.Uninstall` — a first-run full-hash pass over `GameAssembly.dll` plus 169 interop DLLs is tens of seconds of that (R31 finding 12). **The collectible-ALC rule for a background thread is: cancellable, joined on Uninstall, and silent afterwards.** Every emit re-checks the generation while holding the same lock `Uninstall` takes before it joins, so no line can land in the next generation's log; a canceled pass also writes no store file, so it cannot become the next run's baseline.

**The hash is chunked, and the join is frame-sized.** "Cancellable" has to mean the *expensive* part, not just the phase boundaries. Cancellation was checked between stages and per interop file, but the work itself was one `SHA256.ComputeHash(stream)` over `GameAssembly.dll` — a call that observes no token and, on the first run after an update (the exact run the module exists for, where the length-and-mtime fast path cannot apply), is seconds long on a cold file cache. `Plugin.Uninstall` runs on the game **main thread**, so the old 2 s join could produce both bad outcomes at once: a two-second stall mid-tick *and* a timeout that left the collectible ALC pinned by the very thread the join was supposed to retire, surfacing later as `Core reload refused: … still alive; restart the process` (R32 finding 7). `ResilienceCheck.Sha256Stream` now reads in 1 MiB chunks (`HashChunkBytes`) through `IncrementalHash`, checking the token **before every read**, so cancellation is observed within one chunk; the join budget dropped to 250 ms to match.

**The residual, and why it is safe to leave.** Past the budget the pass is not joined again, killed or retried — it finishes on its own and `Uninstall` returns. What it holds while it does is worth stating exactly, because it is the whole argument for not waiting: a `ResilienceOptions`, a `FileStream` plus a `byte[]` over files on disk, an `IncrementalHash`, the `BuildFingerprint` / `AssemblyFingerprint` / `MemberBinding` / `AddressTable` value objects and a `List<string>` of notes. **No IL2CPP object, no `World`, no `Entity`, no Unity handle** — the module is game-free by construction (BepInEx logging arrives through `LogSink`), which is what makes a late-finishing thread a delay rather than a hazard. It can publish nothing either: the sink is already dropped and every emit re-checks the generation, and a canceled pass writes no store file. The only cost is stack frames in collectible Core delaying the ALC collect for the length of one chunk, which shows up in the ordinary collect-wait pin diagnostics and clears itself.

**The fingerprint** (`BuildFingerprint`, stored beside the address tables as `build-{side}-{build}.json`, i.e. in `BepInEx/progenitor-core/addresslib/`):

- the address-library key `{side}-{build}` — the same string `tools/addresslib/{side}-{build}.json` is named after, from `AddressId.ComputeBuildIdFromFile`;
- `GameAssembly.dll`: length and a **full** SHA-256. The build id hashes only the first 64 KiB, so a patch that leaves the PE header and the first 64 KiB alone would otherwise read as "unchanged"; the full hash is the line that catches it (`GameAssemblyChangedUnderSameBuildId`, reported as *"bytes changed under the SAME build id - every relative address is suspect"*);
- every DLL in `BepInEx/interop` (169 on the current build): file name, assembly version from the metadata Assembly row, length, SHA-256, and a last-write stamp used **only** as a fast path;
- whether an address table exists for the build now running and which of `AddressId.DetourSites` have an entry in it;
- the member-level binding list (below).
- the archive folder holding a byte copy of that interop set (`interopArchive`, below), or `""` when this build was never archived.

**Where the fingerprint goes, and why the block says so** (job FUPD5). The store folder is `BepInEx/progenitor-core/`, or its `addresslib/` subfolder when that exists — one place per build for the fingerprint, the address table, the drift map and the interop archives. `Resilience.DescribeEnvironment` used to derive it from `typeof(Resilience).Assembly.Location`, and **in the game that string is empty**: Bootstrap loads Core with `AssemblyLoadContext.LoadFromStream` (`CoreLoader.LoadAndInstallBytes`), and an assembly loaded from bytes has no file location. `Path.GetDirectoryName("")` is `""`, so the store folder, the address-table folder and the Core assembly path were all empty, and the pass printed a full block while doing none of its work: no fingerprint written, no member scan (`members not scanned`), and after FUPD4 no interop archive either (`not archived (no archive folder)`). Because the fingerprint is also the baseline, *every* Core generation reported `first run, no stored fingerprint` — a failure whose only symptom was a line that looks healthy on a fresh install. M4.1's "verified against the current install" had been the offline runner, which is told its folders on the command line and was never affected.

`Resilience.ResolveCoreFolder` takes the first folder that exists out of (1) the folder `Assembly.Location` names — the offline runner, the tests, any host that loads Core from a file — and (2) `<game folder>/BepInEx/progenitor-core`, derived from the main module the same way the interop folder already was. Nothing on `ICoreRuntime` / `ICoreHost` / `ProcessHost` hands Core the folder Bootstrap loaded it from, so the fallback mirrors `CoreLayout.FolderName` rather than asking for it (Core cannot reference Bootstrap, and `Resilience` is compiled into `Progenitor.Tests`, which may reference neither BepInEx nor the game); if that path is ever published to Core, prefer it over the fallback. When `Assembly.Location` is empty the member scan reads `progenitor-core/Progenitor.dll` — the image this process loaded, unless a deploy has staged a newer one since, and a staged build is `.next`, not this file. The block gained a **`store` row** naming the folder and how it was chosen, and an unresolvable folder is now one loud line (`store NOT RESOLVED …` plus a `WARNING: no store folder …` note) instead of a first run that never ends.

**The interop archive** (`InteropArchive`, job FUPD4). `tools/driftmap` — the generator behind the [compatibility rewriter](#compatibility-rewriter)'s drift map — takes `--old <the previous build's BepInEx/interop>` and `--new <the current one>`, both mandatory. BepInEx regenerates `BepInEx/interop` **in place** when the game updates, so until this existed, the `--old` set was destroyed by the very event that created the need for it, and the fingerprint could not stand in: name, version, length and SHA-256 say *that* an assembly changed and can never say *what moved inside it*. So the pass, behind the same new-build-id detection that writes `build-{side}-{build}.json`, copies the whole interop folder to `interop-{side}-{build}/` beside the address tables — the same folder the fingerprint goes in, `BepInEx/progenitor-core/addresslib/` when that subfolder exists, else the Core folder. The rules:

- **Once per build id.** An archive that already exists is never rebuilt, so this costs one copy per game update and nothing on an ordinary start.
- **Before the fingerprint, never after.** The fingerprint is what makes the next run say "same build, nothing to do", so a fingerprint written before a failed archive would mark the build as handled and the copy would never be retried — with the live folder already overwritten.
- **Off unless someone asked for it** (job FUPD6, R53 finding 9). A copy is ~180 MB and the default retention of 3 is ~540 MB **per game root**, written into a Steam-managed game folder that "verify integrity of game files" enumerates and a game update does not clean — and a player who uses the hosted-game launcher has a second root under `VRising_Server` with its own interop and its own retention. The only consumer is `tools/driftmap --old`, a developer tool that is not in the Thunderstore package. So `InteropArchivePolicy` decides, and the shipped default is **false**: on when `[Resilience] ArchiveInterop = true`, on when this is a **developer tree**, off otherwise. An explicitly written key wins in both directions, including inside a developer tree, and a value that does not parse is "nothing was said" rather than a `true` that costs half a gigabyte. Nothing here branches on client versus server — the test server defaults on because it is a developer tree, not because it is a server. The **dev-tree signal is the one `Reload.WatchCore` already established** and is reused rather than reinvented: `tools/deploy.ps1` and `tools/stage-test-server.ps1` write `[Reload] WatchCore = true` into our own client and test-server `progenitor.cfg` and refuse to write it anywhere else, so that key being true *is* the machine-readable "this install is one of ours". The decision is a note in the block (`Resilience: interop archive on: developer tree ([Reload] WatchCore = true)` / `… off: not a developer tree and [Resilience] ArchiveInterop is not set …`) and, when it is off, an `archive off - <reason>` row — because the day an operator needs an `--old` input is six weeks after the day they could still have chosen to keep one.
- **Both keys are written into the `.cfg`, and still read by hand** (job FUPD7, R54 finding 9). `[Resilience] ArchiveInterop` and `[Resilience] InteropArchives` are read straight off disk by `InteropArchive.TryReadFromConfig`, and have to be: the pass runs on a background thread in a file that compiles without BepInEx and without the game. But nothing had ever `Bind`ed either key, and BepInEx writes a key and its description into `BepInEx/config/progenitor.cfg` only when something binds it — so both `ConfigDescription` constants were prose no human path reached, and an operator opening that file found no mention of the archive at all, on exactly the install where it now ships off. `InteropArchiveConfig.Bind` fixes that the way `[Compat] RewriteMode` already does: `Resilience.Install` binds both keys on the **main thread**, through `ConfigHelper` on the shared `ConfigFile` (BCL types only, dropped by `ConfigHelper.UninstallAll` on Core Uninstall), purely so they and their descriptions exist in the file. Nothing reads the entries; the pass keeps reading the file by hand, defaults the same way when a key is absent, and behaves identically whether the bind ran first, lost the race, or was impossible in this host (the offline runner, which has no chainloader). **The bound default is the string `auto`, not `false`** — the one trap in binding this key at all. `InteropArchivePolicy.Decide` treats a *written* key as an operator decision that beats the developer-tree signal in both directions, so a `bool` bind writing `ArchiveInterop = false` as its default would have turned the archive off in every developer tree the moment the file was first written: documenting the switch would have disabled it. `auto` does not parse as a boolean, and an unparseable value is already "nothing was said", so writing it decides nothing. `true` and `false` are the two values that do.
- **Atomic.** Files are copied into a dot-prefixed temporary folder and the whole set is published by one rename. A crash, a canceled pass or a full disk can leave a discarded temp folder — swept by a **later** pass, once the owning process is gone (or its start time proves it never was the owner) or the folder is past `MaximumTemporaryAge`; a canceled pass or a disk-full abort inside a **still-running** process is deliberately left alone until that process exits, and the report tallies it so the bytes are not invisible — but never a half-populated archive under the real name. A truncated archive is worse than no archive: the drift map computed from it would be silently wrong.
- **The temp folder names its owner, and the sweep respects it** (job FUPD6, R53 finding 5). The copy goes to `.interop-{side}-{build}.tmp-p{pid}-{yyyyMMddHHmmss}-{8 hex}`, and the sweep at the head of every pass deletes **only** a temp folder whose owner is gone. A folder with no owner stamp (a pre-FUPD6 name, or something else's) is judged by its own last-write time. The old sweep deleted every temp folder unconditionally, and this matters because the documented two-instance workflows — the test server plus a hosted child, or `tools/extra-client.ps1` beside the Steam client — run the *same* executable out of the *same* folder and therefore share one archive root, one side and one build id. The copy loop also aborts if its own temp folder disappears mid-copy instead of recreating the tree file by file, which is how the loser of that race used to publish a set with a hole in it.
- **A pid is not an owner; the stamp beside it is what decides** (job FUPD7, R54 finding 6). The sweep used to ask only whether the pid was alive, so any unrelated process that inherited the number read as the owner. That is the ordinary case in this repository's own loop: `test-server.ps1 stop` kills `VRisingServer.exe` mid-archive, Windows hands the pid to a `dotnet.exe` or a Grok job within minutes, and the sweep printed *"left `.interop-server-…` alone: pid 12345 is running and may be writing it"* over 180 MB nothing would ever reclaim. The discriminator was already free in the folder name: the owner necessarily started **before** it wrote its own name, so a live process whose `Process.StartTime` is later than the stamp (by more than `InteropArchive.ProcessStartGrace`, five seconds, which absorbs the stamp's one-second truncation) is a recycled pid and the copy is stranded. A start time the OS will not give — denied, or a process that exited between the two probes — is not evidence of anything and reads as the owner, so the sweep still fails closed, as does a liveness probe that throws. Both probes are test seams and travel together: a request that fakes the pid and not the start time gets "unknown" rather than a lookup of a made-up pid in the live process table.
- **The age cap is matched to the copy, not to the calendar** (job FUPD7). `InteropArchiveRequest.MaximumTemporaryAge` is **30 minutes** (it was six hours). The measured copy is 172 files and 179.8 MB and takes seconds (research evidence 193), so six hours guarded nothing about this operation; 30 minutes is still two orders of magnitude over it — enough for a cold disk, an antivirus scanning every file, or a network archive root — and costs half an hour of disk rather than most of a day. Tightening it is safe in the direction that matters: if a genuinely live copy ever did run past the cap, deleting its folder publishes nothing, because the copy loop re-checks `Directory.Exists` before every file and fails rather than rebuilding the tree around the hole. With the start-time rule above, what is left for the cap is only an unstamped folder and an owner whose start time is unknown.
- **What the sweep could not reclaim is counted and printed** (job FUPD7). Retention's glob is `interop-{side}-*` and can never match a dot-prefixed `.interop-*`, so a surviving temp sat **outside** the "~540 MB per game root" ceiling this page advertises, with nothing naming it and nothing ever reclaiming it. `InteropArchiveResult.TemporaryFolders` / `TemporaryBytes` carry the tally, stamped at one exit point so every status has it, and the archive row ends with `; plus 1 unfinished copy 179.8 MB not yet reclaimed` when there is anything to say and stays silent when there is not.
- **Published only after it is counted, and it says what it holds.** Before the rename the copy's file count and total bytes are compared against the source; a mismatch discards it rather than publishing it. Then `progenitor-archive-manifest.txt` is written **into** the temp folder, so it is published atomically with the set it describes. Its format is a header (`progenitor-interop-archive 1`, then `side=`, `build=`, `createdUtc=`, `pid=`, `files=`, `bytes=`), a `--` line, and one line per file: `<sha256> <length> <relative path>` (paths `/`-separated; the length is the second token, so a path with a space in it survives). The manifest is not counted as one of the archived files, so "172 files" means the same number on the pass that wrote the archive and on every pass that reads it back.
- **An archive that cannot prove it is whole is `Incomplete`, and is never offered as `--old`.** Every `AlreadyPresent` pass re-reads the manifest and checks that every listed file exists with the listed length and that the folder holds nothing extra — lengths, not hashes, because re-hashing 180 MB on every start is exactly the cost the fast path exists to avoid, and the hashes are on disk for whoever wants to prove it later. A failure is a status and a row (`… is on disk but INCOMPLETE: missing Unity.Entities.dll — it will not be offered to driftmap as --old`), `InteropArchiveEntry.Complete` is false, and `FormatDriftMapCommand` skips it and takes the next good one, or says there is nothing safe to diff against. One exception, because these sets cannot be recreated: an archive with **no** manifest at all whose file count and total bytes still match the live interop folder is described rather than condemned — the manifest is written then, once, and the archive stays usable.
- **Bounded.** The newest `[Resilience] InteropArchives` archives per side survive (default 3, clamped 1..10, read by hand out of `BepInEx/config/progenitor.cfg` because the pass runs on a background thread in a game-free file). The archive for the build now running is never the one retention deletes, whatever its timestamp says. Each archive is a full copy of the interop set, so three of them is ~540 MB per game root — a ceiling that only holds because an unfinished copy the sweep could not reclaim is now counted and named on the same row (above) rather than sitting outside it.
- **Refused rather than ruinous.** A copy that would not leave 512 MB free is refused with the numbers in the log; unknown free space (a network path that will not answer) is not a refusal.
- **Never in the way.** Same background pass, same cancellation, never on the load thread, and every failure is a report row rather than an exception.

The block's `archive` row says which of those happened, and `pwsh tools/resilience-report.ps1` prints the archives on disk plus the exact `driftmap` command line for the build installed now — the one command an operator runs after a game update.

**Comparison** (`ResilienceDiff.Compare`, pure: two objects in, a diff out — no file system, no clock). The baseline is `build-{side}-{build}.json` for the build now running when it exists (so an interop regeneration under an unchanged `GameAssembly.dll` is still a diff), otherwise the newest stored fingerprint for the same side. `ResilienceCheck.Run` reads the build id first (a 64 KiB read) precisely so `LoadBaseline` can prefer that file; taking the newest unconditionally meant an A / B / A branch switch compared A against B and reported *"build CHANGED"* on every single run, never reaching the same-build comparison (R31 finding 19). It reports added / removed / changed interop assemblies, an address table missing for the new build, detour sites that had a table entry and no longer do, and the member rows that stopped binding. `HasBreakage` means something of ours does not resolve **right now** — no address table, a detour site with no entry in the table that is present, a reference that does not bind, or a member scan that checked nothing — and it is the list the offline tool turns into exit `1`. `Unchanged` is defined as *a baseline, plus `!GameChanged`, plus `!HasBreakage`*, so **`Unchanged` implies `!HasBreakage` by construction** and a term added to one list can never be forgotten in the other (job FUPD9, R56 finding 4: the two were separately hand-written lists that disagreed about `DetourSitesWithoutEntry`, so a site missing from a present table produced the one-line "game build unchanged" block *and* exit `1` over it, with no row naming the site). A test enumerates all 2^15 combinations of the diff's terms; what it asserts is not that implication — since `Unchanged` literally reads `!HasBreakage`, that cannot fail — but that the two lists it sweeps are correctly sorted: it predicts `Unchanged` from the mask alone (a baseline, and no *verdict* term on) and so fails if a term stops reaching a verdict or if one of the three stored facts that feed none (`MembersNew`, `MembersDropped`, `CoreChanged`) is quietly wired into one. The surviving-combination count is derived from that non-verdict list rather than written as a literal (job FUPD10, R57 finding 8). A second test pins both lists against the type by reflection.

**Member-level check** (`MetadataScan`, `MemberBinding`). `System.Reflection.Metadata` over files — no game process, no `Assembly.Load`, no Il2CppInterop. Step 1 walks `Progenitor.dll`'s TypeRef and MemberRef tables and keeps every row whose resolution scope is a game AssemblyRef (`ProjectM*` / `Unity*` / `Stunlock*`); that is exactly the set of game types and members Core's IL mentions (a few hundred rows; the live figure is the block's `members` row, never a number in this file — see "The member count is a measurement" below). Step 2 indexes the matching interop assemblies' TypeDef / MethodDef / FieldDef tables and asks per reference: is the type there (`TypeMissing`), is the name there (`MemberMissing`), does any overload have the signature Core references (`SignatureChanged`, and the report prints what the build now offers) — otherwise `Bound`. This is the "will Core compile-and-bind against this build" answer.

  The startup fast path is a **cache, not a verdict**. The build id already covers the length, so "id unchanged" would imply "length unchanged" and reusing the stored hash on that alone made `GameAssemblyChangedUnderSameBuildId` structurally unreachable in the game — the one place the fast path is on (R31 finding 5). Reuse now also needs `GameAssembly.dll`'s stored mtime (`GameAssemblyModifiedUtc`) to match, and the full hash is recomputed on a schedule regardless: `ResilienceOptions.FullHashInterval` (24 h) against the stored `GameAssemblyHashedUtc`, so a patch that forged the mtime too is still caught on the next day's start. Both fields are new in the stored file; a fingerprint written without them is always "due".


A member reached through a **generic instantiation** (a TypeSpec MemberRef parent — `NativeArray<T>`, `DynamicBuffer<T>`, `ComponentDataFromEntity<T>`, `Il2CppSystem.Collections.Generic.List<T>`, i.e. most of Core's game surface) is resolved against the **generic definition**: ECMA-335 writes such a MemberRef's signature in terms of the declaring type's own parameters (`!0`), which is exactly what the TypeDef side renders, so the two strings compare. These rows used to be dropped with no row and no count, so the whole Entities/Collections surface went unscanned while the block still said *"all bind"* (R31 finding 7). A TypeSpec rooted in an array, a pointer, a generic parameter or a TypeDef of the scanned assembly is out of scope by the same rule that drops non-game TypeRefs; only a blob that does not decode stays `NotChecked` (assembly `(unresolved)`), and `Resolve` preserves a reason the scanner already wrote instead of relabeling it.

**"All bind" is a claim about what was checked.** With some rows unchecked the line reads `N checked, all bind, M not checked`; with **zero** checked — a renamed `ProjectM.dll`, a regenerated interop layout — it is its own line, `N references, NONE checked - the interop assemblies could not be read`, plus the reason. `ResilienceDiff.NothingChecked` makes `Unchanged` false and `HasBreakage` true for that case, so the tool exits 1 rather than reporting a clean bill of health for exactly the failure the check exists to catch (R31 finding 11).

**The log block.** One `Resilience:` headline plus indented `label value` rows, capped at `ResilienceReport.MaxDetailLines` per section so one update cannot flood the log, and closed by a line saying nothing was blocked. Unchanged:

```
Resilience: game build unchanged
  build       client-247473480-6a147771-ec12531-66fe4b61f7a0d186
  baseline    build-client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json (captured 2026-09-04T05:31:44Z)
  store       C:\...\BepInEx\progenitor-core\addresslib  (game folder + BepInEx/progenitor-core; addresslib subfolder)
  interop     169 assemblies, unchanged
  archive     interop-client-247473480-6a147771-ec12531-66fe4b61f7a0d186 already archived (keeping 3)
  addresslib  table client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json present, 3/3 detour sites have an entry
  members     890 references, all bind
```

Changed:

```
Resilience: game build CHANGED
  build       client-248100992-6a1b2c33-ff01234-aabbccdd11223344  (was client-247473480-6a147771-ec12531-66fe4b61f7a0d186)
  baseline    build-client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json (captured 2026-08-20T10:00:00Z)
  store       C:\...\BepInEx\progenitor-core\addresslib  (game folder + BepInEx/progenitor-core; addresslib subfolder)
  interop     169 assemblies: 1 added, 1 removed, 2 changed
              added    ProjectM.Terrain.dll 0.0.0.0
              removed  ProjectM.Legacy.dll 1.0.0.0
              changed  ProjectM.dll  size 12,865,024 -> 12,873,216, sha deadbeef9656 -> 6d5979c29656
              changed  Unity.Entities.dll  sha cafebabe23c0 -> 925801cd23c0
  archive     interop-client-248100992-6a1b2c33-ff01234-aabbccdd11223344 created, 169 files, 201.4 MB (keeping 3, removed interop-client-246900000-5f0a1b22-dd98765-1122334455667788)
  addresslib  NO TABLE for this build (client-248100992-6a1b2c33-ff01234-aabbccdd11223344.json) - 3 detour site(s) will not resolve from the table
              no entry  keyprobe.findentry
              no entry  receive.client
              no entry  receive.server
              rebuild   node tools/addresslib/build.mjs --side client
  members     696 references, 3 do not bind, 2 fixable by the drift map, 1 need the plugin author
  rewrite     driftmap-248100992-6a1b2c33-ff01234-aabbccdd11223344.json sha256 0d94a696993ae205 (12 generated, 1 hand)
              member gone ProjectM!ProjectM.ClientBootstrapSystem.OnReliableEventsReceived System.Void(System.Int32)   [fixable by rewrite]
              type gone   ProjectM!ProjectM.PrefabCollectionSystem.get_PrefabLookupMap ProjectM.PrefabLookupMap()   [fixable by rewrite]
              signature   ProjectM!ProjectM.ServerBootstrapSystem.OnUpdate System.Void()   now: System.Void(System.Single)
  note        report only - nothing was blocked, plugins load as usual
```

**The headline is about the game, and only about the game** (job FUPD8, R55 finding 5). `ResilienceDiff.GameChanged` is the build id, the `GameAssembly.dll` bytes under an unchanged id, the interop set, a missing address table, an interop folder that could not be read at all, a detour site that lost its table entry, or a reference that used to bind and no longer does — and nothing else. **Core's own reference set moving is not the game moving.** A Core deploy that starts referencing five new game types is a detail on the `members` row and never a verdict:

```
Resilience: game build unchanged
  …
  members     895 references (+5 since baseline), all bind
  core        Progenitor itself changed since the baseline (<Core version>)
```

The `core` row is what says *why* the count moved, which is why it is printed next to it. Before FUPD8 that growth vetoed `Unchanged` from inside `ResilienceDiff` and was rendered nowhere, so the test server printed `game build CHANGED` over a block whose every row said unchanged (observed live 2026-09-05, 890 → 895 references); the offline tool exits `0` for that run now instead of `2`. A dropped reference reads the same way (`-N since baseline`).

**The `core` row fires on the reference set moving, not on the version moving, and it names the Core** (jobs FUPD9 and FUPD10, R56 finding 9 and R57 finding 7). FUPD8 keyed it on `CoreChanged`, an assembly-version comparison — and a developer-tree deploy does not bump the version, because version bumps are a separate commit by project rule. So the row was false on exactly the deploys that move the count: the test server printed `897 references (+2 since baseline)` with nothing saying under which Core. FUPD9 fired the row anyway and printed the version — which on that branch is by construction the version the baseline already carried, so the row announced the move and still identified no Core. The fingerprint therefore records **Core's MVID** beside its version (`coreMvid`, the GUID the compiler stamps into every compilation), `CoreChanged` is true when either moved, and the row prints both ends: `core  Progenitor 0.1.0.0/a1b2c3d4 -> 0.1.0.0/0f1e2d3c`. **Both halves of that identity come from one place** (job FUPD11, R58 finding 9): the loaded Core assembly, `typeof(Resilience).Assembly` — `GetName().Version` and `ManifestModule.ModuleVersionId`, read side by side in `DescribeEnvironment` and carried on the options. FUPD10 read the MVID off `progenitor-core/Progenitor.dll` instead, which is a *different assembly* from the running one whenever a deploy has staged over it, so `0.1.0.0/a1b2c3d4` could name two Cores; and it came back empty on the one run it exists for, the run whose core folder could not be resolved and which therefore has no path to read. The manifest module needs no path, no file handle and no `try`. What remains for the case where one side recorded no MVID (a baseline from before the field) is `Core references moved under the same 0.1.0.0 (+2/-1)`, which says the count rather than pretending to answer *which*. An absent MVID on either side is “cannot say”, never “changed”. A steady reference set under a steady identity prints no row.

**And there are two more headlines**, for breakage *without* the game having moved. Core references something this build does not have:

```
Resilience: references DO NOT bind (game build unchanged)
```

or the address table for this build is present and a detour site has no entry in it (job FUPD9):

```
Resilience: detour sites HAVE NO ENTRY (game build unchanged)
```

Both drop out of the one-line shape and get the **full block**: the interop, archive, addresslib and members sections with their detail rows — including the `no entry <site>` rows and the rebuild command — and the closing `note`. Neither is a *prefix* of either build headline, so a grep or a `StartsWith` assertion anchored on `Resilience: game build unchanged` cannot match them.

**The `members` line joins the two halves of M4** (job FCOMPAT3). The scan says what broke; the [compatibility rewriter](#compatibility-rewriter)'s drift map says what of it can be absorbed, and the block says which is which: `N do not bind, M fixable by the drift map, K need the plugin author`, with the map named and hashed on its own `rewrite` row and every covered row marked `[fixable by rewrite]`. The join is string equality over one identity scheme — `MemberBinding.Key` and `DriftRule.From` are literally the same `Assembly|Type|Name|Signature` encoding — plus a fallback to the declaring type's key, because a `typeMove`/`typeRename` carries every member of that type with it. Only `exact`/`high` rules count: a `heuristic` or `addParam` rule is reported and never applied, so counting it as a fix would be a claim the report cannot cash. `ResilienceDiff.JoinDriftMap` is pure (a diff and a map, no file system, no game), and `ResilienceDiff.DriftMapProvider` is the one seam that reaches the running install — null in the tests and in the offline tool, where the block is exactly what it was before.

**The member count is a measurement, not a property of this document.** It is per side, per game build **and per Core**: it moves whenever Core's IL references a game member it did not reference before, which is every deploy that touches a new game type — which is why the headline is about the game and the growth is a `+N since baseline` detail on the `members` row (above). **Read the live figure from the `members` row of the latest resilience block in the log, or from `tools/resilience-report.ps1` offline; never from this file.** Expect the shape (≈900 references, all bind). The measured sequence on this machine, as history rather than as a current value: **696** by the offline runner on 2026-09-04 (not a live process at all, and taken *before* generic-instantiation members were scanned); **890** live at Core generation 18 (evidence 193), unchanged at generation 20 (evidence 194); **895** at generation 21 (evidence 197); **897** at generations 23 to 29 (evidence 197 to 202). Every fenced block on this page that quotes a `members` row is a transcript of the generation it was captured at and is history on the same terms.

The unchanged block above is a real run against the current install (`client-247473480-…`, 169 interop assemblies, table present, 3/3 sites), quoted with the `890` its capture printed — read the block as the report's *shape*. The changed block is the same formatter driven by a fabricated post-update fingerprint — no V Rising update has shipped since this landed.

**Offline.** `pwsh tools/resilience-report.ps1` runs the same checks against a game folder without launching it. It generates a small console project outside the repo that compiles `src/Progenitor.Core/Resilience/*.cs` and the four game-free `Native` files directly, so the tool and the game share one implementation rather than a second copy of the rules; the first run pays a `dotnet build`, later runs reuse it. Read-only against the game folder except for the stored fingerprint (`-NoWrite` skips even that). Exit codes: `0` unchanged, `2` changed but nothing of ours breaks, `1` breakage, `3` the tool itself failed — a bad `-GameDir`, a runner build failure, an unknown argument. `3` really is delivered: `Fail` writes with `$host.UI.WriteErrorLine`, because under `$ErrorActionPreference = 'Stop'` a `Write-Error` unwinds past the `exit 3` and the host exits **1**, so CI read a mistyped path as a breakage alarm (R31 finding 25). Note that `Unchanged` needs a baseline, so the **first** run on a healthy install exits `2`, not `0`. The tool prefixes its notes `Resilience:` like the game does, so one grep finds both. `-Side server`, `-NoMembers`, `-GameDir`, `-StoreDir`, `-TablesDir`, `-Rebuild`. Two rows the game's own block does not print close the loop for an operator: `archives`, listing the interop sets on disk newest first and marking the one for the build installed now, and `driftmap`, the exact command line that diffs the previous archive against the live interop folder. `-NoArchive` skips the copy; `-NoWrite` skips it too (a read-only inspection of somebody else's install must not write 200 MB into it); `-ArchiveKeep` overrides the retention for this run.

**What it cannot detect.** Behavior: a method that kept its shape and changed its meaning reads as `Bound`. Native drift inside a function body: it says whether a table exists for the build, not whether a relative address in it is still the right function — `node tools/addresslib/build.mjs --side {side} --verify` against freshly exported metadata is that check. Plugins: it scans Core's references, not each plugin's (that is M4.2, the compatibility rewriter). Generic instantiations, as above.

## Compatibility rewriter

`Progenitor.Compat` (`src/Progenitor.Core/Compat/`, mirrored into Bootstrap). M4 item 2, jobs FCOMPAT2, FCOMPAT3 and FCOMPAT4. The design is in the internal research notes. The report half is [Update resilience](#update-resilience) above — that says *what broke*; this says *what of it can be absorbed*, and the two are joined in the resilience block's `members` line.

**What it is for, and what it is not.** V Rising is IL2CPP, so a plugin never references the game: it references the **interop assemblies** BepInEx's Il2CppInterop regenerates from the live `GameAssembly.dll` on the first boot after a patch. That regeneration already absorbs every **layout** change — a class field offset is resolved at runtime by the stub, and a struct field's `[FieldOffset]` is baked into the regenerated interop assembly, not into the plugin. So **the rewriter exists for renames, moves and shape changes, and never for layout**. It retargets metadata references and **never edits an operand that is a number**: a stride a plugin author copied out of the game's metadata is an `ldc.i4` with no metadata token, no rewriter can find it, and that stays the plugin author's problem after a patch (the same boundary `Ecs/RawWriteGuard` and the metadata-stride rule draw).

**Pipeline position.** One hunk in each reloader — `PluginReloader.Load` in Core and the mirrored `ReloaderHost.Load` in Bootstrap — **after** the allowlist gate and **before** `LoadFromStream`. Three properties of that placement are deliberate and `CompatRewriterTests` pins the ordering in both files:

- **The allowlist decision is taken on the original bytes and is never re-taken.** The reloader already carries `(bytes, hash, allow)` from `CheckReplacement` into `Load` to close a TOCTOU; the rewriter rides that same carried decision. The rewritten image is **never** hashed for a gate, never written to disk, and never offered to the mod manifest (which keeps signing and serving the original file — a rewritten image is per-machine and per-build).
- **After the gate**, so a refused DLL never reaches a metadata parser.
- **Inside the existing try/catch**, so a rewriter failure degrades to "load the original", never to a refused plugin.

**Plugin-local dependency DLLs (FCOMPAT3).** A mod is often more than one file, and a helper DLL that names a moved type is as broken as the plugin is — fixing only the plugin moves the failure one frame further in. The pass therefore walks the plugin's `AssemblyRef` table with Cecil and rewrites what it finds **leaf-first**, before the plugin itself. The walk is narrow on purpose:

- **only files sitting directly in the plugin's own folder**, resolved to a full path and checked against that folder — a reference name is attacker-controlled metadata and must not walk out of the folder;
- **never a BepInEx-owned folder at all**: a plugin in `core`, `interop`, `unhollowed`, `plugins`, `patchers`, `config` or `progenitor-core` gets no walk (`CompatRewriteHost.IsRefusedDependencyFolder`);
- **never Progenitor, the loader stack or the game surface** as a dependency name — `Progenitor`, `Progenitor.Bootstrap`, `0Harmony`, `Mono.Cecil`, `System.*`, `BepInEx.*`, `Il2Cpp*` and anything `DriftMap.IsGameAssemblyName` claims are all refused;
- **bounded depth** (`CompatRewriteHost.MaxDependencyDepth`, 4);
- **the same gate order as the plugin**: each file is read, hashed, and put to the operator's allowlist **on its original bytes** before Cecil sees it. A refused dependency is left entirely alone and loads from disk exactly as it does today.

Every file gets its own log line with its hash and its allowlist verdict, plus its own `Rewrite:` block when a rule matched. Rewritten dependencies are handed back on a `RewriteBundle` and the reloader's hunk loads them, leaf-first, into the **plugin's own collectible ALC before the plugin** — a name already loaded in an ALC wins over the file on disk, which is how the rewritten image becomes the one the plugin binds, and it means the dependency dies with the plugin instead of outliving it in the default context. Nothing is written to disk in any mode, and the list is empty in every mode but `Apply`.

**A dependency is swept with its plugin (FCOMPAT4, R34 finding 4).** Moving a helper DLL into the collectible context is only a benefit if the unload sweep follows it there. Several sweeps are keyed on an **assembly** rather than on an owner object — `EventBus.RemoveDeclaredIn`, `MainThread.DropOwnedBy`, `HarmonySlotHandoff.ClearPluginContext` — and they used to take `plugin.GetType().Assembly` and only that, so a handler declared in `Shared.dll` survived the unload, pinned the `LoaderAllocator`, kept the ALC alive (which `CoreLoader`'s `PluginAlcsAlive` refusal then reads as "do not swap Core"), and could still fire into unloading code. The owner id is now the plugin's **collectible load context**: the reloader creates exactly one per plugin file and the rewriter's dependencies are loaded into it, so "in this plugin's ALC" *is* "tagged with this plugin". `PluginOwnedAssemblies.Of` derives the set from the context — which also catches a dependency the context's `Resolving` handler loaded, as no registry written at rewrite time would — and every assembly-keyed sweep runs once per assembly in it. The default (non-collectible) context is never treated as owned: it holds every assembly in the process, and sweeping "declared in" across it would drop Core's own registrations. The `RemoveAll(owner)` sweeps were already correct: they key on the owner object a registration named, whichever assembly declared it.

**The drift map.** A per-build data file, `driftmap-{build}.json`, next to the address tables in `BepInEx/progenitor-core/` and named after the same build id (`AddressId.ComputeBuildIdFromFile`). It loads by `AddressLibrary`'s rule with no relaxations: that exact path only — **no** `PROGENITOR_*` search path, **no** `BepInEx/plugins`, **no** game-root walk — and after parse the file's own `side`/`build` must equal the running process, so a file whose *name* matches but whose fields do not is refused.

One rule per drifted member, keyed on the same `Assembly|Type|Name|Signature` string `MemberBinding.Key` produces, so a rule and a resilience row join on equality with no second identity scheme. **The declaring-type half is rendered one way by one function** (`DriftRule.OpenTypeText`, applied inside `DriftRule.Key`): a generic instantiation is reduced to the open type it instantiates, so ``Unity.Collections.NativeArray`1<ProjectM.Entity>`` and ``Unity.Collections.NativeArray`1`` are the same key. That reduction is what makes the join honest (FCOMPAT4, R34 finding 10) — the scan reduces a `TypeSpec` MemberRef parent to its `TypeRef` while Cecil hands the rewriter the instantiated form, and while the two disagreed the resilience block printed `[fixable by rewrite]` beside rows the rewriter could never find, pointing the operator at a remedy that does nothing. **Signatures are not reduced**: both readers render those instantiated, and that is the identity a rule needs to tell two overloads apart.

| Field | Meaning |
|---|---|
| `from` / `to` | `MemberBinding.Key` of the old and new member (a type rule leaves name and signature empty) |
| `kind` | `typeRename` \| `typeMove` \| `memberRename` \| `fieldToProperty` \| `propertyToField` \| `addParam` |
| `confidence` | `exact` \| `high` \| `heuristic` — only **exact** and **high** are ever applied |
| `source` | `generated` \| `hand` — hand rules merge last and always win a key collision |
| `note` | free text, printed in the report |

Three refusals worth knowing. An `ldflda` has no property equivalent, so a `fieldToProperty` skips it and says so rather than inventing a temporary. **A rename may not change the signature, for a field as well as a method** — a field's key signature *is* its field type, so `T|X|System.Int32 -> T|Y|System.Single` is a rename *and* a retype, and renaming the operand alone would emit a `MemberRef` for the new name with the old type and throw `MissingFieldException` at JIT time while the report claimed a fix (R33 finding 8). And a `ctx.Patches` registration pairs an `ldtoken` (rewritable) with an `ldstr` method name (not), so the rewriter detects that pairing and **freezes the whole `TypeRef` row**: Cecil hands out one `TypeReference` object per row, so recording a skip and then letting `ApplyTypeRules` mutate the same object rewrote the `ldtoken` anyway and the report claimed both things at once (R33 finding 4). A per-use exemption is impossible, so the rule is refused for the entire module — the safe direction, and the one the report already described.

**The map is gated like code (FCOMPAT3, R33 finding 1).** A map that says "every call to `A.B` now goes to `C.D`" is an arbitrary-call-redirection primitive, and `BepInEx/progenitor-core` is the same trust level as the folder the threat model already treats as attacker-writable. So the map takes a row in the **same allowlist** as every plugin and every Core image:

- the file's bytes are hashed once, when they are read, and that hash is the identity — the same bytes that were parsed, not a second read;
- the hash is on the map line **every load**: `driftmap-<id>.json sha256 0d94a696993ae205 (12 generated, 1 hand)`, and that line is on the resilience block's `rewrite` row too;
- the map needs an `ok` row, and **only** an `ok` row. Anything else — the map is unlisted, the row is malformed, the gate is unwired, or **there is no allowlist file at all** — means the map **may not drive `Apply`**. It is still parsed, still analyzed and still reported; the mode ceiling drops to `DryRun` and the original bytes load. `CompatRewriteHost.Ceiling` holds that decision, taken once when the map is read, and the refusal is one line:

```
Rewrite: refusing Apply: driftmap-248100992-6a1b2c33-ff01234-aabbccdd11223344.json sha256 0d94a696993ae205 is not on the allowlist (unlisted); mode falls back to DryRun and the original bytes load
```

- **an unwired gate is not a yes.** The allowlist reaches the rewriter as a delegate (`CompatRewriteHost.AllowTokenReader`, wired by each reloader; the mirrored file cannot name `AllowDecision`, whose namespace differs on each side). Null means the map could not be vetted, and the ceiling is `DryRun` — fail closed.
- **and neither is "there is no allowlist" (FCOMPAT4, R34 finding 2).** For a plugin DLL, a missing `progenitor-allow.txt` means the operator declined to gate anything, which is their call. The map is the one input for which it may not mean yes: the map is not a file the operator chose to run, it is a file that reaches inside the ones they did choose and redirects their calls, and "no list" is the dev-machine default and the default for every operator who never wrote one. So a drift map needs a row even when nothing else does, and the refusal names the file to write:

```
Rewrite: refusing Apply: driftmap-248100992-6a1b2c33-ff01234-aabbccdd11223344.json sha256 0d94a696993ae205 cannot be vetted because there is no BepInEx/config/progenitor-allow.txt; a drift map needs a row in it even when nothing else does. Mode falls back to DryRun and the original bytes load
```

**Who may write it.** Anyone who can write `BepInEx/progenitor-core`, which is the operator and any local process running as them. That is unchanged and unfixable from inside the process; what changed is that writing the file is no longer sufficient. Under `Apply`, an unlisted map buys a `DryRun` report instead of a redirected call — and since FCOMPAT4 so does a map on a machine with **no allowlist file at all**, which is the case that used to fail open. `Apply` therefore does nothing at all until the operator has written `progenitor-allow.txt` and put the map's hash in it, beside the plugin hashes.

- `DriftMap.IsGameAssemblyName` decides what may be **written** in a map. It matches a name **segment**, not a prefix: `ProjectM`, `ProjectM.Shared`, `Unity.Entities`, `UnityEngine.CoreModule` and `Stunlock.Core` are the game; `ProjectMBackdoor`, `UnityHax` and `Stunlocked` are not. A rule naming a non-game assembly on either side is dropped at parse time with a reason.
- `PluginRewriter.ScopeFor` decides what may actually be **pointed at**. A `typeMove`'s destination must be an assembly the image already references **or** one present in this install's `BepInEx/interop` folder. It will not mint an `AssemblyRef` for anything else: a move to an assembly the plugin never referenced and the install does not have is not drift, it is a new dependency, and refusing is always safe.

**Applying one.** `PluginRewriter.Rewrite(fileName, bytes, map, mode, out report, interopAssemblies)` — `byte[] in, byte[] out`, no file system, no clock. Mono.Cecil is BepInEx's own (`BepInEx/core/Mono.Cecil.dll`, already on the compile-time reference set; not a new runtime dependency and nothing ships a copy). It reads `InMemory` with a resolver that resolves nothing, retargets `call` / `callvirt` / `newobj` / `ldftn` / `ldtoken` and `ldfld` / `ldflda` / `stfld` / `ldsfld` / `stsfld` operands, replaces a field access with a `call get_X` / `set_X` (accessor chosen by opcode) for a `fieldToProperty` and the reverse for a `propertyToField`, and rewrites a `TypeRef`'s name or `AssemblyRef` scope for a `typeRename` / `typeMove`. Stages run in order — types, then members — and the sites are collected **before** any mutation, so a type rule cannot move the key a member rule is looked up by.

**`addParam` is never generated and not applied by this cut.** An added parameter needs a human to decide which value preserves the old behavior, and getting it wrong is a silent behavior change rather than a load failure — the worst failure mode this feature can have. C# optional-parameter defaults would be recorded in metadata, but an Il2CppInterop generator emits none, so the value would be ours, not the API author's.

**Generating one.** `tools/driftmap` (`dotnet run --project tools/driftmap -- --old <archived interop> --new <current interop> --side client --build <id> --out driftmap-<id>.json`) diffs two interop assembly sets with `System.Reflection.Metadata` — no Cecil, no assembly load, so it runs on a machine that never launched V Rising. It writes only the `generated` block and carries an existing `hand` block over **verbatim**; a malformed existing map aborts the write rather than losing hand rules. Ambiguity is never guessed: two candidates for one rename emit nothing and say so. **`--old` exists because something kept it.** The archived interop set comes from the resilience pass's [interop archive](#update-resilience) (`interop-{side}-{build}/` beside the address tables, newest three per side, job FUPD4); BepInEx regenerates `BepInEx/interop` in place on a game update, so no archive means no drift map for that update, ever. `pwsh tools/resilience-report.ps1` lists the archives and prints the command line for the build installed now.

**A signature match is not evidence of a rename** (R33 finding 7). One removal plus one unrelated addition with the same signature in one type is an ordinary shape for a build diff — `EnableDebugDraw(int)` goes, `SetSpawnBudget(int)` arrives — and `high` is applied without review, so that pairing would silently redirect every call to a method that compiles, binds, runs and does something else. The generator now requires a second piece of evidence before `high`: the names have to look like the same member (one contains the other, they share a leading run of four characters, or their edit distance is small relative to their length — `DriftDiff.NamesLookLikeARename`). Anything that fails it is **still emitted**, as `heuristic`, which is reported, never applied, and which an operator can promote to a hand rule after looking at it. This pass invents the target rather than being given one, so it has to earn the confidence.

**Collectible-ALC discipline.** Cecil lives in the non-collectible default ALC, so a cached `ModuleDefinition` or a resolver holding the module would pin the plugin's `LoaderAllocator` — the Harmony pin with different nouns. The pass is therefore synchronous on the loading thread, everything is created and disposed inside one `using` scope, the resolver is fresh per pass and reads nothing, and **only a `byte[]` leaves the method**. `CompatRewriterTests` loads a rewritten image into a collectible ALC, invokes it, unloads, and asserts it collects.

**Mode.** `[Compat] RewriteMode` in `BepInEx/config/progenitor.cfg`:

| Value | What happens |
|---|---|
| `Off` | The pass computes nothing and returns the input array by reference. |
| `DryRun` (**default**) | The full analysis and the full report run; the **original** bytes load. |
| `Apply` | The rewritten image is what reaches `LoadFromStream`. |

The key is **bound** like every other Core section — `Plugin.Install` → `ConfigHelper.BindEnum` on the shared `ConfigFile`, so the key and its description appear in the `.cfg` instead of being an undocumented string an operator has to know to type, and stored as a string so a `ConfigEntry<RewriteMode>` cannot pin the collectible generation. It is read **once per generation**, at install, and latched (`CompatRewritePolicy.Adopt`): the mode decides whether a plugin's IL is transformed, and a value that changed halfway through a generation would mean two plugins in one process disagreed about what ran.

**One source of truth per generation (FCOMPAT4, R34 finding 12).** The latch has to be visible on the seam the load path actually reads. `Adopt` used to *clear* `CompatRewritePolicy.ModeReader` as its last act, and the load path only consults `Mode` when a reader is set — so the bound value was dead, every plugin load fell back to a fresh `File.ReadAllLines` of the `.cfg`, and the startup line `Compat: RewriteMode=Apply (bound from [Compat] RewriteMode)` was a claim about a value nothing read. `Adopt` now installs a reader that answers the adopted value and nothing else, and the by-hand `.cfg` read (`CompatRewriteHost.ModeForLoad`) is latched once per process rather than taken per load, so an operator — or BepInEx's own config writer — editing the file mid-session cannot load plugin B under a different mode than plugin A. `CompatRewriteHost.Reset` clears the latch, which is what makes a Core swap re-read.

Bootstrap's mirror of `CompatRewriteHost` reads the same `[Compat]`/`RewriteMode` entry out of the same `.cfg` **by hand**, and has to: its reloader answers this question from inside the load path, on a thread where no Core generation and therefore no Core `ConfigFile` binding exists yet, and Bootstrap cannot compile-reference Core to reach the bound value. Both sides read the same section, the same key and the same default, and `CompatRewriterTests` drives both readers against one written `.cfg`.

Enabling `Apply` means the operator's allowlist vets **an input to a transformation rather than the code that runs**. That is the honest statement of the residual risk, and it is why `DryRun` is the default, why the map itself is now on the allowlist, and why the map loads from Core-controlled locations only.

**Every failure loads the original bytes.** Cecil absent or version-skewed (a BepInEx upgrade can change it under us), no map for this build, a map for another build, a map the allowlist does not carry, an unreadable image, an Authenticode-signed image (a signature someone attached to exactly those bytes — refused, not stripped), a rule whose target cannot be built, a `Write` that throws, or a failed self-check. The pass re-reads the image it wrote and confirms no **claimed** fix's `from` key survives; a claimed fix that did not land discards the whole rewrite. *Claimed* is load-bearing (FCOMPAT4, R34 finding 3): the refusals above — the frozen `ldtoken` row, a `typeMove` with no destination scope, a nested destination, an `ldflda`, a non-`call` property accessor — leave the old key in the image by design, and the self-check used to read those as proof of failure and discard every rule that *did* land. One unrewritable site then threw away the working fixes beside it and the plugin loaded unrewritten, with nothing the operator could do about it, because the "offending" rule was one the design requires to be skipped. The pass records each refusal as a **site** (`RewriteReport.RefusedSites`: the method's position in the module walk plus the instruction's IL offset, or the TypeRef row for a type rule) and the self-check skips those sites and nothing else. Per *rule* was much too wide (FCOMPAT5, R35 finding 6): four of those five refusal paths decline **one instruction**, so a plugin that reads `Health.Value` in ten places and takes its address in one had the check turned off for the other nine — a retarget that silently did not land would then ship as `Applied = true` and throw at JIT time, which is the exact failure the self-check exists to catch. A rule that landed somewhere and was refused somewhere else is still verified everywhere it landed. It can only ever turn a working load into a working load, or a broken load into a possibly-working load. Cecil 0.11.4 has no deterministic-write mode, so the rewritten hash is a **log field only** — never gated on, never compared, never put in a manifest.

**The log.** One block per plugin that had at least one applicable rule, capped at 20 detail lines. Dry run:

```
Rewrite: Consumer.dll
  allowlist   sha256 0d94a696993ae205 (vetted)
  loaded      sha256 0d94a696993ae205 (original; dry run)
  map         driftmap-248100992-6a147771-ec12531-66fe4b61f7a0d186.json sha256 0d94a696993ae205 (3 generated, 0 hand)
  mode        DryRun
  would apply 3 rules, 4 sites
              typeMove ProjectM!ProjectM.Mover -> ProjectM.Shared (exact), 1 site
              memberRename ProjectM!ProjectM.Alpha.Ping -> Pong (high), 1 site
```

Applied — the two hashes differ, and both are on the block, so the log answers "what did you vet" and "what did you run" separately. A multi-DLL plugin gets its dependency lines first:
Rewrite: dependency Dep.dll loaded into the plugin's context from the rewritten image

```
Rewrite: dependency Dep.dll sha256 6c1f0a3b8d2e4a70 allowlist=ok
Rewrite: Dep.dll
  allowlist   sha256 6c1f0a3b8d2e4a70 (vetted)
  loaded      sha256 b71c9e0455aa1d38 (rewritten)
  map         driftmap-248100992-6a147771-ec12531-66fe4b61f7a0d186.json sha256 0d94a696993ae205 (3 generated, 0 hand)
  mode        Apply
  applied     1 rule, 1 site
              memberRename ProjectM!ProjectM.Alpha.Ping -> Pong (high), 1 site
              fieldToProperty ProjectM!ProjectM.Flip.Value -> Value (high), 2 sites
  note        rewritten in memory only; the file on disk is unchanged
```

With no map for the running build the whole feature is one line and a byte-for-byte no-op: `Rewrite: no map for build <id>; plugins load unchanged`.

**Not verified in-game.** No V Rising patch has shipped since this landed, so the entire "what a real patch does to the interop set" premise is inferred from how the generator bins types, not observed. The tests cover it against synthetic assemblies built at test time (S1's rename, type move and field→property, with a moved `[FieldOffset]` as the negative control that must emit **nothing**; then a rewritten consumer loaded and **invoked** in a collectible ALC, and a two-DLL plugin whose dependency is rewritten, preloaded and run). The first real patch is the experiment. Still open: `addParam`; the embedded-resource map §4.5 describes (the on-disk file is the only source, which is why it is allowlisted); and the fact that `IsGameAssemblyName` still admits an unbounded namespace under a game root — `ScopeFor`, not the name test, is what actually bounds where a plugin can be pointed.


## Effects

`Progenitor.Effects`. Client vanilla-asset borrowing (M3.0) plus server-authored networked beams. Full API: [effects.md](effects.md). Skip token: `HarmonySlotHandoff.EffectsModule` (`"Effects"`).

Client Borrow lives under a DontDestroyOnLoad root named `progenitor-effects-<pluginId>`. Server `ctx.Effects.ServerBeam` / `Beams` instantiates the professor coil prefab so every client draws the game's sequence (`ProfessorCoilBeam_State_Shared` synced Start/End). `ctx.Effects` fills owner + plugin GUID and `Track`s both; unload and `RemoveAll` destroy the client root and the server beam entities. `.fx beam` is the client LineRenderer placeholder; `.beam` is the replicated game graphic.

```csharp
if (ctx.IsClient)
{
    var img = (UiImageHandle)ctx.Effects.Borrow(AssetKind.Image, "PassiveFrame_Normal");
    img.Color = new EffectColor(1f, 0.84f, 0f, 1f);
    img.Size = new EffectVec2(192f, 192f);
    ctx.Effects.Host(img, HostKind.Overlay);
}
```

| Kind | Handle | What it does | Host |
|---|---|---|---|
| `Image` | `UiImageHandle` | Vanilla sprite by name onto a mod Image. `Color`, `Size`, `Position`, `SortingOrder`. | Overlay. |
| `Particle` | `ParticleHandle` | Clone a named `ParticleSystem`. `Tint`, `Play`/`Stop`, world `Position`. | RenderTexture. |
| `Volume` | `VolumeHandle` | New HDRP Volume + Vignette (fallback ColorAdjustments). **Camera-wide, not per-panel.** | Host is a no-op. |
| `Sequence` | `SequenceEffectHandle` | Buff prefab sequence via `SequenceUtility.PlaySequence` at `Position`. `Tint` is the spawned hybrid driver's `startColor`. | RenderTexture. |
| `VisualEffect` | disabled | Catalog only this cut (`Find`). | — |

`HostKind.Overlay` = Screen Space Overlay canvas, sorting 5000. `HostKind.RenderTexture` = unused layer 31→8, ortho camera, ARGB32 RT, `RawImage`. Dispose of the host releases RT/camera/canvas; the world clone stays until the borrow handle is disposed. Double-dispose is a no-op.

`ctx.Effects` does not throw on the wrong side: `Borrow` / `Host` / `Find` on the server return a disabled handle / empty list; `ServerBeam` / `Beams` on the client return a disabled handle. `Assets.RemoveAll(owner)` and `ServerBeam.RemoveAll(owner)` are the static forms; `ctx.Effects.RemoveAll()` calls both.

### Sample commands

`samples/Progenitor.Sample` is the live consumer. Chat is server-side (`.fx` is registered like `.ember`); the server sends custom event `0x536D4678` (`SmFx`) to that user's client, which borrows through `ctx.Effects`. Disposal uses `MainThread.PostDelayed` (never a coroutine or `Timer`). `.fx off` disposes every handle the Sample still holds.

| Command | Client log prefix | What you should see |
|---|---|---|
| `.fx image` | `Sample: fx image …` | Gold `PassiveFrame_Normal` sprite at screen center for 5 s. |
| `.fx particle` | `Sample: fx particle …` | Gold-tinted `Embers` particle system through a RenderTexture overlay for 5 s. |
| `.fx beam` | `Sample: fx beam …` | **Client placeholder** (owned LineRenderer). Cardinal / GameRed sequence on this client only; other players do not see it. |
| `.fx vignette` | `Sample: fx vignette …` | Gold camera-wide vignette for 3 s. |
| `.fx off` | `Sample: fx off dispose N handle(s)` | Everything the Sample borrowed is gone this frame. |

Each command also logs one `Sample: fx …` line per step (borrow, tint/host, dispose-in-Ns, disposed). The Effects module adds its own `Effects: borrowed` / `hosted` / `disposed` lines.

| Command | Server log prefix | What you should see |
|---|---|---|
| `.beam [seconds] [red\|blue\|coil] [count]` | `Sample: beam spawn …` plus `Effects: server beam spawned prefab=… entity=… len=…` then `… tracking a -> b` | Networked coil beam from you to the nearest Sample rat (else 6 m ahead), tracking both, default 8 s / red / count 1. Every client draws the game sequence. |
| `.beam off` | `Sample: beam off` plus `Effects: server beam disposed` | The Sample's server beams are gone. |

## Camera

`Progenitor.Cameras`. This module controls the local camera for cinematic shots, such as a free-form fight shot. `ctx.Camera` (`PluginCamera`) is **client-only**: every member, `Reset` included, throws `InvalidOperationException` in a server context. After this plugin's unload has released the camera, every call throws as well. Shots need the client world. Before it exists, `SetMode` and the shot calls throw, and `Reset` only clears state.

**Verification:** the mode switch was verified live via scratch (2026-09-25, retail client). This implementation is unit-tested (`CameraTests`), not live-verified. The per-frame writes are read from the client metadata and the native job bodies, and still need the live check below. The free-camera lighting fault and its fix were measured live through a plugin prototype; the module's own focus hook has not been run (see [Lighting under free shots](#lighting-under-free-shots)).

**Live check (2026-09-25, isolated server, client Core with this change).** At the game camera's exact pose, back to back: `_g_CameraZoom` read 15.67 and the volume anchor sat on the floor at the look-at point under the free shot. The frames match the game camera except for foreground geometry near the lens (a chandelier in the corner): the game camera shades it dark, the free camera lights it, and that accounts for the remaining warm difference (mean RGB game 30.8/23.1/20.6, free 38.1/26.6/22.1, frames kept in the internal research notes). Before the change the same comparison read 34.2/24.6/20.4 against 53.2/44.9/45.6.

```csharp
if (ctx.IsClient)
{
    // Cut to a wide free shot of the arena.
    ctx.Camera.FreeShotLookAt(new UnitVec3(-40, 25, -20), bossPosition, instant: true);

    // Four seconds later, orbit the boss; four seconds after that, back to normal play.
    // Passing ctx.Owner drops both posts if the plugin unloads first.
    Progenitor.Dev.MainThread.PostDelayed(4, () =>
        ctx.Camera.Orbit(bossClientEntity, distance: 14f, yawDegrees: 30f, pitchDegrees: 20f), ctx.Owner);
    Progenitor.Dev.MainThread.PostDelayed(8, () => ctx.Camera.Reset(), ctx.Owner);
}
```

Each shot call replaces the previous shot at once, so a sequence needs time between calls. Unload resets the camera even if the last step never runs.

| Member | What it does |
|---|---|
| `SetMode(CameraMode)` | Asks `LocalUserSystem` for `TopDown`, `Orbit`, `Free` or `Static` and stops any scripted shot. The game's own input then drives the camera. |
| `Mode` | The game's current mode (`_CameraType`). A switch shows here **by the next `LocalUserSystem` update**, not in the frame it was requested. `None` means there is no client world. |
| `Orbit(Entity, distance, yawDegrees, pitchDegrees, instant = false)` | Orbit a **client-world** entity (a server entity id is not valid here). The anchor follows it. Switching to a new entity is always a cut. |
| `Orbit(UnitVec3 point, …)` | Orbit a fixed world point. The anchor is `point` plus the orbit camera's `AnchorHeightOffset`. |
| `FreeShot(UnitVec3 position, yawDegrees, pitchDegrees, instant = false)` | Place the free camera. The scene is lit for the point where the view ray meets the floor the local player stands on, or 15 m along the ray (see [Lighting under free shots](#lighting-under-free-shots)). |
| `FreeShot(UnitVec3 position, yawDegrees, pitchDegrees, UnitVec3? focus, instant = false)` | Same, lit for `focus`, the point the shot is about. `null` derives the focus as above. |
| `FreeShotLookAt(UnitVec3 position, UnitVec3 lookAt, instant = false)` | Same as `FreeShot`, aimed at a point; `lookAt` is also the focus. |
| `Reset()` | Stop any shot and return to `TopDown`, whichever plugin owned the camera. Throws on the server and after this plugin's unload; with no client world it only clears state. |
| `IsShotActive` | True while this plugin's shot is being written every frame. |

**Shots.** Each frame the module rewrites the active shot until one of these ends it:

- `Reset` or `SetMode`
- another shot
- another plugin taking the camera
- a client world change
- unload

Calling the same kind of shot again retargets it without a new mode request. With `instant: false` the game's own smoothing glides to the new values. With `instant: true` the first write that lands also sets `LastTarget` and `Current`, so the view cuts.

**Values.** Angles are in **degrees** because the game stores degrees; nothing converts to radians. The conventions and limits:

- Yaw 0 looks along world +Z and yaw 90 along +X. For a point orbit this is world-relative by construction (the anchor rotation is written as identity). For an **entity orbit** the game composes yaw with the target's anchor rotation; the module sets `FollowTargetObjectRotation` off, which should keep it world-relative, but that is **unverified** until the live test.
- Positive pitch looks down, so a positive orbit pitch puts the camera above its anchor.
- Yaw is wrapped to (-180, 180]. It is written as the equivalent angle nearest the camera's current yaw, so a retarget never swings the long way round.
- Pitch is clamped to ±89°.
- Positions, angles and distances must be finite and below 100000 (the game's `TopdownCameraSystem.MAX_REASONABLE_VALUE`).
- Orbit distance must be at least 0.1 m.

A value that breaks these rules throws `ArgumentOutOfRangeException`.

**Orbit limits.** `OrbitCameraSystem` clamps the target distance to `[MinZoom, MaxZoom]` and the pitch to `[MinPitch, MaxPitch]` every frame, with or without input. A shot widens those limits on the live camera instance just enough to admit itself. The next mode switch (including `Reset`) discards that instance, so nothing needs restoring.

**Input.** While a shot runs, the player's mouse, keyboard and wheel do not move the camera. The module uses the game's own fields:

- **Free shots** set `FreeCameraState.StaticCamera`, the flag the game's own `Static` mode sets. It makes the free camera's `UpdateInputs` skip input entirely.
- **Orbit shots** zero the orbit camera's zoom speed and its mouse, auto-rotation, anchor-height and over-the-shoulder sensitivities, plus `AutomaticRotationSpeed`. The orbit job also adds the raw auto-rotate axis (`AutoRotation × deltaTime`) to yaw with no sensitivity applied. The shot rewrites the target every frame, so that can move the view by at most one frame's delta. The live test must confirm this.

The shot is not meant to block the character's own gameplay input (movement, abilities); that is unverified and is a live-test item. `SetMode` without a shot leaves the game's camera input on.

**Timing.** The module writes on `Plugin.Frame`. That event is raised from Bootstrap's `FrameTicker.Update` or from the bootstrap systems' update postfixes, all inside Unity's `Update` phase. The camera systems run later in the same frame: `CameraPresentationGroup` is `OrderFirst` in `PresentationSystemGroup`, which runs in `PreLateUpdate`. So each frame's write is the `Target` the free or orbit job reads that frame:

1. The job applies input to it (none, with input suppressed).
2. It smooths `Current` toward it.
3. It copies it into `LastTarget`.
4. `HybridCameraSystem` hands the result to the Unity camera.

This ordering is read from system attributes and Bootstrap's tick sources, not measured. If the tick ever ran after the camera systems, the shot would lag by a frame but still converge. The module subscribes to `Plugin.Frame` only while a shot is active.

**Ownership and unload.** There is one local camera, so the last plugin whose `SetMode` or shot request succeeded owns it. A request that the game refuses (no `LocalUserSystem`) throws and leaves the previous owner in charge. A request that throws leaves the camera state unknown, so the caller becomes owner without a shot and still resets on unload. A new owner clears the previous owner's shot. The camera goes back to top-down in these cases:

- **Plugin unload:** the owner resets the camera for every unload reason, but only if it left top-down. A plugin that no longer owns the camera leaves it alone.
- **Core uninstall:** the owner is reset too, so a hot reload never strands the player in the free camera.
- **Failed write:** the shot stops and the camera resets.

A client world change drops the shot without a request, because the rebuilt world starts on its own camera. A new shot call requests its mode again when the game no longer reports it and no request is pending. If the wanted mode has not appeared after 60 frames, the module sends the request again, up to 3 times. After that it logs once and stops the shot; the plugin keeps ownership, so its unload or `Reset` still restores top-down.

**Default camera values.** What the client dump shows, and what needs a live read:

| Value | Source |
|---|---|
| Free-camera mouse pitch clamp ±80° | Constants `FreeCameraSystem.CameraUpdateJob.MinimumRotationPitch` / `MaximumRotationPitch`. |
| Camera value guard 100000 | `TopdownCameraSystem.MAX_REASONABLE_VALUE`. |
| Orbit `MinZoom`, `MaxZoom`, `MinPitch`, `MaxPitch`, `ZoomSpeed`, sensitivities | Baked from `OrbitCameraComponent` authoring data into the prefab; not in the dump. **Needs a live read** of the orbit camera instance. |
| Free `DefaultYaw`, `DefaultPitch`, `BaseMovementSpeed`, lerp strengths | Baked from `FreeCameraComponent`. **Needs a live read.** |
| Top-down `ZoomSettings` (`MinZoom`, `MaxZoom`, `MinPitch`, `MaxPitch`) | Baked into `TopdownCamera.StandardZoomSettings` / `BuildModeZoomSettings`. **Needs a live read.** |
| Free camera lighting target | Where the view ray meets the plane **y = 0**, not the floor (below). Read from native code and measured live. |
| Any camera's initial `CameraTarget.Target` | The local user's singleton entity from `LocalUserSystem.TryAssignCameraToLocalUser`; the exact query was not decoded. For the top-down camera it is the player character (observed live). |

### Lighting under free shots

**Game fact.** The game places its lighting on the camera's *target*, not on the camera itself. `HybridCameraSystem.OnUpdate` ends with three calls, in this order: `UpdateCameraData`, `UpdateVolumeAnchor` and `WriteCameraShaderProperties`. Two of them read the target, the entity named by `CameraTarget.Target` (offset 0x0) on the camera, and use its `Translation`:

- `UpdateVolumeAnchor` moves `CameraManager._VolumeAnchor` to that position. This is the `CameraVolumeAnchor` GameObject, used as HDRP's `volumeAnchorOverride`. Local volumes blend by the anchor's position, not the camera's.
- `WriteCameraShaderProperties` sets the shader global `_g_CameraZoom` to the distance from `HybridCameraData.Position` to that position. The game's shaders shade by this value.

`CameraTarget` is not only a lighting input. It also feeds the presentation graphs: `PresentationGraphSystem_Update.CameraTargetEntity` (client) and `UpdatePresentationPostGraphSystem`'s `cameraTargetSingleton`. The latter drives zoom-modifier, sound and volume-profile outputs. Whatever the module names there must therefore be a real subject or the camera's own helper.

A new camera's target is the local user's singleton entity (`LocalUserSystem.TryAssignCameraToLocalUser`). For the top-down camera that is the player character, so its anchor is at her feet and its zoom is its real distance from her. The **free camera** instead targets a helper entity, `FreeCamera.BonusCustomTargetEntity`. Live on 2026-09-25, that helper (70751:7) carried `Translation`, `CustomCameraTarget` and `Simulate`. Every frame `FreeCameraSystem.RayCastTarget_Job` moves the helper to where the camera's forward ray meets the plane **y = 0**, then points `CameraTarget` at it. It does neither when the ray misses that plane, for example on a level or upward view. On a floor that is not at sea level the anchor therefore lands under the floor and `_g_CameraZoom` comes out several times too large.

The point orbit uses the same pattern: `LocalUserSystem.UpdateCamera` moves `OrbitCamera.BonusCustomTargetEntity` to the requested point and targets it. Whether that orbit helper is tagged `CustomCameraTarget` is **not confirmed**. An entity orbit (`SetOrbitCamera(entity)`) targets the entity itself. An **orbit shot** requests the orbit camera with both arguments empty, so its target stays the local user's singleton entity. An orbit shot of anything away from the player is therefore lit, and presented, for the player.

**Measured (isolated server, retail client, 2026-09-25).** The test was in Dracula's throne hall, whose floor is at y = 15. At one camera pose, `_g_CameraZoom` read 35.9 under the free camera and 14.9 under the game camera. Mean frame RGB from back-to-back captures at that same pose:

| Camera | Mean R / G / B |
|---|---|
| Game camera | 34.2 / 24.6 / 20.4 |
| Free camera, game's own target (y = 0) | 53.2 / 44.9 / 45.6 |
| Free camera, target moved to the shot's look-at point every frame before `HybridCameraSystem` | 40.5 / 27.8 / 21.8 |

Toggling the target write reproduced the difference. The prototype for that experiment was a plugin hook; the module code below has **not** been run live.

**The fix.** Every shot has a focus point, and `CameraTarget` is pointed at it:

| Shot | Focus |
|---|---|
| `FreeShotLookAt` | `lookAt` |
| `FreeShot(…, focus)` | `focus` |
| `FreeShot` with no focus | Where the camera's actual view ray (the smoothed `FreeCameraState.Current`) meets the horizontal plane at the local player character's height (`Translation.y` of the `LocalCharacter` entity). The distance along the ray is clamped to 2–1000 m, so a steep crane shot keeps its floor point and the focus never sits on the camera. A view pitched down less than 5°, an upward view, a floor plane above the camera, or no local character uses the point 15 m along the ray instead. |
| Entity orbit | The orbited entity itself: `CameraTarget.Target` names it, as the game's own `SetOrbitCamera(entity)` does. No `Translation` is written. |
| Point orbit | The point, through the orbit camera's helper. |

While a shot is active, the module registers a `SystemHooks.Before<HybridCameraSystem>` hook. `FreeCameraSystem` and `OrbitCameraSystem` are both `[UpdateBefore(HybridCameraSystem)]` in `CameraPresentationGroup`, so the hook runs after the camera has moved and after the free camera's ray cast. For free shots and point orbits, each frame it does what the game's own point orbit does:

1. It writes the focus into the `Translation` of the camera's own helper, `BonusCustomTargetEntity`, using the guarded typed `Write<Translation>`.
2. It points `CameraTarget.Target` at the helper, using an overlay write at the layout offset.

For an entity orbit it only does step 2, naming the orbited entity. A helper that is not tagged `CustomCameraTarget` is refused, so no real entity's `Translation` is ever moved. That the helpers carry the tag is **to confirm live**: the free camera's did on 2026-09-25, and the orbit camera's is unchecked. If the orbit helper lacks it, point orbits keep the game's own target. The hook is disposed when the shot ends. Nothing is restored afterwards:

- The free camera moves its helper again on its next frame.
- Every way an orbit shot ends (`Reset`, `SetMode`, another plugin's shot, unload) requests a mode, and that discards the camera instance.

A focus that cannot be written leaves that frame to the game's own target and never stops the shot; the first such failure after the camera is up is logged once.

**Residual.** With the target moved to the look-at point, the frame was still slightly warmer than the game camera's (+6.3 red, +3.2 green, +1.4 blue). The cause is **not established**; it was not measured separately. The leading candidate is the anchor's position: the fix put it at the shot's look-at point, while the game camera puts it at the player's feet. The anchor therefore sat at a different place, and possibly a different height, and `_g_CameraZoom` was that point's distance rather than the player's. The remaining candidates are inputs outside `HybridCameraSystem` that depend on the camera type; none has been found in the native code read so far. Step 10 of the live test tells the two apart.

### Live test (acceptance procedure, not run)

Use the isolated test server and a test client that nobody is playing on. Drive `ctx.Camera` from a small client plugin or a scratch snippet.

1. Call `FreeShot` with a position 20 m above the player, yaw 0, pitch 45 and `instant: true`. Expect a cut to a view looking down at 45° toward +Z. After the next `LocalUserSystem` update, `Mode` should read `Free`.
2. Move the mouse, press WASD and scroll the wheel. Expect the camera not to move. Check whether the character still moves and casts; the docs claim no effect on gameplay input and that is unverified.
3. Call `FreeShot` again with yaw 90 and `instant: false`. Expect a smooth turn toward +X that takes the short way.
4. Call `Orbit(client player entity, 30, 0, 30)`, with 30 above the vanilla `MaxZoom`. Expect the camera to hold 30 m rather than snap back. Read `OrbitCamera` on the camera instance to confirm the widened limits. Hold right mouse and drag, then scroll. Expect at most one frame's movement.
5. Call `Orbit(point, 12, 45, 20)`. Expect yaw to be world-relative around the fixed point. Repeat with the player entity while she turns; confirm the entity orbit's yaw also stays world-relative.
   Confirm the orbit camera comes up from `SetOrbitCamera` with both arguments empty.
6. Reload the plugin mid-shot, then reload Core mid-shot. After each, expect the top-down camera with normal input.
7. Disconnect during a shot and reconnect. Expect the default camera and no error spam.
8. Lighting, in a room whose floor is not at y = 0 (Dracula's hall, y = 15). Stand the player there and note the game camera's pose. Call `FreeShotLookAt` from that pose toward the player. Read `Shader.GetGlobalFloat("_g_CameraZoom")` and `CameraManager.GetVolumeAnchor().transform.position`; expect a zoom near the game camera's (about 15, not about 36) and the anchor at the look-at point. Capture a frame and compare its mean RGB with the game camera's frame at the same pose.
   Repeat with `FreeShot` and no focus: expect the anchor on the floor plane at the player's height. Level the view (pitch 0): expect the anchor 15 m along the ray.
   Read `CameraTarget` on the free camera: expect it to name `FreeCamera.BonusCustomTargetEntity`, and that entity to carry `CustomCameraTarget` and `Translation`. After `Reset`, expect the anchor back on the player.
9. Orbit lighting. Run `Orbit(point, …)` and `Orbit(entity, …)` on something at least 20 m from the player. Expect the anchor at the point or at the entity's feet, not at the player. For the entity orbit, `CameraTarget.Target` should name the orbited entity, and its `Translation` should be unchanged by the module. Confirm that the orbit camera's `BonusCustomTargetEntity` is non-null and tagged `CustomCameraTarget`. If it is not, the module logs once and leaves point-orbit lighting to the game.
   Presentation: `CameraTarget` also feeds the presentation graphs. During the entity orbit, check that zoom-modifier, sound and volume-profile effects follow the orbited entity and cause no errors in the log. After `Reset`, check that they return to the player.
10. Residual. Pass the player's feet as `focus` to `FreeShot`, at the game camera's exact pose. If the RGB then matches the game camera, the residual was the anchor's position. If a difference remains, look for another input that depends on the camera type.
11. Restore the title-screen idle state per the standing rules.

### References

**Client metadata:**

| Type | Notes |
|---|---|
| `ProjectM.CameraType` | |
| `CameraRequest` | |
| `LocalUserSystem` | `_CameraEntity` 0x60, `_CameraType` 0x68; `SetOrbitCamera(Nullable<Entity>, Nullable<Translation>)`, `SetFreeCamera`, `SetStaticCamera`, `SetTopDownCamera` |
| `FreeCameraState` | `LastTarget`, `Target`, `Current` at 0x4 / 0x18 / 0x2C, each {Yaw 0x0, Pitch 0x4, Position 0x8}; `MovementSpeed` 0x40 |
| `CameraManager` | static `_VolumeAnchor` 0x10 (the HDRP volume anchor `UpdateVolumeAnchor` moves) |
| `FreeCamera` | `BonusCustomTargetEntity` 0x14 (the free camera's lighting helper) |
| `HybridCameraData` | `Position` 0x9C (the camera end of `_g_CameraZoom`) |
| `CameraTarget` | `Target` 0x0 |
| `CustomCameraTarget` | tag on camera helper entities |
| `OrbitCamera` | `BonusCustomTargetEntity` 0x28 (the point orbit's helper) |
| `OrbitCameraState` | `LastTarget`, `Target`, `Current` at 0x24 / 0x4C / 0x74, each {OrbitDistance 0x0, OrbitYaw 0x4, OrbitPitch 0x8, AnchorPosition 0xC, AnchorRotation 0x18} |
| `TopdownCamera` | |
| `TopdownCameraState` | |
| `CameraPresentationGroup` | `[UpdateInGroup(PresentationSystemGroup, OrderFirst = true)]` |
| `LocalCharacter` | tag on the local player's character (client) |
| `FreeCameraSystem` | `[UpdateBefore(HybridCameraSystem)]`; `RayCastTarget_Execute` |
| `HybridCameraSystem` | `UpdateVolumeAnchor_Execute`, `WriteCameraShaderProperties_Execute` |
| `OrbitCameraSystem` | `[UpdateBefore(HybridCameraSystem)]` |
| `TopdownCameraSystem` | |

Every offset above is declared in [`layout.txt`](../src/Progenitor.Core/Layout/layout.txt) (the `Cameras` block) and checked by `layoutgen --check`.

**Native code (client `GameAssembly.dll`):**

- **`LocalUserSystem.UpdateCamera`** instantiates a fresh instance of the mode's prefab on each request. Top-down, orbit and free use `TopDownCameraPrefab`, `OrbitCameraPrefab` and `FreeCameraPrefab`. Static also uses `FreeCameraPrefab` and sets `StaticCamera`.
- **`FreeCameraSystem.CameraUpdateJob`:**
  - In `UpdateInputs`, `StaticCamera` skips all input, and mouse pitch is clamped to ±80.
  - `UpdateCamera` smooths `Current` from `LastTarget` and `Target`, then copies `Target` into `LastTarget`.
  - Angles are multiplied by 0.017453292 before `quaternion.Euler`.
- **`OrbitCameraSystem.CameraUpdateJob`:**
  - In `UpdateInputs`, the pitch and distance clamps run every frame.
  - `UpdateCamera` and `GetAnchorPosition` use the custom look-at target and otherwise fall back to `Target.AnchorPosition`.
  - `UpdateCameraLerp` snaps when the target is new.
- **`HybridCameraSystem.OnUpdate`** ends with `UpdateCameraData_Execute`, `UpdateVolumeAnchor_Execute` and `WriteCameraShaderProperties_Execute`, in that order.
  - The `UpdateVolumeAnchor_Job` lambda body sets `CameraManager._VolumeAnchor.transform.position` to the `Translation` of `CameraTarget.Target`, when it has one.
  - The `WriteCameraShaderProperties_Job` lambda body calls `Shader.SetGlobalFloat("_g_CameraZoom", distance(Translation, HybridCameraData.Position))`.
- **The `FreeCameraSystem.RayCastTarget_Job` lambda body** intersects the camera's forward ray with the plane through the origin with normal (0, 1, 0). On a hit in front of the camera (`t > 0`), it writes the hit point into `FreeCamera.BonusCustomTargetEntity`'s `Translation` and sets `CameraTarget.Target` to that helper.
- **`LocalUserSystem.TryAssignCameraToLocalUser`** sets a new camera's `CameraTarget` to a local-user singleton entity. **`UpdateCamera`**, for an orbit request with a position, writes that position into `OrbitCamera.BonusCustomTargetEntity`'s `Translation` and targets it.

**Research:** the necromancer demo camera notes (live mode switch, `_CameraType` lag) are in the internal research notes.

## Photo mode

A built-in client feature, **off by default**. There is no plugin API: it is Core's own, for players, admins
and anyone filming. Source: `src/Progenitor.Core/Cameras/PhotoMode.cs` (client runtime and the server's
pause handler), `PhotoFlight.cs` and `PhotoPause.cs` (game-free, unit-tested in `PhotoModeTests`).

**Turning it on.** `[PhotoMode] Enabled = false` in `BepInEx/config/progenitor.ui.cfg`, which is
Core's own client cfg and is listed in the Mods settings panel (F10). While it is off, the toggle key only shows
a notice saying where to turn it on. The other settings in that section:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Allow photo mode at all. |
| `HideHudOnEnter` | `true` | Hide the HUD when photo mode starts. |
| `HideNameplates` | `true` | Hiding the HUD also hides nameplates, health bars and floating combat text (`CharacterHUDsCanvas`, `SCTCanvas`). |
| `LookSensitivity` | `1` | Mouse-look speed multiplier. |

**Keys.** Every key is a keybind registered through the keybind API in the same cfg's `[Keybinds]` rows, so each
is listed in the Keybinds tab and can be rebound there. `PhotoMode` is an ordinary gameplay bind. All the others
are **Photo-scope** binds: they fire only while photo mode is active (it holds the character's input then), so they
never clash with, or lose to, a game key on the same chord, and they clash only with each other. Nothing in photo
mode reads a device itself: the pointer (right-button look, mouse movement, the wheel) comes from the framework
input layer's pointer sample (`UnityInputRuntime.TrySamplePointer`, beside `ctx.Input`'s activity sample), and
`DirectKeyReadSourceTests` keeps it that way.

| Key | Default | Does |
|---|---|---|
| `PhotoMode` | Ctrl+Shift+F9 | Enter or leave photo mode. Leaving is the way back to normal play. Not Ctrl+F9, which is the Sample's log panel: a framework default must never take a key a mod already uses. |
| `PhotoModeHideHud` | H | Hide or show the HUD. |
| `PhotoModePause` | P | Pause or resume the world. Admins only; the server decides. |
| `PhotoModeForward` / `PhotoModeBack` | W / S | Fly along the view (held). |
| `PhotoModeLeft` / `PhotoModeRight` | A / D | Fly sideways (held). |
| `PhotoModeUp` / `PhotoModeDown` | E / Q | Fly straight up / down (held). |
| `PhotoModeFast` / `PhotoModeSlow` | R / F | Hold for 4× / one fifth of the speed. |
| `PhotoModeZoomIn` / `PhotoModeZoomOut` | Z / X | Narrow / widen the field of view by 2.5° a press. |
| `PhotoModeExit` | Escape | Leave photo mode. The game's system menu does not open (the exclusive mask takes Back / Menu_Close). |
| `PhotoModeLockOn` | Middle mouse | Lock on: hold to target, release over a unit to lock, tap to release. |

The Keybinds tab lists all of these in their own **Photo mode** group, after Progenitor's section, in the order
toggle, exit, forward, back, left, right, up, down, fast, slow, zoom in, zoom out, lock on, hide HUD, pause, followed by two
fixed rows: **Look around** (hold right mouse) and **Flying speed** (mouse wheel). Photo mode reads the
right mouse button and the wheel itself (a bare right click cannot be a keybind, and the wheel is not a key), so those two are shown, not rebindable.

**No gameplay input while flying.** Entering photo mode takes an exclusive hold on Progenitor's input context
(`UiInputCapture.AcquireExclusive`; the same `RotateCameraBlocker` context and `GetConsumedInputs` mask the Mods
workspace uses, no new native hook) with the `Exclusive` mask: every button action except the UI-navigation
collection (`BlockAllKeys(false)`), `Back` and `Menu_Close`, `RotateCamera` (not allowed back), every analog action
(`AnalogInputAction.All`: move, aim, zoom, camera rotate and move, radial, map, menu sticks) and the menu-open
hotkeys in both button lists. Other mods' keybinds that do not opt into firing while input is captured are quiet
too. On exit the hold stays until every key and mouse button held during photo mode is released (at most 2 s), so
a release does not reach the game as an action; a Core unload drops it at once. Not covered, by design: the
UI-navigation collection (its contents are set by a native static constructor the dump does not include), which
uGUI needs.

**The character does not turn.** V Rising turns the character toward the cursor's world point, which the game
writes into the input command (`EntityInput.AimPosition` and its plane, direction and length, plus
`InputCommand.RotationInput`), not through a button or analog action, so the mask alone did not stop it
(hands-on finding 2026-10-02). Photo mode now holds the aim through its own input lease's **Aim channel**, the
existing command patch `ctx.Input` uses: on the first command after entry it reads the aim the game built from the
cursor and pins the lease to it, and the lease (and with it the held aim) ends on every exit path and on Core
unload. No new hook. Gamepad aim (right stick) is `AnalogInputAction.AimX` / `AimY`, which the exclusive mask
already takes, and the held aim overrides it anyway. Bridge: `photo status` → `aimHeld`, `aimMethod`.

**Lock-on.** Hold the lock key (middle mouse) to target: the unit the game hovers under the cursor (its own hover,
read from the newest input command; living units with `Health`, players, NPCs and bosses) is previewed while the
key is held, as a "Target: name" notice. Release over a unit to lock it ("Locked: name" for 2 s); release over
nothing to cancel; tap the key (under 0.25 s, without moving onto a new unit) while locked to unlock ("Unlocked").
Nothing is shown while the key is up. The cursor is the game's normal cursor (photo mode never hides or locks it;
right-mouse look reads mouse movement), so it is visible while targeting. While locked:
- the camera keeps facing the target (1.2 m above its feet) through WASD / E / Q flight and follows it as it moves;
- the right mouse orbits around the target at constant distance instead of free look;
- **the wheel is the distance to the target** (×1.15 a notch, 1.5 to 200 m) rather than flying speed; R / F still
  fly fast / slow.

The lock ends with "Target lost" when the unit dies, despawns or leaves the client world (no longer exists, `Dead`,
or `Health` at 0), and silently on a world change or photo exit. The name is what the game's own target panel shows: a player's
character name (`PlayerCharacter.Name`); else the localized name of the unit prefab's `ManagedCharacterHUD`
(read through `GameDataSystem.ManagedDataRegistry` and the game's string table: "Rat", a boss's title); only when
neither resolves, a prettified prefab name (`CHAR_Critter_Rat` → "Rat"). The raw prefab name stays readable
separately. The name tag is a Progenitor notice panel at the framework's default place, not a label
anchored over the unit: there is no world-anchored label API yet. 
**No outline.** Offline research found no outline renderer and no per-unit highlight flag: the game's hover (`PickingSystem` → the local character's `Picker`, from `CursorPosition`) drives the character HUD and the target info panel, and `EntityInput.HoveredEntity` is only a one-way copy of it, so there is no proven way to outline an arbitrary unit. Lock-on therefore shows the name notice only (the flash on lock is the "Locked: name" notice). Overwriting `Picker` every frame is a possible later experiment, not implemented.
Bridge: `photo {"action":"lock","entity":"index:version"}` (a client-world entity) and `photo {"action":"unlock"}`;
`photo status` → `targeting`, `preview` and `lockedTarget`, each `{entity, name, prefab, nameSource}` (`nameSource`:
`player`, `localized`, `prefab` or `id`).

**Server switch.** `[PhotoMode] AllowOnThisServer` (default `true`) in the server's `progenitor.cfg`.
When it is `false`, a Progenitor client cannot enter photo mode (the toggle shows "Photo mode is disabled on this
server"), a client already in it leaves with HUD, camera and input restored, the Keybinds group shows that reason,
the admin pause is refused with `disabled-on-server`, and held pauses end. Changes take effect live. The value is
carried on Core's reserved event `'PrpA'` (the client asks once its Progenitor handshake succeeds; the server
answers and pushes every change), not through ConfigSync: Core is not in the handshake answer list, so a Core
entry cannot be synced and does not show a lock row in the client settings. A server without Progenitor cannot
enforce this: photo mode stays allowed there, without the pause.

A modifier cannot be a keybind's main key and a chord fires only with exactly its own modifiers, so the fast and
slow controls are plain held keys rather than Shift and Alt.

**Flying.** WASD moves along the view (pitch included). E goes straight up and Q straight down, whatever the
view. Hold the right mouse button to look. R is 4× faster and F 5× slower. The wheel changes speed (0.25 to
100 m/s, ×1.25 a notch, 6 m/s to start) and Z / X the field of view (10° to 110°). Motion
uses unscaled time, so the camera flies while the world is paused. There is no height, distance or ground limit.
Pitch is unlocked up to the free camera's ±89° (`CameraMath.PitchLimit`): at exactly ±90° yaw stops meaning
anything. A frame longer than 0.1 s moves as if it were 0.1 s.

**What it holds while active.**
- The camera, through a `CameraDirector` free shot rewritten every frame with `instant: true`. It is the last
  owner, as with `ctx.Camera`: a plugin that takes the camera afterwards wins, and leaving photo mode then does not
  reset it.
- An input lease (holder `progenitor.photo`) on every channel with `UserPolicy.Override`, so WASD, clicks
  and ability keys do not move or act for the character. The input release key (Ctrl+Shift+F12) ends the lease,
  and photo mode leaves with it. Keys that open game panels (inventory, map) are not blocked.
- The HUD canvases it disabled (`Canvas.enabled = false` on the root canvases under `HUDCanvas(Clone)`, each put
  back to its own prior value), re-disabled every frame in case the game turns one back on. The game's escape
  menu (`MainMenuCanvas`) and Progenitor's own panels are left alone.
- The main camera's field of view, only once Z / X has changed it.

It leaves on the toggle key, on the input release key, when the client world changes (disconnect, world switch),
and on Core unload. Every exit resumes a pause it asked for, restores the HUD and the field of view, and returns
the top-down camera.

**Pausing.** P sends `PhotoPauseRequest` on Core's reserved event `'PrpP'` (`0x50727050`, both directions like
`'PrsQ'`). The server re-reads the sender's `User`, refuses a non-admin with `NotAdmin` (the client shows a
notice and nothing changes), and otherwise takes one `ctx.Time.Freeze` lease for that user through
`TimeScaleRuntime`. The world stays frozen while any user holds one. A user's freeze ends when they press P
again, leave photo mode, disconnect or stop being an admin (checked every 30 server frames), and on Core unload
on either side. The client only sends when the Progenitor handshake with this server succeeded; otherwise it
says the server cannot pause. See [World speed](#world-speed) for what freezes and what keeps running.

### Live test (acceptance procedure, not run)

Use the isolated test server with a Progenitor client whose user is an admin, and a second, non-admin client.
Steps 2, 6 and 9 can be driven focus-free from the bridge: `photo` `on` / `off` / `status` on the client,
`keybind_press` for `PhotoModePause` / `PhotoModeHideHud`, and `time status` on the server to see the pause lease.
Flying (steps 3 and 4) needs real input and window focus.

1. With `Enabled = false`, press Ctrl+Shift+F9. Expect the notice and no camera change.
2. Set `Enabled = true` in the settings panel. Press Ctrl+Shift+F9. Expect the view to stay where it was, the HUD and
   nameplates gone, and the controls notice.
3. Fly with WASD, E/Q, R and F. Look with the right mouse button, straight up and straight
   down. Expect the character not to move or cast, and no ground or height stop.
4. Wheel for speed; Z / X for field of view. Expect the zoom to hold, not be reset by the game each frame.
5. H: the HUD comes back; H again: gone. Open the inventory while hidden and check what shows.
6. P as the admin: expect "World paused", units and VFX frozen on both clients, and the camera still flying.
   P again: resumed. P, then Ctrl+Shift+F9: expect the world to resume on leaving.
7. P as the non-admin: expect the admin-rights notice and the world still running.
8. Pause, then disconnect the admin client. Expect the server log to end the pause within a second.
9. In photo mode press Ctrl+Shift+F12. Expect photo mode to leave. Reload Core in photo mode: expect the
   top-down camera, the HUD and normal input.

### Not yet proven in the game

Everything below is behind `[PhotoMode] Enabled = false` and has only been compile-checked. All are ordinary
generated-interop property and method calls (no raw native calls, no new generic interop over game value types);
listed for the live check.

| Call | Used for |
|---|---|
| `Resources.FindObjectsOfTypeAll(Il2CppType.Of<Canvas>())` | Finding the game's HUD root canvases |
| `Canvas.get_isRootCanvas` | Keeping only root canvases under `HUDCanvas(Clone)` |
| `Behaviour.set_enabled` on game-owned HUD canvases (and the per-frame re-disable) | Hiding the HUD |
| `Camera.set_fieldOfView` on `Camera.main` | Zoom |

`Behaviour.set_enabled` is already used on Progenitor's own canvas; what is unproven is disabling the game's
HUD canvases and whether the game turns them back on or reads their state. The camera free shot, the input lease
and the `'PrpP'` custom event use existing framework seams.

## Build menu search

A built-in client feature, **on by default**, with no plugin API. In the game's build menu it adds:

- **A search box** above the items. Typing shows the matching items from **every** category and tab in the item
  row, whatever tab is selected.
- **A breadcrumb** in the hover tooltip of every item, with or without a search: `Decoration > Furniture` for a
  cell, `Decoration > Furniture > Workshop` for an item inside a group.

Source: `src/Progenitor.Core/Ui/BuildMenuSearch.cs` (the box and the per-frame hooks), `BuildMenuIndex.cs` (the
menu as data), `BuildMenuSwap.cs` (results from every tab), `BuildMenuCrumb.cs` (the breadcrumb),
`UnityUiRuntime.ExternalFields.cs` (the text field). Game-free and unit-tested: `BuildSearchMatch.cs`
(`BuildSearchMatchTests`) and `BuildMenuBlob.cs` (`BuildMenuBlobTests`, `BuildMenuResultsTests`).

**Status.** Live-verified on 2026-10-04 (Steam client 3840x1080 on the isolated test server):

- The box, the per-tab filter of the first version, its count and x button.
- Typing through the pump; Escape; click focus.
- Leaving the world, and Core hot swaps, with the menu open.
- The breadcrumb on a group cell and on an item inside a group.
- The all-tabs results, through the bridge. On Castle > Foundation, "brazier" put 9 cells in and the game drew
  the 6 unlocked ones from Lighting and Castle, with icons and costs. Hovering one showed the game's tooltip with
  "Lighting > Larger Lights". Switching to Walls kept the results. "stone" gave 20 cells. x put the tab back, and
  the copy was freed within the delay. One letter filtered the tab. A Core hot swap with results showing restored
  the game's blob, with no copy left, and re-attached the box.

### Settings

Mods menu (F10) > **Progenitor** > the **Build menu** group. Each switch takes effect at once. They are stored as
`[BuildSearch]` in `BepInEx/config/progenitor.ui.cfg`, Core's own client cfg, which is never
server-synced.

| Settings page label | cfg key | Default | Meaning |
|---|---|---|---|
| Build menu search | `Enabled` | `true` | Show the search box. Off while the menu is open: the box goes and the menu is the game's own again. |
| Clear search when the menu closes | `ClearOnClose` | `true` | Off: the menu reopens with the last search applied. |
| Show where items live | `Breadcrumbs` | `true` | The breadcrumb line in the tooltip. Independent of the search box. |

### Using the search

| Action | Effect |
|---|---|
| Click the box | Takes the keyboard. While it has it, typed letters do not reach the character or the build hotkeys. |
| Type | From two letters on, the item row shows the matches from every tab, at most 48 cells. The count beside the box reads "12 found", or "48 of 212 found" when there are more. One letter filters the selected tab. |
| Hover a result | The tooltip is the game's own, with the breadcrumb saying where the item normally lives. |
| x button (shown while the box has text) | Clears the search and keeps the keyboard in the box. The row is the selected tab again, with nothing selected for placement. |
| Escape | Clears the search; with the box already empty, gives the keyboard back to the game. The build menu does not close on that press. |
| Enter, or a click elsewhere | Gives the keyboard back and keeps the search. |
| Switch tab | The results stay; they are shown whatever tab is selected. |
| Close the build menu | Clears the search (`ClearOnClose`). |

**Matching.** The query is split into words. An item matches when every word appears in its names, in any order
and position, ignoring case, accents and underscores. An item's names are its localized name and its prefab name
with `TM_` dropped, so `tier02` finds the tier-2 pieces, an English word finds an item on a non-English client, and
a mod's building is found by its prefab name before its display name is localized.

**Grouping.** Results keep the menu's groups:

- A group whose own name matches shows whole.
- Otherwise, a group with several matching items shows as one group cell holding only those items.
- A group with one matching item shows that item on its own.
- A cell listed in two tabs is shown once.

Only items the player can build appear, exactly as in any tab. When something matches but none of it is unlocked,
the selected tab is filtered instead, and its count reads "0 of 17". The castle relocation menu always filters the
selected tab; it builds its row by another path.

### How it works

**The menu as data.** `BuildMenuMapper` (client) builds the item row every frame from the
selected tag of the composition blob: categories > tags > structures (a group, or one blueprint) > blueprints
(internal research notes, 2026-10-04). `BuildMenuIndex` reads that blob once per composition:

1. The mapper's `_CompositionPrefabGuid` names the prefab.
2. Its `BuildMenuComposition` component is read raw through its type index.
3. The blob is copied out under its own header length and walked, every array bounds-checked against that length.
   The offsets and strides are `layout.txt` declarations checked against the dump.

Names come from managed data, all reference types: category and tag names from `ManagedBuildMenuCompositionData`,
group names from `ManagedBuildMenuGroupData`, and item names from `ManagedBlueprintData`.

**Results from every tab.** Nothing native is patched or called. While a query has results, a
`SystemHooks.Before<BuildMenuMapper>` hook does three things before the mapper's update:

1. It points the composition's `Blob` reference at a private copy of the blob.
2. In the copy, the selected tag lists the result cells. The copy keeps every original byte at its offset, appends
   the cells and their blueprint records, and rewrites only that tag's header.
3. It resets the row's selection, as the game does on a tab change, because cells are reused by position.

The game then builds the cells, icons, costs, limits, tooltips and placement as for any tag. The original reference
goes back when any of these happens:

- the query clears;
- nothing unlocked matches;
- the menu closes or the search is turned off;
- the world changes or Core unloads.

Each of these resets the row's selection too. The write first checks the slot still holds the copy, and a copy is
freed 120 frames after the slot is confirmed not to hold it, or on Core unload; a copy that cannot be confirmed out
of use is kept rather than freed. Nothing unlocked matching shows up either as an empty row or as the game hiding
the empty tab and moving to the next one during its update. A move is judged one frame later, because a player's
key press also changes tab inside the update. Then the game's blob goes back and the player's tab is selected
again.

**The box.** The box is a child of `Layout_Keyboard`, beside the row's scroll view
(`StructureContainerScrollView` > `Viewport` (a `Mask`) > `StructureContainer_Inner`). A child of the viewport would
be clipped to the row's 128 px band. It is centered on the scroll view, because a full tab is wider than its window
and scrolls. It ignores the layout group and is placed every frame. When the all-tabs results cannot be shown, a
`SystemHooks.After<BuildMenuMapper>` hook hides non-matching cells of the selected tab instead. It only ever
re-enables cells it hid itself.

**The breadcrumb.** `BuildMenuMapper.UpdateTooltip` fills `BuildMenu.FakeTooltip` every frame from the hovered cell.
The after-mapper hook adds a Progenitor-owned row right under the tooltip's header (`TooltipHeader`), as the next
child of `FakeTooltip`'s vertical layout, so it spans the card and never runs under the header's icon (chosen
2026-10-04). It uses the subtitle's font at 85% of its size in a muted steel blue (#8FAFCB), wraps when long, and starts where the title's text
starts: the layout puts rows at the card's edge, so the row's left margin is measured from the title each frame.
It is never a frame behind the tooltip. The hovered GUID is the cell's `TooltipPrefabGuid` when set, else its
`PrefabGuid`, the same choice the game makes. The place is looked up in the index, preferring the selected tab
when an item is listed in several. A tall item icon hangs over the card's top edge exactly as in the game's own
tooltip (checked with the breadcrumb off).

**Keys.** The box is an ordinary framework text field outside any panel: the same typed-key pump, owned caret and
menu-hotkey guard (`MenuHotkeyGuard.BuildSearch`) as the Mods search. The pump runs while the box exists. While the
box has the keyboard, Core takes the exclusive input hold photo mode uses (`UiInputCapture.AcquireExclusive`). It
is released once every key is up after focus ends, so the Escape that ends typing does not also close the build
menu. Nothing here reads a device; Escape and Enter reach the box through the pump.

**Focus comes from Progenitor only.** Live, the game's menu navigation selected the box in the event system without
a click, and the hold then froze the character. So the box counts as focused only when Progenitor gave it focus,
by a click on it or by the bridge, and a stray selection by the game is dropped every frame.

**Limits.**

- Keyboard and mouse layout. The box is not added to the gamepad build menu (`StructureSelectionGroup_Gamepad`).
- The selected tab's button keeps its own name while it shows results.
- With a search active, the game's "viewed" bookkeeping sees the results tag: switching tab marks the new tab's own
  items as seen although the row shows results, and re-selecting the current tab marks the results as seen.
- Only the keyboard row was checked. The swap also feeds the gamepad row, untested; the box itself sits on the
  keyboard row only.

**Dev bridge.** The client op [`build_search`](bridge-ops-client.md#build_search) opens and closes the build menu without input or focus (as the B key does; `BuildMenuOpener.cs`), reads the state and drives the box: open,
focus, query, counts, `allTabs`, `resultCells`, `crumb`, each cell's names. The server op [`unlock`](bridge-ops-player.md#unlock)
unlocks every recipe for a test character.

### Build search: not yet proven

- Placing a result (the test character had no materials), a query that matches only locked items (the tab
  fallback), and the row's "new" marks with a search active.
- Physical keystrokes into the box: the OS-typing run was interrupted when someone else used the client.
- The settings switches with the menu open. (Closing with a search active and reopening unfiltered ran live through the bridge's `open` / `close` on 2026-10-04.)

The live check:

1. On the isolated test server, unlock everything for the test character (Dev `unlock`, action `all`), and rejoin.
2. Open the build menu on a tab and hover an item: the tooltip shows its breadcrumb.
3. Type the name of an item from another category. The row shows it, the count reads "1 found", `build_search status`
   reports `allTabs: true`. Hover it: the breadcrumb names its home tab.
4. Click it and place it: the placed building is the one hovered.
5. Switch tab: the results stay. Type a word only a group's items share: one group cell.
6. Click x: the selected tab is back with nothing selected, `allTabs: false`, and `blobCopies` drops to 0 within
   120 frames. A one-letter query filters the selected tab.
7. With every unlock reverted (`unlock revert`, rejoin), search for a locked item: the tab stays put and reads
   "0 of n".
8. Hot swap Core with results showing: one box, the original blob back (`allTabs: false` after the swap).

## Dye all

A built-in client feature, **on by default**, with no plugin API. In the game's color picker in
**Character > Equipment**, **hold** a color swatch: a ring fills over it, and when it completes every worn piece and
every transmog takes that color. A normal **click** works as it does in the game and colors only the selected piece.
The game's key-hint row at the bottom of the inventory says so: next to the game's own *[left mouse] Set Color*,
Core adds *Hold [left mouse] Set All*.

It was the sample `progenitor.dyeall` until 2026-10-08 (decided: "part of Core Progenitor, one of the small
quality-of-life features"). Client only: it sends the color event the game's own picker sends, so the server needs
nothing, and a server without Progenitor works the same.

Source: `src/Progenitor.Core/Dye/`. `DyeAllFeature.cs` (install, settings, the test key, the former-mod check, the dev bridge's `DyeAllTestBridge`),
`DyeAllClient.cs` (swatches, the hold, the apply), `DyeSender.cs` (reads and the send), `ChargeRing.cs` and
`HintLabel.cs` (the ring and the key tip). Game-free and unit-tested (`DyeAllTests`, `HoldMachineTests`): `DyeHold.cs` (the hold, the scan gate, `DyePickerSearch`),
`DyeSlots.cs` (slot and index planner), `DyeRing.cs` (palette-blob bounds), `DyeHint.cs` (the key tip's words, show rule,
donor choice and place in the row), `DyeAllSettings.cs` (entries, the old cfg import, the stand-down rule).

**Status.** As the sample, live-verified on 2026-10-02: holding a swatch for 1 s dyes every worn piece, a Live 2026-10-08 (Steam client on the isolated test server, Core 2026.10.116.2): the picker was found when Character > Equipment opened (palette hint drawn, charge ring shown), and three holds dyed every dyeable worn piece (`Dye all (hold): color 30, sent 2 of 2: boots, gloves; chest skipped (AlreadyThatColor), legs (Empty), cloak (NotDyeable)`); the old settings were copied once from `progenitor.dyeall.cfg`, which was left as it was.
click keeps the game's behavior, and (since the 2026-10-08 key-tip change) the game's key-hint row names the hold. The Core version is **built and game-free
tested, and live-verified on 2026-10-08**: the code is the sample's, with the context replaced by Core's logger and owner, the
settings moved to Core's cfg, the stand-down check added, and the search for the picker limited to while a HUD menu is open (new, so the live check must confirm the picker is found when Character > Equipment opens). The live steps are [below](#live-check-core-version-not-run).

**Fixed 2026-10-08: holds did nothing after the first generation.** The game has two pickers: the character menu's and
the interaction (chest, station) menu's each build their own inventory sub-menu with a `TransmogContainer`, and the
second exists only once such a menu was opened. Dye all kept the first container its search returned and never searched
again while that object lived. Once the interaction menu existed, a Core generation could keep its picker, which stays
inactive while Character > Equipment shows, so no swatch was watched, no line drawn and no hold possible, with nothing
logged. Live readback before the fix (generation 22): two containers, the held one under `ContainerMenu(Clone)` and
inactive, zero swatches watched, one search in the whole generation. Now every container is kept, the search runs again
(at most every two seconds) whenever none of the known ones is active, and every active one is listed
(`DyePickerSearch`, unit-tested). Live after the fix (generation 23, Steam client): the character menu's picker was
listed (42 cells watched), the old palette line showed, and the manual holds logged `Dye all (hold): color 20, sent 4 of 4` and
`color 6, sent 4 of 4`. A Core swap leaves no watch, trigger or line behind: after `core_reload` from generation 23
(42 cells watched, the line built) to 24, a scene read found no `progenitor-dyeall-hint` object and no `EventTrigger`
under either container. The bridge's [`dye`](bridge-ops-client.md#dye)
op reads all of this back.

**Changed 2026-10-08: the hint is a key tip in the game's own row.** The first hint was a gray line built under the
palette grid (`ColorSelectionParent`); in play it sat above the equipment slot row and over the slot tabs' arrows.
It is now a copy of the game's own key-hint entry, placed in the inventory menu's bottom key-hint row right after the
game's *[left mouse] Set Color*: *Hold [left mouse] Set All*, with the game's glyph, font (Nocturne Serif SDF, 18),
color and spacing (see the key tip row under [How it works](#how-it-works)). The words follow the game's hold form
(its console entries read *Hold [X] + [Q] Space*); *Hold* takes the caption's style, because the prefab draws its
interaction word heavier. **Live 2026-10-08** (Steam client, Core generation 27): with the character menu open on
Equipment and no picker shown, the entry was built through the same `Show` path from a test snippet. It took the row
`MotionRoot/InputContexts` (not the empty one inside the item tooltip, `MotionRoot/FakeTooltip/InformationEntries/InputContexts`,
which comes first in the hierarchy: the first build looked there and logged "no mouse entry"). It copied the game's
*Edit* entry (the shown mouse entry; *Set Color* was hidden because no picker was open) and drew *[left mouse] Edit  Hold [left mouse] Set All*
in the same font, size and color. Disposing it left no `progenitor-dyeall*` object. **Not yet observed:** the tip
beside *Set Color* with the picker open, in normal play. A bridge mouse click on the slot's tab did not open the
picker focus-free, so that step needs a person at the keyboard: open Character > Equipment, select a dyeable slot so the palette shows,
then read `dye` (`hintShown true`, `hintRow MotionRoot/InputContexts`) and capture the bottom row.

### Settings

Mods menu (F10) > **Progenitor** > the **Equipment** group. Stored as `[DyeAll]` in `BepInEx/config/progenitor.ui.cfg`,
Core's own client cfg, which is never server-synced.

| Settings page label | cfg key | Default | Meaning |
|---|---|---|---|
| Dye all on hold | `Enabled` | `true` | Off: swatches behave exactly as in the game. A live hold cancels, the key tip hides, every swatch watch is removed and nothing is searched for. |
| (cfg only) | `MatchPalettes` | `false` | Experimental, see [Indexes and palettes](#indexes-and-palettes). |
| (cfg only) | `LegacySettingsImported` | `false` | Set once the former mod's cfg was looked for. |

A hold is always 1 second, and a piece under a transmog is always dyed. **Also dye pieces under transmog** and **Hold time** were drawn on this page and then removed (2026-10-08): they are not settings, and an old `HoldSeconds` or `IncludeCovered` value is not read. Gear the player hid with the X cell is still left alone. See [Under a transmog](#under-a-transmog).

**Settings from the former mod.** On the first Core install with this feature, `BepInEx/config/progenitor.dyeall.cfg`
is read once if it exists: `[Dye all] Enabled` and `MatchPalettes`
are copied into `[DyeAll]`, and `LegacySettingsImported` is set whether or not the file was there. The old
file is only read, never written, moved or deleted. Its `HoldSeconds`, `IncludeCovered`, `[Test] ColorIndex` and `[Keybinds] DyeAllTestColor` are not copied.

There is no dye key. The hold is a pointer press on a swatch (`Ui.WatchPointer`). **Developer: dye all with the test color** was a Core key and was removed (2026-10-08).

### The former Dye all mod

A player may still have the old `progenitor.dyeall` installed. Core's copy **stands down** while that mod runs in the
process: it watches no swatch, shows no ring or key tip and sends nothing, so nothing is dyed twice, and logs
`Dye all: the former Dye all mod (progenitor.dyeall) is running; Progenitor's copy stands down …` once. "Runs" is
Progenitor's mod registry (loaded or loading; a copy the player turned off does not count), or, when the registry has
not seen the mod (a chainloader copy right after a Core swap), the chainloader's plugin list. The check runs four
times a second, so turning the old mod off or uninstalling it hands over within a quarter second, with the log line
`… is not running; Progenitor's copy is active.` The old mod's page in the Mods menu starts its provenance block with
*Now part of Progenitor; you can uninstall this mod.*

Why stand down rather than a shim: an installed old copy is the player's file, and only an update could replace it
with a shim. The old mod ran live on 2026-10-02 with the APIs it uses (`Ui.WatchPointer`, `HoldMachine`, `HoldRing`),
and they are unchanged, so standing down never runs two copies. What is **not** proven:

- That the old mod works on this Core at all: no run with the old mod and this Core has been made.
- A chainloader copy (`BepInEx/plugins`) after a Core swap. The chainloader still lists it, so Core's copy stands
  down, but the old copy's watches were made through the previous Core generation and are not re-made by the swap.
  Until the game restarts, possibly neither copy dyes. The usual install is the hot-loaded `progenitor-reload`
  copy, which the swap reloads.
- Between a Core swap and a hot-loaded old copy's reload, only Core's copy runs (a few frames; not observed).

Uninstalling the old mod (or turning it off on its page) is the supported state.

### How it works

| Step | What Dye all does | Game evidence |
|---|---|---|
| Find the swatches | `TransmogContainer` holds the grid of `TransmogColorGridSelectionEntry` cells: a fixed pool of 32 (the X hide/show cell, one cell per color option, then empty cells). There are two containers once an interaction menu was opened (`CharacterMenu(Clone)` and `ContainerMenu(Clone)`, each under its own `CharacterInventorySubMenu`). A scene search that includes inactive objects finds them all; it runs again, at most every two seconds, whenever none of the known ones is active. The cells of every active container are listed four times a second and each new cell is watched. Both run only while a HUD menu is open (`OpenHUDMenuSystem.CurrentMenuType` is not `None`; the picker sits in the inventory sub-menu the character and interaction menus share), never on the title screen or while loading; an unreadable menu type falls back to scanning, logged once. | `TransmogContainer`; `OnUpdateFromParent` fills the list and pads it to 0x20 |
| Watch a press | `Ui.WatchPointer` adds PointerDown, PointerUp and PointerExit rows to the cell's `EventTrigger` (and its button's, when that is a different object), routed through Bootstrap's trampoline. Only a primary (left) press counts. | `GridSelectionEntry` (client dump 390030) handles PointerDown on the cell itself |
| Read the swatch | At press time: `EntryData.EntryType` (only `Normal` = 1 is a color), `Index`, and the selected piece from `EquipmentSlot.SlotEntity`. | `TransmogColorGridSelectionEntry.Data` (client dump 391515): EntryType @0x0, Index @0x10, EquipmentSlot @0x14 |
| The game's click | Untouched. `TransmogContainer.HandleInput` sends the color for the hovered cell on the press frame, so a hold has already colored the selected piece when the ring starts. | `HandleInput` |
| The ring | Core's `HoldRing` and `HoldMachine` over the held cell, raycasts off. It fills over the hold time, then pulses (0.3 s). Sprite: the cell's selected-state sprite when it is named like a ring or circle (borrowed), otherwise a drawn ring. The log says which. | `SelectedStateNode` @0xE0 |
| The key tip | A copy of the game's own *Set Color* entry in the inventory sub-menu's key-hint row (`MotionRoot/InputContexts`, an `InputContextsCollection` whose `ContextsParent` is a `HorizontalLayoutGroup` of pooled `InputContextEntry` objects). The copy is made under an inactive holder; its `InputContextEntry` and every `LocalizedText` are destroyed and every other behaviour but the text, image and layout ones is disabled. `InteractionText` reads *Hold* (a hold is always 1 s, so it never names a time), the mouse glyph stays, the key text is off and `ActionText` reads *Set All*. It sits right after the *Set Color* entry, so the row's layout places it, and shows while the feature is on and the palette shows a color cell. Donor: the shown *Set Color* entry, else a shown entry with the left mouse glyph (another language), else a hidden one; none: no tip, logged once. | `InputContextEntry` (`KeybindImage`, `KeybindText`, `ActionText`, `InteractionText`); `InputContextsCollection.ContextsParent` |
| Cancel | Releasing early, moving off the swatch, the panel closing (or the cell hiding, or its data changing), switching the feature off, standing down or Core uninstall cancels with nothing sent. | |
| Apply | Reads the local character's `Equipment`, picks the pieces, and sends one `ChangeTransmogColorEvent` per piece through the client-to-server event recipe (event + `NetworkEventType` + `SendNetworkEventTag`, fields written at the offsets `Progenitor.Layout` declares, which the interop struct must match). No game method is called. | `ChangeTransmogColorEvent` (client dump 156747), EventId `-695231661` |

**Which pieces.** The picker serves chest, legs, boots, gloves and cloak. For each, the cosmetic (transmog) piece when
one is equipped, sent with the cosmetic equipment type (12 to 16), otherwise the stat piece, and the stat piece under a transmog too ([Under a transmog](#under-a-transmog)). A piece is skipped with no `TransmogData`, zero
colors, the index already worn, or an index at or past its `NumColorOptions`. Headgear, weapon, magic source and bag are
never colored.

### Under a transmog

Added 2026-10-08 (decided: Dye all also dyes the pieces hidden under transmog). **Status: source-checked against
the server metadata and native code, built, game-free tested (`DyeAllTests`) and live-verified on 2026-10-08 except recipe
step 4 (whether the gear shows the color at once when the transmog comes off) and the hidden cases ([live
results](#under-a-transmog-live-results)).**

**How the game stores it.** The color lives on each **item entity**: `TransmogData.ActiveTransmogIndex` (with
`NumColorOptions`). A transmog is a separate item in the matching cosmetic slot of the character's `Equipment`
(`ChestCosmeticSlot` and so on) with its own `TransmogData`, so the gear under it keeps its own color. Each
`EquipmentSlot` also caches the shown palette key (`TransmogKey`, `TransmogIndex`) and the X cell's `HideEquipment`.
Equipping an item rebuilds its slot key from the item's `TransmogData` (`Equipment.SetEquipped`).

**What the event does under a transmog.** The server's handler stores the new index on the item the event names, then
updates the slot key (`TransmogEventsSystem.UpdateTransmogColorForEquipmentSlot`). That write picks
the slot by what is **shown**: for a stat type or its cosmetic type alike, it writes the cosmetic slot whenever a
transmog is equipped, with the key looked up in the palettes of the item the event named, and clears that event type's
`HideEquipment`. So the game's own event, sent for the stat piece under a transmog, does color the real gear, but also
re-keys the transmog's shown color from the gear's palettes.

**What Dye all sends.** For each covered slot, the stat piece first and the transmog right after it, so the transmog's
own event is the last word on what is shown:

| Transmog in that slot | Sent |
|---|---|
| Takes the new color | stat piece → new color, then transmog → new color |
| Keeps its color (already that color, index past its colors, palette missing) | stat piece → new color, then transmog → its own current index (logged as `… (transmog) kept at N`) |
| Has no colors, or keeps its color while its model is hidden with the X cell (a restore would show it) | nothing for the stat piece (`TransmogWouldChange`); the transmog as above |
| Stat piece already that color, or skipped for its own reason (palette missing, index past its colors) | the transmog only, as without the setting |
| Stat piece hidden with the X cell (its event would clear the hide flag and show it when the transmog comes off) | nothing for the stat piece (`CoveredHidden`); the transmog as above |
| Transmog equipped but its entity still waits for sync on the client | nothing for the slot (`TransmogNotSynced`); the next dye-all catches up |

"Covered" is the server's own test: the transmog slot's `SlotId` is nonzero, which is true before the client can use
the transmog's entity. Dye all always sends this plan. There is no setting that dyes only the shown piece.

**Event order.** Confirmed: the server handles color events one at a time, re-reading `Equipment` for each. **Not
verified**: that one client's events are handled in the order they were sent. The plan depends on it (the stat
event before the transmog's); step 3 of the recipe below checks the outcome.

**Not proven.** That a removed transmog shows the new color at once: the gear's color is stored on the item, and the
stat slot's cached key is rebuilt when the item is equipped, but no event rewrites that key while a transmog covers it,
so the gear may show its old color until it is re-equipped. The live recipe below checks both.

<a id="under-a-transmog-live-results"></a>**Live results (2026-10-08, Steam client on the isolated test server,
Core from this branch).** The character wore `Item_Chest_T09_Dracula_Scholar` as the chest with
`Item_Chest_T09_Dracula_Warrior` as its transmog (equipped through the game's `EquipItemEvent`, cosmetic for the
transmog); both started at index 0 of 31.

- **Setting on, color 7:** `chest->7, chest (transmog)->7` (the stat piece first). Both items' `ActiveTransmogIndex` read
  7, and `ChestCosmeticSlot` read index 7. Both chests carry the same palette key, so this run cannot show whether the
  events were handled out of order. The covered `ArmorChestSlot` kept its cached index 0, as expected while covered.
- **Setting off, color 12:** only `chest (transmog)->12` was sent. The gear stayed 7 and the transmog read 12.
- **Setting on, the transmog's own color 12:** `chest->12, chest (transmog) kept at 12`. Both items read 12 and
  `ChestCosmeticSlot` read index 12 (the restore re-keyed it).
- **Not run:** step 4 (taking the transmog off needs the game in front), a hidden stat piece or hidden transmog, and a
  transmog waiting for sync.

**Live recipe.** Isolated test server and Steam client only. Wear a dyeable chest with a dyeable chest
transmog over it (and ideally one slot without a transmog). There is no dye key; the check is a hold on a swatch, as in
the [live check](#live-check-core-version-not-run).

1. Read the starting colors on the server: `get` the character's `Equipment` (note the `ArmorChestSlot` and
   `ChestCosmeticSlot` entities and `TransmogIndex`) and `get` `TransmogData` on both items.
2. In Character > Equipment, hold a swatch neither piece wears. Client log: `Dye all (hold): color N, sent …: chest->N,
   chest (transmog)->N, …` (the chest before its transmog).
3. Read back: both items' `TransmogData.ActiveTransmogIndex` is N. On the character's `Equipment`,
   `ChestCosmeticSlot.TransmogIndex` is N and `ChestCosmeticSlot.TransmogKey` is the transmog's own palette key for N
   (compare with the transmog item's `TransmogAssetKeyBlobData` rows), not the chest's. A chest-palette key here means
   the events were handled out of order. Capture the character: the transmog shows the swatch color.
4. Unequip the chest transmog in game (foreground, a fresh yes) and capture: record whether the chest shows color N at
   once or only after re-equipping it. Re-read `ArmorChestSlot.TransmogIndex`.
5. Put the transmog back and hold the swatch it already wears: the log shows `chest (transmog) kept at …` and a capture
   shows the transmog unchanged.
6. Leave the client at the title screen. Covered dyeing is not a setting, so there is no off step.

**The server.** `TransmogEventsSystem` requires the event's character to be the sender, the item to
have `TransmogData` and be equipped in that slot, and `NewIndex < NumColorOptions`; there is no rate limit and no
per-color unlock check. One dye-all is at most ten events in one frame. **Nothing new is needed on the server**: it is
the game's own event, which the game's server accepts with or without Progenitor.

### Indexes and palettes

An index names a color inside one piece's option
list, built from the piece's `TransmogAssetKey` rows. Where pieces carry the same palettes in the same order, the same
index is the same color; that is what is sent by default. `MatchPalettes` reads each piece's palette rows from its
`TransmogAssetKeyBlobData` blob (bounded by the blob header's length; offsets from `Progenitor.Layout`) and maps the
color through them. It has not been run live, so it stays off.

**Known limits.** A press before the picker is found is a plain click: up to two seconds after the first open of a menu in a session (the search waits two seconds between misses), a quarter second after later opens. While a menu is open but no known picker shows (another tab), the scene search runs every two seconds. A watched cell no longer
passes unhandled scroll events to its parent. Releasing any mouse button over a held swatch cancels. A controller
confirm colors the selected piece as usual. Items whose PrefabGUID differs from the slot's `SlotId` fail the server's
equipped check, as they do for the game's picker.

### Live check (Core version, not run)

Steam client on the isolated test server, never the live server. Check the client is free (not joining, not in use)
before a Core hot swap, and that no old `Progenitor.DyeAll.dll` is left in the client's `BepInEx/progenitor-reload`
(or deliberately leave one for step 4). Wear at least two dyeable pieces, ideally one with a transmog over it.

1. Deploy Core to the client (`./tools/deploy.ps1 -Projects Progenitor.Core -Side client -NoBuild` after a Debug build).
   Client log: `Dye all: no progenitor.dyeall.cfg …` or `Dye all: copied N setting(s) …`, then
   `Dye all: watching the equipment color picker (hold 1.0 s, on).`
2. Settings, focus-free: `mods_ui {"action":"open","site":"standalone"}`, `{"action":"select","plugin":"progenitor","page":"overview"}`,
   `{"action":"widget_read","pane":"content"}`. Expect the **Equipment** `page-section` between Build menu and Chat,
   with `progenitor/DyeAll/Enabled` (toggle, **Dye all on hold**) at `home progenitor-page/equipment`, and no
   `IncludeCovered`, `HoldSeconds` or `DyeAllTestColor` row.
3. The gesture (foreground, needs a fresh yes): open Character > Equipment, select a dyeable slot. Within two seconds
   of the first open (a quarter second on later opens) the key-hint row at the bottom reads
   *[left mouse] Set Color  Hold [left mouse] Set All*. A click colors only the selected piece. A 1 s hold fills the ring, pulses, logs
   `Dye all (hold): color M, sent …`, and every worn piece takes the color. Release early: `Dye all: hold canceled (Released).`
   Read back on the server with `get` on each worn item's `TransmogData`.
4. Former mod (optional): with an old `Progenitor.DyeAll.dll` in the reload folder, the log shows the stand-down line,
   only one ring and one key tip appear, and its Mods page reads *Now part of Progenitor; you can uninstall this mod.*
   Turn it off on its page: `… Progenitor's copy is active.` within a quarter second.
5. Remove any old Dye all DLL you placed, disconnect and leave the client at the title screen.

## Ui

Client-only first cut of M3.1. Full API: [ui.md](ui.md); how to use it: [UI style guide](ui-style.md). Skip token: `HarmonySlotHandoff.UiModule` (`"Ui"`).

`Progenitor.Ui.Ui` is a static factory, not a `ctx` property: a `UnityAction` whose method lives in collectible Core pins the ALC, so Bootstrap owns the listeners and Core fills a ushort-keyed slot. Server calls return a disabled handle. Every GameObject lives under `progenitor-ui-<pluginId>`; unload and `RemoveAll` destroy that exact root. Each panel owns its overlay canvas (`Canvas` + `GraphicRaycaster`); `Dispose` / Close destroys that canvas with the panel so a second panel on the same root keeps its own.

```csharp
using Progenitor.Ui;

IUiPanel panel = Ui.Panel("Sample", owner: ctx.Owner, pluginId: ctx.Guid);
var log = panel.Button("Log click");
log.OnClick(() => ctx.Log.LogInfo("clicked"));
Ui.Show(panel);
// later:
Ui.Hide(panel);
Ui.RemoveAll(ctx.Owner);
```

`Panel` clones `PassiveFrame_Normal` onto a sorting-5000 overlay. `Button` clones a vanilla `SimpleStunButton`; `Toggle` / `Slider` / `Input` clone a vanilla widget when a suitable one is live and build one from the game's font, frame sprite and palette when it is not — see **Rows a player can operate** below. `Label` is text. `OnClick` / `OnChanged` go through Bootstrap `UiSlots`. `Show` unlocks the cursor; Escape / `ButtonInputAction.Back` hides, and since FUI20 it only hides — see **Escape closes the panel and nothing else** below. That hide reaches your panel and not your module, so hold the handle in an `OwnedPanel` and derive "am I open" from it — see **Keep your panel in an `OwnedPanel`** below.

### Rows a player can operate (FUI24, evidence 176)

**The settings panel was display-only.** Not one toggle could be clicked, not one slider dragged, not one field typed into. It listed every keybind and every editable config entry of every loaded plugin, read all their values correctly, and offered no way to change any of them. `AddToggle`, `AddSlider` and `AddInput` each created **one bare component** and shifted their caption sideways — `+80` to leave room for a checkbox, `−140` for a slider or a field — to make space for a control that was never built. A grep for `targetGraphic` and `handleRect` over the whole runtime found them only in `MakeScrollbar`. Those offsets were the only trace on screen that a control had ever been intended, and they are the "ragged alignment" the player reported: rows indented around holes.

**Every instrument said yes.** `AddComponent<Toggle>()` is a legitimate call that returns a live component, so nothing threw and nothing was logged. The handles were enabled and `BoolValue` read `Toggle.isOn` correctly. `TryWire` found `onValueChanged` and registered it, so the whole event path was correct, tested, and unreachable. `Ui: panel 'Progenitor Settings' laid out rows=47 …` is a true sentence about a panel on which nothing can be pressed. **A widget is not the component; it is the component plus the objects it draws through** — and that fact is not a return value anywhere, so it has to be asserted as a reference.

| Control | What is built, and what makes it operable |
|---|---|
| `Toggle` | a 22 px box `Image` (`targetGraphic` — the click target, because a `Toggle` owns no graphic of its own and the pointer reaches it only through a raycastable `Graphic` in its hierarchy) with a mark `Image` inside it (`graphic` — the alpha `Toggle.PlayEffect` drives) |
| `Slider` | a raycastable track (so a click anywhere on the bar moves the value, not only one that lands on the handle), a fill and a handle each inside a parent inset by half a handle so the travel stops where the track does (`fillRect`, `handleRect`, `targetGraphic`), and an explicit `direction` |
| `TMP_InputField` | a background `Image` on the field itself (`targetGraphic` — without it the field is not a raycast target and can never become the `EventSystem`'s selected object, so it cannot be focused at all), a `RectMask2D` viewport (`textViewport`), and a TMP child in the game's font (`textComponent`, where accepted characters are written) |

That last one had a quiet second casualty: `UnityKeyInputSource.TextFieldFocused`, the guard that stops a mod keybind firing while the player is typing (R35/R38), asks the `EventSystem` for its selected object and looks for a `TMP_InputField` on it. It was correct code protecting a case that could not occur.

**Two build-order traps, and they matter as much as the references.** (1) A component added to an already-active `GameObject` runs `OnEnable` immediately — and `Toggle` applies its checkmark's alpha there, `TMP_InputField` creates its caret and selection mesh there — so every control is built **inactive** and activated exactly once at the end. (2) `Instantiate` remaps only references inside the copied subtree, so a clone keeps the game's *persistent* (inspector-wired) `UnityEvent` calls, whose targets are outside the clone and therefore still point at the game's own objects: a cloned checkbox would have driven the vanilla setting it came from. Persistent listeners survive `RemoveAllListeners` and are switched off one at a time with `SetPersistentListenerState(i, UnityEventCallState.Off)` — now done to every clone, **the `SimpleStunButton` button clone included**, which had the same defect and had had it longer. A cloned toggle also arrives still enrolled in the vanilla `ToggleGroup` its reference points at, and is un-enrolled first.

**Clone first, build second, and say which.** The module's rule since evidence 88 is to borrow vanilla pieces rather than ship art, and the obvious donors are the settings screens' `SettingsEntry_*` widgets. Whether any of them is instantiated in the scene *while the player is in the world* is **unknown** and is not assumed: `OptionsMenu` is a view, and evidence 88's live sweep found nine `SimpleStunButton`s and no `MenuButton`s. So the runtime looks for a donor of the exact type that is not ours, is widget-sized (fits the control column, at most a dozen transforms — a donor is meant to be a checkbox, not a whole settings row carrying somebody else's caption), and passes the operability predicate; **the predicate is then re-run on the clone**, which is destroyed if it fails. Each control type logs its path once, so the next live run answers the donor question from a log line instead of from a guess:

```
Ui: settings toggle source=built (no live vanilla toggle was both widget-sized and operable, so one was built)
```

**A control that cannot be made operable is destroyed, not left as an ornament**, and the row reverts to a full-width label — which is exactly what a read-only row (a server-synced setting, another mod's entry) has always been.

**One column rule.** `PanelLayout.Columns(hasControl)` returns the caption's width and x and the control's x from the single fact of whether the row has a control, and `FitLabel` re-applies it on every layout pass. So "is this row indented?" and "does this row have a control?" cannot be decided separately and disagree — patching the offsets alone would have produced neat rows on a panel that still did nothing. The two are side-by-side columns, never a control offset from a caption, because a caption here is an author-written config description that wraps to ten lines; columns cannot collide however tall the row gets, and the control is pinned to the row's **first line** so it stays beside the caption's opening line instead of drifting to the middle of a paragraph. Row width went 400 → 560 to take back the slack in the 600-wide viewport, since a row now has to hold a caption *and* a control column.

### Where a panel is drawn (FUI20, FUI21)

**Two open panels used to be one panel.** `CreatePanel` placed every frame with `PlaceRect(panelGo, Vector2.zero, …)` — dead center, always — so opening the sample's demo panel (F9) and its log panel (Ctrl+F9) together drew both in exactly the same rectangle, titles and rows interleaved into something unreadable. One panel looked right, and one panel is every screenshot the framework had produced. For a framework whose premise is that several plugins each open their own panels, that is a defect and not a cosmetic one.

**FUI20's cascade did not fix it, and a cascade could not.** Stepping each further panel 40 units down and right worked as a mechanism — the log showed `slot=0 at=(0,0)` and `slot=1 at=(40,-40)` — and the screenshot was still unreadable: the frame is 640 wide, so a 40-unit step leaves two panels more than 90% on top of each other. Cascading is a desktop-windowing idiom, and it is only tolerable because the user can drag the top window off the one beneath it. **These panels cannot be dragged at all** — no title-bar drag, no move affordance, no z-order control — so an overlap the framework hands the player is one they cannot resolve. The step was not the thing to tune.

**Tile if they fit; cascade only when they do not (FUI21).** Every panel is exactly `PanelWidth` (640) wide — the frame's width is a constant, only its height is content-derived — so how many frames fit side by side is a question about the canvas alone. `PanelFlow.Columns(canvasW, panelW)` answers it, and `PanelFlow.Offset(spot, …)` lays the shown panels out in that many columns, the occupied columns centered as a group and each panel centered vertically in its own. On the live client (~1638 canvas units wide) that is two columns, so the two sample panels sit at x = ∓332 with a 24-unit gap and **no overlap at all**. Only when there are more panels than columns does the surplus share a column, and then it steps down by a whole `PanelLayout.HeaderHeight` (72) while the canvas has room for a step that size, so the panel underneath still shows its own title band — the only thing that says it is there and which panel it is.

**A panel is placed by its rank in the shown set, not by a slot id.** Where the first panel goes is a fact about how many are open (one is centered, two straddle the center), which a slot-indexed cascade cannot express. `Ui.ReflowPanels` — the one layer that holds every live handle, and therefore the only one that can count the set — orders the shown panels by slot and hands each a `PanelSpot(rank, count)`. The slot survives as the **ordering**: `CascadeSlots` still hands out the lowest free index, so the longest-open panel keeps the leftmost column and a panel opening beside it never makes it swap sides.

**What moves, and when.** The layout is a pure function of the shown set, so opening or closing a panel re-flows all of them; two side-by-side panels become one centered panel when one closes, rather than leaving the survivor parked in a column with an empty column beside it. This deliberately reverses FUI20's "closing the panel above does not slide the one below". The guarantee that replaces it is narrower and is the one that protects the player: **a re-layout caused by *using* a panel never moves anything.** `UnityPanel.Relayout` — the pass that runs when a row's text changes under the player's own click (the rebind flow, R43 finding 4) — recomputes the offset from the same rank, the same count **and the same canvas**, and lands on the same position by construction. Only Show and Hide change the set, and those are the player deliberately opening or closing something.

**Those three are the whole input list, and that took a fix (FUI23).** The guarantee above was first written from rank and count while `PanelFlow.Offset` took a fourth input a re-layout is allowed to change: the panel's own height. `DepthCapacity` fell as a panel grew and the vertical offset was `depth mod (capacity + 1)`, so a panel at depth ≥ 1 sat at −72 while it had room and snapped to 0 — onto the title band of the panel above it — the moment it did not. Reachable through the same rebind flow the guarantee cites: the refusal notice appends a sentence, the row grows, the panel re-lays out taller, and it jumps 72 units under the click the player is still making (R44 finding 5). `Offset` no longer takes a height at all, so the guarantee is a fact about the signature rather than a thing to remember, and `PanelFlowTests.A_panel_that_grows_under_the_player_does_not_move` asserts the parameter list.

**Vertical tiling is not the fallback, on purpose.** Stacking panels one above another would also avoid overlap and would need every panel's height to place any of them — so a panel that re-lays itself out taller would shove its neighbors around while the player is reading them. Column placement depends only on the constant width and the count, so a panel's own re-layout can never move a different panel. No panel's height enters the placement at all: how far a panel sharing a column may step is bounded by the tallest height this *canvas* permits, which is a fact about the canvas.

**Nothing goes off-screen, by construction rather than by a clamp.** A clamp would put every over-far panel back in the same place, which is the original defect. `Columns` only ever returns a count whose whole group fits inside `canvas − 2·EdgeMargin`, and the vertical ladder is bounded by `PanelFlow.CascadeRoom` — `(canvas − cap) / 2 − EdgeMargin`, where `cap` is the **tallest panel this canvas permits** (`PanelLayout.PanelHeightFor`'s 80% ceiling) rather than the panel being placed. Measuring it from the individual panel is what made the offset move when the panel grew (finding 5 above); measuring it from the cap makes it one ladder for every panel on the canvas, and it is still safe in the direction that matters, because a real panel is never taller than the cap the ladder was sized for. A canvas with no room at all steps nothing down; a panel wider than its canvas is centered sideways, which is the only honest answer for a frame that will overlap whatever else is open wherever it is put.

**The ladder reaches every panel that has to stand on it, and that cost a fix (FUI25).** The first canvas-wide ladder kept FUI21's `depth mod (capacity + 1)` wrap and replaced only the capacity, and on this client that capacity is **one**: 1080 units, an 864 cap, 92 units of room, one whole 72-unit band. So the wrap period was two, and with two columns the **fifth** open panel is column 0 depth 2 — `2 mod 2 == 0` — drawn at exactly the offset of the first. Two 640-wide frames dead on top of each other, which is worse than the 90% overlap FUI21 was written to fix, because the covered panel shows nothing at all. Four places said this trade "shows only on a canvas too narrow for two columns"; 1638 has two columns and three panels share column 0 the moment five are open, so the sentence was false in the one configuration anybody runs. `PanelFlow.CascadeStepFor(canvasH, deepest)` now sizes the step from the column's own occupancy: a whole title band while the canvas affords one at that depth, `room / deepest` once it does not, so the deepest panel lands exactly on the margin and **no two panels in a column ever share a place**. **The property given up is that the step is always a whole title band** — five panels put column 0 on a 46-unit step and nine put it on 23 — and it is given up deliberately: FUI20's "a bounded repeat beats an unreadable step" is right about a fixed step chosen once for every canvas, and a repeat's collision is not a small step but a step of zero, the one value at which the player cannot tell the panel is open at all. The step depends on how many panels share the column, so opening a panel re-spaces the column it lands in — and that is **not** carried along by a horizontal re-flow, which is what this sentence used to claim (R46 finding 2). `occupied` is `min(count, columns)`, so on this canvas every panel's x is settled once two are open: at four panels rank 2 sits at y = − 72, at five it sits at − 46, and **nothing moves sideways at all**. 26 units is more than half a row pitch (40 + 8), so a resting cursor can end up over a different row. It is kept, and the reason is not that a Show is a different category from a re-layout — what protects the player is whether the move is caused by the input currently aimed at the thing that moves, which is what FUI23's rebind-row jump was and this is not, and the 26 is against the 332 units a panel moves sideways when one panel becomes two. It is also not an implementation choice: equal rungs that reach the margin cannot also stay still when a panel joins the column, and the churn begins at exactly the depth where the ladder FUI25 replaced drew two panels in one place. `PanelFlowTests.Opening_the_fifth_panel_re_spaces_column_zero_and_moves_nothing_sideways` and `The_only_panels_a_new_panel_moves_are_the_ones_sharing_its_column` pin both halves. A re-layout caused by *using* a panel leaves the set alone and therefore still lands on the same step.

**It is recomputed in the pass that sizes the frame, and it has to be.** The panel is resized to its content on Show (evidence 163), so how far a panel sharing a column may step is not known when the panel is created: a 47-row settings panel capped at 80% of the canvas has almost no vertical room while a four-row demo panel has plenty. `UnityPanel.RunLayout` therefore places the frame in the same pass that sets its `sizeDelta`, and the `Ui: panel '…' laid out …` line carries `rank=r/n` and `at=(x,y)` because placement is only ever visible in a screenshot — which is how two panels sat on top of each other through every log the framework had. The re-flow of the *other* open panels goes through `UnityPanel.Reposition`, which moves the frame from the size it already has and never re-measures a row: re-running a full layout on every open panel to answer a question about arithmetic on the frame would be paying thirty row measurements per panel for a number that does not depend on them.

**Why not a position argument on the API.** A `Ui.Panel(title, x, y)` overload pushes the problem onto the one party that cannot solve it: a plugin author knows where *their* panel should go and has no idea what other mods have open. Two plugins that each pick "top left" collide exactly as hard as two that each pick the center, and the default — which is what the sample hit — would still have to be something. The framework is the only layer that can see every shown panel, so the framework places them; a fixed-position API is a later, additive thing, not a prerequisite for two panels being legible.

### Keep your panel in an `OwnedPanel` (FUI29)

Escape hides your panel and **does not tell you**. `Ui.OnBootstrapKey` hides every shown panel it can see; it does not know which module owns any of them. `Ui.Uninstall` and `Ui.RemoveAll(owner)` dispose them the same way. So a module that keeps `private static IUiPanel? _panel` and answers `_panel != null` says "open" about a panel that left the screen — for the rest of the process. That was measured live on two of this framework's own panels, and it made the sample's toggle eat one press after every Escape.

There is no `Hidden` event to subscribe to, deliberately: a notification is a second representation of a fact the handle already holds, and a second representation is a thing that can be missed — which is the failure itself, one layer up. Derive instead.

```csharp
private static readonly OwnedPanel Owned = new();

public static bool IsOpen => Owned.IsOpen;          // a read of IUiHandle.State, never cached

public static void Open()
{
    Close();
    var panel = Ui.Panel("My panel", owner, pluginId);
    Owned.Adopt(panel);                              // hands back anything it replaced, for you to dispose
    // ... build rows ...
    Ui.Show(panel);
}

public static void Close()
{
    var panel = Owned.Take();                        // releases whatever is held, shown or not
    if (panel == null) return;
    panel.Dispose();
}
```

`IsOpen` is true for `Shown` and for `Disabled` — the server, a skipped module or a failed overlay, where there is nothing to show and re-opening on every key press would only churn. `Built` (shown, then hidden by something) and `Disposed` are both closed. `Live` is the panel while `IsOpen`, and is what a row refresh should go through so a caption rewrite cannot land on a panel the player already dismissed.

Neither `IsOpen` nor `Live` has side effects: they never dispose the stale handle they find, because a getter reached from a diagnostic must not destroy a panel. The stale handle is released by `Take`, which your `Close` runs and which your `Open` reaches through it. One escaped panel per owner therefore stays alive but hidden until you next open, sweep or unload — which is what it did before, minus the wrong answer.

### Widgets and the HUD (P7)

**Experimental: source-checked, built and game-free tested; not verified live.** Full contract and layout rules: [ui.md "Widgets"](ui.md#widgets-p7) and [ui.md "HUD"](ui.md#hud-notifications-locate-and-pick-in-world).

- **Panel widgets** are `UiWidgets` extension methods on any `IUiPanel`: `OrderedList` (drag, up/down, expand, remove; `OnReorder` / `OnRemove`), `Picker` (search with autofill, sections, icons, grayed rows with a reason, single or multi), `Tabs` (with count badges and per-tab content), `CodeArea` (line numbers, caller colors, line errors) and `Tooltip` on any kit control. They are made of the kit's cell rows and are disposed with their panel, so they need no ownership code of their own.
- **`ctx.Hud`** (`PluginHud`, client only, tracked on every unload reason): `Notify` (the game's own announcement, queued), `Locate` (a marker and an edge arrow on a world entity or point), `PickInWorld` (the next interact on a container or station, its panel kept closed, Escape cancels).
- **Seams.** Pick in world uses a Core-owned `Before<OpenHUDMenuSystem>` system hook that exists only while a pick is armed; no Harmony patch and no `ctx.UnsafeHarmony`. The list's drag and the tooltips use owned `EventTrigger` rows through the Bootstrap trampoline, like `Ui.WatchPointer`. No widget reads a key: Escape for a pick comes through the framework's consumed-Escape path. Process-wide widget state is torn down on Core unload through `UiWidgetLifetime`.
- **Sample:** the Controls sample's **Widgets** page ([`WidgetsDemo.cs`](../samples/Progenitor.ControlsSample/WidgetsDemo.cs)).

### Input capture (FUI2)
- **No game call runs under the module lock.** `UiInputCapture.Gate` protects module state — the refcount, the enabled flag, the retry budget, the pump subscription — and nothing else, the same rule `PatchRegistry.RemoveWhere` follows when it removes under its lock and unpatches outside it. Each pass is three steps: **decide** under `Gate` (register / remove / post to the main thread / nothing, from module state only, using a mirror of the last observed registration rather than a live read); **act** with no lock held, on the main thread, behind a separate `CallGate` that only ever serializes the calls themselves; **fold** the result back under `Gate`.
- **The lock order is one-directional, and only one direction is true.** `CallGate` is **never** acquired while `Gate` is held — `Apply` releases `Gate` before `Execute`, and `Uninstall` closes its `Gate` block before it takes `CallGate`. The reverse is *not* forbidden and cannot be: a blocker that re-enters `Acquire` / `Release` from inside its own `Register` takes `Gate` underneath a held `CallGate`, which is the path the module documents as expected. No cycle can form, because the order is never reversed. The comment used to claim the symmetric rule ("`Gate` is never taken while `CallGate` is held"), which is false in that direction and is the kind of stronger-than-true invariant someone later "fixes" by moving a lock (R33 finding 12). `A_blocker_that_re_enters_the_capture_from_another_thread_during_Register_does_not_deadlock` stages the cycle for real — a second thread calling `Acquire` from inside `Register`, joined with a 5 s bound — and fails if the game call is ever taken back under `Gate`. The older same-thread test could not: `Gate` is a `Monitor`, so a same-thread re-entrant `Acquire` never blocks, and that test passed identically against the pre-FUI4 code (R33 test-quality table). It is kept, renamed for what it does pin — the `_inFlight` guard, which stops a second concurrent game call. `Uninstall` follows the same shape: it resets state and swaps the blocker field to `NullUiInputBlocker` under `Gate`, then makes its `IsRegistered` / `Remove` / `Dispose` calls after releasing it. A blocker that pumps, waits, or re-enters `Acquire` from inside `Register` therefore meets a free lock instead of deadlocking, and `Ui.IsCapturingInput` / `Ui.DescribePins` no longer reach into `InputActionSystem` from whatever thread asked.
- **The fold re-checks that the decision is still current.** Before believing a result, the fold compares the blocker the decision was made against with the live one, and the `wanted` it was decided from with the current one. A blocker swapped out mid-call (teardown, a new Core generation) has its result discarded. A panel closed while a register was in flight logs `Ui: input context register order=599 was superseded while the game call was in flight (the panel count changed); …` and chains one more pass, which issues the follow-up remove — so a register that landed for a panel that is already gone is never left in the list. Superseded passes spend no retry budget, and the chain is bounded (8 passes) so a blocker that flips the refcount from inside its own `Register` cannot spin forever.
- **Exhausting the chain stops the spin without giving up the state.** The bound used to stop in whatever state the eighth pass left, with nothing pending and no pump armed — and the eighth pass can leave the context *registered with the count back at zero*, sitting in `_InputContexts` with nobody wanting it and nothing scheduled to take it out: the very leak the chain exists to prevent, reached by the bound that protects it (R33 finding 11). Exhaustion now re-checks wanted against registered and, on a mismatch, sets pending and arms the pump, so one ordinary next-frame pass reconciles them; the warning says which of the two happened. The give-up latch is the one case left unarmed — it suppresses register decisions on purpose and logs its own recovery line, so re-arming would only spin the pump. `Chain_exhaustion_arms_a_pump_that_reconciles_the_context_with_what_is_wanted` drives a fake that churns the count on every game call for exactly eight calls and then behaves, and asserts the final state matches wanted.
- **Giving up hands the budget back.** The give-up after 5 refused registers is a latch, not a pinned attempt counter: the counter resets with it, and the next `Acquire` / `Release` / `CaptureInput` clears the latch. `GaveUpLog` promises recovery "until it is hidden and shown again", and a second panel shown while the first is still up now gets a full five attempts rather than the single one it used to get.

**Showing a panel takes gameplay input where the pointer is, by default (FUI19).** `Ui.CaptureInput(false)` opts out entirely (and `Ui.CaptureInput(true)` opts back in) for a HUD-style overlay that must let the player keep playing underneath; the cursor unlock and Escape forwarding are unaffected either way. `Ui.IsCapturingInput` reports whether a context is live.

### Where the pointer is (FUI19)

**"Capture" was two things wearing one name.** It meant *unlock the cursor* and *block gameplay input*, and a panel did both for as long as it was shown. Only the first has to last: a locked cursor cannot be moved to the panel at all, so the cursor follows the panel. The second does not, and making it last turned any open mod panel into a screen-wide modal — a 640-unit frame open in one corner and the wheel would not zoom, the click would not swing, anywhere. So the cursor still follows the panel and **only the input block follows the pointer**.

`UiCaptureMode` is the three answers, and `WhenHovered` is the framework default:

| Mode | Blocks gameplay input | For |
|---|---|---|
| `WhenHovered` (default) | only while the pointer is inside one of the shown panels' rects | every ordinary panel — the wheel scrolls the list under the mouse and zooms the camera off it |
| `WhileShown` | wherever the pointer is, for as long as any panel is shown | a genuinely modal panel; the pre-FUI19 behavior, kept |
| `Never` | never; the panel never joins the refcount at all | a HUD overlay read while playing — what `Ui.CaptureInput(false)` has always meant, per panel. **Escape closes the panel _and_ opens the vanilla menu** — see below |

**Three ways to set it, most specific first.** `IUiPanel.CaptureMode` (per panel, settable on an open panel and applied at once) → `Ui.CaptureInput(mode, owner)` / `Ui.CaptureInput(mode, pluginId: …)` (that plugin's panels; the two identifying arguments mirror `Ui.Panel`'s, because a default keyed the other way round would silently never match) → `Ui.CaptureInput(mode)` (framework-wide) → the config entry. A mode applies to the **next** Show; an open panel keeps the mode it was shown with, so an unrelated plugin loading cannot change the block under a player mid-interaction. `Ui.CaptureInput(bool)` is unchanged and is still the master switch: `false` removes the context now and keeps it off whatever any panel's mode asks for.

**`[Ui] CaptureMode` in `BepInEx/config/progenitor.ui.cfg`** moves the framework-wide default, alongside `EditOtherModsSettings` in Core's own client cfg. **Client-only and never synced, on purpose:** a synced entry makes the *server* the owner of its value, and this one decides how a player's own mouse behaves on their own screen. It is read at install **and on every `SettingChanged`** — the doc on `BindCaptureMode` used to say "read once, at install" four lines above its own subscription (R44 finding 7). What actually protects an open panel is `UiPanelHandle._shownMode`, frozen at Show and used at Hide, not the number of reads. (There is no ALC pin: the `ConfigFile` is Core-owned and dropped at `Uninstall`, and the handler is rooted only by that file.) A value the enum does not name keeps `WhenHovered` and logs which value it was.

**The hit test is in screen pixels, and that is the load-bearing detail.** Since evidence 165 the mod overlay copies the game's own `CanvasScaler`, so the canvas is laid out in the game's design space and **one canvas unit is not one device pixel** — the scaler puts the scale factor on the canvas `RectTransform`'s `localScale`. The mouse device answers in screen pixels and nothing can change that, so `UnityUiRuntime.PanelRect` moves the panel's rect onto the mouse's ruler rather than the other way round: two opposite corners of `rt.rect` through `rt.TransformPoint` and then `RectTransformUtility.WorldToScreenPoint(null, …)`, which is the same resolution `RectTransformUtility.RectangleContainsScreenPoint(rect, point, null)` performs internally (a null camera means "the screen point is already the world point", exact for a `ScreenSpaceOverlay` canvas because such a canvas's world space *is* the pixel grid). Comparing the mouse against `rt.rect` directly, or dividing the mouse by a scale factor guessed at the call site, is correct only at the scaler's reference resolution — right on the developer's screen and progressively wrong on the player's. Two corners rather than `GetWorldCorners` because our frames are unrotated and the four-corner call allocates an IL2CPP array per panel per frame.

**Only an edge crossing reaches the game.** `HoverRouter` remembers its last answer and reports a change only when the new one differs, and `UiInputCapture.SetPointerBlock` is idempotent underneath that — **idempotent against the blocker, not against the module’s own copy** (FUI23). Asking "did I already decide this" is what let the two copies of the pointer answer drift and stay drifted (R44 finding 1); asking the blocker what it is applying makes every decision self-correcting and still costs no game call when nothing changed. This matters because the thing below is not a boolean: it is the refcount that drives a real `AddInputContext` / `RemoveInputContext` against the game's ordered list, with the `_DenyModificationsOfInputContexts` deferral, a bounded retry budget and a chain budget behind it. Flipping that as the mouse drifts would be indefensible; crossing a panel edge happens at the speed a hand moves. `Only_crossing_the_edge_is_a_change_never_a_frame` drives 180 frames across two crossings and asserts exactly three changes. The per-frame pass is subscribed to `Plugin.Frame` **only while a `WhenHovered` panel is shown** — an all-`WhileShown` set has a constant answer and arms nothing, and a client with no panel open pays nothing.

**The gate is not in `WantedLocked` at all, and that is the whole safety argument (as amended by FUI20).** `WantedLocked()` is `enabled && count > 0`. Until FUI20 the gate was a third, ANDed term — the pointer leaving a panel *removed the context from the game's list* — and the argument was that ANDing rather than ORing kept a stuck gate from leaking a context. It now rests on something stronger: the gate cannot affect registration in either direction, only how much a registered context masks (`UiInputCapture.MaskScope`). A gate stuck in either position — a throw in the pump, a frame handler that stopped, a Core swap mid-decision — still cannot leave a context registered once the last panel closes, so the worst outcome `UiInputCapture` exists to prevent (a list entry in `_InputContexts` with no owner) stays unreachable by construction rather than by care. It is also strictly less work against the game's ordered list: crossing a panel edge sets a flag instead of issuing an `AddInputContext` / `RemoveInputContext` pair. The gate defaults to `true` and is reset to `true` on every 1 → 0, so a process with no pointer source — a server, a test, the frames before the mouse device exists — masks exactly what it did before FUI19.

**The gate has one writer, and the diagnostic reads the other copy (FUI23).** The gate is two values: `UiInputCapture._pointerBlock` (the module’s decision) and `GameInputBlocker._scope` (the volatile int the native mask reads). `Release` used to write the first without telling the second, so closing a panel with the pointer off it left the blocker at `CloseKeyOnly` while the module said `Full`, and reopening the panel *under* the pointer produced no router transition to repair it — the panel was hovered, the mask was believed on, and the wheel zoomed the camera again (R44 finding 1, evidence 167’s defect restored). `WritePointerBlockLocked` is now the only writer of that field and it returns the push its caller owes the blocker, so the write and the push cannot be separated by an edit that touches one of them; `UiInputMaskTests.The_pointer_flag_has_exactly_one_writer` is that grep, as a test. And `UiInputCapture.MaskScope` now reports `IUiInputBlocker.AppliedScope` — the copy that can be wrong — with the module’s decision beside it as `WantedMaskScope`. `Ui.DescribePins` prints `scope=` and `wanted=` and says `DESYNC` when they differ, because a diagnostic that reports only the copy which always agrees with itself is what made finding 1 invisible (R44 finding 4).

### Escape closes the panel and nothing else (FUI20)

**The defect.** With the settings panel open, one Escape press closed the mod panel **and** opened the vanilla SYSTEM menu. Two independent paths saw the same press: Bootstrap's `UiInputContext.Update` reads `Keyboard.current.escapeKey` directly and hides every shown panel, and the game's own pipeline turned the press into `ButtonInputAction.Back` and handed it to `MenuInputSystem`. Nothing stopped the second. It was invisible in the log — `Ui: hide 'Progenitor Settings'` is all our side sees — and only showed up in a screenshot.

**What the game actually does with Escape.** `ProjectM.MenuInputSystem` is the client-world system that opens menus, and it has no per-frame code of its own: its `OnUpdate` is the shared empty stub, and its only real entry point is `HandleInput(InputState)`, which nothing but `InputActionSystem` calls — after subtracting every registered context's `BlockInputState`. No `ProjectM` type reads the keyboard directly: every `escapeKey` / `Key.Escape` / `KeyCode.Escape` in the game's metadata belongs to Unity or the BCL. So Escape **can** be taken cleanly on this build, and the context list is the only place it can be taken.

**Three things had to change, and all three were load-bearing.**

1. **`BlockAllKeys(false)` was never going to do it.** The parameter is named `includeUINavigation` and defaults to `false`: it deliberately exempts `ButtonInputActionCollection.UINavigationActions`, which is what keeps uGUI alive under a chat box and under our panel. Whether `Back` is inside that array is set by a static constructor whose body the dump does not contain, and `BlockAllKeys(true)` is not the answer either — it takes the whole navigation set and the panel stops responding to its own widgets. So the mask names the close actions explicitly: `BlockKey(Back)` (17) **and** `BlockKey(Menu_Close)` (1004), because the Escape key carries the Unity usages `"Back"` and `"Cancel"` and ProjectM has a separate UI-navigation close. That is correct whichever way the `.cctor` goes.

2. **The close key cannot be hover-scoped.** Since FUI19 the block followed the pointer by *removing the context*, so with the mouse anywhere else there was nothing registered to block anything — and Escape has to close a panel from anywhere, or it is not a close gesture. Registration now follows the panel and the **mask** follows the pointer: `UiMaskScope.Full` (chat's whole recipe) while the pointer is over a panel, `UiMaskScope.CloseKeyOnly` (two actions and nothing else) while it is not. `InputMaskPlan.For(scope)` is the whole decision, game-free and tested, and the property that the close actions are taken in *every* scope is a test rather than a line of native callback code nobody can exercise.

3. **The order had to stop tying with the menu system.** `RotateCameraBlocker.Register` hard-codes `AddInputContext(this, world, 600)`, and 600 is `InputContextOrder.MenuInput` — the order `MenuInputSystem` itself sits at. Registering there made our context and the system that opens the vanilla menu **peers**, and which of two equal-order entries subtracts first is decided by a sort this repo cannot read offline (`InputContextState.Order`, sorted through a `Comparison<InputContextState>` whose bodies are not in the metadata). A close key consumed or not by an unspecified tie-break is a half-working fix, so the tie is removed rather than reasoned about: `UiInputCapture.Order` is now **599**. No named order lies between `HUDMenu` (502) and `MenuInput` (600), so every documented relationship survives — still below chat (99) and the HUD menus (495-502), so a vanilla menu opened on top still wins; still above Camera (1000) and Gameplay (1001) — and we are now strictly ahead of the menu system. The cost is that `RotateCameraBlocker.Register` can no longer be used (it hard-codes 600), so `GameInputBlocker` calls `InputActionSystem.AddInputContext(IInputContext, World, int)` and `RemoveInputContext` directly — and must **not** call `Deregister`, whose `_Registered` guard we never set and which would silently leak the context into the game's list. Both halves still verify against `IsContextRegistered`, which is what makes the swap safe rather than hopeful.

**What is still not verified, and how you would tell.** Whether the game honors the block cannot be proved offline; the evidence above is structural (there is no other path to the menu) and the confirmation is a live one — with a panel open, one Escape closes it and leaves the SYSTEM menu shut, from a pointer both on and off the panel. `Ui: input capture … scope=Full|CloseKeyOnly wanted=…` in the pin line says which mask was **applying** (the blocker's own copy) beside which one the module decided, and `DESYNC` when they differ. On a build where the mask detour does not resolve, none of this applies: the context registers with vanilla `_BlockRotateCamera` behavior only, the register line says `mask=absent` and names the consequence, and Escape will double-act.

**`UiCaptureMode.Never` is the mode where Escape still double-acts, by design.** Consuming Escape needs a registered context, and a `Never` panel deliberately registers none — while `SetCapture(true)` still runs for it, because the cursor unlock has to last the whole time a panel is shown whatever the mode says. Cursor unlock and Escape *forwarding* are the same Bootstrap refcount; Escape *consumption* is the Core context. So with `Never` (and with `Ui.CaptureInput(false)`, the same story through the master switch) one press closes the panel **and** opens the vanilla menu. That is the documented meaning of opting out of capture rather than a defect — a HUD overlay you read while playing should not eat the ESC menu — but nothing said so, which is the half R44 finding 6 is about. It is now in `UiCaptureModes.ConfigDescription`, on `UiCaptureMode.Never`, on `IUiPanel.CaptureMode` and on `Ui.CaptureInput(bool)`.

**Both unreadable directions fail closed.** An unreadable pointer (`Mouse.current` null, a read that threw) and an unreadable panel rect both make a hover panel block, which degrades the mode to `WhileShown` rather than to no capture at all — a panel the player can see and cannot scroll is the worse outcome. A pointer *outside the game window* is a different fact and is treated as one: alt-tabbed, on a second monitor, dragged off the edge, it is definitely not over a panel, so the block is released without the player closing anything. A set of `Never` panels with no readable pointer still does not block, because "cannot route" only matters to a panel that asked to be routed.

**Overlap is an OR, and `Never` shields nothing.** Any hovered `WhenHovered` panel blocks; one `WhileShown` panel in the set decides for all of them. A `Never` panel sitting over a hovered one does **not** suppress it — "Never" means "this panel never asks for the block", not "this panel suppresses everyone else's" — because a HUD overlay stretched across the screen would otherwise make every other panel unclickable, and each panel has its own overlay canvas (R24 finding 3) so there is no cheap honest z-order to consult instead.

**Keybind suppression follows the block, not merely "a panel is open".** `Keybinds.Pump` reads `UiInputCapture.IsFullyBlocking` — `enabled && count > 0 && pointerBlock`, the same three terms the mask scope is derived from — so a panel open in one corner while the player is looking at the world no longer suppresses their binds. A hovered panel still does, a `WhileShown` panel does wherever the pointer is, and a focused chat box or text field still wins over everything either way.

**Two properties, because they are two questions (FUI23).** `IsBlocking` is `enabled && count > 0` and answers *should a context be registered* — it must not carry the pointer term, or Escape stops being takeable from anywhere. `IsFullyBlocking` adds the pointer and answers *is the player interacting with a panel*. FUI19 delivered the second by putting the pointer term into the first; FUI20 correctly took it back out and did not revisit the one caller that existed for it, so `Keybinds` was pointed at an expression character-for-character identical to the one it replaced, under a comment describing behavior that had stopped existing (R44 finding 2). Naming both questions is what stops them collapsing into one again; `HoverKeybindTests` drives `Keybinds.Pump` with a panel open and the pointer off it, which nothing did before.

Unity's `EventSystem.IsPointerOverGameObject` answers "is the pointer over mod UI" by raycasting the whole UI stack. This instead reads the cursor against the open panels' bounds — exact for a rectangular frame, no raycast, and it cannot be confused by a vanilla widget under the pointer.

The mechanism is the game's own, not a global input kill (job FUIC). `ProjectM.InputActionSystem` holds a `List<InputContextState>` sorted **ascending** by `InputContextOrder` (`ChatInput = 99` … `HUDMenu = 502`, `MenuInput = 600`, `Camera = 1000`, `Gameplay = 1001`), and each entry subtracts inputs from the ones after it. A mod cannot author a context type — `IInputContext` is emitted by Il2CppInterop as a class, so `ClassInjector` cannot implement it — but the game ships an instantiable one: `ProjectM.RotateCameraBlocker`, whose `Register` is literally `AddInputContext(this, world, 600)`. Core instantiates that, registers it, and postfixes its **uniquely-addressed** `GetConsumedInputs`, guarded on our own instance pointer, to apply chat's mask recipe from `ChatFocusedInputContext.GetConsumedInputs`: `BlockAllKeys(false)` (UI navigation excluded, so the panel itself still works), analog `MoveX|MoveY|AimX|AimY`, and on keyboard-and-mouse `RotateCamera` allowed back so the camera still turns.

Because the list is ordered rather than exclusive, a vanilla menu opened on top of a mod panel still behaves. Order 599 is one below `InputContextOrder.MenuInput` (600), which is where `MenuInputSystem` sits; see **Escape closes the panel and nothing else** for why the tie had to go.

```
Ui: input context registered order=599 mask=live (BlockAllKeys + Move/Aim + Zoom; RotateCamera kept on keyboard+mouse; Back/Menu_Close taken in every scope so Escape closes the panel without also opening the game menu)
Ui: input context registered order=599 mask=absent (the GetConsumedInputs detour is not installed on this build, so the context blocks camera rotation only: no BlockAllKeys, no Move/Aim, no Zoom, and Escape is NOT consumed - one press closes the panel AND opens the game menu. See the 'Ui: input mask not installed' line above for how to fix it.)
Ui: input context removed order=599
```

**`mask=` is measured, not asserted (FUI23).** That line used to state the whole recipe unconditionally, on every successful register — including on a build where `EnsureMask` had already logged that it installed nothing. `Register` returns `true` in that state by design (the context still carries vanilla `_BlockRotateCamera`), so the log promised `BlockAllKeys`, the zoom block and a consumed Escape while the player got none of them and one Escape opened the system menu: job FUI20's own defect 1, with the line whose job is to report what was installed saying the opposite (R44 finding 3). It now takes `IUiInputBlocker.MaskLive`, observed on the pass that did the register, so the two states are distinguishable by grep and the absent one names the consequence a player will actually notice.

Ui: input context register|remove order=599 posted to the game main thread: the caller is on another thread, and _InputContexts must not be mutated off it (_DenyModificationsOfInputContexts only guards main-thread callbacks).
Ui: input context not registered order=599; the panel is shown but gameplay input is not captured yet. Retrying on the next frames (attempt 1 of 5).
Ui: input context register order=599 gave up after 5 attempts; the panel stays shown but gameplay input is not captured until it is hidden and shown again. This is not the _DenyModificationsOfInputContexts deferral (that one waits without a limit and logs its own line) - the InputActionSystem or the Client world was unreachable (main menu or server), or AddInputContext did not land.
Ui: input context order=599 refused removal during teardown; the list entry is leaked until the process exits, but it was made inert (_BlockRotateCamera=false and the per-frame mask is gone), so it consumes no input.
Ui: the input context did not land - IsContextRegistered is false after AddInputContext(order=599). ...
One line each, with the order value. Rules the module enforces:

- **One context, refcounted.** The 0 → 1 shown-panel transition registers; the 1 → 0 transition removes. A second panel does not add a second entry, and closing the first does not free the second's capture.
- **Never mutate the list inside an input callback.** `InputActionSystem` sets `_DenyModificationsOfInputContexts` around every `GetConsumedInputs` / `HandleInput` call, and both `AddInputContext` and `RemoveInputContext` then `Debug.LogError` and silently do nothing. A change requested while that flag is set logs `Ui: input context register|remove order=599 deferred: …` and is retried on the next `Plugin.Frame` tick; the pump is subscribed only while something is pending.
- **Nothing survives teardown.** Panel `Dispose`, `RemoveAll`, plugin unload and Core `Uninstall` all drive the count to zero and remove the context, then dispose the detour. A Core swap that left a context registered would leave the player masked with no owner. Disposing a *shown* panel used to skip its `Hide` (the disposed flag is set before `OnDispose` runs), which also leaked the cursor capture; both are released now.
- **Main thread only, enforced.** `_DenyModificationsOfInputContexts` is set only while the main thread is inside a context callback, so it says nothing about a caller on another thread: an off-thread `Show` would read `false` and mutate `_InputContexts` under the game's own `OnUpdate` walk. Every mutation therefore goes through `MainThread` when the caller is not on it, and logs `Ui: input context register|remove order=599 posted to the game main thread: ...` once. Teardown is the one exception: the Core generation is going away, so a queued removal would be dropped with it - `Uninstall` removes from wherever it was called and warns if that was not the main thread.
- **The add is verified, and refusals are retried.** `RotateCameraBlocker.Register` sets its own `_Registered = true` unconditionally while `AddInputContext` under the deny flag logs and returns without adding, so "nothing threw" is not proof: the add is confirmed with `IsContextRegistered`, and an unconfirmed one drops the context object (it would never add again behind its own guard) rather than caching it. A failed register - no `InputActionSystem` yet, no Client world - is retried on the next frames, bounded at 5 attempts, then one warning says it has given up and names the deny flag so the reader can tell that path apart from the unbounded deferral.
- **Chain exhaustion is bounded too.** A pass superseded while its game call was in flight chains into another, up to eight, and an exhausted chain re-arms the pump so the next frame reconciles registered against wanted. A blocker that keeps churning the panel count from inside its own `Register` exhausts, re-arms and exhausts again — eight `InputActionSystem` mutations and a warning line per frame, forever (R34 finding 15). Three consecutive exhaustions end it: one final warning says it has stopped, and whether the context was left registered with nobody wanting it, and the module makes **no further game calls at all** until a panel is opened again. A refcount change made from *inside* a blocker call does not refill that budget — it is the churn being bounded; any pass that settles or lands does.
- **Nothing persistent is written.** The block is a per-frame mask on a caller-owned `BlockInputState`, never a component or a cursor flip, so a missed teardown costs one frame of movement rather than a stuck character - with one exception the teardown path handles explicitly: the context object carries a persistent `_BlockRotateCamera` field, and vanilla `GetConsumedInputs` blocks `RotateCamera` unconditionally while it is set. A removal the game refuses therefore clears that field before giving up, so the leaked list entry is inert and the camera keeps turning.

Two things this deliberately does **not** do. It does not create the `InputActionsDisabled` tag entity: that is the Steam-overlay hard kill, and `InputActionSystem.UpdateInputDisabled` `DisableDevice`s every Unity device with it, which kills uGUI and the mouse along with the character — right for a non-interactive curtain, wrong for a panel you want to click. And it never patches the shared empty-body stub behind `MenuInputSystem.GetConsumedInputs`, `GameplayInputSystem.GetConsumedInputs` and dozens of unrelated methods, and an IL2CPP detour patches the address, so patching one patches all of them.

The mask detour resolves through the address library first (`DetourSite.Resolve`, site `ui.inputmask`, id `ProjectM.RotateCameraBlocker$$GetConsumedInputs`) with the interop `MethodPointerByPrefix` only as the no-table fallback — see [Native](#native). Until that id is in `tools/addresslib/wanted-client.txt` and the client table is rebuilt, the resolve reports missing and the module degrades gracefully: the context is still registered, with the vanilla `_BlockRotateCamera` body, so the camera stops turning while a panel is open, and one info line names the id and the rebuild command.

Checked with an internal live spike (read-only steps plus one 5 s register/remove round trip).

### Vanilla panel sites (P6, experimental)

`Ui.AttachToVanilla(site, anchor, ctx.Owner, ctx.Guid)` attaches to the game's interaction windows: `ContainerPanel` (left edge, right edge, beside rename), `StationPanel` (workstations and refinement stations, both edges), `InventoryPanel` (beside sort), `CastleHeartPanel` (alias `ThronePanel`; both edges) and `InventoryTooltip` (a line in the item tooltip). The handle reports open, close and the entity shown (`OnPanel`, `Target`: client entity, its `NetworkId` for the server, the hovered item for the tooltip) and hosts `Button(caption)` clones of the game's own inventory bar button and rows in the window's own font. Inline anchors join the vanilla button group; edge columns sit outside the frame and hide, with one log line, rather than overlap another window or leave the screen. A site whose resolver fails after a game update is absent with one line; a missing piece (a donor, an anchor group) is logged once and withholds only what needs it. The refresh is `SystemHooks.After<XMenuMapper>` with `OnlyWhenSystemRuns = false`, reading every mapper once per frame; nothing is patched, no key is read, and everything is released on dispose, `Ui.RemoveAll` and Core Uninstall. Status: source-checked, built and game-free tested (`VanillaPanelTests`), **not live-verified**. Contract, placement rules and the live acceptance procedure: [ui.md](ui.md#vanilla-panel-sites-p6). Example: `samples/Progenitor.PanelsExample`.

### Keybinds (FUI5)

```csharp
ctx.Keybinds.Register(
    "ToggleDemoPanel",
    new KeyChord(UiKey.F9),                 // or UiKey.F9 for the no-modifier overload
    "Open or close the Sample UI demo panel.",
    () => SampleUi.Toggle());
```

**Every key goes through the keybind registry.** A plugin registers each key it acts on with `ctx.Keybinds.Register` (framework code without a context, such as the dev bridge, uses the static `Keybinds.Register`). Plugins must not poll `UnityEngine.Input` (`GetKey*`, `GetButton*`, `GetMouseButton*`, `anyKey*`, `inputString`), the Input System's `Keyboard.current` / `Mouse.current` / `Gamepad.current` or its device list and event hooks (`InputSystem.devices`, `GetDevice`, `onEvent`, `onAnyButtonPress`), IMGUI `Event.current`, or the game's input state (`IsInputPressed` / `IsInputDown` / `IsInputUp`) for their own actions. Only registered binds appear in the Keybinds tab, can be remapped, and take part in conflict resolution. To steer the character rather than bind a key, use [`ctx.Input`](input.md).

The only direct readers are the framework's own input layer:

| File | Why it reads devices |
|---|---|
| `src/Progenitor.Core/Ui/UnityKeyInputSource.cs` | The keybind input pump (`WasPressedThisFrame`, driven by `Keybinds.Pump`) and the key-capture picker (`TryReadPressedChord`, the Keybinds tab's press-a-key flow). |
| `src/Progenitor.Core/Ui/UnityUiRuntime.cs` | The UI kit: typed keys for its text fields (`PumpTypedKeys`), the pointer and wheel sample behind hover, drag and scroll on mod panels, and the controller and arrow-key sample behind [panel navigation](ui.md#controller-and-keyboard-navigation-nav1) (`SampleNav`). |
| `src/Progenitor.Core/Ui/NativeModsEntry.cs` | The UI kit: the pointer hover probe for the Mods entry on the title screen. |
| `src/Progenitor.Core/Inputs/UnityInputRuntime.cs` | `ctx.Input`'s user-activity sample, which lets an input lease yield when the player moves or clicks. It binds no action to a key. |
| `src/Progenitor.Bootstrap/UiInputContext.cs` | Bootstrap's process-lifetime Escape/Back forwarder that closes mod panels; it must outlive Core generations. |
| `src/Progenitor.Dev/Ops/KeyInjectOps.cs` | The dev bridge's [`key`](bridge-ops-client.md#key) test op. It writes input: it queues keyboard events for `Keyboard.current` and reads that keyboard only to report whether its own press was applied. It binds no action to a key. |
| `src/Progenitor.Dev/Ops/MouseInjectOps.cs` | The dev bridge's [`mouse`](bridge-ops-client.md#mouse) test op. It writes input: it queues mouse state events for `Mouse.current` and reads that mouse only to report whether its own move and press were applied. It binds no action to a button. |
| `src/Progenitor.Dev/Ops/NavOps.cs` | The dev bridge's [`nav`](bridge-ops-client.md#nav) test op. It writes input: it adds a virtual gamepad and queues its state events, and reads `Gamepad.current` only to report whether that pad is the current one. It binds no action. |

Otherwise the dev bridge (`src/Progenitor.Dev`) is not an exception: it presses binds with the [`keybind_press`](bridge-ops-client.md#keybind_press) op instead of reading keys. `tests/Progenitor.Tests/DirectKeyReadSourceTests.cs` enforces the rule over `src/`, `samples/` and `spikes/` and reports every offending `file:line`; adding a file to its allowlist requires a reason in the test and a row here. At load, a `ProgenitorPlugin` whose own assembly references one of these reads gets one warning line and an informational `direct-key-read` problem on its Mods page. That check reads metadata only: it also counts unreachable code, does not see helper DLLs the plugin ships, and never refuses the load.

One registration binds one `string` entry under `[Keybinds]` in the plugin's own `GUID.cfg`, in **BepInEx `KeyboardShortcut` format** — main key first, then modifiers, joined with `" + "`: `F9`, `F10 + Ctrl`, `K + Ctrl + Shift`, `None` to unbind. Key names are `UnityEngine.InputSystem.Key` names; modifiers are side-agnostic `Ctrl` / `Shift` / `Alt` (`LeftControl` and friends parse too). Mouse buttons are `MouseLeft`, `MouseRight`, `MouseMiddle`, `MouseBack` and `MouseForward` (`MouseLeft + Ctrl + Shift`); a cfg also reads Unity's `KeyCode` names counted from 0 (`Mouse0` left, `Mouse1` right, `Mouse2` middle, `Mouse3` back, `Mouse4` forward), the shown names (`Left Mouse`, `Right Mouse`, `Middle Mouse`, `Mouse Back`, `Mouse Forward`) and the game's words (`Mouse Button 4` forward, `Mouse Button 5` back, `Scroll Wheel`). An unreadable value keeps the default and logs one line rather than silently unbinding.

**The input read.** A mod cannot add a `ProjectM.ButtonInputAction` — it is a compiled enum — and the vanilla binding list comes from the `Settings/DefaultControls` `InputActionAsset`, so there is no game seam that hands us "a key the player bound". Core therefore polls the Input System's device layer once per `Plugin.Frame`: `UnityEngine.InputSystem.Keyboard.current` (`get_current`), indexed by `Key` (`KeyControl get_Item(Key)`), read through `ButtonControl.wasPressedThisFrame` with `isPressed` for the modifiers. `Unity.InputSystem.dll` is a shipped interop assembly and Bootstrap's `UiInputContext.Update` already reads `Keyboard.current.escapeKey` live, so this path is proven on this build. Legacy `UnityEngine.Input.GetKeyDown(KeyCode)` **is** in the game's metadata and is deliberately not used: this build's active input handling is the Input System package, where the legacy module throws on first read. Reading a device control is not a context mutation, so none of `_DenyModificationsOfInputContexts`' rules apply to this poll. The whole read is behind `IKeyInputSource`, so every rule below is tested without a client.

**The press itself is ours, not the Input System's (evidence 161).** `ButtonControl.wasPressedThisFrame` does not mean "this key went down since you last looked": it is `pressedStepCount == updateStepCount`, true only while the Input System is in the update step that processed the press. The pump samples once per rendered frame, so an update step that advances twice between two samples hides the press completely - `isPressed` reads true and the flag already reads false. That dropped roughly half of every keybind press on this build, which the player experiences as "I have to press F10 twice". So `KeyEdgeTracker` derives the edge itself: the flag is trusted whenever it is set, and otherwise a key seen **up on the immediately preceding sample** and down now counts as a press. The contiguity requirement is load-bearing - the pump skips the source entirely on frames where every bind is suppressed by a captured panel, and after a gap like that a key merely still held must not read as a fresh press. One verdict is cached per key per frame, because `F9` and `Ctrl+F9` share a main key and the pump asks about both in the same frame; a keyboard `deviceId` change clears the held state. `UnityKeyInputSource` logs once, with `InputSystem.settings.updateMode`, when a press had to be recovered - so the log says whether the misalignment is real on a given build. The tracker is game-free and tested (`KeyEdgeTrackerTests`), because the arithmetic is the part that was wrong.

**What the poll costs per frame** (R34 finding 16). The settings panel's own opener is registered from install onward, so `Keybinds.Pump` runs on every client frame for the whole session and its cost is a permanent tax. It is now: one refcount read, a walk of a **cached** registry array (rebuilt only when a bind is registered or disposed — `Live.ToArray()` per frame allocated 60 arrays a second for nothing), and then, only if at least one bound registration could actually fire this frame, one `Keyboard.current` null check, one chat-focus read, and one `KeyControl` read **per bound chord**. With no keybind registered the pump unsubscribes itself and reads nothing at all; with every bind suppressed by an open panel it returns before touching the input source. There is no keyboard scan on the ordinary path — the walk over every `Key` exists only in the rebind capture — and the chat window lookup, which walks every loaded object of its type and allocates, is rate-limited to one attempt per second while no window is cached instead of running every frame.

Three suppression rules, in the order they are checked:

- **Chat wins over everything.** While `ProjectM.UI.HUDChatWindow.IsChatFocused` is true, no bind fires — otherwise typing "f" in chat would trigger every mod bound to F. The gate is **tri-state** (`IKeyInputSource.ChatFocus`: `Focused` / `NotFocused` / `Unknown`) and it **fails closed**: an absent or unreadable chat window answers `Unknown` and suppresses binds exactly as `Focused` does, because an unreadable window cannot prove the player is not typing and firing a bind into a chat line is worse than missing one. That is a real branch, not a comment: the source used to answer "not typing" for every unreachable case, so the fail-closed path could not be entered at all (R34 finding 7). The window is looked up at most once a second while missing and the state change is logged once, not per frame:

```
Ui: no readable chat window (main menu, loading screen, or the HUD is not up yet); keybinds are suppressed until one can be read. ...
Ui: a chat window is readable again; keybinds are live.
```

  The window is found with `Resources.FindObjectsOfTypeAll`, which returns prefabs and assets as well as scene objects, so a candidate is accepted only when its GameObject is in a loaded scene and not hidden in the hierarchy — caching a prefab would read `false` forever and turn the gate permanently off.

  **The one exemption: `allowWhenChatUnknown` (R35 finding 5).** Failing closed on `Unknown` used to suppress *every* bind, F10 included, so a player at the main menu — or on the loading screen after a server whose mods they declined kicked them — could not open the settings panel to reach **Server mods** and undo the install (that group was removed 2026-10-05; the exemption stays so the settings panel opens at the menu). The one place the panel is most wanted was the one place it could not be opened. `Keybinds.Register(..., allowWhenChatUnknown: true)` exempts a bind from the `Unknown` state only. The rule for opting in, and the reason it is safe: **`Unknown` means "no chat window exists in this scene"** — the menu, a loading screen, the frames before the HUD is up — so **a bind that opts in must type text nowhere** (no chat send, no console line, no text field it does not own), because `Unknown` is exactly the state in which a chat box would be invisible to us. Core sets it on the settings-panel opener, and a plugin gets it through `ctx.Keybinds.RegisterOpener` (and the map binds); every other bind still fails closed, and `Focused` still suppresses the opt-in binds along with the rest. The rebind flow ("press a key") is never exempt: it reads *any* key, so it waits for a readable window.

  **When `Unknown` is the real failure.** No readable chat window while a **client world exists** is not the menu — the `HUDChatWindow` lookup has broken, and every non-exempt bind is dead until it recovers. After 300 consecutive unknown polls (~5 s at 60 fps, long enough that a world change or the once-a-second lookup interval never trips it) that logs one warning per episode, so it is visible instead of looking like a quiet main menu:

```
Ui: no readable chat window for 300 consecutive frames while a client world exists - this is not the main menu, so the HUDChatWindow lookup is failing. ...
```

- **Any focused text field suppresses every key bind, exemptions included (R36 finding 5); mouse-button binds still run (2026-10-10: a mouse button types nothing, and Navigate back on Mouse Back was dead while a field kept focus).** The chat probe only ever looks for `HUDChatWindow`, so it is blind by construction to every other text field in the game — and the main menu, which is exactly what `Unknown` means, is full of them: the character-name box, the direct-connect address, the server-browser filter, a private server's password. The `Unknown` exemption was argued from what the *handler* types; the gate exists for what the *player* is typing. So `IKeyInputSource.TextFieldFocused` is a second, generic probe: `UnityEngine.EventSystems.EventSystem.current` → `currentSelectedGameObject` → a `GetComponent` for `TMPro.TMP_InputField` (`get_isFocused`) or legacy `UnityEngine.UI.InputField`, and that component's own `isFocused`. Both types are in the shipped interop and `UnityUiRuntime` already builds and reads a `TMP_InputField` through them. Three hops, no scan, no cache. It suppresses **every** bind — `allowWhenChatUnknown` and `allowWhileCaptured` alike — and the rebind capture with them; the `Unknown` exemption is an exemption from the *chat* probe and from nothing else. Unlike the chat gate this probe is two-state on purpose and answers `false` when it cannot tell: "no EventSystem" and "nothing selected" are the same readable fact, and a read that threw every frame would kill every bind for the session including the opener, which is the failure `allowWhenChatUnknown` was added to fix. The in-world fail-closed guarantee stays with the chat gate. Logged on the transition, not per frame:

```
Ui: a text input field has keyboard focus (a chat box, a name, address or password field, or a mod panel's own field); every keybind is suppressed until it loses focus, the settings panel opener included. ...
Ui: no text field has focus; keybinds are live.
```

  **An exempt bind may not sit on a chord a player could type.** No probe covers a field nobody thought of, so the exemption also costs a chord restriction: for a bind registered `allowWhenChatUnknown: true`, a chord whose main key is one a text field would turn into a character (letters, digits, punctuation, Space, Tab, Enter, the numpad's printing keys) and that does not carry **Ctrl or Alt** is refused — at `Keybinds.Register`, reading the cfg, and in `Keybinds.Rebind`, which is where both the panel's press-a-key flow and a hand-edited cfg value land. `P + Ctrl` is fine; `F10` (the default, and still a function key) is fine; a bare `P` is not. **Not "any modifier" (R37 finding 1).** Two of the eight combinations are exactly how characters are typed, so both are refused too: `Shift` alone is how every capital letter and shifted symbol is produced (`Shift + N` fires on the keystroke that types a capital `N`, which is R36 finding 5 reached through the advice its own fix used to print), and `Ctrl + Alt` together is **AltGr** — Windows delivers `AltGr` as `LeftCtrl` + `RightAlt`, which is how a German, French, Polish or Nordic layout types `@`, `\`, `{` and the rest of the third level, so `Ctrl+Alt+Q` would fire while such a player types `@` into the direct-connect address box. `Ctrl+Shift` and `Alt+Shift` are fine. At registration the player's cfg value is refused back to the default, and a default that is itself a bare printable is the plugin author's mistake — there the *exemption* is dropped rather than the key, so the bind still works and only stops firing at the menu. A rebind is refused outright and the bind stays where it was. Ordinary binds are unaffected: this is the price of the exemption, not a rule about keybinds in general.

  **AltGr is refused at the read, not only at the bind (R37 finding 1).** The chord restriction above only covers binds carrying `allowWhenChatUnknown`; an ordinary bind may legitimately sit on `Ctrl+Alt+Q`, and a cfg written by an older build may already. So `UnityKeyInputSource` reads the **six side-specific** modifier controls — `Keyboard.current.leftCtrlKey` / `rightCtrlKey` / `leftShiftKey` / `rightShiftKey` / `leftAltKey` / `rightAltKey` — instead of the three side-agnostic summaries `ctrlKey` / `shiftKey` / `altKey`, and hands them to `KeyChord.MatchesHeld`. Matching is still exact and still side-agnostic (`Ctrl+K` fires on either Ctrl), with one refusal: a **printable** main key on a chord wanting both `Ctrl` and `Alt` does not fire when the held pair is `LeftCtrl` + `RightAlt` with neither `RightCtrl` nor `LeftAlt` — that is AltGr and is indistinguishable from a player typing a third-level character. `LeftCtrl+LeftAlt+Q` and `RightCtrl+RightAlt+Q` still fire, and `Ctrl+Alt+F5` is never refused, since no layout types a function key. There is no AltGr control to ask instead: `Key.AltGr` is value `54`, declared as an alias of `rightAlt` on the same bit. The rebind capture applies the same predicate, so a chord the read would refuse is never stored.

  **And the refusal is never silent (R38 finding 4).** There is no layout to read — nothing in the Input System reports one — so `LeftCtrl+RightAlt` has to be treated as AltGr for everybody, including the US and UK layouts where right Alt is a plain Alt and left-Ctrl-plus-right-thumb is the *natural* way to hold `Ctrl+Alt`. That refusal used to say nothing in all three places a player could meet it: the bind did not fire, no line was logged, and the panel's press-a-key flow skipped the keystroke and sat in `press a key…` looking broken. The refusal stands; the silence is gone.

  - **At the read.** `IKeyInputSource.WasPressedThisFrame(chord, out refusedAsAltGr)` reports the one refusal it can explain — the key went down with the right modifiers and only the AltGr pair stopped it — and `Keybinds.Pump` logs `AltGrNotFiredLog` once per attempt (`wasPressedThisFrame` is edge-triggered, so that is once per press, not per frame): *"Ui: keybind 'a.mod/Demo' was not fired: Windows delivers AltGr as LeftCtrl+RightAlt, so Q + Ctrl + Alt does not fire when Ctrl and Alt are held that way … Hold LeftCtrl+LeftAlt instead (it fires normally), or rebind to "Q + Ctrl + Shift" or a function key."*
  - **At the rebind capture.** `TryReadPressedChord(out chord, out sides)` now hands back the physical sides instead of dropping the press, and `Keybinds` ends the flow with a **refusal** rather than leaving it running: the long line goes to the log, and `RebindFinished` carries a `RebindResult` (`Bound` / `Cancelled` / `Refused`, with a row-sized `Reason`) so the panel leaves `press a key…` and shows why. A silent no-op is no longer reachable from either path.
  - **The bind itself is a documented limitation, not a rejection.** A printable `Ctrl+Alt` chord is *not* refused at `Register` or `Rebind` for an ordinary bind, because it is not broken: it fires on `LeftCtrl+LeftAlt` and on either right-hand pairing, and only the AltGr pairing never works. So the settings row for such a chord carries the limitation and the alternative next to the bind it applies to (`SettingsPanel.KeybindRowText` → `Keybinds.AltGrAlternative`), which is the same advice FUI10's refusal message gives.


```
Ui: keybind 'progenitor/OpenSettingsPanel' cannot be bound to P. This bind fires at the main menu and on loading screens, where Progenitor cannot see the game's text fields, so a plain character key would type into a name, address or password box and trigger the bind at the same time. Add Ctrl or Alt (e.g. "P + Ctrl"), or use a function key. Shift alone is not enough — Shift is how capitals are typed — and Ctrl+Alt with a printable key is AltGr on German, French, Polish and Nordic layouts, so prefer Ctrl+Shift or a function key. Using F10 instead.
```

- **A mod panel suppresses while it is actually blocking (FUI19).** The test is `UiInputCapture.IsBlocking` — capture enabled, the refcount above zero, **and** the pointer gate open — not merely "a panel is open". So a `WhenHovered` panel open in one corner leaves the player's binds live while they are looking at the world, and takes them the moment the pointer crosses onto the panel; a `WhileShown` panel suppresses throughout, as before. `allowWhileCaptured: true` exempts one bind from *this* rule only (it is not the chat exemption; the settings opener sets both). `Ui.CaptureInput(false)` turns the rule off entirely for a HUD overlay.

Every registration is `ctx.Track`ed, so plugin unload unregisters it; `Ui.RemoveAll(owner)` drops that plugin's binds on the same pass that destroys its panels (so `PluginStaticApiSweep` needs no new call), and Core `Uninstall` drops every bind and the frame subscription before the ALC goes. The `ConfigEntry<string>` is a BCL type, so nothing here can pin a collectible generation. The player's cfg value survives an unregister.

```
Ui: keybind 'progenitor.sample/ToggleDemoPanel' bound to F9 (Open or close the Sample UI demo panel.)
Ui: keybind conflict on F9: 'a.mod/Demo' and 'b.mod/Demo' are both bound to it. Only the one installed first fires; the other is not active until one of them is changed in Mods > Keybinds, the mod's Keys section or the cfg.
Ui: keybind 'b.mod/Demo' is not active: Not active: F9 is used by a.mod, which comes first by mod id (the install times are the same or unknown). Pick another key.
Ui: keybind 'b.mod/Demo' was pressed but not fired: Not active: F9 is used by a.mod, ...
Ui: keybind 'a.mod/Demo' has an unreadable value 'Banana' in the cfg; using F9 instead. ...
Ui: keybinds removed=1 for the unloading plugin.
```

A conflict is a warning, not an error. **Only the winner fires** (KEYS1, decided 2026-09-29; source-checked and game-free tested, not live-verified). The game's own bound keys always come first. Progenitor's own keys come right after the game. Between mods, the one installed first wins (install ledger, else the plugin file's creation time). A mod whose time is unknown comes after every known one, and a tie falls back to plugin id order. A mode-only game key (building, emotes, shapeshift) only warns, and `[Keybinds] EnforceGameWins = false` turns the game rule into warnings. The loser's handler does not run until either side is remapped or unloaded; its Keys row, the Keybinds tab and the Mods list say why ([mods-menu.md](mods-menu.md#keybinds-tab)). Before KEYS1 both handlers ran.

**Reserved chords (KEYS2, requested 2026-09-29; source-checked and game-free tested, not live-verified).** A Progenitor bind is never put on a chord Windows or Steam takes before the game sees it. The table is `ReservedChords.Table` (`src/Progenitor.Core/Ui/ReservedChords.cs`):

| Chord | Reserved by | Reason text |
|---|---|---|
| `Alt+Tab`, `Alt+Shift+Tab`, `Alt+Esc` | Windows | `… switches windows in Windows.` |
| `Ctrl+Alt+Tab` | Windows | `… opens the window switcher in Windows.` |
| `Alt+F4` | Windows | `Alt+F4 closes the window in Windows.` |
| `Alt+Space` | Windows | `… opens the window menu in Windows.` |
| `Ctrl+Esc` | Windows | `… opens the Start menu in Windows.` |
| `Ctrl+Shift+Esc` | Windows | `… opens Task Manager in Windows.` |
| `Ctrl+Alt+Delete` (and with Shift), `Ctrl+Alt+Num Del` | Windows | `… opens the Windows security screen.` |
| `Print Screen`, `Ctrl+Print Screen` | Windows | `… takes a screenshot in Windows.` |
| `Alt+Print Screen` | Windows | `… copies the active window in Windows.` |
| `Alt+Shift+Print Screen` (Left Alt+Left Shift) | Windows | `… turns High Contrast on or off in Windows.` |
| `Alt+Shift+Num Lock` (Left Alt+Left Shift, Mouse Keys) | Windows | `… turns Mouse Keys on or off in Windows.` |
| The Windows key (`LeftMeta` / `RightMeta`) with any modifiers | Windows | `… belongs to Windows (the Start menu, the Game Bar and every Windows-key shortcut).` |
| `Shift+Tab` | Steam | `Shift+Tab opens the Steam overlay.` |
| `F12` | Steam | `F12 takes a Steam screenshot.` |

Every reason ends `Pick another key.` Chords are side-agnostic, so the Left Alt+Left Shift rows also refuse the right-hand keys; that is the safe side. `Shift+Print Screen` and `Ctrl+Shift+Print Screen` are free. `Win+Print Screen` is a Windows-key shortcut (below). `Ctrl+Alt+Esc` has no Windows 10/11 meaning and is not listed. The Steam rows are the client's defaults; Progenitor cannot read Steam's settings, so a player who moved the overlay key is still refused the defaults.

`KeyChord` modifiers are Ctrl, Shift and Alt only, so a `Win + X` shortcut (the Game Bar's `Win+G` and `Win+Alt+…`, `Win+Print Screen`, `Win+Shift+S`) cannot be expressed. It is refused at the read instead. `UnityKeyInputSource` also reads `Keyboard.leftMetaKey` / `rightMetaKey` into `ModifierSides.LeftMeta` / `RightMeta`. A key pressed while either Windows key is held never fires a bind (`KeyChord.MatchesHeld`). The capture refuses it with `Windows-key shortcuts belong to Windows. Pick another key.` The capture also reports the Windows key pressed on its own, so that press is refused rather than ignored.

The table is applied at every place a Progenitor chord is set:

- **A mod's default** on a reserved chord is registered unbound.
- **A cfg value** on one falls back to the default, or to unbound when the default is reserved too.

  The replacement (the default, or `None`) is written back to the cfg, so this happens once: the next start reads a usable value. It logs one warning, for example `Hunts's key F12 is reserved by Steam; pick another key in the Keybinds tab.`, and raises one dismissible `keybind-reserved` warning problem: `F12 is reserved by Steam; your key was reset to F9.` (or `…; your key was unbound.`; for a mod's own default, `Its default key Alt+F4 is reserved by Windows, so it is unbound.`), list line `Key F12 reserved by Steam`. The page line has **Dismiss**. The Keys row and the Keybinds tab row show the same sentence. The problem clears when the player picks a key or dismisses it.
- **`Keybinds.Rebind`** refuses a reserved chord. So do the test-only unchecked path and every capture. A capture keeps waiting for another key (`Keybinds.RebindRefused`, below).
- **Our writes into an ordinary mod's key setting** (`SettingsCatalog.WriteKey`) refuse one.
- **An ordinary mod's key already on one** cannot be stopped, and Progenitor does not rewrite another mod's cfg by itself. Its Keybinds row says `Reserved by Windows; this key may not reach the mod.` (or `Used by Steam; …`), and it gets a `keybind-reserved` problem. The log line is written once per process, not on every read or reload. A `KeyboardShortcut` naming `LeftWindows` / `RightWindows` / `LeftCommand` as a modifier is recognized by its text.

**No conflicting remaps (KEYS2).** What is guaranteed: no remap through Progenitor (`Keybinds.Rebind`, every capture, typed chords, resets, Swap, and the Keybinds tab's writes into config-file keys) stores a chord that a check shows is already taken. `Keybinds.Rebind` returns false, with the bind unchanged and the refusal on `RebindFinished`, when anything else already has the chord:

- a game action on the game's Controls page, mode-only actions included, whatever `EnforceGameWins` says, because the player chose the key;
- any other Progenitor bind, including one of the same mod;
- an ordinary mod's key setting.

The reason names the holder: `F8 is used by Progenitor Sample (Net ping). Pick another key.` / `E is used by the game (Interact). Pick another key.` The capture flows (the settings panel, the mod page's Keys row, the Keybinds tab) refuse such a key and keep waiting. The same applies to typing a chord into a settings row. Choosing the chord a bind already has is a no-op and is accepted, even when that chord is in an existing conflict. `None` (unbind) is always accepted. A reset to a default that is now taken or reserved is refused: `Its default, F9, is now used by Hunts (Open). Unbind it or pick another key.` Limits of the guarantee:

- **Game keys outside a world.** The game's keys are read only in a world. Each read is remembered in memory and in `progenitor-game-keys.txt` in the BepInEx config folder, so at the title screen a remap is checked against the last read, and the refusal says how old it is (`E is used by the game (Interact), as last read in a world 3 days ago. Pick another key.`). A change made in the game's Controls page since then is not known. With nothing remembered yet (the first start), the Keybinds tab accepts the key and says `Game keys can't be checked outside a world; this key may clash with a game action.`
- **A check that cannot run refuses the key** (`Couldn't check this key; try again.`); it never allows it.
- **Races.** The bind and the other Progenitor binds are checked again under the registry lock before a rebind or swap is stored (`This key changed while it was being saved; try again.`). Game and config-file keys are read just before.
- **Existing conflicts are not affected**, for example two mods' defaults, or a key the game took later. The KEYS1 winner rule still decides them and the loser stays inactive and shown.
- **Ordinary mods' own writes** (a mod changing its own cfg, a hand edit) are outside Progenitor.

A grayed row's undo on the mod page goes through the same checks; if the old key is now taken, the undo is refused and logged. `Keybinds.Rebind(pluginId, id, chord, checkConflicts: false)` is internal and used only by tests; reserved chords are refused there too.

**Mid-flow refusals and the capture timeout.** A key refused during a press-a-key flow raises `Keybinds.RebindRefused` (a `RebindResult` with `Outcome = Refused`, `StillCapturing = true` and the reason), never `RebindFinished`, so a subscriber written before KEYS2 still gets exactly one `RebindFinished` per flow, at its end. A refusal restarts the 10 s idle wait, but one capture ends after 30 s in total (`No usable key was chosen within 30 seconds, so the key was left as it was.`). Escape cancels a capture whatever is held with it (Ctrl+Esc, Alt+Esc, Win+Esc).

**Keys that open the mod's UI (KEYOPEN1).** `ctx.Keybinds.RegisterOpener(id, key, description, onPressed)` registers a key that only opens or closes the mod's own UI; it fires at the title screen and the loading screen too, and while a panel has input, so the key closes what it opened. A focused chat box or text field still suppresses it. The handler must only open or close the mod's own UI (a page through `ctx.Menu.OpenPage`, a window, a panel): it must not send chat, run a command or write into a field it does not own, because where no chat window exists the framework cannot tell the player is typing. For the same reason a printable key without Ctrl or Alt is refused: a player's cfg value of that kind falls back to the default, and a printable default keeps its key but fires in play only, with a warning in the log. Examples: the controls sample's Ctrl+F7, the Sample's F9, Ctrl+F9 and Shift+F9. Source-checked and game-free tested (`ModPageOpenTests`). Verified live 2026-10-12 at the title screen on the Steam client: Ctrl+F7, Shift+F9 and F9 opened their page or window and Ctrl+F7 and F9 closed them again, while the Sample's plain F8 bind stayed suppressed.

**Hold and release.** `ctx.Keybinds.RegisterHold(id, key, description, onPressed, whileHeld, onReleased)` runs `whileHeld` every frame the main key stays down after the press, and `onReleased` once when it comes up. `onReleased` also runs when the bind stops being allowed to fire (a panel takes input, chat or a text field gets focus) and when the bind is disposed or its plugin unloads mid-hold. Use it instead of polling a key's held state yourself.

**Map keys.** `ctx.Keybinds.RegisterInMap(id, key, description, onPressed)` and `RegisterHoldInMap(id, key, description, onPressed, whileHeld, onReleased)` register a bind that fires only while the game's world map is open (the full-screen menu on its Map tab). It competes only with what is live there (other map and menu keys, the game's map and menu keys, and Push To Talk, which the game reads on every screen), so a key that means something in play (a letter, a mouse button) can be used in the map. Closing the map under a held key releases it. Plugin example: the Sample's map ruler (`samples/Progenitor.Sample/SampleMap.cs`): Ctrl+C over the map logs the world point under the cursor (`RegisterInMap`), and holding Ctrl+R measures a distance (`RegisterHoldInMap`: the press marks the start, each held frame follows the cursor, the release logs the distance and a release with the map closed cancels). Core's debug buff map teleport (`src/Progenitor.Core/Debugging/DebugMap.cs`, [Debug tools](#debug-tools-debug)) registers the same map-scope hold through `Keybinds.RegisterScoped`.

**Mouse buttons (KEYS8, user report 2026-10-08; source-checked, built and game-free tested in `KeybindMouseButtonTests`, not live-verified; the raycast check is built but runs only in the game).** A bind can be any of the five mouse buttons, alone or with Ctrl, Shift and Alt: `UiKey.MouseLeft`, `MouseRight`, `MouseMiddle`, `MouseBack`, `MouseForward` (`KeyChord.IsMouseKey`). The Keybinds tab, a mod page's Keys row and the settings panel capture a button the same way they capture a key. The middle, back and forward buttons behave exactly like keys. The left and right buttons are how the player attacks, moves and clicks every menu, and Progenitor never consumes input, so the game always sees the click too. Their rules (`MouseChords`, `Keybinds.ClickChordSuppressedReason`):

| Rule | Where | Refusal or reason |
|---|---|---|
| A bare left or right click is never a bind; hold Ctrl, Shift or Alt with it. | Every scope: registration (a cfg value falls back to the default, a default is registered unbound), `Keybinds.Rebind`, every capture, the Keybinds tab | `A plain left or right click attacks, moves and clicks menus, so it can't be a key. Hold Ctrl, Shift or Alt with it (for example Left Mouse + Ctrl + Shift).` |
| Menu and chat keys never use the left or right button, with any modifiers. | `KeybindScope.Menu` (the Mods workspace's own buttons), `KeybindScope.Chat` | `Left and right clicks press the Mods menu's own buttons, …` / `… select text in the chat box, …` |
| A map key may not be Ctrl+Shift+right click (with or without Alt). | `KeybindScope.Map` | `Ctrl+Shift+right click on the map unlocks map regions for admins in the game. Pick another key.` |
| A left or right click chord never fires while the pointer is over a Progenitor window, `allowWhileCaptured` included: a panel that takes input there (hovered, or one that takes input wherever the pointer is), checked every frame, and on the press itself any Progenitor-owned object that is the top UI raycast hit under the pointer, so a passive overlay's or HUD panel's button counts too (`IUiRuntime.PointerOverOwnedUi`). A Progenitor object under one of the game's windows does not count; the game's window takes that click. | The read (`Keybinds.Pump`, `keybind_press`) | `suppressed: the pointer is over a Progenitor window, so the click is the window's` |
| A gameplay left or right click chord does not fire while the game's full-screen menu (the map included) is open. | The read, `KeybindScope.Gameplay` | `suppressed: the game's menu is open, so the click is the menu's` |

So a gameplay `MouseLeft + Ctrl` fires in the world view and the character also attacks on that click. The game's other windows (the inventory, a crafting station) are not detected: a modified click there fires the bind as well as the game's own Ctrl or Shift click. Prefer a map key, another button or a keyboard key when that matters.

**On the map.** The game's `MapMenuMapper.OnMapClicked` turns Ctrl+Shift+left click into its admin teleport (`PlayerTeleportDebugEvent`) and Ctrl+Shift+right click into its admin region unlock, both without a client-side check; plain right click places a marker. A server creates those events only for an authenticated admin (`[AdminEvent(MinRequiredLevel = 2, MinRequiredAuthMethod = 2)]`) and drops them for anyone else while reading them. A map key on `MouseLeft + Ctrl + Shift`, such as Core's **Teleport to map point** if the player binds it there (it is unbound by default), is therefore harmless for a player who is not an authenticated admin: the game's event goes nowhere and only the Progenitor bind acts. For an authenticated admin both teleport to the clicked point. Ctrl+Shift+right click would unlock regions for that admin, so it is refused for map keys. The left and right buttons are not read from the game's bindings (the game reports them only as display text), so a click chord is never shown as clashing with a game action. Another mod's `KeyCode` setting on `Mouse0` or `Mouse1` is compared with click chords; its `Mouse2` to `Mouse4` stay not compared, as before KEYS8, so no bind that existed before changes winner.

**The capture.** The capture takes a left or right button only after it has seen that button up since the capture began, so the click that opened it (on a row's key cap) never becomes the key. A bare left or right click during a capture is never taken and still reaches what it lands on, so **Cancel**, **Unbind** and the scroll bar work with the mouse. On Progenitor's own UI it is silent (ordinary navigation; the capture's help line says `a left or right click needs Ctrl, Shift or Alt`); elsewhere it is refused with the reason above and the capture keeps waiting. A modified click is taken (or refused with its reason), and its own release does not press the Progenitor button under the pointer: `UiSlotTable` drops clicks on Progenitor's widgets until that button is up and two more frames have passed. However long the button is held the drop lasts; only a button that can no longer be read ends it, 3 s after its last held read. Escape still cancels. Conflicts, the first-installed rule and the remap guard treat a mouse chord like any other chord, within its scope. The `keybind_press` bridge op accepts mouse chords (`{"chord":"Mouse0 + Ctrl + Shift"}`) and reports the click reasons above.

The framework's input file reads the buttons: `UnityKeyInputSource` reads `Mouse.current.leftButton` / `rightButton` beside the middle, back and forward buttons, through the same edge tracker. Bootstrap is unchanged.

**Chat keys (Core only).** `KeybindScope.Chat` binds fire only while the player types in the game's chat box, and are then the only binds that run (press only). They meet only the game's in-chat keys (`CycleChat` on Tab), which never stop them; the owner undoes the game's action when both act on one press (the game reads Tab itself in chat, so it cannot be blocked first). Core's name completion key (`ChatCompleteName`, Tab, [chat.md](chat.md#chat-commands-as-built-chat2)) is the one user. There is no plugin API for this scope yet.

### Settings (FUI5)

`Progenitor.Ui.SettingsPanel` lists every registered keybind and every **non-synced** config entry of every plugin Core knows about, with an editor per row. Default opener **F10**, itself a keybind (`progenitor/OpenSettingsPanel` in `BepInEx/config/progenitor.ui.cfg`) and therefore rebindable in the panel it opens.

Row sources are the union of two things, and the panel's open line reports the count so a missing plugin is visible rather than silent: every chainloader plugin's `ConfigFile` (swept on each open) and every plugin that has touched `ctx.Keybinds`, which is how a Progenitor-reloaded plugin — not in the chainloader list — registers itself.

**An edit commits once, at the end of the edit.** The text widget is wired to TMP's `onEndEdit` — Enter, or the field losing focus — never to its per-character `onValueChanged`. That is what makes decimals and negatives typeable at all: on the per-keystroke binding `1.` parsed as `1`, was written, and the parsed value was pushed straight back into the field, deleting the dot the player had just typed, and `-` did not parse so the field snapped back mid-typing (R34 finding 1). One commit is at most one write and therefore at most one cfg save; a commit whose value equals what is already stored writes nothing, so tabbing through a panel does not rewrite the file once per row. The panel writes a value back into a field only on commit — which is how a rejected edit visibly snaps back to what is actually stored.

Editors, and the parse rules `SettingsEditors` pins: **bool** → toggle (`true/false`, `1/0`, `yes/no`, `on/off`); **int** → text, whole numbers only (`1.0` is refused, not truncated) and range-checked against the target type; **float** → text, **invariant** (a German keyboard's `1,5` is refused, never read as 15) and finite only; **enum** → text, by name, case-insensitive, a bare number refused so a renumbered enum cannot silently mean something else (a `[Flags]` enum accepts the comma-separated combination its own formatting prints, e.g. `Combat, Loot`, with every part checked against the declared names); **string** → verbatim, so a prefix setting's trailing space survives. Formatting is invariant too, and floats use round-trip `"R"` so a value the player did not touch is written back unchanged. A rejected edit logs the reason and writes nothing.

**`AcceptableValueRange` / `AcceptableValueList` are consulted before the write.** BepInEx does not throw on an out-of-range set — it *clamps*, and `AcceptableValueList.Clamp` returns `AcceptableValues[0]` for anything invalid, not the nearest value. So a panel that just wrote would turn "the player typed `high`" into "`Difficulty` is now `Low`" and log it as a success (R34 finding 8). The allowed set is read from the entry's description and shown **on the row** (`[Sample] Difficulty  [one of: Low, Med, High]`, `[Sample] SpawnMultiplier  [1 to 10]`), an out-of-range edit is refused with that set in the message and nothing is written, and the success line reports the value **read back from the entry** — naming the typed text separately if the two ever differ:

```
Ui: settings example.mod [Sample] SpawnMultiplier rejected '50': outside the values this setting accepts (1 to 10)
```

Editing writes through the `ConfigEntry`, so BepInEx's own `SettingChanged` + auto-save updates the cfg on disk — the panel never writes a file itself. **Synced entries are shown read-only** and say `(set by the server)`: for the length of a session the server owns them, a client write is refused and reverted, and a server value must never reach the player's `GUID.cfg` ([config-sync.md](config-sync.md)). An entry that cannot be classified is treated as synced, which costs an editor rather than a wrong write. The synced question is asked again **at the moment of the write**, not only when the panel was built: a panel opened at the main menu and left open across a join would otherwise still offer an editable box for a value the server has since taken over.

**Other mods' settings are shown, not edited.** The only "is this the server's?" test the panel has is Progenitor's own config sync, so a third-party mod that syncs its own settings from a server is invisible to it and an edit here would persist a server-owned value into that mod's `GUID.cfg`. Rows discovered by the chainloader sweep are therefore read-only by default and say `(another mod's setting; edit it in that mod)`; an operator who knows the mods involved can set `EditOtherModsSettings = true` under `[Settings]` in `progenitor.ui.cfg`. Rows of plugins that registered themselves through `ctx.Keybinds` — Progenitor's own — are unaffected.

A source whose entry dictionary is mutated by a `Bind` on another thread while the panel is opening is re-walked a few times before it is given up on, so a raced plugin loses its rows only after three attempts rather than on the first one.

**Rebind flow.** Clicking a keybind row's button shows `press a key…` and puts the module in rebind mode: until the next non-modifier press, **no** bind fires anywhere — the point is that pressing the key you want does not also run whatever is already on it. Escape cancels. The new chord is written through the config entry, so it is in the cfg immediately. A chord that is reserved by Windows or Steam, pressed with a Windows key held, or already used by something else is refused on `Keybinds.RebindRefused` and the flow keeps waiting (see **No conflicting remaps** above). **Every ending leaves the flow**, and says which one it was: `Keybinds.RebindFinished` raises a `RebindResult` whose `Outcome` is `Bound`, `Cancelled` (Escape, `CancelRebind`, or the bind went away) or `Refused`, and the panel puts a `Refused` result's `Reason` on the row's label line before restoring the button to the chord that is still bound. Refusals that end the flow are the `allowWhenChatUnknown` chord rule and the AltGr pair (R38 finding 4); neither can leave the row stuck on `press a key…`. KEYS2's refusals keep `press a key…` on purpose, with the reason on the row; they arrive on `RebindRefused`, not `RebindFinished`, and the flow still ends after at most 30 s.

No new asset work: rows are the existing building blocks (`panel.Label`, `panel.Toggle`, `panel.Input`, `panel.Button`), and the panel is a normal `Ui.Panel`, so it takes the usual input capture and Escape closes it.

```
Ui: settings panel opened rows=7 keybinds=2 sources=2
Ui: settings progenitor.sample [Sample] UiDemoGreeting = hi
Ui: settings progenitor.sample [Sample] Count rejected 'banana': expected a whole number
Ui: settings waiting for a key for progenitor.sample/ToggleDemoPanel (Escape cancels; every keybind is suppressed until it lands).
Ui: settings panel closed.
```

### Kept versions (the settings panel's last group)

The settings panel's last group is **Kept versions** (`src/Progenitor.Core/Ui/KeptVersions.cs`, JOINFLOW4): every mod the [version store](join-flow.md#9-keeping-both-versions-stage-4) keeps a parked copy of, one line each, with **Restore** for the player's own version. Nothing is drawn when no versions are kept.

```
— Kept versions —
progenitor.hotdemo: <the version store's page line>
[Restore 2026.10.90]
Ui: settings kept versions: <the restore's answer>
```

The **Server mods** group (FMAN3) that used to precede it, listing packages installed from a server with a **Remove** button, was removed 2026-10-05 with server-sent mod files: Core start now removes those packages itself ([mod-manifest.md](mod-manifest.md#legacy-cleanup-at-core-start)).

Prior art, cited not copied: BepInEx's `KeyboardShortcut` for the cfg value shape. V Rising has no seam for a named mod button in the vanilla controls screen, which is why mod binds live in our panel.

See also [hook and event ordering](hooks-ordering.md).
- [Admin-synced config](config-sync.md)
- [Effects (asset borrowing)](effects.md)
- [Mod manifest (signed catalog, trust on first use, deny descriptor, install window; server-to-client transfer removed 2026-10-05)](mod-manifest.md)
- [UI toolkit](ui.md)

## Abilities

`Progenitor.Abilities.AbilityCast` runs the game's own abilities on demand. It is the supported way
to give a mod a boss's power: you hand the ability group to V Rising's cast pipeline, so the
visuals, timing, damage and replication are the real ability's rather than a reconstruction.

| Member | What it does |
|---|---|
| `Cast(world, character, group, out why)` | Start one cast of `group` on `character`. The character must **own** the group (see below). |
| `TryReadCastState(world, character, out counter, out group)` | What the character is casting. `counter` is the edge to watch; `group` is `Empty` between casts. |
| `TryGetSlotGroup(world, character, slot, out group)` | The ability group in one of the nine bar slots. Slot numbering is per character; read it, do not assume. |
| `TrySetCastTime(world, abilityPrefab, max, post, out prevMax, out prevPost, out why)` | Retime an ability. Edits the **shared prefab**, so restore it on unload. |

Four things will bite you, all measured:

- **The character must own the ability.** A forced cast of a group the character does not have on
  a slot is consumed and silently discarded: the field returns to 0 exactly as if it had worked, and
  the cast counter never moves. To cast a boss power on a player, first give it to them with a
  `ReplaceAbilityOnSlotBuff` and keep it slotted while the feature is armed.
- **One cast at a time.** A forced cast that arrives while the character is already casting is
  discarded silently. This restricts a second cast; it does not prevent an independently
  initialized native payload. SampleFrostDash now spawns the Wendigo ring at both dash edges
  (payload research in the internal research notes).
- **The request is not always consumed.** A declined request stays in `ForceCastGroup` and blocks
  every later one. `Cast` clears the field only when the stale value is one it wrote and the cast
  counter has not moved since.
- **`Cast` returning true only means the request was written.** The only proof a cast happened is
  the cast-started counter moving.
- **Do not observe casts through `AbilityCastStartedEvent`.** Those entities are created and
  consumed inside one frame; a virtual system never sees them. Use `TryReadCastState`.

`Progenitor.Players.PlayerRespawn` does **not** respawn. `Respawn` returns false with
`UnsafeDirectCallRefusal` and does not call `ServerBootstrapSystem.RespawnCharacter`. That call
aborts the dedicated server. `RespawnDeadPlayers` therefore respawns nobody. The working path is
in game: Space, then a waygate, which is `RespawnCharacterSystem`, not the short overload.
The crash evidence is in the internal research notes. A Progenitor
ghost is still not a respawn target: it has the shape of a dead player (`IsBot`, no character).

Worked example: `samples/Progenitor.Sample/SampleFrostDash.cs` (`.frostdash on | off | status | dash`).
`dash` forces the player's own travel ability from the bridge, which is how this feature is tested
without touching the keyboard. Mechanisms and evidence are in the internal research notes.

### `ctx.Abilities`: force cast and no cooldowns

`ctx.Abilities` (`PluginAbilities`) is the plugin-owned, server-only surface. Client calls throw;
ordinary server refusals return false. Call it on the main thread.

| Member | What it does |
|---|---|
| `ForceCast(character, abilityGroupGuid)` / `ForceCast(…, out why)` | Calls `ServerGameManager.ForceCastAbilityGroup(Entity, PrefabGUID)` (server; the manager comes from `ServerScriptMapper.GetServerGameManager()`). False when the world, mapper or character is missing, the character has no `AbilityBar_Shared`, or the group is 0. |
| `NoCooldowns(character)` → `IDisposable` | Keeps every ability the character owns off cooldown with full charges until disposed. Tracked, so plugin unload releases it. |
| `IsNoCooldowns(character)` | True while this plugin holds `NoCooldowns` on the character. |

**Two cast paths, different results.**

| | `AbilityCast.Cast` | `ctx.Abilities.ForceCast` |
|---|---|---|
| Mechanism | writes `AbilityBar_Shared.ForceCastGroup` | `ServerGameManager.ForceCastAbilityGroup` |
| Group not on the character's bar | consumed, never starts (**live 2026-09-25**: `AB_Unholy_CorruptedSkull_AbilityGroup` -1204819086 on a player, `CastStartedCounter` unchanged; the point emote -452406649 on the bar did start) | starts (**live 2026-09-25**, called directly: counter advanced, the projectile flew and hit) |
| `true` means | the request was written | the call returned |

Neither `true` proves a cast. Read `AbilityCast.CastStartedCounter` before the call and a frame
after. The `ForceCast` wrapper itself has not been exercised live; only the underlying game call
has.

**Aim.** Aimed abilities fire at the character's `EntityInput.AimPosition`. For a player that is
written by their client from the mouse, so a forced projectile goes where the player points
(live 2026-09-25). Neither path chooses a target; a client plugin can set the aim with
[`ctx.Input`](input.md).

**Cooldowns are per ability entity** (game defaults):

- Each ability the character has used has its own entity (for example `AB_Unholy_CorruptedSkull_Cast`,
  `AB_Vampire_VeilOfShadow_*`) carrying `AbilityCooldownState { double CooldownEndTime @0x0; float
  CurrentCooldown @0x8 }` and `EntityOwner.Owner` == the character. **Live 2026-09-25:** 50 such
  entities owned by one test player.
- With normal cooldowns, a forced skull barrage every 1.5 s produced 3 casts in about 14 s
  (**live 2026-09-25**). Cooldowns apply to `ForceCast`.
- Charges live on an entity carrying `AbilityChargesState { float ChargeTime @0x0; int
  CurrentCharges @0x4 }`, `AbilityChargesData { … int MaxCharges @0x8 … }` and `EntityOwner`
  together; that is the set the game's `AbilityCooldownSystems` iterates (server metadata).
  **Metadata-derived, not observed.**
- The group state entities (`AbilityGroupState`) carry no cooldown field. The game's own
  `ResetAllCooldowns` AI behavior walks each group's `AbilityStateBuffer` to the ability entities
  and writes `CooldownEndTime = serverTime`, `CurrentCooldown = 0` there (native server
  `ProjectM.Behaviours.ResetAllCooldowns.Initialize`). **Read from native code, not observed.**

**What `NoCooldowns` writes.** About every 0.1 s, on each cached entity whose `EntityOwner` is the
character: `CooldownEndTime = 0` and `CurrentCooldown = 0` when either is non-zero, and
`CurrentCharges = MaxCharges` (capped at the synced 255) when below; charges are never lowered.
Nothing else: cast times, post-cast times, animations and `ChargeTime` are untouched. The
owned-entity lists are rebuilt when a hold is taken, when a held character's `CastStartedCounter`
advances (a first cast of a group creates its ability entity), and once more 0.5 s later. There is
no fixed timer, so while nobody casts nothing scans the world. A rebuild is two queries shared by
all held characters. If one fails, the previous lists are kept and the failure is logged once.
Offsets are `layout.txt` facts checked by `layoutgen --check`.

**Release.** Dispose, plugin unload (every reason), the character ceasing to exist, a new world
epoch (`WorldEpoch.Current`, i.e. a world load), or Core uninstall. Nothing is restored; the game's cooldowns resume from the next
cast. Holds nest: two holders on one character keep it until both release.

**Status: landed, not live-verified.** Unit tests (`AbilityCooldownPolicyTests`) cover the timing
gates, the write decisions and the hold table. That zeroing these fields lets a held player recast
immediately, that charged abilities refill, and that the client HUD follows are **not yet
observed**.

**Live checklist (owed).**

1. `ForceCast(player, -1204819086)` with the skull off the bar: true, `CastStartedCounter` +1 a
   frame later, the projectile flies toward the player's mouse. `AbilityCast.Cast` with the same
   group: counter unchanged.
2. `ForceCast` with a missing entity and with group 0: false with a reason, no exception.
3. Skull every 1.5 s for 14 s: about 3 starts without a hold, about 9 with `NoCooldowns`. The owned
   `_Cast` entities read `CooldownEndTime` 0 and `CurrentCooldown` 0; the client HUD follows.
4. A charged ability: spent charges refill to `MaxCharges` within about 0.1 s, and
   `CurrentCharges` **never exceeds `MaxCharges`**, including with a buff that changes the maximum.
5. A group cast for the first time while held loses its cooldown after that cast (rebuild on the
   counter advance, or the 0.5 s follow-up).
6. Cast times and animations unchanged.
7. Dispose: the next cast goes on cooldown and the log shows the release. Two holds: one dispose
   keeps it, the second releases it.
8. Plugin hot reload and Core reload while held: cooldowns return, no `Plugin.Frame` handler left.
9. Held character destroyed, and a world load: the hold is released with the logged reason.
10. Server frame time with several held characters, idle and while casting.

### Putting a group on the bar: `ctx.Abilities.SetSlot`

`ctx.Abilities.SetSlot(character, slot, group)` shows `group` on bar `slot` (0-8) of a player character until the handle is disposed or the plugin unloads, and the slot then goes back to what the character had. The mechanism is the one the game uses when a player slots a V Blood power: a buff whose prefab carries one `ReplaceAbilityOnSlotBuff` element, which `ReplaceAbilityOnSlotSystem` turns into the slot change. Progenitor registers a clone of `Buff_VBlood_Ability_Replace` per plugin, slot and group (`progenitor.abilityslot.<plugin>.<slot>.<group>`; per plugin because the registry replaces a registration with the same id) and writes the element on the clone, never on the vanilla prefab (research `ability-cast-and-triggers.md` section 2). Priority is 100, above the game's own replace buffs.

On a player character, measured 2026-09-25: slot 0 primary attack, 1 and 4 the weapon's Q and E, 2 Space (travel), 5 R, 6 C, 7 T (ultimate); 3 and 8 empty. Nothing checks that a group suits its slot. The swap is a buff and is saved with the character: after an autosave and an unclean server stop it survives the restart with no owner, until the same plugin slots that group again (which adopts it) or the buff is removed. If the plugin is removed or renamed before that restart, the saved buff names a clone prefab that no longer exists and the load has to drop it (see the save guard for unknown buff prefabs). Two plugins slotting different groups onto one slot both apply at priority 100; which one shows is not defined. Live (earlier build, per-slot clone id): the necro army sample put Corrupted Skull, Chains of Death and Unstable Arachnid on R, C and T (server slots read back, client bar captured), and `.arena reset` gave back the previous spells.

## Models

`ctx.Models` (`Progenitor.Models.PluginModels`, client only) streams in a game presentation prefab that is not loaded right now, keeps it loaded while held, and makes owned copies of it. The first use is hybrid character models (`HYB_*`), such as `HYB_NPCDracula_Evolved`, which the game only loads during Dracula's boss fight.

```csharp
ctx.Models.Catalog = ModelCatalog.Load(path);            // optional name table, see below
ModelHandle dracula = ctx.Models.Load("HYB_NPCDracula_Evolved");
// or, without a table:
ModelHandle dracula = ctx.Models.Load(ModelSection.Parse("ddfc6a4db4009e2448b8f8b6f0965b8f:373"), "HYB_NPCDracula_Evolved");

// A later frame (poll with MainThread.PostDelayed):
if (dracula.IsReady)
{
    GameObject copy = dracula.Instantiate("FilmDracula");
}
```

`Load` returns at once. The handle is `Pending`, then `Ready` or `Failed` with a `ModelLoadReasons` code: `section-not-found`, `request-refused`, `timeout`, `prefab-not-in-section`, `no-client-world`, `server-side`. A Ready handle whose section is unloaded under it goes back to `Pending` with `section-unloaded-reloading` until it is back. Every handle is tracked: unload, for every reason, destroys the plugin's copies and ends its holds. Core uninstall does the same for every plugin.

### How it loads

Each hybrid model ships as one section of the hybrid-model entity scene (`HybridModelStreamingHandler`, client). The loader:

1. finds the section entity by `SceneSectionData.SceneGUID` and `SubSectionIndex`;
2. adds the standard Entities request, `RequestSceneLoaded` (flags 0), if the section has none;
3. waits for `Unity.Scenes.IsSectionLoaded`;
4. finds the prefab by name in `ManagedAssetReferences.AssetLookup`, where the game registers a loaded section's assets, with a `FindObjectsOfTypeAll` name match as the fallback.

The handler's own token API (`ClaimToken` / `ReleaseToken`) is not used. Its generic base `AssetSubSceneStreamingHandler<Singleton>` fails to load through interop (`TypeLoadException`).

Loads of one section share one hold. On the last release the request is removed only when this loader added it and no game-spawned instance of the prefab exists under `HybridModels`. Otherwise it stays, so a unit's model is never unloaded from under it. While held, the section is re-checked every 30 frames and re-requested if the request was removed (for example by the game after a unit with the same model came and went).

### Copies

`ModelHandle.Instantiate(name)` makes an active copy under `progenitor-models-<plugin>` (`DontDestroyOnLoad`) at the origin, with the game's drivers switched off (`ModelInertPolicy`): the `Animator`, the FinalIK solvers (`LimbIK`, `AimIK`, `CCDIK`), every `Hybrid*` component, Magica cloth, and footstep and secondary-animation receivers. Bones hold the prefab's pose until you move them, and skinned renderers update off screen so a posed copy is not culled by its bind-pose bounds. The prefab itself is shared with the game; do not modify it.

### Puppets

`ModelHandle.InstantiatePuppet(name)` wraps an inert copy as a `Puppet`: every transform under it by name, with the pose it was made in kept as the rest pose. Bones are the rig's plain Transforms (`Root_JNT`, `Spine_JNT`, `Left_Hip_JNT`…); with the game's drivers off nothing else moves them.

- `SetLocalRotation`, `SetRotationFromRest(bone, delta)` (rest × delta, in the bone's own axes), `SetWorldRotation`, `SetLocalPosition`, `ResetPose`.
- `AimAt(bone, child, direction)` turns a bone in world space so the line to its child points along a direction. Poses written as "thighs forward, shins down" need no knowledge of each joint's local axes.
- `Dress(equipmentCopy)` moves an equipment copy's skinned meshes onto the puppet, rebinding their bones by name as the game's `HybridEquipmentUser` does, and destroys the rest of the copy immediately. `DressResult.MissingBones` lists bones the puppet lacks.
- Equipment prefabs ship with **empty material slots**; the game fills them on equip. The materials arrive with the equipment's section: find them with `ctx.Models.FindLoadedMaterial(name)` and set them with `Puppet.SetMaterial(renderer, material)`. Character and armor tints are property-block colors (`_BaseColor`, and for transmog armor `_HairColor`, `_ExtraColor`, `_EyeColor` picked by `_CustomizationMask` R/G/B): `Puppet.SetColor(renderer, property, color)`.
- Pose and set materials **a frame after** building and dressing. Live, the first frame after a copy is made reset its pose and cleared materials set on dressed meshes; set a frame later, both held.
- The player models carry `SkinnedMeshRenderAuthoring`; left enabled it cleared dressed materials, so `ModelInertPolicy` switches off every `*Authoring` component.

Equipment streams from its own scene (`34436c5342ba99a468fbcb5f21d18bfa` on the 2026-08-25 build), one item per section; `model-sections.mjs` lists it with `--prefix HYB_,VampireFemale_,VampireMale_` (the default).

**Live, 2026-09-28** (`samples/Progenitor.FilmExample`): TestPlayer built from the resident `HYB_VampireFemale_Prefab`, dressed in the four `VampireFemale_ArmorSet04_Dracula_*` pieces and `VampireMale_HeadGear_RazerHood_Prefab` with no missing bones, given `VampireMale_ArmorSet04_Dracula`, `VampireMale_RazerCloak01` and `VampireMale_Cloak08` and her captured tints, rendered as her in-game look. The evolved Dracula scaled to 1.1 times the player's head height (0.62) and posed. All seven models loaded in 103 to 724 ms.

### Importing props (GLB)

For reproducible imports, use the [static asset package workflow](../tools/asset-build/README.md): export from Blender, build a fresh versioned package, validate it offline, then call `ctx.Models.ImportPackage(directory)`. The package hash detects changed model bytes; it is not an authenticity signature. Both paths use the same parser and tracked runtime ownership. Package validation does not qualify appearance or create a networked item.


`ctx.Models.ImportGlb(path)` loads a binary glTF as new GameObjects under the plugin's models root, at the origin, and returns an `ImportedModel` (tracked; dispose frees the meshes, materials and textures Unity would otherwise keep). `GltfScene` is the game-free reader behind it; it converts to Unity's left-handed space by mirroring X (positions, normals, translations, rotations as `(x, -y, -z, w)`), reversing triangle winding and flipping texture v, and it refuses what it cannot import rather than importing it wrong (skins, morph targets, non-triangle modes, sparse accessors, external `.bin` or image files).

Materials become `HDRP/Lit` (base color and map, metallic, smoothness = 1 - roughness, emissive color and map, alpha test for MASK and BLEND, double-sided) or `HDRP/Unlit` for `KHR_materials_unlit`. HDRP emission is in nits and the game's night exposure is low: emission strengths of 4 to 6 rendered pure white in the church, 0.4 to 0.8 read as lit screens and LEDs. Dark glossy surfaces mirror the fog; author them rough.

Authoring from Blender with the stock exporter (`export_yup=True`): Blender `(x, y, z)` arrives in Unity as `(-x, z, -y)`, so a prop meant to face Unity +Z faces Blender -Y. The film set (`<workspace>\_film\ingame\set\build_set.py`) writes its layout in Unity terms through that mapping.

### Hiding scenery

`ctx.Models.HideScenery(min, max, meshPrefixes)` hides the game's own rendered props in a world box by mesh name (it adds `DisableRendering`); `HideUnitModels(prefix)` switches off the renderers of unit models under `HybridModels` (a unit that still exists on the server, a player). Both are client-only, re-applied every 60 frames so rebuilt props and respawned models stay hidden, and undone on dispose or unload. Baked static-scene pieces ignore `DisableRendering`; they are skipped and counted in `SkippedStatic`. A piece something else already hid is left alone and not counted.

**Live, 2026-09-28** (film sample in Dunley Monastery): the film set GLB (desk, chairs, screen, six racks, four textured plaques; 427 KB) imported at the chancel; `HideScenery` hid the podium and the four candelabras behind it (9 render entities, none static), and `HideUnitModels` hid Raziel (`HYB_NPCNecromancer_MilitiaBishop`, 5 renderers) and the player models.

### Name table

`ModelSection` is the catalog's own spelling, `<scene guid>:<section>`. `tools/catalog/model-sections.mjs` builds a name table from the catalog index and inventory:

```
node tools/catalog/index-catalog.mjs --inventory --out <dir>
node tools/catalog/model-sections.mjs --generated <dir>      # writes <dir>/model-sections.json
```

On the 2026-08-25 build (catalog `b8bd8fa6…`) it lists 321 `HYB_*` models from 1151 scene-section objects. The table is derived data for one build: regenerate it after a game update. Player models (`HYB_VampireFemale_Prefab`, `HYB_VampireMale_Prefab`) are not sections; they are always loaded.

### Verified and open

- **Live, 2026-09-28**, extra client on the film test server, in Dunley Monastery: `Load` of `HYB_NPCDracula_Evolved` (not loaded before) was Ready in 102 ms, and 104 ms after a hot reload. The copy rendered with the game's own materials. The hot reload unloaded cleanly and destroyed the old copy. A section requested by hand in a spike stayed loaded for over a minute while the game unloaded two of its own.
- **Unit-tested** (`ModelsTests`, 44 tests): the section id and its hash words (checked against the live values), the name table, the load, share, release, watchdog and failure paths against a fake streaming runtime, and the inert list. `tools/catalog/test/model-sections.test.mjs` covers the table join.
- **Not yet observed live:** a release that lets the section unload; the `KeptForGame` path; the watchdog re-request; Core uninstall with copies alive.

## Film

`ctx.Film` (`Progenitor.Film.PluginFilm`, client only) records the game world like a film set. Together with `ctx.Models` puppets and a `TakeFile` it plays an animation authored elsewhere and captures it frame by frame.

- **`FilmCamera`** (`ctx.Film.CreateCamera(width, height)`) is an offscreen HDRP camera rendering into a fixed-size target, without the HUD. Before each render it copies the game camera's `HDAdditionalCameraData` (volume layer mask, frame settings, anti-aliasing) and culling mask and anchors volumes at itself: a plain new camera missed the church's interior volume and lit characters blue under the moonlight.
- **`FilmCapture`** (`ctx.Film.Capture(camera, plan, stage)`) sets `Time.captureFramerate` to the plan's rate, so each game frame advances game time by exactly one film frame however long it takes; each frame it calls `stage(filmFrame)`, renders, reads back RGB24 and hands the frame to a background thread that writes it to an ffmpeg process (`CapturePlan.FfmpegArguments`: raw bottom-up RGB24 on stdin, `vflip`, H.264 or a PNG sequence). Warm-up frames (12 by default) stage frame 0 and are not recorded, so TAA and exposure settle. Stop, dispose and unload restore the frame rate and close ffmpeg; the file written so far stays valid.
- Units the server moves (NPCs, players) move in server time. Slow the server to the capture's `RealFps` with `ctx.Time.SlowMotion` to keep them in step.
- **`TakeFile`** reads `take.json` + `take.bin`: per frame each actor's root, pelvis position and per-bone model-space rotation deltas from the bind pose, a camera (position, forward, up, vertical FOV) and prop paths, in Unity axes. `Puppet.ApplyModelPose` turns a frame into a pose: joint world rotation = root × delta × the joint's bind rotation (from the skinned meshes' bind poses), parents first, so the authoring rig's bone axes do not matter.

The film's Blender scene is exported with `<workspace>\_film\ingame\export_take.py` (Blender `Set`-local, converted with Unity (x, y, z) = Blender (-x, z, -y)).

**Live, 2026-09-29** (`samples/Progenitor.FilmExample`): the full take (4,425 frames) played on TestPlayer and Dracula at the in-game desk; a 120-frame 1920x1080 capture ran at 22 frames per wall-clock second and produced a 24 fps H.264 file (ffmpeg exit 0).

## Player forms (experimental, uncommitted)

`Progenitor.Players.Forms.PlayerForms` adds selectable form candidates and inspectable temporary-control handles. This working-tree API has not landed; Bootstrap and Core are deployed only to an isolated test fixture. `AvailableForms` lists only Lord Styx (`1112948824`, `CHAR_BatVampire_VBlood`), marked `Experimental`; no profile is verified, and the empty Bootstrap qualification set currently refuses every normal request.

`IsReady` reports guard availability, not profile qualification. Accepted requests can still be pending; inspect `handle.State` before treating a form as active. Track every non-null handle, even when `TryBegin` fails: partial native failure can leave recovery pending. `TryReturn` reports restoration failure; `Dispose` retains the recovery identifier for retry if restoration fails. Bootstrap owns recovery beyond plugin lifetime.

Both updated Bootstrap and Core are required; Bootstrap installation needs a server restart, not a client update for this addition. 44 policy and 5 facade tests pass. The isolated fixture observed Styx control, timed return, active disconnect restoration and an active-save barrier followed by an explicit successful save retry. Normal-mode melee still crashes; full qualification remains blocked. See [player forms](player-forms.md) for exact evidence and remaining limitations.


## Spawn instance kit (MVP)

`UnitSpawnOptions` and `IUnitHandle` expose instance-only lifetime / team / VBlood strip / stats writes plus a `Configure` callback. Prefer these over `Content.Edit` for per-spawn changes.
