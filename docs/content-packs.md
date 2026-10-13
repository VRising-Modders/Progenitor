# Data-only item packs

**Under development; limited live qualification.** A pack describes cloned native items
with JSON and needs no per-item plugin or compiled assembly. This format does not provide
custom models, textures, executable code or downloaded packages.

The example is [`samples/content-packs/bone-sword`](../samples/content-packs/bone-sword).
It registers the intended item id `example.bone-sword.plus100`, clones the native bone sword,
and declares +100 max health, physical power and spell power. An isolated server/client test
verified those three deltas, restoration when switching to the donor sword, and reconnect
with the custom sword equipped (verified live on 2026-10-10).
The earlier 247-definition run also verified save/restart and cross-player transfer. Incompatible-client refusal and broader multiplayer remain unqualified.

## Install, update and remove

With a Core build containing the pack loader, copy the sample's two files into
`BepInEx/progenitor-content/example.bone-sword/` on the server. The installed directory name
must match the manifest id; the repository's sample directory is named `bone-sword` for browsing.
Core starts a scan every five seconds, visits one pack folder per frame, and registers packs under owner `content.<id>`.
Stat packs wait if connected clients have not advertised item-stat support. While such a pack is
active, joining clients must answer the handshake and advertise that capability.
An item pack is data and does not create a separate mod entry in the Mods UI.

To change a pack, edit the definitions and update the manifest's content hash. Write the
complete pair before waiting for the next scan. Invalid edits retain the last valid loaded
version; malformed input does not remove an existing pack. Live updates must retain existing
item IDs and donor choices; retiring items requires explicit pack removal. Changes to the prefab definition
may require regranting or re-equipping an existing item; installed item behavior awaits live
qualification.

Runtime activation and rollback process at most eight items or four milliseconds per frame.
The time limit is soft: a single registration and the full pack preflight can run longer.
Valid updates temporarily mix old and new definitions; this is not an atomic transaction.
Startup registration remains synchronous before saved items are deserialized.

To request removal, rename `pack.json` to `pack.removed` while leaving the pack directory and
content root in place. Missing folders are not an explicit removal request. Unregistering a
definition does not establish that previously granted or equipped items have disappeared;
remove test items before removing their pack.

## Pack directory and manifest

A pack directory contains exactly two regular UTF-8 files, `pack.json` and `items.json`.
Subdirectories, reparse points, links, DLLs, scripts and extra files are refused. Each file
is limited to 256 KiB. The manifest is strict JSON:

```json
{
  "schema": 1,
  "id": "example.bone-sword",
  "version": "1.0.0",
  "content": "items.json",
  "sha256": "<64 hexadecimal characters matching the exact items.json bytes>"
}
```

Use the checked-in example for a manifest with a real hash. Unknown fields and duplicate
keys are refused. `content` must be exactly `items.json`; it cannot select an arbitrary path.
`id` is 1–48 lowercase ASCII letters, digits, dots or hyphens, with nonempty segments whose
ends are letters or digits. `version` is 1–32 ASCII letters, digits, dots, hyphens or plus
signs. Schema 1 has no dependencies or executable hooks.

The SHA-256 covers the exact file bytes, including whitespace and line endings. It detects
changed contents; it is not a signature or publisher trust claim. After editing `items.json`,
run this from its directory to obtain the new manifest value:

```powershell
(Get-FileHash -LiteralPath .\items.json -Algorithm SHA256).Hash.ToLowerInvariant()
```

## Item definitions

