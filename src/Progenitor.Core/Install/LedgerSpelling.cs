using System;

namespace Progenitor.Install;

/// <summary>
/// POLISH4 item 2: "which plugin GUID did an inspection read out of this listing's package" —
/// the <c>progenitor-cache/identity</c> route, as a probe so nothing here reads a folder. Null (or a
/// probe that throws, or an empty answer) means "not known", never "guess from the name".
/// </summary>
/// <param name="entry">The listing being asked about.</param>
internal delegate string? LedgerIdentityProbe(CatalogEntry entry);

/// <summary>
/// POLISH4: the one comparison that answers "does this install-ledger row name this listing". Every
/// reader asks it and no caller keeps its own version, because the class of defect this closes is
/// exactly two comparisons disagreeing.
///
/// <para><b>The live defect (2026-09-13, deploy #76).</b> HotDemo (<c>Progenitor-HotDemo</c>, plugin
/// <c>progenitor.hotdemo</c>) was installed to Both. The local server's row read
/// <c>"source": "package:progenitor.hotdemo@0.1.0"</c> — the <em>plugin id</em>, written by
/// the first-class <c>progenitor.json</c> restart route — and in a later session the catalog entry
/// carried no inspected manifest, so <c>Manifest.Id</c> was empty and none of the four spellings the
/// old comparison knew matched. The Install row read <c>halves {client: loaded, server: none}</c>,
/// POLISH3's per-half Uninstall reported <c>server: none</c> and did nothing, the pending file and
/// its ledger row stayed, and the Mods tab went on listing the mod as <c>pendload</c> under its
/// GUID.</para>
///
/// <para><b>The two halves of the fix.</b> Writers now record the <em>listing key</em> in
/// <c>source</c> and put the plugin id in the row's own optional <c>pluginId</c> field
/// (<see cref="ServerInstallLedgerEntry.PluginId"/>). Readers accept both spellings: the row's
/// <c>pluginId</c>, and — for a row an older build wrote under the plugin id with no such field —
/// the identity cache, which maps a listing key to the plugin GUID a prior inspection read out of
/// its bytes. So an old row resolves without a re-inspection and a new row resolves with no cache at
/// all.</para>
/// </summary>
internal static class LedgerSpelling
{
    /// <summary>
    /// The <c>source</c> this build writes for one listing: <c>package:&lt;listing key&gt;@&lt;version&gt;</c>.
    /// Never the plugin id — that is the row's <c>pluginId</c> field.
    /// </summary>
    internal static string SourceFor(CatalogEntry entry, string? version) =>
        OrdinaryDependencies.LedgerSource(
            entry == null ? "" : entry.Key,
            string.IsNullOrWhiteSpace(version) ? "0" : version!.Trim());

    /// <summary>
    /// Does <paramref name="package"/> — the package half of a ledger row's <c>source</c> — name
    /// <paramref name="entry"/>?
    ///
    /// <para>The spellings, and no others: the listing key, the inspected manifest's id, the
    /// manifest's package, <c>&lt;owner&gt;-&lt;name&gt;</c>, and (POLISH4) the plugin GUID
    /// <paramref name="identity"/> reports for this listing. The last one is what makes a row an
    /// older build wrote under the plugin id resolvable in a session that has inspected nothing.</para>
    /// </summary>
    internal static bool Matches(CatalogEntry entry, string? package, LedgerIdentityProbe? identity = null)
    {
        if (entry == null || string.IsNullOrWhiteSpace(package)) return false;
        string p = package!.Trim();
        if (string.Equals(entry.Key, p, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(entry.Manifest.Id)
            && string.Equals(entry.Manifest.Id, p, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(entry.Manifest.Package)
            && string.Equals(entry.Manifest.Package, p, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(entry.Owner) && !string.IsNullOrEmpty(entry.PackageName)
            && string.Equals(entry.Owner + "-" + entry.PackageName, p, StringComparison.OrdinalIgnoreCase))
            return true;
        string cached = IdentityOf(entry, identity);
        return cached.Length > 0 && string.Equals(cached, p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same question for a whole scanned row: its <c>source</c>'s package half by the rule above,
    /// or its own <c>pluginId</c> field against any spelling of this listing's identity. The second
    /// rule is the one that needs no cache, so a row this build wrote resolves on its own bytes.
    /// </summary>
    internal static bool Matches(CatalogEntry entry, InstalledLedgerRow row, LedgerIdentityProbe? identity = null)
    {
        if (entry == null) return false;
        if (Matches(entry, row.Id, identity)) return true;
        if (row.PluginId.Length == 0) return false;
        if (!string.IsNullOrEmpty(entry.Manifest.Id)
            && string.Equals(entry.Manifest.Id, row.PluginId, StringComparison.OrdinalIgnoreCase))
            return true;
        string cached = IdentityOf(entry, identity);
        return cached.Length > 0 && string.Equals(cached, row.PluginId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The probe's answer, or empty. A probe that throws is a cache miss, not an error.</summary>
    internal static string IdentityOf(CatalogEntry entry, LedgerIdentityProbe? identity)
    {
        if (entry == null || identity == null) return "";
        string? guid;
        try { guid = identity(entry); }
        catch { return ""; }
        return string.IsNullOrWhiteSpace(guid) ? "" : guid!.Trim();
    }
}
