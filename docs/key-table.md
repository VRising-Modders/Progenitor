# The key-table index model

**Status: partial.** Roadmap item **M2.12**. Narrow join-time agreement is implemented and
offline-tested. Paired live acceptance is prepared as live-rig cases and has not been run.
Save-folder ledger persistence and operator prune are not. The research this rests on (load-path
facts, raw layout, append algorithm, apply gates, tombstones) is in the internal research notes.

---

## Current contract (narrow join agreement)

Saves store the prefab **guid**, not the ItemKeyLookup index. Index motion between boots does not
by itself delete items. What this contract closes is **peer disagreement in one session**: two
sides that registered the same custom items in a different order used to append at their own
`list->Length` and could decode the same inventory slot as different items.

**Server order is authoritative.** On `PlayerConnected`, after the server has applied its own
rows, it sends one `PrfI` (`0x50726649`) packet — `baseLength`, `count`, an order hash, then that
many `(guid, id)` rows in tail order — **before** `PrfD` / `PrfK`. The client:

1. Opens the apply gate from `PrfI` without dumping local registrations in plugin-load order.
2. Recomputes the hash; a mismatch refuses the whole packet.
3. Rebuilds its tail in the server order (`index = baseLength + position`).
4. Occupies a **reserved** row for a guid it does not have (so later indices stay aligned) and
   logs that the item is unusable.
5. Refuses a conflicting guid/id pair and any local extra the server did not list, with a logged
   reason. Those items are not appended. They stay unusable rather than taking someone else's index.

A client vanilla length that does not equal `baseLength`, a tail that would exceed capacity, a
duplicate guid, a truncated/oversized/corrupt payload, or an unreadable `ItemKeyLookup` refuses
the **whole** join-time item table: custom items stay unusable for that session. A second `PrfI`
is ignored (the first accepted order stands). A `PrfI` that arrives after `PrfD`/`PrfK`, or a
join that never accepts `PrfI`, refuses every custom-item append: the items do not resolve at
all, so an inventory index cannot decode as a different custom item. Buff clones are unchanged
(they never occupy a key row). Handshake still carries plugin guid/version only; it does not
encode this order.

An unhandled `PrfI` is dropped (never passed to the game). **Dropping is not compatibility.**
An old Progenitor client that then applies `PrfD` in its own registration order is the
Hermit's-Shawl case. On the named join path, `KeyJoinSession` therefore refuses custom-item use
rather than kicking the session (a vanilla client with no Progenitor content never runs this
path). The observable is the log `KeyAgreement: refused custom items this join: no PrfI key-table
order was accepted before PrfD/PrfK` and `PrefabRegistry: ignoring 'sample.ember' … the item
stays unusable`.

A Core swap does not reopen local-order append. Three session states: this join **accepted** a
`PrfI` (rebuild in server order), this join **refused** (skip every custom item), or **unknown**
(no ledger yet). On the client, unknown is not permission to append: while the live-world apply
gate is open, custom items stay unusable until a `PrfI` is accepted or the join ends
(`WorldReady`). A missing, throwing, or unpack-failed Bootstrap `TakeJoinSession` handoff
refuses the session the same way — it does not degrade to idle. The server process (dedicated
or hosted/single-player) is the authority and still appends in its own order. A vanilla client
never runs this type.

A Core swap of an **established accepted session** keeps that ledger. Export writes it from
`SnapshotContent` (before Uninstall resets the Core static); import restores it before
`NotifyLiveWorldAfterCoreSwap`. EventHooks Uninstall clears `ReadyWorlds`, so the new
generation's first Frame republishes `WorldReady` for the same world — that republish is not
a rejoin: the apply gate stays open and the restored session is not `Reset`. A later genuine
new client world still closes the gate and requires a fresh `PrfI`.

Live mixed-generation join, Core-swap-after-refuse, and Core-swap-while-idle stay gated on an
authorized isolated pair.

Game-free types: `KeyLedger`, `KeyIndexPlan`, `KeyAgreement`, `KeyJoinSession`, `KeyLedgerPacket`.
The live table read/write stays in `ItemKeyTable`. There is **no** index-based save migration and
no `keytable.json` yet — that remaining design is below.

