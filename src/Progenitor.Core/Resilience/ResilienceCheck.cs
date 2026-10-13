using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Progenitor.Native;

namespace Progenitor.Resilience;

/// <summary>Where to look and what to do. Every path is explicit so the check runs offline.</summary>
public sealed class ResilienceOptions
{
    /// <summary><c>client</c> or <c>server</c>. Decides the address-table file name.</summary>
    public string Side { get; set; } = "client";

    /// <summary>Full path to the game's <c>GameAssembly.dll</c>.</summary>
    public string GameAssemblyPath { get; set; } = "";

    /// <summary>BepInEx's generated interop folder (<c>&lt;game&gt;\BepInEx\interop</c>).</summary>
    public string InteropDirectory { get; set; } = "";

    /// <summary>Folder holding <c>{side}-{build}.json</c> address tables.</summary>
    public string AddressTableDirectory { get; set; } = "";

    /// <summary>Folder for <c>build-{side}-{build}.json</c>. Defaults to <see cref="AddressTableDirectory"/>.</summary>
    public string StoreDirectory { get; set; } = "";

    /// <summary>
    /// How <see cref="StoreDirectory"/> was decided, for the report's <c>store</c> row (job
    /// FUPD5). "" prints the folder alone, which is what the offline runner does — it was told
    /// the folder on the command line. In the game the resolution is a guess made from the
    /// process, and a guess has to be readable to be checkable.
    /// </summary>
    public string StoreDirectoryOrigin { get; set; } = "";

    /// <summary><c>Progenitor.dll</c> whose game references are checked. "" skips the member scan.</summary>
    public string CoreAssemblyPath { get; set; } = "";

    /// <summary>Version string recorded with the fingerprint so a Core rebuild is visible in the diff.</summary>
    public string CoreVersion { get; set; } = "";

    /// <summary>
    /// <c>Progenitor.dll</c>'s module version id, lowercase hex with no braces — the other half of
    /// the <c>core</c> row's identity, and it must come from <em>the same assembly</em>
    /// <see cref="CoreVersion"/> came from.
    /// </summary>
    /// <remarks>
    /// <para><b>Job FUPD11 (R58 finding 9).</b> FUPD10 read this off
    /// <see cref="CoreAssemblyPath"/> while the version was read off the loaded assembly, so
    /// <c>0.1.0.0/a1b2c3d4</c> could name two different Cores whenever the file on disk was not
    /// the image this generation was loaded from — which is the normal in-game state, since
    /// Bootstrap loads Core with <c>LoadFromStream</c> and the path is only ever a folder
    /// fallback. Worse, the MVID came back "" on exactly the run it was added for: an unresolved
    /// core folder means no <see cref="CoreAssemblyPath"/>, and the row degraded back to naming a
    /// version alone. The identity of the running Core needs no path, no file handle and no
    /// <c>try</c>: <c>typeof(Resilience).Assembly.ManifestModule.ModuleVersionId</c>. Both halves
    /// are now filled from that one place, beside each other, in
    /// <c>Resilience.DescribeEnvironment</c>.</para>
    /// </remarks>
    public string CoreMvid { get; set; } = "";

    /// <summary>
    /// Reuse a stored hash when an interop file's length and last-write time are unchanged.
    /// Saves re-reading ~180 MB of interop plus GameAssembly.dll on every start. Off in the
    /// offline tool, where correctness beats speed.
    /// </summary>
    public bool UseLengthAndTimeFastPath { get; set; } = true;

    /// <summary>Run <see cref="MetadataScan"/>. Off makes the check a pure file fingerprint.</summary>
    public bool ScanMembers { get; set; } = true;

    /// <summary>Write <c>build-{side}-{build}.json</c> after the comparison.</summary>
    public bool WriteStore { get; set; } = true;

    /// <summary>
    /// Copy <see cref="InteropDirectory"/> to <c>interop-{side}-{build}/</c> in
    /// <see cref="ArchiveDirectory"/> when there is no archive for the build now running — the
    /// only way <c>tools/driftmap</c>'s mandatory <c>--old</c> input can exist after BepInEx has
    /// regenerated the interop folder in place (job FUPD4). Off by default so a fabricated
    /// options object in a test or a caller that only wants the fingerprint never copies 200 MB;
    /// <c>Resilience.DescribeEnvironment</c> and the offline runner turn it on.
    /// </summary>
    public bool ArchiveInterop { get; set; }

