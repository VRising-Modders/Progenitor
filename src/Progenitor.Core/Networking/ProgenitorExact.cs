using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Networking;

/// <summary>
/// Mod compatibility C4: the Progenitor build a server under <b>Exact version for all</b> requires of every client
/// (docs/mod-compatibility.md, Exact applies to Progenitor itself): its own Core release, by version and sha256. Carried in
/// the catalog form signed for clients that declared <see cref="Handshake.Capability.ProgenitorExact"/>.
/// </summary>
internal sealed class ProgenitorRequirement
{
    internal ProgenitorRequirement(string version, string sha)
    {
        Version = (version ?? "").Trim();
        Sha = (sha ?? "").Trim().ToLowerInvariant();
    }

    /// <summary>The server's Progenitor release (its package version, <c>2026.10.159</c>).</summary>
    public string Version { get; }

    /// <summary>The sha256 of the server's loaded <c>Progenitor.dll</c> (lowercase hex).</summary>
    public string Sha { get; }

    public bool Valid => Version.Length > 0 && Sha.Length == 64;
}

/// <summary>
/// C4, server side: whether this server holds clients to its own Progenitor build now, and why not when it does not.
/// Exact applies to Progenitor only while the server's own Core is obtainable by a client (design: "no client is ever
/// stuck"): a release (no sub-release), named by the self-install record Progenitor wrote when it installed itself, and
/// listed active in Progenitor's signed release list (<see cref="Progenitor.Install.SelfFeed"/>) this server reads.
/// </summary>
internal sealed class ProgenitorExactStatus
{
    public static readonly ProgenitorExactStatus Off = new();

    /// <summary>Version matching is Exact version for all.</summary>
    public bool ModeExact { get; set; }

    /// <summary>The server's own Core is a published release a client can fetch.</summary>
    public bool Obtainable { get; set; }

    /// <summary>Clients are held to this server's Progenitor now (<see cref="ModeExact"/> and <see cref="Obtainable"/>).</summary>
    public bool Enforced => ModeExact && Obtainable;

    /// <summary>
    /// Exact version for all, and the server's own Core is not known yet (its image is read shortly after a Core load,
    /// within <see cref="ProgenitorExactSource.PendingSeconds"/>): a join waits for the answer instead of passing unchecked
    /// (live 2026-10-10).
    /// </summary>
    public bool Pending { get; set; }

    /// <summary>A join waits for the client's feature table: Exact is enforced for Progenitor, or may be once the status is known.</summary>
    public bool HoldsJoins => Enforced || (ModeExact && Pending);

    /// <summary>The server's Progenitor version: the recorded release when known, else the running Core's own version.</summary>
    public string Version { get; set; } = "";

    public string Sha { get; set; } = "";

    /// <summary>Why the server's Core is not obtainable (empty when it is).</summary>
    public string Why { get; set; } = "";

    /// <summary>The requirement the catalog carries, or null when Exact is not enforced for Progenitor.</summary>
    public ProgenitorRequirement? Requirement => Enforced ? new ProgenitorRequirement(Version, Sha) : null;

    /// <summary>The log line and the Server tab note under Exact when it is not enforced; empty otherwise.</summary>
    public string NotEnforcedLine => ModeExact && !Obtainable && !Pending ? ProgenitorExact.NotEnforcedPrefix + Why : "";
}

/// <summary>
/// Mod compatibility C4: Exact for Progenitor itself (docs/mod-compatibility.md, Exact applies to Progenitor itself). Game-free.
/// <list type="bullet">
/// <item>Only <b>Exact version for all</b> applies to Progenitor. Under declared ranges and Same minor for all, Progenitor's
/// protocols and capability bits decide, as before (its versions are dates, so "same minor" would mean "same month").</item>
/// <item>The server compares the client's Core version and sha256 (from its feature table) with its own Core. Bootstrap is not
/// compared: the Bootstrap protocol check at Core load already covers it.</item>
/// <item>A client that sends no feature table (a Progenitor older than mod compatibility C1, or a table that did not arrive in
/// the join hold) cannot show which build it runs and is refused while Exact is enforced for Progenitor.</item>
/// </list>
/// </summary>
internal static class ProgenitorExact
{
    /// <summary>The join screens' row id and the refusal's failing id (the one other Progenitor-wide refusals use).</summary>
    public const string RowId = "progenitor";

