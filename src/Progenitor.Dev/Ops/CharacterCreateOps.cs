using System;
using System.Text.Json;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Op <c>create_character</c>: create the first character for a user that has none, for headless
/// extra clients, which never show the creation screen.
///
/// <para><b>Client (preferred).</b> On a client with a world, it sends the same
/// <see cref="CreateCharacterEvent"/> network event the creation screen sends
/// (<c>NetworkEventType.EventId = NetworkEvents.EventId_CreateCharacterEvent</c>, 527272789 in both
/// the client and server metadata, plus <c>SendNetworkEventTag</c>), so the client's own join flow sees its character arrive.
/// The server's <c>HandleCreateCharacterEventSystem</c> validates the name and spawns it.</para>
///
/// <para><b>Server (fallback).</b> Enqueues the inbound event + <see cref="FromCharacter"/> pair for
/// a connected user. The character spawns, but that user's client stays on its creation screen
/// (verified live 2026-09-24: the player saw the creation preview while the compass moved);
/// disconnect and reconnect that client afterwards and it loads straight into the character.</para>
///
/// <para>A new character starts asleep in the crypt, hidden from other players, until its client
/// presses the wake-up key. A headless client cannot; run the server <c>wake</c> op
/// (<see cref="WakeOps"/>).</para>
///
/// <para>Args: <c>name</c> (required, the game's name rules apply); server side also
/// <c>platformId</c> (string or number) or <c>userIndex</c>; optional appearance bytes
/// <c>gender</c>, <c>skin</c>, <c>face</c>, <c>eyes</c>, <c>hairStyle</c>, <c>hairColor</c>,
/// <c>features</c>, <c>accessories</c> (default 0).</para>
/// </summary>
internal static class CharacterCreateOps
{
    /// <summary><c>NetworkEvents.EventId_CreateCharacterEvent</c> (client and server metadata).</summary>
    internal const int CreateCharacterEventId = 527272789;

    public static object Run(World world, JsonElement args)
    {
        var em = world.EntityManager;
        string name = ReadString(args, "name") ?? throw new ArgumentException("args.name is required");
        name = name.Trim();
        if (name.Length == 0 || name.Length > 20)
            throw new ArgumentException("args.name must be 1..20 characters");
        var appearance = ReadAppearance(args, name);
        if (!Progenitor.Plugin.IsServerProcess)
            return SendFromClient(em, appearance);

        var userEntity = ResolveUser(em, args, out var user);
        if (!user.IsConnected)
            throw new InvalidOperationException($"user {user.Index} is not connected");
        var existing = user.LocalCharacter._Entity;
        if (existing != Entity.Null && em.Exists(existing))
            throw new InvalidOperationException(
                $"user {user.Index} already has character {existing.Index}:{existing.Version} ('{user.CharacterName}')");

        var ev = em.CreateEntity(
            ComponentIndex.Of<CreateCharacterEvent>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadWrite));
        ev.Write(em, appearance);
        em.SetComponentData(ev, new FromCharacter { User = userEntity, Character = Entity.Null });

        Plugin.Logger?.LogInfo(
            $"create_character: user {user.Index} platformId {user.PlatformId} name '{name}' event {ev.Index}:{ev.Version}");
        return new
        {
            action = "create_character",
            side = "server",
            userIndex = user.Index,
            platformId = user.PlatformId.ToString(),
            name,
            @event = $"{ev.Index}:{ev.Version}",
            note = "queued; poll users for the character (the game validates the name and spawns it)",
        };
    }

    private static object SendFromClient(EntityManager em, CreateCharacterEvent appearance)
    {
        // Idempotent, so a launcher can retry: a server drops a create sent while the join is
        // still loading (live 2026-09-24: the first send vanished, a resend seconds later spawned).
        var local = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (local.Count > 0)
        {
            var c = local[0];
            return new { action = "create_character", side = "client", already = true, character = $"{c.Index}:{c.Version}" };
        }

        var ev = em.CreateEntity(
            ComponentIndex.Of<CreateCharacterEvent>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite));
        ev.Write(em, appearance);
        // NetworkEventType has bools, so it is not C# unmanaged: typed SetComponentData (as Chat does).
        em.SetComponentData(ev, new NetworkEventType { EventId = CreateCharacterEventId, IsAdminEvent = false, IsDebugEvent = false });
        string name = appearance.Name.ToString();
        Plugin.Logger?.LogInfo($"create_character: client sent CreateCharacterEvent name '{name}' event {ev.Index}:{ev.Version}");
        return new
        {
            action = "create_character",
            side = "client",
            already = false,
            name,
            @event = $"{ev.Index}:{ev.Version}",
            note = "sent; the server validates the name and spawns the character",
        };
    }

    private static CreateCharacterEvent ReadAppearance(JsonElement args, string name) => new()
    {
        GenderIndex = ReadByte(args, "gender"),
        SkintoneIndex = ReadByte(args, "skin"),
        FaceIndex = ReadByte(args, "face"),
        EyeColorIndex = ReadByte(args, "eyes"),
        HairStyleIndex = ReadByte(args, "hairStyle"),
        HairColorIndex = ReadByte(args, "hairColor"),
        FeaturesIndex = ReadByte(args, "features"),
        AccessoriesIndex = ReadByte(args, "accessories"),
        Name = new FixedString64Bytes(name),
    };

    private static Entity ResolveUser(EntityManager em, JsonElement args, out User user)
    {
        ulong? platformId = null;
        int? userIndex = null;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("platformId", out var p))
            {
                if (p.ValueKind == JsonValueKind.String && ulong.TryParse(p.GetString(), out var ps)) platformId = ps;
                else if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out var pn)) platformId = pn;
                else throw new ArgumentException("args.platformId must be an unsigned integer (string preferred)");
            }
            if (args.TryGetProperty("userIndex", out var ui) && ui.ValueKind == JsonValueKind.Number)
                userIndex = ui.GetInt32();
        }
        if (platformId == null && userIndex == null)
            throw new ArgumentException("args.platformId or args.userIndex is required");

        var entities = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < entities.Count; i++)
        {
            var u = em.GetComponentData<User>(entities[i]);
            if ((platformId != null && u.PlatformId == platformId.Value)
                || (platformId == null && u.Index == userIndex!.Value))
            {
                user = u;
                return entities[i];
            }
        }
        throw new InvalidOperationException(
            platformId != null ? $"no User with platformId {platformId}" : $"no User with index {userIndex}");
    }

    private static string? ReadString(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static byte ReadByte(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) || n < 0 || n > 255)
            throw new ArgumentException($"args.{key} must be 0..255");
        return (byte)n;
    }
}
