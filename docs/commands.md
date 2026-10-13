# Commands

Authoritative routing contract for `ctx.Commands`. Attribute discovery, argument types and admin gating remain summarized in [modules.md](modules.md#commands).

Chat lines that start with `CommandRegistry.Prefix` (default `.`) are commands. Every registered command has a stable owning plugin id (the BepInEx GUID, not the display title) and a qualified route. Root shortcuts are requested by the command name (and optional `shortHand`) and can be disabled, renamed or assigned by the administrator.

## Register

```csharp
ctx.Commands.Register(typeof(MyCommands));
ctx.Commands.Register<MyCommands>();          // instance class, not a C# static class
ctx.Commands.Register(new MyCommands(ctx));   // supply the instance
```

`[Command("ember")]` requests the short route `.ember` and always installs `.p <ownerId> ember`. `[CommandGroup("sample")]` plus `[Command("echo")]` is `.sample echo` and `.p <ownerId> sample echo`. Dispose or plugin unload drops only that lifetime's commands.

`ctx.Commands` infers the owner id from `[BepInPlugin].GUID` on the plugin instance (`ctx.Owner`). Pass an explicit owner id only when calling `CommandRegistry.RegisterType` / `RegisterInstance` / `RegisterAssembly` directly. Two live registrations that share an owner id and the same command path collapse like a Swap (last handler wins; the older handle cannot delete the replacement). Distinct paths under a shared id both stay.

## Shortcuts, conflicts and the qualified fallback

Dispatch is case-insensitive on tokens (`OrdinalIgnoreCase`, the same comparer used to build the table). Distinct literal subcommands may share a group root. An explicit literal subcommand is chosen over a generic argument route on the same first token. A trailing separator (`ember` vs `ember.`) is a different token at both collision and dispatch. Quoted spans are one argument.

When two owners request the same short token, neither wins by load order. The short route is disabled for every competing owner, one diagnostic reply names the owners and qualified routes, and every qualified route keeps working. If the primary name is disabled but an alias is unique, the alias stays as the published shortcut. A duplicate qualified identity (same owner id and command path claimed as a replacement of a different owner) is a registration error; the previous valid table is kept.

The qualified root defaults to `p` and is reserved in Progenitor's registry: `.p <ownerId> <command> [args]`. No mod can keep that token as a shortcut. The reserved token is taken from the current settings when the table is built and when a line is dispatched; they stay in lockstep because an apply replaces both together. Blank or whitespace `FallbackRoot` is the default `p`. A value with a space is rejected and the previous table stays. Change the root when `p` is already used on the deployment; the previous reserved token is then released.

Help lists the effective short route when it is enabled, and always names the fallback. `.help` on a disabled conflicting shortcut prints the same single conflict line as typing the shortcut.

## Administrator configuration

Keys live in the shared `progenitor.cfg` under `[Commands]`. They are strings, so other sections are not rewritten. The file is process-lifetime BepInEx config: a Core swap re-reads it on install, so an administrator remap survives. Invalid or conflicting mappings are rejected with a log line and the previous routing table stays active. One bad line rejects the whole change.

| Key | Meaning |
|---|---|
| `FallbackRoot` | Single token for qualified routes. Default `p`. |
| `Assign` | One mapping per line or semicolon: `token = ownerId`. Gives that short token to one owner. |
| `Rename` | `ownerId:name = newToken` or `ownerId:group/name = newToken`. Changes the short route only. |
| `Disable` | `ownerId` or `ownerId:name` (or `ownerId:group/name`). Drops short routes; qualified routes stay. |

Example:

```
[Commands]
FallbackRoot = p
Assign = ember = progenitor.sample
Rename = progenitor.sample:joins = who
Disable = other.mod
```

## Example

`samples/Progenitor.Sample/SampleRouting.cs`, registered from `Plugin.cs` on the server with `SampleRouting.Attach(ctx)`. Two owner ids on the Sample's lifetime:

| Type in chat | What it demonstrates | Expected single reply |
|---|---|---|
| `.spark` | Unique shortcut | `spark from alpha` |
| `.p progenitor.sample.alpha spark` | Qualified route, same handler | `spark from alpha` |
| `.route iron` / `.route steel` | Shared group, distinct literals | `route iron from alpha` / `route steel from beta` |
| `.flare` | Intentional conflict | `Ambiguous command '.flare' is disabled. Owners: progenitor.sample.alpha, progenitor.sample.beta. Use .p progenitor.sample.alpha flare or .p progenitor.sample.beta flare.` |
| `.p progenitor.sample.alpha flare` | Qualified still works | `flare from alpha` |

## Execution

Short and qualified forms call the same handler, parser and `adminOnly` check. A denied or invalid invocation of an owned command replies once (`This command is admin-only.` / usage) and does not fall through (`CommandResult.Denied` or `Executed`; the chat hook cancels). An unowned prefixed line stays `Unmatched` for other chat handlers. Ordinary chat (no prefix) is `NotACommand`. Commands run on the server process with server-side authorization. `ICommandContext.IsAdmin` is the game's `User.IsAdmin`, or true for the player hosting a private game (Progenitor's own rule; the game's flag is unchanged; see [settings-scopes.md](settings-scopes.md#the-private-game-host-is-a-progenitor-admin-2026-10-06)). Client-local command registration is unsupported in this increment: the client process returns `NotACommand` and does not subscribe to chat.

## `.progenitor logs` (server logs)

Built-in, owner `progenitor`, the one subcommand of `.progenitor` (`.prog logs`). Admin only by the server's admin-list rule (`[AdminListGate]`: on the admin list, `adminauth`, or the host of a private game); `.help` and the chat hints show it only to them, and anyone else gets the plain `.progenitor` usage. It builds the server's support bundle, the same zip the client's **Collect logs** makes ([mods-menu.md](mods-menu.md#get-help-collect-logs-support1)), off the main thread, and replies twice:

```
Collecting this server's logs...
Saved vrising-mod-logs-server-20261010-142233.zip (1.2 MB) in BepInEx/progenitor-support on the server. Its personal details are removed; review it before posting.
```

The folder is given relative to the server's folder, never as an absolute path. The bundle holds the server's `BepInEx/LogOutput.log`, its per-session copies (`BepInEx/progenitor-logs`), the Unity log named by the server's `-logFile` argument (the game's `start_server_example.bat` passes `-logFile ".\logs\VRisingServer.log"`) or, without one, `Player*.log` in `%USERPROFILE%\AppData\LocalLow\Stunlock Studios\VRisingServer`, Progenitor's audit logs (`progenitor-settings.log`, `progenitor-remote-install.log`, `progenitor-auto-update.log`) and Progenitor's own cfg and state files, redacted as described in [mods-menu.md](mods-menu.md#get-help-collect-logs-support1) (the game's `overridden by Command Line Parameter '-password'` lines and `-password` / `-rconPassword` arguments read `[hidden]`). `ServerHostSettings.json`, `ServerGameSettings.json`, the admin and ban lists, saves and tokens are never collected. A second `.progenitor logs` while one is running replies `Already collecting logs.` The operator copies the zip off the server; nothing is uploaded. An admin who is in the game can instead let **Collect logs** fetch the same bundle over `'PrSL'` into their own zip under `server/` ([server logs for admins](mods-menu.md#get-help-collect-logs-support1)).

Status: source-checked, built and game-free tested (`SupportBundleTests`); the command itself is not live-verified.

## Lifetime

Registrations belong to the plugin lifetime. Unload disposes that lifetime's handles and `UnregisterOwner` drops only that owner object: commands, aliases and qualified routes all go. A swap may register the replacement before the old handle is disposed; the old handle cannot delete the new rows. Conflicts are recomputed after every change, so a disabled shortcut can return when its competitor disappears, and help/diagnostics follow the new table.

## Verified and open

**Compile-verified / offline-tested:** routing, conflicts (including reversed order and case-only token differences), administrator mappings, invalid-config rollback, quoted arguments, trailing-separator tokens, permission denial (one reply, no handler), unload/swap, alias cleanup, and conflict removal. Filter: `FullyQualifiedName~Command`. Independently reviewed offline (internal review R87).

**Not verified live. Not in a candidate.** This increment is offline-tested only. Coexistence with another chat-command framework is unverified. Cross-framework collision checks are **unavailable**, not a clean result. Discovering another framework's commands is out of scope. Hosting-panel/RCON is not part of this contract. Live gate: exactly-once command/help/conflict checks with another chat-command framework installed.

The executable live checklist (exact commands, expected single replies, and the blocker until another chat-command framework is installed) is kept in the internal research notes.