    /// <summary>
    /// Why <see cref="ArchiveInterop"/> is false, in the words the report prints (job FUPD6).
    /// "" prints the bare <c>off</c> the block always had. <see cref="InteropArchivePolicy"/>
    /// produces this; a caller that simply never wanted an archive leaves it empty.
    /// </summary>
    public string ArchiveDisabledReason { get; set; } = "";

    /// <summary>Where the archives go. "" uses <see cref="StoreDirectory"/>, beside the address tables.</summary>
    public string ArchiveDirectory { get; set; } = "";

    /// <summary>Archives kept per side. Bound from <c>[Resilience] InteropArchives</c>; clamped 1..10.</summary>
    public int ArchiveRetain { get; set; } = InteropArchive.DefaultRetain;

    /// <summary>Free bytes that must remain after an archive copy, or it is refused.</summary>
    public long ArchiveMinimumFreeBytes { get; set; } = InteropArchive.DefaultMinimumFreeBytes;

    /// <summary>Test seam: free bytes on the volume holding a path. Null uses <see cref="DriveInfo"/>.</summary>
    public Func<string, long>? ArchiveFreeSpaceProbe { get; set; }

    /// <summary>Test seam: copy one file. Null uses <see cref="File.Copy(string,string,bool)"/>.</summary>
    public Action<string, string>? ArchiveCopyFile { get; set; }

    /// <summary>
    /// Override how the address table for a build is found. Null uses
    /// <see cref="AddressTableDirectory"/>. Core's runtime wiring passes a lookup that also sees
    /// the tables embedded in <c>Progenitor.dll</c>.
    /// </summary>
    public Func<string, string, AddressTable?>? TableLookup { get; set; }

    /// <summary>Detour sites to check for a table entry. Defaults to <c>AddressId.DetourSites</c>.</summary>
    public IReadOnlyList<DetourSiteInfo>? DetourSites { get; set; }

    /// <summary>
    /// How long a stored <c>GameAssembly.dll</c> hash may be reused before it is recomputed no
    /// matter what the length and mtime say. The fast path is a cache, not a verdict: this is
    /// the schedule that guarantees the full hash — and therefore
    /// <see cref="ResilienceDiff.GameAssemblyChangedUnderSameBuildId"/> — actually runs in the
    /// game (R31 finding 5). Zero or negative means "always re-hash".
    /// </summary>
    public TimeSpan FullHashInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Cancels a running pass. <c>Resilience.Uninstall</c> sets it and joins the thread so a
    /// half-finished pass cannot keep stack frames in collectible Core alive. Cancellation
    /// aborts the pass with an <see cref="OperationCanceledException"/> and, in particular,
    /// skips the store write.
    /// </summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>
    /// Test seam: computes a file's lowercase-hex SHA-256. Null uses the real reader. Only the
    /// tests set it; nothing in the game or the offline tool does.
    /// </summary>
    public Func<string, string>? HashFile { get; set; }
}

/// <summary>Everything one run produced: the fingerprint, the baseline, the diff, the log block.</summary>
public sealed class ResilienceResult
{
    public ResilienceResult(
        BuildFingerprint current,
        BuildFingerprint? baseline,
        ResilienceDiff diff,
        string report,
        string storedPath,
        IReadOnlyList<string> notes,
        InteropArchiveResult? archive = null)
    {
        Current = current;
        Baseline = baseline;
        Diff = diff;
        Report = report ?? "";
        StoredPath = storedPath ?? "";
        Notes = notes ?? Array.Empty<string>();
        Archive = archive;
    }

    public BuildFingerprint Current { get; }
    public BuildFingerprint? Baseline { get; }
    public ResilienceDiff Diff { get; }

    /// <summary>The formatted log block (see <see cref="ResilienceReport"/>).</summary>
    public string Report { get; }

    /// <summary>Where the fingerprint was written, or "" when it was not.</summary>
    public string StoredPath { get; }

    /// <summary>Non-fatal problems: unreadable folder, store not writable, and so on.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>
    /// What the interop archive step did this pass (job FUPD4), or null when it was not asked
    /// for. <see cref="InteropArchiveResult.HasArchive"/> is the answer to "can the next game
    /// update be diffed at all".
    /// </summary>
    public InteropArchiveResult? Archive { get; }
}

