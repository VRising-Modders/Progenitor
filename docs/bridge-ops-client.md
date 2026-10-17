# Bridge operations: input, install and session

Operation reference for the [development bridge](bridge.md), part 5 of 5: keybinds, map, the game's console, keyboard, mouse and gamepad input, the `bot` interaction, build search, material sources, dye, local and remote installs, disconnect, quit and auto-update. Other parts: [core, entities and samples](bridge-ops.md), [players, world and capture](bridge-ops-player.md), [Mods workspace UI](bridge-ops-ui.md), [server info and join flow](bridge-ops-join.md). Part of the [development loop](dev-loop.md).

## `bot` interaction

**Experimental development tooling**, client, main thread; part of the Dev `bot` op ([tools/bots/README.md](../tools/bots/README.md), [input.md](input.md)). Not a framework API.

`{"action":"interact","entity":"index:version"}` asks for one ordinary interaction with a client-world entity that has `Interactable` and a `NetworkId` (a workstation, a prison cell, a container). It closes the open HUD menu, hovers and aims at the entity through the bot's input lease, then presses the synced Interact button for 0.4 s on the third frame. It never moves the OS mouse and never brings the game to the foreground. The character must already be in interaction range: about 1.7 m from the target worked live (2026-10-14), while 3 m gave `not confirmed`; move closer first. Side and angle matter too: a crafting bench took the interaction only from its south side, at 1.4 m (live 2026-10-14); when a request is `not confirmed` within range, approach from another side.

| Field of `bot` `{"action":"status"}` | Meaning |
|---|---|
| `interaction` | `pending: …` while the request runs; `input completed; client target=…; server confirmation required` when the local character's `Interactor.Target` became the entity; `not confirmed: …` when the entity disappeared or 3 s passed (out of range, blocked, refused); `failed to initialize interaction` when the hover or aim could not be set; `none` before any request. |

Every completed, timed-out or failed request releases what it held; `{"action":"stop"}` cancels one. **The result is the client's prediction, not the server's decision:** before crafting or moving items, read the server character's `Interactor.Target` (`get` on the server, component `Interactor`) and match it to the intended server entity by `NetworkId` (use `query` on each side; entity indices differ between the client and the server). A crowded spot can make the server pick a nearer interactable than the client predicted.

Observed on 2026-10-13: a workstation and a prison cell opened through `interact` and the server's `Interactor.Target` named them; a nearby container did too; in a crowded spot the server chose a nearer container than the client predicted; an out-of-range request timed out and released.

## `keybind_press`

Client, main thread. Presses a registered keybind without the keyboard or window focus. The dispatch is Core's `Keybinds.SimulatePress`, the path a real key-down takes: chat and text-field suppression, panel capture and winner enforcement all apply, so a bind that would not fire for the player does not fire here. Refuses on the server. No MCP wrapper; use `call.mjs`.

| Arguments | Meaning |
|---|---|
| `{"bind":"<id>","plugin":"<guid>"}` | Press one bind by id. `plugin` is the owning plugin GUID; pass it when two plugins use the same id. |
| `{"bind":"<id>","plugin":"<guid>","hold":<frames>}` | Press, hold for `frames` frames (0-600) running the while-held handler each frame, then release (`Keybinds.SimulateHold`). A hold-only bind reports `fired: false` with `no press handler: …` to a plain press. |
| `{"chord":"F10 + Ctrl"}` | Press a chord and let the registry decide which binds it reaches. Uses the `KeyChord` format of the `[Keybinds]` cfg entries, mouse buttons included (`{"chord":"Mouse0 + Ctrl + Shift"}`; a left or right click chord reports `suppressed: the pointer is over a Progenitor window, …` or `suppressed: the game's menu is open, …` when a real click would not fire). |

Pass exactly one form. The reply is `{request, fired, dispatches:[{plugin, bind, chord, fired, reason}]}`: `fired` counts handlers that ran, and `reason` is empty for a fired bind and otherwise states why it did not fire (for example `suppressed: chat focused` or `unknown bind`).

```powershell
$bridgeArgs = Join-Path $env:TEMP 'keybind-press.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"chord":"F10"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client keybind_press "@$bridgeArgs"
```

Source-checked against `src/Progenitor.Dev/Ops/KeybindPressOps.cs`; not yet run live.

## `map`

Client, main thread. Opens, closes or reads the game's world map (the full-screen menu's Map tab) with no input and no window focus. It makes the calls the game's own M key ends in: `HUDMenuManager.GoToMenu(FullscreenMenu)` to raise the menu, then `FullscreenMenuMapper.SetState(Map)` (what M does inside the menu), and `HUDMenuManager.CloseMenu` to close (what M does with the map shown). It does not call the key's generic `GoToMenu<FullscreenMenu.InitData>` overload, and it plays no menu sound. No MCP wrapper; use `call.mjs`.

| Arguments | Does |
|---|---|
| `{"action":"status"}` | Reads the state in the same frame. |
| `{"action":"open"}` | Opens the map: raises the menu from gameplay, or switches to the Map tab from another tab. Idempotent: with the map open it replies `ok true` and `why: already: the map is open`. |
| `{"action":"close"}` | Closes the map and the menu. With the map not shown it does nothing: `why` is `already: the map is not open` or `not-map: …` when the menu is open on another tab, which it leaves alone. |

`open` and `close` reply once Core's own map detection (`WorldMap.IsOpen`, the state map-scope keybinds use) has agreed for 4 frames, or with `ok false` and the state after 180 frames. The reply is `{ok, action, open, changed, step, before, after, framesWaited, readback, ms, handler?, setStateCalls?, why | error}`. `before` and `after` hold `inWorld`, `hudMenu`, `menuOpen`, `tab`, `view`, `workspaceOpen`, `probe` and `mapOpen`.

Refusals (`ok false`, `error`): `no-world` (title screen or loading), `no-probe` (Core's game-menu probe is not installed), `workspace-open` (close the Mods workspace first), `game-view` (a full-screen game view is up), and `other-menu` (another HUD menu is open, such as the character menu or a station; the M key does not switch from it either). Refused on the server.

Log lines (client `LogOutput.log`): `Dev map open: raised the game menu (HUDMenuManager.GoToMenu FullscreenMenu)`, `Dev map open: FullscreenMenuMapper.SetState(Map)`, `Dev map close: HUDMenuManager.CloseMenu`, then `Dev map open: verified open=True after N frame(s)` (or `close … open=False`), or `Dev map <action>: refused (<why>)`.

```powershell
$bridgeArgs = Join-Path $env:TEMP 'map.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"open"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client map "@$bridgeArgs"
```

`mods_ui {"action":"menu","op":"open","tab":"map"}` also reaches the Map tab, through the tab's click handler; `map` is the narrower tool for map tests. Source-checked against `src/Progenitor.Dev/Ops/MapOps.cs` and built; the three game calls are each in live use elsewhere in Core (the Mods route, the native MODS tab, the build menu opener), but `map` itself has not been run live.

## `console`

Client, main thread. Runs one command line through the game's own console command dispatcher and returns what the console printed for it. MCP: `progenitor_console`.

| Arguments | Does |
|---|---|
| `{"text":"adminauth"}` | Runs the line, then collects the console's output for `waitMs` (default 1000) and replies. |
| `{"text":"ClanInvite Some Player","waitMs":3000,"echo":false}` | `waitMs` 0 to 10000 (0 replies at once, with what was printed during the call). `echo` (default true) shows `> <text>` in the console's history, as typing it does. |

The call is `StunConsole.Commands.TryCommand(text, echo)`, the one the console's input box makes (Core's **Authenticate as admin** button makes it for `adminauth`). The console looks the first word up in its command table, without case, and runs that command's handler; the rest of the line is the command's arguments, parsed by the console as when typed. The client's command set is registered at startup whatever the player's Options say: the **Enable console** setting and the console window only govern the console key and the window, so the op works with the console disabled, closed, and the game window in the background. No key, focus or device input is involved.

