using Progenitor.Units;

namespace Progenitor.Combat;

/// <summary>
/// A non-player unit finished the kill path (<c>DeathUtilities.Kill</c>). Published on the main
/// thread through <see cref="Events.EventBus"/> so plugin <c>before</c>/<c>after</c>/priority
/// order matches every other Progenitor event.
///
/// <para><b>Epoch-safe ids.</b> <see cref="Died"/> / <see cref="Killer"/> / <see cref="Source"/>
/// are <c>index:version</c> pairs stamped with <see cref="Epoch"/>. After a world load they name
/// something else (or nothing) even when <c>Exists</c> still answers true — treat them as dead
/// when <see cref="CombatEventPolicy.IsStale"/> is true for this event's epoch. Prefer reading
/// what you need inside the handler; do not keep a raw entity across <c>WorldLoaded</c>.</para>
///
/// <para>Players raise <see cref="PlayerDied"/> instead of this type. God-mode prefixes that skip
/// <c>Kill</c> never publish either event.</para>
/// </summary>
public sealed class UnitDied
{
    /// <summary>The unit that died.</summary>
    public UnitId Died { get; }

    /// <summary>Attributed killer when the game provided one; otherwise <see cref="UnitId.None"/>.</summary>
    public UnitId Killer { get; }

    /// <summary>Spell / damage source when the game provided one; otherwise <see cref="UnitId.None"/>.</summary>
    public UnitId Source { get; }

    /// <summary>
    /// Game reason string (<c>StatChangeReason</c> and/or <c>DeathReason</c>). Empty when neither
    /// argument could be read from the kill-path postfix.
    /// </summary>
    public string Reason { get; }

    /// <summary><see cref="WorldState.WorldEpoch.Current"/> at publish time.</summary>
    public int Epoch { get; }

    /// <summary>Construct. Core publishes this; tests construct it directly.</summary>
    public UnitDied(UnitId died, UnitId killer, UnitId source, string? reason, int epoch)
    {
        Died = died;
        Killer = killer;
        Source = source;
        Reason = reason ?? "";
        Epoch = epoch;
    }

    /// <inheritdoc />
    public override string ToString() =>
        "UnitDied died=" + Died + " killer=" + Killer + " source=" + Source
        + " reason=" + Reason + " epoch=" + Epoch;
}

/// <summary>
/// A player character finished the kill path. Same bus and epoch rules as <see cref="UnitDied"/>;
/// raised instead of <see cref="UnitDied"/> when the died entity has <c>PlayerCharacter</c>.
/// </summary>
public sealed class PlayerDied
{
    /// <summary>The character that died.</summary>
    public UnitId Character { get; }

    /// <summary>Owning <c>User</c> entity when readable at publish time; otherwise <see cref="UnitId.None"/>.</summary>
    public UnitId User { get; }

    /// <summary>Attributed killer when the game provided one; otherwise <see cref="UnitId.None"/>.</summary>
    public UnitId Killer { get; }

    /// <summary>Spell / damage source when the game provided one; otherwise <see cref="UnitId.None"/>.</summary>
    public UnitId Source { get; }

    /// <summary>Game reason string. Empty when unread.</summary>
    public string Reason { get; }

    /// <summary><see cref="WorldState.WorldEpoch.Current"/> at publish time.</summary>
    public int Epoch { get; }

    /// <summary>Construct. Core publishes this; tests construct it directly.</summary>
    public PlayerDied(UnitId character, UnitId user, UnitId killer, UnitId source, string? reason, int epoch)
    {
        Character = character;
        User = user;
        Killer = killer;
        Source = source;
        Reason = reason ?? "";
        Epoch = epoch;
    }

    /// <inheritdoc />
    public override string ToString() =>
        "PlayerDied character=" + Character + " user=" + User + " killer=" + Killer
        + " source=" + Source + " reason=" + Reason + " epoch=" + Epoch;
}

/// <summary>
/// A health <c>StatChangeEvent</c> was applied this frame (damage or healing). Published on the
/// main thread through <see cref="Events.EventBus"/> from a Core <c>SystemHooks.After</c> on
/// <c>StatChangeSystem</c> — not from a Burst <c>DealDamageSystem</c> patch.
///
/// <para><b>Numbers.</b> <see cref="OriginalChange"/> is the pre-invulnerability delta the game
/// kept on the event; <see cref="Change"/> is what actually applied (may be zeroed by
/// invulnerability). Negative is damage, positive is healing. Blood changes are not published.</para>
///
/// <para>Same epoch rules as <see cref="UnitDied"/>.</para>
/// </summary>
public sealed class DamageDealt
{
    /// <summary>Entity whose health changed.</summary>
    public UnitId Target { get; }

    /// <summary>Attributed source when present; otherwise <see cref="UnitId.None"/>.</summary>
    public UnitId Source { get; }

    /// <summary>Applied health delta (post-invulnerability / post-mitigation path).</summary>
    public float Change { get; }

    /// <summary>Pre-invulnerability health delta from <c>StatChangeEvent.OriginalChange</c>.</summary>
    public float OriginalChange { get; }

    /// <summary><c>StatChangeEvent.StatChangeFlags</c> as an int.</summary>
    public int Flags { get; }

    /// <summary>Game <c>StatChangeReason</c> string.</summary>
    public string Reason { get; }

    /// <summary><see cref="WorldState.WorldEpoch.Current"/> at publish time.</summary>
    public int Epoch { get; }

    /// <summary>Construct. Core publishes this; tests construct it directly.</summary>
    public DamageDealt(
        UnitId target,
        UnitId source,
        float change,
        float originalChange,
        int flags,
        string? reason,
        int epoch)
    {
        Target = target;
        Source = source;
        Change = change;
        OriginalChange = originalChange;
        Flags = flags;
        Reason = reason ?? "";
        Epoch = epoch;
    }

    /// <inheritdoc />
    public override string ToString() =>
        "DamageDealt target=" + Target + " source=" + Source
        + " change=" + Change.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " original=" + OriginalChange.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " flags=" + Flags + " reason=" + Reason + " epoch=" + Epoch;
}