### Verified and open

**Compile-verified / offline-tested:** `dotnet build` of Core; `dotnet test` filter
`Key|PrefabEdit|PrefabApplyGate|PrefabRegistry|ReceiveSlot`. Fail-closed in source: a client
with no accepted `PrfI` does not append custom items in local order (named join, Core swap of
a refused or idle session, and a missing/failed Bootstrap join-session handoff). The handoff
degrades to refused-and-unusable, not idle. An **accepted** ledger survives a Core swap of an
established session: export/import through Bootstrap `ContentMemory`, then the live-world
gate, then the same-world `WorldReady` republish; ember still resolves at the server's index
and the tail is not rebuilt in local order. Server / host process and a later healthy `PrfI`
are unaffected. Independently reviewed through internal review R88; that review's two remaining
doors are closed in source by this contract. The failing live case was a Core hot swap in an
established session (`KeyAgreement: accepted server key-table order baseLength=1083 …` then,
immediately after generation 1→2, `refused custom items this join: no PrfI … before PrfD/PrfK`
and `ignoring 'sample.ember' … refused this join`). **Verified live:** not this job. **Not in a candidate.**

Paired live gates (isolated pair only, `<testserver>`, ports 9976/9977; do not
touch `C:\VRisingServer`; client must be available):

| Case | File | Gate |
|---|---|---|
| Matched key table | `tools/live-rig/cases/m2-12-key-table.json` | PrfI-before-PrfD on both sides, matching apply-log `index=N` for ember, `.ember` inventory guid identical, `.content define` must not move ember |
| Incompatible client | `tools/live-rig/cases/m2-12-incompatible-client.json` | Old client (no `PrfI` handler): custom items do not resolve; log names the missing ledger. Do not treat a dropped packet as a pass |

```
node tools/live-rig/rig.mjs run m2-12-key-table
node tools/live-rig/rig.mjs run m2-12-incompatible-client
```

**Acceptance procedure — not run.** Failed cleanup stops the run and marks the case incomplete.

---

## 1. The problem in one paragraph

`ProjectM.ItemKeyLookup` is a `NativeList<PrefabGUID>` plus its inverse map. Two networked
fields — `InventoryBuffer.ItemType` and `SharedCastleInventoryItems.ItemType` — travel as an
index into that list instead of as a guid. The game builds the vanilla part deterministically
(walk, `IntroSort`, rebuild the map); Progenitor appends mod rows at the tail afterwards, at
`index = list->Length`. So a mod's index is a function of **plugin load order**, which changes
whenever an operator adds, removes or renames an unrelated mod — and, within a single boot, of
which applies happened to be retried. Three correct behaviors then chain badly: a moved row is
refused by `KeyRowStillHolds`, the refusal leaves the guid out of `PrefabLookupMap`, and the
M2.11 load guard drops every entity holding it.

**The correction that shapes the design:** the save file stores the *guid*, not the index
(research §2.5). Index motion between two boots is harmless on its own. What costs is (a) the
two peers of one session disagreeing, and (b) a remembered index disagreeing with a rebuilt
table inside one process. So the goal is not "the index must never change" — it is **"the index
must be derived from something both peers and both generations can agree on"**.

---

## 2. Remaining design (save-folder ledger)

Join-time `PrfI` agreement above is landed. The on-disk ledger, replay at the server apply gate,
capacity warnings and `.keytable prune` below are **not**. Do not treat this section as current
behavior.

### 2.1 Order, not absolute index

Persist the **relative order of mod rows**, plus the **vanilla baseline length** measured at the
moment the apply gate opens. Absolute indices are then derived:

```
index(guid_k) = baseLength + position_of(guid_k in the ledger)
```

A game update that adds or removes vanilla items changes `baseLength` and shifts every mod row
uniformly — which is fine, because the save holds guids. What must not change under an ordinary
operator action is *position*, and position comes from the ledger, never from load order.

### 2.2 The ledger

`<saveDirectory>/progenitor/keytable.json`, beside the per-mod `ModSave` stores
(`SavePaths.StoreFolder`, `Persistence/SavePaths.cs:25`). Framework-owned; no mod may write it.

