using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ai;

// Game-free half of Core's ally-safe hit default (docs/ai.md, "Ally-safe damaging hits"). Every
// offset is a Layout constant resolved against the server dump. The live half (AllySafety.cs)
// hands these methods spans over native blob memory; the tests hand them byte arrays.
//
// Evidence: the 2026-09-24 boss AI tree review, internal research notes (the prepend was
// applied by hand to Frostmaw Ice Beam and Bishop of Dunley and removed their friendly damage).

/// <summary>What Core did with one damaging hit prefab.</summary>
public enum AllySafetyState
{
    /// <summary>Every hit slot without a team check points at a guarded private copy.</summary>
    Protected,

    /// <summary>Left vanilla by a built-in rule (environmental hazard or player ability).</summary>
    ExemptBuiltIn,

    /// <summary>Left vanilla (or restored) because a plugin holds <c>ctx.Ai.AllySafety.Exempt</c>.</summary>
    ExemptByPlugin,

    /// <summary>Left vanilla because <c>[Ai] AllySafeHits</c> is false and no plugin protects it.</summary>
    Disabled,

    /// <summary>A hit condition could not be read or copied; that slot is unchanged.</summary>
    Failed,

    /// <summary>Core would guard it but has not run a pass since (read-only <c>Describe</c> only).</summary>
    Pending,
}

/// <summary>
/// One damaging hit prefab that has at least one damaging hit slot without a team check. Slot counts
/// are damaging slots only.
/// </summary>
public sealed class AllySafetyEntry
{
    public string Name { get; internal set; } = "";
    public int Guid { get; internal set; }
    public AllySafetyState State { get; internal set; }

    /// <summary>Why it is in <see cref="State"/> (the exemption rule, the holder, or the failure).</summary>
    public string Reason { get; internal set; } = "";

    /// <summary>Hit slots whose condition has no team source, still vanilla.</summary>
    public int TeamBlindSlots { get; internal set; }

    /// <summary>Hit slots with no condition at all, still vanilla.</summary>
    public int NullConditionSlots { get; internal set; }

    /// <summary>Hit slots that point at a guarded copy (<c>NOT IsAlly(Target)</c> first).</summary>
    public int GuardedSlots { get; internal set; }

    /// <summary>Of <see cref="GuardedSlots"/>, how many had no condition before.</summary>
    public int GuardedNullSlots { get; internal set; }

    public override string ToString() =>
        Name + " (" + Guid.ToString(CultureInfo.InvariantCulture) + "): " + State
        + (Reason.Length > 0 ? " — " + Reason : "")
        + " [guarded " + GuardedSlots.ToString(CultureInfo.InvariantCulture)
        + ", team-blind " + TeamBlindSlots.ToString(CultureInfo.InvariantCulture)
        + ", null " + NullConditionSlots.ToString(CultureInfo.InvariantCulture) + "]";
}

/// <summary>Counts from one pass over the server's damaging hit prefabs.</summary>
public sealed class AllySafetyReport
{
    /// <summary><c>[Ai] AllySafeHits</c> at the time of the pass.</summary>
    public bool Enabled { get; internal set; }

    /// <summary>Prefabs with <c>CreateGameplayEventsOnHit</c> and <c>DealDamageOnGameplayEvent</c>.</summary>
    public int DamagingPrefabs { get; internal set; }

    /// <summary>
    /// Damaging prefabs that have damaging hit slots and whose every damaging slot already tests the
    /// team.
    /// </summary>
    public int TeamAwarePrefabs { get; internal set; }

    /// <summary>Damaging prefabs none of whose hit slots is heard by a DealDamage listener (never touched).</summary>
    public int PrefabsWithoutDamagingSlot { get; internal set; }

    /// <summary>Hit slots whose event a DealDamage listener on the same prefab hears.</summary>
    public int DamagingSlots { get; internal set; }

    /// <summary>Of <see cref="DamagingSlots"/>, slots whose condition already tests the team.</summary>
    public int TeamAwareSlots { get; internal set; }

