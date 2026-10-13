---
title: Item stat buffers need native stride, not TypeManager metadata
game: V Rising
game_build: Steam 23521359
progenitor: 2391d139
side: both
area: interop
tags: items, stats, buffer, layout
route: DataPackRuntime, dump-checked Layout constants, native stat-buffer replacement
status: verified-live
date: 2026-10-10
evidence: internal research notes (data-pack live review, 2026-10-10)
---

## Goal

Add three main-stat bonuses to a native item clone without changing its donor.

## Route

Rebuild owned buffer bytes from the donor plus authored rows, using shared native Layout
constants checked against the installed server and client metadata. Retain field checks and exact write readback.

## What the engine did

The live TypeManager metadata wrapper returned ElementSize=0 and TypeSize=4 for the stat
buffer. Requiring ElementSize to equal the verified 36-byte stride refused every registration.
Using the established native-layout route let the server register and client synchronize it.

## Verification

Isolated server/client, Core 2026.10.148.5, commit 2391d139: the custom sword added 100
health, physical power and spell power relative to the vanilla sword on both sides. Switching
back restored all three values; reconnect retained the custom bonuses. Test instances removed.
This does not qualify restart persistence, scale capacity or automatic unique-item recovery.

## Gotchas

### Misleading metadata is not a stride

- **Symptom:** stat packs repeatedly refuse registration with an element-size mismatch.
- **Cause:** the TypeManager return wrapper supplies unusable size fields.
- **Fix:** use the dump-verified Layout stride and guarded raw buffer path, as other native
  buffer adapters do; never use the wrapper's zero size as a read/write length.
