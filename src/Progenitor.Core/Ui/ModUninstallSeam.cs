using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// MODSPAGE1: the pending-uninstall state the mod page needs and does not own.
///
/// <para>Job INSTALL2 owns the ledger flag (<c>uninstallPending</c> on the row plus the
/// <c>.uninstall</c> marker beside the reload copy that Bootstrap honors at start) and
/// <c>src/Progenitor.Core/Install/*.cs</c>, which this job must not edit. So the page reads the
/// state and drives the three actions through this seam, and INSTALL2 implements it and assigns it
/// to <see cref="ModsMenu.UninstallSeam"/>. Until then the seam is null and the page falls back to
/// today's verified behavior: Uninstall deletes the ledgered copies now (the mod stays loaded until
/// the process restarts), and the two states that need the marker — <c>Reactivate</c> and
/// <c>Cancel install</c> — are refused with <see cref="ModPageCopy.ActionUnavailable"/> rather than
/// claimed.</para>
///
/// <para>Every method answers a <see cref="ServerInstallResult"/> — the same result the Install
/// tab's remover answers — so the page can report what actually happened instead of assuming.</para>
/// </summary>
internal interface IModUninstallSeam
{
    /// <summary>
    /// Does the ledger (or its <c>.uninstall</c> marker) say this plugin is removed at the next
    /// start? Absence is "not pending"; this must never throw.
    /// </summary>
    bool IsUninstallPending(string pluginId);

    /// <summary>
    /// Mark an installed mod for removal at the next restart, keeping it loaded until then. A
    /// hot-loaded copy (<paramref name="hot"/>) is removed at once instead, which is the existing
    /// ledger-scoped removal.
    /// </summary>
    ServerInstallResult MarkUninstall(string pluginId, bool hot);

    /// <summary>Reactivate: drop the pending removal so nothing changes at the next restart.</summary>
    ServerInstallResult CancelUninstall(string pluginId);

    /// <summary>
    /// Cancel install: delete the written-but-not-loaded copy, so nothing loads at the next restart.
    /// </summary>
    ServerInstallResult CancelPendingInstall(string pluginId);

    /// <summary>
    /// SRVUNINST1: does this seam account for the plugin at all (a catalog listing its ledger names)?
    /// False means <see cref="MarkUninstall"/> would refuse it as not ours, so the page does not offer it.
    /// </summary>
    bool Owns(string pluginId) => true;

    /// <summary>
    /// SRVUNINST1: does <see cref="MarkUninstall"/> with <paramref name="hot"/> unload the copy now (true) or mark it for the
    /// next start (false)? The confirmation is worded from this, so it promises what Yes does.
    /// </summary>
    bool UnloadsNow(string pluginId, bool hot) => hot;

    /// <summary>
    /// F10 (live 2026-10-07): does the configured local server folder's copy go now (that server's reloader holds a trusted
    /// hot-unloadable image), rather than at its next restart? Decided from the server's own state, never this client's
    /// reloader.
    /// </summary>
    bool ServerUnloadsNow(string pluginId) => false;

    /// <summary>
    /// F4/F10 (live 2026-10-07): remove exactly the sides the confirmation named: this client's copy
    /// (<paramref name="client"/>, by <see cref="UnloadsNow"/>) and the local server folder's copy (<paramref name="server"/>, by
    /// <see cref="ServerUnloadsNow"/>). Nothing else is touched.
    /// </summary>
    ServerInstallResult UninstallSides(string pluginId, bool hot, bool client, bool server) => MarkUninstall(pluginId, hot);
}
