using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Ecs;
using Progenitor.GodMode;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Buffs;

/// <summary>
/// Server debug protection: invulnerable, sun-immune, hazard-immune, no blood drain, ignored
/// by foes, <c>Immortal</c>, and the kill-path guard. The buff is Core's debug buff (<c>progenitor.debug</c>) when
/// that clone is registered, otherwise <c>Admin_Invulnerable_Buff</c> plus
/// <c>AB_Blood_BloodRite_SpellMod_Stealth</c>. Client calls throw.
///
/// <para>Ownership: only buffs this class applied are ever removed, and never one Core's
/// <c>.debug</c> claims. The decisions are <see cref="DebugProtectionPolicy"/> (game-free,
/// tested).</para>
/// </summary>
public static class DebugProtection
{
    /// <summary>Core's debug buff (the <c>.debug</c> command's), applied when its clone is registered.</summary>
    public const string DebugBuffName = Progenitor.Debugging.DebugBuff.Id;
    public const string InvulnerableBuffName = "Admin_Invulnerable_Buff";
    public const string NoAggroBuffName = "AB_Blood_BloodRite_SpellMod_Stealth";

    /// <summary>Guard owner for the debug bridge <c>protect</c> op and the owner-less overloads.</summary>
    private static readonly object Owner = new();
    private static readonly Dictionary<string, bool> ImmortalWasAlreadyOn = new();

    // Owners currently holding debug protection per character (world epoch + id), with the mode
    // each asked for. Only these decide whether the shared buffs and the Immortal grant stay: an
    // unrelated god mode guard (ctx.GodMode, .godmode) never removes them, so it must not keep
    // them alive either.
    private static readonly Dictionary<string, Dictionary<object, DebugProtectionMode>> Holders = new();

    // Buffs this class applied itself, per character. The only ones it ever removes: a buff
    // Core's .debug, an admin or anything else put on is not ours to take off (2026-10-07 facade
    // smoke: KeepAlive stripped Core's .debug buff and its watchdog forgot the grant).
    private static readonly Dictionary<string, DebugProtectionBuffs> Applied = new();

    /// <summary>Apply the protection to a player character or user. Idempotent.</summary>
    public static DebugProtectionResult Protect(World world, Entity target) => Protect(world, target, Owner);

    /// <summary>Apply the protection on behalf of <paramref name="owner"/>. Idempotent.</summary>
    public static DebugProtectionResult Protect(World world, Entity target, object owner)
    {
        var character = RequireCharacter(world, target, out var user);
        bool grantedImmortal = EnsureImmortal(world.EntityManager, character);
        bool guarded = GodMode.GodMode.Add(character, owner);
        Hold(character, owner, DebugProtectionMode.Full);
        var outcome = Converge(world, user, character, Effective(character));
        return Read(world, character, outcome, DebugProtectionMode.Full, grantedImmortal, guarded);
    }

    /// <summary>
    /// Keep this player from dying without making foes ignore her. Sets <c>Immortal</c>, the
    /// kill-path guard, and invulnerability (<c>Admin_Invulnerable_Buff</c>, unless a kept debug
    /// buff already covers it). Takes off the no-drain and stealth buffs only when this class put
    /// them on and no other holder still wants them, and sets <c>Aggroable</c> only when no
    /// foe-ignoring buff stays. A debug buff Core's <c>.debug</c> (or anyone else) owns is left
    /// alone; the result then reports <c>IgnoredByFoes</c> and <c>KeptOtherOwnersBuff</c>.
    /// <c>Immortal</c> alone does not stop the downed screen.
    /// </summary>
    public static DebugProtectionResult KeepAlive(World world, Entity target) => KeepAlive(world, target, Owner);

    /// <summary>Block death on behalf of <paramref name="owner"/> and leave the player targetable when that is ours to decide.</summary>
    public static DebugProtectionResult KeepAlive(World world, Entity target, object owner)
    {
        var character = RequireCharacter(world, target, out var user);
        bool grantedImmortal = EnsureImmortal(world.EntityManager, character);
        bool guarded = GodMode.GodMode.Add(character, owner);
        Hold(character, owner, DebugProtectionMode.AliveOnly);
        var outcome = Converge(world, user, character, Effective(character));
        return Read(world, character, outcome, DebugProtectionMode.AliveOnly, grantedImmortal, guarded);
    }

