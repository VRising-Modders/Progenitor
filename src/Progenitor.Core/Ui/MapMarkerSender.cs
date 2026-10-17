#if !PROGENITOR_TESTS
using System;
using System.Runtime.InteropServices;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Ui;

/// <summary>
/// The player's own map marker (<see cref="WorldMap.SetMarker"/>, <see cref="WorldMap.ClearMarker"/>): the same
/// client-to-server events the world map sends. A click on the open map (<c>MapMenuMapper.OnMapClicked</c>, and the
/// Map Marker key in <c>MapMenuMapper.HandleInput</c>) sends <c>SetMapMarkerEvent { Position = (world x, world z) }</c>
/// through <c>NetworkEvents.SendEvent</c>; the server's <c>SetMapMarkerSystem</c> moves (or spawns) that user's one
/// custom map icon, which is what the map, the minimap and the compass guidance draw. <c>DeleteMapMarkerEvent</c> has no
/// fields and removes it.
///
/// <para>Built with the client-to-server recipe Dye all uses (an entity with the event, <c>NetworkEventType</c> and
/// <c>SendNetworkEventTag</c>; the event's field written raw at the offset the layout file pins, checked against the
/// interop struct first). No game method is called and no new generic interop instantiation is made.</para>
/// </summary>
internal static unsafe class MapMarkerSender
{
    // At most one marker event waits per frame: a second request in the same frame replaces the first, so a burst
    // of calls (a mod in a loop, repeated clicks) sends only the last.
    private static Entity _pending = Entity.Null;
    private static int _pendingFrame = -1;
    private static World? _pendingWorld;

    private static void DropPendingThisFrame(World world, EntityManager em)
    {
        int frame = UnityEngine.Time.frameCount;
        if (_pending != Entity.Null && _pendingFrame == frame && ReferenceEquals(_pendingWorld, world)
            && em.Exists(_pending)
            && em.HasComponent(_pending, ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadOnly)))
            em.DestroyEntity(_pending);
        _pending = Entity.Null;
    }

    private static void NotePending(World world, Entity ev)
    {
        _pending = ev;
        _pendingFrame = UnityEngine.Time.frameCount;
        _pendingWorld = world;
    }

    internal static bool Set(float x, float z, out string why)
    {
        why = "";
        if (!TryClientWorld(out var world, out var em, out why)) return false;
        DropPendingThisFrame(world, em);
        int actual = (int)Marshal.OffsetOf<SetMapMarkerEvent>(nameof(SetMapMarkerEvent.Position));
        if (actual != Layout.SetMapMarkerEventPositionOffset)
        {
            why = "SetMapMarkerEvent.Position is at 0x" + actual.ToString("X") + ", expected 0x"
                  + Layout.SetMapMarkerEventPositionOffset.ToString("X");
            return false;
        }

        var type = ComponentIndex.Of<SetMapMarkerEvent>(ComponentType.AccessMode.ReadWrite);
        var ev = em.CreateEntity(
            type,
            ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite));
        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRW(ev, type.TypeIndex);
            if (raw == null) throw new InvalidOperationException("GetComponentDataRawRW returned null for SetMapMarkerEvent");
            *(float*)(raw + Layout.SetMapMarkerEventPositionOffset) = x;
            *(float*)(raw + Layout.SetMapMarkerEventPositionOffset + 4) = z;
            // NetworkEventType carries bools, so it is not C# unmanaged: typed SetComponentData, as Dye all does.
            em.SetComponentData(ev, new NetworkEventType
            {
                EventId = Layout.SetMapMarkerEventId, IsAdminEvent = false, IsDebugEvent = false,
            });
            NotePending(world, ev);
            return true;
        }
        catch
        {
            em.DestroyEntity(ev); // never leave a send-tagged event with EventId 0 behind
            throw;
        }
    }

    internal static bool Clear(out string why)
    {
        why = "";
        if (!TryClientWorld(out var world, out var em, out why)) return false;
        DropPendingThisFrame(world, em);
        var ev = em.CreateEntity(
            ComponentIndex.Of<DeleteMapMarkerEvent>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<SendNetworkEventTag>(ComponentType.AccessMode.ReadWrite));
        try
        {
            em.SetComponentData(ev, new NetworkEventType
            {
                EventId = Layout.DeleteMapMarkerEventId, IsAdminEvent = false, IsDebugEvent = false,
            });
            NotePending(world, ev);
            return true;
        }
        catch
        {
            em.DestroyEntity(ev);
            throw;
        }
    }

    /// <summary>A client world with this player's character in it (the server reads the sender from the connection).</summary>
    private static bool TryClientWorld(out World world, out EntityManager em, out string why)
    {
        em = default;
        why = "";
        world = Worlds.Client!;
        if (world == null || !world.IsCreated)
        {
            why = "no client world";
            return false;
        }

        em = world.EntityManager;
        var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (locals.Count == 0)
        {
            why = "no local character";
            return false;
        }

        return true;
    }
}
#endif
