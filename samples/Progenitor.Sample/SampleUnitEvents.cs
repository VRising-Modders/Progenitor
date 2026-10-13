using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Progenitor.Events;
using Progenitor.Units;

namespace Progenitor.Sample;

/// <summary>
/// Job FEX3 (RD6 J9, and the units half of J5). The first subscribers in the repository to the
/// four unit events: <see cref="UnitArrived"/>, <see cref="UnitUnreachable"/>,
/// <see cref="UnitStepCompleted"/> and <see cref="UnitSequenceCompleted"/>. Before this,
/// <c>.rat</c> steered units and never listened to one — the events were published, documented
/// and consumed by nothing.
///
/// <b>Why a mod wants them.</b> <c>GoTo</c> and <c>Behaviour.Play</c> are fire-and-forget: the
/// call returns on the tick it was made and the walking happens over the next several seconds.
/// The only ways to know how it ended are to poll <c>handle.Position</c> every frame, or to
/// subscribe. These are the subscribe route, and they arrive on the normal
/// <see cref="EventBus"/> from the main-thread poll, so priority and <c>before</c>/<c>after</c>
/// ordering work exactly as they do for <c>ChatMessage</c> or <c>PlayerConnected</c>.
///
/// <b>The two rules a subscriber has to know</b>, both from the module's own doc comments and
/// both visible in the counters below:
/// <list type="bullet">
/// <item><description>A step that fails ends the sequence
/// (<c>IUnitBehaviour.cs:18-21</c>), so a <see cref="UnitStepCompleted"/> carrying
/// <see cref="UnitStepOutcome.Failed"/> is followed by a
/// <see cref="UnitSequenceCompleted"/> — except for a refused <c>Animate</c>, which is
/// <see cref="UnitStepOutcome.Skipped"/> and the sequence carries on.</description></item>
/// <item><description><b>A teardown that disposes the handle publishes nothing.</b> Despawn and
/// Core uninstall stop the sequencer silently (see <see cref="UnitSequenceCompleted"/>) because a
/// subscriber would otherwise be handed a disposed handle. <c>.rat off</c> is
/// <c>Units.RemoveAll</c> with the owner these rats were spawned with, so it despawns them and
/// takes that same silent route: the sequence counter stays where it was during a patrol, and
/// that is correct, not a dropped event. <b>What <c>Units.RemoveAll</c> does publish</b>
/// (FUNIT4, R57 finding 1) is a <c>Stopped</c> for a sequence it leaves <i>alive</i> — one owned
/// by the unloading assembly through a second object or carrying its plugin id — while anyone
/// else's sequence keeps playing with the unloading assembly's <c>Then</c> steps reported
/// <see cref="UnitStepOutcome.Skipped"/> and nothing published.</description></item>
/// </list>
///
/// One log line per event, so the server log alone proves each one fired, plus a ring of the last
/// <see cref="Capacity"/> lines that <c>.rat status</c> prints for someone who is in chat rather
/// than in the log.
///
/// <b>Live-check note.</b> A unit does not path at all with no connected player near it
/// (CLAUDE.md's <c>DisabledDueToNoPlayersInRange</c> rule), so the arrival half is checked with a
/// player standing next to the rat. The <em>step</em> and <em>sequence</em> events do not depend
/// on that: <c>.rat patrol</c>'s Face and Wait steps complete on a timer either way.
///
/// Reload-safe and game-free: the subscriptions are <c>IDisposable</c>s this file owns and
/// disposes in <see cref="Unsubscribe"/>, and they are registered with the plugin's owner so
/// Core's sweep would drop them anyway. Nothing here touches a game type, so the test project
/// drives the whole thing through the real sequencer and a fake runtime.
/// </summary>
internal static class SampleUnitEvents
{
    /// <summary>Log/grep area: <c>Sample rat-events: …</c>.</summary>
    internal const string Area = "rat-events";

    /// <summary>How many recent event lines <c>.rat status</c> keeps.</summary>
    internal const int Capacity = 8;

    private static readonly object Gate = new();
    private static readonly List<IDisposable> Subs = new(4);
    private static readonly List<string> Lines = new(Capacity);
    private static int _arrived;
    private static int _unreachable;
    private static int _steps;
    private static int _sequences;

    /// <summary>True between <see cref="Subscribe"/> and <see cref="Unsubscribe"/>.</summary>
    internal static bool IsSubscribed
    {
        get { lock (Gate) return Subs.Count > 0; }
    }

    /// <summary><see cref="UnitArrived"/> count since the last subscribe.</summary>
    internal static int ArrivedCount => Volatile.Read(ref _arrived);

    /// <summary><see cref="UnitUnreachable"/> count since the last subscribe.</summary>
    internal static int UnreachableCount => Volatile.Read(ref _unreachable);

    /// <summary><see cref="UnitStepCompleted"/> count since the last subscribe.</summary>
    internal static int StepCount => Volatile.Read(ref _steps);

    /// <summary><see cref="UnitSequenceCompleted"/> count since the last subscribe.</summary>
    internal static int SequenceCount => Volatile.Read(ref _sequences);

    /// <summary>The last <see cref="Capacity"/> event lines, oldest first.</summary>
    internal static IReadOnlyList<string> RecentLines
    {
        get { lock (Gate) return Lines.ToArray(); }
    }

    /// <summary>
    /// Subscribe all four. Idempotent: an existing subscription is dropped first, so a second
    /// call cannot double-count. <paramref name="owner"/> is the plugin's owner object, which is
    /// what <c>EventBus.RemoveAll</c> keys on during a sweep.
    /// </summary>
    internal static void Subscribe(object owner, string? pluginId)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        Unsubscribe();

