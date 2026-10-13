using System;
using System.Collections.Generic;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Systems;
using ProjectM;
using ProjectM.Network;
using ProjectM.Sequencer;
using ProjectM.Gameplay.Scripting;
using Stunlock.Core;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Sample;

/// <summary>Instance-scoped appearance for Sample frost rings. Never edits a vanilla prefab/material.</summary>
internal static unsafe class SampleFrostNovaAppearance
{
    internal const float Radius = 18f;
    internal const string UsageReply = "Usage: .froststyle on | off | status";
    internal static bool Enabled { get; private set; } = true;
    internal enum Kind { Bad, On, Off, Status }
    private const int EventId = 0x536D4E76; // SmNv
    private const int Magic = 0x4E4F5631; // NOV1
    private const string CrystalShader = "Shader Graphs/Shader_VFX_Ice_Crystal_Unlit_Final";
    // Installed client/server dump: RingAoE_DataShared.EndRadius +4. ParticleOutput is
    // 0x60 bytes (last byte +0x59, alignment 8), SequenceEntity +0. TypeInfo.ElementSize
    // reports zero on this interop build; see frost-dash-payload.md for native/live evidence.
    private const int RadiusOffset = 4;
    private const int OutputStride = 96;
    // Existing SmNv/NOV1 packet plus Enabled (1=on, 0=off). Ring sends still carry Id and
    // Radius; .froststyle on|off broadcasts the same struct with a default (invalid)
    // NetworkId so the client can apply the flag without a live ring.
    private struct Packet { public int Magic; public NetworkId Id; public float Radius; public int Enabled; }
    private sealed class ServerRing { public Entity Entity; public long Next; public int Sent; }
    private sealed class ClientRing { public long Expires; public Entity Entity; public bool Prepared; public bool Colored; }
    private sealed class StyledParticle
    {
        public Entity Entity;
        public GameObject Root = null!;
        public ClientRing Owner = null!;
        public double TimeCreated;
        public long Expires;
        public bool TailLogged;
    }
    private sealed class Block
    {
        public Renderer Renderer = null!;
        public int Slot;
        public readonly MaterialPropertyBlock Original = new();
        public readonly MaterialPropertyBlock Edited = new();
    }
    private static readonly List<ServerRing> ServerRings = new();
    private static readonly Dictionary<long, ClientRing> ClientRings = new();
    private static readonly Dictionary<Entity, ClientRing> Owned = new();
    private static readonly List<Block> BlockPool = new();
    private static readonly Dictionary<Entity, StyledParticle> Styled = new();
    private static int _blocks;
    private static PluginContext? _ctx;
    private static World? _world;
    private static IDisposable? _serverTick, _before, _after;

