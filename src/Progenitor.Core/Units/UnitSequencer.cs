using System;
using System.Collections.Generic;
using System.Reflection;

namespace Progenitor.Units;

/// <summary>
/// The step state machine. Game-free on purpose: every decision this job had to make —
/// step ordering, what counts as completion, what a failed step does to the rest of the
/// sequence, what Stop means — is here and is covered by tests against a fake driver and a
/// fake clock, so a regression fails on a machine with no V Rising on it.
/// <para>
/// One step runs at a time, advanced from the same main-thread tick <see cref="Units.Poll"/>
/// already uses. It never touches a behavior-tree blob: the unit runs a vanilla tree and
/// the module drives the steps over it. <see cref="Play"/> forces
/// <c>GenericEnemyState.Idle</c> once before the first step so that tree neither wanders nor
/// fights while the module drives it (R55 finding 2 — six documents said this happened and
/// nothing did it).
/// </para>
/// <para>
/// <b>Re-entrancy is a deferral, not a guard (R55 finding 1).</b> A <see cref="Play"/> or
/// <see cref="Stop"/> issued while the sequencer is inside a tick — from a
/// <see cref="UnitStep.Then"/> callback, from a <see cref="UnitStepCompleted"/> or
/// <see cref="UnitSequenceCompleted"/> subscriber, or from inside a driver write — is queued
/// and applied once, in order, when that tick unwinds. Nothing is applied underneath a
/// running <see cref="Advance"/>, so <c>_index</c> can never point into a sequence that
/// replaced the one being walked. The measured symptoms of the old inline path were a
/// still-running <c>GoTo</c> published as <c>Completed</c> and a first step skipped with no
/// event at all.
/// </para>
/// <para>
/// <b>The deferral covers the handle's own raises too (R56 finding 1).</b> The bus publication
/// and the notify are one sequencer entry — <see cref="RaiseArrived"/> /
/// <see cref="RaiseUnreachable"/> raise the depth, publish, notify and leave — so a
/// <see cref="Play"/> from a <c>UnitArrived</c> subscriber is queued exactly like one from a
/// <see cref="UnitStep.Then"/>. Before that, the publish ran at depth 0 and the subscriber's
/// new sequence was live by the time the <i>old</i> arrival was notified, which completed the
/// new sequence's first <c>GoTo</c> on the spot. Belt and braces on the same defect: every
/// <c>GoTo</c> wait is stamped with a <see cref="UnitMoveTicket"/> (the epoch and step index it
/// was armed for) and a notification carrying any other ticket is discarded with a log line
/// rather than completing whatever happens to be waiting now.
/// </para>
/// </summary>
internal sealed class UnitSequencer : IUnitBehaviour
{
    private static readonly UnitStep[] NoSteps = Array.Empty<UnitStep>();

    private readonly IUnitStepDriver _driver;

    /// <summary>Monotonic seconds. Injected so <see cref="UnitStepKind.Wait"/> is testable.</summary>
    private readonly Func<double> _clock;

    private UnitStep[] _steps = NoSteps;
    private int _index = -1;
    private int _completed;
    private bool _playing;

    /// <summary>Waiting for Arrived / Unreachable on a <see cref="UnitStepKind.GoTo"/>.</summary>
    private bool _awaitingMove;

    /// <summary>Deadline for the waiting <see cref="UnitStepKind.GoTo"/>; 0 means no timeout.</summary>
    private double _moveDeadline;
    private float _moveTimeout;

    /// <summary>
    /// Which step armed the wait above. The deadline belongs to that step and to no other, so
    /// a notification stamped with anything else is somebody else's arrival (R56 finding 1) and
    /// a step that is discarded takes the deadline with it (R56 finding 3).
    /// </summary>
    private UnitMoveTicket _moveTicket = UnitMoveTicket.None;

    /// <summary>
    /// A notification for some other step was discarded while this wait was armed, so
    /// something restarted this unit's walk behind the sequence's back. The timeout and the
    /// no-deadline reminder name that cause instead of the AI-not-ticking one (R57 finding 2):
    /// after the ticket it is the likelier of the two on a server where a mod steers units,
    /// and the two lines look identical to an operator who only reads the end of the wait.
    /// </summary>
    private bool _moveDiscarded;

    /// <summary>Clock reading the current wait started at, for the no-deadline reminder.</summary>
    private double _moveWaitStart;

    /// <summary>
    /// Next clock reading at which a <c>timeoutSeconds: 0</c> wait says it is still waiting;
    /// 0 while there is nothing to remind about (R57 finding 2).
    /// </summary>
    private double _moveNextReminder;

    /// <summary>
    /// Where the unit was when the wait was armed, and then at each reminder. The reminder
    /// compares the two to decide whether it has any business naming a cause (R58 finding 8);
    /// <see cref="_moveHasLastPosition"/> is false whenever the driver could not say.
    /// </summary>
    private UnitVec3 _moveLastPosition;
    private bool _moveHasLastPosition;

