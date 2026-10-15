using System;
using System.Collections.Generic;
using Progenitor.Install;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// MODSPAGE1: the three states the mod page's status label reports. A hot-loadable Progenitor mod
/// never shows the two pending states — it is installed the moment it loads and gone the moment it
/// is removed (today's verified behavior, docs/mods-menu.md).
/// </summary>
internal enum ModPageState
{
    /// <summary>Green in the mock: loaded now.</summary>
    Installed = 0,

    /// <summary>Yellow: the file is written (pending or promoted) and this process has not loaded it.</summary>
    PendingLoad = 1,

    /// <summary>Orange: still loaded, removed at the next restart.</summary>
    PendingUninstall = 2,
}

/// <summary>Which y/n prompt the page has open.</summary>
internal enum ModPromptKind
{
    None = 0,
    Uninstall = 1,
    Reactivate = 2,
    CancelInstall = 3,
}

/// <summary>Readback names for <see cref="ModPageState"/> and <see cref="ModPromptKind"/>.</summary>
internal static class ModPageStates
{
    internal const string Installed = "installed";
    internal const string PendingLoad = "pendload";
    internal const string PendingUninstall = "penduninst";

    internal static string Name(ModPageState state) => state switch
    {
        ModPageState.PendingLoad => PendingLoad,
        ModPageState.PendingUninstall => PendingUninstall,
        _ => Installed,
    };

    internal const string PromptUninstall = "uninstall";
    internal const string PromptReactivate = "reactivate";
    internal const string PromptCancel = "cancel";

    internal static string Name(ModPromptKind kind) => kind switch
    {
        ModPromptKind.Uninstall => PromptUninstall,
        ModPromptKind.Reactivate => PromptReactivate,
        ModPromptKind.CancelInstall => PromptCancel,
        _ => "",
    };

    /// <summary>The prompt an action button opens for a state.</summary>
    internal static ModPromptKind PromptFor(ModPageState state) => state switch
    {
        ModPageState.PendingUninstall => ModPromptKind.Reactivate,
        ModPageState.PendingLoad => ModPromptKind.CancelInstall,
        _ => ModPromptKind.Uninstall,
    };

    /// <summary>
    /// The state of one Mods row, from evidence only: the uninstall marker INSTALL2 writes, then
    /// whether the ledger names the plugin at all, then whether this process actually has it loaded.
    /// A hot-loaded copy is loaded by definition, so it is never <see cref="ModPageState.PendingLoad"/>.
    /// </summary>
    internal static ModPageState Decide(bool uninstallPending, bool ledgered, bool loadedHere, bool hot)
    {
        if (uninstallPending) return ModPageState.PendingUninstall;
        if (hot) return ModPageState.Installed;
        if (ledgered && !loadedHere) return ModPageState.PendingLoad;
        return ModPageState.Installed;
    }

    /// <summary>
    /// POLISH2 item 2: the same decision from the install-ledger rows themselves, so the Mods
    /// sidebar reads the facts the Install tab reads rather than "ledgered and not loaded here".
    /// The order is <see cref="InstalledState"/>'s own (<c>DetectFromLedger</c>):
    /// <list type="number">
    /// <item>the uninstall marker wins;</item>
    /// <item>a copy this process has loaded is installed;</item>
    /// <item>a <c>progenitor-reload/</c> copy on <em>this</em> root that this process has not loaded
    /// is still pending — that is the promoted-but-not-loaded case;</item>
    /// <item>a promoted <c>mod</c> copy on the local-server root is installed: the local server
    /// started with it (<see cref="InstalledState.IsPromotedServerLedgerRow"/>), which is exactly the
    /// live defect — ECF and AutoLatchGates read <c>pendload</c> in the sidebar while the Install tab
    /// already read them <c>installed</c> with <c>halves.server: loaded</c>;</item>
    /// <item>a file still in <c>progenitor-pending/</c> is pending;</item>
    /// <item>anything else the ledger names, with no path to judge by (the old text ledger), keeps
    /// the previous answer: pending.</item>
    /// </list>
    /// </summary>
    internal static ModPageState DecideFromLedger(
        bool uninstallPending,
        IReadOnlyList<InstalledLedgerRow>? rows,
        bool loadedHere,
        bool hot = false)
    {
        if (uninstallPending) return ModPageState.PendingUninstall;
        if (hot || loadedHere) return ModPageState.Installed;
        if (rows == null || rows.Count == 0) return ModPageState.Installed;
        bool serverPromoted = false;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!InstalledState.IsServerLedgerRow(row) && InstalledState.IsReloadLedgerRow(row))
                return ModPageState.PendingLoad;
            if (InstalledState.IsPromotedServerLedgerRow(row)) serverPromoted = true;
        }

        // A pending file, and a row the old text ledger wrote with no path at all, both stay pending.
        return serverPromoted ? ModPageState.Installed : ModPageState.PendingLoad;
    }

    internal static string WordOf(ModPageState state) => state switch
    {
        ModPageState.PendingLoad => ModPageCopy.StatusPendingLoad,
        ModPageState.PendingUninstall => ModPageCopy.StatusPendingUninstall,
        _ => ModPageCopy.StatusInstalled,
    };

    /// <summary>The sidebar marker for a state, or empty for the plain installed row.</summary>
    internal static string DotOf(ModPageState state) => state switch
    {
        ModPageState.PendingLoad => ModPageCopy.DotPendingLoad,
        ModPageState.PendingUninstall => ModPageCopy.DotPendingUninstall,
        _ => "",
    };
}

