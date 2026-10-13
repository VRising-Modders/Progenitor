using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Progenitor.Resilience;

/// <summary>What one <see cref="InteropArchive.Ensure"/> call did.</summary>
public enum InteropArchiveStatus
{
    /// <summary>Archiving was not asked for (<see cref="ResilienceOptions.ArchiveInterop"/> is false).</summary>
    Disabled,

    /// <summary>Nothing to archive: no interop folder, no build id, or no archive root.</summary>
    NotPossible,

    /// <summary>An archive for this build was already there. The whole point of the build-id key.</summary>
    AlreadyPresent,

    /// <summary>A new build id: the interop set was copied to its per-build archive.</summary>
    Created,

    /// <summary>The disk could not hold the copy. Nothing was written, and the report says so.</summary>
    RefusedDiskSpace,

    /// <summary>The copy failed part-way. The half-copy was deleted; the final name never existed.</summary>
    Failed,

    /// <summary>
    /// An archive for this build is on disk but does not prove it is whole: no manifest, an
    /// unreadable one, or one that disagrees with the files beside it (job FUPD6, R53 finding 5).
    /// It is left alone and it is never offered to <c>driftmap</c> as an <c>--old</c> input.
    /// </summary>
    Incomplete,
}

/// <summary>One archived interop set on disk.</summary>
public sealed class InteropArchiveEntry
{
    public InteropArchiveEntry(
        string folderName,
        string path,
        string side,
        string build,
        DateTime modifiedUtc,
        bool complete = true,
        string incompleteReason = "")
    {
        FolderName = folderName ?? "";
        Path = path ?? "";
        Side = side ?? "";
        Build = build ?? "";
        ModifiedUtc = modifiedUtc;
        Complete = complete;
        IncompleteReason = incompleteReason ?? "";
    }

    /// <summary><c>interop-client-247473480-…</c>.</summary>
    public string FolderName { get; }

    public string Path { get; }
    public string Side { get; }

    /// <summary>The build id this set belongs to — the archive's whole reason to exist.</summary>
    public string Build { get; }

    public DateTime ModifiedUtc { get; }

    /// <summary>
    /// True when the archive carries a manifest that agrees with the files beside it. Only a
    /// complete archive is ever offered as <c>driftmap --old</c> (job FUPD6).
    /// </summary>
    public bool Complete { get; }

    /// <summary>Why it is not complete, or "".</summary>
    public string IncompleteReason { get; }
}

/// <summary>The outcome of one archive attempt, formatted into the resilience block.</summary>
public sealed class InteropArchiveResult
{
    public InteropArchiveResult(
        InteropArchiveStatus status,
        string folderName,
        string path,
        int fileCount,
        long bytes,
        IReadOnlyList<string> removed,
        string reason,
        IReadOnlyList<string> notes,
        int retained,
        int temporaryFolders = 0,
        long temporaryBytes = 0)
    {
        Status = status;
        FolderName = folderName ?? "";
        Path = path ?? "";
        FileCount = fileCount;
        Bytes = bytes;
        Removed = removed ?? Array.Empty<string>();
        Reason = reason ?? "";
        Notes = notes ?? Array.Empty<string>();
        Retained = retained;
        TemporaryFolders = temporaryFolders < 0 ? 0 : temporaryFolders;
        TemporaryBytes = temporaryBytes < 0 ? 0 : temporaryBytes;
    }

    public InteropArchiveStatus Status { get; }

    /// <summary><c>interop-{side}-{build}</c>, or "" when there is no archive for this build.</summary>
    public string FolderName { get; }

    /// <summary>Full path of the archive for this build, or "".</summary>
    public string Path { get; }

    public int FileCount { get; }
    public long Bytes { get; }

    /// <summary>Older archives retention deleted on this pass, oldest first.</summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>Why it was refused or failed; "" otherwise.</summary>
    public string Reason { get; }

    /// <summary>Non-fatal problems worth a log line.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>How many archives for this side survived retention.</summary>
    public int Retained { get; }

    /// <summary>
    /// Temp copies this pass's sweep left on disk (job FUPD7, R54 finding 6). Retention counts
    /// <c>interop-*</c> folders and can never see a <c>.interop-*</c> one, so a stranded copy used
    /// to sit outside the advertised "~540 MB per game root" ceiling with nothing ever reclaiming
    /// it or naming it. It is disk the operator is paying for, so it is counted here and printed
    /// on the archive row.
    /// </summary>
    public int TemporaryFolders { get; }

    /// <summary>Bytes held by <see cref="TemporaryFolders"/>.</summary>
    public long TemporaryBytes { get; }

    /// <summary>True when this build's interop set is on disk and diffable.</summary>
    public bool HasArchive => Status is InteropArchiveStatus.Created or InteropArchiveStatus.AlreadyPresent;

    /// <summary>
    /// The same outcome with the sweep's leftovers attached. One exit point stamps this, so every
    /// status carries the tally rather than only the ones whose construction site remembered to.
    /// </summary>
    public InteropArchiveResult WithTemporaries(int folders, long bytes) =>
        new(Status, FolderName, Path, FileCount, Bytes, Removed, Reason, Notes, Retained, folders, bytes);

    public static InteropArchiveResult None(InteropArchiveStatus status, string reason = "") =>
        new(status, "", "", 0, 0, Array.Empty<string>(), reason, Array.Empty<string>(), 0);
}

/// <summary>What to archive and where. Every path is explicit so the archive runs offline.</summary>
public sealed class InteropArchiveRequest
{
    public string Side { get; set; } = "client";

    /// <summary>The address-library build id. "" means "no build id": nothing is archived.</summary>
    public string Build { get; set; } = "";

    /// <summary>The live <c>BepInEx/interop</c> folder BepInEx regenerates in place.</summary>
    public string InteropDirectory { get; set; } = "";

