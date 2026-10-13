using System;
using System.Reflection;

namespace Progenitor.Patches;

/// <summary>
/// Module facade for the patch registry (job FHARM). Installed lazily: the first
/// <c>ctx.Patches.*</c> call (or <see cref="Progenitor.GodMode.GodMode"/>'s first guard) attaches
/// the Harmony applier and the site resolver, so a Core generation that nobody asks to patch
/// anything creates no <c>Harmony</c> instance at all. <see cref="Uninstall"/> is the reverse and
/// is called from <c>Plugin.Uninstall</c> before the collectible ALC unloads.
/// </summary>
public static class Patches
{
    private static readonly object Gate = new();
    private static HarmonyPatchApplier? _applier;

    /// <summary>True once the Harmony applier is attached.</summary>
    public static bool Installed
    {
        get { lock (Gate) return _applier != null; }
    }

    /// <summary>
    /// Attach the Harmony applier and the address-library-first site resolver. Idempotent.
    /// </summary>
    internal static void Install()
    {
        lock (Gate)
        {
            if (_applier != null) return;
            _applier = new HarmonyPatchApplier();
            PatchRegistry.Applier = _applier;
            PatchRegistry.SiteResolver = PatchSites.Describe;
        }
        Plugin.Logger?.LogInfo("patches: registry installed (address-library first, Harmony fallback).");
    }

    /// <summary>
    /// Unpatch <b>everything</b> this Core generation owns or applied on a plugin's behalf, drop
    /// the guarded-entity set, and detach. Must run before the Core ALC unloads: a Harmony patch
    /// whose patch method lives in a collectible assembly pins that assembly for as long as it
    /// is applied. Safe to call when nothing was ever installed.
    /// </summary>
    internal static void Uninstall()
    {
        try { Progenitor.GodMode.GodMode.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("patches: GodMode.Uninstall: " + e.Message); }

        try { PatchRegistry.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("patches: PatchRegistry.Uninstall: " + e.Message); }

        HarmonyPatchApplier? applier;
        lock (Gate)
        {
            applier = _applier;
            _applier = null;
        }
        try { applier?.Forget(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("patches: applier.Forget: " + e.Message); }
        try { PatchSites.Clear(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("patches: PatchSites.Clear: " + e.Message); }
    }

    /// <summary>Pin line for the Core boot log and <c>ping</c>.</summary>
    internal static string DescribePins() => PatchRegistry.DescribePins();
}

/// <summary>
/// <c>ctx.Patches</c>: per-plugin Harmony patching whose entries are tagged with the plugin id
/// and unpatched by <c>PluginReloader.SweepRegistrations</c> on every unload reason. Prefer this
/// over <c>ctx.UnsafeHarmony</c> — which opts the plugin out of hot reload entirely (job
/// FHARM7) — for every target, and especially a game (IL2CPP) one: the registry resolves the
/// site through the address library first, refuses the shared empty-body thunk and Burst jobs,
/// logs one line per patch and per unpatch, and fails closed if the apply throws.
/// </summary>
public sealed class PluginPatches
{
    private readonly object _owner;
    private readonly string _ownerId;

    /// <summary>Bind the registry to one plugin. <c>PluginContext</c> constructs this.</summary>
    public PluginPatches(object owner, string ownerId)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _ownerId = ownerId ?? "";
    }

    /// <summary>
    /// Patch <paramref name="target"/> with a prefix. Returning <c>false</c> skips the original.
    /// Returns null when the site was refused or the apply threw — check it if you need to
    /// know, ignore it if you do not: unload sweeps by owner either way.
    ///
    /// <para><b>Job FHARM5: the handler is not given to Harmony.</b> It is parked in one of
    /// <c>Progenitor.Bootstrap.PatchSlots</c>' 32 process-lifetime slots and Harmony is handed
    /// that slot's static method. A Harmony patch pins the assembly its patch method comes
    /// from <em>for the life of the process, even after <c>Unpatch</c></em> — measured, see
    /// <see cref="PatchSlotLink"/> — so a plugin body given straight to Harmony would make the
    /// plugin permanently un-hot-reloadable. The price is the shape below: you get
    /// <c>__instance</c> and <c>__args</c>, and there is no <c>__result</c>. When the pool is
    /// full the patch is refused with <c>reason=slots-exhausted</c>.</para>
    /// </summary>
    public PatchHandle? Prefix(MethodBase target, SlotPrefix prefix, int priority = 400)
    {
        Patches.Install();
        return PatchRegistry.PrefixSlot(_owner, _ownerId, target, prefix, priority);
    }

    /// <summary>Patch <paramref name="target"/> with a postfix. See <see cref="Prefix(MethodBase, SlotPrefix, int)"/>.</summary>
    public PatchHandle? Postfix(MethodBase target, SlotPostfix postfix, int priority = 400)
    {
        Patches.Install();
        return PatchRegistry.PostfixSlot(_owner, _ownerId, target, postfix, priority);
    }

    /// <summary>Patch <paramref name="target"/> with a finalizer. See <see cref="Prefix(MethodBase, SlotPrefix, int)"/>.</summary>
    public PatchHandle? Finalizer(MethodBase target, SlotFinalizer finalizer, int priority = 400)
    {
        Patches.Install();
        return PatchRegistry.FinalizerSlot(_owner, _ownerId, target, finalizer, priority);
    }

    /// <summary>
    /// Prefix from a <see cref="MethodInfo"/>. The method must be
    /// <c>static bool F(object instance, object[] args)</c>; anything else is refused with
    /// <c>reason=slot-signature</c>. See <see cref="Prefix(MethodBase, SlotPrefix, int)"/> for why
    /// an arbitrary Harmony patch method is no longer accepted here.
    /// </summary>
    public PatchHandle? Prefix(MethodBase target, MethodInfo prefix, int priority = 400)
    {
        Patches.Install();
        return PatchRegistry.Slot(_owner, _ownerId, target, prefix, PatchKind.Prefix, priority);
    }

    /// <summary>Postfix from a <see cref="MethodInfo"/> (<c>static void F(object, object[])</c>).</summary>
    public PatchHandle? Postfix(MethodBase target, MethodInfo postfix, int priority = 400)
    {
        Patches.Install();
        return PatchRegistry.Slot(_owner, _ownerId, target, postfix, PatchKind.Postfix, priority);
    }

    /// <summary>Finalizer from a <see cref="MethodInfo"/> (<c>static Exception F(object, object[], Exception)</c>).</summary>
    public PatchHandle? Finalizer(MethodBase target, MethodInfo finalizer, int priority = 400)
    {
        Patches.Install();
        return PatchRegistry.Slot(_owner, _ownerId, target, finalizer, PatchKind.Finalizer, priority);
    }

    /// <summary>
    /// Unpatch everything this plugin registered <b>and give its slots back</b>. Unload does
    /// this for you. Both halves matter: the unpatch stops the detour, the slot release is what
    /// lets the plugin's ALC collect.
    /// </summary>
    public int RemoveAll()
    {
        int n = PatchRegistry.RemoveAll(_owner);
        // Backstop for an entry whose unpatch threw, and for a slot that outlived its entry.
        PatchSlotLink.ReleaseOwner(_ownerId);
        return n;
    }

    /// <summary>Live patch count across every owner, plus the slot pool, for a status command.</summary>
    public string Describe()
    {
        string pins = PatchRegistry.DescribePins();
        string slots = PatchSlotLink.Describe();
        return string.IsNullOrEmpty(slots) ? pins : pins + " " + slots;
    }
}