    /// <summary>Hit slots that trigger no DealDamage listener; never guarded.</summary>
    public int NonDamagingSlots { get; internal set; }

    public int ProtectedPrefabs { get; internal set; }
    public int GuardedSlots { get; internal set; }

    /// <summary>Of <see cref="GuardedSlots"/>, slots that had no condition and got a fresh one.</summary>
    public int GuardedNullSlots { get; internal set; }

    /// <summary>Slots this pass pointed at a guarded copy (the rest were already guarded).</summary>
    public int NewlyGuardedSlots { get; internal set; }

    /// <summary>Slots this pass pointed back at their original condition.</summary>
    public int RestoredSlots { get; internal set; }

    public int ExemptBuiltIn { get; internal set; }
    public int ExemptByPlugin { get; internal set; }
    public int ExemptByConfig { get; internal set; }
    public int ExemptPrefabs => ExemptBuiltIn + ExemptByPlugin + ExemptByConfig;
    public int Failures { get; internal set; }

    /// <summary>Every damaging prefab that is not simply team-aware.</summary>
    public List<AllySafetyEntry> Entries { get; } = new();

    /// <summary>The one-line summary Core logs.</summary>
    public string Summary() =>
        (Enabled ? "on" : "off (config)") + ": "
        + DamagingPrefabs.ToString(CultureInfo.InvariantCulture) + " prefab(s) with hit and damage buffers, "
        + DamagingSlots.ToString(CultureInfo.InvariantCulture) + " damaging hit slot(s) ("
        + TeamAwareSlots.ToString(CultureInfo.InvariantCulture) + " team-aware; "
        + NonDamagingSlots.ToString(CultureInfo.InvariantCulture) + " non-damaging slot(s) ignored), "
        + TeamAwarePrefabs.ToString(CultureInfo.InvariantCulture) + " team-aware prefab(s), "
        + PrefabsWithoutDamagingSlot.ToString(CultureInfo.InvariantCulture) + " without a damaging slot; guarded "
        + ProtectedPrefabs.ToString(CultureInfo.InvariantCulture) + " prefab(s) / "
        + GuardedSlots.ToString(CultureInfo.InvariantCulture) + " slot(s) ("
        + GuardedNullSlots.ToString(CultureInfo.InvariantCulture) + " null-condition, "
        + NewlyGuardedSlots.ToString(CultureInfo.InvariantCulture) + " new this pass); exempt "
        + ExemptPrefabs.ToString(CultureInfo.InvariantCulture) + " (built-in "
        + ExemptBuiltIn.ToString(CultureInfo.InvariantCulture) + ", plugin "
        + ExemptByPlugin.ToString(CultureInfo.InvariantCulture) + ", config "
        + ExemptByConfig.ToString(CultureInfo.InvariantCulture) + "); restored "
        + RestoredSlots.ToString(CultureInfo.InvariantCulture) + " slot(s); "
        + Failures.ToString(CultureInfo.InvariantCulture) + " failure(s)";

    public override string ToString()
    {
        var sb = new StringBuilder(Summary());
        foreach (var entry in Entries) sb.Append('\n').Append("  ").Append(entry);
        return sb.ToString();
    }
}

/// <summary>How one <c>CreateGameplayEventsOnHit</c> slot's condition reads.</summary>
internal enum HitSlotKind
{
    /// <summary>Some element tests IsAlly, IsEnemy, IsInUnitTeam or IsInNeutralTeam.</summary>
    TeamAware,

    /// <summary>A condition with no team source: it hits allies.</summary>
    TeamBlind,

    /// <summary>No condition blob: the hit always passes, allies included.</summary>
    NullCondition,

    /// <summary>One of Core's guarded copies (any Core generation).</summary>
    Guarded,

    /// <summary>The blob does not parse; never touched.</summary>
    Unreadable,
}