    public const string Name = "Progenitor";

    public const string NotEnforcedPrefix = "Exact is not enforced for Progenitor: ";

    /// <summary>
    /// Server: where the status comes from (live: <c>ProgenitorExactLive</c>, from the self-update facts, the index this
    /// server reads and the current Version matching). Null: Exact is never enforced for Progenitor.
    /// </summary>
    internal static Func<ProgenitorExactStatus>? StatusSource { get; set; }

    /// <summary>The server's status now (<see cref="ProgenitorExactStatus.Off"/> without a source or when it throws).</summary>
    internal static ProgenitorExactStatus ServerStatus()
    {
        try { return StatusSource?.Invoke() ?? ProgenitorExactStatus.Off; }
        catch { return ProgenitorExactStatus.Off; }
    }

    /// <summary>
    /// Whether a join waits for the client's feature table: a client that declared
    /// <see cref="Handshake.Capability.VersionRanges"/> (its ranges, C3), and (C4) any client that declared
    /// <see cref="Handshake.Capability.ModFeatures"/> while this server holds clients to its own Progenitor build, because the
    /// table carries the Core version and sha256 the judgment needs. A client that declared neither sends no table and is
    /// judged at once.
    /// </summary>
    internal static bool HoldsForTable(Handshake.Capability clientCaps, bool progenitorExact) =>
        (clientCaps & Handshake.Capability.VersionRanges) != 0
        || (progenitorExact && (clientCaps & Handshake.Capability.ModFeatures) != 0);

    /// <summary>The Server tab and popup line under Exact when it is enforced.</summary>
    public static string EnforcedLine(string version) => "Players also need exactly Progenitor " + version + ", this server's build. "
        + RecheckSentence;

    /// <summary>
    /// Review fix (docs): a player already on the server is judged again when their game answers again mid-session (a mod
    /// hot reload, or the server's re-ask after a Core swap with players online), and is disconnected with the same sentence.
    /// </summary>
    public const string RecheckSentence =
        "Players already on the server are checked again when their game answers again (a mod reload, or a re-ask after this server's Core is swapped).";

    /// <summary>
    /// The admins' Progenitor line (the server's <see cref="JoinAdminState.ProgenitorExactLine"/>) while <paramref name="mode"/>
    /// is Exact version for all (the popup's staged choice, the Server tab's value), else empty.
    /// </summary>
    public static string AdminLine(JoinAdminState? s, string? mode) =>
        s != null && VersionMatchingText.TryParse(mode, out var m) && m == VersionMatchingMode.Exact ? s.ProgenitorExactLine : "";

    public const string PendingWhy = "this server is still reading its own Progenitor build";

