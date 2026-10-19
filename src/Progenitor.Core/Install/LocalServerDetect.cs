using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Progenitor.Persistence;

namespace Progenitor.Install;

/// <summary>
/// LOCALDETECT1: what auto-detection found for one joined port: the dedicated-server folder whose own
/// <c>VRisingServer.exe</c> holds that port on this PC, or why none was taken.
/// </summary>
internal sealed class LocalServerDetection
{
    /// <summary>A folder was identified, validated, fenced and confirmed to carry Progenitor.</summary>
    public bool Found { get; init; }

    /// <summary>The server folder (canonical, holding <c>VRisingServer.exe</c>), or empty.</summary>
    public string Root { get; init; } = "";

    /// <summary>The joined port this answer is for.</summary>
    public int Port { get; init; }

    /// <summary>The process that holds the port (0 when none was identified).</summary>
    public int Pid { get; init; }

    /// <summary>That process's full image path, or empty.</summary>
    public string ExePath { get; init; } = "";

    /// <summary>The server's own name (its <c>-serverName</c>, else its settings file's <c>Name</c>), or empty.</summary>
    public string Name { get; init; } = "";

    /// <summary>The folder's operator key fingerprint (its <c>BepInEx/config/progenitor-operator.pub</c>), or empty.</summary>
    public string OperatorKey { get; init; } = "";

    /// <summary>Why the folder was or was not taken, in words (the log and the readback).</summary>
    public string Why { get; init; } = "";

    /// <summary>The folder's BepInEx root, or empty.</summary>
    public string BepInExRoot => Root.Length == 0 ? "" : Path.Combine(Root, LocalServerFolder.BepInExFolderName);

    public static LocalServerDetection None(int port, string why, int pid = 0, string exe = "") =>
        new() { Found = false, Port = port, Pid = pid, ExePath = exe ?? "", Why = why ?? "" };
}

/// <summary>
/// LOCALDETECT1 (decided 2026-10-08): any local Progenitor server folder is detected as local automatically. When the
/// joined address is this machine, the process that owns the joined UDP port is identified by the system's UDP table (the
/// same table Stop server's "the folder's own server on the joined port" check reads), matched by its full executable path
/// (never a name alone), and its folder becomes this connection's local route when every check below holds:
/// <list type="number">
/// <item>exactly one process holds the port, and it is a <c>VRisingServer.exe</c> whose image path could be read;</item>
/// <item>its command line could be read (fail closed: an unread one would pass the argument fence blind);</item>
/// <item>it runs on its own save folder (<see cref="ServerProcessControl.SaveFolderRefusal"/>, never the default one);</item>
/// <item>the live-server fence passes for the folder and for the process's own path, working directory and arguments
/// (<see cref="ServerProcessControl.IsLiveServer"/>); a production server folder is never written or controlled;</item>
/// <item>the folder validates as a dedicated-server install (<see cref="LocalServerFolder.Validate"/>), and the exe is
/// that folder's own;</item>
/// <item>the process's own settings (its <c>-persistentDataPath</c> settings file, else the folder's) name the joined port;</item>
/// <item>Progenitor is installed there (<c>BepInEx/plugins</c> holds <c>Progenitor.Bootstrap.dll</c>).</item>
/// </list>
/// Anything unreadable or ambiguous is "not detected", with the reason; the route then falls back to the configured folder
/// or the remote route. Game-free: every system read goes through a probe the tests replace.
/// </summary>
internal static class LocalServerDetector
{
    public const string BootstrapDllName = "Progenitor.Bootstrap.dll";

    /// <summary>Tests: the pids holding a UDP port (null: unknown). Null probe: <see cref="ServerProcessControl.UdpPortOwners"/>.</summary>
    internal static Func<int, IReadOnlyCollection<int>?>? PortOwnersProbe { get; set; }

    /// <summary>Tests: pid → its full image path, command line and working directory (null: gone or unreadable).</summary>
    internal static Func<int, ServerProcessInfo?>? ProcessProbe { get; set; }

    /// <summary>Tests: folder validation. Null probe: <see cref="LocalServerFolder.Validate"/>.</summary>
    internal static Func<string, LocalServerFolderResult>? ValidateProbe { get; set; }

