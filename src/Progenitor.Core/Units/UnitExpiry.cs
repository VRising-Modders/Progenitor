using System;
using System.Collections.Generic;

namespace Progenitor.Units;

/// <summary>
/// What one unit looks like to the expiry sweep: plain facts read off the entity, nothing kept in
/// Core. The stamp is the game's own <c>LifeTime</c> and <c>Age</c> components, which carry no
/// persistence exclusion in the server metadata, so a save, a Core swap and a restart all keep it.
/// </summary>
internal readonly struct UnitExpiryFacts
{
    public UnitExpiryFacts(
        bool exists,
        bool beingDestroyed,
        bool apiSpawned,
        bool hasLifeTime,
        float duration,
        int endAction,
        bool hasAge,
        float age)
    {
        Exists = exists;
        BeingDestroyed = beingDestroyed;
        ApiSpawned = apiSpawned;
        HasLifeTime = hasLifeTime;
        Duration = duration;
        EndAction = endAction;
        HasAge = hasAge;
        Age = age;
    }

    public bool Exists { get; }
    public bool BeingDestroyed { get; }

    /// <summary>Wears Core's API-spawn marker, or is a live handle of this Core generation.</summary>
    public bool ApiSpawned { get; }

    public bool HasLifeTime { get; }

    /// <summary><c>LifeTime.Duration</c>: the unit's <c>Age</c> at which it ends.</summary>
    public float Duration { get; }

    /// <summary><c>LifeTime.EndAction</c> as its integer value (None 0, Kill 1, Destroy 2, RemoveBuffStack 3).</summary>
    public int EndAction { get; }

    public bool HasAge { get; }

    /// <summary><c>Age.Value</c>: seconds of server running time since the unit spawned (or since Core started its clock).</summary>
    public float Age { get; }
}

/// <summary>What the expiry sweep does with one unit.</summary>
internal enum UnitExpiryDecision
{
    /// <summary>Gone, or already being destroyed: nothing to do.</summary>
    Gone,

    /// <summary>Not spawned through the API: never touched.</summary>
    NotApiSpawned,

    /// <summary>No <c>LifeTime</c>, or one that does not destroy (Permanent, Kill, buff stacks).</summary>
    NoLimit,

    /// <summary>Limited and not yet due.</summary>
    Running,

    /// <summary>Limited and past its end by more than <see cref="UnitExpiryPolicy.GraceSeconds"/>: despawn it now.</summary>
    Expired,

    /// <summary>
    /// Limited, but the unit has no <c>Age</c>, so the game's <c>LifeTimeSystem</c> (query
    /// <c>WithAllRW&lt;Age&gt;</c>) never sees it. Core adds <c>Age</c> at 0: the unit then ends
    /// <c>Duration</c> seconds of server time later. Units spawned before this fix are this case.
    /// </summary>
    NoClock,
}

/// <summary>
/// Game-free half of the unit lifetime contract (docs/modules.md, "Lifetime across reload and
/// restart"). The game's <c>LifeTimeSystem.Handle</c> ends an entity
/// when <c>EndAction != None</c> and <c>Duration &lt;= Age.Value</c>; Core's sweep is the backstop
/// for whatever that misses, and despawns through the ordinary Units path a little later.
/// </summary>
internal static class UnitExpiryPolicy
{
    /// <summary><c>LifeTimeEndAction.Destroy</c>, the end action <c>LifetimeSeconds</c> writes.</summary>
    internal const int EndActionDestroy = Layout.LifeTimeEndActionDestroy;

    /// <summary>
    /// How long past its end the sweep waits for the game to remove the unit itself, so the two do
    /// not race on the same frame.
    /// </summary>
    internal const float GraceSeconds = 2f;

    /// <summary>Seconds between periodic sweeps while a server world exists.</summary>
    internal const double SweepIntervalSeconds = 5.0;

    /// <summary>
    /// The <c>LifeTime.Duration</c> to write so the unit ends <paramref name="seconds"/> from now:
    /// the game compares <c>Duration</c> with the unit's <c>Age</c>, not with the time of the write.
    /// A negative or non-finite age reads as 0.
    /// </summary>
    internal static float StampDuration(float currentAge, float seconds)
    {
        float age = float.IsNaN(currentAge) || float.IsInfinity(currentAge) || currentAge < 0f ? 0f : currentAge;
        return age + seconds;
    }

