using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Install;

namespace Progenitor.Ui;

// Settings scopes, step 2 (docs/settings-scopes.md, decided 2026-10-03): the game-free model.
// Scope names and words, the owning half, the availability decision, "when" rules and the two-section
// grouping of the mod page. Nothing here is drawn or sent yet: the page uses it from step 4, the wire
// from step 5. Plain data and pure functions only — no game types, no plugin delegates held.

/// <summary>Which half of a mod owns a setting's value.</summary>
internal enum SettingOwner
{
    /// <summary>The client process registered it; its value is in the player's own cfg.</summary>
    Client = 0,

    /// <summary>The server owns it: a server-wide cfg value, or a per-player value the server keeps.</summary>
    Server = 1,
}

/// <summary>Where this client stands with a server, as far as settings are concerned.</summary>
internal enum SettingConnection
{
    /// <summary>Not in a game on any server.</summary>
    NotConnected = 0,

    /// <summary>Joined (or joining); the Progenitor handshake has not settled yet.</summary>
    Waiting = 1,

    /// <summary>Joined a server that did not answer the Progenitor handshake.</summary>
    NotProgenitor = 2,

    /// <summary>Joined a server whose Progenitor handshake was accepted.</summary>
    Progenitor = 3,
}

/// <summary>Three-valued evidence: known yes, known no, or not known.</summary>
internal enum SettingEvidence
{
    Unknown = 0,
    Yes = 1,
    No = 2,
}

/// <summary>What a row does when the half it needs is absent or unreachable (author's choice).</summary>
internal enum AloneBehavior
{
    /// <summary>Grayed with the framework's reason (the default).</summary>
    Gray = 0,

    /// <summary>Not drawn at all: the setting makes no sense without the other half.</summary>
    Hide = 1,
}

/// <summary>What the page does with one row.</summary>
internal enum SettingRowState
{
    /// <summary>Drawn and editable.</summary>
    Editable = 0,

    /// <summary>Drawn with its value, read-only, with the <c>Admin only</c> note: a permission state, not grayed.</summary>
    AdminOnly = 1,

    /// <summary>Drawn grayed with a reason.</summary>
    Grayed = 2,

    /// <summary>Not drawn (author's <see cref="AloneBehavior.Hide"/>, or a false show-when rule).</summary>
    Hidden = 3,
}

/// <summary>Scope words and helpers.</summary>
internal static class SettingScopes
{
    /// <summary>The readback word for a scope: <c>personal</c> or <c>server-wide</c>.</summary>
    public static string Word(SettingScope scope) => scope == SettingScope.ServerWide ? "server-wide" : "personal";

    /// <summary>The readback word for an owner: <c>client</c> or <c>server</c>.</summary>
    public static string Word(SettingOwner owner) => owner == SettingOwner.Server ? "server" : "client";

    /// <summary>
    /// The scope's name as readbacks wrote it before the aliases existed (<c>Local</c> / <c>Server</c>).
    /// <see cref="SettingScope"/> now has two names per value, so <c>ToString()</c> no longer says which
    /// one comes back; readbacks use this instead and stay byte-for-byte as they were.
    /// </summary>
    public static string LegacyName(SettingScope scope) => scope == SettingScope.ServerWide ? "Server" : "Local";

    /// <summary>
    /// The owning half a declaration implies. A Server-wide setting is always the server's, whichever
    /// process declared it (a client half's synced bind only supplies its schema and default).
    /// </summary>
    public static SettingOwner OwnerOf(SettingScope scope, bool declaredByServer) =>
        scope == SettingScope.ServerWide || declaredByServer ? SettingOwner.Server : SettingOwner.Client;
}

