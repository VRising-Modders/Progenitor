using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Progenitor.Install;

namespace Progenitor.Networking;

/// <summary>
/// JOINADMIN1: who is offered the "Players joining" entry point (the Install toolbar button before the move, the
/// Server settings row now; one rule): the server has the channel (its hello), and the remote-install status says
/// this player is authorized when the server sent one, else the hello's admin flag. Only visibility: the server
/// authorizes every request again.
/// </summary>
internal static class JoinAdminOffer
{
    internal static bool Offered(bool supports, bool helloAdmin, RemoteInstallStatus? status) =>
        supports && (status != null ? status.Authorized : helloAdmin);
}

/// <summary>JOINADMIN1 (join flow stage 3): the size and text limits of the admin's "Players joining" choices.</summary>
internal static class JoinAdminLimits
{
    /// <summary>Recommended client mods: the join section's own cap, so every stored row can be signed.</summary>
    internal const int MaxRecommended = JoinSectionLimits.MaxRecommended;

    /// <summary>The admin's reason, in characters (the section allows 200; the panel asks for a short line).</summary>
    internal const int MaxReasonChars = 120;

    /// <summary><c>Networking.InstallGraceSeconds</c> as the panel edits it: 0 (off) to 300.</summary>
    internal const int MaxWindowSeconds = 300;

    /// <summary>One request or answer on the wire (inside <c>CustomEvents.MaxPayloadBytes</c>).</summary>
    internal const int MaxPacketBytes = 15 * 1024;

    internal const int MaxAlertRows = 5;

    /// <summary>The store file is refused above this size (a hand edit gone wrong).</summary>
    internal const int MaxConfigFileBytes = 256 * 1024;

    /// <summary>The reason a newly added recommendation starts with.</summary>
    internal const string PlaceholderReason = "Add a short reason players will see.";
}

/// <summary>
/// JOINADMIN1: the admin's choices, stored server-side in <c>BepInEx/config/progenitor-join-admin.json</c>:
/// the recommended client mods (id, package, version, reason) and the client mod policy. (The ids of the
/// mods the server shared with refused players went 2026-10-05; a stored <c>shared</c> list loads and is
/// ignored, and the next write drops it.) The install window's length is not here: it stays
/// <c>Networking.InstallGraceSeconds</c> in <c>progenitor.cfg</c>, which the handshake already reads.
/// </summary>
internal sealed class JoinAdminConfig
{
    /// <summary>CLIENTPOLICY1: schema 2 adds <c>requiredClient</c> and <c>clientPolicy</c>; a schema 1 file loads with their defaults.</summary>
    internal const int Schema = 2;

    internal const int SchemaV1 = 1;

    public List<RecommendedMod> Recommended { get; } = new();

    /// <summary>CLIENTPOLICY1: the required client mods, the mode, the approved list and the can't-be-checked choice.</summary>
    public ClientPolicyConfig Policy { get; private set; } = new();

    /// <summary>
    /// A readable name per recommended plugin id (lowercase id → name, at most 64 characters), when the admin's
    /// picker knew one. Signed into the join section's <c>names</c>, so a player who lacks the mod sees its name.
    /// </summary>
    public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Mod compatibility C3: the server-wide <b>Version matching</b> (stored as <c>versionMatching</c>; absent reads as
    /// <c>range</c>). <c>[ClientPolicy] VersionMatching</c> in the server's cfg overrides it (<see cref="VersionMatchCfg"/>).
    /// </summary>
    public VersionMatchingMode VersionMatching { get; set; } = VersionMatchingMode.Range;

    /// <summary>C3: the admins' per-mod rules for mods the server runs (<c>serverModRules</c>, at most 64), which win over the mode.</summary>
    public List<ServerModRule> ServerModRules { get; } = new();

    public JoinAdminConfig Clone()
    {
        var copy = new JoinAdminConfig();
        copy.Recommended.AddRange(Recommended);
        foreach (var pair in Names) copy.Names[pair.Key] = pair.Value;
        copy.Policy = Policy.Clone();
        copy.VersionMatching = VersionMatching;
        copy.ServerModRules.AddRange(ServerModRules);
        return copy;
    }

    /// <summary>C3: the stored per-mod rule of plugin <paramref name="id"/> (case-insensitive), or -1.</summary>
    public int IndexOfServerModRule(string? id)
    {
        string k = (id ?? "").Trim();
        if (k.Length == 0) return -1;
        for (int i = 0; i < ServerModRules.Count; i++)
            if (string.Equals(ServerModRules[i].Id, k, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The stored readable name of recommended id <paramref name="id"/>, or empty.</summary>
    public string NameOf(string? id) =>
        !string.IsNullOrEmpty(id) && Names.TryGetValue(id!.ToLowerInvariant(), out var n) ? n : "";

    /// <summary>A recommended row's key: its plugin id, else its Thunderstore package.</summary>
    internal static string KeyOf(RecommendedMod mod) => mod.Id.Length > 0 ? mod.Id : mod.Package;

    /// <summary>The stored row whose id or package is <paramref name="key"/> (case-insensitive), or -1.</summary>
    public int IndexOfRecommended(string? key)
    {
        string k = (key ?? "").Trim();
        if (k.Length == 0) return -1;
        for (int i = 0; i < Recommended.Count; i++)
        {
            var r = Recommended[i];
            if ((r.Id.Length > 0 && string.Equals(r.Id, k, StringComparison.OrdinalIgnoreCase))
                || (r.Package.Length > 0 && string.Equals(r.Package, k, StringComparison.OrdinalIgnoreCase)))
                return i;
        }

        return -1;
    }


    /// <summary>The file form: indented JSON, rows in the admin's order.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
               { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartArray("recommended");
            foreach (var r in Recommended)
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteString("package", r.Package);
                w.WriteString("version", r.Version);
                w.WriteString("reason", r.Reason);
                string name = NameOf(r.Id);
                if (name.Length > 0) w.WriteString("name", name);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("requiredClient");
            foreach (var r in Policy.Required) WriteClientRow(w, r, withReason: true);
            w.WriteEndArray();
            w.WriteStartObject("clientPolicy");
            w.WriteString("mode", ClientModRules.ModeWord(Policy.Mode));
            w.WriteStartArray("approved");
            foreach (var r in Policy.Approved) WriteClientRow(w, r, withReason: false);
            w.WriteEndArray();
            w.WriteString("unverified", ClientModRules.UnverifiedWord(Policy.Unverified));
            // CLIENTPOLICY3: a new key in schema 2 (a file without it reads as Warn, its default).
            w.WriteString("midSession", ClientModRules.MidSessionWord(Policy.MidSession));
            w.WriteEndObject();
            // Mod compatibility C3: new keys in schema 2 (a file without them reads as range with no per-mod rules).
            w.WriteString("versionMatching", VersionMatchingText.Word(VersionMatching));
            w.WriteStartArray("serverModRules");
            foreach (var r in ServerModRules)
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteString("rule", ClientModRules.Word(r.Rule));
                if (r.Name.Length > 0) w.WriteString("name", r.Name);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteClientRow(Utf8JsonWriter w, ClientModRow r, bool withReason)
    {
        w.WriteStartObject();
        w.WriteString("id", r.Id);
        w.WriteString("package", r.Package);
        w.WriteString("version", r.Version);
        w.WriteString("rule", ClientModRules.Word(r.Rule));
        if (withReason) w.WriteString("reason", r.Reason);
        if (r.Name.Length > 0) w.WriteString("name", r.Name);
        w.WriteEndObject();
    }

    /// <summary>The key of every mod the policy names as required, for the recommend conflict check.</summary>
    public bool IsRequiredClient(string? id, string? package) => ClientPolicyConfig.IndexOfMod(Policy.Required, id, package) >= 0;

    /// <summary>
    /// Read the file form. False (with the reason) when it is not a JSON object of a schema this reader knows.
    /// A row over a cap, malformed or duplicated is skipped with a line in <paramref name="skipped"/>, so one bad
    /// hand edit does not drop the rest.
    /// </summary>
    public static bool TryParse(string? json, out JoinAdminConfig config, out string? error, List<string>? skipped = null)
    {
        config = new JoinAdminConfig();
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var doc = JsonDocument.Parse(json!, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the file is not a JSON object";
                return false;
            }

            int schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int n) ? n : Schema;
            if (schema != Schema && schema != SchemaV1)
            {
                error = "schema " + schema.ToString(CultureInfo.InvariantCulture) + " is not one this reader knows";
                return false;
            }

            if (root.TryGetProperty("recommended", out var rec) && rec.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rec.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    var mod = new RecommendedMod(Progenitor.Plugins.LegacyIds.Normalize(Str(row, "id")), Str(row, "package"), Str(row, "version"), Str(row, "reason"));
                    string? why = JoinAdminPolicy.CheckRecommended(mod);
                    if (why == null && config.Recommended.Count >= JoinAdminLimits.MaxRecommended) why = "more than " + JoinAdminLimits.MaxRecommended + " recommended mods";
                    if (why == null && config.IndexOfRecommended(KeyOf(mod)) >= 0) why = "listed twice";
                    if (why == null && mod.Id.Length > 0 && mod.Package.Length > 0 && config.IndexOfRecommended(mod.Package) >= 0) why = "listed twice";
                    if (why != null)
                    {
                        skipped?.Add("recommended " + (KeyOf(mod).Length > 0 ? KeyOf(mod) : "(no id)") + ": " + why);
                        continue;
                    }

                    config.Recommended.Add(mod);
                    string name = Str(row, "name");
                    if (mod.Id.Length > 0 && name.Length > 0 && JoinAdminPolicy.CheckName(name) == null) config.Names[mod.Id.ToLowerInvariant()] = name;
                }
            }

            // 2026-10-05: a "shared" list (the mods an older server shared with refused players) is ignored: servers no
            // longer send mod files. The next write leaves it out.

            // CLIENTPOLICY1 (schema 2). A schema 1 file has neither key and keeps the defaults: no required client
            // mods, Any mods, Let them join.
            if (root.TryGetProperty("requiredClient", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in req.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    var mod = ReadClientRow(row, withReason: true, out string? bad);
                    string key = mod.Key.Length > 0 ? mod.Key : "(no id)";
                    string? why = bad ?? JoinAdminPolicy.CheckClientRow(mod, required: true);
                    if (why == null && config.Policy.Required.Count >= ClientModPolicyLimits.MaxRequired) why = "more than " + ClientModPolicyLimits.MaxRequired + " required client mods";
                    if (why == null && ClientPolicyConfig.IndexOfMod(config.Policy.Required, mod.Id, mod.Package) >= 0) why = "listed twice";
                    if (why != null)
                    {
                        skipped?.Add("requiredClient " + key + ": " + why);
                        continue;
                    }

                    config.Policy.Required.Add(mod);
                }
            }

            if (root.TryGetProperty("clientPolicy", out var pol) && pol.ValueKind == JsonValueKind.Object)
            {
                string modeWord = Str(pol, "mode");
                if (modeWord.Length > 0)
                {
                    if (ClientModRules.TryParseMode(modeWord, out var mode)) config.Policy.Mode = mode;
                    else skipped?.Add("clientPolicy mode \"" + modeWord + "\": not any or approved; Any mods kept");
                }

                string unverifiedWord = Str(pol, "unverified");
                if (unverifiedWord.Length > 0)
                {
                    if (ClientModRules.TryParseUnverified(unverifiedWord, out var u)) config.Policy.Unverified = u;
                    else skipped?.Add("clientPolicy unverified \"" + unverifiedWord + "\": not allow or refuse; Let them join kept");
                }

                // CLIENTPOLICY3: absent in every file written before stage 3, which keeps the default (Warn and pause it).
                string midWord = Str(pol, "midSession");
                if (midWord.Length > 0)
                {
                    if (ClientModRules.TryParseMidSession(midWord, out var mid)) config.Policy.MidSession = mid;
                    else skipped?.Add("clientPolicy midSession \"" + midWord + "\": not warn or disconnect; Warn and pause it kept");
                }

                if (pol.TryGetProperty("approved", out var app) && app.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in app.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Object) continue;
                        var mod = ReadClientRow(row, withReason: false, out string? bad, defaultRule: ClientModRule.Any);
                        string key = mod.Key.Length > 0 ? mod.Key : "(no id)";
                        string? why = bad ?? JoinAdminPolicy.CheckClientRow(mod, required: false);
                        if (why == null && config.Policy.Approved.Count >= ClientModPolicyLimits.MaxApproved) why = "more than " + ClientModPolicyLimits.MaxApproved + " approved client mods";
                        if (why == null && ClientPolicyConfig.IndexOfMod(config.Policy.Approved, mod.Id, mod.Package) >= 0) why = "listed twice";
                        if (why != null)
                        {
                            skipped?.Add("approved " + key + ": " + why);
                            continue;
                        }

                        config.Policy.Approved.Add(mod);
                    }
                }
            }

            // Mod compatibility C3: absent in every file written before C3 (range, no per-mod rules).
            string matchWord = Str(root, "versionMatching");
            if (matchWord.Length > 0)
            {
                if (VersionMatchingText.TryParse(matchWord, out var match)) config.VersionMatching = match;
                else skipped?.Add("versionMatching \"" + matchWord + "\": not range, minor or exact; Use each mod's declared range kept");
            }

            if (root.TryGetProperty("serverModRules", out var smr) && smr.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in smr.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    string id = Progenitor.Plugins.LegacyIds.Normalize(Str(row, "id"));
                    string ruleWord = Str(row, "rule");
                    string name = Str(row, "name");
                    string? why = null;
                    var rule = ClientModRule.AtLeast;
                    if (id.Length == 0) why = "no id";
                    else if (!ModManifestIds.TryValidateId(id, out var idError)) why = "the mod id is not valid (" + idError + ")";
                    else if (ruleWord.Length == 0 || !ClientModRules.TryParse(ruleWord, out rule)) why = "unknown rule \"" + ruleWord + "\"";
                    else if (JoinAdminPolicy.CheckName(name) is { } badName) why = badName.TrimEnd('.').ToLowerInvariant();
                    else if (config.ServerModRules.Count >= VersionMatchLimits.MaxServerModRules) why = "more than " + VersionMatchLimits.MaxServerModRules + " server mod rules";
                    else if (config.IndexOfServerModRule(id) >= 0) why = "listed twice";
                    if (why != null)
                    {
                        skipped?.Add("serverModRules " + (id.Length > 0 ? id : "(no id)") + ": " + why);
                        continue;
                    }

                    config.ServerModRules.Add(new ServerModRule(id, rule, name));
                }
            }

            // A mod can't be both required and recommended (adding one moves it): a hand edit that lists both keeps
            // the required row.
            for (int i = config.Recommended.Count - 1; i >= 0; i--)
            {
                var r = config.Recommended[i];
                if (!config.IsRequiredClient(r.Id, r.Package)) continue;
                skipped?.Add("recommended " + KeyOf(r) + ": also a required client mod; kept as required");
                config.Recommended.RemoveAt(i);
                if (r.Id.Length > 0) config.Names.Remove(r.Id.ToLowerInvariant());
            }

            return true;
        }
        catch (JsonException e)
        {
            error = "the file is not valid JSON (" + e.Message + ")";
            return false;
        }
    }

    internal static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";

    /// <summary>One stored client mod row. An unknown rule word is <paramref name="bad"/>, never read as another rule.</summary>
    private static ClientModRow ReadClientRow(JsonElement row, bool withReason, out string? bad, ClientModRule defaultRule = ClientModRule.AtLeast)
    {
        bad = null;
        string ruleWord = Str(row, "rule");
        var rule = defaultRule;
        if (ruleWord.Length > 0 && !ClientModRules.TryParse(ruleWord, out rule)) bad = "unknown rule \"" + ruleWord + "\"";
        string reason = withReason && row.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
        return new ClientModRow(Progenitor.Plugins.LegacyIds.Normalize(Str(row, "id")), Str(row, "package"), Str(row, "version"), rule, reason, Str(row, "name"));
    }
}

