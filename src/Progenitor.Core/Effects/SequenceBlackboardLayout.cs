using System;
using System.Buffers.Binary;

namespace Progenitor.Effects;

/// <summary>
/// Game-free layout arithmetic behind <c>SequenceBlackboard</c> (R27 finding 5).
///
/// <para>The blackboard re-point walks a blob-backed <c>BlobArray&lt;SequenceInputElement&gt;</c>
/// with raw pointers and then writes a <c>float3</c> / <c>quaternion</c> at a blob-supplied
/// byte offset into the entity's <c>DynamicBuffer&lt;BlackboardElement&gt;</c>. Every bound in
/// that walk lives here, in a type the test project compiles, so the constants and the
/// overflow behavior are asserted rather than assumed. The <c>#if</c> in
/// <c>SequenceDriverLookup.cs</c> wraps only the Il2Cpp reads and writes.</para>
///
/// <para><b>Sources.</b> <c>SequenceInputElement</c> is
/// <c>{ FixedString64Bytes Name @0; int Index @0x40; BlackboardRegisterType Type @0x44;
/// bool IsInputParameter @0x48 }</c> (client and server metadata, same layout), i.e. 0x49 bytes aligned to 4 =
/// <b>76</b>. <c>BlackboardElement</c> is a single <c>byte Value</c>
/// (client and server metadata), which is why <c>Index</c> is a byte offset and why
/// <c>GetBufferLength</c> on that buffer is both an element count and a byte count.</para>
/// </summary>
internal static class SequenceBlackboardLayout
{
    /// <summary><c>SequenceInputElement.Index</c> field offset.</summary>
    internal const int InputIndexOffset = Layout.SequenceInputElementIndexOffset;

    /// <summary><c>SequenceInputElement.Type</c> field offset.</summary>
    internal const int InputTypeOffset = Layout.SequenceInputElementTypeOffset;

    /// <summary><c>SequenceInputElement.IsInputParameter</c> field offset.</summary>
    internal const int InputIsInputParameterOffset = Layout.SequenceInputElementIsInputParameterOffset;

    /// <summary>
    /// Bytes in <c>SequenceInputElement.IsInputParameter</c> — one <c>bool</c>
    /// (<see cref="InputElementDumpLines"/>). The last field, so it is what sets the element's
    /// field extent.
    /// </summary>
    internal const int InputIsInputParameterBytes = 1;

    /// <summary>
    /// Alignment of the native <c>SequenceInputElement</c> array. The widest field is a 4-byte
    /// <c>int</c> / enum (<c>Index@0x40</c>, <c>Type@0x44</c>); <c>FixedString64Bytes</c> is a
    /// byte blob. So the native array rounds each element up to 4.
    /// </summary>
    internal const int InputElementAlignment = 4;

    /// <summary>
    /// The metadata citation every <c>SequenceInputElement</c> constant here is read from.
    ///
    /// <para><b>R30 finding 22.</b> <see cref="InputElementStride"/> used to be the literal
    /// <c>76</c> with no line behind it and a test fixture that rebuilt itself from the same
    /// constant, so nothing could tell a right 76 from a wrong one. The stride is now
    /// <em>derived</em> from the field offsets below, which are the only thing the dump actually
    /// states, and this string is the citation a test pins against <c>docs/effects.md</c> so the
    /// constant and the evidence for it cannot drift apart.</para>
    /// </summary>
    internal const string InputElementDumpLines = "server metadata SequenceInputElement";

    /// <summary>
    /// The <b>native</b> stride of one <c>SequenceInputElement</c> in the blob: the field extent
    /// rounded up to <see cref="InputElementAlignment"/> — 73 → <b>76</b>. <b>This, not
    /// <c>sizeof</c>, is the number the walk multiplies by</b>; see
    /// <see cref="InputElementPackedBytes"/>.
    ///
    /// <para><b>R30 finding 22.</b> Derived, never typed: the dump states four field offsets and
    /// no size, so the size is arithmetic over the offsets and the alignment, each of which the
    /// dump does state. <see cref="PaddingInvariantHolds"/> re-checks the pair at every use.</para>
    ///
    /// <para><b>R53 finding 7 (job FLAY2).</b> The arithmetic stayed here when FLAY1 migrated
    /// this type's three offsets, so the one stride in the tree with a live regression behind it
    /// was the one nothing checked against the dump. It is now
    /// <c>Layout.SequenceInputElementStride</c>. Declaring a <em>derived</em> value is safe here
    /// and only here: <c>layoutgen</c>'s <c>size</c> rule derives it from the same three offsets
    /// by the same round-up rule, so the declared 76 and the arithmetic below cannot disagree
    /// without <c>--check</c> failing — and <see cref="InputElementPackedBytes"/> stays as the
    /// local cross-check that the two really are the same arithmetic.</para>
    /// </summary>
    internal const int InputElementStride = Layout.SequenceInputElementStride;

    /// <summary>
    /// The dump's max field extent for <c>SequenceInputElement</c>: <c>IsInputParameter</c> at
    /// <c>0x48</c> plus its one byte = <b>73</b>. The native array pads each element up to
    /// <see cref="InputElementStride"/>; the Il2CppInterop-generated struct does not carry that
    /// tail padding, so the managed <c>sizeof</c> reads 73 on this build.
    ///
    /// <para><b>EV17, second pass — the live regression's real cause.</b> The client logged
    /// <c>SequenceInputElement stride is 73, not 76; blackboard re-point disabled for this
    /// build.</c> and every beam went <c>path=none</c>. The first EV17 pass had made
    /// <c>sizeof</c> the authority for element sizes, which is right for a <em>component</em>
    /// (CLAUDE.md trap 7: <c>TypeManager</c> reads 0 for its neighbors and garbage-large for
    /// <c>SizeInChunk</c>) and <b>wrong for a native array stride</b>, because the managed
    /// struct and the native array element can differ by exactly this padding. For a blob or
    /// buffer walk the authority is the dump: the field offsets (<c>Name@0</c> /
    /// <c>Index@0x40</c> / <c>Type@0x44</c> / <c>IsInputParameter@0x48</c> in the
    /// server metadata) and the aligned stride they imply. A <c>sizeof</c> of 73 is the
    /// <em>expected</em> reading here and must not close anything; it is a cross-check that
    /// logs both numbers once.</para>
    ///
    /// <para><b>Where that rule is written down (R30 finding 21).</b> <c>docs/effects.md</c>,
    /// "For a native array stride the dump is the authority; every runtime reading is a
    /// cross-check". <b>Not</b> CLAUDE.md, whose raw-write policy is about a <em>component</em>
    /// write and makes <c>sizeof</c> the authority — which is correct there, and is exactly the
    /// rule that, applied to a blob walk, produced this regression.</para>
    /// </summary>
    internal const int InputElementPackedBytes =
        InputIsInputParameterOffset + InputIsInputParameterBytes;

    /// <summary><c>BlackboardRegisterType.Float3</c>.</summary>
    internal const int TypeFloat3 = Layout.BlackboardRegisterTypeFloat3;

    /// <summary><c>BlackboardRegisterType.Quaternion</c>.</summary>
    internal const int TypeQuaternion = Layout.BlackboardRegisterTypeQuaternion;

    /// <summary>Sanity bound on the blob's input count; a real sequence has a handful.</summary>
    internal const int MaxInputs = 512;

    /// <summary>Bound on the per-sequence index cache.</summary>
    internal const int CacheCap = 512;

