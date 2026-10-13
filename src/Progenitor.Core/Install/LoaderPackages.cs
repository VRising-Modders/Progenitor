using System;
using System.Collections.Generic;

namespace Progenitor.Install;

/// <summary>
/// MODS2: the one place that knows which catalog listings are the <em>plugin loader</em> rather
/// than a mod. BepInEx is what loads Progenitor, so it is always already installed by the time
/// this menu exists, and Progenitor never writes <c>BepInEx/core</c> — so the loader's row is
/// read-only on the Install tab and its counterpart sits in the Mods tab's <c>Required</c> group.
///
/// <para>Data-driven on purpose: one constant list of package full names plus the
/// owner/name-prefix rule Thunderstore's own metadata carries, and
/// <see cref="HasCoreLayout"/> for the case where a package has been inspected and its archive
/// layout is known. No behavior branches on a display name.</para>
/// </summary>
internal static class LoaderPackages
{
    /// <summary>The Thunderstore team that publishes the loader packs.</summary>
    public const string LoaderOwner = "BepInEx";

    /// <summary>Every loader pack's package name starts with this.</summary>
    public const string LoaderNamePrefix = "BepInExPack";

    /// <summary>The V Rising loader pack, by its Thunderstore full name.</summary>
    public const string VRisingLoaderFullName = "BepInEx-BepInExPack_V_Rising";

    /// <summary>
    /// Known loader packages, matched against a listing's catalog key / full name. Add a name
    /// here rather than teaching a pane about a package.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownLoaderPackages = new[]
    {
        VRisingLoaderFullName,
        "BepInEx-BepInExPack",
        "BepInEx-BepInExPack_IL2CPP",
    };

    /// <summary>INSTALL2 item 1: the Thunderstore team that publishes Progenitor itself.</summary>
    public const string ProgenitorOwner = "Progenitor";

    /// <summary>
    /// INSTALL2 item 1: Progenitor's own package names. The framework is what draws this menu, so a
    /// listing of it is never something the menu installs; once it is published, its row would
    /// otherwise offer to write the assembly the row is being drawn by.
    /// </summary>
    public static readonly IReadOnlyList<string> ProgenitorPackageNames = new[]
    {
        "Progenitor",
        "Progenitor_VRising",
    };

    /// <summary>
    /// INSTALL2 item 1: every listing the Install tab hides outright — the loader packs and
    /// Progenitor's own packages, by their Thunderstore full names. One constant list; nothing
    /// branches on a display name.
    /// </summary>
    public static readonly IReadOnlyList<string> HiddenPackages = Hidden();

    private static IReadOnlyList<string> Hidden()
    {
        var list = new List<string>(KnownLoaderPackages);
        for (int i = 0; i < ProgenitorPackageNames.Count; i++)
            list.Add(ProgenitorOwner + "-" + ProgenitorPackageNames[i]);
        return list;
    }

    /// <summary>
    /// Is this listing the plugin loader? True for a known package full name, and for any
    /// package whose owner is <see cref="LoaderOwner"/> and whose name starts with
    /// <see cref="LoaderNamePrefix"/> (so a future <c>BepInExPack_V_Rising_2</c> is covered).
    /// </summary>
    public static bool IsLoaderPackage(string? owner, string? packageName, string? key)
    {
        string candidate = (key ?? "").Trim();
        for (int i = 0; i < KnownLoaderPackages.Count; i++)
        {
            if (candidate.Length > 0
                && string.Equals(candidate, KnownLoaderPackages[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        Split(owner, packageName, candidate, out string team, out string name);
        return string.Equals(team, LoaderOwner, StringComparison.OrdinalIgnoreCase)
            && name.StartsWith(LoaderNamePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// INSTALL2 item 1: is this listing Progenitor itself? Owner <see cref="ProgenitorOwner"/> and
    /// one of <see cref="ProgenitorPackageNames"/>, exactly — a mod that merely has "Progenitor" in
    /// its name is somebody else's mod and stays listed.
    /// </summary>
    public static bool IsProgenitorPackage(string? owner, string? packageName, string? key)
    {
        Split(owner, packageName, (key ?? "").Trim(), out string team, out string name);
        if (!string.Equals(team, ProgenitorOwner, StringComparison.OrdinalIgnoreCase)) return false;
        for (int i = 0; i < ProgenitorPackageNames.Count; i++)
        {
            if (string.Equals(name, ProgenitorPackageNames[i], StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// INSTALL2 item 1: a listing the Install tab does not show at all, on any filter, and does not
    /// count — the loader pack and Progenitor's own packages. Hiding is not the same as
    /// <see cref="IsLoaderPackage"/>'s read-only row: a hidden listing has no row, so no filter, no
    /// count and no pending total can reach it.
    /// </summary>
    public static bool IsHiddenPackage(string? owner, string? packageName, string? key) =>
        IsLoaderPackage(owner, packageName, key) || IsProgenitorPackage(owner, packageName, key);

    /// <summary>Owner and name of a listing, falling back to splitting the <c>&lt;owner&gt;-&lt;name&gt;</c> key.</summary>
    private static void Split(string? owner, string? packageName, string candidate, out string team, out string name)
    {
        team = (owner ?? "").Trim();
        name = (packageName ?? "").Trim();
        if (name.Length != 0 || candidate.Length == 0) return;
        int dash = candidate.IndexOf('-');
        if (dash <= 0) return;
        if (team.Length == 0) team = candidate.Substring(0, dash);
        name = candidate.Substring(dash + 1);
    }

    /// <summary>
    /// The inspector's evidence for the same fact: an archive that carries a <c>core/</c> folder
    /// is the loader's own layout, never a plugin's. Used only where an inspection is already in
    /// hand; the name rules above decide a row that has not been inspected.
    /// </summary>
    public static bool HasCoreLayout(IReadOnlyList<string>? archivePaths)
    {
        if (archivePaths == null) return false;
        for (int i = 0; i < archivePaths.Count; i++)
        {
            string path = (archivePaths[i] ?? "").Replace('\\', '/').TrimStart('/');
            if (path.StartsWith("core/", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.IndexOf("/core/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }
}