```jsonc
{
  "version": 1,
  "gameIdentity": "vrising/1.1.13.0+r99712+b17/net25",   // same string as the PrfE identity
  "baseLength": 1083,                                     // vanilla rows at gate-open, last boot
  "capacity": 2048,                                       // observed list capacity, last boot
  "rows": [
    { "guid": 480926629,  "id": "sample.ember", "owner": "Progenitor.Sample", "seen": "2026-09-05T22:10:04Z" },
    { "guid": -650563057, "id": "sample.old",   "owner": "Progenitor.Sample", "seen": "2026-09-04T19:02:11Z", "state": "tombstone" }
  ]
}
```

- `rows` is **ordered** and is the whole model. Position in the array is position in the tail.
- A row is never reordered and never deleted by ordinary operation. Removing a mod leaves its
  row **reserved**: the guid is still appended, with no prefab behind it — exactly what a soft
  unapply tombstone already does today, and already verified live (`item-key-table.md` §8).
- `state` is `live` (default) or `tombstone`. Both occupy a row. The distinction is only for
  reporting and for the pruning command.
- `id` and `owner` are diagnostics: the ledger is keyed on `guid`.

### 2.3 Replay

At the server apply gate (`LoadPersistenceSystemV2.DeserializeLoadFile` prefix — the same seam
that already opens `PrefabApplyGate`, `item-key-table.md` §7), **before any `TryApply`**:

1. Read the live `list->Length`. That is this boot's `baseLength`.
2. Read the ledger. Compare `gameIdentity` and `baseLength` (§5.2).
3. Walk `rows` in order. For each: `ItemKeyTable.TryAppend(guid)`.
   - A guid whose mod is present this boot gets its row now; the later `TryApply` finds it
     already present and reuses the index (that path exists — `ItemKeyTable.TryAppend` returns
     `"already present"` and `PrefabApplicator` treats it as success, `keyWasNew = false`).
   - A guid whose mod is absent gets a **reserved** row. Nothing else about it is registered.
4. Assert each append landed at the predicted `baseLength + position`. A single disagreement
   aborts replay and falls back to §5.
5. Record the resulting `index` per guid into the same in-process map `KeyRowStillHolds` reads
   (`PrefabApplicator.AppendedIndex`), so the guard is now comparing against a *derived* number
   rather than a historical accident.

New guids registered this boot that the ledger does not know are appended at the tail in the
order they apply, and **written back to the ledger in that order** at the first flush. From the
next boot on they are ledger-driven.

### 2.4 What the client gets

The client cannot read the server's save folder, and today `PrfD` does not carry the index at
all (`Content/PrefabDefinitionPacket.cs`: four ints + three 64-byte ASCII fields). Both sides
independently append at their own list end and agree only by coincidence of order
(research §3.4). That is chain A and it must be closed by construction.

**One new packet, `PrfI` (`'PrfI'` = `0x50726649`), sent once per join, before the first
`PrfD`.** It is the ledger's tail, in order:

```
int32  baseLength        // the server's vanilla row count at its gate
int32  count             // rows that follow, clamped (see below)
int32  guid[count]       // ordered; state is not sent — a reserved row is just a guid
```

The client, at its own apply gate (first definition packet of the join — unchanged, and `PrfI`
now *is* that first packet), replays the same list against its own table:

- If the client's own `list->Length` at that moment equals `baseLength`, appending the `count`
  guids in order reproduces the server's indices exactly, and the count check
  (`ValidateItemKeyLookupEvent.ItemsCount`) matches for free.
- If it does not, the client logs the delta and appends anyway — its rows land at
  `clientBase + position`, uniformly shifted. **That case is a bug on this build** (identical
  builds must produce identical vanilla tables) and is the one thing worth a loud line rather
  than a kick, because kicking on it would strand every player on a content mismatch we have not
  yet learned to diagnose. Chain C (research §4.C) says measure it before deciding — spike S1.
- A guid the client already has locally (a Both-side plugin that registered it before the join)
  is `already present`; if its local index is not the one `PrfI` predicts, the client **unregisters
  and re-appends in server order** if the table is still mod-only past `baseLength`, and
  otherwise refuses the join-time content and logs. This is the one genuinely new mechanism and
  needs its own spike (S6).

