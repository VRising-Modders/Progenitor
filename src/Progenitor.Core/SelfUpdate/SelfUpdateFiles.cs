using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Progenitor.SelfUpdate;

/// <summary>
/// AUTOUPDATE5 (M6.5): the files of Progenitor's own update, shared by Core (which downloads, keeps and stages a release)
/// and Bootstrap (which counts starts and restores the previous copy at start, before Core loads). File-only and game-free:
/// this source is compiled into both assemblies and the tests, so it must not reference either one.
/// <list type="bullet">
/// <item><c>BepInEx/progenitor-self-install.json</c>: the self-install record (which Progenitor release Progenitor
/// installed here, and the sha256 of its files). An install whose files match it is <b>managed</b>.</item>
/// <item><c>BepInEx/progenitor-versions/progenitor/&lt;version&gt;-&lt;sha8&gt;/</c>: kept releases (the version store for
/// Progenitor itself): <c>Progenitor.dll</c>, <c>Progenitor.Bootstrap.dll</c>, <c>addresslib/*.json</c> and
/// <c>release.json</c>.</item>
/// <item><c>BepInEx/progenitor-versions/progenitor/self-update.json</c>: the journal of the update in progress and the
/// versions that failed (never applied automatically again).</item>
/// </list>
/// </summary>
internal static class SelfPaths
{
    public const string StoreFolder = "progenitor-versions";
    public const string StoreSub = "progenitor";
    public const string StateFileName = "self-update.json";
    public const string RecordFileName = "progenitor-self-install.json";
    public const string KeptInfoName = "release.json";
    public const string CoreFolder = "progenitor-core";
    public const string CoreFile = "Progenitor.dll";
    public const string BootstrapFile = "Progenitor.Bootstrap.dll";
    public const string TablesFolder = "addresslib";
    public const string NextSuffix = ".next";

    public static string Store(string root) => Path.Combine(root, StoreFolder, StoreSub);
    public static string State(string root) => Path.Combine(Store(root), StateFileName);
    public static string Record(string root) => Path.Combine(root, RecordFileName);
    public static string CoreDll(string root) => Path.Combine(root, CoreFolder, CoreFile);
    public static string CoreNext(string root) => CoreDll(root) + NextSuffix;
    public static string Tables(string root) => Path.Combine(root, CoreFolder, TablesFolder);
    public static string DefaultBootstrap(string root) => Path.Combine(root, "plugins", BootstrapFile);

    /// <summary>A kept release's folder from its path relative to the BepInEx root (as the journal stores it).</summary>
    public static string Absolute(string root, string relative) =>
        Path.GetFullPath(Path.Combine(root, (relative ?? "").Replace('/', Path.DirectorySeparatorChar)));

    public static string Relative(string root, string absolute) =>
        Path.GetRelativePath(root, absolute).Replace('\\', '/');

    /// <summary>The store folder name of a release: <c>&lt;version&gt;-&lt;first 8 of the Core sha256&gt;</c>, safe characters only.</summary>
    public static string KeptDirName(string version, string coreSha)
    {
        var sb = new StringBuilder();
        foreach (char c in (version ?? "").Trim())
            sb.Append(c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '.' or '_' or '+' ? c : '_');
        if (sb.Length == 0) sb.Append("unknown");
        string sha = (coreSha ?? "").Trim().ToLowerInvariant();
        return sb + "-" + (sha.Length >= 8 ? sha.Substring(0, 8) : "nosha");
    }
}

/// <summary>AUTOUPDATE5: one version of Progenitor that failed (never applied automatically again).</summary>
internal sealed class SelfFailed
{
    public string Version { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime Utc { get; set; }
}

/// <summary>
/// AUTOUPDATE5: the update in progress. States: <c>staged</c> (a Bootstrap release waits for the next launch),
/// <c>swapping</c> (a Core hot swap was requested), <c>trial</c> (the new release runs; starts are counted until Core
/// marks it healthy), <c>ok</c>, <c>failed</c>, <c>rolledback</c> (the start-loop guard restored the previous copy),
/// <c>superseded</c> (another copy was deployed over it; nothing is judged).
/// </summary>
internal sealed class SelfJournal
{
    public const string Staged = "staged";
    public const string Swapping = "swapping";
    public const string Trial = "trial";
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string RolledBack = "rolledback";
    public const string Superseded = "superseded";

