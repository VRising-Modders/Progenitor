using System;
using System.IO;
#if !PROGENITOR_TESTS
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using ProjectM;
using ProjectM.Shared;
using Progenitor.Config;
#endif

namespace Progenitor.Hosted;

/// <summary>
/// How the bundled child's <c>BepInEx/plugins</c> is filled. <c>progenitor-core</c> is a real
/// copy in both modes: the child's Bootstrap promote of <c>Progenitor.dll.next</c> must not
/// <c>File.Move</c> the running client's Core through a junction (R11 finding 2).
/// <see cref="Shared"/> junctions the client's plugins (and the other safe BepInEx loader
/// folders) so client mods also run in the child. <see cref="ProgenitorOnly"/> writes a real
/// plugins folder containing only <c>Progenitor.Bootstrap.dll</c> so a client-only plugin
/// cannot crash the hosted server. Either mode refuses or deletes a leftover
/// <c>plugins/Progenitor.dll</c> — the chainloader would load it before Bootstrap.
/// </summary>
public enum HostedServerPluginsMode
{
    /// <summary>
    /// Junction <c>BepInEx/plugins</c> at the client's plugins (default). <c>progenitor-core</c>
    /// is still a real copy, not a junction.
    /// </summary>
    Shared = 0,

    /// <summary>
    /// Real <c>BepInEx/plugins</c> containing only <c>Progenitor.Bootstrap.dll</c>, and a real
    /// <c>BepInEx/progenitor-core</c> containing <c>Progenitor.dll</c>. Client-only mods are
    /// not loaded by the child.
    /// </summary>
    ProgenitorOnly = 1,
}

/// <summary>
/// What the <c>StunProcess.Start</c> prefix should do after path and staging checks.
/// Game-free so the leftover-Core fail-closed path is unit-testable (R11 finding 8).
/// Harmony cannot cancel Start from a void prefix; a <c>bool</c> prefix that returns
/// false skips the original, which is the only way to refuse the spawn.
/// </summary>
public enum HostedStartAction
{
    /// <summary>Not our exe, launcher disabled, or unsafe path: run the original Start.</summary>
    PassThrough = 0,

    /// <summary>Staging succeeded: strip inherited <c>DOORSTOP_*</c> and run the original.</summary>
    InjectAndStripDoorstop = 1,

    /// <summary>
    /// Leftover <c>plugins/Progenitor.dll</c> could not be deleted, or a leftover hosted
    /// child still has <c>progenitor-core/Progenitor.dll</c> mapped. Skip the original Start.
    /// </summary>
    RefuseSpawn = 2,
}

/// <summary>
/// Where hosted deferred-Uninstall completion hooks attach. Game-free so the
/// ConvertDelegate-vs-Bootstrap-slot decision is unit-testable (R11 finding 4).
/// <c>DelegateSupport.ConvertDelegate</c> caches an IL2CPP delegate for process lifetime
/// and would pin a collectible Core ALC; Bootstrap already ConvertDelegates
/// <c>Application.quitting</c> in the default ALC and invokes
/// <c>ProcessHost.LifetimeQuitting</c> / <c>LifetimeFrame</c>.
/// </summary>
public enum HostedCompletionHookRoute
{
    /// <summary>
    /// Bootstrap owns the <c>Application.quitting</c> ConvertDelegate (default ALC) and
    /// invokes <c>ProcessHost.LifetimeQuitting</c> / <c>LifetimeFrame</c>. Core registers
    /// ordinary <see cref="Action"/>s on those slots and must not ConvertDelegate.
    /// </summary>
    BootstrapSlot = 0,

    /// <summary>
    /// Bootstrap is absent. Core ConvertDelegates <c>Application.quitting</c> and
    /// subscribes to <c>Plugin.Frame</c> — today's behavior, which pins this Core ALC.
    /// The fallback is logged.
    /// </summary>
    CoreConvertDelegateFallback = 1,
}

/// <summary>
/// Client-side hook that stages BepInEx into the bundled server folder before the game starts
/// <c>VRisingServer.exe</c> for a private or single-player session. The child process otherwise
/// has no loader, so hosted play would not load the same plugins as a dedicated server. Call
/// <c>Install</c> from Core on the client; a server process is a no-op. Wired from
/// <c>Plugin.Load</c>. Staging and spawn were exercised live (evidence §§21, 24, 28); §24 showed
/// the previous doorstop <c>target_assembly</c> pointed at the client's core, which this module
/// now writes as <c>{serverDir}\BepInEx\core\BepInEx.Unity.IL2CPP.dll</c>. §28: the child still
/// did not load Doorstop because it inherited the client's <c>DOORSTOP_INITIALIZED</c> (and the
/// rest of <c>DOORSTOP_*</c>); the Start prefix strips those for the spawn and the Harmony
/// finalizer restores them. Nested Starts share one snapshot (see <see cref="DoorstopStripNest"/>).
/// The 30 s child-log probe counts as injected only when <c>LogOutput.log</c> was written at or
/// after this spawn, so a leftover file from a previous child is not a false success. A leftover
/// bundled child still alive (executable path under the game's <c>VRising_Server</c>, never
/// <c>C:\VRisingServer</c>, regardless of argv) skips the log rename; an open-file rename
/// failure is treated as leftover-alive. The probe reports
/// <c>inconclusive: leftover child alive</c> rather than present.
/// Deferred Uninstall completion hooks never <c>ConvertDelegate</c> a Core method when
/// Bootstrap owns process lifetime: they register ordinary Actions on
/// <c>ProcessHost.LifetimeQuitting</c> / <c>LifetimeFrame</c> (R11 finding 4). Bootstrap
/// absent falls back to ConvertDelegate + <c>Plugin.Frame</c> and logs that this pins the
/// Core ALC.
/// </summary>
public static class HostedServerLauncher
{
    /// <summary>
    /// Nest counter for stripping <c>DOORSTOP_*</c> around <c>StunProcess.Start</c>. Nested
    /// Starts (base + override, or a Start that itself starts another process) share one
    /// snapshot; only the outermost <see cref="Leave"/> restores. A Start whose prefix did not
    /// <see cref="Enter"/> must not <see cref="Leave"/> — Harmony <c>__state</c> carries that
    /// bit, and only the finalizer Leaves (the postfix is not patched, so a postfix+finalizer
    /// pair cannot decrement twice). Extra Leaves after <see cref="Depth"/> is 0 are no-ops.
    /// </summary>
    public sealed class DoorstopStripNest
    {
        /// <summary>Current nesting depth. 0 means no hosted Start is in flight.</summary>
        public int Depth { get; private set; }

        /// <summary>
        /// Prefix of a hosted Start. Returns true when this is the outermost call (the caller
        /// should snapshot and clear the environment). Nested calls share that snapshot.
        /// </summary>
        public bool Enter()
        {
            Depth++;
            return Depth == 1;
        }

        /// <summary>
        /// Finalizer of a Start that <see cref="Enter"/>ed. Returns true when this is the
        /// outermost call (the caller should restore the snapshot). A Leave with no matching
        /// Enter returns false so a second decrement cannot restore while an outer Start is
        /// still inside <c>CreateProcessW</c>.
        /// </summary>
        public bool Leave()
        {
            if (Depth <= 0) return false;
            Depth--;
            return Depth == 0;
        }

        /// <summary>
        /// Uninstall: forget in-flight Starts so a later Leave cannot restore twice after the
        /// snapshot has already been written back.
        /// </summary>
        public void Reset() => Depth = 0;

        /// <summary>
        /// True while a Start has <see cref="Enter"/>ed and its finalizer has not yet
        /// <see cref="Leave"/>d. Uninstall must not Reset or restore in this window: the
        /// child's <c>CreateProcessW</c> may still be copying the stripped environment.
        /// </summary>
        public bool HasInFlightStart => Depth > 0;
    }

    /// <summary>
    /// Game-free rules for Uninstall-during-Start (R8 regression B, row 6). Harmony
    /// unpatch must never run on the <c>StunProcess.Start</c> finalizer stack. The
    /// finalizer only marks idle so a later frame tick, <c>Application.quitting</c>, or
    /// the 0.5 s retry can complete Uninstall. <c>Install</c> while Uninstall is deferred
    /// must not no-op: it queues a re-Install that runs after the deferred Uninstall
    /// unpatches.
    /// </summary>
    public static class UninstallDeferral
    {
        /// <summary>
        /// Backstop delay when Uninstall is refused because a Start is between snapshot
        /// and restore. The Start finalizer may never run (prefix threw before Enter);
        /// this retry still completes Uninstall once Depth is 0.
        /// </summary>
        public const double RetrySeconds = 0.5;

        /// <summary>
        /// Kept so existing call sites compile. Always false: canceling the deferred
        /// Uninstall would leave a reload that wanted a clean re-Install sitting on the
        /// old patches, and is the no-op R7 finding 6 named. Queue instead
        /// (<see cref="QueueInstallAfterDeferredUninstall"/>).
        /// </summary>
        public static bool CancelDeferredUninstallOnInstall(bool installed, bool uninstallRequested)
        {
            _ = installed;
            _ = uninstallRequested;
            return false;
        }

        /// <summary>
        /// Harmony unpatch must not run inside <c>StunProcess*.Start</c>'s finalizer
        /// (the method being unpatched). Always false; the finalizer only requests a
        /// later completion via <see cref="RequestIdleCompletionFromFinalizer"/>.
        /// </summary>
        public static bool RunDeferredUninstallFromFinalizer(bool uninstallRequested, bool hasInFlightStart)
        {
            _ = uninstallRequested;
            _ = hasInFlightStart;
            return false;
        }

