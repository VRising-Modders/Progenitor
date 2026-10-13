using System;
using System.Collections.Generic;

namespace Progenitor.Buffs;

/// <summary>What one <c>ctx.Debug</c> holder asked for. Ordered: a stronger mode wins when holders disagree.</summary>
internal enum DebugProtectionMode
{
    /// <summary>No holder left: take off everything <c>ctx.Debug</c> itself put on.</summary>
    None = 0,

    /// <summary><c>KeepAlive</c>: death blocked, still targetable.</summary>
    AliveOnly = 1,

    /// <summary><c>Protect</c>: death blocked and ignored by foes.</summary>
    Full = 2,
}

/// <summary>The three buffs <see cref="DebugProtection"/> manages, as flags.</summary>
[Flags]
internal enum DebugProtectionBuffs
{
    None = 0,

    /// <summary>Core's <c>progenitor.debug</c> clone (invulnerable and ignored by foes on its own).</summary>
    Debug = 1,

    /// <summary><c>Admin_Invulnerable_Buff</c>.</summary>
    Invulnerable = 2,

    /// <summary><c>AB_Blood_BloodRite_SpellMod_Stealth</c> (foes ignore the wearer).</summary>
    NoAggro = 4,
}

/// <summary>What <see cref="DebugProtectionPolicy.Plan"/> decided for one character.</summary>
internal readonly struct DebugProtectionPlan
{
    public DebugProtectionPlan(
        DebugProtectionBuffs apply, DebugProtectionBuffs remove, DebugProtectionBuffs keptForeign,
        bool setTargetable, bool mayClearImmortal)
    {
        Apply = apply;
        Remove = remove;
        KeptForeign = keptForeign;
        SetTargetable = setTargetable;
        MayClearImmortal = mayClearImmortal;
    }

    /// <summary>Buffs to put on (absent now).</summary>
    public DebugProtectionBuffs Apply { get; }

    /// <summary>Buffs to take off. Only ever buffs <c>ctx.Debug</c> applied and nobody else claims.</summary>
    public DebugProtectionBuffs Remove { get; }

    /// <summary>
    /// Foe-ignoring buffs this mode would not want but that belong to someone else (Core's
    /// <c>.debug</c>, an admin, another holder's request), so they stay.
    /// </summary>
    public DebugProtectionBuffs KeptForeign { get; }

    /// <summary>
    /// True when <c>KeepAlive</c> may force <c>Aggroable</c> on. False while a foe-ignoring buff
    /// stays on, because writing <c>Aggroable</c> would fight that buff's owner.
    /// </summary>
    public bool SetTargetable { get; }

    /// <summary>
    /// False while Core's <c>.debug</c> holds a grant on the character: its <c>Immortal</c> is
    /// then Core's to clear, whoever set it first.
    /// </summary>
    public bool MayClearImmortal { get; }
}

/// <summary>
/// The game-free half of <see cref="DebugProtection"/>: which buffs <c>ctx.Debug</c> may add or
/// remove (2026-10-07 facade smoke, problem 2).
///
/// <para><b>The rule.</b> <c>ctx.Debug</c> removes only a buff it applied itself, and never one
/// Core's <c>.debug</c> claims. Before this, <c>KeepAlive</c> always destroyed the
/// <c>progenitor.debug</c> buff and <c>Release</c> destroyed all three whenever no
/// <c>ctx.Debug</c> holder remained, whoever had applied them. On the smoke that stripped Core's
/// own <c>.debug</c> buff: its watchdog logged <c>watchdog buff gone</c> and
/// <c>grant forgotten</c>, and the character was left downed without protection.</para>
///
/// <para><b>Readback.</b> Removal goes through <c>DestroyUtility.Destroy</c>, which lands next
/// frame, so the buff is still in <c>BuffBuffer</c> when the call returns. That is why
/// <c>.protect alive</c> and <c>.protect off</c> answered <c>ok=False</c>. <see cref="After"/>
/// is the state once the planned changes land, and the result reports <c>Pending</c> when a
/// removal is still in flight. Applying is synchronous (<c>DebugEventsSystem.ApplyBuff</c> adds
/// the <c>BuffBuffer</c> row before it returns, which Core's <c>.debug</c> relies on too).</para>
/// </summary>
internal static class DebugProtectionPolicy
{
    private const DebugProtectionBuffs FoeIgnoring = DebugProtectionBuffs.Debug | DebugProtectionBuffs.NoAggro;

    /// <summary>The strongest mode among <paramref name="modes"/>; <see cref="DebugProtectionMode.None"/> when empty.</summary>
    public static DebugProtectionMode Strongest(IEnumerable<DebugProtectionMode>? modes)
    {
        var best = DebugProtectionMode.None;
        if (modes == null) return best;
        foreach (var m in modes)
            if (m > best) best = m;
        return best;
    }