/// <summary>The operations of the admin channel. <see cref="JoinAdminRequest.Op"/> carries the wire word.</summary>
internal static class JoinAdminOps
{
    internal const string Get = "get";
    internal const string RecommendAdd = "recommend-add";
    internal const string RecommendRemove = "recommend-remove";
    internal const string RecommendReason = "recommend-reason";
    internal const string Window = "window";

    // CLIENTPOLICY1: the client mod policy's operations.
    internal const string RequiredAdd = "required-add";
    internal const string RequiredRemove = "required-remove";
    internal const string RequiredReason = "required-reason";
    internal const string RequiredRule = "required-rule";
    internal const string PolicyMode = "policy-mode";
    internal const string ApproveAdd = "approve-add";
    internal const string ApproveRemove = "approve-remove";
    internal const string ApproveBulk = "approve-bulk";
    internal const string Unverified = "unverified";

    // CLIENTPOLICY3: what a mid-session re-check does (value warn or disconnect).
    internal const string MidSession = "mid-session";

    // Mod compatibility C3: Version matching (value range, minor or exact) and a per-mod rule for a mod the server runs
    // (id, rule exact | at-least | minor | any, or default/empty to clear; optional name).
    internal const string VersionMatching = "version-matching";
    internal const string ServerModRule = "server-mod-rule";

    /// <summary>C3: the operations that change version matching (re-sign the catalog; nobody is re-judged or disconnected).</summary>
    internal static bool IsMatching(string? op) => op is VersionMatching or ServerModRule;

    internal static readonly IReadOnlyList<string> All = new[]
    {
        Get, RecommendAdd, RecommendRemove, RecommendReason, Window,
        RequiredAdd, RequiredRemove, RequiredReason, RequiredRule, PolicyMode, ApproveAdd, ApproveRemove, ApproveBulk, Unverified,
        MidSession,
        VersionMatching, ServerModRule,
        // Settings organization stage 5: the server values edited from Progenitor's page (JoinAdminOptions.cs).
        ServerOptionOps.AnswerTimeout, ServerOptionOps.RequireProgenitor, ServerOptionOps.PhotoMode, ServerOptionOps.AllySafeHits, ServerOptionOps.ContainerPreview, ServerOptionOps.RemoteInstall,
        ServerOptionOps.AllowedSteamIds, ServerOptionOps.NoticeSeconds, ServerOptionOps.NoticeRepeatSeconds, ServerOptionOps.NoticeCreationLimitSeconds,
        // REQRULE5: Disconnect them's delay ([ClientPolicy] DisconnectGraceSeconds).
        ServerOptionOps.DisconnectGrace,
        // CHAT4: the Server tab's Chat group (server-only values).
        ServerOptionOps.ChatFilter, ServerOptionOps.ChatFilterWords, ServerOptionOps.ChatFilterAction, ServerOptionOps.ChatRelay,
        ServerOptionOps.ChatRelayUrl, ServerOptionOps.ChatRelayGlobal, ServerOptionOps.ChatRelayLocal, ServerOptionOps.ChatRelayClan,
        ServerOptionOps.ChatRelayWhispers, ServerOptionOps.ChatRelaySystem,
        // AUTOUPDATE3: the Server tab's Updates group (server-only values) and its three actions.
        ServerOptionOps.UpdateMods, ServerOptionOps.UpdateMajors, ServerOptionOps.UpdateRestartWhenEmpty, ServerOptionOps.UpdateEmptyMinutes,
        ServerOptionOps.UpdateBackup, ServerOptionOps.UpdateBackupKeep, ServerOptionOps.UpdateBackupSkipHours,
        UpdateCheck, UpdateRestart, UpdateCancelRestart,
    };

    // AUTOUPDATE3: the Updates group's actions (edits: rate-limited, authorized, lockable and audited like any edit).

    /// <summary><b>Check now</b>: one conditional index request now.</summary>
    internal const string UpdateCheck = "auto-update-check";

    /// <summary><b>Restart now</b>: warn the players, then save and quit to apply the staged updates.</summary>
    internal const string UpdateRestart = "auto-update-restart";

    /// <summary>Cancel Restart now during its warning.</summary>
    internal const string UpdateCancelRestart = "auto-update-cancel-restart";

    internal static bool IsUpdateAction(string? op) => op is UpdateCheck or UpdateRestart or UpdateCancelRestart;

