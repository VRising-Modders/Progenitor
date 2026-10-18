# MCP server and tools

The MCP server that wraps the [development bridge](bridge.md) and the build and deploy scripts, its tools, examples and registration in Claude Code. Part of the [development loop](dev-loop.md).

## MCP tools

The server is `tools/mcp/server.mjs`. Bridge tools take `side`: `"client"` or `"server"`. `"server"` is the staged test server (port 27342, token and log under `_testserver`), never `C:\VRisingServer`.

| Tool | When to use it |
|---|---|
| `progenitor_ping` | First call after the game or test server starts. Confirms the bridge is up, which worlds exist, which reloadable plugins are loaded, and the current `logSeq`. Also shows who used the bridge recently ([Bridge users](bridge.md#bridge-users)). |
| `progenitor_queue` | Before you change the client or test server: `join` with sides, an estimate, the Progenitor version and your mods; `wait` for your turn; `leave` as soon as your live work is done, or `release` the side a waiter needs. `message` writes to the holder (next two waiters) or replies to waiters (holder); mail arrives in tool results. After sending, keep watching (`wait` returns when mail arrives) or `retract`. `status` shows the holders, their versions, time left and how to reach them, and the waiters with their expected start. See [Bridge queue](bridge.md#bridge-queue). |
| `progenitor_whoami` | Shows the identity this MCP process sends to the bridges (ai, session name and ID, client, tool, pid, model). Pass `ai` / `session_name` / `session_id` to override them for the rest of the MCP process. |
| `progenitor_logs` | After a reload, a command, or a change you expect to log. Pass `since` from the previous `latestSeq` so you only see new lines. Filter by substring or minimum level when the buffer is noisy. |
| `bridge_status` | Socket thread; never takes the one-op gate. Liveness after a main-thread timeout. No MCP wrapper; `node tools/mcp/call.mjs client bridge_status`. Contract: [api.md](api.md#dev-bridge-ops). |
| `progenitor_reload` | After `progenitor_deploy` of a non-Core DLL, if you do not want to wait for the one-second scan. Returns the same `events` / `loaded` as the `reload` op. |
| `progenitor_users` | Before `progenitor_command`, to see who is connected and which name to pass as `user`. |
| `progenitor_command` | Exercise a chat command on the test server (for example `.spike2 give`). Always `side=server`. `user` is a name or numeric `User.Index`; ghosts from `progenitor_ghost` work (`Ghost1` / `10001`). Then call `progenitor_logs`. |
| `progenitor_chat_observe` | Server only: inject a chat line and watch `SendPacketToUser` for `windowMs` (default 1500). A handled command must return `containsTextAnywhere: false` (no recipient, including the speaker, got the raw text). `chatServerEvents` is the count of distinct `ChatMessageServerEvent` entities seen on a `Plugin.Frame` poll during the window (0 = consumed silently / not observed; >0 with empty recipients = unobserved send path). |
| `progenitor_ghost` | Server only: `create` / `destroy` / `list` User-only ghost users (no network peer). Implementation defaults to experimental `ConnectedUser`; explicitly set `connectedUser:false`. Destroy in the same batch before autosave; use real clients for recipient tests. See [Ghost users (server)](bridge-ops.md#ghost-users-server). |
| `progenitor_capture` | Client only: watch the game draw its own beams/effects while the user triggers them. `start` (optional `seconds`, `filter`, `maxEntities`), then have the user fire the effect, then `stop` — which writes `BepInEx/progenitor-capture/<timestamp>.json` and returns the path plus a summary (new sequence guids with prefab/GO names, endpoint components and whether they changed, which lines moved, and `changedObjects` — the pooled clones that were re-activated or re-parented rather than created). `status` polls a running window. See [`capture`](bridge-ops-player.md#capture). |
| `progenitor_perf` | Either side: what does Progenitor cost per frame. `start` (optional `seconds`, default 30) samples every frame — frame delta, Progenitor's own main-thread work split by source, main-thread queue depth, GC allocation and heap; `stop` snapshots and returns `reportState: writing`; poll `status` or `live` until `completed` / `failed` for the path, summary (p50/p95/max frame and Progenitor ms, per-source rows, allocation rate, worst frame), `closeMs` / `finalizeMs`, and `writeError`. Bootstrap's queue drain is outside every seam — `queueDepth` is the proxy. See [`perf`](bridge-ops-player.md#perf). |
| `mods_ui` | Client, main thread. Focus-free Mods workspace, and (`menu {op}`) the game's full-screen menu: `status`, `open` on a tab, `close` back to gameplay. No MCP wrapper; `node tools/mcp/call.mjs client mods_ui "@file"`. Contract: [api.md](api.md#dev-bridge-ops). Workflow: [mods-menu.md](mods-menu.md). |
| `keybind_press` | Client, main thread. Press a registered keybind (`{bind, plugin?}` or `{chord}`) through Core's real dispatch, focus-free. No MCP wrapper; `node tools/mcp/call.mjs client keybind_press "@file"`. See [`keybind_press`](bridge-ops-client.md#keybind_press). |
| `map` | Client, main thread. Open, close or read the world map (`{"action":"open"\|"close"\|"status"}`) through the calls the M key ends in, focus-free, read back through Core's map detection. No MCP wrapper. See [`map`](bridge-ops-client.md#map). |
| `key` | Client, main thread. Queue key presses (`tap`, `hold`, `down`, `up`, `release`) and text (`type`) into the Input System, focus-free; every press is released. No MCP wrapper. See [`key`](bridge-ops-client.md#key). |
| `build_search` | Client, main thread. Read and drive the build menu search box (`status`, `set`, `clear`, `focus`, `blur`, `type`). No MCP wrapper; `node tools/mcp/call.mjs client build_search "@file"`. See [`build_search`](bridge-ops-client.md#build_search). |
| `dye` | Client, main thread, read only. Dye all's scan step, menu type, world, known pickers, listed and watched cells, hint and last hold; `{"cells":true}` adds each shown cell's screen center for a `mouse` hold. No MCP wrapper; `node tools/mcp/call.mjs client dye '{}'`. See [`dye`](bridge-ops-client.md#dye). |
| `progenitor_exec` | Compile a C# snippet into a throwaway plugin, hot-load it on one side, wait for `scratch: result` or `scratch: error` in the log, then unload it. About 4 s per run. Fastest way to inspect game state. |
| `progenitor_query` | Find entities by component names (`all` required unless `allowUnbounded`, `none` excluded). `total` is opt-in. Returns `index:version` ids, prefab, position, `truncated`. |
| `progenitor_get` | Read one component of an entity as JSON fields. `entity` is `index:version` from `progenitor_query`. |
| `progenitor_set` | Write listed primitive / Entity / PrefabGUID / float3 fields on one component. Returns the component after the write. |
| `progenitor_components` | List the component type names on an entity. |
| `progenitor_spawn` | Server only: instantiate a prefab by name or GUID hash at `(x,y,z)`. Writes Translation + LastTranslation + SpawnTransform; response is the live position one second later plus `placed`. |
| `progenitor_prefab` | Both sides: look a prefab up in `_PrefabGuidToEntityMap` without spawning. `prefab` is a name or int hash (same three lookups as spawn). Returns `guid`, `name`, collection `entity`, `components`. Optional `component` adds a `get`-style field dump. |
| `progenitor_heal` | Server only: fill Health/Blood to the component max (Blood falls back to 100 when MaxBlood reads 0) and clear `Health.IsDead`. Refuses when `ProjectM.Dead` is still present. `user` is a character name; `entity` is `index:version`. Also removes the downed buff, so a downed player stands up in place; releasing is the alternative ([Downed players](bridge-ops-player.md#heal)). |
| `progenitor_teleport` | Server only: move a character to `x`/`y`/`z`, or onto `to` (a character name) / `toEntity`. Tries `TeleportUtilityServer.Teleport` first. If that throws, or the read-back says the character did not arrive, it writes `Translation`, `LastTranslation`, and `PlayerLastValidPosition`. `method` says which path ran. The write is size- and layout-guarded and refuses rather than writing blind. A helper that lands returns in about one second. A refusal waits one more second so `after` is the position after the write. |
| `progenitor_give` | Server only: add an item stack through `ctx.Items.Grant` (`ServerGameManager.TryAddInventoryItem`). `prefab` is a name or int hash (same lookups as spawn), `amount` default 1. Optional `whenFull`, `whenPresent`, `equip`, `durability`. Returns the `ItemGrantResult`; a prefab missing from `ItemHashLookupMap` is an error, not a silent zero. |
| `progenitor_unlock` | Server only, isolated test server only. TEST SETUP: `status` (default) reads unlock counts and the still-locked blueprints and recipes; `all` / `prefabs` unlock every blueprint and recipe prefab (or the listed ones) for `user`; `lock` / `revert` remove listed or recorded GUIDs. `dryRun`, `limit`. See [`unlock`](bridge-ops-player.md#unlock). `all` and `status` ran live 2026-10-04 (a connected client sees the result after rejoining); `lock` and `revert` not run live. |
| `progenitor_equip` | Server only: wear the stack already in bag `slot` through `ctx.Items.Equip`. An empty or missing slot is an error. Does not create an item. |
| `progenitor_inspect` | Read a whole entity in one call instead of `components` plus one `get` per type. `depth` decodes buffer elements, `max` caps the decoded bytes (`truncated` / `truncatedAt` when it runs out), `only` narrows. |
| `progenitor_snapshot` | Copy one entity's unmanaged component blobs out (base64 + decoded, each row stamped with the game build id, `TypeIndex` and `StableTypeHash` — a hash of 0 is recorded as `stableTypeHashUnread`, not as a hash) so an experiment can be undone. **Not a save** — one entity, one world generation, no buffers or managed components. Hand the whole result to `progenitor_restore`. |
| `progenitor_restore` | Write a snapshot's bytes back. Fail-closed on a missing component, a managed/shared/buffer component, a changed size, or a layout-stamp mismatch (game build id / `TypeIndex` / `StableTypeHash`); `strict` (default) writes nothing unless every entry passes, and `strict: false` overrides only the stamp check, reporting a `warning` per row. |
| `progenitor_players` | Player characters on one side, including offline ones: entity id, name, connected, position. |
| `progenitor_photo` | Photo mode: `on` (optional `enable: true`) / `off` on the client, `status` on either side (`side`, default client). See [`photo`](bridge-ops-player.md#photo). |
| `progenitor_camera_preview` | Client, read-only: the running camera preview's framing offset, where the previewed point really projects (`screenX` against `wantedScreenX`), the camera readings it was measured from, and which HUD canvases it hid and whether a container preview's window stays visible. See [`camera_preview`](bridge-ops-ui.md#camera_preview). |
| `progenitor_locate` | Client: `pin` a `ctx.Hud.Locate` ping at world `x`, `y` (up), `z`; `status` (default) reads every ping's last drawn frame (screen position, label, distance, the character position it was measured from, the camera); `clear`. See [`locate`](bridge-ops-ui.md#locate). |
| `progenitor_time` | Server only: world speed. `action` `status` (default) / `speed` with `value` in [0.001, 1] / `freeze` / `resume` / `release`. The bridge holds one lease; `resume` and `release` drop it so normal speed (or another owner's lease) returns. See [`time`](bridge-ops-player.md#time). |
| `progenitor_status` | Framework status: version, uptime, prefab dump (including tombstones), hosted-launcher dump, persistence directory, virtual-systems dump (`fallback=group|bootstrap|pending`, `pluginFrames`, `groupFired`, `worldReady`, live slots). |
| `progenitor_save` | Server: flush every in-memory `ModSave` store to disk now. Client op errors. Returns `{ ok, dir }`. Does **not** write a world `.save.gz`. |
| `progenitor_world_save` | Server: request a real world save (`TriggerPersistenceSaveSystem.TriggerSave`, entity fallback) then `ModSave.SaveAll`. Returns `{ ok, method, fileName, note }`. Optional `fileName` (default `WorldSave.save`) and `reason` (`ManualSave` / `AutoSave` / `QuickSave`). |
| `progenitor_store` | Read or write one mod's `ModSave` bag. Omit `key` to list keys; pass `key` to get; pass `key` and `value` to set. |
| `progenitor_connect` | Client only: from the main menu, join a dedicated server the same way Direct Connect does. `address` required (IPv4, `host:port`, or `SteamIPv4://host:port`; hostnames and IPv6 refused); `port` defaults to 9976 and wins over a port split from the address; `protocol` optional (`SteamIPv4` default, or `Lidgren` for a LanMode server); `password` optional. Fails if already in a world or a launch is in flight. Join is async — follow with `progenitor_ping` until `worlds` contains `Client_0`. |
| `progenitor_host` | Client only: from the main menu, start a private (hosted) game the same way Private Game / Host New does. `saveName` defaults to `progenitor-hosted`; `lan` defaults to true; `password` optional. Shares the connect launch-in-flight slot. Fails if already in a world. Spawn and join are async — follow with `progenitor_ping` until `worlds` contains `Client_0`. Exercises Core's Hosted launcher. Completing this launch remains unverified. Use `host_capture` (`call.mjs`, no MCP wrapper) to inspect an authorized production Private Game launch when investigating. |
| `progenitor_disconnect` | Client only: focus-free production Leave request. Poll `statusOnly:true` for verified world absence and visible MainMenu; pending is not completion. |
| `progenitor_quit` | Client or server: `Application.Quit`. Returns `{ok:true}` immediately; the process exits after the frame. Logs `quit requested via bridge`. Does not refuse. Bootstrap promotes `Progenitor.dll.next` and `Progenitor.Bootstrap.dll.next` on `Application.quitting`. |
| `progenitor_build` | Compile without deploying. Omit `project` for `Progenitor.sln`; or pass a folder name under `src/` (`Progenitor.Core`, `Progenitor.Dev`, …). |
| `progenitor_deploy` | The usual "make it live" step. Builds (unless `build: false`), then copies DLLs. Default projects: `Progenitor.Bootstrap`, `Progenitor.Core`, `Progenitor.Dev`. `side` is `client`, `server`, or `both`. |
| `progenitor_test_server` | Start, stop, restart, or status the staged test server; or `log` to tail its `LogOutput.log`. Never the live dedicated server. |
| `progenitor_logfile` | When the bridge is not up: boot, crash, or Dev not loaded. Reads `BepInEx/LogOutput.log` from disk for that side. Prefer `progenitor_logs` once `ping` works. |
| `progenitor_catalog_search` | Offline (no `side`). Search maintained recipes by free text, a bare GUID, or a typed identity (`prefab:`, `sequence:`, `archive:`, `object:`, `name:`). A hit is not availability. Never starts the game. See [catalog usage](catalog-usage.md). |
| `progenitor_catalog_inspect` | Offline (no `side`). Inspect one recipe by id: requirements, operations, evidence, limitations. `currentVerification.claimable` is always false; `runtimeAvailability` is `not-observed-offline`. |
| `progenitor_catalog_associations` | Offline (no `side`). Generated-inventory name-match associations for one recipe. A missing inventory is `inventory-unavailable`; never regenerated. An inferred name match is a candidate, not a load. |
| `progenitor_catalog_list` | Offline (no `side`). List every maintained recipe id with family, kind, revision and capture date. Presence on this list is not availability or current verification. |

`worlds`, `ops`, `mods_ui`, `keybind_press`, `map`, `key`, `build_search`, `dye`, `bridge_status` and `bridge_users` exist on the bridge only. Use `progenitor_ping` for world names; `mods_ui`, `keybind_press`, `map`, `key`, `build_search`, `dye` and `bridge_status` through `call.mjs` ([api.md](api.md#dev-bridge-ops)).

### MCP examples

One call each for the inspection tools. Argument names match `tools/mcp/server.mjs`. Return shapes match `src/Progenitor.Dev/Ops/EntityOps.cs` and `StatusOps.cs`.

**`progenitor_chat_observe`** — always the test server. `text` is the injected line. Optional `userIndex`, `characterName`, `windowMs` (default 1500). The bridge holds the request until the window closes (game frame not blocked).

```
text: ".ember 1"
```

Handled command: `{ containsTextAnywhere: false, recipients: [... none with containsText: true ], sourceDestroyed: true, chatServerEvents: 0 }`. Negative control `text: "hello"` should set `containsTextAnywhere: true` if Local chat actually went on the wire; with a `ConnectedUser` ghost present, Global chat is expected to produce a `FailedNotConnected` send to the ghost slot and `chatServerEvents` > 0.

**`progenitor_ghost`** — always the test server. `action` is `create`, `destroy`, or `list`. Optional `count` (default 1), `namePrefix` (default `Ghost`), `admin` (default false), `connectedUser` (implementation default true; explicitly pass false for routine command tests). Create result includes `hasConnectedUser` / `inConnectedUsersQuery` / `connectedUsersQueryCount`. Destroy before autosave.

```
action: create
count: 1
```

Then `progenitor_command` with `user: "Ghost1"` (or `userIndex: 10001`), then `action: destroy`.

**`progenitor_exec`** — `code` is the body of `Snippet.Run()`. `Scratch.Server` / `Scratch.Client` are the worlds; `Scratch.Log(...)` writes to the BepInEx log; the return value is logged as `scratch: result`. A `return` is appended if the last line is not already one.

```
side: server
code: Scratch.Log("world=" + (Scratch.Server?.Name ?? "none")); return Scratch.Server != null;
```

Typical log: `scratch: begin` then `scratch: result: True`. Timeout defaults to 20 s (`timeoutSeconds`).

**Process-pinning shapes are refused before compiling.** A snippet runs on a collectible plugin frame, so a generic interop call over a game value type is not just interop trap 2 (hard crash when the IL2CPP instantiation does not exist) — when it throws instead of crashing, the runtime caches the failure as a `TypeInitializationException`, and that retained exception's `_dynamicMethods` keepalive array — the `object[]` CoreCLR fills with the `LoaderAllocator` of every collectible frame in the captured trace, not `_stackTrace`, which is a primitive `byte[]` of `MethodDesc` pointers and roots nothing — holds the throwaway plugin's allocator and, through it, the Core generation it referenced. The Core generation is then pinned and the next Core swap is refused until the process restarts. A `.cctor` is only the retainer the runtime creates for you: any exception kept alive after being caught on a collectible frame does the same. `progenitor_exec` therefore scans the snippet text and refuses:

| Refused text | Use instead |
|---|---|
| `.WithAll<` / `.WithAny<` / `.WithNone<` / `.WithAllRW<` (`EntityQueryBuilder`) | `Progenitor.Ecs.Query.Entities` (component names, no generics) |
| `GetSingleton<` / `GetSingletonRW<` | `Progenitor.Ecs.Singletons` |
| a `new NativeList<…>` / `NativeHashMap<…>` / `NativeParallelHashMap<…>` in a snippet that also calls `.Add(` | `Progenitor.Ecs.Query.Entities`, `EntityExtensions`, or a container the game already instantiated |

The scan is textual, not a compiler pass: it can refuse a name that only looks like one of these. For a deliberate experiment put the line `// exec: allow-generic-interop` in the snippet — it bypasses the whole scan, and the process will most likely need a restart afterwards.

**A type-initializer failure is reported as a process pin.** When the snippet throws and a `TypeInitializationException` is anywhere in the exception chain, the scratch plugin logs, ahead of `scratch: error`, the line:

```
scratch: PROCESS PIN: a type initializer threw inside this snippet; the runtime caches that exception and it roots this plugin's allocator and the Core generation it referenced until the process restarts. The next Core swap will be refused.
```

so it is carried in the `progenitor_exec` result. Treat that side as needing a restart before the next `core_reload` or `.next` swap.

**`progenitor_query`** — find player characters on the test server, including offline ones:

```
side: server
all: ["PlayerCharacter"]
limit: 10
```

Optional: `none: ["Disabled"]`, `includeDisabled: false`, `near: { x: 0, y: 0, z: 0, radius: 25 }`, `total: true` (walk the full match set; default stops at `limit`), `allowUnbounded: true` (permit an empty `all` list). Result: `{ total, count, truncated, entities: [{ entity, prefab, position }] }`. An empty `all` without `allowUnbounded` is an error.

**`progenitor_get`** — read translation of the entity `query` returned:

```
side: server
entity: "45:1"
component: "Unity.Transforms.Translation"
```

`component` can be a simple name (`Health`) when it is unambiguous. Result: `{ entity, component, size, fields }`.

**`progenitor_set`** — move that entity:

```
side: server
entity: "45:1"
component: "Unity.Transforms.Translation"
fields: { "Value": { "x": 10.0, "y": 0.0, "z": -4.0 } }
```

Unknown field names are an error. Result is the component after the write.

**`progenitor_components`** — list every component on that entity:

```
side: server
entity: "45:1"
```

Result: `{ entity, count, components: ["ProjectM.PlayerCharacter",...] }`.

**`progenitor_spawn`** — always the test server. `prefab` is a spawnable name or an integer PrefabGUID hash:

```
prefab: "Item_Ingredient_Stone"
x: 0
y: 0
z: 0
count: 1
```

Writes Translation + LastTranslation + SpawnTransform.Position, then reads Translation back one second later. Result: `{ count, prefab: { hash, name }, entities: [{ entity, prefab, position, placed }] }`. `placed` is true when the read-back point is within 1 m of `(x,y,z)`.

**`progenitor_prefab`** — look up a collection prefab without spawning. `prefab` is a spawnable name or integer hash. Optional `component` dumps that type's fields.

```
side: server
prefab: "Item_Ingredient_Stone"
```

Result: `{ guid, name, entity, components }`. With `component: "ProjectM.ItemData"`: same plus `fields`. Unknown names list the three lookups (`PrefabLookupMap.TryGetPrefabGuidWithName`, `_SpawnableNameToPrefabGuidDictionary`, int hash).

**`progenitor_heal`** — always the test server. `user` is a character name from `users`; `entity` is the character `index:version`.

```
user: "TestAdmin"
```

Result: `{ entity, name, health: { before, after, max }, blood: { before, after, max, present }, isDead: { before, after } }`. Logs `Dev: heal TestAdmin health a->b blood c->d`.

**`progenitor_players`** — including offline (disabled) characters:

```
side: server
```

Result: `{ count, players: [{ entity, name, connected, userEntity, position }] }`.

**`progenitor_status`** — no extra args:

```
side: server
```

Result: `{ version, side, uptimeSeconds, modules: { prefabs, hosted, persistence, systems } }`. The four module fields are diagnostic strings, not JSON objects. `systems` is `VirtualSystems.Describe()` (`fallback=group|bootstrap|pending`, `pluginFrames`, `groupFired=True` when the SimulationSystemGroup anchor has fired, `worldReady`).

**`progenitor_save`** — always the test server in practice. The tool still takes `side`; the client process returns an error.

```
side: server
```

Result: `{ ok: true, dir: "<save folder or null>" }`.

**`progenitor_world_save`** — always the test server in practice. The tool still takes `side`; the client process returns an error. Optional `fileName` (leaf ending in `.save`, default `WorldSave.save`) and `reason` (`ManualSave` default).

```
side: server
```

Result: `{ ok, method, fileName, note }`. `method` is `TriggerSave` on the preferred path. A new `WorldSave.save.gz` should appear under `save-data/Saves/v4/test1` and the log should contain `ModSave: wrote`.

**`progenitor_store`** — `mod` is the store file name (BepInEx GUID). Omit `key` to list; pass `key` to get; pass `key` and `value` to set.

```
side: server
mod: "progenitor.sample"
key: "joins"
```

List: `{ mod, keys }`. Get: `{ mod, key, value }`. Set: `{ ok: true, mod, key }`.

**`progenitor_connect`** — always the Steam client, from the main menu. `port` defaults to 9976 (the staged test server) and, when present, overrides a port split from `address`. `protocol` defaults to `SteamIPv4`; pass `Lidgren` to join a LanMode server (the staged test server in LanMode only opens a Lidgren socket).

```
address: "127.0.0.1"
port: 9976
```

Also valid: `address: "127.0.0.1:9976"` (omit `port`), or `address: "SteamIPv4://127.0.0.1:9976"`. Hostnames and IPv6 are refused. Optional: `password: "secret"`, `protocol: "Lidgren"`. Result: `{ ok, address, port, protocol, note }` (`protocol` is `SteamIPv4` or `Lidgren`). Errors if the client is already in a world, a launch is already in progress, or this is the server process. Join is async; call `progenitor_ping` until `worlds` includes `Client_0`. The MCP wrapper does not yet take `protocol`; use `call.mjs` / the bridge `connect` op for Lidgren.

**`progenitor_host`** — always the Steam client, from the main menu. Starts a private hosted game (`ShouldHost`). Default `saveName` `progenitor-hosted`, `lan` true.

```
saveName: "progenitor-hosted"
lan: true
```

Optional: `password: "secret"`. Result: `{ ok, saveName, note }`. Errors if the client is already in a world, a launch is already in progress (shared with `connect`), or this is the server process. Spawn and join are async; call `progenitor_ping` until `worlds` includes `Client_0`.

**`progenitor_disconnect`** — always the Steam client. Optional `statusOnly:true` observes without leaving; otherwise requests the production ExitGame view without OS input/focus. Poll until `verified:true`; pending is not completion.

**`progenitor_quit`** — client or test server. No extra args beyond `side`. Requests `Application.Quit`; the tool returns `{ok:true}` and the process exits after the frame.

```
side: client
```

Result: `{ ok: true }`. Does not refuse if a world exists. Prefer this over killing `VRising.exe` so Bootstrap can promote staged Core and Bootstrap `.next` files.

Optional environment, all with defaults that match this machine:

| Variable | Default |
|---|---|
| `PROGENITOR_REPO` | two levels up from `server.mjs` |
| `PROGENITOR_GAME_DIR` | `C:\Program Files (x86)\Steam\steamapps\common\VRising` |
| `PROGENITOR_TEST_ROOT` | `<testserver>` |
| `PROGENITOR_CLIENT_PORT` / `PROGENITOR_SERVER_PORT` | 27341 / 27342 |
| `PROGENITOR_CLIENT_TOKEN_FILE` | Steam-install `BepInEx/config/progenitor-dev.token`. Set together with `PROGENITOR_CLIENT_PORT` to aim MCP `client` tools at an extra instance. |

## Registering the MCP server in Claude Code

This repo already has a project `.mcp.json`:

```json
{
  "mcpServers": {
    "progenitor": {
      "command": "node",
      "args": ["tools/mcp/server.mjs"],
      "env": {}
    }
  }
}
```

Opening the Progenitor folder in Claude Code is enough for that file to be picked up. Paths are relative to the repo root.

To add it by hand instead (user scope, or a checkout without the file):

```
claude mcp add progenitor -- node tools/mcp/server.mjs
```

Run that from the repo root so `tools/mcp/server.mjs` resolves. For project scope:

```
claude mcp add --scope project progenitor -- node tools/mcp/server.mjs
```

Needs **Node 18 or newer**. Then, once per clone:

```
cd tools/mcp
npm install
```

That installs `@modelcontextprotocol/sdk` and `zod`. Without it, Claude Code starts `node tools/mcp/server.mjs` and the process exits on the first import.
