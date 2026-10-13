using System;

namespace Progenitor.Buffs;

/// <summary>
/// Every decision the buff lifecycle makes, with no game and no mutable state: the validation
/// ladder, the conflict rule, the confirm budget and the poll verdict. The live module
/// (<see cref="Buffs"/>) and the handle (<see cref="BuffApplication"/>) call into here, so the
/// tests drive the production decisions instead of a copy of them.
/// </summary>
public static class BuffApplicationPolicy
{
    /// <summary>
    /// Frames to keep looking for the instance <c>ApplyBuff</c> was asked to create before calling
    /// the apply <see cref="BuffApplicationState.NotConfirmed"/>.
    ///
    /// <para>Eight, the same budget and for the same measured reason as
    /// <c>PrefabLiveRefresh.RestoreDurationFrames</c>: <c>DebugEventsSystem.ApplyBuff</c> only
    /// queues an <c>ApplyBuffDebugEvent</c>, and the buff entity is built by the game's buff system
    /// on a later update, so a same-call look always misses. Sharing the number is deliberate —
    /// both are measuring the same window, and a build where one moves has moved the other.</para>
    /// </summary>
    public const int ConfirmFrames = 8;

    /// <summary>
    /// Largest duration this API will write. The game's own <c>[SyncRangedFloat(0, 10000, …)]</c> on
    /// <c>LifeTime.Duration</c> (server metadata) is the declared range of the networked
    /// field; asking for more is a caller mistake, not something to round silently.
    /// </summary>
    public const float MaxDurationSeconds = 10000f;

    /// <summary>
    /// The pre-apply ladder, in the order the answers stop being useful: side, world, the request
    /// itself, the buff, the target. Returns <see cref="BuffRefusal.None"/> when the apply may be
    /// attempted.
    ///
    /// <para>The order is the contract, not an implementation detail. "You are on the client" is a
    /// better answer than "that target does not exist", because on the client it never will.</para>
    /// </summary>
    internal static BuffRefusal Validate(
        BuffRequest? request,
        bool installed,
        bool isServer,
        bool hasWorld,
        BuffResolve resolve,
        BuffTargetCheck target)
    {
        if (request == null) return BuffRefusal.BuffNotRegistered;
        if (!installed) return BuffRefusal.ModuleUnavailable;
        if (!isServer) return BuffRefusal.NotServer;
        if (!hasWorld) return BuffRefusal.NoWorld;
        if (string.IsNullOrWhiteSpace(request.BuffId)) return BuffRefusal.BuffNotRegistered;
        if (!IsDurationUsable(request)) return BuffRefusal.BadDuration;

        switch (resolve)
        {
            case BuffResolve.NotRegistered: return BuffRefusal.BuffNotRegistered;
            case BuffResolve.NotApplied: return BuffRefusal.BuffNotApplied;
        }

        switch (target)
        {
            case BuffTargetCheck.NoWorld: return BuffRefusal.NoWorld;
            case BuffTargetCheck.Missing: return BuffRefusal.TargetMissing;
            case BuffTargetCheck.NotBuffable: return BuffRefusal.TargetNotBuffable;
        }

        return BuffRefusal.None;
    }

    /// <summary>
    /// A duration is usable when the caller kept the prefab's own (any non-positive value is that
    /// sentinel) or asked for a finite, positive, in-range number of seconds. NaN and the
    /// infinities are refused rather than written: a NaN <c>Duration</c> never compares true
    /// against elapsed time, so the buff would simply never end.
    /// </summary>
    public static bool IsDurationUsable(BuffRequest request)
    {
        if (request == null) return false;
        float seconds = request.DurationSeconds;
        if (float.IsNaN(seconds)) return false;
        if (float.IsInfinity(seconds)) return false;
        if (request.KeepsPrefabDuration) return true;
        return seconds > 0f && seconds <= MaxDurationSeconds;
    }

    /// <summary>What to do about an existing live application of the same buff on the same target.</summary>
    internal enum Conflict
    {
        /// <summary>No live application: apply normally.</summary>
        None = 0,