/// <summary>Everything one row's availability depends on. Plain values, so the matrix is testable.</summary>
internal readonly struct SettingAvailabilityFacts
{
    public SettingScope Scope { get; init; }
    public SettingOwner Owner { get; init; }
    public SettingConnection Connection { get; init; }

    /// <summary>The server said it can share settings (its settings manifest capability). Unknown before it said anything.</summary>
    public SettingEvidence ServerSharesSettings { get; init; }

    /// <summary>The server's settings manifest arrived for this join.</summary>
    public bool ManifestArrived { get; init; }

    /// <summary>The mod's server part runs on the joined server: Yes (named), No (proven absent, or paused for this server), Unknown.</summary>
    public SettingEvidence ServerPart { get; init; }

    /// <summary>The server's type and version for this setting match the client's (false: version mismatch).</summary>
    public bool Compatible { get; init; }

    /// <summary>The server authorized this player for server-wide edits (admin list or adminauth).</summary>
    public bool CanEditServerWide { get; init; }

    /// <summary>A client-owned row whose effect needs the mod's server part (<c>needsServerPart</c>).</summary>
    public bool NeedsServerPart { get; init; }

    /// <summary>The author's enabled-when rule holds (true when there is none).</summary>
    public bool AuthorEnabled { get; init; }

    /// <summary>The author's sentence for a false enabled-when rule; empty uses the default.</summary>
    public string? AuthorReason { get; init; }

    /// <summary>The author's visible-when rule holds (true when there is none).</summary>
    public bool AuthorVisible { get; init; }

    public AloneBehavior WhenAlone { get; init; }

    /// <summary>Facts with nothing in the way: a joined Progenitor server whose manifest names the mod.</summary>
    public static SettingAvailabilityFacts Ready(SettingScope scope, SettingOwner owner) => new()
    {
        Scope = scope,
        Owner = owner,
        Connection = SettingConnection.Progenitor,
        ServerSharesSettings = SettingEvidence.Yes,
        ManifestArrived = true,
        ServerPart = SettingEvidence.Yes,
        Compatible = true,
        CanEditServerWide = false,
        AuthorEnabled = true,
        AuthorVisible = true,
    };
}

/// <summary>One row's decided state, its reason code and the words the help line shows.</summary>
internal readonly struct SettingAvailabilityResult
{
    public SettingAvailabilityResult(SettingRowState state, string code, string reason)
    {
        State = state;
        Code = code ?? "";
        Reason = reason ?? "";
    }

    public SettingRowState State { get; }

    /// <summary>The readback code (<c>not-connected</c>, <c>admin-only</c>, …); empty when editable.</summary>
    public string Code { get; }

    /// <summary>The sentence for the help line; empty when editable.</summary>
    public string Reason { get; }

    public bool Editable => State == SettingRowState.Editable;
}

/// <summary>
/// The framework's availability decision for one settings row (docs/settings-scopes.md,
/// "Availability reasons"). Worst first: the first reason that applies wins.
/// </summary>
internal static class SettingAvailability
{
    public const string NotConnectedCode = "not-connected";
    public const string WaitingCode = "waiting";
    public const string NotProgenitorCode = "not-progenitor";
    public const string ServerTooOldCode = "server-too-old";
    public const string NeedsServerPartCode = "needs-server-part";
    public const string VersionMismatchCode = "version-mismatch";
    public const string AuthorCode = "author";
    public const string AdminOnlyCode = "admin-only";
    public const string HiddenCode = "hidden";

    public const string NotConnectedReason = "Not connected to a server.";
    public const string WaitingReason = "Waiting for the server…";
    public const string NotProgenitorReason = "Server doesn't run Progenitor.";
    public const string ServerTooOldReason = "Server's Progenitor can't share settings yet.";
    public const string NeedsServerPartReason = "Needs the server part of this mod.";
    public const string VersionMismatchReason = "Server runs a different version of this mod.";
    public const string AuthorDefaultReason = "Not available right now.";
    public const string AdminOnlyReason = "Only server admins can change this.";

    /// <summary>The control-column note on an admin-only row: <c>Admin only · &lt;value&gt;</c>.</summary>
    public const string AdminOnlyNote = "Admin only";

