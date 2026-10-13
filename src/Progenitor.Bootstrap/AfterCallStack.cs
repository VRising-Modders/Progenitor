using System;

namespace Progenitor.Bootstrap;

/// <summary>
/// Job FHOOK1, completed by job FHOOK2. The prefix-to-postfix hand-off stack behind
/// <c>HarmonyPatches</c>'s <c>SystemBase.Update</c> and
/// <c>WorldUnmanagedImpl.UnmanagedUpdate</c> hooks, kept game-free so the unwind rules are
/// testable without Harmony or IL2CPP types.
///
/// <para><b>Why this is not just an array.</b> Harmony <b>skips the postfix when the patched
/// original throws</b>. A system whose <c>Update</c> throws therefore pushes an
/// <c>AfterCall</c> that is never released, and — because a system group catches per-system
/// exceptions and keeps going — the process carries on and does it again on the next frame. The
/// old array grew one entry per throw and was emptied only by
/// <c>HarmonyPatches.ClearAfterSnapshots</c> at a Core unload, so on a server that never reloads
/// Core a system throwing every frame grew it for the whole process lifetime. Bootstrap is not
/// collectible: nothing here is ever released by an ALC going away.</para>
///
/// <para><b>Two mechanisms, and they are not redundant.</b> A strand is released by whichever of
/// these reaches it first, and each covers a case the other cannot:</para>
///
/// <para><b>1. The token (job FHOOK2), which is what makes the pairing <em>correct</em>.</b>
/// <see cref="Push"/> mints a process-unique token and <see cref="Release"/> takes it back. A
/// postfix releases <em>its own</em> frame by identity, not whatever happens to be on top. That
/// matters because LIFO pairing only survives while every caller of a throwing <c>Update</c> lets
/// the exception propagate: a caller that <em>catches</em> and returns normally — the standard
/// system-group per-system try/catch — used to pop positionally and take the inner system's
/// stranded hand-off, dispatching another call's After with another call's postfix delegate
/// snapshot. Releasing by token makes that structurally impossible rather than merely rare.
/// Because the stack is strictly nested and single-threaded, everything still sitting
/// <em>above</em> the released frame was pushed inside this call and did not come back, so
/// <see cref="Release"/> drops it as abandoned in the same pass: a strand that some enclosing
/// patched <c>Update</c> eventually releases is gone the moment that caller returns, not one
/// frame later.</para>
///
/// <para><b>2. The frame-epoch sweep (job FHOOK1), which is the <em>liveness</em> backstop.</b>
/// The token only reaches strands that lie above a frame somebody later releases. A strand that
/// nothing encloses is never covered: if the outermost patched <c>Update</c> on a call chain
/// throws and the catch sits in something that is <b>not</b> itself a patched
/// <c>SystemBase.Update</c> — Unity's player loop, a group we did not patch, or (routinely, for
/// the unmanaged stack) the unmanaged caller of <c>WorldUnmanagedImpl.UnmanagedUpdate</c> — then
/// that entry sits at the bottom of the stack and every later push lands and releases above it.
/// It would leak for the process lifetime. So the sweep stays: a single <c>Update</c> call cannot
/// span two rendered frames, so <em>every</em> entry still pushed from before the current frame
/// epoch is abandoned by construction — no heuristic, no depth cap, no timeout, no guess about
/// nesting. Bootstrap's clock is <c>ProcessHost.FrameCount</c>, the deduped
/// <c>UnityEngine.Time.frameCount</c> that <c>ProcessHost.Tick</c> publishes once per rendered
/// frame, which is the same clock Core's sweep ultimately runs on (<c>Plugin.Frame</c> is raised
/// from that tick).</para>
///
/// <para><b>The one state in which this backstop stops</b> (job FDOC5, R45 finding 34; stated
/// rather than fixed, because the fix is a hot-path change nothing can verify without the game).
/// <c>ProcessHost.FrameCount</c> advances only inside <c>ProcessHost.Tick</c>, and <c>Tick</c> has
/// three callers: <c>FrameTicker.Update</c>, and <b>postfixes</b> on the two bootstrap systems'
/// <c>OnUpdate</c>. If <c>FrameTicker.Install</c> throws — it is caught and logged, and the
/// process carries on — the clock runs on the postfixes alone; and Harmony skips a postfix when
/// the original throws, so a bootstrap system that throws every frame freezes <c>_lastFrame</c>,
/// <c>DropStale</c> never fires again, and the sweep half is inert in the exact failure it exists
/// for. Core's <c>Plugin.Frame</c> sweep is raised from the same tick and stops with it. What
/// still bounds the stack in that state is the <em>token</em> half, which does not read the clock
/// at all: any enclosing patched <c>Update</c> that returns releases its own frame and everything
/// above it. So a strand that nothing encloses is the case that would then grow unbounded. The
/// remedy — making the clock advance from something a throwing original cannot skip, i.e. a
/// finalizer — is the same trade-off as the paragraph below, and unmeasured for the same
/// reason.</para>
///
/// <para><b>Why the sweep runs from <see cref="Push"/> rather than from a per-frame callback.</b>
/// Core's stack is swept by <c>SystemHooks.OnFrame</c>; the Bootstrap-side equivalent would be a
/// call added to <c>ProcessHost.Tick</c>. Reading the counter at the push instead keeps the
/// release welded to the only thing that can grow the stack — a sweep that runs on no other
/// schedule cannot be reordered away or skipped by a future edit to Tick's ordering block, and a
/// frame in which nothing is pushed is a frame in which nothing has to be released. The cost is
/// one <c>int</c> compare against a static field per patched <c>Update</c>; the counter is read
/// from <c>ProcessHost</c> rather than from <c>UnityEngine.Time</c> so the hot path never enters
/// a Unity ICall.</para>
///
/// <para><b>Why the epoch, and not the frame number itself — corrected by job FDOC5 (R45 finding
/// 34), because the arithmetic was right and the justification was wrong, which is the worse way
/// round.</b> The epoch is a <em>relabeling</em> of the sequence of distinct <c>processFrame</c>
/// values: <see cref="Push"/> advances <c>_epoch</c> exactly when <c>_frame</c> changes, so
/// stamping entries with <c>_epoch</c> and dropping <c>Stamp != _epoch</c> selects the identical
/// set as stamping them with <c>processFrame</c> and dropping <c>Stamp != _frame</c> would. It
/// does <b>not</b> buy a wider release window, and the two claims this paragraph used to make
/// were both false. <c>ProcessHost.Tick</c> does run as a postfix on
/// <c>ServerBootstrapSystem.OnUpdate</c>/<c>ClientBootstrapSystem.OnUpdate</c>, inside a patched
/// <c>SystemBase.Update</c>, so the counter does advance mid-frame — but entries pushed before
/// that tick see <c>FrameCount</c> N−1 and entries pushed after it see N, so they land in
/// <em>different</em> epochs and are not "released together"; and comparing against the previous
/// raw frame number would not have released the live outer frames either, because it is the same
/// comparison. What the epoch actually buys is <b>monotone stamps</b>: they are non-decreasing
/// from the bottom of the stack upward by construction, whatever <c>Time.frameCount</c> does, so
/// the abandoned entries are always a bottom prefix and <c>DropStale</c>'s single forward scan and
/// compaction are correct even if the frame counter wrapped or went backward. That property is
/// what the rest of this class is written against; keep it.</para>
///
/// <para>The real boundary, stated plainly because the epoch's name suggests otherwise: entries
/// are grouped by <b>the tick</b>, not by the rendered frame. Everything pushed after tick N and
/// before tick N+1 shares one epoch — which straddles the frame boundary — and is released at the
/// first push after tick N+1. The one-frame latency is the same one Core accepts and is inherent:
/// inside a system update, an entry pushed earlier in this frame is indistinguishable from a live
/// outer frame we are nested inside.</para>
///
/// <para><b>Why not a Harmony finalizer, which would remove the strand outright</b> (job FDOC5,
/// R45 finding 34 — the alternative neither doc named). A finalizer runs on the throwing path too,
/// which is the exact property whose absence creates the strand: <c>HarmonyPatches</c> already
/// ships one (<c>StunStartHook.Finalizer</c>, patched with the same <c>ref bool __state</c>
/// hand-off shape), so this is not a mechanism the file would have to invent. Both docs used to
/// rule out a <em>different</em> alternative — widening the <c>__state</c> ABI — and never
/// mentioned this one. The counterweight is that these prefixes run for <b>every system, every
/// frame</b>, and a finalizer makes Harmony wrap the original in a try/catch on the hottest
/// patched methods in the process, where the sweep costs one <c>int</c> compare per push. That is
/// the reason to keep the sweep, and it is a reason, not a measurement: <b>nobody has measured the
/// finalizer's per-frame cost on this build.</b> Settling it needs a running client or server —
/// two builds, the same scene, frame times compared — so it is owed rather than answered here. Do
/// not read the sweep's survival as evidence that the finalizer was tried and was slower.</para>
///
/// <para>Stamps are non-decreasing from the bottom of the stack upward — pushes only ever append,
/// the epoch only ever advances, and <see cref="Release"/> only ever truncates from the top — so
/// the abandoned entries the sweep looks for are always a bottom prefix and the survivors keep
/// their order after compaction. Dropped slots are zeroed so the Core-owned postfix delegate they
/// carried is not retained. Nothing on the hot path allocates, locks or throws: <see cref="Push"/>
/// is an <c>int</c> compare plus a store, and <see cref="Release"/> is one <c>long</c> compare in
/// the paired case. Main thread only, as the arrays it replaces always were.</para>
///
/// <para><b>Core's half, closed by job FHOOK3.</b> This token pairs <em>Bootstrap's</em> hand-off,
/// which is what decides whether Core's postfix delegate is called at all and which snapshot of it
/// is used. Core's own <c>Progenitor.Hooks.HookCallStack</c> used to be popped positionally,
/// because the slot delegate that carries the hand-off across the ALC boundary
/// (<c>HarmonySlots.ManagedUpdatePostfix</c>) took a single <c>bool</c> and had nowhere to put a
/// token. That delegate now carries a <c>long callToken</c>: Core mints it, hands it out through
/// the prefix's <c>ref long</c>, this stack keeps it in <c>AfterCall.CoreToken</c>, and the
/// postfix hands it back. Both stacks therefore pair by token and the frame each releases is its
/// own.</para>
///
/// <para>What that cost, recorded here because the next reader will ask: widening those four
/// declarations is a Bootstrap↔Core contract change, so it bumps
/// <c>CoreLayout.ProtocolVersion</c>/<c>BootstrapLink.ProtocolVersion</c> to 4 and needs a restart
/// of both processes with the deploy landing while they are down. The bump is not overhead, it is
/// the safety: it turns a half-deployed pair into a refused load with both numbers in the log,
/// instead of <c>Delegate.CreateDelegate</c> throwing in <c>Bind</c> and taking the site Inert
/// (FLATCH1's measured failure).</para>
/// </summary>
internal sealed class AfterCallStack<T> where T : struct
{
    private struct Slot
    {
        public long Stamp;
        public long Token;
        public T Frame;
    }