    /// <summary>Tests: the default save folder's settings directory parent. Null: <see cref="ServerProcessControl.DefaultSaveRoot"/>.</summary>
    internal static Func<string>? DefaultSaveRootProbe { get; set; }

    internal static void ResetForTests()
    {
        PortOwnersProbe = null;
        ProcessProbe = null;
        ValidateProbe = null;
        DefaultSaveRootProbe = null;
        LocalServerDetectCache.Clear();
        LocalServerDetectCache.Compute = null;
        LocalServerDetectCache.Scheduler = null;
        LocalServerDetectCache.Clock = () => DateTime.UtcNow;
    }

    /// <summary>
    /// The folder of the server that owns <paramref name="port"/> on this PC, or why none. Reads the process table and
    /// files: never call it on the main thread (the route reads <see cref="LocalServerDetectCache"/>).
    /// </summary>
    public static LocalServerDetection Detect(bool addressIsThisMachine, int port)
    {
        try { return DetectCore(addressIsThisMachine, port); }
        catch (Exception e) { return LocalServerDetection.None(port, "detection failed: " + e.Message); }
    }

    private static LocalServerDetection DetectCore(bool addressIsThisMachine, int port)
    {
        if (port <= 0) return LocalServerDetection.None(port, "no joined port recorded");
        if (!addressIsThisMachine) return LocalServerDetection.None(port, "the joined address is not this machine");

        var owners = PortOwnersProbe != null ? PortOwnersProbe(port) : ServerProcessControl.UdpPortOwners(port);
        if (owners == null) return LocalServerDetection.None(port, "the UDP table could not be read, so the owner of port " + port + " is unknown");
        var pids = new HashSet<int>();
        foreach (int pid in owners)
            if (pid > 0) pids.Add(pid);
        if (pids.Count == 0) return LocalServerDetection.None(port, "no process on this PC holds UDP port " + port);
        if (pids.Count > 1)
            return LocalServerDetection.None(port, pids.Count + " processes hold UDP port " + port + ", so the server is ambiguous");

        int owner = 0;
        foreach (int pid in pids) owner = pid;
        var process = ProcessProbe != null ? ProcessProbe(owner) : ReadProcess(owner);
        if (process == null || process.Path.Length == 0)
            return LocalServerDetection.None(port, "the image path of pid " + owner + " (holding port " + port + ") could not be read", owner);
        string exe = process.Path;
        if (!string.Equals(SafeFileName(exe), LocalServerFolder.ServerExecutableName, StringComparison.OrdinalIgnoreCase))
            return LocalServerDetection.None(port, "port " + port + " is held by pid " + owner + ", which is not " + LocalServerFolder.ServerExecutableName, owner, exe);

        string exeDir = SafeDirectoryOf(exe);
        if (exeDir.Length == 0) return LocalServerDetection.None(port, "the server's folder could not be derived from its path", owner, exe);

        // Review (fail closed): the argument fence below reads the command line. An unreadable one (an elevated server)
        // would read as no arguments and pass it blind, so it is a refusal, never "nothing live in it".
        if (process.CommandLine.Trim().Length == 0)
            return LocalServerDetection.None(port, "the command line of pid " + owner + " could not be read, so its arguments (and save folder) are unknown", owner, exe);

        // The live-server fence first, before any file in that folder is read: the folder, the exe, the working directory
        // and the arguments (a -persistentDataPath into the live server's data counts).
        if (LocalServerFolder.IsLiveServerFolder(exeDir) || ServerProcessControl.IsLiveServer(exeDir, process: process))
            return LocalServerDetection.None(port, "the server on port " + port + " is the live dedicated server; it is never written or controlled", owner, exe);

        var valid = ValidateProbe != null ? ValidateProbe(exeDir) : LocalServerFolder.Validate(exeDir);
        if (!valid.Ok || !valid.Available || string.IsNullOrWhiteSpace(valid.CanonicalPath))
            return LocalServerDetection.None(port, "its folder is not a usable dedicated-server folder (" + (valid.Refusal ?? "refused") + ")", owner, exe);
        string root = valid.CanonicalPath!;
        if (LocalServerFolder.IsLiveServerFolder(root) || ServerProcessControl.IsLiveServer(root, process: process))
            return LocalServerDetection.None(port, "the server on port " + port + " is the live dedicated server; it is never written or controlled", owner, exe);
        if (!ServerProcessControl.IsFoldersServerExe(root, exe))
            return LocalServerDetection.None(port, "the process's exe is not the folder's own " + LocalServerFolder.ServerExecutableName, owner, exe);

        // Review M2's start rule, for detection too: the server must run on its own save folder, never the default one the
        // live server may use (a stop through this route saves the world there).
        string wd = process.WorkingDirectory.Length > 0 ? process.WorkingDirectory : exeDir;
        string saveRefusal = ServerProcessControl.SaveFolderRefusal(ServerLaunchRecord.SplitCommandLine(process.CommandLine), wd);
        if (saveRefusal.Length > 0) return LocalServerDetection.None(port, saveRefusal, owner, exe);

        var (ownPort, settingsName) = OwnSettings(root, process);
        if (ownPort <= 0)
            return LocalServerDetection.None(port, "the server's own ServerHostSettings.json port is unknown", owner, exe);
        if (ownPort != port)
            return LocalServerDetection.None(port, "the server's own settings name port " + ownPort + ", not the joined port " + port, owner, exe);

        if (!ProgenitorInstalled(root))
            return LocalServerDetection.None(port, "Progenitor is not installed in " + root + " (no BepInEx/plugins/" + BootstrapDllName + ")", owner, exe);

        string name = ArgumentValue(ServerLaunchRecord.SplitCommandLine(process.CommandLine), "-serverName");
        if (name.Length == 0) name = settingsName;
        return new LocalServerDetection
        {
            Found = true,
            Root = root,
            Port = port,
            Pid = owner,
            ExePath = exe,
            Name = name,
            OperatorKey = Networking.JoinLocalRoute.ReadOperatorFingerprint(root),
            Why = "pid " + owner + " (" + exe + ") holds port " + port + ", its own settings name that port, and Progenitor is installed there",
        };
    }

