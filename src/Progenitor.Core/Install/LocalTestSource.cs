using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Progenitor.Install;

/// <summary>One listing of the local test index that provides a plugin GUID.</summary>
internal sealed class LocalTestMatch
{
    public LocalTestMatch(string key, string version, string name, IReadOnlyList<string>? active = null, long size = 0, bool hotLoadable = false)
    {
        Key = key ?? "";
        Version = version ?? "";
        Name = name ?? "";
        Active = active ?? (Version.Length > 0 ? new[] { Version } : Array.Empty<string>());
        Size = size;
        HotLoadable = hotLoadable;
    }

    /// <summary>The listing's <c>Owner-Name</c> key (the local index's spelling).</summary>
    public string Key { get; }

    /// <summary>The listing's newest version.</summary>
    public string Version { get; }

    public string Name { get; }

    /// <summary>Every active version the local index lists for it (the installer picks from these).</summary>
    public IReadOnlyList<string> Active { get; }

    /// <summary>The newest version's <c>file_size</c>, or 0.</summary>
    public long Size { get; }

    /// <summary>The package's <c>progenitor.json</c> declares <c>hotLoadable</c>.</summary>
    public bool HotLoadable { get; }
}

/// <summary>What one local zip declares, read without loading anything.</summary>
internal sealed class LocalZipFacts
{
    public static readonly LocalZipFacts None = new("", false, Array.Empty<string>());

    public LocalZipFacts(string manifestId, bool hotLoadable, IReadOnlyList<string> pluginGuids)
    {
        ManifestId = manifestId ?? "";
        HotLoadable = hotLoadable;
        PluginGuids = pluginGuids ?? Array.Empty<string>();
    }

    /// <summary>The <c>progenitor.json</c> <c>id</c>, or empty.</summary>
    public string ManifestId { get; }

    public bool HotLoadable { get; }

    /// <summary>Every <c>[BepInPlugin]</c> GUID its DLLs declare.</summary>
    public IReadOnlyList<string> PluginGuids { get; }

    /// <summary>
    /// The one plugin an install of this package is accepted as (the join installer's own rule): the
    /// <c>progenitor.json</c> id, else the single plugin of an ordinary package; empty when it cannot be told.
    /// </summary>
    public string AcceptedPlugin => ManifestId.Length > 0 ? ManifestId : PluginGuids.Count == 1 ? PluginGuids[0] : "";
}

/// <summary>
/// LOCALJOIN1 (debug, requested 2026-10-07): the join window's last resort for a required mod the server
/// named no usable source for, when — and only when — <c>[Install] LocalIndexPath</c> is configured. Each
/// listing of that local index (<see cref="LocalIndexSource"/>, HOT1) is mapped to the one plugin an install of
/// it is accepted as (<see cref="LocalZipFacts.AcceptedPlugin"/>), read from the local zip without loading
/// anything. A server row is matched by its plugin GUID only (never a name). The install itself is the join's
/// usual lane (<see cref="JoinCatalogInstaller"/>), which already accepts the configured local index's downloads.
/// </summary>
internal static class LocalTestSource
{
    /// <summary>The words every place uses for it: the row's source, its button and its note.</summary>
    public const string Words = "local test source";

    public const string InstallCaption = "Install (local test source)";
    public const string UpdateCaption = "Update (local test source)";

    /// <summary>The row's note when the local listing does not serve the version the server needs.</summary>
    public static string WrongVersion(string has, string needs) =>
        "the " + Words + " has " + (has.Length > 0 ? has : "another version") + ", not " + (needs.Length > 0 ? needs : "the version needed");

