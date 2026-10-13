using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Install;

/// <summary>
/// A <c>VRisingServer.exe</c> seen running from inside the validated folder: the pid, the full
/// image path (never a name match) and the raw command line when it could be read.
/// </summary>
internal sealed class ServerProcessInfo
{
    public ServerProcessInfo(int pid, string path, string? commandLine = null, string? workingDirectory = null)
    {
        Pid = pid;
        Path = path ?? "";
        CommandLine = commandLine ?? "";
        WorkingDirectory = workingDirectory ?? "";
    }

    public int Pid { get; }

    /// <summary>Full executable path inside the validated folder. Every stop matches on this.</summary>
    public string Path { get; }

    /// <summary>The live command line, or empty when the read failed. Empty means an argument-less launch record.</summary>
    public string CommandLine { get; }

    /// <summary>
    /// The process's own <c>CurrentDirectory.DosPath</c>, or empty when it could not be read — in
    /// which case the record falls back to the exe's folder (SERVERINST4).
    /// </summary>
    public string WorkingDirectory { get; }
}

/// <summary>
/// <c>BepInEx/progenitor-server-launch.json</c>: how the server Progenitor stopped had been
/// started, so <c>Start server</c> can put it back with exactly the same exe, arguments and
/// working directory. Written only after a stop Progenitor performed; never invented.
/// </summary>
internal sealed class ServerLaunchRecord
{
    public const int CurrentSchema = 1;
    public const string FileName = "progenitor-server-launch.json";
    public const string ByProgenitor = "progenitor";

    private readonly List<string> _args;

    public ServerLaunchRecord(string exe, IEnumerable<string>? args, string workingDirectory, DateTime stoppedUtc, string by = ByProgenitor)
    {
        Exe = exe ?? "";
        _args = args != null ? new List<string>(args) : new List<string>();
        WorkingDirectory = workingDirectory ?? "";
        StoppedUtc = stoppedUtc;
        By = string.IsNullOrWhiteSpace(by) ? ByProgenitor : by;
    }

    public int Schema => CurrentSchema;
    public string Exe { get; }
    public IReadOnlyList<string> Args => _args;
    public string WorkingDirectory { get; }
    public DateTime StoppedUtc { get; }
    public string By { get; }

    public static string PathFor(string serverRoot) =>
        System.IO.Path.Combine(serverRoot, LocalServerFolder.BepInExFolderName, FileName);

    /// <summary>Null when absent, torn, or a schema this build does not read.</summary>
    public static ServerLaunchRecord? TryRead(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot)) return null;
        string path = PathFor(serverRoot!);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != CurrentSchema)
                return null;
            string exe = ReadString(root, "exe");
            if (exe.Length == 0) return null;
            var args = new List<string>();
            if (root.TryGetProperty("args", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) args.Add(item.GetString() ?? "");
                }
            }

            DateTime stopped = DateTime.MinValue;
            if (root.TryGetProperty("stoppedUtc", out var t) && t.ValueKind == JsonValueKind.String
                && DateTime.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                stopped = parsed;
            return new ServerLaunchRecord(exe, args, ReadString(root, "workingDirectory"), stopped, ReadString(root, "by"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Temp-plus-move so a crash mid-write never leaves a half record.</summary>
    public void Write(string serverRoot) =>
        InstallStaging.AtomicWrite(PathFor(serverRoot), Encoding.UTF8.GetBytes(ToJson()));

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", CurrentSchema);
            w.WriteString("exe", Exe);
            w.WriteStartArray("args");
            for (int i = 0; i < _args.Count; i++) w.WriteStringValue(_args[i]);
            w.WriteEndArray();
            w.WriteString("workingDirectory", WorkingDirectory);
            w.WriteString("stoppedUtc", StoppedUtc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("by", By);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// The record for a live process: exe from the matched full path, arguments from its command
    /// line (the first token is the image, not an argument), working directory from the exe's
    /// folder. An unreadable command line yields an empty argument list rather than a guess.
    /// </summary>
    public static ServerLaunchRecord FromProcess(ServerProcessInfo process, DateTime stoppedUtc)
    {
        if (process == null) throw new ArgumentNullException(nameof(process));
        var parts = SplitCommandLine(process.CommandLine);
        var args = new List<string>();
        for (int i = 1; i < parts.Count; i++) args.Add(parts[i]);
        string exe = process.Path.Length > 0 ? process.Path : parts.Count > 0 ? parts[0] : "";
        // SERVERINST4: the process's own CurrentDirectory.DosPath when the PEB read produced one,
        // else the exe's folder — which is what the dedicated server is normally started from.
        string dir = process.WorkingDirectory;
        if (dir.Length == 0)
        {
            try { dir = System.IO.Path.GetDirectoryName(exe) ?? ""; }
            catch { dir = ""; }
        }

        return new ServerLaunchRecord(exe, args, dir, stoppedUtc);
    }

    /// <summary>
    /// Windows command-line split: a quoted run keeps its spaces, <c>""</c> inside a quoted run is
    /// a literal quote, backslashes are only special before a quote. Used to turn the live command
    /// line into the argument vector the record replays.
    /// </summary>
    public static IReadOnlyList<string> SplitCommandLine(string? commandLine)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return list;
        string line = commandLine!;
        var current = new StringBuilder();
        bool inQuotes = false, any = false;
        int backslashes = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                current.Append('\\', backslashes / 2);
                if (backslashes % 2 == 1)
                {
                    current.Append('"');
                }
                else if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                backslashes = 0;
                any = true;
                continue;
            }

            current.Append('\\', backslashes);
            backslashes = 0;
            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (any || current.Length > 0) list.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }

            current.Append(c);
            any = true;
        }

        current.Append('\\', backslashes);
        if (any || current.Length > 0) list.Add(current.ToString());
        return list;
    }

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>What the pane shows under <c>Stop server</c>, and <c>serverInstall.stop.state</c>.</summary>
internal enum ServerStopState
{
    Idle = 0,
    Requested = 1,
    Saving = 2,
    Stopped = 3,
    Failed = 4,
    ForcePrompt = 5,

    /// <summary>SERVERINST4: the server answered and accepted. The save is in flight.</summary>
    Replied = 6,

    /// <summary>SERVERINST4: the save was accepted but never landed. Nothing quit; the server is still running.</summary>
    SaveTimeout = 7,

    /// <summary>SERVERINST4: the server refused because this client's user is not an admin there.</summary>
    NotAdmin = 8,
}

internal sealed class ServerStopOutcome
{
    public ServerStopOutcome(ServerStopState state, string reason, ServerLaunchRecord? record = null)
    {
        State = state;
        Reason = reason ?? "";
        Record = record;
    }

    public ServerStopState State { get; }
    public string Reason { get; }
    /// <summary>The launch record written by a successful stop, else null.</summary>
    public ServerLaunchRecord? Record { get; }
}

internal sealed class ServerStartOutcome
{
    public ServerStartOutcome(bool ok, int pid, string line)
    {
        Ok = ok;
        Pid = pid;
        Line = line ?? "";
    }

    public bool Ok { get; }
    public int Pid { get; }
    public string Line { get; }
}

/// <summary>
/// Stop and start the user's OWN validated local dedicated-server folder. Every operation
/// matches the process by its full executable path inside that folder (never by name), so a
/// <c>VRisingServer.exe</c> anywhere else — including the live dedicated root — is never seen
/// and never signaled.
///
/// <para><b>Graceful route.</b> When the folder is identified as the server this client is
/// talking to and that server answered the Progenitor handshake (the install ledger is not required), the stop is a
/// server-side <c>.stop-server</c> command: the server saves the world and only then quits. The
/// identity check is the cheapest reliable one available offline — the folder's own
/// <c>ServerHostSettings.json</c> (found from the launch record's <c>-persistentDataPath</c>
/// when there is one, else under the install) names the game port, and that port must equal the
/// port the client is currently connected to (<see cref="ConnectedServerPort"/>). It is a file
/// read plus a value this process already holds: no socket, no dev-bridge token, no
/// network.</para>
///
/// <para><b>Force route.</b> Without Progenitor on that server, without a usable identity, or
/// when the graceful route is silent for <see cref="GracefulTimeoutSeconds"/>, the pane offers
/// <c>Stop server (force)</c> behind its own prompt; that is a <c>Kill</c> on the matched
/// process and nothing else.</para>
/// </summary>
internal static class ServerProcessControl
{
    public const int GracefulTimeoutSeconds = 20;
    public const int ForceExitTimeoutSeconds = 10;
    public const string StopCommandName = "stop-server";

    public const string ReasonNoFolder = "No validated local dedicated-server folder.";
    public const string ReasonNotRunning = "No VRisingServer.exe is running from that folder.";
    public const string ReasonNoTransport = "No command route to that server from this client.";
    public const string ReasonNotProgenitor = "Progenitor is not installed on that server.";
    /// <summary>
    /// SERVERINST5: the port mismatch names both values. "This client is not connected to that
    /// server." was true but unactionable — it did not say which server the client <em>was</em> on,
    /// so a wrong <c>-persistentDataPath</c> read and a genuinely different instance looked
    /// identical. A port of 0 (nothing recorded yet) prints <c>unknown</c>.
    /// </summary>
    public static string ReasonNotThisServer(int connectedPort, int folderPort) =>
        "This client is connected to port " + PortText(connectedPort)
        + "; that folder's server uses port " + PortText(folderPort) + ".";