    private Slot[] _slots;
    private int _depth;
    private long _epoch;
    private long _abandoned;
    private long _nextToken;
    private int _frame = int.MinValue;

    internal AfterCallStack(int capacity = 16)
    {
        if (capacity < 1) capacity = 1;
        _slots = new Slot[capacity];
    }

    /// <summary>Frames currently pushed and not yet released.</summary>
    internal int Depth => _depth;

    /// <summary>How many frames have been released as abandoned since this stack was created.</summary>
    internal long AbandonedTotal => _abandoned;

    /// <summary>
    /// Push one prefix's hand-off, first releasing anything left over from an earlier frame
    /// epoch, and return the token its postfix must hand back to <see cref="Release"/>.
    /// <paramref name="processFrame"/> is <c>ProcessHost.FrameCount</c>; any value that changes
    /// once per rendered frame and never changes during one satisfies the rule.
    ///
    /// <para>Tokens are minted from a counter that only ever increases and is never reset — not
    /// by <see cref="Clear"/>, not by the sweep. A token therefore names one push for the life of
    /// the process, so a postfix whose frame was dropped underneath it (a Core unload nested
    /// inside the Update, which <see cref="Clear"/>s) cannot match some later push that happens
    /// to land in the same slot. Zero is never minted and means "this prefix pushed nothing".</para>
    /// </summary>
    internal long Push(in T frame, int processFrame)
    {
        if (processFrame != _frame)
        {
            _frame = processFrame;
            DropStale();
            _epoch++;
        }

        if (_depth >= _slots.Length) Array.Resize(ref _slots, _slots.Length * 2);
        long token = ++_nextToken;
        _slots[_depth].Stamp = _epoch;
        _slots[_depth].Token = token;
        _slots[_depth].Frame = frame;
        _depth++;
        return token;
    }