    internal static bool TryParse(string what, out Kind kind)
    {
        kind = Kind.Bad;
        switch ((what ?? "").Trim().ToLowerInvariant())
        {
            case "": case "status": kind = Kind.Status; return true;
            case "on": kind = Kind.On; return true;
            case "off": kind = Kind.Off; return true;
            default: return false;
        }
    }
    internal static void Attach(PluginContext ctx)
    {
        _ctx = ctx;
        Enabled = true;
        if (ctx.IsServer)
        {
            ctx.Commands.Register(typeof(SampleFrostNovaAppearanceCommands));
            _serverTick = ctx.Systems.Add("sample.nova.send", Send);
        }
        else
        {
            ctx.Net.OnReceive(EventId, Receive);
            _before = ctx.Systems.Add("sample.nova.prepare", VirtualSystemAnchor.Before<PresentationSystemGroup>(Side.Client), Before);
            _after = ctx.Systems.Add("sample.nova.color", VirtualSystemAnchor.After<PresentationSystemGroup>(Side.Client), After);
        }
    }
    internal static int SetEnabled(bool on)
    {
        int restored = 0;
        Enabled = on;
        if (!on) restored = Restore();
        else ApplyRadiusToTracked();
        Broadcast();
        return restored;
    }
    internal static void Detach()
    {
        _serverTick?.Dispose(); _before?.Dispose(); _after?.Dispose();
        _serverTick = _before = _after = null;
        Restore(); BlockPool.Clear(); ServerRings.Clear(); ClientRings.Clear(); Owned.Clear(); Styled.Clear();
        _world = null; _ctx = null;
    }
    internal static void Track(World world, Entity ring)
    {
        if (_world != world) { ServerRings.Clear(); _world = world; }
        if (Enabled) SetRadius(world.EntityManager, ring);
        ServerRings.Add(new ServerRing { Entity = ring });
        if (Enabled)
            Plugin.Logger?.LogInfo($"FROSTSTYLE server ring={ring} radius={Radius}; authored crystal scale unchanged");
    }
    private static long Key(NetworkId id) => ((long)(byte)id.Type << 40) | ((long)id.Normal_Generation << 32) | (uint)id.Normal_Index;
    private static void Send(VirtualSystemContext ctx)
    {
        if (_ctx == null || ctx.World != _world) return;
        long now = Environment.TickCount64;
        for (int i = ServerRings.Count - 1; i >= 0; i--)
        {
            var ring = ServerRings[i];
            if (!ctx.EntityManager.Exists(ring.Entity)) { ServerRings.RemoveAt(i); continue; }
            if (now < ring.Next || !ring.Entity.TryRead<NetworkId>(ctx.EntityManager, out var id) || !id.IsValid) continue;
            _ctx.Net.SendToAll(ctx.World, EventId, new Packet { Magic = Magic, Id = id, Radius = Radius, Enabled = Enabled ? 1 : 0 });
            ring.Next = now + (ring.Sent++ < 3 ? 50 : 500);
        }
    }
    private static void Receive(int id, byte* bytes, int length)
    {
        if (id != EventId || !CustomEvents.TryRead<Packet>(bytes, length, out var packet)
            || packet.Magic != Magic) return;
        bool on = packet.Enabled != 0;
        if (Enabled != on)
        {
            Enabled = on;
            if (!on)
            {
                Restore();
                ClientRings.Clear();
                Owned.Clear();
                Styled.Clear();
            }
            Plugin.Logger?.LogInfo("FROSTSTYLE client enabled=" + on);
        }
        if (!Enabled) return;
        if (!packet.Id.IsValid || packet.Radius != Radius) return;
        var world = Progenitor.Worlds.Client;
        if (world == null) return;
        if (_world != world) { Restore(); ClientRings.Clear(); Owned.Clear(); Styled.Clear(); _world = world; }
        long key = Key(packet.Id);
        if (!ClientRings.TryGetValue(key, out var ring))
        {
            if (ClientRings.Count >= 256) return;
            ClientRings[key] = ring = new ClientRing();
        }
        ring.Expires = Environment.TickCount64 + 10000;
    }
    private static void SetRadius(EntityManager em, Entity ring)
    {
        byte* data = (byte*)em.GetComponentDataRawRW(ring, ComponentIndex.IndexOf<RingAoE_DataShared>());
        *(float*)(data + RadiusOffset) = Radius;
    }
    private static int Restore()
    {
        // Runs before presentation/pool reuse, and on unload. Leave the game's other block
        // values intact; never instantiate Renderer.material or write sharedMaterial.
        int restored = 0;
        for (int i = 0; i < _blocks; i++)
        {
            var b = BlockPool[i];
            try
            {
                if (b.Renderer != null)
                {
                    b.Renderer.SetPropertyBlock(b.Original, b.Slot);
                    restored++;
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Frost style restore: " + e.Message); }
            b.Renderer = null!;
        }
        _blocks = 0;
        return restored;
    }
    private static void Broadcast()
    {
        if (_ctx == null) return;
        var packet = new Packet { Magic = Magic, Radius = Radius, Enabled = Enabled ? 1 : 0 };
        var world = _world ?? Progenitor.Worlds.Server;
        if (world != null) _ctx.Net.SendToAll(world, EventId, packet);
        else _ctx.Net.SendToAll(EventId, packet);
    }
    private static void ApplyRadiusToTracked()
    {
        if (_world == null || !_world.IsCreated) return;
        var em = _world.EntityManager;
        for (int i = 0; i < ServerRings.Count; i++)
        {
            var ring = ServerRings[i];
            if (!em.Exists(ring.Entity)) continue;
            SetRadius(em, ring.Entity);
            ring.Next = 0;
        }
    }
    private static void Before(VirtualSystemContext ctx)
    {
        try
        {
            Restore();
            if (!Enabled) return;
            if (_world != ctx.World) { ClientRings.Clear(); Owned.Clear(); Styled.Clear(); _world = ctx.World; }
            long now = Environment.TickCount64;
            foreach (var key in new List<long>(ClientRings.Keys))
            {
                var record = ClientRings[key];
                if (now >= record.Expires || (record.Prepared && !ctx.EntityManager.Exists(record.Entity)))
                { Owned.Remove(record.Entity); ClientRings.Remove(key); }
            }
            if (ClientRings.Count == 0) return;
            foreach (var ring in Query.Entities(ctx.EntityManager, true, false,
                ComponentIndex.Of<RingAoE_DataShared>(ComponentType.AccessMode.ReadOnly)))
            {
                if (!ring.TryRead<PrefabGUID>(ctx.EntityManager, out var guid) || guid.GuidHash != SampleFrostDash.FrostNovaRing
                    || !ring.TryRead<NetworkId>(ctx.EntityManager, out var net)
                    || !ClientRings.TryGetValue(Key(net), out var record)) continue;
                SetRadius(ctx.EntityManager, ring);
                if (!record.Prepared)
                    Plugin.Logger?.LogInfo($"FROSTSTYLE client ring={ring} radius={Radius} network={net.Normal_Index}:{net.Normal_Generation}");
                record.Entity = ring; record.Prepared = true; Owned[ring] = record;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogError("Frost style prepare: " + e); }
    }
    private static void After(VirtualSystemContext ctx)
    {
        if (!Enabled || (Owned.Count == 0 && Styled.Count == 0)) return;
        try
        {
            var system = ctx.World.GetExistingSystemManaged<UpdatePresentationPostGraphSystem>();
            if (system == null) return;
            var visited = new HashSet<int>();
            var emitting = new HashSet<Entity>();
            ColorOutputs(ctx.EntityManager, system._ThisFrameParticles, emitting);
            ColorOutputs(ctx.EntityManager, system._LastFrameParticles, emitting);
            foreach (var id in new List<Entity>(Styled.Keys))
            {
                var particle = Styled[id];
                var component = ComponentIndex.Of<ParticleSettings>(ComponentType.AccessMode.ReadOnly);
                if (Environment.TickCount64 >= particle.Expires || !ctx.EntityManager.Exists(id)
                    || !ctx.EntityManager.HasComponent(id, component)
                    || !ctx.EntityManager.HasComponent(id, ComponentIndex.Of<SequencerParticle>(ComponentType.AccessMode.ReadOnly)))
                { Styled.Remove(id); continue; }
                var settings = ctx.EntityManager.GetComponentObject<Il2CppSystem.Object>(id, component)?.TryCast<ParticleSettings>();
                double created = *(double*)ctx.EntityManager.GetComponentDataRawRO(id, ComponentIndex.IndexOf<SequencerParticle>());
                if (settings == null || settings.gameObject != particle.Root || created != particle.TimeCreated)
                { Styled.Remove(id); continue; }
                if (!emitting.Contains(id) && !particle.TailLogged)
                {
                    particle.TailLogged = true;
                    Plugin.Logger?.LogInfo($"FROSTSTYLE tint retained for fading particle={id}; sequence output ended while renderer remains alive");
                }
                if (visited.Add(particle.Root.GetInstanceID())) ColorParticle(particle.Root, particle.Owner);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogError("Frost style color: " + e); }
    }
    private static ClientRing? FindOwner(EntityManager em, Entity sequence)
    {
        // Child particle sequences use Attach.Parent; SequenceInstance.Target is null here.
        for (int depth = 0; depth < 8 && em.Exists(sequence); depth++)
        {
            if (Owned.TryGetValue(sequence, out var ring)) return ring;
            if (!sequence.TryRead<Attach>(em, out var attach) || attach.Parent == sequence) break;
            sequence = attach.Parent;
        }
        return null;
    }
    private static void ColorOutputs(EntityManager em, ParticleData data, HashSet<Entity> emitting)
    {
        // Native UpdateParticles creates one Entities row for each Particles row and fills
        // Entities[i] with the created/reused particle entity. Read on the main thread only.
        var outputs = data.Particles.m_ListData; var entities = data.Entities.m_ListData;
        if (outputs == null || entities == null || outputs->m_length != entities->m_length
            || outputs->m_length < 0 || outputs->m_length > 8192) return;
        byte* rows = (byte*)outputs->Ptr; Entity* ids = (Entity*)entities->Ptr;
        if (rows == null || ids == null) return;
        var component = ComponentIndex.Of<ParticleSettings>(ComponentType.AccessMode.ReadOnly);
        for (int i = 0; i < outputs->m_length; i++)
        {
            var owner = FindOwner(em, *(Entity*)(rows + i * OutputStride));
            if (owner == null || !em.Exists(ids[i]) || !em.HasComponent(ids[i], component)) continue;
            var settings = em.GetComponentObject<Il2CppSystem.Object>(ids[i], component)?.TryCast<ParticleSettings>();
            var go = settings?.gameObject;
            if (go == null || !em.HasComponent(ids[i], ComponentIndex.Of<SequencerParticle>(ComponentType.AccessMode.ReadOnly))) continue;
            emitting.Add(ids[i]);
            double created = *(double*)em.GetComponentDataRawRO(ids[i], ComponentIndex.IndexOf<SequencerParticle>());
            if (!Styled.TryGetValue(ids[i], out var styled) || styled.Root != go || styled.TimeCreated != created)
            {
                if (Styled.Count >= 256) continue;
                Styled[ids[i]] = new StyledParticle { Entity = ids[i], Root = go, Owner = owner,
                    TimeCreated = created, Expires = Environment.TickCount64 + 15000 };
            }
        }
    }
    private static void ColorParticle(GameObject go, ClientRing owner)
    {
            int changed = 0;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var material = materials[slot];
                    if (material == null || material.shader.name != CrystalShader) continue;
                    if (_blocks >= 1024) return;
                    if (_blocks == BlockPool.Count) BlockPool.Add(new Block());
                    var block = BlockPool[_blocks++]; block.Renderer = renderer; block.Slot = slot;
                    renderer.GetPropertyBlock(block.Original, slot); renderer.GetPropertyBlock(block.Edited, slot);
                    // A per-slot block takes precedence over the renderer-wide block. Carry its
                    // effective values (including any fade settings) into our temporary override.
                    if (block.Original.isEmpty) renderer.GetPropertyBlock(block.Edited);
                    block.Edited.SetColor("_BaseColor", new Color(0.12f, 0.65f, 0.25f, 1));
                    block.Edited.SetColor("_EmissiveColor", new Color(0.05f, 1.4f, 0.18f, 1));
                    block.Edited.SetColor("_GroundTint", new Color(0.15f, 0.75f, 0.25f, 0));
                    block.Edited.SetColor("_Fresnel_Color", new Color(0.2f, 0.8f, 0.3f, 0));
                    renderer.SetPropertyBlock(block.Edited, slot); changed++;
                }
            }
            if (!owner.Colored && changed > 0)
            {
                owner.Colored = true;
                Plugin.Logger?.LogInfo($"FROSTSTYLE green ring={owner.Entity} crystalRenderers={changed} particleRootScale={go.transform.localScale}; particle sizes untouched");
            }
    }
}

internal static class SampleFrostNovaAppearanceCommands
{
    [Command("froststyle", adminOnly: true, usage: ".froststyle on | off | status",
        description: "Admin only. Enable or disable the sample frost nova appearance override.")]
    public static void FrostStyle(ICommandContext ctx, string what = "")
    {
        try
        {
            if (!SampleFrostNovaAppearance.TryParse(what, out var kind))
            {
                ctx.Reply(SampleFrostNovaAppearance.UsageReply);
                return;
            }

            int restored = 0;
            if (kind == SampleFrostNovaAppearance.Kind.On)
            {
                SampleFrostNovaAppearance.SetEnabled(true);
                ctx.Reply("Frost nova appearance ON.");
            }
            else if (kind == SampleFrostNovaAppearance.Kind.Off)
            {
                restored = SampleFrostNovaAppearance.SetEnabled(false);
                ctx.Reply("Frost nova appearance off.");
            }
            else
            {
                ctx.Reply("Frost nova appearance is " + (SampleFrostNovaAppearance.Enabled ? "ON" : "off") + ".");
            }

            Plugin.Logger?.LogInfo("FROSTSTYLE enabled=" + SampleFrostNovaAppearance.Enabled + " restored=" + restored);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample .froststyle threw: " + e);
            ctx.Reply("Sample froststyle failed. See the server log.");
        }
    }
}
