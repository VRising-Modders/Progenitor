# Test rig

Local test infrastructure beyond one client and one test server: the local Thunderstore stand-in, extra and headless clients, the install-window acceptance procedure, and unattended server verification. Part of the [development loop](dev-loop.md).

## Local Thunderstore (dev only)

LOCALTS1 (user instruction 2026-10-09): a stand-in for Thunderstore on this PC that new versions can be published to, so update tests do not depend on the real site. `tools/local-thunderstore/lts.mjs` (Node, no packages) serves on `127.0.0.1` only:

| Path | What |
|---|---|
| `/c/v-rising/api/v1/package/` | The v1 index, `Last-Modified` from the last publish; `If-Modified-Since` answers 304, `Accept-Encoding: gzip` gets a gzip body (the TSTRAFFIC1 conditional path) |
| `/package/download/<owner>/<name>/<version>/` | The package zip |
| `/icons/<owner>-<name>-<version>.png` | The icon from the zip |
| `/api/experimental/package/<owner>/<name>/<version>/readme/` and `/changelog/` | `{"markdown": …}` from the zip's `README.md` / `CHANGELOG.md` |
| `/status` | What is published and the index's `Last-Modified` |

1. Build a package with `tools/make-dummy-package.ps1 -Out <build>` (it writes `<build>/Progenitor-<Package>-<version>.zip`).
2. Start the server: `node tools/local-thunderstore/lts.mjs serve --data <data> --port 27450`. State lives in `<data>/state.json` and `<data>/packages/`.
3. Publish: `node tools/local-thunderstore/lts.mjs publish --data <data> --zip <build>/Progenitor-HotDemo-0.1.0.zip`. `--as Progenitor-UpdateProbe-1.1.0` re-packs the zip under another package name and version (`manifest.json` `name` / `version_number`; the plugin image and `progenitor.json` are unchanged, because admission requires `progenitor.json` to match the image's `[BepInPlugin]` version), and `--changelog "<text>"` writes its `CHANGELOG.md`, so one build can be published as several versions. Each publish moves `Last-Modified`, so the next conditional check gets a 200 with the new version. It works while the server runs. `unpublish --package <Owner-Name> [--version <v>]`, `list` and `touch` (move `Last-Modified` only) complete it.
4. Point the game at it, in `BepInEx/config/progenitor.cfg` (client and test server):

   ```
   [Install]
   DevThunderstoreUrl = http://127.0.0.1:27450
   ```

   On the test server also set `[RemoteInstall] ThunderstoreBaseUrl = http://127.0.0.1:27450` (its remote installs and install window read that base). The client follows a changed `DevThunderstoreUrl` within two seconds at the title screen (the catalog is rebuilt and one check is made; AUTOUPDATE2); the server's key takes effect at its next catalog build or Core load.

**Safety.** `DevThunderstoreUrl` is cfg only and empty by default. It is used only when it is a loopback address (`127.0.0.1`, `localhost`, `[::1]`) and the tree is a developer tree (`[Reload] WatchCore = true`, written by `tools/deploy.ps1`) or `[Install] AllowDevThunderstore = true`; otherwise it is ignored and the log says why (`Install: dev Thunderstore off: …`). Plain http is accepted only for that loopback address (`ThunderstoreMirror.AllowsHttp`); every other download keeps the https-only rule. A loopback http `ThunderstoreBaseUrl` follows the same dev rule. The index cache keeps one copy per index URL, so the real index's cache is untouched. Restore both keys (`DevThunderstoreUrl` empty, `ThunderstoreBaseUrl = https://thunderstore.io`) when the test is done.

### Recipe: auto-update phase 2

**Run in part on 2026-10-09** (Steam client); the steps after a real restart are an acceptance procedure, not run. Build Core and Dev from the branch and hot-swap them on the client (no Bootstrap change). Use the stand-in above and the test package `samples/Progenitor.UpdateProbe` (its README builds one version per call). Read once per step.

1. Publish `AutoUpdateProbe` 1.0.0, install it from the Install tab (`mods_ui select_tab install`, `widget_click Progenitor-AutoUpdateProbe/client-a`, `dialog_choose yes`). Close the Mods window.
2. Publish 1.1.0 (hot) and `auto_update {"action":"check"}`. Within seconds: `op=apply origin=auto … route=store result=loaded`, `Update probe 1.1.0: loaded`, `status.auto.notice.line` `1 mod updated`, the line under MODS `1 MOD UPDATED` (`badgeDrawn.text`), and the Install tab's `install-update-result` line names the mod and version.
3. Mod page (`mods_ui select plugin progenitor.updateprobe`): **Roll back to 1.0.0** swaps it back (`op=rollback by=menu`) and **Updates** reads **Keep**. Set **Updates** to **Global** (`widget_click Updates value Global`); `auto_update forget_attempts`; 1.1.0 comes back.
4. Connect to the test server; while in the world publish a version, `check`: `status.auto.window` is `in a world` and nothing is applied. `disconnect`: the pass runs at the title screen.
5. Publish a build whose `OnLoad` throws (`-p:UpdateProbeFailLoad=true`): `OnLoad threw`, `op=rollback by=auto`, `op=failed`, `UPDATE FAILED` under MODS; the next pass skips it (`failedBefore`, `auto false`); the mod page lists it with **Clear failed versions**.
6. Publish a version packaged with `-NotHotLoadable`: `route=store result=restart`, the old copy marked, the new one in `progenitor-pending`, the restart bar counts it. Publish another: `queued … in place of the queued …` and one pending ledger row.
7. Not run: restart V Rising (the relaunch takes focus; ask first). After the start the queued version is loaded and `status.auto.verify` is empty; with a build that does not load, it is rolled back and listed as failed.
8. Clean up as in [the sample's README](../samples/Progenitor.UpdateProbe/README.md#clean-up), empty `DevThunderstoreUrl`, delete `progenitor-auto-update.json` and `progenitor-server-requirements.json` when the test created them, stop the stand-in.

### Recipe: auto-update phase 5 (Progenitor itself)

AUTOUPDATE5 ([auto-update.md](auto-update.md#phase-5-as-built)). Run in part on 2026-10-10 (steps 3 to 6). The test machines are developer trees, which never update themselves; the live test sets the cfg-only override and removes it afterwards. Deploy Bootstrap, Core and Dev from the branch and restart each process once, so the running Bootstrap reports its image (`ping.images` has a `Bootstrap` row). Read once per step and check `ping.images` (Core and Bootstrap sha256) after each.

1. In `progenitor.cfg` (client, and the test server for its steps): `[AutoUpdate] SelfUpdateDevOverride = true`; on the server also `[AutoUpdate] Progenitor = Automatic`. Point `[Install] DevThunderstoreUrl` at the stand-in ([above](#local-thunderstore-dev-only)).
2. Build "releases" outside the repository: a package zip per version (`manifest.json`, `BepInEx/plugins/Progenitor.Bootstrap.dll`, `BepInEx/progenitor-core/Progenitor.dll`, `BepInEx/progenitor-core/addresslib/*.json`) with a version newer than the running one, built with `-p:ProgenitorVersion=<version>`. A **Core-only** release ships the running Bootstrap's exact bytes. Publish each with `lts.mjs publish --zip Progenitor-Progenitor-<version>.zip`.
3. Core only: `auto_update self_check {"fetch":true}`; at the title screen `op=route … route=Hot`, `op=apply … swap scheduled`, a Core swap with reason `self-update <version>`, `op=confirm … result=loaded`, and after 30 s `op=healthy`. `self_status.journal.state` is `ok`, `record.version` is the release, `kept` holds it and the previous copy.
4. A Bootstrap release: `route=Restart`, `staged`, `Progenitor.Bootstrap.dll.next` beside Bootstrap, `pendingRestart` set. Restart the client (`tools/launch-client.ps1 -Restart`, a normal quit): Bootstrap logs `… starts now (… start 1 of its trial)`, `ping.images` shows the release's Bootstrap and Core, then `op=healthy`.
5. A broken release (a scratch build whose Core fails to install), on the test server: it is staged; each restart (`tools/test-server.ps1 restart`) logs `is on trial: start N`; the third start logs `was rolled back` and runs the previous Core, and the version is in `self_status.failed` and never offered again.
6. Clean up: redeploy Bootstrap, Core and Dev from `origin/main` and restart each process; remove `SelfUpdateDevOverride` and the server's `[AutoUpdate] Progenitor`; restore `DevThunderstoreUrl`; delete `progenitor-self-install.json` and `progenitor-versions/progenitor/` from both trees; unpublish the test releases.

## Extra clients and headless clients (experimental, early development)

**Experimental.** Extra and headless clients are development-only test tooling. The scripted players that drive them through the `bot*` bridge ops are a separate experiment, not a supported feature; see [tools/bots/README.md](../tools/bots/README.md).

Scripted extra clients on the same PC as the server, for multi-player checks without a second Steam account. Steam `steam://run` will not start a second process; extra instances launch `VRising.exe` directly.

**Authentication.** An extra instance (`PROGENITOR_INSTANCE` set) hails platform id `90000000000020000 + N` with a dummy ticket over Lidgren. On the server, `AllowExtraInstances=true` (Dev config, `[ExtraInstances]`) approves that band without Steam, **only from a loopback Lidgren connection**; anything else goes to Steam, which rejects the dummy ticket. Both halves compile only in dev builds (`PROGENITOR_EXTRA_INSTANCE`, see "Build-time gate" in [bridge.md](bridge.md#the-opt-in-marker--the-bridge-refuses-to-start-without-it)) and the server half still requires a `_testserver` or `progenitor-hosted` instance.

**Dedicated server, players over Steam.** Leave `LanMode` off: it replaces the Steam socket, so Steam players can no longer join. With the switch accepted, the Dev plugin adds a **loopback-only Lidgren listener next to the Steam transport** (`LoopbackTransport`): once `ServerBootstrapSystem._NetServer` exists and before anyone connects, it wraps the Steam transport in the game's own `CompositeServerTransportLayer` with a `LidgrenServer` bound to `127.0.0.1`. Players keep joining by IP on the game port over Steam; extra clients connect to `127.0.0.1:<game port + 2>` (`[ExtraInstances] LoopbackPort`, 0 = game port + 2). Look for `loopback transport: Steam transport kept; Lidgren listening on 127.0.0.1:9988`. It refuses if a connection is already open (restart the server) and does nothing on a LanMode or hosted double-transport server. `UseDoubleTransportLayer` alone does not do this on a dedicated server: the game only honors it in a hosted child.

**Headless.** `-Headless` runs `-batchmode` (no window, GPU device kept) from a separate install root, `<workspace>\_headless\client`, so the player's Steam install, plugins and logs are never touched. Create it once, deploy to it like a client:

```
powershell -File tools/headless-root.ps1
powershell -File tools/deploy.ps1 -Side client -GameDir <workspace>\_headless\client
```

The launcher sets `PROGENITOR_HEADLESS=1`; Bootstrap's `HeadlessClient` then re-registers the `Application.isBatchMode` internal call so game code sees `false`. Without it the game treats a batch-mode process as a server: `UIEntryPoint.Awake` skips the menu with `LoadScene(1)` and `GameBootstrap.Start` fails. `-NoGraphics` (`-nographics`) never reaches the menu (UI shaders unsupported) and is kept only for experiments. Each headless client uses about 5.5 GB of RAM.

**First character.** A new platform id has no character. After the join settles, run `create_character` on that extra client (`{"name":"HeadlessOne"}`, optional appearance bytes); it sends the creation screen's own `CreateCharacterEvent` and is idempotent (`already:true` once the client has a `LocalCharacter`), so retry it if the server has not spawned the character yet: a send during loading is dropped. The server-side form (`platformId` + `name` on the server bridge) also spawns a character but leaves that client on its creation screen until it reconnects. New characters start asleep in the crypt and stay hidden from other players until their client presses the wake-up key (Space), which a headless client cannot do. Run `wake` on the server (`{"user":"HeadlessOne"}` or `{"platformId":"…"}`): before `SpawnCharacterSystem` updates it sets `SyncedButtonInputAction.Respawn` (1024) in the character's `EntityInput.State`, the bit that system waits for, until the user's `SpawnCharacter` request is gone (usually one frame; log `wake: 'HeadlessOne' spawn request finished`). Removing the `AB_Interact_CoffinSleeping` buff instead does not work: the system re-applies it.

`tools/extra-client.ps1 -Index N` (N = 1..9) starts `VRising.exe` with `ProcessStartInfo` (`UseShellExecute = false`) and a per-process environment. It refuses `-Index 0` (that is `launch-client.ps1`), accepts only the Steam install or a root under `<workspace>\_headless`, and never touches `C:\VRisingServer` or the staged test server. `-Stop` kills only the PID recorded for that index, after confirming it is `VRising.exe` under an allowed root, never by name.

| Resource | Value |
|---|---|
| Bridge port | `27341 + 10*N` (index 1 → 27351) via `PROGENITOR_DEV_PORT` |
| Token file | `%TEMP%\progenitor-client-N\progenitor-dev.token` via `PROGENITOR_DEV_TOKEN_FILE` |
| Instance tag | `client-N` via `PROGENITOR_INSTANCE` (`ping` / `status` return it as `instance`) |
| Unity log / saves | `-logFile` and `-persistentDataPath` under `%TEMP%\progenitor-client-N` |
| Always | `-noVideo`. `-Headless` adds `-batchmode` and `PROGENITOR_HEADLESS=1` |

The script polls the instance with `node tools/mcp/call.mjs client ping` (setting `PROGENITOR_CLIENT_PORT` / `PROGENITOR_CLIENT_TOKEN_FILE` in its own process). Optional `-Connect host:port` sends the `connect` op (JSON via `@file`); `-Password` is the join password.

Three headless players on a dedicated server whose loopback listener is on 9988 (verified live 2026-09-24, isolated test server `_testserver\headless` on 9986, the Steam client joined over SteamIPv4 alongside):

```
powershell -File tools/extra-client.ps1 -Index 1 -Headless -Connect 127.0.0.1:9988
powershell -File tools/extra-client.ps1 -Index 2 -Headless -Connect 127.0.0.1:9988
powershell -File tools/extra-client.ps1 -Index 3 -Headless -Connect 127.0.0.1:9988
# per index, once the join settles:
$env:PROGENITOR_CLIENT_PORT = "27351"
$env:PROGENITOR_CLIENT_TOKEN_FILE = "$env:TEMP\progenitor-client-1\progenitor-dev.token"
node tools/mcp/call.mjs client create_character '@name.json'
# then, on the server bridge:  node tools/mcp/call.mjs server wake '@wake.json'   ({"user":"HeadlessOne"})
powershell -File tools/extra-client.ps1 -Index 1 -Stop
```

The default MCP `client` side stays on 27341 (primary Steam client); one MCP process targets one client port. Never point an extra instance at the live server.

## Install window from Thunderstore, TOFU and legacy cleanup

**Acceptance procedure — run live 2026-10-05, except as noted.** This recipe checks live the 2026-10-05 removal of server-sent mod files ([mod-manifest.md](mod-manifest.md#removed-2026-10-05-server-to-client-mod-files)): the retargeted install window with a Thunderstore fix, trust on first use, an old trust file, and the one-time legacy cleanup. (It replaces "Checking the consent flow", removed with the consent panel.) The values in the tables are expectations; the results below were observed.

**Live results, 2026-10-05.** This branch's Core 2026.10.75.x on the Steam client and the isolated test server, with HotDemo 2026.10.57 required and served from a local index:

- **Steps 1–3 (install window).** Observed:
  - the server logged `deferred for 60 s to fix progenitor.hotdemo on the client (thunderstore)` and sent the window descriptor;
  - `join_ui` reported `mode install-window`, and the row showed `install` / `available` / `applies now`;
  - after `row` and `ts_install` came `installing from Thunderstore … (confirmed)`, `Install: planned … ApplyNow`, `Hot demo: loaded`, `Thunderstore batch done: join-now; … the join continues by itself`, and then `re-answered the handshake … sent=true`;
  - the server logged `re-answered inside the install window` and `ok (10 mod(s) match)`;
  - the client reached `Client_0` on the same connection, with no drop or rejoin.

  A client built before `ClientFix` (the immediate kick) was not tested.
- **Step 5 (lapse).** With nothing pressed, after 60 s the server logged `failed: Mod mismatch: progenitor.hotdemo was not ready within 60 s. Install from the join screens, then join again.`
- **A refused join.** When the server had no index entry, the join was refused at once (`not deferred: … cannot be fixed during the join`). The join screens still installed the package from Thunderstore, and **Join now** left the refused session and reconnected.
- **TOFU.** First contact with an address logged `pinned operator fp=… on first use`. With the pinned `fp` edited, the next join logged `operator key changed for host …: was 1111222233334444 now 00b9749a0441bdc5 (not accepted)`, and the row stayed unchanged. The key-changed line on the Thunderstore confirmation (added after this run) is not live-verified.
- **Step 7 (old trust file).** A real trust file whose row carried `riskAck` loaded with no parse warning. The field survived the pin's rewrite.
- **Steps 8–9 (legacy cleanup).** Run with a client start instead of `core_reload`, with the crafted files in `progenitor-reload/`, and on the build before the ledger check was added. Observed:
  - `legacy server mods: sent.mod 1.0 (Sent.dll …): deleted …`;
  - `mine.mod 1.0 (Mine.dll …): left … (other bytes are there now …)`;
  - `removed 1 file(s) …, 2 record row(s) dropped`;
  - the record file was gone afterwards.

  The ledger check was not run live.
- **Setup trap.** The server's `LocalIndexPath` must be edited while the server is stopped. A running server saved its in-memory config over the hand edit.

Needs Core and Dev from this branch on **both** sides (they hot-swap; **no Bootstrap change**), the isolated test server, and the client at the title screen and not in use. Read once per step; do not poll while a person is playing. Commands are `node tools/mcp/call.mjs <side> <op> "@file"` with the JSON in a file.

**Setup.**

1. *A hot-loadable required mod on the server.* Build HotDemo as a required mod at a three-part version (a package's version must equal the image's) and deploy it to the **server only**:
   ```powershell
   dotnet build samples/Progenitor.HotDemo/Progenitor.HotDemo.csproj -c Debug --no-incremental -p:HotDemoRequired=true -p:ProgenitorVersion=2026.10.57 -p:BuildProjectReferences=false
   powershell -NoProfile -File tools/deploy.ps1 -Projects Progenitor.HotDemo -Side server -NoBuild
   ```
   Server log `Reload: loaded Progenitor Hot Demo 2026.10.57`. The client must have no HotDemo: no `Progenitor.HotDemo.dll` / `progenitor.hotdemo.dll` in its `progenitor-reload`, and `client join_ui {"action":"versions"}` lists no kept HotDemo (a kept copy would be a local fix instead).
2. *The local index* (a new folder), from the server's image:
   ```powershell
   .\tools\make-dummy-package.ps1 -Out <testserver>\dummy-window -Sample HotDemo -Dll '<the server HotDemo DLL from step 1>' -Version 2026.10.57
   ```
   `index.json` lists `Progenitor-HotDemo` 2026.10.57 with `hotLoadable: true`.
3. *Both sides read it.* `[Install] LocalIndexPath = <testserver>\dummy-window` in the client's `BepInEx\config\progenitor.cfg` (at the title screen) **and** in the test server's `<testserver>\server\BepInEx\config\progenitor.cfg`. The server needs it: it opens the window only for a package it has read itself. **Edit the server's file while the server is stopped**: a running server saves its in-memory config over hand edits (live 2026-10-05). Note both old values.
4. *The server names the package:* the test server's `BepInEx\config\progenitor-manifest.json` (keep a copy): `{ "mods": [ { "id": "progenitor.hotdemo", "package": "Progenitor-HotDemo" } ] }`. `[Networking] InstallGraceSeconds` 60 (the default). Restart the test server. Expect server `ModManifest: read Thunderstore versions of 1 package(s) for the install window` with no `not listed` and no `index:` error.
5. *First contact for TOFU.* Copy the client's `BepInEx\config\progenitor-trusted-operators.json` aside, then remove the row whose `host` is `127.0.0.1:9976` (or delete the file).

**Observed live (2026-10-05), the refused-join half.** With this setup and the window off (a hard refusal), a refused join followed by a Thunderstore install from the join screens worked: client `Install: planned Progenitor-HotDemo for client: ApplyNow …`, `Hot demo: loaded`, `JoinFlow: Thunderstore batch done: join-now; Loaded 1 mod. …`, then Join now reconnected through `LaunchGame` and the client ended in `Client_0`. The install-window half below (the deferral, the re-answer, `ClientFix`) is still not run.

The client must run Core from this branch: the server opens the window only for a client that declares both `InstallWindow` and `ClientFix` in its capability row; an older client gets the immediate kick (`not deferred: the client did not declare the install-window and client-fix capabilities …`).

**Part 1 — trust on first use and the install window.**

| # | Command | Expect |
|---|---|---|
| 1 | `client connect {"address":"127.0.0.1:9976","protocol":"Lidgren"}` | server `Handshake: <name> deferred for 60 s to fix progenitor.hotdemo on the client (thunderstore) (…)` and `ModManifest: install window descriptor to user N: 1 mod(s), operator <fp>`; client `ModManifest: install window opened: Mods needed: <name>. Install from the join screens within 60 s, or this join is refused.`, `ModManifest: pinned operator fp=<fp> host=127.0.0.1:9976 on first use (…)`, `JoinFlow: screens opened (install-window) …`. The steps below must finish inside the 60 s |
| 2 | `client join_ui {"action":"read"}` (`press continue` if on the message page) | `mode install-window`, `page mods`; the HotDemo row `status missing`, `action install`, `package Progenitor-HotDemo`, `thunderstore available`, tags `applies now` |
| 3 | `join_ui {"action":"row","id":"progenitor.hotdemo"}`, then `join_ui {"action":"ts_install"}`; `read` until `batch.done true` | `result ts-trust`, then `result install`; client log `JoinFlow: installing from Thunderstore: Progenitor-HotDemo@2026.10.57 (confirmed)`, `Install: planned Progenitor-HotDemo for client: ApplyNow …`, `Hot demo: loaded`, `JoinFlow: Thunderstore batch done: join-now; …`, then `JoinFlow: re-answered the handshake after the Thunderstore install of … inside the install window; sent=true`; server `Handshake: <name> re-answered inside the install window`, `Handshake: <name> ok (…)`. If the admission takes the restart route instead (`after-restart`), the window cannot be met: note the reason and stop Part 1 here |
| 4 | the client's `progenitor-trusted-operators.json` | a row `host 127.0.0.1:9976`, the server's `fp` and `spki`, `always true`, `issued` the catalog's stamp; a `thunderstoreAcks` row with `Progenitor-HotDemo@2026.10.57` |
| 5 | Lapse: `client disconnect`; uninstall HotDemo from the client (Install tab, or remove the file and its `progenitor-install.json` row); `client connect …`; wait 60 s without pressing anything | the window opens again (step 1, no `pinned … on first use` this time: the client logs `(trusted)`); after 60 s server `Handshake: <name> failed: Mod mismatch: <name> was not confirmed within 60 s (this client did not answer again in time). If it is installed now, join again; if not, install it from the join screens first.` |
| 6 | (optional) Key change: at the title, move the test server's `progenitor-operator.key` and `.pub` aside, restart it (a new key is generated), connect | client `ModManifest: operator key changed for host 127.0.0.1:9976: was <old> now <new> (not accepted)`; the trust file's row unchanged; on the join screens' "Install from Thunderstore?" confirmation (a refused join's Install) the plain line `This server's key changed since <date>; it was <old fp>.` and `join_ui read` `tsTrust.keyChanged` the same text. Put the old key files back and restart the server |

**Part 2 — an old trust file still loads.**

| # | Command | Expect |
|---|---|---|
| 7 | at the title, add `"riskAck":"2026-09-04T10:00:00Z"` to the `127.0.0.1:9976` row of the client's `progenitor-trusted-operators.json`; connect (with HotDemo installed again, or accept the window) | no `progenitor-trusted-operators.json parse failed` line; the catalog logs `(trusted)`; after the join the row still carries `riskAck` (a rewrite keeps it) |

**Part 3 — the legacy cleanup.** At the title screen, on the client. The crafted files go under `progenitor-pending/<host>/`, which no reloader scans; a Core hot swap runs the cleanup without a process start (a start would first promote them).

| # | Command | Expect |
|---|---|---|
| 8 | Craft a record: copy any small file to `BepInEx\progenitor-pending\legacytest\legacy.test.dll` and another to `…\legacytest\other.test.dll`; take `(Get-FileHash -Algorithm SHA256 <legacy.test.dll>).Hash.ToLower()`; write `BepInEx\config\progenitor-server-mods.txt` (UTF-8, no BOM) with two tab-separated rows: `<that sha256>	legacy.test.dll	legacy.test	1.0.0	legacytest	127.0.0.1:9976	once	2026-09-04T18:00:00Z` and `<64 zeros>	other.test.dll	other.test	1.0.0	legacytest	127.0.0.1:9976	once	2026-09-04T18:00:00Z` | the files exist |
| 9 | Start a new client Core generation: `node tools/mcp/call.mjs client core_reload`, then `client reload_status` until `lastReload.outcome` is `ok` with a newer `generation` ([`core_reload`](bridge-ops.md#core_reload); a Core hot swap, never mid-join) | client log `ModManifest: legacy server mods: legacy.test 1.0.0 (legacy.test.dll, sha256 <prefix>, sent by 127.0.0.1:9976): deleted …\progenitor-pending\legacytest\legacy.test.dll`, `… other.test 1.0.0 (…): left …\other.test.dll (other bytes are there now …)`, then `ModManifest: legacy server mods: servers no longer send mod files (mods come only from Thunderstore); removed 1 file(s) …, 2 record row(s) dropped`; on disk `legacy.test.dll` gone, `other.test.dll` still there, `progenitor-server-mods.txt` gone. A second hot swap logs nothing |

Not covered here (game-free tested only): the allowlist row removal (only when `progenitor-allow.txt` exists; do not create one for a test), a file that cannot be deleted (kept for the next start), a Thunderstore install the install ledger tracks with the same bytes (left, with its allowlist row), a row without a sha256 (kept, with a warning), and `ModVersionService.ForgetServerCopies` (needs a kept copy whose source is `server`).

**Restore.** The client's trust file from setup step 5; delete `progenitor-pending\legacytest\`; HotDemo removed from both sides (or rebuilt without the switch); the test server's overlay, both `LocalIndexPath` values and the operator key files back, then restart the test server; `join_ui forget_seen`; disconnect and leave the client at the title screen.

## Unattended verification (server cases)

`tools/verify.ps1` runs declarative server cases from `tools/verify/` (review its ghost lifecycle before running on a disposable save): resolve a test speaker, send bridge commands, wait for observations and evaluate the log results. See [case-writing rules](../tools/verify/README.md).

```
powershell -NoProfile -File tools/verify.ps1 -Case all
powershell -NoProfile -File tools/verify.ps1 -Case config-watch
powershell -NoProfile -File tools/verify.ps1 -Case all -Json -OutDir C:\Temp\progenitor-verify
powershell -NoProfile -File tools/verify.ps1 -ListCases
```

The runner pings the side, resolves **one ghost admin for the whole run** before the first case
(reusing one that is already there and leaving it alone, otherwise creating one and recording its
index), then per case records the ring's sequence, runs the steps in order and prints one `PASS` /
`FAIL` line per step with the matched log line; at the end it restores every file a case edited from
a pre-run copy, destroys the ghost **only if it created it**, and exits `0` only when every step
passed (`1` a step failed, `3` the bridge or a precondition did). `-Json` writes the same result as a
machine-readable file under `-OutDir` (default: `%TEMP%\progenitor-verify\run-<timestamp>`), which is
also where the backups go.

Ghost ownership belongs to the whole verification run. Cases share the resolved index; they do not independently create, adopt or destroy a ghost. Cleanup may destroy only the run-owned ghost after confirming its identity.

Expectations are matched against the in-memory ring (the `logs` op), not the disk file, as
`[<Level>] <Source>: <text>` — so `\[Error\]` is a severity test. The match **window** opens at the
most recent `command` / `op` / `editFile` step and runs to now; `"scope": "case"` widens it to the
whole case (the default for `expectNoLog`) and `"scope": "ring"` to the whole ring, which is how a
line written at plugin attach is checked. `editFile` takes a regex, a replacement and a path with
`<config>` / `<reload>` / `<server>` / `<testroot>` placeholders resolved from `-TestRoot`.

Eight cases ship, transcribed from the evidence entries they replace:

| Case | What it checks |
|---|---|
| `config-watch` | One config edit produces one watcher reload and one ignored write-back. Check the case's current `knownFailing` / clearing conditions against the running Core. |
| `castles` | `.castle all` / `territory` / `tiles` / `canplace` / `raw` each write their reader line. |
| `systems` | Three virtual systems at three anchors, all counters non-zero after 12 s, `groupFired=True`, `.sys off` disposes three. |
| `ecs` | User census, `ServerRuntimeSettings` raw read, the tag-component refusal with no `Il2CppException`, the `Add/Write/Read/Remove` walk. |
| `handshake-net-store` | Handshake row plan and own event ids (ring scope), `.net reserved` both ways, `.store dir`, `.store flush` with the bytes read back. |
| `content-edit` | The typed write `Default(0) -> Consume(2)`, `.content drop`, and the same before-value on the second define and baseline restoration. |
| `rifts` | `.rifts send` writes the SmRf line, and a `reason=heartbeat` broadcast lands within 65 s. |
| `patch-kinds` | Prefix, postfix and finalizer on one live site, all three fire on `.patchdemo ping`, `RemoveAll … removed=3` on both the Sample's line and Core's (`left` is run-dependent). |

`-Case all` runs them in the `order` each file declares; `patch-kinds` is deliberately last because
`.patchdemo on` pins the Sample's ALC for the rest of the process.

The product’s own Configuration → **Install Progenitor on this server** toggle (SERVERINST1) is likewise file-only: it copies the client’s Bootstrap and Core into the configured server folder, records them in `BepInEx/progenitor-install.json`, removes only ledger files on Remove, refuses while a `VRisingServer.exe` from that folder is running, and never starts or stops a server.

What the rig will not do, by construction: it never starts, stops, restarts, deploys, builds or
promotes anything, and it opens no window. The ops a **case file** may call are allowlisted in the
script (`$AllowedOps`: reads plus `command` and `chat_observe`), so `quit`, `host`, `connect`,
`core_reload`, `reload`, `save`, `world_save`, `spawn` and `set` are refused by the runner rather
than merely unused; `-TestRoot`, every path derived from it, `-OutDir` and every `editFile` path go
through `tools/common/Fence.ps1`, and an `editFile` path that lands outside `-TestRoot` is refused.
`ghost` is **not** case-callable either — it writes and destroys world entities, so it sits in
`$RunnerOps` and only the runner's own two calls reach it; a case says `requires.ghostAdmin` and
never issues the op. Because `ghost destroy` is all-or-nothing over the whole ghost PlatformId
range (see [Ghost users](bridge-ops.md#ghost-users-server)) and has no per-ghost form, the runner refuses to create a ghost while one it did
not make is on the server, and re-lists before the sweep: a maintainer's own ghost is never taken
by a rig run.

**Ghost discovery fails closed.** Unanswered or malformed `ghost list` is retried three times. If discovery still fails, creation is refused (exit 3); uncertain cleanup is skipped with a warning naming the retained index. The sweep runs only when a fresh list identifies exactly the ghost this run created.

Interpret verification results with these limits:

- A ring-scoped assertion can fail because its line aged out of the 4,000-entry ring. Record that cause and rerun from a fresh relevant load when authorized.
- Cases may declare `knownFailing`, `clearsWhen` and `knownFailingSteps`. Conditions can use a deployed Core commit/generation/version, ring log presence/absence or a manual check. A staged but unobserved image is unknown. A passing case with a failure note or a failing case whose clearing condition holds must report the stale note; unrelated failing steps remain unexplained. These annotations do not turn a failing exit code into success.
- A ghost has no player chat channel. Chat-only replies and client visuals require a real user/client observation.
- Save-dependent values are checked as shapes; behavioral invariants are checked exactly. The extra-client/input/screenshot portion needs its own qualification.