    /// <summary>
    /// Upper bound on <c>BlobArray.m_OffsetPtr</c>. R27 finding 5.2: the offset was validated
    /// only as <c>&gt; 0</c>, so a garbage or differently-versioned blob root produced a wild
    /// pointer before a single element was read.
    /// </summary>
    internal const int MaxBlobOffset = 1 << 20;

    /// <summary>Upper bound on the last byte the element walk may touch inside the blob.</summary>
    internal const long MaxBlobBytes = 1L << 22;

    /// <summary><c>sizeof(float3)</c>.</summary>
    internal const int Float3Bytes = 12;

    /// <summary><c>sizeof(quaternion)</c>.</summary>
    internal const int QuaternionBytes = 16;

    /// <summary>
    /// How a runtime size reading relates to the dump. The walk always uses the dump's stride;
    /// this only decides <b>what to log</b>. <b>R30 findings 5 and 9:</b> no value of this enum
    /// closes a gate — see <see cref="ReadingLogFor"/>.
    /// </summary>
    internal enum LayoutAgreement
    {
        /// <summary>The reading is the dump's padded stride. Nothing to say.</summary>
        Exact,

        /// <summary>
        /// The reading is the dump's max field extent — the managed struct without the native
        /// array's tail padding. Expected on this interop; one info line records both numbers.
        /// </summary>
        Unpadded,

        /// <summary>
        /// The reading is neither. That is evidence the dump may be <em>stale</em> and is worth
        /// a warning; it is not evidence that the walk is wrong, because the walk never
        /// multiplies by this number (R30 finding 5).
        /// </summary>
        Mismatch,

        /// <summary>
        /// Nothing could be read at all, or the module's own dump constants contradict each
        /// other (<see cref="PaddingInvariantHolds"/>). The first is a warning; the second is
        /// the one condition that really does disable the walk, and it is a compile-time fact
        /// about this source file, not a runtime reading (R30 finding 14).
        /// </summary>
        Unavailable,
    }

    /// <summary>
    /// <paramref name="value"/> rounded up to a power-of-two <paramref name="alignment"/>, or
    /// -1 when the arguments make no sense. The native array's padding rule in one place, so a
    /// stride can be derived from the dump's field offsets instead of typed (R30 finding 22).
    /// </summary>
    internal static int RoundUpToAlignment(int value, int alignment)
    {
        if (alignment <= 0 || value < 0) return -1;
        long up = (((long)value + alignment - 1) / alignment) * alignment;
        return up > int.MaxValue ? -1 : (int)up;
    }

