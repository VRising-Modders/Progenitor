using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Progenitor.Install;

namespace Progenitor.Ui;

/// <summary>
/// Read-only file access for Steam library discovery. Production uses the live disk;
/// tests inject a fixture so discovery never touches a game install.
/// </summary>
internal interface ISteamLibraryFileSystem
{
    IReadOnlyList<string> SteamRoots();

    bool FileExists(string path);

    bool DirectoryExists(string path);

    string? ReadText(string path);
}

/// <summary>Live disk. Steam roots are the common Program Files locations, never a developer path.</summary>
internal sealed class LiveSteamLibraryFileSystem : ISteamLibraryFileSystem
{
    public IReadOnlyList<string> SteamRoots()
    {
        var list = new List<string>(2);
        Add(list, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(list, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        return list;
    }

    public bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    public bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    public string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static void Add(List<string> list, string? programFiles)
    {
        if (string.IsNullOrWhiteSpace(programFiles)) return;
        string steam = Path.Combine(programFiles, "Steam");
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], steam, StringComparison.OrdinalIgnoreCase)) return;
        }

        list.Add(steam);
    }
}

/// <summary>
/// Discovery sources beyond the Program Files guess (user, 2026-10-01: "is there a registry we can
/// check?"). All read-only; tests inject a fake so no test reads the real registry or process list.
/// </summary>
internal interface IServerDiscoverySources
{
    /// <summary>Steam roots from the registry: HKCU <c>SteamPath</c>, then HKLM (WOW6432Node and native) <c>InstallPath</c>.</summary>
    IReadOnlyList<string> RegistrySteamRoots();

    /// <summary><c>InstallLocation</c> of the uninstall key <c>Steam App 1829350</c> (native and WOW6432Node).</summary>
    IReadOnlyList<string> UninstallLocations();

    /// <summary>The folder of every running <c>VRisingServer.exe</c>, from its full image path.</summary>
    IReadOnlyList<string> RunningServerFolders();
}

/// <summary>No extra sources (a scan built over a test file system).</summary>
internal sealed class NoServerDiscoverySources : IServerDiscoverySources
{
    public static readonly NoServerDiscoverySources Instance = new();
    public IReadOnlyList<string> RegistrySteamRoots() => Array.Empty<string>();
    public IReadOnlyList<string> UninstallLocations() => Array.Empty<string>();
    public IReadOnlyList<string> RunningServerFolders() => Array.Empty<string>();
}

/// <summary>The live registry and process list, read-only, every read guarded.</summary>
internal sealed class LiveServerDiscoverySources : IServerDiscoverySources
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamLibraryScan.DedicatedServerAppId;
    private const string UninstallKeyWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamLibraryScan.DedicatedServerAppId;

    public IReadOnlyList<string> RegistrySteamRoots()
    {
        var list = new List<string>(3);
        Add(list, Read(true, @"Software\Valve\Steam", "SteamPath"));
        Add(list, Read(false, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
        Add(list, Read(false, @"SOFTWARE\Valve\Steam", "InstallPath"));
        return list;
    }

    public IReadOnlyList<string> UninstallLocations()
    {
        var list = new List<string>(2);
        Add(list, Read(false, UninstallKey, "InstallLocation"));
        Add(list, Read(false, UninstallKeyWow, "InstallLocation"));
        return list;
    }

    public IReadOnlyList<string> RunningServerFolders()
    {
        var list = new List<string>();
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("VRisingServer"))
            {
                try
                {
                    // HOSTPROBE: QueryFullProcessImageName, not MainModule (a module-list walk per
                    // process, on the main thread every 30 s from the Mods menu's picker inputs).
                    string? path = ImagePath(proc.Id);
                    if (!string.IsNullOrWhiteSpace(path)) Add(list, Path.GetDirectoryName(path));
                }
                catch { /* access denied for another user's process: skip it */ }
                finally
                {
                    try { proc.Dispose(); } catch { /* */ }
                }
            }
        }
        catch { /* no process list: no suggestion */ }

        return list;
    }

    private static string? ImagePath(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) && size > 0 ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, System.Text.StringBuilder name, ref int size);

    private static string? Read(bool currentUser, string key, string value) =>
        OperatingSystem.IsWindows() ? ReadWindows(currentUser, key, value) : null;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadWindows(bool currentUser, string key, string value)
    {
        try
        {
            var hive = currentUser ? Microsoft.Win32.Registry.CurrentUser : Microsoft.Win32.Registry.LocalMachine;
            using var k = hive.OpenSubKey(key, writable: false);
            return k?.GetValue(value) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void Add(List<string> list, string? path)
    {
        string p = SteamLibraryScan.NormalizeRoot(path);
        if (p.Length == 0) return;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], p, StringComparison.OrdinalIgnoreCase)) return;
        list.Add(p);
    }
}

/// <summary>One suggested folder, where it was found, and what it holds. Never applied by itself.</summary>
internal sealed class ServerFolderSuggestion
{
    public ServerFolderSuggestion(string path, string source, bool hasBepInEx, bool hasLaunchRecord, bool running)
    {
        Path = path ?? "";
        Source = source ?? "";
        HasBepInEx = hasBepInEx;
        HasLaunchRecord = hasLaunchRecord;
        Running = running;
    }