/// <summary>
/// Capture the current game build's fingerprint, compare it with the stored one and format the
/// report. The design: a per-build
/// table named in the failure text, and a load-time reference scan (which member a mod uses that
/// the current game no longer has).
///
/// This type touches the file system but never the game: it is compiled into
/// <c>Progenitor.Tests</c> and into the runner <c>tools/resilience-report.ps1</c> generates, and
/// it runs against a V Rising folder on a machine that never launched the game.
/// </summary>
public static class ResilienceCheck
{
    /// <summary>Subfolder Core keeps address tables in, next to <c>Progenitor.dll</c>.</summary>
    public const string TableFolderName = "addresslib";

    public const string GameAssemblyFileName = "GameAssembly.dll";

    /// <summary>The <c>store</c> row when there is no folder to write to (job FUPD5).</summary>
    public const string StoreUnresolved =
        "NOT RESOLVED - nothing is stored, so every run reports \"first run\"";

    /// <summary>The note emitted alongside <see cref="StoreUnresolved"/>.</summary>
    public const string StoreUnresolvedNote =
        "WARNING: no store folder - the fingerprint is not written, the member scan and the "
        + "interop archive are skipped, and the next run will report \"first run\" again";

    /// <summary>
    /// The report's <c>store</c> row: the folder the fingerprint goes to, and — when the caller
    /// resolved it rather than being told it — how that folder was chosen.
    /// </summary>
    internal static string FormatStoreLine(string storeDirectory, string? origin)
    {
        string how = string.IsNullOrEmpty(origin) ? "" : "  (" + origin + ")";
        return string.IsNullOrEmpty(storeDirectory) ? StoreUnresolved + how : storeDirectory + how;
    }

    /// <summary>Run the whole check. Never throws; failures become <see cref="ResilienceResult.Notes"/>.</summary>
    public static ResilienceResult Run(ResilienceOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        var notes = new List<string>();

        string storeDir = string.IsNullOrEmpty(options.StoreDirectory)
            ? options.AddressTableDirectory
            : options.StoreDirectory;

        // The build id first (a 64 KiB read), so LoadBaseline can prefer *this* build's stored
        // fingerprint instead of whichever side-matching file happens to be newest. Without it an
        // A / B / A branch switch reports "build CHANGED" on every run and never reaches the
        // same-build comparison (R31 finding 19).
        var baseline = LoadBaseline(storeDir, options.Side, out var baselineFile, notes, PeekBuild(options));
        var current = Capture(options, baseline, notes);

        // Job FUPD4: archive the interop set BEFORE the fingerprint is stored, and record in the
        // fingerprint which archive it describes. The order is the point. The fingerprint is what
        // makes the next run say "same build, nothing to do", so a fingerprint written before a
        // failed archive would mark this build as handled and the copy would never be retried —
        // and the interop folder BepInEx regenerates in place would be gone for good.
        var archive = ArchiveInterop(options, storeDir, current, notes);
        if (archive.HasArchive) current = current.WithInteropArchive(archive.FolderName);

        // Job FUPD5: an empty store folder is the failure that reads as a healthy first run. Say
        // it once, loudly, on the pass that could not resolve one — not by omission.
        if (storeDir.Length == 0)
            notes.Add(StoreUnresolvedNote);

        var diff = ResilienceDiff.Compare(baseline, current, baselineFile);
        var report = ResilienceReport.Format(
            diff, current, archive, FormatStoreLine(storeDir, options.StoreDirectoryOrigin));

        string storedPath = "";
        // A canceled pass must not leave its half-built fingerprint behind as the next
        // run's baseline (R31 finding 12).
        if (options.WriteStore && !options.Cancellation.IsCancellationRequested)
            storedPath = Store(storeDir, current, notes);

        return new ResilienceResult(current, baseline, diff, report, storedPath, notes, archive);
    }

    /// <summary>
    /// Keep a copy of this build's interop set beside the address tables, so that the next game
    /// update has an <c>--old</c> folder to diff against. Never throws and never blocks: it runs
    /// on the same background pass, and every failure is a status the report prints.
    /// </summary>
    private static InteropArchiveResult ArchiveInterop(
        ResilienceOptions options,
        string storeDir,
        BuildFingerprint current,
        List<string> notes)
    {
        if (!options.ArchiveInterop)
            return InteropArchiveResult.None(InteropArchiveStatus.Disabled, options.ArchiveDisabledReason);
        if (options.Cancellation.IsCancellationRequested)
            return InteropArchiveResult.None(InteropArchiveStatus.Failed, "the pass was canceled");

        InteropArchiveResult result;
        try
        {
            result = InteropArchive.Ensure(new InteropArchiveRequest
            {
                Side = current.Side,
                Build = current.Build,
                InteropDirectory = options.InteropDirectory,
                ArchiveRoot = string.IsNullOrEmpty(options.ArchiveDirectory) ? storeDir : options.ArchiveDirectory,
                Retain = options.ArchiveRetain,
                MinimumFreeBytes = options.ArchiveMinimumFreeBytes,
                Cancellation = options.Cancellation,
                FreeSpaceProbe = options.ArchiveFreeSpaceProbe,
                CopyFile = options.ArchiveCopyFile,
            });
        }
        catch (Exception e)
        {
            return InteropArchiveResult.None(InteropArchiveStatus.Failed, e.Message);
        }

        foreach (var note in result.Notes) notes.Add(note);
        return result;
    }

