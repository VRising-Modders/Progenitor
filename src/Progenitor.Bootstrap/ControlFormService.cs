using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Players.Forms;
using Stunlock.Core;
using Stunlock.Network;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-owned recovery for temporary player control. No collectible callbacks or plugin
/// objects are retained. Native mechanisms: research/player-forms.md. No form is qualified
/// merely by compiling this service or installing its barriers.
/// </summary>
public static class ControlFormService
{
    private const int Styx = 1112948824;
    private static readonly ControlLeasePolicy Policy = new();
    private static readonly Dictionary<long, Session> Live = new();
    private static readonly Dictionary<long, Session> Cleanup = new();
    private static readonly Dictionary<long, string> Finished = new();
    private static readonly Queue<long> FinishedOrder = new();
    private static readonly HashSet<int> Qualified = new(); // Deliberately empty pending live qualification.
    private static long _nextId;
    private static bool _saveArmed, _disconnectArmed, _destroyArmed, _tickArmed, _loadArmed;
    private static bool _insideBarrier;

    private sealed class Session
    {
        internal long Id;
        internal World World = null!;
        internal ulong Epoch;
        internal Entity User, Canonical, Body;
        internal ControlLease Lease = null!;
        internal double Deadline, SpawnDeadline;
        internal bool AddedImmortal;
        internal bool ChangedImmortal, Busy;
        internal Immortal OriginalImmortal;
        internal string? LastRecoveryError;
        internal string? LastSpawnWait;
        internal readonly List<Entity> Children = new();
    }

    public static bool IsReady => _saveArmed && _disconnectArmed && _destroyArmed && _tickArmed && _loadArmed;
    internal static void MarkLoadArmed() => _loadArmed = true;
    internal static void MarkSaveArmed() => _saveArmed = true;
    internal static void MarkDisconnectArmed() => _disconnectArmed = true;
    internal static void MarkDestroyArmed() => _destroyArmed = true;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Production entry: unqualified forms are refused before any entity mutation.</summary>
    public static bool TryBegin(World world, Entity user, int prefabGuid, double durationSeconds,
        string owner, out long leaseId, out string error)
    {
        leaseId = 0;
        if (!Qualified.Contains(prefabGuid))
        {
            error = "This form has not passed live save, death, disconnect and reload qualification.";
            return false;
        }
        return Begin(world, user, prefabGuid, durationSeconds, owner, out leaseId, out error);
    }

    // Test harness only. This is not exposed through the Core API or a normal Sample command.
    // Caller must use a disposable test save, a real connected client and the qualification script.
    internal static bool BeginQualification(World world, Entity user, int prefabGuid, double durationSeconds,
        string owner, out long leaseId, out string error) =>
        Begin(world, user, prefabGuid, durationSeconds, owner, out leaseId, out error);

