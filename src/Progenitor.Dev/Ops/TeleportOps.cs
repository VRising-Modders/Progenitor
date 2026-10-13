using System;
using System.Collections.Generic;
using System.Text.Json;
using ProjectM;
using ProjectM.Tiles;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>teleport</c> op: move a connected user's character to a point or to
/// another character. Server, main thread.
///
/// <b>Game path.</b> The move goes through the game's own helper,
/// <c>ProjectM.TeleportUtilityServer.Teleport(EntityManager, Entity, float3, Nullable&lt;quaternion&gt;)</c>
/// (in <c>ProjectM.Shared.dll</c>) — a <c>public static</c> entry point with no
/// admin gate and no network event to forge. It was chosen over the two alternatives the
/// research turned up:
///
/// <list type="bullet">
/// <item>Injecting a <c>PlayerTeleportDebugEvent</c> + <c>FromCharacter</c>
/// entity for <c>PlayerTeleportSystem</c> is the exact
/// vanilla admin path, but the system's query composition is built in a native
/// <c>__AssignQueries</c> so the required tag set has to be guessed, and it
/// costs a frame of latency. It is also the path that would let a mod bypass the event's
/// <c>[AdminEvent(MinRequiredLevel = 2)]</c> gate, which lives on the
/// deserialization side, not in the consuming system.</item>
/// <item>Writing the position components directly is what those systems do, but it means this
/// op owns the component set instead of the game.</item>
/// </list>
///
/// <b>Fallback.</b> If the helper throws (a game update moving or renaming it), or the
/// one-second read-back says the character did not arrive, the op writes
/// the three components every server teleport system writes in lockstep — <c>Translation</c>,
/// <c>LastTranslation</c> (the <c>[SyncFloat3]</c> one the
/// client actually interpolates from) and <c>PlayerLastValidPosition</c> —
/// as field pokes through <c>GetComponentDataRawRW</c>, never a guessed-length blob write.
/// The result's <c>method</c> says which path ran. <c>SpawnTransform</c> is deliberately not
/// touched: it is the AI leash home, and the game has a separate
/// <c>TeleportAndSetSpawnPosition</c> overload for when that is wanted.
///
/// <b>The fallback is size- and layout-guarded</b> (R30 finding 12). Every poke goes through
/// <see cref="TeleportArgs.PositionWriteRefusal"/>, which sizes the component exactly the way
/// the <c>set</c> op does and hands that budget to
/// <c>RawWriteGuard.EnsureOverlayMemcpy</c>, after checking that the component still carries a
/// <c>float3</c> at offset 0. A component that fails is <b>refused</b>, not warned about; when
/// no component survives the guard the op fails with
/// <see cref="TeleportArgs.FallbackRefusalMessage"/> rather than writing anything. The
/// fallback is kept rather than deleted because deleting it would take the op out exactly when
/// the game helper moves — the case it exists for — while the write it performs is now no
/// broader than the <c>set</c> op the same bridge already exposes on the same components.
///
/// The response is deferred one Drain-second, like <c>spawn</c>, so <c>after</c> is the live
/// position once the movement group has run and <c>arrived</c> says whether it stuck. A helper
/// that returns without moving the character is not a success: the write runs and the read-back
/// waits one more second. A normal floor still finishes on the first read.
/// </summary>
public static unsafe class TeleportOps
{
    /// <summary>
    /// <paramref name="args"/>: <c>user</c> (character name) or <c>entity</c>
    /// (<c>index:version</c>) for the subject; <c>x</c>/<c>y</c>/<c>z</c>, <c>to</c> (another
    /// character name) or <c>toEntity</c> for the destination.
    /// </summary>
    public static object Run(World world, JsonElement args)
    {
        try
        {
            if (!Progenitor.Plugin.IsServerProcess)
                throw new InvalidOperationException("teleport runs on the server side only");
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("the game world does not exist yet (not in a game)");

            var request = TeleportArgs.Parse(args);
            var em = world.EntityManager;

            var character = request.SubjectKind == TeleportArgs.SubjectKind.Entity
                ? CharacterLookup.FromEntityId(em, request.Subject, "entity")
                : CharacterLookup.FromUserName(em, request.Subject);

            ResolveDestination(em, request, out float3 destination, out Entity destinationEntity);
            destination = SnapToFloor(world, destination);

            var before = EntityOps.TryPosition(em, character)
                ?? throw new InvalidOperationException(
                    $"entity {Format(character)} has no Translation or LocalToWorld, so it has no position to move");

            string method = Move(em, character, destination);
            string? name = CharacterLookup.TryName(em, character);

            Plugin.Logger?.LogInfo(TeleportArgs.LogLine(
                name ?? Format(character),
                before.x, before.y, before.z,
                destination.x, destination.y, destination.z,
                method));

            return ReadBack(world, character, name, method, before, destination, destinationEntity, bound: null);
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"teleport op threw: {e}");
            throw new InvalidOperationException($"teleport op failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// Drop a destination onto the gameplay floor under it. A boss pivot and a guessed
    /// height sit above the ground; <c>TileWorld.TryGetGameplayHeight</c> uses the
    /// requested point, including height, so a stacked floor (the throne room at 15)
    /// stays on that floor instead of the outdoor ground below. A miss leaves the
    /// requested height alone.
    /// </summary>
    private static float3 SnapToFloor(World world, float3 destination)
    {
        try
        {
            // MAPTP1: the tile-world read moved into Core's GroundProbe (the Sample's map teleport uses it
            // too); same call, same -40 floor.
            using var ground = Progenitor.Terrain.GroundProbe.TryOpen(world, out _);
            if (ground == null)
                return destination;
            if (!ground.TryGetHeight(destination.x, destination.y, destination.z, out float floor))
                return destination;
            if (floor >= destination.y - 0.4f)
                return destination;
            Plugin.Logger?.LogInfo(
                "teleport: floor " + destination.y.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " -> " + floor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            destination.y = floor;
            return destination;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("teleport: floor sample failed: " + e.Message);
            return destination;
        }
    }

    private const string GameMethod = "TeleportUtilityServer.Teleport";
    private const string WriteMethod = "Translation+LastTranslation+PlayerLastValidPosition";

    /// <summary>
    /// The game's own helper first; the three-component write only if it is gone. A helper
    /// that returns but does not move the character is handled in the read-back, not here.
    /// Returns the <c>method</c> token that goes into the log line and the JSON result.
    /// </summary>
    private static string Move(EntityManager em, Entity character, float3 destination)
    {
        try
        {
            TeleportUtilityServer.Teleport(em, character, destination);
            return GameMethod;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                $"teleport: TeleportUtilityServer.Teleport failed ({e.Message}); falling back to the position-component write");
        }

        WritePosition(em, character, destination);
        return WriteMethod;
    }

    /// <summary>
    /// Field pokes at offset 0 of the three components every server teleport system writes.
    /// Translation alone moves the entity server-side but the client interpolates from a stale
    /// <c>LastTranslation</c> — the classic rubber-band. A missing component is skipped, not an
    /// error: a non-player entity has no <c>PlayerLastValidPosition</c>. A component that is
    /// present but fails the size/layout guard is a refusal, and if that leaves nothing written
    /// the whole op fails rather than reporting a move that did not happen.
    /// </summary>
    private static void WritePosition(EntityManager em, Entity character, float3 destination)
    {
        var refusals = new List<string>();
        bool wroteAny = false;
        wroteAny |= PokeFloat3<Unity.Transforms.Translation>(em, character, destination, "Translation", refusals);
        wroteAny |= PokeFloat3<LastTranslation>(em, character, destination, "LastTranslation", refusals);
        wroteAny |= PokeFloat3<PlayerLastValidPosition>(em, character, destination, "PlayerLastValidPosition", refusals);

        if (wroteAny) return;

        if (refusals.Count > 0)
            throw new InvalidOperationException(TeleportArgs.FallbackRefusalMessage(Format(character), refusals));

        throw new InvalidOperationException(
            $"entity {Format(character)} has none of Translation / LastTranslation / PlayerLastValidPosition, so there is nothing to write");
    }

    /// <summary>
    /// One guarded poke. The component's raw ECS <c>TypeSize</c> and its interop
    /// <c>Marshal.SizeOf</c> are read separately (R31 finding 16) and its <c>float3</c> located
    /// from the interop struct's <c>FieldOffset</c> attributes, then all of it goes to
    /// <see cref="TeleportArgs.PositionWriteRefusal"/>, which applies the interop offset/extent
    /// checks always and <c>RawWriteGuard.EnsureOverlayMemcpy</c> whenever <c>TypeSize</c> is
    /// something other than the documented 0 "poke fields, do not memcpy" case. False
    /// with nothing added to <paramref name="refusals"/> means "not on this entity"; false with
    /// a reason means the write was refused.
    /// </summary>
    private static bool PokeFloat3<T>(
        EntityManager em,
        Entity entity,
        float3 value,
        string label,
        List<string> refusals)
    {
        ComponentType type;
        try
        {
            type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(entity, type)) return false;
        }
        catch (Exception e)
        {
            refusals.Add($"{label}: could not resolve the component: {e.Message}");
            return false;
        }

        string typeName = typeof(T).FullName ?? typeof(T).Name;
        int ecsTypeSize, interopSize;
        int positionOffset = -1, positionFieldSize = 0;
        try
        {
            // Two separate numbers, deliberately (R31 finding 16): the raw ECS TypeSize the
            // RawWriteGuard rules are written against, and the interop extent the FieldOffset
            // metadata is read out of. ComponentDump.SizeOf collapses them and would hand the
            // guard an interop size in place of the TypeSize it checks.
            ecsTypeSize = TypeManager.GetTypeInfo(type.TypeIndex).TypeSize;
            interopSize = ComponentDump.SizeOf(typeof(T), ecsTypeSize);
            foreach (var spec in ComponentDump.FieldsOf(typeof(T), interopSize))
            {
                if (spec.Kind != ComponentDump.FieldKind.Float3) continue;
                positionOffset = spec.Offset;
                positionFieldSize = spec.Size;
                break;
            }
        }
        catch (Exception e)
        {
            refusals.Add($"{label}: could not size '{typeName}' for the write guard: {e.Message}");
            return false;
        }

        var refusal = TeleportArgs.PositionWriteRefusal(label, typeName, ecsTypeSize, interopSize, positionOffset, positionFieldSize);
        if (refusal != null)
        {
            Plugin.Logger?.LogWarning($"teleport: refusing the fallback write on {Format(entity)} — {refusal}");
            refusals.Add(refusal);
            return false;
        }

        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRW(entity, type.TypeIndex);
            if (raw == null)
            {
                refusals.Add($"{label}: GetComponentDataRawRW returned null on {Format(entity)}");
                return false;
            }
            *(float*)(raw + TeleportArgs.PositionOffset) = value.x;
            *(float*)(raw + TeleportArgs.PositionOffset + 4) = value.y;
            *(float*)(raw + TeleportArgs.PositionOffset + 8) = value.z;
            return true;
        }
        catch (Exception e)
        {
            refusals.Add($"{label}: write on {Format(entity)} failed: {e.Message}");
            return false;
        }
    }

