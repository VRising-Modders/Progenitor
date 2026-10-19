using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Progenitor.Persistence;

namespace Progenitor.Install;

/// <summary>
/// MODS2: one uninstall request, as it sits on disk in
/// <c>BepInEx/progenitor-uninstall.json</c>. <see cref="Files"/> are relative to the BepInEx
/// root with forward slashes.
/// </summary>
internal sealed class ClientUninstallRequest
{
    public ClientUninstallRequest(DateTime requestedUtc, IReadOnlyList<string> files)
    {
        RequestedUtc = requestedUtc;
        Files = files ?? Array.Empty<string>();
    }

    public int Schema => ClientUninstall.CurrentSchema;
    public DateTime RequestedUtc { get; }
    public IReadOnlyList<string> Files { get; }
}

/// <summary>
/// MODS2, the client half of <c>Uninstall Progenitor from this client</c>. This type only ever
/// writes, reads or deletes the marker file: nothing is removed while the game runs, because the
/// files being removed are the ones this process has loaded. Bootstrap honors the marker at the
/// next start, before Core loads (its mirror is <c>Progenitor.Bootstrap.UninstallAtStart</c> —
/// Bootstrap and Core cannot reference each other, the same reason <c>ReloaderHost</c> mirrors
/// <c>PluginReloader</c>; the schema and the two file names must stay in step).
///
/// <para>Every path in the marker is relative to the BepInEx root and is checked, on write and
/// again on read, against <see cref="IsAllowedRelativePath"/>: nothing absolute, nothing that
/// walks up, and never <c>core/</c>. A marker that carries one bad path is refused whole rather
/// than partly applied.</para>
/// </summary>
internal static class ClientUninstall
{
    public const int CurrentSchema = 1;

    /// <summary>The request Bootstrap reads at start. Lives directly under the BepInEx root.</summary>
    public const string FileName = "progenitor-uninstall.json";

    /// <summary>
    /// Written by Bootstrap once it has honored a request. It is the evidence that the removal
    /// ran even though Bootstrap's own DLL can only be deleted at the next reboot.
    /// </summary>
    public const string DoneFileName = "progenitor-uninstall.done";

    /// <summary>BepInEx's own folder. Never written and never deleted by Progenitor.</summary>
    public const string LoaderCoreFolder = "core";

    /// <summary>Folders under the BepInEx root that belong to Progenitor alone.</summary>
    public static readonly IReadOnlyList<string> OwnedFolders = new[]
    {
        "progenitor-core",
        InstalledState.PendingFolder,
        InstalledState.ReloadFolder,
        ListingDiskCache.CacheFolder,
        "progenitor-staging",
        "progenitor-hosted",
    };

    /// <summary>Files under <c>BepInEx/plugins/</c> that are Progenitor's own plugins.</summary>
    public static readonly IReadOnlyList<string> OwnedPluginFileNames = new[]
    {
        Hosted.Staging.Layout.BootstrapDllName,
        Hosted.Staging.Layout.BootstrapDllName + Hosted.Staging.Layout.NextSuffix,
        Hosted.Staging.Layout.BootstrapDllName + Hosted.Staging.Layout.PrevSuffix,
        "Progenitor.Dev.dll",
        "Progenitor.Sample.dll",
        "Progenitor.ReloadExample.dll",
    };

    /// <summary>
    /// Progenitor's files under <c>BepInEx/patchers/</c>: the preloader, and the package archive a mod-manager install
    /// carries Core's folder in (the preloader's <c>PackagePayload.FileName</c>). Left behind, the archive would put Core
    /// back at the next start.
    /// </summary>
    public static readonly IReadOnlyList<string> OwnedPatcherFileNames = new[]
    {
        "Progenitor.Preloader.dll",
        "progenitor-files.zip",
    };

    /// <summary>BepInEx's patcher folder, beside <see cref="InstalledState.PluginsFolder"/>.</summary>
    public const string PatchersFolder = "patchers";

    public const string PromptHeading = "Remove Progenitor from this client?";
    public const string PromptPrefix =
        "At the next start of V Rising, Progenitor removes itself, every mod it installed";
    public const string PromptSuffix =
        "its ledger and caches. Mods installed by other tools and BepInEx stay. The game starts without Progenitor.";

    internal static Func<DateTime>? ClockUtc { get; set; }