    /// <summary>
    /// Release the frame <see cref="Push"/> minted <paramref name="token"/> for, and return it.
    ///
    /// <para>Everything above it is dropped as abandoned in the same pass. The stack is strictly
    /// nested and single-threaded, so anything pushed after this frame and still present did not
    /// come back through its own postfix — it is a strand from a throw this caller swallowed, and
    /// it is released here rather than one rendered frame later by the sweep.</para>
    ///
    /// <para>Returns <c>default</c> and drops nothing when the token is not on the stack: a zero
    /// token (the prefix pushed nothing), or a frame that <see cref="Clear"/> took at a Core
    /// unload nested inside this very Update. That is the unpaired-postfix case the positional
    /// pop used to answer by handing back somebody else's hand-off.</para>
    /// </summary>
    internal T Release(long token)
    {
        if (token == 0) return default;

        int index = _depth - 1;
        while (index >= 0 && _slots[index].Token != token) index--;
        if (index < 0) return default;

        var frame = _slots[index].Frame;
        int above = _depth - 1 - index;
        for (int i = _depth - 1; i >= index; i--) _slots[i] = default;
        _depth = index;
        _abandoned += above;
        return frame;
    }

    /// <summary>
    /// Drop everything, live frames included. Only for a Core unload, where the alternative is
    /// carrying generation N's postfix delegate past <c>Unload</c>; this is what
    /// <c>HarmonyPatches.ClearAfterSnapshots</c> has always done and it keeps doing it. The token
    /// counter is deliberately <em>not</em> reset, so a postfix still in flight across the unload
    /// releases nothing rather than releasing a fresh frame that reused its slot.
    ///
    /// <para>Job FLATCH2 (R45 finding 14). The abandoned <em>counter</em> is reset here, and it is
    /// a different field from the token counter above. <c>HarmonyPatches.ClearAfterSnapshots</c>
    /// re-arms the strand-report latch on this same call, so the latch was generation-scoped while
    /// the number it guards was process-scoped: from the second Core generation on,
    /// <c>ReportStrands</c> saw a non-zero total with a fresh latch and printed "released N After
    /// hand-off(s) ... left behind by an update that threw" on the very first patched
    /// <c>SystemBase.Update</c> of that generation - before anything in it had run, let alone
    /// thrown. It was also permanently poisoning: the count never returned to zero, so a clean
    /// generation could not be observed and the number printed was the process total rather than
    /// this generation's. A diagnostic must never report a state it did not observe. The latch and
    /// the count now re-arm together, on the boundary they both describe. Live frames dropped by
    /// this call are still not counted as abandoned: they are not a strand, they are a teardown.
    /// </para>
    /// </summary>
    internal void Clear()
    {
        Array.Clear(_slots, 0, _slots.Length);
        _depth = 0;
        _abandoned = 0;
    }

