using System;
using System.Collections.Generic;

namespace Progenitor.Dev.Ops;

/// <summary>Which garbage collectors ran during a frame. A frame can carry several.</summary>
[Flags]
internal enum PerfGcKinds : byte
{
    None = 0,
    /// <summary>The IL2CPP (Boehm) collector: <c>Il2CppSystem.GC.CollectionCount(0)</c> moved.</summary>
    Il2Cpp = 1,
    /// <summary>CLR <c>GC.CollectionCount(0)</c> moved (managed plugin heap).</summary>
    Gen0 = 2,
    Gen1 = 4,
    Gen2 = 8,
}

internal static class PerfGcKindNames
{
    public static List<string> Of(PerfGcKinds kinds)
    {
        var names = new List<string>(4);
        if ((kinds & PerfGcKinds.Il2Cpp) != 0) names.Add("il2cpp");
        if ((kinds & PerfGcKinds.Gen0) != 0) names.Add("clrGen0");
        if ((kinds & PerfGcKinds.Gen1) != 0) names.Add("clrGen1");
        if ((kinds & PerfGcKinds.Gen2) != 0) names.Add("clrGen2");
        return names;
    }

    /// <summary>Flags for the collections counted during one frame.</summary>
    public static PerfGcKinds From(int il2cpp, int gen0, int gen1, int gen2)
    {
        var kinds = PerfGcKinds.None;
        if (il2cpp > 0) kinds |= PerfGcKinds.Il2Cpp;
        if (gen0 > 0) kinds |= PerfGcKinds.Gen0;
        if (gen1 > 0) kinds |= PerfGcKinds.Gen1;
        if (gen2 > 0) kinds |= PerfGcKinds.Gen2;
        return kinds;
    }
}

/// <summary>
/// One frame's measurements beyond the original timing row: the IL2CPP heap, every collector's
/// count, the timed Harmony patch bodies, and the seam timestamps. Arrays are the caller's reused
/// scratch; they are copied, never retained.
/// </summary>
internal struct PerfFrameExtra
{
    /// <summary><c>il2cpp_gc_get_used_size</c> at the end of the frame's Core tick; -1 when unavailable.</summary>
    public long Il2CppUsedBytes;

    /// <summary><c>il2cpp_gc_get_heap_size</c>; -1 when unavailable.</summary>
    public long Il2CppHeapBytes;

    /// <summary>Collections counted during this frame, per collector.</summary>
    public int Il2CppCollections;
    public int Gen0;
    public int Gen1;
    public int Gen2;

    /// <summary>IL2CPP bytes attributed to each source column this frame (length <see cref="PerfSourceTable.MaxSources"/>).</summary>
    public long[]? Il2CppSourceBytes;

    /// <summary>Milliseconds and calls per timed patch body (<see cref="Progenitor.Dev.PerfPatchTimer"/> ids).</summary>
    public double[]? PatchMs;
    public int[]? PatchCalls;

    /// <summary>
    /// Offset in ms of each seam (<see cref="Progenitor.Dev.PerfSeams"/> ids) from the window
    /// start, the previous Core tick's end; NaN when the seam did not fire this frame.
    /// </summary>
    public double[]? SeamOffsetMs;

    /// <summary>Previous Core tick end to this Core tick start, in ms.</summary>
    public double WindowMs;

    public static PerfFrameExtra None => new() { Il2CppUsedBytes = -1, Il2CppHeapBytes = -1, WindowMs = double.NaN };
}

/// <summary>
/// Places a frame's time between two seams. The points are the window start (the previous Core
/// tick's end), every seam that fired, and the window end (this Core tick's start); the widest
/// stretch between two consecutive points is where the frame spent its time.
/// </summary>
internal static class PerfSeamGaps
{
    public const int WindowStart = -1;
    public static int WindowEnd => Progenitor.Dev.PerfSeams.Count;

    public const string WindowStartName = "coreTick.end(previous)";
    public const string WindowEndName = "coreTick.start";

    public static string Label(int point)
    {
        if (point < 0) return WindowStartName;
        if (point >= Progenitor.Dev.PerfSeams.Count) return WindowEndName;
        return Progenitor.Dev.PerfSeams.Names[point];
    }

