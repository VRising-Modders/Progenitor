#if !PROGENITOR_TESTS
using System;

namespace Progenitor.Materials;

/// <summary>
/// Process side of <c>ctx.MaterialSource</c>: owns the registry for this Core generation and, when
/// the opt-in Bootstrap seams are present, binds the availability and payment handlers
/// (<see cref="MaterialSeamRuntime"/>). Installed from <c>Plugin.Install</c>, cleared at Uninstall.
/// </summary>
internal static class MaterialRuntime
{
    internal static readonly MaterialSourceRegistry Registry = new();

    internal static void Install()
    {
        Registry.Log = line => Plugin.Logger?.LogInfo(line);
        Registry.HookState = MaterialSeamRuntime.HookState;
        MaterialSeamRuntime.Install(Registry);
    }

    internal static void Uninstall()
    {
        try { MaterialSeamRuntime.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("materials: seam uninstall threw: " + e.Message); }
        Registry.Clear();
        Registry.Log = null;
        Registry.HookState = () => (false, "Core uninstalled");
    }

    /// <summary>A provider registered or went away: every view is stale.</summary>
    internal static void OnRegistrationChanged() => MaterialSeamRuntime.OnRegistrationChanged();
}
#endif
