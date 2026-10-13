using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Networking;

/// <summary>
/// Deny-time descriptor and the read-only report behind the disconnect-screen panel (job FMAN17,
/// research <c>mod-manager.md</c> "Deny-time offers", job FMAN16).
///
/// <para><b>The gap this closes.</b> A client kicked for a missing <c>ServerMustHave</c> plugin got
/// a 479-character sentence and a disconnect. It knew <em>what</em> was missing — <c>ServerAsked</c>
/// has held the server's request rows since FMAN13 — and had no way to learn <em>where to get it</em>:
/// no Thunderstore package, no URL, no <c>sha256</c>, no size. A "click for the homepage" link that
/// resolves only for mods you already have fails the same way: the one row that needs a link is
/// the one that cannot get one.</para>
///
/// <para><b>What is sent, and what is not.</b> A signed catalog narrowed to the rows
/// <em>this server's own <c>Check</c></em> rejected this client over, into a state that grants the
/// catalog and nothing else. The denied user is <b>never</b> added to
/// <c>ModManifest.HandshakeOkUsers</c>, so <c>ModManifest.HandleRequest</c> refuses every
/// <c>PrmR</c> from that peer exactly as it did before — no package byte moves on the deny path.
/// Manifest, not payload: the model every stack in FMAN16's survey converges on, and the one
/// BepInEx-shaped frameworks are unanimous about.</para>
///
/// <para><b>Why the panel is on the disconnect screen and not in the pre-kick window.</b> The kick
/// is deferred (<c>HandshakeAnswerWatch.KickFlushSeconds</c>, 2 s), but that delay is <em>not</em>
/// what gets the <c>Result</c> and the descriptor out ahead of it: both sends are synchronous and
/// already with the transport before <c>PostDelayed</c> schedules the disconnect, so frame and
/// reliable-channel ordering carry them and the constant does not (evidence 185, measured at
/// <c>KickFlushSeconds = 0.0</c> on Lidgren; R48 finding 3). What matters for the panel's placement
/// is the other end: after the kick the client sits on <c>ProjectM.UI.ConnectingView</c>'s error
/// screen — <c>ClientState = 7</c>, <c>_ForceShowBackButton</c> — for as long as the player leaves
/// it there. That is a stable state with no deadline in it; a two-second window is not a place to
/// ask a person to read anything.</para>
///
/// <para><b>Every sentence here states only what the code checked.</b> The consequence quoted per
/// row is <c>Handshake.Compare.EnforcedKickReason</c> — the same function the kick path ran, on
/// this client's own record of what the server asked — so the panel and the disconnect box cannot
/// drift by rewording. A row whose source the catalog does not carry says so outright rather
/// than implying one.</para>
/// </summary>
internal static class ModManifestDenyPolicy
{
    /// <summary>Config section and key of the operator switch.</summary>
    public const string ConfigSection = "Networking";

    /// <summary>Config key of the operator switch.</summary>
    public const string ConfigKey = "DenyTimeModDescriptor";

    /// <summary>
    /// Operator switch default: <b>on</b>.
    ///
    /// <para><b>What it discloses is narrower than what the server already sent this peer
    /// unprompted.</b> <c>Handshake.OnRequest</c> hands every connecting client the server's whole
    /// <c>Required</c> list — id, version, level and rule — <em>before</em> the client has proven
    /// anything, and <c>Compare.MismatchKickReason</c> then ships the failing rows' ids and
    /// expected versions inside the 479-character kick string. Narrowed to the rows this client
    /// actually failed, the descriptor adds exactly four fields the peer did not already have:
    /// Thunderstore <c>package</c>, <c>url</c>, <c>sha256</c> and <c>size</c> — publication
    /// metadata that is public by construction for anything on Thunderstore or other mod sites.</para>
    ///
    /// <para><b>The two failure modes are not symmetric.</b> Default-off fails silently: an
    /// operator who never reads the config ships precisely the defect this job exists to fix, and
    /// no log line or player-visible difference tells them. Default-on fails loudly and small: a
    /// stranger who can fail a handshake learns the sha256 and byte size of packages whose ids and
    /// versions the kick string already gave them.</para>
    ///
    /// <para><b>Who should turn it off.</b> An operator running a private build whose
    /// <em>existence</em> is sensitive — the one case where <c>package</c>/<c>url</c> is not
    /// already public and where a <c>sha256</c> lets someone confirm a build they hold. That
    /// operator has by definition thought about it; the operator who has not is the one the
    /// default must protect.</para>
    /// </summary>
    public const bool DefaultEnabled = true;

    /// <summary>Config description written into <c>progenitor.cfg</c>.</summary>
    public const string ConfigDescription =
        "Send the signed mod catalog, narrowed to the rows the client actually failed, to a client "
        + "this server is about to disconnect over a mod mismatch, so its disconnect screen can name "
        + "the package and where to get it. No package bytes are ever sent on this path and the "
        + "denied client is never granted a transfer. Turn it off if the mere existence of a "
        + "required package is sensitive (a private build). Default true.";

    /// <summary>
    /// How many times one join may (re)open the deny panel. A deny happens once per connection
    /// attempt, which the player drives — but the catalog arrives on the server's schedule and a
    /// hostile server can send several inside the two-second window. Bounded for the same reason
    /// <c>ModConsentRateLimit.MaxPromptsPerJoin</c> is.
    /// </summary>
    public const int MaxPanelsPerJoin = 3;