    /// <summary>
    /// The game port and name the process's own settings give: its <c>-persistentDataPath</c> settings file when it names
    /// one (else the default save folder's, which a server without that argument uses), then the install's own copies.
    /// The first readable file with a port answers.
    /// </summary>
    internal static (int Port, string Name) OwnSettings(string root, ServerProcessInfo process)
    {
        var args = ServerLaunchRecord.SplitCommandLine(process.CommandLine);
        string wd = process.WorkingDirectory.Length > 0 ? process.WorkingDirectory : SafeDirectoryOf(process.Path);
        var candidates = new List<string>();
        string? data = ServerProcessControl.PersistentDataPathOf(args, wd);
        if (data != null) AddCandidate(candidates, data);
        else
        {
            // An unread command line (empty) proves nothing about the save folder: only the install's own copies remain.
            if (process.CommandLine.Length > 0)
            {
                string fallback = DefaultSaveRootProbe != null ? DefaultSaveRootProbe() : ServerProcessControl.DefaultSaveRoot;
                if (!string.IsNullOrWhiteSpace(fallback)) AddCandidate(candidates, fallback);
            }
        }

        AddCandidate(candidates, root);
        try { candidates.Add(Path.Combine(root, "VRisingServer_Data", "StreamingAssets", "Settings", "ServerHostSettings.json")); }
        catch { /* */ }

        foreach (string candidate in candidates)
        {
            var (port, name) = ReadSettings(candidate);
            if (port > 0) return (port, name);
        }

        return (0, "");
    }