    public string Path { get; }

    /// <summary><c>Steam</c>, <c>Steam (registry)</c>, <c>Steam (uninstall entry)</c> or <c>running now</c>.</summary>
    public string Source { get; }

    public bool HasBepInEx { get; }
    public bool HasLaunchRecord { get; }
    public bool Running { get; }

    /// <summary>The line under <c>Found: …</c>: where it came from and what it has.</summary>
    public string Detail =>
        Source + (Running && Source != SteamLibraryScan.SourceRunning ? " · " + SteamLibraryScan.SourceRunning : "")
        + " · " + (HasBepInEx ? "BepInEx installed" : "no BepInEx yet")
        + (HasLaunchRecord ? " · started by Progenitor before" : "");
}

/// <summary>
/// Suggest-only dedicated-server folder discovery. Parses Steam
/// <c>steamapps/libraryfolders.vdf</c> and the common
/// <c>VRisingDedicatedServer</c> root. Never writes, never registers a folder.
/// </summary>
internal sealed class SteamLibraryScan
{
    public const string LibrariesVdfRelative = @"steamapps\libraryfolders.vdf";
    public const string DedicatedServerRelative = @"steamapps\common\VRisingDedicatedServer";
    public const string DedicatedServerFolderName = "VRisingDedicatedServer";
    public const int MaxCandidates = 3;

    /// <summary>V Rising Dedicated Server's Steam app id.</summary>
    public const string DedicatedServerAppId = "1829350";
    public const string AppManifestName = "appmanifest_" + DedicatedServerAppId + ".acf";

    public const string SourceSteam = "Steam";
    public const string SourceSteamRegistry = "Steam (registry)";
    public const string SourceUninstall = "Steam (uninstall entry)";
    public const string SourceRunning = "running now";

    private static readonly Regex InstallDirEntry = new(
        "\"installdir\"\\s+\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PathEntry = new(
        "\"path\"\\s+\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ISteamLibraryFileSystem _fs;
    private readonly IServerDiscoverySources _sources;

    /// <param name="fs">Null: the live disk.</param>
    /// <param name="sources">
    /// Null: the live registry and process list when <paramref name="fs"/> is live too, and none over a
    /// test file system — so no test ever reads the real registry or the machine's processes.
    /// </param>
    public SteamLibraryScan(ISteamLibraryFileSystem? fs = null, IServerDiscoverySources? sources = null)
    {
        _fs = fs ?? new LiveSteamLibraryFileSystem();
#if PROGENITOR_TESTS
        // The test build never reads the machine's registry or process list, whatever the caller passes.
        _sources = sources ?? NoServerDiscoverySources.Instance;
#else
        _sources = sources ?? (fs == null ? new LiveServerDiscoverySources() : NoServerDiscoverySources.Instance);
#endif
    }

    /// <summary>
    /// Validated dedicated-server folders, at most <see cref="MaxCandidates"/>.
    /// A hit is not applied; the Configuration tab offers it as a button.
    /// </summary>
    public IReadOnlyList<string> Suggest()
    {
        var detailed = SuggestDetailed();
        var hits = new List<string>(detailed.Count);
        for (int i = 0; i < detailed.Count; i++) hits.Add(detailed[i].Path);
        return hits;
    }

    /// <summary>
    /// The same suggestions with their source and contents: deduplicated by canonical path (a folder
    /// found twice keeps its first source, and is marked running when any source saw it running),
    /// each validated as Apply would validate it.
    /// </summary>
    public IReadOnlyList<ServerFolderSuggestion> SuggestDetailed()
    {
        var raw = DiscoverSourced();
        var running = new List<string>();
        for (int i = 0; i < raw.Count; i++)
            if (raw[i].Source == SourceRunning) running.Add(Canon(raw[i].Path));

        var hits = new List<ServerFolderSuggestion>(MaxCandidates);
        var seen = new List<string>();
        for (int i = 0; i < raw.Count && hits.Count < MaxCandidates; i++)
        {
            LocalServerFolderResult result;
            try { result = LocalServerFolder.ValidateSuggestion(raw[i].Path); }
            catch { continue; }
            if (!result.Ok || string.IsNullOrEmpty(result.CanonicalPath)) continue;
            string canon = result.CanonicalPath!;
            if (ContainsPath(seen, canon)) continue;
            seen.Add(canon);
            bool bep = false, record = false;
            try { bep = Progenitor.Install.ServerInstall.HasBepInExCore(canon); } catch { bep = false; }
            try { record = Progenitor.Install.ServerLaunchRecord.TryRead(canon) != null; } catch { record = false; }
            hits.Add(new ServerFolderSuggestion(canon, raw[i].Source, bep, record, ContainsPath(running, canon)));
        }

        return hits;
    }

    /// <summary>The <c>installdir</c> of an <c>appmanifest_*.acf</c> body, or null. Game-free parse.</summary>
    public static string? ParseInstallDir(string? acf)
    {
        if (string.IsNullOrWhiteSpace(acf)) return null;
        var match = InstallDirEntry.Match(acf);
        if (!match.Success) return null;
        string dir = match.Groups[1].Value.Trim();
        return dir.Length == 0 || dir.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || dir.Contains("..") ? null : dir;
    }

    /// <summary>A registry or vdf root as a full Windows path (<c>c:/program files (x86)/steam</c> → <c>c:\program files (x86)\steam</c>).</summary>
    public static string NormalizeRoot(string? raw)
    {
        string p = (raw ?? "").Trim().Trim('"').Replace('/', '\\');
        if (p.Length == 0) return "";
        try { return Path.GetFullPath(p).TrimEnd('\\'); }
        catch { return ""; }
    }

    private static string Canon(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return path ?? ""; }
    }

