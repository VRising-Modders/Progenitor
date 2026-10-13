---
title: Register data-pack items before save deserialization
game: V Rising
game_build: Steam 23521359
progenitor: PR 40 Core 2026.10.148.6 hash311bd05e
side: server
area: items
tags: items, pack, save, restart
route: Existing PrefabRegistry DeserializeLoadFile prefix, then ApplyPending
status: verified-live
date: 2026-10-10
evidence: internal research notes (data-pack scale review, 2026-10-10)
---

## Goal

Preserve a saved unique item supplied by a data-only content pack across server restart.

## Route

Scan and register packs in the existing pre-deserialization prefix before applying pending
prefabs. Normal hot-update polling still waits for world initialization. Repeated prefix
calls skip unchanged versions/hashes; no additional native hook is needed.

## What the engine did

The old loader waited for LoadPersistenceSystemV2.Initialized. By then deserialization
had encountered the custom GUID as unknown and SaveSafety had discarded its unique entity.
The inventory slot still reported the correct item GUID and amount with a dead reference.
A visible inventory row alone did not prove the saved item survived.

## Verification

A saved test sword failed under the old loader. After registering before load, restart
restored the sword as a live unique entity. A headless player dropped it, the full client
picked it up, and equipping it still added 100 health, physical power and spell power.
Client and server agreed; switching to a vanilla bone sword restored the baseline.
Only the isolated test instance was used; exact test instances were removed afterward.

## Gotchas

### Loading after initialization is too late

- **Symptom:** the saved custom item appears in inventory but cannot resolve its unique entity.
- **Cause:** its definition is registered after native save deserialization.
- **Fix:** use the established pre-load registration/apply seam and verify both entity
  existence and equipped behavior after restart, not just the inventory GUID.