    private static void AddCandidate(List<string> list, string dataRoot)
    {
        try
        {
            string c = Path.Combine(dataRoot, "Settings", "ServerHostSettings.json");
            if (!list.Exists(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))) list.Add(c);
        }
        catch { /* a malformed path is no candidate */ }
    }

    private static (int Port, string Name) ReadSettings(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return (0, "");
            using var doc = JsonFiles.ParseFile(path);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (0, "");
            int port = 0;
            if (root.TryGetProperty("Port", out var p) && p.ValueKind == JsonValueKind.Number) port = p.GetInt32();
            else if (root.TryGetProperty("GamePort", out var g) && g.ValueKind == JsonValueKind.Number) port = g.GetInt32();
            string name = root.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "").Trim() : "";
            return (port, name);
        }
        catch { return (0, ""); }
    }

    /// <summary>Progenitor's Bootstrap is in the folder's <c>BepInEx/plugins</c> (directly, or one package folder down).</summary>
    internal static bool ProgenitorInstalled(string root)
    {
        try
        {
            string plugins = Path.Combine(root, LocalServerFolder.BepInExFolderName, "plugins");
            if (!Directory.Exists(plugins)) return false;
            if (File.Exists(Path.Combine(plugins, BootstrapDllName))) return true;
            foreach (string dir in Directory.EnumerateDirectories(plugins))
                if (File.Exists(Path.Combine(dir, BootstrapDllName))) return true;
        }
        catch { /* unreadable: not proven */ }

        return false;
    }

    private static string ArgumentValue(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string a = (args[i] ?? "").Trim();
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
            {
                string v = i + 1 < args.Count ? (args[i + 1] ?? "").Trim() : "";
                return v.StartsWith("-", StringComparison.Ordinal) ? "" : v.Trim('"').Trim();
            }

            if (a.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)) return a.Substring(flag.Length + 1).Trim('"').Trim();
        }

        return "";
    }

    /// <summary>
    /// LOCALDETECT1 review (live 2026-10-10): a detection made just before a join boundary still describes the server on
    /// the port when exactly the same process (pid and full image path, with a read command line) is still the port's only
    /// holder. Anything else (another pid, a second holder, an unreadable table or process) is not the same server.
    /// </summary>
    internal static bool StillHolds(LocalServerDetection? previous)
    {
        if (previous == null || !previous.Found || previous.Pid <= 0 || previous.Port <= 0) return false;
        try
        {
            var owners = PortOwnersProbe != null ? PortOwnersProbe(previous.Port) : ServerProcessControl.UdpPortOwners(previous.Port);
            if (owners == null) return false;
            var pids = new HashSet<int>();
            foreach (int pid in owners)
                if (pid > 0) pids.Add(pid);
            if (pids.Count != 1 || !pids.Contains(previous.Pid)) return false;
            var process = ProcessProbe != null ? ProcessProbe(previous.Pid) : ReadProcess(previous.Pid);
            return process != null && process.CommandLine.Trim().Length > 0
                && string.Equals(process.Path, previous.ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static ServerProcessInfo? ReadProcess(int pid)
    {
#if PROGENITOR_TESTS
        return null;
#else
        try
        {
            using var proc = Process.GetProcessById(pid);
            string path = ServerInstall.ExecutablePath(proc);
            if (path.Length == 0) return null;
            ServerInstall.TryReadProcessCommandLine(pid, out string commandLine, out string workingDirectory);
            return new ServerProcessInfo(pid, path, commandLine, workingDirectory);
        }
        catch { return null; }
#endif
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path) ?? ""; }
        catch { return ""; }
    }

    private static string SafeDirectoryOf(string path)
    {
        try { return Path.GetDirectoryName(path) ?? ""; }
        catch { return ""; }
    }
}

/// <summary>
/// LOCALDETECT1: the detection runs a UDP-table read, a process read and file reads, so it never runs on the main thread
/// (the rule after the live freezes). The route reads the last answer for the joined port; a stale answer (older than
/// <see cref="RefreshSeconds"/>, another port, or a join boundary) starts one background refresh. Until an answer for this
/// port arrives the route reads none: not detected, the safe default (the configured folder or the remote route).
///
/// <para>Review (stale folder on a reused port): a join boundary (<see cref="RefreshAll"/>) drops the answer at once, so
/// between "server A left port 9876" and "server B on 9876 is detected" the route reads none rather than A's folder. Each
/// boundary and <see cref="Clear"/> starts a new generation; a refresh started in an older one is discarded when it lands,
/// so an in-flight background read can never write an answer after them.</para>
/// </summary>
internal static class LocalServerDetectCache
{
    internal const double RefreshSeconds = 10;

    private static readonly object Gate = new();
    private static LocalServerDetection? _value;
    private static string _key = "";
    private static DateTime _at;
    private static long _gen;
    private static long _refreshingGen = -1;
    private static LocalServerDetection? _carry;
    private static bool _lastThisMachine;
    private static int _lastPort;
    private static string _loggedKey = "";

    /// <summary>Computes one answer (live: <see cref="LocalServerDetector.Detect"/>). Null: the detector itself.</summary>
    internal static Func<bool, int, LocalServerDetection>? Compute { get; set; }

