using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>
/// Mod compatibility C3: the server-wide <b>Version matching</b> setting (docs/mod-compatibility.md, Server rules), on the
/// Server tab under Mods players need.
/// </summary>
internal enum VersionMatchingMode
{
    /// <summary>Use each mod's declared range (the default): when the versions differ, the newer side's range decides.</summary>
    Range = 0,

    /// <summary>Same minor for all: every mod the server checks must have the server's major and minor.</summary>
    Minor = 1,

    /// <summary>Exact version for all: every mod the server checks must be the server's version exactly.</summary>
    Exact = 2,
}

/// <summary>C3: the words, labels and help of <see cref="VersionMatchingMode"/> (store, cfg, panel and Server tab share them).</summary>
internal static class VersionMatchingText
{
    internal const string WordRange = "range";
    internal const string WordMinor = "minor";
    internal const string WordExact = "exact";

    internal const string Heading = "Version matching";
    internal const string LabelRange = "Use each mod's declared range";
    internal const string LabelMinor = "Same minor for all";
    internal const string LabelExact = "Exact version for all";

    internal const string HelpRange =
        "When versions differ, the newer side's declared range decides; a mod that declares nothing needs the same minor version.";
    internal const string HelpMinor = "Players' mods must have this server's major and minor version (1.2.x for 1.2.0).";

    /// <summary>
    /// The help under Exact. Mod compatibility C4: Progenitor itself is held to Exact too (its Core build, while this
    /// server's build is published on Thunderstore; <see cref="ProgenitorExact"/>).
    /// </summary>
    internal const string HelpExact = "Players must have exactly the versions this server runs, including Progenitor.";

    /// <summary>The per-mod rules, as the Server tab explains them.</summary>
    internal const string ModRulesHelp = "A rule set on one mod wins over this setting for that mod.";

    internal static string Word(VersionMatchingMode mode) => mode switch
    {
        VersionMatchingMode.Minor => WordMinor,
        VersionMatchingMode.Exact => WordExact,
        _ => WordRange,
    };

    internal static string Label(VersionMatchingMode mode) => mode switch
    {
        VersionMatchingMode.Minor => LabelMinor,
        VersionMatchingMode.Exact => LabelExact,
        _ => LabelRange,
    };

    internal static string Help(VersionMatchingMode mode) => mode switch
    {
        VersionMatchingMode.Minor => HelpMinor,
        VersionMatchingMode.Exact => HelpExact,
        _ => HelpRange,
    };

    /// <summary><c>range</c> (also <c>ranges</c>, <c>declared</c>), <c>minor</c> (also <c>same-minor</c>), <c>exact</c>. False for anything else.</summary>
    internal static bool TryParse(string? word, out VersionMatchingMode mode)
    {
        string w = (word ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        mode = VersionMatchingMode.Range;
        switch (w)
        {
            case WordRange: case "ranges": case "declared": case "declared-range": return true;
            case WordMinor: case "same-minor": case "same-minor-for-all": mode = VersionMatchingMode.Minor; return true;
            case WordExact: case "exact-version": case "exact-version-for-all": mode = VersionMatchingMode.Exact; return true;
            default: return false;
        }
    }

    internal static IReadOnlyList<(string Word, string Label, string Help)> Options => new[]
    {
        (WordRange, LabelRange, HelpRange),
        (WordMinor, LabelMinor, HelpMinor),
        (WordExact, LabelExact, HelpExact),
    };
}

/// <summary>
/// C3: the server's version matching as the join evaluation reads it: the server-wide <see cref="Mode"/> and the admins'
/// per-mod rules for mods the server runs (<see cref="ModRules"/>, plugin GUID to rule), which win over the mode.
/// Immutable once built.
/// </summary>
internal sealed class VersionMatchSettings
{
    internal VersionMatchSettings(VersionMatchingMode mode, IReadOnlyDictionary<string, ClientModRule>? modRules = null)
    {
        Mode = mode;
        var rules = new Dictionary<string, ClientModRule>(StringComparer.OrdinalIgnoreCase);
        if (modRules != null)
            foreach (var kv in modRules)
                if (!string.IsNullOrWhiteSpace(kv.Key)) rules[kv.Key.Trim()] = kv.Value;
        ModRules = rules;
    }

