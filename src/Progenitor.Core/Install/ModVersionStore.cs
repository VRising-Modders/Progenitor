using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Install;

/// <summary>
/// JOINFLOW4: where a parked copy came from. <see cref="User"/> is a copy nobody installed for a server (the
/// player's own install, a deploy, a mod dropped in by hand); it is never evicted unless the player asks.
/// </summary>
internal enum ModVersionSource
{
    User = 0,
    Thunderstore = 1,
    Server = 2,
}

/// <summary>JOINFLOW4: a copy of the same mod id from the server-mods record (<c>progenitor-server-mods.txt</c>), kept with a parked copy.</summary>
internal sealed class ParkedRecordRow
{
    public string Sha256 { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string HostKey { get; set; } = "";
    public string Host { get; set; } = "";
    public bool Always { get; set; } = true;
    public string InstalledUtc { get; set; } = "";
}

/// <summary>JOINFLOW4: the install-ledger row (<c>progenitor-install.json</c>) of a copy, kept while the copy is parked.</summary>
internal sealed class ParkedLedgerRow
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Category { get; set; } = "";
    public string Source { get; set; } = "";
    public string WrittenUtc { get; set; } = "";
    public string PluginId { get; set; } = "";
    public string Route { get; set; } = "";

    /// <summary>INDEX1: the row's <c>origin</c> (<c>index</c> for a Progenitor Index install), kept while the copy is parked.</summary>
    public string Origin { get; set; } = "";
}

/// <summary>JOINFLOW4: one parked (kept, not loaded) copy of a mod.</summary>
internal sealed class ParkedVersion
{
    public string Version { get; set; } = "";

    /// <summary>The store folder under the mod's folder (the version, or <c>version~sha8</c> when two builds share a version).</summary>
    public string Dir { get; set; } = "";

    /// <summary>The file name the copy had when it was active, and gets back when it is swapped in.</summary>
    public string File { get; set; } = "";

    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public ModVersionSource Source { get; set; }

    /// <summary><c>Owner-Name@1.8.2</c> for Thunderstore, <c>fp@host</c> for a server's copy, else empty.</summary>
    public string SourceDetail { get; set; } = "";

    /// <summary>Where it was active: <c>reload</c> or <c>pending</c>.</summary>
    public string Placement { get; set; } = ModVersionPaths.PlacementReload;

    /// <summary>The player's own copy (see <see cref="ModVersionSource.User"/>): never evicted.</summary>
    public bool UserInstalled { get; set; }

    public DateTime ParkedUtc { get; set; }
    public DateTime LastUsedUtc { get; set; }

    /// <summary>The handshake level and version rule this copy declared when it was active (empty when unknown).</summary>
    public string Level { get; set; } = "";

    public string Rule { get; set; } = "";

    public ParkedLedgerRow? Ledger { get; set; }
    public ParkedRecordRow? Record { get; set; }

    /// <summary>
    /// A copy a server sent (an older Core's server-copy install) that the player never made their own. Since 2026-10-05
    /// such a copy is never swapped back in: it is skipped by every pick and deleted at Core start.
    /// </summary>
    public bool ServerSent => Source == ModVersionSource.Server && !UserInstalled;

    public ParkedVersion Clone() => (ParkedVersion)MemberwiseClone();
}

/// <summary>JOINFLOW4: the copy that is active now, as far as the store knows it (set when the store or the join flow placed it).</summary>
internal sealed class ActiveVersion
{
    public string Version { get; set; } = "";

    /// <summary>The active file's name in <c>progenitor-reload</c> (empty when not known, e.g. a Thunderstore install still running).</summary>
    public string File { get; set; } = "";

    public string Sha256 { get; set; } = "";

    /// <summary>The server key (<c>fp@host</c>) it was placed for, or empty when it is the player's choice.</summary>
    public string ForServer { get; set; } = "";

    public string ForServerName { get; set; } = "";

    public ModVersionSource Source { get; set; }

    public ActiveVersion Clone() => (ActiveVersion)MemberwiseClone();
}

/// <summary>JOINFLOW4: a swap queued for the next start (the restart route).</summary>
internal sealed class PendingSwap
{
    public string Version { get; set; } = "";