    /// <summary>
    /// The server's status for its own Core. <paramref name="releases"/> are Progenitor's versions on the index this server
    /// reads (<see cref="SelfIndex"/>); empty when it has not read one. <paramref name="starting"/>: the Core loaded moments
    /// ago, so an image not read yet is <see cref="ProgenitorExactStatus.Pending"/> rather than a reason not to enforce.
    /// </summary>
    public static ProgenitorExactStatus StatusOf(VersionMatchingMode mode, SelfFacts? facts, IReadOnlyList<SelfRelease>? releases, bool starting = false)
    {
        var s = new ProgenitorExactStatus { ModeExact = mode == VersionMatchingMode.Exact };
        if (facts == null)
        {
            s.Pending = starting;
            s.Why = starting ? PendingWhy : "its own Progenitor could not be read";
            return s;
        }

        string sha = (facts.CoreSha ?? "").Trim().ToLowerInvariant();
        string running = (facts.CoreVersion ?? "").Trim();
        s.Sha = sha;
        s.Version = running;
        if (sha.Length != 64)
        {
            s.Pending = starting;
            s.Why = starting ? PendingWhy : "its Core image could not be read";
            return s;
        }

        if (SelfDetection.IsSubRelease(running))
        {
            s.Why = running + " is a development build";
            return s;
        }

        // The release this Core is: the self-install record (or the update on trial) names it by the Core's sha256.
        string release = "", package = "";
        if (facts.Record != null && string.Equals(facts.Record.CoreSha, sha, StringComparison.OrdinalIgnoreCase))
        {
            release = facts.Record.Version;
            package = facts.Record.Package;
        }
        else if (facts.State?.Journal is { InProgress: true } j && string.Equals(j.NewCore, sha, StringComparison.OrdinalIgnoreCase))
        {
            release = j.Version;
            package = j.Package;
        }

        if (release.Length == 0)
        {
            s.Why = running + " has no install record from a published release, so players could not get the same build";
            return s;
        }

        s.Version = release;
        if (SelfDetection.IsSubRelease(release))
        {
            s.Why = release + " is a development build";
            return s;
        }

        if (releases == null || releases.Count == 0)
        {
            s.Why = "this server has not read Progenitor's release list yet, so it cannot tell whether " + release + " is published";
            return s;
        }

        foreach (var r in releases)
        {
            if (r == null || !r.Active) continue;
            if (!string.Equals(r.Version, release, StringComparison.OrdinalIgnoreCase)) continue;
            // A record from an older package id (a Thunderstore listing) still names the same Progenitor.
            if (!Progenitor.Install.SelfFeed.SamePackage(r.Package, package)) continue;
            s.Obtainable = true;
            return s;
        }

        s.Why = release + " is not in Progenitor's release list";
        return s;
    }

    /// <summary>
    /// Judge a join once this server's status is known (live 2026-10-10: a join in the moments after a Core load passed
    /// unchecked). <paramref name="status"/> is the status read when the answer arrived; while it is
    /// <see cref="ProgenitorExactStatus.Pending"/> it is read again (<paramref name="reread"/>) and, still pending, the
    /// judgment is put off by <paramref name="later"/> (live: <see cref="PendingRetrySeconds"/> on the main thread). The
    /// pending window is bounded (<see cref="ProgenitorExactSource.PendingSeconds"/>), after which the status is final.
    /// <paramref name="current"/> (review fix): false once the answer was superseded (the user answered again or
    /// disconnected); a wait that finds it false ends without judging.
    /// </summary>
    internal static void JudgeWhenKnown(ProgenitorExactStatus status, Func<ProgenitorExactStatus> reread, Action<ProgenitorExactStatus> judge,
        Action<Action> later, Func<bool>? current = null)
    {
        if (status.ModeExact && status.Pending)
        {
            ProgenitorExactStatus now;
            try { now = reread() ?? ProgenitorExactStatus.Off; }
            catch { now = ProgenitorExactStatus.Off; }
            if (now.ModeExact && now.Pending)
            {
                later(() =>
                {
                    if (current != null && !current()) return;
                    JudgeWhenKnown(now, reread, judge, later, current);
                });
                return;
            }

            status = now;
        }

        judge(status);
    }

    /// <summary>How often a join waiting for the server's own status asks again.</summary>
    public const double PendingRetrySeconds = 0.5;

