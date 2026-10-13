using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Progenitor.Resilience;

/// <summary>
/// One managed assembly in the fingerprint: the identity that decides whether Core still
/// compiles and binds against it. Name, assembly version, byte length, SHA-256 of the whole
/// file. <see cref="ModifiedUtc"/> is *not* identity — it is only the fast-path skip in
/// <see cref="ResilienceCheck"/> (length + mtime unchanged means the bytes are almost
/// certainly unchanged, so 180 MB of interop is not re-hashed on every start). Any mismatch
/// falls through to the hash.
/// </summary>
public sealed class AssemblyFingerprint
{
    public AssemblyFingerprint(string name, string version, long length, string sha256, string modifiedUtc)
    {
        Name = name ?? "";
        Version = version ?? "";
        Length = length;
        Sha256 = sha256 ?? "";
        ModifiedUtc = modifiedUtc ?? "";
    }

    /// <summary>File name, e.g. <c>ProjectM.dll</c>. The dictionary key of the interop set.</summary>
    public string Name { get; }

    /// <summary>Assembly version from the metadata Assembly row, or "" when it could not be read.</summary>
    public string Version { get; }

    public long Length { get; }

    /// <summary>Lowercase hex SHA-256 of the whole file, or "" when it was not hashed (fast path).</summary>
    public string Sha256 { get; }

    /// <summary>Last write time, round-trip UTC. Not identity; see the type remarks.</summary>
    public string ModifiedUtc { get; }

    /// <summary>True when name, version, length and a non-empty hash all match.</summary>
    public bool SameAs(AssemblyFingerprint? other)
    {
        if (other == null) return false;
        if (!string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Version, other.Version, StringComparison.Ordinal)) return false;
        if (Length != other.Length) return false;
        if (Sha256.Length == 0 || other.Sha256.Length == 0) return true; // one side used the fast path
        return string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Human words for what differs, e.g. <c>size 1,024 -&gt; 2,048</c>. Empty when identical.</summary>
    public string DescribeChangeFrom(AssemblyFingerprint old)
    {
        var parts = new List<string>();
        if (old != null && !string.Equals(Version, old.Version, StringComparison.Ordinal))
            parts.Add("version " + Blank(old.Version) + " -> " + Blank(Version));
        if (old != null && Length != old.Length)
            parts.Add("size " + old.Length.ToString("N0", CultureInfo.InvariantCulture)
                      + " -> " + Length.ToString("N0", CultureInfo.InvariantCulture));
        if (old != null && old.Sha256.Length > 0 && Sha256.Length > 0
            && !string.Equals(Sha256, old.Sha256, StringComparison.OrdinalIgnoreCase))
            parts.Add("sha " + Short(old.Sha256) + " -> " + Short(Sha256));
        return string.Join(", ", parts);
    }

    private static string Blank(string s) => string.IsNullOrEmpty(s) ? "(none)" : s;

    private static string Short(string sha) => sha.Length <= 12 ? sha : sha.Substring(0, 12);

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["version"] = Version,
        ["length"] = Length,
        ["sha256"] = Sha256,
        ["modifiedUtc"] = ModifiedUtc,
    };

    public static AssemblyFingerprint FromJson(JsonObject obj) => new(
        JsonRead.String(obj, "name"),
        JsonRead.String(obj, "version"),
        JsonRead.Int64(obj, "length", 0),
        JsonRead.String(obj, "sha256"),
        JsonRead.String(obj, "modifiedUtc"));
}

/// <summary>
/// Everything Progenitor needs to know about "which V Rising is this" without a game process:
/// the address-library key (<c>{side}-{build}</c>, the same string
/// <c>tools/addresslib/{side}-{build}.json</c> is named after), the GameAssembly.dll identity,
/// the whole interop assembly set, whether an address table exists for this build and which
/// detour sites it covers, and the member-level binding list from <see cref="MetadataScan"/>.
///
/// Stored beside the address tables as <c>build-{side}-{build}.json</c>. A later run reads the
/// newest stored fingerprint and diffs. Nothing here loads a game type, so the whole file is
/// compiled into <c>Progenitor.Tests</c> and into <c>tools/resilience-report.ps1</c>'s runner.
/// </summary>
public sealed class BuildFingerprint
{
    public const string FileNamePrefix = "build-";
    public const string FileNameSuffix = ".json";

    /// <summary>Bumped when the stored shape changes in a way an older reader would misread.</summary>
    public const int CurrentVersion = 1;