    internal static readonly VersionMatchSettings Default = new(VersionMatchingMode.Range);

    internal VersionMatchingMode Mode { get; }

    internal IReadOnlyDictionary<string, ClientModRule> ModRules { get; }

    internal bool TryRule(string? guid, out ClientModRule rule)
    {
        rule = ClientModRule.AtLeast;
        return !string.IsNullOrEmpty(guid) && ModRules.TryGetValue(guid!, out rule);
    }

    /// <summary>One line for logs and the bridge: <c>range</c>, <c>exact (2 mod rules)</c>.</summary>
    internal string Describe() =>
        VersionMatchingText.Word(Mode) + (ModRules.Count > 0 ? " (" + ModRules.Count.ToString(CultureInfo.InvariantCulture) + " mod rule" + (ModRules.Count == 1 ? ")" : "s)") : "");
}

/// <summary>
/// C3: what one join evaluation knows besides the two row lists. Built by the server for each answer; the tests build it
/// by hand.
/// </summary>
internal sealed class VersionJudgeContext
{
    internal VersionMatchSettings Settings { get; set; } = VersionMatchSettings.Default;

    /// <summary>The server's effective range for a mod it runs (declared, else from the rule, else the same minor). Null: the default.</summary>
    internal Func<string, string?>? ServerRange { get; set; }

    /// <summary>The server's <em>declared</em> <c>Peers</c> range for a mod, or null/empty when none (used for a client that sent no ranges).</summary>
    internal Func<string, string?>? ServerDeclaredRange { get; set; }

    /// <summary>
    /// The client's effective ranges from its feature table (mod GUID to range), or null when the client sent none (its
    /// Progenitor predates ranges, or its table did not arrive in time). A mod missing from a non-null map gets the
    /// default, the same minor version of the client's build.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? ClientRanges { get; set; }

    /// <summary>
    /// Client side only: the client's range for (mod id, version), which wins over <see cref="ClientRanges"/> when set (null
    /// result: the default for that version). The client judges copies at other versions than the loaded one (a turned-off
    /// copy, a pending install), so its ranges are looked up by version, not by id.
    /// </summary>
    internal Func<string, string, string?>? ClientRangeOf { get; set; }

