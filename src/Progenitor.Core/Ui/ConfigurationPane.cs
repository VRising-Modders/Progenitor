using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>Left-aligned Configuration sections. The previous centered settings dump is retired.</summary>
internal static class ConfigurationLayout
{
    public const string Alignment = "left";
    public const string RetiredCentredDump = "centred-dump";
}

/// <summary>
/// <c>[Mods]</c> keys on the shared <c>progenitor.cfg</c>. There is no separate
/// machine-wide store: BepInEx <c>config/</c> is per install. The dedicated-server path is
/// "this computer, this game install", not a roaming identity.
/// </summary>
internal static class ModsConfigKeys
{
    public const string Section = "Mods";

    public const string UseThunderstore = "UseThunderstore";
    public const string UseNexus = "UseNexus";
    public const string NexusApiKey = "NexusApiKey";
    public const string DedicatedServerPath = "DedicatedServerPath";
    public const string IndexPath = "IndexPath";

    /// <summary>INSTALLSIDES1: list Thunderstore packages their authors flagged as NSFW. Default off.</summary>
    public const string ShowNsfwContent = "ShowNsfwContent";

    /// <summary>Personal: this BepInEx profile's listing filter.</summary>
    public const string ShowNsfwContentScope = "personal";

    /// <summary>Personal: this BepInEx profile's source toggle.</summary>
    public const string UseThunderstoreScope = "personal";

    /// <summary>Personal: this BepInEx profile's source toggle. Default off.</summary>
    public const string UseNexusScope = "personal";

    /// <summary>Personal credential. Never log the value.</summary>
    public const string NexusApiKeyScope = "personal-credential";

    /// <summary>Machine-local path stored in this profile (this computer, this game install).</summary>
    public const string DedicatedServerPathScope = "machine-local-in-this-profile";

    /// <summary>Personal: optional override of the pre-fetched index path.</summary>
    public const string IndexPathScope = "personal";

    /// <summary>Settings organization stage 3: remember the Install tab's filters and sort across restarts (default off).</summary>
    public const string RememberInstallFilters = "RememberInstallFilters";

    /// <summary>The remembered filters and sort (<see cref="InstallFilterMemory"/>); written only while the switch is on.</summary>
    public const string InstallFiltersRemembered = "InstallFiltersRemembered";

    public const string RememberInstallFiltersDescription =
        "Remember the Install mods tab's filters and sort across restarts of V Rising. Off (default): every session starts "
        + "from the defaults. Personal to this BepInEx profile.";

    public const string InstallFiltersRememberedDescription =
        "The Install mods tab's filters and sort, remembered while RememberInstallFilters is on. Written by the Mods menu; "
        + "an empty or unreadable value starts from the defaults.";

    public static string Scope(string key)
    {
        if (string.Equals(key, UseThunderstore, StringComparison.Ordinal)) return UseThunderstoreScope;
        if (string.Equals(key, UseNexus, StringComparison.Ordinal)) return UseNexusScope;
        if (string.Equals(key, NexusApiKey, StringComparison.Ordinal)) return NexusApiKeyScope;
        if (string.Equals(key, DedicatedServerPath, StringComparison.Ordinal)) return DedicatedServerPathScope;
        if (string.Equals(key, IndexPath, StringComparison.Ordinal)) return IndexPathScope;
        if (string.Equals(key, ShowNsfwContent, StringComparison.Ordinal)) return ShowNsfwContentScope;
        return "personal";
    }
}

/// <summary>Bound <c>[Mods]</c> entries plus the validated local-server folder.</summary>
internal sealed class ModsConfigBindings
{
    public ModsConfigBindings(
        ConfigEntry<bool> useThunderstore,
        ConfigEntry<bool> useNexus,
        ConfigEntry<string> nexusApiKey,
        ConfigEntry<string> dedicatedServerPath,
        ConfigEntry<string> indexPath,
        ConfigEntry<bool> showNsfwContent)
    {
        ShowNsfwContent = showNsfwContent ?? throw new ArgumentNullException(nameof(showNsfwContent));
        UseThunderstore = useThunderstore ?? throw new ArgumentNullException(nameof(useThunderstore));
        UseNexus = useNexus ?? throw new ArgumentNullException(nameof(useNexus));
        NexusApiKey = nexusApiKey ?? throw new ArgumentNullException(nameof(nexusApiKey));
        DedicatedServerPath = dedicatedServerPath ?? throw new ArgumentNullException(nameof(dedicatedServerPath));
        IndexPath = indexPath ?? throw new ArgumentNullException(nameof(indexPath));
    }

    public ConfigEntry<bool> UseThunderstore { get; }
    public ConfigEntry<bool> UseNexus { get; }
    public ConfigEntry<string> NexusApiKey { get; }
    public ConfigEntry<string> DedicatedServerPath { get; }
    public ConfigEntry<string> IndexPath { get; }

    /// <summary>INSTALLSIDES1: <c>[Mods] ShowNsfwContent</c>. Read when the Install list filters; nothing subscribes to it.</summary>
    public ConfigEntry<bool> ShowNsfwContent { get; }

    /// <summary>
    /// INSTALLSIMPLE1: <c>[Mods] InstallView</c> (<c>Simple</c>, the default, or <c>Advanced</c>) — the
    /// Install mods tab's remembered view. Read when the tab builds; written by its view switch.
    /// Null when bound by an older caller.
    /// </summary>
    public ConfigEntry<string>? InstallView { get; init; }

    // ---- Settings organization stage 3: the mod-manager rows the Configuration tab draws -----------------
    // Each is the one entry its own module reads (bound here with that module's type, default and words, so the
    // bind returns the same entry). Null when bound by an older caller: the row is then left out.

    /// <summary><c>[Mods] RememberInstallFilters</c>.</summary>
    public ConfigEntry<bool>? RememberInstallFilters { get; init; }

    /// <summary><c>[Mods] InstallFiltersRemembered</c> (cfg only).</summary>
    public ConfigEntry<string>? InstallFiltersRemembered { get; init; }

    /// <summary><c>[Workspace] BackgroundIntensity</c> (read by the workspace each layout).</summary>
    public ConfigEntry<float>? BackgroundIntensity { get; init; }

    /// <summary><c>[Workspace] BackgroundContrast</c>.</summary>
    public ConfigEntry<float>? BackgroundContrast { get; init; }

    /// <summary><c>[Workspace] CanonicalInGameFrame</c>.</summary>
    public ConfigEntry<bool>? CanonicalInGameFrame { get; init; }

    /// <summary><c>[Ui] AdminAuthButton</c> (followed live by <c>AdminAuthFlow</c>).</summary>
    public ConfigEntry<bool>? AdminAuthButton { get; init; }

    /// <summary><c>[Install] MaxDownloadMB</c> (read when a download is planned).</summary>
    public ConfigEntry<int>? MaxDownloadMb { get; init; }

    /// <summary><c>[Install] CurrentUpdateTag</c> (read when the Install tab resolves the current update).</summary>
    public ConfigEntry<string>? CurrentUpdateTag { get; init; }

    /// <summary>AUTOUPDATE1: <c>[AutoUpdate] Mods</c> (<c>Automatic</c>, <c>Notify</c> or <c>Off</c>; read by the update state).</summary>
    public ConfigEntry<string>? AutoUpdateMods { get; init; }

    /// <summary>AUTOUPDATE1: <c>[AutoUpdate] PromptBeforeJoin</c> (read at each join).</summary>
    public ConfigEntry<bool>? AutoUpdatePromptBeforeJoin { get; init; }

    /// <summary>AUTOUPDATE2: <c>[AutoUpdate] MajorVersions</c> (<c>Automatic</c> or <c>Notify</c>; read by the update state).</summary>
    public ConfigEntry<string>? AutoUpdateMajorVersions { get; init; }

    /// <summary>AUTOUPDATE5: <c>[AutoUpdate] Progenitor</c> (<c>Automatic</c>, <c>Notify</c> or <c>Off</c>; Progenitor's own update).</summary>
    public ConfigEntry<string>? AutoUpdateProgenitor { get; init; }
}

/// <summary>
/// Configuration tab: settings-kit form, no hand-built parallel widgets. Source toggles,
/// local dedicated-server folder (text + Apply + status + Clear), and the framework's
/// own settings entry that today lives on the overview.
/// </summary>
/// <summary>
/// REMOTEINST1: the one way an admin-gated control offers authentication — an "Authenticate as
/// admin" button for an admin-list member who has not run adminauth (when <c>[Ui] AdminAuthButton</c>
/// is on), the words to type it otherwise, "ask the host" for a player not on the list, and nothing
/// for anyone already authenticated or when the server has not said.
/// </summary>
internal static class AdminAuthUi
{
    internal const string ButtonKey = "admin-auth";

    /// <summary>What the control would show now (for tests and the readback).</summary>
    internal static Progenitor.Install.AdminAuthOfferKind OfferNow(bool featureGated) =>
        Progenitor.Install.AdminAuthFlow.Offer(Progenitor.Install.RemoteInstallClient.Shared.Status,
            Progenitor.Install.AdminAuthFlow.Shared.Enabled, featureGated);

    internal static void Draw(IUiPanel panel, System.Collections.Generic.ICollection<object>? handles, bool featureGated)
    {
        if (panel == null) return;
        var flow = Progenitor.Install.AdminAuthFlow.Shared;
        var kind = OfferNow(featureGated);
        if (kind == Progenitor.Install.AdminAuthOfferKind.None)
        {
            if (featureGated && flow.State == Progenitor.Install.AdminAuthState.Done)
                handles?.Add(panel.Note(flow.Message));
            return;
        }

        if (kind == Progenitor.Install.AdminAuthOfferKind.Button)
        {
            var button = panel.Button(Progenitor.Install.AdminAuthFlow.ButtonCaption);
            var note = panel.Note(flow.State is Progenitor.Install.AdminAuthState.Authenticating or Progenitor.Install.AdminAuthState.Failed
                ? flow.Message
                : "");
            button.OnClick(() =>
            {
                Progenitor.Install.AdminAuthFlow.Shared.Begin();
                try { note.Text = Progenitor.Install.AdminAuthFlow.Shared.Message; }
                catch { /* the panel was rebuilt */ }
            });
            handles?.Add(button);
            handles?.Add(note);
            return;
        }

        handles?.Add(panel.Note(Progenitor.Install.AdminAuthFlow.HelpText(kind)));
    }
}

internal sealed class ConfigurationPane
{
    // Three homes (decided 2026-10-06): the Preferences tab's four groups, in order.

    /// <summary>
    /// How the Mods tab's mod pages behave: <b>Edit other mods' settings</b>. Until the 2026-10-06 trim this was
    /// <b>Look and navigation</b>; its four other rows moved to the cfg file only (user: "No user will care to change
    /// those"), and the one row left is about the Mods tab, as the next group's is about the Install Mods tab.
    /// </summary>
    public const string ModsTabGroup = "Mods tab";

    /// <summary>How the Install Mods tab shows its list.</summary>
    public const string InstallTabGroup = "Install Mods tab";

    /// <summary>Where mods come from and how much is downloaded.</summary>
    public const string SourcesGroup = "Sources and downloads";

    /// <summary>AUTOUPDATE1: how this PC hears of mod updates (decisions of 2026-10-09).</summary>
    public const string UpdatesGroup = "Updates";

    public const string UpdateModsLabel = "Update mods";

    public const string UpdateModsHelp =
        "What Progenitor does when a mod it installed has a newer version on Thunderstore. Automatic updates it at the title "
        + "screen (never in a world or while joining): a hot-loadable mod loads at once, the rest at the next start, and the "
        + "previous version is kept so you can roll back on the mod's page. Notify only shows the updates (a line under MODS, "
        + "a list on the Install Mods tab, an offer before you join a server). Off shows nothing; each listing's Update stays. "
        + "A mod's own page can choose differently. Personal to this PC.";

    public const string MajorVersionsLabel = "Major versions";

    public const string MajorVersionsHelp =
        "A major version (2.0.0 after 1.4.2) can change how a mod works. Notify (the default) shows it and waits for your "
        + "click even when updates are Automatic; Automatic applies it like any other update. Mods only: Progenitor itself "
        + "uses date versions and follows Update Progenitor instead. Personal to this PC.";

    public const string PromptBeforeJoinLabel = "Offer updates before joining a server";

    public const string PromptBeforeJoinHelp =
        "When you join a server and a mod you have has an update that server accepts, ask Update first or Join anyway. "
        + "Never offers a version past the one the server requires. Personal to this PC.";

    public const string UpdateProgenitorLabel = "Update Progenitor";

    public const string UpdateProgenitorHelp =
        "What Progenitor does when a newer Progenitor is on Thunderstore. Automatic installs it at the title screen: a new Core "
        + "loads at once, a new Bootstrap at the next start, and if it does not start the previous copy comes back by itself. "
        + "Notify only shows it (a line under MODS and the Install Mods tab). Off shows nothing. A Progenitor a mod manager "
        + "installed, a development build and a release that changes a protocol only notify. Its versions are dates, so "
        + "Major versions does not apply to it. Personal to this PC.";

    /// <summary>BROWSERFILTER1: the game's Join Game server browser (the Progenitor servers filter).</summary>
    public const string JoinGameGroup = "Join Game";

    /// <summary>BROWSERFILTER1: the Join Game group's switch.</summary>
    public const string ProgenitorServersLabel = "Show only Progenitor servers";

    public const string ProgenitorServersHelp =
        "Join Game lists only servers running Progenitor. The browser's own Show Only › Progenitor Servers checkbox is the "
        + "same switch. A server counts when it publishes the \"progenitor\" server-list tag, which every Progenitor server "
        + "does unless its operator turned it off. It works with the game's own filters, search and refresh. Personal to this PC.";

