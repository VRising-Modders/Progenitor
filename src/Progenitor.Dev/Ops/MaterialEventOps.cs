using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Dev.Bots;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Server half of <c>materials</c> <c>{"action":"send"}</c>: queue, for a connected player, the event the
/// player's client sends to repair an item at a workstation (<c>RepairItemEvent</c>), repair a worn item
/// (<c>RepairEquippedItemEvent</c>) or upgrade a castle heart (<c>CastleHeartInteractEvent</c> with
/// <c>EventType</c> Upgrade). Focus-free: an event entity with the payload and the player's
/// <c>FromCharacter</c>, the server-side recipe <c>bot_repair</c> and <c>bot_use</c> use; the game's own
/// system handles it next update with its normal validation (interaction with the station or the heart,
/// costs, permissions). A refused event is silent: observe the result (durability, heart level, the
/// <c>materials:</c> log lines).
///
/// <para>Layout: every field offset is checked against the interop wrapper (<c>Marshal.OffsetOf</c>)
/// before the first write and the send is refused on a mismatch; network ids are copied raw (12 bytes,
/// size-checked) from the entities' own <c>NetworkId</c>; the payload is poked field by field through
/// <c>GetComponentDataRawRW</c> (<see cref="MaterialEventArgs"/>), never a guessed-length blob. No
/// generic interop instantiation over a game value type: component types come from
/// <c>ComponentIndex</c>, reads and writes are raw by TypeIndex.</para>
/// </summary>
internal static class MaterialEventOps
{
    private static bool s_layoutChecked;

    public static unsafe object Send(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("materials send runs on the server (it queues the event the player's client would send)");
        var request = MaterialEventArgs.Parse(args);
        CheckLayout();
        var em = world.EntityManager;
        var character = BotCast.ResolveCharacter(em, args, out string name);
        var pc = em.GetComponentData<PlayerCharacter>(character); // existing typed accessor (bot_repair)
        var from = new FromCharacter { User = pc.UserEntity, Character = character };

        switch (request.Kind)
        {
            case MaterialEventArgs.Kind.RepairItem:
            {
                var station = Resolve(em, character, request.Station!, "station");
                var inventory = Resolve(em, character, request.Inventory, "inventory");
                var ws = NetworkIdBytes(em, station, "station");
                var inv = NetworkIdBytes(em, inventory, "inventory");
                var ev = Queue<RepairItemEvent>(em, from, MaterialEventArgs.RepairItemBytes,
                    raw => MaterialEventArgs.WriteRepairItem(raw, ws, inv, request.Slot));
                Log("repair_item station " + Format(station) + " inventory " + Format(inventory) + " slot " + request.Slot, name, ev);
                return new
                {
                    action = "send", @event = "repair_item", user = name, character = Format(character), entity = Format(ev),
                    station = Format(station), inventory = Format(inventory), slot = request.Slot,
                    note = "queued; the server needs the character interacting with the station (its window open). Observe durability and the materials: check Repair line",
                };
            }
            case MaterialEventArgs.Kind.RepairEquipped:
            {
                var ev = Queue<RepairEquippedItemEvent>(em, from, 4,
                    raw => MaterialEventArgs.WriteRepairEquipped(raw, request.EquipmentType));
                Log("repair_equipped type " + request.EquipmentType, name, ev);
                return new
                {
                    action = "send", @event = "repair_equipped", user = name, character = Format(character), entity = Format(ev),
                    equipmentType = request.EquipmentType,
                    note = "queued; observe durability and the materials: check Repair line",
                };
            }
            default:
            {
                var heart = Resolve(em, character, request.Heart, "heart");
                var castleHeart = ComponentIndex.Of<ProjectM.CastleBuilding.CastleHeart>(ComponentType.AccessMode.ReadOnly);
                if (!em.HasComponent(heart, castleHeart)) throw new InvalidOperationException("heart " + Format(heart) + " is not a castle heart");
                var net = NetworkIdBytes(em, heart, "heart");
                var ev = Queue<CastleHeartInteractEvent>(em, from, MaterialEventArgs.HeartEventBytes,
                    raw => MaterialEventArgs.WriteHeartUpgrade(raw, net));
                Log("heart_upgrade heart " + Format(heart), name, ev);
                return new
                {
                    action = "send", @event = "heart_upgrade", user = name, character = Format(character), entity = Format(ev),
                    heart = Format(heart),
                    note = "queued; the server needs the character interacting with the heart (its window open). Observe the heart level and the materials: check Upgrade line",
                };
            }
        }
    }