    /// <summary>
    /// The invariant that has to hold between a dump-derived <paramref name="dumpStride"/> and
    /// the <paramref name="dumpPacked"/> field extent it was derived from:
    /// <paramref name="alignment"/> is a power of two, both sizes are positive, the extent is
    /// not larger than the stride, and the stride is exactly the extent rounded up.
    ///
    /// <para><b>R30 finding 14.</b> <see cref="Agree"/> compared its argument against two
    /// unrelated compile-time constants and never checked them against each other, so a pair
    /// that is impossible — a mistyped constant, or a game update whose new field extent happens
    /// to equal the old padded stride — read as agreement and authorized a walk on a stride
    /// nothing backed. A violated pair is now the <em>only</em> thing that can refuse a walk,
    /// and it names the pair rather than blaming a reading.</para>
    /// </summary>
    internal static bool PaddingInvariantHolds(int dumpStride, int dumpPacked, int alignment)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0) return false;
        if (dumpStride <= 0 || dumpPacked <= 0) return false;
        if (dumpPacked > dumpStride) return false;
        return dumpStride == RoundUpToAlignment(dumpPacked, alignment);
    }

    /// <summary>
    /// Compare a runtime size reading against the dump's padded stride and its unpadded field
    /// extent. Every Effects layout probe goes through this, so "the managed struct drops the
    /// native tail padding" is handled in exactly one place (EV17 second pass). Pass
    /// <paramref name="dumpPacked"/> equal to <paramref name="dumpStride"/> for a struct that
    /// has no tail padding — <c>LineRendererPositionElement</c> (int@0, int@4, float3@8 = 20,
    /// alignment 4) and <c>BlackboardElement</c> (one byte, alignment 1) are both in that class, which is why only the blob
    /// walk was hit.
    ///
    /// <para><paramref name="alignment"/> is required rather than assumed (R30 finding 14): it
    /// is what makes the two constants checkable against each other, and it differs per element
    /// type. A pair that violates <see cref="PaddingInvariantHolds"/> is
    /// <see cref="LayoutAgreement.Unavailable"/> whatever was observed.</para>
    /// </summary>
    internal static LayoutAgreement Agree(int observed, int dumpStride, int dumpPacked, int alignment)
    {
        if (!PaddingInvariantHolds(dumpStride, dumpPacked, alignment))
            return LayoutAgreement.Unavailable;
        if (observed <= 0) return LayoutAgreement.Unavailable;
        if (observed == dumpStride) return LayoutAgreement.Exact;
        if (observed == dumpPacked) return LayoutAgreement.Unpadded;
        return LayoutAgreement.Mismatch;
    }

    /// <summary>
    /// <b>The gate decision, R30 findings 5 and 9.</b> A dump-derived raw walk is allowed
    /// exactly when the module's own constants for that element are self-consistent. Nothing
    /// read at runtime appears here, because nothing read at runtime is multiplied by anything:
    /// <c>sizeof</c>, <c>TypeManager</c> and a per-entity <c>GetBufferLength</c> can only ever
    /// be evidence that the dump looks stale, and evidence is a log line.
    ///
    /// <para>Three jobs running latched a process-wide gate on a runtime reading, and each one
    /// took the whole re-point down live with <c>path=none</c> while the dump-derived walk
    /// underneath was byte-for-byte correct. The rule is <c>docs/effects.md</c>, "For a native
    /// array stride the dump is the authority" — <b>not</b> CLAUDE.md's component raw-write
    /// policy, which makes <c>sizeof</c> the authority and is right for a component and wrong
    /// here (R30 finding 21).</para>
    /// </summary>
    internal static bool WalkAllowed(int dumpStride, int dumpPacked, int alignment) =>
        PaddingInvariantHolds(dumpStride, dumpPacked, alignment);

    /// <summary>
    /// Warning written once when this file's own constants for <paramref name="typeName"/>
    /// contradict each other. The one condition that disables a walk, and a compile-time fact
    /// about this source file rather than a reading (R30 finding 14).
    /// </summary>
    internal static string ConstantsInvariantLog(
        string typeName, int dumpStride, int dumpPacked, int alignment) =>
        "Effects: " + typeName + " dump constants contradict each other (stride " + dumpStride
        + ", field extent " + dumpPacked + ", alignment " + alignment
        + "; the stride must be the extent rounded up to the alignment); raw walks over "
        + typeName + " are disabled for this build.";

    /// <summary>
    /// The one line a runtime size reading may produce, or null when there is nothing to say.
    /// <b>Never a refusal</b> (R30 findings 5 and 9): the caller logs this once and carries on
    /// with the dump's stride.
    /// </summary>
    internal static string? ReadingLogFor(
        string typeName, int observed, int dumpStride, int dumpPacked, int alignment)
    {
        if (!PaddingInvariantHolds(dumpStride, dumpPacked, alignment))
            return ConstantsInvariantLog(typeName, dumpStride, dumpPacked, alignment);
        switch (Agree(observed, dumpStride, dumpPacked, alignment))
        {
            case LayoutAgreement.Exact:
                return null;
            case LayoutAgreement.Unpadded:
                return CrossCheckNote(typeName, observed, dumpStride);
            case LayoutAgreement.Unavailable:
                return UnreadableNote(typeName, dumpStride);
            default:
                return StaleDumpNote(typeName, observed, dumpStride, dumpPacked);
        }
    }

    /// <summary>
    /// True when <see cref="ReadingLogFor"/>'s line is a warning rather than info. Only the
    /// severity moves; nothing here refuses anything.
    /// </summary>
    internal static bool ReadingLogIsWarning(
        int observed, int dumpStride, int dumpPacked, int alignment)
    {
        var agreement = Agree(observed, dumpStride, dumpPacked, alignment);
        return agreement == LayoutAgreement.Mismatch || agreement == LayoutAgreement.Unavailable;
    }

    /// <summary>
    /// Info line when a reading is the unpadded field extent — expected on this interop, and the
    /// reading the first EV17 pass mistook for a layout change.
    /// </summary>
    internal static string CrossCheckNote(string typeName, int observed, int dumpStride) =>
        "Effects: " + typeName + " sizeof is " + observed + " (the managed struct without the "
        + "native array's tail padding); the raw walk uses the dump stride " + dumpStride
        + " and stays on.";

    /// <summary>
    /// Warning when a reading is neither of the dump's two numbers. <b>R30 finding 5:</b> this
    /// line used to end "disabled for this build", and it was that refusal, not the reading,
    /// that took the module down. The walk keeps striding the dump's number — the only number it
    /// ever used — and the line is an alarm for a human, not a switch.
    /// </summary>
    internal static string StaleDumpNote(
        string typeName, int observed, int dumpStride, int dumpPacked) =>
        "Effects: " + typeName + " sizeof is " + observed + ", which is neither the dump's "
        + dumpStride + "-byte stride nor its " + dumpPacked
        + "-byte field extent; the raw walk keeps striding " + dumpStride
        + " (the dump is the authority) — this reading is evidence the dump may be stale.";

    /// <summary>
    /// Warning when no reading was available at all. Also not a refusal (R30 finding 5): an
    /// unreadable interop struct says nothing about a stride that came from the dump.
    /// </summary>
    internal static string UnreadableNote(string typeName, int dumpStride) =>
        "Effects: " + typeName + " sizeof was unavailable; the raw walk keeps striding "
        + dumpStride + " from the dump.";

    /// <summary>
    /// <see cref="Agree"/> with the <c>SequenceInputElement</c> pair filled in. <b>Not</b> the
    /// walk's stride and no longer a gate: the walk always uses
    /// <see cref="InputElementStride"/> whatever this returns (R30 finding 5).
    /// </summary>
    internal static LayoutAgreement AgreeInputElement(int actualStride) =>
        Agree(actualStride, InputElementStride, InputElementPackedBytes, InputElementAlignment);

    /// <summary>
    /// <see cref="ReadingLogFor"/> with the <c>SequenceInputElement</c> pair filled in.
    /// </summary>
    internal static string? InputElementReadingLog(int actualStride) =>
        ReadingLogFor(
            "SequenceInputElement",
            actualStride,
            InputElementStride,
            InputElementPackedBytes,
            InputElementAlignment);

    /// <summary>
    /// True when the blob walk may run at all: the constants back each other. The runtime
    /// reading is not consulted (R30 finding 5).
    /// </summary>
    internal static bool InputElementWalkAllowed() =>
        WalkAllowed(InputElementStride, InputElementPackedBytes, InputElementAlignment);

    /// <summary>True when <c>BlobArray.m_OffsetPtr</c> is inside the range a real blob uses.</summary>
    internal static bool BlobOffsetOk(int offset) => offset > 0 && offset < MaxBlobOffset;

    /// <summary>True when <c>BlobArray.m_Length</c> is inside the range a real sequence uses.</summary>
    internal static bool CountOk(int count) => count > 0 && count <= MaxInputs;

    /// <summary>
    /// True when the whole element walk (<c>offset</c> then <c>count</c> strides) stays inside
    /// <see cref="MaxBlobBytes"/>. Computed in <c>long</c>: <c>count * stride</c> overflows
    /// <c>int</c> well before <see cref="MaxInputs"/> would, and a hostile blob is not bound by
    /// <see cref="MaxInputs"/> until this check runs.
    /// </summary>
    internal static bool WalkOk(int offset, int count)
    {
        if (!BlobOffsetOk(offset) || !CountOk(count)) return false;
        long end = WalkEnd(offset, count);
        return end > 0L && end <= MaxBlobBytes;
    }

    /// <summary>
    /// One past the last byte the walk would touch, in <c>long</c> arithmetic. Not valid on its
    /// own — <see cref="WalkOk"/> and <see cref="WalkFitsBlob"/> are what decide.
    /// </summary>
    internal static long WalkEnd(int offset, int count) =>
        (long)offset + ((long)count * InputElementStride);

    // ---- R31 finding 14: the walk is bounded by the blob's own extent -------------------
    //
    // `WalkOk` bounds `end` against a 4 MiB constant that nothing in the process backs, and the
    // per-element `at + stride > blob.Length` check inside `TryFindSlots` is circular: the span
    // handed in was itself sized from the same untrusted `offset`/`count`. A stale or
    // differently-versioned 256-byte `SequenceInput` blob whose `m_OffsetPtr`/`m_Length` read as
    // 8/512 gives end = 38,920 and the walk reads ~38 KB past the allocation; across an unmapped
    // page that is an access violation the `catch (Exception)` around the walk cannot catch.
    //
    // The extent is available. Unity's `BlobAssetHeader` sits immediately before the data
    // `BlobAssetReferenceData.m_Ptr` points at, with `int Length; // 0x8`
    // (server metadata), so the real allocation size is one read away.

    /// <summary>Size of Unity's <c>BlobAssetHeader</c>, which precedes the blob data.</summary>
    internal const int BlobAssetHeaderBytes = Layout.BlobAssetHeaderBytes;

    /// <summary><c>BlobAssetHeader.Length</c> offset inside the header.</summary>
    internal const int BlobAssetHeaderLengthOffset = Layout.BlobAssetHeaderLengthOffset;

    /// <summary>Where the <c>BlobAssetHeader</c> layout is read from.</summary>
    internal const string BlobAssetHeaderDumpLines = "server metadata BlobAssetHeader";

    /// <summary>
    /// Byte offset, relative to the blob data pointer, of <c>BlobAssetHeader.Length</c>. Negative
    /// by construction: the header is behind the pointer.
    /// </summary>
    internal const int BlobAssetHeaderLengthDelta =
        BlobAssetHeaderLengthOffset - BlobAssetHeaderBytes;

    /// <summary>How a <c>BlobAssetHeader.Length</c> reading relates to a real allocation.</summary>
    internal enum ExtentVerdict
    {
        /// <summary>A length a blob can really have.</summary>
        Ok,

        /// <summary>Zero or negative: not a blob this build lays out the way the dump says.</summary>
        Unreadable,

        /// <summary>Larger than any sequence blob; the header read is not a length.</summary>
        Impossible,
    }

    /// <summary>
    /// Classify a <c>BlobAssetHeader.Length</c> reading. A fact about <em>one</em> blob, so it
    /// refuses one call and never latches a gate (R30 finding 9).
    /// </summary>
    internal static ExtentVerdict ClassifyBlobExtent(long blobLength)
    {
        if (blobLength <= 0L) return ExtentVerdict.Unreadable;
        if (blobLength > MaxBlobBytes) return ExtentVerdict.Impossible;
        return ExtentVerdict.Ok;
    }

    /// <summary>
    /// True when the whole walk fits inside the blob's <b>own</b> extent — not just inside
    /// <see cref="MaxBlobBytes"/>. This is the check <see cref="WalkOk"/> could not make and the
    /// one <c>TryFindSlots</c>' per-element bound was circular without: the span it strides is
    /// only in range because this said so first (R31 finding 14).
    /// </summary>
    internal static bool WalkFitsBlob(int offset, int count, long blobLength)
    {
        if (!WalkOk(offset, count)) return false;
        if (ClassifyBlobExtent(blobLength) != ExtentVerdict.Ok) return false;
        return WalkEnd(offset, count) <= blobLength;
    }

    /// <summary>
    /// Warning written once when a blob's extent refuses the walk, naming every number so a
    /// stale-blob refusal is distinguishable from a header layout this build changed.
    /// </summary>
    internal static string BlobExtentRefusedLog(int offset, int count, long blobLength) =>
        "Effects: sequence blackboard input walk refused; BlobAssetHeader.Length read "
        + blobLength + " (" + BlobAssetHeaderDumpLines + ", header " + BlobAssetHeaderBytes
        + " bytes before the data) but the walk (offset " + offset + ", count " + count
        + ", stride " + InputElementStride + ") would end at " + WalkEnd(offset, count)
        + ". This blob is skipped; the walk stays on for every other sequence.";

    /// <summary>
    /// Byte offset of element <paramref name="index"/> of the walk, or -1 when the walk itself
    /// is out of range. Long arithmetic throughout; the result is only returned when it fits an
    /// <c>int</c>.
    /// </summary>
    internal static int ElementOffset(int offset, int count, int index)
    {
        if (!WalkOk(offset, count)) return -1;
        if (index < 0 || index >= count) return -1;
        long at = (long)offset + ((long)index * InputElementStride);
        if (at < 0L || at + InputElementStride > MaxBlobBytes) return -1;
        return (int)at;
    }

    /// <summary>
    /// Refusal bound on the <c>BlackboardElement</c> buffer's byte count. R28 finding 1 is about
    /// the LineRenderer path, but <see cref="SlotFits"/> has the same dependency: it bounds the
    /// write against a length the interop reported, so a garbage length authorizes a garbage
    /// slot. A sequence's blackboard holds a handful of registers; 64 KB is far past anything
    /// the game authors and well inside a chunk.
    /// </summary>
    internal const int MaxBufferBytes = 1 << 16;

    /// <summary>True when the reported blackboard buffer length can be written into at all.</summary>
    internal static bool BufferLengthOk(int length) => length > 0 && length <= MaxBufferBytes;

    /// <summary>
    /// Classify a reported <c>BlackboardElement</c> buffer length the way
    /// <see cref="LineRendererBufferLayout.Classify"/> classifies the line buffer's
    /// (R29 finding 4, applied here in EV17 after the live <c>path=none</c> regression).
    ///
    /// <para>EV16 fed <b>every</b> refusal to <c>LayoutOnceGate.Fail</c> on the shared
    /// element-size gate — and that gate is read by <c>LayoutOk</c>, which
    /// <c>TryGetSlots</c> consults. So one sequence whose blackboard buffer read as empty turned
    /// slot resolution off for <em>every</em> sequence for the rest of the process, and the only
    /// symptom was `path=none`. A zero-length buffer is an ordinary sequence graph with no
    /// registers: skip it.</para>
    /// </summary>
    internal static LineRendererBufferLayout.LengthVerdict ClassifyBufferLength(int length)
    {
        if (length < 0) return LineRendererBufferLayout.LengthVerdict.Impossible;
        if (length == 0) return LineRendererBufferLayout.LengthVerdict.TooSmall;
        if (length > MaxBufferBytes) return LineRendererBufferLayout.LengthVerdict.Impossible;
        return LineRendererBufferLayout.LengthVerdict.Ok;
    }

    /// <summary>
    /// Warning written once when the blackboard buffer length is a number no buffer can have
    /// (negative or past <see cref="MaxBufferBytes"/>). A legitimately empty buffer gets
    /// <see cref="BufferEmptyLog"/> instead.
    ///
    /// <para><b>R30 finding 9.</b> This refusal used to latch a gate, so one entity's garbage
    /// length turned the blackboard write off for every sequence for the rest of the process. A
    /// length is a fact about one entity; it is now a per-call refusal that logs once.</para>
    /// </summary>
    internal static string BufferLengthRefusedLog(int length) =>
        "Effects: BlackboardElement buffer length is " + length
        + ", which no buffer can have (a real one is 1.." + MaxBufferBytes
        + "); skipping that sequence (the blackboard path stays on).";

    /// <summary>Info written once when a sequence's blackboard buffer is legitimately empty.</summary>
    internal static string BufferEmptyLog() =>
        "Effects: a sequence's BlackboardElement buffer is empty; skipping that sequence "
        + "(the blackboard path stays on).";

    /// <summary>
    /// True when <paramref name="size"/> bytes written at <paramref name="slot"/> stay inside a
    /// <paramref name="bufferLength"/>-byte buffer. R27 finding 5.3: the old
    /// <c>slot + size &lt;= length</c> overflows — an <c>Index</c> of
    /// <c>int.MaxValue - 5</c> makes <c>slot + 12</c> negative, the comparison passes, and the
    /// write lands ~2 GB past the buffer. The addition is done in <c>long</c>.
    /// </summary>
    internal static bool SlotFits(int slot, int size, int bufferLength)
    {
        if (slot < 0 || size <= 0 || bufferLength <= 0) return false;
        return (long)slot + size <= bufferLength;
    }

    /// <summary>Blackboard input the coil client system writes with the beam's start point.</summary>
    internal const string PositionInput = "Position";

    /// <summary>Blackboard input the coil client system writes with the look rotation.</summary>
    internal const string RotationInput = "Rotation";

    /// <summary>
    /// Longest name a <c>FixedString64Bytes</c> can hold: 64 bytes less the 2-byte length and a
    /// terminator.
    /// </summary>
    internal const int NameMaxBytes = Layout.FixedString64MaxUtf8Bytes;

    /// <summary>
    /// True when element <paramref name="element"/>'s <c>FixedString64Bytes Name</c> is
    /// <paramref name="name"/>. ASCII compare against the on-disk form: <c>ushort</c> length,
    /// then UTF-8 bytes.
    /// </summary>
    internal static bool NameIs(ReadOnlySpan<byte> element, string name)
    {
        if (name == null) return false;
        if (element.Length < 2) return false;
        int len = BinaryPrimitives.ReadUInt16LittleEndian(element);
        if (len != name.Length || len > NameMaxBytes) return false;
        if (element.Length < 2 + len) return false;
        for (int i = 0; i < len; i++)
        {
            if (element[2 + i] != (byte)name[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// The <c>Position</c> / <c>Rotation</c> register byte offsets carried by a sequence's
    /// <c>BlobArray&lt;SequenceInputElement&gt;</c>, or -1 each.
    ///
    /// <para><b>EV17.</b> This is the walk that used to live in <c>SequenceBlackboard.Resolve</c>
    /// as raw pointer arithmetic, which is why R29 22.x could say the policy types get coverage
    /// and the edit types get none — and why the live regression that produced
    /// <c>path=none</c> could not be reproduced in a test at all. It is a
    /// <see cref="ReadOnlySpan{T}"/> read now: production wraps the already-bounded blob range
    /// in a span and calls this, so the same code the game runs is the code the tests run.</para>
    ///
    /// <para><paramref name="blob"/> is the blob root; <paramref name="offset"/> and
    /// <paramref name="count"/> are <c>BlobArray.m_OffsetPtr</c> and <c>m_Length</c>, already
    /// read from it. Returns true when either register was found.</para>
    /// </summary>
    internal static bool TryFindSlots(
        ReadOnlySpan<byte> blob, int offset, int count, out int position, out int rotation)
    {
        position = -1;
        rotation = -1;
        if (!WalkOk(offset, count)) return false;

        for (int i = 0; i < count; i++)
        {
            int at = ElementOffset(offset, count, i);
            if (at < 0) break;
            if (at + InputElementStride > blob.Length) break;
            var element = blob.Slice(at, InputElementStride);
            int index = BinaryPrimitives.ReadInt32LittleEndian(element.Slice(InputIndexOffset, 4));
            int type = BinaryPrimitives.ReadInt32LittleEndian(element.Slice(InputTypeOffset, 4));
            if (index < 0) continue;
            if (position < 0 && type == TypeFloat3 && NameIs(element, PositionInput))
                position = index;
            else if (rotation < 0 && type == TypeQuaternion && NameIs(element, RotationInput))
                rotation = index;
            if (position >= 0 && rotation >= 0) break;
        }

        return position >= 0 || rotation >= 0;
    }

    /// <summary>
    /// One-shot Info line describing what the walk actually saw, so a live run answers "why is
    /// the path <c>none</c>" without another round trip. EV17: the EV15/EV16 guards refused on
    /// numbers nobody had ever printed.
    /// </summary>
    /// <param name="sizeOf">
    /// What the managed <c>sizeof</c> read. Reported next to the stride, never used as one
    /// (EV17 second pass): on this interop it is the unpadded 73 while the walk strides 76.
    /// </param>
    internal static string WalkDiagnostic(
        int sizeOf, int blobOffset, int count, int position, int rotation, string inputs) =>
        "Effects: sequence blackboard probe stride=" + InputElementStride + " (dump "
        + InputElementStride + ") sizeof=" + sizeOf
        + " blobOffset=" + blobOffset + " count=" + count
        + " position=" + position + " rotation=" + rotation
        + " inputs=[" + inputs + "]";

    /// <summary>One entry of <see cref="WalkDiagnostic"/>'s <c>inputs</c> list.</summary>
    internal static string DescribeInput(ReadOnlySpan<byte> element)
    {
        if (element.Length < InputElementStride) return "?";
        int len = BinaryPrimitives.ReadUInt16LittleEndian(element);
        if (len < 0 || len > NameMaxBytes || element.Length < 2 + len) return "?";
        var name = new char[len];
        for (int i = 0; i < len; i++)
            name[i] = (char)element[2 + i];
        int index = BinaryPrimitives.ReadInt32LittleEndian(element.Slice(InputIndexOffset, 4));
        int type = BinaryPrimitives.ReadInt32LittleEndian(element.Slice(InputTypeOffset, 4));
        return new string(name) + ":" + type + "@" + index;
    }

    /// <summary>
    /// Up to <paramref name="max"/> <see cref="DescribeInput"/> entries, comma separated. Used
    /// only by the one-shot diagnostic, so the allocation is per Core generation.
    /// </summary>
    internal static string DescribeInputs(ReadOnlySpan<byte> blob, int offset, int count, int max)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count && i < max; i++)
        {
            int at = ElementOffset(offset, count, i);
            if (at < 0 || at + InputElementStride > blob.Length) break;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(DescribeInput(blob.Slice(at, InputElementStride)));
        }

        if (count > max) sb.Append(", …");
        return sb.ToString();
    }
}

/// <summary>
/// One-shot layout check that actually disables the thing it warns about (R27 finding 5.1: the
/// old stride check logged "blackboard re-point disabled for this build." and then returned
/// without setting any flag, so nothing changed). The first failing check latches the gate shut
/// for its life and hands back exactly one log line; every later call is silent.
///
/// <para><b>The gate is closed until a probe passes (R28 finding 2, EV16).</b> The EV15 version
/// defaulted <c>Disabled = false</c>, so <see cref="Enabled"/> was true <em>before any check
/// ran</em>, and <c>CheckStrideOnce</c>'s <c>catch { return; }</c> — the path taken when the
/// interop <c>sizeof</c> throws, i.e. exactly the failure the guard exists for — never touched
/// the gate at all. The walk then ran against a differently-shaped blob with the hardcoded
/// stride. Now <see cref="Enabled"/> requires a check that has run <em>and</em> passed, and
/// every probe's <c>catch</c> calls <see cref="Check"/> with <c>false</c> so a throw fails
/// closed and says so once.</para>
/// </summary>
internal struct LayoutOnceGate
{
    private bool _checked;
    private bool _ok;

    /// <summary>True once a probe has run (passing or failing).</summary>
    internal bool Checked => _checked;

    /// <summary>
    /// True only after a probe ran and passed. Callers must refuse the operation otherwise —
    /// including before the first probe, which is why every call site runs the probe first.
    /// </summary>
    internal bool Enabled => _checked && _ok;

    /// <summary>Inverse of <see cref="Enabled"/>: unprobed and failed both read disabled.</summary>
    internal bool Disabled => !Enabled;

    /// <summary>True once a probe ran and failed (as opposed to not having run yet).</summary>
    internal bool Failed => _checked && !_ok;

    /// <summary>
    /// Run the check once. Returns <paramref name="logOnFail"/> the first time
    /// <paramref name="ok"/> is false (leaving the gate shut); null otherwise. A passing check
    /// is what opens the gate.
    /// </summary>
    internal string? Check(bool ok, string logOnFail)
    {
        if (_checked) return null;
        _checked = true;
        _ok = ok;
        return ok ? null : logOnFail;
    }

    /// <summary>
    /// Latch the gate shut now, outside the one-shot probe — for a runtime value that cannot be
    /// trusted (an absurd buffer length, R28 finding 1). Returns the log line the first time it
    /// closes the gate and null afterwards, so the caller still logs exactly once.
    /// </summary>
    internal string? Fail(string log)
    {
        bool first = !_checked || _ok;
        _checked = true;
        _ok = false;
        return first ? log : null;
    }

    /// <summary>Forget the check (Core uninstall / a new generation). Leaves the gate closed.</summary>
    internal void Reset()
    {
        _checked = false;
        _ok = false;
    }
}

/// <summary>
/// R27 finding 17. The component overlays go through <c>SlotOk</c>; the two raw <b>buffer</b>
/// write paths asserted their element size in a comment and nowhere else, and read the same
/// accessor two different ways: <c>TryWriteLineRendererPositions</c> treats
/// <c>GetBufferLength</c> as an element count and multiplies by a hard-coded 20,
/// <c>SequenceBlackboard</c> treats it as a byte count. Both are only correct while the
/// element size is what the dump says.
///
/// <para><b>R30 findings 5 and 9 changed what this type is for.</b> It used to hold the
/// <em>gate decision</em> for both paths, keyed on a runtime <c>sizeof</c> and a
/// <c>TypeManager</c> reading. Neither number is ever multiplied by anything - both walks
/// stride by the dump constants below - so a disagreement was never evidence that the walk was
/// wrong, and latching on one took the whole raw path down process-wide three jobs running. The
/// decision now lives in <see cref="SequenceBlackboardLayout.WalkAllowed"/> and depends only on
/// the constants; everything here produces log lines.</para>
/// </summary>
internal static class BufferElementGuard
{
    /// <summary><c>BlackboardElement</c> is one <c>byte Value</c>.</summary>
    internal const int BlackboardElementSize = Layout.BlackboardElementStride;

    /// <summary>A single byte: nothing to align to (R30 finding 14 needs this stated).</summary>
    internal const int BlackboardElementAlignment = 1;

    // ---- R31 finding 20: the field extents, so the size constants are checkable -----------
    //
    // `WritesAllowed(dumpSize, alignment)` forwarded `dumpSize` as both stride and packed, so
    // `PaddingInvariantHolds` reduced to "positive, and dumpSize == RoundUp(dumpSize,
    // alignment)" — true for every positive value at alignment 1, and true of the old wrong
    // BuffSequence stride 24 at alignment 8. Mistyping `BlackboardElementSize` to 4 still
    // returned true, so the gate `docs/effects.md` calls the only thing that can refuse the
    // blackboard buffer write could not refuse anything.
    //
    // The fix is a second, independently sourced number: the field extent, summed from the
    // dump's own offsets and field sizes. A mistyped size no longer agrees with the extent it
    // is supposed to be the padded form of, so it is refused. This is still a compile-time fact
    // about this file, not a runtime reading (R30 findings 5 and 21).

    /// <summary><c>BlackboardElement.Value</c> offset.</summary>
    internal const int BlackboardElementValueOffset = Layout.BlackboardElementValueOffset;

    /// <summary><c>BlackboardElement.Value</c> is one <c>byte</c>.</summary>
    internal const int BlackboardElementValueBytes = 1;

    /// <summary>Field extent, from the dump's offsets: <c>Value@0x0</c> plus its one byte.</summary>
    internal const int BlackboardElementPackedBytes =
        BlackboardElementValueOffset + BlackboardElementValueBytes;

    /// <summary>Where <see cref="BlackboardElementSize"/> is read from (R30 finding 22).</summary>
    internal const string BlackboardElementDumpLines = "server metadata BlackboardElement";

    /// <summary>
    /// <c>LineRendererPositionElement</c> is <c>{ int LineRendererIndex@0; int PositionIndex@4;
    /// float3 PositionValue@8; }</c> = 20 bytes, already 4-aligned so there is no tail padding.
    /// </summary>
    internal const int LineRendererPositionElementSize = Layout.LineRendererPositionElementStride;

    /// <summary>Widest field is a 4-byte <c>int</c> / <c>float</c>.</summary>
    internal const int LineRendererPositionElementAlignment = 4;

    /// <summary><c>LineRendererPositionElement.PositionValue</c> offset.</summary>
    internal const int LineRendererPositionValueOffset = Layout.LineRendererPositionValueOffset;

    /// <summary>
    /// Field extent, from the dump's offsets: <c>PositionValue@0x8</c> plus
    /// <see cref="SequenceBlackboardLayout.Float3Bytes"/>. Equals the stride — the element has
    /// no tail padding — which is exactly why R31 finding 20 needed it stated separately.
    /// </summary>
    internal const int LineRendererPositionElementPackedBytes =
        LineRendererPositionValueOffset + SequenceBlackboardLayout.Float3Bytes;

    /// <summary>Where the <c>LineRendererPositionElement</c> offsets are read from.</summary>
    internal const string LineRendererPositionElementDumpLines = "client metadata LineRendererPositionElement";

    /// <summary>
    /// <b>The gate decision for a raw buffer write (R30 findings 5 and 9).</b> True when the
    /// dump constants for the element are self-consistent - which is all the walk depends on.
    /// No runtime reading is an argument here, on purpose.
    ///
    /// <para><b>R31 finding 20.</b> <paramref name="dumpPacked"/> is required and must be the
    /// field extent summed from the dump's own offsets, <em>not</em> a second copy of
    /// <paramref name="dumpSize"/>. Passing the size twice made the invariant
    /// "<c>dumpSize == RoundUp(dumpSize, alignment)</c>", which is true of every positive value
    /// at alignment 1 and true of the wrong 24-byte <c>BuffSequence</c> stride at alignment 8:
    /// the gate could not refuse the very bug R30 finding 2 fixed. With the two numbers sourced
    /// independently, a mistyped size is refused even when the correct stride and extent
    /// coincide.</para>
    /// </summary>
    internal static bool WritesAllowed(int dumpSize, int dumpPacked, int alignment) =>
        SequenceBlackboardLayout.WalkAllowed(dumpSize, dumpPacked, alignment);

    /// <summary>
    /// The one line a <c>sizeof</c> reading may produce for a buffer element, or null. Never a
    /// refusal: both buffer elements are unpadded, so anything but the dump's own number is
    /// evidence the dump may be stale and nothing more.
    /// </summary>
    internal static string? SizeReadingLog(
        string typeName, int sizeOf, int dumpSize, int dumpPacked, int alignment) =>
        SequenceBlackboardLayout.ReadingLogFor(typeName, sizeOf, dumpSize, dumpPacked, alignment);

    /// <summary>True when <see cref="SizeReadingLog"/>'s line is a warning rather than info.</summary>
    internal static bool SizeReadingIsWarning(
        int sizeOf, int dumpSize, int dumpPacked, int alignment) =>
        SequenceBlackboardLayout.ReadingLogIsWarning(sizeOf, dumpSize, dumpPacked, alignment);

    /// <summary>
    /// True when <c>TypeManager</c> answered with a number the dump does not explain. A 0 is
    /// "unknown through this interop" (CLAUDE.md trap 7) and says nothing.
    /// </summary>
    internal static bool TypeManagerDisagrees(int reported, int dumpSize) =>
        reported > 0 && reported != dumpSize;

    /// <summary>
    /// Written once when <c>TypeManager</c> disagrees with the dump. Informational: the raw path
    /// stays on, because the offsets it computes come from the dump. EV15/EV16 let this
    /// condition silently disable the whole blackboard re-point.
    /// </summary>
    internal static string TypeManagerNote(string typeName, int reported, int dumpSize) =>
        "Effects: TypeManager reports " + typeName + " buffer element size " + reported
        + ", but the dump says " + dumpSize
        + "; the walk strides the dump's number and raw buffer writes stay on.";
}

/// <summary>
/// <b>R30 finding 2.</b> <c>BuffSequence</c> is the third raw buffer walk in Effects and it got
/// none of the discipline the other two did: <c>TryReadBuffSequenceGuids</c> hard-coded
/// <c>stride = 24</c> behind a comment that cited no metadata and described a field size the
/// dump contradicts, with <c>length &lt;= 0</c> as its only bound. Over 16-byte elements that
/// read element 1's <c>Condition</c> <em>pointer</em> as a prefab guid hash and walked off the
/// end of any buffer with three or more entries.
///
/// <para><b>Metadata.</b> <c>BuffSequence { SequenceGUID SequenceGuid; // 0x0 ;
/// BlobAssetReference&lt;ConditionBlob&gt; Condition; // 0x8 }</c>
/// (<see cref="DumpLines"/>). <c>SequenceGUID</c> is one <c>int GuidHash; // 0x0</c>
/// - four bytes, padded to eight by the pointer that follows.
/// <c>BlobAssetReference&lt;T&gt;</c> is one field, <c>BlobAssetReferenceData m_data; // 0x0</c>
/// and <c>BlobAssetReferenceData</c> is a <em>union</em> of
/// <c>byte* m_Ptr; // 0x0</c> and <c>long m_Align8Union; // 0x0</c>
/// (<see cref="ConditionDumpLines"/>) - <b>8</b> bytes, not 16. So the element is 8 + 8 = 16
/// and the stride is 16.</para>
///
/// <para>Game-free, so the test project compiles it and the walk the game runs is the walk the
/// tests run.</para>
/// </summary>
internal static class BuffSequenceLayout
{
    /// <summary>Where the <c>BuffSequence</c> field offsets are read from.</summary>
    internal const string DumpLines = "server metadata BuffSequence";

    /// <summary>Where <c>BlobAssetReferenceData</c>'s single 8-byte union is read from.</summary>
    internal const string ConditionDumpLines = "server metadata BlobAssetReferenceData";

    /// <summary><c>BuffSequence.SequenceGuid</c> offset.</summary>
    internal const int SequenceGuidOffset = Layout.BuffSequenceSequenceGuidOffset;

    /// <summary><c>SequenceGUID.GuidHash</c> is a single <c>int</c>.</summary>
    internal const int GuidHashBytes = 4;

    /// <summary><c>BuffSequence.Condition</c> offset.</summary>
    internal const int ConditionOffset = Layout.BuffSequenceConditionOffset;

    /// <summary>
    /// <c>BlobAssetReference&lt;ConditionBlob&gt;</c> is one <c>BlobAssetReferenceData</c>, which
    /// is a union of a pointer and a <c>long</c> both at <c>0x0</c>: <b>8</b> bytes. The old
    /// comment said 16, which is where the 24-byte stride came from.
    /// </summary>
    internal const int ConditionBytes = 8;

    /// <summary>The <c>BlobAssetReference</c> pointer is what the element aligns to.</summary>
    internal const int Alignment = 8;

    /// <summary>Max field extent: <c>Condition@0x8</c> plus its eight bytes = 16.</summary>
    internal const int PackedBytes = ConditionOffset + ConditionBytes;

    /// <summary>
    /// The native stride, derived: the field extent rounded up to <see cref="Alignment"/>.
    /// Already aligned, so 16 - which is why the old 24 over-strode by half an element.
    /// </summary>
    internal const int Stride =
        ((PackedBytes + Alignment - 1) / Alignment) * Alignment;

    /// <summary>
    /// Refusal bound on the entry count. A buff prefab lists a handful of sequences; 256 is far
    /// past anything the game authors, so a larger answer is an interop misread.
    /// </summary>
    internal const int MaxElements = 256;

    /// <summary>Largest buffer the walk will touch, in bytes.</summary>
    internal const long MaxBufferBytes = (long)MaxElements * Stride;

    /// <summary>
    /// True when the constants back each other - the only thing that can refuse this walk
    /// (R30 findings 5 and 14).
    /// </summary>
    internal static bool WalkAllowed() =>
        SequenceBlackboardLayout.WalkAllowed(Stride, PackedBytes, Alignment);

    /// <summary>
    /// Classify a reported buffer length exactly as the other two raw walks do: an empty buffer
    /// is an ordinary buff prefab (skip it), and only a number no buffer can have is a refusal.
    /// Per call, never a latch (R30 finding 9).
    /// </summary>
    internal static LineRendererBufferLayout.LengthVerdict Classify(int length)
    {
        if (length < 0) return LineRendererBufferLayout.LengthVerdict.Impossible;
        if (length == 0) return LineRendererBufferLayout.LengthVerdict.TooSmall;
        if (length > MaxElements) return LineRendererBufferLayout.LengthVerdict.Impossible;
        long total = (long)length * Stride;
        if (total <= 0L || total > MaxBufferBytes) return LineRendererBufferLayout.LengthVerdict.Impossible;
        return LineRendererBufferLayout.LengthVerdict.Ok;
    }

    /// <summary>Total bytes of a walkable buffer, or -1 when the length was refused.</summary>
    internal static long TotalBytes(int length) =>
        Classify(length) == LineRendererBufferLayout.LengthVerdict.Ok ? (long)length * Stride : -1L;

    /// <summary>
    /// Byte offset of element <paramref name="index"/>, or -1 when the walk is out of range.
    /// Long arithmetic throughout, so a hostile length cannot wrap an offset negative.
    /// </summary>
    internal static long ElementOffset(int index, int length)
    {
        long total = TotalBytes(length);
        if (total < 0L) return -1L;
        if (index < 0 || index >= length) return -1L;
        long at = (long)index * Stride;
        if (at < 0L || at + Stride > total) return -1L;
        return at;
    }

    /// <summary>
    /// <c>SequenceGuid.GuidHash</c> of element <paramref name="index"/>. False when the element
    /// or its first field does not fit the buffer, which is what the 24-byte stride could not
    /// say - it just read whatever was there.
    /// </summary>
    internal static bool TryReadGuidHash(
        ReadOnlySpan<byte> buffer, int index, int length, out int hash)
    {
        hash = 0;
        long at = ElementOffset(index, length);
        if (at < 0L) return false;
        long field = at + SequenceGuidOffset;
        if (field < 0L || field + GuidHashBytes > buffer.Length) return false;
        hash = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice((int)field, GuidHashBytes));
        return true;
    }

    /// <summary>
    /// Warning written once per Core generation when the reported entry count is a number no
    /// buffer can have. Per call: this buff prefab is skipped and the next one is tried.
    /// </summary>
    internal static string LengthRefusedLog(int length) =>
        "Effects: BuffSequence buffer length is " + length
        + ", which no buffer can have (a real one is 1.." + MaxElements
        + "); skipping that buff prefab (the BuffSequence resolve stays on).";
}

