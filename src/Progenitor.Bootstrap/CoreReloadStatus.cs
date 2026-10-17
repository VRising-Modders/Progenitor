using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Bootstrap;

/// <summary>
/// Last Core-swap snapshot for the bridge <c>core_reload {"status":true}</c> /
/// <c>reload_status</c> payload and for ping-style dumps. Process-lifetime; not a
/// collectible-Core type.
/// </summary>
public static class CoreReloadStatus
{
    public const string OutcomeNone = "none";
    public const string OutcomeScheduled = "scheduled";
    public const string OutcomeOk = "ok";
    public const string OutcomeRefused = "refused";
    public const string OutcomeFailed = "failed";
    /// <summary>
    /// N+1 Install failed and generation N was restored into a fresh ALC. Nothing
    /// of N is still alive; the next swap does not need a process restart.
    /// </summary>
    public const string OutcomeRestored = "restored";
    public const int PinDiagCap = 16;

    private static readonly object Gate = new();
    private static readonly List<string> PinLines = new();
    private static int _generation;
    private static string _outcome = OutcomeNone;
    private static string _reason = "";
    private static DateTime _timestampUtc;
    private static string? _alcName;
    private static bool _inFlight;
    private static bool _watchEnabled = true;
    private static bool _watchPending;
    private static bool _hostedChild;
    private static bool _hostingPrivateGame;
    private static bool _previousAlive;
    private static bool _watchParked;
    private static string _watchParkedReason = "";
    private static string[] _processLifetimeRows = Array.Empty<string>();
    private static string[] _waitingRows = Array.Empty<string>();

    /// <summary>Job FORD1: reload-folder DLLs waiting for a dependency (<c>ReloaderHost.WaitingRows</c>).</summary>
    public static void SetWaitingPlugins(string[]? rows)
    {
        lock (Gate) _waitingRows = rows ?? Array.Empty<string>();
    }

    private static object[] WaitingDump()
    {
        var list = new List<object>();
        foreach (var text in _waitingRows)
        {
            var parts = (text ?? "").Split('\t');
            if (parts.Length < 4 || parts[0].Length == 0) continue;
            list.Add(new
            {
                file = parts[0],
                guids = parts[1].Length == 0 ? Array.Empty<string>() : parts[1].Split('|'),
                waitingFor = parts[2],
                reason = parts[3],
            });
        }
        return list.ToArray();
    }

    /// <summary>
    /// Job FINJ1: the reloader's process-lifetime plugins (<c>ReloaderHost.ProcessLifetimeRows</c>), set by
    /// <c>CoreLoader.DescribeStatus</c> just before the dump so the answer is current.
    /// </summary>
    public static void SetProcessLifetimePlugins(string[]? rows)
    {
        lock (Gate) _processLifetimeRows = rows ?? Array.Empty<string>();
    }

    /// <summary>The rows above as plain objects for the JSON dump; unparseable rows are skipped.</summary>
    private static object[] ProcessLifetimeDump()
    {
        var list = new List<object>();
        foreach (var text in _processLifetimeRows)
        {
            if (!Il2CppInjectionPolicy.TryParseRow(text, out var row)) continue;
            list.Add(new
            {
                file = row.File,
                guids = row.Guids,
                names = row.Names,
                coreBound = row.CoreBound,
                restartPending = row.Pending,
                sites = row.Sites,
            });
        }
        return list.ToArray();
    }

    public static void Record(int generation, string outcome, string reason, string? alcName, bool inFlight)
    {
        lock (Gate)
        {
            _generation = generation;
            _outcome = string.IsNullOrEmpty(outcome) ? OutcomeNone : outcome;
            _reason = reason ?? "";
            _timestampUtc = DateTime.UtcNow;
            _alcName = alcName;
            _inFlight = inFlight;
        }
    }

    public static void Record(CoreReloadResult result, string reason)
    {
        string outcome = string.IsNullOrEmpty(result.Outcome)
            ? (result.Ok ? OutcomeOk : result.Refused ? OutcomeRefused : OutcomeFailed)
            : result.Outcome;
        // Failed-install restore: the operator-facing summary is the reason the
        // bridge must show (evidence 70). Other paths keep the request reason.
        string recorded = string.Equals(outcome, OutcomeRestored, StringComparison.Ordinal)
            ? result.Summary
            : (string.IsNullOrEmpty(reason) ? result.Summary : reason);
        Record(result.Generation, outcome, recorded, alcName: null, inFlight: false);
    }

    public static void SetWatch(bool enabled, bool pending, bool hostedChild, bool inFlight, bool previousAlive) =>
        SetWatch(enabled, pending, hostedChild, hostingPrivateGame: false, inFlight, previousAlive);