    /// <summary>Waiting for a module timer (<see cref="UnitStepKind.Wait"/> / Animate).</summary>
    private bool _timing;
    private double _deadline;

    /// <summary>Outcome the timer will report; a refused Animate keeps its Skipped through the wait.</summary>
    private UnitStepOutcome _timedOutcome;
    private string _timedReason = "";

    /// <summary>
    /// True while a driver write is on the stack. A refused GoTo raises Unreachable
    /// synchronously from inside that write, so the notify is parked rather than re-entering
    /// the state machine underneath itself.
    /// </summary>
    private bool _inDriver;
    private bool _hasParked;
    private bool _parkedFailed;
    private string _parkedReason = "";

    /// <summary>
    /// Nesting depth of the sequencer's own entry points (Play / Stop / Tick / the two
    /// notifies). Anything a mod calls back into while this is above zero is deferred.
    /// </summary>
    private int _depth;

    /// <summary>
    /// Sequence identity. Bumped by every start and every end, so a walk that was interrupted
    /// by a teardown from inside a callback stops walking instead of reading the next
    /// sequence's array. <c>_playing</c> answers "is something running", never "is <i>this</i>
    /// one running".
    /// </summary>
    private int _epoch;

    private readonly Queue<Pending> _pending = new();

    /// <summary>
    /// A Play or Stop was queued after the running sequence started, so the sequence stops
    /// advancing and lets the queued action take over when the tick unwinds. Distinct from
    /// <c>_pending.Count</c>, which during a drain still holds actions queued <i>before</i> the
    /// one being applied — those must not stop the sequence they just started.
    /// </summary>
    private bool _interrupted;

    internal UnitSequencer(IUnitStepDriver driver, Func<double> clock)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool IsPlaying => _playing;

    public int StepIndex => _playing ? _index : -1;

    public int StepCount => _steps.Length;

    /// <summary>Queued Play / Stop waiting for the current tick to unwind. Tests read it.</summary>
    internal int PendingCount => _pending.Count;

    public void Play(params UnitStep[] steps)
    {
        Units.EnsureMainThread(nameof(Play));

        // Validate and copy first: a null step is the caller's mistake and must throw at the
        // call site whether or not this Play is deferred, and must not have replaced anything.
        var copy = Copy(steps);

        if (_depth > 0)
        {
            _pending.Enqueue(new Pending(copy, stop: false));
            _interrupted = true;
            _driver.Log("Units: sequence Play deferred to the end of this tick (called from a step callback or an event)");
            return;
        }

        Enter();
        try { PlayCore(copy); }
        finally { Leave(); }
    }

    /// <summary>
    /// Stop what is playing. Deferred like <see cref="Play"/> while the sequencer is inside a
    /// tick, and while it waits it <b>cancels</b> anything queued ahead of it (R56 finding 3).
    /// <para>
    /// <b>The coalescing rule.</b> A <c>Stop</c> queued after a <c>Play</c> in the same drain
    /// cancels that <c>Play</c> before it starts: no forced Idle, no first-step driver write,
    /// and <i>no</i> <see cref="UnitSequenceCompleted"/> for it — a sequence that never started
    /// never ended, and publishing <c>Stopped 0/N</c> for it would invent a run that did not
    /// happen. Only the sequence actually playing is reported (<c>Stopped</c>, once). A
    /// <c>Play</c> queued <i>after</i> a <c>Stop</c> is not touched and starts normally, so
    /// "stop this, then start that" still works. Order alone is the right rule for two
    /// <c>Play</c>s and the wrong one here: applying a <c>Play</c> is not free, and before this
    /// the queue turned <c>Play(); Stop();</c> into "walk there, then forget you are watching" —
    /// the unit kept its <c>AiMove_Server</c> goal with no deadline and no sequence behind it.
    /// </para>
    /// </summary>
    public void Stop()
    {
        Units.EnsureMainThread(nameof(Stop));

        if (_depth > 0)
        {
            int cancelled = CountCancellable();
            _pending.Clear();
            _pending.Enqueue(new Pending(null, stop: true));
            _interrupted = true;
            if (cancelled > 0)
            {
                _driver.Log("Units: sequence Stop canceled " + cancelled
                    + " deferred action(s) queued ahead of it; none of them was written to the unit");
            }

            _driver.Log("Units: sequence Stop deferred to the end of this tick (called from a step callback or an event)");
            return;
        }

        Enter();
        try { StopCore(); }
        finally { Leave(); }
    }

    /// <summary>
    /// Queued <see cref="Play"/>s a deferred <see cref="Stop"/> is about to cancel. A queued
    /// Stop is not counted: replacing one Stop with another cancels nothing a caller can see.
    /// </summary>
    private int CountCancellable()
    {
        int count = 0;
        foreach (var pending in _pending)
        {
            if (!pending.IsStop) count++;
        }

        return count;
    }

