using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// INSTALLSIMPLE1 (decided 2026-10-04): the Install mods tab's two views. <see cref="Simple"/>
/// lists just the mods with one button each; <see cref="Advanced"/> is the per-side screen
/// (INSTALLSIDES2, INSTALLFILTERS1) exactly as it was.
/// </summary>
internal enum InstallView
{
    Simple = 0,
    Advanced = 1,
}

/// <summary>INSTALLSIMPLE1: the Simple view's Show chips (single choice).</summary>
internal enum InstallSimpleShow
{
    All = 0,

    /// <summary>A copy on a side Simple acts on (installed, behind, missing a half, waiting on a restart).</summary>
    Installed = 1,

    /// <summary>The row reads <c>↑ Update available</c>.</summary>
    Updates = 2,
}

/// <summary>INSTALLSIMPLE1: the sides one Simple button acts on.</summary>
[Flags]
internal enum InstallSimpleTargets
{
    None = 0,

    /// <summary>This PC (the client's own copy).</summary>
    Client = 1,

    /// <summary>The connected server, when this player may install there.</summary>
    Server = 2,

    Both = Client | Server,
}

/// <summary>
/// INSTALLSIMPLE1: the remembered view, <c>[Mods] InstallView</c> on the shared
/// <c>progenitor.cfg</c> (personal to this BepInEx profile). Anything but
/// <c>Advanced</c> — an empty, missing or unknown value — reads <see cref="InstallView.Simple"/>,
/// the default.
/// </summary>
internal static class InstallViewSetting
{
    public const string ConfigKey = "InstallView";
    public const string SimpleName = "Simple";
    public const string AdvancedName = "Advanced";
    public const InstallView Default = InstallView.Simple;

    public const string Description =
        "Install mods tab view: Simple (just the mods, one button each) or Advanced (per-side columns, server picker and filters). "
        + "Set by the view switch on the tab. Personal to this BepInEx profile.";

    public static InstallView Parse(string? value) =>
        string.Equals((value ?? "").Trim(), AdvancedName, StringComparison.OrdinalIgnoreCase) ? InstallView.Advanced : Default;

    /// <summary>The value written to the cfg.</summary>
    public static string ValueOf(InstallView view) => view == InstallView.Advanced ? AdvancedName : SimpleName;

    /// <summary>The readback name: <c>simple</c> / <c>advanced</c>.</summary>
    public static string Name(InstallView view) => view == InstallView.Advanced ? "advanced" : "simple";

    public static string ShowName(InstallSimpleShow show) => show switch
    {
        InstallSimpleShow.Installed => "installed",
        InstallSimpleShow.Updates => "update",
        _ => "all",
    };

    public static string TargetsName(InstallSimpleTargets targets) => targets switch
    {
        InstallSimpleTargets.Client => "client",
        InstallSimpleTargets.Server => "server",
        InstallSimpleTargets.Both => "both",
        _ => "none",
    };

    /// <summary>The readback list: <c>["client"]</c>, <c>["client", "server"]</c>…</summary>
    public static List<string> TargetsList(InstallSimpleTargets targets)
    {
        var list = new List<string>(2);
        if ((targets & InstallSimpleTargets.Client) != 0) list.Add("client");
        if ((targets & InstallSimpleTargets.Server) != 0) list.Add("server");
        return list;
    }
}

/// <summary>
/// INSTALLSIMPLE1: library and dependency packages, hidden from the Simple view (they still install
/// automatically as a mod's dependency, and Advanced lists them). V Rising's Thunderstore community has
/// no <c>Libraries</c> category (the cached index of 2026-10-04 carries only <c>Mods</c>, <c>Server</c>,
/// <c>Client</c>, <c>Tools</c> and the update tags), so a listing is a library when Thunderstore tags it
/// <c>Libraries</c> / <c>Library</c>, or when another listing in the index depends on it <em>and</em> its
/// one-liner calls it a framework, library or API (a command framework, a UI library…). A mod
/// others depend on that does not describe itself so (a gameplay overhaul, a camera mod) stays a
/// mod. Tools (<see cref="InstallModsPane.IsTool"/>) are hidden separately.
/// </summary>
internal static class InstallLibraries
{
    public static readonly IReadOnlyList<string> LibraryCategories = new[] { "Libraries", "Library" };

