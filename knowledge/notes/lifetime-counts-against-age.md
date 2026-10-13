---
title: A LifeTime duration counts from spawn against Age, not from now
game: V Rising
game_build: Steam 23521359
progenitor: PR 118 (port 99dfe1ca)
side: both
area: buffs
tags: lifetime, age, duration, refresh, buffs, timer, units, expire
route: ctx.Buffs handle Refresh(seconds) writes LifeTime.Duration = current Age + seconds
status: verified-live
date: 2026-10-02
evidence: docs/buffs.md (live on main 2026-10-02; bug run 2026-09-13), internal research notes (corrected API live result, pre-port branch 2026-09-14)
---

## Goal

Make `Refresh(n)` on a timed buff (the TimedBuffExample sample's `.rested <seconds>`) leave exactly `n` seconds remaining, however long the buff has already run.

## Route

Read the instance's `Age` and write `LifeTime.Duration = Age + n`, refusing unreadable timing instead of guessing ([buffs.md](../../docs/buffs.md)). Rejected: writing `n` straight into `Duration` (the original bug) and resetting `Age` (it is not networked; the client runs its own integrator, so the HUD would desynchronize).

## What the engine did

- On `main` (PR 118 port): the buff read back with `Duration` 30 and `Age` ticking; `.rested 30` at about 47.6 s elapsed set `Duration` 77.6 and the buff stayed on.
- On the pre-port branch (2026-09-14, same mechanism, code not on `main`): `Age` advanced +5.168 over a 5.17 s gap while `Duration` held, and three refreshes 10 s apart moved `Duration` by the real elapsed time only (no ratchet).

## Verification

**Verified live** on `main` 2026-10-02, isolated test server and Steam client, Core 2026.10.26 (recorded in [buffs.md](../../docs/buffs.md)). The game build was not recorded at the time; the stamp is the installed client build, unchanged since 2026-08-25. Not measured on `main`: the HUD timer and the drain number. The out-of-range refusal has no live check (the sample caps seconds at 600).

## Gotchas

### A short refresh kills the buff instantly

- **Symptom:** `.rested 90`, then `.rested 30` about 31 s later, and the buff is gone on the next poll (live 2026-09-13).
- **Cause:** `Duration` is a total measured against `Age` since spawn; 30 was already in the past.
- **Fix:** write `Age + seconds`. Never write `Age` itself.

### Nothing deployed had the fix

- **Symptom:** `MissingMethodException` from the samples during the 2026-09-14 qualification, and a run that would have exercised the old refresh.
- **Cause:** both deployed Cores predated the fix (checked by symbol, not by hash), and the samples were built against a different Core.
- **Fix:** before testing, confirm the loaded generation and `lastReload.outcome` through the bridge, and look for the new symbols in the loaded image, as in [hot-reload.md](../../docs/hot-reload.md).
