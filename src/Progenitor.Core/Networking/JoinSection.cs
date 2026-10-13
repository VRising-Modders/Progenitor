using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>How a reset notification repeats once its time has passed (JOINFLOW1).</summary>
public enum ResetRepeat
{
    /// <summary>One time only: after it passes, <c>{reset}</c> shows the passed text.</summary>
    None = 0,

    /// <summary>Every N days, in the server's local calendar.</summary>
    Days = 1,

    /// <summary>Every N weeks, in the server's local calendar.</summary>
    Weeks = 2,
}

/// <summary>How finely the <c>{reset}</c> countdown is drawn: <c>3d</c>, <c>3d 4h</c>, <c>3d 4h 17m</c>, <c>3d 4h 17m 05s</c>.</summary>
public enum ResetPrecision
{
    Day = 0,
    Hour = 1,
    Minute = 2,
    Second = 3,
}

/// <summary>When the server message page shows on a join (an admin's server-wide choice, JOINFLOW1).</summary>
public enum MessageFrequency
{
    /// <summary>On first join, again whenever the text changes, and on the first join after each reset passes (default).</summary>
    WhenChanged = 0,

    /// <summary>On every join. Players get no "don't show again".</summary>
    Always = 1,

    /// <summary>Once per server, then only when the text changes.</summary>
    FirstJoin = 2,
}

/// <summary>When the mods page shows on a successful join (an admin's server-wide choice, JOINFLOW1).</summary>
public enum ModsPageFrequency
{
    /// <summary>Only when a required mod needs attention or the server's mod lists changed since the last visit (default).</summary>
    WhenNeeded = 0,

    /// <summary>On every join.</summary>
    Always = 1,
}

/// <summary>
/// JOINFLOW1: the server's reset <b>notification</b>. It is a countdown shown in the server message and
/// nothing else: no code anywhere resets a world, a castle or a player when it reaches zero. A real
/// scheduled reset is a possible later feature (docs/join-flow.md).
/// </summary>
public sealed class ResetSchedule
{
    public ResetSchedule(string? nextUtc, ResetRepeat repeat, int every, ResetPrecision precision, string? passedText)
    {
        NextUtc = (nextUtc ?? "").Trim();
        Repeat = Enum.IsDefined(typeof(ResetRepeat), repeat) ? repeat : ResetRepeat.None;
        Every = every < 1 ? 1 : every > JoinSectionLimits.MaxRepeatEvery ? JoinSectionLimits.MaxRepeatEvery : every;
        Precision = Enum.IsDefined(typeof(ResetPrecision), precision) ? precision : ResetPrecision.Minute;
        PassedText = string.IsNullOrWhiteSpace(passedText) ? ResetCountdown.DefaultPassedText : passedText!.Trim();
    }

    /// <summary>The next reset as an ISO 8601 UTC string (<c>2026-10-10T18:00:00Z</c>), or empty for none.</summary>
    public string NextUtc { get; }

    public ResetRepeat Repeat { get; }

    /// <summary>How many days or weeks one repeat is (1 to 365).</summary>
    public int Every { get; }

    public ResetPrecision Precision { get; }

    /// <summary>What <c>{reset}</c> reads after a one-time reset has passed (default <c>now</c>).</summary>
    public string PassedText { get; }

    /// <summary>True when <see cref="NextUtc"/> parses as a time.</summary>
    public bool IsSet => ResetCountdown.TryParseUtc(NextUtc, out _);
}

/// <summary>JOINFLOW1: one mod the server recommends (stage 1 reads them from <c>progenitor-manifest.json</c>).</summary>
public sealed class RecommendedMod
{
    public RecommendedMod(string? id, string? package, string? version, string? reason)
    {
        Id = (id ?? "").Trim();
        Package = (package ?? "").Trim();
        Version = (version ?? "").Trim();
        Reason = (reason ?? "").Trim();
    }

    /// <summary>The BepInEx plugin GUID the client checks for "Installed".</summary>
    public string Id { get; }

    /// <summary>Thunderstore <c>Owner-Name</c>, or empty.</summary>
    public string Package { get; }

    public string Version { get; }

    /// <summary>The admin's one-line reason, drawn in italics.</summary>
    public string Reason { get; }
}

/// <summary>The server's time zone, so an admin's date picker and the roll-forward use the server's calendar.</summary>
public sealed class JoinServerZone
{
    public JoinServerZone(string? id, int offsetMinutes)
    {
        Id = (id ?? "").Trim();
        OffsetMinutes = offsetMinutes < -14 * 60 ? -14 * 60 : offsetMinutes > 14 * 60 ? 14 * 60 : offsetMinutes;
    }