    /// <summary>Words of a one-liner that make a depended-on listing a library (whole words, any case).</summary>
    public static readonly IReadOnlyList<string> LibraryWords = new[]
    {
        "framework", "frameworks", "library", "libraries", "lib", "api", "apis",
    };

    public static bool TaggedLibrary(CatalogEntry entry)
    {
        if (entry == null) return false;
        for (int i = 0; i < entry.Categories.Count; i++)
            for (int j = 0; j < LibraryCategories.Count; j++)
                if (string.Equals((entry.Categories[i] ?? "").Trim(), LibraryCategories[j], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Does <paramref name="text"/> call its package a framework, library or API?</summary>
    public static bool DescribesLibrary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int start = -1;
        string t = text!;
        for (int i = 0; i <= t.Length; i++)
        {
            bool letter = i < t.Length && char.IsLetter(t[i]);
            if (letter && start < 0) start = i;
            else if (!letter && start >= 0)
            {
                string word = t.Substring(start, i - start);
                for (int j = 0; j < LibraryWords.Count; j++)
                    if (string.Equals(word, LibraryWords[j], StringComparison.OrdinalIgnoreCase)) return true;
                start = -1;
            }
        }

        return false;
    }

    /// <summary>
    /// The listings some other listing depends on, as <c>owner-name</c> keys (a Thunderstore dependency
    /// string is <c>owner-name-version</c>; neither part may contain a dash).
    /// </summary>
    public static HashSet<string> DependedOn(IReadOnlyList<CatalogEntry> entries)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entries == null) return set;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e == null) continue;
            for (int j = 0; j < e.ForeignDependencies.Count; j++)
            {
                string key = PackageKeyOf(e.ForeignDependencies[j]);
                if (key.Length > 0 && !string.Equals(key, e.Key, StringComparison.OrdinalIgnoreCase)) set.Add(key);
            }
        }

        return set;
    }

    /// <summary><c>dyno-ExampleCommandFramework-0.10.4</c> → <c>dyno-ExampleCommandFramework</c>; empty when malformed.</summary>
    public static string PackageKeyOf(string? dependency)
    {
        string d = (dependency ?? "").Trim();
        int first = d.IndexOf('-');
        int last = d.LastIndexOf('-');
        if (first <= 0 || last <= first + 1) return "";
        return d.Substring(0, last);
    }

    public static bool IsLibrary(CatalogEntry entry, ISet<string>? dependedOn)
    {
        if (entry == null) return false;
        if (TaggedLibrary(entry)) return true;
        if (dependedOn == null || !dependedOn.Contains(entry.Key)) return false;
        return DescribesLibrary(entry.Description);
    }
}

/// <summary>INSTALLSIMPLE1: one Simple row — its status, its one button and the sides that button acts on.</summary>
internal sealed class InstallSimpleRow
{
    public InstallSimpleRow(
        string kind, string words, string glyph, SideCellTone tone, SideButton button, InstallSimpleTargets targets,
        InstallSimpleTargets acted, string hiddenReason = "")
    {
        Kind = kind ?? "";
        Words = words ?? "";
        Glyph = glyph ?? "";
        Tone = tone;
        Button = button;
        Targets = button == SideButton.None ? InstallSimpleTargets.None : targets;
        Acted = acted;
        HiddenReason = hiddenReason ?? "";
    }

    /// <summary>Readback kind (<see cref="InstallSimpleRules"/>' <c>Kind*</c> constants).</summary>
    public string Kind { get; }

    /// <summary>The status words without the glyph (<c>Installed</c>, <c>Update available</c>…).</summary>
    public string Words { get; }

    /// <summary>The mark before the words (<c>✓</c>, <c>↑</c>, <c>!</c>) or empty; drawn only when the game font has it.</summary>
    public string Glyph { get; }