    public BuildFingerprint(
        string side,
        string build,
        string capturedUtc,
        string coreVersion,
        string gameAssemblyPath,
        long gameAssemblyLength,
        string gameAssemblySha256,
        string interopDirectory,
        IReadOnlyList<AssemblyFingerprint> interop,
        bool addressTablePresent,
        IReadOnlyList<string> detourSitesInTable,
        IReadOnlyList<string> detourSitesMissing,
        IReadOnlyList<MemberBinding> members,
        string gameAssemblyModifiedUtc = "",
        string gameAssemblyHashedUtc = "",
        string interopArchive = "",
        int version = CurrentVersion,
        string coreMvid = "")
    {
        Side = side ?? "";
        Build = build ?? "";
        CapturedUtc = capturedUtc ?? "";
        CoreVersion = coreVersion ?? "";
        CoreMvid = coreMvid ?? "";
        GameAssemblyPath = gameAssemblyPath ?? "";
        GameAssemblyLength = gameAssemblyLength;
        GameAssemblySha256 = gameAssemblySha256 ?? "";
        InteropDirectory = interopDirectory ?? "";
        Interop = interop ?? Array.Empty<AssemblyFingerprint>();
        AddressTablePresent = addressTablePresent;
        DetourSitesInTable = detourSitesInTable ?? Array.Empty<string>();
        DetourSitesMissing = detourSitesMissing ?? Array.Empty<string>();
        Members = members ?? Array.Empty<MemberBinding>();
        GameAssemblyModifiedUtc = gameAssemblyModifiedUtc ?? "";
        GameAssemblyHashedUtc = gameAssemblyHashedUtc ?? "";
        InteropArchiveFolder = interopArchive ?? "";
        Version = version;
    }

    public int Version { get; }
    public string Side { get; }
    public string Build { get; }
    public string CapturedUtc { get; }
    public string CoreVersion { get; }

    /// <summary>
    /// <c>Progenitor.dll</c>'s module version id — the GUID the compiler stamps into every build
    /// — lowercase, no braces, or "" when the fingerprint predates this field or was captured by
    /// a caller that filled no identity at all.
    /// </summary>
    /// <remarks>
    /// <para><b>Which Core, not just which version</b> (job FUPD10, R57 finding 7, R56 finding 9).
    /// <see cref="CoreVersion"/> is the assembly version, and a developer-tree deploy never moves
    /// it — version bumps are a separate commit by project rule — so the report's <c>core</c> row
    /// could say the reference set had moved and name no Core at all. The MVID moves on every
    /// compilation, so it is the identity that distinguishes two Cores that share a version.</para>
    /// <para><b>One source, with the version</b> (job FUPD11, R58 finding 9). Both halves are
    /// read off the loaded Core assembly — <c>typeof(Resilience).Assembly</c>, its
    /// <c>GetName().Version</c> and its <c>ManifestModule.ModuleVersionId</c> — in
    /// <c>Resilience.DescribeEnvironment</c>, and arrive here on the options. FUPD10 read the
    /// MVID off <c>Progenitor.dll</c> on disk instead: genuinely free is the reading that needs
    /// no file at all, and a version off the running Core paired with an MVID off a file is an
    /// identity that can name two Cores — the in-game normal, since Bootstrap loads Core from
    /// bytes and the disk file is only ever found by a folder fallback. It was also "" on exactly
    /// the run it exists for, the one whose core folder could not be resolved.</para>
    /// </remarks>
    public string CoreMvid { get; }

    /// <summary>The two together, <c>0.1.0.0/a1b2c3d4</c>, or the version alone when there is no MVID.</summary>
    public string CoreIdentity => FormatCoreIdentity(CoreVersion, CoreMvid);

    /// <summary>
    /// How the <c>core</c> row names one Core: version, plus the first 8 hex digits of the MVID
    /// when there is one. Static so the report can format the baseline's identity too, which it
    /// holds as two strings rather than as a fingerprint.
    /// </summary>
    public static string FormatCoreIdentity(string version, string mvid)
    {
        var v = string.IsNullOrEmpty(version) ? "(no version)" : version;
        if (string.IsNullOrEmpty(mvid)) return v;
        return v + "/" + (mvid.Length <= 8 ? mvid : mvid.Substring(0, 8));
    }

    public string GameAssemblyPath { get; }
    public long GameAssemblyLength { get; }
    public string GameAssemblySha256 { get; }