    /// <summary>Pass lines for a mod whose versions differed (the server log's <c>accepted by … range</c>).</summary>
    internal List<string> Accepted { get; } = new();
}

/// <summary>
/// C3: the version half of the join evaluation (docs/mod-compatibility.md, Evaluation at the join), game-free. Called by
/// <see cref="Handshake.Compare.Evaluate(IReadOnlyList{Handshake.ModRecord}, IReadOnlyList{Handshake.ModRecord}, out List{string}, VersionJudgeContext)"/>
/// wherever the original walk compared two versions of one mod; presence and levels are unchanged.
/// <list type="number">
/// <item>Equal versions always pass.</item>
/// <item>An admin's per-mod rule decides alone.</item>
/// <item>Else <b>Exact version for all</b> or <b>Same minor for all</b> decide.</item>
/// <item>Else (declared ranges), when the client sent ranges: the newer side's range must include the older version; a
/// side that declared nothing gets the same minor version.</item>
/// <item>A client that sent no ranges: the server's declared range when it has one, else the row's attribute rule
/// (today's behavior for an older client).</item>
/// </list>
/// </summary>
internal static class VersionMatchJudge
{
    /// <summary>
    /// The problem for one mod both sides run (<c>"&lt;guid&gt;: &lt;sentence&gt;"</c>), or null when it passes.
    /// <paramref name="serverRow"/> is the server's row (its version, its attribute rule), <paramref name="clientVersion"/>
    /// the client's.
    /// </summary>
    internal static string? Check(Handshake.ModRecord serverRow, string? clientVersion, VersionJudgeContext ctx)
    {
        string id = serverRow.Guid ?? "";
        string s = (serverRow.Version ?? "").Trim();
        string c = (clientVersion ?? "").Trim();
        if (string.Equals(s, c, StringComparison.OrdinalIgnoreCase)) return null;
        var settings = ctx.Settings ?? VersionMatchSettings.Default;

        if (settings.TryRule(id, out var rule))
        {
            if (ClientModRules.Passes(rule, s, c))
            {
                ctx.Accepted.Add(id + " client " + c + " accepted by the admins' rule for it (" + ClientModRules.Label(rule, s) + ")");
                return null;
            }

            return id + ": " + RuleSentence(rule, s, c);
        }

        switch (settings.Mode)
        {
            case VersionMatchingMode.Exact:
                return id + ": " + RuleSentence(ClientModRule.Exact, s, c);
            case VersionMatchingMode.Minor:
                if (Handshake.Compare.SameMinor(s, c))
                {
                    ctx.Accepted.Add(id + " client " + c + " accepted under Same minor for all (server " + s + ")");
                    return null;
                }

                return id + ": " + RuleSentence(ClientModRule.Minor, s, c);
        }

        if (ctx.ClientRanges == null)
        {
            // An older client (or a table that did not come): the server's own declared range, else today's rule.
            string declared = (ctx.ServerDeclaredRange?.Invoke(id) ?? "").Trim();
            if (declared.Length > 0 && CompatRange.TryParse(declared, out var mine, out _))
            {
                if (mine!.Includes(c))
                {
                    ctx.Accepted.Add(id + " client " + c + " accepted by server " + s + "'s range " + mine.Text);
                    return null;
                }

                return id + ": " + ServerRangeSentence(s, mine, c);
            }

            if (Handshake.Compare.VersionsMatch(s, c, serverRow.VersionRule)) return null;
            return LegacyReason(serverRow, c);
        }

        int? order = CompatVersion.Compare(c, s);
        if (order == null)
            return id + ": this server has " + s + " and you have " + c + ", which can't be compared";
        if (order.Value == 0) return null;

        if (order.Value > 0)
        {
            // The client is newer: its range about the older server decides.
            string? r = ctx.ClientRangeOf != null ? ctx.ClientRangeOf(id, c)
                : ctx.ClientRanges.TryGetValue(id, out var byId) ? byId : null;
            string text = !string.IsNullOrWhiteSpace(r) ? r! : CompatDeclaration.DefaultRange(c);
            if (!CompatRange.TryParse(text, out var theirs, out _)) theirs = CompatRange.SameMinorAs(c);
            if (theirs != null && theirs.Includes(s))
            {
                ctx.Accepted.Add(id + " client " + c + " accepted by the client's range " + theirs.Text + " (server " + s + ")");
                return null;
            }

            return id + ": your " + c + " works with " + (theirs?.Describe() ?? "only itself") + "; this server has " + s;
        }

        string own = (ctx.ServerRange?.Invoke(id) ?? "").Trim();
        if (own.Length == 0) own = CompatDeclaration.DefaultRange(s);
        if (!CompatRange.TryParse(own, out var server, out _)) server = CompatRange.SameMinorAs(s);
        if (server != null && server.Includes(c))
        {
            ctx.Accepted.Add(id + " client " + c + " accepted by server " + s + "'s range " + server.Text);
            return null;
        }

        return id + ": " + (server != null ? ServerRangeSentence(s, server, c) : "this server requires exactly " + s + " (you have " + c + ")");
    }

