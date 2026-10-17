using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Progenitor.Native;

namespace Progenitor.Resilience;

/// <summary>
/// M4.1 update resilience. On the first run after a game update, Progenitor says what changed
/// and what will break — before any plugin loads and without a plugin having to fail first.
///
/// <see cref="Install"/> queues one background pass (never on the load thread, never blocking):
/// fingerprint GameAssembly.dll and the whole BepInEx interop set, compare with the fingerprint
/// stored beside the address tables, check whether an address table exists for the build now
/// running and which detour sites it still covers, and re-resolve every game member Core's IL
/// references. On a build id with no archive yet it also copies the whole interop folder to
/// <c>interop-{side}-{build}/</c> beside the address tables, before the new fingerprint is
/// stored (<see cref="InteropArchive"/>) — BepInEx regenerates that folder in place, so this is
/// the only moment the previous build's interop set can be preserved for
/// <c>tools/driftmap</c>. It emits exactly one log block and stores the new fingerprint. It
/// never refuses a load, never patches anything and never talks to the network.
///
/// Game-free by construction: BepInEx logging arrives through <see cref="LogSink"/>, which
/// <c>Plugin.Install</c> points at the <c>Progenitor</c> log source. That is what lets the same
/// code run in <c>Progenitor.Tests</c> and in <c>tools/resilience-report.ps1</c>.
/// </summary>
public static class Resilience
{
    private static readonly object Gate = new();
    private static Action<string>? _logSink;
    private static bool _installed;
    private static int _generation;
    private static Thread? _thread;
    private static CancellationTokenSource? _cancel;
    private static ResilienceResult? _last;

    /// <summary>
    /// How long <see cref="Uninstall"/> waits for the pass thread before giving up. This runs on
    /// the game main thread, so the budget is a frame-sized one: the pass checks the token before
    /// every 1 MiB hash chunk (<c>ResilienceCheck.Sha256Stream</c>), so a canceled pass is
    /// normally gone in well under this and the join covers the read already in flight, not a
    /// whole <c>GameAssembly.dll</c> (R32 finding 7).
    /// </summary>
    public const int UninstallJoinMilliseconds = 250;

    /// <summary>Where the report goes. Set by <c>Plugin.Install</c>; null writes nowhere.</summary>
    public static Action<string>? LogSink
    {
        get { lock (Gate) return _logSink; }
        set { lock (Gate) _logSink = value; }
    }

    /// <summary>Last completed report, for the debug bridge and for tests. Null before the pass finishes.</summary>
    public static ResilienceResult? Last
    {
        get { lock (Gate) return _last; }
    }