    /// <summary>The pending file, relative to the BepInEx root (<c>progenitor-pending/x.dll</c>).</summary>
    public string PendingFile { get; set; } = "";

    /// <summary>The name it promotes to in <c>progenitor-reload</c>.</summary>
    public string File { get; set; } = "";

    public string Sha256 { get; set; } = "";

    /// <summary>The active file marked for removal at the next start (relative), or empty.</summary>
    public string Marked { get; set; } = "";

    public string ForServer { get; set; } = "";
    public string ForServerName { get; set; } = "";
    public DateTime QueuedUtc { get; set; }

    public PendingSwap Clone() => (PendingSwap)MemberwiseClone();
}

/// <summary>JOINFLOW4: one mod's versions in the store.</summary>
internal sealed class ModVersionEntry
{
    public string Id { get; set; } = "";

    /// <summary>The version the player prefers: the one they last chose, else (empty) the newest copy they installed.</summary>
    public string Preferred { get; set; } = "";

    public ActiveVersion? Active { get; set; }
    public PendingSwap? Pending { get; set; }
    public List<ParkedVersion> Parked { get; set; } = new();

    /// <summary>The kept copy of <paramref name="version"/>; of two builds of one version, the most recently parked.</summary>
    public ParkedVersion? FindParked(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        ParkedVersion? best = null;
        foreach (var p in Parked)
            if (string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase) && (best == null || p.ParkedUtc >= best.ParkedUtc)) best = p;
        return best;
    }

    /// <summary>
    /// The kept copy of <paramref name="version"/> that may be swapped in: as <see cref="FindParked"/>, skipping copies a
    /// server sent (<see cref="ParkedVersion.ServerSent"/>).
    /// </summary>
    public ParkedVersion? FindUsable(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        ParkedVersion? best = null;
        foreach (var p in Parked)
            if (!p.ServerSent && string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase) && (best == null || p.ParkedUtc >= best.ParkedUtc)) best = p;
        return best;
    }

    public ModVersionEntry Clone()
    {
        var e = new ModVersionEntry
        {
            Id = Id,
            Preferred = Preferred,
            Active = Active?.Clone(),
            Pending = Pending?.Clone(),
        };
        foreach (var p in Parked) e.Parked.Add(p.Clone());
        return e;
    }
}

/// <summary>JOINFLOW4: the paths of the version store, under a BepInEx root.</summary>
internal sealed class ModVersionPaths
{
    public const string FolderName = "progenitor-versions";
    public const string ManifestName = "versions.json";
    public const string JournalName = "journal.json";
    public const string PartSuffix = ".part";
    public const string PlacementReload = "reload";
    public const string PlacementPending = "pending";

