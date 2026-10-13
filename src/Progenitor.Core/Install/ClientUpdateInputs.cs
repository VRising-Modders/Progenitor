using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Progenitor.Install;

/// <summary>
/// AUTOUPDATE1: reads detection's inputs from disk: the client root's install ledger (the packages Progenitor manages) and
/// the pin and server-requirement files. Game-free; every call does file IO, so the live caller runs it off the main
/// thread. A ledgered file whose bytes no longer match the ledger's sha256 marks its package <see cref="ManagedPackage.Changed"/>
/// (hashes are cached by path, size and write time, so a pass over unchanged files reads nothing twice).
/// </summary>
internal static class ClientUpdateInputs
{
    private static readonly object HashGate = new();
    private static readonly Dictionary<string, (long Length, DateTime Write, string Sha)> HashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ledger's managed packages under <paramref name="gameRoot"/> (the folder holding <c>BepInEx</c>).</summary>
    public static List<ManagedPackage> ReadManaged(string gameRoot)
    {
        var byKey = new Dictionary<string, ManagedPackage>(StringComparer.OrdinalIgnoreCase);
        var order = new List<ManagedPackage>();
        ServerInstallLedger? ledger;
        try { ledger = ServerInstallLedger.TryRead(gameRoot); }
        catch { ledger = null; }
        if (ledger == null) return order;
        foreach (var row in ledger.Files)
        {
            if (row == null || row.UninstallPending) continue;
            bool mod = string.Equals(row.Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal);
            bool pending = string.Equals(row.Category, ServerInstallLedger.CategoryPending, StringComparison.Ordinal);
            if (!mod && !pending) continue;
            if (!InstalledState.TrySplitPackageSource(row.Source, out string package, out string version)) continue;
            if (package.Length == 0 || version.Length == 0) continue;
            if (!byKey.TryGetValue(package, out var m))
            {
                m = new ManagedPackage { Key = package, Version = version, Pending = pending };
                byKey[package] = m;
                order.Add(m);
            }
            else
            {
                // Two versions ledgered at once (an update pending over a loaded copy): the newer one is what the
                // player will run, and what an update must beat.
                if (UpdateVersions.IsNewer(version, m.Version)) { m.Version = version; m.Pending = pending; }
                else if (!pending) m.Pending &= pending;
            }

            if (row.PluginId.Length > 0 && !m.PluginIds.Exists(id => string.Equals(id, row.PluginId, StringComparison.OrdinalIgnoreCase)))
                m.PluginIds.Add(row.PluginId);
            if (!FileMatches(gameRoot, row)) m.Changed = true;
        }

        return order;
    }