    /// <summary>
    /// Decide the changes for <paramref name="mode"/> (the strongest mode any holder still asks
    /// for).
    /// </summary>
    /// <param name="mode">Effective mode across every holder.</param>
    /// <param name="present">Buffs the character wears now.</param>
    /// <param name="ours">Buffs <c>ctx.Debug</c> applied and still attributes to itself.</param>
    /// <param name="coreOwnsDebug">Core's <c>.debug</c> holds a grant on this character.</param>
    /// <param name="debugRegistered">The <c>progenitor.debug</c> clone is registered in this world.</param>
    public static DebugProtectionPlan Plan(
        DebugProtectionMode mode, DebugProtectionBuffs present, DebugProtectionBuffs ours,
        bool coreOwnsDebug, bool debugRegistered)
    {
        ours &= present;
        if (coreOwnsDebug) ours &= ~DebugProtectionBuffs.Debug;
        var removable = present & ours;
        var foreign = present & ~ours;

        switch (mode)
        {
            case DebugProtectionMode.Full:
            {
                var need = debugRegistered
                    ? DebugProtectionBuffs.Debug
                    : DebugProtectionBuffs.Invulnerable | DebugProtectionBuffs.NoAggro;
                return new DebugProtectionPlan(need & ~present, DebugProtectionBuffs.None,
                    DebugProtectionBuffs.None, setTargetable: false, mayClearImmortal: !coreOwnsDebug);
            }

            case DebugProtectionMode.AliveOnly:
            {
                var remove = removable & FoeIgnoring;
                var remaining = present & ~remove;
                var apply = DebugProtectionBuffs.None;
                // A kept debug buff is invulnerable on its own; otherwise the admin buff is the
                // invulnerability.
                if ((remaining & (DebugProtectionBuffs.Debug | DebugProtectionBuffs.Invulnerable)) == 0)
                    apply |= DebugProtectionBuffs.Invulnerable;
                var kept = foreign & FoeIgnoring;
                return new DebugProtectionPlan(apply, remove, kept,
                    setTargetable: kept == DebugProtectionBuffs.None, mayClearImmortal: !coreOwnsDebug);
            }

            default:
                return new DebugProtectionPlan(DebugProtectionBuffs.None, removable, DebugProtectionBuffs.None,
                    setTargetable: false, mayClearImmortal: !coreOwnsDebug);
        }
    }

    /// <summary>
    /// The buffs the character wears once <paramref name="removed"/> has landed (next frame) and
    /// <paramref name="applied"/> has (synchronously). <paramref name="presentNow"/> may already
    /// include <paramref name="applied"/>.
    /// </summary>
    public static DebugProtectionBuffs After(
        DebugProtectionBuffs presentNow, DebugProtectionBuffs removed, DebugProtectionBuffs applied) =>
        (presentNow & ~removed) | applied;

    /// <summary>
    /// What <c>ctx.Debug</c> still attributes to itself afterwards: what it held minus what it
    /// removed or lost, plus what it applied in this call.
    /// </summary>
    public static DebugProtectionBuffs OursAfter(
        DebugProtectionBuffs ours, DebugProtectionBuffs presentBefore, DebugProtectionBuffs removed,
        DebugProtectionBuffs applied) =>
        (ours & presentBefore & ~removed) | applied;

    /// <summary>
    /// The <c>ok</c> a caller sees, judged on <paramref name="after"/> (deferred removals counted
    /// as done).
    /// <list type="bullet">
    /// <item><description><see cref="DebugProtectionMode.Full"/>: <c>Immortal</c>, invulnerable,
    /// ignored by foes.</description></item>
    /// <item><description><see cref="DebugProtectionMode.AliveOnly"/>: <c>Immortal</c> and
    /// invulnerable. Being targetable is reported (<c>IgnoredByFoes</c>) but not required,
    /// because a foe-ignoring buff another owner holds is left on on purpose.</description></item>
    /// <item><description><see cref="DebugProtectionMode.None"/> (release):
    /// <paramref name="leftover"/> is empty, i.e. every removal the plan asked for was
    /// issued.</description></item>
    /// </list>
    /// </summary>
    public static bool Ok(
        DebugProtectionMode request, bool immortal, DebugProtectionBuffs after, bool aggroable,
        DebugProtectionBuffs leftover)
    {
        bool invuln = (after & (DebugProtectionBuffs.Debug | DebugProtectionBuffs.Invulnerable)) != 0;
        bool ignored = (after & FoeIgnoring) != 0 || !aggroable;
        return request switch
        {
            DebugProtectionMode.Full => immortal && invuln && ignored,
            DebugProtectionMode.AliveOnly => immortal && invuln,
            _ => leftover == DebugProtectionBuffs.None,
        };
    }

    /// <summary>True when <paramref name="after"/> makes foes ignore the wearer, or <c>Aggroable</c> is off.</summary>
    public static bool IgnoredByFoes(DebugProtectionBuffs after, bool aggroable) =>
        (after & FoeIgnoring) != 0 || !aggroable;

    /// <summary>True when <paramref name="after"/> is invulnerable.</summary>
    public static bool Invulnerable(DebugProtectionBuffs after) =>
        (after & (DebugProtectionBuffs.Debug | DebugProtectionBuffs.Invulnerable)) != 0;
}