    /// <summary>Where the per-build archives go — the folder the address tables live in.</summary>
    public string ArchiveRoot { get; set; } = "";

    /// <summary>How many archives to keep per side. See <see cref="InteropArchive.DefaultRetain"/>.</summary>
    public int Retain { get; set; } = InteropArchive.DefaultRetain;

    /// <summary>Free bytes that must remain after the copy, or it is refused.</summary>
    public long MinimumFreeBytes { get; set; } = InteropArchive.DefaultMinimumFreeBytes;

    public CancellationToken Cancellation { get; set; }

    /// <summary>Test seam: free bytes on the volume holding a path. Null uses <see cref="DriveInfo"/>.</summary>
    public Func<string, long>? FreeSpaceProbe { get; set; }

    /// <summary>Test seam: copy one file (source, destination). Null uses <see cref="File.Copy(string,string)"/>.</summary>
    public Action<string, string>? CopyFile { get; set; }

    /// <summary>
    /// The id this process stamps into its temp folder name, so another process's sweep can tell
    /// a live copy from a dead one (job FUPD6). Defaults to the real process id.
    /// </summary>
    public int ProcessId { get; set; } = InteropArchive.CurrentProcessId;

    /// <summary>
    /// Test seam: "is a process with this id running?". Null asks the OS. A probe that throws is
    /// read as <b>alive</b> — the sweep fails closed, because deleting a live process's in-flight
    /// copy is the defect this exists to stop.
    /// </summary>
    public Func<int, bool>? ProcessIsAlive { get; set; }

    /// <summary>
    /// Test seam: "when did the process with this id start?", UTC. Null means "ask the OS", and a
    /// null <i>answer</i> means the OS would not say — which the sweep reads as "cannot rule the
    /// owner out" (job FUPD7, R54 finding 6).
    ///
    /// <para>It travels with <see cref="ProcessIsAlive"/>: when that probe is overridden and this
    /// one is not, the start time reads as unknown rather than being looked up for real. A
    /// fabricated pid must never reach the live process table, or a test would be asserting
    /// against whatever the machine happens to be running.</para>
    /// </summary>
    public Func<int, DateTime?>? ProcessStartUtc { get; set; }

    /// <summary>Test seam: the clock the temp-age guard reads. Null uses <see cref="DateTime.UtcNow"/>.</summary>
    public Func<DateTime>? UtcNow { get; set; }

    /// <summary>
    /// How old an abandoned temp copy must be before the sweep deletes it even though its
    /// process still looks alive. The last-resort net, not the main rule: since job FUPD7 a
    /// recycled pid is caught by the owner's start time and never reaches this guard, so what is
    /// left for it is a folder with no owner stamp and a process whose start time the OS refuses
    /// to give. See <see cref="InteropArchive.DefaultMaximumTemporaryAge"/> for the number.
    /// </summary>
    public TimeSpan MaximumTemporaryAge { get; set; } = InteropArchive.DefaultMaximumTemporaryAge;

    /// <summary>
    /// Test seam: per-file SHA-256 for the manifest. Null uses a cancellable streaming read.
    /// </summary>
    public Func<string, string>? HashFile { get; set; }
}

/// <summary>
/// Job FUPD4. <b>The archive that makes the compatibility rewriter's input exist.</b>
///
/// <para><c>tools/driftmap</c> needs <c>--old &lt;the previous build's BepInEx/interop&gt;</c> and
/// <c>--new &lt;the current one&gt;</c>, both mandatory. BepInEx regenerates
/// <c>BepInEx/interop</c> <b>in place</b> when the game updates, and until this file existed
/// nothing kept a copy — so on the day the rewriter was needed its input had already been
/// destroyed, and no amount of later work could bring it back. A fingerprint is not a substitute:
/// <see cref="BuildFingerprint"/> records name, version, length and SHA-256, which says
/// <i>that</i> an assembly changed and can never say <i>what moved inside it</i>
/// (research <c>roadmap-critique.md</c> §12).</para>
///
/// <para>So the resilience pass, on the same background thread and behind the same new-build-id
/// detection that writes <c>build-{side}-{build}.json</c>, copies the whole interop folder to
/// <c>&lt;address tables&gt;/interop-{side}-{build}/</c> <b>before</b> the fingerprint is stored.
/// The copy lands under a dot-prefixed temporary name and is renamed into place, so a crash, a
/// full disk or a canceled pass can leave a discarded temp folder but never a half-populated
/// archive under the real name — a truncated archive read as complete is worse than no archive,
/// because the drift map it produces would be silently wrong. Retention keeps the newest
/// <see cref="DefaultRetain"/> archives per side and deletes the rest, and a copy that would not
/// fit is refused with the numbers in the log rather than filling the operator's disk.</para>
///
/// <para>Game-free by construction, like the rest of <c>Resilience/</c>: it is compiled into
/// <c>Progenitor.Tests</c> and into the runner <c>tools/resilience-report.ps1</c> generates.</para>
/// </summary>
public static class InteropArchive
{
    /// <summary><c>interop-</c>. Archive folder names start with this.</summary>
    public const string FolderPrefix = "interop-";

    /// <summary>
    /// Temp folders start with a dot so the retention glob (<see cref="FolderPrefix"/>) can never
    /// match one and count a half-copy as an archive.
    /// </summary>
    public const string TempPrefix = ".interop-";

    /// <summary>
    /// What separates the build id from the ownership stamp in a temp folder name:
    /// <c>.interop-{side}-{build}.tmp-p{pid}-{yyyyMMddHHmmss}-{guid8}</c>. A build id never
    /// contains this, so the stamp is always the tail after the <b>last</b> occurrence
    /// (job FUPD6).
    /// </summary>
    public const string TempStampMarker = ".tmp-p";

