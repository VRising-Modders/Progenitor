using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Persistence;

namespace Progenitor.Install;

/// <summary>
/// INSTALLFAIL1 follow-up (live 2026-10-03): what an inspection proved about one package version's
/// <c>progenitor.json</c> — its manifest and its declared side — kept at
/// <c>BepInEx/progenitor-cache/verified/&lt;namespace&gt;/&lt;name&gt;/&lt;version&gt;.json</c>.
///
/// <para>The inspection itself lives only in the catalog's memory, so a Core hot swap dropped it and
/// HotDemo's row fell back from the manifest-verified <c>side: both</c> (Client cell
/// <c>Missing: the server has it</c>) to the listing's guess (<c>Not installed</c>). A Thunderstore
/// version's bytes never change, so the facts read from them are rediscovered from here when a new
/// catalog merges its index. They describe the row only: an install still downloads, inspects and
/// plans from the bytes before it writes anything.</para>
/// </summary>
internal sealed class VerifiedManifestCache
{
    public const string FolderName = "verified";
    private const int Schema = 1;

    private readonly string _root;

    public VerifiedManifestCache(string bepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) throw new ArgumentException("BepInEx root is required.", nameof(bepInExRoot));
        _root = Path.Combine(Path.GetFullPath(bepInExRoot), OrdinaryIdentityCache.CacheFolderName, FolderName);
    }

    public string Root => _root;

    /// <summary>One remembered package version.</summary>
    internal sealed class Record
    {
        public Record(ManifestMod manifest, PackageSide side, string? effectivePeers = null)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            Side = side;
            EffectivePeers = string.IsNullOrWhiteSpace(effectivePeers) ? null : effectivePeers!.Trim();
        }

        public ManifestMod Manifest { get; }
        public PackageSide Side { get; }

        /// <summary>
        /// Mod compatibility C3: the range this version's own build uses for its peers (its declared
        /// <c>compatibility.peers</c>, else the image's explicit rule, else the same minor version), or null when the
        /// inspection could not read the plugin image or the record predates ranges.
        /// </summary>
        public string? EffectivePeers { get; }
    }

    public static string Key(string ns, string name, string version) =>
        OrdinaryIdentityCache.Sanitize(ns) + "/" + OrdinaryIdentityCache.Sanitize(name) + "/" + OrdinaryIdentityCache.Sanitize(version);

    public string PathFor(string ns, string name, string version) =>
        Path.Combine(_root, OrdinaryIdentityCache.Sanitize(ns), OrdinaryIdentityCache.Sanitize(name),
            OrdinaryIdentityCache.Sanitize(version) + ".json");

    /// <summary>Remember a manifest-verified inspection of one listing version. Best-effort; never throws.</summary>
    public bool Write(CatalogEntry entry, PackageInspection inspection)
    {
        try
        {
            if (entry == null || inspection == null || !inspection.Ok) return false;
            if (inspection.Provenance != CatalogProvenance.ManifestVerified || inspection.ProgenitorManifest == null) return false;
            if (string.IsNullOrEmpty(entry.Owner) || string.IsNullOrEmpty(entry.PackageName)) return false;
            string version = entry.Manifest.Version ?? "";
            if (version.Length == 0) return false;
            var m = inspection.ProgenitorManifest;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schema", Schema);
                w.WriteString("namespace", entry.Owner);
                w.WriteString("name", entry.PackageName);
                w.WriteString("version", version);
                w.WriteString("side", SideName(inspection.Side));
                w.WriteString("id", m.Id);
                w.WriteString("manifestVersion", m.Version);
                w.WriteNumber("level", (int)m.Level);
                w.WriteNumber("versionRule", (int)m.VersionRule);
                w.WriteBoolean("hotLoadable", m.HotLoadable);
                w.WriteString("manifestNamespace", m.Namespace ?? "");
                w.WriteString("manifestPackage", m.Package ?? "");
                w.WriteString("sha256", m.Sha256 ?? "");
                if (m.Size is long size) w.WriteNumber("size", size);
                w.WriteString("url", m.Url ?? "");
                // Mod compatibility C3: the declared range and the build's effective one (additive; the schema is unchanged).
                if (!string.IsNullOrEmpty(m.Peers)) w.WriteString("peers", m.Peers);
                if (!string.IsNullOrEmpty(inspection.EffectivePeers)) w.WriteString("effectivePeers", inspection.EffectivePeers);
                w.WriteString("inspectedUtc", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }

            InstallStaging.AtomicWrite(PathFor(entry.Owner!, entry.PackageName!, version), ms.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every remembered version, keyed by <see cref="Key"/>. Empty when nothing was verified or the folder is unreadable.</summary>
    public Dictionary<string, Record> ReadAll()
    {
        var all = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(_root)) return all;
            foreach (string path in Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories))
            {
                var (key, record) = TryRead(path);
                if (record != null) all[key] = record;
            }
        }
        catch { /* a cache we cannot read is a cache miss */ }

        return all;
    }

    /// <summary>One remembered version (<see cref="PathFor"/>), or null when it was never verified or cannot be read.</summary>
    public Record? Find(string? ns, string? name, string? version)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) return null;
            string path = PathFor(ns!, name!, version!);
            return File.Exists(path) ? TryRead(path).Record : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Mod compatibility C3: the effective peer range of package <paramref name="packageKey"/> (<c>Owner-Name</c>, as the
    /// install ledger and the update list key it) at <paramref name="version"/>, from its verified inspection; null when
    /// that version was never inspected, or the inspection could not read the plugin image.
    /// </summary>
    public string? EffectivePeersOf(string? packageKey, string? version)
    {
        string key = (packageKey ?? "").Trim();
        int dash = key.LastIndexOf('-');
        if (dash <= 0 || dash >= key.Length - 1) return null;
        return Find(key.Substring(0, dash), key.Substring(dash + 1), (version ?? "").Trim())?.EffectivePeers;
    }

    private static (string Key, Record? Record) TryRead(string path)
    {
        try
        {
            using var doc = JsonFiles.ParseFile(path);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("", null);
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != Schema)
                return ("", null);
            string ns = Read(root, "namespace"), name = Read(root, "name"), version = Read(root, "version");
            string id = Read(root, "id");
            if (ns.Length == 0 || name.Length == 0 || version.Length == 0 || id.Length == 0) return ("", null);
            long? size = root.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
            var manifest = new ManifestMod(
                id,
                Read(root, "manifestVersion"),
                (NetworkCompatibilityLevel)ReadInt(root, "level"),
                (NetworkVersionRule)ReadInt(root, "versionRule"),
                root.TryGetProperty("hotLoadable", out var hot) && hot.ValueKind == JsonValueKind.True,
                Read(root, "manifestNamespace"),
                Read(root, "manifestPackage"),
                Read(root, "sha256"),
                size,
                Read(root, "url"))
            {
                Peers = Read(root, "peers").Length > 0 ? Read(root, "peers") : null,
            };
            return (Key(ns, name, version), new Record(manifest, ParseSide(Read(root, "side")), Read(root, "effectivePeers")));
        }
        catch
        {
            return ("", null);
        }
    }

    internal static string SideName(PackageSide side) => side switch
    {
        PackageSide.Client => "client",
        PackageSide.Server => "server",
        PackageSide.Both => "both",
        _ => "unknown",
    };

    internal static PackageSide ParseSide(string value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "client" => PackageSide.Client,
        "server" => PackageSide.Server,
        "both" => PackageSide.Both,
        _ => PackageSide.Unknown,
    };

    private static string Read(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
}
