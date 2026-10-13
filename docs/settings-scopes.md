# Setting scopes: Personal and Server-wide

**Status: design; steps 2 to 4 implemented.** Steps 2 (the model), 3 (admin-list authorization, rate limit, audit; live-verified, see [mods-menu.md](mods-menu.md#settings-rows)) and 4 (the page, [mod-settings.md](mod-settings.md#settings-scopes-on-the-page-step-4); source-checked and game-free tested, **not live-verified**) are in code. Everything else on this page is design, not implemented, unless a row says "today". It extends the mod page settings of [mod-settings.md](mod-settings.md) (SETTINGS1) and the admin-synced config of [config-sync.md](config-sync.md) (M2.5). Inventory references were source-checked against `origin/main` at `978af824` on 2026-10-03. Nothing here was built or observed live.

Where Progenitor's **own** settings are drawn is specified in [settings-organization.md](settings-organization.md#three-homes-2026-10-06) (three homes, 2026-10-06: **Preferences** for how the Mods window works on this PC, a mod's page in **Mods** for its own settings, the **Server** tab for how the joined server is set up; built, game-free tested, not live-verified). Progenitor's page keeps only its own two server toggles, under **On this server**. Progenitor's own Server settings that live in the server's files (the answer deadline, photo mode and ally-safe hits on the server, remote installs and their admin list, the notice timing) are edited over the admin channel `'PrJa'`, not as ConfigSync rows. They follow the same rules: the admin list or `adminauth` authorizes, 10 edits per minute, and one audit line per edit or refusal. They are hidden from a player who is not an admin, rather than shown `Admin only` (decision 5 there).

## Goals

- One settings list per mod that answers the player's question "who does this affect?", not "which DLL owns this?".
- Server-only mods can offer per-player preferences that each player changes from their own client.
- Admins change server-wide settings from the client. Non-admins can read them.
- The framework grays a setting whose owning half is absent or unreachable, using standard reasons. Mod authors add their own conditions.
- Existing mods keep working unchanged and land in the right section by default.
- Hot reload keeps working: no pinned generations and no IL2CPP-side registrations.

## Decisions (2026-10-03)

1. **No client/server split on the page.** Several server-only mods have settings that each player should adjust for themselves. Code side is the wrong axis.
2. **Each setting declares a scope.**
   - **Personal:** the player changes it for themselves.
   - **Server-wide:** it affects everyone on the server. Only admins can change it; others can read it.
3. **Storage follows from scope plus the owning half.** The UI does not show where a value is stored.

   | Scope | Owning half | Stored in | Edited by |
   |---|---|---|---|
   | Personal | client | the player's own `GUID.cfg` (today's `ctx.Config` / `ctx.Bind`) | the player, locally |
   | Personal | server | the server, per player, keyed by a stable player identity (platform id, never an entity id) | the player, from their client over the network |
   | Server-wide | server | the server's `GUID.cfg`, synced to clients (today's `ctx.Bind(..., synced: true)` / ConfigSync) | admins, from the client over the network |

4. **Page model.** One list with two sections, **Your settings** (Personal) and **Server settings** (Server-wide). Admins can edit Server settings. Other players see them read-only with an **Admin only** note. The section carries one line: `Applies to everyone on <server name>`. Omit an empty section. Do not use tabs.
5. **One half absent.** When the owning half is not installed or cannot be reached, the **framework** grays the setting with a standard reason (`Needs the server part of this mod`, `Not connected to a server`, `Server doesn't run Progenitor`, …). **Mod authors** add an optional "enabled when" rule and may hide settings that make no sense alone.
6. **Admin authorization matches remote install.** Membership in the server's admin list authorizes, with no `adminauth` required. `User.IsAdmin` (adminauth) also authorizes. The host of a private game also authorizes (below).

### The private-game host is a Progenitor admin (2026-10-06)

Decided: the player hosting a private game is an admin for Progenitor's features. In the vanilla game they are not: a private-game host is admin only through the admin list plus `adminauth`, or `everyoneIsAdmin`.

- **How the host is identified.** When a client hosts a private game, the game itself starts `VRisingServer.exe` with `-owner <SteamID> -hostServer` (the game's own argument template; a hosted child's command line was observed live). `ServerAdminList.HostSteamId` reads `-owner` from the server process's own command line, only when `-hostServer` is also present, once per process. A dedicated server is not started with `-hostServer`, so it has no host. Progenitor adds nothing to the launch: the SteamID is the one the game already uses for that save, so a first-ever session works and nothing can drift between the launcher and the child.
- **Where it applies.** Every Progenitor admin decision on the server: `ServerAdminList.AuthorizeShared` (Server-tab and Progenitor server settings, `server_rules` writes, the join-admin channel and its hello, the stop-server channel since F21, the map teleport and its client state since 2026-10-08, and every `.debug` command form since 2026-10-09) and remote installs return `RemoteAuthorizedBy.Host` (audit word `host`) for the host when the admin list and `User.IsAdmin` do not already authorize. `ServerAdminList.IsProgenitorAdmin(platformId, isAdmin)` gates the paths that followed `User.IsAdmin` alone: `ICommandContext.IsAdmin` (so `adminOnly` commands and their `.help` visibility) and photo-mode pause.
- **What it does not do.** The game's own `User.IsAdmin` is never set and `adminauth` is never sent. Vanilla admin console commands still need the game's own admin rules.
- **Wire.** `Host` is sent as `AdminAuth` in the remote-install status, because older clients refuse a byte past `AdminAuth`. A client therefore shows the host "you're an admin there".

