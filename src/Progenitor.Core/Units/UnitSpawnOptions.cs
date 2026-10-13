using System;

namespace Progenitor.Units;

/// <summary>
/// Extra spawn settings for <c>ctx.Units.Spawn(prefab, position, options)</c>. Null fields keep the
/// prefab's own value.
/// <para>
/// <b>Callback timing.</b> <see cref="Configure"/> runs on the game main thread after a successful
/// enabled spawn (instantiate + place + optional behavior tree + optional lifetime / team /
/// stats / VBlood strip and the other fields below), and <b>before</b> <c>Spawn</c> returns. It is
/// <b>not</b> called for a disabled handle (client, skipped module, missing prefab). A throw
/// from the callback is caught, logged, and does not dispose the unit.
/// </para>
/// <para>
/// Instance fields here never edit the shared prefab. Prefer these over <c>Content.Edit</c>
/// when only this spawn should change.
/// </para>
/// </summary>
public sealed class UnitSpawnOptions
{
    /// <summary>
    /// Faction prefab (name such as <c>Faction_Bandits</c>, or a signed GUID hash) written to the new
    /// unit on the spawn tick — both its <c>FactionReference</c> (the source the game derives
    /// <c>Team.FactionIndex</c> from at spawn) and its <c>Team.FactionIndex</c> — and checked again a
    /// second and five seconds later. Spawning several copies of a boss whose faction is not self-friendly (Frostmaw's
    /// <c>Faction_Wendigo</c>) makes them damage and down each other; Progenitor never changes that
    /// silently — pick a self-friendly faction here if you do not want it. See docs/ai.md.
    /// </summary>
    public string? Faction { get; set; }

    /// <summary>Vanilla behavior-tree prefab to bind on the spawn tick (same as the <c>behaviourTree</c> argument).</summary>
    public string? BehaviourTree { get; set; }

    /// <summary>
    /// Optional lifetime in seconds of server time: the unit gets <c>Age</c> when it has none and
    /// <c>LifeTime { Age + seconds, Destroy }</c>. The stamp is saved with the unit, and Core's
    /// lifetime sweep enforces it after a Core swap, a world load or a restart. Plugin unload still
    /// despawns owned units immediately via <see cref="Units.RemoveAll"/> / <c>SweepForUnload</c>.
    /// Must be &gt; 0 when set. Prefer <see cref="Permanent"/> when the goal is
    /// to clear a prefab's short timed life rather than add a new timer.
    /// </summary>
    public float? LifetimeSeconds { get; set; }

    /// <summary>
    /// Hitbox scale applied on the spawn tick and checked again a second and five seconds later, as
    /// <c>ctx.Units.SetHitboxScale</c> does it: 0.25 to 4, 1 (or null) keeps the vanilla hitbox. Out of
    /// range throws before anything is spawned. A unit whose collider cannot be scaled (no capsule or
    /// sphere on the Hittable layer) is spawned anyway with its vanilla hitbox and one warning.
    /// Only the server-side hitbox changes; the model's visual size does not.
    /// </summary>
    public float? HitboxScale { get; set; }

    /// <summary>
    /// Move speed multiplier applied on the spawn tick and checked again a second and five seconds
    /// later, as <c>ctx.Units.SetMoveSpeed</c> does it: 0.1 to 4, 1 (or null) keeps the vanilla speed.
    /// Out of range throws before anything is spawned.
    /// </summary>
    public float? MoveSpeedMultiplier { get; set; }

    /// <summary>
    /// Visual size, tint, glow and overlay drawn on Progenitor clients, as <c>ctx.Units.SetLook</c>
    /// does it; null (or a vanilla look) changes nothing. Validated before anything is spawned: an out
    /// of range field throws. Client-side only and independent of <see cref="HitboxScale"/>.
    /// </summary>
    public UnitLook? Look { get; set; }

    /// <summary>
    /// Absolute level, max health and power, as <c>ctx.Units.SetStats</c> / <c>TrySetStats</c> write
    /// them; null (or an empty one) changes nothing. Validated before anything is spawned: an out of
    /// range field throws. Written once the game's spawn computation has filled the unit's health (a
    /// fresh unit reads max health 0), checked at 0.25 s, 1 s and 5 s; a refusal then is one warning
    /// and the unit keeps its own stats.
    /// </summary>
    public UnitStatOverrides? Stats { get; set; }

    /// <summary><c>Team.Value</c> on this instance. Null leaves the prefab team alone.</summary>
    public int? TeamValue { get; set; }

    /// <summary><c>Team.FactionIndex</c> on this instance. Null leaves the prefab faction index alone.</summary>
    public int? FactionIndex { get; set; }