    /// <summary>
    /// How long an abandoned temp copy survives when its owner cannot be ruled out.
    ///
    /// <para><b>Thirty minutes</b> (job FUPD7, R54 finding 6; it was six hours). The measured copy
    /// is 172 files and 179.8 MB and takes seconds (research evidence 193), so six hours was not a
    /// guard on this operation, it was a guard on the calendar: a stranded 180 MB folder outlived
    /// a whole afternoon of the developer loop. Thirty minutes is still two orders of magnitude
    /// over the measured copy — enough for a cold spinning disk, an antivirus scanning every file,
    /// or a network archive root — while costing half an hour of disk rather than most of a day.
    /// </para>
    ///
    /// <para>Tightening it is safe in the direction that matters. If a genuinely live copy ever
    /// did run past the cap, the sweep deleting its folder does not publish anything: the copy
    /// loop re-checks <c>Directory.Exists</c> before every file and aborts with
    /// <see cref="InteropArchiveStatus.Failed"/> rather than rebuilding the tree around the hole.
    /// The cost is one retried archive on the next start; the cost of the old number was disk that
    /// nothing ever reclaimed.</para>
    /// </summary>
    public static readonly TimeSpan DefaultMaximumTemporaryAge = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far <i>after</i> a temp folder's stamp the owning process may have started before it is
    /// read as a different process wearing a recycled pid (job FUPD7).
    ///
    /// <para>The owner necessarily started before it wrote its own folder name, so in exact terms
    /// the rule is <c>start &lt;= stamp</c>. The stamp is <c>yyyyMMddHHmmss</c>, truncated, so a
    /// process that started inside the same second reads as up to a second late; five seconds
    /// covers that plus a clock the OS and the file name do not agree on to the tick. Anything
    /// beyond it is a pid the OS handed to somebody else after this folder was written.</para>
    /// </summary>
    public static readonly TimeSpan ProcessStartGrace = TimeSpan.FromSeconds(5);

    /// <summary>This process's id, read once. A failure reads as 0, which nothing branches on.</summary>
    public static readonly int CurrentProcessId = ReadCurrentProcessId();

    /// <summary>Archives kept per side. Three: the build now running plus two to diff back through.</summary>
    public const int DefaultRetain = 3;

    /// <summary>Hard bounds on the config-bound retention, so a typo cannot mean "keep zero" or "keep the disk".</summary>
    public const int MinRetain = 1;
    public const int MaxRetain = 10;

    /// <summary>Free bytes that must remain after an archive copy (512 MiB).</summary>
    public const long DefaultMinimumFreeBytes = 512L * 1024 * 1024;

    /// <summary>The <c>[Resilience]</c> section of <c>BepInEx/config/progenitor.cfg</c>.</summary>
    public const string ConfigSection = "Resilience";

    /// <summary>The retention key. An operator who wants a longer history raises this.</summary>
    public const string ConfigKey = "InteropArchives";

    /// <summary>
    /// Same file <c>CompatRewriteHost.ConfigFileName</c> names. Declared again rather than
    /// referenced: <c>Resilience/</c> compiles without Bootstrap and without BepInEx.
    /// </summary>
    public const string ConfigFileName = "progenitor.cfg";

    public const string ConfigDescription =
        "How many archived BepInEx/interop sets to keep per side (" + FolderPrefix
        + "{side}-{build}/, beside the address tables). Each is a full copy of the interop "
        + "folder (~180 MB), so the default of 3 is ~540 MB per game root - and a hosted private "
        + "game has a second root of its own. It is the only possible --old input for "
        + "tools/driftmap after a game update. 1..10. Archiving itself is off unless [Resilience] "
        + ConfigKeyArchive + " is true or this is a developer tree.";

    /// <summary>The on/off key, owned by <see cref="InteropArchivePolicy"/>. Named here for the description.</summary>
    public const string ConfigKeyArchive = "ArchiveInterop";

    /// <summary><c>interop-client-247473480-…</c>.</summary>
    public static string FolderNameFor(string side, string build) =>
        FolderPrefix + (side ?? "") + "-" + (build ?? "");

    /// <summary>
    /// Make sure an archive of the current interop folder exists for
    /// <see cref="InteropArchiveRequest.Build"/>. Never throws: everything that can go wrong is
    /// a status and a reason, because a missing archive must never be able to stop a game start.
    /// </summary>
    public static InteropArchiveResult Ensure(InteropArchiveRequest request)
    {
        // Job FUPD7: one exit point, so whatever the sweep could not reclaim is attached to every
        // status. A tally that only the Created path carried would be missing from exactly the
        // runs where the disk is already in trouble.
        var leftovers = new TemporaryTally();
        var result = EnsureCore(request, leftovers);
        return leftovers.Folders == 0 ? result : result.WithTemporaries(leftovers.Folders, leftovers.Bytes);
    }

    /// <summary>What the sweep left behind, filled in place so one exit point can report it.</summary>
    private sealed class TemporaryTally
    {
        public int Folders;
        public long Bytes;
    }