    /// <summary>
    /// The ids out of <c>Compare.ServerEnforcedFailures</c>: the rows this server's own
    /// <c>Check</c> rejected this client over, in the order the kick reason lists them. Null and
    /// empty inputs yield an empty list, and nothing here throws.
    /// </summary>
    internal static List<string> FailingIds(IReadOnlyList<Handshake.Compare.EnforcedFailure>? failures)
    {
        var ids = new List<string>();
        if (failures == null) return ids;
        for (int i = 0; i < failures.Count; i++)
        {
            string id = failures[i].Id;
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// The signed descriptor for one deny: <paramref name="full"/>'s body with <c>Mods</c> reduced
    /// to <paramref name="failedIds"/>, re-signed with <paramref name="key"/>.
    ///
    /// <para>The operator identity, server name and <c>issued</c> stamp are carried through
    /// unchanged. Carrying <c>issued</c> rather than re-stamping matters: a client that later joins
    /// successfully and receives the <em>full</em> catalog of the same generation sees an equal
    /// <c>issued</c>, which the replay gate allows, so a deny can never poison a later join.</para>
    ///
    /// <para>Returns null when there is nothing to send: no envelope, no failing ids, or no
    /// failing id that the catalog has a row for. Never throws for a caller that is decorating a
    /// disconnect — a throw here would cost the kick.</para>
    /// </summary>
    internal static ManifestEnvelope? Narrow(
        ManifestEnvelope? full,
        IReadOnlyList<string>? failedIds,
        ModManifestKey? key)
    {
        if (full?.Body == null || key == null) return null;
        if (failedIds == null || failedIds.Count == 0) return null;

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < failedIds.Count; i++)
        {
            string id = failedIds[i];
            if (!string.IsNullOrEmpty(id)) wanted.Add(id);
        }
        if (wanted.Count == 0) return null;

        var rows = new List<ManifestMod>();
        var all = full.Body.Mods;
        for (int i = 0; i < all.Count; i++)
        {
            var row = all[i];
            if (row == null || string.IsNullOrEmpty(row.Id)) continue;
            if (!wanted.Contains(row.Id)) continue;
            rows.Add(row);
        }
        // C4: a refusal over this server's Progenitor build keeps the signed requirement, even with no mod row.
        bool progenitor = wanted.Contains(ProgenitorExact.RowId) && full.Body.Progenitor != null;
        if (rows.Count == 0 && !progenitor) return null;

        var body = new ManifestBody(
            full.Body.Schema,
            full.Body.ServerName,
            full.Body.Issued,
            full.Body.Operator,
            full.Body.ProfileCode,
            rows)
        {
            // C3: a narrowed extended form keeps the Version matching it was signed with.
            Matching = full.Body.Matching,
            Progenitor = progenitor ? full.Body.Progenitor : null,
            ProgenitorSha = progenitor ? full.Body.ProgenitorSha : null,
        };
        string canonical = ModManifestJson.CanonicalBody(body);
        string signature = key.Sign(ModManifestCrypto.Utf8(canonical));
        // JOINFLOW1: the join section is bound to (issued, fp), which narrowing keeps, so the full
        // catalog's signed section rides along unchanged.
        return full.WithBody(body, signature);
    }

    /// <summary>
    /// JOINFLOW1: a zero-row descriptor carrying only the full catalog's join section (the server message),
    /// for a refused client that declared the join-flow capability when no failing row has a catalog row.
    /// Null when the catalog carries no join section. Re-signed like <see cref="Narrow"/>, with the same
    /// <c>issued</c>, so the replay gate treats it as the same generation.
    /// </summary>
    internal static ManifestEnvelope? JoinOnly(ManifestEnvelope? full, ModManifestKey? key)
    {
        if (full?.Body == null || key == null || full.Join == null) return null;
        var body = new ManifestBody(
            full.Body.Schema,
            full.Body.ServerName,
            full.Body.Issued,
            full.Body.Operator,
            full.Body.ProfileCode,
            Array.Empty<ManifestMod>())
        {
            Matching = full.Body.Matching,
            Progenitor = full.Body.Progenitor,
            ProgenitorSha = full.Body.ProgenitorSha,
        };
        string signature = key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)));
        return full.WithBody(body, signature);
    }
}

/// <summary>Where a deny row's requirement came from. Only these two exist.</summary>
public enum ModDenyOrigin : byte
{
    /// <summary>This server asked for the row and its own <c>Check</c> rejected this client's copy.</summary>
    ServerRequires = 1,

    /// <summary>
    /// This client declared the row <c>ServerMustHave</c> and the server does not have it. The
    /// server cannot supply what it does not run, so no descriptor row can ever exist for this.
    /// </summary>
    YouRequire = 2,
}

/// <summary>One row of the deny report: the requirement, what this client has, and where to get it.</summary>
public sealed class ModDenyRow
{
    public ModDenyRow(
        string id,
        string requiredVersion,
        string? have,
        ModDenyOrigin origin,
        string? enforcedKickReason,
        string? package = null,
        string? url = null,
        string? sha256 = null,
        long size = 0)
    {
        Id = id ?? "";
        RequiredVersion = requiredVersion ?? "";
        Have = string.IsNullOrEmpty(have) ? null : have;
        Origin = origin;
        EnforcedKickReason = string.IsNullOrEmpty(enforcedKickReason) ? null : enforcedKickReason;
        Package = ManifestMod.EmptyToNull(package);
        Url = ManifestMod.EmptyToNull(url);
        Sha256 = ManifestMod.EmptyToNull(sha256);
        Size = size > 0 ? size : 0;
    }