    /// <summary><c>✓ Installed</c>: the glyph, a space and the words (the words alone without a glyph).</summary>
    public string StatusText => Glyph.Length == 0 ? Words : Glyph + " " + Words;

    public SideCellTone Tone { get; }

    /// <summary>The one button, or <see cref="SideButton.None"/>.</summary>
    public SideButton Button { get; }

    /// <summary>The sides the button acts on (none without a button).</summary>
    public InstallSimpleTargets Targets { get; }

    /// <summary>The sides Simple reads for this row (this PC, plus the connected server for its admin).</summary>
    public InstallSimpleTargets Acted { get; }

    /// <summary>Why the row is not listed in Simple (<c>tool</c>, <c>library</c>, <c>server-only</c>…); empty when listed.</summary>
    public string HiddenReason { get; }

    public bool Listed => HiddenReason.Length == 0;

    /// <summary>Counted by the <c>Installed</c> chip: a copy on a side Simple acts on.</summary>
    public bool HasCopy => Kind is InstallSimpleRules.KindInstalled or InstallSimpleRules.KindUpdate
        or InstallSimpleRules.KindMissingServer or InstallSimpleRules.KindMissingClient
        or InstallSimpleRules.KindPending or InstallSimpleRules.KindRemovalPending or InstallSimpleRules.KindManaged;

    public bool NeedsUpdate => Kind == InstallSimpleRules.KindUpdate;

    public static InstallSimpleRow Hidden(string reason) =>
        new(InstallSimpleRules.KindHidden, "", "", SideCellTone.Muted, SideButton.None, InstallSimpleTargets.None, InstallSimpleTargets.None, reason);
}

/// <summary>
/// INSTALLSIMPLE1 (user-approved rule 2026-10-04): the Simple row of one listing, combined from the
/// same <see cref="SideCells.Decide"/> decision the Advanced row draws, so the two views never disagree.
///
/// <para><b>Where Simple acts.</b> Not connected: this PC only. Connected but not that server's admin:
/// this PC only. Connected and the route to that server is open (the existing Server target: the
/// remote route, which only an authorized admin gets, or the local-folder route when the joined server
/// is this PC's configured folder) and the mod has a server half: one button acts on this PC and that
/// server. A server-only mod is listed only when Simple acts on the server.</para>
///
/// <para><b>Combining.</b> Over the sides Simple acts on, first match wins: a running step (its words,
/// its cancel); a pending install (<c>Loads at next start</c> → <c>Cancel install</c>) or a pending
/// removal (<c>Removed at next start</c> → <c>Cancel uninstall</c>); a refused step (its words, a retry
/// when the side offers one); any side behind (<c>↑ Update available</c> → <c>Update</c> on every side
/// offering it); one side installed and another missing (<c>Missing on server</c> / <c>Missing on this
/// PC</c> → <c>Install</c> on the missing side); every side installed (<c>✓ Installed</c> →
/// <c>Uninstall</c> on every side that can); else <c>Not installed</c> → <c>Install</c> on every side
/// offering it.</para>
/// </summary>
internal static class InstallSimpleRules
{
    // ---- Readback kinds --------------------------------------------------------------------------
    public const string KindHidden = "hidden";
    public const string KindNotInstalled = "not-installed";
    public const string KindInstalled = "installed";
    public const string KindUpdate = "update";
    public const string KindMissingServer = "missing-server";
    public const string KindMissingClient = "missing-client";
    public const string KindPending = "pending";
    public const string KindRemovalPending = "removal-pending";
    public const string KindBusy = "busy";
    public const string KindRefused = "refused";
    public const string KindManaged = "managed";
    public const string KindUnknown = "unknown";

    // ---- Hidden reasons --------------------------------------------------------------------------
    public const string HiddenTool = "tool";
    public const string HiddenLoader = "loader";
    public const string HiddenLibrary = "library";
    public const string HiddenServerOnly = "server-only";
    public const string HiddenNoSide = "no-side";
    public const string HiddenOtherUpdate = "other-update";