    /// <summary>
    /// <c>GameAssembly.dll</c>'s last write time, round-trip UTC. Not identity — it is half of
    /// the startup fast path's reuse condition (R31 finding 5): the build id hashes only the
    /// first 64 KiB, so without an mtime guard an in-place patch that preserved the header and
    /// the size would reuse the stored full hash and make
    /// <see cref="ResilienceDiff.GameAssemblyChangedUnderSameBuildId"/> unreachable in the game.
    /// </summary>
    public string GameAssemblyModifiedUtc { get; }

    /// <summary>
    /// When <see cref="GameAssemblySha256"/> was last actually computed (round-trip UTC), "" for
    /// a fingerprint written before this field existed. The other half of the fast-path guard:
    /// the full hash is recomputed on a schedule
    /// (<see cref="ResilienceOptions.FullHashInterval"/>, 24 h by default) no matter what the
    /// length and mtime say, so a patch that also forged the mtime is still caught on the next day's
    /// start.
    /// </summary>
    public string GameAssemblyHashedUtc { get; }

    public string InteropDirectory { get; }

    /// <summary>
    /// The archive folder holding a byte copy of the interop set this fingerprint describes
    /// (<c>interop-{side}-{build}</c> beside the address tables), or "" when there is none.
    /// Job FUPD4: the fingerprint says <i>that</i> an assembly changed, and only the archive can
    /// ever say <i>what moved inside it</i> — so the fingerprint records which archive is its
    /// own, and an operator reading a stored fingerprint knows whether that build is still
    /// diffable at all.
    /// </summary>
    public string InteropArchiveFolder { get; }

    public IReadOnlyList<AssemblyFingerprint> Interop { get; }
    public bool AddressTablePresent { get; }
    public IReadOnlyList<string> DetourSitesInTable { get; }
    public IReadOnlyList<string> DetourSitesMissing { get; }
    public IReadOnlyList<MemberBinding> Members { get; }

    /// <summary>The address-library key: <c>client-247473480-6a147771-…</c>. Also the file name stem.</summary>
    public string Key => Side + "-" + Build;

    /// <summary><c>build-client-247473480-….json</c>.</summary>
    public string FileName => FileNameFor(Side, Build);

    public static string FileNameFor(string side, string build) =>
        FileNamePrefix + (side ?? "") + "-" + (build ?? "") + FileNameSuffix;

    /// <summary>Address-library table file name for the same build, for the "rebuild this" message.</summary>
    public string AddressTableFileName => (Side ?? "") + "-" + (Build ?? "") + ".json";

    /// <summary>
    /// The same fingerprint with <see cref="InteropArchiveFolder"/> set. Used once, by
    /// <c>ResilienceCheck.Run</c>, between the archive step and the store write: the archive is
    /// made first and the fingerprint that records it is written second, so a fingerprint on
    /// disk never claims an archive that is not there.
    /// </summary>
    public BuildFingerprint WithInteropArchive(string folder) => new(
        Side, Build, CapturedUtc, CoreVersion, GameAssemblyPath, GameAssemblyLength,
        GameAssemblySha256, InteropDirectory, Interop, AddressTablePresent, DetourSitesInTable,
        DetourSitesMissing, Members, GameAssemblyModifiedUtc, GameAssemblyHashedUtc,
        folder ?? "", Version, CoreMvid);

