# Edit results on the wire (design, 2026-09-05)

> **Status: landed 2026-09-05 (jobs FEDW1–FEDW6).** This note was written in the future tense and the
> body below still reads that way; the mechanism it designs is shipped. `PrfE` exists, the server
> ships every touched component after an edit pass, and the client applies the layer last.
> **Verified server-only** on the test server: the byte round trip through the readers and the
> resolved-by-name writer with every read-back matching (evidence 189), the packet's compatibility
> field corrected to the shared game identity and read live (evidence 190, job FEDW2), the same round
> trip under FEDW3's shape rule (evidence 191), and again under FEDW5's per-field value policies
> (evidence 194). The receiver is an **allowlist with preservation** over an explicit list of
> component types Core vouches for, plus a per-field **value policy** on every admitted field, and
> every rule fails closed (FEDW4, FEDW5, FEDW6). **Owed: the two-process check on a real client** —
> nothing here has been exercised across a client and a server at once. **Phase 2 (relaxing the
> `ServerMustHave` requirement for wire-safe edits, which is what makes a server-only balance mod
> possible) is blocked on that check;** the requirement stays until it passes.

Roadmap M2.4 has carried one open item since the ordered edit passes landed: **edited component
values are not on the wire.** `Content.Edit` runs the same editors on both sides, so a client without
the plugin keeps unedited stats, a client whose editor reads a different captured input (a synced
config entry that has not arrived yet) converges only after the plugin calls `InvalidateEdits`, and a
server-only balance change to a vanilla prefab is simply not possible. The current design papers over
that with a rule (a plugin that edits must be `ServerMustHave`) and a documented ritual (invalidate on
`SettingChanged`). This note replaces both with the obvious thing: **the server is authoritative for
edited component values, and it ships the results.**

FEDIT1 made this cheap. The handle already knows, per prefab, exactly which component types the pass
wrote and holds, per component, a closure built at the write site where `T` was known. One more closure
of the same shape - a reader - is the whole serializer. (`PrefabEditHandle.Touched` holds *short* names
(`"ItemData"`), which is what the baselines store is keyed by; the wire needs the full name, so the
readers are keyed by `typeof(T).FullName` and the packet carries that - R50 finding 10.)

## What travels

After the server's edit pass on a prefab produces `anyTouches`, the registry serializes every touched
component of that entity into a **`PrfE`** packet (event id `0x50726645`, next free in the `Prf*`
range after `PrfD` `0x50726644`; the complete list of `Prf*`/`Prh*`/`Prc*` ids is
`src/Progenitor.Bootstrap/ReceiveSlotPolicy.cs`, not `PrefabDefinitionPacket.cs`, which declares only
`PrfD`):

```
PrfE
  int32  guidHash
  int32  gameIdentityHash        // FNV-1a of PrefabEditBuildIdentity.Compose(...) - see below
  int32  editIdentityHash        // PrefabEditTable.Stamp(guid) hashed; lets the client dedupe
  uint8  count                   // 0 = withdraw this guid's layer; else 1..16
  count x
    ascii  typeFullName          // 1..127 bytes, e.g. "ProjectM.ItemData"
    uint16 length                // == Marshal.SizeOf(type) on both sides, <= 1024
    bytes  payload
```

### The game identity (FEDW2, R50 finding 10)

The header's compatibility field is **not** `AddressLibrary.DetectedBuild`, which the first design and
the first implementation both used. That is a fingerprint of the local `GameAssembly.dll`
(`{fileSize}-{timeDateStamp}-{checkSum}-{sha}`) and the library is side-scoped on purpose - its tables
are keyed by `(side, build)`. The client's and the server's `GameAssembly.dll` are different files, so
`serverBuild == clientBuild` is false for every legitimate join: the rule would have dropped 100 % of
the packets this design exists to send. (The **test** server's is
`248598344-6a148fd0-ed22e34-3dcd792996235366`, evidence 189 — nothing in this project runs against
`C:\VRisingServer` — and the client cannot produce it.)

What both binaries do share is the version data compiled into each of them. From the client and
server metadata of this build, `Major=1, Minor=1,
Patch=13, Revision=0, VcsRevision=99712, Build=17` are identical and exactly three fields differ:
`Name` ("VRising" vs "VRisingServer"), `UnixTimestamp` and `Date`, because the two binaries were built
about an hour apart. So `PrefabEditBuildIdentity.Compose` uses the six that agree, drops the three that
cannot, and appends `GameVersionUtils.GetNetworkVersion()`:

```
vrising/1.1.13.0+r99712+b17/net<networkVersion>
```

read on each side from `GameVersionUtils.GetVersionData()` plus `GetNetworkVersion()`, hashed with the
same FNV-1a as everything else here. The network version is the game's own compatibility number: the
server compares the client's hail against its local value in `OnConnectionApproval` and disconnects
with reason `0xd` on a mismatch (research `server-auth-path.md`), so two processes that are talking at
all necessarily agree on it. It changes whenever the protocol does, which is when component layouts
can change - which is the whole question the field is asked.

**What a mismatch means:** the two processes are running different game builds (normally impossible,
because the same number gates the connection) or one of them could not read its own version data. The
layer is dropped, once per Core generation, with both hashes and the local identity string on the
line. `0` is "identity unknown", and **0 matches only 0** - an unknown build never accepts bytes laid
out for a known one. This is not a Progenitor version check; the handshake already kicks on that.

