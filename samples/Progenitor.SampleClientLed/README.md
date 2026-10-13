# Client-led sample

`Progenitor.SampleClientLed` (`progenitor.sampleclientled`) is a mostly-client mod with an optional server part. It is the reference example for a client mod that works on any server and has settings that need its server part.

**Status:** source-checked, game-free tested, and partly live-verified (below). Release build compiled; `tests/Progenitor.Tests/CounterpartSamplesTests.cs` covers the declarations, the join matrix, the settings scopes and grayed rows, and the clock rules. **Live (2026-09-29, user's Steam client + isolated test server, commits ad5d6682 and 95b2c2d0):** client half with no server half: join allowed, nothing paused, server-dependent rows grayed, status bar `Server part not on this server · limited`; both halves: `Server part running`, nothing grayed; client half switched off with the server half present: join allowed, nothing refused; the page shows the right server-part fact right after `Handshake: ok` and keeps it without a reselect. Not observed live: announcement delivery (chat vs popup), the heartbeat fallback, non-admin refusal, and the Ctrl+F6 clock popup. **Since 2026-10-10 (mod compatibility C5) the time request and answer are versioned messages gated on a negotiated feature instead of raw `ctx.Net` packets, and the plugin is version 1.0.0 with a declared range: built and game-free tested, and verified live 2026-10-10** (both parts on the isolated test server and the Steam client): `time.request` and `time.answer` went both ways and the recorded request matches the checked-in fixture ([results](../../docs/mod-compatibility.md#c5-live-test-2026-10-10)). The clock popup and the 12-hour switch were not checked. The earlier live results above predate that change.

## What it does

- **Client part (the mod).** A clock. **Ctrl+F6** (rebindable) opens a small popup titled `Clock` with your local time, and the mod page shows it live. Settings: the clock format and seconds. Works on any server, with or without Progenitor.
- **Server part (optional, tiny).** Offers the feature `time` and answers the client's versioned message `time.request` (client to server) with one read of the world clock (`ctx.Time.GetTimeOfDay()`) and the server's uptime (`time.answer`, server to client), at most once a second per player. It also holds the server's default clock format. It changes nothing in the world.
- **With the server part** the clock adds the in-game time and the next sunrise or sunset (`In-game: Day 12, 14:30 · sunset in 6 min.`), estimated between answers at the server's day length, and `Server default` follows the server's setting. The client asks only while the joined server's part offers `time` (`ctx.Peer.Has("time")`) and the join is accepted: every 30 s, and on a key press (the popup waits up to 1.5 s for a fresh answer). `Server default` still follows the handshake's view of the server part (`ctx.Handshake.ServerPart`), because it comes from a synced setting, not from a message.

Source: [Plugin.cs](Plugin.cs) (game wiring), [ClientLedMessages.cs](ClientLedMessages.cs) (the two messages and the feature name; game-free, linked into the replay tests), [ClientLedPolicy.cs](ClientLedPolicy.cs) (declaration, admission, the clock rules and lines; game-free), [ClientLedSettings.cs](ClientLedSettings.cs) (the entries and the mod page; game-free).

## Declaration

```csharp
[BepInPlugin("progenitor.sampleclientled", "Client-led sample", "1.0.0")]
[NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor, Peers = ">=1.0.0 <2.0.0")]
[Counterpart(CounterpartNeed.Limited, Features = new[] {
    "The server's in-game time and next sunrise or sunset", "The server's default clock format" })]
```

(`Plugin.cs` writes these through the constants in `ClientLedDeclaration`.) Neither side requires the other; two sides that both run it work together at any two 1.x versions (a Progenitor older than ranges holds them to the same major.minor). `[Counterpart(Limited)]` makes the mod page two-sided, says `limited` on a server without the server part and grays the rows marked `needsServerPart`.

**Why not `ServerMustHave` + `Limited`.** One attribute speaks for both sides of the one DLL. On the server, `Limited` does not lower a `ServerMustHave` row (only `Optional` or `ClientExtras` do; see [counterparts](../../docs/mod-settings.md#counterparts)), so a server running this tiny server part would refuse every client without the client part. `NoNeedForSync` states the real rule directly.

## Join matrix

"Server: none" is a Progenitor server that runs at least one other mod but not this one, so its handshake request is known not to name it. The status bar is the client's Mods page. Other mods' own rows can still refuse a client; this table is about this mod's row only.

| Client \ Server | none | server half |
|---|---|---|
| **none** | Join allowed. Nothing paused. No page for this mod. | Join allowed: the server's row is `NoNeedForSync`, so a Progenitor client without the mod passes and a vanilla client is not kicked for it. Nothing paused. Nothing is sent to that player. |
| **client half** | Join allowed. Not paused (the row is `NoNeedForSync`). The clock works with local time. Grayed: **Show server time** (fallback `Uses your local estimate: the clock shows your local time only.`) and **Default format** (also locked; fallback `Without the server part, Server default means 24-hour.`), each with `Needs the server part of this mod.` **In-game time** reads the fallback line. Status bar: `Enabled` · `Client and server` · `Server part not on this server · limited`. | Join allowed when the newer side's range includes the older version: any two 1.x builds (a 2.x against a 1.x is refused). Not paused. Nothing grayed. An administrator edits **Default format** through the request path; a non-administrator's **Apply** is refused by the server. Status bar: `Enabled` · `Client and server` · `Server part running`. |

On a server without Progenitor, or one whose request is empty or filled all 16 wire slots, the server part is unknown: nothing is refused or paused, the server-part cell is omitted, and the same two rows are grayed with `Needs the server part of this mod.` (`Server doesn't run Progenitor.` on a server without it)

## Settings

| Section | Setting (cfg key) | Where it lives | Scope and path | Needs the server part |
|---|---|---|---|---|
| Clock (works on any server) | Format (`[Clock] Format`: `Server default` / `24-hour` / `12-hour`) | Client cfg; your choice overrides the server's default | `scope: Local` | No |
| | Show seconds (`[Clock] ShowSeconds`) | Client cfg | `scope: Local` | No |
| | Show the clock (keybind `ShowClock`, Ctrl+F6) | Client cfg, `[Keybinds]` | Local | No |
| | Local time (info line) | Live | — | No |
| With the server part | Show server time (`[Clock] ShowServerTime`) | Client cfg | `scope: Local` | Yes. Fallback: `Uses your local estimate: the clock shows your local time only.` |
| | Default format (`[Server] DefaultFormat`: `24-hour` / `12-hour`) | Server cfg; synced to clients with the client part. The server-provided default a client can override with **Format**. | `scope: Server`: administrators edit it from the page while joined (request path); otherwise locked | Yes. Fallback: `Without the server part, Server default means 24-hour.` |
| | Server part (info line) | Live, from `ctx.Handshake.ServerPart` | — | No |
| | In-game time (info line) | Estimated from the last answer, or the fallback line | — | Shows the fallback without it |

The server binds only **Default format**. It builds the page too, with that one row, because that call is what opts the synced entry into administrator edits.

## Compatibility across versions

The server part offers `time`; the client sends `time.request` (v1: `Sequence`) only to a server that offers it, and otherwise shows its local estimate. The server answers with `time.answer` (v1: `Sequence`, `Day`, `Hour`, `DayLengthSeconds`, `SunriseHour`, `SunsetHour`, `UptimeMinutes`).

**Version and range.** `[BepInPlugin]` carries the literal `1.0.0` instead of the repository's date version (allowed by [versioning.md](../../docs/versioning.md)), so the compatibility contract applies: a new field or feature is a minor version, a removed or retyped one (or a raised `MinReaderVersion`) a major version. `Peers = ">=1.0.0 <2.0.0"` is honest because the two messages are the only thing that crosses the wire, both skip fields they do not know, and the client asks only a server that offers `time`. A build from before this change (a date version such as `2026.10.5`, raw `ClTq`/`ClTa` packets) is outside every 1.x range and is refused as a version mismatch.

**Replay test.** `tests/Progenitor.Testing.Tests/Samples/Progenitor.SampleClientLed/ClientLedReplayTests.cs` links `ClientLedMessages.cs` as source and checks the fixtures in `Fixtures/1.0.0/` (`time.request@v1`, `time.answer@v1`): both replay through today's types with every value intact, also with unknown fields added, and `MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.0.0", …, allowSelfOnly: true)` passes as **not proven**: 1.0.0 is the first release, so no older version backs the range yet. The fixtures were written by the test (`source: "test"`), not recorded live. Run:

```
dotnet test tests/Progenitor.Testing.Tests --filter "FullyQualifiedName~Progenitor.SampleTests.ClientLed"
```

A change to a message fails `The_fixtures_are_what_1_0_0_sends`. The fixtures carry each message's field schema. A released version's fixtures are frozen: `PROGENITOR_WRITE_FIXTURES=1` writes only a version that has no folder yet (`MessageFixtures.SaveRelease` refuses an existing one), so a changed message is a new version, recorded beside the old one, and the next version's `CheckRange` replays the old fixtures.

Status: built and game-free tested; not verified live.

## Build and deploy

```powershell
dotnet build samples/Progenitor.SampleClientLed -c Debug
./tools/deploy.ps1 -Projects Progenitor.SampleClientLed -Side client -NoBuild   # the mod
./tools/deploy.ps1 -Projects Progenitor.SampleClientLed -Side server -NoBuild   # the optional server part
```

It is not in deploy's default project list; `-Projects` finds it under `samples/`. `-NoBuild` copies `bin/Debug` ([dev-loop.md](../../docs/dev-loop.md#the-three-pieces)). Both copies go to `BepInEx/progenitor-reload` and hot-load. Check that the target client is free before a client deploy.

## Acceptance procedure — not run

Isolated test server (`<testserver>`) only, never the live server. Requires a Core with `ctx.Handshake.ServerPart` on the client. Expectations are written as expectations, not captured output.

1. **Client part only, offline.** Deploy to the client. Open the Mods workspace, select **Client-led sample**. `mods_ui widget_read pane=content`: sections `Clock (works on any server)` and `With the server part`; **Format**, **Show seconds** enabled with `scopeDeclared: local`; **Show server time** and **Default format** `greyed: true` with `Not connected to a server.` and their fallbacks; **Local time** advancing. Press Ctrl+F6 in game (foreground test; user-authorized only): a popup `Clock` with `Local time …` and the fallback line.
2. Join the test server while it runs a Progenitor mod but not this one. Expected: the join succeeds and the sample is not paused; status bar `Server part not on this server · limited`; the two rows grayed with `Needs the server part of this mod.`; the client sends no `time.request` message.
3. Leave. Deploy the server part to the test server. Expected server log: `Client-led sample: server part loaded; answering clock requests.`
4. Join again. Expected: `Server part running`, nothing grayed; **Server part** reads `Running on this server`; within 30 s **In-game time** reads `Day N, HH:MM · sunrise|sunset in X min` and advances. Ctrl+F6: the popup adds `In-game: …`.
5. Set **Format** to `Server default`. As an administrator set **Default format** to `12-hour` and **Apply**: the server cfg changes, and **Local time** and **In-game time** switch to 12-hour. Set **Format** to `24-hour`: the client's own choice wins.
6. Join with a client that does not have the sample: the join succeeds and nothing is sent to it.

Cleanup: remove the sample from both trees or leave it disabled, restore the test server's plugin set, leave the client at the title screen.

## Known limits

- The clock is a popup (the framework notice dialog) and info lines on the mod page, not a HUD line: the framework has no HUD line or toast primitive yet.
- The in-game time between answers is an estimate at the server's day length; a time-of-day override or a stopped clock on the server shows at the next answer.
- The client cannot tell whether its player is an administrator, so a non-administrator sees the request path for **Default format** and learns only from the server's refusal.