    /// <summary>Fingerprint the game as it is on disk right now.</summary>
    public static BuildFingerprint Capture(ResilienceOptions options, BuildFingerprint? reuseFrom, List<string> notes)
    {
        options.Cancellation.ThrowIfCancellationRequested();
        string side = NormalizeSide(options.Side);
        string build = "";
        long gameLength = 0;
        string gameSha = "";
        string gameModified = "";
        string gameHashed = "";

        try
        {
            if (File.Exists(options.GameAssemblyPath))
            {
                var info = new FileInfo(options.GameAssemblyPath);
                gameLength = info.Length;
                gameModified = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
                build = AddressId.ComputeBuildIdFromFile(options.GameAssemblyPath);
                // The build id already covers the length, so length + id would be implied by the
                // id alone: the mtime and the schedule are what make the reuse a real cache hit
                // rather than an unconditional "assume unchanged" (R31 finding 5).
                var reuse = reuseFrom != null
                            && options.UseLengthAndTimeFastPath
                            && reuseFrom.GameAssemblyLength == gameLength
                            && string.Equals(reuseFrom.Build, build, StringComparison.Ordinal)
                            && reuseFrom.GameAssemblySha256.Length > 0
                            && gameModified.Length > 0
                            && string.Equals(reuseFrom.GameAssemblyModifiedUtc, gameModified, StringComparison.Ordinal)
                            && !FullHashDue(reuseFrom, options.FullHashInterval, DateTime.UtcNow);
                if (reuse)
                {
                    gameSha = reuseFrom!.GameAssemblySha256;
                    gameHashed = reuseFrom.GameAssemblyHashedUtc;
                }
                else
                {
                    gameSha = Sha256(options, options.GameAssemblyPath);
                    gameHashed = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                }
            }
            else
            {
                notes.Add(GameAssemblyFileName + " not found at " + options.GameAssemblyPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            notes.Add("could not fingerprint " + options.GameAssemblyPath + ": " + e.Message);
        }

        var interop = FingerprintInterop(options, reuseFrom, notes);
        options.Cancellation.ThrowIfCancellationRequested();
        var (tablePresent, inTable, missing) = CheckDetourSites(options, side, build, notes);
        var members = ScanMembers(options, notes);

        return new BuildFingerprint(
            side,
            build,
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            options.CoreVersion,
            options.GameAssemblyPath,
            gameLength,
            gameSha,
            options.InteropDirectory,
            interop,
            tablePresent,
            inTable,
            missing,
            members,
            gameModified,
            gameHashed,
            "",
            BuildFingerprint.CurrentVersion,
            // Which Core, not just which version (job FUPD10, R57 finding 7) — and since FUPD11
            // (R58 finding 9) both halves arrive on the options together, filled from the one
            // loaded assembly. This method must never reach for a second source: a version off
            // the running Core and an MVID off a file are an identity that can name two Cores.
            options.CoreMvid ?? "");
    }

    /// <summary>
    /// True when the stored full hash is too old to be trusted as a cache entry: no stored stamp
    /// (a pre-schedule fingerprint), an unparseable stamp, a non-positive interval, or a stamp
    /// older than <paramref name="interval"/>. This is what stops the fast path from making
    /// <see cref="ResilienceDiff.GameAssemblyChangedUnderSameBuildId"/> unreachable.
    /// </summary>
    internal static bool FullHashDue(BuildFingerprint? previous, TimeSpan interval, DateTime utcNow)
    {
        if (previous == null) return true;
        if (interval <= TimeSpan.Zero) return true;
        if (previous.GameAssemblyHashedUtc.Length == 0) return true;
        if (!DateTime.TryParse(
                previous.GameAssemblyHashedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var hashed))
            return true;
        return utcNow - hashed.ToUniversalTime() >= interval;
    }

    /// <summary>
    /// The build id alone, read before the baseline is chosen. Returns "" for anything that is
    /// not readable; <see cref="Capture"/> records the reason.
    /// </summary>
    private static string PeekBuild(ResilienceOptions options)
    {
        try
        {
            return File.Exists(options.GameAssemblyPath)
                ? AddressId.ComputeBuildIdFromFile(options.GameAssemblyPath)
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static IReadOnlyList<AssemblyFingerprint> FingerprintInterop(
        ResilienceOptions options,
        BuildFingerprint? reuseFrom,
        List<string> notes)
    {
        var list = new List<AssemblyFingerprint>();
        string[] files;
        try
        {
            if (string.IsNullOrEmpty(options.InteropDirectory) || !Directory.Exists(options.InteropDirectory))
            {
                notes.Add("interop folder not found: " + options.InteropDirectory);
                return list;
            }
            files = Directory.GetFiles(options.InteropDirectory, "*.dll");
        }
        catch (Exception e)
        {
            notes.Add("could not list " + options.InteropDirectory + ": " + e.Message);
            return list;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                options.Cancellation.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                string modified = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
                var previous = reuseFrom?.FindInterop(info.Name);
                bool reuse = options.UseLengthAndTimeFastPath
                             && previous != null
                             && previous.Length == info.Length
                             && previous.Sha256.Length > 0
                             && string.Equals(previous.ModifiedUtc, modified, StringComparison.Ordinal);

                list.Add(reuse
                    ? new AssemblyFingerprint(info.Name, previous!.Version, info.Length, previous.Sha256, modified)
                    : new AssemblyFingerprint(info.Name, MetadataScan.ReadAssemblyVersion(file), info.Length, Sha256(options, file), modified));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                notes.Add("could not fingerprint " + file + ": " + e.Message);
            }
        }
        return list;
    }

    private static (bool present, IReadOnlyList<string> inTable, IReadOnlyList<string> missing) CheckDetourSites(
        ResilienceOptions options,
        string side,
        string build,
        List<string> notes)
    {
        var sites = options.DetourSites ?? AddressId.DetourSites;
        var inTable = new List<string>();
        var missing = new List<string>();

        AddressTable? table = null;
        try
        {
            table = options.TableLookup != null
                ? options.TableLookup(side, build)
                : LookupTableFile(options.AddressTableDirectory, side, build);
        }
        catch (Exception e)
        {
            notes.Add("address table lookup failed: " + e.Message);
        }

        foreach (var site in sites)
        {
            if (table != null && table.TryGetImageOffset(site.Id, out _)) inTable.Add(site.Site);
            else missing.Add(site.Site);
        }
        inTable.Sort(StringComparer.Ordinal);
        missing.Sort(StringComparer.Ordinal);
        return (table != null, inTable, missing);
    }

    /// <summary>
    /// <c>{side}-{build}.json</c> in <paramref name="directory"/>, parsed and checked against the
    /// running side and build. A file whose name matches but whose fields do not is refused, the
    /// same rule <c>AddressLibrary.TableMatchesProcess</c> applies in the game.
    /// </summary>
    public static AddressTable? LookupTableFile(string directory, string side, string build)
    {
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(build)) return null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(directory, TableFolderName, side + "-" + build + ".json"),
                     Path.Combine(directory, side + "-" + build + ".json"),
                 })
        {
            if (!AddressTable.TryLoadFile(candidate, out var table, out _)) continue;
            if (table == null) continue;
            if (!AddressLibrary.TableMatchesProcess(table, side, build)) continue;
            return table;
        }
        return null;
    }

