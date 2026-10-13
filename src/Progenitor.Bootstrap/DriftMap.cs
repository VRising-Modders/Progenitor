using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Progenitor.Bootstrap;

/// <summary>What kind of drift a rule absorbs (research compat-rewriter.md §2.2).</summary>
public enum DriftKind
{
    /// <summary>A type kept its shape and changed its name inside the same assembly.</summary>
    TypeRename = 0,

    /// <summary>A type kept its full name and moved to another interop assembly.</summary>
    TypeMove = 1,

    /// <summary>A member kept its declaring type and signature and changed its name.</summary>
    MemberRename = 2,

    /// <summary>A field became a property: <c>ldfld</c> becomes <c>call get_X</c>.</summary>
    FieldToProperty = 3,

    /// <summary>A property became a field: <c>call get_X</c> becomes <c>ldfld</c>.</summary>
    PropertyToField = 4,

    /// <summary>
    /// A method gained a trailing parameter. Never generated (§2.2): only a human can decide
    /// which constant preserves the old behavior. Parsed and reported; not applied by this cut.
    /// </summary>
    AddParam = 5,
}

/// <summary>How sure the generator (or the author) is. Only Exact and High are ever applied.</summary>
public enum DriftConfidence
{
    /// <summary>Full name, member name and decoded signature all unchanged; only the assembly moved.</summary>
    Exact = 0,

    /// <summary>One-to-one inside one declaring type, same decoded signature and kind.</summary>
    High = 1,

    /// <summary>Needed a name-similarity or ordering guess. Emitted, labeled, never applied.</summary>
    Heuristic = 2,
}

/// <summary>Who wrote the rule. A hand rule always wins a key collision with a generated one.</summary>
public enum DriftSource
{
    Generated = 0,
    Hand = 1,
}

/// <summary>
/// One drifted member. <see cref="From"/> and <see cref="To"/> use the same
/// <c>Assembly|Type|Name|Signature</c> encoding <c>Progenitor.Resilience.MemberBinding.Key</c>
/// produces, so a rule and a resilience row join on string equality with no second identity
/// scheme (research compat-rewriter.md §2.2). A type rule leaves Name and Signature empty.
/// </summary>
public sealed class DriftRule
{
    public DriftRule(
        string from,
        string to,
        DriftKind kind,
        DriftConfidence confidence,
        DriftSource source,
        string note = "")
    {
        // Normalized here rather than at every call site so a rule parsed out of JSON, a rule
        // the generator wrote and a key the rewriter looks up all agree on one rendering of a
        // generic declaring type (R34 finding 10; see OpenTypeText).
        From = NormalizeKey(from);
        To = NormalizeKey(to);
        Kind = kind;
        Confidence = confidence;
        Source = source;
        Note = note ?? "";
        SplitKey(From, out var fa, out var ft, out var fn, out var fs);
        FromAssembly = fa; FromType = ft; FromName = fn; FromSignature = fs;
        SplitKey(To, out var ta, out var tt, out var tn, out var ts);
        ToAssembly = ta; ToType = tt; ToName = tn; ToSignature = ts;
    }

    public string From { get; }
    public string To { get; }
    public DriftKind Kind { get; }
    public DriftConfidence Confidence { get; }
    public DriftSource Source { get; }
    public string Note { get; }

    public string FromAssembly { get; }
    /// <summary>Full type name, nested types joined with <c>/</c>.</summary>
    public string FromType { get; }
    public string FromName { get; }
    public string FromSignature { get; }

    public string ToAssembly { get; }
    public string ToType { get; }
    public string ToName { get; }
    public string ToSignature { get; }

    /// <summary>True for a rule that retargets a TypeRef rather than a member operand.</summary>
    public bool IsTypeRule => Kind == DriftKind.TypeRename || Kind == DriftKind.TypeMove;