    /// <summary>
    /// The widest stretch, in ms, and its bounding points. Allocation-free (the frame path calls
    /// it). A seam offset outside the window is clamped into it; NaN means "did not fire".
    /// Returns 0 with both points at the window bounds when the window is unusable.
    /// </summary>
    public static double Largest(double[]? offsets, double windowMs, out int from, out int to)
    {
        from = WindowStart;
        to = WindowEnd;
        if (double.IsNaN(windowMs) || windowMs <= 0) return 0;

        int seams = Progenitor.Dev.PerfSeams.Count;
        Span<double> at = stackalloc double[seams + 2];
        Span<int> id = stackalloc int[seams + 2];
        int n = 0;
        at[n] = 0; id[n] = WindowStart; n++;
        if (offsets != null)
        {
            for (int s = 0; s < seams && s < offsets.Length; s++)
            {
                double v = offsets[s];
                if (double.IsNaN(v)) continue;
                if (v < 0) v = 0;
                if (v > windowMs) v = windowMs;
                // insertion sort; ties keep seam order
                int k = n;
                while (k > 0 && at[k - 1] > v) { at[k] = at[k - 1]; id[k] = id[k - 1]; k--; }
                at[k] = v; id[k] = s; n++;
            }
        }
        at[n] = windowMs; id[n] = WindowEnd; n++;

        double best = -1;
        for (int i = 1; i < n; i++)
        {
            double gap = at[i] - at[i - 1];
            if (gap > best) { best = gap; from = id[i - 1]; to = id[i]; }
        }
        return best < 0 ? 0 : best;
    }
}

/// <summary>
/// The per-frame and whole-window detail <see cref="PerfRecording"/> keeps beside its timing ring:
/// IL2CPP heap growth and per-source attribution, collector counts and the frames they ran in,
/// timed patch bodies, and seam stamps. Ring arrays share the recording's slots; the totals and
/// the GC event list cover the whole window even when the ring wraps. Nothing allocates after
/// construction.
/// </summary>
internal sealed class PerfFrameDetail
{
    /// <summary>Frames with a collection kept in <see cref="GcEvents"/> (the rest are counted only).</summary>
    public const int MaxGcEvents = 512;

    public static int SeamCount => Progenitor.Dev.PerfSeams.Count;
    public static int PatchCount => Progenitor.Dev.PerfPatchTimer.Count;

    private readonly int _capacity;
    private readonly long[] _il2cppUsed;
    private readonly long[] _il2cppGrowth;
    private readonly long[] _il2cppAttributed;
    private readonly byte[] _gcKinds;
    private readonly float[] _patchMs;
    private readonly float[] _windowMs;
    private readonly float[] _seamMs;
    private readonly float[] _gapMs;
    private readonly sbyte[] _gapFrom;
    private readonly sbyte[] _gapTo;

    public PerfFrameDetail(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _il2cppUsed = new long[_capacity];
        _il2cppGrowth = new long[_capacity];
        _il2cppAttributed = new long[_capacity];
        _gcKinds = new byte[_capacity];
        _patchMs = new float[_capacity];
        _windowMs = new float[_capacity];
        _seamMs = new float[_capacity * SeamCount];
        _gapMs = new float[_capacity];
        _gapFrom = new sbyte[_capacity];
        _gapTo = new sbyte[_capacity];
        GcEvents = new PerfGcEvent[MaxGcEvents];
    }

    // ---- whole-window totals ------------------------------------------------------------------

    /// <summary>True once any frame carried an IL2CPP used-size reading.</summary>
    public bool Il2CppAvailable;
    public long Il2CppUsedFirst = -1;
    public long Il2CppUsedLast = -1;
    public long Il2CppHeapFirst = -1;
    public long Il2CppHeapLast = -1;

    /// <summary>Positive used-size growth summed over frames with no IL2CPP collection.</summary>
    public long Il2CppGrowthNonGc;

    /// <summary>Bytes attributed to Progenitor sources on those same frames.</summary>
    public long Il2CppAttributedNonGc;

    /// <summary>IL2CPP bytes per source column across the window.</summary>
    public readonly long[] SourceIl2CppBytes = new long[PerfSourceTable.MaxSources];

    public long Il2CppCollections;
    public long Gen0Collections;
    public long Gen1Collections;
    public long Gen2Collections;

