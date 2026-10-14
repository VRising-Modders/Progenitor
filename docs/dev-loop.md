# The Progenitor development loop

How to build a change, get it into a running V Rising client or staged test server, and read what happened — from a terminal, a script, or a Claude Code session.

Agents should use the Progenitor tools to build, deploy, request reloads and read the result; see the [hot-swap quickstart](hot-reload.md) for the primary MCP workflow and a small plugin example. Core scheduling/status currently use the direct bridge `core_reload` / `reload_status` operations; do not invent MCP wrappers. This page is the overview; the tool and operation reference is split across the [pages below](#reference-pages). Entity snapshots use the `snapshot` / `restore` operations; screenshots use [observe.ps1](observe-capture.md).

For authorized gameplay tests, use the existing [keyboard](bridge-ops-client.md#key) and [mouse](bridge-ops-client.md#mouse) bridge operations before asking a person to repeat movement or attacks. These operations work through the game's input without moving the OS cursor; `background:true` supports an unfocused client. They have no dedicated MCP wrappers: use `node tools/mcp/call.mjs client <op> @request.json`. Check menus, current bindings and input status first; a successful input report is not proof of the resulting animation or ability. See the [animation test procedure](player-animation-import.md#repeatable-gameplay-capture).

Current instructions belong here. Investigations, dated deployment state and superseded approaches are preserved in the internal research notes; they are evidence, not current operating instructions.

Never deploy Progenitor.Dev, and never point these tools, at the live dedicated server at `C:\VRisingServer`.

## Reference pages

| Page | Contents |
|---|---|
| [Development bridge](bridge.md) | Wire protocol, bridge users and the bridge queue, logging policy, ports, token, the opt-in gate, calling without MCP, main-thread ops |
| [Bridge operations: core, entities and samples](bridge-ops.md) | `ping`, `logs`, `reload`, `core_reload`, users, worlds, commands, ghost users, entity ops, `.debug` tools, Sample commands |
| [Bridge operations: players, world and capture](bridge-ops-player.md) | `heal`, `protect`, `time`, `photo`, `teleport`, `give`, `unlock`, `inspect`, snapshots, saves, `connect`, `host`, `capture`, `perf` |
| [Bridge operations: Mods workspace UI](bridge-ops-ui.md) | `mods_ui`, the game menu, text-field and date-time picker tests, `setting` |
| [Bridge operations: server info and join flow](bridge-ops-join.md) | `messages_record`, `server_rules`, `world_backups`, `server_desc`, `server_browser`, `join_ui`, `join_admin` and their acceptance recipes |
| [Bridge operations: input, install and session](bridge-ops-client.md) | `bot` interaction, `keybind_press`, `map`, `key`, `mouse`, `nav`, `build_search`, `materials`, `dye`, `install_local`, `remote_install`, `disconnect`, `quit`, `auto_update` |
| [MCP server and tools](mcp.md) | MCP tools, examples, registering the server in Claude Code |
| [Watching the client](observe-tool.md) | `tools/observe.ps1`: screenshots, synthetic input and the unattended capture queue |
| [Test rig](test-rig.md) | Local Thunderstore, extra and headless clients, the install-window acceptance procedure, unattended server verification |

## Path placeholders

Paths in these docs use placeholders instead of one machine's folder layout. Substitute your own folders.

| Placeholder | Meaning |
|---|---|
| `<workspace>` | The folder that holds the main repository checkout and its sibling folders (`_testserver`, `_tools`, `_build`, `_observe`, `_headless` and so on) |
| `<repo>` | The Progenitor repository checkout, `<workspace>\Progenitor` by default |
| `<testserver>` | The isolated test server root, `<workspace>\_testserver` (server binaries under `<testserver>\server`) |
| `<tools>` | Native tools and dumps, `<workspace>\_tools` (for example `<tools>\interop-dump`) |
| `<game>` | The V Rising client install folder (Steam default: `C:\Program Files (x86)\Steam\steamapps\common\VRising`) |
| `<live-server>` | The operator's live dedicated server, which no tool here may touch |
| `<local-evidence>` | A maintainer's local evidence folder for captures, probes and handoff files kept outside both repositories; cited so the evidence stays findable, not reproducible from a checkout |

## The three pieces

Three local pieces talk to each other. The game never talks to the MCP server, and nothing here opens a port on a public interface.

```
  editor / Claude Code
          |
          | stdio (MCP)
          v
   tools/mcp/server.mjs          build + deploy.ps1 + test-server.ps1
          |                      copy DLLs into the game folders
          | loopback TCP, JSON
          v
   Progenitor.Dev  (in the game process)
          |
          | calls into Core
          v
   Progenitor.Bootstrap  (chainloader, guid progenitor)
     FrameTicker, LogBuffer, MainThread queue, PluginReloader, SelfUpdate
     LoadFromStream BepInEx/progenitor-core/Progenitor.dll (collectible ALC)
          |
          v
   Progenitor.Core  (not a chainloader plugin; collectible ALC, RequestReload)
          |
          v
   BepInEx/progenitor-reload/*.dll
```

**Bootstrap** is the only chainloader plugin (`progenitor`). It loads Core from a byte stream, runs the frame ticker and main-thread queue even on the client menu, owns process-lifetime hooks and caches, and loads consumer plugins. Its own assembly remains mapped until the process exits.

**Core** (`Progenitor.dll`) loads into a collectible assembly context. A Core swap unloads consumers, snapshots framework state, uninstalls Core, allows a Unity frame for teardown, waits for collection, installs the replacement, and reloads consumers. A swap can refuse; always inspect its final status. Never attach a debugger to diagnose collection: inspecting collectible types can keep them alive.

**Consumer plugins**, including Progenitor.Dev, load from `BepInEx/progenitor-reload/`. Bootstrap scans once per second and waits for a file to settle before reading its bytes. A changed or removed DLL triggers `Unload()`. Dependencies resolve against the current Core generation. Plugin-local localization and assets belong under `<reload>/<AssemblyName>/`; assemblies loaded from bytes have no usable `Assembly.Location`.

Every build stamps a git-derived version (`year.month.release[.sub]`) into these assemblies; see [versioning.md](versioning.md). Each assembly carries the version of its own last change, so a component whose inputs did not change builds byte-identical and keeps its version.

| Assembly | Destination beneath BepInEx | How changes take effect |
|---|---|---|
| `Progenitor.Bootstrap.dll` | `plugins/` | Restart each process that needs the changed Bootstrap. |
| `Progenitor.dll` | `progenitor-core/` | In-process Core swap when Bootstrap is compatible and unload succeeds. |
| `Progenitor.Dev.dll` and consumer DLLs | `progenitor-reload/` | Automatic plugin reload after the file settles. |

Deploy stages Bootstrap and Core as `.dll.next` when their target process is running. Bootstrap promotes a staged Core before its first load and before a requested Core swap. Bootstrap's own staged DLL takes effect on the next launch. The test-server and client launch scripts also promote staged files before launch, covering forced exits that skipped quit handlers.

`Reload.WatchCore` defaults to **false**. The repository's deploy/staging scripts enable it in their selected developer trees. With it enabled, two matching scans of Core's `.next` request a swap; otherwise use [`core_reload`](bridge-ops.md#core_reload) or the next launch. A hosted server child does not watch. A client hosting a private game suspends its watcher until the child exits or it joins a dedicated server. It logs `Core watch refused: this client is hosting a private game; the child keeps the copied Core.` once, leaves the `.next` staged without parking it or writing a `lastReload` record, and reports the state only as `reload.watch.hostingPrivateGame` (`reload.watch.hostedChild` in the child) in `ping` and `reload_status`.

**The plugin hold.** When `tools/deploy.ps1` stages a changed Core against a running process, it holds that side's plugin DLLs (Dev, the Sample, consumers) until the bridge shows the swap landed: the generation moved, nothing is in flight, and `lastReload` is `ok` and no older than the staging. Only that releases them. If the swap does not land, the plugins are **withheld**: they are not copied into `progenitor-reload`, the new Core stays staged, and the script ends with `=== DEPLOY INCOMPLETE (WAIT) ===` and exit code 3. Causes:

- a hosted private game, or the hosted child;
- a refused or parked swap, or a quarantined image;
- `Reload.WatchCore` off;
- no bridge answer;
- another swap in flight or pending, or the built Core already staged and not yet running;
- no swap observed within `-CoreSwapTimeoutSeconds`;
- an interrupted wait.

To finish the deploy, leave the hosted game (the watcher then takes the staged Core) or restart that side. Then run the same deploy again: Core matches the live copy, so the plugins are copied straight away. `-ReleaseHeldPlugins` restores the old copy-anyway order. Use it only for plugins known to load against the running Core. A plugin built against a Core API the process lacks fails to load, with `MissingMethodException` or `TypeLoadException`. On 2026-10-06 such a Dev left its bridge answering `a main-thread op is already running` until restart. That gate leak is fixed: `MainThreadOpGate.TryEnter` can no longer strand the slot. `-CoreSwapTimeoutSeconds 0` still copies plugins before staging Core.

How the client knows its hosted child is still running (Bootstrap `HostedChildTracker`): the `StunProcess.Start` prefix sets the hosting flag and its finalizer passes the child's PID as a hint. A background thread then identifies the child by executable path plus argv (`-parentPID <client pid>` or `progenitor-hosted`; never by name, and never anything under `C:\VRisingServer`). It tries the hint first, then a scan pre-filtered by the `VRisingServer` process name. Bootstrap keeps an open handle to the match. Each once-a-second watch tick on the main thread is then a single non-blocking `WaitForSingleObject(handle, 0)`, with no process enumeration. Holding the handle also guards against PID reuse. The check fails closed: a failed scan, or one that could not read every candidate (`QueryFailed`), keeps the flag. The held handle signaling exit, a completed scan that read every candidate and matched none, a completed scan that no longer lists the PID the spawn site reported (even with unreadable candidates), or a dedicated join clears it. A resolve that finishes after the flag was cleared releases its handle on the next watch tick, hosting or not (2026-10-06 review follow-ups; game-free tested, not run in the game). An earlier build ran the full scan (`Process.GetProcesses` plus `MainModule` per process) on the main thread every second and caused 80–90 ms frames while hosting; see the [knowledge note](../knowledge/notes/hosting-probe-enumerated-all-processes.md). This is Bootstrap code, so a change to it needs a client restart. Built and unit-tested offline; not yet run in the game.

A Bootstrap protocol change requires a matching Core and a restart of every affected process. If both client and test server receive that Bootstrap, restart both. A change used only by one process does not inherently require restarting the other. Core wire-format changes require compatible client and server builds even when both can hot-swap; local generation numbers do not establish binary compatibility.

**Allowlist and recovery.** If `BepInEx/config/progenitor-allow.txt` exists, a staged Bootstrap needs its own `sha256  Progenitor.Bootstrap.dll` row. Use the matching row from the package's `SHA256SUMS`. A refused staged file is quarantined as `.dll.failed-<timestamp>`: correcting the row alone does not restage it. Verify the intended bytes, correct the allowlist, redeploy the affected project, then restart the affected process. Do not create or remove an allowlist merely to bypass a refusal.

A `PROMOTE VERIFY FAILED` message means the staged image changed between approval and promotion. Bootstrap restores the previous image. Identify what wrote the file before retrying.

**Core failure recovery.** A failed install can restore compatible last-good bytes and quarantine the failed image, provided the on-disk file still matches the failed load. A protocol mismatch stays Core-less until a compatible image is available; it does not restore an incompatible last-good Core. A Core-less process does not load Dev or consumers, so its bridge may be unavailable. Read the disk log and correct the staged Bootstrap/Core pair instead of repeatedly calling a missing bridge.

**The development bridge** is Progenitor.Dev's loopback TCP endpoint. The MCP server (`tools/mcp/server.mjs`) is a separate Node process that calls that bridge and repository build/deploy tools. Neither replaces Bootstrap's loader.

## Reload folder trust

The reload folder and the Core generation slot are automatic code-loading paths BepInEx does
not have, so both name their bytes in the log and both can be gated. Full reference:
`docs/modules.md` (Hot reload). What matters for the dev loop:

- **`Reload.WatchCore` ships `false`.** A release must not enable an automatic code-loading
  path for someone who just installed it. Our machines are developer machines, so
  `tools/stage-test-server.ps1` seeds and `tools/deploy.ps1` re-asserts
  `[Reload] WatchCore = true` in `BepInEx/config/progenitor.cfg` in the selected Steam
  client or `_testserver\server` deployment targets (the line it prints is
  `[client] Reload.WatchCore = true (updated) ->...`). Hot swap is therefore unchanged here.
  A tree deployed to by hand, or a fresh BepInEx install, gets `false` and logs
  `Core watch: Reload.WatchCore is false.` — set the entry or use `core_reload`. Never seeded
  into `C:\VRisingServer`: both scripts refuse that root.
- **`[Compat] RewriteMode` ships `DryRun`.** The compatibility rewriter (`docs/modules.md`,
  “Compatibility rewriter”) retargets a plugin’s IL — and its plugin-local dependency DLLs,
  leaf-first — against `driftmap-<build>.json` next to the address tables. `Off` computes
  nothing; `DryRun` (the default) runs the whole analysis, logs the block, and loads the
  **original** bytes; `Apply` loads the rewritten image. Core binds the key through
  `Config.Bind` at install, so it appears in `BepInEx/config/progenitor.cfg` with
  its description on the first start after a deploy — edit it there; the value is read once per
  Core generation, so a change needs a Core swap (`core_reload`) or a restart. If no map
  exists for the selected build, plugins load unchanged and the log reports
  `Rewrite: no map for build <id>; plugins load unchanged`. Grep the two hashes on the
  `Rewrite:` block to see what was vetted and what actually ran, and the `map... sha256` field
  for which map drove it. If you ever create `progenitor-allow.txt` on this machine, the map
  needs a row of its own or `Apply` falls back to `DryRun` with
  `Rewrite: refusing Apply:... is not on the allowlist`.
- **Every load line carries the hash and size.** Grep `sha256=` to see exactly which bytes
  ran: `Reload: loading <dll> sha256=<16 hex> size=<n> allowlist=none`, the full-hash
  `Reload: image …` companion, `Core watch: staged Progenitor.dll.next sha256=…`, and
  `Loaded Core from Progenitor.dll (progenitor-core:N, collectible) sha256=… size=… allowlist=none.`
- **`BepInEx/config/progenitor-allow.txt` is optional and off by default.** Do not create it
  on a dev machine: every rebuild changes the hash, so the loop would refuse your own DLL on
  the next deploy. It is the production posture, not the development one. When it exists,
  only listed `sha256  filename` rows load and everything else is quarantined; a file that
  does not parse fails closed. Correct the file and restage the intended bytes; do not
  remove it merely to bypass the gate.
  `tools/package.ps1` writes `packaging/out/SHA256SUMS` in exactly that row format.

## Publish check

`tools/publish-check.ps1 -Package <zip or folder>` checks a built mod package before it is uploaded to Thunderstore and prints one `PASS` / `WARN` / `FAIL` line per check (`-Json` for JSON; exit code 1 when a check failed). `tools/publish-check.ps1 -Dll <plugin.dll> -WriteProgenitorJson <path> [-HotLoadable]` writes a package's `progenitor.json` from the DLL's attributes and hash. Both build and run `tools/publishcheck`, a game-free .NET 8 tool that reads files and metadata only and contacts nothing. The checks are listed in [publishing.md](publishing.md#7-check-the-package-toolspublish-checkps1). Tests: `PublishCheckTests`, over two fixtures from `tools/make-dummy-package.ps1 -Publishable` and `-PublishFlaws`. Run with Windows PowerShell 5.1 on 2026-10-10 against both fixtures and the HotDemo build.

## Everyday workflow

1. Edit C# under `src/` or `samples/`.
2. `dotnet build` from the repo root (or let deploy do it).
3. Deploy:

   ```
   &./tools/deploy.ps1 -Projects Progenitor.Sample -Side server
   ```

   Useful variants:

   ```
   &./tools/deploy.ps1 -Projects Progenitor.Core,Progenitor.Dev,Progenitor.Sample -Side server
   &./tools/deploy.ps1 -Projects Progenitor.Sample -Side server -NoBuild
   ```

   The script refuses any path under `C:\VRisingServer`. It also runs `layoutgen --check` against
   the game's metadata when it is on the machine (`-DumpDir`, default `<tools>\interop-dump`)
   and refuses to deploy on exit 2, because CI cannot run that half and every number in
   `src/Progenitor.Core/Layout/layout.txt` is a raw memory write (docs/modules.md, "Layout constants").
   That check includes the metadata itself: each side's `GameAssembly.dll` is passed with
   `--game-assembly`, and metadata older than the binary it claims to describe — the shape the first
   day after a V Rising patch takes — is exit 2 with the re-export procedure, not a green run against
   the previous build.

4. Apply the [assembly-specific reload rules](#the-three-pieces). Consumers reload from the reload folder; compatible Core changes can swap in process; Bootstrap changes need an affected-process restart. Do not overwrite a mapped Bootstrap DLL or promote staged files by hand during a running process.

   When changed Core bytes are deployed to a running process, deploy stages Core first and normally holds consumer copies until it observes the swap. The success check requires a newer generation, no swap in flight, and a successful reload timestamp after staging. The wait is shared across selected sides (default 90 seconds), polls each side with a bounded call, and abandons a silent side after 30 seconds. See `-CoreSwapTimeoutSeconds`, `-CoreSwapPollSeconds`, `-CoreSwapPollTimeoutSeconds`, and `-CoreSwapSilenceSeconds` in [deploy.ps1](../tools/deploy.ps1).

   **A finished deploy does not guarantee that Core changed.** On refusal, parked watcher, quarantine, timeout or interrupted wait, the script still copies consumer DLLs and reports a warning. Those consumers may therefore be running against the old Core. Resolve the reported condition before interpreting plugin failures or continuing dependent tests. A pre-existing swap/staged image or unavailable bridge can also prevent the script from establishing an ordered swap. Read the per-side result.

   Identical Core bytes need no generation change. Deploy compares the pending `.next` as well as the live image so an older staged image is not silently left behind. `-WhatIf` builds nothing and writes nothing; it describes existing build output.

   Deploy builds the solution unless `-NoBuild` is set; `-Projects` selects copied artifacts, not the build scope. Projects outside the solution need an explicit build before deploy with `-NoBuild`. Script defaults are Bootstrap, Core, Dev and Sample; the MCP deploy wrapper defaults to Bootstrap, Core and Dev. Pass project and side explicitly to limit the change.

5. Verify the loaded image and behavior. Use `reload_status`, `ping` and bridge logs. If the bridge is unavailable, read the disk log. For native failures, inspect both the BepInEx log and the Unity game log; the bridge ring does not contain every native error.
6. For visual work, capture the result with [observe.ps1](observe-capture.md). Use focus-free capture by default, and use synthetic input only when the user has authorized client testing. Check the visible result, then repeat after reload to detect duplicate handlers or stale resources.

Typical consumer loop: edit → build → deploy that project to the selected test side → verify plugin load → exercise one behavior → inspect logs and, when relevant, a screenshot. A successful reload must leave one working instance of each handler and no resource from the retired plugin that changes subsequent behavior. See the [minimal quickstart](hot-reload.md).

The staged test server is `tools/test-server.ps1 start|stop|restart|status|log`. It only matches a `VRisingServer` whose path is under `<testserver>`. **`start` (and `restart`) promote staged `Progenitor.dll.next` and `Progenitor.Bootstrap.dll.next` before launch:** if the process is not already running and `<testserver>\server\BepInEx\progenitor-core\Progenitor.dll.next` or `...\plugins\Progenitor.Bootstrap.dll.next` exists, the script moves it onto the live file and prints `promoted Progenitor.dll.next` / `promoted Progenitor.Bootstrap.dll.next`. It also removes a leftover `plugins/Progenitor.dll`. A forced `stop` never runs the in-game promote-at-exit (`Application.quitting`), and promoting Bootstrap from inside the old copy is one launch too late, so this is how a Core or Bootstrap deploy against a running test server takes effect on the next `start`. A hosted private game from the Steam client also runs a `VRisingServer.exe`; its bridge is still port 27342, but its token and reload folder live under the client's install, not under `_testserver`. The MCP `server` side is wired to the staged test server.

**Launching the client** is `tools/launch-client.ps1`. It starts V Rising through Steam (`steam://run/1604030`), waits for the client bridge on 27341, then sends the `connect` op (Direct Connect via `GameHelper.LaunchGame`). Default target is `127.0.0.1:9976` (the staged test server). Pass `-Connect ""` to only wait for the bridge. `-Password` is the optional join password. `-Protocol` is `Lidgren` by default (the LanMode test server has no Steam listen socket); pass `-Protocol SteamIPv4` for a Steam-auth dedicated server. `-Host <saveName>` is mutually exclusive with `-Connect`: it sends the `host` op (Private Game / Host New) instead; default save name `progenitor-hosted` if the value is empty; `-Password` is then the host password. JSON for both ops goes through a temp file (`@file`) because PowerShell 5.1 strips quotes. When the client is not already running, the script promotes `BepInEx\progenitor-core\Progenitor.dll.next` and `BepInEx\plugins\Progenitor.Bootstrap.dll.next` (same log lines as `test-server.ps1 start`) before `steam://run`, because a kill skips quit handlers. It also removes a leftover `plugins/Progenitor.dll`.

`-Restart` closes a running client before launching. It first sends the bridge `quit` op (`Application.Quit`) if the bridge answers, waits up to **30 s** for the process to exit, then falls back to `Stop-Process` on the path-matched `VRising.exe` (never by process name; never `C:\VRisingServer`). **Without `-Force`**, `-Restart` refuses when a client world exists in the bridge `ping` worlds list (`Client_0`, …) unless `-Quit` is also passed — that is the "user is playing" guard. A client sitting at the main menu (ping answers, no `Client_*` world) can be restarted without `-Force`. If the process is running but the bridge does not answer, `-Restart` also refuses without `-Force` or `-Quit` (cannot tell whether the user is playing). `-Force` skips the in-world refusal and still tries `quit` first, then kills.

| Flag | Default | Meaning |
|---|---|---|
| `-Connect <address>` | `127.0.0.1:9976` | After the bridge answers, send `connect`. IPv4, `host:port`, or `SteamIPv4://host:port` (the op parses; hostnames and IPv6 fail there). Empty string skips the join. |
| `-Protocol <name>` | `Lidgren` | Connect transport forwarded as `args.protocol`: `Lidgren` (LanMode test server) or `SteamIPv4`. Ignored with `-Host`. |
| `-Host <saveName>` | (off; empty value → `progenitor-hosted`) | After the bridge answers, send `host` instead. Mutually exclusive with `-Connect`. |
| `-Password <text>` | (none) | Join password for `-Connect`, or host password for `-Host`. |
| `-TimeoutSeconds` | `150` | How long to wait for the client bridge after Steam start. |
| `-Restart` | off | Close a running client (bridge `quit`, wait 30 s, then `Stop-Process`) and launch again. |
| `-Quit` | off | With `-Restart`: allow quitting even when `ping.worlds` contains a `Client_*` world. |
| `-Force` | off | With `-Restart`: do not refuse when in a world or when the bridge is down. Still tries `quit` first. Never kill a client the user is playing in without `-Force` or `-Quit`. |

```
powershell -File tools/launch-client.ps1
powershell -File tools/launch-client.ps1 -TimeoutSeconds 180
powershell -File tools/launch-client.ps1 -Connect 127.0.0.1:9976
powershell -File tools/launch-client.ps1 -Connect 127.0.0.1:9976 -Protocol Lidgren
powershell -File tools/launch-client.ps1 -Connect 127.0.0.1:9976 -Password secret
powershell -File tools/launch-client.ps1 -Connect SteamIPv4://127.0.0.1:9976
powershell -File tools/launch-client.ps1 -Connect ""
powershell -File tools/launch-client.ps1 -Host progenitor-hosted
powershell -File tools/launch-client.ps1 -Host progenitor-hosted -Password secret
powershell -File tools/launch-client.ps1 -Restart
powershell -File tools/launch-client.ps1 -Restart -Quit
powershell -File tools/launch-client.ps1 -Restart -Force
```

Join / host is async: the script prints the op result and exits; `Client_0` appears on `ping` after the game scene loads (and, for `-Host`, after the bundled server child starts). A second `connect`/`host` while that scene is still loading returns `"launch already in progress"` (120 s timeout, or client `WorldReady`, clears the slot). There is no verified `-connect ip:port` process argument — do not add one. `progenitor_connect` / `progenitor_host` are the same ops without launching Steam.

## Method-body hot reload (Edit-and-Continue) — the inner loop

**Experimental, server-only, off by default. In-game operation is not verified.** The implementation and ordinary.NET-host prototype exist; neither establishes that deltas work inside V Rising's embedded IL2CPP host. Use full plugin reloads for the normal loop. The following is a qualification procedure, not a verified game quickstart.

Prerequisites: a disposable staged test server, authorization to restart it, a Debug plugin DLL with its matching PDB beside the build output, and a.NET 6 runtime for `tools/enc`.

1. Start the test server with the runtime gate present before CoreCLR starts:

   ```powershell
   &./tools/test-server.ps1 restart -EditAndContinue
   ```

   Expect the launcher to report `DOTNET_MODIFIABLE_ASSEMBLIES=debug` in the server environment. Setting it inside an already running process does not enable deltas.

2. Build and deploy the Debug plugin once, then confirm it is loaded:

   ```powershell
   &./tools/deploy.ps1 -Projects Progenitor.Sample -Side server
   node tools/mcp/call.mjs server ping
   ```

3. Start the source watcher from the repository root:

   ```powershell
   dotnet run --project tools/enc -- --watch --project samples/Progenitor.Sample
   ```

   Defaults use that project's `bin/Debug/net6.0` baseline and the staged test server's reload folder. Options are `--baseline <dll>`, `--out <reload-folder>`, repeatable `--ref <dir>`, and `--once`. Reference scanning includes the baseline, reload/Core, game interop and runtime folders; unreadable or non-managed files are skipped with a diagnostic. Check unexpected skipped files before proceeding.

4. Change one observable method body and save. The watcher should report an emitted `.delta`; the server must separately report `Reload: applied delta...`. Exercise the changed behavior and confirm its state and handler count survived. An emitted file or an `applied` log alone is not sufficient live evidence.
5. Rebuild/redeploy for a refused structural edit, changed registration, rewritten plugin or out-of-step sequence. This returns to full generation reload and establishes a new baseline. Do not delete status files to conceal a load failure.

| Watcher exit code | Meaning |
|---|---|
| 0 | Delta written or no change needed; does not prove the game applied it. |
| 2 | Unsupported structural edit or `EmitDifference` refusal; rebuild and redeploy. |
| 3 | Setup or compilation error; fix the named error before retrying. |

The reloader publishes `<DLL-stem>.delta-state` next to the reload folder with the loaded image hash, applied count and per-load nonce. The DLL file stem routes both status and deltas, even if the assembly name differs. A new nonce resets the watcher to deployed sources so all subsequent edits can be re-emitted. Missing status or `loaded=0` blocks emission: confirm the plugin loaded successfully. If the target applied only part of a sequence, rebuild and redeploy; no disk image represents that partially updated state.

Limits:

- Deltas do not run `Load()` or registration code again. Plugin metadata/signature changes and edits rejected by the conservative classifier require full reload.
- Release assemblies are not editable. Preserve the matching Debug PDB for local/lambda maps.
- A plugin modified by compatibility rewriting refuses deltas. `RewriteMode=Off` and the default `DryRun` retain original bytes; `Apply` can change them.
- An active allowlist must also authorize each delta's hash and filename. Do not bypass that gate for testing.
- Client EnC is not wired into `launch-client.ps1`: Steam supplies the client process environment. Do not infer client support from the server flag.

The standalone prototype is `dotnet run --project tools/enc` (add `-- --matrix` to inspect supported edit categories). It is outside the solution; solution builds do not exercise that.NET 6 host. Linked tests run under the test project's runtime, and their apply portion needs `DOTNET_MODIFIABLE_ASSEMBLIES=debug` set before the test process starts. Preserve that distinction in any verification report.

Source: [EnC tool](../tools/enc/Program.cs), [test-server launcher](../tools/test-server.ps1). Historical investigations and failure examples are in the internal research notes.

## CI

GitHub Actions (`.github/workflows/tests.yml`) runs on every push and pull request. It only builds and runs `tests/Progenitor.Tests` — the game-free unit tests that compile selected Core and Dev sources through `Compile Include` links, with `<ReferenceGame>false</ReferenceGame>` so `Directory.Build.targets` does not pull BepInEx or interop assemblies.

**Core and Dev cannot build in CI.** Those projects set `<ReferenceGame>true</ReferenceGame>` and compile against `$(VRisingGameDir)\BepInEx\core\*.dll` and `$(VRisingInteropDir)\*.dll`. A runner has no V Rising install, so `dotnet build` of the solution or of `src/Progenitor.Core` / `src/Progenitor.Dev` fails the interop check in `Directory.Build.targets`. Do not add those projects to the workflow, and do not `ProjectReference` them from the test project (that would pull the game references transitively).

The job is:

```
dotnet test tests/Progenitor.Tests --configuration Release --logger trx
```

The trx log is uploaded as a workflow artifact. The same command is the local check; it does not need the game. The `test` job checks out full history (`fetch-depth: 0`) so the [build version](versioning.md) can find `origin/main`; `VersioningTests` needs `git` and the dotnet SDK on PATH.

**Time zone and culture.** The runner is in UTC with an en-US culture; a developer machine usually is not. Tests must not depend on either: code that shows local times takes its zone from an injectable seam (`RestoreEnvironment.Zone`, `ServerBackupsServer.Zone`, `KeybindGameCache.Zone`), tests set a fixed zone, and text meant for logs or files formats with an explicit culture. To run the suite as if the machine were elsewhere (no system setting changes; `TestRunZone.cs` swaps the test process's local zone and culture):

```
$env:PROGENITOR_TEST_TIME_ZONE = 'Line Islands Standard Time'   # or UTC, Dateline Standard Time
$env:PROGENITOR_TEST_CULTURE = 'de-DE'                          # a decimal comma
dotnet test tests/Progenitor.Tests --configuration Release
```

The `test` job runs the suite once more this way after the normal run.

**Optional external fixture.** `CastlesTests.Territory_buffer_strides_and_offsets_are_the_ones_the_decoders_walk` checks the decoder constants against the server metadata or the layout block in the internal research notes. It reports a reasoned skip when neither exists and a hard assertion when either is available. A skip does not verify the installed game's layout. `Directory.Build.props` accepts `VRISING_GAME_DIR` and `VRISING_INTEROP_DIR`. To check the game-free test build, point those at a nonexistent path:

```
$env:VRISING_GAME_DIR = 'C:\does-not-exist'
dotnet test tests/Progenitor.Tests --configuration Release --logger trx
```

### The `manifest` job: every `<Compile>` path resolves, at every commit

`tests/Progenitor.Tests` links source files with explicit `<Compile Include>` entries. Add a file and its project entry together so every reviewable commit builds. The manifest check verifies that all referenced paths exist.

**The `test` job cannot catch this, and neither can a test.** GitHub Actions runs a push event
once, at the head commit; the commits under the tip are built by nothing. And a check written as an
xunit test lives inside the very assembly that fails to compile when an `Include` is broken.

So there are two halves, and they are deliberately separate:

- `tests/Progenitor.Tests/ProjectFileManifestTests.cs` asserts the rule at the tip. Its value over
  the compiler is the two cases the compiler is silent about: projects CI never builds (the
  `tools/driftmap` link to Core's `DriftMap.cs`), and `<Compile Remove>` — a `Remove` whose file was
  renamed matches nothing, says nothing, and quietly stops filtering the
  `-p:SkipForeignTestFiles=true` build.
- The `manifest` job runs `.github/workflows/compile-manifest-check.py` over **every commit the push
  or PR adds**. It reads csproj blobs and tree listings straight out of git — no toolchain, no
  per-commit checkout, no build, so it does not violate the "Core and Dev cannot build in CI" rule
  above. Sixty commits take about four seconds.

To run it by hand over any range:

```
$env:RANGE_BASE = (git rev-parse HEAD~20); $env:RANGE_HEAD = (git rev-parse HEAD)
python.github/workflows/compile-manifest-check.py
```

Both halves skip wildcards (`Effects\*.cs` is legal MSBuild and may match nothing) and any path
holding an unexpanded `$(...)` property. Both refuse to pass vacuously: if fewer than 100 paths
resolve, they fail on the grounds that the reader has stopped seeing the file lists.

## Rules for a reloadable plugin

Plugins load into an already running game as well as at startup. Use the [quickstart](hot-reload.md) and [Sample](../samples/Progenitor.Sample/Plugin.cs) for setup and ownership patterns.

- Reverse subscriptions, network handlers, commands, added systems and scheduled work in `Unload()`. Track framework-owned resources in the plugin context and dispose that context. Returning `false` reports incomplete cleanup; it does not cancel unloading.
- Use `ctx.Patches`, `SystemHooks` or `Events` for callbacks. Direct Harmony patches keep collectible code referenced in process-wide caches even after unpatching. Accessing `ctx.UnsafeHarmony` forfeits the hot-swap guarantee.
- `ctx.Patches` uses Bootstrap-owned slots and supports `(object instance, object[] args)`, without `__result`. Slots are bounded (32); handle refusal such as `slots-exhausted`. Dispose/remove the registration during unload. See [module contract](modules.md#hot-reload).
- Tag `MainThread.Post`, `Run` and `PostDelayed` work with the plugin instance or `ctx.Owner`. Unload drops work owned by the retired instance; do not keep delegates from it in process-lifetime statics.
- Keep mutable plugin state on the instance or in a context disposed at unload. Explicitly document any intentionally persistent state and who releases it.
- Do not register IL2CPP types or custom `MonoBehaviour`s from a collectible plugin. Those registrations live for the process lifetime.

Verify at least two successive reloads: the changed behavior appears, a single event causes a single response, cleanup removes owned resources, and no new load/unload errors appear. A loaded DLL alone does not prove that the old instance released its resources.

## Troubleshooting

**No token file yet.** MCP: `no token file for client at...\progenitor-dev.token; has the game started with Progenitor.Dev loaded?` (or `server`, with the `_testserver` path). The file is written in Dev's `Load`. When authorized, start the selected test process with `Progenitor.Dev.dll` in `progenitor-reload`, Core in `progenitor-core`, and Bootstrap in `plugins`. Confirm the [Dev opt-in](bridge.md#the-opt-in-marker--the-bridge-refuses-to-start-without-it) is present. Confirm with `BepInEx/LogOutput.log`: you want a line that `Progenitor Dev Bridge` is listening on `127.0.0.1:27341` or `27342`. If `Bridge.Enabled` is false in config, no file and no socket.

**Bridge not reachable.** MCP: `client bridge not reachable on 127.0.0.1:27341:...` (or server / 27342). The process is not running, it has not finished loading Dev, the port in config does not match `PROGENITOR_*_PORT`, or something else bound the port. `progenitor_logfile` still works. `progenitor_ping` is the check that the socket is actually accepting.

**Plugin failed to load.** `reload` `events` (and the BepInEx log) include `Reload: loading <file> failed:...` or `has no BepInPlugin types`. The DLL must contain a non-abstract `BasePlugin` with `[BepInPlugin]`. A missing dependency on `progenitor`, a bad `AssemblyName`, or an exception in `Load` all land here. After a failure the reloader unloads whatever it did construct; fix, redeploy, wait a second for settle.

**Main-thread timeout.** Bridge error: `timed out waiting for the main thread (is the world running?). The waiter was failed after 30 s; the original work is abandoned and will not run a second time. The in-flight slot stays held until that work completes or is dropped.` MCP may instead report `server bridge timed out on 'ping'` (its own 25s cap). `call.mjs` times out at 30 s (`PROGENITOR_TIMEOUT_MS`) with exit 3. A second main-thread op while the first is still in flight **or still queued after that timeout** returns `"a main-thread op is already running; retry after it finishes"`. The slot is freed when the abandoned lambda finishes or is dropped, and on Dev `Unload` (`DropOwnedBy` then `AbortAll` in a `finally`). A retry before that is expected to fail with already-running, not to double-run. After a timeout, probe with `logs` and `bridge_status` (socket thread); do not ping again. Soak ping interval at least 30 s. Main-thread ops wait for Core's per-frame drain. That drain runs from an injected `FrameTicker` MonoBehaviour every Unity frame from launch, including the client main menu, so `ping`, `reload`, `connect`, `host`, `host_capture`, and `quit` should work before you are in a game. Entity ops (`query`, `get`, `set`, `components`, `spawn`, `players`, `users`, `command`, `chat_observe`, `ghost`) still need a world; wait until `progenitor_ping` reports `Server` or `Client_0`. `status`, `save`, and `store` are also main-thread ops; `save`/`store`/`chat_observe` need the server process. `chat_observe` holds the bridge request for `windowMs` (default 1.5 s) without blocking the game frame (the 30 s op cap still applies). `connect` and `host` are the opposite: they error if `Client_0` already exists, and they share a 120 s launch-in-flight slot (`"launch already in progress"` while the scene is loading). `quit` does not refuse. `ops`, `logs`, and `bridge_status` do not need the main thread and should still work once the socket is up.

**Bridge still reports "already running" after reload.** Wait for the old request to finish or for Dev unload to complete. A new Dev instance should bind its listener and accept work. If it remains stuck after a confirmed reload, preserve both logs and the request/status output; this is a defect, not a reason to keep issuing the same mutation.

**Every call reports "owning plugin is unloading" after reload.** The new plugin instance should accept work after it loads. Persistent refusal after the new bridge is listening is a defect. Record the preceding reload result and logs before restarting an authorized test process.

## Deploy audit 2026-09-06

Destinations and packaging rules for [deploy.ps1](../tools/deploy.ps1) and
[package.ps1](../tools/package.ps1). Select projects and the target side explicitly.

Preview the window's deploy first — it writes nothing, builds nothing, and prints every file it
would copy with its sha256 prefix:

```
pwsh tools/deploy.ps1 -WhatIf
```

Or with an explicit project and side:

```
&./tools/deploy.ps1 -Projects Progenitor.Sample -Side server -WhatIf
```

The prefix is the same 16 hex characters the load lines print (`sha256=…`) and `packaging/out/SHA256SUMS`
carries, so a planned row can be read straight against the BepInEx log after the deploy, or against
a row in an allowlist. `-WhatIf` skips `dotnet build` on purpose (a build writes into `bin/`), so it
plans against whatever is already in `bin/Debug/net6.0` — build first if you want the plan to
describe the code you just wrote.

| Artifact | Created by | `deploy.ps1` → test tree | `package.ps1` → zip | `C:\VRisingServer` fence |
|---|---|---|---|---|
| `Progenitor.Bootstrap.dll` | `dotnet build` | yes → `BepInEx/plugins/` (`.dll.next` while the process is up) | yes → `BepInEx/plugins/` | held |
| `Progenitor.Preloader.dll` | `dotnet build` | only with `-Projects Progenitor.Preloader` → `BepInEx/patchers/`, and only while the process is stopped (see [Deploying the preloader](#deploying-the-preloader)) | not yet | held |
| `Progenitor.dll` (Core) | `dotnet build` | yes → `BepInEx/progenitor-core/` (`.dll.next` while the process is up) | yes → `BepInEx/progenitor-core/` | held |
| `Progenitor.Dev.dll` | `dotnet build` | yes → `BepInEx/progenitor-reload/` | **no** — only with `-IncludeDev`, and its absence is asserted on the staged tree *and* on the sealed zip | held |
| `Progenitor.Sample.dll` | `dotnet build` | yes → `BepInEx/progenitor-reload/` | **no** — the sample is a consumer, not a release payload | held |
| `<plugin>/localization/*.json` | `dotnet build` (copied beside the DLL) | yes → `<reload>/<AssemblyName>/localization/` | only under `-IncludeDev`, for Dev | held |
| `<plugin>/assets/**` | `dotnet build` | yes → `<reload>/<AssemblyName>/assets/` | only under `-IncludeDev`, for Dev | held |
| Address tables `{client,server}-<build>.json` | `node tools/addresslib/build.mjs --side <side> --script <side-method-address-listing> --build <id>` — gitignored, rebuilt per game build | yes → `BepInEx/progenitor-core/addresslib/` in each selected tree, on every deploy | yes → `BepInEx/progenitor-core/addresslib/` (also embedded in Core as `progenitor.addresslib.*`) | held |
| `build-<side>-<build>.json` (Resilience fingerprint) | **Core, at runtime**, into `progenitor-core/addresslib/` beside the tables | **never** | **never** — asserted | n/a |
| `driftmap-<build>.json` | `dotnet run --project tools/driftmap`, placed **by hand** and allowlist-vetted. Check the selected build; do not assume a map is present | **never** | **never** — asserted | n/a |
| `BepInEx/progenitor-core/`, `BepInEx/progenitor-reload/` | `deploy.ps1` (first deploy into a tree) | folders only | folder layout inside the zip | held |
| `BepInEx/progenitor-pending/` | **Core**, when a client accepts a server package (`ModManifestPending`) | **never** | **never** — asserted | n/a |
| `<plugin>.<n>.delta` | `tools/enc` watch mode, straight into a reload folder | **never** | **never** — asserted | n/a |
| `<plugin>.delta-state` | **the reloader**, published beside the reload folder | **never** | **never** — asserted | n/a |
| `BepInEx/config/progenitor-allow.txt` | **the operator**, by hand. Nothing in this repo may create one | **never** — deploy touches only `progenitor.cfg` | **never** — asserted | n/a |
| `BepInEx/config/progenitor.cfg` (`[Reload] WatchCore = true`) | `stage-test-server.ps1` seeds, `deploy.ps1` re-asserts — developer trees only | yes, selected trees | **no** — `WatchCore` ships `false` | held: both scripts refuse the root before writing |
| `BepInEx/config/progenitor-dev.enable` | **by hand**, once per BepInEx tree | **never** | **never** — asserted | n/a |
| `*.token` (bridge token) | `Progenitor.Dev` at runtime | **never** | **never** — asserted | n/a |
| `access.txt` / `tools/accessgen` output | repo-side generator; the access layer is **generated code compiled into Core**, so nothing is needed at runtime | **never** | **never** — asserted | n/a |
| `tools/enc`, `tools/driftmap` build output | `dotnet build` of those tool projects | **never** | **never** — asserted | n/a |
| `packaging/out/SHA256SUMS` | `package.ps1`, for the zip this run produced | n/a — deploy never reads or writes it | written beside the zip; every row pastes into `progenitor-allow.txt` | `Assert-NotForbidden` |

The shared [path fence](../tools/common/Fence.ps1) resolves supported drive/share mappings, aliases and links before comparing directory boundaries. Unrecognized paths fail closed. Never work around a refusal by changing the spelling of the live-server path. Packaging asserts that runtime-owned and developer-only artifacts are absent; address-table deployment selects the client/server build tables, not every JSON file in that folder.

### Deploying the preloader

Status: source-checked, not live-verified.

`Progenitor.Preloader.dll` is a BepInEx preloader patcher. It carries out the Mods menu's next-start requests for ordinary plugins before the chainloader opens any plugin file. It is not in the default `-Projects` list, so select it explicitly:

```
&./tools/deploy.ps1 -Projects Progenitor.Preloader -Side client
```

Deploy copies it to `BepInEx/patchers/` (created if missing, behind the same live-server fence) **only while that side's process is stopped**. BepInEx loads patchers before the chainloader and nothing promotes a `.next` there, so there is no staging: for a running side, deploy prints a warning and skips that side. Stop the game or test server, deploy again, and the patcher takes effect at the next start. `-WhatIf` lists the planned copy as usual. `package.ps1` does not package the preloader yet.

## What needs a process restart

| Change | Required action |
|---|---|
| Consumer plugin | Reload from `progenitor-reload`; verify unload and the replacement behavior. |
| Core with compatible Bootstrap | Request or watch an in-process swap; verify final status and image hash. Restart if collection is refused. |
| Bootstrap | Restart each process that must run the changed Bootstrap. Stage its compatible Core first. |
| Bootstrap/Core protocol change | Deploy the compatible pair and restart affected processes. Both sides need restarting when both were updated. |
| Core network protocol or consumer API | Update communicating sides or rebuild consumers as required. Matching generation numbers do not prove matching code. A restart is only needed for another applicable condition. |
| Process-start settings, refreshed native address hooks or a fresh save world | Follow the affected subsystem's restart/load procedure. A Core swap does not recreate the process or world. |
| Dev opt-in added after load | Reload Progenitor.Dev or restart the selected process to re-run the gate. |

## Client testing restrictions

- Never deploy these tools to or test against `C:\VRisingServer`; match processes by verified path, not executable name.
- Client testing, inputs, launches and restarts require the user's authorization. Do not interrupt a client the user is playing on.
- After a crash, preserve logs before the next launch overwrites evidence and obtain the operator's direction before relaunching.
- Do not run RenderTexture camera effects (`menu` host variants) on the live client.
- Keep deployments serialized: one deploy at a time. Builds and tests may run while the game runs, subject to available resources and user instructions.

The dated restart debts, crash investigation and deployment review narrative have moved to the internal research notes. Do not treat that historical process state as a current instruction.

## Moved sections

Sections that used to be on this page, with their old link targets kept here so older links still land:

- <a id="bridge-wire-protocol"></a>[Bridge wire protocol](bridge.md#bridge-wire-protocol)
- <a id="bridge-users"></a>[Bridge users](bridge.md#bridge-users)
- <a id="bridge-queue"></a>[Bridge queue](bridge.md#bridge-queue)
- <a id="main-thread-queue"></a>[Main-thread queue](bridge.md#main-thread-queue)
- <a id="logging-policy"></a>[Logging policy](bridge.md#logging-policy)
- <a id="ports"></a>[Ports](bridge.md#ports)
- <a id="token-file"></a>[Token file](bridge.md#token-file)
- <a id="loopback-only--never-ship-this"></a>[Loopback only — never ship this](bridge.md#loopback-only--never-ship-this)
- <a id="the-opt-in-marker--the-bridge-refuses-to-start-without-it"></a>[The opt-in marker — the bridge refuses to start without it](bridge.md#the-opt-in-marker--the-bridge-refuses-to-start-without-it)
- <a id="calling-without-mcp"></a>[Calling without MCP](bridge.md#calling-without-mcp)
- <a id="main-thread"></a>[Main thread](bridge.md#main-thread)
- <a id="ops"></a>[Ops](bridge-ops.md#ops)
- <a id="core-debug-tools-debug"></a>[Core debug tools (`.debug`)](bridge-ops.md#core-debug-tools-debug)
- <a id="sample-commands"></a>[Sample commands](bridge-ops.md#sample-commands)
- <a id="local-thunderstore-dev-only"></a>[Local Thunderstore (dev only)](test-rig.md#local-thunderstore-dev-only)
- <a id="recipe-auto-update-phase-2"></a>[Recipe: auto-update phase 2](test-rig.md#recipe-auto-update-phase-2)
- <a id="recipe-auto-update-phase-5-progenitor-itself"></a>[Recipe: auto-update phase 5 (Progenitor itself)](test-rig.md#recipe-auto-update-phase-5-progenitor-itself)
- <a id="mcp-tools"></a>[MCP tools](mcp.md#mcp-tools)
- <a id="mcp-examples"></a>[MCP examples](mcp.md#mcp-examples)
- <a id="registering-the-mcp-server-in-claude-code"></a>[Registering the MCP server in Claude Code](mcp.md#registering-the-mcp-server-in-claude-code)
- <a id="watching-the-client-screenshots-and-synthetic-input-toolsobserveps1"></a>[Watching the client: screenshots and synthetic input (`tools/observe.ps1`)](observe-tool.md#watching-the-client-screenshots-and-synthetic-input-toolsobserveps1)
- <a id="why-a-host-side-script-and-not-a-dev-bridge-op"></a>[Why a host-side script and not a Dev bridge op](observe-tool.md#why-a-host-side-script-and-not-a-dev-bridge-op)
- <a id="subcommands"></a>[Subcommands](observe-tool.md#subcommands)
- <a id="the-loop-this-exists-for"></a>[The loop this exists for](observe-tool.md#the-loop-this-exists-for)
- <a id="screenshot-method-and-the-black-png-problem"></a>[Screenshot method, and the black-PNG problem](observe-tool.md#screenshot-method-and-the-black-png-problem)
- <a id="the-locked-workstation-rule"></a>[The locked-workstation rule](observe-tool.md#the-locked-workstation-rule)
- <a id="the-queue-so-nothing-waits-on-a-human"></a>[The queue, so nothing waits on a human](observe-tool.md#the-queue-so-nothing-waits-on-a-human)
- <a id="exit-codes"></a>[Exit codes](observe-tool.md#exit-codes)
- <a id="files"></a>[Files](observe-tool.md#files)
- <a id="what-it-does-not-prove"></a>[What it does not prove](observe-tool.md#what-it-does-not-prove)
- <a id="extra-clients-and-headless-clients-experimental"></a>[Extra clients and headless clients (experimental)](test-rig.md#extra-clients-and-headless-clients-experimental)
- <a id="install-window-from-thunderstore-tofu-and-legacy-cleanup"></a>[Install window from Thunderstore, TOFU and legacy cleanup](test-rig.md#install-window-from-thunderstore-tofu-and-legacy-cleanup)
- <a id="unattended-verification-server-cases"></a>[Unattended verification (server cases)](test-rig.md#unattended-verification-server-cases)
