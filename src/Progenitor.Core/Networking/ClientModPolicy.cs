using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>
/// CLIENTPOLICY1 (docs/client-mod-policy.md): the version rule of one admin client mod row. Not a
/// <see cref="NetworkVersionRule"/>: the handshake's wire rules are unchanged; <see cref="AtLeast"/> exists only here.
/// </summary>
internal enum ClientModRule
{
    /// <summary><c>&lt;v&gt; or newer</c> (the default for a required row with a version).</summary>
    AtLeast = 0,

    /// <summary><c>Exact &lt;v&gt;</c> (<see cref="Handshake.Compare.VersionsMatch"/> with <c>Exact</c>).</summary>
    Exact = 1,

    /// <summary><c>Same minor as &lt;v&gt;</c> (<see cref="Handshake.Compare.SameMinor"/>).</summary>
    Minor = 2,

    /// <summary><c>Any version</c> (the default for an approved row, and for a row without a version).</summary>
    Any = 3,
}

/// <summary>The admins' choice of which client mods players may use.</summary>
internal enum ClientPolicyMode
{
    /// <summary>Any mods (today's behavior, the default).</summary>
    Any = 0,

    /// <summary>Only approved mods.</summary>
    Approved = 1,
}

/// <summary>What happens to a player whose mods can't be checked (no Progenitor, or no report).</summary>
internal enum UnverifiedPolicy
{
    /// <summary>Let them join (the default).</summary>
    Allow = 0,

    /// <summary>Refuse them (also refuses unmodded players).</summary>
    Refuse = 1,
}

/// <summary>
/// CLIENTPOLICY3 (decided 2026-10-05): what happens when a mod that isn't allowed is loaded during a session, or a
/// required one is unloaded.
/// </summary>
internal enum MidSessionPolicy
{
    /// <summary>Warn and pause it (the default): a pausable mod is paused for this server, anything else gets a notice; nobody is kicked.</summary>
    Warn = 0,

    /// <summary>Disconnect the player: a notice naming the mods, then a disconnect after a short grace unless they are gone.</summary>
    Disconnect = 1,
}

/// <summary>CLIENTPOLICY1: the rule words, labels and the version check.</summary>
internal static class ClientModRules
{
    internal const string WordAtLeast = "at-least";
    internal const string WordExact = "exact";
    internal const string WordMinor = "minor";
    internal const string WordAny = "any";

    internal static string Word(ClientModRule rule) => rule switch
    {
        ClientModRule.Exact => WordExact,
        ClientModRule.Minor => WordMinor,
        ClientModRule.Any => WordAny,
        _ => WordAtLeast,
    };

    /// <summary>Read a rule word (<c>at-least</c>, <c>exact</c>, <c>minor</c>, <c>any</c>; also the enum names). False for anything else.</summary>
    internal static bool TryParse(string? word, out ClientModRule rule)
    {
        string w = (word ?? "").Trim().ToLowerInvariant().Replace("_", "-");
        switch (w)
        {
            case WordAtLeast: case "atleast": case "newer": rule = ClientModRule.AtLeast; return true;
            case WordExact: rule = ClientModRule.Exact; return true;
            case WordMinor: rule = ClientModRule.Minor; return true;
            case WordAny: case "none": rule = ClientModRule.Any; return true;
            default: rule = ClientModRule.AtLeast; return false;
        }
    }

    /// <summary>The rule as the panel and the join screens word it: <c>Exact 1.2.0</c>, <c>1.2.0 or newer</c>, <c>Same minor as 1.2.0</c>, <c>Any version</c>.</summary>
    internal static string Label(ClientModRule rule, string? version)
    {
        string v = (version ?? "").Trim();
        if (rule == ClientModRule.Any || v.Length == 0) return "Any version";
        return rule switch
        {
            ClientModRule.Exact => "Exact " + v,
            ClientModRule.Minor => "Same minor as " + v,
            _ => v + " or newer",
        };
    }

    /// <summary>The rule a row really applies: a row without a version accepts any version.</summary>
    internal static ClientModRule Effective(ClientModRule rule, string? version) =>
        (version ?? "").Trim().Length == 0 ? ClientModRule.Any : rule;

