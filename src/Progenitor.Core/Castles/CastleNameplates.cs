using System;
using ProjectM;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Castles;

/// <summary>
/// Server read and write of a placed model's nameplate (<c>NameableInteractable.Name</c>, the
/// name a player gives a chest or station). The component is networked, so a write reaches
/// every client with the next snapshot. Raw access at the metadata offsets
/// (<see cref="Layout.NameableInteractableNameOffset"/>) through the component overlay; no
/// generic interop call.
/// </summary>
internal static unsafe class CastleNameplates
{
    internal static bool TryRead(EntityManager em, Entity entity, out bool nameable, out string name)
    {
        nameable = false;
        name = "";
        try
        {
            byte* p = Ai.AiRuntime.RO<NameableInteractable>(em, entity, Layout.NameableInteractableNameOffset + NameplateCodec.Size);
            if (p == null) return false;
            nameable = true;
            name = NameplateCodec.Decode(new ReadOnlySpan<byte>(p + Layout.NameableInteractableNameOffset, NameplateCodec.Size));
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogDebug("Castles: nameplate read on " + entity.Index + ":" + entity.Version + " threw: " + e.Message);
            return false;
        }
    }

    internal static bool TryWrite(EntityManager em, Entity entity, string name, out string reason)
    {
        reason = NameplateCodec.Validate(name) ?? "";
        if (reason.Length > 0) return false;
        try
        {
            if (!entity.Exists(em)) { reason = "the entity no longer exists"; return false; }
            if (!entity.Has<NameableInteractable>(em)) { reason = "the entity has no nameplate (NameableInteractable)"; return false; }
            byte* p = Ai.AiRuntime.RW<NameableInteractable>(em, entity, Layout.NameableInteractableNameOffset + NameplateCodec.Size);
            var span = new Span<byte>(p + Layout.NameableInteractableNameOffset, NameplateCodec.Size);
            return NameplateCodec.TryEncode(name, span, out reason);
        }
        catch (Exception e)
        {
            reason = "nameplate write threw: " + e.Message;
            return false;
        }
    }
}