The reader closure is registered by `PrefabEditHandle.Write<T>` next to the baseline capture, and by
`SetComponent<ItemData>` (the interop `ItemData` carries bool fields and is not C# `unmanaged`). Both
produce the **marshaled** layout - `Marshal.StructureToPtr` into a `Marshal.SizeOf(type)` buffer,
`PrefabEditWire.TryMarshalBytes` - because that is what the receiver validates the length against and
what `Marshal.PtrToStructure` parses. **One measure on this wire, and it is `Marshal.SizeOf`.**
FEDW1 shipped the typed reader as `MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))`, the managed
layout, while the receiver checked and parsed the marshaled one; default marshaling widens `bool` to
four bytes and CLAUDE.md's EV17 rule records `sizeof` reading an unpadded extent, so where the two
differed every record was refused as a size mismatch - indistinguishable from the negative control
below - and where they happened to agree on size but not on field offsets the receiver would have
parsed the bytes wrong (R50 finding 10). Both sides run the same generated interop assembly, so the
marshaled layout is identical on the two ends. Nothing is measured at runtime and multiplied.

The packet goes out in the same places `PrfD` does: to a joining client after its `PrfD`/`PrfK`
(reliable events keep order, so definition, key row, then edit results), and to every handshaken user
whenever a pass changes the results (the `editIdentityHash` plus a hash of the payload bytes decides
"changed"; a re-run that writes the same bytes sends nothing).

## What the client does with it

The client applies `PrfE` as **the final layer** for that guid. The ladder is one game-free class,
`PrefabEditApplyDecision`, and its order is the contract: resolve the type by full name (only from
the interop assemblies already loaded, only non-generic value types carrying no managed reference,
never `PrefabGUID`, `Prefab`, or anything `PrefabEditIdentity.IsIdentityType` names), **classify its
shape** (*Safety and refusal* below - the one that matters), check `Marshal.SizeOf(type) == length`,
check the entity already carries the component, **mask the reference-shaped fields back to this
client's own bytes**, capture the baseline, then write:

* `ProjectM.ItemData` through the known-good `SetComponentData<ItemData>` after `PtrToStructure`,
  followed by the same `ItemHashLookupMap` re-sync the handle does;
* every other type through `EntityExtensions.Write<T>` closed over the resolved type with
  `MethodInfo.MakeGenericMethod`. That is a **managed** generic over a blittable struct running in
  CoreCLR - the IL2CPP generic-instantiation trap (interop trap 2) is about calling into IL2CPP
  generics like `SetComponentData<T>`, which this path never does. `Write<T>` already goes through
  `RawWriteGuard.EnsureTypedWrite`, so a stale dump or a wrong size refuses rather than corrupts.
  Both branches receive bytes that have already been through the preservation mask.

  **`MakeGenericMethod` does not enforce `Write<T>`'s `unmanaged` constraint** (R50 finding 10, and
  measured again in this runtime by `MakeGenericMethod_enforces_value_type_but_not_unmanaged`): the
  constraint is a modreq on the type parameter that reflection cannot see, so closing the method over
  `struct { string S; int I; }` succeeds and the body's `sizeof(T)` reports the pointer-inclusive size.
  Reflection *does* enforce "value type" - `MakeGenericMethod(typeof(string))` throws - which is
  strictly weaker, and `RawWriteGuard` only compares sizes. So the resolver asks the question itself:
  `RuntimeHelpers.IsReferenceOrContainsReferences<T>()` through
  `PrefabEditWire.ContainsManagedReferences`, and a type carrying a managed field is refused per record
  before anything is written.

The server layer is stored per guid (`ServerEditLayer`) and **re-applied after every local edit pass**
on that prefab, so a client-side editor cannot win over the server. It is captured as a baseline like
any other write, so `PrfU` (unregister) restores vanilla exactly as FEDIT1 does today, and a new client
world drops it with everything else.

## Safety and refusal

**The threat model is the point.** `PrfE` arrives from whichever server the player joined, and the
client is not required to trust it (threat model surface 6). What it does is write raw component
bytes onto a live prefab entity - and the guid is unconstrained, so *every vanilla prefab in the
client's collection is in range*. That is inherent to the feature (a server-only balance mod edits
vanilla prefabs - that **is** phase 2), which is exactly why the rule below is an allowlist.

### The boundary is a list of components (job FEDW4, R52 finding 1)

**`Content/PrefabEditWireTypes.cs` is the network boundary.** It lists, by full name, the component
types Core vouches for on this wire, each with a per-field policy: which fields may be taken from
the wire, which are kept from the receiver, and the `Marshal.SizeOf` both ends must measure. A
record whose type is not on the list is refused on **both** ends — the server registers no reader
for it, so nothing serializes it, and the client never applies it — with one line per type per Core
generation that names the list rather than the shape.

The seed is exactly what today's Sample edits:

