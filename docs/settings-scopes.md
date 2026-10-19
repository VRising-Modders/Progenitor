# Settings scopes

Progenitor groups settings by who they affect, independently of where the mod runs.

| Group | Owner and storage | Who edits |
|---|---|---|
| **Your preferences** | Client; the player's mod cfg | That player |
| **Your settings on this server** | Server; one value per stable player account | That player |
| **Server settings** | Server; shared mod cfg | Server admins; other players see read-only values |

The second group names the connected server and explains that changes affect only you there. The third says **Applies to everyone on <server>**. Server-only mods can provide both groups without installing their DLL on clients; the client needs Progenitor. Disconnect removes their definitions and values. No previous server's catalog is retained.

## Authoring

```csharp
// Local preference, unchanged API.
var hints = ctx.Bind("Display", "Hints", true, "Show hints on this client.");

// Explicit publication and administrator editing.
var interval = ctx.Bind("Announcements", "Interval", 10,
    scope: SettingScope.ServerWide,
    description: "Minutes between announcements for everyone.",
    acceptableValues: new AcceptableValueRange<int>(1, 120));

// A preference saved separately for each player by this server.
var receive = ctx.Settings.PerPlayer("Announcements", "Receive", true,
    "Receive announcements on this server.");

// On the server, given a stable account id:
bool enabled = receive.For(accountId);
```

`PlayerSetting<T>` is in `Progenitor.Ui`. `For(PlayerAccountId)` and `For(Entity userEntity)` read server values. `SetFor(accountId, value)` validates and saves a server-side change. On the client, check `HasMine` before reading `Mine`; `Set(value, completed)` sends an authenticated request. `Changed` delivers `(PlayerAccountId, T)` on the game thread when a value changes; client events use `PlayerAccountId.None` for the local player and also fire once when the server first provides the value. A re-sent catalog with the same value does not fire it. Handles and callbacks are tracked and released on plugin unload.

`PerPlayer` accepts optional `AcceptableValueBase acceptableValues` and `Func<T, string?> validate`. Return null to accept, a reason to refuse. Types must pass the existing BCL settings type gate and have a settings editor. Arbitrary plugin-defined types are not transmitted or loaded on clients.

The existing `ctx.Bind(..., synced: true)` overload keeps its binary signature and edit permissions. Supported synced binds now publish their schema, but remain read-only unless explicitly opted into administrator edits. Plain, unsynced server cfg entries remain unpublished: tokens, paths and private operator values do not leave the server. Explicit `scope: Personal` on a server bind is refused; use `PerPlayer` instead.

## Edits and validation

The server derives identity from the connected User, never from a submitted player id. Personal writes always affect that sender. Server-wide writes reuse `ServerSettings` authorization, lock list, bounds checks, rate limit and audit log. Settings authorization uses the game's admin list, adminauth or recognized private-game host; remote-install allow lists do not grant or deny settings access.

Each request includes the displayed value as a conflict token. Unseen changes are refused and the current value returned. Personal edits allow 30 requests per account per minute, enforce type, bounds, custom validation, finite numbers and text limits. Server-wide edits retain the existing 10-per-minute limit. Pending client requests time out after eight seconds; when the server already shows the requested value by then, the request reports success. Results are matched by request id, so a write accepted while the server re-sent its catalog still completes as accepted, and the server accepts writes made against the catalog it sent just before the current one. A change requested while a new catalog is arriving waits for it and is then sent. Resetting personal settings excludes server-wide settings; each writable remote row also has its own reset.

## Storage and lifecycle

Personal values are stored at `BepInEx/config/progenitor-players/<modGuid>.json` using decimal platform ids. Only non-default overrides are stored; reset removes an override. Unknown keys survive plugin removal. Values that cannot be parsed or accepted by the current declaration move to `orphans` and read as the default.

An accepted change applies in memory at once and is written to disk coalesced, 1.5 s after the first unsaved change, with an atomic, flushed replacement, so a crash loses at most the last moments of changes and never leaves a partial file. Plugin unload and Core unload write any unsaved changes.

The store notices when the file is edited outside the game (checked every two seconds and before each write) and merges: a key the edit changed takes the file's value, and the store's own unsaved changes to other keys are kept. A file that is not valid JSON in this format, at load or later, is copied to `<modGuid>.json.bad` (a timestamped name when that exists already) with a warning in the log; at load the store starts from defaults, later it keeps the values it last read. The next save replaces the invalid file. A file that cannot be read yet, for example while another program locks it, is retried and never overwritten unread. Values survive plugin/Core reload and do not depend on the world save.

## Protocol

Core channel `PrSt` (`0x50725374`), capability `ScopedSettings = 1073741824`. The earlier design's value 4 belongs to JoinFlow and must not be reused. Since 2026-10-18 bit 8 is `MaterialViews` (declared at run time, only while a client's material seams are bound); the free capability bits are 16, 32 and 2147483648, and the top bit is best kept for an extension marker once the 32-bit row is full. Older peers receive no PrSt traffic. The existing ConfigSync and PrSe paths remain supported.

A versioned, direction-checked binary stream sends a bounded snapshot: begin, up to 512 individual definitions, end. All parts carry a generation; the client publishes the catalog only when its count and identifiers are complete. Each packet is at most 15 KiB, fields at most 4096 UTF-8 bytes, choices at most 128. Updates carry values for that recipient; personal values are never broadcast to other accounts. Register/unregister and permission changes replace the catalog. Client caches and pending callbacks are discarded on disconnect or unload.

Definitions carry identity, mod name/version, type token, scope, default/current value, choices, bounds, help and editability. They are data, never executable client code. Authored local settings retain their presentation predicates; a local/remote type mismatch is read-only. Serializable cross-setting visibility expressions from the original design remain a separate extension; this protocol version does not claim to transmit arbitrary page layouts or delegates.

## Qualification

Game-free: the scoped settings, store, wire and UI tests pass on current main. Live checks on the isolated client/server pair, on the 2026-10-15 build before the port to current main, verified a server-only mod appearing without a client DLL, personal writes by a non-admin, shared settings read-only for that player, admin writes, validation/conflict rejection, personal reset leaving shared values unchanged, persistence across plugin/Core reloads, and clean collection of old Core generations. The three groups were checked through UI readback and screenshots at 1920x1080 and 3840x1080. Two simultaneous player accounts and a full process restart were not live-tested in this batch; account isolation and disk persistence have game-free coverage. See `Progenitor.ReloadExample` for a server-only example and `Progenitor.SampleServerLed` for both-side use. Evidence is maintained in the external research repository. The port to current main has not been run live; one change from the checked build: the handshake capability bit is now 1073741824, because 536870912 was taken by `MaterialRefineView` on main. The review fixes of 2026-10-18 (coalesced store writes, merging external edits, the invalid-file backup, change notifications only on a changed value, write results across a re-sent catalog, and the allocation-free tick) are built and game-free tested only.