    /// <summary>Zips bigger than this are not opened (the dummy packages are a few KiB).</summary>
    public const long MaxZipBytes = 64L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Length, DateTime Write, LocalZipFacts Facts)> ZipCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// GUID → the local listing providing it, for the configured folder; empty when it is not configured or
    /// has no readable index. A GUID two listings provide is left out (no guess). Never throws.
    /// <paramref name="factsOfZip"/> replaces the zip read (tests).
    /// </summary>
    public static Dictionary<string, LocalTestMatch> Map(string? folder, Func<string, LocalZipFacts>? factsOfZip = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new Dictionary<string, LocalTestMatch>(StringComparer.OrdinalIgnoreCase);
        if (factsOfZip != null) return Build(folder, factsOfZip, null);
        // Review: asked for every report and every handshake answer; built again only when the index or a zip changed.
        string key;
        try { key = Path.GetFullPath(folder!.Trim()); }
        catch { return new Dictionary<string, LocalTestMatch>(StringComparer.OrdinalIgnoreCase); }
        string index = Path.Combine(key, LocalIndexSource.IndexFileName);
        string indexStamp = Stamp(index);
        lock (Gate)
        {
            if (_cached != null && string.Equals(_cachedFolder, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_cachedIndexStamp, indexStamp, StringComparison.Ordinal) && _cachedZips.TrueForAll(z => Stamp(z.Path) == z.Stamp))
                return new Dictionary<string, LocalTestMatch>(_cached, StringComparer.OrdinalIgnoreCase);
        }

        var zips = new List<(string Path, string Stamp)>();
        var built = Build(folder, null, zips);
        lock (Gate)
        {
            _cached = new Dictionary<string, LocalTestMatch>(built, StringComparer.OrdinalIgnoreCase);
            _cachedFolder = key;
            _cachedIndexStamp = indexStamp;
            _cachedZips = zips;
        }

        return built;
    }

    private static Dictionary<string, LocalTestMatch>? _cached;
    private static string _cachedFolder = "";
    private static string _cachedIndexStamp = "";
    private static List<(string Path, string Stamp)> _cachedZips = new();

    private static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
        }
        catch { return "?"; }
    }

    private static Dictionary<string, LocalTestMatch> Build(string? folder, Func<string, LocalZipFacts>? factsOfZip, List<(string Path, string Stamp)>? zipsRead)
    {
        var map = new Dictionary<string, LocalTestMatch>(StringComparer.OrdinalIgnoreCase);
        var source = new LocalIndexSource(folder);
        LocalIndexLoad load;
        try { load = source.Load(); }
        catch { return map; }
        if (!load.Ok || load.Entries == null) return map;
        string root = source.Folder!;

        // Every active version of each listing, as the join installer reads them.
        var versions = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in load.Entries) if (e != null && !string.IsNullOrEmpty(e.Key)) wanted[e.Key] = "";
            ThunderstoreVersions.TryLoad(File.ReadAllText(source.IndexPath!), "local:" + source.IndexPath, wanted, versions, local: true, out _);
        }
        catch { /* the listing's newest version stands alone */ }

        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in load.Entries)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key)) continue;
            string? zip = ZipPathOf(entry.DownloadUrl, root);
            if (zip == null) continue;
            zipsRead?.Add((zip, Stamp(zip)));
            LocalZipFacts facts;
            try { facts = factsOfZip != null ? factsOfZip(zip) ?? LocalZipFacts.None : FactsOfZip(zip); }
            catch { continue; }
            string id = facts.AcceptedPlugin.Trim();
            if (id.Length == 0 || ambiguous.Contains(id)) continue;
            IReadOnlyList<string>? active = versions.TryGetValue(entry.Key, out var pv) && pv.Active.Count > 0 ? pv.Active : null;
            // The newest active version (a local index may list oldest first).
            string newest = active != null ? ThunderstoreVersions.Highest(active, _ => true) ?? (entry.Manifest.Version ?? "") : entry.Manifest.Version ?? "";
            var match = new LocalTestMatch(entry.Key, newest, entry.DisplayName ?? entry.PackageName ?? entry.Key,
                active, entry.ListingFileSize ?? 0, facts.HotLoadable);
            if (map.TryGetValue(id, out var other) && !string.Equals(other.Key, match.Key, StringComparison.OrdinalIgnoreCase))
            {
                map.Remove(id);
                ambiguous.Add(id);
                continue;
            }

            map[id] = match;
        }

        return map;
    }

    /// <summary>The local file a <c>file:///</c> download URL names, when it lies inside <paramref name="root"/>; else null.</summary>
    internal static string? ZipPathOf(string? url, string root)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            if (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            string path = Path.GetFullPath(uri.LocalPath);
            string folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>What a local package declares: its <c>progenitor.json</c> id and hotLoadable, and its DLLs' plugin GUIDs.</summary>
    internal static LocalZipFacts FactsOfZip(string zip)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(zip);
            if (!info.Exists || info.Length > MaxZipBytes) return LocalZipFacts.None;
        }
        catch { return LocalZipFacts.None; }

        lock (Gate)
        {
            if (ZipCache.TryGetValue(zip, out var hit) && hit.Length == info.Length && hit.Write == info.LastWriteTimeUtc) return hit.Facts;
        }

        string manifestId = "";
        bool hot = false;
        var guids = new List<string>();
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var e in archive.Entries)
            {
                string name = e.FullName.Replace('\\', '/');
                if (string.Equals(Path.GetFileName(name), "progenitor.json", StringComparison.OrdinalIgnoreCase) && e.Length < 64 * 1024)
                {
                    using var s = e.Open();
                    using var doc = JsonDocument.Parse(s);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) manifestId = (id.GetString() ?? "").Trim();
                    if (root.TryGetProperty("hotLoadable", out var h) && h.ValueKind == JsonValueKind.True) hot = true;
                }
                else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && e.Length < MaxZipBytes)
                {
                    string temp = Path.Combine(Path.GetTempPath(), "progenitor-localtest-" + Guid.NewGuid().ToString("N") + ".dll");
                    try
                    {
                        e.ExtractToFile(temp);
                        if (PluginActions.PluginFileActions.TryReadPluginGuids(temp, out var declared))
                            foreach (string g in declared) Add(guids, g);
                    }
                    finally
                    {
                        try { File.Delete(temp); } catch { /* a temp file */ }
                    }
                }
            }
        }
        catch { /* an unreadable zip provides nothing */ }

        var facts = new LocalZipFacts(manifestId, hot, guids.ToArray());
        lock (Gate)
        {
            if (ZipCache.Count > 256) ZipCache.Clear();
            ZipCache[zip] = (info.Length, info.LastWriteTimeUtc, facts);
        }

        return facts;
    }

    private static void Add(List<string> list, string guid)
    {
        string g = guid.Trim();
        if (g.Length > 0 && !list.Exists(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase))) list.Add(g);
    }

    /// <summary>Tests only.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            ZipCache.Clear();
            _cached = null;
            _cachedFolder = "";
            _cachedZips = new();
        }
    }
}
