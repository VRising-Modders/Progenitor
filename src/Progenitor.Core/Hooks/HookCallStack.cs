using System;

namespace Progenitor.Hooks;

/// <summary>
/// The prefix-to-postfix hand-off stack behind <see cref="SystemHooks"/>, kept game-free so the
/// unwind rules are testable without IL2CPP types.
///
/// <para><b>Why this is not just a list.</b> The prefix pushes a frame and the postfix pops it,
/// but Harmony <b>skips the postfix when the original throws</b>. A patched
/// <c>SystemBase.Update</c> that throws therefore pushes and never pops, and — because a system
/// group catches per-system exceptions and keeps going — the process carries on. The pushed
/// frame is then never unwound: it holds a <c>World</c> and the resolved <c>Entry[]</c> of
/// handler delegates, which is exactly the shape <see cref="SystemHooks.RemoveAll"/> exists to
/// release ("a leftover hook cannot pin a collectible plugin ALC"). A frame stranded here is
/// unreachable from that sweep, so a plugin's delegates outlive its Unload. Job FLATCH1.</para>
///
/// <para><b>The boundary.</b> A single <c>SystemBase.Update</c> call cannot span two rendered
/// frames, so <em>every</em> entry still on this stack that was pushed before the current frame
/// stamp is abandoned by construction — no heuristic, no depth cap, no guess about nesting.
/// <see cref="NextFrame"/> is called once per rendered frame (from <c>SystemHooks.OnFrame</c>,
/// which is itself deduped on <c>Time.frameCount</c>) and drops exactly those. Entries pushed in
/// the current frame are left alone, because <c>OnFrame</c> runs inside <c>BootstrapSystem</c>'s
/// own update and legitimately has live frames underneath it.</para>
///
/// <para>Stamps are non-decreasing from the bottom of the stack upward — pushes only ever append
/// and the stamp only ever advances — so the abandoned entries are always a bottom prefix and
/// the survivors keep their LIFO order after compaction. Dropped slots are zeroed so nothing
/// they referenced is retained; this type holds no reference of its own beyond the live frames.
/// </para>
///
/// <para><b>This stack pairs by token too, since job FHOOK3.</b> It used to pop positionally, and
/// two earlier jobs declined to change that because <see cref="SystemHooks.PostfixManaged"/> is
/// reached through the <c>ManagedUpdatePostfix</c> slot delegate, which carried one <c>bool</c>
/// and had nowhere to put a frame identity. That delegate now carries a <c>long</c> call token
/// instead — the same widening job FHOOK2 made to Harmony's <c>__state</c> on Bootstrap's side,
/// carried one hop further across the ALC boundary. <see cref="Push"/> mints the token, Core hands
/// it back through the prefix's <c>ref long</c>, Bootstrap keeps it in its own
/// <c>AfterCallStack</c> entry and returns it to <see cref="SystemHooks.PostfixManaged"/>, and
/// <see cref="Release"/> takes it. A caller that swallows an inner system's throw therefore
/// releases <em>its own</em> frame and dispatches its own After handlers.</para>
///
/// <para><b>Why the token was worth a cross-ALC ABI change.</b> The residue it closes is not the
/// rare double-nesting it looks like. The outer, catching system only has to carry After handlers,
/// and Progenitor registers exactly that itself: <c>VirtualSystems.EnsureRelay</c> installs
/// <c>SystemHooks.After(typeof(SimulationSystemGroup))</c> the first time any plugin calls
/// <c>ctx.Systems.Add</c> with the default anchor, which is the framework's own recommended
/// per-tick seam. <c>SimulationSystemGroup</c> derives from <c>SystemBase</c> (so its
/// <c>Update</c> is patched) and, as a system group, catches each child's exception and keeps
/// going. A positional pop therefore hands the group's postfix the inner system's frame: the
/// inner system's After handlers run at the wrong point, and every virtual system scheduled on
/// the default anchor is silently skipped — for good, if the inner system throws every frame.
/// </para>
///
/// <para>The frame sweep below is unchanged and is not made redundant: it is the liveness backstop
/// for a strand no token covers — the outermost patched <c>Update</c> on a chain throwing, with
/// the catch sitting somewhere that is not itself a patched <c>SystemBase.Update</c>. Token for
/// pairing, sweep for liveness.</para>
/// </summary>
internal sealed class HookCallStack<T> where T : struct
{
    private struct Slot
    {
        public long Stamp;
        public long Token;
        public T Frame;
    }

    private Slot[] _slots;
    private int _depth;
    private long _stamp;
    private long _abandoned;
    private long _nextToken;

    internal HookCallStack(int capacity = 8)
    {
        if (capacity < 1) capacity = 1;
        _slots = new Slot[capacity];
    }

    /// <summary>Frames currently pushed and not yet popped.</summary>
    internal int Depth => _depth;

    /// <summary>The rendered-frame stamp new pushes are tagged with.</summary>
    internal long FrameStamp => _stamp;

    /// <summary>How many frames have been released as abandoned since this stack was created.</summary>
    internal long AbandonedTotal => _abandoned;

