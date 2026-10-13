---
title: Character sheet rows need effective values and native-safe writes
game: V Rising
game_build: Steam 23521359
progenitor: 87831fcf6
side: both
area: ui
tags: character, sheet, stats
route: Core own-player snapshots and native Attributes rows
status: verified-live
date: 2026-10-11
evidence: internal research notes (character sheet native values, 2026-10-11)
---

## Goal

Show effective item bonuses in the existing Attributes sheet.

## Route

Read authoritative player values and update native rows through CharacterSheetFeature and CharacterSheetSettings. Clone owned settings for missing rows.

## What the engine did

Observed: client equipment can identify an item definition while its item entity is unavailable. Native Blood Drain gathering throws instead of producing a value. Ordinary generic list writes for reference-bearing setting wrappers produced invalid row identifiers; the nullable cap setter also failed.

## Verification

Candidate at the pinned commit: ten headless equip/restoration cycles passed both effective values and actual native text readback on an isolated server/client. Empty weapon movement reset and duplicate-free reopening passed. Build and 24 focused tests passed. Three client and one server same-candidate Core reloads passed with old generations released; reconnect values also passed. No screenshot/hover or gameplay effect qualification. Earlier candidate processes crashed; the fixed candidate survived these cycles.

## Gotchas

### Native setting writes

- **Symptom:** Invalid row identifiers or update exceptions.
- **Cause:** Reference-bearing value wrappers and the generated nullable setter do not marshal correctly through ordinary managed writes.
- **Fix:** Use CharacterSheetSettings native-safe list writes and validated cap clearing; exclude Blood Drain from added rows.

### Missing client item entity

- **Symptom:** Valid equipment never receives a matching effective snapshot.
- **Cause:** The client has the definition but no replicated item entity.
- **Fix:** Validate the definition and broken state server-side for that slot, retaining strict instance checks for known entities and character/epoch/expiry checks for every reply.