    /// <summary>The server's <c>TimeZoneInfo.Id</c> (Windows ids on a Windows server), or empty.</summary>
    public string Id { get; }

    /// <summary>The server's UTC offset when the section was built, in minutes.</summary>
    public int OffsetMinutes { get; }

    /// <summary>
    /// The zone on this machine: the same id when this machine knows it, else a fixed-offset zone (no
    /// daylight-saving rules: a client that cannot resolve the id rolls forward in plain hours).
    /// </summary>
    public TimeZoneInfo Resolve()
    {
        if (Id.Length > 0)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(Id); }
            catch { /* fall through to the offset */ }
        }

        if (OffsetMinutes == 0) return TimeZoneInfo.Utc;
        string name = "Server time (" + ResetCountdown.OffsetLabel(TimeSpan.FromMinutes(OffsetMinutes)) + ")";
        return TimeZoneInfo.CreateCustomTimeZone("progenitor-server-" + OffsetMinutes.ToString(CultureInfo.InvariantCulture),
            TimeSpan.FromMinutes(OffsetMinutes), name, name);
    }
}

/// <summary>
/// JOINFLOW1: what a joining player sees before the mods page — the admins' message, the reset
/// notification, the recommended mods and the server's time zone. It travels inside the signed
/// catalog envelope as a <b>separate signed section</b> (<c>join</c> + <c>joinSignature</c>), never
/// inside the catalog body, for two reasons:
///
/// <list type="bullet">
/// <item><b>Older clients.</b> A client re-canonicalizes the body from the fields it knows and checks the
/// signature over that; a new body key would make every older client reject the whole catalog as a bad
/// signature. Unknown envelope keys, on the other hand, are ignored by every client's parser
/// (<see cref="ModManifestJson.TryParseEnvelope"/> reads only <c>body</c> and <c>signature</c>).</item>
/// <item><b>The deny rule.</b> A deny descriptor may add package/url/sha256/size to a refusal row and may
/// not add a row. The recommended list and the message are not refusal rows and never become rows of the
/// deny report; they are drawn in their own sections of the join screens.</item>
/// </list>
///
/// <para>The section is bound to the body it rode with (<see cref="CatalogIssued"/> and
/// <see cref="CatalogFp"/> must equal the body's), signed by the same operator key over a domain-prefixed
/// canonical form (<see cref="JoinSectionJson.SignDomain"/>), and verified on the client after the body
/// passed its own fingerprint, signature and replay gates. A section that fails any check is dropped and
/// the catalog is still used.</para>
/// </summary>
public sealed class JoinSection
{
    public const int CurrentSchema = 1;

    public JoinSection(
        string? message,
        ResetSchedule? reset,
        IReadOnlyList<RecommendedMod>? recommended,
        JoinServerZone? zone,
        string? catalogIssued = null,
        string? catalogFp = null,
        int schema = CurrentSchema,
        MessageFrequency showMessage = MessageFrequency.WhenChanged,
        ModsPageFrequency showMods = ModsPageFrequency.WhenNeeded,
        IReadOnlyDictionary<string, string>? names = null,
        IReadOnlyDictionary<string, string>? versions = null)
    {
        Schema = schema;
        Names = IdMap(names);
        Versions = IdMap(versions);
        ShowMessage = Enum.IsDefined(typeof(MessageFrequency), showMessage) ? showMessage : MessageFrequency.WhenChanged;
        ShowMods = Enum.IsDefined(typeof(ModsPageFrequency), showMods) ? showMods : ModsPageFrequency.WhenNeeded;
        Message = message ?? "";
        Reset = reset != null && reset.IsSet ? reset : null;
        Recommended = recommended ?? Array.Empty<RecommendedMod>();
        Zone = zone ?? new JoinServerZone("", 0);
        CatalogIssued = catalogIssued ?? "";
        CatalogFp = (catalogFp ?? "").ToLowerInvariant();
    }

    public int Schema { get; }

    /// <summary>The admins' plain-text message, line breaks kept, at most 2,000 characters.</summary>
    public string Message { get; }

    /// <summary>The reset notification, or null when none is set.</summary>
    public ResetSchedule? Reset { get; }

    public IReadOnlyList<RecommendedMod> Recommended { get; }

    public JoinServerZone Zone { get; }

    /// <summary>When the message page shows.</summary>
    public MessageFrequency ShowMessage { get; }

    /// <summary>When the mods page shows on a successful join.</summary>
    public ModsPageFrequency ShowMods { get; }

    /// <summary>
    /// The readable names of the mods the server requires (lowercase id → name, e.g. <c>Progenitor Sample</c>),
    /// so a client that lacks a mod can still name it. Empty when the server sent none.
    /// </summary>
    public IReadOnlyDictionary<string, string> Names { get; }