| type | size | from the wire | kept by the receiver |
|---|---|---|---|
| `ProjectM.ItemData` | 48 | `SilverValue`, `MaxAmount`, `ItemType`, `ItemCategory`, `RemoveOnConsume`, `SortOrder` | `Entity`, `ItemTypeGUID`, `DropItemPrefab`, `DropItemArc` |
| `ProjectM.LifeTime` | 8 | `Duration`, `EndAction` | — |
| `ProjectM.BuffModificationFlagData` | 16 | `ModificationTypes`, `ModificationId` | — |
| `ProjectM.DisableAggroBuff` | 4 | `Mode` | — |
| `ProjectM.ModifyBloodDrainBuff` | 36 | all twelve (two floats, three `ModificationId`, two `ModificationType`, two ints, three bools) | — |

The first four sizes were measured live (evidence 191). `ModifyBloodDrainBuff`'s 36 is derived from
the interop declaration — explicit native offsets 0..34 with `MarshalAs(U1)` bools, rounded to the
4-byte alignment, the same arithmetic that predicts `ItemData`'s measured 48 from its 44-byte
extent — and the suite checks it against `Marshal.SizeOf` over a fixture copied from that
declaration field for field.

**Why a list rather than a better classifier.** R52 measured what a shape rule cannot see in this
game's interop: `Unity.Entities.TypeIndex` is `struct { int Value; }` and is carried by **4,585**
structs; `Unity.Entities.RuntimeBlobHeaderRef` keeps a blob-asset pointer in a plain `long` (the
`Blob` prefix misses it because Unity spelled it `Runtime…`); **351** raw `*Index` / `*Offset`
integer fields are quantities by layout and addresses by meaning; and **1,916** structs surface no
fields at all through reflection. *An integer the game uses as an index and an integer it uses as a
quantity have the same shape.* No walk over fields closes that class, so the decision of which
components a stranger's server may write into a player's process is made by name, by a human, once.

**Plugins cannot extend the list over the wire.** There is no API and deliberately no packet that
carries one — a list a server could add to would not be a boundary. A mod author who needs another
component to travel opens a Progenitor change: the struct's fields and offsets from the game's metadata, the
per-field policy (which fields are plain quantities, which are entities, guids, handles or
indices), and the measured `Marshal.SizeOf`. The list is small on purpose.

### The value domain: what may be *in* an admitted field (job FEDW5, R53 finding 2)

The list decides which fields may travel. It does not, on its own, decide what may be in them, and
R53 measured the gap: of the fields the seed admits, one is a dense 11-member `int` enum the game
switches on, one a dense 4-member one, two are a **byte**-wide enum with 9 members and 247
undefined values, four are floats that can arrive `NaN`, and one is a stack bound that can arrive
negative. The review's other half of the finding is that the per-field analysis the design says a
human made "by name, by a human, once" was written down nowhere. It is written down here.

**How a domain is enforced.** Each admitted field may carry a `PrefabEditFieldValuePolicy`:

* **declared member** — the value must be a member the *receiver's own copy* of that enum declares,
  read by reflection at the choke point so the domain follows the build rather than a copy of it;
* **declared bits** — for a `[Flags]` enum (and for a bare integer bitfield over a named enum),
  every set bit must belong to a declared single-bit member;
* **range** — an explicit inclusive range with its reason and its metadata citation;
* **finite** — a `float`/`double` that is not `NaN` and not an infinity;
* **0 or 1** — a marshaled `bool` byte.

A field with no policy is one where every value of its type is in domain, and *why* is in the table
below — "no policy" is an analysis, not an omission. The check runs on **both** ends: the sender
refuses to frame a record whose local editor produced an out-of-domain value (so a mod author is
told, by name, on their own machine), and the receiver refuses the record after the preservation
mask and before the write. Only that record is refused; the rest of the packet still applies. The
line is logged **once per type and field per Core generation**, keyed on the type and the field and
never on the reason, because the reason carries the attacker-chosen value and keying on it would
buy a hostile server one log line per packet.

A domain that cannot be resolved — a policy naming a field this build does not have, an enum rule
over a field that is no longer an enum, a width no rule can read — refuses **every** record of that
type with the same *"the wire-safe list is stale"* shape the size and field-set gates use.

**Every rule fails closed, including the one that used to fail open** (job FEDW6, R54 finding 3).
A bare integer bitfield names its enum (`BuffModificationFlagData.ModificationTypes` carries
`BuffModificationTypes`), and that name used to be looked for in one assembly and, when it was not
there, dropped silently onto a hard-coded literal — no log, no marker, the design's "the domain
follows the build" claim quietly untrue for that one field. Two things changed. The enum is now
resolved across **the same assemblies the wire's type resolver walks** — because this interop's
split is not the stable thing a single-assembly lookup assumes (`ItemData` and `LifeTime` are in
`ProjectM.Shared.dll` while `DisableAggroBuff` and `ModifyBloodDrainBuff` are in `ProjectM.dll`, and
`ProjectM.ModificationType`, used by a struct in `ProjectM.dll`, is in `ProjectM.Shared.dll`). And
an enum that still does not resolve **refuses every record of its component**, with the stale-list
line naming the enum and the places searched. The dump-cited literal beside the policy is kept as a
**cross-check**: it is never what the wire enforces, and when this build's enum declares a different
mask the difference is logged once per Core generation (`value domain mask for …`) as a stale
citation to re-derive — not absorbed.

