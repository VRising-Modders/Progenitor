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

### Placing per-player settings on the page

A `PerPlayer` setting the page does not place still appears on a client that has the mod: the received rows the page does not draw are added after its sections, grouped by their section (or the section the server named), under **Your settings on this server**. To put one where it belongs, place it with the typed overloads, on both sides:

```csharp
var on = ctx.Settings.PerPlayer("Autoloot", "Autoloot", true, "Pick up nearby drops automatically.");
ctx.Settings.Page(page => page
    .Section("Me")
        .Toggle(on, label: "Autoloot", help: "Pick up drops near you automatically."));
```

`Toggle(PlayerSetting<bool>)`, `Number<T>`, `Slider<T>`, `Dropdown<T>`, `Radio<T>`, `Text(PlayerSetting<string>)`, `Setting<T>` and `ItemList(...)` take the usual `label`, `help`, `showIf` and `enableIf`. The row is drawn from the joined server's definition; while no server provides it, it is not drawn, and this side's cfg row of the same setting (a server-wide item list) is not drawn either. Off the page, `PlayerSetting<T>.Present(new SettingPresentation { … })` gives the label, control, choice labels, units, section (`Group`) and order; for a plain bind, `ctx.Menu.RegisterSetting(entry, presentation)` does the same.

The existing `ctx.Bind(..., synced: true)` overload keeps its binary signature and edit permissions. Supported synced binds now publish their schema, but remain read-only unless explicitly opted into administrator edits. Plain, unsynced server cfg entries remain unpublished: tokens, paths and private operator values do not leave the server. Explicit `scope: Personal` on a server bind is refused; use `PerPlayer` instead.

## Edits and validation

The server derives identity from the connected User, never from a submitted player id. Personal writes always affect that sender. Server-wide writes reuse `ServerSettings` authorization, lock list, bounds checks, rate limit and audit log. Settings authorization uses the game's admin list, adminauth or recognized private-game host; remote-install allow lists do not grant or deny settings access.

Each request includes the displayed value as a conflict token. Unseen changes are refused and the current value returned. Personal edits allow 30 requests per account per minute, enforce type, bounds, custom validation, finite numbers and text limits. Server-wide edits retain the existing 10-per-minute limit. Pending client requests time out after eight seconds; when the server already shows the requested value by then, the request reports success. Results are matched by request id, so a write accepted while the server re-sent its catalog still completes as accepted, and the server accepts writes made against the catalog it sent just before the current one. A change requested while a new catalog is arriving waits for it and is then sent. Resetting personal settings excludes server-wide settings; each writable remote row also has its own reset.

## Storage and lifecycle

Personal values are stored at `BepInEx/config/progenitor-players/<modGuid>.json` using decimal platform ids. Only non-default overrides are stored; reset removes an override. Unknown keys survive plugin removal. Values that cannot be parsed or accepted by the current declaration move to `orphans` and read as the default.

An accepted change applies in memory at once and is written to disk coalesced, 1.5 s after the first unsaved change, with an atomic, flushed replacement, so a crash loses at most the last moments of changes and never leaves a partial file. Plugin unload and Core unload write any unsaved changes.

The store notices when the file is edited outside the game (checked every two seconds and before each write) and merges: a key the edit changed takes the file's value, and the store's own unsaved changes to other keys are kept. A file that is not valid JSON in this format, at load or later, is copied to `<modGuid>.json.bad` (a timestamped name when that exists already) with a warning in the log; at load the store starts from defaults, later it keeps the values it last read. The next save replaces the invalid file. A file that cannot be read yet, for example while another program locks it, is retried and never overwritten unread. Values survive plugin/Core reload and do not depend on the world save.

## Presentation hints

A server-only mod's settings are drawn on a client without its DLL the way the mod's own page draws them. The server derives the hints from the `ctx.Settings.Page` row that places each published setting (a cfg entry, or a `PerPlayer` setting placed with the typed overloads), else from its `ctx.Menu.RegisterSetting` / `PlayerSetting.Present` presentation, else sends none:

