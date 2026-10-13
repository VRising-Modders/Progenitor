using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Progenitor.Install.Admission;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Install;

/// <summary>
/// Opens a downloaded zip without loading any assembly. Enumerates entries, reads
/// Thunderstore <c>manifest.json</c> and <c>progenitor.json</c> if present, hashes each DLL,
/// and refuses traversal, absolute paths, symlinks, duplicate destinations, expansion bombs,
/// reserved names, and (for placement) destinations outside the allowed plugin folders.
/// Zip hash and per-DLL hashes are separate, labeled values.
/// </summary>
internal static class PackageInspector
{
    public const int DefaultMaxEntries = 256;
    public const long DefaultMaxUncompressedBytes = 64L * 1024 * 1024;
    public const int DefaultMaxExpansionRatio = 100;
    public const int DefaultMaxJsonBytes = 256 * 1024;
    public const string ProgenitorJsonName = "progenitor.json";
    public const string ThunderstoreManifestName = "manifest.json";

    private const int UnixSymlinkMask = 0xF000;
    private const int UnixSymlinkType = 0xA000;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    public static PackageInspection InspectFile(string zipPath, string? expectedZipSha256 = null, PackageInspectOptions? options = null)
    {
        options ??= PackageInspectOptions.Default;
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return Refuse(InstallReasons.InspectNotZip, "", 0);

        long zipSize;
        string zipSha;
        try
        {
            zipSize = new FileInfo(zipPath).Length;
            zipSha = CatalogFetcher.HashFile(zipPath);
        }
        catch (Exception e)
        {
            return Refuse(InstallReasons.InspectNotZip + ": " + e.Message, "", 0);
        }

        if (!string.IsNullOrEmpty(expectedZipSha256)
            && !string.Equals(expectedZipSha256, zipSha, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(InstallReasons.ChangedArtifact, zipSha, zipSize);
        }

        try
        {
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return InspectArchive(zip, zipSha, zipSize, options);
        }
        catch (InvalidDataException e)
        {
            return Refuse(InstallReasons.InspectNotZip + ": " + e.Message, zipSha, zipSize);
        }
        catch (Exception e)
        {
            return Refuse(InstallReasons.InspectNotZip + ": " + e.Message, zipSha, zipSize);
        }
    }

    internal static PackageInspection InspectArchive(
        ZipArchive zip,
        string zipSha256,
        long zipSize,
        PackageInspectOptions options)
    {
        var entryNames = new List<string>();
        var dllHashes = new List<ArtifactHash>();
        var dllBytes = new List<KeyValuePair<string, ArtifactBytes>>();
        var fileDestinations = new List<string>();
        bool ordinaryScannable = true;
        var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long uncompressed = 0;
        long compressed = 0;
        int count = 0;
        bool unsupportedLayout = false;
        string? layoutReason = null;
        ZipArchiveEntry? progenitorEntry = null;
        ZipArchiveEntry? tsManifestEntry = null;

        foreach (var entry in zip.Entries)
        {
            count++;
            if (count > options.MaxEntries)
                return Refuse(InstallReasons.InspectTooManyEntries, zipSha256, zipSize);

            string rawName = entry.FullName ?? "";
            entryNames.Add(rawName);

            if (IsDirectory(entry)) continue;

            if (IsSymlink(entry))
                return Refuse(InstallReasons.InspectSymlink, zipSha256, zipSize, entryNames, zipSize);

            if (!TryNormalizeEntryPath(rawName, out var relative, out var pathError))
                return Refuse(pathError!, zipSha256, zipSize, entryNames, zipSize);

            uncompressed += entry.Length;
            compressed += entry.CompressedLength;
            if (uncompressed > options.MaxUncompressedBytes)
                return Refuse(InstallReasons.InspectExpansion, zipSha256, zipSize, entryNames, uncompressed);
            if (compressed > 0 && uncompressed / Math.Max(compressed, 1) > options.MaxExpansionRatio
                && uncompressed > 1024 * 1024)
                return Refuse(InstallReasons.InspectExpansion, zipSha256, zipSize, entryNames, uncompressed);

            string destination = relative.Replace('\\', '/');
            if (destinations.ContainsKey(destination))
                return Refuse(InstallReasons.DuplicateNamed(destination), zipSha256, zipSize, entryNames, uncompressed);
            destinations[destination] = rawName;

            fileDestinations.Add(destination);

            string leaf = Leaf(destination);
            if (ModFolders.IsReservedFileName(leaf))
                return Refuse(InstallReasons.ReservedNamed(leaf), zipSha256, zipSize, entryNames, uncompressed);

            if (IsDisallowedPluginDestination(destination))
            {
                unsupportedLayout = true;
                layoutReason ??= InstallReasons.PluginsLayoutBlockedAt(destination);
            }

            if (string.Equals(destination, ProgenitorJsonName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(leaf, ProgenitorJsonName, StringComparison.OrdinalIgnoreCase) && IsZipRoot(destination))
                progenitorEntry = entry;
            else if (string.Equals(destination, ThunderstoreManifestName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(leaf, ThunderstoreManifestName, StringComparison.OrdinalIgnoreCase) && IsZipRoot(destination))
                tsManifestEntry = entry;

            if (destination.EndsWith(ModFolders.ExpectedExtension, StringComparison.OrdinalIgnoreCase)
                || destination.EndsWith(ModFolders.ExpectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Length > options.MaxUncompressedBytes)
                    return Refuse(InstallReasons.InspectExpansion, zipSha256, zipSize, entryNames, uncompressed);
                string dllSha;
                if (ordinaryScannable
                    && entry.Length <= OrdinaryPackage.MaxDllBytes
                    && dllBytes.Count < OrdinaryPackage.MaxDlls)
                {
                    // INSTORD1: the bytes are read once, hashed, and handed to the non-executing
                    // identity scan. A DLL over the per-image ceiling (or one DLL too many) leaves
                    // the ordinary route unevaluated rather than half-evaluated.
                    byte[] entryBytes = ReadEntry(entry);
                    dllSha = HashBytes(entryBytes);
                    dllBytes.Add(new KeyValuePair<string, ArtifactBytes>(destination, new ArtifactBytes(entryBytes, dllSha)));
                }
                else
                {
                    ordinaryScannable = false;
                    dllSha = HashEntry(entry);
                }

                dllHashes.Add(new ArtifactHash(InstallReasons.DllHashKind, leaf, dllSha, entry.Length));
            }
        }

        if (compressed > 0 && uncompressed > options.MaxUncompressedBytes)
            return Refuse(InstallReasons.InspectExpansion, zipSha256, zipSize, entryNames, uncompressed);

        // INSTORD1: the ordinary-package verdict. A refusal here never installs anything; the
        // planner keeps its own existing refusal unless this one approves.
        OrdinaryPackageLayout ordinary = ordinaryScannable
            ? OrdinaryPackage.Evaluate(fileDestinations, dllBytes)
            : OrdinaryPackageLayout.Refuse(InstallReasons.OrdinaryTooManyDlls);

        string? tsName = null;
        bool hasTs = false;
        if (tsManifestEntry != null)
        {
            hasTs = true;
            if (TryReadJsonObject(tsManifestEntry, options.MaxJsonBytes, out var tsJson, out _) && tsJson != null)
            {
                using (tsJson)
                    tsName = ReadString(tsJson.RootElement, "name");
            }
        }

        if (progenitorEntry == null)
        {
            return new PackageInspection(
                ok: true,
                refusal: null,
                provenance: CatalogProvenance.AdaptedListing,
                zipSha256: zipSha256,
                zipSize: zipSize,
                dllHashes: dllHashes,
                progenitorManifest: null,
                side: PackageSide.Unknown,
                hotLoadableDeclared: false,
                hasProgenitorJson: false,
                hasThunderstoreManifest: hasTs,
                unsupportedLayout: unsupportedLayout,
                layoutReason: layoutReason,
                entryNames: entryNames,
                pluginDependencies: Array.Empty<PluginDependency>(),
                thunderstoreName: tsName,
                ordinaryLayout: ordinary);
        }

        if (!TryReadJsonObject(progenitorEntry, options.MaxJsonBytes, out var doc, out var jsonError)
            || doc == null)
        {
            return Refuse(InstallReasons.InspectBadProgenitorJson + (jsonError == null ? "" : ": " + jsonError),
                zipSha256, zipSize, entryNames, uncompressed, dllHashes);
        }

        using (doc)
        {
            if (!TryParseProgenitorJson(doc.RootElement, out var parsed, out var parseError))
                return Refuse(parseError ?? InstallReasons.InspectContradictory, zipSha256, zipSize, entryNames, uncompressed, dllHashes);

            // Mod compatibility C3: compatibility.peers must agree with the image's [NetworkCompatibility] Peers, as side
            // agrees with [ProgenitorSide]. Checked when the plugin image is among the DLLs read here.
            var compat = ordinaryScannable ? PluginCompatOf(dllBytes, parsed.Manifest.Id) : null;
            if (compat != null && !PeersAgree(parsed.Peers, compat.DeclaredPeers))
                return Refuse(InstallReasons.InspectPeersContradict(parsed.Peers, compat.DeclaredPeers),
                    zipSha256, zipSize, entryNames, uncompressed, dllHashes);
            string? effectivePeers = compat != null && compat.EffectiveRange.Length > 0 ? compat.EffectiveRange
                : parsed.Peers.Length > 0 ? parsed.Peers : null;

            if (string.IsNullOrEmpty(parsed.Sha256))
            {
                // Present but no hash of installable bytes: still a listing, not first-class.
                return new PackageInspection(
                    ok: true,
                    refusal: null,
                    provenance: CatalogProvenance.AdaptedListing,
                    zipSha256: zipSha256,
                    zipSize: zipSize,
                    dllHashes: dllHashes,
                    progenitorManifest: parsed.Manifest,
                    side: parsed.Side,
                    hotLoadableDeclared: parsed.HotLoadable,
                    hasProgenitorJson: true,
                    hasThunderstoreManifest: hasTs,
                    unsupportedLayout: unsupportedLayout,
                    layoutReason: layoutReason,
                    entryNames: entryNames,
                    pluginDependencies: parsed.Dependencies,
                    thunderstoreName: tsName,
                    ordinaryLayout: ordinary)
                {
                    DeclaredPeers = parsed.Peers,
                    EffectivePeers = effectivePeers,
                };
            }

            if (!HashMatchesInstallableBytes(parsed.Sha256, zipSha256, dllHashes, parsed.EntryAssemblies))
            {
                return Refuse(
                    InstallReasons.InspectHashMismatch,
                    zipSha256,
                    zipSize,
                    entryNames,
                    uncompressed,
                    dllHashes);
            }

            return new PackageInspection(
                ok: true,
                refusal: null,
                provenance: CatalogProvenance.ManifestVerified,
                zipSha256: zipSha256,
                zipSize: zipSize,
                dllHashes: dllHashes,
                progenitorManifest: parsed.Manifest,
                side: parsed.Side,
                hotLoadableDeclared: parsed.HotLoadable,
                hasProgenitorJson: true,
                hasThunderstoreManifest: hasTs,
                unsupportedLayout: unsupportedLayout,
                layoutReason: layoutReason,
                entryNames: entryNames,
                pluginDependencies: parsed.Dependencies,
                thunderstoreName: tsName,
                ordinaryLayout: ordinary)
            {
                DeclaredPeers = parsed.Peers,
                EffectivePeers = effectivePeers,
            };
        }
    }

    /// <summary>
    /// C3: the <c>[NetworkCompatibility]</c> declaration of the package's plugin image (the entry point whose GUID is
    /// progenitor.json's <c>id</c>, else the only entry point), or null when no read DLL has one.
    /// </summary>
    internal static PluginCompatFacts? PluginCompatOf(IReadOnlyList<KeyValuePair<string, ArtifactBytes>>? dlls, string? id)
    {
        var all = new List<PluginCompatFacts>();
        foreach (var kv in dlls ?? Array.Empty<KeyValuePair<string, ArtifactBytes>>())
        {
            if (kv.Value.Bytes.Length == 0) continue;
            all.AddRange(PluginCompatReader.Read(kv.Value.Bytes));
        }

        return PluginCompatReader.Pick(all, id);
    }

    /// <summary>
    /// C3: the two peer ranges say the same thing: both absent, or both present with the same text once whitespace is
    /// removed (<c>&gt;= 1.2.0 &lt;2.0.0</c> equals <c>&gt;=1.2.0 &lt;2.0.0</c>).
    /// </summary>
    internal static bool PeersAgree(string? json, string? image) =>
        string.Equals(StripSpace(json), StripSpace(image), StringComparison.OrdinalIgnoreCase);

    private static string StripSpace(string? text)
    {
        var sb = new StringBuilder();
        foreach (char c in text ?? "") if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    internal static bool TryNormalizeEntryPath(string rawName, out string relative, out string? error)
    {
        relative = "";
        error = null;
        string name = (rawName ?? "").Replace('\\', '/');
        if (name.Length == 0)
        {
            error = InstallReasons.InspectTraversal;
            return false;
        }

        if (name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("\\", StringComparison.Ordinal))
        {
            error = InstallReasons.InspectRooted;
            return false;
        }

        if (name.Length >= 2 && char.IsLetter(name[0]) && name[1] == ':')
        {
            error = InstallReasons.InspectAbsolute;
            return false;
        }

        if (name.StartsWith("//", StringComparison.Ordinal) || name.StartsWith("\\\\", StringComparison.Ordinal))
        {
            error = InstallReasons.InspectAbsolute;
            return false;
        }

        var parts = name.Split('/');
        var built = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                error = InstallReasons.InspectTraversal;
                return false;
            }
            if (part.IndexOf(':') >= 0)
            {
                error = InstallReasons.InspectAbsolute;
                return false;
            }
            built.Add(part);
        }

        if (built.Count == 0)
        {
            error = InstallReasons.InspectTraversal;
            return false;
        }

        relative = string.Join("/", built);
        return true;
    }

    internal static bool IsDisallowedPluginDestination(string relative)
    {
        string n = relative.Replace('\\', '/').TrimStart('/');
        if (StartsWithFolder(n, "BepInEx/plugins") || StartsWithFolder(n, "plugins"))
            return true;
        if (StartsWithFolder(n, "BepInEx/patchers") || StartsWithFolder(n, "patchers"))
            return true;
        if (StartsWithFolder(n, "BepInEx/core") || StartsWithFolder(n, "core"))
            return true;
        return false;
    }

    internal static bool IsAllowedReloadDestination(string relative)
    {
        string n = relative.Replace('\\', '/').TrimStart('/');
        if (n.IndexOf('/') < 0 && n.EndsWith(ModFolders.ExpectedExtension, StringComparison.OrdinalIgnoreCase))
            return !ModFolders.IsReservedFileName(n);
        const string reload = "BepInEx/progenitor-reload/";
        if (n.StartsWith(reload, StringComparison.OrdinalIgnoreCase))
        {
            string rest = n.Substring(reload.Length);
            return rest.IndexOf('/') < 0
                && rest.EndsWith(ModFolders.ExpectedExtension, StringComparison.OrdinalIgnoreCase)
                && !ModFolders.IsReservedFileName(rest);
        }
        return false;
    }

    private static bool StartsWithFolder(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsZipRoot(string destination) =>
        destination.IndexOf('/') < 0 && destination.IndexOf('\\') < 0;

    internal static bool IsDirectory(ZipArchiveEntry entry)
    {
        string name = entry.FullName ?? "";
        return name.EndsWith("/", StringComparison.Ordinal) || name.EndsWith("\\", StringComparison.Ordinal);
    }

    internal static bool IsSymlink(ZipArchiveEntry entry)
    {
        int unix = (entry.ExternalAttributes >> 16) & 0xFFFF;
        if ((unix & UnixSymlinkMask) == UnixSymlinkType) return true;
        // NTFS reparse / IO_REPARSE_TAG_SYMLINK in the low attributes is not a zip feature we
        // honor; an entry whose name is an absolute/rooted link is already refused as a path.
        return false;
    }

    private static string Leaf(string relative)
    {
        int slash = relative.LastIndexOf('/');
        return slash >= 0 ? relative.Substring(slash + 1) : relative;
    }

    internal static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    internal static string HashBytes(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return CatalogFetcher.ToHex(sha.ComputeHash(bytes));
    }

    private static string HashEntry(ZipArchiveEntry entry)
    {
        using var sha = SHA256.Create();
        using var stream = entry.Open();
        return CatalogFetcher.ToHex(sha.ComputeHash(stream));
    }

    private static bool TryReadJsonObject(
        ZipArchiveEntry entry,
        int maxBytes,
        out JsonDocument? doc,
        out string? error)
    {
        doc = null;
        error = null;
        if (entry.Length > maxBytes)
        {
            error = "json entry exceeds the cap";
            return false;
        }

        try
        {
            using var stream = entry.Open();
            using var bounded = new BoundedReadStream(stream, maxBytes);
            using var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
            string text = reader.ReadToEnd();
            doc = JsonDocument.Parse(text, JsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                doc = null;
                error = "json root is not an object";
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    internal static bool TryParseProgenitorJson(JsonElement root, out ProgenitorJson parsed, out string? error)
    {
        parsed = default;
        error = null;
        string id = ReadString(root, "id") ?? "";
        string version = ReadString(root, "version") ?? "";
        string? sha = ModManifestJson.NormalizeSha(ReadString(root, "sha256"));
        string? sideText = ReadString(root, "side");
        bool hot = ReadBool(root, "hotLoadable");
        string? ns = ReadString(root, "namespace");
        string? package = ReadString(root, "package");
        long? size = ReadInt64(root, "size");
        string? url = ReadString(root, "url");

        PackageSide side = PackageSide.Unknown;
        if (!string.IsNullOrWhiteSpace(sideText))
        {
            if (!TryParseSide(sideText, out side))
            {
                error = InstallReasons.InspectContradictory;
                return false;
            }
        }

        if (hot && string.IsNullOrWhiteSpace(id))
        {
            error = InstallReasons.InspectContradictory;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(id) && !ModManifestIds.TryValidateId(id, out _))
        {
            error = InstallReasons.InspectContradictory;
            return false;
        }

        var deps = new List<PluginDependency>();
        if (root.TryGetProperty("dependencies", out var depsEl) && depsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in depsEl.EnumerateArray())
            {
                if (d.ValueKind == JsonValueKind.Object)
                {
                    string depId = ReadString(d, "id") ?? "";
                    string depVer = ReadString(d, "version") ?? "";
                    if (depId.Length == 0)
                    {
                        error = InstallReasons.InspectContradictory;
                        return false;
                    }
                    deps.Add(new PluginDependency(depId, depVer));
                }
                else if (d.ValueKind == JsonValueKind.String)
                {
                    // A string here is a foreign listing id, not a plugin GUID. Refuse to treat it as one.
                    error = InstallReasons.InspectContradictory;
                    return false;
                }
            }
        }

        var assemblies = new List<string>();
        ReadStringArray(root, "entryAssemblies", assemblies);
        ReadStringArray(root, "files", assemblies);

        // Mod compatibility C3: "compatibility": { "peers": ">=1.2.0 <2.0.0" } (optional). An invalid range refuses.
        string peers = "";
        if (root.TryGetProperty("compatibility", out var compatEl) && compatEl.ValueKind != JsonValueKind.Null)
        {
            if (compatEl.ValueKind != JsonValueKind.Object)
            {
                error = InstallReasons.InspectContradictory + ": compatibility is not an object";
                return false;
            }

            if (compatEl.TryGetProperty("peers", out var peersEl) && peersEl.ValueKind != JsonValueKind.Null)
            {
                if (peersEl.ValueKind != JsonValueKind.String)
                {
                    error = InstallReasons.InspectBadPeers(peersEl.GetRawText(), "it is not a string");
                    return false;
                }

                peers = (peersEl.GetString() ?? "").Trim();
                if (peers.Length > 0 && !CompatRange.TryParse(peers, out _, out var rangeError))
                {
                    error = InstallReasons.InspectBadPeers(peers, rangeError);
                    return false;
                }
            }
        }

        var manifest = new ManifestMod(
            id,
            version,
            NetworkCompatibilityLevel.NoNeedForSync,
            NetworkVersionRule.Exact,
            hot,
            ns,
            package,
            sha,
            size,
            url)
        {
            Peers = peers.Length > 0 ? peers : null,
        };

        parsed = new ProgenitorJson(manifest, side, hot, sha, deps, assemblies, peers);
        return true;
    }

    internal static bool TryParseSide(string text, out PackageSide side)
    {
        side = PackageSide.Unknown;
        if (string.Equals(text, "client", StringComparison.OrdinalIgnoreCase))
        {
            side = PackageSide.Client;
            return true;
        }
        if (string.Equals(text, "server", StringComparison.OrdinalIgnoreCase))
        {
            side = PackageSide.Server;
            return true;
        }
        if (string.Equals(text, "both", StringComparison.OrdinalIgnoreCase))
        {
            side = PackageSide.Both;
            return true;
        }
        return false;
    }

    /// <summary>
    /// LOCALSIDE1: the side the root <c>progenitor.json</c> of an open package declares, read by the
    /// inspector's own rules (the zip-root entry only, bounded to <paramref name="maxJsonBytes"/>, parsed
    /// with <see cref="TryParseProgenitorJson"/>) and nothing else: no hashes, no layout checks. Unknown when
    /// there is none, it is too big or it does not parse. Never throws. A listing hint before inspection,
    /// never a verdict: the full inspection still decides and replaces it.
    /// </summary>
    internal static PackageSide ReadDeclaredSide(ZipArchive archive, int maxJsonBytes = DefaultMaxJsonBytes)
    {
        if (archive == null) return PackageSide.Unknown;
        try
        {
            int count = 0;
            foreach (var entry in archive.Entries)
            {
                if (++count > DefaultMaxEntries) return PackageSide.Unknown;
                if (IsDirectory(entry) || IsSymlink(entry)) continue;
                if (!TryNormalizeEntryPath(entry.FullName ?? "", out var relative, out _)) continue;
                string destination = relative.Replace('\\', '/');
                if (!IsZipRoot(destination) || !string.Equals(destination, ProgenitorJsonName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!TryReadJsonObject(entry, maxJsonBytes, out var doc, out _) || doc == null) return PackageSide.Unknown;
                using (doc)
                    return TryParseProgenitorJson(doc.RootElement, out var parsed, out _) ? parsed.Side : PackageSide.Unknown;
            }
        }
        catch
        {
            // An unreadable archive declares nothing.
        }

        return PackageSide.Unknown;
    }

    private static bool HashMatchesInstallableBytes(
        string expected,
        string zipSha,
        IReadOnlyList<ArtifactHash> dllHashes,
        IReadOnlyList<string> entryAssemblies)
    {
        if (string.Equals(expected, zipSha, StringComparison.OrdinalIgnoreCase))
            return true;

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entryAssemblies.Count; i++)
        {
            string leaf = Leaf(entryAssemblies[i].Replace('\\', '/'));
            if (leaf.Length > 0) wanted.Add(leaf);
        }

        for (int i = 0; i < dllHashes.Count; i++)
        {
            if (!string.Equals(expected, dllHashes[i].Sha256, StringComparison.OrdinalIgnoreCase))
                continue;
            if (wanted.Count == 0) return true;
            if (wanted.Contains(dllHashes[i].FileName)) return true;
        }
        return false;
    }

    private static void ReadStringArray(JsonElement root, string name, List<string> into)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            string? s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) into.Add(s.Trim());
        }
    }

    private static string? ReadString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Null || p.ValueKind == JsonValueKind.Undefined) return null;
        if (p.ValueKind == JsonValueKind.String) return p.GetString();
        return null;
    }

