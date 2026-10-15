using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Install;

/// <summary>What ties a catalog listing to the copy of a mod that is installed or loaded here.</summary>
internal enum ListingVerification
{
    /// <summary>No listing was found at all.</summary>
    None = 0,

    /// <summary>An install-ledger row names this listing's package at this listing's version.</summary>
    Ledger = 1,

    /// <summary>A package <c>manifest.json</c> beside the DLL names this listing's package at this listing's version.</summary>
    Manifest = 2,

    /// <summary>
    /// A listing was found (by GUID, identity or name), but nothing ties it to these files, or it lists
    /// another version. Its README, description, author, sides and links are not shown for this mod.
    /// </summary>
    Unverified = 3,
}

/// <summary>
/// The rule for showing a catalog listing's facts (README, one-liner, author, release date, sides, store
/// page) for an installed or loaded mod: the listing's package (<c>Owner-Name</c>) <b>and</b> version
/// must be the ones these files came from, as the install ledger row or a package manifest beside the
/// DLL records them. A GUID or a name that spells the package name finds a listing, and it may be a
/// different version or a different project with the same name (live: a hot-loaded 2.0 rework showed
/// the 1.x package's README and its attribution).
/// </summary>
internal static class InstalledListingCheck
{
    internal static string NameOf(ListingVerification verification) => verification switch
    {
        ListingVerification.Ledger => "ledger",
        ListingVerification.Manifest => "manifest",
        ListingVerification.Unverified => "unverified",
        _ => "none",
    };

    internal static bool IsVerified(ListingVerification verification) =>
        verification == ListingVerification.Ledger || verification == ListingVerification.Manifest;

    /// <summary>Decide the rule for one listing against the evidence for one installed plugin.</summary>
    internal static ListingVerification Check(
        CatalogEntry? entry,
        IReadOnlyList<InstalledLedgerRow>? ledger,
        PackageManifestBeside? beside)
    {
        if (entry == null) return ListingVerification.None;
        string key = (entry.Key ?? "").Trim();
        string version = (entry.Manifest.Version ?? "").Trim();
        if (key.Length == 0 || version.Length == 0) return ListingVerification.Unverified;
        if (ledger != null)
        {
            for (int i = 0; i < ledger.Count; i++)
            {
                if (!InstalledState.TrySplitPackageSource(ledger[i].Source, out string package, out string ledgerVersion)) continue;
                if (SamePackage(package, key) && SameVersion(ledgerVersion, version)) return ListingVerification.Ledger;
            }
        }

        if (beside != null && SamePackage(beside.Package, key) && SameVersion(beside.Version, version))
            return ListingVerification.Manifest;
        return ListingVerification.Unverified;
    }

    internal static bool SamePackage(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The same version string, ignoring a leading <c>v</c> and surrounding space.</summary>
    internal static bool SameVersion(string? a, string? b)
    {
        string x = Bare(a), y = Bare(b);
        return x.Length > 0 && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    private static string Bare(string? v)
    {
        string s = (v ?? "").Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1])) s = s.Substring(1);
        return s;
    }
}
