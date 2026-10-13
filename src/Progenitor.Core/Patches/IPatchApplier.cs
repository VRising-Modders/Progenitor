using System.Reflection;

namespace Progenitor.Patches;

/// <summary>
/// The one seam between <see cref="PatchRegistry"/> (game-free, unit-tested) and HarmonyX
/// (<see cref="HarmonyPatchApplier"/>, which the test assembly must not compile — see the
/// <c>ForbidGameReferences</c> target in <c>Progenitor.Tests.csproj</c>).
/// </summary>
public interface IPatchApplier
{
    /// <summary>
    /// Apply one patch. Returns an opaque token the registry hands back to
    /// <see cref="Remove"/>. <b>Throws</b> when the patch cannot be applied: the registry
    /// catches, logs, and does not register the entry — fail closed <em>at the registry level</em>
    /// (no entry, no handle, nothing counted). An implementation may still have left state behind
    /// in whatever it delegates to; the registry follows a throwing apply with a best-effort
    /// <see cref="Remove"/> for that reason.
    /// </summary>
    object Apply(MethodBase target, MethodInfo patch, PatchKind kind, int priority, string ownerId);

    /// <summary>
    /// Undo one <see cref="Apply"/>. Must be safe to call for a token already removed, and for an
    /// <see cref="Apply"/> that <b>threw</b> — the registry calls this after a failed apply,
    /// passing the patch method as the token, to clean up any partial state.
    /// </summary>
    void Remove(object token, MethodBase target, MethodInfo patch, PatchKind kind, string ownerId);
}