Built and game-free tested (`ServerAdminListHostTests`); not live-verified. A live check is in the final report of the change that added it and in [the recipe below](#verifying-the-private-game-host).

## What exists today (inventory)

Paths are relative to the repository root. Line numbers are at `978af824`.

### Binding and storage

| Piece | Today | Evidence |
|---|---|---|
| `ctx.Config` (`PluginConfig.Bind`) | Plain bind on the plugin's `GUID.cfg`. It refuses Bootstrap's shared `progenitor.cfg` and **throws** on `synced: true` because it cannot track the handle. | `src/Progenitor.Core/Plugins/PluginContext.cs:607-680` |
| `ctx.Bind(..., synced, acceptableValues)` | The same bind. With `synced: true` it checks the BCL type gate and tracks `ConfigSync.Register(Guid, Version, entry)`, so unload unregisters it. | `PluginContext.cs:109-149` (gate at 120/144, register at 123/147) |
| Synced type gate | Allows only BCL primitives, `string` and BCL enums, the same gate as the shared cfg: values must round-trip as Toml and must not pin a collectible generation. | `src/Progenitor.Core/Networking/ConfigSync.cs:641-651`; `Config/ConfigHelper.cs:922` (`IsAllowedBclBindType`) |
| ConfigSync wire | `'PrcS'` (`0x50726353`). After an ok handshake the server sends every synced entry of every plugin the client **also reported at a compatible version**. Later changes go out as deltas. The client locks the entry until disconnect, and the server value never reaches the client's cfg on disk. A record must fit a 15 KiB packet. | `ConfigSync.cs:53`, `ConfigSync.cs:808-915`; [config-sync.md](config-sync.md#join); `Networking/CustomEvents.cs:95` (`MaxPayloadBytes`) |
| Bounds | `AcceptableValue*` metadata stays local and does not travel on the wire. A synced value outside the client's bounds is clamped with a log line. | `PluginContext.cs:94-108` (doc) |
| Admin edits of synced values | `'PrSe'` (`0x50725365`), `ServerSettings.Register` / `Request`. Edits are opt-in per entry (`ctx.Menu.AllowServerEdits` or `scope: SettingScope.Server` on a page row). The server checks handshake ok, the peer's snapshot rows, registration, type, the expected previous value, parse and bounds (failing closed). It applies by writing `ConfigEntry.BoxedValue`, which then rides ConfigSync. The request times out after 8 s; a client may have at most 32 pending. | `Networking/ServerSettings.cs:30-31`, `:64`, `:120-145`, `:290-306`; `Ui/PluginMenu.cs:27`; `Ui/ModSettingsPage.cs:536`, `:547-575` |
| **Admin check for those edits** | **`User.IsAdmin` only**, which only `adminauth` sets. It does not read the admin list. This does not match the remote-install decision. | `ServerSettings.cs:305`; `Install/AdminAuthFlow.cs:30-36` says so |
| Per-world mod store | `ctx.Save` / `IModStore`: per-mod JSON in `<save>/progenitor/<guid>.json` beside the world save. It loads on `WorldReady` and flushes on `TriggerSave` and on disconnect. It is server only; on the client it is a no-op. It has no per-player structure. | `Persistence/IModStore.cs`, `Persistence/ModSave.cs:19-31`, `Persistence/SavePaths.cs:33-37` |
| Stable player identity | `PlayerAccountId` wraps `User.PlatformId`. `PlayerDirectory.TryGetByAccount(platformId, …)` looks players up, including offline ones. | `Players/PlayerAccountId.cs:12-15`; `Players/PlayerDirectory.cs:192-204` |
| Per-player preferences today | Only ad hoc. `Progenitor.SampleServerLed` keeps client-cfg values. The client part pushes them over the mod's own event, and the server holds them **in memory, keyed by `User.Index`** (recycled). Its "For server" section shows them as local client settings. | `samples/Progenitor.SampleServerLed/ServerLedPolicy.cs:295-320`; `ServerLedSettings.cs:125-145` |

### UI

| Piece | Today | Evidence |
|---|---|---|
| Scope marker | `SettingScope { Local = 0, Server = 1 }`: where the value lives. The page builder's `scope:` option adds a help-line marker (`Server-set.` / `Local to this client.`). `Server` on a synced entry also opts it into admin edits. | `Ui/SettingDescriptor.cs:68-75`; `Ui/ModSettingsLayout.cs:2000-2001`, `:2056-2057` |
| Edit path | `SettingsRow.EditPath`: `LocalConfig` for a plain row, `None` for a foreign row or a synced row without an admin route, and `ServerRequest` when `ServerSettings.CanRequest`. `Scope` is derived from "is synced now". | `Ui/SettingsRow.cs:217-232`; `Ui/SettingDescriptor.cs:78-91` |
| Control choice | `ModSettingsLayout.ControlFor`: a `ServerRequest` row is a field plus **Apply**, and a read-only row shows `Locked · <value>` with a reason. | `Ui/ModSettingsLayout.cs:1083-1090`; [mod-settings.md](mod-settings.md#which-control-a-row-gets) |
| Page builder | `ctx.Settings.Page(...)` takes author sections and `showIf` / `enableIf` (`Func<bool>`, Core-held, re-evaluated after each commit), `needsServerPart` + `fallback`, `needsRestart`, `scope`. It is tracked and dropped on every unload reason. | `Ui/PluginSettings.cs:39-44`; `Ui/ModSettingsPage.cs:470-575`; [mod-settings.md](mod-settings.md#the-settings-page-builder-stage-2) |
| Writes | `SettingsCatalog.Submit`: parse, bounds (a refusal, not a clamp), binding policy, synced refusal. | `Ui/SettingsCatalog.Bindings.cs:222` |
| Server-part evidence | `ServerPartForSettings`: a paused mod means the part is absent. A mod named in the server's handshake request is running. A mod known absent (from a non-full request) is absent. **Anything else is unknown and grays the row.** | `Ui/ModSettingsLayout.cs:1976-1995` |
| Settings pane | The separate global list (`SettingsPanel`, `mods_ui select page=settings`) uses the same row kinds and the same admin path. | `Ui/SettingsPanel.cs`; [mods-menu.md](mods-menu.md#settings-rows) |
| Configuration tab | Progenitor's own options on the shared `progenitor.cfg` (`[Mods]`). Some are already commented "Personal" (listing filters, source toggles). These are framework settings, not mod settings. | `Ui/ConfigurationPane.cs:16-46` |
| Keybinds | Every `ctx.Keybinds.Register` is a cfg entry in the plugin's own `GUID.cfg` (Core's own keys use `progenitor.ui.cfg`). Keybinds are always client-local. Each one is a row in the page's **Keys** section and in the Keybinds tab. | `Ui/PluginKeybinds.cs:26`, `:48-59`; `Ui/UiKeybindHost.cs:28` |

### What the client knows about the server

| Evidence | Today | Evidence |
|---|---|---|
| Handshake request | The server's request lists its non-ClientOnly plugin rows in at most **16** wire slots. A full request proves no absence (`ServerRequestTruncated`). | `Networking/Handshake.cs:63`, `:2619-2645` |
| Capabilities | Client capability flags `InstallWindow = 1`, `RemoteInstall = 2`. The server sends a channel's messages only to clients that declared it. | `Handshake.cs:169-192` |
| Signed catalog | `'PrmM'`: after the ok handshake and the config snapshot, the server sends its signed **required-mod** catalog and offers. It carries packages, not settings. | `Networking/ModManifest.cs:35-55`; `Handshake.cs:2165-2185` (wire order) |
| Server name | The remote-install wire carries no server name. A remote server is named by its address. | `Install/ServerIdentity.cs:13-14` |
| Route words | `ServerRouteBlock`: `NoServer`, `NotProgenitor`, `RemoteOff`, `NotAdmin`, `NotAllowed`, `Unreachable`, `Pending`. | `Install/ServerRoute.cs:19-43` |

### Remote install, the model for admin requests

- **Identity and privilege come only from the connection.** The server re-reads `User` from the sender entity and takes `PlatformId`, `IsAdmin` and `CharacterName` from it. Nothing about identity travels in the payload (`Install/RemoteInstallEvents.cs:17-38`).
- **Authorization:** `ServerAdminList.Authorize(membership, isAdmin)` returns `AdminList` for a member of the game's admin list (re-read when the file changes), `AdminAuth` when `User.IsAdmin`, otherwise `None`. If the list cannot be read, the server falls back to `IsAdmin` (`Install/ServerAdminList.cs:21-28`, `:90-94`, `:178-181`; `RemoteInstallEvents.cs:320-331`).
- **Order of checks:** admin, enabled, allow-list (`[RemoteInstall] AllowedSteamIds`), syntax, rate limit (`RemoteInstallRateLimiter`: 5 operations per 60 s per SteamID), busy (`Install/RemoteInstallPolicy.cs:117-130`, `:195-217`).
- **Audit:** `BepInEx/progenitor-remote-install.log`, rolled at 1 MiB to `.1` (`Install/RemoteInstallService.cs:1063-1090`).
- **Reserved ids:** `0x5072____` is Core-only, so a plugin cannot forge these requests (`Networking/CustomEventPolicy.cs:137`, `:167`).

### Hot-reload pin rules that apply

- Core never subscribes `SettingChanged` directly on a file it does not own. It uses `ConfigHelper.AddSettingChanged`, which unsubscribes on uninstall. `SettingChangedPinTests` enforces this after the 2026-10-01 pin, where a `RemoteInstallEvents` handler on Bootstrap's shared cfg refused every client Core swap (`Config/ConfigHelper.cs:496-506`; `tests/Progenitor.Tests/SettingChangedPinTests.cs:8-23`).
- The settings page holds plugin delegates in Core only, and a tracked registration drops them on every unload reason ([mod-settings.md](mod-settings.md#the-settings-page-builder-stage-2), Hot reload).
- Synced entries are BCL-typed so a `ConfigEntry<T>` cannot pin a plugin generation.

## Proposed public API

### Principle: extend, don't fork

The two `ConfigEntry`-backed rows of the decision table (Personal client-owned and Server-wide) already exist as `ctx.Bind`. Only Personal server-owned is new, and it **cannot** be a `ConfigEntry<T>`: it holds one value per player. The design therefore:

- **Extends `ctx.Bind` with a `scope` parameter.** `synced: true` stays and means `scope: ServerWide`, so no existing call changes meaning.
- **Adds one new handle type, `PlayerSetting<T>`, through `ctx.Settings.PerPlayer`.** A parallel `ctx.Settings.Personal` / `ServerWide` pair would duplicate `ctx.Bind` for two of the three cases and give authors two ways to declare the same cfg entry.
- **Renames the enum's meaning, not its values.** `SettingScope` gains `Personal = 0` and `ServerWide = 1` as the documented names. `Local` and `Server` stay as aliases of the same values (later `[Obsolete]`), so compiled plugins and `scope: SettingScope.Server` page rows keep working.

### Declarations

```csharp
// Personal, client-owned (unchanged; the default on the client).
var hints = ctx.Bind("Menu", "ShowHints", true, "Show the help text.");

// Server-wide, server-owned: synced to clients, admins edit it from the client.
var minutes = ctx.Bind("Announce", "Minutes", 10, "How often the server announces.",
    scope: SettingScope.ServerWide, acceptableValues: new AcceptableValueRange<int>(1, 120));
// Same as today's synced: true, plus admin editing (decision 2).

// Personal, server-owned: one value per player, kept by the server.
var receive = ctx.Settings.PerPlayer("Announce", "Receive", true, "Receive announcements.");
var delivery = ctx.Settings.PerPlayer("Announce", "Delivery", "Chat", "How announcements reach you.",
    acceptableValues: new AcceptableValueList<string>("Chat", "Popup"),
    enabledWhen: When.On(receive));
```

| Member | Contract |
|---|---|
| `ctx.Bind<T>(section, key, default, description, synced = false, acceptableValues = null, SettingScope? scope = null)` | `scope: Personal` is the plain bind; on the server it is refused with `ArgumentException` ("a server bind is Server-wide; use `ctx.Settings.PerPlayer` for a per-player value"). `scope: ServerWide` is `synced: true` plus admin editing. `synced: true` with `scope: Personal` throws. Omitted: today's behavior (see [Mapping existing mods](#mapping-existing-mods)). The type gate is unchanged. |
| `ctx.Settings.PerPlayer<T>(section, key, default, description, acceptableValues = null, enabledWhen = null, visibleWhen = null, validate = null)` → `PlayerSetting<T>` | Tracked, released on every unload reason. `T` passes the same BCL gate. Registered under `(guid, section, key)`. The key namespace is shared with the plugin's cfg keys, and a collision throws. Callable on **both** halves of a two-sided mod, so shared code needs no side check. |
| `PlayerSetting<T>.For(PlayerAccountId)` / `For(Entity user)` (server) | The player's value, or the default when the player never changed it. Never throws for an unknown player. |
| `PlayerSetting<T>.Mine` (client) | This player's value as last received from the server while joined, else the default. Read-only on the client. Only the page writes, through the wire. |
| `PlayerSetting<T>.Changed` (`Action<PlayerAccountId, T>`) | Raised on the main thread after an accepted write: on the server for any player, and on the client for this player. The subscription is tracked. |
| `PlayerSetting<T>.Set(PlayerAccountId, T)` (server) | For a mod's own chat command or migration. It is validated like a network write and is not audited as an admin write. |
| `validate:` (`Func<T, string?>`) | Optional server-side check after parse and bounds. Return null to accept or a sentence to refuse. It is held in Core and dropped on unload. |

Page builder rows keep their signatures. `ctx.Settings.Page(...)` gains overloads that take a `PlayerSetting<T>` (`Toggle`, `Dropdown`, `Radio`, `Slider`, …). `scope:` on a row becomes **redundant and checked**: the row's scope comes from its declaration, and a contradicting `scope:` logs once and is ignored.

### Value types

Server-owned values cross the wire as BepInEx Toml strings with a type token, like ConfigSync. Every scope therefore uses the same gate: BCL primitives, `string` and BCL enums. A plugin-defined enum is refused, as today. A choice list over `string` (`AcceptableValueList<string>`) covers most enum-like needs. Strings sent to the server are capped at 1 KiB per value. `KeyboardShortcut` / keybinds stay Personal client-owned only.

### Enabled-when and visibility

| Rule | Where it is evaluated | Allowed on |
|---|---|---|
| `showIf` / `enableIf` (`Func<bool>`, today) | the process that draws the page (client) | client-owned rows only. On a server-owned row it cannot run on the client, so registering one throws `ArgumentException` naming `When`. |
| `enabledWhen` / `visibleWhen` (`When`, new) | the client, over values it holds (its own cfg, the server's published values, the player's own server-owned values) | every row |
| `whenAlone: AloneBehavior.Gray` (default) / `.Hide` | the framework, from [availability](#availability-reasons) | every row; `Hide` drops the row instead of graying it when its owning half or a `needsServerPart` dependency is absent |

`When` is plain data, so it can travel in the server's settings manifest: `When.On(s)`, `When.Off(s)`, `When.Equals(s, value)`, `When.NotEquals(s, value)`, `When.In(s, values…)`, `When.AtLeast(s, n)` / `AtMost`, and `When.All(...)` / `When.Any(...)` / `When.Not(...)`, at most 4 deep and 16 terms. A reference to a setting of another mod is refused. A rule over a setting the client cannot read (absent, unreachable) evaluates false, so the row is grayed with the framework's reason for that setting.

## Availability reasons

The framework decides each row's state from plain facts, in one pure function (`SettingAvailability.Decide`). Its matrix gets game-free tests the way `ServerRoute` does.

**Facts:** the row's scope and owning half (client: the client process registered it; server: the server's settings manifest declared it, or the client half bound it synced/per-player); connected or not; handshake state (none, pending, ok, refused, not Progenitor); whether the server's manifest arrived and whether it names the mod; the server's settings capability; per-row type drift; admin authorization for this player; the mod's pause and switch state.

**Worst-first reasons** (row grayed, words on the help line and in the readback `reason`):

| Code | Words | When |
|---|---|---|
| `not-connected` | `Not connected to a server.` | a server-owned row while not joined |
| `waiting` | `Waiting for the server…` | joined, handshake or settings manifest not arrived yet |
| `not-progenitor` | `Server doesn't run Progenitor.` | joined, the server did not answer the Progenitor handshake |
| `server-too-old` | `Server's Progenitor can't share settings yet.` | Progenitor server without the settings capability |
| `needs-server-part` | `Needs the server part of this mod.` | the manifest arrived and does not name the mod (proof, unlike the 16-slot request); or the mod is paused for this server |
| `version-mismatch` | `Server runs a different version of this mod.` | the type token differs, or the mod's version rule excludes the client's version |
| `needs-client-part` | `Needs the client part of this mod.` | reserved: a client-owned row the server's half requires. Not drawn in v1 (a client-owned row of a mod the client lacks is never on the page) |
| `mod-off` | `This mod is turned off.` | the player's switch is off (client-owned rows stay editable: the cfg still works) |
| `author` | the author's sentence, or `Not available right now.` | an `enabledWhen` rule is false |

**Not grayed, read-only:** a Server-wide row for a non-admin shows its value with `Admin only`. That is a permission state, not an availability reason.

**Today's `needsServerPart`** stays for **client-owned** rows whose effect needs the server part. Its evidence improves: once the settings manifest exists, a manifest naming the mod proves the part is running and one not naming it proves it absent. The truncated handshake request is then only the fallback for a server too old to send a manifest.

## Wire protocol

**Status of every message below: design.**

One new Core-reserved id, **`'PrSt'` (`0x50725374`)**, carries both directions. A direction byte keeps a reply from decoding as a request, as in `RemoteInstallWire`. A new client capability, **`Handshake.Capability.ScopedSettings = 4`**, gates it: the server sends nothing on `'PrSt'` to a client that did not declare it, and the client sends nothing before a manifest arrived. Payloads use the existing chunked envelope under the 15 KiB custom-event limit.

| Message | Direction | Content | When |
|---|---|---|---|
| `Manifest` | S→C | `serverName` (from `ServerHostSettings.json` `Name`, else empty, so the client falls back to address); `canEditServerWide` and `authorizedBy` (`adminlist` / `adminauth` / `none`); per mod `{guid, version, name}`; per setting `{id = section/key, scope, typeToken, default, bounds or choices, section, label, help, needsRestart, enabledWhen, visibleWhen, whenAlone}`, plus the **current value** for Server-wide and **this player's value** for Personal. | after the ok handshake, after ConfigSync and the catalog (appended, so the pinned wire order is unchanged); again after the server's plugin set or admin status changes |
| `Delta` | S→C | `{guid, id, value}` (+ `canEditServerWide` when it changed) | a Server-wide change goes to every ok, capable user (including disabled `User` entities, as ConfigSync does); a Personal change goes to that player only |
| `Write` | C→S | `{requestId, guid, id, scope, typeToken, expected, value}`. No identity or privilege fields. | an edit on the page |
| `Result` | S→C | `{requestId, ok, value (the server's current), code, sentence}` | each write |

**Server-wide values on clients that have the plugin** still apply through ConfigSync, which keeps the existing lock and the "never reaches your cfg" guarantee. The manifest is what the **page** reads, so a server-only mod's Server settings show on a client that has no copy of the mod. Admin writes for Server-wide values move from `'PrSe'` to `'PrSt'` `Write`. `'PrSe'` keeps serving clients that did not declare `ScopedSettings` until it is retired.

### Authorization and validation (server, in order)

1. Read `User` from the sender entity resolved from the connection. Refuse when it is not connected or `PlatformId == 0` (`Your account could not be identified.`).
2. The handshake is ok for this user, and the user declared `ScopedSettings`.
3. Registered: `(guid, id)` exists and its scope equals the request's scope.
4. **Server-wide only:** `ServerAdminList.Authorize(adminList.Check(PlatformId), user.IsAdmin) != None`, the same `ServerAdminList` instance remote install uses. `[RemoteInstall] AllowedSteamIds` does **not** apply (decision 6).
5. **Personal only:** the row written is always the sender's own `PlatformId`. A request cannot name another player.
6. Type token equal; parse (`SettingsEditors.TryParse`); bounds failing closed (`ServerSettings.Accepts`); string length cap; the author's `validate`. Today's `'PrSe'` path already applies the length cap from the server's own declaration ([server-wide text limits](mod-settings.md#server-wide-text-limits-textlimit1)).
7. Expected value equals the current value. Otherwise the write is refused with `changed` and the current value.
8. Rate limit per SteamID: a generalized `RemoteInstallRateLimiter`, with Server-wide at 10 writes per 60 s and Personal at 30 writes per 60 s. A refusal reads `Too many changes. Wait a moment and try again.`

Refusal codes reuse the reason table's style: `not-admin`, `not-registered`, `type`, `parse`, `bounds`, `invalid` (author), `changed`, `rate-limited`, `unidentified`, `internal`. On the client the existing pending, accepted, refused and 8 s timeout states of `ServerSettings` carry over.

### Audit

Every accepted **and** refused Server-wide write appends one line to **`BepInEx/progenitor-settings.log`**, with the same writer and 1 MiB roll as `RemoteInstallAuditLog` (a shared `AuditLog` helper): UTC time, character name, SteamID, `authorizedBy`, mod guid, setting id, old value, new value and the result code. Personal writes are not audited by default; refusals of Personal writes are logged at debug level (decision 7).

## Persistence and migration (Personal, server-owned)

- **Location (decision 1):** `BepInEx/config/progenitor-players/<modGuid>.json` in the server's install. It belongs to the server, like its cfg, and survives a world wipe. The world save was rejected, because a world wipe would erase players' preferences.
- **Format:**

  ```json
  {"schema": 1, "mod": "author.mod",
   "players": {"76561190000000000": {"name": "LastKnownName", "updatedUtc": "…",
                                      "values": {"Announce/Receive": "false"}}},
   "orphans": {"76561190000000000": {"Old/Key": "…"}}}
  ```

  Keys are `PlatformId` as decimal strings; never `User.Index` or an entity id. Values are Toml strings, as on the wire. **Only non-default values are stored.** A reset removes the key, so a later change of the author's default reaches every player who never changed it.
- **Writing:** debounced 1 s on the main thread, a temp file plus `File.Replace` (the `progenitor-mod-state.json` pattern), and flushed on Core uninstall and plugin unload. **An existing file that cannot be read** is never overwritten: that mod's writes are refused (`internal`) and logged once until the file is fixed.
- **Unknown and stale keys:** a key with no current registration is kept untouched, because the plugin may be unloaded or mid-reload. A value that no longer parses for the registered type moves to `orphans` with one log line and the player gets the default, so a downgrade can recover it. `PerPlayer(..., migrateFrom: "Old/Key")` is a later option.
- **World epoch and save safety:** the store is not world state. It needs no world epoch and does not interact with `[SaveSafety]` or `TriggerSave`.
- **Client-hosted games:** the host is a player with a `PlatformId` too. Server code does not branch on dedicated versus client-hosted.

## Hot reload

- **Registrations** are Core-owned string-keyed rows (`guid`, `id`, scope, type token, presentation, `When` data). `ctx.Track` disposes them on every unload reason. Delegates (`validate`, `showIf`, `enableIf`) are held in Core only and dropped with the registration.
- **Values survive a plugin reload:** the per-player document is Core-owned plain data (strings), not plugin types. When a new generation registers the same `(guid, id)` it reads the same values again. A server-side reload pushes a manifest `Delta` for that mod to capable clients, as ConfigSync's re-register push does.
- **Core swap:** the store flushes on uninstall and the new generation reloads from disk. The client discards its manifest cache and receives a new one after the handshake refresh.
- **No pins:** there are no `SettingChanged` subscriptions on Bootstrap's shared cfg (use `ConfigHelper.AddSettingChanged` where a file watch is needed; `SettingChangedPinTests` covers the files), no generic interop instantiations (everything is managed), and nothing is handed to IL2CPP or Bootstrap. `PlayerSetting<T>` is constrained to BCL `T`, so the handle a plugin holds cannot pin Core either.

## What the Mods page shows

Both sections keep today's grid, controls, reset arrow and description column. Section heading rows use the game's section header. The **Your settings** and **Server settings** headings replace scope-named author sections. Author sections become small subheadings inside the scope section they belong to, in declared order. A section whose rows are all hidden is omitted. Buttons and info lines are Personal (they run on the client) unless declared otherwise.

| State | Your settings | Server settings |
|---|---|---|
| Joined, admin | editable: client-owned locally, server-owned through `Write` | editable through `Write`; line `Applies to everyone on <server name>` |
| Joined, not admin | editable | values shown read-only; control column `Admin only · <value>`; help line `Only server admins can change this.` |
| Not connected | client-owned editable; server-owned rows the client half declared are grayed `Not connected to a server.` | rows the client half declared, grayed `Not connected to a server.`, showing the client default; line `Applies to everyone on the server you join`. Server-only mods are not listed (decision 4). |
| Joined, waiting | client-owned editable; server-owned grayed `Waiting for the server…` | grayed `Waiting for the server…` |
| Server doesn't run Progenitor | client-owned editable; server-owned grayed `Server doesn't run Progenitor.` | same reason |
| Server's half missing | client-owned editable; `needsServerPart` and server-owned rows grayed `Needs the server part of this mod.`, or hidden under `whenAlone: Hide` | same, or the section omitted when every row is hidden |
| Server-only mod, joined | its Personal rows from the manifest, editable | its Server-wide rows from the manifest |
| Version mismatch | client-owned editable; affected server-owned rows grayed `Server runs a different version of this mod.` | same |

- **Server name:** the manifest's `serverName`, else the joined `address:port` (as `ServerIdentity`), cut in the middle beyond the existing limit.
- **Admin who has not run `adminauth`:** admin-list membership authorizes, so there is no **Authenticate as admin** prompt on this page.
- **Reset all:** it resets the Personal rows the player can edit, client-owned and server-owned. Server-owned rows reset through one `Write` per changed row, rate limited. It never resets Server settings; each admin-editable Server-wide row keeps its own reset arrow.
- **Readback** (`mods_ui widget_read`): `page-setting` gains `scope: personal|server-wide`, `owner: client|server`, `availability {code, reason}`, `adminOnly`. New `page-scope-section` rows `{scope, rows, serverName, canEdit}`. `scopeDeclared` is kept for one release.

## Mapping existing mods

No mod code changes. Each existing declaration lands as follows:

| Declaration today | Scope | Owner | Page section |
|---|---|---|---|
| Client `ctx.Config.Bind` / `ctx.Bind` (not synced) | Personal | client | Your settings |
| Keybinds (`ctx.Keybinds`) | Personal | client | Your settings, Keys subheading (the Keybinds tab is unchanged) |
| `ctx.Bind(..., synced: true)` | Server-wide | server | Server settings. It stays read-only for admins unless opted in through `AllowServerEdits` or `scope: Server`, as today (decision 2). |
| Server `ctx.Bind` / `ctx.Config.Bind` (not synced) | Server-wide, **unpublished** | server | not shown: operator-only cfg values (tokens, URLs, paths) never leave the server unless declared `scope: ServerWide` (decision 3) |
| Page row `scope: SettingScope.Local` / `.Server` | Personal / Server-wide | as bound | as the row's scope |
| An ordinary BepInEx plugin's cfg entry (client) | Personal | client | Your settings, read-only unless `[Settings] EditOtherModsSettings` |
| SampleServerLed "For server" rows (client cfg pushed over the mod's event) | Personal | client | Your settings. Migrating them to `PerPlayer` is step 9. |

## Testing strategy

- **Game-free (xUnit, `tests/Progenitor.Tests`):**
  - the `SettingAvailability.Decide` matrix, one test per reason and the precedence;
  - grouping into the two sections, empty-section omission, subheadings and `whenAlone: Hide`;
  - `When` evaluation and its depth and term caps;
  - wire encode/decode round trips and malformed, truncated and oversize packets;
  - the server write path with injected `User` facts (admin-list member, adminauth only, neither, unreadable list, `PlatformId 0`, the wrong scope, type drift, bounds, `validate` refusal, expected-value conflict, rate limit);
  - audit lines;
  - store persistence: non-default-only storage, reset removes the key, orphans, an unreadable file refuses writes and is never overwritten, debounce and flush;
  - `ctx.Bind` scope argument errors;
  - a source pin that the new files use `ConfigHelper.AddSettingChanged` or no `SettingChanged` at all (an extension of `SettingChangedPinTests`);
  - an unload test showing that registrations, delegates and `Changed` subscribers are released while the store's values remain.
- **Bridge readback:** the new `page-setting` and `page-scope-section` keys, so live checks need no screenshots for state.
- **Live acceptance (isolated test server only, not run):**
  - an admin on the admin list without `adminauth` edits a Server-wide value, a second client sees the delta, and the audit line is written;
  - a non-admin sees `Admin only`;
  - a Personal server-owned value persists across a server restart and a plugin hot reload, and is per player (headless client versus Steam client);
  - a server-only sample mod's settings show on a client without the mod;
  - not connected, a vanilla server, and a server without the mod each show their reason.
  - Leave the client at the title screen afterward.

## Design decisions (2026-10-03)

The recommendations for all ten questions this design raised were accepted. They are decisions for the design; none is implemented. The page mockup (one list, **Your settings** / **Server settings · Applies to everyone on &lt;server&gt;**, `Admin only · <value>` for non-admins, grayed rows with the framework reason) was reviewed and kept as drawn.

1. **Per-player values live with the server install** (`BepInEx/config/progenitor-players/<modGuid>.json`) and survive a world wipe. They do not live in the world save.
2. **Only `scope: ServerWide` is admin-editable by default.** Legacy `synced: true` keeps today's explicit opt-in (`AllowServerEdits` or a page row's `scope: Server`), so no shipped value becomes remotely writable without its author's decision.
3. **Server binds that are not synced are hidden from clients.** They may hold secrets. Declaring `scope: ServerWide` publishes one.
4. **Server-only mods are omitted while not connected.** No previous server's manifest is cached.
5. **Admins do not view or change other players' Personal values in v1.** An audited server command may come later.
6. **`[RemoteInstall] AllowedSteamIds` does not restrict settings edits.** Settings follow the admin list and adminauth only.
7. **Only Server-wide writes are audited.** Personal writes are not; their refusals are logged at debug level.
8. **A framework chat command for Personal server-owned values** (for players without the client mod) comes later, after step 9.
9. **Server settings are editable only while joined in v1.** Editing the local server folder's settings from the title screen is not offered.
10. **Progenitor's Configuration tab** may adopt the Your settings / Server settings model later. It is unchanged for now. *Superseded by the 2026-10-05 decisions in [settings-organization.md](settings-organization.md), then by three homes (2026-10-06): the tab is **Preferences**, holding only how the Mods window works on this PC; Progenitor's own settings are on its page, and the joined server's setup on the **Server** tab.*

## Implementation plan

Each step is a separate, reviewable PR with game-free tests. Nothing before step 5 changes the wire.

1. **Design (this page)** and the roadmap row.
2. **Model:** `SettingScope` aliases (`Personal`, `ServerWide`), `SettingOwner`, `SettingAvailability.Decide` and its reason words, `When` (data and evaluation), and section grouping in `ModSettingsLayout`. Pure code and tests; no behavior change for drawn pages yet.
3. **Admin authorization parity:** `ServerSettings.Apply` authorizes via `ServerAdminList.Authorize` (shared instance with remote install); add the shared `AuditLog` helper and `progenitor-settings.log`; add the generalized rate limiter. Ships value on its own: admins on the list no longer need `adminauth` for today's opted-in rows. Update [config-sync.md](config-sync.md), [mods-menu.md](mods-menu.md#settings-rows), and the `AdminAuthFlow` comment.
4. **Page:** two scope sections for the rows a client already knows (client Personal, synced Server-wide), `Admin only`, the `Applies to everyone on <address>` line, reasons from step 2 over today's handshake evidence, readback keys, and an update to [mod-settings.md](mod-settings.md). *Done (game-free tested, not live-verified).* Differences from this design: the `mod-off` reason was left out (client-owned rows stay editable while a mod is off, so it changed nothing); whether the player may edit Server-wide settings comes from the server's remote-install status until the manifest exists; hide-when-alone is the builder modifier `.HideWhenAlone()` (a parameter would have changed every row method's signature, which compiled plugins bind to).
5. **Settings manifest (`'PrSt'`, `Capability.ScopedSettings`):** Server-wide schema, values and server name; `Write` / `Result` for Server-wide; the client renders server-only mods' Server settings; `needsServerPart` evidence from the manifest. `'PrSe'` stays for older clients.
6. **`ctx.Bind(scope:)`** and `When` on page rows, `whenAlone`, server-owned rows carrying rules in the manifest, and api.md rows.
7. **Per-player store** (`PlayerSettingsStore`) and the server side of `ctx.Settings.PerPlayer` / `PlayerSetting<T>`, with persistence tests.
8. **Personal server-owned over the wire:** the manifest carries the player's values, `Write` for Personal, the client `Mine` / `Changed`, and page integration.
9. **Samples:** migrate SampleServerLed's "For server" rows to `PerPlayer`; add a minimal server-only sample with one Personal and one Server-wide setting and its README acceptance procedure.
10. **Live acceptance** on the isolated test server, with evidence in the internal research notes; then move the roadmap row's status.

## Verifying the private-game host

Not yet run live. On a client hosting a private game, with the host not on the admin list and not having run `adminauth`:

1. In the hosted child's `BepInEx/LogOutput.log`, the command line shows `-owner` and `-hostServer`.
2. `.debug` in chat is accepted (no `This command is admin-only.`), and `.debug status` reports admin.
3. The Server tab rows are editable; an edit writes one `progenitor-settings.log` line with `authorizedBy=host`.
4. `server_rules` writes from the join-admin page succeed; the audit line names `host`.
5. A second player who joins the same private game stays a non-admin.