/// <summary>
/// R28 finding 1 (EV16). The <c>LineRendererPositionElement</c> write path is the raw-buffer
/// path R27 finding 17 named and EV15 only half fixed: it got the element-size check and kept
/// <c>if (length &lt; 2) return false;</c> as its only bound, with every offset computed as
/// <c>i * stride</c> in <c>int</c>.
///
/// <para><c>GetBufferLength</c> reading garbage through the interop is the premise of the
/// element-size guard sitting three lines above it, so a garbage length was assumed not to
/// happen while the stride it multiplies was assumed to. With a large length the loop wrote 20
/// bytes per element for <c>length</c> elements past the chunk, and <c>i * stride</c> overflows
/// <c>int</c> at <c>i ≈ 107,374,182</c>, wrapping negative and writing <em>before</em> the
/// buffer.</para>
///
/// <para>Everything here is game-free so the test project compiles it: the bounds and the
/// overflow behavior are asserted, not assumed — the same treatment
/// <see cref="SequenceBlackboardLayout"/> gave the blackboard walk.</para>
/// </summary>
internal static class LineRendererBufferLayout
{
    /// <summary>A two-point line needs two elements; fewer is not a line.</summary>
    internal const int MinElements = 2;

    /// <summary>
    /// Refusal bound on the element count. A real sequence's buffer holds a handful of entries
    /// (two per line renderer); four thousand is already far past anything the game authors,
    /// so a larger answer is an interop misread, not a big line.
    /// </summary>
    internal const int MaxElements = 4096;

