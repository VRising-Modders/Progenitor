using System;
using System.Globalization;
using System.IO;

namespace Progenitor.Bootstrap;

/// <summary>
/// Game-free decisions for Step E: watch <c>BepInEx/progenitor-core/Progenitor.dll.next</c>
/// and, once the file is complete and unchanged for one second, run the same cascade as
/// the bridge <c>core_reload</c> op with reason <see cref="WatchReason"/>.
/// </summary>
public static class CoreWatchPolicy
{
    public const string ConfigSection = "Reload";
    public const string ConfigKey = "WatchCore";

    /// <summary>
    /// Shipped default for <c>Reload.WatchCore</c> (job FRLD). <b>False</b>: watching
    /// <c>progenitor-core/Progenitor.dll.next</c> is a second automatic code-loading path,
    /// and a framework that ships to strangers must not enable one by default. Developers
    /// opt in per tree — <c>tools/deploy.ps1</c> and <c>tools/stage-test-server.ps1</c>
    /// write <c>[Reload] WatchCore = true</c> into the client and test-server config so the
    /// hot-swap loop is unchanged on our machines. See docs/dev-loop.md.
    /// </summary>
    public const bool DefaultWatchCore = false;
    public const string WatchReason = "watch";
    public const string NextSuffix = ".next";
    public const double SettleSeconds = 1.0;
    public const double ScanIntervalSeconds = 1.0;

    /// <summary>Hosted-server argv from the game's Start template. A dedicated server does not have this.</summary>
    public const string ParentPidArg = "-parentPID";

    /// <summary>Save / argv token of a Progenitor hosted child.</summary>
    public const string HostedSaveToken = "progenitor-hosted";

    /// <summary>
    /// Metadata method name of the live <c>StunProcess.Start</c> body. The abstract
    /// <c>StunProcess$$Start</c> is not the site that spawns the hosted child.
    /// </summary>
    public const string HostedSpawnDumpName = "StunProcess_MONO$$Start";

    /// <summary>
    /// Absolute root of the operator's live dedicated server. Process matching
    /// for a hosted child never treats this path as ours (match by path + argv,
    /// never by <c>VRisingServer.exe</c> name).
    /// </summary>
    public const string ForbiddenLiveServerRoot = @"C:\VRisingServer";

    /// <summary>
    /// Failed <c>ReadAllBytes</c> / rotate of a settled <c>.next</c> is retried
    /// this many times, then the stamp advances so the same file is not hammered
    /// until its length or write time changes.
    /// </summary>
    public const int PromoteRetryLimit = 5;

    public const string ResumeReasonChildExited = "hosted child exited";
    public const string ResumeReasonDedicatedJoin = "joined a dedicated server";

    /// <summary>The staged Core's leaf in the single-folder layout (<c>Progenitor.core.next</c>).</summary>
    public static string NextFileName => CoreLayout.ImageFileName + NextSuffix;

    public static string NextPath(string dllPath) => dllPath + NextSuffix;

    /// <summary>
    /// Length plus last-write of one <c>.next</c> scan. Two consecutive polls with
    /// the same pair means the copy finished; age-of-mtime alone is not enough
    /// because <c>Copy-Item</c> preserves the source write time.
    /// </summary>
    public readonly struct FileStamp : IEquatable<FileStamp>
    {
        public FileStamp(long length, DateTime lastWriteUtc)
        {
            Length = length;
            LastWriteUtc = lastWriteUtc;
        }

        public long Length { get; }
        public DateTime LastWriteUtc { get; }

        public bool Equals(FileStamp other) =>
            Length == other.Length && LastWriteUtc == other.LastWriteUtc;

        public override bool Equals(object? obj) => obj is FileStamp other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Length, LastWriteUtc);

        public static bool operator ==(FileStamp left, FileStamp right) => left.Equals(right);