/// <summary>
/// MODSPAGE1: the listing facts the mod page shows for one installed plugin — version, release
/// date, author, one-liner, store page and README. Built from a <see cref="CatalogEntry"/> the
/// Install index already carries; <see cref="None"/> when no listing names the plugin, and then the
/// page shows what the inventory knows and "no description available".
/// </summary>
internal sealed class ModPageListing
{
    internal static ModPageListing None { get; } = new();

    private ModPageListing()
    {
    }

    internal string Key { get; private init; } = "";
    internal string Owner { get; private init; } = "";
    internal string PackageName { get; private init; } = "";
    internal string Version { get; private init; } = "";

    /// <summary>The release date as the header shows it (the date half of <c>date_created</c>).</summary>
    internal string Released { get; private init; } = "";

    internal string Author { get; private init; } = "";
    internal string OneLiner { get; private init; } = "";

    /// <summary>The Thunderstore package page (<c>package_url</c>), or empty.</summary>
    internal string StoreUrl { get; private init; } = "";

    internal string ReadmeMarkdown { get; private init; } = "";
    internal PackageSide Side { get; private init; } = PackageSide.Unknown;

    /// <summary>The package's manifest declares it is needed on the server as well.</summary>
    internal bool RequiresBoth { get; private init; }

    internal bool Resolved => Key.Length > 0;

    /// <summary>
    /// Adapt one catalog entry. <paramref name="readmeMarkdown"/> comes from the catalog's document
    /// cache or the on-disk README cache; a miss is empty, never an error. No rating is carried (the
    /// user's decision of 2026-09-29: likes and ratings are removed; download counts stay).
    /// </summary>
    internal static ModPageListing FromEntry(CatalogEntry? entry, string? readmeMarkdown)
    {
        if (entry == null) return None;
        var derived = PackageSideDerivation.Derive(entry);
        return new ModPageListing
        {
            Key = entry.Key ?? "",
            Owner = entry.Owner ?? "",
            PackageName = entry.PackageName ?? "",
            Version = entry.Manifest.Version ?? "",
            Released = DateOnly(entry.ReleasedUtc ?? entry.DateUpdated),
            Author = entry.Author ?? entry.Owner ?? "",
            OneLiner = entry.Description ?? "",
            StoreUrl = entry.ListingUrl ?? "",
            ReadmeMarkdown = readmeMarkdown ?? "",
            Side = derived.Side,
            // Only a Progenitor manifest can say "required on both sides"; an adapted Thunderstore
            // row cannot, so its guessed Both is never read as a requirement.
            RequiresBoth = entry.Provenance == CatalogProvenance.ManifestVerified
                && entry.Side == PackageSide.Both,
        };
    }

    /// <summary>The ISO stamp's date half. Anything unparsable is passed through trimmed.</summary>
    internal static string DateOnly(string? stamp)
    {
        string s = (stamp ?? "").Trim();
        if (s.Length == 0) return "";
        int t = s.IndexOf('T');
        if (t >= 10) return s.Substring(0, t);
        return s.Length > 10 ? s.Substring(0, 10) : s;
    }
}

/// <summary>
/// MODSPAGE1: everything the mod page draws for one row, decided from evidence and with no Unity
/// type in sight. The pane turns this into rows; the readback reports the same values, so the page
/// and <c>mods_ui widget_read</c> cannot disagree.
/// </summary>
internal sealed class ModPageModel
{
    private readonly string _oneLinerFallback;