    public static SettingAvailabilityResult Decide(in SettingAvailabilityFacts facts)
    {
        if (!facts.AuthorVisible) return new(SettingRowState.Hidden, HiddenCode, "");

        bool serverOwned = facts.Owner == SettingOwner.Server || facts.Scope == SettingScope.ServerWide;
        string code = serverOwned ? ServerOwnedBlock(facts) : facts.NeedsServerPart ? ServerPartBlock(facts) : "";

        if (code.Length > 0)
        {
            if (facts.WhenAlone == AloneBehavior.Hide && MeansAlone(code)) return new(SettingRowState.Hidden, code, "");
            return new(SettingRowState.Grayed, code, ReasonFor(code));
        }

        if (!facts.AuthorEnabled)
        {
            string sentence = string.IsNullOrWhiteSpace(facts.AuthorReason) ? AuthorDefaultReason : facts.AuthorReason!.Trim();
            return new(SettingRowState.Grayed, AuthorCode, sentence);
        }

        if (facts.Scope == SettingScope.ServerWide && !facts.CanEditServerWide)
            return new(SettingRowState.AdminOnly, AdminOnlyCode, AdminOnlyReason);

        return new(SettingRowState.Editable, "", "");
    }

    /// <summary>The help-line sentence for a code (empty for an unknown or editable code).</summary>
    public static string ReasonFor(string code) => code switch
    {
        NotConnectedCode => NotConnectedReason,
        WaitingCode => WaitingReason,
        NotProgenitorCode => NotProgenitorReason,
        ServerTooOldCode => ServerTooOldReason,
        NeedsServerPartCode => NeedsServerPartReason,
        VersionMismatchCode => VersionMismatchReason,
        AuthorCode => AuthorDefaultReason,
        AdminOnlyCode => AdminOnlyReason,
        _ => "",
    };

    /// <summary>
    /// Codes that mean the other half is absent or unreachable, where an author's
    /// <see cref="AloneBehavior.Hide"/> applies. A wait, a version mismatch and the author's own rule
    /// are not "alone": the row stays drawn, grayed.
    /// </summary>
    public static bool MeansAlone(string code) =>
        code is NotConnectedCode or NotProgenitorCode or ServerTooOldCode or NeedsServerPartCode;

    private static string ServerOwnedBlock(in SettingAvailabilityFacts facts)
    {
        string connection = ConnectionBlock(facts.Connection);
        if (connection.Length > 0) return connection;
        if (facts.ServerSharesSettings == SettingEvidence.No) return ServerTooOldCode;
        if (!facts.ManifestArrived) return WaitingCode;
        if (facts.ServerPart == SettingEvidence.No) return NeedsServerPartCode;
        // With the manifest in hand, a mod it does not name is absent: Unknown means the same here.
        if (facts.ServerPart == SettingEvidence.Unknown) return NeedsServerPartCode;
        if (!facts.Compatible) return VersionMismatchCode;
        return "";
    }

    // A client-owned needsServerPart row: today's rule, unknown grays (only positive evidence enables it).
    private static string ServerPartBlock(in SettingAvailabilityFacts facts)
    {
        string connection = ConnectionBlock(facts.Connection);
        if (connection.Length > 0) return connection;
        return facts.ServerPart == SettingEvidence.Yes ? "" : NeedsServerPartCode;
    }

    private static string ConnectionBlock(SettingConnection connection) => connection switch
    {
        SettingConnection.NotConnected => NotConnectedCode,
        SettingConnection.Waiting => WaitingCode,
        SettingConnection.NotProgenitor => NotProgenitorCode,
        _ => "",
    };
}

/// <summary>One settings row as the grouping sees it.</summary>
internal readonly struct ScopedRow
{
    public ScopedRow(string id, SettingScope scope, string authorSection, SettingRowState state)
    {
        Id = id ?? "";
        Scope = scope;
        AuthorSection = (authorSection ?? "").Trim();
        State = state;
    }

    public string Id { get; }
    public SettingScope Scope { get; }

    /// <summary>The author's section title (<c>ctx.Settings.Page(...).Section</c>, a <c>.Settings</c> group, General, Keys…); empty for none.</summary>
    public string AuthorSection { get; }

    public SettingRowState State { get; }
}