    private void DropStale()
    {
        if (_depth == 0) return;
        int stale = 0;
        while (stale < _depth && _slots[stale].Stamp != _epoch) stale++;
        if (stale == 0) return;

        int live = _depth - stale;
        for (int i = 0; i < live; i++)
            _slots[i] = _slots[stale + i];
        for (int i = live; i < _depth; i++)
            _slots[i] = default;

        _depth = live;
        _abandoned += stale;
    }
}

/// <summary>
/// Job FHOOK1. The one-shot operator line for <see cref="AfterCallStack{T}"/> releases, kept
/// here so its wording is testable; <c>HarmonyPatches</c> itself compiles only against the game.
/// </summary>
internal static class AfterCallStrand
{
    /// <summary>
    /// Reported once per Core generation (re-armed by <c>HarmonyPatches.ClearAfterSnapshots</c>,
    /// the same boundary the per-site throw-log latch uses).
    ///
    /// <para><b>Job FDOC5 (R45 finding 32): the line says what was measured, and gives the throw
    /// as the cause rather than as an observation.</b> The number comes from two places, and
    /// neither of them sees an exception: <see cref="AfterCallStack{T}.Release"/> counts the
    /// entries sitting above the frame a postfix just released, and <c>DropStale</c> counts the
    /// entries left over from an earlier frame epoch. Both are the same fact — a hand-off whose
    /// own postfix never handed it back — and Harmony skipping the postfix of a throwing original
    /// is the cause that produces it, not something this counter established. It said "left
    /// behind by an update that threw", which sends an operator looking for a throwing system on
    /// the strength of a count that never looked.</para>
    ///
    /// <para>The <em>attribution</em> is sound on both sides now, and it was not when this was
    /// written. Since job FHOOK2 this stack is released by token, so a caller that swallows an
    /// inner system's throw releases its own frame and the entries counted above it really are the
    /// ones that never came back. Core's <c>HookCallStack</c> was still popped positionally when
    /// FDOC5 landed, so its twin line carried a caveat naming the outer system; job FHOOK3 widened
    /// the slot delegates to carry a <c>long callToken</c> and removed both the mispairing and the
    /// caveat. What survives on both sides is the rule this line is shaped by: the number is a
    /// count of hand-offs, not an observation of a throw. See <c>HookStrandReport</c>.</para>
    /// </summary>
    internal static string Report(string site, long total) =>
        "HarmonyPatches: released " + total.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " After hand-off(s) at " + (string.IsNullOrEmpty(site) ? "an update hook" : site)
        + " that were never handed back by their own postfix. That is what is counted; the cause"
        + " is an update that threw, because Harmony skips the postfix when the patched original"
        + " throws. Later releases are counted, not logged; the throw itself is logged where it"
        + " was caught.";
}