/// <summary>
/// Hit-condition bytes: classification, the caster-scoped guard prepend, the fresh condition for
/// a slot with none, and the trailer that marks a copy as Core's.
///
/// <para><b>Copy layout.</b> The original blob's bytes, unchanged (its <c>ConditionInfo</c> strings are
/// self-relative, so they stay valid), then at the 8-aligned end the new element array — the three
/// guard elements (caster marked? then <c>NOT IsAlly(Target)</c>), then the original elements with
/// every index shifted by three — then a 16-byte trailer: <see cref="MagicV2"/> and the original
/// blob's data pointer. Copies of the first, unconditional format (<see cref="Magic"/>, one guard
/// element) are still recognized so a pass can restore or replace them. The trailer is how a later Core
/// generation recognizes a guarded slot and restores it, even when the generation that made the
/// copy is gone. The <c>Conditionals</c> header points at the new array.</para>
/// </summary>
internal static class AllySafetyCodec
{
    /// <summary>"PGNALLY1", little-endian.</summary>
    internal const long Magic = 0x31594C4C414E4750;

    /// <summary>"PGNALLY2", little-endian: the scoped (caster-conditional) guard. <see cref="Magic"/> copies are still recognized and restored.</summary>
    internal const long MagicV2 = 0x32594C4C414E4750;

    internal const int TrailerBytes = 16;

    /// <summary>Header hash perturbation, the value the live experiment used, so a copy never shares its original's hash.</summary>
    internal const ulong HashSalt = 0x5A11FE5AFEUL;

    /// <summary><c>Allocator.Persistent</c> as an <c>AllocatorHandle</c> (Index 4, Version 0): what a fresh condition is allocated with.</summary>
    internal const int PersistentAllocatorHandle = 4;

    /// <summary>Longest walk <see cref="Evaluate"/> allows before it calls the graph cyclic.</summary>
    internal const int MaxSteps = 4096;

    /// <summary>
    /// <c>CreateGameplayEventsOnHit</c> row stride: <c>ColliderCastIndex</c> is the last field, and the
    /// blob pointer at 0x10 makes the row 8-aligned, so 0x18 + 4 rounded up to 8 = 0x20.
    /// </summary>
    internal static int HitRowStride => RoundUp(Layout.CreateGameplayEventsOnHitColliderCastIndexOffset + sizeof(int), 8);

    /// <summary>
    /// <c>GameplayEventListeners</c> row stride: <c>GameplayEventId</c> at 0x14 is the last field, and
    /// the blob pointer at 0x8 makes the row 8-aligned, so 0x14 + 8 rounded up to 8 = 0x20.
    /// </summary>
    internal static int ListenerStride => RoundUp(Layout.GameplayEventListenersEventIdOffset + Layout.GameplayEventIdSize, 8);

    /// <summary>A <c>GameplayEventId</c> { GameplayEventType, EventId } at <paramref name="at"/>.</summary>
    internal static (int Type, int Id) ReadEventId(ReadOnlySpan<byte> b, int at) =>
        (Bytes.I32(b, at + Layout.GameplayEventIdTypeOffset), Bytes.I32(b, at + Layout.GameplayEventIdEventIdOffset));

    /// <summary>
    /// The events <paramref name="count"/> <c>GameplayEventListeners</c> rows route to
    /// <c>DealDamage</c>. Only direct DealDamage listeners count: a hit whose event reaches damage
    /// through a Branch, Delay or Iterate listener is not followed.
    /// </summary>
    internal static HashSet<(int Type, int Id)> DamageEvents(ReadOnlySpan<byte> listeners, int count)
    {
        var set = new HashSet<(int, int)>();
        int stride = ListenerStride;
        for (int i = 0; i < count && (i + 1) * stride <= listeners.Length; i++)
        {
            var row = listeners.Slice(i * stride, stride);
            if (Bytes.I32(row, Layout.GameplayEventListenersEventTypeOffset) == Layout.GameplayEventTypeEnumDealDamage)
                set.Add(ReadEventId(row, Layout.GameplayEventListenersEventIdOffset));
        }
        return set;
    }

    /// <summary>
    /// A <c>CreateGameplayEventsOnHit</c> row is damaging when <paramref name="damageEvents"/> holds its
    /// <c>EventId</c>, both the event type (Local / External / AllEvents) and the id.
    /// </summary>
    internal static bool IsDamagingSlot(ReadOnlySpan<byte> hitRow, HashSet<(int Type, int Id)> damageEvents) =>
        damageEvents.Contains(ReadEventId(hitRow, Layout.CreateGameplayEventsOnHitEventIdOffset));