    /// <summary>The sentence for an admin rule or a strict mode: <c>this server requires exactly 1.2.0 (you have 1.3.0)</c>.</summary>
    internal static string RuleSentence(ClientModRule rule, string serverVersion, string clientVersion) => rule switch
    {
        ClientModRule.Exact => "this server requires exactly " + serverVersion + " (you have " + clientVersion + ")",
        ClientModRule.Minor => "this server requires the same minor version as " + serverVersion
                               + " (" + (CompatVersion.MinorOf(serverVersion) ?? serverVersion) + ".x; you have " + clientVersion + ")",
        ClientModRule.AtLeast => "this server requires " + serverVersion + " or newer (you have " + clientVersion + ")",
        _ => "this server accepts any version (you have " + clientVersion + ")",
    };

    private static string ServerRangeSentence(string serverVersion, CompatRange range, string clientVersion) =>
        "this server's " + serverVersion + " works with " + range.Describe() + "; you have " + clientVersion;

    /// <summary>Today's wording for an older client judged by the attribute rule (unchanged so its tests and logs hold).</summary>
    private static string LegacyReason(Handshake.ModRecord serverRow, string clientVersion)
    {
        string expected = serverRow.VersionRule switch
        {
            NetworkVersionRule.Minor => serverRow.Version + " (minor)",
            NetworkVersionRule.None => serverRow.Version + " (any)",
            _ => serverRow.Version,
        };
        return serverRow.Guid + ": expected " + expected + ", got " + clientVersion;
    }

    /// <summary>
    /// The row the server puts on its handshake request under <paramref name="settings"/>: Exact for all writes
    /// <see cref="NetworkVersionRule.Exact"/>, Same minor for all <see cref="NetworkVersionRule.Minor"/>, a per-mod rule its
    /// wire equivalent (Any: <see cref="NetworkVersionRule.None"/>). "Or newer" has no wire rule and is written as
    /// <see cref="NetworkVersionRule.None"/> too: a client then never pauses or swaps away a newer copy the server accepts
    /// (keeping the attribute's rule under a strict mode bit made a C3 client pause it), and an older copy is refused by
    /// the server's judgment with the "or newer" sentence. So a client (of any Progenitor) predicts the strict modes: its
    /// automatic pause, kept-copy choice and update cap. The range mode without a rule keeps the attribute's rule.
    /// </summary>
    internal static Handshake.ModRecord RequestRow(Handshake.ModRecord row, VersionMatchSettings? settings)
    {
        settings ??= VersionMatchSettings.Default;
        NetworkVersionRule? wire = null;
        if (settings.TryRule(row.Guid, out var rule))
        {
            wire = rule switch
            {
                ClientModRule.Exact => NetworkVersionRule.Exact,
                ClientModRule.Minor => NetworkVersionRule.Minor,
                ClientModRule.Any => NetworkVersionRule.None,
                ClientModRule.AtLeast => NetworkVersionRule.None,
                _ => null,
            };
        }
        else if (settings.Mode == VersionMatchingMode.Exact) wire = NetworkVersionRule.Exact;
        else if (settings.Mode == VersionMatchingMode.Minor) wire = NetworkVersionRule.Minor;

        if (wire == null || (wire.Value == row.VersionRule && row.Declared)) return row;
        if (row.Level == NetworkCompatibilityLevel.ClientOnly) return row;
        return new Handshake.ModRecord(row.Guid, row.Version, row.Level, wire.Value, declared: true);
    }

    /// <summary><see cref="RequestRow"/> over a list.</summary>
    internal static List<Handshake.ModRecord> RequestRows(IEnumerable<Handshake.ModRecord>? rows, VersionMatchSettings? settings)
    {
        var list = new List<Handshake.ModRecord>();
        if (rows == null) return list;
        foreach (var r in rows) list.Add(RequestRow(r, settings));
        return list;
    }