    public string State { get; set; } = "";
    public string Version { get; set; } = "";
    public string Previous { get; set; } = "";
    public string Package { get; set; } = "";

    /// <summary>The new and the previous release in the store, relative to the BepInEx root.</summary>
    public string NewDir { get; set; } = "";

    public string PrevDir { get; set; } = "";
    public string NewCore { get; set; } = "";

    /// <summary>Empty: the release keeps the running Bootstrap.</summary>
    public string NewBootstrap { get; set; } = "";

    public string PrevCore { get; set; } = "";
    public string PrevBootstrap { get; set; } = "";

    /// <summary><c>hot</c> (a Core swap) or <c>restart</c> (Bootstrap, at the next launch).</summary>
    public string Route { get; set; } = "";

    /// <summary>Starts of the new release that did not reach Core's healthy mark yet.</summary>
    public int Starts { get; set; }

    public DateTime Utc { get; set; }
    public string Note { get; set; } = "";

    public bool InProgress => State is Staged or Swapping or Trial;

    public Dictionary<string, object?> Describe() => new()
    {
        ["state"] = State,
        ["version"] = Version,
        ["previous"] = Previous,
        ["package"] = Package,
        ["route"] = Route,
        ["starts"] = Starts,
        ["newDir"] = NewDir,
        ["prevDir"] = PrevDir,
        ["newCore"] = NewCore,
        ["newBootstrap"] = NewBootstrap,
        ["prevCore"] = PrevCore,
        ["prevBootstrap"] = PrevBootstrap,
        ["utc"] = Utc.ToString("o", CultureInfo.InvariantCulture),
        ["note"] = Note,
    };
}

/// <summary>AUTOUPDATE5: <c>self-update.json</c> (schema 1): the journal and the failed versions.</summary>
internal sealed class SelfState
{
    public const int Schema = 1;
    public const int MaxFailed = 16;

    public SelfJournal? Journal { get; set; }
    public List<SelfFailed> Failed { get; } = new();