    public AssemblyFingerprint? FindInterop(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        for (int i = 0; i < Interop.Count; i++)
            if (string.Equals(Interop[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return Interop[i];
        return null;
    }

    public string ToJson()
    {
        var interop = new JsonArray();
        foreach (var a in Interop) interop.Add(a.ToJson());
        var members = new JsonArray();
        foreach (var m in Members) members.Add(m.ToJson());

        var obj = new JsonObject
        {
            ["version"] = Version,
            ["side"] = Side,
            ["build"] = Build,
            ["capturedUtc"] = CapturedUtc,
            ["coreVersion"] = CoreVersion,
            ["coreMvid"] = CoreMvid,
            ["gameAssembly"] = new JsonObject
            {
                ["path"] = GameAssemblyPath,
                ["length"] = GameAssemblyLength,
                ["sha256"] = GameAssemblySha256,
                ["modifiedUtc"] = GameAssemblyModifiedUtc,
                ["hashedUtc"] = GameAssemblyHashedUtc,
            },
            ["interopDirectory"] = InteropDirectory,
            ["interopArchive"] = InteropArchiveFolder,
            ["addressTablePresent"] = AddressTablePresent,
            ["detourSitesInTable"] = JsonRead.Array(DetourSitesInTable),
            ["detourSitesMissing"] = JsonRead.Array(DetourSitesMissing),
            ["interop"] = interop,
            ["members"] = members,
        };
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>
    /// Parse a stored fingerprint. Never throws: a truncated or hand-edited file is a
    /// "no baseline" report, not a load failure. Same rule as <c>AddressTable.TryParse</c>.
    /// </summary>
    public static bool TryParse(string json, out BuildFingerprint? fingerprint, out string? error)
    {
        fingerprint = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "JSON is empty."; return false; }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (Exception e) { error = "JSON did not parse: " + e.Message; return false; }

        if (root is not JsonObject obj) { error = "JSON root is not an object."; return false; }

        var game = JsonRead.Object(obj, "gameAssembly");
        var interop = new List<AssemblyFingerprint>();
        foreach (var node in JsonRead.Items(obj, "interop"))
            if (node is JsonObject o) interop.Add(AssemblyFingerprint.FromJson(o));
        var members = new List<MemberBinding>();
        foreach (var node in JsonRead.Items(obj, "members"))
            if (node is JsonObject o) members.Add(MemberBinding.FromJson(o));

        fingerprint = new BuildFingerprint(
            JsonRead.String(obj, "side"),
            JsonRead.String(obj, "build"),
            JsonRead.String(obj, "capturedUtc"),
            JsonRead.String(obj, "coreVersion"),
            game == null ? "" : JsonRead.String(game, "path"),
            game == null ? 0 : JsonRead.Int64(game, "length", 0),
            game == null ? "" : JsonRead.String(game, "sha256"),
            JsonRead.String(obj, "interopDirectory"),
            interop,
            JsonRead.Bool(obj, "addressTablePresent", false),
            JsonRead.Strings(obj, "detourSitesInTable"),
            JsonRead.Strings(obj, "detourSitesMissing"),
            members,
            game == null ? "" : JsonRead.String(game, "modifiedUtc"),
            game == null ? "" : JsonRead.String(game, "hashedUtc"),
            JsonRead.String(obj, "interopArchive"),
            (int)JsonRead.Int64(obj, "version", CurrentVersion),
            // Absent in a fingerprint stored before job FUPD10; "" reads as "this baseline
            // cannot say which Core", which the diff treats as "no evidence it moved" rather
            // than as a move.
            JsonRead.String(obj, "coreMvid"));
        return true;
    }

    /// <summary>Read a stored fingerprint from disk. A missing file is a false, never an exception.</summary>
    public static bool TryLoadFile(string path, out BuildFingerprint? fingerprint, out string? error)
    {
        fingerprint = null;
        error = null;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { error = "file not found: " + path; return false; }
            return TryParse(File.ReadAllText(path), out fingerprint, out error);
        }
        catch (Exception e) { error = "failed to read " + path + ": " + e.Message; return false; }
    }
}

/// <summary>Tolerant JsonNode readers shared by the fingerprint types. Missing fields are defaults.</summary>
internal static class JsonRead
{
    public static string String(JsonObject obj, string name)
    {
        if (obj == null || !obj.TryGetPropertyValue(name, out var node) || node is null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s ?? "";
        return node.ToString() ?? "";
    }

    public static long Int64(JsonObject obj, string name, long fallback)
    {
        if (obj == null || !obj.TryGetPropertyValue(name, out var node) || node is null) return fallback;
        if (node is JsonValue v && v.TryGetValue<long>(out var n)) return n;
        return fallback;
    }

    public static bool Bool(JsonObject obj, string name, bool fallback)
    {
        if (obj == null || !obj.TryGetPropertyValue(name, out var node) || node is null) return fallback;
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return fallback;
    }

    public static JsonObject? Object(JsonObject obj, string name)
    {
        if (obj == null || !obj.TryGetPropertyValue(name, out var node)) return null;
        return node as JsonObject;
    }

    public static IEnumerable<JsonNode?> Items(JsonObject obj, string name)
    {
        if (obj == null || !obj.TryGetPropertyValue(name, out var node) || node is not JsonArray array)
            yield break;
        foreach (var item in array) yield return item;
    }

    public static IReadOnlyList<string> Strings(JsonObject obj, string name)
    {
        var list = new List<string>();
        foreach (var node in Items(obj, name))
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                list.Add(s);
        }
        return list;
    }

    public static JsonArray Array(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        // JsonValue.Create(string), not the generic Add<T>: the generic path builds a
        // JsonValueCustomized<string> that needs a TypeInfoResolver and throws on write.
        foreach (var v in values) array.Add(JsonValue.Create(v));
        return array;
    }
}
