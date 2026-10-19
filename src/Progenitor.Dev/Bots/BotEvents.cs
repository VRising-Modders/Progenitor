using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProjectM;
using ProjectM.CastleBuilding.Rebuilding;
using ProjectM.Network;
using ProjectM.Tiles;
using Progenitor.Drops;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Client op <c>bot_event</c>: send, from the headless bot's own client world, the network events
/// a player's client sends from the build menu, crafting menus, inventory and journal. The server
/// runs its normal validation on each (blueprint/recipe unlock, costs, range, territory), so a
/// sent event proves only that it left the client; success must be observed afterwards (server
/// <c>bot_inventory</c>, <c>castles</c>, <c>Equipment</c> reads). A refused event is silent.
///
/// <para>Recipe: the one <see cref="Ops.CharacterCreateOps"/> uses for <c>CreateCharacterEvent</c>
/// (verified live 2026-09-24): an entity with the event component + <c>NetworkEventType</c> +
/// <c>SendNetworkEventTag</c>; <c>SerializeAndSendClientEventsSystem</c> sends it and the server
/// adds <c>FromCharacter</c>. Event payloads go through <c>ev.Write</c> (raw
/// <c>SetComponentDataRaw</c> by TypeIndex, TypeSize-guarded, no IL2CPP generic);
/// <c>NetworkEventType</c> through typed <c>SetComponentData</c> as in CharacterCreateOps.</para>
///
/// <para>Kinds, with the <c>NetworkEvents.EventId_*</c> constant (the client and server metadata
/// constants are identical):</para>
/// <list type="bullet">
/// <item><c>build</c>: <c>ProjectM.BuildTileModelEvent</c> (825449274):
/// PrefabGuid @0x0, SpawnTranslation @0x4, SpawnTileRotation byte @0x10, VariationIndex @0x11,
/// ResourceConsumeType byte @0x12 (SharedInventory=0, LocalInventory=1), RebuildUniqueKey
/// int @0x14. Args <c>prefab</c> (GUID or name), <c>x</c>, <c>y</c>, <c>z</c> (sent as is,
/// no snapping), <c>rotation</c> 0..3 (default 0), <c>variation</c> (default 0), <c>consume</c>
/// (default 1), <c>rebuildKey</c> (default 0).</item>
/// <item><c>craft</c>: <c>StartCharacterCraftItemEvent</c> (-215712725):
/// RecipeId @0x0. With <c>station</c> ("index:version" of a client entity):
/// <c>StartCraftItemEvent</c> (-382113386): Workstation NetworkId @0x0 (12 bytes,
/// copied raw from the station's own <c>NetworkId</c> as the <c>bot</c> hover action does),
/// RecipeId @0xC. Args <c>recipe</c> (GUID or name), optional <c>station</c>.</item>
/// <item><c>equip</c>: <c>EquipItemEvent</c> (9548331): SlotIndex int @0x0,
/// IsCosmetic bool @0x4. Args <c>slot</c> (main-bag index), optional <c>cosmetic</c>.</item>
/// <item><c>dropall</c>: <c>DropEntireInventoryEvent</c> (-131540227): Setting
/// (<c>DropEntireInventorySetting</c>, int) @0x0. Optional arg <c>setting</c>
/// (<see cref="DropAllSetting"/> name or 0..6, default All).</item>
/// <item><c>claim</c>: <c>ClaimAchievementEvent</c> (-514702814): Achievement
/// @0x0. Args <c>achievement</c> (GUID or name).</item>
/// </list>
///
/// <para>Status: source-checked against the client metadata and the headless client's interop
/// assemblies and built; not verified live. Open questions (default <c>consume</c>, build range,
/// unlock-on-claim) are in the internal research notes.</para>
/// </summary>
internal static class BotEvents
{
    // NetworkEvents constants from the client metadata (the server has the same values).
    internal const int BuildTileModelEventId = 825449274;
    internal const int ClaimAchievementEventId = -514702814;
    internal const int EquipItemEventId = 9548331;
    internal const int DropItemAtSlotEventId = -1012179922;
    internal const int DropEntireInventoryEventId = -131540227;
    internal const int StartCharacterCraftItemEventId = -215712725;
    internal const int StartCraftItemEventId = -382113386;
    // Empty "menu opened" events (each struct has no fields). They are
    // what the journal's OpenedMap / OpenedBuildMenu / OpenedCharacterMenu subtasks count.
    internal const int MapEnteredEventId = -948562153;
    internal const int BuildModeEnteredEventId = -226857865;
    internal const int CharacterMenuOpenedEventId = -1842970489;
    internal const int OpenVBloodMenuEventId = -2027225683;
    // Stations and containers.
    internal const int ToggleRefiningEventId = -1145461241;
    internal const int MoveAllItemsBetweenInventoriesEventV2Id = 1224436760;
    internal const int ActivateVBloodAbilityEventId = 961738838;
    internal const int EnterShapeshiftEventId = -1208165990;