        /// <summary>
        /// True when the Start finalizer should schedule a later Uninstall (Depth is 0
        /// and Uninstall is waiting). Does not mean the finalizer may unpatch.
        /// </summary>
        public static bool RequestIdleCompletionFromFinalizer(bool uninstallRequested, bool hasInFlightStart)
            => uninstallRequested && !hasInFlightStart;

        /// <summary>
        /// Frame tick, <c>Application.quitting</c>, or the 0.5 s retry may unpatch once
        /// no Start is in flight. Re-check Depth under the same lock as Uninstall.
        /// </summary>
        public static bool ShouldCompleteDeferredUninstall(bool uninstallRequested, bool hasInFlightStart)
            => uninstallRequested && !hasInFlightStart;

        /// <summary>
        /// <c>Install</c> while patches are still on and Uninstall is deferred: do not
        /// no-op and do not cancel the Uninstall. Queue a re-Install to run after the
        /// deferred Uninstall completes.
        /// </summary>
        public static bool QueueInstallAfterDeferredUninstall(bool installed, bool uninstallRequested)
            => installed && uninstallRequested;

        /// <summary>
        /// After deferred Uninstall has unpatched, run the queued Install iff one was
        /// requested and we are no longer installed.
        /// </summary>
        public static bool ShouldRunQueuedInstall(bool queuedInstall, bool nowInstalled)
            => queuedInstall && !nowInstalled;
    }

    /// <summary>
    /// Game-free routing for deferred-Uninstall completion (R11 finding 4 / CC open
    /// question 2). When Bootstrap owns process lifetime, Hosted must not
    /// <c>ConvertDelegate</c> a Core method onto <c>Application.quitting</c> (that cache
    /// pins the collectible Core ALC). It registers ordinary Actions on Bootstrap's
    /// lifetime slots instead. When Bootstrap is absent, today's ConvertDelegate +
    /// <c>Plugin.Frame</c> path remains and is logged.
    /// </summary>
    public static class CompletionHookRouting
    {
        /// <summary>
        /// Logged when <see cref="Decide"/> returns
        /// <see cref="HostedCompletionHookRoute.CoreConvertDelegateFallback"/>. The
        /// ConvertDelegate cache pins this Core ALC until process exit.
        /// </summary>
        public const string FallbackLog =
            "Bootstrap absent; falling back to Application.quitting ConvertDelegate and Plugin.Frame (pins this Core ALC until process exit).";

        /// <summary>
        /// Choose the completion-hook route from whether Bootstrap owns process lifetime.
        /// </summary>
        public static HostedCompletionHookRoute Decide(bool bootstrapOwnsLifetime)
            => bootstrapOwnsLifetime
                ? HostedCompletionHookRoute.BootstrapSlot
                : HostedCompletionHookRoute.CoreConvertDelegateFallback;

        /// <summary>
        /// True when Core must register ordinary Actions on Bootstrap lifetime slots
        /// and must not ConvertDelegate.
        /// </summary>
        public static bool UseBootstrapLifetimeSlots(HostedCompletionHookRoute route)
            => route == HostedCompletionHookRoute.BootstrapSlot;

        /// <summary>
        /// True only on the Bootstrap-absent fallback. ConvertDelegate from Core is
        /// otherwise forbidden.
        /// </summary>
        public static bool ConvertDelegateFromCore(HostedCompletionHookRoute route)
            => route == HostedCompletionHookRoute.CoreConvertDelegateFallback;

        /// <summary>
        /// True when Hosted still subscribes to <c>Plugin.Frame</c> (fallback only).
        /// Bootstrap's <c>LifetimeFrame</c> slot replaces that subscription.
        /// </summary>
        public static bool SubscribePluginFrame(HostedCompletionHookRoute route)
            => route == HostedCompletionHookRoute.CoreConvertDelegateFallback;
    }

    /// <summary>
    /// Operator-facing refuse-spawn text. Two staging failures share
    /// <see cref="HostedStartAction.RefuseSpawn"/>; the log must name which one
    /// (R13 finding 9) so the operator does not hunt leftover
    /// <c>plugins/Progenitor.dll</c> when a leftover hosted child is the reason.
    /// </summary>
    public static class RefuseSpawnLog
    {
        public const string LeftoverChildMappedCore =
            "leftover hosted child still has progenitor-core/Progenitor.dll mapped. " +
            "Refusing StunProcess.Start so the copy cannot overwrite a running child's Core. " +
            "Kill that VRisingServer.exe by path (never the live dedicated server), then retry.";

        public const string StalePluginsCoreDll =
            "leftover plugins/Progenitor.dll cannot be deleted without unlinking the client's file. " +
            "Refusing StunProcess.Start so the child does not boot with the Core identity steal.";

        public static string Describe(bool leftoverChildMappedCore)
            => leftoverChildMappedCore ? LeftoverChildMappedCore : StalePluginsCoreDll;

        public static string HookError(bool leftoverChildMappedCore)
            => leftoverChildMappedCore
                ? "staging refused leftover hosted child mapping progenitor-core; spawn skipped"
                : "staging refused leftover plugins/Progenitor.dll; spawn skipped";
    }

    /// <summary>
    /// Game-free Start-prefix decision (R11 finding 8). A leftover
    /// <c>plugins/Progenitor.dll</c> that cannot be deleted, or a leftover bundled
    /// child with dest Core mapped, must skip <c>StunProcess.Start</c>; every other
    /// staging miss still lets the original run but does not strip Doorstop (so we
    /// do not inject a broken tree).
    /// </summary>
    public static class StartDecision
    {
        /// <summary>
        /// Maps the prefix's path/staging outcomes onto <see cref="HostedStartAction"/>.
        /// </summary>
        public static HostedStartAction Decide(
            bool isBundledServerExe,
            bool enabled,
            bool serverDirValid,
            bool stagingOk,
            bool leftoverPluginsCoreDllCouldNotBeRemoved)
        {
            if (!isBundledServerExe || !enabled || !serverDirValid)
                return HostedStartAction.PassThrough;
            if (leftoverPluginsCoreDllCouldNotBeRemoved)
                return HostedStartAction.RefuseSpawn;
            if (!stagingOk)
                return HostedStartAction.PassThrough;
            return HostedStartAction.InjectAndStripDoorstop;
        }
    }

    /// <summary>
    /// Game-free rules for the 30 s child BepInEx log probe. A leftover
    /// <c>LogOutput.log</c> from a previous hosted run must not count as Doorstop injecting
    /// this spawn, and retiring that leftover must never target the live dedicated server.
    /// </summary>
    public static class ChildLogProbe
    {
        /// <summary>BepInEx's default log file name inside the child's <c>BepInEx</c> folder.</summary>
        public const string FileName = "LogOutput.log";

        /// <summary>
        /// Suffix appended to a leftover log so the probe cannot mistake it for this spawn.
        /// Same directory as <see cref="FileName"/>; never a path under
        /// <see cref="HostedPathRules.ForbiddenLiveServerRoot"/>.
        /// </summary>
        public const string RetiredSuffix = ".prev";

        /// <summary>
        /// True when the file exists and was written at or after <paramref name="spawnUtc"/>,
        /// so it is this child's injection rather than a leftover from a previous hosted run.
        /// </summary>
        public static bool IsFromThisSpawn(bool exists, DateTime lastWriteUtc, DateTime spawnUtc)
            => exists && lastWriteUtc >= spawnUtc;

        /// <summary>
        /// True when a process with <paramref name="startUtc"/> counts as a leftover relative to
        /// <paramref name="spawnUtc"/>: it started more than 2 s before the spawn, or its start
        /// time is unknown (kept, conservative). The child spawned by this Start starts after
        /// <paramref name="spawnUtc"/> and is excluded (evidence 38).
        /// </summary>
        public static bool StartedBefore(DateTime? startUtc, DateTime spawnUtc)
            => !startUtc.HasValue || startUtc.Value < spawnUtc.AddSeconds(-2);

