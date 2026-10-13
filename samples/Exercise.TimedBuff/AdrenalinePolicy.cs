using Progenitor.Buffs;

namespace Exercise.TimedBuff;

/// <summary>
/// This plugin's own choices: the buff id, the vanilla base it clones, the stat it changes, the
/// durations the command accepts and every sentence it sends back. No game types below this line
/// so the shape of the choices can be read without a running server.
/// </summary>
public static class AdrenalinePolicy
{
    /// <summary>
    /// Definition id. Hashed into the clone's <c>PrefabGUID</c>
    /// (<c>PrefabGUID.CalculateGuidHash</c>) — keep it stable once anyone has the buff applied.
    /// </summary>
    public const string BuffId = "exercise.adrenaline";

    /// <summary>
    /// Vanilla base to clone. <c>Buff_General_Haste</c> is the one base this repository's own
    /// docs and samples treat as verified for a player-visible timed buff: it carries
    /// <c>ShowOnBuffBar</c> so the clone shows up on the action bar, and it already carries a
    /// <c>LifeTime</c> with an <c>EndAction</c> that ends the buff on its own, which is required
    /// before this API will accept a duration write (see docs/buffs.md, "Duration").
    /// </summary>
    public const string CloneOf = "Buff_General_Haste";

    public const string Name = "Adrenaline Rush";

    public const string Description = "Your heart pounds too hard for the blood loss to matter much.";

    public const float DefaultSeconds = 20f;
    public const float MinSeconds = 5f;
    public const float MaxSeconds = 300f;

    /// <summary>
    /// The one gameplay change this buff makes: blood drain multiplied down to a tenth while it
    /// is on, rather than set to zero outright. Blood drain is the only stat modifier this build
    /// of Progenitor supports (docs/buffs.md, "Supported stat modifiers") — movement speed cannot be
    /// <i>written</i>, because the value semantics have never been measured. It can, however, be
    /// <i>suppressed</i>: see <see cref="Stats"/>, which drops the haste this buff's cloned base
    /// would otherwise grant. Removing a component and authoring a value are different questions.
    /// </summary>
    public static BuffStat Stat() => BuffStat.BloodDrain(BuffStatOp.Multiply, 0.1f);

    /// <summary>
    /// Everything this buff declares. **Both entries below are coordinator corrections made during
    /// live acceptance on 2026-09-13, not the fresh agent's work** — recorded that way so the A6
    /// record stays honest.
    ///
    /// <para><b>The drain stat now means what it says.</b> <see cref="Stat"/> above is unchanged
    /// text, but it used to write the active *and* idle drain from one call, registering two
    /// modifications against the same value — so <c>Multiply 0.1</c> actually produced 0.01x
    /// (observed live: the wearer's drain went 0.005 -> 5e-5). <see cref="BloodDrainScope.Active"/>
    /// is now the default, so this is one modification and a tenth means a tenth. The agent's
    /// intent — "drain a tenth as fast" — was right; the framework was wrong.</para>
    ///
    /// <para><b>Movement speed suppressed.</b> <c>Buff_General_Haste</c> is cloned for its buff-bar
    /// visibility and timed lifetime, and a clone inherits everything it carries — including haste.
    /// Live, this buff granted +50% run speed that nothing in its name, description or code
    /// mentioned.</para>
    /// </summary>
    public static BuffStat[] Stats() => new[]
    {
        Stat(),
        BuffStat.Suppress(BuffStatKind.MovementSpeed),
    };

    public static bool TryParseSeconds(string? argument, out float seconds, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(argument))
        {
            seconds = DefaultSeconds;
            return true;
        }

        string trimmed = argument.Trim();
        if (!float.TryParse(
                trimmed,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out seconds))
        {
            reason = "'" + trimmed + "' is not a number of seconds.";
            return false;
        }

        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < MinSeconds || seconds > MaxSeconds)
        {
            reason = "Give a number of seconds between " + Text(MinSeconds) + " and " + Text(MaxSeconds) + ".";
            seconds = 0f;
            return false;
        }

        return true;
    }

    public static string RequestedReply(float seconds) =>
        Name + " requested for " + Text(seconds) + "s. It is not on yet — watch your bar.";

    public static string ActiveReply(float seconds) =>
        Name + " is on for " + Text(seconds) + "s. Blood drain is down to a tenth.";

    public static string EndedReply(BuffApplicationState state) => state switch
    {
        BuffApplicationState.Ended => Name + " has worn off.",
        BuffApplicationState.Removed => Name + " was taken off.",
        BuffApplicationState.TargetLost => Name + " ended along with your character.",
        BuffApplicationState.NotConfirmed => Name + " never took — nothing was changed.",
        _ => Name + " ended (" + state + ").",
    };

    public const string NothingToRemoveReply = "You do not have " + Name + " from this plugin right now.";

    public const string RemovedReply = Name + " taken off.";

    public const string StatusNoneReply = "No " + Name + " application from this plugin.";

    public static string StatusReply(BuffApplicationState state, string instance) =>
        Name + ": state=" + state + (string.IsNullOrEmpty(instance) ? "" : ", instance=" + instance);

    private static string Text(float value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