    /// <summary>The prompt body: the count, the paths, and what is kept.</summary>
    public static string PromptText(IReadOnlyList<string>? files)
    {
        int count = files?.Count ?? 0;
        var sb = new StringBuilder();
        sb.Append(PromptPrefix);
        sb.Append(" (").Append(count.ToString(CultureInfo.InvariantCulture));
        sb.Append(count == 1 ? " file: " : " files: ");
        if (files != null) sb.Append(string.Join(", ", files));
        sb.Append("), ");
        sb.Append(PromptSuffix);
        return sb.ToString();
    }

    /// <summary>
    /// Everything this client would remove: the ledgered mod / pending / reload files, Core,
    /// Bootstrap, the dev bridge and samples, the caches, the install ledger and the marker
    /// itself. Only paths that exist now are listed, so the prompt names real files; the marker
    /// is always last because it is written after the plan is made.
    /// </summary>
    public static IReadOnlyList<string> Plan(string? bepInExRoot, IReadOnlyList<InstalledLedgerRow>? ledger = null)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root = (bepInExRoot ?? "").Trim();
        if (root.Length == 0) return files;

        void Add(string? relative)
        {
            if (!IsAllowedRelativePath(relative)) return;
            string rel = Normalize(relative);
            if (rel.Length == 0) return;
            if (seen.Add(rel)) files.Add(rel);
        }

        // The ledger first: a mod Progenitor installed anywhere under this root is named by it.
        if (ledger != null)
            foreach (var row in ledger)
            {
                if (row.FilePath.Length == 0) continue;
                if (!string.Equals(row.Root, InstalledState.ClientRoot, StringComparison.OrdinalIgnoreCase)) continue;
                string? rel = TryRelative(root, row.FilePath);
                if (rel != null) Add(rel);
            }

        // Progenitor's own plugins.
        for (int i = 0; i < OwnedPluginFileNames.Count; i++)
        {
            string rel = InstalledState.PluginsFolder + "/" + OwnedPluginFileNames[i];
            if (Exists(root, rel)) Add(rel);
        }

        // A mod manager puts the package in a folder of its own (plugins/<Package>/, patchers/<Package>/): Bootstrap there,
        // and in patchers the preloader and the archive it unpacks Core from. One level down, Progenitor's file names only
        // (in plugins, the first three: Bootstrap and its staged copies).
        foreach (string folder in Subfolders(root, InstalledState.PluginsFolder))
        {
            bool progenitorPackage = false;
            for (int i = 0; i < 3; i++)
            {
                string rel = folder + "/" + OwnedPluginFileNames[i];
                if (!Exists(root, rel)) continue;
                Add(rel);
                progenitorPackage = true;
            }

            // An older package layout flattened Core in beside Bootstrap, where the chainloader would load it.
            if (progenitorPackage && Exists(root, folder + "/Progenitor.dll")) Add(folder + "/Progenitor.dll");
        }

        var patcherFolders = new List<string> { PatchersFolder };
        patcherFolders.AddRange(Subfolders(root, PatchersFolder));
        foreach (string folder in patcherFolders)
            for (int i = 0; i < OwnedPatcherFileNames.Count; i++)
            {
                string rel = folder + "/" + OwnedPatcherFileNames[i];
                if (Exists(root, rel)) Add(rel);
            }

        // Core, the pending and reload folders, the caches, staging and the hosted images.
        for (int i = 0; i < OwnedFolders.Count; i++)
            foreach (string rel in EnumerateFolder(root, OwnedFolders[i]))
                Add(rel);

