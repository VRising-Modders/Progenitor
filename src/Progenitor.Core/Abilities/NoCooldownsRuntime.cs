using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Abilities;

/// <summary>
/// The interop half of <c>ctx.Abilities.NoCooldowns</c>. One <see cref="Plugin.Frame"/>
/// subscription, armed while any character is held and dropped when the last hold goes.
///
/// <para><b>Every <see cref="AbilityCooldownPolicy.ZeroIntervalSeconds"/></b> it zeroes
/// <c>AbilityCooldownState</c> (<c>CooldownEndTime</c>, <c>CurrentCooldown</c>) on each cached
/// ability entity the held character owns and tops <c>AbilityChargesState.CurrentCharges</c> up to
/// <c>AbilityChargesData.MaxCharges</c>. A field already at its target is not written, so an idle
/// ability does not churn its change version.</para>
///
/// <para><b>Owned-entity lists are rebuilt on events, not on a timer</b>
/// (<see cref="AbilityCooldownPolicy.NeedsRebuild"/>): when a hold is taken, when a held
/// character's <c>CastStartedCounter</c> advances (a cast can create a new ability entity), and
/// once more <see cref="AbilityCooldownPolicy.FollowUpRebuildSeconds"/> after that. A rebuild is one
/// query over <c>EntityOwner</c> + <c>AbilityCooldownState</c> and one over <c>EntityOwner</c> +
/// <c>AbilityChargesState</c> + <c>AbilityChargesData</c>, shared by all held characters; the
/// owner is read raw from the query's own guaranteed component with the type index resolved once
/// per rebuild. A failed rebuild keeps the previous lists and logs once.</para>
///
/// <para><b>Release.</b> A hold ends when disposed, when its plugin unloads (the hold is
/// <c>ctx.Track</c>ed), when the character no longer exists, when the world epoch
/// (<see cref="WorldEpoch.Current"/>) differs from the one it was taken in, and on Core uninstall.
/// Nothing is restored: the game's cooldown systems start counting again from the next cast.</para>
///
/// <para>Component reads and writes go through <see cref="AiRuntime.RO{T}"/> /
/// <see cref="AiRuntime.RW{T}"/> at <c>layout.txt</c> offsets; queries are built with
/// <see cref="Query.Build"/>. No generic interop instantiation over a game value type is
/// introduced (<c>ToEntityArray</c> is the <c>NativeArray&lt;Entity&gt;</c> Query already uses).</para>
/// </summary>
internal static unsafe class NoCooldownsRuntime
{
    /// <summary>One plugin's claim on one character. Disposing it releases that claim only.</summary>
    internal sealed class Hold : IDisposable
    {
        internal readonly object Owner;
        internal readonly long Key;
        internal readonly Entity Character;
        internal bool Released;

        internal Hold(object owner, long key, Entity character)
        {
            Owner = owner;
            Key = key;
            Character = character;
        }

        public void Dispose() => Release(this);
    }

    /// <summary>A held character and the ability entities it owns, as of the last rebuild.</summary>
    private sealed class Entry
    {
        internal int Epoch;
        internal Entity Character;
        internal int LastCounter = -1;
        internal readonly List<Entity> Cooldowns = new();
        internal readonly List<Entity> Charges = new();
    }

    private static readonly object Gate = new();
    private static readonly AbilityCooldownPolicy.Holds<Hold> Holds = new();
    private static readonly Dictionary<long, Entry> Entries = new();
    private static bool _hooked;
    private static bool _needsBuild;
    private static long _lastZeroMs = AbilityCooldownPolicy.Never;
    private static long _followUpAtMs = AbilityCooldownPolicy.Never;
    private static int _failuresLogged;
    private static bool _rebuildFailureLogged;

    private const int MaxFailureLogs = 8;

    /// <summary>
    /// Start (or add a holder to) no-cooldowns on <paramref name="character"/>. Returns null with
    /// <paramref name="why"/> when the world is not ready or the character does not exist.
    /// Main thread.
    /// </summary>
    internal static Hold? Take(World? world, Entity character, object owner, out string why)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (world == null || !world.IsCreated)
        {
            why = "no-cooldowns refused: the server world is not ready.";
            return null;
        }
        if (character == Entity.Null || !world.EntityManager.Exists(character))
        {
            why = "no-cooldowns refused: character " + character.Index + ":" + character.Version + " does not exist.";
            return null;
        }

