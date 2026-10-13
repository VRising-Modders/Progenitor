using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Progenitor.Install.Admission;

/// <summary>
/// One reviewed exact-image qualification: the only route to
/// <see cref="AdmissionDecision.ApplyNowEligible"/> in this increment. Keyed by the original
/// artifact sha256 and size, the canonical dependency closure, the game build, the Bootstrap and
/// Core contracts, the side and the operation. Every key must match exactly; a record never
/// covers "all future versions". Local reviewed policy only: a record shipped inside a package
/// is ignored (<see cref="AdmissionReasonCode.EmbeddedQualificationIgnored"/>).
/// </summary>
internal sealed record QualificationRecord(
    string Id,
    string PluginId,
    ArtifactIdentity Artifact,
    DependencyClosure Closure,
    string GameBuild,
    string BootstrapContract,
    string CoreContract,
    PackageSide Side,
    AdmissionOperation Operation,
    InstallTarget Target,
    string QualifiedUtc,
    string Evidence)
{
    /// <summary>
    /// Compares every key against a request. Returns the names of the keys that differ, empty when
    /// the record covers the request. <c>pluginId</c> is checked first so a different plugin never
    /// reads as a "mismatch" of this record.
    /// </summary>
    public IReadOnlyList<string> Mismatches(AdmissionRequest request)
    {
        var rows = new List<string>();
        if (!string.Equals(PluginId, request.Declaration.PluginId, StringComparison.OrdinalIgnoreCase)) rows.Add("pluginId");
        if (!Artifact.Matches(request.Artifact)) rows.Add("artifactSha256");
        if (!Closure.SameAs(request.Closure)) rows.Add("dependencyClosure");
        if (!SameIdentity(GameBuild, request.Identity(CompatibilityKeys.GameBuild))) rows.Add(CompatibilityKeys.GameBuild);
        if (!SameIdentity(BootstrapContract, request.Identity(CompatibilityKeys.BootstrapContract))) rows.Add(CompatibilityKeys.BootstrapContract);
        if (!SameIdentity(CoreContract, request.Identity(CompatibilityKeys.CoreContract))) rows.Add(CompatibilityKeys.CoreContract);
        if (Side != request.Declaration.Side) rows.Add("side");
        if (Operation != request.Operation) rows.Add("operation");
        if (!TargetCovers(Target, request.Target)) rows.Add("target");
        return rows;
    }

    /// <summary>
    /// HOT4: does a record authored for <paramref name="recorded"/> cover a request for
    /// <paramref name="requested"/>? Exactly one widening: a record reviewed for
    /// <see cref="InstallTarget.Both"/> covers either half on its own, because the reviewer accepted
    /// these exact bytes on <em>both</em> processes and a per-half admission asks about one of them.
    /// Nothing narrows: a <c>Client</c> record never covers a local-server half, and a
    /// <c>LocalServer</c> record never covers the client — those stay a <c>target</c> mismatch.
    /// </summary>
    internal static bool TargetCovers(InstallTarget recorded, InstallTarget requested) =>
        recorded == requested || recorded == InstallTarget.Both;

    private static bool SameIdentity(string recorded, string? actual) =>
        !string.IsNullOrWhiteSpace(recorded)
        && !string.IsNullOrWhiteSpace(actual)
        && string.Equals(recorded.Trim(), actual!.Trim(), StringComparison.Ordinal);
}

/// <summary>
/// The local reviewed record set (<c>qualification.json</c> shape). Parsing fails closed: a
/// record missing any key, or with a hash that is not 64 hex characters, is dropped and named in
/// <see cref="Errors"/>, never admitted with a partial key.
/// </summary>
internal sealed class QualificationPolicy
{
    public const int SchemaVersion = 1;
    public const string FileName = "progenitor-qualification.json";

    public static QualificationPolicy Empty { get; } = new(Array.Empty<QualificationRecord>(), Array.Empty<string>(), "none");

    private QualificationPolicy(IReadOnlyList<QualificationRecord> records, IReadOnlyList<string> errors, string version)
    {
        Records = records;
        Errors = errors;
        Version = version;
    }

    public IReadOnlyList<QualificationRecord> Records { get; }
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Policy identity bound into an approval: schema plus record count plus the ids.</summary>
    public string Version { get; }

