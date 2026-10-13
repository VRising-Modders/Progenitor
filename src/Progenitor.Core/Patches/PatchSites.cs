using System;
using System.Collections.Generic;
using System.Reflection;
using Progenitor.Interop;
using Progenitor.Native;

namespace Progenitor.Patches;

/// <summary>
/// Turns a patch target into the <see cref="PatchSiteInfo"/> the registry's refusal gate reads.
/// Resolution order, matching the rule in <c>docs/dev-loop.md</c>:
///
/// <list type="number">
/// <item><description><b>Address library first.</b> A target registered with
/// <see cref="Register"/> carries a metadata method name, so it goes through
/// <see cref="DetourSite.Resolve"/>: the <c>{side}-{build}.json</c> table for the running
/// GameAssembly, then the interop <c>NativeMethodInfoPtr_*</c> prefix fallback only when no
/// table is attached. That is the only path that reports <c>path=table</c>.</description></item>
/// <item><description><b>Interop MethodInfo otherwise.</b> An arbitrary plugin target has no
/// table id — the wanted list ships a handful of methods, not 1.2 million — so the pointer comes
/// from the generated <c>NativeMethodInfoPtr_{name}_*</c> field on the declaring interop type
/// (<see cref="Il2CppNative.MethodPointerByPrefix"/>), which throws rather than guess when the
/// method is overloaded. Path <c>fallback</c>.</description></item>
/// <item><description><b>Unknown.</b> Overloaded, not an interop type, or the module is not
/// loaded. Path <c>unknown</c>, address <c>?</c>. Allowed to patch — Harmony works on the managed
/// interop <see cref="MethodInfo"/> regardless — but the shared-empty-stub guard cannot fire,
/// which is why the ids for Core's own sites are registered explicitly.</description></item>
/// </list>
///
/// <para>Nothing here throws: a resolver failure degrades to <c>unknown</c>.</para>
/// </summary>
internal static class PatchSites
{
    private sealed class Known
    {
        internal Known(string site, string addressId, Func<IntPtr>? fallback)
        {
            Site = site;
            AddressId = addressId;
            Fallback = fallback;
        }

        internal string Site { get; }
        internal string AddressId { get; }
        internal Func<IntPtr>? Fallback { get; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<MethodBase, Known> Table = new();

    /// <summary>
    /// Give <paramref name="target"/> an address-library id so it resolves through the table
    /// instead of the prefix fallback. Core's kill-path sites register here; a plugin may too.
    /// </summary>
    internal static void Register(MethodBase target, string site, string addressId, Func<IntPtr>? prefixFallback)
    {
        if (target == null) return;
        lock (Gate) Table[target] = new Known(site, addressId, prefixFallback);
    }

    /// <summary>Drop every registered id. Core <c>Uninstall</c> calls this.</summary>
    internal static void Clear()
    {
        lock (Gate) Table.Clear();
    }

    /// <summary>
    /// <see cref="PatchRegistry.SiteResolver"/>'s implementation. Never throws.
    /// </summary>
    internal static PatchSiteInfo Describe(MethodBase target)
    {
        string name = PatchRegistry.DescribeTarget(target);
        if (target == null) return PatchSiteInfo.Unknown(name);

        bool burst = IsBurst(target);

        Known? known;
        lock (Gate) Table.TryGetValue(target, out known);

        if (known != null)
        {
            try
            {
                var result = DetourSite.Resolve(known.Site, known.AddressId, known.Fallback);
                if (result.Ok || result.ImageOffset > 0)
                    return new PatchSiteInfo(name, result.Path, result.ImageOffset, burst);
                return new PatchSiteInfo(name, AddressId.PathMissing, PatchSitePolicy.UnknownImageOffset, burst);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("patches: DetourSite.Resolve(" + known.Site + ") threw: " + e.Message);
                return new PatchSiteInfo(name, PatchSitePolicy.PathUnknown, PatchSitePolicy.UnknownImageOffset, burst);
            }
        }

        long imageOffset = TryPrefixImageOffset(target);
        if (imageOffset == PatchSitePolicy.UnknownImageOffset)
            return new PatchSiteInfo(name, PatchSitePolicy.PathUnknown, PatchSitePolicy.UnknownImageOffset, burst);
        return new PatchSiteInfo(name, AddressId.PathFallback, imageOffset, burst);
    }

    /// <summary>
    /// Compiled function pointer for an interop method with no overloads, turned into a relative address.
    /// <see cref="Il2CppNative.MethodPointerByPrefix"/> throws on an ambiguous or missing name,
    /// which is the answer we want: guessing an overload would put the stub check on the wrong
    /// method.
    /// </summary>
    private static long TryPrefixImageOffset(MethodBase target)
    {
        try
        {
            var declaring = target.DeclaringType;
            if (declaring == null) return PatchSitePolicy.UnknownImageOffset;
            var ptr = Il2CppNative.MethodPointerByPrefix(declaring, target.Name);
            if (ptr == IntPtr.Zero) return PatchSitePolicy.UnknownImageOffset;

            var moduleBase = AddressLibrary.ModuleBase;
            if (moduleBase == IntPtr.Zero) return PatchSitePolicy.UnknownImageOffset;
            long imageOffset = ptr.ToInt64() - moduleBase.ToInt64();
            return imageOffset > 0 ? imageOffset : PatchSitePolicy.UnknownImageOffset;
        }
        catch
        {
            // Ambiguous overload, non-interop type, or the static ctor could not run.
            return PatchSitePolicy.UnknownImageOffset;
        }
    }

    /// <summary>
    /// Feed <see cref="PatchSitePolicy.IsBurstJob"/> the live reflection names. Attribute
    /// <em>names</em> only: reading attribute instances off an interop type can throw, and the
    /// name is all the policy needs.
    /// </summary>
    private static bool IsBurst(MethodBase target)
    {
        try
        {
            var attributes = new List<string>();
            foreach (var data in CustomAttributeData.GetCustomAttributes(target))
                attributes.Add(data.AttributeType.Name);

            var declaring = target.DeclaringType;
            var interfaces = new List<string>();
            if (declaring != null)
            {
                foreach (var data in CustomAttributeData.GetCustomAttributes(declaring))
                    attributes.Add(data.AttributeType.Name);
                foreach (var iface in declaring.GetInterfaces())
                    interfaces.Add(iface.Name);
            }

            return PatchSitePolicy.IsBurstJob(attributes, interfaces);
        }
        catch
        {
            return false;
        }
    }
}