    private static InteropArchiveResult EnsureCore(InteropArchiveRequest request, TemporaryTally leftovers)
    {
        if (request == null) return InteropArchiveResult.None(InteropArchiveStatus.NotPossible, "no request");

        string side = ResilienceCheck.NormalizeSide(request.Side);
        string build = request.Build ?? "";
        string root = request.ArchiveRoot ?? "";
        var notes = new List<string>();

        if (build.Length == 0)
            return InteropArchiveResult.None(InteropArchiveStatus.NotPossible, "the build id could not be read");
        if (root.Length == 0)
            return InteropArchiveResult.None(InteropArchiveStatus.NotPossible, "no archive folder");
        if (string.IsNullOrEmpty(request.InteropDirectory) || !SafeDirectoryExists(request.InteropDirectory))
            return InteropArchiveResult.None(InteropArchiveStatus.NotPossible,
                "interop folder not found: " + request.InteropDirectory);

        string folderName = FolderNameFor(side, build);
        string finalPath;
        try
        {
            Directory.CreateDirectory(root);
            finalPath = Path.Combine(root, folderName);
        }
        catch (Exception e)
        {
            return InteropArchiveResult.None(InteropArchiveStatus.Failed,
                "could not create " + root + ": " + e.Message);
        }

        // Leftovers from a *dead* process, or ones old enough that nothing can still be writing
        // them. Swept before anything else so a crashed pass costs disk exactly once, and so the
        // free-space check below sees the space back. Job FUPD6: never another live process's
        // in-flight copy — the two-instance workflows share this root.
        SweepTemporaries(root, side, request, notes, leftovers);

        if (SafeDirectoryExists(finalPath))
        {
            // Job FUPD6 (R53 finding 5). "The folder is there" is not "the archive is whole".
            // Until this check existed every later pass returned AlreadyPresent without once
            // counting the files, so a truncated set published by a lost race would be handed to
            // driftmap months later as the previous build - with the real one long gone.
            bool whole = VerifyArchive(finalPath, out string why);
            if (!whole)
            {
                whole = TryHealLegacyArchive(request, side, build, finalPath, why, notes, out why);
            }

            var kept = Retain(root, side, request.Retain, folderName, notes, out var removedNow);
            return new InteropArchiveResult(
                whole ? InteropArchiveStatus.AlreadyPresent : InteropArchiveStatus.Incomplete,
                folderName, finalPath,
                CountFiles(finalPath), 0, removedNow, whole ? "" : why, notes, kept);
        }

        List<string> files;
        long bytes;
        try
        {
            files = new List<string>(Directory.GetFiles(request.InteropDirectory, "*", SearchOption.AllDirectories));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            bytes = 0;
            foreach (var file in files)
            {
                try { bytes += new FileInfo(file).Length; }
                catch { /* a file that vanished mid-listing is not worth failing the archive over */ }
            }
        }
        catch (Exception e)
        {
            return InteropArchiveResult.None(InteropArchiveStatus.Failed,
                "could not list " + request.InteropDirectory + ": " + e.Message);
        }

        if (files.Count == 0)
            return InteropArchiveResult.None(InteropArchiveStatus.NotPossible,
                "interop folder is empty: " + request.InteropDirectory);

        long free = FreeSpace(request, root, notes);
        long need = bytes + Math.Max(0, request.MinimumFreeBytes);
        if (free >= 0 && free < need)
        {
            return new InteropArchiveResult(
                InteropArchiveStatus.RefusedDiskSpace, "", "", files.Count, bytes,
                Array.Empty<string>(),
                Size(bytes) + " to copy plus " + Size(request.MinimumFreeBytes) + " headroom, "
                + Size(free) + " free on " + root,
                notes, CountArchives(root, side));
        }

        DateTime startedUtc = Now(request);
        string tempPath = Path.Combine(root, TemporaryFolderName(side, build, request.ProcessId, startedUtc));
        try
        {
            Directory.CreateDirectory(tempPath);
            var copy = request.CopyFile;
            foreach (var file in files)
            {
                request.Cancellation.ThrowIfCancellationRequested();
                // Job FUPD6: if the folder we are filling has gone, another process swept it and
                // recreating the tree file by file would publish a set with a hole in it. Stop.
                if (!SafeDirectoryExists(tempPath))
                    throw new IOException("the temporary archive folder was removed while it was being written ("
                                          + tempPath + ")");
                string relative = Relative(request.InteropDirectory, file);
                string destination = Path.Combine(tempPath, relative);
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (copy != null) copy(file, destination);
                else File.Copy(file, destination, overwrite: true);
            }
        }
        catch (Exception e)
        {
            // The rename never happened, so the final name does not exist. Drop the half-copy.
            Delete(tempPath, notes);
            bool cancelled = e is OperationCanceledException;
            return new InteropArchiveResult(
                InteropArchiveStatus.Failed, "", "", files.Count, bytes, Array.Empty<string>(),
                cancelled ? "the pass was canceled part-way through the copy" : "copy failed: " + e.Message,
                notes, CountArchives(root, side));
        }

        // Job FUPD6: count the copy against the source before publishing it, and leave a manifest
        // inside so every later pass - and driftmap, and a human - can ask the same question
        // without the source still being there to compare with. This is the step that turns a
        // lost race from silent into loud.
        try
        {
            int copiedCount = CountFiles(tempPath);
            long copiedBytes = TotalBytes(tempPath);
            if (copiedCount != files.Count || copiedBytes != bytes)
            {
                Delete(tempPath, notes);
                return new InteropArchiveResult(
                    InteropArchiveStatus.Failed, "", "", files.Count, bytes, Array.Empty<string>(),
                    "the copy does not match the source (" + copiedCount + " files / " + Size(copiedBytes)
                    + " copied, " + files.Count + " files / " + Size(bytes) + " in "
                    + request.InteropDirectory + ") - it was discarded rather than published",
                    notes, CountArchives(root, side));
            }

            var manifest = InteropArchiveManifest.Describe(
                tempPath, side, build, request.ProcessId, startedUtc, request.HashFile, request.Cancellation);
            File.WriteAllText(Path.Combine(tempPath, InteropArchiveManifest.FileName), manifest.Format());
        }
        catch (Exception e)
        {
            Delete(tempPath, notes);
            bool cancelled = e is OperationCanceledException;
            return new InteropArchiveResult(
                InteropArchiveStatus.Failed, "", "", files.Count, bytes, Array.Empty<string>(),
                cancelled
                    ? "the pass was canceled before the archive could be verified"
                    : "could not verify and describe the copy: " + e.Message,
                notes, CountArchives(root, side));
        }

        try
        {
            // One rename publishes the whole set. Another process that won the race gets to keep
            // its archive: same build id, same bytes, so the loser just drops its copy.
            if (SafeDirectoryExists(finalPath))
            {
                bool wholeRace = VerifyArchive(finalPath, out string whyRace);
                Delete(tempPath, notes);
                var keptRace = Retain(root, side, request.Retain, folderName, notes, out var removedRace);
                return new InteropArchiveResult(
                    wholeRace ? InteropArchiveStatus.AlreadyPresent : InteropArchiveStatus.Incomplete,
                    folderName, finalPath,
                    CountFiles(finalPath), 0, removedRace, wholeRace ? "" : whyRace, notes, keptRace);
            }
            Directory.Move(tempPath, finalPath);
        }
        catch (Exception e)
        {
            Delete(tempPath, notes);
            return new InteropArchiveResult(
                InteropArchiveStatus.Failed, "", "", files.Count, bytes, Array.Empty<string>(),
                "could not rename the archive into place: " + e.Message, notes, CountArchives(root, side));
        }

        int retained = Retain(root, side, request.Retain, folderName, notes, out var removed);
        return new InteropArchiveResult(
            InteropArchiveStatus.Created, folderName, finalPath, files.Count, bytes,
            removed, "", notes, retained);
    }