    /// <summary>The operations that change the client mod policy (re-sign the catalog, re-check the answer deadlines).</summary>
    internal static bool IsPolicy(string? op) => op is RequiredAdd or RequiredRemove or RequiredReason or RequiredRule
        or PolicyMode or ApproveAdd or ApproveRemove or ApproveBulk or Unverified or MidSession;

    internal static bool IsEdit(string? op) => op != null && op != Get;
}

/// <summary>Client → server: one admin request. Every field is plain text; nothing here names the sender.</summary>
internal sealed class JoinAdminRequest
{
    public int Nonce { get; set; }
    public string Op { get; set; } = JoinAdminOps.Get;
    public string Id { get; set; } = "";
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string Reason { get; set; } = "";

    /// <summary>A recommendation's readable name (the picker's), or empty.</summary>
    public string Name { get; set; } = "";

    public int Seconds { get; set; }

    /// <summary>
    /// The request carried <see cref="Seconds"/> (a window request without it is refused, never read as 0). True for
    /// a request built in code; the decoder sets it from the packet.
    /// </summary>
    public bool HasSeconds { get; set; } = true;

    /// <summary>CLIENTPOLICY1: a client mod row's rule word (<c>at-least</c>, <c>exact</c>, <c>minor</c>, <c>any</c>), or empty for the default.</summary>
    public string Rule { get; set; } = "";

    /// <summary>CLIENTPOLICY1: <c>policy-mode</c>'s <c>any</c> / <c>approved</c>, <c>unverified</c>'s <c>allow</c> / <c>refuse</c>; CLIENTPOLICY3: <c>mid-session</c>'s <c>warn</c> / <c>disconnect</c>; C3: <c>version-matching</c>'s <c>range</c> / <c>minor</c> / <c>exact</c>.</summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// Settings organization stage 5: the request carried <see cref="Value"/> (an <c>allowed-steam-ids</c> request
    /// without it is refused, never read as an empty list). True for a request built in code; the decoder sets it.
    /// </summary>
    public bool HasValue { get; set; } = true;

    /// <summary>CLIENTPOLICY1: <c>approve-bulk</c>'s rows (at most <see cref="ClientModPolicyLimits.MaxBulk"/>).</summary>
    public List<JoinAdminBulkItem> Items { get; } = new();

    /// <summary>CLIENTPOLICY1: the first approved row the answer's state carries (the panel's page through a long list).</summary>
    public int ApprovedFrom { get; set; }
}

/// <summary>CLIENTPOLICY1: one row of an <c>approve-bulk</c> request.</summary>
internal sealed class JoinAdminBulkItem
{
    public string Id { get; set; } = "";
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>CLIENTPOLICY1: a required or approved client mod row as the panel shows it.</summary>
internal sealed class JoinAdminClientRow
{
    public string Id { get; set; } = "";
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string Rule { get; set; } = ClientModRules.WordAtLeast;
    public string Reason { get; set; } = "";
    public string Name { get; set; } = "";

    public string Key => Id.Length > 0 ? Id : Package;

    public ClientModRule RuleValue => ClientModRules.TryParse(Rule, out var r) ? r : ClientModRule.AtLeast;

    internal static JoinAdminClientRow From(ClientModRow r) => new()
    {
        Id = r.Id, Package = r.Package, Version = r.Version, Rule = ClientModRules.Word(r.Rule), Reason = r.Reason, Name = r.Name,
    };
}

/// <summary>A recommended row as the panel shows it; <see cref="FromFile"/> rows come from the overlay and are read-only here.</summary>
internal sealed class JoinAdminRecRow
{
    public string Id { get; set; } = "";
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool FromFile { get; set; }

    /// <summary>The readable name stored with it, or empty.</summary>
    public string Name { get; set; } = "";

    public string Key => Id.Length > 0 ? Id : Package;
}

/// <summary>One aggregated refusal: a mod, the version the server required and the version players had.</summary>
internal sealed class JoinAdminAlertRow
{
    public string Mod { get; set; } = "";
    public string Name { get; set; } = "";
    public string Required { get; set; } = "";
    public string Had { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Count { get; set; }
    public string Text { get; set; } = "";

    /// <summary>
    /// CLIENTPOLICY3: a <c>not-approved</c> line whose mod the approved list (or the always-allowed set) now allows. The
    /// line then reads <c>&lt;mod&gt;: approved now (N joins refused before)</c> and offers no <b>Approve</b>.
    /// </summary>
    public bool Approved { get; set; }

    /// <summary>CLIENTPOLICY3: the line offers <b>Approve</b> (a not-approved line, a plain id, not approved yet).</summary>
    public bool CanApprove => Kind == RefusedJoinLog.KindNotApproved && !Approved && ClientModJudge.IsPlainId(Mod);
}

/// <summary>Server → client: everything the panel draws.</summary>
internal sealed class JoinAdminState
{
    public List<JoinAdminRecRow> Recommended { get; } = new();

    /// <summary>The ids the server requires now (the picker leaves them out of the recommend list).</summary>
    public List<string> Required { get; } = new();

    public int WindowSeconds { get; set; }
    public int RefusedJoins { get; set; }
    public int RefusedDays { get; set; } = RefusedJoinLog.WindowDays;
    public List<JoinAdminAlertRow> Alert { get; } = new();

    // CLIENTPOLICY1 -----------------------------------------------------------------------------------------

    /// <summary>The required client mods (all of them: at most 16).</summary>
    public List<JoinAdminClientRow> RequiredClient { get; } = new();

    /// <summary><c>any</c> or <c>approved</c>.</summary>
    public string PolicyMode { get; set; } = "any";

    /// <summary><c>allow</c> or <c>refuse</c>.</summary>
    public string Unverified { get; set; } = "allow";

    /// <summary>CLIENTPOLICY3: <c>warn</c> or <c>disconnect</c> (empty from a server older than stage 3).</summary>
    public string MidSession { get; set; } = "";

    /// <summary>A window of the approved rows, from <see cref="ApprovedFrom"/> (an answer may not carry all 128).</summary>
    public List<JoinAdminClientRow> Approved { get; } = new();

    public int ApprovedFrom { get; set; }

    /// <summary>How many approved rows the server stores.</summary>
    public int ApprovedTotal { get; set; }

    /// <summary>The state came from a server that knows the client mod policy (an older one sends none of these keys).</summary>
    public bool PolicySupported { get; set; }

    // Settings organization stage 5 ----------------------------------------------------------------------------

    /// <summary>The server options by operation word (<see cref="ServerOptionOps"/>), as invariant text; absent when not bound.</summary>
    public Dictionary<string, string> Options { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The operator's lock list (<c>[ServerSettings] LockedInGame</c>, <see cref="ServerSettingsLock"/>) as the server holds it: setting
    /// ids, group ids and <c>*</c>. The page draws every setting it matches read-only; the server refuses an edit of one anyway.
    /// </summary>
    public List<string> Lock { get; } = new();

    /// <summary>The state came from a server that knows the server options (an older one sends no <c>options</c>).</summary>
    public bool OptionsSupported { get; set; }

    /// <summary>AUTOUPDATE3: the server's update status (the Updates group's status row), or null from an older server.</summary>
    public JoinAdminUpdates? Updates { get; set; }

    // Mod compatibility C3 -----------------------------------------------------------------------------------------

    /// <summary>The state came from a server that knows Version matching (an older one sends no <c>versionMatching</c>).</summary>
    public bool MatchingSupported { get; set; }

    /// <summary>The effective mode's word (<c>range</c>, <c>minor</c>, <c>exact</c>): the cfg override when set, else the admins' choice.</summary>
    public string VersionMatching { get; set; } = "";

    /// <summary>The server's cfg forces the mode (<c>[ClientPolicy] VersionMatching</c>): the control is read-only.</summary>
    public bool VersionMatchingForced { get; set; }

    /// <summary>The mods the server runs, each with the admins' rule for it, then stored rules for mods it no longer runs.</summary>
    public List<JoinAdminServerModRow> ServerMods { get; } = new();

    /// <summary>
    /// Mod compatibility C4: what Exact version for all means for Progenitor on this server (<c>Players also need exactly
    /// Progenitor 2026.10.159, this server's build.</c>, or <c>Exact is not enforced for Progenitor: …</c>); empty from an
    /// older server. Shown under the radio list while Exact is chosen.
    /// </summary>
    public string ProgenitorExactLine { get; set; } = "";
}

/// <summary>AUTOUPDATE3: the server's update status as an admin's Server tab shows it.</summary>
internal sealed class JoinAdminUpdates
{
    internal const int MaxLines = 8;

    /// <summary><c>Checked 05:00 · 1 applied live · 2 waiting for a restart</c>.</summary>
    public string Line { get; set; } = "";

    /// <summary>The updates staged for the next start (<c>Package version</c>).</summary>
    public List<string> RestartPending { get; } = new();

    /// <summary>Updates found that are not applied automatically, and held ones.</summary>
    public List<string> Waiting { get; } = new();

    /// <summary>The last results, newest last.</summary>
    public List<string> Recent { get; } = new();

    /// <summary>Restart now's countdown, seconds left, or -1.</summary>
    public int Countdown { get; set; } = -1;

    /// <summary>Something is known to start the server again (<c>[AutoUpdate] HostRestarts</c>).</summary>
    public bool ComesBack { get; set; }

    /// <summary><c>HostRestarts</c>' words (what restarts the server), or empty.</summary>
    public string HostRestarts { get; set; } = "";
}

/// <summary>Server → client: the answer to one request.</summary>
internal sealed class JoinAdminResponse
{
    public int Nonce { get; set; }
    public string Op { get; set; } = "";
    public bool Ok { get; set; }
    public string Error { get; set; } = "";

    /// <summary>A short note about what was done (<c>Clamped to 300 seconds.</c>), or empty.</summary>
    public string Note { get; set; } = "";

    /// <summary>The state after the request; null on a refusal before authorization (no data for a non-admin).</summary>
    public JoinAdminState? State { get; set; }
}

/// <summary>
/// The admin channel's packet (<c>'PrJa'</c>, one id both ways): a version byte, a kind byte (request, answer,
/// hello), the nonce, then UTF-8 JSON. Strict UTF-8, bounded, and a kind a side does not expect is dropped.
/// </summary>
internal static class JoinAdminWire
{
    internal const byte Version = 1;
    internal const byte KindRequest = 0;
    internal const byte KindResponse = 1;
    internal const byte KindHello = 2;

