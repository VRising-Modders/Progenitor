using System;
using System.Collections.Generic;

namespace Progenitor.Install;

/// <summary>INSTALLSIDES1: what the Install tab's server picker points the Server column at.</summary>
internal enum ServerTargetKind
{
    /// <summary><c>None · this client only</c>: the Server column has nothing to act on.</summary>
    None = 0,

    /// <summary>A dedicated server: the connected one, or a dedicated-server folder on this PC.</summary>
    Server = 1,

    /// <summary>
    /// <c>Private games (this PC)</c>: the private game this client hosts. Its bundled server loads
    /// the client's plugins (<c>HostedServerPluginsMode.Shared</c>) or only Progenitor's own folders
    /// (<c>ProgenitorOnly</c>), so there is nothing to install on it separately.
    /// </summary>
    PrivateGames = 2,
}

/// <summary>INSTALLSIDES1: what one side (this client, or the picked server) holds of a listing.</summary>
internal enum HalfState
{
    Absent = 0,
    Installed = 1,

    /// <summary>Written; loads at that process's next start.</summary>
    Pending = 2,

    /// <summary>Still loaded; goes at that process's next start.</summary>
    RemovalPending = 3,

    /// <summary>Nothing could say (a connected server that lists nothing to this player).</summary>
    Unknown = 4,
}

/// <summary>INSTALLSIDES1: one button a side cell offers. Each acts on that side only.</summary>
internal enum SideButton
{
    None = 0,
    Install = 1,
    Update = 2,
    Uninstall = 3,
    CancelInstall = 4,
    CancelUninstall = 5,

    /// <summary>
    /// INSTALLSIDES2 item 3: a server action this player may not take (not on the server's admin
    /// list). Drawn dim and disabled in place of the buttons an admin would get.
    /// </summary>
    AdminOnly = 6,

    /// <summary>
    /// INSTALLSIDES2 item 3: the same lock, when the game's own <c>adminauth</c> could lift it (on the
    /// admin list, not authenticated): the existing <c>Authenticate as admin</c> flow.
    /// </summary>
    Authenticate = 7,

    /// <summary>
    /// SRVUNINST1: the server refused this side's uninstall because its dependency check could not
    /// run or found dependents; this sends it again with <c>force</c>, after its own confirmation.
    /// </summary>
    UninstallAnyway = 8,
}

/// <summary>INSTALLSIDES2 item 3: what a server cell offers a player the route refuses for want of admin.</summary>
internal enum AdminOffer
{
    /// <summary>Not refused for admin (or not a server route).</summary>
    None = 0,

    /// <summary>Not an admin and nothing here can change it: a dim <c>Admin only</c>.</summary>
    AdminOnly = 1,

    /// <summary>On the admin list, not authenticated: <c>Authenticate as admin</c>.</summary>
    Authenticate = 2,

    /// <summary>The authentication started; waiting for the server's answer.</summary>
    Authenticating = 3,
}

/// <summary>INSTALLSIDES1: the color a side cell's state text takes.</summary>
internal enum SideCellTone
{
    Normal = 0,
    Muted = 1,
    Ok = 2,
    Warn = 3,
    Pending = 4,
}

/// <summary>INSTALLSIDES1: the facts about one side that the per-side decision reads.</summary>
internal sealed class SideHalf
{
    public HalfState State { get; init; }

    /// <summary>The version this side holds (empty when unknown).</summary>
    public string Version { get; init; } = "";

    /// <summary>A step running on this side (<c>Installing…</c>, <c>Downloading…</c>); empty when idle.</summary>
    public string Busy { get; init; } = "";

    /// <summary>The running step can still be canceled.</summary>
    public bool BusyCancelable { get; init; }

    /// <summary>The running step is an uninstall (its cancel says <c>Cancel uninstall</c>).</summary>
    public bool BusyIsRemoval { get; init; }

    /// <summary>This manager can uninstall the copy (Progenitor's ledger, the preloader's offer, or the server's rows).</summary>
    public bool CanUninstall { get; init; }

