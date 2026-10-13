using System;
using System.Collections.Generic;
using Progenitor.Install;
using Progenitor.Networking;

namespace Progenitor.Ui;

/// <summary>
/// INSTALLRESTART1: the Install tab's pending-restart bar, Simple and Advanced alike. One solid band (the mod page's status
/// band) pinned above the column header while a client install, update or removal waits for V Rising to restart: the
/// sentence (<c>2 mods load after a restart</c>) and one key-cap button (<c>Restart now</c>, <c>Restart and rejoin
/// &lt;server&gt;</c> or <c>Restart and resume &lt;save&gt;</c>), which opens the confirmation. The bottom pending bar stays
/// as it was (its list of pending rows and Restart server now).
/// </summary>
internal sealed partial class InstallModsPane
{
    internal const string RestartBandKey = "install-restart";
    internal const string CellRestartText = "restart-text";
    internal const string CellRestartButton = "restart-button";

    /// <summary>The bar's offer (live: <c>JoinFlowClient.CurrentRestartOffer</c>). Null: no bar.</summary>
    internal Func<ClientRestartOffer>? RestartOfferProbe { get; set; }

    /// <summary>The bar's button (live: <c>JoinFlowClient.PressRestartButton</c>, which opens the confirmation).</summary>
    internal Action? RestartPress { get; set; }

    private IUiHandle? _restartBand;
    private ClientRestartOffer _restartOffer = ClientRestartOffer.None;

    /// <summary>The catalog has listings (the index loaded): its pending rows are the ones the bar counts.</summary>
    internal bool HasEntries
    {
        get
        {
            try { return AllEntries().Count > 0; }
            catch { return false; }
        }
    }

    /// <summary>The bar drawn by the last build (readback and tests); null when none.</summary>
    internal IUiHandle? RestartBandHandle => _restartBand;

    /// <summary>The offer the last build drew (<see cref="ClientRestartOffer.None"/> when no bar).</summary>
    internal ClientRestartOffer RestartOfferShown => _restartOffer;

    /// <summary>The band's cells: the sentence (filling the row) and the key-cap button. Game-free.</summary>
    internal static List<CellSpec> RestartBandCells(ClientRestartOffer offer) => new()
    {
        new CellSpec(CellKind.Text, CellRestartText).With(text: offer.Sentence, width: CellWidth.Fill(160f), tone: CellTone.Pending),
        new CellSpec(CellKind.Spacer, CellGap),
        new CellSpec(CellKind.Button, CellRestartButton).With(text: offer.Button, align: CellAlign.Right, skin: CellSkin.KeyCap),
    };

    /// <summary>Build the band when something waits for a restart (called by <c>Build</c> among the pinned top rows).</summary>
    private void BuildRestartBand(IUiPanel panel)
    {
        _restartBand = null;
        _restartOffer = ClientRestartOffer.None;
        ClientRestartOffer offer;
        try { offer = RestartOfferProbe?.Invoke() ?? ClientRestartOffer.None; }
        catch { offer = ClientRestartOffer.None; }
        if (!offer.Shown) return;
        _restartOffer = offer;
        var band = panel.CellRow(new CellRowSpec(RestartBandKey, RestartBandCells(offer),
            height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, band: true));
        band.OnClick(CellRestartButton, PressRestart);
        _restartBand = band;
    }

    /// <summary>The bar's button (bridge: <c>widget_click label=install-restart/restart-button</c>).</summary>
    internal void PressRestart()
    {
        try { RestartPress?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Install: Restart now threw: " + e.Message); }
    }

    /// <summary>The band's readback: <c>pendingRestart {count, mods, canRestart, rejoinTarget, …}</c>.</summary>
    private Dictionary<string, object?> RestartBandWidget() => new() { ["pendingRestart"] = _restartOffer.Describe() };

    /// <summary>
    /// The client changes that wait for V Rising to restart, in list order: a pending install or update whose client half is
    /// pending (or whose halves cannot be read: this process's own write), a client copy waiting while the server's loads,
    /// and a pending removal with a client half. Changes waiting only on a server are not counted: a game restart does not
    /// apply them.
    /// </summary>
    /// <summary>F7: the Mods list's switch holds this listing's plugin off (tests: <see cref="SwitchedOffProbe"/>).</summary>
    internal Func<string, bool>? SwitchedOffProbe { get; set; }

    private bool SwitchedOff(CatalogEntry entry)
    {
        try
        {
            string? id = RestingIdentity(entry)?.PluginGuid;
            if (string.IsNullOrWhiteSpace(id)) id = entry.Manifest.Id;
            if (string.IsNullOrWhiteSpace(id)) return false;
            return SwitchedOffProbe?.Invoke(id!) ?? Plugins.ModEnableState.IsDisabled(id!);
        }
        catch { return false; }
    }

    internal List<(CatalogEntry Entry, bool Remove)> ClientPendingEntries()
    {
        var list = new List<(CatalogEntry, bool)>();
        var entries = SortedForView(AllEntries());
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (IsNotInstallable(entry)) continue;
            // F7 (live 2026-10-07): a mod the player switched off (a hot suspend, or held off) waits for no restart: its file
            // on disk is not a pending install.
            if (SwitchedOff(entry)) continue;
            InstallRowAction action;
            try { action = ActionFor(entry); }
            catch { continue; }
            if (action.Remote) continue;
            var halves = HalvesOf(entry);
            bool clientPending = halves.Client == InstalledState.HalfPending;
            bool anyHalf = clientPending || halves.Server != InstalledState.HalfNone || halves.Client != InstalledState.HalfNone;
            if (action.State == InstallListingState.Pending)
            {
                if (clientPending || !anyHalf) list.Add((entry, false));
                continue;
            }

            if (action.State == InstallListingState.PendingUninstall)
            {
                if (halves.Client != InstalledState.HalfNone || !anyHalf) list.Add((entry, true));
                continue;
            }

            // INSTALLFAIL1: a client copy waiting for the game restart while the local server already loads its own.
            try
            {
                if (DetectClient(entry).State == InstalledKind.Pending) list.Add((entry, false));
            }
            catch { /* no evidence */ }
        }

        return list;
    }
}