    public static void SetWatch(
        bool enabled,
        bool pending,
        bool hostedChild,
        bool hostingPrivateGame,
        bool inFlight,
        bool previousAlive)
    {
        lock (Gate)
        {
            _watchEnabled = enabled;
            _watchPending = pending;
            _hostedChild = hostedChild;
            _hostingPrivateGame = hostingPrivateGame;
            _inFlight = inFlight;
            _previousAlive = previousAlive;
        }
    }

    /// <summary>
    /// Job FREL8: the watcher has parked on an image whose swap was refused permanently
    /// (generation N still alive) and will not look at it again until its bytes change —
    /// the staged <c>.next</c>, or, on a Core-less process, the live <c>Progenitor.dll</c>
    /// the recovery path would have installed (job FREL9, R68 finding 6) — or an image whose
    /// <em>transient</em> refusal answered the same way for
    /// <see cref="CoreWatchPolicy.TransientRetryLimit"/> retries and so is no longer being
    /// treated as one (job FREL10, R69 finding 6). A transient refusal inside its retry budget
    /// is retried with a backoff and is not reported here. Separate from
    /// <see cref="SetWatch(bool, bool, bool, bool, bool, bool)"/> because the status dump
    /// is also built from <c>CoreLoader.DescribeStatus</c>, which knows the watch flags
    /// but not the park; a reader (<c>tools/deploy.ps1</c>) needs
    /// <c>watch.pendingNext=true</c> plus <c>watch.parked=true</c> to tell "the watcher
    /// is about to promote this" from "only a restart will".
    /// </summary>
    public static void SetWatchParked(bool parked, string? reason)
    {
        lock (Gate)
        {
            _watchParked = parked;
            _watchParkedReason = parked ? (reason ?? "") : "";
        }
    }

    public static void ResetPinDiag()
    {
        lock (Gate) PinLines.Clear();
    }

    public static void AppendPin(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (Gate)
        {
            if (PinLines.Count >= PinDiagCap)
                PinLines.RemoveAt(0);
            PinLines.Add(line);
        }
    }

    public static string PinDiagSummary()
    {
        lock (Gate) return JoinPins();
    }

    public static string FormatLastReload()
    {
        lock (Gate) return CoreReloadPolicy.FormatLastReload(_generation, _outcome, _reason, _timestampUtc);
    }

    /// <summary>Anonymous-type shaped dump the Dev bridge serializes as JSON.</summary>
    public static object ToDump(int liveGeneration, string? liveAlcName, int protocol) =>
        ToDump(liveGeneration, liveAlcName, bootstrapProtocol: protocol, coreProtocol: protocol);

    /// <summary>
    /// Same as <see cref="ToDump(int, string?, int)"/> with both handshake numbers.
    /// <paramref name="protocol"/> stays Bootstrap's constant for older readers;
    /// <c>coreProtocol</c> is the reflected Core field (0 if Core is not loaded).
    /// </summary>
    public static object ToDump(int liveGeneration, string? liveAlcName, int bootstrapProtocol, int coreProtocol)
    {
        lock (Gate)
        {
            return new
            {
                generation = liveGeneration,
                alcName = liveAlcName ?? _alcName,
                protocol = bootstrapProtocol,
                bootstrapProtocol,
                coreProtocol,
                inFlight = _inFlight,
                previousAlive = _previousAlive,
                watch = new
                {
                    enabled = _watchEnabled,
                    hostedChild = _hostedChild,
                    hostingPrivateGame = _hostingPrivateGame,
                    pendingNext = _watchPending,
                    parked = _watchParked,
                    parkedReason = _watchParkedReason,
                },
                lastReload = new
                {
                    gen = _generation,
                    outcome = _outcome,
                    reason = _reason,
                    timestamp = _timestampUtc == default
                        ? ""
                        : _timestampUtc.ToString("o", CultureInfo.InvariantCulture),
                },
                // Single-folder install: where this Bootstrap resolved Core at start (InstallLayout). Absent on a
                // Bootstrap built before it, which is always the three-location layout. tools/deploy.ps1 reads it so
                // a deploy never stages a Core.next the running watcher does not look at.
                layout = LayoutDump(),
                pinDiag = JoinPins(),
                // Job FINJ1: reload-folder plugins that registered their own IL2CPP types. They are never
                // unloaded in place; restartPending names a change only a restart picks up, and a
                // coreBound one makes every Core swap refuse until the process restarts.
                processLifetimePlugins = ProcessLifetimeDump(),
                // Job FORD1: reload-folder DLLs not loaded because a dependency is missing or did not load.
                waitingPlugins = WaitingDump(),
            };
        }
    }

    private static object LayoutDump()
    {
        var home = Progenitor.SelfUpdate.InstallLayout.Current;
        return new
        {
            coreFile = home?.CoreFile ?? "",
            coreFolder = home?.Folder ?? "",
            legacy = home?.Legacy ?? false,
            found = home?.Found ?? false,
        };
    }

    private static string JoinPins()
    {
        if (PinLines.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < PinLines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(PinLines[i]);
        }
        return sb.ToString();
    }
}
