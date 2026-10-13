#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Ecs;
using Progenitor.Install;
using Progenitor.Networking;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>Own-player, pull-only effective values while the native character sheet is visible.</summary>
internal static class CharacterSheetChannel
{
    internal const int EventId = 0x50724373; // PrCs
    private static readonly RemoteInstallRateLimiter Reads = new(8, 1);
    private static string _epoch = Guid.NewGuid().ToString("N");
    private static int _sequence, _pending;
    private static string _equipment = "";
    private static double _next, _deadline, _received;
    private static Dictionary<int, float>? _values;
    private static bool _installed;
    internal static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal static unsafe void Install()
    {
        if (_installed) return;
        _installed = true;
        if (Plugin.IsServerProcess) CustomEvents.OnReceiveFromClient(EventId, OnRequest);
        else { CustomEvents.OnReceive(EventId, OnResponse); Handshake.ServerContextCleared += Clear; }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        if (Plugin.IsServerProcess) CustomEvents.RemoveClientHandler(EventId);
        else { CustomEvents.RemoveHandler(EventId); Handshake.ServerContextCleared -= Clear; }
        Clear("unloaded");
    }

    internal static void Clear(string reason)
    {
        _epoch = Guid.NewGuid().ToString("N");
        _pending = 0; _values = null; _equipment = ""; _next = 0;
    }

    internal static IReadOnlyDictionary<int, float>? Update(World world, Entity character)
    {
        string equipment = EquipmentKey(world.EntityManager, character);
        double now = Now;
        if (equipment != _equipment)
        { double next = _next; Clear("equipment changed"); _equipment = equipment; _next = next; }
        if (_pending != 0 && now >= _deadline) { _pending = 0; _values = null; _next = now + 10; }
        if (_pending == 0 && now >= _next && equipment.Length != 0)
        {
            _sequence = _sequence == int.MaxValue ? 1 : _sequence + 1;
            _pending = _sequence; _deadline = now + 2; _next = now + 0.25;
            var packet = new CharacterSheetPacket { Nonce = _pending, Epoch = _epoch, Equipment = equipment };
            if (!CustomEvents.SendToServer(EventId, JsonSerializer.SerializeToUtf8Bytes(packet)))
            { _pending = 0; _next = now + 2; }
        }
        return now - _received <= 1 && _values != null ? _values : null;
    }

    private static unsafe CharacterSheetPacket? Decode(byte* bytes, int length)
    {
        if (bytes == null || length <= 0 || length > CharacterSheetSnapshot.MaxBytes) return null;
        try { return JsonSerializer.Deserialize<CharacterSheetPacket>(new ReadOnlySpan<byte>(bytes, length)); }
        catch { return null; }
    }

    private static unsafe void OnResponse(int _, byte* bytes, int length)
    {
        var response = Decode(bytes, length);
        if (!CharacterSheetSnapshot.IsReplyFor(response, _pending, _epoch)) return;
        if (response!.Equipment != _equipment)
        { _pending = 0; _values = null; _next = Now + 0.25; return; }
        _pending = 0; _received = Now; _values = response!.Values;
    }

    private static unsafe void OnRequest(int _, Entity userEntity, User ignored, byte* bytes, int length)
    {
        var request = Decode(bytes, length);
        if (!CharacterSheetSnapshot.Valid(request, false)) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        if (!userEntity.Has<User>(em)) return;
        var user = em.GetComponentData<User>(userEntity);
        if (!user.IsConnected || Handshake.UserAnswerStatus(user.Index) != "ok" || !Reads.TryTake(user.PlatformId, Now)) return;
        var character = user.LocalCharacter._Entity;
        if (character == Entity.Null || !em.Exists(character)) return;
        string equipment = EquipmentKey(em, character);
        // A predicted equip may not yet be accepted by the server. Never paint the previous gear's values over it.
        var response = new CharacterSheetPacket { Response = true, Nonce = request!.Nonce, Epoch = request.Epoch,
            Equipment = CharacterSheetSnapshot.CompatibleEquipment(request.Equipment, equipment) ? request.Equipment : equipment,
            Values = CharacterStatReader.Read(em, character) };
        var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
        if (bootstrap == null) return;
        var encoded = JsonSerializer.SerializeToUtf8Bytes(response);
        if (encoded.Length <= CharacterSheetSnapshot.MaxBytes) CustomEvents.SendToUser(bootstrap, user.Index, EventId, encoded);
    }

    internal static string EquipmentKey(EntityManager em, Entity character)
    {
        if (!character.Has<Equipment>(em)) return "";
        var e = em.GetComponentData<Equipment>(character);
        string key = string.Join("/", NetworkKey(em, character), Key(em, e.WeaponSlot), Key(em, e.ArmorHeadgearSlot), Key(em, e.ArmorChestSlot),
            Key(em, e.ArmorGlovesSlot), Key(em, e.ArmorLegsSlot), Key(em, e.ArmorFootgearSlot), Key(em, e.CloakSlot), Key(em, e.GrimoireSlot), Key(em, e.BagSlot));
        return key.Contains("pending", StringComparison.Ordinal) ? "" : key;
    }
    private static string Key(EntityManager em, EquipmentSlot s) =>
        s.SlotId.GuidHash == 0 ? "empty" : s.SlotId.GuidHash + (s.IsBroken ? "b" : "w") + ItemNetworkKey(em, s.SlotEntity._Entity);
    private static string ItemNetworkKey(EntityManager em, Entity entity)
    {
        string key = NetworkKey(em, entity);
        return key == "pending" ? "unknown" : key;
    }
    private static string NetworkKey(EntityManager em, Entity entity)
    {
        if (entity == Entity.Null) return "pending";
        if (!entity.Has<NetworkId>(em)) return "pending";
        var id = em.GetComponentData<NetworkId>(entity); // Existing typed accessor used by Chat.
        if (id.Type == NetworkIdType.None) return "pending";
        return id.Type == NetworkIdType.Normal
            ? "n:" + id.Normal_Index + ":" + id.Normal_Generation
            : "s:" + (int)id.Type + ":" + id.MegaStatic_StaticTransformIndex + ":" + id.MegaStatic_TerrainChunkX
                + ":" + id.MegaStatic_TerrainChunkY + ":" + id.MegaStatic_PrefabGUID;
    }

    internal static object Describe() => new { pending = _pending, equipment = _equipment,
        ageSeconds = _values == null ? -1 : Now - _received, values = _values };
}
#endif