    /// <summary>A pending copy this manager wrote and can delete.</summary>
    public bool CanCancelInstall { get; init; }

    /// <summary>A pending removal this manager can take back.</summary>
    public bool CanCancelUninstall { get; init; }

    /// <summary>
    /// Installed by another tool and not removable here (<c>Managed elsewhere</c>, <c>Managed by Gale</c>);
    /// empty otherwise.
    /// </summary>
    public string Managed { get; init; } = "";

    /// <summary>This side's last install was refused (<c>Cannot install</c>); the reason is the drawer's.</summary>
    public string Refused { get; init; } = "";

    /// <summary>
    /// The refusal is a failed step worth trying again (a remote install that failed): the cell keeps
    /// its Install button beside the words. A planner refusal (<c>Cannot install</c>) is not: it would
    /// refuse again.
    /// </summary>
    public bool RefusedRetry { get; init; }

    /// <summary>REMOTEFOLLOW1: the refusal is final for this version (the server would refuse it again): muted words, no retry.</summary>
    public bool RefusedQuiet { get; init; }

    /// <summary>
    /// The copy lives in Progenitor's own folders (<c>progenitor-pending</c> / <c>progenitor-reload</c>).
    /// A private game in <c>ProgenitorOnly</c> mode still loads those (the reload folder stays shared).
    /// </summary>
    public bool ProgenitorManaged { get; init; }

    /// <summary>SRVUNINST1: this side's last uninstall was refused (short words); the drawer has the sentence.</summary>
    public string UninstallRefused { get; init; } = "";

    /// <summary>SRVUNINST1: that refusal may be overridden (<see cref="SideButton.UninstallAnyway"/>).</summary>
    public bool CanForceUninstall { get; init; }

    /// <summary>SRVUNINST1: where an installed copy lives when that is the news (<c>in progenitor-reload</c>); empty otherwise.</summary>
    public string Location { get; init; } = "";

    public static readonly SideHalf None = new();

    public bool Present => State is HalfState.Installed or HalfState.Pending;
}

/// <summary>INSTALLSIDES1: everything the two side cells of one listing are decided from. Plain values.</summary>
internal sealed class SideCellInputs
{
    /// <summary>The side the listing runs on (the inspected <c>[BepInProcess]</c>, else Thunderstore's tags).</summary>
    public PackageSide Side { get; init; }

    /// <summary>Only a manifest-verified <c>side: both</c> declaration. Tags never set it.</summary>
    public bool NeedsBoth { get; init; }

    /// <summary>The listing's version (the newest the provider lists).</summary>
    public string Latest { get; init; } = "";

    /// <summary>A Thunderstore tool or the loader: never installable from here. The client cell says <see cref="NotInstallableText"/>.</summary>
    public bool NotInstallable { get; init; }

    public string NotInstallableText { get; init; } = "";

    /// <summary>
    /// FIX-NONMODS: the Server cell's words for a not-installable listing (a package too large to
    /// download is too large for either side); empty keeps the dash.
    /// </summary>
    public string NotInstallableServerText { get; init; } = "";

    public SideHalf Client { get; init; } = SideHalf.None;

    public SideHalf Server { get; init; } = SideHalf.None;

    public ServerTargetKind Target { get; init; }

    /// <summary>For <see cref="ServerTargetKind.PrivateGames"/>: the hosted server junctions the client's plugins.</summary>
    public bool PrivateShared { get; init; } = true;

    /// <summary>For <see cref="ServerTargetKind.Server"/>: the route to the picked server.</summary>
    public ServerRoute Route { get; init; }

    /// <summary>
    /// INSTALLSIDES2 item 3: the route refuses this player for want of admin
    /// (<see cref="ServerRouteBlock.NotAdmin"/>); what the server cell offers instead.
    /// </summary>
    public AdminOffer Admin { get; init; }
}

/// <summary>INSTALLSIDES1: one side's cell — the state words, their tone, the buttons and the note when it cannot act.</summary>
internal sealed class SideCell
{
    public SideCell(string kind, string text, SideCellTone tone, IReadOnlyList<SideButton>? buttons = null, string note = "")
    {
        Kind = kind ?? "";
        Text = text ?? "";
        Tone = tone;
        Buttons = buttons ?? Array.Empty<SideButton>();
        Note = note ?? "";
    }

