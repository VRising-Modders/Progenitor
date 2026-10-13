using System;
using System.Collections.Generic;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using Progenitor.Abilities;
using Progenitor.Dev.Ops;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_cast</c>: make a bot use one of the abilities on its own bar (slot 0 = basic
/// attack, 1 = weapon skill Q, 2 = travel/dash, 3 = weapon skill E, then spells), the way pressing
/// the key would. Holding the synced button bit on the client does not start a cast (live
/// 2026-09-24: Primary held for 2.5 s, nothing happened), so this uses Core's
/// <see cref="AbilityCast"/> (<c>AbilityBar_Shared.ForceCastGroup</c>), which only casts a group the
/// character already owns. Aim comes from the
/// character's own <c>EntityInput</c>, which the client-side <c>bot</c> op sets before calling this.
///
/// <para>Args: <c>user</c> (character name) or <c>platformId</c>; <c>slot</c> (default 0) or
/// <c>group</c> (PrefabGUID hash); <c>action</c> <c>cast</c> (default) or <c>slots</c> (list the
/// bar). The result carries <c>castStartedCounter</c>; a cast happened when a later call reports a
/// larger counter (a written request alone proves nothing).</para>
/// </summary>
internal static class BotCast
{
    public static unsafe object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot_cast runs on the server");
        var em = world.EntityManager;
        var character = ResolveCharacter(em, args, out string name);
        string action = Str(args, "action")?.Trim().ToLowerInvariant() ?? "cast";
        var slots = ReadSlots(em, character);
        int counter = ReadCastCounter(em, character);
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();

        if (action == "vitals")
        {
            float? hp = null, maxHp = null; bool dead = false;
            if (em.HasComponent(character, ComponentIndex.Of<Health>(ComponentType.AccessMode.ReadOnly)))
            {
                var h = em.GetComponentData<Health>(character); // bool-bearing: typed accessor
                hp = h.Value; maxHp = h.MaxHealth.Value; dead = h.IsDead;
            }
            var (bloodType, bloodQuality) = ReadBloodKind(em, character, prefabs);
            return new { name, health = hp, maxHealth = maxHp, dead, blood = ReadBlood(em, character), bloodType, bloodQuality, sun = ReadSun(em, character), castStartedCounter = counter };
        }
        if (action == "slots" || action == "status")
        {
            var rows = new List<object>();
            var full = ReadSlotsFull(em, character);
            for (int i = 0; i < slots.Count; i++)
                rows.Add(new
                {
                    slot = i,
                    group = slots[i],
                    name = slots[i] != 0 && prefabs != null ? EntityOps.TryPrefabName(prefabs, new PrefabGUID(slots[i])) : null,
                    baseName = full[i].baseGroup != 0 && prefabs != null ? EntityOps.TryPrefabName(prefabs, new PrefabGUID(full[i].baseGroup)) : null,
                });
            return new { name, character = $"{character.Index}:{character.Version}", castStartedCounter = counter, slots = rows };
        }
        if (action != "cast") throw new ArgumentException("args.action must be cast, slots, status or vitals");

        int group;
        if (args.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.Number) group = g.GetInt32();
        else
        {
            int slot = args.TryGetProperty("slot", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
            if (slot < 0 || slot >= slots.Count) throw new ArgumentException($"slot {slot} is outside the bar (0..{slots.Count - 1})");
            group = slots[slot];
            if (group == 0) throw new InvalidOperationException($"slot {slot} is empty");
        }
        bool ok = AbilityCast.Cast(world, character, new PrefabGUID(group), out string why);
        return new
        {
            name,
            requested = ok,
            why,
            group,
            groupName = prefabs != null ? EntityOps.TryPrefabName(prefabs, new PrefabGUID(group)) : null,
            castStartedCounter = counter,
            note = "requested is not proof: call again with action=status and compare castStartedCounter",
        };
    }

    // AbilityGroupSlotBuffer: BaseAbilityGroupOnSlot PrefabGUID @0x0 and
    // GroupSlotEntity NetworkedEntity @0x8 (Entity at +0), stride from the metadata-generated Layout
    // constant. The slot entity's AbilityGroupSlot.GroupGuid (ModifiablePrefabGUID @0x0, value at +0)
    // is the CURRENT group after weapon modifications: with a sword equipped the base
    // stays the unarmed attack while the current group is the sword's. Casting uses the current one.
    private static unsafe List<int> ReadSlots(EntityManager em, Entity character) => ReadSlotsFull(em, character).ConvertAll(s => s.current != 0 ? s.current : s.baseGroup);

    private static unsafe List<(int baseGroup, int current)> ReadSlotsFull(EntityManager em, Entity character)
    {
        var result = new List<(int, int)>();
        var type = ComponentIndex.Of<AbilityGroupSlotBuffer>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(character, type)) return result;
        var slotType = ComponentIndex.Of<AbilityGroupSlot>(ComponentType.AccessMode.ReadOnly);
        int length = em.GetBufferLength(character, type.TypeIndex);
        byte* raw = (byte*)em.GetBufferRawRO(character, type.TypeIndex);
        if (raw == null) return result;
        for (int i = 0; i < length; i++)
        {
            byte* row = raw + i * Layout.AbilityGroupSlotStride;
            int baseGroup = *(int*)row;
            var slotEntity = *(Entity*)(row + 0x8);
            int current = 0;
            if (slotEntity != Entity.Null && em.Exists(slotEntity) && em.HasComponent(slotEntity, slotType))
            {
                byte* slot = (byte*)em.GetComponentDataRawRO(slotEntity, slotType.TypeIndex);
                if (slot != null) current = *(int*)slot;
            }
            result.Add((baseGroup, current));
        }
        return result;
    }

    // Blood.Value float @0x10 (server metadata); raw read, the component carries modifiable wrappers.
    private static unsafe float? ReadBlood(EntityManager em, Entity character)
    {
        var type = ComponentIndex.Of<Blood>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(character, type)) return null;
        byte* raw = (byte*)em.GetComponentDataRawRO(character, type.TypeIndex);
        return raw == null ? null : *(float*)(raw + 0x10);
    }

    // Blood.Quality float @0x18 and PrefabGUID BloodType @0x20: the blood the
    // bot carries now, which a feed replaces with the victim's type and quality. Layout-checked.
    private static unsafe (string? type, float? quality) ReadBloodKind(EntityManager em, Entity character, PrefabCollectionSystem? prefabs)
    {
        var type = ComponentIndex.Of<Blood>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(character, type)) return (null, null);
        if ((int)System.Runtime.InteropServices.Marshal.OffsetOf<Blood>(nameof(Blood.Quality)) != 0x18
            || (int)System.Runtime.InteropServices.Marshal.OffsetOf<Blood>(nameof(Blood.BloodType)) != 0x20) return (null, null);
        byte* raw = (byte*)em.GetComponentDataRawRO(character, type.TypeIndex);
        if (raw == null) return (null, null);
        int guid = *(int*)(raw + 0x20);
        return (prefabs == null ? guid.ToString() : EntityOps.TryPrefabName(prefabs, new PrefabGUID(guid)) ?? guid.ToString(), *(float*)(raw + 0x18));
    }

    // TakeDamageInSun (server metadata): ChargeValue f32 @0x04
    // (0..1, burns at 1), SunIntensity f32 @0x44, IsInSun bool @0x48 (set by CheckInSunSystem).
    // TakeDamageInSunDebuffState: Entity DebuffInstance @0x0, non-null while burning. Raw reads (bools).
    private static unsafe object? ReadSun(EntityManager em, Entity character)
    {
        var type = ComponentIndex.Of<TakeDamageInSun>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(character, type)) return null;
        byte* raw = (byte*)em.GetComponentDataRawRO(character, type.TypeIndex);
        if (raw == null) return null;
        bool burning = false;
        var dtype = ComponentIndex.Of<TakeDamageInSunDebuffState>(ComponentType.AccessMode.ReadOnly);
        if (em.HasComponent(character, dtype))
        {
            var instPtr = (Entity*)em.GetComponentDataRawRO(character, dtype.TypeIndex);
            if (instPtr != null) burning = *instPtr != Entity.Null && em.Exists(*instPtr);
        }
        // SunChargeUpTime (ModifiableFloat, _Value @0x10) and SunCooldownTime f32 @0x14: seconds of sun from
        // charge 0 to 1 (damage starts at 1) and of shade from 1 back to 0 (the game's native sun-damage code).
        // The bot budgets its exposure with them; null if the interop layout differs.
        float? chargeUpTime = null, cooldownTime = null;
        if ((int)System.Runtime.InteropServices.Marshal.OffsetOf<TakeDamageInSun>(nameof(TakeDamageInSun.SunChargeUpTime)) == 0x10
            && (int)System.Runtime.InteropServices.Marshal.OffsetOf<TakeDamageInSun>(nameof(TakeDamageInSun.SunCooldownTime)) == 0x14)
        { chargeUpTime = *(float*)(raw + 0x10); cooldownTime = *(float*)(raw + 0x14); }
        return new { inSun = raw[0x48] != 0, charge = *(float*)(raw + 0x04), intensity = *(float*)(raw + 0x44), burning, chargeUpTime, cooldownTime };
    }

    /// <summary>
    /// Server op <c>bot_daytime</c>: is it day, and for how long. <c>DayNightCycle</c> singleton (server
    /// metadata): Time f64 @0x00, DayDurationInSeconds f32 @0x14 (whole cycle),
    /// DurationBeforeSunDamageAfterSunrise f32 @0x3C, DayTimeStartInSeconds f32 @0x40,
    /// DayTimeDurationInSeconds f32 @0x44; the game's own getters are fmod over these.
    /// Raw read: the component carries bools.
    /// </summary>
    public static unsafe object Daytime(World world)
    {
        var em = world.EntityManager;
        var type = ComponentIndex.Of<DayNightCycle>(ComponentType.AccessMode.ReadOnly);
        var found = Query.Entities(em, includeDisabled: false, includeSystems: false, type);
        if (found.Count == 0) found = Query.Entities(em, includeDisabled: true, includeSystems: true, type);
        if (found.Count == 0) throw new InvalidOperationException("no DayNightCycle singleton");
        byte* raw = (byte*)em.GetComponentDataRawRO(found[0], type.TypeIndex);
        if (raw == null) throw new InvalidOperationException("DayNightCycle raw read returned null");
        double time = *(double*)raw;
        float cycle = *(float*)(raw + 0x14), grace = *(float*)(raw + 0x3C), dayStart = *(float*)(raw + 0x40), dayLen = *(float*)(raw + 0x44);
        double sinceMidnight = cycle > 0 ? time % cycle : 0;
        bool isDay = sinceMidnight >= dayStart && sinceMidnight < dayStart + dayLen;
        bool sunDamage = isDay && sinceMidnight >= dayStart + grace;
        double untilNight = isDay ? dayStart + dayLen - sinceMidnight : 0;
        double untilDay = isDay ? 0 : (sinceMidnight < dayStart ? dayStart - sinceMidnight : cycle - sinceMidnight + dayStart);
        return new { isDay, sunDamage, secondsUntilNight = Math.Round(untilNight, 1), secondsUntilDay = Math.Round(untilDay, 1), cycle, dayStart, dayLength = dayLen, sinceMidnight = Math.Round(sinceMidnight, 1), sunRotation = SunRotation(em) };
    }

    /// <summary>
    /// <c>ProjectM.Sun { float3 Rotation @0x0 }</c>, Euler degrees of the sun:
    /// shadows fall away from it, so a bot looks for shade on the far side of trees and walls. A
    /// singleton that may live on a system entity; null when this world has none.
    /// </summary>
    private static unsafe float[]? SunRotation(EntityManager em)
    {
        var type = ComponentIndex.Of<Sun>(ComponentType.AccessMode.ReadOnly);
        var found = Query.Entities(em, includeDisabled: true, includeSystems: true, type);
        if (found.Count == 0) return null;
        float* r = (float*)em.GetComponentDataRawRO(found[0], type.TypeIndex);
        return r == null ? null : new[] { r[0], r[1], r[2] };
    }

    private static unsafe int ReadCastCounter(EntityManager em, Entity character)
    {
        var type = ComponentIndex.Of<AbilityBar_Shared>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(character, type)) return -1;
        byte* bar = (byte*)em.GetComponentDataRawRO(character, type.TypeIndex);
        return bar == null ? -1 : *(int*)(bar + Layout.AbilityBarCastStartedCounterOffset);
    }

    internal static Entity ResolveCharacter(EntityManager em, JsonElement args, out string name)
    {
        ulong? platformId = null;
        string? wanted = Str(args, "user");
        if (args.TryGetProperty("platformId", out var p))
        {
            if (p.ValueKind == JsonValueKind.String && ulong.TryParse(p.GetString(), out var ps)) platformId = ps;
            else if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out var pn)) platformId = pn;
        }
        if (platformId == null && string.IsNullOrWhiteSpace(wanted))
            throw new ArgumentException("args.user (character name) or args.platformId is required");
        var users = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < users.Count; i++)
        {
            var u = em.GetComponentData<User>(users[i]);
            if ((platformId != null && u.PlatformId == platformId.Value)
                || (platformId == null && string.Equals(u.CharacterName.ToString(), wanted, StringComparison.OrdinalIgnoreCase)))
            {
                var c = u.LocalCharacter._Entity;
                if (c == Entity.Null || !em.Exists(c)) throw new InvalidOperationException($"'{u.CharacterName}' has no character");
                name = u.CharacterName.ToString();
                return c;
            }
        }
        throw new InvalidOperationException("no such user");
    }

    private static string? Str(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
