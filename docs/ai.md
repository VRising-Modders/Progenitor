# Boss AI (`ctx.Ai`)

`ctx.Ai` reads and changes a V Rising boss's AI on the server: its cast options, the health gates in
their conditions, its phase script, a private copy of its behavior tree, and its faction. Every
mutation returns an `IDisposable` that restores exactly what it changed, and the context tracks it,
so every unload reason restores it too. Client calls throw.

**Status: landed; partly verified live.** The mechanisms were proven by hand on Frostmaw
(`CHAR_Wendigo_VBlood`) on 2026-09-24. Through
this API (Core b366e2da, sample e2621273, isolated server, 2026-09-24): six Frostmaws spawned with
`UnitSpawnOptions.Faction` were all `Team` 2/0 with no friendly damage, and the per-unit Sentinel
tree variant cast 0 times in combat while vanilla bosses cast 64. Not yet verified: the per-instance
cast-option copy, the inert marker and re-apply after a load, the Core tree repair, and every change
made after that build. The rest of the live acceptance is [below](#acceptance-procedure--not-run). The
worked example is [`samples/Progenitor.BossVariantsExample`](../samples/Progenitor.BossVariantsExample/README.md).

## How a boss's AI is built

| Layer | Where it lives | Shared? | `ctx.Ai` |
|---|---|---|---|
| Behavior tree | `BehaviourTreeBinding` on the unit → tree prefab → `BehaviourTree.Blob`; per unit `BehaviourTreeInstance { TreeEntity, ExpectedTreeEntity }` | Blob memory is **deduplicated across bosses** (Frostmaw's tree blob is Bandit Stalker's) | `Trees`: private copy per variant, applied per unit |
| Cast options | `CastOptionsUser` → CO prefab → states → groups → `CastOptionBuffer` rows (0x78 bytes each) | Per boss type; rows are re-read on every evaluation | `CastOptions.Edit` (prefab-wide); `CastOptions.CreateVariant` (per instance, experimental) |
| Row conditions | `ConditionBlob` per row (elements 0x28 bytes) | Blobs may be shared | `Conditions.SetHealthGate`: private copy, row repointed |
| Phase script | `Script_ApplyBuffUnderHealthThreshold_DataServer` on the unit | Per instance | `Phases.SetThreshold` |
| Team / faction | `Team { Value, FactionIndex }`; faction prefab `FactionBlobAsset` masks | Faction prefabs are shared | `Factions`, `ctx.Units` spawn options |

The tree is the generic V Blood brain (movement, relocation, return, idle). Everything boss-specific
is in the cast options: groups are tried in priority order, and inside a group a row is picked by
integer weight among the rows that pass their checks. A row casts the unit's ability **slot**, so the
API selects rows by ability group (`FrostNova`, the full `AB_Wendigo_FrostNova_AbilityGroup`, or its
GUID), never by row index — Frostmaw's icicle alone is in three rows.

## Safety rules

- **Never repoint a shared tree prefab's blob while units run it.** Changing a node's type under a
  unit that is executing that node aborts the whole server through Burst
  (`TriedToTickNonTickableNode`, incident INC-20260924-01). `ctx.Ai` has no API that does this. A
  tree variant runs on a private tree entity; `Trees.Apply` writes only the unit's
  `ExpectedTreeEntity`, and the tree job then aborts the running nodes against the **old** blob and
  restarts on the copy. That swap was verified mid-fight.
- **Tree variants and per-instance cast options are experimental** (`experimental: true`,
  `VariantSpec.AllowExperimental`): the private tree reference is not saved (see
  [Persistence](#persistence)): a unit saved on a variant reloads with a null tree and aborts the
  server when its tree runs (INC-20260924-02) unless Core's post-load repair fixes it first.
  Revert or remove variant units before any save.
- **A tree variant is frozen by its first `Apply`.** Units may be executing its bytes, so every edit
  method throws afterwards. Create a new variant instead.
- **Apply a tree variant after the spawn tick.** `BehaviourTreeBindingSystem_Spawn` resolves the
  binding after spawn and would overwrite the swap; `Apply` refuses a unit that has no running tree
  yet. The sample waits 1.5 s.
- **Private blobs are never freed.** A scheduled job may still read one. Identical content is
  allocated once per Core generation and shared, so the leak is one copy per distinct edit, not
  per call (a Frostmaw tree copy is 17 KB, a condition copy about 1 KB).
- **Cast-option edits and health gates are prefab-wide.** They change the boss type's CO prefab: every
  living and future unit of that type reads the new values on its next evaluation. Use a variant for
  per-unit behavior.
- **The row cooldown and the ability's own cooldown both apply; the longer wins.** Frost nova's own
  floor is about 9.5 s, so a 2 s row cooldown gives a 9.5 s cadence (live T3).
- Every write goes through the existing size guards at offsets declared in
  [`layout.txt`](../src/Progenitor.Core/Layout/layout.txt) and resolved against the server metadata.

## Defaults a spawned boss brings with it

| Default | What it does | What to do |
|---|---|---|
| `Team.Value` 2, `FactionIndex` from the prefab (Frostmaw 31, `Faction_Wendigo`) | For unit teams, `IsAllies(a, b)` is `(Friendly[a.faction] & Membership[b.faction]) > 0`. Faction_Wendigo has `Friendly` 0 and membership `Ignored`, so two Frostmaws are **not** allies and their `NOT IsAlly(Target)` attacks hit each other. | Pass a self-friendly faction when you spawn several: `new UnitSpawnOptions { Faction = "Faction_Bandits" }` (100 Frostmaws, 0 friendly damage, still hostile to players). Progenitor never changes it silently. `ctx.Ai.Factions.Describe(name).SelfFriendly` and `AreAllies(a, b)` report it. |
| Damaging hits with no team check (Frostmaw Ice Beam; Bishop of Dunley’s beam, circles and projectiles) | Their hit condition never tests the team, so in vanilla they damage allied bosses too (live 2026-09-24: Ice Beam cost every other Frostmaw in a group about one 98.4-health tick per beam; 4 of 5 vanilla Bishops lost health to each other in 120 s). **Core now guards these by default on the server, for units spawned through the API only:** when the caster wears Core's API-spawn marker (every `ctx.Units.Spawn` unit does), such a hit skips allies and still hits enemies. Vanilla units and bosses are untouched: their hits run the original condition. | Nothing for units you spawn through `ctx.Units`. Exempt a prefab with `ctx.Ai.AllySafety.Exempt(prefab)` or turn the default off with `[Ai] AllySafeHits = false`. See [Ally-safe damaging hits](#ally-safe-damaging-hits). |
| `Immortal` on the prefab | A spawned V Blood is downed at 0 HP instead of dying. | `Describe(...).Immortal`. |
| Where `Team.FactionIndex` comes from | The game derives it from the unit's `FactionReference` (a `ModifiablePrefabGUID`) at spawn. Live: editing the prefab's `Team.FactionIndex` did nothing for new spawns (born 31); setting the prefab's `FactionReference` to Faction_Bandits made them born with 0. A post-spawn `Team` write held for minutes. | Every `ctx.Ai` / `ctx.Units` faction write sets both fields per unit and never edits the shared prefab. |
| Removing a unit or a drop | `DestroyUtility.Destroy` plus an immediate `DestroyTag` removes it on the server only: the client keeps it (live: a loot drop stayed visible, 397 of 400 removed Frostmaws stayed as disabled entities). `Destroy` alone let the game finish it on both sides within 2 s. | Arena and cleanup code must use the replicated path: `IUnitHandle.Despawn` / `ctx.Units`, the Dev `entity` finalize, or `BuffSweep.DestroyUnitReplicated`. Never add `DestroyTag` by hand. |
| Downed buff (Frostmaw `Buff_Wendigo_VBlood_Downed`, `LifeTime` 125 s) | Applied by the phase script at its threshold. When it expires the boss is destroyed; healing does not clear it. | Raising the phase threshold defeats the boss earlier. |
| Spawn buff (`Buff_General_Spawn_VBlood_MidGame`) | Disables aggro for a short time after spawn. | Engage after it ends, or let the boss aggro naturally. |
| Vicinity | A boss engaged while its target is outside roughly 10 m, or outside its combat area, may stay idle. | Spawn within a few meters of the target. |

## Ally-safe damaging hits

**Status: verified live on 2026-09-24** (isolated server, 120 s fights against one protected
player). Five Bishops of Dunley spawned outside the API still damaged each other as in vanilla
(four of five lost 100 to 1,100 health). Five spawned through `ctx.Units` carried the marker and
lost none. The boss variants sample’s Frostmaws cast Ice Beam 10 times with no health lost. Not
verified: whether `MinionMaster` covers a summon’s own attacks, the marker after a save and
restart, and restore on a Core hot swap.

**Scope: units spawned through the API only.** The guard applies only when the hit's caster wears
Core's API-spawn marker, an inert buff that every `ctx.Units.Spawn` and static `Units.Spawn` unit
gets ([modules.md](modules.md#units)). If the game resolves `MinionMaster` as expected, the summons of
such units are covered too. Vanilla units, bosses and players are untouched: their hits run the
original condition, element for element.

**Detection.** Core looks at prefabs that have both a `CreateGameplayEventsOnHit` buffer and
`DealDamageOnGameplayEvent`. Each hit row (stride 0x20) holds a `GameplayEventId` at 0x0
(`{ GameplayEventType, EventId }`) and a `ConditionBlob` pointer at 0x10. That condition gates the
whole hit event: damage, hit buffs such as Freeze, and knockback.

Only **damaging** slots are considered. A slot is damaging when a `GameplayEventListeners` row
(stride 0x20) on the same prefab has `GameplayEventType` `DealDamage` (23) and a `GameplayEventId`
equal to the slot's `EventId` in both fields. Only direct DealDamage listeners count. A hit whose
event reaches damage through a Branch, Delay or Iterate event is not followed, and it stays vanilla.
Every other slot, such as one that buffs allies or hits destructibles, is never touched.

A damaging slot is **team-aware** when any element of its condition has source `IsAlly` (8),
`IsEnemy` (41), `IsInUnitTeam` (66) or `IsInNeutralTeam` (89). Otherwise it is **team-blind** and hits
allies. A prefab is guarded when at least one damaging slot lacks a team source. The collider filter
(`HitColliderCast.PrimaryFilterFlags`, physics layers) never filters by team.

The first Core build guarded every slot without a team source on these prefabs, damaging or not.
On the isolated server it reported 1023 fully team-aware prefabs and guarded 391 prefabs / 497 slots,
with 139 exempt. That was too broad. The read-only scan of 2026-09-24 counted a prefab as team-aware
when any slot had a team source: 1516 team-aware, 35 team-blind and 2 with a null condition on some
slot.

**The fix.** On the server, Core gives every damaging team-blind slot a private copy of its condition
with three elements prepended. The walk starts at element 0; each element moves to its success or
failure index; an index at or past the count ends the walk, and the result is the last check.

| Index | Element | Success | Failure |
|---|---|---|---|
| 0 | `HasBuff(Owner, marker)`: source 7, target 0 | 2 | 1 |
| 1 | `HasBuff(MinionMaster, marker)`: source 7, target 5 | 2 | 3 |
| 2 | `NOT IsAlly(Target)`: source 8, target 1, invert 1 (Frostmaw melee's element) | 3 | new count (false) |
| 3… | the original elements, indices shifted by 3; indices at or past the old count become the new count | | |

A marked caster's hit on an ally ends false at element 2. A marked caster's hit on anyone else
continues into the original chain. An unmarked caster fails elements 0 and 1 and continues into the
original chain at element 3, which then runs exactly as before. A slot with no condition (or an empty
one) gets the three elements plus an always-true tail at index 3: `HasBuff(Target, 0)` inverted, with
both branches terminal. Without the tail, an unmarked caster's walk would end on the failed element 1
and the hit would be blocked. The shared original blob is never written; only the prefab slot's
pointer changes. Copies are never freed: one per distinct original per Core
generation, about 1 KB each. Ability entities already alive keep the condition they were spawned
with. By hand, live: Ice Beam dealt 0 friendly damage over 5 minutes and still froze the player 12 of
12 times. Bishops took 0 friendly damage over 2 minutes; without the fix, 4 of 5 were damaged.

**When it runs.** On the first frame after every world load; 30 s after the server world appears (a
new world loads no save); when Core installs into a running world; 1 s after a content prefab is
registered; and when the config switch changes. Each pass logs one summary line:

```text
Ai: ally-safe hits (world load) on: N prefab(s) with hit and damage buffers, N damaging hit slot(s) (N team-aware; N non-damaging slot(s) ignored), N team-aware prefab(s), N without a damaging slot; guarded N prefab(s) / N slot(s) (N null-condition, N new this pass); exempt N (built-in N, plugin N, config N); restored N slot(s); N failure(s)
```

Every pass is idempotent. A copy ends with a trailer (format tag and original pointer), so a later
pass or a later Core generation recognizes a guarded slot and never guards it twice. Copies from the
first, unconditional build (trailer `PGNALLY1`) are still recognized: a pass replaces them with the
scoped form built from their original, or restores them. A Core swap or unload
points every guarded slot back at its original. A pass also restores any guarded slot that is not
damaging, for example one guarded by the first, broader build.

**Left vanilla by default** (built-in, case-sensitive name rules):

| Rule | Why |
|---|---|
| `EH_*`, `Buff_BearTrap_*`, `Buff_Ocean_Blowfish_*`, `AB_Mech_*` | Environmental hazards: traps, barrels and critters are meant to hurt anything nearby. |
| Names containing `PlayerAbility` | Player consumable abilities (`AB_Lucie_PlayerAbility_*`). |
| `AB_<School>_*` and `<School>_Vampire_*` for Blood, Chaos, Frost, Illusion, Storm, Unholy | Player spells and their passives (`AB_Storm_BallLightning_Projectile`, `Storm_Vampire_Static_*`). Every such prefab in the scan is a player spell or a player summon. |
| `AB_Vampire_<Weapon>_*` and `AB_<Weapon>_*` for Axe, Claws, Crossbow, Daggers, DualHammers, GreatSword, Longbow, Mace, Pistols, Pollaxe, Reaper, Slashers, Spear, Sword, TwinBlades, Unarmed, Whip | Player weapon attacks and skills (`AB_Spear_AThousandSpears_*`, `AB_Pistols_ExplosiveShot_*`). `AB_Vampire_Dracula_*` and the other `AB_Vampire_` bosses are not weapons and stay guarded. |
| `AB_Vampire_Coating_*`, `AB_Vampire_VeilOf*`, `AB_Shapeshift_*` | Player weapon coatings, Veil spells and shapeshift forms. |
| Prefabs that also carry `HealOnGameplayEvent` | The guard would also stop the heal on allies. |

The player rules keep vanilla player behavior unchanged. They come from the name prefixes in the
server scan, not from ability ownership. In that scan they exempt 12 of the 37 flagged prefabs: the
barrel, energy-beam, blowfish, bear-trap and mech hazards, the two Lucie potions, Ball Lightning,
and the Ball Lightning stab of A Thousand Spears. The other 25 are NPC and boss attacks, including
`AB_HighLordSword_SelfStun_Projectile` and `AB_Vampire_Dracula_WolfAttack_Hit`, and Core guards them.
`AB_ChurchOfLight_Priest_HealBomb_Buff` has a null condition. Core guards it unless it carries
`HealOnGameplayEvent`, which has not been checked.

**Opting out.**

- Config: `[Ai] AllySafeHits` in `BepInEx/config/progenitor.cfg`, default `true`. When it is
  `false`, Core restores every slot except those a plugin protects.
- API: `ctx.Ai.AllySafety`. It is server only and main thread only, and the context tracks every hold.

| Member | What it does |
|---|---|
| `Exempt(prefab)` → `IDisposable` | Leaves one damaging hit prefab vanilla while held. Its guarded slots go back to their original conditions now. On dispose they are guarded again if the default still wants them. Exempt wins over Protect. |
| `Protect(prefab)` → `IDisposable` | Guards a prefab the default skips (a built-in exemption, or the switch off) while held, with the same caster-scoped guard: it still covers only API-spawned casters. A prefab whose hits all test the team needs nothing. |
| `IsProtected(prefab)` | True when none of the prefab's damaging hit slots can let an API-spawned caster hit an ally for lack of a team check. |
| `Describe()` → `AllySafetyReport` | Runs a read-only pass now. Returns damaging, team-aware, guarded, exempt and failed counts, and an `AllySafetyEntry` (`AllySafetyState`, reason, slot counts) for every prefab that is not simply team-aware. |
| `Enabled` | The config switch. |

`Exempt` and `Protect` throw when the prefab is not a damaging hit prefab.

```csharp
// A plugin that wants the vanilla friendly fire of one boss attack:
IDisposable keepVanilla = ctx.Ai.AllySafety.Exempt("AB_Militia_BishopOfDunley_HolyBeam_Channel");
ctx.Log.LogInfo(ctx.Ai.AllySafety.Describe().Summary());
```

**Out of scope.** Ring attacks (`RingAoE_DataShared.HitFilter`) already filter by team; only
Morgana's `KillWorshippersAoe` includes allies, on purpose. Script-driven damage and damage from buffs
applied by other events are not covered. A team source in a condition does not prove that it gates
the damage. Only Ice Beam and the Bishop were tested live.

**Still to check live.** None of the following has been observed with this Core code:

- that a hit condition's `Owner` is the caster (the unit that cast the ability), so element 0 sees the marker;
- that `MinionMaster` reaches a summon's master, so summons of API-spawned units are guarded;
- that the API-spawn marker lands on the spawn tick (or at the 1 s / 5 s re-checks), stays inert, shows nothing on Progenitor clients once the client strip ([modules.md](modules.md#core-inert-clones-on-the-client)) is deployed, and survives a save and restart on a spawned unit;
- that vanilla bosses of a guarded type still damage each other exactly as before (for example two vanilla Frostmaws with Ice Beam), and that API-spawned ones do not;
- the always-true tail: that `HasBuff(Target, 0)` inverted is true for every target, including targets without buffs;

- the summary counts on the isolated server. Expect far fewer than the first build's 391 guarded prefabs.
  The floor is the 25 non-exempt prefabs of the 35 + 2 flagged by the scan, if their team-blind slots are
  damaging (the Ice Beam channel's is). The scan kept no per-slot data, so the number of team-aware-looking
  prefabs that also have a damaging team-blind slot cannot be derived from it;
- that no ally-targeting effect (ally buffs, destructible hits) changed;
- no friendly damage from Ice Beam or the Bishop with the default alone;
- restore on `.corereload` and re-guarding by the next generation;
- the null-condition builder's fresh header on `AB_ChurchOfLight_Priest_HealBomb_Buff`;
- whether the client, which keeps vanilla conditions, predicts any hit effect on an ally.

## API

| Member | What it does |
|---|---|
| `ctx.Ai.Describe(bossPrefab)` → `AiProfile` | Tree name, node count and node types; ability slots; every cast-option row decoded (weight, range, exact cooldown, aggro, angle, line of sight, delay, combo, health gate, condition summary); phase script; team, faction masks, `Immortal`. `Describe()` formats it. |
| `ctx.Ai.Observe(unit)` → `AiObservation` | Behavior state, aggro target, current cast, health, team, whether the tree is a variant, cast options, variant name, phase script and recent casts (newest first). |
| `CastOptions.Read(boss)` / `Edit(boss, abilityGroup, CastOptionEdit, priorityGroup?)` | Read rows; change `Weight`, `CooldownSeconds` (exact), `RangeMin`, `RangeMax`, `LineOfSight`, `AggroFilter`, `Angle` on every row of that ability group. Prefab-wide. Dispose restores each field unless something changed it again since. |
| `Conditions.SetHealthGate(boss, abilityGroup, fraction, priorityGroup?)` | Rows whose condition compares the boss's own `HealthFactor` are pointed at a private copy whose gate reads `fraction` (1 = from full health). Refused when no selected row has such a gate. Dispose points the rows back. |
| `Phases.Get(unit)` / `SetThreshold(unit, fraction, buff?)` | Per instance. Re-arms when the unit is above the new threshold. Dispose restores threshold and buff; a buff that already fired stays fired. Only `Script_ApplyBuffUnderHealthThreshold` is supported; `Describe` lists the `…MonsterVBlood` and `…ThreeHealthThreshholds` scripts as unsupported. |
| `Trees.CreateVariant(boss)` → `TreeVariant` | Editable copy: `DisableCombat()` (every `AttackEntityCastOption` → `Succeed`; the boss chases but never casts, live T5B), `SetDelay(node, min, max)`, `SetParameter(node, TreeParameter, value)` for `Delay`, `HealthChangePercentage`, `SetCombatAreaRelocationDistance` and `Int_Set` literals (node type checked first), `FindNodes(typeName)`, `DescribeNode(node)`. |
| `Trees.Apply(unit, variant, experimental: true)` / `Revert(unit)` | Swap one unit onto the copy, or back to its prefab's tree. Refused for a unit bound to another tree or not yet running one. Experimental: the private tree is not saved. |
| `CastOptions.CreateVariant(boss, name, experimental: true)` → `CastOptionsVariant` / `Assign(unit, variant)` | **Experimental.** See [below](#per-instance-cast-options-experimental). |
| `Factions.Describe(faction)` / `Of(unit)` / `AreAllies(a, b)` / `Set(unit, faction)` | Faction index and masks; the game's `Team.IsAllies` (not symmetric; null when a mask is unknown); per-unit write of **both** `FactionReference` (the faction prefab GUID) and `Team.FactionIndex` (the blob's `Index`), each restored on dispose. |
| `ctx.Units.Spawn(prefab, position, UnitSpawnOptions)` / `ctx.Units.SetFaction(handle, faction)` | The same two-field faction write on the spawn tick, checked again at 1 s and 5 s. Not restored on unload: the unit is despawned then. |
| `AllySafety.Exempt(prefab)` / `Protect(prefab)` / `IsProtected(prefab)` / `Describe()` | Core’s ally-safe hit default: exempt or protect one damaging hit prefab, or read the state. See [Ally-safe damaging hits](#ally-safe-damaging-hits). |
| `Variants.Define(name, VariantSpec)` / `Apply(unit, name)` → `AiVariantHandle` / `Remove(unit)` / `VariantOf(unit)` / `UnitsOf(name)` / `Reapply()` | Named per-unit variants; `AiVariantHandle.MarkerApplied` / `MarkerProblem` report the persistence marker. See [persistence](#persistence). |

```csharp
ctx.Ai.Variants.Define("Sentinel", new VariantSpec("CHAR_Wendigo_VBlood")
{
    AllowExperimental = true,
    Tree = tree => tree.DisableCombat(),
    Faction = "Faction_Bandits",
});
// later, a moment after spawning:
AiVariantHandle sentinel = ctx.Ai.Variants.Apply(unitEntity, "Sentinel");
if (sentinel.MarkerProblem != null) ctx.Log.LogWarning(sentinel.MarkerProblem);
```

## Capacity

Verified live 2026-09-24 on the isolated server:
Frostmaws in Dracula's arena, all engaged on one player, born on `Faction_Bandits`. "Unique" gave
every boss its own private tree copy (about 17 KB each) through `ExpectedTreeEntity`; the control
used the shared vanilla tree.

| Bosses | Server fps, unique trees | Server fps, shared trees |
|---:|---:|---:|
| 100 | 30 | 30 |
| 200 | 28.8 | 27.2 |
| 300 | 19 | 19 |
| 400 | 9.8 | 11.9 |
| 500 | 7.6 | not run |

About 200 fighting bosses keep the server at its 30 fps simulation rate; about 300 is degraded; beyond
that it collapses. The limit is the number of **fighting** bosses, not tree uniqueness: private tree
copies added no measurable cost, and server memory went from 1.29 to 1.39 GB between 10 and 500
bosses. Tree swaps completed for every boss up to 400.

Scope: one boss type, one player, one arena. Several players, bosses spread over the map, or other
boss types are untested.

`AiLimits.RecommendedActiveUnits` (200) records this. `ctx.Units` logs **one** warning per plugin
generation the first time that plugin's live spawned units (spawned by it, not yet despawned) exceed
it. It never refuses or throws.

## Per-instance cast options (experimental)

`CastOptions.CreateVariant` copies the boss's CO prefab chain under a GUID derived from the plugin id
and variant name (`<pluginGuid>.ai.co-<name>`):

- **Linked group (Frostmaw, live).** When the CO root carries a `LinkedEntityGroup`, one
  `Instantiate` of the root copies the whole group and remaps references inside it. Core then reads
  the copy's state and group buffers and refuses (destroying the copies) if any holder or option
  reference still names a source entity or an entity outside the copied group, and logs what it
  found. Without a group, the root, each holder and each option are instantiated separately and the
  references rewritten.
- Every copy is tagged `Prefab`, so gameplay queries skip it.
- The root is registered with the same three `PrefabCollectionSystem` rows the content registry
  writes for item and buff clones (`PrefabApplicator.AddCloneLookups`): entity map, asset data, and
  conversion state `Converted`. `EvaluateCastOptions` resolves `CastOptionsUser` through
  `PrefabLookupMap.TryGetConvertedPrefab`, which checks the conversion state.
- A second `CreateVariant` with the same name in the same world re-copies the rows from the source.
  After a Core hot swap (which empties Core's own table but not the prefab maps) the mapped root is
  **adopted** when it is Prefab-tagged, carries the derived GUID and its chain pairs with the source
  chain; otherwise the call refuses.
- The copies are never destroyed or unmapped. `Assign` writes the unit's `CastOptionsUser`.

Unverified, and why it needs `experimental: true`:

- that the cast-option evaluator uses a copy registered this way (first live attempt was refused
  before registration by the old `LinkedEntityGroup` rule);
- what a unit saved with a clone GUID in `CastOptionsUser` does after a restart before the plugin
  re-registers the GUID, or with the plugin removed.

## Modifiable fields

`ExpectedTreeEntity`, `CastOptionsUser.CastOptionsPrefab` and `FactionReference.FactionGuid` are
`Modifiable*` fields, and ctx.Ai writes their current value directly, bypassing the game's
modification stack. A buff that modifies the same field (for example one with
`Script_Buff_ModifyCastOptions`) can override the write while it is active, and its removal can put
back a value that is not ctx.Ai's. Every ctx.Ai undo restores a field **only while it still holds the
value ctx.Ai wrote**, so an undo never overwrites a buff's (or another plugin's) later value; the
field is then left as it is and, for cast-option rows, a warning is logged.

## Persistence

**Rule (mandatory): revert or remove tree-variant units before any save, autosaves included.** The
sample's `.bossvariants clear` does that. ctx.Ai does not do it for you: Core has no plugin-facing
hook that runs before serialization *and* after it. The only save seam Core offers plugins,
`TriggerPersistenceSaveSystem.TriggerSave`, is a postfix on the request, which only queues the save;
the serialization guard that exists (`SerializePersistenceSystemV2.TriggerSave`) is a Bootstrap
prefix private to player forms. Swapping units to the prefab tree before a save and back after it
would need a new Bootstrap slot (a restart) and live proof that it covers autosaves; until then tree
variants stay experimental. Core's tree repair (below) is the safety net, not the plan.

Observed live 2026-09-24 (isolated server, save and restart with two Sentinel units on a private tree
entity): `BehaviourTreeInstance` is saved, but the reference to the private tree entity (which is not
saved) comes back as `Entity.Null` in **both** `TreeEntity` and `ExpectedTreeEntity`. The server
booted, then **aborted** inside Burst as soon as a player came near and the unit's tree job read the
null tree ("The entity does not exist", incident INC-20260924-02). Writing both fields to the
binding's prefab tree before any player connected was the proven repair. Those units also carried
no marker (the marker was not inert on that build).

What Core and ctx.Ai now do about it:

1. **Core tree repair** (`TreeRepair`, every unit, not only ctx.Ai's). On the first frame after
   every world load (before a player can be near) and again at 2, 10 and 30 s, units with a `BehaviourTreeBinding` whose `TreeEntity` or
   `ExpectedTreeEntity` is null or missing are pointed back at the binding's prefab tree, and each
   repair is logged. A missing `ExpectedTreeEntity` alone gets only the expected tree (the tree job's
   own abort path rebinds from the valid running tree). A missing `TreeEntity` gets **both**
   references — the proven repair; writing only the expected tree would make the job's
   abort-on-mismatch path read the null tree — the saved running-node list is then re-expanded against the prefab
   tree, so the repair refuses when a running node index lies outside it and logs an error: such a
   unit can still abort the server and must be removed. The same pass resets a unit's
   `CastOptionsUser` to its prefab's cast options when the unit's GUID no longer resolves (a
   ctx.Ai cast-option copy whose plugin is gone or whose re-apply was refused); a GUID that resolves
   is never touched. Each reset is logged. A ctx.Ai tree
   variant changes node types only to `Succeed`, which never stays running, so its saved running
   list is always valid against the prefab tree. For another mod's private tree that is not
   guaranteed; the index check is the only guard.
2. **Variant re-apply from the marker.** At 3, 12 and 32 s after `WorldLoaded` (one set per world;
   plus one set at `Define` when the world is already up) the
   plugin scans units with a `BuffBuffer` and a behavior tree and re-applies the variant to every
   unit that carries its marker, is the variant's boss prefab and is not yet recorded in this world
   epoch. A unit whose tree is still null is refused on that pass and retried on the next. The undo of
   a re-applied variant restores the **prefab's** cast options, phase threshold, buff and faction —
   the unit's current values are already the variant's.

**The marker.** `Variants.Define` registers a `DefineBuff` clone per variant (`<pluginGuid>.ai.<name>`,
cloned from `VariantSpec.MarkerSource`). Core itself strips the **server** clone to an inert tag:
`LifeTime` -1 / `None`, `Buff_Persists_Through_Death`, and no spawn events, movement speed or
sequences. It does not use `Content.Edit`, so the plugin needs no `[NetworkCompatibility]` (live: a
plugin without it had its `Content.Edit` refused, the clone kept Haste's short `LifeTime` and speed,
and the marker expired off the units). Only sources Core knows how to strip are accepted
(`Buff_General_Haste` today): `Define` refuses any other, and refuses when the clone already exists
and cannot be verified inert; `Apply` refuses (changing nothing) while the clone is not registered or
not verified. A Progenitor client builds its own copy of the clone from the definition packet (from
its own `Buff_General_Haste`), and Core strips that copy too, the frame it is built: the same list,
plus the buff-bar entry (`ShowOnBuffBar`, `ShowOnBuffBarData`). A client strips a variant marker
only because the server announced its GUID (Core event `'PrIc'`), never from the id's shape. See
[modules.md](modules.md#core-inert-clones-on-the-client) for what each side strips and what is
live-verified.

`Apply` puts the marker on the unit through `DebugEventsSystem.ApplyBuff`, which spawns it on
`FromCharacter.Character` synchronously (native `DebugEventsSystem.ApplyBuff`:
`BuffUtility.TrySpawnBuff` then playback). The returned `AiVariantHandle` reports
`MarkerApplied`; when the buff is not in the unit's `BuffBuffer` immediately or a second later,
`MarkerProblem` says why and a warning is logged.

| Ending | Unit | Marker |
|---|---|---|
| `Dispose()` of the `AiVariantHandle`, or `Variants.Remove(unit)` | reverted | removed |
| Plugin unload (any reason) | reverted | **kept**, so the next generation re-applies it |
| Server restart | as saved, then Core tree repair, then re-apply from the marker | as saved |

**Still to prove live:** that the inert marker survives a save and restart on an NPC; that the rescan
re-applies restored units after the tree repair; and what a saved clone GUID in `CastOptionsUser`
does before the plugin re-registers it. The research recorded that spawned bosses do persist in
autosaves (finding 7).

## Bounded leaks

By design nothing below is freed, because a job or a saved reference may still point at it:

- private blobs (tree and condition copies): one per distinct content per Core generation (17 KB
  per Frostmaw tree copy, about 1 KB per condition copy);
- guarded hit-condition copies (ally-safe hits): one per distinct original condition per Core
  generation, made again after each Core swap (one per guarded damaging slot's distinct original, about 1 KB each);
- tree-variant entities: one per `TreeVariant` per plugin generation per world (Prefab-tagged,
  inert);
- cast-option copies: one chain per variant name per world, reused and re-synced across plugin
  reloads and adopted across Core swaps;
- the marker buff clone: one per variant, kept by the content registry like any buff clone.

## Acceptance procedure — not run

Isolated test server only, with a connected test character standing in open ground. Build the
sample (`dotnet build samples/Progenitor.BossVariantsExample/Progenitor.BossVariantsExample.csproj
-c Release`) and deploy it to the server with Core. Then, following the
[sample README](../samples/Progenitor.BossVariantsExample/README.md):

1. `.bossvariants spawn`, wait 3 s, `.bossvariants status`: expect six lines, two `vanilla`, two
   `Frenzied`, two `Sentinel`, all `team=2/0`, Sentinels `tree=variant`.
2. Fight for two minutes at full health. Expect Frenzied bosses to cast Snow Storm and Ice Beam and no
   melee, frost nova about every 9.5 s; vanilla bosses only melee, icicle and nova; Sentinels to
   follow without casting; no boss damaged by another.
3. Damage a Frenzied boss to 25 %: expect it downed. Damage a vanilla boss to 25 %: expect nothing.
4. Unload the plugin: expect every example boss despawned and the CO prefab rows and tree prefab of
   `CHAR_Wendigo_VBlood` byte-identical to a pre-test capture.
5. Persistence: spawn again, save, restart the server with the plugin, `.bossvariants status` after
   30 s. Record whether the variants came back.

Record captures, logs and the exact build externally. Nothing above has been observed yet.