    private const int NetworkIdSize = 12;

    public static object Run(World world, JsonElement args)
    {
        if (Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot_event runs on a client (a headless extra instance); it sends client-to-server events");
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("args must be an object with 'kind'");
        string kind = (Str(args, "kind") ?? throw new ArgumentException("args.kind is required (build, craft, equip, drop, dropall, claim, ui, toggle, moveall, spell, shapeshift)"))
            .Trim().ToLowerInvariant();
        var em = world.EntityManager;
        return kind switch
        {
            "build" => Build(world, em, args),
            "craft" => Craft(world, em, args),
            "equip" => Equip(em, args),
            "drop" => Drop(em, args),
            "dropall" => DropAll(em, args),
            "claim" => Claim(world, em, args),
            "ui" => Ui(em, args),
            "toggle" => Toggle(em, args),
            "moveall" => MoveAll(world, em, args),
            "spell" => Spell(world, em, args),
            "shapeshift" => Shapeshift(world, em, args),
            _ => throw new ArgumentException($"unknown kind '{kind}' (build, craft, equip, drop, dropall, claim, ui, toggle, moveall, spell, shapeshift)"),
        };
    }

    private static object Build(World world, EntityManager em, JsonElement args)
    {
        var guid = ResolveGuid(world, args, "prefab");
        var pos = new float3(Num(args, "x"), Num(args, "y"), Num(args, "z"));
        int rotation = IntOr(args, "rotation", 0);
        if (rotation < 0 || rotation > 3) throw new ArgumentException("args.rotation must be 0..3 (90-degree clockwise steps)");
        int variation = IntOr(args, "variation", 0);
        if (variation < 0 || variation > 255) throw new ArgumentException("args.variation must be 0..255");
        int consume = IntOr(args, "consume", 1);
        if (consume != 0 && consume != 1) throw new ArgumentException("args.consume must be 0 (SharedInventory) or 1 (LocalInventory)");
        int rebuildKey = IntOr(args, "rebuildKey", 0);

        var value = new BuildTileModelEvent
        {
            PrefabGuid = guid,
            SpawnTranslation = new Translation { Value = pos },
            SpawnTileRotation = (TileRotation)(byte)rotation,
            VariationIndex = (byte)variation,
            ResourceConsumeType = (BuildResourceConsumeType)(byte)consume,
            RebuildUniqueKey = new CastleRebuildUniqueKey { Id = rebuildKey },
        };
        var ev = Send(em, value, BuildTileModelEventId);
        Log($"build prefab {guid.GuidHash} at ({pos.x}, {pos.y}, {pos.z}) rot {rotation} consume {consume}", ev);
        return new
        {
            kind = "build",
            @event = Format(ev),
            eventId = BuildTileModelEventId,
            prefab = guid.GuidHash,
            prefabName = NameOf(world, guid),
            x = pos.x,
            y = pos.y,
            z = pos.z,
            rotation,
            variation,
            consume,
            rebuildKey,
            note = "sent; the server validates silently, observe the tile model appearing",
        };
    }

    private static object Craft(World world, EntityManager em, JsonElement args)
    {
        var recipe = ResolveGuid(world, args, "recipe");
        string? stationText = Str(args, "station");
        if (stationText == null)
        {
            var ev = Send(em, new StartCharacterCraftItemEvent { RecipeId = recipe }, StartCharacterCraftItemEventId);
            Log($"craft (character) recipe {recipe.GuidHash}", ev);
            return new
            {
                kind = "craft",
                @event = Format(ev),
                eventId = StartCharacterCraftItemEventId,
                recipe = recipe.GuidHash,
                recipeName = NameOf(world, recipe),
                station = (string?)null,
                note = "sent; observe the output item in the bag after the craft duration",
            };
        }