    internal ModPageModel(
        string pluginId,
        string name,
        string version,
        ModProvenance provenance,
        ModPageState state,
        ModPageListing listing,
        bool hot,
        bool clientHalf,
        bool serverHalf,
        bool hasProgenitorSettings,
        bool actionable,
        string? oneLinerFallback = null,
        ListingMatchKind matched = ListingMatchKind.None)
    {
        Matched = listing == null || !listing.Resolved ? ListingMatchKind.None : matched;
        _oneLinerFallback = (oneLinerFallback ?? "").Trim();
        PluginId = pluginId ?? "";
        Name = string.IsNullOrWhiteSpace(name) ? PluginId : name.Trim();
        Version = (version ?? "").Trim();
        Provenance = provenance;
        State = state;
        Listing = listing ?? ModPageListing.None;
        Hot = hot;
        ClientHalf = clientHalf;
        ServerHalf = serverHalf;
        HasProgenitorSettings = hasProgenitorSettings;
        Actionable = actionable;
    }

    internal string PluginId { get; }
    internal string Name { get; }

    /// <summary>The version the page shows: the listing's when known, else what the inventory observed.</summary>
    internal string Version { get; }

    internal ModProvenance Provenance { get; }
    internal ModPageState State { get; }
    internal ModPageListing Listing { get; }

    /// <summary>This copy is loaded by the reloader, so its removal is a hot unload.</summary>
    internal bool Hot { get; }

    /// <summary>The client root holds this package (loaded or pending).</summary>
    internal bool ClientHalf { get; }

    /// <summary>The server half exists: the local-server root holds it, or the joined server listed it.</summary>
    internal bool ServerHalf { get; }

    internal bool HasProgenitorSettings { get; }

    /// <summary>
    /// Only a Progenitor-ledgered install, or (FWALLOW1) a file in the reload folder, offers an action.
    /// Another tool's file, the loader and this framework's own plugins stay read-only (MODS1/MODS2),
    /// whatever the mock draws for them.
    /// </summary>
    internal bool Actionable { get; }

    /// <summary>FWALLOW1: the reload-folder DLL a <see cref="ModProvenance.Reload"/> page's Uninstall deletes.</summary>
    internal string ReloadFile { get; init; } = "";

    /// <summary>FWALLOW1: the other plugins that DLL declares, which go with it.</summary>
    internal IReadOnlyList<string> AlsoRemoves { get; init; } = Array.Empty<string>();

    /// <summary>
    /// LINK1: which rule found <see cref="Listing"/> — the readback's <c>page-listing.matched</c>.
    /// <see cref="ListingMatchKind.None"/> whenever no listing was resolved at all.
    /// </summary>
    internal ListingMatchKind Matched { get; }

    /// <summary>The readback name of <see cref="Matched"/>.</summary>
    internal string MatchedName => ListingMatchKinds.NameOf(Matched);

    /// <summary>
    /// ABOUTSRC1: the listing the resolution found, whether or not it is shown — the readback's
    /// <c>page-listing.key</c>. Empty when none was found.
    /// </summary>
    internal string CandidateKey { get; init; } = "";

    /// <summary>ABOUTSRC1: the found listing's version (the readback's <c>page-listing.listingVersion</c>).</summary>
    internal string CandidateVersion { get; init; } = "";

    /// <summary>ABOUTSRC1: which rule found <see cref="CandidateKey"/> (<see cref="ListingMatchKind.None"/> for none).</summary>
    internal ListingMatchKind CandidateMatched { get; init; } = ListingMatchKind.None;

    /// <summary>
    /// ABOUTSRC1: what ties the found listing to these files (<see cref="InstalledListingCheck"/>). Only
    /// <see cref="ListingVerification.Ledger"/> and <see cref="ListingVerification.Manifest"/> put it in
    /// <see cref="Listing"/>; an unverified one is not shown at all.
    /// </summary>
    internal ListingVerification Verification { get; init; } = ListingVerification.None;

    private readonly ModAboutText _about;

    /// <summary>
    /// ABOUTSRC1: the About section's text and its source (<see cref="ModAboutText.Choose"/>). Unset, it is
    /// <see cref="Listing"/>'s README: the model's listing is only ever one the caller verified.
    /// </summary>
    internal ModAboutText About
    {
        get => _about.Source != ModAboutSource.None
            ? _about
            : ModAboutText.Choose(Listing.Resolved, Listing.ReadmeMarkdown, null);
        init => _about = value;
    }

    internal string StateName => ModPageStates.Name(State);

    internal string StatusWord => ModPageStates.WordOf(State);