    /// <summary>
    /// Same, plus <paramref name="restartOnly"/>: per-server mod pause could not take this row's
    /// plugin because it was loaded at game start (see <see cref="RestartOnly"/>).
    /// </summary>
    public ModDenyRow(
        string id,
        string requiredVersion,
        string? have,
        ModDenyOrigin origin,
        string? enforcedKickReason,
        bool restartOnly,
        string? package = null,
        string? url = null,
        string? sha256 = null,
        long size = 0)
        : this(id, requiredVersion, have, origin, enforcedKickReason, package, url, sha256, size)
    {
        RestartOnly = restartOnly;
    }

    /// <summary>
    /// True for a <see cref="ModDenyOrigin.YouRequire"/> row whose plugin only a restart can remove
    /// (loaded from <c>BepInEx/plugins</c>, or it took <c>ctx.UnsafeHarmony</c>), so per-server mod
    /// pause left it running. False when it is not known.
    /// </summary>
    public bool RestartOnly { get; }

    public string Id { get; }

    /// <summary>The version the server's own request row named. Never the catalog's.</summary>
    public string RequiredVersion { get; }

    /// <summary>This client's version of the id, or null when it has none.</summary>
    public string? Have { get; }

    public ModDenyOrigin Origin { get; }

    /// <summary>
    /// What <c>Handshake.Compare.Check</c> — the function the kick path itself ran — says about
    /// this id for this client, or null. The only sentence in the row allowed to assert a
    /// consequence.
    /// </summary>
    public string? EnforcedKickReason { get; }

    /// <summary>Thunderstore <c>{ns}-{name}</c> from the signed descriptor, or null.</summary>
    public string? Package { get; }

    /// <summary>Operator-published URL from the signed descriptor, or null.</summary>
    public string? Url { get; }

    /// <summary>SHA-256 of the exact bytes the operator signed for this row, or null.</summary>
    public string? Sha256 { get; }

    /// <summary>Byte size of those bytes, or 0 when the descriptor did not carry one.</summary>
    public long Size { get; }

    /// <summary>True when this client has no copy of the id at all.</summary>
    public bool Missing => Have == null;

    /// <summary>True when the descriptor gave a place a player can actually go.</summary>
    public bool HasSource => Package != null || Url != null;

    /// <summary>Same 16-hex prefix the consent panel prints, so the two agree.</summary>
    public string ShaPrefix => ModText.ShaPrefix(Sha256);

    /// <summary>This row with the descriptor's four actionable fields merged in.</summary>
    internal ModDenyRow WithDescriptor(ManifestMod mod) =>
        mod == null
            ? this
            : new ModDenyRow(
                Id,
                RequiredVersion,
                Have,
                Origin,
                EnforcedKickReason,
                RestartOnly,
                mod.Package,
                mod.Url,
                mod.Sha256,
                mod.Size ?? 0);
}

/// <summary>
/// Everything the disconnect-screen panel shows for one denied join. Built from local state at the
/// deny and re-built once the signed descriptor lands.
/// </summary>
public sealed class ModDenyReport
{
    public ModDenyReport(
        string kickReason,
        string host,
        string serverName,
        string operatorName,
        string fingerprint,
        bool descriptorReceived,
        IReadOnlyList<ModDenyRow> rows)
    {
        KickReason = kickReason ?? "";
        Host = host ?? "";
        ServerName = serverName ?? "";
        OperatorName = operatorName ?? "";
        Fingerprint = fingerprint ?? "";
        DescriptorReceived = descriptorReceived;
        Rows = rows ?? Array.Empty<ModDenyRow>();
    }

    /// <summary>The exact string the disconnect box will show, as it arrived on the wire.</summary>
    public string KickReason { get; }

    /// <summary>The address this client dialed, when it could be captured.</summary>
    public string Host { get; }

    /// <summary>From the signed descriptor; empty when none arrived.</summary>
    public string ServerName { get; }

    /// <summary>From the signed descriptor; empty when none arrived.</summary>
    public string OperatorName { get; }

    /// <summary>From the signed descriptor; empty when none arrived.</summary>
    public string Fingerprint { get; }

    /// <summary>True once a signed, verified descriptor has been merged into these rows.</summary>
    public bool DescriptorReceived { get; }

    public IReadOnlyList<ModDenyRow> Rows { get; }

    /// <summary>How many rows carry a place to get the package.</summary>
    public int SourcedRows
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i].HasSource) n++;
            }
            return n;
        }
    }

    internal ModDenyRow? Find(string? modId)
    {
        if (string.IsNullOrEmpty(modId)) return null;
        for (int i = 0; i < Rows.Count; i++)
        {
            if (string.Equals(Rows[i].Id, modId, StringComparison.OrdinalIgnoreCase)) return Rows[i];
        }
        return null;
    }
}

/// <summary>
/// Every string the deny panel shows, game-free and unit-tested — the same arrangement
/// <see cref="ModConsentText"/> uses, for the same reason: the copy is the part that makes claims,
/// so it is the part a test must be able to read.
/// </summary>
public static class ModDenyText
{
    /// <summary>PLAINDENY1: the plain title; <see cref="PanelTitle"/> adds the server's name.</summary>
    public const string Title = "Can't join this server";