    /// <summary>
    /// Start the one background pass. A second call while installed is a no-op. Returns false
    /// when the pass was not started (already installed, or the thread could not be created).
    /// <paramref name="options"/> is a test seam: null (the production call) describes the
    /// running environment on the pass thread.
    ///
    /// <para>Job FUPD7: before the thread starts, and on the main thread, the two
    /// <c>[Resilience]</c> archive keys are bound so they and their descriptions exist in the
    /// <c>.cfg</c> an operator opens (<c>InteropArchiveConfig</c>). The pass still reads them by
    /// hand off disk and defaults the same way, so it does not matter whether the bind ran, won
    /// the race, or was never possible in this host.</para>
    /// </summary>
    public static bool Install(ResilienceOptions? options = null)
    {
        int generation;
        CancellationTokenSource cancel;

#if !PROGENITOR_TESTS
        InteropArchiveConfig.Bind();
#endif

        lock (Gate)
        {
            if (_installed) return false;
            _installed = true;
            generation = ++_generation;
            _cancel = cancel = new CancellationTokenSource();
        }

        try
        {
            var thread = new Thread(() => RunPass(generation, options, cancel.Token))
            {
                IsBackground = true,
                Name = "progenitor-resilience",
                Priority = ThreadPriority.BelowNormal,
            };
            lock (Gate) _thread = thread;
            thread.Start();
            return true;
        }
        catch (Exception e)
        {
            lock (Gate) { _installed = false; _thread = null; _cancel = null; }
            Emit(generation, "Resilience: check not started: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Stop reporting and stop the pass. Order matters: drop the sink and bump the generation,
    /// <b>cancel</b>, and only then join — briefly. The generation bump discards a stale block,
    /// the cancellation stops the work, and the short bounded <see cref="Thread.Join(int)"/>
    /// covers the common case where the pass is between two cancellation checks (R31 finding 12).
    ///
    /// This call is on the game main thread, so it must never be the long pole. The old 2 s budget
    /// was spent waiting on an uncancellable full-file <c>ComputeHash</c>, so the worst case was
    /// both bad outcomes at once: two seconds of stalled game loop <i>and</i> a timeout with the
    /// thread still running (R32 finding 7). The hash is chunked now, and a pass that has not
    /// exited within <see cref="UninstallJoinMilliseconds"/> is simply left to finish on its own
    /// while <see cref="Uninstall"/> returns. It is never waited on again.
    ///
    /// <para><b>The residual is safe.</b> A thread still running after the generation is gone can
    /// only be inside the pass, and everything the pass references is game-free: a
    /// <see cref="ResilienceOptions"/>, a <c>FileStream</c> plus a <c>byte[]</c> over files on
    /// disk, <c>IncrementalHash</c>, the <c>BuildFingerprint</c> / <c>AssemblyFingerprint</c> /
    /// <c>MemberBinding</c> / <c>AddressTable</c> value objects and <c>List&lt;string&gt;</c>
    /// notes. It holds no IL2CPP object, no <c>World</c>, no <c>Entity</c>, no Unity handle, and
    /// it can publish nothing: every emit re-checks the generation under <see cref="Gate"/> and
    /// the sink is already null, and a canceled pass writes no store file. A canceled archive
    /// copy is the same story: it is abandoned under its dot-prefixed temporary name, never
    /// renamed into place, and swept by the next pass. What it does hold is
    /// stack frames in collectible Core, so it delays the ALC collect until the chunk finishes;
    /// that shows up in the ordinary collect-wait pin diagnostics and clears itself, which is
    /// strictly better than blocking the game loop waiting for it.</para>
    /// </summary>
    public static void Uninstall()
    {
        Thread? thread;
        CancellationTokenSource? cancel;
        lock (Gate)
        {
            _installed = false;
            _generation++;
            _logSink = null;
            thread = _thread;
            _thread = null;
            cancel = _cancel;
            _cancel = null;
        }

        try { cancel?.Cancel(); }
        catch { /* a cancellation callback must not take Uninstall down */ }

        // Never dispose the source: a pass that outlived the join still reads the token.
        // The join is bounded and never retried: past the budget the thread finishes on its own,
        // silently, holding only the game-free objects listed above.
        if (thread == null) return;
        try { thread.Join(UninstallJoinMilliseconds); }
        catch { /* ThreadStateException / interrupt: nothing left to do */ }
    }

    private static void RunPass(int generation, ResilienceOptions? preset, CancellationToken cancellation)
    {
        try
        {
            IReadOnlyList<string> notes = Array.Empty<string>();
            var options = preset ?? DescribeEnvironment(out notes);
            options.Cancellation = cancellation;

            var result = ResilienceCheck.Run(options);
            if (cancellation.IsCancellationRequested) return;

            lock (Gate)
            {
                if (generation != _generation) return; // uninstalled or swapped under us
                _last = result;
            }

            Emit(generation, result.Report);
            foreach (var note in notes) Emit(generation, "Resilience: " + note);
            foreach (var note in result.Notes) Emit(generation, "Resilience: " + note);
        }
        catch (OperationCanceledException)
        {
            // Uninstall asked; say nothing, the sink may already belong to the next generation.
        }
        catch (Exception e)
        {
            Emit(generation, "Resilience: check failed: " + e.Message);
        }
    }

    /// <summary>
    /// Folder Bootstrap loads Core from, under the BepInEx root. Mirrors
    /// <c>Progenitor.Bootstrap.CoreLayout.FolderName</c> — Core cannot reference Bootstrap, and
    /// this file is compiled into <c>Progenitor.Tests</c>, which may reference neither BepInEx nor
    /// the game.
    /// </summary>
    public const string CoreFolderName = "progenitor-core";

    /// <summary>Core's legacy file name (<c>CoreLayout.FileName</c>); the single-folder image is <c>Progenitor.core</c>.</summary>
    public const string CoreFileName = "Progenitor.dll";

    /// <summary>BepInEx's folder name inside the game folder.</summary>
    public const string BepInExFolderName = "BepInEx";

    /// <summary>
    /// Work out the paths from the running process: the game folder from the main module, the
    /// interop folder BepInEx generated inside it, and the address-table folder next to
    /// <c>Progenitor.dll</c> (<c>BepInEx/progenitor-core/</c> or its <c>addresslib/</c> subfolder).
    /// Exposed so the debug bridge and the offline tool can print the same paths.
    ///
    /// <para><b>Job FUPD5.</b> This used to derive everything from
    /// <c>typeof(Resilience).Assembly.Location</c>, which is <c>""</c> in the game: Bootstrap loads
    /// Core with <c>AssemblyLoadContext.LoadFromStream</c> (<c>CoreLoader.LoadAndInstallBytes</c>),
    /// and an assembly loaded from bytes has no location. <c>Path.GetDirectoryName("")</c> is
    /// <c>""</c>, so the store folder, the address-table folder and the Core assembly path were all
    /// empty and the pass silently did nothing but print: no fingerprint stored (so every Core
    /// generation reported "first run, no stored fingerprint"), no member scan, and since FUPD4 no
    /// interop archive either. The folder is resolved from the process now, and the report says
    /// which folder it landed on and how it got there.</para>
    /// </summary>
    public static ResilienceOptions DescribeEnvironment(out IReadOnlyList<string> notes)
    {
        var list = new List<string>();
        string side = AddressLibrary.DetectSide();
        string loadedFrom = "";
        string coreVersion = "";
        string coreMvid = "";

        try
        {
            loadedFrom = typeof(Resilience).Assembly.Location ?? "";
            coreVersion = typeof(Resilience).Assembly.GetName().Version?.ToString() ?? "";
            // Both halves of the core row's identity, off the one assembly (job FUPD11, R58
            // finding 9). The MVID used to be read off Progenitor.dll on disk while the version
            // was read here, so the pair could describe two different Cores — and it came back
            // empty whenever the core folder could not be resolved, which is the one run that
            // most needs to say which Core is running. ManifestModule needs no path and no file.
            coreMvid = typeof(Resilience).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        }
        catch (Exception e)
        {
            list.Add("could not locate Progenitor.dll: " + e.Message);
        }

        string gameRoot = FindGameRoot(DirectoryOf(loadedFrom), list);
        string coreDirectory = ResolveCoreFolder(loadedFrom, gameRoot, SafeDirectoryExists, out var origin);
        if (coreDirectory.Length == 0)
            list.Add(UnresolvedCoreFolderWarning);

        string coreAssembly = ResolveCoreAssemblyPath(loadedFrom, coreDirectory, SafeFileExists);
        if (coreAssembly.Length == 0 && coreDirectory.Length > 0)
            list.Add("member scan skipped: no " + CoreFileName + " in " + coreDirectory);

        string storeDirectory = coreDirectory;
        try
        {
            var subfolder = Path.Combine(coreDirectory, ResilienceCheck.TableFolderName);
            if (coreDirectory.Length > 0 && Directory.Exists(subfolder))
            {
                storeDirectory = subfolder;
                origin += "; " + ResilienceCheck.TableFolderName + " subfolder";
            }
        }
        catch (Exception e)
        {
            list.Add("could not probe the addresslib subfolder: " + e.Message);
        }

        // Job FUPD4: how many archived interop sets to keep. Read out of the same .cfg the rest
        // of Progenitor uses, by hand — this runs on a background thread in a game-free file, so
        // it cannot hold a BepInEx ConfigFile binding, and an unreadable file is the default.
        string bepInExRoot = Path.Combine(gameRoot, BepInExFolderName);
        int retain = InteropArchive.DefaultRetain;
        try
        {
            retain = InteropArchive.ReadRetainFromConfig(bepInExRoot);
        }
        catch (Exception e)
        {
            list.Add("could not read [" + InteropArchive.ConfigSection + "] "
                     + InteropArchive.ConfigKey + ": " + e.Message);
        }

        // Job FUPD6 (R53 finding 9): whether to archive at all. FUPD4 shipped this on for
        // everyone, which wrote ~540 MB per game root into a stranger's Steam install for a
        // developer tool they do not have. Off unless [Resilience] ArchiveInterop says otherwise
        // or this is one of our own trees, detected the way Reload.WatchCore already is.
        var archivePolicy = new InteropArchiveDecision(
            InteropArchivePolicy.DefaultArchiveInterop, InteropArchivePolicy.ReasonDefaultOff);
        try
        {
            archivePolicy = InteropArchivePolicy.Decide(bepInExRoot);
        }
        catch (Exception e)
        {
            list.Add("could not read [" + InteropArchivePolicy.ConfigSection + "] "
                     + InteropArchivePolicy.ConfigKey + ": " + e.Message);
        }
        list.Add(InteropArchivePolicy.FormatNote(archivePolicy));

        notes = list;
        return new ResilienceOptions
        {
            Side = side,
            GameAssemblyPath = Path.Combine(gameRoot, ResilienceCheck.GameAssemblyFileName),
            InteropDirectory = Path.Combine(gameRoot, BepInExFolderName, "interop"),
            AddressTableDirectory = coreDirectory,
            StoreDirectory = storeDirectory,
            StoreDirectoryOrigin = origin,
            // The archive goes where the fingerprint goes, which is where the address tables
            // are: BepInEx/progenitor-core/addresslib when that folder exists, else the Core
            // folder itself. One place to look for everything per-build.
            ArchiveInterop = archivePolicy.Archive,
            ArchiveDisabledReason = archivePolicy.Archive ? "" : archivePolicy.Reason,
            ArchiveDirectory = storeDirectory,
            ArchiveRetain = retain,
            CoreAssemblyPath = coreAssembly,
            CoreVersion = coreVersion,
            CoreMvid = coreMvid,
            UseLengthAndTimeFastPath = true,
            ScanMembers = true,
            WriteStore = true,
            // In the game, the table can also be an embedded resource inside Progenitor.dll,
            // which a directory scan cannot see. AddressLibrary owns that search order.
            TableLookup = static (s, b) =>
                AddressLibrary.TryFindTable(s, b, out var table, out _) ? table : null,
        };
    }

    /// <summary>
    /// The one line the log must carry when nothing could be resolved. It replaces the old silent
    /// failure mode, where an unresolvable folder was indistinguishable from a healthy first run
    /// forever.
    /// </summary>
    public const string UnresolvedCoreFolderWarning =
        "WARNING: the " + CoreFolderName + " folder could not be resolved (this Core was loaded "
        + "from memory, so it has no file location, and the game folder was not found either) - "
        + "the fingerprint is not stored, members are not scanned and interop is not archived";

    /// <summary>
    /// Where <c>Progenitor.dll</c> lives on disk, and how that was decided (the <c>store</c> row
    /// of the report). Two candidates, in order:
    ///
    /// <list type="number">
    /// <item>the folder <paramref name="assemblyLocation"/> names — the offline runner, the tests,
    /// and any host that loads Core from a file;</item>
    /// <item><c>&lt;gameRoot&gt;/BepInEx/progenitor-core</c> — the game, where Bootstrap's
    /// <c>LoadFromStream</c> leaves <c>Assembly.Location</c> empty. The layout is fixed by
    /// <c>CoreLayout</c>, which is Bootstrap's and cannot be referenced from here; nothing in
    /// <c>ICoreRuntime</c> / <c>ICoreHost</c> hands Core the folder it was loaded from, so this
    /// mirrors the constant instead of asking for the value. If Core is ever given that path,
    /// prefer it over this fallback.</item>
    /// </list>
    ///
    /// An existing candidate wins. If neither exists the first non-empty one is still returned —
    /// the folder is created by the store write — and <paramref name="origin"/> says so, because
    /// "I am about to create this" and "I found this" are different claims.
    /// </summary>
    internal static string ResolveCoreFolder(
        string assemblyLocation,
        string gameRoot,
        Func<string, bool> directoryExists,
        out string origin)
    {
        var candidates = new List<(string Path, string How)>();
        string fromAssembly = DirectoryOf(assemblyLocation);
        if (fromAssembly.Length > 0)
            candidates.Add((fromAssembly, "the folder " + CoreFileName + " was loaded from"));
        if (!string.IsNullOrEmpty(gameRoot))
        {
            string derived;
            try { derived = Progenitor.SelfUpdate.InstallLayout.CoreFolder(Path.Combine(gameRoot, BepInExFolderName)); }
            catch { derived = ""; }
            if (derived.Length > 0)
                candidates.Add((derived, "game folder + Core's folder (" + derived + ")"));
        }

        foreach (var candidate in candidates)
        {
            bool exists;
            try { exists = directoryExists(candidate.Path); }
            catch { exists = false; }
            if (!exists) continue;
            origin = candidate.How;
            return candidate.Path;
        }

        if (candidates.Count > 0)
        {
            origin = candidates[0].How + "; folder does not exist yet";
            return candidates[0].Path;
        }

        origin = "unresolved";
        return "";
    }

    /// <summary>
    /// The <c>Progenitor.dll</c> the member scan reads. A real
    /// <paramref name="assemblyLocation"/> is used as-is; otherwise it is the file in
    /// <paramref name="coreFolder"/>, which is the image this process loaded unless a deploy has
    /// staged a newer one since (a staged build is <c>.next</c> and is not this file). "" when
    /// there is nothing to scan.
    /// </summary>
    internal static string ResolveCoreAssemblyPath(
        string assemblyLocation,
        string coreFolder,
        Func<string, bool> fileExists)
    {
        if (!string.IsNullOrEmpty(assemblyLocation)) return assemblyLocation;
        if (string.IsNullOrEmpty(coreFolder)) return "";
        foreach (string name in new[] { Progenitor.SelfUpdate.InstallLayout.CoreFileName, CoreFileName })
        {
            string path;
            try { path = Path.Combine(coreFolder, name); }
            catch { return ""; }
            try { if (fileExists(path)) return path; }
            catch { return ""; }
        }
        return "";
    }

    private static string DirectoryOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetDirectoryName(path) ?? ""; }
        catch { return ""; }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return !string.IsNullOrEmpty(path) && Directory.Exists(path); }
        catch { return false; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
        catch { return false; }
    }

    private static string FindGameRoot(string coreDirectory, List<string> notes)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var main = process.MainModule?.FileName;
            var directory = string.IsNullOrEmpty(main) ? null : Path.GetDirectoryName(main);
            if (!string.IsNullOrEmpty(directory)
                && File.Exists(Path.Combine(directory!, ResilienceCheck.GameAssemblyFileName)))
                return directory!;
        }
        catch (Exception e)
        {
            notes.Add("could not read the main module: " + e.Message);
        }

