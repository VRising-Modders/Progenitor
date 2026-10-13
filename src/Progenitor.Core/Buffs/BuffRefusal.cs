using System;

namespace Progenitor.Buffs;

/// <summary>
/// Why an apply never reached the game. Every value has one actionable sentence in
/// <see cref="BuffRefusalText.For"/>: the caller should be able to fix the call, or know that it
/// cannot be fixed from a plugin, without reading Core.
/// </summary>
public enum BuffRefusal
{
    /// <summary>Not refused.</summary>
    None = 0,

    /// <summary>Called on the client. Buff application is a server-authority operation.</summary>
    NotServer = 1,

    /// <summary>There is no game world yet (main menu, loading, world replaced).</summary>
    NoWorld = 2,

    /// <summary>The buff id was empty, or is not a buff definition this plugin registered.</summary>
    BuffNotRegistered = 3,

    /// <summary>
    /// The definition exists but its prefab entity is not in the world's collection yet:
    /// registering a definition is not the same as the registry having applied the clone.
    /// </summary>
    BuffNotApplied = 4,

    /// <summary>The target entity does not exist (never did, already destroyed, or a stale version).</summary>
    TargetMissing = 5,

    /// <summary>The target exists but carries no <c>BuffBuffer</c>, so it cannot wear a buff.</summary>
    TargetNotBuffable = 6,

    /// <summary>A live application of this buff on this target already belongs to someone else.</summary>
    AlreadyOwned = 7,

    /// <summary>
    /// This owner already has a live application of this buff on this target and the request
    /// asked for <see cref="BuffRefreshPolicy.Reject"/>.
    /// </summary>
    AlreadyActive = 8,

    /// <summary>The duration was NaN, infinite, or zero/negative and not the explicit "no override".</summary>
    BadDuration = 9,

    /// <summary>The module is not installed, or Core is shutting down.</summary>
    ModuleUnavailable = 10,

    /// <summary>
    /// <c>DebugEventsSystem</c> is not in this world yet. The apply path Progenitor uses is the
    /// game's own debug apply; without that system there is no supported way to queue it.
    /// </summary>
    ApplyPathUnavailable = 11,

    /// <summary>A refresh was asked for but the live instance carries no <c>LifeTime</c> to rewrite.</summary>
    NotTimed = 12,

    /// <summary>
    /// The target already wears an instance of this buff that no live application created. This API
    /// will not adopt one: a save-restored instance, or another source's, is not this
    /// application's to retime or destroy.
    /// </summary>
    AlreadyPresent = 13,

    /// <summary>
    /// A canceled apply of this buff on this target is still in flight. The queued
    /// <c>ApplyBuffDebugEvent</c> may still produce an instance, and the canceled application is
    /// the one that will destroy it, so a second apply now would race it.
    /// </summary>
    CancelInFlight = 14,

    /// <summary>
    /// A refresh could not read the instance's elapsed lifetime, so the remaining time it was asked
    /// to leave cannot be computed. The game keeps elapsed time in a separate <c>ProjectM.Age</c>
    /// component; a clone whose base carries no readable <c>Age</c> cannot be refreshed by
    /// remaining time.
    /// </summary>
    RefreshTimingUnavailable = 15,

    /// <summary>
    /// A refresh would have pushed <c>LifeTime.Duration</c> - which is the <b>total</b> lifetime,
    /// not the remaining one - past the range the game's own wire format allows. Refused rather than
    /// clamped, because a silently shortened buff is the surprise this contract exists to remove.
    /// </summary>
    RefreshTotalOutOfRange = 16,

    /// <summary>
    /// The buff's <c>LifeTime.EndAction</c> is one this API will not refresh by remaining time.
    /// Under <c>RemoveBuffStack</c>, <c>Duration</c> is the length of one <b>stack period</b> rather
    /// than a lifetime, so writing <c>elapsed + remaining</c> into it lengthens every later stack;
    /// under an end action this build does not recognize, what <c>Duration</c> means is unknown.
    /// Refused rather than guessed.
    /// </summary>
    RefreshEndActionUnsupported = 17,

    /// <summary>
    /// The write of the new <c>LifeTime.Duration</c> itself did not take — the instance went away
    /// between the read and the write, or the component write threw. Nothing was written, so the
    /// buff keeps the duration it already had.
    ///
    /// <para>This exists because "the write failed" and "the refresh succeeded" must not be the same
    /// value on <see cref="IBuffApplication.LastRefreshRefusal"/>. On the <c>ApplyOrRefresh</c> route
    /// the returned application stays <c>Active</c>, so a <c>None</c> here would tell the caller a
    /// duration took that was never written.</para>
    /// </summary>
    RefreshWriteFailed = 18,

