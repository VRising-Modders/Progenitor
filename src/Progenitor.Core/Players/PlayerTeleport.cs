using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using ProjectM;
using Progenitor.Ecs;
using Progenitor.Units;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Players;

/// <summary>
/// MAPTP1: move a character the way the game's own server teleports do. Server, main thread.
///
/// <para>The game's helper first: <c>ProjectM.TeleportUtilityServer.Teleport(EntityManager, Entity,
/// float3, Nullable&lt;quaternion&gt;)</c> (<c>ProjectM.Shared</c>), a public static with
/// no admin gate and no network event to forge, the path the Dev bridge's <c>teleport</c> op has used
/// live. <c>SpawnTransform</c>, the AI leash home, is never touched.</para>
///
/// <para><see cref="Teleport"/> is the one-shot form: helper, or the <see cref="UnitSpawnPlacement"/> writes if the
/// helper throws, and no read-back. <see cref="TeleportVerified"/> is the checked form the debug-buff map teleport
/// uses: the helper, a read-back <see cref="PlayerTeleportPolicy.ReadbackDelaySeconds"/> later, and when the
/// character did not arrive the Dev op's guarded write of <c>Translation</c>, <c>LastTranslation</c> and
/// <c>PlayerLastValidPosition</c> (<see cref="TryWritePosition"/>, shared with that op) and a second read-back. It
/// reports arrival only from a read-back, never from the helper returning (live 2026-10-19: the helper returned
/// normally for castle-roof targets and moved nobody).</para>
///
/// <para>Mounted, shapeshifted or in combat makes no difference to the call; whether a mount follows its
/// rider is the game helper's business and has not been checked live.</para>
/// </summary>
public static class PlayerTeleport
{
    /// <summary>The <see cref="Teleport"/> method token for the game helper.</summary>
    public const string GameMethod = "TeleportUtilityServer.Teleport";

    /// <summary>The <see cref="Teleport"/> method token for the fallback writes.</summary>
    public const string WriteMethod = "Translation+LastTranslation";

    /// <summary>The method token of <see cref="TryWritePosition"/>, the three-component write.</summary>
    public const string PositionWriteMethod = "Translation+LastTranslation+PlayerLastValidPosition";

    /// <summary>
    /// Move <paramref name="character"/> to <paramref name="destination"/>. Returns the method that ran
    /// (<see cref="GameMethod"/> or <see cref="WriteMethod"/>), or null with <paramref name="why"/> set when
    /// neither could. No read-back: a returned method does not prove the character moved.
    /// </summary>
    public static string? Teleport(EntityManager em, Entity character, float3 destination, out string why)
    {
        why = "";
        if (character == Entity.Null || !em.Exists(character))
        {
            why = "no live character";
            return null;
        }

        string helperError;
        try
        {
            TeleportUtilityServer.Teleport(em, character, destination);
            return GameMethod;
        }
        catch (Exception e)
        {
            helperError = e.Message;
        }

        if (UnitSpawnPlacement.TryWriteTranslation(em, character, destination, "PlayerTeleport"))
        {
            UnitSpawnPlacement.TryWriteLastTranslation(em, character, destination, "PlayerTeleport");
            Plugin.Logger?.LogWarning("PlayerTeleport: the game helper threw (" + helperError + "); wrote the position instead.");
            return WriteMethod;
        }

        why = "the game helper threw (" + helperError + ") and the position could not be written";
        return null;
    }

    /// <summary>What <see cref="TeleportVerified"/> found.</summary>
    /// <param name="Arrived">The read-back put the character at the destination.</param>
    /// <param name="Method">What ran: <see cref="GameMethod"/>, <see cref="PositionWriteMethod"/>, or the helper then the write.</param>
    /// <param name="Why">Empty when arrived; otherwise the reason, for the log.</param>
    /// <param name="After">The last read position, or null when it could not be read.</param>
    public readonly record struct VerifiedTeleport(bool Arrived, string Method, string Why, float3? After);