    /// <summary>Largest buffer the walk will touch, in bytes.</summary>
    internal const long MaxBufferBytes =
        (long)MaxElements * BufferElementGuard.LineRendererPositionElementSize;

    /// <summary><c>LineRendererPositionElement.PositionIndex</c> byte offset.</summary>
    internal const int PositionIndexOffset = Layout.LineRendererPositionIndexOffset;

    /// <summary><c>LineRendererPositionElement.PositionValue</c> byte offset.</summary>
    internal const int PositionValueOffset = Layout.LineRendererPositionValueOffset;

    /// <summary><c>sizeof(int)</c>, the <c>PositionIndex</c> read and write size.</summary>
    internal const int IndexFieldBytes = 4;

    /// <summary>Three floats: the <c>PositionValue</c> write size.</summary>
    internal const int PositionFieldBytes = 12;

    /// <summary>
    /// What a reported buffer length means. R29 finding 4: EV16 collapsed
    /// "too small to be a line" and "that number cannot be a buffer" into one refusal, and both
    /// latched the gate for the process.
    /// </summary>
    internal enum LengthVerdict
    {
        /// <summary>Walkable: <c>MinElements … MaxElements</c> and the byte total fits.</summary>
        Ok,

        /// <summary>
        /// A legitimate but unusable buffer (<c>0</c> or <c>1</c> element). Skip <em>this
        /// entity</em>; never touch the gate.
        /// </summary>
        TooSmall,