    /// <summary>The server's readable name for <paramref name="id"/>, or null.</summary>
    public string? NameOf(string? id) =>
        !string.IsNullOrEmpty(id) && Names.TryGetValue(id!.ToLowerInvariant(), out var name) ? name : null;

    /// <summary>
    /// The versions the server's handshake requires now (lowercase id → version), from its loaded plugins, so
    /// the in-world mods page judges against the server's current state after a hot swap. Unlike a catalog
    /// row's version, an operator overlay pin never changes it. Empty when the server sent none.
    /// </summary>
    public IReadOnlyDictionary<string, string> Versions { get; }

    /// <summary>The server's current required version of <paramref name="id"/>, or null.</summary>
    public string? VersionOf(string? id) =>
        !string.IsNullOrEmpty(id) && Versions.TryGetValue(id!.ToLowerInvariant(), out var v) ? v : null;

    private static SortedDictionary<string, string> IdMap(IReadOnlyDictionary<string, string>? source)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (source != null)
            foreach (var pair in source)
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    map[pair.Key.Trim().ToLowerInvariant()] = pair.Value.Trim();
        return map;
    }

    /// <summary>The <c>issued</c> of the catalog body this section rode with.</summary>
    public string CatalogIssued { get; }

    /// <summary>The operator fingerprint of the catalog body this section rode with.</summary>
    public string CatalogFp { get; }

    /// <summary>True when there is nothing to show: no message, no reset and no recommended mod.</summary>
    public bool IsEmpty => Message.Trim().Length == 0 && Reset == null && Recommended.Count == 0;

    /// <summary>This section bound to a catalog body (its <c>issued</c> and operator fingerprint).</summary>
    public JoinSection BoundTo(ManifestBody? body) =>
        new JoinSection(Message, Reset, Recommended, Zone, body?.Issued ?? "", body?.Operator?.Fp ?? "", Schema, ShowMessage, ShowMods, Names, Versions)
            .CarryPolicy(this);

    // ---- CLIENTPOLICY1: the admins' client mod policy, signed so the join screens draw from signed data ----------

    /// <summary>The required client mods (the admins' rows: name, package, version rule, reason). Empty when none.</summary>
    internal IReadOnlyList<ClientModRow> RequiredClient { get; private set; } = Array.Empty<ClientModRow>();

    /// <summary>Any mods (the default, not written) or Only approved.</summary>
    internal ClientPolicyMode PolicyMode { get; private set; } = ClientPolicyMode.Any;

    /// <summary>The approved client mods (names and rules). Empty when none.</summary>
    internal IReadOnlyList<ClientModRow> Approved { get; private set; } = Array.Empty<ClientModRow>();

    /// <summary>
    /// CLIENTPOLICY2: the plugin GUIDs the server runs (its loaded plugins and the rows its handshake requires), sorted, so
    /// a client judges "a mod this server runs" as the server's rule 2 does before it sends its report. Written only under
    /// Only approved (nothing else reads it). Empty when none.
    /// </summary>
    internal IReadOnlyList<string> ServerRuns { get; private set; } = Array.Empty<string>();

    /// <summary>True when the section carries a client mod policy (the server asks for the client's mod report).</summary>
    internal bool HasClientPolicy => RequiredClient.Count > 0 || PolicyMode == ClientPolicyMode.Approved;

    /// <summary>The same section carrying this client mod policy.</summary>
    internal JoinSection WithPolicy(IReadOnlyList<ClientModRow>? required, ClientPolicyMode mode, IReadOnlyList<ClientModRow>? approved) =>
        WithPolicy(required, mode, approved, ServerRuns);

    /// <summary>The same section carrying this client mod policy and the server's run list (CLIENTPOLICY2).</summary>
    internal JoinSection WithPolicy(IReadOnlyList<ClientModRow>? required, ClientPolicyMode mode, IReadOnlyList<ClientModRow>? approved,
        IReadOnlyList<string>? runs)
    {
        var copy = (JoinSection)MemberwiseClone();
        copy.RequiredClient = required == null ? Array.Empty<ClientModRow>() : new List<ClientModRow>(required);
        copy.PolicyMode = Enum.IsDefined(typeof(ClientPolicyMode), mode) ? mode : ClientPolicyMode.Any;
        copy.Approved = approved == null ? Array.Empty<ClientModRow>() : new List<ClientModRow>(approved);
        copy.ServerRuns = JoinSectionJson.CleanRuns(runs);
        return copy;
    }

    /// <summary>This section with <paramref name="from"/>'s client mod policy (copies made through the constructor keep it).</summary>
    internal JoinSection CarryPolicy(JoinSection? from) =>
        from == null ? this : WithPolicy(from.RequiredClient, from.PolicyMode, from.Approved, from.ServerRuns);
}