    /// <summary>
    /// Teardown stop: Despawn, plugin unload (<c>RemoveAll</c>) and Core uninstall. Clears
    /// the state machine and publishes nothing — the handle is going away, so handing a
    /// disposed unit to a subscriber would be worse than silence. Never deferred: the caller
    /// is on its way out, and the epoch bump is what makes a walk started underneath it stop.
    /// Anything queued is dropped, callbacks included (R55 finding 9).
    /// </summary>
    internal void StopSilently()
    {
        _pending.Clear();
        _interrupted = false;
        if (!_playing) return;
        End(UnitSequenceOutcome.Stopped, "teardown", publish: false);
    }

    /// <summary>
    /// Unload stop: the plugin that <b>owns</b> this unit is going away, so its sequence goes
    /// with it. Unlike <see cref="StopSilently"/> this is not a teardown of the handle — the
    /// unit and the handle stay — so the end is published (R57 finding 1): a
    /// <see cref="UnitSequenceCompleted"/> subscriber is how anybody else learns the walk
    /// stopped, and <see cref="End"/>'s <see cref="ClearMoveWait"/> logs the deadline going
    /// with it rather than leaving a written goal nobody is watching.
    /// <para>
    /// <b>The bystander's reaction runs, on this unit too (job FUNIT6, R59 finding 6).</b> The
    /// event exists so somebody else can park or restart the abandoned unit, and parking it is
    /// a <see cref="Play"/> aimed at exactly this sequencer. FUNIT4 dropped that one — the
    /// queue was cleared in the <c>finally</c> — which made the paragraph in
    /// <c>docs/modules.md</c> say the event is for doing the one thing the code refused. The
    /// deferral is all that was ever needed: a <c>Play</c> issued from inside the publication
    /// is queued because the stack is still in <see cref="End"/>, and <see cref="Leave"/>
    /// drains it once the unload stop has completely finished, against a stopped sequencer, at
    /// depth 0, exactly as a deferred <c>Play</c> from any other callback runs. Nothing about
    /// the unloading ALC needs protecting from it: the delegate belongs to the bystander, and
    /// <c>SweepRegistrations</c> has already run <c>EventBus.RemoveAll(plugin)</c>, so the
    /// unloading plugin has no subscribers left to be one.
    /// </para>
    /// <para>
    /// The handle stays live through all of this — only its sequence ended — so the drained
    /// <c>Play</c> writes to a unit that is still there. A teardown that really is disposing
    /// the handle uses <see cref="StopSilently"/>, publishes nothing, and so cannot be reacted
    /// to at all.
    /// </para>
    /// </summary>
    /// <returns>True when there was a sequence to end.</returns>
    internal bool StopForUnload(string reason, bool publish)
    {
        _pending.Clear();
        _interrupted = false;
        if (!_playing) return false;

        Enter();
        try { End(UnitSequenceOutcome.Stopped, reason, publish); }
        finally
        {
            _interrupted = false;
            // Leave() drains: whatever a subscriber queued at this unit is applied now that the
            // unload stop itself is over.
            Leave();
        }

        return true;
    }

    /// <summary>
    /// Take every <see cref="UnitStep.Then"/> written in <paramref name="assembly"/> out of
    /// this sequence — playing or queued — and leave the rest of it running. Returns how many
    /// were dropped.
    /// <para>
    /// The sweep exists to stop an unloading ALC being pinned by a delegate, and a delegate is
    /// all it has to remove (R57 finding 1). Dropping the whole sequence instead stopped a
    /// sequence its owner was still running — no event, its <c>AiMove_Server</c> goal still
    /// written and its deadline discarded — because one of its steps named a helper the
    /// unloading plugin published, which is the normal way one mod extends another. The step
    /// keeps its slot and completes as a no-op so the indices in
    /// <see cref="UnitStepCompleted"/> still line up with the array the owner passed to
    /// <see cref="Play"/>.
    /// </para>
    /// </summary>
    internal int DropCallbacksFrom(Assembly assembly)
    {
        if (assembly == null) return 0;
        string name = assembly.GetName().Name ?? assembly.FullName ?? "";
        int dropped = Neutralise(_steps, assembly, name);
        foreach (var pending in _pending)
            dropped += Neutralise(pending.Steps, assembly, name);
        return dropped;
    }

    /// <summary>
    /// True when a step this sequencer still holds — playing or queued — carries a callback
    /// written in <paramref name="assembly"/>. <see cref="UnitStep.Then"/> is the one place
    /// Units holds a delegate a reloadable plugin wrote, so an ownerless spawn can pin that
    /// plugin's collectible ALC through it (R55 finding 9).
    /// </summary>
    internal bool HoldsCallbackFrom(Assembly assembly)
    {
        if (assembly == null) return false;
        if (HoldsCallback(_steps, assembly)) return true;
        foreach (var pending in _pending)
        {
            if (HoldsCallback(pending.Steps, assembly)) return true;
        }

        return false;
    }

    /// <summary>Main-thread tick, from the same poll the steering checks run on.</summary>
    internal void Tick()
    {
        if (!_playing) return;

        Enter();
        try { TickCore(); }
        finally { Leave(); }
    }