    public const string CloseButton = "Close";

    /// <summary>
    /// The one thing the player must not misread. Accepting is not offered here because it cannot
    /// work: the denied peer is not in <c>HandshakeOkUsers</c>, so the server refuses every package
    /// request from it. Saying so is cheaper than a button that fails.
    /// </summary>
    public const string NothingWasDownloaded =
        "Nothing was downloaded and nothing was installed. This server does not send files to a client it "
        + "refused, so there is no button here that would fetch them — install the mods yourself from the "
        + "sources below, restart V Rising, and connect again.";

    /// <summary>Why a restart is part of the answer, stated once.</summary>
    public const string RestartNote =
        "Progenitor loads plugins at process start, so a mod you install now takes effect the next time you "
        + "start the game — not in this session.";

    public static string PanelTitle(string? serverName) =>
        string.IsNullOrEmpty(serverName) ? Title : "Can't join " + serverName;

    /// <summary>
    /// JOINWORD1: true when every row is this client's own requirement
    /// (<see cref="ModDenyOrigin.YouRequire"/>): this client declared mods <c>ServerMustHave</c>
    /// that the server does not run, so it was this client that stopped the join. Live 2026-09-27:
    /// the server asked about 0 mods and the panel still said "'&lt;server&gt;' refused this connection"
    /// and "The server's reason".
    /// </summary>
    public static bool RefusedByThisClient(ModDenyReport report)
    {
        if (report == null || report.Rows.Count == 0) return false;
        for (int i = 0; i < report.Rows.Count; i++)
        {
            if (report.Rows[i]?.Origin != ModDenyOrigin.YouRequire) return false;
        }
        return true;
    }

    /// <summary>
    /// Per-server mod pause: said under a <see cref="ModDenyOrigin.YouRequire"/> row whose plugin was
    /// loaded at game start, so it could not be paused for this server the way a hot-loaded mod is.
    /// </summary>
    public const string RestartOnlyNote =
        "  this mod was loaded when the game started, so it cannot be paused for one server; to join, "
        + "restart V Rising without it.";

    /// <summary>JOINWORD1: what to do when only this client's own requirements failed.</summary>
    public const string YourModsFix =
        "Nothing is missing on your side, and the server did nothing wrong. To join this server, turn "
        + "these mods off, then connect again: uninstall them, or move their files out of "
        + "BepInEx/progenitor-reload, which unloads a hot-loaded mod without a restart. Put them back "
        + "to use them on a server that runs them.";

    private static string Who(ModDenyReport report) =>
        report.ServerName.Length > 0
            ? "'" + report.ServerName + "'"
            : (report.Host.Length > 0 ? report.Host : "this server");

    /// <summary>The headline: how many rows, and who refused.</summary>
    public static string Headline(ModDenyReport report)
    {
        if (report == null) return "";
        int n = report.Rows.Count;
        if (RefusedByThisClient(report))
        {
            return "Your client stopped this join: " + n.ToString(CultureInfo.InvariantCulture)
                + (n == 1 ? " of your mods needs " : " of your mods need ") + Who(report)
                + " to run " + (n == 1 ? "it" : "them") + " too, and it does not.";
        }

        string who = report.ServerName.Length > 0
            ? "'" + report.ServerName + "'"
            : (report.Host.Length > 0 ? report.Host : "This server");
        if (n == 0) return who + " refused this connection over a mod mismatch.";
        return who + " refused this connection: " + n.ToString(CultureInfo.InvariantCulture)
            + (n == 1 ? " mod does" : " mods do") + " not match.";
    }

    /// <summary>
    /// The disconnect reason, quoted verbatim from the wire so the panel and the game's own error
    /// screen cannot say different things.
    /// </summary>
    public static string KickReasonLine(ModDenyReport report)
    {
        if (report == null || report.KickReason.Length == 0) return "";
        return (RefusedByThisClient(report) ? "Your client's join check: " : "The server's reason: ")
            + report.KickReason;
    }

    /// <summary>
    /// Who signed the descriptor, or that none arrived. An absent descriptor is a state the player
    /// is told about, because it is the difference between "no link exists" and "the server chose
    /// not to send one".
    /// </summary>
    public static string SourceLine(ModDenyReport report)
    {
        if (report == null) return "";
        if (!report.DescriptorReceived)
        {
            return "This server sent no mod catalog with the refusal, so the rows below carry no download "
                + "source. Everything shown comes from the handshake this client answered.";
        }

        return "Sources below come from a catalog signed by operator '" + report.OperatorName
            + "', key " + report.Fingerprint
            + (report.Host.Length > 0 ? ", host " + report.Host : "")
            + ". The signature was checked on this machine; the packages themselves were not.";
    }

    /// <summary>One row: what is required, what you have, and what the join check said.</summary>
    public static string RowLine(ModDenyRow row)
    {
        if (row == null) return "";
        var sb = new StringBuilder();
        sb.Append(row.Id);
        sb.Append(row.Origin == ModDenyOrigin.ServerRequires
            ? "  server requires " + Version(row.RequiredVersion)
            : "  you require " + Version(row.RequiredVersion));
        sb.Append("  you have ").Append(row.Have ?? "nothing");
        if (row.Size > 0) sb.Append("  ").Append(ModText.FormatSize(row.Size));
        if (row.Sha256 != null) sb.Append("  sha256 ").Append(row.ShaPrefix);
        return sb.ToString();
    }