    /// <summary>True when a mod at <paramref name="got"/> passes <paramref name="rule"/> against <paramref name="required"/>. An empty <paramref name="got"/> passes only <see cref="ClientModRule.Any"/>.</summary>
    internal static bool Passes(ClientModRule rule, string? required, string? got)
    {
        string want = (required ?? "").Trim(), have = (got ?? "").Trim();
        rule = Effective(rule, want);
        if (rule == ClientModRule.Any) return true;
        if (have.Length == 0) return false;
        return rule switch
        {
            ClientModRule.Exact => Handshake.Compare.VersionsMatch(want, have, NetworkVersionRule.Exact),
            ClientModRule.Minor => Handshake.Compare.VersionsMatch(want, have, NetworkVersionRule.Minor),
            _ => Handshake.Compare.AtLeast(want, have),
        };
    }

    /// <summary>The next rule in the panel's cycle: or newer → Exact → Same minor → Any → or newer.</summary>
    internal static ClientModRule Next(ClientModRule rule) => rule switch
    {
        ClientModRule.AtLeast => ClientModRule.Exact,
        ClientModRule.Exact => ClientModRule.Minor,
        ClientModRule.Minor => ClientModRule.Any,
        _ => ClientModRule.AtLeast,
    };

    internal static string ModeWord(ClientPolicyMode mode) => mode == ClientPolicyMode.Approved ? "approved" : "any";

    internal static bool TryParseMode(string? word, out ClientPolicyMode mode)
    {
        string w = (word ?? "").Trim().ToLowerInvariant();
        mode = ClientPolicyMode.Any;
        if (w == "any" || w == "any-mods") return true;
        if (w == "approved" || w == "only-approved") { mode = ClientPolicyMode.Approved; return true; }
        return false;
    }

    internal static string UnverifiedWord(UnverifiedPolicy policy) => policy == UnverifiedPolicy.Refuse ? "refuse" : "allow";

    internal static bool TryParseUnverified(string? word, out UnverifiedPolicy policy)
    {
        string w = (word ?? "").Trim().ToLowerInvariant();
        policy = UnverifiedPolicy.Allow;
        if (w == "allow" || w == "let-them-join") return true;
        if (w == "refuse" || w == "refuse-them") { policy = UnverifiedPolicy.Refuse; return true; }
        return false;
    }

    internal const string WordWarn = "warn";
    internal const string WordDisconnect = "disconnect";

    internal static string MidSessionWord(MidSessionPolicy policy) => policy == MidSessionPolicy.Disconnect ? WordDisconnect : WordWarn;

    /// <summary>CLIENTPOLICY3: <c>warn</c> (also <c>warn-and-pause</c>) or <c>disconnect</c> (also <c>kick</c>). False for anything else.</summary>
    internal static bool TryParseMidSession(string? word, out MidSessionPolicy policy)
    {
        string w = (word ?? "").Trim().ToLowerInvariant();
        policy = MidSessionPolicy.Warn;
        if (w == WordWarn || w == "warn-and-pause" || w == "pause") return true;
        if (w == WordDisconnect || w == "disconnect-the-player" || w == "kick") { policy = MidSessionPolicy.Disconnect; return true; }
        return false;
    }
}

/// <summary>CLIENTPOLICY1: the caps of the admins' client mod policy and of the client's report.</summary>
internal static class ClientModPolicyLimits
{
    /// <summary>Required client mod rows.</summary>
    internal const int MaxRequired = 16;

    /// <summary>Approved client mod rows.</summary>
    internal const int MaxApproved = 128;

    /// <summary>Rows one <c>approve-bulk</c> request may add.</summary>
    internal const int MaxBulk = 64;

    /// <summary>A report packet (inside <c>CustomEvents.MaxPayloadBytes</c>).</summary>
    internal const int MaxReportPacketBytes = 15 * 1024;

    /// <summary>A whole report, all chunks: beyond this it is cut and marked truncated.</summary>
    internal const int MaxReportBytes = 64 * 1024;

