using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Server Dev op <c>ghost</c>: create, list, or destroy User-only entities with no
/// <c>NetConnectionId</c>. Live-verified 2026-09-02 (evidence §8): a 168-byte User blob
/// copied from a real user, patched at the FieldOffsets below,
/// is accepted by the world, listed by
/// <c>users</c>, absent from the approved-user table, and is a valid
/// <see cref="Progenitor.Commands.CommandDispatcher.TryExecute"/> speaker.
/// Create now also attaches <see cref="ConnectedUser"/> (opt-out via <c>connectedUser:false</c>)
/// so <c>ChatMessageSystem._ConnectedUsersQuery</c> selects the ghost as a chat recipient
/// (evidence §14: without it, Global chat produced no <c>SendPacketToUser</c>).
/// Risk: a <see cref="ConnectedUser"/> with no live connection may be dereferenced by
/// snapshot/ghosting netcode each frame. The live run is expected to log
/// <c>FailedNotConnected</c> per send; abort on any exception or frame-time spike.
/// </summary>
public static unsafe class GhostOps
{
    /// <summary>First <c>User.Index</c> assigned to a ghost; real players sit well below this.</summary>
    public const int IndexFloor = 10001;

    /// <summary>
    /// First synthetic PlatformId. Steam IDs are 7656119…; anything in
    /// <c>[PlatformIdBase, PlatformIdBase + PlatformIdSpan)</c> is treated as a leftover
    /// ghost on destroy so a crash between create and Unload cannot persist a fake user.
    /// Extra-client fake ids (<see cref="ExtraInstanceAuth.PlatformIdBase"/>..<c>+99</c>)
    /// are excluded by <see cref="IsGhostPlatformId"/> so this sweep never destroys a
    /// live extra-client User.
    /// </summary>
    public const ulong PlatformIdBase = 90000000000000001UL;

    /// <summary>How many PlatformIds above <see cref="PlatformIdBase"/> count as ghosts.</summary>
    public const int PlatformIdSpan = 10_000;

    private const int MaxCreate = 32;
    private const int MinUserBytes = 165;

    // FieldOffsets from the ghost-users spike (inferred from stubs; write verified live).
    private const int OffIndex = 0;
    private const int OffGeneration = 4;
    private const int OffBufferPositionBits = 8;
    private const int OffLastFullyAckedFrame = 12;
    private const int OffLocalCharacter = 16;
    private const int OffClanEntity = 28;
    private const int OffPlatformId = 40;
    private const int OffTimeLastConnected = 48;
    private const int OffTimeLastInput = 56;
    private const int OffCharacterName = 64;
    private const int OffAfkKickWarningSent = 148;
    private const int OffIsBot = 149;
    private const int OffIsAdmin = 150;
    private const int OffIsConnected = 151;
    private const int OffFirstTimeConnected = 164;

    // ConnectedUser (the IL viewer, ProjectM.Shared.dll, inferred from stubs — only field):
    //   [FieldOffset(0)] public byte UserIndex;
    // No UserEntity, no NetConnectionId, no bools. Size 1. User.Index (ghosts: 10001+)
    // does not fit; this byte is the approved-user slot the chat job addresses.
    private const int OffConnectedUserIndex = 0;
    private const int ConnectedUserStubSize = 1;

    private static readonly object Gate = new();
    private static readonly List<TrackedGhost> Tracked = new();