Bounds, per the R50/R52 wire rules already applied to `PrfE`: `count` is clamped to
`capacity - baseLength` and to a hard ceiling (1024), `baseLength` must be positive and less than
the receiver's own capacity, and the whole packet is refused if it would exceed the client's
headroom. A refused `PrfI` means the client appends nothing and applies nothing — the
fail-closed direction, which today's code already produces when the table is full.

**`PrfK` (key placeholder) becomes redundant for new joiners** — a tombstone is just a reserved
row in `PrfI`. Keep `PrfK` for the mid-session case (a mod unregistered while players are
connected), where a single new row has to reach clients that already have a table.

### 2.5 Where the ledger is written

`ModSave` already owns the save folder and its flush (`Persistence/ModSave.cs`,
`SaveAll` at `:89`, folder capture at `:395`). The ledger flushes on the same triggers, plus once
immediately after the gate-open replay, so a crash between boot and the first autosave does not
lose a newly assigned row. It is *not* a `ModSave` store: it is framework state under the same
folder, written by the same code path, with its own file name so no mod guid can collide with it.

The client never writes a ledger. A hosted (bundled) server is a server and writes one, per the
standing rule that server code never branches on dedicated-vs-bundled.

---

## 3. What this fixes, mapped to the failure chains

| Chain (research §4) | Today | With the ledger |
|---|---|---|
| A — peers disagree in one session | prevented only by two independent orders happening to match | server tells the client the order; client reproduces it or refuses loudly |
| B — remembered index vs rebuilt table → refusal → M2.11 drop | fires on any Core swap or plugin reload that reorders | the remembered index is *derived* from the ledger, so a swap recomputes the same number; a real disagreement now means the ledger is wrong, which is a diagnosable state, not an accident |
| C — count divergence | unmeasured | counts are equal by construction whenever `baseLength` agrees |
| headroom | undocumented cliff at 964 rows | an accounted budget with an operator command and a warning threshold |

It does **not** fix, and must not claim to: a mod removed from a save that still contains its
items. Those entities hold a guid that no longer resolves in `PrefabLookupMap`; reserving the key
row does not bring the prefab back, and M2.11's guard is still what handles it. The ledger's
reserved row only keeps *everyone else's* index still.

---

## 4. Migration

The `test1` save has `sample.ember` at 1083 and one tombstone beyond it, with no ledger.

**No migration step.** On the first boot with the feature:

1. Gate opens. `list->Length` is read → `baseLength = 1083`.
2. `keytable.json` is absent → replay is a no-op, and the boot proceeds exactly as today: applies
   in load order, appends at the tail.
3. Each successful append is recorded, in order, into a fresh ledger. Existing tombstones are
   recorded as reserved rows the first time `ApplyKeyPlaceholders` runs
   (`PrefabRegistry.cs:3444-3480`), which already knows their `savedIndex`.
4. Flush.

From boot two the ledger drives. The first boot therefore *adopts* whatever order it happens to
have, which is the correct choice: that order is what the save's currently connected clients and
the last session already agreed on, and adopting it changes nothing observable.

The only visible artifact is a new file in the save folder, which copies with the save (the same
property `SavePaths` was designed for — `Persistence/SavePaths.cs:6-12`).

**Downgrade** (operator removes the feature): the ledger is ignored; behavior is exactly
today's. The file is inert, not load-bearing.

---

## 5. Failure modes

Every one of these must produce a decision, a log line an operator can act on, and a fail-safe
direction. Fail-safe here means: **prefer refusing to register over silently moving a row**,
because a refusal is loud and reversible and a move is neither — with the single exception in
§5.5.

### 5.1 Ledger missing or unreadable

Treated as "first boot" (§4): adopt this boot's order and write a new ledger. Log at warning
if the file existed but failed to parse, and **do not overwrite it** — rename it aside
(`keytable.json.unreadable-<timestamp>`, the same shape as the Core quarantine name) so the
operator can look at it. A corrupt ledger must never be silently replaced by a guessed one.

### 5.2 Ledger disagrees with the live table

Three separable disagreements, three different answers:

