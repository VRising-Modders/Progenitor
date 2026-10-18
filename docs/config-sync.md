# Admin-synced config

Roadmap M2.5. Config entries bound with `synced: true` are server-authoritative after a successful handshake. Mods can explicitly opt entries into the [in-game admin editor](mods-menu.md). The server validates administrator status, registration, type, bounds and the expected previous value. Other entries remain editable on the server through the plugin `GUID.cfg` watcher or `ConfigEntry<T>.Value`.

Design notes are in the internal research notes.

## Bind

```csharp
var damage = ctx.Bind("Combat", "DamageScale", 1.25f, "Server-owned multiplier.", synced: true);
```

`T` must be a BCL primitive, `string`, or a BCL enum — the same gate `ConfigHelper` uses on the shared Progenitor cfg. A plugin enum or class throws `ArgumentException` at Bind so the Toml form is well-defined and `ConfigEntry<T>` cannot pin a collectible Core generation.

`ctx.Config.Bind(..., synced: true)` throws: that facade cannot track the handle. Use `ctx.Bind(..., synced: true)` so unload unregisters the row.

A record whose Toml form cannot fit in a 15 KiB custom-event packet is refused at Register (`ArgumentException`). A later value that cannot fit is logged (`ConfigSync: skipped {plugin}/{section}/{key}: {error}`) and omitted — fail closed for that entry, never silently dropped.

## Sample

`samples/Progenitor.Sample` binds `Sample.EmberStack` (int, default 99) with `synced: true`. The Late ember editor reads that value instead of a hard-coded 99.

```csharp
var stack = ctx.Bind("Sample", "EmberStack", 99,
    "Max stack for sample.ember after the Late editor.", synced: true);
```

Live recipe: edit `BepInEx/config/progenitor.sample.cfg` on the server (`[Sample]` / `EmberStack`), reconnect. Client log `Sample: EmberStack applied/locked to N`. Bridge `status` fragment `itemMap max=N`. `.ember` prints `max=N`.

## Join

After the handshake result is `ok`, the server sends every Synced entry of every plugin the client also reported **at a compatible version** (guid and version; reserved handshake guids skipped). Packet event id `PrcS` (`0x50726353`) on the existing custom-event channel. Values are BepInEx Toml strings (`GetSerializedValue` / `SetSerializedValue`).

