namespace Progenitor.Ticks;

/// <summary>
/// Collectible Core must not inject a MonoBehaviour. <c>ClassInjector.RegisterTypeInIl2Cpp</c>
/// has no unregister and keys by FullName; Bootstrap already registered
/// <c>Progenitor.Ticks.FrameTicker</c> once per process. Core ticks through Bootstrap's
/// <c>CoreTick</c> slot. This type is a named no-op so a Core dumped without Bootstrap
/// still does not call ClassInjector (that would pin this ALC until process exit).
/// </summary>
internal static class FrameTicker
{
    internal static void Install(object? owner)
    {
        _ = owner;
        // OwnsLifetime is attach-succeeded (or inert-attach after Bootstrap already
        // took the tick). Consult TickOwnership so the matrix cannot drift from
        // StayInertForTicks / CoreMayInstallTicker (R14 finding 7). Never ClassInject:
        // that would pin this collectible ALC, and a second ticker on top of Bootstrap
        // is a double Drain.
        bool attachSucceeded = BootstrapLink.OwnsLifetime;
        bool typeAlreadyRegistered = attachSucceeded;
        string line = TickOwnership.InstallDecision(attachSucceeded, typeAlreadyRegistered);
        if (TickOwnership.StayInertForTicks(attachSucceeded, typeAlreadyRegistered)
            || !TickOwnership.CoreMayInstallTicker(attachSucceeded, typeAlreadyRegistered))
        {
            Plugin.Logger?.LogInfo(line);
            return;
        }

        Plugin.Logger?.LogWarning(line);
    }
}
