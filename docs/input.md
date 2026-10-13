# Local player input (`ctx.Input`)

**Status: landed, not live-verified.** The API, the Sample commands and the Dev `bot` op's migration build, and the game-free half is unit-tested (`tests/Progenitor.Tests/InputTests.cs`). The mechanism it uses was verified live before this implementation: aim and hover through the input controller's `EntityInput` in the necro army sample (2026-09-25), and movement, aim, buttons and hover through command patching in the earlier Dev bot driver (2026-09-24). This code has not run in the game; see [Acceptance procedure](#acceptance-procedure--not-run).

A client plugin can steer the **local** player the way the keyboard and mouse would: aim at a point or unit, hover a unit, walk, press synced buttons, and hand control back. The server receives ordinary input commands and applies its own movement, cooldown and ability rules; this grants no server authority.

It does not move or hide the OS cursor, synthesize OS keyboard or mouse events, drive another player, or control a character from the server (for that, see `ctx.Abilities.ForceCast` and the NPC-control work).

Source: `src/Progenitor.Core/Inputs/`. API rows: [api.md, Input](api.md#input). Example: `samples/Progenitor.Sample/SampleInput.cs`.

## How the game carries input

| Step | What happens | Reference |
|---|---|---|
| 1 | `GameplayInputSystem` fills `EntityInput` from the mouse and keys. | necro research |
| 2 | `CreateInputCommandsSystem` (unmanaged `ISystem`) iterates `(DynamicBuffer<InputCommandBufferElement>, RO<EntityInput>, RO<Controller>)`, appends a command per client tick, and copies that entity's `EntityInput` into `RawInput`. | client metadata |
| 3 | The command buffer is sent. The server reads `MovementInput`, `RotationInput` and `RawInput`. | Dev bot driver, live 2026-09-24 |

The entity in step 2 is the **input controller** (the one entity with `InputCommandBufferElement` and `Controller`), not the `PlayerCharacter`. Writing only the character's `EntityInput` turns the character on the client while the server casts at the real mouse. The character's copy still drives client-side facing and prediction, so both are written.

## Use

```csharp
var result = ctx.Input.TryAcquire(
    new InputLeaseOptions(InputChannels.Aim | InputChannels.Hover | InputChannels.Move, UserPolicy.Yield, reason: "follow"),
    out var lease);
if (result != InputAcquireResult.Acquired) { ctx.Log.LogInfo("input: " + result + "; " + ctx.Input.Status); return; }

lease!.AimAt(target);                 // a client-world unit, re-resolved every frame
lease.Hover(target);                  // targeted spells and interactions pick it
lease.OnFrame = l => l.Move(Steer()); // once per frame, before the write
lease.Ended += why => ctx.Log.LogInfo("input lease ended: " + why);
// ...
lease.Dispose();                      // hand everything back
```

The lease is tracked for the plugin's unload; `Dispose` ends it earlier. Intent persists until changed: a `Move` keeps walking, an `AimAt` keeps aiming. Lease members are main-thread only (hooks, ticks, commands, event handlers, `OnFrame`). After a lease ends every mutator is a no-op, because a lease can end between two calls. Calling a mutator for a channel the lease does not hold throws.

Entities are **client-world** ids. A server entity id is refused (`ArgumentException`).

## Ownership

- **One owner per channel.** `InputChannels`: `Aim`, `Hover`, `Move`, `Buttons`, `All`. Two plugins may hold different channels at once. Acquisition is all-or-nothing.
- **Refused by default.** A request overlapping a held channel returns `Busy` and changes nothing. This differs from `ctx.Camera`, where the last request wins: a silent takeover of the character's controls is worse than a silent camera change.
- **Preempt is explicit.** `Preempt = true` ends the overlapping leases with `Preempted` (their `Ended` handlers run before the new lease is returned) and logs both holders. The Dev `bot` op preempts.
- **No grabbing back.** An acquire from inside an `Ended` handler returns `Busy`, so a preempted plugin cannot retake its channel mid-preempt and the release key cannot be undone by the lease it ended. After the release key every acquire returns `Busy` for 2 s.
- **One writer.** Core's `InputDirector` owns the only hook set; leases record intent. Two leases never race within a frame.
- `ctx.Input.HeldChannels`, `HolderOf(channel)` and `Status` show who holds what.

`InputLeases.TryAcquire(holder, options, out lease)` is the same lease for code with no plugin context (the Dev `bot` op). It is not tracked for any unload; plugins use `ctx.Input`.

## Frame pipeline

Core installs these hooks when the first lease is acquired and removes them when no lease and no pending release remain, and at Core uninstall:

1. **After `GameplayInputSystem`:** validate leases, sample user activity, run each lease's `OnFrame`, then write held, unpaused channels into the controller's and the character's `EntityInput`.
2. **Before `CreateInputCommandsSystem`:** write again, since systems in between may overwrite the component.
3. **After `CreateInputCommandsSystem`:** for each command newer than the last one checked, record whether it already carried the intent (`InputReadback.SourceMatched`), then write the intent into it: `MovementInput`, `RotationInput`, and `RawInput`'s movement, aim, hover and buttons. `lease.LastSent` is the newest such command.

The first pass after an acquire from idle starts at the newest command, so commands already sent are never rewritten. A `ClientFrame` far below the last one seen (a reconnect) re-seeds the same way.

| Channel | Written | Notes |
|---|---|---|
| Aim | `AimPosition` and `AimPositionPlane` = (x, character y, z); `AimDirection` flat and normalized; `AimLength` flat distance; command `RotationInput` = the direction | The floor point the game builds from the cursor (read live 2026-09-25). A point on the character is skipped for that frame. A unit target that disappears keeps its last point (`TargetLost`). |
| Hover | `HoveredEntity` and `HoveredEntityNetworkId` (12 bytes from the unit's `NetworkId`) | A unit that disappears is un-hovered (`TargetLost`). |
| Move | `Movement`, command `MovementInput` | Longer than 1 is normalized. A lease holding `Aim` with no aim set faces the walk. A move-only lease leaves facing to the player. |
| Buttons | Pressed level into `State.InputsPressed` (OR-ed with the player's) | Down and up edges are derived from what was last sent and go on the first new command of a frame only, since one frame can append several commands and a repeated edge reads as a second press. `Tap` presses on the next new command and releases on the next frame's; a tap asked for while the buttons are paused for the user is dropped. |

`ProjectileAimType` and the command's timing fields (`ClientFrame`, `EndTime`, `DeltaTime`, `ServerFrame`, cast counters) are never written.

## Release

A lease ends, and its `Ended` event runs once, on:

| Trigger | `InputLeaseEnd` |
|---|---|
| `Dispose()` | `Released` |
| Plugin unload, every reason | `PluginUnloaded` |
| Core uninstall or Core swap (plugins reacquire in their new `OnLoad`) | `CoreUnloaded` |
| Client world epoch changes | `WorldChanged` |
| Client world gone (disconnect, back to the menu) | `Disconnected` |
| Local character or controller replaced or missing (form change, respawn creating a new entity) | `CharacterChanged` |
| Another lease preempts it | `Preempted` |
| The user presses the release key | `UserPanic` |
| The layout check fails at first acquire | `LayoutRefused` |
| An input hook throws (injection is then disabled until Core reloads) | `Faulted` |

After a lease ends normally, the next new command carries a button-up edge for every button it held and, if it held `Move`, a zero walk, so the server never sees a stuck walk or button. A fault or layout refusal disables injection at once and sends no release; the game's own input then writes the player's real button state without an up edge. Aim is not restored: `GameplayInputSystem` refills it from the mouse the next frame. After a Core swap no release command is sent; the game's own input then writes the player's real state. Entities named by a lease are validated every frame and never persisted.

## User safety and focus

- **The cursor is never touched.** The API does not warp, lock, confine or hide the OS cursor, and does not synthesize OS input. A plugin that hides the cursor for filming owns that change and must restore it on unload.
- **Yield by default.** With `UserPolicy.Yield`, a held channel pauses while the user uses its device: W, A, S, D and the arrow keys pause `Move`; mouse movement or any mouse button pauses `Aim` and `Hover`; Q, E, R, C, T, F, Space and mouse buttons pause `Buttons`. Keys typed into chat or another text field do not count. It resumes after `InputLease.YieldIdleSeconds` (1.5 s) without activity. Paused buttons are released on the next command and pressed again on resume. Pausing does not end the lease (`lease.SuspendedChannels`). Only the default key bindings are recognized; a player who rebound movement keys is detected by the mouse only.
- **Override is explicit.** `UserPolicy.Override` keeps writing while the user is active, for filming and deliberate assist modes. Core logs a warning naming the holder when one is acquired.
- **The user can always take control back.** `Ctrl+Shift+F12` (`[Keybinds] ReleaseInput` in `BepInEx\config\progenitor.ui.cfg`, editable in the Mods settings panel) ends every lease with `UserPanic`, whatever its policy.
- **Focus.** A normal client whose window is in the background stops sending input commands, so a lease there has no effect on the server. The API never takes focus or changes `Application.runInBackground`. `Status.CommandsFlowing` (new commands in the last second, while a lease exists) and `Status.Focused` say whether input can reach the server. Headless (`-batchmode`) clients send while windowless.
- **The server cannot tell.** Injected input is indistinguishable from real input on the server. A server that needs it declared can require the plugin through `ctx.Handshake`.

## Server-side verification

The client cannot prove what the server applied, and a backgrounded client leaves the server's `EntityInput` stale. Verify in layers:

1. **Outgoing commands (client):** `lease.LastSent` / `Status.LastSent`. `SourceMatched = false` means the game had not copied the intent and Core's patch supplied it.
2. **Server `EntityInput`:** read the player's `EntityInput` on the isolated test server (Dev `inspect`). Valid only while that client is focused or headless.
3. **Outcome:** server position change for `Move`; a cast's projectile direction or target, or `AbilityBar_Shared.CastStartedCounter`, for `Aim`, `Hover` and `Buttons`.

There is no server-side plugin API for reading a player's input.

## Bots and headless clients

**Experimental, early development.** The headless bots are a development-only experiment in early development, not a supported feature; see [tools/bots/README.md](../tools/bots/README.md).

The Dev `bot` op (`src/Progenitor.Dev/Bots/BotDriver.cs`) holds one `All` lease as `progenitor.dev/bot` with `Preempt` and `UserPolicy.Override`, acquired by its first non-`status` action and again after the lease ends. `stop` releases everything and ends the lease. Intent does not survive a lease end (disconnect, world change, Core swap): the bot AI reissues aim, holds and hover after a reconnect. `hover` takes a client-world entity with a `NetworkId`. See [tools/bots/README.md](../tools/bots/README.md).

## Layout constants

`src/Progenitor.Core/Layout/layout.txt`, section "Inputs" (client metadata; the server metadata has the same `EntityInput` offsets): `EntityInput` `Movement` 0x0, `AimPosition` 0x8, `AimPositionPlane` 0x14, `AimDirection` 0x20, `AimLength` 0x2C, `HoveredEntity` 0x30, `HoveredEntityNetworkId` 0x38, `ProjectileAimType` 0x44 (bounds the 12-byte network id), `State` 0x48; `SyncedInputState` `InputsUp` 0x0, `InputsDown` 0x2, `InputsPressed` 0x4; `InputCommandBufferElement` `MovementInput` 0x0, `RotationInput` 0x8, `RawInput` 0x14, `ClientFrame` 0x74, `EndTime` 0x88, `DeltaTime` 0x90.

The generator cannot size `InputCommandBufferElement` (its `RawInput` holds a fixed buffer the game's metadata lays out no instance for), so the stride is computed from the last two: `DeltaTime`'s end rounded up to `EndTime`'s 8-byte alignment, 0x98. On the first acquire Core compares every offset and the `NetworkId` size with the interop's `Marshal.OffsetOf`/`SizeOf`, and the stride with the ECS `ElementSize` when that reads non-zero. A mismatch refuses every lease (`LayoutRefused`) and nothing is written.

## Acceptance procedure — not run

Prerequisites: the isolated test server, and either a headless client or a test client the user has released, focused. Deploy the Sample.

1. `.input status`: `layout ok`, `player yes`.
2. `.input aim` near a unit: the character turns to it; the client log's `LastSent` shows a non-zero `hoverNet`; the server `EntityInput` shows the aim point and the unit's network id.
3. `.input walk 1 0`: the server position moves east for 2 s and stops.
4. `.input tap Primary`: one attack; on the server `CastStartedCounter` increments once.
5. On a focused test client, move the mouse during `.input aim`: aim pauses and resumes 1.5 s later. `Ctrl+Shift+F12` ends the lease (`UserPanic`).
6. Unload the Sample, swap Core, disconnect: each ends the lease with the expected reason, and the character stands still with no held button.
7. Record `SourceMatched` for Move in step 3: it settles whether a controller `EntityInput` write alone moves the character (risk 1).
8. Headless bot regression: an existing `tools/bots` scenario (walk, aim, hover + Interact pickup) behaves as before.

## Open risks

1. **Movement source.** The earlier bot driver found (2026-09-24) that the command's `MovementInput` did not follow the character's `EntityInput.Movement`; the necro sample wrote the controller's copy but verified only aim. Core patches the command either way; step 7 of the acceptance procedure records which source works.
2. **Delta compression.** `InputCommandBufferElement.ChangedValues` in the client metadata suggests commands are sent as deltas. Patching a built command is safe only if the changed mask is computed at send time. The bot driver's patch worked live; the server read in step 2 must cover every patched field.
3. **Anti-cheat.** `CreateInputCommandsSystem` has `SpeedHackKey` types. Timing fields are never written; untested against any server-side kick.
4. **Late writers.** A system that rewrites `EntityInput` after `CreateInputCommandsSystem` reads it is not covered; the command patch covers what is sent.
5. **Form and control changes** are treated as `CharacterChanged` until tested with `ControlFormService` and player forms.
6. **User detection** covers the keyboard and mouse with default bindings; a gamepad or rebound keys are not detected, so a `Yield` lease would keep writing until the release key.