        var station = ParseEntity(stationText);
        var netType = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
        if (!em.Exists(station) || !em.HasComponent(station, netType))
            throw new InvalidOperationException($"station {stationText} does not exist or has no NetworkId");
        // The raw copy below assumes Workstation is the 12-byte NetworkId at 0x0 and RecipeId is at
        // 0xC; refuse if the interop wrapper disagrees.
        int netSize = Marshal.SizeOf<NetworkId>();
        int recipeOffset = (int)Marshal.OffsetOf<StartCraftItemEvent>(nameof(StartCraftItemEvent.RecipeId));
        int workstationOffset = (int)Marshal.OffsetOf<StartCraftItemEvent>(nameof(StartCraftItemEvent.Workstation));
        if (netSize != NetworkIdSize || recipeOffset != 0xC || workstationOffset != 0)
            throw new InvalidOperationException(
                $"StartCraftItemEvent layout mismatch: NetworkId size {netSize}, Workstation @{workstationOffset}, RecipeId @{recipeOffset} (dump: 12, 0, 12)");

        var value = new StartCraftItemEvent { RecipeId = recipe };
        int net0, net1, net2;
        unsafe
        {
            int* src = (int*)em.GetComponentDataRawRO(station, netType.TypeIndex);
            if (src == null) throw new InvalidOperationException($"GetComponentDataRawRO(NetworkId) returned null for {stationText}");
            net0 = src[0]; net1 = src[1]; net2 = src[2];
            int* dst = (int*)&value;
            dst[0] = net0; dst[1] = net1; dst[2] = net2;
        }
        var stationEv = Send(em, value, StartCraftItemEventId);
        Log($"craft (station {stationText}) recipe {recipe.GuidHash}", stationEv);
        return new
        {
            kind = "craft",
            @event = Format(stationEv),
            eventId = StartCraftItemEventId,
            recipe = recipe.GuidHash,
            recipeName = NameOf(world, recipe),
            station = Format(station),
            // NetworkId union: Normal_Index @0x0, Normal_Generation byte @0x4,
            // Type NetworkIdType byte @0x8 (522492: None=0, Normal=1, MegaStatic=2).
            stationNetworkId = new { index = net0, generation = net1 & 0xFF, type = net2 & 0xFF },
            note = "sent; observe the station's craft queue or the output item",
        };
    }

    private static unsafe object Equip(EntityManager em, JsonElement args)
    {
        int slot = IntOr(args, "slot", -1);
        if (slot < 0) throw new ArgumentException("args.slot (main-bag index, >= 0) is required");
        bool cosmetic = Bool(args, "cosmetic");
        // EquipItemEvent { int SlotIndex @0x0; bool IsCosmetic @0x4 }. Its ECS
        // TypeSize reads 0 on the client, so SetComponentDataRaw refuses the whole-struct write
        // ("zero sized component", live 2026-09-24); the two fields are poked through
        // GetComponentDataRawRW instead, as Core's input runtime does for EntityInput, after checking the interop
        // offsets agree with the client metadata.
        var ev = SendFieldPoked<EquipItemEvent>(em, EquipItemEventId, raw =>
        {
            *(int*)(raw + 0x0) = slot;
            raw[0x4] = cosmetic ? (byte)1 : (byte)0;
        }, (nameof(EquipItemEvent.SlotIndex), 0x0), (nameof(EquipItemEvent.IsCosmetic), 0x4));
        Log($"equip slot {slot} cosmetic {cosmetic}", ev);
        return new
        {
            kind = "equip",
            @event = Format(ev),
            eventId = EquipItemEventId,
            slot,
            cosmetic,
            note = "sent; observe the server Equipment component",
        };
    }