    /// <summary>Records for one plugin id, in file order.</summary>
    public IReadOnlyList<QualificationRecord> For(string pluginId)
    {
        var rows = new List<QualificationRecord>();
        for (int i = 0; i < Records.Count; i++)
            if (string.Equals(Records[i].PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) rows.Add(Records[i]);
        return rows;
    }

    /// <summary>
    /// Reads <c>BepInEx/config/progenitor-qualification.json</c> when present. A missing file is
    /// the empty policy; a malformed one is the empty policy with the error recorded.
    /// </summary>
    public static QualificationPolicy LoadLocal(string bepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) return Empty;
        string path = PathUnder(bepInExRoot);
        if (!File.Exists(path)) return Empty;
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return new QualificationPolicy(Array.Empty<QualificationRecord>(), new[] { "unreadable: " + e.Message }, "error");
        }
    }

    public static QualificationPolicy Parse(string json)
    {
        var records = new List<QualificationRecord>();
        var errors = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json ?? ""); }
        catch (JsonException e)
        {
            errors.Add("not valid JSON: " + e.Message);
            return new QualificationPolicy(records, errors, "error");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { errors.Add("root is not an object"); return new QualificationPolicy(records, errors, "error"); }
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != SchemaVersion)
            {
                errors.Add("schema is not " + SchemaVersion);
                return new QualificationPolicy(records, errors, "error");
            }
            if (!root.TryGetProperty("records", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                errors.Add("records is not an array");
                return new QualificationPolicy(records, errors, "error");
            }

            int index = 0;
            foreach (var row in rows.EnumerateArray())
            {
                index++;
                if (TryParseRecord(row, out var record, out var error)) records.Add(record!);
                else errors.Add("record " + index + ": " + error);
            }
        }

        var ids = new System.Text.StringBuilder("v" + SchemaVersion + "/" + records.Count);
        for (int i = 0; i < records.Count; i++) ids.Append('/').Append(records[i].Id);
        return new QualificationPolicy(records, errors, ids.ToString());
    }

    private static bool TryParseRecord(JsonElement row, out QualificationRecord? record, out string? error)
    {
        record = null;
        error = null;
        if (row.ValueKind != JsonValueKind.Object) { error = "not an object"; return false; }

        string id = Str(row, "id");
        string pluginId = Str(row, "pluginId");
        string sha = Str(row, "artifactSha256").ToLowerInvariant();
        long size = row.TryGetProperty("artifactSize", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number ? sizeEl.GetInt64() : -1;
        string gameBuild = Str(row, CompatibilityKeys.GameBuild);
        string bootstrap = Str(row, CompatibilityKeys.BootstrapContract);
        string core = Str(row, CompatibilityKeys.CoreContract);
        string sideText = Str(row, "side");
        string opText = Str(row, "operation");
        string targetText = Str(row, "target");
        string qualifiedUtc = Str(row, "qualifiedUtc");
        string evidence = Str(row, "evidence");

        if (id.Length == 0) { error = "missing id"; return false; }
        if (pluginId.Length == 0) { error = "missing pluginId"; return false; }
        if (!IsSha256(sha)) { error = "artifactSha256 is not 64 hex characters"; return false; }
        if (size <= 0) { error = "artifactSize must be a positive integer"; return false; }
        if (gameBuild.Length == 0 || bootstrap.Length == 0 || core.Length == 0) { error = "gameBuild, bootstrapContract and coreContract are required"; return false; }
        if (!TrySide(sideText, out var side)) { error = "side must be client, server or both"; return false; }
        if (!Enum.TryParse<AdmissionOperation>(opText, ignoreCase: true, out var op)) { error = "operation must be InitialInstall, Replace or Remove"; return false; }
        if (!Enum.TryParse<InstallTarget>(targetText, ignoreCase: true, out var target)) { error = "target must be Client, LocalServer or Both"; return false; }
        if (qualifiedUtc.Length == 0 || evidence.Length == 0) { error = "qualifiedUtc and evidence are required"; return false; }

        var closureRows = new List<KeyValuePair<string, string>>();
        if (!row.TryGetProperty("dependencyClosure", out var closure) || closure.ValueKind != JsonValueKind.Object)
        {
            error = "dependencyClosure must be an object of name -> sha256";
            return false;
        }
        foreach (var kv in closure.EnumerateObject())
        {
            string v = kv.Value.ValueKind == JsonValueKind.String ? (kv.Value.GetString() ?? "").ToLowerInvariant() : "";
            if (!IsSha256(v)) { error = "dependencyClosure." + kv.Name + " is not 64 hex characters"; return false; }
            closureRows.Add(new KeyValuePair<string, string>(kv.Name, v));
        }

        record = new QualificationRecord(
            id, pluginId, new ArtifactIdentity(sha, size), new DependencyClosure(closureRows),
            gameBuild, bootstrap, core, side, op, target, qualifiedUtc, evidence);
        return true;
    }

    private static string Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "").Trim() : "";

    internal static bool IsSha256(string s)
    {
        if (s == null || s.Length != 64) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex) return false;
        }
        return true;
    }

    private static bool TrySide(string text, out PackageSide side)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "client": side = PackageSide.Client; return true;
            case "server": side = PackageSide.Server; return true;
            case "both": side = PackageSide.Both; return true;
            default: side = PackageSide.Unknown; return false;
        }
    }

    /// <summary>The path <see cref="LoadLocal"/> reads, so a writer and a reader cannot disagree.</summary>
    public static string PathUnder(string bepInExRoot) =>
        Path.Combine(Path.GetFullPath(bepInExRoot), "config", FileName);

    /// <summary>
    /// The Sample package's record shape. The coordinator fills the hash, size, closure, build and
    /// contract values from the next reviewed build; until then the placeholder hash is empty, the
    /// parser drops the record, and nothing is qualified. Never copy this into a package: an
    /// embedded record is ignored by policy.
    /// </summary>
    public const string SamplePlaceholderJson = @"{
  ""schema"": 1,
  ""records"": [
    {
      ""id"": ""sample-initial-client"",
      ""pluginId"": ""example.sample"",
      ""artifactSha256"": """",
      ""artifactSize"": 0,
      ""dependencyClosure"": {},
      ""gameBuild"": """",
      ""bootstrapContract"": """",
      ""coreContract"": """",
      ""side"": ""client"",
      ""operation"": ""InitialInstall"",
      ""target"": ""Client"",
      ""qualifiedUtc"": """",
      ""evidence"": """"
    }
  ]
}";
}

