using System;
using System.Collections.Generic;

namespace Progenitor.Events;

/// <summary>
/// Which worlds <see cref="EventHooks.WatchWorlds"/> has already published a
/// <see cref="WorldReady"/> for (job FEVT1, R44 finding 3).
///
/// <para><b>The bug this replaces.</b> The ledger used to be a <c>HashSet&lt;string&gt;</c> of world
/// <i>names</i>, and <c>WatchWorlds</c> samples <c>World.s_AllWorlds</c> once per
/// <c>Plugin.Frame</c> — a cadence the game does not share. The client destroys and rebuilds
/// <c>Client_0</c> on every disconnect, rejoin and server change, so between two of our frames the
/// old world can go and a new one arrive under the same name. The name was still in the set,
/// <c>Add</c> returned false, and no <c>WorldReady</c> was published for the world that actually
/// existed. <c>Remove</c> only ran on a frame that saw <i>no</i> world at all, which a
/// destroy-and-create straddling one frame gap never produces. Every mod that initializes on
/// <c>WorldReady</c> then silently never initialized for that session: its panels and keybinds do
/// nothing until the client is restarted. <c>ClientWorldScanPolicy</c> already treats exactly this
/// straddle as reachable — <i>"a count cannot tell 'the same worlds' from 'one destroyed and
/// another created'"</i> (job FEPOCH4, R35 finding 7) — and redesigned the epoch watch around it;
/// this watch never got the same treatment.</para>
///
/// <para><b>The identity is <c>World.SequenceNumber</c>,</b> the same number
/// <see cref="Progenitor.WorldState.ClientWorldEpoch"/> and <c>EventHooks.ChatHook.GetQuery</c>
/// already key on. Note carefully <i>why</i> that is right here and wrong in
/// <see cref="Progenitor.WorldState.WorldEpoch"/>: the sequence number is assigned once in the
/// <c>World</c> constructor and never reused, so it cannot move across a <b>save reload</b>, which
/// runs as a normal tick inside the existing world (R31 finding 2) — <c>WorldEpoch</c> rejects it
/// as an <i>edge</i> for that reason. Here the event being detected <b>is</b> a new <c>World</c>,
/// so the same property is exactly the <i>identity</i> the name was failing to be. Publish when
/// the sequence number under a name differs from the one we published for.</para>
///
/// <para><b>An unreadable sequence number degrades to the old behavior, never to a storm.</b>
/// Reading <c>SequenceNumber</c> is an Il2Cpp property getter on a wrapper the game owns and it can
/// throw; <see cref="UnknownSequence"/> is what the caller passes then. A world first seen with an
/// unknown identity is published for (a missed <c>WorldReady</c> is the failure that matters) and
/// then <i>not</i> published for again while it stays unknown — so a getter that throws on every
/// frame costs one event, not sixty a second. When a real number later becomes readable it is
/// recorded without publishing, so the next genuine swap is caught. <b>The one residual gap,</b>
/// stated rather than hidden: a swap that happens entirely within a run of unreadable frames is
/// missed, exactly as the name-keyed ledger missed every swap.</para>
///
/// <para>Zero is the "no identity" sentinel because Unity assigns sequence numbers from 1 upward,
/// and because <c>WorldLoadHook.ClientTick</c> already uses <c>0UL</c> for "there is no client
/// world".</para>
///
/// <para>Game-free on purpose — it takes a <c>string</c> and a <c>ulong</c>, so the whole rule is
/// unit-tested with no V Rising install, including the destroy-and-recreate case that has no other
/// guard. Locked because <c>DescribePins</c> reads <see cref="Count"/> off the diagnostics path
/// while the game main thread is writing.</para>
/// </summary>
internal sealed class WorldReadyLedger
{
    /// <summary>What the caller passes when it could not read the world's <c>SequenceNumber</c>.</summary>
    internal const ulong UnknownSequence = 0UL;

    private readonly object _gate = new();
    private readonly Dictionary<string, ulong> _published = new(StringComparer.Ordinal);

    /// <summary>How many world names currently have a published <see cref="WorldReady"/> against them.</summary>
    internal int Count { get { lock (_gate) return _published.Count; } }

    /// <summary>
    /// The sequence number this ledger published for under <paramref name="name"/>, or false when
    /// it has published nothing for that name. <see cref="UnknownSequence"/> means it published for
    /// a world whose identity could not be read.
    /// </summary>
    internal bool TryGetPublished(string name, out ulong sequence)
    {
        lock (_gate) return _published.TryGetValue(name, out sequence);
    }

    /// <summary>
    /// A world exists under <paramref name="name"/> this frame, with identity
    /// <paramref name="sequence"/> (<see cref="UnknownSequence"/> when it could not be read).
    /// Returns true when this is a world we have not published a <see cref="WorldReady"/> for —
    /// the first sighting, or a different world under a name we have already seen, which is the
    /// destroy-and-recreate case. Records what it decides, so calling it twice for one world
    /// publishes once.
    /// </summary>
    internal bool NoteFound(string name, ulong sequence)
    {
        if (string.IsNullOrEmpty(name)) return false;
        lock (_gate)
        {
            if (!_published.TryGetValue(name, out ulong published))
            {
                // Never seen under this name: publish, whatever its identity reads as.
                _published[name] = sequence;
                return true;
            }

            if (published == UnknownSequence)
            {
                // We published for a world we could not identify. Learn the identity if it has
                // become readable — that is what makes the *next* swap visible — but do not
                // publish again for what is most likely the same world.
                if (sequence != UnknownSequence) _published[name] = sequence;
                return false;
            }

            // Cannot classify this frame. Keep the identity we know; the next frame decides.
            if (sequence == UnknownSequence) return false;

            if (sequence == published) return false;

            // Same name, different world: the game destroyed one and created another between two
            // of our samples. This is the event the name-keyed ledger swallowed.
            _published[name] = sequence;
            return true;
        }
    }

    /// <summary>
    /// No world exists under <paramref name="name"/> this frame. Forgetting it is what makes the
    /// next creation publish even if its sequence number cannot be read.
    /// </summary>
    internal void NoteGone(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_gate) _published.Remove(name);
    }

    /// <summary>Forget everything. <c>EventHooks.Uninstall</c>, and tests.</summary>
    internal void Clear()
    {
        lock (_gate) _published.Clear();
    }
}