    /// <summary>
    /// Move <paramref name="character"/> to <paramref name="destination"/> and check it arrived. Calls
    /// <paramref name="done"/> on the main thread, once: right away when nothing could run, else after one read-back
    /// (two when the fallback write ran). The delayed steps are posted under <paramref name="owner"/>, so an unload
    /// drops them and <paramref name="done"/> is then never called. Server, main thread.
    /// </summary>
    public static void TeleportVerified(World world, Entity character, float3 destination, object? owner, Action<VerifiedTeleport> done)
    {
        if (done == null) throw new ArgumentNullException(nameof(done));
        if (world == null || !world.IsCreated)
        {
            done(new VerifiedTeleport(false, "", "no game world", null));
            return;
        }

        var em = world.EntityManager;
        if (character == Entity.Null || !em.Exists(character) || !UnitSpawnPlacement.TryReadTranslation(em, character, out var before))
        {
            done(new VerifiedTeleport(false, "", "no live character with a position", null));
            return;
        }

        string helperError = "";
        bool helperRan;
        try
        {
            TeleportUtilityServer.Teleport(em, character, destination);
            helperRan = true;
        }
        catch (Exception e)
        {
            helperRan = false;
            helperError = e.Message;
        }

        if (!helperRan)
        {
            Plugin.Logger?.LogWarning("PlayerTeleport: the game helper threw (" + helperError + "); writing the position instead.");
            WriteThenCheck(world, character, before, destination, owner, PositionWriteMethod, done);
            return;
        }

        Dev.MainThread.PostDelayed(PlayerTeleportPolicy.ReadbackDelaySeconds, () =>
        {
            try
            {
                var after = Read(world, character);
                var arrival = Check(before, destination, after);
                var next = PlayerTeleportPolicy.AfterHelper(arrival, fallbackAllowed: arrival != PlayerTeleportPolicy.Arrival.Unknown);
                if (next == PlayerTeleportPolicy.Next.Done)
                {
                    done(new VerifiedTeleport(true, GameMethod, "", after));
                    return;
                }

                if (next == PlayerTeleportPolicy.Next.Refused)
                {
                    done(new VerifiedTeleport(false, GameMethod, "the character's position could not be read back", after));
                    return;
                }

                Plugin.Logger?.LogInfo("PlayerTeleport: " + GameMethod + " did not move the character; writing " + PositionWriteMethod);
                WriteThenCheck(world, character, before, destination, owner,
                    PlayerTeleportPolicy.HelperThenWrite(GameMethod, PositionWriteMethod), done);
            }
            catch (Exception e)
            {
                done(new VerifiedTeleport(false, GameMethod, "read-back threw: " + e.Message, null));
            }
        }, owner);
    }

    private static void WriteThenCheck(
        World world, Entity character, float3 before, float3 destination, object? owner, string method, Action<VerifiedTeleport> done)
    {
        var refusals = new List<string>();
        bool wrote;
        try
        {
            wrote = world.IsCreated && TryWritePosition(world.EntityManager, character, destination, refusals);
        }
        catch (Exception e)
        {
            wrote = false;
            refusals.Add("write threw: " + e.Message);
        }

        if (!wrote)
        {
            done(new VerifiedTeleport(false, method, refusals.Count > 0 ? string.Join("; ", refusals) : "no position component to write", null));
            return;
        }

        Dev.MainThread.PostDelayed(PlayerTeleportPolicy.ReadbackDelaySeconds, () =>
        {
            try
            {
                var after = Read(world, character);
                var next = PlayerTeleportPolicy.AfterWrite(Check(before, destination, after));
                done(next == PlayerTeleportPolicy.Next.Done
                    ? new VerifiedTeleport(true, method, "", after)
                    : new VerifiedTeleport(false, method, "the character was not at the target after the position write", after));
            }
            catch (Exception e)
            {
                done(new VerifiedTeleport(false, method, "read-back threw: " + e.Message, null));
            }
        }, owner);
    }

    private static float3? Read(World world, Entity character)
    {
        if (!world.IsCreated) return null;
        var em = world.EntityManager;
        if (!em.Exists(character)) return null;
        return UnitSpawnPlacement.TryReadTranslation(em, character, out var p) ? p : null;
    }