    /// <summary>Take off the protection this call granted. A pre-existing <c>Immortal</c> stays.</summary>
    public static DebugProtectionResult Release(World world, Entity target) => Release(world, target, Owner);

    /// <summary>
    /// Drop <paramref name="owner"/>'s hold. What is left converges on the strongest mode the
    /// remaining holders ask for; with none left, every buff this class applied comes off and so
    /// does an <c>Immortal</c> it granted. Never removes a buff it did not apply, and leaves
    /// <c>Immortal</c> to Core while Core's <c>.debug</c> holds a grant.
    /// </summary>
    public static DebugProtectionResult Release(World world, Entity target, object owner)
    {
        var character = RequireCharacter(world, target, out var user);
        GodMode.GodMode.Remove(character, owner);
        Unhold(character, owner);
        var mode = Effective(character);
        var outcome = Converge(world, user, character, mode);
        if (mode == DebugProtectionMode.None)
        {
            if (outcome.Plan.MayClearImmortal)
                ClearImmortalIfWeGranted(world.EntityManager, character);
            else
                ImmortalWasAlreadyOn.Remove(Key(character));
        }
        return Read(world, character, outcome, DebugProtectionMode.None, immortalGranted: false, guarded: false);
    }

    private readonly struct Outcome
    {
        internal Outcome(DebugProtectionPlan plan, DebugProtectionBuffs after, DebugProtectionBuffs removed,
            DebugProtectionBuffs applied, DebugProtectionBuffs leftover)
        {
            Plan = plan;
            After = after;
            Removed = removed;
            Applied = applied;
            Leftover = leftover;
        }

        internal DebugProtectionPlan Plan { get; }
        internal DebugProtectionBuffs After { get; }
        internal DebugProtectionBuffs Removed { get; }
        internal DebugProtectionBuffs Applied { get; }
        internal DebugProtectionBuffs Leftover { get; }
    }

    /// <summary>
    /// Bring the character's buffs to what <paramref name="mode"/> needs, touching only what
    /// <see cref="DebugProtectionPolicy.Plan"/> allows.
    /// </summary>
    private static Outcome Converge(World world, Entity user, Entity character, DebugProtectionMode mode)
    {
        var em = world.EntityManager;
        string key = Key(character);
        var present = Present(world, character);
        Applied.TryGetValue(key, out var ours);
        bool registered = TryGuid(world, DebugBuffName, out _);
        bool coreOwns = Progenitor.Debugging.DebugBuff.CoreHoldsGrant(em, character);

        var plan = DebugProtectionPolicy.Plan(mode, present, ours, coreOwns, registered);

        var removed = DebugProtectionBuffs.None;
        foreach (var flag in Each(plan.Remove))
            if (RemoveBuff(world, character, NameOf(flag))) removed |= flag;

        var applied = DebugProtectionBuffs.None;
        foreach (var flag in Each(plan.Apply))
            if (ApplyNamedBuff(world, user, character, NameOf(flag))) applied |= flag;

        var oursAfter = DebugProtectionPolicy.OursAfter(ours, present, removed, applied);
        if (oursAfter == DebugProtectionBuffs.None) Applied.Remove(key);
        else Applied[key] = oursAfter;

        if (mode == DebugProtectionMode.AliveOnly && plan.SetTargetable)
            EnsureTargetable(em, character);

        var after = DebugProtectionPolicy.After(Present(world, character), removed, DebugProtectionBuffs.None);
        return new Outcome(plan, after, removed, applied, plan.Remove & ~removed);
    }

