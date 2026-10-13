using System;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// The disconnect-screen panel (job FMAN17). Opens by itself, on the client, when a join is
/// refused over a mod mismatch — and never otherwise.
///
/// <para><b>Where it draws.</b> After a handshake kick the client sits on
/// <c>ProjectM.UI.ConnectingView</c>'s error screen (<c>ClientState = 7</c>,
/// <c>_ForceShowBackButton</c>) with <c>Client_0</c> still alive. The overlay is a
/// <c>Canvas</c> at sorting order 5000 under a <c>DontDestroyOnLoad</c> root, so it draws
/// above that screen. It belongs to that join attempt: it is disposed — not hidden — when
/// the client reaches the main menu, when a new join starts, when a later handshake result
/// replaces this one, and on every Core unload. The Mods-menu Escape contract (hidden
/// handles linger until the next <c>Take()</c>) does not apply; a panel logged as closed
/// must not keep rendering.</para>
///
/// <para><b>Read-only, by construction.</b> There is no accept button, because accepting could not
/// work: <c>ModManifest.HandleRequest</c> gates every package request on
/// <c>HandshakeOkUsers</c> and a refused client is never in it. The panel says that outright
/// (<see cref="ModDenyText.NothingWasDownloaded"/>) rather than offering an action that would
/// fail. Every string comes from <see cref="ModDenyText"/>, which the unit tests compile; this file
/// is the thin part on purpose.</para>
///
/// <para><b>It opens once per handshake result.</b> The local diagnosis is on screen in the
/// frame of the refusal. When the signed descriptor lands a frame or two later, <see cref="Open"/>
/// rebuilds the same overlay in place (no close/open pair, no second canvas). A second result
/// replaces this one; it never stacks. <c>ModManifestDeny</c> caps rebuilds per join.</para>
/// </summary>
public static class ModManifestDenyPanel
{
    /// <summary>Plugin id the panel's root is owned by (<c>progenitor-ui-progenitor</c>).</summary>
    public const string PluginId = "progenitor";

    private static readonly object Gate = new();
    private static readonly object Owner = new();
    private static readonly OwnedPanel Owned = new();
    private static bool _subscribed;

    /// <summary>
    /// True while the panel is on screen.
    ///
    /// <para>Derived from the handle, not shadowed beside it (job FUI29). Escape still hides
    /// through <c>Ui.OnBootstrapKey</c> without telling this owner; <see cref="DismissHidden"/>
    /// disposes that leftover the same frame, so a closed log cannot keep a Built handle
    /// drawing. The argument for deriving rather than being told is on
    /// <see cref="OwnedPanel"/>.</para>
    /// </summary>
    public static bool IsOpen => Owned.IsOpen;

    /// <summary>True while a handle is held, including one Escape has already hidden.</summary>
    internal static bool HasHeldPanel
    {
        get
        {
            try { return Owned.Held != null; }
            catch { return false; }
        }
    }

    /// <summary>Subscribe to the deny module. Called from <c>ModManifest.Install</c> on a client.</summary>
    internal static void Install()
    {
        lock (Gate)
        {
            if (_subscribed) return;
            _subscribed = true;
        }

        ModManifestDeny.PanelRequested += OnPanelRequested;
#if !PROGENITOR_TESTS
        try { Plugin.Frame += DismissHidden; }
        catch { /* a missing frame hook still has Open/Close */ }
#endif
    }

    /// <summary>Unsubscribe and close. Called from <c>ModManifest.Uninstall</c> before the ALC goes.</summary>
    internal static void Uninstall()
    {
        bool subscribed;
        lock (Gate)
        {
            subscribed = _subscribed;
            _subscribed = false;
        }

        if (subscribed) ModManifestDeny.PanelRequested -= OnPanelRequested;
#if !PROGENITOR_TESTS
        try { Plugin.Frame -= DismissHidden; }
        catch { /* teardown */ }
#endif
        Close();
    }

    // PLAINDENY1: the panel opens on the plain summary; "Show details" rebuilds it in place with
    // the technical lines under it. A new refusal always opens collapsed.
    private static bool _showDetails;
    private static ModDenyReport? _shown;

    private static void OnPanelRequested(ModDenyReport report)
    {
        _showDetails = false;
        try { Open(report); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: deny panel open threw: " + e.Message);
        }
    }

    /// <summary>
    /// Build and show the panel for one refused join. A second call for the same on-screen
    /// result rebuilds in place (no close/open pair). A call while nothing is shown opens
    /// once. The previous handle is always disposed, never left Hidden.
    /// </summary>
    public static IUiPanel Open(ModDenyReport report)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        bool replacing = Owned.IsOpen;
        DropHeld(logClosed: !replacing);