`items.json` uses the [JSON item contract](content-files.md#the-file). A pack defines at most
128 items. Every item id must begin with its pack id followed by a dot and a nonempty suffix:

```json
{
  "schema": 1,
  "items": [
    {
      "id": "example.bone-sword.plus100",
      "cloneOf": "Item_Weapon_Sword_T01_Bone",
      "name": "Empowered Bone Sword",
      "description": "Adds 100 health, physical power and spell power.",
      "statBonuses": {
        "maxHealth": 100,
        "physicalPower": 100,
        "spellPower": 100
      }
    }
  ]
}
```

Native prefab names and nonzero signed GUID hashes are accepted as clone sources. Definition
validation is all-or-nothing per pack: malformed stats, unknown fields, duplicate keys, invalid
item ids or a hash mismatch produce no registrations. Registry source validation and collision
checks remain required before applying a pack.

Stat bonuses are additive native equipment data. Install the pack on the server and use a
compatible Core build on both peers: the server sends the native clone definition via `PrfD`
and its stat bonuses via a versioned `PrfS` packet immediately afterward. Clients do not need
a copy of the pack files. This synchronization transfers item parameters, not executable code
or custom model/texture assets. A headless player dropped a custom sword and a full client
picked it up through normal game input; equipping it retained all three +100 bonuses on
both client and server. Incompatible-client behavior still awaits live checks.

Core streams joining players' definitions after the ordered key ledger and handshake. It
prioritizes their inventory/equipment, requested open-container contents and nearby equipment
or drops, then nearby container contents by distance, then the remaining catalog. Proximity
is a delivery hint, not a visibility or access check. Every ninth delivery serves the oldest
pending definition so background work progresses. Mods and packs need no extra delivery code.

Priorities refresh every half-second from the player's current position, including after a
teleport. Old proximity priorities expire; received definitions are not downloaded again.
Open-container requests expire after 1.25 seconds unless renewed. The shared spatial index
refreshes periodically with bounded owner reads; its initial query snapshot and individual
native operations can exceed the soft time budget. Delivery allows at most four definitions
per 20-millisecond slice across clients, with a soft four-millisecond budget. Stats follow
their definitions; synchronized edit layers follow the completed stream. Runtime registration
updates retain their existing ordered broadcast path.

Normal play has no loading notice. An item whose key has arrived but whose definition is
missing can still look like an empty slot temporarily; its icon appears automatically when
the definition applies. An internal, non-persisted debug notice can show pending occupied slots
during tests and defaults off each Core generation. There is no inventory interaction barrier.
The 128-item live test verified an open chest's late-listed definition arrived before most of
the catalog and all stat definitions completed. Full 10,000-item fresh joins remain unqualified.

## Validation and runtime status

`Content/DataPackManifest.cs` reads and validates a local directory without registering anything.
It returns the parsed manifest and item document to the Core loader, which owns registration,
collision policy and lifetime. Offline tests exercise manifest bounds, content hashes, namespace
validation and item refusals. This guide does not claim custom-asset delivery or completed
multiplayer qualification. Up to 248 test definitions synchronized with two clients in the
isolated instance. A connected pack update exposed a receive-budget bug; the tested revision
and fix qualification are in the internal research notes. This is not a world-instance capacity guarantee.
The saved test sword survived restart at 247 definitions and retained bonuses after another
cross-player pickup. Activation can cause a load hitch; large distributed instance counts
and sustained performance have not been qualified.

Client Core replacement can lose remote definitions; reconnect before testing packs after a Core
swap. Stat messages refuse unmatched definitions instead of creating incomplete items. Existing
unique item instances are not rewritten by pack updates; regrant the test sword to check changed
bonuses. No items are granted automatically by installation.

On server startup, packs register through the existing pre-deserialization step so saved
custom items can resolve their prefab before the game loads inventories. Normal hot-update
polling waits for a fully initialized world. Admission includes retained identities, pending
appends, standalone edits, encoded ledger size and native key-table capacity.

The [item-count acceptance procedure](content-pack-scale-tests.md) provides an offline
fixture generator and staged measurements. Capacity testing is gated on the single sword
passing live checks. The default identity budget remains 256. For the extended profile, set
`Networking.MaxSyncedPrefabsPerJoin = 1024` in `BepInEx/config/progenitor.cfg` on both
server and client, then reload Core and reconnect. Values clamp to 1–16384; values above 1024 are for controlled diagnostics only. The handshake
advertises the enabled extended profile and refuses clients without it when the server
uses more than the default budget; connected pack activation makes the same check.

Large ledgers use bounded 15 KiB chunks with at most 1,114,124 bytes of reassembly and 16,384 rows. The experimental 16,384-identity profile requires its own client capability; a 1,024-profile client cannot satisfy it. Increasing these guards does not add native table capacity or make 10,000-item joins supported.
Only a complete validated ledger establishes key order. Fresh joins with 500 and 960 test
items passed on the isolated full client, including all three stat rows on every definition.
The 960-item run filled the native 2,048-row table (1,083 vanilla, four Guide, one retained
test ID, 960 new items); item 961 was refused before replacing the working pack. This is
the current allocation boundary, not a proven engine maximum. Loading item details on
demand does not remove the global key-table requirement.

Automatic table growth is not enabled. An internal reservation experiment expanded both
peers to 4,096 rows and synchronized 1,000 test definitions, including an equipped item
above index 2,047. Fresh-join reservation and castle shared-storage maintenance are not
integrated. Save/restart with saved items at that count remains unqualified.

The internal reservation bound is 16,384 rows for the 10,000-definition experiment. Fresh-join definitions are paced, but automatic native reservation and full-profile join qualification remain incomplete. Hot activation is limited to 50 slices per second, each with at most eight operations and a soft four-millisecond budget.

A 10,000-definition connected experiment (2026-10-10) passed complete table/stat agreement and equipped index 11,086 after manual reservation. Loading took roughly six minutes and produced substantial allocation pressure and hitches. This does not qualify fresh joins, castle storage, or saved-item restoration at that count.
