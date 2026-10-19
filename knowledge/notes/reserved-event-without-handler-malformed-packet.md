---
title: A conditional client handler plus an unconditional server send floods malformed packets
game: V Rising
game_build: Steam 23521359
progenitor: 07bad52d
side: both
area: networking
tags: custom-events, reserved, capability, handshake, materials, malformed
route: CustomEvents.OnReceive registered only on one branch of MaterialSeamRuntime.Install, while the server's PushPlayers sent 'PrMv' to every online player
status: verified-live
date: 2026-10-18
evidence: client BepInEx log line "NetworkEvent Packet Validation - Malformed packet <hash> Expected 1349668214" repeating about every 3 s on a client whose Bootstrap lacked the material seams; fix and tests on branch fix/reserved-event-consume (ReservedEventConsumeTests)
---

## Goal

Show material-source totals (Build, Craft, Upgrade, CharacterCraft, RefineView) on clients, without harming a client that cannot show them.

## Route

The server pushes a `'PrMv'` view (`0x50724D76`, decimal 1349668214) to each online player every refresh. The client registers its `'PrMv'` handler in `MaterialSeamRuntime.Install`. Rejected: relying on the per-operation capability bits alone, because every current Core sets them by build, whatever its Bootstrap can do.

## What the engine did

Observed live:

- On a client whose Bootstrap had no material seams, `Install` returned at `if (!_bound) return;` before `CustomEvents.OnReceive(MaterialViewWire.EventId, OnView)`. Nothing owned `'PrMv'` on that client.
- Core's dispatcher found no handler and returned false. Bootstrap's receive policy passes an unowned id to the game. The game's event reader rejected it as malformed and logged one line per packet, roughly every 3 seconds, for the whole session.

Source-checked:

- The server gated only CharacterCraft and RefineView, on bits (`MaterialCharacterCraft`, `MaterialRefineView`) that `Handshake.SelfCapabilities` always sets. Build, Craft and Upgrade went to everyone.
- The Core-swap freeze does not call Core's dispatcher, so changing Core's miss rule cannot drop a buffered packet.

## Verification

- Live: the symptom above. The fix has not been run live.
- Offline: `dotnet build`, `dotnet test` (exit code 0). `ReservedEventConsumeTests` covers the route (reserved id with no handler consumed and logged once, other unknown ids still passed to the game), the `MaterialViews` send gate and the client handler registered while unbound.

## Gotchas

### A handler registered on one branch, a send on every branch

- **Symptom:** the game logs `Malformed packet <hash> Expected <id>` every few seconds, where `<id>` is a Progenitor event id in decimal (`0x5072____` is 1349648384 to 1349713919).
- **Cause:** the receiver registers its handler only when a feature is available, the sender sends whatever the receiver can do, and the unowned id reaches the game's event reader.
- **Fix:** three layers. Core's dispatcher now consumes any reserved id with no handler (one warning per id). The receiver registers the handler before any early return and drops packets while the feature is off. The receiver also advertises a capability bit only when the feature is really running (here `MaterialViews`, 8, computed at run time), and the sender checks it per peer. A plugin's own ids are not in the reserved range, so a plugin needs the second and third layers itself.