    /// <summary>
    /// INSTALLSIDES2 item 2: the side is not one this mod uses, or no server is picked. The cell is
    /// drawn grayed (dim fill) and has no button.
    /// </summary>
    public bool Grayed => Kind is SideCells.KindNotNeeded or SideCells.KindNoServer;

    /// <summary>
    /// INSTALLSIDES2 item 1: the state's mark drawn before the words (<c>✓</c> installed, <c>↑</c> an
    /// update or a missing half), or empty. The runtime draws it only when the game font has it.
    /// </summary>
    public string Glyph => Kind switch
    {
        SideCells.KindInstalled when Tone == SideCellTone.Ok => SideCells.InstalledGlyph,
        SideCells.KindUpdate or SideCells.KindMissing or SideCells.KindOlder => SideCells.UpdateGlyph,
        _ => "",
    };

    /// <summary>Readback name of the state (<see cref="SideCells"/>' <c>Kind*</c> constants).</summary>
    public string Kind { get; }

    /// <summary>The state words, including the note (<c>Not installed · Ask a server admin</c>).</summary>
    public string Text { get; }

    public SideCellTone Tone { get; }

    /// <summary>At most two buttons, in drawing order.</summary>
    public IReadOnlyList<SideButton> Buttons { get; }

    /// <summary>Why the server cell has no button (<see cref="SideCells.AskServerAdmin"/> / <see cref="SideCells.NoServer"/>); empty otherwise.</summary>
    public string Note { get; }

    public bool Has(SideButton button)
    {
        for (int i = 0; i < Buttons.Count; i++)
            if (Buttons[i] == button) return true;
        return false;
    }
}

/// <summary>INSTALLSIDES1: both cells of one listing and the shortcut under its name.</summary>
internal sealed class SideCellsDecision
{
    public SideCellsDecision(SideCell client, SideCell server, SideButton shortcut)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Server = server ?? throw new ArgumentNullException(nameof(server));
        Shortcut = shortcut;
    }

    public SideCell Client { get; }
    public SideCell Server { get; }

    /// <summary><see cref="SideButton.Install"/> (<c>Install on both</c>), <see cref="SideButton.Update"/> (<c>Update both</c>) or none.</summary>
    public SideButton Shortcut { get; }
}

/// <summary>
/// INSTALLSIDES1 (Option A, approved 2026-10-03): the Install row shows each side in its own
/// cell — this client and the picked server — with buttons that act on that side only. One
/// decision, game-free, so the Install row and the mod page head cannot disagree.
///
/// <para>Rules. A side the listing does not run on reads <c>Not needed</c> with no button, unless a
/// copy is there anyway (then it shows that copy so it can be removed). With the private games
/// picked, a server mod's client copy is the one those games load, so its Client cell installs it. Only a manifest-verified
/// <c>side: both</c> mod is ever <c>Missing</c> or <c>older than</c> the other side; Thunderstore's
/// tags are information. A side whose installed version is older than the listing offers
/// <c>Update</c> beside <c>Uninstall</c>. A server the picker cannot act on shows its state plus
/// <c>Ask a server admin</c> or <c>No server</c>, with no button. A private game shares the client's
/// plugins (<c>Same as client</c>); in <c>ProgenitorOnly</c> mode only Progenitor's own folders are
/// shared, so a copy another tool put in <c>BepInEx/plugins</c> reads <c>Not loaded there</c>.</para>
/// </summary>
internal static class SideCells
{
    // ---- Readback kinds --------------------------------------------------------------------------
    public const string KindNotNeeded = "not-used";
    public const string KindNotInstalled = "not-installed";
    public const string KindInstalled = "installed";
    public const string KindUpdate = "update";
    public const string KindMissing = "missing";
    public const string KindOlder = "older";
    public const string KindPending = "pending";
    public const string KindRemovalPending = "removal-pending";
    public const string KindBusy = "busy";
    public const string KindRefused = "refused";
    public const string KindUnknown = "unknown";
    public const string KindNoServer = "no-server";
    public const string KindSameAsClient = "same-as-client";
    public const string KindNotLoaded = "not-loaded";
    public const string KindNotInstallable = "not-installable";

