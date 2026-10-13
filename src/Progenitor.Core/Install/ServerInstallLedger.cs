using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Install;

/// <summary>
/// One file Progenitor wrote into a local dedicated-server folder. <see cref="Path"/> is
/// relative to the server root with forward slashes (<c>BepInEx/plugins/Progenitor.Bootstrap.dll</c>).
/// </summary>
internal sealed class ServerInstallLedgerEntry
{
    public ServerInstallLedgerEntry(
        string path,
        string sha256,
        long size,
        string category,
        string source,
        DateTime writtenUtc,
        bool uninstallPending = false,
        bool hot = false,
        string? pluginId = null,
        string? route = null)
    {
        Hot = hot;
        Route = string.IsNullOrWhiteSpace(route) ? "" : route!.Trim();
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Sha256 = sha256 ?? "";
        Size = size;
        Category = category ?? ServerInstallLedger.CategoryFramework;
        Source = source ?? "";
        WrittenUtc = writtenUtc;
        UninstallPending = uninstallPending;
        PluginId = string.IsNullOrWhiteSpace(pluginId) ? "" : pluginId.Trim();
    }

    public string Path { get; }
    public string Sha256 { get; }
    public long Size { get; }
    /// <summary><c>framework</c> | <c>mod</c> | <c>pending</c>.</summary>
    public string Category { get; }
    /// <summary>
    /// <c>client-image:&lt;sha&gt;</c> or <c>package:&lt;listing key&gt;@&lt;version&gt;</c>.
    ///
    /// <para>POLISH4 item 1: the package half is the <em>listing key</em>
    /// (<c>Progenitor-HotDemo</c>) on every route — pending, promoted, hot and framework rows alike.
    /// The plugin id, when the install knew it, goes in <see cref="PluginId"/> instead. Before this
    /// job the first-class <c>progenitor.json</c> restart route wrote the plugin id here, and in a
    /// later session — where the catalog entry carries no inspected manifest — nothing could join the
    /// row back to its listing, so the server half of a Both install was invisible to the Install tab
    /// and its Uninstall.</para>
    /// </summary>
    public string Source { get; }
    public DateTime WrittenUtc { get; }

    /// <summary>
    /// POLISH4 item 1: the <c>[BepInPlugin]</c> GUID the install read out of this package's bytes,
    /// when it knew it. Optional and absent from every ledger written before this job, so the schema
    /// is unchanged; a reader that needs the plugin id of an older row falls back to the identity
    /// cache (<see cref="LedgerSpelling"/>).
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// INSTALL2 item 4: this file is removed at the next start of the process that loaded it. The
    /// file itself is still on disk and still loaded — the evidence Bootstrap acts on is the
    /// <c>.uninstall</c> marker beside it (<see cref="UninstallMarkers"/>), and this flag is what
    /// the menu reads without walking the folder. Absent (false) in every ledger written before
    /// this job and in every row that is not marked, so the schema is unchanged.
    /// </summary>
    public bool UninstallPending { get; }

    /// <summary>
    /// INSTALL2 defect A: this file was written into <c>progenitor-reload/</c> by
    /// <see cref="HotLoadExecutor"/> in a process that was <em>already running</em>, so that process
    /// hot loaded it and can hot unload it again. It is the <em>only</em> evidence of that: the
    /// reloader loads every DLL in its folder, so "the reloader reports this plugin loaded" says
    /// nothing about how it got there.
    ///
    /// <para>The flag is per-process by construction: it is cleared for every row the first time this
    /// process touches the ledger (<see cref="ServerInstall.ClearHotFlags"/>, called before any read
    /// or write of it), so after a restart a promoted copy is an ordinary loaded mod — no <c>new</c>
    /// tag, and its uninstall writes the <c>.uninstall</c> marker instead of deleting a live file.</para>
    /// </summary>
    public bool Hot { get; }

    /// <summary>
    /// REMOTEINST1: which route wrote this row. <see cref="ServerInstallLedger.RouteRemote"/> for a
    /// remote install the server ran on an admin's request; empty for the local folder route and
    /// for every row an earlier build wrote. Updates and uninstalls follow it.
    /// </summary>
    public string Route { get; }

    /// <summary>The same row with <see cref="Route"/> replaced.</summary>
    public ServerInstallLedgerEntry WithRoute(string? route) =>
        new(Path, Sha256, Size, Category, Source, WrittenUtc, UninstallPending, Hot, PluginId, route);
}

/// <summary>
/// <c>BepInEx/progenitor-install.json</c>: every file Progenitor wrote into a server folder.
/// The uninstall removes nothing that is not listed here, and nothing whose bytes changed.
/// Written temp-plus-move; a torn ledger is read as absent.
/// </summary>
internal sealed class ServerInstallLedger
{
    public const int CurrentSchema = 1;
    public const string FileName = "progenitor-install.json";
    public const string CategoryFramework = "framework";
    public const string CategoryMod = "mod";
    public const string CategoryPending = "pending";
    public const string ClientImageSourcePrefix = "client-image:";
    public const string PackageSourcePrefix = "package:";

    /// <summary>REMOTEINST1: <see cref="ServerInstallLedgerEntry.Route"/> of a row a remote install wrote.</summary>
    public const string RouteRemote = "remote";

    private readonly List<ServerInstallLedgerEntry> _files;

