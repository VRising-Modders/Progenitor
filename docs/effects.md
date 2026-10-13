# Effects — borrow vanilla art, recolor, host, dispose

Client-only first cut of M3 asset borrowing, plus a **server-authored networked beam** that uses the game's own graphic. Research (internal notes): §12 (client borrow) and §26 (server coil beam). Proven families: UI sprite (evidence 78), particle clone (79), HDRP volume (79, camera-wide), Cardinal sequence (80), professor coil networked beam (`ProfessorCoilBeam_State_Shared`, `ProfessorCoilUtility.CreateBeam`).

Two ways to draw a two-point beam:

| Path | API | Who draws | When to use |
|---|---|---|---|
| Client placeholder | `Assets.Beam` / `ctx.Effects.Beam(BeamStyle)` / Sample `.fx beam` | Owned `LineRenderer` on this client only | Menu, local-only cosmetics, no replication. |
| Server-authored game beam | `ServerBeam.Spawn` / `ctx.Effects.ServerBeam` / Sample `.beam` | Server instantiates the professor coil prefab; every client plays the baked sequence from synced `Start`/`End` | Find-chest, tethers, anything other players must see with the real look. |

The red castle / invalid-placement / coffin / arena-flag sequences are **not** a networked entity family (no synced Start/End). They are client sequences. Server beams still spawn the coil entity (the only `[NetworkedComponent]` two-point beam) and tag `BlastTime` so the client tints the replicated sequence GO (path 2). Path 3 — a Content clone of the coil prefab with `ProfessorCoilBeam_Client` sequence GUIDs swapped for a red family — is documented, not implemented.

Mechanism: get a prefab by name, `Instantiate` a detached clone. Mock resolution and kitbash composition are later M3 items. BepInEx-based debug-overlay mods draw debug lines with their own `LineRenderer` (`useWorldSpace = true`, `positionCount = 2`, `SetPosition` each frame, material copied not instantiated). The owned Cardinal beam follows that mechanism, written fresh.

Every GameObject this module creates lives under a DontDestroyOnLoad root named `progenitor-effects-<pluginId>`. Plugin unload destroys that **exact** root (a GUID that extends another plugin's GUID is not swept). Core Uninstall still sweeps every `progenitor-effects-*` leftover. `Object.Destroy` is one frame deferred; unload uses `DestroyImmediate`.

## The unload sweep

Plugin unload runs `PluginReloader.SweepRegistrations`, whose one hunk for these modules is `PluginStaticApiSweep.Run(plugin)`. That is the **only** production caller and it covers all three owner-keyed static APIs:

| API | Pins the ALC through |
|---|---|
| `Assets.RemoveAll(owner)` | `EffectHandle.Owner` |
| `Ui.RemoveAll(owner)` | `UiHandle.Owner` |
| `ServerBeam.RemoveAll(owner)` | `ServerBeamHandle.Owner` |

Each returns how many handles it disposed, so the log is a real count (EV15, R27 findings 4 and 25 — the sweep used to omit `ServerBeam` entirely and hardcode zeros):

```
Reload: swept 2 effect(s) / 1 panel(s) / 1 server beam(s)
```

A plugin that uses `PluginContext` (`ctx.Effects.RemoveAll()`) is already covered; the sweep is what catches the static API.

**Release note (R28 finding 28.16).** `Assets.RemoveAll` and `ServerBeam.RemoveAll` changed `void` → `int` in EV15. The return type is part of the signature blob, so a plugin compiled against an earlier Core throws `MissingMethodException` at the call site and must be rebuilt. The marker values also moved in EV16 (`CastleRed` 9001 → 9100, `Blue` 9002 → 9200). **EV17 versioned that (R29 finding 16):** the reader accepts scheme 1 and scheme 2 for one release and logs once when it sees the old family, so a mixed-version pair degrades with a named reason instead of silently losing its tints. See *Marker scheme versioning*; scheme 1 goes away in the release after the one that ships scheme 2. On the client `ServerBeam` returns a **disabled** handle for every call, and a disabled handle is never tracked at all — it owns nothing, can never expire, and would otherwise grow `Live` without bound for the whole session:

```
Effects: server beam disabled handle not tracked (client, skipped module, or missing prefab); nothing to sweep
```

**Unless it is given a dispose callback (EV16, R28 finding 28.5).** `IServerBeamHandle.OnDisposed` promises the callback runs "by `RemoveAll`, or by Core uninstall", and neither can reach a handle that is in no list — so a plugin following the interface doc leaked whatever it tied to a client-side handle. Registering a callback on a disabled handle records it (and only then, so an untracked `.beam` loop still records nothing); it leaves `Live` on its own `Dispose`, and the count is bounded by `ServerBeam.DisabledCallbackCap` (256, one log line at the cap).

**Nothing joins `Live` after `Uninstall` (R30 finding 19, R31 finding 21).** Both entry points into the tracking list — `TrackForCallbacks` for a disabled handle, and `Track` for every enabled one a successful `Spawn` returns — check `_installed` inside the same `Gate` that `Uninstall` clears `Live` under, so a spawn or a callback registration racing the sweep is either swept or refused, never stranded. Before this, a beam spawned from a plugin's own unload path (or by a queued callback firing after Core's module uninstall) created real coil entities, landed in a post-uninstall `Live` that `UnhookFrame` had already stopped polling, and made `DescribePins` report `handles=1` for the rest of the process — which the reload pin diagnostic reads as a live pin holding the Core generation. A refused handle is not a dropped one: its owner can still `Dispose` it and that still destroys the entities it made.

**Dispose is a main-thread operation, and an off-thread one is repaired rather than half-applied (EV17, R29 finding 15).** `Dispose` latches immediately — a second call is a no-op and the handle stops being polled — and then either does the work here or, off the main thread, posts it through `MainThread` and still throws so the caller is told. The entities are destroyed, the handle is forgotten and the `OnDisposed` callbacks fire **together, on the main thread**, in that order. Until that runs the handle stays in `Live`, so `RemoveAll` and Core uninstall can still finish it themselves (they do; the completion is idempotent). EV16's version ran the bookkeeping in a `finally` reached *past* the destroy loop, so an off-thread dispose left replicated coil / `NetworkedSequence` entities in the world that nothing owned and nothing could reach, and told the plugin its beam was gone. `OnDisposed` callbacks therefore always run where they may touch `EntityManager`; the one exception is registering on an already-completed handle, which runs inline on the registering thread.

```
Effects: server beam Dispose ran off the game main thread; the entity destroy was posted to the main thread and the handle stays tracked until it runs. Dispose from Plugin.Frame, a command, or EventBus.
```

## API

```csharp
using Progenitor.Effects;

// Static entry (owner + plugin GUID so unload can sweep the root):
IEffectHandle h = Assets.Borrow(AssetKind.Image, "PassiveFrame_Normal", owner: this, pluginId: MyPluginInfo.PLUGIN_GUID);
IHostHandle host = Assets.Host(h, HostKind.Overlay);
IReadOnlyList<string> names = Assets.Find(AssetKind.Particle, "Ember");
Assets.RemoveAll(this);

// PluginContext accessor (fills owner / plugin id and Track):
var img = ctx.Effects.Borrow(AssetKind.Image, "PassiveFrame_Normal") as UiImageHandle;
var overlay = ctx.Effects.Host(img, HostKind.Overlay);
ctx.Effects.RemoveAll();

// Server-authored game beam (replicates; every client draws the coil sequence):
IServerBeamHandle beam = ServerBeam.Spawn(BeamPreset.CastleRed, start, end, seconds: 8f, owner: this, pluginId: MyPluginInfo.PLUGIN_GUID);
beam.Track(character.Index, character.Version, rat.Index, rat.Version, BeamPresets.DefaultStartOffset, BeamPresets.DefaultEndOffset);
IServerBeamHandle two = ServerBeam.Beams(BeamPreset.CastleRed, start, end, 8f, count: 2, spacing: 0.4f, owner: this);

// Entity-endpoint sequences (no per-tick writes; the clients resolve the positions):
IServerBeamHandle link = ctx.Effects.ServerBeamLink(character, rat, seconds: 8f);              // two entities
IServerBeamHandle mark = ctx.Effects.ServerBeamMark(rat, seconds: 8f);                          // one entity, red placement beams
IServerBeamHandle blue = ctx.Effects.ServerBeamMark(rat, 8f, BeamPresets.MarkSequenceBlue);

// Release anything spawned only to serve this beam. Runs exactly once, from every path that
// ends the beam: Dispose, the lifetime deadline, RemoveAll, Core uninstall.
mark.OnDisposed(() => ratHandle.Dispose());

ServerBeam.RemoveAll(this);
```

## Server-authored beams

`Progenitor.Effects.ServerBeam`. Server-only; the client returns a disabled handle (one Debug line) so shared plugin code does not need a side check. Spawn / Track / Steady / Blast / Dispose / RemoveAll throw off the game main thread.