    internal string StatusLine => ModPageCopy.StatusLine(StatusWord);

    /// <summary>The action button's caption, or null on a read-only row.</summary>
    internal string? ActionLabel => !Actionable
        ? null
        : State switch
        {
            ModPageState.PendingUninstall => ModPageCopy.CancelUninstall,
            ModPageState.PendingLoad => ModPageCopy.CancelInstall,
            _ => ModPageCopy.Uninstall,
        };

    internal ModPromptKind PromptKind => ModPageStates.PromptFor(State);

    /// <summary>
    /// SRVUNINST1: what Uninstall does per side, decided from evidence before the confirmation opens.
    /// Null (tests, an older wiring) keeps the page's earlier client-shaped behavior.
    /// </summary>
    internal ModUninstallPlan? Uninstall { get; init; }

    /// <summary>
    /// UNINSTREQ1: the connected server requires this mod (<see cref="Networking.RequiredHereFact"/>): the value of the
    /// confirmation's <c>Required by</c> fact, or empty.
    /// </summary>
    internal string RequiredBy { get; init; } = "";

    /// <summary>UNINSTREQ1: the confirmation's facts: <c>Required by</c> when an Uninstall removes this client's copy of a mod the connected server requires.</summary>
    internal IReadOnlyList<DialogFact>? PromptFacts(ModPromptKind kind)
    {
        if (kind != ModPromptKind.Uninstall || RequiredBy.Length == 0) return null;
        // Only when this client's copy goes: a plan that removes only the server's copy leaves the player's.
        if (Uninstall != null && (!Uninstall.CanAct || Uninstall.Client == ClientRemover.None)) return null;
        return new[] { new DialogFact(Networking.RequiredHereFact.Label, RequiredBy, DialogFactTone.Warn) };
    }

    /// <summary>
    /// SRVUNINST1: the action button can act. False only for an Uninstall the plan says nothing can
    /// carry out — the button is then drawn disabled and <see cref="ActionReason"/> says why.
    /// </summary>
    internal bool ActionEnabled => ActionLabel == null || PromptKind != ModPromptKind.Uninstall || Uninstall == null || Uninstall.CanAct;

    /// <summary>Why the action button is disabled; empty while it is enabled.</summary>
    internal string ActionReason => ActionEnabled ? "" : Uninstall?.WhyNot ?? "";


    /// <summary>The package's manifest requires a server component.</summary>
    internal bool RequiresBoth => Listing.RequiresBoth;

    /// <summary>
    /// The warning line's rule: a Progenitor package that needs both sides, with the client half
    /// here and no server half anywhere this client can see.
    /// </summary>
    internal bool ServerHalfMissing => SideLabel == Progenitor.Install.SideStatusRules.MissingOnServer;

    /// <summary>REMOTEINST1: the reverse — the server has it and this client does not.</summary>
    internal bool ClientHalfMissing => SideLabel == Progenitor.Install.SideStatusRules.MissingOnClient;

    /// <summary>
    /// REMOTEINST1: the Install tab's per-side decision over this page's two halves, so the page and
    /// the row never disagree about which half is missing.
    /// </summary>
    private string SideLabel => Progenitor.Install.SideStatusRules.Decide(new Progenitor.Install.SideInputs(
        ClientHalf ? Progenitor.Install.ClientHalfState.Installed : Progenitor.Install.ClientHalfState.Absent, "",
        ServerHalf ? Progenitor.Install.ServerHalfState.Installed : Progenitor.Install.ServerHalfState.Absent, "",
        Progenitor.Install.ServerHalfSource.Unknown, RequiresBoth,
        new Progenitor.Install.ServerRoute(Progenitor.Install.ServerRouteKind.None, Progenitor.Install.ServerRouteBlock.NoServer))).Label;

    /// <summary>The warning line as a reader reads it, marker and all. This is the readback value.</summary>
    internal string? Warning => ServerHalfMissing ? ModPageCopy.WarningServerHalfMissing
        : ClientHalfMissing ? ModPageCopy.WarningClientHalfMissing : null;

    /// <summary>
    /// The same line without its marker: KIT1 draws the <c>(!)</c> as its own red cell in front of
    /// the text, so the text cell must not repeat it.
    /// </summary>
    internal string? WarningBody => ServerHalfMissing ? ModPageCopy.WarningServerHalfMissingBody
        : ClientHalfMissing ? ModPageCopy.WarningClientHalfMissingBody : null;

