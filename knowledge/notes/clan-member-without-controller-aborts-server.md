---
title: A clan member without Controller aborts the server
game: V Rising
game_build: Steam 23521359
progenitor: d137e5065
side: server
area: dev
tags: clan, burst, stress
route: Dev op stress_clan: clan team prefab instance, TeamUtility.AddUserToClan through its generated wrapper, for an offline synthetic user
status: failed
date: 2026-10-18
evidence: test server log, research note stress-stages-2026-10-18.md section 5c (Progenitor-research)
---

## Goal

Put offline synthetic stress owners (User entities made by the Dev bridge, no client, no character) into clans on the isolated test server, the way the server's own clan create step does.

## Route

The clan create step of the server's clan system was replayed: instance of the clan team prefab, ClanTeam fields, then the game's add-member routine (`TeamUtility.AddUserToClan`) and its role routine. Rejected: raising the client's create event, because an offline user has no connection to raise it from.

## What the engine did

Observed: the clan was created and the first member joined (log lines `stress clan created` and `stress clan join`). On the next frame the server aborted in `ProjectM.Gameplay.Clan.UpdateClanStatusSystem`. The error was an `ArgumentException` from `EntityComponentStore::AppendRemovedComponentRecordError`, thrown inside a Burst job, and Burst aborts the process.

Source-checked (metadata): the status job reads each clan member through component lookups for `ClanRole`, `UserTeam`, `User`, `Controller` and `Health`. A real user has `Controller` (one offline user read on the same save had 56 components including `Controller`, `ClanRole`, `UserHeartCount`). The synthetic user had only `User`, `TeamReference` and the `ClanRole` the join added. The exact unguarded read could not be recovered from the job body.

## Verification

Live on the isolated test server, copied save, Dev at d137e5065, 2026-10-18: the crash above, one clan of two offline owners, first member joined. The save was clean (last autosave before the write). The fix (dd980c9dd) was not run live: it refuses before any write, so it writes nothing.

## Gotchas

### A clan member needs the full user shape

- **Symptom:** the server process exits right after a member joins a clan, with an `AppendRemovedComponentRecordError` from a Burst job in `UpdateClanStatusSystem`.
- **Cause:** the clan status job looks up components a real user and its character carry (at least `Controller`, and `Health` on the controlled character). A synthetic user without them makes the lookup throw inside Burst, which aborts instead of logging.
- **Fix:** only put users with a `Controller` whose character exists and has `Health` into a clan. `stress_clan` refuses others (`status_shape`). Use real users, such as the headless clients' users, through `members`.
