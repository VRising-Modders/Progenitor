using Progenitor.Buffs;

namespace Progenitor.TimedBuffExample;

/// <summary>
/// The example's own choices, with no game in them: the definition id, the vanilla base, the
/// stat it modifies, the durations its command accepts and the sentences it replies with.
///
/// <para>Split out for the reason every Progenitor sample splits: what a test asserts about this
/// plugin should be what the plugin does, not a second copy of it. <c>Plugin.cs</c> reads every
/// constant and every sentence from here.</para>
/// </summary>
public static class TimedBuffPolicy
{
    /// <summary>
    /// Definition id. Hashed into the clone's PrefabGUID
    /// (<c>PrefabGUID.CalculateGuidHash</c>), so it must be identical on every peer and renaming
    /// it orphans every instance already applied.
    /// </summary>
    public const string BuffId = "example.rested";

    /// <summary>
    /// Vanilla base to clone. <c>Buff_General_Haste</c> is player-facing: it carries
    /// <c>ShowOnBuffBar</c> (and, on the client, <c>ShowOnBuffBarData</c>), so the clone is visible
    /// on the action bar, and it is a <b>timed</b> buff, so it already carries the
    /// <c>LifeTime</c> this example rewrites per application and the <c>EndAction</c> that decides
    /// what happens at zero. Picking a base that is already visible and already expires is the
    /// point: neither is something a plugin should be inventing.
    /// </summary>
    public const string CloneOf = "Buff_General_Haste";

    /// <summary>HUD title, sent to the client (1-63 ASCII).</summary>
    public const string Name = "Rested";

    /// <summary>HUD tooltip, sent to the client (1-63 ASCII).</summary>
    public const string Description = "Your blood does not drain while this lasts.";

    /// <summary>Default duration when the command is given no number.</summary>
    public const float DefaultSeconds = 30f;

    /// <summary>Shortest duration the command accepts.</summary>
    public const float MinSeconds = 1f;

    /// <summary>
    /// Longest duration the command accepts. Well inside
    /// <see cref="BuffApplicationPolicy.MaxDurationSeconds"/>; this is the example's own limit, so
    /// a typo cannot leave a buff on a player for three hours.
    /// </summary>
    public const float MaxSeconds = 600f;

    /// <summary>
    /// The stats this buff declares.
    ///
    /// <para><b>Blood drain, set to zero, active and idle.</b> This buff promises "your blood does
    /// not drain", which means idle drain too — so it asks for
    /// <see cref="BloodDrainScope.ActiveAndIdle"/> <i>explicitly</i>. That scope registers two
    /// modifications against the same value, which is harmless here only because the op is
    /// <see cref="BuffStatOp.Set"/>: setting the same value twice is idempotent. Under
    /// <c>Add</c> or <c>Multiply</c> it would compound, which is why <c>Active</c> is the default.
    /// This is byte-identical to what ran live on 2026-09-13.</para>
    ///
    /// <para><b>Movement speed suppressed.</b> The base cloned below is
    /// <c>Buff_General_Haste</c>, chosen for its buff-bar visibility and its timed
    /// <c>LifeTime</c>/<c>EndAction</c> — and a clone inherits <b>everything</b>, including that
    /// buff's <c>ModifyMovementSpeedBuff</c>. Live on 2026-09-13 this example granted +50% run speed
    /// nobody asked for and its own description did not mention. Suppressing it is what makes
    /// "your blood does not drain while this lasts" the whole truth.</para>
    /// </summary>
    public static BuffStat[] Stats() => new[]
    {
        BuffStat.BloodDrain(BuffStatOp.Set, 0f, priority: 0, scope: BloodDrainScope.ActiveAndIdle),
        BuffStat.Suppress(BuffStatKind.MovementSpeed),
    };

    /// <summary>The drain stat on its own. Kept for the tests that pin this example's promise.</summary>
    public static BuffStat Stat() => Stats()[0];

    /// <summary>Parse the command's optional seconds argument. False with the reason for a bad one.</summary>
    public static bool TryParseSeconds(string? argument, out float seconds, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(argument))
        {
            seconds = DefaultSeconds;
            return true;
        }

        if (!float.TryParse(
                argument.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out seconds))
        {
            reason = "'" + argument.Trim() + "' is not a number of seconds.";
            return false;
        }

        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < MinSeconds || seconds > MaxSeconds)
        {
            reason = "Seconds must be between " + Text(MinSeconds) + " and " + Text(MaxSeconds) + ".";
            seconds = 0f;
            return false;
        }

        return true;
    }

    /// <summary>What the command replies the moment an apply is <em>requested</em>. Never "it is on".</summary>
    public static string RequestedReply(float seconds) =>
        "Asked the game for " + Name + " (" + Text(seconds) + "s). "
        + "You will see it on your bar when it takes; it is not on yet.";

    /// <summary>What the state-change handler says once the buff is confirmed active.</summary>
    public static string ActiveReply(float seconds) =>
        Name + " is on for " + Text(seconds) + "s.";

    /// <summary>What the handler says for each terminal state. One sentence, honest about what it knows.</summary>
    public static string EndedReply(BuffApplicationState state) => state switch
    {
        BuffApplicationState.Ended =>
            Name + " is over.",
        BuffApplicationState.Removed =>
            Name + " was removed.",
        BuffApplicationState.TargetLost =>
            Name + " ended because your character is gone.",
        BuffApplicationState.NotConfirmed =>
            Name + " did not take - nothing was applied and nothing was changed.",
        _ => Name + " ended (" + state + ").",
    };

    /// <summary>What the remove command replies when there was nothing of ours to remove.</summary>
    public const string NothingToRemoveReply =
        "You have no " + Name + " from this plugin. Nothing was changed.";

    /// <summary>What the remove command replies after removing.</summary>
    public const string RemovedReply = Name + " removed.";

    /// <summary>What the status command replies when there is no application.</summary>
    public const string StatusNoneReply = "No " + Name + " application from this plugin.";

    /// <summary>Status line for a live application. States the state; claims nothing else.</summary>
    public static string StatusReply(BuffApplicationState state, string instance) =>
        Name + ": state=" + state + (string.IsNullOrEmpty(instance) ? "" : " instance=" + instance);

    private static string Text(float value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