    /// <summary>
    /// Dispatch <c>create</c> / <c>destroy</c> / <c>list</c>. Server, main thread. A missing
    /// action is an error — listing by accident after a typo would hide a failed create.
    /// </summary>
    public static object Run(JsonElement args)
    {
        try
        {
            if (!Progenitor.Plugin.IsServerProcess)
                throw new InvalidOperationException("ghost runs on the server side only");

            var action = GetString(args, "action");
            if (string.IsNullOrWhiteSpace(action))
                throw new ArgumentException("args.action is required (create, destroy, or list)");

            return action.Trim().ToLowerInvariant() switch
            {
                "create" => Create(args),
                "destroy" => DestroyAll(reason: "op"),
                "list" => List(),
                _ => throw new ArgumentException($"args.action must be create, destroy, or list (got '{action}')"),
            };
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"ghost op threw: {e}");
            throw new InvalidOperationException($"ghost op failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// Destroy every tracked ghost and any leftover User whose PlatformId is in the ghost
    /// range. Called from the Dev plugin <c>Unload</c> so a hot-reload cannot leave a fake
    /// connected user sitting until autosave.
    /// </summary>
    internal static void Shutdown()
    {
        try
        {
            DestroyAll(reason: "unload");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ghost Shutdown: {e.Message}");
            lock (Gate) Tracked.Clear();
        }
    }

    private static object Create(JsonElement args)
    {
        var world = RequireServerWorld();
        var em = world.EntityManager;

        int count = Math.Clamp(GetInt(args, "count", 1), 1, MaxCreate);
        var prefix = GetString(args, "namePrefix");
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "Ghost";
        prefix = prefix.Trim();
        if (prefix.Length > 48)
            throw new ArgumentException("args.namePrefix is longer than 48 characters");
        bool admin = GetBool(args, "admin", false);
        // Default true: without ConnectedUser the chat job skips the ghost (evidence §14).
        bool addConnectedUser = GetBool(args, "connectedUser", false) /* verified live 2026-09-02: a ConnectedUser without a live connection hard-crashes the server on the next frame (evidence §15); opt in only for a crash test */;

        var usedIndexes = new HashSet<int>();
        var usedPlatforms = new HashSet<ulong>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectUsedIdentity(em, usedIndexes, usedPlatforms, usedNames);

        if (!TryFindTemplate(em, out var template, out var templateName))
            Plugin.Logger?.LogWarning("ghost: no existing User to copy; writing a zeroed blob");

        var created = new List<object>(count);
        for (int i = 0; i < count; i++)
        {
            int index = NextFreeIndex(usedIndexes);
            int ordinal = NextFreeOrdinal(prefix, usedNames, usedPlatforms);
            ulong platformId = PlatformIdBase + (ulong)(ordinal - 1);
            string name = prefix + ordinal.ToString(CultureInfo.InvariantCulture);

            var ghost = em.CreateEntity();
            try
            {
                WriteUserRaw(em, ghost, template, index, platformId, name, admin, connected: true);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"ghost: write failed for {name}: {e}");
                try { if (em.Exists(ghost)) em.DestroyEntity(ghost); }
                catch (Exception destroyEx) { Plugin.Logger?.LogWarning($"ghost: rollback destroy: {destroyEx.Message}"); }
                throw new InvalidOperationException($"failed to write User onto ghost '{name}': {e.Message}", e);
            }

            User readback;
            try { readback = em.GetComponentData<User>(ghost); }
            catch (Exception e)
            {
                try { if (em.Exists(ghost)) em.DestroyEntity(ghost); } catch { /* best-effort */ }
                throw new InvalidOperationException($"ghost '{name}' was written but GetComponentData<User> failed: {e.Message}", e);
            }

            if (addConnectedUser)
            {
                try
                {
                    WriteConnectedUser(em, ghost, readback.Index);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"ghost: ConnectedUser write failed for {name}: {e}");
                    try { if (em.Exists(ghost)) em.DestroyEntity(ghost); }
                    catch (Exception destroyEx) { Plugin.Logger?.LogWarning($"ghost: rollback destroy: {destroyEx.Message}"); }
                    throw new InvalidOperationException($"failed to write ConnectedUser onto ghost '{name}': {e.Message}", e);
                }
            }

            usedIndexes.Add(readback.Index);
            usedPlatforms.Add(readback.PlatformId);
            usedNames.Add(SafeName(readback));

            lock (Gate) Tracked.Add(new TrackedGhost(ghost));

            var described = Describe(em, ghost, readback);
            Plugin.Logger?.LogInfo(
                $"ghost created entity={FormatEntity(ghost)} Index={readback.Index} name='{SafeName(readback)}' " +
                $"platform={readback.PlatformId} admin={readback.IsAdmin} connectedUser={addConnectedUser} " +
                $"template={FormatEntity(template)}");

            created.Add(described);
        }

        return new
        {
            ok = true,
            action = "create",
            count = created.Count,
            connectedUser = addConnectedUser,
            template = template == Entity.Null ? null : new { entity = FormatEntity(template), name = templateName },
            ghosts = created,
        };
    }

    private static object DestroyAll(string reason)
    {
        var world = Progenitor.Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            int pending;
            lock (Gate)
            {
                pending = Tracked.Count;
                Tracked.Clear();
            }
            return new { ok = true, action = "destroy", destroyed = 0, leftovers = 0, droppedTracked = pending, note = "no server world" };
        }

        var em = world.EntityManager;
        var toDestroy = new List<Entity>();
        var seen = new HashSet<Entity>();