| What differs | Meaning | Answer |
|---|---|---|
| `gameIdentity` | the game was updated | expected. Re-derive: keep `rows` order, take the new `baseLength`, rewrite. Log one line naming both identities. |
| `baseLength` only | vanilla row count changed without an identity change (DLC, content settings) | same as above: order is preserved, indices shift uniformly. |
| a replayed append lands at an unexpected index | the table is not what the ledger describes — another mod appended before us, or the gate opened too early | **abort replay**, restore nothing (appends are additive and the rows are ours), fall back to today's behavior for this boot, and do **not** write the ledger back. Loud error. This is the case where a wrong guess is expensive, so we stop guessing. |

### 5.3 Two mods claim one guid

The ledger is keyed on guid and holds one row per guid; a second claimant does not get a second
row. The conflict is resolved where it already is — `PrefabApplicator.TryApply` refuses a guid
that is in `_PrefabGuidToEntityMap` and was not cloned by this process
(`PrefabApplicator.cs:102-107`). The ledger's contribution is that the *loser* does not silently
shift everyone else by appending anyway. Add the owner id to the refusal line so the operator
can see both claimants.

### 5.4 Table full

964 free rows on `test1` today (research §5), and `TryAppend` refuses rather than growing because
no safe native allocator call exists.

Replay must not spend the last rows on reservations for mods nobody has installed. Policy:

1. Compute the budget before replay: `capacity - baseLength`.
2. Replay live rows (mods present this boot) **and** reserved rows in ledger order while the
   budget holds.
3. When the budget is exhausted, refuse the remaining rows **from the tail of the ledger
   backwards** — i.e. the most recently added — never from the middle, so the surviving prefix
   keeps its indices. Log each refusal with its id and owner.
4. Never drop a reserved row to make room for a live one. That would move the live rows behind
   it, which is the exact defect this design exists to remove. If an operator wants the room,
   they take it explicitly (§5.6).
5. Warn at a threshold (say 80 % of the budget) on every boot, with the count, so the cliff is
   seen coming.

### 5.5 The one place a row may move

Pruning, and only pruning, on an explicit operator action (§5.6). Everything else refuses.

### 5.6 Operator surface

Two commands, server-side, admin-gated, no new subsystem:

- `.keytable` / bridge op `keytable` — `baseLength`, capacity, budget used and free, and the
  ordered rows with `id`, `owner`, `state`, derived index, and whether the owning plugin is
  loaded this boot. This is `ItemKeyTable.Describe` grown into a report.
- `.keytable prune <id>` — remove a **reserved** row. Refuses a live row. Says, before doing it,
  exactly how many rows will shift and by how much, and that every client must reconnect. This
  is the deliberate, audited version of the accident the whole design removes.

Pruning a row that a save still has items for is the M2.11 case again: the command must say so
when the guid is still referenced, which it can only know by scanning — so v1 says "this may
still be referenced by saved inventories" unconditionally rather than claiming a scan it did not
do. (The rule from the debug-buff and FMAN13 episodes: a reply reports what was measured, never
what was intended.)

---

## 6. What is game-free and testable

The point of splitting it this way is that everything with a decision in it runs in the unit
suite with no game, no server and no client — the same split `SaveSafetyPolicy` / `SaveSafety`
already uses.

**Game-free (new types, all in the test suite):**

| Type | Holds |
|---|---|
| `Content/KeyLedger.cs` | the ordered rows, guid-keyed; add / find / mark-tombstone / prune; enforces one row per guid and never reorders |
| `Content/KeyLedgerFile.cs` | serialize / parse / round-trip; version handling; the unreadable-file rename name |
| `Content/KeyLedgerPolicy.cs` | `ResolveDisagreement(gameIdentity, baseLength, …)` → one of the §5.2 answers; `PlanReplay(rows, baseLength, capacity)` → the ordered append plan plus the refusal list (§5.4); the 80 % warning; every log string |
| `Content/KeyIndexPlan.cs` | `index = baseLength + position`, and the predicted-vs-actual check |
| `Content/KeyLedgerPacket.cs` | `PrfI` encode/decode, the clamps, and the "refuse the whole packet" decision — pinned exactly like `PrefabWire` / `PrefabEditWireTypes` are |

