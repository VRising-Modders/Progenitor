#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Sequencer;
using Progenitor.Ecs;
using Stunlock.Sequencer;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Progenitor.Effects;

/// <summary>
/// Authoritative sequence-entity → GameObject bind. <c>ParticleLifetimeSystem.OnUpdate</c>
/// (in the game's native code) reads
/// <c>InstantiateParticleSystem</c> on the spawn entity, <c>ParticlePool.GetInstance</c>,
/// then <c>EntityManager.AddComponentObject</c> of the clone's
/// <c>HybridLineRenderers</c> / <c>HybridVisualEffects</c> / <c>ParticleSettings</c>.
/// <c>SequenceInstance</c> has no ParticleGO field (the game's metadata lists Target /
/// SecondaryTarget only). Name-scanning for a new <c>(Clone)</c> misses pooled reuse
/// (<c>ParticlePool._PrefabToInstanceQueue</c> / <c>ReturnInstance</c>).
/// </summary>
internal static class SequenceDriverLookup
{
    private static readonly Dictionary<int, UnityBorrowedObject> Bound = new();
    private static bool _loggedNothingClaimable;

    internal static void Uninstall()
    {
        Bound.Clear();
        _loggedNothingClaimable = false;
        SequenceBlackboard.Clear();
    }

    internal static GameObject? FromSequenceEntity(World? world, Entity sequence)
    {
        if (world == null || !world.IsCreated || sequence == Entity.Null)
            return null;

        try
        {
            var em = world.EntityManager;
            if (!em.Exists(sequence))
                return null;

            var go = TryManagedGo<HybridLineRenderers>(em, sequence)
                ?? TryManagedGo<ParticleSettings>(em, sequence)
                ?? TryManagedGo<HybridVisualEffects>(em, sequence);
            if (IsAlive(go))
                return go;

            // Still waiting for ParticleLifetimeSystem to GetInstance + AddComponentObject,
            // or the GO lives on a child particle entity. Caller falls back to FindFallback
            // (newest unbound hybrid, else pooled reuse). Do not walk every pool child —
            // that would steal a vanilla beam.
            return null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence driver identity resolve threw: " + e.Message);
            return null;
        }
    }