    /// <summary>Chunks one report may use (64 KiB / about 15 KiB, rounded up).</summary>
    internal const int MaxReportChunks = 5;

    /// <summary>Entries a report is read with at most.</summary>
    internal const int MaxReportEntries = 2048;

    internal const int MaxGuidChars = 128;
    internal const int MaxNameChars = 128;
    internal const int MaxVersionChars = 64;
}

/// <summary>
/// CLIENTPOLICY1: one admin client mod row — a required row (<see cref="Reason"/> shown to players) or an approved
/// row (no reason). Matched by plugin GUID (<see cref="Id"/>), else by Thunderstore package.
/// </summary>
internal sealed class ClientModRow
{
    public ClientModRow(string? id, string? package, string? version, ClientModRule rule, string? reason = "", string? name = "")
    {
        Id = (id ?? "").Trim();
        Package = (package ?? "").Trim();
        Version = (version ?? "").Trim();
        Rule = rule;
        Reason = (reason ?? "").Trim();
        Name = (name ?? "").Trim();
    }

    public string Id { get; }
    public string Package { get; }
    public string Version { get; }
    public ClientModRule Rule { get; }
    public string Reason { get; }

    /// <summary>The readable name the admin's picker knew, or empty.</summary>
    public string Name { get; }

    public string Key => Id.Length > 0 ? Id : Package;

    /// <summary>What players and admins read: the name, else the id, else the package.</summary>
    public string Display => Name.Length > 0 ? Name : Key;

    public ClientModRow With(string? version = null, ClientModRule? rule = null, string? reason = null, string? name = null) =>
        new(Id, Package, version ?? Version, rule ?? Rule, reason ?? Reason, name ?? Name);

