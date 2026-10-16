#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using ProjectM;
using ProjectM.Behaviours;
using ProjectM.Gameplay.Scripting;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using PrefabTag = Unity.Entities.Prefab;

namespace Progenitor.Ai;

/// <summary>
/// Live server half of <see cref="PluginAi"/>. Every read and write here is an
/// <c>EntityManager</c> raw component/buffer access at a <see cref="Layout"/> offset, an
/// <c>Instantiate</c>, a <see cref="PrefabLookup"/> call, or a byte copy into a private blob. No
/// native method is called directly and no generic interop is instantiated beyond
/// <see cref="ComponentIndex.Of{T}"/> (plus <c>GetBuffer&lt;BuffBuffer&gt;</c> and
/// <c>SetComponentData&lt;PrefabGUID&gt;</c>, which Core already uses live).
/// Evidence: the 2026-09-24 boss AI tree review (internal research notes).
/// </summary>
internal static unsafe class AiRuntime
{
    internal const int MaxStates = 16;
    internal const int MaxGroups = 64;
    internal const int MaxRows = 256;
    internal const int MaxSlots = 64;
    internal const int MaxHistory = 32;
    internal const int MaxBlobBytes = 4 * 1024 * 1024;

    // ---- world, thread, prefabs ----------------------------------------------------------------

    internal static World Server(string op)
    {
        if (!Plugin.IsServerProcess)
            throw new InvalidOperationException("ctx.Ai." + op + " is server-only; the client has no AI to edit.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Ai." + op + " must run on the game main thread (a command, Plugin.Frame or an event).");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("ctx.Ai." + op + ": the server world does not exist yet.");
        return world;
    }

    internal static bool TryPrefab(World world, string spelling, out int guid, out Entity entity, out string name)
    {
        guid = 0;
        entity = Entity.Null;
        name = "";
        string s = (spelling ?? "").Trim();
        if (s.Length == 0) return false;
        PrefabGUID found;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            found = new PrefabGUID(parsed);
        else if (!TryGuidByAnyName(world, s, out found))
            return false;
        if (!PrefabLookup.TryGetEntity(world, found, out entity) || entity == Entity.Null || !world.EntityManager.Exists(entity))
            return false;
        guid = found.GuidHash;
        name = Name(world, guid);
        if (name.Length == 0) name = s;
        return true;
    }