    private static IReadOnlyList<MemberBinding> ScanMembers(ResilienceOptions options, List<string> notes)
    {
        if (!options.ScanMembers || string.IsNullOrEmpty(options.CoreAssemblyPath))
            return Array.Empty<MemberBinding>();
        if (!File.Exists(options.CoreAssemblyPath))
        {
            notes.Add("member scan skipped: " + options.CoreAssemblyPath + " not found");
            return Array.Empty<MemberBinding>();
        }
        options.Cancellation.ThrowIfCancellationRequested();
        var references = MetadataScan.ScanReferences(options.CoreAssemblyPath, out var error);
        if (error != null) notes.Add(error);
        if (references.Count == 0) return references;
        return MetadataScan.Resolve(references, options.InteropDirectory, options.Cancellation);
    }

    /// <summary>
    /// The fingerprint to compare against: <c>build-{side}-{build}.json</c> for
    /// <paramref name="preferBuild"/> — the build now running — when that file exists and parses
    /// (so an interop regeneration under an unchanged GameAssembly.dll is still a diff, and an
    /// A / B / A branch switch compares A against A), otherwise the newest stored fingerprint for
    /// the same side. <paramref name="preferBuild"/> is "" when the build id could not be read.
    /// </summary>
    public static BuildFingerprint? LoadBaseline(
        string storeDirectory,
        string side,
        out string baselineFile,
        List<string> notes,
        string preferBuild = "")
    {
        baselineFile = "";
        if (string.IsNullOrEmpty(storeDirectory)) return null;

        if (!string.IsNullOrEmpty(preferBuild))
        {
            try
            {
                var name = BuildFingerprint.FileNameFor(NormalizeSide(side), preferBuild);
                if (BuildFingerprint.TryLoadFile(Path.Combine(storeDirectory, name), out var preferred, out _)
                    && preferred != null
                    && string.Equals(preferred.Side, NormalizeSide(side), StringComparison.OrdinalIgnoreCase))
                {
                    baselineFile = name;
                    return preferred;
                }
            }
            catch (Exception e)
            {
                notes.Add("could not read this build's stored fingerprint: " + e.Message);
            }
        }

        string[] files;
        try
        {
            if (!Directory.Exists(storeDirectory)) return null;
            files = Directory.GetFiles(storeDirectory,
                BuildFingerprint.FileNamePrefix + "*" + BuildFingerprint.FileNameSuffix);
        }
        catch (Exception e)
        {
            notes.Add("could not list " + storeDirectory + ": " + e.Message);
            return null;
        }

        BuildFingerprint? newest = null;
        string newestFile = "";
        foreach (var file in files)
        {
            if (!BuildFingerprint.TryLoadFile(file, out var parsed, out var error) || parsed == null)
            {
                if (error != null) notes.Add(error);
                continue;
            }
            if (!string.Equals(parsed.Side, NormalizeSide(side), StringComparison.OrdinalIgnoreCase)) continue;
            if (newest == null || string.CompareOrdinal(parsed.CapturedUtc, newest.CapturedUtc) > 0)
            {
                newest = parsed;
                newestFile = file;
            }
        }

        baselineFile = newest == null ? "" : Path.GetFileName(newestFile);
        return newest;
    }

