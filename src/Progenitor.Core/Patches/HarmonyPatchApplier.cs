using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Progenitor.Patches;

/// <summary>
/// The HarmonyX half of <see cref="PatchRegistry"/>. Deliberately the only file in
/// <c>Patches/</c> that names a HarmonyLib type, so everything else compiles into the game-free
/// test assembly (<c>Progenitor.Tests.csproj</c> fails the build if HarmonyLib lands on its
/// reference list).
///
/// <para>One <see cref="Harmony"/> instance per owner, id <c>{ownerId}.patches</c>. The suffix
/// matters: <c>PluginReloader.SweepRegistrations</c> already calls
/// <c>HarmonyFallback.UnpatchOwnerAndSuffixes(guid)</c>, which unpatches every id that starts
/// with the plugin GUID, so a plugin whose registry entries somehow escaped
/// <see cref="PatchRegistry.RemoveAll(object)"/> is still caught by the existing net rather than
/// pinning its ALC. Core's own entries use Core's GUID with the same suffix and are covered by
/// <see cref="PatchRegistry.Uninstall"/>.</para>
///
/// <para><see cref="Apply"/> throws on failure by contract: the registry catches, logs
/// <c>patches: apply failed …(not registered)</c>, and keeps no entry. That is the registry's
/// state, not HarmonyX's. <c>Harmony.Patch</c> records the patch against the original's
/// <c>PatchInfo</c> in the process-lifetime <c>0Harmony</c> assembly and then compiles and
/// installs the replacement, so a throw part-way through can leave a remnant the registry has no
/// token for. <see cref="Remove"/> is therefore required to be safe for an apply that threw:
/// <c>Unpatch(original, patchMethod)</c> is addressed by the two things the registry still holds
/// and is a no-op when HarmonyX has nothing recorded, so the registry calls it best-effort after
/// every failed apply. If <em>that</em> throws too, the ALC may be pinned by a patch nothing can
/// find; <c>harmony-patches.md</c> §5.4's <c>pin-diag Harmony core-id patches=0</c> after a
/// deliberately-failing apply is the owed live check.</para>
/// </summary>
internal sealed class HarmonyPatchApplier : IPatchApplier
{
    /// <summary>Suffix appended to an owner id to form the Harmony instance id.</summary>
    internal const string IdSuffix = ".patches";

    private readonly object _gate = new();
    private readonly Dictionary<string, Harmony> _byOwner = new(StringComparer.Ordinal);

    public object Apply(MethodBase target, MethodInfo patch, PatchKind kind, int priority, string ownerId)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (patch == null) throw new ArgumentNullException(nameof(patch));

        var harmony = Ensure(ownerId);
        var method = new HarmonyMethod(patch) { priority = priority };

        // Harmony.Patch returns the replacement MethodInfo; the token we hand back is the
        // patch method itself, because Unpatch is addressed by (original, patchMethod).
        switch (kind)
        {
            case PatchKind.Prefix:
                harmony.Patch(target, prefix: method);
                break;
            case PatchKind.Postfix:
                harmony.Patch(target, postfix: method);
                break;
            case PatchKind.Finalizer:
                harmony.Patch(target, finalizer: method);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown patch kind.");
        }

        return patch;
    }

    public void Remove(object token, MethodBase target, MethodInfo patch, PatchKind kind, string ownerId)
    {
        var method = token as MethodInfo ?? patch;
        if (target == null || method == null) return;

        Harmony? harmony;
        lock (_gate) _byOwner.TryGetValue(ownerId ?? "", out harmony);
        // Unpatch is id-agnostic when addressed by patch method, so a missing instance is not
        // a reason to leave the patch applied.
        (harmony ?? Ensure(ownerId)).Unpatch(target, method);
    }

    /// <summary>
    /// Drop the cached Harmony instances. Called from <c>Patches.Uninstall()</c> after
    /// <see cref="PatchRegistry.Uninstall"/> has unpatched everything, so this generation holds
    /// no <see cref="Harmony"/> keyed by a collectible owner id.
    /// </summary>
    internal void Forget()
    {
        lock (_gate) _byOwner.Clear();
    }

    private Harmony Ensure(string? ownerId)
    {
        string id = (string.IsNullOrEmpty(ownerId) ? MyPluginInfo.PLUGIN_GUID : ownerId!) + IdSuffix;
        lock (_gate)
        {
            if (_byOwner.TryGetValue(ownerId ?? "", out var existing)) return existing;
            var harmony = new Harmony(id);
            _byOwner[ownerId ?? ""] = harmony;
            return harmony;
        }
    }
}
