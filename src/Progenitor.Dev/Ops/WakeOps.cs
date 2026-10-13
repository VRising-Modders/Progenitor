using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProjectM;
using ProjectM.Gameplay.Systems;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Server op <c>wake</c>: press "wake up" for a new character that is still asleep in the crypt,
/// for headless clients, which have no keyboard.
///
/// <para><b>Mechanism</b> (native server <c>SpawnCharacterSystem</c> job): a first-time spawn
/// keeps the character in <c>AB_Interact_CoffinSleeping</c> and hidden from other players until
/// the character's synced input carries <c>ButtonInputAction.Respawn</c>
/// (<c>SyncedButtonInputAction.Respawn</c> = 1024, the Space prompt). Removing the buff does not
/// help: the system re-applies it while <c>FirstTimeSpawn</c> is set. This op sets that bit in the
/// character's <c>EntityInput.State</c> immediately before <c>SpawnCharacterSystem</c> updates,
/// every frame, until that user's <c>SpawnCharacter</c> request is gone or 90 frames pass. The
/// client's next input packet overwrites the bit, which is why it is written per frame.</para>
///
/// <para>Args: <c>platformId</c> (string preferred) or <c>user</c> (character name).</para>
/// </summary>
internal static class WakeOps
{
    private const ushort RespawnButton = 1024; // SyncedButtonInputAction.Respawn
    private const int MaxFrames = 90;

    private sealed class Pending
    {
        public Entity User;
        public Entity Character;
        public string Name = "";
        public int Frames;
        public bool Done;
    }

    private static readonly object Gate = new();
    private static readonly List<Pending> Waking = new();
    private static IDisposable? _hook;
    private static bool _frameSubscribed;

    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("wake runs on the server side only");
        var em = world.EntityManager;
        var (userEntity, user) = ResolveUser(em, args);
        var character = user.LocalCharacter._Entity;
        if (character == Entity.Null || !em.Exists(character))
            throw new InvalidOperationException($"user {user.Index} has no character yet (create_character first)");
        if (FindSpawnRequest(em, userEntity) == Entity.Null)
            return new { action = "wake", name = user.CharacterName.ToString(), already = true, note = "no pending spawn request; the character is awake" };

