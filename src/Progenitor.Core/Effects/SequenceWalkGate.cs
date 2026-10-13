using System;

namespace Progenitor.Effects;

/// <summary>
/// Where the Effects gate wiring writes. One line per decision, so a test can drive the wiring
/// with a fake runtime and read back exactly what a live client would have seen.
/// </summary>
internal interface IEffectGateLog
{
    void Info(string message);

    void Warning(string message);
}

/// <summary>
/// <b>R31 finding 26 / R30 open question 7.</b> The code that decides whether a sequence
/// blackboard walk or write runs, in a game-free file the test project compiles.
///
/// <para>Research §A35.1 diagnosed all three of EV15, EV16 and EV17's live regressions the same
/// way: <em>the tested code was right and the untested predicate turned it off</em>. The pure
/// layout helpers in <see cref="SequenceBlackboardLayout"/> were always covered; the wiring that
/// consults them - which gate is asked, in what order, whether a runtime reading may close one,
/// whether a refusal latches or applies to one call - lived inside
/// <c>#if !PROGENITOR_TESTS</c> in <c>SequenceDriverLookup.cs</c>, so no test could reach it.
/// Three batches of fixes to the helpers could not catch a fourth repetition of the same bug.</para>
///
/// <para>Every decision now lives here. <c>SequenceDriverLookup</c> keeps only what genuinely
/// needs <c>Entity</c>, <c>EntityManager</c> and pointers: read the numbers, hand them over, act
/// on the answer. The two rules the wiring exists to hold are:</para>
/// <list type="number">
/// <item>A runtime reading (<c>sizeof</c>, <c>TypeManager</c>) never closes a gate. It is never
/// multiplied by anything - both walks stride the dump's constants - so it can only be evidence
/// that the dump looks stale, and evidence is a log line (R30 findings 5 and 21).</item>
/// <item>A fact about one blob or one entity (an extent, a buffer length) refuses <em>that
/// call</em> and never latches a process-wide gate (R30 finding 9, R31 finding 14).</item>
/// </list>
/// </summary>
internal sealed class SequenceWalkWiring
{
    private LayoutOnceGate _strideGate;
    private LayoutOnceGate _elementSizeGate;
    private bool _strideCrossCheckLogged;
    private bool _elementCrossCheckLogged;
    private bool _walkLogged;
    private bool _extentRefusedLogged;
    private bool _emptyBufferLogged;
    private bool _bufferLengthRefusedLogged;

    /// <summary>Back to the state a fresh Core generation starts in.</summary>
    internal void Reset()
    {
        _strideGate.Reset();
        _elementSizeGate.Reset();
        _strideCrossCheckLogged = false;
        _elementCrossCheckLogged = false;
        _walkLogged = false;
        _extentRefusedLogged = false;
        _emptyBufferLogged = false;
        _bufferLengthRefusedLogged = false;
    }

    /// <summary>True once the input-stride probe has run.</summary>
    internal bool InputGateChecked => _strideGate.Checked;

    /// <summary>True once the blackboard-element probe has run.</summary>
    internal bool BufferGateChecked => _elementSizeGate.Checked;

    /// <summary>
    /// Run the <c>SequenceInputElement</c> probe if it has not run, then report whether the blob
    /// walk may run at all.
    ///
    /// <para><paramref name="readSizeOf"/> is the runtime reading and is <b>reported, never
    /// obeyed</b>: it may return the dump's 76, the managed struct's unpadded 73, a third number
    /// from an interop regeneration, or 0 for "threw / unavailable", and the return value is the
    /// same in every case. That is the EV15 / EV16 / EV17 regression, and the test that would
    /// have caught all three drives this method with a wrong reading and asserts the walk is
    /// still on.</para>
    ///
    /// <para>The one thing that can close this gate is this module's own constants contradicting
    /// each other, which is a compile-time fact (R30 finding 14).</para>
    /// </summary>
    internal bool InputWalkEnabled(Func<int> readSizeOf, IEffectGateLog log)
    {
        if (_strideGate.Checked) return _strideGate.Enabled;

        string? gateLog = _strideGate.Check(
            SequenceBlackboardLayout.InputElementWalkAllowed(),
            SequenceBlackboardLayout.ConstantsInvariantLog(
                "SequenceInputElement",
                SequenceBlackboardLayout.InputElementStride,
                SequenceBlackboardLayout.InputElementPackedBytes,
                SequenceBlackboardLayout.InputElementAlignment));
        if (gateLog != null) log.Warning(gateLog);

        int actual = Read(readSizeOf);
        if (!_strideCrossCheckLogged)
        {
            string? note = SequenceBlackboardLayout.InputElementReadingLog(actual);
            if (note != null)
            {
                _strideCrossCheckLogged = true;
                if (SequenceBlackboardLayout.ReadingLogIsWarning(
                        actual,
                        SequenceBlackboardLayout.InputElementStride,
                        SequenceBlackboardLayout.InputElementPackedBytes,
                        SequenceBlackboardLayout.InputElementAlignment))
                {
                    log.Warning(note);
                }
                else
                {
                    log.Info(note);
                }
            }
        }

        return _strideGate.Enabled;
    }