    /// <summary>
    /// Send the inventory menu's normal whole-stack drop event for the sender's main bag.
    /// DropItemAtSlotEvent.SlotIndex is int @0x0; native client/server
    /// handling is documented in the internal research notes. The server may refuse
    /// silently while casting, under a drop-impairing buff, for flagged items or in PvP combat.
    /// </summary>
    private static unsafe object Drop(EntityManager em, JsonElement args)
    {
        int slot = IntOr(args, "slot", -1);
        if (slot < 0) throw new ArgumentException("args.slot (main-bag index, >= 0) is required");
        if (args.TryGetProperty("amount", out _))
            throw new ArgumentException("args.amount is not supported: the event drops the whole stack");
        if (args.TryGetProperty("from", out _) || args.TryGetProperty("fromNetworkId", out _))
            throw new ArgumentException("drop only supports the sender's main bag; omit from/fromNetworkId");
        var ev = SendFieldPoked<DropItemAtSlotEvent>(em, DropItemAtSlotEventId, raw =>
        {
            *(int*)(raw + 0x0) = slot;
        }, (nameof(DropItemAtSlotEvent.SlotIndex), 0x0));
        Log($"drop slot {slot}", ev);
        return new
        {
            kind = "drop",
            @event = Format(ev),
            eventId = DropItemAtSlotEventId,
            slot,
            note = "sent; observe the donor slot empty and an ItemPickup nearby before claiming success",
        };
    }

    /// <summary>
    /// The inventory menu's "drop everything" button: <c>DropEntireInventoryEvent</c> with its
    /// <c>Setting</c> (what stays in place). The server's <c>DropItemSystem</c> handles it under the same
    /// rules as a single drop and may refuse silently; observe the bag and the dropped container.
    /// </summary>
    private static unsafe object DropAll(EntityManager em, JsonElement args)
    {
        var setting = BotDropAllArgs.Parse(args);
        int raw = BotDropAllArgs.Raw(setting);
        var ev = SendFieldPoked<DropEntireInventoryEvent>(em, DropEntireInventoryEventId, p =>
        {
            *(int*)(p + Layout.ClientDropEntireInventoryEventSettingOffset) = raw;
        }, (nameof(DropEntireInventoryEvent.Setting), Layout.ClientDropEntireInventoryEventSettingOffset));
        Log($"dropall setting {setting} ({raw})", ev);
        return new
        {
            kind = "dropall",
            @event = Format(ev),
            eventId = DropEntireInventoryEventId,
            setting = setting.ToString(),
            settingValue = raw,
            note = "sent; observe the bag emptied (minus what the setting keeps) and the dropped container nearby before claiming success",
        };
    }

    private static object Claim(World world, EntityManager em, JsonElement args)
    {
        var achievement = ResolveGuid(world, args, "achievement");
        var ev = Send(em, new ClaimAchievementEvent { Achievement = achievement }, ClaimAchievementEventId);
        Log($"claim achievement {achievement.GuidHash}", ev);
        return new
        {
            kind = "claim",
            @event = Format(ev),
            eventId = ClaimAchievementEventId,
            achievement = achievement.GuidHash,
            achievementName = NameOf(world, achievement),
            note = "sent; observe AchievementClaimedElement on the server",
        };
    }

    /// <summary>Creates the event entity, writes the payload raw and marks it for sending.</summary>
    private unsafe delegate void FieldPoke(byte* raw);

    /// <summary>
    /// <c>ui</c>: the empty event the client sends when a menu opens. Args <c>menu</c>: map, build,
    /// character or vblood. The event has no fields, so it is sent as the bare component plus
    /// <c>NetworkEventType</c> and <c>SendNetworkEventTag</c>, the same recipe as the other kinds.
    /// </summary>
    private static object Ui(EntityManager em, JsonElement args)
    {
        string menu = (Str(args, "menu") ?? throw new ArgumentException("args.menu is required (map, build, character, vblood)")).Trim().ToLowerInvariant();
        var (type, id) = menu switch
        {
            "map" => (ComponentIndex.Of<MapEnteredEvent>(ComponentType.AccessMode.ReadWrite), MapEnteredEventId),
            "build" => (ComponentIndex.Of<BuildModeEnteredEvent>(ComponentType.AccessMode.ReadWrite), BuildModeEnteredEventId),
            "character" => (ComponentIndex.Of<CharacterMenuOpenedEvent>(ComponentType.AccessMode.ReadWrite), CharacterMenuOpenedEventId),
            "vblood" => (ComponentIndex.Of<OpenVBloodMenuEvent>(ComponentType.AccessMode.ReadWrite), OpenVBloodMenuEventId),
            _ => throw new ArgumentException($"unknown menu '{menu}' (map, build, character, vblood)"),
        };
        var ev = em.CreateEntity(
            type,
            ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite));
        try
        {
            em.SetComponentData(ev, new NetworkEventType { EventId = id, IsAdminEvent = false, IsDebugEvent = false });
        }
        catch
        {
            em.DestroyEntity(ev); // never leave a send-tagged event with EventId 0 behind
            throw;
        }
        Log($"ui menu {menu}", ev);
        return new { kind = "ui", menu, @event = Format(ev), eventId = id };
    }