/// <summary>
/// HOT2: <see cref="QualificationPolicy.LoadLocal"/> behind a stamp check, so the admission pipeline
/// sees a record the operator added without a Core reload, and so a catalog that is built once per
/// process does not pin the policy it happened to find at the first open.
///
/// <para>The stamp is the file's existence, last write time and length — the same three values
/// <c>ReloadAllowStore</c> re-reads its allowlist on. A file whose contents change without any of
/// those moving is not re-read; that is a deliberate trade against hashing the file on every plan,
/// and the coordinator's own <c>install_local qualify</c> writer always moves the length or the
/// stamp. A read that throws leaves the stamp unset so the next call retries a transient lock
/// rather than caching a failure.</para>
/// </summary>
internal static class QualificationPolicyCache
{
    private static readonly object Gate = new();
    private static string _root = "";
    private static string _stamp = "";
    private static bool _haveStamp;
    private static QualificationPolicy _current = QualificationPolicy.Empty;

    /// <summary>The policy for one BepInEx root, re-read when the file's stamp moved.</summary>
    public static QualificationPolicy Current(string? bepInExRoot)
    {
        string root = (bepInExRoot ?? "").Trim();
        if (root.Length == 0) return QualificationPolicy.Empty;

        string stamp;
        try
        {
            var info = new FileInfo(QualificationPolicy.PathUnder(root));
            stamp = info.Exists
                ? info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + info.Length
                : "absent";
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        {
            lock (Gate) _haveStamp = false;
            return QualificationPolicy.LoadLocal(root);
        }

        lock (Gate)
        {
            if (_haveStamp
                && string.Equals(_root, root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_stamp, stamp, StringComparison.Ordinal))
                return _current;
        }

        var policy = QualificationPolicy.LoadLocal(root);
        lock (Gate)
        {
            _root = root;
            _stamp = stamp;
            _haveStamp = true;
            _current = policy;
        }

        return policy;
    }

    /// <summary>Drop the cache (a test, or a writer that wants its own record seen at once).</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _haveStamp = false;
            _current = QualificationPolicy.Empty;
            _root = "";
            _stamp = "";
        }
    }
}
