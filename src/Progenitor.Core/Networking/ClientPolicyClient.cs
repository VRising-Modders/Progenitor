using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Progenitor.Plugins;

namespace Progenitor.Networking;

/// <summary>CLIENTPOLICY2: where a mod this server does not allow stands on this client.</summary>
internal enum JoinNotAllowedState
{
    /// <summary>Loaded now: it is in the report the server judges.</summary>
    Loaded = 0,

    /// <summary>Paused for this server (the per-server pause): unloaded until the player leaves.</summary>
    Paused = 1,

    /// <summary>Switched off in the Mods list; it is still loaded and goes at the next start.</summary>
    TurnsOffAtRestart = 2,
}

/// <summary>CLIENTPOLICY2: the one action a not-allowed row offers.</summary>
internal enum JoinNotAllowedAction
{
    /// <summary>Nothing left to do here (paused, or switched off for the next start).</summary>
    None = 0,

    /// <summary>A hot-loadable Progenitor mod: <b>Pause for this server</b> (the per-server pause; back when you leave).</summary>
    Pause = 1,

    /// <summary>
    /// A mod that cannot be unloaded now (a plain BepInEx plugin, <c>ctx.UnsafeHarmony</c>, loaded at start): <b>Turn off
    /// and restart</b> (the Mods list's switch for the next start, then Restart and rejoin).
    /// </summary>
    TurnOffRestart = 2,
}

/// <summary>CLIENTPOLICY2: one row of the red band's "Not allowed on this server" sub-section.</summary>
internal sealed class JoinNotAllowedRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";

    /// <summary><c>chainloader</c>, <c>progenitor</c> or <c>patcher</c> (where the report says it was loaded from).</summary>
    public string Source { get; init; } = "";

    /// <summary>Bootstrap's reloader can pause it now (a hot-loaded mod without <c>ctx.UnsafeHarmony</c>).</summary>
    public bool Pausable { get; init; }

    public JoinNotAllowedState State { get; init; }

    public JoinNotAllowedAction Action { get; init; }

    /// <summary>Still loaded and still something the player can fix here.</summary>
    public bool Unresolved => Action != JoinNotAllowedAction.None;
}

/// <summary>CLIENTPOLICY2: what the automatic pause takes before the client reports (or why it takes nothing).</summary>
internal sealed class ClientPolicyPausePlan
{
    internal static readonly ClientPolicyPausePlan Nothing = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "");

    internal ClientPolicyPausePlan(IReadOnlyList<string> pause, IReadOnlyList<string> notAllowed, IReadOnlyList<string> restartOnly, string why)
    {
        Pause = pause;
        NotAllowed = notAllowed;
        RestartOnly = restartOnly;
        Why = why ?? "";
    }

    /// <summary>The plugins to pause (Bootstrap's closure of the not-allowed pausable ones).</summary>
    public IReadOnlyList<string> Pause { get; }

    /// <summary>Every loaded mod the policy does not allow (GUIDs).</summary>
    public IReadOnlyList<string> NotAllowed { get; }

    /// <summary>Not-allowed mods that cannot be paused (loaded at start, <c>ctx.UnsafeHarmony</c>): the join is refused for them.</summary>
    public IReadOnlyList<string> RestartOnly { get; }

    /// <summary>Why nothing is paused although some could be; empty otherwise.</summary>
    public string Why { get; }
}

/// <summary>
/// CLIENTPOLICY2 (docs/client-mod-policy.md, "The player's join screens"): the client's reading of a server's signed
/// client mod policy — the required client rows, which loaded mods it does not allow, and the automatic pause. The
/// judgment is the server's own (<see cref="ClientModJudge.Allowed"/>) over the facts the client has: the server's
/// handshake request, the signed run list, required and recommended rows. Game-free.
/// </summary>
internal static class ClientPolicyView
{
    internal const string WhyNoPolicy = "this server's policy is not known on this client yet";
    internal const string WhyAutoPauseOff = ServerModPausePolicy.WhyAutoPauseOff;
    internal const string WhyAsked = "the pause would take a mod the server asks about";
    internal const string WhyRequired = "the pause would take a mod this server requires";
    internal const string WhyNonePausable = "the mods it does not allow were loaded at game start and cannot be paused";