| Hint | From the page row | Without a hint |
|---|---|---|
| Control | `Toggle`, `Slider` (with `step`), `Radio`, `Dropdown`, `Number`, `Text` | per type: switch, number field, text field; a choice row with four or fewer options shows them all (radio), more a dropdown |
| Label and help | `label`, `help` | the cfg key and description |
| Section and order | the page section's title and index, the row's index on the page | the cfg section, catalog order |
| Unit or format text | `units` | none |
| Choice words | `labels` (matched to the row's choices by value) | the stored values |
| Show / enable rule | `ShowWhen`, `EnableWhen`, `ShowIf(entry, value)`, `EnableIf(entry, value)` | always shown and enabled |

A slider needs the setting's `AcceptableValueRange`; without both bounds it is a number field. A page that is registered or dropped, a catalog presentation that changes, and a server fact that changes all re-send the catalog.

### Show and enable rules

```csharp
page.Toggle(server.NoClientHint, label: "Tell them on joining")
    .ShowIf(server.NoClientAutoloot, true);
page.Toggle(caps, label: "Respect Satisvampory caps")
    .ShowWhen(SettingCondition.ServerHasMod("Satisvampory"));
page.Slider(server.Radius, units: "m")
    .EnableWhen(SettingCondition.Is(server.Enabled, true) & SettingCondition.AtLeast(server.Radius, 1));
```

`SettingCondition` forms: `Is` / `IsNot` (a constant), `Above` / `AtLeast` / `Below` / `AtMost` (a number), on a `ConfigEntryBase`, a `section, key` of the same mod, or `playerSetting.Is(value)`; `ServerHasMod(guid)`, which the server answers from its loaded mods in the rule it sends and re-sends when the answer changes (a rule on a row only the client has, or a mod no sent rule asks about, is answered from the server's mod list the client received in the join handshake's feature table; unknown until it arrives); `All` / `Any` (or `&` / `|`). At most 16 parts, 4 deep, constants up to 256 UTF-8 bytes, 1,024 bytes encoded. Equality compares numbers numerically, `true`/`false` and text without case; the ordering comparisons hold only for numbers. A comparison whose setting has no value is false; a server fact without an answer is false. Locally the rule is combined with the row's `showIf` / `enableIf` delegate and evaluated at the same moments; a client without the DLL evaluates it against the values it received, again after each received change. Chained `&` / `|` and repeated `ShowWhen` / `EnableWhen` flatten into one group. A part that names a setting the mod does not publish is left out of the rule the server sends (one note in the server log) and the rest is kept: `Any(IsNot(localEntry, x), ServerHasMod("y"))` reaches a client without the DLL as `ServerHasMod("y")`; a client with the DLL evaluates the whole rule from its own page. **Delegates (`showIf`, `enableIf`, button actions, info functions) are never sent:** on a client without the DLL a delegate-only predicate does nothing.

## Item lists

An item list is a list of lines, each an item prefab GUID or (with `AllowCategories`) a category id, and a count:

```csharp
var rules = new ItemListRules { MaxCount = 100_000, AllowCategories = true,
    Categories = () => satis.Categories() };            // (id, name) pairs for the picker
var filter = ctx.Settings.PerPlayerItemList("Me", "PickupFilter",
    "Items you want less of: the most of each to carry.", rules);
ctx.Settings.Page(page => page.Section("Me").ItemList(filter, label: "Pickup filter"));

// Server: one list per account.
int? cap = filter.For(accountId).MaxForItem(prefabGuid);
```

- `ctx.Settings.ItemList(...)` → `ItemListSetting` is one server-wide list (stored in the mod's cfg, synced to clients with the mod, admins edit it); `ctx.Settings.PerPlayerItemList(...)` → `PlayerItemListSetting` keeps one per player in the players store. Both take `rules` (null: items only, 256 lines, counts 0 to 100,000) and an optional default list.
- Stored and sent as text: lines joined by `;`, each `<guid>=<count>` or `c:<category>=<count>` (`-1531666018=50;c:gems=0`). A category id is 1 to 32 letters, digits, `_`, `-` or `.`.
- The server checks every write against its own copy of the rules: well-formed text, one line per item or category, counts 0 to `MaxCount`, categories only when allowed, at most `MaxEntries` lines, then stores the canonical text. A stored list the rules no longer accept reads as empty (logged once for a server-wide list).
- **Size limits: refused, never chunked.** At most 256 lines and 4,096 UTF-8 bytes of text, one settings field. 256 item lines with counts up to 999 always fit; larger counts or long category names reach the byte limit first, and such a list is refused with the reason. A row is refused at registration when its help and default would not fit one 15 KiB packet beside a full list. Up to 64 categories (ids up to 32, names up to 64 bytes) are sent for the picker.
- The Mods menu draws the row as its size (`3 items listed`) and **Edit**, which opens the list in a game window (item or category, the count, remove) with **+ Add…**, the item picker (the kit checklist over this client's item catalog, with an Items / Categories filter). Each change sends the whole list; a change made while the server answers the one before is sent next, applied to the list the server answered with. The window shows the server's list and stays open when the catalog is re-sent with the same rows. A non-admin reads a server-wide list's size.
- A client without version 2 hints sees the text in a text field; the server still validates it.

## Protocol

Core channel `PrSt` (`0x50725374`), capability `ScopedSettings = 1073741824`. The earlier design's value 4 belongs to JoinFlow and must not be reused. Since 2026-10-18 bit 8 is `MaterialViews` (declared at run time, only while a client's material seams are bound) and bit 16 is `ScopedSettingsPresentation` (below); the free capability bits are 32 and 2147483648, and the top bit is best kept for an extension marker once the 32-bit row is full. Older peers receive no PrSt traffic. The existing ConfigSync and PrSe paths remain supported.

**Version 2.** A client that declares `ScopedSettingsPresentation = 16` beside `ScopedSettings` is sent version 2 rows: the version 1 row followed by a presence byte and the presentation block (control, label, section title and order, row order, step, units, choice labels, show-if and enable-if rules, item-list categories and bounds; at most 6,144 bytes). Every other packet kind is laid out as in version 1 with version byte 2, and clients always write version 1. A client that does not declare it, or an older server, exchanges version 1 only: the client draws the default editors and evaluates no rules. Hints that do not fit their block or the packet are reduced in steps, logged once per row: the category names (ids are shown), then the categories, then the choice labels, then the show/enable rules. The control, label, section, order, step, units and list bounds are never dropped; a list row is refused at registration unless its largest possible value, help and those hints fit one packet. The hints are data only; no delegate or client code is sent.

A versioned, direction-checked binary stream sends a bounded snapshot: begin, up to 512 individual definitions, end. All parts carry a generation; the client publishes the catalog only when its count and identifiers are complete. Each packet is at most 15 KiB, fields at most 4096 UTF-8 bytes, choices at most 128. Updates carry values for that recipient; personal values are never broadcast to other accounts. Register/unregister and permission changes replace the catalog. Client caches and pending callbacks are discarded on disconnect or unload.

Definitions carry identity, mod name/version, type token, scope, default/current value, choices, bounds, help (the page row's help line when it has one) and editability, and at version 2 the presentation hints above. They are data, never executable client code. On a client that has the mod, its own page's options win: its predicates, labels, control and choice words are kept, and the server's hints fill only what the local row leaves unset; a local/remote type mismatch is read-only.

## Qualification

Game-free: the scoped settings, store, wire and UI tests pass on current main. Live checks on the isolated client/server pair, on the 2026-10-15 build before the port to current main, verified a server-only mod appearing without a client DLL, personal writes by a non-admin, shared settings read-only for that player, admin writes, validation/conflict rejection, personal reset leaving shared values unchanged, persistence across plugin/Core reloads, and clean collection of old Core generations. The three groups were checked through UI readback and screenshots at 1920x1080 and 3840x1080. Two simultaneous player accounts and a full process restart were not live-tested in this batch; account isolation and disk persistence have game-free coverage. See `Progenitor.ReloadExample` for a server-only example and `Progenitor.SampleServerLed` for both-side use. Evidence is maintained in the external research repository. The port to current main has not been run live; one change from the checked build: the handshake capability bit is now 1073741824, because 536870912 was taken by `MaterialRefineView` on main. The review fixes of 2026-10-18 (coalesced store writes, merging external edits, the invalid-file backup, change notifications only on a changed value, write results across a re-sent catalog, and the allocation-free tick) are built and game-free tested only.

Presentation hints, show/enable rules and item lists (PrSt version 2, 2026-10-19): source-checked, built and game-free tested (`ScopedSettingsPresentationTests`: encoding of the hints, rules and lists, rule evaluation, validation, size limits, version 1/2 compatibility, derivation from the page, drawing, the item-list editor), **not live-verified**. The item picker and list window have not been drawn in the game yet.