    // ---- Words -----------------------------------------------------------------------------------
    /// <summary>
    /// INSTALLSIDES2 item 2: the grayed cell of a side this mod does not run on. Approved wording
    /// 2026-10-03 ("not used by this mod is a bit ambiguous… no x side"): the Client column reads
    /// <c>No client side</c>, the Server column <c>No server side</c> (was <c>Not used by this mod</c>,
    /// before that <c>Not needed</c>).
    /// </summary>
    public const string NoClientSide = "No client side";

    /// <inheritdoc cref="NoClientSide"/>
    public const string NoServerSide = "No server side";

    /// <summary>The grayed words for the column a cell is in (<paramref name="gameSide"/>: the Client column).</summary>
    public static string NotUsed(bool gameSide) => gameSide ? NoClientSide : NoServerSide;
    public const string NotInstalled = "Not installed";
    public const string InstalledPrefix = "Installed";
    public const string UpdateWord = "update";
    public const string MissingServerHasIt = "Missing: the server has it";
    public const string MissingClientHasIt = "Missing: the client has it";
    public const string OlderThanClient = "older than client";
    public const string OlderThanServer = "older than server";
    public const string UnknownWord = "Unknown";
    public const string NoServer = "No server";

    /// <summary>INSTALLSIDES2 item 2: the grayed Server cell while the picker is on None.</summary>
    public const string NoServerSelected = "No server selected";

    /// <summary>INSTALLSIDES2 item 1: the installed mark (U+2713). Drawn only when the game font has it.</summary>
    public const string InstalledGlyph = "\u2713";

    /// <summary>INSTALLSIDES2 item 1: the update / missing mark (U+2191, WGL4).</summary>
    public const string UpdateGlyph = "\u2191";

    /// <summary>INSTALLSIDES2 item 3: the lock before <c>Admin only</c> (U+1F512). Outside the BMP: drawn only if the font has it.</summary>
    public const string LockGlyph = "\U0001F512";

    public const string AdminOnlyCaption = "Admin only";
    public const string AuthenticateCaption = AdminAuthFlow.ButtonCaption;
    public const string AuthenticatingCaption = AdminAuthFlow.AuthenticatingText;
    public const string AskServerAdmin = "Ask a server admin";
    public const string SameAsClient = "Same as client";
    public const string NotLoadedThere = "Not loaded there";
    public const string CannotInstall = "Cannot install";
    public const string Dash = "—";

    /// <summary>The separator <see cref="Progenitor.Ui.InstallColumns.StatusLines"/> breaks a two-part state at.</summary>
    public const string Dot = " · ";

    // ---- Captions --------------------------------------------------------------------------------
    public const string InstallCaption = "Install";
    public const string UpdateCaption = "Update";
    public const string UninstallCaption = "Uninstall";
    public const string CancelInstallCaption = "Cancel install";
    public const string CancelUninstallCaption = "Cancel uninstall";
    public const string InstallBothCaption = "Install on both";
    public const string UpdateBothCaption = "Update both";

    public static string Caption(SideButton button) => button switch
    {
        SideButton.Install => InstallCaption,
        SideButton.Update => UpdateCaption,
        SideButton.Uninstall => UninstallCaption,
        SideButton.CancelInstall => CancelInstallCaption,
        SideButton.CancelUninstall => CancelUninstallCaption,
        SideButton.AdminOnly => AdminOnlyCaption,
        SideButton.Authenticate => AuthenticateCaption,
        SideButton.UninstallAnyway => RemoteUninstallForce.AnywayCaption,
        _ => "",
    };

    public static string ShortcutCaption(SideButton shortcut) => shortcut switch
    {
        SideButton.Install => InstallBothCaption,
        SideButton.Update => UpdateBothCaption,
        _ => "",
    };

