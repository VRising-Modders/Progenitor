# Custom item management

Progenitor records item ownership when a mod uses the content registration API. Mods must not
insert untracked item definitions and expect automatic lifecycle management. The server retains
historical item IDs, owning plugin IDs, source prefab IDs and plugin paths in
`BepInEx/config/progenitor-item-owners.json`. Unregistration is not an uninstall.

## Removal and recovery

A successful permanent package uninstall records removal intent. The first Core generation can
also recognize a recorded owner absent from a complete plugin-file scan. Failed loads, moved or
disabled copies, staged replacements, temporary unloads and Core swaps do not authorize removal.
Unreadable ownership data or incomplete disk scans preserve items. Older untracked items need an
explicit migration; older ledger records without source information must be registered again.

The ledger keeps every owner ever recorded, so it grows; 10,000 rows is about 2.6 MB. The server
parses it once per change. Later reads reuse that parse, which is matched to the file's content
hash, or to its size and write time once the file is more than 2 s old. Each removal tick reads the
plugin folders once and reuses that read for every owner it checks. Game-free tested, not
live-verified.

Before removing a supported inventory stack, Progenitor durably records its quantity, source,
owner and destination in the current save's `progenitor/item-quarantine.json`. Its companion
`item-world-id.txt` identifies that recovery journal. Do not separate these files from the save.
Native removal is followed by inventory readback. Unsupported items remain intact in the game.
Definition records remain as historical tombstones. Native item-key rows are never compacted.

An admin can open **Mods → Server → Operator → Saved mod items**. When the original mod and item
return, the panel asks whether to attempt restoration. It never automatically restores on reinstall.

- **Restore / retry:** attempt eligible saved stacks; a full bag remains pending.
- **Keep:** retain saved records without initiating restoration.
- **Discard:** confirm permanent abandonment of eligible saved records. Audit tombstones remain.

The panel reports pending, unavailable and restored records, with reasons. Counts describe saved
stacks, not individual units. Only freshly authorized server admins can issue these decisions;
the client cannot edit the server journal. A missing or incompatible definition remains pending.

## Best-effort contract and current limits

Restoration never promises recovery. It requires the same mod, item ID, donor and compatible plain
stack metadata, plus the original player, character and bag. It does not replace existing contents
or equip restored items. A same-prefab stack already in the bag currently blocks restoration so
readback remains unambiguous. Partial or uncertain native operations are blocked from replay.

**Current adapter scope is deliberately narrow:** plain player-bag stacks in the same running
world and Core generation. Opaque in-memory incarnation tokens prevent restoration to a deleted
and recreated character. Tokens are invalid after a restart, world load or Core replacement;
saved records remain, but automatic restoration then reports unavailable. Raw entity handles,
character names and plot positions are not persistent recovery identities.

Unique equipment (including armor and its durability/rolled properties), castle containers,
servant equipment and ground drops are preserved, not removed by this increment. Persistent
character/container incarnation IDs, complete unique-item codecs and an ordered game-save
completion contract are still required for recovery across restarts. A newer save-file timestamp
alone does not prove a particular inventory mutation was saved. Current completion means native
readback in the running world, not crash-proof coordination with the game's save transaction.

## Verification

The ownership policy, atomic journal transitions, stale-writer rejection, retained decisions and
network request policy have offline tests. The integrated code is compile-verified; the new
quarantine/restore UI and adapter have **not been verified live**. Earlier destructive-cleanup
acceptance is historical and does not qualify the new restoration path.

Acceptance procedure — not run: register a plain stack item; remove its mod through the supported
installer; inspect the saved record and inventory; reinstall; choose Keep, then Restore; verify one
restoration and no duplicate after retries. Repeat with a full bag, a deleted character, changed
donor, Core replacement and an interrupted claim. Confirm unsupported armor and castle contents
remain unchanged. Remove test plugins and items after the batch.