    /// <summary>
    /// The handle reached its goal: publish <c>UnitArrived</c> and advance the step, both
    /// inside one sequencer entry so a <see cref="Play"/> or <see cref="Stop"/> from a
    /// subscriber is deferred to the end of it (R56 finding 1). <paramref name="publish"/> is
    /// the handle's bus call — the sequencer stays game-free and does not know what a bus is.
    /// </summary>
    internal void RaiseArrived(UnitMoveTicket ticket, Action? publish)
    {
        Enter();
        try
        {
            Publish(publish, "Arrived");
            NotifyArrived(ticket);
        }
        finally { Leave(); }
    }

    /// <summary>
    /// The handle gave up on the goal. Same one-entry shape as <see cref="RaiseArrived"/>: a
    /// subscriber that starts a new sequence must not have its first step failed by the
    /// arrival of the sequence it replaced.
    /// </summary>
    internal void RaiseUnreachable(UnitMoveTicket ticket, string reason, Action? publish)
    {
        Enter();
        try
        {
            Publish(publish, "Unreachable");
            NotifyUnreachable(ticket, reason);
        }
        finally { Leave(); }
    }

    private void Publish(Action? publish, string what)
    {
        if (publish == null) return;
        try { publish(); }
        catch (Exception e) { _driver.Log("Units: " + what + " publish threw: " + e.Message); }
    }

    /// <summary>The handle raised <see cref="UnitArrived"/>.</summary>
    internal void NotifyArrived(UnitMoveTicket ticket)
    {
        if (_inDriver)
        {
            Park(failed: false, reason: "");
            return;
        }

        if (!_playing || !_awaitingMove) return;
        if (!StillArmed(ticket, "arrival")) return;

        Enter();
        try
        {
            int epoch = _epoch;
            ClearMoveWait();
            CompleteStep(UnitStepOutcome.Completed, "");
            // Advance decides what the interrupt means; returning here instead left a
            // one-step sequence whose arrival was its last step reported Stopped
            // (R56 finding 2, through the notify gate rather than through Advance).
            if (!_playing || _epoch != epoch) return;
            Advance();
        }
        finally { Leave(); }
    }

    /// <summary>The handle raised <see cref="UnitUnreachable"/>.</summary>
    internal void NotifyUnreachable(UnitMoveTicket ticket, string reason)
    {
        if (_inDriver)
        {
            Park(failed: true, reason: reason);
            return;
        }

        if (!_playing || !_awaitingMove) return;
        if (!StillArmed(ticket, "unreachable")) return;

        Enter();
        try
        {
            int epoch = _epoch;
            ClearMoveWait();
            CompleteStep(UnitStepOutcome.Failed, reason);
            if (!_playing || _epoch != epoch) return;
            End(UnitSequenceOutcome.Failed, reason, publish: true);
        }
        finally { Leave(); }
    }

    /// <summary>
    /// True when <paramref name="ticket"/> is the wait this sequencer is actually holding.
    /// Anything else is a notification for a step that is no longer running — a subscriber
    /// replaced the sequence, or a mod called <c>GoTo</c> on the handle behind the sequencer's
    /// back — and completing the live step with it would credit a walk nobody took.
    /// </summary>
    private bool StillArmed(UnitMoveTicket ticket, string what)
    {
        if (ticket.Equals(_moveTicket)) return true;
        // Remember it: this wait can now only end on its own arrival, an unreachable or a
        // Stop, and when it ends on the deadline instead the operator needs the discard named
        // rather than the player-in-range gate thirty seconds further down the log
        // (R57 finding 2).
        _moveDiscarded = true;
        _driver.Log("Units: " + what + " for " + ticket + " discarded; the sequence is waiting on "
            + _moveTicket + " (the step it was armed for is no longer running)");
        return false;
    }

    /// <summary>
    /// Which of the two ways a GoTo wait goes nowhere. Before the move ticket there was one,
    /// and the line named it; the discard is now the likelier one wherever a mod steers units
    /// directly, so the wait has to say which it saw rather than printing the other one's
    /// diagnosis (R57 finding 2).
    /// </summary>
    private string MoveStallCause() => _moveDiscarded
        ? "an arrival or unreachable stamped for a different step was discarded while this one"
          + " waited, so the likeliest cause is that something moved this unit outside the"
          + " sequencer (a direct GoTo or Follow on the handle restarts the walk, and the"
          + " arrival it produces no longer belongs to this step)"
        : "the likeliest cause is that the unit's AI is not ticking — a unit does not path"
          + " with no connected player in range (evidence §22)";

    /// <summary>
    /// A unit that covered less than this between two reminders did not walk. The arrival band
    /// is 1 m, so half of it in a whole minute is not progress by any reading; anything more is,
    /// and gets no diagnosis at all.
    /// </summary>
    private const float ReminderProgressMetres = 0.5f;

