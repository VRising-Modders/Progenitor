using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProjectM;
using Progenitor.Dev.Ops;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_threats</c>: the hostile units around a bot as a player reads a fight, so a bot
/// can dodge a wind-up, time a counter and pick targets: name, level, health, distance, whether the
/// unit is aggroed on this bot, and what it is casting right now.
///
/// <para>Reads, all raw by server-metadata offset, each guarded by an interop layout check:</para>
/// <list type="bullet">
/// <item><description><c>AbilityBar_Shared</c>: <c>float CastTime @0x10</c>, <c>int CastStartedCounter
/// @0x28</c>, <c>AbilityCastingState ThisFrameState @0x44</c> (None 0, Interrupted 1, Casting 2,
/// PostCasting 3, Finished 4), <c>PrefabGUID CastGroupPrefabGuid @0x54</c>, <c>PrefabGUID CastAbilityPrefabGuid
/// @0x58</c>. The same fields the 2026-09-22 boss harness sampled to see attacks fire.</description></item>
/// <item><description><c>AggroConsumer</c>: <c>NetworkedEntity AggroTarget @0x30</c> (Entity at +0),
/// <c>float3 PreCombatPosition @0x18</c>, <c>float MaxDistanceFromPreCombatPosition @0x24</c> (the leash).</description></item>
/// <item><description><c>UnitLevel</c>: <c>ModifiableInt Level @0x0</c>.</description></item>
/// <item><description>Presence only: <c>BloodConsumeSource</c> marks a unit a vampire can feed on once it
/// is wounded (undead, golems and critters have none); <c>VBloodUnit</c> marks a boss, which is fed on
/// only once downed (the boss feed), never as a wounded unit.</description></item>
/// <item><description><c>BloodConsumeSource</c>: <c>float BloodQuality @0x8</c> (rolled per spawn),
/// <c>ModifiablePrefabGUID UnitBloodType @0xC</c> (<c>PrefabGUID _Value @0x0</c>), <c>bool CanBeConsumed @0x20</c>:
/// the blood a feed would give, so a bot can choose whether to hang on or finish.</description></item>
/// </list>
/// <para>Args: <c>user</c>, optional <c>radius</c> (default 30), <c>limit</c> (default 20).</para>
/// </summary>
internal static class BotThreats
{
    public static unsafe object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("bot_threats runs on the server");
        CheckLayout();
        var em = world.EntityManager;
        var character = BotCast.ResolveCharacter(em, args, out string name);
        float radius = args.TryGetProperty("radius", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetSingle() : 30f;
        int limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 20;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        if (!character.TryRead<Translation>(em, out var me)) throw new InvalidOperationException("bot has no Translation");

        var aggroType = ComponentIndex.Of<AggroConsumer>(ComponentType.AccessMode.ReadOnly);
        var barType = ComponentIndex.Of<AbilityBar_Shared>(ComponentType.AccessMode.ReadOnly);
        var levelType = ComponentIndex.Of<UnitLevel>(ComponentType.AccessMode.ReadOnly);
        var bloodType = ComponentIndex.Of<BloodConsumeSource>(ComponentType.AccessMode.ReadOnly);
        var vbloodType = ComponentIndex.Of<VBloodUnit>(ComponentType.AccessMode.ReadOnly);
        var units = Query.Entities(em, includeDisabled: false, includeSystems: false,
            aggroType, ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly));
        var rows = new List<(float d, object row)>();
        foreach (var e in units)
        {
            if (!e.TryRead<Translation>(em, out var tr)) continue;
            float d = math.distance(tr.Value.xz, me.Value.xz);
            if (d > radius) continue;
            if (em.HasComponent(e, ComponentIndex.Of<Health>(ComponentType.AccessMode.ReadOnly)))
            {
                var h = em.GetComponentData<Health>(e);
                if (h.IsDead || h.Value <= 0) continue;
            }
            byte* ag = (byte*)em.GetComponentDataRawRO(e, aggroType.TypeIndex);
            bool onMe = ag != null && *(int*)(ag + 0x30) == character.Index && *(int*)(ag + 0x34) == character.Version;
            object? cast = CastOf(em, e, barType, prefabs);
            int? level = null;
            if (em.HasComponent(e, levelType)) { byte* lv = (byte*)em.GetComponentDataRawRO(e, levelType.TypeIndex); if (lv != null) level = *(int*)lv; }
            e.TryRead<PrefabGUID>(em, out var guid);
            float hp = 0, max = 0;
            if (em.HasComponent(e, ComponentIndex.Of<Health>(ComponentType.AccessMode.ReadOnly)))
            {
                var h = em.GetComponentData<Health>(e);
                hp = h.Value; max = h.MaxHealth._Value;
            }
            rows.Add((d, new
            {
                entity = $"{e.Index}:{e.Version}",
                name = prefabs != null ? EntityOps.TryPrefabName(prefabs, guid) : null,
                level,
                health = hp,
                maxHealth = max,
                x = tr.Value.x, y = tr.Value.y, z = tr.Value.z,
                distance = MathF.Round(d, 2),
                onMe,
                vblood = em.HasComponent(e, vbloodType),
                feedable = em.HasComponent(e, bloodType) && !em.HasComponent(e, vbloodType) && BloodOf(em, e, bloodType, prefabs).canConsume,
                bloodType = BloodOf(em, e, bloodType, prefabs).type,
                bloodQuality = BloodOf(em, e, bloodType, prefabs).quality,
                leash = ag != null ? *(float*)(ag + 0x24) : 0f,
                cast,
            }));
        }
        rows.Sort((a, b) => a.d.CompareTo(b.d));
        var list = new List<object>();
        for (int i = 0; i < rows.Count && i < limit; i++) list.Add(rows[i].row);
        return new { name, count = rows.Count, self = CastOf(em, character, barType, prefabs), threats = list };
    }

    /// <summary>
    /// The unit's current cast from <c>AbilityBar_Shared</c> (state, ability, group, cast time, counter).
    /// Used for enemies and for the bot itself: its own combo step (which primary Cast0N just fired and
    /// whether it is still casting or recovering).
    /// </summary>
    private static unsafe object? CastOf(EntityManager em, Entity e, ComponentType barType, PrefabCollectionSystem? prefabs)
    {
        if (!em.HasComponent(e, barType)) return null;
        byte* bar = (byte*)em.GetComponentDataRawRO(e, barType.TypeIndex);
        if (bar == null) return null;
        int state = *(int*)(bar + 0x44);
        int abilityGuid = *(int*)(bar + 0x58);
        return new
        {
            state = state switch { 0 => "none", 1 => "interrupted", 2 => "casting", 3 => "postcasting", 4 => "finished", _ => state.ToString() },
            ability = abilityGuid == 0 || prefabs == null ? null : EntityOps.TryPrefabName(prefabs, new PrefabGUID(abilityGuid)),
            group = prefabs == null ? null : EntityOps.TryPrefabName(prefabs, new PrefabGUID(*(int*)(bar + 0x54))),
            castTime = *(float*)(bar + 0x10),
            postCastTime = *(float*)(bar + 0x14),
            counter = *(int*)(bar + 0x28),
            completed = *(int*)(bar + 0x2C),
        };
    }

    /// <summary>The unit's blood as a feed would take it (type name, quality 0..100, consumable).</summary>
    private static unsafe (string? type, float? quality, bool canConsume) BloodOf(EntityManager em, Entity e, ComponentType bloodType, PrefabCollectionSystem? prefabs)
    {
        if (!em.HasComponent(e, bloodType)) return (null, null, false);
        byte* raw = (byte*)em.GetComponentDataRawRO(e, bloodType.TypeIndex);
        if (raw == null) return (null, null, false);
        int guid = *(int*)(raw + 0xC);
        string? name = prefabs == null ? guid.ToString() : EntityOps.TryPrefabName(prefabs, new PrefabGUID(guid)) ?? guid.ToString();
        return (name, *(float*)(raw + 0x8), *(raw + 0x20) != 0);
    }

    private static bool s_layoutChecked;

    /// <summary>Refuse raw reads if the interop structs disagree with the metadata offsets used above.</summary>
    private static void CheckLayout()
    {
        if (s_layoutChecked) return;
        void Expect<T>(string field, int offset)
        {
            int actual = (int)Marshal.OffsetOf<T>(field);
            if (actual != offset) throw new InvalidOperationException($"{typeof(T).Name}.{field} is at 0x{actual:X}, expected 0x{offset:X}; refusing raw reads");
        }
        Expect<AbilityBar_Shared>(nameof(AbilityBar_Shared.CastTime), 0x10);
        Expect<AbilityBar_Shared>(nameof(AbilityBar_Shared.PostCastTime), 0x14);
        Expect<AbilityBar_Shared>(nameof(AbilityBar_Shared.CastCompletedCounter), 0x2C);
        Expect<AbilityBar_Shared>(nameof(AbilityBar_Shared.CastStartedCounter), 0x28);
        Expect<AbilityBar_Shared>(nameof(AbilityBar_Shared.ThisFrameState), 0x44);
        Expect<AbilityBar_Shared>(nameof(AbilityBar_Shared.CastGroupPrefabGuid), 0x54);
        Expect<AbilityBar_Shared>(nameof(AbilityBar_Shared.CastAbilityPrefabGuid), 0x58);
        Expect<AggroConsumer>(nameof(AggroConsumer.MaxDistanceFromPreCombatPosition), 0x24);
        Expect<BloodConsumeSource>(nameof(BloodConsumeSource.BloodQuality), 0x8);
        Expect<BloodConsumeSource>(nameof(BloodConsumeSource.UnitBloodType), 0xC);
        Expect<BloodConsumeSource>(nameof(BloodConsumeSource.CanBeConsumed), 0x20);
        Expect<AggroConsumer>(nameof(AggroConsumer.AggroTarget), 0x30);
        s_layoutChecked = true;
    }
}
