---
title: Tile height queries select bands, not surfaces below the probe
game: V Rising
game_build: Steam 23521359
progenitor: 3ee9650ba
side: server
area: terrain
tags: teleport, castle, height
route: DebugMapPolicy.PickFloor through GroundProbe.TryGetHeight
status: verified-live
date: 2026-10-19
evidence: https://github.com/VRising-Modders/Progenitor/pull/167
---

## Goal

Land debug map teleports on a castle's ground level instead of its roof.

## Route

Use the existing gameplay-height query with probe Y -51 to request height level zero. The tile world clamps that level to each cell's base layer; the returned height, not -51, is the destination. Preserve the upper-layer read for diagnostics and the existing non-castle behavior.

## What the engine did

Source-checked: the float3 query selects truncate(clamp((Y + 51) / 5, 0, 20)). A probe at 14.5 therefore selects the same band as a roof at 15.5. Cell layer selection clamps requested level minus BaseHeightLevel into the available layer range. It is not a downward raycast.

## Verification

On 2026-10-19 the user first supplied a screenshot of the failed roof landing, then confirmed "that worked" after server Core 2026.10.160.1+3ee9650ba3 loaded at generation 5. Loaded hash matched the built image. Sixty-eight teleport-related tests passed, including a quantized fake reader on elevated and low terrain. This verifies the user's castle case; not every castle, cave, slope or boundary interpolation was exercised live.

## Gotchas

### Repeated roof band

- **Symptom:** Descending the probe by one metre still lands on the roof.
- **Cause:** The query selects five-metre bands and can return a height above the probe; the old walk stopped on that answer.
- **Fix:** Query the base gameplay layer for castle destinations. Reject missing, nonfinite or below-minimum base heights instead of falling back to the roof.
