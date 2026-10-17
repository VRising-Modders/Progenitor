# Glossary

Fourteen words that appear all over `docs/` and the logs, in the sense Progenitor uses them. Each row
names the document that owns the mechanism.

### generation

One loaded instance of `Progenitor.dll` (Core). Bootstrap loads Core into a **collectible**
`AssemblyLoadContext` named `progenitor-core:{n}` — first load is generation 1, and every
[Core swap](#core-swap) makes the next one. A `static` in Core lives for one generation and no
longer: `ctx.WorldState.Loads` restarts at zero, `WorldEpoch` takes a fresh generation stamp, and
anything that must cross a swap goes through Bootstrap (`ContentMemory`, the Harmony slots, the
unload-reason slot). Bootstrap itself is *not* a generation — it is a normal BepInEx chainloader
plugin that lives for the process. See [modules.md → Hot reload](modules.md#hot-reload).

### Core swap

Replacing the live Core generation with a new one inside a running process: the staged
`Progenitor.dll.next` is promoted onto `Progenitor.dll`, the old generation is uninstalled and its
ALC unloaded, the new one is installed, and every reload-folder plugin is unloaded and loaded again.
A plugin sees this as an unload with [reason](#unload-reason) `Swap`. A **Bootstrap** change is not
swappable and needs both processes restarted once with the new Core already on disk.
See [dev-loop.md](dev-loop.md), [bridge.md](bridge.md) and [modules.md → Hot reload](modules.md#hot-reload).

### reload folder

`BepInEx/progenitor-reload/`. The folder both reloaders scan; a `*.dll` that settles there is loaded
into its own collectible ALC and unloaded again when the file changes or disappears. This — not
`BepInEx/plugins/` — is where a Progenitor plugin goes. Progenitor itself is one folder,
`BepInEx/plugins/Progenitor/`: `Progenitor.Bootstrap.dll`, and Core as `progenitor-core/Progenitor.core`.

### allowlist

`BepInEx/config/progenitor-allow.txt`, optional and off by default. When it exists, only the exact
`sha256  filename` rows it lists may load, and **eight** automatic code-loading paths ask it first:
the two reloaders, the Core load, the `.next` watch, the staged-Core promote, the staged-Bootstrap
promote, the [pending folder](#pending-folder) promote, and a method-body [delta](#delta). Anything
else is quarantined and never reaches `LoadFromStream`. Every load line carries
`allowlist=none|ok|unlisted|malformed`; a file that exists but does not parse fails **closed**. The
[drift map](#drift-map) is the one input that needs a row even when there is no allowlist at all.

### pending folder

`BepInEx/progenitor-pending/<host>/`. Where a package a player accepted **from a server** is
written. No reloader scans it, so nothing a server sent can run in the session that downloaded it;
Bootstrap promotes it into the [reload folder](#reload-folder) at the next process start, re-hashing
it and putting it through the [allowlist](#allowlist) like any other plugin. See
[mod-manifest.md](mod-manifest.md).

### drift map

`driftmap-{build}.json` beside the address tables in `BepInEx/progenitor-core/`. A per-game-build
data file of `from → to` rules (`typeRename`, `typeMove`, `memberRename`, `fieldToProperty`,
`propertyToField`, `addParam`) that the compatibility rewriter uses to retarget a plugin's metadata
references after a V Rising patch. Only `exact` and `high` confidence rules are ever applied, only
under `[Compat] RewriteMode = Apply`, and only when the map has its own allowlist row. Never edits a
numeric operand: a stride copied out of the game's metadata stays the plugin author's problem.
See [modules.md → Compatibility rewriter](modules.md#compatibility-rewriter).

### delta

One `<plugin>.<n>.delta` file in the [reload folder](#reload-folder): an ASCII header (magic
`PRGDELTA1`, plugin, baseline sha256, sequence, changed method tokens) plus the `.dmeta`/`.dil`/
`.dpdb` triple, written by `tools/enc --watch`. It replaces **method bodies in place** through
`MetadataUpdater.ApplyUpdate` — no unload, no `Load()`, no re-registration — which is both the win
and the limit. Applied only when the process has `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, the baseline
hash is the loaded image, the sequence is the next one expected, and the delta itself is
allowlisted. Deltas do not survive a [Core swap](#core-swap).

### world epoch

`ctx.WorldState.LoadEpoch`: an opaque token that changes once per completed world load on the server
and once per client-world create/end on the client. Stamp it next to any `Entity` a plugin keeps
past the current frame and drop the id when `ctx.WorldState.IsStale(stamped)` — a save reload
rebuilds every entity from index 0 at version 1, so `EntityManager.Exists` on a retained
`index:version` can answer true while naming a different entity. The token is generation-stamped, so
a stamp from an older Core [generation](#generation) also reads stale. Never do arithmetic on it.
See [modules.md → World](modules.md#world).

### unload reason

`PluginUnloadReason`, the argument to `OnUnload(TContext ctx, PluginUnloadReason reason)`:
`Swap` (a [Core swap](#core-swap); the same DLL comes back within a frame or two),
`Removal` (the DLL is gone or its replacement will not load; nothing is coming back),
`Shutdown` (the process is exiting) and `Unknown` (nothing established a reason). Only `Removal` is
a reason to sweep live world state; the other three **keep** it, because a kept buff is recoverable
and a swept one is not. See [modules.md → Unload reason](modules.md#unload-reason-fbuff2).

### capture mode

`UiCaptureMode`: *when* a shown mod panel blocks gameplay input, as opposed to whether it ever does.
`WhenHovered` is the framework default — the block follows the pointer, so over a panel the wheel
scrolls the list and a click hits a row, and everywhere else both go to the game. `WhileShown` blocks
for as long as any panel is up, wherever the pointer is, and is for a genuinely modal panel.
`Never` never blocks. Four layers pick it, most specific first: `IUiPanel.CaptureMode` → the
plugin's default (`Ui.CaptureInput(mode, owner/pluginId)`) → the framework default
(`Ui.CaptureInput(mode)`) → the operator's `[Ui] CaptureMode` entry in the client-only
`progenitor.ui.cfg`. A mode applies to the **next** `Show`; an open panel keeps the mode it
was shown with. Distinct from `Ui.CaptureInput(bool)`, which is the master switch and beats every
mode. Not the same as **cursor unlock**, which is never routed by the pointer — a locked cursor
could not be moved onto the panel to begin with. See [ui.md](ui.md) and
[api.md → Ui](api.md#ui).

### press edge

The moment a key goes down, as Progenitor defines it — **derived from a held state the framework
owns**, not read from the engine. `ButtonControl.wasPressedThisFrame` is `pressedStepCount ==
updateStepCount`, true only during the input update step that processed the press, while Progenitor
samples once per rendered frame; an update step that advanced twice between two samples hides the
press completely, which is why every mod keybind once needed two presses. `KeyEdgeTracker` trusts
the engine's flag whenever it *is* set and otherwise counts a key seen up on the **immediately
preceding** sample and down now — the contiguity requirement being the load-bearing part, since the
pump skips frames where every bind is suppressed and a key merely still held must not read as fresh
after a gap. The standing rule: a frame-scoped flag from another subsystem is never an input edge
unless you sample on that subsystem's cadence. Internal machinery; a plugin just registers through
`ctx.Keybinds`. See [modules.md](modules.md) ("Keybinds") and research evidence 161.

### host key

The local, sanitized name for the server a client is talking to, used as a path segment under
`progenitor-pending/` (and, before 2026-10-05, `BepInEx/progenitor-staging/`) and printed on
mod-manifest log lines. Derived **locally** — the signed operator fingerprint of the join truncated
to 16 hex, else the connect address (the server's `address:port` since HOSTKEY1), else `unknown` — then reduced to `[0-9a-z._-]`, capped
at 40 characters, and refused if it is a reserved device name, so a server can never introduce a path
segment of its own. A key the sanitizer had to change or truncate carries an 8-hex digest of the
original so two different hosts cannot collapse onto one. See [mod-manifest.md](mod-manifest.md).

### session marker

The `.progenitor-session` file an older Core dropped beside a server's package a player accepted for
**one join only**. Bootstrap's start-time promote still deletes a package that carries one rather
than moving it, and since 2026-10-05 the client's legacy cleanup deletes the markers it finds beside
recorded server packages. See [mod-manifest.md](mod-manifest.md#legacy-cleanup-at-core-start).

### trust on first use (TOFU)

How a client pins a server's operator key since 2026-10-05: the first verified catalog from an
address with no row in `progenitor-trusted-operators.json` pins that address's key. A later catalog
signed by another key for the address is flagged as a key change and never re-pinned. The pin
installs nothing. See [mod-manifest.md](mod-manifest.md#trust-on-first-use-2026-10-05).

### install window

The time (default 60 s, `[Networking] InstallGraceSeconds`) a server holds a refused join open
when every failing row can be fixed on the client during the join: a Thunderstore package that serves
the needed version, a kept copy, a copy turned off in the Mods list, or (client mod policy) a pause.
The client fixes it from the join screens and answers the handshake again. See
[mod-manifest.md](mod-manifest.md#phase-5--the-deferred-kick-and-the-install-window-job-fman18-server-half).

### offered mod (removed 2026-10-05)

A mod the server held a copy of and would send to a client that consented (`OfferPackage` /
`ModManifest.Offer`). Servers no longer send mod files; both calls are obsolete no-ops. A mod
joining players need comes from Thunderstore: name its package in the catalog. See
[client-mod-policy.md](client-mod-policy.md#security).