    /// <summary>
    /// When true, clear <c>BloodConsumeSource.CanBeConsumed</c> and remove <c>VBloodUnit</c> /
    /// <c>VBloodConsumeSource</c> on this instance so a spawned V Blood clone is not a world-boss
    /// blood source. Does <b>not</b> remove <c>BloodConsumeSource</c> (networked snapshot risk).
    /// </summary>
    public bool StripVBlood { get; set; }

    /// <summary>
    /// Server-applied aura put on the unit on the spawn tick, as <c>ctx.Units.SetAura</c> does it; null
    /// or <see cref="UnitAura.None"/> changes nothing. An undefined value throws before anything is
    /// spawned. Every client that has the aura's clone draws it natively (docs/modules.md, "Unit aura").
    /// When the Core clone is not verified inert yet, the unit spawns without it and one warning is logged.
    /// </summary>
    public UnitAura? Aura { get; set; }

    /// <summary>
    /// When true, the unit's health bar and name plate are hidden on Progenitor clients from the spawn
    /// tick, as <c>ctx.Units.SetHealthBarHidden(handle, true)</c> does it. Default false. When Core's
    /// hide-health-bar buff is not verified yet, the unit spawns with its bar and one warning is logged.
    /// </summary>
    public bool HideHealthBar { get; set; }

#if !PROGENITOR_TESTS
    /// <summary>
    /// A character (normally a player) whose team the unit joins on the spawn tick, as
    /// <c>ctx.Units.JoinTeam(handle, character)</c> does it: its <c>Team.Value</c> and <c>TeamReference</c>
    /// are copied, its own faction index is kept, and it is checked again a second and five seconds later.
    /// Default <see cref="Unity.Entities.Entity.Null"/>: the unit stays on the generic unit team (2). A
    /// refusal is one warning and the unit keeps its team.
    /// </summary>
    public Unity.Entities.Entity TeamOf { get; set; }
#endif

    /// <summary>
    /// When true, a unit whose prefab carries a timed <c>LifeTime</c> has it written to
    /// <c>{Duration -1, EndAction None}</c> on the spawn tick, so it no longer dies or vanishes when
    /// the time runs out. A unit without <c>LifeTime</c> is unchanged. Default false. Example:
    /// <c>CHAR_Unholy_DeathKnight</c> ships <c>{6, Kill}</c> and dies 6 s after spawn.
    /// </summary>
    public bool Permanent { get; set; }

    /// <summary>
    /// When false, the unit drops nothing from its drop tables on death or destroy: its
    /// <c>DropTableBuffer</c>, <c>DropTableOnDeath</c>, <c>DropTableOnDestroy</c> and <c>DropTable</c> are removed on
    /// the spawn tick. Default true (the prefab's drops). Example: <c>CHAR_Unholy_DeathKnight</c> drops
    /// <c>DT_Unit_Dunley_Militia_Rank02</c> (militia loot such as thick hide) on death.
    /// </summary>
    public bool DropLoot { get; set; } = true;

    /// <summary>
    /// Entrance effect applied right after spawn (<see cref="UnitEntrance"/>); None (the default)
    /// changes nothing. An undefined value throws before anything is spawned.
    /// </summary>
    public UnitEntrance Entrance { get; set; }

    /// <summary>
    /// Invoked once for an enabled handle after options above have been applied (or recorded).
    /// Never invoked for a disabled handle. See type remarks for timing.
    /// </summary>
    public Action<IUnitHandle>? Configure { get; set; }

    /// <summary>
    /// Why these options are refused for the settings this class checks itself
    /// (<see cref="LifetimeSeconds"/>, <see cref="Stats"/>, <see cref="Entrance"/>), or null.
    /// Other fields are checked by their own types / spawn path.
    /// </summary>
    public string? Refusal()
    {
        if (LifetimeSeconds is float life && (!(life > 0f) || float.IsNaN(life) || float.IsInfinity(life)))
            return "LifetimeSeconds must be finite and greater than 0.";
        if (Stats != null && Stats.Refusal() is { } statsWhy)
            return statsWhy;
        if (ExtrasRefusal() is { } extras)
            return extras;
        return null;
    }

    /// <summary>Throw when <see cref="Refusal"/> names a problem.</summary>
    public void Validate()
    {
        if (Refusal() is { } why)
            throw new ArgumentOutOfRangeException(nameof(UnitSpawnOptions), why);
    }

    /// <summary>
    /// Why these options are refused for the settings this class checks itself (<see cref="Entrance"/>),
    /// or null. The other fields are checked by their own types.
    /// </summary>
    internal string? ExtrasRefusal() =>
        UnitEntranceTable.IsDefined(Entrance) ? null : UnitEntranceTable.RangeMessage(Entrance);
}
