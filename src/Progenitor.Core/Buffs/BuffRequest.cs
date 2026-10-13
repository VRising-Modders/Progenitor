using System;

namespace Progenitor.Buffs;

/// <summary>
/// What happens when this owner applies a buff it already has live on the same target.
///
/// <para><b>Stacking is deliberately absent.</b> The game's <c>ApplyBuffDebugEvent</c> carries a
/// <c>Stacks</c> field, but what a second stack does is authored per buff prefab
/// (<c>MovementSpeedStackModifier</c> and friends read a per-stack value) and Progenitor has not
/// verified those semantics for a cloned buff. This API always requests exactly one stack and says
/// so rather than exposing a knob whose effect it cannot describe.</para>
/// </summary>
public enum BuffRefreshPolicy
{
    /// <summary>
    /// Default. A second apply is refused with <see cref="BuffRefusal.AlreadyActive"/> and nothing
    /// is written, so a command spammed twice cannot quietly produce two owners of one instance.
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Rewrite the live instance's <c>LifeTime.Duration</c> and return <b>the application that is
    /// already live</b> — not a second handle onto the same instance. One instance keeps one owner.
    ///
    /// <para>The requested seconds are the time left <b>from now</b>: the instance's elapsed time is
    /// read and <c>elapsed + seconds</c> is written. The clock is not restarted — nothing resets
    /// <c>Age</c> — so "30" means "30 seconds from now", whatever has already run.</para>
    ///
    /// <para>A refresh can be refused (unreadable timing, a total past the game's range, or an end
    /// action whose duration is not a lifetime). On this route the returned application stays
    /// <c>Active</c> with its previous <c>RequestedDurationSeconds</c>, so read
    /// <see cref="IBuffApplication.LastRefreshRefusal"/> to tell a refusal from a refresh that took.</para>
    /// </summary>
    Refresh = 1,
}

/// <summary>
/// One apply, as data. Built with <see cref="For(string, BuffEntityId)"/> and the <c>With…</c>
/// methods so adding a field later cannot break a caller's constructor call.
/// </summary>
public sealed class BuffRequest
{
    private BuffRequest(
        string buffId,
        BuffEntityId target,
        float durationSeconds,
        BuffRefreshPolicy refresh)
    {
        BuffId = buffId ?? "";
        Target = target;
        DurationSeconds = durationSeconds;
        Refresh = refresh;
    }

    /// <summary>
    /// Sentinel for <see cref="DurationSeconds"/>: keep whatever duration the cloned vanilla buff
    /// prefab carries. Not a magic number the caller has to know — <see cref="For"/> starts here.
    /// </summary>
    public const float KeepPrefabDuration = -1f;

    /// <summary>
    /// The <c>DefineBuff</c> id, which is also the input to <c>PrefabGUID.CalculateGuidHash</c>.
    /// Must name a buff definition <b>this plugin</b> registered.
    /// </summary>
    public string BuffId { get; }

    /// <summary>The character entity that wears the buff. Must carry <c>BuffBuffer</c>.</summary>
    public BuffEntityId Target { get; }

    /// <summary>
    /// Seconds the instance should last, or <see cref="KeepPrefabDuration"/> to keep the clone's
    /// own duration. Written onto the instance's <c>LifeTime.Duration</c> once the apply is
    /// confirmed; <c>LifeTime.EndAction</c> is never written, so the vanilla base decides what
    /// happens at zero.
    /// </summary>
    public float DurationSeconds { get; }

    /// <summary>What a second apply of the same buff on the same target by this owner does.</summary>
    public BuffRefreshPolicy Refresh { get; }

    /// <summary>True when the caller wants the clone's own duration left alone.</summary>
    public bool KeepsPrefabDuration => DurationSeconds <= 0f;

    /// <summary>Start a request: this buff, on this target, with the prefab's own duration.</summary>
    public static BuffRequest For(string buffId, BuffEntityId target) =>
        new(buffId, target, KeepPrefabDuration, BuffRefreshPolicy.Reject);

    /// <summary>
    /// Override the instance's duration in seconds. A finite value at or below zero keeps the
    /// prefab's duration. Apply refuses non-finite values and positive values above
    /// <see cref="BuffApplicationPolicy.MaxDurationSeconds"/> with <see cref="BuffRefusal.BadDuration"/>.
    /// </summary>
    public BuffRequest WithDuration(float seconds) =>
        new(BuffId, Target, seconds, Refresh);

    /// <summary>Override the instance's duration. A value at or below zero keeps the prefab's duration.</summary>
    public BuffRequest WithDuration(TimeSpan duration) =>
        WithDuration((float)duration.TotalSeconds);

    /// <summary>Keep the duration the cloned vanilla buff prefab carries.</summary>
    public BuffRequest WithPrefabDuration() =>
        new(BuffId, Target, KeepPrefabDuration, Refresh);

    /// <summary>Choose what a second apply by this owner does. Default <see cref="BuffRefreshPolicy.Reject"/>.</summary>
    public BuffRequest WithRefresh(BuffRefreshPolicy policy) =>
        new(BuffId, Target, DurationSeconds, policy);

    public override string ToString() =>
        "id='" + BuffId + "' target=" + Target
        + " duration=" + (KeepsPrefabDuration ? "prefab" : DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s")
        + " refresh=" + Refresh;
}
