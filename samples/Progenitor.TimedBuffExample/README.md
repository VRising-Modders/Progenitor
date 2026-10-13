# Timed buff example

A standalone plugin that defines one timed custom stat buff and applies it on the server. Two source
files and three commands; nothing here is shared with the big `Progenitor.Sample`.

Contracts and the supported stat-modifier list: [`docs/buffs.md`](../../docs/buffs.md). Signatures:
[`docs/api.md`](../../docs/api.md) "Buffs".

## Status

| | |
|---|---|
| Source-checked | Against current `main`'s Core (`ctx.Buffs`, `ctx.Content.DefineBuff`, `ctx.Commands`, `ctx.Chat`). |
| Compile-verified | Debug and Release, as part of the buff API port onto `main` (2026-10-02, branch `api/buffs-port`). |
| Game-free tested | `TimedBuffPolicy.cs` through `BuffStatPolicyTests` (declared stats, duration parsing and bounds, reply text). |
| Verified live | **Live on `main` (2026-10-02, isolated test server and Steam client, PR #118 port, Core 2026.10.26):** apply (`Requested` then `Active`), a second apply refused `AlreadyActive`, the instance read back with `LifeTime.Duration` 30, `Age` ticking and the declared drain stat, expiry noticed by the API, early removal, a refresh shorter than the elapsed time (`.rested 30` at about 47.6 s elapsed set `Duration` 77.6 = elapsed + 30, and the buff stayed on), and a server Core hot swap with `.rested` and Adrenaline Rush both on: both removed before their plugins unloaded, both entities gone and no `BuffBuffer` row left, server on the same process. **Not measured on `main`:** the drain number itself (`Blood.LossPerSecond` read 0 for the idle test character even without buffs), the HUD icon and timer, chat reply wording, and a plugin-only swap. Earlier, on the pre-port buff branch, 2026-09-14, on the isolated pair: apply and expire with drain restored to the recorded baseline, two modifications for `ActiveAndIdle`, movement untouched, `.unrested`, refresh shorter and longer than the elapsed time (`Duration` = elapsed + seconds), HUD seconds within one second of the server, refusal replies, and a plugin swap with a live buff that left the server running with no orphan. That run predates the port; `docs/buffs.md` "Later live procedure" is the sequence that would qualify it on `main`. |

## Build

```
dotnet build samples/Progenitor.TimedBuffExample/Progenitor.TimedBuffExample.csproj -c Debug
```

Output: `samples/Progenitor.TimedBuffExample/bin/Debug/net6.0/Progenitor.TimedBuffExample.dll`.
It needs the generated interop assemblies (`$(VRisingInteropDir)`), like every project here.

## Execution side

**Applies on the server; deploy to both sides.** The plugin declares
`[NetworkCompatibility(ServerMustHave, Exact)]`, which `ctx.Buffs.Stats` requires, so the server
refuses a client without it. The client copy registers the same `DefineBuff` — that is how the
client learns the clone's HUD name and description — and never applies; `ctx.Buffs.Apply` refuses
on the client with a sentence.

Deploy it beside Progenitor with `tools/deploy.ps1 -Projects Progenitor.TimedBuffExample`
([`docs/hot-reload.md`](../../docs/hot-reload.md)). It is a reloadable plugin: no Bootstrap change,
no restart.

## What it does

| File | What is in it |
|---|---|
| `TimedBuffPolicy.cs` | The plugin's own choices with no game in them: the id, the vanilla base, the stat, the durations it accepts, every sentence it replies with. The tests drive this file. |
| `Plugin.cs` | `OnLoad` — define the buff, put the stat on it, register the commands. Three command handlers. No teardown: `ctx.Buffs` tracks every application and the context removes them on unload. |

The buff clones `Buff_General_Haste`, which is **visible** (it carries `ShowOnBuffBar`) and
**timed** (it carries a `LifeTime` with an `EndAction` that already ends it). Both matter: the API
writes only `LifeTime.Duration` and never `EndAction`, so what happens at zero stays the vanilla
base's decision.

## Commands

| Command | Expected observation |
|---|---|
| `.rested [seconds]` | One reply saying the buff was **asked for** (not that it is on), then a second reply when it is confirmed. The **Rested** icon appears on the action bar; blood stops draining. Default 30 s, range 1–600. Running it again while it is on extends it — same instance, and the seconds you pass are the time left **from now** (the clock is not restarted). If that refresh is refused, the reply is the refusal sentence, not a success line. |
| `.unrested` | The icon disappears and blood drains again. Running it again replies that there is nothing to remove and changes nothing. |
| `.restedstatus` | The application's state (`Requested`, `Active`, …) and its buff entity id, or "no application". It reports what the API knows, and claims nothing else. |

Bad input is refused with a sentence and changes nothing: `.rested abc`, `.rested 0`,
`.rested 99999`.

## Cleanup

Nothing to do by hand. `.unrested` removes it; so does unloading the plugin, for any reason, and so
does Core uninstall. After any of those, `.restedstatus` reports no application and the character
carries no leftover modification — inspect it with the Dev bridge if you want to check rather than
trust.

Unload removes the buff **silently**: the `OnStateChanged` handler this example registers to chat the
player is not run, so the player gets the "requested"/"active" messages but no "ended" message when
the plugin is unloaded out from under them. That is deliberate — `ctx.UndoAll()` runs while this
assembly is being torn down, and `ctx.Chat.SendTo` from there would be a call out of a collectible
ALC mid-unload.

The one residual worth knowing: if you unload Core in the frame between `.rested` and the buff being
confirmed, the queued apply may still produce an instance that no Progenitor generation is watching
for. The log says so when it happens.