    /// <summary>Sum of every recorded frame's frameMs, in seconds (the rate denominator).</summary>
    public double TotalSeconds;

    public readonly double[] PatchTotalMs = new double[Progenitor.Dev.PerfPatchTimer.Count];
    public readonly long[] PatchCalls = new long[Progenitor.Dev.PerfPatchTimer.Count];
    public readonly double[] PatchMaxFrameMs = new double[Progenitor.Dev.PerfPatchTimer.Count];

    /// <summary>Frames in which each seam fired at least once.</summary>
    public readonly int[] SeamFiredFrames = new int[Progenitor.Dev.PerfSeams.Count];

    public readonly PerfGcEvent[] GcEvents;
    public int GcEventCount;
    public int GcEventsDropped;

    private long _prevUsed = -1;

    // ---- write --------------------------------------------------------------------------------

    public void Add(int slot, double atSeconds, double frameMs, in PerfFrameExtra x)
    {
        if (!double.IsNaN(frameMs) && frameMs > 0) TotalSeconds += frameMs / 1000.0;

        long used = x.Il2CppUsedBytes;
        long growth = 0;
        if (used >= 0)
        {
            if (!Il2CppAvailable) { Il2CppAvailable = true; Il2CppUsedFirst = used; }
            Il2CppUsedLast = used;
            if (_prevUsed >= 0) growth = used - _prevUsed;
        }
        if (x.Il2CppHeapBytes >= 0)
        {
            if (Il2CppHeapFirst < 0) Il2CppHeapFirst = x.Il2CppHeapBytes;
            Il2CppHeapLast = x.Il2CppHeapBytes;
        }

        long attributed = 0;
        if (x.Il2CppSourceBytes != null)
        {
            for (int i = 0; i < PerfSourceTable.MaxSources && i < x.Il2CppSourceBytes.Length; i++)
            {
                long b = x.Il2CppSourceBytes[i];
                if (b <= 0) continue;
                SourceIl2CppBytes[i] += b;
                attributed += b;
            }
        }

        var kinds = PerfGcKindNames.From(x.Il2CppCollections, x.Gen0, x.Gen1, x.Gen2);
        Il2CppCollections += Math.Max(0, x.Il2CppCollections);
        Gen0Collections += Math.Max(0, x.Gen0);
        Gen1Collections += Math.Max(0, x.Gen1);
        Gen2Collections += Math.Max(0, x.Gen2);
        if ((kinds & PerfGcKinds.Il2Cpp) == 0 && growth > 0)
        {
            Il2CppGrowthNonGc += growth;
            Il2CppAttributedNonGc += Math.Min(attributed, growth);
        }

        double patchMs = 0;
        if (x.PatchMs != null)
        {
            for (int i = 0; i < PatchCount && i < x.PatchMs.Length; i++)
            {
                double ms = x.PatchMs[i];
                if (ms <= 0 && (x.PatchCalls == null || x.PatchCalls[i] == 0)) continue;
                PatchTotalMs[i] += ms;
                if (x.PatchCalls != null && i < x.PatchCalls.Length) PatchCalls[i] += x.PatchCalls[i];
                if (ms > PatchMaxFrameMs[i]) PatchMaxFrameMs[i] = ms;
                patchMs += ms;
            }
        }

        int seamBase = slot * SeamCount;
        for (int s = 0; s < SeamCount; s++)
        {
            double v = x.SeamOffsetMs != null && s < x.SeamOffsetMs.Length ? x.SeamOffsetMs[s] : double.NaN;
            _seamMs[seamBase + s] = (float)v;
            if (!double.IsNaN(v)) SeamFiredFrames[s]++;
        }
        double gap = PerfSeamGaps.Largest(x.SeamOffsetMs, x.WindowMs, out int from, out int to);

        _il2cppUsed[slot] = used;
        _il2cppGrowth[slot] = growth;
        _il2cppAttributed[slot] = attributed;
        _gcKinds[slot] = (byte)kinds;
        _patchMs[slot] = (float)patchMs;
        _windowMs[slot] = (float)x.WindowMs;
        _gapMs[slot] = (float)gap;
        _gapFrom[slot] = (sbyte)from;
        _gapTo[slot] = (sbyte)to;

        if (kinds != PerfGcKinds.None)
        {
            if (GcEventCount < MaxGcEvents)
            {
                GcEvents[GcEventCount++] = new PerfGcEvent
                {
                    AtSeconds = (float)atSeconds,
                    FrameMs = (float)frameMs,
                    Kinds = kinds,
                    Il2CppCollections = x.Il2CppCollections,
                    Il2CppUsedBefore = _prevUsed,
                    Il2CppUsedAfter = used,
                    GapMs = (float)gap,
                    GapFrom = (sbyte)from,
                    GapTo = (sbyte)to,
                };
            }
            else GcEventsDropped++;
        }

        if (used >= 0) _prevUsed = used;
    }