    /// <summary>The local dedicated server folder and its process.</summary>
    public const string LocalServerGroup = "Local server";

    /// <summary>The Local server group's heading (the same words as its left-list item).</summary>
    public const string LocalDedicatedHeading = LocalServerGroup;

    /// <summary>
    /// User note 2026-09-12: what "server mods" mean without a dedicated server. Mods installed to
    /// "My client" are staged into the bundled server a private game spawns, and each plugin runs in
    /// the process its [BepInProcess] names — so a server-side mod installed to the client runs in
    /// the private games this player hosts (verified live 2026-09-12). A validated dedicated server
    /// is only a second install target.
    /// </summary>
    public const string PrivateGameNote =
        "No dedicated server needed for private games: mods you install to your client also load in the private games you host, server-side mods included. Configure a dedicated server here only to install to it as well.";

    public const string NexusNote =
        "Nexus Mods: not yet supported (needs an API key)";

    /// <summary>INSTALLSIDES1: the Sources switch that lists author-flagged NSFW packages.</summary>
    public const string ShowNsfwLabel = "Show mods with NSFW content";

    /// <summary>What the switch relies on, said where the player turns it on.</summary>
    public const string ShowNsfwHelp =
        "Lists packages whose authors flagged them as NSFW on Thunderstore. Off hides them from search and browsing. "
        + "It relies on the author's own flag, so a mod that is not flagged is never caught. A flagged mod you already "
        + "installed stays listed so you can see and remove it.";

    public const string ApplyFolderAction = "ApplyLocalServerFolder";
    public const string ClearFolderAction = "ClearLocalServerFolder";
    public const string UseThisFolder = "Use this folder";
    public const string FolderNotConfigured = "Not configured";
    /// <summary>CONF1: status line for a validated folder is <c>ValidatedPrefix + canonical path</c>.</summary>
    public const string ValidatedPrefix = "Validated: ";
    /// <summary>CONF1: status line for a refused path is <c>RefusedPrefix + reason + " Path: " + normalized</c>.</summary>
    public const string RefusedPrefix = "Refused: ";
    public const string FolderValidStatus = "Valid: VRisingServer.exe found";
    public const string LayoutAlignment = ConfigurationLayout.Alignment;

    /// <summary>SERVERINST1: toggle row under the folder status. Shown only for a validated folder.</summary>
    /// <summary>
    /// INSTALLUX1 item 2: the Install tab's single target decision, under this heading because it
    /// is the setting that answers "and what happens when there IS a local server?".
    /// </summary>
    public const string DefaultInstallTargetLabel = Progenitor.Ui.InstallModsCopy.DefaultTargetLabel;

    /// <inheritdoc cref="DefaultInstallTargetLabel"/>
    public const string DefaultInstallTargetHelp = Progenitor.Ui.InstallModsCopy.DefaultTargetHelp;

    public const string ServerInstallToggle = "Install Progenitor on this server";
    public const string ServerUpdateButton = "Update";
    public const string ServerRemoveButton = "Remove";
    public const string ServerCancelButton = "Cancel";
    public const string ServerRemoveHeading = "Remove Progenitor from this server?";

    /// <summary>SERVERINST3: stop/start rows for the validated folder's own server process.</summary>
    public const string StopServerButton = "Stop server";
    public const string StopServerForceButton = "Stop server (force)";
    public const string StartServerButton = "Start server";
    public const string StopPromptHeading = "Stop the server process?";
    public const string StopPromptStopButton = "Stop";
    public const string StopPromptCancelButton = "Cancel";

    /// <summary>
    /// ADMINLIST1: the "Add me" helper under the Local server rows. Strings live in
    /// <see cref="Progenitor.Install.AdminListFile"/> so they can change after the live check.
    /// </summary>
    public const string AdminListNote = Progenitor.Install.AdminListFile.NotOnListNote;
    public const string AdminListAddButton = Progenitor.Install.AdminListFile.AddMeButton;

    /// <summary>
    /// MODS2: remove Progenitor from this client. The button opens the same in-pane prompt
    /// SERVERINST2 uses; nothing is written until <see cref="ConfirmClientUninstall"/>, and nothing
    /// is deleted while the game runs — Bootstrap honors the marker at the next start.
    /// </summary>
    public const string UninstallClientButton = "Uninstall Progenitor from this client";
    public const string ClientRemoveHeading = Progenitor.Install.ClientUninstall.PromptHeading;
    public const string ClientRemoveButton = "Remove at next start";
    public const string ClientCancelButton = "Cancel";

    /// <summary>Shown under the button once a request is on disk.</summary>
    public const string ClientUninstallRequestedLine =
        "Progenitor removes itself at the next start of V Rising. Cancel to keep it.";
    public const string ClientUninstallCancelledLine = "Uninstall canceled; Progenitor stays installed.";
    public const string ClientUninstallRefusedLine = "The uninstall request could not be written.";

    /// <summary>
    /// Settings organization stage 3: the tab's sections. The Progenitor group (the raw <b>Progenitor settings</b>
    /// pane) left: its link row is the tab's first row, and everything about Progenitor itself is on its page.
    /// </summary>
    public static readonly string[] ConfigGroups = { ModsTabGroup, InstallTabGroup, SourcesGroup, UpdatesGroup, JoinGameGroup, LocalServerGroup };

    public static readonly string[] DetailSectionHeadings =
    {
        ModsTabGroup,
        InstallTabGroup,
        SourcesGroup,
        UpdatesGroup,
        JoinGameGroup,
        LocalDedicatedHeading,
    };

    private readonly ModsConfigBindings _bindings;
    private readonly LocalServerFolder _folder;
    private readonly object _owner;
    private readonly SteamLibraryScan _scan;
    private readonly List<IDisposable> _leases = new();
    private string _draftPath = "";
    private string _status = "";
    private Action? _openMods;
    private Action? _openServer;
    private Action? _rebuild;
    private IReadOnlyList<string> _candidates = Array.Empty<string>();
    private IReadOnlyList<ServerFolderSuggestion> _suggestions = Array.Empty<ServerFolderSuggestion>();
    private ServerInstallSource? _source;
    private ServerInstallStatus? _serverStatus;
    private string _serverLine = "";
    private readonly List<object> _serverHandles = new();

    /// <summary>MODS2: the client-uninstall button, note and prompt rows, for the readback.</summary>
    private readonly List<object> _clientHandles = new();

    /// <summary>
    /// SERVERINST2: the pending uninstall prompt, keyed by the validated folder's canonical
    /// path. <c>ModsMenu.BuildConfigurationPanes</c> discards this pane and constructs a new
    /// one on every recompose, including the one <see cref="RequestUninstall"/> asks for, so
    /// an instance field would be lost before the prompt rows were ever built. The entry is
    /// cleared by Cancel, Remove, Clear, a folder whose ledger is gone, or
    /// <see cref="ResetServerInstallStateForTests"/>.
    /// </summary>
    private static readonly HashSet<string> PendingUninstallPrompts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SERVERINST3: the pending force-stop prompt, keyed and survived the same way.</summary>
    private static readonly HashSet<string> PendingForceStopPrompts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PromptGate = new();

    /// <summary>
    /// MODS2: is the client uninstall prompt up? Static for the same reason the server prompt is —
    /// the host discards this pane and builds a new one on every recompose, including the one
    /// <see cref="RequestClientUninstall"/> asks for. There is one client, so it needs no key.
    /// </summary>
    private static bool _clientPromptOpen;

    /// <summary>MODS2: the last client-uninstall line shown under the button.</summary>
    private static string _clientLine = "";

    /// <summary>
    /// SERVERINST3: last stop outcome per validated folder. Static for the same reason the prompt
    /// is: the host discards this pane and builds a new one on every recompose, so an instance
    /// field would be gone before the rows that report it were built.
    /// </summary>
    private static readonly Dictionary<string, ServerStopOutcome> StopOutcomes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SERVERINST4: every state the last stop for that folder passed through, in order. The outcome
    /// alone cannot show <c>requested → replied → saving → stopped</c>, and those four are exactly
    /// what tells an operator that the server answered and saved rather than that it merely went
    /// away.
    /// </summary>
    private static readonly Dictionary<string, List<string>> StopStateTrails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Install source used when the constructor is not handed one and
    /// <see cref="ServerInstallSource.Detect"/> has nothing (tests, a Core without Bootstrap).
    /// The menu host constructs the pane without a source.
    /// </summary>
    internal static ServerInstallSource? DefaultSource { get; set; }

    /// <summary>
    /// Test seam: the discovery scan for a pane the menu host constructs without one, so a
    /// workspace-level test can drive suggestions through the real recompose path.
    /// </summary>
    internal static Func<SteamLibraryScan>? DefaultScanFactory { get; set; }

    internal static void ResetServerInstallStateForTests()
    {
        lock (PromptGate)
        {
            PendingUninstallPrompts.Clear();
            PendingForceStopPrompts.Clear();
            StopOutcomes.Clear();
            StopStateTrails.Clear();
            AdminListLines.Clear();
            _clientPromptOpen = false;
            _clientLine = "";
            _otherServersOpenFor = null;
            _advancedOpen = false;
        }

        DefaultSource = null;
        DefaultScanFactory = null;
        ClientRootProbe = null;
        Progenitor.Install.AdminListFile.ResetForTests();
    }

    // ---- MODS2: uninstall Progenitor from this client ----

    /// <summary>
    /// The client BepInEx root the uninstall marker is written into. Test seam; production reads
    /// <c>BepInEx.Paths.BepInExRootPath</c>.
    /// </summary>
    internal static Func<string?>? ClientRootProbe { get; set; }

    private static string? ClientRoot
    {
        get
        {
            var probe = ClientRootProbe;
            if (probe != null)
            {
                try { return probe(); }
                catch { return null; }
            }

#if PROGENITOR_TESTS
            return null;
#else
            try { return BepInEx.Paths.BepInExRootPath; }
            catch { return null; }
#endif
        }
    }

    /// <summary>Is the client uninstall prompt up?</summary>
    public bool ClientUninstallPromptOpen
    {
        get { lock (PromptGate) return _clientPromptOpen; }
    }

    /// <summary>The line under the button: the request, the cancel, or a refusal.</summary>
    public string ClientUninstallLine
    {
        get { lock (PromptGate) return _clientLine; }
    }

    private static void SetClientLine(string line)
    {
        lock (PromptGate) _clientLine = line ?? "";
    }

    /// <summary>The files a removal would delete, as the prompt and the readback list them.</summary>
    public static IReadOnlyList<string> ClientUninstallFiles()
    {
        string? root = ClientRoot;
        if (string.IsNullOrEmpty(root)) return Array.Empty<string>();
        IReadOnlyList<InstalledLedgerRow> ledger = Array.Empty<InstalledLedgerRow>();
        try { ledger = InstalledState.Scan(root, null).Ledger; }
        catch { /* the ledger is evidence; a missing one still plans the framework's own files */ }
        return Progenitor.Install.ClientUninstall.Plan(root, ledger);
    }

    /// <summary>The prompt body for the files this client would remove.</summary>
    public string ClientUninstallPromptText => Progenitor.Install.ClientUninstall.PromptText(ClientUninstallFiles());

    /// <summary>Open the prompt. Nothing is written until <see cref="ConfirmClientUninstall"/>.</summary>
    public SettingActionResult RequestClientUninstall()
    {
        lock (PromptGate) _clientPromptOpen = true;
        RequestRebuild();
        return SettingActionResult.Ok(ClientUninstallPromptText);
    }

    /// <summary>Cancel: the prompt closes and any marker already on disk is deleted.</summary>
    public SettingActionResult CancelClientUninstall()
    {
        lock (PromptGate) _clientPromptOpen = false;
        Progenitor.Install.ClientUninstall.Cancel(ClientRoot);
        SetClientLine(ClientUninstallCancelledLine);
        RequestRebuild();
        return SettingActionResult.Ok(ClientUninstallCancelledLine);
    }

    /// <summary>
    /// Write the request. Nothing is deleted here: the files are the ones this process is running
    /// from, so Bootstrap removes them at the next start, before Core loads.
    /// </summary>
    public SettingActionResult ConfirmClientUninstall()
    {
        lock (PromptGate) _clientPromptOpen = false;
        var result = WriteClientUninstall();
        RequestRebuild();
        return result;
    }

    /// <summary>
    /// Settings organization stage 4: the request, as Progenitor's page's <b>Uninstall</b> writes it after its
    /// confirmation (the same request and the same line as the former Configuration-tab prompt).
    /// </summary>
    internal static SettingActionResult WriteClientUninstall()
    {
        bool ok = Progenitor.Install.ClientUninstall.TryWrite(ClientRoot, ClientUninstallFiles());
        SetClientLine(ok ? ClientUninstallRequestedLine : ClientUninstallRefusedLine);
        return ok
            ? SettingActionResult.Ok(ClientUninstallRequestedLine)
            : SettingActionResult.Refuse(ClientUninstallRefusedLine);
    }

    /// <summary>Settings organization stage 4: cancel a request on disk (Progenitor's page's <b>Keep Progenitor</b>).</summary>
    internal static SettingActionResult CancelClientUninstallRequest()
    {
        lock (PromptGate) _clientPromptOpen = false;
        Progenitor.Install.ClientUninstall.Cancel(ClientRoot);
        SetClientLine(ClientUninstallCancelledLine);
        return SettingActionResult.Ok(ClientUninstallCancelledLine);
    }

    /// <summary>True while a removal request for the next start is on disk.</summary>
    internal static bool ClientUninstallRequested
    {
        get
        {
            try { return Progenitor.Install.ClientUninstall.TryRead(ClientRoot) != null; }
            catch { return false; }
        }
    }

    /// <summary><c>mods_ui</c> readback: <c>progenitorUninstall {requested, requestedUtc, files}</c>.</summary>
    public Dictionary<string, object?> ClientUninstallReadback() => ClientUninstallState();