    private static IEnumerable<DebugProtectionBuffs> Each(DebugProtectionBuffs set)
    {
        if ((set & DebugProtectionBuffs.Debug) != 0) yield return DebugProtectionBuffs.Debug;
        if ((set & DebugProtectionBuffs.Invulnerable) != 0) yield return DebugProtectionBuffs.Invulnerable;
        if ((set & DebugProtectionBuffs.NoAggro) != 0) yield return DebugProtectionBuffs.NoAggro;
    }

    private static string NameOf(DebugProtectionBuffs flag) => flag switch
    {
        DebugProtectionBuffs.Debug => DebugBuffName,
        DebugProtectionBuffs.Invulnerable => InvulnerableBuffName,
        _ => NoAggroBuffName,
    };

    private static DebugProtectionBuffs Present(World world, Entity character)
    {
        var present = DebugProtectionBuffs.None;
        if (Wearing(world, character, DebugBuffName)) present |= DebugProtectionBuffs.Debug;
        if (Wearing(world, character, InvulnerableBuffName)) present |= DebugProtectionBuffs.Invulnerable;
        if (Wearing(world, character, NoAggroBuffName)) present |= DebugProtectionBuffs.NoAggro;
        return present;
    }

    private static unsafe void EnsureTargetable(EntityManager em, Entity character)
    {
        var type = ComponentIndex.Of<Aggroable>(ComponentType.AccessMode.ReadWrite);
        if (!em.HasComponent(character, type)) return;
        byte* data = (byte*)em.GetComponentDataRawRW(character, type.TypeIndex);
        if (data == null) return;
        // Aggroable.Value is ModifiableBool at 0x8; _Value is its first byte.
        data[8] = 1;
    }

    private static bool ApplyNamedBuff(World world, Entity user, Entity character, string name)
    {
        if (!TryGuid(world, name, out var guid))
        {
            Plugin.Logger?.LogWarning("DebugProtection: " + name + " is not registered");
            return false;
        }
        var em = world.EntityManager;
        if (BuffAlready(em, character, guid)) return false;
        var des = world.GetExistingSystemManaged<DebugEventsSystem>();
        if (des == null)
            throw new InvalidOperationException("DebugEventsSystem is not in the world yet");
        des.ApplyBuff(
            new FromCharacter { User = user, Character = character },
            new ApplyBuffDebugEvent { BuffPrefabGUID = guid, Stacks = 1 });
        // ApplyBuff adds the BuffBuffer row before it returns; only a buff that is really there
        // is recorded as ours.
        return BuffAlready(em, character, guid);
    }

    /// <summary>Destroy the instance (lands next frame). True when a destroy was issued.</summary>
    private static bool RemoveBuff(World world, Entity character, string name)
    {
        if (!TryGuid(world, name, out var guid)) return false;
        var em = world.EntityManager;
        if (!BuffAlready(em, character, guid, out var instance)) return false;
        DestroyUtility.Destroy(em, instance, DestroyDebugReason.TryRemoveBuff);
        return true;
    }

    private static void Hold(Entity character, object owner, DebugProtectionMode mode)
    {
        string key = Key(character);
        if (!Holders.TryGetValue(key, out var set))
            Holders[key] = set = new Dictionary<object, DebugProtectionMode>(ReferenceEqualityComparer.Instance);
        set[owner] = mode;
    }

    /// <summary>
    /// True while any debug protection grant (the bridge <c>protect</c> op, a plugin's Protect or
    /// KeepAlive) holds <paramref name="character"/>; both modes rely on <c>Immortal</c>, so an
    /// explicit <c>.debug off</c> leaves the component to them.
    /// </summary>
    internal static bool HoldsImmortal(Entity character) => Effective(character) != DebugProtectionMode.None;

    /// <summary>The strongest mode any holder still asks for on <paramref name="character"/>.</summary>
    private static DebugProtectionMode Effective(Entity character) =>
        Holders.TryGetValue(Key(character), out var set)
            ? DebugProtectionPolicy.Strongest(set.Values)
            : DebugProtectionMode.None;