    internal static Transform? TryGetParticleParent(World? world)
    {
        if (world == null || !world.IsCreated) return null;
        try
        {
            var sys = world.GetExistingSystemManaged<ParticleLifetimeSystem>();
            if (sys == null) return null;
            var go = sys._ParticleParent;
            if (!IsAlive(go) || go == null) return null;
            return go.transform;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Claim <paramref name="go"/> for <paramref name="owner"/>. If another handle already
    /// holds this pooled instance, that handle is unbound first (evidence 108/110: the
    /// sequencer reactivates the same clone).
    /// </summary>
    internal static bool Claim(GameObject go, UnityBorrowedObject owner)
    {
        if (!TryInstanceId(go, out int id)) return false;
        bool reused = false;
        if (Bound.TryGetValue(id, out var prev) && !ReferenceEquals(prev, owner))
        {
            try { prev.ReleaseDriverForReuse(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: previous driver unbind threw: " + e.Message);
            }

            reused = true;
        }

        Bound[id] = owner;
        if (reused)
            Plugin.Logger?.LogInfo("Effects: sequence driver reused from pool");
        return reused;
    }

    internal static void Release(GameObject? go, UnityBorrowedObject owner)
    {
        if (!TryInstanceId(go, out int id)) return;
        if (Bound.TryGetValue(id, out var cur) && ReferenceEquals(cur, owner))
            Bound.Remove(id);
    }

    /// <summary>
    /// Fallback when the sequence entity has no managed hybrid yet. Only two clones are
    /// claimable (<see cref="DriverClaimPolicy"/>): one that appeared after our play frame
    /// and is still parked (evidence 99: a fresh clone lands inactive), or one a Progenitor
    /// handle already holds because the sequencer reused it (EV6). A clone that was already
    /// on screen when we played and belongs to nobody is a <b>vanilla beam</b> and is never
    /// claimed — R26 finding 7: claiming it reparented, activated and then deactivated a
    /// live game effect mid-play. Null means "stay unbound"; the poll retries and the owned
    /// line keeps drawing.
    /// </summary>
    internal static GameObject? FindFallback(HashSet<int>? before)
    {
        GameObject? best = null;
        int bestId = int.MinValue;
        var bestClaim = DriverClaim.None;
        int refused = 0;

        void consider(GameObject? go)
        {
            if (!TryInstanceId(go, out int id)) return;
            bool seen = before != null && before.Contains(id);
            bool bound = Bound.ContainsKey(id);
            bool active = false;
            try { active = go != null && go.activeInHierarchy; }
            catch { active = true; }

            var claim = DriverClaimPolicy.Decide(seen, bound, active);
            if (claim == DriverClaim.None)
            {
                refused++;
                return;
            }

            if (!DriverClaimPolicy.Prefer(claim, id, bestClaim, bestId)) return;
            bestClaim = claim;
            bestId = id;
            best = go;
        }

        ForEachHybrid(consider);
        if (best == null && refused > 0 && !_loggedNothingClaimable)
        {
            _loggedNothingClaimable = true;
            Plugin.Logger?.LogInfo(DriverClaimPolicy.NothingClaimable);
        }

        return best;
    }

    private static void ForEachHybrid(Action<GameObject?> consider)
    {
        try
        {
            var type = Il2CppType.Of<HybridLineRendererComponent>();
            if (type == null) return;
            var arr = UnityEngine.Object.FindObjectsOfType(type, true);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                HybridLineRendererComponent? h = null;
                try { h = arr[i] == null ? null : arr[i].TryCast<HybridLineRendererComponent>(); }
                catch { continue; }
                if (h == null) continue;
                try { consider(h.gameObject); }
                catch { /* */ }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence hybrid scan threw: " + e.Message);
        }
    }

    private static GameObject? TryManagedGo<T>(EntityManager em, Entity entity)
        where T : Component
    {
        try
        {
            var ct = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(entity, ct))
                return null;
            // The game's metadata instantiates GetComponentObject<object>(entity, ComponentType),
            // not GetComponentObject<HybridLineRenderers>. The object+type overload is the
            // IL2CPP instantiation that exists (interop trap 1).
            var boxed = em.GetComponentObject<Il2CppSystem.Object>(entity, ct);
            if (boxed == null) return null;
            var mb = boxed.TryCast<T>();
            if (mb == null) return null;
            return mb.gameObject;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Effects: sequence GetComponentObject<" + typeof(T).Name + "> threw: " + e.Message);
            return null;
        }
    }

    internal static bool TryInstanceId(GameObject? go, out int id)
    {
        id = 0;
        if (go == null) return false;
        try
        {
            id = go.GetInstanceID();
            return id != 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsAlive(UnityEngine.Object? obj)
    {
        try { return obj != null && obj.Pointer != IntPtr.Zero; }
        catch { return false; }
    }
}

/// <summary>
/// Read and write a live sequence instance's blackboard registers — the channel the game
/// itself uses to place a sequence after <c>PlaySequence</c>.
///
/// <para><b>Why this exists.</b> The coil beam's graphic is not a two-point line the client
/// draws from <c>State_Shared.Start/End</c>. Native
/// <c>ProfessorCoilSystem_Client.OnUpdate</c> on the client
/// does, per dequeued spawn: destroy the previous <c>SequenceState.Id</c>, pick one of four
/// <b>fixed-length</b> sequence prefabs by <c>|End - Start|</c>
/// (native <c>GetSequence</c>: &lt;12 m, &lt;20 m, &lt;23.5 m, else bucket 4),
/// <c>SequenceUtility.PlaySequence</c>, then
/// <c>SetBlackboardInput(em, "Position", Start)</c> and
/// <c>SetBlackboardInput(em, "Rotation", quaternion.LookRotation(End - Start, up))</c>
/// (the game's string literals <c>"Position"</c> / <c>"Rotation"</c>).
/// Nothing else moves the beam. So re-pointing a running coil beam means writing those two
/// registers again, exactly the way the game wrote them once.</para>
///
/// <para><b>Layout, all metadata/stub-backed.</b> <c>Stunlock.Sequencer.SequenceInput</c>
/// holds <c>BlobAssetReference&lt;SequenceInputBlob&gt;</c>; the blob root is
/// <c>BlobArray&lt;SequenceInputElement&gt; Inputs</c>, i.e.
/// <c>{ int m_OffsetPtr; int m_Length; }</c> with the items at
/// <c>&amp;m_OffsetPtr + m_OffsetPtr</c>. <c>SequenceInputElement</c> is
/// <c>{ FixedString64Bytes Name @0; int Index @0x40; BlackboardRegisterType Type @0x44;
/// bool IsInputParameter @0x48 }</c> (game metadata, offsets confirmed against the
/// interop stub). Native
/// <c>SequenceStateExtensions.SetBlackboardInput&lt;float3&gt;</c> on the client
/// walks that array, matches the name with
/// <c>UTF8ArrayUnsafeUtility.StrCmp</c>, and writes the value at <c>Index</c> as a raw
/// <b>byte offset</b> into the entity's <c>DynamicBuffer&lt;BlackboardElement&gt;</c>
/// (one byte per element). This class does the same three steps with
/// pointers, so no generic interop instantiation is needed (interop trap 2).</para>
/// </summary>
internal static unsafe class SequenceBlackboard
{
    /// <summary>Blackboard input the coil client system writes with the beam's start point.</summary>
    internal const string PositionInput = SequenceBlackboardLayout.PositionInput;

    /// <summary>Blackboard input the coil client system writes with the look rotation.</summary>
    internal const string RotationInput = SequenceBlackboardLayout.RotationInput;

    /// <summary><c>BlackboardRegisterType.Float3</c>.</summary>
    internal const int TypeFloat3 = SequenceBlackboardLayout.TypeFloat3;

    /// <summary><c>BlackboardRegisterType.Quaternion</c>.</summary>
    internal const int TypeQuaternion = SequenceBlackboardLayout.TypeQuaternion;

    /// <summary><c>SequenceInputElement.Index</c> field offset.</summary>
    internal const int InputIndexOffset = SequenceBlackboardLayout.InputIndexOffset;

    /// <summary><c>SequenceInputElement.Type</c> field offset.</summary>
    internal const int InputTypeOffset = SequenceBlackboardLayout.InputTypeOffset;

    /// <summary>
    /// <c>sizeof(SequenceInputElement)</c>: <c>FixedString64Bytes</c> (64) + int + int + bool,
    /// aligned to 4. Cross-checked against the interop struct at first use; a mismatch
    /// <b>disables</b> the re-point (R27 finding 5.1), it does not merely log.
    /// </summary>
    internal const int InputElementStride = SequenceBlackboardLayout.InputElementStride;

    /// <summary>Sanity bound on the blob's input count; a real sequence has a handful.</summary>
    internal const int MaxInputs = SequenceBlackboardLayout.MaxInputs;

    /// <summary>Bound on the per-sequence index cache.</summary>
    internal const int CacheCap = SequenceBlackboardLayout.CacheCap;

    /// <summary>Resolved register byte offsets for one sequence prefab archetype.</summary>
    internal readonly struct Slots
    {
        internal Slots(int position, int rotation)
        {
            Position = position;
            Rotation = rotation;
        }

        /// <summary>Byte offset of the <c>Position</c> float3 register, or -1.</summary>
        internal int Position { get; }

        /// <summary>Byte offset of the <c>Rotation</c> quaternion register, or -1.</summary>
        internal int Rotation { get; }

        internal bool Any => Position >= 0 || Rotation >= 0;
    }

    private static readonly Dictionary<long, Slots> SlotCache = new();

    /// <summary>
    /// Sequences whose walk has not resolved yet, and how many times it has been tried.
    /// EV17: a failed <c>Resolve</c> used to be cached forever, so a sequence read on the frame
    /// it spawned — before the client system filled its <c>SequenceInput</c> blob — was
    /// permanently un-re-pointable. Bounded the same way as <see cref="SlotCache"/>.
    /// </summary>
    private static readonly Dictionary<long, int> ResolveMisses = new();

    /// <summary>How many frames a sequence may fail to resolve before the miss is cached.</summary>
    internal const int ResolveRetries = 30;

    /// <summary>
    /// <b>R31 finding 26 / R30 open question 7.</b> Every gate decision this file used to make
    /// inline now lives in <see cref="SequenceWalkWiring"/>, a game-free class the test project
    /// compiles, so a fake runtime drives the same wiring the game does. What is left here is
    /// only what needs <c>Entity</c>, <c>EntityManager</c> and pointers.
    /// </summary>
    private static readonly SequenceWalkWiring Wiring = new();

    /// <summary>The wiring's sink: BepInEx's logger, behind an interface the tests can fake.</summary>
    private sealed class PluginGateLog : IEffectGateLog
    {
        internal static readonly PluginGateLog Instance = new();

        public void Info(string message) => Plugin.Logger?.LogInfo(message);

        public void Warning(string message) => Plugin.Logger?.LogWarning(message);
    }

    internal static void Clear()
    {
        SlotCache.Clear();
        ResolveMisses.Clear();
        Wiring.Reset();
    }

    internal static int CacheCount => SlotCache.Count;

    /// <summary>
    /// True while the blob walk is allowed to run at all.
    ///
    /// <para>R28 finding 2 (EV16): the gate defaults <b>closed</b>, so it cannot be consulted
    /// before the probe has run. <see cref="StrideOkNow"/> runs the probe on demand.</para>
    ///
    /// <para><b>R30 finding 5.</b> What the probe decides on has changed. It used to be
    /// <c>sizeof(SequenceInputElement)</c> - a number the walk never multiplies by - so a third
    /// value from an interop regeneration took the whole re-point down process-wide while the
    /// dump-derived walk underneath would still have been byte-for-byte correct. That is the
    /// third repetition of one failure (EV15/EV16 via <c>TypeManager</c>, EV17 pass 1 via
    /// <c>sizeof</c>, EV17 pass 2 via a widened accepted set). The gate is now keyed on
    /// <see cref="SequenceBlackboardLayout.InputElementWalkAllowed"/>: the dump constants the
    /// walk actually uses, checked against each other. A runtime reading produces a log line and
    /// cannot close anything.</para>
    ///
    /// <para><b>EV17.</b> This used to be
    /// <c>StrideOkNow() &amp;&amp; !_elementSizeGate.Failed</c>. The element-size gate belongs to
    /// the <em>buffer</em> write and says nothing about whether a blob walk can find a register
    /// offset — but it was in the predicate <see cref="TryGetSlots"/> reads, so anything that
    /// latched it (a <c>TypeManager</c> cross-check disagreeing, or one sequence with an empty
    /// blackboard buffer) turned slot resolution off for every sequence for the rest of the
    /// process. That is the live regression that produced
    /// <c>path=none … this sequence cannot be re-pointed</c> on a client that had been
    /// re-pointing beams fine. The two are decoupled: the stride gate guards the
    /// <em>walk</em>, the element-size gate guards the <em>write</em>, and each is consulted
    /// where it applies.</para>
    /// </summary>
    internal static bool LayoutOk => StrideOkNow();

    /// <summary>
    /// Run the stride probe if it has not run, then report whether the gate is open. Every
    /// blackboard entry point calls this instead of reading the gate directly: with a
    /// default-closed gate, "check the flag, then probe" refuses forever.
    /// </summary>
    private static bool StrideOkNow() =>
        Wiring.InputWalkEnabled(ReadInputElementSizeOf, PluginGateLog.Instance);

    /// <summary>
    /// The runtime reading the wiring reports and never obeys. Separate so the wiring - and the
    /// tests that drive it - can be handed a wrong reading without a game present.
    /// </summary>
    private static int ReadInputElementSizeOf() => sizeof(SequenceInputElement);

    /// <summary>
    /// Byte offsets of the <c>Position</c> / <c>Rotation</c> registers on
    /// <paramref name="sequence"/>, cached by sequence entity. Both -1 means this sequence
    /// graph has no such inputs (nothing to re-point).
    /// </summary>
    internal static bool TryGetSlots(World? world, Entity sequence, out Slots slots)
    {
        slots = new Slots(-1, -1);
        if (world == null || !world.IsCreated || sequence == Entity.Null) return false;
        if (!LayoutOk) return false;
        long key = ((long)sequence.Index << 32) ^ (uint)sequence.Version;
        if (SlotCache.TryGetValue(key, out slots))
            return slots.Any;

        slots = Resolve(world, sequence);
        if (slots.Any)
        {
            ResolveMisses.Remove(key);
            if (SlotCache.Count > CacheCap) SlotCache.Clear();
            SlotCache[key] = slots;
            return true;
        }

        // EV17: a miss is not cached straight away. The coil client system creates the sequence
        // entity and fills its SequenceInput blob, and a tick that lands between the two used to
        // pin (-1, -1) for the entity's whole life — `path=none` forever, with the tracker
        // counting writes it never made. Retry for a bounded number of frames, then give up and
        // cache so the walk is not run every frame for a sequence that genuinely has no
        // Position/Rotation registers.
        ResolveMisses.TryGetValue(key, out int misses);
        misses++;
        if (misses < ResolveRetries)
        {
            if (ResolveMisses.Count > CacheCap) ResolveMisses.Clear();
            ResolveMisses[key] = misses;
            return false;
        }

        ResolveMisses.Remove(key);
        if (SlotCache.Count > CacheCap) SlotCache.Clear();
        SlotCache[key] = slots;
        return false;
    }

    private static Slots Resolve(World world, Entity sequence)
    {
        try
        {
            if (!StrideOkNow()) return new Slots(-1, -1);
            var em = world.EntityManager;
            if (!em.Exists(sequence)) return new Slots(-1, -1);
            var inputType = ComponentIndex.Of<SequenceInput>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(sequence, inputType)) return new Slots(-1, -1);
            void* raw = em.GetComponentDataRawRO(sequence, ComponentIndex.IndexOf<SequenceInput>());
            if (raw == null) return new Slots(-1, -1);
            byte* blob = *(byte**)raw;                 // BlobAssetReferenceData.m_Ptr
            if (blob == null) return new Slots(-1, -1);

            int offset = *(int*)blob;                  // BlobArray.m_OffsetPtr (self-relative)
            int count = *(int*)(blob + 4);             // BlobArray.m_Length

            // R31 finding 14: the blob's OWN extent, not a 4 MiB constant. Unity's
            // BlobAssetHeader sits immediately before the data m_Ptr points at, with
            // `int Length; // 0x8` (SequenceBlackboardLayout.BlobAssetHeaderDumpLines), so the
            // real allocation size is one read behind the pointer. Without it, a stale or
            // differently-versioned 256-byte blob whose m_OffsetPtr/m_Length read as 8/512 sent
            // the walk ~38 KB past the allocation, and TryFindSlots' per-element
            // `at + stride > blob.Length` check could not stop it: blob.Length was itself
            // derived from the same untrusted offset/count.
            long blobLength = *(int*)(blob + SequenceBlackboardLayout.BlobAssetHeaderLengthDelta);

            // R27 finding 5.2: offset used to be checked only for > 0, so a garbage blob root
            // walked a wild pointer. InputWalkInRange bounds offset, count, offset + count *
            // stride and the extent, all in long arithmetic, and refuses THIS blob only.
            if (!Wiring.InputWalkInRange(offset, count, blobLength, PluginGateLog.Instance))
            {
                LogWalkOnce(default, offset, count, -1, -1);
                return new Slots(-1, -1);
            }

            // EV17: the walk itself moved into the game-free SequenceBlackboardLayout so the
            // tests run the same code the game does (R29 22.x: "the edit types get no
            // coverage"). The span is in range because InputWalkInRange said so - against the
            // header's length - not by construction.
            long end = SequenceBlackboardLayout.WalkEnd(offset, count);
            var span = new ReadOnlySpan<byte>(blob, (int)end);
            SequenceBlackboardLayout.TryFindSlots(span, offset, count, out int position, out int rotation);
            LogWalkOnce(span, offset, count, position, rotation);
            return new Slots(position, rotation);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence blackboard input walk threw: " + e.Message);
            return new Slots(-1, -1);
        }
    }

    /// <summary>
    /// One Info line per Core generation with the numbers the walk actually read. The one-shot
    /// flag and the formatting live in <see cref="SequenceWalkWiring.WalkDiagnosticOnce"/>
    /// (R31 finding 26); this only supplies the numbers that need the game.
    /// </summary>
    private static void LogWalkOnce(
        ReadOnlySpan<byte> blob, int offset, int count, int position, int rotation)
    {
        // Reported next to the stride, never used as one: the walk strides by the dump's 76 and
        // this reads the managed struct's unpadded 73 on this interop (EV17 second pass).
        int observedSizeOf;
        try { observedSizeOf = sizeof(SequenceInputElement); }
        catch { observedSizeOf = -1; }
        string inputs = blob.Length == 0
            ? "walk refused"
            : SequenceBlackboardLayout.DescribeInputs(blob, offset, count, 8);
        string? line = Wiring.WalkDiagnosticOnce(
            observedSizeOf, offset, count, position, rotation, inputs);
        if (line != null) Plugin.Logger?.LogInfo(line);
    }

    /// <summary>
    /// Read a float3 register. Used for the same-frame readback that proves whether the
    /// sequencer overwrites our write (the EV5 / EV9 technique).
    /// </summary>
    internal static bool TryReadFloat3(World? world, Entity sequence, int byteOffset, out float3 value)
    {
        value = default;
        if (!LayoutOk) return false;
        if (byteOffset < 0 || !TryBuffer(world, sequence, out byte* buffer, out int length)) return false;
        if (!SequenceBlackboardLayout.SlotFits(byteOffset, SequenceBlackboardLayout.Float3Bytes, length))
            return false;
        value = *(float3*)(buffer + byteOffset);
        return true;
    }

    /// <summary>
    /// Write the <c>Position</c> float3 and <c>Rotation</c> quaternion registers. Both slots go
    /// through <see cref="SequenceBlackboardLayout.SlotFits"/>, which adds in <c>long</c>:
    /// R27 finding 5.3, the old <c>slot + 12 &lt;= length</c> overflowed for a blob-supplied
    /// <c>Index</c> near <c>int.MaxValue</c> and the write landed ~2 GB past the buffer.
    /// </summary>
    internal static bool TryWrite(World? world, Entity sequence, in Slots slots, float3 position, quaternion rotation)
    {
        if (!LayoutOk) return false;
        if (!slots.Any) return false;
        if (!TryBuffer(world, sequence, out byte* buffer, out int length)) return false;
        bool wrote = false;
        if (SequenceBlackboardLayout.SlotFits(slots.Position, SequenceBlackboardLayout.Float3Bytes, length))
        {
            *(float3*)(buffer + slots.Position) = position;
            wrote = true;
        }

        if (SequenceBlackboardLayout.SlotFits(slots.Rotation, SequenceBlackboardLayout.QuaternionBytes, length))
        {
            *(quaternion*)(buffer + slots.Rotation) = rotation;
            wrote = true;
        }

        return wrote;
    }

    private static bool TryBuffer(World? world, Entity sequence, out byte* buffer, out int length)
    {
        buffer = null;
        length = 0;
        if (world == null || !world.IsCreated || sequence == Entity.Null) return false;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(sequence)) return false;
            var type = ComponentIndex.Of<BlackboardElement>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(sequence, type)) return false;
            var index = ComponentIndex.IndexOf<BlackboardElement>();
            // R27 finding 17: GetBufferLength is read here as a BYTE count, which is only true
            // while BlackboardElement is one byte. The gate is the dump constants checked
            // against each other; the two runtime readings are reported and cannot close it
            // (R30 findings 5 and 9, wired in SequenceWalkWiring for R31 finding 26).
            if (!Wiring.BufferWritesEnabled(
                    ReadBlackboardElementSizeOf,
                    () => TypeManager.GetTypeInfo(index).ElementSize,
                    PluginGateLog.Instance))
            {
                return false;
            }

            void* raw = em.GetBufferRawRW(sequence, index);
            if (raw == null) return false;
            length = em.GetBufferLength(sequence, index);
            // R28 finding 1's other half: SlotFits bounds the write against this number, so a
            // garbage length authorizes a garbage slot. The verdict, the one-shot logs and the
            // rule that a length is a fact about ONE entity (never a latch, R30 finding 9) all
            // live in the wiring now (R31 finding 26).
            if (!Wiring.BufferLengthUsable(length, PluginGateLog.Instance))
            {
                length = 0;
                return false;
            }

            buffer = (byte*)raw;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence blackboard buffer read threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// The <c>sizeof</c> reading the wiring reports and never obeys. Separate so the wiring -
    /// and the tests that drive it - can be handed a wrong reading with no game present.
    /// </summary>
    private static int ReadBlackboardElementSizeOf() => sizeof(BlackboardElement);

    /// <summary>
    /// The rotation the coil client system writes: <c>quaternion.LookRotation(End - Start, up)</c>
    /// with the <b>raw</b> (unnormalized) difference, matching native
    /// <c>ProfessorCoilSystem_Client.OnUpdate</c> so a re-point cannot jump relative to the
    /// pose the game baked at spawn. The game's own implementation is called, not a re-derived one.
    /// </summary>
    internal static quaternion LookAlong(float3 start, float3 end)
    {
        var d = new float3(end.x - start.x, end.y - start.y, end.z - start.z);
        if ((d.x * d.x) + (d.y * d.y) + (d.z * d.z) < 1e-8f)
            return quaternion.identity;
        try { return quaternion.LookRotation(d, new float3(0f, 1f, 0f)); }
        catch { return quaternion.identity; }
    }
}
#endif