    /// <summary>Every raw candidate with its source, in priority order: running, Steam (registry), Steam, uninstall entry.</summary>
    internal IReadOnlyList<(string Path, string Source)> DiscoverSourced()
    {
        var found = new List<(string Path, string Source)>();
        void Add(string path, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            for (int i = 0; i < found.Count; i++)
                if (string.Equals(found[i].Path, path, StringComparison.OrdinalIgnoreCase) && found[i].Source == source) return;
            found.Add((path, source));
        }

        IReadOnlyList<string> Safe(Func<IReadOnlyList<string>> read)
        {
            try { return read() ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        foreach (string folder in Safe(_sources.RunningServerFolders)) Add(folder, SourceRunning);
        foreach (string root in Safe(_sources.RegistrySteamRoots))
            foreach (string path in LibraryCandidates(root)) Add(path, SourceSteamRegistry);
        foreach (string root in Safe(_fs.SteamRoots))
            foreach (string path in LibraryCandidates(root)) Add(path, SourceSteam);
        foreach (string location in Safe(_sources.UninstallLocations)) Add(location, SourceUninstall);
        return found;
    }

    /// <summary>The dedicated-server folders one Steam root names: its own and every library's, by app manifest and by the usual folder name.</summary>
    private IEnumerable<string> LibraryCandidates(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) yield break;
        var libraries = new List<string> { root };
        string vdfPath;
        try { vdfPath = Path.Combine(root, LibrariesVdfRelative); }
        catch { yield break; }
        string? vdf = _fs.FileExists(vdfPath) ? _fs.ReadText(vdfPath) : null;
        foreach (string library in ParseLibraryFolders(vdf))
            if (!ContainsPath(libraries, library)) libraries.Add(library);

        foreach (string library in libraries)
        {
            string? fromManifest = null;
            try
            {
                string acf = Path.Combine(library, "steamapps", AppManifestName);
                string? dir = _fs.FileExists(acf) ? ParseInstallDir(_fs.ReadText(acf)) : null;
                if (dir != null) fromManifest = Path.Combine(library, "steamapps", "common", dir);
            }
            catch { fromManifest = null; }

            if (fromManifest != null) yield return fromManifest;
            string usual = DedicatedServerPath(library);
            if (usual.Length > 0) yield return usual;
        }
    }

    /// <summary>Library folders named by a <c>libraryfolders.vdf</c> body. Game-free parse.</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string? vdf)
    {
        if (string.IsNullOrWhiteSpace(vdf)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (Match match in PathEntry.Matches(vdf))
        {
            string raw = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path = UnescapeVdfPath(raw);
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (ContainsPath(list, path)) continue;
            list.Add(path);
        }

        return list;
    }

    /// <summary>The raw candidate paths (any source), for callers that only want paths.</summary>
    internal IReadOnlyList<string> DiscoverRaw()
    {
        var sourced = DiscoverSourced();
        var list = new List<string>(sourced.Count);
        for (int i = 0; i < sourced.Count; i++)
            if (!ContainsPath(list, sourced[i].Path)) list.Add(sourced[i].Path);
        return list;
    }

    internal IReadOnlyList<string> DiscoverRawProgramFiles()
    {
        var found = new List<string>();
        var roots = _fs.SteamRoots() ?? Array.Empty<string>();
        for (int i = 0; i < roots.Count; i++)
        {
            string root = roots[i];
            if (string.IsNullOrWhiteSpace(root)) continue;
            AddCandidate(found, DedicatedServerPath(root));

            string vdfPath = Path.Combine(root, LibrariesVdfRelative);
            string? vdf = _fs.FileExists(vdfPath) ? _fs.ReadText(vdfPath) : null;
            var libraries = ParseLibraryFolders(vdf);
            for (int j = 0; j < libraries.Count; j++)
                AddCandidate(found, DedicatedServerPath(libraries[j]));
        }

        return found;
    }

    private void AddCandidate(List<string> found, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (ContainsPath(found, path)) return;
        found.Add(path);
    }

    private static string DedicatedServerPath(string libraryRoot)
    {
        try { return Path.Combine(libraryRoot, DedicatedServerRelative); }
        catch { return ""; }
    }

    private static string UnescapeVdfPath(string raw)
    {
        string path = raw.Trim().Replace('/', '\\');
        path = path.Replace(@"\\", @"\");
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static bool ContainsPath(IReadOnlyList<string> list, string path)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