    public ModVersionPaths(string bepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) throw new ArgumentException("no BepInEx root", nameof(bepInExRoot));
        BepInExRoot = Path.GetFullPath(bepInExRoot);
    }

    public string BepInExRoot { get; }
    public string Root => Path.Combine(BepInExRoot, FolderName);
    public string Manifest => Path.Combine(Root, ManifestName);
    public string Journal => Path.Combine(Root, JournalName);
    public string Reload => Path.Combine(BepInExRoot, "progenitor-reload");
    public string Pending => Path.Combine(BepInExRoot, "progenitor-pending");
    public string Plugins => Path.Combine(BepInExRoot, "plugins");

    /// <summary>The game folder (the parent of BepInEx), where the install ledger's paths are rooted.</summary>
    public string GameRoot => Path.GetDirectoryName(BepInExRoot) ?? BepInExRoot;

    /// <summary>The stored file of a parked copy.</summary>
    public string StoredFile(string modId, ParkedVersion p) => Path.Combine(Root, modId, p.Dir, p.File);

    /// <summary>Relative to the BepInEx root, forward slashes.</summary>
    public string Relative(string absolute) => Path.GetRelativePath(BepInExRoot, absolute).Replace('\\', '/');

    public string Absolute(string relative) =>
        Path.GetFullPath(Path.Combine(BepInExRoot, (relative ?? "").Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>A mod id, version or file name used as one path segment: <c>[A-Za-z0-9._~+-]</c>, 1–96 characters, not dots only.</summary>
    public static bool IsSafeSegment(string? s)
    {
        if (string.IsNullOrEmpty(s) || s!.Length > 96) return false;
        bool allDots = true;
        foreach (char c in s)
        {
            bool ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or '~' or '+';
            if (!ok) return false;
            if (c != '.') allDots = false;
        }

        return !allDots;
    }

    /// <summary>A file the store may write back into a loader folder: a safe segment ending <c>.dll</c>.</summary>
    public static bool IsSafeDllName(string? name) =>
        IsSafeSegment(name) && name!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && name.Length > 4;
}

/// <summary>JOINFLOW4: the store's bounds.</summary>
internal sealed class ModVersionLimits
{
    public static readonly ModVersionLimits Default = new();

    /// <summary>At most this many parked copies per mod (copies the player installed are kept beyond it).</summary>
    public int MaxPerMod { get; set; } = 3;

    /// <summary>At most this many bytes of parked copies in all (128 MiB).</summary>
    public long MaxTotalBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>The largest single copy the store accepts (64 MiB, the transfer's own ceiling is lower).</summary>
    public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;
}

/// <summary>JOINFLOW4: <c>progenitor-versions/versions.json</c>, schema 1. Written temp-plus-move; a torn file reads as empty.</summary>
internal sealed class ModVersionManifest
{
    public const int Schema = 1;

    public List<ModVersionEntry> Mods { get; } = new();

    public ModVersionEntry? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var m in Mods) if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    public ModVersionEntry GetOrAdd(string id)
    {
        var e = Find(id);
        if (e != null) return e;
        e = new ModVersionEntry { Id = id };
        Mods.Add(e);
        return e;
    }

    /// <summary>Drop entries with nothing parked, nothing active for a server and nothing queued.</summary>
    public void Compact()
    {
        for (int i = Mods.Count - 1; i >= 0; i--)
        {
            var m = Mods[i];
            if (m.Parked.Count == 0 && m.Pending == null && (m.Active == null || m.Active.ForServer.Length == 0))
                Mods.RemoveAt(i);
        }
    }

    public long TotalBytes
    {
        get
        {
            long n = 0;
            foreach (var m in Mods) foreach (var p in m.Parked) n += Math.Max(0, p.Size);
            return n;
        }
    }

    public ModVersionManifest Clone()
    {
        var c = new ModVersionManifest();
        foreach (var m in Mods) c.Mods.Add(m.Clone());
        return c;
    }

    // ---- JSON ----------------------------------------------------------------------------------

    public static ModVersionManifest Read(string path, out string error)
    {
        error = "";
        try
        {
            if (!System.IO.File.Exists(path)) return new ModVersionManifest();
            return Parse(System.IO.File.ReadAllText(path), out error);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return new ModVersionManifest();
        }
    }

    public static ModVersionManifest Parse(string? json, out string error)
    {
        error = "";
        var m = new ModVersionManifest();
        if (string.IsNullOrWhiteSpace(json)) return m;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "not an object"; return m; }
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != Schema)
            {
                error = "unknown schema";
                return m;
            }

            if (root.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
                foreach (var item in mods.EnumerateArray())
                {
                    var e = ReadEntry(item);
                    if (e != null && m.Find(e.Id) == null) m.Mods.Add(e);
                }
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return new ModVersionManifest();
        }

        return m;
    }

    private static ModVersionEntry? ReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string id = Str(item, "id");
        if (!ModVersionPaths.IsSafeSegment(id)) return null;
        var e = new ModVersionEntry { Id = id, Preferred = Str(item, "preferred") };
        if (item.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.Object)
            e.Active = new ActiveVersion
            {
                Version = Str(a, "version"),
                File = Str(a, "file"),
                Sha256 = Str(a, "sha256"),
                ForServer = Str(a, "forServer"),
                ForServerName = Str(a, "forServerName"),
                Source = SourceOf(Str(a, "source")),
            };
        if (item.TryGetProperty("pending", out var q) && q.ValueKind == JsonValueKind.Object)
            e.Pending = new PendingSwap
            {
                Version = Str(q, "version"),
                PendingFile = Str(q, "pendingFile"),
                File = Str(q, "file"),
                Sha256 = Str(q, "sha256"),
                Marked = Str(q, "marked"),
                ForServer = Str(q, "forServer"),
                ForServerName = Str(q, "forServerName"),
                QueuedUtc = Time(q, "queued"),
            };
        if (item.TryGetProperty("parked", out var parked) && parked.ValueKind == JsonValueKind.Array)
            foreach (var p in parked.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var v = new ParkedVersion
                {
                    Version = Str(p, "version"),
                    Dir = Str(p, "dir"),
                    File = Str(p, "file"),
                    Sha256 = Str(p, "sha256").ToLowerInvariant(),
                    Size = p.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
                    Source = SourceOf(Str(p, "source")),
                    SourceDetail = Str(p, "sourceDetail"),
                    Placement = Str(p, "placement"),
                    UserInstalled = p.TryGetProperty("userInstalled", out var u) && u.ValueKind == JsonValueKind.True,
                    ParkedUtc = Time(p, "parked"),
                    LastUsedUtc = Time(p, "lastUsed"),
                    Level = Str(p, "level"),
                    Rule = Str(p, "rule"),
                };
                if (v.Version.Length == 0 || !ModVersionPaths.IsSafeSegment(v.Dir) || !ModVersionPaths.IsSafeDllName(v.File)) continue;
                if (p.TryGetProperty("ledger", out var l) && l.ValueKind == JsonValueKind.Object)
                    v.Ledger = new ParkedLedgerRow
                    {
                        Path = Str(l, "path"),
                        Sha256 = Str(l, "sha256"),
                        Size = l.TryGetProperty("size", out var ls) && ls.ValueKind == JsonValueKind.Number ? ls.GetInt64() : 0,
                        Category = Str(l, "category"),
                        Source = Str(l, "source"),
                        WrittenUtc = Str(l, "writtenUtc"),
                        PluginId = Str(l, "pluginId"),
                        Route = Str(l, "route"),
                        Origin = Str(l, "origin"),
                    };
                if (p.TryGetProperty("record", out var r) && r.ValueKind == JsonValueKind.Object)
                    v.Record = new ParkedRecordRow
                    {
                        Sha256 = Str(r, "sha256"),
                        FileName = Str(r, "fileName"),
                        Id = Str(r, "id"),
                        Version = Str(r, "version"),
                        HostKey = Str(r, "hostKey"),
                        Host = Str(r, "host"),
                        Always = !(r.TryGetProperty("always", out var al) && al.ValueKind == JsonValueKind.False),
                        InstalledUtc = Str(r, "installedUtc"),
                    };
                e.Parked.Add(v);
            }

        return e;
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteStartArray("mods");
            foreach (var e in Mods)
            {
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                w.WriteString("preferred", e.Preferred);
                if (e.Active != null)
                {
                    w.WriteStartObject("active");
                    w.WriteString("version", e.Active.Version);
                    w.WriteString("file", e.Active.File);
                    w.WriteString("sha256", e.Active.Sha256);
                    w.WriteString("forServer", e.Active.ForServer);
                    w.WriteString("forServerName", e.Active.ForServerName);
                    w.WriteString("source", SourceName(e.Active.Source));
                    w.WriteEndObject();
                }

                if (e.Pending != null)
                {
                    w.WriteStartObject("pending");
                    w.WriteString("version", e.Pending.Version);
                    w.WriteString("pendingFile", e.Pending.PendingFile);
                    w.WriteString("file", e.Pending.File);
                    w.WriteString("sha256", e.Pending.Sha256);
                    w.WriteString("marked", e.Pending.Marked);
                    w.WriteString("forServer", e.Pending.ForServer);
                    w.WriteString("forServerName", e.Pending.ForServerName);
                    w.WriteString("queued", Stamp(e.Pending.QueuedUtc));
                    w.WriteEndObject();
                }

                w.WriteStartArray("parked");
                foreach (var p in e.Parked)
                {
                    w.WriteStartObject();
                    w.WriteString("version", p.Version);
                    w.WriteString("dir", p.Dir);
                    w.WriteString("file", p.File);
                    w.WriteString("sha256", p.Sha256);
                    w.WriteNumber("size", p.Size);
                    w.WriteString("source", SourceName(p.Source));
                    w.WriteString("sourceDetail", p.SourceDetail);
                    w.WriteString("placement", p.Placement);
                    w.WriteBoolean("userInstalled", p.UserInstalled);
                    w.WriteString("parked", Stamp(p.ParkedUtc));
                    w.WriteString("lastUsed", Stamp(p.LastUsedUtc));
                    if (p.Level.Length > 0) w.WriteString("level", p.Level);
                    if (p.Rule.Length > 0) w.WriteString("rule", p.Rule);
                    if (p.Ledger != null)
                    {
                        w.WriteStartObject("ledger");
                        w.WriteString("path", p.Ledger.Path);
                        w.WriteString("sha256", p.Ledger.Sha256);
                        w.WriteNumber("size", p.Ledger.Size);
                        w.WriteString("category", p.Ledger.Category);
                        w.WriteString("source", p.Ledger.Source);
                        w.WriteString("writtenUtc", p.Ledger.WrittenUtc);
                        w.WriteString("pluginId", p.Ledger.PluginId);
                        w.WriteString("route", p.Ledger.Route);
                        if (p.Ledger.Origin.Length > 0) w.WriteString("origin", p.Ledger.Origin);
                        w.WriteEndObject();
                    }

                    if (p.Record != null)
                    {
                        w.WriteStartObject("record");
                        w.WriteString("sha256", p.Record.Sha256);
                        w.WriteString("fileName", p.Record.FileName);
                        w.WriteString("id", p.Record.Id);
                        w.WriteString("version", p.Record.Version);
                        w.WriteString("hostKey", p.Record.HostKey);
                        w.WriteString("host", p.Record.Host);
                        w.WriteBoolean("always", p.Record.Always);
                        w.WriteString("installedUtc", p.Record.InstalledUtc);
                        w.WriteEndObject();
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Write(string path) => ModVersionFiles.WriteAtomic(path, Encoding.UTF8.GetBytes(ToJson()));

    internal static string SourceName(ModVersionSource s) => s switch
    {
        ModVersionSource.Thunderstore => "thunderstore",
        ModVersionSource.Server => "server",
        _ => "user",
    };

    internal static ModVersionSource SourceOf(string s) => s switch
    {
        "thunderstore" => ModVersionSource.Thunderstore,
        "server" => ModVersionSource.Server,
        _ => ModVersionSource.User,
    };

    internal static string Stamp(DateTime utc) =>
        utc == default ? "" : utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTime Time(JsonElement obj, string name)
    {
        string text = Str(obj, name);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : default;
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>JOINFLOW4: the store's file primitives: hashing, atomic writes through <c>.part</c>, quiet deletes.</summary>
internal static class ModVersionFiles
{
    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "(none)" : sha!.Length <= 12 ? sha : sha.Substring(0, 12);

    /// <summary>The sha256 of a file, or "" when it is absent, or null when it exists and cannot be read.</summary>
    public static string? ShaOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            return Sha256Hex(File.ReadAllBytes(path));
        }
        catch { return null; }
    }

    /// <summary>
    /// Write <paramref name="bytes"/> to <paramref name="path"/> through a unique <c>.part</c> name and a move, then
    /// stamp it now: a copy keeps its source's write time, and the reloader's one-second settle window must see a
    /// fresh file, never an old stamp that would let it load the same second the previous copy is still loaded.
    /// The read-back must hash to the bytes written, else the file is deleted and this throws.
    /// </summary>
    public static void WriteAtomic(string path, byte[] bytes, DateTime? stamp = null)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
        string part = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ModVersionPaths.PartSuffix;
        try
        {
            using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            File.Move(part, path, overwrite: true);
        }
        finally
        {
            TryDelete(part);
        }

        try { File.SetLastWriteTimeUtc(path, stamp ?? DateTime.UtcNow); }
        catch { /* the write itself stamped it now */ }
        string back = Sha256Hex(File.ReadAllBytes(path));
        if (!string.Equals(back, Sha256Hex(bytes), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new IOException("the written file did not read back as the same bytes");
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch { return false; }
    }

    /// <summary>Delete the <c>.part</c> leftovers a crash left under the store.</summary>
    public static int SweepParts(string root)
    {
        int n = 0;
        try
        {
            if (!Directory.Exists(root)) return 0;
            foreach (var f in Directory.GetFiles(root, "*" + ModVersionPaths.PartSuffix, SearchOption.AllDirectories))
                if (TryDelete(f)) n++;
        }
        catch { /* best effort */ }

        return n;
    }
}