    /// <summary>
    /// Exact and High are applied; Heuristic is reported only. AddParam is parsed but never
    /// applied by this cut — an inserted argument is the one rule kind that can silently change
    /// behavior (§2.2), so it stays report-only until a real patch needs one.
    /// </summary>
    public bool IsApplicable => Kind != DriftKind.AddParam && Confidence != DriftConfidence.Heuristic;

    /// <summary>One report line: <c>memberRename ProjectM!ProjectM.Foo.Bar -> Baz (high)</c>.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(KindText(Kind)).Append(' ');
        sb.Append(FromAssembly).Append('!').Append(FromType);
        if (FromName.Length > 0) sb.Append('.').Append(FromName);
        sb.Append(" -> ");
        if (Kind == DriftKind.TypeMove) sb.Append(ToAssembly);
        else if (IsTypeRule) sb.Append(ToAssembly).Append('!').Append(ToType);
        else sb.Append(ToName.Length > 0 ? ToName : ToType);
        sb.Append(" (").Append(ConfidenceText(Confidence));
        if (Source == DriftSource.Hand) sb.Append(", hand");
        sb.Append(')');
        return sb.ToString();
    }

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["from"] = From,
            ["to"] = To,
            ["kind"] = KindText(Kind),
            ["confidence"] = ConfidenceText(Confidence),
            ["source"] = Source == DriftSource.Hand ? "hand" : "generated",
        };
        if (Note.Length > 0) obj["note"] = Note;
        return obj;
    }

    public static string KindText(DriftKind kind) => kind switch
    {
        DriftKind.TypeRename => "typeRename",
        DriftKind.TypeMove => "typeMove",
        DriftKind.MemberRename => "memberRename",
        DriftKind.FieldToProperty => "fieldToProperty",
        DriftKind.PropertyToField => "propertyToField",
        DriftKind.AddParam => "addParam",
        _ => kind.ToString(),
    };

    public static string ConfidenceText(DriftConfidence c) => c switch
    {
        DriftConfidence.Exact => "exact",
        DriftConfidence.High => "high",
        DriftConfidence.Heuristic => "heuristic",
        _ => c.ToString(),
    };

    public static bool TryParseKind(string? text, out DriftKind kind)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "typerename": kind = DriftKind.TypeRename; return true;
            case "typemove": kind = DriftKind.TypeMove; return true;
            case "memberrename": kind = DriftKind.MemberRename; return true;
            case "fieldtoproperty": kind = DriftKind.FieldToProperty; return true;
            case "propertytofield": kind = DriftKind.PropertyToField; return true;
            case "addparam": kind = DriftKind.AddParam; return true;
            default: kind = DriftKind.MemberRename; return false;
        }
    }

    public static bool TryParseConfidence(string? text, out DriftConfidence confidence)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "exact": confidence = DriftConfidence.Exact; return true;
            case "high": confidence = DriftConfidence.High; return true;
            case "heuristic": confidence = DriftConfidence.Heuristic; return true;
            default: confidence = DriftConfidence.Heuristic; return false;
        }
    }

    /// <summary>The <c>Assembly|Type|Name|Signature</c> key for a member.</summary>
    public static string Key(string assembly, string type, string name, string signature) =>
        (assembly ?? "") + "|" + OpenTypeText(type) + "|" + (name ?? "") + "|" + (signature ?? "");

    /// <summary>
    /// The declaring-type half of a key, rendered the ONE way both sides render it: a generic
    /// instantiation reduced to the open type it instantiates, so
    /// <c>Unity.Collections.NativeArray`1&lt;ProjectM.Entity&gt;</c> and
    /// <c>Unity.Collections.NativeArray`1</c> are the same key.
    ///
    /// R34 finding 10: <c>MetadataScan</c> reduces a <c>TypeSpec</c> parent to the <c>TypeRef</c>
    /// it instantiates, Cecil hands the rewriter the instantiated form, and <c>_byFrom</c> is
    /// ordinal — so without one shared rule the resilience report says <c>fixable by rewrite</c>
    /// about a site the rewriter can never find. This is that shared rule, and it is game-free
    /// string work so both the Cecil side and the System.Reflection.Metadata side can call it.
    ///
    /// Only balanced <c>&lt;...&gt;</c> groups are dropped, so a nested name survives
    /// (<c>G`1&lt;A&gt;/N</c> becomes <c>G`1/N</c>). Never applied to a <b>signature</b>: both
    /// sides render signature types instantiated, and that is the identity a rule needs.
    /// </summary>
    public static string OpenTypeText(string? type)
    {
        string text = type ?? "";
        if (text.IndexOf('<') < 0) return text;
        var sb = new StringBuilder(text.Length);
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<') { depth++; continue; }
            if (c == '>') { if (depth > 0) depth--; continue; }
            if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Re-render a whole key through <see cref="OpenTypeText"/>; malformed keys pass through.</summary>
    private static string NormalizeKey(string? key)
    {
        string text = key ?? "";
        if (text.IndexOf('<') < 0) return text;
        var parts = text.Split('|');
        return parts.Length == 4 ? Key(parts[0], parts[1], parts[2], parts[3]) : text;
    }

    /// <summary>The key for a type row (empty name and signature).</summary>
    public static string TypeKey(string assembly, string type) => Key(assembly, type, "", "");

    /// <summary>
    /// Split a key back into its four parts. A key with the wrong field count yields empties,
    /// which every caller then rejects — a malformed rule must not half-apply.
    /// </summary>
    public static void SplitKey(string? key, out string assembly, out string type, out string name, out string signature)
    {
        assembly = ""; type = ""; name = ""; signature = "";
        if (string.IsNullOrEmpty(key)) return;
        var parts = key!.Split('|');
        if (parts.Length != 4) return;
        assembly = parts[0];
        type = parts[1];
        name = parts[2];
        signature = parts[3];
    }
}