    private static PlayerTeleportPolicy.Arrival Check(float3 before, float3 target, float3? after) =>
        PlayerTeleportPolicy.Check(before.x, before.y, before.z, target.x, target.y, target.z, after?.x, after?.y, after?.z);

    /// <summary>
    /// The Dev op's fallback, shared: field pokes of the <c>float3</c> at offset 0 of <c>Translation</c>,
    /// <c>LastTranslation</c> (the synced one the client interpolates from) and <c>PlayerLastValidPosition</c>, the
    /// three a server teleport system writes together, through <c>GetComponentDataRawRW</c> behind
    /// <see cref="PlayerTeleportPolicy.PositionWriteRefusal"/>. A component the entity does not have is skipped; one
    /// that fails the guard is refused and named in <paramref name="refusals"/>. True when at least one was written.
    /// Server, main thread.
    /// </summary>
    public static bool TryWritePosition(EntityManager em, Entity character, float3 destination, List<string> refusals)
    {
        if (refusals == null) throw new ArgumentNullException(nameof(refusals));
        bool wroteAny = false;
        wroteAny |= Poke<Unity.Transforms.Translation>(em, character, destination, "Translation", refusals);
        wroteAny |= Poke<LastTranslation>(em, character, destination, "LastTranslation", refusals);
        wroteAny |= Poke<PlayerLastValidPosition>(em, character, destination, "PlayerLastValidPosition", refusals);
        return wroteAny;
    }

    private static unsafe bool Poke<T>(EntityManager em, Entity entity, float3 value, string label, List<string> refusals)
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
        int ecsTypeSize, interopSize, offset, fieldSize;
        try
        {
            // Two separate numbers: the raw ECS TypeSize the write guard is written against, and the interop extent
            // the FieldOffset metadata is read out of (the Dev op's ComponentDump does the same).
            ecsTypeSize = TypeManager.GetTypeInfo(type.TypeIndex).TypeSize;
            interopSize = ecsTypeSize > 0 ? ecsTypeSize : SafeMarshalSize(typeof(T));
            FindFloat3(typeof(T), interopSize, out offset, out fieldSize);
        }
        catch (Exception e)
        {
            refusals.Add($"{label}: could not size '{typeName}' for the write guard: {e.Message}");
            return false;
        }

        var refusal = PlayerTeleportPolicy.PositionWriteRefusal(label, typeName, ecsTypeSize, interopSize, offset, fieldSize);
        if (refusal != null)
        {
            Plugin.Logger?.LogWarning("PlayerTeleport: refusing the position write on " + entity.Index + ":" + entity.Version + " — " + refusal);
            refusals.Add(refusal);
            return false;
        }

        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRW(entity, type.TypeIndex);
            if (raw == null)
            {
                refusals.Add($"{label}: GetComponentDataRawRW returned null");
                return false;
            }

            *(float*)(raw + PlayerTeleportPolicy.PositionOffset) = value.x;
            *(float*)(raw + PlayerTeleportPolicy.PositionOffset + 4) = value.y;
            *(float*)(raw + PlayerTeleportPolicy.PositionOffset + 8) = value.z;
            return true;
        }
        catch (Exception e)
        {
            refusals.Add($"{label}: write failed: {e.Message}");
            return false;
        }
    }

    private static int SafeMarshalSize(Type type)
    {
        try { return Marshal.SizeOf(type); }
        catch { return 0; }
    }

    /// <summary>
    /// The first <c>float3</c> among the interop struct's <c>FieldOffset</c> fields: its offset and size (12, or the
    /// gap to the next field when that is smaller). Offset -1 when there is none.
    /// </summary>
    private static void FindFloat3(Type type, int structSize, out int offset, out int size)
    {
        offset = -1;
        size = 0;
        var fields = new List<(Type Type, int Offset)>();
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            var fo = field.GetCustomAttribute<FieldOffsetAttribute>();
            if (fo != null) fields.Add((field.FieldType, fo.Value));
        }

        fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].Type != typeof(float3)) continue;
            int next = i + 1 < fields.Count ? fields[i + 1].Offset : structSize;
            int span = Math.Max(0, next - fields[i].Offset);
            offset = fields[i].Offset;
            size = span > 0 && span < 12 ? span : 12;
            return;
        }
    }
}