        /// <summary>
        /// True when <paramref name="logPath"/> may be renamed aside before this spawn. The
        /// file must be <c>{serverDir}\BepInEx\LogOutput.log</c>, and both it and the retired
        /// name must sit inside <paramref name="serverDir"/> and not under the live dedicated
        /// server. Path-only: does not touch disk.
        /// </summary>
        public static bool MayRetireLeftover(
            string? logPath,
            string? serverDir,
            out string retiredPath,
            out string reason)
        {
            retiredPath = "";
            reason = "";
            try
            {
                if (string.IsNullOrWhiteSpace(logPath) || string.IsNullOrWhiteSpace(serverDir))
                {
                    reason = "path is empty";
                    return false;
                }

                if (HostedPathRules.IsForbiddenLiveServerPath(logPath) ||
                    HostedPathRules.IsForbiddenLiveServerPath(serverDir))
                {
                    reason =
                        $"refusing to retire a log under the live dedicated server root ({HostedPathRules.ForbiddenLiveServerRoot})";
                    return false;
                }

                var logFull = HostedPathRules.Normalize(logPath);
                var serverFull = HostedPathRules.Normalize(serverDir);

                if (!HostedPathRules.IsUnder(logFull, serverFull))
                {
                    reason =
                        $"refusing to retire '{logFull}' which is not inside serverDir '{serverFull}'";
                    return false;
                }

                var parent = Path.GetDirectoryName(logFull);
                var expectedParent = Path.Combine(serverFull, "BepInEx");
                var name = Path.GetFileName(logFull);
                if (parent == null ||
                    !HostedPathRules.PathsEqual(parent, expectedParent) ||
                    !string.Equals(name, FileName, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"refusing to retire unexpected path {logFull}";
                    return false;
                }

                retiredPath = logFull + RetiredSuffix;
                if (HostedPathRules.IsForbiddenLiveServerPath(retiredPath) ||
                    !HostedPathRules.IsUnder(retiredPath, serverFull))
                {
                    reason = $"refusing to retire to '{retiredPath}'";
                    retiredPath = "";
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                reason = "retire-safety check threw: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 30 s probe outcome when a leftover bundled child is still writing
        /// <c>LogOutput.log</c>. Must not be reported as <c>present</c> (that would credit
        /// Doorstop injection on the new spawn from the leftover child's writes).
        /// </summary>
        public const string InconclusiveLeftoverChildAlive = "inconclusive: leftover child alive";

        /// <summary>
        /// True when <paramref name="processPath"/> is a <c>VRisingServer.exe</c> whose folder
        /// is the bundled server directory (the game's <c>VRising_Server</c>). Never true for
        /// the live dedicated server at <see cref="HostedPathRules.ForbiddenLiveServerRoot"/>
        /// on either the process path or <paramref name="bundledServerDir"/>. Path-only: does
        /// not touch processes or disk, and does not inspect argv (the leftover scan matches
        /// by executable path under <c>VRising_Server</c> regardless of command line; this
        /// helper never kills).
        /// </summary>
        public static bool IsBundledChildProcess(string? processPath, string? bundledServerDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(bundledServerDir))
                    return false;

                // Normalize both legs first (full path, trailing separators, \\?\ prefix,
                // case folded by PathsEqual / EndsWith ignore-case). EndsWith on the raw
                // image name misses a \\?\ prefix or a trailing slash (R8 regression D).
                var procFull = HostedPathRules.Normalize(processPath);
                var serverFull = HostedPathRules.Normalize(bundledServerDir);

                if (HostedPathRules.IsForbiddenLiveServerPath(procFull) ||
                    HostedPathRules.IsForbiddenLiveServerPath(serverFull))
                    return false;

                if (!procFull.EndsWith("VRisingServer.exe", StringComparison.OrdinalIgnoreCase))
                    return false;

                var procDir = Path.GetDirectoryName(procFull);
                if (string.IsNullOrEmpty(procDir))
                    return false;

                return HostedPathRules.PathsEqual(procDir, serverFull)
                       || HostedPathRules.IsUnder(procFull, serverFull);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True when a leftover-log <c>File.Move</c> failed because the file is still open
        /// (sharing / lock violation or unauthorized). The probe must then treat the leftover
        /// as alive rather than renaming and later crediting those writes as this spawn.
        /// </summary>
        public static bool IsOpenFileRenameFailure(Exception? e)
        {
            if (e == null) return false;
            if (e is UnauthorizedAccessException) return true;
            if (e is not IOException) return false;

            // Win32 ERROR_SHARING_VIOLATION 32, ERROR_LOCK_VIOLATION 33 (HRESULT 0x8007xxxx).
            const int share = unchecked((int)0x80070020);
            const int lockViolation = unchecked((int)0x80070021);
            int hr = e.HResult;
            if (hr == share || hr == lockViolation || hr == 32 || hr == 33) return true;
            var msg = e.Message;
            return !string.IsNullOrEmpty(msg)
                   && msg.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Any leftover-log rename exception (sharing, unauthorized, disk, unexpected)
        /// means the leftover is still alive. The 30 s probe must then report
        /// <see cref="InconclusiveLeftoverChildAlive"/>, never <c>present</c> (R8 row 3).
        /// </summary>
        public static bool TreatRenameFailureAsLeftoverAlive(Exception? e) => e != null;

        /// <summary>
        /// One-line 30 s probe result. A leftover bundled child still alive is always
        /// <see cref="InconclusiveLeftoverChildAlive"/>, even when the log file looks
        /// newly written (that write is the leftover child's).
        /// </summary>
        public static string FormatProbeLine(
            bool leftoverChildAlive,
            bool exists,
            DateTime lastWriteUtc,
            DateTime spawnUtc,
            double probeSeconds,
            long length,
            string logPath,
            bool dryRun)
        {
            if (leftoverChildAlive)
                return InconclusiveLeftoverChildAlive + ": " + logPath;

            bool present = IsFromThisSpawn(exists, lastWriteUtc, spawnUtc);
            if (present)
            {
                return
                    $"present after {probeSeconds:0}s ({length} bytes, utc {lastWriteUtc:o}): {logPath}";
            }

            string line;
            if (exists)
            {
                line =
                    $"ABSENT after {probeSeconds:0}s (leftover log utc {lastWriteUtc:o} is before spawn {spawnUtc:o}): {logPath}";
            }
            else
            {
                line =
                    $"ABSENT after {probeSeconds:0}s (Doorstop likely did not inject): {logPath}";
            }

            if (dryRun)
                line += " (DryRun was on at spawn; DOORSTOP_* were not cleared)";
            return line;
        }
    }

#if !PROGENITOR_TESTS
    private const double ChildLogProbeSeconds = 30.0;
    private const string DoorstopEnvPrefix = "DOORSTOP_";

    private static readonly object Gate = new();
    private static readonly object ChildLogProbeOwner = new();
    private static readonly object UninstallRetryOwner = new();
    private static readonly object CompletionHooksOwner = new();
    private static readonly List<MethodInfo> Patched = new();
    private static readonly DoorstopStripNest Strip = new();

    private static Harmony? _harmony;
    private static Harmony? _pendingInstallHarmony;
    private static bool _installed;

    /// <summary>
    /// Job FHARM8 (R42 finding 5): <c>StunProcess.Start</c> was refused because the slot pool
    /// cannot carry its prefix-to-finalizer <c>__state</c> and Bootstrap is present. The module
    /// is therefore <b>disarmed</b> for this Core generation even though <see cref="Enabled"/>
    /// still reads whatever the player set. Latched so <see cref="Describe"/> and
    /// <see cref="Armed"/> can tell the truth long after the one boot-time error line scrolled
    /// away, which is the gap between the refusal and the user action it affects.
    /// </summary>
    private static bool _startSitesRefused;
    private static bool _uninstallRequested;
    private static bool _installQueuedAfterUninstall;
    private static bool _completionHooksAttached;
    private static bool _usedBootstrapSlots;
    private static HostedCompletionHookRoute? _completionHookRoute;
    private static Il2CppSystem.Action? _quittingHandler;
    private static bool _waitExtended;
    private static double? _originalWait;
    private static int? _originalRetries;
    private static string? _lastHookError;
    private static string? _lastFileName;
    private static string? _lastDir;
    private static string? _lastDoorstopNote;
    private static string? _lastChildLogProbe;

    // Doorstop env snapshot around StunProcess.Start. Nested Starts share one snapshot;
    // only the outermost Leave writes the variables back. _doorstopDidClear is false under
    // DryRun (log only). Harmony __state is true only on Starts that Enter'd.
    private static Dictionary<string, string?>? _doorstopSnapshot;
    private static bool _doorstopDidClear;

    private static ConfigEntry<bool>? _enabled;
    private static ConfigEntry<bool>? _dryRun;
    private static ConfigEntry<string>? _serverPluginsMode;

    /// <summary>When false, the spawn prefix does nothing. Default true so hosted play just works.</summary>
    public static bool Enabled => _enabled?.Value ?? true;

    /// <summary>
    /// Whether the launcher can actually do anything: the player enabled it <b>and</b> the
    /// <c>StunProcess.Start</c> sites are hooked. <see cref="Enabled"/> alone was reported as if
    /// it were this, which is R42 finding 5 (job FHARM8).
    /// </summary>
    public static bool Armed => Enabled && !_startSitesRefused && _installed;

    /// <summary>
    /// The line logged when the Start sites are refused, and re-stated wherever the player can
    /// see it. Says the setting is on, the module is not, and what to do about it.
    /// </summary>
    internal static string FormatDisarmed() =>
        "Hosted: the private-game launcher is DISARMED for this Core generation even though "
        + "Enabled=" + Enabled + ": StunProcess.Start could not be hooked (see the patches: "
        + "refused line above). Pressing Play will spawn the bundled server WITHOUT the Doorstop "
        + "strip, so it will not behave as a hosted Progenitor server. Update Progenitor.Bootstrap "
        + "so it owns the site, then restart both.";

    /// <summary>
    /// When true, <see cref="Staging.EnsureStaged"/> only logs the work it would do, and
    /// <c>DOORSTOP_*</c> is listed but not stripped from the process environment.
    /// </summary>
    public static bool DryRun => _dryRun?.Value ?? false;

    /// <summary>
    /// How the child's plugins folder is filled. Default
    /// <see cref="HostedServerPluginsMode.Shared"/> junctions plugins at the client so
    /// client-only mods run in the child; <c>progenitor-core</c> is a real copy in both
    /// modes so the child's <c>.next</c> promote cannot <c>File.Move</c> the client's Core.
    /// ProgenitorOnly copies Bootstrap into plugins and Core into progenitor-core.
    /// Stored as a string on the shared ConfigFile so <c>ConfigEntry&lt;HostedServerPluginsMode&gt;</c>
    /// cannot pin a collectible Core generation (evidence 65).
    /// </summary>
    public static HostedServerPluginsMode ServerPluginsMode =>
        ConfigHelper.ReadEnum(_serverPluginsMode, HostedServerPluginsMode.Shared);

    /// <summary>
    /// Patches the <see cref="StunProcess"/> start overrides used to spawn the bundled server.
    /// <see cref="StunProcess.Start"/> is abstract; the Windows client invokes
    /// <see cref="StunProcess_PInvoke.Start"/>, and <see cref="StunProcess_MONO.Start"/> exists for
    /// the other runtime. Both overrides are patched. Safe to call twice.
    /// </summary>
    public static void Install(Harmony? harmony)
    {
        lock (Gate)
        {
            InstallUnlocked(harmony);
        }
    }

    private static void InstallUnlocked(Harmony? harmony)
    {
        BindConfig();

        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.HostedModule))
        {
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.HostedModule));
            return;
        }

        if (Plugin.IsServerProcess)
        {
            Plugin.Logger?.LogInfo("Hosted: server process, launcher not installed.");
            return;
        }

        EnsureCompletionHooksUnlocked();

        if (_installed)
        {
            if (UninstallDeferral.QueueInstallAfterDeferredUninstall(_installed, _uninstallRequested))
            {
                _pendingInstallHarmony = harmony;
                _installQueuedAfterUninstall = true;
                Plugin.Logger?.LogInfo(
                    "Hosted: Install queued after deferred Uninstall (will re-patch once Depth is 0; not a no-op).");
            }
            else
            {
                Plugin.Logger?.LogInfo("Hosted: launcher already installed.");
            }

            return;
        }

        _harmony = harmony;
        if (HarmonySlotHandoff.SkipCoreHarmony)
        {
            _installed = true;
            _installQueuedAfterUninstall = false;
            _pendingInstallHarmony = null;
            Plugin.Logger?.LogInfo(
                $"Hosted: installed (Harmony skipped). Enabled={Enabled} DryRun={DryRun} ServerPluginsMode={ServerPluginsMode}.");
            return;
        }

        if (!BindStunProcessSites(harmony))
            return;

        _installed = true;
        _installQueuedAfterUninstall = false;
        _pendingInstallHarmony = null;
        Plugin.Logger?.LogInfo(
            $"Hosted: installed (per-site prefix+finalizer). Enabled={Enabled} DryRun={DryRun} ServerPluginsMode={ServerPluginsMode}.");
    }