/// <summary>
/// A parsed <c>driftmap-{build}.json</c>: the generated block, the hand-written block, and the
/// side/build the file claims. Pure data — no file system, no clock. Loading it is
/// <see cref="DriftMapStore"/>'s job, and the rules that decide where a map may come from live
/// there (research compat-rewriter.md §4.5).
/// </summary>
public sealed class DriftMap
{
    private readonly Dictionary<string, DriftRule> _byFrom = new(StringComparer.Ordinal);
    private readonly List<DriftRule> _rules = new();

    private DriftMap(string side, string build)
    {
        Side = side ?? "";
        Build = build ?? "";
    }

    public string Side { get; }
    public string Build { get; }

    /// <summary>Generated rules first, then hand rules. Hand rules already won any collision.</summary>
    public IReadOnlyList<DriftRule> Rules => _rules;

    public int GeneratedCount { get; private set; }
    public int HandCount { get; private set; }

    /// <summary>Where the map was read from, for the log line. Set by <see cref="DriftMapStore"/>.</summary>
    public string Origin { get; set; } = "";

    /// <summary>
    /// Lowercase-hex SHA-256 of the map file's bytes, set by <see cref="DriftMapStore"/> and ""
    /// for a map built in memory. This is the identity the operator's allowlist gates on (job
    /// FCOMPAT3, research compat-rewriter.md §4.5): a map is an arbitrary-call-redirection
    /// primitive, so it is held to the same "these exact bytes" rule as a plugin DLL, and it is
    /// logged on every load so the log says which map produced a rewrite.
    /// </summary>
    public string Sha256 { get; set; } = "";

    /// <summary>The rule for a member/type key, or null.</summary>
    public DriftRule? Find(string key) =>
        key != null && _byFrom.TryGetValue(key, out var rule) ? rule : null;

    /// <summary>True when at least one rule is Exact/High and not addParam.</summary>
    public bool HasApplicableRules
    {
        get
        {
            for (int i = 0; i < _rules.Count; i++)
                if (_rules[i].IsApplicable) return true;
            return false;
        }
    }

    /// <summary>Build a map in memory. Used by tools/driftmap and by the tests.</summary>
    public static DriftMap Create(string side, string build, IEnumerable<DriftRule>? rules)
    {
        var map = new DriftMap(side, build);
        if (rules != null)
            foreach (var rule in rules)
                map.Add(rule, out _);
        return map;
    }