**It changes the client for real.** Client commands act on the player's own game as if typed: `bind` and the keybinding commands change the console's key bindings, `Disconnect` leaves the server, `Kill` kills the character, the camera, HUD and rendering commands change settings for the session. Run only what the test needs, on a client nobody is playing, and undo it afterwards.

A client command that needs the server sends its own network event; the server decides. `adminauth` sends the game's admin-auth request, and the server sets `User.IsAdmin` when the player's platform ID is on its admin list. The client-side handler itself prints nothing. Read the result on the server: [`users`](bridge-ops.md#users) gives each user's `admin` (`User.IsAdmin`).

The reply: `request`, `known` (the command word is in the console's command table; `null` when the table could not be read), `output` (the console window's messages added after the command, read `waitMs` later, one line per row with the color tags removed, the newest 200), `truncated`, `waitedMs`, `ok` (false when the console has no such command: `known false` or a `COMMAND NOT FOUND>` line) and `error`. Output a server sends back later than `waitMs` is not included. Lines come from the console window (`ConsoleUI_Unity`), which the game creates at startup; `outputError` says when it is missing.

Security: like every op, Dev bridge only (loopback plus the token). Each command line is logged before it runs (`Dev console: running the game console command '<text>'`) and after (`Dev console: '<text>' ran; N output line(s)` or `was not found`); the game also logs `[StunConsole.TryCommand] <text>` to its Unity log.

```powershell
$bridgeArgs = Join-Path $env:TEMP 'console.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"text":"adminauth","waitMs":2000}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client console "@$bridgeArgs"
node tools/mcp/call.mjs server users
```

Source-checked against `src/Progenitor.Dev/Ops/ConsoleOps.cs` and the game's console code (`ConsoleCommandHandler.TryCommand`, the client command system's `Allowed` returning true, `ConsoleSystem.OnCreate` creating and registering the console window, `StunConsole.Write` adding to the window's messages), built, and game-free tested (`BridgeConsoleTests`: arguments, the command word, the output lines). The `adminauth` call itself was live-checked through Core's button on 2026-10-01; this op has not been run live.

## `key`

Client, main thread. Passes keystrokes into the game's own input with no OS input and no window focus, so the game and Progenitor's keybinds react as if the player pressed them. Each change of the held keys is queued as a whole-keyboard state event for `Keyboard.current` through `InputSystem.QueueEvent`; the next input update applies it like a device report. No MCP wrapper; use `call.mjs`.

| Arguments | Does |
|---|---|
| `{"action":"tap","keys":"M"}` | Press, hold `ms` (default 50), release. Replies after the release. |
| `{"action":"hold","keys":"Ctrl+R","ms":800}` | The same with a longer default (200 ms, at most 10000). |
| `{"action":"down","keys":"W","ms":5000}` | Press and reply after 3 frames. The keys stay down until `up`, `release`, or `ms` (default 10000, at most 60000). |
| `{"action":"up","keys":"W"}` | Release keys this op holds; `notHeld` lists the others. |
| `{"action":"release"}` | Release every key this op holds. |
| `{"action":"type","text":"hello"}` | Queue one text event per character (at most 256). |
| `{"action":"status"}` | The held keys and the `input` block below. |

`keys` uses the `[Keybinds]` cfg key names joined with `+`: one key plus any modifiers. The punctuation keys also take the character they type on a US layout: `` ` `` and `~` (both the console key, `Backquote`; also `Tilde`, `Grave`), `'`, `;`, `,`, `.`, `/`, `\`, `[`, `]`, `-`, `=` (also `Apostrophe`, `Hyphen`, `Dash`); a shifted character names its key, not a Shift chord. `+` cannot name a key (it joins the chord); use `Equals` with `Shift` or `NumpadPlus`. `Ctrl`, `Shift` and `Alt` press the left key; `RightCtrl`, `RightShift`, `RightAlt` (or `AltGr`) the right one. Modifiers go down one event before the main key and come up one event after it. Mouse buttons are refused.

Every press is released: by `up` or `release`, by its `ms`, and on Dev unload or a Core swap (`Dev key: released … on unload`). While keys are held, the injected state replaces the physical keyboard's state, so do not type on the test client during a `key` run.

The reply carries `input`: `focused`, `runInBackground`, `backgroundBehavior`, `updateMode`, `keyboard`, `keyboardId`, `keyboardEnabled`, `keyboardCanRunInBackground`, `backgroundOverride`. A `tap`, `hold` or `down` also replies `reached` (keys the keyboard reported pressed) and, when one was not, `notReached` with `ok false`. The Input System's default `backgroundBehavior` (`ResetAndDisableNonBackgroundDevices`) disables the keyboard while the game window is in the background and drops its events; the op then refuses with that reason. `"background":true` opts in to `IgnoreFocus` plus `InputSystem.EnableDevice(keyboard)` for the op, restored when the last held key is released (`Dev key: InputSettings.backgroundBehavior … restored`).

What sees the keys: the game's input actions (M opens the map; with the map open, M closes it), and Core's keybind pump, which reads the same keyboard. So `key hold Ctrl+R` exercises a map-scope bind end to end, which `keybind_press` does not (that op calls the dispatch directly). Text events reach `Keyboard.onTextInput` listeners, which include the UI kit's text fields; the game's own text fields (chat) read the OS message queue instead and are not expected to receive them.

Log lines: `Dev key tap: down M for 50 ms`, `Dev key release-after-ms: up M`, `Dev key type: queued N text event(s)`, `Dev key <action>: refused (<why>)`.

```powershell
$bridgeArgs = Join-Path $env:TEMP 'key.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"tap","keys":"M"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client key "@$bridgeArgs"
```

Source-checked against `src/Progenitor.Dev/Ops/KeyInjectOps.cs`, built, and game-free tested (argument parsing, the release ledger, the event bytes against the dump's layouts in `BridgeKeyInjectTests`). Not run live: whether the game's actions and Core's pump see the events, and how the client behaves unfocused, are open.

## `mouse`

Client, main thread. Moves the pointer and clicks inside the game's own input, with no OS input: the real cursor does not move. Each step queues one whole mouse state event (position, buttons) for `Mouse.current` through `InputSystem.QueueEvent`, as `key` does for the keyboard. The game's input actions, uGUI and Core's pointer reads all see it. No MCP wrapper; use `call.mjs`.

| Arguments | Does |
|---|---|
| `{"action":"click","x":960,"y":300}` | The pointer to (x, y) with no button, then `settleFrames` frames later (default 3, at most 120) the button down, then after `ms` (default 80, at most 10000) the button up, all at that point. Replies after the release. |
| `{"action":"click","target":"chat","button":"right","ms":300}` | The same at the center of the game's chat log (the rect its own hover test uses). Refused while the log is not on screen. |
| `{"action":"move","x":960,"y":300}` | The pointer only; replies after `settleFrames` frames. |
| `{"action":"drag","x":100,"y":460,"toX":600,"toY":700,"steps":30}` | Press at (x, y), move one step per frame to (toX, toY) over `steps` frames (default 20, at most 600), release there. `button` as for click. Replies after the release. |
| `{"action":"wheel","x":960,"y":300,"notches":-3}` | WHEEL1: the pointer to (x, y), then after `settleFrames` one wheel reading of `notches` × 120 (positive scrolls up, away from the player; -20 to 20, not 0) and a zero reading the next frame, the 0 to non-zero crossing a real notch makes, so the framework's wheel handler counts it. Replies with `wheelSeen` (the mouse reported the reading). Coordinates are the game's screen pixels (`input.screenWidth` × `screenHeight`), not a capture's. |
| `{"action":"release"}` | Let go of every button the op holds. |
| `{"action":"status"}` | The `input` block, the held buttons and the `chat` block. |

`x` and `y` are screen pixels with the origin at the bottom left (the Input System's `Mouse.position`). `button` is `left` (default), `right`, `middle`, `back` or `forward`. A held button is released by its click, by `release`, and on Dev unload or a Core swap (`Dev mouse: released every button`). The next physical mouse report replaces the injected position, so do not move the test client's mouse during a `mouse` run.

The reply carries `input` (`focused`, `backgroundBehavior`, `mouse`, `mouseId`, `mouseEnabled`, `pointerX`, `pointerY`, `screenWidth`, `screenHeight`, `backgroundOverride`), `positionReached`, and for a click `pressReached`; `ok` is false when the mouse never reported the move or the press. `chat` (for `target:"chat"` and `status`) gives `shown`, `state`, `alpha`, the log's screen rect and center, or `why` it is not shown. While the game window is in the background the Input System disables the mouse and drops its events; the op then refuses unless `"background":true`, which switches `backgroundBehavior` to `IgnoreFocus` and enables the mouse for the op, restored afterwards.

Log lines: `Dev mouse click: pointer to (x, y)`, `Dev mouse click: left down for 80 ms`, `Dev mouse click: left up`, `Dev mouse <action>: refused (<why>)`.

```powershell
$bridgeArgs = Join-Path $env:TEMP 'mouse.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"click","target":"chat"}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client mouse "@$bridgeArgs"
```

Source-checked against `src/Progenitor.Dev/Ops/MouseInjectOps.cs`, built, and game-free tested (argument parsing and the event bytes against the mouse state layout in `BridgeMouseInjectTests`). **Verified live 2026-10-07** on the Steam client (3840x1080) in a world on the test server: `click` at coordinates and at `target:"chat"`, with `background:true`, reported `positionReached` and `pressReached`. The game acted on the injected left clicks (the player's cast counter rose by one per click away from the chat box). Not run live: `move` alone, the other buttons, and `release` while a button is held.

## `chat_tabs`

Client, main thread. CHAT12: the chat tab row as drawn ([chat.md](chat.md#tab-row-look-and-reordering-chat12)). Reflection into Core's `Progenitor.Chat.ChatTabs`. No MCP wrapper; use `call.mjs`.

| Arguments | Does |
|---|---|
| `{"action":"read"}` | `loaded` (the saved tabs are read; false until the chat first updates), `art` (`menu`, `resident`, `kit` or `unresolved`) with `artFont`, `artGlow`, `artLine`, `artStar`; `active`, `hovered`, `drag`, `saved` (`[Chat] Tabs`); `items`, each `kind` (`tab`, `add`, `menu`), `caption`, `state` (`Normal`, `Hover`, `Active`, `Dragged`), for tabs `index` and `unread`, `font`, `fontSize` and its screen rect (`minX`..`maxY`, `centerX`, `centerY`, the `mouse` op's coordinates). |
| `{"action":"move","from":2,"to":0}` | Moves a tab as the quick menu's Move left / right does, saves the order, and replies with `moved` and the read. Refused (`moved false`, nothing saved) while `loaded` is false; a drag in progress is cancelled. |
| `{"action":"edit","tab":1}` | FORMDIALOG1: opens the tab editor ([chat.md](chat.md#tab-editor-formdialog1)) for tab `tab`, or for a new custom tab with `-1`, as the quick menu does, and replies `{opened, dialog}` with the `mods_ui status` dialog readback. Refused (`ok false`) for **All**, a tab that does not exist, a full row, or before the tabs are read. Answer it with `mods_ui widget_click`, `dialog_type` and `dialog_choose`. Source-checked and built; not run live. |
| `{"action":"select","index":1}` | CHAT13 ([input line per tab](chat.md#input-line-per-tab-chat13)): shows a tab as a click does, with its send channel and draft; replies `selected` and the read. |
| `{"action":"line","text":"hello"}` | CHAT13: puts text in the chat input line, as typing leaves it; replies `set` and the read. |

The read also carries `tabs` (each `index`, `name`, `scope`, `sendTo` (the remembered channel, empty for the default), `sendNow` (`global`, `local`, `clan`, `whisper`, `system` or `keep`), `draft`, `holdsLine`) and `input` (`mode`, `modeName`, `whisperTarget`, `focused`, `text`, `partner`).

Aim a `mouse` `move`, `click` or `drag` at an item's center to test hover, selection and reordering. Source-checked and built; not run live.

## `chat_history`

Client, main thread. The chat log, the drawn window and the history file ([large history](chat.md#large-history-and-performance)). Reflection into Core's `Progenitor.Chat.ChatView`. No MCP wrapper; use `call.mjs`.

| Arguments | Does |
|---|---|
| `{"action":"read"}` | `on`, `attached`, `lines` (in the log), `capacity` and `setting` (`[Chat] HistoryLines`), `tab` and `tabLines` (lines the active tab shows), `windowStart` and `windowCount` (the drawn window in the tab's lines), `following` (false while scrolled back), `lineObjects` and `lineObjectsShown` (UI line objects built and showing, at most 120), `filterChecks` (tab filter checks so far), `loading` and `pending` (the history read at join and the lines waiting for it), `saveDirty`, `saving`, `keepHistory`. |
| `{"action":"fill","count":5000}` | Adds `count` (1 to 5000) numbered test lines to this client's log only: nothing is sent to the server or another player. Spread over Global, Local, Clan, System and whispers, or all on `kind` (`Global`, `Region`, `Team`, `WhisperFrom`, `WhisperTo`, `System`, `Lore`, `Local`). They are saved with the history like other lines. Replies with the read. Refused while the view is off or the history is still being read. |

```powershell
$bridgeArgs = Join-Path $env:TEMP 'chat-history.json'
[System.IO.File]::WriteAllText($bridgeArgs, '{"action":"fill","count":5000}', [System.Text.UTF8Encoding]::new($false))
node tools/mcp/call.mjs client chat_history "@$bridgeArgs"
```

Source-checked and built; not run live.

## `nav`

Client, main thread. NAV1 [controller and keyboard navigation](ui.md#controller-and-keyboard-navigation-nav1): read the focus, take one step, or press a button on a virtual gamepad.

| Arguments | Does |
|---|---|
| `{"action":"state"}` | The focus (`nav`: `surface`, `pane`, `marked`, `pendingPane`, `focus {kind, type, text, part, hooked}`) and the virtual gamepad (`pad`: `added`, `deviceId`, `current`). |
| `{"action":"press","button":"down"}` | One navigation step through the model (`UiNav.Execute`); replies `taken` and the focus after it. No device is involved. |
| `{"action":"pad","button":"a","ms":120}` | Add a virtual gamepad to the Input System (once), make it current, queue its state with the button held, and release it after `ms` (default 120, at most 5000). The step goes through the real device read, and the game sees a controller. Read `state` after the hold. `background:true` lets the Input System take the pad's events while the window is in the background (as `key` does); the setting goes back 0.2 s after the release. A newer press is not cut short by an older press's release. |
| `{"action":"pad_remove"}` | Remove the virtual gamepad and restore the background setting. Also done on Dev unload. |
| `{"action":"hold","button":"down","ms":900}` | BRIDGEUI1: hold one or several buttons (`down+a`) through the framework's own per-frame navigation sample (`SampleNav`) for `ms` (default 120, at most 5000), so a held direction repeats (0.40 s, then every 0.09 s) exactly as a held D-pad does. No device, no background setting, no focus. It takes the [`ui_input`](bridge-ops-ui.md#ui-input-without-focus-bridgeui1) lease, so the player touching the real mouse or keyboard, a `ui_input release`, a Core swap or Dev unload ends it at once. Replies after the hold with `nav`. |
| `{"action":"stick","y":-1,"ms":400}` | BRIDGEUI1: the right stick's vertical axis (-1 to 1, positive up) through the same sample: scrolls the focused list (a data table's body, a master-detail half). |
| `{"action":"focus","row":"demo-table:row0","cell":"check"}` | BRIDGEUI1: put the focus on a visible cell row, or one of its cells, of the screen navigation is on, and draw its marker, as a run of presses ending there would. This is the way into a content pane and to a row past the first screen. `ok false` when the row is not a stop of that screen (a window that captures only under the pointer is never navigated). |

`button` is `up`, `down`, `left`, `right`, `a`, `b`, `x`, `lt` or `rt`. The pad's state is the dump's `GamepadState` (28 bytes: buttons, two sticks, two trigger floats); a direction is the D-pad bit, a trigger its float at 1.

Log lines: `Dev nav: added a virtual gamepad (device N)`, `Dev nav: removed the virtual gamepad.`, `Ui: nav tab <tab> reason=nav-tab` (diagnostic).

A virtual gamepad can switch the game into its controller look while it exists. Remove it at the end of a test batch.

MCP: `progenitor_nav`. `hold`, `stick` and `focus` are live-verified on the test client 2026-10-14 (the focus into a data table's body, presses from there, a held direction repeating, the right stick scrolling) and game-free tested (`BridgeUiInputTests`).

Source-checked against `src/Progenitor.Dev/Ops/NavOps.cs`, built, and game-free tested (button names, the pad state and event bytes against the dump's layout in `UiNavTests`). **Run live 2026-10-10** on the Steam client at the title screen with `background:true`: the pad was added, became current and drove every step of the procedure below except the in-world ones; `pad_remove` removed it. The game shows its controller prompts while the pad exists and goes back to the mouse's when the real mouse moves.

**Acceptance procedure (run at the title screen 2026-10-10; the in-world part not run).** On a test client with the Mods workspace open on the Mods tab: `nav pad down` (with `background:true` if the window is not focused) shows the focus on the first list entry (`state`: `marked: true`, pane 0). `pad down` moves it, `pad a` opens that mod's page and, a few frames later, `state` reports pane 1. `pad b` returns to pane 0; `pad rt` selects Install Mods. On a settings page, the focused row shows the game's band and its description; `pad right` on a toggle row flips it. Open a `ctx.Dialogs.Confirm`: `state` starts on the dismissal (No); `pad b` answers No. Never press `a` on a mod page without reading `state` first: the page's Uninstall is one cell to the right of its switch. Moving the real mouse hides the marker. Finish with `pad_remove`, and check the controller look is gone after the mouse moves.

## Map-scope keybinds through the bridge

With a client in a world and the keybind's plugin loaded:

1. `map {"action":"open"}`: `ok true`, `open true`.
2. `keybind_press {"plugin":"<guid>","bind":"<id>"}` for a `RegisterInMap` bind: `fired: 1`. For a `RegisterHoldInMap` bind add `"hold":30`.
3. Optional, the real key path: `key {"action":"hold","keys":"<chord>","ms":500}`: `reached` lists every key, and the bind's log line appears.
4. `map {"action":"close"}`: `ok true`, `open false`. Repeat step 2: the bind reports it did not fire (map closed).

## `build_search`

Client, main thread. Reads and drives the [build menu search](modules.md#build-menu-search) Core adds to the game's
build menu, through Core's `Progenitor.Ui.BuildSearchTestBridge` by reflection. **Live 2026-10-04:** `status`, `set`, `clear`, `focus`, `blur` and `type` (text, then Escape twice) all behaved as described below on the Steam client.
Every action but `status`, `open` and `close` needs the build menu open. Refused on the server.

| Args | Does |
|---|---|
| `{}` or `{"action":"status","cells":40}` | `open`, `attached`, `enabled`, `clearOnClose`, `focused`, `guard` (`build-search` while the box has the keyboard), `holdEngaged`, `exclusiveHolds`, `query`, `words`, `total`, `visible`, `hidden`, `countText`, `clearShown`, `lastError`; the all-tabs results (`allTabs`, `resultCells`, `resultsMatched`, `resultsNote`, `blobCopies`: private blob copies not yet freed); the breadcrumb (`breadcrumbs`, `crumb`, `crumbShown`, `crumbHoveredGuid`, `crumbHost`, `indexFailure`); and up to `cells` (0 to 500) cells: `index`, `active`, `hiddenBySearch`, `name` (the localized name), `searchable` (the normalized text the query is matched against). |
| `{"action":"open"}` / `{"action":"close"}` | Opens or closes the game's build menu with no input or focus, the way the B key does: open creates the `OpenMenuEvent` {CharacterInfo, init entity} plus `CharacterMenu.InitData` {Building} the key creates; close calls `HUDMenuManager.CloseMenu` when the character menu (which hosts the build menu) is the open HUD menu, and does nothing otherwise. Unlike the key, open skips the build-menu progression requirement. The change shows on the next frame: read `status`. Live 2026-10-04, no input or focus: open showed Castle > Foundation with the box; close closed it; a second close and a second open reported "not open" / "already open"; open, search, close and reopen came back unfiltered with the blob restored. |
| `{"action":"hover","index":0}` | Shows row cell 0's tooltip without moving the mouse: sets the cell's `GridSelectionEntry.IsHovered` (the flag the game's own pointer-enter sets) and clears it on every other cell; `-1` clears all. The real pointer clears it on its next enter or exit. Live 2026-10-04. |
| `{"action":"select","index":0}` | Picks row cell 0 as a click does: sends the entry's own pointer-down (or pointer-click, by the entry's `SelectOnMouseDown`) with a left-button event, so the game enters placement for that piece. Refuses a disabled or hidden cell. Returns `status` plus `selected`. Used to place a piece with the [material sources](modules.md#material-sources) test without the mouse; source-checked, run on 2026-10-13. |
| `{"action":"set","text":"wall"}` | Writes the query through the field's own edit path. The filter applies on the next mapper update. |
| `{"action":"clear"}` | What the x button does: empties the query and keeps the keyboard in the box. |
| `{"action":"focus"}` / `{"action":"blur"}` | Gives or takes the keyboard, as a click on the box or elsewhere does. |
| `{"action":"type","text":"wall","keys":["Escape"]}` | With focus, queues `text` and then `keys` (chords in [`mods_ui type`](bridge-ops-ui.md#mods_ui-text-field-tests-texttest1) syntax) for the typed-key pump, one item a frame. Escape and Enter reach the box's own handlers, as the physical keys do. Read back with `status` a few frames later. |

From bash: `node tools/mcp/call.mjs client build_search '{"action":"status"}'`. There is no MCP wrapper.

To test with every item unlocked, run [`unlock`](bridge-ops-player.md#unlock) with `action: all` on the isolated test server first, and
`revert` afterwards.

## `materials`

Either side, main thread. The [material sources](modules.md#material-sources) seams through Core's internal `MaterialsTestBridge` (reflection). Development tooling, not a plugin API.

| Args | Does |
|---|---|
| `{}` or `{"action":"status"}` | `side`, `status` (the `ctx.MaterialSource.Status` line), `hooksInstalled`, `hooksReason`, `wired`, `payments`, `faults`, `shortfalls`, `viewsSent`, `recomputes`, `skips` (the `Status` counters), `bootstrap` (Bootstrap's `materials: native seams …` line). Client: `views`, per operation (`Build`, `Craft`, `Upgrade`, `CharacterCraft`) `null` or `{sequence, territory, heart, station, totals}` (the held view the cost display uses; `station` is the workstation or, for `Upgrade`, the castle heart; 0 for `Build` and `CharacterCraft`). Server: `openOperations`, `cachedViews` (per-player views held for reuse), `injection`. |
| `{"action":"inject","from":"c-…","to":"c-…","item":<hash>}` | Server. Fault injection for the transaction check: right after the **next** governed check (the next craft start or placement whose count the provider governs), moves every unit of the item from one container to the other through the inventory engine, so that operation's payment meets a container that changed between its check and its payment. One shot; logs `materials: development fault injection armed: …` and, when it runs, `materials: fault injection moved N of item … after the check of tx=…`. |
| `{"action":"clear"}` | Server. Drops an injection that has not run. |
| `{"action":"trace"}` | Server. One shot: the next governed check logs one `materials: trace <operation> character=… rows=… bag=… pool=… total=…` line: the game's own count without shared storage (`bag`, item:amount), the pool lines Progenitor appended (`pool`), and the totals of the rows handed to the game's check (`total`). For diagnosing a check that refuses although the pool should cover the cost. |
| `{"action":"send","event":"repair_equipped","user":"<name>","equipment":"chest"}` | Server. Queues `RepairEquippedItemEvent` for that player, the event the character menu's repair sends. `equipment`: `headgear`, `chest`, `weapon`, `magicsource`, `footgear`, `legs`, `cloak`, `gloves`, `bag`, or the `EquipmentType` int. |
| `{"action":"send","event":"repair_item","user":"<name>","station":"target","slot":3}` | Server. Queues `RepairItemEvent` (repair at a workstation): `station` is the workstation (`index:version`, or `target` for the character's interaction target), `inventory` the inventory holding the item (`self`, the default, for the bag, `target`, or `index:version`), `slot` its index. The server requires the character to be interacting with the station: open its window on the client first. |
| `{"action":"send","event":"heart_upgrade","user":"<name>"}` | Server. Queues `CastleHeartInteractEvent` with `EventType` Upgrade for `heart` (`target`, the default: the heart whose window the character has open; or `index:version`). The server requires the character to be interacting with the heart. |
| `{"action":"send","event":"character_craft","user":"<name>","recipe":<hash>}` | Server. Queues `StartCharacterCraftItemEvent` (the request the Character window's crafting sends) for `recipe`, the recipe's PrefabGUID hash. The server requires the recipe in the player's own crafting list (unlocked) and a free queue slot; the pool pays only while the character stands on a plot. Game-free tested (`MaterialEventArgsTests`); ran live on 2026-10-14 (the game counted once and paid from the pool on a plot, and kept its own rules off a plot). |

`send` takes `user` (character name) or `platformId` for the player, and queues the event as an entity with the payload and the player's `FromCharacter`, the server-side recipe of `bot_repair`; the game's own system handles it on its next update with its normal checks, and a refused event is silent (read durability, the heart level and the `materials:` log lines). Field offsets are checked against the interop wrappers before the first write; network ids are copied from the entities' own `NetworkId`. Each queued event logs `materials send: queued …`.

From bash: `node tools/mcp/call.mjs server materials '{"action":"status"}'` (or `client`). There is no MCP wrapper. `status`, `inject` and `clear` ran live on 2026-10-14. `send` (all four events) and `trace` ran live on 2026-10-14: the game counted, paid and repaired or upgraded, or refused a short cost at its check ([Shared Resources Test, Repair and Upgrade cases](../samples/Progenitor.SharedResourcesTest/README.md#repair-and-upgrade-cases)). A `repair_item` the game refuses at its check usually means an ingredient of the repair cost has no source; `trace` shows the rows the check saw.

## `servants`

Server only, main thread. **Built and game-free tested (`ServantBridgeArgsTests`); not run live.**
A castle's servants and hunts through Core's public `ServantBridge`, the same code as
[`ctx.Servants`](modules.md#servants) (Core grants Dev no `InternalsVisibleTo`; `ServantBridge` is not a plugin
API). Refused on the client. MCP: `progenitor_servants`. Every action except `timers` names the castle by
`territory` (index) or `heart` (`index:version` of the heart or anything connected to it). Ids are of the
current world load.

| Args | Does |
|---|---|
| `{"action":"list","territory":n}` (or `"heart":"i:v"`; `list` is the default) | `servants`: one row per coffin as the throne lists it: `row` (`#1..`), `name`, `state` (`Free`, `HasItems`, `AwayOnHunt`, `Injured`, `Dead`), `servant`, `coffin`, `injuryEndsUtc`, `injury`, `proficiency`, `lootFactor`, `gearLevel`, `expertise`, `perks`, `itemStacks`. `hunts`: the heart's hunt record: `row`, `mission`, `missionName`, `zone` (`x,y#zone`), `slot`, `lengthSeconds`, `startedUtc`, `endsUtc`, `secondsLeft`, `servants`. Also `timersDisabled`. |
| `{"action":"zones","territory":n}` | The zones with a servant mission that the heart's owner (or `user`, `index:version` of a user entity) discovered; `"all":true` also lists the others. Per zone: `zone`, `mission`, `missionName`, `discovered` (null when the progression cannot be read), `active` (this castle already has a hunt there; the game allows one per zone and castle), `servantSlots` (`MissionData.ServantSlots`, -1 unreadable). Also `durationSlots` (`slot`, `settingSeconds`, `lengthSeconds` a send writes now), `activeHunts`, `discoveryReadable`, `allProgressionUnlocked`, `timersDisabled`. |
| `{"action":"send","territory":n,"servants":["Alda","#2"],"mission":<guid>,"slot":0}` | `ctx.Servants.Send` as `progenitor.dev bridge` (so `ServantsSent.By` and the `servants: sent by=…` line name the bridge). `servants`: up to three names (case-insensitive, unique), `#n` rows of `list` or `index:version`, as an array or a comma list; omitted: the castle's free servants (capped by the zone's slots). `mission` (GUID hash) or `zone` (`x,y#zone`), not both; neither: the first discovered zone with no hunt of this castle and enough slots. `slot` default 0. `user` and `checkDiscovered` (default true) as in `ServantSendRequest`. Returns `ok`, `refusal` and `reason` (the game's check that failed), `summary`, `mission`, `zone`, `picked`, `slot`, `lengthSeconds`, `startedUtc`, `servants`, `buffApplied`. |
| `{"action":"finish","territory":n}` (optional `"mission":<guid>`, `"method":"timers"\|"row"`) | Makes the castle's active hunts (or the one on `mission`) end now; the game returns them on its next update. See below. Returns `ok`, `method`, `reason`, `hunts` (as they were), `finished` (method `row`: the rewritten rows), `timersWereDisabled`, `restorePending`. |
| `{"action":"empty","territory":n}` (optional `"servants":[…]`, `"to":"i:v"`, `"discard":true`) | Moves each servant's inventory into the castle's containers with the inventory engine's `TransferAll` (the `ctx.Inventory.TransferAll` code: the total is conserved, never duplicated or destroyed; worn servant gear is not touched). `servants` as for `send` (any number); omitted: every servant at home (`Free`, `HasItems`, `Injured`); a servant on a hunt is skipped with a reason. `to`: one container of this castle (`index:version`); omitted: the castle's ordinary containers (not stations, the heart or coffins; only containers that accept every item), tried in the castle's order until the servant is empty. Returns `ok`, `targets` (tried, in order) and per servant `before`, `moved`, `left`, `into` (`container xN`) and `reason` (`no room: N unit(s) did not fit and stay on the servant` when the containers are full). **`discard:true` is for tests only**: what did not fit is removed from the servant (`discarded`, `discardedItems`) and the server logs a `servants: EMPTY DISCARD (test only)` warning naming every item. Without it nothing is ever removed. |
| `{"action":"timers"}`, `{"action":"timers","off":true\|false}` | Reads, or sets, the game's `SyncedServerDebugSettings.ServantTimersDisabled` (`off:true` disables the timers). A set cancels a restore a `finish` scheduled. Returns `timersDisabled`, `wasDisabled`, `restorePending`, `cancelledRestore`. |

`finish` methods and side effects:

- **`timers` (default)**: the game's own servant-timers debug setting (the switch the `DisableServantTimers` console command flips). With it on, the game's return system treats every hunt as due, so **every active hunt on the server returns** on the next update, not only this castle's; a send made while it is on lasts 1e-5 of its length. Progenitor turns it back on (enables the timers) as soon as the targeted rows are gone from the record, at most 10 s later, and also on a Core unload or swap; it logs `servants: finish: the hunt returned; servant timers restored (enabled)`. When the timers were already disabled, nothing changes and nothing is restored. The setting is a replicated, saved server setting: a server stopped in that window keeps it, so read `timers` after a restart and set `off:false` if needed.
- **`row`**: rewrites only the matching rows' `MissionStartTimeTicks` (verified hunt-record layout, the stride-checked buffer accessor `ctx.Servants` reads with; no other field and no new layout) so start plus length lies one second in the past; a start is never moved later. Only those hunts return, with their real lengths kept for the next send. The throne map shows the hunt's start moved back until it returns.

Either way the game rolls each servant's success at the return, as for any hunt: **a servant can die or come back injured**, so use servants you can lose. Each finish logs `servants: finish by the Dev bridge: …`.

From bash: `node tools/mcp/call.mjs server servants '{"action":"list","territory":5}'`.

## `dye`

Client, main thread, read only. Reads Core's [Dye all](modules.md#dye-all) through Core's `Progenitor.Dye.DyeAllTestBridge`
by reflection; it never presses, sends or changes a setting. Refused on the server.

| Args | Returns |
|---|---|
| `{}` | `step` (the last quarter-second scan: `Release` off or standing down, `Pause` no client world or no HUD menu open, `Scan`), its inputs `menu` (`OpenHUDMenuSystem.CurrentMenuType`; 0 none, 1 the character menu, -1 unreadable), `hasWorld`, `enabled`, `standingDown`; `stopped`, `released`, `finds` (scene searches so far this generation); `containerFound` / `containerActive` (the picker being listed); `containers` (every known `TransmogContainer`: its HUD `menu`, `alive`, `active`, `current`); `cellsListed` (cells in the active picker on the last scan), `watched` (cells with a pointer watch) and `watchHandles`; `hasColorSwatch`, `hintShown` (the key tip is showing), `hintRow` (the key-hint row it sits in, `MotionRoot/InputContexts`), `hintText` (`Hold [LMB] Set All`); `holdPhase`, `heldSwatch`; `lastHold` (`color N, sent K of K` or `canceled (Reason)`) and `lastHoldUtc`; `frameErrors`; `installed`, `client`. |
| `{"cells":true}` | Also `cells`: each shown cell of the active picker with `watched`, `id`, `color`, `index` and its screen center `x`, `y` (Input System pixels, origin bottom left). |

From bash: `node tools/mcp/call.mjs client dye '{}'`. There is no MCP wrapper.

**Self-test of a hold** (client in game, Character > Equipment open with a dyeable slot selected): `dye {"cells":true}`,
pick a cell with `color: true`, then `mouse {"action":"click","x":<x>,"y":<y>,"ms":1500,"background":true}`. A plain
click colors the selected piece first (the game's behavior); after the hold time the log shows
`Dye all (hold): color N, sent …` and `dye` reports it as `lastHold`. The injected press moves the game's pointer, so
do not run it while someone is using that client. Not yet run through the bridge: on 2026-10-08 the plain readback
was run live and the holds were made by hand; `{"cells":true}` was built but only read with the menu closed (no cells),
so its screen coordinates are not yet checked against a press.

**Live 2026-10-08** (Steam client, Core generation 23): with Character > Equipment open, `step Scan`, `menu 1`,
`containers` = `CharacterMenu(Clone)` active and `ContainerMenu(Clone)` inactive, `cellsListed 42`, `watched 42`,
`hintShown true`, and `lastHold` `color 20, sent 4 of 4` after a hold.

## `install_local`

Either side, socket thread. `status` and `reload` read only (no world, no download, no write); `qualify` writes one JSON file under `BepInEx/config/`. Reports and re-reads the local package index the Install tab merges in front of the fetched one — `[Install] LocalIndexPath` in `BepInEx/config/progenitor.cfg`, a folder holding an `index.json` in the Thunderstore v1 shape whose version `download_url` and `icon` are `file:///…` URLs (job HOT1). `{"action":"status"}` is the default; `{"action":"reload"}` re-reads the folder and merges it again first. `catalog: false` means the Install tab has not built its catalog in this process yet, so the folder was read on its own.

```
→ {"id":9,"token":"...","op":"install_local","args":{"action":"status"}}
← {"id":9,"ok":true,"result":{"ok":true,"action":"status","localIndexPath":"<testserver>\dummy","configured":true,"indexFile":"<testserver>\dummy\index.json","catalog":true,"merged":297,"entries":["Progenitor-ReloadExample"],"shadowed":0,"error":null}}
```

**A changed key** (LOCALRELOAD1, 2026-10-07; built and game-free tested, not live-verified): `reload`, and every index load the Install tab makes, reads `[Install] LocalIndexPath` again, so a key set, changed or cleared after the catalog was built takes effect without a Core reload (a cleared key drops the local entries; the log says `Install: [Install] LocalIndexPath is now …`). `reload` reports the folder after it re-read; `configuredNow` is the key as configured at the moment of the call.

Build such a folder with `tools/make-dummy-package.ps1 -Out <folder>`: it builds the chosen sample, zips the DLL with a generated `progenitor.json` (`hotLoadable: true`, the image's real sha256, and `compatibility.peers` copied from the image's `[NetworkCompatibility]` `Peers` when it sets one), a Thunderstore `manifest.json`, a README and a 256x256 `icon.png` into `<folder>/Progenitor-<Sample>-0.1.0.zip`, and writes `<folder>/index.json` plus a sibling `README.md` / `CHANGELOG.md`. A local listing's README and changelog are its own zip's `README.md` / `CHANGELOG.md`; the sibling files stand in only when the index lists one package ([F11](mods-menu.md#local-test-listings-readme-per-package-and-the-page-title-f11)). It writes nothing outside `-Out` and contacts nothing.

**Publish-shaped packages:** `-Publishable` builds `<Owner>-<Package>-<Version>.zip` the way [publishing.md](publishing.md) asks a mod author to (side-prefixed description, `website_url`, the BepInExPack and `VRising_Modders-Progenitor-<ProgenitorVersion>` dependencies, the README requirement block, `CHANGELOG.md`, `LICENSE`); `-PublishFlaws` builds the same package with deliberate faults (BOM, no side prefix, no BepInExPack dependency, a 128×128 icon, no requirement block, a wrong `progenitor.json` hash and side, a stray token). The publish checker's test fixtures in `tests/Progenitor.Tests/Fixtures/ExampleTeam-*` were generated this way ([Publish check](dev-loop.md#publish-check)).

**Third-party-shaped packages** (no real mod needed): `-Kind Plain` builds one plain BepInEx plugin package (`<Owner>-<Package>-<Version>.zip`, the plugin at `BepInEx/plugins/<PluginName>/<PluginName>.dll`, `-Paperwork` adds `SOURCE_REVIEW.md` and `Source/`), and `-Kind Pair` builds a library and a mod that depends on it (manifest dependency, `[BepInDependency]` on the library's GUID and an assembly reference). Both compile against the installed game's `BepInEx\core` (`VRISING_GAME_DIR`), write `index.json` beside the zips (`-Append` merges) and touch nothing else. `-Owner`, `-Package`, `-PluginName`, `-Library` and `-Version` name them. The `InstallBepInExPluginsLayoutTests` fixture `QATeam-QStacks-1.0.1.zip` was built with `-Kind Plain -Owner QATeam -Package QStacks -PluginName QStack -Version 1.0.1 -Paperwork`. Built and inspected offline 2026-10-10; not installed live.

`-Sample` chooses which sample is packaged and defaults to `HotDemo` (job HOT2). `samples/Progenitor.HotDemo` declares `[ProgenitorSide(Side.Both)]` and is therefore the one a **client** can hot load; `-Sample ReloadExample` is server-only and is refused for a client target with `SideMismatch`. `-Side` defaults to the side the chosen sample's image really carries, which is the only honest default — declaring a side the image does not carry produces a package the admission evaluator refuses, which is the check working.

`{"action":"qualify"}` (job HOT2) authors the reviewed qualification record apply-now needs:

```
→ {"id":10,"token":"...","op":"install_local","args":{"action":"qualify",
     "zip":"<testserver>\\dummy\\Progenitor-HotDemo-0.1.0.zip",
     "target":"Client","evidence":"research/hot-load/hotdemo-initial.md"}}
← {"id":10,"ok":true,"result":{"ok":true,"action":"qualify","target":"Client","file":"…\\config\\progenitor-qualification.json",
     "replaced":false,"records":1,"image":"Progenitor.HotDemo.dll","embeddedRecordIgnored":false,"scanValid":true,
     "record":{"id":"progenitor.hotdemo/initialinstall/client","artifactSha256":"…","side":"both",…}}}
```

It inspects the zip with the ordinary non-executing pipeline, fills the artifact sha/size, the dependency closure and the three compatibility identities from this process, and appends the record under an atomic write (schema 1). `target` must be `Client`, `LocalServer` or `Both` — an unknown value is refused, never defaulted. `evidence` is required, and `replaced: true` means a row with the same id was rewritten rather than a duplicate added (the id is keyed by plugin id, operation and target, so a second target is a second row). An existing file this parser cannot read is refused rather than overwritten, so the operator's own rows are never discarded.

**A record is a review claim, never self-certification.** Writing one says a person accepted these exact bytes on this game build against this Bootstrap/Core pairing. Nothing in the op inspects behavior and nothing can: metadata proves structure and finds known forbidden references, not tracked-facade discipline or complete cleanup. A record shipped *inside* a package stays ignored and reported (`EmbeddedQualificationIgnored`); this op writes local reviewed policy under the process's own BepInEx root and does not change that. Record authoring: [mod-manifest.md](mod-manifest.md). The snapshot and recheck the record is checked against: [hot-reload.md](hot-reload.md).

## `remote_install`

Either side, main thread. Reads and drives the admin remote install channel ([mod-package-capabilities.md](mod-package-capabilities.md#admin-remote-installs)). Source-checked and game-free tested; not live-verified.

| Side | `action` | Effect |
|---|---|---|
| Client | `status` (default) | The Install tab's server route (`route.kind` `local`/`remote`/`none`, `route.block`, `route.reason`, `route.why`, `route.connectedIsLocal`) and the inputs it was decided from (`route.inputs`: `configuredPath`, `pathValid`, `canonicalPath`, `folderPort`, `connected`, `connectedAddress`, `connectedPort`, `portRecorded`, `isLocalMachine`, `handshakeOk`, `remoteStatus`); whether the connected server has spoken on the channel (`serverSupportsRemote`), its last status (`authorized`, `authorizedBy` `adminlist`/`adminauth`/`none`, `admin` as an alias for one release, `enabled`, `allowed`, `reachability`, `rows`), the SteamID the server reported (`steamId`), every package's latest step (`steps`) and, with `package`, that one (`latest`). A finished step stays until that package's next operation. The route is re-decided whenever one of its inputs changes, Mods workspace open or not. |
| Client | `request` | Sends the `list` request again (the Install tab does this on every open). |
| Client | `install` / `update` | With `package` (`Namespace-Name`) and `version` (exact): sends the same request the row's Install / Update button sends — only when the tab's route is `remote`; otherwise it is refused and names the route (drive a local install through `mods_ui`). `"route":"remote"` forces the remote request to test the channel itself. |
| Client | `uninstall` | With `package` (optional `"force":true` to remove despite dependents, or while the server cannot read the index). Same route rule and `route` override. |
| Client | `cancel` | With `package`: cancels the running step if it has not reached the write. |
| Client | `adminauth` | Runs the game's own `adminauth` exactly as the **Authenticate as admin** button does (refused while `[Ui] AdminAuthButton` is off). Read `status` → `adminAuth.state` (`authenticating` → `done` / `failed`) and `status.adminAuthed`. |
| Server | `status` (default) | Bound `[RemoteInstall]` settings, `busy`, the Thunderstore probe (`probe.state`, `probe.detail`, `probe.checkedUtc`), the ledger rows with their route, and the last 20 audit lines. |
| Server | `probe` | Starts the reachability probe (at most once a minute; never waits). Read `status` again for the answer. |

```
→ {"id":11,"token":"...","op":"remote_install","args":{"action":"install","package":"Dyno-ExampleCommandFramework","version":"0.10.4"}}
← {"id":11,"ok":true,"result":{"ok":true,"side":"client","nonce":3,"route":{"kind":"remote","block":"none",...},"latest":{"stage":"accepted",...}}}
```

The server re-checks every request against its admin list (`adminlist.txt`, re-read when it changes) or `User.IsAdmin`, so this op cannot install anything the Install tab could not.

## `disconnect`

Client process only, main thread. Requests `ViewManager.GoToView(ViewLocation.ExitGame)`, the production Leave confirmation's terminal transition, without OS input or focus changes. Native native analysis verifies that the non-generic wrapper forwards to the same `GoToView<EmptyInitData>(26, null)` call, and the ExitGame overlay yields a frame before requesting MainMenu. It does not merely drop the network connection.

```json
{"op":"disconnect","args":{"statusOnly":true}}
{"op":"disconnect","args":{}}
{"op":"disconnect","args":{"statusOnly":true}}
```

Inspect before requesting; then poll `statusOnly:true`. `state:"pending"` means the transition was requested, **not** verified. `verified:true` requires no live client world, `ViewLocation.MainMenu`, and an active `MainMenuNewView`. Result fields include `requested`, `clientWorldPresent`, `view`, `mainMenuVisible`, `elapsedSeconds`, and `error`. Bridge-envelope success is not proof of leaving.

Repeated pending requests do not reissue. At 120 seconds the state becomes `timedOut` without automatic retry. A native exception retains `requestFailed` intent because a transition may have partially started. A replacement client world produces `superseded`, with no request sent to that world. Observation-only, launch-in-flight, or absent-world calls never initiate a transition. Thus an error HUD after world destruction can report `notVerified`; do not pretend that is the main menu. Dev reload drops this in-memory request latch; use status-only observation after reload rather than blindly repeating a mutation.

Six pure policy tests and the Dev build pass. Live request/poll reached verified MainMenu with no client world; the final policy build was redeployed afterward and the parent verified multiple leave/rejoin cycles. See the research note for evidence. On a server process, the op refuses with `disconnect runs on the client side only`. To exit the process instead, use `quit`.

## `quit`

Client **or** server, main thread. Calls `UnityEngine.Application.Quit()` and returns `{ok:true}` immediately. Unity exits after the current frame. Does **not** refuse: a running world, a launch in flight, and the main menu are all valid. Logs a BepInEx warning `quit requested via bridge`. Bootstrap's `Application.quitting` handler then promotes staged `progenitor-core/Progenitor.dll.next` and `plugins/Progenitor.Bootstrap.dll.next`. This is the graceful process-exit the test loop should prefer over killing `VRising.exe`. Killing the process skips the quit handler.

```
→ {"id":22,"token":"...","op":"quit"}
← {"id":22,"ok":true,"result":{"ok":true}}
```

No args. Works from the client main menu (no world). On the test server it also quits that process — do not point this at `C:\VRisingServer`.

```
node tools/mcp/call.mjs client quit
node tools/mcp/call.mjs server quit
```

## `auto_update`

Both sides, main thread (AUTOUPDATE1 to AUTOUPDATE3, [auto-update.md](auto-update.md)). On a client, reflection into Core's `AutoUpdateTestBridge` (the table below); on a server, into `ServerAutoUpdateTestBridge` (the [server table](#auto_update-on-a-server)). The `self_*` actions (AUTOUPDATE5, Progenitor's own update) run on either side, through `AutoUpdateTestBridge`. Built and game-free tested; verified live in part 2026-10-09 ([phase 1](auto-update.md#phase-1-as-built), [phase 2](auto-update.md#phase-2-as-built), [phase 3](auto-update.md#phase-3-as-built)).

| Action | What it does |
|---|---|
| `status` (default) | The update state: `mode`, `promptBeforeJoin`, `checked`, `header`, `count`, `items[{package, name, installed, latest, major, pinned, pinnedAt, newDependencies, pluginId, local, line}]`, `skipped[{package, reason}]`, `badge {count, text}`, `badgeDrawn {site, wanted, shown, text, rect, captionRect}` (the label as drawn beside the title screen's MODS entry), `installList {builtFor, current}`, `run {state, origin, prepared[]}` and `preJoin {armed, state, held, heldServer, lastServer, lastDecision, lastResult, rememberedServers}`. |
| `recheck` | A detection pass on the index already read. Never a request. |
| `pin` / `unpin` (`package`) | **Keep this version** and **Unpin**. |
| `update` (`package`), `update_all` | Start the same runner the Install tab buttons start; the confirmation opens as a dialog. Answer it with `mods_ui dialog_choose {"choice":"yes"}` (or `no`). |
| `prejoin_preview` (`host`, `port`) | What the pre-join prompt would offer for that server now, with the requirements remembered from its last handshake. Nothing is launched. |
| `prejoin_forget` | Forget this session's **Join anyway** answers, so the prompt shows again. |
| `check` | One index request now (the stand-in or Thunderstore), as the Install tab's own check; detection runs on the answer. |
| `auto_run` | An automatic pass now on what is due, when the apply window is open (`status.auto.window` says why not). The title screen's tick starts the same pass by itself. |
| `rollback` (`plugin`, optional `package`) | The mod page's **Roll back**: the previous kept version back, then kept at it. |
| `mode` (`package`, `choice`) | The mod page's **Updates**: `Global` (captioned **Default (…)**: follows Preferences › Updates), `Automatic`, `Notify`, `Off` or `Keep` (pins the version the ledger names). |
| `clear_failed` (`package`) | **Clear failed versions**: the next pass may try them again. |
| `forget_attempts` | Forget this session's automatic attempts (each version is tried once per session). |
| `start_check` | Run the after-start check of restart updates now (normally 10 s after Core loads). |
| `subtitle_dot` (`choice` `on`/`off`) | The optional accent-red dot before the line under MODS (for the user's choice; not a setting). |
| `screenshot` (`path`, an absolute `.png`) | The game's own frame, written at the end of the frame: no focus, no input. |
| `self_status` | AUTOUPDATE5, either side: Progenitor's own update. `side`, `mode`, `devOverride`, `kind` (`Managed`, `Hand`, `ModManager`, `Changed`, `Development`), `running {coreVersion, bootstrapVersion, coreSha256, bootstrapSha256, protocol, devBridge, modManager}`, `record`, `offer {kind, installed, release, major, failedBefore, auto, notAutoReason, clickable, line}`, `waitingCount`, `listed`, `journal {state, version, previous, route, starts, …}`, `failed[]`, `kept[]`, `runState`, `lastResult`, `window`, `pendingRestart`, `placement` (mod compatibility C4: the build placed for an Exact server, `{state, server, version, coreSha256, previousVersion, previousCoreSha256, previousDir, previousRecord, placedUtc, joined}`, or null), `placing` (a Use Progenitor press being prepared: `{version, coreSha256, server, phase, error}`, or null), `history`. |
| `self_check` (optional `fetch`) | Read the cached index again and recompute the offer. With `"fetch":true`, first one conditional index request (owner `auto-update`, its backoff respected). |
| `self_apply` | Install the offered release now, as the Install tab's **Update** (the apply window still applies; a development build or a mod-manager install is refused). |
| `self_select` (`version`, optional `sha256`) | Which kept release an Exact server's Core would use (selection only). |
| `self_clear_failed`, `self_forget_attempts` | Forget the failed versions (tried again) and this session's attempts. |

`status` also carries `auto` (`window`, `windowFacts`, `due`, `attempted`, `notice {line, updated, failed, detail}`, `lastSeenNotice`, `modes`, `pins`, `failed`, `verify`, `history`), `majorVersions`, `indexSource {catalogBuiltFor, settingNow, mirror}`, each item's `auto`, `notAutoReason`, `perMod` and `failedBefore`, and `badgeDrawn` (the line under MODS as drawn: `wanted`, `text`, `rect`, `rowRect`, `listLift`, `source`, and `menuRows[]` with each title button's row, caption and subtitle rects).

The prompt itself is shown by `connect` (it calls the same `GameHelper.LaunchGame` the Join Game browser and Direct Connect end in): `connect {"address":"127.0.0.1","port":9976}` returns at once, the join is held, `mods_ui status` shows the dialog (`dialog.kind` `prejoin-updates`), and `mods_ui dialog_choose` answers it (`yes` = **Update first**, `no` = **Cancel**, `Join anyway` by caption). The follow-up questions are `prejoin-restart`, `prejoin-failed` and `prejoin-not-loaded`. `connect` keeps its own launch latch for up to 120 s, so a `connect` right after a held join that was cancelled answers `launch already in progress`; wait it out.

### `auto_update` on a server

AUTOUPDATE3. Nothing waits: an action starts the work and `status` reads how it went (`history`, `outcomes`, and the server's `BepInEx/progenitor-auto-update.log`).

| Action | What it does |
|---|---|
| `status` (default; also `plan`) | `settings` (the bound `[AutoUpdate]` values, `comesBack`), `line` (the Server tab's status), `runState`, `worldLoadedUtc`, `lastPassUtc`, `lastCheck`, `items[{package, installed, latest, auto, notAuto, failedBefore, …}]`, `skipped`, `held`, `attempted`, `restartPending`, `online`, `emptySinceUtc`, `quiet`, `quietRemainingSeconds`, `restartRule` (why Restart when empty would not restart now), `countdownSeconds`, `restarting`, `lastRestart`, `notice`, `outcomes`, `failed`, `verify` (the apply journal), `history`, `bridgeBackup`. |
| `check` (also `apply`) | One conditional index request now and a pass (Automatic applies; Notify reports). |
| `check_cache` | A pass on the cached index body, no request. |
| `tick`, `start_check`, `world_loaded` | Run the once-a-second tick, the after-start check of staged updates, or mark the world loaded, now. |
| `restart_now`, `cancel_restart` | An admin's **Restart now** (the warning while players are online) and its cancel. |
| `rollback` (`plugin`, `package`) | The previous kept version back, pinned. |
| `clear_failed` (`package`), `forget_attempts` | Clear the server's failed list of a package; forget this session's attempts. |
| `backup` (`label`) | One pre-update backup under the batch rules (the setting, the skip window, the retention); `status.bridgeBackup` says how it ended. |
| `notice` | Send the current admin notice to the admins online again. |
| `set` (`key`, `value`) | Write one `[AutoUpdate]` entry of the server's cfg as the operator would (no lock, no admin check), for example `HostRestarts`. Only that key's line of the cfg is rewritten (other keys' hand edits stay). |