    /// <summary>The request capability bits that tell a client the strict mode (neither: declared ranges, or an older server).</summary>
    internal static Handshake.Capability ModeBits(VersionMatchSettings? settings) => (settings?.Mode ?? VersionMatchingMode.Range) switch
    {
        VersionMatchingMode.Exact => Handshake.Capability.MatchExact,
        VersionMatchingMode.Minor => Handshake.Capability.MatchSameMinor,
        _ => Handshake.Capability.None,
    };

    /// <summary>Client: the server's mode from its request bits (Range when neither is set).</summary>
    internal static VersionMatchingMode ModeOf(Handshake.Capability serverCaps) =>
        (serverCaps & Handshake.Capability.MatchExact) != 0 ? VersionMatchingMode.Exact
        : (serverCaps & Handshake.Capability.MatchSameMinor) != 0 ? VersionMatchingMode.Minor
        : VersionMatchingMode.Range;
}

/// <summary>
/// C3: where the server reads its version matching and its own mods' ranges. The admins' store sets
/// <see cref="SettingsSource"/>; the plugin registry sets <see cref="DeclarationSource"/>. Both default to "nothing set".
/// </summary>
internal static class VersionMatching
{
    /// <summary>The current server-wide setting and per-mod rules (the join-admin store). Null: <see cref="VersionMatchSettings.Default"/>.</summary>
    internal static Func<VersionMatchSettings>? SettingsSource { get; set; }

    /// <summary>A loaded mod's declaration by GUID (the plugin registry), or null when it has no attribute or is not loaded.</summary>
    internal static Func<string, CompatDeclaration?>? DeclarationSource { get; set; }

    internal static VersionMatchSettings Current()
    {
        try { return SettingsSource?.Invoke() ?? VersionMatchSettings.Default; }
        catch { return VersionMatchSettings.Default; }
    }

    /// <summary>
    /// The server's effective range for a row: the registry's declaration of the mod, else an explicit rule on the row
    /// (a <c>Require</c> with a level and rule), else the same minor version.
    /// </summary>
    internal static string EffectiveRangeOf(Handshake.ModRecord row)
    {
        CompatDeclaration? d = null;
        try { d = DeclarationSource?.Invoke(row.Guid); }
        catch { d = null; }
        if (d != null && d.EffectiveRange.Length > 0) return d.EffectiveRange;
        return CompatRanges.EffectiveText(row.Version, null, row.Declared, row.VersionRule) ?? "";
    }

    /// <summary>The server's declared <c>Peers</c> for a mod, or empty.</summary>
    internal static string DeclaredRangeOf(string guid)
    {
        try { return DeclarationSource?.Invoke(guid)?.DeclaredPeers ?? ""; }
        catch { return ""; }
    }

    /// <summary>A judge context for one answer: the current settings, this server's ranges, and the client's ranges when it sent them.</summary>
    internal static VersionJudgeContext ContextFor(IReadOnlyList<Handshake.ModRecord> serverRows, IReadOnlyDictionary<string, string>? clientRanges,
        VersionMatchSettings? settings = null)
    {
        var byGuid = new Dictionary<string, Handshake.ModRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in serverRows) if (!string.IsNullOrEmpty(r.Guid)) byGuid[r.Guid] = r;
        return new VersionJudgeContext
        {
            Settings = settings ?? Current(),
            ServerRange = g => byGuid.TryGetValue(g, out var r) ? EffectiveRangeOf(r) : null,
            ServerDeclaredRange = DeclaredRangeOf,
            ClientRanges = clientRanges,
        };
    }

