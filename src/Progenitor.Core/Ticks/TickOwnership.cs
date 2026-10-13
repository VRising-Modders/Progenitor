namespace Progenitor.Ticks;

/// <summary>
/// Process-lifetime tick ownership. Pure so the attach-vs-fallback matrix is testable
/// without IL2CPP. ClassInjector keys injected types by FullName and has no unregister,
/// so only one CLR type may occupy <c>Progenitor.Ticks.FrameTicker</c> per process:
/// Bootstrap registers it. Collectible Core never ClassInjectors; a fallback tick is
/// the Harmony OnUpdate postfix only.
/// </summary>
internal static class TickOwnership
{
    /// <summary>
    /// Logged when <c>TryAttach</c> fails after Bootstrap already injected the ticker
    /// FullName. Core must not <c>AddComponent</c> a second ticker or Harmony-patch
    /// <c>OnUpdate</c> on top of Bootstrap's postfix (that is a double tick).
    /// </summary>
    internal const string AttachFailedInertLog = "attach failed, Core stays inert for ticks";

    /// <summary>
    /// Core may <c>RegisterTypeInIl2Cpp</c> / <c>AddComponent</c> its own
    /// <c>FrameTicker</c> only when attach did not succeed and the FullName is still free.
    /// A registered FullName means Bootstrap got there first: attach to that ticker
    /// (or stay inert), never inject a second CLR type.
    /// </summary>
    internal static bool CoreMayInstallTicker(bool attachSucceeded, bool typeAlreadyRegistered)
    {
        if (attachSucceeded) return false;
        if (typeAlreadyRegistered) return false;
        return true;
    }

    /// <summary>
    /// <c>TryAttach</c> failed after Bootstrap already registered the FullName.
    /// Plugin.Install would otherwise Harmony-patch <c>OnUpdate</c> and
    /// <c>AddComponent</c> a second ticker; returning this true is the signal to
    /// skip that fallback and leave Core's tick subscribers unhooked.
    /// </summary>
    internal static bool StayInertForTicks(bool attachSucceeded, bool typeAlreadyRegistered) =>
        !attachSucceeded && typeAlreadyRegistered;

    /// <summary>
    /// Core may add its own BepInEx log listener only when Bootstrap's ring is not already
    /// forwarding (no backend) and is not already in <c>Logger.Listeners</c>. A second ring
    /// splits the sequence the debug bridge reads.
    /// </summary>
    internal static bool CoreMayInstallLogRing(bool backendAlreadySet, bool bootstrapRingListening) =>
        !backendAlreadySet && !bootstrapRingListening;

    /// <summary>
    /// True when Core would add a second tick source on top of Bootstrap (Harmony
    /// <c>OnUpdate</c> postfix and/or a second ClassInjector ticker). Production
    /// <c>Plugin.Install</c> must keep this false whenever Bootstrap is present.
    /// </summary>
    internal static bool CoreAddsSecondTickSource(bool bootstrapPresent, bool corePatchesOnUpdate, bool coreInjectsTicker) =>
        bootstrapPresent && (corePatchesOnUpdate || coreInjectsTicker);

    /// <summary>
    /// Log line Core's FrameTicker.Install emits. Never ClassInjects; the
    /// Bootstrap-absent tick is the Harmony OnUpdate postfix only.
    /// </summary>
    internal static string InstallDecision(bool attachSucceeded, bool typeAlreadyRegistered)
    {
        if (StayInertForTicks(attachSucceeded, typeAlreadyRegistered))
            return AttachFailedInertLog;
        if (!CoreMayInstallTicker(attachSucceeded, typeAlreadyRegistered))
            return "FrameTicker: refusing ClassInjector from collectible Core; Bootstrap owns the injected type.";
        return "FrameTicker: Bootstrap absent; Core will not ClassInjector a MonoBehaviour (that pins this ALC). Tick is the Harmony OnUpdate postfix only.";
    }
}