**Nothing about that resolution is decided by load order** (job FEDW7, R55 finding 6). The search
FEDW6 widened took the *first* match over `PrefabEditWire.IsAllowedAssemblyName`, which is a
`StartsWith` prefix test — `ProjectMagic.dll`, `ProjectMayhem.dll` and `ProjectMonitor.dll` all pass
it, and any of them may sit in a player's `BepInEx/plugins` — so the mask enforced against bytes
another machine wrote depended on which assembly `AppDomain.GetAssemblies()` listed first, and the
two sides of one build could enforce different masks because they have different plugin sets (the
build-hash gate cannot see that: it compares the *game*'s identity, which is equal). Unlike a
component type, whose resolution is re-checked against `Marshal.SizeOf` by the size and field-set
gates, a wrong enum has no backstop at all — its answer *is* the domain. So resolution now runs in
four steps, and only one of them is a search:

1. **The assembly the seed names.** Every `NamedEnumBits` entry records the simple name of the
   assembly the interop declares its enum in, read off the interop rather than guessed. Today there
   is exactly one such entry, `BuffModificationFlagData.ModificationTypes`, and its enum is declared
   by **`ProjectM.Shared`** — `the IL viewer -t ProjectM.BuffModificationTypes` against
   `BepInEx\interop\ProjectM.dll` answers *"was not found in the module being native, but only
   in ProjectM.Shared"*, and the same command against `ProjectM.Shared.dll` prints the `[Flags]`
   enum. A test refuses a seed entry that resolves an enum by name without saying where it lives.
2. **The naming type's own assembly** — one specific assembly, not a search.
3. **The searched set**, if neither answered: every already-loaded `ProjectM*` / `Stunlock*` /
   `Unity*` assembly, with **every** match collected. One match is taken; **two or more refuse the
   whole component** with `the wire-safe list is stale for …: ambiguous: the bit enum 'X' is
   declared by N loaded assemblies (A, B), and Progenitor will not guess which one this component
   means`. That is the same fail-closed rule as everything else here, applied to the case FEDW6 did
   not consider, and it is what keeps the prefix allowlist from mattering: a third-party assembly
   that declares a game enum's full name can now only cause a refusal, never a substitution.
4. **`Type.GetType`**, for a name that is already assembly-qualified. Nothing is ever *loaded* to
   satisfy any of the four.

R52's duplicate-full-name measurement — **14 duplicates across the loaded assemblies, all
zero-field compiler artifacts** — cannot produce an enum collision on this build, for two
independent reasons: an enum necessarily declares its `value__` instance field, so a type measured
as zero-field is not one, and every step above accepts a candidate only when `Type.IsEnum` is true.
The ambiguity rule is therefore dormant today by measurement, not by hope; it exists for the
plugin-set case, which is the one that is not measurable from this repository. The ambiguous branch
is tested by injecting the step-3 search (`PrefabEditShapeReflect.EnumSearchOverride`), because two
assemblies declaring one full name cannot be built inside a single test assembly.

**The per-field table.** Twenty-seven top-level fields, twenty-three of which take bytes from the
wire. `docs`-side and code-side must agree: the same table is mirrored as a comment block beside the
seed in `Content/PrefabEditWireTypes.cs`, and `PrefabEditWireTypesTests` compares the field names in
the two against the list itself, so they cannot drift apart in silence.

**The seed cites types and fields by name, never by metadata line** (job FEDW6, R54 finding 5). Line
citations used to sit beside each policy; R54 found eleven off by one or two, several landing on a
neighboring field — `MaxAmount`, the one field with a numeric bound, cited the line of
`DropItemPrefab`, a `PrefabGUID` this very list *preserves*. A line number changes on every game
update and says nothing the name does not, so the seed's policy reasons and this table carry none,
and a test keeps it that way.

| field | kind | what the game does with it | policy | worst case from a hostile server |
|---|---|---|---|---|
| `ItemData.SilverValue` | `float` | the item's silver value, summed by trade/salvage pricing | finite | a silly price on this client's copy of the prefab |
| `ItemData.Entity` | `Entity` | back-reference to the item entity | **kept by the receiver** | — (never taken from the wire) |
| `ItemData.ItemTypeGUID` | `PrefabGUID` | identity | **kept by the receiver** | — |
| `ItemData.DropItemPrefab` | `PrefabGUID` | what spawns when the item is dropped | **kept by the receiver** | — |
| `ItemData.DropItemArc` | `PrefabGUID` | the drop's arc prefab | **kept by the receiver** | — |
| `ItemData.MaxAmount` | `int` | the stack bound inventory code compares an amount against | `0 .. 2³⁰-1` | a stack that fills at one, or never — no integer overflow, which the ceiling is for |
| `ItemData.ItemType` | `enum : int`, 11 dense members | switched on by the inventory and equip paths | declared member | an item that behaves as another declared kind |
| `ItemData.ItemCategory` | `[Flags] enum : long` | bit tests for `BloodBound` / `SoulBound` / `Locked` and category filters | **none — see below** | items shown as soulbound, bloodbound or locked on this client |
| `ItemData.RemoveOnConsume` | `bool` (`U1`) | whether consuming destroys the item | 0 or 1 | a consumable that does or does not vanish |
| `ItemData.SortOrder` | `ushort` | inventory sort key | none: every value of the type is in domain | a mis-sorted inventory |
| `LifeTime.Duration` | `float` | seconds counted down by the lifetime system | finite (**not** clamped — see below) | a buff that lasts a long time, or one that never ends |
| `LifeTime.EndAction` | `enum : int`, 4 dense members | switched on when the timer ends: `None`/`Kill`/`Destroy`/`RemoveBuffStack` | declared member | a timed entity destroyed instead of killed, or the reverse |
| `BuffModificationFlagData.ModificationTypes` | `long` bitfield over `BuffModificationTypes` | OR'd into the wearer's networked `BuffableFlagState.Value` | declared bits (0..47 except 8 and 10) | a prefab buff granting declared immunities the server could grant anyway |
| `BuffModificationFlagData.ModificationId` | `struct { int Id }` | the id the reversal path matches on | none: an id, not a subscript (see the label below) | a modification that cannot be reversed |
| `DisableAggroBuff.Mode` | `[Flags] enum : int`, 2 bits | registers one or both aggro modifications on the wearer | declared bits (`3`) | units ignoring, or not ignoring, the wearer |
| `ModifyBloodDrainBuff.BloodValue` | `float` | the value the modification applies to blood drain | finite | a wrong drain rate; a `NaN` under `Multiply` would poison the result |
| `ModifyBloodDrainBuff.BloodIdleValue` | `float` | the idle half of the same | finite | as above |
| `ModifyBloodDrainBuff.ModificationId` | `struct { int Id }` | id | none: an id | as `BuffModificationFlagData.ModificationId` |
| `ModifyBloodDrainBuff.IgnoreIdleDrainModId` | `struct { int Id }` | id | none: an id | as above |
| `ModifyBloodDrainBuff.ModificationIdleId` | `struct { int Id }` | id | none: an id | as above |
| `ModifyBloodDrainBuff.ModificationType` | `enum : byte`, 9 of 256 | chooses `Set`/`Add`/`Multiply`/`BitwiseOR`… for the value | declared member | a different declared arithmetic on a blood-drain number |
| `ModifyBloodDrainBuff.ModificationIdleType` | `enum : byte`, 9 of 256 | the idle half | declared member | as above |
| `ModifyBloodDrainBuff.ModificationPriority` | `int` | sort key among competing modifiers | none: compared, never indexed with | this modifier winning or losing against another |
| `ModifyBloodDrainBuff.ModificationIdlePriority` | `int` | the idle half | none: as above | as above |
| `ModifyBloodDrainBuff.AffectBloodValue` | `bool` (`U1`) | whether the drain value is modified at all | 0 or 1 | the modification applying or not |
| `ModifyBloodDrainBuff.AffectIdleBloodValue` | `bool` (`U1`) | the idle half | 0 or 1 | as above |
| `ModifyBloodDrainBuff.IgnoreIdleDrainWhileActive` | `bool` (`U1`) | whether idle drain is suppressed | 0 or 1 | as above |

**Nothing in this table is memory-unsafe, and that is a measured statement, not a hope.** R53
audited every admitted field against the server metadata and the interop's own `FieldLayout` /
`FieldMarshal` metadata: not one is a pointer, an `Entity`, a `PrefabGUID`, a `FixedString`, a blob
handle, or a length the game multiplies. The residual the value domain narrows is *gameplay* damage
on a client that joined a hostile server, and it lasts as long as that client's world.

**Three entries need their reasoning spelled out.**

* **`ItemData.ItemCategory` has no expressible bit policy**, and that is the analysis rather than an
  omission. Every bit 0..30 is a declared member; the game declares `Locked = -2147483648` as a
  sign-extended `int` constant inside a **`long`** enum, so vanilla data
  legitimately carries bits 31..63; and `ALL = -1` is itself a declared member. No bit
  rule separates a hostile value from a vanilla one, and the values a bit rule *could* refuse are
  reachable through combinations it must allow. Refusing `ALL` alone would be theater.
* **`LifeTime.Duration` is finite but not clamped**, even though the game's own
  `[SyncRangedFloat(0, 10000, 2, 1)]` sits on that field: `-1` is the vanilla
  "never expires" convention and Core's debug buff writes it (`DebugBuff.InfiniteDuration`). A range
  taken from a *sync* attribute would refuse a legitimate edit.
* **`ProjectM.ModificationId` is a counter and a hash-map key, not a handle into a native table —
  inferred from the server metadata, not verified live.** (R53's tail: the claim was in the tree with no
  citation and no verified/inferred label, in the one file whose job is to be the network boundary.)
  The evidence: `ModificationId` is `readonly int Id` handed out by `NewId`, and
  the load path remaps ids through a `NativeParallelHashMap<ModificationId, ModificationId>` with an
  id counter and an old/new maximum (`CompressModificationIdsOnLoadSystem`'s
  `ModificationIdRemappingJob`). A key and a counter, never an array subscript,
  so a wrong one costs a modification that cannot be reversed — not a wrong-memory read.

### The shape rule: preservation inside the list (job FEDW3, R51 finding 1)

**The second gate.** Inside the allowlist the classifier still runs, and it must **agree** with the
per-field policy: the same marshaled size, the same set of top-level fields, and exactly the
declared fields preserved. A disagreement — a game update that moved a field, changed a size, or
turned a number into a handle — refuses the record with a *"the wire-safe list is stale"* line
naming both readings. The list fails closed against a game update; it does not silently widen.

**Fail-closed on a struct that cannot be walked.** A field list reflection could not enumerate, or
an empty one for a component larger than a byte, refuses the type. Before FEDW4 both cases returned
"write every byte from the wire" — the exact inverse of the rule stated one line above them in the
source, and the case R52 measured 1,916 times.

**Integer-encoded handles are matched by name**, as identity types already were: `TypeIndex`,
`SystemTypeIndex`, `ChunkIndex`, `ColliderKey`, `AllocatorHandle`, `JobHandle`, `UnityObjectRef`,
`EntityGuid`, `RuntimeBlobHeaderRef`, and any *type or field* name ending `Index`, `Offset`,
`Handle` or `Ref`. Over-preservation is the safe direction and costs nothing now that the list, not
the classifier, decides what may travel.

**A `bool` is not always four bytes.** Every interop struct here declares
`[MarshalAs(UnmanagedType.U1)]` next to an explicit native `FieldOffset`, so a bool field is one
byte. Measuring them as the default four ran `ModifyBloodDrainBuff`'s last bool (offset 34) past the
end of its own 36-byte component and the fit check refused the whole type.



FEDW1 and FEDW2 shipped an *identity* denylist - a non-generic value type carrying no managed
reference, not `PrefabGUID` or `Prefab`, whose `Marshal.SizeOf` the sender got right, that the
target entity already has. There was **no check on what is inside the struct**, and R51 measured
what this game's interop hands such a filter: **169** public value types with a direct
`BlobAssetReference<T>` field (which bottoms out in `BlobAssetReferenceData.m_Ptr`, a raw `byte*`),
**781** with a direct `Entity` / `ModifiableEntity`, **286** with a raw pointer field - **every one
of them C# `unmanaged`**, so every one accepted. One well-formed 60-byte packet naming
`ProjectM.CreateGameplayEventOnKill` wrote an attacker-chosen `0x4141414141414141` into a field
Burst code dereferences, in a process holding the player's Steam session.

So the rule is now positive, and it lives in `Content/PrefabEditShape.cs` (game-free, unit-tested
against real fixture structs; `PrefabEditShapeReflect` fills it from `Marshal.OffsetOf` /
`Marshal.SizeOf`):

* **Value-shaped** - the bytes come from the wire - is a primitive numeric (`byte`..`double`,
  `char`), `bool`, an enum, or a struct of those, recursively, to a depth of 8.
* **Reference-shaped** - the bytes come from *the receiver's own current component* - is anything
  else: a pointer, `IntPtr`/`UIntPtr`, a field whose type's last name segment is `Entity`,
  `ModifiableEntity`, `PrefabGUID`, `NetworkId` or `SystemHandle`, or begins with `Blob`,
  `FixedString`, `FixedList`, `Native` or `Unsafe`, a nested struct reflection could not open, and
  any form the adapter could not name. Unknown preserves; that is the safe direction.
* The receiver builds a **mask** of those byte ranges from the field offsets and sizes of *its own*
  type, copies its current bytes over them, and writes the result. `ItemData.Entity` and any
  pointer therefore stay exactly what the client already had, whatever the packet said.
* A type whose every byte is reference-shaped is **refused**, and the same classification runs on
  the **server at send time**, so such a component is never shipped at all.
* One line per type per Core generation names the fields that were preserved.

**Why identity types are matched by name.** `Unity.Entities.Entity` is `struct { int Index; int
Version; }` and `PrefabGUID` is `struct { int _Value; }` - pure allowlist *by shape*. They are
references in meaning, not in layout, so a small name list is the only thing that can see them. The
cost is that an unrelated struct called `NativeSomething` is preserved rather than written.

**Known narrowing, deliberate.** `PrefabGUID` is on that list as an identity, so a component field
that happens to be a `PrefabGUID` used as *data* (a "spawn this on consume" reference, say) no
longer travels: the client keeps its own value and the log names the field. That is the price of a
rule that cannot tell a guid used as a pointer from one used as a number, and it fails towards the
client's own state rather than towards the server's. If a real edit needs one, the answer is a
narrower allowance keyed on the *component* type, not a hole in the shape rule.

**`ItemData` is held to the same rule.** It used to be the one path with no shape check of any kind
(straight to `SetComponentData<ItemData>`), and it carries an `Entity` field; the bytes reaching
either writer branch have now been through the mask.

### The rest of the refusals

* The sender's game identity hash must equal the receiver's (see *The game identity* above). A
  mismatch logs once per Core generation and drops the packet; the handshake already kicks on Core
  version mismatch, so this only guards two processes on different game builds - or one that could
  not read its own version, which hashes to 0 and matches only 0.
* `count`, `typeFullName` length, `length` and total packet size are capped in `PrefabEditWire`; the
  join cap of 256 synced rows in `PrefabWire.PrefabSyncBudget` is charged **once per prefab**
  (it bounds how many prefabs a server may reach into, not how often it corrects them).
* A type name on the wire must be **printable ASCII**. The parser used to mask bytes with `& 0x7F`
  and keep the result, so `TryRead` was not the inverse of `TryWrite` and a name carrying a newline
  forged whole log lines through sixteen refusal sites per packet. Every type name that reaches a
  log line additionally goes through `PrefabEditWire.SanitiseTypeName` (printable, 64 characters),
  and the line says when it was altered.
* A type that does not resolve, is not a value type, is generic, carries a managed reference, is an
  identity type, is entirely reference-shaped, or whose size disagrees is refused **per record**;
  the rest of the packet still applies. Every refusal has its own log line, and a *standing* refusal
  (a size disagreement, a shape refusal) is logged once per type per generation rather than once per
  pass.
* The server never sends a component the pass did not touch, so a vanilla prefab with one edited field
  ships exactly one component, not the entity.

### Bounds on the channel (job FEDW3, R51 findings 2 and 8)

The per-prefab budget charge left the *packet* unbounded: after the first packet for a guid every
further one was free, and each cost a parse, up to 16 type resolutions, up to 16 entity writes and
up to 16 log lines. Three bounds now sit in front of it, in this order:

1. **Per join, `PrefabSyncBudget.TryAdmitEditPacket`:** at most 512 `PrfE` packets and 1 MiB of
   payload, one log line per join when spent. Charged before anything is parsed.
2. **Receive-side dedupe:** the payload hash per guid; an identical repeat returns before the store,
   the writes and the log lines.
3. The per-prefab synced-row budget, unchanged.

On the **send** side, one packet per prefab per second (`PrefabEditWireLayer.MinSendIntervalMs`),
with the suppressed send **coalesced into a trailing one** rather than dropped, so an editor whose
value changes every frame produces one packet a second and the last value still lands. And the
dedupe is asked *before* the expensive half - read the readers, hash, ask, and only then resolve
types, walk the caps and frame the packet - so a no-change pass costs a read and a hash rather than
a full serialize.

### Withdrawing a layer, and a layer that arrives too early (R51 finding 7)

* **`count == 0` is a withdraw.** It used to be a refusal; the encoding is now spent on the one
  thing `PrfU` cannot do, because a *vanilla* prefab has no registry entry: drop the layer for this
  guid and restore its baseline. The server sends it when the last editor of a prefab is removed or
  when a pass stops touching it, and the right to send one is claimed once so the two seams cannot
  both fire. On the client, a prefab with local editors gets an `EditorsChanged` pass (restore, then
  re-run them); one without gets a plain restore.
* **A layer that lands before `PrefabCollectionSystem` has the entity is queued**, not dropped in
  silence, and retried on the registry's wire frame with a bounded budget (240 attempts, about a
  minute) and one line on give-up. For the case the whole feature is for - a client with *no* local
  editor for that guid - no later pass ever revisited it, so the layer was lost for the session.

