# Live-test rig

Offline Node tooling that makes a live test session fail closed: every spawned entity is tracked, cleanup is confirmed absent, protection/admin/health/carriers are checked at the end, and every bridge request is saved.

A previous catalog live session left a spawned hostile unit alive, a Removal-based Sample reload dropped the test character's protection, the character was killed, and several readbacks were never saved. This rig exists so those failures cannot be silent.

It talks to the Dev bridge only when you `run` a case or `cleanup` a previous run. `stamps` hashes the client install's content catalog on disk and does not call the bridge. It never points at `C:\VRisingServer`. Nothing else in this folder opens a game process.

## CLI

From the repository root:

```
node tools/live-rig/rig.mjs list
node tools/live-rig/rig.mjs stamps [--game <dir>]
node tools/live-rig/rig.mjs run <case.json> [--out <dir>] [--side server] [--dll <path>] [--force-teardown] [--attach-readback|--no-attach-readback] [--site main-menu|top-tab|shortcut]
node tools/live-rig/rig.mjs cleanup <runDir> [--out <dir>] [--attach-readback|--no-attach-readback]
node tools/live-rig/rig.mjs cleanup --entity <index:version> [--prefab <name>] [--out <dir>] [--attach-readback|--no-attach-readback]
```

`<case.json>` may be a path or a name under `cases/`, `cases-ui/`, or `cases-catalog/`. Default `--out` is `tools/live-rig/runs`. Each run writes `<out>/<UTC>-<case>/report.json` and `trace.jsonl`. `cleanup <runDir>` and `cleanup --entity` write `<out>/<UTC>-cleanup/report.json`.

`--side` selects which process's reload folder and Sample image the swap/removal/reinstall steps act on, and the default side for snippet load. It does not choose the admin user. Admin grant/restore, `.debug`, `.catalogex`, and the admin-state check always use the **server** user entity (resolved once from server `players`/`users`). `--site` (`main-menu` | `top-tab` | `shortcut`) substitutes `@site` on `mods_ui` open steps; omitted, the rig picks `main-menu` when the client has no `Client_0` and `top-tab` when connected, and records that on `startState.site`.

### Status and exit codes

`complete` is the only successful status. A leftover tracked entity is never `complete`. An unconfirmed exists query is never `complete`. An exception after the run directory exists still writes `report.json` with status `aborted` and the error text. Passing UI steps cannot turn an incomplete run `complete`. A protection field marked `unavailable (non-admin refusal)` does not by itself change the status computed from the other checks.

| Status | When | Exit |
|---|---|---|
| `complete` | Every step passed, cleanup confirmed every tracked entity absent, every final check passed | 0 |
| `incomplete` | Steps finished, but cleanup left entities, left unconfirmed exists queries, a final check failed, a teardown step failed, or a required restoration ack was not observed | 1 |
| `aborted` | A step failed, or an exception escaped start/cleanup/checks; cleanup, admin restore and report still ran | 1 |
| (usage error) | Missing command, unknown flag, bad `--side` | 2 |

`--force-teardown` runs after status is decided. It destroys **this run's** `skipCleanupFor` entities and records the result as `teardown` without changing status. It never adopts leftovers from a previous run. Use it only to leave the world tidy after a qualification case that must time out.

A case may also list `"teardown": [steps]`. Those steps run after the step loop — including after an abort or a failed step — and before `cleanupAll`. Teardown steps that need the plugin under test run only when that plugin is loaded. If the reload folder does not contain it (a `.rig-hold` is present, or the DLL is missing) or the last reload event was a removal without a reinstall, the run restores it first — the same hold-restore as the fail-safe — waits for the Sample loaded line, and records that under `report.teardown.restore` with the log lines and timing. A command step in teardown must see a handler line within the bounded wait (`expectLog` on the step, or the command text in the Sample log). Missing that line fails the teardown step. Each result is recorded under `report.teardown.steps` with its own pass/fail. A passing teardown cannot turn an aborted run into `complete`. A failing teardown step makes the run `incomplete` (never `complete`), with the reason naming that step.

`cleanup <runDir>` reads that run's `report.json`, adopts every entity in `cleanup.remaining` **and** `cleanup.unconfirmed` (and `teardown.remaining` / `teardown.unconfirmed` if present) plus leftover **client** carriers from `cleanup.clientCarriers.remaining` and leftover client `NetworkedSequence` rows on the carriers check, into a fresh tracker. Client leftovers are adopted on the **client** side (prefab from the report or a fresh exists readback). Client deltas whose prefab is not `NetworkedSequence` are listed remaining and not destroyed. Status is `complete` only when nothing remains and nothing is unconfirmed. An empty adoption set is reported as `nothing to clean` with status `complete` only when the previous report itself listed no remaining, no unconfirmed, and no leftover client carriers.

`cleanup --entity <index:version>` adopts that one id (it does not read a previous report), then runs the same destroy + confirm path. Optional `--prefab` must equal the prefab name on a fresh exists readback. Status is `complete` only when the entity is gone. A missing entity, a prefab mismatch, and a player/user entity are refused.

Before destroy, `cleanup <runDir>` pins the current server to `report.session` from the original run: `{serverStartedUtc, sampleHash, generation}`. The Dev `ping` has no stable session field, so `serverStartedUtc` is `now − ping.uptimeSeconds` (tolerance 5 s) together with Core `reload.generation` and the Sample image hash. A mismatch is recorded on `refusals` and nothing is destroyed. Each leftover's recorded prefab must also equal the prefab on a fresh `entity` exists readback (`{hash, name}`); a recycled `index:version` with a different prefab is refused, not destroyed. `cleanup --entity` records the same session pin on its report so a later `cleanup <runDir>` of that run can match it.

Both cleanup forms refuse the player's own character and user ids from `players`/`users` without issuing an entity request. After exists, a prefab name matching `CHAR_Vampire*` is also refused. The entity op refuses those too; the rig must not request them.

A run that granted admin and could not restore it prints `admin: NOT restored`, records the restore error on `report.adminGranted`, and is never exit 0.

## Cases