    /// <summary>
    /// <b>R31 finding 14.</b> Per-call bounds on one blob's element walk.
    /// <paramref name="blobLength"/> is <c>BlobAssetHeader.Length</c>, read from the 32 bytes
    /// that precede the blob data (<see cref="SequenceBlackboardLayout.BlobAssetHeaderDumpLines"/>).
    ///
    /// <para><see cref="SequenceBlackboardLayout.WalkOk"/> alone bounds the walk against a 4 MiB
    /// constant nothing in the process backs, and the per-element check inside
    /// <c>TryFindSlots</c> is circular because the span it strides was sized from the same
    /// untrusted <paramref name="offset"/> and <paramref name="count"/>. This is the only check
    /// that compares the walk against the real allocation, so the span the caller builds is in
    /// range because this returned true - not by construction.</para>
    ///
    /// <para>A refusal is a fact about one blob: this call returns false, the warning is written
    /// once, and every other sequence still walks (R30 finding 9).</para>
    /// </summary>
    internal bool InputWalkInRange(int offset, int count, long blobLength, IEffectGateLog log)
    {
        if (SequenceBlackboardLayout.WalkFitsBlob(offset, count, blobLength)) return true;

        if (!SequenceBlackboardLayout.WalkOk(offset, count))
        {
            // The offset/count themselves are out of range; the caller's own once-per-generation
            // walk diagnostic already names them, so nothing extra is said here.
            return false;
        }

        if (!_extentRefusedLogged)
        {
            _extentRefusedLogged = true;
            log.Warning(SequenceBlackboardLayout.BlobExtentRefusedLog(offset, count, blobLength));
        }

        return false;
    }

    /// <summary>
    /// The one Info line per Core generation with the numbers the walk actually read, or null
    /// after it has been written. EV17: the guards refused on values nobody had ever printed, so
    /// a live client logging <c>path=none</c> gave no way to tell a stride mismatch from a blob
    /// that was not ready from a sequence with no Position/Rotation registers at all.
    /// </summary>
    internal string? WalkDiagnosticOnce(
        int observedSizeOf, int offset, int count, int position, int rotation, string inputs)
    {
        if (_walkLogged) return null;
        _walkLogged = true;
        return SequenceBlackboardLayout.WalkDiagnostic(
            observedSizeOf, offset, count, position, rotation, inputs);
    }

    /// <summary>
    /// Run the <c>BlackboardElement</c> probe if it has not run, then report whether the raw
    /// buffer write may run at all.
    ///
    /// <para>Both readings are reported and neither closes the gate, for the same reason as
    /// <see cref="InputWalkEnabled"/>: <c>GetBufferLength</c> is read as a byte count and the
    /// slot bounds come from the dump's element size, so a <c>TypeManager</c> or <c>sizeof</c>
    /// disagreement is evidence the dump looks stale and nothing more. EV15/EV16 latched on
    /// exactly this and took the whole blackboard re-point down process-wide.</para>
    ///
    /// <para>The gate itself is <see cref="BufferElementGuard.WritesAllowed"/> over two
    /// independently sourced constants - the size and the field extent summed from the dump's
    /// offsets - which is what makes it able to refuse a mistyped size at all (R31 finding 20).</para>
    /// </summary>
    internal bool BufferWritesEnabled(
        Func<int> readSizeOf, Func<int> readTypeManagerSize, IEffectGateLog log)
    {
        if (_elementSizeGate.Checked) return _elementSizeGate.Enabled;

        const int size = BufferElementGuard.BlackboardElementSize;
        const int packed = BufferElementGuard.BlackboardElementPackedBytes;
        const int alignment = BufferElementGuard.BlackboardElementAlignment;

        string? gateLog = _elementSizeGate.Check(
            BufferElementGuard.WritesAllowed(size, packed, alignment),
            SequenceBlackboardLayout.ConstantsInvariantLog(
                "BlackboardElement", size, packed, alignment));
        if (gateLog != null) log.Warning(gateLog);

        int sizeOf = Read(readSizeOf);
        string? note = BufferElementGuard.SizeReadingLog(
            "BlackboardElement", sizeOf, size, packed, alignment);
        if (note != null)
        {
            if (BufferElementGuard.SizeReadingIsWarning(sizeOf, size, packed, alignment))
                log.Warning(note);
            else
                log.Info(note);
        }

        int reported = Read(readTypeManagerSize);
        if (BufferElementGuard.TypeManagerDisagrees(reported, size) && !_elementCrossCheckLogged)
        {
            _elementCrossCheckLogged = true;
            log.Warning(BufferElementGuard.TypeManagerNote("BlackboardElement", reported, size));
        }

        return _elementSizeGate.Enabled;
    }

    /// <summary>
    /// Per-call verdict on one entity's <c>GetBufferLength</c>. An empty buffer is an ordinary
    /// sequence graph with no registers - skip it quietly; only a number no buffer can have is a
    /// refusal, and it is still a refusal of <em>this</em> call (R30 finding 9). EV17 latched
    /// here and one entity with an empty blackboard disabled the write for every sequence.
    /// </summary>
    internal bool BufferLengthUsable(int length, IEffectGateLog log)
    {
        var verdict = SequenceBlackboardLayout.ClassifyBufferLength(length);
        if (verdict == LineRendererBufferLayout.LengthVerdict.Ok) return true;

        if (verdict == LineRendererBufferLayout.LengthVerdict.TooSmall)
        {
            if (!_emptyBufferLogged)
            {
                _emptyBufferLogged = true;
                log.Info(SequenceBlackboardLayout.BufferEmptyLog());
            }

            return false;
        }

        if (!_bufferLengthRefusedLogged)
        {
            _bufferLengthRefusedLogged = true;
            log.Warning(SequenceBlackboardLayout.BufferLengthRefusedLog(length));
        }

        return false;
    }

    /// <summary>
    /// A runtime reading, or 0 when it threw. R28 finding 2's <c>catch { return; }</c> hole is
    /// closed by reading 0 rather than by latching: a <c>sizeof</c> that throws says nothing
    /// about a stride that came from the dump.
    /// </summary>
    private static int Read(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }
}