    private static void ResolveDestination(
        EntityManager em,
        TeleportArgs.Request request,
        out float3 destination,
        out Entity destinationEntity)
    {
        destinationEntity = Entity.Null;
        if (request.DestinationKind == TeleportArgs.DestinationKind.Point)
        {
            destination = new float3(request.X, request.Y, request.Z);
            return;
        }

        destinationEntity = request.DestinationKind == TeleportArgs.DestinationKind.Entity
            ? CharacterLookup.FromEntityId(em, request.Destination, "toEntity")
            : CharacterLookup.FromUserName(em, request.Destination);

        destination = EntityOps.TryPosition(em, destinationEntity)
            ?? throw new InvalidOperationException(
                $"destination {Format(destinationEntity)} has no Translation or LocalToWorld, so there is nowhere to teleport to");
    }

    /// <summary>
    /// After a game revive, wait until <c>Dead</c> is gone, then run the normal teleport
    /// (floor snap, game helper, write if that helper refuses). The game event itself has
    /// no position field.
    /// </summary>
    internal static object PlaceWhenAlive(World world, Entity user, Entity character, float3 destination)
    {
        var deferred = new DeferredResult(8000);
        MainThread.PostDelayed(TeleportArgs.ReadbackDelaySeconds, () =>
            ContinueAfterRespawn(world, user, character, destination, deferred, 0, null), deferred);
        return deferred;
    }