    private static bool FileMatches(string gameRoot, ServerInstallLedgerEntry row)
    {
        string path;
        try { path = ServerInstallLedger.Absolute(gameRoot, row.Path); }
        catch { return false; }
        if (row.Sha256.Length == 0) return File.Exists(path);
        string? sha = Sha256Of(path);
        return sha != null && string.Equals(sha, row.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? Sha256Of(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch { return null; }

        lock (HashGate)
        {
            if (HashCache.TryGetValue(info.FullName, out var hit) && hit.Length == info.Length && hit.Write == info.LastWriteTimeUtc)
                return hit.Sha;
        }

        string sha;
        try
        {
            using var stream = File.OpenRead(info.FullName);
            using var hasher = SHA256.Create();
            sha = BitConverter.ToString(hasher.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        catch { return null; }

        lock (HashGate)
        {
            if (HashCache.Count > 512) HashCache.Clear();
            HashCache[info.FullName] = (info.Length, info.LastWriteTimeUtc, sha);
        }

        return sha;
    }

    public static string PinsPath(string bepInExRoot) => Path.Combine(bepInExRoot, "config", UpdatePins.FileName);

    public static string RequirementsPath(string bepInExRoot) => Path.Combine(bepInExRoot, "config", ServerRequirementMemory.FileName);

    public static UpdatePins ReadPins(string bepInExRoot)
    {
        try
        {
            string path = PinsPath(bepInExRoot);
            lock (PinsGate) return File.Exists(path) ? UpdatePins.Parse(File.ReadAllText(path)) : new UpdatePins();
        }
        catch { return new UpdatePins(); }
    }

    public static bool WritePins(string bepInExRoot, UpdatePins pins, out string error) =>
        WriteAtomic(PinsPath(bepInExRoot), pins.ToJson(), out error);

    /// <summary>Review L1: the one lock every reader-modifier-writer of the auto-update file takes.</summary>
    internal static readonly object PinsGate = new();

    /// <summary>
    /// Review L1: read, change and write <c>progenitor-auto-update.json</c> under <see cref="PinsGate"/>, atomically (unique
    /// temp file, then replace). <paramref name="change"/> returns false to write nothing. A file that exists but does not
    /// parse is never written over: it is moved aside to <c>progenitor-auto-update.json.bad-&lt;utc&gt;</c> and logged, and
    /// the change starts from an empty file.
    /// </summary>
    internal static bool EditPins(string bepInExRoot, Func<UpdatePins, bool> change, Action<string>? log, out string error)
    {
        error = "";
        lock (PinsGate)
        {
            string path = PinsPath(bepInExRoot);
            UpdatePins pins;
            try
            {
                if (!File.Exists(path)) pins = new UpdatePins();
                else
                {
                    pins = UpdatePins.Parse(File.ReadAllText(path), out bool ok);
                    if (!ok)
                    {
                        string aside = path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                        File.Move(path, aside, overwrite: true);
                        try { log?.Invoke(UpdatePins.FileName + " could not be read; it was kept as " + Path.GetFileName(aside) + " and a new one is started"); }
                        catch { /* */ }
                    }
                }
            }
            catch (Exception e)
            {
                error = "could not read " + UpdatePins.FileName + ": " + e.Message;
                return false;
            }

            bool write;
            try { write = change(pins); }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }

            return !write || WriteAtomic(path, pins.ToJson(), out error);
        }
    }

    public static ServerRequirementMemory ReadRequirements(string bepInExRoot)
    {
        try
        {
            string path = RequirementsPath(bepInExRoot);
            return File.Exists(path) ? ServerRequirementMemory.Parse(File.ReadAllText(path)) : new ServerRequirementMemory();
        }
        catch { return new ServerRequirementMemory(); }
    }

    public static bool WriteRequirements(string bepInExRoot, ServerRequirementMemory memory, out string error) =>
        WriteAtomic(RequirementsPath(bepInExRoot), memory.ToJson(), out error);

    private static bool WriteAtomic(string path, string text, out string error)
    {
        error = "";
        string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
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
            catch { /* a stray temp file costs nothing */ }
        }
    }

    /// <summary>The index listings detection reads, keyed by listing key (one per key; the first wins, as the catalog's local merge does).</summary>
    public static Dictionary<string, IndexListing> ListingsOf(IReadOnlyList<CatalogEntry> entries, Func<string, bool>? isLocal)
    {
        var map = new Dictionary<string, IndexListing>(StringComparer.OrdinalIgnoreCase);
        if (entries == null) return map;
        foreach (var e in entries)
        {
            if (e == null) continue;
            string key = e.Key;
            if (string.IsNullOrEmpty(key) || map.ContainsKey(key)) continue;
            bool local;
            try { local = isLocal?.Invoke(key) ?? false; }
            catch { local = false; }
            map[key] = new IndexListing
            {
                Key = key,
                Name = DisplayName(e),
                Latest = e.Manifest?.Version ?? "",
                Deprecated = e.IsDeprecated,
                Nsfw = e.HasNsfwContent,
                Local = local,
                Dependencies = e.ForeignDependencies ?? (IReadOnlyList<string>)Array.Empty<string>(),
            };
        }

        return map;
    }

    private static string DisplayName(CatalogEntry e)
    {
        string name = e.DisplayName ?? "";
        if (name.Length == 0) name = e.PackageName ?? "";
        if (name.Length == 0) name = e.Key;
        return name.Replace('_', ' ');
    }
}