    private static string Store(string storeDirectory, BuildFingerprint fingerprint, List<string> notes)
    {
        if (string.IsNullOrEmpty(storeDirectory)) return "";
        try
        {
            Directory.CreateDirectory(storeDirectory);
            var path = Path.Combine(storeDirectory, fingerprint.FileName);
            // Same-folder temp plus a move: the pass runs on a background thread, so a process
            // exit mid-write would otherwise leave a truncated file that reads as "no baseline"
            // (R31 finding 12's second sibling).
            var temp = path + ".tmp";
            File.WriteAllText(temp, fingerprint.ToJson());
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception e)
        {
            notes.Add("could not store the fingerprint in " + storeDirectory + ": " + e.Message);
            return "";
        }
    }

    /// <summary>
    /// How much is hashed between two cancellation checks. One 1 MiB read off a cold disk is the
    /// unit of latency <c>Resilience.Uninstall</c>'s join has to absorb.
    /// </summary>
    internal const int HashChunkBytes = 1 << 20;

    private static string Sha256(ResilienceOptions options, string path)
    {
        var hook = options.HashFile;
        if (hook != null) return hook(path) ?? "";
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HashChunkBytes);
        return Sha256Stream(stream, options.Cancellation);
    }

    /// <summary>
    /// Lowercase-hex SHA-256 of <paramref name="stream"/>, read in <see cref="HashChunkBytes"/>
    /// chunks with <paramref name="cancellation"/> checked before every read, so a canceled pass
    /// leaves the hasher within one chunk. <c>SHA256.ComputeHash(stream)</c> was a single
    /// uncancellable call, and over a ~1 GB <c>GameAssembly.dll</c> on a cold file cache that
    /// call is seconds long - all of which <c>Resilience.Uninstall</c>'s join sat through on the
    /// game main thread, often only to time out with the pass thread still holding Core frames
    /// and the collectible ALC pinned (R32 finding 7).
    /// </summary>
    internal static string Sha256Stream(Stream stream, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashChunkBytes];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string NormalizeSide(string? side) =>
        string.Equals(side, "server", StringComparison.OrdinalIgnoreCase) ? "server" : "client";
}
