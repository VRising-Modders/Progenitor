using System;
using Progenitor.Patches;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FHARM7. The routing decision for the direct <c>Harmony.Patch</c> calls FHARM6 audited and
/// left in Core, one case per site, plus the two log lines the decision writes.
///
/// <para>The sites themselves (<c>Events/EventHooks.cs</c>, <c>Hosted/HostedServerLauncher.cs</c>,
/// <c>Reload/HarmonyFallback.cs</c>) all reference <c>ProjectM</c> and cannot be linked into a
/// game-free test project, so what is pinned here is the rule they each call with one boolean —
/// <c>slotShapeFits</c> — that their own doc comment argues for. The mapping from site to that
/// boolean is asserted here by name so a future site cannot be added without picking a side.</para>
/// </summary>
public class CorePatchFallbackTests
{
    // The per-site table. `slotShapeFits` is false exactly when the body needs something the
    // pool's (__instance, __args) shape cannot carry: __result, or a __state carried from a
    // prefix to a postfix/finalizer.
    public static TheoryData<string, string, bool> Sites => new()
    {
        // Events: three prefixes/postfixes that read __instance and one bound argument. No
        // __result, no __state -> slot-routed under <guid>.core.
        { CorePatchFallbackPolicy.ModuleEvents, "ChatMessageSystem.OnUpdate", true },
        { CorePatchFallbackPolicy.ModuleEvents, "ServerBootstrapSystem.OnUserConnected", true },
        { CorePatchFallbackPolicy.ModuleEvents, "ServerBootstrapSystem.OnUserDisconnected", true },
        // Hosted: prefix + finalizer joined by __state (the Doorstop-strip nest Enter bit).
        { CorePatchFallbackPolicy.ModuleHosted, "StunProcess.Start", false },
        // SystemHooks: prefix + postfix joined by __state (the call frame push/pop).
        { CorePatchFallbackPolicy.ModuleSystemHooks, "SystemBase.Update", false },
        { CorePatchFallbackPolicy.ModuleSystemHooks, "WorldUnmanagedImpl.UnmanagedUpdate", false },
        // Job FHARM8, the three fallbacks FHARM7 left unrouted.
        // Content: a no-argument prefix and a no-argument diagnostic postfix on the same target.
        // Nothing crosses between them (the prefix never skips the original and keeps no state),
        // so each is an independent slot.
        { CorePatchFallbackPolicy.ModuleContent, "LoadPersistenceSystemV2.DeserializeLoadFile", true },
        // Localization: a postfix reading one by-value argument, bound out of __args by the
        // original's own parameter name.
        { CorePatchFallbackPolicy.ModuleLocalization, "GameLocalization.SetLanguage", true },
        // Persistence: BeginCreateServerWorld is one by-value argument -> slot. TriggerSave's
        // entire body is a test of __result, which a signature-agnostic slot body never sees,
        // so with Bootstrap present it is refused and mod stores are written on world unload and
        // shutdown rather than on each autosave.
        { CorePatchFallbackPolicy.ModulePersistence, "ServerWorldManager.BeginCreateServerWorld", true },
        { CorePatchFallbackPolicy.ModulePersistence, "TriggerPersistenceSaveSystem.TriggerSave", false },
    };

    [Theory]
    [MemberData(nameof(Sites))]
    public void With_Bootstrap_present_a_slot_shaped_site_routes_through_the_pool_and_the_others_are_refused(
        string module, string site, bool slotShapeFits)
    {
        _ = module;
        _ = site;
        var route = CorePatchFallbackPolicy.Decide(slotShapeFits, bootstrapPresent: true);
        Assert.Equal(
            slotShapeFits ? CoreFallbackRoute.Slot : CoreFallbackRoute.Refused,
            route);
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void With_Bootstrap_absent_every_site_falls_back_to_a_direct_patch(
        string module, string site, bool slotShapeFits)
    {
        _ = module;
        _ = site;
        Assert.Equal(
            CoreFallbackRoute.Direct,
            CorePatchFallbackPolicy.Decide(slotShapeFits, bootstrapPresent: false));
    }

    [Fact]
    public void The_direct_line_names_the_pin_and_the_generation_it_pins()
    {
        string line = CorePatchFallbackPolicy.FormatDirect(
            CorePatchFallbackPolicy.ModuleEvents, "ChatMessageSystem.OnUpdate", coreGeneration: 7);

        Assert.Contains("patches: WARNING direct fallback events ChatMessageSystem.OnUpdate", line, StringComparison.Ordinal);
        Assert.Contains("Progenitor.Bootstrap is absent", line, StringComparison.Ordinal);
        Assert.Contains("pins Core generation 7", line, StringComparison.Ordinal);
        Assert.Contains("harmony-patches.md section 8", line, StringComparison.Ordinal);
        Assert.Single(line.Split('\n'));
    }

    [Fact]
    public void The_refusal_line_names_the_reason_and_asks_for_a_restart_of_both()
    {
        string line = CorePatchFallbackPolicy.FormatRefused(
            CorePatchFallbackPolicy.ModuleHosted, "StunProcess.Start");

        Assert.Contains("reason=" + CorePatchFallbackPolicy.ReasonNeedsDirectShape, line, StringComparison.Ordinal);
        Assert.Contains("__state", line, StringComparison.Ordinal);
        Assert.Contains("restart both", line, StringComparison.Ordinal);
        Assert.Single(line.Split('\n'));
    }

    /// <summary>
    /// Job FHARM8. Every site in the table that routes to a slot must be paid for out of the
    /// reserve, so the table above and <see cref="CorePatchBudget"/> have to agree about which
    /// modules take pool slots at all. A site added to one and not the other is the failure this
    /// catches — the budget being wrong is how a live server refuses a lazily-armed Core guard
    /// hours into a session.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sites))]
    public void Every_slot_shaped_fallback_module_is_paid_for_in_the_core_patch_budget(
        string module, string site, bool slotShapeFits)
    {
        _ = site;
        if (!slotShapeFits) return;
        // Hosted and SystemHooks never reach here (both are false for every site).
        Assert.True(
            CorePatchBudget.CountFor(module) > 0,
            "module '" + module + "' routes a fallback through the pool but has no entry in "
            + "CorePatchBudget, so PatchSlots.CoreReserve does not cover it.");
    }

    [Fact]
    public void A_site_the_pool_cannot_carry_is_refused_rather_than_patched_directly_when_Bootstrap_is_there()
    {
        // TriggerSave is the FHARM8 addition to this class of site: its body is "if (!__result)
        // return;", and a slot body is never handed __result.
        Assert.Equal(
            CoreFallbackRoute.Refused,
            CorePatchFallbackPolicy.Decide(slotShapeFits: false, bootstrapPresent: true));
        Assert.Equal(
            CoreFallbackRoute.Direct,
            CorePatchFallbackPolicy.Decide(slotShapeFits: false, bootstrapPresent: false));
    }

    [Fact]
    public void An_empty_module_or_site_still_produces_a_readable_line()
    {
        string line = CorePatchFallbackPolicy.FormatDirect("", "", 0);
        Assert.Contains("patches: WARNING direct fallback ? ?", line, StringComparison.Ordinal);
    }
}
