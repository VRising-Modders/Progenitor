# Exercise: timed buff

A standalone Progenitor plugin, originally written by a fresh agent from the public docs as an
authoring exercise. It defines one custom timed buff, **Adrenaline Rush**, visible on the action
bar, that cuts the wearer's blood drain to a tenth for a chosen number of seconds. It strips the
run-speed bonus the cloned `Buff_General_Haste` base would otherwise carry. Three chat
commands cover turn-on, check, and take-off-early. Unloading or removing the plugin leaves nobody
wearing the buff.

Contracts: [`docs/buffs.md`](../../docs/buffs.md). Signatures: [`docs/api.md`](../../docs/api.md)
"Buffs". The canonical example for the same task is
[`Progenitor.TimedBuffExample`](../Progenitor.TimedBuffExample).

## Status

| | |
|---|---|
| Source-checked | Against current `main`'s Core (`ctx.Buffs`, `ctx.Content.DefineBuff`, `ctx.Commands`, `ctx.Chat`). |
| Compile-verified | Debug and Release, as part of the buff API port onto `main` (2026-10-02, branch `api/buffs-port`). |
| Game-free tested | `BuffStatPolicyTests` pins the declared stats: blood drain `Multiply 0.1` with the single-modification `Active` scope, plus suppression of the movement speed the haste base would otherwise grant. |
| Verified live | **Live on `main` (2026-10-02, isolated test server and Steam client, PR #118 port, Core 2026.10.26):** apply (`Requested` then `Active`), a second apply refused `AlreadyActive`, the instance read back with `LifeTime.Duration` 30, `Age` ticking and the declared drain stat, expiry noticed by the API, early removal, a refresh shorter than the elapsed time (`.rested 30` at about 47.6 s elapsed set `Duration` 77.6 = elapsed + 30, and the buff stayed on), and a server Core hot swap with `.rested` and Adrenaline Rush both on: both removed before their plugins unloaded, both entities gone and no `BuffBuffer` row left, server on the same process. **Not measured on `main`:** the drain number itself (`Blood.LossPerSecond` read 0 for the idle test character even without buffs), the HUD icon and timer, chat reply wording, and a plugin-only swap. Earlier, on the pre-port buff branch, 2026-09-14, this plugin ran on the isolated pair beside `Progenitor.TimedBuffExample`; with `.rested` removed and Adrenaline Rush still on, the wearer's drain read 0.0005 = 0.005 x 0.1, one modification. That run predates the port. |

A DLL built before the port (for example the 2026-09-13 build) does not load against current Core:
it fails with `MissingMethodException` on `PluginContext.get_Buffs()`. Rebuild it from this source.

## Build

From the repository root:

```
dotnet build samples/Exercise.TimedBuff/Exercise.TimedBuff.csproj -c Debug
```

Output: `samples/Exercise.TimedBuff/bin/Debug/net6.0/Exercise.TimedBuff.dll`. It needs the
generated interop assemblies (`$(VRisingInteropDir)`), like every project here. The two `MSB3246`
"bad image" warnings come from Core's interop resolution, not from this plugin.

## Deploy

Deploy to **both** sides. The plugin declares
`[NetworkCompatibility(ServerMustHave, Exact)]`, which `ctx.Buffs.Stats` requires, so the server
refuses a client without it; the client copy registers the definition so the HUD can show the
buff's name, description and icon, and never applies. It is a reloadable plugin: no Bootstrap
change, no restart. Follow [`docs/hot-reload.md`](../../docs/hot-reload.md) for the deploy and its
completion checks (`tools/deploy.ps1 -Projects Exercise.TimedBuff`).

## Commands

| Command | Expected observation |
|---|---|
| `.adrenaline [seconds]` | Default 20 s, range 5–300. A reply that the buff was *asked for*, then a second message once the game confirms it is on, and the Adrenaline Rush icon on the action bar. Asking again while it is active is refused with a sentence; nothing is retimed. |
| `.adrenalinestatus` | This plugin's application state (`Requested`, `Active`, …) and buff entity id, or that there is none. |
| `.calmdown` | Removes this plugin's application early. Running it again, or after expiry, replies that there is nothing to remove and changes nothing. |

Bad input is refused with a sentence and changes nothing: `.adrenaline abc`, `.adrenaline 99999`.

## Acceptance procedure — not run on `main`

On the isolated test server with one connected character (never the live server):

1. Confirm the server log shows `Exercise timed buff: loaded, buff id 'exercise.adrenaline'`, and
   no `PrefabRegistry: refused edit from exercise.timedbuff` line.
2. Record the baseline `Blood.LossPerSecond` with no buff on.
3. `.adrenaline 30`: expect the two replies above and the icon. Drain is about a tenth of the
   baseline, not zero.
4. `.adrenalinestatus`: expect `state=Active` and a buff entity id.
5. `.adrenaline 30` again while active: expect the already-active refusal and no retime.
6. Let it expire: expect the "worn off" message, the icon gone, drain at baseline and no
   application in `.adrenalinestatus`.
7. `.adrenaline 60`, then `.calmdown`: expect immediate removal; `.calmdown` again replies that
   there is nothing to remove, with no error in the log.
8. With the buff active, hot-swap this plugin: expect the buff removed as part of the unload, no
   orphan buff entity on the character, and drain at baseline.

## Cleanup

Nothing to do by hand. `ctx.Buffs` tracks every application this plugin created and removes them
on every unload reason (hot swap, DLL removal, shutdown), and Core uninstall finishes them too.
Unload removes the buff silently: the `OnStateChanged` handler is not run, so no "worn off" chat
arrives for an unload.

The fresh agent's original authoring log (sources used, guesses and dead ends) is kept in the
internal research notes.