    /// <summary>Readback name of a button (<c>install</c>, <c>cancel-uninstall</c>…).</summary>
    public static string Name(SideButton button) => button switch
    {
        SideButton.Install => "install",
        SideButton.Update => "update",
        SideButton.Uninstall => "uninstall",
        SideButton.CancelInstall => "cancel-install",
        SideButton.CancelUninstall => "cancel-uninstall",
        SideButton.AdminOnly => "admin-only",
        SideButton.Authenticate => "authenticate",
        SideButton.UninstallAnyway => "uninstall-anyway",
        _ => "none",
    };

    public static string Name(AdminOffer offer) => offer switch
    {
        AdminOffer.AdminOnly => "admin-only",
        AdminOffer.Authenticate => "authenticate",
        AdminOffer.Authenticating => "authenticating",
        _ => "none",
    };

    public static string Name(SideCellTone tone) => tone switch
    {
        SideCellTone.Muted => "muted",
        SideCellTone.Ok => "ok",
        SideCellTone.Warn => "warn",
        SideCellTone.Pending => "pending",
        _ => "normal",
    };

    public static string Name(ServerTargetKind kind) => kind switch
    {
        ServerTargetKind.Server => "server",
        ServerTargetKind.PrivateGames => "private-games",
        _ => "none",
    };

    /// <summary><c>Installed 2.4.0</c>, or <c>Installed</c> when the version is unknown.</summary>
    public static string InstalledText(string? version)
    {
        string v = (version ?? "").Trim();
        return v.Length == 0 ? InstalledPrefix : InstalledPrefix + " " + v;
    }

    /// <summary>The words that say why a route-blocked server cell has no button.</summary>
    /// <remarks>
    /// REMOTEUNREACH1 (live 2026-10-03): every route block used to read <c>Ask a server admin</c>, so an
    /// authorized admin whose server could not reach Thunderstore was told to ask an admin. Each block
    /// now says what it is; only not-admin and not-allowed name an admin.
    /// </remarks>
    public static string BlockNote(ServerRoute route) => route.Block switch
    {
        ServerRouteBlock.NotAdmin or ServerRouteBlock.NotAllowed => AskServerAdmin,
        ServerRouteBlock.Unreachable => ServerCantReachThunderstore,
        ServerRouteBlock.RemoteOff => InstallStatusLabels.RemoteInstallsOff,
        ServerRouteBlock.NotProgenitor => InstallStatusLabels.NoProgenitor,
        ServerRouteBlock.Pending => InstallStatusLabels.Checking,
        _ => NoServer,
    };

    /// <summary>REMOTEUNREACH1: an unreachable-Thunderstore route's note (the server, not this client, is cut off).</summary>
    public const string ServerCantReachThunderstore = "Server can't reach Thunderstore";

    /// <summary>Does the side's installed version trail the listing's?</summary>
    public static bool Outdated(string? installed, string? latest)
    {
        string i = (installed ?? "").Trim();
        string l = (latest ?? "").Trim();
        if (i.Length == 0 || l.Length == 0) return false;
        return SideStatusRules.Compare(i, l) < 0;
    }

    public static SideCellsDecision Decide(SideCellInputs input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        var client = ClientCell(input);
        var server = ServerCell(input);
        var shortcut = SideButton.None;
        if (client.Has(SideButton.Install) && server.Has(SideButton.Install)) shortcut = SideButton.Install;
        else if (client.Has(SideButton.Update) && server.Has(SideButton.Update)) shortcut = SideButton.Update;
        return new SideCellsDecision(client, server, shortcut);
    }

    // ---- The client cell -------------------------------------------------------------------------

