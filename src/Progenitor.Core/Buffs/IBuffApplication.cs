using System;

namespace Progenitor.Buffs;

/// <summary>
/// One plugin's application of one buff to one target. Core-owned, so a collectible plugin never
/// holds a game entity across unload.
///
/// <para><b>What this handle owns is its own contribution and nothing else.</b> Removal destroys
/// the buff instance <em>this</em> application confirmed, on the target it was applied to, in the
/// world epoch it was applied in. It never touches another application's instance, never removes a
/// buff it did not create, and never claims that some other change to the target is reversible.
/// The stat contribution itself is reversed by the game: a buff instance registers its modification
/// through the game's own <c>ModificationsRegistry</c> and the matching <c>Destroy_…</c> system
/// reverses it by id when the instance dies, which is why a second mod's modifier on the same stat
/// survives this removal untouched.</para>
///
/// <para><b>Idempotent.</b> <see cref="Remove"/> and <see cref="IDisposable.Dispose"/> may be called
/// any number of times, in any order, from a terminal state, after the target died, or after the
/// world was replaced. Only the first call that finds a live instance it owns destroys anything.</para>
///
/// <para><b>Not confirmation.</b> A handle returned in <see cref="BuffApplicationState.Requested"/>
/// has queued the game's apply and observed nothing. Wait for <see cref="BuffApplicationState.Active"/>
/// (or subscribe with <see cref="OnStateChanged"/>) before telling a player the buff is on.</para>
/// </summary>
public interface IBuffApplication : IDisposable
{
    /// <summary>The <c>DefineBuff</c> id this application was asked for.</summary>
    string BuffId { get; }

    /// <summary>BepInEx plugin GUID recorded at apply time. The unload sweep matches on it.</summary>
    string PluginId { get; }

    /// <summary>The character this buff was applied to.</summary>
    BuffEntityId Target { get; }

    /// <summary>
    /// The buff entity the game built, or <see cref="BuffEntityId.None"/> while the apply is
    /// unconfirmed, refused, or already over.
    /// </summary>
    BuffEntityId Instance { get; }

    /// <summary>Where this application is. See <see cref="BuffApplicationState"/>.</summary>
    BuffApplicationState State { get; }

    /// <summary>
    /// True only in <see cref="BuffApplicationState.Active"/>: the buff instance has been observed
    /// in the target's <c>BuffBuffer</c>.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Why the apply never reached the game, or <see cref="BuffRefusal.None"/>. Set together with
    /// <see cref="BuffApplicationState.Refused"/> and never afterwards.
    /// </summary>
    BuffRefusal Refusal { get; }

    /// <summary>The one actionable sentence for <see cref="Refusal"/>, or an empty string.</summary>
    string RefusalReason { get; }

    /// <summary>
    /// Why the <b>last refresh</b> of this live application did not take, or
    /// <see cref="BuffRefusal.None"/> when the last one succeeded (or none was asked for).
    ///
    /// <para><b>Read this after an <c>ApplyOrRefresh</c>.</b> On the refresh route the returned
    /// handle is the application that is already live, so <see cref="State"/> stays
    /// <see cref="BuffApplicationState.Active"/> and <see cref="Refusal"/> stays
    /// <see cref="BuffRefusal.None"/> - those describe the original apply and are deliberately not
    /// rewritten. Without this property a refresh that was refused is indistinguishable from one
    /// that took, and the worked example reported success for a buff whose duration was never
    /// written.</para>
    ///
    /// <para>Cleared on the next successful refresh.</para>
    /// </summary>
    BuffRefusal LastRefreshRefusal { get; }

    /// <summary>
    /// The one actionable sentence for <see cref="LastRefreshRefusal"/>, or an empty string when the
    /// last refresh succeeded or none was asked for.
    /// </summary>
    string LastRefreshRefusalReason { get; }

    /// <summary>
    /// Seconds this application asked the instance to last, or
    /// <see cref="BuffRequest.KeepPrefabDuration"/> when it kept the clone's own duration.
    /// <b>A request, not a clock:</b> the countdown is the game's <c>LifeTime</c> on the instance,
    /// and nothing here re-reads it.
    /// </summary>
    float RequestedDurationSeconds { get; }

    /// <summary>
    /// Raised on the game main thread whenever <see cref="State"/> changes, including into a
    /// terminal state. Handlers are dropped when the application ends or the plugin unloads;
    /// an exception from one is logged and does not stop the others.
    /// </summary>
    event Action<IBuffApplication>? OnStateChanged;

    /// <summary>
    /// Extend (or shorten) the live instance's remaining time to <paramref name="seconds"/>.
    /// False when this application is not <see cref="BuffApplicationState.Active"/>, the duration
    /// is not a finite positive number of seconds, or the instance carries no <c>LifeTime</c>.
    /// </summary>
    bool Refresh(float seconds);

    /// <summary>
    /// End this application. Destroys the instance it owns if there is one; if the apply has not
    /// been confirmed yet, cancels it and keeps watching long enough to destroy an instance that
    /// arrives late, so a cancel cannot leave an orphan on the player. Safe to call repeatedly.
    /// </summary>
    void Remove();
}
