using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Progenitor.Ecs;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Players;

/// <summary>
/// Bring a dead player back, from the server, with no input from the client.
///
/// <para><b>Why this is a framework capability and not a test script.</b> Unattended client
/// testing works right up until the test character dies. After that the client sits on
/// <i>You were slain — press Space to respawn</i>, the character stops simulating, and every
/// server-side action aimed at it silently does nothing: a forced cast is written and never
/// consumed, its counter never moves, and the whole thing reads as "the feature is broken" rather
/// than "the actor is dead". That is a genuinely expensive failure to diagnose, and the only way
/// out of it used to be a human pressing a key. Now it is a call.</para>
///
/// <para><b>The seam</b> is <c>ServerBootstrapSystem.RespawnCharacter(EntityCommandBuffer, Entity
/// user, float3? customSpawnLocation, Entity previousCharacter, Entity fadeOutEntity, int
/// spawnLocationIndex)</c> — public, and free of by-ref struct parameters, so it is safe to call
/// through the interop (interop trap 1 rules out the ones that are not).</para>
///
/// <para>The argument is the <b>User</b> entity, not the character: a real respawn replaces
/// the character, so the caller's old character handle would be stale afterwards.</para>
///
/// <para><b>The direct call is refused.</b> <c>ServerBootstrapSystem.RespawnCharacter</c>
/// (the short overload, followed by an immediate command-buffer playback) aborts the
/// dedicated server. A <c>NativeArray</c> allocated with <c>Allocator.Temp</c> already
/// aborts on the debug-bridge drain; the same array is legal after
/// <c>ServerBootstrapSystem</c> updates, and <c>RespawnCharacter</c> still aborts from
/// that hook. The in-game path that works is <c>RespawnCharacterSystem</c>.</para>
/// </summary>
public static class PlayerRespawn
{
    /// <summary>
    /// Stable refusal returned by <see cref="Respawn"/> until respawn goes through
    /// <c>RespawnCharacterSystem</c> instead of the short <c>RespawnCharacter</c> overload.
    /// </summary>
    public const string UnsafeDirectCallRefusal = PlayerRespawnPolicy.UnsafeDirectCallRefusal;