    /// <summary>
    /// <c>toggle</c>: <c>ToggleRefiningEvent { NetworkId Refinementstation @0x0 }</c>.
    /// Flips the station's <c>BurnContainer.Enabled</c> (a mist brazier's on switch); the
    /// server requires the character to be interacting with it. Args <c>target</c> ("index:version" of
    /// the station's client entity).
    /// </summary>
    private static unsafe object Toggle(EntityManager em, JsonElement args)
    {
        var (target, net) = NetworkIdOf(em, Str(args, "target") ?? throw new ArgumentException("args.target is required"));
        var ev = SendFieldPoked<ToggleRefiningEvent>(em, ToggleRefiningEventId, raw =>
        {
            *(int*)(raw + 0x0) = net.i0; *(int*)(raw + 0x4) = net.i1; *(int*)(raw + 0x8) = net.i2;
        }, (nameof(ToggleRefiningEvent.Refinementstation), 0x0));
        Log($"toggle {Format(target)}", ev);
        return new { kind = "toggle", @event = Format(ev), eventId = ToggleRefiningEventId, target = Format(target) };
    }

    /// <summary>
    /// <c>moveall</c>: <c>MoveAllItemsBetweenInventoriesEventV2 { NetworkId FromInventory @0x0;
    /// NetworkId ToInventory @0xC; PrefabGUID FilteredItem @0x18 }</c>, e.g.
    /// every bone from the bag into a mist brazier. Args <c>from</c> and <c>to</c> ("index:version" of
    /// client entities, or "self" for the bot's own character), <c>item</c> (GUID or name; omitted: no
    /// filter, every item, the container "take all", e.g. looting a death container). Both ends must be
    /// the bot's own inventory or one it is interacting with.
    /// </summary>
    private static unsafe object MoveAll(World world, EntityManager em, JsonElement args)
    {
        // An end is a client entity ("index:version"), "self", or a raw NetworkId as [i0, i1, i2]
        // (fromNetworkId / toNetworkId): a refining station's output inventory has no client entity the
        // bot can name, so bot_station reports its id (collecting from the station itself takes input).
        var (from, f) = RawNetworkId(args, "fromNetworkId") is { } fr ? (Entity.Null, fr) : NetworkIdOf(em, Str(args, "from") ?? "self");
        var (to, t) = RawNetworkId(args, "toNetworkId") is { } tr ? (Entity.Null, tr)
            : NetworkIdOf(em, Str(args, "to") ?? throw new ArgumentException("args.to (or toNetworkId) is required"));
        var item = args.TryGetProperty("item", out _) ? ResolveGuid(world, args, "item") : new PrefabGUID(0);
        var ev = SendFieldPoked<MoveAllItemsBetweenInventoriesEventV2>(em, MoveAllItemsBetweenInventoriesEventV2Id, raw =>
        {
            *(int*)(raw + 0x0) = f.i0; *(int*)(raw + 0x4) = f.i1; *(int*)(raw + 0x8) = f.i2;
            *(int*)(raw + 0xC) = t.i0; *(int*)(raw + 0x10) = t.i1; *(int*)(raw + 0x14) = t.i2;
            *(int*)(raw + 0x18) = item.GuidHash;
        }, (nameof(MoveAllItemsBetweenInventoriesEventV2.FromInventory), 0x0),
           (nameof(MoveAllItemsBetweenInventoriesEventV2.ToInventory), 0xC),
           (nameof(MoveAllItemsBetweenInventoriesEventV2.FilteredItem), 0x18));
        Log($"moveall {item.GuidHash} {Format(from)} -> {Format(to)}", ev);
        return new { kind = "moveall", @event = Format(ev), eventId = MoveAllItemsBetweenInventoriesEventV2Id, from = Format(from), to = Format(to), item = item.GuidHash, itemName = NameOf(world, item) };
    }