    private static string PortText(int port) =>
        port > 0 ? port.ToString(CultureInfo.InvariantCulture) : "unknown";

    /// <summary>
    /// SERVERINST5: this Bootstrap predates the <c>LaunchGame</c> connect record
    /// (<c>BootstrapLink.RecordsConnectPort</c> false), so no port is knowable in this process and a
    /// Core reload cannot fix it — only restarting the client on the current Bootstrap can.
    /// </summary>
    public const string ReasonBootstrapTooOld =
        "This Bootstrap does not record the joined server's port (restart with the current Bootstrap).";
    public const string ReasonGracefulTimeout = "The server did not stop within " + "20" + " seconds.";
    public const string ReasonStillRunning = "The process did not exit.";
    public const string ReasonAlreadyRunning = "That server is already running.";
    public const string ReasonNoLaunchRecord = "no launch record yet (stop it once through Progenitor, or start it yourself)";
    public const string StoppedLine = "Stopped";

    /// <summary>SERVERINST4: the graceful route needs a completed Progenitor handshake with that server.</summary>
    public const string ReasonNoHandshake = "This client has not completed a Progenitor handshake with that server.";

    /// <summary>SERVERINST4: the server answered that this client's user is not an admin there.</summary>
    public const string ReasonNotAdmin = "That server refused: only an admin can stop it.";

    /// <summary>SERVERINST4: the save was accepted but no save file appeared, so nothing quit.</summary>
    public const string ReasonSaveTimeout =
        "The server accepted the stop and triggered a save, but no save file appeared within "
        + "15 seconds. It did not quit.";

    /// <summary>SERVERINST4: the server answered that the save itself failed.</summary>
    public const string ReasonSaveFailed = "The server could not save the world, so it is still running.";

    /// <summary>Longest <c>process.commandLine</c> the readback carries.</summary>
    public const int CommandLineReadbackMax = 512;

    /// <summary>Test seam: server root → the running process, or null. Production uses <see cref="ServerInstall.FindRunningServer"/> plus a command-line read.</summary>
    internal static Func<string, ServerProcessInfo?>? RunningProbe { get; set; }

    /// <summary>Test seam: kill the matched process. Production calls <c>Process.Kill</c> on that pid after re-matching its full path.</summary>
    internal static Func<ServerProcessInfo, bool>? KillOverride { get; set; }

    /// <summary>Test seam: exe, args, workingDirectory → pid. Production is <c>Process.Start</c> with no shell and no window changes.</summary>
    internal static Func<string, IReadOnlyList<string>, string, int>? StartOverride { get; set; }

    /// <summary>
    /// Test seam and the client's command route: send a <see cref="StopServerRequest"/> carrying
    /// this correlation nonce. Production is
    /// <c>CustomEvents.SendToServer&lt;StopServerRequest&gt;</c> on
    /// <see cref="Networking.CustomEventPolicy.StopServerEventId"/>, wired by
    /// <see cref="StopServerEvents"/>; null means this client has no route, and the stop falls
    /// through to the force prompt.
    /// </summary>
    internal static Func<int, bool>? SubmitCommand { get; set; }

    /// <summary>
    /// Test seam and the client's reply inbox: the <see cref="StopServerReply"/> received for this
    /// nonce, or null while none has arrived. Production is <see cref="StopServerEvents.ReplyFor"/>.
    /// </summary>
    internal static Func<int, StopServerReply?>? PollReply { get; set; }

    /// <summary>
    /// SERVERINST4 identity check: true when this client's Progenitor handshake with the connected
    /// server completed. Production reads <c>Handshake.LastClientResult == "ok"</c>, which is set
    /// only from a <c>PrhQ</c> Result packet — a packet nothing but a Progenitor server sends. The
    /// graceful route is therefore never offered to a server that is not running Progenitor, on top
    /// of the ledger and port checks.
    /// </summary>
    internal static Func<bool>? ProgenitorHandshakeOk { get; set; }

    /// <summary>
    /// Test seam: every state the current stop passes through, in order. The pane records the
    /// sequence so <c>stop.state</c> reports <c>requested → replied → saving → stopped</c> rather
    /// than only the outcome.
    /// </summary>
    internal static Action<ServerStopState>? StopProgress { get; set; }

    /// <summary>Game port this client is currently connected to, or 0 when not connected. Used only by the identity check.</summary>
    internal static Func<int>? ConnectedServerPort { get; set; }

    /// <summary>
    /// SERVERINST5: whether the Bootstrap in this process records the joined port at all
    /// (<c>BootstrapLink.RecordsConnectPort</c>, wired by <see cref="StopServerEvents.Install"/>).
    /// Null means "assume it does": that is the shape every test that only sets
    /// <see cref="ConnectedServerPort"/> expects, and it keeps a missing wire from inventing a
    /// Bootstrap-too-old verdict. A throw fails closed to too-old.
    /// </summary>
    internal static Func<bool>? ConnectPortRecorded { get; set; }

    /// <summary>
    /// SERVERINST5 readback only: <c>"&lt;protocol&gt;://&lt;address&gt;:&lt;port&gt;"</c> for the
    /// server this client joined, or "". Never part of the gate — the gate compares ports.
    /// </summary>
    internal static Func<string>? ConnectedServerDescription { get; set; }