    /// <summary>
    /// Unload sweep: drop every holder entry that belongs to <paramref name="plugin"/> (the plugin
    /// instance itself or a <see cref="GodModeGuard.GodModeOwner"/> made for it), whatever
    /// character or world epoch it was recorded under, so no unloaded generation stays reachable.
    /// </summary>
    public static int Forget(object plugin)
    {
        if (plugin == null) return 0;
        int removed = 0;
        var emptied = new List<string>();
        foreach (var (key, set) in Holders)
        {
            var doomed = new List<object>();
            foreach (var o in set.Keys)
            {
                if (ReferenceEquals(o, plugin)
                    || (o is GodModeGuard.GodModeOwner g && ReferenceEquals(g.Plugin, plugin)))
                    doomed.Add(o);
            }
            foreach (var o in doomed) set.Remove(o);
            removed += doomed.Count;
            if (set.Count == 0) emptied.Add(key);
        }
        foreach (var key in emptied) Holders.Remove(key);
        return removed;
    }

    /// <summary>Drop <paramref name="owner"/>; returns how many debug holders remain.</summary>
    private static int Unhold(Entity character, object owner)
    {
        string key = Key(character);
        if (!Holders.TryGetValue(key, out var set)) return 0;
        set.Remove(owner);
        if (set.Count == 0) Holders.Remove(key);
        return set.Count;
    }

    private static bool EnsureImmortal(EntityManager em, Entity character)
    {
        string key = Key(character);
        bool had = character.Has<Immortal>(em);
        bool wasOn = false;
        if (had && character.TryRead(em, out Immortal current))
            wasOn = current.IsImmortal;
        if (!ImmortalWasAlreadyOn.ContainsKey(key))
            ImmortalWasAlreadyOn[key] = wasOn;
        if (!had)
            character.Add<Immortal>(em);
        var immortal = new Immortal { IsImmortal = true };
        character.Write(em, in immortal);
        return !wasOn;
    }

    /// <summary>
    /// An explicit <c>.debug off</c> kept <c>Immortal</c> because this class's grant holds the
    /// character: from now on the component is ours to clear, so the grant's release makes the
    /// player mortal instead of leaving an Immortal nobody owns.
    /// </summary>
    internal static void AdoptImmortal(Entity character)
    {
        string key = Key(character);
        if (ImmortalWasAlreadyOn.ContainsKey(key))
            ImmortalWasAlreadyOn[key] = false;
    }

    private static void ClearImmortalIfWeGranted(EntityManager em, Entity character)
    {
        string key = Key(character);
        if (!ImmortalWasAlreadyOn.TryGetValue(key, out bool already) || already)
        {
            ImmortalWasAlreadyOn.Remove(key);
            return;
        }
        ImmortalWasAlreadyOn.Remove(key);
        if (!em.Exists(character) || !character.Has<Immortal>(em)) return;
        var off = new Immortal { IsImmortal = false };
        character.Write(em, in off);
    }

    private static DebugProtectionResult Read(
        World world, Entity character, Outcome outcome, DebugProtectionMode request, bool immortalGranted, bool guarded)
    {
        var em = world.EntityManager;
        bool immortal = character.Has<Immortal>(em)
            && character.TryRead(em, out Immortal flag)
            && flag.IsImmortal;
        bool aggroable = true;
        if (character.TryRead(em, out Aggroable aggro))
            aggroable = aggro.Value.Value;
        var after = outcome.After;
        bool ok = DebugProtectionPolicy.Ok(request, immortal, after, aggroable, outcome.Leftover);
        bool keptOther = request == DebugProtectionMode.AliveOnly
            && (after & (DebugProtectionBuffs.Debug | DebugProtectionBuffs.NoAggro)) != 0;
        return new DebugProtectionResult(
            ok,
            immortal,
            GodMode.GodMode.IsGuarded(character) || guarded,
            DebugProtectionPolicy.IgnoredByFoes(after, aggroable),
            DebugProtectionPolicy.Invulnerable(after),
            immortalGranted,
            pending: outcome.Removed != DebugProtectionBuffs.None,
            keptOtherOwnersBuff: keptOther);
    }

    private static bool Wearing(World world, Entity character, string name) =>
        TryGuid(world, name, out var guid) && BuffAlready(world.EntityManager, character, guid);