    /// <summary>
    /// The server's judgment of a client's Progenitor, or null when it passes (or Exact is not enforced for Progenitor).
    /// <paramref name="sentTable"/>: the client's feature table arrived (it carries the Core version and sha256);
    /// <paramref name="declaredTable"/>: the client declared <see cref="Handshake.Capability.ModFeatures"/>, so it sends one (a
    /// table that did not arrive within the join hold is a slow table, not an old Progenitor).
    /// </summary>
    public static string? Check(ProgenitorExactStatus? status, bool sentTable, string? clientVersion, string? clientSha, bool declaredTable = false)
    {
        if (status == null || !status.Enforced) return null;
        string want = status.Version;
        string sha = (clientSha ?? "").Trim();
        string have = (clientVersion ?? "").Trim();
        if (sha.Length > 0 && string.Equals(sha, status.Sha, StringComparison.OrdinalIgnoreCase)) return null;
        if (!sentTable && declaredTable)
            return Problem(want, "your Progenitor did not say which build it runs in time; join again");
        if (!sentTable)
            return Problem(want, "your Progenitor is too old to say which build it runs; update Progenitor to join");
        if (sha.Length == 0)
            return Problem(want, have.Length > 0 ? "you have " + have + ", but it did not say which build" : "your Progenitor did not say which build it runs");
        if (have.Length > 0 && string.Equals(SelfDetection.ReleaseOf(have), want, StringComparison.OrdinalIgnoreCase) && !SelfDetection.IsSubRelease(have))
            return Problem(want, "you have another build of " + want);
        return Problem(want, "you have " + (have.Length > 0 ? have : "another build"));
    }

    /// <summary><c>Progenitor: this server requires exactly Progenitor 2026.10.159 (you have 2026.10.158)</c>.</summary>
    public static string Problem(string version, string detail) =>
        Name + ": " + Sentence(version, detail);