    public PerfFrameDetail Snapshot()
    {
        var c = new PerfFrameDetail(_capacity);
        Array.Copy(_il2cppUsed, c._il2cppUsed, _capacity);
        Array.Copy(_il2cppGrowth, c._il2cppGrowth, _capacity);
        Array.Copy(_il2cppAttributed, c._il2cppAttributed, _capacity);
        Array.Copy(_gcKinds, c._gcKinds, _capacity);
        Array.Copy(_patchMs, c._patchMs, _capacity);
        Array.Copy(_windowMs, c._windowMs, _capacity);
        Array.Copy(_seamMs, c._seamMs, _seamMs.Length);
        Array.Copy(_gapMs, c._gapMs, _capacity);
        Array.Copy(_gapFrom, c._gapFrom, _capacity);
        Array.Copy(_gapTo, c._gapTo, _capacity);
        c.Il2CppAvailable = Il2CppAvailable;
        c.Il2CppUsedFirst = Il2CppUsedFirst;
        c.Il2CppUsedLast = Il2CppUsedLast;
        c.Il2CppHeapFirst = Il2CppHeapFirst;
        c.Il2CppHeapLast = Il2CppHeapLast;
        c.Il2CppGrowthNonGc = Il2CppGrowthNonGc;
        c.Il2CppAttributedNonGc = Il2CppAttributedNonGc;
        Array.Copy(SourceIl2CppBytes, c.SourceIl2CppBytes, SourceIl2CppBytes.Length);
        c.Il2CppCollections = Il2CppCollections;
        c.Gen0Collections = Gen0Collections;
        c.Gen1Collections = Gen1Collections;
        c.Gen2Collections = Gen2Collections;
        c.TotalSeconds = TotalSeconds;
        Array.Copy(PatchTotalMs, c.PatchTotalMs, PatchTotalMs.Length);
        Array.Copy(PatchCalls, c.PatchCalls, PatchCalls.Length);
        Array.Copy(PatchMaxFrameMs, c.PatchMaxFrameMs, PatchMaxFrameMs.Length);
        Array.Copy(SeamFiredFrames, c.SeamFiredFrames, SeamFiredFrames.Length);
        Array.Copy(GcEvents, c.GcEvents, GcEventCount);
        c.GcEventCount = GcEventCount;
        c.GcEventsDropped = GcEventsDropped;
        c._prevUsed = _prevUsed;
        return c;
    }

    // ---- read, by physical slot ---------------------------------------------------------------

    public long Il2CppUsed(int slot) => _il2cppUsed[slot];
    public long Il2CppGrowth(int slot) => _il2cppGrowth[slot];
    public long Il2CppAttributed(int slot) => _il2cppAttributed[slot];
    public PerfGcKinds GcKinds(int slot) => (PerfGcKinds)_gcKinds[slot];
    public double PatchMs(int slot) => _patchMs[slot];
    public double WindowMs(int slot) => _windowMs[slot];
    public double SeamMs(int slot, int seam) => _seamMs[slot * SeamCount + seam];
    public double GapMs(int slot) => _gapMs[slot];
    public int GapFrom(int slot) => _gapFrom[slot];
    public int GapTo(int slot) => _gapTo[slot];
}

/// <summary>One frame in which at least one collector ran.</summary>
internal struct PerfGcEvent
{
    public float AtSeconds;
    public float FrameMs;
    public PerfGcKinds Kinds;
    public int Il2CppCollections;
    public long Il2CppUsedBefore;
    public long Il2CppUsedAfter;
    public float GapMs;
    public sbyte GapFrom;
    public sbyte GapTo;
}