    /// <summary>
    /// C3, the client's copy of the server's judge: a context for <see cref="VersionMatchJudge.Check"/> rebuilt from what the
    /// server told this client, so the join screens, the refusal rows and the catalog report judge a version exactly as the
    /// server's join check did instead of by the row's attribute rule.
    /// <list type="bullet">
    /// <item>The mode is the catalog's <see cref="ManifestBody.Matching"/>; each row's <see cref="ManifestMod.Match"/> (an
    /// admin's per-mod rule, or the strict mode's word) is a per-mod rule.</item>
    /// <item>The server's range for a mod is its catalog row's <see cref="ManifestMod.Peers"/>, else (a row a refusal's
    /// narrowed catalog left out) the effective range of the server's request row, as the server derives it.</item>
    /// <item>This client's range for a mod is the effective range of the copy at the version being judged
    /// (<paramref name="ownRangeOf"/>: mod id and version; for the loaded copy the one its feature table sends). Review
    /// fix: looked up by version, never by id alone, so a pending install, a kept copy or a turned-off copy at another
    /// version is never judged with the loaded copy's range. Unknown: the default for that version, as on the server.</item>
    /// </list>
    /// Null when the catalog does not carry a Version matching (no catalog, an older server, or the current form an older
    /// client gets): the caller keeps the request rows' rules, which the server rewrote for Exact or Same minor for all.
    /// </summary>
    internal static VersionJudgeContext? ClientContextFor(ManifestBody? catalog, IReadOnlyList<Handshake.ModRecord>? asked,
        Func<string, string, string?>? ownRangeOf)
    {
        if (catalog == null || !VersionMatchingText.TryParse(catalog.Matching, out var mode)) return null;
        var rules = new Dictionary<string, ClientModRule>(StringComparer.OrdinalIgnoreCase);
        var peers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in catalog.Mods ?? Array.Empty<ManifestMod>())
        {
            if (m == null || string.IsNullOrEmpty(m.Id)) continue;
            if (ClientModRules.TryParse(m.Match, out var rule) && !string.IsNullOrWhiteSpace(m.Match)) rules[m.Id] = rule;
            if (!string.IsNullOrWhiteSpace(m.Peers)) peers[m.Id] = m.Peers!.Trim();
        }

        var rows = new Dictionary<string, Handshake.ModRecord>(StringComparer.OrdinalIgnoreCase);
        if (asked != null)
            foreach (var r in asked)
                if (!string.IsNullOrEmpty(r.Guid) && !rows.ContainsKey(r.Guid)) rows[r.Guid] = r;

