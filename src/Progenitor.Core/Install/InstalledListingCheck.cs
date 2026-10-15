using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Install;

/// <summary>What ties a catalog listing's package to the copy of a mod that is installed or loaded here.</summary>
internal enum ListingVerification
{
    /// <summary>No listing was found at all.</summary>
    None = 0,

    /// <summary>An install-ledger row for this side names this listing's package.</summary>
    Ledger = 1,

    /// <summary>A package <c>manifest.json</c> beside the DLL names this listing's package.</summary>
    Manifest = 2,

    /// <summary>
    /// A listing was found (by GUID, identity or name), but nothing ties its package to these files. Nothing of
    /// it is shown for this mod: it may be another project with the same name.
    /// </summary>
    Unverified = 3,
}

/// <summary>The decision for one listing against one installed plugin.</summary>
internal readonly struct ListingCheck
{
    internal ListingCheck(ListingVerification verification, bool sameVersion)
    {
        Verification = verification;
        SameVersion = InstalledListingCheck.IsVerified(verification) && sameVersion;
    }

    /// <summary>What ties the listing's package to these files.</summary>
    internal ListingVerification Verification { get; }

    /// <summary>
    /// The same evidence also records the listing's version. Only then is the listing's README (written for that
    /// version) shown; a verified package at another version (an update is available) keeps its package facts.
    /// </summary>
    internal bool SameVersion { get; }

    /// <summary>The package is verified: author, store page, one-liner and sides may be shown.</summary>
    internal bool PackageVerified => InstalledListingCheck.IsVerified(Verification);
}

/// <summary>
/// The rule for showing a catalog listing's facts for an installed or loaded mod. A listing's package facts (author,
/// one-liner, store page, sides) are shown only when the install ledger row for this side, or a package manifest
/// beside the DLL, names the listing's package (<c>Owner-Name</c>). Its README is shown only when that evidence also
/// names the listing's version. A GUID or a name that spells the package name finds a listing, and it may be another
/// project with the same name (live: a hot-loaded 2.0 rework showed the 1.x package's README and its attribution).
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

    /// <summary>
    /// Decide the rule for one listing. <paramref name="ledger"/> must already be this side's rows
    /// (<see cref="LedgerOfThisSide"/>); a ledger row decides before the manifest does.
    /// </summary>
    internal static ListingCheck Check(
        CatalogEntry? entry,
        IReadOnlyList<InstalledLedgerRow>? ledger,
        PackageManifestBeside? beside)
    {
        if (entry == null) return new ListingCheck(ListingVerification.None, false);
        string key = (entry.Key ?? "").Trim();
        string version = (entry.Manifest.Version ?? "").Trim();
        if (key.Length == 0) return new ListingCheck(ListingVerification.Unverified, false);
        if (ledger != null)
        {
            bool package = false, same = false;
            for (int i = 0; i < ledger.Count; i++)
            {
                if (!InstalledState.TrySplitPackageSource(ledger[i].Source, out string name, out string ledgerVersion)) continue;
                if (!SamePackage(name, key)) continue;
                package = true;
                if (SameVersion(ledgerVersion, version)) same = true;
            }

            if (package) return new ListingCheck(ListingVerification.Ledger, same);
        }

        if (beside != null && SamePackage(beside.Package, key))
            return new ListingCheck(ListingVerification.Manifest, SameVersion(beside.Version, version));
        return new ListingCheck(ListingVerification.Unverified, false);
    }

    /// <summary>
    /// The ledger rows that describe the copy on this side. The rows of this process's own root, when there are any;
    /// none when this process has a copy loaded that no row of its own names (a server-root row is the local
    /// server's file, not this one); the other root's rows only for a mod this process does not have at all, whose
    /// page then describes that other copy.
    /// </summary>
    internal static IReadOnlyList<InstalledLedgerRow> LedgerOfThisSide(
        IReadOnlyList<InstalledLedgerRow>? rows, bool loadedHere, string otherRoot)
    {
        if (rows == null || rows.Count == 0) return Array.Empty<InstalledLedgerRow>();
        var own = new List<InstalledLedgerRow>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
            if (!string.Equals(rows[i].Root, otherRoot, StringComparison.OrdinalIgnoreCase)) own.Add(rows[i]);
        if (own.Count > 0) return own;
        return loadedHere ? Array.Empty<InstalledLedgerRow>() : rows;
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
