using System;

namespace Progenitor.Plugins;

/// <summary>
/// Reads <c>Progenitor.Bootstrap.CoreLoader.IsReloading</c> without a compile reference to
/// Bootstrap (Core must load with Bootstrap absent; see <c>BootstrapLink</c> for the same seam
/// on the process-lifetime services).
///
/// <b>Fallback only since job FBUFF4.</b> Bootstrap's <c>ReloaderHost</c> now publishes the
/// reason itself through <c>Progenitor.Bootstrap.PluginUnloadSlot</c>
/// (<see cref="BootstrapUnloadSlotProbe"/>), so this probe answers only for an unload neither
/// reloader drove — BepInEx tearing a plugin down on its own, or a caller of
/// <c>BasePlugin.Unload()</c> outside the reload loop. Before FBUFF4 it was the <em>only</em>
/// source on the production path, and R30 finding 4 showed what that cost: a latched failed
/// lookup answered "no swap in flight", which the policy turned into a Removal, which swept
/// every player's buff with no warning anywhere.
///
/// Three-valued now: <see cref="CoreSwapProbeResult.Unknown"/> is not
/// <see cref="CoreSwapProbeResult.Idle"/>, and Unknown keeps state.
/// </summary>
internal static class CoreSwapProbe
{
    internal const string Member = "Progenitor.Bootstrap.CoreLoader.IsReloading";

    private static readonly BootstrapMemberLookup Lookup =
        new("Progenitor.Bootstrap.CoreLoader", "IsReloading", typeof(bool), Member);

    /// <summary>Tests only: point the lookup at a type in this assembly.</summary>
    internal static Func<Type?>? TypeOverrideForTests
    {
        set => Lookup.TypeOverrideForTests = value;
    }

    /// <summary>Tests / a Bootstrap that loaded after the first probe: look the property up again.</summary>
    internal static void ResetForTests() => Lookup.ResetForTests();

    /// <summary>
    /// <see cref="CoreSwapProbeResult.InFlight"/> while Bootstrap has a Core generation swap
    /// scheduled or running, <see cref="CoreSwapProbeResult.Idle"/> when Bootstrap says there is
    /// none, and <see cref="CoreSwapProbeResult.Unknown"/> when the member could not be read —
    /// which is warned about once (naming the member) rather than silently answering Idle.
    ///
    /// Bootstrap absent is <see cref="CoreSwapProbeResult.Idle"/>, not Unknown: with no
    /// Bootstrap there is no Core generation swap, and Core's own <c>PluginReloader</c> signals
    /// every unload it drives.
    /// </summary>
    internal static CoreSwapProbeResult Probe(Action<string>? warn)
    {
        if (!Lookup.TryRead(warn, out var value))
            return Lookup.SawBootstrap ? CoreSwapProbeResult.Unknown : CoreSwapProbeResult.Idle;
        return value is true ? CoreSwapProbeResult.InFlight : CoreSwapProbeResult.Idle;
    }
}