/// <summary>An author subheading inside a scope section, and its rows in declared order.</summary>
internal sealed class ScopedSubsection
{
    public ScopedSubsection(string title) => Title = title ?? "";

    /// <summary>The subheading; empty draws none (rows before any section, or a title the scope heading already says).</summary>
    public string Title { get; }

    public List<ScopedRow> Rows { get; } = new();
}

/// <summary>One of the page's two scope sections.</summary>
internal sealed class ScopedSection
{
    public ScopedSection(SettingScope scope, string heading, string line)
    {
        Scope = scope;
        Heading = heading;
        Line = line;
    }

    public SettingScope Scope { get; }

    /// <summary><c>Your settings</c> or <c>Server settings</c>.</summary>
    public string Heading { get; }

    /// <summary>The one line under the heading (Server settings: <c>Applies to everyone on …</c>); empty for none.</summary>
    public string Line { get; }

    public List<ScopedSubsection> Subsections { get; } = new();

    public int RowCount
    {
        get
        {
            int n = 0;
            foreach (var sub in Subsections) n += sub.Rows.Count;
            return n;
        }
    }
}

/// <summary>
/// The mod page's two sections (decided 2026-10-03): <b>Your settings</b> (Personal) then
/// <b>Server settings</b> (Server-wide), one list, no tabs. The author's sections become subheadings
/// inside the scope their rows belong to, in declared order; hidden rows are dropped; an empty
/// subheading or section is omitted.
/// </summary>
internal static class SettingScopeSections
{
    public const string YourHeading = "Your settings";
    public const string ServerHeading = "Server settings";
    public const string AppliesPrefix = "Applies to everyone on ";
    public const string AppliesNotConnected = "Applies to everyone on the server you join";

    /// <summary>
    /// Author section titles that only restate a scope heading. Under the new headings they would read
    /// <c>Your settings › Personal</c>, so the subheading is dropped and the rows keep their order.
    /// </summary>
    internal static readonly string[] RedundantTitles =
    {
        "Personal", "Your settings", "Client", "Local", "Server", "Server settings", "Server-wide", "Server wide",
    };

    /// <summary>
    /// <c>Applies to everyone on &lt;server&gt;</c>: the server's full name when known, else its address; when not
    /// connected, <see cref="AppliesNotConnected"/>.
    /// </summary>
    public static string ServerLine(bool connected, string? serverName, string? address)
    {
        if (!connected) return AppliesNotConnected;
        return AppliesPrefix + ServerIdentity.Connected(serverName, address).ShortName;
    }

    public static IReadOnlyList<ScopedSection> Group(IEnumerable<ScopedRow> rows, string serverLine)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        var personal = new ScopedSection(SettingScope.Personal, YourHeading, "");
        var server = new ScopedSection(SettingScope.ServerWide, ServerHeading, serverLine ?? "");
        foreach (var row in rows)
        {
            if (row.State == SettingRowState.Hidden) continue;
            var section = row.Scope == SettingScope.ServerWide ? server : personal;
            string title = IsRedundant(row.AuthorSection) ? "" : row.AuthorSection;
            ScopedSubsection? target = null;
            foreach (var sub in section.Subsections)
                if (string.Equals(sub.Title, title, StringComparison.OrdinalIgnoreCase)) { target = sub; break; }
            if (target == null) section.Subsections.Add(target = new ScopedSubsection(title));
            target.Rows.Add(row);
        }

        var result = new List<ScopedSection>(2);
        if (personal.RowCount > 0) result.Add(personal);
        if (server.RowCount > 0) result.Add(server);
        return result;
    }

    public static bool IsRedundant(string? title)
    {
        string t = (title ?? "").Trim();
        if (t.Length == 0) return true;
        foreach (var word in RedundantTitles)
            if (string.Equals(t, word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