        public static bool operator !=(FileStamp left, FileStamp right) => !left.Equals(right);
    }

    /// <summary>
    /// True when this scan matches the previous scan (same length and write time)
    /// and the file is non-empty. First sight of a path is never settled.
    /// </summary>
    public static bool IsSettled(FileStamp? previous, FileStamp current) =>
        previous is FileStamp p
        && p.Length == current.Length
        && p.LastWriteUtc == current.LastWriteUtc
        && current.Length > 0;

    /// <summary>
    /// Age-of-mtime helper kept for tests of the old 1 s wait. Live watch uses
    /// <see cref="IsSettled(FileStamp?, FileStamp)"/> (two identical scans).
    /// </summary>
    public static bool IsSettled(DateTime lastWriteUtc, DateTime nowUtc, double settleSeconds = SettleSeconds) =>
        (nowUtc - lastWriteUtc).TotalSeconds >= settleSeconds;

    /// <summary>
    /// True when this process is the bundled server a client spawned for a private game.
    /// Those children inherit the client's BepInEx layout; Core swaps belong to the client
    /// (and would fight the child's own load). Dedicated / test-server processes do not
    /// pass <see cref="ParentPidArg"/>.
    /// </summary>
    public static bool IsHostedChild(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return false;
        if (ContainsArg(commandLine, ParentPidArg)) return true;
        return ContainsToken(commandLine, HostedSaveToken);
    }

    /// <summary>
    /// True when <paramref name="arguments"/> look like the client's spawn of a
    /// Progenitor hosted child (<c>-parentPID</c> or <c>progenitor-hosted</c>).
    /// Used so the <em>client</em> can refuse WatchCore while that child is live.
    /// </summary>
    public static bool LooksLikeHostedSpawn(string? arguments) =>
        IsHostedChild(arguments);

    /// <summary>One scan of the staged file. Tests assert this; live <c>CoreWatch</c> logs the matching line.</summary>
    public static CoreWatchDecision Decide(
        bool enabled,
        bool hostedChild,
        bool alreadyReloading,
        bool nextExists,
        bool settled,
        bool sameStampAsLastRequest,
        bool hostingPrivateGame = false)
    {
        if (!enabled) return CoreWatchDecision.Disabled;
        if (!nextExists) return CoreWatchDecision.Skip;
        if (!settled) return CoreWatchDecision.Skip;
        if (sameStampAsLastRequest) return CoreWatchDecision.Skip;
        if (hostedChild) return CoreWatchDecision.RefuseHosted;
        if (hostingPrivateGame) return CoreWatchDecision.RefuseHosting;
        if (alreadyReloading) return CoreWatchDecision.RefuseInFlight;
        return CoreWatchDecision.Request;
    }

    /// <summary>
    /// After <see cref="Decide"/> returns <see cref="CoreWatchDecision.Request"/>,
    /// look at the settled bytes. An invalid image is quarantined and must not
    /// schedule a swap (evidence 83). Bytes identical to the live Core are
    /// skipped with one log line instead of unloading the live generation.
    /// Invalid wins over identical so junk that happens to match a junk live
    /// file is still renamed away.
    /// </summary>
    public static CoreWatchInspectDecision InspectStaged(bool nextIsValidImage, bool identicalToLive)
    {
        if (!nextIsValidImage) return CoreWatchInspectDecision.Quarantine;
        if (identicalToLive) return CoreWatchInspectDecision.SkipIdentical;
        return CoreWatchInspectDecision.Request;
    }

    /// <summary>True when inspect still allows <see cref="CoreLoader.RequestReload"/>.</summary>
    public static bool MayRequestAfterInspect(CoreWatchInspectDecision inspect) =>
        inspect == CoreWatchInspectDecision.Request;

    /// <summary>
    /// Identical-to-live stamps so the skip line is once. Quarantine does not
    /// stamp (the file is gone or will be retried; stay armed for the next
    /// <c>.next</c>). A real swap still waits for a successful promote.
    /// </summary>
    public static bool StampAfterInspect(CoreWatchInspectDecision inspect) =>
        inspect == CoreWatchInspectDecision.SkipIdentical;

    /// <summary>
    /// True when a scheduled cascade must abort: a staged <c>.next</c> was
    /// present and promote did not rotate it onto the live dll (quarantine or
    /// a failed read). Reloading the unchanged live file is the evidence 83
    /// pointless swap. A Core-less process still Installs the live file
    /// (protocol-bump recovery: the matching image may already be on disk).
    /// </summary>
    public static bool CancelScheduledReload(bool hadStagedNext, bool promoteSucceeded) =>
        CancelScheduledReload(hadStagedNext, promoteSucceeded, coreLoaded: true);

    /// <summary>
    /// Same as <see cref="CancelScheduledReload(bool, bool)"/> with an explicit
    /// Core-loaded flag. Core-less never cancels: there is nothing to unload.
    /// </summary>
    public static bool CancelScheduledReload(bool hadStagedNext, bool promoteSucceeded, bool coreLoaded) =>
        coreLoaded && hadStagedNext && !promoteSucceeded;

    /// <summary>
    /// True when two buffers are the same non-empty Core image. Null or empty
    /// is never identical (those fail image validation instead).
    /// </summary>
    public static bool BytesIdentical(byte[]? nextBytes, byte[]? liveBytes)
    {
        if (nextBytes == null || liveBytes == null) return false;
        if (nextBytes.Length == 0 || liveBytes.Length == 0) return false;
        if (nextBytes.Length != liveBytes.Length) return false;
        for (int i = 0; i < nextBytes.Length; i++)
        {
            if (nextBytes[i] != liveBytes[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// True when <see cref="CoreLoader.RequestReload"/> queued the cascade.
    /// A refuse must not stamp the watch so the next stable scan retries.
    /// The stamp itself advances only after a successful promote of that file,
    /// or after an identical-to-live skip.
    /// </summary>
    public static bool RequestAccepted(string? requestReloadResult) =>
        !string.IsNullOrEmpty(requestReloadResult)
        && requestReloadResult.StartsWith("scheduled", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True while the watcher is parked on a refused image: the file on disk still has
    /// the (length, write time) that <c>RequestReload</c> refused <b>permanently</b>
    /// (<see cref="CoreWatchRefusal.Permanent"/>). Parked means the tick does no work at
    /// all — no read, no SHA-256, no log line — because that refusal ("generation N is
    /// still alive") holds until the process restarts, and a restart promotes the staged
    /// file without the watcher. The park is keyed to the stamp, so operator action — a
    /// fresh <c>deploy.ps1</c>, a hand-copied image, deleting the file — unparks it on the
    /// next scan (job FREL8, R67 finding 1). A <em>transient</em> refusal never parks; it
    /// is held with <see cref="HeldOnStamp"/> instead (job FREL9, R68 finding 1).
    /// </summary>
    public static bool ParkedOnStamp(FileStamp? parked, bool fileExists, FileStamp current) =>
        fileExists && parked is FileStamp p && p.Equals(current);

    /// <summary>
    /// Backoff between retries of an image <c>RequestReload</c> refused transiently
    /// (job FREL9). Long enough that a stuck plugin-ALC latch costs one read every five
    /// scans rather than one per scan, short enough that the deploy's hold window still
    /// contains several tries.
    /// </summary>
    public const double TransientRetrySeconds = 5.0;

    /// <summary>
    /// How many retries a transiently refused image gets before the watcher gives up on it
    /// and parks (job FREL10, R69 finding 6). Twelve at
    /// <see cref="TransientRetrySeconds"/> is one minute — far longer than the few GCs a
    /// plugin-ALC latch needs, and short enough that a latch which never clears (BepInEx's
    /// IL2CPP detour delegate cache is permanent by design) stops costing a request, a
    /// refusal and a log line every five seconds for the life of the process. The bound is
    /// per stamp: a re-staged image starts a fresh minute.
    /// </summary>
    public const int TransientRetryLimit = 12;

    /// <summary>
    /// True when a transient refusal has stopped being transient: this many retries on the
    /// same bytes, so the watcher parks the image instead of asking forever
    /// (<paramref name="retriesOnThisStamp"/> counts the refusals <em>after</em> the first,
    /// so the park lands at <see cref="TransientRetryLimit"/> × <see cref="TransientRetrySeconds"/>
    /// seconds of holding). The park is keyed to the stamp exactly as
    /// <see cref="ParkedOnStamp"/> is, so a fresh deploy unparks it.
    /// </summary>
    public static bool ExhaustedTransientRetries(int retriesOnThisStamp) =>
        retriesOnThisStamp >= TransientRetryLimit;

    /// <summary>
    /// True when a retry of a held image must <b>not</b> read and hash it again: the same
    /// (length, write time) already passed the read, the SHA-256, the allowlist and the
    /// identical-to-live comparison on the attempt that was refused, and the stamp is the
    /// evidence the bytes did not move (job FREL10, R69 finding 6). Before this, every
    /// retry cost two 2 MB reads and a hash of bytes the watcher had already inspected.
    /// </summary>
    public static bool MayReuseInspection(FileStamp? inspected, bool fileExists, FileStamp current) =>
        fileExists && inspected is FileStamp p && p.Equals(current);

    /// <summary>
    /// Marker of <c>CoreReloadPolicy.RefusePluginAlcAlive</c>. Its text ends in the same
    /// "still alive; restart the process" as the permanent refusal, but it is transient by
    /// construction: <c>ReloaderHost.PollPluginLatch</c> clears the latch as soon as the
    /// weaks die, which is what the next <c>RequestReload</c> calls first.
    /// </summary>
    private const string PluginAlcMarker = "plugin ALC";

    /// <summary>Marker of <c>CoreReloadPolicy.RefusePreviousAlive</c>, the one permanent refusal.</summary>
    private const string RestartMarker = "still alive; restart the process";

    /// <summary>
    /// True when a <c>RequestReload</c> answer keeps the watcher off this image for the rest
    /// of the process (job FREL9, R68 finding 1). Exactly one of the four non-<c>scheduled</c> answers
    /// <c>CoreLoader.RequestReload</c> can return is permanent:
    /// <list type="bullet">
    /// <item><c>RefusePreviousAlive</c> — generation N did not collect; only a restart clears it.
    ///   <b>Permanent.</b></item>
    /// <item><c>RefusePluginAlcAlive</c> — self-clearing in <c>PollPluginLatch</c>. Transient.</item>
    /// <item><c>RefuseAlreadyReloading</c> — a swap is in flight. Transient.</item>
    /// <item><c>FormatFailed(..., "request", ...)</c> from the catch — an exception. Transient.</item>
    /// </list>
    /// Anything unrecognised is treated as transient: the cost of retrying is one read per
    /// <see cref="TransientRetrySeconds"/>, the cost of parking wrongly is a stranded Core.
    /// </summary>
    public static bool ParkOnRefusal(string? requestReloadResult)
    {
        if (RequestAccepted(requestReloadResult)) return false;
        string text = requestReloadResult ?? "";
        if (ContainsToken(text, PluginAlcMarker)) return false;
        return ContainsToken(text, RestartMarker);
    }

    /// <summary>
    /// True when this answer refused the swap but will clear on its own: hold the image for
    /// <see cref="TransientRetrySeconds"/> and ask again. The complement of
    /// <see cref="ParkOnRefusal"/> among the refusals.
    /// </summary>
    public static bool HoldOnRefusal(string? requestReloadResult) =>
        !RequestAccepted(requestReloadResult) && !ParkOnRefusal(requestReloadResult);

    /// <summary>
    /// True while a transiently refused image is inside its backoff: same bytes on disk,
    /// and the retry deadline has not arrived. Unlike a park this expires on its own, so
    /// a plugin-ALC latch that clears a second later still gets its swap.
    /// </summary>
    public static bool HeldOnStamp(
        FileStamp? held, bool fileExists, FileStamp current, double nowSeconds, double retryAtSeconds) =>
        fileExists && held is FileStamp p && p.Equals(current) && nowSeconds < retryAtSeconds;

    /// <summary>
    /// The single <c>watch.parked</c> the status dump carries covers both parks: the staged
    /// <c>.next</c> and the Core-less live image (R68 finding 6). Either one means "only a
    /// restart will load this Core".
    /// </summary>
    public static bool ParkedForDump(bool stagedParked, bool liveParked) =>
        stagedParked || liveParked;

    /// <summary>Reason shown with <see cref="ParkedForDump"/>; the staged image wins when both are parked.</summary>
    public static string? ParkReasonForDump(bool stagedParked, string? stagedReason, string? liveReason) =>
        stagedParked ? stagedReason : liveReason;

    /// <summary>Deadline of the next retry after a transient refusal.</summary>
    public static double NextRetryAt(double nowSeconds) => nowSeconds + TransientRetrySeconds;

    /// <summary>
    /// One line per distinct reason, never one per tick: a transient refusal that keeps
    /// answering the same string is logged once and then retried silently until the answer
    /// changes or the swap lands.
    /// </summary>
    public static bool ShouldLogRefusal(string? lastLoggedReason, string? reason) =>
        !string.Equals(lastLoggedReason ?? "", reason ?? "", StringComparison.Ordinal);

    /// <summary>
    /// The Core-less recovery path's gate. <paramref name="alreadyReloadingNow"/> must be a
    /// <b>fresh</b> read taken after the staged path of the same tick may have scheduled a
    /// swap (R68 finding 1): a value read at the top of the tick is stale the moment the
    /// staged arm calls <c>RequestReload</c>, and using it makes this path ask a second time,
    /// collect <c>RefuseAlreadyReloading</c>, and hold the recovery image for no reason.
    /// </summary>
    public static bool MayTryCoreLessLiveInstall(
        bool enabled, bool hostedChild, bool hostingPrivateGame, bool alreadyReloadingNow) =>
        enabled && !hostedChild && !hostingPrivateGame && !alreadyReloadingNow;

    /// <summary>True when this promote outcome may advance the watch stamp.</summary>
    public static bool StampAfterPromote(bool promoteSucceeded) => promoteSucceeded;

    /// <summary>True while a failed promote of the same stamp should be retried.</summary>
    public static bool MayRetryPromote(int failures) =>
        failures > 0 && failures < PromoteRetryLimit;

    /// <summary>True when the same settled file has failed promote too many times.</summary>
    public static bool ExhaustedPromoteRetries(int failures) =>
        failures >= PromoteRetryLimit;

    /// <summary>
    /// True when <paramref name="commandLine"/> names this process as the parent
    /// (<c>-parentPID 1234</c> / <c>-parentPID=1234</c>) or carries
    /// <see cref="HostedSaveToken"/>.
    /// </summary>
    public static bool HasHostedArgv(string? commandLine, int parentPid)
    {
        if (string.IsNullOrEmpty(commandLine)) return false;
        if (ContainsToken(commandLine, HostedSaveToken)) return true;
        return HasParentPid(commandLine, parentPid);
    }

    /// <summary>
    /// True when a process with this path and command line is the Progenitor
    /// hosted child of <paramref name="parentPid"/>. Never matches by executable
    /// name. The live dedicated root is always refused. When the command line
    /// cannot be read, only the exact spawned path (from
    /// <see cref="HostedSpawnDumpName"/> / <c>StunProcess.Start</c>)
    /// counts so a still-running child is not missed.
    /// </summary>
    public static bool IsLiveHostedChild(
        string? executablePath,
        string? commandLine,
        int parentPid,
        string? spawnedPath)
    {
        if (IsForbiddenLiveServerPath(executablePath) || IsForbiddenLiveServerPath(spawnedPath))
            return false;
        if (HasHostedArgv(commandLine, parentPid))
            return true;
        if (!string.IsNullOrEmpty(commandLine)) return false;
        return PathsEqual(executablePath, spawnedPath);
    }

    /// <summary>
    /// True when a <c>GameHelper.LaunchGame</c> is a join of a dedicated (or
    /// otherwise non-hosted) server: not hosting, and the save name is not
    /// <see cref="HostedSaveToken"/>. Connecting to the child just spawned
    /// keeps the hosting flag.
    /// </summary>
    public static bool IsDedicatedJoin(bool shouldHost, string? serverSaveName)
    {
        if (shouldHost) return false;
        if (!string.IsNullOrEmpty(serverSaveName) && ContainsToken(serverSaveName, HostedSaveToken))
            return false;
        return true;
    }

    /// <summary>
    /// True when a client that was hosting should drop the flag: the child is
    /// gone, or this process joined a dedicated server. Query failures must pass
    /// <paramref name="childStillRunning"/> as true (fail closed).
    /// </summary>
    public static bool ShouldClearHosting(bool wasHosting, bool childStillRunning, bool dedicatedJoin)
    {
        if (!wasHosting) return false;
        if (dedicatedJoin) return true;
        return !childStillRunning;
    }

    /// <summary>
    /// Fail-closed live-child result of one process scan. A throw, or a scan that
    /// could not query any process, is still-running so WatchCore does not resume
    /// while the bundled server is alive. Only a completed scan that found no
    /// match is a positive "child exited".
    /// </summary>
    public static bool ChildStillRunning(bool scanFailed, int processesQueried, bool foundLive)
    {
        if (foundLive) return true;
        if (scanFailed) return true;
        if (processesQueried <= 0) return true;
        return false;
    }

    /// <summary>
    /// Job HOSTPROBE: fail-closed verdict of one name-pre-filtered scan
    /// (<see cref="HostedChildTracker"/>). A failed enumeration keeps the flag. A completed
    /// enumeration with no candidate at all is a positive "child exited" (the pre-filter is
    /// what made <paramref name="processesQueried"/> zero, not a query failure). Candidates of
    /// which none could be read keep the flag, exactly like <see cref="ChildStillRunning"/>.
    /// </summary>
    public static bool ChildStillRunningAfterScan(bool scanFailed, int candidates, int processesQueried, bool foundLive)
    {
        if (foundLive) return true;
        if (scanFailed) return true;
        if (candidates <= 0) return false;
        return ChildStillRunning(scanFailed: false, processesQueried, foundLive: false);
    }

    /// <summary>
    /// The scan verdict with the hosting-probe review's follow-ups (2026-10-06). A found child or a failed enumeration keeps
    /// the flag; no candidate at all clears it. (b) When the spawn site reported the child's PID and the completed scan no
    /// longer lists it, that child is gone: clear, even if other candidates could not be read. (c) Otherwise a candidate that
    /// could not be read (<paramref name="queryFailed"/>, or fewer read than listed) may be the child: keep the flag. Only a
    /// scan that read every candidate and matched none clears it.
    /// </summary>
    public static bool ChildStillRunningAfterScan(
        bool scanFailed, int candidates, int processesQueried, bool foundLive, bool queryFailed, int spawnPid, bool spawnPidListed)
    {
        if (foundLive) return true;
        if (scanFailed) return true;
        if (candidates <= 0) return false;
        if (spawnPid > 0 && !spawnPidListed) return false;
        if (queryFailed || processesQueried < candidates) return true;
        return false;
    }

    public static string FormatHostedScanFailed() =>
        "Core watch: hosted-child probe could not query processes; keeping the hosting flag.";

    public static string FormatHostedQueryFailed() =>
        "Core watch: hosted-child probe OpenProcess/NtQueryInformationProcess failed; keeping the hosting flag.";

    /// <summary>
    /// True when <paramref name="path"/> is <see cref="ForbiddenLiveServerRoot"/>
    /// or a descendant. A sibling such as <c>C:\VRisingServerBackup</c> is not a match.
    /// </summary>
    public static bool IsForbiddenLiveServerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = NormalizePath(path);
            string forbidden = NormalizePath(ForbiddenLiveServerRoot);
            if (string.Equals(full, forbidden, StringComparison.OrdinalIgnoreCase)) return true;
            string prefix = forbidden.EndsWith("\\", StringComparison.Ordinal) ? forbidden : forbidden + "\\";
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasParentPid(string? commandLine, int pid)
    {
        if (string.IsNullOrEmpty(commandLine) || pid <= 0) return false;
        string pidText = pid.ToString(CultureInfo.InvariantCulture);
        int start = 0;
        while (start < commandLine.Length)
        {
            int i = IndexOfIgnoreCase(commandLine, ParentPidArg, start);
            if (i < 0) return false;
            if (i > 0 && !IsBoundary(commandLine[i - 1]))
            {
                start = i + 1;
                continue;
            }
            int after = i + ParentPidArg.Length;
            if (after < commandLine.Length && commandLine[after] == '=') after++;
            while (after < commandLine.Length && (commandLine[after] == ' ' || commandLine[after] == '\t'))
                after++;
            if (after + pidText.Length <= commandLine.Length
                && string.Compare(commandLine, after, pidText, 0, pidText.Length, StringComparison.Ordinal) == 0)
            {
                int end = after + pidText.Length;
                if (end >= commandLine.Length || IsBoundary(commandLine[end]) || !char.IsDigit(commandLine[end]))
                    return true;
            }
            start = i + 1;
        }
        return false;
    }

    public static string FormatSettled() =>
        "Core watch: Progenitor.dll.next settled; requesting reload.";

    public static string FormatSkippedIdentical() =>
        "Core watch: Progenitor.dll.next is identical to the live Core; skipped";

    public static string FormatRefusedInFlight() =>
        "Core watch refused: a swap is already in progress.";

    public static string FormatRefusedHosted() =>
        "Core watch refused: this process is a hosted child.";

    public static string FormatRefusedHosting() =>
        "Core watch refused: this client is hosting a private game; the child keeps the copied Core.";

    public static string FormatDisabled() =>
        "Core watch: Reload.WatchCore is false.";

    /// <summary>
    /// One line per refused image (job FREL8). A refusal such as "generation N is still
    /// alive; restart the process" is permanent for this process, so the watcher parks
    /// on the refused stamp instead of re-reading and re-hashing the 2 MB image every
    /// second: 810 unguarded triples on the live test server turned the 4,000-entry log
    /// ring over roughly every twenty minutes (R67 finding 1).
    /// </summary>
    public static string FormatRefusedRequest(string? reason) =>
        "Core watch: RequestReload refused; will retry when the staged file changes; "
        + "a restart promotes it. " + (reason ?? "");

    /// <summary>
    /// One line per distinct transient reason (job FREL9). The image is not parked: the
    /// watcher tries again after <see cref="TransientRetrySeconds"/>, so the operator is
    /// told to wait rather than to restart.
    /// </summary>
    public static string FormatTransientRefusal(string? reason) =>
        "Core watch: RequestReload refused for now; retrying every "
        + TransientRetrySeconds.ToString("0.#", CultureInfo.InvariantCulture)
        + " s until it clears. " + (reason ?? "");

    /// <summary>
    /// The one line a give-up writes (job FREL10, R69 finding 6): a refusal that answered the
    /// same way for <see cref="TransientRetryLimit"/> retries is not clearing on its own, so
    /// the watcher parks on those bytes and the operator is told to restart — the same advice
    /// the permanent refusal gives, arrived at by measurement instead of by the string.
    /// </summary>
    public static string FormatTransientGaveUp(int retries, string? reason) =>
        "Core watch: RequestReload has been refused for "
        + retries.ToString(CultureInfo.InvariantCulture) + " retries over "
        + (retries * TransientRetrySeconds).ToString("0.#", CultureInfo.InvariantCulture)
        + " s; it is not clearing. Parked until the staged file changes; a restart promotes it. "
        + (reason ?? "");

    public static string FormatPromoteRetry(int failures, int limit, string? error) =>
        "Core watch: promote failed; will retry "
        + failures.ToString(CultureInfo.InvariantCulture) + "/"
        + limit.ToString(CultureInfo.InvariantCulture) + ". " + (error ?? "");

    public static string FormatPromoteRetryExhausted(string? error) =>
        "Core watch: promote failed; giving up until the file changes. " + (error ?? "");

    public static string FormatWatchResumed(string? reason) =>
        "Core watch: WatchCore resumed (" + (reason ?? "") + ").";

    /// <summary>
    /// Disk name for a Core image that failed Install / protocol check. The watcher
    /// only looks at <c>.next</c>, so this file cannot retrigger a swap. Milliseconds
    /// keep two failures in the same UTC second from clobbering each other.
    /// </summary>
    public static string FailedFileName(DateTime utc) => FailedFileName(CoreLayout.FileName, utc);

    /// <summary>
    /// <see cref="FailedFileName(DateTime)"/> for the live image named <paramref name="liveFileName"/>
    /// (<c>Progenitor.core</c> in the single-folder layout, <c>Progenitor.dll</c> in the legacy one).
    /// </summary>
    public static string FailedFileName(string? liveFileName, DateTime utc) =>
        (string.IsNullOrEmpty(liveFileName) ? CoreLayout.FileName : liveFileName) + ".failed-"
        + utc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

    public static string QuarantinePath(string dllPath, DateTime utc)
    {
        if (string.IsNullOrEmpty(dllPath))
            throw new ArgumentException("dllPath is required.", nameof(dllPath));
        string? dir = Path.GetDirectoryName(dllPath);
        string name = FailedFileName(Path.GetFileName(dllPath), utc);
        if (string.IsNullOrEmpty(dir))
            return name;
        return Path.Combine(dir, name);
    }

    private static bool ContainsArg(string commandLine, string arg)
    {
        int i = IndexOfIgnoreCase(commandLine, arg);
        if (i < 0) return false;
        if (i > 0 && !IsBoundary(commandLine[i - 1])) return false;
        int after = i + arg.Length;
        if (after < commandLine.Length && !IsBoundary(commandLine[after]) && commandLine[after] != '=')
            return false;
        return true;
    }

    private static bool ContainsToken(string text, string token)
    {
        int i = IndexOfIgnoreCase(text, token);
        return i >= 0;
    }

    private static int IndexOfIgnoreCase(string haystack, string needle) =>
        IndexOfIgnoreCase(haystack, needle, 0);

    private static int IndexOfIgnoreCase(string haystack, string needle, int start)
    {
        if (string.IsNullOrEmpty(needle) || haystack.Length - start < needle.Length) return -1;
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            haystack, needle, start, CompareOptions.OrdinalIgnoreCase);
    }

    private static bool IsBoundary(char c) =>
        c == ' ' || c == '"' || c == '\'' || c == '\t' || c == '\r' || c == '\n';

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        try
        {
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

/// <summary>Result of <see cref="CoreWatchPolicy.Decide"/>.</summary>
public enum CoreWatchDecision
{
    Skip,
    Disabled,
    RefuseHosted,
    RefuseHosting,
    RefuseInFlight,
    Request,
}

/// <summary>Result of <see cref="CoreWatchPolicy.InspectStaged"/>.</summary>
public enum CoreWatchInspectDecision
{
    Request,
    SkipIdentical,
    Quarantine,
}
