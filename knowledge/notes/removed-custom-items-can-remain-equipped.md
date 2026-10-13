---
title: Removed custom items can remain equipped and spam missing-prefab errors
game: V Rising
game_build: Steam 23521359
progenitor: 7185832f
side: both
area: content
tags: items, uninstall, inventory
route: Audit inventory and equipment, queue UnequipItemEvent, remove exact custom IDs with TryRemoveItemAtIndex
status: verified-live
date: 2026-10-09
evidence: internal research notes (animation controls review, 2026-10-09)
---

## Goal

Remove remnants of an explicitly discarded test outfit without touching unrelated items.

## Route

Audit exact custom GUIDs across inventories and player equipment. For worn matches, queue the normal
UnequipItemEvent with FromCharacter and an automatic bag destination. On a later frame remove the
matching bag rows through InventoryUtilitiesServer.TryRemoveItemAtIndex. Rescan afterward.
Do not directly destroy equipped entities or compact the item-key table.

## What the engine did

The test mod DLL was already absent on both sides. The server retained retired custom keys and five
live items. The joining client reserved key placeholders but had no matching prefab definitions.
Two worn items produced repeated PrefabLookupMap.TryGet errors; the other three occupied bag slots.

## Verification

Verified live on the isolated server and client: three bag items removed; the two equipped pieces
unequipped and removed on the following pass; a third pass found no matching equipment or bag rows.
The Player log stopped growing at the unequip boundary. Cleanup plugin unloaded cleanly.
The tested Core baseline was 2026.10.130.1; the exact image hashes and one-shot source are in evidence.
This evidence covers manual, explicitly authorized cleanup. Current automatic ownership and quarantine behavior is documented in [item management](../../docs/item-removal.md); restoration is not qualified by this older test.
Dropped items, servant equipment and restart persistence were not qualified.

A separate delayed-definition chest test on 2026-10-10 (same game build, ac7c18fee,
both peers) confirmed a known custom key and Amount=1 in the client bag buffer rendered
as an empty slot. Applying the withheld definition made its icon appear automatically in
the still-open chest (captured in the internal research notes).
This one-item test does not qualify missing key rows or large fresh joins.

## Gotchas

### Unloaded does not mean removed from the world

- **Symptom:** missing-prefab errors or invisible occupied slots after removing a content mod.
- **Cause:** live item instances survive while the new client has no definition for their custom IDs.
- **Fix:** perform identity-scoped, server-authoritative item cleanup on deliberate uninstall. Preserve
  items during temporary hot reloads and updates; missing definitions alone do not authorize deletion.

### Delayed definitions resemble missing items

- **Symptom:** an occupied chest slot appears empty during definition synchronization.
- **Cause:** a known item key does not supply the client UI's item metadata.
- **Fix:** finish applying the definition; the open chest refreshes automatically. A loading
  state or completion barrier is still needed before gradual downloads are suitable for normal use.