    private bool Add(DriftRule rule, out string refusal)
    {
        refusal = "";
        if (rule == null) { refusal = "null rule"; return false; }
        if (rule.From.Length == 0 || rule.To.Length == 0)
        {
            refusal = "rule with an empty from/to key";
            return false;
        }
        if (rule.FromAssembly.Length == 0 || rule.ToAssembly.Length == 0)
        {
            refusal = "rule key is not Assembly|Type|Name|Signature: " + rule.From;
            return false;
        }
        // §4.7: the remit is game-surface drift. A rule that redirects into System.* or into a
        // plugin is out of remit by definition and is refused at parse time, not at apply time.
        if (!IsGameAssemblyName(rule.FromAssembly) || !IsGameAssemblyName(rule.ToAssembly))
        {
            refusal = "rule names a non-game assembly (" + rule.FromAssembly + " -> " + rule.ToAssembly + ")";
            return false;
        }
        if (_byFrom.TryGetValue(rule.From, out var existing))
        {
            // Hand rules are merged last and always win; a generated rule never overwrites one.
            if (existing.Source == DriftSource.Hand && rule.Source != DriftSource.Hand)
            {
                refusal = "generated rule shadowed by a hand rule: " + rule.From;
                return false;
            }
            _rules.Remove(existing);
            if (existing.Source == DriftSource.Hand) HandCount--; else GeneratedCount--;
        }
        _byFrom[rule.From] = rule;
        _rules.Add(rule);
        if (rule.Source == DriftSource.Hand) HandCount++; else GeneratedCount++;
        return true;
    }

    /// <summary>
    /// Assembly-name roots treated as "the game". <c>UnityEngine</c> is listed in its own right
    /// because the match below is <b>not</b> a prefix test.
    /// </summary>
    public static readonly string[] GameAssemblyPrefixes = { "ProjectM", "Unity", "UnityEngine", "Stunlock" };

