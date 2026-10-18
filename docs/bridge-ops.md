# Bridge operations: core, entities and samples

Operation reference for the [development bridge](bridge.md), part 1 of 5: health, logs, reload, users and worlds, commands and chat, ghost users, stress castles, entity queries and edits, the Core debug tools and the Sample commands. Other parts: [players, world and capture](bridge-ops-player.md), [Mods workspace UI](bridge-ops-ui.md), [server info and join flow](bridge-ops-join.md), [input, install and session](bridge-ops-client.md). Part of the [development loop](dev-loop.md).

## Ops

Examples below use a placeholder token. Send the real value from the token file. `result` shapes match `Handlers.cs`.

## `ping`

Health. Worlds, uptime, reload folder, plugins currently loaded through the reloader, latest log sequence. `instance` is the `PROGENITOR_INSTANCE` tag (empty string when unset; extra clients set `client-N`). `bridgeUsers` lists the current and recent bridge users ([Bridge users](bridge.md#bridge-users)).

```
→ {"id":1,"token":"...","op":"ping"}
← {"id":1,"ok":true,"result":{"side":"server","progenitor":"0.1.0","uptimeSeconds":87,"worlds":["Server"],"reloadFolder":"<testserver>\\server\\BepInEx\\progenitor-reload","reloadedPlugins":["Progenitor Dev Bridge","Spike 2 Prefab"],"logSeq":412,"instance":""}}
```

On the client, `side` is `"client"` and `worlds` typically includes `"Client_0"`.

`images` lists the loaded Progenitor images with their sha256 and `version` (the assembly's informational version, `year.month.release+revision`; null when unknown): `Core` (with its generation), `Bootstrap` (hashed by Bootstrap at `Load`, before any promote; absent with a Bootstrap older than auto-update phase 5) and each reloaded plugin.

## `logs`

In-memory ring (4000 entries) of every BepInEx log source. Not the disk file. `since` is exclusive (lines with `seq > since`). `max` is 1–2000, default 200. `filter` is a case-insensitive substring on text or source. `level` is a minimum BepInEx severity: `Fatal`, `Error`, `Warning`, `Message`, `Info`, `Debug`.

```
→ {"id":2,"token":"...","op":"logs","args":{"since":410,"max":50,"filter":"Reload","level":"Info"}}
← {"id":2,"ok":true,"result":{"latestSeq":412,"count":1,"lines":[{"seq":411,"time":"14:02:11.003","level":"Info","source":"Progenitor","text":"Reload: loaded Spike 2 Prefab 0.1.0 from Progenitor.Spike2.Prefab.dll."}]}}
```

Use `latestSeq` from the previous call as the next `since`.

## `bridge_status`

Socket thread; never takes the one-op gate. Liveness probe after a main-thread timeout. Contract: [`bridge_status` in api.md](api.md#dev-bridge-ops). No MCP wrapper; use `call.mjs`.

## `bridge_users`

Socket thread; never takes the one-op gate. Returns `{current, recent}`: the current bridge user and up to three distinct previous ones, each `{ai, name, id, client, tool, pid, model, firstSeen, lastSeen, requests, lastOp, ageSeconds}` (`current` is null before the first request). No MCP wrapper; `progenitor_ping` shows the same data. See [Bridge users](bridge.md#bridge-users).

```
→ {"id":3,"token":"...","op":"bridge_users"}
← {"id":3,"ok":true,"result":{"current":{"ai":"claude","name":"Example session","id":"<session id>","client":"claude-code 2.1","tool":"mcp","pid":1234,"model":"","firstSeen":"2026-10-10T12:00:00Z","lastSeen":"2026-10-10T12:05:00Z","requests":12,"lastOp":"bridge_users","ageSeconds":0},"recent":[]}}
```

## `reload`

Scan the reload folder immediately and return what changed, plus the plugins still loaded.

```
→ {"id":3,"token":"...","op":"reload"}
← {"id":3,"ok":true,"result":{"events":["Reload: Progenitor.Spike2.Prefab.dll changed, reloading.","Reload: unloaded Spike 2 Prefab: clean","Reload: loaded Spike 2 Prefab 0.1.0 from Progenitor.Spike2.Prefab.dll."],"loaded":["Progenitor Dev Bridge","Spike 2 Prefab"]}}
```

An empty `events` array means nothing had settled and changed since the last scan. If you copied a DLL less than a second ago, wait and call again.

## `core_reload`

Request an in-process Core swap. `scheduled: true` means the request was queued; unload, collection and install happen on later frames. `reload_status`, or `core_reload` with `{"status":true}`, reads status without scheduling work.

From the repository root in PowerShell, with the test bridge already running:

```powershell
node tools/mcp/call.mjs server reload_status
node tools/mcp/call.mjs server core_reload
```

After the bridge returns, read status again:

```powershell
node tools/mcp/call.mjs server reload_status
node tools/mcp/call.mjs server ping
```

The call tool prints the operation result, so inspect its inner `ok` as well as the shell exit code. Success requires a newer `status.generation`, `status.inFlight = false`, and `status.lastReload.outcome = "ok"` with a timestamp after your request. Confirm the expected plugin is loaded and its behavior works once. For a deploy, also compare the logged image hash to the built DLL; generation numbers are local counters. An identical-image deploy may correctly skip a swap without incrementing the generation.

| State | Meaning and next step |
|---|---|
| `inFlight = true` | Wait and re-read status. Do not schedule another swap. |
| `previousAlive = true` or a permanent collection refusal | Preserve diagnostics and restart the affected process when authorized. Repeated requests cannot release the old Core. |
| `watch.parked = true` | Read `watch.parkedReason`. The watcher has stopped retrying that file stamp; fix the cause, then restage or restart as appropriate. |
| Transient plugin-collection refusal | Watcher retries every 5 seconds, up to 12 retries per file stamp, then parks. Observe the result instead of redeploying repeatedly. |
| Promotion/read failure | Watcher retries up to 5 times. Inspect the disk log, path and file before restaging. |
| Protocol mismatch / generation 0 | Deploy a compatible Bootstrap/Core pair; restart if Bootstrap must change. The bridge may be absent until Core installs. |
| Install failure | Check whether the log confirms last-good restoration or a Core-less state. A failed install is not a successful upgrade. |

The watcher uses the same request path when `Reload.WatchCore=true`; that setting ships false and is enabled by the local deploy tools. It parks by file length/write time and retries when that stamp changes. Neither a remaining `.next` nor its disappearance alone proves success: promotion, identical-image skip and quarantine can all consume it.

Implementation references: [CoreReloadOps](../src/Progenitor.Dev/Ops/CoreReloadOps.cs), [CoreReloadStatus](../src/Progenitor.Bootstrap/CoreReloadStatus.cs), [CoreWatchPolicy](../src/Progenitor.Bootstrap/CoreWatchPolicy.cs). These command names and status fields were checked against source; this documentation update did not run a live swap.

## `users`

Users on the server world, or the client world if there is no server world in this process. `entity` / `character` are `index:version`.

```
→ {"id":4,"token":"...","op":"users"}
← {"id":4,"ok":true,"result":{"users":[{"index":0,"name":"TestAdmin","connected":true,"admin":true,"platformId":7656119,"entity":"12:1","character":"45:1"}]}}
```

Before a world exists: `{"users":[],"note":"no world yet"}`.

## `worlds`

Created ECS worlds and a live entity count.

```
→ {"id":5,"token":"...","op":"worlds"}
← {"id":5,"ok":true,"result":{"worlds":[{"name":"Server","entities":18432}]}}
```

There is no MCP wrapper for this op. `progenitor_ping` already returns world names.

## `command`

Server process only. Sends a chat line as if a connected player typed it: a `ChatMessageEvent` + `FromCharacter` entity, the shape the server makes from a client's line. Anything that hooks chat (Progenitor commands, the event bus, the word filter, other command frameworks, the game's chat job) sees it. Replies go to that user's client; if the handler also logs, follow with `logs`. Use it for plain lines too (`hello`, item and location tokens, mentions): no OS keystrokes are needed.

| Arg | Default | Meaning |
|---|---|---|
| `text` | required | The line. Longer than 509 UTF-8 bytes is cut, as the game cuts it. |
| `user` | first connected user | A character name, or a numeric `User.Index` (number or numeric string). Ghosts from the `ghost` op resolve the same way (`"Ghost1"`, `10001`). |
| `userIndex` | | A `User.Index`. Wins over `user`. |
| `channel` | `local` | `local`, `global` or `clan` (`team`). Whisper (needs a receiver) and System (admin-only, already sent to its sender) are refused. |
| `echo` | `true` | Also send the line to the speaker, stamped with their own network ids. |

**Why the echo.** The game's chat job never sends a Global, Local or clan line back to the player who typed it: their client draws its own line as they press Enter. An injected line has no typing client, so without the echo a line from the only connected player reaches nobody, and the bridge reply still says it was injected. That was the "nothing arrives, not even `hello`" result. Other players still get the line by the game's own rules (Local: within 48 m on the ground plane; Global: every connected player; mutes apply). The echo goes out whether or not a handler takes the line, as a typed line shows on the typist's screen before the server answers. Set `"echo":false` to see only what the server sends (for example, to count packets).

```
→ {"id":6,"token":"...","op":"command","args":{"text":"hello","channel":"global"}}
← {"id":6,"ok":true,"result":{"injectedAs":"TestAdmin","userIndex":0,"text":"hello","channel":"global","echoed":true}}
→ {"id":7,"token":"...","op":"command","args":{"text":".spike2 give","user":"TestAdmin","echo":false}}
← {"id":7,"ok":true,"result":{"injectedAs":"TestAdmin","userIndex":0,"text":".spike2 give","channel":"local","echoed":false}}
```

`echoed` is false when the echo was off or the speaker has no connected-user slot (a ghost made with `connectedUser:false`). On the client process this returns `ok: false` with `"command runs on the server side only"`. With nobody connected: `"no connected user to speak as"`. A ghost's `Reply` has no network slot, so the system-message text never reaches a client (one-shot `FailedNotConnected`); the command still runs.

Verification: argument parsing is game-free tested (`ChatInjectArgsTests`); source-checked and built. The echo is **not checked live**: the expected result is the line in the speaker's chat, drawn as their own (live check steps in [chat.md](chat.md#testing-chat-from-the-bridge)).

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-command-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"text":"hello","channel":"global"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server command "@$bridgeArgs"
```

## `chat_observe`

Server process only. Injects a chat line the same way `command` does with `"echo":false` (`ChatMessageEvent` + `FromCharacter`, `ChatMessageType.Local`; no echo, so the packets it reports are only what the server sends on its own), then watches every `ServerBootstrapSystem.SendPacketToUser` for `windowMs` (default 1500). The game frame is not blocked: the observer is a Bootstrap-owned postfix (Dev fills the slot once, lazily), and the bridge completes via `MainThread.PostDelayed` when the window closes. Packet payloads are scanned for the UTF-8 and UTF-16 forms of the injected text (FixedString chat is UTF-8). State-sync packets are omitted unless they contain the needle.

While armed, each `Plugin.Frame` also queries `ProjectM.Network.ChatMessageServerEvent` entities and records distinct ones. The result field `chatServerEvents` is that count. Use it to tell **consumed silently** (source destroyed, `chatServerEvents` 0, no send) from **sent via an unobserved path** (source destroyed, `chatServerEvents` > 0, still no `SendPacketToUser`). Transient event entities can be created and destroyed between frames, so 0 means "not observed on a frame poll", not a hard proof the job never created one.

Use this observation to inspect command consumption for a real connected test user. Verifying delivery to another player requires another real client. Do not add `ConnectedUser` to a synthetic ghost for this test: that experimental path has crash evidence and is not a safe substitute for a connected peer.

`text` is required. Speaker is `userIndex` (`User.Index`), or `characterName` / `user` (character name), or the first connected user.

```
→ {"id":6,"token":"...","op":"chat_observe","args":{"text":".ember 1","windowMs":1500}}
← {"id":6,"ok":true,"result":{"injected":{"injectedAs":"TestAdmin","userIndex":0,"text":".ember 1","requestedText":".ember 1","entity":"88:1"},"recipients":[],"containsTextAnywhere":false,"payloadReachable":true,"sourceDestroyed":true,"chatServerEvents":0,"truncated":false,"windowMs":1500,"connectedUsers":[{"index":0,"name":"TestAdmin"}]}}
```

`.ember` is admin-only. A non-admin receives `This command is admin-only.` (`CommandResult.Denied`). Denied commands are still handled, so their chat entity should be consumed before serialization. `.joins` is the ungated alternative for this observation.

**Handled command** (`.ember 1`, `.joins`, anything Progenitor or a consumer cancels): `containsTextAnywhere` is **false**, and no `recipients[]` entry has `containsText: true`. Not even an echo to the speaker. `sourceDestroyed` is true because the chat hook destroyed the entity before `ChatMessageJob`. `chatServerEvents` is **0** (the job never ran, so no `ChatMessageServerEvent`). Recipients may still list unrelated Events packets from the window; those must have `containsText: false`.

**Unmatched line** (`hello`, or a dot-command nobody registered): `containsTextAnywhere` is **true** if the game serialized the line (Local, so a second player only sees it in range; with one connected user the speaker echo is enough). `sourceDestroyed` is whatever the chat job did with the leftover entity. `chatServerEvents` > 0 means the job created outbound event entities even if `recipients` is empty (unobserved send, or `FailedNotConnected` to a ghost).

On the client process: `"chat_observe runs on the server side only"`. With nobody connected: `"no connected user to speak as"`. `windowMs` is clamped to 50–15000.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-chat_observe-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"text":".ember 1"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server chat_observe "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-chat_observe-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"text":"hello","windowMs":1500}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server chat_observe "@$bridgeArgs"
```

## Ghost users (server)

Server process only, main thread. Creates a synthetic User with no network peer for command, event and persistence tests. The operation copies an existing User record, assigns a reserved identity and creates `[User, Simulate]`. Use the bridge operation rather than reproducing its raw layout writes in a snippet.

**Experimental default:** the current implementation attaches `ConnectedUser` unless `connectedUser:false` is supplied. That path has server-crash evidence and is not qualified for routine tests. Use `connectedUser:false` for a narrowly scoped command test and a real client for network-recipient tests. Even a User-only ghost must be destroyed in the same test batch before autosave; a persisted ghost can contaminate the save. This API reference describes the implementation, not a guarantee that leaving a ghost alive is safe.

`ConnectedUser` layout (the IL viewer, `ProjectM.Shared.dll`, inferred from stubs): `[FieldOffset(0)] byte UserIndex` only — no `UserEntity`, no connection id, no bools, size 1. `User.Index` (ghosts start at 10001) does not fit in that byte (it is the approved-user slot the chat job addresses). Create writes a free slot, preferring `(byte)User.Index` (Ghost1 → 17) when unused. No copy-from-template unless ECS `TypeSize` is larger than 1 (hidden fields); if the template user is offline and nobody else has `ConnectedUser`, the extra bytes are zeros plus `UserIndex`.

`action` is required: `create` | `destroy` | `list`.

| Arg | Default | Meaning |
|---|---|---|
| `count` | 1 (max 32) | How many to create |
| `namePrefix` | `Ghost` | Name is `<prefix><n>` (`Ghost1`, `Ghost2`, …) |
| `admin` | `false` | `User.IsAdmin` |
| `connectedUser` | `true` (experimental) | Set `false` for routine command tests. The default recipient-component path has crash evidence and remains unqualified. |

Create assigns a unique `User.Index` starting at **10001** (skips any index already in use, including offline players) and `PlatformId` **90000000000000001+n**. Tracked in a static list. Destroy removes every tracked ghost **and** any User whose PlatformId is in that range, so a leftover from a crash is cleaned. List returns the tracked ghosts with `entity` / `index` / `name`. The Dev plugin `Unload` also destroys them.

Each created (and listed) ghost reports `hasConnectedUser` (`Entity.Has<ConnectedUser>`), `connectedUserIndex` (the byte slot, or null), `inConnectedUsersQuery` (whether the ghost appears in the enabled-only `ConnectedUser` query — the `_ConnectedUsersQuery` equivalent), and `connectedUsersQueryCount` (how many entities that query currently matches).

```
→ {"id":23,"token":"...","op":"ghost","args":{"action":"create","count":1,"connectedUser":false}}
← {"id":23,"ok":true,"result":{"ok":true,"action":"create","count":1,"connectedUser":false,"template":{"entity":"12:1","name":"TestAdmin"},"ghosts":[{"entity":"88:1","index":10001,"name":"Ghost1","platformId":90000000000000001,"admin":false,"connected":true,"bot":true,"hasConnectedUser":false,"connectedUserIndex":null,"inConnectedUsersQuery":false,"connectedUsersQueryCount":1}]}}

→ {"id":24,"token":"...","op":"ghost","args":{"action":"list"}}
← {"id":24,"ok":true,"result":{"ok":true,"action":"list","count":1,"ghosts":[{"entity":"88:1","index":10001,"name":"Ghost1",...}]}}

→ {"id":25,"token":"...","op":"ghost","args":{"action":"destroy"}}
← {"id":25,"ok":true,"result":{"ok":true,"action":"destroy","destroyed":1,"leftovers":0,"ghosts":[]}}
```

**Caveats** (from the live spike — do not skip these):

- **No character.** `User.LocalCharacter` is cleared. The `players` op will not list a ghost. Anything that needs a world position or inventory will not work.
- **Not in the approved-user table.** `PlayerControl.TryGetConnection` is false; `SendPacketToUser` has no live slot. Prefab definition packets and `PlayerConnected` never fire for it. With `connectedUser: true` the chat job *will* address a packet at the ghost's `ConnectedUser.UserIndex`; the send is expected to return `FailedNotConnected`. That is the leak signal, not a success.
- **Netcode may dereference a connection-less `ConnectedUser` every frame** (snapshot / inventory ghosting). Abort the live run on any exception or frame-time spike. Destroy promptly.
- **Replies vanish.** `ctx.Reply` / `ServerChatUtils.SendSystemMessageToClient` hit `FailedNotConnected` once per line. The command still executes (`.joins` returned `Executed`); there is just nobody to show the text to. Follow with `logs` / `store`.
- **Destroy in the same test batch before autosave.** `User` is a persisted component; a ghost left alive is written into `test1`. `destroy` (or Dev Unload) is the cleanup. A crash between create and destroy: run `destroy` after the next boot — it also sweeps PlatformIds in the ghost range.

On the client process: `"ghost runs on the server side only"`. With no server world: `"no server world"`.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-ghost-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"create","connectedUser":false}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server ghost "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-command-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"text":".joins","user":"Ghost1"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server command "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-ghost-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"destroy"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server ghost "@$bridgeArgs"
```

## Stress castles (server): `stress_populate`, `stress_clear`, `stress_status`, `stress_team`

Server process only, main thread. Fills the free castle plots of a save with test castles owned by offline test owners, for load tests of `ctx.Castles`, inventories and the server frame. MCP: `progenitor_stress` (`action`: `populate`, `clear`, `status`, `team`).

**Refused** unless every check passes: no path of the process (executable, game root, BepInEx config, `-persistentDataPath`) is under `C:\VRisingServer`; the executable or game root is in a `_testserver` folder; `-persistentDataPath` is a `moo-world-copy*` folder under `_testserver` that is not a `.bak` copy; and the request carries `confirm:"stress"` (a `dryRun` needs no confirm). The user approved the op for the Moo World copy test save only. Its owners are meant to stay in that save until `stress_clear` removes them; this is an exception to the ghost-user rule above, for this op only. Back up the save first.

**Status:** planner, gate and runner are game-free and unit-tested (`DevStressPlanTests`); the MCP registration is tested (`stress-tool.test.mjs`); Dev builds. **Not run live.** In this build only the owner step has a proven path. Clans, hearts, fuel, floors, chests, stations and chest fill have none, so a full populate refuses with `unproven` and writes nothing (see `missing` below).

### `stress_populate`

| Arg | Default | Meaning |
|---|---|---|
| `seed` | required, 0..9999 | Plan seed. The same seed and world give the same plan. It also picks the owners' PlatformId band. |
| `owners` | 40 (1..99) | Test owners `Stress01`, `Stress02`, … Each gets 2..5 plots; fewer owners when there are fewer than 2 usable plots each. |
| `clans` | 12 | Clans `StressClan01`, … of 2..5 owners, adjusted to fit; 0 for none. |
| `plots` | `"free"` | Every free plot, or an array of territory indices. |
| `chests` | `[10, 25]` | Chests per plot, `[min, max]`, up to 30. |
| `stations` | `[6, 10]` | Stations per plot, up to 12. |
| `fill` | 0.5 | Average share of a chest's slots to fill; about one chest in five is planned nearly full (90..98 %). |
| `budgetMs` | 4 (0.5..20) | Main-thread milliseconds spent per frame. At least one step runs per frame. |
| `dryRun` | false | Plan and report; write nothing. |
| `partial` | false | Run only the steps that have a proven path and refuse each plot at its first step without one. |
| `confirm` | | `"stress"`. |

What the plan holds per plot: a castle heart (`TM_BloodFountain_CastleHeart`) at the territory's heart pivot (the `bot_territories` centroid spot: pivot tile ≡ 5 mod 10, the 30 × 30 tile footprint all buildable), fuel of 100..400 `Item_BloodEssence_T01`, a 6 × 6 cell floor block (`TM_Castle_Floor_Foundation_Stone01`, 27 floors beside the heart's 3 × 3 cells, in one of four corners that fits the buildable mask, ordered so each floor touches the heart or an earlier floor), stations one per cell (two `TM_Workstation_SimpleCraftingbench`, then furnace small and large, sawmill, grinder, tannery, loom, blood press and fabricator in seeded order) and chests two per cell (general, big, minerals, alchemy, fish and gems kinds, each with 2..6 fitting materials). Prefab names other than the general chest, the heart, the stone floor, the `_Small` refiners, the fabricator and the simple bench are candidates resolved by name and are not checked against the game.

The run is a job: territory discovery (one territory per step), the plan, then owners, clans and per plot heart, fuel, floors, stations, chests and fills. The reply comes once the plan exists (or the job ended), and has the same shape as `stress_status`. A plot refused at plan time has `status: refused` and a `reason`: `plot_taken`, `no_such_plot`, `not_a_plot` (world region `None` or a debug territory), `no_valid_pivot`, `no_floor_fit` or `no_owner_capacity`. At run time a plot whose owner was not created is refused `owner_failed`; one whose heart failed is refused with the heart's code and nothing else is tried on it; other step failures leave it `partial` with the first refusals listed.

`missing` maps each step kind with no proven path to the reason. If it is not empty and `partial` is false, the job ends `refused` with reason `unproven` before any write. The job is also refused when owners of this seed are already in the world (run `stress_clear` first) or owners of another seed are (their names would clash).

Owners: each is a User written the way `ghost create` writes one (a real user's record copied and patched), with `IsConnected` false, `IsBot` true, no `ConnectedUser` and no character, a `User.Index` reserved for every owner before anything is written, and PlatformId `90000000000100000 + seed × 100 + n`. Each owner then gets its own team the way the server gives a new user one: an instance of the game's player team prefab (`ServerRootPrefabCollection.PlayerTeamPrefabGUID`) whose `UserTeam` points at the owner, and a `TeamReference` on the owner pointing at that team. This is what `TeamUtility.ClaimCastle` reads to give the owner a castle. **Owner indexes:** owners first take indexes no User holds below the highest existing one, then the ones right after it, never 0, all at most 111 so 16 of the game's 128 per-user mask slots stay above them for players who join later; every User counts, online or offline. If that does not fit, or a User cannot be read, the job ends `refused` (`owner_index`) before any write, and each owner is checked again right before it is created. The game's 128-user masks are keyed by the connection slot, not `User.Index`, and a new player gets the highest existing `User.Index` plus one, so this keeps later players' indexes low. If the team step fails, the owner is destroyed again and refused with `no_team_prefab` or `team_failed`. That band is outside the ghost and extra-client bands, so `ghost destroy` and Dev unload leave them. Not live-verified: how an enabled offline User with no character behaves in the game's systems, the autosave and the next load, and the owner team (source-checked against the server code only).

### `stress_clear`

Args: `seed` (required), `confirm:"stress"`, `budgetMs`, `dryRun`. Finds every User in that seed's band and destroys each with its character and its own team, if any. An owner that still owns a castle heart (`heart_left`) or is in a clan (`clan_member`) is kept and reported: removing a heart or a clan member has no proven path. Run it twice: the second run finds nothing and reports `removed: 0`. The reply comes when the job ends.

### `stress_status`

The running or last job: `kind`, `seed`, `phase` (`discover`, `plan`, `execute`, `done`, `refused`, `failed`, `cancelled`), `reason`, `steps` (`done`/`total`), `ticks`, `maxTickMs`, `totalMs`, `missing`, territory counts, owner and plot counts by status and reason, and rows: `results` (per plot: owner, status, reason, heart, fuel, floors, stations, chests, filled, refusals), `ownerResults` and `clanResults`; for a clear, `removed`, `kept` and `users`. Args: `plot` (territory index) adds that plot's full layout (positions, prefabs, chest contents); `rows:false` returns counts only. `{phase: "idle"}` before any job. A Dev unload stops a job between steps; what it made stays until `stress_clear`.

### `stress_team`

Read only; no gate, never writes. Args: `seed` (required), `heart` (optional, a castle heart entity `index:version`). Returns `playerTeamPrefab` (`guid`, `entity`, or `playerTeamPrefabError`) and, per owner of that seed, `name`, `entity`, `userIndex`, `team`, `teamState` (`ready`; `missing` when the owner has no team; or a refusal code: `connected`, `foreign_team`, `team_without_allies`), `missing` (the pieces not found) and `allies` (the team's `TeamAllies` rows). With `heart`: `heart` (`exists`, `castleTeam`, `castleTeamAllies`) and per owner `claimRefusal`, null when every read `TeamUtility.ClaimCastle` makes would succeed for that owner and heart, else the first missing piece. It does not call ClaimCastle. **Not run live.**

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-stress-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"seed":7,"dryRun":true}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server stress_populate "@$bridgeArgs"
[System.IO.File]::WriteAllText($bridgeArgs, '{"plot":12}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server stress_status "@$bridgeArgs"
[System.IO.File]::WriteAllText($bridgeArgs, '{"seed":7,"confirm":"stress"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server stress_clear "@$bridgeArgs"
```

### Staged ops on existing stress castles: `stress_owner_groups`, `stress_clan`, `stress_refine`, `stress_stations`, `stress_stage_status`

These work on castles a seed's stress owners already hold (on the Moo World copy: 140 generated castles, owners `Stress01`..`Stress40`). Castles of other owners are never touched. Same gate as `stress_populate`; a write needs `confirm:"stress"`, a `dryRun` does not. Work runs a few steps per frame under `budgetMs` (default 4); each op reads back on a later frame. The reply comes when the job ends (or after 25 s; then read `stress_stage_status`). MCP: `progenitor_stress` with `action` `groups`, `clan`, `refine`, `stations`, `stage`.

**Status:** planners are game-free and unit-tested (`DevStressStagesTests`), the MCP mapping is tested, Dev builds. **Not run live.**

**`stress_owner_groups`** (read only). Args: `seed`, `groups` (clan sizes, default `[8, 6, 4, 2]`), `share` (castle-share owners, default half of the owners outside clans). Owners keep their castles; this only decides which owner is in which group:

- **solo**: the owners whose own castles lie closest together;
- **castle-share**: owners with 3 or more castles, each owner's castles one island;
- **clans**: the rest, chosen to spread each clan across the map and to mix neighbors, by a deterministic hill climb over swaps. Score: 0.55 x mean clan spread + 0.35 x neighbor mixing + 0.10 x castle-share own spread. A clan's spread is its castles' mean pairwise and mean nearest-other-member distance over the map diagonal, plus the share of regions it covers. Mixing is the share of each plot's 4 nearest other-owner plots in a different group.

The reply lists, per group, members (name, platform id, user index, castle count, own spread), `castlesPerOwner`, every castle's `territory`, `x`, `z` (world meters, territory center) and `region`, and `spread` (`meanPairDistance`, `minPairDistance`, `meanNearestDistance`, `regions`, `score`). It also has `mixing`, `score`, `mapDiagonal` and `islands` (per island its territories and `rawCastles`, see `stress_refine`). The same seed and castles give the same grouping, and the other staged ops use it.

**`stress_clan`**. Args: `seed`, `groups`, `share`, `clan` (one clan ordinal, to prove one clan first), `experiment:true` (required unless `dryRun`), `confirm`. For each planned clan it uses the server's clan create step: an instance of the clan team prefab, then `ClanTeam` name `StressClanNN`, motto, a random clan GUID, the next team value and the leader's network id. Each member then joins through the game's add-member routine `TeamUtility.AddUserToClan`, called through its generated wrapper, which is why the `experiment` opt-in exists. That routine links the member's team and the clan team both ways, copies the clan's team data, sets `User.ClanEntity` and raises the game's team events. The op writes `User.ClanEntity` back and sets the role through the game's `ClanSystem_Server.SetClanRole`. The first member is leader, and `ClanRole` is added first because offline owners lack it. The game's character clan name step is skipped: it needs the user's controller, which offline owners lack. A clan is refused before any write when a member is connected, already in a clan or has no own team. It is also refused when it is bigger than the server's live `ClanSize` setting: the invite path enforces that limit and the add-member routine does not.

A member is also refused (`status_shape`) unless it has a `Controller` whose controlled character exists and has `Health`. The game's clan status system looks these up for every clan member inside a Burst job, and a missing component aborts the server. That happened live on 2026-10-18 with a stress owner, which has no `Controller` and no character. Stress owners therefore cannot join a clan in this build. The refusal stays until the status job's reads are known to be guarded.

`members` (platform ids as strings, leader first) builds one clan of exactly those users instead of the seed's grouping, named `name` (default `StressClanH1`). Use it for the headless clients' users, which are real users with characters. The same create and join steps run, with the same refusals, except that connected members are allowed: the game's own create and accept paths run for connected users. For a user with a `Controller`, the clan name is also set on the character through the game's `ClanUtility.SetCharacterClanName`. The job stops at the first failed step.

Castle teams are **not** added to the clan's allies, because the game's join path does not add them: a castle stays allied to its owner's team, and clan access goes through that team. The readback lists, per member: `clanEntity`, team allies, team data against the clan's, role, and per castle `alliedToOwner` and `castleTeamInClanAllies`. `stress_clear` keeps clan members, because removing a member has no proven path.

**`stress_refine`**. Args: `seed`, `groups`, `share`, `plots` (`"stress"` (the default) or territory indices; the grouping always sees every castle of the seed), `hours` (default 1), `supply` (`"spread"`, `"local"` or `"auto"`, the default: spread for clan and castle-share islands, local for solo owners), `suppliers` (the share of a spread island's castles that get raws, default 1/3 and at least one, farthest apart first), `emptyChests` (chests per castle left empty for products, default 3), `speed` (extra refining speed factor, default 1), `confirm`, `dryRun`.

- Every unlocked refinement recipe of every station in the selected castles is switched on through `ctx.Stations.SetRecipeEnabled`. That makes the same writes as the station window's checkbox: the recipe row, its linked rows and the station's update flag.
- Raw inputs for `hours` are added to chests through the `give` item path. A station runs one recipe at a time, so its time is split over its runnable recipes. Batches per recipe = ceil(time x server refinement rate x speed / recipe duration), and each batch takes the recipe inputs x the server refinement cost.
- With `spread`, an island's whole need goes only to its raw castles, so factories pull raws across plots. The other castles' chests get none. With `local`, each castle gets what its own stations use.
- Chests are filled emptiest first. The `emptyChests` emptiest chests of every castle get nothing.

The plan lists per island its territories, `rawCastles`, the item `need` and, per raw castle, the stocking chests, the kept-empty chests and the items. The readback lists per castle the recipes on out of those unlocked, locked recipes, empty chests, and for raw castles each planned item against what the chests now hold. Locked recipes are left alone: unlocking is a progression change, not the window's checkbox.

**`stress_stations`**. Args: `seed`, `plots`, `perCastle` (default 15), `experiment`, `limit` (default 1), `confirm`, `dryRun`. It reads each castle's refinement stations and plans the top-up to `perCastle`: the station kinds already in use, round-robin, fewest first. No proven path places a building for an offline owner, because the build event needs the owner's character.

**Experiment** (`experiment:true`, `plots` with 1 to 3 castles, `confirm`): it places up to `limit` stations per castle with the writes the castle generator makes for a room object. That is an instance of the station prefab, `Translation`, and `CastleHeartConnection` set to the castle's heart, with no rotation.

- The spot is a free block of the castle's floors. It is the size of an unrotated station of the same kind in that castle, and clear of every other footprint by one tile.
- The position copies that station's offset from its own footprint.
- The readback for each placed station reports `heartConnected`, `team` against the heart's `castleTeam`, `inventories` (expect 2), `status` (`noOwner` should be false), `listedInCastle`, `footprint`, `overlaps`, `listedAsStation` and `structureRevisionChanged`.

The experiment proves whether the game registers the tiles, gives the castle team and keeps the station across a save reload. Removing a placed station has no proven path.

**`stress_stage_status`**: the running or last staged job: `op`, `phase` (`running`, `planned`, `done`, `done_with_failures`, `stopped`, `cancelled`), `steps`, `done`, `failed`, `maxTickMs`, `plan`, `notes`, step `rows` and `readback`.

```
[System.IO.File]::WriteAllText($bridgeArgs, '{"seed":18}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server stress_owner_groups "@$bridgeArgs"
[System.IO.File]::WriteAllText($bridgeArgs, '{"seed":18,"clan":1,"dryRun":true}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server stress_clan "@$bridgeArgs"
[System.IO.File]::WriteAllText($bridgeArgs, '{"seed":18,"plots":[12],"supply":"local","dryRun":true}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server stress_refine "@$bridgeArgs"
```

## `ops`

The list of ops this build knows. Useful as a smoke test that does not touch the world.

```
→ {"id":7,"token":"...","op":"ops"}
← {"id":7,"ok":true,"result":{"ops":["ping","logs","bridge_status","reload","users","worlds","command","chat_observe","ghost","query","get","set","components","spawn","prefab","buff","heal","teleport","give","inspect","snapshot","restore","players","status","save","world_save","store","connect","host","host_capture","disconnect","quit","castles","capture","perf","mods_ui","ops"]}}
```

## `query`

Find entities by required (`all`) and excluded (`none`) component names. **`all` is required** unless `allowUnbounded` is true: an empty `all` list with `IncludeDisabledEntities` matches every entity and stalls the simulation. Default queries drop disabled entities (offline players), so `includeDisabled` defaults to true. `near` is an optional `{x,y,z,radius}` sphere. `limit` is 1–2000, default 50. `total` is **opt-in** (default false): without it the walk stops after `limit` accepted rows and `total` equals `count`; with `total: true` the walk continues (up to a hard scan cap of 200_000) so `total` is the full accepted count while `count` stays the rows returned. Hitting the hard cap sets `truncated: true`. Returns `index:version` ids, prefab GUID/name, and position.

```
→ {"id":8,"token":"...","op":"query","args":{"all":["PlayerCharacter"],"limit":10}}
← {"id":8,"ok":true,"result":{"total":1,"count":1,"truncated":false,"entities":[{"entity":"45:1","prefab":{"hash":123,"name":"CHAR_VampireMale"},"position":{"x":0.0,"y":0.0,"z":0.0}}]}}
```

## `get`

Read one IComponentData blob as JSON fields. `entity` is `index:version` from `query`. `component` is a simple name (`Health`) or full name (`Unity.Transforms.Translation`).

```
→ {"id":9,"token":"...","op":"get","args":{"entity":"45:1","component":"Unity.Transforms.Translation"}}
← {"id":9,"ok":true,"result":{"entity":"45:1","component":"Unity.Transforms.Translation","size":12,"fields":{"Value":{"x":0.0,"y":0.0,"z":0.0}}}}
```

## `set`

Patch listed primitive / Entity / PrefabGUID / float3 fields on a copy of the component bytes and write them back. Unknown field names are refused. Returns the component after the write (same shape as `get`).

```
→ {"id":10,"token":"...","op":"set","args":{"entity":"45:1","component":"Unity.Transforms.Translation","fields":{"Value":{"x":10.0,"y":0.0,"z":-4.0}}}}
← {"id":10,"ok":true,"result":{"entity":"45:1","component":"Unity.Transforms.Translation","size":12,"fields":{"Value":{"x":10.0,"y":0.0,"z":-4.0}}}}
```

## `components`

List the component type names on one entity.

```
→ {"id":11,"token":"...","op":"components","args":{"entity":"45:1"}}
← {"id":11,"ok":true,"result":{"entity":"45:1","count":3,"components":["ProjectM.PlayerCharacter","Unity.Transforms.Translation","Stunlock.Core.PrefabGUID"]}}
```

## `spawn`

Server process only. Instantiates a prefab from `PrefabCollectionSystem._PrefabGuidToEntityMap` at `(x,y,z)`. `prefab` is an integer GUID hash or a spawnable name. `count` defaults to 1 (max 256). Placement writes **Translation, LastTranslation, and SpawnTransform.Position** (Rotation is left as the prefab facing) through the same helper Units uses; Translation alone is not enough. The response is deferred one second (`MainThread.PostDelayed(1,...)`) so `position` is the live Translation after spawn systems have run, and `placed` is true when that point is within 1 m of the request. Units may still need extra setup to behave (Follow / GoTo / a connected player nearby).

```
→ {"id":12,"token":"...","op":"spawn","args":{"prefab":"Item_Ingredient_Stone","x":0,"y":0,"z":0,"count":1}}
← {"id":12,"ok":true,"result":{"count":1,"prefab":{"hash":-123456,"name":"Item_Ingredient_Stone"},"entities":[{"entity":"88:1","prefab":{"hash":-123456,"name":"Item_Ingredient_Stone"},"position":{"x":0.0,"y":0.0,"z":0.0},"placed":true}]}}
```

On the client process this returns `ok: false` with `"spawn runs on the server side only; the client cannot instantiate gameplay prefabs"`. The one-op slot stays held for the read-back second.

## `prefab`

Both sides, main thread. Resolves a prefab the same way `spawn` does (`PrefabLookupMap.TryGetPrefabGuidWithName`, then `_SpawnableNameToPrefabGuidDictionary`, then int hash) and returns the collection entity from `_PrefabGuidToEntityMap` **without instantiating it**. `components` is the same type-name list as the `components` op. Optional `component` adds that type's fields, same walk as `get`.

`prefab` is an integer GUID hash or a spawnable name. Unknown names list the three lookups that ran.

```
→ {"id":35,"token":"...","op":"prefab","args":{"prefab":"Item_Ingredient_Stone"}}
← {"id":35,"ok":true,"result":{"guid":-123456,"name":"Item_Ingredient_Stone","entity":"12:1","components":["Unity.Entities.Prefab","Stunlock.Core.PrefabGUID","ProjectM.ItemData"]}}

→ {"id":36,"token":"...","op":"prefab","args":{"prefab":"Item_Ingredient_Stone","component":"ProjectM.ItemData"}}
← {"id":36,"ok":true,"result":{"guid":-123456,"name":"Item_Ingredient_Stone","entity":"12:1","components":["Unity.Entities.Prefab","Stunlock.Core.PrefabGUID","ProjectM.ItemData"],"fields":{"ItemType":1}}}
```

A name the three lookups miss returns `ok: false` with `"no prefab named '…'. Tried PrefabLookupMap.TryGetPrefabGuidWithName, _SpawnableNameToPrefabGuidDictionary, and int hash. …"`. A resolved hash that is not in `_PrefabGuidToEntityMap` is `"prefab HASH is not in _PrefabGuidToEntityMap. …"`.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-prefab-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"prefab":"Item_Ingredient_Stone"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server prefab "@$bridgeArgs"
$bridgeArgs = Join-Path $env:TEMP 'progenitor-prefab-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"prefab":"Item_Ingredient_Stone","component":"ProjectM.ItemData"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client prefab "@$bridgeArgs"
```

## `buff`

Server process only, main thread. Apply or remove a buff prefab on a live entity through `DebugEventsSystem.ApplyBuff` / `DestroyUtility.Destroy(..., TryRemoveBuff)`. `entity` is `index:version` (a `PlayerCharacter` or a `User`; the op fills `FromCharacter` from whichever you pass). `guid` is an integer PrefabGUID hash or a spawnable name. `apply` defaults true.

Returns the buff instance id (`buff`) so you can `get` / `components` it. `already: true` means the GUID was already on that target's `BuffBuffer`.

```
→ {"id":33,"token":"...","op":"buff","args":{"entity":"45:1","guid":"progenitor.debug","apply":true}}
← {"id":33,"ok":true,"result":{"apply":true,"already":false,"entity":"45:1","buff":"88:1","guid":123,"name":"progenitor.debug","note":"applied"}}

→ {"id":34,"token":"...","op":"buff","args":{"entity":"45:1","guid":"progenitor.debug","apply":false}}
← {"id":34,"ok":true,"result":{"apply":false,"already":false,"entity":"45:1","buff":"88:1","guid":123,"name":"progenitor.debug","note":"removed"}}
```

On the client process this returns `ok: false` with `"buff runs on the server side only"`. Core's `.debug on|off` ([below](#core-debug-tools-debug)) uses this same apply path after `DefineBuff("progenitor.debug").CloneOf("Buff_General_Haste").Icon("icons/debug.png")` (a visible source so the buff bar can show an icon; the PNG, embedded in Core, replaces the haste sprite on the client) and its prefab pass of `ModifyBloodDrainBuff` / `LifeTime`.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-buff-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"entity":"45:1","guid":"progenitor.debug","apply":true}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server buff "@$bridgeArgs"
```

Live recipe (blood-drain test, Core's `.debug`; the steps ran with the Sample's `.nodrain` before the move and have not been repeated with Core's module):

1. Join the disposable test server as a vampire and read `users` to obtain the current character entity; never reuse an entity id from an earlier run.
2. Read `ProjectM.Blood`, then run `.debug on` as that user's admin command. Expect the definition/apply logs for `progenitor.debug`, a live buff instance and `edit Immortal IsImmortal=True`.
3. Confirm `ModifyBloodDrainBuff` is present on the buff and the blood bar stops falling. The stored `LossPerSecond._Value` may remain unchanged because modifiers live elsewhere. Check the custom icon visually and inspect `ProjectM.Immortal.IsImmortal` on the character.
4. Run `.debug status`. Confirm the process-owned god-mode guard and its kill hooks are armed. A surviving buff is persisted world state; it does not prove those hooks survived a process restart. `.debug on` over an existing buff re-runs the character protection pass.
5. On a disposable character, exercise an authorized kill path and confirm a blocked-kill diagnostic and `Health.IsDead=false`. A patch refusal or failed registration means this protection is unverified; do not infer it from the buff icon.
6. Run `.debug off`. Confirm the owned guard/component clears, drain resumes and the icon disappears. It also clears owned protection when the buff is already absent.

Check recovery and ownership separately:

- After a Core swap, expect `Debug: unload reason=Swap, cleared Immortal + god-mode guard` even though the buff is retained. The next generation must reconcile its own surviving grants and rearm protection. The gap between cleanup and recovery is not protected by a plugin before-save callback.
- The server reconciles on `WorldReady` and its watchdog. Grants for characters not streamed in remain unresolved and retry without mutation. A guard read-back of `No` also retries. Three consecutive read-back exceptions stop only that grant; three whole-pass exceptions stop the pass. `STOPPED`, unreadable and unresolved results mean unknown protection, not success.
- The watchdog inspects the debug buff's grant ledger, not every character carrying `Immortal`. After removing a buff through another path, its owned protection should clear within 60 frames. Foreign or pre-existing `Immortal` must survive load, reload, unload and `.debug off`.
- The ledger keys grants by account and character name. A renamed/recreated character does not inherit another character's grant: reconciliation drops the mismatched row while leaving foreign protection untouched. Reading an unknown/mismatched row must not mutate it. A recreated character with the same name remains an identity limitation; use a disposable save for that test.

Preserve the `world-ready reconcile` summary, `.debug status`, entity read-backs and screenshots for each recovery case. A missing summary or a stopped retry is a failed or incomplete observation.

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-command-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"text":".debug on"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server command "@$bridgeArgs"
```

## `entity`

Server or client world, main thread. `action` is `exists`, `destroy`, `finalize` or `force`; `entities` is an array of `index:version` (at most 64). Players, users and prefab entities are refused. `destroy` is DIAG5 phase `buffs`: each still-existing `BuffBuffer` row gets `DestroyUtility.Destroy(TryRemoveBuff)` + `DestroyTag`; the unit is not touched. Call `finalize` with the returned `buffs` until it reports `tagged` (or `stuck` after 4 cycles).

**Replicated removal.** At `tagged` (and in `force`) the unit gets `DestroyUtility.Destroy` **alone** and the game's destroy flow finishes it, which is what tells clients. The entry reports `destroy: "replicated"`, `existsAfter` and `destroyTagFallbackFrames` (120). Only if the unit still exists after that many frames is `DestroyTag` added, with the warning `entity finalize: <id> still exists 120 frames after DestroyUtility.Destroy; adding DestroyTag as a fallback`. Live 2026-09-24: `Destroy` plus an immediate `DestroyTag` removed a loot drop and 397 of 400 Frostmaws on the server while the client kept them (the drop stayed visible, the bosses as disabled entities); `Destroy` alone removed a drop and a boss on both sides within 2 s. `ctx.Units` despawn uses the same final step. Arena and cleanup scripts must use this path, not a hand-written `DestroyTag`.

## Core debug tools (`.debug`)

**Acceptance procedure — not run.** Core's built-in admin debug buff and map powers ([modules.md](modules.md#debug-tools-debug)), moved from the Sample's `.nodrain` / `.maptp` on 2026-10-06. Built and game-free tested only. No Bootstrap change: a Core swap installs it on each side.

Prerequisites: an authorized session on the isolated test server (`<testserver>`, `127.0.0.1:9976`) and a Steam client that is not in use by the user, with the Dev bridge on both; an admin test user and a second, non-admin user (a headless client is enough for the refusal). Check that the client is not joining before any client deploy.

1. **Deploy Core only, both sides.** Build this branch, then deploy Core without the Sample:

   ```powershell
   dotnet build Progenitor.sln -c Debug
   if ($LASTEXITCODE -ne 0) { throw 'build failed; do not deploy' }
   powershell -NoProfile -File tools/deploy.ps1 -Projects Progenitor.Core -Side server -NoBuild
   powershell -NoProfile -File tools/deploy.ps1 -Projects Progenitor.Core -Side client -NoBuild
   ```

   Confirm each Core swap with `reload_status` as in [hot-reload.md](hot-reload.md#swap-core-and-confirm-completion). The server log shows `Debug: installed (server: .debug, buff progenitor.debug, map powers).` and `Debug: world-ready reconcile …`; the client log `Debug: installed (client: buff progenitor.debug, map powers).` An older Sample that still defines `.nodrain` must not stay loaded next to it (its map key is the same Middle Mouse bind): run `.nodrain off` with it first, then remove `Progenitor.Sample.dll` from `BepInEx/progenitor-reload/` on each side (or deploy this branch's Sample, which no longer has it). `.help` as an admin lists `.debug` and no `.nodrain` or `.maptp`.
2. **On, as an admin.** Join and run `.debug on` (or the server `command` op with `{"text":".debug on"}`). Expect `Debug on. buff=<idx:ver> guid=<n>` and the readout ending `all five protections are live on one buff. One icon is correct.`, then `map revealed: on` and `map teleport: on`. Server log: `Debug: grant recorded character=… preExisting=False`, `Debug: edit Immortal IsImmortal=True added=True …`, `godmode: guard added …`. Visually: one buff-bar icon named **Debug** with the debug icon (not the haste icon) and no haste glow; the blood bar stops falling.
3. **Protections.** Read `ProjectM.Immortal` and `BuffableFlagState` on the character with the `components` op; stand in daylight; let a hostile approach (it should not engage). On a disposable character, drive an authorized kill path and expect `godmode: blocked kill site=…` and `Health.IsDead=false`.
4. **Map powers.** Press M: the whole map is uncovered (client log `Debug map: state reveal=True teleport=True flag=True`, Core `WorldMap: reveal full map on`). As a listed admin who has not run `adminauth` (client log `Debug map: state … teleport=True gameTeleports=False`), hold Ctrl+Shift+left click over an open field (**Teleport to map point**, its default; the Keybinds tab lists it under Progenitor): a ring fills in 1 s and you land there; chat `Teleported`; server log `Debug map: teleport <name>: requested (…) chosen (…) moved 0.0 m … result=Teleported via=map`. Release early: client log `Debug map: hold canceled (Released); nothing sent.` Then run `adminauth` in the console (`gameTeleports=True` on the next state) and Ctrl+Shift+left click again: the game's own teleport moves you on the click, no ring appears and the server logs no `via=map` line (one teleport). Run `.debug tp 0 0` (requested, chosen and moved) and `.debug tp 99999 0` (refused, off the world).
5. **Another player.** As the admin, `.debug status <other player's name>` reads their state back to you; `.debug on <name>` applies it to them and they get `An admin turned the debug buff on for you.`; `.debug off <name>` removes it.
6. **A Core hot swap keeps it.** With the buff on, deploy Core to the server again (step 1) and confirm the swap. Expect `Debug: unload reason=Swap, keeping 1 live instance(s)`, `Debug: unload reason=Swap, cleared Immortal + god-mode guard from 1 character(s)`, `Debug: grant kept …`, then from the new generation `Debug: world-ready reconcile over 1 own grant(s): adopted 1, …`. `.debug status` must show the guard on again with `prefixes 3/3` and the all-clear line. A `grant ledger not loaded from the save folder yet` line followed by an adopt is acceptable; a reconcile that never adopts is a failure. Repeat once on the client side (map reveal returns after `asked the server for the map powers`).
7. **Off.** `.debug off`. Expect `Debug off.`, the readout with every protection `OFF`, `map revealed: OFF`; the icon disappears, the blood bar falls again, and M shows only your own exploration (`WorldMap: reveal full map off`). `.debug tp 0 0` now refuses with `the debug buff is not on (.debug on)`.
8. **A non-admin is refused; a listed admin is not.** As the non-admin user (not on the admin list), `.debug on`, `.debug`, `.debug tp 0 0`, `.debug build` and `.debug on <admin's name>` each reply `This command is admin-only.` and change nothing; `.help` does not list `.debug` or `.debug build`, and the chat command hints offer neither. Then, as a player on the admin list who has not run `adminauth` since the server started, `.debug status` is accepted and `.help` lists `.debug` (decided 2026-10-09).
9. Restore the test state (debug buff off everywhere unless the session keeps it on deliberately), disconnect, and leave the client at the title screen.

Bridge-only (focus-free) checks: `{"text":".debug status"}`, `{"text":".debug on"}` and `{"text":".debug tp <x> <z>"}` through the server `command` op, and the `entity` / `components` ops for the character position and components. The [live rig](../tools/live-rig/README.md) reads protection with `.debug status`.

## Sample commands

`samples/Progenitor.Sample` chat commands on the test server (stand next to the spawn for `.rat` — units without a connected player nearby do not path):

**Admin-only commands** use the game's `User.IsAdmin` flag, visible in `users`. Resource grants, replicated spawns, protection changes and operator diagnostics are gated; `.ember` also clamps each call to 50. A non-admin receives `This command is admin-only.` and does not see those commands in `.help`. The command table is the reference. `.patchdemo` uses the supported Bootstrap slot path and remains reloadable; direct unsafe Harmony usage has different restrictions. See [admin gating](modules.md) for the declaration pattern.

| Command | What it does |
|---|---|
| `.ember [count]` | **Admin only.** Give Ember Shards (cloned Crystal), **max 50 per call** — a larger count replies `Clamped <n> to the per-command maximum of 50.` and grants 50. |
| `.joins` | Per-save join counter. |
| `.fx image\|particle\|beam [thin\|red\|blue]\|vignette\|off` | Client-side Effects demo (placeholder LineRenderer for `.fx beam`). `.fx beam` defaults to the game-red castle preset when that sequence GUID resolves, otherwise the thin unlit core; `.fx beam red` / `blue` / `thin` pick a preset. `.fx particle` is Flame 2× + Embers 3× at the player; `.fx particle embers 4` picks one named system. |
| `.beam [seconds] [red\|blue\|coil] [count] [steady\|blast]` `\| link [seconds]` `\| mark [red\|blue] [seconds]` `\| off` | **Admin only.** Server-authored networked beam — every client draws it natively. Bare form: a `ProfessorCoilBeam` entity with synced Start/End, default 8 s, red tint, count 1, `steady` phase, from you to the nearest `CHAR_Critter_Rat` (else 6 m ahead), tracking both; the client re-points it each frame through the sequence blackboard (`Position`/`Rotation`). `link` plays a `NetworkedSequence` between two networked entities (default `AB_Charm_Channeling`, `-389499105`); it reuses the nearest rat or spawns one 6 m ahead as the far end and despawns it with the beam. `mark` plays a single-target `NetworkedSequence` on one entity — the build-mode placement beams, `red` = invalid `29614748` (default), `blue` = valid `-450089786` — which follows that entity. `off` disposes whichever is running. Color and phase tokens may appear in any slot; the first positive number is the seconds — clamped to 19 s on the coil path and **600 s** on `link`/`mark`, and the `link` marker rat is despawned when the beam ends, not left standing. `.fx beam` is the client-only placeholder. |
| `.ui demo\|close` | UI toolkit demo panel. |
| `.rat [distance]` | **Admin only.** Spawn `CHAR_Critter_Rat` in front of you (default 3 m). Reply `Spawned rat index:version at (x, y, z)`. |
| `.rat follow` | **Admin only.** Last rat follows you. |
| `.rat go` | **Admin only.** Last rat walks 6 m ahead. |
| `.rat off` | **Admin only.** Despawn the Sample's rats. |
| `.triggers on\|off\|status` | **Admin only.** `ctx.Triggers` (`SampleTriggers.cs`). `on` arms a 6 m circle, 4 m tall, and a 16×4×16 m rectangle where you stand. Entering the circle spawns `CHAR_Critter_Rat`. Leaving the rectangle logs `Sample triggers:`. The first sight does not fire. Verified live on the isolated test server, 2026-10-08: circle leave at 7 m, rectangle leave at 9.2 m, a rat on return, then `off`. A point 4 m up was not held by teleport. |
| `.castle [inside]` | **Admin only.** Bare form prints your plot summary then the buildings grouped by prefab. `.castle inside` says whether you are standing in your own territory and names which test answered (buildable-tile mask or territory bounds). Reads only; the gate is because it prints entity ids, territory bounds and a full building census. |
| `.epoch` | **Admin only.** Prints `ctx.WorldState.Describe()` verbatim, then whether the handle this plugin stamped at the **first** `.castle` of the session is stale. The stamp is the castle heart's `index:version` plus the epoch it was read in; `.epoch` never touches that entity, it only reports `IsStale`. Log: `Sample epoch: world: epoch=2 loads=1 stamped=2 handle=castle#13 heart 45:1 stale=False`. Before any `.castle`, the second reply line is `No handle stamped yet - run.castle once (it stamps your castle heart), then.epoch.` |
| `.patchdemo on\|off\|ping\|status` | **Admin only.** Demonstrates `ctx.Patches` with a void prefix, postfix and finalizer on the server message helper. `ping` exercises the target; inspect all counters. `off` removes the three registrations; reload must do the same. Bootstrap-owned slots keep this example reloadable. `left` includes other patch owners. |
| `.access` | **Admin only.** Reads `ProjectM.GameDataSystem.RandomLocalizationKeys` through the Sample's **own** generated access layer (`samples/Progenitor.Sample/Access/access.txt` + `Access/Access.Generated.cs`, both checked in) and reports the runtime offset next to the one the dump records, plus the declaration count/hash and Core's resolution line. Log: `Sample access: read ProjectM.GameDataSystem.RandomLocalizationKeys offset=0x100 present=True declared=1 sha256=7e2c85802e78 | access: resolved=1 declared=2`. A member missing on the build replies one sentence starting `The declared member did not resolve on this build:`. |
| `.godmode on\|off\|status` | **Admin only.** Exercises `ctx.GodMode` without adding a buff or Immortal component. Inspect `Describe()` and armed prefix count. Owner `sample.godmode` is separate from Core's `.debug` (`progenitor.debug`); turning either off must preserve the other owner's protection. |
| `.items grant <prefabNameOrGuid> [amount]` / `.items equip <slot>` | **Admin only.** `ctx.Items.Grant` and `ctx.Items.Equip` (`SampleItems.cs`). Amount is 1–10000. Equip slots are the 35-slot bag, 0–34. The reply prints `action`, `added`, `slot` and `equipped`. Source-checked and game-free tested; not live-verified. |
| `.stations list [radius]` / `.stations recipes [id]` / `.stations toggle <recipeNameOrGuid> on\|off [id]` / `.stations status [id]` | **Admin only.** `ctx.Stations` (`SampleStations.cs`). `list` prints recipe stations near you with their `index:version@epoch` ids; the others act on that id (an id from an earlier world load is refused), or on the nearest station within 20 m (refused when you have no character). `toggle` uses the refinement station's own per-recipe switch and refuses on a workstation. Source-checked and game-free tested; not live-verified. |
| `.protect on\|alive\|off` | **Admin only.** `ctx.Debug.Protect`, `KeepAlive` and `Release` on your character (`SampleDebugProtect.cs`). Separate from `.godmode` and from Core's `.debug`. The reply prints `ok`, `immortal`, `guarded`, `ignoredByFoes` and `invulnerable`, and says when a buff removal lands next frame or when another owner's foe-ignoring buff was left on. Source-checked and game-free tested; not live-verified. |
| `.players` / `.players name <name>` / `.players account <platformId>` | **Admin only.** `ctx.Players` (`SamplePlayerDirectory.cs`): online list, name lookup, account lookup. An ambiguous name is listed and not picked. Session text is printed next to `fresh=`. Source-checked and game-free tested; not live-verified. |
| `.form` / `.form begin [name] [seconds]` / `.form return` | **Admin only.** Experimental `PlayerForms` (`SampleForms.cs`), not a `ctx` property. Lists the catalog and `IsReady`, calls `TryBegin`, and tracks any handle with `ctx.Track`. A begin is expected to be refused until a profile is qualified. Source-checked and game-free tested; not live-verified. |
| `.shot` / `.shot reset` / `.shot mode <topdown\|orbit\|free\|static>` / `.shot orbit` / `.shot point <x> <y> <z>` / `.shot free` / `.shot shot <yaw> <pitch> [focus]` | `ctx.Camera` on your client (`SampleCamera.cs`). The server sends `SmSh`; the client log (`Sample camera:`) is the result. `.shot orbit` orbits your character, `.shot point` orbits a world point, `.shot free` is `FreeShotLookAt`, `.shot shot` is `FreeShot` (add `focus` for the focus overload), `.shot reset` returns to top-down. Not admin-gated: it only moves the caller's own camera. Source-checked and game-free tested; not live-verified. Do not load this beside a plugin that also registers `.shot`. NecroArmyExample's camera cues are `.camera`, a different command. |
| `.compat` | Any player. Feature negotiation (`SampleCompat.cs`, mod compatibility C1/C2): replies with the features the server's Sample offers and the caller's client as the server sees it (`ctx.Peers.Of(user)`: Progenitor version, this mod's version, `camera`), then sends the client a versioned `compat.report` message and the client logs its view of the server (`Sample compat: …`). Built and game-free tested (`SampleCompatTests`); not verified live. |
| `Ctrl+F9` (client keybind) | Second Sample keybind, and the one on a **modifier chord** (cfg value `F9 + Ctrl` under `[Keybinds]` in `progenitor.sample.cfg`). Toggles a small panel listing the last three `Sample …` lines this process wrote. The panel is an ordinary `Ui.Panel`, so it takes input capture: **movement stops while it is open**, camera rotation still works, Escape closes it. Registered `allowWhileCaptured: true` so the chord can close the panel it opened. Client log: `Ui: keybind 'progenitor.sample/ToggleLogPanel' bound to F9 + Ctrl (…)` then `Sample logpanel: opened rows=4 (capture is on: movement stops until it closes)` / `Sample logpanel: closed`. |
| `Ctrl+C` / hold `Ctrl+R` (client map keybinds) | The map-scope keybind example (`SampleMap.cs`, `ctx.Keybinds.RegisterInMap` / `RegisterHoldInMap`); both work only while the world map is open. Ctrl+C logs `Sample map: point x=… z=…` for the cursor; holding Ctrl+R and moving the cursor logs `Sample map: measured … m from (…) to (…)` on release, or `Sample map: measure canceled (map closed)`. Off the map: `Sample map: no world point under the cursor (…)`. Client only; nothing is sent to the server. Not live-verified (acceptance procedure in the [Sample README](../samples/Progenitor.Sample/README.md#map-keys)). |
| `F10` → **Sample** rows | Two constrained settings entries so the panel has one row of each shape: `SpawnMultiplier` (`AcceptableValueRange<int>` 1–10, row reads `[1 to 10]`) and `Difficulty` (`AcceptableValueList<string>` `Low, Med, High`, row reads `[one of: Low, Med, High]`). An out-of-set edit is **refused**, not clamped: `Ui: settings progenitor.sample [Sample] SpawnMultiplier rejected '50': outside the values this setting accepts (1 to 10)`. |

`.castle` server log prefix `Sample castle:`. Expected on the live plot (territory 13, TestPlayer, one blood-fountain heart and one outdoor floor) — the two reply lines are pinned by `SampleCastleTests`:

```
Castle #13 lv1 — TestPlayer — heart <index:version>, territory 2639,2899..2831,3011 (192×112 tiles, 0 blocks), floors 2 [decaying, bounds-only]
Buildings (2): TM_BloodFountain_CastleHeart×1, TM_Castle_Floor_Outdoor_Plain01×1
```

Example location arguments (choose coordinates appropriate to the current disposable save):

```
Outside castle #13 — you are at tile 2701,3662, territory is 2639,2899..2831,3011 (checked against the territory bounds).
```

Log lines: `Sample castle: summary user=<index:version> castle=13 buildings=2` and `Sample castle: inside user=<index:version> castle=13`. The heart entity id in line 1 changes on every save reload — entity indices are session-scoped, territory index and `NetworkId` are not. **`bounds-only` and `0 blocks` are the two things to watch**: they become the tile mask and a real block count if the `CastleTerritoryTiles` / `CastleTerritoryBlocks` buffer reads land (guarded, not yet exercised live — see [modules.md](modules.md) Castles). Client `.castle` is a disabled handle: `Castles.ForUser is server-only; the client returns disabled handles. This is logged once.` and the reply is `No castle: you do not own a castle heart on this server.`

`.rat` server log prefix `Sample: rat`. Expect `Units: spawned 'CHAR_Critter_Rat' entity=index:version at (x, y, z)` then `Sample: rat spawned entity=index:version pos=(x, y, z) distance=3`. Client `.rat` is a disabled handle: `Units: Spawn('CHAR_Critter_Rat') is server-only; returning a disabled handle.`

```
$bridgeArgs = Join-Path $env:TEMP 'progenitor-command-args.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"text":".rat"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server command "@$bridgeArgs"
```

## Sample framework checks

These Sample commands exercise the corresponding framework surfaces. Use an admin on the staged test server and an authorized client for visual/input checks. Expected output is a check to perform, not evidence that this documentation update ran it.

1. **`.epoch` - the world epoch.** `.castle` once (this stamps the heart entity with
   `ctx.WorldState.LoadEpoch` and logs `Sample epoch: stamped castle#13 heart 45:1 in world load
   epoch N`), then `.epoch`: expect `world: epoch=N loads=M` and `... IsStale=False: still the same
   world load`. Now make the world change under it - reload the save, or swap Core - and run
   `.epoch` again **without** running `.castle`: expect `IsStale=True... Drop it.` A Core swap is
   enough on its own, because a stamp from an earlier Core generation is stale by definition. A
   second `.castle` deliberately does **not** re-stamp: the demo is about still holding the old id.
   `SampleEpochTests` pins both reply lines.

2. **`.patchdemo` — `ctx.Patches`.** Run `.patchdemo on`, then `.patchdemo ping`; expect one prefix response per controlled invocation and log the patch ids. `.patchdemo off` must remove its prefix/postfix/finalizer registrations (`removed=3`). The global `left` count includes other owners and need not be zero. Reloading Sample with the demo enabled must perform equivalent cleanup on every unload reason, including `Swap`. Direct Harmony patch bodies can pin collectible code; this demo uses Bootstrap-owned slots.

3. **`.access` — generated field access.** Run it twice. Confirm the offset against the current build's interop/dump, a successful member read, and the Sample facade's own `declared`/`interop-verified` report. The shared Core resolver count should grow on the first resolution and remain stable on the second. A Core uninstall resets that cache. Do not report Core's facade verification as verification of a consumer's generated file. Check drift with:

   ```
   dotnet run --project tools/accessgen -- \
     --declarations samples/Progenitor.Sample/Access/access.txt \
     --out samples/Progenitor.Sample/Access \
     --namespace Progenitor.Sample.Access --build 248598344 --check \
     --interop "$VRISING_GAME_DIR/BepInEx/interop"
   ```

   **Supply `--interop` when verifying field declarations.** Without an interop directory, the generator refuses them unless `--unverified` is explicit. That override is an unverified output, not a passed check. Build targets can use `VRISING_INTEROP_DIR` or `VRISING_GAME_DIR`; a machine without the game must retain the corresponding verification limitation.

   ```
   dotnet run --project tools/accessgen -- \
     --declarations src/Progenitor.Core/Access/access.txt \
     --out src/Progenitor.Core/Access/Generated --check
   ```

4. **F10 and Ctrl+F9 - settings shapes, a chord, and input capture (client).** F10: the **Sample**
   group must show `SpawnMultiplier  [1 to 10]` and `Difficulty  [one of: Low, Med, High]`. Type
   `50` into the first and `hard` into the second: both must be **refused** with the allowed set in
   the message and nothing written to `progenitor.sample.cfg` - a value that silently
   became `1` or `Low` fails the rejection check (BepInEx can clamp rather than throw, and
   `AcceptableValueList.Clamp` returns element 0). Then Ctrl+F9: the panel opens listing the last
   three `Sample...` lines this **client** wrote, `Sample logpanel: opened rows=...`, and
   **movement stops** while it is open (the panel takes input capture); camera rotation still works
   and Escape closes it. Ctrl+F9 again closes it - that press only works because the bind is
   registered `allowWhileCaptured: true`. Rebinding it in the panel writes `F9 + Ctrl` (BepInEx
   `KeyboardShortcut` spelling) into `[Keybinds]`.

5. **`.godmode` - the kill-path guard on its own.** `.godmode on` gives `godmode: guard added
   entity=45:1 guarded=1` and a reply ending `godmode: guarded=1 patched=3`. **`patched=3` is the
   assertion**: `patched=0` means the prefix group did not install and the character is *not*
   protected, which the reply says in as many words. Take damage, or drive a kill path from the
   Dev bridge: expect `godmode: blocked kill site=... entity=45:1` once per entity. `.godmode off`
   gives `godmode: guard removed entity=45:1 guarded=0`. This is independent of Core's `.debug`:
   running `.godmode off` must not disturb a `.debug` grant, and neither command's ledger lines
   should appear in the other's log.

   **Check owner independence.** Enable `.debug`, then `.godmode`. Both owners should be listed, with three kill hooks armed. Turning `.godmode off` must leave `.debug` protection; turning `.debug off` while `.godmode` remains must remove only the buff-owned effects and retain the god-mode guard. An owner-removal result is not proof the entity is now unguarded; inspect the final owners/status.

6. **Unload reason.** Every Sample unload logs `Sample unload: reason=X -> keep|sweep plain
   game state; Core-owned handles are swept on every reason either way` (the debug buff that
   used to follow it is Core's now). `Swap`, `Shutdown` and
   `Unknown` must read `keep`; only `Removal` reads `sweep`. `SampleEpochTests` pins the mapping
   against `PluginUnloadReasonPolicy.KeepsState` so the doc and the code cannot drift.