    private static int Stride => ConditionCodec.Stride;
    private static int SuccessAt => Layout.ConditionElementSuccessOffset;
    private static int FailureAt => Layout.ConditionElementFailureOffset;
    private static int TargetAt => Layout.ConditionElementUnionOffset + Layout.TargetBoolConditionTargetOffset;
    private static int InvertAt => Layout.ConditionElementUnionOffset + Layout.TargetBoolConditionInvertOffset;

    /// <summary>A fresh condition's header: <c>Conditionals</c> then <c>ConditionInfo</c> (ends at 0x1A), 8-aligned.</summary>
    internal static int FreshHeaderBytes =>
        RoundUp(Layout.ConditionBlobInfoOffset + Layout.ConditionInfoUsingMinionMasterChecksOffset + 1, 8);

    internal static int RoundUp(int value, int align) => (value + align - 1) / align * align;

    internal static bool IsTeamSource(int source) =>
        source == Layout.ConditionSourceIsAlly
        || source == Layout.ConditionSourceIsEnemy
        || source == Layout.ConditionSourceIsInUnitTeam
        || source == Layout.ConditionSourceIsInNeutralTeam;

    /// <summary>
    /// Classify one slot. <paramref name="isNull"/> is a null blob pointer; otherwise
    /// <paramref name="blob"/> is the blob's data bytes (length from its header).
    /// </summary>
    internal static HitSlotKind Classify(bool isNull, ReadOnlySpan<byte> blob)
    {
        if (isNull) return HitSlotKind.NullCondition;
        if (TryGuard(blob, out _)) return HitSlotKind.Guarded;
        if (!ConditionCodec.TryElements(blob, out int first, out int count)) return HitSlotKind.Unreadable;
        for (int i = 0; i < count; i++)
            if (IsTeamSource(ConditionCodec.Source(blob, first + i * Stride))) return HitSlotKind.TeamAware;
        return HitSlotKind.TeamBlind;
    }

    /// <summary><c>NOT IsAlly(Target)</c>, as Frostmaw's melee hit has it: Source 8, Target 1, Invert 1.</summary>
    internal static void WriteGuard(Span<byte> element, int success, int failure)
    {
        element.Slice(0, Stride).Clear();
        Bytes.PutI32(element, Layout.ConditionElementSourceOffset, Layout.ConditionSourceIsAlly);
        Bytes.PutI32(element, SuccessAt, success);
        Bytes.PutI32(element, FailureAt, failure);
        Bytes.PutI32(element, TargetAt, Layout.ConditionTargetTarget);
        element[InvertAt] = 1;
    }

    /// <summary><c>HasBuff(target, prefab)</c>, optionally inverted: Source 7, union <c>TargetPrefabCondition</c>.</summary>
    internal static void WriteHasBuff(Span<byte> element, int target, int prefab, bool invert, int success, int failure)
    {
        element.Slice(0, Stride).Clear();
        Bytes.PutI32(element, Layout.ConditionElementSourceOffset, Layout.ConditionSourceHasBuff);
        Bytes.PutI32(element, SuccessAt, success);
        Bytes.PutI32(element, FailureAt, failure);
        Bytes.PutI32(element, PrefabTargetAt, target);
        element[PrefabInvertAt] = invert ? (byte)1 : (byte)0;
        Bytes.PutI32(element, PrefabAt, prefab);
    }

    private static int PrefabTargetAt => Layout.ConditionElementUnionOffset + Layout.TargetPrefabConditionTargetOffset;
    private static int PrefabInvertAt => Layout.ConditionElementUnionOffset + Layout.TargetPrefabConditionInvertOffset;
    private static int PrefabAt => Layout.ConditionElementUnionOffset + Layout.TargetPrefabConditionPrefabOffset;