    /// <summary>The <c>process.connectedTo</c> readback value, or "" when nothing was recorded.</summary>
    public static string ConnectedTo()
    {
        try { return ConnectedServerDescription?.Invoke() ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// <c>"&lt;protocol&gt;://&lt;address&gt;:&lt;port&gt;"</c>, or "" when the address or the port
    /// is missing. A missing protocol still formats (<c>"127.0.0.1:9976"</c>): the address and port
    /// are what identify the server, the transport name is a label.
    /// </summary>
    public static string FormatConnectedTo(string? protocol, string? address, int port)
    {
        if (string.IsNullOrWhiteSpace(address) || port <= 0) return "";
        string head = string.IsNullOrWhiteSpace(protocol) ? "" : protocol!.Trim() + "://";
        return head + address!.Trim() + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    internal static Func<DateTime>? ClockUtc { get; set; }

    /// <summary>Test seam: the poll wait between process checks. Production sleeps; tests advance their clock instead.</summary>
    internal static Action<int>? SleepMs { get; set; }

    internal static void ResetForTests()
    {
        RunningProbe = null;
        RunningCache.Clear();
        KillOverride = null;
        StartOverride = null;
        SubmitCommand = null;
        PollReply = null;
        ProgenitorHandshakeOk = null;
        StopProgress = null;
        ConnectedServerPort = null;
        ConnectPortRecorded = null;
        ConnectedServerDescription = null;
        ClockUtc = null;
        SleepMs = null;
        PidExitProbe = null;
        ScanProbe = null;
        UdpOwnerProbe = null;
        DefaultSaveRootForTests = null;
        UseStartMutex = false;
        lock (FenceCacheGate)
        {
            FenceCache.Clear();
            ReplayCache.Clear();
        }
        lock (StartGate) _starting = false;
    }

    // ---- Review H1 (2026-10-08): the live-server fence -----------------------------------------------------------

    /// <summary>Review H1: Progenitor never stops, kills or starts the live dedicated server.</summary>
    public const string ReasonLiveServer = "That is the live dedicated server; Progenitor never stops or starts it.";

    /// <summary>
    /// Review H1: the fence every stop, kill and start passes: the folder, and a launch record's exe, working directory
    /// and arguments, or a running process's path and command line, must not name the live dedicated server
    /// (<see cref="WorldRestore.WorldRestorePaths.IsLiveServerPath"/>, <see cref="Hosted.HostedPathRules.IsForbiddenLiveServerPath"/>,
    /// <see cref="WorldRestore.WorldRestorePaths.IsLiveServerCommandLine"/>).
    /// </summary>
    internal static bool IsLiveServer(string? root, ServerLaunchRecord? record = null, ServerProcessInfo? process = null)
    {
        try
        {
            if (LivePath(root)) return true;
            if (record != null && (LivePath(record.Exe) || LivePath(record.WorkingDirectory)
                || WorldRestore.WorldRestorePaths.IsLiveServerCommandLine(record.Args, record.WorkingDirectory))) return true;
            if (process != null)
            {
                string wd = process.WorkingDirectory.Length > 0 ? process.WorkingDirectory : SafeDirectoryOf(process.Path);
                if (LivePath(process.Path) || LivePath(process.WorkingDirectory)
                    || WorldRestore.WorldRestorePaths.IsLiveServerCommandLine(ServerLaunchRecord.SplitCommandLine(process.CommandLine), wd)) return true;
            }

            return false;
        }
        catch { return true; /* a fence that cannot decide refuses */ }
    }

    private static string SafeDirectoryOf(string path)
    {
        try { return System.IO.Path.GetDirectoryName(path) ?? ""; }
        catch { return ""; }
    }

    private static readonly object FenceCacheGate = new();
    private static readonly Dictionary<string, (bool Live, long At)> FenceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Review L3 (2026-10-08): <see cref="IsLiveServer"/> for the frame's caption reads, reused for 30 s per root and
    /// process (path, command line, working directory), so the path resolution does not run on every caption sync.
    /// The stop and the start themselves always ask <see cref="IsLiveServer"/> afresh.
    /// </summary>
    internal static bool IsLiveServerCached(string? root, ServerProcessInfo? process = null)
    {
        string key = (root ?? "") + "|" + (process?.Path ?? "") + "|" + (process?.CommandLine ?? "") + "|" + (process?.WorkingDirectory ?? "");
        long now = Stopwatch.GetTimestamp();
        lock (FenceCacheGate)
        {
            if (FenceCache.TryGetValue(key, out var hit) && now - hit.At < 30 * Stopwatch.Frequency) return hit.Live;
        }

        bool live = IsLiveServer(root, process: process);
        lock (FenceCacheGate)
        {
            if (FenceCache.Count > 64) FenceCache.Clear();
            FenceCache[key] = (live, now);
        }

        return live;
    }

    private static readonly Dictionary<string, (string Refusal, long At)> ReplayCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Review L3: <see cref="StartRefusal"/> for the record a running process would leave (what "Save, stop and start
    /// again" would replay), reused for 30 s per root and process, for the frame's caption reads.
    /// </summary>
    internal static string ReplayRefusalCached(string root, ServerProcessInfo process)
    {
        string key = root + "|" + process.Path + "|" + process.CommandLine + "|" + process.WorkingDirectory;
        long now = Stopwatch.GetTimestamp();
        lock (FenceCacheGate)
        {
            if (ReplayCache.TryGetValue(key, out var hit) && now - hit.At < 30 * Stopwatch.Frequency) return hit.Refusal;
        }

        string refusal;
        try { refusal = StartRefusal(root, ServerLaunchRecord.FromProcess(process, Now())); }
        catch (Exception e) { refusal = e.Message; }
        lock (FenceCacheGate)
        {
            if (ReplayCache.Count > 64) ReplayCache.Clear();
            ReplayCache[key] = (refusal, now);
        }

        return refusal;
    }

    // ---- Review M2 (2026-10-08): a start names its own save folder, never the default one ----------------------------

    public const string ReasonNoPersistentDataPath =
        "The launch record has no -persistentDataPath, so the server would use the default save folder another server may use.";

    public const string ReasonDefaultSavePath =
        "The launch record uses the default save folder (AppData\\LocalLow\\Stunlock Studios\\VRisingServer), which the live server may use.";

    /// <summary>Tests: the default save folder (null: the user's <c>AppData\LocalLow\Stunlock Studios\VRisingServer</c>).</summary>
    internal static string? DefaultSaveRootForTests { get; set; }

    /// <summary>The dedicated server's default save folder: the one used without <c>-persistentDataPath</c>.</summary>
    internal static string DefaultSaveRoot
    {
        get
        {
            if (DefaultSaveRootForTests != null) return DefaultSaveRootForTests;
            string home = "";
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch { home = ""; }
            return home.Length == 0 ? "" : System.IO.Path.Combine(home, "AppData", "LocalLow", "Stunlock Studios", "VRisingServer");
        }
    }

    /// <summary>The value of <c>-persistentDataPath</c> (or <c>-persistentDataPath=value</c>), resolved against the working directory; null when absent.</summary>
    internal static string? PersistentDataPathOf(IReadOnlyList<string> args, string? workingDirectory)
    {
        if (args == null) return null;
        for (int i = 0; i < args.Count; i++)
        {
            string a = (args[i] ?? "").Trim();
            string? value = null;
            if (string.Equals(a, "-persistentDataPath", StringComparison.OrdinalIgnoreCase))
            {
                // Final review: a following -flag is the next argument, not the value.
                value = i + 1 < args.Count ? args[i + 1] : "";
                if (value.TrimStart().StartsWith("-", StringComparison.Ordinal)) return null;
            }
            else if (a.StartsWith("-persistentDataPath=", StringComparison.OrdinalIgnoreCase))
                value = a.Substring("-persistentDataPath=".Length);
            if (value == null) continue;
            value = value.Trim().Trim('"').Trim();
            if (value.Length == 0) return null;
            try
            {
                if (!System.IO.Path.IsPathRooted(value) && !string.IsNullOrWhiteSpace(workingDirectory))
                    value = System.IO.Path.Combine(workingDirectory!, value);
            }
            catch { /* keep the text */ }

            return value;
        }

        return null;
    }

    /// <summary>Final review: "under" with a comparison that cannot be made counting as under (not allowed).</summary>
    private static bool UnderOrUnknown(string path, string root)
    {
        try
        {
            string p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>
    /// "" when a launch record may start this folder's server, else why not: the live-server fence (H1), exactly the
    /// folder's own exe and a working directory inside it (M-d), and its own save folder: a <c>-persistentDataPath</c>
    /// that is not the default one (M2).
    /// </summary>
    internal static string StartRefusal(string root, ServerLaunchRecord record)
    {
        if (IsLiveServer(root, record)) return ReasonLiveServer;
        if (!IsFoldersServerExe(root, record.Exe)) return ReasonNotServerExe;
        if (record.WorkingDirectory.Length > 0 && !IsInside(root, record.WorkingDirectory)) return ReasonBadWorkingDirectory;
        return SaveFolderRefusal(record.Args, record.WorkingDirectory);
    }

    /// <summary>
    /// Review M2: "" when <paramref name="args"/> give the server its own save folder (a <c>-persistentDataPath</c> that is
    /// not the default one), else why not. LOCALDETECT1 applies the same rule to an auto-detected server.
    /// </summary>
    internal static string SaveFolderRefusal(IReadOnlyList<string> args, string? workingDirectory)
    {
        string? data = PersistentDataPathOf(args, workingDirectory);
        if (data == null) return ReasonNoPersistentDataPath;
        string fallback = DefaultSaveRoot;
        if (fallback.Length > 0)
        {
            try
            {
                if (UnderOrUnknown(WorldRestore.WorldRestorePaths.FinalPath(data), WorldRestore.WorldRestorePaths.FinalPath(fallback))
                    || UnderOrUnknown(WorldRestore.WorldRestorePaths.PlainFullPath(data), WorldRestore.WorldRestorePaths.PlainFullPath(fallback)))
                    return ReasonDefaultSavePath;
            }
            catch { return ReasonDefaultSavePath; }
        }

        return "";
    }

    /// <summary>LOCALDETECT1 review: the running server's command line could not be read, so its arguments are unknown.</summary>
    public const string ReasonCommandLineUnknown =
        "Progenitor can't read that server's command line, so it can't rule out the live server's data; it sends no stop.";

    /// <summary>
    /// LOCALDETECT1 review (fail closed): the stop gate for an auto-detected local server. <see cref="GracefulAvailable"/>,
    /// and the running process's own command line must have been read (an unreadable one would pass the argument fence
    /// blind), and its save folder must be its own (<see cref="SaveFolderRefusal"/>).
    /// </summary>
    public static bool GracefulAvailableDetected(string? serverRoot, out string reason)
    {
        if (!GracefulAvailable(serverRoot, out reason)) return false;
        var running = Running(serverRoot);
        if (running == null) { reason = ReasonNotRunning; return false; }
        if (running.CommandLine.Trim().Length == 0) { reason = ReasonCommandLineUnknown; return false; }
        string wd = running.WorkingDirectory.Length > 0 ? running.WorkingDirectory : SafeDirectoryOf(running.Path);
        string save = SaveFolderRefusal(ServerLaunchRecord.SplitCommandLine(running.CommandLine), wd);
        if (save.Length > 0) { reason = save; return false; }
        reason = "";
        return true;
    }

    private static bool LivePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (WorldRestore.WorldRestorePaths.IsLiveServerPath(path) || Hosted.HostedPathRules.IsForbiddenLiveServerPath(path));

    // ---- Review M-c (2026-10-08): one start at a time, process-wide ------------------------------------------------

    private static readonly object StartGate = new();
    private static bool _starting;

    public const string ReasonStartInFlight = "A start of that server is already in progress.";

    /// <summary>True while a start (Preferences' Start server, or the wait-and-start after Save, stop and start again) runs.</summary>
    internal static bool StartInFlight
    {
        get { lock (StartGate) return _starting; }
    }

    /// <summary>
    /// Review L4 (2026-10-08): the start lock spans Core generations and processes: a named mutex in this session's
    /// namespace, held by the claiming thread (claim and release run in the same method), next to the in-process flag
    /// (a mutex is re-entrant on its own thread).
    /// </summary>
    internal const string StartMutexName = "Local\\Progenitor.ServerStart";

    [ThreadStatic] private static System.Threading.Mutex? _startMutex;

    /// <summary>Tests: false skips the named mutex (parallel test hosts share the session namespace).</summary>
    internal static bool UseStartMutex { get; set; } = true;

    private static bool TryClaimStart()
    {
        lock (StartGate)
        {
            if (_starting) return false;
            _starting = true;
        }

        if (!UseStartMutex) return true;
        System.Threading.Mutex? mutex = null;
        try
        {
            mutex = new System.Threading.Mutex(false, StartMutexName);
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (System.Threading.AbandonedMutexException) { owned = true; }
            if (owned)
            {
                _startMutex = mutex;
                return true;
            }
        }
        catch { /* no mutex: refuse below, fail closed */ }

        try { mutex?.Dispose(); } catch { /* */ }
        lock (StartGate) _starting = false;
        return false;
    }

    private static void ReleaseStart()
    {
        var mutex = _startMutex;
        _startMutex = null;
        if (mutex != null)
        {
            try { mutex.ReleaseMutex(); } catch { /* */ }
            try { mutex.Dispose(); } catch { /* */ }
        }

        lock (StartGate) _starting = false;
    }

    // ---- Review M1 (2026-10-08): the local stop goes only to the process bound to the joined port --------------------

    public const string ReasonPortOwnerUnknown = "Progenitor can't tell which process uses the joined port, so it sends no stop.";

    public static string ReasonPortNotThisProcess(int port) =>
        "Port " + PortText(port) + " belongs to another process than this folder's server, so Progenitor sends no stop there.";

    /// <summary>Test seam: the pids bound to a UDP port (null: unknown). Null probe: the system's UDP table.</summary>
    internal static Func<int, IReadOnlyCollection<int>?>? UdpOwnerProbe { get; set; }

    private static readonly object UdpGate = new();
    private static int _udpPort;
    private static IReadOnlyCollection<int>? _udpOwners;
    private static long _udpAt;

    /// <summary>The pids with a UDP socket on <paramref name="port"/> (IPv4 and IPv6), reused for 2 s; null when the table could not be read.</summary>
    internal static IReadOnlyCollection<int>? UdpPortOwners(int port)
    {
        if (port <= 0) return null;
        if (UdpOwnerProbe != null) return UdpOwnerProbe(port);
        long now = Stopwatch.GetTimestamp();
        lock (UdpGate)
        {
            if (_udpPort == port && _udpAt != 0 && now - _udpAt < 2 * Stopwatch.Frequency) return _udpOwners;
        }

        IReadOnlyCollection<int>? owners = null;
        try
        {
            var set = new HashSet<int>();
            // Final review: either family unread is "unknown", never a partial answer.
            bool both = ReadUdpOwners(2, 12, 4, 8, port, set);
            both &= ReadUdpOwners(23, 28, 20, 24, port, set);
            owners = both ? set : null;
        }
        catch { owners = null; }

        lock (UdpGate)
        {
            _udpPort = port;
            _udpOwners = owners;
            _udpAt = now;
        }

        return owners;
    }

    /// <summary>One address family's <c>MIB_UDP(6)TABLE_OWNER_PID</c>; true when it was read.</summary>
    private static bool ReadUdpOwners(int family, int rowSize, int portOffset, int pidOffset, int port, HashSet<int> into)
    {
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, 1, 0);
        // The table can grow between the size query and the read (ERROR_INSUFFICIENT_BUFFER, 122): ask again, a few times.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (size <= 0) return false;
            size += 1024;
            IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                uint result = GetExtendedUdpTable(buffer, ref size, false, family, 1, 0);
                if (result == 122) continue;
                if (result != 0) return false;
                ReadUdpRows(buffer, rowSize, portOffset, pidOffset, port, into);
                return true;
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        }

        return false;
    }

    private static void ReadUdpRows(IntPtr buffer, int rowSize, int portOffset, int pidOffset, int port, HashSet<int> into)
    {
        int count = System.Runtime.InteropServices.Marshal.ReadInt32(buffer);
        for (int i = 0; i < count; i++)
        {
            IntPtr row = buffer + 4 + i * rowSize;
            int raw = System.Runtime.InteropServices.Marshal.ReadInt32(row, portOffset);
            int rowPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
            if (rowPort == port) into.Add(System.Runtime.InteropServices.Marshal.ReadInt32(row, pidOffset));
        }
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    public static string StopStateName(ServerStopState state) => state switch
    {
        ServerStopState.Requested => "requested",
        ServerStopState.Saving => "saving",
        ServerStopState.Stopped => "stopped",
        ServerStopState.Failed => "failed",
        ServerStopState.ForcePrompt => "force-prompt",
        ServerStopState.Replied => "replied",
        ServerStopState.SaveTimeout => "save-timeout",
        ServerStopState.NotAdmin => "not-admin",
        _ => "idle",
    };

    /// <summary>
    /// <c>process.commandLine</c>: the live command line without the image token (which is already
    /// <c>process.path</c>), each token that contains whitespace re-quoted, truncated to
    /// <see cref="CommandLineReadbackMax"/> characters with a trailing ellipsis. Never invented — an
    /// unreadable command line reads as <c>""</c>.
    /// </summary>
    public static string RedactCommandLine(string? commandLine)
    {
        var parts = ServerLaunchRecord.SplitCommandLine(commandLine);
        if (parts.Count <= 1) return "";
        var sb = new StringBuilder();
        for (int i = 1; i < parts.Count; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            string token = parts[i];
            bool quote = token.Length == 0 || token.IndexOf(' ') >= 0 || token.IndexOf('\t') >= 0;
            if (quote) sb.Append('"').Append(token).Append('"');
            else sb.Append(token);
            if (sb.Length > CommandLineReadbackMax) break;
        }

        string text = sb.ToString();
        return text.Length <= CommandLineReadbackMax
            ? text
            : text.Substring(0, CommandLineReadbackMax - 1) + "…";
    }

    public static string ForcePromptBody(string? serverRoot) =>
        "This ends " + LocalServerFolder.ServerExecutableName + " running from " + (serverRoot ?? "") +
        " without a save. Unsaved progress since the last autosave is lost.";

    public static string StartedLine(int pid) => "Started (pid " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";

    public static string RefusedLine(string reason) => "Refused: " + (reason ?? "");

    public static string StartUnavailableLine() =>
        "Start server unavailable — " + ReasonNoLaunchRecord;

    /// <summary>The running server of <paramref name="serverRoot"/>, or null. Never signals it.</summary>
    public static ServerProcessInfo? Running(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot)) return null;
        if (RunningProbe != null) return RunningProbe(serverRoot!);
        // Review 2026-10-07: a process scan and a command-line read never run on the main thread. The frame (route
        // decisions, the Configuration and Install panes, the rejoin) reads the last answer and, when it is older than
        // RunningCacheSeconds, starts one background refresh; until the first answer arrives the server reads as not
        // seen, the safe default (no local route, no loopback rejoin).
        if (OnMainThread()) return RunningCache.Get(serverRoot!);
        return RunningNow(serverRoot);
    }

    /// <summary>
    /// The scan now, on the calling thread, and the cache updated. Off the main thread, or for an explicit one-shot user
    /// action that must see the process itself (Force stop and its wait for exit).
    /// </summary>
    public static ServerProcessInfo? RunningNow(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot)) return null;
        if (RunningProbe != null) return RunningProbe(serverRoot!);
        ServerProcessInfo? info = null;
        string? path = ServerInstall.FindRunningServerNow(serverRoot!);
        if (path != null) info = MatchByPath(path) ?? new ServerProcessInfo(0, path);
        RunningCache.Store(serverRoot!, info);
        return info;
    }

    /// <summary>Seconds a cached answer serves the main thread before a background refresh starts.</summary>
    internal const double RunningCacheSeconds = 3;

    /// <summary>Is the caller the game's main thread (tests: never, unless set).</summary>
    internal static Func<bool> OnMainThread { get; set; } =
#if PROGENITOR_TESTS
        () => false;
#else
        () => Progenitor.Dev.MainThread.IsMainThread;
#endif

    /// <summary>
    /// Review 2026-10-07: the last answer per server folder, refreshed in the background. Thread-safe; a refresh per folder
    /// at a time.
    /// </summary>
    internal static class RunningCache
    {
        private sealed class Entry
        {
            public ServerProcessInfo? Value;
            public DateTime At;
            public bool Known;
            public bool Refreshing;
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tests: run the refresh inline instead of on the thread pool.</summary>
        internal static Action<Action>? Scheduler { get; set; }

        internal static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        internal static ServerProcessInfo? Get(string root)
        {
            bool start = false;
            ServerProcessInfo? value;
            lock (Gate)
            {
                if (!Entries.TryGetValue(root, out var e)) Entries[root] = e = new Entry();
                value = e.Value;
                bool stale = !e.Known || (Clock() - e.At).TotalSeconds >= RunningCacheSeconds;
                if (stale && !e.Refreshing)
                {
                    e.Refreshing = true;
                    start = true;
                }
            }

            if (start)
            {
                void Refresh()
                {
                    try { RunningNow(root); }
                    catch { Store(root, null); }
                }

                try
                {
                    if (Scheduler != null) Scheduler(Refresh);
                    else System.Threading.Tasks.Task.Run(Refresh);
                }
                catch { lock (Gate) if (Entries.TryGetValue(root, out var e)) e.Refreshing = false; }
            }

            return value;
        }

        /// <summary>Whether an answer for <paramref name="root"/> has arrived yet.</summary>
        internal static bool Known(string root)
        {
            lock (Gate) return Entries.TryGetValue(root ?? "", out var e) && e.Known;
        }

        internal static void Store(string root, ServerProcessInfo? value)
        {
            lock (Gate)
            {
                if (!Entries.TryGetValue(root, out var e)) Entries[root] = e = new Entry();
                e.Value = value;
                e.At = Clock();
                e.Known = true;
                e.Refreshing = false;
            }
        }

        internal static void Clear()
        {
            lock (Gate) Entries.Clear();
        }
    }

    /// <summary>
    /// True when the graceful command route may be used for this folder: a command transport exists, and the folder's game port is the one this client is
    /// connected to. <paramref name="reason"/> names the first check that failed.
    /// </summary>
    public static bool GracefulAvailable(string? serverRoot, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(serverRoot)) { reason = ReasonNoFolder; return false; }
        if (IsLiveServer(serverRoot)) { reason = ReasonLiveServer; return false; }
        // The install ledger is NOT required (live 2026-09-12: a tool-deployed server has no ledger
        // yet answers the Progenitor handshake); the handshake check below is the proof of Progenitor.
        if (SubmitCommand == null) { reason = ReasonNoTransport; return false; }
        // SERVERINST5, before the comparison: an older Bootstrap records no port, and reporting that
        // as a mismatch would send the user hunting a port problem a Core reload cannot fix.
        bool recorded;
        try { recorded = ConnectPortRecorded?.Invoke() ?? true; }
        catch { recorded = false; }
        if (!recorded) { reason = ReasonBootstrapTooOld; return false; }
        int connected = 0;
        try { connected = ConnectedServerPort?.Invoke() ?? 0; }
        catch { connected = 0; }
        int folderPort = HostPort(serverRoot!);
        if (connected <= 0 || folderPort <= 0 || connected != folderPort)
        {
            reason = ReasonNotThisServer(connected, folderPort);
            return false;
        }
        // SERVERINST4, last because it is the strictest: the port says "this is the folder I am
        // talking to", the handshake says "and the thing answering there is Progenitor".
        bool handshake;
        try { handshake = ProgenitorHandshakeOk?.Invoke() ?? false; }
        catch { handshake = false; }
        if (!handshake) { reason = ReasonNoHandshake; return false; }
        // Review M1 (2026-10-08): the port alone does not say which server answers there (a test folder configured with the
        // live server's port, while connected to the live server). The folder's own server must be running, and it must be
        // the process bound to the joined port. Unknown either way: no stop.
        var running = Running(serverRoot);
        if (running == null) { reason = ReasonNotRunning; return false; }
        // The per-process fence uncached here (final review): this check gates the stop itself.
        if (IsLiveServer(serverRoot, process: running)) { reason = ReasonLiveServer; return false; }
        var owners = UdpPortOwners(connected);
        if (owners == null || running.Pid <= 0) { reason = ReasonPortOwnerUnknown; return false; }
        bool bound = false;
        foreach (int pid in owners) bound |= pid == running.Pid;
        if (!bound) { reason = ReasonPortNotThisProcess(connected); return false; }
        return true;
    }

    /// <summary>
    /// The game port this folder's server listens on, from its <c>ServerHostSettings.json</c>
    /// (<c>-persistentDataPath</c> of the launch record first, then the install's own settings
    /// folders), or 0 when no settings file could be read.
    /// </summary>
    public static int HostPort(string serverRoot)
    {
        foreach (string candidate in HostSettingsCandidates(serverRoot))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(candidate));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("Port", out var p) && p.ValueKind == JsonValueKind.Number) return p.GetInt32();
                if (root.TryGetProperty("GamePort", out var g) && g.ValueKind == JsonValueKind.Number) return g.GetInt32();
            }
            catch { /* an unreadable settings file is "unknown port", never a match */ }
        }

        return 0;
    }

    internal static IReadOnlyList<string> HostSettingsCandidates(string serverRoot)
    {
        var list = new List<string>();
        // Live 2026-09-12: before the first stop there is no launch record, yet the running
        // process's own command line (PEB-read) names -persistentDataPath, where the real
        // ServerHostSettings.json lives; the install's StreamingAssets copy is only the default.
        try
        {
            var live = Running(serverRoot);
            if (live != null) AddPersistentDataCandidates(list, ServerLaunchRecord.SplitCommandLine(live.CommandLine));
        }
        catch { /* the probe is best effort here; the record and folder candidates follow */ }
        var record = ServerLaunchRecord.TryRead(serverRoot);
        if (record != null) AddPersistentDataCandidates(list, record.Args);

        list.Add(Path.Combine(serverRoot, "Settings", "ServerHostSettings.json"));
        list.Add(Path.Combine(serverRoot, "VRisingServer_Data", "StreamingAssets", "Settings", "ServerHostSettings.json"));
        return list;
    }

    /// <summary>Tests: the host-settings candidates a command line names.</summary>
    internal static IReadOnlyList<string> HostSettingsCandidatesFromArgs(IReadOnlyList<string> args)
    {
        var list = new List<string>();
        AddPersistentDataCandidates(list, args);
        return list;
    }

    private static void AddPersistentDataCandidates(List<string> list, IReadOnlyList<string> args)
    {
        // Final review: both spellings (-persistentDataPath value, -persistentDataPath=value), as the start reads them.
        string? data = PersistentDataPathOf(args, null);
        if (data == null) return;
        string candidate;
        try { candidate = Path.Combine(data, "Settings", "ServerHostSettings.json"); }
        catch { return; }
        if (!list.Exists(c => string.Equals(c, candidate, StringComparison.OrdinalIgnoreCase))) list.Add(candidate);
    }

    /// <summary>
    /// Graceful stop: send a <see cref="StopServerRequest"/> on Progenitor's own reliable event
    /// channel, then watch for the matching <see cref="StopServerReply"/> and for the matched
    /// process to exit, for up to <see cref="GracefulTimeoutSeconds"/>. Exit writes the launch
    /// record from the command line captured before the stop.
    ///
    /// <para>The reply is what this route gained in SERVERINST4: a refusal now <em>says so</em>
    /// (<see cref="ServerStopState.NotAdmin"/>, <see cref="ServerStopState.SaveTimeout"/>,
    /// <see cref="ServerStopState.Failed"/>) instead of looking like silence, and only real silence
    /// ends at <see cref="ServerStopState.ForcePrompt"/>. This route never kills anything: a named
    /// refusal is a stop that did not happen, not a reason to skip the save.</para>
    /// </summary>
    public static ServerStopOutcome Stop(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot))
            return new ServerStopOutcome(ServerStopState.Failed, ReasonNoFolder);
        string root = serverRoot!;
        if (IsLiveServer(root)) return new ServerStopOutcome(ServerStopState.Failed, ReasonLiveServer);
        // An explicit one-shot action: it must see the process itself (and waits for its exit), so it scans now.
        var process = RunningNow(root);
        if (process == null) return new ServerStopOutcome(ServerStopState.Failed, ReasonNotRunning);
        if (IsLiveServer(root, process: process)) return new ServerStopOutcome(ServerStopState.Failed, ReasonLiveServer);

        if (!GracefulAvailable(root, out string why))
            return new ServerStopOutcome(ServerStopState.ForcePrompt, why);

        int nonce = StopServerEvents.NextNonce();
        bool sent;
        try { sent = SubmitCommand!.Invoke(nonce); }
        catch { sent = false; }
        if (!sent) return new ServerStopOutcome(ServerStopState.ForcePrompt, ReasonNoTransport);
        Report(ServerStopState.Requested);

        DateTime deadline = Now().AddSeconds(GracefulTimeoutSeconds);
        bool accepted = false;
        while (true)
        {
            StopServerReply? reply = null;
            try { reply = PollReply?.Invoke(nonce); }
            catch { reply = null; }
            if (reply is { } r)
            {
                if (!r.IsAccepted) return Refused(r.Reason);
                if (!accepted)
                {
                    accepted = true;
                    Report(ServerStopState.Replied);
                    if (r.Reason == StopReplyReason.Saving) Report(ServerStopState.Saving);
                }
            }

            if (Running(root) == null) break;
            if (Now() >= deadline)
                return new ServerStopOutcome(ServerStopState.ForcePrompt, ReasonGracefulTimeout);
            try { (SleepMs ?? DefaultSleep)(250); }
            catch { /* a refused sleep only tightens the loop */ }
        }

        var record = WriteLaunchRecord(root, process);
        Report(ServerStopState.Stopped);
        return new ServerStopOutcome(ServerStopState.Stopped, StoppedLine, record);
    }

    /// <summary>
    /// A named refusal from the server. Each one is terminal: the server is still running, and the
    /// pane says which check failed rather than offering the kill as if nothing had answered.
    /// </summary>
    private static ServerStopOutcome Refused(StopReplyReason reason)
    {
        var state = reason switch
        {
            StopReplyReason.NotAdmin => ServerStopState.NotAdmin,
            StopReplyReason.SaveTimeout => ServerStopState.SaveTimeout,
            _ => ServerStopState.Failed,
        };
        string text = reason switch
        {
            StopReplyReason.NotAdmin => ReasonNotAdmin,
            StopReplyReason.SaveTimeout => ReasonSaveTimeout,
            _ => ReasonSaveFailed,
        };
        Report(state);
        return new ServerStopOutcome(state, text);
    }

    private static void Report(ServerStopState state)
    {
        try { StopProgress?.Invoke(state); }
        catch { /* a readback recorder must never fail a stop */ }
    }

    /// <summary>Force stop: <c>Kill</c> on the process matched by full path inside the folder, then wait ≤ 10 s for exit.</summary>
    public static ServerStopOutcome ForceStop(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot))
            return new ServerStopOutcome(ServerStopState.Failed, ReasonNoFolder);
        string root = serverRoot!;
        if (IsLiveServer(root)) return new ServerStopOutcome(ServerStopState.Failed, ReasonLiveServer);
        var process = Running(root);
        if (process == null) return new ServerStopOutcome(ServerStopState.Failed, ReasonNotRunning);
        if (IsLiveServer(root, process: process)) return new ServerStopOutcome(ServerStopState.Failed, ReasonLiveServer);

        bool killed;
        try { killed = KillOverride != null ? KillOverride(process) : KillMatched(root, process); }
        catch { killed = false; }
        if (!killed) return new ServerStopOutcome(ServerStopState.Failed, ReasonStillRunning);
        if (!WaitForExit(root, ForceExitTimeoutSeconds))
            return new ServerStopOutcome(ServerStopState.Failed, ReasonStillRunning);

        var record = WriteLaunchRecord(root, process);
        return new ServerStopOutcome(ServerStopState.Stopped, StoppedLine, record);
    }

    public const string ReasonScanFailed = "The process list could not be read, so Progenitor can't tell whether that server is running.";
    public const string ReasonNoFreshRecord = "this stop did not read the server's command line, so its launch is unknown";
    public const string ReasonNotServerExe = "The launch record does not start that folder's own VRisingServer.exe.";
    public const string ReasonBadWorkingDirectory = "The launch record's working directory is outside that folder.";

    /// <summary>
    /// Start the folder's server from its launch record: exactly the recorded exe, arguments and working directory.
    /// Review M-c: one start at a time process-wide, and the process list is read now (never the cached answer), so a
    /// second press right after a start finds the new server.
    /// </summary>
    public static ServerStartOutcome Start(string? serverRoot)
    {
        if (string.IsNullOrWhiteSpace(serverRoot))
            return new ServerStartOutcome(false, 0, RefusedLine(ReasonNoFolder));
        string root = serverRoot!;
        if (IsLiveServer(root)) return new ServerStartOutcome(false, 0, RefusedLine(ReasonLiveServer));
        if (!TryClaimStart()) return new ServerStartOutcome(false, 0, RefusedLine(ReasonStartInFlight));
        try
        {
            if (!TryScanNow(root, out var running)) return new ServerStartOutcome(false, 0, RefusedLine(ReasonScanFailed));
            if (running != null) return new ServerStartOutcome(false, 0, RefusedLine(ReasonAlreadyRunning));
            var record = ServerLaunchRecord.TryRead(root);
            if (record == null) return new ServerStartOutcome(false, 0, StartUnavailableLine());
            return StartCore(root, record);
        }
        finally { ReleaseStart(); }
    }

    /// <summary>
    /// F30 and review M-a/M-b: start the server again after Progenitor's own stop, from the record that stop wrote
    /// (<paramref name="record"/>, built from <paramref name="stopped"/>, a process with a pid and a read command line).
    /// Waits for that process to exit (its handle, its full path re-matched), then needs two clean reads of the process
    /// list (a read error refuses) before it starts anything. Runs off the main thread; holds the start lock throughout,
    /// so Preferences' Start server cannot start a second server meanwhile.
    /// </summary>
    internal static ServerStartOutcome StartAgainAfterStop(string? serverRoot, ServerProcessInfo? stopped, ServerLaunchRecord? record, int exitWaitSeconds)
    {
        if (string.IsNullOrWhiteSpace(serverRoot))
            return new ServerStartOutcome(false, 0, RefusedLine(ReasonNoFolder));
        string root = serverRoot!;
        if (stopped == null || stopped.Pid <= 0 || string.IsNullOrWhiteSpace(stopped.CommandLine) || record == null)
            return new ServerStartOutcome(false, 0, RefusedLine(ReasonNoFreshRecord));
        if (IsLiveServer(root, record, stopped)) return new ServerStartOutcome(false, 0, RefusedLine(ReasonLiveServer));
        if (!TryClaimStart()) return new ServerStartOutcome(false, 0, RefusedLine(ReasonStartInFlight));
        try
        {
            if (!WaitForPidExit(stopped.Pid, stopped.Path, exitWaitSeconds))
                return new ServerStartOutcome(false, 0, RefusedLine(ReasonStillRunning));
            for (int clean = 0; clean < 2; clean++)
            {
                if (clean > 0)
                {
                    try { (SleepMs ?? DefaultSleep)(500); }
                    catch { /* */ }
                }

                if (!TryScanNow(root, out var running)) return new ServerStartOutcome(false, 0, RefusedLine(ReasonScanFailed));
                if (running != null) return new ServerStartOutcome(false, 0, RefusedLine(ReasonStillRunning));
            }

            return StartCore(root, record);
        }
        finally { ReleaseStart(); }
    }

    /// <summary>The checks every start passes, then the start itself; the new process is stored in the cache.</summary>
    private static ServerStartOutcome StartCore(string root, ServerLaunchRecord record)
    {
        // Review H1, M-d, M2: never the live dedicated server (folder, exe, working directory, argument); exactly the
        // folder's own VRisingServer.exe with a working directory inside it; and its own save folder.
        string refusal = StartRefusal(root, record);
        if (refusal.Length > 0) return new ServerStartOutcome(false, 0, RefusedLine(refusal));

        int pid;
        try { pid = StartOverride != null ? StartOverride(record.Exe, record.Args, record.WorkingDirectory) : StartProcess(record); }
        catch (Exception e) { return new ServerStartOutcome(false, 0, RefusedLine(e.Message)); }
        if (pid <= 0) return new ServerStartOutcome(false, 0, RefusedLine(ReasonStillRunning));
        // Review M-c: the next main-thread read sees the new server at once, not a stale "not running".
        try { RunningCache.Store(root, new ServerProcessInfo(pid, record.Exe, JoinCommandLine(record.Exe, record.Args), record.WorkingDirectory)); }
        catch { /* */ }
        return new ServerStartOutcome(true, pid, StartedLine(pid));
    }

    /// <summary>Review M-d: <paramref name="exe"/> is <c>&lt;root&gt;\VRisingServer.exe</c> after canonicalization.</summary>
    internal static bool IsFoldersServerExe(string root, string? exe)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(exe)) return false;
        try
        {
            string want = LocalServerFolder.Canonicalize(System.IO.Path.Combine(root, LocalServerFolder.ServerExecutableName));
            string got = LocalServerFolder.Canonicalize(exe!);
            if (want == LocalServerFolder.UnrecognisedSentinel || got == LocalServerFolder.UnrecognisedSentinel) return false;
            return string.Equals(want.TrimEnd('\\', '/'), got.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Test seam: wait for pid's exit (pid, full path, seconds) and say whether it exited. Null: the process handle.</summary>
    internal static Func<int, string, int, bool>? PidExitProbe { get; set; }

    /// <summary>
    /// Review M-b: wait on the stopped process itself. A pid that no longer exists, or that now belongs to another
    /// image (recycled), has exited; otherwise its handle is waited on.
    /// </summary>
    private static bool WaitForPidExit(int pid, string path, int seconds)
    {
        if (PidExitProbe != null) return PidExitProbe(pid, path, seconds);
        Process proc;
        try { proc = Process.GetProcessById(pid); }
        catch (ArgumentException) { return true; }
        catch { return false; }
        using (proc)
        {
            try
            {
                if (!string.Equals(SafePath(proc), path, StringComparison.OrdinalIgnoreCase)) return true;
                return proc.WaitForExit(Math.Max(0, seconds) * 1000);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Review M-b: one read of the process list now. False when it could not be read (an error, or a VRisingServer
    /// whose image path is unreadable), so "nothing seen" is never mistaken for "not running".
    /// </summary>
    /// <summary>Test seam: one read of the process list (root to (read ok, the process or null)). Null: the real scan.</summary>
    internal static Func<string, (bool Ok, ServerProcessInfo? Running)>? ScanProbe { get; set; }

    private static bool TryScanNow(string root, out ServerProcessInfo? running)
    {
        running = null;
        if (ScanProbe != null)
        {
            var (ok, found) = ScanProbe(root);
            running = ok ? found : null;
            return ok;
        }

        if (RunningProbe != null)
        {
            running = RunningProbe(root);
            return true;
        }

        if (!ServerInstall.TryFindRunningServerNow(root, out string? path)) return false;
        running = path == null ? null : MatchByPath(path) ?? new ServerProcessInfo(0, path);
        RunningCache.Store(root, running);
        return true;
    }

    /// <summary>The command line a record replays, for the cache and the readback.</summary>
    internal static string JoinCommandLine(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder(QuoteArgument(exe));
        for (int i = 0; i < args.Count; i++) sb.Append(' ').Append(QuoteArgument(args[i]));
        return sb.ToString();
    }

    /// <summary>Windows argument quoting (the inverse of <see cref="ServerLaunchRecord.SplitCommandLine"/>).</summary>
    internal static string QuoteArgument(string? arg)
    {
        string a = arg ?? "";
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }

            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }

        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Write the launch record for a process that has just exited. Best effort: a failed write is not a failed stop.
    /// Review M-a: a process whose command line could not be read never replaces a record that has arguments (that
    /// launch would lose its world and ports); the existing record stays and is returned.
    /// </summary>
    public static ServerLaunchRecord? WriteLaunchRecord(string serverRoot, ServerProcessInfo process)
    {
        var record = ServerLaunchRecord.FromProcess(process, Now());
        // Review M2: a bare record (no arguments: an unread command line, or a launch with none) is never written, over a
        // record or over nothing; replaying it would start the server on the default save folder.
        if (record.Args.Count == 0) return ServerLaunchRecord.TryRead(serverRoot);

        try { record.Write(serverRoot); }
        catch { return record; }
        return record;
    }

    private static bool WaitForExit(string serverRoot, int seconds)
    {
        DateTime deadline = Now().AddSeconds(seconds);
        while (true)
        {
            if (RunningNow(serverRoot) == null) return true;
            if (Now() >= deadline) return false;
            try { (SleepMs ?? DefaultSleep)(250); }
            catch { /* a refused sleep only tightens the loop */ }
        }
    }

    private static void DefaultSleep(int ms)
    {
#if !PROGENITOR_TESTS
        System.Threading.Thread.Sleep(ms);
#endif
    }

    private static bool IsInside(string serverRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string canonRoot = LocalServerFolder.Canonicalize(serverRoot);
            string canonPath = LocalServerFolder.Canonicalize(path);
            if (canonPath == LocalServerFolder.UnrecognisedSentinel) canonPath = path;
            return LocalServerFolder.IsUnderOrEqual(canonPath, canonRoot);
        }
        catch
        {
            return false;
        }
    }

    private static DateTime Now() => ClockUtc?.Invoke() ?? DateTime.UtcNow;

    private static ServerProcessInfo? MatchByPath(string fullPath)
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("VRisingServer"))
            {
                try
                {
                    if (string.Equals(SafePath(proc), fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        ServerInstall.TryReadProcessCommandLine(proc.Id, out string commandLine, out string workingDirectory);
                        return new ServerProcessInfo(proc.Id, fullPath, commandLine, workingDirectory);
                    }
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* */ }
                }
            }
        }
        catch { /* a failed scan is not proof of anything; the caller keeps the path-only info */ }

        return null;
    }

    // HOSTPROBE: QueryFullProcessImageName, not MainModule. MainModule walks the target's module
    // list (milliseconds per process) and this runs on the main thread from the join-flow
    // last-server recheck while a VRisingServer is up (for example a hosted private game).
    private static string SafePath(Process proc) => ServerInstall.ExecutablePath(proc);

    // --- PEB decoding ---------------------------------------------------------------------------
    //
    // Offsets are x64 only; the dedicated server is x64 and a 32-bit target is refused rather than
    // read with the wrong strides. They are constants here, and unit-tested against a byte fixture,
    // so a mis-decode shows up in the suite instead of as an empty launch record on a live stop.

    /// <summary>x64 <c>PEB.ProcessParameters</c>.</summary>
    internal const int PebProcessParametersOffset = 0x20;

    /// <summary>x64 <c>PROCESS_BASIC_INFORMATION.PebBaseAddress</c>.</summary>
    internal const int ProcessBasicInformationPebOffset = 0x08;

    /// <summary>x64 <c>RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath</c> (the CURDIR's first field).</summary>
    internal const int CurrentDirectoryDosPathOffset = 0x38;

    /// <summary>x64 <c>RTL_USER_PROCESS_PARAMETERS.CommandLine</c>.</summary>
    internal const int CommandLineOffset = 0x70;

    /// <summary>Bytes of an x64 <c>UNICODE_STRING</c>: two USHORTs, four bytes of padding, an 8-byte pointer.</summary>
    internal const int UnicodeStringBytes = 16;

    /// <summary>
    /// Decode one x64 <c>UNICODE_STRING</c> out of a copied buffer:
    /// <c>USHORT Length; USHORT MaximumLength; ULONG (padding); PWSTR Buffer;</c> — the padding is
    /// the reason <c>Buffer</c> is at +8 and not at +4, and getting that wrong reads half a pointer.
    /// <paramref name="byteLength"/> is <c>Length</c> in bytes, not characters. False when the
    /// buffer is too short, the length is odd (a UTF-16 string cannot be), or the pointer is null
    /// with a non-zero length.
    /// </summary>
    internal static bool TryDecodeUnicodeString(
        ReadOnlySpan<byte> buffer, int offset, out int byteLength, out ulong address)
    {
        byteLength = 0;
        address = 0;
        if (offset < 0 || buffer.Length < offset + UnicodeStringBytes) return false;
        int length = buffer[offset] | (buffer[offset + 1] << 8);
        int maximum = buffer[offset + 2] | (buffer[offset + 3] << 8);
        ulong pointer = 0;
        for (int i = 7; i >= 0; i--) pointer = (pointer << 8) | buffer[offset + 8 + i];
        if ((length & 1) != 0) return false;
        if (length > maximum) return false;
        if (length == 0) { address = pointer; return pointer != 0; }
        if (pointer == 0) return false;
        byteLength = length;
        address = pointer;
        return true;
    }

    /// <summary>
    /// Decode <paramref name="byteLength"/> bytes of little-endian UTF-16 out of
    /// <paramref name="bytes"/>, stopping at the first NUL so a buffer whose <c>Length</c> counts a
    /// terminator does not carry it into the argument vector.
    /// </summary>
    internal static string DecodeWideString(ReadOnlySpan<byte> bytes, int byteLength)
    {
        if (byteLength <= 0 || bytes.Length < byteLength) return "";
        var chars = new char[byteLength / 2];
        int used = 0;
        for (int i = 0; i + 1 < byteLength; i += 2)
        {
            char c = (char)(bytes[i] | (bytes[i + 1] << 8));
            if (c == '\0') break;
            chars[used++] = c;
        }

        return new string(chars, 0, used);
    }

    /// <summary>Read an x64 pointer out of a copied buffer.</summary>
    internal static bool TryReadPointer(ReadOnlySpan<byte> buffer, int offset, out ulong address)
    {
        address = 0;
        if (offset < 0 || buffer.Length < offset + 8) return false;
        for (int i = 7; i >= 0; i--) address = (address << 8) | buffer[offset + i];
        return address != 0;
    }

    private static bool KillMatched(string serverRoot, ServerProcessInfo process)
    {
        if (!IsInside(serverRoot, process.Path)) return false;
        if (process.Pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(process.Pid);
            // Re-match the full path at the moment of the kill: a pid can be recycled between the
            // probe and here, and a name match alone is never enough.
            if (!string.Equals(SafePath(proc), process.Path, StringComparison.OrdinalIgnoreCase)) return false;
            proc.Kill();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Review L-b (2026-10-08): the server gets its own console, minimized and not activated (SW_SHOWMINNOACTIVE, as
    /// <c>tools/test-server.ps1</c> starts it), so it neither shares the client's BepInEx console nor takes focus.
    /// </summary>
    internal const short ShowMinNoActive = 7;

    internal const uint CreateNewConsole = 0x00000010;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateBreakawayFromJob = 0x01000000;

    /// <summary>The dedicated server's Steam app id.</summary>
    internal const string ServerSteamAppId = "1604030";

    /// <summary>
    /// Review L2 (2026-10-08): the client's Steam variables name the game's app (and its overlay); the server gets its own
    /// <c>SteamAppId</c> and none of the client's game ids.
    /// </summary>
    internal static void CleanSteamEnvironment(IDictionary<string, string?> environment)
    {
        foreach (string name in new[] { "SteamGameId", "SteamOverlayGameId", "SteamAppId", "SteamClientLaunch", "SteamEnv" })
            environment.Remove(name);
        environment["SteamAppId"] = ServerSteamAppId;
    }
    private const int StartfUseShowWindow = 0x00000001;

    private static int StartProcess(ServerLaunchRecord record)
    {
        // Live 2026-09-12: a server started from the client inherits the client's DOORSTOP_*
        // variables and Doorstop then skips injection in the child (no BepInEx, no Progenitor) -
        // the same trap the hosted launcher strips around the private-game spawn
        // (HostedServerLauncher.DoorstopStripNest). Here the strip is per child environment only.
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[e.Key?.ToString() ?? ""] = e.Value?.ToString();
        env.Remove("");
        StripDoorstopEnvironment(env);
        CleanSteamEnvironment(env);
        string block = EnvironmentBlock(env);
        var commandLine = new StringBuilder(JoinCommandLine(record.Exe, record.Args));
        var si = new StartupInfoW
        {
            cb = System.Runtime.InteropServices.Marshal.SizeOf<StartupInfoW>(),
            dwFlags = StartfUseShowWindow,
            wShowWindow = ShowMinNoActive,
        };
        IntPtr envPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(block);
        try
        {
            string? cwd = string.IsNullOrWhiteSpace(record.WorkingDirectory) ? null : record.WorkingDirectory;
            // Review L2: out of the client's job object when the job allows it, so closing the game does not end the server;
            // a job that refuses breakaway (access denied) gets the plain start, and the log says so.
            ProcessInformation pi;
            bool ok = CreateProcessW(record.Exe, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CreateNewConsole | CreateUnicodeEnvironment | CreateBreakawayFromJob, envPtr, cwd, ref si, out pi);
            if (!ok && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 5)
            {
                try { Plugin.Logger?.LogInfo("Install: the client's job refuses breakaway; the server starts inside it."); }
                catch { /* */ }
                commandLine = new StringBuilder(JoinCommandLine(record.Exe, record.Args));
                ok = CreateProcessW(record.Exe, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateNewConsole | CreateUnicodeEnvironment, envPtr, cwd, ref si, out pi);
            }

            if (!ok) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(envPtr); }
    }

    /// <summary>A Unicode environment block: <c>name=value\0</c> per variable, sorted by name, ending in an extra <c>\0</c>.</summary>
    internal static string EnvironmentBlock(IDictionary<string, string?> env)
    {
        var names = new List<string>(env.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (string name in names)
        {
            if (name.Length == 0 || name.IndexOf('=') > 0) continue;
            sb.Append(name).Append('=').Append(env[name] ?? "").Append('\0');
        }

        sb.Append('\0');
        return sb.ToString();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct StartupInfoW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcessW(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfoW startupInfo, out ProcessInformation processInformation);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Remove every <c>DOORSTOP_*</c> variable from a child's environment so Doorstop injects afresh. Returns the removed names.</summary>
    internal static IReadOnlyList<string> StripDoorstopEnvironment(IDictionary<string, string?> environment)
    {
        var removed = new List<string>();
        foreach (var key in new List<string>(environment.Keys))
        {
            if (key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase)) { environment.Remove(key); removed.Add(key); }
        }
        return removed;
    }
}

/// <summary>
/// Server side of the graceful stop. The command saves the world through the caller-supplied
/// save path and quits only after that save reports success; a failed save replies with the
/// reason and the process keeps running.
/// </summary>
internal static class ServerStopCommand
{
    public const string DeniedReply = "Only an admin can stop the server.";
    public const string SaveFailedPrefix = "World save failed, so the server is still running: ";
    public const string SavingReply = "Saving the world before stopping…";
    public const string StoppingReply = "World saved. Stopping the server.";
    public const string NoSaveReply = "No world save path is available on this process.";

    /// <summary>
    /// SERVERINST4: the save was accepted but no save file appeared within the confirmation window,
    /// so nothing quit. The world the operator asked to keep is the reason this is not a quit.
    /// </summary>
    public const string SaveTimeoutReply =
        "The world save was triggered but no save file appeared within "
        + "15 seconds, so the server is still running.";

    /// <summary>Server seam: trigger the world save, returning null when it was accepted or the reason it was not.</summary>
    internal static Func<string?>? SaveWorld { get; set; }

    /// <summary>
    /// Server seam, SERVERINST4: sample the save folder <b>before</b> the trigger and return the
    /// predicate that says whether the save has since landed. Null means completion is not
    /// observable on this process, and the quit then follows acceptance as it did before.
    /// </summary>
    internal static Func<Func<bool>>? SaveWatch { get; set; }

    /// <summary>
    /// Server seam, SERVERINST4: run an action after a delay, on the main thread. Production is
    /// <c>MainThread.PostDelayed</c>. This is why the confirmation is <em>not</em> a blocking wait:
    /// the queued <c>SavePersistentDataEvent</c> is serialized by a system on a later frame, so a
    /// loop that held this frame would wait for a save it was itself preventing.
    /// </summary>
    internal static Action<double, Action>? Schedule { get; set; }

    /// <summary>Server seam: quit on the next frame. Production is <c>UnityEngine.Application.Quit()</c> scheduled on the frame ticker.</summary>
    internal static Action? QuitNextFrame { get; set; }

    internal static Func<DateTime>? ClockUtc { get; set; }

    internal static void ResetForTests()
    {
        SaveWorld = null;
        SaveWatch = null;
        Schedule = null;
        QuitNextFrame = null;
        ClockUtc = null;
    }

    /// <summary>
    /// Runs the command. Returns true when the quit was scheduled or the confirmation wait started.
    /// Non-admins are denied without touching the save path; a save that fails never reaches the
    /// quit.
    /// </summary>
    public static bool Execute(bool isAdmin, Action<string> reply) => Execute(isAdmin, reply, null);

    /// <summary>
    /// The same path with a structured failure channel, so the wire reply can name <em>which</em>
    /// check refused instead of shipping a sentence the client has to parse.
    /// <paramref name="failed"/> is called at most once, and only on a path that does not quit.
    /// </summary>
    public static bool Execute(bool isAdmin, Action<string> reply, Action<StopReplyReason>? failed)
    {
        if (reply == null) throw new ArgumentNullException(nameof(reply));
        if (!isAdmin)
        {
            reply(DeniedReply);
            failed?.Invoke(StopReplyReason.NotAdmin);
            return false;
        }

        var save = SaveWorld;
        if (save == null)
        {
            reply(NoSaveReply);
            failed?.Invoke(StopReplyReason.NoSavePath);
            return false;
        }

        reply(SavingReply);

        // Sampled before the trigger: "the newest save file is newer than it was" is only evidence
        // if "was" predates the save being asked for.
        Func<bool>? landed = null;
        try { landed = SaveWatch?.Invoke(); }
        catch { landed = null; }

        string? failure;
        try { failure = save(); }
        catch (Exception e) { failure = e.Message; }
        if (!string.IsNullOrEmpty(failure))
        {
            reply(SaveFailedPrefix + failure);
            failed?.Invoke(StopReplyReason.SaveFailed);
            return false;
        }

        var quit = QuitNextFrame;
        if (quit == null)
        {
            reply(SaveFailedPrefix + "no quit path on this process");
            failed?.Invoke(StopReplyReason.NoQuitPath);
            return false;
        }

        var schedule = Schedule;
        if (landed == null || schedule == null)
        {
            // Nothing here can observe the completion, so acceptance is all there is. Stated rather
            // than hidden: this is the one path that quits on an accepted save.
            reply(StoppingReply);
            quit();
            return true;
        }

        ConfirmThenQuit(landed, quit, reply, failed, schedule);
        return true;
    }

    /// <summary>
    /// Poll the save folder off-frame until the save lands, then quit. On
    /// <see cref="Persistence.WorldSaveTrigger.SaveConfirmTimeoutSeconds"/> of no advance the server
    /// keeps running and the client is told <see cref="StopReplyReason.SaveTimeout"/>: an unsaved
    /// quit is the one outcome this route exists to prevent.
    /// </summary>
    private static void ConfirmThenQuit(
        Func<bool> landed, Action quit, Action<string> reply, Action<StopReplyReason>? failed, Action<double, Action> schedule)
    {
        DateTime deadline = Now().AddSeconds(Persistence.WorldSaveTrigger.SaveConfirmTimeoutSeconds);
        void Poll()
        {
            bool ok;
            try { ok = landed(); }
            catch { ok = false; }
            if (ok)
            {
                reply(StoppingReply);
                quit();
                return;
            }

            if (Now() >= deadline)
            {
                reply(SaveTimeoutReply);
                failed?.Invoke(StopReplyReason.SaveTimeout);
                return;
            }

            schedule(Persistence.WorldSaveTrigger.ConfirmPollSeconds, Poll);
        }

        schedule(Persistence.WorldSaveTrigger.ConfirmPollSeconds, Poll);
    }

    private static DateTime Now() => ClockUtc?.Invoke() ?? DateTime.UtcNow;
}