    /// <summary><c>this server requires exactly Progenitor 2026.10.159 (you have 2026.10.158)</c>.</summary>
    public static string Sentence(string version, string detail) =>
        "this server requires exactly Progenitor " + version + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")");

    // ---- the client --------------------------------------------------------------------------------------------

    /// <summary>The catalog's requirement, or null when it carries none (or an invalid one).</summary>
    public static ProgenitorRequirement? RequirementOf(ManifestBody? body)
    {
        if (body?.Progenitor == null) return null;
        var req = new ProgenitorRequirement(body.Progenitor, body.ProgenitorSha ?? "");
        return req.Valid ? req : null;
    }

    /// <summary>The client's facts for the Progenitor row (live: <c>SelfUpdates</c>).</summary>
    internal sealed class ClientFacts
    {
        public string CoreVersion { get; set; } = "";
        public string CoreSha { get; set; } = "";

        /// <summary>How this Progenitor was installed (<see cref="SelfDetection.KindOf"/>).</summary>
        public SelfInstallKind Kind { get; set; } = SelfInstallKind.Hand;

        /// <summary>Where the required build can come from: a kept copy, a Thunderstore release, or nowhere.</summary>
        public Func<ProgenitorRequirement, FixSource>? SourceOf { get; set; }

        /// <summary>The version a placement for a server is queued or running for, or empty.</summary>
        public string Placing { get; set; } = "";
    }

    internal enum FixSource
    {
        None = 0,

        /// <summary>The version store keeps a copy with the server's sha256: no download.</summary>
        Kept = 1,

        /// <summary>The release list names the server's release: downloaded from GitHub, then checked against the server's sha256.</summary>
        Thunderstore = 2,
    }

    /// <summary>The row's facts as the join screens draw them.</summary>
    internal sealed class RowFacts
    {
        public JoinRowStatus Status { get; set; }
        public JoinRowAction Action { get; set; }

        /// <summary>The same release number, another build (a development or hand build of the same version).</summary>
        public bool SameVersion { get; set; }

        public FixSource Source { get; set; }

        /// <summary>Why the row cannot be fixed here (Tell the admin), or what Use Progenitor does; empty when ready.</summary>
        public string Note { get; set; } = "";
    }

    public const string NoteDevelopment = "this is a development build of Progenitor; it is never swapped for a server";
    public const string NoteKept = "kept, no download";
    public const string NoteThunderstore = "from GitHub";
    public const string NotePlacing = "being put in place for this server";

    public static string NoteModManager(string version) =>
        "a mod manager installed Progenitor here; install Progenitor " + version + " there";

    public const string NoteChanged = "Progenitor's files changed since Progenitor installed them; it cannot swap them";

    public static string NoteNotListed(string version) => "Progenitor " + version + " is not in the release list here";

    /// <summary>The row for <paramref name="req"/> against this client's own Core (pure).</summary>
    public static RowFacts Decide(ProgenitorRequirement req, ClientFacts? own)
    {
        own ??= new ClientFacts();
        var row = new RowFacts();
        string sha = (own.CoreSha ?? "").Trim();
        if (sha.Length > 0 && string.Equals(sha, req.Sha, StringComparison.OrdinalIgnoreCase))
        {
            row.Status = JoinRowStatus.Ready;
            row.Action = JoinRowAction.Ready;
            return row;
        }

        string have = (own.CoreVersion ?? "").Trim();
        int order = UpdateVersions.Compare(SelfDetection.ReleaseOf(have), req.Version);
        row.SameVersion = order == 0;
        row.Status = order > 0 ? JoinRowStatus.Newer : JoinRowStatus.Older;
        row.Action = JoinRowAction.TellAdmin;
        switch (own.Kind)
        {
            case SelfInstallKind.Development:
                row.Note = NoteDevelopment;
                return row;
            case SelfInstallKind.ModManager:
                row.Note = NoteModManager(req.Version);
                return row;
            case SelfInstallKind.Changed:
                row.Note = NoteChanged;
                return row;
        }

        FixSource source;
        try { source = own.SourceOf?.Invoke(req) ?? FixSource.None; }
        catch { source = FixSource.None; }
        row.Source = source;
        if (source == FixSource.None)
        {
            row.Note = NoteNotListed(req.Version);
            return row;
        }

        row.Action = JoinRowAction.UseProgenitor;
        row.Note = string.Equals(own.Placing, req.Version, StringComparison.OrdinalIgnoreCase) ? NotePlacing
            : source == FixSource.Kept ? NoteKept : NoteThunderstore;
        return row;
    }

    /// <summary>The Use Progenitor caption: <c>Use Progenitor 2026.10.159</c>.</summary>
    public static string UseCaption(string version) => "Use Progenitor " + version;

    /// <summary>The short message Tell the admin copies for the Progenitor row.</summary>
    public static string TellAdminMessage(string server, string version, string? have, string note) =>
        "Hi! " + server + " requires exactly Progenitor " + version + ", but I have " + (string.IsNullOrEmpty(have) ? "another build" : have)
        + " and can't switch to it here" + (string.IsNullOrEmpty(note) ? "" : " (" + note + ")") + ". Could Version matching allow other Progenitor builds?";

    /// <summary>What Use Progenitor says when the swap is queued (it runs at the title screen).</summary>
    public static string Queued(string version, bool atTitle) => atTitle
        ? "Progenitor " + version + " is being put in place for this server. Progenitor reloads at the title screen; join the server again when it says it is ready."
        : "Progenitor " + version + " will be put in place for this server when you are back at the title screen.";

    /// <summary>The title screen notice once the placed build loaded.</summary>
    public static string PlacedNotice(string version) => "Progenitor " + version + " is in place for the server that needs it. Join it again.";

    /// <summary>The title screen notice once the player's own build is back after leaving.</summary>
    public static string RestoredNotice(string version) => "Your Progenitor " + version + " is back after leaving the server that needed another build.";
}

/// <summary>
/// C4, the server's status source: <see cref="ProgenitorExact.StatusOf"/> on the current Version matching, this process's
/// self-update facts and the Progenitor releases of the index it reads, cached for <see cref="CacheSeconds"/> (the catalog
/// scan asks every second, every join asks once). A change of the line under Exact is logged once.
/// Game-free: the probes default to the live sources; the tests replace them.
/// </summary>
internal static class ProgenitorExactSource
{
    public const double CacheSeconds = 2;

