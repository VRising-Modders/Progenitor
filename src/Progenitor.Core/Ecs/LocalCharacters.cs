#if !PROGENITOR_TESTS
using ProjectM;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Ecs;

/// <summary>The client's own character, for client code in any module (dye, sort events, the container preview).</summary>
internal static class LocalCharacters
{
    /// <summary>The local character (this client's own), or false.</summary>
    internal static bool TryGet(EntityManager em, out Entity character)
    {
        character = Entity.Null;
        var locals = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<LocalCharacter>(ComponentType.AccessMode.ReadOnly));
        if (locals.Count == 0) return false;
        character = locals[0];
        return em.Exists(character);
    }
}
#endif