    /// <summary>The first-format (v1) guard: <c>NOT IsAlly(Target)</c> at index 0, success 1, failure = count.</summary>
    internal static bool IsGuard(ReadOnlySpan<byte> blob, int element, int count) =>
        IsAllyGuard(blob, element, 1, count);

    private static bool IsAllyGuard(ReadOnlySpan<byte> blob, int element, int success, int failure) =>
        ConditionCodec.Source(blob, element) == Layout.ConditionSourceIsAlly
        && Bytes.I32(blob, element + TargetAt) == Layout.ConditionTargetTarget
        && blob[element + InvertAt] != 0
        && Bytes.I32(blob, element + SuccessAt) == success
        && Bytes.I32(blob, element + FailureAt) == failure;

    private static bool IsHasBuff(ReadOnlySpan<byte> blob, int element, int target, int prefab, bool invert, int success, int failure) =>
        ConditionCodec.Source(blob, element) == Layout.ConditionSourceHasBuff
        && Bytes.I32(blob, element + PrefabTargetAt) == target
        && (blob[element + PrefabInvertAt] != 0) == invert
        && Bytes.I32(blob, element + PrefabAt) == prefab
        && Bytes.I32(blob, element + SuccessAt) == success
        && Bytes.I32(blob, element + FailureAt) == failure;

    /// <summary>Elements the scoped guard puts in front of the original chain.</summary>
    internal const int GuardElements = 3;

    /// <summary>
    /// Write the scoped guard at <paramref name="array"/> (element 0 of the new array):
    /// <list type="bullet">
    /// <item>e0 <c>HasBuff(Owner, marker)</c>: success → 2, failure → 1;</item>
    /// <item>e1 <c>HasBuff(MinionMaster, marker)</c>: success → 2, failure → 3 (the original chain);</item>
    /// <item>e2 <c>NOT IsAlly(Target)</c>: success → 3, failure → <paramref name="newCount"/> (false).</item>
    /// </list>
    /// With <paramref name="tail"/> (an empty or null original), e3 is <c>HasBuff(Target, 0)</c> inverted,
    /// success and failure terminal: "the target has no buff with GUID 0", true, so the walk's last
    /// result for a caster that passes is true, as the vanilla empty condition is.
    /// </summary>
    private static void WriteScopedGuard(Span<byte> array, int marker, int newCount, bool tail)
    {
        WriteHasBuff(array.Slice(0, Stride), Layout.ConditionTargetOwner, marker, false, 2, 1);
        WriteHasBuff(array.Slice(Stride, Stride), Layout.ConditionTargetMinionMaster, marker, false, 2, GuardElements);
        WriteGuard(array.Slice(2 * Stride, Stride), GuardElements, newCount);
        if (tail)
            WriteHasBuff(array.Slice(3 * Stride, Stride), Layout.ConditionTargetTarget, 0, true, newCount, newCount);
    }

    /// <summary>
    /// A copy of <paramref name="original"/> that only guards hits whose caster (the condition's Owner,
    /// or its MinionMaster) wears the API-spawn <paramref name="marker"/> buff:
    /// a marked caster's hit on an ally ends false; a marked caster's hit on anyone else, and every
    /// unmarked caster's hit, continues into the original chain at index 3, unchanged. Original
    /// indices shift by three; an index at or past the old count stays terminal (new count). An
    /// original with no elements gets the always-true tail (see <see cref="WriteScopedGuard"/>).
    /// Throws when the blob does not parse or an index is negative.
    /// </summary>
    internal static byte[] Prepend(ReadOnlySpan<byte> original, long originalPointer, int marker)
    {
        if (!ConditionCodec.TryElements(original, out int first, out int count))
            throw new ArgumentException("the condition blob does not parse");
        bool tail = count == 0;
        int newCount = GuardElements + count + (tail ? 1 : 0);
        int arrayAt = RoundUp(original.Length, 8);
        int length = arrayAt + newCount * Stride + TrailerBytes;
        var data = new byte[length];
        original.CopyTo(data);
        WriteScopedGuard(data.AsSpan(arrayAt, newCount * Stride), marker, newCount, tail);
        for (int i = 0; i < count; i++)
        {
            var from = original.Slice(first + i * Stride, Stride);
            var to = data.AsSpan(arrayAt + (GuardElements + i) * Stride, Stride);
            from.CopyTo(to);
            Bytes.PutI32(to, SuccessAt, Shift(Bytes.I32(from, SuccessAt), count, newCount));
            Bytes.PutI32(to, FailureAt, Shift(Bytes.I32(from, FailureAt), count, newCount));
        }
        Bytes.PutI32(data, Layout.ConditionBlobConditionalsOffset, arrayAt - Layout.ConditionBlobConditionalsOffset);
        Bytes.PutI32(data, Layout.ConditionBlobConditionalsOffset + Bytes.BlobArrayLengthOffset, newCount);
        WriteTrailer(data, MagicV2, originalPointer);
        return data;
    }