    private static bool BuffAlready(EntityManager em, Entity character, PrefabGUID guid) =>
        BuffAlready(em, character, guid, out _);

    private static bool BuffAlready(EntityManager em, Entity character, PrefabGUID guid, out Entity instance)
    {
        instance = Entity.Null;
        try
        {
            var type = ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(character, type)) return false;
            var buffer = em.GetBuffer<BuffBuffer>(character, true);
            int hash = guid._Value;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].PrefabGuid._Value != hash) continue;
                instance = buffer[i].Entity;
                return instance != Entity.Null;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("DebugProtection: BuffBuffer walk failed: " + e.Message);
        }
        return false;
    }

    private static bool TryGuid(World world, string name, out PrefabGUID guid) =>
        PrefabLookup.TryGetGuidByName(world, name, out guid);

    private static Entity RequireCharacter(World world, Entity target, out Entity user)
    {
        if (!Plugin.IsServerProcess)
            throw new InvalidOperationException("debug protection runs on the server side only");
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("the game world does not exist yet (not in a game)");
        var em = world.EntityManager;
        if (!em.Exists(target))
            throw new InvalidOperationException("entity " + Key(target) + " does not exist");
        if (em.HasComponent<PlayerCharacter>(target))
        {
            user = em.GetComponentData<PlayerCharacter>(target).UserEntity;
            return target;
        }
        if (em.HasComponent<User>(target))
        {
            user = target;
            var character = em.GetComponentData<User>(target).LocalCharacter._Entity;
            if (!em.Exists(character))
                throw new InvalidOperationException("user " + Key(target) + " has no character");
            return character;
        }
        throw new InvalidOperationException("entity " + Key(target) + " is not a player");
    }

    // The Immortal record is process memory keyed by world epoch, so a raw index:version from
    // an earlier world load can never name a different character here.
    private static string Key(Entity entity) =>
        WorldEpoch.Current.ToString(CultureInfo.InvariantCulture) + "/"
        + entity.Index.ToString(CultureInfo.InvariantCulture) + ":" + entity.Version.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What <see cref="DebugProtection.Protect"/> or <see cref="DebugProtection.Release"/> left on the character.</summary>
public readonly struct DebugProtectionResult
{
    public DebugProtectionResult(bool ok, bool immortal, bool guarded, bool ignoredByFoes, bool invulnerable, bool immortalGranted)
        : this(ok, immortal, guarded, ignoredByFoes, invulnerable, immortalGranted, pending: false, keptOtherOwnersBuff: false)
    {
    }

    public DebugProtectionResult(
        bool ok, bool immortal, bool guarded, bool ignoredByFoes, bool invulnerable, bool immortalGranted,
        bool pending, bool keptOtherOwnersBuff)
    {
        Ok = ok;
        Immortal = immortal;
        Guarded = guarded;
        IgnoredByFoes = ignoredByFoes;
        Invulnerable = invulnerable;
        ImmortalGranted = immortalGranted;
        Pending = pending;
        KeptOtherOwnersBuff = keptOtherOwnersBuff;
    }

    /// <summary>
    /// The request holds once pending changes land. Buff removal is deferred to the next frame;
    /// <see cref="Ok"/>, <see cref="IgnoredByFoes"/> and <see cref="Invulnerable"/> already count
    /// it as done (see <see cref="Pending"/>).
    /// </summary>
    public bool Ok { get; }
    public bool Immortal { get; }
    public bool Guarded { get; }
    public bool IgnoredByFoes { get; }
    public bool Invulnerable { get; }
    public bool ImmortalGranted { get; }

    /// <summary>A buff removal was issued and lands next frame; the flags describe the state after it.</summary>
    public bool Pending { get; }

    /// <summary>
    /// <c>KeepAlive</c> left a foe-ignoring buff on because someone else owns or still wants it
    /// (Core's <c>.debug</c>, an admin, another holder's <c>Protect</c>).
    /// </summary>
    public bool KeptOtherOwnersBuff { get; }
}
