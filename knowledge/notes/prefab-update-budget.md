---
title: Repeated item definitions must reuse their synchronization charge
game: V Rising
game_build: Steam 23521359
progenitor: 6a5093c3 + PR40 sync fix
side: both
area: items
tags: items, pack, sync, budget
route: DataPackRuntime manifest updates, PrfD/PrfK/PrfS receive budgets
status: verified-live
date: 2026-10-10
evidence: internal research notes (data-pack scale review, 2026-10-10)
---

## Goal

Update a populated data-only item pack without losing client definitions.

## Route

Count distinct GUIDs for the retained-row budget; separately bound packet work in a
one-second window. Preflight active and historical identities and ledger bytes before
unregistering the old pack. Raising the cap alone does not fix repeated-update exhaustion.

## What the engine did

With 247 test definitions, eight baseline definitions and one tombstone, a version-only
update of 128 items left the server at 255 active definitions but the client at 127.
Unregisters applied; replacement definitions exhausted the cumulative per-join budget.
Stat updates then lacked matching definitions. The native key table had retained the rows.

## Verification

Core 2026.10.148.5 reproduced the failure. The fix build 2026.10.148.6 retained all
247 test definitions and native +100 stat rows after connected pack updates. Two clients
reported the matching counts. Adding the 248th test definition was refused before mutation
because it required 257 distinct identities including the tombstone. Limits depend on
existing content and historical keys; this is not a guarantee of arbitrary world capacity.

## Gotchas

### Replacements are not new rows

- **Symptom:** items disappear after a pack update even though the final active count fits.
- **Cause:** charging every replacement packet consumes a lifetime budget, while unregisters
  still arrive; retired items also occupy stable native key rows.
- **Fix:** retain one identity charge per GUID and bound update work separately. Include
  tombstones, historical keys, pending appends and standalone synced edits in admission.
  Rapid plugin bursts beyond packet limits still require reconnecting; packets are not retried.

### Raising the receive cap does not enlarge the native table

- **Symptom:** a larger pack is refused despite a raised receive limit.
- **Cause:** pack admission used the compiled default, and the ordered ledger was limited
  to one 15 KiB packet. The live native table also had only 2,048 allocated rows.
- **Fix:** admission uses the configured budget; large ledgers reassemble bounded chunks
  before applying any rows. The extended client capacity is advertised separately from
  codec support. At the PR40 extended build, 960 new items plus four Guide rows and one
  retained test ID filled the 1,083-row vanilla table to 2,048; all 960 client stat buffers
  passed. A 961st item was refused without replacing the prior pack. Native growth and
  on-demand index remapping were not tested. See extended evidence in the same review.

A subsequent guarded reservation experiment (2026-10-10) expanded both peers to 4,096 rows, preserved existing indices, and verified inventory index 2,086 with 1,000 test definitions. This internal helper is not called automatically: fresh-join reservation and castle shared-storage maintenance remain incomplete. The game's fixed 21-bit item encoding is not the observed 2,048-row allocation limit.

The subsequent 10,000-definition experiment (2026-10-10) used a separately advertised 16,384-identity profile and manual reservation on both peers. Ordered tables and all stat buffers matched, including inventory/equipment at index 11,086. Hot loading still caused heavy allocations and hitches; fresh joins and castle storage at this count remain unqualified.