        /// <summary>
        /// A number no <c>DynamicBuffer</c> can have (negative, absurdly large, or a total that
        /// overflows the walk). This is the interop misread the guard exists for: latch.
        /// </summary>
        Impossible,
    }

    /// <summary>
    /// Classify the buffer length the interop reported.
    ///
    /// <para><b>R29 finding 4 (EV17).</b> A <c>SequenceInstance</c> whose
    /// <c>LineRendererPositionElement</c> buffer has no positions authored yet, or one line
    /// renderer's single point, is an <em>ordinary entity</em> — not an interop misread. EV16
    /// fed both it and a garbage length to <c>LayoutOnceGate.Fail</c>, so the first such
    /// sequence the client or server touched turned
    /// <c>TryWriteLineRendererPositions</c> off for every beam for the rest of the process and
    /// said the length was "outside 2..4096", which reads as a build problem. `0 <= length &lt;
    /// MinElements` is now <see cref="LengthVerdict.TooSmall"/>: this entity is skipped and the
    /// gate is untouched.</para>
    /// </summary>
    internal static LengthVerdict Classify(int length, int stride)
    {
        if (stride <= 0) return LengthVerdict.Impossible;
        if (length < 0) return LengthVerdict.Impossible;
        if (length < MinElements) return LengthVerdict.TooSmall;
        if (length > MaxElements) return LengthVerdict.Impossible;
        long total = (long)length * stride;
        if (total <= 0L || total > MaxBufferBytes) return LengthVerdict.Impossible;
        return LengthVerdict.Ok;
    }