    /// <summary>True when this row names <paramref name="key"/> (its id or its package, case-insensitive).</summary>
    public bool Names(string? key)
    {
        string k = (key ?? "").Trim();
        return k.Length > 0
            && ((Id.Length > 0 && string.Equals(Id, k, StringComparison.OrdinalIgnoreCase))
                || (Package.Length > 0 && string.Equals(Package, k, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>True when this row and <paramref name="other"/> name the same mod (shared id or shared package).</summary>
    public bool SameModAs(string? id, string? package) =>
        (!string.IsNullOrEmpty(id) && Id.Length > 0 && string.Equals(Id, id!.Trim(), StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrEmpty(package) && Package.Length > 0 && string.Equals(Package, package!.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>One line for the audit log.</summary>
    public string Describe() => Key + " " + ClientModRules.Label(Rule, Version) + (Reason.Length > 0 ? ": " + Reason : "");
}

/// <summary>CLIENTPOLICY1: the admins' client mod policy as stored (schema 2 of <c>progenitor-join-admin.json</c>).</summary>
internal sealed class ClientPolicyConfig
{
    public List<ClientModRow> Required { get; } = new();
    public ClientPolicyMode Mode { get; set; } = ClientPolicyMode.Any;
    public List<ClientModRow> Approved { get; } = new();
    public UnverifiedPolicy Unverified { get; set; } = UnverifiedPolicy.Allow;

    /// <summary>CLIENTPOLICY3: what a mid-session re-check does about a mod that isn't allowed (stored as <c>midSession</c>; default Warn).</summary>
    public MidSessionPolicy MidSession { get; set; } = MidSessionPolicy.Warn;

    /// <summary>The server asks clients for their mods (the capability bit): a required row, or Only approved.</summary>
    public bool Active => Required.Count > 0 || Mode == ClientPolicyMode.Approved;

    /// <summary>A client that never answers the handshake is refused for this policy alone: a required row, or Only approved with Refuse them.</summary>
    public bool RequiresAnswer => Required.Count > 0 || (Mode == ClientPolicyMode.Approved && Unverified == UnverifiedPolicy.Refuse);

    public ClientPolicyConfig Clone()
    {
        var c = new ClientPolicyConfig { Mode = Mode, Unverified = Unverified, MidSession = MidSession };
        c.Required.AddRange(Required);
        c.Approved.AddRange(Approved);
        return c;
    }

    public int IndexOfRequired(string? key) => IndexOf(Required, key);
    public int IndexOfApproved(string? key) => IndexOf(Approved, key);

    internal static int IndexOf(List<ClientModRow> rows, string? key)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Names(key)) return i;
        return -1;
    }

    internal static int IndexOfMod(List<ClientModRow> rows, string? id, string? package)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].SameModAs(id, package)) return i;
        return -1;
    }
}

/// <summary>CLIENTPOLICY1: where a reported plugin was loaded from.</summary>
internal static class ClientModSources
{
    internal const string Chainloader = "chainloader";
    internal const string Progenitor = "progenitor";
    internal const string Patcher = "patcher";

    internal static string Clean(string? source)
    {
        string s = (source ?? "").Trim().ToLowerInvariant();
        return s == Progenitor || s == Patcher ? s : Chainloader;
    }
}

/// <summary>CLIENTPOLICY1: one plugin a client reports. Every field is the client's claim.</summary>
internal sealed class ClientModEntry
{
    public ClientModEntry(string? guid, string? name, string? version, string? source, string? package = "")
    {
        Guid = Clip(guid, ClientModPolicyLimits.MaxGuidChars);
        Name = Clip(name, ClientModPolicyLimits.MaxNameChars);
        Version = Clip(version, ClientModPolicyLimits.MaxVersionChars);
        Source = ClientModSources.Clean(source);
        Package = Clip(package, JoinSectionLimits.MaxPackageChars);
    }

    public string Guid { get; }
    public string Name { get; }
    public string Version { get; }
    public string Source { get; }
    public string Package { get; }

    public string Display => Name.Length > 0 ? Name : Guid.Length > 0 ? Guid : Package;

    internal static string Clip(string? s, int max)
    {
        string t = (s ?? "").Trim();
        var sb = new StringBuilder(Math.Min(t.Length, max));
        foreach (char c in t)
        {
            if (sb.Length >= max) break;
            if (!char.IsControl(c)) sb.Append(c);
        }

        return sb.ToString();
    }
}

/// <summary>
/// SELFFIX1: a required mod this client says it can fix by itself during the join. Untrusted: it may only earn the
/// install window, never acceptance (the server judges the real report again when the window ends).
/// </summary>
internal sealed class ClientModFix
{
    /// <summary>A copy turned off in the Mods list that turns on now (it hot-loads).</summary>
    public const string KindTurnOn = "turn-on";

    /// <summary>A package the configured local test source (<c>[Install] LocalIndexPath</c>) offers, which hot-loads.</summary>
    public const string KindLocalTest = "local-test";

    /// <summary>At most this many claims are sent or read.</summary>
    public const int MaxFixes = 32;

    public const int MaxKeyChars = 128;

    /// <summary>A claim whose version is longer is neither sent nor read.</summary>
    public const int MaxVersionChars = 64;

    public ClientModFix(string key, string kind, string version)
    {
        Key = (key ?? "").Trim();
        Kind = (kind ?? "").Trim();
        Version = (version ?? "").Trim();
    }

    /// <summary>The plugin GUID.</summary>
    public string Key { get; }

    public string Kind { get; }

    /// <summary>The version the fix would load, or empty.</summary>
    public string Version { get; }

    public static bool KnownKind(string? kind) => kind is KindTurnOn or KindLocalTest;
}

/// <summary>CLIENTPOLICY1: a client's inventory report as the server holds it.</summary>
internal sealed class ClientModReport
{
    public List<ClientModEntry> Entries { get; } = new();

    /// <summary>SELFFIX1: the required mods this client says it can fix by itself (untrusted; see <see cref="ClientModFix"/>).</summary>
    public List<ClientModFix> Fixes { get; } = new();

    /// <summary>The client cut the report to fit the 64 KiB total: the missing part can't be checked.</summary>
    public bool Truncated { get; set; }

    /// <summary>The first entry for plugin GUID <paramref name="guid"/>, or null.</summary>
    public ClientModEntry? FindGuid(string? guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        foreach (var e in Entries)
            if (string.Equals(e.Guid, guid, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>The first entry the install ledger tied to <paramref name="package"/>, or null.</summary>
    public ClientModEntry? FindPackage(string? package)
    {
        if (string.IsNullOrEmpty(package)) return null;
        foreach (var e in Entries)
            if (e.Package.Length > 0 && string.Equals(e.Package, package, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>The entry a row names: by plugin GUID, else by package.</summary>
    public ClientModEntry? Find(ClientModRow row) =>
        (row.Id.Length > 0 ? FindGuid(row.Id) : null) ?? (row.Package.Length > 0 ? FindPackage(row.Package) : null);
}

/// <summary>What the server runs, for the judgment's "a mod this server runs" rule (game-free facts).</summary>
internal sealed class ClientModServerFacts
{
    /// <summary>Plugin GUID → (version, the handshake rule its client copy must meet, or null for any version).</summary>
    public Dictionary<string, (string Version, NetworkVersionRule? Rule)> Runs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ids and packages of the mods this server recommends.</summary>
    public HashSet<string> Recommended { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The three outcomes of a judgment.</summary>
internal enum ClientJudgmentOutcome
{
    /// <summary>The policy passed (or there is none).</summary>
    Pass = 0,

    /// <summary>Nothing could be checked and Let them join applies (the admin alert counts it).</summary>
    PassUnchecked = 1,

    /// <summary>The join is refused with <see cref="ClientModVerdict.Reason"/>.</summary>
    Refuse = 2,
}

/// <summary>One problem a judgment found.</summary>
internal sealed class ClientModProblem
{
    internal const string KindMissing = "missing";
    internal const string KindOlder = "older";
    internal const string KindNewer = "newer";
    internal const string KindMismatch = "mismatch";
    internal const string KindNotAllowed = "not-allowed";

    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Required { get; set; } = "";
    public string Had { get; set; } = "";
    public ClientModRule Rule { get; set; }

    /// <summary>What a player reads: <c>Missing</c>, <c>Update needed</c>, <c>Newer than allowed</c>, <c>Not allowed on this server</c>.</summary>
    public string Status => Kind switch
    {
        KindMissing => "Missing",
        KindOlder => "Update needed",
        KindNewer => "Newer than allowed",
        KindNotAllowed => "Not allowed on this server",
        _ => "Wrong version",
    };
}

/// <summary>CLIENTPOLICY1: one join's judgment.</summary>
internal sealed class ClientModVerdict
{
    public ClientJudgmentOutcome Outcome { get; set; }

    /// <summary>A report was judged (false: no report, or nothing to judge).</summary>
    public bool Checked { get; set; }

    /// <summary>The mods could not be (fully) checked: no report, or a truncated one.</summary>
    public bool Unchecked { get; set; }

    public List<ClientModProblem> Required { get; } = new();
    public List<ClientModProblem> NotAllowed { get; } = new();

    /// <summary>The readable refusal (ASCII, at most 479 characters), or empty.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The verdict in a word, for logs and the bridge: <c>pass</c>, <c>pass-unchecked</c>, <c>refuse</c>.</summary>
    public string Word => Outcome switch
    {
        ClientJudgmentOutcome.PassUnchecked => "pass-unchecked",
        ClientJudgmentOutcome.Refuse => "refuse",
        _ => "pass",
    };
}

/// <summary>
/// CLIENTPOLICY1: the server's judgment of one join (docs/client-mod-policy.md, "How a join is judged", rules 1–3
/// and 5). Game-free: the live handshake and the tests call this same code.
/// </summary>
internal static class ClientModJudge
{
    /// <summary>The plugin GUIDs always allowed: Progenitor's own (Core and Bootstrap, the dev bridge, the preloader).</summary>
    internal static readonly string[] ProgenitorIds =
    {
        "progenitor", "progenitor.dev", "progenitor.preloader",
    };

    internal const string UncheckedReason =
        "This server checks client mods, and yours could not be checked. Join with Progenitor installed.";

    /// <summary>
    /// Prefixed to a refusal when the client's report carries an id in the form Progenitor used before the 2026-10 id rename
    /// (<see cref="Progenitor.Plugins.LegacyIds"/>): that client runs an older Progenitor, and the rows below are its old ids.
    /// </summary>
    internal const string OlderProgenitorReason =
        "Your Progenitor is older than this server's (its plugin ids were renamed). Update Progenitor and its mods.";

    /// <summary>True when a client's report names a plugin by an id from before the 2026-10 id rename.</summary>
    internal static bool HasRenamedIds(ClientModReport? report)
    {
        if (report == null) return false;
        foreach (var e in report.Entries)
            if (Progenitor.Plugins.LegacyIds.IsLegacy(e.Guid)) return true;
        return false;
    }

    internal const string NeedsPrefix = "Mods needed: ";
    internal const string NotAllowedPrefix = "Mods not allowed on this server: ";

    /// <summary>A plugin GUID of 1 to 63 characters from <c>[A-Za-z0-9._-]</c> (what the refused-join record keeps of a client's claim).</summary>
    internal static bool IsPlainId(string? id)
    {
        string s = id ?? "";
        if (s.Length == 0 || s.Length > JoinSectionLimits.MaxIdChars) return false;
        foreach (char c in s)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
        return true;
    }

    /// <summary>BepInEx (any <c>BepInEx</c> GUID) and Progenitor are always allowed.</summary>
    internal static bool AlwaysApproved(string? guid)
    {
        string g = (guid ?? "").Trim();
        if (g.Length == 0) return false;
        if (g.Equals("bepinex", StringComparison.OrdinalIgnoreCase) || g.StartsWith("bepinex.", StringComparison.OrdinalIgnoreCase)) return true;
        g = Progenitor.Plugins.LegacyIds.Normalize(g);
        foreach (string id in ProgenitorIds)
            if (string.Equals(g, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Judge one join. <paramref name="report"/> null: no report (a client without Progenitor's report, or none
    /// arrived in time). Rule 1 (required) runs first, then rule 2 (Only approved); rule 3 decides a missing or
    /// truncated report. Rule 4 (the install window) is not applied: every problem refuses.
    /// </summary>
    internal static ClientModVerdict Judge(ClientPolicyConfig? policy, ClientModReport? report, ClientModServerFacts? facts)
    {
        var verdict = new ClientModVerdict { Outcome = ClientJudgmentOutcome.Pass };
        if (policy == null || !policy.Active) return verdict;
        facts ??= new ClientModServerFacts();
        verdict.Checked = report != null;

        // Rule 1: every required row in the report, passing its rule. No report: every row is missing.
        foreach (var row in policy.Required)
        {
            var entry = report?.Find(row);
            var rule = ClientModRules.Effective(row.Rule, row.Version);
            if (entry == null)
            {
                verdict.Required.Add(Problem(row, ClientModProblem.KindMissing, ""));
                continue;
            }

            if (ClientModRules.Passes(rule, row.Version, entry.Version)) continue;
            int? order = Handshake.Compare.CompareOrder(entry.Version, row.Version);
            string kind = order switch { < 0 => ClientModProblem.KindOlder, > 0 => ClientModProblem.KindNewer, _ => ClientModProblem.KindMismatch };
            if (entry.Version.Length == 0) kind = ClientModProblem.KindOlder;
            verdict.Required.Add(Problem(row, kind, entry.Version));
        }

        // Rule 2: Only approved.
        if (policy.Mode == ClientPolicyMode.Approved && report != null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in report.Entries)
            {
                string key = e.Guid.Length > 0 ? e.Guid : e.Package;
                if (key.Length == 0 || !seen.Add(key)) continue;
                if (Allowed(policy, facts, e)) continue;
                verdict.NotAllowed.Add(new ClientModProblem
                {
                    Key = key, Name = e.Display, Kind = ClientModProblem.KindNotAllowed, Had = e.Version, Rule = ClientModRule.Any,
                });
            }
        }

        // Rule 3: no report, or a cut one.
        bool unverifiedRefuses = policy.Mode == ClientPolicyMode.Approved && policy.Unverified == UnverifiedPolicy.Refuse;
        verdict.Unchecked = report == null || report.Truncated;
        bool refuseUnchecked = verdict.Unchecked && unverifiedRefuses;

        if (verdict.Required.Count > 0 || verdict.NotAllowed.Count > 0 || refuseUnchecked)
        {
            verdict.Outcome = ClientJudgmentOutcome.Refuse;
            verdict.Reason = ReasonFor(verdict.Required, verdict.NotAllowed, refuseUnchecked);
            if (HasRenamedIds(report)) verdict.Reason = Handshake.Compare.TruncateReason(OlderProgenitorReason + " " + verdict.Reason, Handshake.ResultMessageMaxChars);
            return verdict;
        }

        verdict.Outcome = verdict.Unchecked && policy.Mode == ClientPolicyMode.Approved ? ClientJudgmentOutcome.PassUnchecked : ClientJudgmentOutcome.Pass;
        return verdict;
    }

    private static ClientModProblem Problem(ClientModRow row, string kind, string had) => new()
    {
        Key = row.Key, Name = row.Display, Kind = kind, Required = row.Version, Had = had, Rule = ClientModRules.Effective(row.Rule, row.Version),
    };

    /// <summary>Rule 2's allowed set: always approved, a mod the server runs or requires (passing its rule), a recommended mod, or an approved row it passes.</summary>
    internal static bool Allowed(ClientPolicyConfig policy, ClientModServerFacts facts, ClientModEntry e)
    {
        if (AlwaysApproved(e.Guid)) return true;
        if (e.Guid.Length > 0 && facts.Runs.TryGetValue(e.Guid, out var run))
        {
            if (run.Rule == null || run.Version.Length == 0) return true;
            if (Handshake.Compare.VersionsMatch(run.Version, e.Version, run.Rule.Value)) return true;
        }

        // A required row's version problem is rule 1's to report; the mod itself is allowed.
        if (FindFor(policy.Required, e) != null) return true;
        if ((e.Guid.Length > 0 && facts.Recommended.Contains(e.Guid)) || (e.Package.Length > 0 && facts.Recommended.Contains(e.Package))) return true;
        var approved = FindFor(policy.Approved, e);
        return approved != null && ClientModRules.Passes(approved.Rule, approved.Version, e.Version);
    }

    private static ClientModRow? FindFor(List<ClientModRow> rows, ClientModEntry e)
    {
        foreach (var r in rows)
            if (r.Id.Length > 0 && e.Guid.Length > 0 && string.Equals(r.Id, e.Guid, StringComparison.OrdinalIgnoreCase)) return r;
        foreach (var r in rows)
            if (r.Id.Length == 0 && r.Package.Length > 0 && e.Package.Length > 0 && string.Equals(r.Package, e.Package, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>
    /// The readable refusal: <c>Mods needed: A (1.2.0 or newer), B.</c> then <c>Mods not allowed on this server: X, Y.</c>,
    /// then the can't-be-checked sentence. ASCII only (anything else becomes <c>?</c>) and cut to the 479-character
    /// wire message.
    /// </summary>
    internal static string ReasonFor(IReadOnlyList<ClientModProblem> required, IReadOnlyList<ClientModProblem> notAllowed, bool unchecked_)
    {
        var parts = new List<string>();
        if (required.Count > 0)
        {
            var names = new List<string>();
            foreach (var p in required)
            {
                string label = p.Rule == ClientModRule.Any || p.Required.Length == 0 ? "" : " (" + ClientModRules.Label(p.Rule, p.Required)
                    + (p.Had.Length > 0 ? ", you have " + p.Had : "") + ")";
                names.Add(p.Name + label);
            }

            parts.Add(NeedsPrefix + string.Join(", ", names) + ".");
        }

        if (notAllowed.Count > 0)
        {
            var names = new List<string>();
            foreach (var p in notAllowed) names.Add(p.Name);
            parts.Add(NotAllowedPrefix + string.Join(", ", names) + ".");
        }

        if (unchecked_) parts.Add(UncheckedReason);
        return Handshake.Compare.TruncateReason(Ascii(string.Join(" ", parts)), Handshake.ResultMessageMaxChars);
    }

    /// <summary>Printable ASCII only: the Result packet is ASCII (an em dash arrives as <c>?</c> anyway).</summary>
    internal static string Ascii(string? text)
    {
        var sb = new StringBuilder((text ?? "").Length);
        foreach (char c in text ?? "")
            sb.Append(c >= 0x20 && c < 0x7F ? c : c == '·' ? '-' : '?');
        return sb.ToString();
    }

    /// <summary>
    /// The no-answer kick's required-list text with the policy's part: the required client mods' names (<c>Mods needed:
    /// A, B</c>), and for Only approved with Refuse them, that client mods are checked.
    /// </summary>
    internal static string NoAnswerSummary(string baseSummary, ClientPolicyConfig? policy)
    {
        string b = string.IsNullOrEmpty(baseSummary) ? "none" : baseSummary;
        if (policy == null || !policy.RequiresAnswer) return b;
        var parts = new List<string>();
        if (policy.Required.Count > 0)
        {
            var names = new List<string>();
            foreach (var r in policy.Required) names.Add(r.Display + (r.Version.Length > 0 && r.Rule != ClientModRule.Any ? " " + ClientModRules.Label(r.Rule, r.Version) : ""));
            parts.Add(NeedsPrefix + string.Join(", ", names));
        }

        if (policy.Mode == ClientPolicyMode.Approved && policy.Unverified == UnverifiedPolicy.Refuse)
            parts.Add("client mods are checked (Progenitor needed)");
        string extra = Ascii(string.Join("; ", parts));
        return b == "none" ? extra : b + "; " + extra;
    }

    /// <summary>
    /// CLIENTPOLICY2: the readable names of a refusal's required client rows (the admins' own names, by row key), stored with
    /// the record so the alert reads <c>Robe Pack</c> rather than <c>test.robes.pack</c>. Never a not-approved mod's name:
    /// that is the client's text.
    /// </summary>
    internal static Dictionary<string, string> RecordNames(ClientModVerdict verdict)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (verdict == null || verdict.Outcome != ClientJudgmentOutcome.Refuse) return names;
        foreach (var p in verdict.Required)
            if (p.Key.Length > 0 && p.Name.Length > 0 && !string.Equals(p.Name, p.Key, StringComparison.Ordinal)) names[p.Key] = p.Name;
        return names;
    }

    /// <summary>The refused-join record's rows for a refusal: the required rows (admin-defined) and the not-approved ids (validated ids only).</summary>
    internal static List<(string Mod, string Required, string Had, string Kind)> RecordRows(ClientModVerdict verdict)
    {
        var rows = new List<(string, string, string, string)>();
        if (verdict == null || verdict.Outcome != ClientJudgmentOutcome.Refuse) return rows;
        foreach (var p in verdict.Required)
        {
            if (rows.Count >= 32) break;
            string kind = p.Kind switch
            {
                ClientModProblem.KindMissing => RefusedJoinLog.KindClientMissing,
                ClientModProblem.KindOlder => RefusedJoinLog.KindClientOlder,
                ClientModProblem.KindNewer => RefusedJoinLog.KindClientNewer,
                _ => RefusedJoinLog.KindClientOlder,
            };
            string had = p.Had.Length > 0 && !ModManifestIds.TryValidateVersion(p.Had, out _) ? "?" : p.Had;
            rows.Add((p.Key, p.Required, had, kind));
        }

        foreach (var p in verdict.NotAllowed)
        {
            if (rows.Count >= 32) break;
            // Client-chosen text: only a plain id ([A-Za-z0-9._-], at most 63) is recorded, never its name or version,
            // so a player cannot write a sentence into the admins' alert.
            if (!IsPlainId(p.Key)) continue;
            rows.Add((p.Key, "", "", RefusedJoinLog.KindNotApproved));
        }

        if (rows.Count == 0 && verdict.Unchecked) rows.Add((RefusedJoinLog.UncheckedMod, "", "", RefusedJoinLog.KindUnchecked));
        return rows;
    }
}