        return new VersionJudgeContext
        {
            Settings = new VersionMatchSettings(mode, rules),
            ServerRange = g => peers.TryGetValue(g, out var p) ? p
                : rows.TryGetValue(g, out var row) ? CompatRanges.EffectiveText(row.Version, null, row.Declared, row.VersionRule)
                : null,
            ServerDeclaredRange = g => peers.TryGetValue(g, out var p) ? p : null,
            // Non-null: this client declares ranges (the server judged it with its table). The ranges themselves come from
            // ClientRangeOf, by version.
            ClientRanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ClientRangeOf = (id, version) =>
            {
                if (ownRangeOf == null) return null;
                try { return ownRangeOf(id, version); }
                catch { return null; }
            },
        };
    }

    /// <summary>
    /// C3: this process's own effective range for mod <paramref name="id"/> at <paramref name="version"/> (the one its
    /// feature table sends), or null when the loaded copy is not at that version (a pending install, a kept or a
    /// turned-off copy: its range is not this one) or declares nothing.
    /// </summary>
    internal static string? OwnRangeOf(string id, string? version)
    {
        try
        {
            var d = DeclarationSource?.Invoke(id);
            if (d == null || !string.Equals(d.Version, (version ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            return d.EffectiveRange.Length > 0 ? d.EffectiveRange : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// C3: the phrase after <c>accepted</c> of the newest pass line in <paramref name="ctx"/> since <paramref name="before"/>
    /// lines (<c>by the client's range &gt;=1.4.0 &lt;1.5.0</c>), or empty when the check added none (equal versions).
    /// </summary>
    internal static string AcceptedPhrase(VersionJudgeContext ctx, int before)
    {
        if (ctx == null || ctx.Accepted.Count <= before) return "";
        string line = ctx.Accepted[ctx.Accepted.Count - 1];
        int at = line.IndexOf(" accepted ", StringComparison.Ordinal);
        string phrase = at >= 0 ? line.Substring(at + " accepted ".Length) : line;
        // The client-range line ends with "(server X)"; the report names the server's version itself.
        int server = phrase.LastIndexOf(" (server ", StringComparison.Ordinal);
        if (server > 0 && phrase.EndsWith(")", StringComparison.Ordinal)) phrase = phrase.Substring(0, server);
        return phrase;
    }
}

/// <summary>
/// C4 review fix: one generation per answering user, so a judgment that waits for the server's own Progenitor status
/// (<see cref="ProgenitorExact.JudgeWhenKnown"/>) is dropped when the user answers again or disconnects; a stale wait
/// must never judge a later session's answer. Game-free.
/// </summary>
internal sealed class AnswerGenerations
{
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _current = new();
    private int _next;

    /// <summary>A new answer from <paramref name="user"/>: its generation (earlier ones are superseded).</summary>
    internal int Next(int user)
    {
        lock (_gate)
        {
            int g = ++_next;
            if (_current.Count >= 512 && !_current.ContainsKey(user)) _current.Clear();
            _current[user] = g;
            return g;
        }
    }

    internal bool IsCurrent(int user, int generation)
    {
        lock (_gate) return _current.TryGetValue(user, out int g) && g == generation;
    }

    internal void Forget(int user)
    {
        lock (_gate) _current.Remove(user);
    }

    internal void Clear()
    {
        lock (_gate) _current.Clear();
    }
}

/// <summary>
/// C3, the deferred join hold of C1: the server waits a short, bounded time after a handshake answer for the client's
/// feature table (it carries the client's ranges) before judging the answer, then proceeds with whatever it has.
/// One hold per user; a new answer replaces the previous hold; a disconnect forgets it. Game-free: the live half
/// posts the expiry and feeds table arrivals.
/// </summary>
internal sealed class FeatureTableHold
{
    /// <summary>How long a join waits for the table after the answer. The client sends it right after its answer on the same reliable channel.</summary>
    internal const double WaitSeconds = 3;

    private readonly object _gate = new();
    private readonly Dictionary<int, (int Generation, int TablesAtAnswer, Action<string> Judge)> _waiting = new();
    private int _generation;

    /// <summary>
    /// Hold user <paramref name="user"/>'s answer until a full table newer than <paramref name="tablesAtAnswer"/> arrives
    /// (<see cref="OnTable"/>) or the hold expires (<see cref="TryExpire"/>). Returns the hold's generation.
    /// </summary>
    internal int Begin(int user, int tablesAtAnswer, Action<string> judge)
    {
        lock (_gate)
        {
            int g = ++_generation;
            if (_waiting.Count >= 512 && !_waiting.ContainsKey(user)) _waiting.Clear();
            _waiting[user] = (g, tablesAtAnswer, judge);
            return g;
        }
    }

    /// <summary>A full table from <paramref name="user"/> arrived; <paramref name="tablesNow"/> is its count. Returns the held judgment to run, or null.</summary>
    internal Action<string>? OnTable(int user, int tablesNow)
    {
        lock (_gate)
        {
            if (!_waiting.TryGetValue(user, out var w) || tablesNow <= w.TablesAtAnswer) return null;
            _waiting.Remove(user);
            return w.Judge;
        }
    }

    /// <summary>The hold's time is up: the held judgment to run, or null when it already ran, was replaced or forgotten.</summary>
    internal Action<string>? TryExpire(int user, int generation)
    {
        lock (_gate)
        {
            if (!_waiting.TryGetValue(user, out var w) || w.Generation != generation) return null;
            _waiting.Remove(user);
            return w.Judge;
        }
    }

    internal bool IsWaiting(int user)
    {
        lock (_gate) return _waiting.ContainsKey(user);
    }

    internal void Forget(int user)
    {
        lock (_gate) _waiting.Remove(user);
    }

    internal void Clear()
    {
        lock (_gate) _waiting.Clear();
    }
}