    /// <summary>The <c>progenitorUninstall</c> readback, for the Configuration tab and Progenitor's page alike.</summary>
    internal static Dictionary<string, object?> ClientUninstallState()
    {
        var request = Progenitor.Install.ClientUninstall.TryRead(ClientRoot);
        bool promptOpen;
        string line;
        lock (PromptGate)
        {
            promptOpen = _clientPromptOpen;
            line = _clientLine;
        }

        return new Dictionary<string, object?>
        {
            ["requested"] = request != null,
            ["requestedUtc"] = request == null
                ? ""
                : request.RequestedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["files"] = request?.Files ?? (IReadOnlyList<string>)Array.Empty<string>(),
            ["promptOpen"] = promptOpen,
            ["line"] = line,
        };
    }

    public ConfigurationPane(
        ModsConfigBindings bindings,
        LocalServerFolder folder,
        object owner,
        SteamLibraryScan? scan = null,
        ServerInstallSource? source = null)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _scan = scan ?? DefaultScanFactory?.Invoke() ?? new SteamLibraryScan();
        _source = source ?? DefaultSource ?? ServerInstallSource.Detect();
        _draftPath = LocalServerFolder.NormalizeTyped(bindings.DedicatedServerPath.Value);
        // CONF1: a config-provided path is validated here, not only on Apply, so opening the
        // pane shows its state. A folder the host already validated is not re-probed.
        if (_draftPath.Length > 0 && !_folder.HasValidatedServer && !_folder.IsUnavailable)
        {
            var configured = _folder.Apply(_draftPath);
            _status = configured.Ok
                ? ValidatedStatus(configured.CanonicalPath ?? _draftPath)
                : RefusedStatus(configured.Refusal, _draftPath);
            if (configured.Ok) _draftPath = configured.CanonicalPath ?? _draftPath;
        }
        else
        {
            _status = StatusFromFolder();
        }
    }

    public static string ValidatedStatus(string canonicalPath) => ValidatedPrefix + canonicalPath;

    public static string RefusedStatus(string? refusal, string normalizedPath)
    {
        string reason = string.IsNullOrWhiteSpace(refusal) ? LocalServerFolder.RefusalEmpty : refusal!;
        return string.IsNullOrEmpty(normalizedPath)
            ? RefusedPrefix + reason
            : RefusedPrefix + reason + " Path: " + normalizedPath;
    }

    public LocalServerFolder Folder => _folder;

    public string StatusLine => _status;

    public string DraftPath => _draftPath;

    /// <summary>Suggest-only discovery hits shown on this pane. Never auto-applied.</summary>
    public IReadOnlyList<string> DiscoveredFolders => _candidates;

    /// <summary>The suggestions with their source and contents.</summary>
    internal IReadOnlyList<ServerFolderSuggestion> DiscoveredSuggestions => _suggestions;

    /// <summary>What discovery found about the configured folder itself (its chips go on its status line), or null.</summary>
    internal ServerFolderSuggestion? ConfiguredFound { get; private set; }

    /// <summary>
    /// The "Other servers found (N)" list is expanded (only meaningful while a folder is configured).
    /// Static and keyed by the configured canonical path for the same reason the SERVERINST2 prompt
    /// is: <c>ModsMenu.BuildConfigurationPanes</c> constructs a new pane on every recompose, including
    /// the one the header's click asks for, so an instance field was lost before the rows were built
    /// (live, 50381e5d). A different configured path reads as collapsed; closing the workspace clears it.
    /// </summary>
    private static string? _otherServersOpenFor;

    internal bool OtherServersOpen
    {
        get
        {
            string? key = _folder.HasValidatedServer ? _folder.Current.CanonicalPath : null;
            if (key == null) return false;
            lock (PromptGate) return string.Equals(_otherServersOpenFor, key, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            string? key = _folder.HasValidatedServer ? _folder.Current.CanonicalPath : null;
            lock (PromptGate) _otherServersOpenFor = value ? key : null;
        }
    }

    /// <summary>The workspace closed: the other-servers list starts collapsed on the next open.</summary>
    internal static void ResetOtherServersOpen()
    {
        lock (PromptGate)
        {
            _otherServersOpenFor = null;
            // Settings organization: the Sources Advanced fold starts closed on the next open too.
            _advancedOpen = false;
        }
    }

    /// <summary>The row name of the collapsed list's header.</summary>
    public static string OtherServersHeader(int count) => "Other servers found (" + count + ")";

    public const string ShowCaption = "Show";
    public const string HideCaption = "Hide";

    /// <summary>
    /// Live polish: the configured folder is never offered as a suggestion; what discovery knows about
    /// it (running now, BepInEx) is shown on its own status line instead.
    /// </summary>
    internal void SplitSuggestions(IReadOnlyList<ServerFolderSuggestion> found)
    {
        string? configured = _folder.HasValidatedServer ? _folder.Current.CanonicalPath : null;
        var rest = new List<ServerFolderSuggestion>(found.Count);
        ConfiguredFound = null;
        foreach (var s in found)
        {
            if (configured != null && SamePath(s.Path, configured)) { ConfiguredFound = s; continue; }
            rest.Add(s);
        }

        _suggestions = rest;
        var paths = new List<string>(rest.Count);
        foreach (var s in rest) paths.Add(s.Path);
        _candidates = paths;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>The configured folder's status line, with discovery's chips when it found that folder.</summary>
    internal string StatusLineWithChips()
    {
        var found = ConfiguredFound;
        if (found == null || !_folder.HasValidatedServer) return _status;
        string chips = (found.Running ? " · " + SteamLibraryScan.SourceRunning : "")
            + " · " + (found.HasBepInEx ? "BepInEx installed" : "no BepInEx yet")
            + (found.HasLaunchRecord ? " · started by Progenitor before" : "");
        return _status + chips;
    }

    /// <summary>A long path for a row name: the drive and the last folders kept, the middle elided.</summary>
    public static string MiddleEllipsis(string? path, int max = 48)
    {
        string p = path ?? "";
        if (p.Length <= max || max < 9) return p;
        int head = (max - 1) / 2;
        int tail = max - 1 - head;
        return p.Substring(0, head) + "…" + p.Substring(p.Length - tail);
    }

    /// <summary>The prominent one-candidate prompt: no folder configured and exactly one valid suggestion.</summary>
    internal ServerFolderSuggestion? SingleSuggestion =>
        !_folder.HasValidatedServer && _suggestions.Count == 1 ? _suggestions[0] : null;

    public static string FoundSingleLine(string path) => "Found a dedicated server at " + path + " — " + UseThisFolder;

    /// <summary>What choosing a found folder means, said before the click.</summary>
    public static string FoundConsequence(string path) =>
        "Choosing it makes " + path + " the local server: installs to the server and Stop server act on that folder. Nothing is chosen until you press " + UseThisFolder + ".";

    public static ModsConfigBindings Bind(ConfigFile file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        return new ModsConfigBindings(
            ConfigHelper.Bind(
                file,
                ModsConfigKeys.Section,
                ModsConfigKeys.UseThunderstore,
                true,
                "Browse Thunderstore listings on the Install mods tab (Progenitor Index listings are shown either way). Personal to this BepInEx profile."),
            ConfigHelper.Bind(
                file,
                ModsConfigKeys.Section,
                ModsConfigKeys.UseNexus,
                false,
                NexusNote + " Personal to this BepInEx profile."),
            ConfigHelper.Bind(
                file,
                ModsConfigKeys.Section,
                ModsConfigKeys.NexusApiKey,
                "",
                "Nexus API key. Personal credential; the UI shows configured or not set, never the value in logs."),
            ConfigHelper.Bind(
                file,
                ModsConfigKeys.Section,
                ModsConfigKeys.DedicatedServerPath,
                "",
                "Same-machine dedicated-server folder. This computer, this game install. Used when not connected, and as the fallback while connected; a Progenitor server joined on this PC is detected automatically. Empty means no configured local server."),
            ConfigHelper.Bind(
                file,
                ModsConfigKeys.Section,
                ModsConfigKeys.IndexPath,
                "",
                "Optional override of the pre-fetched package index path. Empty uses BepInEx/config/progenitor-index.json."),
            ConfigHelper.Bind(
                file,
                ModsConfigKeys.Section,
                ModsConfigKeys.ShowNsfwContent,
                false,
                ShowNsfwHelp + " Personal to this BepInEx profile."))
        {
            InstallView = ConfigHelper.Bind(
                file,
                ModsConfigKeys.Section,
                InstallViewSetting.ConfigKey,
                InstallViewSetting.SimpleName,
                InstallViewSetting.Description),
            RememberInstallFilters = ConfigHelper.Bind(
                file, ModsConfigKeys.Section, ModsConfigKeys.RememberInstallFilters, false, ModsConfigKeys.RememberInstallFiltersDescription),
            InstallFiltersRemembered = ConfigHelper.Bind(
                file, ModsConfigKeys.Section, ModsConfigKeys.InstallFiltersRemembered, "", ModsConfigKeys.InstallFiltersRememberedDescription),
            BackgroundIntensity = ConfigHelper.Bind(
                file, WorkspaceArtworkIntensity.Section, WorkspaceArtworkIntensity.Key, WorkspaceArtworkIntensity.Default,
                WorkspaceArtworkIntensity.Description),
            BackgroundContrast = ConfigHelper.Bind(
                file, WorkspaceArtworkIntensity.Section, WorkspaceArtworkIntensity.ContrastKey, WorkspaceArtworkIntensity.DefaultContrast,
                WorkspaceArtworkIntensity.ContrastDescription),
            CanonicalInGameFrame = ConfigHelper.Bind(
                file, WorkspaceArtworkIntensity.Section, WorkspaceHostLayout.PinKey, true, WorkspaceHostLayout.PinDescription),
            AdminAuthButton = ConfigHelper.Bind(
                file, Progenitor.Install.AdminAuthFlow.ConfigSection, Progenitor.Install.AdminAuthFlow.ConfigKey, true,
                Progenitor.Install.AdminAuthFlow.ConfigDescription),
            // The file's own entries (the same ones BindMaxDownloadMb / BindCurrentUpdateTag hand out on the shared file),
            // bound directly so a pane is never handed another file's entry those helpers cached first.
            MaxDownloadMb = ConfigHelper.Bind(
                file, ConfigHelper.InstallSection, ConfigHelper.MaxDownloadMbKey, ConfigHelper.MaxDownloadMbDefault,
                ConfigHelper.MaxDownloadMbDescription),
            CurrentUpdateTag = ConfigHelper.Bind(
                file, ConfigHelper.InstallSection, ConfigHelper.CurrentUpdateTagKey, "", ConfigHelper.CurrentUpdateTagDescription),
            AutoUpdateMods = ConfigHelper.Bind(
                file, Progenitor.Install.UpdateSettings.Section, Progenitor.Install.UpdateSettings.ModsKey,
                Progenitor.Install.UpdateSettings.DefaultMode,
                "Automatic, Notify or Off. " + UpdateModsHelp),
            AutoUpdatePromptBeforeJoin = ConfigHelper.Bind(
                file, Progenitor.Install.UpdateSettings.Section, Progenitor.Install.UpdateSettings.PromptKey,
                Progenitor.Install.UpdateSettings.DefaultPrompt, PromptBeforeJoinHelp),
            AutoUpdateMajorVersions = ConfigHelper.Bind(
                file, Progenitor.Install.UpdateSettings.Section, Progenitor.Install.UpdateSettings.MajorKey,
                Progenitor.Install.UpdateSettings.DefaultMajor,
                "Automatic or Notify. " + MajorVersionsHelp),
            AutoUpdateProgenitor = ConfigHelper.Bind(
                file, Progenitor.Install.SelfUpdateSettings.Section, Progenitor.Install.SelfUpdateSettings.ProgenitorKey,
                Progenitor.Install.SelfUpdateSettings.DefaultHere,
                "Automatic, Notify or Off. " + Progenitor.Install.SelfUpdateSettings.Help),
        };
    }

    public static string NexusKeyDisplay(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "not set" : "configured";

    /// <summary>Three homes: what the purpose line's <b>Mods »</b> and <b>Server »</b> open (the Mods menu's tab paths).</summary>
    public void SetLinks(Action? openMods, Action? openServer)
    {
        _openMods = openMods;
        _openServer = openServer;
    }

    /// <summary>Recompose the content panel after a server install/uninstall or prompt change. The host defers it past the current dispatch.</summary>
    public void SetRebuild(Action? rebuild) => _rebuild = rebuild;

    public void SetServerInstallSource(ServerInstallSource? source) => _source = source;

    /// <summary>Last inspected server-install state; <see cref="RefreshServerInstall"/> re-reads the folder.</summary>
    public ServerInstallStatus? ServerInstall => _serverStatus;

    /// <summary>Status under the toggle: installed/not installed/update/refusal or the last action's message.</summary>
    public string ServerInstallLine => _serverLine;

    public bool UninstallPromptOpen => PromptOpen;

    private string? PromptKey => _folder.HasValidatedServer ? _folder.Current.CanonicalPath : null;

    private bool PromptOpen
    {
        get
        {
            string? key = PromptKey;
            if (key == null) return false;
            lock (PromptGate) return PendingUninstallPrompts.Contains(key);
        }
        set => SetPrompt(PromptKey, value);
    }

    private static void SetPrompt(string? key, bool open)
    {
        if (key == null) return;
        lock (PromptGate)
        {
            if (open) PendingUninstallPrompts.Add(key);
            else PendingUninstallPrompts.Remove(key);
        }
    }

    /// <summary>The uninstall confirmation copy for the current ledger.</summary>
    public string UninstallPromptText => Progenitor.Install.ServerInstall.PromptText(_serverStatus?.Ledger);

    public ServerInstallStatus RefreshServerInstall()
    {
        _serverStatus = _folder.HasValidatedServer
            ? Progenitor.Install.ServerInstall.Inspect(_folder.Current.CanonicalPath, _source)
            : new ServerInstallStatus(ServerInstallState.Refused, "", Progenitor.Install.ServerInstall.RefusalNoFolder, null, "", "", "", "");
        _serverLine = DefaultServerLine(_serverStatus);
        return _serverStatus;
    }

    /// <summary>Copy this client's Bootstrap and Core into the validated folder and write the ledger. Never starts a process.</summary>
    public SettingActionResult InstallProgenitorOnServer()
    {
        var status = RefreshServerInstall();
        if (status.State == ServerInstallState.Refused)
        {
            _serverLine = status.Reason;
            RequestRebuild();
            return SettingActionResult.Refuse(_serverLine);
        }

        var result = Progenitor.Install.ServerInstall.Install(_folder.Current.CanonicalPath, _source);
        RefreshServerInstall();
        _serverLine = result.Message;
        RequestRebuild();
        return result.Ok ? SettingActionResult.Ok(result.Message) : SettingActionResult.Refuse(result.Message);
    }

    /// <summary>Toggle off: open the warning prompt. Nothing is removed until <see cref="ConfirmUninstall"/>.</summary>
    public SettingActionResult RequestUninstall()
    {
        var status = RefreshServerInstall();
        if (status.State == ServerInstallState.Refused)
        {
            _serverLine = status.Reason;
            RequestRebuild();
            return SettingActionResult.Refuse(_serverLine);
        }

        if (status.Ledger == null)
        {
            _serverLine = Progenitor.Install.ServerInstall.NotInstalledStatus;
            RequestRebuild();
            return SettingActionResult.Ok(_serverLine);
        }

        PromptOpen = true;
        RequestRebuild();
        return SettingActionResult.Ok(UninstallPromptText);
    }

    public SettingActionResult CancelUninstall()
    {
        PromptOpen = false;
        RefreshServerInstall();
        RequestRebuild();
        return SettingActionResult.Ok(_serverLine);
    }

    /// <summary>Remove: ledger mod and pending files, then framework files, empty Progenitor folders, the ledger. Refuses while the server runs.</summary>
    public SettingActionResult ConfirmUninstall()
    {
        PromptOpen = false;
        var result = Progenitor.Install.ServerInstall.Uninstall(_folder.HasValidatedServer ? _folder.Current.CanonicalPath : null);
        RefreshServerInstall();
        _serverLine = result.Message;
        RequestRebuild();
        return result.Ok ? SettingActionResult.Ok(result.Message) : SettingActionResult.Refuse(result.Message);
    }

    // ---- SERVERINST3: stop and start the validated folder's own server ----

    private string? StopKey => _folder.HasValidatedServer ? _folder.Current.CanonicalPath : null;

    public bool ForceStopPromptOpen
    {
        get
        {
            string? key = StopKey;
            if (key == null) return false;
            lock (PromptGate) return PendingForceStopPrompts.Contains(key);
        }
    }

    private void SetForcePrompt(bool open)
    {
        string? key = StopKey;
        if (key == null) return;
        lock (PromptGate)
        {
            if (open) PendingForceStopPrompts.Add(key);
            else PendingForceStopPrompts.Remove(key);
        }
    }

    private ServerStopOutcome StopOutcome
    {
        get
        {
            string? key = StopKey;
            if (key == null) return new ServerStopOutcome(ServerStopState.Idle, "");
            lock (PromptGate)
                return StopOutcomes.TryGetValue(key, out var outcome) ? outcome : new ServerStopOutcome(ServerStopState.Idle, "");
        }
        set
        {
            string? key = StopKey;
            if (key == null) return;
            lock (PromptGate) StopOutcomes[key] = value;
        }
    }

    private List<string> StopStateTrail
    {
        get
        {
            string? key = StopKey;
            if (key == null) return new List<string>();
            lock (PromptGate)
                return StopStateTrails.TryGetValue(key, out var trail) ? new List<string>(trail) : new List<string>();
        }
        set
        {
            string? key = StopKey;
            if (key == null) return;
            lock (PromptGate) StopStateTrails[key] = value;
        }
    }

    /// <summary>The force-stop confirmation copy for the validated folder.</summary>
    public string ForceStopPromptText => ServerProcessControl.ForcePromptBody(StopKey ?? "");

    /// <summary>The running server of the validated folder, or null. Read-only probe.</summary>
    public ServerProcessInfo? RunningServer => ServerProcessControl.Running(StopKey);

    /// <summary>Graceful stop: the server saves and quits itself. Silence or no route opens the force prompt.</summary>
    public SettingActionResult StopServer()
    {
        if (!_folder.HasValidatedServer)
            return SettingActionResult.Refuse(Progenitor.Install.ServerInstall.RefusalNoFolder);
        var trail = new List<string>();
        var previous = ServerProcessControl.StopProgress;
        ServerProcessControl.StopProgress = state => trail.Add(ServerProcessControl.StopStateName(state));
        ServerStopOutcome outcome;
        try { outcome = ServerProcessControl.Stop(StopKey); }
        finally { ServerProcessControl.StopProgress = previous; }
        StopStateTrail = trail;
        StopOutcome = outcome;
        SetForcePrompt(outcome.State == ServerStopState.ForcePrompt);
        _serverLine = outcome.Reason;
        RefreshServerInstall();
        if (outcome.State != ServerStopState.Stopped) _serverLine = outcome.Reason;
        RequestRebuild();
        return outcome.State == ServerStopState.Stopped
            ? SettingActionResult.Ok(_serverLine)
            : SettingActionResult.Refuse(_serverLine);
    }

    /// <summary>Open the force prompt without sending anything.</summary>
    public SettingActionResult RequestForceStop()
    {
        if (!_folder.HasValidatedServer)
            return SettingActionResult.Refuse(Progenitor.Install.ServerInstall.RefusalNoFolder);
        StopOutcome = new ServerStopOutcome(ServerStopState.ForcePrompt, StopOutcome.Reason);
        SetForcePrompt(true);
        RequestRebuild();
        return SettingActionResult.Ok(ForceStopPromptText);
    }

    public SettingActionResult CancelForceStop()
    {
        SetForcePrompt(false);
        StopOutcome = new ServerStopOutcome(ServerStopState.Idle, "");
        RefreshServerInstall();
        RequestRebuild();
        return SettingActionResult.Ok(_serverLine);
    }

    /// <summary>Confirmed force stop: <c>Kill</c> on the process matched by full path inside the folder.</summary>
    public SettingActionResult ConfirmForceStop()
    {
        SetForcePrompt(false);
        var outcome = ServerProcessControl.ForceStop(StopKey);
        StopOutcome = outcome;
        RefreshServerInstall();
        if (outcome.State != ServerStopState.Stopped) _serverLine = outcome.Reason;
        RequestRebuild();
        return outcome.State == ServerStopState.Stopped
            ? SettingActionResult.Ok(_serverLine)
            : SettingActionResult.Refuse(outcome.Reason);
    }

    /// <summary>Start the folder's server from its launch record. Absent record: the unavailable line, nothing started.</summary>
    public SettingActionResult StartServer()
    {
        if (!_folder.HasValidatedServer)
            return SettingActionResult.Refuse(Progenitor.Install.ServerInstall.RefusalNoFolder);
        var outcome = ServerProcessControl.Start(StopKey);
        _serverLine = outcome.Line;
        if (outcome.Ok) StopOutcome = new ServerStopOutcome(ServerStopState.Idle, "");
        RequestRebuild();
        return outcome.Ok ? SettingActionResult.Ok(outcome.Line) : SettingActionResult.Refuse(outcome.Line);
    }

    /// <summary>True for the toggle, its status note, the Update button and the prompt widgets this pane built.</summary>
    public bool IsServerInstallHandle(object handle)
    {
        for (int i = 0; i < _serverHandles.Count; i++)
        {
            if (ReferenceEquals(_serverHandles[i], handle)) return true;
        }

        return false;
    }

    /// <summary><c>mods_ui</c> readback: <c>serverInstall {state, version, reason, ledgerFiles, promptOpen, line}</c>.</summary>
    public Dictionary<string, object?> ServerInstallReadback()
    {
        var status = _serverStatus ?? RefreshServerInstall();
        return new Dictionary<string, object?>
        {
            ["state"] = status.StateName,
            ["version"] = status.Version,
            ["reason"] = status.Reason,
            ["ledgerFiles"] = status.LedgerFiles,
            ["promptOpen"] = PromptOpen,
            ["line"] = _serverLine,
            ["process"] = ProcessReadback(),
            ["stop"] = StopReadback(),
        };
    }

    /// <summary>SERVERINST3: <c>process {running, pid, path, graceful, launchRecord}</c>.</summary>
    private Dictionary<string, object?> ProcessReadback()
    {
        string? key = StopKey;
        var running = ServerProcessControl.Running(key);
        bool graceful = key != null && ServerProcessControl.GracefulAvailable(key, out _);
        return new Dictionary<string, object?>
        {
            ["running"] = running != null,
            ["pid"] = running?.Pid ?? 0,
            ["path"] = running?.Path ?? "",
            ["graceful"] = graceful,
            ["launchRecord"] = ServerLaunchRecord.TryRead(key) != null,
            // SERVERINST4: the arguments the record would replay, without the image token (already
            // `path`), each token that contains whitespace re-quoted, capped at 512 characters.
            ["commandLine"] = ServerProcessControl.RedactCommandLine(running?.CommandLine),
            // SERVERINST5: the server this client actually joined, "<protocol>://<address>:<port>",
            // or "" when nothing was recorded. Reading it next to `graceful` is how a port mismatch
            // is diagnosed without guessing which side is wrong.
            ["connectedTo"] = ServerProcessControl.ConnectedTo(),
        };
    }

    /// <summary>SERVERINST3: <c>stop {state, reason}</c>. The force prompt being open is its own state.</summary>
    private Dictionary<string, object?> StopReadback()
    {
        var outcome = StopOutcome;
        var state = ForceStopPromptOpen ? ServerStopState.ForcePrompt : outcome.State;
        return new Dictionary<string, object?>
        {
            ["state"] = ServerProcessControl.StopStateName(state),
            ["reason"] = outcome.Reason,
            // SERVERINST4: the whole sequence, so `requested → replied → saving → stopped` is
            // readable after the fact and a `not-admin` / `save-timeout` refusal names itself.
            ["states"] = StopStateTrail,
        };
    }

    public IReadOnlyList<SettingsRow> BuildRows()
    {
        DisposeLeases();
        var rows = new List<SettingsRow>();

        var thunder = SettingsCatalog.RegisterSetting(
            SettingsPanel.PluginId,
            _bindings.UseThunderstore,
            new SettingPresentation
            {
                Label = "Use Thunderstore",
                Help = "",
                Group = SourcesGroup,
                Order = 10,
                EditorHint = SettingsEditorHint.Toggle,
                CommitPolicy = SettingCommitPolicy.Immediate,
            },
            _owner);
        _leases.Add(thunder);
        rows.Add(SettingsRow.ForEntry(SettingsPanel.PluginId, _bindings.UseThunderstore, synced: false, presentation: thunder.Presentation)
            .WithAlignment(LayoutAlignment));

        var nsfw = SettingsCatalog.RegisterSetting(
            SettingsPanel.PluginId,
            _bindings.ShowNsfwContent,
            new SettingPresentation
            {
                Label = ShowNsfwLabel,
                Help = ShowNsfwHelp,
                Group = SourcesGroup,
                Order = 20,
                EditorHint = SettingsEditorHint.Toggle,
                CommitPolicy = SettingCommitPolicy.Immediate,
            },
            _owner);
        _leases.Add(nsfw);
        rows.Add(SettingsRow.ForEntry(SettingsPanel.PluginId, _bindings.ShowNsfwContent, synced: false, presentation: nsfw.Presentation)
            .WithAlignment(LayoutAlignment));

        var path = SettingsCatalog.RegisterSetting(
            SettingsPanel.PluginId,
            _bindings.DedicatedServerPath,
            new SettingPresentation
            {
                Label = "Local dedicated-server folder",
                Help = "Same-machine folder. Apply validates it. A refused Apply keeps the previous valid configuration. Used when not connected; a Progenitor server you join on this PC is found automatically.",
                Group = LocalServerGroup,
                Order = 40,
                EditorHint = SettingsEditorHint.Text,
                CommitPolicy = SettingCommitPolicy.OnSubmit,
            },
            _owner);
        _leases.Add(path);
        rows.Add(SettingsRow.ForEntry(SettingsPanel.PluginId, _bindings.DedicatedServerPath, synced: false, presentation: path.Presentation)
            .WithAlignment(LayoutAlignment));

        _leases.Add(SettingsCatalog.RegisterAction(
            SettingsPanel.PluginId,
            ApplyFolderAction,
            _ => ApplyDraft(),
            new SettingActionSpec
            {
                Label = "Apply",
                Help = "",
                Group = LocalServerGroup,
                Order = 41,
            },
            _owner));
        rows.Add(SettingsRow.ForAction(ActionBinding(ApplyFolderAction)).WithAlignment(LayoutAlignment));

        _leases.Add(SettingsCatalog.RegisterAction(
            SettingsPanel.PluginId,
            ClearFolderAction,
            _ => ClearFolder(),
            new SettingActionSpec
            {
                Label = "Clear",
                Help = "",
                Group = LocalServerGroup,
                Order = 42,
            },
            _owner));
        rows.Add(SettingsRow.ForAction(ActionBinding(ClearFolderAction)).WithAlignment(LayoutAlignment));
        return rows;
    }

    /// <summary>
    /// MENUKIT2 round C: a Configuration section heading like the game's Options section header, with its
    /// red rule under it (the rest of the tab's controls are drawn on the settings grid).
    /// </summary>
    private static IUiLabel GameHeading(IUiPanel panel, string text)
    {
        var heading = panel.Heading(text);
        heading.SectionRule();
        return heading;
    }

    // ---- MENUKIT2 round D: labeled rows, game buttons, the description column -----------------------

    /// <summary>Row names of the tab's labeled button rows.</summary>
    internal const string LocalServerRowName = "Local server";

    /// <summary>UI polish: the short line for a running server; the full sentence is the hover description.</summary>
    internal const string ServerRunningLine = "Server running — stop it before installing or removing";
    internal const string ForceStopRowName = "Force stop";
    internal const string ServerProgenitorRowName = "Progenitor on this server";
    internal const string UninstallRowName = "Uninstall from this client";

    /// <summary>What the column says with nothing hovered.</summary>
    internal const string ColumnIntro = "Point at a setting to read what it does.";

    /// <summary>What each labeled row explains in the description column.</summary>
    internal const string ThunderstoreHelp = "List and install mods from Thunderstore. Mods from the Progenitor Index are listed either way. Nexus Mods is not supported yet (it needs an API key).";
    internal const string StopServerHelp = "Stop the dedicated server running from the configured folder, so mods can be installed to it or removed. Stop asks it to save and quit; force stop ends the process.";
    internal const string StartServerHelp = "Start the dedicated server from the configured folder with the settings it last ran with.";
    internal const string UninstallHelp = "Progenitor removes itself the next time V Rising starts; nothing is deleted while the game runs.";

    /// <summary>
    /// A settings-grid button row: <paramref name="name"/> on the left and a game button captioned
    /// <paramref name="caption"/> as its control (the Stop / Progenitor settings / Uninstall rows).
    /// </summary>
    private static IUiButton GridButton(IUiPanel panel, string name, string caption)
    {
        panel.SettingsGrid(true);
        try { return panel.NamedButton(name, caption); }
        finally { panel.SettingsGrid(false); }
    }

    /// <summary>Show <paramref name="body"/> in the description column while the pointer is on <paramref name="row"/>.</summary>
    private void Describe(IUiPanel panel, IUiHandle? row, string heading, string body)
    {
        if (row == null || panel is not UiPanelHandle) return;
        try
        {
            _leases.Add(panel.OnRowHover(row, entered =>
            {
                if (entered)
                    panel.ShowDescription(new ModSettingsDescription("", heading, body, null, "", ModSettingsDescription.SourceHover));
                else
                    panel.ShowDescription(ModSettingsDescription.Empty(ConfigurationTitle, ColumnIntro));
            }));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: configuration row description threw: " + e.Message); }
    }

    /// <summary>
    /// An explanatory paragraph that belongs in the description column: hidden on the workspace's panel
    /// (the row it explains shows it on hover), kept as a line anywhere else.
    /// </summary>
    private static void InColumn(IUiPanel panel, IUiHandle? note)
    {
        if (note == null || panel is not UiPanelHandle) return;
        try { note.Visible = false; }
        catch { /* the line stays */ }
    }

    /// <summary>Three homes: the tab's title (renamed from Configuration).</summary>
    internal const string PreferencesTitle = "Preferences";

    /// <summary>The description column's empty heading (the tab's name).</summary>
    internal const string ConfigurationTitle = PreferencesTitle;

    private IUiLabel? _titleHeading;
    private IUiHandle? _localServerHeading;

    /// <summary>Three homes: the Local server group's heading as drawn (the left list scrolls to it), or null.</summary>
    internal IUiHandle? LocalServerHeadingHandle => _localServerHeading;

    /// <summary>
    /// Three homes: the heading a left-list group scrolls to — a settings group's (drawer or subheading) or the Local
    /// server's. Null when it is not drawn.
    /// </summary>
    internal IUiHandle? GroupHeading(string? group)
    {
        if (string.Equals(group, LocalServerGroup, StringComparison.OrdinalIgnoreCase)) return _localServerHeading;
        return _settingsView?.HeadingFor(group);
    }

    /// <summary>The Local server group's heading: a gold small-caps subheading (four rows or fewer of its own kind).</summary>
    private IUiHandle? LocalServerHeading(IUiPanel panel)
    {
        try
        {
            panel.SettingsGrid(true);
            var row = panel.CellRow(ModSettingsScopes.PlainSubheadingRow(LocalServerHeadingIndex, LocalServerGroup, false));
            AddReadback(row, ModSettingsView.RowSection, new Dictionary<string, object?>
            {
                ["title"] = LocalServerGroup,
                ["scope"] = "personal",
                ["kind"] = "local-server",
                ["drawer"] = false,
            });
            return row;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Local server heading threw: " + e.Message);
            return null;
        }
        finally { panel.SettingsGrid(false); }
    }

    // ---- Three homes (live polish 2026-10-06): the Local server rows laid out like the other settings rows ----------

    internal const string ServerFolderLabel = "Server folder";
    internal const string ServerFolderHelp =
        "The dedicated server's folder (where VRisingServer.exe is). Apply checks it and uses it; Clear forgets it. Nothing is "
        + "installed until you turn on Install Progenitor on this server.";
    internal const string ApplyFolderCaption = "Apply";
    internal const string ClearFolderCaption = "Clear";
    internal const string StatusRowLabel = "Status";

    /// <summary>Cell-row keys and the readback key of the muted status rows.</summary>
    internal const string RowFolderStatus = "local-server-status";
    internal const string RowServerStatus = "local-server-progenitor";
    internal const string RowLocalStatus = "localServerStatus";
    internal const string CellStatusLabel = "label";
    internal const string CellStatusValue = "value";

    /// <summary>
    /// A muted info row of the Local server group: its name in the name column and the line beside it, cut with an ellipsis
    /// (the whole line is the hover description). Off the workspace's panel (a test's recording panel) it stays a note.
    /// </summary>
    private IUiHandle StatusRow(IUiPanel panel, string key, string label, string text, string full)
    {
        if (panel is not UiPanelHandle) return panel.Note(text);
        var row = GridCellRow(panel, StatusRowSpec(key, label, text));
        AddReadback(row, RowLocalStatus, new Dictionary<string, object?> { ["label"] = label, ["text"] = text });
        AddReadback(row, RowLabelColumn, (Func<object?>)(() => LabelColumnReadback(row, CellStatusLabel)));
        Describe(panel, row, label, string.IsNullOrEmpty(full) ? text : full);
        return row;
    }

    /// <summary>The muted info row: name, then the line in the muted tone.</summary>
    internal static CellRowSpec StatusRowSpec(string key, string label, string text) => new(
        key,
        new List<CellSpec>
        {
            GridNameLead(),
            new CellSpec(CellKind.Muted, CellStatusLabel).With(
                text: label ?? "", width: CellWidth.Px(StatusLabelWidth), minWidth: StatusLabelWidth, tone: CellTone.Muted,
                fontSize: Progenitor.Ui.GameArt.GameSettingsLook.NameText, vAlign: CellVAlign.Middle),
            new CellSpec(CellKind.Muted, CellStatusValue).With(
                text: text ?? "", width: CellWidth.Fill(160f), tone: CellTone.Muted, wrap: CellWrap.Ellipsis, vAlign: CellVAlign.Middle),
        },
        height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true,
        indent: 0f);

    internal const float StatusLabelWidth = 220f;

    /// <summary>TOKENTIPS2: one wrapped line of a wrapping status row (the muted text's size, at 1.3).</summary>
    internal const float StatusWrapLineHeight = 20f;

    /// <summary>PENDINGFIT1: the most lines a wrapping status row is fitted to (Pending changes: summary, 8 changes, +N more, warning, and room to wrap).</summary>
    internal const int StatusWrapMaxLines = 16;

    /// <summary>
    /// TOKENINSERT1: one row of a field's Insert buttons on the settings grid: the name column reads <c>Insert</c> on the
    /// first row (empty on the next ones), then key-cap buttons (<paramref name="captions"/>, cell keys
    /// <see cref="Progenitor.Networking.TextTokens.InsertCellKey"/> by token index).
    /// </summary>
    internal static CellRowSpec InsertRowSpec(string key, bool first, IReadOnlyList<int> indices, IReadOnlyList<string> captions)
    {
        var cells = new List<CellSpec>(indices.Count + 2)
        {
            GridNameLead(),
            new CellSpec(CellKind.Muted, CellStatusLabel).With(
                text: first ? Progenitor.Networking.TextTokens.InsertLabel : "", width: CellWidth.Px(StatusLabelWidth), minWidth: StatusLabelWidth,
                tone: CellTone.Muted, fontSize: Progenitor.Ui.GameArt.GameSettingsLook.NameText, vAlign: CellVAlign.Middle),
        };
        foreach (int i in indices)
        {
            string caption = captions[i];
            float w = Progenitor.Networking.TextTokens.InsertButtonWidth(caption);
            cells.Add(new CellSpec(CellKind.Button, Progenitor.Networking.TextTokens.InsertCellKey(i)).With(
                text: caption, width: CellWidth.Px(w), minWidth: w, fontSize: Progenitor.Networking.TextTokens.InsertFontSize, skin: CellSkin.KeyCap));
        }

        cells.Add(new CellSpec(CellKind.Spacer, "insert-rest"));
        return new CellRowSpec(key, cells, height: Progenitor.Networking.TextTokens.InsertRowHeight, rightInset: HeadBlockLayout.RightColumnInset,
            plain: true, indent: 0f);
    }

    /// <summary>
    /// TOKENINSERT1: the Players see line under a field: the rendered text in a wrapping cell of a fixed
    /// <see cref="Progenitor.Networking.TextTokens.PreviewLines"/> lines (an ellipsis past them; it changes while the
    /// admin types, so it is not refitted), its links drawn as players see them when <paramref name="links"/>.
    /// </summary>
    internal static CellRowSpec PreviewRowSpec(string key, string text, bool links)
    {
        var value = new CellSpec(CellKind.Text, CellStatusValue).With(
            text: text ?? "", width: CellWidth.Fill(160f), tone: CellTone.Normal, wrap: CellWrap.Wrap, vAlign: CellVAlign.Top,
            fontSize: PreviewFontSize);
        value.Linkify = links;
        return new CellRowSpec(
            key,
            new List<CellSpec>
            {
                GridNameLead(),
                new CellSpec(CellKind.Muted, CellStatusLabel).With(
                    text: Progenitor.Networking.TextTokens.PreviewLabel, width: CellWidth.Px(StatusLabelWidth), minWidth: StatusLabelWidth,
                    tone: CellTone.Muted, fontSize: Progenitor.Ui.GameArt.GameSettingsLook.NameText, vAlign: CellVAlign.Top),
                value,
            },
            height: Progenitor.Networking.TextTokens.PreviewLines * StatusWrapLineHeight + 12f, rightInset: HeadBlockLayout.RightColumnInset,
            plain: true, indent: 0f);
    }

    /// <summary>TOKENINSERT1: the Players see text size (the muted status rows' size).</summary>
    internal const float PreviewFontSize = 16f;

    /// <summary>
    /// TOKENTIPS2: <see cref="StatusRowSpec"/> whose line wraps: every line kept (explicit line breaks too), the value at
    /// the top of the row, and the row as tall as the text the runtime measures (<see cref="CellRowSpec.FitWrap"/>); the
    /// height here is the estimate at <paramref name="rowWidth"/>, used until a measurement exists.
    /// </summary>
    internal static CellRowSpec WrappingStatusRowSpec(string key, string label, string text, float rowWidth, int fixedLines = 0)
    {
        float valueWidth = Math.Max(120f, rowWidth - StatusLabelWidth - HeadBlockLayout.RightColumnInset - 40f);
        int lines = 0;
        foreach (string paragraph in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            lines += Math.Max(1, MenuPanelLayout.WrappedLines(paragraph, valueWidth, Progenitor.Ui.GameArt.GameSettingsLook.NameText, 12));
        // RADIODESC1: a text that changes while shown (a preview) keeps a fixed number of lines (the cell truncates past them).
        if (fixedLines > 0) lines = fixedLines;
        float height = Math.Max(ModSettingsRowGrid.ActionRowHeight, lines * StatusWrapLineHeight + 12f);
        return new CellRowSpec(
            key,
            new List<CellSpec>
            {
                GridNameLead(),
                new CellSpec(CellKind.Muted, CellStatusLabel).With(
                    text: label ?? "", width: CellWidth.Px(StatusLabelWidth), minWidth: StatusLabelWidth, tone: CellTone.Muted,
                    fontSize: Progenitor.Ui.GameArt.GameSettingsLook.NameText, vAlign: CellVAlign.Top),
                new CellSpec(CellKind.Muted, CellStatusValue).With(
                    text: text ?? "", width: CellWidth.Fill(160f), tone: CellTone.Muted, wrap: CellWrap.Wrap, vAlign: CellVAlign.Top),
            },
            height: height, rightInset: HeadBlockLayout.RightColumnInset, plain: true, indent: 0f, fitWrap: fixedLines <= 0)
        {
            // PENDING3: never shorter than an ordinary status row, so the name (Pending changes) is drawn.
            FitWrapFloor = ModSettingsRowGrid.ActionRowHeight,
            // PENDINGFIT1: as tall as its text grows to, not twice the build-time estimate: a row drawn with one line
            // ("No changes pending") that later lists the summary, eight changes, "+N more" and the warning is 11 lines.
            FitWrapCeiling = fixedLines <= 0 ? StatusWrapMaxLines * StatusWrapLineHeight + 12f : 0f,
        };
    }

    /// <summary>Off the workspace (a test's recording panel): the grid row with Apply and Clear inline.</summary>
    private void BuildFolderInline(IUiPanel panel)
    {
        panel.SettingsGrid(true);
        panel.BeginInline();
        IUiInput folderField;
        try
        {
            folderField = panel.Input(ServerFolderLabel, string.IsNullOrWhiteSpace(_draftPath) ? "" : _draftPath);
            // INLINEAPPLY1: the inline Apply and Clear are game buttons (the workspace draws them as key-cap cells).
            var apply = panel.Button(ApplyFolderCaption);
            apply.GameButton();
            apply.OnClick(() => ApplyDraft());
            var clear = panel.Button(ClearFolderCaption);
            clear.GameButton();
            clear.OnClick(() => ClearFolder());
        }
        finally
        {
            panel.EndInline();
            panel.SettingsGrid(false);
        }

        folderField.OnChanged(text => _draftPath = LocalServerFolder.NormalizeTyped(text));
        Describe(panel, folderField, ServerFolderLabel, ServerFolderHelp);
        panel.Note(StatusLineWithChips());
    }

    /// <summary>Cell-row keys and cells of the folder rows on the workspace.</summary>
    internal const string RowFolder = "local-server-folder";
    internal const string RowFolderActions = "local-server-folder-actions";
    internal const string CellFolderLabel = "label";
    internal const string CellFolderPath = "path";
    internal const string CellFolderApply = "apply";
    internal const string CellFolderClear = "clear";

    /// <summary>The folder row: its name in the label column, the path field over the rest of the row.</summary>
    internal static CellRowSpec FolderRowSpec(string? path) => new(
        RowFolder,
        new List<CellSpec>
        {
            GridNameLead(),
            new CellSpec(CellKind.Text, CellFolderLabel).With(
                text: ServerFolderLabel, width: CellWidth.Px(StatusLabelWidth), minWidth: StatusLabelWidth,
                fontSize: Progenitor.Ui.GameArt.GameSettingsLook.NameText, vAlign: CellVAlign.Middle),
            new CellSpec(CellKind.Input, CellFolderPath).With(
                text: path ?? "", placeholder: FolderNotConfigured, width: CellWidth.Fill(320f), minWidth: 320f, skin: CellSkin.ValueBox),
        },
        height: ModSettingsRowGrid.RowPitch, rightInset: HeadBlockLayout.RightColumnInset, plain: true,
        indent: 0f);

    /// <summary>The line under the field: Apply and Clear as the kit's standard key caps, at the row's right.</summary>
    internal static CellRowSpec FolderActionsSpec() => new(
        RowFolderActions,
        new List<CellSpec>
        {
            new CellSpec(CellKind.Spacer, "gap"),
            new CellSpec(CellKind.Button, CellFolderApply).With(text: ApplyFolderCaption, align: CellAlign.Right, skin: CellSkin.KeyCap),
            new CellSpec(CellKind.Button, CellFolderClear).With(text: ClearFolderCaption, align: CellAlign.Right, skin: CellSkin.KeyCap),
        },
        height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, plain: true,
        indent: 0f);

    private void BuildFolderRows(IUiPanel panel)
    {
        var row = GridCellRow(panel, FolderRowSpec(string.IsNullOrWhiteSpace(_draftPath) ? "" : _draftPath));
        AddReadback(row, RowLabelColumn, (Func<object?>)(() => LabelColumnReadback(row, CellFolderLabel)));
        row.OnChanged(CellFolderPath, text => _draftPath = LocalServerFolder.NormalizeTyped(text));
        Describe(panel, row, ServerFolderLabel, ServerFolderHelp);
        AddReadback(row, RowFolderReadback, (Func<object?>)(() => new Dictionary<string, object?>
        {
            ["label"] = ServerFolderLabel,
            ["draft"] = _draftPath ?? "",
            ["validated"] = _folder.HasValidatedServer,
        }));
        var actions = GridCellRow(panel, FolderActionsSpec());
        actions.OnClick(CellFolderApply, () => ApplyDraft());
        actions.OnClick(CellFolderClear, () => ClearFolder());
    }

    internal const string RowFolderReadback = "localServerFolder";

    // ---- The Local server's cell rows on the settings grid's name column (live 2026-10-06, third pass) -----------

    /// <summary>
    /// Live 2026-10-06: these rows' labels started about 8 px left of the settings rows' names. They were cell rows added
    /// off the settings grid (laid out over the content width, not the grid's), and a cell row's first cell starts at the
    /// row's edge (<see cref="CellRowSpec.Indent"/> moves only a joined row's seam). Now they are grid rows, like every
    /// other settings row (<see cref="GridCellRow"/>), and a blank lead cell puts the label's left edge on the grid's name
    /// column: <see cref="ModSettingsRowGrid.NameInset"/> from the row's left, the inset every settings row's name has.
    /// </summary>
    internal static CellSpec GridNameLead()
    {
        float lead = GridNameLeadWidth;
        return new CellSpec(CellKind.Muted, CellNameLead).With(text: "", width: CellWidth.Px(lead), minWidth: lead);
    }

    internal const string CellNameLead = "name-lead";

    /// <summary>The lead cell's width: the name inset less the cell gap the layout adds after it.</summary>
    internal static float GridNameLeadWidth => CellLayout.GridNameLeadWidth;

    /// <summary>A cell row added on the settings grid (the grid's width and left, as every settings row).</summary>
    private static IUiCellRow GridCellRow(IUiPanel panel, CellRowSpec spec)
    {
        panel.SettingsGrid(true);
        try { return panel.CellRow(spec); }
        finally { panel.SettingsGrid(false); }
    }

    /// <summary>Readback key of the label column check: <c>{labelLeft, rowLeft, labelOffset, gridNameInset, layoutOffset}</c>.</summary>
    internal const string RowLabelColumn = "labelColumn";

    /// <summary>
    /// The label's left edge as drawn (screen x, from the row's cell rects) and from the row's left, against the inset a
    /// settings row's name has. <c>layoutOffset</c> is the same from the layout model, before anything is drawn.
    /// </summary>
    internal static Dictionary<string, object?> LabelColumnReadback(IUiCellRow row, string labelCell)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["gridNameInset"] = ModSettingsRowGrid.NameInset,
            ["layoutOffset"] = LayoutLabelOffset(labelCell),
            ["labelLeft"] = null,
            ["rowLeft"] = null,
            ["labelOffset"] = null,
        };
        try
        {
            var described = row.Describe();
            double? rowLeft = described.TryGetValue("rect", out var r) && r is Dictionary<string, object?> rr && rr["x"] is float rx ? rx : null;
            double? labelLeft = null;
            if (described.TryGetValue("cells", out var c) && c is List<object?> cells)
                foreach (var cell in cells)
                    if (cell is Dictionary<string, object?> cd && Equals(cd["key"], labelCell) && cd["rect"] is Dictionary<string, object?> lr && lr["x"] is float lx)
                        labelLeft = lx;
            d["labelLeft"] = labelLeft;
            d["rowLeft"] = rowLeft;
            if (labelLeft != null && rowLeft != null) d["labelOffset"] = Math.Round(labelLeft.Value - rowLeft.Value, 1);
        }
        catch { /* the layout's figure stands */ }
        return d;
    }

    /// <summary>The label cell's left edge from the row's left in the layout model (any row width: left cells pack from the left).</summary>
    internal static float LayoutLabelOffset(string labelCell, float rowWidth = 900f)
    {
        var spec = labelCell == CellFolderLabel ? FolderRowSpec("x") : StatusRowSpec("x", "Status", "y");
        foreach (var p in MenuPanelLayout.CellColumns(rowWidth, spec))
            if (p.Key == labelCell) return p.X - p.Width * .5f + rowWidth * .5f;
        return float.NaN;
    }

    /// <summary>The Local server heading's cell-row index (<c>page-section-90</c>), clear of the settings view's.</summary>
    internal const int LocalServerHeadingIndex = 90;

    public void Build(IUiPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        panel.ContentAlignment = ConfigurationLayout.Alignment;
        BuildRows();
        IReadOnlyList<ServerFolderSuggestion> found;
        try { found = _scan.SuggestDetailed() ?? Array.Empty<ServerFolderSuggestion>(); }
        catch { found = Array.Empty<ServerFolderSuggestion>(); }
        SplitSuggestions(found);

        // Round E: the pane's centered title band goes. Three homes: the tab's title is the game's section header with
        // its red rule, then the purpose line and its two links.
        Ui.HidePaneTitle(panel);
        _titleHeading = GameHeading(panel, PreferencesTitle);
        // MENUKIT2 round D: the explanations are in the description column; the rows are the game's.
        panel.SettingsDescriptionMode(true);

        // Three homes: the purpose line first, then the settings on the mod page's row kit (ModSettingsView embedded:
        // the same controls, reset arrow and description column), then the local server's hand-built action rows.
        BuildPurpose(panel);
        BuildSettings(panel);

        _localServerHeading = LocalServerHeading(panel);

        InColumn(panel, panel.Note(PrivateGameNote));
        // Suggest-only: a found folder (Steam, the registry, a running server) is offered, never applied.
        // A production server on this machine can be among them, so the line says what choosing it does.
        var single = SingleSuggestion;
        if (single != null)
        {
            panel.Label(FoundSingleLine(single.Path));
            panel.Note(FoundConsequence(single.Path));
        }

        // Three homes (live polish 2026-10-06): the folder is a settings row like the others — its name in the name column
        // and the field — and its status a muted info row under it. On the workspace the field takes the rest of the row (it
        // holds long paths; the second live pass read "C:\VRisingN" beside inline buttons), and Apply / Clear are key caps on
        // the line under it.
        if (panel is UiPanelHandle)
        {
            BuildFolderRows(panel);
            StatusRow(panel, RowFolderStatus, StatusRowLabel, StatusLineWithChips(), StatusLine);
        }
        else
        {
            BuildFolderInline(panel);
        }

        // Live polish: after the path field and its Validated line, one settings-grid row per suggestion — the path as the row name, the source and
        // chips in the description column on hover, a key-cap Use this folder. With a folder configured
        // the list folds under "Other servers found (N)".
        bool collapsible = _folder.HasValidatedServer && _suggestions.Count > 0;
        if (collapsible)
        {
            var header = GridButton(panel, OtherServersHeader(_suggestions.Count), OtherServersOpen ? HideCaption : ShowCaption);
            header.OnClick(() =>
            {
                OtherServersOpen = !OtherServersOpen;
                RequestRebuild();
            });
        }

        if (!collapsible || OtherServersOpen)
        {
            for (int i = 0; i < _suggestions.Count; i++)
            {
                var suggestion = _suggestions[i];
                string captured = suggestion.Path;
                var use = GridButton(panel, MiddleEllipsis(suggestion.Path), UseThisFolder);
                use.OnClick(() => ApplyDraft(captured));
                Describe(panel, use, FoundLine(suggestion.Path), suggestion.Detail + "\n\n" + FoundConsequence(suggestion.Path));
            }
        }

        // INSTALLSIDES1: no Default install target row. The Install tab's rows act per side (each has
        // its own Client and Server buttons) on the server its picker names, so the setting no longer
        // decides anything there; [Install] DefaultTarget stays bound so existing cfg files load.
        BuildServerInstall(panel);
        BuildAdminList(panel);
        // Settings organization stage 4 (decision 1): Uninstall Progenitor from this client is on Progenitor's page,
        // under Your settings · Private games (ProgenitorPage); this tab keeps mod-manager preferences only.
        panel.ShowDescription(ModSettingsDescription.Empty(ConfigurationTitle, ColumnIntro));
    }

    // ---- Three homes: the purpose line and the settings on the row kit ----------------------------------

    /// <summary>The user's words (2026-10-06): what this tab is for, and where the other two homes are.</summary>
    internal const string PurposeText =
        "How this Mods window works on this PC. What a mod does is on its page in Mods; how the server you're on is set up is in Server.";

    /// <summary>The purpose links' row: its sentence, then <b>Mods »</b> and <b>Server »</b> (<c>widget_click label=link-purpose/mods</c>).</summary>
    internal const string PurposeLinksText = "Mod settings and the server's setup";

    internal const string PurposeLinksHelp =
        "Mods: each mod's own settings are on its page (Progenitor's under Mods → Required → Progenitor). Server: how the server "
        + "you're on is set up. Nothing on this tab changes the game or any server. Back returns here.";

    /// <summary>Readback key of a link row: <c>{target, landing, label}</c>.</summary>
    internal const string RowPageLink = "page-link";

    /// <summary>Live polish 2026-10-06: short enough for the name column (the long form was cut off).</summary>
    internal const string AdvancedLabel = "Advanced sources";

    /// <summary>The fold's captions (not Show / Hide, which the other-servers list uses).</summary>
    internal const string AdvancedShowCaption = "Expand";
    internal const string AdvancedHideCaption = "Collapse";

    internal const string InstallViewLabel = "Install view";
    internal const string InstallViewHelp =
        "Simple shows each mod once with one button that installs, updates or removes it where it is needed. Advanced shows "
        + "the per-side rows, the filter bar and the server picker. The Install tab's own switch changes the same setting.";

    internal const string EditOtherModsLabel = "Edit other mods' settings";
    internal const string EditOtherModsHelp =
        "Lets the Mods menu change the settings of other BepInEx mods, not only show them. Off by default: a mod that takes "
        + "its settings from the server it joins cannot be told apart, so a change here could overwrite them. Applies at once.";

    internal const string RememberFiltersLabel = "Remember Install filters and sort";
    internal const string RememberFiltersHelp =
        "Keeps the Install tab's filters and sort when V Rising restarts. Off: each session starts from the defaults.";

    internal const string DownloadLimitLabel = "Download limit";
    internal const string DownloadLimitHelp =
        "The largest Thunderstore package the Install tab downloads, in MB (8 to 1,024). Larger listings read Too large to install.";
    internal const string IndexPathLabel = "Package index path";
    internal const string IndexPathHelp =
        "A file to read the package index from instead of BepInEx/config/progenitor-index.json. Empty: the default.";
    internal const string CanonicalFrameLabel = "Keep the in-game frame canonical";
    internal const string CanonicalFrameHelp =
        "In a world, the Mods menu keeps the game menu's own frame position and size. Off: it follows the frame the menu reports.";

    /// <summary>The <b>Advanced</b> rows of Sources are shown (this session; the tab is rebuilt on each press).</summary>
    private static bool _advancedOpen;

    internal static bool AdvancedOpen
    {
        get { lock (PromptGate) return _advancedOpen; }
        set { lock (PromptGate) _advancedOpen = value; }
    }

    private ModSettingsView? _settingsView;
    private readonly List<(IUiHandle Handle, string Key, object? Value)> _readback = new();

    /// <summary>The Configuration tab's settings view as drawn (tests, the bridge).</summary>
    internal ModSettingsView? SettingsView => _settingsView;

    private void AddReadback(IUiHandle? handle, string key, object? value)
    {
        if (handle == null) return;
        _readback.Add((handle, key, value));
    }

    /// <summary>
    /// The <c>mods_ui widget_read</c> extras of a row this pane drew with the row kit (<c>page-setting</c>,
    /// <c>page-section</c>, <c>page-link</c>, …). False for anything else.
    /// </summary>
    internal bool TryDescribeRow(IUiHandle handle, out Dictionary<string, object?>? extra)
    {
        extra = null;
        if (handle == null) return false;
        foreach (var (h, key, value) in _readback)
        {
            if (!ReferenceEquals(h, handle)) continue;
            extra ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            object? v = value;
            if (v is Func<object?> live)
            {
                try { v = live(); }
                catch (Exception e) { v = "error: " + e.Message; }
            }

            extra[key] = v;
        }

        return extra != null;
    }

    /// <summary>Three homes: the purpose line (a wrapping note) and the row with its two links.</summary>
    private void BuildPurpose(IUiPanel panel)
    {
        var note = panel.Note(PurposeText);
        AddReadback(note, RowPurpose, PurposeText);
        var row = panel.CellRow(PurposeRow());
        row.OnClick(SettingsLinks.CellMods, () => OpenMods());
        row.OnClick(SettingsLinks.CellServer, () => OpenServer());
        Describe(panel, row, PurposeLinksText, PurposeLinksHelp);
        AddReadback(row, RowPageLink, (Func<object?>)(() => new Dictionary<string, object?>
        {
            ["targets"] = new List<string> { SettingsLinks.TargetMods, SettingsLinks.TargetServerTab },
            ["landing"] = SettingsLinks.LandingTop,
            ["label"] = PurposeLinksText,
        }));
    }

    /// <summary>Readback key of the purpose line.</summary>
    internal const string RowPurpose = "preferences-purpose";

    /// <summary>The purpose links' row: the sentence, then <b>Mods »</b> and <b>Server »</b> at the control column's right.</summary>
    internal static CellRowSpec PurposeRow() => new(
        SettingsLinks.PurposeKey,
        new List<CellSpec>
        {
            new CellSpec(CellKind.Text, SettingsLinks.CellLabel).With(
                text: PurposeLinksText, width: CellWidth.Fill(160f), fontSize: Progenitor.Ui.GameArt.GameSettingsLook.NameText,
                vAlign: CellVAlign.Middle, wrap: CellWrap.Ellipsis),
            new CellSpec(CellKind.Button, SettingsLinks.CellMods).With(
                text: SettingsLinks.CaptionFor(SettingsLinks.TargetMods), align: CellAlign.Right, skin: CellSkin.KeyCap),
            new CellSpec(CellKind.Button, SettingsLinks.CellServer).With(
                text: SettingsLinks.CaptionFor(SettingsLinks.TargetServerTab), align: CellAlign.Right, skin: CellSkin.KeyCap),
        },
        height: ModSettingsRowGrid.RowPitch, rightInset: HeadBlockLayout.RightColumnInset, plain: true,
        indent: ModSettingsRowGrid.NameInset);

    /// <summary>The purpose line's <b>Mods »</b>.</summary>
    public SettingActionResult OpenMods()
    {
        var open = _openMods;
        if (open == null) return SettingActionResult.Refuse("The Mods tab is not reachable from here.");
        open();
        return SettingActionResult.Ok("Mods");
    }

    /// <summary>The purpose line's <b>Server »</b>.</summary>
    public SettingActionResult OpenServer()
    {
        var open = _openServer;
        if (open == null) return SettingActionResult.Refuse("The Server tab is not reachable from here.");
        open();
        return SettingActionResult.Ok("Server");
    }

    /// <summary>
    /// Three homes: Mods tab, Install Mods tab, and Sources and downloads, as the row kit draws them. The 2026-10-06 trim
    /// (user: "No user will care to change those") left Background intensity and contrast, Game menu tabs in back/forward,
    /// the Authenticate-as-admin button and Current game update tag in the cfg file only (<see cref="SettingPlaces"/>), and
    /// dropped the Nexus Mods placeholder line. Every group has four settings or fewer now, so none is a drawer.
    /// </summary>
    internal List<ModSettingsSection> SettingsSections()
    {
        var menu = new List<ModSettingsItem>();
        AddSetting(menu, CoreUiSettings.EditOtherModsSettings, EditOtherModsLabel, EditOtherModsHelp, SettingControl.Toggle);

        var install = new List<ModSettingsItem>();
        AddSetting(install, _bindings.InstallView, InstallViewLabel, InstallViewHelp, SettingControl.Dropdown,
            choices: new[] { InstallViewSetting.SimpleName, InstallViewSetting.AdvancedName });
        AddSetting(install, _bindings.RememberInstallFilters, RememberFiltersLabel, RememberFiltersHelp, SettingControl.Toggle);

        var sources = new List<ModSettingsItem>();
        AddSetting(sources, _bindings.UseThunderstore, "Use Thunderstore", ThunderstoreHelp, SettingControl.Toggle);
        AddSetting(sources, _bindings.ShowNsfwContent, ShowNsfwLabel, ShowNsfwHelp, SettingControl.Toggle);
        AddSetting(sources, _bindings.MaxDownloadMb, DownloadLimitLabel, DownloadLimitHelp, SettingControl.Number,
            min: ConfigHelper.MaxDownloadMbMin, max: ConfigHelper.MaxDownloadMbMax, units: "MB");
        bool open = AdvancedOpen;
        sources.Add(ModSettingsItem.ForButton(SettingsPanel.PluginId + "/button/" + AdvancedLabel,
            open ? AdvancedHideCaption : AdvancedShowCaption, () => ToggleAdvanced(),
            confirm: null, new ModSettingOptions { Label = AdvancedLabel, Help = "The package index path and keeping the in-game frame canonical: two settings most players never need.", Uncounted = true }));
        if (open)
        {
            // The fold's own two rows: not counted toward the group's settings, so opening the fold never turns Sources
            // into a drawer (its heading would change style under the pointer).
            AddSetting(sources, _bindings.IndexPath, IndexPathLabel, IndexPathHelp, SettingControl.Text, uncounted: true);
            AddSetting(sources, _bindings.CanonicalInGameFrame, CanonicalFrameLabel, CanonicalFrameHelp, SettingControl.Toggle, uncounted: true);
        }

        var updates = new List<ModSettingsItem>();
        AddSetting(updates, _bindings.AutoUpdateMods, UpdateModsLabel, UpdateModsHelp, SettingControl.Dropdown,
            choices: Progenitor.Install.UpdateSettings.ModeWords);
        AddSetting(updates, _bindings.AutoUpdateMajorVersions, MajorVersionsLabel, MajorVersionsHelp, SettingControl.Dropdown,
            choices: Progenitor.Install.UpdateSettings.MajorWords);
        AddSetting(updates, _bindings.AutoUpdatePromptBeforeJoin, PromptBeforeJoinLabel, PromptBeforeJoinHelp, SettingControl.Toggle);
        AddSetting(updates, _bindings.AutoUpdateProgenitor, UpdateProgenitorLabel, UpdateProgenitorHelp, SettingControl.Dropdown,
            choices: Progenitor.Install.UpdateSettings.ModeWords);

        var browser = new List<ModSettingsItem>();
        AddSetting(browser, CoreUiSettings.ServerBrowserProgenitorOnly, ProgenitorServersLabel, ProgenitorServersHelp, SettingControl.Toggle);

        var sections = new List<ModSettingsSection>(4);
        if (menu.Count > 0) sections.Add(new ModSettingsSection(ModsTabGroup, ModSettingsSectionKind.Page, menu));
        if (install.Count > 0) sections.Add(new ModSettingsSection(InstallTabGroup, ModSettingsSectionKind.Page, install));
        sections.Add(new ModSettingsSection(SourcesGroup, ModSettingsSectionKind.Page, sources));
        if (updates.Count > 0) sections.Add(new ModSettingsSection(UpdatesGroup, ModSettingsSectionKind.Page, updates));
        if (browser.Count > 0) sections.Add(new ModSettingsSection(JoinGameGroup, ModSettingsSectionKind.Page, browser));
        return sections;
    }

    private void ToggleAdvanced()
    {
        AdvancedOpen = !AdvancedOpen;
        RequestRebuild();
    }

    /// <summary>
    /// One mod-manager setting on the row kit: its entry (the one its module reads), written through the settings
    /// catalog's validated path. A registered binding's presentation (Use Thunderstore, NSFW) is kept under the
    /// label and help given here. A null entry (not bound in this process) draws nothing.
    /// </summary>
    private static void AddSetting(
        List<ModSettingsItem> items, ConfigEntryBase? entry, string label, string help, SettingControl control,
        IReadOnlyList<string>? choices = null, double? min = null, double? max = null, double? step = null, string? units = null,
        bool uncounted = false)
    {
        if (entry == null) return;
        var registered = SettingsCatalog.PresentationFor(SettingsPanel.PluginId, entry.Definition.Section, entry.Definition.Key);
        var presentation = ModSettingsLayout.Merge(registered, new SettingPresentation
        {
            Label = label,
            Help = help,
            Choices = choices == null ? null : new List<string>(choices).ToArray(),
            Min = min.HasValue ? min.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : null,
            Max = max.HasValue ? max.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : null,
            Step = step.HasValue ? step.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : null,
            Units = units,
        });
        // Progenitor's own file (the shared cfg is listed by the chainloader sweep as another source): editable here.
        var row = SettingsRow.ForEntry(SettingsPanel.PluginId, entry, synced: false, foreign: false, presentation: presentation)
            .WithAlignment(LayoutAlignment);
        items.Add(ModSettingsItem.ForRow(row, new ModSettingOptions { Label = label, Help = help, Control = control, Uncounted = uncounted }));
    }

    private void BuildSettings(IUiPanel panel)
    {
        _settingsView?.Release();
        _readback.RemoveAll(r => r.Key != RowPageLink && r.Key != RowPurpose);
        var view = new ModSettingsView(SettingsPanel.PluginId, SettingsSections(), null)
        {
            Embedded = true,
            EmptyHeading = ConfigurationTitle,
            EmptyBody = ColumnIntro,
            // Three homes: a group of more than four settings is a drawer; the others are gold subheadings.
            DrawerScope = SettingsDrawers.ScopePreferences,
            DrawerToggled = (_, _) => ModsMenu.NoteSectionScrollMoved(),
        };
        _settingsView = view;
        try { view.Build(panel, AddReadback); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Configuration settings threw: " + e.Message); }
    }

    /// <summary>
    /// UI polish: where the server half goes now — set by the Mods menu to the Install tab's own route
    /// decision. Null: the validated local folder, else no server.
    /// </summary>
    internal Func<Progenitor.Install.ServerRoute>? RouteProbe { get; set; }

    /// <summary>How the remote route authorizes this player (for the description's words).</summary>
    internal Func<Progenitor.Install.RemoteAuthorizedBy>? AuthorizedByProbe { get; set; }

    /// <summary>The description of the selected <c>Default install target</c>, live.</summary>
    public string DefaultInstallTargetDescription
    {
        get
        {
            var setting = Progenitor.Install.DefaultInstallTarget.Parse(DefaultInstallTargetValue);
            Progenitor.Install.ServerRoute route;
            try
            {
                route = RouteProbe?.Invoke() ?? (_folder.HasValidatedServer
                    ? new Progenitor.Install.ServerRoute(Progenitor.Install.ServerRouteKind.Local, Progenitor.Install.ServerRouteBlock.None)
                    : new Progenitor.Install.ServerRoute(Progenitor.Install.ServerRouteKind.None, Progenitor.Install.ServerRouteBlock.NoServer));
            }
            catch { route = new Progenitor.Install.ServerRoute(Progenitor.Install.ServerRouteKind.None, Progenitor.Install.ServerRouteBlock.NoServer); }

            var by = Progenitor.Install.RemoteAuthorizedBy.AdminList;
            try { by = AuthorizedByProbe?.Invoke() ?? by; }
            catch { /* the words only */ }
            string? path = _folder.HasValidatedServer ? _folder.Current.CanonicalPath : null;
            return Progenitor.Install.DefaultInstallTargetHelp.Describe(setting, route, path, by);
        }
    }

    /// <summary>
    /// The stored <c>[Install] DefaultTarget</c>, normalized to one of the four values. Writing it
    /// writes the bound config entry when one exists; with no bound entry (tests, a Core without a
    /// config file) it is remembered for this pane so the row still answers what it shows.
    /// </summary>
    public string DefaultInstallTargetValue
    {
        get
        {
            string? stored = _defaultTargetOverride;
            if (stored == null)
            {
                try { return Progenitor.Install.DefaultInstallTarget.ValueOf(Progenitor.Install.DefaultInstallTargetConfig.Resolve()); }
                catch { stored = null; }
            }

            return Progenitor.Install.DefaultInstallTarget.ValueOf(
                Progenitor.Install.DefaultInstallTarget.Parse(stored));
        }

        set
        {
            string normalised = Progenitor.Install.DefaultInstallTarget.ValueOf(
                Progenitor.Install.DefaultInstallTarget.Parse(value));
            var entry = TryDefaultInstallTargetEntry();
            if (entry != null)
            {
                try { entry.Value = normalised; }
                catch { /* a refused write leaves the override below as the pane's answer */ }
            }

            _defaultTargetOverride = normalised;
        }
    }

    private string? _defaultTargetOverride;

    private static BepInEx.Configuration.ConfigEntry<string>? TryDefaultInstallTargetEntry()
    {
        try { return Progenitor.Config.ConfigHelper.DefaultInstallTargetEntry; }
        catch { return null; }
    }

    /// <summary>MODS2: true for the uninstall button, its note and the prompt rows this pane built.</summary>
    public bool IsClientUninstallHandle(object handle)
    {
        for (int i = 0; i < _clientHandles.Count; i++)
            if (ReferenceEquals(_clientHandles[i], handle)) return true;
        return false;
    }

    public static string FoundLine(string path) => "Found: " + path;

    private void BuildServerInstall(IUiPanel panel)
    {
        _serverHandles.Clear();
        if (!_folder.HasValidatedServer)
        {
            _serverStatus = null;
            PromptOpen = false;
            return;
        }

        var status = RefreshServerInstall();
        if (PromptOpen && status.Ledger == null) PromptOpen = false;

        if (PromptOpen)
        {
            var heading = GameHeading(panel, ServerRemoveHeading);
            var body = panel.Note(UninstallPromptText);
            panel.BeginInline();
            var remove = panel.Button(ServerRemoveButton);
            remove.OnClick(() => ConfirmUninstall());
            var cancel = panel.Button(ServerCancelButton);
            cancel.OnClick(() => CancelUninstall());
            panel.EndInline();
            _serverHandles.Add(heading);
            _serverHandles.Add(body);
            _serverHandles.Add(remove);
            _serverHandles.Add(cancel);
            return;
        }

        if (status.State == ServerInstallState.Refused)
        {
            // Not enabled: no toggle while the folder cannot be written or its server is running.
            // The stop/start rows still build: a running server is exactly when Stop server matters.
            // UI polish: a running server is one short line; the full sentence (path included) is the
            // description when the row is hovered.
            bool running = status.Reason.StartsWith(Progenitor.Install.ServerInstall.RefusalRunningPrefix, StringComparison.Ordinal);
            var refused = StatusRow(panel, RowServerStatus, ServerProgenitorRowName, running ? ServerRunningLine : status.Reason, status.Reason);
            _serverHandles.Add(refused);
            BuildProcessRows(panel);
            return;
        }

        bool on = status.State == ServerInstallState.Installed || status.State == ServerInstallState.UpdateAvailable;
        panel.SettingsGrid(true);
        var toggle = panel.Toggle(ServerInstallToggle, on);
        panel.SettingsGrid(false);
        toggle.OnChanged(v => OnServerToggle(v));
        _serverHandles.Add(toggle);
        Describe(panel, toggle, ServerInstallToggle, "Install or remove Progenitor on the dedicated server in the configured folder.");
        if (status.State == ServerInstallState.UpdateAvailable)
        {
            var update = GridButton(panel, ServerProgenitorRowName, ServerUpdateButton);
            update.OnClick(() => InstallProgenitorOnServer());
            _serverHandles.Add(update);
        }

        var line = StatusRow(panel, RowServerStatus, ServerProgenitorRowName, _serverLine, _serverLine);
        _serverHandles.Add(line);
        BuildProcessRows(panel);
    }

    /// <summary>
    /// SERVERINST3: <c>Stop server</c> / <c>Stop server (force)</c> while a server runs from the
    /// validated folder, <c>Start server</c> when none runs. Every row is a
    /// <c>serverInstall</c> handle, so <c>mods_ui widget_read</c> reports the process and stop
    /// blocks from any of them.
    /// </summary>
    private void BuildProcessRows(IUiPanel panel)
    {
        if (!_folder.HasValidatedServer) return;

        if (ForceStopPromptOpen)
        {
            var heading = GameHeading(panel, StopPromptHeading);
            var body = panel.Note(ForceStopPromptText);
            panel.BeginInline();
            var stop = panel.Button(StopPromptStopButton);
            stop.OnClick(() => ConfirmForceStop());
            var cancel = panel.Button(StopPromptCancelButton);
            cancel.OnClick(() => CancelForceStop());
            panel.EndInline();
            _serverHandles.Add(heading);
            _serverHandles.Add(body);
            _serverHandles.Add(stop);
            _serverHandles.Add(cancel);
            return;
        }

        bool running = ServerProcessControl.Running(StopKey) != null;
        if (running)
        {
            var graceful = GridButton(panel, LocalServerRowName, StopServerButton);
            graceful.OnClick(() => StopServer());
            Describe(panel, graceful, StopServerButton, StopServerHelp);
            _serverHandles.Add(graceful);
            var force = GridButton(panel, ForceStopRowName, StopServerForceButton);
            force.OnClick(() => RequestForceStop());
            force.Destructive();
            Describe(panel, force, StopServerForceButton, StopServerHelp);
            _serverHandles.Add(force);
            // REMOTEINST1: a player on this server's admin list who has not run adminauth gets the
            // button (or the words). Since F21 and review L3 the graceful stop and .stop-server accept the admin list; the
            // offer stays for the other adminOnly commands, which gate on User.IsAdmin.
            bool connectedHere = false;
            try { connectedHere = ServerProcessControl.GracefulAvailable(StopKey, out _); }
            catch { connectedHere = false; }
            AdminAuthUi.Draw(panel, _serverHandles, featureGated: connectedHere);
            return;
        }

        if (ServerLaunchRecord.TryRead(StopKey) == null)
        {
            var unavailable = panel.Note(ServerProcessControl.StartUnavailableLine());
            _serverHandles.Add(unavailable);
            return;
        }

        var start = GridButton(panel, LocalServerRowName, StartServerButton);
        start.OnClick(() => StartServer());
        Describe(panel, start, StartServerButton, StartServerHelp);
        _serverHandles.Add(start);
    }

    // ---- ADMINLIST1: "Add me" for the validated server's adminlist.txt ----

    internal const string AdminListRowName = "Admin list";

    /// <summary>
    /// The last Add me line per validated folder. Static because the host discards this pane and
    /// builds a new one on every recompose, including the one the click asks for.
    /// </summary>
    private static readonly Dictionary<string, string> AdminListLines = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<object> _adminListHandles = new();

    /// <summary>True for the note, button and lines the Add me helper built.</summary>
    public bool IsAdminListHandle(object handle)
    {
        for (int i = 0; i < _adminListHandles.Count; i++)
            if (ReferenceEquals(_adminListHandles[i], handle)) return true;
        return false;
    }

    /// <summary>The last Add me outcome line for the validated folder, or empty.</summary>
    public string AdminListLine
    {
        get
        {
            string? key = StopKey;
            if (key == null) return "";
            lock (PromptGate) return AdminListLines.TryGetValue(key, out var line) ? line : "";
        }
    }

    /// <summary>
    /// Shown only for a validated folder, a known SteamID (<see cref="Progenitor.Install.AdminListFile.LocalSteamId"/>
    /// is 0 in production until a proven read exists, which hides the helper) and a list that does
    /// not hold it, and never while a stop or uninstall prompt is up. The last outcome line stays
    /// under the section after the add, when the note and button are gone.
    /// </summary>
    private void BuildAdminList(IUiPanel panel)
    {
        _adminListHandles.Clear();
        string? root = StopKey;
        if (root == null || PromptOpen || ForceStopPromptOpen) return;
        ulong id = Progenitor.Install.AdminListFile.LocalSteamId();
        if (id == 0) return;

        if (Progenitor.Install.AdminListFile.NeedsAdd(root, id, out var location))
        {
            _adminListHandles.Add(panel.Note(AdminListNote));
            string? target = location?.Target;
            if (target == null)
            {
                _adminListHandles.Add(panel.Note(Progenitor.Install.AdminListFile.RefusedLine(Progenitor.Install.AdminListFile.ReasonNoLocation)));
            }
            else
            {
                var add = GridButton(panel, AdminListRowName, AdminListAddButton);
                add.OnClick(() => AddMeToAdminList());
                Describe(panel, add, AdminListAddButton, Progenitor.Install.AdminListFile.AddMeHelp);
                _adminListHandles.Add(add);
                _adminListHandles.Add(panel.Note(Progenitor.Install.AdminListFile.WillAddLine(id, target)));
            }
        }

        string line = AdminListLine;
        if (line.Length > 0) _adminListHandles.Add(panel.Note(line));
    }

    /// <summary>Add me: append this player's SteamID to the validated server's adminlist.txt.</summary>
    public SettingActionResult AddMeToAdminList()
    {
        string? root = StopKey;
        var result = Progenitor.Install.AdminListFile.Add(root, Progenitor.Install.AdminListFile.LocalSteamId());
        string line = result.Line;
        if (root != null)
        {
            lock (PromptGate) AdminListLines[root] = line;
        }

        RequestRebuild();
        return result.Kind == Progenitor.Install.AdminListEditKind.Refused
            ? SettingActionResult.Refuse(line)
            : SettingActionResult.Ok(line);
    }

    /// <summary>
    /// SERVERINST2: a click on the toggle is a flip of the real state, not of the kit's value.
    /// The toggle is only ever built checked while the ledger exists, so a widget whose bound
    /// value fell out of step (a recompose, a bridge <c>widget_click</c> that re-sent the same
    /// value) still opens the prompt from installed and still installs from not-installed.
    /// </summary>
    private void OnServerToggle(bool on)
    {
        var status = RefreshServerInstall();
        bool installed = status.Ledger != null;
        if (status.State == ServerInstallState.Refused)
        {
            _serverLine = status.Reason;
            RequestRebuild();
            return;
        }

        if (installed) RequestUninstall();
        else InstallProgenitorOnServer();
    }

    private static string DefaultServerLine(ServerInstallStatus status) => status.State switch
    {
        ServerInstallState.Installed => Progenitor.Install.ServerInstall.InstalledStatus(status.Version),
        ServerInstallState.UpdateAvailable => Progenitor.Install.ServerInstall.UpdateStatus(status),
        ServerInstallState.Refused => status.Reason,
        _ => Progenitor.Install.ServerInstall.NotInstalledStatus,
    };

    private void RequestRebuild()
    {
        try { _rebuild?.Invoke(); }
        catch { /* the host's recompose is best-effort */ }
    }

    /// <summary>
    /// CONF1: normalize (invisible marks, whitespace, quotes, env vars, ~) then validate through
    /// <see cref="LocalServerFolder"/>. The outcome is always shown: "Validated: path" or the exact
    /// refusal. A valid path is persisted to <c>DedicatedServerPath</c>; a refusal keeps the
    /// previous valid configuration. Either way the pane recomposes so the status note and the
    /// server-install rows reflect the folder.
    /// </summary>
    public SettingActionResult ApplyDraft(string? typed = null)
    {
        string candidate = LocalServerFolder.NormalizeTyped(typed ?? _draftPath);
        if (candidate.Length == 0)
            candidate = LocalServerFolder.NormalizeTyped(_bindings.DedicatedServerPath.Value);
        _draftPath = candidate;
        var result = _folder.Apply(_draftPath);
        if (!result.Ok)
        {
            _status = RefusedStatus(result.Refusal, _draftPath);
            RequestRebuild();
            return SettingActionResult.Refuse(_status);
        }

        _bindings.DedicatedServerPath.Value = result.CanonicalPath ?? _draftPath;
        _draftPath = _bindings.DedicatedServerPath.Value ?? _draftPath;
        _status = ValidatedStatus(_draftPath);
        RequestRebuild();
        return SettingActionResult.Ok(_status);
    }

    public SettingActionResult ClearFolder()
    {
        SetPrompt(PromptKey, false);
        _folder.Clear();
        _bindings.DedicatedServerPath.Value = "";
        _draftPath = "";
        _status = FolderNotConfigured;
        RequestRebuild();
        return SettingActionResult.Ok(_status);
    }

    public void Dispose()
    {
        try { _settingsView?.Release(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Configuration settings release threw: " + e.Message); }
        _settingsView = null;
        _readback.Clear();
        DisposeLeases();
    }

    private SettingActionBinding ActionBinding(string id)
    {
        string settingId = SettingsPanel.PluginId + "/" + SettingBinding.ActionSection + "/" + id;
        foreach (var action in SettingsCatalog.AllActions())
        {
            if (string.Equals(action.SettingId, settingId, StringComparison.OrdinalIgnoreCase))
                return action;
        }

        throw new InvalidOperationException("settings action was not registered: " + id);
    }

    private string StatusFromFolder()
    {
        if (_folder.HasValidatedServer) return ValidatedStatus(_folder.Current.CanonicalPath ?? _draftPath);
        if (_folder.IsUnavailable)
            return RefusedStatus(_folder.Current.Refusal ?? LocalServerFolder.RefusalLaterInvalid, _folder.Current.CanonicalPath ?? _draftPath);
        if (!string.IsNullOrWhiteSpace(_draftPath)) return RefusedStatus(LocalServerFolder.RefusalEmpty, _draftPath);
        return FolderNotConfigured;
    }

    private void DisposeLeases()
    {
        for (int i = 0; i < _leases.Count; i++)
        {
            try { _leases[i].Dispose(); }
            catch { /* teardown */ }
        }

        _leases.Clear();
    }
}
