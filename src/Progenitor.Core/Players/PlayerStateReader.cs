#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Players;

/// <summary>
/// Collects the facts behind <c>ctx.Players.State</c> from the server world and hands them to
/// <see cref="PlayerStateLogic.Build"/>. Read-only, and only through access paths the framework
/// already uses: the typed <c>User</c> read (the directory's), <c>Has&lt;T&gt;</c>, the
/// <c>Health</c> raw read at its layout offsets, the <c>BuffBuffer</c> read the AI module uses,
/// the prefab name table, and a raw byte of <c>Shapeshift</c> at its layout offset. No new generic
/// interop instantiation over a game value type.
/// </summary>
internal static unsafe class PlayerStateReader
{
    /// <summary>State for <paramref name="info"/>, re-validated against the live user entity.</summary>
    internal static PlayerStateInfo Read(World? world, PlayerInfo? info)
    {
        if (info == null) return PlayerStateInfo.Fail(PlayerStateStatus.NotAPlayer, "no player (null PlayerInfo)");
        if (info.WorldEpoch != WorldEpoch.Current)
            return PlayerStateInfo.Fail(PlayerStateStatus.Stale,
                "the PlayerInfo is from world epoch " + info.WorldEpoch + "; the world is now in epoch " + WorldEpoch.Current + ". Look the player up again.");
        var user = new Entity { Index = info.Session.UserIndex, Version = info.Session.UserVersion };
        return Read(world, user, info.PlatformId);
    }

    /// <summary>State for a <c>User</c> entity. <paramref name="expectedAccount"/> 0 skips the account check.</summary>
    internal static PlayerStateInfo Read(World? world, Entity user, ulong expectedAccount = 0)
    {
        if (!Plugin.IsServerProcess) return PlayerStateInfo.Fail(PlayerStateStatus.Unavailable, "player state is read on the server only");
        world ??= Worlds.Server;
        if (world == null || !world.IsCreated) return PlayerStateInfo.Fail(PlayerStateStatus.Unavailable, "the server world is not running");
        var em = world.EntityManager;
        try
        {
            if (user == Entity.Null) return PlayerStateInfo.Fail(PlayerStateStatus.NotAPlayer, "no user entity (Entity.Null)");
            if (!user.Exists(em)) return PlayerStateInfo.Fail(PlayerStateStatus.Stale, "user entity " + user.Index + ":" + user.Version + " no longer exists");
            if (!user.Has<User>(em)) return PlayerStateInfo.Fail(PlayerStateStatus.NotAPlayer, "entity " + user.Index + ":" + user.Version + " is not a User (pass the User entity, not the character)");

            User u = em.GetComponentData<User>(user);
            if (expectedAccount != 0 && u.PlatformId != expectedAccount)
                return PlayerStateInfo.Fail(PlayerStateStatus.Stale, "the user entity now belongs to another account. Look the player up again.");

            var f = new PlayerStateFacts
            {
                Online = u.IsConnected,
                DownedBuff = Guid(world, PlayerStateLogic.DownedBuffName, PlayerStateLogic.DownedBuffGuid),
                PvpCombatBuff = Guid(world, PlayerStateLogic.PvpCombatBuffName, PlayerStateLogic.PvpCombatBuffGuid),
                CombatBuff = Guid(world, PlayerStateLogic.CombatBuffName, PlayerStateLogic.CombatBuffGuid),
            };

            Entity character = Entity.Null;
            try { character = u.LocalCharacter._Entity; } catch { character = Entity.Null; }
            f.HasCharacter = character != Entity.Null;
            f.CharacterExists = f.HasCharacter && character.Exists(em);
            if (f.CharacterExists)
            {
                f.CharacterDisabled = character.Has<Disabled>(em);
                f.HealthRead = AiRuntime.TryHealth(em, character, out f.Health, out f.MaxHealth, out f.HealthIsDead);
                f.HasDeadComponent = character.Has<Dead>(em);
                f.BuffsRead = character.Has<BuffBuffer>(em);
                if (f.BuffsRead)
                {
                    var guids = AiRuntime.BuffGuids(em, character);
                    var buffs = new List<(int, string)>(guids.Count);
                    foreach (int g in guids)
                        buffs.Add((g, PrefabLookup.TryGetName(world, new PrefabGUID(g), out string name) ? name : ""));
                    f.Buffs = buffs;
                }

                f.ControllerShapeshifted = ReadShapeshifted(em, user);
            }

            return PlayerStateLogic.Build(f);
        }
        catch (Exception e)
        {
            // A read must never throw into a command or event handler.
            Plugin.Logger?.LogWarning("ctx.Players.State: read of " + user.Index + ":" + user.Version + " failed: " + e.Message);
            return PlayerStateInfo.Fail(PlayerStateStatus.Unavailable, "the read failed: " + e.Message);
        }
    }

    /// <summary>The live GUID for a vanilla prefab name, or the recorded one when the name table cannot answer.</summary>
    private static int Guid(World world, string name, int fallback) =>
        PrefabLookup.TryGetGuidByName(world, name, out var guid) && guid.GuidHash != 0 ? guid.GuidHash : fallback;

    /// <summary>
    /// <c>Shapeshift.IsShapeshifted</c> on the controller (the User entity carries it, not the
    /// body). Read as the raw byte at its layout offset: the wrapper's <c>Value</c> getter is a
    /// native call on a by-ref struct. Null when absent or unreadable.
    /// </summary>
    private static bool? ReadShapeshifted(EntityManager em, Entity controller)
    {
        try
        {
            byte* p = AiRuntime.RO<Shapeshift>(em, controller, Layout.ShapeshiftIsShapeshiftedOffset + 1);
            return p == null ? null : p[Layout.ShapeshiftIsShapeshiftedOffset] != 0;
        }
        catch
        {
            return null;
        }
    }
}
#endif
