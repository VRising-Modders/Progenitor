#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ecs;
using ProjectM;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Combat;

/// <summary>
/// Server: every <see cref="CombatMusicRepair.CheckSeconds"/>, clear combat music that no unit targeting the player
/// carries any more (<see cref="CombatMusicRepair"/>), as the game's own "stopped targeting" step would. Connected
/// players only; a value is changed only after two checks in a row found it stale.
/// </summary>
internal static class CombatMusicRepairServer
{
    private static bool _installed;
    private static double _next;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly Dictionary<Entity, int> Stale = new();
    private static readonly List<(int Prefab, bool Active)> Targeting = new();

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        Plugin.Frame -= OnFrame;
        Plugin.Frame += OnFrame;
    }

    internal static void Uninstall()
    {
        _installed = false;
        try { Plugin.Frame -= OnFrame; }
        catch { /* already detached */ }
        Stale.Clear();
    }

    private static void OnFrame()
    {
        double now = Clock.Elapsed.TotalSeconds;
        if (now < _next) return;
        _next = now + CombatMusicRepair.CheckSeconds;
        try { Check(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Combat music: the check threw: " + e.Message); }
    }

    private static void Check()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        // Disabled characters are offline players: their music does not play.
        var players = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<CombatMusicListener_Shared>(ComponentType.AccessMode.ReadOnly));
        var seen = new HashSet<Entity>();
        foreach (var player in players)
        {
            seen.Add(player);
            if (!player.TryRead<CombatMusicListener_Shared>(em, out var listener)) continue;
            int current = listener.UnitPrefabGuid.GuidHash;
            if (current == 0)
            {
                Stale.Remove(player);
                continue;
            }

            ReadTargeting(em, player);
            int expected = CombatMusicRepair.Expected(current, Targeting);
            if (expected == current)
            {
                Stale.Remove(player);
                continue;
            }

            int? last = Stale.TryGetValue(player, out var s) ? s : null;
            if (!CombatMusicRepair.ShouldRepair(current, expected, last))
            {
                Stale[player] = current;
                continue;
            }

            Stale.Remove(player);
            listener.UnitPrefabGuid = new PrefabGUID(expected);
            listener.PlayOutro = false;
            player.Write(em, listener);
            Plugin.Logger?.LogInfo("Combat music: cleared stale music (unit prefab " + current + ") on " + player.Index + ":" +
                player.Version + "; no unit of it targets the player" + (expected != 0 ? ", now " + expected : "") + ".");
        }

        if (Stale.Count > 0)
            foreach (var gone in new List<Entity>(Stale.Keys))
                if (!seen.Contains(gone)) Stale.Remove(gone);
    }

    /// <summary>
    /// The units targeting the player: the player's <c>InverseAggroBufferElement</c> rows, as the game's own music code
    /// walks them, with each unit's prefab and whether its music source is active.
    /// </summary>
    private static void ReadTargeting(EntityManager em, Entity player)
    {
        Targeting.Clear();
        if (!em.HasComponent(player, ComponentIndex.Of<InverseAggroBufferElement>(ComponentType.AccessMode.ReadOnly))) return;
        var buffer = em.GetBuffer<InverseAggroBufferElement>(player, true);
        for (int i = 0; i < buffer.Length; i++)
        {
            var unit = buffer[i].Entity;
            if (unit == Entity.Null || !em.Exists(unit)) continue;
            if (!unit.TryRead<CombatMusicSource_Server>(em, out var source)) continue;
            int prefab = unit.TryRead<PrefabGUID>(em, out var guid) ? guid.GuidHash : 0;
            Targeting.Add((prefab, source.Active.Value));
        }
    }
}
#endif
