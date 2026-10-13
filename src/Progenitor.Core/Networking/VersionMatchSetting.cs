using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// Mod compatibility C3: one admins' per-mod rule for a mod the server runs (<c>serverModRules</c> in
/// <c>progenitor-join-admin.json</c>). It wins over the server-wide <b>Version matching</b> for that mod.
/// </summary>
internal sealed class ServerModRule
{
    public ServerModRule(string? id, ClientModRule rule, string? name = "")
    {
        Id = (id ?? "").Trim();
        Rule = rule;
        Name = (name ?? "").Trim();
    }

    /// <summary>The plugin GUID.</summary>
    public string Id { get; }

    public ClientModRule Rule { get; }

    /// <summary>The readable name the panel knew when the rule was set, or empty.</summary>
    public string Name { get; }

    public string Describe() => Id + " " + ClientModRules.Word(Rule);
}

/// <summary>C3: the caps of the version matching store.</summary>
internal static class VersionMatchLimits
{
    /// <summary>Stored per-mod rules.</summary>
    internal const int MaxServerModRules = 64;

    /// <summary>Mods the server runs that one state lists (the panel's rule rows).</summary>
    internal const int MaxServerModRows = 64;
}

/// <summary>
/// C3: <c>[ClientPolicy] VersionMatching</c> in the server's <c>progenitor.cfg</c>. Empty (the default): the admins' choice in
/// <c>progenitor-join-admin.json</c> applies. <c>range</c>, <c>minor</c> or <c>exact</c>: forced, and the in-game control is
/// read-only. An unknown word is warned once and ignored. Read on use (a cfg reload applies it). Game-free: the live server
/// sets <see cref="Source"/>.
/// </summary>
internal static class VersionMatchCfg
{
    internal const string Section = "ClientPolicy";
    internal const string Key = "VersionMatching";

    internal const string Description =
        "Server. Forces Version matching for mods players have: range (each mod's declared range), minor (same minor for all) or "
        + "exact (exact version for all). Empty: the admins' choice in the game (Mods players need) applies. When set, that choice is "
        + "read-only in the game. Per-mod rules set by admins still win for their mod.";

    /// <summary>The cfg value now (live: the bound entry), or null when not bound.</summary>
    internal static Func<string?>? Source { get; set; }

    /// <summary>Where the one warning per unknown word goes (live: the BepInEx log).</summary>
    internal static Action<string>? Warn { get; set; }

    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cfg text now (empty when not bound).</summary>
    internal static string Text()
    {
        try { return (Source?.Invoke() ?? "").Trim(); }
        catch { return ""; }
    }

    /// <summary>The mode <paramref name="text"/> forces, or null (empty, or an unknown word, which is warned once).</summary>
    internal static VersionMatchingMode? Forced(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (VersionMatchingText.TryParse(t, out var mode)) return mode;
        if (Warned.Add(t))
            Warn?.Invoke("[" + Section + "] " + Key + " = '" + t + "' is not range, minor or exact; the admins' choice applies.");
        return null;
    }

    /// <summary>The mode the cfg forces now, or null.</summary>
    internal static VersionMatchingMode? ForcedNow() => Forced(Text());

    /// <summary>The note on the read-only control, and the refusal of an edit of it.</summary>
    internal static string ForcedNote(VersionMatchingMode mode) =>
        "Set in the server's config file ([" + Section + "] " + Key + " = " + VersionMatchingText.Word(mode) + ").";

    internal static void Reset()
    {
        Source = null;
        Warn = null;
        Warned.Clear();
    }
}

/// <summary>C3: builds the judge's <see cref="VersionMatchSettings"/> from the store and the cfg override. Game-free.</summary>
internal static class VersionMatchSetting
{
    /// <summary>The effective mode: the cfg override when set, else the stored choice.</summary>
    internal static VersionMatchingMode Effective(JoinAdminConfig? config, VersionMatchingMode? forced) =>
        forced ?? config?.VersionMatching ?? VersionMatchingMode.Range;

    internal static VersionMatchSettings Build(JoinAdminConfig? config, VersionMatchingMode? forced)
    {
        var rules = new Dictionary<string, ClientModRule>(StringComparer.OrdinalIgnoreCase);
        if (config != null)
            foreach (var r in config.ServerModRules)
                if (r.Id.Length > 0) rules[r.Id] = r.Rule;
        return new VersionMatchSettings(Effective(config, forced), rules);
    }
}

/// <summary>C3: one mod the server runs as the panel lists it, with the admins' rule for it (empty: the server setting).</summary>
internal sealed class JoinAdminServerModRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>The per-mod rule's word (<c>exact</c>, <c>at-least</c>, <c>minor</c>, <c>any</c>), or empty for none.</summary>
    public string Rule { get; set; } = "";

    /// <summary>False for a stored rule whose mod the server does not run now (listed so it can be cleared).</summary>
    public bool Runs { get; set; } = true;

    public string Key => Id;

    public string Display => Name.Length > 0 ? Name : Id;
}

