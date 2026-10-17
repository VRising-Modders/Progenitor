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
    internal static bool Set(float x, float z, out string why)
    {
        why = "";
        if (!TryClientWorld(out var em, out why)) return false;
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
        if (!TryClientWorld(out var em, out why)) return false;
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
            return true;
        }
        catch
        {
            em.DestroyEntity(ev);
            throw;
        }
    }

    /// <summary>A client world with this player's character in it (the server reads the sender from the connection).</summary>
    private static bool TryClientWorld(out EntityManager em, out string why)
    {
        em = default;
        why = "";
        var world = Worlds.Client;
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
