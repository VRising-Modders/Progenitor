# Hot reload

Use this page for current reload decisions and verification. The [minimal example](../samples/Progenitor.ReloadExample/README.md) teaches command/event cleanup; the [larger Sample](../samples/Progenitor.Sample/Plugin.cs) demonstrates content and state management.

## Choose the correct operation

Server/client deactivation is a separate [planned coordinated operation](mod-deactivation.md). Today's reload tools do not automatically retire matching client mods when a server mod is disabled. Do not simulate the proposed transaction by deleting client DLLs or issuing independent unloads.

An updated client Core requests a fresh handshake after its consumer plugins reload. With the updated Core on both sides, the server delays approximately 5.1 seconds before sending its ordinary requirements again; this restores config locks and menu edit availability without reconnecting. Check the fresh handshake and lock readback, not just the old handshake status. Config locking is not uninterrupted during a Core swap.

Progenitor performs the hot swap. Build/deployment tools deliver the DLL; the running framework owns unloading, cleanup, collection checks and loading. Do not write a separate assembly loader or restart a process as the normal mod-update path.

### AI callers: use Progenitor's tools

When the Progenitor MCP connection is available, call these tools with structured arguments. The names and arguments below are source-checked in [server.mjs](../tools/mcp/server.mjs); this documentation session did not call them against the game.