    /// <summary>
    /// After the source is installed (a Core load), a Core image not read yet is pending for at most this long; then it is
    /// final (<c>Exact is not enforced for Progenitor: its Core image could not be read</c>).
    /// </summary>
    public const double PendingSeconds = 20;

    private static double _installedAt = double.NaN;

    private static readonly object Gate = new();
    private static ProgenitorExactStatus? _cached;
    private static double _at = double.MinValue;
    private static string _lastLine = "";

    internal static Func<VersionMatchingMode> Mode { get; set; } = () => VersionMatching.Current().Mode;
    internal static Func<SelfFacts?> Facts { get; set; } = () => SelfUpdates.Facts();
    internal static Func<IReadOnlyList<SelfRelease>> Releases { get; set; } = () => SelfUpdates.Releases;
    internal static Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    internal static Action<string>? Log { get; set; }

    /// <summary>Install as <see cref="ProgenitorExact.StatusSource"/>.</summary>
    internal static void Install()
    {
        lock (Gate) _installedAt = Clock();
        ProgenitorExact.StatusSource = Current;
    }

    /// <summary>Within <see cref="PendingSeconds"/> of <see cref="Install"/>.</summary>
    private static bool Starting(double now)
    {
        lock (Gate) return !double.IsNaN(_installedAt) && now >= _installedAt && now - _installedAt < PendingSeconds;
    }

    internal static void Uninstall()
    {
        Func<ProgenitorExactStatus> mine = Current;
        if (ProgenitorExact.StatusSource != null && ProgenitorExact.StatusSource.Method == mine.Method) ProgenitorExact.StatusSource = null;
        Reset();
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            _cached = null;
            _at = double.MinValue;
            _lastLine = "";
            _installedAt = double.NaN;
        }
    }

    /// <summary>The status now; the mode is read on every call (an admin's change applies at once), the rest is cached.</summary>
    internal static ProgenitorExactStatus Current()
    {
        var mode = Mode();
        double now = Clock();
        lock (Gate)
        {
            if (_cached != null && now >= _at && now - _at < CacheSeconds && _cached.ModeExact == (mode == VersionMatchingMode.Exact))
                return _cached;
        }

        var status = ProgenitorExact.StatusOf(mode, Facts(), Releases(), Starting(now));
        // A pending status is not cached: the next read may know the image already.
        if (status.Pending) return status;
        string line = status.NotEnforcedLine.Length > 0 ? status.NotEnforcedLine
            : status.Enforced ? "Exact applies to Progenitor: players need exactly " + status.Version + " (" + Short(status.Sha) + ")" : "";
        bool log;
        lock (Gate)
        {
            _cached = status;
            _at = now;
            log = !string.Equals(line, _lastLine, StringComparison.Ordinal);
            _lastLine = line;
        }

        if (log && line.Length > 0)
        {
            try { Log?.Invoke("VersionMatching: " + line); }
            catch { /* logging only */ }
        }

        return status;
    }

    /// <summary>
    /// The admins' line for Exact version for all, whatever the mode is now: <c>Players also need exactly Progenitor X, this
    /// server's build.</c> when this server's build is obtainable, else <c>Exact is not enforced for Progenitor: …</c>.
    /// </summary>
    internal static string LineIfExact()
    {
        ProgenitorExactStatus status;
        try { status = ProgenitorExact.StatusOf(VersionMatchingMode.Exact, Facts(), Releases(), Starting(Clock())); }
        catch { return ""; }
        return status.Obtainable ? ProgenitorExact.EnforcedLine(status.Version) : status.NotEnforcedLine;
    }

    private static string Short(string sha) => (sha ?? "").Length >= 12 ? sha.Substring(0, 12) : sha ?? "";
}
