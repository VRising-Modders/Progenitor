namespace Progenitor.Buffs;

/// <summary>
/// Where one buff application is in its life. <b>A requested application is not an active one</b>:
/// <c>DebugEventsSystem.ApplyBuff</c> only queues an <c>ApplyBuffDebugEvent</c> and the game builds
/// the buff entity on a later update (the same window
/// <c>PrefabLiveRefresh.RestoreDurationFrames</c> was measured for), so
/// <see cref="Requested"/> and <see cref="Active"/> are different facts and the API never collapses
/// them.
///
/// <para><see cref="Refused"/>, <see cref="Ended"/>, <see cref="Removed"/>,
/// <see cref="TargetLost"/> and <see cref="NotConfirmed"/> are terminal: an application never
/// leaves them, and every one of them is safe to <c>Dispose</c> again.</para>
/// </summary>
public enum BuffApplicationState
{
    /// <summary>
    /// Nothing was asked of the game. The request failed a check before any write —
    /// <see cref="IBuffApplication.Refusal"/> says which one. Not a failure of the game.
    /// </summary>
    Refused = 0,

    /// <summary>
    /// <c>ApplyBuff</c> was queued and the buff entity has not been observed yet. The application
    /// owns nothing at this point; canceling here still has to watch for a late instance, which is
    /// what <see cref="Removed"/> from this state means.
    /// </summary>
    Requested = 1,

    /// <summary>
    /// The buff instance was found in the target's <c>BuffBuffer</c>. This is the only state in
    /// which a duration, a removal or a stat contribution is a claim about the world.
    /// </summary>
    Active = 2,

    /// <summary>
    /// Confirmed active, then the instance disappeared and this application did not remove it.
    /// <b>The three causes are indistinguishable through this path</b> — the lifetime elapsed,
    /// the wearer died and the game cleared its buffs, or another source destroyed it — so the
    /// state says what was observed and no more.
    /// </summary>
    Ended = 3,

    /// <summary>This application removed its own instance (<c>Remove</c>, <c>Dispose</c>, unload).</summary>
    Removed = 4,

    /// <summary>
    /// The target entity stopped existing (or the world was replaced under it). Nothing was
    /// written and nothing is owed: the buff went with the entity.
    /// </summary>
    TargetLost = 5,

    /// <summary>
    /// Queued, then the confirm window expired with no instance in the target's
    /// <c>BuffBuffer</c>. The apply did not take; nothing is owned and nothing is removed.
    /// </summary>
    NotConfirmed = 6,
}