    /// <summary>
    /// Removes only this module's Start prefix/finalizer. Staged files on disk are left in place.
    /// Drops the owned 30 s child-log probe so Uninstall cannot leave a callback against a
    /// collected Core. Refuses (logs and retries) while a Start is between snapshot and restore:
    /// Reset + restore in that window would put <c>DOORSTOP_INITIALIZED</c> back for the child
    /// and make the later finalizer a no-op. Completion is the next frame tick
    /// (Bootstrap <c>LifetimeFrame</c> / <c>Plugin.Frame</c> fallback / <c>MainThread.Post</c>),
    /// <c>Application.quitting</c> (Bootstrap slot, or ConvertDelegate only when Bootstrap is
    /// absent), or a 0.5 s <c>MainThread.PostDelayed</c> — never Harmony unpatch from inside
    /// the Start finalizer, and never ConvertDelegate of a Core method when Bootstrap owns
    /// process lifetime (R11 finding 4).
    /// </summary>
    public static void Uninstall()
    {
        lock (Gate)
        {
            UninstallUnlocked();
        }
    }

    private static void UninstallUnlocked()
    {
        if (!_installed && !_uninstallRequested) return;

        if (Strip.HasInFlightStart)
        {
            _uninstallRequested = true;
            Plugin.Logger?.LogWarning(
                "Hosted: Uninstall refused; a Start is between snapshot and restore (nest=" +
                Strip.Depth + "). Deferred Uninstall completes from the next frame tick, " +
                "Application.quitting, or a " + UninstallDeferral.RetrySeconds.ToString("0.#") +
                "s retry — never from the StunProcess.Start finalizer (Harmony unpatch of the " +
                "running method).");
            ScheduleUninstallRetryUnlocked();
            EnsureCompletionHooksUnlocked();
            return;
        }

        bool queuedInstall = _installQueuedAfterUninstall;
        var pendingHarmony = _pendingInstallHarmony ?? _harmony;
        _installQueuedAfterUninstall = false;
        _pendingInstallHarmony = null;

        HarmonySlotHandoff.Clear(HarmonySlotHandoff.StunProcessStartPrefix);
        HarmonySlotHandoff.Clear(HarmonySlotHandoff.StunProcessStartFinalizer);
        var harmony = _harmony;
        if (harmony != null)
        {
            foreach (var method in Patched)
            {
                try { harmony.Unpatch(method, HarmonyPatchType.Prefix, harmony.Id); }
                catch (Exception e) { Plugin.Logger?.LogError($"Hosted: unpatch prefix {method.DeclaringType?.Name}.Start threw: {e}"); }
                try { harmony.Unpatch(method, HarmonyPatchType.Finalizer, harmony.Id); }
                catch (Exception e) { Plugin.Logger?.LogError($"Hosted: unpatch finalizer {method.DeclaringType?.Name}.Start threw: {e}"); }
            }
        }
        Patched.Clear();
        _harmony = null;
        _installed = false;
        _uninstallRequested = false;
        Strip.Reset();
        RestoreDoorstopEnvUnlocked();
        try { Dev.MainThread.DropOwnedBy(ChildLogProbeOwner, assembly: null); }
        catch (Exception e) { Plugin.Logger?.LogError($"Hosted: drop child-log probe threw: {e}"); }
        try { Dev.MainThread.DropOwnedBy(UninstallRetryOwner, assembly: null); }
        catch (Exception e) { Plugin.Logger?.LogError($"Hosted: drop uninstall retry threw: {e}"); }
        try { Dev.MainThread.DropOwnedBy(CompletionHooksOwner, assembly: null); }
        catch (Exception e) { Plugin.Logger?.LogError($"Hosted: drop completion-hook re-point threw: {e}"); }
        DropCompletionHooksUnlocked();
        _enabled = null;
        _dryRun = null;
        _serverPluginsMode = null;
        Plugin.Logger?.LogInfo("Hosted: launcher uninstalled.");

        if (UninstallDeferral.ShouldRunQueuedInstall(queuedInstall, nowInstalled: false) && pendingHarmony != null)
        {
            try { InstallUnlocked(pendingHarmony); }
            catch (Exception e) { Plugin.Logger?.LogError($"Hosted: queued Install after deferred Uninstall threw: {e}"); }
        }
    }

    /// <summary>
    /// Queue Uninstall for the next Drain (after the current Start finalizer returns) and a
    /// 0.5 s backstop. Drops a previous retry first so nested Starts do not pile callbacks.
    /// Never unpatches here — the caller may be the Start finalizer.
    /// </summary>
    private static void ScheduleUninstallRetryUnlocked()
    {
        try { Dev.MainThread.DropOwnedBy(UninstallRetryOwner, assembly: null); }
        catch (Exception e) { Plugin.Logger?.LogError($"Hosted: drop deferred-Uninstall retry threw: {e}"); }

        try { Dev.MainThread.Post(CompleteDeferredUninstallFromTick, UninstallRetryOwner); }
        catch (Exception e) { Plugin.Logger?.LogError($"Hosted: Post deferred Uninstall threw: {e}"); }

        try
        {
            Dev.MainThread.PostDelayed(
                UninstallDeferral.RetrySeconds,
                CompleteDeferredUninstallFromTick,
                UninstallRetryOwner);
        }
        catch (Exception e) { Plugin.Logger?.LogError($"Hosted: PostDelayed deferred-Uninstall retry threw: {e}"); }
    }

    private static void CompleteDeferredUninstallFromTick()
    {
        try { Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogError($"Hosted: deferred Uninstall from tick threw: {e}"); }
    }

    private static void OnFrameCompleteDeferredUninstall()
    {
        if (!_uninstallRequested) return;
        lock (Gate)
        {
            if (!UninstallDeferral.ShouldCompleteDeferredUninstall(_uninstallRequested, Strip.HasInFlightStart))
                return;
            try { UninstallUnlocked(); }
            catch (Exception e) { Plugin.Logger?.LogError($"Hosted: deferred Uninstall from Frame threw: {e}"); }
        }
    }