    /// <summary>True when the unit carries a lifetime that destroys it (the shape <c>LifetimeSeconds</c> writes).</summary>
    internal static bool IsLimited(in UnitExpiryFacts facts) =>
        facts.HasLifeTime
        && facts.EndAction == EndActionDestroy
        && facts.Duration >= 0f
        && !float.IsNaN(facts.Duration)
        && !float.IsInfinity(facts.Duration);

    internal static UnitExpiryDecision Decide(in UnitExpiryFacts facts)
    {
        if (!facts.Exists || facts.BeingDestroyed) return UnitExpiryDecision.Gone;
        if (!facts.ApiSpawned) return UnitExpiryDecision.NotApiSpawned;
        if (!IsLimited(facts)) return UnitExpiryDecision.NoLimit;
        if (!facts.HasAge) return UnitExpiryDecision.NoClock;
        return facts.Age >= facts.Duration + GraceSeconds ? UnitExpiryDecision.Expired : UnitExpiryDecision.Running;
    }

    /// <summary>Seconds of server time left before the game ends the unit, or null when it has no limit or no clock.</summary>
    internal static float? Remaining(in UnitExpiryFacts facts)
    {
        if (!IsLimited(facts) || !facts.HasAge) return null;
        return Math.Max(0f, facts.Duration - facts.Age);
    }
}

/// <summary>The world the sweep reads and acts on; the live half is <c>UnityUnitExpiry</c>, tests use a fake.</summary>
internal interface IUnitExpiryWorld
{
    /// <summary>Units that carry a <c>LifeTime</c>, disabled ones included. Ids are valid for this pass only.</summary>
    IReadOnlyList<UnitId> Candidates();

    UnitExpiryFacts Read(UnitId unit);

    /// <summary>Add <c>Age</c> at 0 so the game's <c>LifeTimeSystem</c> sees the unit. False when refused.</summary>
    bool StartClock(UnitId unit);

    /// <summary>Despawn through the ordinary Units path. False when refused.</summary>
    bool Despawn(UnitId unit);
}

/// <summary>Counts from one sweep pass.</summary>
internal readonly struct UnitExpirySweepResult
{
    public UnitExpirySweepResult(int scanned, int limited, int running, int despawned, int refused, int clocksStarted)
    {
        Scanned = scanned;
        Limited = limited;
        Running = running;
        Despawned = despawned;
        Refused = refused;
        ClocksStarted = clocksStarted;
    }

    public int Scanned { get; }
    public int Limited { get; }
    public int Running { get; }
    public int Despawned { get; }
    public int Refused { get; }
    public int ClocksStarted { get; }

    /// <summary>True when the pass changed something or failed to, i.e. worth a log line.</summary>
    public bool Acted => Despawned > 0 || Refused > 0 || ClocksStarted > 0;
}

/// <summary>
/// One pass over every candidate: despawn the API-spawned units that are past their end, and start
/// the clock on those that have none. Holds no state between passes, so a new Core generation, a
/// world load or a restart needs nothing handed over: the stamp is on the entity.
/// </summary>
internal static class UnitExpirySweep
{
    internal static UnitExpirySweepResult Run(IUnitExpiryWorld world)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        var candidates = world.Candidates();
        int scanned = 0, limited = 0, running = 0, despawned = 0, refused = 0, clocks = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var unit = candidates[i];
            UnitExpiryFacts facts;
            try { facts = world.Read(unit); }
            catch { continue; }
            scanned++;
            switch (UnitExpiryPolicy.Decide(facts))
            {
                case UnitExpiryDecision.Running:
                    limited++;
                    running++;
                    break;
                case UnitExpiryDecision.Expired:
                    limited++;
                    bool gone;
                    try { gone = world.Despawn(unit); }
                    catch { gone = false; }
                    if (gone) despawned++;
                    else refused++;
                    break;
                case UnitExpiryDecision.NoClock:
                    limited++;
                    bool started;
                    try { started = world.StartClock(unit); }
                    catch { started = false; }
                    if (started) clocks++;
                    else refused++;
                    break;
            }
        }

        return new UnitExpirySweepResult(scanned, limited, running, despawned, refused, clocks);
    }

    internal static string Format(UnitExpirySweepResult result, string why) =>
        "Units: lifetime sweep (" + why + "): " + result.Despawned + " expired unit(s) despawned, "
        + result.ClocksStarted + " lifetime clock(s) started on unit(s) without Age, "
        + result.Refused + " refused, " + result.Running + " still running.";
}