    /// <summary>The policy a section carries, or null when it carries none.</summary>
    internal static ClientPolicyConfig? PolicyOf(JoinSection? section)
    {
        if (section == null || !section.HasClientPolicy) return null;
        var policy = new ClientPolicyConfig { Mode = section.PolicyMode };
        policy.Required.AddRange(section.RequiredClient);
        policy.Approved.AddRange(section.Approved);
        return policy;
    }

    /// <summary>
    /// The facts rule 2 reads, as this client knows them: the rows the server's handshake request asked about (their
    /// version rule), then the signed run list (any version), and the recommended ids and packages.
    /// </summary>
    internal static ClientModServerFacts FactsOf(JoinSection? section, IReadOnlyList<Handshake.ModRecord>? asked)
    {
        var facts = new ClientModServerFacts();
        foreach (var r in asked ?? Array.Empty<Handshake.ModRecord>())
        {
            if (string.IsNullOrEmpty(r.Guid) || Handshake.Compare.IsReservedGuid(r.Guid)) continue;
            facts.Runs[r.Guid] = (r.Version, r.Declared ? r.VersionRule : NetworkVersionRule.Exact);
        }

        if (section == null) return facts;
        foreach (string id in section.ServerRuns)
            if (!facts.Runs.ContainsKey(id)) facts.Runs[id] = ("", null);
        foreach (var m in section.Recommended)
        {
            if (m.Id.Length > 0) facts.Recommended.Add(m.Id);
            if (m.Package.Length > 0) facts.Recommended.Add(m.Package);
        }

        return facts;
    }