    private static SideCell ClientCell(SideCellInputs input)
    {
        var half = input.Client ?? SideHalf.None;
        if (input.NotInstallable)
            return new SideCell(KindNotInstallable, input.NotInstallableText, SideCellTone.Muted);
        if (half.Busy.Length > 0) return BusyCell(half);

        // A server-side mod is not needed on this client — unless the picked server is the private
        // games this client hosts, which load it from this client's plugins.
        bool runsHere = input.Side != PackageSide.Server || input.Target == ServerTargetKind.PrivateGames;
        var other = input.Target == ServerTargetKind.Server ? input.Server ?? SideHalf.None : SideHalf.None;
        return HalfCell(half, input, runsHere, gameSide: true, other, canAct: true, canInstall: true, blockNote: "");
    }

    // ---- The server cell -------------------------------------------------------------------------

    private static SideCell ServerCell(SideCellInputs input)
    {
        if (input.NotInstallable)
            return new SideCell(KindNotInstallable,
                string.IsNullOrEmpty(input.NotInstallableServerText) ? Dash : input.NotInstallableServerText, SideCellTone.Muted);
        bool runsThere = input.Side != PackageSide.Client;
        switch (input.Target)
        {
            case ServerTargetKind.None:
                // INSTALLSIDES2 item 2: grayed for every mod, the server mods included.
                return new SideCell(KindNoServer, NoServerSelected, SideCellTone.Muted);
            case ServerTargetKind.PrivateGames:
                return PrivateGamesCell(input, runsThere);
        }

        var half = input.Server ?? SideHalf.None;
        var route = input.Route;
        if (half.Busy.Length > 0) return BusyCell(half);

        // INSTALLSIDES2 item 3: not an admin. The states stay as an admin would read them; the buttons
        // an admin would get become one dim Admin only (or Authenticate as admin when adminauth can
        // lift the lock). No grayed cell and no "Ask a server admin" note.
        if (route.Block == ServerRouteBlock.NotAdmin && input.Admin != AdminOffer.None)
        {
            var asAdmin = HalfCell(half, input, runsThere, gameSide: false, input.Client ?? SideHalf.None, true, true, "");
            // REMOTEFOLLOW1 (live: HotDemo read "Installed 0.1.0" with no lock for a non-admin): a copy
            // the server holds is one an admin could uninstall or cancel, even when this player's view
            // of it (the handshake, no ledger rows) carries no button. It gets the lock too.
            bool held = half.State is HalfState.Installed or HalfState.Pending or HalfState.RemovalPending
                && half.Managed.Length == 0;
            if (asAdmin.Buttons.Count == 0 && !held) return asAdmin;
            var locked = input.Admin is AdminOffer.Authenticate or AdminOffer.Authenticating
                ? SideButton.Authenticate
                : SideButton.AdminOnly;
            return new SideCell(asAdmin.Kind, asAdmin.Text, asAdmin.Tone, new[] { locked });
        }

        bool canInstall = route.CanInstall;
        bool canAct = route.CanManage;
        string note = canInstall ? "" : BlockNote(route);
        return HalfCell(half, input, runsThere, gameSide: false, input.Client ?? SideHalf.None, canAct, canInstall, note);
    }

    private static SideCell PrivateGamesCell(SideCellInputs input, bool runsThere)
    {
        if (!runsThere) return new SideCell(KindNotNeeded, NoServerSide, SideCellTone.Muted);
        var client = input.Client ?? SideHalf.None;
        if (input.PrivateShared || !client.Present || client.ProgenitorManaged)
            return new SideCell(KindSameAsClient, SameAsClient, SideCellTone.Muted);
        // ProgenitorOnly: the hosted server's plugins folder holds only Bootstrap. A copy another
        // tool put in the client's BepInEx/plugins never reaches it.
        return new SideCell(KindNotLoaded, NotLoadedThere, SideCellTone.Warn);
    }

    // ---- Shared ----------------------------------------------------------------------------------

    private static SideCell BusyCell(SideHalf half)
    {
        var buttons = half.BusyCancelable
            ? new[] { half.BusyIsRemoval ? SideButton.CancelUninstall : SideButton.CancelInstall }
            : Array.Empty<SideButton>();
        return new SideCell(KindBusy, half.Busy, SideCellTone.Pending, buttons);
    }