    /// <summary>
    /// What, if anything, the wait-forever reminder is entitled to say about <i>why</i>
    /// (R58 finding 8). A reminder is a progress report; a cause is a diagnosis, and the
    /// sequencer only has evidence for one of them at a time:
    /// <list type="bullet">
    /// <item>A discarded notification is evidence, and it is the sequencer's own — so
    /// <see cref="MoveStallCause"/>'s discard branch is printed as soon as it happens.</item>
    /// <item>Otherwise the only evidence available is whether the unit moved since the last
    /// reminder. It did: nothing is wrong, so nothing is said. It did not: the no-players gate
    /// is worth naming, and now it is a finding rather than a guess.</item>
    /// <item>A driver that cannot report a position gets neither claim, and the reminder says
    /// so — for the real handle that means the runtime could not read the entity, which is a
    /// unit that has been killed or despawned (job FUNIT6, R59 finding 5). The old line printed
    /// "no connected player in range" at t=60 on every healthy long walk, which is the line an
    /// operator greps for after a real incident.</item>
    /// </list>
    /// </summary>
    private string ReminderDiagnosis()
    {
        if (_moveDiscarded) return "; " + MoveStallCause();

        if (!_driver.TryGetPosition(out var now))
        {
            _moveHasLastPosition = false;
            return "; its position could not be read, which is what a unit that has been killed"
                + " or despawned looks like from here, so the cause is unknown from here";
        }

        bool had = _moveHasLastPosition;
        var previous = _moveLastPosition;
        _moveLastPosition = now;
        _moveHasLastPosition = true;

        if (had && UnitVec3.Distance(previous, now) < ReminderProgressMetres)
        {
            return "; it has not moved since the last reminder, so " + MoveStallCause();
        }

        return "";
    }

    private void TickCore()
    {
        int epoch = _epoch;

        if (!_driver.Alive)
        {
            CompleteStep(UnitStepOutcome.Failed, "unit gone");
            if (!_playing || _epoch != epoch) return;
            End(UnitSequenceOutcome.Failed, "unit gone", publish: true);
            return;
        }

        // The GoTo deadline first: it is the only step that waits on something the game may
        // never produce (R55 finding 3).
        if (_awaitingMove && _moveDeadline > 0d && _clock() >= _moveDeadline)
        {
            float seconds = _moveTimeout;
            string cause = MoveStallCause();
            ClearMoveWait();
            string reason = "did not arrive within " + seconds.ToString("0.##") + "s";
            _driver.Log("Units: GoTo timed out after " + seconds.ToString("0.##") + "s; " + cause);
            CompleteStep(UnitStepOutcome.TimedOut, reason);
            if (!_playing || _epoch != epoch) return;
            End(UnitSequenceOutcome.Failed, reason, publish: true);
            return;
        }

        // A wait with no deadline has no other way of reaching a log (R57 finding 2). One line
        // a minute, saying what it knows and nothing more (R58 finding 8).
        if (_awaitingMove && _moveNextReminder > 0d && _clock() >= _moveNextReminder)
        {
            double waited = _clock() - _moveWaitStart;
            _moveNextReminder = _clock() + UnitStep.NoDeadlineReminderSeconds;
            _driver.Log("Units: GoTo " + _moveTicket + " has waited " + waited.ToString("0")
                + "s with timeoutSeconds: 0 (wait forever), so nothing but its own arrival, an"
                + " unreachable or a Stop will end it" + ReminderDiagnosis());
        }

        if (!_timing || _clock() < _deadline) return;

        _timing = false;
        CompleteStep(_timedOutcome, _timedReason);
        // Not "|| _interrupted": Advance is the one place that decides what an interrupt means,
        // and for a sequence with no steps left it means Completed, not Stopped (R56 finding 2).
        if (!_playing || _epoch != epoch) return;
        Advance();
    }

    private void PlayCore(UnitStep[]? copy)
    {
        _interrupted = false;
        // A second Play replaces the first: the old one is reported Stopped before anything of
        // the new one runs, so a subscriber never sees two sequences overlapping on one unit.
        if (_playing)
            End(UnitSequenceOutcome.Stopped, "replaced by a new sequence", publish: true);

        if (copy == null)
            return;

        if (!_driver.Alive)
        {
            _driver.Log("Units: sequence ignored; the unit is disabled or gone.");
            return;
        }

        _steps = copy;
        _index = -1;
        _completed = 0;
        _playing = true;
        _epoch++;
        int epoch = _epoch;
        _driver.Log("Units: sequence start steps=" + copy.Length);

        ForceIdle(copy);
        if (!_playing || _epoch != epoch) return;

        // Advance again: a Play or Stop queued from inside the forced Idle stops the walk
        // there, and a fresh sequence always has at least one step, so this reaches the
        // interrupt check and not the Completed branch.
        Advance();
    }

    private void StopCore()
    {
        _interrupted = false;
        if (!_playing) return;
        End(UnitSequenceOutcome.Stopped, "stopped", publish: true);
    }