    /// <summary>
    /// The side badges: what is <b>known</b> about the sides this mod runs on, and nothing else.
    ///
    /// <para><b>MODSPOLISH1</b> (live, BasinHoverTips, 2026-09-13): an unknown side used to draw an
    /// <c>Unknown until checked</c> pill. It said nothing the muted provenance block's
    /// <c>Server requirement unknown</c> did not already say, and it was the only cell on the head
    /// block's third row for a plugin with no listing — so the page drew an empty band under the
    /// meta line to hold it. With no one-liner and no badge
    /// <see cref="HeadBlockLayout.Build"/> emits no third row at all and the band is gone; a page
    /// that has either keeps the row exactly where it was.</para>
    /// </summary>
    internal IReadOnlyList<string> Badges
    {
        get
        {
            if (RequiresBoth)
                return new[] { ModPageCopy.BadgeClient, ModPageCopy.BadgeServerRequired };
            return Listing.Side switch
            {
                PackageSide.Both => new[] { ModPageCopy.BadgeClient, ModPageCopy.BadgeServer },
                PackageSide.Server => new[] { ModPageCopy.BadgeServer },
                PackageSide.Client => new[] { ModPageCopy.BadgeClient },
                _ => Array.Empty<string>(),
            };
        }
    }

    internal string BadgeLine => string.Join(" · ", Badges);

    internal string MetaVersion => Version.Length > 0 ? Version : ModMenuInventory.VersionUnknown;

    internal string MetaReleased => Listing.Released;

    internal string MetaAuthor => Listing.Author;

    /// <summary><c>&lt;version&gt; · &lt;released&gt; · by &lt;author&gt;</c>, dropping what is unknown.</summary>
    internal string MetaLine
    {
        get
        {
            var parts = new List<string>(3) { MetaVersion };
            if (MetaReleased.Length > 0) parts.Add(MetaReleased);
            if (MetaAuthor.Length > 0) parts.Add("by " + MetaAuthor);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The listing's short description, else what the plugin registered with <c>ctx.Menu</c> — a mod
    /// no listing names still says what it is when its author registered a description.
    /// </summary>
    internal string OneLiner => Listing.OneLiner.Length > 0 ? Listing.OneLiner : _oneLinerFallback;

    internal string StoreUrl => Listing.StoreUrl;

    /// <summary><c>page-settings.kind</c>: the settings kit rows, or the "does not expose" sentence.</summary>
    internal string SettingsKind => HasProgenitorSettings ? "progenitor" : "none";

    // ---- prompts --------------------------------------------------------------------------

    internal string PromptHeading(ModPromptKind kind) => kind switch
    {
        ModPromptKind.Reactivate => ModPageCopy.ReactivateHeading(Name, Version),
        ModPromptKind.CancelInstall => ModPageCopy.CancelInstallHeading(Name, Version),
        // REQRULE6: the plan's sides name where the uninstall goes (this game, the server, or both).
        _ => Uninstall is { CanAct: true } plan
            ? ModPageCopy.UninstallHeadingFrom(Name, Version, plan.TargetWords)
            : ModPageCopy.UninstallHeading(Name, Version),
    };

    internal string PromptBody(ModPromptKind kind)
    {
        switch (kind)
        {
            case ModPromptKind.Reactivate:
                return ModPageCopy.ReactivateBody;
            case ModPromptKind.CancelInstall:
                return ModPageCopy.CancelInstallBody;
            default:
                // SRVUNINST1: the plan's body says what each side's remover will do.
                if (Uninstall is { CanAct: true } plan) return plan.Body;
                // FWALLOW1: installed outside the Install tab — say which file goes, and that it goes now.
                if (Provenance == ModProvenance.Reload)
                    return ModPageCopy.UninstallBodyReload(System.IO.Path.GetFileName(ReloadFile), AlsoRemoves);
                string body = Hot ? ModPageCopy.UninstallBodyHot : ModPageCopy.UninstallBody;
                // The local-server half is a second file on a root this process never loads.
                if (ServerHalf && Listing.Side != PackageSide.Client) body += ModPageCopy.UninstallBodyServerHalf;
                return body;
        }
    }

    internal static string PromptYes(ModPromptKind kind) => kind switch
    {
        ModPromptKind.Reactivate => ModPageCopy.ReactivateYes,
        ModPromptKind.CancelInstall => ModPageCopy.CancelInstallYes,
        _ => ModPageCopy.UninstallYes,
    };

    internal static string PromptNo(ModPromptKind kind) => kind switch
    {
        ModPromptKind.Uninstall => ModPageCopy.UninstallNo,
        _ => ModPageCopy.PromptNo,
    };
}