/// <summary>Size caps of the join section (JOINFLOW1). The parser drops a section that breaks one.</summary>
internal static class JoinSectionLimits
{
    /// <summary>The server message, in characters (the settings row's <c>MaxLength</c>).</summary>
    public const int MaxMessageChars = 2000;

    /// <summary>Recommended mods per server.</summary>
    public const int MaxRecommended = 16;

    public const int MaxReasonChars = 200;

    public const int MaxPackageChars = 128;

    public const int MaxVersionChars = 64;

    public const int MaxIdChars = 63;

    public const int MaxPassedTextChars = 120;

    public const int MaxZoneIdChars = 64;

    public const int MaxRepeatEvery = 365;

    /// <summary>Readable mod names the section carries, and the longest name.</summary>
    public const int MaxNames = 64;

    public const int MaxNameChars = 64;

    /// <summary>
    /// The canonical section, in UTF-8 bytes. At every cap with three-byte characters throughout, the
    /// message is about 6 KiB and sixteen recommended rows about 22 KiB; CLIENTPOLICY1's sixteen required client
    /// rows add about 22 KiB and 128 approved rows about 50 KiB at their caps (ASCII ids and packages), and CLIENTPOLICY2's
    /// run list of 256 ids about 17 KiB, so a section at every limit is about 117 KiB: inside this cap, inside the
    /// catalog's 256 KiB reassembly cap, and chunked
    /// at 15 KiB like the rest of the envelope. (A reader older than CLIENTPOLICY1 drops a section with policy keys
    /// anyway: its canonical form does not have them.)
    /// </summary>
    public const int MaxSectionBytes = 128 * 1024;

    /// <summary>CLIENTPOLICY1: rows the section carries (the store's own caps).</summary>
    public const int MaxRequiredClient = ClientModPolicyLimits.MaxRequired;

    public const int MaxApproved = ClientModPolicyLimits.MaxApproved;

    /// <summary>CLIENTPOLICY2: plugin GUIDs the section's run list carries (at most 63 characters each, about 16 KiB).</summary>
    public const int MaxServerRuns = 256;
}

/// <summary>Canonical JSON, parsing, signing and verification of the join section (JOINFLOW1).</summary>
internal static class JoinSectionJson
{
    /// <summary>
    /// Prefix of the signed bytes. The operator key also signs catalog bodies, whose canonical form starts
    /// with <c>{</c>; a prefix that cannot start a body keeps a join signature from ever verifying as a body
    /// signature, or the other way round.
    /// </summary>
    internal const string SignDomain = "progenitor.join.v1\n";

    /// <summary>The same section carrying <paramref name="names"/> (the server's readable names of the mods it requires).</summary>
    internal static JoinSection WithNames(JoinSection section, IReadOnlyDictionary<string, string>? names) =>
        new JoinSection(section.Message, section.Reset, section.Recommended, section.Zone, section.CatalogIssued, section.CatalogFp,
            section.Schema, section.ShowMessage, section.ShowMods, names, section.Versions).CarryPolicy(section);

    /// <summary>The same section carrying the server's readable names and current versions of the mods it requires.</summary>
    internal static JoinSection WithRequired(JoinSection section, IReadOnlyDictionary<string, string>? names, IReadOnlyDictionary<string, string>? versions) =>
        new JoinSection(section.Message, section.Reset, section.Recommended, section.Zone, section.CatalogIssued, section.CatalogFp,
            section.Schema, section.ShowMessage, section.ShowMods, names, versions).CarryPolicy(section);

    /// <summary>
    /// CLIENTPOLICY1: the same section carrying the admins' client mod policy (<paramref name="policy"/>), within the
    /// section's caps: the required client rows, the mode, and the approved rows' names and rules.
    /// </summary>
    internal static JoinSection WithClientPolicy(JoinSection section, ClientPolicyConfig? policy) =>
        WithClientPolicy(section, policy, null);