    /// <summary>
    /// <c>spell</c>: <c>ActivateVBloodAbilityEvent { PrefabGUID AbilityGUID @0x0; bool PrimarySlot @0x4 }</c>,
    /// the spell-book "put this spell on my bar" event (e.g. Blood Rite next
    /// to Shadow Bolt). Args <c>ability</c> (GUID or name), <c>primary</c> (true: first spell slot).
    /// </summary>
    private static unsafe object Spell(World world, EntityManager em, JsonElement args)
    {
        var ability = ResolveGuid(world, args, "ability");
        bool primary = args.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True;
        var ev = SendFieldPoked<ActivateVBloodAbilityEvent>(em, ActivateVBloodAbilityEventId, raw =>
        {
            *(int*)(raw + 0x0) = ability.GuidHash;
            raw[0x4] = primary ? (byte)1 : (byte)0;
        }, (nameof(ActivateVBloodAbilityEvent.AbilityGUID), 0x0), (nameof(ActivateVBloodAbilityEvent.PrimarySlot), 0x4));
        Log($"spell {ability.GuidHash} primary {primary}", ev);
        return new { kind = "spell", @event = Format(ev), eventId = ActivateVBloodAbilityEventId, ability = ability.GuidHash, abilityName = NameOf(world, ability), primary };
    }

    /// <summary>
    /// <c>shapeshift</c>: <c>ProjectM.Network.EnterShapeshiftEvent { PrefabGUID Shapeshift @0x0; bool
    /// ExitOnSameForm @0x4 }</c>, what the power wheel / Shapeshift hotkeys send:
    /// e.g. Blood Mend, <c>AB_Shapeshift_BloodMend_Group</c> -1996241419 (heals 5% max HP per second for 1
    /// blood; moving, damage or a cast ends it). Args <c>ability</c>
    /// (GUID or name), <c>exit</c> (true: leave that form).
    /// </summary>
    private static unsafe object Shapeshift(World world, EntityManager em, JsonElement args)
    {
        var ability = ResolveGuid(world, args, "ability");
        bool exit = args.TryGetProperty("exit", out var ex) && ex.ValueKind == JsonValueKind.True;
        var ev = SendFieldPoked<ProjectM.Network.EnterShapeshiftEvent>(em, EnterShapeshiftEventId, raw =>
        {
            *(int*)(raw + 0x0) = ability.GuidHash;
            raw[0x4] = exit ? (byte)1 : (byte)0;
        }, (nameof(ProjectM.Network.EnterShapeshiftEvent.Shapeshift), 0x0), (nameof(ProjectM.Network.EnterShapeshiftEvent.ExitOnSameForm), 0x4));
        Log($"shapeshift {ability.GuidHash} exit {exit}", ev);
        return new { kind = "shapeshift", @event = Format(ev), eventId = EnterShapeshiftEventId, ability = ability.GuidHash, abilityName = NameOf(world, ability), exit };
    }

    /// <summary>A client entity ("index:version", or "self" = the local character) and its raw 12-byte NetworkId.</summary>
    private static (int i0, int i1, int i2)? RawNetworkId(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        if (el.GetArrayLength() != 3) throw new ArgumentException($"args.{key} must be [i0, i1, i2]");
        return (el[0].GetInt32(), el[1].GetInt32(), el[2].GetInt32());
    }

    private static unsafe (Entity entity, (int i0, int i1, int i2) net) NetworkIdOf(EntityManager em, string text)
    {
        Entity e;
        if (text.Trim().Equals("self", StringComparison.OrdinalIgnoreCase))
        {
            var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
            if (locals.Count == 0) throw new InvalidOperationException("no local character");
            e = locals[0];
        }
        else e = ParseEntity(text);
        var netType = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
        if (!em.Exists(e) || !em.HasComponent(e, netType)) throw new InvalidOperationException($"{text} does not exist or has no NetworkId");
        if (Marshal.SizeOf<NetworkId>() != NetworkIdSize) throw new InvalidOperationException("NetworkId size differs from the dump (12); refusing the raw copy");
        int* src = (int*)em.GetComponentDataRawRO(e, netType.TypeIndex);
        if (src == null) throw new InvalidOperationException($"GetComponentDataRawRO(NetworkId) returned null for {text}");
        return (e, (src[0], src[1], src[2]));
    }

