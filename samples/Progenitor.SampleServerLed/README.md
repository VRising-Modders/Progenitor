# Server-led sample

`Progenitor.SampleServerLed` (`progenitor.sampleserverled`) is a mostly-server mod with an optional client part. It is the reference example for a server mod whose client part is optional, and for the three kinds of setting such a mod has: server options for the server, client options for the server, and client options for the client.

**Status:** source-checked, game-free tested, and partly live-verified (below). Release build compiled; `tests/Progenitor.Tests/CounterpartSamplesTests.cs` covers the declarations, the join matrix, the settings scopes and grayed rows, and the server's per-player preferences. **Live (2026-09-29, user's Steam client + isolated test server, commits ad5d6682 and 95b2c2d0):** client half with no server half: join allowed, nothing paused, server-dependent rows grayed, status bar `Server part not on this server · limited`; both halves: `Server part running`, nothing grayed, the server logged the client's preference packet (`preferences from TestPlayer receive=on delivery=Chat`) and handled `.serverled status`; client half switched off with the server half present: join allowed, nothing refused; the page shows the right server-part fact right after `Handshake: ok` and keeps it without a reselect. Not observed live: announcement delivery (chat vs popup), the heartbeat fallback, non-admin refusal, and the Ctrl+F6 clock popup. **Since 2026-10-10 (mod compatibility C5) the two parts talk through versioned messages and negotiated features instead of raw `ctx.Net` packets, and the plugin is version 1.0.0 with a declared range: built and game-free tested, and verified live 2026-10-10** (both parts on the isolated test server and the Steam client): the `prefs` message reached the server (`preferences from <player> receive=on delivery=Popup.`), announcements arrived as a popup (`chat=0 popup=1`) and, after the client part was removed, as chat (`chat=1 popup=0`); the recorded payloads match the checked-in fixtures ([results](../../docs/mod-compatibility.md#c5-live-test-2026-10-10)). Not observed: `.serverled announce` (needs an administrator). The earlier live results above were observed with the raw packets.

## What it does

- **Server part (the mod).** Every `[Server] AnnounceMinutes` minutes (1–120, default 10) the server announces to every connected player: `<greeting> · N players online · server up X min.` `.serverled status` tells the caller what the server announces and which preferences it uses for them. `.serverled announce` (administrators) announces now. It spawns nothing and changes nothing in the world.
- **Client part (optional).** Sends this player's preferences to the server as the versioned message `prefs` (client to server), only while the joined server's part offers the feature `prefs` (`ctx.Peer.Has("prefs")`) and the join is accepted. It re-sends them on a change and every 60 s as a heartbeat, so a server whose sample was reloaded learns them again, and sends a goodbye (`prefs` with the gone kind) when it unloads (switched off, paused, reloaded) while it can still send. It offers the feature `popups` and shows popup announcements (the message `announce`, server to client), which carry the chat line's exact text. The server sends that message only to a player whose client offers `popups` (`ctx.Peers.Of(user).Has("popups")`) and whose client part reported preferences in the last 150 s (two and a half heartbeats); otherwise, and whenever the send does not go out, that player gets the chat line. After a goodbye, or 150 s without a message (a client part that failed to reload or vanished), that player is back on the server defaults.
- **Without the client part** a player gets every announcement as a system chat line. Vanilla clients and Progenitor clients without this mod join normally.

Source: [Plugin.cs](Plugin.cs) (game wiring), [ServerLedMessages.cs](ServerLedMessages.cs) (the two messages and the feature names; game-free, linked into the replay tests), [ServerLedPolicy.cs](ServerLedPolicy.cs) (declaration, validation, the per-player table, the schedule and every line; game-free), [ServerLedSettings.cs](ServerLedSettings.cs) (the entries and the mod page; game-free).

## Declaration

```csharp
[BepInPlugin("progenitor.sampleserverled", "Server-led sample", "1.0.0")]
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor, Peers = ">=1.0.0 <2.0.0")]
[Counterpart(CounterpartNeed.Optional, ClientExtras = "...", Features = new[] { "Server announcements" })]
```