    /// <summary>
    /// CLIENTPOLICY2: the same with the server's run list (<paramref name="runs"/>, the GUIDs of the mods it runs), signed
    /// only under Only approved, so a client judges "a mod this server runs" before it reports (the automatic pause).
    /// </summary>
    internal static JoinSection WithClientPolicy(JoinSection section, ClientPolicyConfig? policy, IEnumerable<string>? runs)
    {
        if (section == null) throw new ArgumentNullException(nameof(section));
        if (policy == null) return section.WithPolicy(null, ClientPolicyMode.Any, null, null);
        var required = new List<ClientModRow>();
        foreach (var r in policy.Required)
            if (required.Count < JoinSectionLimits.MaxRequiredClient && ClientRowFits(r, withReason: true, out _)) required.Add(r);
        var approved = new List<ClientModRow>();
        foreach (var r in policy.Approved)
            if (approved.Count < JoinSectionLimits.MaxApproved && ClientRowFits(r, withReason: false, out _)) approved.Add(r.With(reason: ""));
        var list = policy.Mode == ClientPolicyMode.Approved && runs != null ? new List<string>(runs) : null;
        return section.WithPolicy(required, policy.Mode, approved, list);
    }

    /// <summary>
    /// CLIENTPOLICY2: a run list within its caps: plain ids (<c>[A-Za-z0-9._-]</c>, at most 63 characters), lowercase, no
    /// repeats, sorted (so the canonical form does not depend on load order), at most <see cref="JoinSectionLimits.MaxServerRuns"/>.
    /// </summary>
    internal static IReadOnlyList<string> CleanRuns(IEnumerable<string>? runs)
    {
        if (runs == null) return Array.Empty<string>();
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string id in runs)
        {
            string s = (id ?? "").Trim().ToLowerInvariant();
            if (!ClientModJudge.IsPlainId(s)) continue;
            set.Add(s);
        }

        var list = new List<string>(Math.Min(set.Count, JoinSectionLimits.MaxServerRuns));
        foreach (string s in set)
        {
            if (list.Count >= JoinSectionLimits.MaxServerRuns) break;
            list.Add(s);
        }