    public ServerInstallLedger(string version, string bootstrapSha256, string coreSha256, DateTime installedUtc, IEnumerable<ServerInstallLedgerEntry>? files = null)
    {
        Version = version ?? "";
        BootstrapSha256 = bootstrapSha256 ?? "";
        CoreSha256 = coreSha256 ?? "";
        InstalledUtc = installedUtc;
        _files = files != null ? new List<ServerInstallLedgerEntry>(files) : new List<ServerInstallLedgerEntry>();
    }

    public int Schema => CurrentSchema;
    public string Version { get; }
    public string BootstrapSha256 { get; }
    public string CoreSha256 { get; }
    public DateTime InstalledUtc { get; }
    public IReadOnlyList<ServerInstallLedgerEntry> Files => _files;

    public static string PathFor(string serverRoot) =>
        System.IO.Path.Combine(serverRoot, LocalServerFolder.BepInExFolderName, FileName);

    public static string Relative(string serverRoot, string absolute)
    {
        string rel = System.IO.Path.GetRelativePath(serverRoot, absolute);
        return rel.Replace('\\', '/');
    }

    public static string Absolute(string serverRoot, string relative) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(serverRoot, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public bool Contains(string relativePath)
    {
        for (int i = 0; i < _files.Count; i++)
        {
            if (string.Equals(_files[i].Path, relativePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Add or replace the entry for <paramref name="entry"/>'s path.</summary>
    public ServerInstallLedger With(ServerInstallLedgerEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        var next = new List<ServerInstallLedgerEntry>(_files.Count + 1);
        for (int i = 0; i < _files.Count; i++)
        {
            if (!string.Equals(_files[i].Path, entry.Path, StringComparison.OrdinalIgnoreCase))
                next.Add(_files[i]);
        }

        next.Add(entry);
        return new ServerInstallLedger(Version, BootstrapSha256, CoreSha256, InstalledUtc, next);
    }

    public IReadOnlyList<ServerInstallLedgerEntry> OfCategory(string category)
    {
        var list = new List<ServerInstallLedgerEntry>();
        for (int i = 0; i < _files.Count; i++)
        {
            if (string.Equals(_files[i].Category, category, StringComparison.Ordinal))
                list.Add(_files[i]);
        }

        return list;
    }

    /// <summary>Null when absent, torn, or a schema this build does not read.</summary>
    public static ServerInstallLedger? TryRead(string serverRoot)
    {
        string path = PathFor(serverRoot);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != CurrentSchema)
                return null;
            string version = ReadString(root, "version");
            string bootstrap = ReadString(root, "bootstrapSha256");
            string core = ReadString(root, "coreSha256");
            DateTime installed = ReadTime(root, "installedUtc");
            var files = new List<ServerInstallLedgerEntry>();
            if (root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string rel = ReadString(item, "path");
                    if (string.IsNullOrEmpty(rel)) continue;
                    long size = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    bool marked = item.TryGetProperty("uninstallPending", out var pending)
                        && pending.ValueKind == JsonValueKind.True;
                    bool hot = item.TryGetProperty("hot", out var live) && live.ValueKind == JsonValueKind.True;
                    files.Add(new ServerInstallLedgerEntry(
                        rel,
                        ReadString(item, "sha256"),
                        size,
                        ReadString(item, "category"),
                        ReadString(item, "source"),
                        ReadTime(item, "writtenUtc"),
                        marked,
                        hot,
                        ReadString(item, "pluginId"),
                        ReadString(item, "route")));
                }
            }

            return new ServerInstallLedger(version, bootstrap, core, installed, files);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Temp-plus-move so a crash mid-write never leaves a half ledger.</summary>
    public void Write(string serverRoot)
    {
        string path = PathFor(serverRoot);
        InstallStaging.AtomicWrite(path, Encoding.UTF8.GetBytes(ToJson()));
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", CurrentSchema);
            w.WriteString("version", Version);
            w.WriteString("bootstrapSha256", BootstrapSha256);
            w.WriteString("coreSha256", CoreSha256);
            w.WriteString("installedUtc", Stamp(InstalledUtc));
            w.WriteStartArray("files");
            for (int i = 0; i < _files.Count; i++)
            {
                var f = _files[i];
                w.WriteStartObject();
                w.WriteString("path", f.Path);
                w.WriteString("sha256", f.Sha256);
                w.WriteNumber("size", f.Size);
                w.WriteString("category", f.Category);
                w.WriteString("source", f.Source);
                w.WriteString("writtenUtc", Stamp(f.WrittenUtc));
                // POLISH4 item 1: written only when the install knew the plugin id, so a row for a
                // package nothing inspected keeps exactly the bytes every earlier build wrote.
                if (f.PluginId.Length > 0) w.WriteString("pluginId", f.PluginId);
                // INSTALL2: written only when set, so an unmarked ledger's bytes are exactly what
                // every build before this job wrote.
                if (f.UninstallPending) w.WriteBoolean("uninstallPending", true);
                // INSTALL2 defect A: only a file the running process hot loaded carries this, and only
                // until that process ends.
                if (f.Hot) w.WriteBoolean("hot", true);
                // REMOTEINST1: only a remote install writes it, so a local ledger keeps its bytes.
                if (f.Route.Length > 0) w.WriteString("route", f.Route);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTime ReadTime(JsonElement obj, string name)
    {
        string text = ReadString(obj, name);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : DateTime.MinValue;
    }
}
