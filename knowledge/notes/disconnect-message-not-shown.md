---
title: The disconnect screen shows only the reason code, never the kick message
game: V Rising
game_build: Steam 23521359
progenitor: 41797b5f
side: both
area: networking
tags: kick, disconnect, reason, message, ConnectionStatusChangeReason, vanilla, unmodded, chat
route: PlayerControl.Disconnect -> ServerBootstrapSystem._NetServer.Disconnect(connection, reason, message)
status: verified-live
date: 2026-10-05
evidence: screenshots <tools>\tsrepro\kick-*.png and the 2026-10-04 unmodded-client run (server log "no handshake answer within 20 s" then Kicked); docs/api.md PlayerControl row
---

## Goal

Tell a player why the server removed them. The case that matters is a player without Progenitor joining a server that requires it, who gets only the game's own screens.

## Route

`PlayerControl.Disconnect(world, userEntity, message, reason)` calls the server network layer's `Disconnect(connection, reason, message)`. The message travels to the client and lands in `ClientBootstrapSystem.DisconnectReasonExtraData`. The game's own kick event carries no text at all.

## What the engine did

Observed on the Steam client against the isolated test server. A server snippet disconnected the player under each of these reasons with the same message. The disconnect screen read "Communication with server was lost. Reason: …":

| Reason | Screen text |
|---|---|
| `Kicked` | "You were kicked from the server" |
| `IncorrectVersion` | "IncorrectVersion" (the enum name) |
| `Banned` | "You are banned (Monday, January 1, 0001 12:00:00 AM)": the extra data is parsed as the ban expiry |
| `Unknown` | "k_ESteamNetConnectionEnd_App_Min" |

The message text never appeared. On 2026-10-04 an unmodded client (BepInEx disabled) joining a server that required Progenitor mods was in the world for about 20 s, then saw only the `Kicked` line. The full reason existed only in the server log.

## Verification

**Verified live** 2026-10-05 for the four reasons above, on the modded Steam client (the disconnect screen is the game's own UI). The `Kicked` case was also seen on an unmodded client on 2026-10-04. Not tested: the other 16 reason codes.

## Gotchas

### The kick message looks delivered but is never displayed

- **Symptom:** A kick with a clear message, and the player sees only "You were kicked from the server". The docs claimed the message was "shown on their end".
- **Cause:** The client keeps the message in its disconnect data, but the disconnect screen formats only the reason code. `Banned` alone reads the extra data, and only as a date.
- **Fix:** Use `Kicked`. Before the kick, send the player a system chat message with the reason, repeated until the kick so they notice it. On a Progenitor client, show it in Progenitor's own UI.

### Another reason code doesn't help

- **Symptom:** Trying a "better" code to carry the text.
- **Cause:** `IncorrectVersion` and `Unknown` print raw identifiers, and `Banned` turns the text into a nonsense date.
- **Fix:** Keep `Kicked`; it is the only one that reads naturally.
