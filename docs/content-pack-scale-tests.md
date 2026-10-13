# Item-count acceptance procedure — partial live qualification

This measures distinct registered item definitions, not inventory stack count. It does not
establish capacity for custom meshes, textures or creatures. All fixtures reuse the bone sword.
The generator is offline-only and never installs packs, grants items or contacts a game process.

## Prepare offline

From the repository root, with Node installed:

```powershell
node --test tools/content-pack-scale/generate.test.mjs
node tools/content-pack-scale/generate.mjs C:\Temp\progenitor-item-scale 6
```

Use a new scratch output directory. The optional final argument is the number of other
registered definitions, including the original bone sword example and other framework/mod
content; `6` is illustrative, not a measured baseline. Recalculate it before live testing.
The generator refuses existing output and obvious game content directories.

Stages contain 1, 10, 50, 100, 128, 129, 250, 256, 257, 500, 1,000 and 10,000 distinct items.
Each stage is a complete replacement fixture set with stable IDs and at most 128 items per
pack. `matrix.json` records counts, bytes and expected guard outcomes; `measurements.csv`
is an empty results template. Neither file belongs inside an installed pack directory.

The default definition guard is 256 across registered content. With six other definitions,
250 test definitions reach that guard. Larger stages are **refusal tests only**, not throughput
benchmarks. Do not disable the guard to run them. The extended profile in
[content packs](content-packs.md) permits up to 1,024 identities with bounded chunked ledger
transport; both peers must opt in. Native key-table capacity still applies. Per-pack activation is not transactional
across a stage: an over-budget stage may partially register before another pack is refused.
For the extended profile, record the configured budget and actual native table headroom
separately; the generated default-profile expectations do not apply. The generated matrix estimates active definitions only. Runtime admission also includes
retired identities, historical item-key rows, pending registrations and encoded ledger bytes.
The default receive budget allows 256 distinct identities; existing identities can update
without spending another row. Definitions/placeholders and stats have separate 1,024-packet
one-second receive limits. Do not raise guards to interpret refused stages as capacity tests.

## Gate before increasing counts

When the isolated client/server are authorized and available, test the original
[bone sword pack](content-packs.md) first. Record framework/game versions and pack hashes.
Compare a vanilla bone sword and the custom sword under identical character/buff conditions.
Expect exactly +100 max health, physical power and spell power relative to the donor; after
unequipping, expect the original baseline. Recheck the vanilla sword to detect donor mutation.
Confirm a compatible joining client sees the correct name, appearance and bonuses, and that
an incompatible client is refused as documented. Stop if any check fails.

## Run one stage at a time

1. Capture a no-scale-pack baseline: registered definition count, server/client memory,
   join time, frame times and inventory opening time. Use the same character, location,
   graphics settings, observation interval and connection for comparisons.
2. Complete cleanup of the previous stage and reset the client connection before each
   connected-activation measurement. Confirm the no-scale baseline and available packet
   budget again. Using only the isolated test instance, install the selected stage's pack directories
   under its `BepInEx/progenitor-content`. Do not copy the whole output root. Start with
   one definition, then progress only after the prior stage passes.
3. Separately measure activation with a connected compatible client and a fresh client
   join after activation. Record end-to-end time until all expected definitions and stats
   are available; subtract the baseline when assessing cost. The loader polls every five
   seconds, so wall time includes scan delay. Record server and client counts, not just
   successful registration log lines. Repeat timings three times where practical.
4. Sample the first and last item of every pack, especially across the 128/129 boundary:
   correct identity, name, icon, +100 bonuses, equip/unequip and unchanged donor. Grant only
   sampled items initially. Many granted instances are a separate later stress dimension.
5. Record errors, memory after settling, frame-time median/p95 and inventory response.
   Stop escalation on missing definitions, lookup errors, wrong stats, persistent growth,
   crashes or clearly degraded responsiveness. Agree performance thresholds from baseline
   before interpreting a stage as acceptable.
6. Verify isolated save/restart and reconnect for each accepted stage; check sampled items
   retain identity and behavior. Core replacement is a separate test requiring client
   reconnect; hot-reload success is not implied by pack activation.

Ascending fixture stages retain IDs, allowing a separate incremental-update experiment.
Repeated definitions reuse the row charge, but excessive bursts can still exhaust the
receive rate limits and require reconnecting. Treat that as a synchronization limit,
not proof the game cannot handle the item count. The primary capacity measurements
use a clean stage baseline and fresh connection. Write complete file pairs before the next
scan. Do not install different stages alongside one another.

## Cleanup and evidence

Remove every granted test item from the isolated world before retiring packs. Unique-item
quarantine is incomplete; unregistering a definition is not proof its instances are gone.
Rename each installed scale pack's `pack.json` to `pack.removed`, then verify no active scale
definitions or stale inventory references remain. Missing directories alone are not removal.
For descending or clean-start comparisons, complete cleanup first and reconnect to reset
per-join synchronization budgets; do not shrink a live pack by deleting item IDs.
Retired-definition placeholders can still consume synchronization budget after removal.
Count those in the baseline too; if cleanup cannot recover the intended clean baseline,
use an authorized disposable test snapshot with no scale-item history. Do not delete ledger
or save records to force the numbers down.

Store actual results, logs and captures outside this repository in the internal research notes
and update their index. Record stage, hashes, other-definition count, clean/incremental mode,
versions and run number. Generated fixtures and passing offline tests are not live capacity
evidence. The isolated instance synchronized 248 test definitions with two clients and
retained +100 bonuses after a headless-to-full-client drop/pickup. Pack updates at that
boundary exposed a cumulative-budget failure; the fix and its exact qualification are
recorded in the internal research notes.
Large distributed instance counts, repeated timing runs and incompatible clients remain
unqualified. The full procedure above has not been completed.

Extended-profile fresh joins verified 500 and 960 test definitions on one full client; all
bonus rows matched. Item 961 was refused at the observed 2,048-row native allocation.
Incremental runtime activation is verified with a connected client at 500 then 960 definitions.
A 40-second update sample peaked at 125 ms server frame time and 166 ms Core work;
individual operations and preflight can exceed the soft budget. This improves the earlier
1.7-second synchronous activation hitch but is not a smooth-frame guarantee. Native table
growth and restart with saved items at that count remain unqualified.