    public bool IsFailed(string version)
    {
        foreach (var f in Failed)
            if (string.Equals(f.Version, version, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void AddFailed(string version, string reason, DateTime utc)
    {
        Failed.RemoveAll(f => string.Equals(f.Version, version, StringComparison.OrdinalIgnoreCase));
        Failed.Add(new SelfFailed { Version = version ?? "", Reason = reason ?? "", Utc = utc });
        while (Failed.Count > MaxFailed) Failed.RemoveAt(0);
    }

    public static SelfState Parse(string? json)
    {
        var s = new SelfState();
        if (string.IsNullOrWhiteSpace(json)) return s;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return s;
            if (root.TryGetProperty("journal", out var j) && j.ValueKind == JsonValueKind.Object)
            {
                s.Journal = new SelfJournal
                {
                    State = Str(j, "state"),
                    Version = Str(j, "version"),
                    Previous = Str(j, "previous"),
                    Package = Str(j, "package"),
                    NewDir = Str(j, "newDir"),
                    PrevDir = Str(j, "prevDir"),
                    NewCore = Str(j, "newCore"),
                    NewBootstrap = Str(j, "newBootstrap"),
                    PrevCore = Str(j, "prevCore"),
                    PrevBootstrap = Str(j, "prevBootstrap"),
                    Route = Str(j, "route"),
                    Starts = j.TryGetProperty("starts", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int v) ? v : 0,
                    Utc = Time(j, "utc"),
                    Note = Str(j, "note"),
                };
            }

            if (root.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.Array)
                foreach (var row in f.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    string version = Str(row, "version");
                    if (version.Length == 0) continue;
                    s.Failed.Add(new SelfFailed { Version = version, Reason = Str(row, "reason"), Utc = Time(row, "utc") });
                }
        }
        catch (JsonException) { return new SelfState(); }

        return s;
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            if (Journal != null)
            {
                var j = Journal;
                w.WriteStartObject("journal");
                w.WriteString("state", j.State);
                w.WriteString("version", j.Version);
                w.WriteString("previous", j.Previous);
                w.WriteString("package", j.Package);
                w.WriteString("route", j.Route);
                w.WriteNumber("starts", j.Starts);
                w.WriteString("newDir", j.NewDir);
                w.WriteString("prevDir", j.PrevDir);
                w.WriteString("newCore", j.NewCore);
                w.WriteString("newBootstrap", j.NewBootstrap);
                w.WriteString("prevCore", j.PrevCore);
                w.WriteString("prevBootstrap", j.PrevBootstrap);
                w.WriteString("utc", j.Utc.ToString("o", CultureInfo.InvariantCulture));
                w.WriteString("note", j.Note);
                w.WriteEndObject();
            }

            w.WriteStartArray("failed");
            foreach (var f in Failed)
            {
                w.WriteStartObject();
                w.WriteString("version", f.Version);
                w.WriteString("reason", f.Reason);
                w.WriteString("utc", f.Utc.ToString("o", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static DateTime Time(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t : default;
}

/// <summary>
/// AUTOUPDATE5: the self-install record, <c>BepInEx/progenitor-self-install.json</c>: the Progenitor release Progenitor
/// installed here and the sha256 of its Bootstrap and Core. Written when an update Progenitor installed reaches Core's
/// healthy mark (or was restored by the start-loop guard). Files that match it are managed: only those update themselves.
/// </summary>
internal sealed class SelfInstallRecord
{
    public const int Schema = 1;

    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string CoreSha { get; set; } = "";
    public string BootstrapSha { get; set; } = "";
    public DateTime InstalledUtc { get; set; }

    /// <summary>Where it came from: <c>thunderstore</c>, or <c>rollback</c> after the start-loop guard.</summary>
    public string Source { get; set; } = "";

    public bool Matches(string coreSha, string bootstrapSha) =>
        CoreSha.Length > 0 && BootstrapSha.Length > 0
        && string.Equals(CoreSha, coreSha, StringComparison.OrdinalIgnoreCase)
        && string.Equals(BootstrapSha, bootstrapSha, StringComparison.OrdinalIgnoreCase);

    public static SelfInstallRecord? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var rec = new SelfInstallRecord
            {
                Package = SelfState.Str(r, "package"),
                Version = SelfState.Str(r, "version"),
                CoreSha = SelfState.Str(r, "coreSha256"),
                BootstrapSha = SelfState.Str(r, "bootstrapSha256"),
                InstalledUtc = SelfState.Time(r, "installedUtc"),
                Source = SelfState.Str(r, "source"),
            };
            return rec.Version.Length == 0 ? null : rec;
        }
        catch (JsonException) { return null; }
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("package", Package);
            w.WriteString("version", Version);
            w.WriteString("coreSha256", CoreSha);
            w.WriteString("bootstrapSha256", BootstrapSha);
            w.WriteString("installedUtc", InstalledUtc.ToString("o", CultureInfo.InvariantCulture));
            w.WriteString("source", Source);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["package"] = Package,
        ["version"] = Version,
        ["coreSha256"] = CoreSha,
        ["bootstrapSha256"] = BootstrapSha,
        ["installedUtc"] = InstalledUtc.ToString("o", CultureInfo.InvariantCulture),
        ["source"] = Source,
    };
}

/// <summary>AUTOUPDATE5: <c>release.json</c> of a kept release.</summary>
internal sealed class SelfKept
{
    public string Version { get; set; } = "";
    public string Package { get; set; } = "";
    public string CoreSha { get; set; } = "";
    public string BootstrapSha { get; set; } = "";
    public int Protocol { get; set; }

    /// <summary><c>thunderstore</c> (downloaded) or <c>installed</c> (the copy that ran before an update).</summary>
    public string Source { get; set; } = "";

    public DateTime KeptUtc { get; set; }

    /// <summary>The folder name under the store (filled when listed).</summary>
    public string Dir { get; set; } = "";

    public static SelfKept? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            return new SelfKept
            {
                Version = SelfState.Str(r, "version"),
                Package = SelfState.Str(r, "package"),
                CoreSha = SelfState.Str(r, "coreSha256"),
                BootstrapSha = SelfState.Str(r, "bootstrapSha256"),
                Protocol = r.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int n) ? n : 0,
                Source = SelfState.Str(r, "source"),
                KeptUtc = SelfState.Time(r, "keptUtc"),
            };
        }
        catch (JsonException) { return null; }
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("version", Version);
            w.WriteString("package", Package);
            w.WriteString("coreSha256", CoreSha);
            w.WriteString("bootstrapSha256", BootstrapSha);
            w.WriteNumber("protocol", Protocol);
            w.WriteString("source", Source);
            w.WriteString("keptUtc", KeptUtc.ToString("o", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["dir"] = Dir,
        ["version"] = Version,
        ["package"] = Package,
        ["coreSha256"] = CoreSha,
        ["bootstrapSha256"] = BootstrapSha,
        ["protocol"] = Protocol,
        ["source"] = Source,
        ["keptUtc"] = KeptUtc.ToString("o", CultureInfo.InvariantCulture),
    };
}

/// <summary>AUTOUPDATE5: reads and writes of the files above. Every call catches; nothing here throws.</summary>
internal static class SelfFiles
{
    private static readonly object Gate = new();

    public static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(bytes));
    }

    /// <summary>The sha256 of a file, or empty when it cannot be read.</summary>
    public static string Sha256File(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var sha = SHA256.Create();
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Hex(sha.ComputeHash(s));
        }
        catch { return ""; }
    }

    private static string Hex(byte[] h)
    {
        var sb = new StringBuilder(h.Length * 2);
        foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>Write through a temp file in the same folder, then move it over the target.</summary>
    public static bool WriteAtomic(string path, byte[] bytes, out string error)
    {
        error = "";
        string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* a stray temp costs nothing */ }
        }
    }