    /// <summary>
    /// Bosses are console spawnables, but factions, cast-option prefabs and buffs are not, so ask
    /// <c>PrefabLookupMap.TryGetPrefabGuidWithName</c> first (the lookup the content registry uses
    /// for buff sources) and fall back to the spawnable list.
    /// </summary>
    /// <summary>
    /// A prefab's GUID by name: <c>PrefabLookupMap.TryGetPrefabGuidWithName</c> first (buffs are not
    /// spawnables; research buffs.md, evidence 97), then the spawnable dictionary.
    /// </summary>
    internal static bool TryGuidByAnyName(World world, string name, out PrefabGUID guid)
    {
        guid = default;
        try
        {
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabs != null && prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(name, out guid, true) && guid.GuidHash != 0)
                return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ai: TryGetPrefabGuidWithName('" + name + "') threw: " + e.Message);
        }
        return PrefabLookup.TryGetGuidByName(world, name, out guid);
    }

    internal static Entity RequirePrefab(World world, string spelling, string what, out int guid, out string name)
    {
        if (!TryPrefab(world, spelling, out guid, out var entity, out name))
            throw new InvalidOperationException(what + " '" + spelling + "' is not a prefab on this server.");
        return entity;
    }

    internal static Entity PrefabEntity(World world, int guid) =>
        PrefabLookup.TryGetEntity(world, new PrefabGUID(guid), out var e) ? e : Entity.Null;

    internal static string Name(World world, int guid) =>
        guid != 0 && PrefabLookup.TryGetName(world, new PrefabGUID(guid), out var n) ? n : "";

    internal static int PrefabGuidOf(EntityManager em, Entity entity)
    {
        try
        {
            if (entity.Has<PrefabGUID>(em) && entity.TryRead(em, out PrefabGUID guid)) return guid.GuidHash;
        }
        catch { }
        return 0;
    }

    internal static string Fmt(Entity e) =>
        e.Index.ToString(CultureInfo.InvariantCulture) + ":" + e.Version.ToString(CultureInfo.InvariantCulture);

    internal static void RequireAlive(EntityManager em, Entity unit)
    {
        if (unit == Entity.Null || !em.Exists(unit))
            throw new InvalidOperationException("entity " + Fmt(unit) + " does not exist.");
    }

    // ---- raw component and buffer access -------------------------------------------------------

    private static int TypeSize(TypeIndex index)
    {
        try { return TypeManager.GetTypeInfo(index).TypeSize; }
        catch { return 0; }
    }

    private static int ElementSize(TypeIndex index)
    {
        try { return TypeManager.GetTypeInfo(index).ElementSize; }
        catch { return 0; }
    }

    /// <summary>Read-only pointer to <typeparamref name="T"/>, or null when absent or smaller than <paramref name="need"/>.</summary>
    internal static byte* RO<T>(EntityManager em, Entity entity, int need)
    {
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly);
        if (entity == Entity.Null || !em.Exists(entity) || !em.HasComponent(entity, type)) return null;
        int size = TypeSize(type.TypeIndex);
        if (size > 0 && size < need) return null;
        return (byte*)em.GetComponentDataRawRO(entity, type.TypeIndex);
    }

    /// <summary>
    /// Writable overlay pointer to <typeparamref name="T"/> for field pokes at declared offsets —
    /// the <c>GetComponentDataRawRW</c> overlay path <see cref="RawWriteGuard"/> describes, never a
    /// memcpy of a guessed size. TypeSize 0 is the norm through this interop and is accepted; a
    /// non-zero TypeSize smaller than the last byte the caller pokes is refused.
    /// </summary>
    internal static byte* RW<T>(EntityManager em, Entity entity, int need)
    {
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite);
        string name = typeof(T).Name;
        if (entity == Entity.Null || !em.Exists(entity) || !em.HasComponent(entity, type))
            throw new InvalidOperationException("entity " + Fmt(entity) + " has no " + name + ".");
        int size = TypeSize(type.TypeIndex);
        if (size > 0 && size < need)
            throw new InvalidOperationException("Refusing overlay write of " + name + ": TypeSize " + size + " is smaller than the field extent " + need + ".");
        byte* p = (byte*)em.GetComponentDataRawRW(entity, type.TypeIndex);
        if (p == null) throw new InvalidOperationException(name + " GetComponentDataRawRW returned null.");
        return p;
    }

    /// <summary>
    /// Buffer pointer and length. The stride is always the dump-derived one the caller passes, never
    /// a TypeManager size (CLAUDE.md). A TypeManager <c>ElementSize</c> that is non-zero and smaller
    /// than that stride would mean the walk overruns each element, so it refuses; larger or zero
    /// values are ignored as the interop's usual noise. The length is capped by the caller's loop.
    /// </summary>
    internal static byte* Buffer<T>(EntityManager em, Entity entity, int stride, bool write, out int length)
    {
        length = 0;
        var type = ComponentIndex.Of<T>(write ? ComponentType.AccessMode.ReadWrite : ComponentType.AccessMode.ReadOnly);
        if (entity == Entity.Null || !em.Exists(entity) || !em.HasComponent(entity, type)) return null;
        int element = ElementSize(type.TypeIndex);
        if (element > 0 && element < stride)
            throw new InvalidOperationException(typeof(T).Name + " element size " + element + " is smaller than the declared stride " + stride + "; refusing the walk.");
        length = em.GetBufferLength(entity, type.TypeIndex);
        if (length <= 0) return null;
        return (byte*)(write ? em.GetBufferRawRW(entity, type.TypeIndex) : em.GetBufferRawRO(entity, type.TypeIndex));
    }

    internal static Entity ReadEntity(byte* p) => new() { Index = *(int*)p, Version = *(int*)(p + sizeof(int)) };

    internal static void WriteEntity(byte* p, Entity e)
    {
        *(int*)p = e.Index;
        *(int*)(p + sizeof(int)) = e.Version;
    }

    internal static void EnsurePrefabTag(EntityManager em, Entity entity)
    {
        if (!entity.Has<PrefabTag>(em)) entity.Add<PrefabTag>(em);
    }

    // ---- blobs -----------------------------------------------------------------------------------

    internal static byte* BlobData(byte* referenceField) => *(byte**)referenceField;

    internal static int BlobLength(byte* data)
    {
        if (data == null) return 0;
        int length = *(int*)(data - Layout.BlobAssetHeaderBytes + Layout.BlobAssetHeaderLengthOffset);
        return length > 0 && length <= MaxBlobBytes ? length : 0;
    }

    internal static byte[] CopyOut(byte* data)
    {
        int length = BlobLength(data);
        if (length <= 0) throw new InvalidOperationException("blob length is unreadable");
        var bytes = new byte[length];
        new ReadOnlySpan<byte>(data, length).CopyTo(bytes);
        return bytes;
    }

    private static readonly object CopyGate = new();
    private static readonly Dictionary<string, IntPtr> Copies = new(StringComparer.Ordinal);

    /// <summary>
    /// A private blob holding <paramref name="data"/>, with the 32-byte header of
    /// <paramref name="template"/> and its ValidationPtr pointed at the new data. Allocated
    /// Persistent and <b>never freed</b>: a scheduled job may still read it. Identical content is
    /// allocated once per Core generation and shared (one copy per variant, not per call).
    /// </summary>
    internal static byte* PrivateBlob(byte[] data, byte* template)
    {
        if (data == null || data.Length == 0) throw new ArgumentException("blob data is empty");
        if (template == null) throw new ArgumentNullException(nameof(template));
        string key = Convert.ToHexString(SHA256.HashData(data));
        lock (CopyGate)
        {
            if (Copies.TryGetValue(key, out var existing)) return (byte*)existing;
            int header = Layout.BlobAssetHeaderBytes;
            byte* block = (byte*)UnsafeUtility.Malloc(data.Length + header, 16, Allocator.Persistent);
            if (block == null) throw new InvalidOperationException("blob allocation failed");
            new ReadOnlySpan<byte>(template - header, header).CopyTo(new Span<byte>(block, header));
            byte* copy = block + header;
            data.AsSpan().CopyTo(new Span<byte>(copy, data.Length));
            *(void**)(block + Layout.BlobAssetHeaderValidationPtrOffset) = copy;
            *(int*)(block + Layout.BlobAssetHeaderLengthOffset) = data.Length;
            Copies[key] = (IntPtr)copy;
            Plugin.Logger?.LogInfo("Ai: private blob " + data.Length + " bytes at 0x" + ((long)copy).ToString("X", CultureInfo.InvariantCulture) + " (kept for the process; never freed)");
            return copy;
        }
    }

    /// <summary>
    /// A private blob holding <paramref name="data"/> behind the given 32-byte
    /// <paramref name="header"/>, whose <c>ValidationPtr</c> and <c>Length</c> are then pointed at the
    /// copy (<see cref="AllySafetyCodec.SealHeader"/>). Same rules as the other overload: Persistent,
    /// <b>never freed</b>, one allocation per distinct header and content per Core generation. Not
    /// logged per blob; the caller logs a summary.
    /// </summary>
    internal static byte* PrivateBlob(byte[] data, byte[] header)
    {
        if (data == null || data.Length == 0) throw new ArgumentException("blob data is empty");
        int headerBytes = Layout.BlobAssetHeaderBytes;
        if (header == null || header.Length != headerBytes) throw new ArgumentException("header must be " + headerBytes + " bytes");
        var keyed = new byte[headerBytes + data.Length];
        header.CopyTo(keyed, 0);
        data.CopyTo(keyed, headerBytes);
        string key = "h:" + Convert.ToHexString(SHA256.HashData(keyed));
        lock (CopyGate)
        {
            if (Copies.TryGetValue(key, out var existing)) return (byte*)existing;
            byte* block = (byte*)UnsafeUtility.Malloc(data.Length + headerBytes, 16, Allocator.Persistent);
            if (block == null) throw new InvalidOperationException("blob allocation failed");
            byte* copy = block + headerBytes;
            var headerSpan = new Span<byte>(block, headerBytes);
            header.AsSpan().CopyTo(headerSpan);
            data.AsSpan().CopyTo(new Span<byte>(copy, data.Length));
            AllySafetyCodec.SealHeader(headerSpan, (long)copy, data.Length);
            Copies[key] = (IntPtr)copy;
            return copy;
        }
    }

    internal static int PrivateBlobCount
    {
        get { lock (CopyGate) return Copies.Count; }
    }

    // ---- ability slots and cast options ---------------------------------------------------------

    internal static List<AiAbilitySlot> Slots(World world, Entity boss)
    {
        var em = world.EntityManager;
        var slots = new List<AiAbilitySlot>();
        byte* raw = Buffer<AbilityGroupSlotBuffer>(em, boss, Layout.AbilityGroupSlotStride, write: false, out int length);
        for (int i = 0; raw != null && i < length && i < MaxSlots; i++)
        {
            int guid = *(int*)(raw + i * Layout.AbilityGroupSlotStride + Layout.AbilityGroupSlotGroupOffset);
            slots.Add(new AiAbilitySlot(i, guid, Name(world, guid)));
        }
        return slots;
    }

    /// <summary>One cast-option row's home: the option entity and its row index.</summary>
    internal readonly struct RowRef
    {
        internal RowRef(Entity option, int row, AiCastOptionRow decoded)
        {
            Option = option;
            Row = row;
            Decoded = decoded;
        }

        internal Entity Option { get; }
        internal int Row { get; }
        internal AiCastOptionRow Decoded { get; }
    }

    internal static int CastOptionsGuid(EntityManager em, Entity unitOrPrefab)
    {
        byte* p = RO<CastOptionsUser>(em, unitOrPrefab, Layout.CastOptionsUserPrefabOffset + sizeof(int));
        return p == null ? 0 : *(int*)(p + Layout.CastOptionsUserPrefabOffset);
    }

    /// <summary>The holder entities of <paramref name="co"/>'s state buffer, with their state masks.</summary>
    internal static List<(int State, Entity Holder)> States(EntityManager em, Entity co)
    {
        var list = new List<(int, Entity)>();
        byte* raw = Buffer<CastOptionStateBuffer>(em, co, Layout.CastOptionStateBufferStride, write: false, out int length);
        for (int s = 0; raw != null && s < length && s < MaxStates; s++)
        {
            byte* at = raw + s * Layout.CastOptionStateBufferStride;
            list.Add((*(int*)(at + Layout.CastOptionStateBufferStateOffset), ReadEntity(at + Layout.CastOptionStateBufferEntityOffset)));
        }
        return list;
    }

    internal static List<Entity> Groups(EntityManager em, Entity holder)
    {
        var list = new List<Entity>();
        byte* raw = Buffer<CastOptionGroupBuffer>(em, holder, Layout.CastOptionGroupBufferStride, write: false, out int length);
        for (int g = 0; raw != null && g < length && g < MaxGroups; g++)
            list.Add(ReadEntity(raw + g * Layout.CastOptionGroupBufferStride + Layout.CastOptionGroupBufferEntityOffset));
        return list;
    }

    /// <summary>Decode every row of a CO prefab (or clone). <paramref name="refs"/> receives each row's home.</summary>
    internal static List<AiCastOptionGroup> ReadCastOptions(EntityManager em, Entity co, IReadOnlyList<AiAbilitySlot> slots, List<RowRef>? refs)
    {
        var groups = new List<AiCastOptionGroup>();
        int stride = CastOptionCodec.Stride;
        foreach (var (state, holder) in States(em, co))
        {
            var options = Groups(em, holder);
            for (int g = 0; g < options.Count; g++)
            {
                var rows = new List<AiCastOptionRow>();
                byte* raw = Buffer<CastOptionBuffer>(em, options[g], stride, write: false, out int length);
                for (int r = 0; raw != null && r < length && r < MaxRows; r++)
                {
                    byte* row = raw + r * stride;
                    var decoded = CastOptionCodec.Decode(new ReadOnlySpan<byte>(row, stride), g, r, slots);
                    byte* condition = BlobData(row + Layout.CastOptionConditionOffset);
                    int conditionLength = BlobLength(condition);
                    if (conditionLength > 0)
                    {
                        var span = new ReadOnlySpan<byte>(condition, conditionLength);
                        decoded.ConditionSummary = ConditionCodec.Summarize(span);
                        var gates = ConditionCodec.HealthGates(span);
                        if (gates.Count > 0) decoded.HealthGate = ConditionCodec.ReadGate(span, gates[0]);
                    }
                    rows.Add(decoded);
                    refs?.Add(new RowRef(options[g], r, decoded));
                }
                groups.Add(new AiCastOptionGroup(state, g, rows));
            }
        }
        return groups;
    }

    internal static byte* RowPointer(EntityManager em, Entity option, int row)
    {
        byte* raw = Buffer<CastOptionBuffer>(em, option, CastOptionCodec.Stride, write: true, out int length);
        if (raw == null || row < 0 || row >= length)
            throw new InvalidOperationException("cast-option row " + row + " of " + Fmt(option) + " is gone.");
        return raw + row * CastOptionCodec.Stride;
    }

    /// <summary>Rows of <paramref name="co"/> that cast <paramref name="abilityGroup"/>; throws when none.</summary>
    internal static List<RowRef> SelectRows(World world, Entity co, IReadOnlyList<AiAbilitySlot> slots, string abilityGroup, int? priority)
    {
        if (!CastOptionCodec.TryResolveGroup(slots, abilityGroup, out int groupGuid, out string error))
            throw new InvalidOperationException(error);
        var refs = new List<RowRef>();
        ReadCastOptions(world.EntityManager, co, slots, refs);
        var hits = refs.FindAll(r => r.Decoded.AbilityGroupGuid == groupGuid && (priority == null || r.Decoded.PriorityGroup == priority.Value));
        if (hits.Count == 0)
            throw new InvalidOperationException("no cast-option row casts '" + abilityGroup + "'" + (priority == null ? "" : " in priority group " + priority.Value) + ".");
        return hits;
    }

    /// <summary>Apply one edit to every selected row. Returns an undo that restores exactly what it changed.</summary>
    internal static Action EditRows(World world, List<RowRef> rows, CastOptionEdit edit)
    {
        var em = world.EntityManager;
        var done = new List<(Entity Option, int Row, List<BytePatch> Patches)>();
        foreach (var r in rows)
        {
            byte* row = RowPointer(em, r.Option, r.Row);
            done.Add((r.Option, r.Row, CastOptionCodec.Apply(new Span<byte>(row, CastOptionCodec.Stride), edit)));
        }
        int epoch = WorldEpoch.Current;
        return () =>
        {
            if (WorldEpoch.IsStale(epoch)) return;
            var w = Worlds.Server;
            if (w == null || !w.IsCreated) return;
            var e = w.EntityManager;
            int skipped = 0;
            foreach (var (option, index, patches) in done)
            {
                if (!e.Exists(option)) continue;
                byte* row;
                try { row = RowPointer(e, option, index); }
                catch { continue; }
                var span = new Span<byte>(row, CastOptionCodec.Stride);
                for (int i = patches.Count - 1; i >= 0; i--)
                    if (!patches[i].Restore(span)) skipped++;
            }
            if (skipped > 0)
                Plugin.Logger?.LogWarning("Ai: " + skipped + " cast-option field(s) were changed again after this edit and were left as they are.");
        };
    }

    /// <summary>
    /// Point every selected row that has a boss-health gate at a private condition copy whose gates
    /// read <paramref name="fraction"/>. Refuses when no selected row has such a gate. The original
    /// blobs are never written.
    /// </summary>
    internal static Action SetHealthGate(World world, List<RowRef> rows, float fraction, string abilityGroup)
    {
        AiChecks.Fraction(fraction, nameof(fraction));
        var em = world.EntityManager;
        var plan = new List<(Entity Option, int Row, IntPtr Original, IntPtr Copy)>();
        foreach (var r in rows)
        {
            byte* row = RowPointer(em, r.Option, r.Row);
            byte* original = BlobData(row + Layout.CastOptionConditionOffset);
            if (BlobLength(original) <= 0) continue;
            byte[] data = CopyOut(original);
            if (ConditionCodec.WriteGates(data, fraction) == 0) continue;
            plan.Add((r.Option, r.Row, (IntPtr)original, (IntPtr)PrivateBlob(data, original)));
        }
        if (plan.Count == 0)
            throw new InvalidOperationException("no '" + abilityGroup + "' row has a HealthFactor condition on the boss; nothing to move.");
        foreach (var (option, index, _, copy) in plan)
            *(IntPtr*)(RowPointer(em, option, index) + Layout.CastOptionConditionOffset) = copy;
        int epoch = WorldEpoch.Current;
        return () =>
        {
            if (WorldEpoch.IsStale(epoch)) return;
            var w = Worlds.Server;
            if (w == null || !w.IsCreated) return;
            var e = w.EntityManager;
            foreach (var (option, index, original, copy) in plan)
            {
                if (!e.Exists(option)) continue;
                byte* row;
                try { row = RowPointer(e, option, index); }
                catch { continue; }
                var field = (IntPtr*)(row + Layout.CastOptionConditionOffset);
                if (*field == copy) *field = original; // the copy stays allocated: a job may still read it
            }
        };
    }

    // ---- behavior trees ------------------------------------------------------------------------

    internal static int TreeGuidOf(EntityManager em, Entity unitOrPrefab)
    {
        byte* p = RO<BehaviourTreeBinding>(em, unitOrPrefab, Layout.BehaviourTreeBindingPrefabGuidOffset + sizeof(int));
        return p == null ? 0 : *(int*)(p + Layout.BehaviourTreeBindingPrefabGuidOffset);
    }

    internal static byte* TreeBlob(EntityManager em, Entity treeEntity)
    {
        byte* p = RO<BehaviourTree>(em, treeEntity, Layout.BehaviourTreeBlobPointerOffset + sizeof(long));
        return p == null ? null : BlobData(p + Layout.BehaviourTreeBlobPointerOffset);
    }

    internal static int BlackboardSize(EntityManager em, Entity treeEntity)
    {
        byte* p = RO<BehaviourTreeBlackboardSize>(em, treeEntity, Layout.BehaviourTreeBlackboardSizeValueOffset + sizeof(int));
        return p == null ? 0 : *(int*)(p + Layout.BehaviourTreeBlackboardSizeValueOffset);
    }

    internal static Entity CurrentTree(EntityManager em, Entity unit)
    {
        byte* p = RO<BehaviourTreeInstance>(em, unit, Layout.BehaviourTreeInstanceExpectedTreeOffset + sizeof(long));
        return p == null ? Entity.Null : ReadEntity(p + Layout.BehaviourTreeInstanceTreeEntityOffset);
    }

    internal static Entity ExpectedTree(EntityManager em, Entity unit)
    {
        byte* p = RO<BehaviourTreeInstance>(em, unit, Layout.BehaviourTreeInstanceExpectedTreeOffset + sizeof(long));
        return p == null ? Entity.Null : ReadEntity(p + Layout.BehaviourTreeInstanceExpectedTreeOffset);
    }

    /// <summary>
    /// Ask the unit's tree job to move to <paramref name="tree"/>. The job aborts running nodes
    /// against the old blob, resizes and clears the blackboard, and restarts in state 0. This is the
    /// only tree swap Progenitor performs; it never rewrites a tree entity's blob under a unit.
    /// </summary>
    internal static void WriteExpectedTree(EntityManager em, Entity unit, Entity tree)
    {
        byte* p = RW<BehaviourTreeInstance>(em, unit, Layout.BehaviourTreeInstanceExpectedTreeOffset + sizeof(long));
        WriteEntity(p + Layout.BehaviourTreeInstanceExpectedTreeOffset, tree);
    }

    /// <summary>Core tree repair only: both references to the prefab tree (see <see cref="TreeRepairPolicy"/>).</summary>
    internal static void WriteBothTrees(EntityManager em, Entity unit, Entity tree)
    {
        byte* p = RW<BehaviourTreeInstance>(em, unit, Layout.BehaviourTreeInstanceExpectedTreeOffset + sizeof(long));
        WriteEntity(p + Layout.BehaviourTreeInstanceTreeEntityOffset, tree);
        WriteEntity(p + Layout.BehaviourTreeInstanceExpectedTreeOffset, tree);
    }

    /// <summary>
    /// A new tree entity for <paramref name="variant"/>: Instantiate the vanilla tree prefab, tag it
    /// Prefab again so no query picks it up (vanilla tree entities are prefabs), and point its blob
    /// at a private copy of the variant's bytes — before any unit references it.
    /// </summary>
    internal static Entity MaterializeTree(World world, TreeVariant variant)
    {
        var em = world.EntityManager;
        var source = PrefabEntity(world, variant.TreeGuid);
        if (source == Entity.Null) throw new InvalidOperationException("tree prefab " + variant.TreeGuid + " is gone.");
        byte* template = TreeBlob(em, source);
        if (template == null) throw new InvalidOperationException("tree prefab " + variant.TreeName + " has no blob.");
        if (BlobLength(template) != variant.Data.Length)
            throw new InvalidOperationException("tree prefab " + variant.TreeName + " blob changed size since the variant was copied; create the variant again.");
        variant.Freeze();
        byte* copy = PrivateBlob(variant.Data, template);
        if (source.Has<LinkedEntityGroup>(em))
            throw new InvalidOperationException("tree prefab " + variant.TreeName + " has a LinkedEntityGroup; refusing to instantiate it.");
        var tree = em.Instantiate(source);
        EnsurePrefabTag(em, tree);
        byte* field = RW<BehaviourTree>(em, tree, Layout.BehaviourTreeBlobPointerOffset + sizeof(long));
        *(byte**)(field + Layout.BehaviourTreeBlobPointerOffset) = copy;
        Plugin.Logger?.LogInfo("Ai: tree variant of " + variant.TreeName + " -> entity " + Fmt(tree) + " (" + string.Join("; ", variant.Changes) + ")");
        return tree;
    }

    // ---- phase script ---------------------------------------------------------------------------

    internal static AiPhaseScript? ReadPhase(World world, Entity entity)
    {
        byte* p = RO<Script_ApplyBuffUnderHealthThreshold_DataServer>(world.EntityManager, entity, PhaseScriptCodec.MinBytes);
        if (p == null) return null;
        return PhaseScriptCodec.Decode(new ReadOnlySpan<byte>(p, PhaseScriptCodec.MinBytes), g => Name(world, g));
    }

    internal static List<string> UnsupportedPhaseScripts(EntityManager em, Entity entity)
    {
        var list = new List<string>();
        if (entity.Has<Script_ApplyBuffUnderHealthThreshhold_MonsterVBlood_DataServer>(em))
            list.Add(nameof(Script_ApplyBuffUnderHealthThreshhold_MonsterVBlood_DataServer));
        if (entity.Has<Script_ApplyBuffUnderThreeHealthThreshholds_DataServer>(em))
            list.Add(nameof(Script_ApplyBuffUnderThreeHealthThreshholds_DataServer));
        return list;
    }

    internal static bool TryHealth(EntityManager em, Entity unit, out float value, out float max, out bool dead)
    {
        value = max = 0f;
        dead = false;
        byte* p = RO<Health>(em, unit, Layout.HealthIsDeadOffset + 1);
        if (p == null) return false;
        max = *(float*)(p + Layout.HealthMaxOffset);
        value = *(float*)(p + Layout.HealthValueOffset);
        dead = p[Layout.HealthIsDeadOffset] != 0;
        return true;
    }

    /// <param name="originalsFrom">
    /// For a unit restored from a save its current values are already the variant's; pass the boss
    /// prefab so the undo restores the prefab's values instead. <c>Entity.Null</c> keeps what was there.
    /// </param>
    internal static Action SetPhase(World world, Entity unit, float fraction, int? buffGuid, Entity originalsFrom = default)
    {
        var em = world.EntityManager;
        RequireAlive(em, unit);
        byte[]? prefabBytes = null;
        if (originalsFrom != Entity.Null)
        {
            byte* q = RO<Script_ApplyBuffUnderHealthThreshold_DataServer>(em, originalsFrom, PhaseScriptCodec.MinBytes);
            if (q != null) prefabBytes = new ReadOnlySpan<byte>(q, PhaseScriptCodec.MinBytes).ToArray();
        }
        byte* p = RW<Script_ApplyBuffUnderHealthThreshold_DataServer>(em, unit, PhaseScriptCodec.MinBytes);
        float factor = TryHealth(em, unit, out float hp, out float max, out _) && max > 0f ? hp / max : 1f;
        var patches = PhaseScriptCodec.Write(new Span<byte>(p, PhaseScriptCodec.MinBytes), fraction, buffGuid, factor);
        if (prefabBytes != null)
        {
            for (int i = 0; i < patches.Count; i++)
            {
                var patch = patches[i];
                if (patch.Offset == Layout.PhaseScriptThresholdMetOffset) continue;
                patches[i] = patch.WithBefore(prefabBytes.AsSpan(patch.Offset, patch.After.Length).ToArray());
            }
        }
        int epoch = WorldEpoch.Current;
        return () =>
        {
            if (WorldEpoch.IsStale(epoch)) return;
            var w = Worlds.Server;
            if (w == null || !w.IsCreated || !w.EntityManager.Exists(unit)) return;
            byte* q;
            try { q = RW<Script_ApplyBuffUnderHealthThreshold_DataServer>(w.EntityManager, unit, PhaseScriptCodec.MinBytes); }
            catch { return; }
            var span = new Span<byte>(q, PhaseScriptCodec.MinBytes);
            // ThresholdMet is live state: once the buff fired it stays fired, whatever this edit wrote.
            for (int i = patches.Count - 1; i >= 0; i--)
                if (patches[i].Offset != Layout.PhaseScriptThresholdMetOffset) patches[i].Restore(span);
        };
    }

    // ---- team and faction -----------------------------------------------------------------------

    internal static bool TryTeam(EntityManager em, Entity entity, out int value, out int factionIndex)
    {
        value = factionIndex = 0;
        byte* p = RO<Team>(em, entity, Layout.TeamFactionIndexOffset + sizeof(int));
        if (p == null) return false;
        value = *(int*)(p + Layout.TeamValueOffset);
        factionIndex = *(int*)(p + Layout.TeamFactionIndexOffset);
        return true;
    }

    internal static AiFactionInfo? FactionFromPrefab(World world, Entity factionPrefab, int guid)
    {
        var em = world.EntityManager;
        byte* p = RO<Faction>(em, factionPrefab, Layout.FactionDataOffset + sizeof(long));
        if (p == null) return null;
        byte* blob = BlobData(p + Layout.FactionDataOffset);
        if (blob == null || BlobLength(blob) < Layout.FactionBlobIndexOffset + sizeof(int)) return null;
        return new AiFactionInfo(
            Name(world, guid),
            guid,
            *(int*)(blob + Layout.FactionBlobIndexOffset),
            *(int*)(blob + Layout.FactionBlobMembershipOffset),
            *(int*)(blob + Layout.FactionBlobFriendlyOffset),
            *(int*)(blob + Layout.FactionBlobNeutralOffset),
            *(int*)(blob + Layout.FactionBlobHostileOffset));
    }

    internal static AiFactionInfo RequireFaction(World world, string spelling)
    {
        var entity = RequirePrefab(world, spelling, "faction", out int guid, out _);
        return FactionFromPrefab(world, entity, guid)
            ?? throw new InvalidOperationException("'" + spelling + "' is not a faction prefab (no Faction component).");
    }

    internal static AiFactionInfo? FactionOfReference(World world, Entity unitOrPrefab)
    {
        var em = world.EntityManager;
        byte* p = RO<FactionReference>(em, unitOrPrefab, Layout.FactionReferenceGuidOffset + sizeof(int));
        if (p == null) return null;
        int guid = *(int*)(p + Layout.FactionReferenceGuidOffset);
        var entity = PrefabEntity(world, guid);
        return entity == Entity.Null ? null : FactionFromPrefab(world, entity, guid);
    }

    private static int _factionEpoch;
    private static Dictionary<int, AiFactionInfo>? _factions;

    /// <summary>Every faction prefab by runtime index (one prefab-inclusive query per world epoch).</summary>
    internal static Dictionary<int, AiFactionInfo> Factions(World world)
    {
        if (_factions != null && _factionEpoch == WorldEpoch.Current) return _factions;
        var table = new Dictionary<int, AiFactionInfo>();
        var em = world.EntityManager;
        var entities = Query.EntitiesWithOptions(
            em,
            EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            ComponentIndex.Of<Faction>(ComponentType.AccessMode.ReadOnly));
        foreach (var e in entities)
        {
            var info = FactionFromPrefab(world, e, PrefabGuidOf(em, e));
            if (info != null && !table.ContainsKey(info.Index)) table[info.Index] = info;
        }
        _factions = table;
        _factionEpoch = WorldEpoch.Current;
        return table;
    }

    internal static bool TryFactionReference(EntityManager em, Entity unit, out int guid)
    {
        guid = 0;
        byte* p = RO<FactionReference>(em, unit, Layout.FactionReferenceGuidOffset + sizeof(int));
        if (p == null) return false;
        guid = *(int*)(p + Layout.FactionReferenceGuidOffset);
        return true;
    }

    /// <summary>
    /// Put one unit on <paramref name="faction"/>: its <c>FactionReference</c> (the source the game
    /// derives <c>Team.FactionIndex</c> from at spawn; live: a prefab reference to Faction_Bandits
    /// made new spawns born with index 0, while editing the prefab's Team index did nothing) and its
    /// <c>Team.FactionIndex</c> (the field ally checks read; a post-spawn write held for minutes
    /// live). The undo restores each field that still holds what this call wrote.
    /// </summary>
    internal static Action SetFaction(World world, Entity unit, AiFactionInfo faction, Entity originalsFrom = default)
    {
        var em = world.EntityManager;
        RequireAlive(em, unit);
        int teamExtent = Layout.TeamFactionIndexOffset + sizeof(int);
        int refExtent = Layout.FactionReferenceGuidOffset + sizeof(int);
        int? prefabGuid = null, prefabIndex = null;
        if (originalsFrom != Entity.Null)
        {
            if (TryFactionReference(em, originalsFrom, out int g))
            {
                prefabGuid = g;
                var entity = PrefabEntity(world, g);
                var info = entity == Entity.Null ? null : FactionFromPrefab(world, entity, g);
                if (info != null) prefabIndex = info.Index;
            }
            if (prefabIndex == null && TryTeam(em, originalsFrom, out _, out int idx)) prefabIndex = idx;
        }
        BytePatch? reference = null;
        if (unit.Has<FactionReference>(em))
        {
            byte* r = RW<FactionReference>(em, unit, refExtent);
            reference = BytePatch.Write(new Span<byte>(r, refExtent), Layout.FactionReferenceGuidOffset, BytePatch.Int(faction.Guid));
            if (prefabGuid is int pg) reference = reference.WithBefore(BytePatch.Int(pg));
        }
        byte* p = RW<Team>(em, unit, teamExtent);
        var team = BytePatch.Write(new Span<byte>(p, teamExtent), Layout.TeamFactionIndexOffset, BytePatch.Int(faction.Index));
        if (prefabIndex is int pi) team = team.WithBefore(BytePatch.Int(pi));
        int epoch = WorldEpoch.Current;
        return () =>
        {
            if (WorldEpoch.IsStale(epoch)) return;
            var w = Worlds.Server;
            if (w == null || !w.IsCreated || !w.EntityManager.Exists(unit)) return;
            try
            {
                byte* q = RW<Team>(w.EntityManager, unit, teamExtent);
                team.Restore(new Span<byte>(q, teamExtent));
            }
            catch { }
            if (reference == null) return;
            try
            {
                byte* q = RW<FactionReference>(w.EntityManager, unit, refExtent);
                reference.Restore(new Span<byte>(q, refExtent));
            }
            catch { }
        };
    }

    /// <summary>True when the unit's <c>FactionReference</c> (if it has one) and <c>Team.FactionIndex</c> both name <paramref name="faction"/>.</summary>
    internal static bool HasFaction(EntityManager em, Entity unit, AiFactionInfo faction)
    {
        if (!TryTeam(em, unit, out _, out int index) || index != faction.Index) return false;
        return !unit.Has<FactionReference>(em) || (TryFactionReference(em, unit, out int guid) && guid == faction.Guid);
    }

    // ---- observation ------------------------------------------------------------------------------

    internal static int BehaviourState(EntityManager em, Entity unit)
    {
        byte* p = RO<BehaviourTreeState>(em, unit, Layout.BehaviourTreeStateValueOffset + sizeof(int));
        return p == null ? 0 : *(int*)(p + Layout.BehaviourTreeStateValueOffset);
    }

    internal static Entity AggroTarget(EntityManager em, Entity unit)
    {
        byte* p = RO<AggroConsumer>(em, unit, Layout.AggroConsumerAggroTargetOffset + sizeof(long));
        return p == null ? Entity.Null : ReadEntity(p + Layout.AggroConsumerAggroTargetOffset);
    }

    internal static int CastingGroup(EntityManager em, Entity unit)
    {
        byte* p = RO<AbilityBar_Shared>(em, unit, Layout.AbilityBarCastGroupPrefabGuidOffset + sizeof(int));
        return p == null ? 0 : *(int*)(p + Layout.AbilityBarCastGroupPrefabGuidOffset);
    }

    internal static List<AiCastHistoryEntry> History(World world, Entity unit, int max)
    {
        var list = new List<AiCastHistoryEntry>();
        byte* raw = Buffer<CastHistoryBufferElement>(world.EntityManager, unit, Layout.CastHistoryStride, write: false, out int length);
        for (int i = 0; raw != null && i < length && i < max; i++)
            list.Add(CastHistoryCodec.Decode(new ReadOnlySpan<byte>(raw + i * Layout.CastHistoryStride, Layout.CastHistoryStride), g => Name(world, g)));
        return list;
    }

    // ---- buffs (variant markers) -----------------------------------------------------------------

    internal static List<int> BuffGuids(EntityManager em, Entity unit)
    {
        var list = new List<int>();
        try
        {
            if (!unit.Has<BuffBuffer>(em)) return list;
            var buffer = em.GetBuffer<BuffBuffer>(unit, true);
            for (int i = 0; i < buffer.Length; i++) list.Add(buffer[i].PrefabGuid.GuidHash);
        }
        catch { }
        return list;
    }

    internal static Entity FindBuff(EntityManager em, Entity unit, int guid)
    {
        try
        {
            if (!unit.Has<BuffBuffer>(em)) return Entity.Null;
            var buffer = em.GetBuffer<BuffBuffer>(unit, true);
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i].PrefabGuid.GuidHash == guid) return buffer[i].Entity;
        }
        catch { }
        return Entity.Null;
    }

    /// <summary>
    /// Queue the marker buff through <c>DebugEventsSystem.ApplyBuff</c> with the unit as both User and
    /// Character — the Dev <c>buff</c> op's route for NPCs, used live on Frostmaw during the scale test.
    /// </summary>
    internal static void ApplyBuff(World world, Entity unit, int guid)
    {
        var em = world.EntityManager;
        if (FindBuff(em, unit, guid) != Entity.Null) return;
        var des = world.GetExistingSystemManaged<DebugEventsSystem>()
            ?? throw new InvalidOperationException("DebugEventsSystem is not in the server world yet.");
        des.ApplyBuff(
            new FromCharacter { User = unit, Character = unit },
            new ApplyBuffDebugEvent { BuffPrefabGUID = new PrefabGUID(guid), Stacks = 1 });
    }

    internal static bool RemoveBuff(World world, Entity unit, int guid)
    {
        var em = world.EntityManager;
        var instance = FindBuff(em, unit, guid);
        if (instance == Entity.Null || !em.Exists(instance)) return false;
        DestroyUtility.Destroy(em, instance, DestroyDebugReason.TryRemoveBuff);
        return true;
    }

    // ---- cast-option prefab clones (experimental) ------------------------------------------------

    internal sealed class CoClone
    {
        internal int Guid;
        internal int SourceGuid;
        internal int Epoch;
        internal Entity Root;
        internal readonly List<(Entity Source, Entity Clone)> Options = new();
    }

    private static readonly Dictionary<int, CoClone> Clones = new();

    /// <summary>
    /// A private copy of a cast-option prefab chain (CO root, one holder per state, one option
    /// entity per group), registered under <paramref name="guid"/> in
    /// <c>_PrefabGuidToEntityMap</c>. Every copy is Prefab-tagged. A second call for the same GUID in
    /// the same world re-copies the rows from the source instead of cloning again. Clones are never
    /// destroyed and never unmapped: a unit that still names the GUID must keep resolving it.
    /// </summary>
    internal static CoClone CloneCastOptions(World world, int sourceGuid, int guid)
    {
        var em = world.EntityManager;
        var source = PrefabEntity(world, sourceGuid);
        if (source == Entity.Null) throw new InvalidOperationException("cast-option prefab " + sourceGuid + " is gone.");

        if (Clones.TryGetValue(guid, out var existing) && existing.Epoch == WorldEpoch.Current && em.Exists(existing.Root))
        {
            if (existing.SourceGuid != sourceGuid) throw new InvalidOperationException("cast-option clone " + guid + " was made from another source.");
            ResyncRows(em, existing);
            return existing;
        }

        // Read the whole source chain before any structural change.
        var holders = new List<(Entity Source, List<Entity> Options)>();
        foreach (var (_, holder) in States(em, source))
        {
            if (!em.Exists(holder)) throw new InvalidOperationException("cast-option holder " + Fmt(holder) + " is gone.");
            holders.Add((holder, Groups(em, holder)));
        }
        var originals = new HashSet<Entity>(Chain(source, holders));
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>()
            ?? throw new InvalidOperationException("PrefabCollectionSystem is missing.");

        // A Core hot swap empties Clones but not the prefab maps: adopt the mapped root when it is
        // one of ours (the GUID is derived from the plugin id and variant name, and the root is a
        // Prefab-tagged copy of this source's chain, not the source itself).
        if (PrefabLookup.TryGetEntity(world, new PrefabGUID(guid), out var mapped) && mapped != Entity.Null && em.Exists(mapped))
        {
            var adopted = new CoClone { Guid = guid, SourceGuid = sourceGuid, Epoch = WorldEpoch.Current, Root = mapped };
            if (originals.Contains(mapped) || !mapped.Has<PrefabTag>(em) || PrefabGuidOf(em, mapped) != guid
                || !TryPairClone(em, mapped, holders, originals, allowed: null, adopted, out string why))
                throw new InvalidOperationException("prefab GUID " + guid + " is mapped to " + Fmt(mapped) + ", which is not a cast-option clone of " + Name(world, sourceGuid) + " (" + WhyNot(em, mapped, originals, guid) + "); pick another variant name.");
            Clones[guid] = adopted;
            ResyncRows(em, adopted);
            Plugin.Logger?.LogInfo("Ai: adopted cast-option clone " + guid + " root=" + Fmt(mapped) + " (made by an earlier Core generation); rows re-copied from the source");
            return adopted;
        }

        var clone = new CoClone { Guid = guid, SourceGuid = sourceGuid, Epoch = WorldEpoch.Current };
        var created = new List<Entity>();
        bool registered = false;
        try
        {
            if (source.Has<LinkedEntityGroup>(em))
            {
                // Instantiate clones the whole linked group and remaps Entity references inside it
                // (live: CO_Wendigo's root has one). Verify the remap rather than assume it.
                clone.Root = em.Instantiate(source);
                created.AddRange(LinkedGroup(em, clone.Root));
                if (!created.Contains(clone.Root)) created.Insert(0, clone.Root);
                Plugin.Logger?.LogInfo("Ai: cast-option prefab " + Name(world, sourceGuid) + " has a LinkedEntityGroup; instantiated as a group of " + created.Count + " entit(ies)");
                if (!TryPairClone(em, clone.Root, holders, originals, new HashSet<Entity>(created), clone, out string why))
                    throw new InvalidOperationException("instantiated cast-option group was not remapped: " + why + "; refusing to use it.");
                foreach (var e in created) EnsurePrefabTag(em, e);
            }
            else
            {
                foreach (var e in originals)
                {
                    if (e != source && e.Has<LinkedEntityGroup>(em))
                        throw new InvalidOperationException("cast-option entity " + Fmt(e) + " has its own LinkedEntityGroup; refusing to clone it.");
                }
                clone.Root = em.Instantiate(source);
                created.Add(clone.Root);
                var holderClones = new List<(Entity Clone, List<Entity> OptionClones)>();
                foreach (var (holder, options) in holders)
                {
                    var hc = em.Instantiate(holder);
                    created.Add(hc);
                    var oc = new List<Entity>();
                    foreach (var option in options)
                    {
                        var c = em.Instantiate(option);
                        created.Add(c);
                        oc.Add(c);
                        clone.Options.Add((option, c));
                    }
                    holderClones.Add((hc, oc));
                }
                foreach (var e in created) EnsurePrefabTag(em, e);

                // Structural changes are done; buffer pointers taken from here on stay valid.
                byte* stateRaw = Buffer<CastOptionStateBuffer>(em, clone.Root, Layout.CastOptionStateBufferStride, write: true, out int stateCount);
                if (stateRaw == null || stateCount != holderClones.Count) throw new InvalidOperationException("cloned state buffer does not match the source.");
                for (int s = 0; s < stateCount; s++)
                    WriteEntity(stateRaw + s * Layout.CastOptionStateBufferStride + Layout.CastOptionStateBufferEntityOffset, holderClones[s].Clone);
                foreach (var (hc, oc) in holderClones)
                {
                    byte* groupRaw = Buffer<CastOptionGroupBuffer>(em, hc, Layout.CastOptionGroupBufferStride, write: true, out int groupCount);
                    if (groupRaw == null || groupCount != oc.Count) throw new InvalidOperationException("cloned group buffer does not match the source.");
                    for (int g = 0; g < groupCount; g++)
                        WriteEntity(groupRaw + g * Layout.CastOptionGroupBufferStride + Layout.CastOptionGroupBufferEntityOffset, oc[g]);
                }
            }

            if (clone.Root.Has<PrefabGUID>(em)) em.SetComponentData(clone.Root, new PrefabGUID(guid));
            // Same three rows the content registry writes for its clones: EvaluateCastOptions
            // resolves CastOptionsUser through PrefabLookupMap.TryGetConvertedPrefab.
            Progenitor.Content.PrefabApplicator.AddCloneLookups(
                prefabs, new PrefabGUID(sourceGuid), new PrefabGUID(guid), clone.Root, "ai.co." + guid,
                out bool addedEntity, out bool addedAsset, out bool addedState);
            registered = addedEntity || addedAsset || addedState;
            if (!addedEntity || !addedState)
                throw new InvalidOperationException("could not register cast-option clone GUID " + guid + " (entity=" + addedEntity + " asset=" + addedAsset + " state=" + addedState + ").");
        }
        catch
        {
            if (registered) Progenitor.Content.PrefabApplicator.RemoveCloneLookups(prefabs, new PrefabGUID(guid));
            foreach (var e in created)
            {
                try { if (em.Exists(e)) em.DestroyEntity(e); } catch { }
            }
            throw;
        }
        Clones[guid] = clone;
        Plugin.Logger?.LogInfo("Ai: cast-option clone " + guid + " of " + Name(world, sourceGuid) + " root=" + Fmt(clone.Root) + " options=" + clone.Options.Count + " (experimental; never destroyed)");
        return clone;
    }

    private static List<Entity> LinkedGroup(EntityManager em, Entity root)
    {
        var list = new List<Entity>();
        byte* raw = Buffer<LinkedEntityGroup>(em, root, Layout.LinkedEntityGroupStride, write: false, out int length);
        for (int i = 0; raw != null && i < length && i < 1024; i++)
            list.Add(ReadEntity(raw + i * Layout.LinkedEntityGroupStride + Layout.LinkedEntityGroupValueOffset));
        return list;
    }

    /// <summary>
    /// Walk a copied chain beside the source chain: same state and group counts, and no reference
    /// that still names a source entity (or, with <paramref name="allowed"/>, one outside the copied
    /// group). Fills <see cref="CoClone.Options"/> with (source option, copy option) pairs.
    /// </summary>
    private static bool TryPairClone(
        EntityManager em,
        Entity root,
        List<(Entity Source, List<Entity> Options)> holders,
        HashSet<Entity> originals,
        HashSet<Entity>? allowed,
        CoClone clone,
        out string why)
    {
        why = "";
        clone.Options.Clear();
        var states = States(em, root);
        if (states.Count != holders.Count) { why = "state count " + states.Count + " vs " + holders.Count; return false; }
        for (int s = 0; s < states.Count; s++)
        {
            var holder = states[s].Holder;
            if (originals.Contains(holder)) { why = "state " + s + " still names the source holder " + Fmt(holder); return false; }
            if (allowed != null && !allowed.Contains(holder)) { why = "state " + s + " names " + Fmt(holder) + ", outside the copied group"; return false; }
            var options = Groups(em, holder);
            if (options.Count != holders[s].Options.Count) { why = "group count of state " + s + " is " + options.Count + " vs " + holders[s].Options.Count; return false; }
            for (int g = 0; g < options.Count; g++)
            {
                if (originals.Contains(options[g])) { why = "group " + g + " still names the source option " + Fmt(options[g]); return false; }
                if (allowed != null && !allowed.Contains(options[g])) { why = "group " + g + " names " + Fmt(options[g]) + ", outside the copied group"; return false; }
                clone.Options.Add((holders[s].Options[g], options[g]));
            }
        }
        Plugin.Logger?.LogInfo("Ai: cast-option copy " + Fmt(root) + ": " + states.Count + " state(s), " + clone.Options.Count + " option entit(ies), every reference points into the copy");
        return true;
    }

    private static string WhyNot(EntityManager em, Entity mapped, HashSet<Entity> originals, int guid)
    {
        if (originals.Contains(mapped)) return "it is the source itself";
        if (!mapped.Has<PrefabTag>(em)) return "it is not Prefab-tagged";
        if (PrefabGuidOf(em, mapped) != guid) return "its PrefabGUID is " + PrefabGuidOf(em, mapped);
        return "its chain does not match the source";
    }

    private static IEnumerable<Entity> Chain(Entity root, List<(Entity Source, List<Entity> Options)> holders)
    {
        yield return root;
        foreach (var (holder, options) in holders)
        {
            yield return holder;
            foreach (var option in options) yield return option;
        }
    }

    private static void ResyncRows(EntityManager em, CoClone clone)
    {
        int stride = CastOptionCodec.Stride;
        foreach (var (source, copy) in clone.Options)
        {
            byte* from = Buffer<CastOptionBuffer>(em, source, stride, write: false, out int n);
            byte* to = Buffer<CastOptionBuffer>(em, copy, stride, write: true, out int m);
            if (from == null || to == null || n != m || n > MaxRows) throw new InvalidOperationException("cast-option clone rows no longer match the source.");
            new ReadOnlySpan<byte>(from, n * stride).CopyTo(new Span<byte>(to, m * stride));
        }
    }

    internal static Action AssignCastOptions(World world, Entity unit, int guid, Entity originalsFrom = default)
    {
        var em = world.EntityManager;
        RequireAlive(em, unit);
        int need = Layout.CastOptionsUserPrefabOffset + sizeof(int);
        int prefabCo = originalsFrom == Entity.Null ? 0 : CastOptionsGuid(em, originalsFrom);
        byte* p = RW<CastOptionsUser>(em, unit, need);
        var patch = BytePatch.Write(new Span<byte>(p, need), Layout.CastOptionsUserPrefabOffset, BytePatch.Int(guid));
        if (prefabCo != 0) patch = patch.WithBefore(BytePatch.Int(prefabCo));
        int epoch = WorldEpoch.Current;
        return () =>
        {
            if (WorldEpoch.IsStale(epoch)) return;
            var w = Worlds.Server;
            if (w == null || !w.IsCreated || !w.EntityManager.Exists(unit)) return;
            try
            {
                byte* q = RW<CastOptionsUser>(w.EntityManager, unit, need);
                patch.Restore(new Span<byte>(q, need));
            }
            catch { }
        };
    }
}
#endif