        lock (Gate)
        {
            Waking.Add(new Pending { User = userEntity, Character = character, Name = user.CharacterName.ToString() });
            _hook ??= SystemHooks.Before<SpawnCharacterSystem>(PressBeforeSpawnSystem);
            if (!_frameSubscribed)
            {
                Progenitor.Plugin.Frame += OnFrame;
                _frameSubscribed = true;
            }
        }
        Plugin.Logger?.LogInfo($"wake: pressing Respawn for '{user.CharacterName}' ({character.Index}:{character.Version}) before SpawnCharacterSystem");
        return new { action = "wake", name = user.CharacterName.ToString(), character = $"{character.Index}:{character.Version}", already = false, note = "queued; poll users / the spawn request for completion" };
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            Waking.Clear();
            try { _hook?.Dispose(); } catch { /* best-effort */ }
            _hook = null;
            if (_frameSubscribed)
            {
                Progenitor.Plugin.Frame -= OnFrame;
                _frameSubscribed = false;
            }
        }
    }

    private static void PressBeforeSpawnSystem(SystemHookContext ctx)
    {
        var em = ctx.World.EntityManager;
        lock (Gate)
        {
            foreach (var p in Waking)
            {
                if (p.Done) continue;
                if (!em.Exists(p.Character) || FindSpawnRequest(em, p.User) == Entity.Null || ++p.Frames > MaxFrames)
                {
                    p.Done = true;
                    Plugin.Logger?.LogInfo($"wake: '{p.Name}' {(p.Frames > MaxFrames ? "still asleep after " + MaxFrames + " frames" : "spawn request finished")}");
                    continue;
                }
                if (!TryPressRespawn(em, p.Character, out string detail))
                {
                    p.Done = true;
                    Plugin.Logger?.LogWarning($"wake: '{p.Name}' {detail}");
                    continue;
                }
                if (p.Frames == 1) Plugin.Logger?.LogInfo($"wake: '{p.Name}' {detail}");
            }
        }
    }

    // EntityInput.State (0x48) . InputsPressed (+0x4) and InputsDown (+0x2), ushort each: server
    // and client metadata. The ECS TypeSize of EntityInput reads 0 through this interop, so a typed
    // SetComponentData is refused; poke the two fields after checking the interop layout agrees.
    private const int StateOffset = 0x48;
    private const int InputsDownOffset = 0x2;
    private const int InputsPressedOffset = 0x4;

    private static unsafe bool TryPressRespawn(EntityManager em, Entity character, out string detail)
    {
        int state = (int)Marshal.OffsetOf<EntityInput>(nameof(EntityInput.State));
        int down = (int)Marshal.OffsetOf<SyncedInputState>(nameof(SyncedInputState.InputsDown));
        int pressed = (int)Marshal.OffsetOf<SyncedInputState>(nameof(SyncedInputState.InputsPressed));
        int extent = Marshal.SizeOf<EntityInput>();
        if (state != StateOffset || down != InputsDownOffset || pressed != InputsPressedOffset
            || extent < StateOffset + InputsPressedOffset + sizeof(ushort))
        {
            detail = $"EntityInput layout differs from the dump (State {state:X}, Down {down:X}, Pressed {pressed:X}, size {extent}); not writing";
            return false;
        }
        var type = ComponentIndex.Of<EntityInput>(ComponentType.AccessMode.ReadWrite);
        if (!em.HasComponent(character, type))
        {
            detail = "character has no EntityInput";
            return false;
        }
        int ecsSize = TypeManager.GetTypeInfo(type.TypeIndex).TypeSize;
        if (ecsSize != 0 && ecsSize < StateOffset + InputsPressedOffset + sizeof(ushort))
        {
            detail = $"EntityInput ECS TypeSize {ecsSize} is smaller than the field; not writing";
            return false;
        }
        byte* raw = (byte*)em.GetComponentDataRawRW(character, type.TypeIndex);
        if (raw == null)
        {
            detail = "GetComponentDataRawRW returned null";
            return false;
        }
        ushort before = *(ushort*)(raw + StateOffset + InputsPressedOffset);
        *(ushort*)(raw + StateOffset + InputsDownOffset) |= RespawnButton;
        *(ushort*)(raw + StateOffset + InputsPressedOffset) |= RespawnButton;
        detail = $"InputsPressed {before} -> {*(ushort*)(raw + StateOffset + InputsPressedOffset)} (ECS TypeSize {ecsSize})";
        return true;
    }

    // Hooks are disposed outside the hook callback.
    private static void OnFrame()
    {
        lock (Gate)
        {
            Waking.RemoveAll(p => p.Done);
            if (Waking.Count > 0) return;
        }
        Shutdown();
    }

    private static Entity FindSpawnRequest(EntityManager em, Entity userEntity)
    {
        var requests = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<SpawnCharacter>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < requests.Count; i++)
        {
            if (requests[i].TryRead<SpawnCharacter>(em, out var s) && s.User == userEntity)
                return requests[i];
        }
        return Entity.Null;
    }

    private static (Entity, User) ResolveUser(EntityManager em, JsonElement args)
    {
        ulong? platformId = null;
        string? name = null;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("platformId", out var p))
            {
                if (p.ValueKind == JsonValueKind.String && ulong.TryParse(p.GetString(), out var ps)) platformId = ps;
                else if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out var pn)) platformId = pn;
            }
            if (args.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.String) name = u.GetString();
        }
        if (platformId == null && string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("args.platformId or args.user (character name) is required");
        var users = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < users.Count; i++)
        {
            var u = em.GetComponentData<User>(users[i]);
            if ((platformId != null && u.PlatformId == platformId.Value)
                || (platformId == null && string.Equals(u.CharacterName.ToString(), name, StringComparison.OrdinalIgnoreCase)))
                return (users[i], u);
        }
        throw new InvalidOperationException("no such user");
    }
}