    /// <summary>
    /// The consequence line, quoted from <c>Handshake.Compare.Check</c>. Empty when the check said
    /// nothing about this id, which is the only honest answer available then.
    /// </summary>
    public static string ReasonLine(ModDenyRow row) =>
        row?.EnforcedKickReason == null ? "" : "  join check: " + row.EnforcedKickReason;

    /// <summary>Where to get it, or the plain statement that nothing said where.</summary>
    public static string WhereLine(ModDenyRow row)
    {
        if (row == null) return "";
        if (row.Origin == ModDenyOrigin.YouRequire)
        {
            return "  this server does not run this mod, so it cannot supply it; the requirement is on your "
                + "machine, not theirs.";
        }
        if (row.Url != null) return "  get it from " + row.Url;
        if (row.Package != null) return "  get it from Thunderstore: " + row.Package;
        return "  the server named no source for this mod.";
    }

    /// <summary>
    /// The panel's lines in order, one label each; <see cref="Compose"/> and the panel both read
    /// this, so they cannot say different things. A join this client stopped (JOINWORD1) drops the
    /// catalog, download and restart lines, which are about installing what a server requires,
    /// and says how to turn the client's own mods off instead.
    /// </summary>
    public static IReadOnlyList<string> Lines(ModDenyReport report)
    {
        var lines = new List<string>();
        if (report == null) return lines;
        lines.Add(Headline(report));
        string kick = KickReasonLine(report);
        if (kick.Length > 0) lines.Add(kick);
        if (RefusedByThisClient(report))
        {
            lines.Add(YourModsFix);
        }
        else
        {
            lines.Add(SourceLine(report));
            lines.Add(NothingWasDownloaded);
            lines.Add(RestartNote);
        }

        for (int i = 0; i < report.Rows.Count; i++)
        {
            lines.Add(RowLine(report.Rows[i]));
            string reason = ReasonLine(report.Rows[i]);
            if (reason.Length > 0) lines.Add(reason);
            lines.Add(WhereLine(report.Rows[i]));
            if (report.Rows[i].RestartOnly && report.Rows[i].Origin == ModDenyOrigin.YouRequire)
                lines.Add(RestartOnlyNote);
        }
        return lines;
    }

    // ---- PLAINDENY1: the plain summary the panel leads with -------------------------------
    // Requested 2026-09-28, on the full-text panel: "This message is still not clear enough. It need
    // to be easier to read, less technical garbage." The panel now opens on a few plain sentences
    // and a bullet per mod; everything above (reason text, catalog signature, host, per-row join
    // check) moves behind "Show details" as Details(report).

    /// <summary>The "Show details" / "Hide details" button captions.</summary>
    public const string ShowDetailsButton = "Show details";
    public const string HideDetailsButton = "Hide details";

    /// <summary>
    /// Optional runtime lookup from a plugin id to its readable name (the panel sets it from the
    /// loaded-mods inventory). Null or a miss falls back to the package name, then a tidied id.
    /// </summary>
    public static Func<string, string?>? NameLookup { get; set; }

    /// <summary>
    /// A readable name for a row: the loaded plugin's own name when known, else the package's name
    /// part (<c>Owner-Name</c> → <c>Name</c>), else the id's last segment with its first letter
    /// capitalized (<c>progenitor.sample</c> → <c>Sample</c>).
    /// </summary>
    public static string DisplayName(ModDenyRow row)
    {
        if (row == null) return "";
        try
        {
            string? known = NameLookup?.Invoke(row.Id);
            if (!string.IsNullOrWhiteSpace(known)) return known!.Trim();
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(row.Package)) return Install.InstallSummary.NameOf(row.Package!);
        string id = (row.Id ?? "").Trim();
        int dot = id.LastIndexOf('.');
        string last = dot >= 0 && dot + 1 < id.Length ? id.Substring(dot + 1) : id;
        return last.Length == 0 ? id : char.ToUpperInvariant(last[0]) + last.Substring(1);
    }