    private static bool ReadBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static long? ReadInt64(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return null;
        if (p.TryGetInt64(out long n)) return n < 0 ? null : n;
        return null;
    }

    private static PackageInspection Refuse(
        string reason,
        string zipSha,
        long zipSize,
        IReadOnlyList<string>? names = null,
        long uncompressed = 0,
        IReadOnlyList<ArtifactHash>? dlls = null) =>
        new(
            ok: false,
            refusal: reason,
            provenance: CatalogProvenance.AdaptedListing,
            zipSha256: zipSha,
            zipSize: zipSize,
            dllHashes: dlls ?? Array.Empty<ArtifactHash>(),
            progenitorManifest: null,
            side: PackageSide.Unknown,
            hotLoadableDeclared: false,
            hasProgenitorJson: false,
            hasThunderstoreManifest: false,
            unsupportedLayout: false,
            layoutReason: null,
            entryNames: names ?? Array.Empty<string>(),
            pluginDependencies: Array.Empty<PluginDependency>(),
            thunderstoreName: null);

    internal readonly struct ProgenitorJson
    {
        public ProgenitorJson(
            ManifestMod manifest,
            PackageSide side,
            bool hotLoadable,
            string? sha256,
            IReadOnlyList<PluginDependency> dependencies,
            IReadOnlyList<string> entryAssemblies,
            string peers = "")
        {
            Manifest = manifest;
            Side = side;
            HotLoadable = hotLoadable;
            Sha256 = sha256;
            Dependencies = dependencies;
            EntryAssemblies = entryAssemblies;
            Peers = peers ?? "";
        }

        /// <summary>C3: <c>compatibility.peers</c>, trimmed; empty when not declared.</summary>
        public string Peers { get; }

        public ManifestMod Manifest { get; }
        public PackageSide Side { get; }
        public bool HotLoadable { get; }
        public string? Sha256 { get; }
        public IReadOnlyList<PluginDependency> Dependencies { get; }
        public IReadOnlyList<string> EntryAssemblies { get; }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        private long _read;

        public BoundedReadStream(Stream inner, long max)
        {
            _inner = inner;
            _max = max;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= _max) return 0;
            int take = (int)Math.Min(count, _max - _read);
            int n = _inner.Read(buffer, offset, take);
            _read += n;
            return n;
        }
    }
}

internal sealed class PackageInspectOptions
{
    public static PackageInspectOptions Default { get; } = new();

    public int MaxEntries { get; init; } = PackageInspector.DefaultMaxEntries;
    public long MaxUncompressedBytes { get; init; } = PackageInspector.DefaultMaxUncompressedBytes;
    public int MaxExpansionRatio { get; init; } = PackageInspector.DefaultMaxExpansionRatio;
    public int MaxJsonBytes { get; init; } = PackageInspector.DefaultMaxJsonBytes;
}
