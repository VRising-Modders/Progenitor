using System;

namespace Progenitor.Models;

/// <summary>
/// Which components <see cref="ModelHandle"/> switches off on a copy so the game's own drivers
/// leave it alone. Names are IL2CPP class names as seen on the live hybrid prefabs
/// (2026-09-28: <c>HYB_VampireMale_Prefab</c>, <c>HYB_NPCDracula_Evolved</c>): the
/// <c>Animator</c>; FinalIK solvers (<c>LimbIK</c>, <c>AimIK</c>, <c>CCDIK</c>); every
/// <c>Hybrid*</c> model component (bones, animation, locomotion, transform, renderer binding,
/// equipment); Magica cloth; footstep and secondary-animation receivers; and
/// <c>*Authoring</c> components such as <c>SkinnedMeshRenderAuthoring</c> on the player models,
/// which, left on, cleared the materials of dressed equipment and reset posed bones on the next
/// frame (live 2026-09-28).
/// </summary>
internal static class ModelInertPolicy
{
    private static readonly string[] Prefixes = { "Hybrid", "Magica" };
    private static readonly string[] Contains = { "Footstep", "SecondaryAnimation", "ProxyPrefab" };

    public static bool IsInert(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        if (string.Equals(typeName, "Animator", StringComparison.Ordinal)) return true;
        if (typeName.EndsWith("IK", StringComparison.Ordinal)) return true;
        if (typeName.EndsWith("Authoring", StringComparison.Ordinal)) return true;
        foreach (var p in Prefixes) if (typeName.StartsWith(p, StringComparison.Ordinal)) return true;
        foreach (var c in Contains) if (typeName.IndexOf(c, StringComparison.Ordinal) >= 0) return true;
        return false;
    }
}
