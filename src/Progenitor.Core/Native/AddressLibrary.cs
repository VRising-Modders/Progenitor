using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Progenitor.Native;

/// <summary>
/// Resolves a stable metadata function id to a live address in the loaded GameAssembly.dll.
/// Native detours resolve through <see cref="DetourSite.Resolve"/>: this table first, then
/// <c>MethodPointerByPrefix</c> only when the JSON for the running build is missing.
///
/// Never throws on a missing id, a missing table, or a build mismatch: a hook that calls
/// <see cref="TryResolve"/> must be able to skip the detour instead of taking the process down.
/// JSON is loaded only from Core-controlled locations: an embedded resource in this assembly,
/// or the exact <c>{side}-{build}.json</c> next to Core (<c>BepInEx/progenitor-core/</c>) / in
/// that folder's <c>addresslib/</c> subfolder. <c>BepInEx/plugins</c>, environment variables,
/// and game-root directory walks are not consulted. After parse,
/// <see cref="AddressTable.Side"/> and <see cref="AddressTable.Build"/> must equal the running
/// process before the table is trusted. The matching file is chosen by process side plus the
/// size+stamp+checksum+prefix-SHA build id the generator writes. A resolved relative address is refused when
/// it is negative or not strictly inside the loaded module. A failed load is not latched: the
/// next <see cref="TryResolve"/> retries.
/// </summary>
public static class AddressLibrary
{
    private const string ResourcePrefix = "progenitor.addresslib.";
    private const string FolderName = "addresslib";
    private const string GameAssemblyFileName = "GameAssembly.dll";
    private const string PluginsFolderName = "plugins";

    /// <summary>
    /// Historical override name. Ignored: a reloadable plugin must not be able to point Core at
    /// attacker-chosen relative addresses. Kept public so docs and greps can name the thing that is not a search path.
    /// </summary>
    public const string IgnoredOverrideEnvironmentVariable = "PROGENITOR_ADDRESSLIB_DIR";

    private static readonly object Gate = new();
    private static bool _loadAttempted;
    private static AddressTable? _table;
    private static IntPtr _moduleBase;
    private static long _moduleMemorySize;
    private static string? _loadedSide;
    private static string? _loadedBuild;
    private static string? _loadedSource;
    private static string? _loadedPath;
    private static PeSection[]? _executableSections;
    private static string? _detectedSide;
    private static string? _detectedBuild;
    private static string? _status;
    private static readonly HashSet<string> MissingIds = new(StringComparer.Ordinal);
    private static readonly List<DetourSiteOutcome> SiteOutcomes = new();

    /// <summary>
    /// Optional extra log sink. Core also writes through <c>Plugin.Logger</c> when not compiled
    /// into the test assembly. Tests assign this to capture the build-mismatch message.
    /// </summary>
    public static Action<string>? LogSink { get; set; }

    public static bool IsLoaded
    {
        get { lock (Gate) return _table != null && _moduleBase != IntPtr.Zero; }
    }

    /// <summary>
    /// True after a load that saw GameAssembly.dll and either attached a matching table or
    /// decided none exists. False when GameAssembly is not in the module list yet so a later
    /// frame can retry. Never set before that attempt finishes.
    /// </summary>
    public static bool LoadAttempted
    {
        get { lock (Gate) return _loadAttempted; }
    }

    public static string? LoadedSide
    {
        get { lock (Gate) return _loadedSide; }
    }

    public static string? LoadedBuild
    {
        get { lock (Gate) return _loadedBuild; }
    }

    /// <summary><c>embedded</c> or <c>disk</c> when a table is attached; null otherwise.</summary>
    public static string? LoadedSource
    {
        get { lock (Gate) return _loadedSource; }
    }

    /// <summary>Filesystem path or embedded resource name of the attached table.</summary>
    public static string? LoadedPath
    {
        get { lock (Gate) return _loadedPath; }
    }

    /// <summary>
    /// Side fingerprint of the running process even when no table attached (so the
    /// missing-table error can name it). Null until GameAssembly.dll has been seen.
    /// </summary>
    public static string? DetectedSide
    {
        get { lock (Gate) return _detectedSide ?? _loadedSide; }
    }