        /// <summary>Same owner, <see cref="BuffRefreshPolicy.Refresh"/>: extend the live one and return it.</summary>
        Refresh = 1,

        /// <summary>Same owner, <see cref="BuffRefreshPolicy.Reject"/>: refuse and write nothing.</summary>
        Reject = 2,

        /// <summary>
        /// A different owner holds it. Always refused, whatever the policy: one buff instance has
        /// one owner, and a refresh by a stranger would silently retime somebody else's buff.
        /// </summary>
        Foreign = 3,

        /// <summary>
        /// The application holding the slot has been canceled and its queued apply is still in
        /// flight. Refused whoever asks and whatever the policy: the canceled application is the
        /// one that will destroy a late instance, and refreshing it (or applying beside it) would
        /// race that. It clears itself within <see cref="ConfirmFrames"/>.
        /// </summary>
        Cancelling = 4,
    }

    /// <summary>
    /// The conflict rule. <paramref name="sameOwner"/> is the module's own answer to "is the live
    /// application's owner the one asking", settled by reference (and by plugin id) the same way
    /// the unit sweep settles ownership. <paramref name="canceling"/> is checked first because a
    /// canceled request is nobody's to extend — not even its own owner's.
    /// </summary>
    internal static Conflict DecideConflict(
        bool hasLive, bool sameOwner, bool canceling, BuffRefreshPolicy policy)
    {
        if (!hasLive) return Conflict.None;
        if (canceling) return Conflict.Cancelling;
        if (!sameOwner) return Conflict.Foreign;
        return policy == BuffRefreshPolicy.Refresh ? Conflict.Refresh : Conflict.Reject;
    }

    /// <summary>The refusal a conflict produces, or <see cref="BuffRefusal.None"/> when it is not one.</summary>
    internal static BuffRefusal RefusalFor(Conflict conflict) => conflict switch
    {
        Conflict.Reject => BuffRefusal.AlreadyActive,
        Conflict.Foreign => BuffRefusal.AlreadyOwned,
        Conflict.Cancelling => BuffRefusal.CancelInFlight,
        _ => BuffRefusal.None,
    };

    /// <summary>What one poll of a live application should do.</summary>
    internal enum PollStep
    {
        /// <summary>Terminal already, or nothing to look at. Stop polling.</summary>
        Done = 0,

        /// <summary>Still waiting for the queued apply to produce an instance.</summary>
        WaitForInstance = 1,

        /// <summary>The instance appeared: record it, write the duration, become Active.</summary>
        Confirm = 2,

        /// <summary>The confirm budget is spent with no instance: NotConfirmed.</summary>
        GiveUp = 3,

        /// <summary>Confirmed active and still there: nothing to do.</summary>
        StillActive = 4,

        /// <summary>Confirmed active and the instance is gone: Ended.</summary>
        Ended = 5,

        /// <summary>The target entity is gone: TargetLost, and nothing is owed.</summary>
        TargetLost = 6,

        /// <summary>The world was replaced under this application: TargetLost, and no write.</summary>
        WorldChanged = 7,

        /// <summary>
        /// A cancel arrived while still Requested and the instance has now appeared: destroy it.
        /// This is the whole reason a canceled request keeps polling — dropping it here is what
        /// leaves an orphan buff on a player.
        /// </summary>
        CancelLateInstance = 8,

        /// <summary>A cancel arrived while Requested and the budget is spent: nothing was created.</summary>
        CancelGiveUp = 9,
    }