    /// <summary>Tests: run a refresh inline instead of on the thread pool.</summary>
    internal static Action<Action>? Scheduler { get; set; }

    internal static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Tests: the log lines written when an answer changes.</summary>
    internal static Action<string>? LogSink { get; set; }

    /// <summary>The last answer for this address kind and port (null until one arrives); never blocks.</summary>
    internal static LocalServerDetection? Get(bool addressIsThisMachine, int port)
    {
        if (port <= 0 || !addressIsThisMachine) return null;
        string key = "local:" + port;
        LocalServerDetection? value;
        bool start;
        long gen;
        lock (Gate)
        {
            bool sameKey = string.Equals(key, _key, StringComparison.Ordinal);
            value = sameKey ? _value : null;
            bool stale = value == null || (Clock() - _at).TotalSeconds >= RefreshSeconds;
            gen = _gen;
            start = stale && _refreshingGen != gen;
            if (start) _refreshingGen = gen;
            _lastThisMachine = addressIsThisMachine;
            _lastPort = port;
        }

        if (start) Start(key, addressIsThisMachine, port, gen);
        return value;
    }

    private static void Start(string key, bool addressIsThisMachine, int port, long gen)
    {
        void Run()
        {
            LocalServerDetection result;
            LocalServerDetection? carry;
            lock (Gate) carry = gen == _gen ? _carry : null;
            try
            {
                // An answer made just before the join boundary for this port, when the same process still holds it.
                result = carry != null && carry.Port == port && LocalServerDetector.StillHolds(carry)
                    ? carry
                    : (Compute ?? LocalServerDetector.Detect)(addressIsThisMachine, port);
            }
            catch (Exception e) { result = LocalServerDetection.None(port, "detection failed: " + e.Message); }
            bool log = false;
            lock (Gate)
            {
                if (_refreshingGen == gen) _refreshingGen = -1;
                // A join boundary or a Clear since this read started: the answer may describe the previous server.
                if (gen == _gen)
                {
                    _key = key;
                    _value = result;
                    _at = Clock();
                    string logKey = key + "|" + result.Found + "|" + result.Root + "|" + result.Pid + "|" + result.Why;
                    log = !string.Equals(logKey, _loggedKey, StringComparison.Ordinal);
                    if (log) _loggedKey = logKey;
                }
            }

            if (log) Log(result);
        }

        try
        {
            if (Scheduler != null) Scheduler(Run);
            else System.Threading.Tasks.Task.Run(Run);
        }
        catch
        {
            lock (Gate) if (_refreshingGen == gen) _refreshingGen = -1;
        }
    }

    private static void Log(LocalServerDetection result)
    {
        string line = result.Found
            ? "Install: local server auto-detected for joined port " + result.Port + ": " + result.Root + " (" + result.Why + ")."
            : "Install: no local server folder auto-detected for joined port " + result.Port + " (" + result.Why + ").";
        try
        {
            if (LogSink != null) LogSink(line);
            else InstallLog.Info(line);
        }
        catch { /* logging never breaks the route */ }
    }

    /// <summary>
    /// A join boundary: the answer is dropped now (reads return none until a fresh one lands) and a refresh for the last
    /// asked port starts at once, not at the next read (live 2026-10-10: the next read came many seconds later). The
    /// dropped answer is kept aside: the refresh reuses it only when the same process still holds the port
    /// (<see cref="LocalServerDetector.StillHolds"/>), so a server that moved onto the port is detected afresh.
    /// </summary>
    internal static void RefreshAll()
    {
        bool start;
        long gen;
        bool thisMachine;
        int port;
        lock (Gate)
        {
            _carry = _value is { Found: true } found ? found : null;
            _gen++;
            _value = null;
            _key = "";
            _at = default;
            gen = _gen;
            thisMachine = _lastThisMachine;
            port = _lastPort;
            start = port > 0 && thisMachine && _refreshingGen != gen;
            if (start) _refreshingGen = gen;
        }

        if (start) Start("local:" + port, thisMachine, port, gen);
    }

    /// <summary>Core unload (and tests): everything dropped, and any read still in flight is discarded when it lands.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            _gen++;
            _value = null;
            _key = "";
            _at = default;
            _refreshingGen = -1;
            _carry = null;
            _lastPort = 0;
            _lastThisMachine = false;
            _loggedKey = "";
            LogSink = null;
        }
    }
}