    /// <summary>
    /// For event structs whose ECS TypeSize reads 0 through this interop: create the event entity,
    /// then write fields one by one through GetComponentDataRawRW, never a guessed-length blob.
    /// Refuses unless every named field sits at the expected offset in the interop struct.
    /// </summary>
    private static unsafe Entity SendFieldPoked<T>(EntityManager em, int eventId, FieldPoke poke, params (string field, int offset)[] layout) where T : unmanaged
    {
        foreach (var (field, offset) in layout)
        {
            int actual = (int)System.Runtime.InteropServices.Marshal.OffsetOf<T>(field);
            if (actual != offset)
                throw new InvalidOperationException($"{typeof(T).Name}.{field} is at 0x{actual:X}, expected 0x{offset:X}; refusing the raw write");
        }
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite);
        var ev = em.CreateEntity(
            type,
            ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite));
        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRW(ev, type.TypeIndex);
            if (raw == null) throw new InvalidOperationException($"GetComponentDataRawRW returned null for {typeof(T).Name}");
            poke(raw);
            em.SetComponentData(ev, new NetworkEventType { EventId = eventId, IsAdminEvent = false, IsDebugEvent = false });
        }
        catch
        {
            em.DestroyEntity(ev);
            throw;
        }
        return ev;
    }

    private static Entity Send<T>(EntityManager em, T value, int eventId) where T : unmanaged
    {
        var ev = em.CreateEntity(
            ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite));
        try
        {
            ev.Write(em, value);
        }
        catch
        {
            em.DestroyEntity(ev);
            throw;
        }
        // Typed SetComponentData, as CharacterCreateOps does (proven live on the client). A failure
        // here would leave a SendNetworkEventTag entity with EventId 0 behind: destroy it.
        try { em.SetComponentData(ev, new NetworkEventType { EventId = eventId, IsAdminEvent = false, IsDebugEvent = false }); }
        catch
        {
            em.DestroyEntity(ev);
            throw;
        }
        return ev;
    }

    private static PrefabGUID ResolveGuid(World world, JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var el))
            throw new ArgumentException($"args.{key} is required (int PrefabGUID hash or prefab name)");
        if (el.ValueKind == JsonValueKind.Number)
            return new PrefabGUID(el.GetInt32());
        if (el.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"args.{key} must be an int hash or a string name");
        var text = (el.GetString() ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException($"args.{key} is empty");
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash))
            return new PrefabGUID(hash);
        if (PrefabLookup.TryGetGuidByName(world, text, out var byName) && byName.GuidHash != 0)
            return byName;
        try
        {
            var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabs != null
                && prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(text, out byName, true)
                && byName.GuidHash != 0)
                return byName;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"bot_event: TryGetPrefabGuidWithName('{text}') failed: {e.Message}");
        }
        throw new ArgumentException($"no prefab named '{text}' for args.{key}; pass the integer PrefabGUID hash");
    }

    private static string? NameOf(World world, PrefabGUID guid) =>
        PrefabLookup.TryGetName(world, guid, out var name) ? name : null;

    private static Entity ParseEntity(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            throw new ArgumentException($"'{text}' is not an entity id (index:version)");
        return new Entity { Index = index, Version = version };
    }

    private static string Format(Entity e) => $"{e.Index}:{e.Version}";

    private static void Log(string what, Entity ev) =>
        Plugin.Logger?.LogInfo($"bot_event: sent {what} event {Format(ev)}");

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static float Num(JsonElement a, string k)
    {
        if (!a.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Number)
            throw new ArgumentException($"args.{k} (number) is required");
        return v.GetSingle();
    }

    private static int IntOr(JsonElement a, string k, int d)
    {
        if (!a.TryGetProperty(k, out var v)) return d;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n))
            throw new ArgumentException($"args.{k} must be an integer");
        return n;
    }

    private static bool Bool(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
}