    /// <summary>
    /// Build id of the running GameAssembly.dll even when no matching JSON exists.
    /// Null until the file has been fingerprinted.
    /// </summary>
    public static string? DetectedBuild
    {
        get { lock (Gate) return _detectedBuild ?? _loadedBuild; }
    }

    /// <summary>True when a table for this process is attached (embedded or disk).</summary>
    public static bool TablePresent
    {
        get { lock (Gate) return _table != null && _moduleBase != IntPtr.Zero; }
    }

    public static IntPtr ModuleBase
    {
        get { lock (Gate) return _moduleBase; }
    }

    /// <summary>Last load outcome, including the mismatch text when no JSON matches this binary.</summary>
    public static string? Status
    {
        get { lock (Gate) return _status; }
    }

    public static AddressTable? Current
    {
        get { lock (Gate) return _table; }
    }

    /// <summary>
    /// Ping/status <c>native</c> payload: build id, table present, sites resolved by
    /// table vs fallback vs missing. Merges Bootstrap receive-detour outcomes via
    /// reflection so a collectible Core still reports the process-lifetime JMP.
    /// </summary>
    public static NativePingSnapshot DescribeForPing()
    {
        try { EnsureLoaded(); }
        catch { /* Describe must not throw into a bridge op. */ }
        List<DetourSiteOutcome> sites;
        string side;
        string build;
        bool tablePresent;
        string status;
        lock (Gate)
        {
            side = _detectedSide ?? _loadedSide ?? DetectSide();
            build = _detectedBuild ?? _loadedBuild ?? "";
            tablePresent = _table != null && _moduleBase != IntPtr.Zero;
            status = _status ?? "";
            sites = new List<DetourSiteOutcome>(SiteOutcomes);
        }
        MergeBootstrapSites(sites);
        return new NativePingSnapshot(side, build, tablePresent, status, sites);
    }

    /// <summary>One grep line for ping/status and the boot log (<c>native side=… table=yes|no sites=…</c>).</summary>
    public static string Describe()
    {
        try { return DescribeForPing().Line; }
        catch (Exception e) { return "native (describe failed: " + e.Message + ")"; }
    }