    /// <summary>
    /// One poll, decided from facts the caller already read this frame. Pure: the same inputs give
    /// the same step, and nothing here touches the world.
    /// </summary>
    /// <param name="state">Where the application is now.</param>
    /// <param name="cancelRequested">A Remove / Dispose arrived before the apply was confirmed.</param>
    /// <param name="epochChanged">The world-load epoch moved since the apply was stamped.</param>
    /// <param name="targetExists">The target entity is still allocated.</param>
    /// <param name="instanceFound">An instance of this buff is in the target's BuffBuffer now.</param>
    /// <param name="framesWaited">Polls already spent waiting for the instance.</param>
    internal static PollStep Poll(
        BuffApplicationState state,
        bool cancelRequested,
        bool epochChanged,
        bool targetExists,
        bool instanceFound,
        int framesWaited)
    {
        if (state != BuffApplicationState.Requested && state != BuffApplicationState.Active)
            return PollStep.Done;

        // The epoch check comes before every other reading. After a world reload the stamped
        // target index names a different entity, so "the target exists" and "the instance is
        // there" are both answers about somebody else.
        if (epochChanged) return PollStep.WorldChanged;

        if (state == BuffApplicationState.Requested)
        {
            if (cancelRequested)
            {
                if (instanceFound) return PollStep.CancelLateInstance;
                return framesWaited >= ConfirmFrames ? PollStep.CancelGiveUp : PollStep.WaitForInstance;
            }

            if (!targetExists) return PollStep.TargetLost;
            if (instanceFound) return PollStep.Confirm;
            return framesWaited >= ConfirmFrames ? PollStep.GiveUp : PollStep.WaitForInstance;
        }

        // Active.
        if (!targetExists) return PollStep.TargetLost;
        return instanceFound ? PollStep.StillActive : PollStep.Ended;
    }

    /// <summary>
    /// Is a removal this application's to make? Only a confirmed instance it recorded, on a target
    /// that still exists, in the epoch it was applied in. Everything else is a no-op, which is what
    /// makes repeated removal safe.
    /// </summary>
    internal static bool MayDestroy(
        BuffApplicationState state, bool epochChanged, bool hasInstance, bool instanceExists) =>
        state == BuffApplicationState.Active && !epochChanged && hasInstance && instanceExists;

    /// <summary>True for a state that can never change again.</summary>
    public static bool IsTerminal(BuffApplicationState state) =>
        state is BuffApplicationState.Refused
            or BuffApplicationState.Ended
            or BuffApplicationState.Removed
            or BuffApplicationState.TargetLost
            or BuffApplicationState.NotConfirmed;

    /// <summary>True while the module must keep polling this application.</summary>
    public static bool IsLive(BuffApplicationState state) =>
        state is BuffApplicationState.Requested or BuffApplicationState.Active;

    /// <summary>
    /// True when this application should be counted as holding the (target, buff) pair against
    /// other applications. A request counts: it has already asked the game for the instance, and
    /// a second apply in the same frame would produce exactly the double-owner case the conflict
    /// rule exists to prevent.
    /// </summary>
    internal static bool HoldsSlot(BuffApplicationState state) => IsLive(state);

    /// <summary>Grep <c>buff applied</c> / <c>buff requested</c> / <c>buff ended</c>.</summary>
    internal static string FormatTransition(
        string buffId, BuffEntityId target, BuffEntityId instance, BuffApplicationState state) =>
        "buff " + Verb(state) + " id='" + (buffId ?? "") + "' target=" + target
        + (instance.IsNone ? "" : " instance=" + instance)
        + " state=" + state;

    private static string Verb(BuffApplicationState state) => state switch
    {
        BuffApplicationState.Requested => "requested",
        BuffApplicationState.Active => "applied",
        BuffApplicationState.Ended => "ended",
        BuffApplicationState.Removed => "removed",
        BuffApplicationState.TargetLost => "target lost",
        BuffApplicationState.NotConfirmed => "not confirmed",
        _ => "refused",
    };

    /// <summary>
    /// The sentence <see cref="BuffApplicationState.Ended"/> means, spelled out wherever it is
    /// reported. It is one observation covering three causes, and a caller that treats it as
    /// "expired" will be wrong for the other two.
    /// </summary>
    public const string EndedMeaning =
        "the instance is gone and this application did not remove it: the duration elapsed, the "
        + "wearer died and the game cleared its buffs, or another source destroyed it - this path "
        + "cannot tell those apart";
}