### Recovering after a client Core swap (R51 finding 5, corrected by R52 finding 3)

The received layer **does not survive a client Core swap**: it lives on `PrefabEditTable.Wire`, a
Core static, so the next generation builds an empty one. (Two files used to assert opposite things
about this; the layer is gone, and the resend below is the recovery.) The server's dedupe hash is
untouched by the swap and the player did not rejoin, so nothing ever re-pushed and the client's own
editors won permanently for the rest of the session.

FEDW3 tried to catch this with an edge into `Handshake.UserAnswerStatus(index) == "ok"`, on the
premise that a Core swap re-runs the handshake. **It cannot fire for a client-side swap**: the
Install re-arm is server-only, `HandshakeAnswerWatch.Arm` returns early for a user already at `Ok`,
and `OnAnswer` writes `Ok` over `Ok`, so the status never leaves `ok` and there is no edge.

So the recovery is an explicit seam:

```csharp
public static void PrefabRegistry.ResendEditLayersTo(int userIndex)
```

Server-side, idempotent, and called by the `Networking` side when an **already-ok** user re-answers
the handshake — which is exactly what a client that swapped Core does. The poll stays for the case
it does see (a **server** Core generation, where every user re-answers) and the two cannot duplicate
each other, because every per-user send is deduped **per user**: `PrefabEditWireLayer` records the
payload hash each user was last sent per prefab. That also fixes the join duplication R52 measured —
the join send and the first handshake-ok edge used to ship every layer twice, charged twice against
the client's own 512-packet budget. The join send clears that user's record first (a user index is
reused) and is the only one that touches the broadcast dedupe and the rate floor; a resend touches
neither, so it cannot delay a broadcast or drop an owed trailing send.

