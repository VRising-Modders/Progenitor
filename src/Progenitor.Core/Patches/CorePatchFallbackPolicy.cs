using System.Globalization;

namespace Progenitor.Patches;

/// <summary>Where one of Core's own fallback patches is allowed to go (job FHARM7).</summary>
public enum CoreFallbackRoute
{
    /// <summary>Register through the Bootstrap slot pool under the <c>&lt;guid&gt;.core</c> owner.</summary>
    Slot,

    /// <summary>
    /// Hand HarmonyX a <c>Progenitor.Core</c> method. Permitted only when
    /// <c>Progenitor.Bootstrap</c> is absent, which is a dev configuration (a Core dumped on its
    /// own, or a unit test) — there is no pool to route through and no hot swap to protect.
    /// </summary>
    Direct,

    /// <summary>
    /// Refuse. The site's shape does not fit a slot <em>and</em> Bootstrap is present, so a
    /// direct patch would pin a Core generation that is expected to hot-swap.
    /// </summary>
    Refused,
}

/// <summary>
/// Job FHARM7: the one rule for the direct <c>Harmony.Patch</c> calls that FHARM6 audited and
/// left in Core (<c>harmony-patches.md</c> section 8.7).
///
/// <para><b>The problem.</b> Each of those calls is a fallback taken when Bootstrap does not own
/// the site, and each hands HarmonyX a <c>MethodInfo</c> from the collectible Core ALC, which
/// pins that Core generation <b>for the life of the process, even after a clean unpatch</b>
/// (<c>StackTraceFixes.RealMethodMap</c>, and <c>Il2CppDetourMethodPatcher.DelegateCache</c> for
/// IL2CPP targets — measured, <c>harmony-patches.md</c> section 8.1). "Fallback" was doing the
/// work of a mitigation and is not one: a Bootstrap that is present but a version behind takes
/// exactly the same path, on every boot, and every <c>.corereload</c> after it leaks a
/// generation.</para>
///
/// <para><b>The rule.</b> A fallback whose body fits the pool's signature-agnostic shape
/// (<c>__instance</c> + <c>__args</c>, no <c>__result</c> and no <c>__state</c>) routes through
/// the pool under the Core owner id, exactly like the world-load postfix and god mode's kill
/// prefixes. A fallback whose body needs a shape the pool cannot carry keeps its direct patch —
/// but only when Bootstrap is <em>absent</em>, and it says so in one warning naming the pin and
/// the Core generation it pins. With Bootstrap present, a shape the pool cannot carry is
/// <see cref="CoreFallbackRoute.Refused"/>: the fix is a typed Bootstrap site plus a restart of
/// both, not a silent permanent pin.</para>
///
/// <para>Game-free, so <c>CorePatchFallbackTests</c> pins the decision and both log lines
/// without a V Rising install.</para>
/// </summary>
public static class CorePatchFallbackPolicy
{
    /// <summary>Refusal token for a site the pool cannot express while Bootstrap is present.</summary>
    public const string ReasonNeedsDirectShape = "needs-direct-shape";

    /// <summary>Module token for the event hooks' three fallbacks.</summary>
    public const string ModuleEvents = "events";

    /// <summary>Module token for the client-hosted launcher's <c>StunProcess.Start</c> fallback.</summary>
    public const string ModuleHosted = "hosted";

    /// <summary>Module token for the two <c>SystemBase.Update</c> fallbacks in <c>HarmonyFallback</c>.</summary>
    public const string ModuleSystemHooks = "systemhooks";

    /// <summary>
    /// Module token for <c>PrefabRegistry</c>'s <c>DeserializeLoadFile</c> fallback (job FHARM8).
    /// </summary>
    public const string ModuleContent = "content";

    /// <summary>Module token for <c>Localization</c>'s <c>SetLanguage</c> fallback (job FHARM8).</summary>
    public const string ModuleLocalization = "localization";

    /// <summary>Module token for <c>ModSave</c>'s two save-site fallbacks (job FHARM8).</summary>
    public const string ModulePersistence = "persistence";

    /// <summary>
    /// Route one Core fallback. <paramref name="slotShapeFits"/> is a property of the patch body,
    /// decided at the call site and stated in its comment: false when the body needs
    /// <c>__result</c> or carries <c>__state</c> from a prefix to a postfix/finalizer.
    /// <paramref name="bootstrapPresent"/> is <c>PatchRegistry.SlotsAvailable</c> in the game
    /// build — the pool resolving is exactly the question "is Bootstrap there".
    /// </summary>
    public static CoreFallbackRoute Decide(bool slotShapeFits, bool bootstrapPresent)
    {
        if (!bootstrapPresent) return CoreFallbackRoute.Direct;
        return slotShapeFits ? CoreFallbackRoute.Slot : CoreFallbackRoute.Refused;
    }

    /// <summary>
    /// The one-line warning a <see cref="CoreFallbackRoute.Direct"/> fallback logs. Names the pin
    /// and the Core generation it pins, so a dev who later wonders why <c>.corereload</c> stopped
    /// collecting can grep for it.
    /// <c>patches: WARNING direct fallback events ProjectM.ChatMessageSystem::OnUpdate ...</c>
    /// </summary>
    public static string FormatDirect(string module, string site, int coreGeneration) =>
        "patches: WARNING direct fallback " + Safe(module) + " " + Safe(site)
        + ": Progenitor.Bootstrap is absent, so HarmonyX is handed a Progenitor.Core method and "
        + "pins Core generation " + coreGeneration.ToString(CultureInfo.InvariantCulture)
        + " for the life of the process even after unpatch (harmony-patches.md section 8); "
        + "a dev configuration only — .corereload will leak this generation.";

    /// <summary>
    /// The line a <see cref="CoreFallbackRoute.Refused"/> fallback logs. The site is left
    /// unpatched on purpose.
    /// </summary>
    public static string FormatRefused(string module, string site) =>
        "patches: refused direct fallback " + Safe(module) + " " + Safe(site)
        + " reason=" + ReasonNeedsDirectShape
        + ": the slot pool cannot carry this site's shape (__result or a prefix-to-finalizer "
        + "__state) and Progenitor.Bootstrap IS present, so a direct patch would pin this Core "
        + "generation for the life of the process (harmony-patches.md section 8). The site is "
        + "left unpatched; update Bootstrap so it owns the site, then restart both.";

    private static string Safe(string? s) => string.IsNullOrEmpty(s) ? "?" : s!;
}