    private delegate void Poke(Span<byte> raw);

    /// <summary>Create the event entity with <c>FromCharacter</c>, poke the payload, and set the sender.</summary>
    private static unsafe Entity Queue<T>(EntityManager em, FromCharacter from, int bytes, Poke poke) where T : unmanaged
    {
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadWrite);
        int ecsSize = 0;
        try { ecsSize = TypeManager.GetTypeInfo(type.TypeIndex).TypeSize; } catch { /* 0: field pokes only */ }
        if (ecsSize > 0 && ecsSize < bytes)
            throw new InvalidOperationException(typeof(T).Name + " TypeSize " + ecsSize + " is smaller than the " + bytes + " bytes it carries; refusing");
        if (Marshal.SizeOf<T>() < bytes)
            throw new InvalidOperationException(typeof(T).Name + " interop size " + Marshal.SizeOf<T>() + " is smaller than " + bytes + "; refusing");
        var ev = em.CreateEntity(type, ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadWrite));
        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRW(ev, type.TypeIndex);
            if (raw == null) throw new InvalidOperationException("GetComponentDataRawRW returned null for " + typeof(T).Name);
            poke(new Span<byte>(raw, bytes));
            em.SetComponentData(ev, from);
        }
        catch
        {
            em.DestroyEntity(ev);
            throw;
        }
        return ev;
    }

    private static Entity Resolve(EntityManager em, Entity character, string text, string name)
    {
        Entity e;
        if (text == "self") e = character;
        else if (text == "target")
        {
            if (!character.TryRead(em, out Interactor interactor) || interactor.Target == Entity.Null)
                throw new InvalidOperationException("args." + name + " is 'target' but the character has no interaction target (open the window first)");
            e = interactor.Target;
        }
        else
        {
            var parts = text.Split(':');
            e = new Entity { Index = int.Parse(parts[0]), Version = int.Parse(parts[1]) };
        }
        if (!em.Exists(e)) throw new InvalidOperationException("args." + name + " " + Format(e) + " does not exist");
        return e;
    }

    private static unsafe byte[] NetworkIdBytes(EntityManager em, Entity e, string name)
    {
        var netType = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(e, netType)) throw new InvalidOperationException(name + " " + Format(e) + " has no NetworkId");
        if (Marshal.SizeOf<NetworkId>() != MaterialEventArgs.NetworkIdSize)
            throw new InvalidOperationException("NetworkId size differs from the dump (12); refusing the raw copy");
        byte* src = (byte*)em.GetComponentDataRawRO(e, netType.TypeIndex);
        if (src == null) throw new InvalidOperationException("GetComponentDataRawRO(NetworkId) returned null for " + name);
        return new ReadOnlySpan<byte>(src, MaterialEventArgs.NetworkIdSize).ToArray();
    }

    private static void CheckLayout()
    {
        if (s_layoutChecked) return;
        void Expect<T>(string field, int offset)
        {
            int actual = (int)Marshal.OffsetOf<T>(field);
            if (actual != offset) throw new InvalidOperationException($"{typeof(T).Name}.{field} is at 0x{actual:X}, expected 0x{offset:X}; refusing raw writes");
        }
        Expect<RepairItemEvent>(nameof(RepairItemEvent.Workstation), MaterialEventArgs.RepairWorkstationOffset);
        Expect<RepairItemEvent>(nameof(RepairItemEvent.Inventory), MaterialEventArgs.RepairInventoryOffset);
        Expect<RepairItemEvent>(nameof(RepairItemEvent.Slot), MaterialEventArgs.RepairSlotOffset);
        Expect<RepairEquippedItemEvent>(nameof(RepairEquippedItemEvent.EquipmentType), MaterialEventArgs.EquipmentTypeOffset);
        Expect<CastleHeartInteractEvent>(nameof(CastleHeartInteractEvent.CastleHeart), MaterialEventArgs.HeartOffset);
        Expect<CastleHeartInteractEvent>(nameof(CastleHeartInteractEvent.EventType), MaterialEventArgs.HeartEventTypeOffset);
        if ((byte)CastleHeartInteractEventType.Upgrade != MaterialEventArgs.HeartUpgrade)
            throw new InvalidOperationException("CastleHeartInteractEventType.Upgrade is not 0; refusing");
        s_layoutChecked = true;
    }

    private static string Format(Entity e) => e.Index + ":" + e.Version;

    private static void Log(string what, string user, Entity ev) =>
        Plugin.Logger?.LogInfo("materials send: queued " + what + " for " + user + " as event " + Format(ev));
}