        // The install ledger, then the marker this plan becomes.
        if (Exists(root, ServerInstallLedger.FileName)) Add(ServerInstallLedger.FileName);
        Add(FileName);
        return files;
    }

    /// <summary>The marker on disk, or null when absent, torn, a foreign schema, or unsafe.</summary>
    public static ClientUninstallRequest? TryRead(string? bepInExRoot)
    {
        string path = PathFor(bepInExRoot);
        if (path.Length == 0 || !File.Exists(path)) return null;
        try
        {
            using var doc = JsonFiles.ParseFile(path);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || schema.GetInt32() != CurrentSchema)
                return null;
            DateTime requested = DateTime.MinValue;
            if (root.TryGetProperty("requestedUtc", out var stamp) && stamp.ValueKind == JsonValueKind.String
                && DateTime.TryParse(stamp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                requested = parsed;
            var files = new List<string>();
            if (root.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    string raw = (item.GetString() ?? "").Trim();
                    if (raw.Length == 0) continue;
                    // A marker that names anything outside the BepInEx root is refused whole, on
                    // the spelling it carries.
                    if (!IsAllowedRelativePath(raw)) return null;
                    files.Add(Normalize(raw));
                }

            return new ClientUninstallRequest(requested, files);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Write the request. Refuses (and writes nothing) when the plan is empty or names a path
    /// outside the BepInEx root.
    /// </summary>
    public static bool TryWrite(string? bepInExRoot, IReadOnlyList<string> files)
    {
        string path = PathFor(bepInExRoot);
        if (path.Length == 0 || files == null || files.Count == 0) return false;
        // Checked on the spelling given, not on a normalized copy: normalizing first would strip
        // a leading "/" and make a rooted path look relative.
        for (int i = 0; i < files.Count; i++)
            if (!IsAllowedRelativePath(files[i])) return false;
        try
        {
            var now = (ClockUtc ?? (() => DateTime.UtcNow))();
            InstallStaging.AtomicWrite(path, Encoding.UTF8.GetBytes(ToJson(new ClientUninstallRequest(now, files))));
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Cancel: the request file is the whole state, so deleting it is the cancel.</summary>
    public static bool Cancel(string? bepInExRoot)
    {
        string path = PathFor(bepInExRoot);
        if (path.Length == 0 || !File.Exists(path)) return false;
        InstallStaging.TryDelete(path);
        return !File.Exists(path);
    }

    public static string PathFor(string? bepInExRoot) =>
        string.IsNullOrWhiteSpace(bepInExRoot) ? "" : Path.Combine(bepInExRoot!, FileName);

    public static string ToJson(ClientUninstallRequest request)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", CurrentSchema);
            w.WriteString("requestedUtc", request.RequestedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            w.WriteStartArray("files");
            for (int i = 0; i < request.Files.Count; i++) w.WriteStringValue(request.Files[i]);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// A path the marker may carry: relative, no drive, no rooted segment, no <c>..</c>, and never
    /// BepInEx's own <c>core/</c>. Mirrored in Bootstrap; both sides check it.
    /// </summary>
    public static bool IsAllowedRelativePath(string? relative)
    {
        string raw = (relative ?? "").Trim();
        // A rooted path ("/etc/hosts", a UNC share, "C:/...") is not relative to anything and
        // is refused on its raw spelling, before any normalization could strip the root away.
        if (raw.Length == 0 || raw.Length > 400) return false;
        if (raw[0] == '/' || raw[0] == '\\') return false;
        if (raw.Length > 1 && raw[1] == ':') return false;
        string rel = Normalize(raw);
        if (rel.Length == 0) return false;
        string[] parts = rel.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || part == "." || part == "..") return false;
        }

        return !string.Equals(parts[0], LoaderCoreFolder, StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string? relative) =>
        (relative ?? "").Trim().Replace('\\', '/').TrimStart('/');

    private static bool Exists(string root, string relative)
    {
        try { return File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); }
        catch { return false; }
    }

    /// <summary>The direct subfolders of <paramref name="folder"/>, as root-relative paths; links are skipped.</summary>
    private static IEnumerable<string> Subfolders(string root, string folder)
    {
        string[] found;
        try
        {
            string dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) yield break;
            found = Directory.GetDirectories(dir);
        }
        catch
        {
            yield break;
        }

        for (int i = 0; i < found.Length; i++)
        {
            bool link;
            try { link = (File.GetAttributes(found[i]) & FileAttributes.ReparsePoint) != 0; }
            catch { link = true; }
            if (link) continue;
            yield return folder + "/" + Path.GetFileName(found[i]);
        }
    }

    private static IEnumerable<string> EnumerateFolder(string root, string folder)
    {
        string dir;
        string[] found;
        try
        {
            dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) yield break;
            found = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        for (int i = 0; i < found.Length; i++)
        {
            string? rel = TryRelative(root, found[i]);
            if (rel != null) yield return rel;
        }
    }

    private static string? TryRelative(string root, string absolute)
    {
        try
        {
            string full = Path.GetFullPath(absolute);
            string baseFull = Path.GetFullPath(root);
            string rel = Normalize(Path.GetRelativePath(baseFull, full));
            return IsAllowedRelativePath(rel) ? rel : null;
        }
        catch
        {
            return null;
        }
    }
}
