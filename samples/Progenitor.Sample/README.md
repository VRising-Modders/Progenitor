# Progenitor.Sample

`Progenitor.Sample` (`progenitor.sample`) is the framework's broad example plugin: one DLL for both sides that exercises most of the API. Its chat commands are listed in [bridge-ops.md](../../docs/bridge-ops.md#sample-commands).

## The debug buff is part of Core now

The admin debug buff that used to live here (`.nodrain on|off|status`, buff `sample.nodrain`) and its map powers (revealed map, hold Middle Mouse over the map to teleport, `.maptp <x> <z>`) moved into Progenitor Core on 2026-10-06. Use Core's built-in command instead:

| Was (Sample) | Now (Core) |
|---|---|
| `.nodrain on` / `.nodrain off` / `.nodrain status` | `.debug on` / `.debug off` / `.debug` (also `.debug on <player>` for another online player) |
| `.maptp <x> <z>` | `.debug tp <x> <z>` |
| `[DebugMap]` in `progenitor.sample.cfg` | `[DebugMap]` in Core's cfg files (cfg-only) |
| `[Keybinds] TeleportToMapPoint` (Middle Mouse) in the Sample's cfg | The game's own admin teleport, Ctrl+Shift+click on the map (authenticated admins); otherwise Core keybind `TeleportToMapPoint`, unbound by default (Keybinds tab, under Progenitor) |

Behavior, settings, persistence rules and status are in [Debug tools](../../docs/modules.md#debug-tools-debug); the live acceptance procedure (not run) is in the [development loop](../../docs/bridge-ops.md#core-debug-tools-debug).

**Updating from a Sample that still has `.nodrain`.** Core migrates the Sample's grant ledger once (so an `Immortal` it granted is still taken back, and one it did not grant is still left alone), but not the buff: run `.debug on` with the new Core. `.debug off` removes a leftover `sample.nodrain` buff. The Sample's `.godmode` command (a kill-path guard without a buff) is unchanged and still keeps its own owner, separate from `.debug`.
## Map keys

`SampleMap.cs` is the plugin example for map-scope keybinds (`ctx.Keybinds.RegisterInMap` / `RegisterHoldInMap`) and the `WorldMap` pointer: a client-only map ruler. Both keys work only while the game's world map is open, appear in the Keybinds tab with the "works only while the map is open" note, and can be remapped (`[Keybinds] MapPoint` and `MapMeasure` in the Sample's cfg). Results go to the client log as `Sample map: …` and to the Ctrl+F9 panel; nothing is sent to the server.

| Key (default) | Does |
|---|---|
| `Ctrl+C` (`MapPoint`, `RegisterInMap`) | Logs the world X/Z under the cursor, or why there is none (pointer off the map). |
| hold `Ctrl+R` (`MapMeasure`, `RegisterHoldInMap`) | The press marks the point under the cursor, each held frame follows the cursor while it is on the map, and the release logs the straight-line distance in meters with dx/dz. Closing the map before releasing cancels. |

Status: source-checked, built and game-free tested (`SampleMapTests`), **not live-verified**.

**Acceptance procedure (not run).** On a test client in a world with this Sample loaded: open the map, point at a spot and press Ctrl+C; the client log shows `Sample map: point x=… z=…`. Hold Ctrl+R, move the cursor across the map and release; the log shows `Sample map: measured … m from (…) to (…); dx=… dz=…`. Hold Ctrl+R and close the map before releasing; the log shows `Sample map: measure canceled (map closed)`. With the map closed neither chord logs anything. The Keybinds tab lists both under the Sample with the map-only note.

## Opening the Example page, and the game's look