    private static string Count(int n, string one, string many) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    /// <summary>
    /// The plain lines, in order: what is wrong in one sentence, a bullet per mod, and what to do.
    /// Server-required rows and this client's own rows each get their own short block.
    /// </summary>
    public static IReadOnlyList<string> Summary(ModDenyReport report)
    {
        var lines = new List<string>();
        if (report == null) return lines;

        var missing = new List<ModDenyRow>();
        var otherVersion = new List<ModDenyRow>();
        var yours = new List<ModDenyRow>();
        for (int i = 0; i < report.Rows.Count; i++)
        {
            var row = report.Rows[i];
            if (row == null) continue;
            if (row.Origin == ModDenyOrigin.YouRequire) yours.Add(row);
            else if (string.IsNullOrEmpty(row.Have)) missing.Add(row);
            else otherVersion.Add(row);
        }

        if (missing.Count + otherVersion.Count + yours.Count == 0)
        {
            lines.Add("This server turned the join down because its mods don't match yours.");
            return lines;
        }

        bool anySource = false;
        if (missing.Count > 0)
        {
            lines.Add("This server needs " + Count(missing.Count, "mod", "mods") + " you don't have:");
            foreach (var row in missing) lines.Add(Bullet(row, ref anySource));
        }
        if (otherVersion.Count > 0)
        {
            lines.Add("This server needs a different version of " + Count(otherVersion.Count, "mod", "mods") + ":");
            foreach (var row in otherVersion)
            {
                string line = "\u2022 " + DisplayName(row) + " \u2014 it needs " + Version(row.RequiredVersion)
                    + ", you have " + row.Have;
                string where = PlainWhere(row);
                if (where.Length > 0) { line += " \u2014 " + where; anySource = true; }
                lines.Add(line);
            }
        }
        if (missing.Count + otherVersion.Count > 0)
        {
            if (!anySource) lines.Add("The server didn't say where to get them.");
            lines.Add("Install them, restart the game, and join again.");
        }

        if (yours.Count > 0)
        {
            lines.Add((missing.Count + otherVersion.Count > 0 ? "Also, " : "")
                + Count(yours.Count, "of your mods only works", "of your mods only work")
                + " on servers that run " + (yours.Count == 1 ? "it" : "them") + " too, and this one doesn't:");
            bool anyRestart = false;
            foreach (var row in yours)
            {
                if (row.RestartOnly) anyRestart = true;
                lines.Add("\u2022 " + DisplayName(row) + (row.RestartOnly ? " (needs a game restart to turn off)" : ""));
            }
            lines.Add(anyRestart
                ? "Turn them off to join this server. Mods loaded when the game started need a restart without them."
                : "Turn them off to join this server.");
        }
        return lines;
    }

    private static string Bullet(ModDenyRow row, ref bool anySource)
    {
        string line = "\u2022 " + DisplayName(row) + " (" + Version(row.RequiredVersion) + ")";
        string where = PlainWhere(row);
        if (where.Length > 0) { line += " \u2014 " + where; anySource = true; }
        return line;
    }

    private static string PlainWhere(ModDenyRow row)
    {
        if (row.Url != null) return "get it from " + row.Url;
        if (row.Package != null) return "on Thunderstore as " + row.Package;
        return "";
    }

    /// <summary>
    /// The technical lines behind "Show details": the full text the panel used to show
    /// (<see cref="Lines"/>), for troubleshooting and for a mod author.
    /// </summary>
    public static IReadOnlyList<string> Details(ModDenyReport report) => Lines(report);

    /// <summary>The whole panel as text: the summary, then the details. Tests read this.</summary>
    public static string Compose(ModDenyReport report)
    {
        if (report == null) return "";
        var sb = new StringBuilder();
        foreach (string line in Summary(report)) sb.Append(line).Append('\n');
        foreach (string line in Details(report)) sb.Append(line).Append('\n');
        return sb.ToString();
    }

    private static string Version(string version) =>
        string.IsNullOrEmpty(version) ? "(no version)" : version;
}

/// <summary>Deny-path log lines, in one place, all greppable as <c>ModManifest:</c>.</summary>
public static class ModDenyLog
{
    public static string Sent(int userIndex, int rows, string fingerprint) =>
        "ModManifest: deny descriptor for user " + userIndex.ToString(CultureInfo.InvariantCulture)
        + ": " + rows.ToString(CultureInfo.InvariantCulture)
        + (rows == 1 ? " row" : " rows") + " signed fp=" + fingerprint
        + " (no transfer granted; this peer is not handshake-ok)";

    public static string Disabled() =>
        "ModManifest: deny descriptor is off (" + ModManifestDenyPolicy.ConfigSection + "."
        + ModManifestDenyPolicy.ConfigKey + " = false); a refused client is told what is missing but not "
        + "where to get it.";

    public static string NothingToSend(int userIndex) =>
        "ModManifest: no deny descriptor for user " + userIndex.ToString(CultureInfo.InvariantCulture)
        + ": the catalog has no row for any failing mod.";

    public static string Report(ModDenyReport report) =>
        "ModManifest: join refused; " + report.Rows.Count.ToString(CultureInfo.InvariantCulture)
        + " mod row(s), " + report.SourcedRows.ToString(CultureInfo.InvariantCulture)
        + " with a download source"
        + (report.DescriptorReceived ? " (signed catalog received)" : " (no catalog received)");

    public static string PanelCapReached(int cap) =>
        "ModManifest: deny panel already opened " + cap.ToString(CultureInfo.InvariantCulture)
        + " time(s) this join; not reopening.";
}

/// <summary>
/// The client half: hold the deny for this join, build the report, and raise
/// <see cref="PanelRequested"/> for the panel to draw. Game-free; the panel file is the only piece
/// that touches Unity.
/// </summary>
public static class ModManifestDeny
{
    private static readonly object Gate = new();
    private static ModDenyReport? _report;
    private static string _kickReason = "";
    private static bool _denied;
    private static int _panels;

    /// <summary>
    /// Raised on the client when a join was refused over a mod mismatch. The panel subscribes;
    /// nothing else may. Raised again when the signed descriptor lands and adds sources to the
    /// rows, so the panel is up immediately and gains its links a frame or two later.
    /// </summary>
    public static event Action<ModDenyReport>? PanelRequested;

    /// <summary>The report for the current denied join, or null.</summary>
    public static ModDenyReport? Current
    {
        get { lock (Gate) return _report; }
    }

    /// <summary>True between a refused handshake result and the next join attempt.</summary>
    public static bool JoinWasDenied
    {
        get { lock (Gate) return _denied; }
    }

#if PROGENITOR_TESTS
    internal static int TestPanelsThisJoin
    {
        get { lock (Gate) return _panels; }
    }