    /// <summary>Record a detour-site outcome for ping. Last write for a site name wins.</summary>
    internal static void RecordSite(DetourSiteOutcome outcome)
    {
        lock (Gate)
        {
            for (int i = SiteOutcomes.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(SiteOutcomes[i].Site, outcome.Site, StringComparison.Ordinal))
                    continue;
                SiteOutcomes.RemoveAt(i);
            }
            SiteOutcomes.Add(outcome);
        }
    }

    /// <summary>Log a per-site resolve line. Warnings are the not-installed error.</summary>
    internal static void EmitSiteLog(string message, bool warning)
    {
        if (warning) Log(message);
        else LogInfo(message);
    }

    /// <summary>Tests inject a detected side/build so the missing-table message can name them.</summary>
    internal static void SetDetectedProcess(string side, string build)
    {
        lock (Gate)
        {
            _detectedSide = string.IsNullOrEmpty(side) ? null : side;
            _detectedBuild = string.IsNullOrEmpty(build) ? null : build;
        }
    }

    /// <summary>
    /// Image base of the loaded GameAssembly module plus the relative address for <paramref name="id"/>.
    /// Returns false (address zero) when the library is not loaded, the build does not match,
    /// the id is absent, or the relative address is outside the loaded module. Does not throw. A failed
    /// load is retried on the next call so dropping a matching JSON later can succeed without
    /// a process restart.
    /// </summary>
    public static bool TryResolve(string id, out IntPtr address)
    {
        address = IntPtr.Zero;
        try
        {
            if (string.IsNullOrEmpty(id)) return false;
            EnsureLoaded();
            AddressTable? table;
            IntPtr moduleBase;
            long moduleSize;
            lock (Gate)
            {
                table = _table;
                moduleBase = _moduleBase;
                moduleSize = _moduleMemorySize;
            }
            if (table == null || moduleBase == IntPtr.Zero) return false;
            if (!table.TryGetImageOffset(id, out var imageOffset))
            {
                NoteMissing(id);
                return false;
            }
            if (imageOffset < 0) return false;
            if (moduleSize > 0 && (ulong)imageOffset >= (ulong)moduleSize) return false;
            address = AddImageOffset(moduleBase, imageOffset, moduleSize);
            return address != IntPtr.Zero;
        }
        catch (Exception e)
        {
            Log("TryResolve(" + id + ") threw: " + e.Message);
            address = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>
    /// Same addition the live resolver uses, so tests can check <c>moduleBase + imageOffset</c> without
    /// loading GameAssembly.dll. Does not range-check; see
    /// <see cref="AddImageOffset(IntPtr, long, long)"/>.
    /// </summary>
    public static IntPtr AddImageOffset(IntPtr moduleBase, long imageOffset) => AddImageOffset(moduleBase, imageOffset, 0);

    /// <summary>
    /// Image base plus relative address, or zero when it is negative or (when
    /// <paramref name="moduleMemorySize"/> is positive) not strictly inside the module.
    /// </summary>
    public static IntPtr AddImageOffset(IntPtr moduleBase, long imageOffset, long moduleMemorySize)
    {
        if (moduleBase == IntPtr.Zero || imageOffset < 0) return IntPtr.Zero;
        if (moduleMemorySize > 0 && (ulong)imageOffset >= (ulong)moduleMemorySize) return IntPtr.Zero;
        var abs = unchecked((ulong)(long)moduleBase + (ulong)imageOffset);
        return (IntPtr)(long)abs;
    }

    /// <summary>
    /// Install a table and module base without reading the process. Unit tests use this; the
    /// game path is the lazy load inside <see cref="TryResolve"/>. Not a public plugin API:
    /// a consumer must not be able to point Core at attacker-chosen relative addresses.
    /// </summary>
    internal static void Attach(AddressTable table, IntPtr moduleBase, long moduleMemorySize = 0) =>
        Attach(table, moduleBase, moduleMemorySize, executableSections: null, source: "attached", path: null);

    /// <summary>
    /// Same as <see cref="Attach(AddressTable, IntPtr, long)"/> with executable sections so
    /// tests can refuse an in-module relative address that is not in <c>.text</c>.
    /// </summary>
    internal static void Attach(
        AddressTable table,
        IntPtr moduleBase,
        long moduleMemorySize,
        PeSection[]? executableSections,
        string? source = "attached",
        string? path = null)
    {
        if (table == null) throw new ArgumentNullException(nameof(table));
        lock (Gate)
        {
            _table = table;
            _moduleBase = moduleBase;
            _moduleMemorySize = moduleMemorySize < 0 ? 0 : moduleMemorySize;
            _executableSections = executableSections;
            _loadedSide = table.Side;
            _loadedBuild = table.Build;
            _loadedSource = source;
            _loadedPath = path;
            _detectedSide = table.Side;
            _detectedBuild = table.Build;
            _loadAttempted = true;
            _status = "attached " + table.Side + " build " + table.Build + " entries=" + table.Count
                      + " " + AddressId.FormatTableSource(source ?? "attached", path, table.Build);
            MissingIds.Clear();
            SiteOutcomes.Clear();
        }
    }

    /// <summary>
    /// True when <paramref name="imageOffset"/> is inside an executable section of the loaded
    /// module. No parsed sections (tests that only <see cref="Attach"/> a table) skip
    /// this check so the existing module-size range test remains the gate.
    /// </summary>
    internal static bool ImageOffsetInExecutableSection(long imageOffset)
    {
        lock (Gate)
        {
            if (_executableSections == null || _executableSections.Length == 0)
                return true;
            return AddressId.ImageOffsetLiesInExecutableSection(imageOffset, _executableSections);
        }
    }

    /// <summary>Table lookup without resolving to an address. False when no table is attached.</summary>
    internal static bool TryGetTableImageOffset(string id, out long imageOffset)
    {
        imageOffset = 0;
        try
        {
            EnsureLoaded();
            lock (Gate)
            {
                if (_table == null) return false;
                return _table.TryGetImageOffset(id, out imageOffset);
            }
        }
        catch
        {
            imageOffset = 0;
            return false;
        }
    }

    /// <summary>Drop a previously attached or loaded table. Tests call this between cases.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _table = null;
            _moduleBase = IntPtr.Zero;
            _moduleMemorySize = 0;
            _executableSections = null;
            _loadedSide = null;
            _loadedBuild = null;
            _loadedSource = null;
            _loadedPath = null;
            _detectedSide = null;
            _detectedBuild = null;
            _loadAttempted = false;
            _status = null;
            MissingIds.Clear();
            SiteOutcomes.Clear();
        }
    }

    /// <summary>
    /// Names Core actually ships: embedded <c>progenitor.addresslib.*</c> resources, then the
    /// exact files next to this assembly in <c>BepInEx/progenitor-core/</c> (or its
    /// <c>addresslib/</c> subfolder). Not a directory listing of <c>BepInEx/plugins</c> or the
    /// game root, and not <see cref="IgnoredOverrideEnvironmentVariable"/>.
    /// </summary>
    public static IReadOnlyList<string> ListShippedNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var resource in typeof(AddressLibrary).Assembly.GetManifestResourceNames())
            {
                if (resource.IndexOf("addresslib", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var name = resource;
                if (name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(ResourcePrefix.Length);
                if (seen.Add(name)) names.Add(name);
            }
        }
        catch (Exception e)
        {
            Log("ListShippedNames resources: " + e.Message);
        }
        try
        {
            foreach (var path in EnumerateCoreControlledFiles("*", "*"))
            {
                if (!File.Exists(path)) continue;
                var name = Path.GetFileName(path);
                if (seen.Add(name)) names.Add(name);
            }
        }
        catch (Exception e)
        {
            Log("ListShippedNames files: " + e.Message);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Exact JSON paths Core will open for a side/build: <c>addresslib/{side}-{build}.json</c>
    /// then <c>{side}-{build}.json</c>, both next to this assembly (Core lives in
    /// <c>BepInEx/progenitor-core/</c>). A folder named <c>plugins</c> is skipped. No
    /// environment variable, no <c>AppContext.BaseDirectory</c>, no <c>GetFiles(*.json)</c>
    /// of <c>BepInEx/plugins</c>.
    /// </summary>
    public static IReadOnlyList<string> ListCandidateFiles(string side, string build)
    {
        var list = new List<string>();
        foreach (var path in EnumerateCoreControlledFiles(side, build))
            list.Add(path);
        return list;
    }

    /// <summary>
    /// True when the parsed table is for this process, not merely a file whose name looks like
    /// it. Filename matching is not enough: a planted JSON can reuse <c>{side}-{build}.json</c>.
    /// </summary>
    public static bool TableMatchesProcess(AddressTable table, string side, string build)
    {
        if (table == null) return false;
        if (string.IsNullOrEmpty(side) || string.IsNullOrEmpty(build)) return false;
        if (!string.Equals(table.Side, side, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(table.Build, build, StringComparison.Ordinal);
    }

    internal static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_table != null && _moduleBase != IntPtr.Zero) return;
            if (_loadAttempted) return;
        }
        try
        {
            TryLoadRunningProcess();
        }
        catch (Exception e)
        {
            SetStatus("load threw: " + e.Message);
            Log(Status!);
        }
    }

    internal static bool TryLoadRunningProcess()
    {
        if (!TryGetGameAssembly(out var moduleBase, out var modulePath, out var moduleSize, out var whyModule))
        {
            SetStatus("GameAssembly.dll is not loaded (" + whyModule + ").", log: true);
            return false;
        }

        string build;
        long moduleFileLength;
        uint moduleCheckSum;
        PeSection[]? sections;
        try
        {
            build = AddressId.ComputeBuildIdFromFile(modulePath);
            moduleFileLength = new FileInfo(modulePath).Length;
            TryReadModulePe(modulePath, out moduleCheckSum, out sections);
        }
        catch (Exception e)
        {
            SetStatus("could not fingerprint " + modulePath + ": " + e.Message, log: true);
            return false;
        }

        var side = DetectSide();
        lock (Gate)
        {
            _detectedSide = side;
            _detectedBuild = build;
        }
        if (!TryFindTable(side, build, moduleFileLength, moduleCheckSum, out var table, out var whyTable, out var source, out var sourcePath))
        {
            // Keep the module base so a prefix fallback can still report a relative address. Do not
            // latch LoadAttempted: a matching JSON dropped later (or an embed that
            // appears after a Core swap) must be visible on the next TryResolve.
            lock (Gate)
            {
                _moduleBase = moduleBase;
                _moduleMemorySize = moduleSize < 0 ? 0 : moduleSize;
                _executableSections = sections;
            }
            var shipped = ListShippedNames();
            var shippedText = shipped.Count == 0 ? "(none)" : string.Join(", ", shipped);
            SetStatus(
                "Address library has no table for " + side + " build " + build +
                " (" + whyTable + "). Shipped files: " + shippedText +
                ". Native lookups will return false until node tools/addresslib/build.mjs --side " + side +
                " is re-run for this GameAssembly.dll.",
                log: true);
            return false;
        }

        lock (Gate)
        {
            _table = table;
            _moduleBase = moduleBase;
            _moduleMemorySize = moduleSize < 0 ? 0 : moduleSize;
            _executableSections = sections;
            _loadedSide = table!.Side;
            _loadedBuild = table.Build;
            _loadedSource = source;
            _loadedPath = sourcePath;
            _detectedSide = table.Side;
            _detectedBuild = table.Build;
            _loadAttempted = true;
            _status = "loaded " + table.Side + " build " + table.Build + " entries=" + table.Count +
                      " moduleBase=0x" + ((long)moduleBase).ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                      + " " + AddressId.FormatTableSource(source, sourcePath, table.Build);
        }
        LogInfo(Status!);
        return true;
    }

    internal static string DetectSide()
    {
        try
        {
            var name = Process.GetCurrentProcess().ProcessName;
            if (name.StartsWith("VRisingServer", StringComparison.OrdinalIgnoreCase)) return "server";
        }
        catch
        {
            // Fall through to client. A name we cannot read is not a reason to throw.
        }
        return "client";
    }

    internal static bool TryFindTable(string side, string build, out AddressTable? table, out string why) =>
        TryFindTable(side, build, moduleFileLength: -1, moduleCheckSum: 0, out table, out why, out _, out _);

    internal static bool TryFindTable(
        string side,
        string build,
        long moduleFileLength,
        uint moduleCheckSum,
        out AddressTable? table,
        out string why,
        out string source,
        out string sourcePath)
    {
        table = null;
        why = "";
        source = "";
        sourcePath = "";
        var fileName = side + "-" + build + ".json";

        try
        {
            var asm = typeof(AddressLibrary).Assembly;
            foreach (var resource in asm.GetManifestResourceNames())
            {
                if (!ResourceMatches(resource, fileName)) continue;
                using var stream = asm.GetManifestResourceStream(resource);
                if (stream == null) continue;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                if (!AddressTable.TryParse(json, out table, out var parseError) || table == null)
                {
                    why = parseError ?? ("unreadable resource " + resource);
                    continue;
                }
                if (!TableMatchesProcess(table, side, build))
                {
                    why = "embedded " + resource + " is side=" + table.Side + " build=" + table.Build +
                          ", not " + side + "/" + build;
                    table = null;
                    continue;
                }
                if (!AcceptBuildFingerprint(table, moduleFileLength, moduleCheckSum, resource, out why))
                {
                    table = null;
                    continue;
                }
                source = "embedded";
                sourcePath = resource;
                return true;
            }
        }
        catch (Exception e)
        {
            why = "embedded lookup threw: " + e.Message;
        }

        foreach (var path in EnumerateCoreControlledFiles(side, build))
        {
            if (!File.Exists(path)) continue;
            if (!AddressTable.TryLoadFile(path, out table, out var fileError) || table == null)
            {
                why = fileError ?? ("unreadable " + path);
                continue;
            }
            if (!TableMatchesProcess(table, side, build))
            {
                why = path + " is side=" + table.Side + " build=" + table.Build + ", not " + side + "/" + build;
                table = null;
                continue;
            }
            if (!AcceptBuildFingerprint(table, moduleFileLength, moduleCheckSum, path, out why))
            {
                table = null;
                continue;
            }
            source = "disk";
            sourcePath = path;
            return true;
        }

        if (string.IsNullOrEmpty(why)) why = "no Core-controlled file or resource named " + fileName;
        return false;
    }

    /// <summary>
    /// When the live module's length/checksum are known, the table's build id must
    /// name those fields. <paramref name="moduleFileLength"/> negative skips the check
    /// (unit tests that do not fingerprint a real GameAssembly).
    /// </summary>
    internal static bool AcceptBuildFingerprint(
        AddressTable table,
        long moduleFileLength,
        uint moduleCheckSum,
        string? sourceName,
        out string why)
    {
        why = "";
        if (table == null) { why = "no table"; return false; }
        if (moduleFileLength < 0) return true;
        if (AddressId.BuildIdMatchesModule(table.Build, moduleFileLength, moduleCheckSum))
            return true;
        why = (sourceName ?? "table") + " build id size/checksum does not match GameAssembly length="
              + moduleFileLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
              + " checksum=" + moduleCheckSum.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        return false;
    }

    private static void TryReadModulePe(string modulePath, out uint checkSum, out PeSection[]? sections)
    {
        checkSum = 0;
        sections = null;
        try
        {
            using var fs = new FileStream(
                modulePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: Math.Min(AddressId.BuildIdPrefixBytes, 64 * 1024),
                FileOptions.SequentialScan);
            var n = Math.Min(AddressId.BuildIdPrefixBytes, fs.Length);
            if (n <= 0) return;
            var prefix = new byte[(int)n];
            int read = 0;
            while (read < prefix.Length)
            {
                int got = fs.Read(prefix, read, prefix.Length - read);
                if (got <= 0) break;
                read += got;
            }
            if (read <= 0) return;
            ReadOnlySpan<byte> span = prefix.AsSpan(0, read);
            AddressId.TryReadPeIdentifiers(span, out _, out checkSum);
            if (AddressId.TryReadPeSections(span, out var parsed) && parsed.Length > 0)
                sections = parsed;
        }
        catch
        {
            checkSum = 0;
            sections = null;
        }
    }

    private static bool ResourceMatches(string resourceName, string fileName)
    {
        if (resourceName.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)) return true;
        if (resourceName.Equals(ResourcePrefix + fileName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IEnumerable<string> EnumerateCoreControlledFiles(string side, string build)
    {
        string? asmDir = null;
        try { asmDir = Path.GetDirectoryName(typeof(AddressLibrary).Assembly.Location); }
        catch { /* Location can throw for some load contexts. */ }
        if (string.IsNullOrEmpty(asmDir)) yield break;
        if (IsPluginsDirectory(asmDir)) yield break;

        var fileName = string.Equals(side, "*", StringComparison.Ordinal) || string.Equals(build, "*", StringComparison.Ordinal)
            ? null
            : side + "-" + build + ".json";

        if (fileName == null)
        {
            // Listing shipped names: only the two Core-adjacent locations, exact JSON files,
            // never a recursive or BepInEx/plugins glob.
            var folder = Path.Combine(asmDir, FolderName);
            if (Directory.Exists(folder))
            {
                string[] files;
                try { files = Directory.GetFiles(folder, "*-*.json"); }
                catch { files = Array.Empty<string>(); }
                foreach (var file in files)
                    yield return file;
            }
            string[] beside;
            try { beside = Directory.GetFiles(asmDir, "*-*.json"); }
            catch { beside = Array.Empty<string>(); }
            foreach (var file in beside)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("client-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("server-", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
            yield break;
        }

        yield return Path.Combine(asmDir, FolderName, fileName);
        yield return Path.Combine(asmDir, fileName);
    }

    private static bool IsPluginsDirectory(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(name, PluginsFolderName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetGameAssembly(out IntPtr moduleBase, out string modulePath, out long moduleMemorySize, out string why)
    {
        moduleBase = IntPtr.Zero;
        modulePath = "";
        moduleMemorySize = 0;
        why = "";
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var name = module.ModuleName ?? "";
                if (!name.Equals(GameAssemblyFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                moduleBase = module.BaseAddress;
                modulePath = module.FileName ?? "";
                try { moduleMemorySize = module.ModuleMemorySize; }
                catch { moduleMemorySize = 0; }
                if (moduleBase == IntPtr.Zero)
                {
                    why = "GameAssembly.dll base address is zero.";
                    return false;
                }
                if (string.IsNullOrEmpty(modulePath) || !File.Exists(modulePath))
                {
                    why = "GameAssembly.dll path is missing on the process module.";
                    return false;
                }
                return true;
            }
        }
        catch (Exception e)
        {
            why = "enumerating modules threw: " + e.Message;
            return false;
        }

        var handle = GetModuleHandleW(GameAssemblyFileName);
        if (handle != IntPtr.Zero)
        {
            moduleBase = handle;
            why = "GameAssembly.dll is loaded but Process.Modules did not report a file path.";
            return false;
        }

        why = "GameAssembly.dll is not in the process module list.";
        return false;
    }

    private static void NoteMissing(string id)
    {
        lock (Gate)
        {
            if (!MissingIds.Add(id)) return;
        }
        Log("no relative address for id '" + id + "' in the loaded address table.");
    }

    private static void SetStatus(string message, bool log = false)
    {
        bool changed;
        lock (Gate)
        {
            changed = !string.Equals(_status, message, StringComparison.Ordinal);
            _status = message;
        }
        if (log && changed) Log(message);
    }

    private static void Log(string message)
    {
        try { LogSink?.Invoke(message); }
        catch { /* a throwing sink must not escape into a detour. */ }
#if !PROGENITOR_TESTS
        try { Plugin.Logger?.LogWarning("[addresslib] " + message); }
        catch { /* logging must not take down a hook. */ }
#endif
    }

    private static void LogInfo(string message)
    {
        try { LogSink?.Invoke(message); }
        catch { /* a throwing sink must not escape into a detour. */ }
#if !PROGENITOR_TESTS
        try { Plugin.Logger?.LogInfo("[addresslib] " + message); }
        catch { /* logging must not take down a hook. */ }
#endif
    }

    /// <summary>
    /// Bootstrap receive detours install before Core loads and record their outcomes on
    /// the public <c>ReceiveDetourPing.NativePingLines</c> surface. Reflection so Core
    /// does not compile-reference Bootstrap. <c>ReceiveDetours</c> itself is internal;
    /// <c>Assembly.GetType</c> would miss it. Malformed lines are skipped.
    /// </summary>
    private static void MergeBootstrapSites(List<DetourSiteOutcome> sites)
    {
        try
        {
            Type? type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(asm.GetName().Name, "Progenitor.Bootstrap", StringComparison.Ordinal))
                    continue;
                type = asm.GetType("Progenitor.Bootstrap.ReceiveDetourPing")
                    ?? asm.GetType("Progenitor.Bootstrap.ReceiveDetours");
                if (type != null) break;
            }
            if (type == null) return;
            var prop = type.GetProperty("NativePingLines", BindingFlags.Public | BindingFlags.Static);
            if (prop == null) return;
            if (prop.GetValue(null) is not string[] lines) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sites.Count; i++)
                seen.Add(sites[i].Site);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryParsePingLine(lines[i], out var outcome)) continue;
                if (!seen.Add(outcome.Site)) continue;
                sites.Add(outcome);
            }
        }
        catch
        {
            // A missing Bootstrap type must not fail ping.
        }
    }

    internal static bool TryParsePingLine(string? line, out DetourSiteOutcome outcome)
    {
        outcome = default;
        if (string.IsNullOrEmpty(line)) return false;
        // site|id|path|imageOffset|address
        var parts = line.Split('|');
        if (parts.Length < 4) return false;
        if (!long.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var imageOffset))
            imageOffset = 0;
        IntPtr address = IntPtr.Zero;
        if (parts.Length >= 5
            && long.TryParse(parts[4], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var addr))
            address = (IntPtr)addr;
        outcome = new DetourSiteOutcome(parts[0], parts[1], parts[2], imageOffset, address);
        return !string.IsNullOrEmpty(parts[0]);
    }

    internal static string FormatPingPipe(DetourSiteOutcome outcome) =>
        (outcome.Site ?? "") + "|" + (outcome.Id ?? "") + "|" + (outcome.Path ?? "") + "|"
        + outcome.ImageOffset.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "|"
        + ((long)outcome.Address).ToString("X", System.Globalization.CultureInfo.InvariantCulture);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