    private static int Shift(int index, int count, int newCount)
    {
        if (index < 0) throw new ArgumentException("condition index " + index + " is negative; refusing to remap it");
        return index >= count ? newCount : index + GuardElements;
    }

    /// <summary>
    /// A condition for a slot that had none: the scoped guard plus the always-true tail (4 elements).
    /// <c>ConditionInfo</c> is zero: both strings empty, no mount or minion-master checks. The
    /// trailer's original pointer is 0, which is what a restore writes back.
    /// </summary>
    internal static byte[] BuildNullCondition(int marker)
    {
        int arrayAt = FreshHeaderBytes;
        int newCount = GuardElements + 1;
        int length = arrayAt + newCount * Stride + TrailerBytes;
        var data = new byte[length];
        WriteScopedGuard(data.AsSpan(arrayAt, newCount * Stride), marker, newCount, tail: true);
        Bytes.PutI32(data, Layout.ConditionBlobConditionalsOffset, arrayAt - Layout.ConditionBlobConditionalsOffset);
        Bytes.PutI32(data, Layout.ConditionBlobConditionalsOffset + Bytes.BlobArrayLengthOffset, newCount);
        WriteTrailer(data, MagicV2, 0);
        return data;
    }

    private static void WriteTrailer(byte[] data, long magic, long originalPointer)
    {
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(data.Length - TrailerBytes, 8), magic);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(data.Length - 8, 8), originalPointer);
    }

    /// <summary>True when <paramref name="blob"/> is one of Core's guarded copies, either format.</summary>
    internal static bool TryGuard(ReadOnlySpan<byte> blob, out long originalPointer) =>
        TryGuard(blob, out originalPointer, out _, out _);

    /// <summary>
    /// True when <paramref name="blob"/> is one of Core's guarded copies. <paramref name="format"/> 1 is
    /// the first, unconditional build (<see cref="Magic"/>: <c>NOT IsAlly(Target)</c> first, failure =
    /// count); 2 is the scoped guard (<see cref="MagicV2"/>: the three elements of
    /// <see cref="WriteScopedGuard"/>, both HasBuff elements naming the same <paramref name="marker"/>).
    /// Either way the element array ends exactly at the trailer, and <paramref name="originalPointer"/>
    /// is what the slot held before (0 = null).
    /// </summary>
    internal static bool TryGuard(ReadOnlySpan<byte> blob, out long originalPointer, out int format, out int marker)
    {
        originalPointer = 0;
        format = 0;
        marker = 0;
        if (blob.Length < Bytes.BlobArrayWidth + Stride + TrailerBytes) return false;
        long magic = Bytes.I64(blob, blob.Length - TrailerBytes);
        if (magic != Magic && magic != MagicV2) return false;
        if (!ConditionCodec.TryElements(blob, out int first, out int count) || count < 1) return false;
        if (first + count * Stride != blob.Length - TrailerBytes) return false;
        if (magic == Magic)
        {
            if (!IsGuard(blob, first, count)) return false;
            format = 1;
        }
        else
        {
            if (count < GuardElements + 1) return false;
            int m = Bytes.I32(blob, first + PrefabAt);
            if (!IsHasBuff(blob, first, Layout.ConditionTargetOwner, m, false, 2, 1)) return false;
            if (!IsHasBuff(blob, first + Stride, Layout.ConditionTargetMinionMaster, m, false, 2, GuardElements)) return false;
            if (!IsAllyGuard(blob, first + 2 * Stride, GuardElements, count)) return false;
            format = 2;
            marker = m;
        }
        originalPointer = Bytes.I64(blob, blob.Length - 8);
        return true;
    }

    /// <summary>
    /// The game's walk: i = 0; r = check(i); i = r ? Success : Failure; while i is inside the array;
    /// the result is the last r. A null blob or zero elements is true. <paramref name="check"/> gets
    /// the element index. For tests and diagnostics only; the game evaluates its own copy.
    /// </summary>
    internal static bool Evaluate(ReadOnlySpan<byte> blob, Func<int, bool> check)
    {
        if (blob.Length == 0) return true;
        if (!ConditionCodec.TryElements(blob, out int first, out int count))
            throw new ArgumentException("the condition blob does not parse");
        bool result = true;
        int i = 0, steps = 0;
        while (i >= 0 && i < count)
        {
            if (++steps > MaxSteps) throw new InvalidOperationException("condition walk does not end");
            result = check(i);
            int at = first + i * Stride;
            i = result ? Bytes.I32(blob, at + SuccessAt) : Bytes.I32(blob, at + FailureAt);
        }
        return result;
    }

    // ---- the 32-byte BlobAssetHeader in front of the data -------------------------------------

    /// <summary>
    /// Header for a copy: the original's, with <c>Hash</c> perturbed so the copy is never mistaken
    /// for it. <c>ValidationPtr</c> and <c>Length</c> are written by <see cref="SealHeader"/> once the
    /// copy is allocated.
    /// </summary>
    internal static byte[] CopyHeader(ReadOnlySpan<byte> template)
    {
        if (template.Length < Layout.BlobAssetHeaderBytes) throw new ArgumentException("header is shorter than a BlobAssetHeader");
        var header = template.Slice(0, Layout.BlobAssetHeaderBytes).ToArray();
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(Layout.BlobAssetHeaderHashOffset, 8));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(Layout.BlobAssetHeaderHashOffset, 8), hash ^ HashSalt);
        return header;
    }

    /// <summary>A header for a fresh blob: Persistent allocator, <paramref name="hash"/>, padding 0.</summary>
    internal static byte[] NewHeader(ulong hash)
    {
        var header = new byte[Layout.BlobAssetHeaderBytes];
        Bytes.PutI32(header, Layout.BlobAssetHeaderAllocatorOffset, PersistentAllocatorHandle);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(Layout.BlobAssetHeaderHashOffset, 8), hash);
        return header;
    }

    /// <summary>Point the header at its data: <c>ValidationPtr</c> = data address, <c>Length</c> = data length.</summary>
    internal static void SealHeader(Span<byte> header, long dataPointer, int length)
    {
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(Layout.BlobAssetHeaderValidationPtrOffset, 8), dataPointer);
        Bytes.PutI32(header, Layout.BlobAssetHeaderLengthOffset, length);
    }

    /// <summary>FNV-1a 64 of the bytes (never 0): the hash a fresh condition's header carries.</summary>
    internal static ulong ContentHash(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h == 0 ? 1 : h;
    }
}

