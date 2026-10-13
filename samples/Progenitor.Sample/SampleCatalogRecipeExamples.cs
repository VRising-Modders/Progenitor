#if !PROGENITOR_TESTS
using System;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Effects;
using Progenitor.Networking;
using Progenitor.Plugins;
using ProjectM.Network;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Sample;

/// <summary>
/// Compile-verified usage shapes for the catalog recipes, plus <c>.catalogex</c> so a live
/// job can borrow Charm on the client (chat commands only run on the server).
/// </summary>
internal static unsafe class SampleCatalogRecipeExamples
{
    /// <summary>Server→client event id, ASCII <c>SmCx</c> ("sample catalogex"). Outside 0x5072____.</summary>
    internal const int EventId = 0x536D4378;

    internal const int KindBorrow = 1;
    internal const int KindDispose = 2;
    internal const int KindStatus = 3;
    internal const int KindWarm = 4;

    internal const string UsageReply = "Usage: .catalogex borrow|dispose|status|warm";

    internal static IServerBeamHandle CharmTargetLink(PluginContext ctx, Entity source, Entity target) =>
        ctx.Effects.ServerBeamLink(source, target, seconds: 20f, sequenceGuid: -541113174);

    internal static IServerBeamHandle CharmCasterDiagnosticLink(PluginContext ctx, Entity source, Entity target) =>
        ctx.Effects.ServerBeamLink(source, target, seconds: 20f, sequenceGuid: -389499105);

    internal static IEffectHandle CharmLoopParticleBorrow(PluginContext ctx) =>
        ctx.Effects.Borrow(AssetKind.Particle, "Part_Charm_Loop_01");

    private static IEffectHandle? _borrowed;

    internal struct Packet
    {
        public int Kind;
    }