## What changes for mod authors

* `Content.Edit` still runs locally on the client when the plugin is present, but the result is now the
  server's regardless. The "invalidate on `SettingChanged`" ritual in `docs/modules.md` becomes
  server-only.
* Phase 2 (after live verification): the `ServerMustHave` requirement for editors is relaxed to a
  warning when every touched type is wire-safe, which is what makes a **server-only balance mod**
  possible: a plugin installed only on the server edits vanilla prefabs and every client sees the
  numbers. That is the capability the current design forbids. R51 was explicit that phase 2 must not
  land before the shape rule, because phase 2 is precisely what makes "a server reaches into every
  client's vanilla prefabs" the expected case rather than the attack; FEDW3 landed it. **What phase 2
  still owes is the two-process check** (step 2 of the plan below, plus step 3's negative control) —
  the client half of this design has never run against a real server.

## Verification plan

1. **Server-only round trip through `progenitor_exec`** (no client needed): serialize the Sample's
   edited `sample.ember` `ItemData` and `sample.nodrain` `BuffModificationFlagData` with the reader
   closures, restore the baseline, apply the bytes through the resolved-by-name writer, read back
   `MaxAmount == 99` and `ModificationTypes == 7`. This proves the byte path minus the network hop, and
   the network hop is what `PrfD` already proves.
2. **Two-process check** at the next client sitting: the Sample's Late editor removed from the *client*
   only; join; the client's `sample.ember` tooltip must still show 99, and the log must carry
   `PrefabRegistry: applied PrfE for 480926629: ItemData (server layer)`. Then the server's synced
   `Sample.EmberStack` changed to 50 with the client connected: the client converges without any
   `InvalidateEdits` on its side.
3. **Negative control:** a hand-built `PrfE` with `length` off by one is refused per record with the
   documented line and the entity is untouched. Unambiguous since FEDW2: the sender and the receiver
   take the same measure, so a size mismatch can only be a hand-built packet or a real layout
   surprise, never the normal path.

## Why not the alternatives

* *Send the editor inputs instead (synced config) and rely on identical editors.* That is today's
  design; it fails the moment the two closures differ, and it cannot express a server-only mod.
* *Send whole entities.* The archetype differs per side (client-only presentation components) and the
  cost is unbounded; touched components are the exact minimum.
* *Run the client's pass only when the server says so.* Still requires the plugin on the client.

## Status

**Landed (FEDW1); server round trip verified on the test server 2026-09-05 and re-verified after each rewrite of the byte measure and the apply path (evidence 189 for FEDW1: ItemData 48 bytes, LifeTime, BuffModificationFlagData, DisableAggroBuff all `applied=ok, readBack=match`, `MaxAmount=99`; evidence 190 for FEDW2's Core `2f1f9f0`; evidence 191 for FEDW3's Core `a3207de`, which also measured `ItemData size=48 preserved=[Entity, ItemTypeGUID, DropItemPrefab, DropItemArc]` — the four sizes the wire-safe list pins); compatibility field, byte measure and the managed-reference refusal corrected by FEDW2 (R50 finding 10); the shape rule, the channel bounds, the withdraw, the early-arrival retry, the handshake re-send and the extracted decision class added by FEDW3 (R51 findings 1-9); the wire-safe list made the boundary, the classifier's fail-open cases closed and the resend seam added by FEDW4 (R52 findings 1 and 3). The two-process check is still pending, and nothing in FEDW4 is live-verified.** The files:

* `Content/PrefabEditWireTypes.cs` — **the boundary**: the vouched component types, their per-field
  policies and their expected sizes, plus the agreement check against the classifier. Game-free.
* `Content/PrefabEditWire.cs` — framing, caps, the identity rule and `PrefabEditBuildIdentity`, the
  byte measure, the type rules (`TryAcceptType`), the log sanitizer. Game-free.
* `Content/PrefabEditShape.cs` — the preservation classifier and the byte mask, the second gate
  inside the list. Game-free, driven by tests against fixture structs.
* `Content/PrefabEditShapeReflect.cs` — the reflection that feeds it (`Marshal.OffsetOf` /
  `Marshal.SizeOf`), cached per Core generation. Game-free.
* `Content/PrefabEditApplyDecision.cs` — the security boundary as one ladder: resolve, shape, size,
  `HasComponent`, mask, baseline, write, with a per-record catch. Game-free; the live adapter behind
  `IPrefabEditApplySite` only supplies reflection facts and performs the write.
* `Content/PrefabEditWireLayer.cs` — readers, the send dedupe and rate floor, the receive dedupe, the
  stored server layer. Game-free.
* `Content/PrefabEditWireApply.cs` — serialize, send, receive, apply: the only part that needs an
  `EntityManager`.
* Write-site readers in `PrefabEditHandle`; the seams, the withdraw restore and the wire frame in
  `PrefabRegistry`.

52 tests at FEDW1, 63 after FEDW2, 93 after FEDW3, 128 after FEDW4. Phase 2 (the compatibility relaxation) is a separate job after the two-process check passes.

Four things the implementation settled that the design above left open:

* **The packet is capped at 15 KiB**, because that is `CustomEvents.MaxPayloadBytes` and the
  per-record caps multiply out past it (16 x 1154 = 18,477). `PrefabEditWire.SelectRecords` trims
  before the packet is built and names every record it refused; the builder never truncates.
* **The handshake gate is read through `Handshake.UserAnswerStatus(index) == "ok"`**, not
  `ConfigSync`'s private `HandshakeOkUsers` mirror, which Content cannot see. It is the same fact:
  `AnswerWatch.OnAnswer` is called on the accepted-answer path one line before `ConfigSync` fills its
  set. The **join** send is not gated on it — `OnUserConnected` fires before the client has answered,
  so `PrfE` follows `PrfD`'s own join policy.
* **The per-join synced-row budget is charged once per prefab, not once per packet.** The cap exists
  to bound how many prefabs a server may reach into; charging every push would let a server that
  legitimately corrects one value 256 times exhaust it.
* **`PrfE` is in Bootstrap's seeded `ReceiveSlotPolicy` list** (`809f072`, together with `PrcS`,
  whose omission predated this job). `src/Progenitor.Bootstrap/ReceiveSlotPolicy.cs` is the complete
  list of Progenitor fourccs and the one that matters: an owned id missing from `CoreProtocolIds` is
  handed to the game during the Core-swap window. (`PrefabDefinitionPacket.cs` declares only `PrfD`;
  the design note used to point there.)