    /// <summary>
    /// The game revive writes the crypt or waygate <i>after</i> <c>Dead</c> clears. Moving
    /// on the first living read gets overwritten. Wait until that placement has stopped,
    /// then teleport to the bound point.
    /// </summary>
    private static void ContinueAfterRespawn(
        World world, Entity user, Entity character, float3 destination, DeferredResult deferred, int attempt, float3? settled)
    {
        try
        {
            if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                return;
            if (world == null || !world.IsCreated)
            {
                deferred.SetException(new InvalidOperationException("respawn point: world is gone"));
                return;
            }

            var em = world.EntityManager;
            character = LiveCharacter(em, user, character);
            if (!em.Exists(character))
            {
                deferred.SetException(new InvalidOperationException("respawn point: character is gone"));
                return;
            }

            if (em.HasComponent<ProjectM.Dead>(character))
            {
                if (attempt < 6)
                {
                    MainThread.PostDelayed(TeleportArgs.ReadbackDelaySeconds, () =>
                        ContinueAfterRespawn(world, user, character, destination, deferred, attempt + 1, null), deferred);
                    return;
                }
                deferred.SetResult(new
                {
                    action = "respawn",
                    location = "point",
                    character = Format(character),
                    arrived = false,
                    reason = "still dead",
                });
                return;
            }

            var pos = EntityOps.TryPosition(em, character);
            bool stable = pos.HasValue && settled.HasValue && math.distancesq(pos.Value, settled.Value) < 0.25f;
            if (!stable && attempt < 6)
            {
                MainThread.PostDelayed(TeleportArgs.ReadbackDelaySeconds, () =>
                    ContinueAfterRespawn(world, user, character, destination, deferred, attempt + 1, pos), deferred);
                return;
            }

            destination = SnapToFloor(world, destination);
            var before = pos ?? throw new InvalidOperationException("respawn point: character has no position");
            string method = Move(em, character, destination);
            string? name = CharacterLookup.TryName(em, character);
            Plugin.Logger?.LogInfo(TeleportArgs.LogLine(
                name ?? Format(character), before.x, before.y, before.z,
                destination.x, destination.y, destination.z, method));
            ReadBack(world, character, name, method, before, destination, Entity.Null, deferred);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("respawn point failed: " + e);
            deferred.SetException(e);
        }
    }