        return list.Count == 0 ? Array.Empty<string>() : list;
    }

    private static bool ClientRowFits(ClientModRow r, bool withReason, out string? error)
    {
        error = null;
        if (r.Id.Length == 0 && r.Package.Length == 0) error = "a client mod row has no id or package";
        else if (r.Id.Length > JoinSectionLimits.MaxIdChars) error = "a client mod id is longer than " + JoinSectionLimits.MaxIdChars + " characters";
        else if (r.Package.Length > JoinSectionLimits.MaxPackageChars) error = "a client mod package is too long";
        else if (r.Version.Length > JoinSectionLimits.MaxVersionChars) error = "a client mod version is too long";
        else if (r.Name.Length > JoinSectionLimits.MaxNameChars) error = "a client mod name is too long";
        else if (withReason && r.Reason.Length > JoinSectionLimits.MaxReasonChars) error = "a client mod reason is too long";
        return error == null;
    }

    private static void WriteClientRows(StringBuilder sb, string key, IReadOnlyList<ClientModRow> rows, bool withReason)
    {
        Key(sb, key, first: false);
        sb.Append('[');
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Key(sb, "id", first: true);
            ModManifestJson.WriteString(sb, r.Id);
            Key(sb, "name", first: false);
            ModManifestJson.WriteString(sb, r.Name);
            Key(sb, "package", first: false);
            ModManifestJson.WriteString(sb, r.Package);
            if (withReason)
            {
                Key(sb, "reason", first: false);
                ModManifestJson.WriteString(sb, r.Reason);
            }

            Key(sb, "rule", first: false);
            ModManifestJson.WriteString(sb, ClientModRules.Word(r.Rule));
            Key(sb, "version", first: false);
            ModManifestJson.WriteString(sb, r.Version);
            sb.Append('}');
        }

        sb.Append(']');
    }

    // Reads a client mod row list within its caps; false (with the error) when over one or a rule is unknown.
    private static bool ReadClientRows(JsonElement el, string key, bool withReason, int max, List<ClientModRow> into, out string? error)
    {
        error = null;
        if (!el.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return true;
        foreach (var row in arr.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (into.Count >= max)
            {
                error = "join lists more than " + max + " " + key + " rows";
                return false;
            }

            string ruleWord = Str(row, "rule");
            if (!ClientModRules.TryParse(ruleWord, out var rule))
            {
                error = "join " + key + " row has an unknown rule \"" + ruleWord + "\"";
                return false;
            }

            var r = new ClientModRow(Str(row, "id"), Str(row, "package"), Str(row, "version"), rule, withReason ? Str(row, "reason") : "", Str(row, "name"));
            if (!ClientRowFits(r, withReason, out error)) return false;
            into.Add(r);
        }

        return true;
    }

    private static void WriteMap(StringBuilder sb, string key, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return;
        Key(sb, key, first: false);
        sb.Append('{');
        bool first = true;
        foreach (var pair in map)
        {
            if (!first) sb.Append(',');
            first = false;
            ModManifestJson.WriteString(sb, pair.Key);
            sb.Append(':');
            ModManifestJson.WriteString(sb, pair.Value);
        }

        sb.Append('}');
    }

    // Reads an id → text map within the caps; false (with the error) when over one.
    private static bool ReadMap(JsonElement el, string key, int maxValueChars, Dictionary<string, string> into, out string? error)
    {
        error = null;
        if (!el.TryGetProperty(key, out var m) || m.ValueKind != JsonValueKind.Object) return true;
        foreach (var p in m.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            string value = p.Value.GetString() ?? "";
            if (into.Count >= JoinSectionLimits.MaxNames || p.Name.Length > JoinSectionLimits.MaxIdChars || value.Length > maxValueChars)
            {
                error = "join " + key + " are over their caps";
                return false;
            }

            into[p.Name] = value;
        }

        return true;
    }

    internal static string Canonical(JoinSection section)
    {
        section ??= new JoinSection("", null, null, null);
        var sb = new StringBuilder(256 + section.Message.Length);
        sb.Append('{');
        Key(sb, "catalog", first: true);
        sb.Append('{');
        Key(sb, "fp", first: true);
        ModManifestJson.WriteString(sb, section.CatalogFp);
        Key(sb, "issued", first: false);
        ModManifestJson.WriteString(sb, section.CatalogIssued);
        sb.Append('}');
        Key(sb, "message", first: false);
        ModManifestJson.WriteString(sb, section.Message);
        // Written only when the server sent names, like the frequencies and versions below.
        WriteMap(sb, "names", section.Names);

        Key(sb, "recommended", first: false);
        sb.Append('[');
        for (int i = 0; i < section.Recommended.Count; i++)
        {
            var r = section.Recommended[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            Key(sb, "id", first: true);
            ModManifestJson.WriteString(sb, r.Id);
            Key(sb, "package", first: false);
            ModManifestJson.WriteString(sb, r.Package);
            Key(sb, "reason", first: false);
            ModManifestJson.WriteString(sb, r.Reason);
            Key(sb, "version", first: false);
            ModManifestJson.WriteString(sb, r.Version);
            sb.Append('}');
        }

        sb.Append(']');
        Key(sb, "reset", first: false);
        if (section.Reset == null) sb.Append("null");
        else
        {
            var r = section.Reset;
            sb.Append('{');
            Key(sb, "every", first: true);
            sb.Append(r.Every.ToString(CultureInfo.InvariantCulture));
            Key(sb, "next", first: false);
            ModManifestJson.WriteString(sb, r.NextUtc);
            Key(sb, "passed", first: false);
            ModManifestJson.WriteString(sb, r.PassedText);
            Key(sb, "precision", first: false);
            ModManifestJson.WriteString(sb, r.Precision.ToString());
            Key(sb, "repeat", first: false);
            ModManifestJson.WriteString(sb, r.Repeat.ToString());
            sb.Append('}');
        }

        Key(sb, "schema", first: false);
        sb.Append(section.Schema.ToString(CultureInfo.InvariantCulture));
        // Written only when not the default, so a section without them (an earlier server) re-canonicalizes
        // to the bytes it was signed over, and a reader that predates them verifies a default section.
        if (section.ShowMessage != MessageFrequency.WhenChanged)
        {
            Key(sb, "showMessage", first: false);
            ModManifestJson.WriteString(sb, section.ShowMessage.ToString());
        }

        if (section.ShowMods != ModsPageFrequency.WhenNeeded)
        {
            Key(sb, "showMods", first: false);
            ModManifestJson.WriteString(sb, section.ShowMods.ToString());
        }

        WriteMap(sb, "versions", section.Versions);
        // CLIENTPOLICY1: written only when set, like the keys above, so a section without a policy re-canonicalizes to
        // the bytes an earlier server signed (and an earlier reader verifies it).
        if (section.Approved.Count > 0) WriteClientRows(sb, "approved", section.Approved, withReason: false);
        if (section.PolicyMode != ClientPolicyMode.Any)
        {
            Key(sb, "policyMode", first: false);
            ModManifestJson.WriteString(sb, ClientModRules.ModeWord(section.PolicyMode));
        }

        if (section.RequiredClient.Count > 0) WriteClientRows(sb, "requiredClient", section.RequiredClient, withReason: true);
        // CLIENTPOLICY2: written only when set (Only approved), like the policy keys above.
        if (section.ServerRuns.Count > 0)
        {
            Key(sb, "runs", first: false);
            sb.Append('[');
            for (int i = 0; i < section.ServerRuns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                ModManifestJson.WriteString(sb, section.ServerRuns[i]);
            }

            sb.Append(']');
        }

        Key(sb, "zone", first: false);
        sb.Append('{');
        Key(sb, "id", first: true);
        ModManifestJson.WriteString(sb, section.Zone.Id);
        Key(sb, "offset", first: false);
        sb.Append(section.Zone.OffsetMinutes.ToString(CultureInfo.InvariantCulture));
        sb.Append('}');
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>The bytes the operator key signs: <see cref="SignDomain"/> then the canonical section.</summary>
    internal static byte[] SignedBytes(JoinSection section) => ModManifestCrypto.Utf8(SignDomain + Canonical(section));

    /// <summary>Sign a section already bound to its body.</summary>
    internal static string Sign(JoinSection section, ModManifestKey key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        return key.Sign(SignedBytes(section));
    }

    /// <summary>
    /// The section of an envelope whose body already passed its gates, or null with the reason: absent, bound to
    /// another body, signed by another key, or a bad signature.
    /// </summary>
    internal static JoinSection? Verified(ManifestEnvelope? envelope, out string? error)
    {
        error = null;
        var section = envelope?.Join;
        if (envelope == null || section == null)
        {
            error = envelope?.JoinError;
            return null;
        }

        var body = envelope.Body;
        if (!string.Equals(section.CatalogIssued, body.Issued, StringComparison.Ordinal)
            || !string.Equals(section.CatalogFp, (body.Operator.Fp ?? "").ToLowerInvariant(), StringComparison.Ordinal))
        {
            error = "the join section belongs to another catalog (issued " + section.CatalogIssued + ", fp " + section.CatalogFp + ")";
            return null;
        }

        bool ok;
        try { ok = ModManifestCrypto.Verify(body.Operator.Spki ?? "", SignedBytes(section), envelope.JoinSignature ?? ""); }
        catch { ok = false; }
        if (!ok)
        {
            error = "the join section's signature does not match the catalog's operator key";
            return null;
        }

        return section;
    }

    /// <summary>
    /// Read a <c>join</c> object. Fails closed: a field over its cap, an unknown schema or a section over
    /// <see cref="JoinSectionLimits.MaxSectionBytes"/> drops the whole section (the catalog is still used).
    /// Unknown keys are ignored when reading, but the signature is checked over the canonical form this reader
    /// rebuilds, so a section carrying a key or value it does not know fails verification. A later field must
    /// be written only when it differs from a default older readers assume, or come with a schema bump.
    /// </summary>
    internal static bool TryRead(JsonElement el, out JoinSection? section, out string? error)
    {
        section = null;
        error = null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            error = "join is not an object";
            return false;
        }

        int schema = el.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int n) ? n : 0;
        if (schema != JoinSection.CurrentSchema)
        {
            error = "join schema " + schema.ToString(CultureInfo.InvariantCulture) + " is not one this client reads";
            return false;
        }

        string message = Str(el, "message");
        if (message.Length > JoinSectionLimits.MaxMessageChars)
        {
            error = "join message is longer than " + JoinSectionLimits.MaxMessageChars + " characters";
            return false;
        }

        string issued = "", fp = "";
        if (el.TryGetProperty("catalog", out var cat) && cat.ValueKind == JsonValueKind.Object)
        {
            issued = Str(cat, "issued");
            fp = Str(cat, "fp");
        }

        ResetSchedule? reset = null;
        if (el.TryGetProperty("reset", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            string passed = Str(r, "passed");
            if (passed.Length > JoinSectionLimits.MaxPassedTextChars)
            {
                error = "join reset passed text is too long";
                return false;
            }

            if (!Enum.TryParse(Str(r, "repeat"), ignoreCase: true, out ResetRepeat repeat)) repeat = ResetRepeat.None;
            if (!Enum.TryParse(Str(r, "precision"), ignoreCase: true, out ResetPrecision precision)) precision = ResetPrecision.Minute;
            int every = r.TryGetProperty("every", out var ev) && ev.ValueKind == JsonValueKind.Number && ev.TryGetInt32(out int e) ? e : 1;
            reset = new ResetSchedule(Str(r, "next"), repeat, every, precision, passed);
        }

        var recommended = new List<RecommendedMod>();
        if (el.TryGetProperty("recommended", out var rec) && rec.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rec.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (recommended.Count >= JoinSectionLimits.MaxRecommended)
                {
                    error = "join lists more than " + JoinSectionLimits.MaxRecommended + " recommended mods";
                    return false;
                }

                var mod = new RecommendedMod(Str(row, "id"), Str(row, "package"), Str(row, "version"), Str(row, "reason"));
                if (!RecommendedFits(mod, out error)) return false;
                if (mod.Id.Length > 0 || mod.Package.Length > 0) recommended.Add(mod);
            }
        }

        string zoneId = "";
        int offset = 0;
        if (el.TryGetProperty("zone", out var z) && z.ValueKind == JsonValueKind.Object)
        {
            zoneId = Str(z, "id");
            if (zoneId.Length > JoinSectionLimits.MaxZoneIdChars) zoneId = "";
            if (z.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.Number && off.TryGetInt32(out int o)) offset = o;
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!ReadMap(el, "names", JoinSectionLimits.MaxNameChars, names, out error)) return false;
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!ReadMap(el, "versions", JoinSectionLimits.MaxVersionChars, versions, out error)) return false;

        if (!Enum.TryParse(Str(el, "showMessage"), ignoreCase: true, out MessageFrequency showMessage)) showMessage = MessageFrequency.WhenChanged;
        if (!Enum.TryParse(Str(el, "showMods"), ignoreCase: true, out ModsPageFrequency showMods)) showMods = ModsPageFrequency.WhenNeeded;
        var requiredClient = new List<ClientModRow>();
        if (!ReadClientRows(el, "requiredClient", withReason: true, JoinSectionLimits.MaxRequiredClient, requiredClient, out error)) return false;
        var approved = new List<ClientModRow>();
        if (!ReadClientRows(el, "approved", withReason: false, JoinSectionLimits.MaxApproved, approved, out error)) return false;
        var policyMode = ClientPolicyMode.Any;
        string modeWord = Str(el, "policyMode");
        if (modeWord.Length > 0 && !ClientModRules.TryParseMode(modeWord, out policyMode))
        {
            error = "join policyMode \"" + modeWord + "\" is not one this client reads";
            return false;
        }

        // CLIENTPOLICY2: the server's run list. A list over its cap, or an id that is not plain, drops the section; the
        // signature is checked over the canonical form rebuilt from it, so a reordered list fails verification.
        var runs = new List<string>();
        if (el.TryGetProperty("runs", out var runsEl) && runsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in runsEl.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.String) continue;
                string id = v.GetString() ?? "";
                if (runs.Count >= JoinSectionLimits.MaxServerRuns || !ClientModJudge.IsPlainId(id))
                {
                    error = "join runs are over their caps";
                    return false;
                }

                runs.Add(id);
            }
        }

        section = new JoinSection(message, reset, recommended, new JoinServerZone(zoneId, offset), issued, fp, schema, showMessage, showMods, names, versions)
            .WithPolicy(requiredClient, policyMode, approved, runs);
        if (ModManifestCrypto.Utf8(Canonical(section)).Length > JoinSectionLimits.MaxSectionBytes)
        {
            section = null;
            error = "join section is larger than " + JoinSectionLimits.MaxSectionBytes + " bytes";
            return false;
        }

        return true;
    }

    /// <summary>One recommended row within its caps; the operator overlay and the parser share it.</summary>
    internal static bool RecommendedFits(RecommendedMod mod, out string? error)
    {
        error = null;
        if (mod.Id.Length > JoinSectionLimits.MaxIdChars) error = "recommended id is longer than " + JoinSectionLimits.MaxIdChars + " characters";
        else if (mod.Package.Length > JoinSectionLimits.MaxPackageChars) error = "recommended package is too long";
        else if (mod.Version.Length > JoinSectionLimits.MaxVersionChars) error = "recommended version is too long";
        else if (mod.Reason.Length > JoinSectionLimits.MaxReasonChars) error = "recommended reason is longer than " + JoinSectionLimits.MaxReasonChars + " characters";
        return error == null;
    }

    /// <summary>
    /// The operator overlay's <c>recommended</c> rows, trimmed to the caps: a row over a cap is skipped with a
    /// reason, and rows past <see cref="JoinSectionLimits.MaxRecommended"/> are skipped.
    /// </summary>
    internal static List<RecommendedMod> Clean(IReadOnlyList<RecommendedMod>? rows, List<string>? skipped = null)
    {
        var list = new List<RecommendedMod>();
        if (rows == null) return list;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null || (row.Id.Length == 0 && row.Package.Length == 0)) continue;
            if (list.Count >= JoinSectionLimits.MaxRecommended)
            {
                skipped?.Add((row.Id.Length > 0 ? row.Id : row.Package) + ": more than " + JoinSectionLimits.MaxRecommended + " recommended mods");
                continue;
            }

            if (!RecommendedFits(row, out var why))
            {
                skipped?.Add((row.Id.Length > 0 ? row.Id : row.Package) + ": " + why);
                continue;
            }

            list.Add(row);
        }

        return list;
    }

    private static void Key(StringBuilder sb, string key, bool first)
    {
        if (!first) sb.Append(',');
        ModManifestJson.WriteString(sb, key);
        sb.Append(':');
    }

    private static string Str(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return "";
        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    }
}