/// <summary>C3: the panel's and the Server tab's words for Version matching and the per-mod rules.</summary>
internal static class VersionMatchText
{
    /// <summary>The per-mod rule word that clears a rule (the request's <c>rule</c>; also an empty word).</summary>
    internal const string WordDefault = "default";

    internal const string ModsHeading = "Rules for this server's mods";
    internal const string UseServerSetting = "Use the server setting";
    internal const string ServerSettingCaption = "Server setting";
    internal const string NoMods = "This server runs no mods a rule can be set for.";
    internal const string NotRunning = "not on this server now";
    internal const string NotRunError = "This server doesn't run that mod. These rules are only for mods the server runs.";
    internal const string ProgenitorError = "Progenitor itself takes no rule here.";

    /// <summary>The note after a change: nobody is disconnected.</summary>
    internal const string NextJoinNote = "Applies from each player's next join.";

    /// <summary>The Server tab row's label.</summary>
    internal const string Label = VersionMatchingText.Heading;

    /// <summary>The Server tab's summary row of the per-mod rules.</summary>
    internal const string RulesLabel = "Rules for one mod";

    internal const string RulesNone = "None";

    internal const string RulesHelp = VersionMatchingText.ModRulesHelp + " Set them in the Players joining popup.";

    internal const string Help =
        "Which client versions of the mods this server runs may join. A rule set on one mod in the Players joining popup wins over this "
        + "for that mod; required client mods keep their own rule. Applies from each player's next join.";

    /// <summary>RADIO1: the radio list's options (values as the requests send them).</summary>
    internal static IReadOnlyList<UiChoiceOption> Options
    {
        get
        {
            var list = new List<UiChoiceOption>();
            foreach (var o in VersionMatchingText.Options) list.Add(new UiChoiceOption(o.Word, o.Label, o.Help));
            return list;
        }
    }