Implementation (metadata-backed, not `CreateBeam` ECB): `EntityManager.Instantiate` of the prefab that carries `ProfessorCoilBeam_Data_Server` (resolved by name, GUID, or a `Prefab + ProfessorCoilBeam_Data_Server` query — the spawnable name is not a string literal in the game's metadata). Then pointer overlays on `ProfessorCoilBeam_State_Shared` (Start @0 / End @0xC / PreviewTime @0x18 / BlastTime @0x1C, snapshot 32) matching the native `ProfessorCoilUtility.CreateBeam` (Instantiate + those two SetComponents + Translation). A dummy `ProfessorCoilEvent` entity (no `SpawnTag`, so `ProfessorCoilSystem_Server_Spawn` does not create extra beams) keeps `State_Server.Event` valid. Dispose is `DestroyUtility.Destroy` + `DestroyTag`.

### Why a beam has to keep talking (EV13)

The coil beam is authored as a **≤ 3 s** effect and nothing in the game keeps a longer one alive. Three facts, all dump-backed and live-checked:

1. **Its whole networked payload is one component.** The generated snapshot for this prefab, `Snapshot_acdkgjbefjd`, carries `ProfessorCoilBeam_State_Shared` and nothing else — not `Translation`, not `Age`, not `State_Server`. That is why the replicated copy reads `Translation (0,0,0)` on the client and why every visible change has to travel through Start / End / PreviewTime / BlastTime.
2. **The server only sends an entity that changed.** `PrioritizeSystem` builds each user's serialize list from `UpToDateUserBitMask` / `SyncToUserBitMask` and the per-field `SnapshotFrameChangedBuffer` stamps (field indices from `Snapshot_acdkgjbefjd`). A beam whose endpoints are standing still produces no diff, so no packet.
3. **The client disables an unheard-from entity after three seconds.** `NetworkedTimeout` on the coil prefab is `TimeoutAfter = 3, Enabled = true` (live-read on both sides). `ReceivePacketSystem.ReceivePacketJob` refreshes `LastTimeSynced` when it deserializes a snapshot for the entity; the client's `TimeoutSystem` (`[SpawnInWorld(4)]`, `AfterDeserializationGroup`) then adds `DisabledDueToTimeout` + `Disabled`. Once disabled the copy never moves again and never receives the destroy either — exactly the leaked, frozen beams of evidence 131.

So every live beam **forces a synced change at least once a second** (`BeamPresets.KeepAliveSeconds`, comfortably under `NetworkedTimeoutSeconds`) by alternating `PreviewTime` between two values that keep the same phase. Endpoint moves count as the change on their own; the alternation only carries a motionless beam.

Per tick, per beam entity, the server writes:

| Write | Why | Ref |
|---|---|---|
| `State_Shared.Start` / `.End` | the beam itself; the only synced payload | `ProfessorCoilBeam_State_Shared` |
| `State_Shared.PreviewTime`, alternating, at least once a second | keep-alive: the only way to make an unchanged beam produce a snapshot diff | `Snapshot_acdkgjbefjd`, `PrioritizeSystem` |
| `Translation` = beam **midpoint** | position relevancy: `PrioritizeSystem.AddCloseToUserJob` places the entity in the `UserActivityGrid` from `Translation` | `PrioritizeSystem.AddCloseToUserJob`, `GetPriorityWithPosition` |
| `Age = 0` | the server's own `ProfessorCoilSystem_Server_Update.UpdateBeams` does exactly one thing to a beam: `if (State_Server.State == Preview && Age > PreviewTime) State = Blast` (native) | `ProfessorCoilSystem_Server_Update.UpdateBeams` |

and once, at spawn, it **removes `DisableWhenNoPlayersInRange`** from the instance: `DisableWhenNoPlayersInRangeSystem_Server` disables a whole terrain chunk at a time, a disabled entity drops out of `PrioritizeSystem`'s queries, and replication then stops with no error anywhere. A beam is short-lived and explicitly owned by a mod, so it opts out. If something disables a beam anyway, the runtime logs one warning naming the entity.

### The client tick's failure paths (EV16)

Three rules the EV15 tick did not have, all in `ServerBeamRuntime.ClientBeamTick` with their game-free halves in `ClientBeamPoll` / `ClientBeamTracker` so the test project drives them:

| Rule | Why |
|---|---|
| An **unreadable** `World.SequenceNumber` counts as a world change (`ClientBeamPoll.ShouldRebuild`, `WorldSeq` returns `ulong?`) | R28 finding 26. The old `catch { return 0UL; }` made every unreadable world report 0, so the inequality was never true and the cached `EntityQuery` outlived its world; `ToEntityArray` on it is a native crash, not an exception. One wasted rebuild beats one dead world. |
| A query released **because** the world is gone or swapped is dropped without `Dispose` | same finding: disposing a query whose world is already destroyed reaches into freed native state. A destroyed world disposes its own queries. |
| A query build or enumeration failure logs **once per episode** and reports `ClientBeamPoll.FailureBeamCount` (0) so the idle poll applies | R28 finding 28.4. Both paths logged unconditionally and left the "always enumerate" sentinel, so a client in that state wrote sixty warnings a second for the session. |
| A client world loss drops the tracker's keys **without** end lines (`ClientBeamTracker.DropForWorldLoss`) | R28 finding 28.9. The tracker survived the world loss, so the next tick in the *new* world reported "tracking ended" for entities of the dead one. |

### Steady vs Blast

The client picks the sequence itself, from **its own local `Age`** against the **synced `PreviewTime`**. The native `ProfessorCoilSystem_Client` lambda:

```
if (Start == End) return;
if (SequenceState == Entity.Null)  enqueue(Start, End, Preview);   // once, endpoints baked in
else if (BeamState == Blast)       return;                          // one-way
else if (Age > PreviewTime)        enqueue(Start, End, Blast);      // once
```

`Age` is never synced, so it starts at 0 on each client and just runs. EV11 wrote `PreviewTime = seconds` (8), the client's Age passed 8, and the beam played the coil **discharge** — measured live on the leaked copy: `Age = 10.93`, `PreviewTime = 8`, `BeamState = 1`.

| Phase | `PreviewTime` written | Client shows |
|---|---|---|
| `BeamPhase.Steady` (default) | `20` / `19.5` alternating (`SyncRangedFloat(0, 20)`) | the Preview sequence for the whole lifetime |
| `BeamPhase.Blast` | `0` / `0.0625` alternating | the discharge on the client's next frame |

`handle.Steady()` / `handle.Blast()` switch and rewrite on the spot. Blast is one-way per client: a client that has already seen the flash never returns to Preview, so choose the phase at spawn.

**Lifetime cap.** A steady beam lives at most `BeamPresets.MaxSeconds` (19 s), because `PreviewTime` cannot sync above 20 and the client's Age would pass it. `ServerBeam` clamps a longer request and logs once. For a longer beam, spawn a new one.

### Tracking reaches the client (EV14)

Replicating the endpoints is not enough on its own: the vanilla client system bakes `Start`/`End` into the sequence on the single frame it spawns it (the code above) and never looks again. So the client tick (`ClientBeamTick`, run from `Plugin.Frame`) walks replicated coil beams whose `BlastTime` carries a Progenitor marker, reads the live synced `Start`/`End`, and re-points the sequence. It never writes the pooled clone's renderers or materials (see *The pooled clone is read-only*).

**Per-frame cost of the client tick (EV15, R27 finding 10).** `ServerBeam.Install` hooks `Plugin.Frame` unconditionally, so this tick runs on every client for the whole session whether or not anything ever called `ServerBeam`. It therefore allocates nothing per frame: the `EntityQuery` is built **once** per Core generation and disposed by `IServerBeamRuntime.Shutdown()` from `ServerBeam.Uninstall` (the pattern `PrefabApplicator` already uses), the enumeration is `ToEntityArray(Allocator.Temp)` read in place — no `ComponentType[]`, no `List<Entity>` — and the tracker's `ended` list is reused. When the last enumeration found **no** coil beams at all (the normal state of a client nowhere near one), the query only runs every `ClientBeamPoll.IdleQueryEveryFrames` (15) frames; the frame a beam appears it is back to every frame. A coil beam's body is baked by the vanilla client system on the frame it spawns, so starting the re-point up to a quarter of a second later is invisible.

**The re-point channel is the sequence's blackboard, not a LineRenderer buffer.** Native `ProfessorCoilSystem_Client.OnUpdate` (client) is, per dequeued spawn:

```csharp
if (client.SequenceState.Id != Entity.Null) DestroyUtility.Destroy(em, client.SequenceState.Id);  // ProfessorCoilSystems.cs:625
var rot = quaternion.LookRotation(End - Start, up);          // raw difference, not normalised
var guid = GetSequence(length(End - Start), state, client);  // one of four fixed-length prefabs
var seq = SequenceUtility.PlaySequence(em, beamEntity, prefabOf(guid), Entity.Null);
seq.SetBlackboardInput(em, "Position", Start);               // string literals 18611 / 14192
seq.SetBlackboardInput(em, "Rotation", rot);
client.SequenceState = seq; client.BeamState = state; em.SetComponentData(beamEntity, client);
```

So a coil beam's body is a **fixed-length sequence prefab placed at `Position` and aimed by `Rotation`** — there is no two-point line to re-point. `(Sequence)ProfessorCoilBeam` (live-read on the client: guid `1419723633` / `491911373` / `-1239335937` / `-758460232` for the four Preview buckets) carries `SequenceInput` + `BlackboardElement` and **no** `LineRendererPositionElement` and **no** `Pull_Entity`.

That is why EV13's tracking line never appeared live (evidence 134): `TryWriteLineRendererPositions` returned false on every frame because that buffer does not exist on this sequence, and the caller only logged on success. The buffer write is kept for sequence families whose body really is a LineRenderer; the coil is driven through the blackboard.

**How a blackboard register is written** (all offsets checked against the game's metadata and the interop stubs, no generic interop instantiation):

| Step | Where | Ref |
|---|---|---|
| `Stunlock.Sequencer.SequenceInput` on the sequence entity holds `BlobAssetReference<SequenceInputBlob>` | 8-byte blob pointer | `SequenceInput` |
| blob root is `BlobArray<SequenceInputElement> Inputs` = `{ int m_OffsetPtr; int m_Length; }`, items at `&m_OffsetPtr + m_OffsetPtr` | | `SequenceInputBlob` |
| `SequenceInputElement` = `{ FixedString64Bytes Name @0; int Index @0x40; BlackboardRegisterType Type @0x44; bool IsInputParameter @0x48 }`, stride 76 | name match is `Position` (Float3 = 3) / `Rotation` (Quaternion = 9) | `SequenceInputElement`, `BlackboardRegisterType` |
| `Index` is a **byte offset** into the entity's `DynamicBuffer<BlackboardElement>` (one byte per element) | write `float3` / `quaternion` there | native `SequenceStateExtensions.SetBlackboardInput<float3>` |

`Progenitor.Effects.SequenceBlackboard` does exactly those three steps with pointers and caches the two byte offsets per sequence entity. The rotation comes from the game's own `quaternion.LookRotation(End - Start, up)` (called through the interop, not re-derived) so a re-point cannot jump relative to the pose the game baked at spawn.

**Blackboard safety rules (EV15, R27 finding 5 / 17).** Every bound in that walk lives in `Progenitor.Effects.SequenceBlackboardLayout` — a game-free type the test project compiles, so the constants and the overflow behavior are asserted, not assumed. The `#if !PROGENITOR_TESTS` in `SequenceDriverLookup.cs` wraps only the Il2Cpp reads and writes.

**The gate *wiring* is game-free too (R31 finding 26, R30 open question 7).** The helpers above were always tested; the code that *consults* them — which gate is asked, in what order, whether a reading may close one, whether a refusal latches or applies to one call — used to live inside the `#if !PROGENITOR_TESTS`. Research §A35.1 diagnosed all three of EV15, EV16 and EV17 the same way: *the tested code was right and the untested predicate turned it off*, so three batches of fixes to the helpers could not catch a fourth repetition. Every decision now lives in `SequenceWalkWiring` (`Effects/SequenceWalkGate.cs`), on the test project's compile list. The runtime readings arrive as `Func<int>` and the log as `IEffectGateLog`, so `EffectsTests`' `FakeSequenceRuntime` drives the same wiring the client runs — including the fact a wrong `sizeof` with a correct dump stride keeps the walk on, end to end.

| Rule | Where | Why |
|---|---|---|
| Only a **constant pair this file cannot justify** disables the re-point; the gate is `LayoutOnceGate`, consulted by `Resolve`, `TryWrite` and `TryReadFloat3` | `SequenceBlackboardLayout.WalkAllowed` / `PaddingInvariantHolds` | the original check logged `blackboard re-point disabled for this build.` and then set nothing, so the walk ran anyway and the message was a lie. R30 finding 5: it was then wired to a runtime `sizeof`, which the walk never multiplies by — three jobs running latched a process-wide gate on a reading and each took the module down live. R30 finding 14: the predicate never checked its two constants against each other, so an impossible pair read as agreement |
| **The gate starts closed.** `Enabled` means "a probe ran *and* passed"; unprobed and failed both refuse, so every call site runs its probe first and reads the gate after | `LayoutOnceGate` (`Checked` / `Enabled` / `Failed` / `Fail`), `SequenceDriverLookup.StrideOkNow` | EV16, R28 finding 2. The EV15 gate defaulted open, and `CheckStrideOnce`'s `catch { return; }` — the path taken when the interop `sizeof` throws, i.e. exactly the failure the probe exists for — never touched it, so the walk ran against a differently-shaped blob with the hardcoded stride 76. Every probe's `catch` now calls `Check(false, …)` |
| `BlobArray.m_OffsetPtr` is bounded (`0 < offset < 1<<20`), as is `m_Length` (`1..512`), and so is `offset + count * 76` (`≤ 1<<22`), all in `long` | `SequenceBlackboardLayout.WalkOk` / `ElementOffset` | the offset used to be checked only for `> 0`; a garbage or differently-versioned blob root walked a wild pointer and `NameIs` dereferenced `*(ushort*)element` at every step |
| **The walk is bounded by the blob's own extent**, not only by the 4 MiB constant: `BlobAssetHeader.Length` is read 24 bytes behind the data pointer (`BlobAssetHeader` in the server metadata) and `offset + count * 76` must fit inside it. A refusal skips **that blob** | `SequenceBlackboardLayout.WalkFitsBlob` / `ClassifyBlobExtent` / `BlobExtentRefusedLog`, via `SequenceWalkWiring.InputWalkInRange` | R31 finding 14: nothing read the real allocation size, so `TryFindSlots`' per-element `at + stride > blob.Length` check was **circular** — the span it strides was itself sized from the same untrusted `m_OffsetPtr` / `m_Length`. A stale 256-byte blob reading `8` / `512` gives `end = 38,920` and the walk read ~38 KB past the allocation; across an unmapped page that is an access violation `catch (Exception)` cannot catch |
| Every register write is `slot >= 0 && (long)slot + size <= length` | `SequenceBlackboardLayout.SlotFits` | `slot + 12 <= length` overflows: an `Index` of `int.MaxValue - 5` made the sum negative, the comparison passed, and the write landed ~2 GB past the buffer |
| Both raw **buffer** paths resolve the element size from `TypeManager.GetTypeInfo(index).ElementSize` **then** the interop struct's own `sizeof`, and open the gate only on an answer that matches the dump | `BufferElementGuard.Resolve` / `SizeOk`, called from `SequenceBlackboard.TryBuffer` and `UnityEffectRuntime.TryWriteLineRendererPositions` | the two paths read the same accessor differently — `LineRendererPositionElement` as an element count times a hard-coded 20, `BlackboardElement` as a byte count — and both are only correct while the element size is the dump's (20 and 1). EV17, R29 finding 19: `SizeOk` used to be `reported <= 0 \|\| reported == expected`, so a `TypeManager` reading `0` — trap 7, and the entire premise of the guard — **opened** the gate. `0` is now "unknown", the `sizeof` fallback lives in the probe, and *nothing answering* is a refusal with its own log line |
| The `LineRendererPositionElement` **length** is classified three ways — `Ok` (`2 … 4096`), `TooSmall` (`0` or `1`: skip that sequence), `Impossible` (negative, over the cap, or an overflowing byte total: **skip that sequence too**, R30 finding 9) — and once `Ok`, `length * stride` is checked in `long`, every element offset is `(long)i * stride`, and every field write is checked against `length * stride` before the pointer is formed | `LineRendererBufferLayout.Classify` / `LengthOk` / `ElementOffset` / `FieldFits` (game-free, compiled by the test project), called from `UnityEffectRuntime.TryWriteLineRendererPositions` | EV16, R28 finding 1: `if (length < 2) return false;` was the **only** bound on the path whose element-size guard exists because `GetBufferLength` can read garbage — a large length wrote 20 bytes per element past the chunk, and `i * stride` overflows `int` at `i ≈ 107,374,182` and wraps negative, writing *before* the buffer. EV17, R29 finding 4: EV16 then latched on **both** refusals, so one ordinary sequence with an unauthored (0-element) or single-point line buffer turned the raw path off for every beam for the rest of the process. R30 finding 9: the *other* half of that split never landed here, so an `Impossible` length still latched the **element-size** gate — a fact about one entity closing a gate about the build. Both refusals are per call now |
| Every element is written from **its own** `PositionIndex`; the first two are seeded positionally only when the buffer carries no usable `{0,1}` pair | `LineRendererBufferLayout.SeedPositionally` / `EndpointFor` | EV17, R29 residual 21.9. Elements 0 and 1 used to be written as start and end unconditionally with `PositionIndex` stamped over, while the loop from `i = 2` consulted it — so element 0 carrying `PositionIndex 1` put the start point in the end slot and destroyed the authored index |
| The `BlackboardElement` buffer length is bounded too (`1 … 65536` bytes), before `SlotFits` uses it, and a refusal skips **that sequence** | `SequenceBlackboardLayout.ClassifyBufferLength`, called from `SequenceBlackboard.TryBuffer` | same dependency one level down: `SlotFits` bounds the register write *against the length the interop reported*, so a garbage length authorizes a garbage slot. R30 finding 9: this used to latch a gate on one entity's reading |
| The `BuffSequence` buffer walk strides **16**, bounded like the other two | `BuffSequenceLayout`, called from `UnityEffectRuntime.TryReadBuffSequenceGuids` | R30 finding 2: it strode **24** over a 16-byte element with `length <= 0` as its only bound, so at `i = 1` it read the low dword of element 1's `Condition` **pointer** as a prefab guid hash and from `i = 2` read past the buffer. A buff prefab with several `BuffSequence` entries resolved the wrong sequence or none |

**For a native array stride the dump is the authority; every runtime reading is a cross-check (EV17, corrected in R30).** Four rounds of this went wrong and three of them took the whole blackboard re-point down live with `path=none`.

- EV15/EV16 made **`TypeManager.GetTypeInfo(...).ElementSize`** authoritative and let a nonzero disagreement latch.
- EV17's first pass over-corrected to **`sizeof`**, which is right for a *component* and wrong for a *native array element*. The Il2CppInterop-generated `[StructLayout(LayoutKind.Explicit)]` struct does not carry the native array's tail padding, so `sizeof(SequenceInputElement)` reads **73** while the blob strides **76**, and the client logged `SequenceInputElement stride is 73, not 76; blackboard re-point disabled for this build.`
- EV17's second pass widened the accepted set from `{76}` to `{76, 73}` and kept the structure: a reading was still the on/off switch. R30 finding 5: any *third* value — a regenerated interop, a `Pack` or `MarshalAs` change — would have taken the module down again, for a number nothing in the walk multiplies by.

**The rule, in the form that survives.** A runtime reading can only ever be evidence that the dump looks *stale*. Evidence is a log line.

> The stride and the field offsets of a native array or blob element come from the **dump**. Nothing read at runtime is ever multiplied by anything. The only thing that may refuse a walk is the walk's own constants failing to back each other.

**This is not CLAUDE.md's raw-write policy, and the code no longer says it is (R30 finding 21).** CLAUDE.md's rule is about a **component** write and makes `sizeof(T)` the authority, because `TypeManager.TypeSize` reads 0 for ordinary components through this interop and `SizeInChunk` reported **515** for a 32-byte `Follower`. That rule is correct for a component and is precisely what, applied to a blob walk, produced the EV17 first-pass regression. Two sites in `SequenceBlackboardLayout.cs` and `UnityEffectRuntime.cs` cited CLAUDE.md for the *native array* rule; they cite this section now. The two rules are:

| Write | Authority for the element size | Reading that disagrees |
|---|---|---|
| a **component** overlay (`GetComponentDataRawRW` + a field offset) | the interop struct's own `sizeof` | `TypeManager` warns, never refuses (CLAUDE.md) |
| a **native array / blob / buffer** walk (`i * stride`) | the **dump's** field offsets and the alignment they imply | `sizeof` *and* `TypeManager` both warn, never refuse (this section) |

**What each reading produces.** `SequenceBlackboardLayout.Agree(observed, dumpStride, dumpPacked, alignment)` is the one predicate, and `alignment` is required rather than assumed so the two constants can be checked against each other (R30 finding 14):

| Reading | Verdict | Effect |
|---|---|---|
| the dump's padded stride (76 / 20 / 1) | `Exact` | nothing logged |
| the dump's unpadded field extent (73 for `SequenceInputElement`) | `Unpadded` | one info line naming both numbers |
| anything else, nonzero | `Mismatch` | one **warning**; the walk keeps striding the dump's number |
| unreadable (threw, read 0) | `Unavailable` | one **warning**; same |
| the two dump constants contradict each other | `Unavailable` | one warning, **and this is the one case that disables the walk** |

The invariant `Agree` checks before anything else: the alignment is a power of two, both sizes are positive, `dumpPacked <= dumpStride`, and `dumpStride == RoundUp(dumpPacked, alignment)`. R30 finding 14's hole was a **collision** — a game update whose new unpadded extent equals the old padded stride read as `Exact`, silently, with the walk striding 76 over an 80-byte array and the diagnostic positively asserting `stride=76 (dump 76) sizeof=76`.

**Metadata behind every stride in the module (R30 findings 2 and 22).** `InputElementStride = 76` used to be a literal, and the dump states four field offsets and *no size*: the only runtime observation in existence, `sizeof = 73`, is evidence for 73. It is derived now — `IsInputParameter@0x48 + 1` rounded up to the element's 4-byte alignment — and each type's citation is a constant in the source that a test checks against this table.

| Element | Fields | Metadata | Alignment | Extent | Stride |
|---|---|---|---|---|---|
| `SequenceInputElement` | `Name@0` (64), `Index@0x40`, `Type@0x44`, `IsInputParameter@0x48` | `server metadata SequenceInputElement` | 4 | 73 | **76** |
| `LineRendererPositionElement` | `LineRendererIndex@0`, `PositionIndex@4`, `PositionValue@8` | `client metadata LineRendererPositionElement` | 4 | 20 | **20** |
| `BlackboardElement` | `byte Value@0` | `server metadata BlackboardElement` | 1 | 1 | **1** |
| `BuffSequence` | `SequenceGuid@0` (a single `int GuidHash`, padded to 8), `Condition@0x8` | `server metadata BuffSequence` | 8 | 16 | **16** |

`BuffSequence.Condition` is a `BlobAssetReference<ConditionBlob>`, which is one `BlobAssetReferenceData m_data`, which is a **union** of `byte* m_Ptr` and `long m_Align8Union` both at `0x0` (`server metadata BlobAssetReferenceData`) — **8** bytes, not 16. The pre-R30 comment said 16, which is where the wrong 24-byte stride came from.

Of the four, only `SequenceInputElement` has tail padding to lose; all four go through the same predicate so a future edit cannot quietly reintroduce the trap.

**The two numbers the gate compares must be sourced independently (R31 finding 20).** `BufferElementGuard.WritesAllowed` used to take one size and forward it as *both* stride and packed, which reduced the invariant to `dumpSize == RoundUp(dumpSize, alignment)` — true of every positive value at alignment 1, and true of the wrong 24-byte `BuffSequence` stride at alignment 8. Mistyping `BlackboardElementSize` to 4 still returned `true`, so the gate this file calls the only thing that can refuse the blackboard buffer write **could not refuse anything**, and it would not have caught R30 finding 2. It now takes the size *and* a field extent summed from the dump's own offsets (`BlackboardElementValueOffset + …`, `LineRendererPositionValueOffset + Float3Bytes`). Both buffer elements are unpadded, so their two numbers coincide — which is exactly why they have to come from different places. The one other `TypeManager` read in Effects, `ServerBeamRuntime.SlotOk`'s `SizeInChunk`, was checked and left alone: it is a per-call one-directional bound that proceeds on 0 and never supplies an offset.

**What latches and what does not.** A gate latch is permanent for the Core generation, so after R30 it is reserved for one thing: the module's own dump constants contradicting each other, which is a compile-time fact about the source rather than anything observed at runtime. Everything else — every reading, every per-entity length — refuses at most the call it arrived on and logs once.

| Condition | Effect |
|---|---|
| a type's dump constants fail `PaddingInvariantHolds` | **latch that walk**, `<type> dump constants contradict each other (—)` |
| a blob's `BlobAssetHeader.Length` cannot hold `offset + count * 76` | **skip that blob**, one warning naming every number (R31 finding 14) |
| `sizeof(SequenceInputElement)` is 73 | **nothing is disabled** — one info line, the walk strides 76 |
| `sizeof` of any element is neither the dump's stride nor its unpadded extent | **warn once**, `— the raw walk keeps striding <n> (the dump is the authority)`; nothing is disabled |
| `sizeof` unavailable (threw) | **warn once**, `sizeof was unavailable; the raw walk keeps striding <n> from the dump.`; nothing is disabled |
| `TypeManager` answers a number the dump does not explain | **warn once**, `raw buffer writes stay on`; nothing is disabled |
| `TypeManager` answers 0 | nothing; that is the documented trap 7 |
| line buffer length is negative, over 4096, or overflows the walk | **skip that sequence**, one warning |
| line buffer holds 0 or 1 element | **skip that sequence**, one info line |
| blackboard buffer length is negative or over 65536 | **skip that sequence**, one warning |
| blackboard buffer is empty | **skip that sequence**, one info line |
| `BuffSequence` buffer length is negative or over 256 | **skip that buff prefab**, one warning |
| an element's `PositionIndex` is neither 0 nor 1 | that element is left alone; the walk continues |
| a sequence's `SequenceInput` blob does not resolve yet | retried for `SequenceBlackboard.ResolveRetries` (30) frames before the miss is cached |

**The walk is game-free and prints itself once (EV17).** `SequenceBlackboardLayout.TryFindSlots(ReadOnlySpan<byte>, offset, count, out position, out rotation)` is the blob walk the client runs — production wraps the already-bounded blob range in a span and calls it, so the tests exercise the same code. One Info line per Core generation reports what it saw, which is what the `path=none` regression needed and did not have:

```
Effects: sequence blackboard probe stride=76 (dump 76) sizeof=73 blobOffset=8 count=2 position=8 rotation=24 inputs=[Position:3@8, Rotation:9@24]
```

`stride=` is the number the walk actually uses and comes from the dump; `sizeof=` is the managed reading beside it, and 73 there is expected, not a fault. A `count=0`, or an `inputs=[…]` with no `Position:3@…`, each point at a different cause, and `inputs=[walk refused]` means `WalkOk` rejected the blob header.

<a id="layout-log-lines"></a>**Layout and bounds log lines — the one authoritative list.** Every line below is written **once per Core generation**. R30 finding 23: the "Expected log lines" section near the end of this document used to carry a second, stale copy of three of these, so an operator grepping for the older string found nothing; that section now links here instead of restating them.

The one line that means the module is **off** (and the only latch left after R30 finding 5):

```
Effects: <type> dump constants contradict each other (stride <n>, field extent <n>, alignment <n>; the stride must be the extent rounded up to the alignment); raw walks over <type> are disabled for this build.
```

Lines that mean "a reading disagrees with the dump" — **warnings, and nothing is disabled**:

```
Effects: <type> sizeof is <n>, which is neither the dump's <n>-byte stride nor its <n>-byte field extent; the raw walk keeps striding <n> (the dump is the authority) — this reading is evidence the dump may be stale.
Effects: <type> sizeof was unavailable; the raw walk keeps striding <n> from the dump.
Effects: TypeManager reports <type> buffer element size <n>, but the dump says <n>; the walk strides the dump's number and raw buffer writes stay on.
```

Lines that mean "**this** sequence or buff prefab is skipped" — the first three are warnings, the last two info:

```
Effects: LineRendererPositionElement buffer length is <n>, which no buffer can have (a real one is 2..4096); skipping that sequence (the raw path stays on).
Effects: BlackboardElement buffer length is <n>, which no buffer can have (a real one is 1..65536); skipping that sequence (the blackboard path stays on).
Effects: BuffSequence buffer length is <n>, which no buffer can have (a real one is 1..256); skipping that buff prefab (the BuffSequence resolve stays on).
Effects: LineRendererPositionElement buffer has <n> element(s), fewer than 2; skipping that sequence (the raw path stays on).
Effects: a sequence's BlackboardElement buffer is empty; skipping that sequence (the blackboard path stays on).
```

And the one that is expected on this interop and means nothing is wrong (Info):

```
Effects: SequenceInputElement sizeof is 73 (the managed struct without the native array's tail padding); the raw walk uses the dump stride 76 and stays on.
```

If you see `blackboard re-point disabled for this build` or `raw buffer writes disabled for this build` on a build after R30, the deploy is older than this document: those strings no longer exist in the source.

**Length is quantized.** `GetSequence` (native, client) picks `SequencesLength1` under **12 m**, `…2` under **20 m**, `…3` under **23.5 m**, otherwise `…4` — cuts EV11 recorded as "not in the metadata". `BeamPresets.LengthBucket(metres)` exposes them. The chosen prefab has a fixed drawn length, so a tracked coil beam follows in **position and aim** but keeps the length bucket it was spawned with. A beam whose ends separate across a cut needs a respawn; `ServerBeam.Link` has no such limit.

**Readback.** One line per sequence records the register offsets and what the game had put there before our first write, and a second line fires the first time the value we wrote last frame is not the value we read this frame (the EV5 / EV9 same-frame readback). Together they say whether the sequencer is fighting the write. Both are keyed on the **sequence** entity, not the beam (EV15, R27 finding 23): the coil client system destroys and respawns the sequence on a Preview→Blast swap, and a beam-keyed readback compared our last write against a brand-new sequence's registers and logged a spurious "overwritten between frames".

**Markers.** Every preset writes a marker into the otherwise unused synced `BlastTime`, so the client tick can tell a Progenitor beam from a vanilla coil discharge: `Coil` 9000, `CastleRed` 9100, `Blue` 9200 (`BeamPresets.MarkerSpacing` = 100). Vanilla blast durations are small numbers of seconds, and the server never lets a Progenitor beam consume Blast.

**The wire step is 0.01, measured (EV17, R29 finding 16 / open question 4).** EV15 and EV16 both picked a marker layout against a quantization they recorded as unknown. It is not unknown. `[SyncRangedFloat(0, 10000, 2, 1, null, null)]` on `BlastTime` (server) binds `SyncRangedFloat.ctor(float min, float max, int decimals, OverflowHandling overflowHandling = 1, …)`, so the third argument is **`decimals = 2`**. The generated code calls `NetworkSync.WriteRangedFloat(ref NetBufferOut, value, min, max, decimals)` and `ReadRangedFloat`; in the game's native code (`ProjectM.Network.NetworkSync.WriteRangedFloat`) those are a plain fixed-point integer:

```c
scale = (int)powf(10f, decimals);            /* 0x41200000 == 10f  => 100 */
lo = (int)roundf(scale * min); hi = (int)roundf(scale * max);
v  = clamp((int)(scale * value), lo, hi);    /* truncation, nudged off both endpoints */
WriteRangedInteger(lo, hi, v);               /* read back as v / scale */
```

So `BlastTime` travels as an integer over `0 … 1,000,000` with a step of **0.01**, exposed as `BeamPresets.BlastTimeWireStep`. Three things follow, all of which the two earlier jobs had to guess: EV15's markers one apart were safe; EV16's markers 100 apart are safe; and 9200 is nowhere near the 10000 ceiling, so `OverflowHandling` never clamps and the markers cannot collapse onto one value. `PreviewTime` is the same encoding over `0 … 20` — step 0.01 there too, which is why `BlastPreviewTimeAlt = 0.0625` lands on the wire as `0.06`; still an immediate blast, and still a snapshot diff, which is all the keep-alive needs.

**Marker tolerance (EV15, R27 finding 16; spacing EV16, R28 finding 28.7).** A miss was completely silent, because `ClientBeamTracker.Observe` only ever logs when a beam *matched*, so the comparison is a **band**, never `==`. The markers are **100 apart** and the band is `MarkerTolerance = 40` — under half the spacing, so no value is ambiguous. `BeamPresets.MarkerBucket` returns the nearest marker within the tolerance and **0 otherwise**, so a value in the gap between two markers (9050) is not ours at all; `IsProgenitorMarker`, `IsTintMarker` and `TintForMarker` all go through the bucket.

**Marker scheme versioning (EV17, R29 finding 16).** The marker set is a wire contract and EV16 changed it with nothing naming the change. Against a server on the previous Core, 9001 and 9002 fall inside scheme 2's ±40 band around 9000, so every tinted beam bucketed to the *untinted* coil marker and the only symptom was tints that stopped appearing — the "0 matched markers" line does not fire while other beams match.

| | Coil | Red | Blue | Band |
|---|---|---|---|---|
| Scheme 1 (EV15) | 9000 | 9001 | 9002 | `LegacyMarkerTolerance` 0.4 |
| **Scheme 2 (EV16, current)** | 9000 | 9100 | 9200 | `MarkerTolerance` 40 |

`BeamPresets.MarkerSchemeVersion` is **2**: that is what this Core *writes*. The reader accepts **both** families for one release, and `MarkerBucket(blastTime, out bool legacy)` always answers with a scheme-2 marker, so tint, tracking and the client tick are scheme-agnostic; only the log cares.

**The two schemes overlap, and the order is by exactness (R30 finding 20).** `MarkerTolerance` is 40, which is 4000× the measured wire step, so every value in `[9000.6, 9001.4] ∪ [9001.6, 9002.4]` is inside a scheme-1 band **and** inside scheme 2's band around 9000. EV17 tested scheme 1 first and unconditionally, so it won the whole overlap: a value a scheme-2 server wrote as an untinted coil marker came back tinted, with a permanent and false "this server runs an older Core" warning. The class comment's "no value can be ambiguous between two markers" was true only *within* scheme 2. `MarkerBucket` decides in this order:

1. an **exact** scheme-2 marker (9000 / 9100 / 9200) — always, before any band;
2. an **exact** scheme-1 marker (9001 / 9002) — reported as legacy. Those are the only two values an older Core ever wrote and the wire step is 0.01 with no rounding, so 9001 arrives as exactly 9001;
3. scheme 2's ±40 band, which is then the only band in play.

`BeamPresets.MarkerSchemesOverlap(blastTime)` is true for a value both schemes claim, and `MarkerSchemeOverlapLog` names the winner. The client tick classifies every replicated `BlastTime` through `ClientMarkerLog.Classify`, which is what writes this line — once per Core generation (R31 finding 17: both helpers were correct and had **no caller**, so an overlapping value resolved to `MarkerCoil` silently while this section documented a line nobody could see):

```
Effects: server beam client saw a BlastTime (9001.2) that both marker schemes claim; scheme 2 won and it reads as 9000 (scheme 1 is accepted only for its exact values 9001/9002).
```

The legacy line still fires once per client for an exact scheme-1 value:

```
Effects: server beam client saw a marker-scheme-1 BlastTime (9001); this server runs an older Core. Scheme 2 (9000/9100/9200) is what this build writes; scheme 1 is accepted for one release.
```

**Scheme 1 is removed in the release after the one that ships this.** Until then, seeing that line means a mixed pair, not a bug. And when coil beams are replicating but none of them carries a marker in *either* scheme, the client says so once:

```
Effects: server beam client: <n> coil beams, 0 matched markers
```

That line is the negative evidence the old code could never produce. If it appears with a nonzero count while a server beam is up, the marker did not survive the wire and the whole EV13/EV14 client re-point is doing nothing.

**Presets**

| Preset | Prefab family | Look | Length buckets | Marker |
|---|---|---|---|---|
| `CastleRed` (default) | Coil entity (`ProfessorCoilBeam_*`) | Coil Preview sequence; the red tint is recorded but **not applied** (the sequence clone is pooled and read-only) | Four (`SequencesLength1`–`4`), picked by the native `GetSequence(float length, …)`; beyond bucket 4 uses bucket 4. Exact meter cuts are not in the game's metadata. | `BlastTime = 9100` |
| `Coil` | Same | Native coil Preview sequence, untouched | Same four buckets | `BlastTime = 9000` |
| `Blue` | Same | Same as `CastleRed`, blue tint recorded | Same | `BlastTime = 9200` |

| `Link` | `NetworkedSequence` (guid 651179295) | **Not a coil beam.** The game's own sequence carrier: `SpawnSequenceForEntity { SequenceGuid, Target, SecondaryTarget, PlayOnSelf }` syncs both endpoints as *entities* (a `NetworkId` pair), so every client resolves both positions itself each frame. No per-tick writes, no keep-alive (the prefab carries no `NetworkedTimeout`), no phase, no `MaxSeconds` cap. Spawned by `ServerBeam.Link(a, b, seconds, sequenceGuid)` (two entities) or `ServerBeam.Mark(entity, seconds, sequenceGuid)` (one). | n/a | n/a |

### Naming a sequence: `(Sequence)<asset>`

A `SequenceGUID.GuidHash` **is** a `PrefabGUID` hash: native `ProfessorCoilSystem_Client.OnUpdate` looks its sequence up in `PrefabLookupMap`, and the client's prefab name map calls those entities `(Sequence)<asset name>`. So a sequence can be resolved by name on the client — `client prefab {"prefab":"(Sequence)AB_Charm_Channeling"}` — but **not on the server**: sequence prefabs are `[ComponentInWorld(4)]` and are absent from the server's `_PrefabGuidToEntityMap` (live-checked on both processes). `ServerBeam` therefore only writes the hash; the client resolves it, and an unknown hash silently draws nothing.

Only a sequence prefab that carries `ProjectM.Sequencer.Pull_Entity` reads `SequenceInstance.Target` / `SecondaryTarget` each frame, i.e. tracks entities. `(Sequence)ProfessorCoilBeam` does **not** (it is placed from `Position` / `Rotation` blackboard registers) and must never be passed to `Link` / `Mark`.

| Constant | Hash | Prefab | Shape |
|---|---|---|---|
| `BeamPresets.MarkSequenceRed` (`DefaultMarkSequence`) | `29614748` | `(Sequence)TM_EH_Mines_ExplosiveBarrel_Placeable_T01` | one target; the build-mode **invalid**-placement effect — two red flowing beams that rise from the target and follow it (evidence 135) |
| `BeamPresets.MarkSequenceBlue` | `-450089786` | same asset, **valid**-placement variant | one target, blue |
| `BeamPresets.LinkSequenceCharmChanneling` (`DefaultLinkSequence`) | `-389499105` | `(Sequence)AB_Charm_Channeling` | two entities (`Pull_Entity` + `Pull_BuffTarget`) |
| `BeamPresets.LinkSequenceSubdueChanneling` | `-483640042` | `(Sequence)AB_Subdue_Channeling` | two entities |
| `BeamPresets.LinkSequenceCardinalOrb` | `586432528` | `(Sequence)AB_Cardinal_SummonOrb_Beam_Buff` | the find-chest beam (evidence 80) |
| `BeamPresets.LinkSequenceWarEventPortal` | `1818774975` | `(Sequence)TM_WarEvent_PortalObject_Lesser` | war-event portal beam |

**EV13's default was dead.** `Script_WarEvent_PortalToGateSequenceData.SequenceGuid` reads `-287312447` off a live war-event object, but that hash is in **neither** side's `_PrefabGuidToEntityMap`, so the client could never resolve it and the link drew nothing. `Link` now defaults to `(Sequence)AB_Charm_Channeling` and `Mark` to the red placement sequence.

**Both endpoints must replicate.** `Target` / `SecondaryTarget` travel as `NetworkId`s, so a bare marker entity created by a mod is useless here — use a character or a spawned unit. `Mark` fills only `Target`, leaves `SecondaryTarget` null and sets `PlayOnSelf`, matching the entity-targeted rows the live capture found.

**`Link` is fixed at its spawn pose on this build (observed 2026-09-04).** `IServerBeamHandle.TracksEntitiesNatively` says `Track` is a no-op because the endpoints are entity references, not two synced float3s. That is true and it was read as "the game follows them for us"; it is not. A `.beam link` beam stays where it was spawned while the player walks away.

The sequencer does refresh registers every frame — `Pull_EntitySystem`, `Pull_PositionSystem`, `Pull_RotationSystem`, all `[UpdateInGroup(typeof(BeforeSequencerUpdateGroup), OrderFirst = true)]` (client) — but of the three, the one that supplies a **position** reads `in Translation` off the *sequence instance entity itself* (`Pull_PositionJob.OriginalLambdaBody(DynamicBuffer<BlackboardElement>, in Pull_Position, in Translation)`, client; `Pull_Position` / `Pull_Rotation` are `{ int BlackboardIndex; }`). `Pull_Entity` writes `SequenceInstance`'s Target/SecondaryTarget into an *entity* register; nothing in that pass turns an entity register into a world position. So the pose comes from a `Translation` the server writes once at spawn and never moves.

Two ways out, neither built yet — **this is the next Effects job, not a settled design**:

1. **Move the carrier.** The server rewrites the `NetworkedSequence` entity's `Translation` (and a rotation) each tick from the tracked entities, exactly as the coil path rewrites `Start` / `End`. Cheapest, and it reuses `ServerBeamHandle.Poll`; needs `Translation` to be in the link prefab's snapshot, which is unverified.
2. **Re-point it client-side.** Extend `ClientBeamTick` to walk replicated `NetworkedSequence` beams the way it walks coil beams and drive their `Position` / `Rotation` blackboard registers. Same `SequenceBlackboard` code; needs a marker on the link entity (the coil path uses `BlastTime`, which a `NetworkedSequence` does not carry) and a second client query.

Until one lands, `Link` and `Mark` are a **pin at a pose**, not a leash.

No other family syncs two **positions**: `ServantCoffinEffects`, `BlueprintData.InvalidPlaceSequence` and `CastleArenaFlagBuff_Client.BeamSequenceGUID` are client sequences with no synced endpoints, and the war-event node beams sync a float3 gate position (`Script_WarEvent_PortalToGateSequenceData`). Families that sync two **entities** do exist, and `Link` is one of them; the other, a channeling buff with `SpellTarget.Target` (`AB_Charm_Channeling` 1002852223, `AB_Subdue_Channeling` -942018407; client `BuffSequenceSystem`), is server-creatable but drags the buff's gameplay effects along, so it is documented in research §28.4 and not built.

**Naming a sequence for `Link`.** `BuffSequence`, which holds the vanilla beams' `SequenceGUID`s, is `[ComponentInWorld(4)]` — client world only — so the server cannot read a channeling beam's sequence from its own prefab data. `ServerBeam.Link` takes an explicit `SequenceGUID.GuidHash`, and with 0 falls back to a live read of `Script_WarEvent_PortalToGateSequenceData.SequenceGuid` (`[ComponentInWorld(2, 4)]`) — the portal-to-gate beam. On the test save that is `-287312447`, read off `TM_WarEvent_PortalObject_Lesser`.

**Color and count.** The entity has no color field, and the sequence GameObject comes from `ParticlePool` so it must not be recolored. A differently colored server beam needs its own body (a Content clone of the coil prefab with different `ProfessorCoilBeam_Client` sequence GUIDs) — documented, not implemented. Count is N entities: `Beams(count, spacing)` offsets them sideways. No sequence in the game's metadata bakes several beams as one entity, and it has no global concurrent cap (`ProfessorCoilEvent_Random.BeamCount` is per-event). `Beams` clamps count to 1–8.

**Log lines**

```
Effects: ServerBeam installed (server)
Effects: ServerBeam installed (client tint only)
Effects: server beam spawned prefab=<name> entity=<index:version> len=<m> preview=<s> blast=<marker> seconds=<s>
Effects: server beam tracking <a> -> <b>
Effects: server beam keep-alive phase=Steady previewTime=19.5 every 1s (client NetworkedTimeout 3s)
Effects: server beam phase=Blast previewTime=0
Effects: server beam seconds clamped to 19; ...
Effects: server beam <index:version> is Disabled on the server; it will stop replicating ...
Effects: server beam client tracking entity=<index:version> sequence=<index:version> path=blackboard     (client)
Effects: server beam client tracking entity=<index:version> sequence=<index:version> path=none (no Position/Rotation blackboard input and no LineRendererPositionElement; this sequence cannot be re-pointed — use ServerBeam.Link)     (client)
Effects: server beam client tracking ended entity=<index:version> sequence=<index:version> writes=N     (client)
Effects: server beam client blackboard sequence=<index:version> positionSlot=<n> rotationSlot=<n> game=(x,y,z) ours=(x,y,z)     (client, once per sequence)
Effects: server beam client blackboard overwritten between frames sequence=<index:version> ours=(x,y,z) game=(x,y,z)     (client, once per sequence)
Effects: server beam client tint red skipped (pooled clone is read-only) entity=<index:version>     (client)
Effects: server beam client: <n> coil beams, 0 matched markers     (client, once)
Effects: server beam client cannot read World.SequenceNumber; treating every world check as a change and rebuilding the beam query.     (client, once)
Effects: server beam client query threw: <e>     (client, once per failure episode)
Effects: server beam client query build threw: <e>     (client, once per failure episode)
Effects: server beam disabled handle not tracked (client, skipped module, or missing prefab); nothing to sweep     (once)
Effects: server beam link seconds clamped to 600; ...
Effects: server beam link spawned prefab=NetworkedSequence entity=<index:version> sequence=<guid> <name> <a> -> <b> seconds=<s>
Effects: server beam mark spawned prefab=NetworkedSequence entity=<index:version> sequence=<guid> <name> target=<index:version> playOnSelf=true seconds=<s>
Effects: server beam link failed; returning a disabled handle.
Effects: server beam mark needs a live entity; returning a disabled handle.
Effects: server beam link tracks <a> -> <b> natively; Track is a no-op
Effects: server beam disposed
Effects: swept N server beam handle(s)
```

`path=` is `blackboard`, `buffer`, `blackboard+buffer`, `hybrid` or `none`. A coil beam reads `blackboard`; `none` means nothing could re-point that sequence and the line will stay where it was baked.

Sample commands (all server-side, `.fx beam` remains the client-only placeholder):

| Command | What it does |
|---|---|
| `.beam [seconds] [red\|blue\|coil] [count] [steady\|blast]` | Coil beam, character → nearest `CHAR_Critter_Rat`, else 6 m ahead, tracking both. Defaults 8 s, red, count 1, steady. |
| `.beam link [seconds]` | `ServerBeam.Link` from the caller to the nearest rat; with no rat nearby one is spawned 6 m ahead as the marker and despawned with the beam. |
| `.beam mark [red\|blue] [seconds]` | `ServerBeam.Mark` — the build-mode placement beams on the nearest rat, or on the caller when there is none. |
| `.beam off` | Disposes the beam and any rat spawned for a link. |

**Command ordering (EV15, R27 finding 20).** Every `.beam` shape sweeps the previous beam **before** anything looks for a rat, and the search skips the marker rat that sweep just despawned (`DestroyUtility.Destroy` is one frame deferred, so it is still enumerable). Otherwise a second `.beam link` picked the rat it was about to destroy, linked a dead entity, and still replied as if it had worked. The `.beam link` ordering lives in the game-free `SampleBeam.RunLinkWithMarker(args, ILinkWorld, …)`, so tests drive it; `LiveLinkWorld` is the thin ECS adapter.

The marker rat's lifetime is tied to the handle through `IServerBeamHandle.OnDisposed(Action)`, so it is despawned when the beam ends — by its own deadline, by `.beam off`, by `RemoveAll`, or by unload — instead of standing in the world until the next `.beam off`:

```
Sample: beam link marker despawned with the beam
```

`ServerBeam.Link` / `Mark` clamp `seconds` to `BeamPresets.MaxLinkSeconds` (600) and `.beam`'s parser clamps the token, so `.beam link 999999999` can no longer create a replicated, proximity-ungated entity with a ~31-year `LifeTime` (R27 finding 23). Known limitation, unchanged: `.beam off` calls `ServerBeam.RemoveAll(owner)` plugin-wide, so on a multiplayer test server one player's `.beam` cancels another's.

Sample log lines:

```
Sample: beam spawn entity=<i:v> preset=CastleRed phase=Steady count=1 dest=rat <i:v> enabled=True
Sample: beam link marker rat <i:v> at x,y,z
Sample: beam link entity=<i:v> sequence=-389499105 (Sequence)AB_Charm_Channeling <a> -> <b> seconds=8 dest=rat <i:v> enabled=True
Sample: beam mark entity=<i:v> sequence=29614748 (Sequence)TM_EH_Mines_ExplosiveBarrel_Placeable_T01 (invalid/red) target=<i:v> dest=rat <i:v> seconds=8 enabled=True
Sample: beam off
```

| Kind | Handle | What it does | Host |
|---|---|---|---|
| `Image` | `UiImageHandle` | Vanilla sprite by name onto a mod Image. `Color`, `Size`, `Position`, `SortingOrder`. | Overlay (native). |
| `Particle` | `ParticleHandle` | `Instantiate` a named `ParticleSystem`. `Tint` writes `startColor` and material `_UnlitColor`/`_Color`/`_EmissionColor`. `Play`/`Stop`, world `Position`. `Scale(float)` is uniform and chainable before or after `Play`: it always writes the borrowed root `transform.localScale`, and when `ParticleSystem.main.scalingMode` is not `Hierarchy` (`Local` / `Shape`) it also multiplies `main.startSizeMultiplier` (those modes ignore the transform). The runtime logs the path once per clone. Disposed handles log `Effects: Scale refused; Particle '…' is disposed` and refuse. | RenderTexture. |
| `Volume` | `VolumeHandle` | New HDRP Volume + Vignette (fallback ColorAdjustments). **Camera-wide, not per-panel.** | Host is a no-op raise of priority; no canvas. |
| `Sequence` | `SequenceEffectHandle` | Resolve a buff prefab's sequence, then the spike's pose-only `SequenceUtility.PlaySequence(em, prefab, Start, identity)` (evidence 91: that call produces the hybrid clone and the beam sound; a two-point `PlaySequence(target, prefab, secondary)` lost both). Play waits until both Start and End are assigned (evidence 96: the first setter used to play with the other end at origin); `SetEndpoints(start, end)` plays a **fixed** pose; `Track(startEntity, endEntity, startOffset, endOffset)` and `Track(startEntity, worldEnd)` follow entities each late frame (client `Translation` / `LocalToWorld.Position` through a cached `ComponentType`; a destroyed entity freezes that end and logs once). **How the driver is found:** `ParticleLifetimeSystem.OnUpdate` `ParticlePool.GetInstance`s the clone then `AddComponentObject`s `HybridLineRenderers` / `ParticleSettings` on the spawn entity. Effects reads that managed component (`GetComponentObject<object>(entity, ComponentType)`), not a `(Clone)` name scan. `SequenceInstance` has no ParticleGO. The sequencer **pools** clones (`ParticlePool._PrefabToInstanceQueue` / `ReturnInstance`); a second play reuses the same GO (`driver reused from pool`) after unbinding the previous handle. Dispose restores the clone to `_ParticleParent` and `SetActive(false)` — never `DestroyImmediate` a pooled object the game owns. A handle whose GO the pool recycled drops the bind (`driver recycled by the sequencer`). **How a world beam is drawn:** the default pixels are an **owned** `LineRenderer` under the plugin root (`useWorldSpace=true`, `alignment=View`, `textureMode=Stretch`, `numCapVertices=4`, `localScale=1`, `startWidth=endWidth` in meters, `widthMultiplier=1`, constant width curve, default **0.12 m**, no shadows). Material is additive HDRP Unlit (`Shader.Find("HDRP/Unlit")` first with `_SURFACE_TYPE_TRANSPARENT` / `_BLENDMODE_ADD` / `_ENABLE_FOG_ON_TRANSPARENT` and render queue Transparent; if `shader.isSupported` is false, fallbacks `Sprites/Default`, `Universal Render Pipeline/Unlit`, `Unlit/Color`; last resort is the prefab material copied onto the **owned** line only). Tint default is a soft blue at alpha 0.8. **Never** copy the clone's trail-sprite material/width by default (evidence 119: `S_VFX_GenericTrails_SPRITE` at prefab width 0.53 drew a folded sheet). **Never** assign, instance, or destroy materials on the pooled clone (evidence 125: EV7 `Renderer.material` + `Destroy` left a magenta wedge). `LineWidth(float)`, `LineColor(EffectColor)`, `UsePrefabLineMaterial()` are chainable before or after Play. Cardinal's hybrid `LineRenderer` is not the visible body (evidence 110). Bootstrap still tries `Camera.onPreCull` / `onPreRender`. HDRP never calls `FireOnPreCull`, so after two silent frames the slot runs from FrameTicker `LateUpdate`. Hybrid `SetPosition`+`Refresh` stays as inspect-only. Marker bind is behind `Assets.BindSequencerMarkers` (default **off**). A transform-write failure is logged once per handle and not retried. The clone is `SetActive(true)` once after parenting (evidence 99 left it inactive). The play entity (and markers, when the flag is on) are destroyed on Dispose; the pooled clone is returned to the pool parent. A prefab without `SequenceGUID` / `BuffSequence` returns a disabled handle and one log line. Sequence tint writes the owned line only. The driver poll stops after the first successful bind. | RenderTexture. The sequence clone often appears a frame after `PlaySequence`: Tint and Host before that record the intent; a bounded `MainThread.PostDelayed(0)` poll (default 30 frames) binds from the sequence entity's `HybridLineRenderers` and moves it to the RT layer. |
| `VisualEffect` | disabled | Catalog only this cut (`Find`). Recolor needs per-asset property names (evidence 79). | — |

`HostKind.Overlay` = Screen Space Overlay canvas, sorting 5000. `HostKind.RenderTexture` = one unnamed layer 31→8 **with no renderer on it**, reserved per plugin root (never a named gameplay layer), ortho camera, ARGB32 RT, `RawImage`. The RT is recreated when the screen size changes. Re-`Host` of a live handle disposes the previous host first. Dispose of the host releases RT, camera and canvas **and restores the driver's previous layer**; the world clone stays until the borrow handle is disposed. Host never returns a disabled handle silently: the log line names the reason (missing layer, missing Camera/RawImage, borrowed object not alive). On the server the module installs a no-op runtime: nothing is constructed.

## Example

```csharp
protected override void OnLoad(SampleContext ctx)
{
    if (!ctx.IsClient) return;

    var img = (UiImageHandle)ctx.Effects.Borrow(AssetKind.Image, "PassiveFrame_Normal");
    img.Color = new EffectColor(1f, 0.84f, 0f, 1f);
    img.Size = new EffectVec2(192f, 192f);
    img.SortingOrder = 5000;
    ctx.Effects.Host(img, HostKind.Overlay);

    var embers = (ParticleHandle)ctx.Effects.Borrow(AssetKind.Particle, "Embers");
    embers.Tint = new EffectColor(1f, 0.84f, 0f);
    embers.Scale(3f).Play();
    ctx.Effects.Host(embers, HostKind.RenderTexture);

    var beam = ctx.Effects.Beam(BeamStyle.GameRed); // Thin / GameRed / GameFeed / GameBlue
    beam.LineColor(new EffectColor(0.25f, 0.55f, 1f, 0.8f)) // hue; HDR intensity of a copied material is kept
        .Track(character, rat, startOffset: new float3(0f, 1f, 0f), endOffset: new float3(0f, 0.5f, 0f));
}
```

Live recipe in the Sample plugin (chat, server → client event `SmFx`): `.fx image` / `.fx particle` / `.fx particle menu` / `.fx beam [thin|red|blue]` / `.fx beam menu` / `.fx vignette` / `.fx off`. Each step logs `Sample: fx …` on the client; timers use `MainThread.PostDelayed`. `.fx beam` and `.fx particle` play in the world (no Host). `.fx beam` defaults to **red** when `Assets.PreferredBeamStyle()` found a castle-family sequence at runtime, otherwise **thin**. Extra tokens ride in SampleCommands' `where` string: `.fx beam red` / `.fx beam blue` / `.fx beam thin`. `.fx particle` defaults to Flame at 2× plus Embers at 3× (the visible combination from evidence 95; unscaled embers are faint). Extra tokens: `.fx particle embers` picks one at 3×, `.fx particle "embers 4"` (or `particle embers 4` after join) picks one at 4×. `.fx off` still disposes every handle under the Sample's `progenitor-effects-*` root. `.fx beam menu` and `.fx particle menu` host into the overlay only when `Assets.Host` can size and place the RawImage; this cut has no such option, so they log `Sample: fx … menu needs a sized host (module follow-up)` and skip Host (beam still plays in the world; particle menu does nothing else). See the Effects section of [modules.md](modules.md).

## Beam presets

`Assets.Beam(BeamStyle)` / `ctx.Effects.Beam(style)` borrows a sequence and applies the look. `LineColor` on a copied game material keeps that material's HDR intensity (`EffectColor.TintPreserveIntensity`). `Glow(width, alpha)` adds or removes the halo line. Dispose destroys the owned core, the glow, and **their** materials — never the pooled clone.

| Preset | Sequence borrowed (sound + particles) | Body | Glow |
|---|---|---|---|
| `Thin` | Cardinal buff `AB_Cardinal_SummonOrb_Beam_Buff` / `PrefabGUID(-2014639169)` (sequence prefab guid 586432528, evidence 80) | Owned additive `HDRP/Unlit` line, 0.12 m. No game-material copy. | Off unless `Glow` is set. |
| `GameRed` | First live GUID from `ServantCoffinEffects.ActiveServantGuid`, else `WakeUpReadyGuid` / `ActiveConversionGuid`, else `BlueprintData.InvalidPlaceSequence`, else `CastleArenaFlagBuff_Client.BeamSequenceGUID`; when none of those entities exist, the Cardinal sequence. | **A copy of `Beam_Dracula_BloodSoul_Channeling_01`** (else `Beam_Charm_Channeling_01`, `Beam_Subdue_Channeling_01`, `Part_WarEvent_ShadowRift_Beam`) — the game's `Shader Graphs/Effect` material draws the line. Falls back to the owned line with the clone's material copied onto it. | None when a prefab body is borrowed (it already glows); otherwise 0.36 m / alpha 0.35 unless the copied material carries emission. |
| `GameFeed` | Same rule; usually the Cardinal sequence. | A copy of `Beam_Feed_Channeling_01` (`Shader Graphs/Liquid`, the blood-feed beam), else `Part_WarEvent_SubToMainNode_Beam`. | Same. |
| `GameBlue` | Cardinal, same as Thin. Cardinal's body is the particle/trail graph, not a line (evidence 119, 110), so there is nothing to borrow. | Soft-blue owned unlit core. | Default halo, same knobs. |

### Borrowed game lines (EV12)

Evidence 130 cataloged every loaded client `LineRenderer` and found which vanilla two-point beams have a **drawable line body** rather than a trail sprite: the channeling family (Dracula blood-soul, feed, charm, subdue) on `Shader Graphs/Effect` / `Liquid`, plus the war-event node lines. `GameRed` / `GameFeed` `Instantiate` one of those prefabs, parent the copy under the plugin root, remove its `HybridLineRendererComponent` (its `Awake` caches the LineRenderer and `CalculateThrowArc` rewrites `Positions`, and on a real sequence entity `ParticleLifetimeSystem.UpdateLineRendererPositionElement_Job` feeds it), then drive that LineRenderer's two positions every late frame. The copy is **ours**: it may be tinted, resized and destroyed. Vertex colors always follow the preset tint; the copied **material** is only repainted when the plugin sets `Tint` / `LineColor` itself, so borrowing keeps the vanilla look by default. Dispose destroys the copy and the one material copied for it; the source prefab is never touched. Prefab names are asset names and are not in the game's metadata — the catalog lives in `BeamCatalog.GameRedLineBeams` / `GameFeedLineBeams`.

A style no longer degrades to `Thin` just because its sequence GUIDs are missing: if a line prefab resolves, the Cardinal sequence supplies the audio and the borrowed line supplies the pixels, and `Style` stays `GameRed` / `GameFeed`. `Assets.PreferredBeamStyle()` returns `GameRed` when **either** resolves.

### The pooled clone is read-only

The GameObject the sequencer hands us comes out of `ParticlePool.GetInstance` and goes back in on `ReturnInstance`. **No** write of any kind lands on it: no `Renderer.material` (the getter instances a per-renderer copy), no `ParticleSystem.main.startColor`, no `BeamEmitter.StartColor`, no `Destroy` of an instanced material. Anything else leaks color — or a destroyed material — into every later user of that clone, ours or the game's (evidence 125: the magenta wedge). `CloneWritePolicy.AllowsMaterialWrites(kind, weInstantiatedIt)` is the rule and every tint call site passes ownership explicitly; a refused tint logs once:

```
Effects: tint skipped on 'Beam_Cardinal_Orb_Guardian_01(Clone)': the sequencer clone is pooled and read-only; tint stays on objects we instantiated (R26 finding 3)
```

This also covers the **server beam** client tint: `ServerBeam`'s `CastleRed` / `Blue` markers no longer recolor the replicated sequence GO, because that GO is pooled. The coil keeps its baked look; a recolored server beam needs a body of its own (path 3), not a write into the pool.

### Which clone the fallback finder may claim

The authoritative bind is the sequence entity's managed `HybridLineRenderers` (`SequenceDriverLookup.FromSequenceEntity`). While that is not attached yet, the fallback may claim only: a clone a Progenitor handle already holds (the sequencer reused it from the pool — EV6), or a clone that appeared **after** our play frame and is still parked (evidence 99: a fresh clone lands inactive). A clone that was already on screen when we played and belongs to nobody is a **vanilla beam** and is never claimed — taking it reparented, activated and then deactivated a live game effect. When nothing is claimable the handle stays unbound (the owned or borrowed line is the visible body anyway) and logs once:

```
Effects: no claimable sequence driver (every hybrid clone was already on screen and unbound); staying unbound
```

### Recycle stops the clone writes, not the owned line

When the sequencer takes the pooled clone back, `SequenceEffectHandle.Recycled` turns true and the **clone-related** writes stop: the borrowed game-line copy is disabled and the driver reference is dropped. The owned `LineRenderer` is our own GameObject with our own material under our own root and nothing about it depends on the clone, so it keeps drawing from the handle's endpoints (and keeps tracking, because the handle stays watched) until the handle's own lifetime ends.

That is EV15 correcting EV12 (R27 finding 11). EV12 hid the owned line and unwatched the handle, with no rebind path — and the sequencer recycles at the natural end of the *game* sequence, typically a second or two, not at the end of the plugin's beam. A `Borrow(Sequence) + Track(character, unit)` beam asked to live for 30 s therefore vanished after the sequence's own length, and only `Dispose` ever ran again.

Logged once per handle:

```
Effects: sequence driver recycled by the sequencer; clone writes stopped, owned line keeps drawing
```

**A recycled handle never re-claims from the play-frame snapshot (EV16, R28 finding 13).** Surviving the recycle is not enough on its own: `TryBindSequenceDriver` fell through from `DropRecycledDriver()` to `FindFallback(_seenDrivers)`, and that snapshot is taken once at play and was never refreshed — so the next `Play()` / `Tint` / `SetEndpoints` saw the clone the sequencer had just parked as unknown-and-unbound and `DriverClaimPolicy.Decide` rated it a fresh `NewClone`. It was reparented under `progenitor-effects-<id>` and activated, `ParticlePool.GetInstance` then handed the same object to a vanilla effect, and `DestroyRoot` took it — R26 finding 7's failure, re-reachable. The recycle now drops the snapshot, and `DriverClaimPolicy.MayUseFallback(recycled, haveSnapshot)` refuses the fallback for a recycled handle. The authoritative bind (`SequenceDriverLookup.FromSequenceEntity`) is unaffected: that clone is ours by construction. R26 #7's rule itself is unchanged — an active clone that is not ours is still never claimed.

### Pool bookkeeping uses instance ids

Il2CppInterop can front one native object with two managed wrappers, so `ReferenceEquals` is not identity. The recorded pool parent and the recycle check compare `GetInstanceID()`. Our own root can never be recorded as the pool parent — `RestorePoolParent` would park the game's pooled clone under it and plugin unload's `DestroyRoot` would destroy it:

```
Effects: pooled clone was already under our root; taking ParticleLifetimeSystem._ParticleParent as the pool parent
```

LateFrame also writes `LineRendererPositionElement` start/end on the sequence entity (consumed by `ParticleLifetimeSystem.UpdateLineRendererPositionElement_Job`) and logs which path draws:

```
Effects: sequence beam drawn by owned LineRenderer with game material
Effects: sequence beam drawn by game line via LineRendererPositionElement
Effects: sequence beam drawn by a borrowed game line prefab
Effects: sequence beam body = loaded beam prefab 'Beam_Dracula_BloodSoul_Channeling_01' Shader Graphs/Effect width 0.24 (evidence 130; LineRenderer body, HybridLineRendererComponent)
Effects: Beam(GameRed) found no loaded game line prefab; keeping the owned line
Effects: sequence glow line width=0.36 alpha=0.35
Effects: sequence glow comes from the copied material
Effects: sequence clone LineRenderer shader 'Shader Graphs/S_VFX_GenericTrails_SPRITE' is a trail sprite; keeping owned unlit (evidence 119)
Effects: Beam(GameRed) borrow <hash> source=ServantCoffinEffects.ActiveServantGuid (ServantCoffinstationSequenceSystem)
Effects: Beam(GameRed) not found at runtime; using Thin
```

Unload disposes both handles (canvas, RT, clones). No leftover `progenitor-effects-*` GOs.

## Expected log lines

The layout, bounds and stride-probe lines are **not** repeated here: they live in one place, [Layout and bounds log lines](#layout-log-lines), so an operator grepping this file for a probe message cannot find a stale copy of it (R30 finding 23).

```
Effects: installed (client)
Effects: installed (server no-op)
Effects: ServerBeam installed (server)
Effects: ServerBeam installed (client tint only)
Effects: server beam spawned prefab=ProfessorCoilBeam entity=12:1 len=6
Effects: server beam tracking 10:1 -> 20:1
Effects: server beam disposed
Effects: server beam client tracking entity=12:1 sequence=88:3 path=blackboard
Effects: server beam client tracking ended entity=12:1 sequence=88:3 writes=240
Effects: server beam client tint red skipped (pooled clone is read-only) entity=12:1
Effects: server beam client: 3 coil beams, 0 matched markers
Reload: swept 2 effect(s) / 1 panel(s) / 1 server beam(s)
Effects: Borrow(Image, 'PassiveFrame_Normal') is client-only; returning a disabled handle.
Effects: Host(Overlay) is client-only; returning a disabled handle.
Effects: Find(Particle) is client-only; returning no names.
Effects: borrowed Image 'PassiveFrame_Normal' under progenitor-effects-example.sample
Effects: hosted Overlay for Image 'PassiveFrame_Normal'
Effects: disposed Image 'PassiveFrame_Normal'
Effects: no Image named 'Missing' on this client; returning a disabled handle.
Effects: volume is camera-wide (not a per-panel overlay)
Effects: sequence play pose start=(…) end=(…) seq=…
Effects: sequence driver 'Beam_Cardinal_Orb_Guardian_01(Clone)'
Effects: sequence driver 'Beam_Cardinal_Orb_Guardian_01(Clone)' components=Transform,LineRenderer,HybridLineRendererComponent
Effects: RT host reserved layer 31 (unnamed, no renderer) for progenitor-effects-<pluginId>.
Effects: RT host moved driver 'Beam_Cardinal_Orb_Guardian_01(Clone)' to layer 31 (restore on dispose).
Effects: sequence driver 'Beam_Cardinal_Orb_Guardian_01(Clone)' landed after 1 frame(s)
Effects: sequence hybrid Positions before=[…] after=[…] stuck=True sequencerMatched=False
Effects: sequence beam drawn by owned LineRenderer (sequencer line not drawable: HybridLineRenderer Refresh copies Positions into LineRenderer.SetPosition but Cardinal's visible body is the ParticleSystem/trail graph)
Effects: sequence owned line material=HDRP/Unlit width=0.12
Effects: sequence owned line readback startWidth=0.12 widthMultiplier=1 lossyScale=(1,1,1) shader=HDRP/Unlit isSupported=True renderQueue=3000
Effects: sequence endpoint start lost; frozen
Effects: sequence endpoint end lost; frozen
Effects: Shader.Find('HDRP/Unlit') is null
Effects: sequence owned line shader 'HDRP/Unlit' isSupported=False; trying fallback
Effects: sequence owned line using fallback shader 'Sprites/Default'
Effects: sequence owned line no unlit shader; falling back to prefab material
Effects: sequence driver reused from pool
Effects: sequence driver recycled by the sequencer; clone writes stopped, owned line keeps drawing
Effects: no claimable sequence driver (every hybrid clone was already on screen and unbound); staying unbound
Effects: pooled clone was already under our root; taking ParticleLifetimeSystem._ParticleParent as the pool parent
Effects: tint skipped on '<clone>': the sequencer clone is pooled and read-only; tint stays on objects we instantiated (R26 finding 3)
Effects: sequence late writes=N, sequencer overwrote before us N frames
Effects: Sequence 'SomeOtherBuff' has no BuffSequence buffer; returning a disabled handle.
Effects: uninstalled leftover=0
Diagnostics.SkipCoreModules: skipped module Effects
Bootstrap: LateFrame hooked Camera.onPreCull
Bootstrap: LateFrame hooked Camera.onPreRender
Bootstrap: LateFrame falling back to FrameTicker.LateUpdate
LateFrame: Camera/SRP callback never fired; falling back to FrameTicker.LateUpdate
LateFrame: first invoke source=FrameTicker.LateUpdate frame=N slot=filled
```

**Per-frame cost (steady state).** After the first late write of a handle the LateFrame slot allocates nothing: no string formatting (inspect / readback logs are once per handle), no `Vector3[]` / `List<Vector3>` wrappers, no LINQ, no closures. Counters are plain ints. Tracked endpoints are `Exists` + `HasComponent` + `GetComponentData` through cached `ComponentType`s (no try/catch per entity: a missing entity freezes that end and logs once). The owned line is two cached `LineRenderer.SetPosition(int, Vector3)` calls. Hybrid `SetPosition`+`Refresh` stays inspect-only. Default material/width are assigned when the owned line is created (`HDRP/Unlit`, 0.12 m, `widthMultiplier=1`); `UsePrefabLineMaterial()` copies `clone.sharedMaterial` onto the owned line only. Dispose / `RemoveAll` destroys the owned GameObject and **its** material — never a material assigned to the pooled clone.

Sample `.fx beam` (client, world only — no Host). Live play **tracks** the local character to the nearest `Health` unit within 30 m, else the character to a point 6 m ahead (`Track`; the line follows as they move). Tests / a missing pose still use `SetEndpoints`. The hybrid clone is parented under the plugin root while the handle lives and restored to `ParticleLifetimeSystem._ParticleParent` on dispose. Pixels come from the owned `LineRenderer` (thin unlit line, not the trail sprite); Bootstrap LateFrame still writes hybrid Positions for inspect:

```
Sample: fx beam borrow -2014639169
Effects: borrowed Sequence '-2014639169' under progenitor-effects-progenitor.sample
Effects: sequence play pose start=(x,y,z) end=(x,y,z) seq=…
Effects: sequence driver GO not found this frame (sequence entity has no HybridLineRenderers yet; Host will poll again).
Sample: fx beam start=(x,y,z) end=(x,y,z)
Sample: fx beam tint blue
Sample: fx beam track character -> unit N:V
Sample: fx beam dispose in 5s
Effects: sequence late-frame watch seq=… driver=(unbound)
Effects: sequence beam drawn by owned LineRenderer (sequencer line not drawable: HybridLineRenderer Refresh copies Positions into LineRenderer.SetPosition but Cardinal's visible body is the ParticleSystem/trail graph)
Effects: sequence driver 'Beam_Cardinal_Orb_Guardian_01(Clone)' landed after 1 frame(s)
Effects: sequence driver 'Beam_Cardinal_Orb_Guardian_01(Clone)' components=Transform,LineRenderer,HybridLineRendererComponent,…
Effects: sequence hybrid Positions before=[(0,0,0),(driver)] after=[start,end] stuck=True sequencerMatched=False
Effects: sequence late-frame first write game=[(0,0,0),(driver)] ours=[start,end] frame=N
Effects: sequence owned line material=HDRP/Unlit width=0.12
Effects: sequence owned line readback startWidth=0.12 widthMultiplier=1 lossyScale=(1,1,1) shader=HDRP/Unlit isSupported=True renderQueue=3000
```

A second play in the same process (same or new handle) binds the pooled clone:

```
Effects: sequence driver reused from pool
Effects: sequence driver 'Beam_Cardinal_Orb_Guardian_01(Clone)' landed after 1 frame(s)
```

On dispose / `RemoveAll` (exactly once per handle):

```
Effects: sequence late writes=N, sequencer overwrote before us N frames lastFrame=F
```

A spike exec that never disposes must not poison the next play: the next bind reuses the pooled GO (unbinds the leftover handle first). If the leftover handle's late write sees the GO deactivated / reparented back to the pool, it logs `Effects: sequence driver recycled by the sequencer; clone writes stopped, owned line keeps drawing`, disables the borrowed clone line and drops the bind; the owned line keeps drawing.

`before=` is the **game's** write at the start of the late slot, not our previous frame. `sequencerMatched=False` plus `drawn by late-frame SetPosition` is the Cardinal path (evidence 101). If a graph ever honors the marker, `before=` is already `[start,end]`, overwrite count stays 0, and the verdict is `Effects: sequence beam sequencer honoured the marker`. Marker / SequenceInstance bind stays behind `Assets.BindSequencerMarkers` (default off) until that happens.

If the clone landed inactive (evidence 99):

```
Effects: sequence driver 'Beam_Cardinal_Orb_Guardian_01(Clone)' SetActive(true) (was activeSelf=False inHierarchy=False)
```

There must be **no** per-frame `Effects: sequence LocalToWorld write threw` and **no** `Effects: sequence onBeforeRender hook threw: Method unstripping failed`. Bootstrap logs `Camera.onPreCull` (or `onPreRender`) at Load; because HDRP never fires that callback, two frames later it logs `LateFrame: Camera/SRP callback never fired; falling back to FrameTicker.LateUpdate` and a once-per-generation `LateFrame: first invoke source=FrameTicker.LateUpdate frame=N slot=filled`.

The play-pose line must name both ends (never `end=(0,0,0)` from a first-setter play). The owned `LineRenderer` is the drawable body (`useWorldSpace=true`, `alignment=View`, `textureMode=Stretch`, `startWidth=endWidth=0.12`, `widthMultiplier=1`, additive `HDRP/Unlit` with the transparent keywords, positions = start/end, updated each late frame when `Track` is used). The once-per-handle readback names the **owned** line (`startWidth`, `widthMultiplier`, `lossyScale`, `shader`, `isSupported`, `renderQueue`) — never the clone. Hybrid `Positions` on the inspect spike may still read `[start, end]` after the late write; that copy is **not** what the GPU shows for Cardinal. The pooled clone may sit under the plugin root while the handle lives (`activeSelf=True`); on dispose it is restored to `_ParticleParent` (do not destroy it, and do not destroy its materials).

If the clone is a frame late, the play line is followed by `Effects: sequence driver GO not found this frame …`, then the landed / components / first late-write inspect lines.

Sample `.fx beam menu` (client): the same world lines, then `Sample: fx beam menu needs a sized host (module follow-up)` instead of a Host call, then `Sample: fx beam dispose in 5s`.

Sample `.fx particle` (client, world clones near the player — no Host). Defaults are Flame 2× + Embers 3×:

```
Sample: fx particle borrow Flame
Effects: borrowed Particle 'Flame' under progenitor-effects-progenitor.sample
Effects: particle 'Flame' scale 2 via transform (Hierarchy)
Sample: fx particle scale 2
Sample: fx particle tint gold
Sample: fx particle world (x,y,z)
Sample: fx particle dispose in 5s
Sample: fx particle borrow Embers
Effects: borrowed Particle 'Embers' under progenitor-effects-progenitor.sample
Effects: particle 'Embers' scale 3 via transform (Hierarchy)
Sample: fx particle scale 3
Sample: fx particle tint gold
Sample: fx particle world (x,y,z)
Sample: fx particle dispose in 5s
```

The `via transform (Hierarchy)` line is the scaling-mode path: Hierarchy only needs `localScale`. If `ParticleSystem.scalingMode` (interop convenience for `main.scalingMode`) is Local or Shape the same line reads `via transform + startSize (Local)` (or Shape) because those modes ignore the transform and the handle also writes `ParticleSystem.startSize` (the `main.startSizeMultiplier` wrapper). Logged once per clone. The recovered `MainModule` C# wrapper does not expose those two properties; the ParticleSystem wrappers do.

Sample `.fx particle embers 4` / `.fx particle "embers 4"` (one named clone):

```
Sample: fx particle borrow embers
Effects: borrowed Particle 'embers' under progenitor-effects-progenitor.sample
Effects: particle 'embers' scale 4 via transform (Hierarchy)
Sample: fx particle scale 4
Sample: fx particle tint gold
Sample: fx particle world (x,y,z)
Sample: fx particle dispose in 5s
```

Sample `.fx particle menu` (client): `Sample: fx particle menu needs a sized host (module follow-up)` and no borrow/host.

Late-driver case (hybrid GO appears after `PlaySequence`, evidence from the two-endpoint `.fx beam`):

```
Effects: sequence driver GO not found this frame (sequence entity has no HybridLineRenderers yet; Host will poll again).
Effects: RT host has no sequence driver GO yet; camera looks along the beam (clone is not parented under our root).
Sample: fx beam tint blue
Sample: fx beam host RenderTexture
Effects: hosted RenderTexture for Sequence '-2014639169'
Effects: sequence driver 'Beam_Cardinal_Orb_Guardian_01(Clone)' landed after 1 frame(s)
Effects: RT host moved driver 'Beam_Cardinal_Orb_Guardian_01(Clone)' to layer 31 (restore on dispose).
```

`.fx vignette` logs `intensity=0.75 smoothness=0.6` and that the game's own post effects can dominate.

## Particle activation and Charm catalog correction (2026-09-06)

BorrowParticle activates its owned clone before playback. A source taken from the game's particle pool can be inactive; ParticleSystem.Play alone does not activate that GameObject. The live probe reproduced an enabled handle with seven inactive systems and zero particles, then visible emission after activating only the owned copy.

Charm's caster sequence -389499105 contains only mat_Transparent_Weapon01. The target debuff AB_Charm_Channeling_Target_Debuff (-1760277810) uses sequence -541113174, which references both Charm beams and the loop/crown particles. Replaying that sequence produced visible Charm particles; a faithful beam between moving endpoints remains unverified. Do not count the existing Link default as visual acceptance evidence.

