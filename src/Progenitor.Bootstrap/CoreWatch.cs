using System;
using System.Diagnostics;
using System.IO;
using BepInEx;

namespace Progenitor.Bootstrap;

/// <summary>
/// Step E: poll <c>BepInEx/progenitor-core/Progenitor.dll.next</c> on Bootstrap's
/// FrameTicker. Two consecutive scans with the same (length, write time) inspect
/// the bytes: invalid images are quarantined with no reload, bytes identical to
/// the live dll are skipped, otherwise <see cref="CoreLoader.RequestReload"/>
/// runs with reason <c>watch</c> — the same cascade as the bridge
/// <c>core_reload</c> op. The watch stamp advances when that image has been
/// promoted onto the live dll, or after an identical skip. Refuses while a swap
/// is in flight, on a hosted child, and on a client that currently has a live
/// hosted child.
/// Hosting clears when the child exits or this client joins a dedicated server.
/// Config <c>Reload.WatchCore</c> (default <b>false</b>; opt-in for developers, written
/// as true into our own client and test-server config by <c>tools/deploy.ps1</c>).
/// The settled image's SHA-256 and size are logged before the swap is requested, and an
/// image the optional <c>BepInEx/config/progenitor-allow.txt</c> does not list is
/// quarantined instead of promoted (job FRLD).
/// A request <c>CoreLoader</c> refuses <b>permanently</b> (generation N is still alive)
/// <b>parks</b> the watcher on that image (job FREL8): until its length or write time
/// changes the tick reads nothing, hashes nothing and logs nothing, because that refusal
/// lasts until the process restarts — and the restart promotes the staged file without the
/// watcher. <c>watch.parked</c> in the status dump, for the staged image and for the
/// Core-less live image alike (job FREL9, R68 finding 6).
/// A <b>transient</b> refusal (a plugin ALC still collecting, a swap already in flight, an
/// exception) does not park on its first answer: the image is held for
/// <see cref="CoreWatchPolicy.TransientRetrySeconds"/> and tried again, with one log line
/// per distinct reason (job FREL9, R68 finding 1). A retry re-reads and re-hashes nothing —
/// the stamp already says the bytes are the ones the first attempt inspected — and after
/// <see cref="CoreWatchPolicy.TransientRetryLimit"/> retries on one stamp the watcher parks
/// with a line saying the refusal has stopped being transient (job FREL10, R69 finding 6).
/// </summary>
internal static class CoreWatch
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _nextScan;
    private static CoreWatchPolicy.FileStamp _lastRequestedStamp;
    private static bool _haveRequestedStamp;
    private static CoreWatchPolicy.FileStamp _lastSeenStamp;
    private static bool _haveSeenStamp;
    private static bool _loggedHosted;
    private static bool _loggedHosting;
    private static bool _loggedDisabled;
    private static bool _loggedInFlight;
    private static CoreWatchPolicy.FileStamp _parkedStamp;
    private static bool _haveParkedStamp;
    private static string? _parkedReason;
    private static CoreWatchPolicy.FileStamp _parkedLiveStamp;
    private static bool _haveParkedLiveStamp;
    private static string? _parkedLiveReason;
    private static bool _parkedStagedActive;
    private static bool _parkedLiveActive;
    private static CoreWatchPolicy.FileStamp _heldStamp;
    private static bool _haveHeldStamp;
    private static double _heldRetryAt;
    private static int _heldRetries;
    private static string? _lastTransientReason;
    private static CoreWatchPolicy.FileStamp _inspectedStamp;
    private static bool _haveInspectedStamp;
    private static CoreWatchPolicy.FileStamp _heldLiveStamp;
    private static bool _haveHeldLiveStamp;
    private static double _heldLiveRetryAt;
    private static int _heldLiveRetries;
    private static string? _lastTransientLiveReason;
    private static int _promoteFailures;
    private static CoreWatchPolicy.FileStamp _lastTriedLiveStamp;
    private static bool _haveTriedLiveStamp;
    private static CoreWatchPolicy.FileStamp _lastSeenLiveStamp;
    private static bool _haveSeenLiveStamp;

    internal static void Tick()
    {
        try { TickCore(); }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("Core watch: " + e.Message);
        }
    }

    internal static bool HasPendingNext()
    {
        try
        {
            string path = CoreWatchPolicy.NextPath(CoreLayout.DllPath(Paths.BepInExRootPath));
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Advance the watch stamp after <c>PromoteCoreFile</c> wrote the settled
    /// bytes onto the live dll. A refuse or a failed rotate must not call this.
    /// </summary>
    internal static void NotePromoteSucceeded()
    {
        if (!_haveSeenStamp) return;
        _lastRequestedStamp = _lastSeenStamp;
        _haveRequestedStamp = true;
        _promoteFailures = 0;
        Unpark();
    }

    /// <summary>
    /// Job FREL8: stop looking at the image <c>RequestReload</c> just refused, when that
    /// refusal is the process-lifetime one ("generation N is still alive; restart the
    /// process"). Retrying those bytes once a second buys nothing and costs a 2 MB read, a
    /// SHA-256 and three log lines per second (R67 finding 1). The park is released as soon
    /// as the file's length or write time changes, or the file is gone. Job FREL9 narrows it
    /// to that one answer: the other three self-clear and are held, not parked.
    /// </summary>
    private static void Park(CoreWatchPolicy.FileStamp stamp, string? reason)
    {
        _parkedStamp = stamp;
        _haveParkedStamp = true;
        _parkedReason = reason;
        _parkedStagedActive = true;
        ClearHold();
        PublishParked();
    }

    private static void Unpark()
    {
        _haveParkedStamp = false;
        _parkedReason = null;
        _parkedStagedActive = false;
        PublishParked();
    }

    /// <summary>
    /// Job FREL9: back off from an image whose refusal clears on its own. Unlike a park this
    /// expires by itself, so the swap still lands once the plugin ALCs collect or the
    /// in-flight cascade ends. Job FREL10 (R69 finding 6) bounds it: the retries on one stamp
    /// are counted, and the <see cref="CoreWatchPolicy.TransientRetryLimit"/>-th one parks the
    /// image with a line saying the refusal has stopped being transient. Without the bound a
    /// latch that never clears — the upstream IL2CPP detour delegate cache is permanent by
    /// design — kept the watcher asking, and logging, every five seconds forever.
    /// </summary>
    private static void Hold(CoreWatchPolicy.FileStamp stamp, string? reason, double now)
    {
        if (_haveHeldStamp && stamp.Equals(_heldStamp))
        {
            _heldRetries++;
        }
        else
        {
            _heldStamp = stamp;
            _haveHeldStamp = true;
            _heldRetries = 0;
            _lastTransientReason = null;
        }

        if (CoreWatchPolicy.ExhaustedTransientRetries(_heldRetries))
        {
            int retries = _heldRetries;
            // Park clears the hold, so read the count first. The park is keyed to the same
            // stamp, so a re-staged image gets a fresh minute of retries.
            Park(stamp, reason);
            ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatTransientGaveUp(retries, reason));
            return;
        }

        _heldRetryAt = CoreWatchPolicy.NextRetryAt(now);
        if (CoreWatchPolicy.ShouldLogRefusal(_lastTransientReason, reason))
        {
            _lastTransientReason = reason;
            ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatTransientRefusal(reason));
        }
    }

    private static void ClearHold()
    {
        _haveHeldStamp = false;
        _heldRetryAt = 0;
        _heldRetries = 0;
        _lastTransientReason = null;
    }

    /// <summary>
    /// R68 finding 6: the dump's single <c>watch.parked</c> covers both parks — the staged
    /// <c>.next</c> and the Core-less live <c>Progenitor.dll</c>. The Core-less park is the
    /// state an operator most needs named (no Core, recovery disarmed), and it was invisible
    /// because the staged path wrote <c>false</c> earlier in the same tick.
    /// </summary>
    private static void PublishParked()
    {
        CoreReloadStatus.SetWatchParked(
            CoreWatchPolicy.ParkedForDump(_parkedStagedActive, _parkedLiveActive),
            CoreWatchPolicy.ParkReasonForDump(_parkedStagedActive, _parkedReason, _parkedLiveReason));
    }

    /// <summary>
    /// The Core-less twin of <see cref="Park"/>: stop asking the recovery path to Install the
    /// live <c>Progenitor.dll</c> until its bytes change. Reached by the one permanent refusal
    /// and, since job FREL10, by a transient one that has answered the same way for
    /// <see cref="CoreWatchPolicy.TransientRetryLimit"/> retries. <c>TickCore</c> publishes the
    /// park to the status dump when this returns (R68 finding 6).
    /// </summary>
    private static void ParkLive(CoreWatchPolicy.FileStamp stamp, string? reason)
    {
        _parkedLiveStamp = stamp;
        _haveParkedLiveStamp = true;
        _parkedLiveReason = reason;
        _parkedLiveActive = true;
        _haveHeldLiveStamp = false;
        _heldLiveRetries = 0;
        _lastTransientLiveReason = null;
    }

    private static void UnparkLive()
    {
        _haveParkedLiveStamp = false;
        _parkedLiveReason = null;
        _parkedLiveActive = false;
    }

    /// <summary>
    /// A failed <c>ReadAllBytes</c> / rotate of a still-present <c>.next</c>.
    /// Leaves the stamp so the next settled scan retries, up to
    /// <see cref="CoreWatchPolicy.PromoteRetryLimit"/>.
    /// </summary>
    internal static void NotePromoteFailed(string? error)
    {
        if (!HasPendingNext())
        {
            _promoteFailures = 0;
            return;
        }

        _promoteFailures++;
        if (CoreWatchPolicy.ExhaustedPromoteRetries(_promoteFailures))
        {
            if (_haveSeenStamp)
            {
                _lastRequestedStamp = _lastSeenStamp;
                _haveRequestedStamp = true;
            }
            ProcessHost.Log?.LogWarning(CoreWatchPolicy.FormatPromoteRetryExhausted(error));
            return;
        }

        _haveRequestedStamp = false;
        ProcessHost.Log?.LogWarning(
            CoreWatchPolicy.FormatPromoteRetry(_promoteFailures, CoreWatchPolicy.PromoteRetryLimit, error));
    }

    /// <summary>
    /// Hosting flag dropped (child gone or dedicated join). Reset the once-per-process
    /// refuse line so a later host is logged, and emit the resume line.
    /// </summary>
    internal static void NoteHostingCleared(string? reason)
    {
        _loggedHosting = false;
        ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatWatchResumed(reason));
    }

    /// <summary>
    /// Stamp the live <c>Progenitor.dll</c> as already tried so a Core-less watch
    /// does not RequestReload the same protocol-mismatch image every scan.
    /// </summary>
    internal static void NoteLiveImageTried()
    {
        try
        {
            string live = CoreLayout.DllPath(Paths.BepInExRootPath);
            if (!File.Exists(live))
            {
                _haveTriedLiveStamp = false;
                _haveSeenLiveStamp = false;
                return;
            }
            var info = new FileInfo(live);
            var stamp = new CoreWatchPolicy.FileStamp(info.Length, info.LastWriteTimeUtc);
            _lastTriedLiveStamp = stamp;
            _haveTriedLiveStamp = true;
            _lastSeenLiveStamp = stamp;
            _haveSeenLiveStamp = true;
        }
        catch
        {
            /* watch tick will retry */
        }
    }

    private static void TickCore()
    {
        double now = Clock.Elapsed.TotalSeconds;
        if (now < _nextScan) return;
        _nextScan = now + CoreWatchPolicy.ScanIntervalSeconds;

        try { ProcessHost.RefreshHostingPrivateGame(); }
        catch { /* watch tick must not throw */ }

        bool enabled = ProcessHost.WatchCore;
        bool hosted = ProcessHost.IsHostedChild;
        bool hosting = ProcessHost.IsHostingPrivateGame;
        bool busy = CoreLoader.IsReloading;
        if (!busy) _loggedInFlight = false;
        CoreReloadStatus.SetWatch(
            enabled, HasPendingNext(), hosted, hosting, busy, CoreLoader.PreviousGenerationAlive);

        string nextPath;
        try
        {
            nextPath = CoreWatchPolicy.NextPath(CoreLayout.DllPath(Paths.BepInExRootPath));
        }
        catch
        {
            return;
        }

        bool exists = File.Exists(nextPath);
        CoreWatchPolicy.FileStamp stamp = default;
        bool settled = false;
        bool same = false;
        if (exists)
        {
            long length = 0;
            DateTime write = default;
            try
            {
                var info = new FileInfo(nextPath);
                length = info.Length;
                write = info.LastWriteTimeUtc;
            }
            catch
            {
                exists = false;
            }
            if (exists)
            {
                stamp = new CoreWatchPolicy.FileStamp(length, write);
                settled = CoreWatchPolicy.IsSettled(_haveSeenStamp ? _lastSeenStamp : null, stamp);
                if (_haveSeenStamp && !stamp.Equals(_lastSeenStamp))
                    _promoteFailures = 0;
                _lastSeenStamp = stamp;
                _haveSeenStamp = true;
                same = _haveRequestedStamp && stamp.Equals(_lastRequestedStamp);
            }
        }
        else
        {
            _haveSeenStamp = false;
            _promoteFailures = 0;
        }

        // Job FREL8: an image a previous tick had refused is skipped here, before
        // ShouldRequestReload reads and hashes it. Only a change of (length, write time)
        // — or the file going away — unparks; a restart promotes the staged file without
        // the watcher touching it. Folded into `same` so Decide returns Skip and the tick
        // writes nothing at all.
        bool parked = CoreWatchPolicy.ParkedOnStamp(
            _haveParkedStamp ? _parkedStamp : null, exists, stamp);
        if (!parked && _haveParkedStamp) Unpark();
        _parkedStagedActive = parked;

        // Job FREL9: a transient refusal holds the image for TransientRetrySeconds instead of
        // parking it. The hold is dropped as soon as the bytes change, so a fresh deploy is
        // never made to wait for it.
        if (_haveHeldStamp && !(exists && stamp.Equals(_heldStamp))) ClearHold();

        // Job FREL10: the read + SHA-256 + allowlist + identical-to-live comparison is kept
        // only while the bytes it was taken from are still the bytes on disk. Any change of
        // (length, write time), or the file going away, throws it away and the next Request
        // inspects for real.
        if (_haveInspectedStamp && !(exists && stamp.Equals(_inspectedStamp)))
            _haveInspectedStamp = false;
        bool held = CoreWatchPolicy.HeldOnStamp(
            _haveHeldStamp ? _heldStamp : null, exists, stamp, now, _heldRetryAt);
        PublishParked();
        same |= parked || held;

        var decision = CoreWatchPolicy.Decide(
            enabled, hosted, busy, exists, settled, same, hostingPrivateGame: hosting);
        switch (decision)
        {
            case CoreWatchDecision.Request:
                // Job FREL10 (R69 finding 6): a retry of a held image re-reads nothing. The
                // first attempt on these bytes read them, hashed them, checked the allowlist
                // and compared them with the live dll; the stamp says they have not moved, so
                // the retry is the RequestReload alone — no 2 MB reads, no hash, and neither
                // the staged-image line nor the settled line a second time.
                if (!CoreWatchPolicy.MayReuseInspection(
                        _haveInspectedStamp ? _inspectedStamp : null, exists, stamp))
                {
                    if (!ShouldRequestReload(nextPath, stamp))
                        break;
                    _inspectedStamp = stamp;
                    _haveInspectedStamp = true;
                    ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatSettled());
                }
                string result = CoreLoader.RequestReload(CoreWatchPolicy.WatchReason);
                if (CoreWatchPolicy.RequestAccepted(result))
                {
                    // Stamp waits for NotePromoteSucceeded. A failed rotate of
                    // this same (length, mtime) must retry — and must inspect again,
                    // because promote rotates these bytes onto the live dll.
                    _haveInspectedStamp = false;
                    Unpark();
                    ClearHold();
                }
                else if (CoreWatchPolicy.ParkOnRefusal(result))
                {
                    // Park on these bytes: the next tick skips them without reading.
                    // The park is what makes this line once per image, so it is not also
                    // deduped by reason — a *new* staged file refused for the same reason
                    // is a second thing the operator did and gets its own line.
                    Park(stamp, result);
                    ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatRefusedRequest(result));
                }
                else
                {
                    // Transient (plugin ALC still collecting, a swap in flight, a throw):
                    // back off and try again, one line per distinct reason.
                    Hold(stamp, result, now);
                }
                break;
            case CoreWatchDecision.RefuseHosted:
                if (!_loggedHosted)
                {
                    ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatRefusedHosted());
                    _loggedHosted = true;
                }
                break;
            case CoreWatchDecision.RefuseHosting:
                if (!_loggedHosting)
                {
                    ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatRefusedHosting());
                    _loggedHosting = true;
                }
                break;
            case CoreWatchDecision.RefuseInFlight:
                if (!_loggedInFlight)
                {
                    ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatRefusedInFlight());
                    _loggedInFlight = true;
                }
                break;
            case CoreWatchDecision.Disabled:
                if (exists && settled && !_loggedDisabled)
                {
                    ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatDisabled());
                    _loggedDisabled = true;
                }
                break;
        }

        // R68 finding 1: `busy` above was read before the switch, which may have just
        // scheduled a cascade. The Core-less path reads IsReloading itself, after that.
        TryRequestCoreLessLiveInstall(enabled, hosted, hosting, now);
        PublishParked();
    }

    /// <summary>
    /// Core-less recovery: a changed live <c>Progenitor.dll</c> (deploy copied
    /// over the file without a <c>.next</c>) must Install directly. Dev cannot
    /// ask for <c>core_reload</c> until Core loads the bridge.
    /// </summary>
    private static void TryRequestCoreLessLiveInstall(bool enabled, bool hosted, bool hosting, double now)
    {
        // The reloading flag is read here, never handed in: the staged arm of this same tick
        // may have scheduled a swap a few lines ago, and a stale `false` makes this path ask
        // again, collect RefuseAlreadyReloading and hold the recovery image (R68 finding 1).
        if (!CoreWatchPolicy.MayTryCoreLessLiveInstall(
                enabled, hosted, hosting, CoreLoader.IsReloading))
            return;
        if (CoreLoader.Generation >= CoreReloadPolicy.FirstGeneration) return;
        if (CoreLoader.Assembly != null) return;

        string livePath;
        try { livePath = CoreLayout.DllPath(Paths.BepInExRootPath); }
        catch { return; }
        if (!File.Exists(livePath)) return;

        CoreWatchPolicy.FileStamp stamp;
        try
        {
            var info = new FileInfo(livePath);
            stamp = new CoreWatchPolicy.FileStamp(info.Length, info.LastWriteTimeUtc);
        }
        catch
        {
            return;
        }

        bool settled = CoreWatchPolicy.IsSettled(_haveSeenLiveStamp ? _lastSeenLiveStamp : null, stamp);
        _lastSeenLiveStamp = stamp;
        _haveSeenLiveStamp = true;

        // Job FREL8: same park as the staged path, and only for the same permanent refusal.
        // A Core-less process whose live image is refused for good would otherwise ask once a
        // second forever and CoreLoader would log its own refusal line each time. Job FREL9:
        // a transient refusal is held with a backoff instead, and the park is published to
        // the status dump (R68 finding 6) — without it the dump says "not parked" for a
        // watcher that will never look at Progenitor.dll again.
        bool parked = CoreWatchPolicy.ParkedOnStamp(
            _haveParkedLiveStamp ? _parkedLiveStamp : null, fileExists: true, stamp);
        if (!parked) UnparkLive();
        else _parkedLiveActive = true;
        if (parked) return;

        if (_haveHeldLiveStamp && !stamp.Equals(_heldLiveStamp))
        {
            _haveHeldLiveStamp = false;
            _heldLiveRetries = 0;
            _lastTransientLiveReason = null;
        }
        if (CoreWatchPolicy.HeldOnStamp(
                _haveHeldLiveStamp ? _heldLiveStamp : null, fileExists: true, stamp, now, _heldLiveRetryAt))
            return;

        bool same = _haveTriedLiveStamp && stamp.Equals(_lastTriedLiveStamp);
        if (!CoreReloadPolicy.ShouldRequestCoreLessLiveChange(
                coreLoaded: false, liveExists: true, liveSettled: settled, sameStampAsLastTry: same))
            return;

        string result = CoreLoader.RequestReload(CoreWatchPolicy.WatchReason);
        if (CoreWatchPolicy.RequestAccepted(result))
        {
            UnparkLive();
            _haveHeldLiveStamp = false;
            _heldLiveRetries = 0;
            _lastTransientLiveReason = null;
        }
        else if (CoreWatchPolicy.ParkOnRefusal(result))
        {
            ParkLive(stamp, result);
            ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatRefusedRequest(result));
        }
        else
        {
            if (_haveHeldLiveStamp) _heldLiveRetries++;
            else
            {
                _haveHeldLiveStamp = true;
                _heldLiveRetries = 0;
                _lastTransientLiveReason = null;
            }
            _heldLiveStamp = stamp;

            // Job FREL10 (R69 finding 6): the same bound as the staged path. A Core-less
            // process whose recovery Install is refused transiently for a minute is not
            // recovering, and asking every five seconds for the life of the process only
            // buries the reason it will not.
            if (CoreWatchPolicy.ExhaustedTransientRetries(_heldLiveRetries))
            {
                int retries = _heldLiveRetries;
                ParkLive(stamp, result);
                ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatTransientGaveUp(retries, result));
                return;
            }

            _heldLiveRetryAt = CoreWatchPolicy.NextRetryAt(now);
            if (CoreWatchPolicy.ShouldLogRefusal(_lastTransientLiveReason, result))
            {
                _lastTransientLiveReason = result;
                ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatTransientRefusal(result));
            }
        }
    }

    /// <summary>
    /// False when the settled <c>.next</c> must not schedule a swap: invalid
    /// (quarantine, no stamp) or byte-identical to the live dll (one skip line,
    /// stamp so the line is once). A read miss retries the next scan.
    /// </summary>
    private static bool ShouldRequestReload(string nextPath, CoreWatchPolicy.FileStamp stamp)
    {
        if (!TryReadAllBytes(nextPath, out byte[] nextBytes))
            return false;

        // Job FRLD: name the staged bytes before anything decides to promote them, and
        // refuse an image the operator's allowlist does not carry. Quarantining here (the
        // same rename an invalid PE gets) keeps an unlisted .next from being retried every
        // scan and never touches the live dll.
        string nextName = Path.GetFileName(nextPath);
        string nextHash = ReloadAllowPolicy.Sha256Hex(nextBytes);
        long nextSize = nextBytes.LongLength;
        var nextAllow = ReloadAllowStore.Decide(nextName, nextHash);
        ProcessHost.Log?.LogInfo(
            ReloadAllowPolicy.FormatStagedCore(nextName, nextHash, nextSize, nextAllow));
        if (!ReloadAllowPolicy.MayLoad(nextAllow))
        {
            ProcessHost.Log?.LogError(
                ReloadAllowPolicy.FormatRefusedCore(nextName, nextHash, nextSize, nextAllow));
            CoreLoader.QuarantineNextFile(
                nextPath,
                "sha256 " + ReloadAllowPolicy.Short(nextHash) + " allowlist=" + ReloadAllowPolicy.Token(nextAllow));
            _lastRequestedStamp = stamp;
            _haveRequestedStamp = true;
            return false;
        }

        byte[]? liveBytes = null;
        try
        {
            string live = CoreLayout.DllPath(Paths.BepInExRootPath);
            if (File.Exists(live))
                liveBytes = File.ReadAllBytes(live);
        }
        catch
        {
            liveBytes = null;
        }

        bool identical = CoreWatchPolicy.BytesIdentical(nextBytes, liveBytes);
        string inspectError = "";
        // Always validate: InspectStaged ranks invalid over identical, so a truncated
        // .next that happens to match a truncated live dll still quarantines (R22 #11).
        bool valid = CoreLoader.TryValidateCoreImage(nextBytes, out inspectError);
        var inspect = CoreWatchPolicy.InspectStaged(valid, identical);
        if (CoreWatchPolicy.MayRequestAfterInspect(inspect))
            return true;

        if (inspect == CoreWatchInspectDecision.Quarantine)
        {
            if (string.IsNullOrEmpty(inspectError))
                inspectError = "not a PE image or truncated";
            CoreLoader.QuarantineNextFile(nextPath, inspectError);
        }
        else if (inspect == CoreWatchInspectDecision.SkipIdentical)
        {
            ProcessHost.Log?.LogInfo(CoreWatchPolicy.FormatSkippedIdentical());
            try { File.Delete(nextPath); }
            catch { /* stamp below so a stuck file is not logged every scan */ }
        }

        if (CoreWatchPolicy.StampAfterInspect(inspect))
        {
            _lastRequestedStamp = stamp;
            _haveRequestedStamp = true;
        }
        return false;
    }

    private static bool TryReadAllBytes(string path, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