    /// <summary>
    /// Push one prefix's call frame and return the token its postfix must hand back to
    /// <see cref="Release"/>.
    ///
    /// <para>Tokens come from a counter that only ever increases and is never reset — not by
    /// <see cref="Clear"/>, not by the sweep. A token therefore names one push for the life of
    /// this Core generation (and across a <c>ReinstallCurrent</c> restore-in-place, where these
    /// statics survive), so a postfix whose frame was dropped underneath it cannot match a later
    /// push that happened to land in the same slot. Zero is never minted and means "this prefix
    /// pushed nothing", which is the same one-compare test the old <c>bool</c> hand-off answered.
    /// </para>
    /// </summary>
    internal long Push(in T frame)
    {
        if (_depth >= _slots.Length) Array.Resize(ref _slots, _slots.Length * 2);
        long token = ++_nextToken;
        _slots[_depth].Stamp = _stamp;
        _slots[_depth].Token = token;
        _slots[_depth].Frame = frame;
        _depth++;
        return token;
    }

    /// <summary>
    /// Release the frame <see cref="Push"/> minted <paramref name="token"/> for, and return it.
    ///
    /// <para>Everything above it is dropped as abandoned in the same pass. This stack is strictly
    /// nested and single-threaded, so anything pushed after that frame and still present did not
    /// come back through its own postfix — it is a strand from a throw this caller swallowed, and
    /// it is released here rather than one rendered frame later by the sweep.</para>
    ///
    /// <para>Returns <c>default</c> and drops nothing when the token is not on the stack: a zero
    /// token (the prefix pushed nothing), or a frame <see cref="Clear"/> took at a Core teardown
    /// nested inside this very update. That is the unpaired-postfix case — a Core swap mid-update
    /// — which the positional pop used to answer by handing back somebody else's frame.</para>
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
    /// Advance to the next rendered frame, releasing every entry left over from an earlier one.
    /// Returns how many were released so the caller can report them; the running total is on
    /// <see cref="AbandonedTotal"/>.
    ///
    /// <para>A strand is released on the frame <em>after</em> the one it was pushed in, and that
    /// latency is inherent, not slack: this runs from inside <c>BootstrapSystem</c>'s own update,
    /// where an entry pushed earlier in this same frame is indistinguishable from a live outer
    /// system whose update we are nested inside. Waiting one frame makes the verdict exact.</para>
    /// </summary>
    internal int NextFrame()
    {
        int dropped = DropStale();
        _stamp++;
        return dropped;
    }

    /// <summary>
    /// Drop everything, live frames included. Only for Core teardown, where the alternative is
    /// carrying a <c>World</c> and a handler array past <c>Uninstall</c>; the mirror of
    /// Bootstrap's <c>HarmonyPatches.ClearAfterSnapshots</c>.
    ///
    /// <para>Job FHOOK3: the token counter is deliberately <em>not</em> reset here, so a postfix
    /// still in flight across the teardown releases nothing rather than releasing a fresh frame
    /// that reused its slot. The abandoned counter is deliberately not reset either, and that is a
    /// different field and a different reason — see <c>SystemHooks.DescribeCounts</c>, where the
    /// wanted quantity is how many frames this ALC has ever stranded. Do not make either
    /// symmetric with Bootstrap's on sight.</para>
    /// </summary>
    internal void Clear()
    {
        Array.Clear(_slots, 0, _slots.Length);
        _depth = 0;
    }

    private int DropStale()
    {
        if (_depth == 0) return 0;
        int stale = 0;
        while (stale < _depth && _slots[stale].Stamp != _stamp) stale++;
        if (stale == 0) return 0;

        int live = _depth - stale;
        for (int i = 0; i < live; i++)
            _slots[i] = _slots[stale + i];
        for (int i = live; i < _depth; i++)
            _slots[i] = default;

        _depth = live;
        _abandoned += stale;
        return stale;
    }
}

/// <summary>
/// Job FDOC5 (R45 finding 32). The one-shot operator line for <see cref="HookCallStack{T}"/>'s
/// sweep, kept here for the same reason Bootstrap's <c>AfterCallStrand</c> is kept beside its
/// stack: <c>SystemHooks</c> is <c>unsafe</c> and binds ProjectM and Unity.Entities, so nothing
/// in the suite can compile it, and a sentence a test cannot read is a sentence that drifts.
/// </summary>
internal static class HookStrandReport
{
    /// <summary>
    /// <b>What the number is, and what it is not</b> (job FDOC5, R45 finding 32). <c>NextFrame</c>
    /// counts the call frames still pushed from an earlier rendered frame. That is a frame whose
    /// own postfix never released it — nothing here observes an exception — and Harmony skipping
    /// the postfix of a throwing original is the cause that produces it.
    ///
    /// <para><b>The caveat FDOC5 wrote here is gone, because job FHOOK3 removed the thing it
    /// described.</b> This stack used to pop positionally, so when a system group swallowed an
    /// inner system's throw the frame the sweep released belonged to the <em>outer</em> system,
    /// which had not thrown, and the line had to warn an operator not to read it as naming the
    /// thrower. <see cref="HookCallStack{T}"/> now releases by token, so a caller that catches
    /// takes its own frame and drops the strand above it in the same pass; the frames this sweep
    /// still releases are the ones nothing enclosing ever released, which are the frames whose own
    /// postfix was skipped. Attribution is sound again, and the caveat would now be false. Keep
    /// the shape FDOC5 established — measurement first, cause named as a cause — because the count
    /// is still not something that watched an exception happen.</para>
    /// </summary>
    internal static string Report(int dropped) =>
        "SystemHooks: released " + dropped.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " hook call frame(s) that were never released by their own postfix. That is what is"
        + " counted; the cause is a system that threw, because Harmony skips the postfix when the"
        + " patched original throws. Since job FHOOK3 this stack pairs by call token, so a caller"
        + " that swallowed the throw released its own frame and these are the frames of the calls"
        + " that did not come back. The throw itself is logged by whatever caught it. Further"
        + " releases are counted in the pin dump only.";
}
