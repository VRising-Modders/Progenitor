#if !PROGENITOR_TESTS
using System;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.WorldState;
using ProjectM;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Live half of <c>ctx.Units.JoinTeam</c> and <see cref="UnitSpawnOptions.TeamOf"/> (docs/modules.md,
/// "Join a character's team"). Server only; main thread.
///
/// <para>Copies <c>Team.Value</c> and <c>TeamReference.Value</c> (a <c>ModifiableEntity</c>) from the
/// character onto the unit through <see cref="AiRuntime.RW{T}"/> field pokes at the dump offsets
/// (<c>layout.txt</c>), the same overlay path the faction write uses; no generic component access. The
/// unit keeps its own <c>Team.FactionIndex</c> (<see cref="UnitTeamRules"/>). Checked again at 1 s and 5 s
/// against the character's team at that moment, and written again with a warning when something reset it.
/// Nothing is restored on unload or despawn.</para>
/// </summary>
internal static unsafe class UnitTeams
{
    private static readonly double[] Rechecks = { 1.0, 5.0 };

    private static int TeamExtent => Layout.TeamFactionIndexOffset + sizeof(int);

    private static int ReferenceOffset => Layout.TeamReferenceValueOffset + Layout.ModifiableEntityValueOffset;

    private static int ReferenceExtent => ReferenceOffset + sizeof(Entity);

    /// <summary>Put the unit on <paramref name="character"/>'s team now and schedule the two checks. Throws when refused.</summary>
    internal static void Join(IUnitHandle handle, Entity character, object? owner)
    {
        Units.EnsureMainThread("JoinTeam");
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (handle.IsDisposed) throw new InvalidOperationException("the unit handle is disposed.");
        if (!handle.Enabled) throw new InvalidOperationException("the unit handle is disabled.");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException("JoinTeam is server-only and there is no server world.");
        var em = world.EntityManager;
        var unit = handle.Entity.ToEntity();
        if (!em.Exists(unit)) throw new InvalidOperationException("unit " + handle.Entity + " no longer exists.");
        bool characterExists = character != Entity.Null && em.Exists(character);
        string? refusal = UnitTeamRules.Refusal(
            handle.Entity,
            UnitId.From(character),
            characterExists,
            characterExists && character.Has<Team>(em),
            characterExists && character.Has<TeamReference>(em),
            unit.Has<Team>(em),
            unit.Has<TeamReference>(em),
            HasTeamModifyingBuff(em, unit));
        if (refusal != null) throw new InvalidOperationException("JoinTeam refused: " + refusal);

        var target = Read(em, character) ?? throw new InvalidOperationException("JoinTeam refused: the team of " + AiRuntime.Fmt(character) + " could not be read.");
        Write(em, unit, target);
        Units.LogDebug("Units: " + handle.Entity + " joined the team of " + AiRuntime.Fmt(character) + " (Team.Value " + target.Value + ", TeamReference " + target.Reference + ").");
        ScheduleChecks(handle, unit, character, owner);
    }

    /// <summary>True when a buff in the unit's <c>BuffBuffer</c> carries <c>ModifyTeamBuff</c>.</summary>
    internal static bool HasTeamModifyingBuff(EntityManager em, Entity unit)
    {
        try
        {
            if (!unit.Has<BuffBuffer>(em)) return false;
            var buffer = em.GetBuffer<BuffBuffer>(unit, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                var buff = buffer[i].Entity;
                if (buff != Entity.Null && buff.Has<ModifyTeamBuff>(em)) return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: BuffBuffer walk of " + AiRuntime.Fmt(unit) + " threw: " + e.Message);
        }
        return false;
    }

    /// <summary>The entity's team, or null when it lacks <c>Team</c> or <c>TeamReference</c>.</summary>
    internal static UnitTeamState? Read(EntityManager em, Entity entity)
    {
        byte* team = AiRuntime.RO<Team>(em, entity, TeamExtent);
        byte* reference = AiRuntime.RO<TeamReference>(em, entity, ReferenceExtent);
        if (team == null || reference == null) return null;
        var refEntity = AiRuntime.ReadEntity(reference + ReferenceOffset);
        return new UnitTeamState(
            *(int*)(team + Layout.TeamValueOffset),
            *(int*)(team + Layout.TeamFactionIndexOffset),
            UnitId.From(refEntity));
    }

    private static void Write(EntityManager em, Entity unit, UnitTeamState character)
    {
        var current = Read(em, unit) ?? throw new InvalidOperationException("the team of unit " + AiRuntime.Fmt(unit) + " could not be read.");
        var wanted = UnitTeamRules.Joined(current, character);
        byte* team = AiRuntime.RW<Team>(em, unit, TeamExtent);
        *(int*)(team + Layout.TeamValueOffset) = wanted.Value;
        byte* reference = AiRuntime.RW<TeamReference>(em, unit, ReferenceExtent);
        AiRuntime.WriteEntity(reference + ReferenceOffset, wanted.Reference.ToEntity());
    }

    private static void ScheduleChecks(IUnitHandle handle, Entity unit, Entity character, object? owner)
    {
        int epoch = WorldEpoch.Current;
        foreach (double delay in Rechecks)
        {
            double d = delay;
            Dev.MainThread.PostDelayed(d, () =>
            {
                try
                {
                    if (handle.IsDisposed || !handle.Enabled || WorldEpoch.IsStale(epoch)) return;
                    var world = Worlds.Server;
                    if (world == null || !world.IsCreated) return;
                    var em = world.EntityManager;
                    if (!em.Exists(unit) || !em.Exists(character)) return;
                    var target = Read(em, character);
                    var now = Read(em, unit);
                    if (target == null || now == null || UnitTeamRules.IsOnTeamOf(now.Value, target.Value)) return;
                    if (HasTeamModifyingBuff(em, unit))
                    {
                        Plugin.Logger?.LogWarning("Units: team of " + handle.Entity + " changed " + d + " s after JoinTeam while a ModifyTeamBuff buff is on it; that buff owns the team, not written again.");
                        return;
                    }
                    Plugin.Logger?.LogWarning("Units: team of " + handle.Entity + " changed " + d + " s after JoinTeam (Team.Value " + now.Value.Value + "); writing the team of " + AiRuntime.Fmt(character) + " again.");
                    Write(em, unit, target.Value);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Units: team re-check threw: " + e.Message);
                }
            }, owner);
        }
    }
}
#endif
