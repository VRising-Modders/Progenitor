#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Install;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>
/// JOINADMIN1 (join flow stage 3): what the admin panel borrows from the Install tab — its catalog (the
/// recommend picker's Thunderstore search) and its server update (the alert's "Update on server"). The panel's
/// entry point is the "Players joining this server" row on Progenitor's page (<see cref="JoinServerSettings"/>),
/// no longer a button on the Install tab's toolbar.
/// </summary>
public static partial class ModsMenu
{
    /// <summary>The Install tab's catalog listings matching <paramref name="query"/> (the recommend picker).</summary>
    internal static IReadOnlyList<CatalogEntry> JoinAdminCatalogSearch(string? query)
    {
        try { return Catalog().Search(query); }
        catch { return Array.Empty<CatalogEntry>(); }
    }

    /// <summary>CLIENTPOLICY1: the install-ledger rows of both roots (the client report's packages). Never throws.</summary>
    internal static IReadOnlyList<InstalledLedgerRow> JoinAdminLedger()
    {
        try { return CurrentLedger(); }
        catch { return Array.Empty<InstalledLedgerRow>(); }
    }

    /// <summary>The listing a loaded plugin came from, or null.</summary>
    internal static CatalogEntry? JoinAdminEntryForPlugin(string pluginId)
    {
        try { return (Catalog() as InstallCatalogService)?.FindEntryForPlugin(pluginId); }
        catch { return null; }
    }

    /// <summary>
    /// Whether the Install tab offers a server update for plugin <paramref name="pluginId"/> now; with
    /// <paramref name="start"/>, start it exactly as the row's Server Update button does.
    /// </summary>
    internal static bool JoinAdminServerUpdate(string pluginId, bool start, out string listingKey, out string latest, out string why)
    {
        listingKey = latest = why = "";
        try { return EnsureInstallPane().ServerUpdateFor(pluginId, start, out listingKey, out latest, out why); }
        catch (Exception e)
        {
            why = "The Install tab could not answer: " + e.Message;
            return false;
        }
    }
}
#endif