    /// <summary>
    /// One side's cell from its own half. <paramref name="runs"/> is false when the listing's side
    /// excludes this half; <paramref name="other"/> is the opposite side's half (for the both-sided
    /// <c>Missing</c> / <c>older than</c> words). <paramref name="canAct"/> gates Uninstall and the
    /// cancels; <paramref name="canInstall"/> gates Install and Update; <paramref name="blockNote"/>
    /// is appended when an install-shaped action is withheld.
    /// </summary>
    private static SideCell HalfCell(
        SideHalf half, SideCellInputs input, bool runs, bool gameSide, SideHalf other, bool canAct, bool canInstall, string blockNote)
    {
        string restart = gameSide ? InstallStatusLabels.PendingGameRestart : InstallStatusLabels.PendingServerRestart;
        string removal = gameSide ? InstallStatusLabels.RemovalPendingGameRestart : InstallStatusLabels.RemovalPendingServerRestart;
        var buttons = new List<SideButton>(2);

        switch (half.State)
        {
            case HalfState.Pending:
                if (canAct && half.CanCancelInstall) buttons.Add(SideButton.CancelInstall);
                return new SideCell(KindPending, WithNote(restart, buttons.Count == 0 && half.CanCancelInstall ? blockNote : ""),
                    SideCellTone.Pending, buttons);

            case HalfState.RemovalPending:
                if (canAct && half.CanCancelUninstall) buttons.Add(SideButton.CancelUninstall);
                // INSTALLSIDES2 item 1: waiting on a restart reads pending (pale blue), like an install.
                return new SideCell(KindRemovalPending, removal, SideCellTone.Pending, buttons);

            case HalfState.Installed:
                return InstalledCell(half, input, gameSide, other, canAct, canInstall, blockNote);
        }

        if (half.Refused.Length > 0)
        {
            if (half.RefusedRetry && canInstall) buttons.Add(SideButton.Install);
            // REMOTEFOLLOW1: a refusal the server would repeat reads muted, with nothing to press.
            return new SideCell(KindRefused, half.Refused, half.RefusedQuiet ? SideCellTone.Muted : SideCellTone.Warn, buttons);
        }

        if (half.State == HalfState.Unknown)
        {
            if (!runs) return new SideCell(KindNotNeeded, NotUsed(gameSide), SideCellTone.Muted);
            if (canInstall) buttons.Add(SideButton.Install);
            return new SideCell(KindUnknown, WithNote(UnknownWord, canInstall ? "" : blockNote), SideCellTone.Muted, buttons);
        }

        // Absent.
        if (!runs) return new SideCell(KindNotNeeded, NotUsed(gameSide), SideCellTone.Muted);
        if (canInstall) buttons.Add(SideButton.Install);
        if (input.NeedsBoth && other.Present)
            return new SideCell(KindMissing, WithNote(gameSide ? MissingServerHasIt : MissingClientHasIt, canInstall ? "" : blockNote),
                SideCellTone.Warn, buttons, canInstall ? "" : blockNote);
        return new SideCell(KindNotInstalled, WithNote(NotInstalled, canInstall ? "" : blockNote), SideCellTone.Muted, buttons,
            canInstall ? "" : blockNote);
    }

    private static SideCell InstalledCell(
        SideHalf half, SideCellInputs input, bool gameSide, SideHalf other, bool canAct, bool canInstall, string blockNote)
    {
        var buttons = new List<SideButton>(2);
        string v = half.Version;
        bool outdated = Outdated(v, input.Latest);
        bool older = !outdated && input.NeedsBoth && other.Present && v.Length > 0 && other.Version.Length > 0
            && SideStatusRules.Compare(v, other.Version) < 0;
        bool update = (outdated || older) && canInstall;
        if (update) buttons.Add(SideButton.Update);
        // SRVUNINST1: after a refused uninstall the cell says so, and offers the override when there is one.
        bool refused = half.UninstallRefused.Length > 0;
        if (canAct && half.CanUninstall && half.Managed.Length == 0)
            buttons.Add(refused && half.CanForceUninstall ? SideButton.UninstallAnyway : SideButton.Uninstall);
        if (refused && !outdated && !older)
            return new SideCell(KindInstalled, InstalledText(v) + Dot + half.UninstallRefused, SideCellTone.Warn, buttons);

        string withheld = (outdated || older) && !canInstall ? blockNote : "";
        if (outdated)
            return new SideCell(KindUpdate, WithNote(v + Dot + UpdateWord + " " + input.Latest.Trim(), withheld), SideCellTone.Warn, buttons, withheld);
        if (older)
            return new SideCell(KindOlder, WithNote(v + Dot + (gameSide ? OlderThanServer : OlderThanClient), withheld), SideCellTone.Warn,
                buttons, withheld);
        string text = InstalledText(v);
        if (half.Managed.Length > 0) text += Dot + half.Managed;
        else if (half.Location.Length > 0) text += Dot + half.Location;
        return new SideCell(KindInstalled, text, half.Managed.Length > 0 ? SideCellTone.Muted : SideCellTone.Ok, buttons);
    }