    /// <summary>
    /// True when the buffer length the interop reported can be walked at all. Computed in
    /// <c>long</c>: <c>length * stride</c> overflows <c>int</c> long before <c>int.MaxValue</c>
    /// elements, and a garbage length is not bound by <see cref="MaxElements"/> until this runs.
    /// </summary>
    internal static bool LengthOk(int length, int stride) =>
        Classify(length, stride) == LengthVerdict.Ok;

    /// <summary>Total bytes of a walkable buffer, or -1 when the length was refused.</summary>
    internal static long TotalBytes(int length, int stride) =>
        LengthOk(length, stride) ? (long)length * stride : -1L;

    /// <summary>
    /// Byte offset of element <paramref name="index"/>, or -1 when the walk is out of range.
    /// Long arithmetic throughout, so a hostile index cannot wrap into a negative offset.
    /// </summary>
    internal static long ElementOffset(int index, int length, int stride)
    {
        if (!LengthOk(length, stride)) return -1L;
        if (index < 0 || index >= length) return -1L;
        long at = (long)index * stride;
        if (at < 0L || at + stride > (long)length * stride) return -1L;
        return at;
    }

    /// <summary>
    /// True when <paramref name="size"/> bytes at
    /// <paramref name="elementOffset"/> + <paramref name="fieldOffset"/> stay inside
    /// <paramref name="totalBytes"/>. Checked before <em>every</em> read and write, not once
    /// per loop, because the element offset is the only thing the caller derived from the
    /// interop's answer.
    /// </summary>
    internal static bool FieldFits(long elementOffset, int fieldOffset, int size, long totalBytes)
    {
        if (elementOffset < 0L || fieldOffset < 0 || size <= 0 || totalBytes <= 0L) return false;
        long at = elementOffset + fieldOffset;
        if (at < 0L) return false;
        return at + size <= totalBytes;
    }