    /// <summary>
    /// The invariant the whole design rests on, and until R55 finding 2 the one nothing
    /// implemented: post <c>ExternalBehaviourTreeTransitionRequest { State = Idle }</c> through
    /// the same driver seam <see cref="UnitStepKind.Idle"/> uses, once, before step 1.
    /// <para>
    /// <b>Once, and not again between steps.</b> The research says an Idle request on a unit
    /// that is off its leash does not park it — the tree goes to <c>Return</c> and walks home
    /// (<c>unit-movement.md</c> §20: <c>Idle</c> request → <c>BehaviourTreeState=Return (64)</c>,
    /// verified live; <c>IdleReturnDistance</c> / <c>SpawnPositionToBlackboard</c> are why).
    /// Re-posting Idle before every Face or Wait would therefore <i>cause</i> the very
    /// wandering it is meant to stop, on exactly the units that have already walked somewhere
    /// — which after a GoTo step is all of them. One post at the start, then the module's own
    /// writes own the unit; if a live run shows the tree taking it back, the next lever is
    /// <c>AiMove_Server.FreezeRotationWhenStationary</c> or pinning <c>SpawnTransform</c>, not
    /// a second Idle.
    /// </para>
    /// <para>
    /// A refused force does not fail the sequence: it is a safety measure the caller did not
    /// ask for, and the steps that follow are still worth running. It gets its own log line so
    /// a live run says which case it hit.
    /// </para>
    /// </summary>
    private void ForceIdle(UnitStep[] steps)
    {
        if (steps.Length > 0 && steps[0].Kind == UnitStepKind.Idle)
            return; // step 1 is the same post; two in a row would only be noise.

        var result = Call(() => _driver.Idle());
        if (result == UnitStepResult.Failed)
        {
            _driver.Log("Units: sequence could not force Idle (" + ParkedReasonOr("idle refused")
                + "); the vanilla tree may wander or fight between steps");
            return;
        }

        _driver.Log("Units: sequence forced Idle before step 1");
    }

    /// <summary>Start the next step, looping through everything that finishes immediately.</summary>
    private void Advance()
    {
        int epoch = _epoch;
        while (_playing && _epoch == epoch)
        {
            // "There are no steps left" is not "there are steps this sequence will not reach",
            // so the bounds check comes first: a sequence whose last step just completed ends
            // Completed regardless of what that step queued (R56 finding 2). End bumps the
            // epoch, so the queued action then starts cleanly against a stopped sequencer and
            // no "replaced by a new sequence" is published for a run that finished.
            int next = _index + 1;
            if (next >= _steps.Length)
            {
                End(UnitSequenceOutcome.Completed, "", publish: true);
                return;
            }

            // A Play or Stop queued by the step just finished takes over as soon as this tick
            // unwinds, so stop feeding the old sequence steps it was never going to reach.
            if (_interrupted) return;

            _index = next;
            var step = _steps[next];
            _driver.Log("Units: step " + (next + 1) + "/" + _steps.Length + " " + step);

            var result = Start(step, out string reason);
            if (_epoch != epoch) return;

            switch (result)
            {
                case UnitStepResult.Running:
                    return; // waiting on a notify (GoTo) or a timer.
                case UnitStepResult.Failed:
                    CompleteStep(UnitStepOutcome.Failed, reason);
                    if (!_playing || _epoch != epoch) return;
                    End(UnitSequenceOutcome.Failed, reason, publish: true);
                    return;
                case UnitStepResult.Skipped:
                    CompleteStep(UnitStepOutcome.Skipped, reason);
                    break;
                default:
                    CompleteStep(UnitStepOutcome.Completed, "");
                    break;
            }
        }
    }