**Needs the game (kept thin, no decisions in it):**

- reading `list->Length` and `capacity` at gate-open — `ItemKeyTable` already does this;
- the replay loop calling `ItemKeyTable.TryAppend` in the plan's order;
- writing the file where `ModSave` says the save folder is;
- sending / receiving `PrfI` on the existing `CustomEvents` channel.

Test the plan, not the table: `PlanReplay` takes numbers and returns a list, so "removing an
unrelated mod does not move anyone" is a pure assertion over two plans, and "the budget runs out"
is a third. That is the property the roadmap says nothing establishes — it becomes a unit test.

---

## 7. Spikes, in order

Each one is server-only unless it says otherwise, and each has a criterion that can fail.
S1-S3 answer the research note's open questions and **must run before any code is written**,
because S3 in particular can make part of this design unnecessary and S1 can make it more urgent.

| # | Question | Method | Pass criterion |
|---|---|---|---|
| **S1** | Does an inventory slot travel as an index, and does the encoder's width follow `Items.Length`? | Read the native `NetworkSync.WriteItemPrefabGUID` and `ReadItemPrefabGUID`. No live run needed. | The body reads/writes through `ItemKeyLookup` and we can state the bit width. **Fails** if it turns out to write the guid, in which case chains A and C evaporate and this whole design shrinks to §5.4 alone. |
| **S2** | What is `ItemsHashCode` over, and is a mismatch fatal? | Analyze `ValidateItemKeyLookupEvent.CreateEvent` and `ValidateItemKeyLookupSystem_Client.OnUpdate`. | We can say whether the hash is order-sensitive and what the client does on a mismatch. Both are currently guesses (research §2.3). |
| **S3** | Does the save store the guid rather than the index? | Server only, existing `test1`. Note `sample.ember`'s index. Register a second Sample item so the first one's index would shift if order changed, restart, and read the *first* item's stack out of the same chest with `progenitor_get`. | The stack is still Ember Shard. **Fails** if the slot decodes as a different item, which would mean the save does hold indices and the ledger must become absolute, not relative. |
| **S4** | Is the tail stable for the whole session once appended after the gate? | Log `ItemKeyTable.Describe` at gate-open, at first join, and every 5 min for 30 min. | The index never moves. Closes `item-key-table.md` §10's last bullet. |
| **S5** | The roadmap's own experiment: does load order decide the index? | Register two Sample items under two plugin ids (alphabetically first and second). Boot, record both indices. Remove the alphabetically-first plugin, restart, record the second's index. | The second item's row **moves**. If it does not, the `index = list->Length` model is wrong and §2 must be rewritten before anything is built. The same run says whether the `key index mismatch` refusal or the M2.11 drop fires first when a Core swap follows. |
| **S6** | Can a client re-seat a locally-registered Both-side guid into the server's order? | Client sitting. Both-side Sample item registered locally before the join; server sends `PrfI` placing it at a different position. | The client ends with the server's index, and the slot renders correctly. This is the only new mechanism in the design and the only one that can crash a client, so it is last and it is behind a flag. |

S1-S3 are cheap and answer whether the rest is worth building. S5 is the one the roadmap already
specified; it is placed after S3 because if S3 fails the experiment's meaning changes.

**Do not run S1, S2 or the headless native analysis while someone is playing** — it competes for the
machine (standing note in `CLAUDE.md`).

---

## 8. Order of work, once the spikes pass

1. `KeyLedger` + `KeyLedgerFile` + `KeyLedgerPolicy` + `KeyIndexPlan`, tests first, no game.
2. Wire the replay into the server gate. Ledger written, replay driving indices, `PrfI` not yet
   sent. Verify on the test server across three restarts with a mod added and removed between
   them: indices hold.
3. `KeyLedgerPacket` + `PrfI` send/receive, tests first. Client applies from `PrfI`.
   Two-process check.
4. `.keytable` report; the 80 % warning; the budget refusals.
5. `.keytable prune`, last, because it is the only destructive path.

Items 1 and 2 are worth landing on their own even if 3 slips: they close chain B, which is the
one that loses data.