        // Fallback: <game>\BepInEx\progenitor-core\Progenitor.dll, or a reload generation
        // folder under it. Walk up until GameAssembly.dll is the neighbor.
        var current = coreDirectory;
        for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(current); depth++)
        {
            try
            {
                if (File.Exists(Path.Combine(current, ResilienceCheck.GameAssemblyFileName))) return current;
                current = Path.GetDirectoryName(current) ?? "";
            }
            catch
            {
                break;
            }
        }
        notes.Add("game folder not found from the process or from " + coreDirectory);
        return coreDirectory;
    }

    /// <summary>
    /// Write one line, but only while <paramref name="generation"/> is still the installed one.
    /// The generation check and the call happen under <see cref="Gate"/>, which
    /// <see cref="Uninstall"/> takes before it releases the lock and joins — so no line can land
    /// in the next generation's log after Uninstall returns. <c>Monitor</c> is reentrant, so a
    /// sink that reads <see cref="LogSink"/> is safe; the join is deliberately outside the lock.
    /// </summary>
    private static void Emit(int generation, string text)
    {
        lock (Gate)
        {
            if (generation != _generation) return;
            var sink = _logSink;
            if (sink == null || string.IsNullOrEmpty(text)) return;
            try { sink(text); }
            catch { /* a logging failure must not take the pass down */ }
        }
    }
}