    /// <summary>
    /// Archives on disk for one side, newest first. Ordered by last-write time and then by name
    /// descending, so two archives written in the same clock tick are still a stable order.
    /// </summary>
    public static IReadOnlyList<InteropArchiveEntry> List(string root, string side)
    {
        var list = new List<InteropArchiveEntry>();
        if (string.IsNullOrEmpty(root)) return list;
        string wanted = ResilienceCheck.NormalizeSide(side);
        string prefix = FolderPrefix + wanted + "-";
        string[] directories;
        try
        {
            if (!Directory.Exists(root)) return list;
            directories = Directory.GetDirectories(root, prefix + "*");
        }
        catch
        {
            return list;
        }

        foreach (var directory in directories)
        {
            string name = Path.GetFileName(directory) ?? "";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string build = name.Substring(prefix.Length);
            if (build.Length == 0) continue;
            DateTime modified;
            try { modified = Directory.GetLastWriteTimeUtc(directory); }
            catch { modified = DateTime.MinValue; }
            bool complete = VerifyArchive(directory, out string why);
            list.Add(new InteropArchiveEntry(name, directory, wanted, build, modified, complete, why));
        }

        list.Sort(static (a, b) =>
        {
            int byTime = b.ModifiedUtc.CompareTo(a.ModifiedUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(b.FolderName, a.FolderName);
        });
        return list;
    }

    /// <summary>
    /// The command an operator runs after a game update: diff the newest archive that is
    /// <b>not</b> the running build against the live interop folder. Returns a one-line
    /// explanation instead when there is nothing to diff against — which is the honest answer on
    /// the first update after this feature landed, and the reason the archive exists at all.
    /// </summary>
    public static string FormatDriftMapCommand(
        string root,
        string side,
        string build,
        string interopDirectory,
        string outputPath = "")
    {
        string wanted = ResilienceCheck.NormalizeSide(side);
        InteropArchiveEntry? previous = null;
        InteropArchiveEntry? skipped = null;
        foreach (var entry in List(root, wanted))
        {
            if (string.Equals(entry.Build, build, StringComparison.Ordinal)) continue;
            // Job FUPD6: an archive that cannot prove it is whole is never offered as --old. A
            // drift map computed from a truncated set reports assemblies "removed" that were only
            // never copied, and it is the input the compatibility rewriter is fed.
            if (!entry.Complete) { skipped ??= entry; continue; }
            previous = entry;
            break;
        }

        if (previous == null && skipped != null)
            return "the only earlier interop archive for " + wanted + " (" + skipped.FolderName
                   + ") is INCOMPLETE (" + skipped.IncompleteReason
                   + ") - it is not a safe --old input and is not offered as one";

        if (previous == null)
            return "no earlier interop archive for " + wanted
                   + " yet - the drift map needs two builds, so this one is the baseline";

        string output = outputPath.Length > 0
            ? outputPath
            : Path.Combine(root ?? "", "driftmap-" + (build ?? "") + ".json");

        return "dotnet run --project tools/driftmap -- --old " + Quote(previous.Path)
               + " --new " + Quote(interopDirectory ?? "")
               + " --side " + wanted + " --build " + (build ?? "")
               + " --out " + Quote(output);
    }

    /// <summary>
    /// The archive rows for the resilience block: what happened this pass, then what is on disk.
    /// </summary>
    public static string DescribeStatus(InteropArchiveResult? result) =>
        result == null ? "not run" : DescribeOutcome(result) + DescribeTemporaries(result);

    /// <summary>
    /// What the sweep could not reclaim, appended to every archive row (job FUPD7). Retention's
    /// count never includes these — its glob cannot match a dot-prefixed folder — so without this
    /// clause an operator reading "keeping 3" is reading a number that leaves out real disk.
    /// </summary>
    private static string DescribeTemporaries(InteropArchiveResult result)
    {
        if (result.TemporaryFolders <= 0) return "";
        return "; plus " + result.TemporaryFolders.ToString(CultureInfo.InvariantCulture)
               + (result.TemporaryFolders == 1 ? " unfinished copy " : " unfinished copies ")
               + Size(result.TemporaryBytes) + " not yet reclaimed";
    }

    private static string DescribeOutcome(InteropArchiveResult result)
    {
        switch (result.Status)
        {
            case InteropArchiveStatus.Disabled:
                // Job FUPD6: the archive ships off, so the row has to say why, or an operator who
                // needs an --old input six weeks from now finds out by not having one.
                return result.Reason.Length == 0 ? "off" : "off - " + result.Reason;
            case InteropArchiveStatus.Incomplete:
                return result.FolderName + " is on disk but INCOMPLETE: " + result.Reason
                       + " - it will not be offered to driftmap as --old";
            case InteropArchiveStatus.Created:
                return result.FolderName + " created, " + result.FileCount + " files, " + Size(result.Bytes)
                       + " (keeping " + result.Retained + Removed(result) + ")";
            case InteropArchiveStatus.AlreadyPresent:
                return result.FolderName + " already archived (keeping " + result.Retained + Removed(result) + ")";
            case InteropArchiveStatus.RefusedDiskSpace:
                return "NOT archived, not enough disk: " + result.Reason
                       + " - the next update's drift map will have no --old input";
            case InteropArchiveStatus.Failed:
                return "NOT archived: " + result.Reason
                       + " - the next update's drift map will have no --old input";
            default:
                return "not archived (" + (result.Reason.Length == 0 ? "nothing to archive" : result.Reason) + ")";
        }
    }

    /// <summary>
    /// Read <c>[Resilience] InteropArchives</c> out of <c>BepInEx/config/progenitor.cfg</c>
    /// by hand, the way <c>CompatRewriteHost.ReadModeFromConfig</c> does: the pass runs on a
    /// background thread in a game-free file, so it cannot take a BepInEx <c>ConfigFile</c>
    /// binding. Anything missing, unparseable or out of range is <see cref="DefaultRetain"/>.
    /// </summary>
    public static int ReadRetainFromConfig(string bepInExRoot) =>
        TryReadFromConfig(bepInExRoot, ConfigSection, ConfigKey, out string text)
            ? ClampRetain(text)
            : DefaultRetain;

    /// <summary>
    /// One <c>section</c>/<c>key</c> out of the same file, as raw text. False means "the file
    /// does not exist, could not be read, or never mentions that key" — which callers read as
    /// "nothing was said", never as a value (job FUPD6: the archive's default-on rule has to tell
    /// an unset key from a key set to false).
    /// </summary>
    public static bool TryReadFromConfig(string bepInExRoot, string section, string key, out string value)
    {
        value = "";
        try
        {
            string path = Path.Combine(bepInExRoot ?? "", "config", ConfigFileName);
            if (!File.Exists(path)) return false;
            bool inSection = false;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    inSection = string.Equals(line, "[" + section + "]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    continue;
                value = line.Substring(eq + 1).Trim();
                return true;
            }
        }
        catch
        {
            // An unreadable config is "nothing was said". It is never a start failure.
        }
        return false;
    }

    /// <summary>
    /// A boolean key. False return is "not set"; a key set to junk is "not set" too, because a
    /// typo must not read as a deliberate <c>true</c> that writes 540 MB into someone's install.
    /// </summary>
    public static bool TryReadFlagFromConfig(string bepInExRoot, string section, string key, out bool value)
    {
        value = false;
        if (!TryReadFromConfig(bepInExRoot, section, key, out string text)) return false;
        if (bool.TryParse(text, out value)) return true;
        value = false;
        return false;
    }

    /// <summary>Parse and clamp a retention value. Junk is <see cref="DefaultRetain"/>.</summary>
    public static int ClampRetain(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? ClampRetain(n)
            : DefaultRetain;

    /// <summary>Clamp a retention count into <see cref="MinRetain"/>..<see cref="MaxRetain"/>.</summary>
    public static int ClampRetain(int value) =>
        value < MinRetain ? MinRetain : value > MaxRetain ? MaxRetain : value;

    // ---------------------------------------------------------------------------- internals

    /// <summary>
    /// Delete everything past the newest <paramref name="retain"/> archives for this side. The
    /// archive named <paramref name="keepAlways"/> is never a deletion candidate even if the
    /// clock says it is the oldest, because it is the one describing the build now running.
    /// </summary>
    private static int Retain(
        string root,
        string side,
        int retain,
        string keepAlways,
        List<string> notes,
        out IReadOnlyList<string> removed)
    {
        int limit = ClampRetain(retain);
        var all = List(root, side);
        var deleted = new List<string>();
        int kept = 0;

        foreach (var entry in all)
        {
            bool pinned = string.Equals(entry.FolderName, keepAlways, StringComparison.OrdinalIgnoreCase);
            if (kept < limit || pinned) { kept++; continue; }
            if (Delete(entry.Path, notes)) deleted.Add(entry.FolderName);
            else kept++; // it is still on disk, so it is still an archive
        }

        deleted.Reverse(); // oldest first reads better in the log
        removed = deleted;
        return kept;
    }

    /// <summary>
    /// <c>.interop-{side}-{build}.tmp-p{pid}-{yyyyMMddHHmmss}-{guid8}</c>. The pid and the start
    /// stamp are what let another process's sweep tell "someone is writing this right now" from
    /// "someone was killed while writing this" (job FUPD6).
    /// </summary>
    public static string TemporaryFolderName(string side, string build, int pid, DateTime startedUtc) =>
        TempPrefix + (side ?? "") + "-" + (build ?? "") + TempStampMarker
        + pid.ToString(CultureInfo.InvariantCulture) + "-"
        + startedUtc.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-"
        + Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>
    /// Read the owner back out of a temp folder name. False for anything this code did not write
    /// (a FUPD4-era name, or a folder someone else put there), which the sweep then judges by age
    /// alone.
    /// </summary>
    public static bool TryParseTemporaryName(string? folderName, out int pid, out DateTime startedUtc)
    {
        pid = 0;
        startedUtc = default;
        if (string.IsNullOrEmpty(folderName)) return false;
        int marker = folderName!.LastIndexOf(TempStampMarker, StringComparison.Ordinal);
        if (marker < 0) return false;
        string tail = folderName.Substring(marker + TempStampMarker.Length);
        var parts = tail.Split('-');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) || pid < 0)
            return false;
        if (!DateTime.TryParseExact(parts[1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startedUtc))
            return false;
        return true;
    }

    /// <summary>
    /// Job FUPD6 (R53 finding 5). Delete only temp copies nobody can still be writing.
    ///
    /// <para>Four rules, in order. A name this code wrote whose <b>process is gone</b> is deleted —
    /// that is the killed-pass case the sweep was always for. A name whose pid is alive but whose
    /// process <b>started after the stamp in the folder name</b> is a recycled pid wearing a dead
    /// owner's number, so the folder is stranded and is deleted too (job FUPD7, R54 finding 6).
    /// A name whose owner still cannot be ruled out is <b>left alone</b> until it is older than
    /// <see cref="InteropArchiveRequest.MaximumTemporaryAge"/>. A name this code did not write has
    /// no owner to ask, so it is judged by its folder's last-write time under the same age rule.
    /// </para>
    ///
    /// <para>The start-time discriminator is what the pid alone could never be. <c>test-server.ps1
    /// stop</c> kills the process mid-archive, Windows hands the number to a <c>dotnet.exe</c> or
    /// a Grok job within minutes, and until FUPD7 the sweep read that as the owner and printed
    /// <i>"left … alone: pid 12345 is running"</i> over 180 MB nothing would ever reclaim. The
    /// owner necessarily started before it wrote its own folder name, so a later start is proof of
    /// a different process; <see cref="ProcessStartGrace"/> absorbs the stamp's one-second
    /// truncation. A start time the OS will not give is not proof of anything, and reads as the
    /// owner — the sweep still fails closed.</para>
    ///
    /// <para>The old rule deleted every temp folder unconditionally, and the documented
    /// two-instance workflows (test server plus hosted child; <c>tools/extra-client.ps1</c>
    /// beside the Steam client) share one archive root with the same side and the same build id.
    /// The loser of that race could then publish a set with a hole in it under the final name.</para>
    ///
    /// <para>Whatever survives is tallied into <paramref name="leftovers"/> and printed on the
    /// archive row: retention's glob cannot see a <c>.interop-*</c> folder, so an untallied
    /// leftover is disk outside every budget the report advertises.</para>
    /// </summary>
    private static void SweepTemporaries(
        string root, string side, InteropArchiveRequest request, List<string> notes, TemporaryTally leftovers)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            DateTime now = Now(request);
            var maxAge = request.MaximumTemporaryAge;
            if (maxAge <= TimeSpan.Zero) maxAge = DefaultMaximumTemporaryAge;

            foreach (var directory in Directory.GetDirectories(root, TempPrefix + side + "-*"))
            {
                string name = Path.GetFileName(directory) ?? "";
                if (TryParseTemporaryName(name, out int pid, out var startedUtc))
                {
                    string pidText = pid.ToString(CultureInfo.InvariantCulture);
                    if (!IsAlive(request, pid))
                    {
                        Delete(directory, notes);
                        continue;
                    }

                    var ownerStart = ProcessStart(request, pid);
                    if (ownerStart.HasValue && ownerStart.Value - startedUtc > ProcessStartGrace)
                    {
                        notes.Add("sweeping " + name + ": pid " + pidText + " is alive but started "
                                  + Stamp(ownerStart.Value) + ", after this copy was stamped "
                                  + Stamp(startedUtc) + " - it is a recycled pid, not the owner");
                        Delete(directory, notes);
                        continue;
                    }

                    if (now - startedUtc > maxAge)
                    {
                        notes.Add("sweeping " + name + ": still claimed by pid " + pidText
                                  + " but older than " + Age(maxAge));
                        Delete(directory, notes);
                        continue;
                    }

                    notes.Add("left " + name + " alone: pid " + pidText
                              + " is running and may be writing it");
                    Keep(directory, leftovers);
                    continue;
                }

                DateTime written;
                try { written = Directory.GetLastWriteTimeUtc(directory); }
                catch { written = DateTime.MinValue; }
                if (now - written > maxAge)
                {
                    Delete(directory, notes);
                }
                else
                {
                    notes.Add("left " + name + " alone: it carries no owner stamp and is younger than "
                              + Age(maxAge));
                    Keep(directory, leftovers);
                }
            }
        }
        catch (Exception e)
        {
            notes.Add("could not sweep abandoned archive copies in " + root + ": " + e.Message);
        }
    }

    /// <summary>Count one surviving temp folder and the disk it holds.</summary>
    private static void Keep(string directory, TemporaryTally leftovers)
    {
        leftovers.Folders++;
        leftovers.Bytes += TotalBytes(directory);
    }

    /// <summary>The age guard in the units it is actually set in. 30 minutes must not print "0h".</summary>
    private static string Age(TimeSpan span) =>
        span < TimeSpan.FromHours(1)
            ? span.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture) + " min"
            : span.TotalHours.ToString("F0", CultureInfo.InvariantCulture) + "h";

    private static string Stamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// When the process holding <paramref name="pid"/> started, UTC, or null for "the OS would not
    /// say" (job FUPD7). Null is never treated as evidence: it leaves the folder to the age guard.
    ///
    /// <para>A request that overrides <see cref="InteropArchiveRequest.ProcessIsAlive"/> and not
    /// <see cref="InteropArchiveRequest.ProcessStartUtc"/> gets null rather than a real lookup —
    /// the pid it invented belongs to nobody, and asking the live process table about it would
    /// make the answer depend on what else the machine is running.</para>
    /// </summary>
    private static DateTime? ProcessStart(InteropArchiveRequest request, int pid)
    {
        if (pid <= 0) return null;
        try
        {
            var probe = request.ProcessStartUtc;
            if (probe != null) return probe(pid)?.ToUniversalTime();
            if (request.ProcessIsAlive != null) return null;
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            // Denied (a service, another user's process), already exited between the two calls, or
            // a platform that will not answer. All of them are "unknown", never "not the owner".
            return null;
        }
    }

    private static DateTime Now(InteropArchiveRequest request)
    {
        try { return request.UtcNow?.Invoke() ?? DateTime.UtcNow; }
        catch { return DateTime.UtcNow; }
    }

    /// <summary>
    /// Fails closed: a probe that throws, or an OS that will not answer, is "alive". Deleting a
    /// live process's copy is the failure being defended against; keeping a dead one costs disk
    /// until the age guard fires.
    /// </summary>
    private static bool IsAlive(InteropArchiveRequest request, int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var probe = request.ProcessIsAlive;
            if (probe != null) return probe(pid);
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // GetProcessById's "no such process"
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Does this archive folder carry a manifest that agrees with the files beside it? Public
    /// because the report, the tests and <c>tools/driftmap</c> all ask the same question about a
    /// folder they did not write (job FUPD6).
    /// </summary>
    public static bool VerifyArchive(string archivePath, out string reason)
    {
        if (!InteropArchiveManifest.TryLoad(archivePath, out var manifest, out reason) || manifest == null)
            return false;
        return manifest.Verify(archivePath, out reason);
    }

    /// <summary>
    /// An archive written before manifests existed (FUPD4) is complete far more often than not,
    /// and throwing away 180 MB of a set that cannot be recreated would be the wrong reading. So:
    /// when the manifest is simply <b>absent</b> and the folder matches the live interop set this
    /// pass just listed, write the manifest now. Anything else — a manifest that exists and
    /// disagrees, or a folder that does not match — stays Incomplete and is never healed.
    /// </summary>
    private static bool TryHealLegacyArchive(
        InteropArchiveRequest request,
        string side,
        string build,
        string finalPath,
        string originalReason,
        List<string> notes,
        out string reason)
    {
        reason = originalReason;
        try
        {
            string manifestPath = Path.Combine(finalPath, InteropArchiveManifest.FileName);
            if (File.Exists(manifestPath)) return false;
            if (string.IsNullOrEmpty(request.InteropDirectory) || !SafeDirectoryExists(request.InteropDirectory))
                return false;

            int liveCount = CountFiles(request.InteropDirectory);
            long liveBytes = TotalBytes(request.InteropDirectory);
            int archivedCount = CountFiles(finalPath);
            long archivedBytes = TotalBytes(finalPath);
            if (liveCount == 0 || liveCount != archivedCount || liveBytes != archivedBytes)
            {
                reason = "no " + InteropArchiveManifest.FileName + ", and the folder holds "
                         + archivedCount.ToString(CultureInfo.InvariantCulture) + " files / " + Size(archivedBytes)
                         + " against " + liveCount.ToString(CultureInfo.InvariantCulture) + " files / "
                         + Size(liveBytes) + " in " + request.InteropDirectory;
                return false;
            }

            var manifest = InteropArchiveManifest.Describe(
                finalPath, side, build, request.ProcessId, Now(request), request.HashFile, request.Cancellation);
            File.WriteAllText(manifestPath, manifest.Format());
            notes.Add("wrote " + InteropArchiveManifest.FileName + " for " + Path.GetFileName(finalPath)
                      + ": it predates manifests and still matches the live interop set");
            reason = "";
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e)
        {
            reason = originalReason + "; could not write one either: " + e.Message;
            return false;
        }
    }

    private static int ReadCurrentProcessId()
    {
        try { return Environment.ProcessId; }
        catch { return 0; }
    }

    private static bool Delete(string path, List<string> notes)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception e)
        {
            notes.Add("could not delete " + path + ": " + e.Message);
            return false;
        }
    }

    private static long FreeSpace(InteropArchiveRequest request, string root, List<string> notes)
    {
        var probe = request.FreeSpaceProbe;
        try
        {
            if (probe != null) return probe(root);
            string full = Path.GetFullPath(root);
            string? drive = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(drive)) return -1;
            return new DriveInfo(drive!).AvailableFreeSpace;
        }
        catch (Exception e)
        {
            // Unknown free space is not a refusal: a network path that cannot answer must not
            // cost the archive. The copy either fits or fails, and a failure is already handled.
            notes.Add("could not read free space on " + root + ": " + e.Message);
            return -1;
        }
    }

    private static int CountArchives(string root, string side) => List(root, side).Count;

    /// <summary>
    /// Files in a folder, not counting the archive's own manifest — so "214 files" means the same
    /// number on the pass that created the archive and on every pass that reads it back.
    /// </summary>
    private static int CountFiles(string path)
    {
        try
        {
            int n = 0;
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                if (!IsManifest(path, file)) n++;
            return n;
        }
        catch { return 0; }
    }

    private static long TotalBytes(string path)
    {
        try
        {
            long total = 0;
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                if (IsManifest(path, file)) continue;
                try { total += new FileInfo(file).Length; }
                catch { /* a file that vanished mid-listing is not worth failing over */ }
            }
            return total;
        }
        catch { return 0; }
    }

    private static bool IsManifest(string root, string file)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(file),
                Path.GetFullPath(Path.Combine(root, InteropArchiveManifest.FileName)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static string Relative(string root, string file)
    {
        string full = Path.GetFullPath(file);
        string prefix = Path.GetFullPath(root);
        if (!prefix.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            prefix += Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(prefix.Length)
            : Path.GetFileName(file);
    }

    private static string Removed(InteropArchiveResult result) =>
        result.Removed.Count == 0 ? "" : ", removed " + string.Join(", ", result.Removed);

    /// <summary>Bytes in the units an operator reads a disk in.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0) return "unknown";
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double value = bytes / 1024.0;
        if (value < 1024) return value.ToString("F1", CultureInfo.InvariantCulture) + " KB";
        value /= 1024.0;
        if (value < 1024) return value.ToString("F1", CultureInfo.InvariantCulture) + " MB";
        value /= 1024.0;
        return value.ToString("F2", CultureInfo.InvariantCulture) + " GB";
    }

    private static string Quote(string path) =>
        path.IndexOf(' ') < 0 ? path : "\"" + path + "\"";
}