        int epoch = WorldEpoch.Current;
        long key = AbilityCooldownPolicy.Key(character.Index, character.Version);

        // An entry from an earlier world epoch names a different entity now; its holders are
        // stale. Drop them before this hold starts a fresh entry.
        bool stale;
        lock (Gate)
            stale = Entries.TryGetValue(key, out var old) && !AbilityCooldownPolicy.SameEpoch(old.Epoch, epoch);
        if (stale) DropCharacter(key, character, "replaced: taken again in a new world epoch");

        var hold = new Hold(owner, key, character);
        lock (Gate)
        {
            if (Holds.Add(key, hold))
            {
                Entries[key] = new Entry { Epoch = epoch, Character = character };
                _needsBuild = true;
                _lastZeroMs = AbilityCooldownPolicy.Never;
                Plugin.Logger?.LogInfo(AbilityCooldownPolicy.FormatHold(character.Index, character.Version));
            }
            Hook();
        }
        why = "";
        return hold;
    }

    /// <summary>True when <paramref name="owner"/> holds no-cooldowns on <paramref name="character"/>.</summary>
    internal static bool IsHeldBy(Entity character, object owner)
    {
        long key = AbilityCooldownPolicy.Key(character.Index, character.Version);
        lock (Gate) return Holds.IsHeldBy(key, h => ReferenceEquals(h.Owner, owner));
    }

    private static void Release(Hold hold)
    {
        lock (Gate)
        {
            if (hold.Released) return;
            hold.Released = true;
            if (Holds.Remove(hold.Key, hold))
            {
                Entries.Remove(hold.Key);
                Plugin.Logger?.LogInfo(AbilityCooldownPolicy.FormatRelease(hold.Character.Index, hold.Character.Version, "last holder released"));
            }
            if (Holds.CharacterCount == 0) Unhook();
        }
    }

    /// <summary>Drop one character entirely (it is gone, or its world epoch is).</summary>
    private static void DropCharacter(long key, Entity character, string why)
    {
        lock (Gate)
        {
            foreach (var h in Holds.Drop(key)) h.Released = true;
            Entries.Remove(key);
            if (Holds.CharacterCount == 0) Unhook();
        }
        Plugin.Logger?.LogInfo(AbilityCooldownPolicy.FormatRelease(character.Index, character.Version, why));
    }

    /// <summary>Core uninstall: release every hold and drop the frame subscription.</summary>
    internal static void Uninstall()
    {
        lock (Gate)
        {
            foreach (var h in Holds.DropAll()) h.Released = true;
            Entries.Clear();
            Unhook();
            _lastZeroMs = AbilityCooldownPolicy.Never;
            _followUpAtMs = AbilityCooldownPolicy.Never;
            _needsBuild = false;
        }
    }

    private static void Hook()
    {
        if (_hooked) return;
        Plugin.Frame += Tick;
        _hooked = true;
    }

    private static void Unhook()
    {
        if (!_hooked) return;
        try { Plugin.Frame -= Tick; } catch { /* Frame may already be gone on uninstall */ }
        _hooked = false;
    }

    // ---- the frame tick ------------------------------------------------------------------------

    private static void Tick()
    {
        long now = Environment.TickCount64;
        Entry[] entries;
        bool neverBuilt;
        lock (Gate)
        {
            if (!AbilityCooldownPolicy.Due(now, _lastZeroMs, AbilityCooldownPolicy.ZeroIntervalSeconds)) return;
            _lastZeroMs = now;
            neverBuilt = _needsBuild;
            entries = new Entry[Entries.Count];
            Entries.Values.CopyTo(entries, 0);
        }
        if (entries.Length == 0) return;

        try
        {
            var world = Worlds.Server;
            int epoch = WorldEpoch.Current;
            var live = new List<Entry>(entries.Length);
            bool advanced = false;
            foreach (var entry in entries)
            {
                long key = AbilityCooldownPolicy.Key(entry.Character.Index, entry.Character.Version);
                if (world == null || !world.IsCreated || !AbilityCooldownPolicy.SameEpoch(entry.Epoch, epoch))
                {
                    DropCharacter(key, entry.Character, "the world epoch it was taken in has ended");
                    continue;
                }
                if (!world.EntityManager.Exists(entry.Character))
                {
                    DropCharacter(key, entry.Character, "character no longer exists");
                    continue;
                }
                int counter = AbilityCast.CastStartedCounter(world, entry.Character);
                if (AbilityCooldownPolicy.CounterAdvanced(entry.LastCounter, counter))
                {
                    advanced = true;
                    entry.LastCounter = counter;
                }
                live.Add(entry);
            }
            if (live.Count == 0 || world == null) return;

            var em = world.EntityManager;
            if (AbilityCooldownPolicy.NeedsRebuild(neverBuilt, advanced, now, _followUpAtMs))
            {
                // A cast (or a new hold) schedules exactly one follow-up; the follow-up itself does not.
                bool isFollowUp = !neverBuilt && !advanced;
                // A failed rebuild is not retried every tick (that would be a world scan at 10 Hz);
                // the next cast or hold triggers the next attempt.
                Rebuild(em, live);
                lock (Gate) _needsBuild = false;
                _followUpAtMs = isFollowUp
                    ? AbilityCooldownPolicy.Never
                    : now + (long)(AbilityCooldownPolicy.FollowUpRebuildSeconds * 1000.0);
            }
            foreach (var entry in live) Zero(em, entry);
        }
        catch (Exception e)
        {
            LogFailure("tick threw: " + e.Message);
        }
    }

    /// <summary>
    /// Rebuild both lists for every live entry. On any failure the previous lists are left as
    /// they were (the hold keeps working on what it knew) and the failure is logged once.
    /// </summary>
    private static bool Rebuild(EntityManager em, List<Entry> live)
    {
        var byKey = new Dictionary<long, Entry>(live.Count);
        foreach (var entry in live)
            byKey[AbilityCooldownPolicy.Key(entry.Character.Index, entry.Character.Version)] = entry;

        var cooldowns = new Dictionary<long, List<Entity>>();
        var charges = new Dictionary<long, List<Entity>>();
        try
        {
            var ownerType = ComponentIndex.Of<EntityOwner>(ComponentType.AccessMode.ReadOnly);
            int need = Layout.EntityOwnerOwnerOffset + 2 * sizeof(int);
            int size = TypeSizeOrZero(ownerType.TypeIndex);
            if (size > 0 && size < need)
                throw new InvalidOperationException("EntityOwner TypeSize " + size + " is smaller than the Owner extent " + need + ".");

            Collect(em, ownerType, byKey, cooldowns,
                ownerType, ComponentIndex.Of<AbilityCooldownState>(ComponentType.AccessMode.ReadOnly));
            Collect(em, ownerType, byKey, charges,
                ownerType,
                ComponentIndex.Of<AbilityChargesState>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<AbilityChargesData>(ComponentType.AccessMode.ReadOnly));
        }
        catch (Exception e)
        {
            if (!_rebuildFailureLogged)
            {
                _rebuildFailureLogged = true;
                Plugin.Logger?.LogWarning(AbilityCooldownPolicy.FormatRebuildFailed(e.Message));
            }
            return false;
        }

        foreach (var pair in byKey)
        {
            var entry = pair.Value;
            entry.Cooldowns.Clear();
            entry.Charges.Clear();
            if (cooldowns.TryGetValue(pair.Key, out var c)) entry.Cooldowns.AddRange(c);
            if (charges.TryGetValue(pair.Key, out var h)) entry.Charges.AddRange(h);
        }
        return true;
    }

    /// <summary>
    /// Run one query and bucket its entities by held owner. Throws on failure (the caller keeps
    /// the previous lists). The owner is read raw: the query requires <c>EntityOwner</c>, so no
    /// per-entity Exists / HasComponent round trip is needed.
    /// </summary>
    private static void Collect(
        EntityManager em,
        ComponentType ownerType,
        Dictionary<long, Entry> byKey,
        Dictionary<long, List<Entity>> into,
        params ComponentType[] all)
    {
        var query = Query.Build(em, includeDisabled: true, includeSystems: false, all);
        try
        {
            var array = query.ToEntityArray(Allocator.Temp);
            try
            {
                var ownerIndex = ownerType.TypeIndex;
                for (int i = 0; i < array.Length; i++)
                {
                    var e = array[i];
                    byte* p = (byte*)em.GetComponentDataRawRO(e, ownerIndex);
                    if (p == null) continue;
                    var owner = AiRuntime.ReadEntity(p + Layout.EntityOwnerOwnerOffset);
                    long key = AbilityCooldownPolicy.Key(owner.Index, owner.Version);
                    if (!byKey.TryGetValue(key, out var entry) || entry.Character != owner) continue;
                    if (!into.TryGetValue(key, out var list))
                    {
                        list = new List<Entity>();
                        into[key] = list;
                    }
                    list.Add(e);
                }
            }
            finally
            {
                array.Dispose();
            }
        }
        finally
        {
            query.Dispose();
        }
    }

    private static int TypeSizeOrZero(TypeIndex index)
    {
        try { return TypeManager.GetTypeInfo(index).TypeSize; }
        catch { return 0; }
    }

    private static void Zero(EntityManager em, Entry entry)
    {
        const int cooldownExtent = Layout.AbilityCooldownStateCurrentOffset + sizeof(float); // the last byte poked
        foreach (var e in entry.Cooldowns)
        {
            try
            {
                byte* ro = AiRuntime.RO<AbilityCooldownState>(em, e, cooldownExtent);
                if (ro == null) continue; // gone since the rebuild; the next rebuild drops it
                double end = *(double*)(ro + Layout.AbilityCooldownStateEndTimeOffset);
                float current = *(float*)(ro + Layout.AbilityCooldownStateCurrentOffset);
                if (!AbilityCooldownPolicy.NeedsCooldownWrite(end, current)) continue;
                byte* rw = AiRuntime.RW<AbilityCooldownState>(em, e, cooldownExtent);
                *(double*)(rw + Layout.AbilityCooldownStateEndTimeOffset) = 0.0;
                *(float*)(rw + Layout.AbilityCooldownStateCurrentOffset) = 0f;
            }
            catch (Exception ex)
            {
                LogFailure("cooldown write on " + e.Index + ":" + e.Version + " threw: " + ex.Message);
            }
        }

        foreach (var e in entry.Charges)
        {
            try
            {
                byte* data = AiRuntime.RO<AbilityChargesData>(em, e, Layout.AbilityChargesDataMaxChargesOffset + sizeof(int));
                byte* state = AiRuntime.RO<AbilityChargesState>(em, e, Layout.AbilityChargesStateCurrentChargesOffset + sizeof(int));
                if (data == null || state == null) continue;
                int max = *(int*)(data + Layout.AbilityChargesDataMaxChargesOffset);
                int current = *(int*)(state + Layout.AbilityChargesStateCurrentChargesOffset);
                int target = AbilityCooldownPolicy.ChargesToWrite(current, max);
                if (target < 0) continue;
                byte* rw = AiRuntime.RW<AbilityChargesState>(em, e, Layout.AbilityChargesStateCurrentChargesOffset + sizeof(int));
                *(int*)(rw + Layout.AbilityChargesStateCurrentChargesOffset) = target;
            }
            catch (Exception ex)
            {
                LogFailure("charge write on " + e.Index + ":" + e.Version + " threw: " + ex.Message);
            }
        }
    }

    private static void LogFailure(string line)
    {
        if (_failuresLogged >= MaxFailureLogs) return;
        _failuresLogged++;
        Plugin.Logger?.LogWarning("Abilities: no-cooldowns " + line
            + (_failuresLogged == MaxFailureLogs ? " (further failures are not logged)" : ""));
    }
}