    // ---- Words -----------------------------------------------------------------------------------
    public const string Installed = "Installed";
    public const string UpdateAvailable = "Update available";
    public const string NotInstalled = SideCells.NotInstalled;
    public const string MissingOnServer = SideStatusRules.MissingOnServer;
    public const string MissingOnThisPc = "Missing on this PC";
    public const string LoadsAtNextStart = "Loads at next start";
    public const string RemovedAtNextStart = "Removed at next start";

    public const string InstalledGlyph = SideCells.InstalledGlyph;
    public const string UpdateGlyph = SideCells.UpdateGlyph;
    public const string MissingGlyph = "!";

    /// <summary>
    /// The Simple row of a listing. <paramref name="sides"/> is the listing's side-cell decision for the
    /// server Simple points at (the connected one, else none); <paramref name="actsOnServer"/> says the
    /// route to that server is open to this player.
    /// </summary>
    public static InstallSimpleRow Decide(SideCellsDecision sides, PackageSide side, bool actsOnServer)
    {
        if (sides == null) throw new ArgumentNullException(nameof(sides));
        var client = sides.Client;
        var server = sides.Server;
        if (client.Kind == SideCells.KindNotInstallable) return InstallSimpleRow.Hidden(HiddenTool);
        if (side == PackageSide.Server && !actsOnServer) return InstallSimpleRow.Hidden(HiddenServerOnly);

        bool clientIn = client.Kind != SideCells.KindNotNeeded && !client.Grayed;
        bool serverIn = actsOnServer && !server.Grayed
            && server.Kind is not (SideCells.KindNotNeeded or SideCells.KindNoServer or SideCells.KindSameAsClient
                or SideCells.KindNotLoaded or SideCells.KindNotInstallable);
        if (!clientIn && !serverIn) return InstallSimpleRow.Hidden(HiddenNoSide);

        var acted = (clientIn ? InstallSimpleTargets.Client : 0) | (serverIn ? InstallSimpleTargets.Server : 0);
        var cells = new List<(SideCell Cell, InstallSimpleTargets Side)>(2);
        if (clientIn) cells.Add((client, InstallSimpleTargets.Client));
        if (serverIn) cells.Add((server, InstallSimpleTargets.Server));

        // 1. A step running on a side: its words and its cancel.
        foreach (var (cell, at) in cells)
        {
            if (cell.Kind != SideCells.KindBusy) continue;
            var button = cell.Buttons.Count > 0 ? cell.Buttons[0] : SideButton.None;
            return new InstallSimpleRow(KindBusy, cell.Text, "", SideCellTone.Pending, button, at, acted);
        }

        // 2. Waiting on a restart: a pending install first, then a pending removal.
        var loads = Offering(cells, c => c.Kind == SideCells.KindPending, SideButton.CancelInstall, out bool anyLoads);
        if (anyLoads)
            return new InstallSimpleRow(KindPending, LoadsAtNextStart, "", SideCellTone.Pending,
                loads != 0 ? SideButton.CancelInstall : SideButton.None, loads, acted);
        var leaves = Offering(cells, c => c.Kind == SideCells.KindRemovalPending, SideButton.CancelUninstall, out bool anyLeaves);
        if (anyLeaves)
            return new InstallSimpleRow(KindRemovalPending, RemovedAtNextStart, "", SideCellTone.Pending,
                leaves != 0 ? SideButton.CancelUninstall : SideButton.None, leaves, acted);

        // 3. A refused step: its words, and its retry where the side keeps one.
        foreach (var (cell, at) in cells)
        {
            if (cell.Kind != SideCells.KindRefused) continue;
            bool retry = cell.Has(SideButton.Install);
            return new InstallSimpleRow(KindRefused, cell.Text, "", cell.Tone, retry ? SideButton.Install : SideButton.None, at, acted);
        }

        // 3b. SRVUNINST1: a refused uninstall the admin may override: its words and Uninstall anyway.
        foreach (var (cell, at) in cells)
        {
            if (!cell.Has(SideButton.UninstallAnyway)) continue;
            return new InstallSimpleRow(KindRefused, RemoteUninstallForce.RefusedWord, "", SideCellTone.Warn, SideButton.UninstallAnyway, at, acted);
        }

        // 4. Any side behind the listing (or behind the other side): Update every side that offers it.
        var updates = Offering(cells, c => c.Kind is SideCells.KindUpdate or SideCells.KindOlder, SideButton.Update, out bool anyBehind);
        if (anyBehind)
            return new InstallSimpleRow(KindUpdate, UpdateAvailable, UpdateGlyph, SideCellTone.Warn,
                updates != 0 ? SideButton.Update : SideButton.None, updates, acted);

        InstallSimpleTargets installed = 0, absent = 0;
        SideCell? managed = null;
        foreach (var (cell, at) in cells)
        {
            if (cell.Kind == SideCells.KindInstalled)
            {
                installed |= at;
                if (cell.Tone == SideCellTone.Muted && managed == null) managed = cell;
            }
            else if (cell.Kind is SideCells.KindNotInstalled or SideCells.KindMissing or SideCells.KindUnknown) absent |= at;
        }

        // 5. One side has it and another lacks it: Install the missing side.
        if (installed != 0 && absent != 0)
        {
            var install = Offering(cells, c => c.Kind is SideCells.KindNotInstalled or SideCells.KindMissing or SideCells.KindUnknown,
                SideButton.Install, out _);
            bool serverMissing = (absent & InstallSimpleTargets.Server) != 0;
            return new InstallSimpleRow(serverMissing ? KindMissingServer : KindMissingClient,
                serverMissing ? MissingOnServer : MissingOnThisPc, MissingGlyph, SideCellTone.Warn,
                install != 0 ? SideButton.Install : SideButton.None, install, acted);
        }

        // 6. Every side has it: Uninstall every side that can.
        if (installed != 0 && installed == acted)
        {
            var uninstall = Offering(cells, _ => true, SideButton.Uninstall, out _);
            if (uninstall == 0 && managed != null)
                return new InstallSimpleRow(KindManaged, ManagedWords(managed.Text), "", SideCellTone.Muted, SideButton.None, 0, acted);
            return new InstallSimpleRow(KindInstalled, Installed, InstalledGlyph, SideCellTone.Ok,
                uninstall != 0 ? SideButton.Uninstall : SideButton.None, uninstall, acted);
        }

        // 7. Not installed anywhere Simple acts: Install every side that offers it.
        if (absent != 0)
        {
            var install = Offering(cells, _ => true, SideButton.Install, out _);
            return new InstallSimpleRow(KindNotInstalled, NotInstalled, "", SideCellTone.Muted,
                install != 0 ? SideButton.Install : SideButton.None, install, acted);
        }

        // Anything else (a state no side cell produces today): the first side's own words.
        var first = cells[0].Cell;
        return new InstallSimpleRow(KindUnknown, first.Text, "", first.Tone, SideButton.None, 0, acted);
    }

    /// <summary>The sides among those matching <paramref name="match"/> whose cell offers <paramref name="button"/>.</summary>
    private static InstallSimpleTargets Offering(
        List<(SideCell Cell, InstallSimpleTargets Side)> cells, Func<SideCell, bool> match, SideButton button, out bool any)
    {
        any = false;
        InstallSimpleTargets targets = 0;
        foreach (var (cell, at) in cells)
        {
            if (!match(cell)) continue;
            any = true;
            if (cell.Has(button)) targets |= at;
        }

        return targets;
    }

    /// <summary><c>Installed 1.0 · Managed by Gale</c> → <c>Managed by Gale</c>.</summary>
    private static string ManagedWords(string text)
    {
        string t = text ?? "";
        int dot = t.LastIndexOf(SideCells.Dot, StringComparison.Ordinal);
        return dot >= 0 ? t.Substring(dot + SideCells.Dot.Length).Trim() : t;
    }

    /// <summary>The button's caption: <c>Install</c>, <c>Update</c>, <c>Uninstall</c>, <c>Cancel install</c>, <c>Cancel uninstall</c>.</summary>
    public static string Caption(SideButton button) => SideCells.Caption(button);
}