    internal static void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        if (ctx.IsServer)
            ctx.Commands.Register(typeof(SampleCatalogExCommands));
        else
            ctx.Net.OnReceive(EventId, OnReceive);
    }

    internal static bool TryParse(string what, out int kind)
    {
        kind = 0;
        switch ((what ?? "").Trim().ToLowerInvariant())
        {
            case "borrow": kind = KindBorrow; return true;
            case "dispose": kind = KindDispose; return true;
            case "status": kind = KindStatus; return true;
            case "warm": kind = KindWarm; return true;
            default: return false;
        }
    }

    internal static bool Send(ICommandContext ctx, int kind)
    {
        var plugin = Plugin.Active;
        if (plugin == null) return false;
        return plugin.Net.SendToUser(ctx.UserEntity, EventId, new Packet { Kind = kind });
    }

    /// <summary>
    /// Play Charm target sequence <c>-541113174</c> with the caller's character as both
    /// endpoints so <c>Part_Charm_Loop_01</c> becomes resident on the client. Same-entity
    /// endpoints are accepted by <c>ServerBeam.Link</c> (only <c>endIndex == 0</c> is a
    /// one-entity mark). The handle is tracked by <c>ServerBeamLink</c> and disposes at
    /// its deadline.
    /// </summary>
    internal static void WarmOnServer(ICommandContext ctx, PluginContext plugin)
    {
        var character = ctx.CharacterEntity;
        if (character == Entity.Null)
        {
            Plugin.Logger?.LogInfo("CATALOGEX warm entity= enabled=False");
            ctx.Reply("catalogex warm: no character.");
            return;
        }

        var handle = plugin.Effects.ServerBeamLink(character, character, seconds: 8f, sequenceGuid: -541113174);
        Plugin.Logger?.LogInfo("CATALOGEX warm entity=" + handle.Entity + " enabled=" + handle.Enabled);
        ctx.Reply("catalogex warm entity=" + handle.Entity + " enabled=" + handle.Enabled);
    }

    private static void OnReceive(int eventId, byte* payload, int length)
    {
        try
        {
            if (eventId != EventId) return;
            if (!CustomEvents.TryRead<Packet>(payload, length, out var packet)) return;
            if (packet.Kind == KindBorrow) BorrowOnClient();
            else if (packet.Kind == KindDispose) DisposeOnClient();
            else if (packet.Kind == KindStatus) StatusOnClient();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample catalogex receive threw: " + e);
        }
    }

    private static void BorrowOnClient()
    {
        DisposeOnClient();
        var ctx = Plugin.Active;
        if (ctx == null)
        {
            Plugin.Logger?.LogInfo("CATALOGEX borrow enabled=False root=");
            return;
        }

        var handle = CharmLoopParticleBorrow(ctx);
        _borrowed = handle;
        if (handle is ParticleHandle particle && particle.Enabled)
        {
            if (TryLocalCharacterPosition(out var pos))
                particle.Position = pos;
        }

        Plugin.Logger?.LogInfo("CATALOGEX borrow enabled=" + handle.Enabled + " root=" + handle.RootName);
    }

    private static void DisposeOnClient()
    {
        var handle = _borrowed;
        _borrowed = null;
        if (handle == null) return;
        try { handle.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("CATALOGEX dispose threw: " + e.Message); }
        Plugin.Logger?.LogInfo("CATALOGEX dispose state=" + handle.State + " disposed=" + handle.IsDisposed);
    }

    private static void StatusOnClient()
    {
        var handle = _borrowed;
        string root = handle?.RootName ?? "progenitor-effects-progenitor.sample";
        int children = CountRootChildren(root, out bool present);
        string state = handle == null ? "none" : handle.State.ToString();
        Plugin.Logger?.LogInfo(
            "CATALOGEX status handle=" + (handle == null ? "null" : state)
            + " enabled=" + (handle != null && handle.Enabled)
            + " disposed=" + (handle != null && handle.IsDisposed)
            + " root=" + root
            + " rootPresent=" + present
            + " children=" + children);
    }

    private static bool TryLocalCharacterPosition(out EffectVec3 position)
    {
        position = default;
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return false;
            var em = world.EntityManager;
            var users = Query.Entities(
                em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
            for (int i = 0; i < users.Count; i++)
            {
                var user = em.GetComponentData<User>(users[i]);
                var ch = user.LocalCharacter._Entity;
                if (ch == Entity.Null || !em.Exists(ch)) continue;
                if (!ch.TryRead<Translation>(em, out var translation)) continue;
                float3 p = translation.Value;
                position = new EffectVec3(p.x, p.y, p.z);
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("CATALOGEX pose: " + e.Message);
        }

        return false;
    }

    private static int CountRootChildren(string rootName, out bool present)
    {
        present = false;
        int children = 0;
        try
        {
            var arr = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.GameObject>();
            if (arr == null) return 0;
            for (int i = 0; i < arr.Length; i++)
            {
                var go = arr[i];
                if (go == null) continue;
                string name;
                try { name = go.name ?? ""; }
                catch { continue; }
                if (name != rootName) continue;
                present = true;
                try { children = go.transform.childCount; }
                catch { children = 0; }
                break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("CATALOGEX root census: " + e.Message);
        }

        return children;
    }
}

internal static class SampleCatalogExCommands
{
    [Command("catalogex", adminOnly: true, usage: ".catalogex borrow|dispose|status|warm")]
    public static void CatalogEx(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleCatalogRecipeExamples.TryParse(what, out int kind))
            {
                ctx.Reply(SampleCatalogRecipeExamples.UsageReply);
                return;
            }

            var plugin = Plugin.Active;
            if (plugin == null)
            {
                ctx.Reply("Sample plugin context is not available.");
                return;
            }

            if (kind == SampleCatalogRecipeExamples.KindWarm)
            {
                SampleCatalogRecipeExamples.WarmOnServer(ctx, plugin);
                return;
            }

            bool sent = SampleCatalogRecipeExamples.Send(ctx, kind);
            ctx.Reply(sent
                ? "catalogex " + what + " sent to your client."
                : "Could not send catalogex to your client (are you connected?).");
            Plugin.Logger?.LogInfo("Sample: catalogex server " + what + " sent=" + sent);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .catalogex threw: " + e);
            ctx.Reply("Sample catalogex failed. See the server log.");
        }
    }
}
#endif