/// <summary>
/// Which damaging hit prefabs Core leaves vanilla, and the one decision that combines the rules,
/// the config switch and plugin holds.
/// </summary>
internal static class AllySafetyRules
{
    /// <summary>
    /// Player spell schools. Player spells are <c>AB_&lt;School&gt;_*</c>; their passives and
    /// procs are <c>&lt;School&gt;_Vampire_*</c> (<c>Storm_Vampire_Static_ChainLightning</c>).
    /// </summary>
    internal static readonly string[] SpellSchools = { "Blood", "Chaos", "Frost", "Illusion", "Storm", "Unholy" };

    /// <summary>
    /// Player weapon types. Weapon attacks are <c>AB_Vampire_&lt;Weapon&gt;_*</c>; some weapon skills
    /// are <c>AB_&lt;Weapon&gt;_*</c> (<c>AB_Spear_AThousandSpears_*</c>, <c>AB_Pistols_ExplosiveShot_*</c>).
    /// </summary>
    internal static readonly string[] Weapons =
    {
        "Axe", "Claws", "Crossbow", "Daggers", "DualHammers", "GreatSword", "Longbow", "Mace", "Pistols",
        "Pollaxe", "Reaper", "Slashers", "Spear", "Sword", "TwinBlades", "Unarmed", "Whip",
    };