(`Plugin.cs` writes these through the constants in `ServerLedDeclaration`.) `Optional` with `ClientExtras` lowers the row to `NoNeedForSync` on both sides ([counterparts](../../docs/mod-settings.md#counterparts)): the server never requires the client part, and the client part never requires the server part. Two sides that both run it work together at any two 1.x versions; a Progenitor older than ranges holds them to the same major.minor (`Minor`).

## Join matrix

"Server: none" is a Progenitor server that runs at least one other mod but not this one, so its handshake request is known not to name it. The status bar is the client's Mods page; the server has none. Other mods' own rows can still refuse a client; this table is about this mod's row only.

| Client \ Server | none | server half |
|---|---|---|
| **none** | Join allowed. Nothing paused. No page for this mod. | Join allowed: the server's row is `NoNeedForSync`, so a Progenitor client without the mod passes and a vanilla client is not kicked for it. Nothing paused. The player gets announcements as chat lines. |
| **client half** | Join allowed. Not paused (the client row is `NoNeedForSync`). Grayed: both server options (also locked: `Set by the server. Join a server to see its value.`), both client options for the server, and **Popup seconds** and **Log announcements**, each with `Needs the server part of this mod.` and its fallback. **Join check** and the **Server part** line stay live. Status bar: `Enabled` · `Client and server` · `Server part not on this server · limited`. | Join allowed when the newer side's range includes the older version: any two 1.x builds (a 2.x against a 1.x is refused). Not paused. Nothing grayed. An administrator edits the server options through the request path; a non-administrator's **Apply** is refused by the server (`Only server administrators can change this setting.`). Status bar: `Enabled` · `Client and server` · `Server part running`. |

On a server without Progenitor, or one whose request is empty or filled all 16 wire slots, the server part is unknown: nothing is refused or paused, the server-part cell is omitted, and the same rows are grayed with `Needs the server part of this mod.` (`Server doesn't run Progenitor.` on a server without it)

## Settings

| Section | Setting (cfg key) | Where it lives | Scope and path | Needs the server part |
|---|---|---|---|---|
| Server options (for the server) | Announce minutes (`[Server] AnnounceMinutes`, 1–120) | Server cfg; synced to clients with the client part | `scope: Server`: administrators edit it from the page while joined (request path); otherwise locked | Yes. Fallback: `This server does not run the server part, so there is nothing to set here.` |
| | Greeting (`[Server] Greeting`) | Server cfg; synced | `scope: Server`, as above | Yes, same fallback |
| Client options for the server | Receive announcements (`[ForServer] ReceiveAnnouncements`) | Client cfg; sent to the server, which honors it for this player | Local file, no declared scope | Yes. Fallback: `Without the server part there are no announcements, so this is not sent.` |
| | Delivery (`[ForServer] Delivery`: `Chat` / `Popup`) | Client cfg; sent to the server | As above | Yes, same fallback |
| Client options for the client | Popup seconds (`[Client] PopupSeconds`, 0–60; 0 keeps the popup until OK) | Client cfg | `scope: Local` | Yes. Fallback: `Announcements come only from the server part, so nothing is shown.` |
| | Log announcements (`[Client] LogAnnouncements`) | Client cfg | `scope: Local` | Yes, same fallback |
| | Join check (`[Client] JoinCheck`): log whether each joined server runs the server part | Client cfg | `scope: Local` | No: works on any server |
| | Server part (info line): `Running on this server` / `Not on this server` / `Unknown …` | Read live from `ctx.Handshake.ServerPart` | — | No |

The server binds only the two server options. It builds the page too, with only the first section, because that call is what opts the synced entries into administrator edits.

## Compatibility across versions

| | Server part | Client part |
|---|---|---|
| Offers | `prefs` (it reads preferences) | `popups` (it shows announcements) |
| Sends | `announce` (v1: `Sequence`, `Text`) only to a client that offers `popups`; otherwise, or when the send returns anything but `Sent`, a chat line | `prefs` (v1: `Kind`, `Receive`, `Delivery`) only to a server that offers `prefs` |

**Version and range.** `[BepInPlugin]` carries the literal `1.0.0` instead of the repository's date version (allowed by [versioning.md](../../docs/versioning.md)), so the compatibility contract applies: a new field or feature is a minor version, a removed or retyped one (or a raised `MinReaderVersion`) a major version. `Peers = ">=1.0.0 <2.0.0"` is honest because nothing else crosses the wire: both messages skip fields they do not know, a value the server does not define (a new kind or delivery) is refused rather than guessed, and each side asks for the other's feature before sending. A build from before this change (a date version such as `2026.10.5`, raw `SlPr`/`SlAn` packets) is outside every 1.x range and is refused as a version mismatch rather than half-working.

**Replay test.** `tests/Progenitor.Testing.Tests/Samples/Progenitor.SampleServerLed/ServerLedReplayTests.cs` links `ServerLedMessages.cs` as source and checks the fixtures in `Fixtures/1.0.0/` (four `prefs@v1`, one `announce@v1`): every one replays through today's types and the server's value check, also with unknown fields added; every message skips unknown fields; and `MessageCompat.CheckRange(">=1.0.0 <2.0.0", "1.0.0", …, allowSelfOnly: true)` passes as **not proven**: 1.0.0 is the first release, so no older version backs the range yet. The fixtures were written by the test (`source: "test"`), not recorded live. Run:

```
dotnet test tests/Progenitor.Testing.Tests --filter "FullyQualifiedName~Progenitor.SampleTests.ServerLed"
```

A change to a message fails `The_fixtures_are_what_1_0_0_sends`. The fixtures carry each message's field schema. A released version's fixtures are frozen: `PROGENITOR_WRITE_FIXTURES=1` writes only a version that has no folder yet (`MessageFixtures.SaveRelease` refuses an existing one), so a changed message is a new version, recorded beside the old one, and the next version's `CheckRange` replays the old fixtures.

Status: built and game-free tested; not verified live.

## Build and deploy

```powershell
dotnet build samples/Progenitor.SampleServerLed -c Debug
./tools/deploy.ps1 -Projects Progenitor.SampleServerLed -Side server -NoBuild   # the server part
./tools/deploy.ps1 -Projects Progenitor.SampleServerLed -Side client -NoBuild   # the optional client part
```

It is not in deploy's default project list; `-Projects` finds it under `samples/`. `-NoBuild` copies `bin/Debug` ([dev-loop.md](../../docs/dev-loop.md#the-three-pieces)). Both copies go to `BepInEx/progenitor-reload` and hot-load. Check that the target client is free before a client deploy.

## Acceptance procedure — not run

Isolated test server (`<testserver>`) only, never the live server. Requires a Core with `ctx.Handshake.ServerPart` on both sides. Expectations are written as expectations, not captured output.

1. **Server part only.** Deploy to the server only; set `[Server] AnnounceMinutes = 1`. Expected server log: `Server-led sample: server part loaded; announcing every 1 min. …` and the lowered-row line `…: [Counterpart(Optional)] runs without its counterpart, so its handshake row is declared NoNeedForSync …`.
2. Join with a client that does not have the sample. Expected: the join succeeds; within a minute a system chat line `Welcome. This server runs the server-led sample. · 1 player online · server up …`; server log `announcement 1 sent: chat=1 popup=0 skipped=0.`. `.serverled status` replies with `server defaults: your client part has not reported`.
3. Leave. Deploy the client part; open the Mods workspace, select **Server-led sample** offline. `mods_ui widget_read pane=content`: sections `Server options (for the server)`, `Client options for the server`, `Client options for the client`; every row except **Join check** has `needsServerPart: true` and `greyed: true` with `Not connected to a server.`; the two server options have `scopeDeclared: server`.
4. Join the server. Expected: client log `Server-led sample: this server runs the server part; …`; server log `preferences from <name> receive=on delivery=Chat.`; the page's status bar `Server part running` and no row grayed; **Server part** reads `Running on this server`.
5. Set **Delivery** to `Popup`. Expected within about 3 s: server log `… delivery=Popup.`; at the next announcement (or `.serverled announce` as an administrator) a popup titled `Server announcement`, closed after **Popup seconds**; server log `chat=0 popup=1`. Turn **Receive announcements** off: `skipped=1` and nothing shown.
6. As an administrator, change **Announce minutes** on the page and press **Apply**: the server cfg changes and the help line shows the server's answer. As a non-administrator: the server refuses with `Only server administrators can change this setting.`
7. Remove the server part from the test server and join again with the client part. Expected: the join succeeds, the sample is not paused, and no `prefs` message is sent (no server log line). What the client can tell depends on the server's handshake request: **if the test server still runs at least one other Progenitor mod** (a non-empty request that does not fill 16 slots), the part is known absent — client log `… does not run the server part, so it announces nothing.`, status bar `Server part not on this server · limited`, the rows from step 3 grayed with `Needs the server part of this mod.` and their fallback. **If it runs no other Progenitor mod** (an empty request) or none at all, the part is unknown — no join-check line, no server-part cell, and the rows grayed with `Needs the server part of this mod.` (`Server doesn't run Progenitor.` on a server without it)
8. With both parts running and **Delivery** `Popup`, switch the client part off on its page. Expected server log `<name>'s client part unloaded; using the server defaults.` and the next announcement as a chat line (`chat=1 popup=0`).

Cleanup: remove the sample from both trees or leave it disabled, restore the test server's plugin set, leave the client at the title screen.

## Known limits

- The client cannot tell whether its player is an administrator, so a non-administrator sees the request path for the server options and learns only from the server's refusal. Locking those rows for non-administrators needs a framework change (a client-side admin fact).
- The server keeps preferences per connection slot for the session only. After a server-side reload it uses the defaults until each client's next heartbeat (at most 60 s). A client part that disappears without its goodbye (a crash, a failed reload) is dropped after 150 s; announcements in that window still go to Popup and are not shown.
- The join-check line is written once per join, keyed on the client world (`ClientWorldEpoch.ObservedSequence`) and remembered process-wide, so a Core swap or plugin reload does not repeat it.
- The popup is the framework notice dialog, not a HUD element: the framework has no HUD line or toast primitive yet, so a popup takes the cursor while it is open.