    /// <summary>
    /// True when <paramref name="name"/> is one of <see cref="GameAssemblyPrefixes"/> exactly, or
    /// a dotted child of one (<c>ProjectM.Shared</c>, <c>Unity.Entities</c>).
    ///
    /// R33 finding 2: this used to be <c>StartsWith</c>, so <c>ProjectMBackdoor</c>,
    /// <c>UnityHax</c> and friends passed the one check §4.7 says keeps the rewriter inside its
    /// remit. The boundary is the name <b>segment</b> now: a prefix that runs on into other
    /// letters is not the game. That alone is not the whole defense and is not claimed to be —
    /// a dotted child is still an unbounded namespace — which is why
    /// <c>PluginRewriter.ScopeFor</c> additionally refuses to aim a plugin at any assembly the
    /// module did not already reference and the install does not already have in its interop
    /// folder. The name test decides what may be written in a map; that one decides what may
    /// actually be pointed at.
    /// </summary>
    public static bool IsGameAssemblyName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i < GameAssemblyPrefixes.Length; i++)
        {
            string root = GameAssemblyPrefixes[i];
            if (name!.Length < root.Length) continue;
            if (!name.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Length == root.Length) return true;
            if (name[root.Length] == '.') return true;
        }
        return false;
    }

    public string ToJson()
    {
        var generated = new JsonArray();
        var hand = new JsonArray();
        foreach (var rule in _rules)
            (rule.Source == DriftSource.Hand ? hand : generated).Add(rule.ToJson());
        var root = new JsonObject
        {
            ["side"] = Side,
            ["build"] = Build,
            ["generated"] = generated,
            ["hand"] = hand,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Parse a map. Never throws: a malformed file yields null and a reason, and the load path
    /// then loads the original bytes (§2.6). Individual malformed rules are dropped with a
    /// reason in <paramref name="refusals"/> rather than failing the whole file, so one bad
    /// hand rule cannot disable the rest of the map.
    /// </summary>
    public static DriftMap? Parse(string? json, out string error, out IReadOnlyList<string> refusals)
    {
        error = "";
        var dropped = new List<string>();
        refusals = dropped;
        if (string.IsNullOrWhiteSpace(json)) { error = "empty map"; return null; }
        JsonNode? root;
        try { root = JsonNode.Parse(json!); }
        catch (Exception e) { error = "not JSON: " + e.Message; return null; }
        if (root is not JsonObject obj) { error = "root is not a JSON object"; return null; }

        string side = ReadString(obj, "side");
        string build = ReadString(obj, "build");
        if (side.Length == 0 || build.Length == 0)
        {
            error = "map has no side/build";
            return null;
        }
        var map = new DriftMap(side, build);
        // Generated first, hand last, so a hand rule wins the collision (§2.2).
        ReadBlock(obj, "generated", DriftSource.Generated, map, dropped);
        ReadBlock(obj, "hand", DriftSource.Hand, map, dropped);
        return map;
    }

    private static void ReadBlock(JsonObject obj, string name, DriftSource source, DriftMap map, List<string> dropped)
    {
        if (obj[name] is not JsonArray array) return;
        foreach (var node in array)
        {
            if (node is not JsonObject entry) { dropped.Add(name + ": entry is not an object"); continue; }
            string from = ReadString(entry, "from");
            string to = ReadString(entry, "to");
            if (!DriftRule.TryParseKind(ReadString(entry, "kind"), out var kind))
            {
                dropped.Add(name + ": unknown kind '" + ReadString(entry, "kind") + "' for " + from);
                continue;
            }
            if (!DriftRule.TryParseConfidence(ReadString(entry, "confidence"), out var confidence))
            {
                dropped.Add(name + ": unknown confidence for " + from);
                continue;
            }
            var rule = new DriftRule(from, to, kind, confidence, source, ReadString(entry, "note"));
            if (!map.Add(rule, out var refusal)) dropped.Add(name + ": " + refusal);
        }
    }

    private static string ReadString(JsonObject obj, string name)
    {
        if (obj[name] is JsonValue value && value.TryGetValue<string>(out var s)) return s ?? "";
        return "";
    }
}

/// <summary>
/// Where a drift map may come from. A map that says "every call to A.B now goes to C.D" is an
/// arbitrary-call-redirection primitive, so it takes AddressLibrary's loading rule with no
/// relaxations (research compat-rewriter.md §4.5): the exact <c>driftmap-{build}.json</c> beside
/// Core, next to the address tables. No PROGENITOR_* search path, no <c>BepInEx/plugins</c>, no
/// game-root walk; and after parse the file's own side/build must equal the running process, so
/// a file whose name matches but whose fields do not is refused.
/// </summary>
public static class DriftMapStore
{
    public const string FilePrefix = "driftmap-";
    public const string FileExtension = ".json";
    private const string PluginsFolderName = "plugins";

    /// <summary><c>driftmap-{build}.json</c>.</summary>
    public static string FileName(string build) => FilePrefix + (build ?? "") + FileExtension;

    /// <summary>Refuse a folder that is BepInEx/plugins — the same refusal AddressLibrary makes.</summary>
    public static bool IsPluginsDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var name = Path.GetFileName(path!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(name, PluginsFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Load the map for <paramref name="build"/> from <paramref name="folder"/>. Returns null
    /// with a status line for every failure — absent, unreadable, malformed, or labeled for
    /// another build/side. Never throws.
    /// </summary>
    public static DriftMap? TryLoad(string? folder, string side, string build, out string status)
    {
        status = "";
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(build))
        {
            status = "no core folder or build id";
            return null;
        }
        if (IsPluginsDirectory(folder))
        {
            status = "refused: a drift map is never read from BepInEx/plugins";
            return null;
        }
        string path = Path.Combine(folder!, FileName(build));
        string text;
        string sha;
        try
        {
            if (!File.Exists(path)) { status = "no map for build " + build; return null; }
            // Read the bytes once and hash exactly those bytes: the identity the allowlist gate
            // checks must be the identity that was parsed, not a second read of the file.
            byte[] raw = File.ReadAllBytes(path);
            sha = Sha256Hex(raw);
            text = new UTF8Encoding(false, false).GetString(StripBom(raw));
        }
        catch (Exception e)
        {
            status = "map " + FileName(build) + " unreadable: " + e.Message;
            return null;
        }
        var map = DriftMap.Parse(text, out var error, out var refusals);
        if (map == null)
        {
            status = "map " + FileName(build) + " is malformed: " + error;
            return null;
        }
        if (!string.Equals(map.Side, side, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(map.Build, build, StringComparison.Ordinal))
        {
            status = "map " + FileName(build) + " is labeled " + map.Side + "-" + map.Build
                     + "; this process is " + side + "-" + build + "; refused";
            return null;
        }
        map.Origin = FileName(build);
        map.Sha256 = sha;
        status = FormatMapLine(map, refusals.Count);
        return map;
    }

    /// <summary>Lowercase-hex SHA-256, the same encoding every Progenitor load line prints.</summary>
    public static string Sha256Hex(byte[]? bytes)
    {
        if (bytes == null) return "";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>First 16 hex characters, the short form every load line prints.</summary>
    public static string ShortSha(string? sha) =>
        string.IsNullOrEmpty(sha) ? "" : (sha!.Length <= 16 ? sha : sha.Substring(0, 16));

    private static byte[] StripBom(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            var trimmed = new byte[raw.Length - 3];
            Buffer.BlockCopy(raw, 3, trimmed, 0, trimmed.Length);
            return trimmed;
        }
        return raw;
    }

    /// <summary>
    /// <c>driftmap-{build}.json sha256 4f2a… (12 generated, 3 hand)</c>, plus dropped rules when
    /// any. The hash is on every map line so the log names the exact bytes that drove a rewrite.
    /// </summary>
    public static string FormatMapLine(DriftMap map, int droppedRules)
    {
        var sb = new StringBuilder();
        sb.Append(map.Origin.Length > 0 ? map.Origin : FileName(map.Build));
        if (map.Sha256.Length > 0) sb.Append(" sha256 ").Append(ShortSha(map.Sha256));
        sb.Append(" (").Append(map.GeneratedCount.ToString(CultureInfo.InvariantCulture)).Append(" generated, ");
        sb.Append(map.HandCount.ToString(CultureInfo.InvariantCulture)).Append(" hand");
        if (droppedRules > 0) sb.Append(", ").Append(droppedRules.ToString(CultureInfo.InvariantCulture)).Append(" refused");
        sb.Append(')');
        return sb.ToString();
    }
}

/// <summary>
/// <c>[Compat] RewriteMode</c>. Off computes nothing; DryRun runs the whole analysis and loads
/// the original bytes; Apply loads the rewritten image. Default is <see cref="RewriteMode.DryRun"/>:
/// the operator sees what would be rewritten, and their allowlist still vets exactly what runs.
/// </summary>
public enum RewriteMode
{
    /// <summary>The pass computes nothing and returns the input array by reference.</summary>
    Off = 0,

    /// <summary>Full analysis, full report, original bytes loaded. The default.</summary>
    DryRun = 1,

    /// <summary>The rewritten image is what reaches LoadFromStream.</summary>
    Apply = 2,
}

/// <summary>Parsing and log text for <see cref="RewriteMode"/>. Game-free; mirrored into Bootstrap.</summary>
public static class CompatRewritePolicy
{
    public const string ConfigSection = "Compat";
    public const string ConfigKey = "RewriteMode";
    public const RewriteMode DefaultMode = RewriteMode.DryRun;

    /// <summary>
    /// The four tokens <c>ReloadAllowPolicy.Token</c> prints. The rewriter cannot name
    /// <c>AllowDecision</c>: the allowlist lives in <c>Progenitor.Reload</c> on Core's side and
    /// in <c>Progenitor.Bootstrap</c> on the other, and a <c>using</c> for either would make the
    /// two copies of this file differ. The reloader hands the gate over as a
    /// <c>Func&lt;string,string,string&gt;</c> returning one of these instead, which keeps the
    /// mirror byte-identical and lets the tests drive the gate with no allowlist on disk.
    /// </summary>
    public const string AllowTokenNoList = "none";

    /// <summary>The (sha256, filename) pair is on the allowlist.</summary>
    public const string AllowTokenListed = "ok";

    /// <summary>
    /// The operator's allowlist file, named in the refusal line so "the map is not vetted" is
    /// actionable rather than cryptic. Kept as a literal because the mirror may not reference
    /// <c>ReloadAllowPolicy.FileName</c> (it lives in a different namespace on each side, which
    /// is what the mirror forbids); <c>CompatRewriterTests</c> asserts the two strings are equal.
    /// </summary>
    public const string AllowListFileName = "progenitor-allow.txt";

    public const string ConfigDescription =
        "Off | DryRun | Apply. What the compatibility rewriter does with a plugin image when a " +
        "drift map for the running game build sits next to the address tables. Off computes " +
        "nothing. DryRun (default) runs the whole analysis, logs what would be rewritten, and " +
        "loads the ORIGINAL bytes. Apply loads the rewritten image - note that the operator's " +
        "allowlist then vets the input to a transformation rather than the code that runs, which " +
        "is why it is not the default. Every failure loads the original bytes.";

    // Written on the install thread and read from the load path, so both are volatile (R34
    // finding 12). An enum with an int base is a legal volatile field.
    private static volatile RewriteMode _mode = DefaultMode;
    private static volatile Func<string>? _reader;

    /// <summary>Host/test seam: when set, the mode is read through this on every load.</summary>
    public static Func<string>? ModeReader
    {
        get => _reader;
        set => _reader = value;
    }

    /// <summary>The mode in force. Falls back to <see cref="DefaultMode"/> on anything unparseable.</summary>
    public static RewriteMode Mode
    {
        get
        {
            var reader = _reader;
            if (reader == null) return _mode;
            try { return Parse(reader()); }
            catch { return DefaultMode; }
        }
        set => _mode = value;
    }

    /// <summary>
    /// Latch the value <c>Plugin.Install</c> bound through <c>Config.Bind</c> for this Core
    /// generation (job FCOMPAT3). Read <b>once</b>, at install, rather than per plugin load: the
    /// mode decides whether a plugin's IL is transformed, and a value that could change halfway
    /// through a generation would mean two plugins in one process disagreed about what ran.
    /// The latch replaces <see cref="ModeReader"/> with a reader that answers the adopted value
    /// and nothing else. R34 finding 12: <b>clearing</b> the reader used to be the latch, and it
    /// took the mode off the load path entirely — <c>CompatRewriteHost.RewriteForLoad</c> only
    /// consults <see cref="Mode"/> when a reader is set, so every plugin load fell back to
    /// re-reading the .cfg from disk and the bound value was dead. Installing a constant reader
    /// keeps the seam the load path looks at and makes the latch the thing it finds there.
    /// </summary>
    public static RewriteMode Adopt(string? text)
    {
        var mode = Parse(text);
        string latched = Text(mode);
        _mode = mode;
        _reader = () => latched;
        return mode;
    }

    /// <summary>Drop the latch and the seam. Tests, and a Core uninstall.</summary>
    public static void ResetMode()
    {
        _reader = null;
        _mode = DefaultMode;
    }

    public static RewriteMode Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "off" => RewriteMode.Off,
        "dryrun" => RewriteMode.DryRun,
        "dry-run" => RewriteMode.DryRun,
        "apply" => RewriteMode.Apply,
        _ => DefaultMode,
    };

    public static string Text(RewriteMode mode) => mode switch
    {
        RewriteMode.Off => "Off",
        RewriteMode.DryRun => "DryRun",
        RewriteMode.Apply => "Apply",
        _ => mode.ToString(),
    };
}