    /// <summary>Other player kits: weapon coatings, Veil travel spells and shapeshift forms.</summary>
    internal static readonly string[] PlayerKitPrefixes = { "AB_Vampire_Coating_", "AB_Vampire_VeilOf", "AB_Shapeshift_" };

    /// <summary>Environmental hazards: world traps and critters that are meant to hurt anything nearby.</summary>
    internal static readonly string[] HazardPrefixes = { "EH_", "Buff_BearTrap_", "Buff_Ocean_Blowfish_", "AB_Mech_" };

    /// <summary>
    /// The built-in reason a prefab is left vanilla, or null when Core guards it. Names are matched
    /// case-sensitively, as the game spells them. <paramref name="healsOnHit"/> is true when the prefab
    /// also carries <c>HealOnGameplayEvent</c>: the hit condition gates the whole hit event, so a guard
    /// would stop an ally heal too. An unknown (empty) name is guarded: the default is protection.
    /// </summary>
    internal static string? BuiltInExemption(string? name, bool healsOnHit = false)
    {
        if (string.IsNullOrEmpty(name))
            return healsOnHit ? HealsOnHitReason : null;
        string n = name!;
        foreach (var prefix in HazardPrefixes)
            if (n.StartsWith(prefix, StringComparison.Ordinal))
                return "environmental hazard (" + prefix + "*)";
        if (n.IndexOf("PlayerAbility", StringComparison.Ordinal) >= 0)
            return "player ability (*PlayerAbility*)";
        foreach (var school in SpellSchools)
        {
            if (n.StartsWith("AB_" + school + "_", StringComparison.Ordinal))
                return "player spell (AB_" + school + "_*)";
            if (n.StartsWith(school + "_Vampire_", StringComparison.Ordinal))
                return "player spell passive (" + school + "_Vampire_*)";
        }
        foreach (var weapon in Weapons)
        {
            if (n.StartsWith("AB_Vampire_" + weapon + "_", StringComparison.Ordinal))
                return "player weapon ability (AB_Vampire_" + weapon + "_*)";
            if (n.StartsWith("AB_" + weapon + "_", StringComparison.Ordinal))
                return "player weapon skill (AB_" + weapon + "_*)";
        }
        foreach (var prefix in PlayerKitPrefixes)
            if (n.StartsWith(prefix, StringComparison.Ordinal))
                return "player kit (" + prefix + "*)";
        return healsOnHit ? HealsOnHitReason : null;
    }

    internal const string HealsOnHitReason = "heals on hit (HealOnGameplayEvent): a guard would also stop the heal on allies";

    /// <summary>
    /// Whether a prefab's team-blind slots should point at guarded copies. A plugin's Exempt always
    /// wins; a plugin's Protect overrides the built-in rules and the config switch; otherwise the
    /// switch and the built-in rules decide.
    /// </summary>
    internal static bool ShouldGuard(bool enabled, string? builtInExemption, int exemptHolds, int protectHolds)
    {
        if (exemptHolds > 0) return false;
        if (protectHolds > 0) return true;
        return enabled && builtInExemption == null;
    }

    /// <summary>
    /// Why a prefab <see cref="ShouldGuard"/> declined is left vanilla: a plugin's Exempt first, then
    /// a built-in rule, otherwise the config switch.
    /// </summary>
    internal static AllySafetyState UnguardedState(string? builtInExemption, int exemptHolds)
    {
        if (exemptHolds > 0) return AllySafetyState.ExemptByPlugin;
        if (builtInExemption != null) return AllySafetyState.ExemptBuiltIn;
        return AllySafetyState.Disabled;
    }
}