        var panel = Ui.Ui.Panel(ModDenyText.PanelTitle(report.ServerName), Owner, PluginId);
        var stale = Owned.Adopt(panel);
        if (stale != null && !ReferenceEquals(stale, panel))
        {
            try { stale.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: deny panel stale dispose threw: " + e.Message); }
        }

        if (panel.Enabled && !Build(panel, report))
        {
            // DIALOGS1: the chrome (and its backdrop) may already be on the panel; a half-built
            // panel must not leave the disconnect screen dimmed and unclickable.
            DropHeld(logClosed: true);
            return panel;
        }

        Ui.Ui.Show(panel);
        // DIALOGS1: Close is the main action, so Enter closes it (after the same grace a dialog has).
        try { _openedAt = UiDialog.Clock(); } catch { _openedAt = 0; }
        UiDialog.EnterFallback = EnterHandler;
        if (!replacing)
        {
            Plugin.Logger?.LogInfo(
                "Ui: deny panel opened host=" + report.Host + " rows=" + report.Rows.Count
                + " sourced=" + report.SourcedRows);
        }
        return panel;
    }

    /// <summary>
    /// Dispose the panel. No-op when nothing is held. Always destroys the handle; a Hide
    /// that leaves a Built overlay drawing is not a close.
    /// </summary>
    public static void Close() => DropHeld(logClosed: true);

    /// <summary>
    /// Escape hid this panel and told nobody. Dispose the leftover handle so it cannot
    /// keep rendering after the owner already treats it as closed.
    /// </summary>
    internal static void DismissHidden()
    {
        if (Owned.Held == null || Owned.IsOpen) return;
        DropHeld(logClosed: true);
    }

    private static long _openedAt;

    private static readonly Func<bool> EnterHandler = EnterClose;

    /// <summary>Enter on the refusal panel: Close, its main action. False while nothing is open or inside the grace.</summary>
    internal static bool EnterClose()
    {
        if (!Owned.IsOpen) return false;
        long now;
        try { now = UiDialog.Clock(); } catch { return false; }
        if (now - _openedAt < UiDialog.EnterGraceMs) return false;
        Close();
        return true;
    }

    private static void DropHeld(bool logClosed)
    {
        if (ReferenceEquals(UiDialog.EnterFallback, EnterHandler)) UiDialog.EnterFallback = null;
        var panel = Owned.Take();
        if (panel == null) return;
        try { panel.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: deny panel dispose threw: " + e.Message); }
        if (logClosed)
            Plugin.Logger?.LogInfo("Ui: deny panel closed.");
    }

    private static bool Build(IUiPanel panel, ModDenyReport report)
    {
        _shown = report;
#if !PROGENITOR_TESTS
        // Readable names for mods this client has loaded (its own rows); others fall back to the
        // package name or a tidied id.
        ModDenyText.NameLookup ??= LoadedName;
#endif
        // DIALOGS1: drawn with the shared dialog layout — the title, the plain summary (and on
        // request the technical details) as lines, and one centered button row with Show details on
        // the left and Close, the main action, on the right. The panel keeps its own lifecycle
        // (OwnedPanel, disposed per join); it is not one of the queued kit dialogs.
        var lines = new System.Collections.Generic.List<string>(ModDenyText.Summary(report));
        if (_showDetails) lines.AddRange(ModDenyText.Details(report));
        var content = new DialogContent
        {
            Shape = UiDialogShape.Notice,
            // JOINFLOW1 review: the dialog title is a rich-text label and the name is the server's; drawn literally.
            Title = UiRichText.Literal(ModDenyText.PanelTitle(report.ServerName)),
            Lines = lines,
            RowKey = ButtonsRow,
            MainKey = CellClose,
            MainCaption = ModDenyText.CloseButton,
            MainTone = CellTone.Gold,
            Footer = Ui.Ui.FrameworkFooter,
        };
        content.ExtraButtons.Add(new DialogButton(
            CellDetails, _showDetails ? ModDenyText.HideDetailsButton : ModDenyText.ShowDetailsButton, DialogButtonRole.Option));

        UiDialogView view;
        try { view = BuildView(panel, content); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: deny panel build threw: " + e.Message);
            return false;
        }

        try
        {
            view.Buttons?.OnClick(CellDetails, ToggleDetails);
            view.Buttons?.OnClick(CellClose, Close);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: deny panel buttons threw: " + e.Message);
        }

        return true;
    }

    /// <summary>The view builder; a test replaces it to make the build throw after the chrome is on.</summary>
    internal static Func<IUiPanel, DialogContent, UiDialogView> BuildView { get; set; } = (p, c) => UiDialogView.Build(p, c);

    /// <summary>DIALOGS1: the button row's key and its two cells (<c>deny-buttons/details</c>, <c>deny-buttons/close</c>).</summary>
    internal const string ButtonsRow = "deny-buttons";

    internal const string CellDetails = "details";

    internal const string CellClose = "close";

#if !PROGENITOR_TESTS
    private static string? LoadedName(string id)
    {
        var rows = Ui.ModMenuInventory.Snapshot().Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Id, id, StringComparison.OrdinalIgnoreCase) && rows[i].DisplayName.Length > 0)
                return rows[i].DisplayName;
        }
        return null;
    }
#endif

    private static void ToggleDetails()
    {
        var report = _shown;
        if (report == null) return;
        _showDetails = !_showDetails;
        try { Open(report); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: deny panel details toggle threw: " + e.Message); }
    }

}