    private static Entity LiveCharacter(EntityManager em, Entity user, Entity fallback)
    {
        try
        {
            if (em.Exists(user) && em.HasComponent<ProjectM.Network.User>(user))
            {
                var character = em.GetComponentData<ProjectM.Network.User>(user).LocalCharacter._Entity;
                if (em.Exists(character))
                    return character;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("respawn point: user character lookup failed: " + e.Message);
        }
        return fallback;
    }

    /// <summary>
    /// Same deferred shape <c>spawn</c> uses: the move lands in a later system group than the
    /// bridge Drain, so a same-tick read would report the pre-move position.
    /// </summary>
    private static object ReadBack(
        World world,
        Entity character,
        string? name,
        string method,
        float3 before,
        float3 destination,
        Entity destinationEntity,
        DeferredResult? bound)
    {
        var deferred = bound ?? new DeferredResult(TeleportArgs.ReadbackWaitMs);
        try
        {
            MainThread.PostDelayed(TeleportArgs.ReadbackDelaySeconds, () =>
            {
                try
                {
                    if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                        return;

                    ReadLive(world, character, destinationEntity, out var ax, out var ay, out var az, out var destinationEntityId, out var destinationName);
                    if (method == GameMethod
                        && world.IsCreated
                        && !TeleportArgs.IsArrived(destination.x, destination.y, destination.z, ax, ay, az))
                    {
                        WritePosition(world.EntityManager, character, destination);
                        Plugin.Logger?.LogInfo(
                            "teleport: " + GameMethod + " did not move " + (name ?? Format(character))
                            + "; writing " + WriteMethod);
                        MainThread.PostDelayed(TeleportArgs.ReadbackDelaySeconds, () =>
                        {
                            try
                            {
                                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket))
                                    return;
                                ReadLive(world, character, destinationEntity, out var bx, out var by, out var bz, out var wroteEntityId, out var wroteName);
                                Complete(deferred, character, name, GameMethod + " refused; " + WriteMethod, before, destination, bx, by, bz, wroteEntityId, wroteName);
                            }
                            catch (Exception e)
                            {
                                Plugin.Logger?.LogError($"teleport read-back failed: {e}");
                                deferred.SetException(e);
                            }
                        }, deferred);
                        return;
                    }

                    Complete(deferred, character, name, method, before, destination, ax, ay, az, destinationEntityId, destinationName);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"teleport read-back failed: {e}");
                    deferred.SetException(e);
                }
            }, deferred);
        }
        catch (Exception e)
        {
            deferred.SetException(e);
            throw;
        }

        return deferred;
    }

    private static void ReadLive(
        World world,
        Entity character,
        Entity destinationEntity,
        out float? x,
        out float? y,
        out float? z,
        out string? destinationEntityId,
        out string? destinationName)
    {
        x = y = z = null;
        destinationEntityId = destinationEntity == Entity.Null ? null : Format(destinationEntity);
        destinationName = null;
        try
        {
            if (!world.IsCreated) return;
            var em = world.EntityManager;
            if (em.Exists(character))
            {
                var found = EntityOps.TryPosition(em, character);
                if (found.HasValue)
                {
                    x = found.Value.x;
                    y = found.Value.y;
                    z = found.Value.z;
                }
            }
            if (destinationEntity != Entity.Null && em.Exists(destinationEntity))
                destinationName = CharacterLookup.TryName(em, destinationEntity);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"teleport read-back of {Format(character)} failed: {e.Message}");
        }
    }

    private static void Complete(
        DeferredResult deferred,
        Entity character,
        string? name,
        string method,
        float3 before,
        float3 destination,
        float? afterX,
        float? afterY,
        float? afterZ,
        string? destinationEntityId,
        string? destinationName)
    {
        deferred.SetResult(TeleportArgs.Result(
            Format(character),
            name,
            method,
            before.x, before.y, before.z,
            destination.x, destination.y, destination.z,
            afterX, afterY, afterZ,
            destinationEntityId,
            destinationName));
    }

    private static string Format(Entity entity) => $"{entity.Index}:{entity.Version}";
}