    // ---- INSTALLSIDES2 item 5: the drawer's per-side restart note ------------------------------

    public const string ClientLabel = "Client";
    public const string ServerLabel = "Server";
    public const string PrivateGamesLabel = "Private games";
    public const string LoadsNow = "loads without a restart";
    public const string LoadsAtGameRestart = "loads at the next game restart";
    public const string LoadsAtServerRestart = "loads at the next server restart";
    public const string LoadsWithClient = "load the client's copy at their next start";

    /// <summary>
    /// The drawer's head note, one part per side the mod uses: <c>Client: loads without a restart ·
    /// Server: loads at the next server restart</c>. A side the mod does not run on is left out, and
    /// so is the server while none is picked; a tool or the loader has no note.
    ///
    /// <para>Client: without a restart when the open plan applies now, else (no plan yet) when the
    /// listing declares it needs none. Server: the local-folder route never loads anything into a
    /// running server from here (the server's own Bootstrap promotes the copy at its next start); the
    /// remote route hot-loads a listing that declares no restart. The private games this client hosts
    /// load the client's copy when they start.</para>
    /// </summary>
    public static string RestartNote(
        PackageSide side, ServerTargetKind target, ServerRouteKind route, bool declaresNoRestart,
        bool? clientPlanNeedsRestart = null, bool notInstallable = false, bool localServerHot = false)
    {
        if (notInstallable) return "";
        var parts = new List<string>(2);
        bool clientUses = side != PackageSide.Server;
        bool serverUses = side != PackageSide.Client;
        if (clientUses)
        {
            bool restart = clientPlanNeedsRestart ?? !declaresNoRestart;
            parts.Add(ClientLabel + ": " + (restart ? LoadsAtGameRestart : LoadsNow));
        }

        if (serverUses)
        {
            switch (target)
            {
                case ServerTargetKind.PrivateGames:
                    parts.Add(PrivateGamesLabel + ": " + LoadsWithClient);
                    break;
                case ServerTargetKind.Server:
                    // HOTDEMO1: the local folder too, when its running server's reload folder is watched.
                    bool now = (route == ServerRouteKind.Remote || (route == ServerRouteKind.Local && localServerHot)) && declaresNoRestart;
                    parts.Add(ServerLabel + ": " + (now ? LoadsNow : LoadsAtServerRestart));
                    break;
            }
        }

        return string.Join(Dot, parts);
    }

    private static string WithNote(string text, string note) =>
        string.IsNullOrEmpty(note) ? text : text + Dot + note;

    /// <summary>The readback of one cell: <c>{kind, text, tone, buttons[], note}</c>.</summary>
    public static Dictionary<string, object?> ToWidget(SideCell cell)
    {
        var buttons = new List<string>(cell.Buttons.Count);
        for (int i = 0; i < cell.Buttons.Count; i++) buttons.Add(Name(cell.Buttons[i]));
        return new Dictionary<string, object?>
        {
            ["kind"] = cell.Kind,
            ["text"] = cell.Text,
            ["tone"] = Name(cell.Tone),
            ["buttons"] = buttons,
            ["note"] = cell.Note,
        };
    }
}