| Step | Progenitor call | Check |
|---|---|---|
| Build this example | Run the explicit project build in the [ReloadExample instructions](../samples/Progenitor.ReloadExample/README.md#build-offline). | The current `progenitor_build` wrapper searches `src/` only, not `samples/`. Do not pass it this sample's name; stop if the explicit build fails. |
| Deploy reviewed output | `progenitor_deploy` with `{"projects":["Progenitor.ReloadExample"],"side":"server","build":false}` after its explicit build. | Always supply project and side. Do not use the wrapper's broad defaults, which still include an old spike project. |
| Rescan consumer plugins | `progenitor_reload` with `{"side":"server"}`. | Inspect the load/unload result; an empty scan is not proof of a failure or success. |
| Inspect new records | `progenitor_logs` with `{"side":"server","since":-1,"max":60,"filter":"Reload:"}`; later use the returned sequence. | Confirm image identity and behavior. |

The current MCP server has no dedicated `progenitor_core_reload` or `progenitor_reload_status` tools. Use the direct Progenitor bridge calls below for those operations; do not invent tool names. Deployment normally lets Bootstrap's watcher schedule the Core swap. A direct bridge call still invokes Progenitor's own implementation.

For projects under `src/`, `progenitor_build` accepts the project name, for example `{"project":"Progenitor.Core"}`. With `{}` it builds the solution. Check the reported exit code before deployment.

### Programmer callers and tool fallback

The following commands use the same Progenitor bridge and deployment scripts when MCP is unavailable. All live steps require the authorized isolated test target.

| Change | Mechanism | Restart needed? |
|---|---|---|
| Reloadable mod DLL | Replace the settled DLL in `BepInEx/progenitor-reload/`; old plugin unloads, new plugin loads. | Normally no. An unload/pinning refusal may require a restart. |
| Core (`Progenitor.dll`) | Stage `BepInEx/progenitor-core/Progenitor.dll.next`; compatible Bootstrap swaps the Core generation. | Normally no. Check watcher settings, compatibility and the actual outcome. |
| Bootstrap (`Progenitor.Bootstrap.dll`) | Stage in `BepInEx/plugins/`; loaded at process startup. | Yes, for each process receiving the changed Bootstrap. Protocol changes require a compatible Core/Bootstrap pair on each updated side. |
| Supported method-body delta | Experimental Edit-and-Continue; does not rerun registration or unload. | Server startup must enable it. In-game execution remains unverified in the documented evidence; see [advanced instructions](dev-loop.md#method-body-hot-reload-edit-and-continue--the-inner-loop). |

A deploy that stages a new Core holds that side's plugin DLLs until the swap is observed. If the swap does not land, the plugins are withheld, not copied, and the script exits 3. Causes include a hosted private game, a refusal, a timeout or no bridge. Leave the hosted game or restart, then deploy again. See "The plugin hold" under [The three pieces](dev-loop.md#the-three-pieces) for the full list and `-ReleaseHeldPlugins`.

Do not put reloadable mods in the chainloader's `plugins` folder. Do not deploy the default all-project set when you only intend to update one mod. The client and server are separate processes; a server-only change does not inherently require a client restart. Bootstrap's `UiActionSlot` (the process-lifetime `Il2CppSystem.Action` trampoline the native MODS outer tab leases for `GridSelectionEntry.OnLeftClicked`; cleared on every Core unload path like `UiSlots`) is a Bootstrap file, so deploying it requires an authorized client restart; Core reads its capability by name and reports `adapter-unavailable` (imitation route) on an older Bootstrap without a protocol bump. The hosted-child liveness tracker (`HostedChildTracker`, which keeps `IsHostingPrivateGame` current without a main-thread process scan; see [dev-loop.md](dev-loop.md#the-three-pieces)) is Bootstrap code too: it needs an authorized client restart and no protocol bump, because Core reads only the unchanged `IsHostingPrivateGame` property. Bootstrap's `TextInputQueue` (TEXTINPUT1: the process-lifetime `Keyboard.onTextInput` subscription that queues typed characters for Core's text fields) is the same kind of change: it needs an authorized client restart, and a Core on an older Bootstrap keeps the US physical-key map (`mods_ui status` → `focus.textInput.capability` 0).

## Verification scope

Plugin reloads and in-process Core swaps on server and client have historical live evidence in the internal research notes. Those are dated observations, not guarantees for every current build or plugin. Read later results when a research file begins with its original design.

This documentation update performs offline checks only. The new minimal example is separately labeled compile-verified/awaiting live acceptance in its README and validation record. No new live reload success is claimed.

## Build and reload one existing Sample

These commands are source-checked against the build/deployment tools. Run from the repository root. Game operations below require an authorized isolated test session and a configured Dev bridge. If the user is playing elsewhere, stop at the offline build.

```powershell
dotnet build samples/Progenitor.Sample/Progenitor.Sample.csproj -c Debug
if ($LASTEXITCODE -ne 0) { throw 'Sample build failed; do not deploy' }
powershell -NoProfile -File tools/deploy.ps1 -Projects Progenitor.Sample -Side server -NoBuild
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed; inspect its output' }
node tools/mcp/call.mjs server reload
```

`-Projects` selects what is copied; without `-NoBuild`, deployment builds the whole solution. Here the explicit project build supplies the DLL. Stop if that build fails. `reload` requests an immediate scan; an empty result can mean the file has not settled or the watcher already handled it. Inspect the new load records and behavior:

```powershell
[IO.File]::WriteAllText((Join-Path $PWD 'reload-logs.json'), '{"since":-1,"max":60,"filter":"Reload:"}', [Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs server logs @reload-logs.json
```

This writes UTF-8 without a BOM on Windows PowerShell 5.1 as well as PowerShell 7. Delete the local `reload-logs.json` request file when finished.

Check that the intended assembly/image hash loaded, the previous instance unloaded cleanly, and the mod's command or behavior uses the new code. Repeat the change/reload cycle and check for duplicate callbacks or leftover resources. For a small step-by-step exercise, use the [reload example](../samples/Progenitor.ReloadExample/README.md). Its commands deliberately target only that project.

## The Install tab writes the reload folder on the admitted path

Job HOT1: `progenitor-reload/` is no longer written only by `tools/deploy.ps1` and Bootstrap's promote. When the Install-mods tab plans an install whose decision is `ApplyNow`, whose admission is `ApplyNowEligible` (an exact reviewed qualification record plus an available runtime snapshot — see [mod-manifest.md](mod-manifest.md) and the admission notes) and whose target is a root **this** process runs, `Install/HotLoadExecutor.cs` writes the approved image into `BepInEx/progenitor-reload/<pluginId>.dll` (temp file plus move, hash verified after the move) and records it in that root's `progenitor-install.json` as category `mod`, source `package:<owner>-<name>@<version>`. POLISH4: that **listing key** is the spelling every install route writes into `source` — the hot route, the ordinary-package route and the first-class `progenitor.json` restart route (`InstallStaging.AppendServerLedger`, which used to write the plugin id there and so made the local server's half of a Both install unreachable from a later session). The plugin id the install read from the bytes goes in the row's own optional `pluginId` field instead, and a row an older build wrote under the plugin-id spelling is still resolved, through the inspected-identity cache (`LedgerSpelling.Matches`). `framework` rows keep `client-image:<sha>`. The reloader's ordinary once-a-second scan then loads it — a reload-folder write is a reload-folder write, so nothing in `PluginReloader` changed, including the settle delay and the allow-list decision. Remove deletes that file through its ledger row, so the reloader unloads the plugin within a second and no restart is owed. Any other admission (`Unknown`, `Blocked`) and any target this process does not run keep the pending/restart route, and the plan says which one it took. A staged image is still not proof of a load: read the `Reload:` log lines and the loaded image, exactly as above.

### The Bootstrap admission snapshot

Job HOT2 supplies the "available runtime snapshot" that sentence depends on. Bootstrap owns the production reloader, the Core generation counter, the reload allowlist and the process-lifetime UnsafeHarmony table; Core can compile-reference none of them and is collectible besides, so a narrow snapshot crosses the seam instead of Core reflecting over active generations.

`Progenitor.Bootstrap.AdmissionSnapshot.Capture(pluginId, fileName, sha256)` returns plain values only — no `Assembly`, `Type`, `BasePlugin` or delegate, because a snapshot is read by a collectible generation and may outlive the one it describes. Every read is guarded and **fails closed**: a read that throws makes busy and pinned true and the allowlist false, because a snapshot that cannot see the loader must not read as a quiet one.

| Snapshot value | Bootstrap source |
|---|---|
| `ServerProcess` | `Plugin.IsServerProcess` |
| `Generation` | `CoreLoader.Generation` |
| `LoaderBusy` | `CoreLoader.IsReloading` |
| `PriorGenerationPinned` | `CoreLoader.PreviousGenerationAlive`, `ReloaderHost.LatchedPluginAlcsAlive()`, `ReloaderHost.AnyDetourPinned()`, `ReloaderHost.ShouldRefuseNewPluginAlc(stem)` |
| `PluginLoaded`, `PluginName`, `PluginVersion`, `PluginImageSha256` | `ReloaderHost.TryDescribeLoadedPlugin(guid, …)` (the one accessor HOT2 added; `LoadedPluginNames` answers names, not GUIDs) |
| `UnsafeHarmonyMarked` | `UnsafeHarmonySlot.IsMarked(guid)` |
| `AllowlistAllows`, `AllowlistStatus` | `ReloadAllowStore.Current().Decide(fileName, sha256)` — the **candidate** pair the install intends to write, not the loaded one, because an allowlist row matches both halves |
| `ReloadFolder` | `ReloaderHost.Folder` |

`ProcessHost.AdmissionSnapshotCapability = 1` publishes that this Bootstrap answers at all. It is deliberately **not** a `CoreLayout.ProtocolVersion` bump: an older Bootstrap is a valid pairing whose only consequence is that `Install/Admission/BootstrapAdmissionSnapshotSource.cs` returns an unavailable snapshot naming the capability, every admission is `Unknown`, and installs take the restart route. Shipping the snapshot to a live process is still a **Bootstrap change**, so it needs an authorized restart of each affected process; a Core reload alone will not pick it up.

Two rules the snapshot encodes rather than leaves to the caller:

- **A configured server folder alone never supplies a running-server decision.** `LocalServerRuntimeConnected` is true in Bootstrap's snapshot only when this process *is* the server. Since HOTDEMO1 (2026-10-03), a client admits the local-server half against a snapshot built from what it can see: `VRisingServer.exe` running from that folder (matched by full path), Progenitor Core there, and no reload allowlist (`HotLoadTrust.ProbeLocalServer`). With all three, the half is written into that server's `progenitor-reload/`, and an uninstall deletes it there. Without them it is refused with `LocalServerRuntimeUnavailable` and installs for the server's next start, as before. See [mod-manifest.md](mod-manifest.md#a-record-or-the-trusted-flag-alone-is-not-an-apply-now-install).
- **Pinned and marked block a fresh load under their own reasons.** A retiring same-name ALC with nothing loaded, and an UnsafeHarmony mark that survives an unload, both fill `CurrentPlugin`, so the refusal reads `PriorGenerationPinned` or `UnsafeHarmonyMarked` rather than being folded into `LoaderBusy`.

The three compatibility identities a qualification record is keyed by are resolved in `Install/Admission/CompatibilityIdentities.cs`: `gameBuild` from the Resilience pass's fingerprint, `bootstrapContract` from the Core/Bootstrap protocol pair, `coreContract` from Core's version plus the admission policy version. A value that cannot be resolved is the literal `unknown`, never blank — the record parser treats a blank on either side as a mismatch, so an unresolved build cannot accidentally satisfy a record that also left the field blank.

Because `coreContract` is Core's *build* version, every Core build expires every record. HOTDEMO1 (live 2026-10-03) hit this: HotDemo's record was reviewed against `core/0.1.0`, Core was `2026.10.28.7`, and the install correctly fell back to the restart route (`QualificationMismatch`). The Install tab's `Install: planned …` log line now ends with that reason. On 2026-10-03 it was decided to **trust the hotLoadable flag**: records are no longer required, and a clean hotLoadable `ProgenitorPlugin` is admitted with `TrustedHotLoadable` even when its record expired. A record is still listed as a fact, and still admits a plugin the flag does not cover. The rules are in [mod-manifest.md](mod-manifest.md#qualification-records-authoring-the-only-route-to-apply-now-job-hot2); see also [mods-menu.md](mods-menu.md#hotdemo-hot-install-and-uninstall-trusted-hotloadable-hotdemo1). Not live-verified.

### `Both` is admitted once per half

Job HOT4: a `Both` install is two processes, so it is admitted twice — the client half with `InstallTarget.Client` and the local-server half with `InstallTarget.LocalServer` (`InstallCatalogService.ComputePlan`). A reviewed record therefore applies to the half it was written for, and `QualificationRecord.TargetCovers` adds exactly one widening: a record authored for `Both` (`install_local {action:"qualify", target:"Both"}` writes one such row) covers either half on its own, while a `Client` record still never covers the server and a `LocalServer` record never covers the client. The plan carries the per-process decisions in `plan.halves [{target, decision, executorAvailable, reason}]`, and mixed halves are a valid plan: client `ApplyNow` with an executor plus server `RestartServerRequired`. Confirming one writes `progenitor-reload/` in this process for the client half and the server root's `progenitor-pending/index/` for the other (`result {hotLoaded: true, serverPendingPath: …}`); the row reads `installed / Remove` with `restart false` and the drawer adds `Local server: installed, loads at its next start`. Remove takes both halves back — a hot unload here and the server's pending file and ledger row with it. Before HOT4 the whole request target was evaluated once, so a client-scoped record matched nothing and both halves went pending.

### The recheck immediately before the write

The admission decision and the write are not the same instant, and Core can swap between them. `HotLoadExecutor.ExecuteToReload` therefore captures the snapshot again as its first act and compares it against the loader state the plan was bound to — the `loaderState` fact the evaluator recorded (`available`, `gen`, `busy`, `pinned`, `blocked`, `side`). On any drift it refuses with `AdmissionReasonCode.SnapshotChanged`, quotes both keys, and **writes nothing**; the running plugin is untouched. On a match the write proceeds and the result carries `rechecked: true`, which is also what `listing.action.result.rechecked` shows. A readback with `hotLoaded: true` and `rechecked: false` would mean the recheck did not run, so read both.

An evaluator with no snapshot source returns no recapture and the executor performs no recheck. That path is only reachable on a plan that could never have been admitted in the first place.

## The uninstall marker is honored before Core loads

Job MODS2 added the reverse of an install: **Uninstall Progenitor from this client** (since settings organization stage 4: Mods → Progenitor → Your settings · Private games → **Remove Progenitor from this PC** [Uninstall]) writes `BepInEx/progenitor-uninstall.json` (`{schema: 1, requestedUtc, files[]}`) and deletes nothing, because the files listed are the ones the running process loaded. The removal happens at the next start of the process, in `Progenitor.Bootstrap.UninstallAtStart.Honour`, called from `Plugin.Load` **before** `BindDiagnostics`, before `PendingInstalls.PromoteAtStart`, before any Harmony patch and before `ProcessHost.Install` loads Core. It deletes the listed files that exist, empties the Progenitor-owned folders (`progenitor-core`, `progenitor-pending`, `progenitor-reload`, `progenitor-cache`, `progenitor-staging`, `progenitor-hosted`), schedules its own `plugins/Progenitor.Bootstrap.dll` with `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` — it is the image this process is running from, so it can only go at the next reboot — writes `BepInEx/progenitor-uninstall.done` (`requestedUtc`, `completedUtc`, `deleted`, `left`), deletes the request, logs one line, and returns without loading Core.

Two rules decide what it may touch. Every path in the marker is relative to the BepInEx root, and it is checked on the spelling the marker carries (normalizing first would strip a leading `/` and make a rooted path look relative) and then again after resolution: nothing rooted, nothing with a `..` segment, and never BepInEx's own `core/`. A marker that carries one bad path is **refused whole** — nothing is deleted, the request is left on disk to inspect, and Core loads normally; the log line says `refused`. Nothing outside `BepInEx/` is ever touched, on either path.

Two consequences for a deploy loop: this is Bootstrap work, so it takes a restart of the affected process to take effect, and the `.done` note is the only evidence on disk that it ran — Bootstrap's own DLL is still there until the machine reboots, so its presence is not proof the removal failed. **Cancel** in the pane deletes the request; a marker left from a canceled plan is never honored, because cancel is the delete.

### One mod's uninstall marker (INSTALL2)

Job INSTALL2 gave a single mod the same shape, for the same reason: deleting the file of a loaded plugin does not unload it. Uninstalling an ordinary (non-hot) **loaded** mod from the Install tab or the mod page writes `<file>.uninstall` beside its `progenitor-reload/` copy and sets `uninstallPending: true` on that row of `progenitor-install.json` (`Install/UninstallMarkers.cs`). Nothing is deleted while the process runs, so the mod keeps working and **Reactivate** — which deletes the marker and clears the flag — is a real way back.

At the next start, `PendingInstalls.RemoveMarkedAtStart(ReloadFolder, …)` runs from `PromoteAtStart` **before** `PromoteAll`, and therefore before `ProcessHost.Install` starts the tick that owns the first reload scan: every `progenitor-reload/*.dll` with a `.uninstall` sibling is deleted together with the marker, a marker whose DLL is already gone is swept as an orphan, and a DLL that cannot be deleted keeps its marker, is warned about (`it loads this start and the next start tries again`) and is retried at the following start. The enumeration is flat — the same depth both reloaders scan — so a plugin-local dependency folder under that root is never touched.

Two details worth knowing:

- **No reloader changed.** Both reloaders glob the reload root with one flat `*.dll` pattern, and `<name>.dll.uninstall` is not matched by it, so a marker is invisible to the scan. `InstallConfirmTests.Bootstrap_removes_marked_reload_copies_before_the_promote` pins both the glob and the ordering.
- **The ledger row is dropped afterwards, not at the delete.** Bootstrap cannot reference Core, so it cannot rewrite `progenitor-install.json`. The next `ServerInstall.ReconcilePromoted` — which the Mods workspace runs on every open — drops a row that is flagged `uninstallPending` and whose file is gone; a flagged row whose file is still there is still only a request and stays. Until that pass runs, the ledger still names a file that is no longer on disk, which is why `IsUninstallPending` reads the marker as well as the flag.

A hot-loadable Progenitor mod is not marked: the reloader drops its assembly with the file, so its removal is the existing ledger-scoped delete and nothing survives to the next start. A pending install that never loaded is canceled the same way — the written file is deleted.

This is a Bootstrap change: it takes a restart of each affected process before a marker can be honored at all.

## Version swaps through the reload folder (JOINFLOW4)

The join flow's [version store](join-flow.md#9-keeping-both-versions-stage-4) swaps one mod's versions with nothing but reload-folder writes, so the reloader does the unloading and loading exactly as for a deploy. Source-checked and game-free tested; not live-verified.

- **Now** (no `ctx.UnsafeHarmony`, no dependents, a quiet loader): the displaced copy is kept in `BepInEx/progenitor-versions/` and removed from `progenitor-reload/` (unload reason `Removal` on the next scan). Under the same file name the kept copy replaces it in place: the ordinary changed-file reload (`Swap`). Under another file name the kept copy is written only after the reloader no longer lists the displaced plugin (Bootstrap's `ReloaderHost.TryDescribeLoadedPlugin`, through the HOT2 admission snapshot; with an older Bootstrap, after it drops out of this client's handshake answer or a few seconds), because `ReloaderHost.Scan` loads a settled new file inside its file loop and unloads removed files only after that loop; it is written through `.part`, hash-checked and stamped now. Two copies of one plugin GUID are never in the folder together.
- **Restart** (`ctx.UnsafeHarmony` — the reloader refuses an in-place swap of it — a retiring previous generation, dependents, or an older Bootstrap without the admission snapshot): the kept copy goes to `progenitor-pending/` and the loaded copy gets the INSTALL2 `.uninstall` marker; at the next start Bootstrap deletes the marked copy before it promotes the pending one ([above](#one-mods-uninstall-marker-install2)). No Bootstrap change.

A swap is never started by the bridge or the menus while a join is between its connect and the handshake answer; the handshake's own swap (before the reply) is the one that runs then.

## Swap Core and confirm completion

Core swaps unload and reload consumer plugins too. Process-lifetime plugins are the exception ([below](#plugins-that-inject-il2cpp-types-restart-required)). First inspect the baseline:

```powershell
node tools/mcp/call.mjs server reload_status
powershell -NoProfile -File tools/deploy.ps1 -Projects Progenitor.Core -Side server
node tools/mcp/call.mjs server reload_status
```

`Reload.WatchCore` is disabled by default in the shipped configuration; the deployment tool enables it for its development targets. When the watcher is enabled, a settled `.next` schedules the swap. Avoid sending another request while `inFlight` is true. When manual scheduling is appropriate, use:

```powershell
node tools/mcp/call.mjs server core_reload
```

Then poll `reload_status` at a reasonable interval. The request returning `scheduled=true` is not completion. Confirm that `inFlight` is false, the expected generation/image is active, and `lastReload.outcome` is `"ok"`. Inspect a refusal, parked watcher or restored-old-generation result before deciding what to do next. A generation number alone does not prove that the intended new bytes loaded. Check the logs and mod behavior after the cascade.

Apply the same procedure to `client` only when that client is authorized for testing. Do not use client bridge operations while it is playing on the live server.

### Core and Bootstrap from different builds: shared config

Bootstrap hands its `progenitor.cfg` to Core as `ProcessHost.SharedConfig` before Core loads (PR #24). This is optional and is **not** a `CoreLayout.ProtocolVersion` bump. With an older Bootstrap, Core still starts and each section it binds at install logs `Config: [Section] not bound yet …`. Once the chainloader completes, a line `… bound late from progenitor.cfg (chainloader ready)` follows for each section, about a second later. Until then that section runs on its defaults. The handover takes effect only after the affected process restarts with the new Bootstrap. After that restart, the startup log shows `Compat: RewriteMode=… (bound from [Compat] RewriteMode)` before `Chainloader startup complete`, with no `Config:` deferral lines. Both pairings were checked live on the isolated test server on 2026-09-24. How Core reaches the file is described in [Modules: Config](modules.md#config).

### Renamed ids (2026-10): deploy Bootstrap and restart every process

Progenitor's ids dropped a personal vendor prefix: Bootstrap and Core are `progenitor` (Harmony ids `progenitor` and `progenitor.bootstrap`), the bridge `progenitor.dev`, the preloader `progenitor.preloader`, the samples `progenitor.<sample>`, and the cfg files `progenitor.cfg`, `progenitor.ui.cfg`, `progenitor.serverlist.cfg`, `progenitor.servermessage.cfg`, `progenitor.<sample>.cfg`. Consumer plugins declare `[BepInDependency("progenitor")]`; a plugin outside this repository that still names the old id must change it, or BepInEx refuses it for a missing dependency.

This is a Bootstrap change and a `CoreLayout.ProtocolVersion` bump (5). Deploy Bootstrap, Core, the bridge, the preloader and the samples together, then restart each process (client, test server, headless clients). A new Core under an older Bootstrap is refused: a swap logs `Core reload refused: Bootstrap protocol 4 does not accept Core protocol 5.` and keeps the running Core; a start logs `Core not loaded: protocol mismatch; …` and stays Core-less until Bootstrap is deployed and the process restarted. A plugin file that the Install tab wrote under its old id stays in `progenitor-reload/` and no longer loads; reinstall it.

On the first start of the new Bootstrap, state an older build wrote is found again (`Progenitor.Plugins.LegacyIds`, which recognizes the old ids by shape):

| State | What happens |
|---|---|
| `BepInEx/config/<old prefix>.progenitor*.cfg` | Bootstrap copies each to its current name before any plugin binds it, keeps the old file as `<old name>.bak`, and logs one `Renamed ids: <old> -> <new> …` line. A current file that already exists is never overwritten (`Renamed ids: kept …`). |
| World-save stores (`<save>/progenitor/<old id>.json`) | Renamed the same way when the world loads (`ModSave: Renamed ids: …`). |
| `[ServerSettings] LockedInGame`, the join-admin store (`progenitor-join-admin.json`) | Old setting ids and mod ids read as the current ones; the next save writes the current ones. |
| Install ledger, qualification records, trusted operators, join notices seen, declined mods, server profiles, mod on/off state | Not mapped: they describe files and packages built under the old ids, which still carry them. A reinstall or a new prompt replaces them. |

Client and server must run the same side of the rename; there is no wire compatibility. A new server still recognizes an older client's Progenitor rows as Progenitor, and when it refuses that client the reason starts `Your Progenitor is older than this server's (its plugin ids were renamed). Update Progenitor and its mods.` Source-checked and unit-tested (`LegacyIdsTests`); not yet run live.

### Progenitor's own update (auto-update phase 5)

Progenitor can replace its own Core and Bootstrap from a Thunderstore release ([auto-update.md](auto-update.md#phase-5-as-built)). It uses the procedure above, with two additions a developer should know:

- A Core-only release is staged as `Progenitor.dll.next` and swapped with the reason `self-update <version>`; the new generation confirms the swap itself (no swap in flight, its own loaded image is the release). `reload_status` shows that reason in `lastReload`.
- **Bootstrap may write Core before it loads it.** While a release is on trial, Bootstrap's `Load` counts the start (`AutoUpdate: progenitor <version> is on trial: start N`), puts a staged release's Core in place (`starts now`), or, after two starts without Core's healthy mark, writes the previous Core back and stages the previous Bootstrap (`was rolled back`). Only a start that runs exactly the journal's images is judged: a deploy of other bytes ends the trial (`superseded`) and nothing is restored. The journal is `BepInEx/progenitor-versions/progenitor/self-update.json`.

Development trees never update themselves (a sub-release version or the Dev bridge loaded), unless the cfg-only `[AutoUpdate] SelfUpdateDevOverride` is set for a test. `ping.images` lists the running `Bootstrap` image (hashed at `Load`, before any promote) beside `Core`, so both halves of a deploy or an update can be checked. Bootstrap older than this phase does not report it.

## Reload-folder mods that depend on each other

Reload-folder DLLs load in dependency order. The reloader uses `[BepInDependency]` and assembly references, keeping file-name order when there are none. A mod can reference another reload-folder mod's assembly. A mod whose hard dependency is missing waits instead of failing: it shows in `reload_status.waitingPlugins` and on the Mods tab, and it loads by itself once the dependency arrives. Redeploying a provider reloads its dependents after it. Contract: [Modules: reload-folder load order](modules.md#reload-folder-load-order-follows-dependencies). This is a Bootstrap change, so restart each process to take it.

## Plugins that inject IL2CPP types: restart required

IL2CPP type injection (`ClassInjector.RegisterTypeInIl2Cpp`, `BasePlugin.AddComponent<T>`) cannot be undone in a running process. Bootstrap's reloader therefore never unloads or reloads such a reload-folder plugin in place. It detects the plugin from the image before it loads, and from `ClassInjector`'s injected names before any unload. On 2026-10-06, before this rule existed, a Core swap reloaded a third-party chat plugin. Its second registration failed with `already injected`, and chat stopped opening until the game restarted.

- **Redeploy or Install-tab update:** the running build stays loaded. The log says `Restart required`; the Mods tab shows `Restart required` on the mod; and `reload_status` shows `restartPending: "changed"`. Restart the process to load the new build.
- **Core swap:** a plugin that does not reference Progenitor stays loaded through the swap. A plugin that does reference it makes the process refuse Core swaps until it restarts, and the watcher parks. `tools/deploy.ps1` sees `coreBound` in `reload.processLifetimePlugins` and withholds the dependent plugins before staging.
- **Check:** `node tools/mcp/call.mjs client reload_status` lists them under `processLifetimePlugins`.

Contract, detection rules and messages: [Modules: plugins that inject IL2CPP types](modules.md#plugins-that-inject-il2cpp-types-are-process-lifetime). This needs a Bootstrap deployment and a restart of each process. Verified live on the Steam client on 2026-10-06: the Core swap kept that chat plugin loaded and a rejoin captured its canvas again. Later that day a human Enter press opened chat in a world.

## Write a mod that can unload

- Derive from `ProgenitorPlugin<TContext>`. Keep the context constructor empty; register in `OnLoad` through `ctx.Commands`, `ctx.Events`, `ctx.Hooks`, `ctx.Systems`, `ctx.Net` and other tracked facades.
- Use `ctx.Patches` for supported patches. Reading `ctx.UnsafeHarmony` marks the plugin non-hot-swappable; raw Harmony patches can retain the old assembly even after unpatching.
- Track additional disposable resources with `ctx.Track`. Explicitly detach callbacks registered outside the tracked facades and clear references that outlive the plugin. Tracking a handle does not make unsafe native registrations unloadable.
- Handle `OnUnload(ctx, reason)` only for extra cleanup. Context-owned registrations/resources are cleaned up on every reason. Preserve plain persisted state only when the next generation can safely rediscover it; do not preserve temporary unit/effect handles across Core replacement.
- Rediscover world entities after reload; do not persist raw runtime entity IDs as permanent identities. See [unload contracts](modules.md#unload-reason) and [world epochs](modules.md#world).

## Per-server mod pause (unload reason `Suspend`)

A client can unload its own reload-folder plugins for the length of one connection; see
[per-server mod pause](mods-menu.md#per-server-mod-pause) for when and why. Bootstrap's
`ReloaderHost.Suspend` unloads the DLL (every plugin in it, plus reload plugins that
`[BepInDependency]` on it) with `PluginUnloadReason.Suspend` and keeps it out of every later scan,
including after a redeploy of the same file (the new build loads when the pause ends); deleting the
file ends the pause. `ReloaderHost.Resume` releases the DLL together with the paused DLLs it depends
on and the paused DLLs that depend on it, and loads them dependencies first. A released DLL that
cannot load yet (its previous plugin ALC is still listed, or the file is inside the one-second
settle window) stays **resuming**: every scan retries it, with at most
`PluginSuspendPolicy.MaxCollectPulses` (5) blocking collect pulses per DLL, dependencies first, with
one log line per state change, and it is never put on the Failed list for that reason; only a real
load failure is. `ProcessHost.KeepPluginsPaused` turns a resuming DLL back into a paused one. `Resume` reports only DLLs loaded when it
returns; Core reports the rest (`ModsResumed`) when a later scan loads them. The paused set, each
paused plugin's handshake row ids, and a handshake request whose deferred reply was not sent yet
(`ProcessHost.PendingHandshakeRequest`, stamped with the connect address and
`ProcessHost.ConnectionIdentity`), and resumed plugins not yet reported
(`ProcessHost.PendingResumeReports`) live in Bootstrap, so a Core swap keeps them and the next
Core resumes by any recorded row and answers that request after its plugins reload. Plugins loaded from `BepInEx/plugins` and plugins
that took `ctx.UnsafeHarmony` are never paused.

**Deploying it needs a Bootstrap restart** of the client (the entry points and the slot code are
Bootstrap's). A Core running on an older Bootstrap finds no `ProcessHost.SuspendPlugins(string[], string[], out string)` (or no `PendingHandshakeRequest`) and falls
back to today's handshake refusal, logging `ServerModPause: cannot pause mods: this
Progenitor.Bootstrap is older than per-server mod pause (...)` once and showing a notice that asks
for an update and restart. Source-checked and game-free tested (`ServerModPauseTests`,
`PluginUnloadReasonTests`); not live-verified.

## When a reload does not complete

| Observation | Next step |
|---|---|
| No new load after copying | Check the selected side/folder, file settling, image hash, and allowlist. Byte-identical deployments may intentionally do nothing. |
| Swap was scheduled | Wait for completion status; do not repeatedly schedule it. |
| Previous generation is still alive | Read the pin/refusal diagnostic. Remove the retaining registration in source; a process-lifetime pin may need an authorized restart. The `pin-diag native-pin …` list shows what the generation held before release; the `pin-release NativePinRegistry … after=0` line confirms those were released. When native pins and `pin-diag holders` are clear, the retainer is outside Progenitor's statics: take a process dump and `gcroot` the generation's `LoaderAllocator` (the procedure is in the internal research notes). |
| `pin-diag Json collectible default=N` with N > 0 | This line is logged after the plugin sweeps and before Core's uninstall sweep, which drops what remains. If its samples name a plugin assembly, that plugin serialized after its `Unload` (a thread or timer it did not stop): stop that work in `OnUnload`. `modStore=` entries only hold retired plugin generations, never Core. The outgoing Core predates the JSON sweep when this line is **missing** from a refused swap. In that case one authorized restart loads a Core that can release the cache. |
| Watcher is parked | Read `watch.parkedReason`; repeated requests are not a repair. Follow [Core reload details](bridge-ops.md#core_reload). |
| New Core failed and old Core was restored | The intended update failed. Inspect quarantine/recovery logs and fix the build before retrying. |
| Core/Bootstrap protocol mismatch | Deploy a compatible pair and restart the affected process for Bootstrap. |
| Effect or buff still appears after a swap | Separate persisted game state from runtime guards and callbacks; read back what the new generation actually rearmed. |
| `Core reload refused: process-lifetime plugin …`, or a deploy says a plugin `keeps its current build` | A reload-folder plugin registered its own IL2CPP types. Read `reload.processLifetimePlugins`, then restart that process. See [below](#plugins-that-inject-il2cpp-types-restart-required). |

API details are in [modules.md](modules.md#hot-reload), bridge/tool reference in [dev-loop.md](dev-loop.md#reference-pages), and authoring examples in [tutorial.md](tutorial.md). Investigation history belongs in the external research links, not in this procedure.