    /// <summary>
    /// A refresh was asked for on an application that is not <c>Active</c>: it was refused, has not
    /// been confirmed yet, or has already ended or been removed. There is no live instance to
    /// retime. Check <c>State</c>, and apply again rather than refreshing.
    /// </summary>
    RefreshNotActive = 19,
}

/// <summary>One sentence per <see cref="BuffRefusal"/>. Game-free so the tests pin the wording.</summary>
public static class BuffRefusalText
{
    /// <summary>
    /// The actionable reason, with <paramref name="detail"/> (a buff id, a target, an owner)
    /// appended when the caller supplied one.
    /// </summary>
    public static string For(BuffRefusal refusal, string? detail = null)
    {
        string body = Body(refusal);
        return string.IsNullOrEmpty(detail) ? body : body + " (" + detail + ")";
    }

    private static string Body(BuffRefusal refusal) => refusal switch
    {
        BuffRefusal.None => "not refused",
        BuffRefusal.NotServer =>
            "buffs are applied by the server; call this from server-side plugin code "
            + "(ctx.IsServer) and let the client plugin only register the definition",
        BuffRefusal.NoWorld =>
            "there is no game world yet; apply from a WorldReady handler, a command, or a "
            + "virtual system rather than from plugin load",
        BuffRefusal.BuffNotRegistered =>
            "no buff definition with that id is registered by this plugin; call "
            + "ctx.Content.DefineBuff(id).CloneOf(...).Register() first, and use the same id here",
        BuffRefusal.BuffNotApplied =>
            "the definition is registered but its prefab is not in this world's collection yet; "
            + "a registration is not an application - wait for the world to be ready and retry",
        BuffRefusal.TargetMissing =>
            "the target entity does not exist in this world; re-resolve it (a stored "
            + "index:version does not survive a save reload)",
        BuffRefusal.TargetNotBuffable =>
            "the target has no BuffBuffer, so the game cannot put a buff on it; pass a character "
            + "entity, not a User entity or a plain marker",
        BuffRefusal.AlreadyOwned =>
            "another live application already owns this buff on this target; one application "
            + "owns one instance, so remove that one or define a second buff id",
        BuffRefusal.AlreadyActive =>
            "this owner already has a live application of this buff on this target; pass "
            + "BuffRefreshPolicy.Refresh to extend it instead of applying twice",
        BuffRefusal.BadDuration =>
            "the duration must be a finite number of seconds greater than zero; omit it to keep "
            + "the duration the cloned vanilla buff already carries",
        BuffRefusal.ModuleUnavailable =>
            "the buff module is not installed in this process (Core is loading, unloading, or the "
            + "module was skipped)",
        BuffRefusal.ApplyPathUnavailable =>
            "DebugEventsSystem is not in this world yet; it is the game path Progenitor applies "
            + "buffs through, so retry once the world has finished creating its systems",
        BuffRefusal.NotTimed =>
            "the live instance carries no LifeTime, so its duration cannot be set; clone a vanilla "
            + "buff that already expires on its own (for example a Buff_General_* timed buff)",
        BuffRefusal.AlreadyPresent =>
            "the target already wears this buff and no live application created it (a save-restored "
            + "instance, or another source); this API never adopts an instance it did not create - "
            + "remove that one first, or define a second buff id",
        BuffRefusal.CancelInFlight =>
            "a canceled apply of this buff on this target is still in flight; wait a few frames "
            + "for it to finish and try again",
        BuffRefusal.RefreshTimingUnavailable =>
            "the live instance carries no readable Age, so the remaining time a refresh should "
            + "leave cannot be computed; remove and apply again to restart the clock",
        BuffRefusal.RefreshTotalOutOfRange =>
            "the elapsed time plus the requested remaining time is past the range the game's "
            + "LifeTime.Duration allows; remove and apply again to restart the clock",
        BuffRefusal.RefreshEndActionUnsupported =>
            "this buff's LifeTime.EndAction is not one a remaining-time refresh can be computed "
            + "for - under RemoveBuffStack the duration is one stack period rather than a lifetime, "
            + "and an unrecognized end action gives the duration no known meaning; remove and apply "
            + "again instead",
        BuffRefusal.RefreshWriteFailed =>
            "the new duration could not be written onto the live instance - it went away between "
            + "the read and the write, or the write threw; nothing was written, so the buff keeps "
            + "the duration it already had",
        BuffRefusal.RefreshNotActive =>
            "this application is not active, so there is no live instance to retime - check State, "
            + "and apply again rather than refreshing",
        _ => "refused",
    };

    /// <summary>The one log line a refusal writes. Grep <c>buff refused</c>.</summary>
    public static string Line(string buffId, BuffEntityId target, BuffRefusal refusal, string? detail = null) =>
        "buff refused id='" + (buffId ?? "") + "' target=" + target
        + " reason=" + refusal + ": " + For(refusal, detail);
}
