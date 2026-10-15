using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>Empty / loading / error surfaces for the Install tab. Ready is the list view.</summary>
internal enum InstallPaneSurface
{
    Ready = 0,
    NoSourceEnabled = 1,
    IndexNotLoaded = 2,
    Loading = 3,
    Error = 4,
}

/// <summary>On-demand README fetch state for the opened drawer. Not per-frame.</summary>
internal enum ListingDocState
{
    Idle = 0,
    Loading = 1,
    Ready = 2,
    Unavailable = 3,
}

/// <summary>
/// Primary-action phase of the opened listing (DL1). Download/check is separate from
/// execution approval: <see cref="Reviewing"/> holds an approval that a target change drops.
/// </summary>
internal enum InstallActionPhase
{
    Idle = 0,
    Downloading = 1,
    Inspected = 2,
    Reviewing = 3,
    Executing = 4,
    Done = 5,
    Failed = 6,
    Cancelled = 7,
}

/// <summary>The one install fact a row shows (LIST2): what its button says and how it reads back.</summary>
internal enum InstallListingState
{
    /// <summary>Not installed; the button offers Install.</summary>
    None = 0,
    /// <summary>Download, check or write in progress.</summary>
    Installing = 1,
    /// <summary>Loaded (or present) — nothing to do; installed mods link to the Mods tab.</summary>
    Installed = 2,
    /// <summary>Written to pending; loads at the next restart.</summary>
    Pending = 3,
    /// <summary>A newer listing than the installed one.</summary>
    Update = 4,
    /// <summary>A Thunderstore tool, not a plugin; never installable from here.</summary>
    Tool = 5,
    /// <summary>The planner refused; the exact reason sits under the button.</summary>
    Blocked = 6,
    /// <summary>
    /// INSTORD1: a loaded Progenitor-ledgered install whose files were just removed. The process
    /// still holds the assembly until it restarts, so the row says so rather than reading
    /// not-installed.
    /// </summary>
    Removed = 7,

    /// <summary>
    /// MODS2: the plugin loader (BepInExPack). Already installed by definition - Progenitor runs
    /// because of it - and Progenitor never writes <c>BepInEx/core</c>, so the row is disabled and
    /// counts as not installable, exactly like <see cref="Tool"/>.
    /// </summary>
    Loader = 8,

    /// <summary>
    /// INSTALL2 item 4: installed, still loaded, and asked to go at the next start. The file is on
    /// disk with a <c>.uninstall</c> marker beside it; the row is orange, its one-liner says
    /// <see cref="InstallModsCopy.UninstalledPendingLine"/> and its button offers
    /// <see cref="InstallModsCopy.Reactivate"/>.
    /// </summary>
    PendingUninstall = 9,

    /// <summary>
    /// REMOTEINST1 per-side status: a declared both-sided mod missing a half, or with the two halves
    /// at different versions. The button (if any) installs or updates the missing or older half.
    /// </summary>
    Sides = 10,
}

/// <summary>
/// INSTALL2 item 3: the y/n confirmation open under one row. One per pane at a time; the kind is
/// decided by the row's state when the button was pressed, never by the button's caption.
/// </summary>
internal enum InstallPromptKind
{
    None = 0,

    /// <summary><c>Install &lt;name&gt; &lt;version&gt;?</c> — the only install confirmation; the target comes from the setting.</summary>
    Install = 1,

    /// <summary><c>Uninstall &lt;name&gt; &lt;version&gt;?</c></summary>
    Uninstall = 2,

    /// <summary><c>Keep &lt;name&gt; installed?</c> — the Reactivate confirmation.</summary>
    Reactivate = 3,

    /// <summary><c>Cancel installing &lt;name&gt;?</c> — a pending install that has not loaded.</summary>
    CancelInstall = 4,
}

/// <summary>One target-control choice. Absent when the local server is not configured.</summary>
internal readonly struct InstallTargetChoice
{
    public InstallTargetChoice(InstallTarget target, string caption, bool available)
    {
        Target = target;
        Caption = caption ?? "";
        Available = available;
    }

    public InstallTarget Target { get; }
    public string Caption { get; }
    public bool Available { get; }
}

/// <summary>Player-facing copy of the Install list view (LIST2). Game-free constants; no hash, tier or framework wording.</summary>
internal static class InstallModsCopy
{
    // ---- Toolbar and filters ---------------------------------------------------------
    public const string SearchPlaceholder = "Search mods";
    public const string SideCaption = "Side";
    public const string NoRestartChip = "No restart needed";

    /// <summary>INSTALLFILTERS1: "Current game update (Oakveil)" (was "Current update").</summary>
    public const string CurrentUpdateChip = "Current game update";

    // INSTALLFILTERS1: the Show group (single choice, each with its match count) and the Only group.
    public const string ShowCaption = "Show";
    public const string OnlyCaption = "Only";
    public const string ShowAll = "All";
    public const string ShowInstalled = "Installed";
    public const string ShowMissing = "Missing a side";
    public const string ShowUpdate = "Needs update";

    /// <summary><c>Missing a side 2</c>: a Show chip's caption with its count.</summary>
    public static string ShowChipCaption(string caption, int count) =>
        caption + " " + count.ToString(CultureInfo.InvariantCulture);

    // The one-line summary: "Showing 12 · missing a side · client mods · current game update only".
    public const string SummaryShowing = "Showing";
    public const string SummaryInstalled = "installed";
    public const string SummaryMissing = "missing a side";
    public const string SummaryUpdate = "needing an update";
    public const string SummaryClient = "client mods";
    public const string SummaryServer = "server mods";
    public const string SummaryBoth = "mods for both sides";
    public const string SummaryNoRestart = "no restart needed";
    public const string SummaryCurrent = "current game update only";
    public const string SummaryMatching = "matching";
    public const string ClearFilters = "Clear filters";
    public const string Listings = "listings";
    public const string Listing = "listing";
    public const string Filtered = "filtered";
    public const string Shown = "shown";
    public const string Descriptions = "descriptions";

    // ---- Header ----------------------------------------------------------------------
    public const string HeaderMod = "Mod";
    public const string HeaderAuthor = "Author";
    public const string HeaderSide = "Side";
    public const string HeaderVersion = "Version";
    public const string HeaderReleased = "Released";

    /// <summary>INSTALLCOLS1: when this machine installed the mod (see <see cref="InstallTimes"/>).</summary>
    public const string HeaderInstalled = "Installed";
    public const string HeaderDownloads = "Downloads";

    /// <summary>INSTALLCOLS1: the Status column — the state text and the one button, right-aligned.</summary>
    public const string HeaderStatus = "Status";

    /// <summary>INSTALLSIDES1: the Client column (this game's copy); the Server column's caption names the picked server.</summary>
    public const string HeaderClient = "Client";

    /// <summary>INSTALLSIDES1: the drawer's own close button, at its top.</summary>
    public const string Collapse = "Collapse";

    /// <summary>INSTALLSIDES2 item 4: the README reader window's dismissal.</summary>
    public const string ReaderClose = "Close";

    // LIMITS1: the confirmation's side-only and both-sides answers.
    public const string PromptYesClientOnly = "Yes, client only";
    public const string PromptYesServerOnly = "Yes, server only";
    public const string PromptYesInstallBoth = "Yes, install on both";
    public const string PromptYesUpdateBoth = "Yes, update both";
    public const string BothNoServer = "No server is picked, so only this client is installed.";
    public const string BothPrivateGames = "The private games this client hosts load this client's copy.";
    public const string BothAdminOnly = "Only a server admin can install it on the server.";
    public const string BothOtherSideBlocked = "The other side cannot be installed from here.";

    /// <summary>INSTALLSIDES1: the expand button's hover text and readback.</summary>
    public const string ExpandTooltip = "Show details";

    /// <inheritdoc cref="ExpandTooltip"/>
    public const string CollapseTooltip = "Hide details";

    /// <summary>INSTALLSIDES1: the author line under a listing's name.</summary>
    public const string ByPrefix = "by ";

    /// <summary>INSTALLSIDES1: the toolbar's label before the server picker.</summary>
    public const string ServerPickerLabel = "Server";

    /// <summary>INSTALLSIDES1: the picker dialog marks the server in effect with this (Latin-1, in every game font).</summary>
    public const string PickedMark = "» ";

    /// <summary>INSTALLSIDES1: a picked folder this pane has no way to make the configured one.</summary>
    public const string PickerCannotApply = "This server folder cannot be chosen here; set it under Configuration.";

    /// <summary>INSTALLSIDES1: the picker dialog's way out without a change.</summary>
    public const string PickerCancel = "Close";

    /// <summary>INSTALLSIDES1: the drawer's facts.</summary>
    public const string FactLatest = "Latest version";

    /// <inheritdoc cref="FactLatest"/>
    public const string FactReleased = "Released";

    /// <inheritdoc cref="FactLatest"/>
    public const string FactSide = "Side";

    /// <inheritdoc cref="FactLatest"/>
    public const string FactInstalledHere = "Installed here";

    /// <inheritdoc cref="FactLatest"/>
    public const string FactNsfw = "NSFW content";

    /// <summary>The <c>NSFW content</c> fact's value: whose flag it is.</summary>
    public const string NsfwFlagged = "Flagged by the author";

    /// <summary>The <c>Installed here</c> fact of a listing this machine does not have.</summary>
    public const string NotInstalledHere = "Not on this PC";

    /// <summary>The <c>Side</c> fact's words.</summary>
    public const string SideFactClient = "Client (this game)";

    /// <inheritdoc cref="SideFactClient"/>
    public const string SideFactServer = "Server";

    /// <inheritdoc cref="SideFactClient"/>
    public const string SideFactBoth = "Client and server";

    /// <inheritdoc cref="SideFactClient"/>
    public const string SideFactUnknown = "Unknown until checked";

    /// <inheritdoc cref="SideFactClient"/>
    public const string SideFactTagged = " (from its Thunderstore tags)";

    /// <summary>The <c>Side</c> fact for a listing.</summary>
    public static string SideFact(PackageSide side, bool fromTags)
    {
        string words = side switch
        {
            PackageSide.Client => SideFactClient,
            PackageSide.Server => SideFactServer,
            PackageSide.Both => SideFactBoth,
            _ => SideFactUnknown,
        };
        return side != PackageSide.Unknown && fromTags ? words + SideFactTagged : words;
    }

    /// <summary>
    /// INSTALLCOLS1: the note under a listing's name when it does not carry the current update's tag
    /// (<c>Built for Gloomrot</c>). Nothing is shown when it does. It replaces the old Update column,
    /// which said the current tag on every row.
    /// </summary>
    public const string BuiltForPrefix = "Built for ";

    /// <summary>INSTALLCOLS1: the same note for a listing that carries no update tag at all.</summary>
    public const string NoUpdateTag = "No update tag";

    // ---- The primary action ----------------------------------------------------------
    public const string InstallRestart = "Install · restart";

    /// <summary>
    /// UI polish (2026-10-01): the Install button says where the mod goes; the restart a write owes is
    /// shown by the Status label after it (<c>Pending game restart</c> / <c>Pending server restart</c>).
    /// </summary>
    public const string InstallOnServer = "Install on server";

    /// <inheritdoc cref="InstallOnServer"/>
    public const string InstallOnBoth = "Install on both";

    /// <inheritdoc cref="InstallOnServer"/>
    public const string UpdateOnServer = "Update on server";

    /// <inheritdoc cref="InstallOnServer"/>
    public const string UpdateOnBoth = "Update on both";

    /// <inheritdoc cref="InstallOnServer"/>
    public const string UpdateHere = "Update";

    /// <summary>
    /// F20: the pending action row's button when a server restart applies the changes. It asks the
    /// server to save and stop; nothing in Progenitor starts it again by itself, so it does not say
    /// "Restart".
    /// </summary>
    public const string StopServerToApply = "Stop server to apply";

    /// <summary>F20: the same button while the request is in flight (disabled).</summary>
    public const string StoppingServer = "Stopping…";

    /// <summary>F20: the stop confirmation's heading, <c>Stop Name?</c>.</summary>
    public static string StopServerHeading(string name) => "Stop " + name + "?";

    public const string StopServerYes = "Save and stop";
    public const string StopServerNo = "Cancel";
    public const string StopServerClose = "Close";

    public const string StopServerNote =
        "The server saves the world, then quits. Everyone on it is disconnected, you included.";

    public const string StopFactServer = "Server";
    public const string StopFactPlayers = "Players online";
    public const string StopFactStartsAgain = "Starts again";

    /// <summary>F20: what the client cannot know is said, not guessed.</summary>
    public const string PlayersUnknown = "Unknown: this client cannot see who is connected there.";

    public const string PlayersNone = "None that this client can see.";

    /// <summary>At most this many names are listed in the confirmation; the rest are counted.</summary>
    public const int PlayersListed = 6;

    /// <summary>
    /// F20: <c>3 (this client's list, you included): A, B, C</c>. The client world's <c>User</c>
    /// entities are what it knows; they include this player.
    /// </summary>
    public static string PlayerCounter(System.Collections.Generic.IReadOnlyList<string>? names)
    {
        if (names == null) return PlayersUnknown;
        if (names.Count == 0) return PlayersNone;
        var shown = new System.Collections.Generic.List<string>();
        for (int i = 0; i < names.Count && i < PlayersListed; i++) shown.Add(names[i]);
        string list = string.Join(", ", shown) + (names.Count > PlayersListed ? " and " + (names.Count - PlayersListed) + " more" : "");
        return names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " (this client's list, you included): " + list;
    }

    /// <summary>F20: how a local dedicated server starts again. Progenitor does not start it by itself.</summary>
    public const string StartsAgainLocal =
        "Not by itself. Start it with Start server under Preferences › Local server, or the way you usually start it.";

    /// <summary>F20: how a connected (remote) server starts again.</summary>
    public const string StartsAgainRemote = "Not by Progenitor. Your host, panel or script starts it again.";

    /// <summary>The footer's tail after "Name stopped." on each route.</summary>
    public const string StoppedLocalTail = "Start it again with Start server under Preferences › Local server.";

    public const string StoppedRemoteTail = "Your host, panel or script starts it again.";

    /// <summary>
    /// F30 (live 2026-10-08): the configured local dedicated server that Progenitor can start again (Preferences › Local
    /// server › Start server) gets a real restart: the pending bar, the drawer and the confirmation say so.
    /// </summary>
    public const string StopAndStartAgain = "Save, stop and start again";

    /// <summary>F30: the confirmation's heading when Progenitor starts the server again, <c>Restart Name?</c>.</summary>
    public static string RestartServerHeading(string name) => "Restart " + name + "?";

    public const string StartsAgainByProgenitor =
        "Yes: Progenitor starts it again from its folder once it has stopped, and you can rejoin when it's back.";

    /// <summary>F30: the footer's tail after "Name stopped." while Progenitor starts it again.</summary>
    public const string StartingAgainTail = "Starting it again…";

    /// <summary>F30: the footer once Progenitor started the server again.</summary>
    public static string StartedAgainLine(string name) =>
        (string.IsNullOrWhiteSpace(name) ? "The server" : name) + " stopped and started again. Rejoin it when it's back.";

    /// <summary>F30: the footer when the start after the stop failed: the reason, then the manual way.</summary>
    public static string StartAgainFailedLine(string name, string why) =>
        (string.IsNullOrWhiteSpace(name) ? "The server" : name) + " stopped, but Progenitor couldn't start it again: "
        + (why ?? "").Trim().TrimEnd('.') + ". " + StoppedLocalTail;

    /// <summary>F20: the local route's stop needs a connection to that server running Progenitor; why not.</summary>
    public static string StopUnavailable(string reason) =>
        "Progenitor cannot ask it to stop from here: " + (reason ?? "").Trim()
        + " Stop server and Stop server (force) are under Preferences › Local server.";

    public const string RestartConnectedServer = "restart the connected server to apply.";
    public const string RestartBothConnected = "restart the game and the connected server to apply.";
    public const string Install = "Install";
    public const string Installing = "Installing…";
    public const string Installed = "Installed";
    public const string InstalledPending = "Installed · restart to load";
    public const string UpdateRestart = "Update · restart";

    /// <summary>INSTALLCOLS1: a Thunderstore tool's state text; it has no button.</summary>
    public const string ToolNotPlugin = "Tool, not a plugin";

    /// <summary>INSTALLCOLS1: the Status column's text for an install still waiting in <c>progenitor-pending/</c>.</summary>
    public const string StatusPendingLoad = Progenitor.Install.InstallStatusLabels.PendingGameRestart;

    /// <summary>INSTALLCOLS1: the Status column's text for a mod that goes at the next start (ledgered or not).</summary>
    public const string StatusRemovedAtNextStart = Progenitor.Install.InstallStatusLabels.RemovalPendingGameRestart;

    /// <summary>INSTALLCOLS1: the Status column's text for INSTORD1's removed-but-still-loaded row.</summary>
    public const string StatusRemovedStillLoaded = Progenitor.Install.InstallStatusLabels.RemovalPendingGameRestart;

    /// <summary>
    /// INSTALL2 item 2: the mock's small <c>new</c> tag beside <see cref="Installed"/>, for a mod
    /// this session hot loaded.
    ///
    /// <para>INSTALLUX2 item 2 retired it — the user: "Let's get rid of the install new features.
    /// Just change it to an uninstall button." No caption carries the tag any more; the constants
    /// stay only so the <c>listing.newTag</c> readback key can keep saying false.</para>
    /// </summary>
    public const string NewTag = " · new";

    /// <inheritdoc cref="NewTag"/>
    public const string InstalledNew = Installed + NewTag;

    /// <summary>
    /// INSTALLUX2 item 2: an installed row's own button. The row states what it IS (the status
    /// word beside it) and offers the one thing left to do.
    /// </summary>
    public const string Uninstall = "Uninstall";

    /// <summary>
    /// INSTALLUX2 item 2: the button of a row whose install is still only a pending file.
    /// INSTALLCOLS1: says what it cancels (user, 2026-09-29: "maybe cancel should be cancel uninstall").
    /// </summary>
    public const string CancelInstallButton = "Cancel install";

    /// <summary>REMOTEINST1: the button of a row whose server step is still downloading or verifying.</summary>
    public const string CancelRemote = "Cancel";

    /// <summary>REMOTEINST1: the short Status words of a server install that failed; the reason sits in the drawer.</summary>
    public const string RemoteFailedStatus = "Server install failed";

    /// <summary>REMOTEINST1: the confirmation body of a remote install.</summary>
    public const string RemoteInstallBody =
        "Installs on the server you are connected to. The server downloads it from Thunderstore itself; "
        + "a hot-loadable Progenitor mod loads now, anything else at the next server restart.";

    /// <summary>REMOTEINST1: the same, for an update of a server copy.</summary>
    public const string RemoteUpdateBody =
        "Updates the copy on the server you are connected to. The server downloads the new version from Thunderstore itself.";

    /// <summary>REMOTEINST1: the uninstall body of a server copy (remote route).</summary>
    /// <summary>SRVUNINST1: the Client cell's words for a copy in <c>progenitor-reload</c> that no ledger lists (FWALLOW1).</summary>
    public const string InReloadFolder = "in progenitor-reload";

    /// <summary>LOCALONLY1: the Server cell of a listing only this PC's local test source carries, on the remote route.</summary>
    public const string LocalOnlyServerWord = "Local test source only: the server can't fetch it";

    /// <summary>LOCALONLY1: the sentence that says why no server half is installed or offered.</summary>
    /// <summary>F16: the drawer line for a listing the connected server installs from its own local test source.</summary>
    public static string ServerLocalSourceSentence(string version) =>
        "The server installs it from its own local test source" + (string.IsNullOrWhiteSpace(version) ? "." : " (" + version.Trim() + ").");

    public const string LocalOnlyServerSentence =
        "This listing comes from this PC's local test source ([Install] LocalIndexPath); the connected server can't fetch it, so only this client's copy is installed.";

    /// <summary>SRVUNINST1 review: the lead of a whole-row box's sentence about this client's copy that no ledger lists.</summary>
    public const string ThisClientCopy = "This client's copy: ";

    /// <summary>The same copy when nothing here removes it.</summary>
    public const string ThisClientCopyStays = "This client's copy stays.";

    /// <summary>SRVUNINST1 review: the uninstall box's fact label for this client's copy no ledger lists.</summary>
    public const string FactThisClient = "This client";

    /// <summary>That fact's value when nothing here removes the copy.</summary>
    public const string ThisClientCopyStaysValue = "Stays: nothing here can remove this copy.";

    public const string RemoteUninstallBody =
        "Removes it from the server you are connected to. Only files Progenitor installed are removed.";


    /// <summary>INSTALLCOLS1: the button of a row that is removed at the next start. Never a bare "Cancel".</summary>
    public const string CancelUninstall = "Cancel uninstall";

    /// <summary>
    /// INSTALL2 item 2: the button of an uninstalled-but-pending row. INSTALLCOLS1: the row's button
    /// now reads <see cref="CancelUninstall"/>; this stays the action's label and the readback's
    /// <c>reactivate</c> action name, and the confirmation still asks "Keep … installed?".
    /// </summary>
    public const string Reactivate = "Reactivate";

    /// <summary>SETTINGS1 stage 3: the button that cancels a waiting next-start uninstall of a mod another tool installed.</summary>
    public const string PlainCancel = CancelUninstall;

    /// <summary>INSTALL2 item 2: the one-liner an uninstalled-but-pending row shows in place of its description.</summary>
    public const string UninstalledPendingLine = "Uninstalled · removed at the next restart";

    /// <summary>MODS2: the loader row's caption. Disabled, and in the ok tone: it IS installed.</summary>
    public const string LoaderInstalled = "Installed · loader";

    /// <summary>MODS2: the loader row's drawer note.</summary>
    public const string LoaderNote =
        "The plugin loader is already installed; Progenitor never writes BepInEx/core.";
    public const string CannotInstall = "Cannot install";

    // ---- MANAGED1: installed, but not by this manager ------------------------------------

    /// <summary>
    /// MANAGED1: the muted, non-clickable label in the button slot of an installed row the install
    /// ledger does not name (copied by hand, deployed by a script, or installed by another mod
    /// manager). There is no Uninstall for such a row: the user's decision is that this menu only
    /// removes what it installed. Same width as <see cref="InstallRestart"/>.
    /// </summary>
    public const string ManagedElsewhere = "Managed elsewhere";

    /// <summary>MANAGED1: the prefix of <see cref="ManagedBy"/> when the other tool is known.</summary>
    public const string ManagedByPrefix = "Managed by ";

    /// <summary>
    /// MANAGED1: <c>Managed by &lt;tool&gt;</c> when the other manager is known and the caption is no
    /// wider than <see cref="ManagedElsewhere"/>; <see cref="ManagedElsewhere"/> otherwise.
    /// </summary>
    public static string ManagedBy(string? tool)
    {
        string name = (tool ?? "").Trim();
        if (name.Length == 0) return ManagedElsewhere;
        string caption = ManagedByPrefix + name;
        return caption.Length <= ManagedElsewhere.Length ? caption : ManagedElsewhere;
    }

    /// <summary>MANAGED1: the drawer note of a row labeled <see cref="ManagedElsewhere"/>.</summary>
    public const string ManagedElsewhereNote =
        "Installed outside the Progenitor mod manager (by hand, a script or another mod manager), "
        + "so it can't be uninstalled here. To remove it, delete its files from BepInEx/plugins or use "
        + "the tool that installed it.";

    /// <summary>MANAGED1: the line after <see cref="ManagedElsewhereNote"/> that names the detected files.</summary>
    public const string ManagedElsewhereFilesPrefix = "Files: ";

    /// <summary>
    /// MANAGED1: <see cref="ManagedElsewhereNote"/>, plus <c>Files: a, b</c> on a second line when the
    /// installed-state scan found this listing's files. Only the note when none are known.
    /// </summary>
    public static string ManagedElsewhereNoteFor(IReadOnlyList<string>? files)
    {
        if (files == null || files.Count == 0) return ManagedElsewhereNote;
        var known = new List<string>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(files[i])) known.Add(files[i].Trim());
        }

        return known.Count == 0
            ? ManagedElsewhereNote
            : ManagedElsewhereNote + "\n" + ManagedElsewhereFilesPrefix + string.Join(", ", known);
    }

    // ---- INSTORD1: Remove ------------------------------------------------------------
    public const string Remove = "Remove";
    public const string RemoveRestartToUnload = "Remove \u00b7 restart to unload";
    public const string RemovedPending = "Removed";
    public const string RemovedNeedsRestart = "Removed \u2014 still loaded until this process restarts";
    public const string RemoveFailed = "Nothing was removed";

    /// <summary>
    /// POLISH3 item A: the local server's half was on disk and the uninstall could not take it. Said
    /// out loud rather than reported as a success, which is what hid the live defect.
    /// </summary>
    public const string ServerHalfNotTaken = "The local server's copy could not be removed";
    public const string RestartRequired = "Restart required";
    public const string NoRestartNeeded = "No restart needed";
    /// <summary>
    /// Job LIST4 item 4. The two disclosure glyphs every caption in this pane uses, in one
    /// place: WGL4's BLACK RIGHT-POINTING POINTER (U+25BA) and BLACK DOWN-POINTING TRIANGLE
    /// (U+25BC). The small-triangle pair this pane used before — U+25B8 <c>▸</c> and U+25BE
    /// <c>▾</c> — is outside WGL4 and drew TMP's missing-glyph box on the live client
    /// (<c>More info ☐</c>), the same defect job LIST3 fixed for
    /// <see cref="MenuPanelLayout.SortIndicator"/>.
    /// </summary>
    // Live 2026-09-12: U+25BA also drew as TMP's missing-glyph box in the game font, so the
    // expand marker is Latin-1 » (U+00BB), present in every font the game ships.
    public const string GlyphExpand = "»";

    /// <inheritdoc cref="GlyphExpand"/>
    public const string GlyphCollapse = "▼";

    public const string ConfigureInMods = "Configure in Mods " + GlyphExpand;

    public const string TargetClient = "My client (and private games I host)";
    public const string TargetServer = "Server";
    public const string TargetBoth = "Both";
    public const string StaleWarnMark = "⚠ ";

    // ---- Drawer ----------------------------------------------------------------------
    public const string MoreInfo = "More info " + GlyphExpand;
    public const string LessInfo = "Less info " + GlyphCollapse;
    public const string ReadFullDescription = "Read full description";
    public const string FactDependencies = "Dependencies";
    public const string FactPackage = "Package";
    /// <summary>INSTORD2: the plugin GUID a prior inspection recorded, shown at rest without a download.</summary>
    public const string FactPlugin = "Plugin";
    public const string FactUpdateTag = "Update tag";
    public const string FactDownloads = "Downloads";
    public const string LoadsInClient = "my client";
    public const string LoadsInServer = "the server (private games I host, or a dedicated server)";
    public const string LoadsInBoth = "my client and the server";
    public const string LoadsInUnknown = "unknown until checked";
    public const string LoadsInPrefix = "loads in ";
    public const string StaleSuffix = " — may be incompatible with the current patch";
    public const string LoadingDescription = "Loading description…";
    public const string DescriptionUnavailablePrefix = "Full description unavailable";
    public const string NoDescription = "No description.";
    public const string ReadMoreOnThunderstore = "Read more on Thunderstore";
    public const int ReadmePlainCap = MarkdownPlain.DefaultMaxChars;
    public const string DependenciesNone = "none";
    public const string SizeUnknown = "unknown";
    public const string UpdatedUnknown = "unknown";
    public const string UpdateTagNone = "—";
    public const string Dash = "—";

    // ---- Pending bar and footer ------------------------------------------------------
    public const string RestartNow = "Restart now";
    public const string RefreshIndex = "Refresh index";
    public const string CloseMods = "Close Mods";
    public const string OverviewRefresh = "Overview / refresh";

    // ---- Surfaces --------------------------------------------------------------------
    public const string NoSourceEnabled =
        "No catalog source is enabled. Turn Thunderstore on under Configuration, or drop a pre-fetched index.";
    public const string IndexNotLoaded = "The package index is not loaded.";
    public const string Loading = "Loading the package index…";
    public const string ErrorRetry = "Retry";
    public const string NoMatchingListings = "No matching listings.";
    public const string TargetUnavailable = "Local server target is unavailable. Pending changes for it are blocked.";
    public const string DownloadCancelled = "Download canceled — nothing was written";

    // ---- Review copy kept for the planner's ApplyNow reason (AdmissionTests pin it) ----
    public const string MyClient = "My client";
    public const string LocalServerOnly = "Local server only";
    public const string Both = "Both";
    public const string ReviewInstallation = "Review installation";
    public const string TakesEffectAtRestart = "takes effect at next restart";
    public const string ApplyNowUnavailable = "Apply now: not yet available (executor)";

    /// <summary>
    /// HOT2: what an admitted apply-now install actually costs. The reloader scans
    /// <c>progenitor-reload/</c> once a second, so "seconds" is the observed cost of the write plus
    /// that scan — not a promise about the plugin's own load time.
    /// </summary>
    public const string NoRestartLoadsInSeconds = "no restart — loads in seconds";

    /// <summary>
    /// HOT4: the second drawer line of a mixed <c>Both</c> install — the client half is live, the
    /// local server's copy waits for that process's own next start. The sentence is INSTORD2's own
    /// server reason (<see cref="InstalledState.PromotedServerReason"/>) said as a half.
    /// </summary>
    public const string ServerHalfPending = "Local server: installed, loads at its next start";

    /// <summary>HOT2: the folder an admitted confirm writes into, named in the review line.</summary>
    public const string ReloadFolderPrefix = "BepInEx/progenitor-reload/";
    public const string TargetPrefix = "Target: ";
    public const string ConfirmWrites = "Confirm writes ";

    /// <summary>The target caption of the review line.</summary>
    public static string TargetCaption(InstallTarget target) => target switch
    {
        InstallTarget.LocalServer => LocalServerOnly,
        InstallTarget.Both => Both,
        _ => MyClient,
    };

    /// <summary>
    /// The review line: <c>Review installation — Target: My client · takes effect at next restart ·
    /// Confirm writes BepInEx/progenitor-pending/index/x.dll</c>. Readback only on this tab.
    /// </summary>
    /// <param name="executorAvailable">
    /// HOT2: the plan's <see cref="InstallPlan.ExecutorAvailable"/>. With an apply-now decision AND an
    /// executor the line promises <see cref="NoRestartLoadsInSeconds"/> and names the reload file the
    /// confirm writes. Without an executor an apply-now decision keeps the old honest sentence — the
    /// pending file and a restart — because that is what confirming would actually do.
    /// </param>
    public static string ReviewLine(
        InstallTarget target,
        InstallDecision decision,
        string? pendingFileName,
        bool executorAvailable = false)
    {
        bool hot = decision == InstallDecision.ApplyNow && executorAvailable;
        string consequence = decision switch
        {
            InstallDecision.ApplyNow when executorAvailable => NoRestartLoadsInSeconds,
            InstallDecision.ApplyNow => ApplyNowUnavailable + " — " + TakesEffectAtRestart,
            _ => TakesEffectAtRestart,
        };
        string line = ReviewInstallation + " — " + TargetPrefix + TargetCaption(target) + " · " + consequence;
        if (!string.IsNullOrEmpty(pendingFileName))
            line += " · " + ConfirmWrites + (hot ? ReloadFolderPrefix : "BepInEx/progenitor-pending/index/") + pendingFileName;
        return line;
    }

    /// <summary>Toolbar count (LIST2): <c>N listings</c> at the defaults; <c>F listings filtered · M shown</c> when narrowed.</summary>
    public static string CountText(int shown, int filtered, bool atDefaults)
    {
        string n = shown.ToString(CultureInfo.InvariantCulture);
        if (atDefaults || filtered <= 0) return n + " " + (shown == 1 ? Listing : Listings);
        string f = filtered.ToString(CultureInfo.InvariantCulture);
        return f + " " + (filtered == 1 ? Listing : Listings) + " " + Filtered + " · " + n + " " + Shown;
    }

    /// <summary>The small toolbar text while READMEs fetch: <c>descriptions 12/296</c>; empty when idle or done.</summary>
    public static string DescriptionsText(bool running, int done, int total) =>
        running ? Descriptions + " " + done.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>The chip caption: <c>Current update (Oakveil)</c>; the bare label while the current update is unknown.</summary>
    public static string CurrentUpdateChipCaption(string? tag) =>
        string.IsNullOrEmpty(tag) ? CurrentUpdateChip : CurrentUpdateChip + " (" + StripUpdateSuffix(tag) + ")";

    /// <summary><c>Oakveil Update</c> → <c>Oakveil</c> for the chip and the Update column.</summary>
    public static string StripUpdateSuffix(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return "";
        string t = tag.Trim();
        return t.EndsWith(CatalogUpdateTags.Suffix, StringComparison.OrdinalIgnoreCase)
            ? t.Substring(0, t.Length - CatalogUpdateTags.Suffix.Length).TrimEnd()
            : t;
    }

    // ---- INSTALL2 item 3: the y/n confirmations --------------------------------------
    public const string PromptNo = "No";

    /// <summary>LOCALDETECT1 review: the server folder changed while the confirmation was open; nothing was written.</summary>
    public const string ServerFolderChanged =
        "The server folder changed while this box was open (you joined another server), so nothing was written. Open it again to act on the new server.";

    /// <summary>LOCALDETECT1 review: the install's target changed between the box and the write; nothing was written.</summary>
    public const string ServerTargetChanged =
        "Where this install goes changed after you confirmed it (the server connection changed), so nothing was written. Open it again to see where it goes now.";

    public const string PromptYesInstall = "Yes, install";
    public const string PromptYesUninstall = "Yes, uninstall";

    /// <summary>INSTALL2 item 6: the Yes of an uninstall that other installed mods depend on.</summary>
    public const string PromptYesUninstallAnyway = "Yes, uninstall anyway";
    public const string PromptYesKeep = "Yes, keep it";
    public const string PromptYesCancel = "Yes, cancel";

    /// <summary>
    /// INSTALLUX1 item 1: a refused install replaces the body with its reason and leaves one way out.
    /// There is nothing to say yes to, so the box drops <see cref="PromptNo"/> and its Yes for this.
    /// </summary>
    public const string PromptClose = "Close";

    /// <summary>INSTALLUX1 item 1: <c>Cannot install — &lt;reason&gt;</c>, the refused body.</summary>
    public static string CannotInstallBody(string? reason)
    {
        string r = (reason ?? "").Trim();
        return r.Length == 0 ? CannotInstall : CannotInstall + " — " + r;
    }

    /// <summary>INSTALLUX1 item 2: the Configuration row's caption on the Install tab's own copy.</summary>
    public const string DefaultTargetLabel = "Default install target";

    /// <inheritdoc cref="DefaultTargetLabel"/>
    public const string DefaultTargetHelp =
        "Where Install puts a mod. Follow the mod's side sends a server mod to the server and a client mod to your client. "
        + "Server means your configured local server folder when you are not connected or are connected to it, otherwise the "
        + "server you are connected to when you are its admin; with neither, a server mod installs to your client and the row says why.";

    /// <summary>The uninstall body of a hot-loaded mod: the reloader drops it with the file.</summary>
    public const string UninstallHotBody = "Unloads now; nothing to restart.";

    /// <summary>
    /// POLISH3 item A: the same mod with a downloaded copy still waiting on the local server. One Yes
    /// unloads the client's half and deletes that copy, so the body says both.
    /// </summary>
    public const string UninstallHotServerPendingBody =
        "Unloads now on the client and cancels the pending install on the local server.";

    /// <summary>
    /// POLISH3 item A: the same mod with a promoted copy the local server has loaded. That half
    /// cannot be taken from under a running server, so it is marked for its next start.
    /// </summary>
    public const string UninstallHotServerLoadedBody =
        "Unloads now on the client; removed from the local server at its next restart.";

    /// <summary>REQRULE3: the local server's reloader holds its copy too, so the one Yes unloads both now.</summary>
    public const string UninstallHotServerNowBody = "Unloads now on the client and on the local server; nothing to restart.";

    /// <summary>REQRULE3: the local server's copy alone, held by its reloader: removed now.</summary>
    public const string UninstallNowServerBody = "Removed from the local server now; its reloader unloads it, nothing to restart.";

    /// <summary>REQRULE3: the local server's reloader drops its copy now; this game's copy waits for its restart.</summary>
    public const string UninstallServerNowGameLaterBody =
        "Unloads now on the local server; removed from V Rising at its next restart, and it stays loaded on this game until then.";

    /// <summary>
    /// POLISH3 item A: the hot uninstall's body for the local server's half —
    /// <see cref="InstalledState.HalfNone"/>, <see cref="InstalledState.HalfPending"/> or
    /// <see cref="InstalledState.HalfLoaded"/>. Live (deploy #74) the box promised "nothing to
    /// restart" while a server copy it never touched sat in <c>progenitor-pending/</c>.
    /// </summary>
    public static string UninstallHotBodyFor(string? serverHalf) => serverHalf switch
    {
        InstalledState.HalfPending => UninstallHotServerPendingBody,
        InstalledState.HalfLoaded => UninstallHotServerLoadedBody,
        _ => UninstallHotBody,
    };

    /// <summary>The uninstall body of a loaded copy this process cannot drop: the client's half.</summary>
    public const string UninstallRestartBody =
        "Removed at the next restart of V Rising; it stays loaded until then.";

    /// <summary>The same sentence for a copy that only the local server holds (POLISH2 item 3).</summary>
    public const string UninstallRestartServerBody =
        "Removed at the next restart of the local server; it stays loaded until then.";

    /// <summary>The same sentence for a copy both processes hold (POLISH2 item 3).</summary>
    public const string UninstallRestartBothBody =
        "Removed at the next restart of V Rising and the local server; it stays loaded until then.";

    /// <summary>Both uninstall bodies end with this: nothing under <c>BepInEx/config</c> is touched.</summary>
    public const string SettingsKeptBody = "Your settings for it are kept.";

    public const string ReactivateBody = "Cancels the pending removal; nothing changes at the next restart.";
    public const string CancelInstallBody = "Cancels the pending install; the downloaded file is deleted.";

    /// <summary>INSTALL2 item 3: <c>Install CryptSnooze 2.0.4?</c></summary>
    public static string PromptInstallHeading(string name, string version) =>
        "Install " + Named(name, version) + "?";

    /// <summary>INSTALL2 item 3: <c>Uninstall CryptSnooze 2.0.4?</c></summary>
    public static string PromptUninstallHeading(string name, string version) =>
        "Uninstall " + Named(name, version) + "?";

    /// <summary>CONFIRMTARGET2: <c>Update RustyRepairs to 1.2.3?</c></summary>
    public static string PromptUpdateHeading(string name, string version)
    {
        string n = (name ?? "").Trim();
        string v = (version ?? "").Trim();
        return v.Length == 0 ? "Update " + n + "?" : "Update " + n + " to " + v + "?";
    }

    /// <summary>CONFIRMTARGET2: <c>Uninstall RustyRepairs?</c> — the version is in the facts.</summary>
    public static string PromptUninstallShortHeading(string name) => "Uninstall " + (name ?? "").Trim() + "?";

    /// <summary>CONFIRMTARGET2: the update confirmation's Yes.</summary>
    public const string PromptYesUpdate = "Yes, update";

    // ---- CONFIRMTARGET2: the confirmation's facts block -----------------------------------

    public const string FactMod = "Mod";
    public const string FactServer = "Server";
    public const string FactFolder = "Folder";
    public const string FactAddress = "Address";
    public const string FactAlsoOn = "Also on";
    public const string FactTakesEffect = "Takes effect";
    public const string FactRequires = "Requires";
    public const string FactSettings = "Your settings";
    public const string FactServerCopy = "Server copy";

    /// <summary>
    /// F6: the <c>Server</c> fact of the client cell's install box that also offers the server: which answer
    /// writes it (<c>Forms Test (only with Yes, install on both)</c>).
    /// </summary>
    public static string ServerOnlyWith(string server, string bothCaption) => server + " (only with " + bothCaption + ")";

    /// <summary>REQRULE6: the same with the server's kind: <c>Forms Test (on this PC; only with Yes, install on both)</c>.</summary>
    public static string ServerOnlyWith(ServerIdentity server, string bothCaption)
    {
        string name = server.HasName ? server.Name : server.Remote ? ConnectedServerUnnamed : LocalServerUnnamed;
        return name + " (" + (server.Remote ? "remote" : "on this PC") + "; only with " + bothCaption + ")";
    }

    /// <summary>F6: that box's <c>This client</c> fact: when this client's copy loads, which both answers install.</summary>
    public static string ThisClientBothAnswers(string effect) => effect + " (both answers)";

    /// <summary>F6 follow-up: the Server cell box's <c>This client</c> fact when it also offers both: <c>Only with Yes, install on both</c>.</summary>
    public static string ClientOnlyWith(string bothCaption) => "Only with " + bothCaption;

    public const string ThisGameValue = "This game";
    public const string LocalServerUnnamed = "Your local server";
    public const string ConnectedServerUnnamed = "The connected server";
    public const string EffectNow = "Now (hot-loaded)";
    public const string EffectGameRestart = "At the next game restart";
    public const string EffectServerRestart = "At the next server restart";
    public const string EffectBothRestart = "At the next game and server restart";
    public const string EffectMixed = "Now on this game; at the next server restart on the server";
    public const string EffectChecking = "Checking the package…";
    public const string EffectRemote = "Now if it hot-loads, else at the next server restart";

    /// <summary>F33: removing a server copy still waiting in its pending folder.</summary>
    public const string EffectRemoteCancelsPending = "Now (cancels the pending server install)";

    /// <summary>F33 (live 2026-10-08): the install box's <c>Source</c> fact for a listing the server's own local index offers.</summary>
    public const string FactSource = "Source";

    /// <summary>F33: <c>Night Garden's local test source (1.0.0)</c>.</summary>
    public static string ServerLocalSourceFact(string server, string version) =>
        (string.IsNullOrWhiteSpace(server) ? "The server" : server.Trim()) + "'s local test source"
        + (string.IsNullOrWhiteSpace(version) ? "" : " (" + version.Trim() + ")");
    public const string EffectUnloadNow = "Now (unloads)";
    public const string EffectUnloadNowCancelsServer = "Now; also cancels the pending server install";
    public const string EffectUnloadNowServerLater = "Now on this game; at the next server restart on the server";

    /// <summary>REQRULE3: this game's copy waits for its restart while the local server's reloader drops its copy now.</summary>
    public const string EffectGameRestartServerNow = "Now on the server; at the next game restart on this game";
    public const string SettingsKeptValue = "Kept";

    public const string RequiresInstalled = "installed";
    public const string RequiresAlongside = "will be installed alongside";
    public const string RequiresBlocks = "missing (blocks the install)";

    public const string NoteServerRestart = "The server must be restarted for this to load.";
    public const string NoteGameRestart = "The game must be restarted for this to load.";
    public const string NoteBothRestart = "The game and the server must be restarted for this to load.";
    public const string NoteRemovedServerRestart = "It stays loaded until the server restarts.";
    public const string NoteRemovedGameRestart = "It stays loaded until the game restarts.";
    public const string NoteRemovedBothRestart = "It stays loaded until the game and the server restart.";

    /// <summary><c>RustyRepairs 1.2.2 by iTestTeam</c>.</summary>
    public static string ModFactValue(string name, string version, string owner)
    {
        string text = Named(name, version);
        string o = (owner ?? "").Trim();
        return o.Length == 0 || string.Equals(o, "unknown", StringComparison.OrdinalIgnoreCase) ? text : text + " by " + o;
    }

    /// <summary>The <c>Server</c> fact: the name, else what kind of server it is.</summary>
    public static string ServerFactValue(ServerIdentity server) =>
        (server.HasName ? server.Name : server.Remote ? ConnectedServerUnnamed : LocalServerUnnamed) + ServerKindSuffix(server);

    /// <summary>
    /// REQRULE6 (user report 2026-10-08): which kind of server the <c>Server</c> fact names: the configured folder on this PC
    /// (its facts show the folder) or a remote one (its facts show the address).
    /// </summary>
    public static string ServerKindSuffix(ServerIdentity server) => server.Remote ? " (remote)" : " (on this PC)";

    // ---- REQRULE6: where the action goes, in the title and the first fact ---------------------------

    public const string FactInstallsOn = "Installs on";
    public const string FactRemovesFrom = "Removes from";
    public const string ThisGameOnly = "This game only";
    public const string ServerOnly = "The server only";
    public const string ServerOnlyClientHasIt = "The server only — this game already has it";
    public const string ServerOnlyClientKeepsIt = "The server only — this game keeps its copy";
    public const string ServerOnlyNotThisGame = "The server only — not this game";

    /// <summary><c>this game</c>, <c>Progenitor Forms Test</c> or <c>this game and Progenitor Forms Test</c>: the object of "on" / "from".</summary>
    public static string TargetWords(bool client, bool server, string serverName)
    {
        string name = string.IsNullOrWhiteSpace(serverName) ? ServerIdentity.LocalFallback : serverName.Trim();
        return client && server ? ServerIdentity.ThisGame + " and " + name : server ? name : ServerIdentity.ThisGame;
    }

    /// <summary><c>This game and Progenitor Forms Test</c>.</summary>
    public static string BothValue(string serverName) => Capital(TargetWords(true, true, serverName));

    /// <summary>The Client cell's box that also offers both: <c>This game only, or this game and Name with Yes, install on both</c>.</summary>
    public static string ClientOrBothValue(string serverName, string bothCaption) =>
        ThisGameOnly + ", or " + TargetWords(true, true, serverName) + " with " + bothCaption;

    /// <summary>The Server cell's box that also offers both: <c>Name only, or this game and Name with Yes, install on both</c>.</summary>
    public static string ServerOrBothValue(string serverName, string bothCaption) =>
        Capital(TargetWords(false, true, serverName)) + " only, or " + TargetWords(true, true, serverName) + " with " + bothCaption;

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

    /// <summary><c>Install HotDemo 0.1.0 on Progenitor Forms Test?</c></summary>
    public static string PromptInstallHeadingOn(string name, string version, string target) =>
        target.Length == 0 ? PromptInstallHeading(name, version) : "Install " + Named(name, version) + " on " + target + "?";

    /// <summary><c>Update RustyRepairs to 1.2.3 on this game?</c></summary>
    public static string PromptUpdateHeadingOn(string name, string version, string target)
    {
        string h = PromptUpdateHeading(name, version);
        return target.Length == 0 ? h : h.Substring(0, h.Length - 1) + " on " + target + "?";
    }

    /// <summary><c>Uninstall RustyRepairs from this game and Progenitor Forms Test?</c></summary>
    public static string PromptUninstallHeadingFrom(string name, string target) =>
        target.Length == 0 ? PromptUninstallShortHeading(name) : "Uninstall " + (name ?? "").Trim() + " from " + target + "?";

    /// <summary><c>Cancel installing RustyRepairs on Progenitor Forms Test?</c></summary>
    public static string PromptCancelInstallHeadingOn(string name, string target) =>
        target.Length == 0 ? PromptCancelInstallHeading(name) : "Cancel installing " + (name ?? "").Trim() + " on " + target + "?";

    /// <summary><c>Keep RustyRepairs installed on this game?</c></summary>
    public static string PromptReactivateHeadingOn(string name, string target) =>
        target.Length == 0 ? PromptReactivateHeading(name) : "Keep " + (name ?? "").Trim() + " installed on " + target + "?";

    /// <summary>
    /// A <c>Requires</c> value: <c>ExampleCommandFramework 0.10.4 — installed on that server</c>,
    /// <c>… — will be installed alongside</c>, <c>… — missing (blocks the install)</c>.
    /// <paramref name="where"/> is <see cref="InstallSummary.WhereFound"/>'s place, or empty.
    /// </summary>
    public static string RequiresValue(string package, string version, bool present, bool blocks, string? where)
    {
        string name = InstallSummary.NameOf(package);
        string v = (version ?? "").Trim();
        string head = v.Length > 0 ? name + " " + v : name;
        if (blocks) return head + " — " + RequiresBlocks;
        if (!present) return head + " — " + RequiresAlongside;
        string w = (where ?? "").Trim();
        return head + " — " + RequiresInstalled + (w.Length > 0 ? " " + w : "");
    }

    /// <summary>
    /// CONFIRMTARGET1: a confirmation body that touches a server starts with the line naming it —
    /// <see cref="ServerIdentity.ServerLine"/>, or <see cref="ServerIdentity.BothLine"/> when this
    /// game is written too — then the body as before.
    /// </summary>
    public static string WithServerLine(string body, ServerIdentity? server, bool both)
    {
        if (server == null) return body;
        string line = both ? server.BothLine : server.ServerLine;
        return string.IsNullOrEmpty(body) ? line : line + "\n" + body;
    }

    /// <summary>
    /// CONFIRMTARGET1: the local server's fixed words name it when its name is known
    /// (<c>… restart of Progenitor Forms Test …</c>).
    /// </summary>
    public static string NameLocalServer(string body, ServerIdentity? server)
    {
        if (server == null || server.Remote || !server.HasName || string.IsNullOrEmpty(body)) return body;
        return body.Replace(InstallSummary.ServerName, server.Name);
    }

    /// <summary>INSTALL2 item 3: <c>Keep CryptSnooze installed?</c> — the Reactivate confirmation.</summary>
    public static string PromptReactivateHeading(string name) => "Keep " + (name ?? "").Trim() + " installed?";

    /// <summary>INSTALL2 item 3: <c>Cancel installing CryptSnooze?</c></summary>
    public static string PromptCancelInstallHeading(string name) => "Cancel installing " + (name ?? "").Trim() + "?";

    private static string Named(string name, string version)
    {
        string n = (name ?? "").Trim();
        string v = (version ?? "").Trim();
        return v.Length == 0 ? n : n + " " + v;
    }

    /// <summary>INSTALL2 item 6: <c>Other installed mods need it: A, B.</c></summary>
    public static string DependentsWarning(IReadOnlyList<string>? dependents)
    {
        if (dependents == null || dependents.Count == 0) return "";
        return "Other installed mods need it: " + string.Join(", ", dependents) + ".";
    }

    /// <summary>The pending bar with no pending uninstalls.</summary>
    public static string PendingBar(int count) => PendingBar(count, 0);

    // ---- POLISH1 defect H: which process has to restart ------------------------------
    /// <summary>The <c>pending.sides</c> readback values, and what each one asks the player to do.</summary>
    public const string SidesClient = "client";

    /// <inheritdoc cref="SidesClient"/>
    public const string SidesServer = "server";

    /// <inheritdoc cref="SidesClient"/>
    public const string SidesBoth = "both";

    /// <inheritdoc cref="SidesClient"/>
    public const string SidesNone = "none";

    public const string RestartGame = "restart the game to apply.";
    public const string RestartLocalServer = "restart the local server to apply.";
    public const string RestartBoth = "restart the game and the local server to apply.";

    /// <summary>
    /// POLISH1 defect H: the sentence for the sides the pending changes are actually on. Live
    /// (deploy #70) two installs were pending on the local server alone and the bar said "restart the
    /// game to apply" — the one restart that would not have applied them.
    /// </summary>
    public static string RestartSentence(string? sides) => RestartSentence(sides, remote: false);

    /// <summary>The same sentence naming the server the route writes to: the local server, or the connected one.</summary>
    public static string RestartSentence(string? sides, bool remote) => sides switch
    {
        SidesServer => remote ? RestartConnectedServer : RestartLocalServer,
        SidesBoth => remote ? RestartBothConnected : RestartBoth,
        _ => RestartGame,
    };

    /// <summary>
    /// POLISH2 item 3: the uninstall prompt's body for the halves the file is actually on — the same
    /// side rule as <see cref="RestartSentence"/>. Live (deploy #70) AutoLatchGates was installed on
    /// the local server alone and its prompt promised the next restart of V Rising, which would not
    /// have removed anything. <see cref="SidesNone"/> and an unknown value keep the client sentence,
    /// because the client is the process this menu runs in.
    /// </summary>
    public static string UninstallRestartBodyFor(string? sides) => sides switch
    {
        SidesServer => UninstallRestartServerBody,
        SidesBoth => UninstallRestartBothBody,
        _ => UninstallRestartBody,
    };

    /// <summary>
    /// INSTALL2 item 5: the pending bar — <c>2 mods installed · 1 uninstalled — restart the game to
    /// apply. »</c>. The glyph is the pane's own expand marker: the text is a button, and pressing it
    /// lists the pending mods under the bar.
    /// </summary>
    /// <param name="sides">
    /// POLISH1 defect H: <see cref="SidesClient"/> | <see cref="SidesServer"/> |
    /// <see cref="SidesBoth"/> — which processes have to restart for these changes. Null (and
    /// <see cref="SidesNone"/>, which cannot happen while there is anything to count) keeps the
    /// client sentence, so a caller that does not know the sides says what the bar always said.
    /// </param>
    public static string PendingBar(int count, int uninstall, string? sides = null) =>
        PendingBar(count, uninstall, sides, remote: false);

    /// <inheritdoc cref="PendingBar(int, int, string?)"/>
    public static string PendingBar(int count, int uninstall, string? sides, bool remote)
    {
        if (count <= 0 && uninstall <= 0) return "";
        var parts = new List<string>(2);
        if (count > 0)
            parts.Add(count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " mod installed" : " mods installed"));
        if (uninstall > 0) parts.Add(uninstall.ToString(CultureInfo.InvariantCulture) + " uninstalled");
        return string.Join(" · ", parts) + " — " + RestartSentence(sides, remote) + " " + GlyphExpand;
    }

    /// <summary>INSTALL2 item 5: one expanded pending row's state caption.</summary>
    public const string PendingItemToLoad = "Pending restart · to load";

    /// <inheritdoc cref="PendingItemToLoad"/>
    public const string PendingItemToUninstall = "Pending restart · to uninstall";

    /// <summary>
    /// CONFIRMTARGET1: a pending row whose change waits on a server says which one —
    /// <c>Pending restart of Progenitor Forms Test · to load</c>. Null keeps the plain caption.
    /// </summary>
    public static string PendingItemCaption(bool toLoad, ServerIdentity? server)
    {
        if (server == null) return toLoad ? PendingItemToLoad : PendingItemToUninstall;
        return server.PendingRestartOf + (toLoad ? " · to load" : " · to uninstall");
    }

    public static string DescriptionUnavailable(string? reason) =>
        DescriptionUnavailablePrefix + " (" + (string.IsNullOrWhiteSpace(reason) ? "unavailable" : reason.Trim()) + ")";
}

/// <summary>Sort/label facts of one row as the list computes them (game-free, for tests and readback).</summary>
internal readonly struct InstallRowAction
{
    public InstallRowAction(
        InstallListingState state, string label, CellTone tone, bool enabled, bool restart, string reason, string managed = "",
        string plainPluginId = "", string managedWhy = "", string status = "", bool remote = false)
    {
        StatusText = status ?? "";
        Remote = remote;
        ManagedWhy = (managedWhy ?? "").Trim();
        PlainPluginId = plainPluginId ?? "";
        State = state;
        Label = label ?? "";
        Tone = tone;
        Enabled = enabled;
        Restart = restart;
        Reason = reason ?? "";
        Managed = managed ?? "";
    }

    /// <summary>
    /// MANAGED1: the muted label drawn in the button slot of an installed row this manager did not
    /// install (<see cref="InstallModsCopy.ManagedElsewhere"/> / <c>Managed by &lt;tool&gt;</c>).
    /// Empty on every other row.
    /// </summary>
    public string Managed { get; }

    /// <summary>
    /// INSTALLCOLS1: why the preloader cannot remove a managed-elsewhere mod, in a few words
    /// (<c>Shares its folder with another mod</c>) — the Status column's second line. Empty when
    /// unknown and on every other row.
    /// </summary>
    public string ManagedWhy { get; }

    /// <summary>
    /// SETTINGS1 stage 3: a mod another tool installed that the preloader can uninstall at the next
    /// start (its plugin GUID); its button goes to <see cref="InstallModsPane.PlainActionHandler"/>, not
    /// to the ledgered-install prompts. Empty on every other row.
    /// </summary>
    public string PlainPluginId { get; }

    public bool Plain => PlainPluginId.Length > 0;

    /// <summary>
    /// REMOTEINST1: the Status column's words when they are not the state's own (a remote step in
    /// flight, the route an install used, why a server row has no route). Empty everywhere else.
    /// </summary>
    public string StatusText { get; }

    /// <summary>REMOTEINST1: this row acts on the connected server (the remote route), not on a local root.</summary>
    public bool Remote { get; }

    /// <summary>The same action with <see cref="StatusText"/> replaced.</summary>
    public InstallRowAction WithStatus(string status) =>
        new(State, Label, Tone, Enabled, Restart, Reason, Managed, PlainPluginId, ManagedWhy, status, Remote);

    /// <summary>The same action with a short Status label and its full sentence as the drawer note.</summary>
    public InstallRowAction WithStatus(string status, string reason) =>
        new(State, Label, Tone, Enabled, Restart, Reason.Length > 0 ? Reason : reason, Managed, PlainPluginId, ManagedWhy, status, Remote);

    public InstallListingState State { get; }
    public string Label { get; }
    public CellTone Tone { get; }
    public bool Enabled { get; }
    public bool Restart { get; }
    /// <summary>The note under the button: the planner reason when blocked, the error when failed, otherwise empty.</summary>
    public string Reason { get; }
}

/// <summary>
/// Install-mods tab (LIST2): one full-width list view — search, toolbar, filter chips, a
/// sortable column header, one two-line <c>CellRow</c> per listing and a drawer of kit rows
/// under the opened row. Reads <see cref="IInstallCatalog"/> snapshots; catalog/network work
/// stays on the async interface. Installs execute to pending only — <c>progenitor-reload/</c>
/// is never written from here.
/// </summary>
internal sealed partial class InstallModsPane
{
    public const string SearchBy = "description, name, owner";

    /// <summary>HOT3 defect 3: the two reasons this pane asks for a fresh Mods inventory snapshot.</summary>
    public const string InventoryReasonHotLoaded = "hotLoaded";

    /// <inheritdoc cref="InventoryReasonHotLoaded"/>
    public const string InventoryReasonHotUnloaded = "hotUnloaded";

    /// <summary>
    /// POLISH3 item A: an uninstall that took the local server's half. The Mods tab lists a mod from
    /// the ledgers, so a canceled or marked server half changes that list too — live the Mods tab
    /// kept reading <c>pendload</c> after the Uninstall.
    /// </summary>
    public const string InventoryReasonUninstalled = "uninstalled";

    /// <summary>
    /// POLISH3 item A readback <c>removed.halves {client, server}</c>: what the one Yes did to each
    /// half. <c>none</c> — nothing of it was on that side; <c>unloaded</c> — the reload copy went and
    /// the reloader dropped the assembly; <c>removed</c> — the files were deleted; <c>cancelled</c> —
    /// a downloaded pending copy that never loaded was deleted; <c>marked</c> — a loaded copy was
    /// marked for its process's next start; <c>failed</c> — that half refused.
    /// </summary>
    public const string UninstallHalfNothing = "none";

    /// <inheritdoc cref="UninstallHalfNothing"/>
    public const string UninstallHalfUnloaded = "unloaded";

    /// <inheritdoc cref="UninstallHalfNothing"/>
    public const string UninstallHalfRemoved = "removed";

    /// <inheritdoc cref="UninstallHalfNothing"/>
    public const string UninstallHalfCancelled = "cancelled";

    /// <inheritdoc cref="UninstallHalfNothing"/>
    public const string UninstallHalfMarked = "marked";

    /// <inheritdoc cref="UninstallHalfNothing"/>
    public const string UninstallHalfFailed = "failed";
    public const int ListingSnippetChars = 60;
    public const string LayoutAlignment = "left";

    /// <summary>Row keys of the fixed rows; listing rows use the catalog key, drawer rows <see cref="DrawerKeyPrefix"/> + key.</summary>
    public const string ToolbarKey = "toolbar";
    public const string FiltersKey = "filters";
    public const string HeaderKey = "header";
    public const string DrawerKeyPrefix = "drawer:";

    /// <summary>INSTALLSIDES1: the drawer's scrolling body row, under its head.</summary>
    public const string DrawerBodyKeyPrefix = "drawerBody:";

    /// <summary>INSTALLSIDES1: the server picker dialog's tag, kind and key.</summary>
    public const string PickerDialogKind = "server-picker";

    /// <summary>INSTALL2 item 3: the confirmation's own rows — the heading, the body, and the button row.</summary>
    public const string PromptKeyPrefix = "prompt:";

    /// <inheritdoc cref="PromptKeyPrefix"/>
    public const string PendingItemKeyPrefix = "pendingItem:";

    /// <summary>Cell keys.</summary>
    public const string CellIcon = "icon";
    public const string CellName = "name";
    public const string CellAuthor = "author";
    public const string CellSide = "side";
    public const string CellVersion = "version";
    public const string CellReleased = "released";

    /// <summary>INSTALLCOLS1: the Installed date column (INSTALLSIDES1: gone from the row; the drawer's <c>Installed here</c> fact).</summary>
    public const string CellInstalled = "installed";

    /// <summary>INSTALLSIDES1: the row's expand/collapse button, the only thing that opens or closes its drawer.</summary>
    public const string CellExpand = "expand";

    /// <summary>INSTALLSIDES1: the quiet <c>Install on both</c> / <c>Update both</c> shortcut under the mod's name.</summary>
    public const string CellBoth = "both";

    /// <summary>
    /// Chat stage 3 (mod links): the quiet <c>Share</c> button over the right end of the name's first line,
    /// shown while the pointer is on the row; a listing whose key is not a Thunderstore package id keeps a
    /// hidden plain cell under the same key, so the row's cells stay the same set.
    /// </summary>
    public const string CellShare = "share";

    /// <summary>Chat stage 3 (mod links): <b>Share in chat</b> in the drawer head.</summary>
    public const string CellShareDrawer = "shareInChat";

    /// <summary>INSTALLSIDES1: the Client column — the state words and up to two buttons.</summary>
    public const string CellClient = "client";

    /// <inheritdoc cref="CellClient"/>
    public const string CellClientA = "client-a";

    /// <inheritdoc cref="CellClient"/>
    public const string CellClientB = "client-b";

    /// <summary>INSTALLSIDES1: the Server column — the picked server's state words and up to two buttons.</summary>
    public const string CellServer = "server";

    /// <inheritdoc cref="CellServer"/>
    public const string CellServerA = "server-a";

    /// <inheritdoc cref="CellServer"/>
    public const string CellServerB = "server-b";

    /// <summary>INSTALLSIDES1: the drawer head's cells.</summary>
    public const string CellCollapse = "collapse";

    /// <inheritdoc cref="CellCollapse"/>
    public const string CellConfigure = "configure";

    /// <inheritdoc cref="CellCollapse"/>
    public const string CellRestartNow = "restartNow";

    /// <inheritdoc cref="CellCollapse"/>
    public const string CellReadFull = "readFull";

    /// <summary>INSTALLSIDES1: the drawer body's indent and its scrolling text block.</summary>
    public const string CellIndent = "indent";

    /// <inheritdoc cref="CellIndent"/>
    public const string CellBody = "body";

    /// <summary>INSTALLSIDES1: the toolbar's server picker and its label.</summary>
    public const string CellServerLabel = "serverLabel";

    /// <inheritdoc cref="CellServerLabel"/>
    public const string CellServerPick = "serverPick";
    public const string CellGap = "gap";
    public const string CellDownloads = "downloads";

    /// <summary>The Status column's button (the handle kept its LIST2 name, so bridge clicks on <c>&lt;row&gt;/install</c> still work).</summary>
    public const string CellInstall = "install";
    public const string CellSearch = "search";
    public const string CellCount = "count";
    public const string CellDescriptions = "descriptions";
    public const string CellSideCaption = "sideCaption";

    /// <summary>INSTALLFILTERS1: the second filter row (the Only group, the summary, Clear filters).</summary>
    public const string FiltersOnlyKey = "filters-only";

    /// <summary>
    /// INSTALLFILTERS1: on a row too narrow for the Side and Show groups side by side, the Show group
    /// wraps to a row of its own under this key (the readback's <c>filters.rows</c> lists the rows).
    /// </summary>
    public const string FiltersShowKey = "filters-show";

    public const string CellShowCaption = "showCaption";
    public const string CellShowAll = "show-all";
    public const string CellShowInstalled = "show-installed";
    public const string CellShowMissing = "show-missing";
    public const string CellShowUpdate = "show-update";
    public const string CellOnlyCaption = "onlyCaption";
    public const string CellSummary = "summary";
    public const string CellGroupGap = "groupGap";
    public const string CellSideAny = "side-any";
    public const string CellSideServer = "side-server";
    public const string CellSideClient = "side-client";
    public const string CellSideBoth = "side-both";
    public const string CellNoRestart = "norestart";
    public const string CellCurrent = "current";
    public const string CellClear = "clear";
    public const string CellTitle = "title";
    public const string CellAction = "action";

    /// <summary>
    /// Job LIST4 item 1. The drawer head's only cell: the one install fact
    /// (<see cref="InstallModsCopy.RestartRequired"/> / <see cref="InstallModsCopy.NoRestartNeeded"/>).
    /// The head no longer carries an <see cref="CellAction"/> button — the listing row's
    /// Install-column button is the single primary action.
    /// </summary>
    public const string CellNote = "note";
    /// <summary>INSTALL2 item 3: the confirmation's two buttons.</summary>
    public const string CellPromptNo = "promptNo";

    /// <inheritdoc cref="CellPromptNo"/>
    public const string CellPromptYes = "promptYes";

    /// <summary>LIMITS1: the confirmation's "Yes, install on both" / "Yes, update both" button.</summary>
    public const string CellPromptBoth = "promptBoth";

    /// <summary>
    /// INSTALLUX2 item 1: the single button of a refusal dialog (<c>Close</c>). It answers No, but
    /// it is its own handle so a bridge client can tell a refusal box from a y/n box by the cell it
    /// finds.
    /// </summary>
    public const string CellPromptClose = "promptClose";

    /// <summary>
    /// INSTALLUX2 item 2: the status text left of the Status column's button. INSTALLCOLS1: one of
    /// <see cref="RowStatusText"/>'s texts on at most two lines (<c>Installed</c>,
    /// <c>Pending restart · to load</c>, <c>Removed at next start</c>, <c>Managed elsewhere</c>…).
    /// Empty on a row that has no install to state; the cell is always present so every row's
    /// button ends at the same right edge, and it takes the whole column when there is no button.
    /// </summary>
    public const string CellInstallStatus = "installStatus";

    /// <summary>INSTALL2 item 5: one expanded pending row — the listing's name and its pending state.</summary>
    public const string CellPendingName = "pendingName";

    /// <inheritdoc cref="CellPendingName"/>
    public const string CellPendingState = "pendingState";

    /// <summary>Sort keys the header raises (<see cref="CellSpec.SortKey"/>).</summary>
    public const string SortKeyName = "name";
    public const string SortKeyReleased = "released";
    public const string SortKeyDownloads = "downloads";

    /// <inheritdoc cref="SortKeyName"/>
    public const string SortKeyAuthor = "author";

    /// <inheritdoc cref="SortKeyName"/>
    public const string SortKeyInstalled = "installed";

    /// <summary>
    /// Column widths shared by the header and every listing row. INSTALLCOLS1 moved them to
    /// <see cref="InstallColumns"/>, the one column model; these names stay for the callers that
    /// place a drawer row against the list's columns.
    /// </summary>
    public const float AuthorWidth = InstallColumns.AuthorWidth;
    public const float SideWidth = InstallColumns.SideWidth;
    public const float VersionWidth = InstallColumns.VersionWidth;
    public const float ReleasedWidth = InstallColumns.DateWidth;
    public const float InstalledWidth = InstallColumns.InstalledWidth;
    public const float DownloadsWidth = InstallColumns.DownloadsWidth;

    /// <summary>The pending bar's expanded rows: the mod's name takes this share of the row.</summary>
    public const float PendingNameFraction = 0.30f;

    /// <summary>The toolbar's search field (LIST3 defect 3): the mock's ≈ 380 px at the left of the toolbar.</summary>
    public const float SearchCellWidth = 380f;

    /// <summary>Rows the icon request covers beyond the viewport estimate, above and below.</summary>
    public const int IconLookahead = 10;
    public const int DefaultViewportRows = 16;

    /// <summary>Pinned rows above the list: the toolbar (with the search field), the filter row and the column header.</summary>
    public const int TopChromeRows = 3;

    /// <summary>Pinned rows under the list without a pending bar: none (LIST3 item 9 — Escape and the native tabs close the workspace).</summary>
    public const int FooterRows = 0;
    public const int PendingBarRows = 2;

    /// <summary>The Install pane's layout: the whole frame width, no sidebar column, no pane title (LIST3 items 6 and 8).</summary>
    public const MenuPaneMode PaneMode = MenuPaneMode.FullWidth;

    /// <summary>
    /// How many two-line listing rows fit the list viewport of a pane <paramref name="paneHeight"/>
    /// tall (whole rows; the pinned top chrome and, when there is one, the pending bar are
    /// reserved first). The same arithmetic <c>UnityPanel</c> insets the viewport with.
    /// </summary>
    public static int VisibleListingRows(float paneHeight, bool pendingBar = false)
    {
        float list = ListViewportHeight(paneHeight, pendingBar);
        float pitch = CellLayout.TwoLineRowHeight + PanelLayout.RowGap;
        return (int)Math.Floor((list + PanelLayout.RowGap + .01f) / pitch);
    }

    /// <summary>The list viewport in px for a pane <paramref name="paneHeight"/> tall.</summary>
    public static float ListViewportHeight(float paneHeight, bool pendingBar = false)
    {
        float viewport = MenuPanelLayout.PaneViewportHeight(paneHeight, PaneMode)
            - MenuPanelLayout.ChromeHeight(TopChromeRows)
            - MenuPanelLayout.ChromeHeight(FooterRows + (pendingBar ? PendingBarRows : 0));
        return viewport < 0f ? 0f : viewport;
    }

    /// <summary>
    /// Hook for the package inspector (LIST2): once a package's <c>[BepInProcess]</c> is known
    /// the probe answers its side; null (the default) leaves the listing's derived side.
    /// </summary>
    public static Func<CatalogEntry, PackageSide?>? ProcessFilterProbe { get; set; }

    /// <summary>Per-catalog state that outlives one pane: filters, sort, READMEs, the fetch run, the open drawer and this session's installs.</summary>
    private sealed class ListState
    {
        public readonly InstallSearchFilters Filters = new();
        public readonly Dictionary<string, string> ReadmeIndex = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ReadmeUnavailable = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> SessionPending = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> SessionInstalled = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>INSTORD1: listings whose ledgered files this session removed while they were loaded.</summary>
        public readonly HashSet<string> SessionRemoved = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// HOT3: listings this session wrote straight into <c>progenitor-reload/</c>
        /// (<see cref="ExecuteCallResult.HotLoaded"/>). Their Remove is a hot unload, so the row says
        /// <see cref="InstallModsCopy.Remove"/> and not <c>Remove · restart to unload</c> even in the
        /// second before the reloader's scan picks the file up.
        /// </summary>
        public readonly HashSet<string> SessionHotLoaded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// HOT3 live (deploy #65): listings this session hot unloaded. The reloader drops the
        /// assembly about a second after the file goes and the Mods inventory snapshot this pane was
        /// built with still names the plugin, so a rebuild immediately after the Remove read that
        /// stale evidence back as installed. A key in here is not loaded until something installs it
        /// again, whatever the probe and the snapshot still say.
        /// </summary>
        public readonly HashSet<string> SessionHotUnloaded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// INSTALL2 item 4: listings this session marked for removal at the next start. The ledger
        /// flag is the durable record (<see cref="UninstallMarkers"/>); this is what a pane rebuilt
        /// before the ledger could be re-read still knows, and it is the only evidence a catalog
        /// without a ledger view (the tests' fake) has.
        /// </summary>
        public readonly HashSet<string> SessionUninstallPending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>INSTALL2 item 3: the confirmation open under a row, and which row it belongs to.</summary>
        public InstallPromptKind Prompt;

        /// <inheritdoc cref="Prompt"/>
        public string? PromptKey;

        /// <summary>INSTALL2 item 5: the pending bar's list is expanded.</summary>
        public bool PendingOpen;
        public ReadmePrefetchScheduler? Prefetch;
        public IconCache? Icons;
        public ListingDiskCache? Disk;
        /// <summary>
        /// DESCCACHE1: README index keys (<c>key@version</c>) already looked up in the disk cache for
        /// the current <see cref="Disk"/>. Replaces a single "adopted" flag, which was set by the first
        /// build even when the index had not finished loading, so every listing that arrived later was
        /// never read from disk and was downloaded again on every game start (live 2026-09-29:
        /// "descriptions 49/299" with 310 README files already cached).
        /// </summary>
        public readonly HashSet<string> DiskChecked = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// README-STRUCT1: README markdown by index key (<c>key@version</c>) — this session's fetches
        /// and the <c>.md</c> files read back from the disk cache — so the drawer keeps headings and
        /// code when the catalog's in-memory documents are gone (a new process or Core generation).
        /// </summary>
        public readonly Dictionary<string, string> ReadmeMarkdown = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>README-STRUCT1: index keys whose <c>.md</c> file was already looked for.</summary>
        public readonly HashSet<string> MarkdownChecked = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// README-STRUCT1: index keys whose drawer had only the plain text and asked the network once
        /// for the markdown (a README cached before the <c>.md</c> files existed). Never asked twice.
        /// </summary>
        public readonly HashSet<string> StructureFetchTried = new(StringComparer.OrdinalIgnoreCase);
        public string? OpenKey;

        /// <summary>
        /// INSTALLSIDES1: the listing whose drawer is open. Only its expand button (and the drawer's
        /// own Collapse) changes it; the flow listing (<see cref="OpenKey"/>) follows the buttons.
        /// </summary>
        public string? DrawerKey;

        /// <summary>INSTALLSIDES1: the server picker's choice (an entry id), or null for the default.</summary>
        public string? PickedServer;

        /// <summary>INSTALLSIDES1: listings whose last write this session went to the server alone.</summary>
        public readonly HashSet<string> SessionServerOnly = new(StringComparer.OrdinalIgnoreCase);

        public bool MoreInfo;
        public bool FullDescription;

        /// <summary>INSTALLSIMPLE1: the Simple view's Show chip (Advanced keeps its own in <see cref="Filters"/>).</summary>
        public InstallSimpleShow SimpleShow;

        /// <summary>
        /// INSTALLSIMPLE1: Simple's sort (Mod, Released or Downloads; newest released by default) and whether
        /// it runs against the column's natural direction. Advanced keeps its own in <see cref="Filters"/>.
        /// </summary>
        public InstallSort SimpleSort = InstallSort.Released;

        /// <inheritdoc cref="SimpleSort"/>
        public bool SimpleSortReversed;
    }

    private static readonly ConditionalWeakTable<IInstallCatalog, ListState> ProcessState = new();

    private readonly IInstallCatalog _catalog;
    private readonly ListState _state;
    private readonly Action<Action> _post;
    private readonly ICatalogClock? _clock;
    private bool _indexLoadAttempted;
    private string? _lastError;
    private string _search = "";
    private InstallTarget _target = InstallTarget.Client;
    private LocalServerFolder? _server;
    // REMOTEINST1: the open Install box is a remote-only install (nothing is written here), and the
    // listing whose server half follows a local Both install to the connected server.
    private bool _promptRemoteOnly;

    /// <summary>CONFIRMTARGET2: the open install box updates a copy that is already there.</summary>
    private bool _promptUpdate;

    /// <summary>CONFIRMTARGET2: the open install box is an update (remote or local).</summary>
    internal bool IsUpdatePrompt =>
        _state.Prompt == InstallPromptKind.Install
        && (_promptUpdate || (_promptRemoteOnly && _promptRemoteOp == RemoteInstallOp.Update));
    private RemoteInstallOp _promptRemoteOp = RemoteInstallOp.Install;
    private string? _remoteAfterLocal;
    // REMOTEINST1 per-side: the half an "Install on server/client" or "Update …" press writes.
    private InstallTarget? _targetOverride;
    private InstalledStateQuery? _installedQuery;
    // HOT3: the reloader's loaded-plugin report for this build (null = not read yet).
    private IReadOnlyList<string>? _loadedNames;
    private CurrentUpdateResolution? _currentUpdate;
    private int _lastShown;
    private int _lastFiltered;
    private int _lastTotal;
    private int _viewportRows = DefaultViewportRows;
    private Action? _detailRebuild;
    private Action<string>? _openInstalled;
    private bool _restartRequested;

    // LIST3 item 10: true while an automatic index refresh is in flight (the surface reads Loading until entries exist).
    private bool _indexRefreshing;

    // Built handles (one pane build).
    private IUiPanel? _panel;
    private IUiCellRow? _toolbar;
    private IUiCellRow? _filters;

    /// <summary>INSTALLFILTERS1: the second filter row (Only, the summary, Clear filters).</summary>
    private IUiCellRow? _filtersOnly;

    /// <summary>INSTALLFILTERS1: every filter row as built, and the row each filter cell is on.</summary>
    private readonly List<IUiCellRow> _filterRows = new();
    private readonly Dictionary<string, IUiCellRow> _filterRowOf = new(StringComparer.Ordinal);

    private void OnFilterClick(string key, Action action)
    {
        if (_filterRowOf.TryGetValue(key, out var row)) row.OnClick(key, action);
    }

    private void SetFilterCell(string key, string? text = null, CellTone? tone = null, bool? visible = null)
    {
        if (!_filterRowOf.TryGetValue(key, out var row)) return;
        try { row.SetCell(key, text: text, tone: tone, visible: visible); }
        catch { /* */ }
    }

    /// <summary>The filter rows as built (one to three, see <see cref="FilterRowsFor"/>).</summary>
    internal IReadOnlyList<IUiCellRow> FilterRows => _filterRows;
    private IUiCellHeader? _header;
    private IUiLabel? _emptyLabel;
    private readonly List<(IUiCellRow Row, CatalogEntry Entry, string? IconUrl)> _rows = new();
    private readonly Dictionary<string, int> _rowIndex = new(StringComparer.OrdinalIgnoreCase);
    // ICON2: the URL asked for and when, so a Failed entry is asked again after IconCache.RetryAfter
    // instead of being gated out of every later RequestVisibleIcons for the life of the build.
    private readonly Dictionary<string, DateTime> _iconRequested = new(StringComparer.Ordinal);
    private readonly List<IUiHandle> _drawerHandles = new();
    private IUiCellRow? _drawerHead;
    private IUiLabel? _drawerReason;
    private IUiHandle? _readmeFirst;
    private readonly List<IUiHandle> _pendingBar = new();
    private int _readmeBlockCount;
    private CancellationTokenSource? _iconCts;

    // Drawer documents (the opened listing's README).
    private string _docsKey = "";
    private ListingDocState _readmeState = ListingDocState.Idle;
    private string? _readmeReason;
    private string _readmePlain = "";

    // Primary flow (DL1). The phase belongs to the opened listing: a selection change or a
    // pane teardown bumps _pageToken so a late completion for another page is dropped.
    private InstallActionPhase _phase = InstallActionPhase.Idle;
    private int _pageToken;
    private CancellationTokenSource? _downloadCts;
    private Task? _activeDownload;
    private string? _downloadKey;
    private string? _downloadVersion;
    private string? _actionError;
    private ServerInstallResult? _lastRemoveResult;

    /// <summary>POLISH3 item A: what the last Uninstall did to each half. See <see cref="UninstallHalfNothing"/>.</summary>
    private (string Client, string Server) _lastUninstallHalves =
        (UninstallHalfNothing, UninstallHalfNothing);
    private string? _approvedHash;
    private InstallTarget _approvedTarget = InstallTarget.Client;
    private InstallPlan? _lastPlan;
    private ExecuteCallResult? _executeResult;
    private string? _resultSha256;
    private int _droppedCompletions;
    private bool _dropLogged;

    // INSTALLUX1 item 1: the confirmation opens on the click, before the download and inspection
    // that produce the plan. _promptPlanReady says whether that work has finished (readback
    // plan.ready); _promptRefusal replaces the body when it refused; _promptYesPending remembers a
    // Yes the player pressed while it was still running, so it is honored the moment it lands.
    private bool _promptPlanReady;
    private string? _promptRefusal;
    private bool _promptYesPending;

    // INSTALL2 item 3: the planned set behind the open Install confirmation (DEPS1), the set that
    // was written, and the dependents the open Uninstall confirmation has to warn about (item 6).
    private InstallPlanSet? _planSet;
    private ExecuteSetResult? _executeSet;
    private IReadOnlyList<string> _promptDependents = Array.Empty<string>();
    private Task? _activeSetPlan;

    public InstallModsPane(IInstallCatalog catalog)
        : this(catalog, null, null)
    {
    }

    /// <summary>
    /// <paramref name="post"/> marshals fetch results onto the game main thread (ModsMenu
    /// passes <c>MainThread.Post</c>); tests leave it null and run inline. <paramref name="clock"/>
    /// paces the README fetch (<see cref="ReadmePrefetchScheduler.MinSpacing"/>).
    /// </summary>
    public InstallModsPane(IInstallCatalog catalog, ICatalogClock? clock, Action<Action>? post)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _state = ProcessState.GetValue(_catalog, _ => NewListState());
        _post = post ?? (action => action());
        _clock = clock;
        _state.Prefetch ??= new ReadmePrefetchScheduler(
            (key, ct) => _catalog.LoadListingDocumentsAsync(key, ct),
            clock,
            _post);
    }

    // ---- Settings organization stage 3: remembered filters and sort -----------------------------

    /// <summary>
    /// The remembered filters and sort (<see cref="InstallFilterMemory"/>), read when a catalog's list state is first
    /// made in a session, or null when nothing is remembered (the Configuration tab's switch is off). Set by the Mods
    /// menu; null (tests, an older wiring) starts from the defaults.
    /// </summary>
    internal static Func<string?>? RememberedFiltersLoad { get; set; }

    /// <summary>Writes the filters and sort to remember; the Mods menu's sink stores them only while the switch is on.</summary>
    internal static Action<string>? RememberedFiltersSave { get; set; }

    private static ListState NewListState()
    {
        var state = new ListState { PickedServer = RememberedPick };
        string? text = null;
        try { text = RememberedFiltersLoad?.Invoke(); }
        catch { text = null; }
        if (InstallFilterMemory.TryDecode(text, out var snapshot))
        {
            snapshot.ApplyTo(state.Filters);
            state.SimpleShow = snapshot.SimpleShow;
            state.SimpleSort = snapshot.SimpleSort;
            state.SimpleSortReversed = snapshot.SimpleSortReversed;
        }

        return state;
    }

    /// <summary>The filters and sort on show now, as remembered text.</summary>
    internal string FilterMemoryText() =>
        InstallFilterMemory.Encode(InstallFilterSnapshot.Of(_state.Filters, _state.SimpleShow, _state.SimpleSort, _state.SimpleSortReversed));

    /// <summary>Hand the filters and sort on show to <see cref="RememberedFiltersSave"/> (a tab switch, the workspace closing).</summary>
    internal void SaveFilterMemory()
    {
        var save = RememberedFiltersSave;
        if (save == null) return;
        try { save(FilterMemoryText()); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Install: remembering the filters threw: " + e.Message); }
    }

    // ---- Wiring ---------------------------------------------------------------------

    /// <summary>Filter row state. Shared per catalog instance for the process; on disk only while the Configuration tab's Remember switch is on.</summary>
    public InstallSearchFilters Filters => _state.Filters;

    public InstallSort Sort => _state.Filters.Sort;

    public ReadmePrefetchScheduler Prefetch => _state.Prefetch!;

    /// <summary>The listing-icon cache (per catalog for the process). Null: image cells keep the placeholder.</summary>
    public IconCache? Icons => _state.Icons;

    public void SetIconCache(IconCache? icons)
    {
        if (ReferenceEquals(_state.Icons, icons)) return;
        if (_state.Icons != null)
        {
            try { _state.Icons.Arrived -= OnIconArrived; }
            catch { /* */ }
        }
        _state.Icons = icons;
        if (icons != null) icons.Arrived += OnIconArrived;
    }

    /// <summary>The on-disk README/icon memory. Null (tests): memory only.</summary>
    public void SetDiskCache(ListingDiskCache? disk)
    {
        if (ReferenceEquals(_state.Disk, disk)) return;
        _state.Disk = disk;
        _state.DiskChecked.Clear();
        _state.MarkdownChecked.Clear();
    }

    public ListingDiskCache? DiskCache => _state.Disk;

    /// <summary>How many listing rows the viewport shows; icons are requested for that many plus <see cref="IconLookahead"/>.</summary>
    public void SetViewportRows(int rows) => _viewportRows = rows < 1 ? 1 : rows;

    public int ViewportRows => _viewportRows;

    /// <summary>Phase-change hook: ModsMenu posts a pane rebuild on the next Drain; tests rebuild inline or observe the state.</summary>
    public void SetDetailRebuild(Action? rebuild) => _detailRebuild = rebuild;

    /// <summary>The <c>Configure in Mods ►</c> route: ModsMenu switches the strip to Mods and selects the plugin.</summary>
    public void SetOpenInstalled(Action<string>? open) => _openInstalled = open;

    private static LedgerIdentityProbe MemoizeIdentity(LedgerIdentityProbe probe)
    {
        if (probe.Target is IdentityMemo) return probe;
        return new IdentityMemo(probe).Read;
    }

    private sealed class IdentityMemo
    {
        private readonly LedgerIdentityProbe _probe;
        private readonly Dictionary<CatalogEntry, string?> _answers = new(ReferenceEqualityComparer.Instance);

        internal IdentityMemo(LedgerIdentityProbe probe) => _probe = probe;

        internal string? Read(CatalogEntry entry)
        {
            if (entry == null) return _probe(entry!);
            if (_answers.TryGetValue(entry, out var known)) return known;
            string? answer = _probe(entry);
            _answers[entry] = answer;
            return answer;
        }
    }

    /// <summary>
    /// True once the installed-state scan (ledgers, files, inventory) has been handed in. Before that
    /// every side reads as absent for want of evidence, not because nothing is installed: a mod page
    /// opened before the Install tab must scan first (<c>ModsMenu.PageSideCells</c>), never draw that.
    /// </summary>
    internal bool HasInstalledQuery => _installedQuery != null;

    public void SetInstalledQuery(InstalledStateQuery? query)
    {
        // POLISH4 item 2: a query handed in without an identity probe gets this pane's catalog
        // service's. One place, so no caller can supply a query whose ledger rows the pane reads with
        // a weaker comparison than its Uninstall uses — that mismatch is the whole POLISH4 defect.
        var service = Service;
        if (query != null && query.IdentityProbe == null && service != null)
            query = query.WithIdentityProbe(service.IdentityProbe);
        // The identity route reads progenitor-cache/identity from disk, and a build asks it for every
        // listing against every ledger row, several times over. One answer per listing per scan: every
        // build hands in a fresh query, so an inspection that writes an identity is seen by the next one.
        if (query?.IdentityProbe != null)
            query = query.WithIdentityProbe(MemoizeIdentity(query.IdentityProbe));
        _installedQuery = query;
        ForgetBuildMemo();
        InvalidateSideCache();
        // HOT3: the reloader's report is read once per build, beside the file/ledger scan this query
        // is, so a full-index pass (PendingCount over hundreds of rows) costs one call, not one each.
        _loadedNames = null;
    }

    /// <summary>
    /// POLISH1 defect C: re-reconcile the install ledgers of both roots and, when one moved, re-take
    /// everything this build had already read from them. Called at the top of <see cref="Build"/>,
    /// which is every menu open and every tab select.
    ///
    /// <para>The order matters and is the whole defect. The menu scans the roots
    /// (<c>CurrentInstalledQuery</c>) and then builds; a reconcile that runs inside the build is
    /// therefore behind the snapshot the rows are drawn from, so a promote it just recorded would
    /// first appear one rebuild later — or never, since nothing else asks. So a reconcile that changed
    /// something re-scans here and hands the pane the new query.</para>
    ///
    /// <para>Returns true when a ledger changed. Never throws: a stale row is a wrong caption, a
    /// throwing one is no tab at all.</para>
    /// </summary>
    internal bool RecheckLedgers()
    {
        bool changed;
        var probe = LedgerRecheckProbe;
        if (probe != null)
        {
            try { changed = probe(); }
            catch { return false; }
        }
        else
        {
            var service = Service;
            if (service == null) return false;
            try { changed = service.ReconcileChangedLedgers().Changed; }
            catch { return false; }
        }

        if (!changed) return false;
        ForgetLoadedPlugins();
        ForgetMarkedPackages();
        RescanInstalledQuery();
        return true;
    }

    /// <summary>
    /// POLISH1 defect C: the reconcile pass. Defaults to the catalog service's own
    /// <c>ReconcileChangedLedgers</c>; a test substitutes one and answers whether it changed anything.
    /// </summary>
    internal Func<bool>? LedgerRecheckProbe { get; set; }

    /// <summary>
    /// POLISH1 defect C: the re-scan a changed ledger needs. Defaults to scanning both of the
    /// service's roots again, carrying this build's inventory rows over (the inventory is the menu's,
    /// and a promote on disk does not change it). A test substitutes one, or leaves it null with no
    /// service so its synthetic query stands.
    /// </summary>
    internal Func<InstalledStateQuery?>? InstalledQueryRescan { get; set; }

    private void RescanInstalledQuery()
    {
        var probe = InstalledQueryRescan;
        if (probe != null)
        {
            try
            {
                var replacement = probe();
                if (replacement != null) SetInstalledQuery(replacement);
            }
            catch { /* the previous scan stands */ }
            return;
        }

        var service = Service;
        if (service == null) return;
        try
        {
            SetInstalledQuery(InstalledState.Scan(
                service.ClientBepInExRoot,
                service.ServerBepInExRoot,
                _installedQuery?.Inventory,
                _installedQuery?.LoadedGeneration,
                // POLISH4 item 2: the identity route, so a ledger row an older build wrote under the
                // plugin id is a row this scan can join back to its listing.
                service.IdentityProbe));
        }
        catch { /* the previous scan stands */ }
    }

    /// <summary>
    /// HOT3 defect 1: the plugin names the reloader reports loaded right now. Defaults to
    /// <see cref="Reload.PluginReloader.LoadedPluginNames"/> — the same seam HOT2's admission
    /// snapshot reads the loaded plugin through, and the only evidence that is current rather than as
    /// old as this pane's build. A test substitutes a list; never throws.
    /// </summary>
    internal Func<IReadOnlyList<string>>? LoadedPluginsProbe { get; set; }

    /// <summary>
    /// HOT3 defect 3: called with <c>hotLoaded</c> / <c>hotUnloaded</c> when this session's install or
    /// remove changed what is loaded in this process, so <c>ModsMenu</c> can retake
    /// <see cref="ModMenuInventory.Snapshot"/> instead of leaving the Mods tab drawn from the list the
    /// workspace opened with. Null (tests, and any pane without a menu) does nothing.
    /// </summary>
    internal Action<string>? InventoryRefresh { get; set; }

    /// <summary>
    /// HOT3 live (deploy #65): the pane's delayed marshaler — <c>MainThread.PostDelayed</c>, wired by
    /// <c>ModsMenu</c>. Used for the one re-check after a hot unload, so a probe result that was still
    /// stale at the rebuild is corrected without polling. Null (tests, unless substituted) schedules nothing.
    /// </summary>
    internal Action<double, Action>? PostDelayedHook { get; set; }

    /// <summary>
    /// How long after a hot unload the pane looks once more. The reloader scans
    /// <c>progenitor-reload/</c> once a second, so two seconds is one scan plus slack — not a promise
    /// about the unload, which the row already reflects through <see cref="ListState.SessionHotUnloaded"/>.
    /// </summary>
    public const double HotUnloadRecheckSeconds = 2d;

    /// <summary>
    /// SRVUNINST1 (live 2026-10-07): a fresh installed-state scan with the inventory read again (the Mods
    /// menu's). <see cref="InstalledQueryRescan"/> carries the build's inventory over, which still lists a
    /// plugin the reloader has just unloaded. Null falls back to <see cref="RescanInstalledQuery"/>.
    /// </summary>
    internal Func<InstalledStateQuery?>? FreshInstalledQueryProbe { get; set; }

    /// <summary>
    /// SRVUNINST1 (live 2026-10-07: after HotDemo's reload-folder copy was deleted and unloaded, its
    /// Client cell still read "Installed 0.1.0 · Managed elsewhere" 8 s later): a client copy outside
    /// the ledger was removed. The rows read a fresh scan now, and once more after the reloader's next
    /// scan has unloaded it (<see cref="HotUnloadRecheckSeconds"/>), since the build's scan predates both.
    /// </summary>
    internal void NoteClientCopyRemoved()
    {
        Rescan();
        var post = PostDelayedHook;
        if (post == null) return;
        // Review: a released pane (the workspace closed) or a new page bumped the token; the re-check belongs to neither.
        int token = _pageToken;
        try { post(HotUnloadRecheckSeconds, () => { if (token == _pageToken) Rescan(); }); }
        catch { /* a re-check is evidence, never a gate */ }

        void Rescan()
        {
            try
            {
                var fresh = FreshInstalledQueryProbe;
                if (fresh == null) RescanInstalledQuery();
                else if (fresh() is { } query) SetInstalledQuery(query);
            }
            catch { /* the last scan stands */ }

            ForgetLoadedPlugins();
            try { NoteRemoteChanged(); }
            catch { /* a row that went away with a rebuild */ }
            RequestDetailRebuild();
        }
    }

    /// <summary>HOT3: the <see cref="HotUnloadRecheckSeconds"/> re-check. Dropped if the pane was released or the selection moved.</summary>
    private void ScheduleHotUnloadRecheck()
    {
        var post = PostDelayedHook;
        if (post == null) return;
        int token = _pageToken;
        try
        {
            post(HotUnloadRecheckSeconds, () =>
            {
                // A released pane or a new page bumped the token; this rebuild belongs to neither.
                if (token != _pageToken) return;
                ForgetLoadedPlugins();
                RequestDetailRebuild();
            });
        }
        catch { /* a re-check is evidence, never a gate on the removal that asked for it */ }
    }

    /// <summary>The reloader's current report, or empty. Evidence, never a gate: a failure is empty.</summary>
    internal IReadOnlyList<string> LoadedPluginNames()
    {
        if (_loadedNames != null) return _loadedNames;
        try
        {
            var probe = LoadedPluginsProbe;
            if (probe != null) _loadedNames = probe() ?? (IReadOnlyList<string>)Array.Empty<string>();
#if PROGENITOR_TESTS
            // The reloader is a game-side type the test build does not compile; a test supplies the probe.
            else _loadedNames = Array.Empty<string>();
#else
            else _loadedNames = Reload.PluginReloader.LoadedPluginNames();
#endif
        }
        catch { _loadedNames = Array.Empty<string>(); }
        return _loadedNames;
    }

    /// <summary>HOT3: drop the cached reloader report, so the next read is after this install/remove.</summary>
    private void ForgetLoadedPlugins()
    {
        _loadedNames = null;
        ForgetBuildMemo();
    }

    // Build-scoped answers (see Build): null outside a build, so every other caller reads live.
    private Dictionary<CatalogEntry, InstalledStateResult>? _detectMemo;
    private Dictionary<CatalogEntry, bool>? _ledgerMemo;

    private void BeginBuildMemo()
    {
        _sideMemo = new Dictionary<string, SideCellsDecision>(StringComparer.OrdinalIgnoreCase);
        _detectMemo = new Dictionary<CatalogEntry, InstalledStateResult>(ReferenceEqualityComparer.Instance);
        _detectClientMemo = new Dictionary<CatalogEntry, InstalledStateResult>(ReferenceEqualityComparer.Instance);
        _ledgerMemo = new Dictionary<CatalogEntry, bool>(ReferenceEqualityComparer.Instance);
        _installedMemo = new Dictionary<CatalogEntry, InstallTime>(ReferenceEqualityComparer.Instance);
    }

    private void EndBuildMemo()
    {
        _sideMemo = null;
        _detectMemo = null;
        _detectClientMemo = null;
        _ledgerMemo = null;
        _installedMemo = null;
    }

    private void ForgetBuildMemo()
    {
        _sideMemo?.Clear();
        _detectMemo?.Clear();
        _ledgerMemo?.Clear();
        _installedMemo?.Clear();
    }

    /// <summary>
    /// HOT3: one installed-state read for the pane — the scanned files, ledger and inventory of this
    /// build plus the reloader's live report. The listing's cached plugin GUID is deliberately not
    /// read here: <see cref="RestingIdentity"/> goes to the identity cache on disk, and this runs for
    /// every visible row of every build and for every row of <see cref="PendingCount"/>.
    /// </summary>
    private InstalledStateResult DetectInstalled(CatalogEntry entry)
    {
        if (_detectMemo != null && entry != null)
        {
            if (!_detectMemo.TryGetValue(entry, out var known))
                _detectMemo[entry] = known = DetectInstalledNow(entry);
            return known;
        }
        return DetectInstalledNow(entry!);
    }

    private Dictionary<CatalogEntry, InstalledStateResult>? _detectClientMemo;

    /// <summary>
    /// INSTALLSIDES2 item 6: this client's own evidence only — the client root's pending and reload
    /// files, the client ledger rows, and what this process loaded. <see cref="DetectInstalled"/>
    /// reads both roots for the listing as a whole; a promoted copy on the local server read through
    /// it as "loaded" made the Client cell say <c>Installed 0.1.0 · Managed elsewhere</c> for a mod
    /// this client did not have (live 2026-10-03, HotDemo).
    /// </summary>
    internal InstalledStateResult DetectClient(CatalogEntry entry)
    {
        if (_detectClientMemo != null && entry != null)
        {
            if (!_detectClientMemo.TryGetValue(entry, out var known))
                _detectClientMemo[entry] = known = DetectClientNow(entry);
            return known;
        }
        return DetectClientNow(entry!);
    }

    private InstalledStateResult DetectClientNow(CatalogEntry entry)
    {
        var names = HotUnloadedNow(entry) ? Array.Empty<string>() : LoadedPluginNames();
        var query = (_installedQuery ?? new InstalledStateQuery()).ForRoot(InstalledState.ClientRoot);
        return InstalledState.Detect(entry, query, names);
    }

    private InstalledStateResult DetectInstalledNow(CatalogEntry entry)
    {
        // HOT3 live (deploy #65): a plugin this session hot unloaded is going away within the
        // reloader's next scan, so its lingering report is not evidence that it is loaded — but only
        // while its ledgered files are actually gone, so a re-install of the same listing takes over.
        var names = HotUnloadedNow(entry) ? Array.Empty<string>() : LoadedPluginNames();
        return InstalledState.Detect(entry, _installedQuery, names);
    }

    /// <summary>HOT3: was this listing hot unloaded this session, with none of its ledgered files back?</summary>
    private bool HotUnloadedNow(CatalogEntry entry) =>
        _state.SessionHotUnloaded.Contains(entry.Key)
        && InstalledState.LedgerFilesFor(entry, _installedQuery).Count == 0;

    /// <summary>HOT3: is this listing's reload copy loaded by the reloader (its Remove a hot unload)?</summary>
    /// <summary>
    /// INSTALL2 defect A (live, deploy #69): is this listing's copy one <em>this process</em> hot
    /// loaded? Only two things say so — this session's own executor writes
    /// (<see cref="ListState.SessionHotLoaded"/>) and the <c>hot</c> ledger flag the executor wrote,
    /// which is cleared for every row at the next start.
    ///
    /// <para>The reloader's loaded-plugin report is deliberately <b>not</b> evidence: the reloader
    /// loads every DLL in <c>progenitor-reload/</c>, including a copy Bootstrap promoted at start from
    /// an ordinary restart-route install. Reading that as "hot" made CryptSnooze say
    /// <c>Installed · new</c> and made its Uninstall delete a live, loaded file at once instead of
    /// writing the <c>.uninstall</c> marker. After a restart such a mod is an ordinary loaded mod.</para>
    /// </summary>
    /// <remarks>
    /// POLISH1 defect D adds the third source: a ledgered <c>mod</c> copy on this root that the
    /// reloader reports loaded <em>and</em> whose exact bytes the local reviewed policy covers for
    /// same-session activation on this side (<see cref="HotUnloadQualification"/>). That is the same
    /// evidence the planner requires before the executor may write those bytes at all, so a mod that
    /// was hot loaded in an earlier session is hot-unloadable again after a restart instead of owing
    /// one. Live (deploy #70) <c>Progenitor-HotDemo</c> read <c>restart true</c> after a restart.
    ///
    /// <para>The session-scoped <c>new</c> tag does <b>not</b> follow: see
    /// <see cref="IsNewThisSession"/>.</para>
    /// </remarks>
    internal bool IsHotLoaded(CatalogEntry entry) =>
        entry != null
        && !HotUnloadedNow(entry)
        && (IsNewThisSession(entry) || QualifiedHotLoaded(entry));

    /// <summary>
    /// POLISH1 defect D: did <em>this session</em> hot load this listing? The two session-scoped
    /// sources — this pane's own executor writes and the <c>hot</c> ledger flag, which a fresh process
    /// clears for every row before the first read. Only this decides the small <c>new</c> tag
    /// (<see cref="InstallModsCopy.InstalledNew"/>) and the <c>newTag</c> readback: the tag means
    /// "arrived while you were playing" (project rule), so a reviewed image that is merely
    /// hot-unloadable after a restart is <c>Installed</c> with no tag.
    /// </summary>
    internal bool IsNewThisSession(CatalogEntry entry) =>
        entry != null
        && !HotUnloadedNow(entry)
        && (_state.SessionHotLoaded.Contains(entry.Key) || LedgerSaysHot(entry));

    /// <summary>
    /// POLISH1 defect D: a ledgered reload copy on this root that the reloader has loaded
    /// (<see cref="InstalledStateResult.HotLoaded"/>) and whose bytes are reviewed for same-session
    /// activation here. Both halves are required: the reloader loads every DLL in
    /// <c>progenitor-reload/</c>, promoted third-party mods included, and those are not collectible.
    /// </summary>
    private bool QualifiedHotLoaded(CatalogEntry entry) =>
        DetectInstalled(entry).HotLoaded && QualifiedForSameSession(entry);

    /// <summary>
    /// POLISH1 defect D: the local reviewed policy's answer for this listing's loaded image. Defaults
    /// to <c>BepInEx/config/progenitor-qualification.json</c> under the client root through
    /// <see cref="Admission.QualificationPolicyCache"/> (re-read only when the file's stamp moves); a
    /// test substitutes <see cref="QualifiedProbe"/>. Never throws: unreviewed is the safe answer.
    /// </summary>
    internal Func<CatalogEntry, bool>? QualifiedProbe { get; set; }

    private bool QualifiedForSameSession(CatalogEntry entry)
    {
        var probe = QualifiedProbe;
        if (probe != null)
        {
            try { return probe(entry); }
            catch { return false; }
        }

        var service = Service;
        if (service == null) return false;
        try
        {
            string root = service.ClientBepInExRoot;
            if (string.IsNullOrEmpty(root)) return false;
            var file = InstalledState.ReloadFileOn(entry, _installedQuery, InstalledState.ClientRoot);
            if (file == null) return false;
            string? pluginId = RestingIdentity(entry)?.PluginGuid;
            if (string.IsNullOrWhiteSpace(pluginId)) pluginId = entry.Manifest.Id;
            return HotUnloadQualification.Covers(
                    Progenitor.Install.Admission.QualificationPolicyCache.Current(root),
                    pluginId,
                    file.Value.Sha256,
                    file.Value.Size,
                    InstallTarget.Client)
                // HOTDEMO1 (user policy 2026-10-03): or the trusted hotLoadable flag of these exact bytes.
                || service.TrustsHotUnload(entry, file.Value.Path, out _);
        }
        catch { return false; }
    }

    /// <summary>
    /// INSTALL2 defect A: the <c>hot</c> ledger flag for this listing. One ledger read per root per
    /// build (the flags are read together, like the pending-removal ones), and a fresh process clears
    /// every flag before the first read.
    /// </summary>
    private bool LedgerSaysHot(CatalogEntry entry)
    {
        var probe = HotLedgerProbe;
        if (probe != null)
        {
            try { return probe(entry); }
            catch { return false; }
        }

        var hot = HotPackages();
        if (hot.Count == 0) return false;
        if (hot.Contains(entry.Key)) return true;
        return !string.IsNullOrWhiteSpace(entry.Manifest.Id) && hot.Contains(entry.Manifest.Id!);
    }

    /// <summary>
    /// INSTALL2 defect A: the ledger's own answer to "did this process hot load it", for a catalog
    /// without a service (the tests' fake). Null means "ask the catalog service".
    /// </summary>
    internal Func<CatalogEntry, bool>? HotLedgerProbe { get; set; }

    private HashSet<string>? _hotPackages;

    private HashSet<string> HotPackages()
    {
        if (_hotPackages != null) return _hotPackages;
        var service = Service;
        if (service == null) return _hotPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { _hotPackages = new HashSet<string>(service.HotLoadedPackages(), StringComparer.OrdinalIgnoreCase); }
        catch { _hotPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        return _hotPackages;
    }

    private void ForgetHotPackages() => _hotPackages = null;

    private void NoteInventoryChanged(string reason)
    {
        var hook = InventoryRefresh;
        if (hook == null) return;
        try { hook(reason); }
        catch { /* a refresh hint is never a gate on the install itself */ }
    }

    /// <summary>The current game-update tag (<see cref="CatalogUpdateTags.ResolveCurrent"/>), resolved once per build. Null when unknown.</summary>
    public string? CurrentUpdateTag => CurrentUpdate.Tag;

    /// <summary>Readback <c>currentUpdateSource</c>: <c>config</c> | <c>adapter</c> | <c>unknown</c>.</summary>
    public string CurrentUpdateSource => CurrentUpdate.Source;

    private CurrentUpdateResolution CurrentUpdate
    {
        get
        {
            _currentUpdate ??= CatalogUpdateTags.ResolveCurrent();
            return _currentUpdate.Value;
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            string next = value ?? "";
            if (string.Equals(_search, next, StringComparison.Ordinal)) return;
            _search = next;
            RefilterListings();
        }
    }

    /// <summary>The opened drawer's listing key (the selection), or null. INSTALLSIDES1: the drawer, not the flow.</summary>
    public string? SelectedKey => _state.DrawerKey;

    /// <summary>INSTALLSIDES1: the listing the install flow belongs to (the last button pressed), or null.</summary>
    public string? FlowKey => _state.OpenKey;

    public InstallTarget Target => _target;

    public bool IndexLoadAttempted => _indexLoadAttempted;

    public string? LastError => _lastError;

    public InstallProgress Status => _catalog.Status;

    /// <summary>Opening a catalog row yields a catalog identity, never an installed-plugin one.</summary>
    public ModsWorkspaceIdentity? SelectedIdentity =>
        string.IsNullOrEmpty(_state.DrawerKey) ? null : ModsWorkspaceIdentity.Catalog(_state.DrawerKey!);

    public Task<CatalogLoadResult> BeginLoadIndex(CancellationToken cancellationToken)
    {
        _indexLoadAttempted = true;
        _indexRefreshing = true;
        _refreshOverCache = false;
        _lastError = null;
        return _catalog.LoadIndexAsync(cancellationToken);
    }

    // True while a refresh runs over an index already on show (served from the cache or an
    // earlier fetch): a failure then keeps that list instead of replacing it with the error surface.
    private bool _refreshOverCache;

    /// <summary>
    /// LIST3 item 10: the automatic refresh through <paramref name="refresher"/> — the fetched
    /// index replaces the cached one when it lands (<see cref="NoteLoadResult"/>). A cached
    /// index already on show keeps the surface Ready meanwhile; with nothing on show it reads Loading.
    /// </summary>
    public Task<CatalogLoadResult> BeginIndexRefresh(InstallIndexRefresher refresher, CancellationToken cancellationToken) =>
        BeginIndexRefresh(refresher, cancellationToken, force: false);

    /// <summary>TSTRAFFIC1: <paramref name="force"/> is the player's Retry (it goes out during a failure backoff).</summary>
    public Task<CatalogLoadResult> BeginIndexRefresh(InstallIndexRefresher refresher, CancellationToken cancellationToken, bool force)
    {
        if (refresher == null) throw new ArgumentNullException(nameof(refresher));
        _indexLoadAttempted = true;
        _indexRefreshing = true;
        _refreshOverCache = _catalog.Search(null).Count > 0 && !ShownFromOtherSource();
        _lastError = null;
        _pendingSource = refresher.SourceUrl;
        return refresher.RefreshAsync(cancellationToken, force);
    }

    /// <summary>True while an index load or refresh started here has not reported back.</summary>
    public bool IndexRefreshing => _indexRefreshing;

    // INSTALLERR1: the source URL of the refresh in flight (its result's list belongs to it).
    private string? _pendingSource;

    public void NoteLoadResult(CatalogLoadResult result) => NoteLoadResult(result, _pendingSource, fromCache: false);

    /// <summary>
    /// INSTALLERR1: <paramref name="sourceUrl"/> is the index URL the result's list came from (a list from another
    /// source than the current one is never shown); <paramref name="fromCache"/> marks a list served from the disk cache.
    /// </summary>
    public void NoteLoadResult(CatalogLoadResult result, string? sourceUrl, bool fromCache)
    {
        _pendingSource = null;
        // INSTALLERR1: a second report of the same check (each joined caller's continuation) is not another failure.
        bool outstanding = _indexRefreshing;
        _indexLoadAttempted = true;
        _indexRefreshing = false;
        // A failed refresh over a list already on show keeps that list (the error is logged, not
        // shown over rows); a failed load with nothing on show is the error surface with Retry.
        bool keepCached = !result.Ok && _refreshOverCache && _catalog.Search(null).Count > 0;
        _refreshOverCache = false;
        _lastError = result.Ok || keepCached ? null : (result.Error ?? "the package index could not be loaded");
        NoteIndexState(result, sourceUrl, fromCache, keepCached, outstanding);
    }

    // ---- Filters, sort and search ---------------------------------------------------

    public void SetSideFilter(InstallSideFilter side)
    {
        if (_state.Filters.Side == side) return;
        _state.Filters.Side = side;
        RefilterListings();
    }

    public void ToggleNoRestartOnly()
    {
        _state.Filters.NoRestartOnly = !_state.Filters.NoRestartOnly;
        RefilterListings();
    }

    public void ToggleCurrentUpdateOnly()
    {
        _state.Filters.CurrentUpdateOnly = !_state.Filters.CurrentUpdateOnly;
        RefilterListings();
    }

    /// <summary><c>Clear filters</c>: no search, any side, defaults. The sort is kept.</summary>
    public void ClearFilters()
    {
        _state.Filters.Reset();
        _search = "";
        RefilterListings();
    }

    /// <summary>True at the defaults: empty search, any side, no restart chip off, current update on.</summary>
    public bool AtDefaults =>
        (IsSimple ? _state.SimpleShow == InstallSimpleShow.All : _state.Filters.IsDefault) && string.IsNullOrWhiteSpace(_search);

    /// <summary>
    /// Change the sort to <paramref name="sort"/> in its natural direction (INSTALLCOLS1:
    /// <see cref="InstallColumns.NaturalDescending"/>). Rows are rebuilt in the new order through
    /// the rebuild hook.
    /// </summary>
    public void SetSort(InstallSort sort) => SetSort(sort, reversed: false);

    /// <summary>INSTALLCOLS1: change the sort and its direction (<paramref name="reversed"/> against the column's natural one).</summary>
    public void SetSort(InstallSort sort, bool reversed)
    {
        if (_state.Filters.Sort == sort && _state.Filters.SortReversed == reversed) return;
        _state.Filters.Sort = sort;
        _state.Filters.SortReversed = reversed;
        MarkHeaderSorted();
        RequestDetailRebuild();
    }

    /// <summary>
    /// INSTALLCOLS1: a click on a sortable header. A new column sorts in its natural direction;
    /// the column already sorted reverses. The toolbar's <c>Sort:</c> button is gone — the header
    /// is the one sort control, and nothing else read that button.
    /// </summary>
    public void HeaderSort(InstallSort sort)
    {
        if (_state.Filters.Sort == sort) SetSort(sort, !_state.Filters.SortReversed);
        else SetSort(sort, reversed: false);
    }

    /// <summary>INSTALLCOLS1: the sorted column reads ▲ rather than ▼.</summary>
    public bool SortAscending => InstallColumns.Ascending(_state.Filters.Sort, _state.Filters.SortReversed);

    private void MarkHeaderSorted()
    {
        if (_header == null) return;
        try
        {
            // INSTALLSIMPLE1: Simple keeps its own sort (Mod, Released or Downloads).
            if (IsSimple) _header.SetSorted(SortKeyOf(_state.SimpleSort), InstallColumns.Ascending(_state.SimpleSort, _state.SimpleSortReversed));
            else _header.SetSorted(SortKeyOf(_state.Filters.Sort), SortAscending);
        }
        catch { /* */ }
    }

    public static string SortKeyOf(InstallSort sort) => sort switch
    {
        InstallSort.Released => SortKeyReleased,
        InstallSort.Name => SortKeyName,
        InstallSort.Author => SortKeyAuthor,
        InstallSort.Installed => SortKeyInstalled,
        _ => SortKeyDownloads,
    };

    /// <summary>
    /// A readback/bridge sort key as a sort. An unknown key is refused (false) and hands back the
    /// pane's default — POLISH2 item 1: Recently released, the same value a fresh
    /// <see cref="InstallSearchFilters"/> carries, so the fallback and the default cannot drift.
    /// INSTALLCOLS1: this is also the migration of an old sort — <c>likes</c> and <c>rating</c>
    /// (removed in stage 3) and any key a newer build wrote are refused onto that default.
    /// </summary>
    public static bool TryParseSortKey(string? key, out InstallSort sort)
    {
        switch ((key ?? "").Trim().ToLowerInvariant())
        {
            case SortKeyName: sort = InstallSort.Name; return true;
            case SortKeyReleased: sort = InstallSort.Released; return true;
            case SortKeyDownloads: sort = InstallSort.Downloads; return true;
            case SortKeyAuthor: sort = InstallSort.Author; return true;
            case SortKeyInstalled: sort = InstallSort.Installed; return true;
            default: sort = new InstallSearchFilters().Sort; return false;
        }
    }

    /// <summary>Match counts as last refiltered: shown now, filtered away from the defaults, total in the index.</summary>
    public (int Shown, int Filtered, int Total) MatchCount => (_lastShown, _lastFiltered, _lastTotal);

    public string CountText() => InstallModsCopy.CountText(_lastShown, _lastFiltered, AtDefaults);

    /// <summary>
    /// Every listing this tab may show — the catalog's search minus the hidden ones (INSTALL2 item
    /// 1). Hiding happens here rather than in the filters so that nothing downstream can reach a
    /// hidden listing: no row is built for it, so it cannot be counted, filtered, sorted, revealed,
    /// counted as pending, or widen the Install column.
    /// </summary>
    public IReadOnlyList<CatalogEntry> AllEntries()
    {
        var hits = _catalog.Search(null);
        if (hits == null || hits.Count == 0) return Array.Empty<CatalogEntry>();
        List<CatalogEntry>? kept = null;
        bool showNsfw = ShowNsfwContent;
        for (int i = 0; i < hits.Count; i++)
        {
            if (!IsHidden(hits[i]) && !NsfwHiddenNow(hits[i], showNsfw)) continue;
            kept = new List<CatalogEntry>(hits.Count);
            for (int j = 0; j < hits.Count; j++)
            {
                if (!IsHidden(hits[j]) && !NsfwHiddenNow(hits[j], showNsfw)) kept.Add(hits[j]);
            }

            break;
        }

        return kept ?? hits;
    }

    // ---- INSTALLSIDES1: the NSFW filter ----------------------------------------------------------

    /// <summary>
    /// INSTALLSIDES1: <c>[Mods] ShowNsfwContent</c>, read when the list filters. Null (tests, a pane
    /// built before the bindings) reads the default, off.
    /// </summary>
    internal Func<bool>? ShowNsfwProbe { get; set; }

    /// <summary>Whether author-flagged NSFW listings are shown right now.</summary>
    public bool ShowNsfwContent
    {
        get
        {
            var probe = ShowNsfwProbe;
            if (probe == null) return false;
            try { return probe(); }
            catch { return false; }
        }
    }

    /// <summary>
    /// INSTALLSIDES1: the one NSFW rule. A listing is hidden when its author flagged it, the switch is
    /// off, and nothing of it is installed — an installed flagged mod stays listed so it can be seen
    /// and removed. An unflagged listing is never hidden by it.
    /// </summary>
    internal static bool NsfwHidden(bool flagged, bool show, bool installed) => flagged && !show && !installed;

    private bool NsfwHiddenNow(CatalogEntry entry, bool show) =>
        entry != null && entry.HasNsfwContent && !show && NsfwHidden(true, show, InstalledAnywhere(entry));

    /// <summary>
    /// Is any copy of this listing on this client or the picked server — loaded, pending, marked, or
    /// written this session? Asked only for flagged listings, so its disk reads stay rare.
    /// </summary>
    internal bool InstalledAnywhere(CatalogEntry entry)
    {
        if (entry == null) return false;
        try
        {
            if (_state.SessionPending.Contains(entry.Key) || _state.SessionInstalled.Contains(entry.Key)
                || _state.SessionUninstallPending.Contains(entry.Key) || _state.SessionRemoved.Contains(entry.Key))
                return true;
            if (DetectInstalled(entry).State != InstalledKind.NotInstalled) return true;
            var halves = HalvesOf(entry);
            if (halves.Client != InstalledState.HalfNone || halves.Server != InstalledState.HalfNone) return true;
            if (RemoteServer != null && RemoteRowFor(entry) != null) return true;
        }
        catch
        {
            // An unreadable install state keeps the listing hidden, which is what the switch asks.
        }

        return false;
    }

    /// <summary>
    /// INSTALL2 item 1: a listing the tab does not show at all — any loader pack
    /// (<see cref="LoaderPackages.KnownLoaderPackages"/> and the owner/prefix rule) and Progenitor's
    /// own packages. The loader used to have a read-only row; a row that can only say "already
    /// installed, and not by this menu" is noise, and Progenitor's own listing would offer to write
    /// the assembly drawing the row.
    /// </summary>
    public static bool IsHidden(CatalogEntry entry) =>
        entry != null && LoaderPackages.IsHiddenPackage(entry.Owner, entry.PackageName, entry.Key);

    /// <summary>Filters and text search for one entry; README text counts when cached.</summary>
    public bool IsVisible(CatalogEntry entry) =>
        IsSimple
            ? SimpleVisible(entry)
            : VisibleExceptShow(entry) && ShowMatches(entry, _state.Filters.Show);

    /// <summary>Would the entry show at the defaults (no search, any side, current update on)? The count's baseline.</summary>
    public bool VisibleAtDefaults(CatalogEntry entry)
    {
        if (entry == null || IsHidden(entry)) return false;
        var defaults = new InstallSearchFilters();
        return defaults.Matches(entry, CurrentUpdateTag);
    }

    /// <summary>The entries that pass the filters and the search, in the current sort order.</summary>
    public IReadOnlyList<CatalogEntry> VisibleEntries()
    {
        var hits = AllEntries();
        var visible = new List<CatalogEntry>(hits.Count);
        for (int i = 0; i < hits.Count; i++)
        {
            if (IsVisible(hits[i])) visible.Add(hits[i]);
        }
        return SortedForView(visible);
    }

    /// <summary>INSTALLCOLS1: <paramref name="entries"/> in the pane's sort, direction and install times.</summary>
    internal List<CatalogEntry> SortedForView(IReadOnlyList<CatalogEntry> entries) =>
        IsSimple
            ? SimpleSorted(entries)
            : Sorted(entries, _state.Filters.Sort, _state.Filters.SortReversed, InstalledUtcFor);

    /// <summary>Sort a set of entries by <paramref name="sort"/>; ties keep the index order. Missing figures sort last.</summary>
    public static List<CatalogEntry> Sorted(IReadOnlyList<CatalogEntry> entries, InstallSort sort) =>
        Sorted(entries, sort, reversed: false, installedAt: null);

    /// <summary>
    /// INSTALLCOLS1: as <see cref="Sorted(IReadOnlyList{CatalogEntry}, InstallSort)"/>, against the
    /// column's natural direction when <paramref name="reversed"/>. A listing with no figure for the
    /// column (undated, unpublished, not installed) sorts last in <em>both</em> directions.
    /// <paramref name="installedAt"/> answers the Installed column; null reads every listing as not
    /// installed.
    /// </summary>
    internal static List<CatalogEntry> Sorted(
        IReadOnlyList<CatalogEntry> entries, InstallSort sort, bool reversed, Func<CatalogEntry, DateTime?>? installedAt)
    {
        var indexed = new List<(CatalogEntry Entry, int Index, DateTime? Installed)>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            DateTime? at = null;
            if (sort == InstallSort.Installed && installedAt != null)
            {
                try { at = installedAt(entries[i]); }
                catch { at = null; }
            }
            indexed.Add((entries[i], i, at));
        }
        indexed.Sort((a, b) =>
        {
            int cmp = CompareBy(a.Entry, b.Entry, sort, reversed, a.Installed, b.Installed);
            return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
        });
        var result = new List<CatalogEntry>(indexed.Count);
        for (int i = 0; i < indexed.Count; i++) result.Add(indexed[i].Entry);
        return result;
    }

    private static int CompareBy(
        CatalogEntry a, CatalogEntry b, InstallSort sort, bool reversed, DateTime? installedA, DateTime? installedB)
    {
        int dir = reversed ? -1 : 1;
        switch (sort)
        {
            case InstallSort.Name:
                return dir * string.Compare(HeadingText(a), HeadingText(b), StringComparison.OrdinalIgnoreCase);
            case InstallSort.Author:
            {
                string? aa = a.Owner ?? a.Author, ab = b.Owner ?? b.Author;
                bool ma = string.IsNullOrWhiteSpace(aa), mb = string.IsNullOrWhiteSpace(ab);
                if (ma || mb) return MissingLast(ma, mb);
                return dir * string.Compare(aa, ab, StringComparison.OrdinalIgnoreCase);
            }
            case InstallSort.Released:
            {
                string ra = ReleasedStamp(a), rb = ReleasedStamp(b);
                if (ra.Length == 0 || rb.Length == 0) return MissingLast(ra.Length == 0, rb.Length == 0);
                return dir * string.CompareOrdinal(rb, ra);
            }
            case InstallSort.Installed:
            {
                if (installedA == null || installedB == null) return MissingLast(installedA == null, installedB == null);
                return dir * installedB.Value.CompareTo(installedA.Value);
            }
            default:
            {
                long? da = a.Downloads, db = b.Downloads;
                if (da == null || db == null) return MissingLast(da == null, db == null);
                return dir * db.Value.CompareTo(da.Value);
            }
        }
    }

    private static int MissingLast(bool aMissing, bool bMissing) =>
        aMissing && bMissing ? 0 : aMissing ? 1 : -1;

    private static string ReleasedStamp(CatalogEntry entry) =>
        entry.ReleasedUtc ?? entry.DateUpdated ?? "";

    public static bool MatchesSearch(CatalogEntry entry, string? query) => SearchRank(entry, query) > 0;

    /// <summary>
    /// 2 = a term hit the description (description-title), 1 = name/owner/categories only,
    /// 0 = no match. Raw API metadata (uuid, urls, dates, download counts, dependency strings,
    /// package id/key) is never searched.
    /// </summary>
    public static int SearchRank(CatalogEntry entry, string? query) => SearchRank(entry, query, null);

    /// <summary>
    /// As <see cref="SearchRank(CatalogEntry, string?)"/>, plus <paramref name="readmeText"/>:
    /// the cached README plain text for this entry's version, or null when it has not been
    /// fetched. A term that only the README carries still matches (rank 1).
    /// </summary>
    public static int SearchRank(CatalogEntry entry, string? query, string? readmeText)
    {
        if (entry == null) return 0;
        if (string.IsNullOrWhiteSpace(query)) return 1;
        string[] terms = SplitTerms(query);
        if (terms.Length == 0) return 1;
        bool descriptionHit = false;
        for (int i = 0; i < terms.Length; i++)
        {
            string term = terms[i];
            bool inDescription = Contains(entry.Description, term);
            bool inName = Contains(entry.DisplayName, term);
            bool inOwner = Contains(entry.Owner, term) || Contains(entry.Author, term);
            bool inReadme = readmeText != null && Contains(readmeText, term);
            if (!inDescription && !inName && !inOwner && !inReadme) return 0;
            if (inDescription) descriptionHit = true;
        }

        return descriptionHit ? 2 : 1;
    }

    // ---- READMEs: index, disk and the automatic fetch --------------------------------

    /// <summary>True when README plain text for this listing's version is known (memory or adopted from disk); search then matches it.</summary>
    public bool HasReadmeForSearch(CatalogEntry entry) =>
        entry != null && _state.ReadmeIndex.ContainsKey(ReadmeIndexKey(entry));

    public string SearchScope(CatalogEntry entry) => HasReadmeForSearch(entry) ? "withReadme" : "oneLiner";

    /// <summary>Record README plain text for search (and on disk when a cache is set). Unavailable READMEs record nothing.</summary>
    public void NoteReadmeForSearch(CatalogEntry entry, ListingDocumentSet documents)
    {
        if (entry == null || documents == null) return;
        if (!documents.ReadmeOk)
        {
            // A canceled fetch is not a verdict: the next run asks again.
            if (!string.Equals(documents.ReadmeError, InstallReasons.FetchCancelled, StringComparison.Ordinal))
                _state.ReadmeUnavailable.Add(ReadmeIndexKey(entry));
            return;
        }
        string plain = MarkdownPlain.Join(MarkdownPlain.RenderBlocks(documents.ReadmeMarkdown, int.MaxValue)) ?? "";
        _state.ReadmeIndex[ReadmeIndexKey(entry)] = plain;
        _state.ReadmeUnavailable.Remove(ReadmeIndexKey(entry));
        _state.Disk?.WriteReadme(entry.Owner, entry.PackageName, entry.Manifest.Version, ReadmeStamp(entry), plain);
        // README-STRUCT1: the markdown too, so a later process or Core generation draws its structure.
        string markdown = documents.ReadmeMarkdown ?? "";
        if (markdown.Length > 0)
        {
            _state.ReadmeMarkdown[ReadmeIndexKey(entry)] = markdown;
            _state.Disk?.WriteReadmeMarkdown(entry.Owner, entry.PackageName, entry.Manifest.Version, ReadmeStamp(entry), markdown);
        }
    }

    private string? ReadmeTextFor(CatalogEntry entry) =>
        _state.ReadmeIndex.TryGetValue(ReadmeIndexKey(entry), out var text) ? text : null;

    private static string ReadmeIndexKey(CatalogEntry entry) =>
        (entry.Key ?? "") + "@" + (entry.Manifest.Version ?? "");

    /// <summary>The release stamp a cached README was written for; a newer index stamp refetches.</summary>
    public static string ReadmeStamp(CatalogEntry entry) =>
        entry.ReleasedUtc ?? entry.DateUpdated ?? "";

    /// <summary>Adopt READMEs an earlier pane (same catalog) fetched, then the disk cache, once per catalog and version set.</summary>
    private void AdoptCachedReadmes(IReadOnlyList<CatalogEntry> entries)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (HasReadmeForSearch(entry) || string.IsNullOrEmpty(entry.Key)) continue;
            if (_catalog.TryGetCachedListingDocuments(entry.Key, out var cached) && cached != null)
            {
                NoteReadmeForSearch(entry, cached);
                continue;
            }
            var disk = _state.Disk;
            if (disk == null || !_state.DiskChecked.Add(ReadmeIndexKey(entry))) continue;
            if (disk.TryReadReadme(entry.Owner, entry.PackageName, entry.Manifest.Version, ReadmeStamp(entry), out string text))
                _state.ReadmeIndex[ReadmeIndexKey(entry)] = text;
        }
    }

    /// <summary>
    /// TSTRAFFIC1: the most README fetches one build queues: the rows on screen, never more than this.
    /// About ten rows fit the native frame (<see cref="VisibleListingRows"/>), so this is "what the player
    /// is looking at". Each version is fetched once ever (the disk cache), a scroll queues nothing,
    /// and the README text only feeds search and the drawer, so no look-ahead is worth its requests
    /// (icons keep theirs: a missing icon is a visible hole).
    /// </summary>
    public const int MaxReadmePrefetchPerBuild = 10;

    /// <summary>The README fetches one build may queue: <see cref="ViewportRows"/>, at most <see cref="MaxReadmePrefetchPerBuild"/>.</summary>
    public int ReadmePrefetchBudget => Math.Min(_viewportRows, MaxReadmePrefetchPerBuild);

    /// <summary>
    /// TSTRAFFIC1: the rows on screen (from the first visible row, <see cref="ReadmePrefetchBudget"/> of them)
    /// with a fetchable identity whose README is not known yet. Before TSTRAFFIC1 this was every listing in
    /// the index, so each Install build swept the whole index at 250 ms a request.
    /// </summary>
    public List<string> PendingReadmeKeys()
    {
        var keys = new List<string>();
        var visible = VisibleEntries();
        int first = FirstVisibleRowIndex();
        if (first < 0 || first >= visible.Count) first = 0;
        int end = Math.Min(visible.Count, first + ReadmePrefetchBudget);
        for (int i = first; i < end; i++)
        {
            var entry = visible[i];
            if (string.IsNullOrEmpty(entry.Key)) continue;
            if (HasReadmeForSearch(entry) || _state.ReadmeUnavailable.Contains(ReadmeIndexKey(entry))) continue;
            if (string.IsNullOrWhiteSpace(entry.Owner) || string.IsNullOrWhiteSpace(entry.PackageName)
                || string.IsNullOrWhiteSpace(entry.Manifest.Version))
                continue;
            keys.Add(entry.Key);
        }
        return keys;
    }

    /// <summary>
    /// The README prefetch for the rows on screen (TSTRAFFIC1; LIST2 swept the whole index): at most
    /// <see cref="ReadmePrefetchBudget"/> listings per build, one request in flight, ≥ 250 ms apart,
    /// cancellable. Cached versions (memory or disk) and remembered failures are never re-requested.
    /// Every other README is fetched when its drawer opens. Returns the number queued.
    /// </summary>
    public int ResumePrefetch()
    {
        if (Prefetch.Running) return 0;
        // DESCCACHE1: a listing that arrived after the first build is read from disk here (no network),
        // so search covers every README already cached.
        AdoptCachedReadmes(AllEntries());
        var keys = PendingReadmeKeys();
        if (keys.Count == 0)
        {
            SyncDescriptionsCell();
            return 0;
        }
        Prefetch.Start(keys, OnPrefetchResult, OnPrefetchFinished);
        SyncDescriptionsCell();
        return keys.Count;
    }

    public void CancelPrefetchDescriptions()
    {
        Prefetch.Cancel();
        SyncDescriptionsCell();
    }

    private void OnPrefetchResult(string key, ListingDocumentSet documents)
    {
        if (_catalog.TryGetEntry(key, out var entry) && entry != null)
            NoteReadmeForSearch(entry, documents);
        RefilterListings();
        if (string.Equals(key, _state.DrawerKey, StringComparison.OrdinalIgnoreCase) && _readmeState == ListingDocState.Loading)
            RequestDetailRebuild();
    }

    private void OnPrefetchFinished() => SyncDescriptionsCell();

    /// <summary>Readback <c>descriptions {state, done, total}</c>: done = READMEs known for the index, total = fetchable listings.</summary>
    public (string State, int Done, int Total) DescriptionsProgress()
    {
        int total = 0, done = 0;
        var entries = AllEntries();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (string.IsNullOrWhiteSpace(entry.Owner) || string.IsNullOrWhiteSpace(entry.PackageName)
                || string.IsNullOrWhiteSpace(entry.Manifest.Version))
                continue;
            total++;
            if (HasReadmeForSearch(entry) || _state.ReadmeUnavailable.Contains(ReadmeIndexKey(entry))) done++;
        }
        return (ReadmePrefetchScheduler.StateName(Prefetch.State), done, total);
    }

    public string DescriptionsText()
    {
        var (state, done, total) = DescriptionsProgress();
        return InstallModsCopy.DescriptionsText(state == "running", done, total);
    }

    // DESCCACHE1: the description fetch is behind the scenes; the toolbar no longer shows its
    // progress (the user, 2026-09-29: "we shouldn't need to see that number at all in the UI").
    // DescriptionsText / DescriptionsProgress stay for the bridge readback.
    private void SyncDescriptionsCell() { }

    // ---- Icons -------------------------------------------------------------------------

    /// <summary>The icon URL for a listing (Thunderstore's CDN by version full name), or null.</summary>
    public static string? IconUrlFor(CatalogEntry entry) =>
        ThunderstoreAdapter.TryBuildIconUrl(entry, out var url) ? url : null;

    /// <summary>The listing keys whose icons the last build/refilter asked for (viewport rows ± <see cref="IconLookahead"/>).</summary>
    public IReadOnlyCollection<string> RequestedIconUrls => _iconRequested.Keys;

    private DateTime IconNow => _clock?.UtcNow ?? DateTime.UtcNow;

    /// <summary>
    /// Ask the cache for the icons of the rows that can be on screen: the first
    /// <see cref="ViewportRows"/> + <see cref="IconLookahead"/> visible rows. Ready sprites are
    /// painted at once; the rest arrive through <see cref="IconCache.Arrived"/>. A URL that
    /// failed is asked again once <see cref="IconCache.RetryAfter"/> has passed (ICON2 (c)).
    /// </summary>
    public void RequestVisibleIcons() => RequestIconWindow(FirstVisibleRowIndex());

    /// <summary>
    /// INSTALLUX1 item 7, live (deploy #71): 149 rows read <c>icon.state missing</c> on the
    /// scrolled-to screen while <c>progenitor-cache/icons/</c> held 102 files, because this used to
    /// ask only for the first <see cref="ViewportRows"/> + <see cref="IconLookahead"/> visible rows
    /// and only from <c>Build</c> / <c>RefilterListings</c> — a scroll never asked for more. The
    /// window is now the rows around <paramref name="firstVisible"/> (the pane's own scroll offset,
    /// or 0 at build), with <see cref="IconLookahead"/> above and below; the request is idempotent
    /// per URL, so a notch costs a dictionary lookup per row.
    ///
    /// <para>Beside it, every visible row is asked through the DISK lane
    /// (<see cref="IconCache.RequestFromDiskAsync"/>): a file already under
    /// <c>progenitor-cache/icons/</c> costs one read and no fetch, so a row scrolled to reads
    /// <c>ready</c> without ever having been in a network window. A disk miss retires silently and
    /// leaves the network lane to the window.</para>
    /// </summary>
    public void RequestIconWindow(int firstVisible)
    {
        var icons = _state.Icons;
        if (icons == null) return;
        // ICON2 (b): the disk lane, so an icon already under progenitor-cache/icons never waits
        // behind a slow (or hung) network fetch.
        var disk = _state.Disk;
        if (disk != null && icons.DiskProbe == null) icons.DiskProbe = disk.ReadIconBytes;
        DateTime now = IconNow;
        if (firstVisible < 0) firstVisible = 0;
        int from = firstVisible - IconLookahead;
        if (from < 0) from = 0;
        int to = firstVisible + _viewportRows + IconLookahead;
        int visible = -1;
        for (int i = 0; i < _rows.Count; i++)
        {
            var (row, entry, url) = _rows[i];
            if (!IsVisible(entry)) continue;
            visible++;
            if (string.IsNullOrEmpty(url)) continue;
            if (icons.TryGetSprite(url, out var sprite))
            {
                try { row.SetImage(CellIcon, sprite); }
                catch { /* */ }
                continue;
            }

            bool inWindow = visible >= from && visible < to;
            if (!inWindow)
            {
                // Outside the window: the disk lane only. This never reaches the network.
                _iconCts ??= new CancellationTokenSource();
                _ = icons.RequestFromDiskAsync(url, _iconCts.Token);
                continue;
            }

            if (_iconRequested.TryGetValue(url, out var asked))
            {
                if (icons.StateOf(url) != IconState.Failed) continue;
                if (now - asked < IconCache.RetryAfter) continue;
            }
            _iconRequested[url] = now;
            _iconCts ??= new CancellationTokenSource();
            _ = icons.RequestAsync(url, _iconCts.Token);
        }
    }

    /// <summary>
    /// The index (among the rows the filters show) of the first row on screen, from the list's own
    /// scroll offset. Zero without a measurement — at build there is none, and the window is then
    /// the top of the list, exactly as before.
    /// </summary>
    internal int FirstVisibleRowIndex()
    {
        float offset = ScrollOffsetY();
        if (offset <= 0f) return 0;
        float pitch = CellLayout.TwoLineRowHeight + PanelLayout.RowGap;
        if (pitch <= 0.01f) return 0;
        return (int)Math.Floor(offset / pitch);
    }

    /// <summary>
    /// How far down its content the list is scrolled, in px. Substituted by tests; in the game it
    /// is the live <c>ScrollRect</c>, read through <see cref="UiReveal.TryOffset"/>.
    /// </summary>
    internal Func<float>? ScrollOffsetProbe { get; set; }

    private float ScrollOffsetY()
    {
        var probe = ScrollOffsetProbe;
        if (probe != null)
        {
            try { return probe(); }
            catch { return 0f; }
        }

        return UiReveal.TryOffset(_panel, out float offset) ? offset : 0f;
    }

    /// <summary>
    /// INSTALLUX1 item 7: the wheel wrote a notch onto a list. The pane does not know which list,
    /// and does not need to — asking for its own current window again is idempotent per URL.
    /// Subscribed for the life of one build (<see cref="Build"/>) and dropped by
    /// <see cref="ResetHandles"/>, so a retired pane is never called.
    /// </summary>
    private void OnPanelScrolled()
    {
        if (_panel == null) return;
        RequestVisibleIcons();
    }

    private Action? _scrollHook;

    private void HookScroll()
    {
        UnhookScroll();
        _scrollHook = OnPanelScrolled;
        UiReveal.Scrolled += _scrollHook;
    }

    private void UnhookScroll()
    {
        var hook = _scrollHook;
        _scrollHook = null;
        if (hook != null) UiReveal.Scrolled -= hook;
    }

    private void OnIconArrived(string url)
    {
        var icons = _state.Icons;
        if (icons == null || string.IsNullOrEmpty(url)) return;
        if (!icons.TryGetSprite(url, out var sprite)) return;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!string.Equals(_rows[i].IconUrl, url, StringComparison.Ordinal)) continue;
            try { _rows[i].Row.SetImage(CellIcon, sprite); }
            catch { /* */ }
        }
    }

    /// <summary>Pane teardown: outstanding icon requests for this build are dropped (ready icons stay cached).</summary>
    public void CancelIconRequests()
    {
        var cts = _iconCts;
        _iconCts = null;
        if (cts == null) return;
        try { cts.Cancel(); }
        catch { /* */ }
        try { cts.Dispose(); }
        catch { /* */ }
        _iconRequested.Clear();
    }

    // ---- Selection and the drawer -----------------------------------------------------

    /// <summary>
    /// Go to <paramref name="key"/>: its drawer opens and its flow is the pane's (null closes both).
    /// The Mods menu's navigation and remembered selection come here. INSTALLSIDES1: a button on a
    /// row never does — it focuses the flow only (<see cref="FocusListing"/>), so a press cannot open
    /// or close a drawer.
    /// </summary>
    public void SelectListing(string? key)
    {
        FocusListing(key);
        SetDrawer(key);
    }

    /// <summary>
    /// INSTALLSIDES1: make <paramref name="key"/> the listing the install flow belongs to, without
    /// touching which drawer is open. A change closes the open confirmation and drops the old
    /// listing's flow, exactly as a change of selection always did.
    /// </summary>
    private void FocusListing(string? key)
    {
        string? next = string.IsNullOrWhiteSpace(key) ? null : key!.Trim();
        if (!string.Equals(_state.OpenKey, next, StringComparison.OrdinalIgnoreCase))
        {
            // INSTALL2 item 3: a confirmation belongs to one row.
            ClosePrompt();
            // Page A → B: A's download is canceled and its completion, should it still land,
            // is dropped by the token; B starts idle (an existing inspection re-plans on build).
            ResetFlow();
        }

        _state.OpenKey = next;
    }

    /// <summary>INSTALLSIDES1: open the drawer under <paramref name="key"/> (null closes it). A change resets the drawer state.</summary>
    private void SetDrawer(string? key)
    {
        string? next = string.IsNullOrWhiteSpace(key) ? null : key!.Trim();
        if (!string.Equals(_state.DrawerKey, next, StringComparison.OrdinalIgnoreCase))
        {
            _state.MoreInfo = false;
            _state.FullDescription = false;
            ResetDocuments();
        }

        _state.DrawerKey = next;
    }

    /// <summary>The listing whose drawer is open, or null.</summary>
    public string? DrawerKey => _state.DrawerKey;

    /// <summary>
    /// INSTALLSIDES1: the expand button (and the drawer's Collapse): open this row's drawer, or close
    /// it when it is the open one. Nothing else opens or closes a drawer — not a click on the row, its
    /// title or any of its buttons.
    ///
    /// <para>INSTALLSIDES2 (user feedback 2026-10-03: "the collapse and expansion of the drawers moves
    /// the whole window around, it should stay where it is"): the list stays still. The clicked row
    /// is measured before the rebuild and put back at the same distance from the viewport's top after
    /// it — opening row B while drawer A above it closes is compensated, so B does not jump. A drawer
    /// that reaches below the viewport is left there for the player to scroll to.</para>
    /// </summary>
    public void ToggleDrawer(string key)
    {
        _anchorKey = key;
        _anchorY = MeasureRowY(key);
        if (string.Equals(_state.DrawerKey, key, StringComparison.OrdinalIgnoreCase)) SetDrawer(null);
        else SetDrawer(key);

        RequestDetailRebuild();
    }

    private string? _anchorKey;
    private float? _anchorY;

    /// <summary>The clicked row's top from the list viewport's top edge now, or null when it cannot be measured.</summary>
    private float? MeasureRowY(string key)
    {
        var probe = AnchorProbe;
        if (probe != null)
        {
            try { return probe(key, null)?.RowY; }
            catch { return null; }
        }

        if (_panel == null || !_rowIndex.TryGetValue(key, out int index) || index < 0 || index >= _rows.Count) return null;
        return UiReveal.TryAnchor(_panel, _rows[index].Row, write: false, 0f, out float rowY, out _) ? rowY : null;
    }

    /// <summary>
    /// Tests: stands in for the runtime's row measurement — (row key, the wanted distance or null to
    /// measure only) → (the row's distance now, the offset held afterwards). Null in the game.
    /// </summary>
    internal Func<string, float?, (float RowY, float Offset)?>? AnchorProbe { get; set; }

    public bool MoreInfoExpanded => _state.MoreInfo;

    /// <summary>
    /// INSTALLSIDES1: the drawer shows every fact in its scrolling body, so there is no
    /// <c>More info</c> switch any more; this only flips the remembered flag a bridge may still read.
    /// </summary>
    public void ToggleMoreInfo() => _state.MoreInfo = !_state.MoreInfo;

    /// <summary>INSTALLSIDES2 item 4: the README reader window is open.</summary>
    public bool FullDescriptionShown => _readerDialog != null && _readerDialog.IsOpen;

    private UiDialog? _readerDialog;

    /// <summary>The reader dialog's kind (its readback and <c>dialog</c> bridge name).</summary>
    public const string ReaderDialogKind = "readme";

    /// <summary>
    /// INSTALLSIDES2 item 4 (option A): <c>Read full description</c> opens the whole README in its own
    /// reader window — the game's popup with one scrolling text block (headings, body and code as
    /// the drawer drew them) and <c>Close</c>. The drawer stays compact and the list does not move.
    /// </summary>
    public void ShowFullDescription()
    {
        string? key = _state.DrawerKey;
        if (string.IsNullOrEmpty(key) || !_catalog.TryGetEntry(key!, out var entry) || entry == null) return;
        OpenReader(entry);
    }

    internal bool OpenReader(CatalogEntry entry)
    {
        var lines = ReaderLines(entry);
        if (lines.Count == 0) return false;
        var factory = DialogFactory();
        if (factory == null) return false;
        float width = DialogLayout.ContentRowWidth();
        float height = Math.Max(DialogLayout.ReaderMinHeight,
            Math.Min(DialogLayout.ReaderHeight, InstallDrawerLayout.EstimateContentHeight(lines, width)));
        var content = new DialogContent
        {
            Shape = UiDialogShape.Notice,
            Title = HeadingText(entry),
            MainCaption = "",
            CancelCaption = InstallModsCopy.ReaderClose,
            Reader = lines,
            ReaderHeight = (float)Math.Round(height),
        };
        _readerDialog?.Close();
        _readerDialog = UiDialog.OpenContent(factory, content, "install", ReaderDialogKind, entry.Key);
        return _readerDialog != null;
    }

    /// <summary>The whole README as reader lines (the one-liner first when the README does not repeat it).</summary>
    internal List<CellLine> ReaderLines(CatalogEntry entry)
    {
        var lines = new List<CellLine>();
        string oneLiner = ListingSnippet(entry.Description);
        if (oneLiner.Length > 0) lines.Add(new CellLine(CellLineKind.Body, oneLiner));
        var blocks = ReadmeBlocksFor(entry);
        for (int i = 0; i < blocks.Count; i++) lines.Add(LineOf(blocks[i]));
        return lines;
    }

    /// <summary>Pane teardown (ModsMenu.ReleaseTabWorkspace): cancel the active download and forget the approval.</summary>
    public void CancelDownload() => ResetFlow();

    private void ResetFlow()
    {
        // INSTALLFAIL1: a flow that ends unwritten (No, Close, another listing) leaves no download behind.
        Service?.ReleaseDownload(_downloadKey ?? _state.OpenKey);
        _pageToken++;
        var cts = _downloadCts;
        _downloadCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); }
            catch { /* */ }
            try { cts.Dispose(); }
            catch { /* */ }
        }

        _phase = InstallActionPhase.Idle;
        _downloadKey = null;
        _downloadVersion = null;
        _actionError = null;
        _approvedHash = null;
        _lastPlan = null;
        _executeResult = null;
        _resultSha256 = null;
        _planSet = null;
        _executeSet = null;
        _activeSetPlan = null;
    }

    private void RequestDetailRebuild()
    {
        var rebuild = _detailRebuild;
        if (rebuild == null) return;
        try { rebuild(); }
        catch { /* */ }
    }

    // ---- The DL1 flow ------------------------------------------------------------------

    public Task? ActiveDownload => _activeDownload;

    public InstallActionPhase Phase => _phase;

    public int DroppedCompletions => _droppedCompletions;

    public string? ApprovedPlanHash => _approvedHash;

    public InstallPlan? LastPlan => _lastPlan;

    public ExecuteCallResult? LastExecuteResult => _executeResult;

    public string? ActionError => _actionError;

    /// <summary>
    /// INSTALLUX1 item 2: the per-row target chooser is gone — the target comes from
    /// <c>[Install] DefaultTarget</c> — so this is always false. Kept because the
    /// <c>action.chooser</c> readback keeps its key.
    /// </summary>
    public bool TargetChooserOpen => false;

    /// <summary>INSTALLUX1 item 1: the download + inspection behind the open box has finished (readback <c>plan.ready</c>).</summary>
    public bool PromptPlanReady => _promptPlanReady;

    /// <summary>INSTALLUX1 item 1: why the open install box cannot install anything, or null.</summary>
    public string? PromptRefusal => _state.Prompt == InstallPromptKind.Install ? _promptRefusal : null;

    /// <summary>INSTALLUX1 item 1: a Yes pressed before the inspection finished, waiting to be honored.</summary>
    internal bool PromptYesPending => _promptYesPending;

    public static string PhaseName(InstallActionPhase phase) => phase switch
    {
        InstallActionPhase.Downloading => "downloading",
        InstallActionPhase.Inspected => "inspected",
        InstallActionPhase.Reviewing => "reviewing",
        InstallActionPhase.Executing => "executing",
        InstallActionPhase.Done => "done",
        InstallActionPhase.Failed => "failed",
        InstallActionPhase.Cancelled => "cancelled",
        _ => "idle",
    };

    public static string TargetName(InstallTarget target) => target switch
    {
        InstallTarget.LocalServer => "localServer",
        InstallTarget.Both => "both",
        _ => "client",
    };

    /// <summary>
    /// Player action: the row's or the drawer's Install button. Opens the drawer for the
    /// listing, then downloads and checks it (<see cref="InstallModsCopy.Installing"/>); once
    /// inspected, a single target executes to pending at once and more than one target shows
    /// the chooser, where the chosen target writes at once (job CONF2: the click on the action is
    /// the intent, so there is no second Confirm). Installed, pending, tool and busy rows ignore the click
    /// (an installed row opens its drawer instead).
    /// </summary>
    public bool OnInstallClick(string key)
    {
        if (string.IsNullOrEmpty(key) || !_catalog.TryGetEntry(key, out var entry) || entry == null) return false;
        // INSTALL2 item 3: the confirmation under the row owns the decision while it is up. Only its
        // own No / Yes close it, so a second press on the row cannot restart the download or write
        // behind an open prompt.
        if (PromptOpenFor(entry)) return false;
        var action = ActionFor(entry);
        // REMOTEINST1: a remote result shown on a row the local route now owns is acknowledged by acting on it.
        if (!action.Remote && RemoteServer?.LatestFor(entry.Key) is { Stage: RemoteInstallStage.Failed or RemoteInstallStage.Refused })
        {
            RemoteServer?.ClearFinished(entry.Key);
            action = ActionFor(entry);
        }
        if (action.Remote)
        {
            switch (action.State)
            {
                case InstallListingState.Installing:
                    if (action.Enabled) RemoteServer?.Cancel(entry.Key);
                    RequestDetailRebuild();
                    return action.Enabled;
                case InstallListingState.Installed:
                    return action.Enabled && OpenPrompt(entry, InstallPromptKind.Uninstall);
                case InstallListingState.Pending:
                    return action.Enabled && OpenPrompt(entry, InstallPromptKind.CancelInstall);
                case InstallListingState.PendingUninstall:
                    return false;
                case InstallListingState.Update:
                    return OpenRemoteInstallPrompt(entry, RemoteInstallOp.Update);
                default:
                    if (!action.Enabled) return false;
                    return OpenRemoteInstallPrompt(entry, RemoteInstallOp.Install);
            }
        }

        if (action.State == InstallListingState.Sides)
        {
            if (!action.Enabled) return false;
            var decision = SideStatusRules.Decide(SideInputsOf(entry));
            switch (decision.Action)
            {
                case SideAction.InstallOnServer:
                case SideAction.UpdateServer:
                    if (CurrentRoute.Kind == ServerRouteKind.Remote)
                        return OpenRemoteInstallPrompt(entry,
                            decision.Action == SideAction.UpdateServer || RemoteRowFor(entry) != null ? RemoteInstallOp.Update : RemoteInstallOp.Install);
                    _targetOverride = InstallTarget.LocalServer;
                    break;
                case SideAction.InstallOnClient:
                case SideAction.UpdateClient:
                    _targetOverride = InstallTarget.Client;
                    break;
                default:
                    return false;
            }

            if (!string.Equals(_state.OpenKey, entry.Key, StringComparison.OrdinalIgnoreCase)) FocusListing(entry.Key);
            var halfTarget = _targetOverride;
            ApplyDefaultTarget(entry);
            OpenPrompt(entry, InstallPromptKind.Install);
            _targetOverride = halfTarget;
            if (_phase == InstallActionPhase.Inspected && PlanSelected(_server) is { Ok: true, Plan: { } halfPlan } && halfPlan.CanExecuteToPending)
                return ContinueAfterInspection();
            return BeginDownloadAndCheck();
        }

        if (action.Plain && action.Enabled)
        {
            var handler = PlainActionHandler;
            if (handler == null) return false;
            handler(action.PlainPluginId, entry.DisplayName ?? entry.PackageName ?? action.PlainPluginId,
                action.State == InstallListingState.PendingUninstall);
            return true;
        }

        switch (action.State)
        {
            case InstallListingState.Tool:
            case InstallListingState.Loader:
            case InstallListingState.Installing:
            case InstallListingState.Blocked:
                return false;
            // Removed (INSTORD1: files gone, the assembly still loaded until a restart) keeps
            // falling through to the install flow, so a re-install of the same listing is still
            // possible without restarting first.

            // INSTALL2 items 2 and 3: every state that has something to undo opens its own y/n box.
            case InstallListingState.PendingUninstall:
                return OpenPrompt(entry, InstallPromptKind.Reactivate);
            case InstallListingState.Pending:
                if (!action.Enabled) return false;
                return OpenPrompt(entry, InstallPromptKind.CancelInstall);
            case InstallListingState.Installed:
                // INSTALLSIDES1: a press never opens a drawer; an installed row with nothing to undo
                // here simply does nothing.
                return action.Enabled && OpenPrompt(entry, InstallPromptKind.Uninstall);
        }

        if (!string.Equals(_state.OpenKey, entry.Key, StringComparison.OrdinalIgnoreCase))
            FocusListing(entry.Key);

        // INSTALLUX1 item 1, the user's decision: "when they click install a popup opens and
        // confirms". The box opens on THIS click — not after a download, an "Installing…" caption
        // and a target question — and the download plus inspection run behind it. The target is
        // resolved here, from the setting, because the body's "Loads at the next restart of …"
        // sentence and the plan both need it.
        var resolvedTarget = ApplyDefaultTarget(entry);
        // REMOTEINST1: a Both install on the remote route writes the client half here and sends the
        // server half to the connected server once the local write succeeded.
        _remoteAfterLocal = resolvedTarget.ToConnectedServer && resolvedTarget.LocalInstall ? entry.Key : null;
        OpenPrompt(entry, InstallPromptKind.Install);

        if (_phase == InstallActionPhase.Inspected && PlanSelected(_server) is { Ok: true, Plan: { } plan } && plan.CanExecuteToPending)
            return ContinueAfterInspection();

        return BeginDownloadAndCheck();
    }

    // ---- INSTALLUX1 item 2: the target comes from the setting ---------------------------

    /// <summary>
    /// INSTALLUX1 item 2: the <c>[Install] DefaultTarget</c> setting, read through
    /// <c>ConfigHelper</c> in the game and substituted by tests. A pane whose key was never bound
    /// reads <see cref="DefaultInstallTarget.Default"/>.
    /// </summary>
    internal Func<DefaultInstallTargetSetting>? DefaultTargetProbe { get; set; }

    /// <summary>The setting this pane is installing by.</summary>
    public DefaultInstallTargetSetting DefaultTargetSetting
    {
        get
        {
            var probe = DefaultTargetProbe;
            if (probe != null)
            {
                try { return probe(); }
                catch { return DefaultInstallTarget.Default; }
            }

            try { return DefaultInstallTargetConfig.Resolve(); }
            catch { return DefaultInstallTarget.Default; }
        }
    }

    /// <summary>
    /// INSTALLUX1 item 2: the target one listing installs to, and whether a local-server answer had
    /// to fall back to the client because Configuration has no validated folder.
    /// </summary>
    public ResolvedInstallTarget ResolvedTargetFor(CatalogEntry entry) =>
        // LOCALONLY1: a listing only this PC's local test source carries installs this client's copy; the connected server
        // cannot fetch it.
        entry != null && LocalOnlyOnRemote(entry)
            ? new ResolvedInstallTarget(InstallTarget.Client, false, CurrentRoute, false, true)
            : DefaultInstallTarget.Resolve(
                DefaultTargetSetting,
                entry == null ? PackageSide.Unknown : SideOf(entry),
                CurrentRoute);

    /// <summary>LOCALONLY1 tests: whether a listing comes from this PC's local test source. Null asks the catalog service.</summary>
    internal Func<CatalogEntry, bool>? LocalListingProbe { get; set; }

    /// <summary>LOCALONLY1: the listing is this PC's local test source's (<c>[Install] LocalIndexPath</c>).</summary>
    internal bool IsLocalListing(CatalogEntry entry)
    {
        if (entry == null) return false;
        try { return LocalListingProbe?.Invoke(entry) ?? (_catalog as InstallCatalogService)?.IsLocalKey(entry.Key) ?? false; }
        catch { return false; }
    }

    /// <summary>
    /// LOCALONLY1 (live 2026-10-07: a both-sides install of a local test package sent its server half, which the server
    /// refused as not on Thunderstore): on the remote route the connected server cannot fetch a listing that only this PC's
    /// local test source carries, so no server install is offered or sent for it.
    /// </summary>
    internal bool LocalOnlyOnRemote(CatalogEntry entry) =>
        CurrentRoute.Kind == ServerRouteKind.Remote && IsLocalListing(entry) && SideOf(entry) != PackageSide.Client && ServerLocalOffer(entry) == null;

    /// <summary>
    /// F16 (live 2026-10-07): the connected server's own local test index offers this listing (its status says so), so the
    /// server installs it from there: its Server cell offers Install/Update and names that source. Null otherwise.
    /// </summary>
    internal RemoteServerRow? ServerLocalOffer(CatalogEntry entry)
    {
        if (entry == null || CurrentRoute.Kind != ServerRouteKind.Remote) return null;
        try { return RemoteServer?.LocalOfferFor(entry.Key); }
        catch { return null; }
    }

    // ---- REMOTEINST1: the server route --------------------------------------------------------

    /// <summary>The connected server's remote-install channel. Null keeps the pane exactly as it was.</summary>
    internal RemoteInstallClient? RemoteServer { get; set; }

    /// <summary>The route the one Server target takes now. Null derives it from the local folder alone.</summary>
    internal Func<ServerRoute>? ServerRouteProbe { get; set; }

    /// <summary>
    /// CONFIRMTARGET1: which server a remote (true) or local-folder (false) step touches. Null
    /// derives the local one from this pane's validated folder and its <c>ServerHostSettings.json</c>
    /// name, and leaves the remote one unnamed.
    /// </summary>
    internal Func<bool, ServerIdentity>? ServerIdentityProbe { get; set; }

    private string _hostNameRoot = "";
    private string _hostName = "";
    private long _hostNameAtTicks;

    /// <summary>CONFIRMTARGET1: the server one step touches, for the words that name it.</summary>
    internal ServerIdentity ServerIdentityFor(bool remote)
    {
        var probe = ServerIdentityProbe;
        if (probe != null)
        {
            try
            {
                var answer = probe(remote);
                if (answer != null) return answer;
            }
            catch { /* fall through to the pane's own answer */ }
        }

        if (remote) return ServerIdentity.Connected("", "");
        string root = _server != null && _server.HasValidatedServer ? _server.Current.CanonicalPath ?? "" : "";
        return ServerIdentity.Local(LocalHostName(root), root);
    }

    /// <summary>The folder's server name, re-read at most every two seconds (a build asks per row).</summary>
    private string LocalHostName(string root)
    {
        if (root.Length == 0) return "";
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (string.Equals(root, _hostNameRoot, StringComparison.OrdinalIgnoreCase)
            && now - _hostNameAtTicks < 2 * System.Diagnostics.Stopwatch.Frequency)
            return _hostName;
        try { _hostName = ServerIdentity.HostName(root); }
        catch { _hostName = ""; }
        _hostNameRoot = root;
        _hostNameAtTicks = now;
        return _hostName;
    }

    /// <summary>
    /// CONFIRMTARGET1: the server an install, update or uninstall confirmation touches, and whether
    /// this game is written too. Null when the step touches only this game (or fell back to it).
    /// </summary>
    internal (ServerIdentity? Server, bool Both) PromptServerFor(CatalogEntry entry, InstallPromptKind kind)
    {
        if (entry == null) return (null, false);
        if (kind == InstallPromptKind.Install)
        {
            if (_promptRemoteOnly) return (ServerIdentityFor(remote: true), false);
            var resolved = PromptResolvedFor(entry);
            if (resolved.FellBackToClient) return (null, false);
            if (resolved.ToConnectedServer) return (ServerIdentityFor(remote: true), resolved.LocalInstall);
            if (!resolved.LocalInstall) return (null, false);
            return resolved.Target switch
            {
                InstallTarget.LocalServer => (ServerIdentityFor(remote: false), false),
                InstallTarget.Both => (ServerIdentityFor(remote: false), true),
                _ => (null, false),
            };
        }

        if (kind is InstallPromptKind.Uninstall or InstallPromptKind.CancelInstall or InstallPromptKind.Reactivate
            && _promptSide != PromptSide.Whole)
        {
            // INSTALLSIDES1: a side button's box names only that side.
            if (_promptSide == PromptSide.Client) return (null, false);
            return (ServerIdentityFor(remote: CurrentRoute.Kind == ServerRouteKind.Remote), false);
        }

        if (kind == InstallPromptKind.Uninstall)
        {
            var inputs = SideInputsOf(entry);
            bool client = SideStatusRules.ClientPresent(inputs.Client);
            if (!SideStatusRules.ServerPresent(inputs.Server)) return (null, false);
            if (inputs.Source == ServerHalfSource.Local) return (ServerIdentityFor(remote: false), client);
            if (inputs.Source == ServerHalfSource.Remote && inputs.Route.Kind == ServerRouteKind.Remote)
                return (ServerIdentityFor(remote: true), client);
        }

        return (null, false);
    }

    /// <summary>
    /// REMOTEINST1 / F20: send the graceful Stop server request with this nonce (the pending bar's
    /// Stop server to apply and the drawer's Save and stop server). Wired to
    /// <see cref="ServerProcessControl.SubmitCommand"/>; null hides both buttons. Never restarts.
    /// </summary>
    internal Func<int, bool>? RemoteRestartRequest { get; set; }

    /// <summary>
    /// The server route this pane installs by. INSTALLSIDES1: with a server picker, the picked
    /// server's route — the connected one keeps the selector's route, a local folder is the local
    /// route once it is the configured folder, and the private games and None have no route.
    /// </summary>
    public ServerRoute CurrentRoute
    {
        get
        {
            var picked = PickedServer;
            if (picked == null || picked.Kind == ServerPickKind.Connected) return DecidedRoute;
            string configured = _server != null && _server.HasValidatedServer ? _server.Current.CanonicalPath ?? "" : "";
            if (picked.Kind == ServerPickKind.LocalFolder && configured.Length == 0 && ServerRouteProbe != null)
            {
                // The route probe reads [Mods] DedicatedServerPath itself (a pane built before the
                // workspace bound its folder): trust its local answer for the folder it validated.
                var decided = DecidedRoute;
                if (decided.Kind == ServerRouteKind.Local && PickerInputs is { } inputs && ServerPicker.SameId(inputs.ConfiguredPath, picked.Path))
                    return decided;
            }

            return ServerPicker.RouteFor(picked, DecidedRoute, configured);
        }
    }

    /// <summary>The route the selector decided from the connection and the configured folder (REMOTEINST1).</summary>
    private ServerRoute DecidedRoute
    {
        get
        {
            var probe = ServerRouteProbe;
            if (probe != null)
            {
                try { return probe(); }
                catch { /* fall through to the folder-only answer */ }
            }

            return _server != null && _server.HasValidatedServer
                ? new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None)
                : new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer);
        }
    }

    // ---- INSTALLSIDES1: the server picker -----------------------------------------------------

    /// <summary>
    /// The facts the picker's list is built from (connected server, configured folder, folders found
    /// on this PC). Set by the Mods menu; null (tests, an older wiring) means no picker: the Server
    /// column follows the route as before.
    /// </summary>
    internal Func<ServerPickerInputs>? ServerPickerProbe { get; set; }

    /// <summary>
    /// Make a picked local folder the configured one (<c>[Mods] DedicatedServerPath</c>, validated);
    /// returns the refusal, or null when it was applied. Set by the Mods menu.
    /// </summary>
    internal Func<string, string?>? ApplyServerFolder { get; set; }

    /// <summary>
    /// Whether the private games this client hosts share its plugins folder
    /// (<c>HostedServerPluginsMode.Shared</c>, the default). Set by the Mods menu; null reads shared.
    /// </summary>
    internal Func<bool>? PrivateGamesSharedProbe { get; set; }

    private ServerPickerInputs? _pickerInputs;
    private long _pickerInputsAt;

    /// <summary>The picker's facts, read at most every two seconds (a build asks per row).</summary>
    private ServerPickerInputs? PickerInputs
    {
        get
        {
            var probe = ServerPickerProbe;
            if (probe == null) return null;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_pickerInputs != null && now - _pickerInputsAt < 2 * System.Diagnostics.Stopwatch.Frequency) return _pickerInputs;
            try { _pickerInputs = probe(); }
            catch { _pickerInputs ??= new ServerPickerInputs(); }
            _pickerInputsAt = now;
            return _pickerInputs;
        }
    }

    /// <summary>Drop the cached picker facts (a pick, a configured-folder change).</summary>
    internal void ForgetPickerInputs()
    {
        _pickerInputs = null;
        _pickerInputsAt = 0;
    }

    /// <summary>The picker's entries now, in order; empty without a picker.</summary>
    internal IReadOnlyList<ServerPickEntry> PickerEntries()
    {
        var inputs = PickerInputs;
        return inputs == null ? Array.Empty<ServerPickEntry>() : ServerPicker.Build(inputs);
    }

    /// <summary>The server the Server column acts on, or null without a picker.</summary>
    internal ServerPickEntry? PickedServer
    {
        get
        {
            var entries = PickerEntries();
            if (entries.Count == 0) return null;
            // INSTALLSIMPLE1: Simple has no picker; it points at the connected server, else none.
            return IsSimple ? SimplePick(entries) : ServerPicker.Resolve(_state.PickedServer, entries);
        }
    }

    /// <summary>What the Server column is pointed at.</summary>
    internal ServerTargetKind ServerTarget
    {
        get
        {
            var picked = PickedServer;
            if (picked != null) return picked.TargetKind;
            // No picker: the route decides, and a blocked route still names a server. With no server
            // at all, what this client reaches is the private games it hosts — the old fallback, where
            // a server mod installed to the client.
            var route = CurrentRoute;
            return route.Kind == ServerRouteKind.None && route.Block == ServerRouteBlock.NoServer
                ? ServerTargetKind.PrivateGames
                : ServerTargetKind.Server;
        }
    }

    /// <summary>The Server column's header caption.</summary>
    internal string ServerHeaderCaption()
    {
        var picked = PickedServer;
        if (picked != null) return ServerPicker.HeaderCaption(picked);
        var route = CurrentRoute;
        if (route.Kind == ServerRouteKind.None) return ServerPicker.ServerWord;
        var identity = ServerIdentityFor(remote: route.Kind == ServerRouteKind.Remote);
        return identity.HasName ? ServerPicker.ServerWord + " · " + identity.Name : ServerPicker.ServerWord;
    }

    /// <summary>
    /// The player picked <paramref name="id"/> in the server picker. A dedicated-server folder that is
    /// not the configured one is applied first (validated); a refusal keeps the old pick and says why.
    /// </summary>
    internal bool PickServer(string id)
    {
        InvalidateSideCache();
        var entries = PickerEntries();
        ServerPickEntry? chosen = null;
        for (int i = 0; i < entries.Count; i++)
            if (ServerPicker.SameId(entries[i].Id, id)) { chosen = entries[i]; break; }
        if (chosen == null) return false;
        if (chosen.Kind == ServerPickKind.LocalFolder)
        {
            string configured = _server != null && _server.HasValidatedServer ? _server.Current.CanonicalPath ?? "" : "";
            if (!ServerPicker.SameId(configured, chosen.Path))
            {
                string? refusal;
                var apply = ApplyServerFolder;
                if (apply == null) refusal = InstallModsCopy.PickerCannotApply;
                else
                {
                    try { refusal = apply(chosen.Path); }
                    catch (Exception e) { refusal = e.Message; }
                }
                if (refusal != null)
                {
                    _pickerError = refusal;
                    RequestDetailRebuild();
                    return false;
                }
            }
        }

        _pickerError = null;
        _state.PickedServer = chosen.Id;
        RememberedPick = chosen.Id;
        ForgetPickerInputs();
        ClosePrompt();
        RequestDetailRebuild();
        return true;
    }

    private string? _pickerError;

    /// <summary>
    /// The last pick, for the catalog a configured-folder change rebuilds (its pane starts with this
    /// pick rather than the default). A plain string: it dies with this Core generation.
    /// </summary>
    internal static string? RememberedPick { get; set; }

    /// <summary>Why the last pick was refused, or null.</summary>
    internal string? PickerError => _pickerError;

    private UiDialog? _pickerDialog;

    /// <summary>
    /// The picker's dropdown bar was pressed: the game's popup with one button per server (the
    /// current one marked), <c>Cancel</c> to keep it.
    /// </summary>
    /// <summary>The picker dialog's body: what the pick does, then which server this client is connected to.</summary>
    private static string PickerBody(IReadOnlyList<ServerPickEntry> entries)
    {
        string line = ServerPicker.ConnectedLine(entries);
        return line.Length == 0 ? ServerPicker.DialogBody : ServerPicker.DialogBody + " " + line;
    }

    internal bool OpenServerPicker()
    {
        var entries = PickerEntries();
        if (entries.Count == 0) return false;
        var factory = DialogFactory();
        if (factory == null) return false;
        var current = PickedServer;
        var options = new List<string>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            bool on = current != null && ServerPicker.SameId(current.Id, entries[i].Id);
            options.Add((on ? InstallModsCopy.PickedMark : "") + entries[i].ChoiceCaption);
        }

        var content = new DialogContent
        {
            Shape = UiDialogShape.Choose,
            Title = ServerPicker.DialogTitle,
            Body = _pickerError != null ? _pickerError : PickerBody(entries),
            Options = options,
            CancelCaption = InstallModsCopy.PickerCancel,
        };
        var ids = new List<string>(entries.Count);
        for (int i = 0; i < entries.Count; i++) ids.Add(entries[i].Id);
        _pickerDialog?.Close();
        var dialog = UiDialog.OpenContent(factory, content, "install", PickerDialogKind, PickerDialogKind);
        _pickerDialog = dialog;
        dialog.OnPick(index =>
        {
            _pickerDialog = null;
            if (index >= 0 && index < ids.Count) PickServer(ids[index]);
        });
        dialog.OnNo(() => _pickerDialog = null);
        return true;
    }

    /// <summary>INSTALLSIDES1 readback <c>picker {picked, entries[], error, target}</c>.</summary>
    internal Dictionary<string, object?> PickerToWidget()
    {
        var entries = PickerEntries();
        var list = new List<Dictionary<string, object?>>(entries.Count);
        for (int i = 0; i < entries.Count; i++) list.Add(ServerPicker.ToWidget(entries[i]));
        var picked = PickedServer;
        return new Dictionary<string, object?>
        {
            ["picked"] = picked == null ? null : ServerPicker.ToWidget(picked),
            ["chosen"] = _state.PickedServer,
            ["entries"] = list,
            ["error"] = _pickerError,
            ["target"] = SideCells.Name(ServerTarget),
            ["route"] = CurrentRoute.EffectiveName, // REMOTETEST1: names a forced remote route
            ["block"] = CurrentRoute.BlockName,
            ["header"] = ServerHeaderCaption(),
            ["dialogOpen"] = _pickerDialog != null && _pickerDialog.IsOpen,
        };
    }

    // ---- INSTALLSIDES1: the two side cells --------------------------------------------------------

    private Dictionary<string, SideCellsDecision>? _sideMemo;

    /// <summary>Both cells of one listing (memoized for one build: the row, its readback and its clicks ask together).</summary>
    internal SideCellsDecision SideCellsFor(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (_sideMemo != null && _sideMemo.TryGetValue(entry.Key, out var known)) return known;
        var decided = SideCells.Decide(SideCellInputsOf(entry));
        if (_sideMemo != null) _sideMemo[entry.Key] = decided;
        return decided;
    }

    /// <summary>The facts the per-side decision reads for one listing.</summary>
    internal SideCellInputs SideCellInputsOf(CatalogEntry entry)
    {
        bool tool = IsTool(entry);
        bool loader = IsLoader(entry);
        var target = ServerTarget;
        bool shared = true;
        try { shared = PrivateGamesSharedProbe?.Invoke() ?? true; }
        catch { shared = true; }
        return new SideCellInputs
        {
            Side = SideOf(entry),
            NeedsBoth = NeedsBoth(entry),
            Latest = entry.Manifest.Version ?? "",
            NotInstallable = tool || loader,
            NotInstallableText = tool ? NotInstallableText(entry) : loader ? InstallModsCopy.LoaderInstalled : "",
            // A package too large to download is too large for either side.
            NotInstallableServerText = tool && IsTooLarge(entry) ? NotInstallableText(entry) : "",
            Client = tool || loader ? SideHalf.None : ClientHalfOf(entry),
            Server = tool || loader || target != ServerTargetKind.Server ? SideHalf.None : ServerHalfOf(entry),
            Target = target,
            PrivateShared = shared,
            Route = CurrentRoute,
            Admin = target == ServerTargetKind.Server ? AdminOfferNow() : AdminOffer.None,
        };
    }

    /// <summary>
    /// INSTALLSIDES2 item 3: what the Server column offers a player the route refuses for want of
    /// admin. The connected server's status says whether this player is on its admin list; the
    /// existing <see cref="AdminAuthFlow"/> decides whether <c>Authenticate as admin</c> applies
    /// (on the list, not authenticated, <c>[Ui] AdminAuthButton</c> on).
    /// </summary>
    internal AdminOffer AdminOfferNow()
    {
        var route = CurrentRoute;
        if (route.Block != ServerRouteBlock.NotAdmin) return AdminOffer.None;
        var probe = AdminOfferProbe;
        if (probe != null)
        {
            try { return probe(); }
            catch { return AdminOffer.AdminOnly; }
        }

        RemoteInstallStatus? status = null;
        try { status = RemoteServer?.Status; }
        catch { status = null; }
        var flow = AdminAuthFlow.Shared;
        if (flow.State == AdminAuthState.Authenticating) return AdminOffer.Authenticating;
        return AdminAuthFlow.Offer(status, flow.Enabled, featureGated: true) == AdminAuthOfferKind.Button
            ? AdminOffer.Authenticate
            : AdminOffer.AdminOnly;
    }

    /// <summary>Tests: the admin offer without a server status. Null in the game.</summary>
    internal Func<AdminOffer>? AdminOfferProbe { get; set; }

    /// <summary>Tests and the bridge: what pressing <c>Authenticate as admin</c> runs. Null: the shared flow.</summary>
    internal Func<bool>? AuthenticateAction { get; set; }

    /// <summary>The local install flow is running for <paramref name="entry"/> on <paramref name="server"/>'s side.</summary>
    private bool FlowOn(CatalogEntry entry, bool server) =>
        IsOpen(entry) && (server ? _target != InstallTarget.Client : _target != InstallTarget.LocalServer);

    /// <summary>A local flow's step on that side: <c>Installing…</c> while it downloads or writes; the refusal once planned.</summary>
    private (string Busy, string Refused) FlowWords(CatalogEntry entry, bool server)
    {
        if (!FlowOn(entry, server)) return ("", "");
        if (_phase is InstallActionPhase.Downloading or InstallActionPhase.Executing) return (InstallModsCopy.Installing, "");
        if (_phase is InstallActionPhase.Inspected or InstallActionPhase.Reviewing)
        {
            if ((_lastPlan != null && !_lastPlan.CanExecuteToPending) || (_lastPlan == null && _actionError != null))
                return ("", SideCells.CannotInstall);
        }

        // INSTALLFAIL1: a confirmed install that wrote nothing says so on its side (Install retries).
        if (_phase == InstallActionPhase.Failed) return ("", InstallStatusLabels.InstallFailed);
        return ("", "");
    }

    /// <summary>The Client cell's facts: what this client holds, who installed it and what can be done to it here.</summary>
    internal SideHalf ClientHalfOf(CatalogEntry entry)
    {
        var (busy, refused) = FlowWords(entry, server: false);
        if (busy.Length > 0) return new SideHalf { Busy = busy };

        var halves = HalvesOf(entry);
        // INSTALLSIDES2 item 6: this client's evidence only (never the local server's copy).
        var detected = DetectClient(entry);
        bool serverOnly = _state.SessionServerOnly.Contains(entry.Key);
        bool clientLedger = halves.Client != InstalledState.HalfNone
            || (IsLedgered(entry) && halves.Server == InstalledState.HalfNone && !serverOnly);
        string version = ClientVersionOf(entry, detected);

        // The keep is always offered, as the one-button row offered it: a catalog that cannot unmark
        // says so when its Yes is pressed.
        if (ClientRemovalPending(entry, halves, detected))
            return new SideHalf { State = HalfState.RemovalPending, Version = version, CanCancelUninstall = true, ProgenitorManaged = clientLedger };

        if (_state.SessionRemoved.Contains(entry.Key))
            return new SideHalf { State = HalfState.RemovalPending, Version = version };

        bool hotGone = HotUnloadedNow(entry) && detected.Files.Count == 0;
        if (hotGone) return new SideHalf { State = HalfState.Absent, Refused = refused, RefusedRetry = refused == InstallStatusLabels.InstallFailed };

        if (IsHotLoaded(entry) && clientLedger)
            return new SideHalf { State = HalfState.Installed, Version = version, CanUninstall = true, ProgenitorManaged = true };

        // A copy the inventory reports loaded is installed whatever the ledger's half says: a copy
        // Bootstrap promoted and BepInEx loaded at start reads "pending" in the halves (only a hot
        // copy reads loaded there).
        bool loadedNow = detected.State is InstalledKind.Loaded or InstalledKind.ManagedElsewhere or InstalledKind.UpdateAvailable;
        bool pending = !loadedNow
            && (halves.Client == InstalledState.HalfPending
                || (!serverOnly && halves.Client == InstalledState.HalfNone && halves.Server == InstalledState.HalfNone
                    && (_state.SessionPending.Contains(entry.Key) || detected.State == InstalledKind.Pending)));
        if (pending)
            return new SideHalf { State = HalfState.Pending, Version = version, CanCancelInstall = clientLedger, ProgenitorManaged = true };

        bool installed = halves.Client == InstalledState.HalfLoaded
            || (!serverOnly && _state.SessionInstalled.Contains(entry.Key))
            || detected.State is InstalledKind.Loaded or InstalledKind.ManagedElsewhere or InstalledKind.UpdateAvailable;
        if (installed && !(serverOnly && halves.Client == InstalledState.HalfNone && detected.State == InstalledKind.NotInstalled))
        {
            // MANAGED1: a copy the scan found with no Progenitor file on this client's disk is another
            // tool's even when a ledger names the package; with a readable ledger that does not name
            // it, so is any copy this session did not write.
            bool elsewhere = (detected.State == InstalledKind.ManagedElsewhere && halves.Client == InstalledState.HalfNone)
                || (!clientLedger && LedgerProbe != null && !_state.SessionInstalled.Contains(entry.Key));
            if (elsewhere)
            {
                var offer = PlainOfferFor(detected.PluginId);
                if (offer is { RemovalPending: true })
                    return new SideHalf { State = HalfState.RemovalPending, Version = version, CanCancelUninstall = true };
                if (offer is { Reload: true })
                    return new SideHalf { State = HalfState.Installed, Version = version, CanUninstall = true, Location = InstallModsCopy.InReloadFolder };
                if (offer is { CanUninstall: true })
                    return new SideHalf { State = HalfState.Installed, Version = version, CanUninstall = true };
                return new SideHalf { State = HalfState.Installed, Version = version, Managed = InstallModsCopy.ManagedBy(detected.Manager) };
            }

            bool canUninstall = clientLedger;
            return new SideHalf { State = HalfState.Installed, Version = version, CanUninstall = canUninstall, ProgenitorManaged = clientLedger };
        }

        return new SideHalf { State = HalfState.Absent, Refused = refused, RefusedRetry = refused == InstallStatusLabels.InstallFailed };
    }

    private string ClientVersionOf(CatalogEntry entry, InstalledStateResult detected)
    {
        try
        {
            foreach (var row in InstalledState.LedgerRowsFor(entry, _installedQuery))
            {
                if (string.Equals(row.Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (row.Version.Length > 0) return row.Version;
            }
        }
        catch { /* versions are evidence, never a gate */ }

        if (!string.IsNullOrEmpty(detected.LoadedVersion)) return detected.LoadedVersion!;
        if (_state.SessionInstalled.Contains(entry.Key) || _state.SessionPending.Contains(entry.Key)) return entry.Manifest.Version ?? "";
        return "";
    }

    /// <summary>The Server cell's facts for the picked server (local folder or connected).</summary>
    internal SideHalf ServerHalfOf(CatalogEntry entry)
    {
        var route = CurrentRoute;
        if (route.Kind == ServerRouteKind.Local)
        {
            var (busy, refused) = FlowWords(entry, server: true);
            if (busy.Length > 0) return new SideHalf { Busy = busy };
            var halves = HalvesOf(entry);
            string version = ServerVersionOf(entry);
            bool serverOnly = _state.SessionServerOnly.Contains(entry.Key);
            if (ServerRemovalPending(entry, halves))
                return new SideHalf { State = HalfState.RemovalPending, Version = version, CanCancelUninstall = true, ProgenitorManaged = true };
            if (halves.Server == InstalledState.HalfPending || (serverOnly && halves.Server == InstalledState.HalfNone && _state.SessionPending.Contains(entry.Key)))
                return new SideHalf { State = HalfState.Pending, Version = version.Length > 0 ? version : entry.Manifest.Version ?? "", CanCancelInstall = true, ProgenitorManaged = true };
            if (halves.Server == InstalledState.HalfLoaded || (serverOnly && halves.Server == InstalledState.HalfNone && _state.SessionInstalled.Contains(entry.Key)))
                return new SideHalf { State = HalfState.Installed, Version = version.Length > 0 ? version : entry.Manifest.Version ?? "", CanUninstall = true, ProgenitorManaged = true };
            // F34 (live 2026-10-08): the private game loads the copy its launcher staged from this client's reload folder
            // (no server ledger row): installed there, removed with this client's copy rather than from the Server cell.
            if (halves.Server == InstalledState.HalfNone && refused.Length == 0
                && InstalledState.StagedOnServer(entry, _installedQuery, out string stagedVersion))
                return new SideHalf { State = HalfState.Installed, Version = stagedVersion.Length > 0 ? stagedVersion : entry.Manifest.Version ?? "" };
            return new SideHalf { State = HalfState.Absent, Refused = refused, RefusedRetry = refused == InstallStatusLabels.InstallFailed };
        }

        var client = RemoteServer;
        var flight = client?.InFlightFor(entry.Key);
        if (flight != null)
            return new SideHalf
            {
                Busy = InstallStatusLabels.Stage(flight),
                BusyCancelable = flight.Stage < RemoteInstallStage.Installing,
                BusyIsRemoval = flight.Op == RemoteInstallOp.Uninstall,
            };

        var status = client?.Status;
        bool rowsKnown = status != null && status.RowsIncluded && route.Kind == ServerRouteKind.Remote;
        if (rowsKnown)
        {
            var row = client!.RowFor(entry, _installedQuery?.IdentityProbe);
            var last = client.LatestFor(entry.Key);
            // LOCALONLY1: the server cannot fetch a listing only this PC's local test source carries.
            // F16: unless the server's own local test index offers it (then it installs from there).
            if (row == null && IsLocalListing(entry) && SideOf(entry) != PackageSide.Client && client.LocalOfferFor(entry.Key) == null)
                return new SideHalf { State = HalfState.Absent, Refused = InstallModsCopy.LocalOnlyServerWord, RefusedQuiet = true };

            if (row != null)
            {
                // SRVUNINST1: a refused uninstall stays on the cell until the next step on this mod
                // (it used to be only in the client log), with Uninstall anyway when it can be overridden.
                bool refusedUninstall = last is { Op: RemoteInstallOp.Uninstall, Stage: RemoteInstallStage.Refused or RemoteInstallStage.Failed };
                return row.State switch
                {
                    RemoteRowState.Pending => new SideHalf { State = HalfState.Pending, Version = row.Version, CanCancelInstall = true },
                    RemoteRowState.UninstallPending => new SideHalf { State = HalfState.RemovalPending, Version = row.Version },
                    _ => new SideHalf
                    {
                        State = HalfState.Installed, Version = row.Version, CanUninstall = true,
                        UninstallRefused = refusedUninstall ? RemoteUninstallForce.RefusedWord : "",
                        CanForceUninstall = refusedUninstall && RemoteUninstallForce.Offers(last),
                    },
                };
            }

            if (last != null && last.IsTerminal)
            {
                switch (last.Stage)
                {
                    case RemoteInstallStage.HotLoaded:
                        return new SideHalf { State = HalfState.Installed, Version = entry.Manifest.Version ?? "" };
                    case RemoteInstallStage.PendingRestart when last.Op != RemoteInstallOp.Uninstall:
                        return new SideHalf { State = HalfState.Pending, Version = entry.Manifest.Version ?? "" };
                    case RemoteInstallStage.Failed:
                    case RemoteInstallStage.Refused:
                        // REMOTEFOLLOW1: no Install the server is known to refuse again.
                        return new SideHalf
                        {
                            State = HalfState.Absent, Refused = InstallStatusLabels.Failure(last),
                            RefusedRetry = !InstallStatusLabels.WouldRepeat(last.Reason), RefusedQuiet = InstallStatusLabels.WouldRepeat(last.Reason),
                        };
                }
            }

            return new SideHalf { State = HalfState.Absent };
        }

        (bool Known, string? Version) handshake = (false, null);
        try { handshake = ServerHandshakeProbe?.Invoke(entry) ?? (false, null); }
        catch { handshake = (false, null); }
        if (handshake.Known)
            return handshake.Version != null
                ? new SideHalf { State = HalfState.Installed, Version = handshake.Version }
                : new SideHalf { State = HalfState.Absent };
        return new SideHalf { State = HalfState.Unknown };
    }

    private string ServerVersionOf(CatalogEntry entry)
    {
        try
        {
            foreach (var row in InstalledState.LedgerRowsFor(entry, _installedQuery))
            {
                if (!string.Equals(row.Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (row.Version.Length > 0) return row.Version;
            }
        }
        catch { /* versions are evidence, never a gate */ }

        return "";
    }

    private HashSet<string>? _markedClient;
    private HashSet<string>? _markedServer;

    /// <summary>The listings marked for removal on one side's roots (one ledger read per side per build).</summary>
    private HashSet<string>? MarkedOn(bool server)
    {
        var service = Service;
        if (service == null) return null;
        ref var memo = ref server ? ref _markedServer : ref _markedClient;
        if (memo != null) return memo;
        try { memo = new HashSet<string>(service.UninstallPendingPackages(server ? InstallTarget.LocalServer : InstallTarget.Client), StringComparer.OrdinalIgnoreCase); }
        catch { memo = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        return memo;
    }

    private static bool MarkedMatches(HashSet<string> marked, CatalogEntry entry)
    {
        if (marked.Count == 0) return false;
        if (marked.Contains(entry.Key)) return true;
        if (!string.IsNullOrWhiteSpace(entry.Manifest.Id) && marked.Contains(entry.Manifest.Id!)) return true;
        foreach (string package in marked)
            if (InstalledState.PackageMatches(entry, package)) return true;
        return false;
    }

    /// <summary>This client's copy is marked to go at the next game start.</summary>
    private bool ClientRemovalPending(CatalogEntry entry, (string Client, string Server) halves, InstalledStateResult detected)
    {
        var marked = MarkedOn(server: false);
        if (marked != null) return halves.Client != InstalledState.HalfNone && MarkedMatches(marked, entry);
        // No service (a test catalog): the listing-wide answer, on the side that holds a copy.
        if (!UninstallPendingNow(entry)) return false;
        return !(halves.Client == InstalledState.HalfNone && halves.Server != InstalledState.HalfNone);
    }

    /// <summary>The local server's copy is marked to go at its next start.</summary>
    private bool ServerRemovalPending(CatalogEntry entry, (string Client, string Server) halves)
    {
        if (halves.Server == InstalledState.HalfNone) return false;
        var marked = MarkedOn(server: true);
        if (marked != null) return MarkedMatches(marked, entry);
        return UninstallPendingNow(entry) && halves.Client == InstalledState.HalfNone;
    }

    // ---- INSTALLSIDES1: the side buttons ---------------------------------------------------------

    /// <summary>
    /// A side cell's button. Each acts on that side only and opens the same confirmations and runs
    /// the same executors as before: the local flow for this client or the local server folder, the
    /// remote route for a connected server. A press never opens or closes a drawer.
    /// </summary>
    public bool OnSideClick(string key, bool server, SideButton button)
    {
        if (string.IsNullOrEmpty(key) || !_catalog.TryGetEntry(key, out var entry) || entry == null) return false;
        if (PromptOpenFor(entry)) return false;
        var cells = SideCellsFor(entry);
        var cell = server ? cells.Server : cells.Client;
        if (!cell.Has(button)) return false;
        var half = server ? ServerHalfOf(entry) : ClientHalfOf(entry);
        bool remote = server && CurrentRoute.Kind == ServerRouteKind.Remote;

        switch (button)
        {
            case SideButton.AdminOnly:
                // A dim, disabled lock: nothing to do.
                return false;

            case SideButton.Authenticate:
            {
                if (AdminOfferNow() == AdminOffer.Authenticating) return false;
                bool began;
                try { began = AuthenticateAction?.Invoke() ?? AdminAuthFlow.Shared.Begin(); }
                catch { began = false; }
                RequestDetailRebuild();
                return began;
            }

            case SideButton.Install:
            case SideButton.Update:
                if (remote)
                    return OpenRemoteInstallPrompt(entry,
                        button == SideButton.Update || RemoteRowFor(entry) != null ? RemoteInstallOp.Update : RemoteInstallOp.Install);
                RemoteServer?.ClearFinished(entry.Key);
                return BeginSideInstall(entry, server ? InstallTarget.LocalServer : InstallTarget.Client, remoteAfter: false,
                    update: button == SideButton.Update);

            case SideButton.Uninstall:
                if (!server)
                {
                    var plain = PlainClientOffer(entry);
                    if (plain != null) return InvokePlain(entry, plain, cancel: false);
                }
                return OpenSidePrompt(entry, InstallPromptKind.Uninstall, server);

            case SideButton.UninstallAnyway:
                // SRVUNINST1: the server refused the uninstall for its dependency check; this box says the
                // check is skipped, and its Yes sends the request again with force.
                _promptForce = true;
                return OpenSidePrompt(entry, InstallPromptKind.Uninstall, server);

            case SideButton.CancelInstall:
                if (half.Busy.Length > 0)
                {
                    if (remote && half.BusyCancelable) RemoteServer?.Cancel(entry.Key);
                    RequestDetailRebuild();
                    return half.BusyCancelable;
                }
                return OpenSidePrompt(entry, InstallPromptKind.CancelInstall, server);

            case SideButton.CancelUninstall:
                if (half.Busy.Length > 0)
                {
                    if (remote && half.BusyCancelable) RemoteServer?.Cancel(entry.Key);
                    RequestDetailRebuild();
                    return half.BusyCancelable;
                }
                if (!server)
                {
                    var plain = PlainClientOffer(entry);
                    if (plain != null) return InvokePlain(entry, plain, cancel: true);
                }
                return OpenSidePrompt(entry, InstallPromptKind.Reactivate, server);
        }

        return false;
    }

    /// <summary>
    /// The quiet shortcut under the name: <c>Install on both</c> / <c>Update both</c>. The local
    /// folder takes both halves in one write; a connected server gets its half through the remote
    /// route once this client's half is written.
    /// </summary>
    public bool OnBothClick(string key)
    {
        if (string.IsNullOrEmpty(key) || !_catalog.TryGetEntry(key, out var entry) || entry == null) return false;
        if (PromptOpenFor(entry)) return false;
        if (SideCellsFor(entry).Shortcut == SideButton.None) return false;
        RemoteServer?.ClearFinished(entry.Key);
        bool update = SideCellsFor(entry).Shortcut == SideButton.Update;
        if (LocalOnlyOnRemote(entry)) return BeginSideInstall(entry, InstallTarget.Client, remoteAfter: false, update);
        return CurrentRoute.Kind == ServerRouteKind.Remote
            ? BeginSideInstall(entry, InstallTarget.Client, remoteAfter: true, update)
            : BeginSideInstall(entry, InstallTarget.Both, remoteAfter: false, update);
    }

    /// <summary>The local install flow with its target fixed to the pressed side (the setting is not read).</summary>
    private bool BeginSideInstall(CatalogEntry entry, InstallTarget target, bool remoteAfter, bool update = false)
    {
        if (!string.Equals(_state.OpenKey, entry.Key, StringComparison.OrdinalIgnoreCase)) FocusListing(entry.Key);
        _targetOverride = target;
        _remoteAfterLocal = remoteAfter ? entry.Key : null;
        ApplyDefaultTarget(entry);
        OpenPrompt(entry, InstallPromptKind.Install);
        // An Update button's box says Update even when the row as a whole reads installed (one side
        // behind the listing, or behind the other side).
        if (update && !_promptUpdate)
        {
            _promptUpdate = true;
            ShowPromptDialog(entry);
        }

        // OpenPrompt keeps the override: it is ClosePrompt that clears it.
        _targetOverride = target;
        if (_phase == InstallActionPhase.Inspected && PlanSelected(_server) is { Ok: true, Plan: { } plan } && plan.CanExecuteToPending)
            return ContinueAfterInspection();
        return BeginDownloadAndCheck();
    }

    private bool OpenSidePrompt(CatalogEntry entry, InstallPromptKind kind, bool server)
    {
        if (!string.Equals(_state.OpenKey, entry.Key, StringComparison.OrdinalIgnoreCase)) FocusListing(entry.Key);
        // Set before the box is drawn, so its words name this side only.
        _promptSide = server ? PromptSide.Server : PromptSide.Client;
        return OpenPrompt(entry, kind);
    }

    /// <summary>The preloader's offer for this client's copy of a mod another tool installed, or null.</summary>
    private PlainOffer? PlainClientOffer(CatalogEntry entry)
    {
        var halves = HalvesOf(entry);
        if (halves.Client != InstalledState.HalfNone) return null;
        var detected = DetectInstalled(entry);
        if (detected.State is not (InstalledKind.Loaded or InstalledKind.ManagedElsewhere or InstalledKind.UpdateAvailable)) return null;
        var offer = PlainOfferFor(detected.PluginId);
        return offer is { CanUninstall: true } or { RemovalPending: true } ? offer : null;
    }

    private bool InvokePlain(CatalogEntry entry, PlainOffer offer, bool cancel)
    {
        var handler = PlainActionHandler;
        if (handler == null) return false;
        var detected = DetectInstalled(entry);
        handler(detected.PluginId, entry.DisplayName ?? entry.PackageName ?? detected.PluginId, cancel && offer.RemovalPending);
        return true;
    }

    /// <summary>The connected server's ledger row for this listing, when the remote route can manage it.</summary>
    internal RemoteServerRow? RemoteRowFor(CatalogEntry entry)
    {
        var client = RemoteServer;
        if (client == null || entry == null || CurrentRoute.Kind != ServerRouteKind.Remote) return null;
        return client.RowFor(entry, _installedQuery?.IdentityProbe);
    }

    /// <summary>REMOTEINST1: the remote state changed (a step, the status, the rows). Refresh the rows in place.</summary>
    internal void NoteRemoteChanged()
    {
        InvalidateSideCache();
        // REMOTETEST1 (live: a re-filter of ~290 rows every 1-2 s while a remote request ran): a
        // remote step or status packet rewrites the rows' cells in place. The whole list is rebuilt
        // (and so re-filtered) only when something the cells cannot show changed: the route, the
        // pending bar's counts, or a side cell that now needs a button its row was built without.
        bool structural = false;
        for (int i = 0; i < _rows.Count; i++)
        {
            try
            {
                SyncActionCells(_rows[i].Entry);
                bool fits = IsSimple ? SimpleButtonFits(_rows[i].Row, _rows[i].Entry) : SideButtonsFit(_rows[i].Row, _rows[i].Entry);
                if (!fits) structural = true;
            }
            catch { /* a row that went away with a rebuild */ }
        }

        string signature = RemoteSignature();
        if (!string.Equals(signature, _remoteBuiltSignature, StringComparison.Ordinal)) structural = true;
        if (structural)
        {
            _remoteRebuilds++;
            RequestDetailRebuild();
            return;
        }

        _remoteInPlace++;
        SyncToolbarAndChips();
    }

    private string? _remoteBuiltSignature;
    private int _remoteRebuilds;
    private int _remoteInPlace;

    /// <summary>REMOTETEST1 readback: remote changes that rebuilt the list, and those applied in place.</summary>
    internal (int Rebuilds, int InPlace) RemoteRefreshCounts => (_remoteRebuilds, _remoteInPlace);

    /// <summary>The route and the pending bar's counts: what a remote change can alter that only a rebuild redraws.</summary>
    private string RemoteSignature()
    {
        var route = CurrentRoute;
        return route.EffectiveName + "|" + route.BlockName + "|" + PendingCount() + "|" + PendingUninstallCount()
            // INSTALLSIMPLE1: Simple lists server-only mods only while it acts on the server.
            + (IsSimple ? "|simple:" + (SimpleActsOnServer ? "server" : "pc") : "");
    }

    // ---- JOINADMIN1: the alert's server update ---------------------------------------------------
    // The "Players joining" entry point moved from this toolbar to Progenitor's page (Server settings).

    /// <summary>
    /// JOINADMIN1: does the Server cell of the listing for plugin <paramref name="pluginId"/> offer Update now (the
    /// catalog knows a newer version than the server runs, through a route this player can use)? With
    /// <paramref name="start"/>, hand off to that Update exactly as its button does: the filters are cleared, the
    /// search names the listing, and the remote update's confirmation opens under its row. False with
    /// <paramref name="why"/> otherwise.
    /// </summary>
    internal bool ServerUpdateFor(string pluginId, bool start, out string listingKey, out string latest, out string why)
    {
        listingKey = latest = why = "";
        CatalogEntry? entry = null;
        try { entry = (_catalog as InstallCatalogService)?.FindEntryForPlugin(pluginId); }
        catch { entry = null; }
        if (entry == null)
        {
            why = "The package index does not list this mod.";
            return false;
        }

        listingKey = entry.Key;
        latest = entry.Manifest.Version ?? "";
        if (!SideCellsFor(entry).Server.Has(SideButton.Update))
        {
            why = "No server update is offered for " + entry.Key + " (the index's version is not newer, or no server route is open).";
            return false;
        }

        if (!start) return true;
        ClearFilters();
        Search = entry.Key;
        try { _toolbar?.SetCell(CellSearch, text: entry.Key); }
        catch { /* the field shows the old text until the next build */ }
        if (OnSideClick(entry.Key, server: true, SideButton.Update)) return true;
        why = "The update could not start (another step for this mod is running?).";
        return false;
    }

    /// <summary>Does every button this row's side cells want exist as a button cell in the built row?</summary>
    private bool SideButtonsFit(IUiCellRow row, CatalogEntry entry)
    {
        var sides = SideCellsFor(entry);
        return Fits(sides.Client, CellClientA, CellClientB) && Fits(sides.Server, CellServerA, CellServerB);

        bool Fits(SideCell cell, string aKey, string bKey) =>
            (cell.Buttons.Count < 1 || IsButtonCell(row, aKey)) && (cell.Buttons.Count < 2 || IsButtonCell(row, bKey));
    }

    private bool OpenRemoteInstallPrompt(CatalogEntry entry, RemoteInstallOp op)
    {
        RemoteServer?.ClearFinished(entry.Key);
        // Focus first: focusing a different listing resets the flow, which would drop the flag.
        if (!string.Equals(_state.OpenKey, entry.Key, StringComparison.OrdinalIgnoreCase)) FocusListing(entry.Key);
        ApplyDefaultTarget(entry);
        _promptRemoteOnly = true;
        _promptRemoteOp = op;
        return OpenPrompt(entry, InstallPromptKind.Install);
    }

    private bool ConfirmRemoteInstall(CatalogEntry entry)
    {
        var op = _promptRemoteOp;
        ClosePrompt();
        bool ok = SendRemote(entry, op, force: false);
        RequestDetailRebuild();
        return ok;
    }

    private bool SendRemote(CatalogEntry entry, RemoteInstallOp op, bool force)
    {
        if (op is RemoteInstallOp.Install or RemoteInstallOp.Update && LocalOnlyOnRemote(entry))
        {
            _actionError = InstallModsCopy.LocalOnlyServerSentence;
            return false;
        }

        var client = RemoteServer;
        if (client == null)
        {
            _actionError = ServerRouteCopy.RemoteOff;
            return false;
        }

        string version = op is RemoteInstallOp.Install or RemoteInstallOp.Update ? entry.Manifest.Version ?? "" : "";
        // F16: a listing the server's own local index offers installs at the version that index has.
        if (op is RemoteInstallOp.Install or RemoteInstallOp.Update && ServerLocalOffer(entry) is { Version.Length: > 0 } offer) version = offer.Version;
        int nonce = client.Begin(op, entry.Key, version, force, out string? refusal);
        if (nonce == 0)
        {
            _actionError = refusal ?? ServerRouteCopy.RemoteOff;
            return false;
        }

        _actionError = null;
        SyncActionCells(entry);
        return true;
    }

    /// <summary>
    /// REMOTEINST1: send the server copy's uninstall when the remote route manages one. Returns
    /// whether there was a server copy to act on; <paramref name="sent"/> says whether the request left.
    /// </summary>
    private bool TryRemoteUninstall(CatalogEntry entry, bool force, out bool sent)
    {
        sent = false;
        var row = RemoteRowFor(entry);
        if (row == null || row.State == RemoteRowState.UninstallPending) return false;
        sent = SendRemote(entry, RemoteInstallOp.Uninstall, force);
        return true;
    }

    /// <summary>
    /// REMOTEINST1: the row of a listing whose server half lives on the connected server, or null
    /// when the local rows decide. A remote-only listing (a Server mod, or the Server setting) is
    /// driven entirely by what the server reported: its step in flight, its ledger row, its last
    /// result.
    /// </summary>
    private InstallRowAction? RemoteActionFor(CatalogEntry entry)
    {
        var client = RemoteServer;
        if (client == null) return null;
        var flight = client.InFlightFor(entry.Key);
        if (flight != null)
        {
            return new InstallRowAction(
                InstallListingState.Installing, InstallModsCopy.CancelRemote, CellTone.Pending,
                flight.Stage < RemoteInstallStage.Installing, false, ServerRouteCopy.Stage(flight, ServerIdentityFor(remote: true)),
                status: InstallStatusLabels.Stage(flight), remote: true);
        }

        var route = CurrentRoute;
        if (route.Kind != ServerRouteKind.Remote)
        {
            // A remote step's refusal or failure stays on its row until the next operation on it,
            // even after the route changed; the button then follows the route the tab uses now.
            var done = client.LatestFor(entry.Key);
            if (done != null && done.Stage is RemoteInstallStage.Failed or RemoteInstallStage.Refused)
                return new InstallRowAction(InstallListingState.None, InstallModsCopy.Install, CellTone.Gold, true, true,
                    ServerRouteCopy.Failure(done), status: InstallStatusLabels.Failure(done), remote: false);
            return null;
        }

        var resolved = ResolvedTargetFor(entry);
        var row = RemoteRowFor(entry);
        bool remoteOnly = (resolved.ToConnectedServer && !resolved.LocalInstall)
            || (row != null && HalvesOf(entry).Client == InstalledState.HalfNone && !resolved.LocalInstall);
        if (!remoteOnly) return null;

        if (row != null)
        {
            string installedWords = string.Equals(row.Route, ServerInstallLedger.RouteRemote, StringComparison.Ordinal)
                ? ServerRouteCopy.InstalledRemote
                : ServerRouteCopy.InstalledLocal;
            switch (row.State)
            {
                case RemoteRowState.Pending:
                    return new InstallRowAction(InstallListingState.Pending, InstallModsCopy.InstalledPending, CellTone.Pending, true, true,
                        ServerRouteCopy.PendingSentence(
                            string.Equals(row.Route, ServerInstallLedger.RouteRemote, StringComparison.Ordinal),
                            ServerIdentityFor(remote: true)),
                        status: InstallStatusLabels.PendingServerRestart,
                        remote: true);
                case RemoteRowState.UninstallPending:
                    return new InstallRowAction(InstallListingState.PendingUninstall, InstallModsCopy.Reactivate, CellTone.Warn, false, true,
                        ServerRouteCopy.RemovedAtRestartSentenceFor(ServerIdentityFor(remote: true)), status: InstallStatusLabels.RemovalPendingServerRestart, remote: true);
                default:
                    string listed = entry.Manifest.Version ?? "";
                    if (listed.Length > 0 && row.Version.Length > 0 && !string.Equals(listed, row.Version, StringComparison.Ordinal)
                        && route.CanInstall)
                        return new InstallRowAction(InstallListingState.Update, InstallModsCopy.UpdateRestart, CellTone.Warn, true, true,
                            "The server has " + row.Version + "; " + listed + " is available.", status: installedWords, remote: true);
                    return new InstallRowAction(InstallListingState.Installed, InstallModsCopy.Installed, CellTone.Ok, true,
                        row.State != RemoteRowState.Hot, "", status: installedWords, remote: true);
            }
        }

        var last = client.LatestFor(entry.Key);
        if (last != null && last.IsTerminal)
        {
            switch (last.Stage)
            {
                case RemoteInstallStage.HotLoaded:
                    return new InstallRowAction(InstallListingState.Installed, InstallModsCopy.Installed, CellTone.Ok, false, false,
                        last.Reason == RemoteInstallReason.None ? "" : ServerRouteCopy.Failure(last),
                        status: ServerRouteCopy.InstalledRemote, remote: true);
                case RemoteInstallStage.PendingRestart when last.Op != RemoteInstallOp.Uninstall:
                    return new InstallRowAction(InstallListingState.Pending, InstallModsCopy.InstalledPending, CellTone.Pending, false, true,
                        ServerRouteCopy.PendingSentence(true, ServerIdentityFor(remote: true)), status: InstallStatusLabels.PendingServerRestart, remote: true);
                case RemoteInstallStage.Failed:
                case RemoteInstallStage.Refused:
                    return new InstallRowAction(InstallListingState.None, InstallModsCopy.Install, CellTone.Gold,
                        route.CanInstall && !InstallStatusLabels.WouldRepeat(last.Reason), true, // REMOTEFOLLOW1
                        ServerRouteCopy.Failure(last), status: InstallStatusLabels.Failure(last), remote: true);
            }
        }

        if (!route.CanInstall)
            return new InstallRowAction(InstallListingState.None, InstallModsCopy.Install, CellTone.Gold, false, true,
                (route.Reason + (route.Help.Length > 0 ? " " + route.Help : "")).Trim(), status: InstallStatusLabels.Route(route), remote: true);
        return new InstallRowAction(InstallListingState.None, InstallModsCopy.Install, CellTone.Gold, true, true, "", remote: true);
    }

    /// <summary>
    /// REMOTEINST1: a server-side row with no server route says why, in one line — the install
    /// itself still falls back to the client exactly as before.
    /// </summary>
    private InstallRowAction WithRouteNote(CatalogEntry entry, InstallRowAction action)
    {
        if (action.StatusText.Length > 0 || action.Remote) return action;
        var route = CurrentRoute;
        if (route.Kind != ServerRouteKind.None) return action;
        if (route.Block is ServerRouteBlock.None or ServerRouteBlock.NoServer) return action;
        if (SideOf(entry) != PackageSide.Server) return action;
        if (action.State is not (InstallListingState.None or InstallListingState.Update)) return action;
        return action.WithStatus(InstallStatusLabels.Route(route), (route.Reason + (route.Help.Length > 0 ? " " + route.Help : "")).Trim());
    }

    /// <summary>Set <see cref="Target"/> from the setting for this listing. Never refuses: the fallback is the client.</summary>
    private ResolvedInstallTarget ApplyDefaultTarget(CatalogEntry entry)
    {
        var resolved = _targetOverride is { } half
            ? new ResolvedInstallTarget(half, false, CurrentRoute, _remoteAfterLocal != null
                && string.Equals(_remoteAfterLocal, entry.Key, StringComparison.OrdinalIgnoreCase), true)
            : ResolvedTargetFor(entry);
        if (!TrySetTarget(resolved.Target, _server, out _))
        {
            _target = InstallTarget.Client;
            // LOCALDETECT1 review (live 2026-10-10): the box must say where the write goes. A server target this pane cannot
            // write is the client fallback in the box's words too, never "on <server>" over a client write.
            if (resolved.LocalInstall && resolved.Target != InstallTarget.Client)
                resolved = new ResolvedInstallTarget(InstallTarget.Client, true, resolved.Route, false, true);
        }

        _approvedHash = null;
        _promptResolved = resolved;
        _promptResolvedKey = entry.Key;
        return resolved;
    }

    /// <summary>
    /// INSTALLSIDES1: the target the open install box writes to, as decided when it opened — a side
    /// button's own half, else the setting's. Every sentence and fact of the box reads this, so the
    /// box names the side the player pressed.
    /// </summary>
    private ResolvedInstallTarget PromptResolvedFor(CatalogEntry entry) =>
        _promptResolved is { } resolved && string.Equals(_promptResolvedKey, entry.Key, StringComparison.OrdinalIgnoreCase)
            ? resolved
            : ResolvedTargetFor(entry);

    private ResolvedInstallTarget? _promptResolved;
    private string? _promptResolvedKey;

    /// <summary>INSTALLSIDES1: which side an uninstall, cancel or keep box acts on. Whole is every earlier caller's.</summary>
    private enum PromptSide
    {
        Whole = 0,
        Client = 1,
        Server = 2,
    }

    private PromptSide _promptSide;

    /// <summary>SRVUNINST1: the open uninstall box is the server copy's <c>Uninstall anyway</c> (sent with force).</summary>
    private bool _promptForce;

    /// <summary>SRVUNINST1 review: the open whole-row Uninstall box's client copy no ledger lists (null: not that box).</summary>
    private (bool Has, string Sentence)? _promptClientPlain;

    /// <summary>SRVUNINST1 review: a Yes that found a different client copy than its box showed (listing key, note).</summary>
    private (string Key, string Note)? _uninstallChanged;

    /// <summary>INSTALLSIDES1 readback <c>action.promptSide</c>.</summary>
    internal string PromptSideName => _promptSide switch
    {
        PromptSide.Client => "client",
        PromptSide.Server => "server",
        _ => "whole",
    };

    // ---- INSTALL2 item 3: the y/n confirmations ----------------------------------------

    /// <summary>The confirmation open under a row, or <see cref="InstallPromptKind.None"/>.</summary>
    public InstallPromptKind Prompt => _state.Prompt;

    /// <summary>The listing the open confirmation belongs to.</summary>
    public string? PromptKey => _state.Prompt == InstallPromptKind.None ? null : _state.PromptKey;

    /// <summary>INSTALL2 item 6: the installed mods that need the listing the open Uninstall confirmation would remove.</summary>
    internal IReadOnlyList<string> PromptDependents =>
        _state.Prompt == InstallPromptKind.Uninstall ? _promptDependents : Array.Empty<string>();

    /// <summary>The planned set behind the open Install confirmation (null when the catalog has no set planner).</summary>
    internal InstallPlanSet? PlanSet => _planSet;

    /// <summary>The set the last confirmed install wrote.</summary>
    internal ExecuteSetResult? LastExecuteSet => _executeSet;

    /// <summary>The set plan started by the last Install click, for tests to await.</summary>
    internal Task? ActiveSetPlan => _activeSetPlan;

    private bool PromptOpenFor(CatalogEntry entry) =>
        _state.Prompt != InstallPromptKind.None
        && entry != null
        && string.Equals(_state.PromptKey, entry.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Open one confirmation under <paramref name="entry"/>'s row, selecting that row so the box is
    /// drawn where the player pressed. The Uninstall box asks for the dependents first (item 6), so
    /// its Yes can read <see cref="InstallModsCopy.PromptYesUninstallAnyway"/> instead of refusing
    /// after the fact.
    /// </summary>
    private bool OpenPrompt(CatalogEntry entry, InstallPromptKind kind)
    {
        if (!string.Equals(_state.OpenKey, entry.Key, StringComparison.OrdinalIgnoreCase)) FocusListing(entry.Key);
        _promptUpdate = kind == InstallPromptKind.Install && IsUpdateRow(entry);
        _state.Prompt = kind;
        _state.PromptKey = entry.Key;
        _promptDependents = kind == InstallPromptKind.Uninstall ? DependentsOf(entry) : Array.Empty<string>();
        // SRVUNINST1 review: the client copy no ledger lists, as this box shows it; Yes acts on exactly this.
        _promptClientPlain = kind == InstallPromptKind.Uninstall && _promptSide == PromptSide.Whole ? WholeClientPlain(entry) : null;
        _uninstallChanged = null;
        // INSTALLUX1 item 1: an install box opens before its plan exists; every other kind decides
        // from facts the pane already holds, so it is ready the moment it opens.
        _promptPlanReady = kind != InstallPromptKind.Install || _promptRemoteOnly;
        _promptRefusal = null;
        _promptYesPending = false;
        _actionError = null;
        // LOCALDETECT1 review: the server write root this box was opened against, held until its Yes.
        _promptServerRoot = ServerWriteRoot();
        _promptServerRootPinned = true;
        // INSTALLUX2 item 1: the box is a modal dialog, and it opens on THIS click — not at the
        // pane's next rebuild, which may be posted.
        ShowPromptDialog(entry);
        RequestDetailRebuild();
        return true;
    }

    /// <summary>The confirmation's No: nothing is written, and an install's download approval is dropped.</summary>
    public bool PromptNo()
    {
        if (_state.Prompt == InstallPromptKind.None) return false;
        bool install = _state.Prompt == InstallPromptKind.Install;
        ClosePrompt();
        if (install) ResetFlow();
        RequestDetailRebuild();
        return true;
    }

    private void ClosePrompt()
    {
        // INSTALLUX2 item 1: the modal goes with the decision. Idempotent: the kit closed its own
        // panel before it called the handler that got us here.
        ClosePromptDialog();
        _state.Prompt = InstallPromptKind.None;
        _state.PromptKey = null;
        _promptUpdate = false;
        _promptDependents = Array.Empty<string>();
        _promptPlanReady = false;
        _promptRefusal = null;
        _promptYesPending = false;
        _promptRemoteOnly = false;
        _targetOverride = null;
        _promptSide = PromptSide.Whole;
        _promptForce = false;
        _promptClientPlain = null;
        _promptResolved = null;
        _promptResolvedKey = null;
        _promptSimpleBoth = false;
        _promptServerRoot = null;
        _promptServerRootPinned = false;
        _yesTarget = null;
    }

    private string? _yesTarget;

    /// <summary>
    /// LOCALDETECT1 review: where the open install box says each half goes: the resolved target, the remote send, the
    /// client fallback and the server it names (folder or address). The Yes records it; the write must still match.
    /// </summary>
    internal string BoxTargetSignature(CatalogEntry entry)
    {
        var r = PromptResolvedFor(entry);
        string server = "";
        try
        {
            var (id, both) = PromptServerFor(entry, InstallPromptKind.Install);
            server = id == null ? "-" : (id.Remote ? "remote:" + id.Address : "local:" + id.Folder.Trim().TrimEnd('\\', '/').ToLowerInvariant()) + (both ? "+client" : "");
        }
        catch { server = "?"; }

        return r.Target + "|" + r.LocalInstall + "|" + r.ToConnectedServer + "|" + r.FellBackToClient + "|" + server;
    }

    /// <summary>InstallModsCopy.ServerTargetChanged, logged with both targets; nothing is written.</summary>
    private bool RefuseTargetChanged(string agreed, string now)
    {
        try { Plugin.Logger?.LogWarning("Install: where this install goes changed after its confirmation (agreed " + agreed + ", now " + now + "); nothing was written."); }
        catch { /* */ }
        ClosePrompt();
        ResetFlow();
        _actionError = InstallModsCopy.ServerTargetChanged;
        RequestDetailRebuild();
        return false;
    }

    private string? _promptServerRoot;
    private bool _promptServerRootPinned;

    /// <summary>
    /// LOCALDETECT1 review: the server BepInEx root the catalog writes now (live: the catalog's
    /// <c>ServerBepInExRoot</c>, which follows the connection). Null probe: none known, so nothing is pinned.
    /// </summary>
    internal Func<string?>? ServerWriteRootProbe { get; set; }

    private string? ServerWriteRoot()
    {
        try { return ServerWriteRootProbe?.Invoke(); }
        catch { return null; }
    }

    /// <summary>
    /// LOCALDETECT1 review: the confirmation names a server folder; if the write root moved since it opened (the player
    /// joined another local server), its Yes refuses rather than write to a folder the box did not name.
    /// </summary>
    internal bool ServerRootMovedSincePrompt()
    {
        if (!_promptServerRootPinned || ServerWriteRootProbe == null) return false;
        string before = (_promptServerRoot ?? "").Trim().TrimEnd('\\', '/');
        string now = (ServerWriteRoot() ?? "").Trim().TrimEnd('\\', '/');
        return !string.Equals(before, now, StringComparison.OrdinalIgnoreCase);
    }

    private bool RefuseMovedServerRoot()
    {
        try { Plugin.Logger?.LogWarning("Install: the server write root changed from '" + (_promptServerRoot ?? "") + "' to '" + (ServerWriteRoot() ?? "") + "' while the confirmation was open; nothing was written."); }
        catch { /* */ }
        bool install = _state.Prompt == InstallPromptKind.Install;
        ClosePrompt();
        if (install) ResetFlow();
        _actionError = InstallModsCopy.ServerFolderChanged;
        RequestDetailRebuild();
        return false;
    }

    /// <summary>
    /// The confirmation's Yes. Install writes the planned set (dependencies first) or, with no set
    /// planner, the single approved plan; Uninstall marks a loaded copy for the next start or unloads a
    /// hot one now; Reactivate drops that mark; Cancel installing deletes the written pending file.
    /// </summary>
    public bool PromptYes()
    {
        var kind = _state.Prompt;
        string key = _state.PromptKey ?? "";
        if (kind == InstallPromptKind.None || key.Length == 0) return false;
        if (!_catalog.TryGetEntry(key, out var entry) || entry == null)
        {
            ClosePrompt();
            return false;
        }

        if (!(kind == InstallPromptKind.Install && _promptRemoteOnly) && PromptMovedOffItsServer(entry, kind)) return RefuseMovedServerRoot();

        switch (kind)
        {
            case InstallPromptKind.Install:
                // INSTALLUX1 item 1: "a Yes click before it finishes is honored when it finishes".
                // The button reads Yes, install throughout and the player never clicks twice.
                if (_promptRemoteOnly) return ConfirmRemoteInstall(entry);
                if (_promptRefusal != null) return false;
                // LOCALDETECT1 review: what this Yes agreed to (where each half goes); the write must match it.
                _yesTarget = BoxTargetSignature(entry);
                if (!_promptPlanReady)
                {
                    _promptYesPending = true;
                    RequestDetailRebuild();
                    return true;
                }

                return ConfirmInstall();
            case InstallPromptKind.Reactivate:
                return ConfirmReactivate(entry);
            case InstallPromptKind.CancelInstall:
                return ConfirmCancelInstall(entry);
            default:
                return ConfirmUninstall(entry);
        }
    }

    /// <summary>LOCALDETECT1 review: the box touches a local server folder, and the write root moved since it opened.</summary>
    private bool PromptMovedOffItsServer(CatalogEntry entry, InstallPromptKind kind)
    {
        if (!ServerRootMovedSincePrompt()) return false;
        // A box opened with a server folder in play may have named it: refuse. One opened with none refuses when its Yes
        // would now write a local server folder it never named.
        if (!string.IsNullOrWhiteSpace(_promptServerRoot)) return true;
        try { return PromptServerFor(entry, kind).Server is { Remote: false }; }
        catch { return true; /* cannot tell which server the box named: refuse */ }
    }

    private bool ConfirmInstall()
    {
        // LOCALDETECT1 review: a Yes honored when the plan finishes is checked too.
        if (_state.Prompt == InstallPromptKind.Install && !_promptRemoteOnly && _state.PromptKey is { Length: > 0 } pk
            && _catalog.TryGetEntry(pk, out var pinned) && pinned != null)
        {
            if (PromptMovedOffItsServer(pinned, InstallPromptKind.Install)) return RefuseMovedServerRoot();
            // LOCALDETECT1 review (live 2026-10-10: the box said "on <detected server>", the write went to the client):
            // one source for the box and the write. A target that differs from what the Yes agreed to, or a plan target
            // that differs from the box's, writes nothing.
            string now = BoxTargetSignature(pinned);
            var shown = PromptResolvedFor(pinned);
            bool planDiffers = shown.LocalInstall && shown.Target != _target;
            if (planDiffers || (_yesTarget != null && !string.Equals(_yesTarget, now, StringComparison.Ordinal)))
                return RefuseTargetChanged(_yesTarget ?? now, now + " (plan target " + _target + ")");
        }

        var set = _planSet;
        var executor = SetExecutorOrDefault;
        ClosePrompt();
        string? remoteKey = _remoteAfterLocal;
        _remoteAfterLocal = null;
        // The server half follows only the listing whose Install armed it.
        if (remoteKey != null && !string.Equals(remoteKey, (set?.Root?.Entry.Key ?? _state.PromptKey ?? _state.OpenKey), StringComparison.OrdinalIgnoreCase))
            remoteKey = null;
        if (set == null || executor == null)
        {
            // No set planner: the single approved plan is the whole install, exactly as before.
            if (!BeginReview()) return false;
            bool ok = ConfirmExecute().Ok;
            if (ok) SendRemoteHalf(remoteKey);
            return ok;
        }

        _phase = InstallActionPhase.Executing;
        SyncActionCells(set.Root!.Entry);
        ExecuteSetResult written;
        try { written = executor(set); }
        catch (Exception e) { written = ExecuteSetResult.Refuse(e.Message); }

        _executeSet = written;
        _planSet = null;
        AdoptSetResult(written);
        if (written.Ok) SendRemoteHalf(remoteKey);
        RequestDetailRebuild();
        return written.Ok;
    }

    /// <summary>REMOTEINST1: the server half of a Both install on the remote route.</summary>
    private void SendRemoteHalf(string? key)
    {
        if (string.IsNullOrEmpty(key) || !_catalog.TryGetEntry(key!, out var entry) || entry == null) return;
        if (LocalOnlyOnRemote(entry))
        {
            _actionError = InstallModsCopy.LocalOnlyServerSentence;
            InstallLog.Info("Install: " + entry.Key + ": installed on this client only; " + InstallModsCopy.LocalOnlyServerSentence);
            return;
        }

        if (CurrentRoute.Kind != ServerRouteKind.Remote || !CurrentRoute.CanInstall) return;
        var row = RemoteRowFor(entry);
        var op = row != null ? RemoteInstallOp.Update : RemoteInstallOp.Install;
        if (row != null && string.Equals(row.Version, entry.Manifest.Version ?? "", StringComparison.Ordinal)) return;
        SendRemote(entry, op, force: false);
    }

    /// <summary>
    /// Fold one written set into the row states: the root decides this row's caption, and every
    /// dependency that was written is a row of its own that is now pending or loaded.
    /// </summary>
    private void AdoptSetResult(ExecuteSetResult written)
    {
        if (!written.Ok)
        {
            _phase = InstallActionPhase.Failed;
            _actionError = written.Error ?? InstallReasons.FailedWriteNothing;
            // A set that stopped part-way still wrote the members before it; those rows are real.
            for (int i = 0; i < written.Members.Count; i++) NoteWritten(written.Members[i]);
            ForgetLoadedPlugins();
            return;
        }

        _phase = InstallActionPhase.Done;
        _actionError = null;
        bool anyHot = false;
        for (int i = 0; i < written.Members.Count; i++)
        {
            var member = written.Members[i];
            NoteWritten(member);
            anyHot |= member.HotLoaded;
            if (!member.IsRoot) continue;
            _executeResult = new ExecuteCallResult(true, null, member.Path, false, member.HotLoaded, null, member.ServerPendingPath);
            _resultSha256 = HashPendingFile(member.Path);
        }

        ForgetLoadedPlugins();
        if (anyHot) NoteInventoryChanged(InventoryReasonHotLoaded);
    }

    private void NoteWritten(ExecuteMemberResult member)
    {
        string key = member.Package;
        if (key.Length == 0) return;
        // INSTALLSIDES1: a write to the server's folder alone does not put anything on this client.
        if (_target == InstallTarget.LocalServer) _state.SessionServerOnly.Add(key);
        else _state.SessionServerOnly.Remove(key);
        _state.SessionUninstallPending.Remove(key);
        _state.SessionHotUnloaded.Remove(key);
        _state.SessionRemoved.Remove(key);
        if (member.HotLoaded)
        {
            _state.SessionInstalled.Add(key);
            _state.SessionHotLoaded.Add(key);
            _state.SessionPending.Remove(key);
        }
        else
        {
            _state.SessionPending.Add(key);
        }
    }

    private bool ConfirmUninstall(CatalogEntry entry)
    {
        bool force = _promptDependents.Count > 0 || _promptForce;
        bool hot = IsHotLoaded(entry);
        var marker = Marker;
        var halves = HalvesOf(entry);
        var side = _promptSide;
        // SRVUNINST1 review: the client copy no ledger lists goes only when the box named it (its sentence).
        var shownPlain = _promptClientPlain;
        var plain = side == PromptSide.Whole ? WholeClientPlain(entry) : (Has: false, Sentence: "");
        if (side == PromptSide.Whole && shownPlain is { } shown && (shown.Has != plain.Has || !string.Equals(shown.Sentence, plain.Sentence, StringComparison.Ordinal)))
        {
            // SRVUNINST1 review: the client copy is not what the box showed: nothing is done.
            ClosePrompt();
            _uninstallChanged = (entry.Key, ModUninstallPlan.ChangedNote);
            RequestDetailRebuild();
            return false;
        }

        ClosePrompt();
        if (side == PromptSide.Server) return ConfirmServerUninstall(entry, force);
        if (side == PromptSide.Client) return ConfirmClientUninstall(entry, force, hot, marker);

        // SRVUNINST1 (live 2026-10-07: Simple's one Uninstall of HotDemo reached only the server): a
        // client copy no ledger lists — a reload-folder file, an ordinary plugin — takes its own
        // remover, which the client cell offered. The box said what happens to it (WholeClientPlain), so no second one.
        bool plainDone = false;
        if (plain.Has && plain.Sentence.Length > 0 && PlainUninstallNow is { } removeNow)
        {
            string note;
            try { note = removeNow(DetectInstalled(entry).PluginId ?? "", entry.DisplayName ?? entry.PackageName ?? entry.Key); }
            catch (Exception e) { note = e.Message; }
            InstallLog.Info("Install: " + entry.Key + " client copy (not ledgered): " + note);
            plainDone = true;
        }

        // REMOTEINST1: a server copy on the connected server goes through the remote route. When this
        // machine holds nothing of it, that request is the whole uninstall.
        if (TryRemoteUninstall(entry, force, out bool remoteSent)
            && halves.Client == InstalledState.HalfNone && halves.Server == InstalledState.HalfNone)
        {
            RequestDetailRebuild();
            return remoteSent;
        }

        if (plainDone && halves.Client == InstalledState.HalfNone && halves.Server == InstalledState.HalfNone)
        {
            ForgetLoadedPlugins();
            RequestDetailRebuild();
            return true;
        }

        // POLISH3 item A: one Yes acts on EVERY half the file is on. The client's half takes the
        // route its own state asks for (below); the local server's is its own decision, because the
        // two halves can disagree — live (deploy #74) HotDemo's client half was hot loaded while the
        // server's was a downloaded `progenitor-pending/` copy, and the single client-shaped route
        // left that copy and its ledger row behind.
        string serverDid = UninstallHalfNothing;
        bool serverOk = true;
        if (halves.Server == InstalledState.HalfPending)
        {
            // Nothing loaded it: canceling the pending install is deleting the downloaded file.
            serverOk = ActOnServerHalf(entry, force, mark: false);
            serverDid = serverOk ? UninstallHalfCancelled : UninstallHalfFailed;
        }
        else if (halves.Server == InstalledState.HalfLoaded)
        {
            // A promoted copy the local server has loaded is marked for its next start, unless
            // (HOTDEMO1) that server's reload folder is watched and the copy is a trusted hotLoadable
            // image: then deleting it is the unload.
            bool now = LocalServerHalfUnloadsNow(entry, log: true);
            serverOk = ActOnServerHalf(entry, force, mark: !now);
            serverDid = serverOk ? (now ? UninstallHalfUnloaded : UninstallHalfMarked) : UninstallHalfFailed;
        }

        bool serverActed = serverDid != UninstallHalfNothing;
        // The client's half is only narrowed when the server's was handled separately; with no server
        // half in play the call stays exactly the whole-catalog one every earlier flow made.
        var clientScope = serverActed ? InstallTarget.Client : InstallTarget.Both;

        if (serverActed && halves.Client == InstalledState.HalfNone)
        {
            // Nothing of it on this side. The server half was the whole uninstall.
            _lastUninstallHalves = (UninstallHalfNothing, serverDid);
            _actionError = serverOk ? null : _actionError;
            ForgetMarkedPackages();
            ForgetLoadedPlugins();
            NoteInventoryChanged(InventoryReasonUninstalled);
            RequestDetailRebuild();
            return serverOk;
        }

        // A hot-loadable Progenitor mod keeps today's verified behavior: the reload copy goes and the
        // reloader drops the assembly within a second. Nothing is marked, because nothing survives to
        // the next start.
        if (hot || marker == null)
        {
            bool clientOk = OnRemoveClick(entry.Key, force, clientScope);
            _lastUninstallHalves = (
                clientOk ? (hot ? UninstallHalfUnloaded : UninstallHalfRemoved) : UninstallHalfFailed,
                serverDid);
            if (serverActed) NoteInventoryChanged(InventoryReasonUninstalled);
            return clientOk && serverOk;
        }

        ServerInstallResult result;
        try { result = Mark(entry.Key, mark: true, force, clientScope); }
        catch (Exception e) { result = ServerInstallResult.Refuse(e.Message); }

        _lastRemoveResult = result;
        if (!result.Ok || result.NoOp)
        {
            // Nothing of it is ledgered here, or the mark was refused: say so rather than drawing an
            // orange row for a removal that did not happen.
            _actionError = result.Ok ? InstallModsCopy.RemovedPending : (result.Message ?? InstallModsCopy.RemoveFailed);
            _lastUninstallHalves = (UninstallHalfFailed, serverDid);
            RequestDetailRebuild();
            return false;
        }

        _state.SessionUninstallPending.Add(entry.Key);
        ForgetMarkedPackages();
        _actionError = null;
        _phase = InstallActionPhase.Idle;
        _lastPlan = null;
        _planSet = null;
        _executeResult = null;
        _lastUninstallHalves = (UninstallHalfMarked, serverDid);
        ForgetLoadedPlugins();
        if (serverActed) NoteInventoryChanged(InventoryReasonUninstalled);
        RequestDetailRebuild();
        return serverOk;
    }

    /// <summary>
    /// INSTALLSIDES1: the Client cell's Uninstall — this client's copy only. A hot copy unloads now;
    /// a loaded one is marked for the next start; nothing on a server is touched.
    /// </summary>
    private bool ConfirmClientUninstall(CatalogEntry entry, bool force, bool hot, Func<string, bool, bool, ServerInstallResult>? marker)
    {
        if (hot || marker == null)
        {
            bool ok = OnRemoveClick(entry.Key, force, InstallTarget.Client);
            _lastUninstallHalves = (ok ? (hot ? UninstallHalfUnloaded : UninstallHalfRemoved) : UninstallHalfFailed, UninstallHalfNothing);
            return ok;
        }

        ServerInstallResult result;
        try { result = Mark(entry.Key, mark: true, force, InstallTarget.Client); }
        catch (Exception e) { result = ServerInstallResult.Refuse(e.Message); }

        _lastRemoveResult = result;
        if (!result.Ok || result.NoOp)
        {
            _actionError = result.Ok ? InstallModsCopy.RemovedPending : (result.Message ?? InstallModsCopy.RemoveFailed);
            _lastUninstallHalves = (UninstallHalfFailed, UninstallHalfNothing);
            RequestDetailRebuild();
            return false;
        }

        _state.SessionUninstallPending.Add(entry.Key);
        ForgetMarkedPackages();
        _actionError = null;
        _lastUninstallHalves = (UninstallHalfMarked, UninstallHalfNothing);
        ForgetLoadedPlugins();
        RequestDetailRebuild();
        return true;
    }

    /// <summary>
    /// INSTALLSIDES1: the Server cell's Uninstall — the picked server's copy only. The remote route
    /// sends the server's own uninstall; the local folder cancels a pending copy or marks a loaded one
    /// for that server's next start. This client's copy is never touched.
    /// </summary>
    private bool ConfirmServerUninstall(CatalogEntry entry, bool force)
    {
        if (CurrentRoute.Kind == ServerRouteKind.Remote)
        {
            bool had = TryRemoteUninstall(entry, force, out bool sent);
            RequestDetailRebuild();
            return had && sent;
        }

        var halves = HalvesOf(entry);
        bool pending = halves.Server == InstalledState.HalfPending
            || (halves.Server == InstalledState.HalfNone && _state.SessionServerOnly.Contains(entry.Key) && _state.SessionPending.Contains(entry.Key));
        bool now = !pending && LocalServerHalfUnloadsNow(entry, log: true); // HOTDEMO1, review L1: the box's own question
        bool ok = ActOnServerHalf(entry, force, mark: !pending && !now);
        _lastUninstallHalves = (UninstallHalfNothing, ok
            ? (pending ? UninstallHalfCancelled : now ? UninstallHalfUnloaded : UninstallHalfMarked)
            : UninstallHalfFailed);
        if (ok && pending)
        {
            _state.SessionPending.Remove(entry.Key);
            _state.SessionServerOnly.Remove(entry.Key);
        }
        ForgetLoadedPlugins();
        NoteInventoryChanged(InventoryReasonUninstalled);
        RequestDetailRebuild();
        return ok;
    }

    /// <summary>
    /// POLISH3 item A: take the local server's half of one listing — delete its downloaded
    /// <c>progenitor-pending/</c> copy (<paramref name="mark"/> false) or write the marker its next
    /// start honors (<paramref name="mark"/> true). Never touches the client's roots, so the client
    /// half keeps whichever route its own state asks for. Returns false with
    /// <see cref="_actionError"/> set when the server root refused.
    /// </summary>
    /// <summary>HOTDEMO1: the local server unloads its copy now when it is removed (see <see cref="InstallCatalogService.ServerUnloadsNow"/>); a test substitutes <see cref="ServerUnloadsNowProbe"/>.</summary>
    internal Func<CatalogEntry, bool>? ServerUnloadsNowProbe { get; set; }

    private bool ServerUnloadsNow(CatalogEntry entry, bool log = true)
    {
        try
        {
            if (ServerUnloadsNowProbe is { } probe) return probe(entry);
            if (Service is not { } service) return false;
            bool now = service.ServerUnloadsNow(entry, out string why);
            if (log) InstallLog.Info("Install: local server copy of " + entry.Key + (now ? " is unloaded now (" : " is marked for its next start (") + (now ? "trusted hotLoadable, reload folder watched" : why) + ").");
            return now;
        }
        catch { return false; }
    }

    private bool ActOnServerHalf(CatalogEntry entry, bool force, bool mark)
    {
        ServerInstallResult result;
        try
        {
            result = mark
                ? Mark(entry.Key, mark: true, force, InstallTarget.LocalServer)
                : Remove(entry.Key, force, InstallTarget.LocalServer);
        }
        catch (Exception e) { result = ServerInstallResult.Refuse(e.Message); }

        // A no-op is a failure here, not silence: the half was read off the ledgers a line ago, so
        // "nothing matched" means the removal could not name the row it can see. That silence is
        // exactly what left the live server copy behind — the Yes reported success and the Mods tab
        // went on listing the mod as `pendload`.
        if (!result.Ok || result.NoOp)
        {
            _actionError = result.Ok
                ? InstallModsCopy.ServerHalfNotTaken
                : (result.Message ?? InstallModsCopy.RemoveFailed);
            return false;
        }

        ForgetMarkedPackages();
        return true;
    }

    private bool ConfirmReactivate(CatalogEntry entry)
    {
        var marker = Marker;
        var side = _promptSide;
        ClosePrompt();
        if (side != PromptSide.Whole)
        {
            // INSTALLSIDES1: keep this side's copy; the other side's mark stands.
            ServerInstallResult kept;
            try { kept = Mark(entry.Key, mark: false, force: true, side == PromptSide.Server ? InstallTarget.LocalServer : InstallTarget.Client); }
            catch (Exception e) { kept = ServerInstallResult.Refuse(e.Message); }
            _lastRemoveResult = kept;
            if (!kept.Ok)
            {
                _actionError = kept.Message ?? InstallModsCopy.RemoveFailed;
                RequestDetailRebuild();
                return false;
            }

            _state.SessionUninstallPending.Remove(entry.Key);
            ForgetMarkedPackages();
            _actionError = null;
            ForgetLoadedPlugins();
            RequestDetailRebuild();
            return true;
        }

        if (marker == null)
        {
            _actionError = InstallModsCopy.RemoveFailed;
            RequestDetailRebuild();
            return false;
        }

        ServerInstallResult result;
        try { result = marker(entry.Key, false, true); }
        catch (Exception e) { result = ServerInstallResult.Refuse(e.Message); }

        _lastRemoveResult = result;
        if (!result.Ok)
        {
            _actionError = result.Message ?? InstallModsCopy.RemoveFailed;
            RequestDetailRebuild();
            return false;
        }

        _state.SessionUninstallPending.Remove(entry.Key);
        ForgetMarkedPackages();
        _actionError = null;
        ForgetLoadedPlugins();
        RequestDetailRebuild();
        return true;
    }

    private bool ConfirmCancelInstall(CatalogEntry entry)
    {
        var side = _promptSide;
        ClosePrompt();
        if (side == PromptSide.Server)
        {
            if (CurrentRoute.Kind == ServerRouteKind.Remote)
            {
                bool had = TryRemoteUninstall(entry, force: false, out bool remoteSent);
                RequestDetailRebuild();
                return had && remoteSent;
            }

            bool ok = ActOnServerHalf(entry, force: true, mark: false);
            if (ok)
            {
                _state.SessionPending.Remove(entry.Key);
                _state.SessionServerOnly.Remove(entry.Key);
            }
            ForgetLoadedPlugins();
            NoteInventoryChanged(InventoryReasonUninstalled);
            RequestDetailRebuild();
            return ok;
        }

        if (side == PromptSide.Client) return OnRemoveClick(entry.Key, force: true, InstallTarget.Client);

        // REMOTEINST1: a pending copy on the connected server is canceled there. Only a pending one:
        // a loaded server copy is never taken by a Cancel install aimed at this client's pending half.
        if (RemoteRowFor(entry) is { State: RemoteRowState.Pending }
            && TryRemoteUninstall(entry, force: false, out bool sent)
            && HalvesOf(entry).Client == InstalledState.HalfNone)
        {
            RequestDetailRebuild();
            return sent;
        }

        return OnRemoveClick(entry.Key, force: true);
    }

    /// <summary>
    /// Player action: <c>Download and check</c> (the first half of Install). One call per
    /// click while idle, failed or canceled; a second click while busy is ignored. The result
    /// is applied through the marshaler for the page token captured here.
    /// </summary>
    public bool BeginDownloadAndCheck()
    {
        if (_phase == InstallActionPhase.Downloading || _phase == InstallActionPhase.Executing) return false;
        if (string.IsNullOrEmpty(_state.OpenKey) || !_catalog.TryGetEntry(_state.OpenKey, out var entry) || entry == null)
            return false;

        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        _downloadKey = entry.Key;
        _downloadVersion = entry.Manifest.Version ?? "";
        _actionError = null;
        _approvedHash = null;
        _executeResult = null;
        _resultSha256 = null;
        _phase = InstallActionPhase.Downloading;
        int token = _pageToken;
        string key = entry.Key;
        string version = _downloadVersion;
        // Immediate in-place feedback before the posted rebuild lands.
        SyncActionCells(entry);
        _activeDownload = DownloadAndInspectFlowAsync(key, version, token, cts.Token);
        RequestDetailRebuild();
        return true;
    }

    private async Task DownloadAndInspectFlowAsync(string key, string version, int token, CancellationToken ct)
    {
        InspectCallResult result;
        try
        {
            result = await _catalog.DownloadAndInspectAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new InspectCallResult(false, InstallReasons.FetchCancelled, null, null);
        }
        catch (Exception e)
        {
            result = new InspectCallResult(false, e.Message, null, null);
        }

        _post(() => CompleteDownload(key, version, token, result));
    }

    private void CompleteDownload(string key, string version, int token, InspectCallResult result)
    {
        bool samePage = token == _pageToken
            && string.Equals(key, _state.OpenKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(version, _downloadVersion ?? "", StringComparison.Ordinal)
            && _phase == InstallActionPhase.Downloading;
        if (!samePage)
        {
            _droppedCompletions++;
            if (!_dropLogged)
            {
                _dropLogged = true;
                try
                {
                    Plugin.Logger?.LogInfo(
                        "Install: dropped a download completion for " + key + " " + version
                        + " (page changed or pane released); nothing applied.");
                }
                catch { /* */ }
            }

            return;
        }

        _activeDownload = null;
        var cts = _downloadCts;
        _downloadCts = null;
        try { cts?.Dispose(); }
        catch { /* */ }
        if (result.Ok)
        {
            _phase = InstallActionPhase.Inspected;
            _actionError = null;
            ContinueAfterInspection();
            return;
        }

        bool cancelled = string.Equals(result.Error, InstallReasons.FetchCancelled, StringComparison.Ordinal);
        _phase = cancelled ? InstallActionPhase.Cancelled : InstallActionPhase.Failed;
        _actionError = cancelled ? InstallModsCopy.DownloadCancelled : (result.Error ?? InstallReasons.FetchTransport);
        // INSTALLUX1 item 1: the box opened on the click, so a download that never arrived says so
        // in the box instead of leaving it promising an install that cannot happen.
        RefuseOpenInstallPrompt(_actionError);
        RequestDetailRebuild();
    }

    /// <summary>
    /// After a successful inspection: a blocked plan shows <c>Cannot install</c> with the
    /// planner reason; with one target the plan is approved and written at once; with more the
    /// chooser opens and the chosen target is approved and written on its own click (job CONF2).
    /// </summary>
    private bool ContinueAfterInspection()
    {
        _phase = InstallActionPhase.Inspected;

        // INSTALLUX1 item 2: the inspection is what makes a package's side knowable when the index
        // did not carry it (progenitor.json's `side`, or [BepInProcess]). The target was resolved
        // from the listing on the click; re-resolve it here against the inspected entry, or a server
        // mod with no Server category on the store would land on the client under "Follow the mod's
        // side" and the box would call it a Client mod.
        if (!string.IsNullOrEmpty(_state.OpenKey)
            && _catalog.TryGetEntry(_state.OpenKey!, out var inspected) && inspected != null)
            ApplyDefaultTarget(inspected);

        // INSTALL2 defect B (live, deploy #69): the closure is planned FIRST when there is a set
        // planner. Asking PlanSelected first refused the whole install on the very dependency the set
        // would have installed — AutoLatchGates read `blocked / Cannot install` with
        // `closureCount 0` and no prompt, because the root's own INSTORD1 rule says "requires
        // ExampleCommandFramework — install it first". PlanWithDependenciesAsync plans the missing
        // members and then plans the root with them counted as satisfied, so that rule no longer fires.
        var planner = SetPlannerOrDefault;
        string key = _state.OpenKey ?? "";
        if (planner != null && key.Length > 0)
        {
            BeginSetPlan(planner, key);
            return true;
        }

        // No set planner (a catalog without one): the single plan is the whole install.
        var planned = PlanSelected(_server);
        if (!planned.Ok || planned.Plan == null || !planned.Plan.CanExecuteToPending)
        {
            _lastPlan = planned.Plan;
            _actionError = planned.Error ?? planned.Plan?.Reason ?? InstallReasons.PlanBlocked;
            RefuseOpenInstallPrompt(_actionError);
            RequestDetailRebuild();
            return false;
        }

        _lastPlan = planned.Plan;
        OpenInstallPrompt();
        return true;
    }

    /// <summary>
    /// DEPS1: plan the opened listing with its dependency closure. The members are downloaded and
    /// inspected, so this is async; the confirmation opens on the marshaler for the page token
    /// captured here, and a refused set leaves <c>Cannot install — &lt;dep&gt; refused: …</c> under the
    /// row instead of a box that promises an install that cannot happen.
    /// </summary>
    private void BeginSetPlan(Func<string, InstallTarget, CancellationToken, Task<InstallPlanSet>> planner, string key)
    {
        int token = _pageToken;
        var target = _target;
        _activeSetPlan = PlanSetAsync(planner, key, target, token);
    }

    private async Task PlanSetAsync(
        Func<string, InstallTarget, CancellationToken, Task<InstallPlanSet>> planner,
        string key,
        InstallTarget target,
        int token)
    {
        InstallPlanSet set;
        try { set = await planner(key, target, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { set = InstallPlanSet.Refuse(e.Message); }

        _post(() =>
        {
            if (token != _pageToken || !string.Equals(key, _state.OpenKey, StringComparison.OrdinalIgnoreCase))
            {
                _droppedCompletions++;
                return;
            }

            if (!set.Ok)
            {
                _planSet = null;
                _actionError = set.Error ?? InstallReasons.PlanBlocked;
                _phase = InstallActionPhase.Inspected;
                // The row must say why, and the readback's plan object is what the bridge reads the
                // refusal off: keep the root's own (blocked) plan behind it when there is one.
                var planned = PlanSelected(_server);
                _lastPlan = planned.Plan;
                // INSTALLUX1 item 1: the box is already open, so the refusal replaces its body
                // rather than closing a box the player is looking at.
                RefuseOpenInstallPrompt(_actionError);
                RequestDetailRebuild();
                return;
            }

            _planSet = set;
            _lastPlan = set.Root;
            OpenInstallPrompt();
        });
    }

    /// <summary>
    /// The inspection finished and the plan is good: the box that has been open since the click
    /// (INSTALLUX1 item 1) is now ready, so its body becomes the full summary and a Yes pressed
    /// while it was checking is honored here — once.
    /// </summary>
    private void OpenInstallPrompt()
    {
        string key = _state.OpenKey ?? "";
        if (key.Length == 0 || !_catalog.TryGetEntry(key, out var entry) || entry == null) return;
        _state.Prompt = InstallPromptKind.Install;
        _state.PromptKey = entry.Key;
        _promptDependents = Array.Empty<string>();
        _promptPlanReady = true;
        _promptRefusal = null;
        if (_promptYesPending)
        {
            _promptYesPending = false;
            ConfirmInstall();
            return;
        }

        // INSTALLUX2 item 1: the dialog was built from the words the box said while it was
        // checking; the full summary is a different body, so it is re-drawn here.
        ShowPromptDialog(entry);
        RequestDetailRebuild();
    }

    /// <summary>
    /// INSTALLUX1 item 1: the open install box could not be planned. Its body becomes
    /// <c>Cannot install — &lt;reason&gt;</c> and its only button is <c>Close</c>; a Yes that was
    /// waiting is dropped, because there is nothing to say yes to.
    /// </summary>
    private void RefuseOpenInstallPrompt(string? reason)
    {
        if (_state.Prompt != InstallPromptKind.Install)
        {
            ClosePrompt();
            return;
        }

        _promptPlanReady = true;
        _promptYesPending = false;
        _promptRefusal = string.IsNullOrWhiteSpace(reason) ? InstallReasons.PlanBlocked : reason!.Trim();
        // INSTALLUX2 item 1: the refusal replaces the open dialog's body and leaves it one button.
        ReopenPromptDialog();
    }

    /// <summary>The drawer's Cancel of a running download; a review is left (approval dropped).</summary>
    public void CancelPrimary()
    {
        if (_phase == InstallActionPhase.Downloading)
        {
            var cts = _downloadCts;
            _downloadCts = null;
            _pageToken++;
            try { cts?.Cancel(); }
            catch { /* */ }
            try { cts?.Dispose(); }
            catch { /* */ }
            _activeDownload = null;
            _phase = InstallActionPhase.Cancelled;
            _actionError = InstallModsCopy.DownloadCancelled;
            RequestDetailRebuild();
            return;
        }

        if (_phase == InstallActionPhase.Reviewing)
        {
            _approvedHash = null;
            _phase = InstallActionPhase.Inspected;
            _actionError = null;
            RequestDetailRebuild();
        }
    }

    /// <summary>Approve the current plan (the review step). Confirm must follow with the same plan hash.</summary>
    public bool BeginReview()
    {
        if (_phase == InstallActionPhase.Downloading || _phase == InstallActionPhase.Executing) return false;
        var planned = PlanSelected(_server);
        if (!planned.Ok || planned.Plan == null || !planned.Plan.CanExecuteToPending)
        {
            _actionError = planned.Error ?? planned.Plan?.Reason ?? InstallReasons.PlanBlocked;
            _phase = InstallActionPhase.Inspected;
            RequestDetailRebuild();
            return false;
        }

        var approved = _catalog.Approve(planned.Plan.PlanHash);
        if (!approved.Ok)
        {
            _actionError = approved.Error ?? InstallReasons.ApprovalMissing;
            _phase = InstallActionPhase.Inspected;
            RequestDetailRebuild();
            return false;
        }

        _lastPlan = planned.Plan;
        _approvedHash = planned.Plan.PlanHash;
        _approvedTarget = _target;
        _actionError = null;
        _phase = InstallActionPhase.Reviewing;
        return true;
    }

    /// <summary>
    /// The write: the plan is recomputed and must still hash to the approved one (a
    /// target change or changed bytes refuse it); then execute to pending. A restart plan
    /// leaves the row <see cref="InstallListingState.Pending"/>; a hot-load plan with an
    /// executor leaves it <see cref="InstallListingState.Installed"/>.
    /// </summary>
    public ExecuteCallResult ConfirmExecute()
    {
        if (_phase != InstallActionPhase.Reviewing || string.IsNullOrEmpty(_approvedHash))
            return new ExecuteCallResult(false, InstallReasons.ApprovalMissing, null, false);

        var current = PlanSelected(_server);
        if (!current.Ok || current.Plan == null
            || _target != _approvedTarget
            || !string.Equals(current.Plan.PlanHash, _approvedHash, StringComparison.OrdinalIgnoreCase))
        {
            _approvedHash = null;
            _phase = InstallActionPhase.Inspected;
            _actionError = current.Ok && current.Plan != null ? InstallReasons.ApprovalInvalid : (current.Error ?? InstallReasons.ApprovalInvalid);
            RequestDetailRebuild();
            return new ExecuteCallResult(false, _actionError, null, false);
        }

        _phase = InstallActionPhase.Executing;
        var executed = _catalog.ExecuteToPending(_approvedHash);
        _executeResult = executed;
        _approvedHash = null;
        if (executed.Ok)
        {
            _phase = InstallActionPhase.Done;
            _actionError = null;
            _resultSha256 = HashPendingFile(executed.PendingPath);
            string key = _state.OpenKey ?? "";
            if (key.Length > 0)
            {
                if (PlanNeedsRestart(current.Plan)) _state.SessionPending.Add(key);
                else _state.SessionInstalled.Add(key);
                // INSTALLSIDES1: a write to the server's folder alone put nothing on this client.
                if (_target == InstallTarget.LocalServer) _state.SessionServerOnly.Add(key);
                else _state.SessionServerOnly.Remove(key);
                // HOT3: this write went into progenitor-reload/ in this process, so the row's Remove
                // is a hot unload and the Mods tab has a plugin it has not listed yet.
                // HOT3: an install undoes this session's removals of the same listing.
                _state.SessionHotUnloaded.Remove(key);
                if (executed.HotLoaded)
                {
                    _state.SessionHotLoaded.Add(key);
                    _state.SessionRemoved.Remove(key);
                }
            }

            ForgetLoadedPlugins();
            if (executed.HotLoaded) NoteInventoryChanged(InventoryReasonHotLoaded);
        }
        else
        {
            _phase = InstallActionPhase.Failed;
            _actionError = executed.Error ?? InstallReasons.FailedWriteNothing;
        }

        RequestDetailRebuild();
        return executed;
    }

    private static string? HashPendingFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            return CatalogFetcher.HashFile(path);
        }
        catch { return null; }
    }

    /// <summary>
    /// Set the target. A change after inspection drops the approval; the plan is recomputed.
    /// INSTALLUX1 item 2 removed the chooser chips that used to call this from the pane; it stays
    /// for <c>ApplyDefaultTarget</c> and the bridge.
    /// </summary>
    public bool SelectTarget(InstallTarget target)
    {
        if (_phase == InstallActionPhase.Downloading || _phase == InstallActionPhase.Executing) return false;
        if (!TrySetTarget(target, _server, out _)) return false;
        _approvedHash = null;
        if (_phase == InstallActionPhase.Reviewing) _phase = InstallActionPhase.Inspected;
        _actionError = null;
        return true;
    }

    public bool TrySetTarget(InstallTarget target, LocalServerFolder? server, out string? refusal)
    {
        if (target == InstallTarget.Client)
        {
            _target = InstallTarget.Client;
            refusal = null;
            return true;
        }

        // LOCALDETECT1 review: the route's own local folder (the private game's, or the auto-detected server's) is a server
        // target too: the catalog writes there (its override root), so the configured folder is not required.
        if (RouteLocalFolderWritable())
        {
            _target = target;
            refusal = null;
            return true;
        }

        if (server == null || !server.HasValidatedServer)
        {
            refusal = server != null && server.IsUnavailable
                ? InstallModsCopy.TargetUnavailable
                : InstallReasons.NoConfiguredLocalServer;
            return false;
        }

        _target = target;
        refusal = null;
        return true;
    }

    /// <summary>
    /// Target control. My client is always present and is the default. Local server and
    /// Both appear only after Configuration has a validated folder. Both is never implied
    /// by package metadata. An invalid-later folder keeps the choices visible but unavailable.
    /// </summary>
    public static IReadOnlyList<InstallTargetChoice> TargetChoices(LocalServerFolder? server)
    {
        var list = new List<InstallTargetChoice>(3)
        {
            new(InstallTarget.Client, InstallModsCopy.TargetClient, available: true),
        };
        if (server == null || (!server.HasValidatedServer && !server.IsUnavailable))
            return list;

        bool available = server.HasValidatedServer;
        list.Add(new(InstallTarget.LocalServer, InstallModsCopy.TargetServer, available));
        list.Add(new(InstallTarget.Both, InstallModsCopy.TargetBoth, available));
        return list;
    }

    public static InstallTarget DefaultTarget => InstallTarget.Client;

    /// <summary>Package metadata never expands the selected targets. Both is explicit or not used.</summary>
    public static InstallTarget ResolveTarget(InstallTarget? selected, PackageSide side, LocalServerFolder? server)
    {
        _ = side;
        _ = server;
        return selected ?? InstallTarget.Client;
    }

    public static bool BothImpliedByMetadata(PackageSide side) => false;

    public static bool TargetOperationBlocked(InstallTarget target, LocalServerFolder? server)
    {
        if (target == InstallTarget.Client) return false;
        return server == null || !server.HasValidatedServer;
    }

    /// <summary>
    /// HOT2: this pane can now hot load. All four gates exist — a reviewed qualification record, an
    /// available Bootstrap admission snapshot, the apply-now executor, and the recheck immediately
    /// before its write — so a confirmed install of an admitted package lands in
    /// <c>progenitor-reload/</c> and the reloader picks it up within a second.
    ///
    /// <para>This is a statement about the <em>lane</em>, not about any one package. The pane itself
    /// still never writes a reload file: it calls <c>_catalog.ExecuteToPending</c>, and the service
    /// routes to <c>HotLoadExecutor</c> only for a plan that is <c>ApplyNowEligible</c> on the root
    /// this process runs. Everything else is still the restart route.</para>
    /// </summary>
    public static bool CanHotLoadFromThisPane => true;

    /// <summary>
    /// LOCALDETECT1 review: the route is local to a folder other than the configured one (the private game's or the
    /// auto-detected server's), not forced remote, and the catalog's server write root is that folder.
    /// </summary>
    internal bool RouteLocalFolderWritable()
    {
        try
        {
            var route = CurrentRoute;
            if (route.Kind != ServerRouteKind.Local || route.ForcedRemote) return false;
            if (HostedRouteFolder() is not { Length: > 0 } folder) return false;
            if (ServerWriteRootProbe == null) return true;
            string root = (ServerWriteRoot() ?? "").Trim().TrimEnd('\\', '/');
            string want = System.IO.Path.Combine(folder, LocalServerFolder.BepInExFolderName).TrimEnd('\\', '/');
            return string.Equals(System.IO.Path.GetFullPath(root.Length == 0 ? "." : root), System.IO.Path.GetFullPath(want),
                StringComparison.OrdinalIgnoreCase) && root.Length > 0;
        }
        catch { return false; }
    }

    public PlanCallResult PlanSelected(LocalServerFolder? server)
    {
        if (string.IsNullOrEmpty(_state.OpenKey))
            return new PlanCallResult(false, InstallReasons.UnknownEntry, null);
        // LOCALDETECT1 review: a server half on the route's own folder plans against the catalog's override root.
        if (_target != InstallTarget.Client && RouteLocalFolderWritable())
            return _catalog.Plan(_state.OpenKey!, _target);
        if (TargetOperationBlocked(_target, server))
            return new PlanCallResult(false, server != null && server.IsUnavailable
                ? InstallModsCopy.TargetUnavailable
                : InstallReasons.NoConfiguredLocalServer, null);
        if (server != null && _target != InstallTarget.Client && !server.CanWrite(out _))
            return new PlanCallResult(false, InstallModsCopy.TargetUnavailable, null);
        return _catalog.Plan(_state.OpenKey, _target);
    }

    public ExecuteCallResult ExecuteToPending(string planHash)
    {
        var approved = _catalog.Approve(planHash);
        if (!approved.Ok)
            return new ExecuteCallResult(false, approved.Error ?? InstallReasons.ApprovalMissing, null, false);
        return _catalog.ExecuteToPending(planHash);
    }

    // ---- Row facts -------------------------------------------------------------------

    /// <summary>A Thunderstore <c>Tools</c> listing: never a plugin, never installable from here.</summary>
    public static bool IsTool(CatalogEntry entry) =>
        IsToolCategory(entry) || IsModManager(entry) || IsTooLarge(entry);

    /// <summary>Thunderstore tags it <c>Tools</c>.</summary>
    public static bool IsToolCategory(CatalogEntry entry)
    {
        if (entry == null) return false;
        for (int i = 0; i < entry.Categories.Count; i++)
        {
            if (string.Equals(entry.Categories[i], "Tools", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>FIX-NONMODS: a mod manager application (<see cref="NonModPackages.KnownModManagers"/>, by owner-name).</summary>
    public static bool IsModManager(CatalogEntry entry) =>
        entry != null && NonModPackages.IsModManager(entry.Owner, entry.PackageName, entry.Key);

    /// <summary>The download ceiling a listing is judged against (<c>[Install] MaxDownloadMB</c>); tests may set it.</summary>
    internal static long DownloadLimit
    {
        get => _downloadLimit ?? NonModPackages.DownloadLimit;
        set => _downloadLimit = value;
    }

    private static long? _downloadLimit;

    /// <summary>Tests: go back to the setting.</summary>
    internal static void ResetDownloadLimit() => _downloadLimit = null;

    /// <summary>FIX-NONMODS: the listing's published package is over the download ceiling.</summary>
    public static bool IsTooLarge(CatalogEntry entry) =>
        entry != null && NonModPackages.TooLarge(entry.ListingFileSize, DownloadLimit);

    /// <summary>
    /// The words a never-installable row shows (one rule for the side cells and the status): a tool,
    /// a mod manager app, or a package too large to download.
    /// </summary>
    internal static string NotInstallableText(CatalogEntry entry)
    {
        if (IsTooLarge(entry)) return NonModPackages.TooLargeText(entry.ListingFileSize!.Value, DownloadLimit);
        if (IsModManager(entry)) return NonModPackages.ModManagerText;
        return InstallModsCopy.ToolNotPlugin;
    }

    /// <summary>The drawer's sentence for a never-installable row (empty for a plain tool).</summary>
    internal static string NotInstallableReason(CatalogEntry entry)
    {
        if (IsTooLarge(entry)) return NonModPackages.TooLargeReason(entry.ListingFileSize!.Value, DownloadLimit);
        if (IsModManager(entry)) return NonModPackages.ModManagerReason;
        return "";
    }

    /// <summary>
    /// MODS2: is this listing the plugin loader? Decided by <see cref="LoaderPackages"/> from the
    /// listing's own owner/name metadata, never from a display name.
    /// </summary>
    public static bool IsLoader(CatalogEntry entry) =>
        entry != null && LoaderPackages.IsLoaderPackage(entry.Owner, entry.PackageName, entry.Key);

    /// <summary>
    /// MODS2: rows this menu never installs - a Thunderstore tool and the loader. Pending counts,
    /// filters and the drawer head ask this rather than naming the two states again.
    /// </summary>
    public static bool IsNotInstallable(CatalogEntry entry) => IsTool(entry) || IsLoader(entry);

    /// <summary>Does this plan need a restart? Only a hot-load plan with a live executor does not.</summary>
    public static bool PlanNeedsRestart(InstallPlan? plan) =>
        plan == null || plan.Decision != InstallDecision.ApplyNow || !plan.ExecutorAvailable;

    /// <summary>Before inspection: the listing's own declaration; after: the plan.</summary>
    public bool NeedsRestart(CatalogEntry entry)
    {
        if (IsOpen(entry) && _lastPlan != null) return PlanNeedsRestart(_lastPlan);
        return !InstallSearchFilters.DeclaresNoRestart(entry);
    }

    private bool IsOpen(CatalogEntry entry) =>
        entry != null && string.Equals(_state.OpenKey, entry.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>The side a listing loads in: the inspector's probe once it has looked, else the listing's derived side.</summary>
    public static PackageSide SideOf(CatalogEntry entry)
    {
        if (entry == null) return PackageSide.Unknown;
        var probe = ProcessFilterProbe;
        if (probe != null)
        {
            try
            {
                var probed = probe(entry);
                if (probed != null) return probed.Value;
            }
            catch { /* the listing's side stands */ }
        }
        return PackageSideDerivation.Derive(entry).Side;
    }

    public static CellTone SideTone(PackageSide side) => side switch
    {
        PackageSide.Server => CellTone.Server,
        PackageSide.Client => CellTone.Client,
        PackageSide.Both => CellTone.Both,
        _ => CellTone.Normal,
    };

    /// <summary>The drawer's <c>loads in …</c> phrase for a side (a tool reads <c>—</c>).</summary>
    public static string LoadsIn(PackageSide side, bool tool = false)
    {
        if (tool) return InstallModsCopy.Dash;
        return side switch
        {
            PackageSide.Client => InstallModsCopy.LoadsInClient,
            PackageSide.Server => InstallModsCopy.LoadsInServer,
            PackageSide.Both => InstallModsCopy.LoadsInBoth,
            _ => InstallModsCopy.LoadsInUnknown,
        };
    }

    public static string StateName(InstallListingState state) => state switch
    {
        InstallListingState.Removed => "removed",
        InstallListingState.Installing => "installing",
        InstallListingState.Installed => "installed",
        InstallListingState.Pending => "pending",
        InstallListingState.Update => "update",
        InstallListingState.Tool => "tool",
        InstallListingState.Loader => "loader",
        InstallListingState.Blocked => "blocked",
        InstallListingState.PendingUninstall => "penduninst",
        InstallListingState.Sides => "sides",
        _ => "none",
    };

    /// <summary>
    /// The one install fact of a row: state, button caption and tone, and the note under
    /// the drawer button. Installed state comes from <see cref="InstalledState.Detect"/> and
    /// this session's writes; the DL1 phase applies to the opened row only.
    /// </summary>
    public InstallRowAction ActionFor(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (!IsTool(entry) && !IsLoader(entry) && RemoteActionFor(entry) is { } remote)
            return remote.State == InstallListingState.Installing ? remote : WithWhereCaption(entry, WithSides(entry, remote));
        return WithWhereCaption(entry, WithSides(entry, WithRouteNote(entry, WithLocalRouteWords(entry, LocalActionFor(entry)))));
    }

    /// <summary>
    /// UI polish: an Install or Update button says where it writes — <c>Install</c> (this client),
    /// <c>Install on server</c> (local folder or remote route), <c>Install on both</c> — and never
    /// "· restart"; the Status label after the write says what restart it owes.
    /// </summary>
    private InstallRowAction WithWhereCaption(CatalogEntry entry, InstallRowAction action)
    {
        if (action.Plain) return action;
        bool install = action.Label is InstallModsCopy.Install or InstallModsCopy.InstallRestart;
        bool update = action.Label is InstallModsCopy.UpdateRestart;
        if (!install && !update) return action;
        if (action.State is not (InstallListingState.None or InstallListingState.Update or InstallListingState.Removed)) return action;
        string caption = WhereCaption(ResolvedTargetFor(entry), update);
        if (caption == action.Label) return action;
        return new InstallRowAction(action.State, caption, action.Tone, action.Enabled, action.Restart, action.Reason,
            action.Managed, action.PlainPluginId, action.ManagedWhy, action.StatusText, action.Remote);
    }

    internal static string WhereCaption(ResolvedInstallTarget resolved, bool update)
    {
        bool server = resolved.ToConnectedServer || resolved.Target is InstallTarget.LocalServer or InstallTarget.Both;
        bool client = resolved.ToConnectedServer ? resolved.LocalInstall : resolved.Target != InstallTarget.LocalServer;
        if (server && client) return update ? InstallModsCopy.UpdateOnBoth : InstallModsCopy.InstallOnBoth;
        if (server) return update ? InstallModsCopy.UpdateOnServer : InstallModsCopy.InstallOnServer;
        return update ? InstallModsCopy.UpdateHere : InstallModsCopy.Install;
    }

    // ---- REMOTEINST1: per-side status ------------------------------------------------------------

    /// <summary>
    /// The connected server's handshake list for one listing: whether the list is known at all, and
    /// the version it lists for this mod (null when it does not list it). Set by the Mods menu.
    /// </summary>
    internal Func<CatalogEntry, (bool Known, string? Version)>? ServerHandshakeProbe { get; set; }

    /// <summary>
    /// Mod compatibility C3: (listing, this client's version, the server's version) is accepted by the connected server's
    /// own judgment (<c>ServerVersionCheck</c> on its requirement: Version matching, per-mod rules, ranges). Null or false:
    /// two different versions are a version mismatch, as before.
    /// </summary>
    internal Func<CatalogEntry, string, string, bool>? ServerAcceptsProbe { get; set; }

    /// <summary>Only a manifest-verified <c>side: both</c> declaration — the mod page's required-server rule.</summary>
    internal static bool NeedsBoth(CatalogEntry entry) =>
        entry != null && entry.Provenance == CatalogProvenance.ManifestVerified && entry.Side == PackageSide.Both;

    /// <summary>The two halves of one listing and where the server half's state came from.</summary>
    internal SideInputs SideInputsOf(CatalogEntry entry)
    {
        var halves = HalvesOf(entry);
        string clientVersion = "", serverVersion = "";
        try
        {
            foreach (var row in InstalledState.LedgerRowsFor(entry, _installedQuery))
            {
                bool onServer = string.Equals(row.Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase);
                if (onServer && serverVersion.Length == 0) serverVersion = row.Version;
                if (!onServer && clientVersion.Length == 0) clientVersion = row.Version;
            }
        }
        catch { /* versions are evidence, never a gate */ }

        ClientHalfState client;
        if (halves.Client == InstalledState.HalfLoaded) client = ClientHalfState.Installed;
        else if (halves.Client == InstalledState.HalfPending) client = ClientHalfState.PendingRestart;
        else
        {
            var detected = DetectInstalled(entry);
            client = detected.State is InstalledKind.Loaded or InstalledKind.ManagedElsewhere or InstalledKind.UpdateAvailable
                ? ClientHalfState.Installed
                : ClientHalfState.Absent;
            if (client == ClientHalfState.Installed && clientVersion.Length == 0) clientVersion = detected.LoadedVersion ?? "";
        }

        if (client != ClientHalfState.Absent && halves.Client != InstalledState.HalfNone && UninstallPendingNow(entry))
            client = ClientHalfState.RemovalPending;

        var route = CurrentRoute;
        // REMOTETEST1: a server forced onto the remote route is never read off its folder.
        bool localConfigured = _server != null && _server.HasValidatedServer && !route.ForcedRemote;
        (ServerHalfState, string)? local = null;
        if (route.Kind == ServerRouteKind.Local || localConfigured)
        {
            var state = halves.Server == InstalledState.HalfLoaded ? ServerHalfState.Installed
                : halves.Server == InstalledState.HalfPending ? ServerHalfState.PendingRestart
                : ServerHalfState.Absent;
            local = (state, serverVersion);
        }

        var status = RemoteServer?.Status;
        bool rowsKnown = status != null && status.RowsIncluded && route.Kind == ServerRouteKind.Remote;
        var remoteRow = rowsKnown ? RemoteServer!.RowFor(entry, _installedQuery?.IdentityProbe) : null;
        (bool Known, string? Version) handshake = (false, null);
        try { handshake = ServerHandshakeProbe?.Invoke(entry) ?? (false, null); }
        catch { handshake = (false, null); }

        var server = SideStatusRules.ServerHalf(route, localConfigured, local, remoteRow, rowsKnown, handshake.Version, handshake.Known);
        // Mod compatibility C3: two different versions the connected server's judgment accepts are not a mismatch.
        bool accepted = false;
        if (clientVersion.Length > 0 && server.Version.Length > 0 && !string.Equals(clientVersion, server.Version, StringComparison.Ordinal))
        {
            try { accepted = ServerAcceptsProbe?.Invoke(entry, clientVersion, server.Version) ?? false; }
            catch { accepted = false; }
        }

        return new SideInputs(client, clientVersion, server.State, server.Version, server.Source, NeedsBoth(entry), route, accepted);
    }

    /// <summary>A declared both-sided row says which half is missing, pending or older.</summary>
    private InstallRowAction WithSides(CatalogEntry entry, InstallRowAction action)
    {
        if (action.State is InstallListingState.Tool or InstallListingState.Loader or InstallListingState.Installing
            or InstallListingState.Blocked) return action;
        if (!NeedsBoth(entry)) return action;
        if (IsOpen(entry) && _phase is InstallActionPhase.Downloading or InstallActionPhase.Executing) return action;
        // INSTALLFAIL1: the failure reason is the row's; the side sentence must not replace it.
        if (IsOpen(entry) && _phase == InstallActionPhase.Failed && action.Reason.Length > 0) return action;
        var decision = SideStatusRules.Decide(SideInputsOf(entry));
        if (!decision.Decided) return action;
        string sentence = decision.Sentence;
        if (decision.Action != SideAction.None)
            return new InstallRowAction(InstallListingState.Sides, SideStatusRules.Caption(decision.Action), CellTone.Warn, true,
                true, sentence, status: decision.Label);
        if (decision.Warn)
            return new InstallRowAction(InstallListingState.Sides, "", CellTone.Warn, false, false, sentence, status: decision.Label);
        return new InstallRowAction(action.State, action.Label, action.Tone, action.Enabled, action.Restart,
            action.Reason.Length > 0 ? action.Reason : sentence, action.Managed, action.PlainPluginId, action.ManagedWhy,
            decision.Label, action.Remote);
    }

    /// <summary>Readback <c>sides {client, server, serverSource, needsBoth, mismatch}</c>.</summary>
    internal Dictionary<string, object?> SidesToWidget(CatalogEntry entry)
    {
        var inputs = SideInputsOf(entry);
        var decision = SideStatusRules.Decide(inputs);
        return new Dictionary<string, object?>
        {
            ["client"] = SideStatusRules.Name(inputs.Client),
            ["server"] = SideStatusRules.Name(inputs.Server),
            ["serverSource"] = SideStatusRules.Name(inputs.Source),
            ["needsBoth"] = inputs.NeedsBoth,
            ["mismatch"] = decision.Mismatch,
            ["label"] = decision.Label,
            ["action"] = SideStatusRules.Caption(decision.Action),
        };
    }

    /// <summary>
    /// The uninstall confirmation of a declared both-sided mod names exactly which sides it touches,
    /// and a side it cannot reach as left in place.
    /// </summary>
    internal string UninstallSidesSentence(CatalogEntry entry)
    {
        var inputs = SideInputsOf(entry);
        var touched = new List<string>();
        var left = new List<string>();
        if (SideStatusRules.ClientPresent(inputs.Client)) touched.Add("this client");
        if (SideStatusRules.ServerPresent(inputs.Server))
        {
            bool reachable = inputs.Source == ServerHalfSource.Local
                || (inputs.Source == ServerHalfSource.Remote && inputs.Route.Kind == ServerRouteKind.Remote);
            if (reachable) touched.Add(inputs.Source == ServerHalfSource.Local ? "the server (local folder)" : "the server (remote)");
            else left.Add("the server (no route to it from here)");
        }

        string text = touched.Count > 0 ? "Removes it from " + string.Join(" and ", touched) + "." : "";
        if (left.Count > 0) text += (text.Length > 0 ? " " : "") + "Left in place: " + string.Join(", ", left) + ".";
        return text;
    }

    /// <summary>
    /// REMOTEINST1 (decided): a copy that lives only on the server names the route that put it
    /// there. The local folder route's rows say "(local folder)"; the remote route's say "(remote)".
    /// A copy on this client keeps today's words.
    /// </summary>
    private InstallRowAction WithLocalRouteWords(CatalogEntry entry, InstallRowAction action)
    {
        if (action.StatusText.Length > 0 || action.Remote || action.Managed.Length > 0 || action.Plain) return action;
        if (action.State is not (InstallListingState.Installed or InstallListingState.Pending)) return action;
        var halves = HalvesOf(entry);
        if (halves.Client != InstalledState.HalfNone) return action;
        if (halves.Server == InstalledState.HalfLoaded && action.State == InstallListingState.Installed)
            return action.WithStatus(ServerRouteCopy.InstalledLocal, "Installed in the local server folder; loaded at its last start.");
        if (halves.Server == InstalledState.HalfPending && action.State == InstallListingState.Pending)
            return action.WithStatus(ServerRouteCopy.PendingLocal, ServerRouteCopy.PendingLocalSentence);
        return action;
    }

    private InstallRowAction LocalActionFor(CatalogEntry entry)
    {
        if (IsTool(entry))
            return new InstallRowAction(InstallListingState.Tool, NotInstallableText(entry), CellTone.Disabled, false, false,
                NotInstallableReason(entry));
        // MODS2: the loader is already installed and is not ours to write. Disabled, tone ok, and
        // the drawer note says why - decided before any installed-state detection.
        if (IsLoader(entry))
            return new InstallRowAction(
                InstallListingState.Loader, InstallModsCopy.LoaderInstalled, CellTone.Ok, false, false,
                InstallModsCopy.LoaderNote);

        var installed = DetectInstalled(entry);
        bool restart = NeedsRestart(entry);

        // INSTALL2 item 2/4: an uninstalled-but-pending row. The file is still there and still
        // loaded, so this is decided before any installed-state reading: orange, and the only thing
        // it offers is the way back.
        if (UninstallPendingNow(entry))
            return new InstallRowAction(
                InstallListingState.PendingUninstall, InstallModsCopy.Reactivate, CellTone.Warn, true, true, "");

        // INSTORD1: a row this session has just removed says so; a loaded one keeps the assembly
        // until the process restarts and the note says that, not "not installed". INSTALL2 item 4
        // replaced that route with the marker for every removal that can carry one, so this is now
        // only reached when the catalog has no marker seam.
        if (_state.SessionRemoved.Contains(entry.Key))
            return new InstallRowAction(InstallListingState.Removed, InstallModsCopy.RemovedNeedsRestart, CellTone.Warn, false, true, "");

        bool ledgered = IsLedgered(entry);

        // HOT3 live (deploy #65): a hot unload with nothing of the package left on disk is simply not
        // installed, whatever the reloader's about-to-expire report and the Mods inventory snapshot
        // this pane was built with still say. Without this the rebuild right after the Remove read
        // that stale inventory row back as managed-elsewhere and the row said "Installed" for good.
        // A re-install of the same listing takes over: its own flow phase (Installing…, Cannot
        // install, the chooser) is answered below, and a successful write clears the set.
        if (HotUnloadedNow(entry) && installed.Files.Count == 0
            && !(IsOpen(entry) && _phase != InstallActionPhase.Idle))
        {
            return new InstallRowAction(
                InstallListingState.None,
                restart ? InstallModsCopy.InstallRestart : InstallModsCopy.Install,
                CellTone.Gold,
                true,
                restart,
                "");
        }

        // HOT3 defect 1: a reload copy this process loaded — or wrote into progenitor-reload/ itself,
        // seconds before the reloader's scan reports it — is installed, not "installed, restart to
        // load". This decision comes before the pending one, which is what the promoted-at-start
        // route still reads.
        // INSTALL2 item 2: an installed row is green and says Installed, whatever removing it would
        // cost — a hot copy carries the session's new tag. What the button opens is the y/n
        // confirmation, and the confirmation is where the cost is stated (Unloads now / Removed at the
        // next restart). The old Remove / Remove · restart to unload captions are gone: the row states
        // the fact, the prompt states the consequence.
        // INSTALL2 defect A: hot means "this process wrote and loaded it", never "the reloader reports
        // it loaded" — see IsHotLoaded. A promoted copy the reloader loaded at start falls through to
        // the ordinary installed branch below, which owes a restart and takes the marker route.
        // POLISH1 defect D: the caption and the tag part company. Hot-unloadable is what makes
        // `restart` false; the `new` tag is only for an install this session actually hot loaded.
        bool hotLoaded = IsHotLoaded(entry);
        if (hotLoaded && ledgered)
            return new InstallRowAction(
                InstallListingState.Installed,
                // INSTALLUX2 item 2: no `new` tag. A hot-loaded copy is simply Installed; what the
                // row offers beside that word is Uninstall.
                InstallModsCopy.Installed,
                CellTone.Ok,
                true,
                false,
                "");

        if (_state.SessionPending.Contains(entry.Key) || installed.State == InstalledKind.Pending)
        {
            // INSTALL2 item 2: yellow, and clickable when it is our own pending file — the click
            // opens Cancel installing <name>?, which deletes it.
            return new InstallRowAction(
                InstallListingState.Pending, InstallModsCopy.InstalledPending, CellTone.Pending, ledgered, true, "");
        }

        if (_state.SessionInstalled.Contains(entry.Key) || installed.State == InstalledKind.Loaded || installed.State == InstalledKind.ManagedElsewhere)
        {
            if (!ledgered || installed.State == InstalledKind.ManagedElsewhere)
            {
                // MANAGED1 (decided): no Uninstall for an install this manager did not make —
                // copied by hand, deployed by a script, or another mod manager's. The button slot
                // says so in a muted label and the drawer says how to remove it. Two cases qualify:
                // the inventory has it loaded with no Progenitor file on disk (managed-elsewhere), or
                // the ledger is readable and does not name a copy this session did not write. With no
                // ledger view at all (LedgerProbe null), or a copy this session just wrote, nothing is
                // known about who installed it and the row keeps its plain Installed word.
                bool elsewhere = installed.State == InstalledKind.ManagedElsewhere
                    || (LedgerProbe != null && !_state.SessionInstalled.Contains(entry.Key));
                if (elsewhere)
                {
                    // SETTINGS1 stage 3: where the preloader can act with high confidence it offers
                    // Uninstall (or Cancel while one waits); otherwise the label stays, with why.
                    var offer = PlainOfferFor(installed.PluginId);
                    if (offer is { RemovalPending: true })
                        return new InstallRowAction(
                            InstallListingState.PendingUninstall, InstallModsCopy.PlainCancel, CellTone.Warn, true, true,
                            "", plainPluginId: installed.PluginId);
                    if (offer is { CanUninstall: true })
                        return new InstallRowAction(
                            InstallListingState.Installed, InstallModsCopy.Installed, CellTone.Ok, true, true,
                            offer.LastFailure, plainPluginId: installed.PluginId);
                    string note = InstallModsCopy.ManagedElsewhereNoteFor(installed.Files);
                    if (offer != null && offer.Why.Length > 0) note = offer.Why + ". " + note;
                    return new InstallRowAction(
                        InstallListingState.Installed, InstallModsCopy.Installed, CellTone.Ok, false, false,
                        note, InstallModsCopy.ManagedBy(installed.Manager), managedWhy: offer?.Why ?? "");
                }
                return new InstallRowAction(InstallListingState.Installed, InstallModsCopy.Installed, CellTone.Ok, false, false, "");
            }

            // HOT3 defect 1: the hot case was answered above. A copy Bootstrap promoted and BepInEx
            // loaded at start cannot be dropped in process, so its confirmation owes a restart.
            return new InstallRowAction(
                InstallListingState.Installed, InstallModsCopy.Installed, CellTone.Ok, true, true, "");
        }

        bool update = installed.State == InstalledKind.UpdateAvailable;
        if (IsOpen(entry))
        {
            switch (_phase)
            {
                case InstallActionPhase.Downloading:
                case InstallActionPhase.Executing:
                    return new InstallRowAction(InstallListingState.Installing, InstallModsCopy.Installing, CellTone.Disabled, false, restart, "");
                case InstallActionPhase.Done:
                    if (_executeResult is { Ok: true })
                    {
                        return PlanNeedsRestart(_lastPlan)
                            ? new InstallRowAction(InstallListingState.Pending, InstallModsCopy.InstalledPending, CellTone.Pending, false, true, "")
                            : new InstallRowAction(InstallListingState.Installed, InstallModsCopy.Installed, CellTone.Ok, false, false, "");
                    }
                    break;
                case InstallActionPhase.Inspected:
                case InstallActionPhase.Reviewing:
                    // INSTALL2 item 3: while the confirmation is open the row keeps its Install
                    // caption — the prompt under it owns the decision, and a second press on the row
                    // is ignored rather than restarting the download (OnInstallClick).
                    // INSTALL2 defect B: a blocked row always carries its refusal. A plan whose own
                    // Reason is empty (and a set refusal, which is not on the plan at all) used to
                    // read `blocked` with reason '' — nothing on screen said why.
                    if (_lastPlan != null && !_lastPlan.CanExecuteToPending)
                        return new InstallRowAction(
                            InstallListingState.Blocked, InstallModsCopy.CannotInstall, CellTone.Disabled, false, restart,
                            BlockedReason(_lastPlan.Reason));
                    if (_lastPlan == null && _actionError != null)
                        return new InstallRowAction(
                            InstallListingState.Blocked, InstallModsCopy.CannotInstall, CellTone.Disabled, false, restart,
                            BlockedReason(null));
                    break;
            }
        }

        string reason = IsOpen(entry) && (_phase == InstallActionPhase.Failed || _phase == InstallActionPhase.Cancelled) ? _actionError ?? "" : "";
        if (update)
            return new InstallRowAction(InstallListingState.Update, InstallModsCopy.UpdateRestart, CellTone.Warn, true, true, reason);
        return new InstallRowAction(
            InstallListingState.None,
            restart ? InstallModsCopy.InstallRestart : InstallModsCopy.Install,
            CellTone.Gold,
            true,
            restart,
            reason);
    }

    /// <summary>
    /// INSTALL2 defect B: the sentence a <c>blocked</c> row shows. The plan's own reason when it has
    /// one, else the flow error (a refused dependency set's <c>Cannot install — &lt;dep&gt; refused: …</c>
    /// is only there), else the generic refusal — never empty.
    /// </summary>
    private string BlockedReason(string? planReason)
    {
        if (!string.IsNullOrWhiteSpace(planReason)) return planReason!;
        if (!string.IsNullOrWhiteSpace(_actionError)) return _actionError!;
        return InstallReasons.PlanBlocked;
    }

    /// <summary>
    /// INSTORD1: does Progenitor's own install ledger name this listing on either target? Only a
    /// ledgered install offers Remove; anything else is another tool's file and is never touched.
    /// Null probe (the default) means "no ledger view", and the row keeps its Installed caption.
    /// </summary>
    public Func<CatalogEntry, bool>? LedgerProbe { get; set; }

    /// <summary>
    /// INSTORD2: the identity a prior inspection of this listing recorded
    /// (<c>progenitor-cache/identity</c>). Defaults to the catalog service's own cache, so the
    /// drawer and the readback can name the plugin at rest without a download. A test may substitute one.
    /// </summary>
    internal Func<CatalogEntry, OrdinaryIdentityRecord?>? IdentityProbe { get; set; }

    internal OrdinaryIdentityRecord? RestingIdentity(CatalogEntry entry)
    {
        var probe = IdentityProbe;
        try
        {
            if (probe != null) return probe(entry);
            return _catalog is InstallCatalogService service ? service.TryRestingIdentity(entry) : null;
        }
        catch { return null; }
    }

    /// <summary>The <c>Plugin</c> drawer fact: <c>&lt;guid&gt; 1.2.0 · loads in the server</c>. Empty when nothing was inspected.</summary>
    internal string IdentityFact(CatalogEntry entry)
    {
        var identity = RestingIdentity(entry);
        if (identity == null || identity.PluginGuid.Length == 0) return "";
        var sb = new System.Text.StringBuilder(identity.PluginGuid);
        if (identity.PluginVersion.Length > 0) sb.Append(' ').Append(identity.PluginVersion);
        if (identity.LoadsIn.Length > 0) sb.Append(" · ").Append(InstallModsCopy.LoadsInPrefix).Append(identity.LoadsIn);
        return sb.ToString();
    }

    /// <summary>
    /// INSTORD1: the Remove executor. Defaults to the catalog service's own ledger-scoped removal;
    /// a test may substitute one. A catalog without it (the empty catalog) cannot remove.
    /// </summary>
    public Func<string, ServerInstallResult>? Remover { get; set; }

    /// <summary>
    /// INSTALL2 item 6: the same removal, forced past the dependents warning. Defaults to the catalog
    /// service's own; falls back to <see cref="Remover"/> for a catalog that has only the plain one.
    /// </summary>
    internal Func<string, bool, ServerInstallResult>? ForcingRemover { get; set; }

    /// <summary>
    /// INSTALL2 item 4: ask for one listing's loaded copies to be removed at the next start
    /// (<c>true</c>) — the marker and the ledger flag — or drop that request (<c>false</c>, the
    /// Reactivate). Defaults to the catalog service's own; a catalog without it cannot mark, and the
    /// Uninstall then falls back to <see cref="Remover"/>.
    /// </summary>
    internal Func<string, bool, bool, ServerInstallResult>? UninstallMarker { get; set; }

    /// <summary>INSTALL2 item 4: is this listing's removal already pending on disk? Defaults to the catalog service's ledger read.</summary>
    internal Func<CatalogEntry, bool>? UninstallPendingProbe { get; set; }

    /// <summary>
    /// INSTALL2 item 6: the ledgered packages that need this one, asked <em>before</em> the removal so
    /// the confirmation can warn and offer <see cref="InstallModsCopy.PromptYesUninstallAnyway"/>.
    /// Defaults to the catalog service's own <c>DependentsOf</c>.
    /// </summary>
    internal Func<string, IReadOnlyList<string>>? DependentsProbe { get; set; }

    /// <summary>
    /// DEPS1/INSTALL2 item 3: plan one listing together with everything it needs. Defaults to the
    /// catalog service's <c>PlanWithDependenciesAsync</c>; a catalog without it falls back to the
    /// plain plan, whose confirmation body names the dependencies without their versions.
    /// </summary>
    internal Func<string, InstallTarget, CancellationToken, Task<InstallPlanSet>>? SetPlanner { get; set; }

    /// <summary>DEPS1/INSTALL2 item 3: write one planned set, dependencies first. Defaults to the catalog service's own.</summary>
    internal Func<InstallPlanSet, ExecuteSetResult>? SetExecutor { get; set; }

    private InstallCatalogService? Service => _catalog as InstallCatalogService;

    private Func<string, InstallTarget, CancellationToken, Task<InstallPlanSet>>? SetPlannerOrDefault
    {
        get
        {
            if (SetPlanner != null) return SetPlanner;
            var service = Service;
            return service == null ? null : service.PlanWithDependenciesAsync;
        }
    }

    private Func<InstallPlanSet, ExecuteSetResult>? SetExecutorOrDefault =>
        SetExecutor ?? (Service is { } service ? service.ExecuteWithDependencies : null);

    private ServerInstallResult Remove(string key, bool force) => Remove(key, force, InstallTarget.Both);

    /// <summary>
    /// POLISH3 item A: the removal narrowed to one side's roots. The injected seams
    /// (<see cref="ForcingRemover"/>, <see cref="Remover"/>) are whole-catalog fakes and are asked
    /// as before — a test that substitutes one is standing in for the roots it cares about — so
    /// scoping only changes what the real catalog service touches.
    /// </summary>
    private ServerInstallResult Remove(string key, bool force, InstallTarget scope)
    {
        if (ForcingRemover is { } injected) return injected(key, force);
        if (Service is { } service) return service.RemovePackage(key, force, scope);
        var plain = Remover;
        if (plain != null) return plain(key);
        return ServerInstallResult.Refuse(InstallModsCopy.RemoveFailed);
    }

    /// <summary>
    /// POLISH3 item A: mark or unmark one listing on one side's roots. Falls back to the unscoped
    /// <see cref="Marker"/> seam, which is what the fake catalogs supply.
    /// </summary>
    private ServerInstallResult Mark(string key, bool mark, bool force, InstallTarget scope)
    {
        if (UninstallMarker is { } injected) return injected(key, mark, force);
        var service = Service;
        if (service == null) return ServerInstallResult.Refuse(InstallModsCopy.RemoveFailed);
        return mark ? service.MarkUninstall(key, force, scope) : service.CancelUninstall(key, scope);
    }

    /// <summary>INSTALL2 item 4: mark or unmark one listing; null when this catalog cannot.</summary>
    private Func<string, bool, bool, ServerInstallResult>? Marker
    {
        get
        {
            if (UninstallMarker != null) return UninstallMarker;
            var service = Service;
            if (service == null) return null;
            return (key, mark, force) => mark ? service.MarkUninstall(key, force) : service.CancelUninstall(key);
        }
    }

    private IReadOnlyList<string> DependentsOf(CatalogEntry entry)
    {
        var probe = DependentsProbe ?? (Service is { } service ? service.DependentsOf : null);
        if (probe == null) return Array.Empty<string>();
        try { return probe(entry.Key) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// INSTALL2 item 4: is this listing marked for removal at the next start? This session's own mark
    /// first (a pane rebuilt in the same breath knows it), then the ledger flag on disk.
    /// </summary>
    internal bool UninstallPendingNow(CatalogEntry entry)
    {
        if (entry == null) return false;
        if (_state.SessionUninstallPending.Contains(entry.Key)) return true;
        var probe = UninstallPendingProbe;
        if (probe != null)
        {
            try { return probe(entry); }
            catch { return false; }
        }

        // One ledger read per root per build, not one per listing: ActionFor asks this for every row
        // and the bar's counts ask again, and the ledger is a file.
        var marked = MarkedPackages();
        if (marked.Count == 0) return false;
        if (marked.Contains(entry.Key)) return true;
        if (!string.IsNullOrWhiteSpace(entry.Manifest.Id) && marked.Contains(entry.Manifest.Id!)) return true;
        // POLISH3 item A: a mark written against a row the first-class restart route ledgered under
        // the plugin id is still this listing's mark. The removal now reaches those rows, so the
        // read-back has to recognize them too, or the row would go orange-less after a Yes.
        foreach (string package in marked)
        {
            if (InstalledState.PackageMatches(entry, package)) return true;
        }

        return false;
    }

    private HashSet<string>? _markedPackages;

    private HashSet<string> MarkedPackages()
    {
        if (_markedPackages != null) return _markedPackages;
        var service = Service;
        if (service == null) return _markedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { _markedPackages = new HashSet<string>(service.UninstallPendingPackages(), StringComparer.OrdinalIgnoreCase); }
        catch { _markedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        return _markedPackages;
    }

    /// <summary>Forget the per-build pending-removal read: a mark, a cancel, a removal or a rebuild.</summary>
    private void ForgetMarkedPackages()
    {
        _markedPackages = null;
        _markedClient = null;
        _markedServer = null;
        _sideMemo?.Clear();
        ForgetHotPackages();
    }

    private bool IsLedgered(CatalogEntry entry)
    {
        var probe = LedgerProbe;
        if (probe == null) return false;
        if (_ledgerMemo != null && entry != null && _ledgerMemo.TryGetValue(entry, out bool known)) return known;
        bool ledgered;
        try { ledgered = probe(entry!); }
        catch { ledgered = false; }
        if (_ledgerMemo != null && entry != null) _ledgerMemo[entry] = ledgered;
        return ledgered;
    }

    /// <summary>
    /// INSTORD1 player action: Remove. Deletes exactly the pending and promoted files Progenitor's
    /// ledger names for this package (its companions included), drops those rows, and leaves the
    /// row <c>none</c> when nothing was loaded or <c>removed</c> when the process still holds the
    /// assembly. Never touches a file outside the ledger.
    /// </summary>
    public bool OnRemoveClick(string key) => OnRemoveClick(key, force: false);

    /// <inheritdoc cref="OnRemoveClick(string)"/>
    /// <param name="force">
    /// INSTALL2 item 6: delete it even though other ledgered installs list it as a dependency. Only
    /// the confirmation sets this, and only after its body has named those mods.
    /// </param>
    public bool OnRemoveClick(string key, bool force) => OnRemoveClick(key, force, InstallTarget.Both);

    /// <inheritdoc cref="OnRemoveClick(string, bool)"/>
    /// <param name="scope">
    /// POLISH3 item A: which side's roots this call takes the files from.
    /// <see cref="InstallTarget.Client"/> when the local server's half was already handled on its own
    /// (canceled or marked) and this call owns only the client's; <see cref="InstallTarget.Both"/>
    /// otherwise, which is every earlier caller's behavior.
    /// </param>
    public bool OnRemoveClick(string key, bool force, InstallTarget scope)
    {
        if (string.IsNullOrEmpty(key) || !_catalog.TryGetEntry(key, out var entry) || entry == null) return false;
        if (_phase == InstallActionPhase.Downloading || _phase == InstallActionPhase.Executing) return false;

        if (Remover == null && ForcingRemover == null && Service == null)
        {
            _actionError = InstallModsCopy.RemoveFailed;
            RequestDetailRebuild();
            return false;
        }

        // HOTDEMO1: "still loaded until this process restarts" is about THIS process's copy. The
        // listing-wide state read Loaded from the local server's promoted copy, so removing a client
        // copy that was only pending left the Client cell on `Removal pending game restart`.
        var installed = DetectClient(entry);
        bool wasLoaded = installed.State == InstalledKind.Loaded || _state.SessionInstalled.Contains(entry.Key);

        ServerInstallResult result;
        try { result = Remove(entry.Key, force, scope); }
        catch (Exception e) { result = ServerInstallResult.Refuse(e.Message); }

        _lastRemoveResult = result;
        _state.SessionUninstallPending.Remove(entry.Key);
        ForgetMarkedPackages();
        ForgetLoadedPlugins();
        if (!result.Ok || result.NoOp)
        {
            _actionError = result.Ok ? InstallModsCopy.RemovedPending : (result.Message ?? InstallModsCopy.RemoveFailed);
            _state.SessionPending.Remove(entry.Key);
            _state.SessionInstalled.Remove(entry.Key);
            RequestDetailRebuild();
            return result.Ok;
        }

        _state.SessionPending.Remove(entry.Key);
        _state.SessionInstalled.Remove(entry.Key);
        _state.SessionHotLoaded.Remove(entry.Key);

        // HOT3 defect 2: a hot unload leaves nothing loaded — the reloader drops the assembly with
        // the file — so the row goes back to none/Install at once. Only a promoted copy this process
        // cannot unload keeps "Removed — still loaded until this process restarts".
        if (wasLoaded && !result.HotUnloaded) _state.SessionRemoved.Add(entry.Key);
        if (result.HotUnloaded)
        {
            _state.SessionHotUnloaded.Add(entry.Key);
            NoteInventoryChanged(InventoryReasonHotUnloaded);
            // One more look after the reloader's next scan, in case a probe or snapshot read at the
            // rebuild below was still the pre-unload one.
            ScheduleHotUnloadRecheck();
        }
        _actionError = null;
        _phase = InstallActionPhase.Idle;
        _lastPlan = null;
        _executeResult = null;
        RequestDetailRebuild();
        return true;
    }

    /// <summary>The last Remove result, for the drawer note and readback.</summary>
    public ServerInstallResult? LastRemoveResult => _lastRemoveResult;

    /// <summary>Readback <c>listing {id, state, restart, side, loadsIn, rating, …}</c> of a row.</summary>
    public Dictionary<string, object?> ListingToWidget(CatalogEntry entry)
    {
        var action = ActionFor(entry);
        var side = SideOf(entry);
        var widget = new Dictionary<string, object?>
        {
            ["id"] = entry.Key,
            ["state"] = StateName(action.State),
            ["sides"] = SidesToWidget(entry),
            // INSTALLSIDES1: the two cells the row draws and the shortcut under the name.
            ["client"] = SideCells.ToWidget(SideCellsFor(entry).Client),
            ["server"] = SideCells.ToWidget(SideCellsFor(entry).Server),
            ["shortcut"] = SideCells.Name(SideCellsFor(entry).Shortcut),
            ["drawerOpen"] = string.Equals(_state.DrawerKey, entry.Key, StringComparison.OrdinalIgnoreCase),
            ["nsfw"] = entry.HasNsfwContent,
            ["restart"] = action.Restart,
            ["side"] = PackageSideDerivation.SideName(side),
            ["loadsIn"] = LoadsIn(side, action.State is InstallListingState.Tool or InstallListingState.Loader),
            ["label"] = action.Label,
            ["reason"] = action.Reason,
            // HOT3 defect 4: whether this row's Remove is a hot unload — the readback's own name for
            // the difference between an uninstall that unloads now and one that owes a restart.
            ["hotLoaded"] = IsHotLoaded(entry),
            // INSTALL2 item 4: this row's files go at the next restart (the ledger flag / marker).
            ["uninstallPending"] = UninstallPendingNow(entry),
            // INSTALL2 item 2 / INSTALLUX2 item 2: the "new" tag is gone from every caption, so this
            // key survives only to keep the readback's shape and is always false.
            ["newTag"] = false,
            // INSTALLUX2 item 2: the buttons this row actually draws in the Install column.
            ["actions"] = RowActionNames(action),
            // MANAGED1: the label of an install this manager did not make ("Managed elsewhere" /
            // "Managed by <tool>"); empty on every other row. INSTALLCOLS1 draws it as the Status
            // column's text rather than in the button's place.
            ["managed"] = RowManagedText(action),
            // INSTALLCOLS1: the Status column's text (one line, before it is broken for the cell)
            // and the button beside it ("" when the row has none).
            ["status"] = RowStatusText(action).Replace("\n", " · "),
            ["button"] = RowButtonText(action),
            // INSTALLCOLS1: the Installed column — its short date, the UTC stamp and which
            // evidence said so (ledger / fileCreated / fileWritten / none).
            ["installed"] = InstalledToWidget(entry),
            ["released"] = ReleasedShortText(entry),
            ["releasedDate"] = ReleasedDate(entry, out _),
            ["downloads"] = entry.Downloads,
            // INSTALLCOLS1: the warn-tone note under the name, or "" when the listing carries the
            // current update (or the current update is unknown).
            ["updateNote"] = UpdateNoteText(entry, CurrentUpdateTag),
            // SETTINGS1 stage 3: the preloader can uninstall this mod another tool installed.
            ["plainUninstall"] = action.Plain,
            ["currentUpdate"] = CurrentUpdateTag != null && CatalogUpdateTags.IsTagged(entry, CurrentUpdateTag),
            ["searchScope"] = SearchScope(entry),
            ["icon"] = IconToWidget(IconUrlFor(entry)),
            // INSTORD2: what the install ledgers say about this listing, and the files they name
            // that are on disk now — visible at rest, with no inspection this session.
            ["ledger"] = LedgerToWidget(entry),
            ["files"] = FilesToWidget(entry),
            // HOT4: the two halves of a Both install, which can disagree — the client loaded now,
            // the local server pending for its next start.
            ["halves"] = HalvesToWidget(entry),
            ["serverHalfNote"] = ServerHalfNote(entry),
            // Job LIST4 item 1: the action object belongs to the row that carries the one button.
            // Only the opened listing has a flow (phase, progress, plan), so a closed row's is
            // null rather than the opened row's plan restated under someone else's id. The
            // drawer head keeps the same object for one release; see TryDescribeSidebarRow.
            ["action"] = IsOpen(entry) ? ActionToWidget(entry) : null,
        };
        // INSTALLSIMPLE1: the Simple row (status, its one button, the sides it acts on); absent in Advanced.
        if (IsSimple) widget["simple"] = SimpleToWidget(entry);
        return widget;
    }

    /// <summary>
    /// HOT4: what each process holds of this listing — <c>loaded</c> | <c>pending</c> | <c>none</c>
    /// per half. The client half counts as loaded on the same evidence the row's caption uses (the
    /// reloader's report or this session's own hot write), never on the file alone.
    /// </summary>
    internal (string Client, string Server) HalvesOf(CatalogEntry entry) =>
        InstalledState.HalvesOf(entry, _installedQuery, IsHotLoaded(entry));

    /// <summary>
    /// POLISH2 item 3: which processes hold a copy of this listing right now —
    /// <see cref="InstallModsCopy.SidesClient"/> | <see cref="InstallModsCopy.SidesServer"/> |
    /// <see cref="InstallModsCopy.SidesBoth"/>, and <see cref="InstallModsCopy.SidesNone"/> when
    /// nothing the ledger names is on disk. A half counts when it is anything but
    /// <see cref="InstalledState.HalfNone"/>: a pending half still has a file the uninstall removes.
    /// This is the same reading <see cref="PendingSides"/> does for the bar, for one listing, and it
    /// is what the uninstall prompt names as the restart it owes.
    /// </summary>
    internal string InstalledSidesOf(CatalogEntry entry)
    {
        if (entry == null) return InstallModsCopy.SidesNone;
        var halves = HalvesOf(entry);
        bool client = halves.Client != InstalledState.HalfNone;
        bool server = halves.Server != InstalledState.HalfNone;
        if (client && server) return InstallModsCopy.SidesBoth;
        if (server) return InstallModsCopy.SidesServer;
        if (client) return InstallModsCopy.SidesClient;
        return InstallModsCopy.SidesNone;
    }

    /// <summary>POLISH3 item A readback <c>removed.halves {client, server}</c>.</summary>
    internal Dictionary<string, object?> UninstallHalvesToWidget() =>
        new()
        {
            ["client"] = _lastUninstallHalves.Client,
            ["server"] = _lastUninstallHalves.Server,
        };

    /// <summary>POLISH3 item A: what the last Uninstall did to each half, for the tests and the pane.</summary>
    internal (string Client, string Server) LastUninstallHalves => _lastUninstallHalves;

    /// <summary>HOT4 readback <c>listing.halves {client, server}</c>.</summary>
    internal Dictionary<string, object?> HalvesToWidget(CatalogEntry entry)
    {
        var halves = HalvesOf(entry);
        return new Dictionary<string, object?>
        {
            ["client"] = halves.Client,
            ["server"] = halves.Server,
        };
    }

    /// <summary>
    /// HOT4: the drawer's second line for a mixed install — the client half is live and the local
    /// server's is written for its next start. Empty for every other state, including a both-pending
    /// install, whose single "Restart required" head note is already the whole truth.
    /// </summary>
    internal string ServerHalfNote(CatalogEntry entry)
    {
        if (entry == null) return "";
        var halves = HalvesOf(entry);
        return halves.Client == InstalledState.HalfLoaded && halves.Server == InstalledState.HalfPending
            ? InstallModsCopy.ServerHalfPending
            : "";
    }

    /// <summary>INSTORD2 readback <c>listing.ledger {client, server, version}</c>.</summary>
    internal Dictionary<string, object?> LedgerToWidget(CatalogEntry entry)
    {
        var view = InstalledState.LedgerViewOf(entry, _installedQuery);
        return new Dictionary<string, object?>
        {
            ["client"] = view.Client,
            ["server"] = view.Server,
            ["version"] = view.Version,
        };
    }

    /// <summary>INSTORD2 readback <c>listing.files [{path, kind, root}]</c> — <c>pending</c>|<c>reload</c>, <c>client</c>|<c>server</c>.</summary>
    internal List<Dictionary<string, object?>> FilesToWidget(CatalogEntry entry)
    {
        var files = InstalledState.LedgerFilesFor(entry, _installedQuery);
        var list = new List<Dictionary<string, object?>>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["path"] = files[i].Path,
                ["kind"] = files[i].Kind,
                ["root"] = files[i].Root,
            });
        }

        return list;
    }

    /// <summary>
    /// ICON2 readback <c>listing.icon {state, cached, error}</c>: <c>none</c> when the listing has
    /// no icon URL, else the cache's state for it (<c>missing</c> until it is asked for);
    /// <c>cached</c> is true when the bytes came from the disk lane.
    /// </summary>
    public Dictionary<string, object?> IconToWidget(string? url)
    {
        var icons = _state.Icons;
        string state = "none";
        if (!string.IsNullOrEmpty(url))
            state = icons == null ? "missing" : IconStateName(icons.StateOf(url));
        return new Dictionary<string, object?>
        {
            ["state"] = state,
            ["cached"] = icons != null && icons.WasDiskHit(url),
            ["error"] = icons?.ErrorOf(url),
        };
    }

    private static string IconStateName(IconState state) => state switch
    {
        IconState.Queued => "queued",
        IconState.Loading => "loading",
        IconState.Ready => "ready",
        IconState.Failed => "failed",
        _ => "missing",
    };

    /// <summary>
    /// ICON2 readback <c>toolbar.icons {ready, queued, loading, failed, inFlightUrl, lastError,
    /// fetches, stalledSeconds}</c>: the counts are over the URLs this build asked for, so a stall
    /// is visible as <c>queued</c> standing still with a non-zero <c>stalledSeconds</c>.
    /// </summary>
    public Dictionary<string, object?> IconsToWidget()
    {
        var icons = _state.Icons;
        int ready = 0, queued = 0, loading = 0, failed = 0;
        if (icons != null)
        {
            foreach (var url in _iconRequested.Keys)
            {
                switch (icons.StateOf(url))
                {
                    case IconState.Ready: ready++; break;
                    case IconState.Queued: queued++; break;
                    case IconState.Loading: loading++; break;
                    case IconState.Failed: failed++; break;
                }
            }
        }
        return new Dictionary<string, object?>
        {
            ["ready"] = ready,
            ["queued"] = queued,
            ["loading"] = loading,
            ["failed"] = failed,
            ["inFlightUrl"] = icons?.InFlightUrl,
            ["lastError"] = icons?.LastError,
            ["fetches"] = icons?.FetchCount ?? 0,
            ["stalledSeconds"] = icons == null ? 0d : Math.Round(icons.StalledSeconds, 1),
        };
    }

    /// <summary>
    /// <c>action</c> readback of the opened listing: label/state plus the flow <c>phase</c>,
    /// the catalog's live <c>progress</c>, the last <c>error</c>, the current
    /// <c>plan {decision, target, reason, hash, approved}</c> and, once written,
    /// <c>result {path, sha256, alreadyPresent, hotLoaded, serverPendingPath, rechecked}</c> and, after a
    /// Remove, <c>removed {ok, noOp, hotUnloaded, files, skipped}</c> (HOT2).
    /// </summary>
    /// <summary>REMOTEINST1 readback <c>action.route</c>: the route, why it is blocked, and the server copy.</summary>
    internal Dictionary<string, object?> RouteToWidget(CatalogEntry entry)
    {
        var route = CurrentRoute;
        var resolved = ResolvedTargetFor(entry);
        var row = RemoteRowFor(entry);
        var latest = RemoteServer?.LatestFor(entry.Key);
        return new Dictionary<string, object?>
        {
            ["kind"] = route.KindName,
            ["effective"] = route.EffectiveName, // REMOTETEST1
            ["block"] = route.BlockName,
            ["reason"] = route.Reason,
            ["toConnectedServer"] = resolved.ToConnectedServer,
            ["localInstall"] = resolved.LocalInstall,
            ["serverRow"] = row == null ? null : RemoteInstallBridgeOps.RowToWidget(row),
            ["latest"] = latest == null ? null : RemoteInstallBridgeOps.ProgressToWidget(latest),
        };
    }

    internal Dictionary<string, object?> ActionToWidget(CatalogEntry entry)
    {
        var action = ActionFor(entry);
        var plan = _lastPlan;
        var identity = RestingIdentity(entry);
        var row = new Dictionary<string, object?>
        {
            // INSTORD2: the resting identity, so pluginGuid/loadsIn are answerable while plan is null.
            ["identity"] = identity == null
                ? null
                : new Dictionary<string, object?>
                {
                    ["pluginGuid"] = identity.PluginGuid,
                    ["pluginVersion"] = identity.PluginVersion,
                    ["loadsIn"] = identity.LoadsIn.Length == 0 ? null : identity.LoadsIn,
                    ["version"] = identity.Version,
                },
            ["label"] = action.Label,
            ["state"] = StateName(action.State),
            ["reason"] = action.Reason,
            ["enabled"] = action.Enabled,
            ["restart"] = action.Restart,
            // REMOTEINST1 readback: which route the row acts through and the words its Status shows.
            ["statusText"] = RowStatusText(action),
            ["remote"] = action.Remote,
            ["route"] = RouteToWidget(entry),
            ["phase"] = PhaseName(_phase),
            ["progress"] = ProgressText(),
            ["error"] = _actionError,
            ["chooser"] = false,
            // INSTALL2 item 3: the y/n box open under this row, in the words it is showing.
            ["prompt"] = PromptOpenFor(entry)
                ? new Dictionary<string, object?>
                {
                    ["kind"] = PromptName(_state.Prompt),
                    // INSTALLUX2 item 1: the box is a modal dialog now, not an in-row expansion.
                    ["presentation"] = "dialog",
                    ["heading"] = PromptHeadingText(entry, _state.Prompt),
                    ["body"] = PromptBodyText(entry, _state.Prompt),
                    // CONFIRMTARGET2: what the dialog draws for install / update / uninstall.
                    ["facts"] = DialogFacts.Describe(PromptFacts(entry, _state.Prompt)),
                    ["note"] = PromptNote(entry, _state.Prompt),
                    ["update"] = IsUpdatePrompt,
                    ["yes"] = PromptRefusal != null ? "" : PromptYesText(_state.Prompt),
                    ["no"] = PromptRefusal != null ? InstallModsCopy.PromptClose : InstallModsCopy.PromptNo,
                    ["chooser"] = false,
                    ["target"] = TargetName(ResolvedTargetFor(entry).Target),
                    ["ready"] = _promptPlanReady,
                    ["refusal"] = PromptRefusal,
                    ["dependents"] = _promptDependents,
                }
                : null,
            ["plan"] = plan == null
                ? null
                : new Dictionary<string, object?>
                {
                    ["decision"] = DecisionName(plan.Decision),
                    ["target"] = TargetName(plan.Target),
                    ["reason"] = plan.Reason,
                    ["hash"] = plan.PlanHash,
                    // INSTALLUX1 item 1: has the download + inspection behind the open confirmation
                    // finished? False while the box shows "Checking the package...".
                    ["ready"] = _promptPlanReady,
                    ["approved"] = _approvedHash != null && string.Equals(_approvedHash, plan.PlanHash, StringComparison.OrdinalIgnoreCase),
                    // INSTORD1: the ordinary-package facts, read from the bytes.
                    // INSTORD2: a plan built without ordinary layout facts falls back to the
                    // identity a prior inspection cached, never to the package name.
                    ["pluginGuid"] = string.IsNullOrEmpty(plan.OrdinaryPluginGuid)
                        ? (identity != null && identity.PluginGuid.Length > 0 ? identity.PluginGuid : plan.OrdinaryPluginGuid)
                        : plan.OrdinaryPluginGuid,
                    ["loadsIn"] = string.Equals(plan.OrdinaryLoadsIn, "unknown", StringComparison.Ordinal)
                        && identity != null && identity.LoadsIn.Length > 0
                        ? identity.LoadsIn
                        : plan.OrdinaryLoadsIn,
                    ["companions"] = plan.CompanionFileNames,
                    // DEPS1: with a planned closure the rows carry each dependency's version, its own
                    // decision and the target it was narrowed to; without one, the plan's own
                    // satisfied-by list is all there is.
                    ["dependencies"] = _planSet != null ? _planSet.DependencyRows() : DependenciesToWidget(plan),
                    ["closureCount"] = _planSet?.ClosureCount ?? 0,
                    ["ledger"] = new Dictionary<string, object?>
                    {
                        ["client"] = plan.LedgerClient,
                        ["server"] = plan.LedgerServer,
                    },
                    // HOT4: the per-process decisions. One row for a single target; two for Both,
                    // which may disagree (client ApplyNow with an executor, server restart).
                    ["halves"] = HalvesToWidget(plan),
                    ["mixed"] = plan.IsMixed,
                },
            // INSTALLUX1 item 2: the target an install of THIS listing resolves to, from the
            // [Install] DefaultTarget setting and the listing's own side. The per-row chooser is
            // gone (action.chooser is always false), so this is the whole answer to "where would
            // Install put it?" — and it is answerable before any click.
            ["target"] = TargetName(ResolvedTargetFor(entry).Target),
            ["targetSetting"] = DefaultInstallTarget.ValueOf(DefaultTargetSetting),
            ["targetFallback"] = ResolvedTargetFor(entry).FellBackToClient,
        };
        if (_executeResult is { Ok: true } done)
        {
            row["result"] = new Dictionary<string, object?>
            {
                ["path"] = done.PendingPath,
                ["sha256"] = _resultSha256,
                ["alreadyPresent"] = done.AlreadyPresent,
                // HOT2: the three facts that tell an apply-now install from a restart one.
                // hotLoaded — the path is a progenitor-reload/ file in THIS process, so no restart.
                // serverPendingPath — the local-server half of a Both target, which is a restart install.
                // rechecked — the Bootstrap loader state was captured again immediately before the
                // write and still matched the approval (handoff item 4). False on the restart route.
                ["hotLoaded"] = done.HotLoaded,
                ["serverPendingPath"] = done.ServerPendingPath,
                ["rechecked"] = done.Rechecked,
                // DEPS1: every package this install wrote, dependencies first, the root last. Null
                // when the install went through the single-plan route.
                ["members"] = _executeSet?.MemberRows(),
            };
        }
        else if (_executeSet is { } set)
        {
            // A set that stopped part-way has no root result, but the members before it are real
            // installs and the readback has to name them.
            row["result"] = new Dictionary<string, object?>
            {
                ["path"] = null,
                ["error"] = set.Error,
                ["failedPackage"] = set.FailedPackage,
                ["members"] = set.MemberRows(),
            };
        }
        if (_lastRemoveResult is { } removed)
        {
            // HOT2: Remove of a hot-loaded package took the reload file back, so the reloader unloads
            // the plugin within a second and nothing stays live until a restart.
            row["removed"] = new Dictionary<string, object?>
            {
                ["ok"] = removed.Ok,
                ["noOp"] = removed.NoOp,
                ["hotUnloaded"] = removed.HotUnloaded,
                ["files"] = removed.Removed,
                ["skipped"] = removed.Skipped,
                // DEPS1: the ledgered packages that list this one as a dependency. Non-empty on a
                // removal refused for that reason; INSTALL2 renders the warning from it.
                ["dependents"] = removed.Dependents,
                // INSTALL2 item 4: the removal was recorded as a request — a marker beside each file
                // and the ledger row flagged — rather than carried out now.
                ["pending"] = UninstallPendingNow(entry),
                // POLISH3 item A: what the one Yes did to each half — the client's and the local
                // server's can differ (unloaded / canceled), and the live defect was a Yes that
                // silently did nothing to one of them.
                ["halves"] = UninstallHalvesToWidget(),
            };
        }

        return row;
    }

    /// <summary>HOT4 readback <c>plan.halves [{target, decision, executorAvailable, reason}]</c>.</summary>
    internal static List<Dictionary<string, object?>> HalvesToWidget(InstallPlan plan)
    {
        var list = new List<Dictionary<string, object?>>(plan.Halves.Count);
        for (int i = 0; i < plan.Halves.Count; i++)
        {
            var half = plan.Halves[i];
            list.Add(new Dictionary<string, object?>
            {
                ["target"] = TargetName(half.Target),
                ["decision"] = DecisionName(half.Decision),
                ["executorAvailable"] = half.ExecutorAvailable,
                ["reason"] = half.Reason,
            });
        }

        return list;
    }

    /// <summary>INSTORD1 readback <c>plan.dependencies [{package, satisfiedBy}]</c>.</summary>
    private static List<Dictionary<string, object?>> DependenciesToWidget(InstallPlan plan)
    {
        var list = new List<Dictionary<string, object?>>(plan.OrdinaryDependencies.Count);
        for (int i = 0; i < plan.OrdinaryDependencies.Count; i++)
        {
            var dependency = plan.OrdinaryDependencies[i];
            list.Add(new Dictionary<string, object?>
            {
                ["package"] = dependency.Package,
                ["satisfiedBy"] = dependency.SatisfiedBy,
            });
        }

        return list;
    }

    private string? ProgressText()
    {
        if (_phase != InstallActionPhase.Downloading) return null;
        InstallProgress status;
        try { status = _catalog.Status; }
        catch { return null; }
        if (status == null) return null;
        if (!string.IsNullOrEmpty(status.EntryKey)
            && !string.Equals(status.EntryKey, _downloadKey, StringComparison.OrdinalIgnoreCase))
            return null;
        string text = status.Message ?? "";
        if (status.BytesTotal is long total && total > 0)
            text += " (" + status.BytesReceived.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + " bytes)";
        else if (status.BytesReceived > 0)
            text += " (" + status.BytesReceived.ToString(CultureInfo.InvariantCulture) + " bytes)";
        return text;
    }

    public static string DecisionName(InstallDecision decision) => decision switch
    {
        InstallDecision.ApplyNow => "ApplyNow",
        InstallDecision.RestartClientRequired => "RestartClientRequired",
        InstallDecision.RestartServerRequired => "RestartServerRequired",
        InstallDecision.RestartBothRequired => "RestartBothRequired",
        _ => "UnknownBlocked",
    };

    /// <summary>Listings that load at the next restart: this session's writes plus pending files on disk.</summary>
    public int PendingCount()
    {
        int n = 0;
        var entries = AllEntries();
        for (int i = 0; i < entries.Count; i++)
        {
            if (IsNotInstallable(entries[i])) continue;
            // INSTALL2 item 4: a row that is going away at the next restart is counted by
            // PendingUninstallCount, never twice.
            if (UninstallPendingNow(entries[i])) continue;
            if (_state.SessionPending.Contains(entries[i].Key)) { n++; continue; }
            // A copy waiting on the connected server's restart (remote route) counts as well.
            if (RemoteServer != null && ActionFor(entries[i]) is { Remote: true, State: InstallListingState.Pending }) { n++; continue; }
            var installed = DetectInstalled(entries[i]);
            // INSTALLFAIL1: a client copy waiting for the game restart counts even when the local
            // server already loads its own copy (the whole-listing state then reads Loaded).
            if (installed.State == InstalledKind.Pending
                || DetectClient(entries[i]).State == InstalledKind.Pending) n++;
        }
        return n;
    }

    /// <summary>
    /// F20: the pending row's <c>Stop server to apply</c> and the drawer's <c>Save and stop server</c>.
    /// Opens the confirmation (the server, the players this client sees on it, how it starts again);
    /// its yes sends the one graceful stop request (<see cref="ServerStopRequest"/>). While a request
    /// is in flight a press does nothing. Progenitor never starts the server again from here.
    /// </summary>
    public void RequestRestart()
    {
        if (_stop.InFlight)
        {
            try { Plugin.Logger?.LogInfo("Install: Stop server to apply pressed while request " + _stop.Nonce + " is in flight; nothing sent."); }
            catch { /* */ }
            return;
        }

        var factory = DialogFactory();
        if (factory == null)
        {
            // No dialog kit (a pane without a panel): nothing can confirm, so nothing is sent.
            try { Plugin.Logger?.LogInfo("Install: Stop server to apply pressed with no dialog to confirm it; nothing sent."); }
            catch { /* */ }
            return;
        }

        bool remote = CurrentRoute.Kind == ServerRouteKind.Remote;
        var identity = ServerIdentityFor(remote);
        string why = StopGate(remote);
        bool refused = why.Length > 0;
        // F30: the configured local server Progenitor can start again gets a real restart.
        bool again = !remote && !refused && CanStartAgain();
        var facts = new List<DialogFact>
        {
            new(InstallModsCopy.StopFactServer, identity.Phrase),
            new(InstallModsCopy.StopFactPlayers, InstallModsCopy.PlayerCounter(OnlinePlayers())),
            again
                ? new DialogFact(InstallModsCopy.StopFactStartsAgain, InstallModsCopy.StartsAgainByProgenitor)
                : new DialogFact(InstallModsCopy.StopFactStartsAgain, remote ? InstallModsCopy.StartsAgainRemote : InstallModsCopy.StartsAgainLocal,
                    DialogFactTone.Warn),
        };
        CloseStopDialog();
        var dialog = UiDialog.Open(
            factory,
            tag: "install",
            kind: StopDialogKind,
            key: StopDialogKind,
            heading: again ? InstallModsCopy.RestartServerHeading(identity.TitleName) : InstallModsCopy.StopServerHeading(identity.TitleName),
            body: "",
            facts: facts,
            note: refused ? InstallModsCopy.StopUnavailable(why) : InstallModsCopy.StopServerNote,
            yesCaption: refused ? "" : again ? InstallModsCopy.StopAndStartAgain : InstallModsCopy.StopServerYes,
            yesTone: LabelTone.Danger,
            noCaption: refused ? InstallModsCopy.StopServerClose : InstallModsCopy.StopServerNo,
            rowKey: StopDialogRowKey,
            yesKey: CellStopYes,
            noKey: refused ? CellStopClose : CellStopNo);
        _stopDialog = dialog;
        if (!refused)
            dialog.OnYes(() =>
            {
                // Review L-a: the gate and the restart offer are read again at Yes (the box may have been open a while).
                string now = StopGate(remote);
                if (now.Length > 0)
                {
                    try { Plugin.Logger?.LogInfo("Install: stop not sent; " + now); }
                    catch { /* */ }
                    return;
                }

                BeginStop(remote, identity, again && !remote && CanStartAgain());
            });
        dialog.OnNo(() => { });
        try { Plugin.Logger?.LogInfo("Install: Stop server to apply pressed; confirmation open" + (refused ? " (refused: " + why + ")." : ".")); }
        catch { /* */ }
    }

    internal const string StopDialogKind = "stop-server";
    internal const string StopDialogRowKey = "install-stop-buttons";
    internal const string CellStopYes = "stopYes";
    internal const string CellStopNo = "stopNo";
    internal const string CellStopClose = "stopClose";

    private UiDialog? _stopDialog;

    /// <summary>The stop confirmation on screen, or null.</summary>
    internal UiDialog? StopDialog => _stopDialog is { IsOpen: true } open ? open : null;

    private void CloseStopDialog()
    {
        var open = _stopDialog;
        _stopDialog = null;
        try { if (open is { IsOpen: true }) open.Close(); }
        catch { /* */ }
    }

    /// <summary>F20: the one stop request this pane has out, and its outcome.</summary>
    private readonly ServerStopRequest _stop = new();

    internal ServerStopRequest StopRequest => _stop;

    /// <summary>F20 test seam: the clock the stop request times out by. Null is the wall clock.</summary>
    internal Func<DateTime>? StopClock { get => _stop.Clock; set => _stop.Clock = value; }

    /// <summary>F20: the reply inbox for a nonce. Null reads <see cref="StopServerEvents.ReplyFor"/>.</summary>
    internal Func<int, StopServerReply?>? StopReplyProbe { get; set; }

    /// <summary>
    /// F20: the connected players' character names as this client's world lists them, or null when
    /// it cannot tell (not connected, no world). Wired by <c>ModsMenu</c>.
    /// </summary>
    internal Func<IReadOnlyList<string>?>? OnlinePlayersProbe { get; set; }

    /// <summary>F20: true while this client is connected to a server. Null means "cannot tell".</summary>
    internal Func<bool>? ConnectedProbe { get; set; }

    /// <summary>Seconds between reads of the answer while a stop request is in flight.</summary>
    public const double StopPollSeconds = 0.5;

    private IReadOnlyList<string>? OnlinePlayers()
    {
        try { return OnlinePlayersProbe?.Invoke(); }
        catch { return null; }
    }

    /// <summary>
    /// "" when the stop may be asked; otherwise why not. The remote route is the connected server
    /// itself. The local route must be the server this client is connected to, running Progenitor —
    /// the same check Preferences' Stop server makes (<see cref="ServerProcessControl.GracefulAvailable"/>).
    /// </summary>
    private string StopGate(bool remote)
    {
        if (RemoteRestartRequest == null) return ServerProcessControl.ReasonNoTransport;
        if (remote) return "";
        string root = LocalStopRoot();
        if (root.Length == 0) return ServerProcessControl.ReasonNoFolder;
        try
        {
            // LOCALDETECT1 review (fail closed): an auto-detected server must have a readable command line and its own save
            // folder before a stop (which saves its world) is sent through this route.
            bool detected = DetectedRouteFolder() is { Length: > 0 } d && string.Equals(TrimSep(root), TrimSep(d), StringComparison.OrdinalIgnoreCase);
            if (LocalStopGate != null)
            {
                string gate = LocalStopGate(root) ?? "";
                if (gate.Length > 0 || !detected) return gate;
                return (DetectedStopGate ?? DefaultDetectedStopGate)(root) ?? "";
            }

            if (detected) return DefaultDetectedStopGate(root);
            return ServerProcessControl.GracefulAvailable(root, out string why) ? "" : why;
        }
        catch (Exception e) { return e.Message; }
    }

    private static string DefaultDetectedStopGate(string root) =>
        ServerProcessControl.GracefulAvailableDetected(root, out string why) ? "" : why;

    private static string TrimSep(string path) => (path ?? "").Trim().TrimEnd('\\', '/');

    /// <summary>
    /// LOCALDETECT1 review: the auto-detected local server's folder while the route decision makes it the local route
    /// (live: ModsMenu's <c>DetectedRouteBepInExRoot</c>), else null. Its stop takes the stricter gate.
    /// </summary>
    internal Func<string?>? DetectedRouteFolderProbe { get; set; }

    /// <summary>Test seam: the detected server's extra stop gate (root → "" to allow). Null: <see cref="ServerProcessControl.GracefulAvailableDetected"/>.</summary>
    internal Func<string, string?>? DetectedStopGate { get; set; }

    private string? DetectedRouteFolder()
    {
        try { return DetectedRouteFolderProbe?.Invoke(); }
        catch { return null; }
    }

    /// <summary>Test seam for the local route's gate: root → "" to allow, or why not. Null is <see cref="ServerProcessControl.GracefulAvailable"/>.</summary>
    internal Func<string, string?>? LocalStopGate { get; set; }

    /// <summary>
    /// Review M-e: the private game's folder while the route decision makes it the local route (live: ModsMenu's
    /// <c>RouteOverrideBepInExRoot</c>; LOCALDETECT1: or the auto-detected local server's folder for this connection), else
    /// null. Null probe: the hosting flag (tests). Save, stop and start again stays the configured folder's only.
    /// </summary>
    internal Func<string?>? HostedRouteFolderProbe { get; set; }

    private string? HostedRouteFolder()
    {
        try { return HostedRouteFolderProbe != null ? HostedRouteFolderProbe() : Install.HostedServerFolder.Current(); }
        catch { return null; }
    }

    // Review (L7): the local route's own folder: the hosted private game's while this client hosts one.
    private string LocalStopRoot() =>
        HostedRouteFolder() is { Length: > 0 } hosted && CurrentRoute.Kind == ServerRouteKind.Local ? hosted
        : _server != null && _server.HasValidatedServer ? _server.Current.CanonicalPath ?? "" : "";

    /// <summary>
    /// F20: the confirmation's yes. Sends the request, relabels and disables the button, and reads
    /// the answer every <see cref="StopPollSeconds"/> until it ends. On the local route the process
    /// seen before the stop is kept so its launch record is written once it exits, which is what
    /// makes Preferences' Start server available.
    /// </summary>
    private void BeginStop(bool remote, ServerIdentity identity, bool startAgain = false)
    {
        _stopDialog = null;
        string root = remote ? "" : LocalStopRoot();
        ServerProcessInfo? before = null;
        if (root.Length > 0)
        {
            try { before = ServerProcessControl.Running(root); }
            catch { before = null; }
        }

        _stop.Send = RemoteRestartRequest;
        _stop.Poll = StopReplyProbe ?? StopServerEvents.ReplyFor;
        // The server quitting ends this client's connection to it. Only a connection that was there
        // when the request left counts as lost; a client that cannot tell never ends early.
        var connected = ConnectedProbe;
        bool connectedAtStart = false;
        try { connectedAtStart = connected != null && connected(); }
        catch { connectedAtStart = false; }
        _stop.Gone = () =>
        {
            if (before != null && root.Length > 0 && ServerProcessControl.Running(root) == null) return true;
            return connectedAtStart && connected != null && !connected();
        };
        _stop.Log = line =>
        {
            try { Plugin.Logger?.LogInfo(line); }
            catch { /* */ }
        };
        _startAgainLine = "";
        // Review M-a: only a stop whose process this pane saw with a pid and a read command line starts it again (the
        // record it writes is that launch); a bare or unknown launch is never replayed.
        _stopStartAgain = startAgain && root.Length > 0 && before is { Pid: > 0 } seen
            && !string.IsNullOrWhiteSpace(seen.CommandLine) && !ServerProcessControl.IsLiveServer(root, process: seen);
        bool sent = _stop.Begin(identity.TitleName,
            remote ? InstallModsCopy.StoppedRemoteTail : _stopStartAgain ? InstallModsCopy.StartingAgainTail : InstallModsCopy.StoppedLocalTail,
            restartsAgain: _stopStartAgain);
        _restartRequested = true;
        try { Plugin.Logger?.LogInfo("Install: stop confirmed; graceful stop request " + _stop.Nonce + " sent=" + sent + "."); }
        catch { /* */ }
        _stopRoot = root;
        _stopProcess = before;
        SyncStopCells();
        ScheduleStopTick();
    }

    private string _stopRoot = "";
    private ServerProcessInfo? _stopProcess;

    /// <summary>F20: one read of the answer; the footer follows. Tests call it directly.</summary>
    internal void TickStopRequest()
    {
        if (!_stop.Tick()) return;
        ServerProcessInfo? stopped = null;
        ServerLaunchRecord? written = null;
        if (_stop.Phase == StopRequestPhase.Stopped && _stopRoot.Length > 0 && _stopProcess is { } process)
        {
            // As Preferences' Stop server does: the record Start server replays.
            try { written = ServerProcessControl.WriteLaunchRecord(_stopRoot, process); }
            catch { written = null; /* best effort, like the stop's own */ }
            stopped = process;
            _stopProcess = null;
        }

        if (_stopStartAgain)
        {
            if (_stop.Phase == StopRequestPhase.Stopped) BeginStartAgain(_stopRoot, _stop.ServerName, stopped, written);
            else if (!_stop.InFlight) _stopStartAgain = false;
        }

        SyncStopCells();
    }

    // ---- F30: Save, stop and start again (the configured local server Progenitor can start) ------------------------

    private bool _stopStartAgain;
    private volatile string _startAgainLine = "";

    /// <summary>F30: the footer's line once the start after the stop finished ("" until then).</summary>
    internal string StartAgainLine => _startAgainLine;

    /// <summary>F30 test seam: runs the start (live: a background thread, never the main thread).</summary>
    internal Action<Action>? StartAgainRunner { get; set; }

    /// <summary>Seconds the start waits for the stopped server's process to exit.</summary>
    internal const int StartAgainExitWaitSeconds = 60;

    /// <summary>
    /// F30: the local route's configured, validated server folder (not a private game this client hosts), whose server
    /// is running now, so its process is what the stop's launch record replays. Never a remote server.
    /// </summary>
    internal bool CanStartAgain()
    {
        try
        {
            if (CurrentRoute.Kind != ServerRouteKind.Local) return false;
            if (HostedRouteFolder() is { Length: > 0 }) return false;
            if (_server == null || !_server.HasValidatedServer) return false;
            // Review L-a: the one root the stop uses.
            string root = LocalStopRoot();
            if (root.Length == 0 || !string.Equals(root, _server.Current.CanonicalPath ?? "", StringComparison.OrdinalIgnoreCase)) return false;
            // Review H1: never the live dedicated server (review L3: the fence's answer is cached per root and process).
            if (ServerProcessControl.IsLiveServerCached(root)) return false;
            // Main thread: the cached answer (never a process scan here). Review M-a: a process with a pid and a read
            // command line, so the record the stop writes is the launch to replay; review M2: one the start would accept
            // (its own -persistentDataPath, not the default save folder).
            var running = ServerProcessControl.Running(root);
            return running is { Pid: > 0 } && !string.IsNullOrWhiteSpace(running.CommandLine)
                && !ServerProcessControl.IsLiveServerCached(root, running)
                && ServerProcessControl.ReplayRefusalCached(root, running).Length == 0;
        }
        catch { return false; }
    }

    private void BeginStartAgain(string root, string name, ServerProcessInfo? stopped, ServerLaunchRecord? record)
    {
        _stopStartAgain = false;
        try { Plugin.Logger?.LogInfo("Install: " + name + " stopped; starting it again from its folder."); }
        catch { /* */ }
        void Run()
        {
            ServerStartOutcome outcome;
            try { outcome = ServerProcessControl.StartAgainAfterStop(root, stopped, record, StartAgainExitWaitSeconds); }
            catch (Exception e) { outcome = new ServerStartOutcome(false, 0, e.Message); }
            _startAgainLine = outcome.Ok ? InstallModsCopy.StartedAgainLine(name) : InstallModsCopy.StartAgainFailedLine(name, outcome.Line);
            try { Plugin.Logger?.LogInfo("Install: start again " + (outcome.Ok ? "started (pid " + outcome.Pid + ")" : "failed: " + outcome.Line) + "."); }
            catch { /* */ }
        }

        var runner = StartAgainRunner;
        if (runner != null) runner(Run);
        else System.Threading.Tasks.Task.Run(Run);
        ScheduleStartAgainSync();
    }

    private void ScheduleStartAgainSync()
    {
        var post = PostDelayedHook;
        if (post == null)
        {
            SyncStopCells();
            return;
        }

        try
        {
            post(StopPollSeconds, () =>
            {
                SyncStopCells();
                if (_startAgainLine.Length == 0) ScheduleStartAgainSync();
            });
        }
        catch { /* the footer keeps its last line */ }
    }

    private void ScheduleStopTick()
    {
        var post = PostDelayedHook;
        if (post == null || !_stop.InFlight) return;
        try
        {
            post(StopPollSeconds, () =>
            {
                TickStopRequest();
                ScheduleStopTick();
            });
        }
        catch { /* the footer keeps its last line */ }
    }

    /// <summary>The pending bar's cells follow the stop request in place: caption, tone and sentence.</summary>
    private void SyncStopCells()
    {
        if (_pendingBar.Count == 0 || _pendingBar[0] is not IUiCellRow bar) return;
        try
        {
            string caption = PendingRestartCaption(PendingSides());
            if (caption.Length > 0)
                bar.SetCell(CellPendingRestart, text: caption, tone: _stop.InFlight ? CellTone.Disabled : CellTone.Normal);
            string line = PendingLine(_pendingBarText);
            bar.SetCell(CellPendingText, text: line);
        }
        catch { /* a bar that went away with a rebuild */ }

        if (_drawerHead != null)
        {
            try
            {
                // False (and nothing written) when the open drawer has no such button.
                _drawerHead.SetCell(CellRestartNow, text: _stop.InFlight ? InstallModsCopy.StoppingServer : CanStartAgain() ? InstallModsCopy.StopAndStartAgain : ServerRouteCopy.RestartNowCaption,
                    tone: _stop.InFlight ? CellTone.Disabled : CellTone.Normal);
            }
            catch { /* */ }
        }
    }

    /// <summary>The bar's sentence: the stop request's line while it has one, else the pending sentence.</summary>
    private string PendingLine(string sentence)
    {
        if (_startAgainLine.Length > 0 && _stop.Phase == StopRequestPhase.Stopped) return _startAgainLine;
        string line = _stop.Line;
        return line.Length > 0 ? line : sentence;
    }

    public bool RestartRequested => _restartRequested;

    // ---- Text helpers ------------------------------------------------------------------

    public static string HeadingText(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        return entry.DisplayName ?? entry.PackageName ?? entry.SearchLabel;
    }

    public static string HeaderAuthor(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        return entry.Owner ?? entry.Author ?? "unknown";
    }

    public static string HeaderVersion(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        return string.IsNullOrWhiteSpace(entry.Manifest.Version) ? "unknown" : entry.Manifest.Version;
    }

    public const string ReleasedSourceVersion = "version";
    public const string ReleasedSourcePackage = "package";
    public const string ReleasedSourceNone = "none";

    /// <summary>
    /// <c>YYYY-MM-DD</c> of the release date and where it came from
    /// (<see cref="ReleasedSourceVersion"/> / <see cref="ReleasedSourcePackage"/> /
    /// <see cref="ReleasedSourceNone"/>). Empty when there is none. The index stamp is never a release date.
    /// </summary>
    public static string ReleasedDate(CatalogEntry entry, out string source)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (TryFormatDate(entry.ReleasedUtc, out string released))
        {
            source = ReleasedSourceVersion;
            return released;
        }
        if (TryFormatDate(entry.DateUpdated, out string updated))
        {
            source = ReleasedSourcePackage;
            return updated;
        }
        source = ReleasedSourceNone;
        return "";
    }

    public static string ReleasedSourceOf(CatalogEntry entry)
    {
        ReleasedDate(entry, out string source);
        return source;
    }

    /// <summary>The drawer's date: <c>YYYY-MM-DD</c>, or <c>—</c> when none.</summary>
    public static string ReleasedText(CatalogEntry entry)
    {
        string date = ReleasedDate(entry, out _);
        return date.Length == 0 ? InstallModsCopy.Dash : date;
    }

    /// <summary>
    /// INSTALLCOLS1: "today" for the short dates — the local date, which decides whether a date
    /// carries its year. A test substitutes a fixed day; null reads the machine's clock.
    /// </summary>
    internal Func<DateTime>? TodayProbe { get; set; }

    private DateTime Today()
    {
        try { return (TodayProbe?.Invoke() ?? DateTime.Now).Date; }
        catch { return DateTime.Now.Date; }
    }

    /// <summary>
    /// INSTALLCOLS1: the Released column — the same release date as the drawer (the version's
    /// <c>date_created</c>, else the package's <c>date_updated</c>) as a short plain date
    /// (<c>Sep 23</c>, <c>Sep '25</c> in another year), or <c>—</c>. The readback's
    /// <c>releasedDate</c> is the full <c>YYYY-MM-DD</c>.
    /// </summary>
    internal string ReleasedShortText(CatalogEntry entry)
    {
        string date = ReleasedDate(entry, out _);
        if (date.Length == 0
            || !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return InstallModsCopy.Dash;
        return InstallColumns.ShortDate(day, Today());
    }

    /// <summary>
    /// INSTALLCOLS1: the file a loaded plugin came from, for the Installed column of a mod another
    /// tool installed. Defaults to <see cref="InstallTimes.PluginLocation"/>; a test substitutes one.
    /// </summary>
    internal static Func<string, string?>? PluginFileProbe { get; set; }

    private Dictionary<CatalogEntry, InstallTime>? _installedMemo;

    /// <summary>Is this row installed on this machine in a way the Installed column dates?</summary>
    private static bool DatesInstall(InstallListingState state) =>
        state is InstallListingState.Installed or InstallListingState.Pending
            or InstallListingState.PendingUninstall or InstallListingState.Update;

    /// <summary>
    /// INSTALLCOLS1: when this machine installed the listing, through the one shared rule
    /// (<see cref="InstallTimes"/>): the earliest <c>writtenUtc</c> of the install-ledger rows that
    /// name the listing; else, for a mod another tool installed, its plugin file's creation time
    /// (last-write time when that cannot be read); else the file a Progenitor scan found.
    /// <see cref="InstallTime.Unknown"/> for a listing that is not installed.
    /// </summary>
    internal InstallTime InstalledTimeFor(CatalogEntry entry)
    {
        if (entry == null) return InstallTime.Unknown;
        if (_installedMemo != null && _installedMemo.TryGetValue(entry, out var known)) return known;
        var answer = InstalledTimeNow(entry);
        if (_installedMemo != null) _installedMemo[entry] = answer;
        return answer;
    }

    private InstallTime InstalledTimeNow(CatalogEntry entry)
    {
        try
        {
            if (!DatesInstall(ActionFor(entry).State)) return InstallTime.Unknown;
            var rows = InstalledState.LedgerRowsFor(entry, _installedQuery);
            var stamps = new List<DateTime?>(rows.Count);
            for (int i = 0; i < rows.Count; i++) stamps.Add(rows[i].WrittenUtc);
            DateTime? ledger = InstallTimes.Earliest(stamps);
            if (ledger != null) return InstallTimes.Pick(ledger, null, null);

            var installed = DetectInstalled(entry);
            string? file = null;
            if (installed.PluginId.Length > 0)
            {
                var probe = PluginFileProbe;
                file = probe != null ? probe(installed.PluginId) : InstallTimes.PluginLocation(installed.PluginId);
            }
            if (string.IsNullOrWhiteSpace(file) && installed.Files.Count > 0) file = installed.Files[0];
            var (created, written) = InstallTimes.FileTimes(file);
            return InstallTimes.Pick(null, created, written);
        }
        catch { return InstallTime.Unknown; }
    }

    /// <summary>The Installed column's sort key: the install time, or null (sorted last) when not installed.</summary>
    internal DateTime? InstalledUtcFor(CatalogEntry entry) => InstalledTimeFor(entry).Utc;

    /// <summary>
    /// INSTALLCOLS1: the Installed column — the local date this machine installed the listing as a
    /// short plain date, or blank when it is not installed (or nothing says when).
    /// </summary>
    internal string InstalledText(CatalogEntry entry)
    {
        var at = InstalledTimeFor(entry);
        return at.Utc == null ? "" : InstallColumns.ShortDate(at.Utc.Value.ToLocalTime(), Today());
    }

    /// <summary>Readback <c>listing.installed {date, utc, source}</c>.</summary>
    private Dictionary<string, object?> InstalledToWidget(CatalogEntry entry)
    {
        var at = InstalledTimeFor(entry);
        return new Dictionary<string, object?>
        {
            ["date"] = InstalledText(entry),
            ["fullDate"] = at.Utc == null ? null : InstallColumns.FullDate(at.Utc.Value.ToLocalTime()),
            ["utc"] = at.Utc?.ToString("o", CultureInfo.InvariantCulture),
            ["source"] = at.SourceName,
        };
    }

    /// <summary>
    /// INSTALLCOLS1: the row width the column readback resolves against — the Install pane's
    /// full-width row for the last workspace frame (the 1920×1080 fallback before one exists). A
    /// test substitutes a width.
    /// </summary>
    internal Func<float>? ColumnRowWidthProbe { get; set; }

    internal float ColumnRowWidth()
    {
        try
        {
            var probe = ColumnRowWidthProbe;
            if (probe != null) return probe();
            var last = ModsWorkspaceFrame.Last;
            var frame = last.Width > 1f ? last : ModsWorkspaceFrame.Fallback(1920f, 1080f);
            return MenuPanelLayout.ForFrame(frame, sidebar: false, PaneMode).RowWidth;
        }
        catch { return UnityUiRuntimeMetrics.RowWidth; }
    }

    /// <summary>
    /// Readback <c>header.columns</c>: the stable set, the resolved width of every column at
    /// <see cref="ColumnRowWidth"/> (0 for a dropped one), the dropped columns in the order they
    /// went, the drop priority itself, and which columns sort.
    /// </summary>
    internal Dictionary<string, object?> ColumnsToWidget()
    {
        float rowWidth = ColumnRowWidth();
        var cells = HeaderCells();
        var placed = InstallColumns.Resolve(rowWidth, cells);
        var widths = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sortable = new List<string>();
        for (int i = 0; i < cells.Count; i++)
        {
            widths[cells[i].Key] = placed[i].Visible ? (object)Math.Round(placed[i].Width, 1) : 0d;
            if (!string.IsNullOrEmpty(cells[i].SortKey)) sortable.Add(cells[i].SortKey!);
        }
        return new Dictionary<string, object?>
        {
            ["set"] = InstallColumns.ColumnNames,
            ["keys"] = InstallColumns.Keys,
            ["rowWidth"] = Math.Round(rowWidth, 1),
            ["widths"] = widths,
            ["dropped"] = InstallColumns.DroppedAt(rowWidth, cells),
            ["dropPriority"] = InstallColumns.DropPriority,
            ["sortable"] = sortable,
        };
    }

    /// <summary>The drawer meta line: <c>by dyno · released 2026-08-30 · loads in my client</c> (+ the stale note).</summary>
    public string MetaLine(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        string date = ReleasedDate(entry, out string source);
        string when = source == ReleasedSourcePackage ? "updated " + date : "released " + (date.Length == 0 ? InstallModsCopy.UpdatedUnknown : date);
        string line = "by " + HeaderAuthor(entry) + " · " + when + " · " + InstallModsCopy.LoadsInPrefix + LoadsIn(SideOf(entry), IsNotInstallable(entry));
        string? tag = CurrentUpdateTag;
        if (tag != null && !CatalogUpdateTags.IsTagged(entry, tag))
            line += " · " + InstallModsCopy.StaleWarnMark + UpdateTagText(entry) + InstallModsCopy.StaleSuffix;
        return line;
    }

    private static bool TryFormatDate(string? stamp, out string date)
    {
        date = "";
        if (string.IsNullOrWhiteSpace(stamp)) return false;
        string formatted = FormatReleasedDate(stamp);
        if (formatted == InstallModsCopy.UpdatedUnknown) return false;
        date = formatted;
        return true;
    }

    /// <summary>ISO timestamp to <c>YYYY-MM-DD</c>. Unknown when the stamp cannot be parsed.</summary>
    public static string FormatReleasedDate(string? stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp)) return InstallModsCopy.UpdatedUnknown;
        if (stamp.Length >= 10
            && stamp[4] == '-' && stamp[7] == '-'
            && DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            return stamp.Substring(0, 10);
        if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var issued))
            return InstallModsCopy.UpdatedUnknown;
        DateTime utc = issued.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(issued, DateTimeKind.Utc)
            : issued.ToUniversalTime();
        return utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The Downloads column: <c>1,234,567</c>, or <c>—</c>.</summary>
    public static string DownloadsText(CatalogEntry entry)
    {
        if (entry?.Downloads == null) return InstallModsCopy.Dash;
        return entry.Downloads.Value.ToString("#,0", CultureInfo.InvariantCulture);
    }

    /// <summary>The drawer's Update tag fact: every tag the author set, without the <c> Update</c> suffix; <c>—</c> when untagged.</summary>
    public static string UpdateTagText(CatalogEntry entry)
    {
        var tags = CatalogUpdateTags.UpdateTagsOf(entry);
        if (tags.Count == 0) return InstallModsCopy.UpdateTagNone;
        var names = new List<string>(tags.Count);
        for (int i = 0; i < tags.Count; i++) names.Add(InstallModsCopy.StripUpdateSuffix(tags[i]));
        return string.Join(", ", names);
    }

    /// <summary>
    /// INSTALLCOLS1: the note that leads the second line under a listing's name, in the warn tone,
    /// when the listing does not carry the current update's tag — <c>Built for Gloomrot</c> (the
    /// author's last tag), or <c>No update tag</c> when it has none. Empty when it carries the
    /// current tag, and when the current update is unknown. This replaces LIST3's Update column,
    /// which repeated <c>Oakveil</c> on every row while the Current update filter was on.
    /// </summary>
    public static string UpdateNoteText(CatalogEntry entry, string? currentTag)
    {
        if (entry == null || string.IsNullOrEmpty(currentTag)) return "";
        if (CatalogUpdateTags.IsTagged(entry, currentTag)) return "";
        var tags = CatalogUpdateTags.UpdateTagsOf(entry);
        if (tags.Count == 0) return InstallModsCopy.NoUpdateTag;
        return InstallModsCopy.BuiltForPrefix + InstallModsCopy.StripUpdateSuffix(tags[tags.Count - 1]);
    }

    public static string DescriptionText(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Description)) return InstallModsCopy.NoDescription;
        return PlainUntrusted(entry.Description);
    }

    /// <summary>The row's second line: the one-liner collapsed to one line (TMP ellipsis clips it to the cell).</summary>
    public static string ListingSnippet(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        return CollapseWhitespace(PlainUntrusted(description.Trim()));
    }

    /// <summary>
    /// The two-line name cell: <c>Name\nby owner</c>. INSTALLSIDES1: the author is the Mod column's
    /// second line (the Author column is gone); the one-liner opens the drawer instead.
    /// </summary>
    public static string NameCellText(CatalogEntry entry)
    {
        string name = HeadingText(entry);
        string author = HeaderAuthor(entry);
        return author.Length == 0 ? name : name + "\n" + InstallModsCopy.ByPrefix + author;
    }

    /// <summary>
    /// INSTALL2 item 2: the row's two lines as this pane draws them. An uninstalled-but-pending row
    /// replaces the one-liner with <see cref="InstallModsCopy.UninstalledPendingLine"/> — what the mod
    /// does is no longer the useful sentence when it is on its way out.
    /// </summary>
    internal string RowNameText(CatalogEntry entry) => NameCellText(entry);

    public static string DependenciesFact(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        var names = new List<string>();
        for (int i = 0; i < entry.PluginDependencies.Count; i++)
        {
            string id = entry.PluginDependencies[i].Id;
            if (!string.IsNullOrWhiteSpace(id)) names.Add(id);
        }
        for (int i = 0; i < entry.ForeignDependencies.Count; i++)
        {
            string id = entry.ForeignDependencies[i];
            if (!string.IsNullOrWhiteSpace(id)) names.Add(id);
        }
        return names.Count == 0 ? InstallModsCopy.DependenciesNone : string.Join(", ", names);
    }

    public static string SizeFact(CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        long? size = entry.ListingFileSize ?? entry.Manifest.Size;
        if (size == null || size.Value <= 0) return InstallModsCopy.SizeUnknown;
        return FormatSize(size.Value);
    }

    /// <summary>The Package fact: <c>zip · 1.2 MB</c>.</summary>
    public static string PackageFact(CatalogEntry entry) => "zip · " + SizeFact(entry);

    /// <summary>
    /// Job LIST4 item 1. The drawer head's caption: the one install fact. A tool row has no
    /// install fact to state, so its head is blank rather than claiming a restart either way.
    /// </summary>
    internal static string HeadNoteText(InstallRowAction action)
    {
        if (action.State is InstallListingState.Tool or InstallListingState.Loader) return "";
        return action.Restart ? InstallModsCopy.RestartRequired : InstallModsCopy.NoRestartNeeded;
    }

    /// <summary>
    /// INSTALLSIDES2 item 5: the drawer head's note, one part per side the mod uses
    /// (<see cref="SideCells.RestartNote"/>): the client from the open plan when there is one, else
    /// the listing's own declaration; the server from the picked server's route.
    /// </summary>
    internal string HeadNoteText(CatalogEntry entry, InstallRowAction action)
    {
        bool notInstallable = action.State is InstallListingState.Tool or InstallListingState.Loader;
        bool? clientPlan = IsOpen(entry) && _lastPlan != null && _target != InstallTarget.LocalServer
            ? PlanNeedsRestart(_lastPlan)
            : null;
        bool serverHot = false; // HOTDEMO1
        try { serverHot = CurrentRoute.Kind == ServerRouteKind.Local && (Service?.LocalServerHot(InstallTarget.LocalServer) ?? false); }
        catch { serverHot = false; }
        return SideCells.RestartNote(SideOf(entry), ServerTarget, CurrentRoute.Kind,
            InstallSearchFilters.DeclaresNoRestart(entry), clientPlan, notInstallable, serverHot);
    }

    /// <summary>
    /// Job LIST4 item B. Is this README block one the drawer already shows above it? A listing
    /// whose README opens by restating its own one-liner ("Removes version watermark") or by
    /// naming itself ("VRising-RemoveBuildNumberLabel", "RemoveBuildNumberLabel") produced four
    /// copies of the same sentence in an expanded drawer. Only an exact block — the trimmed,
    /// whitespace-collapsed text equal to one of those, ignoring case — is dropped: a paragraph
    /// that merely <em>starts</em> with the name is still the description and is kept.
    /// </summary>
    internal static bool IsDuplicateReadmeBlock(string? text, CatalogEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (string.IsNullOrWhiteSpace(text)) return false;
        string plain = CollapseWhitespace(PlainUntrusted(text).Trim());
        if (plain.Length == 0) return false;
        return Same(plain, ListingSnippet(entry.Description))
            || Same(plain, entry.PackageName)
            || Same(plain, entry.DisplayName)
            || Same(plain, entry.Key);

        static bool Same(string plain, string? other) =>
            !string.IsNullOrWhiteSpace(other)
            && string.Equals(plain, CollapseWhitespace(other.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    public InstallPaneSurface Surface(bool thunderstoreEnabled, bool nexusEnabled)
    {
        if (!thunderstoreEnabled && !nexusEnabled)
            return InstallPaneSurface.NoSourceEnabled;

        var status = _catalog.Status;
        int entries = _catalog.Search(null).Count;
        // A cached index on show stays the list while a refresh runs (LIST3 item 10).
        if ((status.Kind == InstallStatusKind.LoadingIndex || _indexRefreshing) && entries == 0)
            return InstallPaneSurface.Loading;
        // INSTALLERR1: a list from another source than the current one (the dev URL was set or cleared) is never shown;
        // the Mods menu rebuilds the catalog for the new source.
        if (entries > 0 && !FlowActive && ShownFromOtherSource())
            return InstallPaneSurface.Loading;
        // An entry-scoped failure (a refused download, inspection or plan: EntryKey set) belongs
        // to that row's action, never to the index surface.
        if ((status.Kind == InstallStatusKind.Failed && string.IsNullOrEmpty(status.EntryKey)) || !string.IsNullOrEmpty(_lastError))
            return InstallPaneSurface.Error;
        if (!_indexLoadAttempted && entries == 0)
            return InstallPaneSurface.IndexNotLoaded;
        return InstallPaneSurface.Ready;
    }

    public static string SurfaceMessage(InstallPaneSurface surface, string? error)
    {
        return surface switch
        {
            InstallPaneSurface.NoSourceEnabled => InstallModsCopy.NoSourceEnabled,
            InstallPaneSurface.IndexNotLoaded => InstallModsCopy.IndexNotLoaded,
            InstallPaneSurface.Loading => InstallModsCopy.Loading,
            InstallPaneSurface.Error => string.IsNullOrEmpty(error) ? "The package index failed to load." : error,
            _ => "",
        };
    }

    /// <summary>Footer captions the Install tab pins: none since LIST3 item 9 (the pending bar is not a footer).</summary>
    public static IReadOnlyList<string> SidebarFooterCaptions { get; } = Array.Empty<string>();

    public static bool FooterHasOverviewRefresh
    {
        get
        {
            for (int i = 0; i < SidebarFooterCaptions.Count; i++)
            {
                if (string.Equals(SidebarFooterCaptions[i], InstallModsCopy.OverviewRefresh, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    // ---- Drawer documents -------------------------------------------------------------

    public ListingDocState ReadmeState => _readmeState;

    public string ReadmePlain => _readmePlain;

    public int ReadmeBlockCount => _readmeBlockCount;

    /// <summary>True once the opened listing's README is known (ready or unavailable).</summary>
    public bool ListingDocumentsSettled =>
        !string.IsNullOrEmpty(_state.DrawerKey)
        && string.Equals(_docsKey, _state.DrawerKey, StringComparison.OrdinalIgnoreCase)
        && (_readmeState == ListingDocState.Ready || _readmeState == ListingDocState.Unavailable)
        && !NeedsStructureFetch();

    /// <summary>
    /// README-STRUCT1: the open drawer shows a README cached as plain text only (before the markdown
    /// was kept): ask the network once for the markdown, then keep what it answers (a failure leaves
    /// the plain text in place and is not retried).
    /// </summary>
    private bool NeedsStructureFetch()
    {
        if (_readmeState != ListingDocState.Ready) return false;
        if (!_catalog.TryGetEntry(_state.DrawerKey!, out var entry) || entry == null) return false;
        return !_state.StructureFetchTried.Contains(ReadmeIndexKey(entry)) && ReadmePlainOnly(entry);
    }

    internal IUiHandle? ReadmeBodyHandle => _readmeFirst;

    internal object ReadmeToWidget() => new
    {
        state = DocStateName(_readmeState),
        chars = _readmePlain.Length,
        reason = _readmeState == ListingDocState.Unavailable ? _readmeReason : null,
        blocks = _readmeBlockCount,
    };

    /// <summary>Start the on-demand README fetch for the opened listing (the drawer shows <c>Loading description…</c> meanwhile).</summary>
    public Task<ListingDocumentSet> BeginLoadListingDocuments(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_state.DrawerKey) || !_catalog.TryGetEntry(_state.DrawerKey!, out var entry) || entry == null)
            return Task.FromResult(ListingDocumentSet.Unavailable(_state.DrawerKey ?? "", InstallReasons.UnknownEntry));

        _docsKey = entry.Key;
        if (_readmeState != ListingDocState.Ready && _readmeState != ListingDocState.Unavailable)
            _readmeState = ListingDocState.Loading;
        _state.StructureFetchTried.Add(ReadmeIndexKey(entry));
        return _catalog.LoadListingDocumentsAsync(entry.Key, cancellationToken);
    }

    /// <summary>Apply a completed fetch on the main thread. Ignores a result for a listing that is no longer open.</summary>
    public void ApplyListingDocuments(ListingDocumentSet documents)
    {
        if (documents == null) return;
        if (string.IsNullOrEmpty(_state.DrawerKey)) return;
        if (!string.Equals(_docsKey, _state.DrawerKey, StringComparison.OrdinalIgnoreCase)) return;
        if (!_catalog.TryGetEntry(_state.DrawerKey!, out var opened) || opened == null) return;
        string cacheKey = ThunderstoreListingDocuments.CacheKey(opened.Owner ?? "", opened.PackageName ?? "", opened.Manifest.Version ?? "");
        bool matches = string.Equals(documents.Key, _state.DrawerKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(documents.Key, _docsKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(documents.Key, cacheKey, StringComparison.OrdinalIgnoreCase);
        if (!matches) return;

        // README-STRUCT1: a structure fetch for a README already known as plain text that fails keeps
        // the plain text (Ready) and records no verdict; it is not retried.
        if (!documents.ReadmeOk && !string.IsNullOrEmpty(ReadmeTextFor(opened)))
        {
            _readmeState = ListingDocState.Ready;
            _readmePlain = ReadmeTextFor(opened) ?? "";
            return;
        }

        NoteReadmeForSearch(opened, documents);
        if (documents.ReadmeOk)
        {
            _readmeState = ListingDocState.Ready;
            _readmeReason = null;
            _readmePlain = ReadmeTextFor(opened) ?? "";
        }
        else
        {
            _readmeState = ListingDocState.Unavailable;
            _readmeReason = documents.ReadmeError ?? "unavailable";
            _readmePlain = "";
        }
        RefilterListings();
    }

    public static string FormatReadme(string? markdown, string? listingUrl) =>
        MarkdownPlain.Join(FormatReadmeBlocks(markdown, listingUrl));

    internal static List<MarkdownBlock> FormatReadmeBlocks(string? markdown, string? listingUrl)
    {
        _ = listingUrl;
        var full = MarkdownPlain.RenderBlocks(markdown, int.MaxValue);
        var capped = MarkdownPlain.CapBlocks(full, InstallModsCopy.ReadmePlainCap);
        if (capped.Count == 0) return capped;
        if (MarkdownPlain.ContentLength(full) <= InstallModsCopy.ReadmePlainCap)
            return capped;
        for (int i = 0; i < capped.Count; i++)
        {
            if (string.Equals(capped[i].Text, InstallModsCopy.ReadMoreOnThunderstore, StringComparison.Ordinal))
                return capped;
        }
        capped.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, InstallModsCopy.ReadMoreOnThunderstore));
        return capped;
    }

    /// <summary>
    /// The drawer's README rows for the opened listing: the catalog's markdown (this
    /// session's fetch) rendered per block, else the plain text adopted from disk one row
    /// per line. Empty while nothing is known.
    /// </summary>
    internal List<MarkdownBlock> ReadmeBlocksFor(CatalogEntry entry)
    {
        if (_catalog.TryGetCachedListingDocuments(entry.Key, out var cached) && cached != null && cached.ReadmeOk)
            return WithoutDuplicateBlocks(MarkdownPlain.RenderBlocks(cached.ReadmeMarkdown, int.MaxValue), entry);
        // README-STRUCT1: the markdown this process kept or the disk cache holds, before the plain text.
        string? markdown = ReadmeMarkdownFor(entry);
        if (!string.IsNullOrEmpty(markdown))
            return WithoutDuplicateBlocks(MarkdownPlain.RenderBlocks(markdown, int.MaxValue), entry);
        string? plain = ReadmeTextFor(entry);
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(plain)) return blocks;
        foreach (var line in plain.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, line.Trim()));
        }
        return WithoutDuplicateBlocks(blocks, entry);
    }

    /// <summary>
    /// Job LIST4 item B: drop the README blocks the drawer already shows above them (see
    /// <see cref="IsDuplicateReadmeBlock"/>). Filtering here rather than at each call site keeps
    /// the cap, the <c>Read full description</c> split and the readback counts over one list.
    /// </summary>
    internal static List<MarkdownBlock> WithoutDuplicateBlocks(List<MarkdownBlock> blocks, CatalogEntry entry)
    {
        if (blocks == null || blocks.Count == 0) return blocks ?? new List<MarkdownBlock>();
        var kept = new List<MarkdownBlock>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (IsDuplicateReadmeBlock(blocks[i].Text, entry)) continue;
            kept.Add(blocks[i]);
        }
        return kept;
    }

    /// <summary>
    /// README-STRUCT1: the README markdown for this listing version, from this process's state or the
    /// disk cache's <c>.md</c> file (read once per key), or null.
    /// </summary>
    private string? ReadmeMarkdownFor(CatalogEntry entry)
    {
        string key = ReadmeIndexKey(entry);
        if (_state.ReadmeMarkdown.TryGetValue(key, out var known)) return known;
        var disk = _state.Disk;
        if (disk == null || !_state.MarkdownChecked.Add(key)) return null;
        if (disk.TryReadReadmeMarkdown(entry.Owner, entry.PackageName, entry.Manifest.Version, ReadmeStamp(entry), out string md) && md.Length > 0)
        {
            _state.ReadmeMarkdown[key] = md;
            return md;
        }

        return null;
    }

    /// <summary>
    /// README-STRUCT1: the drawer's README is only the plain search text (no markdown in memory or on
    /// disk), so its headings and code would be drawn as paragraphs.
    /// </summary>
    private bool ReadmePlainOnly(CatalogEntry entry)
    {
        if (_catalog.TryGetCachedListingDocuments(entry.Key, out var cached) && cached != null && cached.ReadmeOk) return false;
        if (!string.IsNullOrEmpty(ReadmeMarkdownFor(entry))) return false;
        return !string.IsNullOrEmpty(ReadmeTextFor(entry));
    }

    private void ResetDocuments()
    {
        _docsKey = "";
        _readmeState = ListingDocState.Idle;
        _readmeReason = null;
        _readmePlain = "";
    }

    private static string DocStateName(ListingDocState state) => state switch
    {
        ListingDocState.Ready => "ready",
        ListingDocState.Unavailable => "unavailable",
        ListingDocState.Idle => "idle",
        _ => "loading",
    };

    // ---- Build --------------------------------------------------------------------------

    /// <summary>The listing rows as built: key → (row, entry).</summary>
    internal IReadOnlyList<(IUiCellRow Row, CatalogEntry Entry, string? IconUrl)> Rows => _rows;

    internal IUiCellRow? ToolbarRow => _toolbar;
    internal IUiCellRow? FilterRow => _filters;
    internal IUiCellRow? FilterOnlyRow => _filtersOnly;
    internal IUiCellHeader? HeaderRow => _header;
    internal IUiCellRow? DrawerHeadRow => _drawerHead;
    internal IReadOnlyList<IUiHandle> DrawerHandles => _drawerHandles;
    internal IUiLabel? DrawerReasonHandle => _drawerReason;
    /// <summary>The toolbar's search cell (LIST3): its <see cref="CellSpec.Text"/> is the field's value, <see cref="CellSpec.Placeholder"/> the hint.</summary>
    internal CellSpec? SearchCell
    {
        get
        {
            var cells = _toolbar?.Cells;
            if (cells == null) return null;
            for (int i = 0; i < cells.Count; i++)
            {
                if (string.Equals(cells[i].Key, CellSearch, StringComparison.Ordinal)) return cells[i];
            }
            return null;
        }
    }
    internal IUiLabel? EmptyLabelHandle => _emptyLabel;
    internal IReadOnlyList<IUiHandle> PendingBarHandles => _pendingBar;

    /// <summary>
    /// INSTALLUX2 item 2: the status WORD one row states, or empty when the row has no install to
    /// state. The user: the row must SAY it is installed and offer Uninstall, not hide the removal
    /// behind an `Installed · new` toggle.
    /// </summary>
    /// <remarks>
    /// INSTALLCOLS1: the Status column's text — <c>Installed</c>, <c>Pending restart · to load</c>,
    /// <c>Removed at next start</c> (a ledgered and a plain pending uninstall alike),
    /// <c>Managed elsewhere</c> with the short reason on its second line, <c>Tool, not a plugin</c>.
    /// The column breaks it into at most two lines (<see cref="InstallColumns.StatusLines"/>).
    /// </remarks>
    internal static string RowStatusText(InstallRowAction action) => action.State switch
    {
        _ when action.StatusText.Length > 0 => action.StatusText,
        InstallListingState.Installed when action.Managed.Length > 0 && RowButtonText(action).Length == 0 =>
            // The reason is the second line only when it fits the column whole; the drawer note
            // always carries it. The Status column is never cut with an ellipsis.
            action.ManagedWhy.Length > 0 && InstallColumns.Fits(action.ManagedWhy, InstallColumns.StatusWidth)
                ? action.Managed + "\n" + action.ManagedWhy
                : action.Managed,
        InstallListingState.Installed => InstallModsCopy.Installed,
        InstallListingState.Update => InstallModsCopy.Installed,
        InstallListingState.Pending => InstallModsCopy.StatusPendingLoad,
        InstallListingState.PendingUninstall => InstallModsCopy.StatusRemovedAtNextStart,
        InstallListingState.Tool => action.Label.Length > 0 ? action.Label : InstallModsCopy.ToolNotPlugin,
        InstallListingState.Loader => InstallModsCopy.LoaderInstalled,
        InstallListingState.Removed => InstallModsCopy.StatusRemovedStillLoaded,
        _ => "",
    };

    /// <summary>The status text's tone: green installed, yellow pending, orange pending-uninstall, muted managed elsewhere and tools.</summary>
    internal static CellTone RowStatusTone(InstallRowAction action) => action.State switch
    {
        InstallListingState.Installing when action.Remote => CellTone.Pending,
        InstallListingState.Sides => CellTone.Warn,
        _ when action.StatusText == SideStatusRules.ServerUnknown => CellTone.Muted,
        InstallListingState.None when action.StatusText.Length > 0 => CellTone.Warn,
        InstallListingState.Installed when action.Managed.Length > 0 && RowButtonText(action).Length == 0 => CellTone.Muted,
        InstallListingState.Pending => CellTone.Pending,
        InstallListingState.PendingUninstall => CellTone.Warn,
        InstallListingState.Removed => CellTone.Warn,
        InstallListingState.Tool => CellTone.Muted,
        _ => CellTone.Ok,
    };

    /// <summary>
    /// INSTALLUX2 item 2: the caption of the button beside the status word — <c>Uninstall</c>,
    /// <c>Reactivate</c>, <c>Cancel</c> — or the row's own caption where there is no status word
    /// (a not-installed row keeps its single Install button). Empty when the row offers nothing.
    /// </summary>
    /// <remarks>
    /// INSTALLCOLS1: every caption says what it does — <c>Cancel install</c>, <c>Cancel uninstall</c>
    /// (the ledgered pending uninstall's <c>Reactivate</c> too), never a bare <c>Cancel</c>. Tools,
    /// the loader and a removed row state their fact and draw no button.
    /// </remarks>
    internal static string RowButtonText(InstallRowAction action) => action.State switch
    {
        InstallListingState.Installing when action.Remote => action.Enabled ? InstallModsCopy.CancelRemote : "",
        InstallListingState.PendingUninstall when action.Remote => "",
        InstallListingState.Installed => action.Enabled ? InstallModsCopy.Uninstall : "",
        InstallListingState.Pending => action.Enabled ? InstallModsCopy.CancelInstallButton : "",
        InstallListingState.PendingUninstall => InstallModsCopy.CancelUninstall,
        InstallListingState.Tool => "",
        InstallListingState.Loader => "",
        // INSTORD1: the files are gone and the press falls through to the install flow, so the
        // button says what it does: install it again.
        InstallListingState.Removed => InstallModsCopy.Install,
        _ => action.Label,
    };

    /// <summary>
    /// MANAGED1: the label of a row that has no button because this manager did not install it
    /// (<see cref="InstallModsCopy.ManagedElsewhere"/>). INSTALLCOLS1 draws it as the Status column's
    /// text. Empty whenever the row draws a button, and on every other row.
    /// </summary>
    internal static string RowManagedText(InstallRowAction action) =>
        RowButtonText(action).Length == 0 ? action.Managed : "";

    /// <summary>The button's tone: the row's own for a plain Install, warn for the two that take a mod away.</summary>
    internal static CellTone RowButtonTone(InstallRowAction action) => action.State switch
    {
        InstallListingState.Installed => CellTone.Warn,
        InstallListingState.Pending => CellTone.Warn,
        InstallListingState.PendingUninstall => CellTone.Warn,
        _ => action.Tone,
    };

    /// <summary>
    /// INSTALLUX2 item 2 readback <c>listing.actions[]</c>: the buttons this row actually draws.
    /// A row whose only offer is its status word (an install managed elsewhere) lists none.
    /// </summary>
    internal static IReadOnlyList<string> RowActionNames(InstallRowAction action)
    {
        if (RowButtonText(action).Length == 0) return Array.Empty<string>();
        return action.State switch
        {
            InstallListingState.Installing when action.Remote => new[] { "cancel-remote" },
            InstallListingState.Sides => new[] { action.Label.Length > 0 ? action.Label.ToLowerInvariant().Replace(' ', '-') : "none" },
            InstallListingState.Installed => new[] { "uninstall" },
            InstallListingState.Pending => new[] { "cancel" },
            InstallListingState.PendingUninstall => new[] { action.Plain ? "cancel-removal" : "reactivate" },
            _ => new[] { "install" },
        };
    }

    /// <summary>What the preloader can do for a mod another tool installed (see <see cref="PlainOfferProbe"/>).</summary>
    internal sealed class PlainOffer
    {
        internal bool CanUninstall { get; init; }
        internal bool RemovalPending { get; init; }

        /// <summary>Why it cannot, in a few words ("Shares its folder with another mod"); empty when it can.</summary>
        internal string Why { get; init; } = "";

        /// <summary>A plain note when the last start could not carry out an uninstall; empty otherwise.</summary>
        internal string LastFailure { get; init; } = "";

        /// <summary>
        /// SRVUNINST1: the copy is a DLL in this client's <c>progenitor-reload</c> that no ledger lists (FWALLOW1):
        /// its Uninstall deletes that file now and the reloader unloads it, as on the Mods page.
        /// </summary>
        internal bool Reload { get; init; }
    }

    /// <summary>
    /// SETTINGS1 stage 3 seam: plugin GUID → what the preloader can do for it. The Mods menu sets it
    /// (<c>ModSwitch</c> and <c>PluginFileActions.Identify</c>); null keeps every such row "Managed elsewhere".
    /// </summary>
    internal static Func<string, PlainOffer?>? PlainOfferProbe { get; set; }

    /// <summary>The Uninstall / Cancel press on such a row: (plugin GUID, display name, cancel).</summary>
    internal static Action<string, string, bool>? PlainActionHandler { get; set; }

    /// <summary>
    /// SRVUNINST1: remove such a copy now, without a second confirmation — for a whole-row Uninstall
    /// whose own box already named this client: (plugin GUID, display name) → the sentence of what was
    /// done. A reload-folder copy is deleted (the reloader unloads it); an ordinary plugin gets the
    /// preloader's next-start request. Null: the client copy is left and the row says so.
    /// </summary>
    internal static Func<string, string, string>? PlainUninstallNow { get; set; }

    /// <summary>SRVUNINST1 review: plugin GUID → the sentence of what <see cref="PlainUninstallNow"/> does to that copy; empty when it cannot.</summary>
    internal static Func<string, string>? PlainUninstallSentence { get; set; }

    /// <summary>
    /// Chat stage 3 (mod links): a listing's Share control was pressed (its catalog key, a Thunderstore
    /// package id). The Mods menu sets it (<c>ModsMenu.ShareCatalog</c>); null draws no share control.
    /// </summary>
    internal static Action<string>? ShareInChat { get; set; }

    /// <summary>Whether a listing offers the share control: a share handler is set and its key is a package id.</summary>
    internal static bool CanShare(CatalogEntry entry) =>
        ShareInChat != null && entry != null && Progenitor.Chat.ModLinkToken.IsPackage(entry.Key);

    /// <summary>The row's hidden share cell: the quiet button when the listing can be shared, else a plain hidden cell.</summary>
    internal static CellSpec ShareRowCell(CatalogEntry entry)
    {
        if (CanShare(entry)) return ModsListLayout.ShareCell(CellShare, CellName);
        var plain = new CellSpec(CellKind.Text, CellShare).With(text: "");
        plain.OverlayOf = CellName;
        plain.Visible = false;
        return plain;
    }

    private static PlainOffer? PlainOfferFor(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || PlainOfferProbe == null) return null;
        try { return PlainOfferProbe(pluginId); }
        catch { return null; }
    }

    /// <summary>
    /// The row spec of one listing (INSTALLSIDES1, Option A): the expand button, the icon, the Mod
    /// column (name over the author, the update note leading the second line, the quiet
    /// <c>Install on both</c> / <c>Update both</c> shortcut over its right end), Side, the Client and
    /// Server cells (<see cref="SideCells"/>: each side's words and its own buttons), Released and
    /// Downloads. Widths come from the column model, so the header and every row share them.
    /// </summary>
    internal List<CellSpec> RowCells(CatalogEntry entry)
    {
        var sides = SideCellsFor(entry);
        var side = SideOf(entry);
        bool open = string.Equals(_state.DrawerKey, entry.Key, StringComparison.OrdinalIgnoreCase);
        bool leaving = sides.Client.Kind == SideCells.KindRemovalPending;

        // INSTALLSIDES1: the expand button is the only control that opens or closes the drawer.
        var expand = new CellSpec(CellKind.Button, CellExpand).With(
            text: open ? InstallModsCopy.GlyphCollapse : InstallModsCopy.GlyphExpand,
            skin: CellSkin.Expander,
            align: CellAlign.Center);
        var name = new CellSpec(CellKind.Text, CellName).With(
            text: RowNameText(entry),
            wrap: CellWrap.TwoLine,
            tone: leaving ? CellTone.Warn : CellTone.Normal);
        name.NoteLead = UpdateNoteText(entry, CurrentUpdateTag);
        name.NoteLeadTone = CellTone.Warn;

        // The quiet shortcut sits over the right end of the Mod column, on the author line.
        string shortcut = SideCells.ShortcutCaption(sides.Shortcut);
        var both = new CellSpec(shortcut.Length > 0 ? CellKind.Button : CellKind.Text, CellBoth).With(
            text: shortcut,
            width: CellWidth.Px(InstallColumns.ShortcutWidth),
            minWidth: InstallColumns.ShortcutWidth,
            fontSize: InstallColumns.ShortcutFontSize,
            vAlign: CellVAlign.Bottom,
            skin: CellSkin.Quiet,
            align: CellAlign.Right);
        both.OverlayOf = CellName;
        both.HeightOverride = InstallColumns.ShortcutHeight;
        both.Visible = shortcut.Length > 0;
        if (both.Visible) name.NoteReserve = InstallColumns.ShortcutWidth + 4f;

        var cells = new List<CellSpec>
        {
            expand,
            new CellSpec(CellKind.Image, CellIcon).With(skin: CellSkin.IconWell),
            name,
            both,
            ShareRowCell(entry),
        };
        // INSTALLSIDES2 item 2: no Side column; a side the mod does not use is a grayed cell.
        float sideWidth = SideColumnWidthNow();
        AddSideCells(cells, sides.Client, CellClient, CellClientA, CellClientB, CellColumn.Client, sideWidth);
        AddSideCells(cells, sides.Server, CellServer, CellServerA, CellServerB, CellColumn.Server, sideWidth, AdminOfferOf(sides.Server));
        cells.Add(new CellSpec(CellKind.Muted, CellReleased).With(text: ReleasedShortText(entry)));
        // VANILLA1: figures are secondary text — the game's resting gray, never brighter than the name.
        cells.Add(new CellSpec(CellKind.Muted, CellDownloads).With(text: InstallColumns.CompactCount(entry.Downloads), tone: CellTone.Muted));
        string sorted = InstallColumns.CellOfSort(_state.Filters.Sort);
        for (int i = 0; i < cells.Count; i++) InstallColumns.Shape(cells[i], sorted);
        return cells;
    }

    /// <summary>The side columns' width on this pane's row (<see cref="InstallColumns.SideColumnWidthFor"/>).</summary>
    internal float SideColumnWidthNow() => InstallColumns.SideColumnWidthFor(ColumnRowWidth());

    /// <summary>The admin offer a server cell's lock button stands for (its caption while authenticating).</summary>
    private AdminOffer AdminOfferOf(SideCell server) =>
        server.Has(SideButton.Authenticate) || server.Has(SideButton.AdminOnly) ? AdminOfferNow() : AdminOffer.None;

    /// <summary>
    /// One side's cells: the state words (broken into at most two lines, after the state's glyph) and
    /// its two button slots, all in the side's column (INSTALLSIDES2: the band and the button tint).
    /// </summary>
    private static void AddSideCells(
        List<CellSpec> cells, SideCell cell, string textKey, string aKey, string bKey, CellColumn column,
        float columnWidth, AdminOffer admin = AdminOffer.None)
    {
        var a = cell.Buttons.Count > 0 ? cell.Buttons[0] : SideButton.None;
        var b = cell.Buttons.Count > 1 ? cell.Buttons[1] : SideButton.None;
        string captionA = SideButtonCaption(a, admin);
        string captionB = SideButtonCaption(b, admin);
        float width = SideTextWidthFor(columnWidth, captionA, a, captionB, b);
        var text = new CellSpec(CellKind.Muted, textKey).With(
            text: InstallColumns.StatusLines(cell.Text, width - GlyphRoom(SideGlyph(cell)), InstallColumns.SideTextFontSize),
            // A grayed side recedes: its words dimmer than the muted "Not installed".
            tone: cell.Grayed ? CellTone.Faint : SideTone(cell.Tone),
            width: CellWidth.Px(width),
            minWidth: width,
            fontSize: InstallColumns.SideTextFontSize);
        text.Column = column;
        text.Grayed = cell.Grayed;
        text.Glyph = SideGlyph(cell);
        cells.Add(text);
        cells.Add(SideButtonCell(aKey, captionA, a, column, admin));
        cells.Add(SideButtonCell(bKey, captionB, b, column, admin));
    }

    /// <summary>
    /// INSTALLSIDES2 live review 2: the mark before a side's words. <c>↑</c> means an update only; a
    /// missing half (a both-sided mod one side lacks) is <c>!</c> (WGL4) in the same amber, so the two
    /// cannot be confused. Installed keeps <c>✓</c>.
    /// </summary>
    internal static string SideGlyph(SideCell cell) =>
        cell.Kind == SideCells.KindMissing ? MissingGlyph : cell.Glyph;

    /// <summary>The missing-half mark (U+0021).</summary>
    public const string MissingGlyph = "!";

    /// <summary>Room the state glyph and its space take before the words.</summary>
    private static float GlyphRoom(string glyph) =>
        string.IsNullOrEmpty(glyph) ? 0f : CellLayout.EstimateTextWidth(glyph + " ", InstallColumns.SideTextFontSize);

    /// <summary>
    /// REMOTEFOLLOW1: one side button's laid-out width, the lock's room included. The state words get
    /// the column minus exactly these widths. Live, the Admin only button added the lock glyph's room
    /// but the words' width did not subtract it, so a row with that button was ~25 px wider than its
    /// column and the Mod cell shrank: CastleLink's Client and Server cells sat left of every other row.
    /// </summary>
    internal static float SideButtonCellWidth(string caption, SideButton button)
    {
        if (string.IsNullOrEmpty(caption)) return InstallColumns.SideButtonMinWidth;
        float width = InstallColumns.SideButtonWidth(caption);
        if (button == SideButton.AdminOnly) width += GlyphRoom(SideCells.LockGlyph);
        return width;
    }

    /// <summary>The state words' width in a side column of <paramref name="column"/> px with these buttons shown.</summary>
    internal static float SideTextWidthFor(float column, string captionA, SideButton a, string captionB, SideButton b)
    {
        float width = column;
        if (!string.IsNullOrEmpty(captionA)) width -= SideButtonCellWidth(captionA, a) + CellLayout.Gap;
        if (!string.IsNullOrEmpty(captionB)) width -= SideButtonCellWidth(captionB, b) + CellLayout.Gap;
        return Math.Max(40f, width);
    }

    /// <summary>A side button's caption; the lock reads <c>Authenticating…</c> while the flow runs.</summary>
    internal static string SideButtonCaption(SideButton button, AdminOffer admin = AdminOffer.None) =>
        button == SideButton.Authenticate && admin == AdminOffer.Authenticating
            ? SideCells.AuthenticatingCaption
            : SideCells.Caption(button);

    /// <summary>
    /// A side button: the game's popup button at its caption's width, tinted to its column, or — with
    /// nothing to offer — a hidden plain cell under the same key, so nothing can press a button the
    /// player cannot see. <c>Admin only</c> (and <c>Authenticating…</c>) is disabled and dim; the lock
    /// glyph before it is drawn only when the font has it.
    /// </summary>
    private static CellSpec SideButtonCell(string key, string caption, SideButton button, CellColumn column, AdminOffer admin = AdminOffer.None)
    {
        bool shown = caption.Length > 0;
        bool locked = button == SideButton.AdminOnly
            || (button == SideButton.Authenticate && admin == AdminOffer.Authenticating);
        float width = SideButtonCellWidth(caption, button);
        var spec = new CellSpec(shown ? CellKind.Button : CellKind.Text, key).With(
            text: caption,
            width: CellWidth.Px(width),
            minWidth: width,
            fontSize: InstallColumns.SideButtonFontSize,
            skin: CellSkin.PopupButton,
            tone: locked ? CellTone.Disabled : SideButtonTone(button));
        spec.Column = column;
        if (button == SideButton.AdminOnly) spec.Glyph = SideCells.LockGlyph;
        spec.Visible = shown;
        return spec;
    }

    /// <summary>
    /// The state words' tone in the kit's cell tones (INSTALLSIDES2 item 1): light green installed,
    /// amber an update or a missing half, pale blue waiting on a restart, muted not installed.
    /// </summary>
    internal static CellTone SideTone(SideCellTone tone) => tone switch
    {
        SideCellTone.Muted => CellTone.Muted,
        SideCellTone.Ok => CellTone.OkBright,
        SideCellTone.Warn => CellTone.Amber,
        SideCellTone.Pending => CellTone.Info,
        _ => CellTone.Normal,
    };

    /// <summary>A side button's tone: the game's light caption; the popup button draws no color by tone but red for Danger.</summary>
    internal static CellTone SideButtonTone(SideButton button) => button switch
    {
        SideButton.Install or SideButton.Update => CellTone.Gold,
        SideButton.AdminOnly => CellTone.Disabled,
        _ => CellTone.Normal,
    };

    /// <summary>
    /// The header cells: one per column, the same keys and widths as <see cref="RowCells"/> (a side
    /// column's header spans its text and both button slots). Mod, Client (install date), Released
    /// and Downloads sort; Side and Server do not. The Server caption names the picked server.
    /// </summary>
    internal List<CellSpec> HeaderCells()
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellExpand).With(text: "", align: CellAlign.Center),
            new CellSpec(CellKind.Text, CellIcon).With(text: ""),
            new CellSpec(CellKind.Text, CellName).With(text: InstallModsCopy.HeaderMod, sortKey: SortKeyName),
            new CellSpec(CellKind.Text, CellClient).With(text: InstallModsCopy.HeaderClient, sortKey: SortKeyInstalled,
                width: CellWidth.Px(SideColumnWidthNow()), minWidth: SideColumnWidthNow()),
            new CellSpec(CellKind.Text, CellServer).With(text: ServerHeaderCaption(),
                width: CellWidth.Px(SideColumnWidthNow()), minWidth: SideColumnWidthNow()),
            new CellSpec(CellKind.Text, CellReleased).With(text: InstallModsCopy.HeaderReleased, sortKey: SortKeyReleased),
            new CellSpec(CellKind.Text, CellDownloads).With(text: InstallModsCopy.HeaderDownloads, sortKey: SortKeyDownloads),
        };
        string sorted = InstallColumns.CellOfSort(_state.Filters.Sort);
        for (int i = 0; i < cells.Count; i++) InstallColumns.Shape(cells[i], sorted);
        // INSTALLSIDES2 item 1: the side headers carry their column's band and rule.
        // VANILLA1: every caption in its own case, in small caps, at the header size.
        foreach (var cell in cells)
        {
            if (cell.Key == CellClient) cell.Column = CellColumn.Client;
            else if (cell.Key == CellServer) cell.Column = CellColumn.Server;
            cell.SmallCaps = true;
            cell.FontSizeOverride = InstallColumns.HeaderFontSize;
        }
        return cells;
    }

    /// <summary>
    /// VANILLA1: listing rows and their drawer use the Mods list's soft row — a faint light hover wash
    /// and a soft gold selected fill — rather than the kit's near-black hover and solid selected fill,
    /// so the two list tabs of the workspace answer the pointer the same way.
    /// </summary>
    internal const bool ListRowsSoft = true;

    /// <summary>The server picker's dropdown bar width on the toolbar.</summary>
    public const float PickerWidth = 360f;

    /// <summary>The <c>Server</c> label before the picker.</summary>
    public const float PickerLabelWidth = 64f;

    /// <summary>
    /// The toolbar cells (LIST3 defects 2–4): the search field at the left and the count as plain
    /// text. INSTALLSIDES1: the server picker at the right — the <c>Server</c> label and the game's
    /// dropdown bar naming the picked server — whenever the Mods menu wired a picker.
    /// </summary>
    internal List<CellSpec> ToolbarCells()
    {
        // INSTALLSIMPLE1: the Simple | Advanced switch at the left of the search row (when the view is remembered).
        var cells = ViewSwitchShown ? ViewSwitchCells() : new List<CellSpec>();
        cells.AddRange(new List<CellSpec>
        {
            // SEARCHCLEAR1: a search box: the clear button while it has text; Escape clears, then leaves it.
            new CellSpec(CellKind.Input, CellSearch) { Clearable = true }.With(text: _search, placeholder: InstallModsCopy.SearchPlaceholder, width: CellWidth.Px(SearchCellWidth), minWidth: CellLayout.MinInputWidth, skin: CellSkin.ValueBox),
            // VANILLA1: the count is body text in the game's resting gray, quieter than the field.
            new CellSpec(CellKind.Text, CellCount).With(text: CountText(), minWidth: 120f, tone: CellTone.Muted),
            new CellSpec(CellKind.Spacer, CellGap),
        });
        // JOINADMIN1: "Players joining" is no longer here; it is a row on Progenitor's page (Server settings).
        // Settings organization stage 8: Simple's link to those Server settings, for an admin acting on the server.
        if (ServerSettingsLinkShown) cells.Add(ServerSettingsLinkCell());

        // INSTALLSIMPLE1: Simple has no server picker.
        var picked = IsSimple ? null : PickedServer;
        if (picked != null)
        {
            cells.Add(new CellSpec(CellKind.Muted, CellServerLabel).With(text: InstallModsCopy.ServerPickerLabel,
                width: CellWidth.Px(PickerLabelWidth), minWidth: PickerLabelWidth, align: CellAlign.Right));
            cells.Add(new CellSpec(CellKind.Button, CellServerPick).With(text: picked.Caption,
                width: CellWidth.Px(PickerWidth), minWidth: PickerWidth, align: CellAlign.Right, skin: CellSkin.Dropdown));
        }

        return cells;
    }

    /// <summary>Floor a flexible chip shrinks to on a narrow row (its caption then ellipsizes).</summary>
    public const float ChipMinWidth = 110f;

    /// <summary>
    /// Slack added to every chip's estimated caption width (LIST3 defect 1): the mean-glyph
    /// estimate under-measures a short capitalized word (<c>Any</c>, <c>Both</c> drew
    /// <c>A…</c> / <c>Bo…</c> live while <c>Server</c> / <c>Client</c> fit), so one glyph's
    /// worth of room is kept on top of the button padding.
    /// </summary>
    public static readonly float ChipCaptionSlack = MenuPanelLayout.HeaderButtonFontSize * MenuPanelLayout.AverageGlyphEm;

    /// <summary>
    /// One fixed width for the four side chips: the widest side caption (<c>Server</c>,
    /// which fit live) plus the slack, so <c>Any</c> and <c>Both</c> can never be narrower
    /// than a caption that is known to fit.
    /// </summary>
    public static readonly float SideChipWidth = ChipNaturalWidth(WidestSideCaption());

    private static string WidestSideCaption()
    {
        string widest = InstallSearchFilters.AnyCaption;
        foreach (string caption in new[] { InstallSearchFilters.ServerCaption, InstallSearchFilters.ClientCaption, InstallSearchFilters.BothCaption })
        {
            if (caption.Length > widest.Length) widest = caption;
        }
        return widest;
    }

    /// <summary>The width a chip asks for: its caption estimate, the button padding and <see cref="ChipCaptionSlack"/>.</summary>
    internal static float ChipNaturalWidth(string caption) =>
        CellLayout.EstimateTextWidth(caption, MenuPanelLayout.HeaderButtonFontSize) + CellLayout.ButtonPad + ChipCaptionSlack;

    /// <summary>
    /// A chip: a button cell sized to its caption. A short chip is fixed at that width; a
    /// <paramref name="flexible"/> one (the long captions) asks for it and shrinks towards
    /// <see cref="ChipMinWidth"/> when the row cannot hold everything. <paramref name="width"/>
    /// fixes the chip at a shared width instead (the side chips).
    /// </summary>
    internal static CellSpec Chip(string key, string caption, bool on, CellAlign align = CellAlign.Left, bool flexible = false, float? width = null)
    {
        // INSTALLFILTERS1: selected is unmistakable — the filled gold face; unselected the quiet dark face.
        // VANILLA1: drawn on the game's popup button sprite (CellSkin.Chip); the kit chip without it.
        var spec = new CellSpec(CellKind.Button, key).With(text: caption, tone: on ? CellTone.ChipOn : CellTone.ChipOff, align: align, skin: CellSkin.Chip);
        float natural = Math.Max(ChipNaturalWidth(caption), width ?? 0f);
        if (flexible && width == null) spec.With(width: CellWidth.Auto, minWidth: Math.Min(natural, ChipMinWidth));
        else spec.With(width: CellWidth.Px(natural), minWidth: natural);
        return spec;
    }

    /// <summary>
    /// INSTALLFILTERS1 (user, 2026-10-03: "make the filters up top readable, and add a filter for
    /// missing … and needs update"): the first filter row — the <c>Side</c> group (Any · Client ·
    /// Server · Both) and the <c>Show</c> group (All · Installed · Missing a side · Needs update, each
    /// with its match count), single choice each. A selected chip is the filled gold face; the two
    /// attention chips take an amber rim while they have matches.
    /// </summary>
    internal List<CellSpec> SideShowCells()
    {
        var cells = SideGroupCells();
        cells.Add(new CellSpec(CellKind.Spacer, CellGroupGap).With(width: CellWidth.Px(GroupGap), minWidth: GroupGap));
        cells.AddRange(ShowGroupCells());
        return cells;
    }

    /// <summary>Every filter cell of every group, in bar order (Side, Show, Only, the summary, Clear).</summary>
    internal List<CellSpec> FilterCells()
    {
        var cells = SideShowCells();
        cells.AddRange(OnlyCells());
        return cells;
    }

    /// <summary>The Side group: its caption and the four single-choice chips.</summary>
    internal List<CellSpec> SideGroupCells()
    {
        var f = _state.Filters;
        return new List<CellSpec>
        {
            Caption(CellSideCaption, InstallModsCopy.SideCaption),
            Chip(CellSideAny, InstallSearchFilters.AnyCaption, f.Side == InstallSideFilter.Any, width: SideChipWidth),
            Chip(CellSideClient, InstallSearchFilters.ClientCaption, f.Side == InstallSideFilter.Client, width: SideChipWidth),
            Chip(CellSideServer, InstallSearchFilters.ServerCaption, f.Side == InstallSideFilter.Server, width: SideChipWidth),
            Chip(CellSideBoth, InstallSearchFilters.BothCaption, f.Side == InstallSideFilter.Both, width: SideChipWidth),
        };
    }

    /// <summary>The Show group: its caption and the four single-choice chips with their counts.</summary>
    internal List<CellSpec> ShowGroupCells()
    {
        var counts = ShowCounts();
        return new List<CellSpec>
        {
            Caption(CellShowCaption, InstallModsCopy.ShowCaption),
            ShowChip(CellShowAll, InstallModsCopy.ShowAll, InstallShowFilter.All, counts),
            ShowChip(CellShowInstalled, InstallModsCopy.ShowInstalled, InstallShowFilter.Installed, counts),
            ShowChip(CellShowMissing, InstallModsCopy.ShowMissing, InstallShowFilter.MissingSide, counts),
            ShowChip(CellShowUpdate, InstallModsCopy.ShowUpdate, InstallShowFilter.NeedsUpdate, counts),
        };
    }

    /// <summary>
    /// The filter rows for a <paramref name="rowWidth"/> px row, in order: <c>filters</c> (Side and
    /// Show side by side when they fit, else Side alone and <c>filters-show</c> under it) and
    /// <c>filters-only</c>. A group is never split, so it wraps whole to its own line; nothing
    /// shares a line with the search and picker row.
    /// </summary>
    internal List<(string Key, List<CellSpec> Cells)> FilterRowsFor(float rowWidth)
    {
        var rows = new List<(string Key, List<CellSpec> Cells)>(3);
        var combined = SideShowCells();
        if (NaturalWidth(combined) <= rowWidth) rows.Add((FiltersKey, combined));
        else
        {
            rows.Add((FiltersKey, SideGroupCells()));
            rows.Add((FiltersShowKey, ShowGroupCells()));
        }
        rows.Add((FiltersOnlyKey, OnlyCells()));
        return rows;
    }

    /// <summary>A row's width at its cells' asks: fixed widths, the floors of the flexible ones, and the gaps.</summary>
    internal static float NaturalWidth(IReadOnlyList<CellSpec> cells)
    {
        float total = 0f;
        int shown = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (!c.Visible) continue;
            float w = c.Width.Mode == CellWidthMode.Px ? Math.Max(c.Width.Value, c.MinWidth) : c.MinWidth;
            total += w;
            shown++;
        }
        return total + Math.Max(0, shown - 1) * CellLayout.Gap;
    }

    /// <summary>
    /// INSTALLFILTERS1: the second filter row — the <c>Only</c> group (No restart needed · Current
    /// game update), independent toggles; the one-line summary of what the list shows; and
    /// <c>Clear filters</c> (off the defaults only). Two rows always, so the groups never crowd the
    /// search and picker row above them at any width.
    /// </summary>
    internal List<CellSpec> OnlyCells()
    {
        var f = _state.Filters;
        var cells = new List<CellSpec>
        {
            Caption(CellOnlyCaption, InstallModsCopy.OnlyCaption),
            Chip(CellNoRestart, InstallModsCopy.NoRestartChip, f.NoRestartOnly, flexible: true),
            Chip(CellCurrent, InstallModsCopy.CurrentUpdateChipCaption(CurrentUpdateTag), f.CurrentUpdateOnly && CurrentUpdateTag != null, flexible: true),
            new CellSpec(CellKind.Muted, CellSummary).With(text: SummaryText(), width: CellWidth.Fill(120f), minWidth: 120f, optional: true),
            Chip(CellClear, InstallModsCopy.ClearFilters, false, CellAlign.Right),
        };
        cells[cells.Count - 1].Visible = !AtDefaults;
        return cells;
    }

    /// <summary>The space between two filter groups.</summary>
    public const float GroupGap = 18f;

    private static CellSpec Caption(string key, string text)
    {
        float width = CellLayout.EstimateTextWidth(text, MenuPanelLayout.HeaderButtonFontSize) + 12f;
        return new CellSpec(CellKind.Muted, key).With(text: text, width: CellWidth.Px(width), minWidth: width, optional: true);
    }

    /// <summary>A Show chip: its caption and count, reserved wide enough for a three-digit count.</summary>
    private CellSpec ShowChip(string key, string caption, InstallShowFilter show, IReadOnlyDictionary<InstallShowFilter, int> counts)
    {
        int count = counts.TryGetValue(show, out int n) ? n : 0;
        var spec = Chip(key, InstallModsCopy.ShowChipCaption(caption, count), _state.Filters.Show == show,
            width: ChipNaturalWidth(InstallModsCopy.ShowChipCaption(caption, 999)));
        spec.Tone = ShowChipTone(_state.Filters.Show == show, show, count);
        return spec;
    }

    /// <summary>
    /// A Show chip's look: selected — the filled gold face; an attention chip (Missing a side, Needs
    /// update) with matches — the amber rim; otherwise the quiet dark face.
    /// </summary>
    internal static CellTone ShowChipTone(bool on, InstallShowFilter show, int count) =>
        on ? CellTone.ChipOn
        : count > 0 && show is InstallShowFilter.MissingSide or InstallShowFilter.NeedsUpdate ? CellTone.ChipAttention
        : CellTone.ChipOff;

    // ---- INSTALLFILTERS1: the Show predicates and counts -------------------------------------

    /// <summary>
    /// Does one listing's pair of side cells pass <paramref name="show"/>? The same decision the row
    /// draws (<see cref="SideCells.Decide"/>), so the filter and the row cannot disagree:
    /// <list type="bullet">
    /// <item><c>Installed</c>: either cell holds a copy — installed, behind the listing, older than
    /// the other side, waiting on a restart or leaving at one.</item>
    /// <item><c>Missing a side</c>: either cell is <c>Missing</c> (<see cref="SideCells.KindMissing"/>,
    /// drawn <c>! Missing: the server has it</c>): a manifest-verified both-sided mod one side has and
    /// the other lacks. A side the mod does not use, None, and Private games never are.</item>
    /// <item><c>Needs update</c>: either cell is behind the listing (<see cref="SideCells.KindUpdate"/>)
    /// or older than the other side (<see cref="SideCells.KindOlder"/>), drawn with <c>↑</c>.</item>
    /// </list>
    /// </summary>
    internal static bool ShowPredicate(SideCellsDecision sides, InstallShowFilter show)
    {
        if (sides == null) return show == InstallShowFilter.All;
        return show switch
        {
            InstallShowFilter.Installed => HasCopy(sides.Client) || HasCopy(sides.Server),
            InstallShowFilter.MissingSide => sides.Client.Kind == SideCells.KindMissing || sides.Server.Kind == SideCells.KindMissing,
            InstallShowFilter.NeedsUpdate => NeedsUpdate(sides.Client) || NeedsUpdate(sides.Server),
            _ => true,
        };
    }

    private static bool HasCopy(SideCell cell) =>
        cell.Kind is SideCells.KindInstalled or SideCells.KindUpdate or SideCells.KindOlder
            or SideCells.KindPending or SideCells.KindRemovalPending;

    private static bool NeedsUpdate(SideCell cell) => cell.Kind is SideCells.KindUpdate or SideCells.KindOlder;

    /// <summary>Does the listing pass the Show filter that is on (All passes everything without reading its cells)?</summary>
    internal bool ShowMatches(CatalogEntry entry, InstallShowFilter show)
    {
        if (show == InstallShowFilter.All || entry == null) return true;
        try { return ShowPredicate(SideCellsCached(entry), show); }
        catch { return false; }
    }

    /// <summary>
    /// The Show chips' counts: how many listings each choice would show, with the search, the Side
    /// group and the Only toggles as they are (the Show choice itself aside). Recomputed whenever the
    /// filter row is drawn or synced — after a search, a filter click, an install, a picker change
    /// or an index refresh.
    /// </summary>
    internal IReadOnlyDictionary<InstallShowFilter, int> ShowCounts()
    {
        var counts = new Dictionary<InstallShowFilter, int>
        {
            [InstallShowFilter.All] = 0,
            [InstallShowFilter.Installed] = 0,
            [InstallShowFilter.MissingSide] = 0,
            [InstallShowFilter.NeedsUpdate] = 0,
        };
        long countsStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var entries = AllEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!VisibleExceptShow(entry)) continue;
                counts[InstallShowFilter.All]++;
                SideCellsDecision sides;
                try { sides = SideCellsCached(entry); }
                catch { continue; }
                if (ShowPredicate(sides, InstallShowFilter.Installed)) counts[InstallShowFilter.Installed]++;
                if (ShowPredicate(sides, InstallShowFilter.MissingSide)) counts[InstallShowFilter.MissingSide]++;
                if (ShowPredicate(sides, InstallShowFilter.NeedsUpdate)) counts[InstallShowFilter.NeedsUpdate]++;
            }
        }
        finally
        {
            _lastCountsMs = (System.Diagnostics.Stopwatch.GetTimestamp() - countsStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        _lastShowCounts = counts;
        return counts;
    }

    private IReadOnlyDictionary<InstallShowFilter, int>? _lastShowCounts;
    private double _lastCountsMs;

    /// <summary>
    /// LIMITS1 (typing lag): the per-side decisions the Show filter and its counts read, kept across
    /// re-filters. Live, each keystroke spent 114–191 ms in the input handler, re-deciding both side
    /// cells of every listing (installed-state, ledger and route reads) for the counts — and none of
    /// that changes while the player types. Dropped whenever it can change: a build, a new install
    /// scan, an install action on a row, a remote-install update, a picker change.
    /// </summary>
    private readonly Dictionary<string, SideCellsDecision> _sideCache = new(StringComparer.OrdinalIgnoreCase);

    internal SideCellsDecision SideCellsCached(CatalogEntry entry)
    {
        if (_sideMemo != null) return SideCellsFor(entry);
        if (_sideCache.TryGetValue(entry.Key, out var known)) return known;
        var decided = SideCellsFor(entry);
        _sideCache[entry.Key] = decided;
        return decided;
    }

    /// <summary>Forget the cached per-side decisions (see <see cref="SideCellsCached"/>).</summary>
    internal void InvalidateSideCache() => _sideCache.Clear();

    /// <summary>Tests and the readback: how many decisions are cached.</summary>
    internal int SideCacheCount => _sideCache.Count;

    /// <summary>Everything <see cref="IsVisible"/> asks but the Show choice.</summary>
    private bool VisibleExceptShow(CatalogEntry entry) =>
        entry != null
        && !IsHidden(entry)
        && _state.Filters.Matches(entry, CurrentUpdateTag)
        && SearchRank(entry, _search, ReadmeTextFor(entry)) > 0;

    public void SetShowFilter(InstallShowFilter show)
    {
        if (_state.Filters.Show == show) return;
        _state.Filters.Show = show;
        RefilterListings();
    }

    /// <summary>
    /// The one-line summary beside the Only group: <c>Showing 12 · missing a side · client mods ·
    /// current game update only</c> — the count shown, then each filter that narrows the list.
    /// </summary>
    internal string SummaryText()
    {
        var f = _state.Filters;
        var parts = new List<string> { InstallModsCopy.SummaryShowing + " " + _lastShown.ToString(CultureInfo.InvariantCulture) };
        switch (f.Show)
        {
            case InstallShowFilter.Installed: parts.Add(InstallModsCopy.SummaryInstalled); break;
            case InstallShowFilter.MissingSide: parts.Add(InstallModsCopy.SummaryMissing); break;
            case InstallShowFilter.NeedsUpdate: parts.Add(InstallModsCopy.SummaryUpdate); break;
        }
        switch (f.Side)
        {
            case InstallSideFilter.Client: parts.Add(InstallModsCopy.SummaryClient); break;
            case InstallSideFilter.Server: parts.Add(InstallModsCopy.SummaryServer); break;
            case InstallSideFilter.Both: parts.Add(InstallModsCopy.SummaryBoth); break;
        }
        if (f.NoRestartOnly) parts.Add(InstallModsCopy.SummaryNoRestart);
        if (f.CurrentUpdateOnly && CurrentUpdateTag != null) parts.Add(InstallModsCopy.SummaryCurrent);
        if (!string.IsNullOrWhiteSpace(_search)) parts.Add(InstallModsCopy.SummaryMatching + " \u201c" + _search.Trim() + "\u201d");
        return string.Join(SideCells.Dot, parts);
    }

    /// <summary>
    /// Build the whole tab into <paramref name="panel"/>: the pinned search field, the
    /// toolbar, the filter row and the column header, then one row per listing (hidden when
    /// filtered out) with the drawer under the opened one, and the pending bar plus the
    /// footer at the bottom. Nothing here fetches on its own except the READMEs of the rows on
    /// screen (<see cref="ResumePrefetch"/>, at most <see cref="ReadmePrefetchBudget"/>) and the visible icons.
    /// </summary>
    public void Build(
        IUiPanel panel,
        bool thunderstoreEnabled,
        bool nexusEnabled,
        LocalServerFolder? server,
        Action refreshIndex,
        Action closeMods,
        DateTime utcNow)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (refreshIndex == null) throw new ArgumentNullException(nameof(refreshIndex));
        if (closeMods == null) throw new ArgumentNullException(nameof(closeMods));
        _ = utcNow;
        ResetHandles();
        InvalidateSideCache();
        _panel = panel;
        _server = server;
        _currentUpdate = null;
        // INSTALLSIMPLE1: the remembered view is read once per build; the library set follows the index.
        _viewRead = null;
        _dependedOn = null;
        // POLISH1 defect C: before a single row is read, bring both install ledgers back in step with
        // what the processes did to the files — the local server restarting and promoting is the one
        // change this session never hears about otherwise.
        RecheckLedgers();
        // One installed-state read per listing per build. The column-width pass, the rows and the
        // pending bar each ask for every listing, and nothing they read changes inside a build.
        BeginBuildMemo();
        try
        {
            panel.ContentAlignment = LayoutAlignment;

            // LIST3 item 9: no footer (Escape and the native tabs close the workspace; the index
            // refreshes itself on open). closeMods stays on the signature for the menu's wiring.
            _ = closeMods;
            // INSTALLERR1: the mod list's state in the game's look (a centered block for a failure, a notice band over a list).
            _refreshIndex = refreshIndex;
            var indexView = IndexView(thunderstoreEnabled, nexusEnabled, utcNow);
            _lastIndexView = indexView;
            var surface = indexView.Surface;
            if (surface != InstallPaneSurface.Ready)
            {
                Ui.PinMenuRows(panel, MenuRowPin.Content);
                BuildIndexSurface(panel, indexView);
                return;
            }

            // Toolbar (with the search field at its left), chips and the column header stay above the scrolling list.
            Ui.PinMenuRows(panel, MenuRowPin.Top);
            _toolbar = panel.CellRow(new CellRowSpec(ToolbarKey, ToolbarCells()));
            _toolbar.OnChanged(CellSearch, text =>
            {
                // LIMITS1 (typing lag): the field echoes the character itself; the list re-filters
                // once typing pauses (SearchDebounceSeconds), not on every keystroke.
                _search = text ?? "";
                // SEARCHCLEAR1: an emptied box (the clear button, the last Backspace) shows every row at
                // once; only typing waits for the pause.
                if (_search.Length == 0) RefilterSearchNow();
                else ScheduleSearchRefilter();
            });
            // INSTALLSIDES1: the server picker's dropdown bar opens the game's popup list.
            _toolbar.OnClick(CellServerPick, () => OpenServerPicker());
            // INSTALLSIMPLE1: the view switch.
            _toolbar.OnClick(CellViewSimple, () => SetView(InstallView.Simple));
            _toolbar.OnClick(CellViewAdvanced, () => SetView(InstallView.Advanced));
            _toolbar.OnClick(CellServerSettings, () => PressServerSettings());
            // INSTALLFILTERS1: the filter groups, wrapped whole onto as many rows as the width needs.
            _filterRows.Clear();
            if (IsSimple) BuildSimpleFilters(panel);
            else foreach (var (rowKey, rowCells) in FilterRowsFor(ColumnRowWidth()))
            {
                var row = panel.CellRow(new CellRowSpec(rowKey, rowCells));
                _filterRows.Add(row);
                if (rowKey == FiltersKey) _filters = row;
                else if (rowKey == FiltersOnlyKey) _filtersOnly = row;
                foreach (var cell in rowCells) _filterRowOf[cell.Key] = row;
            }
            if (!IsSimple)
            {
                OnFilterClick(CellSideAny, () => SetSideFilter(InstallSideFilter.Any));
                OnFilterClick(CellSideServer, () => SetSideFilter(InstallSideFilter.Server));
                OnFilterClick(CellSideClient, () => SetSideFilter(InstallSideFilter.Client));
                OnFilterClick(CellSideBoth, () => SetSideFilter(InstallSideFilter.Both));
                OnFilterClick(CellShowAll, () => SetShowFilter(InstallShowFilter.All));
                OnFilterClick(CellShowInstalled, () => SetShowFilter(InstallShowFilter.Installed));
                OnFilterClick(CellShowMissing, () => SetShowFilter(InstallShowFilter.MissingSide));
                OnFilterClick(CellShowUpdate, () => SetShowFilter(InstallShowFilter.NeedsUpdate));
                OnFilterClick(CellNoRestart, ToggleNoRestartOnly);
                OnFilterClick(CellCurrent, ToggleCurrentUpdateOnly);
                OnFilterClick(CellClear, ClearFilters);
            }
            // INSTALLERR1: the small muted source and age line (the local stand-in, the fallback, "Updated 2 hours ago").
            BuildIndexStatus(panel, indexView);
            // INSTALLRESTART1: the pending-restart bar, above the column header (Simple and Advanced).
            BuildRestartBand(panel);
            // AUTOUPDATE1: the update list (band plus up to four rows), under the restart bar.
            BuildUpdateRows(panel);
            // INSTALLUX1 item 4: the header's Install column has to end where the rows' Install
            // buttons do, one scrollbar band inside the row edge.
            _header = IsSimple
                ? panel.CellHeader(SimpleHeaderCells(), HeaderKey, HeadBlockLayout.RightColumnInset)
                : panel.CellHeader(HeaderCells(), HeaderKey, HeadBlockLayout.RightColumnInset);
            MarkHeaderSorted();
            _header.OnSort(key =>
            {
                if (IsSimple) SimpleHeaderSort(key);
                else if (TryParseSortKey(key, out var sort)) HeaderSort(sort);
            });

            // The list: every listing has a row (visibility follows the filters), in sort order.
            Ui.PinMenuRows(panel, MenuRowPin.Content);
            var entries = AllEntries();
            AdoptCachedReadmes(entries);
            var ordered = SortedForView(entries);
            int openAt = -1;
            bool flowFound = _state.OpenKey == null;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (_state.DrawerKey != null && openAt < 0
                    && string.Equals(_state.DrawerKey, ordered[i].Key, StringComparison.OrdinalIgnoreCase))
                    openAt = i;
                if (!flowFound && string.Equals(_state.OpenKey, ordered[i].Key, StringComparison.OrdinalIgnoreCase))
                    flowFound = true;
            }

            // Creating a row is ~30 Unity objects; all of them in one frame was most of the tab
            // switch. With DeferRowFill the first frame gets what the viewport shows (and the opened
            // row); the rest follow in FillRows, a frame at a time.
            int wantVisible = ViewportRows + IconLookahead;
            int visibleBuilt = 0;
            int next = 0;
            for (; next < ordered.Count; next++)
            {
                if (DeferRowFill && next > openAt && visibleBuilt >= wantVisible) break;
                if (AddListingRow(panel, ordered[next]) && IsVisible(ordered[next])) visibleBuilt++;
            }

            _emptyLabel = panel.Label(InstallModsCopy.NoMatchingListings);
            if (openAt < 0 && _state.DrawerKey != null) SetDrawer(null);
            if (!flowFound) FocusListing(null);
            _unbuilt = next < ordered.Count ? ordered.GetRange(next, ordered.Count - next) : null;

            BuildPendingBar(panel, PendingCount());
            _remoteBuiltSignature = RemoteSignature(); // REMOTETEST1: what NoteRemoteChanged compares against
            RefilterListings();
            ResumePrefetch();
            RequestVisibleIcons();
            HookScroll();
            // Last, after RefilterListings' layout pass: the row positions the anchor measures do
            // not exist until the rows that are hidden by the filters have been taken out.
            // INSTALLSIDES2: no reveal — a toggle keeps its row where it was; nothing else moves the list.
            AnchorClickedRow();
            if (_unbuilt != null)
            {
                int generation = ++_fillGeneration;
                NextFrame(() => FillRows(panel, generation));
            }
        }
        finally { EndBuildMemo(); }
    }

    /// <summary>
    /// Build the rows a deferred <see cref="Build"/> left out, in sort order, a frame's budget at a
    /// time. A later build or a disposed panel drops the remainder (by panel and generation). New
    /// rows take the filters as they are now; the count and icons follow once the list is whole.
    /// </summary>
    private void FillRows(IUiPanel panel, int generation)
    {
        if (!ReferenceEquals(panel, _panel) || generation != _fillGeneration || _unbuilt == null) return;
        var rest = _unbuilt;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long budget = System.Diagnostics.Stopwatch.Frequency * FillBudgetMs / 1000;
        int taken = 0;
        Ui.PinMenuRows(panel, MenuRowPin.Content);
        UiLayoutBatch.Begin();
        BeginBuildMemo();
        try
        {
            while (taken < rest.Count)
            {
                var entry = rest[taken++];
                if (AddListingRow(panel, entry))
                {
                    var row = _rows[_rows.Count - 1].Row;
                    try { row.Visible = IsVisible(entry); }
                    catch { /* */ }
                }
                if (System.Diagnostics.Stopwatch.GetTimestamp() - start >= budget) break;
            }
        }
        finally
        {
            EndBuildMemo();
            UiLayoutBatch.End();
        }

        if (taken < rest.Count)
        {
            _unbuilt = rest.GetRange(taken, rest.Count - taken);
            NextFrame(() => FillRows(panel, generation));
            return;
        }

        _unbuilt = null;
        RefilterListings();
    }

    /// <summary>One listing's row (and its drawer when it is the opened one). False for a keyless entry.</summary>
    private bool AddListingRow(IUiPanel panel, CatalogEntry entry)
    {
        string key = entry.Key;
        if (string.IsNullOrEmpty(key)) return false;
        bool isOpen = string.Equals(_state.DrawerKey, key, StringComparison.OrdinalIgnoreCase);
        // INSTALLUX1 item 4: every right-hand column ends one scrollbar band inside the row edge.
        // VANILLA1: the Mods list's soft selected fill, the game's faint line between entries, and
        // (round 2) the quiet hover: the game's entry hue at a low alpha, every text tone readable.
        bool simple = IsSimple;
        // Chat stage 3 (mod links): a shareable listing's row reports the pointer, which shows its Share button.
        bool share = CanShare(entry);
        var row = panel.CellRow(new CellRowSpec(
            key, simple ? SimpleRowCells(entry) : RowCells(entry), twoLine: true, rightInset: HeadBlockLayout.RightColumnInset,
            soft: ListRowsSoft, separator: true, quietHover: true, hover: share));
        // INSTALLSIDES1: no row click. Only the expand button opens or closes the drawer; a click on
        // the row, its title or any of its buttons never does.
        row.OnClick(CellExpand, () => ToggleDrawer(key));
        if (share)
        {
            var hovered = row;
            row.OnHover(entered =>
            {
                if (!hovered.IsDisposed) hovered.SetCell(CellShare, visible: entered);
            });
            row.OnClick(CellShare, () => ShareInChat?.Invoke(key));
        }
        if (simple)
        {
            // INSTALLSIMPLE1: the one button (the LIST2 key, so `<key>/install` clicks it).
            row.OnClick(CellInstall, () => OnSimpleClick(key));
        }
        else
        {
            row.OnClick(CellBoth, () => OnBothClick(key));
            row.OnClick(CellClientA, () => OnSideSlot(key, server: false, slot: 0));
            row.OnClick(CellClientB, () => OnSideSlot(key, server: false, slot: 1));
            row.OnClick(CellServerA, () => OnSideSlot(key, server: true, slot: 0));
            row.OnClick(CellServerB, () => OnSideSlot(key, server: true, slot: 1));
        }
        row.SetSelected(isOpen);
        row.SetExpanded(isOpen);
        _rowIndex[key] = _rows.Count;
        _rows.Add((row, entry, IconUrlFor(entry)));
        if (isOpen) BuildDrawer(panel, entry);
        return true;
    }

    /// <summary>A side button slot was pressed: act on the button the cell offers in that slot now.</summary>
    internal bool OnSideSlot(string key, bool server, int slot)
    {
        if (string.IsNullOrEmpty(key) || !_catalog.TryGetEntry(key, out var entry) || entry == null) return false;
        var cells = SideCellsFor(entry);
        var cell = server ? cells.Server : cells.Client;
        if (slot < 0 || slot >= cell.Buttons.Count) return false;
        return OnSideClick(key, server, cell.Buttons[slot]);
    }

    /// <summary>
    /// Build only the first screenful of rows in <see cref="Build"/> and the rest over the next
    /// frames (<see cref="FillRows"/>). Off by default, so a caller that reads every row right after
    /// the build still gets them; the Mods menu turns it on when the list opens at its top.
    /// </summary>
    public bool DeferRowFill { get; set; }

    /// <summary>
    /// Runs a <see cref="FillRows"/> step on a later frame. The pane's post is not that in the game:
    /// work posted from inside the main-thread drain runs in the same drain. Null uses the post
    /// (a test's queue is its own frame).
    /// </summary>
    public Action<Action>? FrameScheduler { get; set; }

    private void NextFrame(Action step) => (FrameScheduler ?? _post)(step);

    /// <summary>Main-thread time one <see cref="FillRows"/> frame may spend creating rows.</summary>
    public const int FillBudgetMs = 12;

    /// <summary>True while a deferred build still has rows to create.</summary>
    public bool FillPending => _unbuilt != null;

    private List<CatalogEntry>? _unbuilt;
    private int _fillGeneration;

    /// <summary>
    /// INSTALLSIDES2: after a drawer toggle's rebuild, put the clicked row back at the distance from
    /// the viewport's top it had before (<see cref="ToggleDrawer"/>), once this build's rows are
    /// placed — inside <c>Build</c> they are still an intermediate layout (the LIST4 lesson). The
    /// marshaler is the pane's own; a build that has been replaced since is dropped by panel
    /// identity. No toggle, no write: the list keeps whatever offset it had.
    /// </summary>
    private void AnchorClickedRow()
    {
        var panel = _panel;
        string? key = _anchorKey;
        float? wanted = _anchorY;
        _anchorKey = null;
        _anchorY = null;
        if (panel == null || key == null || wanted == null) return;
        _post(() =>
        {
            if (!ReferenceEquals(panel, _panel)) return;
            AnchorRowNow(key, wanted.Value);
        });
    }

    private void AnchorRowNow(string key, float wanted)
    {
        _anchoredTo = null;
        var probe = AnchorProbe;
        if (probe != null)
        {
            var answer = probe(key, wanted);
            if (answer != null) _anchoredTo = (key, answer.Value.RowY, wanted, answer.Value.Offset);
            return;
        }

        if (_panel == null || !_rowIndex.TryGetValue(key, out int index) || index < 0 || index >= _rows.Count) return;
        if (UiReveal.TryAnchor(_panel, _rows[index].Row, write: true, wanted, out float rowY, out float offset))
            _anchoredTo = (key, rowY, wanted, offset);
    }

    /// <summary>
    /// Readback <c>drawer:&lt;key&gt;.anchoredTo {key, rowY, wantedY, offset}</c>: the last toggle's
    /// row, where it was measured after the rebuild (before the write), where it was before the
    /// toggle, and the content offset the list holds afterwards. Null when nothing was anchored.
    /// </summary>
    internal Dictionary<string, object?>? AnchoredToWidget()
    {
        var at = _anchoredTo;
        if (at == null) return null;
        return new Dictionary<string, object?>
        {
            ["key"] = at.Value.Key,
            ["rowY"] = Math.Round(at.Value.RowY, 1),
            ["wantedY"] = Math.Round(at.Value.WantedY, 1),
            ["offset"] = Math.Round(at.Value.Offset, 1),
        };
    }

    private (string Key, float RowY, float WantedY, float Offset)? _anchoredTo;

    private void ResetHandles()
    {
        ForgetMarkedPackages();
        UnhookScroll();
        _panel = null;
        _toolbar = null;
        _filters = null;
        _filtersOnly = null;
        _filterRows.Clear();
        _filterRowOf.Clear();
        _header = null;
        _emptyLabel = null;
        _rows.Clear();
        _rowIndex.Clear();
        _unbuilt = null;
        _fillGeneration++;
        _iconRequested.Clear();
        _drawerHandles.Clear();
        _drawerHead = null;
        _drawerBody = null;
        _drawerReason = null;
        _readmeFirst = null;
        _pendingBar.Clear();
        _pendingItems.Clear();
        _restartBand = null;
        _indexStatus = null;
        _indexBlock.Clear();
        _readmeBlockCount = 0;
    }

    /// <summary>
    /// The pending bar, pinned under the list only while a listing waits for a restart
    /// (LIST3 item 9: no Refresh index / Close Mods rows — the index refreshes itself on
    /// open, and Escape or a native tab closes the workspace).
    /// </summary>
    /// <summary>UI polish: the pending action row's cell keys.</summary>
    internal const string PendingRowKey = "install-pending";
    internal const string CellPendingText = "pending-text";
    internal const string CellPendingRestart = "pending-restart";

    /// <summary>
    /// The pending bar's button: <c>Stop server to apply</c> (F20) when a server restart applies the changes
    /// (and a restart route exists), nothing when only a game restart does — nothing in the game can
    /// restart the game.
    /// </summary>
    internal string PendingRestartCaption(string sides) =>
        (sides == InstallModsCopy.SidesServer || sides == InstallModsCopy.SidesBoth) && RemoteRestartRequest != null
            ? (_stop.InFlight ? InstallModsCopy.StoppingServer : CanStartAgain() ? InstallModsCopy.StopAndStartAgain : InstallModsCopy.StopServerToApply)
            : "";

    private void BuildPendingBar(IUiPanel panel, int pending)
    {
        int uninstall = PendingUninstallCount();
        if (pending <= 0 && uninstall <= 0) return;
        Ui.PinMenuRows(panel, MenuRowPin.Bottom);
        // UI polish (live 2026-10-01): one compact action row like the settings page's — the short
        // sentence (still the toggle for the list below it) and a key-cap restart button, both at the
        // right — instead of two full-width flat bars with left-aligned text.
        // POLISH1 defect H: the sentence names the processes these changes actually wait on, and the
        // server by route: the local server, or the connected one.
        string sides = PendingSides();
        bool remote = CurrentRoute.Kind == ServerRouteKind.Remote;
        string sentence = InstallModsCopy.PendingBar(pending, uninstall, sides, remote);
        string restartCaption = PendingRestartCaption(sides);
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Spacer, CellGap),
            new CellSpec(CellKind.Button, CellPendingText).With(text: PendingLine(sentence), align: CellAlign.Right),
        };
        if (restartCaption.Length > 0)
            cells.Add(new CellSpec(CellKind.Button, CellPendingRestart).With(text: restartCaption, align: CellAlign.Right, skin: CellSkin.KeyCap,
                tone: _stop.InFlight ? CellTone.Disabled : CellTone.Normal));
        var bar = panel.CellRow(new CellRowSpec(PendingRowKey, cells,
            height: ModSettingsRowGrid.ActionRowHeight, rightInset: HeadBlockLayout.RightColumnInset, separator: true));
        _pendingBarText = sentence;
        bar.OnClick(CellPendingText, TogglePendingList);
        if (restartCaption.Length > 0) bar.OnClick(CellPendingRestart, RequestRestart);
        _pendingBar.Add(bar);

        // INSTALL2 item 5: one row per pending mod, under the bar, each in its own color.
        var items = PendingItems();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool toLoad = item.State == InstallListingState.Pending;
            Ui.StyleMenuRow(panel, MenuRowTone.Chrome);
            var row = panel.CellRow(new CellRowSpec(PendingItemKeyPrefix + item.Key, new List<CellSpec>
            {
                new CellSpec(CellKind.Text, CellPendingName).With(text: item.Name, width: CellWidth.Fraction(PendingNameFraction)),
                new CellSpec(CellKind.Spacer, CellGap),
                new CellSpec(CellKind.Text, CellPendingState).With(
                    text: PendingItemStateText(item.Key, toLoad),
                    tone: toLoad ? CellTone.Pending : CellTone.Warn,
                    align: CellAlign.Right),
            }, rightInset: HeadBlockLayout.RightColumnInset));
            try { row.Visible = _state.PendingOpen; }
            catch { /* */ }
            _pendingItems.Add(row);
            _pendingBar.Add(row);
        }
    }

    private readonly List<IUiHandle> _pendingItems = new();
    private string _pendingBarText = "";

    /// <summary>The pending action row's sentence (the readback and tests read it).</summary>
    internal string PendingBarText => _pendingBarText;

    /// <summary>INSTALL2 item 5: the bar's list is expanded.</summary>
    public bool PendingListOpen => _state.PendingOpen;

    /// <summary>INSTALL2 item 5: expand or collapse the list of pending mods under the bar.</summary>
    public void TogglePendingList()
    {
        _state.PendingOpen = !_state.PendingOpen;
        for (int i = 0; i < _pendingItems.Count; i++)
        {
            try { _pendingItems[i].Visible = _state.PendingOpen; }
            catch { /* */ }
        }
    }

    /// <summary>
    /// INSTALL2 item 5: the pending mods, in the list's own order — the ones that load at the next
    /// restart and the ones that go at it.
    /// </summary>
    /// <summary>
    /// CONFIRMTARGET1: one pending row's caption. A change that waits only on a server names it;
    /// one that waits on this game (alone or as well) keeps the plain caption.
    /// </summary>
    internal string PendingItemStateText(string key, bool toLoad)
    {
        ServerIdentity? server = null;
        try
        {
            var entry = FindEntry(key);
            if (entry != null)
            {
                var action = ActionFor(entry);
                if (action.Remote) server = ServerIdentityFor(remote: true);
                else
                {
                    var halves = HalvesOf(entry);
                    bool client = toLoad ? halves.Client == InstalledState.HalfPending : halves.Client != InstalledState.HalfNone;
                    bool onServer = toLoad ? halves.Server == InstalledState.HalfPending : halves.Server != InstalledState.HalfNone;
                    if (onServer && !client) server = ServerIdentityFor(remote: false);
                }
            }
        }
        catch { server = null; }

        return InstallModsCopy.PendingItemCaption(toLoad, server);
    }

    private CatalogEntry? FindEntry(string key)
    {
        var entries = AllEntries();
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Key, key, StringComparison.OrdinalIgnoreCase)) return entries[i];
        return null;
    }

    internal List<(string Key, string Name, InstallListingState State)> PendingItems()
    {
        var items = new List<(string, string, InstallListingState)>();
        var entries = SortedForView(AllEntries());
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (IsNotInstallable(entry)) continue;
            var state = ActionFor(entry).State;
            if (state == InstallListingState.Pending || state == InstallListingState.PendingUninstall)
                items.Add((entry.Key, HeadingText(entry), state));
        }

        return items;
    }

    /// <summary>
    /// POLISH1 defect H readback <c>pending.sides</c>: which processes have to restart for the pending
    /// changes — <c>client</c>, <c>server</c>, <c>both</c>, or <c>none</c> when nothing is pending.
    ///
    /// <para>A pending install's side is where its files are: the <c>pending</c> halves of
    /// <see cref="HalvesOf(CatalogEntry)"/>, which is read off the ledgered files on disk. A pending
    /// uninstall's side is where the files it will delete are. A row whose sides cannot be read at all
    /// — this session's own write with no ledger view behind it (the tests' fake catalog) — counts as
    /// the client, because the client is this process and the one restart the player controls from
    /// here; that is also exactly what the bar said before this existed.</para>
    /// </summary>
    public string PendingSides()
    {
        bool client = false, server = false;
        var entries = AllEntries();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (IsNotInstallable(entry)) continue;
            var rowAction = ActionFor(entry);
            var state = rowAction.State;
            if (state != InstallListingState.Pending && state != InstallListingState.PendingUninstall) continue;
            if (rowAction.Remote) { server = true; continue; }
            var halves = HalvesOf(entry);
            bool any = false;
            if (state == InstallListingState.Pending)
            {
                if (halves.Client == InstalledState.HalfPending) { client = true; any = true; }
                if (halves.Server == InstalledState.HalfPending) { server = true; any = true; }
            }
            else
            {
                if (halves.Client != InstalledState.HalfNone) { client = true; any = true; }
                if (halves.Server != InstalledState.HalfNone) { server = true; any = true; }
            }

            if (!any) client = true;
        }

        if (client && server) return InstallModsCopy.SidesBoth;
        if (server) return InstallModsCopy.SidesServer;
        if (client) return InstallModsCopy.SidesClient;
        return InstallModsCopy.SidesNone;
    }

    /// <summary>INSTALL2 item 4/5: listings whose files go at the next restart.</summary>
    public int PendingUninstallCount()
    {
        int n = 0;
        var entries = AllEntries();
        for (int i = 0; i < entries.Count; i++)
        {
            if (IsNotInstallable(entries[i])) continue;
            if (UninstallPendingNow(entries[i])) n++;
            else if (RemoteServer != null && ActionFor(entries[i]) is { Remote: true, State: InstallListingState.PendingUninstall }) n++;
        }

        return n;
    }

    /// <summary>
    /// INSTALLSIDES1: the drawer under the opened row — two rows joined to it. The head
    /// (<c>drawer:&lt;key&gt;</c>, <see cref="InstallDrawerLayout.HeadHeight"/> tall) carries the
    /// <c>Collapse</c> button, the install fact, and the drawer's actions (<c>Read full description</c>,
    /// <c>Configure in Mods</c>, the remote <c>Restart now</c>). The body (<c>drawerBody:&lt;key&gt;</c>)
    /// is one scrolling text block with its own bar: the reasons, the one-liner, the facts and the
    /// README. Both rows are selected, continue the row above (<see cref="CellRowSpec.JoinAbove"/>:
    /// the accent rail and the fill run through the gaps) and start at
    /// <see cref="InstallDrawerLayout.Indent"/>, under the mod's name, with a seam joining them to the
    /// row. The body is as tall as <see cref="InstallDrawerLayout.BodyHeight"/> allows, so row and
    /// drawer take about 80% of the list and the next listing stays in view.
    /// </summary>
    private void BuildDrawer(IUiPanel panel, CatalogEntry entry)
    {
        var action = ActionFor(entry);
        string key = entry.Key;
        float indentCell = InstallDrawerLayout.Indent - CellLayout.Gap;

        var lines = DrawerLines(entry, action, out bool hasReadme);
        bool configure = action.State is InstallListingState.Installed or InstallListingState.Pending
            || SideCellsFor(entry).Client.Kind is SideCells.KindInstalled or SideCells.KindUpdate or SideCells.KindPending;
        bool restart = action.Remote && action.State == InstallListingState.Pending && RemoteRestartRequest != null;

        var headCells = new List<CellSpec>
        {
            new CellSpec(CellKind.Muted, CellIndent).With(text: "", width: CellWidth.Px(indentCell), minWidth: indentCell),
            new CellSpec(CellKind.Button, CellCollapse).With(text: InstallModsCopy.Collapse, skin: CellSkin.PopupButton,
                fontSize: InstallColumns.SideButtonFontSize,
                width: CellWidth.Px(InstallColumns.SideButtonWidth(InstallModsCopy.Collapse)),
                minWidth: InstallColumns.SideButtonWidth(InstallModsCopy.Collapse)),
            // INSTALLSIDES2 item 5: one restart note per side the mod uses.
            new CellSpec(CellKind.Muted, CellNote).With(text: HeadNoteText(entry, action), width: CellWidth.Fill(80f), minWidth: 80f),
        };
        if (hasReadme)
            headCells.Add(new CellSpec(CellKind.Button, CellReadFull).With(text: InstallModsCopy.ReadFullDescription,
                skin: CellSkin.PopupButton, fontSize: InstallColumns.SideButtonFontSize, align: CellAlign.Right,
                width: CellWidth.Px(InstallColumns.SideButtonWidth(InstallModsCopy.ReadFullDescription)),
                minWidth: InstallColumns.SideButtonWidth(InstallModsCopy.ReadFullDescription)));
        // Chat stage 3 (mod links): the listing's Share in chat, beside its other actions.
        bool share = CanShare(entry);
        if (share)
            headCells.Add(new CellSpec(CellKind.Button, CellShareDrawer).With(text: ModLinks.ShareCaption,
                skin: CellSkin.PopupButton, fontSize: InstallColumns.SideButtonFontSize, align: CellAlign.Right,
                width: CellWidth.Px(InstallColumns.SideButtonWidth(ModLinks.ShareCaption)),
                minWidth: InstallColumns.SideButtonWidth(ModLinks.ShareCaption)));
        if (configure)
            headCells.Add(new CellSpec(CellKind.Button, CellConfigure).With(text: InstallModsCopy.ConfigureInMods,
                skin: CellSkin.PopupButton, fontSize: InstallColumns.SideButtonFontSize, align: CellAlign.Right,
                width: CellWidth.Px(InstallColumns.SideButtonWidth(InstallModsCopy.ConfigureInMods)),
                minWidth: InstallColumns.SideButtonWidth(InstallModsCopy.ConfigureInMods)));
        if (restart)
        {
            string caption = _stop.InFlight ? InstallModsCopy.StoppingServer : CanStartAgain() ? InstallModsCopy.StopAndStartAgain : ServerRouteCopy.RestartNowCaption;
            headCells.Add(new CellSpec(CellKind.Button, CellRestartNow).With(text: caption,
                skin: CellSkin.PopupButton, fontSize: InstallColumns.SideButtonFontSize, align: CellAlign.Right,
                width: CellWidth.Px(InstallColumns.SideButtonWidth(caption)), minWidth: InstallColumns.SideButtonWidth(caption)));
        }

        var head = panel.CellRow(new CellRowSpec(DrawerKeyPrefix + key, headCells,
            height: InstallDrawerLayout.HeadHeight, rightInset: HeadBlockLayout.RightColumnInset,
            joinAbove: true, indent: InstallDrawerLayout.Indent, soft: ListRowsSoft));
        head.OnClick(CellCollapse, () => ToggleDrawer(key));
        if (hasReadme) head.OnClick(CellReadFull, ShowFullDescription);
        if (share) head.OnClick(CellShareDrawer, () => ShareInChat?.Invoke(key));
        if (configure)
        {
            string pluginId = string.IsNullOrEmpty(entry.Manifest.Id) ? key : entry.Manifest.Id;
            head.OnClick(CellConfigure, () => _openInstalled?.Invoke(pluginId));
        }

        if (restart)
        {
            // F20: the same confirmation and the same one request as the pending bar's button.
            head.OnClick(CellRestartNow, RequestRestart);
        }

        head.SetSelected(true);
        _drawerHead = head;
        _drawerHandles.Add(head);

        // INSTALLUX2 item 1: the confirmation is a modal dialog; a rebuild only makes sure the open
        // one shows the words this pass computed (a plan that landed, a refusal).
        if (PromptOpenFor(entry)) ShowPromptDialog(entry);

        // INSTALLSIDES2 item 4 (option A): a compact, fixed body — no scroll bar, no wheel of its own
        // (the wheel scrolls the list); as tall as its clamped text, the next listing still in view.
        float rowWidth = ColumnRowWidth();
        float bodyWidth = rowWidth - InstallDrawerLayout.Indent - HeadBlockLayout.RightColumnInset;
        float content = InstallDrawerLayout.EstimateContentHeight(lines, bodyWidth);
        float bodyHeight = InstallDrawerLayout.CompactBodyHeight(ListViewport, CellLayout.TwoLineRowHeight, content);
        var bodyCell = new CellSpec(CellKind.ScrollText, CellBody).With(width: CellWidth.Fill(120f), minWidth: 120f);
        bodyCell.Lines = lines;
        bodyCell.NoScroll = true;
        var body = panel.CellRow(new CellRowSpec(DrawerBodyKeyPrefix + key, new List<CellSpec>
        {
            new CellSpec(CellKind.Muted, CellIndent).With(text: "", width: CellWidth.Px(indentCell), minWidth: indentCell),
            bodyCell,
        }, height: bodyHeight, rightInset: HeadBlockLayout.RightColumnInset, joinAbove: true, soft: ListRowsSoft));
        body.SetSelected(true);
        _drawerBody = body;
        _readmeFirst = body;
        _drawerHandles.Add(body);
        _drawerBodyHeight = bodyHeight;
        _drawerContentEstimate = content;
        _drawerLines = lines.Count;
    }

    private IUiCellRow? _drawerBody;
    private float _drawerBodyHeight;
    private float _drawerContentEstimate;
    private int _drawerLines;

    /// <summary>The drawer's scrolling body row, for the readback and the tests.</summary>
    internal IUiCellRow? DrawerBodyRow => _drawerBody;

    private float _listViewport;

    /// <summary>
    /// INSTALLSIDES1: the list viewport's height in px (the Mods menu measures it from the workspace
    /// frame, as it does the visible row count). Zero or less leaves the fallback.
    /// </summary>
    public void SetListViewport(float px) => _listViewport = float.IsNaN(px) || float.IsInfinity(px) ? 0f : px;

    /// <summary>The list viewport the drawer is sized against.</summary>
    internal float ListViewport => _listViewport > 1f ? _listViewport : InstallDrawerLayout.FallbackViewport;

    /// <summary>
    /// INSTALLSIDES2 item 4: the compact drawer body's text, top to bottom: why an action was refused
    /// or failed, the route and server-half notes, the stale-tag warning, a short description (the
    /// one-liner, then the README's opening paragraphs cut to <see cref="InstallDrawerLayout.ExcerptChars"/>),
    /// and the facts two to a line (latest version, released, downloads, side, installed here,
    /// requires, NSFW when flagged, package, plugin, update tag). <paramref name="hasReadme"/> says a
    /// README is known, for the <c>Read full description</c> reader.
    /// </summary>
    /// <summary>
    /// The width one grid column of facts has: half the drawer body's text width, less a gutter
    /// (the second column starts at <see cref="CellLayout.FactSecondColumnPercent"/>).
    /// </summary>
    internal float FactHalfWidth()
    {
        float body = ColumnRowWidth() - InstallDrawerLayout.Indent - HeadBlockLayout.RightColumnInset - 2f * CellLayout.ScrollPad;
        return Math.Max(120f, body * CellLayout.FactSecondColumnPercent / 100f - FactGutter);
    }

    /// <summary>The air kept between a left-column fact and the right column.</summary>
    internal const float FactGutter = 24f;

    /// <summary>A fact (its label, two spaces and its value, at the body size) fits one grid column.</summary>
    internal static bool FitsHalf((string Label, string Text) fact, float half) =>
        CellLayout.EstimateTextWidth((fact.Label ?? "") + "  " + (fact.Text ?? ""), CellLayout.ScrollBodyFontSize) <= half;

    internal List<CellLine> DrawerLines(CatalogEntry entry, InstallRowAction action, out bool hasReadme)
    {
        hasReadme = false;
        var lines = new List<CellLine>();
        if (!string.IsNullOrEmpty(action.Reason)) lines.Add(new CellLine(CellLineKind.Warn, action.Reason));
        // INSTALLFAIL1: the sentence behind a side's `Install failed`, whatever the row's own action says.
        if (IsOpen(entry) && _phase == InstallActionPhase.Failed && !string.IsNullOrEmpty(_actionError)
            && !string.Equals(_actionError, action.Reason, StringComparison.Ordinal))
            lines.Add(new CellLine(CellLineKind.Warn, _actionError!));
        // LOCALONLY1: why this listing has no server half to install on the connected server.
        if (LocalOnlyOnRemote(entry)) lines.Add(new CellLine(CellLineKind.Muted, InstallModsCopy.LocalOnlyServerSentence));
        else if (ServerLocalOffer(entry) is { } offered)
            lines.Add(new CellLine(CellLineKind.Muted, InstallModsCopy.ServerLocalSourceSentence(offered.Version)));
        // SIMPLESCOPE1: Simple acts only on this PC's half of a both-sides mod here; the server half is the admins'.
        if (SimpleServerHalfNote(entry) is { Length: > 0 } halfNote) lines.Add(new CellLine(CellLineKind.Muted, halfNote));

        // SRVUNINST1 review: an Uninstall whose client copy changed after its box opened did nothing; say so.
        if (_uninstallChanged is { } changed && string.Equals(changed.Key, entry.Key, StringComparison.OrdinalIgnoreCase))
            lines.Add(new CellLine(CellLineKind.Warn, changed.Note));

        // SRVUNINST1: a refused remote uninstall says why in the drawer (it used to be only in the log).
        if (RemoteServer?.LatestFor(entry.Key) is { Op: RemoteInstallOp.Uninstall, Stage: RemoteInstallStage.Refused or RemoteInstallStage.Failed } refusedStep)
        {
            string why = ModUninstallPlan.ResultNote(refusedStep, ServerIdentityFor(remote: true));
            if (RemoteUninstallForce.Offers(refusedStep)) why += " " + RemoteUninstallForce.AnywayCaption + " skips that check.";
            if (!string.Equals(why, action.Reason, StringComparison.Ordinal)) lines.Add(new CellLine(CellLineKind.Warn, why));
        }

        var sides = SideCellsFor(entry);
        // The route's sentence when the Server cell is held back: by a note, or (INSTALLSIDES2 item 3)
        // by the Admin only / Authenticate as admin lock, whose why is the admin list.
        bool locked = sides.Server.Has(SideButton.AdminOnly) || sides.Server.Has(SideButton.Authenticate);
        if ((sides.Server.Note.Length > 0 || locked) && sides.Server.Kind != SideCells.KindNoServer)
        {
            var route = CurrentRoute;
            string why = (route.Reason + (route.Help.Length > 0 ? " " + route.Help : "")).Trim();
            if (why.Length > 0 && !string.Equals(why, action.Reason, StringComparison.Ordinal))
                lines.Add(new CellLine(CellLineKind.Muted, why));
        }
        string half = ServerHalfNote(entry);
        if (half.Length > 0) lines.Add(new CellLine(CellLineKind.Muted, half));
        if (action.Remote && action.State == InstallListingState.Pending && RemoteRestartRequest != null)
            lines.Add(new CellLine(CellLineKind.Muted, ServerRouteCopy.RestartNowHelp));
        string? tag = CurrentUpdateTag;
        if (tag != null && !CatalogUpdateTags.IsTagged(entry, tag))
            lines.Add(new CellLine(CellLineKind.Warn, InstallModsCopy.StaleWarnMark + UpdateTagText(entry) + InstallModsCopy.StaleSuffix));

        // The short description: the one-liner, then the README's opening paragraphs, each clamped.
        string oneLiner = InstallDrawerLayout.Excerpt(ListingSnippet(entry.Description));
        var blocks = ReadmeBlocksFor(entry);
        string excerpt = "";
        if (blocks.Count > 0)
        {
            hasReadme = true;
            _readmeState = ListingDocState.Ready;
            _docsKey = entry.Key;
            _readmePlain = ReadmeTextFor(entry) ?? MarkdownPlain.Join(blocks);
            var opening = new System.Text.StringBuilder();
            for (int i = 0; i < blocks.Count && opening.Length < InstallDrawerLayout.ExcerptChars; i++)
            {
                if (blocks[i].Kind != MarkdownBlockKind.Paragraph) continue;
                if (opening.Length > 0) opening.Append(' ');
                opening.Append(blocks[i].Text);
            }
            excerpt = InstallDrawerLayout.Excerpt(opening.ToString());
            _readmeBlockCount = blocks.Count;
        }
        else if (_state.ReadmeUnavailable.Contains(ReadmeIndexKey(entry)) || _readmeState == ListingDocState.Unavailable)
        {
            _readmeState = ListingDocState.Unavailable;
            _docsKey = entry.Key;
        }
        else
        {
            if (_readmeState == ListingDocState.Idle) _readmeState = ListingDocState.Loading;
            _docsKey = entry.Key;
        }

        if (oneLiner.Length > 0) lines.Add(new CellLine(CellLineKind.Body, oneLiner));
        if (excerpt.Length > 0 && !string.Equals(excerpt, oneLiner, StringComparison.OrdinalIgnoreCase))
            lines.Add(new CellLine(CellLineKind.Body, excerpt));
        else if (_readmeState == ListingDocState.Loading)
            lines.Add(new CellLine(CellLineKind.Muted, InstallModsCopy.LoadingDescription));
        else if (_readmeState == ListingDocState.Unavailable)
            lines.Add(new CellLine(CellLineKind.Muted, InstallModsCopy.DescriptionUnavailable(_readmeReason)));

        // The facts, two to a line.
        var facts = new List<(string Label, string Text)>
        {
            (InstallModsCopy.FactLatest, HeaderVersion(entry)),
        };
        string released = ReleasedDate(entry, out string source);
        facts.Add((InstallModsCopy.FactReleased,
            released.Length == 0 ? InstallModsCopy.UpdatedUnknown : source == ReleasedSourcePackage ? released + " (updated)" : released));
        facts.Add((InstallModsCopy.FactDownloads, DownloadsText(entry)));
        var side = SideOf(entry);
        facts.Add((InstallModsCopy.FactSide,
            InstallModsCopy.SideFact(side, entry.Provenance != CatalogProvenance.ManifestVerified && ProcessFilterProbe?.Invoke(entry) == null)));
        var installed = InstalledTimeFor(entry);
        facts.Add((InstallModsCopy.FactInstalledHere,
            installed.Utc == null ? InstallModsCopy.NotInstalledHere : InstallColumns.FullDate(installed.Utc.Value.ToLocalTime())));
        facts.Add((InstallModsCopy.FactRequires, DependenciesFact(entry)));
        if (entry.HasNsfwContent) facts.Add((InstallModsCopy.FactNsfw, InstallModsCopy.NsfwFlagged));
        facts.Add((InstallModsCopy.FactPackage, PackageFact(entry)));
        string identity = IdentityFact(entry);
        if (identity.Length > 0) facts.Add((InstallModsCopy.FactPlugin, identity));
        facts.Add((InstallModsCopy.FactUpdateTag, UpdateTagText(entry)));
        // A tidy two-column grid (live review): the facts that fit half the body fill it in order, two
        // to a line, both columns aligned; a fact too long for half the line (a long dependency
        // list) spans both columns, after the grid, so it never leaves a hole in it.
        float halfWidth = FactHalfWidth();
        var shortFacts = new List<(string Label, string Text)>();
        var longFacts = new List<(string Label, string Text)>();
        foreach (var fact in facts) (FitsHalf(fact, halfWidth) ? shortFacts : longFacts).Add(fact);
        for (int i = 0; i < shortFacts.Count; i += 2)
        {
            var first = shortFacts[i];
            if (i + 1 < shortFacts.Count)
                lines.Add(new CellLine(CellLineKind.Fact, first.Text, first.Label, shortFacts[i + 1].Label, shortFacts[i + 1].Text));
            else
                lines.Add(new CellLine(CellLineKind.Fact, first.Text, first.Label));
        }
        foreach (var fact in longFacts) lines.Add(new CellLine(CellLineKind.Fact, fact.Text, fact.Label));

        return lines;
    }

    private static CellLine LineOf(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => new CellLine(CellLineKind.Heading, block.Text ?? ""),
        MarkdownBlockKind.Code => new CellLine(CellLineKind.Code, block.Text ?? ""),
        _ => new CellLine(CellLineKind.Body, block.Text ?? ""),
    };

    // ---- INSTALL2 item 3: the confirmation's words --------------------------------------

    /// <summary>The confirmation's heading for one row and kind.</summary>
    internal string PromptHeadingText(CatalogEntry entry, InstallPromptKind kind)
    {
        string name = HeadingText(entry);
        string version = HeaderVersion(entry);
        // CONFIRMTARGET2: one short line; what it needs is in the facts below. REQRULE6: it names where it goes.
        string target = PromptTargetWords(entry, kind);
        return kind switch
        {
            InstallPromptKind.Install => IsUpdatePrompt
                ? InstallModsCopy.PromptUpdateHeadingOn(name, version, target)
                : InstallModsCopy.PromptInstallHeadingOn(name, version, target),
            InstallPromptKind.Uninstall => InstallModsCopy.PromptUninstallHeadingFrom(name, target),
            InstallPromptKind.Reactivate => InstallModsCopy.PromptReactivateHeadingOn(name, target),
            InstallPromptKind.CancelInstall => InstallModsCopy.PromptCancelInstallHeadingOn(name, target),
            _ => "",
        };
    }

    /// <summary>
    /// REQRULE6 (user report 2026-10-08: a Simple view Install of a mod "Missing on server" showed Not changed / This client
    /// and Takes effect, and the user could not tell it installs only the server's copy): which sides the box's Yes changes,
    /// and the server it names. The same rule the <c>Not changed</c> fact follows, plus the side buttons and a whole-row
    /// Cancel install, which cancels the halves that are pending.
    /// </summary>
    internal (bool Client, bool Server, ServerIdentity? Id) PromptTouches(CatalogEntry entry, InstallPromptKind kind)
    {
        if (entry == null) return (true, false, null);
        var (server, both) = PromptServerFor(entry, kind);
        bool touchesServer = server != null;
        bool touchesClient = !touchesServer || both;
        bool sided = kind is InstallPromptKind.Uninstall or InstallPromptKind.CancelInstall or InstallPromptKind.Reactivate;
        if (sided && _promptSide == PromptSide.Client) { touchesServer = false; touchesClient = true; }
        if (sided && _promptSide == PromptSide.Server) { touchesServer = true; touchesClient = false; }
        if (kind == InstallPromptKind.Install && _promptRemoteOnly) { touchesServer = true; touchesClient = false; }
        if (kind == InstallPromptKind.CancelInstall && _promptSide == PromptSide.Whole)
        {
            try
            {
                var halves = HalvesOf(entry);
                bool c = halves.Client == InstalledState.HalfPending, sv = halves.Server == InstalledState.HalfPending;
                if (c || sv) { touchesClient = c; touchesServer = sv; }
                if (sv && server == null) server = ServerIdentityFor(remote: false);
            }
            catch { /* the client words */ }
        }

        if (kind == InstallPromptKind.Reactivate && _promptSide == PromptSide.Whole)
        {
            // Review M1: Keep installed un-marks every marked half (CancelUninstall over both sides' roots).
            try
            {
                var halves = HalvesOf(entry);
                bool c = ClientRemovalPending(entry, halves, DetectInstalled(entry)), sv = ServerRemovalPending(entry, halves);
                if (c || sv) { touchesClient = c; touchesServer = sv; }
                if (sv) server ??= ServerIdentityFor(remote: false);
            }
            catch { /* the client words */ }
        }

        if (kind == InstallPromptKind.CancelInstall && _promptSide == PromptSide.Whole && RemoteRowFor(entry) is { State: RemoteRowState.Pending })
        {
            // Review M2: a pending copy on the connected server is canceled there too (ConfirmCancelInstall).
            touchesServer = true;
            touchesClient = HalvesOf(entry).Client == InstalledState.HalfPending;
            server = ServerIdentityFor(remote: true);
        }

        if (kind == InstallPromptKind.Uninstall && _promptSide == PromptSide.Whole && server == null)
        {
            // The whole-row Uninstall takes every half the ledgers name (POLISH3 item A), whatever the route.
            try
            {
                var halves = HalvesOf(entry);
                if (halves.Server != InstalledState.HalfNone)
                {
                    touchesServer = true;
                    touchesClient = halves.Client != InstalledState.HalfNone;
                    server = ServerIdentityFor(remote: false);
                }
            }
            catch { /* the client words */ }
        }

        if (touchesServer && server == null) server = ServerIdentityFor(remote: CurrentRoute.Kind == ServerRouteKind.Remote);
        return (touchesClient, touchesServer, touchesServer ? server : null);
    }

    /// <summary>REQRULE6: the heading's <c>on …</c> / <c>from …</c> words; empty for a box that offers two answers (its facts say which).</summary>
    internal string PromptTargetWords(CatalogEntry entry, InstallPromptKind kind)
    {
        try
        {
            if (kind == InstallPromptKind.Install && (PromptRefusal ?? "").Length > 0) return "";
            if (kind == InstallPromptKind.Install && (BothOfferServer(entry) != null || ServerCellBothCaption(entry, kind).Length > 0)) return "";
            var t = PromptTouches(entry, kind);
            return InstallModsCopy.TargetWords(t.Client, t.Server, t.Id?.TitleName ?? "");
        }
        catch { return ""; }
    }

    /// <summary>
    /// REQRULE6: the first fact after <c>Mod</c>: <c>Installs on</c> / <c>Removes from</c> and the sides, so a server-only
    /// action says so before the server's details. A server-only action also says what this game has, so the box needs no
    /// <c>Not changed</c> line for it.
    /// </summary>
    private DialogFact? TargetFact(CatalogEntry entry, InstallPromptKind kind, (ServerIdentity Server, string BothCaption)? offered)
    {
        string label = kind == InstallPromptKind.Uninstall ? InstallModsCopy.FactRemovesFrom : InstallModsCopy.FactInstallsOn;
        if (offered is { } o) return new DialogFact(label, InstallModsCopy.ClientOrBothValue(o.Server.TitleName, o.BothCaption));
        var t = PromptTouches(entry, kind);
        string name = t.Id?.TitleName ?? ServerIdentity.LocalFallback;
        if (kind == InstallPromptKind.Install && ServerCellBothCaption(entry, kind) is { Length: > 0 } caption)
            return new DialogFact(label, InstallModsCopy.ServerOrBothValue(name, caption));
        if (t.Client && t.Server) return new DialogFact(label, InstallModsCopy.BothValue(name));
        if (!t.Server) return new DialogFact(label, InstallModsCopy.ThisGameOnly);
        if (SideOf(entry) == PackageSide.Server) return new DialogFact(label, InstallModsCopy.ServerOnly);
        bool clientHas;
        try { clientHas = SideStatusRules.ClientPresent(SideInputsOf(entry).Client); }
        catch { clientHas = false; }
        string value = kind == InstallPromptKind.Uninstall
            ? clientHas ? InstallModsCopy.ServerOnlyClientKeepsIt : InstallModsCopy.ServerOnly
            : clientHas ? InstallModsCopy.ServerOnlyClientHasIt : InstallModsCopy.ServerOnlyNotThisGame;
        return new DialogFact(label, value);
    }

    /// <summary>CONFIRMTARGET2: the row opens an update — a newer version of a copy already there.</summary>
    private bool IsUpdateRow(CatalogEntry entry)
    {
        try
        {
            var action = ActionFor(entry);
            if (action.State == InstallListingState.Update) return true;
            if (action.State != InstallListingState.Sides) return false;
            var decision = SideStatusRules.Decide(SideInputsOf(entry));
            return decision.Action is SideAction.UpdateServer or SideAction.UpdateClient;
        }
        catch { return false; }
    }

    /// <summary>
    /// CONFIRMTARGET2: the facts the install, update and uninstall boxes draw (labels muted,
    /// values bright), with the rows that do not apply left out. Empty for the other kinds.
    /// </summary>
    internal IReadOnlyList<DialogFact> PromptFacts(CatalogEntry entry, InstallPromptKind kind)
    {
        var facts = new List<DialogFact>();
        if (entry == null || kind is not (InstallPromptKind.Install or InstallPromptKind.Uninstall)) return facts;
        facts.Add(new DialogFact(InstallModsCopy.FactMod,
            InstallModsCopy.ModFactValue(HeadingText(entry), HeaderVersion(entry), HeaderAuthor(entry))));
        bool refused = kind == InstallPromptKind.Install && (PromptRefusal ?? "").Length > 0;
        if (refused) return facts;

        var (server, both) = PromptServerFor(entry, kind);
        // F6 (live 2026-10-07): the client cell's box that also offers "Yes, install on both" names both sides the way
        // the Server cell's box names its server, and says which answer installs which side.
        var offered = kind == InstallPromptKind.Install && server == null ? BothOfferServer(entry) : null;
        // REQRULE6: where the Yes goes, before the server's details.
        if (TargetFact(entry, kind, offered) is { } target) facts.Add(target);
        if (offered is { } other)
        {
            facts.Add(new DialogFact(InstallModsCopy.FactServer,
                InstallModsCopy.ServerOnlyWith(other.Server, other.BothCaption)));
            if (!other.Server.Remote && other.Server.Folder.Length > 0)
                facts.Add(new DialogFact(InstallModsCopy.FactFolder, ServerIdentity.MiddleEllipsis(other.Server.Folder, ServerIdentity.FolderMax)));
            if (other.Server.Remote && other.Server.Address.Length > 0)
                facts.Add(new DialogFact(InstallModsCopy.FactAddress, other.Server.Address));
            facts.Add(new DialogFact(InstallModsCopy.FactServerCopy, RemoteEffect(RemoteRowFor(entry), uninstall: false)));
        }
        else if (server != null)
        {
            facts.Add(new DialogFact(InstallModsCopy.FactServer, InstallModsCopy.ServerFactValue(server)));
            if (!server.Remote && server.Folder.Length > 0)
                facts.Add(new DialogFact(InstallModsCopy.FactFolder, ServerIdentity.MiddleEllipsis(server.Folder, ServerIdentity.FolderMax)));
            if (server.Remote && server.Address.Length > 0)
                facts.Add(new DialogFact(InstallModsCopy.FactAddress, server.Address));
            // F39 item 3: "Also on: This game" repeated the Installs on / Removes from line (REQRULE6), so it is gone.
        }

        // A box offering both sides leaves neither side unchanged: its answers say which sides change.
        // F6 follow-up: the Server cell's box that also offers "install on both" names this client the same way.
        if (offered == null && ServerCellBothCaption(entry, kind) is { Length: > 0 } bothCaption)
            facts.Add(new DialogFact(InstallModsCopy.FactThisClient, InstallModsCopy.ClientOnlyWith(bothCaption)));
        else if (offered == null) AddNotChanged(entry, kind, server, both, facts);
        if (kind == InstallPromptKind.Install) AddInstallEffectAndRequires(entry, server, facts, offered != null);
        else
        {
            AddUninstallEffect(entry, facts);
            // SRVUNINST1 review: the box draws its facts, so this client's copy no ledger lists is one of them.
            if (_promptSide == PromptSide.Whole && (_promptClientPlain ?? WholeClientPlain(entry)) is { Has: true } plain)
                facts.Add(new DialogFact(InstallModsCopy.FactThisClient,
                    plain.Sentence.Length > 0 ? plain.Sentence : InstallModsCopy.ThisClientCopyStaysValue));
            // UNINSTREQ1: removing this client's copy of a mod the connected server requires says what happens.
            if (_promptSide == PromptSide.Client || (_promptSide == PromptSide.Whole && entry.Side != PackageSide.Server))
            {
                string required = RequiredByFor(entry);
                if (required.Length > 0) facts.Add(new DialogFact(Networking.RequiredHereFact.Label, required, DialogFactTone.Warn));
            }
        }
        return facts;
    }

    /// <summary>
    /// SIMPLESCOPE1 (project rule, 2026-10-07): a confirmation touching a server or a both-sides mod states each side: the
    /// <c>Server</c> and <c>Also on</c> facts name the sides it changes, and <c>Not changed</c> names the side it leaves —
    /// this client on a server-only action, the connected server on a this-PC-only action (for its admin: its copy stays,
    /// it doesn't have this mod, or nothing changes there while its half is unknown; for a player who is not its admin,
    /// its admins manage the server half).
    /// </summary>
    private void AddNotChanged(CatalogEntry entry, InstallPromptKind kind, ServerIdentity? server, bool both, List<DialogFact> facts)
    {
        var side = SideOf(entry);
        bool touchesServer = server != null;
        bool touchesClient = !touchesServer || both;
        if (kind == InstallPromptKind.Uninstall && _promptSide == PromptSide.Client) { touchesServer = false; touchesClient = true; }
        if (kind == InstallPromptKind.Uninstall && _promptSide == PromptSide.Server) { touchesServer = true; touchesClient = false; }
        if (kind == InstallPromptKind.Install && _promptRemoteOnly) { touchesServer = true; touchesClient = false; }
        if (!touchesServer && side != PackageSide.Both) return;
        // REQRULE6: a server-only action's Installs on / Removes from line already says what this game has.
        if (touchesServer && !touchesClient) return;
        if (touchesClient && !touchesServer && side == PackageSide.Both && ConnectedServerKnown)
        {
            var route = CurrentRoute;
            string name = ServerIdentityFor(remote: route.Kind != ServerRouteKind.Local).ShortName;
            bool admin = route.Kind is ServerRouteKind.Remote or ServerRouteKind.Local;
            // F14 (live 2026-10-07): an admin's line reads the server half, so a server without a copy is not
            // told its copy stays. A player who is not its admin keeps the admins-manage wording either way.
            ServerHalfState serverHalf;
            try { serverHalf = SideInputsOf(entry).Server; }
            catch { serverHalf = ServerHalfState.Unknown; }
            string value = !admin ? InstallSimpleCopy.ServerAdminsManage(name)
                : SideStatusRules.ServerPresent(serverHalf) ? InstallSimpleCopy.ServerKeepsCopy(name)
                : serverHalf == ServerHalfState.Absent ? InstallSimpleCopy.ServerHasNone(name)
                : InstallSimpleCopy.ServerUnchanged(name);
            facts.Add(new DialogFact(InstallSimpleCopy.FactNotChanged, value));
        }
    }

    /// <summary>UNINSTREQ1: the <c>Required by</c> value for this listing's plugin, or empty. Never throws.</summary>
    private string RequiredByFor(CatalogEntry entry)
    {
        try
        {
            // The plugin this client has (as the Client cell reads it), else the listing's identity.
            string? id = DetectInstalled(entry).PluginId;
            if (string.IsNullOrWhiteSpace(id)) id = RestingIdentity(entry)?.PluginGuid;
            if (string.IsNullOrWhiteSpace(id)) id = entry.Manifest.Id;
            return Networking.RequiredHereFact.ValueFor(id);
        }
        catch { return ""; }
    }

    /// <summary>
    /// F6: the server the client cell's install box also offers through its "Yes, install on both" (the configured
    /// folder's server on the local route, the connected one on the remote route) and that button's caption; null
    /// when the box offers no second side.
    /// </summary>
    internal (ServerIdentity Server, string BothCaption)? BothOfferServer(CatalogEntry entry)
    {
        if (entry == null || _promptRemoteOnly || _targetOverride != InstallTarget.Client) return null;
        BothOffer offer;
        try { offer = BothOfferFor(entry, InstallPromptKind.Install); }
        catch { return null; }
        if (!offer.Offered) return null;
        var route = CurrentRoute.Kind;
        if (route is not (ServerRouteKind.Local or ServerRouteKind.Remote)) return null;
        return (ServerIdentityFor(remote: route == ServerRouteKind.Remote), offer.BothCaption);
    }

    /// <summary>F6 follow-up: the both answer's caption of a Server cell install box that also offers this client, or empty.</summary>
    private string ServerCellBothCaption(CatalogEntry entry, InstallPromptKind kind)
    {
        if (kind != InstallPromptKind.Install || !(_promptRemoteOnly || _targetOverride == InstallTarget.LocalServer)) return "";
        try
        {
            var offer = BothOfferFor(entry, kind);
            return offer.Offered ? offer.BothCaption : "";
        }
        catch { return ""; }
    }

    /// <param name="sidesNamed">F6: the box names each side, so this client's effect is its <c>This client</c> fact.</param>
    private void AddInstallEffectAndRequires(CatalogEntry entry, ServerIdentity? server, List<DialogFact> facts, bool sidesNamed = false)
    {
        var resolved = PromptResolvedFor(entry);
        if (_promptRemoteOnly)
        {
            facts.Add(new DialogFact(InstallModsCopy.FactTakesEffect, RemoteEffect(RemoteRowFor(entry), uninstall: false)));
            AddRemoteSource(entry, server, facts);
            return;
        }

        string effectLabel = sidesNamed ? InstallModsCopy.FactThisClient : InstallModsCopy.FactTakesEffect;
        if (!_promptPlanReady)
        {
            facts.Add(new DialogFact(effectLabel, InstallModsCopy.EffectChecking));
            return;
        }

        var plan = _planSet?.Root ?? _lastPlan;
        if (plan != null)
        {
            string effect = EffectOf(plan.Decision, plan.Target, plan.IsMixed);
            facts.Add(new DialogFact(effectLabel, sidesNamed ? InstallModsCopy.ThisClientBothAnswers(effect) : effect,
                plan.Decision == InstallDecision.ApplyNow && !plan.IsMixed ? DialogFactTone.Ok : DialogFactTone.Normal));
        }
        if (resolved.ToConnectedServer)
        {
            facts.Add(new DialogFact(InstallModsCopy.FactServerCopy, RemoteEffect(RemoteRowFor(entry), uninstall: false)));
            AddRemoteSource(entry, server, facts);
        }

        // The dependency rows: where each was looked for is the plan's target (local route only).
        var target = plan?.Target ?? resolved.Target;
        string where = InstallSummary.WhereFound(target, server != null && !server.Remote ? server : null);
        bool first = true;
        void Add(string value, DialogFactTone tone)
        {
            facts.Add(new DialogFact(first ? InstallModsCopy.FactRequires : "", value, tone));
            first = false;
        }

        if (_planSet != null && _planSet.Ok)
        {
            for (int i = 0; i < _planSet.Closure.Count; i++)
            {
                var member = _planSet.Closure[i];
                var planned = _planSet.PlannedMember(member.Package);
                string version = planned != null ? planned.Version : member.Version;
                Add(InstallModsCopy.RequiresValue(member.Package, version, !member.IsMissing, false, where),
                    member.IsMissing ? DialogFactTone.Normal : DialogFactTone.Ok);
            }
        }
        else if (plan != null)
        {
            for (int i = 0; i < plan.OrdinaryDependencies.Count; i++)
            {
                var d = plan.OrdinaryDependencies[i];
                if (string.Equals(d.SatisfiedBy, OrdinaryDependencies.BySatisfiedBepInEx, StringComparison.Ordinal)) continue;
                bool blocks = !d.Satisfied && !string.IsNullOrEmpty(d.Refusal);
                bool present = d.Satisfied && !string.Equals(d.SatisfiedBy, OrdinaryDependencies.BySatisfiedClosure, StringComparison.Ordinal);
                Add(InstallModsCopy.RequiresValue(d.Package, "", present, blocks, where),
                    blocks ? DialogFactTone.Danger : present ? DialogFactTone.Ok : DialogFactTone.Normal);
            }
        }
    }

    /// <summary>
    /// F33 (live 2026-10-08): the connected server's effect from its own status row: a hot copy unloads (or an update
    /// loads) now, a copy loaded at its start goes at its next restart, a pending copy is canceled now. Only a row the
    /// status does not settle keeps "Now if it hot-loads, else at the next server restart".
    /// </summary>
    internal static string RemoteEffect(RemoteServerRow? row, bool uninstall) => row?.State switch
    {
        RemoteRowState.Hot => uninstall ? InstallModsCopy.EffectUnloadNow : InstallModsCopy.EffectNow,
        RemoteRowState.Loaded when uninstall => InstallModsCopy.EffectServerRestart,
        RemoteRowState.Pending => uninstall ? InstallModsCopy.EffectRemoteCancelsPending : InstallModsCopy.EffectServerRestart,
        _ => InstallModsCopy.EffectRemote,
    };

    /// <summary>F33: the install box names the server's own local test source when that is where the server installs from.</summary>
    private void AddRemoteSource(CatalogEntry entry, ServerIdentity? server, List<DialogFact> facts)
    {
        try
        {
            if (CurrentRoute.Kind != ServerRouteKind.Remote || ServerLocalOffer(entry) is not { } offer) return;
            string name = server != null && server.Remote ? server.TitleName : ServerIdentityFor(remote: true).TitleName;
            facts.Add(new DialogFact(InstallModsCopy.FactSource, InstallModsCopy.ServerLocalSourceFact(name, offer.Version)));
        }
        catch { /* no source fact */ }
    }

    /// <summary>The <c>Takes effect</c> value of a plan's decision.</summary>
    internal static string EffectOf(InstallDecision decision, InstallTarget target, bool mixed)
    {
        if (decision == InstallDecision.ApplyNow) return mixed ? InstallModsCopy.EffectMixed : InstallModsCopy.EffectNow;
        if (decision == InstallDecision.RestartServerRequired) return InstallModsCopy.EffectServerRestart;
        if (decision == InstallDecision.RestartBothRequired) return InstallModsCopy.EffectBothRestart;
        if (decision == InstallDecision.RestartClientRequired) return InstallModsCopy.EffectGameRestart;
        return target switch
        {
            InstallTarget.LocalServer => InstallModsCopy.EffectServerRestart,
            InstallTarget.Both => InstallModsCopy.EffectBothRestart,
            _ => InstallModsCopy.EffectGameRestart,
        };
    }

    /// <summary>
    /// REQRULE3 (live 2026-10-08): HotDemo installed on both sides from the client, its server copy hot-loaded by the local
    /// server's reloader, and the uninstall box said "at the next server restart on the server" although the removal deletes
    /// that copy now (HOTDEMO1: <see cref="ServerUnloadsNow"/>). The box asks the same question the removal does: the local
    /// server's half is loaded, its reload folder is watched from here and the copy is a trusted hot-loadable image. Any other
    /// loaded copy (the server not running, an image that is not hot-loadable) keeps the marker for its next start.
    /// </summary>
    /// <para>Review L1: the removal (<see cref="ConfirmUninstall"/>, the Server cell's local uninstall) asks this same
    /// function, on any route: the local server folder's copy is the local server's whatever server this client joined.</para>
    private bool LocalServerHalfUnloadsNow(CatalogEntry entry, bool log = false) =>
        HalvesOf(entry).Server != InstalledState.HalfPending
        && ServerUnloadsNow(entry, log);

    private void AddUninstallEffect(CatalogEntry entry, List<DialogFact> facts)
    {
        string effect;
        if (_promptSide == PromptSide.Client)
            effect = IsHotLoaded(entry) ? InstallModsCopy.EffectUnloadNow : InstallModsCopy.EffectGameRestart;
        else if (_promptSide == PromptSide.Server)
            effect = CurrentRoute.Kind == ServerRouteKind.Remote ? RemoteEffect(RemoteRowFor(entry), uninstall: true)
                : LocalServerHalfUnloadsNow(entry) ? InstallModsCopy.EffectUnloadNow : InstallModsCopy.EffectServerRestart;
        else if (RemoteRowFor(entry) is { } remoteRow && HalvesOf(entry).Client == InstalledState.HalfNone)
            effect = RemoteEffect(remoteRow, uninstall: true);
        else if (IsHotLoaded(entry))
        {
            effect = HalvesOf(entry).Server switch
            {
                InstalledState.HalfPending => InstallModsCopy.EffectUnloadNowCancelsServer,
                // REQRULE3 (live 2026-10-08): a copy the local server's reloader holds goes now, as the removal does it.
                InstalledState.HalfLoaded => LocalServerHalfUnloadsNow(entry) ? InstallModsCopy.EffectUnloadNow : InstallModsCopy.EffectUnloadNowServerLater,
                _ => InstallModsCopy.EffectUnloadNow,
            };
        }
        else
        {
            bool serverNow = LocalServerHalfUnloadsNow(entry);
            effect = InstalledSidesOf(entry) switch
            {
                InstallModsCopy.SidesServer => serverNow ? InstallModsCopy.EffectUnloadNow : InstallModsCopy.EffectServerRestart,
                InstallModsCopy.SidesBoth => serverNow ? InstallModsCopy.EffectGameRestartServerNow : InstallModsCopy.EffectBothRestart,
                _ => InstallModsCopy.EffectGameRestart,
            };
        }

        facts.Add(new DialogFact(InstallModsCopy.FactTakesEffect, effect));
        facts.Add(new DialogFact(InstallModsCopy.FactSettings, InstallModsCopy.SettingsKeptValue));
    }

    /// <summary>
    /// CONFIRMTARGET2: at most one short sentence under the facts, only when something needs saying:
    /// a refusal, a fallback to this game, a restart the change waits on, other mods that need it,
    /// or a copy left where this menu cannot reach. Empty otherwise.
    /// </summary>
    internal string PromptNote(CatalogEntry entry, InstallPromptKind kind)
    {
        if (entry == null) return "";
        if (kind == InstallPromptKind.Install)
        {
            string refusal = PromptRefusal ?? "";
            if (refusal.Length > 0) return InstallModsCopy.CannotInstallBody(refusal);
            if (_promptRemoteOnly) return "";
            var resolved = PromptResolvedFor(entry);
            if (resolved.FellBackToClient) return FallbackSentence(resolved.Route);
            if (!_promptPlanReady) return "";
            var plan = _planSet?.Root ?? _lastPlan;
            if (plan == null) return "";
            return EffectOf(plan.Decision, plan.Target, plan.IsMixed) switch
            {
                InstallModsCopy.EffectServerRestart => InstallModsCopy.NoteServerRestart,
                InstallModsCopy.EffectGameRestart => InstallModsCopy.NoteGameRestart,
                InstallModsCopy.EffectBothRestart => InstallModsCopy.NoteBothRestart,
                _ => "",
            };
        }

        if (kind == InstallPromptKind.Uninstall)
        {
            string warning = InstallModsCopy.DependentsWarning(_promptDependents);
            if (warning.Length > 0) return warning;
            if (_promptSide == PromptSide.Client)
                return IsHotLoaded(entry) ? "" : InstallModsCopy.NoteRemovedGameRestart;
            if (_promptSide == PromptSide.Server)
                return CurrentRoute.Kind == ServerRouteKind.Remote || HalvesOf(entry).Server == InstalledState.HalfPending
                       || LocalServerHalfUnloadsNow(entry)
                    ? ""
                    : InstallModsCopy.NoteRemovedServerRestart;
            string sides = UninstallSidesSentence(entry);
            int left = sides.IndexOf("Left in place:", StringComparison.Ordinal);
            if (left >= 0) return sides.Substring(left);
            if (IsHotLoaded(entry) || RemoteRowFor(entry) != null) return "";
            bool serverNow = LocalServerHalfUnloadsNow(entry);
            return InstalledSidesOf(entry) switch
            {
                InstallModsCopy.SidesServer => serverNow ? "" : InstallModsCopy.NoteRemovedServerRestart,
                InstallModsCopy.SidesBoth => serverNow ? InstallModsCopy.NoteRemovedGameRestart : InstallModsCopy.NoteRemovedBothRestart,
                _ => InstallModsCopy.NoteRemovedGameRestart,
            };
        }

        return "";
    }

    /// <summary>
    /// The confirmation's body. An install's is DEPS1's own sentences
    /// (<see cref="InstallSummary.Describe(InstallPlanSet)"/> when the closure was planned, the plain
    /// plan otherwise), so the pane and the bridge say the same words; an uninstall's states what the
    /// removal costs and, when other installs need it, what it breaks (item 6).
    /// </summary>
    internal string PromptBodyText(CatalogEntry entry, InstallPromptKind kind)
    {
        if (kind is InstallPromptKind.Install or InstallPromptKind.Uninstall)
        {
            var (server, both) = PromptServerFor(entry, kind);
            // A refused install names no server: nothing is written anywhere.
            if (kind == InstallPromptKind.Install && (PromptRefusal ?? "").Length > 0) server = null;
            string text = InstallModsCopy.WithServerLine(
                InstallModsCopy.NameLocalServer(PromptBodyCore(entry, kind, server), server), server, both);
            // SRVUNINST1 review: a whole-row Uninstall names the client copy no ledger lists, exactly as its
            // Yes will treat it: removed (its own sentence), or left in place.
            if (kind == InstallPromptKind.Uninstall && _promptSide == PromptSide.Whole)
            {
                var (has, sentence) = _promptClientPlain ?? WholeClientPlain(entry);
                if (has) text = (text + " " + (sentence.Length > 0 ? InstallModsCopy.ThisClientCopy + sentence : InstallModsCopy.ThisClientCopyStays)).Trim();
            }

            return text;
        }

        return PromptBodyCore(entry, kind, null);
    }

    /// <summary>
    /// SRVUNINST1 review: is there a client copy no ledger lists that this row's Uninstall can reach (a
    /// reload-folder DLL, an ordinary plugin), and the sentence of what removing it does (empty: it stays).
    /// </summary>
    internal (bool Has, string Sentence) WholeClientPlain(CatalogEntry entry)
    {
        if (entry == null || HalvesOf(entry).Client != InstalledState.HalfNone) return (false, "");
        if (PlainClientOffer(entry) is not { CanUninstall: true, RemovalPending: false }) return (false, "");
        string sentence = "";
        try { sentence = PlainUninstallSentence?.Invoke(DetectInstalled(entry).PluginId ?? "") ?? ""; }
        catch { sentence = ""; }
        return (true, (sentence ?? "").Trim());
    }

    private string PromptBodyCore(CatalogEntry entry, InstallPromptKind kind, ServerIdentity? server)
    {
        switch (kind)
        {
            case InstallPromptKind.Install:
                return InstallPromptBody(entry, server);
            case InstallPromptKind.Reactivate:
                return InstallModsCopy.ReactivateBody;
            case InstallPromptKind.CancelInstall:
                return InstallModsCopy.CancelInstallBody;
            case InstallPromptKind.Uninstall when _promptSide == PromptSide.Client:
                return (IsHotLoaded(entry) ? InstallModsCopy.UninstallHotBody : InstallModsCopy.UninstallRestartBody)
                    + " " + InstallModsCopy.SettingsKeptBody + DependentsSuffix();
            case InstallPromptKind.Uninstall when _promptSide == PromptSide.Server && _promptForce:
                return RemoteUninstallForce.ConfirmBody(RemoteServer?.LatestFor(entry.Key));
            case InstallPromptKind.Uninstall when _promptSide == PromptSide.Server:
                return (CurrentRoute.Kind == ServerRouteKind.Remote
                        ? InstallModsCopy.RemoteUninstallBody
                        : HalvesOf(entry).Server == InstalledState.HalfPending
                            ? InstallModsCopy.CancelInstallBody
                            : LocalServerHalfUnloadsNow(entry) ? InstallModsCopy.UninstallNowServerBody
                            : InstallModsCopy.UninstallRestartServerBody)
                    + " " + InstallModsCopy.SettingsKeptBody + DependentsSuffix();
            case InstallPromptKind.Uninstall when NeedsBoth(entry) && UninstallSidesSentence(entry).Length > 0:
                return UninstallSidesSentence(entry) + " " + InstallModsCopy.SettingsKeptBody;
            case InstallPromptKind.Uninstall when RemoteRowFor(entry) != null
                                                  && HalvesOf(entry).Client == InstalledState.HalfNone:
                return InstallModsCopy.RemoteUninstallBody + " " + InstallModsCopy.SettingsKeptBody;
            case InstallPromptKind.Uninstall:
                // POLISH2 item 3: a restart is owed by the processes that actually hold the file, so
                // the sentence names those and not always the game.
                // POLISH3 item A: a hot client half whose local-server half is still on disk unloads
                // AND takes that half; the body says both, because one Yes now does both.
                // REQRULE3: a local server copy its reloader holds goes now, as the removal does it.
                bool serverNow = LocalServerHalfUnloadsNow(entry);
                string sidesNow = InstalledSidesOf(entry);
                string body = (IsHotLoaded(entry)
                        ? serverNow ? InstallModsCopy.UninstallHotServerNowBody : InstallModsCopy.UninstallHotBodyFor(HalvesOf(entry).Server)
                        : serverNow && sidesNow == InstallModsCopy.SidesServer ? InstallModsCopy.UninstallNowServerBody
                        : serverNow && sidesNow == InstallModsCopy.SidesBoth ? InstallModsCopy.UninstallServerNowGameLaterBody
                        : InstallModsCopy.UninstallRestartBodyFor(sidesNow))
                    + " " + InstallModsCopy.SettingsKeptBody;
                string warning = InstallModsCopy.DependentsWarning(_promptDependents);
                return warning.Length == 0 ? body : body + " " + warning;
            default:
                return "";
        }
    }

    private string DependentsSuffix()
    {
        string warning = InstallModsCopy.DependentsWarning(_promptDependents);
        return warning.Length == 0 ? "" : " " + warning;
    }

    /// <summary>
    /// INSTALLUX1 item 1: the install confirmation's body through its three lives. While the
    /// download and inspection run behind the open box it is the sentences the listing alone
    /// supports plus <c>Checking the package…</c>; when the plan lands it is DEPS1's full
    /// summary; when the plan refused it is <c>Cannot install — &lt;reason&gt;</c>.
    ///
    /// <para>The side word is the LISTING's (Server / Client / Both), not the target's: with the
    /// target coming from a setting, calling a server mod a "Client mod" because the player has no
    /// dedicated server would be false. When the target fell back to the client for want of a
    /// configured local server, the body says so (item 2).</para>
    /// </summary>
    internal string InstallPromptBody(CatalogEntry entry) => InstallPromptBody(entry, null);

    /// <inheritdoc cref="InstallPromptBody(CatalogEntry)"/>
    internal string InstallPromptBody(CatalogEntry entry, ServerIdentity? server)
    {
        string refusal = PromptRefusal ?? "";
        if (refusal.Length > 0) return InstallModsCopy.CannotInstallBody(refusal);

        var resolved = PromptResolvedFor(entry);
        string side = PromptSideWord(SideOf(entry));
        if (_promptRemoteOnly)
        {
            return InstallSummary.HeadSentence(side, HeaderAuthor(entry), HeaderVersion(entry))
                + " " + (_promptRemoteOp == RemoteInstallOp.Update ? InstallModsCopy.RemoteUpdateBody : InstallModsCopy.RemoteInstallBody);
        }

        string body = !_promptPlanReady
            ? InstallSummary.DescribeChecking(side, HeaderAuthor(entry), HeaderVersion(entry))
            : _planSet != null
                ? InstallSummary.Describe(_planSet, side, server != null && !server.Remote ? server : null)
                : InstallSummary.Describe(_lastPlan, side, server != null && !server.Remote ? server : null);
        if (resolved.FellBackToClient) body += " " + FallbackSentence(resolved.Route);
        else if (resolved.ToConnectedServer) body += " " + InstallModsCopy.RemoteInstallBody;
        return body;
    }

    /// <summary>
    /// REMOTEINST1: the sentence a confirmation adds when the server half fell back to the client.
    /// With no server at all it is the sentence every earlier build wrote.
    /// </summary>
    internal static string FallbackSentence(ServerRoute route)
    {
        if (route.Block is ServerRouteBlock.None or ServerRouteBlock.NoServer) return DefaultInstallTarget.NoLocalServerSentence;
        string reason = route.Reason.TrimEnd('.');
        return reason + "; installing to my client.";
    }

    /// <summary>
    /// INSTALLUX2 item 3: the confirmation's side word — <c>Client and server mod by …</c> for a
    /// both-sided listing. POLISH3 item D folded that wording into
    /// <see cref="InstallSummary.SideWord(PackageSide)"/> itself, so this is now a plain forward and
    /// every caller of either answers the same word. It is kept as the pane's named seam because the
    /// prompt tests and the bridge readback both ask for it by this name.
    /// </summary>
    internal static string PromptSideWord(PackageSide side) => InstallSummary.SideWord(side);

    /// <summary>The confirmation's Yes caption; an uninstall that breaks other installs says so on the button.</summary>
    internal string PromptYesText(InstallPromptKind kind) => kind switch
    {
        InstallPromptKind.Install => IsUpdatePrompt ? InstallModsCopy.PromptYesUpdate : InstallModsCopy.PromptYesInstall,
        InstallPromptKind.Uninstall => _promptDependents.Count > 0
            ? InstallModsCopy.PromptYesUninstallAnyway
            : InstallModsCopy.PromptYesUninstall,
        InstallPromptKind.Reactivate => InstallModsCopy.PromptYesKeep,
        InstallPromptKind.CancelInstall => InstallModsCopy.PromptYesCancel,
        _ => "",
    };

    /// <summary>
    /// The Yes button's requested tone: ok for the two that keep a mod, warn for the two that take one away.
    /// The dialog draws a destructive prompt (<see cref="PromptIsDestructive"/>) in the danger tone instead.
    /// </summary>
    internal static CellTone PromptYesTone(InstallPromptKind kind) => kind switch
    {
        InstallPromptKind.Install or InstallPromptKind.Reactivate => CellTone.Ok,
        _ => CellTone.Warn,
    };

    /// <summary>INSTALL2 item 3 readback: <c>action.prompt {kind, heading, body, yes, no, dependents, chooser, target}</c>.</summary>
    internal static string PromptName(InstallPromptKind kind) => kind switch
    {
        InstallPromptKind.Install => "install",
        InstallPromptKind.Uninstall => "uninstall",
        InstallPromptKind.Reactivate => "reactivate",
        InstallPromptKind.CancelInstall => "cancelInstall",
        _ => "none",
    };

    /// <summary>
    /// INSTALLUX2 item 1: where the confirmation's panel comes from. Production leaves this null
    /// and the dialog is built with <c>Ui.Panel</c>; the game-free tests substitute a fake panel.
    /// A pane that has never been built (no <c>_panel</c>) opens no dialog rather than reaching for
    /// a runtime that is not there.
    /// </summary>
    internal Func<string, IUiPanel>? DialogPanelFactory { get; set; }

    /// <summary>The dialog this pane has on screen, or null. INSTALLUX2 item 1.</summary>
    internal UiDialog? PromptDialog => _promptDialog is { IsOpen: true } open ? open : null;

    private UiDialog? _promptDialog;

    private Func<string, IUiPanel>? DialogFactory()
    {
        var factory = DialogPanelFactory;
        if (factory != null) return factory;
        if (_panel == null) return null;
        return title => Ui.Panel(title);
    }

    /// <summary>
    /// INSTALLUX2 item 1, the user on the INSTALLUX1 build: "it expands like the old behavior asking
    /// me if I want to stall like this instead of popping up a dialogue." The confirmation is a modal
    /// dialog in the middle of the screen now — nothing prompt-shaped is drawn in the row or its
    /// drawer. Only the presentation moved: the state machine
    /// (<see cref="OpenPrompt"/> / <see cref="PromptYes"/> / <see cref="PromptNo"/>), the wording
    /// helpers and the handles (<c>prompt:&lt;key&gt;</c>, <c>promptYes</c> / <c>promptNo</c>, and
    /// <see cref="CellPromptClose"/> for a refusal) are the ones INSTALL2 and INSTALLUX1 published.
    ///
    /// <para>Called again whenever the box's words change — the plan landing behind an open install
    /// box, a refusal replacing the body, a Yes taken before the inspection finished — because the
    /// kit's dialog is built once from the strings it is handed.</para>
    /// </summary>
    private void ShowPromptDialog(CatalogEntry? entry)
    {
        var kind = _state.Prompt;
        if (kind == InstallPromptKind.None || entry == null)
        {
            ClosePromptDialog();
            return;
        }

        var factory = DialogFactory();
        if (factory == null)
        {
            ClosePromptDialog();
            return;
        }

        // INSTALLUX1 item 1: a refused install has nothing to say yes to, so the box keeps one
        // button and it closes — under its own cell key.
        bool refused = kind == InstallPromptKind.Install && PromptRefusal != null;
        string key = entry.Key;
        // CONFIRMTARGET2 (the user: "Make it more readable."): install, update and uninstall boxes
        // draw a facts block and at most one sentence; the others keep their one-line body.
        bool facts = kind is InstallPromptKind.Install or InstallPromptKind.Uninstall;
        // LIMITS1: one side's Install / Update of a mod both sides want offers "on both" too.
        var both = refused ? BothOffer.None : BothOfferFor(entry, kind);
        string yes = refused ? "" : both.Offered ? both.SideOnlyCaption : PromptYesText(kind);
        string note = facts ? PromptNote(entry, kind) : "";
        if (facts && both.Note.Length > 0) note = note.Length > 0 ? note + " " + both.Note : both.Note;
        var extras = both.Offered
            ? new List<(DialogButton, Action)>
            {
                (new DialogButton(CellPromptBoth, both.BothCaption, DialogButtonRole.Option, CellTone.Gold), () =>
                {
                    // The side-only flow closes; the both-sides flow (the row's shortcut) starts.
                    PromptNo();
                    // F39 (live 2026-10-08): this box already named both sides and the timing, so its answer is the Yes:
                    // the both-sides box is answered at once (honored when its plan is ready). It stays on screen only
                    // when that plan is refused, and then says why.
                    if (OnBothClick(key) && _state.Prompt == InstallPromptKind.Install
                        && string.Equals(_state.PromptKey, key, StringComparison.OrdinalIgnoreCase))
                        PromptYes();
                }),
            }
            : null;
        var dialog = UiDialog.Open(
            factory,
            tag: "install",
            kind: PromptName(kind),
            key: key,
            heading: PromptHeadingText(entry, kind),
            body: facts ? "" : PromptBodyText(entry, kind),
            facts: facts ? PromptFacts(entry, kind) : null,
            note: facts ? note : null,
            yesCaption: yes,
            yesTone: PromptYesLabelTone(kind),
            noCaption: refused ? InstallModsCopy.PromptClose : InstallModsCopy.PromptNo,
            rowKey: PromptKeyPrefix + key,
            yesKey: CellPromptYes,
            noKey: refused ? CellPromptClose : CellPromptNo,
            extras: extras,
            danger: !refused && PromptIsDestructive(kind));
        _promptDialog = dialog;
        dialog.OnYes(() =>
        {
            PromptYes();
            // INSTALLUX1 item 1: a Yes taken before the inspection finished leaves the box open,
            // waiting. The kit closed the panel when it answered, so put it back up.
            if (_state.Prompt != InstallPromptKind.None) ReopenPromptDialog();
        });
        dialog.OnNo(() => PromptNo());
    }

    /// <summary>LIMITS1: what the confirmation offers beyond the pressed side.</summary>
    internal readonly struct BothOffer
    {
        public BothOffer(bool offered, string sideOnlyCaption, string bothCaption, string note)
        {
            Offered = offered;
            SideOnlyCaption = sideOnlyCaption ?? "";
            BothCaption = bothCaption ?? "";
            Note = note ?? "";
        }

        public static BothOffer None => new(false, "", "", "");

        /// <summary>The box carries a second yes that acts on both sides.</summary>
        public bool Offered { get; }

        /// <summary>The main yes then reads "Yes, client only" / "Yes, server only".</summary>
        public string SideOnlyCaption { get; }

        /// <summary>"Yes, install on both" / "Yes, update both".</summary>
        public string BothCaption { get; }

        /// <summary>Why the other side is not offered, for a mod both sides want; empty otherwise.</summary>
        public string Note { get; }
    }

    /// <summary>
    /// LIMITS1: the install confirmation opened from one side's Install or Update. It offers the
    /// other side too exactly when the row's own <c>Install on both</c> / <c>Update both</c>
    /// shortcut would (both cells carry the same action, so both routes allow it). A mod both sides
    /// want (manifest-verified) whose other side cannot be acted on says why instead: no server
    /// picked, the private games load this client's copy, admin only, or a route that refuses.
    /// </summary>
    internal BothOffer BothOfferFor(CatalogEntry entry, InstallPromptKind kind)
    {
        if (entry == null || kind != InstallPromptKind.Install) return BothOffer.None;
        // INSTALLSIMPLE1: a Simple both-sides button's box already installs on both.
        if (_promptSimpleBoth) return BothOffer.None;
        bool? server = _promptRemoteOnly ? true
            : _targetOverride == InstallTarget.Client ? false
            : _targetOverride == InstallTarget.LocalServer ? true
            : (bool?)null;
        if (server == null) return BothOffer.None;
        SideCellsDecision sides;
        try { sides = SideCellsFor(entry); }
        catch { return BothOffer.None; }
        string sideOnly = server.Value ? InstallModsCopy.PromptYesServerOnly : InstallModsCopy.PromptYesClientOnly;
        if (sides.Shortcut is SideButton.Install or SideButton.Update)
            return new BothOffer(true, sideOnly,
                sides.Shortcut == SideButton.Update ? InstallModsCopy.PromptYesUpdateBoth : InstallModsCopy.PromptYesInstallBoth, "");

        if (!NeedsBoth(entry)) return BothOffer.None;
        var other = server.Value ? sides.Client : sides.Server;
        bool wants = other.Kind is SideCells.KindNotInstalled or SideCells.KindMissing or SideCells.KindUpdate
            or SideCells.KindOlder or SideCells.KindUnknown or SideCells.KindNoServer or SideCells.KindSameAsClient;
        if (!wants) return BothOffer.None;
        string why = other.Kind == SideCells.KindNoServer ? InstallModsCopy.BothNoServer
            : other.Kind == SideCells.KindSameAsClient ? InstallModsCopy.BothPrivateGames
            : other.Has(SideButton.AdminOnly) || other.Has(SideButton.Authenticate) ? InstallModsCopy.BothAdminOnly
            : other.Note.Length > 0 ? other.Note + "."
            : InstallModsCopy.BothOtherSideBlocked;
        return new BothOffer(false, "", "", why);
    }

    /// <summary>Re-draw the open confirmation from the words it says now.</summary>
    private void ReopenPromptDialog()
    {
        string key = _state.PromptKey ?? "";
        if (key.Length == 0 || !_catalog.TryGetEntry(key, out var entry) || entry == null)
        {
            ClosePromptDialog();
            return;
        }

        ShowPromptDialog(entry);
    }

    private void ClosePromptDialog()
    {
        var dialog = _promptDialog;
        _promptDialog = null;
        dialog?.Close();
    }

    /// <summary>
    /// The Install tab's prompts whose Yes takes something away (Uninstall, and Cancel install, which
    /// deletes the downloaded file): Enter does not answer them and their Yes is drawn in the danger tone.
    /// </summary>
    internal static bool PromptIsDestructive(InstallPromptKind kind) =>
        kind is InstallPromptKind.Uninstall or InstallPromptKind.CancelInstall;

    /// <summary>
    /// <see cref="PromptYesTone"/> in the kit's own tone enum: the dialog block takes a
    /// <see cref="LabelTone"/>, the row cells a <see cref="CellTone"/>, and both say the same thing.
    /// </summary>
    internal static LabelTone PromptYesLabelTone(InstallPromptKind kind) => kind switch
    {
        InstallPromptKind.Install or InstallPromptKind.Reactivate => LabelTone.Ok,
        _ => LabelTone.Warn,
    };

    /// <summary>The confirmation's rows, for the readback and the tests.</summary>
    internal IReadOnlyList<IUiHandle> PromptHandles
    {
        get
        {
            var dialog = PromptDialog;
            if (dialog == null) return Array.Empty<IUiHandle>();
            var list = new List<IUiHandle>(3);
            if (dialog.Prompt.Heading != null) list.Add(dialog.Prompt.Heading);
            if (dialog.Prompt.Body != null) list.Add(dialog.Prompt.Body);
            if (dialog.Prompt.Buttons != null) list.Add(dialog.Prompt.Buttons);
            return list;
        }
    }

    /// <summary>The confirmation's heading row, or null when no box is open.</summary>
    internal IUiLabel? PromptHeadingHandle => PromptDialog?.Prompt.Heading;

    private static IUiLabel AddBlockLabel(IUiPanel panel, MarkdownBlock block)
    {
        string text = block.Text ?? "";
        return block.Kind switch
        {
            MarkdownBlockKind.Heading => panel.Heading(text),
            MarkdownBlockKind.Code => panel.Note(text),
            _ => panel.Label(text),
        };
    }

    /// <summary>Rewrite a row's side cells and the drawer head's install fact in place (the posted rebuild follows).</summary>
    private void SyncActionCells(CatalogEntry entry)
    {
        var action = ActionFor(entry);
        if (_rowIndex.TryGetValue(entry.Key, out int index) && index < _rows.Count)
        {
            // A cell's kind is fixed when it is built, so a slot that had no button cannot grow one
            // here: the words and existing buttons follow now, the posted rebuild does the rest.
            _sideMemo?.Remove(entry.Key);
            _sideCache.Remove(entry.Key);
            var sides = SideCellsFor(entry);
            if (IsSimple)
            {
                // INSTALLSIMPLE1: the Simple row's status and its one button.
                SyncSimpleCells(_rows[index].Row, entry);
            }
            else
            {
                SyncSide(_rows[index].Row, sides.Client, CellClient, CellClientA, CellClientB);
                SyncSide(_rows[index].Row, sides.Server, CellServer, CellServerA, CellServerB);
                string shortcut = SideCells.ShortcutCaption(sides.Shortcut);
                try { _rows[index].Row.SetCell(CellBoth, text: shortcut, visible: shortcut.Length > 0 && IsButtonCell(_rows[index].Row, CellBoth)); }
                catch { /* */ }
            }
        }
        if (_drawerHead != null && string.Equals(_drawerHead.Key, DrawerKeyPrefix + entry.Key, StringComparison.OrdinalIgnoreCase))
        {
            try { _drawerHead.SetCell(CellNote, text: HeadNoteText(entry, action)); }
            catch { /* */ }
        }
    }

    private static bool IsButtonCell(IUiCellRow row, string key)
    {
        var cells = row.Cells;
        for (int i = 0; i < cells.Count; i++)
            if (string.Equals(cells[i].Key, key, StringComparison.Ordinal)) return cells[i].Kind == CellKind.Button;
        return false;
    }

    private void SyncSide(IUiCellRow row, SideCell cell, string textKey, string aKey, string bKey)
    {
        string a = cell.Buttons.Count > 0 ? SideCells.Caption(cell.Buttons[0]) : "";
        string b = cell.Buttons.Count > 1 ? SideCells.Caption(cell.Buttons[1]) : "";
        float width = SideTextWidthFor(SideColumnWidthNow(), a, cell.Buttons.Count > 0 ? cell.Buttons[0] : SideButton.None, b, cell.Buttons.Count > 1 ? cell.Buttons[1] : SideButton.None);
        try
        {
            row.SetCell(textKey, text: InstallColumns.StatusLines(cell.Text, width - GlyphRoom(SideGlyph(cell)), InstallColumns.SideTextFontSize),
                tone: cell.Grayed ? CellTone.Faint : SideTone(cell.Tone));
        }
        catch { /* */ }
        try { row.SetCell(aKey, text: a, visible: a.Length > 0 && IsButtonCell(row, aKey)); }
        catch { /* */ }
        try { row.SetCell(bKey, text: b, visible: b.Length > 0 && IsButtonCell(row, bKey)); }
        catch { /* */ }
    }

    /// <summary>
    /// Show or hide already-built rows and rewrite the toolbar/filter cells in place. Never
    /// rebuilds the panel: the search field stays focused and its handle identity is unchanged.
    /// </summary>
    public void RefilterListings()
    {
        long refilterStart = System.Diagnostics.Stopwatch.GetTimestamp();
        int changedRows = 0;
        UiLayoutBatch.Begin();
        try
        {
            int shown = 0, baseline = 0;
            var entries = AllEntries();
            bool simple = IsSimple;
            for (int i = 0; i < entries.Count; i++)
            {
                if (simple ? SimpleListed(entries[i]) : VisibleAtDefaults(entries[i])) baseline++;
            }
            // LIMITS1: the existing rows are shown or hidden in place — nothing is rebuilt — and a
            // row whose visibility does not change is not touched (no SetActive, no relayout ask).
            for (int i = 0; i < _rows.Count; i++)
            {
                var (row, entry, _) = _rows[i];
                bool match = IsVisible(entry);
                bool was;
                try { was = row.Visible; }
                catch { was = !match; }
                if (was != match)
                {
                    try { row.Visible = match; }
                    catch { /* */ }
                    changedRows++;
                }
                if (match) shown++;
            }
            // A deferred build's rows still to come count as the rows they will be.
            int unbuiltRows = 0;
            if (_unbuilt != null)
            {
                for (int i = 0; i < _unbuilt.Count; i++)
                {
                    if (string.IsNullOrEmpty(_unbuilt[i].Key)) continue;
                    unbuiltRows++;
                    if (IsVisible(_unbuilt[i])) shown++;
                }
            }

            // The drawer follows its row.
            bool drawerVisible = _state.DrawerKey != null
                && _rowIndex.TryGetValue(_state.DrawerKey, out int openIndex)
                && openIndex < _rows.Count
                && IsVisible(_rows[openIndex].Entry);
            for (int i = 0; i < _drawerHandles.Count; i++)
            {
                var handle = _drawerHandles[i];
                bool wanted = drawerVisible && DrawerHandleWanted(handle);
                try { if (handle.Visible != wanted) handle.Visible = wanted; }
                catch { /* */ }
            }

            if (_emptyLabel != null)
            {
                try { _emptyLabel.Visible = shown == 0; }
                catch { /* */ }
            }

            _lastShown = shown;
            _lastTotal = _rows.Count > 0 ? _rows.Count + unbuiltRows : entries.Count;
            _lastFiltered = Math.Max(0, baseline - shown);
            SyncToolbarAndChips();

            // The search cell follows the pane's search (Clear filters, a bridge-set search);
            // a keystroke already wrote the cell, so the typed field is never rewritten.
            var searchCell = SearchCell;
            if (searchCell != null && !string.Equals(searchCell.Text, _search, StringComparison.Ordinal))
            {
                try { _toolbar?.SetCell(CellSearch, text: _search); }
                catch { /* */ }
            }
        }
        finally
        {
            UiLayoutBatch.End();
            NoteRefilter(refilterStart, changedRows);
        }
        RequestVisibleIcons();
    }

    /// <summary>INSTALLSIDES1: the drawer is two rows that always show together.</summary>
    private static bool DrawerHandleWanted(IUiHandle handle) => handle != null;

    // ---- LIMITS1: typing in the search field -------------------------------------------------

    /// <summary>How long after the last keystroke the list re-filters.</summary>
    public const double SearchDebounceSeconds = 0.22;

    /// <summary>A re-filter over this many milliseconds is logged (one frame at 60 fps).</summary>
    public const double RefilterBudgetMs = 16.0;

    private int _searchGeneration;
    private bool _searchPending;

    /// <summary>
    /// A keystroke: re-filter once typing pauses. Each keystroke supersedes the one before (a
    /// generation counter), so a burst of letters costs one re-filter. Without a delayed marshaler
    /// (tests, a pane with no menu) it re-filters at once.
    /// </summary>
    private void ScheduleSearchRefilter()
    {
        var delayed = PostDelayedHook;
        if (delayed == null)
        {
            RefilterListings();
            return;
        }

        int generation = ++_searchGeneration;
        var panel = _panel;
        _searchPending = true;
        try
        {
            delayed(SearchDebounceSeconds, () =>
            {
                if (generation != _searchGeneration || !ReferenceEquals(panel, _panel)) return;
                _searchPending = false;
                RefilterListings();
            });
        }
        catch
        {
            _searchPending = false;
            RefilterListings();
        }
    }

    /// <summary>SEARCHCLEAR1: re-filter now, superseding a re-filter that was waiting for typing to pause.</summary>
    private void RefilterSearchNow()
    {
        _searchGeneration++;
        _searchPending = false;
        RefilterListings();
    }

    /// <summary>
    /// SEARCHCLEAR1: the workspace closed, so the next open starts with an empty search. The rows are
    /// re-filtered in place (the pane is hidden; a later build reads the empty search).
    /// </summary>
    internal void ResetSearchForClose()
    {
        if (_search.Length == 0 && !_searchPending) return;
        _search = "";
        RefilterSearchNow();
    }

    /// <summary>A search re-filter is waiting for typing to pause.</summary>
    internal bool SearchRefilterPending => _searchPending;

    private int _refilters;
    private double _lastRefilterMs;
    private double _maxRefilterMs;
    private int _lastChangedRows;
    private int _overBudget;

    /// <summary>
    /// The typing budget readback (<c>typing</c> on the toolbar): re-filters run, the last and the
    /// largest one's cost in milliseconds (this pane's own work: the visibility writes, the counts
    /// and the one layout the batch runs), how many rows the last one showed or hid, how many went
    /// over <see cref="RefilterBudgetMs"/>, and whether one is waiting for typing to pause.
    /// </summary>
    internal Dictionary<string, object?> TypingToWidget() => new()
    {
        ["refilters"] = _refilters,
        ["lastMs"] = Math.Round(_lastRefilterMs, 2),
        ["maxMs"] = Math.Round(_maxRefilterMs, 2),
        ["lastChangedRows"] = _lastChangedRows,
        ["countsMs"] = Math.Round(_lastCountsMs, 2),
        ["cachedDecisions"] = _sideCache.Count,
        ["overBudget"] = _overBudget,
        ["budgetMs"] = RefilterBudgetMs,
        ["pending"] = _searchPending,
        ["debounceMs"] = SearchDebounceSeconds * 1000.0,
    };

    private void NoteRefilter(long startTicks, int changedRows)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _refilters++;
        _lastRefilterMs = ms;
        _lastChangedRows = changedRows;
        if (ms > _maxRefilterMs) _maxRefilterMs = ms;
        if (ms > RefilterBudgetMs)
        {
            _overBudget++;
            try
            {
                Plugin.Logger?.LogInfo("Install: re-filter took " + ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms ("
                    + changedRows + " rows shown or hidden of " + _rows.Count + "; budget "
                    + RefilterBudgetMs.ToString("0", CultureInfo.InvariantCulture) + " ms).");
            }
            catch { /* a log line */ }
        }
    }

    private void SyncToolbarAndChips()
    {
        if (_toolbar != null)
        {
            try { _toolbar.SetCell(CellCount, text: CountText()); }
            catch { /* */ }
            // DESCCACHE1: the descriptions cell is gone from the toolbar; INSTALLCOLS1: so is the sort button.
        }
        if (_filterRows.Count > 0 && IsSimple) SyncSimpleChips();
        else if (_filterRows.Count > 0)
        {
            var f = _state.Filters;
            SetChip(CellSideAny, f.Side == InstallSideFilter.Any);
            SetChip(CellSideServer, f.Side == InstallSideFilter.Server);
            SetChip(CellSideClient, f.Side == InstallSideFilter.Client);
            SetChip(CellSideBoth, f.Side == InstallSideFilter.Both);
            var counts = ShowCounts();
            SyncShowChip(CellShowAll, InstallModsCopy.ShowAll, InstallShowFilter.All, counts);
            SyncShowChip(CellShowInstalled, InstallModsCopy.ShowInstalled, InstallShowFilter.Installed, counts);
            SyncShowChip(CellShowMissing, InstallModsCopy.ShowMissing, InstallShowFilter.MissingSide, counts);
            SyncShowChip(CellShowUpdate, InstallModsCopy.ShowUpdate, InstallShowFilter.NeedsUpdate, counts);
            SetChip(CellNoRestart, f.NoRestartOnly);
            SetChip(CellCurrent, f.CurrentUpdateOnly && CurrentUpdateTag != null);
            SetFilterCell(CellSummary, text: SummaryText());
            SetFilterCell(CellClear, visible: !AtDefaults);
        }
        MarkHeaderSorted();
    }

    private void SetChip(string key, bool on) => SetFilterCell(key, tone: on ? CellTone.ChipOn : CellTone.ChipOff);

    private void SyncShowChip(string key, string caption, InstallShowFilter show, IReadOnlyDictionary<InstallShowFilter, int> counts)
    {
        int count = counts.TryGetValue(show, out int n) ? n : 0;
        SetFilterCell(key, text: InstallModsCopy.ShowChipCaption(caption, count), tone: ShowChipTone(_state.Filters.Show == show, show, count));
    }

    // ---- Readback ------------------------------------------------------------------------

    /// <summary>
    /// <c>widget_read</c> extras for a handle this pane built: a listing row carries
    /// <c>listing {id, state, restart, side, loadsIn}</c> (with <c>icon {state, cached, error}</c>);
    /// the toolbar <c>count {shown, filtered, total}</c>, <c>descriptions {state, done, total}</c>,
    /// <c>icons {ready, queued, loading, failed, inFlightUrl, lastError, fetches, stalledSeconds}</c> and <c>sort</c>; the
    /// filter row <c>filters {…}</c>; the header <c>sort</c>; every drawer row
    /// <c>drawer: &lt;id&gt;</c> (the head also <c>action {…}</c>, the first README row
    /// <c>readme {…}</c>); the pending bar <c>pending {count}</c>.
    /// </summary>
    internal bool TryDescribeSidebarRow(IUiHandle handle, out Dictionary<string, object?>? extra)
    {
        extra = null;
        if (handle == null) return false;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!ReferenceEquals(handle, _rows[i].Row)) continue;
            extra = new Dictionary<string, object?> { ["listing"] = ListingToWidget(_rows[i].Entry) };
            return true;
        }

        if (ReferenceEquals(handle, _toolbar))
        {
            var (state, done, total) = DescriptionsProgress();
            extra = new Dictionary<string, object?>
            {
                // INSTALLSIDES1: the server picker and what the Server column acts on.
                ["picker"] = ServerPickerProbe == null ? null : PickerToWidget(),
                ["nsfw"] = new Dictionary<string, object?> { ["shown"] = ShowNsfwContent },
                ["count"] = new Dictionary<string, object?> { ["shown"] = _lastShown, ["filtered"] = _lastFiltered, ["total"] = _lastTotal },
                ["descriptions"] = new Dictionary<string, object?> { ["state"] = state, ["done"] = done, ["total"] = total },
                ["sort"] = SortKeyOf(_state.Filters.Sort),
                ["sortDirection"] = InstallColumns.DirectionName(_state.Filters.Sort, _state.Filters.SortReversed),
                ["atDefaults"] = AtDefaults,
                ["search"] = _search,
                ["icons"] = IconsToWidget(),
                // LIMITS1: the typing budget (re-filter cost, debounce pending).
                ["typing"] = TypingToWidget(),
                // INSTALLSIMPLE1: which view is on show and what Simple acts on.
                ["view"] = ViewToWidget(),
                // Settings organization stage 8.
                ["serverSettingsLink"] = ServerSettingsLinkToWidget(),
            };
            if (IsSimple)
            {
                extra["sort"] = SimpleSortName;
                extra["sortDirection"] = SimpleSortDirection;
            }
            return true;
        }

        if (IsSimple && _filterRows.Exists(r => ReferenceEquals(handle, r)))
        {
            extra = new Dictionary<string, object?> { ["filters"] = SimpleFiltersToWidget() };
            return true;
        }

        if (_filterRows.Exists(r => ReferenceEquals(handle, r)))
        {
            var f = _state.Filters;
            var counts = _lastShowCounts ?? ShowCounts();
            int Count(InstallShowFilter show) => counts.TryGetValue(show, out int n) ? n : 0;
            extra = new Dictionary<string, object?>
            {
                ["filters"] = new Dictionary<string, object?>
                {
                    ["side"] = InstallSearchFilters.SideName(f.Side),
                    ["noRestart"] = f.NoRestartOnly,
                    ["currentUpdate"] = f.CurrentUpdateOnly,
                    ["currentUpdateTag"] = CurrentUpdateTag,
                    ["currentUpdateSource"] = CurrentUpdateSource,
                    ["atDefaults"] = AtDefaults,
                    // INSTALLFILTERS1: the Show choice, each choice's count and the summary line.
                    ["show"] = InstallSearchFilters.ShowName(f.Show),
                    ["counts"] = new Dictionary<string, object?>
                    {
                        ["all"] = Count(InstallShowFilter.All),
                        ["installed"] = Count(InstallShowFilter.Installed),
                        ["missing"] = Count(InstallShowFilter.MissingSide),
                        ["update"] = Count(InstallShowFilter.NeedsUpdate),
                    },
                    ["summary"] = SummaryText(),
                    ["rows"] = _filterRows.ConvertAll(r => r.Key),
                },
            };
            return true;
        }

        if (ReferenceEquals(handle, _header) && IsSimple)
        {
            extra = new Dictionary<string, object?>
            {
                ["sort"] = SimpleSortName,
                ["sortDirection"] = SimpleSortDirection,
                ["columns"] = SimpleColumnsToWidget(),
            };
            return true;
        }

        if (ReferenceEquals(handle, _header))
        {
            extra = new Dictionary<string, object?>
            {
                ["sort"] = SortKeyOf(_state.Filters.Sort),
                ["sortDirection"] = InstallColumns.DirectionName(_state.Filters.Sort, _state.Filters.SortReversed),
                ["columns"] = ColumnsToWidget(),
            };
            return true;
        }

        for (int i = 0; i < _drawerHandles.Count; i++)
        {
            if (!ReferenceEquals(handle, _drawerHandles[i])) continue;
            extra = new Dictionary<string, object?> { ["drawer"] = _state.DrawerKey };
            if (ReferenceEquals(handle, _drawerBody))
            {
                extra["drawerBody"] = new Dictionary<string, object?>
                {
                    ["height"] = Math.Round(_drawerBodyHeight, 1),
                    ["lines"] = _drawerLines,
                    ["contentEstimate"] = Math.Round(_drawerContentEstimate, 1),
                    ["listViewport"] = Math.Round(ListViewport, 1),
                    ["blockShare"] = Math.Round(
                        InstallDrawerLayout.BlockHeight(_drawerBodyHeight, CellLayout.TwoLineRowHeight) / Math.Max(1f, ListViewport), 3),
                };
            }
            if (ReferenceEquals(handle, _drawerHead) && _state.DrawerKey != null
                && _catalog.TryGetEntry(_state.DrawerKey!, out var opened) && opened != null)
            {
                // Job LIST4 item 1: the object's home is now listing.action on the row that
                // carries the button. It stays here as an alias for one release so the existing
                // bridge checks that read drawer:<key>.action keep working.
                extra["action"] = ActionToWidget(opened);
                extra["anchoredTo"] = AnchoredToWidget();
            }
            if (ReferenceEquals(handle, _readmeFirst)) extra["readme"] = ReadmeToWidget();
            return true;
        }

        if (_restartBand != null && ReferenceEquals(handle, _restartBand))
        {
            extra = RestartBandWidget();
            return true;
        }

        if (TryDescribeIndexRow(handle, out extra)) return true;

        if (_updatesBand != null && ReferenceEquals(handle, _updatesBand))
        {
            extra = UpdatesBandWidget();
            return true;
        }

        foreach (var (package, row) in _updateRows)
        {
            if (!ReferenceEquals(handle, row)) continue;
            extra = UpdateRowWidget(UpdateRowPrefix + package) ?? new Dictionary<string, object?>();
            return true;
        }

        for (int i = 0; i < _pendingBar.Count; i++)
        {
            if (!ReferenceEquals(handle, _pendingBar[i])) continue;
            // INSTALL2 item 5: both totals, whether the list is expanded, and the list itself.
            var items = new List<Dictionary<string, object?>>();
            var pendingItems = PendingItems();
            for (int j = 0; j < pendingItems.Count; j++)
            {
                items.Add(new Dictionary<string, object?>
                {
                    ["key"] = pendingItems[j].Key,
                    ["state"] = StateName(pendingItems[j].State),
                });
            }

            extra = new Dictionary<string, object?>
            {
                ["pending"] = new Dictionary<string, object?>
                {
                    ["count"] = PendingCount(),
                    ["uninstall"] = PendingUninstallCount(),
                    // POLISH1 defect H: which processes these changes wait on, and therefore which
                    // restart sentence the bar is showing.
                    ["sides"] = PendingSides(),
                    ["open"] = _state.PendingOpen,
                    ["items"] = items,
                    // F20: the one stop request and where it stands.
                    ["stop"] = new Dictionary<string, object?>
                    {
                        ["state"] = _stop.PhaseWord,
                        ["inFlight"] = _stop.InFlight,
                        ["nonce"] = _stop.Nonce,
                        ["line"] = _stop.Line,
                        ["states"] = new List<string>(_stop.States),
                        ["dialogOpen"] = StopDialog != null,
                    },
                },
            };
            return true;
        }

        return false;
    }

    // ---- Private helpers -------------------------------------------------------------------

    private static string[] SplitTerms(string query)
    {
        string[] raw = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (raw.Length == 0) return Array.Empty<string>();
        var terms = new List<string>(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            string t = raw[i].Trim();
            if (t.Length > 0) terms.Add(t);
        }
        return terms.ToArray();
    }

    private static string CollapseWhitespace(string value)
    {
        var chars = new char[value.Length];
        int n = 0;
        bool space = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c))
            {
                if (n == 0 || space) continue;
                chars[n++] = ' ';
                space = true;
                continue;
            }
            chars[n++] = c;
            space = false;
        }
        return n == 0 ? "" : new string(chars, 0, n).TrimEnd();
    }

    private static string PlainUntrusted(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var chars = new char[text.Length];
        int n = 0;
        bool inTag = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (inTag) continue;
            chars[n++] = c;
        }
        return n == text.Length ? text : new string(chars, 0, n);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
        double mb = bytes / (1024d * 1024d);
        return mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrEmpty(value)
        && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>
/// LIST3 item 10: the automatic package-index refresh. The last fetched index is kept under
/// <c>BepInEx/progenitor-cache/index.json</c> (its fetch time in <c>index.stamp</c>) and is
/// served the moment the Mods workspace opens; a check then runs once per open and replaces
/// it when it lands. A later open in the same session checks again only when the cache is older
/// than <see cref="RefreshInterval"/>. Game-free: the fetch and the JSON sink are delegates
/// (<c>InstallCatalogService.LoadIndexFromJson</c> in the client).
///
/// <para>TSTRAFFIC1: in the client the check is a <see cref="ConditionalIndexFetch"/>: a compressed,
/// conditional request that answers 304 (no body) while Thunderstore's index is unchanged, and
/// which owns the disk copy (body, stamp and validators). The delegate constructor (tests, an
/// older wiring) keeps the old behavior: a plain fetch whose body this class writes.</para>
/// </summary>
internal sealed class InstallIndexRefresher
{
    /// <summary>A cached index younger than this is not refetched by a later open in the same session.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    public const string CacheFolderName = "progenitor-cache";
    public const string IndexFileName = "index.json";
    public const string StampFileName = "index.stamp";

    private readonly string? _dir;
    private readonly Func<string, string?, CatalogLoadResult> _loadJson;
    private readonly Func<CancellationToken, Task<CatalogTextFetchResult>> _fetch;
    private readonly ConditionalIndexFetch? _conditional;
    private readonly ICatalogClock _clock;
    private readonly object _gate = new();
    private DateTime? _cacheStampUtc;
    private bool _served;
    private bool _fetchedThisSession;
    private Task<CatalogLoadResult>? _inFlight;

    /// <param name="bepInExRoot">The BepInEx folder the cache lives under; null keeps the cache in memory only.</param>
    /// <param name="loadJson">The catalog's JSON sink (<c>LoadIndexFromJson(json, stamp)</c>).</param>
    /// <param name="fetch">Fetch the index body (<c>CatalogFetcher.FetchTextAsync</c> on the index URL).</param>
    public InstallIndexRefresher(
        string? bepInExRoot,
        Func<string, string?, CatalogLoadResult> loadJson,
        Func<CancellationToken, Task<CatalogTextFetchResult>> fetch,
        ICatalogClock? clock = null)
    {
        _loadJson = loadJson ?? throw new ArgumentNullException(nameof(loadJson));
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _clock = clock ?? new SystemCatalogClock();
        _dir = string.IsNullOrWhiteSpace(bepInExRoot) ? null : Path.Combine(Path.GetFullPath(bepInExRoot), CacheFolderName);
    }

    /// <summary>
    /// TSTRAFFIC1: the client wiring. <paramref name="conditional"/> checks the index (304 reuses the
    /// cached body) and writes the disk copy; its cache must be this root's <c>progenitor-cache</c>.
    /// </summary>
    public InstallIndexRefresher(
        string? bepInExRoot,
        Func<string, string?, CatalogLoadResult> loadJson,
        ConditionalIndexFetch conditional,
        ICatalogClock? clock = null)
        : this(bepInExRoot, loadJson, ct => conditional.FetchAsync(ct), clock)
    {
        _conditional = conditional ?? throw new ArgumentNullException(nameof(conditional));
    }

    /// <summary>TSTRAFFIC1: the conditional check behind this refresher, or null (delegate wiring).</summary>
    public ConditionalIndexFetch? Conditional => _conditional;

    /// <summary>INSTALLERR1: the index URL this refresher reads (its disk copy's), or null (delegate wiring: not known).</summary>
    public string? SourceUrl => _conditional?.Cache.Url;

    // LOCALTS1 (live 2026-10-09): the cache served is the one the conditional check writes, which is per index URL
    // (index.json for thunderstore.io, its own file for the dev stand-in). Before, a dev URL served thunderstore.io's copy.
    public string? IndexPath => _conditional?.Cache.IndexPath ?? (_dir == null ? null : Path.Combine(_dir, IndexFileName));

    public string? StampPath => _conditional?.Cache.StampPath ?? (_dir == null ? null : Path.Combine(_dir, StampFileName));

    /// <summary>True once the on-disk index was handed to the catalog this session.</summary>
    public bool CacheServed { get { lock (_gate) return _served; } }

    /// <summary>True once a fetch completed successfully this session.</summary>
    public bool FetchedThisSession { get { lock (_gate) return _fetchedThisSession; } }

    /// <summary>When the index on show (cached or fetched) was fetched; null when nothing is known.</summary>
    public DateTime? CacheStampUtc { get { lock (_gate) return _cacheStampUtc; } }

    public bool Fetching
    {
        get
        {
            Task? task;
            lock (_gate) task = _inFlight;
            return task != null && !task.IsCompleted;
        }
    }

    /// <summary>
    /// TSTRAFFIC1: carry this session's check over from the refresher a catalog rebuild replaced (a server folder
    /// change, e.g. joining a local server), so the rebuild does not count as a new session and check again. The
    /// new catalog is still served the cache on the next open.
    /// </summary>
    public void InheritSession(InstallIndexRefresher? previous)
    {
        if (previous == null || ReferenceEquals(previous, this)) return;
        bool fetched;
        DateTime? stamp;
        lock (previous._gate)
        {
            fetched = previous._fetchedThisSession;
            stamp = previous._cacheStampUtc;
        }

        lock (_gate)
        {
            _fetchedThisSession |= fetched;
            if (fetched && stamp != null) _cacheStampUtc = stamp;
        }
    }

    /// <summary>The stamp of the index on disk, or null when there is none (or it is unreadable).</summary>
    public bool TryReadCacheStamp(out DateTime stampUtc)
    {
        stampUtc = default;
        string? path = StampPath;
        if (path == null) return false;
        try
        {
            if (!File.Exists(path) || !File.Exists(IndexPath!)) return false;
            string text = File.ReadAllText(path).Trim();
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
            stampUtc = parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Serve the on-disk index once per session: read it, hand it to the catalog, remember
    /// its stamp. False when there is no cache, it is unreadable, or the catalog refused it.
    /// </summary>
    public bool TryServeCache(out CatalogLoadResult result)
    {
        result = new CatalogLoadResult(false, "no cached index", 0, null);
        lock (_gate)
        {
            if (_served) return false;
        }
        if (!TryReadCacheStamp(out var stamp)) return false;
        string json;
        try { json = File.ReadAllText(IndexPath!); }
        catch (Exception e)
        {
            result = new CatalogLoadResult(false, e.Message, 0, null);
            return false;
        }
        var loaded = _loadJson(json, CatalogFetcher.FormatStamp(stamp));
        result = loaded;
        if (!loaded.Ok) return false;
        lock (_gate)
        {
            _served = true;
            _cacheStampUtc = stamp;
        }
        return true;
    }

    /// <summary>
    /// Whether an open should fetch now: the first open of the session always does; a later
    /// one only when the index on show is older than <see cref="RefreshInterval"/> (or nothing
    /// is on show). Never while a fetch is in flight.
    /// </summary>
    public bool ShouldFetch(DateTime utcNow)
    {
        if (Fetching) return false;
        lock (_gate)
        {
            if (!_fetchedThisSession) return true;
            if (_cacheStampUtc == null) return true;
            return utcNow - _cacheStampUtc.Value > RefreshInterval;
        }
    }

    /// <summary>
    /// Fetch the index, write the cache, hand the body to the catalog. One fetch at a time: a
    /// second call while one runs returns the same task. The result is the catalog's answer
    /// (or the fetch failure); a failure leaves the cache and the catalog untouched.
    /// </summary>
    public Task<CatalogLoadResult> RefreshAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken, force: false);

    /// <summary>
    /// The same; <paramref name="force"/> (the player's Retry) goes out even while automatic checks are
    /// paused after repeated failures (<see cref="ConditionalIndexFetch"/>).
    /// </summary>
    public Task<CatalogLoadResult> RefreshAsync(CancellationToken cancellationToken, bool force)
    {
        lock (_gate)
        {
            if (_inFlight != null && !_inFlight.IsCompleted) return _inFlight;
            _inFlight = RefreshCoreAsync(cancellationToken, force);
            return _inFlight;
        }
    }

    private async Task<CatalogLoadResult> RefreshCoreAsync(CancellationToken cancellationToken, bool force)
    {
        CatalogTextFetchResult fetched;
        try
        {
            fetched = _conditional != null
                ? await _conditional.FetchAsync(cancellationToken, force).ConfigureAwait(false)
                : await _fetch(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CatalogLoadResult(false, InstallReasons.FetchCancelled, 0, null);
        }
        catch (Exception e)
        {
            return new CatalogLoadResult(false, e.Message, 0, null) { FailureCause = RecordingCatalogTransport.Classify(e, false) };
        }

        // INSTALLERR1: the cause, the HTTP status and the backoff flag travel with the failure.
        if (fetched == null || !fetched.Ok || fetched.Text == null)
            return new CatalogLoadResult(false, fetched?.Error ?? InstallReasons.FetchTransport, 0, null)
            {
                FailureCause = fetched?.FailureCause ?? FetchFailureCause.None,
                StatusCode = fetched?.StatusCode ?? 0,
                Paused = fetched?.Paused ?? false,
            };

        DateTime now = _clock.UtcNow;
        string stamp = CatalogFetcher.FormatStamp(now);
        var loaded = _loadJson(fetched.Text, stamp);
        if (!loaded.Ok) return loaded;
        // TSTRAFFIC1: the conditional check already wrote (or, on a 304, re-stamped) the disk copy.
        if (_conditional == null) WriteCache(fetched.Text, stamp);
        lock (_gate)
        {
            _served = true;
            _fetchedThisSession = true;
            _cacheStampUtc = now.ToUniversalTime();
        }
        return loaded;
    }

    private void WriteCache(string json, string stamp)
    {
        if (_dir == null) return;
        try
        {
            Directory.CreateDirectory(_dir);
            string tmp = IndexPath! + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(IndexPath!)) File.Delete(IndexPath!);
            File.Move(tmp, IndexPath!);
            File.WriteAllText(StampPath!, stamp);
        }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("Install: index cache write failed: " + e.Message); }
            catch { /* */ }
        }
    }
}

/// <summary>
/// Coalesces panel <c>Relayout</c> into one <c>RunLayout</c> per filter pass. Nested
/// <see cref="Begin"/>/<see cref="End"/> pairs flush only at depth zero, and only
/// when a visibility or text write asked for a layout.
/// </summary>
internal static class UiLayoutBatch
{
    private static int _depth;
    private static int _deferred;
    private static Action? _flush;

    public static void BindFlush(Action? flush) => _flush = flush;

    public static void Begin() => _depth++;

    public static void End()
    {
        if (_depth > 0) _depth--;
        if (_depth != 0) return;
        if (_deferred == 0) return;
        _deferred = 0;
        _flush?.Invoke();
    }

    /// <summary>
    /// True when the caller should run layout now. False when a batch is open:
    /// the write is recorded and <see cref="End"/> drains once.
    /// </summary>
    public static bool ShouldRunLayout()
    {
        if (_depth <= 0) return true;
        _deferred++;
        return false;
    }

#if PROGENITOR_TESTS
    public static int DeferredForTests => _deferred;

    public static void ResetForTests()
    {
        _depth = 0;
        _deferred = 0;
        _flush = null;
    }
#endif
}