    private static void OnQuittingCompleteDeferredUninstall()
    {
        try { Uninstall(); }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogError($"Hosted: deferred Uninstall from Application.quitting threw: {e}"); }
            catch { /* process is dying */ }
        }
    }

    /// <summary>
    /// Attach deferred-Uninstall completion so it still runs when Drain does not (quit)
    /// or the Start finalizer never fires. When Bootstrap owns process lifetime, this
    /// points <c>ProcessHost.LifetimeQuitting</c> / <c>LifetimeFrame</c> at ordinary
    /// Core Actions. Bootstrap's ConvertDelegate target is
    /// <c>ProcessHost.InvokeLifetimeQuitting</c> (a Bootstrap method); Tick calls
    /// <c>InvokeLifetimeFrame</c>. Core never ConvertDelegates on this path (R11 finding 4);
    /// the Core Actions are KeepDelegate'd under the current generation so
    /// <c>ReleaseGeneration</c> drops them. Re-points every call: <c>Plugin.Install</c>
    /// currently nulls the slots after Hosted.Install returns.
    /// When Bootstrap is absent, falls back to ConvertDelegate + <see cref="Plugin.Frame"/>
    /// once, with a log that this pins the Core ALC.
    /// </summary>
    private static void EnsureCompletionHooksUnlocked()
    {
        var route = CompletionHookRouting.Decide(BootstrapLink.OwnsLifetime);
        _completionHookRoute = route;

        if (CompletionHookRouting.UseBootstrapLifetimeSlots(route))
        {
            AttachBootstrapSlotsUnlocked();
            bool first = !_completionHooksAttached;
            _completionHooksAttached = true;
            _usedBootstrapSlots = true;
            if (first)
            {
                Plugin.Logger?.LogInfo(
                    "Hosted: deferred-Uninstall completion routed through Bootstrap lifetime slots (no ConvertDelegate).");
            }

            // Plugin.Install/Uninstall currently null the slots after Hosted returns on the
            // same stack. Re-point on the next Drain so this generation stays in the slot
            // until DropCompletionHooks. The callback no-ops once Uninstall has finished.
            try { Dev.MainThread.DropOwnedBy(CompletionHooksOwner, assembly: null); }
            catch (Exception e) { Plugin.Logger?.LogError($"Hosted: drop Bootstrap-slot re-point threw: {e}"); }
            try { Dev.MainThread.Post(RepointBootstrapSlotsFromTick, CompletionHooksOwner); }
            catch (Exception e) { Plugin.Logger?.LogError($"Hosted: Post Bootstrap-slot re-point threw: {e}"); }
            return;
        }

        if (_completionHooksAttached) return;
        _completionHooksAttached = true;
        _usedBootstrapSlots = false;
        Plugin.Logger?.LogWarning("Hosted: " + CompletionHookRouting.FallbackLog);

        if (CompletionHookRouting.SubscribePluginFrame(route))
        {
            try { Plugin.Frame += OnFrameCompleteDeferredUninstall; }
            catch (Exception e) { Plugin.Logger?.LogError($"Hosted: Plugin.Frame subscribe threw: {e}"); }
        }

        if (!CompletionHookRouting.ConvertDelegateFromCore(route))
            return;

        try
        {
            _quittingHandler = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                (Action)OnQuittingCompleteDeferredUninstall);
            UnityEngine.Application.quitting += _quittingHandler;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                $"Hosted: Application.quitting subscribe threw ({e.Message}); Frame tick and the " +
                UninstallDeferral.RetrySeconds.ToString("0.#") + "s retry remain.");
        }
    }

    private static void AttachBootstrapSlotsUnlocked()
    {
        try { BootstrapLink.SetLifetimeFrame(OnFrameCompleteDeferredUninstall); }
        catch (Exception e) { Plugin.Logger?.LogError($"Hosted: SetLifetimeFrame threw: {e}"); }
        try { LifetimeQuit.SetHosted(OnQuittingCompleteDeferredUninstall); }
        catch (Exception e) { Plugin.Logger?.LogError($"Hosted: SetLifetimeQuitting threw: {e}"); }
    }

    private static void RepointBootstrapSlotsFromTick()
    {
        try
        {
            lock (Gate)
            {
                if (!_installed && !_uninstallRequested) return;
                if (!_usedBootstrapSlots) return;
                AttachBootstrapSlotsUnlocked();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Hosted: Bootstrap-slot re-point threw: {e}");
        }
    }

    /// <summary>
    /// Drop process-lifetime completion hooks so a collectible Core Uninstall does not
    /// leave this generation in Bootstrap slots or on <c>Application.quitting</c>.
    /// No-op when hooks were never attached. Must not ConvertDelegate.
    /// </summary>
    private static void DropCompletionHooksUnlocked()
    {
        if (!_completionHooksAttached)
        {
            _completionHookRoute = null;
            _usedBootstrapSlots = false;
            return;
        }

        if (_usedBootstrapSlots)
        {
            try { BootstrapLink.SetLifetimeFrame(null); }
            catch (Exception e) { Plugin.Logger?.LogError($"Hosted: clear LifetimeFrame threw: {e}"); }
            try { LifetimeQuit.SetHosted(null); }
            catch (Exception e) { Plugin.Logger?.LogError($"Hosted: clear LifetimeQuitting threw: {e}"); }
        }
        else
        {
            try { Plugin.Frame -= OnFrameCompleteDeferredUninstall; }
            catch (Exception e) { Plugin.Logger?.LogError($"Hosted: Plugin.Frame unsubscribe threw: {e}"); }

            var quitting = _quittingHandler;
            _quittingHandler = null;
            if (quitting != null)
            {
                try { UnityEngine.Application.quitting -= quitting; }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"Hosted: Application.quitting unsubscribe threw: {e.Message}");
                }
            }
        }

        _completionHooksAttached = false;
        _usedBootstrapSlots = false;
        _completionHookRoute = null;
    }

    /// <summary>
    /// One-line-per-field status for the debug bridge or a log dump. Reads wait constants and the
    /// last staging result; never throws.
    /// </summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine("Hosted game launcher");
            sb.AppendLine($"  process: {(Plugin.IsServerProcess ? "server" : "client")}");
            sb.AppendLine($"  installed: {_installed}  patches: {Patched.Count}");
            sb.AppendLine($"  Enabled: {Enabled}  Armed: {Armed}  DryRun: {DryRun}  ServerPluginsMode: {ServerPluginsMode}");
            if (_startSitesRefused) sb.AppendLine("  " + FormatDisarmed());
            sb.AppendLine($"  game root: {SafePath(() => Paths.GameRootPath)}");
            sb.AppendLine($"  client BepInEx: {SafePath(() => Paths.BepInExRootPath)}");
            sb.AppendLine($"  ServerPath setting: {ReadServerPath()}");
            AppendWaitConstants(sb);
            sb.AppendLine($"  last Start fileName: {_lastFileName ?? "(none)"}");
            sb.AppendLine($"  last Start dir: {_lastDir ?? "(none)"}");
            sb.AppendLine($"  last Doorstop env: {_lastDoorstopNote ?? "(none)"}");
            sb.AppendLine($"  last child BepInEx log probe: {_lastChildLogProbe ?? "(none)"}");
            sb.AppendLine(
                $"  doorstop nest: {Strip.Depth}  uninstall deferred: {_uninstallRequested}  install queued: {_installQueuedAfterUninstall}");
            sb.AppendLine(
                $"  completion hooks: {(_completionHookRoute.HasValue ? _completionHookRoute.Value.ToString() : "(none)")}  attached: {_completionHooksAttached}  bootstrapSlots: {_usedBootstrapSlots}");
            if (_lastHookError != null)
                sb.AppendLine($"  last hook error: {_lastHookError}");
            var staging = Staging.LastSummary;
            if (!string.IsNullOrEmpty(staging))
            {
                sb.AppendLine("  last staging:");
                foreach (var line in staging.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    sb.AppendLine("    " + line);
            }
            else
            {
                sb.AppendLine("  last staging: (none)");
            }
        }
        catch (Exception e)
        {
            sb.AppendLine($"  Describe threw: {e.Message}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// What this module registered that can outlive a collectible Core generation
    /// (Bootstrap lifetime slots, ConvertDelegate on Application.quitting, Harmony fallback).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            if (_installed) holds.Add("installed");
            if (_harmony != null) holds.Add("HarmonyFallback");
            if (_completionHooksAttached)
            {
                holds.Add(_usedBootstrapSlots
                    ? "BootstrapLifetimeSlots"
                    : "ConvertDelegate+Plugin.Frame");
            }
            if (_quittingHandler != null) holds.Add("Application.quitting");
            if (_uninstallRequested) holds.Add("deferredUninstall");
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.HostedModule, holds);
    }

    // ---- harmony ------------------------------------------------------------------------------

    /// <summary>
    /// Job FHARM7: the direct fallback for the three <c>Start</c> overrides, permitted only when
    /// <c>Progenitor.Bootstrap</c> is absent.
    ///
    /// <para><b>Why this one cannot be slot-routed.</b> The pool's bodies are
    /// signature-agnostic — <c>__instance</c> and <c>__args</c>, nothing else — and this site is
    /// a prefix/finalizer <b>pair</b> joined by Harmony's <c>__state</c>: the prefix's Enter bit
    /// tells <see cref="FinalizerStart"/> whether this particular <c>Start</c> entered the
    /// Doorstop-strip nest, and a finalizer that Leaves a nest it did not Enter corrupts the
    /// depth for the client's real spawns. A slot carries no <c>__state</c>, so routing this
    /// through the pool would silently drop the pairing. With Bootstrap present the site is
    /// therefore <b>refused</b> rather than patched directly (Bootstrap owns
    /// <c>StunProcessStartPrefix</c>/<c>Finalizer</c> as typed slots; a Bootstrap that does not
    /// is a Bootstrap to update, followed by a restart of both). With Bootstrap absent — a Core
    /// dumped on its own — there is no pool and no hot swap to protect, so the direct patch runs
    /// and names the generation it pins.</para>
    /// </summary>
    private static void TryPatchStart(Harmony harmony, Type type, string prefixName)
    {
        try
        {
            var method = AccessTools.Method(type, "Start");
            if (method == null)
            {
                Plugin.Logger?.LogWarning($"Hosted: {type.Name}.Start was not found on the interop type.");
                return;
            }

            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(HostedServerLauncher), prefixName),
                finalizer: new HarmonyMethod(typeof(HostedServerLauncher), nameof(FinalizerStart)));
            Patched.Add(method);
            Plugin.Logger?.LogInfo($"Hosted: patched {type.Name}.Start ({method.GetParameters().Length} args, prefix+finalizer).");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Hosted: patching {type.Name}.Start threw: {e}");
        }
    }

    internal delegate bool StunProcessStart(string? fileName, string? dir, out bool enteredNest);
    internal delegate void StunProcessStartFinalizer(bool enteredNest);

    /// <summary>
    /// Per-site: fill prefix and finalizer independently so a failed finalizer Bind
    /// cannot empty a live refuse-spawn prefix (R14 finding 1). Fallback-Patch the
    /// Start overrides only when Bootstrap did not own the original <b>and</b> Bootstrap is
    /// absent altogether (job FHARM7): the pair is joined by <c>__state</c>, which a slot cannot
    /// carry, so with Bootstrap present the site is refused rather than permanently pinned.
    /// </summary>
    private static bool BindStunProcessSites(Harmony? harmony)
    {
        var prefix = HarmonySlotHandoff.Bind(
            HarmonySlotHandoff.StunProcessStartPrefix,
            new StunProcessStart(SlotPrefix));
        var finalizer = HarmonySlotHandoff.Bind(
            HarmonySlotHandoff.StunProcessStartFinalizer,
            new StunProcessStartFinalizer(FinalizerStart));

        if (prefix == HarmonySlotHandoff.SiteBind.Inert)
            Plugin.Logger?.LogError(HarmonySlotHandoff.InertLog("Hosted", HarmonySlotHandoff.StunProcessStartPrefix));
        if (finalizer == HarmonySlotHandoff.SiteBind.Inert)
            Plugin.Logger?.LogError(HarmonySlotHandoff.InertLog("Hosted", HarmonySlotHandoff.StunProcessStartFinalizer));
        if (prefix == HarmonySlotHandoff.SiteBind.Vanilla)
            Plugin.Logger?.LogError(HarmonySlotHandoff.VanillaLog("Hosted", HarmonySlotHandoff.StunProcessStartPrefix));
        if (finalizer == HarmonySlotHandoff.SiteBind.Vanilla)
            Plugin.Logger?.LogError(HarmonySlotHandoff.VanillaLog("Hosted", HarmonySlotHandoff.StunProcessStartFinalizer));

        if (prefix != HarmonySlotHandoff.SiteBind.Fallback || finalizer != HarmonySlotHandoff.SiteBind.Fallback)
            return true;

        // Job FHARM7: prefix+finalizer joined by __state is a shape the slot pool cannot carry,
        // so the direct patch is allowed only with Bootstrap absent. See TryPatchStart.
        var route = Progenitor.Patches.CorePatchFallbackPolicy.Decide(
            slotShapeFits: false,
            bootstrapPresent: Progenitor.Patches.PatchRegistry.SlotsAvailable);
        if (route != Progenitor.Patches.CoreFallbackRoute.Direct)
        {
            Plugin.Logger?.LogError(Progenitor.Patches.CorePatchFallbackPolicy.FormatRefused(
                Progenitor.Patches.CorePatchFallbackPolicy.ModuleHosted,
                HarmonySlotHandoff.StunProcessStart));
            // Job FHARM8 (R42 finding 5). Install returns here without setting _installed, and
            // Enabled — the flag the player set — still reads on. The refusal is one error line
            // at Core install; the consequence appears minutes later when the player presses Play
            // and the child spawns with Doorstop still attached. Latch it so the truth is
            // available at the moment the player looks (Describe, and the disarmed line below).
            _startSitesRefused = true;
            Plugin.Logger?.LogError(FormatDisarmed());
            return false;
        }

        Harmony instance;
        try { instance = RequireHarmony(harmony); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Hosted: no Harmony instance and Bootstrap slots unavailable: " + e.Message);
            return false;
        }

        Plugin.Logger?.LogWarning(Progenitor.Patches.CorePatchFallbackPolicy.FormatDirect(
            Progenitor.Patches.CorePatchFallbackPolicy.ModuleHosted,
            HarmonySlotHandoff.StunProcessStart,
            Progenitor.WorldState.WorldEpoch.CoreGenerationOrZero()));

        Plugin.Logger?.LogInfo(HarmonySlotHandoff.FallbackSiteLog(HarmonySlotHandoff.StunProcessStart));
        // Base Start is abstract (il2cpp: Public_Abstract_Virtual_New). Patching it is best-effort
        // in case something calls through the interop wrapper; the overrides are the real hooks.
        TryPatchStart(instance, typeof(StunProcess), nameof(PrefixFileName));
        TryPatchStart(instance, typeof(StunProcess_PInvoke), nameof(PrefixFileName));
        TryPatchStart(instance, typeof(StunProcess_MONO), nameof(PrefixPath));
        return true;
    }

    private static Harmony RequireHarmony(Harmony? harmony)
    {
        if (harmony != null)
        {
            _harmony = harmony;
            return harmony;
        }
        var created = Progenitor.Reload.HarmonyFallback.Ensure();
        _harmony = created;
        return created;
    }

    private static bool SlotPrefix(string? fileName, string? dir, out bool enteredNest)
    {
        enteredNest = false;
        try
        {
            var action = OnStart(fileName, dir);
            if (action == HostedStartAction.RefuseSpawn)
                return false;
            if (action == HostedStartAction.InjectAndStripDoorstop)
                enteredNest = BeginDoorstopStrip();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Hosted: Start prefix threw (server will still launch): {e}");
            return true;
        }
    }

    // Full signatures so Harmony can match IL2CPP by position as well as by name.
    // __state is true only when this Start Enter'd the nest; the finalizer Leaves iff that bit.
    // Returning false skips StunProcess.Start (R11 finding 8 leftover Core fail-closed).
    private static bool PrefixFileName(string fileName, string arguments, string dir, bool hidden, bool redirectStandardInput, ref bool __state)
    {
        _ = arguments;
        _ = hidden;
        _ = redirectStandardInput;
        __state = false;
        bool run = SlotPrefix(fileName, dir, out bool entered);
        __state = entered;
        return run;
    }

    // StunProcess_MONO override names the first parameter path.
    private static bool PrefixPath(string path, string arguments, string dir, bool hidden, bool redirectStandardInput, ref bool __state)
    {
        _ = arguments;
        _ = hidden;
        _ = redirectStandardInput;
        __state = false;
        bool run = SlotPrefix(path, dir, out bool entered);
        __state = entered;
        return run;
    }

    /// <summary>
    /// Restore the client's Doorstop env after <c>CreateProcess</c> has copied it (or failed).
    /// Harmony postfix is skipped when the original throws; the finalizer always runs, so restore
    /// lives only here. <paramref name="__state"/> is the prefix's Enter bit: a non-hosted Start
    /// (or a hosted Start that failed the path refuse) must not decrement the nest. The whole
    /// body is try/catch so a restore-log throw cannot replace <c>StunProcess.Start</c>'s
    /// exception (Harmony would otherwise keep the finalizer's).
    /// </summary>
    private static void FinalizerStart(bool __state)
    {
        try
        {
            if (__state)
                EndDoorstopStrip();

            // Do not Unpatch from this stack (R8 regression B). Depth is now 0 after Leave;
            // schedule completion for the next FrameTicker Drain / Bootstrap LifetimeFrame
            // or Application.quitting slot (ConvertDelegate only when Bootstrap is absent) /
            // 0.5 s retry. Same Gate as Install so a queued re-Install cannot race.
            lock (Gate)
            {
                if (UninstallDeferral.RequestIdleCompletionFromFinalizer(
                        _uninstallRequested, Strip.HasInFlightStart))
                {
                    ScheduleUninstallRetryUnlocked();
                }
            }
        }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogError($"Hosted: Start finalizer threw: {e}"); }
            catch { /* never replace StunProcess.Start's exception */ }
        }
    }

    /// <returns>
    /// <see cref="HostedStartAction"/> for this Start: inject (strip Doorstop), refuse the
    /// spawn (leftover Core that cannot be deleted), or pass through.
    /// </returns>
    private static HostedStartAction OnStart(string? fileName, string? dir)
    {
        try
        {
            _lastFileName = fileName;
            _lastDir = dir;

            bool isServerExe = !string.IsNullOrEmpty(fileName) &&
                fileName.EndsWith("VRisingServer.exe", StringComparison.OrdinalIgnoreCase);
            if (!isServerExe)
                return StartDecision.Decide(false, Enabled, serverDirValid: false, stagingOk: false, leftoverPluginsCoreDllCouldNotBeRemoved: false);

            if (!Enabled)
            {
                Plugin.Logger?.LogInfo("Hosted: Enabled=false, not staging the bundled server.");
                return StartDecision.Decide(true, false, serverDirValid: false, stagingOk: false, leftoverPluginsCoreDllCouldNotBeRemoved: false);
            }

            Plugin.Logger?.LogInfo($"Hosted: StunProcess.Start fileName='{fileName}' dir='{dir}'.");

            if (!TryResolveServerDir(fileName!, dir, out var serverDir, out var reason))
            {
                _lastHookError = reason;
                Plugin.Logger?.LogError($"Hosted: cannot resolve server directory: {reason}");
                return StartDecision.Decide(true, true, serverDirValid: false, stagingOk: false, leftoverPluginsCoreDllCouldNotBeRemoved: false);
            }

            // Refuse before EnsureStaged so a mis-pointed ServerPath cannot wipe C:\VRisingServer
            // (or any other VRisingServer.exe outside the Steam install). Staging checks again.
            string gameRoot;
            string clientBep;
            try
            {
                gameRoot = Paths.GameRootPath;
                clientBep = Paths.BepInExRootPath;
            }
            catch (Exception e)
            {
                _lastHookError = "could not read game paths: " + e.Message;
                Plugin.Logger?.LogError($"Hosted: {_lastHookError}");
                return StartDecision.Decide(true, true, serverDirValid: false, stagingOk: false, leftoverPluginsCoreDllCouldNotBeRemoved: false);
            }

            if (!HostedPathRules.TryValidateServerDirectory(serverDir, gameRoot, clientBep, out var unsafeReason))
            {
                _lastHookError = unsafeReason;
                Plugin.Logger?.LogError($"Hosted: refusing to stage: {unsafeReason}");
                return StartDecision.Decide(true, true, serverDirValid: false, stagingOk: false, leftoverPluginsCoreDllCouldNotBeRemoved: false);
            }

            Staging.EnsureStaged(serverDir);
            // F23: the private game's server folder, for the Install tab's local route while this client hosts it.
            Progenitor.Install.HostedServerFolder.LastStaged = serverDir;
            var action = StartDecision.Decide(
                isBundledServerExe: true,
                enabled: true,
                serverDirValid: true,
                stagingOk: Staging.LastOk,
                leftoverPluginsCoreDllCouldNotBeRemoved: Staging.LastRefuseSpawn);
            if (action == HostedStartAction.RefuseSpawn)
            {
                bool leftoverChild = Staging.LastLeftoverChildMappedCore;
                _lastHookError = RefuseSpawnLog.HookError(leftoverChild);
                Plugin.Logger?.LogError(
                    "Hosted: " + RefuseSpawnLog.Describe(leftoverChild) + " " + Staging.LastSummary);
                return action;
            }

            if (action != HostedStartAction.InjectAndStripDoorstop)
            {
                _lastHookError = "staging failed; Doorstop not stripped so a broken tree is not injected";
                Plugin.Logger?.LogError($"Hosted: staging failed; child Start still runs without Doorstop rewrite. {Staging.LastSummary}");
                return action;
            }

            ExtendWaitConstantsOnce();
            ScheduleChildLogProbe(serverDir);
            return action;
        }
        catch (Exception e)
        {
            _lastHookError = e.Message;
            Plugin.Logger?.LogError($"Hosted: Start prefix threw (server will still launch): {e}");
            return HostedStartAction.PassThrough;
        }
    }

    internal static bool TryResolveServerDir(string fileName, string? dir, out string serverDir, out string reason)
    {
        serverDir = "";
        reason = "";
        try
        {
            string resolved;
            if (Path.IsPathRooted(fileName))
            {
                resolved = Path.GetFullPath(fileName);
            }
            else if (!string.IsNullOrEmpty(dir))
            {
                resolved = Path.GetFullPath(Path.Combine(dir, fileName));
            }
            else
            {
                resolved = Path.GetFullPath(Path.Combine(Paths.GameRootPath, fileName));
            }

            var folder = Path.GetDirectoryName(resolved);
            if (string.IsNullOrEmpty(folder))
            {
                reason = $"no directory in resolved path '{resolved}'";
                return false;
            }

            serverDir = folder;
            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }

    private static void ExtendWaitConstantsOnce()
    {
        if (_waitExtended) return;
        try
        {
            double oldWait = ClientBootstrapSystem.TIME_TO_WAIT_FOR_LOCAL_SERVER_STARTUP;
            int oldRetries = ClientBootstrapSystem.MAX_CONNECT_RETRIES;
            _originalWait = oldWait;
            _originalRetries = oldRetries;

            double newWait = oldWait * 4.0;
            int newRetries = oldRetries * 2;
            ClientBootstrapSystem.TIME_TO_WAIT_FOR_LOCAL_SERVER_STARTUP = newWait;
            ClientBootstrapSystem.MAX_CONNECT_RETRIES = newRetries;
            _waitExtended = true;

            Plugin.Logger?.LogInfo(
                $"Hosted: wait constants TIME_TO_WAIT_FOR_LOCAL_SERVER_STARTUP {oldWait} -> {newWait}, " +
                $"MAX_CONNECT_RETRIES {oldRetries} -> {newRetries} " +
                $"(RETRY_DELAY left at {ClientBootstrapSystem.RETRY_DELAY}).");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Hosted: failed to extend ClientBootstrapSystem wait constants: {e}");
        }
    }

    // ---- Doorstop env around the child spawn --------------------------------------------------

    /// <summary>
    /// Unity Doorstop (the <c>winhttp.dll</c> shim) exports its state as <c>DOORSTOP_*</c>
    /// process environment variables and skips injection when <c>DOORSTOP_INITIALIZED</c> is
    /// already set. The injected client carries those variables; a child started with
    /// <c>CreateProcessW(..., lpEnvironment=NULL, ...)</c> inherits them and Doorstop then
    /// treats the child as already initialized (evidence §28: staged shim + config, no
    /// <c>LogOutput.log</c>). Snapshot every <c>DOORSTOP_*</c> name, delete them so the child
    /// does not inherit, and restore in the finalizer. DryRun logs the names and leaves the
    /// environment alone. Nested Starts share the outermost snapshot.
    /// </summary>
    /// <returns>
    /// True after a successful <see cref="DoorstopStripNest.Enter"/> so the finalizer Leaves.
    /// False when snapshot/clear threw: Enter is rolled back and <c>DOORSTOP_*</c> is restored
    /// so <c>__state</c> stays false.
    /// </returns>
    private static bool BeginDoorstopStrip()
    {
        lock (Gate)
        {
            bool outermost = Strip.Enter();
            try
            {
                if (!outermost)
                {
                    Plugin.Logger?.LogInfo(
                        $"Hosted: DOORSTOP_* already stripped (nest={Strip.Depth}); keeping the outer snapshot.");
                    return true;
                }

                SnapshotAndClearDoorstopEnvUnlocked();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"Hosted: snapshot/clear of DOORSTOP_* threw: {e}");
                bool shouldRestore = Strip.Leave();
                if (shouldRestore)
                    RestoreDoorstopEnvUnlocked();
                return false;
            }
        }
    }

    private static void EndDoorstopStrip()
    {
        lock (Gate)
        {
            try
            {
                bool outermost = Strip.Leave();
                if (!outermost) return;
                RestoreDoorstopEnvUnlocked();
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"Hosted: restore of DOORSTOP_* threw: {e}");
            }
        }
    }

    private static void SnapshotAndClearDoorstopEnvUnlocked()
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var env = Environment.GetEnvironmentVariables();
        foreach (var key in env.Keys)
        {
            if (key is not string name) continue;
            if (!name.StartsWith(DoorstopEnvPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var value = env[key];
            snapshot[name] = value?.ToString();
        }

        _doorstopSnapshot = snapshot;
        var names = FormatDoorstopNames(snapshot);

        if (snapshot.Count == 0)
        {
            _doorstopDidClear = false;
            _lastDoorstopNote = "none present";
            Plugin.Logger?.LogInfo(
                "Hosted: no DOORSTOP_* environment variables in this process; child will not inherit a Doorstop-initialized state.");
            return;
        }

        if (DryRun)
        {
            _doorstopDidClear = false;
            _lastDoorstopNote = "DryRun, would clear " + names;
            Plugin.Logger?.LogInfo(
                $"Hosted: DryRun, would clear DOORSTOP_* so the child does not inherit them: {names}");
            return;
        }

        // Mark cleared before the loop so a throw mid-loop still lets the finalizer restore.
        _doorstopDidClear = true;
        foreach (var kv in snapshot)
            Environment.SetEnvironmentVariable(kv.Key, null);

        _lastDoorstopNote = "cleared " + names;
        Plugin.Logger?.LogInfo($"Hosted: cleared DOORSTOP_* for child spawn: {names}");
    }

    /// <summary>
    /// Put the client's Doorstop variables back. No-op when DryRun (nothing was deleted) or when
    /// the snapshot was already restored. Caller holds <see cref="Gate"/> and has either
    /// <see cref="DoorstopStripNest.Leave"/>d to 0 or <see cref="DoorstopStripNest.Reset"/> on Uninstall.
    /// </summary>
    private static void RestoreDoorstopEnvUnlocked()
    {
        var snapshot = _doorstopSnapshot;
        bool didClear = _doorstopDidClear;
        _doorstopSnapshot = null;
        _doorstopDidClear = false;
        if (!didClear || snapshot == null || snapshot.Count == 0) return;

        foreach (var kv in snapshot)
            Environment.SetEnvironmentVariable(kv.Key, kv.Value);

        var names = FormatDoorstopNames(snapshot);
        _lastDoorstopNote = "restored " + names;
        Plugin.Logger?.LogInfo($"Hosted: restored DOORSTOP_*: {names}");
    }

    /// <summary>
    /// Once per hosted spawn, look 30 s later for <c>{serverDir}\BepInEx\LogOutput.log</c>.
    /// Presence with a last-write at or after this spawn means Doorstop injected; a leftover
    /// file from a previous child is treated as absent. Observational: runs under DryRun too
    /// (the message then notes that the env was not cleared). Owned so Uninstall can drop it.
    /// A leftover child is matched by executable path under the game's <c>VRising_Server</c>
    /// regardless of argv. An open-file rename failure is treated as leftover-alive so the
    /// probe cannot report <c>present</c> off the leftover write. The probe re-scans at
    /// fire time, not only at spawn.
    /// </summary>
    private static void ScheduleChildLogProbe(string serverDir)
    {
        try
        {
            string logPath = Path.Combine(serverDir, "BepInEx", ChildLogProbe.FileName);
            bool dry = DryRun;
            bool leftoverChildAlive = TryFindAliveBundledChild(serverDir, out var leftoverPath);
            if (leftoverChildAlive)
            {
                Plugin.Logger?.LogWarning(
                    "Hosted: leftover bundled child still alive (" + leftoverPath +
                    "); not retiring LogOutput.log. 30 s probe will report '" +
                    ChildLogProbe.InconclusiveLeftoverChildAlive + "' rather than present.");
            }
            else if (!TryRetireLeftoverChildLog(logPath, serverDir, dry, out var retireWhy))
            {
                leftoverChildAlive = true;
                leftoverPath = logPath;
                Plugin.Logger?.LogWarning(
                    "Hosted: leftover LogOutput.log could not be retired (" + retireWhy +
                    "); treating as leftover-alive. 30 s probe will report '" +
                    ChildLogProbe.InconclusiveLeftoverChildAlive + "' rather than present.");
            }

            DateTime spawnUtc = DateTime.UtcNow;
            _lastChildLogProbe = leftoverChildAlive
                ? $"scheduled {ChildLogProbeSeconds:0}s -> {logPath} (spawn utc {spawnUtc:o}; leftover child {leftoverPath})"
                : $"scheduled {ChildLogProbeSeconds:0}s -> {logPath} (spawn utc {spawnUtc:o})";
            Plugin.Logger?.LogInfo(
                $"Hosted: will check for child BepInEx log in {ChildLogProbeSeconds:0}s: {logPath}");
            Dev.MainThread.PostDelayed(ChildLogProbeSeconds, () =>
            {
                try
                {
                    // Re-evaluate leftover at probe time: a child missed at spawn (MainModule
                    // access-denied) may still be writing, and a leftover that died at t=1s
                    // stays inconclusive if spawn already flagged it.
                    bool stillLeftover = leftoverChildAlive
                                         || TryFindAliveBundledChild(serverDir, spawnUtc, out _);

                    bool exists = File.Exists(logPath);
                    DateTime lastWrite = default;
                    long length = 0;
                    if (exists)
                    {
                        var info = new FileInfo(logPath);
                        lastWrite = info.LastWriteTimeUtc;
                        length = info.Length;
                    }

                    string line = ChildLogProbe.FormatProbeLine(
                        stillLeftover,
                        exists,
                        lastWrite,
                        spawnUtc,
                        ChildLogProbeSeconds,
                        length,
                        logPath,
                        dry);

                    _lastChildLogProbe = line;
                    Plugin.Logger?.LogInfo("Hosted: child BepInEx log " + line);
                }
                catch (Exception e)
                {
                    _lastChildLogProbe = "probe threw: " + e.Message;
                    Plugin.Logger?.LogError($"Hosted: child BepInEx log probe threw: {e}");
                }
            }, owner: ChildLogProbeOwner);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Hosted: could not schedule child BepInEx log probe: {e}");
        }
    }

    /// <returns>
    /// True when there is nothing to retire, DryRun would retire, the path was refused
    /// (not an open-file miss), or the rename succeeded. False when the file exists and
    /// <c>File.Move</c> failed — caller treats that as leftover-alive.
    /// </returns>
    private static bool TryRetireLeftoverChildLog(string logPath, string serverDir, bool dry, out string why)
    {
        why = "";
        try
        {
            if (!File.Exists(logPath)) return true;
            if (!ChildLogProbe.MayRetireLeftover(logPath, serverDir, out var retired, out var reason))
            {
                Plugin.Logger?.LogWarning($"Hosted: not retiring leftover child log: {reason}");
                why = reason;
                // Path refuse is not an open-file miss (live-server legs never reach here).
                return true;
            }

            if (dry)
            {
                Plugin.Logger?.LogInfo(
                    $"Hosted: DryRun, would retire leftover child BepInEx log {logPath} -> {retired}");
                return true;
            }

            if (File.Exists(retired))
                File.Delete(retired);
            File.Move(logPath, retired);
            Plugin.Logger?.LogInfo($"Hosted: retired leftover child BepInEx log to {retired}");
            return true;
        }
        catch (Exception e)
        {
            bool leftoverAlive = ChildLogProbe.TreatRenameFailureAsLeftoverAlive(e);
            why = ChildLogProbe.IsOpenFileRenameFailure(e)
                ? "open-file rename failure: " + e.Message
                : leftoverAlive
                    ? "rename failure (treating leftover as alive): " + e.Message
                    : e.Message;
            Plugin.Logger?.LogWarning($"Hosted: could not retire leftover child BepInEx log: {why}");
            return !leftoverAlive;
        }
    }

    /// <summary>
    /// True when a <c>VRisingServer.exe</c> whose path sits under <paramref name="serverDir"/>
    /// (the game's <c>VRising_Server</c>) is still running. Matched by executable path
    /// regardless of argv. Never matches <c>C:\VRisingServer</c>: process identity is the
    /// full path, not the exe name, and this helper never kills. Prefers
    /// <c>QueryFullProcessImageName</c> (limited information) so an access-denied
    /// <c>MainModule</c> does not miss a leftover.
    /// </summary>
    private static bool TryFindAliveBundledChild(string serverDir, out string foundPath)
        => TryFindAliveBundledChild(serverDir, null, out foundPath);

    /// <summary>
    /// Same scan, but a process that started at or after <paramref name="startedBeforeUtc"/>
    /// (minus a 2 s clock slack) is not a leftover: it is the child this Start spawned.
    /// Evidence 38: the probe-time rescan without this exclusion reported the fresh child as
    /// "leftover alive" and made every healthy hosted run inconclusive. A process whose start
    /// time cannot be read is kept (conservative: inconclusive beats a false present).
    /// </summary>
    private static bool TryFindAliveBundledChild(string serverDir, DateTime? startedBeforeUtc, out string foundPath)
    {
        foundPath = "";
        try
        {
            foreach (var proc in Process.GetProcessesByName("VRisingServer"))
            {
                try
                {
                    if (foundPath.Length > 0)
                        continue;

                    if (!TryGetExecutablePath(proc, out var path) || path.Length == 0)
                        continue;

                    if (startedBeforeUtc.HasValue && !ChildLogProbe.StartedBefore(TryGetStartTimeUtc(proc), startedBeforeUtc.Value))
                        continue;

                    if (HostedPathRules.IsForbiddenLiveServerPath(path))
                        continue;

                    if (!ChildLogProbe.IsBundledChildProcess(path, serverDir))
                        continue;

                    foundPath = path;
                }
                finally
                {
                    try { proc.Dispose(); } catch { /* best-effort */ }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Hosted: bundled-child process scan threw: {e.Message}");
            foundPath = "";
            return false;
        }

        return foundPath.Length > 0;
    }

    /// <summary>UTC start time of <paramref name="proc"/>, or null when it cannot be read.</summary>
    private static DateTime? TryGetStartTimeUtc(Process proc)
    {
        try { return proc.StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    /// <summary>
    /// Full image path of <paramref name="proc"/>. Limited-info query first so Wow64 /
    /// access-denied <c>MainModule</c> does not skip a leftover bundled child.
    /// </summary>
    private static bool TryGetExecutablePath(Process proc, out string path)
    {
        path = "";
        try
        {
            if (TryQueryFullProcessImageName(proc.Id, out var queried) && queried.Length > 0)
            {
                path = queried;
                return true;
            }
        }
        catch { /* fall through to MainModule */ }

        try
        {
            path = proc.MainModule?.FileName ?? "";
            return path.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryQueryFullProcessImageName(int pid, out string path)
    {
        path = "";
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero) return false;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size) || size <= 0)
                return false;
            path = sb.ToString();
            return path.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try { CloseHandle(handle); } catch { /* best-effort */ }
            }
        }
    }

    private static string FormatDoorstopNames(Dictionary<string, string?> snapshot)
    {
        if (snapshot.Count == 0) return "(none)";
        var names = new List<string>(snapshot.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", names);
    }

    // ---- config / diagnostics -----------------------------------------------------------------

    /// <summary>
    /// Bind <c>[Hosted]</c> on the shared file now, or once it resolves; the property defaults
    /// hold until then. At process start this used to fall back to a separate
    /// <c>progenitor.hosted.cfg</c> (the chainloader lookup always missed there), so
    /// the <c>[Hosted]</c> keys in progenitor.cfg only applied after a Core hot reload.
    /// </summary>
    private static void BindConfig()
    {
        if (_enabled != null) return;
        SharedConfig.BindWhenAvailable("[Hosted]", BindConfigFrom);
        try
        {
            var legacy = Path.Combine(Paths.ConfigPath, LegacyHostedConfigName);
            if (File.Exists(legacy))
                Plugin.Logger?.LogWarning("Hosted: " + legacy + " is no longer read. Move any [Hosted] "
                                          + "settings in it to [Hosted] in " + ConfigHelper.SharedFileName + ".");
        }
        catch { /* diagnostics only */ }
    }

    /// <summary>The standalone file [Hosted] used at process start before the shared-file handover.</summary>
    private const string LegacyHostedConfigName = "progenitor.hosted.cfg";

    private static void BindConfigFrom(ConfigFile config)
    {
        if (_enabled != null) return;
        try
        {
            _enabled = ConfigHelper.Bind(
                config,
                "Hosted",
                "Enabled",
                true,
                "Stage BepInEx into the bundled VRisingServer.exe folder when the client starts a private or single-player game.");
            _dryRun = ConfigHelper.Bind(
                config,
                "Hosted",
                "DryRun",
                false,
                "Log what would be copied or linked into the bundled server folder, without writing anything.");
            _serverPluginsMode = ConfigHelper.BindEnum(
                config,
                "Hosted",
                "ServerPluginsMode",
                HostedServerPluginsMode.Shared,
                "How the bundled child's BepInEx/plugins is filled. Shared (default): junction the client's plugins folder; the child then loads every client plugin, including client-only mods. progenitor-core is always a real copy (never a junction) so the child's .next promote cannot File.Move the running client's Core. ProgenitorOnly: a real plugins folder containing only Progenitor.Bootstrap.dll and a real progenitor-core folder containing Progenitor.dll (progenitor-reload stays junctioned). Use ProgenitorOnly when a client-only plugin would crash the child.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Hosted: config bind threw; using defaults Enabled=true DryRun=false ServerPluginsMode=Shared. {e}");
        }
    }

    private static void AppendWaitConstants(StringBuilder sb)
    {
        try
        {
            sb.AppendLine(
                $"  TIME_TO_WAIT_FOR_LOCAL_SERVER_STARTUP: {ClientBootstrapSystem.TIME_TO_WAIT_FOR_LOCAL_SERVER_STARTUP}" +
                (_waitExtended && _originalWait is double w ? $" (was {w}, x4)" : " (not yet extended)"));
            sb.AppendLine(
                $"  MAX_CONNECT_RETRIES: {ClientBootstrapSystem.MAX_CONNECT_RETRIES}" +
                (_waitExtended && _originalRetries is int r ? $" (was {r}, x2)" : " (not yet extended)"));
            sb.AppendLine($"  RETRY_DELAY: {ClientBootstrapSystem.RETRY_DELAY} (unchanged)");
        }
        catch (Exception e)
        {
            sb.AppendLine($"  wait constants: unreadable ({e.Message})");
        }
    }

    private static string ReadServerPath()
    {
        try
        {
            var path = SettingsManager.ClientSettings?.General?.ServerPath;
            return string.IsNullOrEmpty(path) ? "(unset)" : path;
        }
        catch (Exception e)
        {
            return $"(unreadable: {e.Message})";
        }
    }

    private static string SafePath(Func<string> read)
    {
        try { return read() ?? "(null)"; }
        catch (Exception e) { return $"(unreadable: {e.Message})"; }
    }
#endif
}
