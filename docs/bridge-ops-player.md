# Bridge operations: players, world and capture

Operation reference for the [development bridge](bridge.md), part 2 of 5: healing and protection, first characters, time, photos, teleport, give and unlock, inspection, snapshots, saves, connect and host, capture and performance probes. Other parts: [core, entities and samples](bridge-ops.md), [Mods workspace UI](bridge-ops-ui.md), [server info and join flow](bridge-ops-join.md), [input, install and session](bridge-ops-client.md). Part of the [development loop](dev-loop.md).

## `heal`

Server process only, main thread. Fills the character's `Health.Value` from `Health.MaxHealth._Value` (read from the component — never a hardcoded 125) and `Blood.Value` from `Blood.MaxBlood._Value` (100 when that reads 0). Clears `Health.IsDead` if it was set. Then, if the character is downed, removes the downed buff (below). Does not touch `BloodQualityBuff` or any other buff. Replaces the manual `set` recipe (`Health.Value=125`, `Blood.Value=100` on the character from `users`).

Refuses a character that still has `ProjectM.Dead`, before any write. Filling health and clearing `IsDead` while that component remains leaves movement locked and hides the slain screen. That is not a respawn.

**When a player dies or is downed** (the guidance the MCP server gives agents): read the state first (dead = `ProjectM.Dead`; downed = `Buff_General_Vampire_Wounded_Buff` without `Dead`). If a person is playing that character, report and ask; do not revive them unasked. Downed: `heal` restores them in place, with nothing lost. Dead: [`respawn`](#respawn), then `teleport` back to where they were, then `heal` (verified live 2026-10-11); the death's costs (blood type and quality reset, durability) are not undone. A death screen where Space does nothing: clear a broken respawn-list entry with [`bind`](#respawn) `{"clear": true}`. Protections (`progenitor.debug`, `Admin_Invulnerable_Buff`) block damage and make enemies ignore the player; never strip `Immortal` from a player.

**Downed players.** A player who dies usually goes into the downed state first, not dead: `Buff_General_Vampire_Wounded_Buff` (PrefabGUID -1992158531), the buff whose entity carries `VampireDownedBuff`. Healing alone would leave the player downed, so `heal` restores a downed player in place: it fills health first, then removes that buff through the same `DestroyUtility.Destroy(..., TryRemoveBuff)` path as [`buff`](bridge-ops.md#buff) `apply: false`. The result's `downed` says `found`, `removed` (the destroy was issued), `goneNow` (the instance was already gone in the same frame; it can last one more), the buff `index:version` and its `guid`, and the log line ends `downed buff <id> (guid <hash>) removed`. The alternative is the game's own path: the player releases ([`downed`](#downed) `action: finish`, the downed X key) and respawns at their coffin bind point or, without one, the closest official map teleport (waypoint). A character that is fully dead (`ProjectM.Dead`) needs [`respawn`](#respawn). Status: the downed behavior is as reported 2026-10-11; heal's buff removal is unit-tested for its result and log shape (`HealLogicTests`). Live on the isolated test server (2026-10-11, Core 2026.10.170.5+d653330c06, Dev 2026.10.171 from this change): the downed buff applied with `buff` (`Buff_General_Vampire_Wounded_Buff`, -1992158531, carrying `VampireDownedBuff`) on a connected character, then `heal` reported `found: true, removed: true, goneNow: false`; a moment later the buff entity no longer existed and the character's `BuffBuffer` had no downed entry. Not yet run on a player downed by a real lethal hit (health 0). An offline character that already had `ProjectM.Dead` was correctly left to `respawn`.

## `protect`

Server process only. `apply` true (the default) is full debug protection: no-drain or invulnerable plus stealth, `Immortal`, and the kill-path guard. Foes then ignore her. `mode` `alive` is `KeepAlive`: `Immortal`, the kill-path guard and invulnerability; the no-drain and stealth buffs come off only if this protection put them on (never Core's `.debug` buff), and `Aggroable` is forced on only when no foe-ignoring buff stays, so she does not die and bosses can still target her unless another owner's buff says otherwise. `apply` false releases the grant.

`user` is a character name (from `users`). `entity` is `index:version` of a `PlayerCharacter` (or a `User`, whose `LocalCharacter` is used). `entity` wins when both are set. Ghosts have no character and are refused.

```
→ {"id":37,"token":"...","op":"heal","args":{"user":"TestAdmin"}}
← {"id":37,"ok":true,"result":{"entity":"45:1","name":"TestAdmin","health":{"before":1.0,"after":125.0,"max":125.0},"blood":{"before":12.5,"after":100.0,"max":100.0,"present":true},"isDead":{"before":false,"after":false},"downed":{"found":false,"removed":false,"goneNow":false,"buff":null,"guid":null}}}

→ {"id":38,"token":"...","op":"heal","args":{"entity":"45:1"}}
← {"id":38,"ok":true,"result":{"entity":"45:1","name":"TestAdmin","health":{"before":0.0,"after":125.0,"max":125.0},"blood":{"before":12.5,"after":100.0,"max":100.0,"present":true},"isDead":{"before":false,"after":false},"downed":{"found":true,"removed":true,"goneNow":false,"buff":"88:1","guid":-1992158531}}}
```

Logs one line `Dev: heal TestAdmin health 1->125 blood 12.5->100`. On the client process this returns `ok: false` with `"heal runs on the server side only"`. Missing both args: `"args.user or args.entity is required"`. Unknown name: `"no user named '…'"`.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-heal-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"user":"TestAdmin"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server heal "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-heal-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"entity":"45:1"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server heal "@$bridgeArgs"
```

## `damage`

Server process only, main thread. Creates a `DealDamageEvent` for the character. `amount` is raw damage in hit points (`PlayerVampire` modifier 1). `source` is an optional `index:version`; it defaults to the character. A lethal amount sets `IsDead` and adds `ProjectM.Dead`. It did not produce the downed state; a hostile unit's own attack did.

## `downed`

Server process only. `action` `finish` (the default) creates a `GiveUpReviveEvent` plus `FromCharacter`, the same event as the downed `X` key: the player releases and respawns at their coffin bind point or, without one, the closest official map teleport (waypoint). To restore a downed player in place instead, see [Downed players](#heal). `action` `seconds` rewrites `LifeTime.Duration` on the character's `VampireDownedBuff` to `Age + seconds`. That buff is absent once the character is fully dead.

## `respawn`

Server process only. Creates a `CharacterRespawnEvent` plus `FromCharacter`. `location` is `crypt`, `wooden`, `stone`, or `waypoint` (default `waypoint`). `index` is the spawn option (default 0). Does not call `ServerBootstrapSystem.RespawnCharacter`. Verified live: waypoint index 0 cleared `ProjectM.Dead` and restored health to 125.

`bind` (test tooling) records a point for that user only; `{"clear": true}` forgets it. It never touches the game's respawn list, so a normal respawn (the death screen, the game) is unchanged: only this bridge `respawn` op uses the point, teleporting a bound user there once the revive has replaced the character (checked once a second for up to 10 s). Optional `x`, `y`, `z` on `respawn` bind that user, then revive the same way. Until 2026-10-11 bind linked a server-only marker entity into the user's `RespawnPointOwnerBuffer`; the client cannot resolve it, and its death screen threw "The entity does not exist" every frame, so Space never respawned. `bind` and `bind {"clear": true}` remove any such leftover marker.

## `create_character`

Creates the first character for a user that has none, mainly for headless extra clients ([test rig](test-rig.md#extra-clients-and-headless-clients-experimental-early-development)). Args: `name` (required, 1..20 characters, the game's name rules apply) and optional appearance bytes `gender`, `skin`, `face`, `eyes`, `hairStyle`, `hairColor`, `features`, `accessories` (default 0).

**Client (preferred).** Sends the creation screen's own `CreateCharacterEvent` (event id 527272789), unless the client already has a `LocalCharacter`. Then, deferred, it waits for the character and for the client to leave the character-creation scene, and replies:

| Field | Meaning |
|---|---|
| `ok` | `true` when `result` is `ready`. |
| `already` | `true` when the client already had a character, so nothing was sent. |
| `result` | `ready` (in the game, creation scene gone), `not_spawned` (no character within 10 s; a send during loading can be dropped, so run the op again), `still_loading` (the character exists but the client stayed on its loading screen for 10 s), or `stuck` (the op's own calls did not clear the scene). |
| `leftCreation`, `handler` | Whether the op had to take the client off the creation scene, and with which of the game's calls: `HUDMenuManager.GoToMenu(WakeUpMenu)`, `Object.Destroy(creation scene)`, both, or `none`. |
| `character`, `hudMenu`, `view`, `creationScenes` | The last read: the local character entity, `HUDMenuType`, `ViewLocation` and the number of creation scenes. |
| `name`, `event` | Only when it sent: the name and the event entity. |
| `note` | What to do next for every result except `ready`. |

The whole op finishes within 22 s, inside `call.mjs`'s default 30 s timeout.

**Why a client could stay on the creation scene.** While a character-less user's join loads, the game's loading screen already builds the creation scene, whose camera narrows the main camera to that scene. Only the creation menu's shutdown destroys it. A character that arrives during loading (a bridge send before the join settles) makes the game skip the creation menu, so the scene and its camera stayed: the character played normally behind a view of the crypt (seen live 2026-10-18 with an earlier version of this op, which only sent; a disconnect and reconnect cleared it). The op now leaves the game 30 frames for its own exit, then makes the game's own call for whatever is left: `GoToMenu(WakeUpMenu)` while the creation menu is still current (what the menu itself does once the character exists), or `Object.Destroy` on a creation scene that outlived its menu (what the menu's shutdown does). It never touches the scene while the loading screen is up. Because an existing character makes the op skip the send, running it again on a client left on the creation scene clears it.

**Server (fallback).** `platformId` (string or number) or `userIndex` plus `name` on the server bridge enqueues the inbound event for a connected user. The character spawns, but that client stays on its creation screen (verified live 2026-09-24). Run the client form on that client afterwards, or disconnect and reconnect it.

New characters start asleep in the crypt; a headless client cannot press the wake-up key, so run the server `wake` op ([test rig](test-rig.md#extra-clients-and-headless-clients-experimental-early-development)).

Status: the decisions are covered by `BridgeCharacterCreatePlanTests`, and the op compiles. The menu exit and the scene cleanup are source-checked against the game's client metadata only; **not yet verified live**. Acceptance: on a fresh world, run the client op during the join's loading screen, continue into the game, and expect `result: ready` with `handler: Object.Destroy(creation scene)` and the normal game view; run it again and expect `already: true`, `handler: none`.

## Allocator.Temp on the bridge drain

A `NativeArray` allocated with `Allocator.Temp` on the debug-bridge drain aborts the server (`Allocator.Temp allocations are only allowed on the main thread or job worker thread with an explicit ManagedTempMemScope`). The same array is legal in an after-`ServerBootstrapSystem` hook. `Allocator.TempJob` was legal in both places. Do not add new `NativeArray` or `EntityCommandBuffer` allocations with `Allocator.Temp` from a bridge op. Query helpers that already pass `Allocator.Temp` from this drain have not aborted; that is not permission to copy the `NativeArray` pattern.

## `time`

Server process only, main thread. The game's replicated world speed (`ProjectM.TimeScale.Value`), through the same leases as `ctx.Time.Freeze` / `SlowMotion` ([World speed](api.md#world-speed)). Every connected player is affected.

| `action` | Effect |
|---|---|
| `status` (default) | Read only: `value` (1 normal, 0 frozen), `paused` (the game's `IsPaused`, never written), `engineScale` (`UnityEngine.Time.timeScale` on the server), `governing`, and `leases` (every live lease, oldest first, with `owner` = plugin GUID or `progenitor.dev bridge`; the one with `governs: true` is applied). |
| `speed` | `value` in [0.001, 1] (0.25 is quarter speed). The bridge takes a lease at that speed, then drops its previous one. 0 and values above 1 are refused; use `freeze`. |
| `freeze` | The bridge holds speed 0. |
| `resume` / `release` | Drop the bridge's own lease. Another owner's lease then governs; with none left, the speed from before the first lease (normally 1) is written back. Leases other plugins hold are untouched. |

The bridge's lease is also released by a Core reload, a world load, Core uninstall and Dev unload. A refused request (`ok: false`, `refused` says why: world still loading, singleton missing, client process) keeps the bridge's previous lease.

```
→ {"id":40,"token":"...","op":"time","args":{"action":"speed","value":0.5}}
← {"id":40,"ok":true,"result":{"action":"speed","ok":true,"refused":null,"released":null,"value":0.5,"paused":false,"engineScale":1,"governing":"SlowMotion","bridgeHolds":true,"leases":[{"owner":"progenitor.dev bridge","kind":"SlowMotion","scale":0.5,"governs":true}],"readError":null}}

→ {"id":41,"token":"...","op":"time","args":{"action":"resume"}}
```

`engineScale` lags a write by up to one frame. Response shapes above are from the source, not a live run.

## `photo`

Main thread. Core's built-in photo mode ([modules.md](modules.md#photo-mode)), off by default. `action` `on` and `off` run on the **client**; `status` (default) answers on either side.

| `action` | Effect |
|---|---|
| `status` | `enabled`, `active`, `paused`, `pausePending`, `hudHidden`, `pose`, `speed`, `fov`, `holdsInput`, `gameInputBlocked` (the exclusive hold is live and the context registered), `inputHold` (`engaged` / `draining` / `released`), `blockedCollections` (what the mask takes, in words), `inputCapture` (the capture module's one-line state), `aimHeld` and `aimMethod` (the character's aim pinned through the input lease), `targeting`, `preview` and `lockedTarget` (lock-on; each `{entity, name, prefab, nameSource}`, where `name` is the localized display name and `prefab` the raw prefab name), `serverAllows` (true / false / null) and `serverAllowsSource` (`server`, `no-progenitor-server`, `pending`, `not-in-world`; on the server `config`); on the server, `serverPauses` (users holding a photo-mode pause). |
| `on` | Enter photo mode as Ctrl+Shift+F9 would: takes the camera, holds the character's input, hides the HUD (per `HideHudOnEnter`). `enable: true` first sets `[PhotoMode] Enabled = true` in Core's ui cfg; without it a disabled photo mode answers `ok: false` with the refusal. |
| `lock` | Client, photo mode active: lock on to `entity` (`index:version`, a living unit in the client world). Answers `ok: false` with the reason otherwise. |
| `unlock` | Client: drop the lock (`changed` false when nothing was locked). |
| `off` | Leave photo mode: resumes a pause this client asked for, restores the HUD and field of view, returns the top-down camera. `changed` is false when it was not active. |

The pause, HUD and flight controls are Photo-scope keybinds; drive them focus-free with `keybind_press` (`{"plugin":"progenitor","bind":"PhotoModePause"}`; ids in [modules.md](modules.md#photo-mode)) while photo mode is active. A server-side pause appears in `time status` as a `photo mode: <name>` lease.

```
→ {"id":42,"token":"...","op":"photo","args":{"action":"on","enable":true}}
→ {"id":43,"token":"...","op":"photo","args":{"action":"off"}}
```

## `teleport`

Server process only, main thread. Moves a character to a point or onto another character.

**Subject:** `user` is a character name (from `users`); `entity` is `index:version` of a `PlayerCharacter` (or a `User`, whose `LocalCharacter` is used). `entity` wins when both are set. Ghosts have no character and are refused.

**Destination:** exactly one of `x`/`y`/`z` (all three required together), `to` (another character's name), or `toEntity` (`index:version`). `toEntity` wins over `to`, which wins over the point. **No destination is an error** — a teleport must never silently become a teleport to the origin.

The requested height is first dropped to the gameplay floor at that spot (`TileWorld.TryGetGameplayHeight`). The sample includes the requested height, so a stacked floor such as the throne room stays on that floor; a miss keeps the requested height. The move then goes through the game's own `ProjectM.TeleportUtilityServer.Teleport(EntityManager, Entity, float3, Nullable<quaternion>)` (`ProjectM.Shared.dll`): a `public static` entry point with no admin gate and no network event to forge. If that helper throws, or the one-second read-back says the character did not arrive, the op writes the three components every server teleport system writes in lockstep — `Translation`, `LastTranslation` (the `[SyncFloat3]` one the client interpolates from) and `PlayerLastValidPosition` — as `GetComponentDataRawRW` field pokes, never a guessed-length blob. A refused helper waits one more second so `after` is the position after that write. `method` names which path ran: the helper alone, the write alone, or `TeleportUtilityServer.Teleport refused; Translation+LastTranslation+PlayerLastValidPosition`. `SpawnTransform` is deliberately **not** touched: it is the AI leash home, and the game has a separate `TeleportAndSetSpawnPosition` overload for when that is wanted.

**Fallback write guard.** Before writing a position, the operation verifies the component still exposes a `float3` at offset 0 and its interop extent covers 12 bytes. It checks raw ECS `TypeSize` separately with `RawWriteGuard` when that size is nonzero. `TypeSize=0` permits the verified field-pointer path; it does not authorize a guessed full-struct copy. A moved/missing field or insufficient extent is refused. Missing optional position components are skipped; if every candidate is refused, teleport fails with the component-specific reasons. Do not substitute interop size for ECS size.

The response is deferred one second (`MainThread.PostDelayed(1,...)`, the same shape `spawn` uses) so `after` is the live position once the movement group has run, and `arrived` is true when that is within 2 m of the request.

```
→ {"id":40,"token":"...","op":"teleport","args":{"user":"TestAdmin","x":-2125,"y":5,"z":-3167.6}}
← {"id":40,"ok":true,"result":{"entity":"45:1","name":"TestAdmin","method":"TeleportUtilityServer.Teleport","from":{"x":0.0,"y":0.0,"z":0.0},"to":{"x":-2125.0,"y":5.0,"z":-3167.6},"after":{"x":-2125.0,"y":5.0,"z":-3167.6},"arrived":true,"destination":null}}

→ {"id":41,"token":"...","op":"teleport","args":{"user":"TestAdmin","to":"Ghost1"}}
← {"id":41,"ok":true,"result":{"entity":"45:1","name":"TestAdmin","method":"TeleportUtilityServer.Teleport","from":{"x":-2125.0,"y":5.0,"z":-3167.6},"to":{"x":10.0,"y":0.0,"z":-4.0},"after":{"x":10.0,"y":0.0,"z":-4.0},"arrived":true,"destination":{"entity":"88:1","name":"Ghost1"}}}
```

Logs one line `Dev: teleport TestAdmin (0, 0, 0) -> (-2125, 5, -3167.6) via TeleportUtilityServer.Teleport`. On the client process this returns `ok: false` with `"teleport runs on the server side only"`. No destination: `"a destination is required: args.x/args.y/args.z for a point, or args.to (character name) / args.toEntity ('index:version') for another entity"`. A partial point: `"args.x, args.y and args.z are all required for a point destination"`.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-teleport-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"user":"TestAdmin","x":-2125,"y":5,"z":-3167.6}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server teleport "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-teleport-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"user":"TestAdmin","to":"Ghost1"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server teleport "@$bridgeArgs"
```

## `give`

Server process only, main thread. Adds an item stack to a character's inventory.

`user` / `entity` pick the target the same way `teleport` does; a chest or a station also works, since anything with an `InventoryBuffer` is a legitimate target. `prefab` is a spawnable name or an integer PrefabGUID hash — the same argument `spawn` takes, through the same lookups. `amount` defaults to 1 and is clamped to 1..10000 on this op. `ctx.Items.Grant` refuses a non-positive amount instead of clamping it.

Optional. `whenFull` is `allowPartial` (default) or `requireComplete`. `requireComplete` does not remove a short stack that already landed. `whenPresent` is `stack` (default), `refuse`, or `equipExisting`. `equip` true wears the new stack. `durability` `none` sets `LossType` to `None` on the item entity, including a piece the character is already wearing. These are `ItemGrantOptions` on `ctx.Items.Grant`. `equip` as its own op only wears a bag slot that already holds a stack; an empty slot is an error.

The character entity does not hold `InventoryBuffer`. The bag is `InventoryInstanceElement.ExternalInventoryEntity`. `give` and `equip` read that entity; the add itself is still `TryAddInventoryItem` on the character.

The add goes through `ProjectM.Scripting.ServerGameManager.TryAddInventoryItem(Entity, PrefabGUID, int)`, reached through `ServerScriptMapper.GetServerGameManager()` (the mapper is `[SpawnInWorld(2)]`, so it only exists on the server). That wrapper carries its own `ItemLookupMap`, kept in sync with `GameDataSystem.ItemHashLookupMap`, so the op does not build an `AddItemSettings` of its own. It bottoms out in the same `InventoryUtilitiesServer.TryAddItem` a mod would call directly and returns the same `AddItemResponse`.

There is **no "amount added" field** on that response — it reports `RemainingAmount`, so `added` is `requested - RemainingAmount` and `didNotFit` is the rest. `added + didNotFit` always equals `requested`.

The bridge returns the `ItemGrantResult` from `ctx.Items.Grant` (`ok`, `action`, `requested`, `added`, `didNotFit`, `slot`, `itemEntity`, `equipped`, `durabilityCleared`, `result`). A refused call is `ok: false` and the reason string, not a result object.

```
→ {"id":42,"token":"...","op":"give","args":{"user":"TestPlayer","prefab":"Item_Ingredient_Stone","amount":5}}
← {"id":42,"ok":true,"result":{"ok":true,"action":"added","reason":null,"requested":5,"added":5,"didNotFit":0,"slot":10,"itemEntity":null,"equipped":false,"durabilityCleared":false,"result":"Success_Complete"}}
```

A `Failed_*` result is an **error**, not a quiet `added: 0`. `Failed_ItemDoesntExist` — the normal outcome for a cloned prefab that was never registered into `ItemHashLookupMap` — returns `ok: false` with `"prefab N ('…') is not in the server's item table (GameDataSystem.ItemHashLookupMap), so it cannot go into an inventory. …"`. `Failed_InventoryDidntExist`, `Failed_InventoryFull` and `Failed_TriedToEquipItemWithoutEquippable` each get their own sentence.

One caution the item table does not enforce: `InventoryBuffer.Amount` is `[SyncRangedInt(0, 4095)]`, so a single slot above 4095 does not replicate correctly. `TryAddInventoryItem` splits across stacks, so a large `amount` is fine as long as no one slot is pushed past that.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-give-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"user":"TestAdmin","prefab":"Item_Ingredient_Stone","amount":25}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server give "@$bridgeArgs"
```

## `unlock`

**Live (2026-10-04, isolated Forms test server, one connected player):** `status`, then `all` with `dryRun`, then
`all`. 1,849 new GUIDs were planned and written. The one-second read-back showed every one unlocked, with 975
blueprints and 638 recipes; the other 223 blueprints and 30 recipes are content-locked (DLC the account lacks).
**A connected client does not see the bulk part until it rejoins:** the raw write sends no
`UnlockProgressionServerEvent`. After a leave and rejoin, the build menu's Foundation tab went from 2 items to 20.
`lock` and `revert` were not run.


Server process only, main thread, **isolated test server only**: refused when the process path, game root or BepInEx config resolves under `C:\VRisingServer`. TEST SETUP for build-menu and crafting tests: unlock every blueprint and recipe for one player, including the prefabs mods add, and undo it. Status: source-checked against the server dump and native handlers, compile-tested and unit-tested (`DevUnlockArgsTests`); `all` and `status` ran live 2026-10-04 (a connected client sees the result after rejoining); `lock` and `revert` not run live.

`user` is a character name, or the `User.Index` as a JSON number. `action`:

| `action` | Effect |
|---|---|
| `status` (default) | Read only. Row counts of `UnlockedProgressionElement`, `UnlockedBlueprintElement` and `UnlockedRecipeElement` (unlocked and content-locked), totals of `BlueprintData` and `RecipeData` prefabs, the ones still locked (first `limit`, default 50, max 1000), and the record file. |
| `all` | Every `BlueprintData` prefab and every `RecipeData` prefab the player does not have yet. |
| `prefabs` | The listed `prefabs` (names or GUID hashes; each must be in the prefab collection). |
| `lock` | Remove the listed `prefabs` from `UnlockedProgressionElement`. |
| `revert` | Remove exactly the GUIDs the record says `all` / `prefabs` added, then delete the record. |

`dryRun: true` plans and reports without writing. A GUID counts as unlocked the way the game's `ProgressionUtility.HasUnlockedProgression` decides: it is in `UnlockedProgressionElement`, in the recipe or blueprint buffer with `UserHasRequiredContentFlags` true, or in `UnlockedShapeshiftElement`. Rows whose content flag is false (DLC the player lacks) are counted as `contentLocked` and skipped: adding them cannot change that flag.

How the unlock works: the game's `UpdateUnlockedBuffers` rebuilds the blueprint and recipe buffers from `UnlockedProgressionElement`, expanding techs and books and also adding any listed GUID whose prefab has `BlueprintData` or `RecipeData`. So the blueprint and recipe GUIDs are listed directly; no tech is unlocked, which is why mod clones that no tech references are covered. Every new GUID but the last is written into `UnlockedProgressionElement` in one raw write (`UnsafeEntityManagerUtility.SetBufferDataRaw`, layout-checked), and the last is raised as the game's `UnlockProgressionDebugEvent` (EventId 1381311156) on the server as a received event for that player. Its handler adds it and rebuilds the derived buffers once. The player needs a live character. The GUIDs added are recorded **before** the write in `BepInEx/config/progenitor-unlock-<name>.json` on the server (name lower-cased; characters other than letters, digits, `-` and `_` become `_`); later unlocks append to it.

Undo: the game has no lock-progression event (only the server-to-client `LockProgressionServerEvent`). `lock` and `revert` write `UnlockedProgressionElement` without the targets and without one kept row that is in no derived buffer (normally a tech or V Blood GUID), then raise that row again as `UnlockProgressionDebugEvent`. The handler re-adds it and rebuilds, so the list ends as before minus the targets. The op refuses when no kept row qualifies. A GUID that a remaining tech or book grants stays unlocked (`stillUnlocked` in the read-back). A GUID only granted through a tech cannot be locked here (`notInProgression`).

**Owners.** For each new GUID a Progenitor mod registered, the record also stores that mod under `owners` (`{"-1276252187": {"mod": "<plugin GUID>", "id": "sample.bench.crystal-thread", "kind": "recipe"}}`), and `newList` rows carry `mod`. Records written before this have no `owners` and still parse. `status` adds `dangling`: `UnlockedProgressionElement` rows whose prefab is not in the collection, with the registering mod when Core knows it. Those are the rows that make the client log `does not exist as a registered prefab` twice a second. Core removes the Progenitor ones itself when their mod is removed, after the next world load, or when the player next connects ([recipes.md](recipes.md#when-the-mod-is-gone)); use `lock` for anything else.

Writes return after a one-second read-back (`readback`): `eventConsumed`, how many GUIDs now count as unlocked (or still do, for `lock`/`revert`) and the buffer sizes. If the handler did not put the lock trigger row back, the read-back appends it raw and says so. Then the derived buffers were not rebuilt: run `status` again. The op writes only `UnlockedProgressionElement` and the event; spell-school passives, shapeshifts and journal state are not touched.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-unlock-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"user":"TestAdmin","action":"all","dryRun":true}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server unlock "@$bridgeArgs"
```

Then the same call without `dryRun`, `{"user":"TestAdmin","action":"status"}` to read the result, and `{"user":"TestAdmin","action":"revert"}` to restore the player.

## `inspect`

Both sides, main thread. Returns each component on one entity with its decoded fields, combining the component inventory and field reads in one operation.

Decoding is the same walk `get` uses. Buffers add `GetBufferRawRO` / `GetBufferLength` (both public on this interop's `EntityManager`), decoding `depth` elements each.

| Arg | Default | Meaning |
|---|---|---|
| `entity` | required | `index:version` |
| `depth` | `0` (max 256) | Buffer elements decoded per buffer. 0 lists names and lengths only. |
| `max` | `65536` (1024–1048576) | Decoded-byte budget across the whole entity. |
| `only` | every component | Component names (simple or full) to decode. The rest are still listed, with `skipped: "not requested"`. |

Every row carries `component`, `kind` (`component` / `tag` / `buffer` / `managed` / `shared`) and `size`; a decoded row adds `fields`, a buffer row adds `length` (and `elements` / `elementsTruncated` when `depth` is set). A component that cannot be decoded — a managed or shared component, an interop struct that cannot be found, a read that throws — gets a `skipped` reason instead of failing the whole call. Running out of budget sets `truncated: true` and `truncatedAt` to the component the walk stopped on, so the caller knows where to resume with `only`.

```
→ {"id":43,"token":"...","op":"inspect","args":{"entity":"45:1","depth":2,"max":8192}}
← {"id":43,"ok":true,"result":{"entity":"45:1","name":"TestAdmin","prefab":{"hash":123,"name":"CHAR_VampireMale"},"count":118,"depth":2,"bytes":4096,"maxBytes":8192,"truncated":false,"truncatedAt":null,"components":[{"component":"Unity.Transforms.Translation","kind":"component","size":12,"fields":{"Value":{"x":0.0,"y":0.0,"z":0.0}}},{"component":"ProjectM.BuffBuffer","kind":"buffer","size":16,"length":7,"elements":[{"PrefabGuid":-1454216809,"Entity":{"index":902,"version":1}},{"PrefabGuid":29614748,"Entity":{"index":903,"version":1}}],"elementsTruncated":true},{"component":"ProjectM.Prefab","kind":"tag","size":0}]}}
```

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-inspect-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"entity":"45:1"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server inspect "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-inspect-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"entity":"45:1","only":["Health","Blood"]}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server inspect "@$bridgeArgs"
```

## `snapshot` / `restore`

Both sides, main thread. `snapshot` copies one entity's unmanaged component blobs out as base64 (plus the same decoded view `get` returns); `restore` writes those bytes back. This is how you undo an experiment on the test server: snapshot, poke at the entity with `set` / `buff` / `teleport`, then restore.

**This is not a save.** It is one entity, one world generation, unmanaged `IComponentData` only. It does not capture buffers, managed or shared components, the archetype, or anything the entity points at, and an `Entity` field inside a restored blob only means anything while that other entity still lives at the same `index:version`. Restoring into a world that has been reloaded since the snapshot writes stale entity references. Use [`world_save`](#world_save) for a real save.

`snapshot` takes `entity` (required), `max` (raw-byte budget, default 65536, max 1048576) and `only`. Rows are the same shape `inspect` uses plus `bytes` (base64) on the ones actually copied; buffers, tags, managed and shared components are listed with a `skipped` reason and no blob. Hand the **whole result** back as `args.snapshot`.

**Layout stamp.** Snapshots carry the GameAssembly build id and each readable component's `typeIndex` and `stableTypeHash`. Unreadable parts are omitted and count as failed checks. A zero stable hash means unknown, never a matching identity. `restore` refuses that row under the default `strict:true`; `strict:false` reports a per-row warning. The build id includes the file size, PE timestamp/checksum and hash prefix.

`restore` takes `entity`, `snapshot`, and `strict` (default `true`). The size rules are hard in both modes:

| Rule | Refusal |
|---|---|
| Still on the entity | `'X' is no longer on this entity; restore does not add components back` |
| Still unmanaged `IComponentData` | `'X' is a managed component; there is no raw blob to write back` (and the shared / buffer / tag variants) |
| Still exactly the recorded byte size | `'X' changed size since the snapshot (32 -> 28 bytes); the layout is different and writing the old bytes would corrupt the chunk` |

Only the build/type identity checks can be relaxed by `strict:false`; size and layout guards still apply. A component with the same size can have reordered fields, so size alone does not establish compatibility:

```
'ProjectM.Health' was captured on game build '236…-a-b-cafe…' and this process is build '241…-c-d-beef…': the game binary changed, so the component's field layout is not guaranteed to be the one those bytes came from. The byte count still matches, so the old bytes would fit, but nothing here checks the field order. Pass strict: false to write them anyway
```

A snapshot taken before stamping (or hand-written) is refused the same way, with `'X' has no layout stamp in the snapshot …`. Note what the stamp is **not**: it is an identity, not a layout description. Equal stamps prove the component is the same type on the same `GameAssembly.dll`; nothing here reads field offsets.

With `strict` every entry is decided before anything is written, and one refusal aborts the whole call: `ok: false` with `restore of 45:1 refused and wrote nothing (2 component(s) failed): …. Pass strict: false to write the components that do still match.` With `strict: false` the entries that pass are written, a stamp mismatch is written anyway with a `warning` on its row (and counted in the result's `warnings`, logged as `Dev: restore 45:1 wrote ProjectM.Health over a layout-stamp mismatch: …`), and the rest come back as `restored: false` rows with their reason. The write itself is the same `SetComponentDataRaw` call `set` uses, with the whole blob instead of a patched field, and each one logs `Dev: restore 45:1 ProjectM.Health 32 bytes`.

```
→ {"id":44,"token":"...","op":"snapshot","args":{"entity":"45:1","only":["Health","Blood"]}}
← {"id":44,"ok":true,"result":{"entity":"45:1","world":"Server","takenUtc":"2026-09-04T00:00:00.0000000Z","build":"236…-a-b-cafe…","captured":2,"skipped":116,"bytes":56,"maxBytes":65536,"truncated":false,"truncatedAt":null,"note":"Not a save: …","components":[{"component":"ProjectM.Health","kind":"unmanaged","size":32,"typeIndex":77,"stableTypeHash":"0x00000000deadbeef","bytes":"AAAAAA…","fields":{"Value":125.0}}]}}

→ {"id":45,"token":"...","op":"restore","args":{"entity":"45:1","snapshot":{"build":"236…-a-b-cafe…","components":[{"component":"ProjectM.Health","size":32,"typeIndex":77,"stableTypeHash":"0x00000000deadbeef","bytes":"AAAAAA…"}]}}}
← {"id":45,"ok":true,"result":{"entity":"45:1","strict":true,"restored":1,"refused":0,"warnings":0,"components":[{"component":"ProjectM.Health","size":32,"restored":true}]}}
```

A snapshot whose rows all lack `bytes` is refused up front: `"args.snapshot.components has no restorable rows (every row was a tag, a buffer, or skipped)"`. A row whose `size` disagrees with its decoded base64 is `"…says size 32 but carries 4 decoded bytes; the snapshot is corrupt"`.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-snapshot-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"entity":"45:1"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server snapshot "@$bridgeArgs" > snap.json
node tools/mcp/call.mjs server restore "@restore-args.json"
```

## `players`

PlayerCharacter entities, including disabled (offline) ones. The display name is taken from `User` via `PlayerCharacter.UserEntity`.

```
→ {"id":13,"token":"...","op":"players"}
← {"id":13,"ok":true,"result":{"count":1,"players":[{"entity":"45:1","name":"TestAdmin","connected":true,"userEntity":"12:1","position":{"x":0.0,"y":0.0,"z":0.0}}]}}
```

## `status`

Snapshot of Core version, process side, uptime, and one-line dumps of the prefab registry, hosted launcher, persistence directory, and virtual systems. No args. Prefab text is `PrefabRegistry.Describe()` (`registered=N tombstones=M`) plus either each definition's id/guid/source (no world yet) or `PrefabRegistry.Describe(world, guid)` per item. Hosted text is `HostedServerLauncher.Describe()`. Persistence is `ModSave.CurrentSaveDirectory`, or `(directory not resolved yet)`. Systems text is `VirtualSystems.Describe()` (`fallback=group|bootstrap|pending`, `pluginFrames`, `groupFired`, `worldReady`, then each live slot). `instance` is the same `PROGENITOR_INSTANCE` tag as `ping`.

```
→ {"id":14,"token":"...","op":"status"}
← {"id":14,"ok":true,"result":{"version":"0.1.0","side":"server","uptimeSeconds":87,"modules":{"prefabs":"registered=1 tombstones=0; sample.ember guid=...","hosted":"Hosted game launcher\n  process: server\n...","persistence":"<testserver>\\server\\save-data\\Saves\\v4\\test1","systems":"fallback=group pluginFrames=... groupFired=True;..."},"instance":""}}
```

## `save`

Server process only. Calls `ModSave.SaveAll()` — the same flush as the game's save trigger — without waiting for autosave. `dir` is the folder written to, or `null` when the save folder has not been resolved yet.

```
→ {"id":15,"token":"...","op":"save"}
← {"id":15,"ok":true,"result":{"ok":true,"dir":"<testserver>\\server\\save-data\\Saves\\v4\\test1"}}
```

On the client process this returns `ok: false` with `"save runs on the server side only"`.

## `world_save`

Server process only, main thread. Requests a **real world save** the same way the game does, then flushes per-mod JSON so both land together. The `save` op above only calls `ModSave.SaveAll()` and never writes a `.save.gz`.

Preferred path: copy the `ServerRuntimeSettings` singleton (raw pointer copy, same as ModSave) and call `TriggerPersistenceSaveSystem.TriggerSave(reason, nameEndingInDotSave, settings)`. The interop signature is by-value (`SaveReason`, `FixedString128Bytes`, `ServerRuntimeSettings`) — no by-ref struct, so it is callable. Success queues a `SavePersistentDataEvent`; `SerializePersistenceSystemV2` writes the file later this frame or the next. Core's TriggerSave postfix already flushes JSON on a true result; this op still calls `ModSave.SaveAll()` after a successful trigger.

Fallback if that call throws: create the same one-component entity (typed `Add<SavePersistentDataEvent>`, overlay field pokes if the typed write is refused). Do not create two in one frame.

Gates from the analyze, checked when readable and named in `note` when not:

| Gate | How this op treats it |
|---|---|
| One `SavePersistentDataEvent` per frame | Counted via a non-generic query. `ok: false` / `method: none` when already queued. |
| `StartupState` CombinedState == `SuccessfulStartup` (4) | Read from the two `ServerStartupState` fields on the copied singleton (OR). Reported always. Entity fallback is skipped when not ready. |
| Terrain ready | `_TerrainManagerInitialized` on the trigger system is reported. `TerrainManager.SystemData` is **not** read here; TriggerSave still checks it. |

`fileName` (optional) is a leaf ending in `.save`. Default `WorldSave.save` (distinct from the `AutoSave_N` rotation). A trailing `.gz` is stripped because TriggerSave concatenates it when `CompressSaveFiles` is on. `reason` (optional) is `ManualSave` (default), `AutoSave`, or `QuickSave`.

```
→ {"id":26,"token":"...","op":"world_save"}
← {"id":26,"ok":true,"result":{"ok":true,"method":"TriggerSave","fileName":"WorldSave.save.gz","note":"startup=SuccessfulStartup (...) terrainFlag=True queued=0 compress=True; TriggerSave accepted (SavePersistentDataEvent queued). ModSave.SaveAll chained after the trigger. TerrainManager.SystemData is not read here."}}
```

`method` is `TriggerSave`, `SavePersistentDataEvent` (fallback), or `none` (gate closed before either path). Inner `ok: false` means the op ran and the game declined — look at `note`. On the client process this returns `ok: false` with `"world_save runs on the server side only"`.

Live check (not run from the job that added the op): `node tools/mcp/call.mjs server world_save` on the test server, then a new `WorldSave.save.gz` (or `AutoSave_N.save.gz` if you passed that name) under `save-data/Saves/v4/test1` and a `ModSave: wrote` line in the log.

```
node tools/mcp/call.mjs server world_save
$bridgeArgs = Join-Path $env:TEMP 'progenitor-world_save-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"fileName":"WorldSave.save","reason":"ManualSave"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server world_save "@$bridgeArgs"
```

## `store`

Peek or poke one mod's `IModStore` (`ModSave.Store(mod)`). `mod` is required (the file-name GUID, for example `progenitor.sample`). No `key` lists keys; `key` without `value` returns the JSON node (or a string, or `null` if missing); both set the key and return `ok`. Setting `value` without `key` is an error. `"scope": "server"` uses the wipe-surviving server store instead (`ServerSave.Store(mod)`, `<persistentDataPath>\progenitor-server`); results carry `scope` (`world` or `server`).

```
→ {"id":16,"token":"...","op":"store","args":{"mod":"progenitor.sample"}}
← {"id":16,"ok":true,"result":{"mod":"progenitor.sample","scope":"world","keys":["joins"]}}

→ {"id":17,"token":"...","op":"store","args":{"mod":"progenitor.sample","key":"joins"}}
← {"id":17,"ok":true,"result":{"mod":"progenitor.sample","key":"joins","value":{"Players":[{"PlatformId":"7656119","Name":"TestAdmin","Count":3}]}}}

→ {"id":18,"token":"...","op":"store","args":{"mod":"progenitor.sample","key":"joins","value":{"Players":[]}}}
← {"id":18,"ok":true,"result":{"ok":true,"mod":"progenitor.sample","scope":"world","key":"joins"}}
```

## `connect`

Client process only, main thread, **no world**. Builds a `ProjectM.GameConnect` the same way Direct Connect does (`ConnectAddress.CreateSteamIPv4` by default, `CreateClient = true`, `ShouldHost = false`, optional `ConnectPassword`) and calls `ProjectM.UI.GameHelper.LaunchGame`. That loads the game scene; the actual net connect happens later (`ClientBootstrapSystem.Connect` is not used here). `password` is optional. Optional `protocol` is `SteamIPv4` (default, unchanged) or `Lidgren` (`ConnectAddress.CreateLidgrenIPv4(host, port)`, the same factory as the hosted `FallbackConnectAddress`). A LanMode server only opens a Lidgren socket, so the Steam client must pass `protocol: "Lidgren"` to join the staged test server. **Extra instance** (`PROGENITOR_INSTANCE` set): Dev's `ExtraInstanceAuth` postfix rewrites a `CreateSteamIPv4` result to `CreateLidgrenIPv4` before `LaunchGame` sees it — SteamIPv4 needs Steam Networking, which the extra process does not have (evidence §19). When `protocol` is omitted the JSON result still says `protocol: SteamIPv4` because that rewrite is not in `ConnectOps`; the wire address is Lidgren. Passing `protocol: "Lidgren"` skips the Steam factory (no rewrite needed). Fake platform id and the Steam-ticket skip are described in the internal research notes.

**Address forms** (`ConnectAddressParser`; IPv4 only). `CreateSteamIPv4` takes a `FixedString32Bytes` host and does not parse, so `127.0.0.1:9976` must be split before the factory or the colon lands in the host field.

| Form | Example |
|---|---|
| IPv4 | `127.0.0.1` |
| IPv4 `host:port` | `127.0.0.1:9976` |
| Scheme | `SteamIPv4://127.0.0.1:9976` |

Hostnames and IPv6 are refused. An explicit `args.port` wins over a port split from the address; otherwise the split port, otherwise **9976** (the staged test server's game port; query `9977` is not the connect port). Empty `address`, or port `0` / `> 65535`, is an error. Other schemes (`Lidgren://`, …) are still refused — pick the transport with `args.protocol`, not an address scheme. Omitted / empty `protocol` is `SteamIPv4`; `Lidgren` is the LanMode path; anything else is an error.

**Launch-in-flight.** `LaunchGame` returns as soon as the scene load starts; `Client_0` appears later. The bridge's one-in-flight flag is cleared when that call returns, so a second `connect` / `host` in the load gap would stack scene loads. A separate process-wide slot covers that gap: set just before `LaunchGame`, cleared on client `WorldReady` or after **120 s**. A second `connect` or `host` while that slot is held is `"launch already in progress"`. The two ops share the slot.

**Collection before the load.** Every `LaunchGame`, including these ops, first runs a full .NET collection and logs `GcBeforeLaunch: collected before LaunchGame in <ms> ms (...)`. Expect that delay before the scene load starts. `[Diagnostics] CollectBeforeWorldLoad=false` turns it off. See [Collect before world load](modules.md#collect-before-world-load); not verified live.

Fails when this is a server process, or when `Progenitor.Worlds.Client` is already non-null (`"already in a game"`). Join is async: the result returns as soon as `LaunchGame` is invoked. Watch `ping` for `Client_0` in `worlds`, or `status`.

```
→ {"id":19,"token":"...","op":"connect","args":{"address":"127.0.0.1","port":9976}}
← {"id":19,"ok":true,"result":{"ok":true,"address":"127.0.0.1","port":9976,"protocol":"SteamIPv4","note":"LaunchGame invoked; join is async. Watch ping.worlds for Client_0."}}

→ {"id":19,"token":"...","op":"connect","args":{"address":"127.0.0.1:9976","protocol":"Lidgren"}}
← {"id":19,"ok":true,"result":{"ok":true,"address":"127.0.0.1","port":9976,"protocol":"Lidgren","note":"LaunchGame invoked; join is async. Watch ping.worlds for Client_0."}}
```

Also valid: `"address":"127.0.0.1:9976"` (no `port` key), or `"address":"SteamIPv4://127.0.0.1:9976"`. On the server process this returns `ok: false` with `"connect runs on the client side only"`. Already in a world: `"already in a game"`. Launch already in progress: `"launch already in progress"`. Unknown `protocol`: `"args.protocol must be SteamIPv4 or Lidgren"`.

## `host`

Client process only, main thread, **no world**. Builds a `ProjectM.GameConnect` the same way Private Game / Host New does (`ShouldHost = true`, `CreateClient = true`, `HostDedicated = false`, `ServerSaveName`, loopback `ConnectAddress.CreateSteamIPv4("127.0.0.1", HostPort)`, optional `ConnectPassword`) and calls `ProjectM.UI.GameHelper.LaunchGame`. Shares the **connect launch-in-flight slot**: a second `host`/`connect` while the scene is loading is `"launch already in progress"`. `GameConnect` has no LAN field: `lan` (default `true`) and the server password are poked onto in-memory `SettingsManager.ServerHostSettings` (`LanMode`, `Password`, `SaveName`, `PrivateGame`, `Port`) and `GameClientSettings.IsPrivateGame`. That persist-to-disk path is unverified; the op does not call `SaveToPath`. Default `saveName` is `progenitor-hosted`. Re-using a name continues that world if the folder already exists. `HostPort` comes from `ClientHostSettings.Port` (shipped default 9876), not the test-server 9976. `HostMaxUsers` from `ServerHostSettings.MaxConnectedUsers` (fallback 10); `HostFallbackPort` from `ServerHostSettings.FallbackPort` (0 if unreadable).

Fails when this is a server process, or when `Progenitor.Worlds.Client` is already non-null (`"already in a game"`). Spawn and join are async: the result returns as soon as `LaunchGame` is invoked. Watch `ping` for `Client_0`. Core's `HostedServerLauncher` Start prefix then stages BepInEx into the bundled server folder.

```
→ {"id":21,"token":"...","op":"host","args":{"saveName":"progenitor-hosted","lan":true}}
← {"id":21,"ok":true,"result":{"ok":true,"saveName":"progenitor-hosted","note":"LaunchGame invoked (ShouldHost); child server spawn and join are async. Watch ping.worlds for Client_0. lan=True hostPort=9876 password=no."}}
```

On the server process this returns `ok: false` with `"host runs on the client side only"`. Already in a world: `"already in a game"`. Empty `saveName` uses the default. A name with path characters, `..`, or longer than 64 characters is an error.

`host` remains unverified for completing a hosted-game launch. If `GameBootstrap.Start` fails, capture an authorized production Private Game launch through `host_capture` and compare its inputs.

## `host_capture`

Client process only, main thread, **no world required**. Bootstrap-owned prefixes on `ProjectM.UI.GameHelper.LaunchGame(GameConnect)` and `ProjectM.GameBootstrap.Start` (Dev fills the slots) that dump every `GameConnect` field, `SettingsManager.ServerHostSettings` / `ServerGameSettings` (null vs value), named static holders (save name, difficulty preset, `ConnectingView._GameConnect`), and every field of the `GameBootstrap` instance about to run `Start`. That is the capture for the hosted-launch NRE: the real Private Game / Host New click is the remaining ground truth.

Installed **by default** when the Dev plugin loads on the client. Cost is a log dump per call; both methods run once per launch. `GameBootstrap.Start` is a public no-arg wrapper over native `Start_Private_Void_0` (no by-ref struct). Bootstrap patches the IL2CPP method pointer lazily on first bind; if that trampoline is missing, the op falls back to `GameBootstrap.Awake` and reports `startPatchable: false`. Live confirmation is a `GameBootstrap.Start prefix` line in the capture file after you click Host.

Every line is written twice: BepInEx log with prefix `host_capture:`, and append to `%TEMP%\progenitor-host-capture.txt` (UTC timestamp + sequence). Filter the in-memory ring with `logs` `{filter:"host_capture"}`.

**How to use it (diff the real menu against the bridge `host` op):**

1. Launch the Steam client with Progenitor.Dev loaded (or `tools/launch-client.ps1 -Connect ""` so it only waits for the bridge). Stay on the main menu.
2. Prefixes are already installed. Optional: `client host_capture` with arguments `{"ops":"dump"}` (using the [JSON-file calling pattern](bridge.md#calling-without-mcp)) to snapshot menu state before the click.
3. In the game, click **Play → Private Game → Host** (Host New, pick a name / difficulty, start). Do not use the bridge `host` op for this run.
4. Read `%TEMP%\progenitor-host-capture.txt` and/or `progenitor_logs` with `filter: host_capture`. You want the `GameHelper.LaunchGame prefix` block (every `GameConnect` field the UI actually passed) and the `GameBootstrap.Start prefix` block (instance fields + settings at the moment Start runs — or the NRE, if it still throws).
5. Quit back to the menu (or relaunch). Run the bridge `host` op (`client host` with arguments `{"saveName":"progenitor-hosted","lan":true}` (using the [JSON-file calling pattern](bridge.md#calling-without-mcp))). The same prefixes fire again.
6. Diff the two captures: `GameConnect` fields, `ServerHostSettings` / `ServerGameSettings` nulls and primitives, `GameDifficultyPreset`, `GameBootstrap.ServerHostData` / `ClientCreateData` / `DifficultySettingPresets`. The first mismatch is the next `host` op fix.

Args:

| Key | Meaning |
|---|---|
| `enable` | `true` install prefixes, `false` remove them. Already on at client load. |
| `ops` (or `action`) | `dump` — write the settings + `FindObjectOfType<GameBootstrap>` / `ConnectingView` snapshot now, without launching. |

Empty args dump. `{enable:true}` / `{enable:false}` only change installation. Both can be sent together (`enable` first). Server process: `"host_capture runs on the client side only"`.

```
→ {"id":26,"token":"...","op":"host_capture","args":{"ops":"dump"}}
← {"id":26,"ok":true,"result":{"ok":true,"installed":true,"startPatchable":true,"startPatched":true,"awakePatched":false,"launchGamePatched":true,"capturePath":"%TEMP%\\progenitor-host-capture.txt","dumped":true,"dumpLines":80,"note":"…"}}
```

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-host_capture-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"ops":"dump"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client host_capture "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-host_capture-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"enable":false}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client host_capture "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-host_capture-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"enable":true}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client host_capture "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-logs-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"filter":"host_capture","max":200}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client logs "@$bridgeArgs"
```

There is no MCP wrapper for `host_capture`; use `call.mjs`.

## `capture`

Client process only, main thread, **world required**. An observation window over the game's own effect draw: instead of inferring the mechanism from the game's metadata, take a baseline, let the player trigger the effect, and read back what actually appeared.

`{"action":"start","seconds":10,"filter":"beam|sequence|line","maxEntities":2000}` snapshots

- every entity carrying one of `ProjectM.Sequencer.SpawnSequenceForEntity`, `Stunlock.Sequencer.SequenceInstance`, `ProjectM.SequenceGUID`, `ProjectM.BuffSequence`, `ProjectM.Sequencer.BuffSequenceSpawned`, `ProjectM.Sequencer.LineRendererPositionElement`, `ProjectM.PreviewPlacementSequence`, `ProjectM.PreviewPlacementBuff`, `ProjectM.ProfessorCoilBeam_Client`, `ProjectM.ProfessorCoilBeam_State_Shared` (one cached `AddAny` query, disabled entities included);
- every GameObject already under `ParticleLifetimeSystem._ParticleParent` (the sequencer's pool parent), plus every `LineRenderer` and `HybridLineRendererComponent` in the scene.

It then records, per frame, entities that appeared or disappeared (`index:version`, prefab guid + name, component type names, and a field dump of the endpoint-bearing components: `SpawnSequenceForEntity.SequenceGuid/Target/SecondaryTarget/SequenceInstance`, `SequenceInstance.Target/SecondaryTarget`, `ProfessorCoilBeam_State_Shared.Start/End`, `PreviewPlacementSequence.Sequence/ValidPlacement`, `LineRendererPositionElement` buffer entries, `Translation`), and GameObjects that appeared under the particle parent (name, active, parent name, component names, `LineRenderer` positionCount / positions / width / start+end color / material shader, `ParticleSystem` isPlaying + particleCount, `HybridLineRendererComponent.Positions`).

**Inspect `changed` as well as `appeared`.** Sequencers reuse particle and line objects through pooling. Activation, reparenting and state changes can identify an effect even when no new GameObject appears. A capture with no appearances does not prove that the visual failed. Read the entity/object changes, truncation flags and screenshot together.

| `kind` | When |
|---|---|
| `activated` / `deactivated` | `activeInHierarchy` flipped. An object that was **inactive at baseline** and activates — the pooled-clone case — carries its full `description` at activation time |
| `reparented` | the parent instance id changed; also emitted for an object that **left the sequencer's pool parent**, which is the pooled spawn (the pool parks *idle* clones and reparents a *live* one out, so a departure is a take and an arrival is a return — the first version of the op had this backwards). Carries a `description` so the new parent has a name |
| `lineMoved` | a `LineRenderer` or hybrid position moved more than 1 cm; also appends a sample to that object's `lineSamples` series |
| `particleStarted` / `particleStopped` | `ParticleSystem.isPlaying` flipped |

Read `summary.changedObjects[{name,kinds[],count}]` first on any pooled effect: `gameObjects` (the "appeared" list) is empty there by construction.

`start` returns immediately; the window runs on `Plugin.Frame` and is **not** bound by the 30 s socket timeout.

**Cost and caps: three tiers, three budgets, and a floor under each.** Every tier measures *its own* cost, amortizes against the **2 ms** budget with *its own* interval, and can never switch another tier off:

| Tier | What | Interval | Skip counter |
|---|---|---|---|
| 1 | the cached entity query and diff | every frame, widening to at most every 30th when its own cost exceeds the budget | `skippedEntityDiffs` |
| 2 | the particle-parent walk (depth 4; **256** direct children, **512** nodes total) | every frame, widening to at most every 30th | `skippedGameObjectWalks` |
| 3 | the scene sweep — two `FindObjectsOfType` calls, 5–30 ms in a loaded client — carrying the position samples (max **50** per object, max **40** objects), the endpoint re-read of up to **32** tracked entities, and the whole comparison pass | every **10th** frame at most, widening to at most every 120th (`budget.sampleInterval`) | `skippedSceneScans`, `skippedComparisons` |

The sweep uses a running whole-frame cost budget, but the pool walk runs at least every 30 frames and the scene sweep every 120. Forced work is counted in `budget.forcedScans` / `forcedWalks`. A sweep also stops at its own deadline (10 budgets, 20 ms) and increments `capture.scanDeadlineStops`. Interpret missing observations together with these counters.

The running average starts at half the budget, so an expensive first scan does not suppress the initial observation window.

Comparing one tracked object is a dictionary lookup and a few scalar tests — no LINQ, reused arrays, and the only string read per pass is the object's own name; before/after text is formatted only when a transition is recorded. (Filling the scratch on the live side does cost about a dozen interop calls and one marshaled name string per object, which is why tier 3 has that deadline.)

**Truncation.** Rows are capped at 500 appeared, 500 disappeared, 200 objects and 400 changes. On entity overflow, the stable subset is sorted by `index:version` and disappearance rows are suppressed because partial scans cannot establish absence. Scene sweeps retain the lowest 400 instance ids. An incomplete pool walk is not diffed. Inspect `capture.entityOverflow`, `capture.truncatedFrames`, `capture.scanOverflow` and `capture.incompleteWalks`; hitting a cap sets `truncated`.

Skipping at least one quarter of the sweeps also sets `truncated`; inspect `summary.sweepSkipRatio` and `summary.sweepsSkipped`.

**Identity.** Captures retain entity `index:version` and GameObject identity data (instance id, initial parent and first-seen frame), not IL2CPP wrappers. A parked object activated beneath a new parent starts a new tracked life; a deactivate/reparent across a sufficiently long observation gap can indicate a recycled id. Check `tracking.rekeyed` and closed sample series when interpreting pooled effects.

Deactivate-and-reparent counts as recycled identity only after more than 4 unseen comparison passes (`CaptureObjectIdentity.RecycleUnseenPasses`). A normal return to pool remains the same identity going quiet; the next hand-out creates a new life. A different non-empty name also establishes replacement. Comparison passes are not frame counts because scans can be skipped.

**Sampling saturation.** The 40 sampling slots and 40 live series rotate; the stalest entry can yield to a new one. Inspect `summary.samplingSaturated`, `capture.sampling{count,saturated,rotations}` and `capture.seriesRotations` when comparing timelines.

The window stops on Dev plugin unload.

`filter` is a `|`-separated list of case-insensitive substrings matched against a new GameObject's name, its parent's name, and its component type names. It never filters entities — the interest component set already does that. Pass `""` to record every new object.

`{"action":"stop"}` closes the window, writes `BepInEx/progenitor-capture/<yyyyMMdd-HHmmss-fff>.json` (a **sibling** of the reload folder, so the reloader never scans it), and returns the path plus the summary. If the **write** fails the summary still comes back, with `path: null` and `writeError` naming the failure — a full disk must not lose the capture as well. Stopping after the window already closed itself returns that finished report instead of failing. `{"action":"status"}` returns `running`, `elapsedSeconds`, `remainingSeconds`, `frames`, `truncated`, `sweepSkipRatio`, `samplingSaturated`, the three tier intervals, live counts, and the last report's path + summary.

Report file layout:

| Key | Contents |
|---|---|
| `capture` | `side`, `world`, `startedUtc`, `requestedSeconds`, `elapsedSeconds`, `filter`, `maxEntities`, `stopReason` (`stop` / `deadline` / `world-gone` / `unload`), `truncated`, `truncatedFrames`, `truncatedBySkippedSweeps`, `entityOverflow`, `scanOverflow`, `incompleteWalks`, `scanDeadlineStops`, `sampling{max,count,saturated,saturatedRegistrations,rotations}`, `seriesSaturated`, `seriesRotations`, `seriesRefused`, `baseline{entities,gameObjects}`, `budget{budgetMs,frames,skippedGameObjectWalks,skippedSceneScans,skippedComparisons,skippedEntityDiffs,overBudgetFrames,averageMs,runningAverageMs,maxMs,totalMs,scans,averageScanMs,maxScanMs,sampleInterval,entityDiffs,averageEntityDiffMs,maxEntityDiffMs,entityDiffInterval,walks,averageWalkMs,maxWalkMs,walkInterval,sweepsDue,sweepSkipRatio,sweepSkipRatioExceeded,forcedScans,forcedWalks}`, `tracking{maxTracked,tracked,dropped,retired,rekeyed,passes,comparisons,transitions}` |
| `entitiesAppeared` / `entitiesDisappeared` | `entity`, `frame`, `atSeconds`, `prefab{hash,name}`, `components[]`, `endpoints{component:{field:value}}`, `sequenceGuids[]` |
| `gameObjects` | `instanceId`, `name`, `active`, `parent`, `source` (`particleParent` / `lineRenderer` / `hybrid`), `frame`, `atSeconds`, `components[]`, `line{positionCount,positions,startWidth,endWidth,startColor,endColor,shader,useWorldSpace}`, `particle{isPlaying,particleCount}`, `hybridPositions[]` |
| `gameObjectChanges` | one row per transition of a tracked object: `instanceId`, `name`, `frame`, `atSeconds`, `kind` (`activated` / `deactivated` / `reparented` / `lineMoved` / `particleStarted` / `particleStopped`), `before`, `after`, and `description` (a full `gameObjects`-shaped row) on a pooled activation or a re-parent |
| `lineSamples` | one series per sampled LineRenderer: `instanceId`, `name`, `moved`, `sampleCount`, `truncated`, `retired` (its instance id was recycled), `samples[{frame,atSeconds,positions[]}]` |
| `endpointTracks` | one row per entity/component/field: `first`, `last`, `samples`, `changed` |

The `summary` the op returns is `frames`, `elapsedSeconds`, `stopReason`, `truncated`, `sweepSkipRatio`, `sweepsSkipped`, `samplingSaturated`, `counts{…}`, `sequences[{guid,frame,prefabs[],gameObjects[]}]` (each new sequence guid with the prefab of the entity that carried it and the GameObjects that appeared within one sampling interval plus four frames — narrower than that and a `lineRenderer`-sourced row, whose frame is always a multiple of the interval, missed its own sequence on half the phases), `endpoints[{component,entities,fields,changedFields,changed}]`, `lines[{name,samples,moved}]`, and `changedObjects[{name,kinds[],count}]`.

**Workflow.**

1. `client capture` with arguments `{"action":"start","seconds":15}` (using the [JSON-file calling pattern](bridge.md#calling-without-mcp)) (or `progenitor_capture`). Check `types.missing` is empty and `baseline` is non-zero.
2. Ask the user to trigger the vanilla effect **now**: build mode over invalid ground (two red beams), feed on an enemy (red feed beam), or a charm / subdue channel.
3. `client capture` with arguments `{"action":"status"}` (using the [JSON-file calling pattern](bridge.md#calling-without-mcp)) while it runs if you want counts.
4. `client capture` with arguments `{"action":"stop"}` (using the [JSON-file calling pattern](bridge.md#calling-without-mcp)) and read the returned `path`.

Read the report as: `summary.sequences[].guid` → its `prefabs[]` (what the sequence belongs to) → its `gameObjects[]` (the clone the sequencer produced) → `entitiesAppeared[].endpoints` (which component carries the endpoints) → `endpointTracks` / `lineSamples[].moved` (whether the client rewrites them each frame or writes once). If `gameObjects` is empty — the normal case for a warm pool — read `summary.changedObjects[]` instead and follow the `activated` row's `description` to the clone. The internal research notes walk that chain with the shapes to expect.

```
→ {"id":27,"token":"...","op":"capture","args":{"action":"start","seconds":10}}
← {"id":27,"ok":true,"result":{"running":true,"world":"Client_0","seconds":10,"filter":"beam|sequence|line","baseline":{"entities":41,"gameObjects":118},"types":{"resolved":["ProjectM.Sequencer.SpawnSequenceForEntity","…"],"missing":[]},"note":"trigger the effect now, …"}}
→ {"id":28,"token":"...","op":"capture","args":{"action":"stop"}}
← {"id":28,"ok":true,"result":{"running":false,"path":"C:\\…\\BepInEx\\progenitor-capture\\20260903-190405-678.json","summary":{"frames":612,"counts":{…},"sequences":[{"guid":586432528,"prefabs":["AB_Cardinal_SummonOrb_Beam_Buff"],"gameObjects":["Beam_Cardinal(Clone)"]}],"endpoints":[{"component":"SpawnSequenceForEntity","entities":1,"changed":true,"changedFields":["Target"]}],"lines":[{"name":"Beam_Cardinal(Clone)","samples":50,"moved":true}]}}}
```

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-capture-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"start","seconds":15}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client capture "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-capture-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"status"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client capture "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-capture-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"stop"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client capture "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-capture-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"start","seconds":20,"filter":"","maxEntities":4000}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client capture "@$bridgeArgs"
```

On the server process this returns `ok: false` with `"capture runs on the client side only"`. A second `start` while one is running is an error; `stop` first.

## `perf`

**Freeze diagnostics:** start a capture before reproducing the slowdown with MCP
`progenitor_perf {"side":"client","action":"start","seconds":120}`.
Poll `progenitor_perf {"side":"client","action":"live"}` about once per second.
This uses the socket-thread `perf_status` op; it does not queue gameplay work, walk
the live recording, or take the lock `start` / `stop` / ordinary `status` hold while
they snapshot or uninstall. Bypassing the main-thread queue is not a response
deadline: the read still waits on a short private progress lock and the report-write
slot lock (not Unity APIs, not the file write).

`perf_status` returns `running`, `historical`, `captureId`, `progress`, and report-write state:

| Field | Meaning |
|---|---|
| `running` | true while this capture is open |
| `historical` | true after deadline/stop/unload/core-swap, until the next capture or Dev reload |
| `captureId` | monotonic id for this capture; 0 before any capture |
| `progress` | null before any capture; otherwise `frames`, `sampleSilenceMs`, `lastFrameMs`, `lastCoreTickMs`, `maxFrameMs` |
| `reportState` | `none` / `writing` / `completed` / `failed` for the latest close |
| `writeError` | retained error when `reportState` is `failed`; otherwise null |
| `closeMs` / `finalizeMs` | main-thread snapshot+uninstall, then background Summary+JSON+write |

Before any sample, the frame measurements are null. A new `start` allocates a new
`captureId` and resets metrics before the first publication. After completion the
same scalars stay available as historical (`running=false`, `historical=true`,
`captureId`); a late frame from the finished capture cannot update them or a newer
capture.

Growing silence while `running=true` means samples have stopped arriving; it does
not identify the blocked function or prove a freeze. Correlate with `bridge_status`
and the freeze dump. A long frame interval is measured only when ticks resume;
if the capture closes during the stall, that interval is omitted from the ring.
The finished report records `sampleSilenceMsAtClose` and `endedDuringSilence` so
that case is not mistaken for a clean run that simply had no long frames. When
`historical=true`, silence continues to age and is not a current liveness signal.
`live` also reports `reportState` (`none` / `writing` / `completed` / `failed`) so
the socket path can tell a still-finalizing report from a finished or failed write
without waiting for the main thread. `start`, `stop`, and ordinary `status` still
require the main thread: do not retry them after a timeout. These are CPU-side
measurements, not GPU timings or native stack attribution. The socket progress path
is built and offline-tested; live freeze acceptance remains to be run on the
isolated pair.

**Either side** (written for the server, works on the client), main thread, **no world required**. What does Progenitor cost this process, per frame, without attaching a profiler — and *which module* costs it.

`{"action":"start","seconds":30}` opens a window that samples **every frame**:

| Sampled | From |
|---|---|
| frame delta (ms) | the gap between consecutive Core ticks; `Plugin.FrameTick` de-dupes on `Time.frameCount`, so that gap is the Unity frame |
| Progenitor's own main-thread work, split by source | `Stopwatch` around each dispatched callback in Core's three funnels (below) |
| main-thread queue depth + delayed-queue depth | the live `Progenitor.Bootstrap.WorkQueue` collections, bound once reflectively and then read as an `ICollection.Count`. **Bootstrap only**: falling back to Core's `Progenitor.Dev.MainThread` meant the sink held a *Core* static collection for the capture's life, which is the collectible-ALC pin this whole op is careful about. A process without Bootstrap reports `queueSource: unavailable` and depth `-1` |
| GC allocated bytes per frame | `GC.GetAllocatedBytesForCurrentThread` delta, probed once at `start`; a runtime without it degrades to a `GC.GetTotalMemory(false)` delta (the report's `allocApi` says which) |
| managed heap size | `GC.GetTotalMemory(false)` |

**The timing seam.** Core carries one hook for this, `src/Progenitor.Core/Dev/PerfProbe.cs` — a single nullable sink slot. With nothing installed (every shipped build) each dispatch point pays one static field read and a branch, and `Plugin.Frame` keeps its ordinary `Frame?.Invoke()` shape. Three funnels report through it, because between them they carry every per-frame Progenitor cost and they are the last places that still know *whose* work it is:

| Funnel | Gives |
|---|---|
| `Plugin.Tick` (`Plugin.cs`) | one timing per `Plugin.Frame` subscriber, bucketed by the subscriber's declaring type |
| `Hooks.SystemHooks.Invoke` | one timing per plugin system hook, bucketed by plugin id |
| `Events.EventBus.Publish` | one timing per event handler, bucketed by plugin id |

A Dev-side wrapper was not an option for the first of those: `Plugin.Frame` is an `event Action`, so decorating it would mean unsubscribing each handler and substituting our own, which breaks every `Plugin.Frame -= OnFrame` in Core and puts a Dev delegate where `Plugin.RemoveFrameHandlers` cannot drop it — a guaranteed collectible-ALC pin.

**Source names.** A Core subscriber is named after its module (the first namespace segment, which is the source tree): `core:effects` is the ServerBeam keep-alive poll, `core:units` the unit arrival poll, `core:content` the prefab refresh / edit flush / handshake watch / apply retry, `core:networking/<Type>.<Method>` one source per networking handler (the join screens' frame steps `JoinFlowClient.FrameOpen`, `FrameBatch`, `FrameReconnect`, `FrameLastServer`, `FrameTurnOns`, `FrameVersions`, then `JoinFlowPanel.OnFrame`, the deny panel's sweep, the manifest and kick watches; split since one of them was 78% of Progenitor's frame time on 2026-10-06), plus `core:events`, `core:hooks`, `core:systems`, `core:hosted`. A subscriber from any other assembly is `plugin:<assembly>` (`plugin:Progenitor.Dev`, `plugin:Progenitor.Sample`). System hooks and events are `hook:<pluginId>` / `event:<pluginId>` (`hook:core` for Core's own). At most 24 sources; the 25th and later land in `other` and set `sourceOverflow`.

**What it does not see.** Bootstrap drains the main-thread queue (`ProcessHost.Tick` → `WorkQueue.Drain`) **before** Core's tick, and Bootstrap is not instrumented, so drain execution time is not attributed to anything. `queueDepth` is the proxy for it, and every report and summary repeats that in `note`. `coreTickMsPerFrame` minus the sum of the sources is Core's own dispatch overhead. Networking *sends* are attributed to whichever handler triggered them, not to a bucket of their own.

**Cost and caps.** The profiler caches source classification and preallocates frame buffers. Captures retain at most 7,200 frames; longer/faster runs wrap, report `wrapped:true`, and distinguish `frames` from `framesRecorded`. Hook/event detail is capped at 64 rows with truncation flags. `seconds` is clamped to 1–300. Interpret allocation measurements with the capture overhead and workload in mind.

`start` returns immediately and the window is **not** bound by the 30 s socket timeout. `{"action":"stop"}` snapshots the recording, uninstalls the sink, and returns immediately with `reportState: "writing"`. `Summary()`, JSON serialization and `File.WriteAllText` run on a dedicated background thread (`progenitor-perf-write`). The file is `BepInEx/progenitor-perf/<yyyyMMdd-HHmmss-fff>.json` (a **sibling** of the reload folder, like `progenitor-capture`, so the reloader never scans it). Poll `status` or socket-thread `live` until `reportState` is `completed` or `failed` — do not infer completion from the file appearing. A failed write retains `writeError` and the summary when `Summary()` succeeded, with `path: null`, instead of losing the capture. Stopping after the deadline already closed the window returns that report's current `reportState` instead of failing. `{"action":"status"}` returns `running`, `reportState`, `elapsedSeconds`, `remainingSeconds`, `frames`, `wrapped`, the source list, `installedGeneration` / `coreGeneration`, `watchdog`, `closePending` (a close the watchdog asked for that the main thread has not run yet), `captureId`, `closeMs` / `finalizeMs`, `endedDuringSilence`, and the last report's path.

**Capture lifetime is owned by Dev.** Its deadline must close the window even if Core has no world, stops dispatching or swaps generations. A Core swap closes that generation's capture rather than silently continuing against an unobserved dispatcher.

So a Dev-owned timer polls twice a second (`PerfOps.CloseIfDue`, decided by `PerfWindowPolicy` so the frame path and the timer agree), and `PerfProbe` carries two facts for it to poll: a `Generation` that moves on every install, and a `CoreUninstalled` flag that Core's `Plugin.Uninstall` latches through `PerfProbe.NoteCoreUninstall()`. Any of *"Core said it is going away"*, *"the generation moved"* or *"the installed sink is not ours"* closes the window with **`stopReason: "core-swap"`** and writes the partial report (`perf.partial: true`), rather than leaving a wedged op behind. Dev unload and a Core swap dispose that generation's timer and sink through an idempotent tracked disposable so no static keeps a delegate into the old Core. A Core swap is closed on the watchdog thread immediately (Core is gone, so nothing is writing); other off-thread closes still wait for the recording to go quiet.

**The timer does not serialize mutable game state.** Finalization follows this ownership sequence:

- The timer only **asks** (`PerfCloseGate.Request`), latching the first reason so a `core-swap` it noticed is not later relabeled `deadline`. `status` reports the pending reason as `closePending`.
- The **close thread** (the game main thread from the next `Sink.CoreTick` or `perf` op entry, or the watchdog when the recording has been quiet for 2 seconds) snapshots the ring buffers, source table and detail dictionaries under `PerfCloseGate.RecordGate` (`PerfRecording.Snapshot`) and uninstalls the sink. That portion is bounded: a handful of `Array.Copy` calls plus uninstall. `stop` from the bridge takes the same path.
- `Summary()`, JSON serialization and `File.WriteAllText` run on a dedicated background thread (`progenitor-perf-write`), matching the freeze-dump writer. Snapshotting does not itself move that work off the close thread; the background step does. `reportState` is `writing` until that thread finishes, then `completed` or `failed` with a retained `writeError`. Poll `status` or socket-thread `live` (`perf_status`) for those fields — do not infer completion from the file appearing.
- A later `start` is refused while `reportState` is `writing`, so two captures cannot share or overwrite one report identity. Unload detaches the writer: the file may still land, but the callback does not publish into a new capture. This is a diagnostic close-path fix, not an explanation of any observed freeze.

Report file layout:

| Key | Contents |
|---|---|
| `perf` | `side`, `world`, `startedUtc`, `requestedSeconds`, `elapsedSeconds`, `stopReason` (`stop` / `deadline` / `unload` / `core-swap`), `partial`, `frames`, `framesRecorded`, `recordedSeconds`, `capacity`, `wrapped`, `sources[]`, `sourceOverflow`, `allocApi`, `queueSource`, `note`, `captureId`, `closeMs` (main-thread snapshot + uninstall only), `sampleSilenceMsAtClose`, `endedDuringSilence`, `lastFrameMs` |
| `frame` | `deltaMs{p50,p95,p99,max,mean,samples}`, `meanFps` |
| `progenitor` | `msPerFrame{…}`, `coreTickMsPerFrame{…}`, `sharePercent` (of total frame time), `scopeNote` — `sharePercent` is Progenitor cost per *frame*, not a share of Core's tick: hook and event samples are dispatched from the game's own system update, outside the `Plugin.Frame` loop `coreTickMs` measures, so a per-source row can legitimately exceed it  |
| `sources` | one row per source, heaviest total first: `name`, `p50Ms`, `p95Ms`, `maxMs`, `meanMs`, `totalMs`, `shareOfProgenitorPercent`, `shareOfFramePercent` |
| `hooks` | one row per plugin id + system + before/after: `calls`, `totalMs`, `maxMs` |
| `events` | one row per plugin id + event type: `calls`, `totalMs`, `maxMs` |
| `mainThread` | `queueDepth{…}`, `delayedDepth{…}`, `drainTimed: false`, `note` |
| `gc` | `api`, `allocatedBytes`, `allocatedBytesPerSecond`, `bytesPerFrame{…}`, `heapBytes{…}` |
| `worstFrames` | the five heaviest frames: `frame`, `atSeconds`, `frameMs`, `progenitorMs`, `coreTickMs`, `queueDepth`, `allocBytes`, and `sources{name: ms}` — the breakdown that made that frame heavy |
| `il2cpp` | the IL2CPP (Boehm) heap: `available`, `api`, Unity's `incremental` / `incrementalTimeSliceNs` / `gcMode` read at start, `gcCount`, `usedBytes{…}` (`il2cpp_gc_get_used_size` at each Core tick), `usedBytesFirst/Last`, `heapBytesFirst/Last`, `growthBytesPerSecond` (used-size growth on frames without an IL2CPP collection), `attributedBytesPerSecond`, `bytesPerSecondBySource{name: B/s, …, outside: B/s}`, `gcFrames[]` (every frame in which **any** collector ran: `atSeconds`, `frameMs`, `kinds` = `il2cpp` / `clrGen0` / `clrGen1` / `clrGen2`, `il2cppUsedBefore/After`, `largestGap`), `gcFramesTotal`, `gcFramesTruncated`, `gcFrameMs{…}`, `note` |
| `clrGc` | CLR collections during the window: `gen0`, `gen1`, `gen2` |
| `patches` | timed bodies of Progenitor's Harmony prefixes/postfixes and Bootstrap slot callbacks on game methods (`SystemBase.Update.prefix/postfix`, `UnmanagedUpdate.prefix/postfix`, `LateFrame`, …): `calls`, `callsPerFrame`, `totalMs`, `meanMsPerFrame`, `maxFrameMs`. The Il2CppInterop trampoline around each body is not inside the timing; `calls` is how many there were |
| `seams` | `firedFrames{seam: frames}`, `hooked` (what the capture hooked), `note` |
| `slowestFrames` | the ten frames with the longest **frame interval** (not the most Progenitor time): `frameMs`, `progenitorMs`, `patchMs`, `outsideMs` (frame minus both), `coreTickMs`, `gc[]` (collectors that ran in that frame), `il2cppGrowthBytes`, `allocBytes`, `queueDepth`, `largestGap{from, to, ms}`, `windowMs`, `seamsMs{seam: ms from the previous Core tick's end}`, `sources{…}` |

**Seams and collectors (IL2CPPGC1).** While a capture runs, the op hooks Before/After the side's bootstrap system (`ClientBootstrapSystem` / `ServerBootstrapSystem`), `SimulationSystemGroup` and `PresentationSystemGroup`, stamps Bootstrap's late-frame slot from Core, and posts a stamp to the main-thread queue each frame so the next `WorkQueue.Drain` marks `bootstrapTick.drain` (the start of Bootstrap's frame tick: drain, `CoreWatch`, the plugin scan, then Core's tick). Each frame's window runs from the previous Core tick's end to this Core tick's start; `largestGap` names the two consecutive seams with the most time between them, so an 80 ms frame says whether it sat inside simulation, inside presentation, between the late frame and the next drain (rendering, present, vsync), or between the drain and Core's tick (Bootstrap's own work). Per frame the sink also reads `il2cpp_gc_get_used_size` and `il2cpp_gc_get_heap_size` (direct exports, no allocation), `Il2CppSystem.GC.CollectionCount(0)` (one boxed int per frame on the IL2CPP heap, the probe's own overhead) and `GC.CollectionCount(0..2)`. Around every timed callback it reads the used size again, which attributes IL2CPP bytes to a source. The used size moves in heap-block steps, so per-source bytes are estimates that mean something summed over a window, and `outside` is everything no timed callback was running for. Built and unit-tested offline; not yet live-verified.

The `summary` published when `reportState` becomes `completed` or `failed` is `frames`, `framesRecorded`, `elapsedSeconds`, `stopReason`, `partial`, `wrapped`, `frameMs{…}`, `progenitorMs{…}`, `progenitorSharePercent`, the top 8 `sources[]`, `allocBytesPerSecond`, `heapBytes`, `queueDepth{…}`, `worstFrame` (the heaviest one with its per-source breakdown), `il2cpp` (as in the file, with the first 12 `gcFrames` and the top 8 sources plus `outside`), `clrGc`, `patches`, `slowestFrames` (the five slowest, without `sources`), `captureId`, `closeMs`, `finalizeMs` (background Summary + JSON + file write), `sampleSilenceMsAtClose`, `endedDuringSilence`, and `lastFrameMs`. `stop` itself returns `reportState: "writing"`; poll for the summary.

```
→ {"id":40,"token":"...","op":"perf","args":{"action":"start","seconds":30}}
← {"id":40,"ok":true,"result":{"running":true,"side":"server","world":"Server","seconds":30,"capacity":4564,"captureId":1,"allocApi":"GC.GetAllocatedBytesForCurrentThread","queueSource":"Progenitor.Bootstrap.WorkQueue","note":"sampling every frame; …"}}

→ {"id":41,"token":"...","op":"perf","args":{"action":"stop"}}
← {"id":41,"ok":true,"result":{"running":false,"reportState":"writing","captureId":1,"closeMs":0.4,"note":"sampling stopped; the report is being written on a background thread. poll status or live until reportState is completed or failed"}}

→ {"id":42,"token":"...","op":"perf","args":{"action":"status"}}
← {"id":42,"ok":true,"result":{"running":false,"reportState":"completed","captureId":1,"path":"C:\\…\\BepInEx\\progenitor-perf\\20260903-190405-678.json","closeMs":0.4,"finalizeMs":12.8,"endedDuringSilence":false,"summary":{"frames":1803,"frameMs":{"p50":16.7,"p95":18.2,"max":41.9,"mean":16.63,"samples":1803},"progenitorMs":{"p50":0.031,"p95":0.09,"max":2.41,"mean":0.04,"samples":1803},"progenitorSharePercent":0.24,"sources":[{"name":"core:content","p50Ms":0.012,"p95Ms":0.04,"maxMs":2.1,"totalMs":38.4,"shareOfProgenitorPercent":52.1}],"allocBytesPerSecond":41230.0,"worstFrame":{"frame":902,"frameMs":41.9,"progenitorMs":2.41,"sources":{"core:content":2.1,"hook:progenitor.sample":0.31}}}}}
```

**Workflow.** Start it, let the thing you care about happen (a player joining, a `.beam`, a hot reload, plain idle), stop it, then read `summary.sources[]` — if `progenitorSharePercent` is small and no source stands out, the framework is not what is costing the frame. `worstFrame.sources` is the first place to look for a hitch inside Progenitor's tick; for a hitch outside it, read `slowestFrames[].largestGap` and `gc`, then `il2cpp.gcFrames` and `il2cpp.bytesPerSecondBySource`. And `mainThread.queueDepth.max` is what to look at when a bridge op felt slow.

Keep a baseline from the same build, workload, tick rate and capture settings. Compare frame-time percentiles and framework share against it; an idle-server measurement is not a performance guarantee for combat or client rendering. Historical measurements are in the external research archive.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-perf-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"start","seconds":30}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server perf "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-perf-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"status"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server perf "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-perf-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"stop"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server perf "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-perf-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"start","seconds":60}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client perf "@$bridgeArgs"
```

A second `start` while one is running, or while the previous report is still `writing`, is an error; `stop` first and wait until `reportState` is `completed` or `failed`. A window that *should* have closed is closed on the way in, so "already running" cannot survive a missed deadline any more. A Dev plugin unload (a reload, or the Core reload cascade) closes the window with `stopReason: "unload"` and detaches the writer so it cannot publish into a later capture; a Core swap that happens while Dev stays loaded closes it with `stopReason: "core-swap"`. Both still write the file on the background thread. The sink is installed into a Core static, so leaving it behind would pin the Dev context and refuse the next Core swap.
