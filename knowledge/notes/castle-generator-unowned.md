---
title: Generated castles need a separate owner claim
game: V Rising
game_build: Steam 23521359
progenitor: 30bfc0d313
side: server
area: castles
tags: castle, generation, ownership
route: Scoped GenerateCastleSystem with GenerateCastleEvent and Castle API readback
status: verified-live
date: 2026-10-18
evidence: ../../../Progenitor-research/questions/stress-server-placement-answer-2026-10-18.md
---

## Goal

Generate a test castle entirely on the server without a connected builder.

## Route

Check an empty territory and the prefab collection, create one isolated
GenerateCastleSystem, submit GenerateCastleEvent with a temporary admin User,
call Update once, then destroy the request, system and temporary User in the same call.
Inspect the castle on later frames. This is a test-world adapter, not a player build API.

## What the engine did

Verified live: the generator was absent from the normal server world even though
its prefab collection was present. The scoped system generated a castle with a heart,
320 floors, 1238 structures, 33 containers and 30 stations. Its owner was null.
Granting 100 blood essence produced fuel quantity 99 and a nonzero fuel end time
when the game consumed the first item. A second generation refused the occupied
territory without changing its heart or structure revision.

## Verification

Verified live on copied Moo World, territory 0, server Core/Dev 2026.10.93.
Log sequences 8962/8968 record generation; 9242/9248/9253 record inventories and fuel;
9317/9318 record occupied refusal and temporary User count restored to three.
Ownership, clans, individual resource clearing and save reload remain unverified.

## Gotchas

### Event exists but its consumer does not

- **Symptom:** A generation event has no active consumer in the normal world.
- **Cause:** The generator is not part of the ordinary server update loop.
- **Fix:** The test adapter runs a scoped system once and destroys it immediately;
  refuse if a generator or pending generation event already exists. Do not enable QA mode.

### Generated does not mean owned

- **Symptom:** Castle API discovers the castle but returns a null owner.
- **Cause:** Generation connects structures to the heart without claiming it for the admin.
- **Fix:** Keep ownership as a separate pending qualification; do not report a completed
  owned castle or proceed to bulk population until the claim path passes live verification.