| Case | What it does |
|---|---|
| `cleanup-ok` | Spawn a rat 3 m from the player, wait 1 s, destroy it, confirm absent. |
| `m2-12-key-table` | Paired item-key agreement (M2.12). **Acceptance procedure — not run.** PrfI-before-PrfD, matching ember apply `index=N`, `.ember` inventory guid 480926629 on both sides, `.content define` must not move ember. |
| `m2-12-incompatible-client` | Old client, no `PrfI` handler. **Acceptance procedure — not run.** Dropping `PrfI` is not a pass: client must log the missing-ledger refusal and ember stays unusable. Requires a mixed-generation pair. |
| `m2-4-crystal-maxamount` | Paired Crystal `MaxAmount` (M2.4). **Acceptance procedure — not run.** Baseline B after unload, 40 after load with `sent PrfE` / `applied PrfE`, 40 after Sample swap, B after unload. A failed restore fails the step. `--dll` required. |
| `cleanup-timeout` | Spawn a rat and skip cleanup. The run must be `incomplete` and list the rat. |
| `swap` | Spawn a hostile `CHAR_Bandit_Thief`, read Health, then swap the Sample image **three times** (`--dll`, same file is enough: the rig bumps LastWrite so Core still Swaps). The rig destroys the thief and `existsSweep` must pass before each swap. After each swap it waits for the Sample load line (no reconcile wait: the debug buff is Core-owned), then records protection, player identity, `ping.images` hash, handshake/connection, and carriers leftover. A dropped guard after the swap fails the swap step (see below). |
| `swap-client` | Three **client** Sample swaps (`--side client`). No hostiles. Each cycle warms and borrows a tracked Charm particle, copies Sample into the **client** reload folder (LastWrite bump; never remove/reinstall; never a Core swap), waits for the client load line plus `Sample handshake: … on the client side` (no server reconcile wait), then checks `Effects: disposed`, connection retained, exactly one `FROSTSTYLE client enabled=` line per toggle, and a subsequent borrow/dispose. See [Client swap](#client-swap). |
| `removal-reinstall` | Same hostile setup, remove the Sample (protection must still pass: the debug buff is Core-owned), then reinstall (protection must pass; the rig rearms only when the readback is not ok). |

UI cases under `cases-ui/` are focus-free Mods workspace procedures. They talk to the client Dev `mods_ui` op (`status` / `open` / `close` / `back` / `select` / `search` / `scroll` / `widget_read` / `widget_click` / `native_tab` / `resolution`; each result is `{ok, action, before, after, detail, elapsedMs}`). Status fields are `open` / `site` / `plugin` / `page` / `modsCapture` / `inputCapture` / `capture` / `panes` / `observers` / `width` / `height` / `mode` (plus `curtain` / `pageId` / `search` / `entries`). `modsCapture` is true only while the Mods workspace owns input capture/curtain. `inputCapture` is global `Ui.IsCapturingInput`. `capture` is the global alias of `inputCapture` (accepted on the live object, deprecated for case expects). Connected in-game `inputCapture` stays true with Mods closed on the world HUD; closed expects use `after.modsCapture=false`, never `capture` or `inputCapture`. `open site=top-tab` brings up the fullscreen menu itself and reports `detail.menuOpenedBy`; otherwise it fails with `detail.error` `"top-tab button not attached"`. Do not prime with `shortcut`. `close` is the full owned close. `back` returns from settings/custom to overview. Closed mutations refuse with `detail.error` `"workspace closed"`. Run with `--side client`. **Acceptance procedure — not run** in this tree. Every case records the live resolution from `status`, and teardown closes the workspace, restores that recorded `width`/`height`/`mode`, and asserts `after.open=false`, `after.modsCapture=false`, and `after.panes` equal the recorded baseline. A main-thread timeout fail-fasts later restore/close (it does not send them).

Gate 3/4 cases (`ui-sample-swap-page-open`, `ui-core-swap-open`, `ui-core-swap-main-menu`, `ui-removal-reinstall`, `ui-settings`, `ui-disconnect-reconnect`) use the current `mods_ui` argument contract: `select.page` is `overview` / `settings` / `custom` (custom page title in `group`), panes are `list` / `content`, and status fields are `modsCapture` / `inputCapture` / `capture` / `panes` / `observers` / `width` / `height` / `mode` (resolution restore requires `mode`). They record baseline resolution from `status`. An `op` step may set `pollMs` + `timeoutMs` or `poll: {intervalMs, timeoutMs}` to retry until `expect` holds (used for `reload_status`, ping/users wait, and settings note reads); the step records `elapsedMs`. `expect` path values may be `@record:…` or `{ "neq": … }` (generation moved). A recorded `op` stamps `since` / `logSeq` / `seenAt` from a socket `logs` snapshot taken before the call so a later `log` `count` step can anchor on it. `log` `count` 0 is an observation window (`timeoutMs`) that passes when no matching line arrives.

| Case | What it does |
|---|---|
| `ui-baseline` | Main menu (`world: main-menu`): `mods_ui` status closed; ping ×3; soak 2 min closed, no capture. |
| `ui-baseline-connected` | Same closed soak while connected (`world: connected`). |
| `ui-open-close-cycles` | 20 cycles: open (`site` `main-menu` or `top-tab`), status, close, status. Each close asserts curtain/`modsCapture` released and `panes` equal the recorded baseline. One capture every 5 cycles. |
| `ui-soak-menu` | Open at the main menu, soak 15 min, no capture, then close. |
| `ui-soak-menu-capture` | Same with a PrintWindow capture every 60 s. Run the no-capture control first. |
| `ui-soak-settings` | Connected: open top-tab (op brings up the fullscreen menu), select Sample Server settings, soak 15 min, no capture, close. |
| `ui-soak-settings-capture` | Same with a capture every 60 s. |
| `ui-native-tab-return` | Open top-tab (`detail.menuOpenedBy`); `native_tab` `tab` (other); assert closed+`modsCapture` released; open; `native_tab` `same`; assert; repeat 3×. Keeps `native_tab` (does not use `back`). |
| `ui-resolutions` | Record baseline resolution from `status`; for 1280×720, 1920×1080, and the recorded baseline: set width/height/mode, open, `widget_read`, capture, close. Teardown restores the recorded baseline. |
| `ui-list-and-search` | Select Sample overview; search `zzz`; search `""`; content scroll top/bottom; custom Example page; `back` to overview; close; reopen. |
| `ui-sample-swap-page-open` | Connected top-tab. Three client Sample swaps (`swap` + LastWrite bump, `--dll`) with custom page Example open. After each: owned page closed, sidebar lists Sample, exactly one `Sample menu: action handled.` line per `widget_click` "Try an action" (`sinceRecord` on the click, `count` 1), connection retained, observer/pane counts equal the open baseline, capture. |
| `ui-core-swap-open` | Connected top-tab. Three compatible client Core swaps (`op` `core_reload`, poll `reload_status` until `status.inFlight` false, generation moved, `lastReload.outcome` ok, `previousAlive` false). Entry attached once, no duplicate observers/panes, workspace reopenable, connection retained. |
| `ui-core-swap-main-menu` | Same Core swaps at the main menu. No connection assertions. |
| `ui-removal-reinstall` | Connected. Remove Sample with Example open; page closed; `widget_click` after removal expects `ok` false. Reinstall; reopen works. Not swap acceptance. |
| `ui-settings` | Connected. Personal ShowHints persist; synced-readonly `source` and invoked 0; ExampleLimit non-admin refusal, admin 12, bounds 999, pending close (no dead-widget log); restore 10 and admin false. Inventory kinds from Sample/Core (`Running` / `Loaded on this client`); no Installed/Enabled. Stale expected-value is not exercisable through the UI. |
| `ui-disconnect-reconnect` | Open connected, `op` disconnect, poll ping until no `Client_0`, workspace closed and inventory `Server mod list unavailable`. `op` connect `127.0.0.1:9976`, wait users connected+handshake ok, reopen, measure handshake line → first `source=admin` readback. |
| `ui-disconnect-only` | Poll `disconnect` `statusOnly` until a leftover leave latch is clear (`requested` false, `state` not `superseded`), request the leave (`requested: true`), wait for no `Client_0` and the inventory-cleared handshake. Does not reconnect. `world: any`, `endWorld: main-menu`. |
| `ui-reconnect-only` | From the main menu, `connect` `127.0.0.1:9976`, wait Client_0 and handshake-ok. Lidgren if LanMode-only. `endWorld: connected`. |
| `ui-main-menu-set` | Connected stock disconnect (same leave as `ui-disconnect-only`) plus a `sequence` of the main-menu cases. Does not inline soak. |

Main-menu path: run `ui-disconnect-only` (or `ui-main-menu-set`) so the client leaves with `op disconnect` and ping has no `Client_0`; then `ui-soak-menu`, `ui-core-swap-main-menu`, and `ui-open-close-cycles --site main-menu`; finish with `ui-reconnect-only` (`connect` 127.0.0.1:9976; pass `args.protocol` Lidgren if the test server is LanMode-only). Soak is not inlined in `ui-main-menu-set`.

Catalog cases under `cases-catalog/` are acceptance procedures. `active-ring-unload` parses the frost-dash ring id from a log line, asserts it exists, removes Sample, waits (bounded, since the ring log) for `FROSTDASH cleanup alive=` with `alive>=1` and the tracked ring id in `destroyed=[...]` (recorded as `ringUnloadCleanup`), keeps the log-clock `maxAgeMs` 1500 check from Start to `Progenitor.Sample.dll removed`, then `existsAt` expect false. That cleanup line is the disposal proof (unload destroyed a ring that was alive). An older Sample image that does not log it fails the log step and the case stays **aborted**. `.frostdash off` is teardown so it still runs after abort: the leftover `.rig-hold` is restored first, then off must match Sample's `FROSTDASH cleanup alive=` line (`expectLog`). Injecting the command while Sample is still on hold is not a pass. `pool-reuse-identity` prepares the census DLL once (`snippetPrepare`), then load-only censuses: structured POOLID identity and `_BaseColor` at baseline, tinted playback (**T** = active slots carrying Sample `_BaseColor`; fresh allocations count), and rest (every id in T must be present, inactive, and without an override — a missing T id fails; a destroyed/recycled instance cannot pass as restored), then `.froststyle off` (server `FROSTSTYLE enabled=False`, client `FROSTSTYLE client enabled=False`) and a `poolReuse` step that retries `.frostdash dash` + 1500 ms load-only census up to 3 times until a nonempty untinted subset **R** of **T** is active again as present rows. Empty **R** is `reuse not observed`. A Sample with no `.froststyle` handler fails the off step (`no handler line for .froststyle off`). Then `.froststyle on`, a final rest census, and teardown `.frostdash off` (`FROSTDASH cleanup alive=`). **Verified live** 2026-09-07 (`reviews/2026-09-07-catalog-cases-10`): T=10, R=T on attempt 1, capture unavailable (that run had no capture step). The case now warms PrintWindow (`capturePrepare`), starts one tinted-window frame at +100 ms, and starts a reuse-window burst at +100/+500/+900 ms while the +1500 ms census stays on schedule (shots are fire-and-forget children; `censusDelayMs` above 2000 fails `census late`). `capture.available` is true only when those PNGs were saved; `visual.verified` stays false until a person or the reviewing agent views the frames. Identity is Unity instance id plus renderer id observed present at rest between the two activations. See [Capture](#capture).

## Snippet

A `snippetPrepare` step (or `snippet` with `"prepare": true`) copies `file` onto `tools/scratch/Snippet.cs`, sets that copy's mtime to now (`fs.utimesSync`), records that copy time, and builds `tools/scratch/Progenitor.Scratch.csproj` with `--no-incremental`. It does **not** copy the DLL into the reload folder.

Windows `CopyFile` keeps the source file's mtime, so incremental MSBuild would otherwise treat a different `Snippet.cs` as unchanged and keep the previous Scratch DLL. Prepare deletes the previous output DLL and passes `--no-incremental` so the build rewrites it. It then requires the output DLL's mtime to be later than the recorded copy time. A non-zero build, a missing DLL, a missing mtime, or an older mtime fails fast. An unchanged DLL hash after a different recorded source is not treated as a stale build: the same source can already have been compiled through `tools/scratch` (for example an offline compile check that restored the previous `Snippet.cs`). The accepted DLL sha256, source hash, copy time, and `buildElapsedMs` are recorded (`dllHash`). Timed cases must run this **before** the dash they want to observe.

A later `snippet` step for the same `file` only loads that already-built DLL: it verifies the source bytes and DLL hash still equal the prepared values (no `dotnet build`), copies `Progenitor.Scratch.dll` into the target side's reload folder, waits for `Reload: image Progenitor.Scratch.dll sha256=<prefix>` matching `dllHash`, then the `scratch: result` line. It records `elapsedMs` from step start to that result line. A load with no matching prepare fails `snippet not prepared`. Source or DLL bytes that drifted after prepare fail without loading.

Default snippet side is `--side`. Cases that read client Unity objects set `"side": "client"`.

`swap` copies `--dll` onto the live reload folder, then sets that copy's LastWrite to now (`fs.utimesSync`). Reinstall and hold-restore do the same after they put Sample back in the folder. Windows `CopyFile` keeps the source mtime, so a second copy of the same file would otherwise leave LastWrite identical to the loaded image and Core would skip the Swap (`loaded.LastWrite == write`). Core's plugin reloader compares LastWrite only: it does not hash the bytes to decide whether to Swap. A same-byte copy with a bumped LastWrite Swaps too, and that Swap of identical bytes is still a real generation swap (unload + load). After the copy, the rig checks that the destination LastWrite differs from the LastWrite it recorded for the loaded image (or, on the first copy, from the file's own pre-copy LastWrite) and fails fast naming that unchanged stamp instead of waiting 20 s. Distinct swap2/swap3 images can be set as per-step `dll` paths; `<path>` on every swap step is `--dll`.

```
node tools/live-rig/rig.mjs run swap --dll samples/Progenitor.Sample/bin/Debug/net6.0/Progenitor.Sample.dll
```

The debug buff (`.debug`, Core id `progenitor.debug`), its grant ledger and its world-ready reconcile live in Core. A Sample swap does not touch them and writes no reconcile line, so after each Swap the rig waits only for the Sample `Reload:` changed/load lines and records `reconcile: {ok: true, skipped: true, reason: "protection is Core-owned; a Sample swap does not reconcile"}`. Then it reads `.debug status`. If that parse is not ok (`flags=7` but `guard=False`, a missing `flags=` line, or any other failed parse), it records the finding `protection dropped by swap` and the **swap step is FAILED**. A later `.debug on` rearm does not make the step or the run succeed: a Sample swap that drops the Core-owned protection is a Core defect, and the run ends `aborted`, never `complete`. Only a Core install/swap or a new world writes `Debug: world-ready reconcile …` (`isDebugReconcileReadyLine`; the `no server world yet` deferral does not count). Each swap step also records `playerIdentity` (character id must be unchanged), `pingImages` / `sampleHashAfter` from `ping.images`, `handshake` (Sample handshake or Core Handshake line), and `carriers` leftover versus the pre-copy snapshot.

### Client swap

`swap-client` is the same copy-and-LastWrite path on `--side client`. It never removes the DLL, never reinstalls from a `.rig-hold`, and never swaps Core. `--side client` already selects the client reload folder (`ping.reloadFolder` for that side); admin grant, `.debug`, `.catalogex`, and protection stay on the **server** user.

```
node tools/live-rig/rig.mjs run swap-client --side client --dll samples/Progenitor.Sample/bin/Debug/net6.0/Progenitor.Sample.dll
```

After each client Swap the rig waits for `Reload: loaded Progenitor Sample … from Progenitor.Sample.dll` (already in the Sample swap matchers) **and** the Sample client attach line `Sample handshake: declared N row(s) on the client side costing …` (`SampleHandshake.AttachedLine` via `SampleLog`; live cases-3 `charm-client-handshake-logs.json` seq 5198). It does **not** wait for a world-ready reconcile (no Sample swap writes one; the Core `Debug: world-ready reconcile` line is server-side). `reconcile.skipped` is recorded. A dropped server debug-buff guard still fails the swap (fail-closed); a client check is not proof of server protection. Connected user + `users.handshake` ok after the swap are required when those fields are present.

Each cycle establishes a tracked Charm borrow (`.catalogex warm` then `.catalogex borrow`, client `CATALOGEX borrow` / `CATALOGEX status` lines) so unload teardown is exercised, then observes `unloaded Progenitor Sample` and `Effects: disposed Particle` for that borrow. `.froststyle off` then `.froststyle on` must produce **exactly one** `FROSTSTYLE client enabled=` line per toggle (`count: 1` on the client log step; two lines means a leftover handler). After the bounded wait, `log` counts matching lines in the collected window (`since` is the latest seq at step start when `sinceRecord` is omitted): more than `count` fails with `duplicate handler: <n> lines matching <regex>`; fewer fails with `expected <count> matching line(s), got <n>`. `waitForSingleFroststyleClientLine` in `lib/reload.mjs` is the same check for offline Froststyle experiments. Then `.catalogex borrow` / `.catalogex dispose` on the new generation.

No hostiles are spawned; `existsSweep` must still be empty before each swap. Client `NetworkedSequence` leftovers from warm are destroyed at cleanup as today and recorded as a finding (`client carrier outlived server disposal`). That recovery is not automatic-lifecycle proof. Intended finals: froststyle enabled=True, frostdash off, admin false, no test resources.

Removal no longer affects protection: the debug buff is Core-owned, so a Sample Removal or reinstall leaves it applied. `removal` records the post-removal readback; `reinstall` reads protection back and rearms with `.debug on` only when that readback is not ok (`protectionAfter.rearmed`, with the failed readback on `protectionAfter.before`).

## Capture

Focus-free screenshots of the V Rising **client** window. The step spawns Windows PowerShell 5.1 (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`) with `tools/observe.ps1 shot`. It never runs `key` / `type` / `move` / `click` / `scroll`, never passes `-AllowGameInput`, never inherits stdin, and never uses `-Method Screen` or `-Method Auto`. Capture is not a workaround for focus.

```
<powershell 5.1> -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 shot -Window client -Out <png> -Region window -Method PrintWindow -MaxWidth 1920
```

`-Window client` is the V Rising process under the game install (`VRising.exe` whose path starts with `-GameDir`). `-Method PrintWindow` asks that window to redraw; it does not steal focus or read the desktop. `-Region window` is the window rectangle (`monitor` / `virtual` are refused: PrintWindow cannot capture them). `-MaxWidth 1920` is the live-rig default (omit `0` on the step to keep native resolution). Each shot is bounded (`timeoutMs`, default 10 s). Stdin is ignored.

PNGs go under the run directory as `captures/<name>-<ISO>-f<n>.png` (ISO timestamp with `:` replaced so the name is a valid Windows path). The step result records `path`, `bytes`, `wallMs`, `startedMs` / `endedMs` (and ISO `startedAt` / `endedAt`), `traceIdRange` (bridge `trace.jsonl` ids at start and end of the shot), `exitCode`, and stdout/stderr tails. PrintWindow takes about 1 s, so frame content time is between start and end. A non-zero observe exit, a missing file, a zero-byte file, or a timeout fails the step when `requireFrames` is true (the default). A capture never starts while a main-thread op is in flight on that side (`bridge.mainThreadInFlight`); the child waits until the gate is idle. Soak shots are scheduled strictly between samples (never on the same tick as ping / `bridge_status` / logs) and recorded on `captureWindows[]` with `startedAt` / `endedAt` and the nearest ping window.

`burst: {count, intervalMs}` plus optional `delayMs` **starts** several child processes on a schedule from step start (or, inside `poolReuse`, from the reuse dash). Each shot is `child_process.spawn` (stdio ignored on stdin, stdout/stderr piped to tails). The timed wait does not await PrintWindow. Optional `waitMs` sleeps until that offset, records `censusDelayMs`, and fails `census late: <ms> ms` if it exceeds `maxCensusDelayMs` (default 2000). `poolReuse` starts reuse frames at +100/+500/+900 ms, censuses at +1500 ms, then awaits the children (bounded, in-flight cap). `capturePrepare` (or `capture` with `"prepare": true`) takes one warmup shot, records `warmupMs`, and **deletes** the file — it is not evidence. Run it before a timed window so the first kept shot is fast.

Asynchronous helper children (captures, `observe.ps1` input, snippet builds, including `poolReuse` and soak shots) are tracked in one registry. On SIGINT/SIGTERM or an internal abort the rig refuses new children, sends SIGTERM, waits a 2 s grace, force-kills leftovers, and waits for them to exit. No orphan helper continues touching the client after a stop. The report records `cancelled: {requested: true, children, killed, waitedMs}` when a stop was requested.

A saved frame is inspectable evidence only after a person or the reviewing agent has viewed it. The rig does not analyze pixels. `visual.verified` stays `false` until a reviewer fills `verifiedBy` / `verifiedAt`. Readback success (for example nonempty **R** on `poolReuse`) is separate from visual success. `capture.available` is `true` with `paths` only when frames were written.

## OS-injected input cases

`mods_ui` `widget_click` / `open` / `native_tab` invoke owned handlers. That evidence is **`handler-invocation`**. These cases drive the real keyboard and mouse through `observe.ps1` (`SendInput`) and are labeled **`os-injected`**. A human hand is **`physical`**. Never mark one as another.

`input` `{kind:"input", action: click|move|scroll|key|type, target?: {rectOf, offset?}, keys?, text?, label: "os-injected"}` calls `mods_ui rects`, resolves `target.rectOf` against the live `detail.rects` rows (`id` / `kind` / `label` / `plugin` / `index` / `controls`; Windows client-area pixels, with `visible` / `interactable` / `occludedBy`), refuses a hidden, inert, or occluded control (a miss names the available ids), computes the click point (rect center plus `offset`), and spawns Windows PowerShell 5.1 with `tools/observe.ps1` bounded at 10 s. It records the rect, point, observe stdout/exit, before/after `mods_ui` status, and the evidence label. `assertWindow` `{kind:"assertWindow", expect: {clientWidth, clientHeight, mode}}` calls `mods_ui window` (nested `detail.client.width` / `.height`, Screen size, `fullScreenMode`, display size) and fails on mismatch.

Case-language `rectOf` selectors map to those live ids:

| `rectOf` | Live `rects` match |
|---|---|
| `topTab` | `id` `top-tab` |
| `mainMenuButton` | `id` `main-menu` |
| `close` | `id` `close-back` |
| `search` | `id` `search` |
| `@site` | `topTab` or `mainMenuButton` from the run site |
| `sidebarRow:<plugin>` | `id` `sidebar-row` with that `plugin` |
| `nativeTab:<label>` | `id` `native-tab` with that `label` |
| `scrollbar:<pane>:handle` / `:track` | `id` `<pane>-scrollbar-handle` / `-track` (`list` or `content`) |
| `widget:<label>:input` / `:toggle` / `:apply` | `id` `widget` with that `label`, then its `controls` entry (`apply` also matches a sibling widget labeled `Apply <label>`) |

Command lines issued (coordinates are client-area pixels; no `-ScreenCoords`):

```
<powershell 5.1> -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 click -Window client -X <n> -Y <n> -AllowGameInput -Json
<powershell 5.1> -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 move -Window client -X <n> -Y <n> -AllowGameInput -Json
<powershell 5.1> -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 scroll -Window client -X <n> -Y <n> -Delta <n> -AllowGameInput -Json
<powershell 5.1> -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 key <chord> -Window client -AllowGameInput -Json
<powershell 5.1> -NoProfile -ExecutionPolicy Bypass -File tools/observe.ps1 type <text> -Window client -AllowGameInput -Json
```

Prerequisite from `observe.ps1`: the workstation must be **unlocked** (input is refused with exit 3 while locked; there is no override), and the target is brought to the **foreground** before SendInput (exit 4 if focus does not arrive). `-AllowGameInput` is required for the V Rising client. Hands off the keyboard and mouse until the step finishes. `shot` JSON reports `sourceWidth`/`sourceHeight` for the captured rectangle and `width`/`height` for the saved file.

**Acceptance procedure — not run** in this tree. `--side client`. Each case records baseline resolution from `status` and teardown closes the workspace, restores that `width`/`height`/`mode`, and restores admin when the case granted it.

| Case | What it does |
|---|---|
| `input-entry-buttons` | `world: any`, `@site`. OS-injected click of `mainMenuButton` (main menu) or `topTab` (connected); click `close`; assert closed. |
| `input-list-and-scroll` | Click a sidebar row and assert selection; click the list scrollbar handle and the content scrollbar; assert `after.scroll` changed. |
| `input-settings` | Click `widget:ExampleLimit:input`, type `12`, click Apply; poll Saved as admin (rig `set`); restore 10 with polled Saved before dropping admin. |
| `input-native-tab-return` | Click native `Spellbook` while Mods is open (closed and released); click it again when already selected. |
| `input-shortcut-and-escape` | Press the configured shortcut from gameplay (`after.shortcut`, else the case `shortcut` field); assert open; Escape (documented hide, not `ModsMenu.Close()`). Type into search; no client keybind/ability lines in that window; `modsCapture` released after close. |
| `input-resolutions` | `resolution` `windowed` 1280×720 then `assertWindow`; open; capture both panes (native pixels); `rects` for list and content; same at 1920×1080; restore the recorded baseline (`detail.client.width` / `.height`) and `assertWindow`. |

## Admin grant and restore

`.debug status` and `.debug on` (Core) are admin-only server chat commands. When the test user is not admin at run start, the rig grants `User.IsAdmin=true` through the bridge `set` op on the **server** (`entity` = the server user entity, `component` = `User`, `fields.IsAdmin` = `true`), records `report.adminGranted {userEntity, userName, at, restoredAt}`, and restores `IsAdmin=false` in a `finally` that runs on every path: exception before start state, exception in cleanup or any check, exists-poll timeout, and step abort. Restore is attempted even when the report cannot be completed; its outcome or error is written on `adminGranted`. The start-state protection readback runs **after** that grant so a non-admin user does not produce a false "no `flags=` line" baseline. The admin-state check then expects the original value.

If the user is already admin, no `set` is issued and `adminGranted` is `null`.

Protection is a precondition. After the grant, the start-state `.debug status` readback runs. If it is not ok, the rig rearms with `.debug on` as the test user and re-reads the `flags=` line **before any step**. `startState.protection` is `{initial, rearmed:true, after}` when that rearm ran, or `{initial, rearmed:false}` when the initial readback was already ok. If the post-rearm readback is still not ok, the run is `aborted` with reason `protection could not be armed`, no steps run, and cleanup / restore / final checks still run.

The final `.debug status` readback requires admin. If the live user is not admin (the case dropped it, teardown restored it, or the run-level grant was already restored), the rig re-grants **for that one readback** and logs `admin: re-granted for protection readback`. That elevation is explicit and logged; it is not a silent grant to paint the report green. `adminState` is the last admin readback and expects the original value. A non-admin refusal (`This command is admin-only.`) is never parsed as `guard=False`. The check is recorded `protection: unavailable (non-admin refusal)` and does not by itself fail the run; other checks still decide `complete` vs `incomplete`. An authorized `flags=` parse that is not ok still fails the run.

Case-level restorations that need an acknowledgment (settings Apply, admin `set`, resolution) record `ackRequired` on `report.restore`. Settings Apply is not restored until a later `widget_read` shows `Saved by the server.` **and** a value readback (a queued Apply or local textbox is not proof). Admin is acknowledged by a `get` (or `users`) readback of `IsAdmin`. Resolution is acknowledged by a matching `window` / `status` / `assertWindow` readback, not the resolution mutation itself. Unconfirmed restoration acks leave the run `incomplete` with `restorationUnconfirmed: [<items>]`, a `restoration unconfirmed: <item>` finding, and `adminRestoredDespiteUnconfirmed: true`; the `finally` path still restores admin so a test user is not left elevated.

A case may set `"world"`: `"connected"` (default), `"main-menu"`, or `"any"`. Default `connected` is today's start: pick a player, grant admin if needed, arm protection (`.debug`), snapshot carriers, and run player-health / protection / admin / carrier final checks. `"main-menu"` asserts the opening client ping has no `Client_0` world (abort with a reason that names that world otherwise), skips admin grant, protection arming, player health, and carrier queries that need a player position, records `startState.world = "main-menu"`, and still runs log-signal scanning, remaining-entity (empty tracker), and UI teardown. `"any"` detects `Client_0` on that ping and applies the matching rules, recording the detected world on `startState.world`. A case may set `"endWorld"`: `"connected"` or `"main-menu"` so those final checks (and client-carrier cleanup) follow the world the case ends in; omitted, they follow the resolved start world.

`startState.sampleHash` is the server Sample image hash taken from the opening ping (also stored on `images.server.sampleHash`) so a swap run can show before/after hashes. `startState.clientSampleHash` is the client Sample hash from the same opening pings (`images.client.sampleHash`). A `--side client` swap records `sampleHashAfter` from the **client** ping.

`report.session` is `{serverStartedUtc, sampleHash, generation}` from that same server ping. Ping has no session UUID: `serverStartedUtc` is derived as now minus `uptimeSeconds`, compared later with a 5 s tolerance. `cleanup <runDir>` refuses to destroy when this pin does not match the current ping.

Every run report also records catalog build stamps at top-level (where `tools/catalog/lib/rigRuns.mjs` `buildStampsFromRun` reads `contentCatalogSha256` and `gameIdentity`). Missing values are `null` plus a `stampErrors` entry; they are never guessed. Each framework side is composed only from that side's own ping — a client Sample change cannot be hidden by a populated server ping. A missing ping is `null` for that side, not a hybrid filled from the other side.

| Stamp | Source |
|---|---|
| `contentCatalogSha256` | sha256 of the client install's `VRising_Data/StreamingAssets/ContentArchives/archive_dependencies.bin` — the same hash `index-catalog.mjs` writes as inventory `source.catalogSha256`. Read-only. `node tools/live-rig/rig.mjs stamps` prints this value with no bridge call. |
| `gameIdentity` | From ping, as `vrising/<version>+r<revision>+b<build>/<runtime>` (recipe form, e.g. `vrising/1.1.13.0+r99712+b17/net25`). Mapping: `ping.gameIdentity` used as-is when it starts with `vrising/` (Dev ping returns that Compose string, or null); else compose `Major.Minor.Patch.Revision+rVcsRevision+bBuild/netNetworkVersion` from `versionData` / `gameVersion` / `version` (object) / `game` / those fields on ping itself. If server and client pings both carry an identity and they disagree, `gameIdentity` is `null`, both values are stored on `gameIdentitySides`, and `stampErrors` records the disagreement. |
| `frameworkSides` | `{ server, client }`. Each value is `generation:<n>; core:<hash>; dev:<hash>; sample:<hash>` from that side's ping, or `null` when that side has no ping. Generation is `reload.generation` or the Core `images[]` row. Hashes are full sha256 (not log prefixes) from `coreHash` / `devHash` / `sampleHash` or `images[]` rows named `Core`, `Progenitor.Dev`, `Progenitor.Sample` (file stems; `.dll` / short names still accepted). Plugin rows have no generation; `sha256` and `images` may be null. A missing part is the token `null` inside that side's string. |
| `framework` | Always `null` on new reports. A non-null string is a legacy combined stamp; readers must not treat it as a current per-side identity. |
| `stampErrors` | One entry per missing side, missing part, unreadable catalog, missing game identity, or disagreeing game identities. Framework entries name `side`. |
| `stampPhases` | `{ start, final }`, each `{ frameworkSides, contentCatalogSha256, gameIdentity, at }`, captured at run start and at the final readback. |
| `stampTransition` | `true` when any side's framework string differs between `stampPhases.start` and `stampPhases.final`. The report also records a finding `image changed during run: <side> <before> -> <after>`. Absent when the images did not change. |

`stamps` is offline. `--game` defaults to the client install (`PROGENITOR_GAME_DIR` or `C:\Program Files (x86)\Steam\steamapps\common\VRising`).

## Reload fail-safe

Swap, removal, and reinstall all refuse to change the Sample file while a hostile tracked entity still exists. After the hostile destroy, `existsSweep` must run and pass; an accepted destroy request is not enough.

Removal matchers are Sample-specific (`Progenitor.Sample.dll removed` / `unloaded Progenitor Sample`). Scratch or Dev unload lines cannot satisfy them. Swap and reinstall load lines are the same: `loaded Progenitor Sample` from `Progenitor.Sample.dll`.

If the Removal unload lines do not arrive within the timeout, `removal` moves the `.rig-hold` file back, reads protection back (rearming only if not ok), and returns a step failure that includes the reinstall outcome. After the steps, if a hold file is still present (successful removal without a later reinstall step), the run puts Sample back before teardown commands and again before final checks if it remains, and rearms only if the protection readback is not ok. The teardown restore is `report.teardown.restore`; the later fail-safe is still `report.holdRestore`.

## Trace

Every bridge call appends a `type: "request"` line to `trace.jsonl` **before** sending, then a `type: "response"` / `"error"` / `"timeout"` line after. Both share an `id`, so a kill mid-call still leaves the request on disk.

## adopt, logEntity and existsAt

| Kind | What it does |
|---|---|
| `adopt` | `{kind:"adopt", entity:"index:version", name, hostile:false, prefab?:"CHAR_Critter_Rat"}`. Verifies the id exists through the entity op (and, when `prefab` is given, that the exists readback's prefab name matches) and tracks it for cleanup. Fails on a missing entity, a prefab mismatch, or a player/user entity (`CHAR_Vampire*` prefab, or the character/user ids from `players`/`users` — those ids are not requested). |
| `logEntity` | `{kind:"logEntity", side, sinceRecord or filter, regex, name}`. Waits for the latest matching log line, captures `index:version` from group 1, and adopts it under `name`. Not hostile. Not destroyed on cleanup unless `"track": true`. |
| `log` | `{kind:"log", side, sinceRecord or filter, regex, record?, count?}`. Waits (bounded) for a matching log line. Without `sinceRecord` (and without an explicit numeric `since`), `since` is the latest log seq on that side at step start so only lines emitted after the step began can satisfy it; the step result records that `since`. Optional `count` then counts matching lines in that collected window only: more than `count` fails `duplicate handler: <n> lines matching <regex>`; fewer fails `expected <count> matching line(s), got <n>`. `count` 0 uses `timeoutMs` as an observation window and passes when no matching line arrives. Every `count` log in a shipped case must set `sinceRecord`. `@tracked:name` in `regex` is replaced with the adopted entity id, escaped and wrapped so it matches only as a whole `index:version` token (delimited by a non-digit-or-colon on both sides). `destroyed=[17:1]` and `destroyed=[7:10]` do not match tracked `7:1`; `destroyed=[7:1]`, `destroyed=[3:2,7:1]`, `destroyed=[7:1,9:9]`, and `ring=Entity(7:1)` do. Optional `maxAgeMs` / `ageClock` (`wall` or `log`) measure from the `sinceRecord` stamp to this line. |
| `existsAt` | `{kind:"existsAt", entity:"@tracked:name", expect}`. Checks exists now. Optional `after` waits for a log line first. Optional `maxAgeMs` fails when the adopted entity is older than that window at the check. Default `"ageClock": "wall"` measures `ageMs` from `logEntity.seenAt` to `clock.now()`. `"ageClock": "log"` measures `logAgeMs` from the log timestamp of the `logEntity` line to the `after` line. Both clocks are recorded. `"expect": "any"` records exists without asserting it. |
| `capturePrepare` | `{kind:"capturePrepare"}`. One discarded PrintWindow warmup (`warmupMs`). Not evidence. See [Capture](#capture). |
| `capture` | `{kind:"capture", burst?:{count,intervalMs}, delayMs?, waitMs?, maxCensusDelayMs?, timeoutMs?, requireFrames?}`. Focus-free `observe.ps1 shot` (`-Window client -Method PrintWindow`) as fire-and-forget children. See [Capture](#capture). |
| `poolReuse` | `{kind:"poolReuse", dash, waitMs, maxCensusDelayMs?, retries, retryWaitMs, baselineRecord, tintedRecord, overrideColor, snippet, capture?}`. Retries `.frostdash dash` + 1500 ms load-only census (default 3, each retry after rings rest) until a nonempty subset **R** of tinted **T** is active again with no Sample override. **R** ids must be present rows in that census. **T** is active + Sample `_BaseColor` at the tinted census, including fresh allocations. Empty **R** is `reuse not observed`. Optional `capture: {delayMs, burst:{count,intervalMs}}` **starts** PrintWindow children during that wait (pool-reuse-identity: +100/+500/+900 ms starts, census at +1500, then await shots). `censusDelayMs` above `maxCensusDelayMs` (default 2000) fails `census late: <ms> ms`. A shot still running at the collect bound is timed out; the step fails only when `requireFrames` is true (default for the reuse burst). Reports `qualifiedTinted`, `reusedUntinted`, the ids, `retriesUsed`, `censusDelayMs`, `capture.available` (true with `paths` only when frames were saved), and `visual: {frames, verified:false, verifiedBy, verifiedAt}`. A saved frame is inspectable evidence only after a person or the reviewing agent has viewed it; the rig does not analyze pixels. Readback success is separate from visual success. |
| `op` | `{kind:"op", side, op, args, record, expect, timeoutMs, pollMs, tolerateBridgeDownMs}`. One bridge call, or when `pollMs` is set, repeated calls until `expect` holds or `timeoutMs` elapses (default 30 s). The result is traced like every other call. `expect` is a `{path: value, ...}` map on the result (dotted paths) **or** a regex matched against `JSON.stringify(result)`. Path values may be `@record:name.path` or `{neq: value}` (generation moved). Fails when expect does not hold. Args may use `@record:name.path`. A `record` stamps `since` / `logSeq` / `seenAt` from a socket `logs` snapshot taken **before** the call. `mods_ui` `open` fills `args.site` from the case `site` when omitted. `tolerateBridgeDownMs` keeps retrying a socket refusal (`ECONNREFUSED` / `ECONNRESET` / `EPIPE` before any response) for that bound; a refusal never marks the side main-thread suspect. |
| `coreSwap` | `{kind:"coreSwap", side, expectGenerationDelta:1, maxWaitMs:90000, allowPreviousAlive?}`. Reads `reload_status` (generation before), sends `core_reload` (expect `scheduled`), then polls `reload_status` every 3 s until `inFlight` is false and generation moved by `expectGenerationDelta`. Socket refusals are tolerated for `maxWaitMs` — a Core swap unloads Dev, so the bridge is briefly refused — and never mark the side main-thread suspect (each poll is one bounded socket attempt; a refused connect never entered the gate). Records `generationBefore` / `generationAfter`, `alcName`, `lastReload.outcome`, `previousAlive`, `collected` from `reload_status` / `lastReload` when present, the refusal window (`first` / `last` refused-attempt timestamps), and loaded image hashes from a ping afterwards. Fails on `lastReload.outcome` not ok, generation not moved, timeout, or `previousAlive` true (`reason` `previous generation still alive`). `allowPreviousAlive: true` records that as a finding instead of failing. |
| `recover` | `{kind:"recover", side, timeoutMs}`. The only way to try **one** bounded `ping` after a main-thread timeout. Success clears `mainThreadSuspect`. Never retries a timed-out mutation. |
| `soak` | `{kind:"soak", side, minutes, pingIntervalSeconds (default 30), logsIntervalSeconds (default 15, socket-thread), bridgeStatusIntervalSeconds (default 10, socket-thread), capture: null \| {intervalSeconds}, perf: null \| {windowSeconds (max 300), gapSeconds (default 2)}, dumpAfterSeconds (default 15), failAfterSeconds (default 60), stopOnFirstFailure:true, maxAfterFailureMinutes?}`. Records each sample (`bridge_status` entered/abandoned/frame/lastDrainAt/lastFreezeDump, ping ok/elapsedMs plus `startedAt`/`endedAt`, logSeq delta, newest Progenitor/Sample lines, capture path if any, and when `perf` is set a socket `perf_status` snapshot: frameCount, lastIntervalMs, sampleSilenceMs, running, captureId, request duration). Captures start only between samples and never while a main-thread op is in flight; the result lists `captureWindows[]` (`startedAt`/`endedAt`, nearest ping window). `perf` starts a bounded main-thread capture (`perf start`, `seconds=windowSeconds`) only when no main-thread op is in flight, one op at a time. A window is at most 300 s: a 15-minute soak records several `perfWindows[]` (kind `window` / `gap`) with explicit start/end, frames, maxIntervalMs, and a missing-interval note when a window closed during a freeze — never one window covering the soak. When `running=false`, the soak fetches the report through `perf` status/stop if the main thread is responsive, then starts the next window after `gapSeconds`. `perfLatency` is max and p50/p95/p99 of ping RTT and of `perf_status` duration. Each freeze episode names the perf window active at that time, or `"no window active"`. Any ping or frozen-frame episode above 5 s sets `responsivenessFailed: true` with `maxStallMs`, even if the stall recovered. Two consecutive `bridge_status` samples (10 s apart) with the same `frame` while the socket still answers mark `mainThreadFrozenSince` and stop pings (they would occupy the gate); `bridge_status`/`logs`/`perf_status` continue. If the freeze exceeds `dumpAfterSeconds` and `lastFreezeDump` is not already newer than the freeze start, the soak sends one socket `dump {"reason":"rig freeze"}` and records the response. Recovery records `mainThreadFrozenMs` and the recovery sample. `freezes[]` lists every episode. A freeze longer than `failAfterSeconds` fails the step with evidence preserved. First ping timeout stops further pings; `bridge_status` and `logs` keep polling for the remainder or until `maxAfterFailureMinutes`. Fails with samples, failures, first failure time, and `logSeq` `frozen` or `advancing` after the failure. Capture children are bounded as the capture step; soak liveness does not require frames (`requireFrames` defaults false on soak captures). |

## Main-thread discipline

The Dev bridge allows one main-thread op at a time per process. A timed-out waiter **abandons** the work but **does not free the slot** until that lambda completes. The rig therefore serializes main-thread calls per side and does not grow a retry queue after a timeout.

`lib/bridge.mjs` `BRIDGE_OPS` is the one table. Socket-thread: `logs`, `ops`, `bridge_status`, `perf_status`, `dump`. Main-thread (and any unlisted name, fail closed): `ping`, `reload`, `core_reload`, `reload_status`, `users`, `worlds`, `command`, `chat_observe`, `broadcast`, `ghost`, `query`, `get`, `set`, `components`, `spawn`, `entity`, `prefab`, `buff`, `heal`, `teleport`, `give`, `inspect`, `snapshot`, `restore`, `players`, `status`, `save`, `world_save`, `store`, `connect`, `host`, `host_capture`, `capture`, `perf`, `disconnect`, `quit`, `castles`, `mods_ui`, `exec`. `bridge.mainThreadInFlight(side)` is the current gated op (`op`/`id`/`since`) or null.

When a main-thread op times out, that side is `mainThreadSuspect`. The rig sends **no** further main-thread ops to it. Later main-thread steps fail fast with `main thread suspect since <ISO> after <op> id <n>`. Socket-thread `logs` / `ops` / `bridge_status` continue. A `recover` step is the only way to try one bounded ping again. Never retry a timed-out mutation automatically. A socket-level connection refusal or reset (`ECONNREFUSED` / `ECONNRESET` / `EPIPE` before any response) is `bridgeDown` (recorded with the time) and never marks the side suspect: the call never entered the gate. That call fails unless the caller opted into a bounded wait (`op.tolerateBridgeDownMs`, or a `coreSwap` poll). Exists confirmation stops on the first failed exists query (no retry queue). Run-level admin restore still issues one `set` (`bypassSuspect`) so a leftover `IsAdmin=true` is not left behind; UI resolution restore is a teardown **step** and fail-fasts.

Soak samples `bridge_status` at 10 s (socket, gate-free) and ping at most every 30 s, with logs at 15 s. Optional `perf_status` is also socket-thread and is polled every sample. Captures run between those samples, never while `mainThreadInFlight` is set. `perf start` / report `status` / `stop` are main-thread and wait for that gate. After a ping timeout, further pings stop; `bridge_status` and `logs` continue. A stuck `frame` across two 10 s `bridge_status` samples is a freeze: pings stop, socket `dump` may run once (`reason: "rig freeze"`) unless the in-process watchdog already wrote `lastFreezeDump`, and a freeze longer than `failAfterSeconds` fails the step. A recovered stall above 5 s still sets `responsivenessFailed`. Include a no-capture control before a capture-enabled soak.

## Leftovers from a previous session

A leftover the previous run did not record cannot be adopted from `cleanup <runDir>`. Find it on the **server** with health+prefab near the player, then `cleanup --entity` (or an `adopt` step). A PrefabGUID-only query walks every prefab and truncates, so it can miss a unit sitting next to the player.

```
query {"all":["Health","PrefabGUID"],"near":{...,"radius":40},"limit":200}
```

Then:

```
node tools/live-rig/rig.mjs cleanup --entity <index:version> [--prefab CHAR_Critter_Rat] [--out <dir>]
```

## Entity destroy protocol

Tracked destroy is two Dev actions plus an exists poll (internal diagnosis DIAG5). The Dev `entity` op is the contract; the tracker is the only caller. The unit is destroyed only at `tagged`. There is no `detach` / `waiting-detach` phase.

1. **destroy** — Destroy+DestroyTag the unit's copied buffs. **No** Attach strip. **No** Destroy/DestroyTag on the unit. Each entry is `{phase:"buffs", buffs:[ids], staleRows, buffSweepComplete, reason}`.
2. **finalize** — poll every 250 ms (bound 8 s, default) with `{entities:[unit ids], buffs:{"<unit id>":[accumulated ids]}}`. Repeated calls return:
   - `{phase:"waiting-buffs", pending, staleRows}` — some listed buff id still `Exists`
   - `{phase:"buffs", buffs:[new ids], cycle}` — listed ids are gone and new live buffs appeared; those ids are accumulated (cap 4, then `{phase:"stuck", reason}`)
   - `{phase:"tagged", destroyTagAdded:true}` — listed ids are gone; Destroy+DestroyTag the unit
3. **exists** — the existing bounded poll until the unit is absent.

If `buffSweepComplete` is false, finalize is **not** polled. The entity is `unconfirmed` with the op's `reason` and the run is never `complete`. `{phase:"stuck"}` or a finalize that never reaches `tagged` within the bound is `unconfirmed` with that reason — also never `complete`. `exists` is unchanged.

A destroy response with `phase:"detach"` (an older Dev image) or with no `phase` is a contract mismatch: the finding is `entity op contract mismatch: <observed>` (`detach` or `no phase`). The entity is `unconfirmed`. The tracker does **not** finalize, tag, or fall back.

`--attach-readback` inspects the unit with `{only:["AttachedBuffer"], depth:16}` once **before** destroy and once at `tagged` (the DIAG5 readbacks), and stores both results on `cleanup.entities[i].attachedBuffer {before, atTagged}` (on the report, never trace-only). Default **on** for thief/DIAG cases (case name/summary, a `CHAR_Bandit_Thief` spawn, or a tracked thief prefab). `--no-attach-readback` turns it off.

`cleanup <runDir>`, `cleanup --entity`, `adopt`, and `--force-teardown` use this same path.

Per-entity fields on `report.cleanup.entities` (and on `unconfirmed` when that is the outcome):

| Field | Meaning |
|---|---|
| `phase` | Latest phase (`buffs`, `waiting-buffs`, `tagged`, `stuck`) |
| `buffsAt` / `taggedAt` | Timestamps when that phase was first observed (`null` if never reached) |
| `buffs` | Accumulated buff ids from every `buffs` response |
| `staleRows` | Stale BuffBuffer rows from the latest response |
| `cycles` | New-buff finalize cycles (`0` when none) |
| `finalizePolls` | Finalize calls issued (`0` when finalize was skipped) |
| `absenceLatencyMs` | Exists-poll wait until the unit was gone (`null` when finalize never tagged) |
| `attachedBuffer.before` / `atTagged` | Inspect payloads when `--attach-readback` ran |

## Log signals

After steps and cleanup, the run scans server logs since the opening ping's `logSeq` (`max` 400, `filter` `""`) and pings the server. Counts are stored on the report as `expectedDetachLogs` and `logSignals`. Citation: internal diagnosis DIAG5.

| Kind | Line | Effect |
|---|---|---|
| Expected | `destroyed without being detached` | Counted on `expectedDetachLogs.destroyedWithoutBeingDetached`. Does **not** fail the run. Fires for every destroyed unit with attached buffs. |
| Expected | `DetachEntity information` | Counted on `expectedDetachLogs.detachEntityInformation`. Does **not** fail the run. |
| Fatal | `Tried to detach` **and** `AttachedBuffer` | Missing parent-buffer row. `logSignals.fatal.triedToDetachAttachedBuffer`. Run is `incomplete`; `reason` is that line. |
| Fatal | `points at buff.Target` | DIAG2 Job 4 abort. `logSignals.fatal.pointsAtBuffTarget`. Run is `incomplete`; `reason` is that line. |
| Fatal | `This Exception was thrown from a job compiled with Burst` | Burst abort. `logSignals.fatal.burstException`. Run is `incomplete`; `reason` is that line. |
| Fatal | server bridge not answering after the run | `logSignals.fatal.bridgeUnanswered`. Run is `incomplete`; `reason` is `server bridge not answering`. |

A non-zero fatal count makes the run `incomplete` even when every tracked entity is confirmed absent. Expected detach lines on a successful destroy are the pass, not a defect.

## Cleanup remaining vs unconfirmed

`cleanup.remaining` is only entities whose fresh exists readback is `exists: true`. An entity already confirmed absent is never listed as remaining. On abort, remaining is computed from a new exists poll, not from the tracker list.

Entities whose exists query itself failed (bridge error or timeout) are listed separately as `cleanup.unconfirmed` with the error. They are never silently remaining and never silently gone. Unconfirmed entries keep the run from being `complete`. A destroy that cannot tag the unit (`buffSweepComplete` false, stuck, finalize bound, or a contract mismatch) is also `unconfirmed`, not remaining. `cleanup <runDir>` adopts both buckets (and `teardown.*` when present). Treating unconfirmed as an empty remaining list is a defect: that is the leftover the command exists to pick up.

## Final checks

`cleanupAll` destroy requests always run **before** the final-check readbacks. The report records that order on `finalChecksOrder` (`cleanupAll`, then `playerHealth`, `protection`, `adminRestore`, `adminState`, `remainingTestEntities`, `carriers`). Protection is not re-read before destroy.

Every run, including aborted ones, records:

- `playerHealth` — `Health.IsDead` is false on the test character (server)
- `protection` — `.debug status` is parsed from the Core `Debug: status … flags=<n>` log line (when the Sample is loaded its chat logger may also echo the typed text `.debug status`; only the `Debug: status` line with a numeric `flags=` counts). Pass requires `flags=7` exactly, `guard=True`, matching prefixes, `drain`/`aggro`/`persists` True. Live frost dash has shown `flags=68719477263` on the same buff; SampleFrostDash does not write `BuffableFlagState`, so extra bits stay a failed parse. A non-admin refusal is `unavailable (non-admin refusal)` with `value` null (never `guard=False`) and does not by itself fail the run.
- `adminState` — the server user's admin flag equals the value at run start (after any grant has been restored)
- `remainingTestEntities` — every tracked entity reports `exists=false`; unconfirmed exists queries fail this check
- `carriers` — `SpawnSequenceForEntity` within 60 m of the player, both sides, matches the start **ids** (not counts). The query passes `total: true` and `limit: 200`. The check (and the start-state capture) fail when `truncated === true`, `total > entities.length`, or `entities.length === limit` — a 200-row set is not the full world. After tracked cleanup, client-side delta `NetworkedSequence` carriers are destroyed through the client entity op and confirmed absent; the final check then compares the live id set to the baseline. Server-side deltas are not destroyed: the check waits up to 10 s (poll 500 ms) for them to expire and fails if any remain. A capped query at run start aborts with a reason that names the cap.

### Carrier leftovers

`.catalogex warm` (and Charm recipes) can leave a client `NetworkedSequence` replica after the server has already disposed the beam at its deadline. That is known game behavior, not a tracked spawn.

At run start the rig records carrier ids per side. At cleanup it computes the delta (ids present now that were not in the baseline):

| Side / prefab | Action |
|---|---|
| Client `NetworkedSequence` delta | Destroy through the client entity op (carriers have no BuffBuffer: destroy returns `buffs:[]` and finalize tags immediately), confirm absence with the bounded exists poll. Record `cleanup.clientCarriers {destroyed:[ids], remaining:[ids]}` and finding `client carrier outlived server disposal: <ids>` even when the run is `complete`. |
| Server delta | Not destroyed (the server owns them and disposes at deadline). Wait the settle time; if they remain, the run is `incomplete`. |
| Baseline id | Never destroyed. |
| Client delta whose prefab name is not `NetworkedSequence` | Reported on `cleanup.clientCarriers.remaining` and on the carriers leftover list; not destroyed. `cleanup <runDir>` adopts it on the client side and still does not destroy it; that recovery is never `complete` / `nothing to clean` while the entity exists. |

The carriers check is ok only when the current id set equals the baseline id set on both sides after that cleanup, and the query was not capped.

`leftoverSummary` is `{trackedRemaining, unconfirmed, carriersLeftover, adminRestored, protectionOk}` so a reader can see at a glance what was left. `protectionOk` is false when the field is unavailable. `report.restore` lists each `ackRequired` restoration and any `unconfirmed` items. `report.restorationUnconfirmed` is the same unconfirmed name list (empty when every ack was observed). `report.cancelled` is `{requested: true, children, killed, waitedMs}` after a stop.

Tracked entities are never the player's character or user. Hostiles are destroyed before any Sample swap or removal; a leftover hostile refuses the reload.

## Offline tests

No game, no sockets. From the repository root:

```
node --test "tools/live-rig/test/*.test.mjs"
```

The CLI `run` path talks to the Dev bridge and cannot be injected with a fake world, so leftover-entity exit mapping is covered by `exitCodeForStatus` plus `runCase` (see `test/cli.test.mjs`). Source-checked against `tools/mcp/call.mjs` (ports, token files, live-server refusal, JSON-line protocol, 30 s timeout). Live cases are an acceptance procedure — not run in this tree.