    /// <summary>
    /// Job FMAN17: the two <see cref="Handshake"/> tables <see cref="OnJoinDenied"/> reads,
    /// supplied directly. Same reason a test seam exists — a
    /// test must not have to mutate the process-wide handshake tables, which belong to another
    /// xUnit collection, to say what a server asked for.
    /// </summary>
    internal static IReadOnlyList<Handshake.ModRecord>? TestServerAsked;

    internal static IReadOnlyList<Handshake.ModRecord>? TestProvided;

    internal static void ResetForTests()
    {
        PanelRequested = null;
        TestServerAsked = null;
        TestProvided = null;
        TestRestartOnly = null;
        lock (Gate)
        {
            _report = null;
            _kickReason = "";
            _denied = false;
            _panels = 0;
        }
        // Do not Close the overlay: UI tests in another collection may hold it.
    }
#endif

    internal static IReadOnlyList<Handshake.ModRecord> Asked()
    {
#if PROGENITOR_TESTS
        if (TestServerAsked != null) return TestServerAsked;
#endif
        return Handshake.CopyServerAsked();
    }

    /// <summary>Tests only: stands in for <see cref="ServerModPause.IsRestartOnly"/>.</summary>
#pragma warning disable CS0649 // assigned by the test project only
    internal static Func<string, bool>? TestRestartOnly;
#pragma warning restore CS0649

    private static Func<string, bool> RestartOnlyPredicate() =>
        TestRestartOnly ?? ServerModPause.IsRestartOnly;

    internal static IReadOnlyList<Handshake.ModRecord> Provided()
    {
#if PROGENITOR_TESTS
        if (TestProvided != null) return TestProvided;
#endif
        return Handshake.CopyProvided();
    }