        lock (Gate)
        {
            foreach (var t in Tracked)
            {
                if (t.Entity == Entity.Null) continue;
                try
                {
                    if (em.Exists(t.Entity) && seen.Add(t.Entity))
                        toDestroy.Add(t.Entity);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"ghost: exists check on tracked {FormatEntity(t.Entity)}: {e.Message}");
                }
            }
            Tracked.Clear();
        }

        int leftovers = 0;
        foreach (var e in AllUsers(em))
        {
            User u;
            try { u = em.GetComponentData<User>(e); }
            catch { continue; }
            if (!IsGhostPlatformId(u.PlatformId)) continue;
            if (seen.Add(e))
            {
                toDestroy.Add(e);
                leftovers++;
            }
        }

        int destroyed = 0;
        foreach (var entity in toDestroy)
        {
            if (TryDestroyGhostEntity(em, entity))
                destroyed++;
        }

        Plugin.Logger?.LogInfo($"ghost destroy ({reason}): destroyed={destroyed} leftovers={leftovers}");
        return new
        {
            ok = true,
            action = "destroy",
            destroyed,
            leftovers,
            ghosts = Array.Empty<object>(),
        };
    }

    private static object List()
    {
        var world = Progenitor.Worlds.Server;
        if (world == null || !world.IsCreated)
            return new { ok = true, action = "list", count = 0, ghosts = Array.Empty<object>(), note = "no server world" };

        var em = world.EntityManager;
        var rows = new List<object>();
        var still = new List<TrackedGhost>();
        lock (Gate)
        {
            foreach (var t in Tracked)
            {
                try
                {
                    if (!em.Exists(t.Entity)) continue;
                    var u = em.GetComponentData<User>(t.Entity);
                    still.Add(new TrackedGhost(t.Entity));
                    rows.Add(Describe(em, t.Entity, u));
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"ghost list: {FormatEntity(t.Entity)}: {e.Message}");
                }
            }
            Tracked.Clear();
            Tracked.AddRange(still);
        }

        return new { ok = true, action = "list", count = rows.Count, ghosts = rows };
    }

    /// <summary>
    /// User has bool fields so it is not C# unmanaged: toolkit <c>Write&lt;T&gt;</c> will not
    /// compile. <c>Add&lt;User&gt;</c> is non-generic AddComponent. Bytes go through
    /// SetComponentDataRaw at the FieldOffsets above.
    /// Live: TypeSize reads 0 through this interop; Marshal.SizeOf is 168.
    /// </summary>
    private static void WriteUserRaw(EntityManager em, Entity ghost, Entity template, int index, ulong platformId, string name, bool admin, bool connected)
    {
        var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadWrite);
        var typeIndex = ComponentIndex.IndexOf<User>();
        int ecsSize = 0;
        try { ecsSize = TypeManager.GetTypeInfo(typeIndex).TypeSize; }
        catch (Exception e) { Plugin.Logger?.LogWarning($"ghost TypeSize read failed: {e.Message}"); }

        int marshalSize = Marshal.SizeOf<User>();
        int size = ecsSize > 0 ? ecsSize : marshalSize;
        if (size < MinUserBytes)
            throw new InvalidOperationException($"User size {size} is smaller than FieldOffset 164; refusing to write.");

        var buf = new byte[size];
        if (template != Entity.Null && em.Exists(template) && em.HasComponent(template, userType))
        {
            byte* src = (byte*)em.GetComponentDataRawRO(template, typeIndex);
            if (src == null)
                throw new InvalidOperationException($"GetComponentDataRawRO({FormatEntity(template)}, User) was null");
            Marshal.Copy((IntPtr)src, buf, 0, size);
        }

        PutI32(buf, OffIndex, index);
        PutI32(buf, OffGeneration, 0);
        PutI32(buf, OffBufferPositionBits, 0);
        PutI32(buf, OffLastFullyAckedFrame, 0);
        ClearNetworkedEntity(buf, OffLocalCharacter);
        ClearNetworkedEntity(buf, OffClanEntity);
        PutU64(buf, OffPlatformId, platformId);
        long now = DateTime.UtcNow.Ticks;
        PutI64(buf, OffTimeLastConnected, now);
        PutI64(buf, OffTimeLastInput, now);
        PutFixedString64(buf, OffCharacterName, name);
        buf[OffAfkKickWarningSent] = 0;
        buf[OffIsBot] = 1;
        buf[OffIsAdmin] = admin ? (byte)1 : (byte)0;
        buf[OffIsConnected] = connected ? (byte)1 : (byte)0;
        if (size > OffFirstTimeConnected) buf[OffFirstTimeConnected] = 0;

        ghost.Add<User>(em);
        fixed (byte* ptr = buf)
            em.SetComponentDataRaw(ghost, typeIndex, ptr, size);
    }

    /// <summary>
    /// Attaches <see cref="ConnectedUser"/> so the ghost is visible to
    /// <c>ChatMessageSystem._ConnectedUsersQuery</c>. Layout from the IL viewer on
    /// <c>ProjectM.Shared.dll</c> (inferred from stubs, not live-verified):
    /// <c>[FieldOffset(0)] byte UserIndex</c> — no <c>UserEntity</c>, no connection
    /// id, no bools. Not the copy-blob case unless ECS TypeSize is larger than the
    /// 1-byte stub (then copy a live <see cref="ConnectedUser"/> and patch byte 0;
    /// if the template user is offline and nobody else has the component, zeros
    /// plus <c>UserIndex</c>). <c>User.Index</c> 10001 does not fit in this byte
    /// (approved-user slot); a free slot is chosen, preferring the low 8 bits of
    /// the ghost's <c>User.Index</c>. Write goes through <see cref="EntityExtensions.Add{T}(Entity, EntityManager)"/>
    /// plus <c>SetComponentDataRaw</c> — never <c>GetSingleton</c>, never generic
    /// interop over the value type.
    /// </summary>
    private static byte WriteConnectedUser(EntityManager em, Entity ghost, int userIndex)
    {
        byte slot = AllocateConnectedUserIndex(em, userIndex);
        var typeIndex = ComponentIndex.IndexOf<ConnectedUser>();
        int ecsSize = 0;
        try { ecsSize = TypeManager.GetTypeInfo(typeIndex).TypeSize; }
        catch (Exception e) { Plugin.Logger?.LogWarning($"ghost ConnectedUser TypeSize read failed: {e.Message}"); }

        int marshalSize = Marshal.SizeOf<ConnectedUser>();
        int size = ecsSize > 0 ? ecsSize : Math.Max(marshalSize, ConnectedUserStubSize);
        if (size < ConnectedUserStubSize)
            throw new InvalidOperationException($"ConnectedUser size {size} cannot hold UserIndex at offset {OffConnectedUserIndex}");

        var buf = new byte[size];
        if (size > ConnectedUserStubSize)
        {
            // Stub is 1 byte. A larger TypeSize means hidden fields the interop omitted.
            if (TryCopyConnectedUserBlob(em, buf, typeIndex, size))
                Plugin.Logger?.LogInfo($"ghost: copied {size}-byte ConnectedUser blob from a live user, patching UserIndex={slot}");
            else
                Plugin.Logger?.LogWarning($"ghost: no live ConnectedUser to copy ({size}-byte TypeSize, stub is {ConnectedUserStubSize} byte); writing zeros + UserIndex={slot}");
        }

        buf[OffConnectedUserIndex] = slot;

        ghost.Add<ConnectedUser>(em);
        fixed (byte* ptr = buf)
            em.SetComponentDataRaw(ghost, typeIndex, ptr, size);
        return slot;
    }

    /// <summary>
    /// Pick a <see cref="ConnectedUser.UserIndex"/> that no existing component uses.
    /// Prefers <c>(byte)userIndex</c> so Ghost1 (10001) lands on slot 17 when free.
    /// </summary>
    private static byte AllocateConnectedUserIndex(EntityManager em, int userIndex)
    {
        var used = new HashSet<byte>();
        var cuType = ComponentIndex.Of<ConnectedUser>(ComponentType.AccessMode.ReadOnly);
        foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: false, cuType))
        {
            if (TryReadConnectedUserIndex(em, e, out var existing))
                used.Add(existing);
        }

        byte preferred = (byte)userIndex;
        if (!used.Contains(preferred)) return preferred;
        for (int i = 0; i < 256; i++)
        {
            byte b = (byte)i;
            if (!used.Contains(b)) return b;
        }

        Plugin.Logger?.LogWarning($"ghost: every ConnectedUser.UserIndex byte is taken; reusing {preferred}");
        return preferred;
    }

    private static bool TryReadConnectedUserIndex(EntityManager em, Entity entity, out byte index)
    {
        index = 0;
        try
        {
            if (!entity.Has<ConnectedUser>(em)) return false;
            var typeIndex = ComponentIndex.IndexOf<ConnectedUser>();
            byte* src = (byte*)em.GetComponentDataRawRO(entity, typeIndex);
            if (src == null) return false;
            index = src[OffConnectedUserIndex];
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ghost read ConnectedUser.UserIndex on {FormatEntity(entity)}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Copy a live (non-ghost) <see cref="ConnectedUser"/> blob. Offline users lack the
    /// component, so this walks the ConnectedUser query rather than the User template.
    /// </summary>
    private static bool TryCopyConnectedUserBlob(EntityManager em, byte[] dest, TypeIndex typeIndex, int size)
    {
        var cuType = ComponentIndex.Of<ConnectedUser>(ComponentType.AccessMode.ReadOnly);
        foreach (var e in Query.Entities(em, includeDisabled: false, includeSystems: false, cuType))
        {
            try
            {
                if (!em.Exists(e) || !em.HasComponent(e, cuType)) continue;
                if (e.Has<User>(em))
                {
                    try
                    {
                        var u = em.GetComponentData<User>(e);
                        if (IsGhostPlatformId(u.PlatformId)) continue;
                    }
                    catch { /* still a live ConnectedUser — copy it */ }
                }

                byte* src = (byte*)em.GetComponentDataRawRO(e, typeIndex);
                if (src == null) continue;
                Marshal.Copy((IntPtr)src, dest, 0, size);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"ghost: ConnectedUser blob copy from {FormatEntity(e)}: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// <c>stress_populate</c> owner: the same User write as <c>ghost create</c> (template blob
    /// from a real user, patched at the offsets above), but <c>IsConnected</c> false, no
    /// <see cref="ConnectedUser"/>, and not tracked here, so Dev unload and <c>ghost destroy</c>
    /// leave it (its PlatformId is outside the ghost band). Read back through the typed accessor;
    /// rolled back on any failure. The User write path is the live-verified ghost one; an
    /// offline-flagged synthetic user kept across autosave and reload is not live-verified.
    /// </summary>
    internal static Entity CreateOfflineUser(EntityManager em, int index, ulong platformId, string name, out User readback)
    {
        TryFindTemplate(em, out var template, out _);
        var entity = em.CreateEntity();
        try
        {
            WriteUserRaw(em, entity, template, index, platformId, name, admin: false, connected: false);
            readback = em.GetComponentData<User>(entity);
        }
        catch
        {
            try { if (em.Exists(entity)) em.DestroyEntity(entity); } catch { /* best-effort rollback */ }
            throw;
        }
        if (readback.PlatformId != platformId || readback.Index != index || readback.IsConnected)
        {
            try { if (em.Exists(entity)) em.DestroyEntity(entity); } catch { /* best-effort rollback */ }
            throw new InvalidOperationException(
                $"read-back mismatch for '{name}': index {readback.Index}, platformId {readback.PlatformId}, connected {readback.IsConnected}");
        }
        return entity;
    }

    // ---- Offline user team ------------------------------------------------------------------
    // The server's new-user path (ServerBootstrapSystem's authenticate step, new-user branch, read
    // 2026-10-18): Instantiate(ServerRootPrefabCollection.PlayerTeamPrefabGUID), UserTeam.UserEntity =
    // user, then the user's TeamReference base value = the team (ModificationsRegistry.SetBaseValue,
    // which for a field with no registered modification just stores the value). No team event.

    private static int TeamReferenceEntityOffset => Layout.TeamReferenceValueOffset + Layout.ModifiableEntityValueOffset;

    /// <summary>
    /// Give an offline synthetic user (a <c>stress_populate</c> owner) its own team the way the server
    /// gives one to a new user: an instance of the game's player team prefab
    /// (<c>ServerRootPrefabCollection.PlayerTeamPrefabGUID</c>, resolved through
    /// <c>PrefabCollectionSystem._PrefabGuidToEntityMap</c>), its <c>UserTeam.UserEntity</c> set to the user,
    /// and the user's <c>TeamReference</c> (added when missing) pointing at it. Field pokes at the
    /// <c>layout.txt</c> offsets through <c>GetComponentDataRawRW</c>; no native call, no new generic
    /// interop instantiation. Idempotent: a user that already has its own team is left alone. Refuses a
    /// connected user and a user whose <c>TeamReference</c> names someone else's team. On any failure
    /// after the team was instantiated, the team is destroyed again. Server, main thread.
    /// Source-checked against the server code; not live-verified.
    /// </summary>
    internal static OfflineUserTeamResult EnsureOfflineUserTeam(World world, Entity user)
    {
        if (world == null || !world.IsCreated) return OfflineUserTeamResult.Refuse("no_world", "no server world");
        var em = world.EntityManager;
        var seen = ObserveOfflineUserTeam(em, user, out var existing, out _);
        var decision = OfflineUserTeamRules.Decide(seen);
        if (decision.Action == OfflineUserTeamAction.Refuse) return OfflineUserTeamResult.Refuse(decision.Code, decision.Detail);
        if (decision.Action == OfflineUserTeamAction.Ready) return OfflineUserTeamResult.Done(existing, created: false, "already had its team");

        if (!TryPlayerTeamPrefab(world, out var prefab, out var guid, out var why))
            return OfflineUserTeamResult.Refuse("no_team_prefab", why);
        if (!em.HasComponent(prefab, ComponentIndex.Of<UserTeam>(ComponentType.AccessMode.ReadOnly))
            || !em.HasComponent(prefab, ComponentIndex.Of<TeamAllies>(ComponentType.AccessMode.ReadOnly)))
            return OfflineUserTeamResult.Refuse("no_team_prefab", $"player team prefab {guid} lacks UserTeam or TeamAllies");

        var team = Entity.Null;
        try
        {
            team = em.Instantiate(prefab);
            WriteEntityField<UserTeam>(em, team, Layout.UserTeamUserEntityOffset, user);
            if (!user.Has<TeamReference>(em)) user.Add<TeamReference>(em);
            WriteEntityField<TeamReference>(em, user, TeamReferenceEntityOffset, team);

            var after = ObserveOfflineUserTeam(em, user, out var readTeam, out _);
            if (OfflineUserTeamRules.Decide(after).Action != OfflineUserTeamAction.Ready || readTeam != team)
                throw new InvalidOperationException("read-back: missing " + string.Join(", ", OfflineUserTeamRules.Missing(after)) + $", team {FormatEntity(readTeam)}");
        }
        catch (Exception e)
        {
            if (team != Entity.Null)
            {
                try { if (em.Exists(team)) em.DestroyEntity(team); } catch { /* best-effort rollback */ }
            }
            Plugin.Logger?.LogWarning($"offline user team for {FormatEntity(user)} failed: {e.Message}");
            return OfflineUserTeamResult.Refuse("team_failed", e.Message);
        }

        Plugin.Logger?.LogInfo($"offline user team created: user {FormatEntity(user)} team {FormatEntity(team)} prefab {guid}");
        return OfflineUserTeamResult.Done(team, created: true, $"team {FormatEntity(team)} from prefab {guid}");
    }

    /// <summary>
    /// Read what <see cref="OfflineUserTeamRules.Decide"/> needs: the user, the entity its
    /// <c>TeamReference</c> names, that team's <c>UserTeam</c> back-pointer and <c>TeamAllies</c> rows.
    /// </summary>
    internal static OfflineUserTeamObservation ObserveOfflineUserTeam(EntityManager em, Entity user, out Entity team, out List<Entity> allies)
    {
        team = Entity.Null;
        allies = new List<Entity>();
        var o = new OfflineUserTeamObservation();
        if (user == Entity.Null || !em.Exists(user)) return o;
        o.UserExists = true;
        o.HasUser = user.Has<User>(em);
        if (o.HasUser)
        {
            try { o.IsConnected = em.GetComponentData<User>(user).IsConnected; }
            catch { o.HasUser = false; }
        }
        o.HasTeamReference = TryReadEntityField<TeamReference>(em, user, TeamReferenceEntityOffset, out team);
        if (!o.HasTeamReference) team = Entity.Null;
        o.TeamExists = team != Entity.Null && em.Exists(team);
        if (!o.TeamExists) return o;
        o.TeamHasUserTeam = TryReadEntityField<UserTeam>(em, team, Layout.UserTeamUserEntityOffset, out var back);
        o.TeamPointsAtUser = o.TeamHasUserTeam && back == user;
        o.TeamHasAllies = TryReadAllies(em, team, allies);
        return o;
    }

    /// <summary>
    /// Read what <c>TeamUtility.ClaimCastle(EntityManager, user, heart, limitType)</c> would read, without
    /// calling it: <see cref="OfflineUserTeamRules.ClaimCastleRefusal"/> is null when every read succeeds.
    /// </summary>
    internal static ClaimCastleObservation ObserveClaim(EntityManager em, Entity user, Entity heart, out Entity castleTeam, out List<Entity> castleAllies)
    {
        castleTeam = Entity.Null;
        castleAllies = new List<Entity>();
        var c = new ClaimCastleObservation();
        var userTeam = Entity.Null;
        c.UserHasTeamReference = user != Entity.Null && em.Exists(user)
            && TryReadEntityField<TeamReference>(em, user, TeamReferenceEntityOffset, out userTeam);
        c.UserTeamReady = c.UserHasTeamReference && userTeam != Entity.Null && em.Exists(userTeam)
            && TryReadAllies(em, userTeam, new List<Entity>());
        c.HeartExists = heart != Entity.Null && em.Exists(heart);
        if (!c.HeartExists) return c;
        c.HeartHasCastleHeart = em.HasComponent(heart, ComponentIndex.Of<ProjectM.CastleBuilding.CastleHeart>(ComponentType.AccessMode.ReadOnly));
        c.HeartHasTeamReference = TryReadEntityField<TeamReference>(em, heart, TeamReferenceEntityOffset, out castleTeam);
        if (!c.HeartHasTeamReference) { castleTeam = Entity.Null; return c; }
        c.CastleTeamReady = castleTeam != Entity.Null && em.Exists(castleTeam) && TryReadAllies(em, castleTeam, castleAllies);
        c.CastleTeamAllies = castleAllies.Count;
        if (castleAllies.Count == 1)
        {
            var previous = castleAllies[0];
            c.PreviousTeamReady = previous != Entity.Null && em.Exists(previous)
                && TryReadAllies(em, previous, new List<Entity>())
                && em.HasComponent(previous, ComponentIndex.Of<UserTeam>(ComponentType.AccessMode.ReadOnly));
        }
        return c;
    }

    /// <summary>
    /// <c>ServerRootPrefabCollection.PlayerTeamPrefabGUID</c> (the GUID the server's new-user path reads),
    /// cross-checked against <c>PrefabCollectionSystem._RootPrefabs.PlayerTeamPrefabGUID</c>, resolved to
    /// the prefab entity through <c>_PrefabGuidToEntityMap</c>.
    /// </summary>
    internal static bool TryPlayerTeamPrefab(World world, out Entity prefab, out string guidText, out string why)
    {
        prefab = Entity.Null;
        guidText = "";
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        if (prefabs == null) { why = "no PrefabCollectionSystem"; return false; }

        int? serverGuid = null;
        var serverType = ComponentIndex.Of<ServerRootPrefabCollection>(ComponentType.AccessMode.ReadOnly);
        foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: true, serverType))
        {
            byte* p = (byte*)em.GetComponentDataRawRO(e, serverType.TypeIndex);
            if (p == null) continue;
            int size = SafeTypeSize(serverType.TypeIndex);
            if (size > 0 && size < Layout.ServerRootPrefabCollectionPlayerTeamPrefabGuidOffset + sizeof(int)) continue;
            serverGuid = *(int*)(p + Layout.ServerRootPrefabCollectionPlayerTeamPrefabGuidOffset);
            break;
        }

        int? rootGuid = null;
        try { rootGuid = prefabs._RootPrefabs.PlayerTeamPrefabGUID.GuidHash; }
        catch (Exception e) { Plugin.Logger?.LogWarning($"offline user team: RootPrefabCollection read failed: {e.Message}"); }

        if (serverGuid is int s && rootGuid is int r && s != r)
        {
            why = $"ServerRootPrefabCollection names player team prefab {s} but RootPrefabCollection names {r}";
            return false;
        }
        int? chosen = serverGuid ?? rootGuid;
        if (chosen is not int guid || guid == 0) { why = "no player team prefab GUID in either root prefab collection"; return false; }
        guidText = guid.ToString(CultureInfo.InvariantCulture);
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(new Stunlock.Core.PrefabGUID(guid), out prefab) || prefab == Entity.Null || !em.Exists(prefab))
        {
            prefab = Entity.Null;
            why = $"player team prefab {guid} is not in _PrefabGuidToEntityMap";
            return false;
        }
        why = "";
        return true;
    }

    private static bool TryReadEntityField<T>(EntityManager em, Entity entity, int offset, out Entity value)
    {
        value = Entity.Null;
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly);
        if (entity == Entity.Null || !em.Exists(entity) || !em.HasComponent(entity, type)) return false;
        int size = SafeTypeSize(type.TypeIndex);
        if (size > 0 && size < offset + 8) return false;
        byte* p = (byte*)em.GetComponentDataRawRO(entity, type.TypeIndex);
        if (p == null) return false;
        value = new Entity { Index = *(int*)(p + offset), Version = *(int*)(p + offset + 4) };
        return true;
    }

    private static void WriteEntityField<T>(EntityManager em, Entity entity, int offset, Entity value)
    {
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite);
        string name = typeof(T).Name;
        if (!em.Exists(entity) || !em.HasComponent(entity, type))
            throw new InvalidOperationException($"{FormatEntity(entity)} has no {name}");
        int size = SafeTypeSize(type.TypeIndex);
        if (size > 0 && size < offset + 8)
            throw new InvalidOperationException($"refusing {name} poke: TypeSize {size} is smaller than offset {offset} + 8");
        byte* p = (byte*)em.GetComponentDataRawRW(entity, type.TypeIndex);
        if (p == null) throw new InvalidOperationException($"{name} GetComponentDataRawRW returned null");
        *(int*)(p + offset) = value.Index;
        *(int*)(p + offset + 4) = value.Version;
    }

    /// <summary>The <c>TeamAllies</c> rows through the non-generic raw buffer read, at the metadata stride.</summary>
    private static bool TryReadAllies(EntityManager em, Entity team, List<Entity> into)
    {
        var type = ComponentIndex.Of<TeamAllies>(ComponentType.AccessMode.ReadOnly);
        if (team == Entity.Null || !em.Exists(team) || !em.HasComponent(team, type)) return false;
        int length = em.GetBufferLength(team, type.TypeIndex);
        if (length <= 0) return true;
        byte* raw = (byte*)em.GetBufferRawRO(team, type.TypeIndex);
        if (raw == null) return false;
        for (int i = 0; i < length; i++)
        {
            byte* row = raw + (i * Layout.TeamAlliesStride) + Layout.TeamAlliesValueOffset;
            into.Add(new Entity { Index = *(int*)row, Version = *(int*)(row + 4) });
        }
        return true;
    }

    private static int SafeTypeSize(TypeIndex typeIndex)
    {
        try { return TypeManager.GetTypeInfo(typeIndex).TypeSize; }
        catch { return 0; }
    }

    /// <summary>Destroy a synthetic user, its character and its own team, if it has them (ghost and stress owners).</summary>
    internal static bool TryDestroyGhostEntity(EntityManager em, Entity entity)
    {
        try
        {
            if (!em.Exists(entity)) return false;
            Entity character = Entity.Null;
            try
            {
                var u = em.GetComponentData<User>(entity);
                character = u.LocalCharacter._Entity;
            }
            catch { /* destroy the user anyway */ }

            if (character != Entity.Null && character != entity && em.Exists(character))
            {
                Plugin.Logger?.LogInfo($"ghost destroying character {FormatEntity(character)} of {FormatEntity(entity)}");
                em.DestroyEntity(character);
            }

            try
            {
                var seen = ObserveOfflineUserTeam(em, entity, out var team, out var allies);
                if (OfflineUserTeamRules.IsOwnTeam(seen.TeamExists, seen.TeamHasUserTeam, seen.TeamPointsAtUser) && team != entity)
                {
                    if (allies.Count > 0)
                        Plugin.Logger?.LogWarning($"ghost destroying team {FormatEntity(team)} of {FormatEntity(entity)} with {allies.Count} ally row(s) still on it");
                    Plugin.Logger?.LogInfo($"ghost destroying team {FormatEntity(team)} of {FormatEntity(entity)}");
                    em.DestroyEntity(team);
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning($"ghost team cleanup for {FormatEntity(entity)}: {e.Message}"); }

            Plugin.Logger?.LogInfo($"ghost destroying user {FormatEntity(entity)}");
            em.DestroyEntity(entity);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ghost destroy {FormatEntity(entity)}: {e}");
            return false;
        }
    }

    private static bool TryFindTemplate(EntityManager em, out Entity entity, out string name)
    {
        entity = Entity.Null;
        name = "";
        Entity fallback = Entity.Null;
        string fallbackName = "";

        foreach (var e in AllUsers(em))
        {
            User u;
            try { u = em.GetComponentData<User>(e); }
            catch { continue; }
            if (IsGhostPlatformId(u.PlatformId) || StressIdentity.IsStressPlatformId(u.PlatformId)) continue;
            string n = SafeName(u);
            if (u.IsConnected)
            {
                entity = e;
                name = n;
                return true;
            }
            if (fallback == Entity.Null)
            {
                fallback = e;
                fallbackName = n;
            }
        }

        if (fallback == Entity.Null) return false;
        entity = fallback;
        name = fallbackName;
        return true;
    }

    private static void CollectUsedIdentity(EntityManager em, HashSet<int> indexes, HashSet<ulong> platforms, HashSet<string> names)
    {
        foreach (var e in AllUsers(em))
        {
            try
            {
                var u = em.GetComponentData<User>(e);
                indexes.Add(u.Index);
                platforms.Add(u.PlatformId);
                names.Add(SafeName(u));
            }
            catch { /* skip unreadable */ }
        }
    }

    private static int NextFreeIndex(HashSet<int> used)
    {
        for (int i = IndexFloor; i < IndexFloor + PlatformIdSpan; i++)
        {
            if (!used.Contains(i)) return i;
        }
        throw new InvalidOperationException($"no free User.Index in {IndexFloor}..{IndexFloor + PlatformIdSpan - 1}");
    }

    /// <summary>
    /// 1-based ordinal so the first ghost is named <c>Ghost1</c> with PlatformId
    /// <see cref="PlatformIdBase"/> (n=0 in the spike table).
    /// </summary>
    private static int NextFreeOrdinal(string prefix, HashSet<string> usedNames, HashSet<ulong> usedPlatforms)
    {
        for (int n = 1; n <= PlatformIdSpan; n++)
        {
            string name = prefix + n.ToString(CultureInfo.InvariantCulture);
            ulong platform = PlatformIdBase + (ulong)(n - 1);
            if (usedNames.Contains(name) || usedPlatforms.Contains(platform)) continue;
            return n;
        }
        throw new InvalidOperationException($"no free ghost ordinal for prefix '{prefix}' in 1..{PlatformIdSpan}");
    }

    /// <summary>
    /// Leftover-ghost predicate. Extra-client ids
    /// <see cref="ExtraInstanceAuth.PlatformIdBase"/>..<c>+99</c> are excluded even if
    /// that band later overlaps this span: <c>ghost destroy</c> / Dev Unload must not
    /// destroy a live extra-client User.
    /// </summary>
    internal static bool IsGhostPlatformId(ulong platformId)
    {
        if (ExtraInstanceAuth.IsOwnPlatformId(platformId))
            return false;
        return platformId >= PlatformIdBase && platformId < PlatformIdBase + (ulong)PlatformIdSpan;
    }

    private static List<Entity> AllUsers(EntityManager em) =>
        Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));

    private static World RequireServerWorld()
    {
        var world = Progenitor.Worlds.Server;
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("no server world; wait until ping reports a Server world");
        return world;
    }

    /// <summary>
    /// Per-ghost row for create/list. <c>hasConnectedUser</c> is
    /// <see cref="EntityExtensions.Has{T}"/>. <c>inConnectedUsersQuery</c> is whether
    /// this entity appears in the default (enabled-only) <see cref="ConnectedUser"/>
    /// query — the equivalent of <c>ChatMessageSystem._ConnectedUsersQuery</c>.
    /// </summary>
    private static object Describe(EntityManager em, Entity entity, User u)
    {
        bool hasConnectedUser = false;
        try { hasConnectedUser = entity.Has<ConnectedUser>(em); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"ghost Has<ConnectedUser> {FormatEntity(entity)}: {e.Message}"); }

        byte? connectedUserIndex = null;
        if (hasConnectedUser && TryReadConnectedUserIndex(em, entity, out var slot))
            connectedUserIndex = slot;

        int connectedUsersQueryCount = 0;
        bool inConnectedUsersQuery = false;
        try
        {
            // Game query skips disabled; ghosts are enabled. Match that so the diagnostic
            // answers "would ChatMessageJob see this entity?" not "does the component exist
            // on a disabled leftover".
            var list = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<ConnectedUser>(ComponentType.AccessMode.ReadOnly));
            connectedUsersQueryCount = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == entity)
                {
                    inConnectedUsersQuery = true;
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ghost ConnectedUser query {FormatEntity(entity)}: {e.Message}");
        }

        return new
        {
            entity = FormatEntity(entity),
            index = u.Index,
            name = SafeName(u),
            platformId = u.PlatformId,
            admin = u.IsAdmin,
            connected = u.IsConnected,
            bot = u.IsBot,
            hasConnectedUser,
            connectedUserIndex,
            inConnectedUsersQuery,
            connectedUsersQueryCount,
        };
    }

    private static string SafeName(User u)
    {
        try { return u.CharacterName.ToString(); }
        catch { return "<name?>"; }
    }

    private static string FormatEntity(Entity e) => $"{e.Index}:{e.Version}";

    private static void PutI32(byte[] b, int o, int v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16);
        b[o + 3] = (byte)(v >> 24);
    }

    private static void PutI64(byte[] b, int o, long v)
    {
        PutI32(b, o, (int)v);
        PutI32(b, o + 4, (int)(v >> 32));
    }

    private static void PutU64(byte[] b, int o, ulong v) => PutI64(b, o, (long)v);

    private static void ClearNetworkedEntity(byte[] b, int o)
    {
        // NetworkedEntity: Entity (int Index + int Version) @0, bool _WaitingForSync @8. Size 12.
        for (int i = 0; i < 12 && o + i < b.Length; i++) b[o + i] = 0;
    }

    private static void PutFixedString64(byte[] b, int o, string text)
    {
        var fs = new FixedString64Bytes(text ?? "");
        byte* src = (byte*)&fs;
        for (int i = 0; i < 64 && o + i < b.Length; i++) b[o + i] = src[i];
    }

    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined) return null;
        try { return el.GetString(); }
        catch { return null; }
    }

    private static int GetInt(JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            return n;
        return fallback;
    }

    private static bool GetBool(JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        try
        {
            if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var s)) return s;
        }
        catch { }
        return fallback;
    }

    private readonly struct TrackedGhost
    {
        public readonly Entity Entity;
        public TrackedGhost(Entity entity) => Entity = entity;
    }
}

/// <summary>Outcome of <see cref="GhostOps.EnsureOfflineUserTeam"/>.</summary>
internal readonly struct OfflineUserTeamResult
{
    private OfflineUserTeamResult(bool ok, bool created, Entity team, string code, string detail)
    {
        Ok = ok;
        Created = created;
        Team = team;
        Code = code;
        Detail = detail;
    }

    public bool Ok { get; }
    /// <summary>True when this call instantiated the team; false when the user already had it.</summary>
    public bool Created { get; }
    public Entity Team { get; }
    /// <summary>Empty when <see cref="Ok"/>; else <c>no_world</c>, <c>no_user</c>, <c>connected</c>, <c>foreign_team</c>, <c>team_without_allies</c>, <c>no_team_prefab</c> or <c>team_failed</c>.</summary>
    public string Code { get; }
    public string Detail { get; }

    public static OfflineUserTeamResult Done(Entity team, bool created, string detail) => new(true, created, team, "", detail);

    public static OfflineUserTeamResult Refuse(string code, string detail) => new(false, false, Entity.Null, code, detail);
}