    /// <summary>AUTOUPDATE3: server → admin, an update notice (never sent to a client that did not declare the channel).</summary>
    internal const byte KindNotice = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] EncodeRequest(JoinAdminRequest r)
    {
        return Frame(KindRequest, r.Nonce, w =>
        {
            w.WriteString("op", r.Op ?? "");
            if ((r.Id ?? "").Length > 0) w.WriteString("id", r.Id);
            if ((r.Package ?? "").Length > 0) w.WriteString("package", r.Package);
            if ((r.Version ?? "").Length > 0) w.WriteString("version", r.Version);
            if (r.Op == JoinAdminOps.RecommendAdd || r.Op == JoinAdminOps.RecommendReason
                || r.Op == JoinAdminOps.RequiredAdd || r.Op == JoinAdminOps.RequiredReason) w.WriteString("reason", r.Reason ?? "");
            if ((r.Op == JoinAdminOps.RecommendAdd || r.Op == JoinAdminOps.RequiredAdd || r.Op == JoinAdminOps.ApproveAdd || r.Op == JoinAdminOps.ServerModRule)
                && (r.Name ?? "").Length > 0)
                w.WriteString("name", r.Name);
            if ((r.Op == JoinAdminOps.Window || ServerOptionOps.IsSeconds(r.Op)) && r.HasSeconds) w.WriteNumber("seconds", r.Seconds);
            if ((r.Rule ?? "").Length > 0) w.WriteString("rule", r.Rule);
            // An install list may be empty (any admin), so its value is always written; CHAT4: so are the text options'.
            if ((r.Value ?? "").Length > 0 || (ServerOptionOps.WritesEmptyValue(r.Op) && r.HasValue)) w.WriteString("value", r.Value ?? "");
            if (r.ApprovedFrom > 0) w.WriteNumber("approvedFrom", r.ApprovedFrom);
            if (r.Op == JoinAdminOps.ApproveBulk)
            {
                w.WriteStartArray("items");
                foreach (var x in r.Items)
                {
                    w.WriteStartObject();
                    if (x.Id.Length > 0) w.WriteString("id", x.Id);
                    if (x.Package.Length > 0) w.WriteString("package", x.Package);
                    if (x.Version.Length > 0) w.WriteString("version", x.Version);
                    if (x.Name.Length > 0) w.WriteString("name", x.Name);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }
        });
    }

    /// <summary>
    /// CLIENTPOLICY1: split an "approve everything" list into <c>approve-bulk</c> requests that each carry at most
    /// <see cref="ClientModPolicyLimits.MaxBulk"/> rows and fit one packet.
    /// </summary>
    internal static List<JoinAdminRequest> BulkRequests(IReadOnlyList<JoinAdminBulkItem> items, int approvedFrom = 0)
    {
        var list = new List<JoinAdminRequest>();
        var current = new JoinAdminRequest { Op = JoinAdminOps.ApproveBulk, ApprovedFrom = approvedFrom };
        foreach (var item in items ?? Array.Empty<JoinAdminBulkItem>())
        {
            current.Items.Add(item);
            bool over = current.Items.Count > ClientModPolicyLimits.MaxBulk || EncodeRequest(current).Length > JoinAdminLimits.MaxPacketBytes;
            if (!over) continue;
            current.Items.RemoveAt(current.Items.Count - 1);
            if (current.Items.Count > 0) list.Add(current);
            current = new JoinAdminRequest { Op = JoinAdminOps.ApproveBulk, ApprovedFrom = approvedFrom };
            current.Items.Add(item);
        }

        if (current.Items.Count > 0) list.Add(current);
        return list;
    }

    /// <summary>The hello a server sends a capable client after an ok handshake: whether it is an admin here.</summary>
    internal static byte[] EncodeHello(bool admin) => EncodeHello(admin, rules: false);

    /// <summary>
    /// The hello; settings organization stage 6: <paramref name="rules"/> says this server answers the Server rules channel
    /// (<c>'PrSv'</c>), so a client never sends it to a server without the handler.
    /// </summary>
    internal static byte[] EncodeHello(bool admin, bool rules) => Frame(KindHello, 0, w =>
    {
        w.WriteBoolean("admin", admin);
        if (rules) w.WriteBoolean("rules", true);
    });

    /// <summary>
    /// An answer. When the state does not fit <see cref="JoinAdminLimits.MaxPacketBytes"/>, what the panel can do
    /// without goes first, from the end: the alert rows, the required-id list (the server
    /// still refuses recommending a required mod), then the readable names (the ids stay). The
    /// stored rows themselves always fit: at every cap with three-byte characters they are about 14 KiB.
    /// </summary>
    internal static byte[] EncodeResponse(JoinAdminResponse r)
    {
        while (true)
        {
            byte[] bytes = Frame(KindResponse, r.Nonce, w => WriteResponse(w, r));
            if (bytes.Length <= JoinAdminLimits.MaxPacketBytes) return bytes;
            var s = r.State;
            // AUTOUPDATE3: the update status's history lines go first (the line itself always fits).
            if (s?.Updates is { } u && (u.Recent.Count > 0 || u.Waiting.Count > 0))
            {
                if (u.Recent.Count > 0) u.Recent.RemoveAt(0);
                else u.Waiting.RemoveAt(u.Waiting.Count - 1);
            }
            else if (s != null && s.Alert.Count > 0) s.Alert.RemoveAt(s.Alert.Count - 1);
            else if (s != null && s.Required.Count > 0) s.Required.Clear();
            // CLIENTPOLICY1: the approved rows past what fits stay on the server; the panel pages to them (approvedFrom).
            else if (s != null && s.Approved.Count > 1) s.Approved.RemoveRange(s.Approved.Count / 2, s.Approved.Count - s.Approved.Count / 2);
            else if (s != null && s.Approved.Count > 0) s.Approved.Clear();
            else if (s != null && (s.RequiredClient.Exists(x => x.Name.Length > 0) || s.Recommended.Exists(x => x.Name.Length > 0)
                                   || s.ServerMods.Exists(x => x.Name.Length > 0)))
            {
                foreach (var x in s.RequiredClient) x.Name = "";
                foreach (var x in s.Recommended) x.Name = "";
                foreach (var x in s.ServerMods) x.Name = "";
            }
            // C3: the server's mods without a rule go last (their rows can be asked for again after a rule frees room).
            else if (s != null && s.ServerMods.FindLastIndex(x => x.Rule.Length == 0) is int plain and >= 0) s.ServerMods.RemoveAt(plain);
            else if (s != null) r.State = null;
            else throw new ArgumentException("The admin answer does not fit one packet.");
        }
    }

    // Non-ASCII text is written as UTF-8, not as \uXXXX escapes (six bytes a character); the packet is read
    // only by this parser, never placed in a page.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static byte[] Frame(byte kind, int nonce, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(Version);
        stream.WriteByte(kind);
        var n = BitConverter.GetBytes(nonce);
        if (!BitConverter.IsLittleEndian) Array.Reverse(n);
        stream.Write(n, 0, 4);
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteResponse(Utf8JsonWriter w, JoinAdminResponse r)
    {
        w.WriteString("op", r.Op ?? "");
        w.WriteBoolean("ok", r.Ok);
        if ((r.Error ?? "").Length > 0) w.WriteString("error", r.Error);
        if ((r.Note ?? "").Length > 0) w.WriteString("note", r.Note);
        var s = r.State;
        if (s == null) return;
        w.WriteStartObject("state");
        w.WriteNumber("windowSeconds", s.WindowSeconds);
        w.WriteNumber("refusedJoins", s.RefusedJoins);
        w.WriteNumber("refusedDays", s.RefusedDays);
        w.WriteStartArray("recommended");
        foreach (var x in s.Recommended)
        {
            w.WriteStartObject();
            w.WriteString("id", x.Id);
            w.WriteString("package", x.Package);
            w.WriteString("version", x.Version);
            w.WriteString("reason", x.Reason);
            if (x.Name.Length > 0) w.WriteString("name", x.Name);
            if (x.FromFile) w.WriteBoolean("fromFile", true);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("required");
        foreach (string id in s.Required) w.WriteStringValue(id);
        w.WriteEndArray();
        w.WriteStartArray("alert");
        foreach (var a in s.Alert)
        {
            w.WriteStartObject();
            w.WriteString("mod", a.Mod);
            w.WriteString("name", a.Name);
            w.WriteString("required", a.Required);
            w.WriteString("had", a.Had);
            w.WriteString("kind", a.Kind);
            w.WriteNumber("count", a.Count);
            w.WriteString("text", a.Text);
            if (a.Approved) w.WriteBoolean("approved", true);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        // CLIENTPOLICY1: written only by a server that knows the policy.
        if (s.PolicySupported)
        {
            w.WriteStartObject("clientPolicy");
            w.WriteString("mode", s.PolicyMode);
            w.WriteString("unverified", s.Unverified);
            if (s.MidSession.Length > 0) w.WriteString("midSession", s.MidSession);
            w.WriteNumber("approvedFrom", s.ApprovedFrom);
            w.WriteNumber("approvedTotal", s.ApprovedTotal);
            WriteClientRows(w, "required", s.RequiredClient, withReason: true);
            WriteClientRows(w, "approved", s.Approved, withReason: false);
            w.WriteEndObject();
        }

        // Settings organization stage 5: written only by a server that knows the options.
        if (s.OptionsSupported)
        {
            w.WriteStartObject("options");
            foreach (var pair in s.Options) w.WriteString(pair.Key, pair.Value);
            w.WriteEndObject();
            w.WriteStartArray("lock");
            foreach (string entry in s.Lock) w.WriteStringValue(entry);
            w.WriteEndArray();
        }

        // Mod compatibility C3: written only by a server that knows Version matching.
        if (s.MatchingSupported)
        {
            w.WriteStartObject("versionMatching");
            w.WriteString("mode", s.VersionMatching);
            if (s.VersionMatchingForced) w.WriteBoolean("forced", true);
            if (s.ProgenitorExactLine.Length > 0) w.WriteString("progenitor", s.ProgenitorExactLine);
            w.WriteStartArray("mods");
            foreach (var m in s.ServerMods)
            {
                w.WriteStartObject();
                w.WriteString("id", m.Id);
                if (m.Name.Length > 0) w.WriteString("name", m.Name);
                if (m.Version.Length > 0) w.WriteString("version", m.Version);
                if (m.Rule.Length > 0) w.WriteString("rule", m.Rule);
                if (!m.Runs) w.WriteBoolean("runs", false);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        // AUTOUPDATE3: written only by a server that runs the auto-updater.
        if (s.Updates is { } u)
        {
            w.WriteStartObject("updates");
            w.WriteString("line", u.Line);
            w.WriteNumber("countdown", u.Countdown);
            if (u.ComesBack) w.WriteBoolean("comesBack", true);
            if (u.HostRestarts.Length > 0) w.WriteString("hostRestarts", u.HostRestarts);
            WriteLines(w, "restart", u.RestartPending);
            WriteLines(w, "waiting", u.Waiting);
            WriteLines(w, "recent", u.Recent);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static void WriteLines(Utf8JsonWriter w, string name, List<string> lines)
    {
        w.WriteStartArray(name);
        int n = 0;
        foreach (string l in lines)
        {
            if (n++ >= JoinAdminUpdates.MaxLines) break;
            w.WriteStringValue(l.Length > 200 ? l.Substring(0, 199) + "…" : l);
        }

        w.WriteEndArray();
    }

    private static void ReadLines(JsonElement el, string name, List<string> into)
    {
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var x in arr.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String && into.Count < JoinAdminUpdates.MaxLines) into.Add(x.GetString() ?? "");
    }

    /// <summary>
    /// AUTOUPDATE3: the server's notice to an admin (a kind a client older than phase 3 drops: <see cref="KindOf"/> was capped at
    /// <see cref="KindHello"/>): <c>N server updates available</c>, applied, failed, waiting for a restart.
    /// </summary>
    internal static byte[] EncodeNotice(string kind, string text, bool offerRestart) => Frame(KindNotice, 0, w =>
    {
        w.WriteString("kind", kind ?? "");
        w.WriteString("text", (text ?? "").Length > 300 ? text!.Substring(0, 299) + "…" : text ?? "");
        if (offerRestart) w.WriteBoolean("offerRestart", true);
    });

    internal static bool TryDecodeNotice(ReadOnlySpan<byte> packet, out string kind, out string text, out bool offerRestart)
    {
        kind = text = "";
        offerRestart = false;
        if (KindOf(packet) != KindNotice || !TryBody(packet, out _, out var doc) || doc == null) return false;
        using (doc)
        {
            kind = JoinAdminConfig.Str(doc.RootElement, "kind");
            text = JoinAdminConfig.Str(doc.RootElement, "text");
            offerRestart = doc.RootElement.TryGetProperty("offerRestart", out var r) && r.ValueKind == JsonValueKind.True;
            return text.Length > 0;
        }
    }

    private static void WriteClientRows(Utf8JsonWriter w, string name, List<JoinAdminClientRow> rows, bool withReason)
    {
        w.WriteStartArray(name);
        foreach (var x in rows)
        {
            w.WriteStartObject();
            if (x.Id.Length > 0) w.WriteString("id", x.Id);
            if (x.Package.Length > 0) w.WriteString("package", x.Package);
            if (x.Version.Length > 0) w.WriteString("version", x.Version);
            w.WriteString("rule", x.Rule);
            if (withReason) w.WriteString("reason", x.Reason);
            if (x.Name.Length > 0) w.WriteString("name", x.Name);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static JoinAdminClientRow ClientRow(JsonElement x) => new()
    {
        Id = JoinAdminConfig.Str(x, "id"), Package = JoinAdminConfig.Str(x, "package"), Version = JoinAdminConfig.Str(x, "version"),
        Rule = JoinAdminConfig.Str(x, "rule"), Reason = JoinAdminConfig.Str(x, "reason"), Name = JoinAdminConfig.Str(x, "name"),
    };

    /// <summary>The kind byte of a packet, or -1 when it is not one of ours.</summary>
    internal static int KindOf(ReadOnlySpan<byte> packet) =>
        packet.Length >= 6 && packet.Length <= JoinAdminLimits.MaxPacketBytes && packet[0] == Version && packet[1] <= KindNotice ? packet[1] : -1;

    internal static bool TryDecodeRequest(ReadOnlySpan<byte> packet, out JoinAdminRequest request)
    {
        request = new JoinAdminRequest();
        if (KindOf(packet) != KindRequest || !TryBody(packet, out int nonce, out var doc) || doc == null) return false;
        using (doc)
        {
            var root = doc.RootElement;
            request.Nonce = nonce;
            request.Op = JoinAdminConfig.Str(root, "op");
            request.Id = JoinAdminConfig.Str(root, "id");
            request.Package = JoinAdminConfig.Str(root, "package");
            request.Version = JoinAdminConfig.Str(root, "version");
            // The reason is kept as sent (trimmed by the policy), so a control character reaches the check.
            request.Reason = root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() ?? "" : "";
            request.Name = root.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? "" : "";
            request.HasSeconds = root.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number && sec.TryGetInt32(out _);
            request.Seconds = request.HasSeconds ? sec.GetInt32() : 0;
            request.Rule = JoinAdminConfig.Str(root, "rule");
            request.Value = JoinAdminConfig.Str(root, "value");
            request.HasValue = root.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String;
            request.ApprovedFrom = Int(root, "approvedFrom");
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                // One more than the cap is read, so the policy refuses an over-long request instead of cutting it.
                foreach (var x in items.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object) continue;
                    if (request.Items.Count > ClientModPolicyLimits.MaxBulk) break;
                    request.Items.Add(new JoinAdminBulkItem
                    {
                        Id = JoinAdminConfig.Str(x, "id"), Package = JoinAdminConfig.Str(x, "package"),
                        Version = JoinAdminConfig.Str(x, "version"), Name = x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
                    });
                }
            }

            return request.Op.Length > 0;
        }
    }

    internal static bool TryDecodeHello(ReadOnlySpan<byte> packet, out bool admin) => TryDecodeHello(packet, out admin, out _);

    internal static bool TryDecodeHello(ReadOnlySpan<byte> packet, out bool admin, out bool rules)
    {
        admin = false;
        rules = false;
        if (KindOf(packet) != KindHello || !TryBody(packet, out _, out var doc) || doc == null) return false;
        using (doc)
        {
            admin = doc.RootElement.TryGetProperty("admin", out var a) && a.ValueKind == JsonValueKind.True;
            rules = doc.RootElement.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.True;
            return true;
        }
    }

    internal static bool TryDecodeResponse(ReadOnlySpan<byte> packet, out JoinAdminResponse response)
    {
        response = new JoinAdminResponse();
        if (KindOf(packet) != KindResponse || !TryBody(packet, out int nonce, out var doc) || doc == null) return false;
        using (doc)
        {
            var root = doc.RootElement;
            response.Nonce = nonce;
            response.Op = JoinAdminConfig.Str(root, "op");
            response.Ok = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
            response.Error = JoinAdminConfig.Str(root, "error");
            response.Note = JoinAdminConfig.Str(root, "note");
            if (root.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                var s = new JoinAdminState
                {
                    WindowSeconds = Int(st, "windowSeconds"),
                    RefusedJoins = Int(st, "refusedJoins"),
                    RefusedDays = Int(st, "refusedDays"),
                };
                foreach (var x in Rows(st, "recommended", JoinAdminLimits.MaxRecommended * 2))
                    s.Recommended.Add(new JoinAdminRecRow
                    {
                        Id = JoinAdminConfig.Str(x, "id"), Package = JoinAdminConfig.Str(x, "package"),
                        Version = JoinAdminConfig.Str(x, "version"), Reason = JoinAdminConfig.Str(x, "reason"), Name = JoinAdminConfig.Str(x, "name"),
                        FromFile = x.TryGetProperty("fromFile", out var f) && f.ValueKind == JsonValueKind.True,
                    });
                if (st.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                    foreach (var x in req.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.String && s.Required.Count < 128) s.Required.Add(x.GetString() ?? "");
                foreach (var x in Rows(st, "alert", JoinAdminLimits.MaxAlertRows * 2))
                    s.Alert.Add(new JoinAdminAlertRow
                    {
                        Mod = JoinAdminConfig.Str(x, "mod"), Name = JoinAdminConfig.Str(x, "name"),
                        Required = JoinAdminConfig.Str(x, "required"), Had = JoinAdminConfig.Str(x, "had"),
                        Kind = JoinAdminConfig.Str(x, "kind"), Count = Int(x, "count"), Text = JoinAdminConfig.Str(x, "text"),
                        Approved = x.TryGetProperty("approved", out var ap) && ap.ValueKind == JsonValueKind.True,
                    });
                if (st.TryGetProperty("clientPolicy", out var cp) && cp.ValueKind == JsonValueKind.Object)
                {
                    s.PolicySupported = true;
                    s.PolicyMode = JoinAdminConfig.Str(cp, "mode");
                    s.Unverified = JoinAdminConfig.Str(cp, "unverified");
                    s.MidSession = JoinAdminConfig.Str(cp, "midSession");
                    s.ApprovedFrom = Int(cp, "approvedFrom");
                    s.ApprovedTotal = Int(cp, "approvedTotal");
                    foreach (var x in Rows(cp, "required", ClientModPolicyLimits.MaxRequired * 2)) s.RequiredClient.Add(ClientRow(x));
                    foreach (var x in Rows(cp, "approved", ClientModPolicyLimits.MaxApproved * 2)) s.Approved.Add(ClientRow(x));
                }

                if (st.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
                {
                    s.OptionsSupported = true;
                    foreach (var p in opts.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && ServerOptionOps.IsOption(p.Name) && s.Options.Count < 64)
                            s.Options[p.Name] = p.Value.GetString() ?? "";
                    if (st.TryGetProperty("lock", out var locked) && locked.ValueKind == JsonValueKind.Array)
                        foreach (var x in locked.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.String && s.Lock.Count < 128 && (x.GetString() ?? "").Length is > 0 and <= 160)
                                s.Lock.Add(x.GetString()!);
                }

                if (st.TryGetProperty("versionMatching", out var vm) && vm.ValueKind == JsonValueKind.Object)
                {
                    s.MatchingSupported = true;
                    s.VersionMatching = JoinAdminConfig.Str(vm, "mode");
                    s.VersionMatchingForced = vm.TryGetProperty("forced", out var fo) && fo.ValueKind == JsonValueKind.True;
                    string progenitorLine = JoinAdminConfig.Str(vm, "progenitor");
                    s.ProgenitorExactLine = progenitorLine.Length > 240 ? progenitorLine.Substring(0, 240) : progenitorLine;
                    foreach (var x in Rows(vm, "mods", VersionMatchLimits.MaxServerModRows + VersionMatchLimits.MaxServerModRules))
                        s.ServerMods.Add(new JoinAdminServerModRow
                        {
                            Id = JoinAdminConfig.Str(x, "id"), Name = JoinAdminConfig.Str(x, "name"), Version = JoinAdminConfig.Str(x, "version"),
                            Rule = VersionMatchText.TryParseRule(JoinAdminConfig.Str(x, "rule"), out string rw) ? rw : "",
                            Runs = !(x.TryGetProperty("runs", out var ru) && ru.ValueKind == JsonValueKind.False),
                        });
                }

                if (st.TryGetProperty("updates", out var up) && up.ValueKind == JsonValueKind.Object)
                {
                    var u = new JoinAdminUpdates
                    {
                        Line = JoinAdminConfig.Str(up, "line"),
                        Countdown = up.TryGetProperty("countdown", out var cd) && cd.ValueKind == JsonValueKind.Number && cd.TryGetInt32(out int c) ? c : -1,
                        ComesBack = up.TryGetProperty("comesBack", out var cb) && cb.ValueKind == JsonValueKind.True,
                        HostRestarts = JoinAdminConfig.Str(up, "hostRestarts"),
                    };
                    ReadLines(up, "restart", u.RestartPending);
                    ReadLines(up, "waiting", u.Waiting);
                    ReadLines(up, "recent", u.Recent);
                    s.Updates = u;
                }

                response.State = s;
            }

            return true;
        }
    }

    private static IEnumerable<JsonElement> Rows(JsonElement el, string name, int max)
    {
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        int n = 0;
        foreach (var x in arr.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            if (n++ >= max) yield break;
            yield return x;
        }
    }

    private static int Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n) ? n : 0;

    private static bool TryBody(ReadOnlySpan<byte> packet, out int nonce, out JsonDocument? doc)
    {
        doc = null;
        var n = packet.Slice(2, 4).ToArray();
        if (!BitConverter.IsLittleEndian) Array.Reverse(n);
        nonce = BitConverter.ToInt32(n, 0);
        try
        {
            string json = StrictUtf8.GetString(packet.Slice(6).ToArray());
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return true;
            doc.Dispose();
            doc = null;
            return false;
        }
        catch (Exception e) when (e is JsonException || e is DecoderFallbackException || e is ArgumentException)
        {
            doc?.Dispose();
            doc = null;
            return false;
        }
    }
}

/// <summary>What one request did: the new config (or the same), the window value to write, and the audit line's parts.</summary>
internal sealed class JoinAdminDecision
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string Note { get; set; } = "";
    public JoinAdminConfig Config { get; set; } = new();
    public int? WindowSeconds { get; set; }

    /// <summary>Settings organization stage 5: the server option to store (<see cref="OptionValue"/>), or null.</summary>
    public string? OptionOp { get; set; }

    public string? OptionValue { get; set; }

    public bool RecommendedChanged { get; set; }

    /// <summary>CLIENTPOLICY1: the client mod policy changed (stored, signed, and the answer deadlines re-checked).</summary>
    public bool PolicyChanged { get; set; }

    /// <summary>
    /// Mod compatibility C3: Version matching or a per-mod rule changed (stored and signed; it applies from each player's next
    /// join, so nobody is re-judged or disconnected).
    /// </summary>
    public bool MatchingChanged { get; set; }

    /// <summary>Any stored change.</summary>
    public bool Changed => RecommendedChanged || PolicyChanged || MatchingChanged;

    /// <summary>The audit line's row: <c>progenitor/JoinAdmin/&lt;op&gt;[/&lt;id&gt;]</c>.</summary>
    public string AuditRow { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";

    internal static JoinAdminDecision Refused(JoinAdminConfig config, string row, string error, string before = "", string after = "") =>
        new() { Ok = false, Error = error, Config = config, AuditRow = row, Before = before, After = after };
}

/// <summary>
/// JOINADMIN1: every decision about an admin request, game-free. The caller (<see cref="JoinAdminServer"/>) has
/// already applied the rate limit, the handshake and the authorization; this checks the request itself against
/// the stored choices and what the server runs.
/// </summary>
internal static class JoinAdminPolicy
{
    internal const string NotAdminError = "Only server administrators can change who joins.";
    internal const string HandshakeError = "The framework handshake is not ready.";
    internal const string RateLimitedError = ServerSettings.RateLimitedError;
    internal const string UnknownOpError = "Unknown request.";

    /// <summary>The audit row id of a request.</summary>
    internal static string AuditRowFor(string? op, string? key) =>
        ConfigSync.FrameworkSettingsId + "/JoinAdmin/" + (string.IsNullOrEmpty(op) ? "?" : op)
        + (string.IsNullOrEmpty(key) ? "" : "/" + key);

    /// <summary><c>Networking.InstallGraceSeconds</c> as the panel stores it: 0 to 300.</summary>
    internal static int ClampWindow(int seconds) =>
        seconds < 0 ? 0 : seconds > JoinAdminLimits.MaxWindowSeconds ? JoinAdminLimits.MaxWindowSeconds : seconds;

    /// <summary>Null when <paramref name="reason"/> is a reason the panel stores; else why not.</summary>
    internal static string? CheckReason(string? reason)
    {
        string text = reason ?? "";
        if (text.Length > JoinAdminLimits.MaxReasonChars)
            return "The reason is longer than " + JoinAdminLimits.MaxReasonChars + " characters (" + text.Length.ToString(CultureInfo.InvariantCulture) + ").";
        foreach (char c in text)
            if (char.IsControl(c)) return "The reason must be one line of plain text.";
        return null;
    }

    /// <summary>Null when <paramref name="name"/> is a readable name the store keeps (one line, at most 64 characters).</summary>
    internal static string? CheckName(string? name)
    {
        string text = name ?? "";
        if (text.Length > JoinSectionLimits.MaxNameChars) return "The name is longer than " + JoinSectionLimits.MaxNameChars + " characters.";
        foreach (char c in text)
            if (char.IsControl(c)) return "The name must be one line of plain text.";
        return null;
    }

    /// <summary>Null when a recommended row is one the store keeps; else why not.</summary>
    internal static string? CheckRecommended(RecommendedMod mod)
    {
        if (mod.Id.Length == 0 && mod.Package.Length == 0) return "A recommended mod needs a plugin id or a Thunderstore package.";
        if (mod.Id.Length > 0 && !ModManifestIds.TryValidateId(mod.Id, out var idError)) return "The mod id is not valid (" + idError + ").";
        if (mod.Package.Length > 0 && !RemoteInstallWire.IsValidPackage(mod.Package)) return "The package is not a Thunderstore Owner-Name.";
        if (mod.Package.Length > JoinSectionLimits.MaxPackageChars) return "The package is too long.";
        if (mod.Version.Length > JoinSectionLimits.MaxVersionChars) return "The version is too long.";
        foreach (char c in mod.Version)
            if (char.IsControl(c) || char.IsWhiteSpace(c)) return "The version must be one word.";
        return CheckReason(mod.Reason);
    }

    /// <summary>
    /// Decide one request.
    /// <paramref name="requiredIds"/> every id the server requires, <paramref name="overlay"/> the
    /// <c>progenitor-manifest.json</c> rows, and <paramref name="windowSeconds"/> the current window.
    /// </summary>
    /// <param name="serverMods">C3: every mod id the server runs (a per-mod rule is refused for any other); null: <paramref name="requiredIds"/>.</param>
    /// <param name="forcedMatching">C3: the mode <c>[ClientPolicy] VersionMatching</c> forces, or null.</param>
    internal static JoinAdminDecision Decide(
        JoinAdminConfig current, JoinAdminRequest request,
        IReadOnlyCollection<string> requiredIds, IReadOnlyList<RecommendedMod>? overlay, int windowSeconds,
        IReadOnlyCollection<string>? serverMods = null, VersionMatchingMode? forcedMatching = null)
    {
        current ??= new JoinAdminConfig();
        string op = (request?.Op ?? "").Trim().ToLowerInvariant();
        string id = (request?.Id ?? "").Trim();
        string package = (request?.Package ?? "").Trim();
        string key = id.Length > 0 ? id : package;
        string row = AuditRowFor(op, key);
        switch (op)
        {
            case JoinAdminOps.Get:
                return new JoinAdminDecision { Ok = true, Config = current, AuditRow = row };

            case JoinAdminOps.RecommendAdd:
            {
                string reason = (request!.Reason ?? "").Trim();
                if (reason.Length == 0) reason = JoinAdminLimits.PlaceholderReason;
                var mod = new RecommendedMod(id, package, request.Version, reason);
                if (CheckRecommended(mod) is { } bad) return JoinAdminDecision.Refused(current, row, bad, after: reason);
                if (current.IndexOfRecommended(id) >= 0 || current.IndexOfRecommended(package) >= 0)
                    return JoinAdminDecision.Refused(current, row, "That mod is already recommended.");
                if ((id.Length > 0 && Contains(requiredIds, id)) || current.IsRequiredClient(id, package))
                    return JoinAdminDecision.Refused(current, row, "This server requires that mod, so it is not a recommendation.");
                if (current.Recommended.Count >= JoinAdminLimits.MaxRecommended)
                    return JoinAdminDecision.Refused(current, row, "At most " + JoinAdminLimits.MaxRecommended + " mods can be recommended.");
                string name = (request.Name ?? "").Trim();
                if (CheckName(name) is { } badName) return JoinAdminDecision.Refused(current, row, badName, after: name);
                var next = current.Clone();
                next.Recommended.Add(mod);
                if (id.Length > 0 && name.Length > 0 && !string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                    next.Names[id.ToLowerInvariant()] = name;
                return new JoinAdminDecision
                {
                    Ok = true, Config = next, RecommendedChanged = true, AuditRow = row,
                    After = Describe(mod),
                    Note = OverlayHas(overlay, mod) ? "This mod is also in progenitor-manifest.json; the reason set here is the one players see." : "",
                };
            }

            case JoinAdminOps.RecommendRemove:
            {
                int at = current.IndexOfRecommended(key);
                if (at < 0)
                    return JoinAdminDecision.Refused(current, row, OverlayHas(overlay, key)
                        ? "That recommendation comes from progenitor-manifest.json; edit that file to remove it."
                        : "That mod is not recommended.");
                var next = current.Clone();
                var old = next.Recommended[at];
                next.Recommended.RemoveAt(at);
                if (old.Id.Length > 0) next.Names.Remove(old.Id.ToLowerInvariant());
                return new JoinAdminDecision { Ok = true, Config = next, RecommendedChanged = true, AuditRow = row, Before = Describe(old) };
            }

            case JoinAdminOps.RecommendReason:
            {
                int at = current.IndexOfRecommended(key);
                string reason = (request!.Reason ?? "").Trim();
                if (at < 0)
                    return JoinAdminDecision.Refused(current, row, OverlayHas(overlay, key)
                        ? "That recommendation comes from progenitor-manifest.json; edit its reason in that file."
                        : "That mod is not recommended.", after: reason);
                var old = current.Recommended[at];
                if (CheckReason(reason) is { } bad) return JoinAdminDecision.Refused(current, row, bad, old.Reason, reason);
                if (reason.Length == 0) reason = JoinAdminLimits.PlaceholderReason;
                if (string.Equals(old.Reason, reason, StringComparison.Ordinal))
                    return new JoinAdminDecision { Ok = true, Config = current, AuditRow = row, Before = old.Reason, After = reason, Note = "Unchanged." };
                var next = current.Clone();
                next.Recommended[at] = new RecommendedMod(old.Id, old.Package, old.Version, reason);
                return new JoinAdminDecision { Ok = true, Config = next, RecommendedChanged = true, AuditRow = row, Before = old.Reason, After = reason };
            }

            case JoinAdminOps.Window:
            {
                if (!request!.HasSeconds)
                    return JoinAdminDecision.Refused(current, AuditRowFor(op, ""), "Give the window in whole seconds, 0 to " + JoinAdminLimits.MaxWindowSeconds + ".");
                int asked = request.Seconds;
                int value = ClampWindow(asked);
                return new JoinAdminDecision
                {
                    Ok = true, Config = current, WindowSeconds = value, AuditRow = AuditRowFor(op, ""),
                    Before = windowSeconds.ToString(CultureInfo.InvariantCulture), After = value.ToString(CultureInfo.InvariantCulture),
                    Note = value != asked ? "Clamped to " + value.ToString(CultureInfo.InvariantCulture) + " seconds (0 to " + JoinAdminLimits.MaxWindowSeconds + ")." : "",
                };
            }

            default:
                if (JoinAdminOps.IsPolicy(op)) return ClientPolicyDecisions.Decide(current, request!, op, id, package, row);
                if (JoinAdminOps.IsMatching(op)) return VersionMatchDecisions.Decide(current, request!, op, id, serverMods ?? requiredIds, forcedMatching);
                if (ServerOptionOps.IsOption(op)) return ServerOptionDecisions.Decide(current, request!, op);
                return JoinAdminDecision.Refused(current, AuditRowFor("?", ""), UnknownOpError);
        }
    }

    /// <summary>
    /// CLIENTPOLICY1: null when a client mod row is one the store keeps; else why not. A required row's reason is
    /// checked like a recommendation's; a rule other than Any needs a version.
    /// </summary>
    internal static string? CheckClientRow(ClientModRow mod, bool required)
    {
        if (mod.Id.Length == 0 && mod.Package.Length == 0) return "A client mod row needs a plugin id or a Thunderstore package.";
        if (mod.Id.Length > 0 && !ModManifestIds.TryValidateId(mod.Id, out var idError)) return "The mod id is not valid (" + idError + ").";
        if (mod.Package.Length > 0 && !RemoteInstallWire.IsValidPackage(mod.Package)) return "The package is not a Thunderstore Owner-Name.";
        if (mod.Package.Length > JoinSectionLimits.MaxPackageChars) return "The package is too long.";
        if (mod.Version.Length > JoinSectionLimits.MaxVersionChars) return "The version is too long.";
        foreach (char c in mod.Version)
            if (char.IsControl(c) || char.IsWhiteSpace(c)) return "The version must be one word.";
        if (CheckName(mod.Name) is { } badName) return badName;
        if (required) return CheckReason(mod.Reason);
        return null;
    }

    /// <summary>
    /// The recommended rows the server signs: the admin's rows first (in their order), then each
    /// <c>progenitor-manifest.json</c> row whose id or package no admin row names, up to
    /// <see cref="JoinAdminLimits.MaxRecommended"/>. An admin row for the same mod wins (its reason is shown).
    /// </summary>
    internal static List<RecommendedMod> Merge(IReadOnlyList<RecommendedMod>? admin, IReadOnlyList<RecommendedMod>? overlay)
    {
        var list = new List<RecommendedMod>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(RecommendedMod? m)
        {
            if (m == null || list.Count >= JoinAdminLimits.MaxRecommended) return;
            if ((m.Id.Length > 0 && keys.Contains(m.Id)) || (m.Package.Length > 0 && keys.Contains(m.Package))) return;
            list.Add(m);
            if (m.Id.Length > 0) keys.Add(m.Id);
            if (m.Package.Length > 0) keys.Add(m.Package);
        }

        if (admin != null) foreach (var m in admin) Add(m);
        if (overlay != null) foreach (var m in overlay) Add(m);
        return list;
    }

    private static bool OverlayHas(IReadOnlyList<RecommendedMod>? overlay, RecommendedMod mod) =>
        (mod.Id.Length > 0 && OverlayHas(overlay, mod.Id)) || (mod.Package.Length > 0 && OverlayHas(overlay, mod.Package));

    private static bool OverlayHas(IReadOnlyList<RecommendedMod>? overlay, string key)
    {
        if (overlay == null || string.IsNullOrEmpty(key)) return false;
        foreach (var m in overlay)
            if (string.Equals(m.Id, key, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Package, key, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool Contains(IReadOnlyCollection<string> ids, string id)
    {
        foreach (string s in ids)
            if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Describe(RecommendedMod m) =>
        (m.Id.Length > 0 ? m.Id : m.Package) + (m.Version.Length > 0 ? " " + m.Version : "") + ": " + m.Reason;
}

/// <summary>
/// CLIENTPOLICY1: the client mod policy's request decisions (docs/client-mod-policy.md, "Admin settings" and "Edge
/// cases"). Game-free; <see cref="JoinAdminPolicy.Decide"/> hands these operations here after the caller applied the
/// rate limit, the handshake and the authorization.
/// </summary>
internal static class ClientPolicyDecisions
{
    internal const string RequiredAlwaysAllowed = "Required mods are always allowed.";

    internal static JoinAdminDecision Decide(JoinAdminConfig current, JoinAdminRequest request, string op, string id, string package, string row)
    {
        var policy = current.Policy;
        switch (op)
        {
            case JoinAdminOps.RequiredAdd:
            {
                string version = (request.Version ?? "").Trim();
                if (!RuleOf(request.Rule, version, ClientModRule.AtLeast, out var rule, out string? badRule)) return JoinAdminDecision.Refused(current, row, badRule!);
                string reason = (request.Reason ?? "").Trim();
                if (reason.Length == 0) reason = JoinAdminLimits.PlaceholderReason;
                var mod = new ClientModRow(id, package, version, rule, reason, (request.Name ?? "").Trim());
                if (JoinAdminPolicy.CheckClientRow(mod, required: true) is { } bad) return JoinAdminDecision.Refused(current, row, bad, after: reason);
                if (ClientPolicyConfig.IndexOfMod(policy.Required, id, package) >= 0) return JoinAdminDecision.Refused(current, row, "That mod is already required.");
                if (policy.Required.Count >= ClientModPolicyLimits.MaxRequired)
                    return JoinAdminDecision.Refused(current, row, "At most " + ClientModPolicyLimits.MaxRequired + " client mods can be required.");
                var next = current.Clone();
                next.Policy.Required.Add(mod);
                // A mod can't be both required and recommended: adding it here moves it.
                string note = "";
                bool recommendedChanged = false;
                for (int i = next.Recommended.Count - 1; i >= 0; i--)
                {
                    var r = next.Recommended[i];
                    if (!mod.SameModAs(r.Id, r.Package)) continue;
                    next.Recommended.RemoveAt(i);
                    if (r.Id.Length > 0) next.Names.Remove(r.Id.ToLowerInvariant());
                    recommendedChanged = true;
                    note = "Moved from Recommended to Required.";
                }

                return new JoinAdminDecision
                {
                    Ok = true, Config = next, PolicyChanged = true, RecommendedChanged = recommendedChanged, AuditRow = row, After = mod.Describe(), Note = note,
                };
            }

            case JoinAdminOps.RequiredRemove:
            {
                int at = policy.IndexOfRequired(id.Length > 0 ? id : package);
                if (at < 0) return JoinAdminDecision.Refused(current, row, "That mod is not required.");
                var next = current.Clone();
                var old = next.Policy.Required[at];
                next.Policy.Required.RemoveAt(at);
                return new JoinAdminDecision { Ok = true, Config = next, PolicyChanged = true, AuditRow = row, Before = old.Describe() };
            }

            case JoinAdminOps.RequiredReason:
            {
                int at = policy.IndexOfRequired(id.Length > 0 ? id : package);
                string reason = (request.Reason ?? "").Trim();
                if (at < 0) return JoinAdminDecision.Refused(current, row, "That mod is not required.", after: reason);
                var old = policy.Required[at];
                if (JoinAdminPolicy.CheckReason(reason) is { } bad) return JoinAdminDecision.Refused(current, row, bad, old.Reason, reason);
                if (reason.Length == 0) reason = JoinAdminLimits.PlaceholderReason;
                if (string.Equals(old.Reason, reason, StringComparison.Ordinal))
                    return new JoinAdminDecision { Ok = true, Config = current, AuditRow = row, Before = old.Reason, After = reason, Note = "Unchanged." };
                var next = current.Clone();
                next.Policy.Required[at] = old.With(reason: reason);
                return new JoinAdminDecision { Ok = true, Config = next, PolicyChanged = true, AuditRow = row, Before = old.Reason, After = reason };
            }

            case JoinAdminOps.RequiredRule:
            {
                int at = policy.IndexOfRequired(id.Length > 0 ? id : package);
                if (at < 0) return JoinAdminDecision.Refused(current, row, "That mod is not required.");
                var old = policy.Required[at];
                string version = (request.Version ?? "").Trim();
                if (version.Length == 0) version = old.Version;
                if (!RuleOf(request.Rule, version, old.Rule, out var rule, out string? badRule)) return JoinAdminDecision.Refused(current, row, badRule!);
                var changed = old.With(version: version, rule: rule);
                if (JoinAdminPolicy.CheckClientRow(changed, required: true) is { } bad) return JoinAdminDecision.Refused(current, row, bad);
                string before = ClientModRules.Label(old.Rule, old.Version), after = ClientModRules.Label(rule, version);
                if (old.Rule == rule && string.Equals(old.Version, version, StringComparison.Ordinal))
                    return new JoinAdminDecision { Ok = true, Config = current, AuditRow = row, Before = before, After = after, Note = "Unchanged." };
                var next = current.Clone();
                next.Policy.Required[at] = changed;
                return new JoinAdminDecision { Ok = true, Config = next, PolicyChanged = true, AuditRow = row, Before = before, After = after };
            }

            case JoinAdminOps.PolicyMode:
            {
                if (!ClientModRules.TryParseMode(request.Value, out var mode))
                    return JoinAdminDecision.Refused(current, row, "Choose any or approved.", after: request.Value ?? "");
                string before = ClientModRules.ModeWord(policy.Mode), after = ClientModRules.ModeWord(mode);
                if (policy.Mode == mode) return new JoinAdminDecision { Ok = true, Config = current, AuditRow = row, Before = before, After = after, Note = "Unchanged." };
                var next = current.Clone();
                next.Policy.Mode = mode;
                return new JoinAdminDecision { Ok = true, Config = next, PolicyChanged = true, AuditRow = row, Before = before, After = after };
            }

            case JoinAdminOps.Unverified:
            {
                if (!ClientModRules.TryParseUnverified(request.Value, out var u))
                    return JoinAdminDecision.Refused(current, row, "Choose allow or refuse.", after: request.Value ?? "");
                string before = ClientModRules.UnverifiedWord(policy.Unverified), after = ClientModRules.UnverifiedWord(u);
                if (policy.Unverified == u) return new JoinAdminDecision { Ok = true, Config = current, AuditRow = row, Before = before, After = after, Note = "Unchanged." };
                var next = current.Clone();
                next.Policy.Unverified = u;
                return new JoinAdminDecision { Ok = true, Config = next, PolicyChanged = true, AuditRow = row, Before = before, After = after };
            }

            case JoinAdminOps.MidSession:
            {
                // CLIENTPOLICY3: Warn and pause it, or Disconnect the player. Not signed: the server's own notice tells a
                // player what happens, so the join section (and an older client's reading of it) is unchanged.
                if (!ClientModRules.TryParseMidSession(request.Value, out var mid))
                    return JoinAdminDecision.Refused(current, JoinAdminPolicy.AuditRowFor(op, ""), "Choose warn or disconnect.", after: request.Value ?? "");
                string before = ClientModRules.MidSessionWord(policy.MidSession), after = ClientModRules.MidSessionWord(mid);
                string midRow = JoinAdminPolicy.AuditRowFor(op, "");
                if (policy.MidSession == mid) return new JoinAdminDecision { Ok = true, Config = current, AuditRow = midRow, Before = before, After = after, Note = "Unchanged." };
                var next = current.Clone();
                next.Policy.MidSession = mid;
                return new JoinAdminDecision
                {
                    Ok = true, Config = next, PolicyChanged = true, AuditRow = midRow, Before = before, After = after,
                    Note = mid == MidSessionPolicy.Disconnect ? ClientPolicyText.MidSessionDisconnectNote : "",
                };
            }

            case JoinAdminOps.ApproveAdd:
            {
                string version = (request.Version ?? "").Trim();
                if (!RuleOf(request.Rule, version, ClientModRule.Any, out var rule, out string? badRule)) return JoinAdminDecision.Refused(current, row, badRule!);
                var mod = new ClientModRow(id, package, version, rule, "", (request.Name ?? "").Trim());
                if (JoinAdminPolicy.CheckClientRow(mod, required: false) is { } bad) return JoinAdminDecision.Refused(current, row, bad);
                if (ClientPolicyConfig.IndexOfMod(policy.Approved, id, package) >= 0) return JoinAdminDecision.Refused(current, row, "That mod is already approved.");
                if (policy.Approved.Count >= ClientModPolicyLimits.MaxApproved)
                    return JoinAdminDecision.Refused(current, row, "At most " + ClientModPolicyLimits.MaxApproved + " client mods can be approved.");
                var next = current.Clone();
                next.Policy.Approved.Add(mod);
                string note = ClientModJudge.AlwaysApproved(id) ? "That mod is always allowed; the row changes nothing." : "";
                return new JoinAdminDecision { Ok = true, Config = next, PolicyChanged = true, AuditRow = row, After = mod.Describe(), Note = note };
            }

            case JoinAdminOps.ApproveRemove:
            {
                string key = id.Length > 0 ? id : package;
                int at = policy.IndexOfApproved(key);
                if (at < 0) return JoinAdminDecision.Refused(current, row, "That mod is not approved.");
                var old = policy.Approved[at];
                if (ClientPolicyConfig.IndexOfMod(policy.Required, old.Id, old.Package) >= 0) return JoinAdminDecision.Refused(current, row, RequiredAlwaysAllowed);
                var next = current.Clone();
                next.Policy.Approved.RemoveAt(at);
                return new JoinAdminDecision { Ok = true, Config = next, PolicyChanged = true, AuditRow = row, Before = old.Describe() };
            }

            case JoinAdminOps.ApproveBulk:
            {
                string bulkRow = JoinAdminPolicy.AuditRowFor(op, "");
                if (request.Items.Count == 0) return JoinAdminDecision.Refused(current, bulkRow, "Name at least one mod to approve.");
                if (request.Items.Count > ClientModPolicyLimits.MaxBulk)
                    return JoinAdminDecision.Refused(current, bulkRow, "At most " + ClientModPolicyLimits.MaxBulk + " mods can be approved in one request.");
                var next = current.Clone();
                int added = 0, already = 0, always = 0, full = 0;
                var bad = new List<string>();
                var addedKeys = new List<string>();
                foreach (var item in request.Items)
                {
                    var mod = new ClientModRow(item.Id, item.Package, "", ClientModRule.Any, "", CleanName(item.Name));
                    if (JoinAdminPolicy.CheckClientRow(mod, required: false) is { } why)
                    {
                        if (bad.Count < 3) bad.Add((mod.Key.Length > 0 ? mod.Key : "(no id)") + ": " + why);
                        continue;
                    }

                    if (ClientModJudge.AlwaysApproved(mod.Id)) { always++; continue; }
                    if (ClientPolicyConfig.IndexOfMod(next.Policy.Approved, mod.Id, mod.Package) >= 0) { already++; continue; }
                    if (next.Policy.Approved.Count >= ClientModPolicyLimits.MaxApproved) { full++; continue; }
                    next.Policy.Approved.Add(mod);
                    addedKeys.Add(mod.Key);
                    added++;
                }

                var notes = new List<string> { "Approved " + added.ToString(CultureInfo.InvariantCulture) + (added == 1 ? " mod." : " mods.") };
                if (already > 0) notes.Add(already.ToString(CultureInfo.InvariantCulture) + " already approved.");
                if (always > 0) notes.Add(always.ToString(CultureInfo.InvariantCulture) + " always allowed.");
                if (full > 0) notes.Add(full.ToString(CultureInfo.InvariantCulture) + " not added: at most " + ClientModPolicyLimits.MaxApproved + " can be approved.");
                if (bad.Count > 0) notes.Add("Skipped " + string.Join("; ", bad) + ".");
                if (added == 0)
                    return new JoinAdminDecision { Ok = bad.Count == 0 && full == 0, Error = bad.Count > 0 || full > 0 ? string.Join(" ", notes) : "", Config = current, AuditRow = bulkRow, Note = string.Join(" ", notes) };
                return new JoinAdminDecision
                {
                    Ok = true, Config = next, PolicyChanged = true, AuditRow = bulkRow, After = string.Join(", ", addedKeys), Note = string.Join(" ", notes),
                };
            }
        }

        return JoinAdminDecision.Refused(current, JoinAdminPolicy.AuditRowFor("?", ""), JoinAdminPolicy.UnknownOpError);
    }

    /// <summary>The row's rule: the request's word, else <paramref name="fallback"/>; a rule other than Any needs a version.</summary>
    private static bool RuleOf(string? word, string version, ClientModRule fallback, out ClientModRule rule, out string? error)
    {
        error = null;
        rule = fallback;
        string w = (word ?? "").Trim();
        if (w.Length > 0 && !ClientModRules.TryParse(w, out rule))
        {
            error = "Choose a version rule: at-least, exact, minor or any.";
            return false;
        }

        if (w.Length == 0 && version.Length == 0) rule = ClientModRule.Any;
        if (rule != ClientModRule.Any && version.Length == 0)
        {
            error = "Give a version for this rule, or choose Any version.";
            return false;
        }

        return true;
    }

    private static string CleanName(string? name)
    {
        string n = (name ?? "").Trim();
        return n.Length > JoinSectionLimits.MaxNameChars ? n.Substring(0, JoinSectionLimits.MaxNameChars) : n;
    }
}
