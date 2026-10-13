using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Progenitor.Networking;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Content;

/// <summary>Ordered identities travel first; definitions use a bounded, per-player stream.</summary>
internal static unsafe class ItemDefinitionDelivery
{
    private const int DemandEvent = 0x50726651; // PrfQ
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Demand
    {
        public int Version, Priority, Count;
        public fixed int Guids[32];
    }
    private sealed class Pending
    {
        internal World World = null!;
        internal Entity User;
        internal readonly Dictionary<int, PrefabDefinition> Definitions = new();
        internal readonly DefinitionPriorityQueue Queue = new(16384);
        internal Action Complete = null!;
        internal double NextDemand, NextRelevance;
        internal int DemandTokens = 4;
        internal bool Ready;
        internal readonly HashSet<int> Identities = new();
        internal readonly Dictionary<int, (DefinitionPriority Priority, double Expires)> Demands = new();
        internal int Failures;
    }
    private static readonly Dictionary<Entity, Pending> Jobs = new();
    private static readonly ItemDefinitionRelevance Relevance = new();
    private static ItemDemandClientRuntime? _client;
    private static bool _serverSupportsDemand;
    private static double _nextSlice;
    private static int _roundRobin;
    // Not persisted: a debug bridge experiment may opt in for this Core generation.
    internal static bool DebugLoadingNotice;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    internal static void Install()
    {
        if (Plugin.IsServerProcess) CustomEvents.OnReceiveFromClient(DemandEvent, ReceiveDemand);
        else _client = new ItemDemandClientRuntime(SendDemand);
        Plugin.Frame += Tick;
    }
    internal static void Uninstall()
    {
        Plugin.Frame -= Tick;
        CustomEvents.RemoveClientHandler(DemandEvent);
        Jobs.Clear();
        Relevance.Clear();
        _client?.Dispose();
        _client = null;
        _serverSupportsDemand = false;
        DebugLoadingNotice = false;
    }
    internal static void NoteServerCapabilities(Handshake.Capability caps) =>
        _serverSupportsDemand = (caps & Handshake.Capability.ItemDefinitionPriority) != 0;
    internal static void EndClientConnection()
    {
        _serverSupportsDemand = false;
        _client?.Reset();
    }
    internal static bool HasPending(Entity user) => Jobs.ContainsKey(user);
    internal static void Ready(Entity user) { if (Jobs.TryGetValue(user, out var job)) job.Ready = true; }
    internal static void Begin(World world, Entity user, PrefabDefinition[] definitions, bool[] applied, Action complete)
    {
        var job = new Pending { World = world, User = user, Complete = complete };
        for (int i = 0; i < definitions.Length; i++)
        {
            if (!applied[i]) continue;
            var definition = definitions[i];
            job.Definitions[definition.Guid.GuidHash] = definition;
            job.Identities.Add(definition.Guid.GuidHash);
            job.Queue.Enqueue(definition.Guid.GuidHash);
        }
        Jobs[user] = job;
    }
    private static void Tick()
    {
        try
        {
            double now = Now;
            if (!Plugin.IsServerProcess)
            {
                var client = Worlds.Client;
                if (client != null && client.IsCreated) _client?.Tick(client, now);
                else _client?.Reset();
                return;
            }
            if (now < _nextSlice || Jobs.Count == 0) return;
            _nextSlice = now + .02;
            var jobs = new List<Pending>(Jobs.Values);
            for (int i = jobs.Count - 1; i >= 0; i--)
            {
                var candidate = jobs[i];
                if (candidate.World.IsCreated && ReferenceEquals(candidate.World, Worlds.Server)
                    && candidate.World.EntityManager.Exists(candidate.User)
                    && candidate.World.EntityManager.GetComponentData<User>(candidate.User).IsConnected) continue;
                Jobs.Remove(candidate.User);
                jobs.RemoveAt(i);
            }
            if (jobs.Count == 0) { Relevance.Clear(); return; }
            var watch = Stopwatch.StartNew();
            Relevance.Refresh(jobs[0].World, jobs[0].Identities);
            int sent = 0;
            for (int n = 0; n < jobs.Count && sent < 4 && watch.Elapsed.TotalMilliseconds < 4; n++)
            {
                var job = jobs[(_roundRobin + n) % jobs.Count];
                if (!job.World.IsCreated || !ReferenceEquals(job.World, Worlds.Server)
                    || !job.World.EntityManager.Exists(job.User)
                    || !job.World.EntityManager.GetComponentData<User>(job.User).IsConnected)
                { Jobs.Remove(job.User); continue; }
                if (!job.Ready) continue;
                if (now >= job.NextRelevance)
                {
                    job.NextRelevance = now + .5;
                    job.Queue.ResetPriorities();
                    Relevance.Promote(job.World, job.User, job.Queue);
                    foreach (int key in new List<int>(job.Demands.Keys))
                    {
                        var demand = job.Demands[key];
                        if (demand.Expires < now || !job.Queue.ContainsPending(key)) job.Demands.Remove(key);
                        else job.Queue.Enqueue(key, demand.Priority);
                    }
                }
                while (sent < 4 && watch.Elapsed.TotalMilliseconds < 4 && job.Queue.TryDequeue(out int guid))
                {
                    sent++;
                    if (!job.Definitions.TryGetValue(guid, out var def)) continue;
                    // Unregistered definitions must not be resurrected by an older queued snapshot.
                    if (!PrefabRegistry.IsRegisteredForDelivery(def)) continue;
                    bool ok = false;
                    try
                    {
                        if (!PrefabDefinitionPacket.TryFrom(def, out var packet, out var error))
                            Plugin.Logger?.LogWarning("Item definition could not be encoded: " + error);
                        else
                        {
                            ok = CustomEvents.SendToUser(job.World, job.User, PrefabDefinitionPacket.EventId, packet);
                            var user = job.World.EntityManager.GetComponentData<User>(job.User);
                            if (ok && def.Kind == PrefabKind.Item && Handshake.SupportsItemStats(user.Index))
                                ok = CustomEvents.SendToUser(job.World, job.User, ItemStatPacket.EventId, ItemStatPacket.From(def));
                        }
                    }
                    catch (Exception e) { ok = false; Plugin.Logger?.LogWarning("Item definition send will retry: " + e.Message); }
                    if (!ok)
                    {
                        job.Queue.Forget(guid);
                        job.Queue.Enqueue(guid);
                        if (++job.Failures >= 100) { Jobs.Remove(job.User); Plugin.Logger?.LogWarning("Item definition stream stopped after repeated send failures."); }
                        break;
                    }
                    job.Failures = 0;
                }
                if (job.Queue.PendingCount == 0)
                { Jobs.Remove(job.User); job.Complete(); }
            }
            _roundRobin = (_roundRobin + 1) % Math.Max(1, jobs.Count);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Item definition delivery: " + e.Message); }
    }
    private static void SendDemand(IReadOnlyList<int> inventory, IReadOnlyList<int> container)
    {
        if (!_serverSupportsDemand) return;
        Send(inventory, 0);
        Send(container, 1);
    }
    private static void Send(IReadOnlyList<int> guids, int priority)
    {
        for (int offset = 0; offset < Math.Min(64, guids.Count); offset += 32)
        {
            var packet = new Demand { Version = 1, Priority = priority, Count = Math.Min(32, guids.Count - offset) };
            for (int i = 0; i < packet.Count; i++) packet.Guids[i] = guids[offset + i];
            CustomEvents.SendToServer(DemandEvent, packet);
        }
    }
    private static void ReceiveDemand(int eventId, Entity userEntity, User user, byte* payload, int length)
    {
        if (!user.IsConnected || !Jobs.TryGetValue(userEntity, out var job)
            || !CustomEvents.TryRead<Demand>(payload, length, out var packet)
            || packet.Version != 1 || packet.Count < 1 || packet.Count > 32 || packet.Priority < 0 || packet.Priority > 1) return;
        double now = Now;
        // At most eight small requests per second per authenticated connection.
        if (now >= job.NextDemand) { job.DemandTokens = 4; job.NextDemand = now + .5; }
        if (job.DemandTokens-- <= 0) return;
        for (int i = 0; i < packet.Count; i++)
            if (job.Queue.ContainsPending(packet.Guids[i]))
            {
                var priority = packet.Priority == 0 ? DefinitionPriority.InventoryOrEquipment : DefinitionPriority.ActiveContainerOrVisible;
                job.Demands[packet.Guids[i]] = (priority, now + 1.25);
                job.Queue.Enqueue(packet.Guids[i], priority);
            }
    }
}