    /// <summary>A per-mod rule word as stored: <c>exact</c>, <c>at-least</c>, <c>minor</c>, <c>any</c>, or empty for none. False for an unknown word.</summary>
    internal static bool TryParseRule(string? word, out string normalized)
    {
        string w = (word ?? "").Trim();
        normalized = "";
        if (w.Length == 0 || string.Equals(w, WordDefault, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w, "server", StringComparison.OrdinalIgnoreCase) || string.Equals(w, "none", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!ClientModRules.TryParse(w, out var rule)) return false;
        normalized = ClientModRules.Word(rule);
        return true;
    }

    /// <summary>The Rule button's cycle: the server setting → Exact → or newer → Same minor → Any version → the server setting.</summary>
    internal static string NextRule(string? word)
    {
        if (!TryParseRule(word, out string w) || w.Length == 0) return ClientModRules.WordExact;
        return w switch
        {
            ClientModRules.WordExact => ClientModRules.WordAtLeast,
            ClientModRules.WordAtLeast => ClientModRules.WordMinor,
            ClientModRules.WordMinor => ClientModRules.WordAny,
            _ => "",
        };
    }

    /// <summary>
    /// The rule in full: <c>Use the server setting</c>, <c>Exact 1.2.0</c>, <c>1.2.0 or newer</c>, <c>Same minor as 1.2.0</c>,
    /// <c>Any version</c>; without a version, the short word (<c>Exact</c>).
    /// </summary>
    internal static string RuleLabel(string? word, string? version)
    {
        if (!TryParseRule(word, out string w) || w.Length == 0) return UseServerSetting;
        ClientModRules.TryParse(w, out var rule);
        // A stored rule for a mod the server no longer runs has no version to name: the rule's short word.
        return (version ?? "").Trim().Length == 0 ? ClientPolicyText.RuleCaption(rule) : ClientModRules.Label(rule, version);
    }

    /// <summary>The Rule button's caption (short).</summary>
    internal static string RuleCaption(string? word)
    {
        if (!TryParseRule(word, out string w) || w.Length == 0) return ServerSettingCaption;
        ClientModRules.TryParse(w, out var rule);
        return ClientPolicyText.RuleCaption(rule);
    }

    /// <summary>The second line of a mod's row: its id (under a name), its version, the rule in full.</summary>
    internal static string Second(JoinAdminServerModRow r)
    {
        var parts = new List<string>();
        if (r.Name.Length > 0 && r.Id.Length > 0) parts.Add(r.Id);
        if (r.Version.Length > 0) parts.Add(r.Version);
        parts.Add(RuleLabel(r.Rule, r.Version));
        if (!r.Runs) parts.Add(NotRunning);
        return string.Join(" · ", parts);
    }

    /// <summary>The Server tab's summary: <c>None</c>, or <c>ChatSkin: Exact 1.2.0; Robes: Any version</c>.</summary>
    internal static string RulesSummary(IReadOnlyList<JoinAdminServerModRow>? rows)
    {
        var parts = new List<string>();
        if (rows != null)
            foreach (var r in rows)
                if (r.Rule.Length > 0) parts.Add(r.Display + ": " + RuleLabel(r.Rule, r.Version));
        return parts.Count == 0 ? RulesNone : string.Join("; ", parts);
    }

    /// <summary>The count beside the section heading: <c>2 rules</c>.</summary>
    internal static string RuleCount(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " rule" : " rules");
}

/// <summary>C3: the decisions of <c>version-matching</c> and <c>server-mod-rule</c> (after rate limit, handshake, authorization and lock).</summary>
internal static class VersionMatchDecisions
{
    internal static JoinAdminDecision Decide(JoinAdminConfig current, JoinAdminRequest request, string op, string id,
        IReadOnlyCollection<string> serverMods, VersionMatchingMode? forced)
    {
        if (op == JoinAdminOps.VersionMatching)
        {
            string row = JoinAdminPolicy.AuditRowFor(op, "");
            string value = request.Value ?? "";
            if (!VersionMatchingText.TryParse(value, out var mode))
                return JoinAdminDecision.Refused(current, row, "Choose range, minor or exact.", after: value);
            string before = VersionMatchingText.Word(current.VersionMatching), after = VersionMatchingText.Word(mode);
            if (forced is { } f) return JoinAdminDecision.Refused(current, row, VersionMatchCfg.ForcedNote(f), before, after);
            if (current.VersionMatching == mode)
                return new JoinAdminDecision { Ok = true, Config = current, AuditRow = row, Before = before, After = after, Note = "Unchanged." };
            var next = current.Clone();
            next.VersionMatching = mode;
            return new JoinAdminDecision
            {
                Ok = true, Config = next, MatchingChanged = true, AuditRow = row, Before = before, After = after, Note = VersionMatchText.NextJoinNote,
            };
        }

        // server-mod-rule
        string ruleRow = JoinAdminPolicy.AuditRowFor(op, id);
        if (id.Length == 0) return JoinAdminDecision.Refused(current, ruleRow, "Name the mod (its plugin id).");
        if (!ModManifestIds.TryValidateId(id, out var idError)) return JoinAdminDecision.Refused(current, ruleRow, "The mod id is not valid (" + idError + ").");
        if (!VersionMatchText.TryParseRule(request.Rule, out string word))
            return JoinAdminDecision.Refused(current, ruleRow, "Choose a rule: exact, at-least, minor, any or default.", after: request.Rule ?? "");
        int at = current.IndexOfServerModRule(id);
        string was = at >= 0 ? ClientModRules.Word(current.ServerModRules[at].Rule) : VersionMatchText.WordDefault;
        string now = word.Length > 0 ? word : VersionMatchText.WordDefault;
        if (word.Length == 0)
        {
            if (at < 0) return new JoinAdminDecision { Ok = true, Config = current, AuditRow = ruleRow, Before = was, After = now, Note = "Unchanged." };
            var cleared = current.Clone();
            cleared.ServerModRules.RemoveAt(at);
            return new JoinAdminDecision
            {
                Ok = true, Config = cleared, MatchingChanged = true, AuditRow = ruleRow, Before = was, After = now, Note = VersionMatchText.NextJoinNote,
            };
        }

        if (ClientModJudge.AlwaysApproved(id) || Handshake.Compare.IsReservedGuid(id))
            return JoinAdminDecision.Refused(current, ruleRow, VersionMatchText.ProgenitorError, was, now);
        if (!Contains(serverMods, id)) return JoinAdminDecision.Refused(current, ruleRow, VersionMatchText.NotRunError, was, now);
        string name = (request.Name ?? "").Trim();
        if (JoinAdminPolicy.CheckName(name) is { } badName) return JoinAdminDecision.Refused(current, ruleRow, badName, was, now);
        ClientModRules.TryParse(word, out var rule);
        if (at >= 0 && current.ServerModRules[at].Rule == rule)
            return new JoinAdminDecision { Ok = true, Config = current, AuditRow = ruleRow, Before = was, After = now, Note = "Unchanged." };
        if (at < 0 && current.ServerModRules.Count >= VersionMatchLimits.MaxServerModRules)
            return JoinAdminDecision.Refused(current, ruleRow, "At most " + VersionMatchLimits.MaxServerModRules + " mods can have their own rule.", was, now);
        var next2 = current.Clone();
        if (name.Length == 0 && at >= 0) name = current.ServerModRules[at].Name;
        var stored = new ServerModRule(id, rule, name);
        if (at >= 0) next2.ServerModRules[at] = stored;
        else next2.ServerModRules.Add(stored);
        return new JoinAdminDecision
        {
            Ok = true, Config = next2, MatchingChanged = true, AuditRow = ruleRow, Before = was, After = now, Note = VersionMatchText.NextJoinNote,
        };
    }

    private static bool Contains(IReadOnlyCollection<string>? ids, string id)
    {
        if (ids == null) return false;
        foreach (string s in ids)
            if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