        var subs = new List<IDisposable>(4)
        {
            EventBus.On<UnitArrived>(OnArrived, owner: owner, pluginId: pluginId),
            EventBus.On<UnitUnreachable>(OnUnreachable, owner: owner, pluginId: pluginId),
            EventBus.On<UnitStepCompleted>(OnStep, owner: owner, pluginId: pluginId),
            EventBus.On<UnitSequenceCompleted>(OnSequence, owner: owner, pluginId: pluginId),
        };

        lock (Gate) Subs.AddRange(subs);
        SampleLog.Info(Area, SubscribedLine());
    }

    /// <summary>The line written when the four subscriptions go in.</summary>
    internal static string SubscribedLine() =>
        "subscribed to UnitArrived, UnitUnreachable, UnitStepCompleted and UnitSequenceCompleted; "
        + "one line each, last " + N(Capacity) + " shown by .rat status";

    /// <summary>Drop the subscriptions and forget the counters. Unload calls this.</summary>
    internal static void Unsubscribe()
    {
        List<IDisposable> subs;
        lock (Gate)
        {
            subs = new List<IDisposable>(Subs);
            Subs.Clear();
            Lines.Clear();
        }

        for (int i = 0; i < subs.Count; i++)
        {
            try { subs[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogError("Sample rat-events dispose threw: " + e); }
        }

        Volatile.Write(ref _arrived, 0);
        Volatile.Write(ref _unreachable, 0);
        Volatile.Write(ref _steps, 0);
        Volatile.Write(ref _sequences, 0);
    }

    /// <summary>The <see cref="UnitArrived"/> line.</summary>
    internal static string ArrivedLine(UnitId entity, UnitVec3 position) =>
        "arrived entity=" + entity + " at=" + position;

    /// <summary>The <see cref="UnitUnreachable"/> line. The reason is Core's, verbatim.</summary>
    internal static string UnreachableLine(UnitId entity, UnitVec3 goal, string? reason) =>
        "unreachable entity=" + entity + " goal=" + goal + " reason=" + Reason(reason);

    /// <summary>
    /// The <see cref="UnitStepCompleted"/> line. Index is zero-based on the event and one-based
    /// here, because the reply reads as "step 3/6" to a person watching a rat.
    /// </summary>
    internal static string StepLine(UnitId entity, UnitStepKind step, int index, int count, UnitStepOutcome outcome, string? reason) =>
        "step " + N(index + 1) + "/" + N(count) + " " + step + " -> " + outcome
        + " entity=" + entity + Because(outcome == UnitStepOutcome.Completed, reason);

    /// <summary>The <see cref="UnitSequenceCompleted"/> line.</summary>
    internal static string SequenceLine(UnitId entity, UnitSequenceOutcome outcome, int stepsCompleted, int count, string? reason) =>
        "sequence " + outcome + " " + N(stepsCompleted) + "/" + N(count)
        + " entity=" + entity + Because(outcome == UnitSequenceOutcome.Completed, reason);

    /// <summary>The <c>.rat status</c> reply: the counters, then the ring.</summary>
    internal static IReadOnlyList<string> StatusLines()
    {
        var lines = new List<string>(Capacity + 2)
        {
            "unit events: arrived=" + N(ArrivedCount) + " unreachable=" + N(UnreachableCount)
                + " steps=" + N(StepCount) + " sequences=" + N(SequenceCount)
                + " subscribed=" + (IsSubscribed ? "True" : "False"),
        };

        var recent = RecentLines;
        if (recent.Count == 0)
        {
            lines.Add("Nothing yet. Try .rat patrol (Face and Wait complete on a timer) or "
                + ".rat go with a player standing next to the rat.");
            return lines;
        }

        for (int i = 0; i < recent.Count; i++)
            lines.Add("  " + recent[i]);
        return lines;
    }

    private static void OnArrived(UnitArrived ev)
    {
        try
        {
            Interlocked.Increment(ref _arrived);
            Record(ArrivedLine(ev.Handle.Entity, ev.Position));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample UnitArrived handler threw: " + e);
        }
    }

    private static void OnUnreachable(UnitUnreachable ev)
    {
        try
        {
            Interlocked.Increment(ref _unreachable);
            Record(UnreachableLine(ev.Handle.Entity, ev.Goal, ev.Reason));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample UnitUnreachable handler threw: " + e);
        }
    }

    private static void OnStep(UnitStepCompleted ev)
    {
        try
        {
            Interlocked.Increment(ref _steps);
            Record(StepLine(ev.Handle.Entity, ev.Step.Kind, ev.Index, ev.Count, ev.Outcome, ev.Reason));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample UnitStepCompleted handler threw: " + e);
        }
    }

    private static void OnSequence(UnitSequenceCompleted ev)
    {
        try
        {
            Interlocked.Increment(ref _sequences);
            Record(SequenceLine(ev.Handle.Entity, ev.Outcome, ev.StepsCompleted, ev.Count, ev.Reason));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample UnitSequenceCompleted handler threw: " + e);
        }
    }

    private static void Record(string line)
    {
        lock (Gate)
        {
            Lines.Add(line);
            while (Lines.Count > Capacity) Lines.RemoveAt(0);
        }

        SampleLog.Info(Area, line);
    }

    private static string Reason(string? reason) =>
        string.IsNullOrEmpty(reason) ? "<none given>" : reason!;

    private static string Because(bool ok, string? reason) =>
        ok || string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