    /// <summary>The entries of <paramref name="entries"/> the policy does not allow (Only approved; empty otherwise).</summary>
    internal static List<ClientModEntry> NotAllowed(ClientPolicyConfig? policy, ClientModServerFacts facts, IEnumerable<ClientModEntry>? entries)
    {
        var list = new List<ClientModEntry>();
        if (policy == null || policy.Mode != ClientPolicyMode.Approved || entries == null) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e == null) continue;
            string key = e.Guid.Length > 0 ? e.Guid : e.Package;
            if (key.Length == 0 || !seen.Add(key)) continue;
            if (!ClientModJudge.Allowed(policy, facts, e)) list.Add(e);
        }

        return list;
    }

    /// <summary>
    /// The red band's "Not allowed on this server" rows: every loaded mod the policy does not allow (pausable: Pause for
    /// this server; else Turn off and restart, or nothing once it is switched off for the next start), then the mods this
    /// server's pause holds that it would not allow (resolved, shown so the player sees what went).
    /// </summary>
    internal static List<JoinNotAllowedRow> NotAllowedRows(JoinSection? section, IReadOnlyList<Handshake.ModRecord>? asked,
        IReadOnlyList<ClientModEntry>? loaded, IReadOnlyList<ClientModEntry>? pausedHere, Func<string, bool>? pausable,
        Func<string, bool>? turnsOffAtRestart)
    {
        var rows = new List<JoinNotAllowedRow>();
        var policy = PolicyOf(section);
        if (policy == null || policy.Mode != ClientPolicyMode.Approved) return rows;
        var facts = FactsOf(section, asked);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in NotAllowed(policy, facts, loaded))
        {
            seen.Add(e.Guid);
            bool canPause = e.Guid.Length > 0 && Ask(pausable, e.Guid);
            bool offLater = !canPause && e.Guid.Length > 0 && Ask(turnsOffAtRestart, e.Guid);
            rows.Add(new JoinNotAllowedRow
            {
                Id = e.Guid.Length > 0 ? e.Guid : e.Package,
                Name = e.Display,
                Version = e.Version,
                Source = e.Source,
                Pausable = canPause,
                State = offLater ? JoinNotAllowedState.TurnsOffAtRestart : JoinNotAllowedState.Loaded,
                Action = canPause ? JoinNotAllowedAction.Pause : offLater ? JoinNotAllowedAction.None : JoinNotAllowedAction.TurnOffRestart,
            });
        }

        foreach (var e in NotAllowed(policy, facts, pausedHere))
        {
            if (e.Guid.Length == 0 || !seen.Add(e.Guid)) continue;
            rows.Add(new JoinNotAllowedRow
            {
                Id = e.Guid, Name = e.Display, Version = e.Version, Source = e.Source, Pausable = true,
                State = JoinNotAllowedState.Paused, Action = JoinNotAllowedAction.None,
            });
        }

        return rows;
    }

    private static bool Ask(Func<string, bool>? probe, string id)
    {
        if (probe == null) return false;
        try { return probe(id); }
        catch { return false; }
    }

    /// <summary>
    /// The automatic pause (docs/client-mod-policy.md, "Automatic pausing"): before this client answers a server that
    /// asked for its report, pause every loaded mod that server's policy does not allow and that can be paused, so an
    /// honest player with only hot-loadable extras joins with no clicks. Pauses only when all of these hold: the policy
    /// is known (signed and remembered for this server), Only approved, auto-pause is on for the server, and Bootstrap's
    /// closure of the candidates takes no mod the server asks about (by its GUID or a row it provides) and no mod the
    /// policy requires. Not-allowed mods that cannot be paused stay loaded (the join is refused for them, with Turn off
    /// and restart on the screens).
    /// </summary>
    internal static ClientPolicyPausePlan PlanPause(
        JoinSection? policySection,
        IReadOnlyList<Handshake.ModRecord>? asked,
        IReadOnlyList<ClientModEntry>? loaded,
        IReadOnlyCollection<string>? suspendable,
        Func<IReadOnlyList<string>, (IReadOnlyList<string> Closure, string Refusal)>? closure,
        IReadOnlyList<Handshake.ModRecord>? provided,
        IReadOnlyDictionary<string, string>? owners,
        bool autoPause)
    {
        var policy = PolicyOf(policySection);
        if (policy == null || policy.Mode != ClientPolicyMode.Approved) return ClientPolicyPausePlan.Nothing;
        var facts = FactsOf(policySection, asked);
        var notAllowed = NotAllowed(policy, facts, loaded);
        if (notAllowed.Count == 0) return ClientPolicyPausePlan.Nothing;
        var canPause = new HashSet<string>(suspendable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        var candidates = new List<string>();
        var restart = new List<string>();
        foreach (var e in notAllowed)
        {
            if (e.Guid.Length == 0) continue;
            ids.Add(e.Guid);
            if (canPause.Contains(e.Guid)) candidates.Add(e.Guid);
            else restart.Add(e.Guid);
        }

        if (candidates.Count == 0) return new ClientPolicyPausePlan(Array.Empty<string>(), ids, restart, restart.Count > 0 ? WhyNonePausable : "");
        if (!autoPause) return new ClientPolicyPausePlan(Array.Empty<string>(), ids, restart, WhyAutoPauseOff);
        if (closure == null) return new ClientPolicyPausePlan(Array.Empty<string>(), ids, restart, ServerModPausePolicy.WhyNotClean);

        IReadOnlyList<string> set;
        string refusal;
        try { (set, refusal) = closure(candidates); }
        catch (Exception e) { (set, refusal) = (Array.Empty<string>(), e.Message); }
        if (set == null || set.Count == 0)
            return new ClientPolicyPausePlan(Array.Empty<string>(), ids, restart, string.IsNullOrEmpty(refusal) ? ServerModPausePolicy.WhyNotClean : refusal);

        var askedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in asked ?? Array.Empty<Handshake.ModRecord>())
            if (!string.IsNullOrEmpty(r.Guid)) askedIds.Add(r.Guid);
        foreach (string plugin in set)
        {
            if (askedIds.Contains(plugin)) return new ClientPolicyPausePlan(Array.Empty<string>(), ids, restart, WhyAsked);
            foreach (var r in provided ?? Array.Empty<Handshake.ModRecord>())
                if (askedIds.Contains(r.Guid) && owners != null && owners.TryGetValue(r.Guid, out var o)
                    && string.Equals(o, plugin, StringComparison.OrdinalIgnoreCase))
                    return new ClientPolicyPausePlan(Array.Empty<string>(), ids, restart, WhyAsked);
            if (policy.IndexOfRequired(plugin) >= 0) return new ClientPolicyPausePlan(Array.Empty<string>(), ids, restart, WhyRequired);
        }

        return new ClientPolicyPausePlan(new List<string>(set), ids, restart, "");
    }

    // ---- the policy this client remembers per server (progenitor-server-profiles.json) ------------------------------

    /// <summary>
    /// The part of a signed section the automatic pause needs before the next join's report (the request carries only
    /// a capability bit): the mode, the required and approved rows, the recommended keys and the run list, as one JSON
    /// object. Empty when the section carries no policy (the profile then forgets any it had).
    /// </summary>
    internal static string Remembered(JoinSection? section)
    {
        if (section == null || !section.HasClientPolicy) return "";
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("mode", ClientModRules.ModeWord(section.PolicyMode));
            WriteRows(w, "required", section.RequiredClient);
            WriteRows(w, "approved", section.Approved);
            w.WriteStartArray("recommended");
            foreach (var m in section.Recommended)
            {
                w.WriteStartObject();
                w.WriteString("id", m.Id);
                w.WriteString("package", m.Package);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("runs");
            foreach (string id in section.ServerRuns) w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRows(Utf8JsonWriter w, string key, IReadOnlyList<ClientModRow> rows)
    {
        w.WriteStartArray(key);
        foreach (var r in rows)
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id);
            w.WriteString("package", r.Package);
            w.WriteString("version", r.Version);
            w.WriteString("rule", ClientModRules.Word(r.Rule));
            w.WriteString("name", r.Name);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    /// <summary>A remembered policy as a section (no message), or null when the text is empty or unreadable.</summary>
    internal static JoinSection? FromRemembered(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json!.Length > 256 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            ClientModRules.TryParseMode(Str(root, "mode"), out var mode);
            var required = ReadRows(root, "required", ClientModPolicyLimits.MaxRequired);
            var approved = ReadRows(root, "approved", ClientModPolicyLimits.MaxApproved);
            var recommended = new List<RecommendedMod>();
            if (root.TryGetProperty("recommended", out var rec) && rec.ValueKind == JsonValueKind.Array)
                foreach (var m in rec.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object || recommended.Count >= JoinSectionLimits.MaxRecommended) continue;
                    recommended.Add(new RecommendedMod(Str(m, "id"), Str(m, "package"), "", ""));
                }

            var runs = new List<string>();
            if (root.TryGetProperty("runs", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var v in r.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String) runs.Add(v.GetString() ?? "");
            return new JoinSection("", null, recommended, null).WithPolicy(required, mode, approved, runs);
        }
        catch (JsonException) { return null; }
    }

    private static List<ClientModRow> ReadRows(JsonElement root, string key, int max)
    {
        var rows = new List<ClientModRow>();
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return rows;
        foreach (var o in arr.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object || rows.Count >= max) continue;
            ClientModRules.TryParse(Str(o, "rule"), out var rule);
            var row = new ClientModRow(Str(o, "id"), Str(o, "package"), Str(o, "version"), rule, "", Str(o, "name"));
            if (row.Key.Length > 0) rows.Add(row);
        }

        return rows;
    }

    private static string Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- words -------------------------------------------------------------------------------------------------------

    /// <summary>The sub-heading inside the red band (drawn in capitals).</summary>
    internal const string NotAllowedHeading = "Not allowed on this server";

    internal const string PauseButton = "Pause for this server";
    internal const string TurnOffRestartButton = "Turn off and restart";
    internal const string StaysOffLine = "It stays off until you turn it back on in the Mods list.";
    internal const string PausedWord = "Paused";
    internal const string TurnsOffWord = "Off at restart";
    internal const string NotAllowedWord = "Not allowed";

    /// <summary>The message page's line when the server checks client mods.</summary>
    internal const string ChecksLine = "This server checks your client mods.";

    /// <summary><c>Paused 1 mod this server doesn't allow: A. It comes back when you leave.</c> (plural: <c>2 mods</c>, <c>They come back</c>).</summary>
    internal static string PausedNotice(IReadOnlyList<string> names)
    {
        int n = names?.Count ?? 0;
        return "Paused " + ServerModPauseText.Mods(n) + " this server doesn't allow: "
            + ServerModPauseText.Join(names) + (n == 1 ? ". It comes back when you leave." : ". They come back when you leave.");
    }

    /// <summary>
    /// The full second line of a not-allowed row, for the readback (the drawn line is fitted to the row's width,
    /// <see cref="JoinFlowLayout.NotAllowedLine"/>). The "stays off" sentence is the Turn off and restart confirmation's.
    /// </summary>
    internal static string RowLine(JoinNotAllowedRow row)
    {
        if (row == null) return "";
        var parts = new List<string> { RowLead(row) };
        if (row.Version.Length > 0) parts.Add(row.Version);
        parts.Add(RowDetail(row));
        return string.Join(" · ", parts);
    }

    /// <summary>The short words after a not-allowed row's version: <c>back when you leave</c>, <c>can't be paused</c>, <c>off at the next start</c>.</summary>
    internal static string RowDetail(JoinNotAllowedRow row) => row?.State switch
    {
        JoinNotAllowedState.Paused => "back when you leave",
        JoinNotAllowedState.TurnsOffAtRestart => "off at the next start",
        _ => row?.Pausable == true ? "back when you leave" : "can't be paused",
    };

    /// <summary>The Turn off and restart confirmation's title.</summary>
    internal const string TurnOffConfirmTitle = "Turn off and restart?";

    /// <summary>
    /// The confirmation's body (the row is too narrow for it): <c>Alpha can't be paused for one server, so it is
    /// switched off in the Mods list and goes at the next start of V Rising. It stays off until you turn it back on in the Mods list.</c>
    /// </summary>
    internal static string TurnOffConfirmBody(string names, int count) =>
        names + (count == 1 ? " can't be paused for one server, so it is switched off in the Mods list and goes"
            : " can't be paused for one server, so they are switched off in the Mods list and go")
        + " at the next start of V Rising. " + (count == 1 ? StaysOffLine : "They stay off until you turn them back on in the Mods list.");

    /// <summary>The confirmation's Cancel.</summary>
    internal const string NothingTurnedOff = "Nothing was turned off.";

    /// <summary>The mods page's line while the confirmation is open.</summary>
    internal static string TurnOffAsked(string names) => "Confirm turning off " + names + ".";

    /// <summary>The colored lead of a not-allowed row: <c>Not allowed</c>, <c>Paused for this server</c>, <c>Turns off at restart</c>.</summary>
    internal static string RowLead(JoinNotAllowedRow row) => row?.State switch
    {
        JoinNotAllowedState.Paused => "Paused for this server",
        JoinNotAllowedState.TurnsOffAtRestart => "Turns off at restart",
        _ => NotAllowedWord,
    };

    /// <summary>The action cell: the button caption, or the resolved word.</summary>
    internal static string ActionCaption(JoinNotAllowedRow row) => row?.Action switch
    {
        JoinNotAllowedAction.Pause => PauseButton,
        JoinNotAllowedAction.TurnOffRestart => TurnOffRestartButton,
        _ => row?.State == JoinNotAllowedState.TurnsOffAtRestart ? TurnsOffWord : PausedWord,
    };

    internal static string ActionName(JoinNotAllowedAction action) => action switch
    {
        JoinNotAllowedAction.Pause => "pause",
        JoinNotAllowedAction.TurnOffRestart => "turn-off-restart",
        _ => "none",
    };

    internal static string StateName(JoinNotAllowedState state) => state switch
    {
        JoinNotAllowedState.Paused => "paused",
        JoinNotAllowedState.TurnsOffAtRestart => "turns-off-at-restart",
        _ => "loaded",
    };

    /// <summary><c>Alpha is paused for this server. Join now; no download or restart needed.</c></summary>
    internal static string PausedJoinNow(string names) => names + (names.IndexOf(',') >= 0 ? " are" : " is")
        + " paused for this server. Join now; no download or restart needed.";

    /// <summary><c>Alpha turns off at the next start of V Rising. It stays off until you turn it back on in the Mods list.</c></summary>
    internal static string TurnsOffAtNextStart(string names) => names + (names.IndexOf(',') >= 0 ? " turn" : " turns")
        + " off at the next start of V Rising. " + StaysOffLine;

    /// <summary><c>Can't pause Alpha: &lt;why&gt;</c></summary>
    internal static string PauseFailed(string name, string why) =>
        "Can't pause " + name + ": " + (string.IsNullOrWhiteSpace(why) ? "it could not be paused." : why.Trim());

    /// <summary><c>Can't turn off Alpha: &lt;why&gt;</c></summary>
    internal static string TurnOffFailed(string name, string why) =>
        "Can't turn off " + name + ": " + (string.IsNullOrWhiteSpace(why) ? "the switch refused." : why.Trim());

    internal const string PauseNotAvailable = "Pausing mods is not available here. Turn it off in Mods, then join again.";
}
