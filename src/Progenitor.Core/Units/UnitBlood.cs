using System;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Runtime half of <see cref="PluginUnits.SetBlood(Entity, string, float)"/>. Creates the entity a
/// client's admin console sends for "consume blood" (<c>NetworkEventType</c> +
/// <c>ReceiveNetworkEventTag</c> + <c>FromCharacter</c> + <c>ConsumeBloodAdminEvent</c>); the game's
/// <c>VariousMigratedDebugEventsSystem</c> applies it on its next update and destroys the entity.
/// Server only, main thread.
/// </summary>
internal static unsafe class UnitBlood
{
    internal static bool Set(World? world, Entity character, int bloodTypeGuid, float quality, out string why)
    {
        if (!UnitBloodPolicy.IsValidQuality(quality))
        {
            why = UnitBloodPolicy.QualityRefusal(quality);
            return false;
        }
        if (world == null || !world.IsCreated)
        {
            why = "the server world is not ready";
            return false;
        }
        // A GUID that is not a blood type would reach the game's handler as Blood.BloodType and be
        // saved with the character; only BloodType_ prefabs are passed on.
        if (!PrefabLookup.TryGetName(world, new PrefabGUID(bloodTypeGuid), out string typeName)
            || !typeName.StartsWith("BloodType_", StringComparison.Ordinal))
        {
            why = UnitBloodPolicy.UnknownTypeRefusal(bloodTypeGuid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return false;
        }
        var em = world.EntityManager;
        if (character == Entity.Null || !em.Exists(character)
            || !em.HasComponent<PlayerCharacter>(character)
            || !em.HasComponent(character, ComponentIndex.Of<Blood>(ComponentType.AccessMode.ReadOnly)))
        {
            why = UnitBloodPolicy.NoBloodRefusal(character.Index, character.Version);
            return false;
        }
        var user = em.GetComponentData<PlayerCharacter>(character).UserEntity;

        var ev = em.CreateEntity(
            ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<ReceiveNetworkEventTag>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<ConsumeBloodAdminEvent>(ComponentType.AccessMode.ReadWrite));
        em.SetComponentData(ev, new NetworkEventType { EventId = Layout.ConsumeBloodAdminEventId, IsAdminEvent = true });

        byte* from = (byte*)em.GetComponentDataRawRW(ev, ComponentIndex.IndexOf<FromCharacter>());
        byte* payload = (byte*)em.GetComponentDataRawRW(ev, ComponentIndex.IndexOf<ConsumeBloodAdminEvent>());
        if (from == null || payload == null)
        {
            em.DestroyEntity(ev);
            why = "the event entity's components could not be written";
            return false;
        }
        *(Entity*)(from + Layout.FromCharacterUserOffset) = user;
        *(Entity*)(from + Layout.FromCharacterCharacterOffset) = character;
        UnitBloodPolicy.WritePayload(new Span<byte>(payload, UnitBloodPolicy.PayloadSize), bloodTypeGuid, quality);
        why = "";
        return true;
    }

    internal static bool TryResolve(World? world, string bloodType, out int guid, out string why)
    {
        guid = 0;
        string name = UnitBloodPolicy.PrefabName(bloodType);
        if (world == null || !world.IsCreated)
        {
            why = "the server world is not ready";
            return false;
        }
        if (!PrefabLookup.TryGetGuidByName(world, name, out PrefabGUID found))
        {
            why = UnitBloodPolicy.UnknownTypeRefusal(name);
            return false;
        }
        guid = found.GuidHash;
        why = "";
        return true;
    }
}