    private static bool Begin(World world, Entity userEntity, int prefabGuid, double seconds,
        string owner, out long leaseId, out string error)
    {
        leaseId = 0;
        error = "Control recovery barriers are not installed, or this is not the game thread.";
        if (!ProcessHost.IsMainThread || !IsReady || _insideBarrier) return false;
        if (Live.Count + Cleanup.Count >= ControlLeasePolicy.MaximumLeases)
        { error = "Outstanding form or cleanup limit reached."; return false; }
        error = "Invalid form, owner, duration or server world.";
        if (prefabGuid != Styx || string.IsNullOrWhiteSpace(owner) || !double.IsFinite(seconds)
            || seconds <= 0 || seconds > 300 || world == null || !world.IsCreated
            || world.GetExistingSystemManaged<ServerBootstrapSystem>() == null) return false;
        Session? session = null;
        Entity spawned = Entity.Null;
        try
        {
            var em = world.EntityManager;
            if (!Has<User>(em, userEntity) || !Has<Controller>(em, userEntity))
                throw new InvalidOperationException("A real user with a controller is required.");
            var user = em.GetComponentData<User>(userEntity);
            var canonical = user.LocalCharacter.GetEntityOnServer();
            var control = em.GetComponentData<Controller>(userEntity);
            if (user.IsBot || !user.IsConnected || !Has<ConnectedUser>(em, userEntity)
                || !Has<PlayerCharacter>(em, canonical) || !Has<Health>(em, canonical)
                || em.GetComponentData<PlayerCharacter>(canonical).UserEntity != userEntity
                || em.GetComponentData<Health>(canonical).Value <= 0 || Has<Dead>(em, canonical)
                || Has<DestroyTag>(em, canonical) || control.Controlled.GetEntityOnServer() != canonical)
                throw new InvalidOperationException("The connected user must control their own living vampire.");
            if (!Has<ControlledBy>(em, canonical)
                || em.GetComponentData<ControlledBy>(canonical).Controller != userEntity)
                throw new InvalidOperationException("Canonical control backlink does not match.");
            foreach (var existing in Live.Values)
                if (existing.Epoch == world.SequenceNumber && existing.User == userEntity)
                    throw new InvalidOperationException("Return from the current form before selecting another.");
            var saving = world.GetExistingSystemManaged<SerializePersistenceSystemV2>();
            if (saving == null || saving.IsSaving) throw new InvalidOperationException("Save system unavailable or saving.");
            var loading = world.GetExistingSystemManaged<LoadPersistenceSystemV2>();
            if (loading == null || !loading.Initialized)
                throw new InvalidOperationException("Persistence initialization must finish before requesting a form.");
            var collection = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (collection == null || !collection._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(prefabGuid), out var prefab))
                throw new InvalidOperationException("Form prefab is unavailable.");
            // Snapshot and validate before Instantiate. The selected profile is an NPC, never a player clone.
            if (Has<PlayerCharacter>(em, prefab) || Has<User>(em, prefab)
                || !Has<AbilityBar_Shared>(em, prefab) || !Has<EntityInput>(em, prefab))
                throw new InvalidOperationException("Prefab does not match the audited NPC profile.");
            var position = em.GetComponentData<Translation>(canonical).Value;
            spawned = em.Instantiate(prefab);
            session = new Session { Id = checked(++_nextId), World = world, Epoch = world.SequenceNumber,
                User = userEntity, Canonical = canonical, Body = spawned, Deadline = Now + seconds, SpawnDeadline = Now + 5 };
            var identity = new ControlLeaseIdentity(session.Epoch, user.PlatformId, Id(canonical), Id(spawned), owner);
            if (!Policy.TryReserve(identity, seconds, ControlSafetyCapabilities.Required, out var lease, out error))
                throw new InvalidOperationException(error);
            session.Lease = lease!;
            Live.Add(session.Id, session); // Recovery owns the partial operation before preparation or transfer.
            leaseId = session.Id;
            Prepare(em, session, position);
            session.AddedImmortal = !Has<Immortal>(em, canonical);
            if (session.AddedImmortal) em.AddComponent(canonical, Type<Immortal>());
            session.OriginalImmortal = em.GetComponentData<Immortal>(canonical);
            session.ChangedImmortal = !session.OriginalImmortal.IsImmortal;
            if (session.ChangedImmortal) em.SetComponentData(canonical, new Immortal { IsImmortal = true });
            // NetworkId and native abilities are initialized by subsequent spawn systems.
            // Returning true here means a tracked pending lease, never proof of a completed switch.
            ProcessHost.Log?.LogInfo($"FORMS pending id={session.Id} user={userEntity} canonical={canonical} body={spawned}");
            error = "";
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            if (session?.Lease != null)
            {
                Policy.Fail(session.Lease, error);
                if (!TryReturn(session.Id, out var recovery)) error += "; recovery pending: " + recovery;
            }
            else if (spawned != Entity.Null && world != null && world.IsCreated)
            {
                try { world.EntityManager.DestroyEntity(spawned); }
                catch (Exception cleanup) { error += "; unclaimed spawn cleanup failed: " + cleanup.Message; }
            }
            return false;
        }
    }

    private static void Prepare(EntityManager em, Session session, float3 position)
    {
        var body = session.Body;
        session.Children.Add(body);
        em.AddComponent(body, Type<PersistenceV2.DontSaveEntity>());
        if (Has<LinkedEntityGroup>(em, body))
        {
            var linked = em.GetBuffer<LinkedEntityGroup>(body);
            var members = new Entity[linked.Length];
            for (int i = 0; i < members.Length; i++) members[i] = linked[i].Value;
            foreach (var member in members)
            {
                if (!session.Children.Contains(member)) session.Children.Add(member);
                if (em.Exists(member) && !Has<PersistenceV2.DontSaveEntity>(em, member))
                    em.AddComponent(member, Type<PersistenceV2.DontSaveEntity>());
            }
        }
        em.SetComponentData(body, new Translation { Value = position });
        if (Has<LastTranslation>(em, body)) em.SetComponentData(body, new LastTranslation { Value = position });
        if (Has<SpawnTransform>(em, body))
        { var spawn = em.GetComponentData<SpawnTransform>(body); spawn.Position = position; em.SetComponentData(body, spawn); }
        // Initial Styx profile: reject unexpected lifetime machinery instead of stripping by name.
        if (Has<LifeTime>(em, body) || Has<DestroyAfterDuration>(em, body) || Has<DestroyOnSpawn>(em, body))
            throw new InvalidOperationException("Unexpected form lifetime requires profile review.");
        ConfigureFormRewards(em, body);
        // Immortal is only damage protection. Destruction barriers and recovery are still required.
        if (!Has<Immortal>(em, body)) em.AddComponent(body, Type<Immortal>());
        em.SetComponentData(body, new Immortal { IsImmortal = true });
    }

    public static bool TryReturn(long leaseId, out string error)
    {
        error = "Return must run on the game thread.";
        if (!ProcessHost.IsMainThread) return false;
        if (!Live.TryGetValue(leaseId, out var s))
        {
            bool restored = Cleanup.ContainsKey(leaseId)
                || (Finished.TryGetValue(leaseId, out var state) && state.StartsWith("Restored", StringComparison.Ordinal));
            error = restored ? "" : "No verified restoration record exists for this lease; inspect its status.";
            return restored;
        }
        if (s.Busy) { error = "A native control transition is already in progress."; return false; }
        s.Busy = true;
        Policy.BeginRestore(s.Lease);
        try
        {
            if (!s.World.IsCreated || s.World.SequenceNumber != s.Epoch)
                throw new InvalidOperationException("Original world no longer exists; no other world will be mutated.");
            var em = s.World.EntityManager;
            if (!Has<User>(em, s.User) || !Has<Controller>(em, s.User) || !Has<PlayerCharacter>(em, s.Canonical))
                throw new InvalidOperationException("Canonical user/body missing; refusing body destruction and save.");
            if (IsDestroying(em, s.Canonical))
                throw new InvalidOperationException("Canonical vampire is pending destruction; recovery cannot be declared complete.");
            var user = em.GetComponentData<User>(s.User);
            if (user.PlatformId != s.Lease.Identity.User || user.LocalCharacter.GetEntityOnServer() != s.Canonical
                || em.GetComponentData<PlayerCharacter>(s.Canonical).UserEntity != s.User)
                throw new InvalidOperationException("Canonical identity changed; refusing to overwrite another relationship.");
            var controlled = em.GetComponentData<Controller>(s.User).Controlled.GetEntityOnServer();
            if (controlled != s.Body && controlled != s.Canonical && em.Exists(controlled))
                throw new InvalidOperationException("Control changed outside this lease; recovery requires review.");
            // Native rebinding removes backlinks. Check ownership BEFORE calling it.
            if ((Has<ControlledBy>(em, s.Body) && em.GetComponentData<ControlledBy>(s.Body).Controller != s.User)
                || (Has<ControlledBy>(em, s.Canonical) && em.GetComponentData<ControlledBy>(s.Canonical).Controller != s.User))
                throw new InvalidOperationException("A body backlink belongs to another controller.");
            EnsureNoOtherController(em, s);
            // Self-to-self rebind is an emergency path when the borrowed body has already disappeared.
            // Both native entity arguments remain valid. Never feed a destroyed entity to ControlUnit.
            if (controlled == s.Body && em.Exists(s.Body) && Has<ConnectedUser>(em, s.User))
            {
                if (!CanUseNetworkTransition(em, s.User, s.Body, s.Canonical))
                    throw new InvalidOperationException("Native return network prerequisites are incomplete; recovery retained.");
                s.World.GetExistingSystemManaged<DebugEventsSystem>().ControlUnit(
                    new FromCharacter { User = s.User, Character = s.Canonical },
                    new ControlDebugEvent { EntityTarget = s.Canonical });
            }
            else if (controlled != s.Canonical || !Has<ControlledBy>(em, s.Canonical))
                ReactToPlayerCharacterSpawnSystem.UpdateControl(em, s.User,
                    controlled == s.Body && em.Exists(s.Body) ? s.Body : s.Canonical, s.Canonical, ServerFrame(em));
            if (Has<ControlledBy>(em, s.Body))
            {
                if (em.GetComponentData<ControlledBy>(s.Body).Controller != s.User)
                    throw new InvalidOperationException("Borrowed body now belongs to another controller.");
                Remove<ControlledBy>(em, s.Body);
            }
            Remove<IsConnected>(em, s.Body);
            var restored = em.GetComponentData<Controller>(s.User);
            if (restored.Controlled.GetEntityOnServer() != s.Canonical)
                throw new InvalidOperationException("Native return did not restore the canonical body.");
            restored.PreviouslyControlled = Entity.Null; // Never serialize a reference to the disposable body.
            em.SetComponentData(s.User, restored);
            bool backlink = Has<ControlledBy>(em, s.Canonical)
                && em.GetComponentData<ControlledBy>(s.Canonical).Controller == s.User;
            if (!backlink || Has<ControlledBy>(em, s.Body) || restored.PreviouslyControlled == s.Body)
                throw new InvalidOperationException("Return readback failed.");
            if (s.AddedImmortal) Remove<Immortal>(em, s.Canonical);
            else if (s.ChangedImmortal && Has<Immortal>(em, s.Canonical)
                && em.GetComponentData<Immortal>(s.Canonical).IsImmortal)
                em.SetComponentData(s.Canonical, s.OriginalImmortal);
            // Only after all control links are restored may this service delete its owned clone.
            if (!Policy.ObserveRestored(s.Lease, Id(user.LocalCharacter.GetEntityOnServer()),
                    Id(restored.Controlled.GetEntityOnServer()), true, true))
                throw new InvalidOperationException("Return policy verification failed.");
            Live.Remove(leaseId);
            Cleanup[leaseId] = s;
            RecordFinished(leaseId, "Restored; owned-body cleanup pending");
            PollCleanup(s.World);
            ProcessHost.Log?.LogInfo($"FORMS restored id={leaseId} canonical={s.Canonical}");
            error = "";
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Policy.Fail(s.Lease, error);
            if (s.LastRecoveryError != error)
            {
                ProcessHost.Log?.LogError($"FORMS recovery retained id={leaseId}: {error}");
                s.LastRecoveryError = error;
            }
            return false;
        }
        finally { s.Busy = false; }
    }

    public static string Describe(long leaseId) => Live.TryGetValue(leaseId, out var s)
        ? $"{s.Lease.Snapshot.State}: {s.Lease.Snapshot.Failure}"
        : Finished.TryGetValue(leaseId, out var state) ? state : "Unknown or expired lease record";

    internal static bool BeforeSave(World world)
    {
        if (Live.Count == 0 && Cleanup.Count == 0) return true;
        if (_insideBarrier) return false;
        _insideBarrier = true;
        try
        {
            bool ok = ReturnAll(world);
            PollCleanup(world);
            // Destruction is deferred. Do not serialize while known owned entities can still
            // be referenced by inventory, ability or controller data in this world.
            foreach (var s in Cleanup.Values)
                if (s.Epoch == world.SequenceNumber) ok = false;
            if (!ok) ProcessHost.Log?.LogError("FORMS save refused: canonical control recovery has not verified.");
            return ok;
        }
        catch (Exception e) { ProcessHost.Log?.LogError("FORMS save barrier: " + e); return false; }
        finally { _insideBarrier = false; }
    }

    internal static bool BeforeUnload()
    {
        try { return ReturnAll(); }
        catch (Exception e) { ProcessHost.Log?.LogError("FORMS unload barrier: " + e); return false; }
    }
    // Persistence can rebuild entities within the same World/SequenceNumber. Block the
    // outer load request/update while any recorded entity could be reused by a load.
    internal static bool BeforeLoad(World world)
    {
        if (Live.Count == 0 && Cleanup.Count == 0) return true;
        try
        {
            foreach (var s in Live.Values) if (s.Epoch == world.SequenceNumber) return false;
            foreach (var s in Cleanup.Values) if (s.Epoch == world.SequenceNumber) return false;
            return true;
        }
        catch (Exception e) { ProcessHost.Log?.LogError("FORMS load barrier: " + e.Message); return false; }
    }
    private static bool ReturnAll(World? world = null)
    {
        bool ok = true;
        RetireEndedWorlds();
        foreach (var s in new List<Session>(Live.Values))
            if ((world == null || world.SequenceNumber == s.Epoch) && !TryReturn(s.Id, out _)) ok = false;
        return ok;
    }

    internal static void BeforeDisconnect(ServerBootstrapSystem bootstrap, NetConnectionId connection)
    {
        if (Live.Count == 0) return;
        try
        {
        var clients = bootstrap._ApprovedUsersLookup;
        if (clients == null) return;
        for (int i = 0; i < clients.Length; i++)
        {
            var client = clients[i];
            if (client == null || !client.Exists || !client.NetConnectionId.Equals(connection)) continue;
            foreach (var s in new List<Session>(Live.Values))
                if (s.Epoch == bootstrap.World.SequenceNumber && s.User == client.UserEntity) TryReturn(s.Id, out _);
            break;
        }
        }
        catch (Exception e) { ProcessHost.Log?.LogError("FORMS disconnect recovery: " + e); }
    }

    internal static void Tick()
    {
        _tickArmed = true;
        Poll();
        PollCleanup();
    }

    // Called before managed systems, including destruction-reaction and fixed-step groups.
    internal static bool BeforeSystem(SystemBase system)
    {
        if (Live.Count == 0) return true;
        try
        {
        string name = system.GetIl2CppType().Name;
        if (name == "ReactToDestroyTagGroup" || name == "DestroyGroup_Server"
            || name == "FixedStepSimulationSystemGroup" || name == "StartSimulationGroup")
            return Poll(system.World);
        return true;
        }
        catch (Exception e) { ProcessHost.Log?.LogError("FORMS destruction barrier: " + e); return false; }
    }

    private static bool Poll(World? scope = null)
    {
        bool ok = true;
        RetireEndedWorlds();
        foreach (var s in new List<Session>(Live.Values))
        {
            if (scope != null && s.Epoch != scope.SequenceNumber) continue;
            if (s.Busy) continue;
            try
            {
                if (!s.World.IsCreated) { ok &= TryReturn(s.Id, out _); continue; }
                var em = s.World.EntityManager;
                bool canonicalUnchanged = Has<User>(em, s.User) && Has<Controller>(em, s.User)
                    && em.GetComponentData<User>(s.User).IsConnected
                    && em.GetComponentData<User>(s.User).PlatformId == s.Lease.Identity.User
                    && em.GetComponentData<User>(s.User).LocalCharacter.GetEntityOnServer() == s.Canonical
                    && Has<PlayerCharacter>(em, s.Canonical)
                    && em.GetComponentData<PlayerCharacter>(s.Canonical).UserEntity == s.User
                    && IsLiving(em, s.Canonical);
                if (!canonicalUnchanged || Now >= s.Deadline) { ok &= TryReturn(s.Id, out _); continue; }
                if (s.Lease.Snapshot.State == ControlLeaseState.Reserved)
                {
                    bool spawnIncomplete = Has<SpawnTag>(em, s.Body) || Has<Disabled>(em, s.Body);
                    float health = Has<Health>(em, s.Body) ? em.GetComponentData<Health>(s.Body).Value : 0;
                    bool networkReady = !spawnIncomplete && CanUseNetworkTransition(em, s.User, s.Canonical, s.Body);
                    var readiness = ControlLeasePolicy.DecideSpawnReadiness(em.Exists(s.Body), IsDestroying(em, s.Body),
                        Has<Dead>(em, s.Body), spawnIncomplete, networkReady, health, Now >= s.SpawnDeadline);
                    if (readiness == ControlSpawnReadiness.Wait)
                    {
                        string observation = $"spawning={spawnIncomplete} health={health} networkReady={networkReady}";
                        if (s.LastSpawnWait != observation)
                        { s.LastSpawnWait = observation; ProcessHost.Log?.LogInfo($"FORMS waiting id={s.Id} {observation}"); }
                        continue;
                    }
                    if (readiness == ControlSpawnReadiness.Return)
                    {
                        ProcessHost.Log?.LogWarning($"FORMS spawn refused id={s.Id} spawning={spawnIncomplete} health={health} networkReady={networkReady} deadline={Now >= s.SpawnDeadline}");
                        ok &= TryReturn(s.Id, out _);
                        continue;
                    }
                    if (em.GetComponentData<Controller>(s.User).Controlled.GetEntityOnServer() != s.Canonical
                        || !Has<ControlledBy>(em, s.Canonical)
                        || em.GetComponentData<ControlledBy>(s.Canonical).Controller != s.User
                        || Has<ControlledBy>(em, s.Body) || !Has<ConnectedUser>(em, s.User))
                        throw new InvalidOperationException("Control graph changed while the form was spawning.");
                    EnsureNoOtherController(em, s);
                    ConfigureFormRewards(em, s.Body); // Spawn initialization may rewrite defaults.
                    s.Busy = true;
                    try
                    {
                    s.World.GetExistingSystemManaged<DebugEventsSystem>().ControlUnit(
                        new FromCharacter { User = s.User, Character = s.Canonical },
                        new ControlDebugEvent { EntityTarget = s.Body });
                    }
                    finally { s.Busy = false; }
                    var actual = em.GetComponentData<Controller>(s.User).Controlled.GetEntityOnServer();
                    if (!Has<ControlledBy>(em, s.Body) || em.GetComponentData<ControlledBy>(s.Body).Controller != s.User
                        || Has<ControlledBy>(em, s.Canonical))
                        throw new InvalidOperationException("Native activation backlinks did not verify.");
                    if (!Policy.ObserveActivated(s.Lease, Id(em.GetComponentData<User>(s.User).LocalCharacter.GetEntityOnServer()), Id(actual)))
                    { ok &= TryReturn(s.Id, out _); continue; }
                    ProcessHost.Log?.LogInfo($"FORMS active id={s.Id} canonical={s.Canonical} body={s.Body}");
                    continue;
                }
                if (s.Lease.Snapshot.State != ControlLeaseState.Active || Now >= s.Deadline
                    || !Has<User>(em, s.User) || !em.GetComponentData<User>(s.User).IsConnected
                    || !IsLiving(em, s.Body) || !IsLiving(em, s.Canonical)
                    || em.GetComponentData<Controller>(s.User).Controlled.GetEntityOnServer() != s.Body
                    || !Has<ControlledBy>(em, s.Body)
                    || em.GetComponentData<ControlledBy>(s.Body).Controller != s.User
                    || Has<ControlledBy>(em, s.Canonical))
                    ok &= TryReturn(s.Id, out _);
            }
            catch (Exception e) { Policy.Fail(s.Lease, e.Message); ok = false; }
        }
        return ok;
    }

    private static void EnsureNoOtherController(EntityManager em, Session s)
    {
        var query = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { Type<Controller>() },
            Options = EntityQueryOptions.IncludeDisabledEntities });
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (entities[i] == s.User) continue;
                var target = em.GetComponentData<Controller>(entities[i]).Controlled.GetEntityOnServer();
                if (target == s.Body || target == s.Canonical)
                    throw new InvalidOperationException("Another user controls a leased body.");
            }
        }
        finally { entities.Dispose(); query.Dispose(); }
    }

    private static void RetireEndedWorlds()
    {
        foreach (var s in new List<Session>(Live.Values))
        {
            if (s.World.IsCreated) continue;
            Policy.ObserveWorldEnded(s.Epoch);
            Live.Remove(s.Id);
            RecordFinished(s.Id, "World ended; no replacement world was mutated");
        }
    }

    private static void PollCleanup(World? scope = null)
    {
        foreach (var s in new List<Session>(Cleanup.Values))
        {
            if (scope != null && scope.SequenceNumber != s.Epoch) continue;
            try
            {
                if (!s.World.IsCreated) { Cleanup.Remove(s.Id); RecordFinished(s.Id, "World ended"); continue; }
                var em = s.World.EntityManager;
                // Controller references count even when a backlink has already been removed.
                // Refuse the entire cleanup batch before adding any destruction tag.
                EnsureNoCleanupController(em, s);
                bool remaining = false;
                foreach (var entity in s.Children)
                {
                    if (!em.Exists(entity)) continue;
                    remaining = true;
                    // Another owner taking an entity is a cleanup conflict, never a reason to kill it.
                    if (Has<ControlledBy>(em, entity)) continue;
                    if (!Has<DestroyTag>(em, entity)) DestroyUtility.Destroy(em, entity, DestroyDebugReason.TryRemoveBuff);
                }
                if (!remaining) { Cleanup.Remove(s.Id); RecordFinished(s.Id, "Restored; owned-body cleanup verified"); }
            }
            catch (Exception e) { ProcessHost.Log?.LogWarning("FORMS cleanup retained: " + e.Message); }
        }
    }

    private static void RecordFinished(long id, string state)
    {
        if (!Finished.ContainsKey(id)) FinishedOrder.Enqueue(id);
        Finished[id] = state;
        while (FinishedOrder.Count > 128) Finished.Remove(FinishedOrder.Dequeue());
    }

    private static void EnsureNoCleanupController(EntityManager em, Session s)
    {
        var query = em.CreateEntityQuery(new EntityQueryDesc { All = new[] { Type<Controller>() },
            Options = EntityQueryOptions.IncludeDisabledEntities });
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var controller = em.GetComponentData<Controller>(entities[i]);
                    if (s.Children.Contains(controller.Controlled.GetEntityOnServer())
                        || s.Children.Contains(controller.PreviouslyControlled))
                        throw new InvalidOperationException("A controller still references an owned cleanup entity.");
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
    }

    private static bool IsLiving(EntityManager em, Entity entity) => Has<Health>(em, entity)
        && em.GetComponentData<Health>(entity).Value > 0 && !Has<Dead>(em, entity) && !IsDestroying(em, entity);

    private static bool IsDestroying(EntityManager em, Entity entity) => Has<DestroyTag>(em, entity)
        || (Has<DestroyState>(em, entity) && em.GetComponentData<DestroyState>(entity).Value != DestroyStateEnum.NotDestroyed);

    private static bool CanUseNetworkTransition(EntityManager em, Entity user, Entity previous, Entity target)
    {
        if (!Has<ConnectedUser>(em, user) || !Has<UserEntityNetworkState>(em, user)
            || !Has<NetworkId>(em, previous) || !Has<NetworkId>(em, target)) return false;
        var oldId = em.GetComponentData<NetworkId>(previous);
        var newId = em.GetComponentData<NetworkId>(target);
        int length = em.GetBuffer<UserEntityNetworkState>(user).Length;
        return oldId.IsValid && newId.IsValid && oldId.Normal_Index > 0 && newId.Normal_Index > 0
            && oldId.Normal_Index < length && newId.Normal_Index < length;
    }

    private static void ConfigureFormRewards(EntityManager em, Entity body)
    {
        // Preserve the prefab's networked component schema. Both blood-source components are
        // networked; the fixture reported missing snapshot components after their removal.
        // These field/buffer controls still require explicit feed/reward qualification.
        if (Has<BloodConsumeSource>(em, body))
        {
            var blood = em.GetComponentData<BloodConsumeSource>(body);
            blood.CanBeConsumed = false;
            em.SetComponentData(body, blood);
        }
        if (Has<ProjectM.Shared.DropTableOnDeath>(em, body))
        {
            var drop = em.GetComponentData<ProjectM.Shared.DropTableOnDeath>(body);
            drop.Dropped = true;
            em.SetComponentData(body, drop);
        }
        if (Has<ProjectM.Shared.DropTableBuffer>(em, body))
            em.GetBuffer<ProjectM.Shared.DropTableBuffer>(body).Clear();
    }

    private static int ServerFrame(EntityManager em)
    {
        var query = em.CreateEntityQuery(new ComponentType[] { Type<ServerTime>() });
        try { return em.GetComponentData<ServerTime>(query.GetSingletonEntity()).FrameCount; }
        finally { query.Dispose(); }
    }
    private static ControlBody Id(Entity entity) => new(entity.Index, entity.Version);
    private static ComponentType Type<T>() => ComponentType.FromTypeIndex(TypeManager.GetTypeIndex(Il2CppType.Of<T>()));
    private static bool Has<T>(EntityManager em, Entity entity) => entity != Entity.Null && em.Exists(entity) && em.HasComponent(entity, Type<T>());
    private static void Remove<T>(EntityManager em, Entity entity) { if (Has<T>(em, entity)) em.RemoveComponent(entity, Type<T>()); }
}