    /// <summary>
    /// Warning written once per Core generation when the interop hands back a length no buffer
    /// can have (<see cref="LengthVerdict.Impossible"/>). A
    /// <see cref="LengthVerdict.TooSmall"/> length gets <see cref="LengthSkippedLog"/> instead
    /// (R29 finding 4).
    ///
    /// <para><b>R30 finding 9.</b> This used to latch the <em>element-size</em> gate - the one
    /// the whole line path reads - on one entity's <c>GetBufferLength</c>, so a single misread
    /// or legitimately large line buffer turned <c>TryWriteLineRendererPositions</c> off for
    /// every beam for the rest of the process and blamed "this build". The split the blackboard
    /// path already had ("a length is a fact about one entity; an element size is a fact about
    /// the build") is applied here: the refusal is per call and the line says so.</para>
    /// </summary>
    internal static string LengthRefusedLog(int length) =>
        "Effects: LineRendererPositionElement buffer length is " + length
        + ", which no buffer can have (a real one is " + MinElements + ".." + MaxElements
        + "); skipping that sequence (the raw path stays on).";

    /// <summary>
    /// Info written once when a sequence's line buffer is legitimately too small to hold a
    /// two-point line. Not a failure and not a latch — the entity is skipped and the next one
    /// is tried (R29 finding 4).
    /// </summary>
    internal static string LengthSkippedLog(int length) =>
        "Effects: LineRendererPositionElement buffer has " + length + " element(s), fewer than "
        + MinElements + "; skipping that sequence (the raw path stays on).";

    /// <summary>
    /// Which endpoint an element carries, read from its own <c>PositionIndex</c>:
    /// 0 = start, 1 = end, -1 = neither (leave the element alone).
    /// </summary>
    internal static int EndpointFor(int positionIndex) =>
        positionIndex == 0 ? 0 : positionIndex == 1 ? 1 : -1;

    /// <summary>
    /// True when the walk must <b>seed</b> elements 0 and 1 as start and end (writing their
    /// <c>PositionIndex</c>) rather than honoring the indices already in the buffer.
    ///
    /// <para><b>R29 residual 21.9 (EV17).</b> EV16 wrote elements 0 and 1 unconditionally as
    /// start and end and stamped <c>PositionIndex = i</c> over whatever was there, while the
    /// loop from <c>i = 2</c> consulted <c>PositionIndex</c> — so a buffer whose element 0
    /// carries <c>PositionIndex 1</c> got the start point written into the end slot, and the
    /// authored index was destroyed on the way. The first two elements' own indices decide:
    /// a usable <c>{0,1}</c> pair in either order is honored, anything else (an unauthored
    /// buffer of zeroes, a duplicate, an out-of-range value) is seeded positionally, which is
    /// what a freshly instantiated two-point line looks like.</para>
    /// </summary>
    internal static bool SeedPositionally(int index0, int index1)
    {
        if (index0 == 0 && index1 == 1) return false;
        if (index0 == 1 && index1 == 0) return false;
        return true;
    }
}