`SampleMenu.cs` is the plugin example for `ctx.Menu.OpenPage` and `UiLook`. `Shift+F9` (`[Keybinds] OpenExamplePage`, client only) calls `ctx.Menu.OpenPage("Example")`: the Mods menu opens the way its own key opens it (in a world through the game's menu, on its MODS tab), then shows the Sample's Example page. The Example page's status line is a tone label (`panel.Label(text, LabelTone.Muted)`, then `SetText(…, LabelTone.Ok)` on the action), and its **Try an action** button is drawn as the game's standard button (`panel.Button(…).GameStyle()`).

Status: source-checked, built and game-free tested (`ModPageOpenTests`, `UiLookTests`), **not live-verified**.

Shift+F9, F9 and Ctrl+F9 are registered with `ctx.Keybinds.RegisterOpener` (they only open or close the Sample's own UI), so they work at the title screen as well as in a world ([Keybinds](../../docs/modules.md#keybinds-fui5)).

**Acceptance procedure (not run).** On a test client in a world with this Sample loaded and the game's menu closed, press Shift+F9; the game's menu opens on its MODS tab with the Example page showing (not the spellbook page), and the client log has a workspace transition with `reason=mod-page`. On the page, **Try an action** has the steel game-button face, and pressing it turns the status line green.

## The F9 window: a floating window in the game's look

`SampleUi.cs` (F9, `[Keybinds] ToggleDemoPanel`) opens the Sample's floating window with `UiLook.GameWindow()`: the game's popup frame, and the Mods page's grid inside. It shows each kind of control a window can hold: an inline row of the game's compact buttons (**Log click**, **Close**) and a button that asks first (`ConfirmButton`); read-only rows (a note, a fact, a live `Info` readout of the ember stack, a colored status line, a divider); pickers (a number, a color, a date-time and a key field), each reporting to the status line and the log (`Sample: ui picked …`); and the demo toggle. Built and game-free tested (`SampleUiTests`); **not live-verified**.

## Facade samples

These commands are the author-facing examples for facades that previously had no Sample call, or only a call buried in another plugin. Each one calls the real API. `.triggers` was run on the isolated test server on 2026-10-08. The others have not been run in the game.

| Command | Facade | File |
|---|---|---|
| `.items grant` / `.items equip` | `ctx.Items` | `SampleItems.cs` |
| `.stations list` / `recipes` / `toggle` / `status` | `ctx.Stations` | `SampleStations.cs` |
| `.drops near` / `take` / `log` / `prefabs` | `ctx.Drops`, `ctx.Inventory.OnGroundDrop` / `OnDropAll` | `SampleDrops.cs` |
| `.protect on\|alive\|off` | `ctx.Debug` | `SampleDebugProtect.cs` |
| `.players` | `ctx.Players` | `SamplePlayerDirectory.cs` |
| `.shot` (`preview [hold]` / `hold` / `end` pair the server `Observe` lease with the client `Preview`) | `ctx.Camera` | `SampleCamera.cs` |
| `.form` | `PlayerForms` (experimental, not a `ctx` property) | `SampleForms.cs` |
| `.triggers on\|off\|status` | `ctx.Triggers` | `SampleTriggers.cs` |

Status: source-checked. Game-free tests are `SampleAuthorFacadeTests`. **Not live-verified.** `.form begin` is expected to be refused until a form profile is qualified; the sample still calls `TryBegin` and tracks any handle. `.shot` results are client log lines (`Sample camera:`), because the shot calls throw on the server; `.shot preview` also logs the server lease (`server lease ended: …`).

## Feature negotiation (`.compat`)

`SampleCompat.cs` is the plugin example for `ctx.Features`, `ctx.Peer` and `ctx.Peers` ([modules.md](../../docs/modules.md#feature-negotiation-and-versioned-messages-mod-compatibility-c1c2)). The client offers the feature `camera`, because it is the side that applies the `.shot` packet. `.compat` (any player) runs on the server and replies with what the server's Sample offers and the caller's client as the server sees it (`ctx.Peers.Of(user)`: Progenitor version, this mod's version, `camera`). It then sends the client a versioned message (`compat.report`, `ctx.Messages`), and the client logs what it offers and the joined server as it sees it (`ctx.Peer`) as `Sample compat: …` lines (client log and the Ctrl+F9 panel). If that send does not go out, the reply names the `SendResult`.

The Sample's other packets stay raw `ctx.Net` on purpose: the Sample is declared `Exact`, so both sides always run the same build and a fixed-size struct cannot be misread. It declares no `Peers` range for the same reason.

Status: source-checked, built and game-free tested (`SampleCompatTests`), **not verified live**.

**Acceptance procedure (not run).** With the Sample on the test client and the isolated test server, joined: `.compat` replies `This server's Sample offers no features.`, `Your client, as this server sees it: Progenitor <version>; this mod <version>; camera yes.` and `Asked your client for its view; …`; the client log shows `Sample compat: this client offers camera=1.` and `Sample compat: the joined server: Progenitor <version>; this mod <version>; camera no.`

## Requests and subscriptions (`.request`, `.subscribe`)

`SampleExchange.cs` is the plugin example for `ctx.Requests` and `ctx.Subscriptions` ([modules.md](../../docs/modules.md#requests-large-messages-and-subscriptions)). Its message types are in `SampleExchangeMessages.cs`, which `tests/Progenitor.Testing.Tests` links for its round-trip and replay tests. Chat commands run on the server, so each command asks the caller's client to act with a versioned message (`exchange.start`). The client acts and logs `Sample exchange: …`, and its result comes back to the player as a whisper.

- **`.request [kib]`** (default 64, range 1 to 1000). The client sends `exchange.blob.get`. The server's handler answers with that many deterministic bytes and their checksum; 64 KiB takes five packets. The handler allows a burst of 3 requests, then one every two seconds. The client checks the checksum.
- **`.subscribe start|stop [key]`** (default key `lobby`). `start` subscribes the client to the server's `board` topic for that key. The client logs the snapshot (padded to two packets) and every line posted after it. `stop` unsubscribes.
- **`.subscribe post [@key] <text>`** appends a line and publishes it to every subscriber. **`.subscribe status [key]`** shows the board and its subscribers.
- Keys come from players, so the Sample bounds what it keeps for them: only a post creates a board, at most 16 boards exist, and a subscribe or `status` for an unknown key sees an empty board without creating one. Copy that pattern.

Status: source-checked, built and game-free tested (`SampleExchangeTests`, `SampleExchangeRoundTripTests`), **not verified live**. Acceptance procedure: [modules.md](../../docs/modules.md#live-acceptance-procedure-not-run).