    /// <summary>
    /// A fresh handshake request arrived, so the previous join's refusal is no longer this
    /// client's state. Called from <c>Handshake.OnRequest</c>, which runs once per join attempt and
    /// always before the result. Dismisses the deny panel for join N — a new join must not keep
    /// that overlay over the next world.
    /// </summary>
    internal static void OnJoinAttempt()
    {
        OnJoinEnded();
        // JOINFLOW1: a new join attempt starts the join screens' state over (the message may show again).
        try { JoinFlowClient.OnJoinAttempt(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: join attempt reset threw: " + e.Message); }
    }

    /// <summary>
    /// The join ended (client <c>WorldReady</c>, return to the main menu, a later handshake
    /// result that is not a refusal, or Core going down). The report and the panel belong to
    /// that join: both are dropped, and the panel is disposed, not hidden.
    /// </summary>
    internal static void OnJoinEnded()
    {
        lock (Gate)
        {
            _report = null;
            _kickReason = "";
            _denied = false;
            _panels = 0;
        }

        // Dispose the overlay this join opened. Close is a no-op when nothing is held; it
        // must not Hide-and-keep, because a handle left in Built still draws until Take().
        ModManifestDenyPanel.Close();
        // JOINFLOW1: the join screens that replaced it belong to the same join.
        try { JoinFlowClient.OnDenyEnded(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: join end threw: " + e.Message); }
    }

    /// <summary>
    /// <c>Handshake.OnResult</c> with <c>Ok == 0</c>. Builds the report from local state alone —
    /// what this server asked (<c>Handshake.CopyServerAsked</c>), what this client answered
    /// (<c>Handshake.CopyProvided</c>) and what <c>Compare.Check</c> makes of the pair — and opens
    /// the panel at once. No descriptor is needed for this half; it is what FMAN16 established was
    /// already on the client.
    /// </summary>
    internal static void OnJoinDenied(string? kickReason, string host)
    {
        try
        {
            lock (Gate)
            {
                _denied = true;
                _kickReason = kickReason ?? "";
            }

            var rows = BuildLocalRows(Asked(), Provided(), RestartOnlyPredicate());
            Publish(new ModDenyReport(kickReason ?? "", host, "", "", "", descriptorReceived: false, rows));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ModManifest: deny report threw: " + e.Message);
        }
    }

    /// <summary>
    /// The signed descriptor landed for a join that was refused. Merges package / url / sha256 /
    /// size into the rows already on screen and republishes. A catalog that arrives when the join
    /// was <em>not</em> refused never reaches here — <c>ModManifest.AcceptEnvelope</c> routes that
    /// to the join screens.
    /// </summary>
    internal static void OnCatalogAccepted(PendingManifest? pending)
    {
        try
        {
            if (pending == null || !pending.SignatureValid) return;
            ModDenyReport? previous;
            string kick;
            lock (Gate)
            {
                if (!_denied) return;
                previous = _report;
                kick = _kickReason;
            }

            // Mod compatibility C3: the descriptor carries the server's Version matching, per-mod rules and ranges, so the
            // rows are rebuilt with the server's own judgment: a version it accepted is not a refusal row.
            var asked = Asked();
            var provided = Provided();
            VersionJudgeContext? judge = null;
            try { judge = VersionMatching.ClientContextFor(pending.Envelope.Body, asked, VersionMatching.OwnRangeOf); }
            catch { judge = null; }
            // Review fix: a request already forgotten (the join ended before a multi-packet catalog finished) leaves nothing
            // to rebuild from; the rows on screen stay.
            var baseRows = judge != null && asked.Count > 0
                ? BuildLocalRows(asked, provided, RestartOnlyPredicate(), judge)
                : previous?.Rows ?? BuildLocalRows(asked, provided, RestartOnlyPredicate());
            var merged = Merge(baseRows, pending.Envelope.Body);
            Publish(new ModDenyReport(
                kick,
                pending.Host,
                pending.Envelope.Body.ServerName,
                pending.OperatorName,
                pending.Fingerprint,
                descriptorReceived: true,
                merged));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ModManifest: deny descriptor merge threw: " + e.Message);
        }
    }

    /// <summary>
    /// The rows a client can build with no wire at all: every id this server asked about whose copy
    /// its own <c>Check</c> rejects, plus every id this client declared <c>ServerMustHave</c> that
    /// the server did not ask about. Order follows <c>Compare.Evaluate</c>, which is the order the
    /// kick reason lists them in.
    /// </summary>
    internal static IReadOnlyList<ModDenyRow> BuildLocalRows(
        IReadOnlyList<Handshake.ModRecord>? asked,
        IReadOnlyList<Handshake.ModRecord>? provided) => BuildLocalRows(asked, provided, restartOnly: null);


    /// <summary>
    /// Same, marking each <see cref="ModDenyOrigin.YouRequire"/> row for which
    /// <paramref name="restartOnly"/> says only a restart can remove the plugin.
    /// </summary>
    internal static IReadOnlyList<ModDenyRow> BuildLocalRows(
        IReadOnlyList<Handshake.ModRecord>? asked,
        IReadOnlyList<Handshake.ModRecord>? provided,
        Func<string, bool>? restartOnly) => BuildLocalRows(asked, provided, restartOnly, judge: null);

    /// <summary>
    /// Mod compatibility C3: the same, with versions judged under <paramref name="judge"/> (the server's judgment rebuilt
    /// from its catalog, <see cref="VersionMatching.ClientContextFor"/>); null is the original check.
    /// </summary>
    internal static IReadOnlyList<ModDenyRow> BuildLocalRows(
        IReadOnlyList<Handshake.ModRecord>? asked,
        IReadOnlyList<Handshake.ModRecord>? provided,
        Func<string, bool>? restartOnly,
        VersionJudgeContext? judge)
    {
        var rows = new List<ModDenyRow>();
        if (asked == null) return rows;

        Handshake.Compare.Evaluate(asked, provided, out var failedIds, judge);
        var enforced = Handshake.Compare.ServerEnforcedFailures(failedIds, asked, provided, judge);
        var enforcedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < enforced.Count; i++) enforcedIds.Add(enforced[i].Id);

        for (int i = 0; i < failedIds.Count; i++)
        {
            string id = failedIds[i];
            if (string.IsNullOrEmpty(id)) continue;
            bool serverSide = enforcedIds.Contains(id);
            string required = VersionOf(serverSide ? asked : provided, id);
            string? have = HaveVersion(provided, id);
            bool restart = false;
            if (!serverSide && restartOnly != null)
            {
                try { restart = restartOnly(id); }
                catch { restart = false; }
            }
            rows.Add(new ModDenyRow(
                id,
                required,
                have,
                serverSide ? ModDenyOrigin.ServerRequires : ModDenyOrigin.YouRequire,
                Handshake.Compare.EnforcedKickReason(id, asked, provided, judge),
                restart));
        }

        return rows;
    }

    /// <summary>
    /// Merge the descriptor's actionable fields onto rows built from local state. The descriptor
    /// may not add a row: a catalog row for an id this client did not fail is not a refusal
    /// reason, and listing it would be the server deciding what the panel says.
    /// </summary>
    internal static IReadOnlyList<ModDenyRow> Merge(IReadOnlyList<ModDenyRow> rows, ManifestBody? body)
    {
        if (rows == null) return Array.Empty<ModDenyRow>();
        if (body == null) return rows;
        var merged = new List<ModDenyRow>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            ManifestMod? match = null;
            for (int j = 0; j < body.Mods.Count; j++)
            {
                if (string.Equals(body.Mods[j].Id, row.Id, StringComparison.OrdinalIgnoreCase))
                {
                    match = body.Mods[j];
                    break;
                }
            }
            merged.Add(match == null ? row : row.WithDescriptor(match));
        }
        return merged;
    }

    private static void Publish(ModDenyReport report)
    {
        lock (Gate)
        {
            if (_panels >= ModManifestDenyPolicy.MaxPanelsPerJoin)
            {
                Plugin.Logger?.LogInfo(ModDenyLog.PanelCapReached(ModManifestDenyPolicy.MaxPanelsPerJoin));
                return;
            }
            _panels++;
            _report = report;
        }

        Plugin.Logger?.LogInfo(ModDenyLog.Report(report));
        try { PanelRequested?.Invoke(report); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ModManifest: deny panel threw on open: " + e.Message);
        }
    }

    private static string VersionOf(IReadOnlyList<Handshake.ModRecord>? rows, string id)
    {
        if (rows == null) return "";
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Guid, id, StringComparison.OrdinalIgnoreCase)) return rows[i].Version;
        }
        return "";
    }

    private static string? HaveVersion(IReadOnlyList<Handshake.ModRecord>? provided, string id)
    {
        string v = VersionOf(provided, id);
        return v.Length == 0 ? null : v;
    }
}