**Which plugins are sent (mod compatibility C3).** A plugin's entries are sent when the join's version judgment passes for it ([mod-compatibility.md](mod-compatibility.md#evaluation-at-the-join)): **Version matching**, the admins' per-mod rule, else the newer side's declared range (for example a 1.4.0 server declaring `>=1.2.0 <2.0.0` sends to a 1.2.0 client). A plugin that fails is skipped with one log line naming the reason. Only when the handshake made no judgment does the attribute rule alone decide, as before C3: `Minor` (same major.minor), `None` (Handshake's presence-only rule: versions are not compared), or Exact (the default, including undeclared `Require`; an identical version string).

The client applies a server value only when the same section/key exists **with the same BCL type** (the type name travels on v2 packets). Under Minor a v1 packet (empty type name) is refused, with an explicit type-gate `schema-drift` line, so a mixed-Core swap cannot silently apply a string onto an int. Exact/None still apply a v1 packet so a mixed-Core snapshot is not dropped. A breaking config change (renamed key, changed type or meaning) needs a new key.

**Keys the server did not send, the default lock (C3; a behavior change).** To a client that declared `Capability.VersionRanges`, the join snapshot carries a reserved marker record after each plugin it covered (every plugin both sides run that passed the judgment, including one the server has no synced key for): empty section, key and value, type name `progenitor:snapshot-sent`. Older clients never get markers. When the snapshot's last packet arrives (snapshot flag set, more-flag clear), every synced entry the client registered for a marked plugin that the snapshot did not contain is **locked to the entry's declared default** for the session, one line per key. The server's older build predates the key, so it behaves as if the setting were at its default, and the client does too. Before C3 such a key kept the client's local value, unlocked; that is still what happens for a plugin the server did not mark (an older server, an older client, or a plugin the server does not run). A plugin the server only names on its handshake (a `Require` for a companion client mod it does not load, a `ClientOnly` row) is not one it runs and is never marked, and a plugin whose snapshot had to skip a value too large to send loses its marker, so no key is locked to a default because of it. Progenitor's own settings are never marked.

**Values the client cannot read (C3).** A value of another type is skipped and the entry is locked to its declared default, with the `schema-drift` line (before C3: skipped, local value kept). An enum name the client's enum does not have is the declared default with a `schema-drift` line, instead of a Toml parse failure. Out-of-bounds values follow `SyncedBounds` as before.

Default locks use the same lock as a server value: refused writes, parked locals, the held save, and the local value back on disconnect. A later push of that key replaces the default.

Status: source-checked and game-free tested (`ConfigSyncRangeTests`, `ConfigSyncTests`); not live-verified.

The client applies them, caches its previous locals, and **locks** the entries until disconnect (or a new `Client_0` world). Plugin hot-reload while still in the session does **not** restore locals: Unregister keeps the lock and the server value (and does not write Toml), and a re-bind of the same key re-attaches (one `re-attached … after plugin reload` line). A server push or server re-register that lands while the plugin is unbound updates the session lock, so re-attach writes the **newest** server value. Live BepInEx assigns `ConfigEntry.Value` first; ConfigSync reverts on the entry's `SettingChanged` / the file's `SettingChanged` immediately and logs one line per key until unlock. `SaveOnConfigSet` stays **off** while any Synced slot on that file is locked — `EndApply` never forces it back to true — so BepInEx does not auto-save a later non-synced `Value` set; ConfigSync saves it instead (see [Local settings in a held cfg](#local-settings-in-a-held-cfg)). Locked keys are also parked as BepInEx `OrphanedEntries` holding the pre-join local (including a key bound after the session is already locked), so an explicit `ConfigFile.Save()` writes that local instead of the live server `BoxedValue`. Parking drops the live row even when `ConfigFile.Remove` throws.

On disconnect the cached locals are written back, the entries unlock, `SaveOnConfigSet` is restored, and the file is `Save()`d so disk matches the restored locals.

### Local settings in a held cfg

A cfg with synced entries usually also holds the plugin's own non-synced entries (player preferences, keybinds). While the hold is on, ConfigSync saves that file itself after any of them changes, whoever made the change: a plugin setting `ConfigEntry<T>.Value` itself, or the settings page (which also saves at once).

| Rule | Behavior |
|---|---|
| Debounce | One pending save per file, written `ConfigSync.LocalSaveDebounceSeconds` (**1.5 s**) after the last change, and at most `ConfigSync.LocalSaveMaxWaitSeconds` (**10 s**) after the first, so a plugin writing every frame is still saved. |
| What is written | The same save as an explicit `ConfigFile.Save()` during the hold (`ConfigSync.TrySaveHeldFile`): locked keys are parked orphans holding the player's own pre-join values, so a server value never reaches disk. When a locked key could not be parked, nothing is saved, with one warning per file per session. |
| Not a trigger | A write by sync itself to a synced entry (join snapshot, later push, default lock, revert of a refused write, restore). A plugin's write to its own entry made while an apply runs is saved after the apply. |
| Left to the mod | A file whose `SaveOnConfigSet` was already off before the hold. |
| Flush | A pending save is written at once on disconnect or a new client world, when the file's last synced entry unregisters (plugin unload or hot reload), on Core unload, and on a normal quit (Bootstrap's quit slot). A forced kill runs none of these. |
| Server value left live | When a restore gives up and cannot park the player's value, that file keeps `SaveOnConfigSet` off for the rest of the Core generation and nothing saves it, because its live row still holds the server's value. |

Status: source-checked and game-free tested (`ConfigSyncLocalSaveTests`, and `ConfigHelperTests` for a value left live); not live-verified.

Acceptance procedure — not run:

1. Client and test server run a plugin with a synced entry and a non-synced entry in one cfg, where the plugin sets the non-synced `ConfigEntry<T>.Value` from its own code. A settings-page write is not this path: it saves at once (SAVEHOLD1) and logs no line here.
2. Join the test server. Make the plugin change the non-synced entry. Within about 2 s the client `BepInEx/config/<guid>.cfg` holds the new value, the synced key still holds the pre-join local (not the server's value), and the log has one `ConfigSync: saved '…' for a local setting change while its synced settings are locked (debounce)` line for that file.
3. Change it again and hot-reload the plugin within the debounce: the cfg holds the newest value. No new log line: the save line is logged once per file per Core generation.
4. Change it again and quit the game normally within the debounce: after restart the value is kept.
5. Disconnect: the cfg holds the restored locals and the non-synced value.

**"Server values never reach your `.cfg`" is a tested claim, not an assertion.** `ConfigSyncTests.A_synced_value_never_reaches_the_cfg_on_disk` drives a synced entry through the real client apply path against the `ConfigFile` shim (`tests/Progenitor.Tests/ConfigFileShims.cs`, whose `Save()` writes a real file: Entries then OrphanedEntries, the same order as live BepInEx) and asserts on **bytes read back from disk**, not on the shim's in-memory `LastSavedText`:

1. before the join the file holds the local (`Count = 3`);
2. after `ApplyRecords` the overlay is the server's value (`Count == 9`) and the file is byte-for-byte unchanged;
3. a write to the locked key and a write to an unrelated non-synced key on the same file change nothing on disk at once (`SaveOnConfigSet` is held off while any synced slot on that file is locked; the non-synced change is saved later by the debounced local save, which this test does not run), and the locked key reverts with `ConfigSync: refused write to …`;
4. an explicit `ConfigFile.Save()` during the session writes the parked pre-join local (`Count = 3`, never `9` or `99`) and the non-synced key's new value;
5. after `RestoreLocal()` the file holds the restored local and `SaveOnConfigSet` is back on.

The shim is not the live BepInEx type (`Test_project_does_not_reference_live_BepInEx_ConfigFile` asserts that), so a live client is still worth one confirmation pass: after a join, grep `ConfigSync: refused write` for a locked key, confirm `GUID.cfg` still has the pre-join local after an explicit `Save()` and after a non-synced key change, and that a Sample hot-reload while connected logs `re-attached` and keeps the server stack cap.

## Later changes

A server-side `SettingChanged` on a Synced entry is packed as a delta and sent to every connected user that passed handshake, **including disabled `User` entities** (the same set handshake re-arm walks). A server re-register (plugin hot-reload, or a late load after join) pushes the live value only when at least one user has already matched (`HandshakeOkUsers.Count > 0`); boot binds before anyone has handshaken do not log a push-to-zero. The client updates the locked value and still restores the original local on disconnect.

A client that has no matching plugin or no matching section/key ignores that tuple with one log line.

## Log lines

Server:

- `ConfigSync: sending {n} synced setting(s) to {name}` (with ` ({m} plugin(s) marked sent)` for a C3 client)
- `ConfigSync: pushed {plugin}/{section}/{key} to {n} client(s)`
- `ConfigSync: skipped {plugin}/{section}/{key}: {error}`
- `ConfigSync: ignored {guid} synced settings: client version '{client}' does not match server '{server}' ({rule or the judgment's reason})`

Client:

- `ConfigSync: applied {n} synced setting(s); locked until disconnect`
- `ConfigSync: ignored {plugin}/{section}/{key}: no such plugin or entry`
- `ConfigSync: schema-drift {plugin}/{section}/{key}: client type '{client}' does not match server '{server}'; using the default {value}`
- `ConfigSync: schema-drift {plugin}/{section}/{key}: '{value}' is not a value this client's {Enum} knows; using the default {value}`
- `ConfigSync: {plugin}/{section}/{key} not sent by the server (its version predates it); using the default {value}`
- `ConfigSync: schema-drift {plugin}/{section}/{key}: v1 packet has no type name; type gate refused under Minor (client type '{client}')`
- `ConfigSync: re-attached {plugin}/{section}/{key} to the locked server value after plugin reload`
- `ConfigSync: refused write to {plugin}/{section}/{key} while locked to the server value`
- `ConfigSync: restored {n} local setting(s) after disconnect`
- `ConfigSync: saved '{path}' for a local setting change while its synced settings are locked ({debounce|max wait|disconnect|plugin unload|unload|process exit}); synced keys keep this client's own values on disk` (once per file)
- `ConfigSync: a local setting change in '{path}' was not saved ({reason}): {why}` (once per file per session)

Administrator edits from the client (opt-in per entry; authorized by the server's admin list or `adminauth`, rate limited and audited to `BepInEx/progenitor-settings.log`) are described under [Settings rows](mods-menu.md#settings-rows). Not provided: a separate config-editor plugin UI.