    /// <summary>Run one step's write. Arms the timer for the timed kinds.</summary>
    private UnitStepResult Start(UnitStep step, out string reason)
    {
        reason = "";
        ClearMoveWait();
        _timing = false;

        switch (step.Kind)
        {
            case UnitStepKind.Wait:
                return ArmTimer(step.Seconds, UnitStepOutcome.Completed, "", out reason);

            case UnitStepKind.Invoke:
                // A Then the sweep neutralized did not run, and a subscriber that confirms its
                // own side effect from UnitStepCompleted has to be able to tell (R58 finding 6).
                // Skipped is the outcome that already means "the sequence carries on and this
                // step did not do its job", the same one a refused Animate reports.
                if (step.CallbackDropped)
                {
                    reason = step.CallbackDroppedReason;
                    return UnitStepResult.Skipped;
                }

                try { step.Callback?.Invoke(); }
                catch (Exception e)
                {
                    reason = "callback threw: " + e.Message;
                    return UnitStepResult.Failed;
                }

                return UnitStepResult.Done;

            case UnitStepKind.Animate:
            {
                var wrote = Call(() => _driver.Animate(step.Animation, step.Seconds));
                bool refused = wrote is UnitStepResult.Failed or UnitStepResult.Skipped;
                // Cosmetic, so a refusal must not strand a patrol: hold the step for its full
                // duration and report Skipped, never Failed.
                return ArmTimer(
                    step.Seconds,
                    refused ? UnitStepOutcome.Skipped : UnitStepOutcome.Completed,
                    refused ? ParkedReasonOr("animation refused") : "",
                    out reason);
            }

            case UnitStepKind.GoTo:
            {
                var started = Call(() => _driver.GoTo(step.Position));
                if (started == UnitStepResult.Running)
                {
                    _awaitingMove = true;
                    _moveTimeout = step.TimeoutSeconds;
                    _moveDeadline = step.TimeoutSeconds > 0f ? _clock() + step.TimeoutSeconds : 0d;
                    // Stamp the wait and hand the stamp to the driver, so the arrival that
                    // comes back can be matched against the step that asked for it.
                    _moveTicket = new UnitMoveTicket(_epoch, _index);
                    _moveDiscarded = false;
                    _moveWaitStart = _clock();
                    _moveNextReminder = step.TimeoutSeconds > 0f
                        ? 0d
                        : _moveWaitStart + UnitStep.NoDeadlineReminderSeconds;
                    // The first reminder's comparison point: where the unit stood when it was
                    // told to walk (R58 finding 8).
                    _moveHasLastPosition = _driver.TryGetPosition(out _moveLastPosition);
                    _driver.MoveArmed(_moveTicket);
                    return UnitStepResult.Running;
                }

                if (started == UnitStepResult.Failed) reason = ParkedReasonOr("goto refused");
                return started;
            }

            case UnitStepKind.Follow:
                // A standing state, not a destination: done once the write sticks. The 3 m
                // Arrived the handle raises later is a plain event, not a step boundary. No
                // timeout for the same reason: there is nothing to arrive at.
                return Immediate(Call(() => _driver.Follow(step.Target)), "follow refused", out reason);

            case UnitStepKind.FaceEntity:
                return Immediate(Call(() => _driver.Face(step.Target)), "face refused", out reason);

            case UnitStepKind.FacePosition:
                return Immediate(Call(() => _driver.Face(step.Position)), "face refused", out reason);

            default:
                return Immediate(Call(() => _driver.Idle()), "idle refused", out reason);
        }
    }

    /// <summary>A step that has no waiting state: Running from the driver means Done.</summary>
    private UnitStepResult Immediate(UnitStepResult result, string fallbackReason, out string reason)
    {
        reason = result == UnitStepResult.Failed ? ParkedReasonOr(fallbackReason) : "";
        return result == UnitStepResult.Running ? UnitStepResult.Done : result;
    }

    /// <summary>
    /// Run a driver write with the re-entrancy guard up, then fold in anything the write
    /// raised synchronously (a refused GoTo publishes Unreachable from inside the call).
    /// </summary>
    private UnitStepResult Call(Func<UnitStepResult> write)
    {
        _hasParked = false;
        _parkedFailed = false;
        _parkedReason = "";
        UnitStepResult result;
        _inDriver = true;
        try
        {
            result = write();
        }
        catch (Exception e)
        {
            _parkedReason = "step threw: " + e.Message;
            _hasParked = true;
            _parkedFailed = true;
            return UnitStepResult.Failed;
        }
        finally
        {
            _inDriver = false;
        }

        if (_hasParked)
            return _parkedFailed ? UnitStepResult.Failed : UnitStepResult.Done;
        return result;
    }

    private string ParkedReasonOr(string fallback) =>
        string.IsNullOrEmpty(_parkedReason) ? fallback : _parkedReason;

    private void Park(bool failed, string reason)
    {
        _hasParked = true;
        _parkedFailed = failed;
        _parkedReason = reason ?? "";
    }

    private UnitStepResult ArmTimer(float seconds, UnitStepOutcome outcome, string timerReason, out string reason)
    {
        reason = timerReason;
        if (seconds <= 0f)
            return outcome == UnitStepOutcome.Skipped ? UnitStepResult.Skipped : UnitStepResult.Done;

        _timing = true;
        _deadline = _clock() + seconds;
        _timedOutcome = outcome;
        _timedReason = timerReason;
        return UnitStepResult.Running;
    }

    /// <summary>
    /// Disarm the GoTo wait. <paramref name="why"/> is non-empty where the wait is being
    /// <b>discarded</b> rather than satisfied — the step that armed the deadline is going away
    /// and the deadline goes with it, which is a thing a live log has to be able to show
    /// (R56 finding 3). The unit's own <c>AiMove_Server</c> goal is not cleared here: undoing a
    /// walk is a driver write with live consequences (an Idle request on a unit off its leash
    /// walks it home, <c>unit-movement.md</c> §20), so a caller who wants the unit parked asks
    /// for it with an <see cref="UnitStepKind.Idle"/> step.
    /// </summary>
    private void ClearMoveWait(string why = "")
    {
        if (_awaitingMove && why.Length > 0)
        {
            _driver.Log("Units: GoTo wait " + _moveTicket + " disarmed (" + why
                + "); its deadline is gone with it and the unit keeps whatever goal was written");
        }

        _awaitingMove = false;
        _moveDeadline = 0d;
        _moveTimeout = 0f;
        _moveTicket = UnitMoveTicket.None;
        _moveDiscarded = false;
        _moveWaitStart = 0d;
        _moveNextReminder = 0d;
        _moveLastPosition = default;
        _moveHasLastPosition = false;
    }