    /// <summary>
    /// Does not respawn. Returns false with <see cref="UnsafeDirectCallRefusal"/> after the
    /// world and user checks. <paramref name="why"/> is set rather than throwing, and rather
    /// than aborting the process.
    /// </summary>
    public static bool Respawn(World? world, Entity userEntity, out string why)
    {
        if (world == null || !world.IsCreated)
        {
            why = "respawn refused: no server world.";
            return false;
        }

        try
        {
            var em = world.EntityManager;
            if (!em.Exists(userEntity))
            {
                why = "respawn refused: user entity " + userEntity.Index + ":" + userEntity.Version + " does not exist.";
                return false;
            }

            if (!em.HasComponent(userEntity, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
            {
                why = "respawn refused: entity " + userEntity.Index + ":" + userEntity.Version + " is not a User.";
                return false;
            }

            why = UnsafeDirectCallRefusal;
            Plugin.Logger?.LogWarning("Players: " + why + " user " + userEntity.Index + ":" + userEntity.Version);
            return false;
        }
        catch (Exception e)
        {
            why = "respawn threw: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// Does not respawn anyone while <see cref="Respawn"/> refuses the direct call. Still walks
    /// connected non-bot users and logs the refusal for each dead or missing character, so a
    /// fixture can see why the count stayed 0. Returns 0.
    /// </summary>
    public static int RespawnDeadPlayers(World? world)
    {
        if (world == null || !world.IsCreated) return 0;
        int count = 0;
        try
        {
            var em = world.EntityManager;
            var users = Query.Entities(em, true, false, ComponentType.ReadOnly<User>());
            for (int i = 0; i < users.Count; i++)
            {
                if (!em.HasComponent(users[i], ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly))) continue;
                var user = em.GetComponentData<User>(users[i]);
                if (!user.IsConnected) continue;

                // Never respawn a bot. A Progenitor ghost is a User-only entity with IsBot set,
                // IsConnected true and LocalCharacter Null, which is exactly the shape this method
                // looks for in a dead player - so without this it respawns the ghost, gives it a
                // character the ghost was never meant to own, and leaves that character orphaned
                // when the ghost is destroyed. Measured: "respawned user 326844:63 (previous
                // character 0:0)".
                if (user.IsBot) continue;

                var character = user.LocalCharacter._Entity;
                bool dead = character == Entity.Null
                            || !em.Exists(character)
                            || em.HasComponent(character, ComponentIndex.Of<Dead>(ComponentType.AccessMode.ReadOnly));
                if (!dead) continue;

                if (Respawn(world, users[i], out string why)) count++;
                else Plugin.Logger?.LogWarning("Players: " + why);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Players: RespawnDeadPlayers threw: " + e);
        }
        return count;
    }

    static readonly Dictionary<Entity, float3> BoundPoints = new();
    static readonly Dictionary<Entity, Entity> BoundMarkers = new();

    /// <summary>
    /// Record a world point for <paramref name="userEntity"/>. Test tooling only: the Dev bridge
    /// <c>respawn</c> op teleports a bound user there after it revives them. Nothing else uses it,
    /// so a normal respawn (the death screen, the game) is never changed (project rule, 2026-10-11:
    /// Core does not interfere with normal gameplay and death).
    ///
    /// <para>The old bind linked a server-only marker entity into the user's
    /// <c>RespawnPointOwnerBuffer</c>. That buffer replicates to the client, whose death screen
    /// (<c>DeathMenuMapper</c>) resolves every respawn point and threw "The entity does not exist"
    /// on the marker every frame, so Space never respawned the player. Bind no longer touches the
    /// respawn list, and removes markers an older bind left there.</para>
    ///
    /// <para>The point lives in memory for this Core generation. Does not move anyone now.</para>
    /// </summary>
    public static bool Bind(World? world, Entity userEntity, float x, float y, float z, out string why)
    {
        why = "";
        if (world == null || !world.IsCreated)
        {
            why = "bind refused: no server world.";
            return false;
        }
        try
        {
            var em = world.EntityManager;
            if (!TryUser(em, userEntity, out var user, out why))
                return false;
            int removed = RemoveBindMarkers(em, user);
            BoundPoints[user] = new float3(x, y, z);
            Plugin.Logger?.LogInfo(
                "Players: bind user " + user.Index + ":" + user.Version + " to " + x + "," + y + "," + z
                + " (used by the bridge respawn op only"
                + (removed > 0 ? "; removed " + removed + " old respawn-list marker(s)" : "") + ")");
            return true;
        }
        catch (Exception e)
        {
            why = "bind threw: " + e.Message;
            Plugin.Logger?.LogError("Players: " + why);
            return false;
        }
    }

    /// <summary>The point this user is bound to, if any. <paramref name="index"/> is always 0.</summary>
    public static bool TryBoundPoint(World? world, Entity userEntity, out float x, out float y, out float z, out int index)
    {
        x = y = z = 0f;
        index = 0;
        if (world == null || !world.IsCreated || !world.EntityManager.Exists(userEntity))
            return false;
        if (!TryUser(world.EntityManager, userEntity, out var user, out _))
            return false;
        if (!BoundPoints.TryGetValue(user, out var point))
            return false;
        x = point.x;
        y = point.y;
        z = point.z;
        return true;
    }

    /// <summary>
    /// Forget this user's bound point and remove any marker an older bind left in the user's
    /// respawn list (those break the client's death screen). Returns how many markers were
    /// removed, or -1 with <paramref name="why"/> set.
    /// </summary>
    public static int Unbind(World? world, Entity userEntity, out string why)
    {
        why = "";
        if (world == null || !world.IsCreated)
        {
            why = "unbind refused: no server world.";
            return -1;
        }
        try
        {
            var em = world.EntityManager;
            if (!TryUser(em, userEntity, out var user, out why))
                return -1;
            BoundPoints.Remove(user);
            int removed = RemoveBindMarkers(em, user);
            Plugin.Logger?.LogInfo("Players: unbind user " + user.Index + ":" + user.Version + " removed " + removed + " respawn-list marker(s)");
            return removed;
        }
        catch (Exception e)
        {
            why = "unbind threw: " + e.Message;
            Plugin.Logger?.LogError("Players: " + why);
            return -1;
        }
    }

    /// <summary>
    /// Remove every entry an older <see cref="Bind"/> wrote into this user's respawn list, keep
    /// every real entry, and destroy the markers. Returns how many were removed.
    /// </summary>
    static unsafe int RemoveBindMarkers(EntityManager em, Entity user)
    {
        if (!TryBuffer(em, user, out var typeIndex, out int length) || length == 0)
            return 0;
        var respawnType = RespawnPointTypeOf();
        const int maxEntries = 16;
        if (length > maxEntries)
            throw new InvalidOperationException("respawn list is longer than " + maxEntries + " entries");

        byte* raw = (byte*)em.GetBufferRawRO(user, typeIndex);
        if (raw == null)
            throw new InvalidOperationException("respawn list raw buffer was null");
        byte* kept = stackalloc byte[OwnerStride * maxEntries];
        int keptCount = 0;
        var markers = new List<Entity>();
        for (int i = 0; i < length; i++)
        {
            byte* element = raw + (i * OwnerStride);
            var point = new Entity { Index = *(int*)element, Version = *(int*)(element + 4) };
            if (IsStaleBindEntry(em, point, respawnType, user))
            {
                markers.Add(point);
                continue;
            }
            byte* dest = kept + (keptCount * OwnerStride);
            for (int b = 0; b < OwnerStride; b++) dest[b] = element[b];
            keptCount++;
        }
        if (markers.Count == 0)
            return 0;

        UnsafeEntityManagerUtility.SetBufferDataRaw(em, user, typeIndex.Value, OwnerStride, 4, keptCount, kept);

        // The buffer write is finished and no raw pointer into it is used below.
        foreach (var marker in markers)
            if (marker != Entity.Null && em.Exists(marker))
                em.DestroyEntity(marker);
        BoundMarkers.Remove(user);
        return markers.Count;
    }

    /// <summary>
    /// An entry an older bind wrote: a bare marker (no PrefabGUID, no NetworkId) owned by this
    /// user, or an entry whose point no longer exists. A placed coffin always has a PrefabGUID.
    /// </summary>
    static bool IsStaleBindEntry(EntityManager em, Entity point, ComponentType respawnType, Entity user)
    {
        if (point == Entity.Null || !em.Exists(point)) return true;
        if (em.HasComponent(point, ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly))) return false;
        if (em.HasComponent(point, new ComponentType(Il2CppType.From(typeof(NetworkId)), ComponentType.AccessMode.ReadOnly))) return false;
        return IsBindMarker(em, point, respawnType, user);
    }

    /// <summary>
    /// Owner-list element stride. The connection verifier walks the buffer by 0x1C, and a
    /// live inspect reports the same element size. This is not a component-blob size.
    /// </summary>
    const int OwnerStride = 28;

    const int ExitOffset = 0x00;
    const int DelayBuffOffset = 0x0C;
    const int SleepBuffOffset = 0x10;
    const int TypeOffset = 0x14;
    const int HasOwnerOffset = 0x15;
    const int ReverseOwnerOffset = 0x18;
    const int NetworkIdOffset = 0x0C;
    const int LockedOffset = 0x18;

    static unsafe bool TryNativeLink(EntityManager em, Entity user, out float3 point, out int index, out Entity marker)
    {
        point = default;
        index = 0;
        marker = Entity.Null;
        if (!TryBuffer(em, user, out var bufferIndex, out int length) || length <= 0)
            return false;

        byte* raw = (byte*)em.GetBufferRawRO(user, bufferIndex);
        if (raw == null)
            return false;

        var respawnType = RespawnPointTypeOf();
        var teamType = TeamTypeOf();
        for (int i = 0; i < length; i++)
        {
            byte* element = raw + (i * OwnerStride);
            var candidate = new Entity { Index = *(int*)element, Version = *(int*)(element + 4) };
            if (!em.Exists(candidate) || !em.HasComponent(candidate, teamType))
                continue;
            if (!ReverseOwnerIs(em, candidate, respawnType, user))
                continue;
            if (!candidate.TryRead(em, out Translation translation))
                continue;
            point = translation.Value;
            index = i;
            marker = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Copies <c>RespawnPoint</c> from a live point, then pokes the coffin fields through
    /// the existing slot. <c>SetComponentDataRaw</c> of a chosen length is not used: a
    /// zero TypeSize is not proof the component is a tag, and it is not a write size.
    /// </summary>
    static unsafe void WriteRespawnPoint(EntityManager em, Entity marker, Entity user)
    {
        var componentType = RespawnPointTypeOf();
        var typeIndex = componentType.TypeIndex;
        if (typeIndex.IsZeroSized || SingletonsZeroSize.IsZeroSized(typeIndex.Value))
            throw new InvalidOperationException(RespawnPointDiagnostic(typeIndex));

        var donor = FindDonor(em, marker, componentType);
        if (!em.HasComponent(marker, componentType))
            em.AddComponent(marker, componentType);
        UnsafeEntityManagerUtility.CopyComponentData(em, donor, marker, typeIndex);

        byte* data;
        try
        {
            data = (byte*)em.GetComponentDataRawRW(marker, typeIndex);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(RespawnPointDiagnostic(typeIndex) + " poke failed: " + e.Message);
        }

        if (data == null)
            throw new InvalidOperationException(RespawnPointDiagnostic(typeIndex) + " poke returned null");

        *(float*)(data + ExitOffset) = 0f;
        *(float*)(data + ExitOffset + 4) = 0f;
        *(float*)(data + ExitOffset + 8) = 0f;
        *(int*)(data + DelayBuffOffset) = 0;
        *(int*)(data + SleepBuffOffset) = 0;
        *(byte*)(data + TypeOffset) = (byte)RespawnPointType.WoodenCoffin;
        *(byte*)(data + HasOwnerOffset) = 1;
        *(int*)(data + ReverseOwnerOffset) = user.Index;
        *(int*)(data + ReverseOwnerOffset + 4) = user.Version;
        *(byte*)(data + ReverseOwnerOffset + 8) = 0;
    }

    static string RespawnPointDiagnostic(TypeIndex typeIndex)
    {
        int typeSize = 0;
        int sizeInChunk = 0;
        try
        {
            var info = TypeManager.GetTypeInfo(typeIndex);
            typeSize = info.TypeSize;
            sizeInChunk = info.SizeInChunk;
        }
        catch (Exception e)
        {
            return "RespawnPoint write refused: index=" + typeIndex.Value
                + " zero=" + typeIndex.IsZeroSized
                + " type info failed: " + e.Message;
        }

        return "RespawnPoint write refused: index=" + typeIndex.Value
            + " zero=" + typeIndex.IsZeroSized
            + " typeSize=" + typeSize
            + " sizeInChunk=" + sizeInChunk
            + ". Not writing a guessed blob.";
    }

    static Entity FindDonor(EntityManager em, Entity marker, ComponentType respawnType)
    {
        var points = Query.Entities(em, true, false, respawnType);
        for (int i = 0; i < points.Count; i++)
        {
            var candidate = points[i];
            if (SameEntity(candidate, marker) || !em.Exists(candidate))
                continue;
            if (em.HasComponent(candidate, respawnType))
                return candidate;
        }

        throw new InvalidOperationException(
            "bind refused: no live RespawnPoint to copy. Refusing to invent the component bytes.");
    }

    static unsafe void CopyTeam(EntityManager em, Entity user, Entity marker)
    {
        var teamType = TeamTypeOf();
        if (!em.HasComponent(user, teamType))
            throw new InvalidOperationException("bind refused: this user has no Team, and a zero Team is rejected");

        byte* team = (byte*)em.GetComponentDataRawRO(user, teamType.TypeIndex);
        if (team == null)
            throw new InvalidOperationException("bind refused: this user's Team could not be read");
        int clan = *(int*)team & 0xFFFF;
        if (clan == 0 || clan == 1)
            throw new InvalidOperationException(
                "bind refused: user team clan " + clan + " is rejected by the respawn alliance check");

        if (!em.HasComponent(marker, teamType))
            em.AddComponent(marker, teamType);
        UnsafeEntityManagerUtility.CopyComponentData(em, user, marker, teamType.TypeIndex);
    }

    /// <summary>
    /// Replaces wooden entries on this user only and appends the marker. Other coffin types
    /// stay. The new record copies the marker's <c>NetworkId</c> when it has one.
    /// </summary>
    static unsafe void WriteOwnerElement(EntityManager em, Entity user, Entity marker)
    {
        if (!TryBuffer(em, user, out var typeIndex, out int length))
            throw new InvalidOperationException("respawn list is not on this user");

        var respawnType = RespawnPointTypeOf();
        const int maxEntries = 16;
        if (length > maxEntries)
            throw new InvalidOperationException("respawn list is longer than " + maxEntries + " entries");

        byte* kept = stackalloc byte[OwnerStride * (maxEntries + 1)];
        int keptCount = 0;
        byte* raw = length > 0 ? (byte*)em.GetBufferRawRO(user, typeIndex) : null;
        if (length > 0 && raw == null)
            throw new InvalidOperationException("respawn list raw buffer was null");

        // Only this framework's own bind markers are replaced. A real coffin the player placed is
        // also a wooden point owned by this user, so it is told apart by having a PrefabGUID:
        // markers are bare CreateEntity entities and never carry one.
        var staleMarkers = new List<Entity>();
        for (int i = 0; i < length; i++)
        {
            byte* element = raw + (i * OwnerStride);
            var point = new Entity { Index = *(int*)element, Version = *(int*)(element + 4) };
            if (SameEntity(point, marker))
                continue;
            if (IsBindMarker(em, point, respawnType, user))
            {
                staleMarkers.Add(point);
                continue;
            }
            byte* dest = kept + (keptCount * OwnerStride);
            for (int b = 0; b < OwnerStride; b++) dest[b] = element[b];
            keptCount++;
        }

        WriteElement(em, kept + (keptCount * OwnerStride), marker);
        int markerSlot = keptCount;
        keptCount++;

        if (length == 0)
        {
            AppendElement(em, user, typeIndex, kept);
        }
        else if (keptCount == length)
        {
            byte* writable = (byte*)em.GetBufferRawRW(user, typeIndex);
            if (writable == null)
                throw new InvalidOperationException("respawn list raw buffer was null");
            for (int b = 0; b < keptCount * OwnerStride; b++) writable[b] = kept[b];
        }
        else if (keptCount == length + 1)
        {
            AppendElement(em, user, typeIndex, kept + (markerSlot * OwnerStride));
        }
        else
        {
            UnsafeEntityManagerUtility.SetBufferDataRaw(
                em, user, typeIndex.Value, OwnerStride, 4, keptCount, kept);
        }

        if (!ListContains(em, user, typeIndex, marker))
            throw new InvalidOperationException(
                "respawn list does not contain marker " + marker.Index + ":" + marker.Version
                + " (typeIndex " + typeIndex.Value + ")");

        // The buffer write is finished and no raw pointer into it is used below, so the structural
        // change of destroying a replaced marker cannot invalidate anything still being read.
        foreach (var stale in staleMarkers)
        {
            if (!SameEntity(stale, marker) && em.Exists(stale))
                em.DestroyEntity(stale);
        }
    }

    /// <summary>
    /// A marker made by <see cref="Bind"/> for this user: a wooden point whose reverse owner is
    /// this user and which has no PrefabGUID (a placed coffin always has one).
    /// </summary>
    static bool IsBindMarker(EntityManager em, Entity point, ComponentType respawnType, Entity user) =>
        IsWooden(em, point, respawnType)
        && ReverseOwnerIs(em, point, respawnType, user)
        && !em.HasComponent(point, ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly));

    static unsafe void AppendElement(EntityManager em, Entity user, TypeIndex typeIndex, byte* element)
    {
        try
        {
            UnsafeEntityManagerUtility.AddBufferElement(em, user, typeIndex.Value, element);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Players: respawn list append with flagged index failed: " + e.Message);
            UnsafeEntityManagerUtility.AddBufferElement(
                em, user, typeIndex.Value & SingletonsZeroSize.ClearFlagsMask, element);
        }
    }

    static unsafe void WriteElement(EntityManager em, byte* element, Entity marker)
    {
        for (int i = 0; i < OwnerStride; i++) element[i] = 0;
        *(int*)element = marker.Index;
        *(int*)(element + 4) = marker.Version;
        var networkType = new ComponentType(Il2CppType.From(typeof(NetworkId)), ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(marker, networkType))
            return;
        byte* networkId = (byte*)em.GetComponentDataRawRO(marker, networkType.TypeIndex);
        if (networkId == null)
            return;
        for (int i = 0; i < 12; i++) element[NetworkIdOffset + i] = networkId[i];
        element[LockedOffset] = 0;
    }

    static unsafe bool ListContains(EntityManager em, Entity user, TypeIndex typeIndex, Entity marker)
    {
        int length;
        try { length = em.GetBufferLength(user, typeIndex); }
        catch { return false; }
        if (length <= 0)
            return false;
        byte* raw = (byte*)em.GetBufferRawRO(user, typeIndex);
        if (raw == null)
            return false;
        for (int i = 0; i < length; i++)
        {
            byte* element = raw + (i * OwnerStride);
            if (*(int*)element == marker.Index && *(int*)(element + 4) == marker.Version)
                return true;
        }

        return false;
    }

    static unsafe bool IsWooden(EntityManager em, Entity point, ComponentType respawnType)
    {
        if (!em.Exists(point) || !em.HasComponent(point, respawnType))
            return false;
        byte* data = (byte*)em.GetComponentDataRawRO(point, respawnType.TypeIndex);
        return data != null && *(byte*)(data + TypeOffset) == (byte)RespawnPointType.WoodenCoffin;
    }

    static unsafe bool ReverseOwnerIs(EntityManager em, Entity point, ComponentType respawnType, Entity user)
    {
        if (!em.Exists(point) || !em.HasComponent(point, respawnType))
            return false;
        byte* data = (byte*)em.GetComponentDataRawRO(point, respawnType.TypeIndex);
        if (data == null)
            return false;
        return *(int*)(data + ReverseOwnerOffset) == user.Index
            && *(int*)(data + ReverseOwnerOffset + 4) == user.Version;
    }

    static bool TryBuffer(EntityManager em, Entity user, out TypeIndex typeIndex, out int length)
    {
        typeIndex = ComponentIndex.IndexOf<RespawnPointOwnerBuffer>();
        length = 0;
        try
        {
            length = em.GetBufferLength(user, typeIndex);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Players: respawn list read failed: " + e.Message);
            return false;
        }
    }

    static ComponentType RespawnPointTypeOf()
        => new(Il2CppType.From(typeof(RespawnPoint)), ComponentType.AccessMode.ReadWrite);

    static ComponentType TeamTypeOf()
        => new(Il2CppType.From(typeof(Team)), ComponentType.AccessMode.ReadWrite);

    static bool SameEntity(Entity a, Entity b) => a.Index == b.Index && a.Version == b.Version;

    static void LinkToUser(EntityManager em, Entity user, Entity marker)
    {
        CopyTeam(em, user, marker);
        WriteRespawnPoint(em, marker, user);
        WriteOwnerElement(em, user, marker);
    }

    static bool TryUser(EntityManager em, Entity entity, out Entity user, out string why)
    {
        user = entity;
        why = "";
        if (!em.Exists(entity))
        {
            why = "bind refused: entity " + entity.Index + ":" + entity.Version + " does not exist.";
            return false;
        }

        if (em.HasComponent(entity, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
            return true;
        if (em.HasComponent(entity, ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly)))
        {
            user = em.GetComponentData<PlayerCharacter>(entity).UserEntity;
            if (em.Exists(user))
                return true;
            why = "bind refused: character has no user.";
            return false;
        }

        why = "bind refused: entity " + entity.Index + ":" + entity.Version + " is not a user or character.";
        return false;
    }
}