    public static bool WriteAtomic(string path, string text, out string error) =>
        WriteAtomic(path, Encoding.UTF8.GetBytes(text), out error);

    public static SelfState ReadState(string root)
    {
        try
        {
            string path = SelfPaths.State(root);
            lock (Gate) return File.Exists(path) ? SelfState.Parse(File.ReadAllText(path)) : new SelfState();
        }
        catch { return new SelfState(); }
    }

    /// <summary>Read, change and write the state under one lock. <paramref name="change"/> returns false to write nothing.</summary>
    public static bool EditState(string root, Func<SelfState, bool> change, out string error)
    {
        error = "";
        lock (Gate)
        {
            SelfState state;
            string path = SelfPaths.State(root);
            try { state = File.Exists(path) ? SelfState.Parse(File.ReadAllText(path)) : new SelfState(); }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }

            bool write;
            try { write = change(state); }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }

            return !write || WriteAtomic(path, state.ToJson(), out error);
        }
    }

    public static SelfInstallRecord? ReadRecord(string root)
    {
        try
        {
            string path = SelfPaths.Record(root);
            return File.Exists(path) ? SelfInstallRecord.Parse(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    public static bool WriteRecord(string root, SelfInstallRecord record, out string error) =>
        WriteAtomic(SelfPaths.Record(root), record.ToJson(), out error);

    public static SelfKept? ReadKept(string dir)
    {
        try
        {
            string path = Path.Combine(dir, SelfPaths.KeptInfoName);
            var k = File.Exists(path) ? SelfKept.Parse(File.ReadAllText(path)) : null;
            if (k != null) k.Dir = Path.GetFileName(dir);
            return k;
        }
        catch { return null; }
    }

    /// <summary>Copy a kept release's address tables over the live ones (the live folder keeps files the release does not have).</summary>
    public static int CopyTables(string fromDir, string toDir)
    {
        int n = 0;
        try
        {
            string src = Path.Combine(fromDir, SelfPaths.TablesFolder);
            if (!Directory.Exists(src)) return 0;
            Directory.CreateDirectory(toDir);
            foreach (string f in Directory.GetFiles(src, "*.json"))
            {
                if (WriteAtomic(Path.Combine(toDir, Path.GetFileName(f)), File.ReadAllBytes(f), out _)) n++;
            }
        }
        catch { /* the tables are an optimization; a missing one falls back to the prefix scan */ }

        return n;
    }
}