    private void CompleteStep(UnitStepOutcome outcome, string reason)
    {
        if (_index < 0 || _index >= _steps.Length) return;
        var step = _steps[_index];
        // Failed and TimedOut both end the sequence, so neither counts as a step that ran.
        if (outcome is UnitStepOutcome.Completed or UnitStepOutcome.Skipped) _completed++;
        _driver.StepCompleted(step, _index, _steps.Length, outcome, reason ?? "");
    }

    private void End(UnitSequenceOutcome outcome, string reason, bool publish)
    {
        int completed = _completed;
        int count = _steps.Length;
        _playing = false;
        _timing = false;
        _index = -1;
        _completed = 0;
        _steps = NoSteps;
        _epoch++;
        ClearMoveWait("sequence " + outcome);
        _driver.Log("Units: sequence end " + outcome
            + " " + completed + "/" + count
            + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"));
        if (publish)
            _driver.SequenceCompleted(outcome, completed, count, reason ?? "");
    }

    private void Enter() => _depth++;

    private void Leave()
    {
        _depth--;
        if (_depth == 0) Drain();
    }

    /// <summary>
    /// Apply what a callback asked for, once each and in the order it was asked, now that the
    /// stack is back at the frame. Anything queued from inside a queued action lands at the
    /// back of the same queue rather than recursing.
    /// </summary>
    private void Drain()
    {
        while (_pending.Count > 0)
        {
            var next = _pending.Dequeue();
            _depth++;
            try
            {
                if (next.IsStop) StopCore();
                else PlayCore(next.Steps);
            }
            catch (Exception e)
            {
                _driver.Log("Units: deferred sequence action threw: " + e.Message);
            }
            finally
            {
                _depth--;
            }
        }
    }

    private static UnitStep[]? Copy(UnitStep[]? steps)
    {
        if (steps == null || steps.Length == 0) return null;
        var copy = new UnitStep[steps.Length];
        for (int i = 0; i < steps.Length; i++)
            copy[i] = steps[i] ?? throw new ArgumentException("A sequence cannot contain a null step.", nameof(steps));
        return copy;
    }

    private static bool HoldsCallback(UnitStep[]? steps, Assembly assembly)
    {
        if (steps == null) return false;
        for (int i = 0; i < steps.Length; i++)
        {
            if (WrittenIn(steps[i]?.Callback, assembly)) return true;
        }

        return false;
    }

    /// <summary>
    /// The method's declaring assembly <b>or</b> the target's: a lambda closing over a type in
    /// the unloading ALC pins it either way.
    /// </summary>
    private static bool WrittenIn(Action? callback, Assembly assembly)
    {
        if (callback == null) return false;
        if (ReferenceEquals(callback.Method?.DeclaringType?.Assembly, assembly)) return true;
        return ReferenceEquals(callback.Target?.GetType().Assembly, assembly);
    }

    /// <summary>Replace each matching step in place with the same step minus its callback.</summary>
    private static int Neutralise(UnitStep[]? steps, Assembly assembly, string assemblyName)
    {
        if (steps == null) return 0;
        int dropped = 0;
        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            if (step == null || !WrittenIn(step.Callback, assembly)) continue;
            steps[i] = step.WithoutCallback(assemblyName);
            dropped++;
        }

        return dropped;
    }

    /// <summary>A Play (with its already-copied steps) or a Stop waiting for the tick to unwind.</summary>
    private readonly struct Pending
    {
        internal Pending(UnitStep[]? steps, bool stop)
        {
            Steps = steps;
            IsStop = stop;
        }

        internal UnitStep[]? Steps { get; }

        internal bool IsStop { get; }
    }
}

/// <summary>
/// Which step armed a <see cref="UnitStepKind.GoTo"/> wait: the sequence epoch and the step
/// index inside it. The handle carries it from the write to the arrival, and the sequencer
/// refuses a notification that does not match the wait it is holding. Without it,
/// <c>_awaitingMove</c> alone answered "is somebody waiting", never "is <i>this</i> arrival
/// the one being waited for" — the same conflation the epoch field fixed for sequences
/// (R56 finding 1).
/// </summary>
internal readonly struct UnitMoveTicket : IEquatable<UnitMoveTicket>
{
    /// <summary>No wait. A handle holds this for a move nobody sequenced.</summary>
    internal static readonly UnitMoveTicket None = new(0, -1);

    internal UnitMoveTicket(int epoch, int step)
    {
        Epoch = epoch;
        Step = step;
    }

    internal int Epoch { get; }

    internal int Step { get; }

    internal bool IsNone => Step < 0;

    public bool Equals(UnitMoveTicket other) => Epoch == other.Epoch && Step == other.Step;

    public override bool Equals(object? obj) => obj is UnitMoveTicket other && Equals(other);

    public override int GetHashCode() => (Epoch * 397) ^ Step;

    public override string ToString() => IsNone ? "no step" : "step " + (Step + 1) + " of sequence " + Epoch;
}
